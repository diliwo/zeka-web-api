#!/usr/bin/env python3
"""Narrow, non-gating evidence support for the one ZEK-50 Ubuntu profile."""
import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

SOURCE = "e87f5e125566e3a03874aef601feb0da82861c06"
TREE = "9be3e43825809230fc4e6920e4226bf80e68742f"
TOOLING = "b119bb4e45d95b26959b91fac201dbc049b82c20"
BASE_TREE = "90c57581c40653d17c53d6a2aa7d644be361e246"
BUNDLE = "11f7c8c9373e562316db419d92f91b479a6f896f859922c9fd71f112e8652f96"
PROJECTS = (
    "Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
    "Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
    "Services/AdminAreaManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj",
    "Services/ClientManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj",
    "Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj",
)
POLICY = (
    ".github/workflows/c1-validation.yml", ".github/workflows/mvp-validation.yml",
    "Deployments/database/production-conformance.targets.json", "Directory.Build.props", "global.json",
    "scripts/ci/c1-ratchet/baseline.schema.json", "scripts/ci/c1-ratchet/baseline-v1.json",
    "scripts/ci/c1_ratchet.py", "scripts/ci/c1_ratchet_context.py", "scripts/ci/c1_ratchet_jobs.py",
    "scripts/ci/c1_ratchet_trx.py", "scripts/ci/nuget.strict.config", "scripts/ci/run-c1-ratchet.sh",
    "scripts/ci/run-production-conformance.sh", "scripts/ci/tests/test_c1_ratchet.py",
    "scripts/ci/tests/test_c1_ratchet_context.py", "scripts/ci/tests/c1-ratchet/baseline.trx",
    "scripts/ci/tests/c1-ratchet/candidate.trx", "scripts/security/check-nuget-credentials.sh",
)
TARGET_CLASS = "Infrastructure.IntegrationTests.PermissionMigrationTests"
TARGET_METHOD = "Legacy_member_never_receives_an_inferred_role_and_reviewed_mapping_preserves_membership"
EXCEPTIONS = (
    "Npgsql.PostgresException", "Npgsql.NpgsqlException", "Docker.DotNet.DockerApiException",
    "Docker.DotNet.DockerContainerNotFoundException",
    "DotNet.Testcontainers.Containers.ContainerNotRunningException",
    "DotNet.Testcontainers.Containers.ContainerNotFoundException", "System.TimeoutException",
    "System.OperationCanceledException", "System.Threading.Tasks.TaskCanceledException",
    "System.InvalidOperationException", "System.IO.IOException", "System.IO.FileNotFoundException",
    "System.IO.DirectoryNotFoundException", "System.UnauthorizedAccessException", "System.OutOfMemoryException",
    "System.TypeInitializationException", "System.TypeLoadException", "System.BadImageFormatException",
    "System.DllNotFoundException", "System.Net.Sockets.SocketException", "System.Net.Http.HttpRequestException",
    "System.AccessViolationException", "System.Runtime.InteropServices.SEHException",
)
PHASES = {"BEFORE_PROCESS_LAUNCH", "PROCESS_LAUNCHED", "VSTEST_EXECUTION_REQUESTED", "TEST_RUN_ANNOUNCED",
          "BUILD_SUCCEEDED", "BUILD_FAILED", "TEST_RUN_SUCCEEDED", "TEST_RUN_FAILED",
          "MIGRATION_EXECUTION_FRAME_OBSERVED", "PERMISSION_TEST_FRAME_OBSERVED",
          "PERMISSION_FIXTURE_STARTUP_FRAME_OBSERVED", "PERMISSION_FIXTURE_DISPOSAL_FRAME_OBSERVED"}
MANAGED_PHASES = {"CLR_STARTED", "TEST_HOST_ASSEMBLY_OBSERVED", "INFRASTRUCTURE_TEST_ASSEMBLY_OBSERVED",
                  "MIGRATION_BOOTSTRAP_OBSERVED", "UNHANDLED_EXCEPTION", "MANAGED_PROCESS_EXIT"}
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
OUTCOMES = {"Passed", "Failed", "NotExecuted", "Inconclusive", "Timeout", "Aborted", "Error", "Warning", "NotRunnable"}
GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")


SAFE_REFUSALS = frozenset((
    'ADMISSION_OUTPUT_LIMIT',
    'DIAGNOSTIC_SOURCE_MISSING',
    'DUPLICATE_JSON_KEY',
    'EVENT_NOT_ADMITTED',
    'HOSTED_IMAGE_ADMISSION_REQUIRED',
    'IMAGE_DIGEST_MISSING',
    'IMAGE_ID_MISSING',
    'INVOCATION_NATIVE_INVALID',
    'INVOCATION_ORDER_OR_PATH_INVALID',
    'JSON_INPUT_MISSING_OR_OVERSIZED',
    'LOCK_BYTES_CHANGED',
    'LOCK_INVENTORY_DRIFT',
    'LOCK_NOT_REGULAR',
    'LOCK_SOURCE_DRIFT',
    'MANAGED_BINDING_INVALID',
    'MANAGED_CATEGORY_INVALID',
    'MANAGED_ERROR_INVALID',
    'MANAGED_FILE_INVALID',
    'MANAGED_PHASE_INVALID',
    'MULTIPLE_TRX_FILES',
    'NATIVE_ADMISSION_COMMAND_FAILED',
    'NATIVE_OBSERVER_NOT_COMPLETE',
    'NATIVE_SUCCESS_TRX_DISAGREEMENT',
    'OBSERVED_NATIVE_DISAGREEMENT',
    'OBSERVED_NATIVE_MISSING',
    'OBSERVER_BINDING_INVALID',
    'OBSERVER_CATEGORY_INVALID',
    'OBSERVER_ENUM_INVALID',
    'OBSERVER_FILE_INVALID',
    'OBSERVER_NATIVE_INVALID',
    'OBSERVER_NATIVE_OUTCOME_DUPLICATED',
    'OBSERVER_PHASE_INVALID',
    'OBSERVER_SIGNAL_INVALID',
    'OUTPUT_TEMP_EXISTS',
    'PG_BINARY_VERSION_MISMATCH',
    'PG_TAG_NOT_ALLOWED',
    'POLICY_ENTRY_INVALID',
    'PROFILE_BINDING_INVALID',
    'PROFILE_INVOCATION_LIMIT',
    'PROTECTED_BUNDLE_DRIFT',
    'REF_NOT_ADMITTED',
    'REVISION_DRIFT',
    'SDK_VERSION_MISMATCH',
    'SYMLINK_DIRECTORY_REFUSED',
    'SYMLINK_OUTPUT_REFUSED',
    'TRACKED_SOURCE_DRIFT',
    'TREE_DRIFT',
    'TRX_COUNTERS_MISSING',
    'TRX_COUNTER_INVALID',
    'TRX_COUNTER_MISMATCH',
    'TRX_DEFINITION_EXECUTION_MISMATCH',
    'TRX_DIRECTORY_SYMLINK',
    'TRX_DTD_REFUSED',
    'TRX_DUPLICATE_DEFINITION',
    'TRX_DUPLICATE_ENTRY',
    'TRX_EXECUTED_MISMATCH',
    'TRX_FILE_INVALID',
    'TRX_ID_INVALID',
    'TRX_INVENTORY_LINK_COUNT_MISMATCH',
    'TRX_LINK_INVALID',
    'TRX_METHOD_MISSING',
    'TRX_ROWS_MISSING',
    'TRX_TEST_LIST_MISMATCH',
    'TRX_UNKNOWN_OUTCOME',
    'UBUNTU_VERSION_MISMATCH',
))

class DiagnosticRefusal(ValueError):
    pass

def safe_refusal(error):
    if isinstance(error, DiagnosticRefusal) and len(error.args) == 1 and type(error.args[0]) is str and error.args[0] in SAFE_REFUSALS:
        return error.args[0]
    return "ADMISSION_OR_COLLECTION_FAILED"

def require(condition, code):
    if not condition:
        raise DiagnosticRefusal(code)


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def command(args, timeout=60, cwd=None):
    value = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout, check=False, cwd=cwd)
    require(value.returncode == 0, "NATIVE_ADMISSION_COMMAND_FAILED")
    require(len(value.stdout) <= 16 * 1024 * 1024, "ADMISSION_OUTPUT_LIMIT")
    return value.stdout


def git(repo, *args):
    return command(("git", "--no-replace-objects", "-C", str(repo), *args))


def directory(value):
    path = Path(value).absolute()
    require(not any(p.is_symlink() for p in (path, *path.parents)), "SYMLINK_DIRECTORY_REFUSED")
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    return path


def write(path, value):
    path = Path(path)
    require(not path.is_symlink(), "SYMLINK_OUTPUT_REFUSED")
    temporary = path.with_suffix(path.suffix + ".tmp")
    require(not temporary.exists() and not temporary.is_symlink(), "OUTPUT_TEMP_EXISTS")
    with temporary.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def unique(items):
    result = dict(items)
    require(len(result) == len(items), "DUPLICATE_JSON_KEY")
    return result


def load(path):
    path = Path(path)
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 16 * 1024 * 1024,
            "JSON_INPUT_MISSING_OR_OVERSIZED")
    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)


def policy(repo):
    digest = hashlib.sha256()
    for path in sorted(POLICY):
        row = git(repo, "ls-tree", "HEAD", "--", path).rstrip(b"\r\n")
        metadata, actual = row.split(b"\t")
        mode, kind, _ = metadata.split(b" ")
        require(actual == path.encode() and mode in (b"100644", b"100755") and kind == b"blob", "POLICY_ENTRY_INVALID")
        body = git(repo, "cat-file", "blob", "HEAD:" + path)
        digest.update(f"{path}\t{mode.decode()}\t{hashlib.sha256(body).hexdigest()}\n".encode())
    return digest.hexdigest()


def source_identity(source, tooling):
    for repo, revision, tree in ((source, SOURCE, TREE), (tooling, TOOLING, BASE_TREE)):
        require(git(repo, "rev-parse", "HEAD").strip().decode() == revision, "REVISION_DRIFT")
        require(git(repo, "rev-parse", "HEAD^{tree}").strip().decode() == tree, "TREE_DRIFT")
        require(not git(repo, "status", "--porcelain=v1", "--untracked-files=all").strip(), "TRACKED_SOURCE_DRIFT")
        require(policy(repo) == BUNDLE, "PROTECTED_BUNDLE_DRIFT")


def locks(source):
    names = git(source, "ls-files", "--", "**/packages*.lock.json").decode().splitlines()
    require(len(names) == len(set(names)) == 42, "LOCK_INVENTORY_DRIFT")
    result = {}
    for name in names:
        path = source / name
        require(path.is_file() and not path.is_symlink(), "LOCK_NOT_REGULAR")
        body = path.read_bytes()
        require(body == git(source, "cat-file", "blob", "HEAD:" + name), "LOCK_SOURCE_DRIFT")
        result[name] = hashlib.sha256(body).hexdigest()
    return result


def admit(args):
    raw, safe = directory(args.raw), directory(args.safe)
    source, tooling = Path(args.source), Path(args.tooling)
    require(os.environ.get("GITHUB_REPOSITORY") == "diliwo/zeka-web-api" and
            os.environ.get("GITHUB_EVENT_NAME") == "push" and os.environ.get("GITHUB_RUN_ATTEMPT") == "1",
            "EVENT_NOT_ADMITTED")
    require(os.environ.get("GITHUB_REF") == "refs/heads/codex/zek50-ubuntu-profile-diagnostic-20261125-v2", "REF_NOT_ADMITTED")
    diagnostic = os.environ.get("GITHUB_SHA", "")
    require(re.fullmatch(r"[0-9a-f]{40}", diagnostic), "DIAGNOSTIC_SOURCE_MISSING")
    source_identity(source, tooling)
    os_values = {}
    for line in Path("/etc/os-release").read_text().splitlines():
        key, _, value = line.partition("=")
        if key in ("ID", "VERSION_ID"):
            os_values[key] = value.strip('"')
    require(platform.system() == "Linux" and os_values == {"ID": "ubuntu", "VERSION_ID": "24.04"}, "UBUNTU_VERSION_MISMATCH")
    sdk = command(("dotnet", "--version"), cwd=source).decode().strip()
    require(sdk == "8.0.129", "SDK_VERSION_MISMATCH")
    before = locks(source)
    write(raw / "locks-before.json", before)
    write(raw / "context.json", {"tooling": TOOLING, "base": TOOLING, "baseTree": BASE_TREE,
                                 "candidate": SOURCE, "testTree": TREE})
    write(safe / "admission.json", {
        "schema": 1, "mode": "STANDALONE_PROFILE_DIAGNOSTIC_NOT_C1_GATE", "sourceSha": SOURCE, "sourceTree": TREE,
        "toolingSha": TOOLING, "diagnosticSha": diagnostic, "originalRun": 45624605180, "originalJob": 139241519519,
        "runId": int(os.environ["GITHUB_RUN_ID"]), "attempt": 1, "observedAtUtc": utc(),
        "observedOs": {"id": "ubuntu", "version": "24.04", "architecture": platform.machine(), "processorCount": os.cpu_count()},
        "observedSdk": sdk, "protectedBundleUnchanged": True, "sourceLockCount": 42,
        "profile": "postgresql", "projects": list(PROJECTS), "projectOrderPreserved": True,
        "buildConfiguration": "Release", "prebuild": False, "filter": None, "hangTimeout": "15m",
        "temporaryObserverChangesRuntimeTiming": True,
        "fidelityDifferences": ["Separate non-required diagnostic workflow; original protected workflows unchanged.",
                                "PostgreSQL 17.11 images are admitted/preloaded before execution, warming image caches.",
                                "Native-output observer and managed startup hook add disclosed diagnostic overhead."]})


def image(args):
    require(args.tag in {"postgres:17", "postgres:17-alpine", "postgres:17.11", "postgres:17.11-alpine"}, "PG_TAG_NOT_ALLOWED")
    safe = directory(args.safe)
    require(os.environ.get("GITHUB_ACTIONS") == "true", "HOSTED_IMAGE_ADMISSION_REQUIRED")
    value = json.loads(command(("docker", "image", "inspect", "--format",
                               '{"id":{{json .Id}},"digests":{{json .RepoDigests}}}', args.tag)))
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", value.get("id", "")), "IMAGE_ID_MISSING")
    digests = value.get("digests")
    require(isinstance(digests, list) and digests and all(re.fullmatch(r"postgres@sha256:[0-9a-f]{64}", d) for d in digests),
            "IMAGE_DIGEST_MISSING")
    output = command(("docker", "run", "--rm", "--network", "none", "--entrypoint", "postgres", args.tag, "--version"), 60)
    require(re.fullmatch(rb"postgres \(PostgreSQL\) 17\.11(?: \([^\r\n]{1,128}\))?\r?\n?", output), "PG_BINARY_VERSION_MISMATCH")
    write(safe / ("image-" + args.tag.replace(":", "-") + ".json"), {
        "schema": 1, "tag": args.tag, "imageId": value["id"], "repoDigests": digests,
        "observedBinaryVersion": "17.11", "binaryProbeOnly": True, "serverVersionQueried": False,
        "profileContainerImageIdsObserved": False})


def safe_error(message, stack):
    matches = [(message.find(name), name) for name in EXCEPTIONS if name in message]
    category = min(matches)[1] if matches else None
    match = re.search(r"\b(?:SQLSTATE|SqlState)\s*[:=]\s*([0-9A-Z]{5})\b", message)
    if not match:
        match = re.search(r"\bNpgsql\.PostgresException(?:\s+\(0x[0-9a-fA-F]{1,16}\))?\s*:\s*([0-9A-Z]{5}):", message)
    phase = "NOT_RECOVERED"
    if TARGET_CLASS in stack:
        if "InitializeAsync" in stack:
            phase = "PERMISSION_FIXTURE_STARTUP_FRAME_OBSERVED"
        elif "DisposeAsync" in stack:
            phase = "PERMISSION_FIXTURE_DISPOSAL_FRAME_OBSERVED"
        elif TARGET_METHOD in stack:
            phase = "PERMISSION_TEST_FRAME_OBSERVED"
    return {"category": category, "sqlState": match.group(1) if match else None,
            "phase": phase, "categoryBasis": "TRX_MESSAGE_ALLOWLIST_MATCH", "causeEstablished": False}


def trx(path, native):
    require(path.is_file() and not path.is_symlink() and 0 < path.stat().st_size <= 64 * 1024 * 1024, "TRX_FILE_INVALID")
    body = path.read_bytes()
    require(b"<!DOCTYPE" not in body.upper() and b"<!ENTITY" not in body.upper(), "TRX_DTD_REFUSED")
    root = ET.fromstring(body)
    definitions = {}
    for item in root.findall("t:TestDefinitions/t:UnitTest", NS):
        key = item.get("id")
        require(key and key not in definitions, "TRX_DUPLICATE_DEFINITION")
        definitions[key] = item
    entries = {}
    for item in root.findall("t:TestEntries/t:TestEntry", NS):
        key = (item.get("testId"), item.get("executionId"))
        require(key not in entries, "TRX_DUPLICATE_ENTRY")
        entries[key] = item
    rows = root.findall("t:Results/t:UnitTestResult", NS)
    require(rows, "TRX_ROWS_MISSING")
    counts = {name: 0 for name in OUTCOMES}
    seen, executions, failed, target = set(), set(), [], []
    for row in rows:
        test_id, execution = row.get("testId"), row.get("executionId")
        key = (test_id, execution)
        require(test_id and execution and GUID.fullmatch(test_id) and GUID.fullmatch(execution), "TRX_ID_INVALID")
        require(key not in seen and execution not in executions and key in entries and test_id in definitions, "TRX_LINK_INVALID")
        seen.add(key)
        executions.add(execution)
        definition = definitions[test_id]
        declared = definition.find("t:Execution", NS)
        require(declared is not None and declared.get("id") == execution, "TRX_DEFINITION_EXECUTION_MISMATCH")
        require(not row.get("testListId") or row.get("testListId") == entries[key].get("testListId"),
                "TRX_TEST_LIST_MISMATCH")
        method = definition.find("t:TestMethod", NS)
        require(method is not None, "TRX_METHOD_MISSING")
        outcome = row.get("outcome")
        require(outcome in OUTCOMES, "TRX_UNKNOWN_OUTCOME")
        counts[outcome] += 1
        record = {"testId": test_id, "executionId": execution, "outcome": outcome, "linksVerified": True}
        if outcome == "Failed":
            message = row.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS)
            stack = row.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS)
            record.update(safe_error(message, stack))
            failed.append(record)
        if method.get("className", "").split(",", 1)[0] == TARGET_CLASS and method.get("name") == TARGET_METHOD:
            target.append(dict(record, test=TARGET_CLASS + "." + TARGET_METHOD))
    require(len(entries) == len(definitions) == len(rows), "TRX_INVENTORY_LINK_COUNT_MISMATCH")
    counters = root.find("t:ResultSummary/t:Counters", NS)
    require(counters is not None, "TRX_COUNTERS_MISSING")
    def number(name):
        value = counters.get(name)
        require(value is not None and re.fullmatch(r"[0-9]{1,7}", value), "TRX_COUNTER_INVALID")
        return int(value)
    require(number("total") == len(rows) and number("passed") == counts["Passed"] and
            number("failed") == counts["Failed"] and number("notExecuted") == counts["NotExecuted"], "TRX_COUNTER_MISMATCH")
    require(number("executed") == len(rows) - counts["NotExecuted"], "TRX_EXECUTED_MISMATCH")
    if native == 0:
        require(counts["Passed"] == len(rows) and number("executed") == number("total"), "NATIVE_SUCCESS_TRX_DISAGREEMENT")
    return {"present": True, "valid": True, "total": len(rows), "passed": counts["Passed"],
            "failed": counts["Failed"], "skipped": counts["NotExecuted"], "outcomes": counts,
            "nativeTrxAgreement": native is not None and ((native == 0 and counts["Failed"] == 0) or (native != 0 and counts["Failed"] > 0)),
            "failedRows": failed, "targetPermissionTest": target}


def native_observation(safe, slot):
    path = safe / f"native-{slot}.jsonl"
    result = {"recorded": False, "processLaunched": False, "nativeExit": None, "signal": None, "observedPhase": "NOT_RECOVERED",
              "firstConsoleException": None, "observerStatus": "NOT_AVAILABLE", "causeEstablished": False}
    if not path.exists():
        return result
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 16 * 1024 * 1024, "OBSERVER_FILE_INVALID")
    outcomes, observed_phases = [], []
    for line in path.read_text(encoding="utf-8").splitlines():
        value = json.loads(line, object_pairs_hook=unique)
        require(value.get("schema") == 1 and value.get("sourceSha") == SOURCE and value.get("invocation") == slot, "OBSERVER_BINDING_INVALID")
        if value.get("kind") == "phase":
            require(value.get("phase") in PHASES, "OBSERVER_PHASE_INVALID")
            observed_phases.append(value["phase"])
        if value.get("kind") == "native-outcome":
            outcomes.append(value)
    require(len(outcomes) <= 1, "OBSERVER_NATIVE_OUTCOME_DUPLICATED")
    if not outcomes:
        result.update(recorded=True, processLaunched="PROCESS_LAUNCHED" in observed_phases,
                      observedPhase=observed_phases[-1] if observed_phases else "NOT_RECOVERED",
                      observerStatus="INCOMPLETE")
        return result
    value = outcomes[0]
    require(value.get("observedPhase") in PHASES and value.get("observerStatus") in {"PASS", "FAIL"}, "OBSERVER_ENUM_INVALID")
    native, signal = value.get("nativeExit"), value.get("signal")
    require(native is None or type(native) is int, "OBSERVER_NATIVE_INVALID")
    require(signal is None or (type(signal) is int and 0 < signal < 128), "OBSERVER_SIGNAL_INVALID")
    first = value.get("firstConsoleException")
    if first is not None:
        require(first.get("category") in EXCEPTIONS and (first.get("sqlState") is None or
                re.fullmatch(r"[0-9A-Z]{5}", first["sqlState"])), "OBSERVER_CATEGORY_INVALID")
        first = {"category": first["category"], "sqlState": first.get("sqlState"), "terminalCauseEstablished": False}
    result.update(recorded=True, processLaunched="PROCESS_LAUNCHED" in observed_phases,
                  nativeExit=native, signal=signal, observedPhase=value["observedPhase"],
                  firstConsoleException=first, observerStatus=value["observerStatus"],
                  trxProduced=value.get("trxProduced") is True)
    return result


def managed_observation(safe, slot):
    phases, unhandled, process_ids = set(), [], set()
    for path in sorted(safe.glob(f"managed-{slot}-*.jsonl")):
        require(re.fullmatch(rf"managed-{slot}-[0-9]+\.jsonl", path.name) and path.is_file() and
                not path.is_symlink() and path.stat().st_size <= 16 * 1024 * 1024, "MANAGED_FILE_INVALID")
        for line in path.read_text(encoding="utf-8").splitlines():
            value = json.loads(line, object_pairs_hook=unique)
            require(value.get("schema") == 1 and value.get("sourceSha") == SOURCE and
                    value.get("invocation") == slot and type(value.get("pid")) is int,
                    "MANAGED_BINDING_INVALID")
            phase = value.get("phase")
            require(phase in MANAGED_PHASES, "MANAGED_PHASE_INVALID")
            phases.add(phase)
            process_ids.add(value["pid"])
            if phase == "UNHANDLED_EXCEPTION":
                observation = value.get("observation")
                require(observation is None or isinstance(observation, list), "MANAGED_ERROR_INVALID")
                for error in (observation or [])[:8]:
                    category, sql_state = error.get("category"), error.get("sqlState")
                    require(category in (*EXCEPTIONS, "OTHER_EXCEPTION") and (sql_state is None or
                            re.fullmatch(r"[0-9A-Z]{5}", sql_state)), "MANAGED_CATEGORY_INVALID")
                    unhandled.append({"category": category, "sqlState": sql_state,
                                      "basis": "MANAGED_UNHANDLED_EXCEPTION_EVENT"})
    return {"processesObserved": len(process_ids), "phases": sorted(phases), "unhandled": unhandled,
            "scope": "PROCESS_NOT_INDIVIDUAL_TEST", "causeEstablished": False}


def finalize(args):
    raw, safe = directory(args.raw), directory(args.safe)
    errors, reports = [], []
    native_profile = None
    status = raw / "profile.exit"
    if status.is_file() and not status.is_symlink():
        value = status.read_text().strip()
        if re.fullmatch(r"[0-9]{1,3}", value) and int(value) <= 255:
            native_profile = int(value)
        else:
            errors.append("PROFILE_EXIT_INVALID")
    invocations = []
    try:
        if (raw / "invocations.json").exists():
            data = load(raw / "invocations.json")
            require(data.get("sourceSha") == SOURCE and data.get("tree") == TREE, "PROFILE_BINDING_INVALID")
            invocations = data["invocations"]
        elif (raw / "invocations.ndjson").exists():
            invocations = [json.loads(line, object_pairs_hook=unique) for line in (raw / "invocations.ndjson").read_text().splitlines() if line.strip()]
        require(len(invocations) <= 5, "PROFILE_INVOCATION_LIMIT")
    except (OSError, ValueError, TypeError, KeyError):
        errors.append("INVOCATION_RECORD_UNAVAILABLE_OR_INVALID")
        invocations = []
    for slot, project in enumerate(PROJECTS, 1):
        row = {"slot": slot, "project": project, "nativeExit": None, "attempted": False,
               "trx": {"present": False, "valid": None}, "observer": None, "managedObserver": None}
        try:
            if slot <= len(invocations):
                invocation = invocations[slot - 1]
                require(invocation.get("id") == project and invocation.get("project") == project and
                        invocation.get("resultsDir") == f"result-{slot}", "INVOCATION_ORDER_OR_PATH_INVALID")
                native = invocation.get("exitCode")
                require(type(native) is int and 0 <= native <= 255, "INVOCATION_NATIVE_INVALID")
                row.update(attempted=True, nativeExit=native)
        except (OSError, ValueError, TypeError, KeyError):
            errors.append(f"PROJECT_{slot}_INVOCATION_RECORD_INVALID")
        try:
            row["observer"] = native_observation(safe, slot)
            row["managedObserver"] = managed_observation(safe, slot)
            observation = row["observer"]
            if row["attempted"]:
                require(observation["recorded"] and observation["observerStatus"] == "PASS", "NATIVE_OBSERVER_NOT_COMPLETE")
                observed_native = observation["nativeExit"]
                require(type(observed_native) is int, "OBSERVED_NATIVE_MISSING")
                shell_native = observed_native if observed_native >= 0 else 128 - observed_native
                require(shell_native == row["nativeExit"], "OBSERVED_NATIVE_DISAGREEMENT")
        except (OSError, ValueError, TypeError, KeyError):
            errors.append(f"PROJECT_{slot}_OBSERVER_INCOMPLETE")
        try:
            result_dir = raw / f"result-{slot}"
            require(not result_dir.is_symlink(), "TRX_DIRECTORY_SYMLINK")
            files = list(result_dir.glob("*.trx")) if result_dir.is_dir() else []
            row["trx"] = {"present": bool(files), "valid": None, "filesObserved": len(files)}
            require(len(files) <= 1, "MULTIPLE_TRX_FILES")
            if files:
                row["trx"] = trx(files[0], row["nativeExit"])
            elif row["attempted"]:
                errors.append(f"PROJECT_{slot}_TRX_NOT_PRODUCED")
        except (OSError, ValueError, TypeError, KeyError, ET.ParseError):
            errors.append(f"PROJECT_{slot}_COLLECTION_INCOMPLETE")
            row["collectionStatus"] = "INCOMPLETE"
        reports.append(row)
    source_preserved = None
    postcheck_status, postcheck_reason = "NOT_VERIFIED", None
    try:
        source_identity(Path(args.source), Path(args.tooling))
        require(locks(Path(args.source)) == load(raw / "locks-before.json"), "LOCK_BYTES_CHANGED")
        source_preserved = True
        postcheck_status = "PASS"
    except (OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as error:
        postcheck_reason = safe_refusal(error)
        drift_codes = {"REVISION_DRIFT", "TREE_DRIFT", "TRACKED_SOURCE_DRIFT", "PROTECTED_BUNDLE_DRIFT",
                       "LOCK_SOURCE_DRIFT", "LOCK_BYTES_CHANGED", "LOCK_INVENTORY_DRIFT", "LOCK_NOT_REGULAR"}
        if isinstance(error, DiagnosticRefusal) and postcheck_reason in drift_codes:
            source_preserved, postcheck_status = False, "DRIFT_OBSERVED"
            errors.append("SOURCE_OR_LOCK_DRIFT_OBSERVED")
        else:
            source_preserved, postcheck_status = None, "NOT_VERIFIED"
            errors.append("SOURCE_OR_LOCK_POSTCHECK_NOT_VERIFIED")
    attempted = sum(row["attempted"] for row in reports)
    execution_observed = attempted > 0 or any(row["observer"] and row["observer"].get("processLaunched") for row in reports)
    complete_inventory = attempted == 5 and all(row["trx"].get("valid") for row in reports)
    test_failure = any(row["trx"].get("valid") and row["trx"].get("failed", 0) > 0 for row in reports)
    host_startup = any(row["managedObserver"] and "TEST_HOST_ASSEMBLY_OBSERVED" in row["managedObserver"]["phases"] for row in reports)
    result = {"schema": 1, "mode": "STANDALONE_PROFILE_DIAGNOSTIC_NOT_C1_GATE", "sourceSha": SOURCE,
              "sourceTree": TREE, "toolingSha": TOOLING, "observedAtUtc": utc(),
              "profileNativeExit": native_profile, "projectsRecorded": attempted, "projectCountRequired": 5,
              "profileExecutionObserved": execution_observed,
              "testHostStartupObserved": host_startup, "testFailureObserved": test_failure,
              "observerFailureObserved": bool(errors),
              "sourceAnd42LocksPreserved": source_preserved,
              "sourceAnd42LocksPostcheck": {"status": postcheck_status, "reasonCode": postcheck_reason},
              "projects": reports,
              "collectionStatus": "PASS" if not errors else "INCOMPLETE", "collectionErrors": errors,
              "nativeOutcome": "NOT_OBSERVED" if native_profile is None else "SUCCESS" if native_profile == 0 else "FAILURE",
              "testOutcome": "NOT_RUN" if not execution_observed else "PASS" if complete_inventory and native_profile == 0 and not errors else "FAIL" if test_failure else "NOT_VERIFIED",
              "causeEstablished": False, "originalHostedFailureCause": "NOT_RECOVERED",
              "rawNativeLogsPublished": False, "rawTrxPublished": False,
              "limitations": ["Console/TRX category matches are observations, not established terminal causes.",
                              "A missing category or project frame is NOT_RECOVERED, not inferred startup failure.",
                              "Managed observer activity is process-scoped and may not identify the individual failing case."]}
    write(safe / "profile-result.json", result)
    print(json.dumps({"collectionStatus": result["collectionStatus"], "testOutcome": result["testOutcome"],
                      "profileNativeExit": native_profile, "projectsRecorded": attempted}, sort_keys=True))
    return 0 if not errors else 2


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="operation", required=True)
    for name in ("admit", "finalize"):
        item = sub.add_parser(name)
        for argument in ("source", "tooling", "raw", "safe"):
            item.add_argument("--" + argument, required=True)
    item = sub.add_parser("image")
    item.add_argument("--tag", required=True)
    item.add_argument("--safe", required=True)
    args = parser.parse_args()
    try:
        return {"admit": admit, "image": image, "finalize": finalize}[args.operation](args) or 0
    except (OSError, ValueError, TypeError, KeyError, ET.ParseError, subprocess.SubprocessError) as error:
        category = safe_refusal(error)
        if args.operation == "finalize":
            try:
                write(directory(args.safe) / "profile-result.json", {"schema": 1, "sourceSha": SOURCE,
                      "collectionStatus": "FAIL", "collectionErrors": ["FINALIZER_FAILED", category],
                      "testOutcome": "NOT_VERIFIED", "causeEstablished": False})
            except (OSError, ValueError):
                pass
        print(json.dumps({"operation": args.operation, "observerStatus": "FAIL", "category": category}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
