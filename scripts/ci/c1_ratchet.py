#!/usr/bin/env python3
"""Bind VSTest observations to a C1 run and compare them with the admitted ratchet."""

import argparse
import difflib
import hashlib
import json
import re
import sys
from pathlib import Path, PurePosixPath

from c1_ratchet_trx import TrxError, identify_project, load_trx, validate_identity

ANCHOR = "23ad61e84ae976b6a483d2a859871c3b40d98249"
AUTHORITY = "https://linear.app/zeka-saas/issue/ZEK-17#comment-0281b80b-a1a6-42d9-b8f8-02bd8d117a05"
INITIAL_BASELINE_SHA256 = "81e9f33157c0933d892e22042d63939d35cef8e6ab9aff8ed968d1230e48f85b"
ROLES = ("base", "candidate")
OUTCOMES = ("Passed", "Failed", "NotExecuted")
MAX_JSON = 12 * 1024 * 1024


class RatchetError(ValueError):
    pass


def require(condition, code):
    if not condition:
        raise RatchetError(code)


def object_pairs(pairs):
    result = dict(pairs)
    require(len(result) == len(pairs), "DUPLICATE_JSON_KEY")
    return result


def read_json(path):
    require(path.is_file() and not path.is_symlink(), "MISSING_OR_UNSAFE_JSON")
    require(path.stat().st_size <= MAX_JSON, "JSON_TOO_LARGE")
    try:
        return json.loads(path.read_bytes(), object_pairs_hook=object_pairs)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise RatchetError("MALFORMED_JSON") from error


def save_json(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(value, output, indent=2, sort_keys=True, ensure_ascii=True)
        output.write("\n")


def shape(obj, keys, code):
    require(isinstance(obj, dict) and set(obj) == set(keys.split()), code)


def hex_sha(value, length=40):
    return isinstance(value, str) and re.fullmatch(rf"[0-9a-f]{{{length}}}", value) and int(value, 16)


def relative(value, pattern):
    return isinstance(value, str) and re.fullmatch(pattern, value) and all(
        part not in ("", ".", "..") for part in value.split("/")
    )


def project_path(value):
    return relative(value, r"[A-Za-z0-9._/-]+\.csproj")


def validate_context(value):
    shape(value, "schema event candidate base testTree baseTree runId runAttempt repository", "INVALID_CONTEXT")
    require(type(value["schema"]) is int and value["schema"] == 1
            and value["event"] in ("pull_request", "push")
            and value["repository"] == "diliwo/zeka-web-api"
            and all(hex_sha(value[field]) for field in ("candidate", "base", "testTree", "baseTree"))
            and all(isinstance(value[field], (str, int)) and re.fullmatch(r"[1-9][0-9]*", str(value[field]))
                    for field in ("runId", "runAttempt")), "INVALID_CONTEXT")
    return value


def identity(value):
    try:
        return validate_identity(value)
    except ValueError as error:
        raise RatchetError("INVALID_TEST_IDENTITY") from error


def signature(value):
    shape(value, "exception assertion frame", "INVALID_SIGNATURE")
    require(isinstance(value["exception"], str) and re.fullmatch(r"[A-Za-z0-9_.-]{1,160}", value["exception"])
            and isinstance(value["assertion"], str) and re.fullmatch(r"[A-Za-z0-9_.:-]{1,240}", value["assertion"])
            and isinstance(value["frame"], str) and re.fullmatch(r"[A-Za-z0-9_.+\x60<>$-]{1,420}", value["frame"]),
            "INVALID_SIGNATURE")
    return tuple(value[k] for k in ("exception", "assertion", "frame"))


def validate_baseline(value):
    shape(value, "schema id revision anchor authority jobs failures obligations", "INVALID_BASELINE")
    require(type(value["schema"]) is int and value["schema"] == 1 and isinstance(value["id"], str)
            and re.fullmatch(r"C1-RATCHET-[a-z0-9-]+", value["id"])
            and hex_sha(value["revision"]) and value["anchor"] == ANCHOR and value["authority"] == AUTHORITY
            and isinstance(value["jobs"], dict) and value["jobs"], "INVALID_BASELINE")
    projects = set()
    for lane, scopes in value["jobs"].items():
        require(relative(lane, r"[a-z][a-z0-9-]{0,39}") and isinstance(scopes, dict) and scopes,
                "INVALID_BASELINE_JOB")
        for key, policy in scopes.items():
            require(relative(key, r"[A-Za-z0-9._/-]{1,280}"), "INVALID_BASELINE_JOB")
            require(isinstance(policy, dict), "INVALID_BASELINE_JOB")
            shape(policy, "project count allowed_skips" if "project" in policy else "count allowed_skips",
                  "INVALID_BASELINE_JOB")
            project = policy.get("project", key)
            require(project_path(project) and type(policy["count"]) is int and policy["count"] > 0
                    and isinstance(policy["allowed_skips"], list), "INVALID_BASELINE_JOB")
            projects.add(project)
            skips = set()
            for entry in policy["allowed_skips"]:
                shape(entry, "identity platform reason", "INVALID_SKIP_RULE")
                case = identity(entry["identity"])
                require(entry["platform"] == "linux" and isinstance(entry["reason"], str)
                        and re.fullmatch(r"[A-Za-z0-9 ._,()/+-]{1,180}", entry["reason"])
                        and case not in skips, "INVALID_SKIP_RULE")
                skips.add(case)
    require(isinstance(value["failures"], list) and isinstance(value["obligations"], list), "INVALID_BASELINE")
    failures, labels = {}, set()
    for entry in value["failures"]:
        shape(entry, "id project identity signatures issues", "INVALID_FAILURE_BASELINE")
        require(isinstance(entry["id"], str) and re.fullmatch(r"[AC][1-9][0-9]?", entry["id"])
                and entry["id"] not in labels and entry["project"] in projects
                and isinstance(entry["signatures"], list) and entry["signatures"]
                and isinstance(entry["issues"], list) and entry["issues"]
                and all(isinstance(issue, str) and re.fullmatch(r"(?:ZEK-|GitHub #)[0-9]+", issue)
                        for issue in entry["issues"]), "INVALID_FAILURE_BASELINE")
        case = (entry["project"], identity(entry["identity"]))
        signatures = {signature(item) for item in entry["signatures"]}
        require(case not in failures and len(signatures) == len(entry["signatures"])
                and all(item[1] != "unrecognized-failure" for item in signatures), "DUPLICATE_FAILURE_BASELINE")
        failures[case] = signatures
        labels.add(entry["id"])
    require(len(value["obligations"]) == 1, "HISTORICAL_OBLIGATION_MISSING")
    obligation = value["obligations"][0]
    shape(obligation, "id project identity status description issues evidence", "INVALID_HISTORICAL_OBLIGATION")
    require(obligation["id"] == "H1" and obligation["project"] in projects
            and obligation["status"] == "NOT_REPRODUCED_RETAINED"
            and isinstance(obligation["description"], str)
            and re.fullmatch(r"[A-Za-z0-9 ._,()/+-]{1,240}", obligation["description"])
            and obligation["evidence"] == "https://github.com/diliwo/zeka-web-api/actions/runs/43314934322/job/131520423950"
            and isinstance(obligation["issues"], list) and obligation["issues"]
            and all(isinstance(issue, str) and re.fullmatch(r"(?:ZEK-|GitHub #)[0-9]+", issue)
                    for issue in obligation["issues"]), "INVALID_HISTORICAL_OBLIGATION")
    historical = (obligation["project"], identity(obligation["identity"]))
    require(historical not in failures, "HISTORICAL_OBLIGATION_ADMITTED_AS_FAILURE")
    return failures


def validate_result(item):
    shape(item, "identity outcome signature", "INVALID_RESULT")
    case = identity(item["identity"])
    require(item["outcome"] in OUTCOMES, "INVALID_RESULT_OUTCOME")
    if item["outcome"] == "Failed":
        sig = signature(item["signature"])
    else:
        require(item["signature"] is None, "UNEXPECTED_RESULT_SIGNATURE")
        sig = None
    return case, item["outcome"], sig


def expected_source(context, role):
    return (context["base"], context["baseTree"]) if role == "base" else (
        context["candidate"], context["testTree"])


def raw_directory(root, value):
    require(relative(value, r"[A-Za-z0-9._/-]{1,180}"), "UNSAFE_RAW_RESULTS_PATH")
    path = root.joinpath(*PurePosixPath(value).parts)
    require(path.is_dir() and not path.is_symlink() and path.resolve().is_relative_to(root.resolve()),
            "MISSING_OR_UNSAFE_RESULTS_DIRECTORY")
    return path


def capture(context, baseline, lane, role, inputs):
    scopes = baseline["jobs"][lane]
    raw = read_json(inputs / "invocations.json")
    shape(raw, "schema sourceSha tree started completed invocations", "INVALID_INVOCATIONS")
    require(type(raw["schema"]) is int and raw["schema"] == 1 and raw["started"] is True
            and raw["completed"] is True and isinstance(raw["invocations"], list)
            and (raw["sourceSha"], raw["tree"]) == expected_source(context, role),
            "MISSING_OR_WRONG_SOURCE_EXECUTION")
    populated, result_dirs = {}, set()
    for invocation in raw["invocations"]:
        shape(invocation, "id project exitCode resultsDir", "INVALID_INVOCATION")
        key, project, status = invocation["id"], invocation["project"], invocation["exitCode"]
        require(isinstance(key, str) and isinstance(project, str)
                and type(status) is int and status in (0, 1)
                and isinstance(invocation["resultsDir"], str) and invocation["resultsDir"] not in result_dirs, "INVALID_INVOCATION")
        result_dirs.add(invocation["resultsDir"])
        directory = raw_directory(inputs, invocation["resultsDir"])
        files = sorted(directory.rglob("*.trx"))
        require(files and all(not x.is_symlink() and x.resolve().is_relative_to(directory.resolve())
                              for x in files), "MISSING_OR_UNSAFE_TRX")
        if project == "zeka-web-api.sln":
            require(lane == "sonar-suite" and len(raw["invocations"]) == 1
                    and all(k == p.get("project", k) for k, p in scopes.items()), "UNEXPECTED_SOLUTION_INVOCATION")
            selected = [(identify_project(p, scopes), p) for p in files]
        else:
            require(key in scopes and scopes[key].get("project", key) == project and len(files) == 1,
                    "INVOCATION_SELECTION_MISMATCH")
            selected = [(key, files[0])]
        failed = False
        for scope, path in selected:
            require(scope in scopes and scope not in populated, "DUPLICATE_OR_UNEXPECTED_PROJECT")
            results = load_trx(path, scopes[scope].get("project", scope))
            unique = {validate_result(item)[0] for item in results}
            require(results and len(unique) == len(results), "ZERO_OR_DUPLICATE_TESTS")
            failed = failed or any(x["outcome"] == "Failed" for x in results)
            populated[scope] = {"results": sorted(results, key=lambda item: item["identity"]),
                                "trxSha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        require(status == int(failed), "NATIVE_EXIT_AND_TRX_DISAGREE")
    require(set(populated) == set(scopes), "INCOMPLETE_PROJECT_COVERAGE")
    return {"schema": 1, "context": context, "lane": lane, "role": role, "projects": populated}


def safe_diff(before, after):
    def lines(value):
        return (json.dumps(value, sort_keys=True, indent=2, ensure_ascii=True) + "\n").splitlines(keepends=True)
    return "".join(difflib.unified_diff(lines(before), lines(after), "accepted-baseline", "candidate-baseline"))


def collect(context, baseline, evidence, errors):
    groups, observed = {}, set()
    for role in ROLES:
        for lane, scopes in baseline["jobs"].items():
            file = evidence / f"{role}-{lane}.json"
            if not file.is_file():
                errors.append({"reason": "MISSING_REQUIRED_JOB_EVIDENCE", "role": role, "lane": lane})
                continue
            try:
                artifact = read_json(file)
                shape(artifact, "schema context lane role projects", "INVALID_EVIDENCE")
                require(type(artifact["schema"]) is int and artifact["schema"] == 1
                        and artifact["context"] == context and artifact["lane"] == lane
                        and artifact["role"] == role and isinstance(artifact["projects"], dict)
                        and set(artifact["projects"]) == set(scopes), "EVIDENCE_BINDING_MISMATCH")
                for key, policy in scopes.items():
                    group = artifact["projects"][key]
                    shape(group, "results trxSha256", "INVALID_EVIDENCE_PROJECT")
                    require(hex_sha(group["trxSha256"], 64) and isinstance(group["results"], list)
                            and group["results"], "INVALID_EVIDENCE_PROJECT")
                    cases = {}
                    for item in group["results"]:
                        case, outcome, sig = validate_result(item)
                        require(case not in cases, "DUPLICATE_TEST_IDENTITY")
                        cases[case] = (outcome, sig)
                    groups[(role, lane, key)] = cases
                    # A matching invocation inventory and at least one executed
                    # case are both required, even when exact skips are admitted.
                    require(any(value[0] != "NotExecuted" for value in cases.values()),
                            "ZERO_TEST_EXECUTION")
                    observed.add((role, lane))
                    allowed_skips = {identity(x["identity"]) for x in policy["allowed_skips"]}
                    for case, (outcome, _) in cases.items():
                        if outcome == "NotExecuted" and case not in allowed_skips:
                            errors.append({"reason": "UNEXPECTED_SKIP", "role": role, "lane": lane,
                                           "scope": key, "identity": case})
            except RatchetError as error:
                errors.append({"reason": str(error), "role": role, "lane": lane})
    extras = {p.name for p in evidence.glob("*.json")} - {
        f"{role}-{lane}.json" for role in ROLES for lane in baseline["jobs"]
    }
    if extras:
        errors.append({"reason": "UNEXPECTED_JOB_EVIDENCE", "count": len(extras)})
    return groups, observed


def compare(context, proposed, accepted, evidence, initial):
    failures = validate_baseline(accepted)
    validate_baseline(proposed)
    errors = []
    diff = safe_diff(accepted, proposed)
    if diff:
        errors.append({"reason": "BASELINE_CHANGE_REQUIRES_INDEPENDENT_ADMISSION"})
    groups, observed = collect(context, accepted, evidence, errors)
    summary = []
    for lane, scopes in accepted["jobs"].items():
        for key, policy in scopes.items():
            project = policy.get("project", key)
            old = groups.get(("base", lane, key))
            new = groups.get(("candidate", lane, key))
            if old is not None and initial and len(old) != policy["count"]:
                errors.append({"reason": "PINNED_BASE_PROJECT_COUNT_MISMATCH", "lane": lane,
                               "scope": key, "expected": policy["count"], "observed": len(old)})
            if old is not None and new is not None:
                for case in sorted(set(old) - set(new)):
                    errors.append({"reason": "MISSING_OR_RENAMED_TEST", "lane": lane,
                                   "scope": key, "identity": case})
            for role, cases in (("base", old), ("candidate", new)):
                if cases is None:
                    continue
                counts = dict.fromkeys(OUTCOMES, 0)
                for case, (outcome, sig) in cases.items():
                    counts[outcome] += 1
                    if outcome != "Failed":
                        continue
                    allowed = failures.get((project, case), set())
                    if sig not in allowed:
                        errors.append({"reason": "REPLACED_FAILURE_SIGNATURE" if allowed else "NEW_FAILURE",
                                       "role": role, "lane": lane, "scope": key,
                                       "identity": case, "signature": sig})
                summary.append({"role": role, "lane": lane, "scope": key, "counts": counts})
    return {"schema": 1, "context": context, "baseline": accepted["id"], "baselineDiff": diff,
            "bootstrapFromPinnedAnchor": initial, "observedJobBindings": sorted(observed),
            "summary": summary, "status": "FAIL" if errors else "PASS", "errors": errors}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    sub = cli.add_subparsers(dest="command", required=True)
    for action in ("capture", "compare"):
        p = sub.add_parser(action)
        p.add_argument("--context", type=Path, required=True)
        p.add_argument("--baseline", type=Path, required=True)
        p.add_argument("--out", type=Path, required=True)
    sub.choices["capture"].add_argument("--lane", required=True)
    sub.choices["capture"].add_argument("--role", choices=ROLES, required=True)
    sub.choices["capture"].add_argument("--run-inputs", type=Path, required=True)
    sub.choices["compare"].add_argument("--evidence", type=Path, required=True)
    sub.choices["compare"].add_argument("--base-baseline", type=Path, required=True)
    args = cli.parse_args()
    try:
        context = validate_context(read_json(args.context))
        candidate = read_json(args.baseline)
        if args.command == "capture":
            validate_baseline(candidate)
            require(args.lane in candidate["jobs"], "UNKNOWN_REQUIRED_JOB")
            result = capture(context, candidate, args.lane, args.role, args.run_inputs)
        else:
            initial = not args.base_baseline.exists()
            if initial:
                require(context["base"] == ANCHOR
                        and context["baseTree"] == "5014e711075f86282b0c54171ddbfacdd656f583"
                        and hex_sha(INITIAL_BASELINE_SHA256, 64),
                        "UNTRUSTED_BASELINE_BOOTSTRAP")
                reviewed = Path(__file__).with_name("c1-ratchet") / "baseline-v1.json"
                accepted = read_json(reviewed)
                require(hashlib.sha256(reviewed.read_bytes()).hexdigest() == INITIAL_BASELINE_SHA256,
                        "UNTRUSTED_BASELINE_BOOTSTRAP")
            else:
                accepted = read_json(args.base_baseline)
            result = compare(context, candidate, accepted, args.evidence, initial)
        save_json(args.out, result)
        if args.command == "compare":
            print(f"C1 Failure Ratchet: {result['status']} ({len(result['errors'])} findings)")
            return int(result["status"] != "PASS")
        return 0
    except (OSError, ValueError, KeyError, TypeError) as error:
        # Only our fixed validation codes may be exported. Native text and untrusted
        # inputs (including parser error messages) stay out of stdout and artifacts.
        code = str(error) if isinstance(error, (RatchetError, TrxError)) else "INPUT_OR_TEST_EVIDENCE_REJECTED"
        if args.command == "compare" and not args.out.exists():
            save_json(args.out, {"schema": 1, "status": "FAIL", "baselineDiff": None,
                                 "baselineDiffUnavailable": "Unsafe or unavailable baseline data",
                                 "errors": [{"reason": code}]})
        print(f"C1 Failure Ratchet rejected evidence: {code}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
