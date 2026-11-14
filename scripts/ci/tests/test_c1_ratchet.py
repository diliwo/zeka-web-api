"""Deterministic end-to-end tests of C1 evidence capture and comparison."""

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path
import subprocess
from unittest.mock import patch
from tempfile import TemporaryDirectory
from unittest import TestCase, main
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from c1_ratchet import (
    ANCHOR, AUTHORITY, RatchetError, capture, compare, read_accepted_baseline,
    read_json, save_json, validate_context, validate_hosted_context, validate_policy_schema,
)
from c1_ratchet_trx import TrxError, identify_project, load_trx


TRX = Path(__file__).with_name("c1-ratchet")
TOOLING = Path(__file__).resolve().parents[1]
POLICY = TOOLING / "c1-ratchet"
PROJECT = "Services/Synthetic/Tests/C1.Ratchet.TestFixtures.csproj"
ASSEMBLY = "C1.Ratchet.TestFixtures"
FQN = "C1.Ratchet.TestFixtures.ProbeTests."
THEORY_ROW = '(sha256:' + sha256(b'(kind: "permission")').hexdigest() + ')'


def case(method, row=""):
    return [ASSEMBLY, FQN + method, row]


def failure(identifier, method, assertion, row=""):
    return {
        "id": identifier,
        "project": PROJECT,
        "identity": case(method, row),
        "signatures": [{"exception": "not-emitted", "assertion": assertion, "frame": FQN + method}],
        "issues": ["ZEK-17"],
    }


class RatchetFixtureTests(TestCase):
    def setUp(self):
        directory = TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.folder = Path(directory.name)
        self.evidence = self.folder / "evidence"
        self.evidence.mkdir()
        self.context = validate_context({
            "schema": 2,
            "event": "pull_request",
            "candidate": "1" * 40,
            "base": ANCHOR,
            "testTree": "2" * 40,
            "baseTree": "3" * 40,
            "runId": "100",
            "runAttempt": "1",
            "repository": "diliwo/zeka-web-api",
            "mode": "installed",
            "tooling": ANCHOR,
            "policySha256": "5" * 64,
            "baselineSha256": "6" * 64,
        })
        self.lane = "non-pg"
        self.baseline = {
            "schema": 1,
            "id": "C1-RATCHET-fixture-v1",
            "revision": "4" * 40,
            "anchor": ANCHOR,
            "authority": AUTHORITY,
            "jobs": {self.lane: {PROJECT: {"count": 3, "allowed_skips": []}}},
            "failures": [
                failure("A1", "KnownAuthorityFailure", "Assert.Equal:Unavailable:Authorized", THEORY_ROW),
                failure("A2", "Companion", "Assert.Single:empty"),
            ],
            "obligations": [{
                "id": "H1",
                "project": PROJECT,
                "identity": case("Historical"),
                "status": "NOT_REPRODUCED_RETAINED",
                "description": "Synthetic historical evidence",
                "issues": ["ZEK-17"],
                "evidence": "https://github.com/diliwo/zeka-web-api/actions/runs/43314934322/job/131520423950",
            }],
        }
        self.base = (TRX / "baseline.trx").read_text(encoding="utf-8")
        self.candidate = (TRX / "candidate.trx").read_text(encoding="utf-8")

    def capture(self, role, trx, exit_code=1):
        invocation = self.folder / role
        results = invocation / "results"
        results.mkdir(parents=True)
        save_json(invocation / "invocations.json", {
            "schema": 1,
            "sourceSha": self.context["base" if role == "base" else "candidate"],
            "tree": self.context["baseTree" if role == "base" else "testTree"],
            "started": True,
            "completed": True,
            "invocations": [{"id": PROJECT, "project": PROJECT, "exitCode": exit_code, "resultsDir": "results"}],
        })
        if trx is not None:
            (results / "test.trx").write_text(trx, encoding="utf-8")
        result = capture(self.context, self.baseline, self.lane, role, invocation)
        save_json(self.evidence / f"{role}-{self.lane}.json", result)
        return result

    def compare(self, proposed=None):
        return compare(self.context, self.baseline if proposed is None else proposed,
                       self.baseline, self.evidence, initial=True)

    def assert_failed_for(self, result, reason):
        self.assertEqual(result["status"], "FAIL")
        self.assertIn(reason, {error["reason"] for error in result["errors"]})

    def both(self, text=None):
        self.capture("base", self.base)
        return self.capture("candidate", self.candidate if text is None else text)

    def test_complete_observations_subset_of_admitted_failures_pass(self):
        result = self.both()
        self.assertEqual(result["projects"][PROJECT]["results"][2]["identity"][-1], THEORY_ROW)
        self.assertNotIn('(kind: "permission")', str(result))
        self.assertEqual(self.compare()["status"], "PASS")
        self.assertEqual(self.compare()["baselineDiff"], "")
        self.assertEqual(identify_project(TRX / "candidate.trx", [PROJECT]), PROJECT)

    def test_hosted_context_rejects_other_run_or_attempt(self):
        hosted = {
            "GITHUB_REPOSITORY": self.context["repository"],
            "GITHUB_EVENT_NAME": self.context["event"],
            "GITHUB_RUN_ID": self.context["runId"],
            "GITHUB_RUN_ATTEMPT": self.context["runAttempt"],
        }
        with patch.dict("os.environ", hosted):
            validate_hosted_context(self.context)
            for key, other in (("runId", "101"), ("runAttempt", "2")):
                with self.subTest(field=key), self.assertRaisesRegex(RatchetError, "WORKFLOW_RUN_CONTEXT_MISMATCH"):
                    validate_hosted_context({**self.context, key: other})

    def test_installed_context_rejects_candidate_owned_tooling(self):
        with self.assertRaisesRegex(RatchetError, "UNTRUSTED_POLICY_REVISION"):
            validate_context({**self.context, "tooling": self.context["candidate"]})

    def test_bootstrap_context_rejects_unapproved_baseline_digest(self):
        with self.assertRaisesRegex(RatchetError, "UNTRUSTED_BASELINE_BOOTSTRAP"):
            validate_context({**self.context, "mode": "bootstrap", "tooling": self.context["candidate"]})

    def test_accepted_baseline_bytes_cannot_be_replaced_or_removed(self):
        path = self.folder / "accepted.json"
        save_json(path, self.baseline)
        context = {**self.context, "baselineSha256": sha256(path.read_bytes()).hexdigest()}
        self.assertEqual(read_accepted_baseline(path, context), self.baseline)
        path.write_text("{}", encoding="utf-8")
        with self.assertRaisesRegex(RatchetError, "ACCEPTED_BASELINE_BYTES_MISMATCH"):
            read_accepted_baseline(path, context)
        path.unlink()
        with self.assertRaisesRegex(RatchetError, "MISSING_OR_UNSAFE_JSON"):
            read_accepted_baseline(path, context)

    def test_exact_tooling_schema_accepts_the_reviewed_v1_and_normal_fixture(self):
        validate_policy_schema(read_json(POLICY / "baseline-v1.json"))
        validate_policy_schema(self.baseline)

    def test_tooling_schema_rejects_object_array_and_scalar_drift(self):
        baseline = self.baseline
        attempts = (
            {**baseline, "schema": True},
            {**baseline, "authority": "relative/owner"},
            {**baseline, "jobs": {"Lane": baseline["jobs"][self.lane]}},
            {**baseline, "jobs": {self.lane: {PROJECT: {"count": True, "allowed_skips": []}}}},
            {**baseline, "jobs": {self.lane: {PROJECT: {"count": 0, "allowed_skips": []}}}},
            {**baseline, "failures": [{**baseline["failures"][0], "identity": ["A", "M"]}]},
            {**baseline, "failures": [{**baseline["failures"][0], "identity": ["A", "M", "x" * 1025]}]},
            {**baseline, "obligations": []},
            {**baseline, "ratchet-admission": "candidate claim"},
        )
        for index, attempt in enumerate(attempts, start=1):
            with self.subTest(attempt=index), self.assertRaisesRegex(
                    RatchetError, "^BASELINE_SCHEMA_VALIDATION_FAILED$"):
                validate_policy_schema(attempt)

    def test_unsupported_malformed_and_remote_schema_are_rejected_even_if_unused(self):
        original = read_json(POLICY / "baseline.schema.json")
        attempts = (
            {"oneOf": [{"type": "string"}]},
            {"$ref": "https://example.invalid/schema"},
            {"$ref": "#/$defs/unused"},
            {"properties": []},
            {"required": ["duplicate", "duplicate"]},
            {"pattern": "["},
        )
        path = self.folder / "baseline.schema.json"
        for index, attempt in enumerate(attempts, start=1):
            with self.subTest(attempt=index):
                tampered = {**original, "$defs": {**original["$defs"], "unused": attempt}}
                path.write_text(json.dumps(tampered), encoding="utf-8")
                with self.assertRaisesRegex(RatchetError, "^UNSUPPORTED_BASELINE_SCHEMA$"):
                    validate_policy_schema(self.baseline, path)

    def run_schema_cli(self, action, schema, proposed=None):
        root = self.folder / ("cli-" + action)
        root.mkdir()
        (root / "c1-ratchet").mkdir()
        for filename in ("c1_ratchet.py", "c1_ratchet_trx.py"):
            (root / filename).write_bytes((TOOLING / filename).read_bytes())
        save_json(root / "c1-ratchet" / "baseline.schema.json", schema)
        accepted = root / "accepted.json"
        save_json(accepted, self.baseline)
        context = root / "context.json"
        digest_context = {**self.context, "baselineSha256": sha256(accepted.read_bytes()).hexdigest()}
        save_json(context, digest_context)
        for receipt_path in self.evidence.glob("*.json"):
            receipt = read_json(receipt_path)
            receipt["context"] = digest_context
            receipt_path.write_text(json.dumps(receipt), encoding="utf-8")
        out = root / "output.json"
        baseline = accepted
        if action == "compare":
            baseline = root / "candidate" / "baseline-v1.json"
            baseline.parent.mkdir()
            save_json(baseline, proposed if proposed is not None else self.baseline)
            # A permissive candidate schema cannot affect selected tooling validation.
            save_json(baseline.with_name("baseline.schema.json"), {"type": "object"})
            args = ["--accepted-baseline", str(accepted), "--evidence", str(self.evidence)]
        else:
            args = ["--lane", self.lane, "--role", "base", "--run-inputs", str(self.folder / "base")]
        command = [sys.executable, "-E", str(root / "c1_ratchet.py"), action,
                   "--context", str(context), "--baseline", str(baseline), "--out", str(out), *args]
        with patch.dict("os.environ", {"GITHUB_ACTIONS": "false"}):
            result = subprocess.run(command, check=False, capture_output=True, text=True, timeout=30)
        return result, out

    def test_capture_cli_rejects_data_invalid_under_selected_schema(self):
        self.both()
        schema = read_json(POLICY / "baseline.schema.json")
        schema["properties"]["id"]["minLength"] = 50
        result, out = self.run_schema_cli("capture", schema)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("BASELINE_SCHEMA_VALIDATION_FAILED", result.stderr)
        self.assertFalse(out.exists())

    def test_compare_cli_rejects_candidate_under_tooling_schema_before_semantic_diff(self):
        self.both()
        schema = read_json(POLICY / "baseline.schema.json")
        schema["properties"]["id"]["maxLength"] = len(self.baseline["id"])
        result, out = self.run_schema_cli("compare", schema, {**self.baseline, "id": self.baseline["id"] + "-other"})
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assert_failed_for(read_json(out), "BASELINE_SCHEMA_VALIDATION_FAILED")

    def test_compare_and_capture_cli_accept_current_schema(self):
        self.both()
        schema = read_json(POLICY / "baseline.schema.json")
        result, out = self.run_schema_cli("compare", schema)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(read_json(out)["status"], "PASS")

        result, out = self.run_schema_cli("capture", schema)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(read_json(out)["lane"], self.lane)

    def test_abbreviated_theory_rows_use_hashed_bound_test_ids(self):
        from xml.etree import ElementTree as ET
        ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        caption = '(kind: "' + 'x' * 50 + '"...)'
        text = self.candidate.replace('(kind: &quot;permission&quot;)', caption.replace('"', '&quot;'))
        root = ET.fromstring(text)
        original_id = "11111111-1111-4111-8111-000000000001"
        second_id = "11111111-1111-4111-8111-000000000004"
        original_execution = "22222222-2222-4222-8222-000000000001"
        second_execution = "22222222-2222-4222-8222-000000000004"
        for tag in ("Results", "TestDefinitions", "TestEntries"):
            parent = root.find(ns + tag)
            row = ET.tostring(parent[0], encoding="unicode")
            parent.append(ET.fromstring(row.replace(original_id, second_id).replace(original_execution, second_execution)))
        root.find(f"{ns}ResultSummary/{ns}Counters").attrib.update(total="4", executed="4", failed="2")
        trx = self.folder / "abbreviated.trx"
        trx.write_text(ET.tostring(root, encoding="unicode"), encoding="utf-8")
        report = f"[xUnit.net 00:00:00.33]     {FQN}KnownAuthorityFailure{caption.replace(chr(34), '&quot;')} [FAIL]"
        with_reports = self.with_runner_errors(trx.read_text(encoding="utf-8"), report, report)
        trx.write_text(with_reports, encoding="utf-8")
        rows = load_trx(trx, PROJECT)
        expected = {case("KnownAuthorityFailure", "(sha256:" + sha256(identifier.encode()).hexdigest() + ")")[-1]
                    for identifier in (original_id, second_id)}
        self.assertEqual({row["identity"][-1] for row in rows if row["outcome"] == "Failed"}, expected)
        self.assertNotIn(caption, str(rows))
        self.assertNotIn(original_id, str(rows))
        self.assertNotIn(second_id, str(rows))

        rerun = ET.tostring(root, encoding="unicode").replace(
            original_execution, "33333333-3333-4333-8333-000000000001")
        trx.write_text(rerun, encoding="utf-8")
        self.assertEqual(load_trx(trx, PROJECT), rows)

        trx.write_text(rerun.replace("VsTestRunner2/netcoreapp", "UnknownRunner"), encoding="utf-8")
        with self.assertRaisesRegex(TrxError, "TRX_ABBREVIATED_CASE_ADAPTER_UNSUPPORTED"):
            load_trx(trx, PROJECT)

    def test_literal_dots_and_unicode_spaces_preserve_the_caption_hash(self):
        caption = '(kind: "first\u00a0second\u2003...")'
        trx = self.folder / "unicode.trx"
        trx.write_text(self.candidate.replace('(kind: &quot;permission&quot;)', caption.replace('"', '&quot;')),
                       encoding="utf-8")
        rows = load_trx(trx, PROJECT)
        expected = "(sha256:" + sha256(caption.encode("utf-8")).hexdigest() + ")"
        self.assertIn(case("KnownAuthorityFailure", expected), [row["identity"] for row in rows])
        self.assertNotIn(caption, str(rows))

    def test_line_or_paragraph_separators_in_theory_captions_are_rejected(self):
        for separator in ("\u2028", "\u2029", "&#x9;"):
            with self.subTest(separator=repr(separator)):
                trx = self.folder / "unsafe.trx"
                trx.write_text(self.candidate.replace("&quot;permission&quot;", f"&quot;first{separator}second&quot;"),
                               encoding="utf-8")
                with self.assertRaisesRegex(TrxError, "TRX_THEORY_ROW_UNSAFE_OR_AMBIGUOUS"):
                    load_trx(trx, PROJECT)

    def test_new_failure_rejected(self):
        text = self.candidate.replace(
            'outcome="Passed" testName="' + FQN + 'Healthy" />',
            'outcome="Failed" testName="' + FQN + 'Healthy">'
            '<Output><ErrorInfo><Message>Assert.True() Failure&#10;Expected: True&#10;Actual: False</Message>'
            '<StackTrace>at ' + FQN + 'Healthy()</StackTrace></ErrorInfo></Output></UnitTestResult>',
        ).replace('passed="2" failed="1"', 'passed="1" failed="2"')
        self.both(text)
        self.assert_failed_for(self.compare(), "NEW_FAILURE")

    def test_runner_failure_summary_correlates_to_new_failure(self):
        text = self.candidate.replace(
            'outcome="Passed" testName="' + FQN + 'Healthy" />',
            'outcome="Failed" testName="' + FQN + 'Healthy">'
            '<Output><ErrorInfo><Message>Assert.True() Failure&#10;Expected: True&#10;Actual: False</Message>'
            '<StackTrace>at ' + FQN + 'Healthy()</StackTrace></ErrorInfo></Output></UnitTestResult>',
        ).replace('passed="2" failed="1"', 'passed="1" failed="2"')
        text = self.with_runner_errors(text, f"[xUnit.net 00:00:00.33]     {FQN}Healthy [FAIL]")
        evidence = self.both(text)
        self.assert_failed_for(self.compare(), "NEW_FAILURE")
        self.assertNotIn("[xUnit.net", str(evidence))

    def test_unbound_or_repeated_run_errors_reject_evidence(self):
        failure_display = FQN + 'KnownAuthorityFailure(kind: &quot;permission&quot;)'
        valid = f"[xUnit.net 00:00:00.33]     {failure_display} [FAIL]"
        for errors in (
            ("Test host crashed",),
            ("",),
            (f"[xUnit.net 00:00:00.33]     {FQN}Healthy [FAIL]",),
            (valid.replace("permission", "different"),),
            (valid + " while closing the recorder",),
            (valid, valid),
        ):
            with self.subTest(errors=errors):
                trx = self.folder / "run-error.trx"
                trx.write_text(self.with_runner_errors(self.candidate, *errors), encoding="utf-8")
                with self.assertRaisesRegex(TrxError, "TRX_RUN_INFRASTRUCTURE_ERROR"):
                    load_trx(trx, PROJECT)

    @staticmethod
    def with_runner_errors(text, *errors):
        items = "".join(f'<RunInfo outcome="Error"><Text>{error}</Text></RunInfo>' for error in errors)
        return text.replace("</ResultSummary>", f"<RunInfos>{items}</RunInfos></ResultSummary>")

    def test_unrecognized_failure_exports_only_safe_rejection_signature(self):
        text = self.candidate.replace("Assert.Equal() Failure", "Unsupported raw diagnostic")
        result = self.both(text)
        cases = result["projects"][PROJECT]["results"]
        failure_record = next(item for item in cases if item["outcome"] == "Failed")
        self.assertEqual(failure_record["signature"]["assertion"], "unrecognized-failure")
        self.assertNotIn("Unsupported raw diagnostic", str(result))
        self.assert_failed_for(self.compare(), "REPLACED_FAILURE_SIGNATURE")

    def test_replaced_failure_signature_rejected(self):
        self.both(self.candidate.replace("Expected: Unavailable\nActual:   Authorized", "Expected: 2\nActual:   1"))
        self.assert_failed_for(self.compare(), "REPLACED_FAILURE_SIGNATURE")

    def test_missing_test_rejected(self):
        from xml.etree import ElementTree as ET
        ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        root = ET.fromstring(self.candidate)
        for tag in ("Results", "TestDefinitions", "TestEntries"):
            parent = root.find(ns + tag)
            parent.remove(parent[1])
        counters = root.find(f"{ns}ResultSummary/{ns}Counters")
        counters.attrib.update(total="2", executed="2", passed="1")
        self.both(ET.tostring(root, encoding="unicode"))
        self.assert_failed_for(self.compare(), "MISSING_OR_RENAMED_TEST")

    def test_renamed_test_rejected(self):
        self.both(self.candidate.replace("Healthy", "Renamed"))
        self.assert_failed_for(self.compare(), "MISSING_OR_RENAMED_TEST")

    def test_unexpected_skip_rejected_even_if_vstest_skip_counter_zero(self):
        self.both(self.candidate.replace('outcome="Passed" testName="' + FQN + 'Healthy"',
                                         'outcome="NotExecuted" testName="' + FQN + 'Healthy"')
                  .replace('executed="3" passed="2"', 'executed="2" passed="1"'))
        self.assert_failed_for(self.compare(), "UNEXPECTED_SKIP")

    def test_truncated_trx_rejected(self):
        with self.assertRaises(TrxError):
            self.capture("candidate", self.candidate[:1000])

    def test_zero_trx_files_rejected(self):
        with self.assertRaisesRegex(RatchetError, "MISSING_OR_UNSAFE_TRX"):
            self.capture("candidate", None)

    def test_unauthorized_baseline_growth_rejected_with_complete_diff(self):
        self.both()
        proposed = deepcopy(self.baseline)
        proposed["failures"].append(failure("A3", "Healthy", "Assert.True:True:False"))
        result = self.compare(proposed)
        self.assert_failed_for(result, "BASELINE_CHANGE_REQUIRES_INDEPENDENT_ADMISSION")
        self.assertIn('+"', result["baselineDiff"].replace("+        ", "+").replace("+      ", "+"))
        self.assertIn('Assert.True:True:False', result["baselineDiff"])

    def test_pr_authored_admission_field_does_not_authorize_change(self):
        self.both()
        proposed = deepcopy(self.baseline)
        proposed["ratchet-admission"] = "Synthetic owner claim"
        with self.assertRaisesRegex(RatchetError, "INVALID_BASELINE"):
            self.compare(proposed)

    def test_missing_required_job_rejected(self):
        self.capture("base", self.base)
        self.assert_failed_for(self.compare(), "MISSING_REQUIRED_JOB_EVIDENCE")

    def test_nonzero_infrastructure_counter_rejects_otherwise_complete_trx(self):
        with self.assertRaisesRegex(TrxError, "TRX_RUN_INFRASTRUCTURE_COUNTER_NONZERO"):
            self.capture("candidate", self.candidate.replace('aborted="0"', 'aborted="1"'))

    def test_unapproved_compilation_profile_rejected(self):
        for old, new in [
            ("/bin/Release/", "/bin/Debug/"),
            ("/net8.0/", "/net9.0/"),
            (f"/{ASSEMBLY}.dll", "/Renamed.dll"),
        ]:
            with self.subTest(profile=new):
                with TemporaryDirectory() as directory:
                    trx = Path(directory) / "bad.trx"
                    trx.write_text(self.candidate.replace(old, new), encoding="utf-8")
                    with self.assertRaisesRegex(TrxError, "TRX_PROJECT_BINARY_MISMATCH"):
                        load_trx(trx, PROJECT)

    def test_infrastructure_exit_cannot_be_recognized_as_test_failure(self):
        with self.assertRaisesRegex(RatchetError, "INVALID_INVOCATION"):
            self.capture("candidate", self.candidate, exit_code=2)


if __name__ == "__main__":
    main()
