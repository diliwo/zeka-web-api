"""Synthetic TRX regression tests; these do not claim application execution."""

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parents[1] / "reconcile_application_conformance.py"
SPEC = importlib.util.spec_from_file_location("reconcile", SCRIPT)
reconcile = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(reconcile)
NS = reconcile.NS["t"]


def node(parent, tag, **attributes):
    return ET.SubElement(parent, f"{{{NS}}}{tag}", attributes)


def trx(cases):
    root = ET.Element(f"{{{NS}}}TestRun")
    definitions = node(root, "TestDefinitions")
    results = node(root, "Results")
    for index, (method, outcome, class_name, arguments) in enumerate(cases):
        name = f"{class_name}.{method}{arguments}"
        test = node(definitions, "UnitTest", id=str(index), name=name)
        node(test, "Execution", id=f"execution-{index}")
        node(test, "TestMethod", className=class_name, name=method)
        if outcome is not None:
            node(results, "UnitTestResult", testId=str(index), executionId=f"execution-{index}",
                 testName=name, outcome=outcome)
    summary = node(root, "ResultSummary")
    outcomes = [case[1] for case in cases if case[1] is not None]
    node(summary, "Counters", total=str(len(cases)),
         executed=str(sum(value != "NotExecuted" for value in outcomes)),
         passed=str(outcomes.count("Passed")), failed=str(outcomes.count("Failed")),
         notExecuted=str(outcomes.count("NotExecuted")))
    return root


class ReconciliationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name)
        self.inventory = {"targets": [
            {"id": "CTX-04", "evidence": [
                {"suite": "adminarea-infra", "test": "Route"},
                {"suite": "adminarea-infra", "test": "Body"}]},
            {"id": "Independent", "evidence": [{"suite": "adminarea-infra", "test": "Route"}]}],
            "blockedBoundaries": [{"id": "LIFE", "status": "blocked", "reason": "Not authorized"}]}
        for suite in reconcile.SUITES:
            self.write(suite, trx([("Route", "Passed", "Tests.Tenant", ""),
                                   ("Body", "Passed", "Tests.Tenant", "")]))

    def write(self, suite, root):
        ET.ElementTree(root).write(self.path / f"{suite}.trx", encoding="utf-8", xml_declaration=True)

    def body(self, outcome):
        self.write("adminarea-infra", trx([("Route", "Passed", "Tests.Tenant", ""),
                                           ("Body", outcome, "Tests.Tenant", "")]))

    def assert_target(self, expected):
        report, passed, _ = reconcile.reconcile(self.inventory, self.path)
        self.assertEqual(expected, report["targets"][0]["status"])
        if expected != "passed":
            self.assertFalse(passed)
        self.assertEqual(self.inventory["blockedBoundaries"], report["blockedBoundaries"])
        return report, passed

    def test_all_mapped_tests_passing(self):
        _, passed = self.assert_target("passed")
        self.assertTrue(passed)

    def test_failed_mapping_does_not_fail_independent_target(self):
        self.body("Failed")
        report, _ = self.assert_target("failed")
        self.assertEqual("passed", report["targets"][1]["status"])

    def test_skipped_or_unknown_outcome_never_passes(self):
        for outcome in ("NotExecuted", "Skipped", "Inconclusive", "Error", "passed", ""):
            with self.subTest(outcome=outcome):
                self.body(outcome)
                self.assert_target("failed")

    def test_absent_mapping_is_not_satisfied_by_text_elsewhere(self):
        root = trx([("Route", "Passed", "Tests.Tenant", "")])
        node(root, "Output").text = "Body"
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_definition_without_execution_fails(self):
        self.body(None)
        self.assert_target("failed")

    def test_missing_and_unreadable_trx_fail(self):
        path = self.path / "adminarea-infra.trx"
        path.unlink()
        self.assert_target("failed")
        path.mkdir()
        self.assert_target("failed")

    def test_malformed_xml_fails(self):
        (self.path / "adminarea-infra.trx").write_text("<TestRun>", encoding="utf-8")
        self.assert_target("failed")

    def test_duplicate_result_sections_fail(self):
        root = trx([("Route", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Tenant", "")])
        root.append(copy.deepcopy(root.find("t:Results", reconcile.NS)))
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_unsupported_result_cannot_be_ignored(self):
        root = trx([("Route", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Tenant", "")])
        node(root.find("t:Results", reconcile.NS), "OtherResult", outcome="Failed")
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_unsupported_definition_cannot_be_ignored(self):
        root = trx([("Route", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Tenant", "")])
        node(root.find("t:TestDefinitions", reconcile.NS), "OtherTest", id="unsupported")
        self.write("adminarea-infra", root)
        with self.assertRaisesRegex(ValueError, "Unsupported TRX definition"):
            reconcile.read_suite(self.path / "adminarea-infra.trx")
        report, passed, errors = reconcile.reconcile(self.inventory, self.path)
        self.assertFalse(passed)
        self.assertEqual({"adminarea-infra"}, set(errors))
        for target in report["targets"]:
            self.assertEqual("failed", target["status"])
            self.assertEqual([], target["evidence"])

    def test_empty_or_entirely_unexecuted_suite_cannot_credit_targets(self):
        for cases in ([], [("Route", None, "Tests.Tenant", ""), ("Body", None, "Tests.Tenant", "")]):
            with self.subTest(cases=cases):
                self.write("adminarea-infra", trx(cases))
                with self.assertRaisesRegex(ValueError, "No TRX execution"):
                    reconcile.read_suite(self.path / "adminarea-infra.trx")
                report, passed, errors = reconcile.reconcile(self.inventory, self.path)
                self.assertFalse(passed)
                self.assertEqual({"adminarea-infra"}, set(errors))
                for target in report["targets"]:
                    self.assertEqual("failed", target["status"])
                    self.assertEqual([], target["evidence"])

    def test_inconsistent_case_identity_fails(self):
        root = trx([("Route", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Tenant", "")])
        root.find("t:TestDefinitions/t:UnitTest/t:TestMethod", reconcile.NS).set("name", "Other")
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_wrong_namespace_fails(self):
        self.write("adminarea-infra", ET.Element("TestRun"))
        self.assert_target("failed")

    def test_duplicate_definition_identity_fails(self):
        self.write("adminarea-infra", trx([("Route", "Passed", "Tests.Tenant", ""),
            ("Body", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Tenant", "")]))
        self.assert_target("failed")

    def test_ambiguous_method_in_different_classes_fails(self):
        self.write("adminarea-infra", trx([("Route", "Passed", "Tests.Tenant", ""),
            ("Body", "Passed", "Tests.Tenant", ""), ("Body", "Passed", "Tests.Other", "")]))
        report, _ = self.assert_target("failed")
        self.assertEqual("passed", report["targets"][1]["status"])

    def test_duplicate_execution_fails(self):
        root = trx([("Body", "Passed", "Tests.Tenant", "")])
        results = root.find("t:Results", reconcile.NS)
        results.append(copy.deepcopy(results[0]))
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_mismatched_execution_identity_fails(self):
        for attribute in ("testId", "executionId", "testName"):
            with self.subTest(attribute=attribute):
                root = trx([("Body", "Passed", "Tests.Tenant", "")])
                root.find("t:Results/t:UnitTestResult", reconcile.NS).set(attribute, "unmatched")
                self.write("adminarea-infra", root)
                self.assert_target("failed")

    def test_missing_and_duplicate_mapping_fail(self):
        for mappings in ([], [self.inventory["targets"][0]["evidence"][0]] * 2):
            with self.subTest(mappings=mappings):
                self.inventory["targets"][0]["evidence"] = mappings
                self.assert_target("failed")

    def test_invalid_mapping_cannot_credit_passing_trx(self):
        for mapping in (None, "Body", {}, {"suite": "unknown", "test": "Body"},
                        {"suite": "adminarea-infra"}, {"suite": "adminarea-infra", "test": 1},
                        {"suite": "adminarea-infra", "test": ""},
                        {"suite": "adminarea-infra", "test": " \t"}):
            with self.subTest(mapping=mapping):
                self.inventory["targets"][0]["evidence"] = [
                    {"suite": "adminarea-infra", "test": "Route"}, mapping]
                report, passed, errors = reconcile.reconcile(self.inventory, self.path)
                self.assertFalse(passed)
                self.assertEqual({}, errors)
                self.assertEqual("failed", report["targets"][0]["status"])
                self.assertEqual("Invalid evidence mapping.", report["targets"][0]["reason"])
                self.assertEqual([], report["targets"][0]["evidence"])
                self.assertEqual("passed", report["targets"][1]["status"])
                self.assertEqual(self.inventory["targets"][1]["evidence"], report["targets"][1]["evidence"])
                self.assertEqual(self.inventory["blockedBoundaries"], report["blockedBoundaries"])

    def test_all_parameterized_cases_must_execute_and_pass(self):
        for outcome in ("Passed", "Failed", "NotExecuted", None):
            with self.subTest(outcome=outcome):
                self.write("adminarea-infra", trx([("Route", "Passed", "Tests.Tenant", ""),
                    ("Body", "Passed", "Tests.Tenant", "(mode: 0)"),
                    ("Body", outcome, "Tests.Tenant", "(mode: 1)")]))
                self.assert_target("passed" if outcome == "Passed" else "failed")

    def test_substring_method_name_does_not_match(self):
        self.write("adminarea-infra", trx([("Route", "Passed", "Tests.Tenant", ""),
                                           ("Body_extra", "Passed", "Tests.Tenant", "")]))
        self.assert_target("failed")

    def test_unmapped_failure_fails_suite_without_overriding_targets(self):
        self.write("auth-infra", trx([("Unmapped", "Failed", "Tests.Other", "")]))
        _, passed = self.assert_target("passed")
        self.assertFalse(passed)

    def test_forged_passing_counters_do_not_override_failed_result(self):
        root = trx([("Route", "Passed", "Tests.Tenant", ""), ("Body", "Failed", "Tests.Tenant", "")])
        counters = root.find("t:ResultSummary/t:Counters", reconcile.NS)
        counters.set("passed", "2")
        counters.set("failed", "0")
        self.write("adminarea-infra", root)
        self.assert_target("failed")

    def test_cli_writes_truthful_target_results_and_exit_code(self):
        inventory_path = self.path / "inventory.json"
        inventory_path.write_text(json.dumps(self.inventory), encoding="utf-8")
        for outcome, expected_code in (("Passed", 0), ("Failed", 1), ("NotExecuted", 1)):
            with self.subTest(outcome=outcome):
                self.body(outcome)
                process = subprocess.run([sys.executable, str(SCRIPT), str(inventory_path), str(self.path)],
                                         capture_output=True, text=True)
                self.assertEqual(expected_code, process.returncode, process.stderr)
                report = json.loads((self.path / "target-results.json").read_text(encoding="utf-8"))
                self.assertEqual("passed" if expected_code == 0 else "failed", report["targets"][0]["status"])
                self.assertEqual("passed", report["targets"][1]["status"])


if __name__ == "__main__":
    unittest.main()
