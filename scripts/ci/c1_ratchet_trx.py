"""Read native VSTest TRX without exporting raw diagnostics or result paths."""

import hashlib
import re
from decimal import Decimal
from pathlib import Path, PurePosixPath
from unicodedata import category
from xml.etree import ElementTree as ET


class TrxError(ValueError):
    """A fixed, safe diagnostic for unusable or unsupported test evidence."""


_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
_FQN = re.compile(r"[A-Za-z_][A-Za-z0-9_]*[A-Za-z0-9_.+@`]*")
_COMPONENT = re.compile(r"[A-Za-z0-9_.-]+")
_GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}")
_XUNIT_V2_ADAPTER = "executor://xunit/vstestrunner2/netcoreapp"
_XUNIT_ABBREVIATED_VALUE = re.compile(
    r'(?:[:=] |\[|, )"(?P<body>(?:[^"\\]|\\.){24,50}\\?)"\.\.\.(?=\s*(?:,|\)|\]|}))'
)
_FRAME = re.compile(r"at (?P<name>[A-Za-z_][A-Za-z0-9_.+@`<>]*(?:\.\.ctor)?)\(")
_SECRET_SHAPED = re.compile(
    r"op://|[?&](?:token|key|sig|password)=|"
    r"gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|"
    r"\b[A-Za-z0-9_/-]{20,}\.[A-Za-z0-9_/-]{15,}\.[A-Za-z0-9_/-]{15,}\b|"
    r"\b(?:password|passwd|secret|token|credential)\s*=\s*\S+",
    flags=re.IGNORECASE,
)
_NUMBER = r"-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?"
_STARTUP_METHOD = "Tracked_settings_and_environment_overrides_start_only_with_known_authentication_keys"
_STARTUP_FRAME = "Zeka.Authentication.Tests.RealHostStartupTests." + _STARTUP_METHOD
_COUNT_EQUAL = frozenset({("2", "1")})
_CATEGORICAL_EQUAL = frozenset({
    ("Unavailable", "Authorized"),
    ("Forbidden", "ServiceUnavailable"),
})
_RANGES = frozenset({("1.5", "3"), ("1.8", "2.8")})
_PUBLIC_CAPTIONS = {
    "Infrastructure.IntegrationTests.TenantHttpTests.Adapter_rejects_conflicting_or_unknown_contract_values":
        '(invalid: "permission")',
    "Infrastructure.IntegrationTests.AuthenticationStartupTests." + _STARTUP_METHOD: "(unexpectedKey: null)",
}
_ZERO_COUNTERS = (
    "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
    "notRunnable", "disconnected", "warning", "completed", "inProgress", "pending",
)


def _require(condition, reason):
    if not condition:
        raise TrxError(reason)


def _one(parent, tag, reason):
    nodes = parent.findall(_NS + tag)
    _require(len(nodes) == 1, reason)
    return nodes[0]


def _read(path):
    try:
        with Path(path).open("rb") as stream:
            data = stream.read(32_000_001)
        _require(0 < len(data) <= 32_000_000, "TRX_SIZE_OUT_OF_RANGE")
        text = data.decode("utf-8-sig")
        _require("<!DOCTYPE" not in text and "<!ENTITY" not in text, "TRX_XML_DECLARATION_REJECTED")
        root = ET.fromstring(text)
    except (OSError, UnicodeError, ET.ParseError):
        raise TrxError("TRX_UNREADABLE_OR_MALFORMED") from None
    _require(root.tag == _NS + "TestRun", "TRX_UNSUPPORTED_ROOT")
    return root


def _definitions(root):
    entries = _one(root, "TestDefinitions", "TRX_TEST_DEFINITIONS_MISSING_OR_AMBIGUOUS")
    _require(len(entries) > 0 and all(node.tag == _NS + "UnitTest" for node in entries),
             "TRX_TEST_DEFINITIONS_EMPTY_OR_UNSUPPORTED")
    return entries


def _project_path(project):
    path = str(project)
    _require(path.endswith(".csproj") and all(
        part not in (".", "..") and _COMPONENT.fullmatch(part)
        for part in path.split("/")
    ), "TRX_EXPECTED_PROJECT_PATH_INVALID")
    return PurePosixPath(path)


def _assembly(codebase, project):
    parts = codebase.replace("\\", "/").split("/")
    directory = project.parent.parts
    prefix_length = len(parts) - len(directory) - 4
    if any(part in (".", "..") for part in parts) or prefix_length < 0:
        return None
    folded = [part.casefold() for part in parts]
    if folded[prefix_length:-4] != [part.casefold() for part in directory] or folded[-4] != "bin":
        return None
    binary = parts[-1]
    if (
        folded[-3] != "release"
        or folded[-2] != "net8.0"
        or binary != project.stem + ".dll"
        or not _COMPONENT.fullmatch(binary)
    ):
        return None
    return binary.removesuffix(".dll")


def identify_project(path, projects):
    """Resolve one result file to its matching configured repository project."""
    methods = [_one(node, "TestMethod", "TRX_TEST_METHOD_MISSING_OR_AMBIGUOUS")
               for node in _definitions(_read(path))]
    codebases = {node.get("codeBase", "") for node in methods}
    _require(len(codebases) == 1, "TRX_BINARY_SOURCE_MISSING_OR_AMBIGUOUS")
    codebase = codebases.pop()
    matches = [str(project) for project in projects if _assembly(codebase, _project_path(project))]
    _require(len(matches) == 1, "TRX_PROJECT_IDENTITY_MISSING_OR_AMBIGUOUS")
    return matches[0]


def validate_identity(identity):
    """Verify the safe serialized identity common to baseline and TRX rows."""
    _require(isinstance(identity, (list, tuple)) and len(identity) == 3
             and all(isinstance(value, str) for value in identity), "TRX_CASE_IDENTITY_INVALID")
    assembly, qualified, row = identity
    _require(0 < len(assembly) <= 180 and 0 < len(qualified) <= 420
             and _COMPONENT.fullmatch(assembly) and "." in qualified and _FQN.fullmatch(qualified)
             and all(qualified.split(".")) and not any(_SECRET_SHAPED.search(x) for x in (assembly, qualified)),
             "TRX_CASE_IDENTITY_INVALID")
    _require(not row or row == _PUBLIC_CAPTIONS.get(qualified) or re.fullmatch(r"\(sha256:[0-9a-f]{64}\)", row),
             "TRX_THEORY_ROW_UNSAFE_OR_AMBIGUOUS")
    return tuple(identity)


def _protected_row(qualified, row, test_id, adapter):
    # The owner already published these two captions in the authoritative ratchet.
    if not row or _PUBLIC_CAPTIONS.get(qualified) == row:
        return row
    _require(row.startswith("(") and row.endswith(")") and len(row) <= 768
             and all(character.isprintable() or category(character) == "Zs" for character in row)
             and "…" not in row and "···" not in row,
             "TRX_THEORY_ROW_UNSAFE_OR_AMBIGUOUS")
    # xUnit v2 abbreviates strings with ASCII dots outside the closing quote.
    # Its netcoreapp adapter derives testId from the fully serialized arguments.
    if any(len(match["body"].encode("utf-16-le")) == 100 for match in _XUNIT_ABBREVIATED_VALUE.finditer(row)):
        _require(adapter == _XUNIT_V2_ADAPTER, "TRX_ABBREVIATED_CASE_ADAPTER_UNSUPPORTED")
        row = test_id
    return "(sha256:" + hashlib.sha256(row.encode("utf-8")).hexdigest() + ")"


def _row(qualified, display):
    """Use the exact xUnit row caption, independently of display-name prefixes."""
    short = ".".join(qualified.split(".")[-2:])
    for prefix in (qualified, short, qualified.rsplit(".", 1)[-1]):
        if display == prefix:
            return ""
        if display.startswith(prefix + "("):
            return display[len(prefix):]
    raise TrxError("TRX_DISPLAY_NAME_UNBOUND")


def _attribute_id(node, name):
    value = node.get(name)
    _require(isinstance(value, str) and _GUID.fullmatch(value), "TRX_NATIVE_ID_MISSING_OR_INVALID")
    return value.lower()


def _counter(counters, attribute):
    value = counters.get(attribute, "")
    _require(bool(re.fullmatch(r"[0-9]{1,9}", value)), "TRX_COUNTER_MISSING_OR_INVALID")
    return int(value)


def _project_frame(stack, project, qualified):
    directory = "/".join(project.parent.parts[:2] if project.parts[0] == "Services" else project.parent.parts)
    for line in stack.splitlines():
        frame = _FRAME.match(line.strip())
        if not frame or " in " not in line:
            continue
        source = line.split(" in ", 1)[1].replace("\\", "/")
        name = frame.group("name")
        owned = f"/{directory}/" in "/" + source
        if owned or (name == _STARTUP_FRAME and "/Shared/Authentication.TestSupport/RealHostStartupTests.cs" in "/" + source):
            return name
    # Some adapters omit file paths. Accept the exact test method, never a coincidental assembly namespace.
    for line in stack.splitlines():
        frame = _FRAME.match(line.strip())
        if frame and " in " not in line and frame.group("name") in (qualified, _STARTUP_FRAME):
            return frame.group("name")
    raise TrxError("TRX_PROJECT_FRAME_MISSING")


def _assertion(message, qualified):
    stripped = message.strip()
    equal = re.fullmatch(r"Assert\.Equal\(\) Failure(?:: Values differ)?\s+Expected:\s+(\w+)\s+Actual:\s+(\w+)", stripped)
    if equal and (equal.groups() in _CATEGORICAL_EQUAL or equal.groups() in _COUNT_EQUAL):
        return "not-emitted", "Assert.Equal:" + ":".join(equal.groups())
    interval = re.fullmatch(
        rf"Assert\.InRange\(\) Failure(?:: Value not in range)?\s+Range:\s+\(({_NUMBER}) - ({_NUMBER})\)\s+Actual:\s+({_NUMBER})",
        stripped,
    )
    if interval and interval.groups()[:2] in _RANGES:
        start, end, actual = map(Decimal, interval.groups())
        _require(not start <= actual <= end, "TRX_ASSERTION_DIAGNOSTIC_INCONSISTENT")
        return "not-emitted", f"Assert.InRange:{interval[1]}:{interval[2]}:outside"
    if stripped in (
        "The collection was expected to contain a single element, but it was empty.",
        "Assert.Single() Failure: The collection was empty",
    ):
        return "not-emitted", "Assert.Single:empty"
    if re.fullmatch(r"System\.TimeoutException\s*:\s*The operation has timed out\.", stripped):
        return "System.TimeoutException", "operation-timed-out"
    management_exception = re.compile(
        r"^System\.ArgumentException\s*:\s*ManagementEndpoint is required for exact topology/capability verification\.$",
        flags=re.MULTILINE,
    )
    startup_log = "\n".join(line.strip() for line in stripped.splitlines())
    if qualified.endswith("." + _STARTUP_METHOD) and (
        len(management_exception.findall(startup_log)) == 1
        and re.search(r"^Hosting failed to start$", startup_log, re.MULTILINE)
        and re.search(r"^at Zeka\.Extensions\.EventBus\.RabbitMq\.RabbitMqTopology\.\.ctor\(", startup_log, re.MULTILINE)
        and re.search(r"(?:^|\n)Expected: True\nActual:\s+False$", startup_log)
    ):
        return "not-emitted", "Assert.True:True:False:inner-System.ArgumentException:ManagementEndpoint-required"
    if re.fullmatch(
        r"Assert\.True\(\) Failure(?:\s+Condition is false)?\s+Expected:\s+True\s+Actual:\s+False",
        stripped,
    ):
        return "not-emitted", "Assert.True:True:False"
    return "not-emitted", "unrecognized-failure"


def _signature(result, project, qualified):
    output = _one(result, "Output", "TRX_FAILED_OUTPUT_MISSING_OR_AMBIGUOUS")
    info = _one(output, "ErrorInfo", "TRX_FAILURE_DIAGNOSTIC_MISSING_OR_AMBIGUOUS")
    message = _one(info, "Message", "TRX_FAILURE_MESSAGE_MISSING_OR_AMBIGUOUS")
    stack = _one(info, "StackTrace", "TRX_FAILURE_STACK_MISSING_OR_AMBIGUOUS")
    _require(len(message) == len(stack) == 0 and message.text and stack.text,
             "TRX_FAILURE_DIAGNOSTIC_MISSING_OR_AMBIGUOUS")
    exception, assertion = _assertion(message.text, qualified)
    frame = _project_frame(stack.text, project, qualified)
    _require(len(frame) <= 420 and not _SECRET_SHAPED.search(frame), "TRX_FRAME_UNSAFE")
    return {"exception": exception, "assertion": assertion, "frame": frame}


def load_trx(path, project):
    """Return flat, project-qualified rows only when counts and provenance agree."""
    project = _project_path(project)
    root = _read(path)
    definitions = {}
    codebases = set()
    for definition in _definitions(root):
        test_id = _attribute_id(definition, "id")
        _require(test_id not in definitions, "TRX_DUPLICATE_DEFINITION")
        method = _one(definition, "TestMethod", "TRX_TEST_METHOD_MISSING_OR_AMBIGUOUS")
        codebase = method.get("codeBase", "")
        codebases.add(codebase)
        adapter = method.get("adapterTypeName", "").casefold()
        _require(adapter.startswith("executor://xunit/"), "TRX_TEST_ADAPTER_UNSUPPORTED")
        assembly = _assembly(codebase, project)
        _require(assembly is not None
                 and definition.get("storage", "").replace("\\", "/").casefold()
                 == codebase.replace("\\", "/").casefold(),
                 "TRX_PROJECT_BINARY_MISMATCH")
        qualified = method.get("className", "") + "." + method.get("name", "")
        execution = _one(definition, "Execution", "TRX_EXECUTION_LINK_MISSING_OR_AMBIGUOUS")
        definitions[test_id] = (assembly, qualified, definition.get("name", ""), _attribute_id(execution, "id"), adapter)
    _require(len(codebases) == 1, "TRX_BINARY_SOURCE_MISSING_OR_AMBIGUOUS")
    entries = {}
    for entry in _one(root, "TestEntries", "TRX_TEST_ENTRIES_MISSING_OR_AMBIGUOUS"):
        _require(entry.tag == _NS + "TestEntry" and len(entry) == 0, "TRX_NESTED_TEST_ENTRY_UNSUPPORTED")
        execution_id = _attribute_id(entry, "executionId")
        _require(execution_id not in entries, "TRX_DUPLICATE_EXECUTION")
        entries[execution_id] = _attribute_id(entry, "testId")
    results = _one(root, "Results", "TRX_TEST_RESULTS_MISSING_OR_AMBIGUOUS")
    _require(len(results) > 0, "TRX_ZERO_TESTS")
    rows = []
    identities = set()
    used_definitions = set()
    counts = {"Passed": 0, "Failed": 0, "NotExecuted": 0}
    for result in results:
        _require(result.tag == _NS + "UnitTestResult" and result.find(_NS + "InnerResults") is None,
                 "TRX_NESTED_TEST_RESULT_UNSUPPORTED")
        test_id = _attribute_id(result, "testId")
        execution_id = _attribute_id(result, "executionId")
        _require(test_id in definitions and entries.pop(execution_id, None) == test_id
                 and test_id not in used_definitions, "TRX_RESULT_EXECUTION_UNBOUND")
        used_definitions.add(test_id)
        assembly, qualified, definition_name, linked_id, adapter = definitions[test_id]
        row = _row(qualified, result.get("testName", ""))
        _require(execution_id == linked_id and row == _row(qualified, definition_name),
                 "TRX_RESULT_DEFINITION_MISMATCH")
        identity = validate_identity([assembly, qualified, _protected_row(qualified, row, test_id, adapter)])
        _require(identity not in identities, "TRX_DUPLICATE_CASE_IDENTITY")
        identities.add(identity)
        outcome = result.get("outcome")
        _require(outcome in counts, "TRX_RESULT_OUTCOME_UNSUPPORTED")
        counts[outcome] += 1
        signature = _signature(result, project, qualified) if outcome == "Failed" else None
        rows.append({"identity": list(identity), "outcome": outcome, "signature": signature})
    _require(not entries and len(used_definitions) == len(definitions), "TRX_INCOMPLETE_RESULT_LINKS")
    summary = _one(root, "ResultSummary", "TRX_RUN_SUMMARY_MISSING_OR_AMBIGUOUS")
    _require(summary.get("outcome") == ("Failed" if counts["Failed"] else "Completed"),
             "TRX_RUN_SUMMARY_INCONSISTENT")
    run_infos = summary.findall(f"{_NS}RunInfos/{_NS}RunInfo")
    _require(all(info.get("outcome", "").casefold() in ("warning", "informational") for info in run_infos),
             "TRX_RUN_INFRASTRUCTURE_ERROR")
    counters = _one(summary, "Counters", "TRX_COUNTERS_MISSING_OR_AMBIGUOUS")
    known_counters = {"total", "passed", "failed", "executed", "notExecuted", *_ZERO_COUNTERS}
    _require(counters.attrib.keys() == known_counters, "TRX_RUN_COUNTER_SET_UNSUPPORTED")
    _require(all(_counter(counters, key) == 0 for key in _ZERO_COUNTERS),
             "TRX_RUN_INFRASTRUCTURE_COUNTER_NONZERO")
    _require(_counter(counters, "total") == len(rows)
             and _counter(counters, "passed") == counts["Passed"]
             and _counter(counters, "failed") == counts["Failed"]
             and _counter(counters, "executed") == counts["Passed"] + counts["Failed"]
             # VSTest 17.x hard-codes this summary field; the per-result outcomes are authoritative.
             and _counter(counters, "notExecuted") in (0, counts["NotExecuted"]),
             "TRX_RUN_COUNTERS_INCONSISTENT")
    return sorted(rows, key=lambda row: row["identity"])
