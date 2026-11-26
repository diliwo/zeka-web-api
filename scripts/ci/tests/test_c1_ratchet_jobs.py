"""Validate synthetic hosted-job evidence through the real unchanged entrypoint."""

from copy import deepcopy
from hashlib import sha256
import json
import os
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
import contextlib
import io
import importlib.util
from unittest.mock import patch


TOOLING = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLING))
from c1_ratchet import ANCHOR, AUTHORITY, RatchetError, identity  # noqa: E402
import c1_ratchet_context as ratchet_context  # noqa: E402

# The reusable input fixture must come from this exact tooling checkout, even
# under isolated Python entry or when another repository is on the import path.
_fixture_spec = importlib.util.spec_from_file_location(
    "c1_ratchet_local_input_fixtures", Path(__file__).with_name("test_c1_ratchet.py"))
ratchet_fixtures = importlib.util.module_from_spec(_fixture_spec)
_fixture_spec.loader.exec_module(ratchet_fixtures)


SCRIPT = TOOLING / "c1_ratchet_jobs.py"
PROJECT = "Services/Synthetic/Tests/C1.Ratchet.TestFixtures.csproj"
REQUIRED = (
    "Check out exact test source", "Check out ratchet tooling", "Install pinned .NET SDK",
    "Allocate private output and sanitized artifact paths",
    "Reject tracked NuGet credentials before credential delivery", "Restore and prepare the profile",
    "Execute test profile", "Normalize TRX evidence without test output",
    "Upload only sanitized test identity and result evidence",
)
CLI_STEP = "Install pinned standalone Sonar CLI for original scanner-aware tests"


class HostedJobEvidenceTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="zeka-jobs-unit-")
        self.addCleanup(directory.cleanup)
        self.folder = Path(directory.name)
        self.source = self.folder / "jobs.json"
        self.receipt = self.folder / "receipt.json"
        self.policy = self.folder / "baseline.json"
        self.baseline = {
            "schema": 1, "id": "C1-RATCHET-synthetic-jobs", "revision": "5" * 40,
            "anchor": ANCHOR, "authority": AUTHORITY,
            "jobs": {lane: {PROJECT: {"count": 1, "allowed_skips": []}}
                     for lane in ("non-postgres", "postgresql", "sonar-suite")},
            "failures": [],
            "obligations": [{
                "id": "H1", "project": PROJECT,
                "identity": ["C1.Ratchet.TestFixtures", "C1.Ratchet.TestFixtures.HistoricalTests.Unresolved", ""],
                "status": "NOT_REPRODUCED_RETAINED", "description": "Synthetic retained obligation",
                "issues": ["ZEK-17"],
                "evidence": "https://github.com/diliwo/zeka-web-api/actions/runs/43314934322/job/131520423950",
            }],
        }
        policy_bytes = json.dumps(self.baseline).encode("utf-8")
        self.policy.write_bytes(policy_bytes)
        self.context = {
            "schema": 2, "event": "pull_request", "candidate": "1" * 40, "base": "2" * 40,
            "testTree": "3" * 40, "baseTree": "4" * 40, "runId": "100", "runAttempt": "1",
            "repository": "diliwo/zeka-web-api", "mode": "installed", "tooling": "2" * 40,
            "policySha256": "6" * 64, "baselineSha256": sha256(policy_bytes).hexdigest(),
        }
        self.environment = {
            "CONTEXT_JSON": json.dumps(self.context), "GITHUB_REPOSITORY": "diliwo/zeka-web-api",
            "GITHUB_EVENT_NAME": "pull_request", "GITHUB_RUN_ID": "100", "GITHUB_RUN_ATTEMPT": "1",
            "JOB_API_EXIT": "0", "FIXTURE_RESULT": "success", "CONTEXT_RESULT": "success",
            "TEST_RESULT": "success", "DOWNLOAD_RESULT": "success", "COMPARE_EXIT": "0",
        }
        self.jobs = [self.job(role, lane, identifier) for identifier, (role, lane) in enumerate(
            ((role, lane) for role in ("base", "candidate") for lane in self.baseline["jobs"]), 1)]

    def job(self, role, lane, identifier):
        names = [*REQUIRED, *([CLI_STEP] if lane in ("non-postgres", "sonar-suite") else [])]
        return {"name": f"C1 {role} {lane}", "id": identifier, "head_sha": self.context["candidate"],
                "status": "completed", "conclusion": "success", "run_id": 100, "run_attempt": 1,
                "steps": [{"name": name, "conclusion": "success"} for name in names]}

    def execute(self, pages=None, expected_errors=(), environment=None, raw=None):
        self.source.write_text(json.dumps([{"jobs": self.jobs}] if pages is None else pages) if raw is None else raw,
                               encoding="utf-8")
        if self.receipt.exists():
            self.receipt.unlink()
        active_environment = {key: value for key, value in {**self.environment, **(environment or {})}.items()
                              if value is not None}
        with patch.dict(os.environ, active_environment, clear=True), \
                patch.object(sys, "argv", [str(SCRIPT), str(self.source), str(self.receipt), str(self.policy)]):
            if expected_errors:
                with self.assertRaisesRegex(SystemExit, "C1 Failure Ratchet rejected"):
                    runpy.run_path(str(SCRIPT), run_name="__main__")
            else:
                runpy.run_path(str(SCRIPT), run_name="__main__")
        result = json.loads(self.receipt.read_text(encoding="utf-8"))
        self.assertEqual(result["errors"], sorted(set(expected_errors)))
        self.assertEqual(result["status"], "FAIL" if expected_errors else "PASS")
        self.assertEqual(result["schema"], 1)
        return result

    @staticmethod
    def set_step(job, name, conclusion):
        next(step for step in job["steps"] if step["name"] == name)["conclusion"] = conclusion

    def test_all_required_profiles_and_exact_hosted_binding_pass(self):
        result = self.execute()
        self.assertEqual(set(result["jobs"]), {job["name"] for job in self.jobs})
        self.assertTrue(all(not item["onlyTestExecutionFailed"] for item in result["jobs"].values()))

    def test_unrelated_jobs_and_multiple_query_pages_do_not_satisfy_or_break_the_inventory(self):
        result = self.execute(pages=[{"jobs": self.jobs[:3]}, {"jobs": [{"name": "Other job"}, *self.jobs[3:]]}])
        self.assertEqual(len(result["jobs"]), 6)

    def test_only_test_execution_failure_is_classified_without_a_false_native_pass(self):
        self.jobs[0]["conclusion"] = "failure"
        self.set_step(self.jobs[0], "Execute test profile", "failure")
        result = self.execute(environment={"TEST_RESULT": "failure"})
        self.assertEqual(result["jobs"][self.jobs[0]["name"]]["conclusion"], "failure")
        self.assertTrue(result["jobs"][self.jobs[0]["name"]]["onlyTestExecutionFailed"])

    def test_failed_upstream_query_never_credits_present_job_bytes(self):
        result = self.execute(environment={"JOB_API_EXIT": "1"},
                              expected_errors=("UPSTREAM_JOB_QUERY_FAILED", "MISSING_REQUIRED_UPSTREAM_JOB"))
        self.assertEqual(result["jobs"], {})

    def test_missing_profile_is_refused(self):
        self.jobs.pop()
        self.execute(expected_errors=("MISSING_REQUIRED_UPSTREAM_JOB",))

    def test_duplicate_profile_is_refused_and_keeps_first_identity(self):
        duplicate = deepcopy(self.jobs[0])
        duplicate["id"] = 99
        self.jobs.append(duplicate)
        result = self.execute(expected_errors=("DUPLICATE_UPSTREAM_JOB",))
        self.assertEqual(result["jobs"][duplicate["name"]]["id"], 1)

    def test_invalid_json_is_refused(self):
        self.execute(raw="{", expected_errors=("MALFORMED_UPSTREAM_JOB_EVIDENCE", "MISSING_REQUIRED_UPSTREAM_JOB"))

    def test_malformed_pages_jobs_and_required_fields_are_refused(self):
        for pages in ({"jobs": self.jobs}, [{}], [{"jobs": [{"name": self.jobs[0]["name"]}]}],
                      [{"jobs": [{"name": []}]}]):
            with self.subTest(pages=pages):
                self.execute(pages=pages, expected_errors=("MALFORMED_UPSTREAM_JOB_EVIDENCE", "MISSING_REQUIRED_UPSTREAM_JOB"))

    def test_source_run_attempt_and_completion_drift_are_refused(self):
        for field, value in (("head_sha", "7" * 40), ("run_id", 101), ("run_attempt", 2), ("status", "in_progress")):
            with self.subTest(field=field):
                original = self.jobs[0][field]
                self.jobs[0][field] = value
                self.execute(expected_errors=("UPSTREAM_JOB_IDENTITY_OR_STATUS",))
                self.jobs[0][field] = original

    def test_missing_required_step_is_refused(self):
        self.jobs[0]["steps"] = self.jobs[0]["steps"][1:]
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))

    def test_duplicate_step_names_are_refused(self):
        self.jobs[0]["steps"].append(deepcopy(self.jobs[0]["steps"][0]))
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))

    def test_scanner_aware_profiles_require_the_pinned_cli_step(self):
        for index in (0, 2):
            with self.subTest(profile=self.jobs[index]["name"]):
                original = deepcopy(self.jobs[index]["steps"])
                self.jobs[index]["steps"] = [step for step in original if step["name"] != CLI_STEP]
                self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))
                self.jobs[index]["steps"] = original

    def test_skipped_or_cancelled_required_setup_is_refused(self):
        for conclusion in ("skipped", "cancelled", None):
            with self.subTest(conclusion=conclusion):
                self.set_step(self.jobs[0], "Restore and prepare the profile", conclusion)
                self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))

    def test_job_and_execution_step_conclusion_disagreement_is_refused(self):
        self.set_step(self.jobs[0], "Execute test profile", "skipped")
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))

    def test_successful_job_with_an_extra_failed_step_is_inconsistent(self):
        self.jobs[0]["steps"].append({"name": "Unexpected extra step", "conclusion": "failure"})
        self.execute(expected_errors=("INCONSISTENT_UPSTREAM_JOB",))

    def test_failed_setup_is_an_infrastructure_failure_not_an_admitted_test_failure(self):
        self.jobs[0]["conclusion"] = "failure"
        self.set_step(self.jobs[0], "Restore and prepare the profile", "failure")
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE", "UPSTREAM_INFRASTRUCTURE_FAILURE"))

    def test_cancelled_job_is_an_infrastructure_failure(self):
        self.jobs[0]["conclusion"] = "cancelled"
        self.set_step(self.jobs[0], "Execute test profile", "cancelled")
        self.execute(expected_errors=("UPSTREAM_INFRASTRUCTURE_FAILURE",))

    def test_failed_job_without_the_exact_execution_failure_is_refused(self):
        self.jobs[0]["conclusion"] = "failure"
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE", "UPSTREAM_INFRASTRUCTURE_FAILURE"))

    def test_missing_or_failed_fixture_result_is_refused(self):
        for value in (None, "", "failure", "cancelled"):
            with self.subTest(value=value):
                self.execute(environment={"FIXTURE_RESULT": value}, expected_errors=("RATCHET_FIXTURE_TESTS_FAILED",))

    def test_incomplete_context_test_download_or_comparison_is_refused(self):
        for field, value in (("CONTEXT_RESULT", "failure"), ("TEST_RESULT", "cancelled"),
                             ("DOWNLOAD_RESULT", "failure"), ("COMPARE_EXIT", "1")):
            with self.subTest(field=field):
                self.execute(environment={field: value}, expected_errors=("INCOMPLETE_OR_REJECTED_RATCHET_EVIDENCE",))

    def test_repeated_error_kinds_are_deduplicated_in_the_receipt(self):
        for job in self.jobs:
            job["status"] = "in_progress"
        self.execute(expected_errors=("UPSTREAM_JOB_IDENTITY_OR_STATUS",))

    def test_hosted_context_mismatch_is_rejected_before_receipt_creation(self):
        with self.assertRaisesRegex(RatchetError, "WORKFLOW_RUN_CONTEXT_MISMATCH"):
            self.execute(environment={"GITHUB_RUN_ID": "101"})
        self.assertFalse(self.receipt.exists())

    def test_changed_baseline_bytes_are_rejected_before_job_evidence(self):
        self.policy.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(RatchetError, "ACCEPTED_BASELINE_BYTES_MISMATCH"):
            self.execute()
        self.assertFalse(self.receipt.exists())

    def test_candidate_owned_tooling_context_is_not_accepted(self):
        context = {**self.context, "tooling": self.context["candidate"]}
        with self.assertRaisesRegex(RatchetError, "UNTRUSTED_POLICY_REVISION"):
            self.execute(environment={"CONTEXT_JSON": json.dumps(context)})
        self.assertFalse(self.receipt.exists())

    def bind_synthetic_policy(self, data=None):
        # This is only the owned fixture's input digest, never real source authority.
        content = json.dumps(self.baseline).encode("utf-8") if data is None else data
        self.policy.write_bytes(content)
        self.context["baselineSha256"] = sha256(content).hexdigest()
        self.environment["CONTEXT_JSON"] = json.dumps(self.context)

    def test_hash_bound_malformed_policy_bytes_are_refused_before_job_evidence(self):
        for data in (b"{", b"\xff"):
            with self.subTest(data=data):
                self.bind_synthetic_policy(data)
                with self.assertRaisesRegex(RatchetError, "MALFORMED_JSON"):
                    self.execute()
                self.assertFalse(self.receipt.exists())

    def test_duplicate_members_in_hash_bound_policy_cannot_override_acceptance(self):
        raw = json.dumps(self.baseline)[:-1] + ',"schema":1}'
        self.bind_synthetic_policy(raw.encode("utf-8"))
        with self.assertRaisesRegex(RatchetError, "DUPLICATE_JSON_KEY"):
            self.execute()
        self.assertFalse(self.receipt.exists())

    def test_malformed_authority_uri_is_refused_before_any_hosted_receipt(self):
        for authority in ("https://[broken", "https://example.invalid/%GG"):
            with self.subTest(authority=authority):
                self.baseline["authority"] = authority
                self.bind_synthetic_policy()
                with self.assertRaisesRegex(RatchetError, "BASELINE_SCHEMA_VALIDATION_FAILED"):
                    self.execute()
                self.assertFalse(self.receipt.exists())

    def test_explicit_linux_skip_policy_does_not_relax_hosted_job_requirements(self):
        rule = {"identity": ["C1.Ratchet.TestFixtures", "C1.Ratchet.TestFixtures.PlatformTests.LinuxOnly", ""],
                "platform": "linux", "reason": "Explicit synthetic platform restriction"}
        self.baseline["jobs"]["non-postgres"][PROJECT]["allowed_skips"] = [rule]
        self.bind_synthetic_policy()
        self.assertEqual(len(self.execute()["jobs"]), 6)
        self.set_step(self.jobs[0], "Restore and prepare the profile", "skipped")
        self.execute(expected_errors=("UPSTREAM_REQUIRED_STEPS_INCOMPLETE",))

    def test_duplicate_skip_rule_cannot_silently_expand_the_accepted_policy(self):
        rule = {"identity": ["C1.Ratchet.TestFixtures", "C1.Ratchet.TestFixtures.PlatformTests.LinuxOnly", ""],
                "platform": "linux", "reason": "Explicit synthetic platform restriction"}
        self.baseline["jobs"]["non-postgres"][PROJECT]["allowed_skips"] = [rule, deepcopy(rule)]
        self.bind_synthetic_policy()
        with self.assertRaisesRegex(RatchetError, "INVALID_SKIP_RULE"):
            self.execute()
        self.assertFalse(self.receipt.exists())

    def test_invalid_skip_identity_is_rejected_before_job_evidence(self):
        # The actual schema refuses this malformed identity before any receipt exists.
        self.baseline["jobs"]["non-postgres"][PROJECT]["allowed_skips"] = [{
            "identity": ["invalid assembly/name", "C1.Ratchet.TestFixtures.PlatformTests.LinuxOnly", ""],
            "platform": "linux", "reason": "Explicit synthetic platform restriction"}]
        self.bind_synthetic_policy()
        with self.assertRaisesRegex(RatchetError, "BASELINE_SCHEMA_VALIDATION_FAILED"):
            self.execute()
        self.assertFalse(self.receipt.exists())

    def test_policy_identity_parser_refuses_malformed_test_identity_values(self):
        for value in ([], ["invalid assembly/name", "Namespace.Class.Method", ""], "Namespace.Class.Method"):
            with self.subTest(value=value), self.assertRaisesRegex(RatchetError, "INVALID_TEST_IDENTITY"):
                identity(value)
    def test_each_hosted_context_field_must_match_the_actual_event_environment(self):
        for field, value in (("GITHUB_REPOSITORY", "someone/else"), ("GITHUB_EVENT_NAME", "push"),
                             ("GITHUB_RUN_ATTEMPT", "2"), ("GITHUB_RUN_ID", None)):
            with self.subTest(field=field):
                with self.assertRaisesRegex(RatchetError, "WORKFLOW_RUN_CONTEXT_MISMATCH"):
                    self.execute(environment={field: value})
                self.assertFalse(self.receipt.exists())


class RatchetCommandEvidenceTests(ratchet_fixtures.RatchetFixture):
    """Exercise the actual CLI against owned evidence, not copied gate code."""

    def setUp(self):
        super().setUp()
        self.policy = self.folder / "accepted.json"
        self.policy.write_text(json.dumps(self.baseline), encoding="utf-8")
        self.context["baselineSha256"] = sha256(self.policy.read_bytes()).hexdigest()
        self.context_file = self.folder / "context.json"
        self.context_file.write_text(json.dumps(self.context), encoding="utf-8")
        self.result_file = self.folder / "result.json"

    def command(self, action="compare", *, baseline=None, arguments=(), environment=None):
        args = [str(TOOLING / "c1_ratchet.py"), action, "--context", str(self.context_file),
                "--baseline", str(baseline or self.policy), "--out", str(self.result_file)]
        args += (["--accepted-baseline", str(self.policy), "--evidence", str(self.evidence)] if action == "compare"
                 else ["--lane", self.lane, "--role", "candidate", "--run-inputs", str(self.folder / "candidate")])
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false", **(environment or {})}, clear=True), \
                patch.object(sys, "argv", [*args, *map(str, arguments)]), \
                contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            with self.assertRaises(SystemExit) as ended:
                runpy.run_path(str(TOOLING / "c1_ratchet.py"), run_name="__main__")
        return ended.exception.code, stdout.getvalue(), stderr.getvalue()

    def test_cli_capture_keeps_failed_native_test_evidence_without_claiming_tests_passed(self):
        self.capture("candidate", self.candidate)
        native, stdout, stderr = self.command("capture")
        self.assertEqual((native, stdout, stderr), (0, "", ""))
        result = json.loads(self.result_file.read_text(encoding="utf-8"))
        self.assertEqual(result["context"], self.context)
        self.assertEqual(result["role"], "candidate")
        self.assertEqual({item["outcome"] for item in result["projects"][PROJECT]["results"]}, {"Passed", "Failed"})

    def test_cli_compare_accepts_only_complete_bound_evidence_and_the_same_baseline(self):
        self.both()
        native, stdout, stderr = self.command(environment={
            "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": self.context["repository"],
            "GITHUB_EVENT_NAME": self.context["event"], "GITHUB_RUN_ID": self.context["runId"],
            "GITHUB_RUN_ATTEMPT": self.context["runAttempt"]})
        self.assertEqual((native, stderr), (0, ""))
        self.assertEqual(stdout.strip(), "C1 Failure Ratchet: PASS (0 findings)")
        result = json.loads(self.result_file.read_text(encoding="utf-8"))
        self.assertEqual(result["baselineDiff"], "")
        self.assertEqual(result["errors"], [])
        self.assertFalse(result["bootstrapFromPinnedAnchor"])

    def test_cli_unknown_lane_refuses_capture_before_writing_any_evidence(self):
        native, _, stderr = self.command("capture", arguments=("--lane", "unapproved"))
        self.assertEqual(native, 1)
        self.assertIn("UNKNOWN_REQUIRED_JOB", stderr)
        self.assertFalse(self.result_file.exists())

    def test_cli_native_zero_cannot_relabel_failed_trx_rows(self):
        self.capture("candidate", self.candidate)
        invocation = self.folder / "candidate" / "invocations.json"
        data = json.loads(invocation.read_text(encoding="utf-8"))
        data["invocations"][0]["exitCode"] = 0
        invocation.write_text(json.dumps(data), encoding="utf-8")
        native, _, stderr = self.command("capture")
        self.assertEqual(native, 1)
        self.assertIn("NATIVE_EXIT_AND_TRX_DISAGREE", stderr)
        self.assertFalse(self.result_file.exists())

    def test_cli_changed_accepted_bytes_emit_a_fail_receipt_not_a_candidate_diff(self):
        self.policy.write_bytes(self.policy.read_bytes() + b"\n")
        native, stdout, stderr = self.command()
        self.assertEqual((native, stdout), (1, ""))
        self.assertIn("ACCEPTED_BASELINE_BYTES_MISMATCH", stderr)
        result = json.loads(self.result_file.read_text(encoding="utf-8"))
        self.assertEqual(result["status"], "FAIL")
        self.assertIsNone(result["baselineDiff"])
        self.assertEqual(result["errors"], [{"reason": "ACCEPTED_BASELINE_BYTES_MISMATCH"}])

    def test_cli_malformed_candidate_reports_only_a_controlled_error(self):
        marker = "synthetic-sensitive-row-canary"
        proposed = self.folder / "proposed.json"
        proposed.write_text('{"row":"' + marker, encoding="utf-8")
        native, stdout, stderr = self.command(baseline=proposed)
        self.assertEqual((native, stdout), (1, ""))
        self.assertIn("MALFORMED_JSON", stderr)
        result = self.result_file.read_text(encoding="utf-8")
        self.assertNotIn(marker, stdout + stderr + result)
        self.assertEqual(json.loads(result)["errors"], [{"reason": "MALFORMED_JSON"}])

    def test_cli_candidate_baseline_cannot_admit_itself(self):
        self.both()
        proposed = self.folder / "proposed.json"
        proposed.write_text(json.dumps({**self.baseline, "id": self.baseline["id"] + "-changed"}), encoding="utf-8")
        native, stdout, stderr = self.command(baseline=proposed)
        self.assertEqual((native, stderr), (1, ""))
        self.assertEqual(stdout.strip(), "C1 Failure Ratchet: FAIL (1 findings)")
        result = json.loads(self.result_file.read_text(encoding="utf-8"))
        self.assertEqual(result["errors"], [{"reason": "BASELINE_CHANGE_REQUIRES_INDEPENDENT_ADMISSION"}])
        self.assertIn("-changed", result["baselineDiff"])

    def test_cli_missing_profile_artifacts_remain_explicit_failures(self):
        native, stdout, stderr = self.command()
        self.assertEqual((native, stderr), (1, ""))
        self.assertEqual(stdout.strip(), "C1 Failure Ratchet: FAIL (2 findings)")
        result = json.loads(self.result_file.read_text(encoding="utf-8"))
        self.assertEqual({item["reason"] for item in result["errors"]}, {"MISSING_REQUIRED_JOB_EVIDENCE"})
        self.assertEqual({item["role"] for item in result["errors"]}, {"base", "candidate"})

    def test_cli_existing_output_is_never_overwritten_even_by_valid_input(self):
        self.both()
        retained = b'{"retained":"original-evidence"}\n'
        self.result_file.write_bytes(retained)
        native, stdout, stderr = self.command()
        self.assertEqual((native, stdout), (1, ""))
        self.assertIn("INPUT_OR_TEST_EVIDENCE_REJECTED", stderr)
        self.assertEqual(self.result_file.read_bytes(), retained)

    def test_cli_mismatched_hosted_run_fails_before_accepting_the_local_artifact(self):
        native, _, stderr = self.command("capture", environment={"GITHUB_ACTIONS": "true"})
        self.assertEqual(native, 1)
        self.assertIn("WORKFLOW_RUN_CONTEXT_MISMATCH", stderr)
        self.assertFalse(self.result_file.exists())


class ContextCommandBoundaryTests(unittest.TestCase):
    """Early context-command refusals must not inspect Git or echo untrusted data."""

    def setUp(self):
        directory = tempfile.TemporaryDirectory(prefix="zeka-context-command-unit-")
        self.addCleanup(directory.cleanup)
        self.folder = Path(directory.name)

    def command(self, arguments=(), environment=None):
        stderr = io.StringIO()
        with patch.object(sys, "argv", [str(TOOLING / "c1_ratchet_context.py"), *map(str, arguments)]), \
                patch.dict(os.environ, environment or {}, clear=True), contextlib.redirect_stderr(stderr):
            with self.assertRaises(SystemExit) as ended:
                runpy.run_path(str(TOOLING / "c1_ratchet_context.py"), run_name="__main__")
        self.assertEqual(ended.exception.code, 1)
        return stderr.getvalue()

    def test_unknown_context_command_is_refused_without_a_git_operation(self):
        self.assertEqual(self.command(("unapproved-operation",)),
                         "C1 ratchet context refused: INVALID_CONTEXT_COMMAND\n")

    def test_missing_event_path_has_a_fixed_safe_diagnostic(self):
        self.assertEqual(self.command(),
                         "C1 ratchet context refused: CONTEXT_IO_OR_ENVIRONMENT_UNAVAILABLE\n")

    def test_malformed_event_bytes_fail_before_resolving_any_repository(self):
        event = self.folder / "event.json"
        for contents in (b'\xff', b'{"synthetic-row":"do-not-export"', b'{"a":1,"a":2}', b'{"a":NaN}'):
            with self.subTest(contents=contents):
                event.write_bytes(contents)
                result = self.command(environment={"GITHUB_EVENT_PATH": str(event)})
                expected = "DUPLICATE_JSON_KEY" if contents == b'{"a":1,"a":2}' else (
                    "MALFORMED_JSON" if contents == b'{"a":NaN}' else "INVALID_GITHUB_EVENT")
                self.assertEqual(result, f"C1 ratchet context refused: {expected}\n")
                self.assertNotIn("do-not-export", result)

    def test_tooling_context_file_is_bounded_and_malformed_input_has_no_fallback(self):
        context = self.folder / "context.json"
        for contents in (b" " * 8193, b"[]", b'{"schema":2}', b'\xff'):
            with self.subTest(size=len(contents)):
                context.write_bytes(contents)
                self.assertEqual(self.command(("verify-tooling", context, self.folder)),
                                 "C1 ratchet context refused: INVALID_TOOLING_CONTEXT\n")

    def test_tooling_path_must_be_a_directory_before_git_can_be_invoked(self):
        path = self.folder / "not-a-checkout"
        path.write_text("public synthetic fixture", encoding="utf-8")
        context = {"schema": 2, "mode": "installed", "tooling": "1" * 40, "base": "1" * 40,
                   "policySha256": "2" * 64, "baselineSha256": "3" * 64}
        with self.assertRaisesRegex(ratchet_context.ContextError, "UNSAFE_TOOLING_PATH"):
            ratchet_context.verify_tooling(context, path)


if __name__ == "__main__":
    unittest.main()
