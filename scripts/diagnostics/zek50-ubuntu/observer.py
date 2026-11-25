#!/usr/bin/env python3
"""Temporary native observer. Raw output never reaches disk or the caller."""
from __future__ import annotations
import datetime
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys

SOURCE = "e87f5e125566e3a03874aef601feb0da82861c06"
PROJECTS = (
    "Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
    "Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
    "Services/AdminAreaManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj",
    "Services/ClientManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj",
    "Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
)
EXCEPTIONS = (
    "Npgsql.PostgresException", "Npgsql.NpgsqlException", "Docker.DotNet.DockerApiException",
    "Docker.DotNet.DockerContainerNotFoundException",
    "DotNet.Testcontainers.Containers.ContainerNotRunningException",
    "DotNet.Testcontainers.Containers.ContainerNotFoundException",
    "System.TimeoutException", "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException",
    "System.InvalidOperationException", "System.IO.IOException", "System.IO.FileNotFoundException",
    "System.IO.DirectoryNotFoundException", "System.UnauthorizedAccessException", "System.OutOfMemoryException",
    "System.TypeInitializationException", "System.TypeLoadException", "System.BadImageFormatException",
    "System.DllNotFoundException", "System.Net.Sockets.SocketException", "System.Net.Http.HttpRequestException",
    "System.AccessViolationException", "System.Runtime.InteropServices.SEHException",
)
TYPE_PATTERNS = tuple((name, re.compile(r"(?<![\w.])" + re.escape(name) + r"(?![\w.])")) for name in EXCEPTIONS)
COUNTERS = re.compile(r"(?:Passed!|Failed!)\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+)")
STATE = re.compile(r"\b(?:SQLSTATE|SqlState)\s*[:=]\s*([0-9A-Z]{5})\b")
PG_STATE = re.compile(r"\bNpgsql\.PostgresException(?:\s*\(0x[0-9A-Fa-f]{8}\))?\s*:\s*([0-9A-Z]{5}):")
COMPILER = re.compile(r"\b(?:error|warning)\s+(CS[0-9]{4}|MSB[0-9]{4}|NU[0-9]{4})\b")

def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()

def classify(line):
    """Emit closed categories only. No fragment of the supplied line escapes."""
    result = []
    if "ZEKA_DIAG_MANAGED_OBSERVER_FAILED" in line:
        result.append({"kind": "observer-error", "category": "MANAGED_OBSERVER_FAILED"})
    for marker, phase in (
        ("Starting test execution, please wait", "VSTEST_EXECUTION_REQUESTED"),
        ("Test run for ", "TEST_RUN_ANNOUNCED"), ("Build succeeded.", "BUILD_SUCCEEDED"),
        ("Build FAILED.", "BUILD_FAILED"),
    ):
        if marker in line:
            result.append({"kind": "phase", "phase": phase})
    for category, pattern in TYPE_PATTERNS:
        if pattern.search(line):
            match = STATE.search(line) or PG_STATE.search(line)
            result.append({"kind": "exception-observation", "category": category,
                           "sqlState": match.group(1) if match else None,
                           "terminalCauseEstablished": False})
    for match in COMPILER.finditer(line):
        result.append({"kind": "build-code", "code": match.group(1)})
    if "Microsoft.EntityFrameworkCore.Migrations." in line:
        result.append({"kind": "phase", "phase": "MIGRATION_EXECUTION_FRAME_OBSERVED"})
    if "Infrastructure.IntegrationTests.PermissionMigrationTests" in line:
        phase = "PERMISSION_TEST_FRAME_OBSERVED"
        if "InitializeAsync" in line:
            phase = "PERMISSION_FIXTURE_STARTUP_FRAME_OBSERVED"
        elif "DisposeAsync" in line:
            phase = "PERMISSION_FIXTURE_DISPOSAL_FRAME_OBSERVED"
        result.append({"kind": "phase", "phase": phase})
    for match in COUNTERS.finditer(line):
        result.append({"kind": "console-counts", "failed": int(match.group(1)), "passed": int(match.group(2)),
                       "skipped": int(match.group(3)), "total": int(match.group(4)),
                       "authority": "CONSOLE_REPORTED_ONLY"})
    return result

def signal_number(native):
    return -native if native is not None and native < 0 else None

def main():
    real = Path(os.environ["ZEKA_DIAG_REAL_DOTNET"]).resolve(strict=True)
    if real == Path(__file__).resolve() or not real.is_file():
        raise ValueError("REAL_DOTNET_INVALID")
    args = sys.argv[1:]
    if not args or args[0] != "test":
        os.execv(str(real), [str(real), *args])
    if len(args) < 2 or args[1] not in PROJECTS or "--filter" in args or "--no-build" in args:
        raise ValueError("PROFILE_COMMAND_NOT_ADMITTED")
    slot = PROJECTS.index(args[1]) + 1
    for key, value in {"--configuration": "Release", "--logger": "trx;LogFilePrefix=ratchet",
                       "--blame-hang-timeout": "15m", "--blame-hang-dump-type": "none"}.items():
        if args.count(key) != 1 or args[args.index(key) + 1] != value:
            raise ValueError("PROFILE_ARGUMENT_DRIFT")
    if "--no-restore" not in args or "--blame" not in args or args.count("--results-directory") != 1:
        raise ValueError("PROFILE_ARGUMENT_DRIFT")
    results = Path(args[args.index("--results-directory") + 1]).resolve()
    raw = Path(os.environ["RATCHET_RAW"]).resolve(strict=True)
    if results != raw / f"result-{slot}" or not results.is_dir():
        raise ValueError("RESULTS_BOUNDARY_DRIFT")
    safe = Path(os.environ["ZEKA_DIAG_SAFE_DIR"]).resolve(strict=True)
    if not safe.is_dir() or safe.is_symlink():
        raise ValueError("SAFE_DIRECTORY_INVALID")
    observer_failed = False
    first_exception = None
    phase = "BEFORE_PROCESS_LAUNCH"
    started = utc()
    sequence = 0
    child = None
    native = None
    log = (safe / f"native-{slot}.jsonl").open("x", encoding="utf-8")

    def emit(item):
        nonlocal sequence
        sequence += 1
        row = {"schema": 1, "sourceSha": SOURCE, "invocation": slot, "sequence": sequence,
               "observedAtUtc": utc(), **item}
        log.write(json.dumps(row, separators=(",", ":")) + "\n")
        log.flush()
        os.fsync(log.fileno())

    def observe(line):
        nonlocal first_exception, phase, observer_failed
        for item in classify(line):
            if item["kind"] == "exception-observation" and first_exception is None:
                first_exception = {"category": item["category"], "sqlState": item["sqlState"]}
            if item["kind"] == "phase":
                phase = item["phase"]
            if item["kind"] == "observer-error":
                observer_failed = True
            emit(item)

    try:
        emit({"kind": "phase", "phase": phase, "trxProduced": False})
        env = dict(os.environ)
        env["ZEKA_DIAG_INVOCATION"] = str(slot)
        hook = env.get("ZEKA_DIAG_HOOK")
        if hook:
            hook_path = Path(hook).resolve(strict=True)
            if not hook_path.is_file() or env.get("DOTNET_STARTUP_HOOKS"):
                raise ValueError("HOOK_BOUNDARY_DRIFT")
            env["DOTNET_STARTUP_HOOKS"] = str(hook_path)
        child = subprocess.Popen([str(real), *args], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, env=env)
        phase = "PROCESS_LAUNCHED"
        emit({"kind": "phase", "phase": phase, "pid": child.pid})
        pending = b""
        discarded = False
        assert child.stdout is not None
        while chunk := child.stdout.read1(4096):
            pending += chunk
            while b"\n" in pending:
                line, pending = pending.split(b"\n", 1)
                if not discarded:
                    observe(line[:16384].decode("utf-8", errors="replace"))
                discarded = False
            if len(pending) > 16384:
                if not discarded:
                    observe(pending[:16384].decode("utf-8", errors="replace"))
                pending = b""
                discarded = True
                emit({"kind": "output-bound", "category": "OVERSIZED_LINE_DISCARDED"})
        if pending and not discarded:
            observe(pending.decode("utf-8", errors="replace"))
        native = child.wait()
    except BaseException:
        observer_failed = True
        try:
            emit({"kind": "observer-error", "category": "NATIVE_OBSERVER_FAILED"})
        except BaseException:
            pass
        if child is not None:
            try:
                if child.stdout is not None:
                    while child.stdout.read1(4096):
                        pass
            except BaseException:
                pass
            native = child.wait()
    finally:
        trx = [p for p in results.glob("*.trx") if p.is_file() and not p.is_symlink() and p.stat().st_size]
        final = {"kind": "native-outcome", "startedAtUtc": started, "completedAtUtc": utc(),
                 "nativeExit": native, "signal": signal_number(native), "observedPhase": phase,
                 "firstConsoleException": first_exception, "trxProduced": bool(trx), "trxFiles": len(trx),
                 "observerStatus": "FAIL" if observer_failed else "PASS", "causeEstablished": False}
        try:
            emit(final)
        finally:
            log.close()
        print(json.dumps({"invocation": slot, **final}, separators=(",", ":")), flush=True)
    if native is None:
        return 250
    if native < 0:
        number = -native
        if number not in (signal.SIGKILL, signal.SIGSTOP):
            signal.signal(number, signal.SIG_DFL)
        os.kill(os.getpid(), number)
        return 128 + number
    return native

if __name__ == "__main__":
    try:
        code = main()
    except BaseException:
        print('{"kind":"observer-error","category":"OBSERVER_ADMISSION_FAILED","testLaunched":false}', flush=True)
        code = 250
    raise SystemExit(code)
