"""Validate hosted jobs against the protected revision's execution policy."""

import itertools
import json
import os
import sys
from pathlib import Path

from c1_ratchet import (
    read_accepted_baseline, validate_baseline, validate_context, validate_hosted_context, validate_policy_schema,
)

source, receipt, policy = map(Path, sys.argv[1:])
context = validate_context(json.loads(os.environ["CONTEXT_JSON"]))
validate_hosted_context(context)
baseline = read_accepted_baseline(policy, context)
validate_policy_schema(baseline)
validate_baseline(baseline)
lanes = baseline["jobs"]
expected = {f"C1 {role} {lane}" for role, lane in itertools.product(("base", "candidate"), lanes)}
errors = []
selected = {}
if os.environ["JOB_API_EXIT"] != "0":
    errors.append("UPSTREAM_JOB_QUERY_FAILED")
else:
    try:
        for page in json.loads(source.read_text(encoding="utf-8")):
            for job in page["jobs"]:
                name = job["name"]
                if name not in expected:
                    continue
                if name in selected:
                    errors.append("DUPLICATE_UPSTREAM_JOB")
                    continue
                required = {"Check out exact test source", "Check out ratchet tooling",
                            "Install pinned .NET SDK", "Allocate private output and sanitized artifact paths",
                            "Reject tracked NuGet credentials before credential delivery",
                            "Restore and prepare the profile", "Execute test profile",
                            "Normalize TRX evidence without test output",
                            "Upload only sanitized test identity and result evidence"}
                if name.endswith((" non-postgres", " sonar-suite")):
                    required.add("Install pinned standalone Sonar CLI for original scanner-aware tests")
                steps = {step["name"]: step["conclusion"] for step in job["steps"]}
                if (len(steps) != len(job["steps"]) or not required <= steps.keys()
                        or any(steps[item] != "success" for item in required - {"Execute test profile"})
                        or steps.get("Execute test profile") != job["conclusion"]):
                    errors.append("UPSTREAM_REQUIRED_STEPS_INCOMPLETE")
                failures = [step["name"] for step in job["steps"] if step["conclusion"] == "failure"]
                selected[name] = {"id": job["id"], "status": job["status"],
                                  "conclusion": job["conclusion"],
                                  "onlyTestExecutionFailed": failures == ["Execute test profile"]}
                if (job["head_sha"] != context.get("candidate") or job["status"] != "completed"
                        or job["run_id"] != int(os.environ["GITHUB_RUN_ID"])
                        or job["run_attempt"] != int(os.environ["GITHUB_RUN_ATTEMPT"])):
                    errors.append("UPSTREAM_JOB_IDENTITY_OR_STATUS")
                if job["conclusion"] == "success":
                    if failures:
                        errors.append("INCONSISTENT_UPSTREAM_JOB")
                elif job["conclusion"] != "failure" or failures != ["Execute test profile"]:
                    errors.append("UPSTREAM_INFRASTRUCTURE_FAILURE")
    except (KeyError, OSError, TypeError, ValueError):
        errors.append("MALFORMED_UPSTREAM_JOB_EVIDENCE")
if set(selected) != expected:
    errors.append("MISSING_REQUIRED_UPSTREAM_JOB")
if (os.environ["CONTEXT_RESULT"] != "success" or os.environ["TEST_RESULT"] not in ("success", "failure")
        or os.environ["DOWNLOAD_RESULT"] != "success" or os.environ["COMPARE_EXIT"] != "0"):
    errors.append("INCOMPLETE_OR_REJECTED_RATCHET_EVIDENCE")
receipt.write_text(json.dumps({"schema": 1, "jobs": selected, "errors": sorted(set(errors)),
                               "status": "FAIL" if errors else "PASS"}, indent=2) + "\n", encoding="utf-8")
if errors:
    raise SystemExit("C1 Failure Ratchet rejected upstream or comparison evidence.")
