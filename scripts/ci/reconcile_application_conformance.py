"""Reconcile application-conformance mappings with executed TRX results (stdlib only)."""

import argparse
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
SUITES = ("auth-infra", "adminarea-unit", "adminarea-infra", "client-unit", "client-infra")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def required_attribute(element, name):
    value = element.get(name) if element is not None else None
    if not value or not value.strip():
        raise ValueError("Missing TRX identity or outcome")
    return value


def exactly_one(parent, path):
    elements = parent.findall(path, NS)
    if len(elements) != 1:
        raise ValueError("Missing or duplicate TRX section")
    return elements[0]


def read_suite(path):
    root = ET.parse(path).getroot()
    if root.tag != f"{{{NS['t']}}}TestRun":
        raise ValueError("Unexpected TRX root or namespace")
    definition_section = exactly_one(root, "t:TestDefinitions")
    result_section = exactly_one(root, "t:Results")
    summary = exactly_one(root, "t:ResultSummary")
    counters = exactly_one(summary, "t:Counters")
    definitions = {}
    identities = set()
    for test in definition_section:
        if test.tag != f"{{{NS['t']}}}UnitTest":
            raise ValueError("Unsupported TRX definition")
        test_id = required_attribute(test, "id")
        name = required_attribute(test, "name")
        method = exactly_one(test, "t:TestMethod")
        class_name = required_attribute(method, "className")
        method_name = required_attribute(method, "name")
        execution = required_attribute(exactly_one(test, "t:Execution"), "id")
        full_name = f"{class_name}.{method_name}"
        if name != full_name and not (name.startswith(full_name + "(") and name.endswith(")")):
            raise ValueError("TRX case name does not match its method")
        identity = (class_name, method_name, name)
        if test_id in definitions or identity in identities:
            raise ValueError("Duplicate TRX test identity")
        identities.add(identity)
        definitions[test_id] = (class_name, method_name, name, execution)

    results = {}
    executions = set()
    for result in result_section:
        if result.tag != f"{{{NS['t']}}}UnitTestResult":
            raise ValueError("Unsupported TRX result")
        test_id = required_attribute(result, "testId")
        execution = required_attribute(result, "executionId")
        name = required_attribute(result, "testName")
        outcome = required_attribute(result, "outcome")
        if test_id not in definitions or test_id in results or execution in executions:
            raise ValueError("Missing or duplicate TRX execution identity")
        if (name, execution) != definitions[test_id][2:]:
            raise ValueError("TRX execution does not match its definition")
        executions.add(execution)
        results[test_id] = outcome

    if not definitions or not results:
        raise ValueError("No TRX execution")
    observed = {
        "total": len(definitions),
        "executed": sum(value != "NotExecuted" for value in results.values()),
        "passed": sum(value == "Passed" for value in results.values()),
        "failed": sum(value == "Failed" for value in results.values()),
        "notExecuted": sum(value == "NotExecuted" for value in results.values()),
    }
    for key, count in observed.items():
        if int(required_attribute(counters, key)) != count:
            raise ValueError("TRX counters do not reconcile with definitions and results")
    return definitions, results


def mapping_failure(mapping, suite):
    definitions, results = suite
    # A method mapping includes all distinct parameterized cases of that one method.
    # Same-named methods in different classes are ambiguous, never a substring match.
    matches = [(test_id, definition) for test_id, definition in definitions.items()
               if definition[1] == mapping["test"]]
    if not matches:
        return "Mapped test is absent from TRX definitions."
    if len({definition[0] for _, definition in matches}) != 1:
        return "Mapped test identity is ambiguous across classes."
    for test_id, _ in matches:
        if test_id not in results:
            return "Mapped test has no TRX execution."
        if results[test_id] != "Passed":
            return "Mapped test has a non-passing TRX outcome."
    return None


def reconcile(inventory, results_dir):
    suites = {}
    errors = {}
    for name in SUITES:
        try:
            suites[name] = read_suite(results_dir / f"{name}.trx")
        except (OSError, ET.ParseError, ValueError, LookupError):
            errors[name] = "Missing, unreadable, malformed or ambiguous TRX data."
    targets = []
    for target in inventory["targets"]:
        reason = None
        mappings = target.get("evidence")
        seen = set()
        if not isinstance(mappings, list) or not mappings:
            reason = "Target has no evidence mappings."
        else:
            for mapping in mappings:
                if (not isinstance(mapping, dict) or mapping.get("suite") not in SUITES
                        or not isinstance(mapping.get("test"), str) or not mapping["test"].strip()):
                    reason = "Invalid evidence mapping."
                    break
                key = (mapping["suite"], mapping["test"])
                if key in seen:
                    reason = "Duplicate evidence mapping."
                    break
                seen.add(key)
                failure = errors.get(mapping["suite"])
                if failure is None:
                    failure = mapping_failure(mapping, suites[mapping["suite"]])
                if failure is not None:
                    reason = f"{mapping['suite']}::{mapping['test']}: {failure}"
                    break
        targets.append({"id": target["id"], "status": "failed" if reason else "passed",
                        "reason": reason, "evidence": mappings if not reason else []})
    # Suite failures still fail the runner, without falsely failing unrelated targets.
    for name, (definitions, results) in suites.items():
        if len(definitions) != len(results) or any(value != "Passed" for value in results.values()):
            errors[name] = "Suite contains failed, skipped or unexecuted tests."
    report = {"schemaVersion": 1, "targets": targets,
              "blockedBoundaries": inventory["blockedBoundaries"]}
    passed = not errors and all(target["status"] == "passed" for target in targets)
    return report, passed, errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory", type=Path)
    parser.add_argument("results_dir", type=Path)
    args = parser.parse_args()
    try:
        inventory = json.loads(args.inventory.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        if (not isinstance(inventory["targets"], list) or not inventory["targets"]
                or len({target["id"] for target in inventory["targets"]}) != len(inventory["targets"])):
            raise ValueError("Invalid target inventory")
        report, passed, errors = reconcile(inventory, args.results_dir)
        (args.results_dir / "target-results.json").write_text(
            json.dumps(report, indent=2) + "\n", encoding="utf-8")
    except (OSError, ValueError, KeyError, TypeError):
        print("Application-conformance result reconciliation failed closed.", file=sys.stderr)
        return 1
    for target in report["targets"]:
        print(f"{target['id']}: {target['status']}" + (f" - {target['reason']}" if target["reason"] else ""))
    for name, reason in errors.items():
        print(f"{name}: {reason}", file=sys.stderr)
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
