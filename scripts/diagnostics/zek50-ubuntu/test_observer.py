import contextlib
import io
import os
import sys
import tempfile
from types import SimpleNamespace
from unittest import mock
import importlib.util
import json
from pathlib import Path
import unittest
import uuid

spec = importlib.util.spec_from_file_location("temporary_observer", Path(__file__).with_name("observer.py"))
observer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(observer)

class ObserverTests(unittest.TestCase):
    def test_stream_redaction_retains_only_safe_categories(self):
        canary = uuid.uuid4().hex
        line = ("Npgsql.PostgresException : 42501: amqp://user:" + canary + "@host "
                "Password=" + canary + "; Pwd=" + canary + "; Bearer " + canary)
        value = observer.classify(line)
        self.assertEqual("42501", value[0]["sqlState"])
        self.assertNotIn(canary, json.dumps(value))
        self.assertNotIn("amqp", json.dumps(value))

    def test_hresult_postgres_state_keeps_no_detail_or_credentials(self):
        canary = uuid.uuid4().hex
        line = ("Npgsql.PostgresException (0x80004005): 42501: denied DETAIL: " + canary
                + " Host=private.invalid; Password=" + canary + "; Bearer " + canary)
        value = observer.classify(line)
        self.assertEqual([{"kind": "exception-observation", "category": "Npgsql.PostgresException",
                           "sqlState": "42501", "terminalCauseEstablished": False}], value)
        exported = json.dumps(value)
        self.assertNotIn(canary, exported)
        self.assertNotIn("DETAIL", exported)
        self.assertNotIn("private.invalid", exported)
        self.assertNotIn("Password", exported)

    def test_phase_observation_does_not_assert_cause(self):
        value = observer.classify("at Infrastructure.IntegrationTests.PermissionMigrationTests.InitializeAsync()")
        self.assertEqual([{"kind": "phase", "phase": "PERMISSION_FIXTURE_STARTUP_FRAME_OBSERVED"}], value)

    def test_native_signal_is_separate_from_exit_status(self):
        self.assertEqual(9, observer.signal_number(-9))
        self.assertIsNone(observer.signal_number(137))
        self.assertIsNone(observer.signal_number(None))

    def test_observer_error_is_not_a_test_exception(self):
        value = observer.classify("ZEKA_DIAG_MANAGED_OBSERVER_FAILED")
        self.assertEqual([{"kind": "observer-error", "category": "MANAGED_OBSERVER_FAILED"}], value)

    def test_private_messages_and_key_material_are_not_exported(self):
        canary = uuid.uuid4().hex
        self.assertEqual([], observer.classify("-----BEGIN PRIVATE KEY----- " + canary))
        self.assertEqual([], observer.classify("connection=" + canary + "; api_key=" + canary))

    def test_console_counts_are_not_declared_authoritative(self):
        value = observer.classify("Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1")
        self.assertEqual("CONSOLE_REPORTED_ONLY", value[0]["authority"])

    def _run_synthetic(self, native, output=b"", launch_error=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            real = root / "fake-dotnet"
            real.write_text("synthetic executable metadata fixture")
            raw = root / "raw"
            safe = root / "safe"
            (raw / "result-1").mkdir(parents=True)
            safe.mkdir()
            fake = SimpleNamespace(stdout=io.BytesIO(output), pid=1234, wait=lambda: native)

            def launch(*args, **kwargs):
                rows = [json.loads(line) for line in (safe / "native-1.jsonl").read_text().splitlines()]
                self.assertEqual("BEFORE_PROCESS_LAUNCH", rows[-1]["phase"])
                if launch_error:
                    raise OSError("private-" + uuid.uuid4().hex)
                return fake

            args = ["observer", "test", observer.PROJECTS[0], "--configuration", "Release", "--no-restore",
                    "--logger", "trx;LogFilePrefix=ratchet", "--results-directory", str(raw / "result-1"),
                    "--blame", "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "none"]
            environment = {"ZEKA_DIAG_REAL_DOTNET": str(real),
                           "ZEKA_DIAG_SAFE_DIR": str(safe), "RATCHET_RAW": str(raw)}
            with mock.patch.object(observer.sys, "argv", args), \
                 mock.patch.dict(observer.os.environ, environment, clear=True), \
                 mock.patch.object(observer.subprocess, "Popen", side_effect=launch), \
                 mock.patch.object(observer.os, "kill"), \
                 mock.patch.object(observer.signal, "SIGKILL", 9, create=True), \
                 mock.patch.object(observer.signal, "SIGSTOP", 19, create=True), \
                 mock.patch.object(observer.signal, "signal"), \
                 contextlib.redirect_stdout(io.StringIO()):
                result = observer.main()
            rows = [json.loads(line) for line in (safe / "native-1.jsonl").read_text().splitlines()]
            return result, rows

    def test_native_failure_before_trx_keeps_independent_outcome(self):
        canary = uuid.uuid4().hex.encode()
        native, rows = self._run_synthetic(7, b"System.IO.FileNotFoundException: " + canary + b"\n")
        self.assertEqual(7, native)
        final = rows[-1]
        self.assertEqual(7, final["nativeExit"])
        self.assertFalse(final["trxProduced"])
        self.assertEqual("PASS", final["observerStatus"])
        self.assertEqual("System.IO.FileNotFoundException", final["firstConsoleException"]["category"])
        self.assertNotIn(canary.decode(), json.dumps(rows))

    def test_native_signal_before_trx_is_not_relabelled_exit_137(self):
        native, rows = self._run_synthetic(-9)
        self.assertEqual(137, native)
        self.assertEqual(-9, rows[-1]["nativeExit"])
        self.assertEqual(9, rows[-1]["signal"])
        self.assertFalse(rows[-1]["trxProduced"])

    def test_observer_launch_failure_is_separate_from_host_failure(self):
        native, rows = self._run_synthetic(None, launch_error=True)
        self.assertEqual(250, native)
        self.assertIsNone(rows[-1]["nativeExit"])
        self.assertEqual("FAIL", rows[-1]["observerStatus"])
        self.assertFalse(rows[-1]["trxProduced"])

if __name__ == "__main__":
    unittest.main()
