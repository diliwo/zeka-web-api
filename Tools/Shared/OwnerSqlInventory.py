#!/usr/bin/env python3
"""Fail-closed source inventory and PG17 execution-receipt comparison.

This proves current handwritten producer execution coverage, not arbitrary migration
or PL/pgSQL branch conformance. It never serializes SQL, parameters or file contents.
"""
from __future__ import annotations
import argparse
from datetime import datetime, timedelta
import hashlib
import os
import shutil
import subprocess
import glob
import xml.etree.ElementTree as ET
import json
import re
import unicodedata
from pathlib import Path

SCHEMA = "zeka-owner-sql-execution/v1"
SCHEMA_V2 = "zeka-owner-sql-execution/v2"
TOOLS = ("Tools/Zeka.AuthOwnerProvision", "Tools/Zeka.DbMigrate", "Tools/Zeka.LocalStack.DatabaseSetup")
ROOT_LOCK_PROPS_SHA256 = "921d89480d2f4f6b97cb1453b963b0809092a9e01bd62879f3f1ae0a1cbcc1c6"
AUTH_WEB_PROJECT = "Services/AuthManager/AuthManager.API/AuthManager.API.csproj"
SERVICE_PROJECTS = (
    AUTH_WEB_PROJECT,
    "Services/AuthManager/AuthManager.Infrastructure/AuthManager.Infrastructure.csproj",
    "Services/AdminAreaManagement/AdminAreaManagement.Infrastructure/AdminAreaManagement.Infrastructure.csproj",
    "Services/ClientManagement/Client.Infrastructure/ClientManagement.Infrastructure.csproj",
)
EXTRA = ("Shared/RlsSecurityManifestVerifier.cs", "Shared/RuntimeDatabaseIdentityValidator.cs",
         "Services/AuthManager/AuthManager.Infrastructure/Persistence/TenantContextInitializer.cs")
HELPERS = {
    "Shared/RlsSecurityManifestVerifier.cs": ("RowsAsync",),
    "Tools/Zeka.LocalStack.DatabaseSetup/Program.cs": ("Execute", "Scalar", "Rows"),
    "Tools/Zeka.LocalStack.DatabaseSetup/GuardChecks.cs": ("ExecuteAsync",),
    "Tools/Zeka.DbMigrate/MigrationEngine.cs": ("ExecuteFixedAsync",),
}
QUERY_FILE = "Deployments/local-stack/OwnerFirstAdminPreflightQueries.ps1"
QUERY_IDS = ("owner.identity.v1", "owner.schema-presence.v1", "owner.schema-shape.v1",
             "owner.privileges.v1", "owner.catalogue.v1", "owner.existing-identity.v1",
             "owner.idempotency.v1", "owner.rls-metadata.v1")
CONTROL_IDS = tuple("owner.transaction." + x + ".v1" for x in
                    ("begin-readonly", "statement-timeout", "lock-timeout", "rollback"))
DEPENDENCIES = {
    "owner.privileges.v1": ("Deployments/local-stack/OwnerFirstAdminPreflightPrivileges.ps1",),
    "owner.rls-metadata.v1": ("Deployments/local-stack/OwnerFirstAdminRlsMetadata.ps1",),
}
CLOSED_SURFACE = "Tools/Shared/OwnerSqlClosedSurface.json"
SOURCE_BOUNDARY = "Tools/Shared/OwnerSqlReferencedSourceBoundary.json"
SOURCE_BOUNDARY_POLICY = "Every referenced compiled source/project file-set or byte change requires explicit source review and manifest acceptance; no automatic enrollment."
CS_STRING_PREFIX = re.compile(r"(?:\$+@?|@\$?)?([\"'])")
CS_RAW_DELIMITER = re.compile(r'"{3,}')
BOOTSTRAPS = tuple("Deployments/database/bootstrap-" + x + "-roles.sql" for x in
                   ("auth", "adminarea", "client"))
RAW_EXECUTE = re.compile(r"(?:\b([A-Za-z_]\w*)\s*)?\??\.\s*(Execute(?:Reader|Scalar|NonQuery|BootstrapNonQuery)(?:Async)?)\s*\(")
# Fail closed even for extension/static calls, generic queries and method groups.
# These APIs must never become an empty inventory or a runtime EF SQL exemption.
EF_SQL_API = re.compile(r"\b(?:ExecuteSql\w*|SqlQuery\w*|FromSql\w*|ExecuteUpdate(?:Async)?|ExecuteDelete(?:Async)?)\b")
WRAPPER = "OwnerSqlExecution"
AUTH_INFRA = "Services/AuthManager/AuthManager.Infrastructure/"
AUTH_RECEIPT = AUTH_INFRA + "Persistence/AuthOwnerSqlReceipt.cs"
AUTH_EF_WRAPPERS = {
    "ObserveAuthLoadAsync": ("FromSqlInterpolated", "LoadAsync"),
    "ObserveAuthQueryAsync": ("FromSqlInterpolated", "SingleOrDefaultAsync"),
    "ObserveAuthUpdateAsync": ("ExecuteUpdateAsync", None),
}
AUTH_EF_FILES = {
    AUTH_INFRA + "Identity/BrowserSessions.cs": {"ObserveAuthQueryAsync", "ObserveAuthUpdateAsync"},
    AUTH_INFRA + "Workers/WorkerIdentitySource.cs": {"ObserveAuthLoadAsync"},
}
AUTH_EF_NAME = re.compile(r"\b(" + "|".join(AUTH_EF_WRAPPERS) + r")\b")
AUTH_EF_CALL = re.compile(r"\bAuthOwnerSqlReceipt\s*\.\s*" + AUTH_EF_NAME.pattern + r"\s*\(")

class InventoryError(ValueError):
    pass

SCANNER = shutil.which("sonar") or shutil.which("sonar.exe")
_READ_CACHE: dict[str, tuple[tuple, bytes]] = {}

def public_bytes(path: Path) -> bytes:
    """Enforce deterministic scan before any source/receipt content read or hash."""
    if path.is_symlink() or not path.is_file():
        raise InventoryError("Input must be an existing regular non-symlink file")
    resolved = path.resolve()
    stat = resolved.stat()
    identity = (stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns)
    key = str(resolved)
    if key in _READ_CACHE:
        previous, data = _READ_CACHE[key]
        if previous != identity: raise InventoryError("Input changed after its successful scan")
        return data
    if not SCANNER: raise InventoryError("Required deterministic Sonar scanner is unavailable")
    try:
        result = subprocess.run([SCANNER, "analyze", "secrets", str(resolved)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False, timeout=120)
    except (OSError, subprocess.TimeoutExpired):
        raise InventoryError("Required deterministic Sonar scan could not complete") from None
    if result.returncode != 0:
        raise InventoryError("Required deterministic Sonar scan failed; input was not read")
    scanned = resolved.stat()
    if (scanned.st_dev,scanned.st_ino,scanned.st_size,scanned.st_mtime_ns) != identity:
        raise InventoryError("Input changed during its secret scan")
    data = resolved.read_bytes()
    after = resolved.stat()
    if (after.st_dev,after.st_ino,after.st_size,after.st_mtime_ns) != identity:
        raise InventoryError("Input changed during its post-scan read")
    _READ_CACHE[key] = (identity,data)
    return data

def digest(path: Path) -> str:
    return hashlib.sha256(public_bytes(path)).hexdigest()

def code_mask(text: str) -> str:
    """Mask C# comments/strings, preserving newlines and physical call-site offsets.

    Raw/interpolated strings are treated as opaque SQL-bearing literals. Nested
    interpolation expressions are deliberately not execution sites: executing SQL
    from within an interpolation is rejected by the literal-content safety check.
    """
    out = list(text)
    i = 0
    def blank(a: int, b: int) -> None:
        for k in range(a, b):
            if out[k] not in "\r\n": out[k] = " "
    while i < len(text):
        start = i
        if text.startswith("//", i):
            end = text.find("\n", i)
            i = len(text) if end < 0 else end
            blank(start, i); continue
        if text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end < 0: raise InventoryError("Unterminated C# comment")
            i = end + 2; blank(start, i); continue
        # Prefix can be $, @, $@ or @$; ordinary character literals also masked.
        prefix = CS_STRING_PREFIX.match(text, i)
        if prefix:
            token = prefix.group(0)
            quote = prefix.group(1)
            # Count raw delimiters separately: an overlapping quote alternative
            # obscures the ordinary/empty-string boundary (Sonar S5855).
            if quote == '"':
                raw = CS_RAW_DELIMITER.match(text, i + len(token) - 1)
                if raw:
                    quote = raw.group(0)
                    token = token[:-1] + quote
            i += len(token)
            if len(quote) >= 3:
                end = text.find(quote, i)
                if end < 0: raise InventoryError("Unterminated C# raw string")
                i = end + len(quote)
            else:
                verbatim = "@" in token
                while i < len(text):
                    if text[i] == quote:
                        if verbatim and i + 1 < len(text) and text[i + 1] == quote:
                            i += 2; continue
                        i += 1; break
                    if not verbatim and text[i] == "\\": i += 2
                    else: i += 1
                else: raise InventoryError("Unterminated C# string or character literal")
            # No hidden executable database calls in interpolated expressions.
            if "$" in token and (re.search(r"\b(?:ExecuteReader|ExecuteScalar|ExecuteNonQuery)(?:Async)?\s*\(", text[start:i])
                                 or EF_SQL_API.search(text[start:i])):
                raise InventoryError("Database execution inside string interpolation is unsupported")
            blank(start, i); continue
        i += 1
    return "".join(out)

def matching(code: str, start: int, opening: str, closing: str) -> int:
    depth = 0
    for i in range(start, len(code)):
        if code[i] == opening: depth += 1
        elif code[i] == closing:
            depth -= 1
            if depth == 0: return i
    raise InventoryError("Unbalanced C# delimiters")

def helper_ranges(code: str, names: tuple[str, ...]) -> tuple[list[tuple[int,int]], set[int]]:
    bodies, declarations = [], set()
    for name in names:
        for m in re.finditer(r"\b" + re.escape(name) + r"\s*\(", code):
            # Declaration ends with body or => after balanced signature.
            close = matching(code, code.index("(", m.start()), "(", ")")
            tail = code[close + 1:]
            nxt = re.match(r"\s*(\{|=>)", tail)
            if not nxt: continue
            # Calls immediately followed by lambda/block are unsupported, never silently ignored.
            lineprefix = code[max(code.rfind(";",0,m.start()), code.rfind("}",0,m.start()), code.rfind("{",0,m.start()))+1:m.start()]
            if not re.search(r"\b(?:static|private|public|internal|protected)\b", lineprefix): continue
            declarations.add(m.start())
            body_start = close + 1 + nxt.end() - len(nxt.group(1))
            if nxt.group(1) == "{":
                body_end = matching(code, body_start, "{", "}")
            else:
                body_end = code.find(";", body_start)
                if body_end < 0: raise InventoryError("Unterminated helper expression body")
            signature = code[m.start():close]
            if not (re.search(r"CallerFilePath", signature) and re.search(r"CallerLineNumber", signature)):
                raise InventoryError("SQL helper lacks compiler caller identity: " + name)
            bodies.append((body_start, body_end))
    return bodies, declarations

def auth_ef_sites(relative: str, code: str) -> tuple[set[int], set[int]]:
    """Recognize only the sealed Auth call sites with a single observed EF command."""
    if relative != AUTH_RECEIPT and re.search(r"\bObserveAuthCommand\b", code):
        raise InventoryError("Auth EF receipt observer used outside its adapter: " + relative)
    if relative == AUTH_RECEIPT:
        # These exact enum references classify provider events; they do not invoke EF.
        event_kinds = re.finditer(r"\bCommandSource\s*\.\s*(ExecuteUpdate|FromSqlQuery)\b(?!\s*\()", code)
        return set(), {kind.start(1) for kind in event_kinds}

    sites, supported_ef, wrapped_names = set(), set(), set()
    for call in AUTH_EF_CALL.finditer(code):
        name = call.group(1)
        if name not in AUTH_EF_FILES.get(relative, ()):
            raise InventoryError("Unregistered Auth EF receipt call in " + relative)
        opening = call.end() - 1
        end = matching(code, opening, "(", ")")
        prefix = re.match(r"\s*database\s*,\s*\(\s*\)\s*=>\s*", code[opening+1:end])
        operations = list(EF_SQL_API.finditer(code, opening+1, end))
        expected_api, terminal = AUTH_EF_WRAPPERS[name]
        if prefix is None or len(operations) != 1 or operations[0].group() != expected_api:
            raise InventoryError("Unsupported Auth EF receipt operation in " + relative)
        operation = operations[0]
        receiver = code[opening+1+prefix.end():operation.start()]
        expected_receiver = r"database\s*\.\s*\w+\s*\.\s*" if terminal else r"affected\s*\.\s*"
        call_arguments = re.match(r"\s*\(", code[operation.end():end])
        if not re.fullmatch(expected_receiver, receiver) or call_arguments is None:
            raise InventoryError("Unsupported Auth EF receipt operation in " + relative)
        operation_end = matching(code, operation.end()+call_arguments.end()-1, "(", ")")
        tail = code[operation_end+1:end]
        # The whole expression must execute and finish one supported command. No caller
        # identity overrides, deferred enumerables, other expressions or second commands.
        expected_tail = r"\s*\.\s*" + terminal + r"\s*\(\s*\w+\s*\)\s*" if terminal else r"\s*"
        if operation_end >= end or not re.fullmatch(expected_tail, tail):
            raise InventoryError("Unsupported Auth EF receipt completion in " + relative)
        wrapped_names.add(call.start(1))
        supported_ef.add(operation.start())
        sites.add(call.start())
    if any(name.start() not in wrapped_names for name in AUTH_EF_NAME.finditer(code)):
        raise InventoryError("Unqualified/unsupported Auth EF receipt call in " + relative)
    return sites, supported_ef


def csharp_producers(relative: str, text: str) -> list[dict]:
    code = code_mask(text)
    if any(unicodedata.category(char) == "Cf" for char in code):
        raise InventoryError("C# identifier format characters require explicit SQL discovery review in " + relative)
    if re.search(r"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})", code):
        raise InventoryError("Escaped C# identifiers require explicit SQL discovery review in " + relative)
    offsets, supported_ef = auth_ef_sites(relative, code)
    if any(api.start() not in supported_ef for api in EF_SQL_API.finditer(code)):
        raise InventoryError("Unsupported runtime EF SQL API in " + relative)
    helper_names = HELPERS.get(relative, ())
    bodies, declarations = helper_ranges(code, helper_names)
    recognized = {m.start(2) for m in RAW_EXECUTE.finditer(code)}
    for m in re.finditer(r"\bExecute(?:Reader|Scalar|NonQuery|BootstrapNonQuery)(?:Async)?\b",code):
        if m.start() not in recognized:
            raise InventoryError("Unqualified/unsupported database execution in " + relative)
    for match in RAW_EXECUTE.finditer(code):
        receiver = match.group(1)
        if receiver != WRAPPER:
            raise InventoryError("Uninstrumented database execution at " + relative + ":L" + str(code.count("\n",0,match.start())+1))
        if any(a <= match.start() <= b for a,b in bodies):
            close = matching(code, code.index("(", match.start()), "(", ")")
            args = code[match.start():close]
            if not (re.search(r"\b\w*[Ff]ile\w*\b", args) and re.search(r"\b\w*[Ll]ine\w*\b", args)):
                raise InventoryError("SQL helper does not forward compiler caller identity: " + relative)
        else:
            close = matching(code, code.index("(", match.start()), "(", ")")
            if re.search(r"\bproducer(?:File|Line)\b", code[match.start():close]):
                raise InventoryError("Unregistered caller-forwarding helper in " + relative)
            offsets.add(match.start())
    for name in helper_names:
        for match in re.finditer(r"\b" + re.escape(name) + r"\s*\(", code):
            if match.start() not in declarations: offsets.add(match.start())
    # Additional provider execution APIs require explicit review, not an exemption.
    if re.search(r"\b(?:ExecuteDbDataReader|ExecuteSqlCommand)(?:Async)?\b", code):
        raise InventoryError("Unsupported database execution API in " + relative)
    return [{"producerId": relative + ":L" + str(code.count("\n",0,i)+1),
             "source": relative, "kind": "csharp-callsite"} for i in sorted(offsets)]

def discover_tools(root: Path) -> tuple[list[Path],list[Path]]:
    """Include every production Tools source/project; only named fixture scopes excluded."""
    base = root/"Tools"
    if not base.is_dir(): raise InventoryError("Missing production Tools source scope")
    projects, files = [], []
    for path in base.rglob("*"):
        if not path.is_file(): continue
        relative = path.relative_to(base)
        if {"bin","obj"}.intersection(relative.parts): continue
        if "Zeka.OwnerSqlContractTests" in relative.parts: continue
        if any(part.endswith(".Tests") for part in relative.parts[:-1]): continue
        if path.name.endswith(".Tests.csproj"): continue
        if path.suffix.lower() == ".csproj": projects.append(path)
        elif path.suffix.lower() == ".cs": files.append(path)
    if not projects: raise InventoryError("Empty production tool project inventory")
    return sorted(projects),sorted(files)

def discover_project_sources(root: Path, roots: list[Path]) -> tuple[list[Path], list[Path]]:
    """Conservative source closure for the repository's literal SDK project graph.

    This is not an MSBuild evaluator. Conditional/dynamic source composition,
    custom targets/imports and nonstandard default-item policies are rejected,
    rather than guessed or executed. Package/generated SQL remains a separate
    provider obligation; every repository C# source in the compiled graph is read.
    """
    root = root.resolve()
    projects, files, pending = set(), set(), list(roots)

    def contained(path: Path) -> Path:
        # Check every existing path component before resolving any reparse point.
        absolute = Path(os.path.abspath(path))
        if not absolute.is_relative_to(root):
            raise InventoryError("Project source/reference escapes approved repository")
        cursor = absolute
        while cursor != root.parent:
            if cursor.is_symlink() or getattr(cursor, "is_junction", lambda: False)():
                raise InventoryError("Reparse point in project source closure")
            cursor = cursor.parent
        resolved = absolute.resolve()
        if not resolved.is_relative_to(root):
            raise InventoryError("Project source/reference escapes approved repository")
        return resolved

    def literal_path(base: Path, value: str, suffix: str) -> Path:
        if not value or any(x in value for x in ("$", "@", "%", "*", "?", ";")):
            raise InventoryError("Dynamic project source/reference scope requires explicit review")
        candidate = contained(base/value.replace(chr(92), "/"))
        if candidate.suffix.lower() != suffix or not candidate.is_file():
            raise InventoryError("Missing/unsupported project source/reference input")
        return candidate

    while pending:
        project = contained(pending.pop())
        if project in projects: continue
        if not project.is_file() or project.suffix.lower() != ".csproj":
            raise InventoryError("Missing/unsupported source project")
        # Only the reviewed root lockfile selector may participate in build composition.
        for ancestor in (project.parent, *project.parent.parents):
            for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
                candidate = ancestor/name
                if not os.path.lexists(candidate):
                    continue
                if ancestor == root and name == "Directory.Build.props":
                    approved_props = contained(candidate)
                    if approved_props.is_file() and digest(approved_props) == ROOT_LOCK_PROPS_SHA256:
                        files.add(approved_props)
                        continue
                raise InventoryError("Directory build composition requires explicit source-scope review")
        tree = ET.fromstring(public_bytes(project))
        auth_web = project.relative_to(root).as_posix() == AUTH_WEB_PROJECT
        sdk = "Microsoft.NET.Sdk.Web" if auth_web else "Microsoft.NET.Sdk"
        if tree.tag != "Project" or tree.attrib != {"Sdk": sdk}:
            raise InventoryError("Unsupported project SDK/source composition")
        projects.add(project)
        if any(node.tag not in {"PropertyGroup", "ItemGroup"} for node in tree):
            raise InventoryError("Unsupported project build/source composition")
        parent = {child: node for node in tree.iter() for child in node}
        safe_properties = {"OutputType", "TargetFramework", "ImplicitUsings", "Nullable", "AssemblyName",
                           "Version", "ZekaSourceSha", "InformationalVersion", "IncludeSourceRevisionInInformationalVersion"}
        if auth_web:
            safe_properties |= {"InvariantGlobalization", "DockerDefaultTargetOS", "DockerfileContext", "UserSecretsId"}
        removals, linked, references = [], [], []
        for node in tree.iter():
            tag = node.tag.split("}")[-1]
            if parent.get(node) is not None and parent[node].tag == "PropertyGroup" and tag not in safe_properties:
                raise InventoryError("Unreviewed project property requires explicit source-scope review")
            if tag in {"Import", "ImportGroup", "Target", "Choose", "When", "Otherwise", "Sdk", "Reference"}:
                raise InventoryError("Unsupported project build/source composition")
            if tag in {"EnableDefaultItems", "EnableDefaultCompileItems", "DefaultItemExcludes",
                       "DefaultExcludesInProjectFolder", "DefaultItemExcludesInProjectFolder",
                       "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "ImportDirectoryBuildProps",
                       "ImportDirectoryBuildTargets", "CustomBeforeMicrosoftCommonTargets",
                       "CustomAfterMicrosoftCommonTargets", "CustomBeforeMicrosoftCommonCrossTargetingTargets",
                       "CustomAfterMicrosoftCommonCrossTargetingTargets", "MSBuildProjectExtensionsPath",
                       "BaseIntermediateOutputPath", "IntermediateOutputPath", "MSBuildExtensionsPath",
                       "MSBuildExtensionsPath32", "MSBuildExtensionsPath64"}:
                raise InventoryError("Custom default Compile scope requires explicit review")
            if tag not in {"Compile", "ProjectReference"}: continue
            ancestor = node
            while ancestor is not None:
                if ancestor.get("Condition"):
                    raise InventoryError("Conditional project source/reference scope requires explicit review")
                ancestor = parent.get(ancestor)
            if tag == "ProjectReference":
                if set(node.attrib) != {"Include"} or list(node):
                    raise InventoryError("Unsupported project-reference metadata")
                references.append(literal_path(project.parent, node.get("Include"), ".csproj"))
            elif "Include" in node.attrib:
                if set(node.attrib) - {"Include", "Link"} or list(node):
                    raise InventoryError("Unsupported linked Compile metadata")
                linked.append(literal_path(project.parent, node.get("Include"), ".cs"))
            elif set(node.attrib) == {"Remove"} and not list(node):
                value = node.get("Remove").replace(chr(92), "/")
                # Only literal files or a literal subtree removal are supported.
                subtree = value.endswith("/**")
                base = value[:-3] if subtree else value
                if not base or any(x in base for x in ("$", "@", "%", "*", "?", ";")):
                    raise InventoryError("Dynamic Compile removal requires explicit review")
                removals.append((contained(project.parent/base), subtree))
            else:
                raise InventoryError("Unsupported Compile scope operation")
        defaults = set()
        for directory, subdirs, names in os.walk(project.parent, followlinks=False):
            for name in list(subdirs):
                child = Path(directory)/name
                if child.is_symlink() or getattr(child, "is_junction", lambda: False)():
                    raise InventoryError("Reparse point in default Compile source scope")
                if name in {"bin", "obj"} or name.startswith("."): subdirs.remove(name)
            for name in names:
                if Path(name).suffix.lower() != ".cs" or name.startswith("."): continue
                candidate = contained(Path(directory)/name)
                if not any(candidate == excluded or (subtree and candidate.is_relative_to(excluded))
                           for excluded, subtree in removals):
                    defaults.add(candidate)
        files.update(defaults)
        files.update(linked)
        pending.extend(references)
    return sorted(projects), sorted(files)


def discover_closed_scripts(root: Path) -> dict[str, Path]:
    """Closed reviewed owner PS/SQL surface, including support scripts with no SQL.

    This is deliberately a byte-bound review gate, not a permissive SQL detector.
    A new file or any changed factory/support body requires a reviewed manifest edit.
    """
    result = {}
    scopes = (("Deployments/local-stack", {".ps1", ".psm1", ".psd1", ".sql"}),
              ("Deployments/database", {".sql"}),
              ("Tools", {".ps1", ".psm1", ".psd1", ".sql"}))
    for directory, extensions in scopes:
        base = root/directory
        if not base.is_dir(): raise InventoryError("Missing closed owner-script source scope")
        for path in base.rglob("*"):
            parts = path.relative_to(base).parts
            if directory == "Tools" and (set(parts).intersection({"bin", "obj", "Zeka.OwnerSqlContractTests"})
                    or any(part.endswith(".Tests") for part in parts[:-1])):
                continue
            if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
                raise InventoryError("Reparse point in closed owner-script source scope")
            if path.is_file() and path.suffix.lower() in extensions:
                result[path.relative_to(root).as_posix()] = path
    return result


def _unique_members(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise InventoryError("Duplicate closed-surface manifest member")
        result[key] = value
    return result


def source_graph_sha256(bindings: dict[str, str]) -> str:
    """Canonical exact path-set/byte binding; full inputs remain in the inventory."""
    canonical = json.dumps(bindings, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def validate_referenced_source_boundary(root: Path, bindings: dict[str, str]) -> set[str]:
    """Fail closed before excluding reviewed service-runtime SQL from OWNER receipts.

    An unchanged runtime routine can become reachable from a changed caller. Hence
    the complete source/project graph, including all callers and OwnerSqlExecution,
    is sealed before any individual runtime-only classification is considered.
    This is a reviewed source boundary, not general C# reachability analysis.
    """
    manifest = json.loads(public_bytes(root/SOURCE_BOUNDARY), object_pairs_hook=_unique_members)
    if (not isinstance(manifest, dict)
            or set(manifest) != {"schema", "reviewPolicy", "baseRevision", "sourceGraphSha256", "runtimeOnlySources"}
            or manifest["schema"] != "zeka-owner-sql-referenced-source-boundary/v1"
            or manifest["reviewPolicy"] != SOURCE_BOUNDARY_POLICY
            or not isinstance(manifest["baseRevision"], str)
            or not re.fullmatch(r"[0-9a-f]{40}", manifest["baseRevision"])
            or not isinstance(manifest["sourceGraphSha256"], str)
            or not re.fullmatch(r"[0-9a-f]{64}", manifest["sourceGraphSha256"])
            or not isinstance(manifest["runtimeOnlySources"], dict)):
        raise InventoryError("Referenced source boundary contract rejected")
    if source_graph_sha256(bindings) != manifest["sourceGraphSha256"]:
        raise InventoryError("Referenced compiled source/project bytes or file set changed; explicit review required")
    for relative, item in manifest["runtimeOnlySources"].items():
        if (relative not in bindings or not relative.startswith("Services/") or not relative.endswith(".cs")
                or not isinstance(item, dict) or set(item) != {"sha256", "classification", "reason"}
                or item["classification"] != "service-runtime-only"
                or not isinstance(item["sha256"], str) or item["sha256"] != bindings[relative]
                or not isinstance(item["reason"], str) or not item["reason"].strip()):
            raise InventoryError("Runtime-only referenced source classification rejected")
    return set(manifest["runtimeOnlySources"])


def validate_closed_scripts(root: Path) -> dict[str, str]:
    manifest = json.loads(public_bytes(root/CLOSED_SURFACE), object_pairs_hook=_unique_members)
    if (not isinstance(manifest, dict) or set(manifest) != {"schema", "reviewPolicy", "files"}
            or manifest["schema"] != "zeka-owner-sql-closed-surface/v1"
            or manifest["reviewPolicy"] != "Every file-set or byte change requires explicit source review and manifest acceptance; no automatic enrollment."
            or not isinstance(manifest["files"], dict)):
        raise InventoryError("Closed owner-script manifest contract rejected")
    files = discover_closed_scripts(root)
    declared = manifest["files"]
    if set(files) != set(declared):
        raise InventoryError("Closed owner-script file set changed; explicit review required")
    required = {QUERY_FILE, *BOOTSTRAPS, *(p for paths in DEPENDENCIES.values() for p in paths)}
    if not required.issubset(declared): raise InventoryError("Closed required owner-script source missing")
    kinds = {"query-factory", "delegated-query-factory", "bootstrap-batch",
             "reviewed-orchestration-support", "separate-lifecycle-fixture-sql"}
    bindings = {CLOSED_SURFACE: digest(root/CLOSED_SURFACE)}
    for relative, path in sorted(files.items()):
        item = declared[relative]
        if (not isinstance(item, dict) or set(item) != {"sha256", "classification"}
                or not isinstance(item["classification"], str) or item["classification"] not in kinds
                or not isinstance(item["sha256"], str) or not re.fullmatch(r"[0-9a-f]{64}", item["sha256"])):
            raise InventoryError("Closed owner-script classification/hash rejected")
        actual = digest(path)
        if actual != item["sha256"]:
            raise InventoryError("Closed owner-script bytes changed; explicit review required")
        bindings[relative] = actual
    # Fixed query/body and support-script structure is reviewed at these exact byte hashes.
    # Even a hidden second query under an existing producer ID changes bytes and fails here.
    if declared[QUERY_FILE]["classification"] != "query-factory":
        raise InventoryError("Closed query-factory classification rejected")
    for path in (p for paths in DEPENDENCIES.values() for p in paths):
        if declared[path]["classification"] != "delegated-query-factory":
            raise InventoryError("Closed delegated-factory classification rejected")
    for path in BOOTSTRAPS:
        if declared[path]["classification"] != "bootstrap-batch":
            raise InventoryError("Closed bootstrap classification rejected")
    return bindings

def recheck_source_sets(root: Path, expected_graph_paths: set[str], expected_script_paths: set[str]) -> None:
    """Detect late source additions/removals as well as cached-input byte changes."""
    tool_projects, tool_files = discover_tools(root)
    projects, closure_files = discover_project_sources(root, tool_projects + [root/p for p in SERVICE_PROJECTS])
    paths = set(projects) | set(tool_files) | set(closure_files) | {root/p for p in EXTRA} | {root/"Tools/Shared/OwnerSqlExecution.cs"}
    actual = {path.relative_to(root).as_posix() for path in paths}
    if actual != expected_graph_paths:
        raise InventoryError("Referenced source/project file set changed during inventory")
    if set(discover_closed_scripts(root)) != expected_script_paths:
        raise InventoryError("Closed owner-script file set changed during inventory")


def build_inventory(root: Path) -> dict:
    # Keep established owner tools mandatory, but never use that minimum as a scope ceiling.
    for directory in TOOLS:
        if not (root/directory).is_dir(): raise InventoryError("Missing source scope: " + directory)
    tool_projects, files = discover_tools(root)
    core = "Tools/Shared/OwnerSqlExecution.cs"
    files = [p for p in files if p.relative_to(root).as_posix() != core]
    files.extend(root / p for p in EXTRA)
    producers, bindings = [], validate_closed_scripts(root)
    script_paths = set(bindings) - {CLOSED_SURFACE}
    # Seal every default/linked/transitive source and project before any runtime
    # classification: a changed owner caller must not invoke unchanged excluded SQL.
    projects, closure_files = discover_project_sources(root, tool_projects + [root/p for p in SERVICE_PROJECTS])
    files.extend(closure_files)
    files.append(root/core)
    graph_bindings, source_data = {}, {}
    for path in sorted(set(files) | set(projects)):
        relative = path.relative_to(root).as_posix()
        if not path.is_file() or path.is_symlink(): raise InventoryError("Missing/non-regular source: " + relative)
        data = public_bytes(path)
        graph_bindings[relative] = hashlib.sha256(data).hexdigest()
        if path.suffix.lower() == ".cs": source_data[relative] = data
    runtime_only = validate_referenced_source_boundary(root, graph_bindings)
    bindings.update(graph_bindings)
    bindings[SOURCE_BOUNDARY] = digest(root/SOURCE_BOUNDARY)
    for relative, data in sorted(source_data.items()):
        if relative == core or relative in runtime_only: continue
        producers.extend(csharp_producers(relative, data.decode("utf-8-sig")))
    qpath = root / QUERY_FILE
    qtext = public_bytes(qpath).decode("utf-8-sig")
    ids = set(re.findall(r"['\"](owner\.[a-z.-]+\.v\d+)['\"]", qtext))
    if ids != set(QUERY_IDS + CONTROL_IDS):
        raise InventoryError("PowerShell query/control identifier set changed; review inventory contract")
    bindings[QUERY_FILE] = digest(qpath)
    for identity in QUERY_IDS + CONTROL_IDS:
        item = {"producerId": identity, "source": QUERY_FILE, "kind": "powershell-query"}
        deps = DEPENDENCIES.get(identity, ())
        if deps:
            item["sourceBindings"] = {p: digest(root/p) for p in deps}
            bindings.update(item["sourceBindings"])
        producers.append(item)
    for source in BOOTSTRAPS:
        bindings[source] = digest(root/source)
        producers.append({"producerId": "bootstrap:"+source+":batch", "source": source, "kind": "bootstrap-batch"})
    for item in producers: item["producerSha256"] = bindings[item["source"]]
    if len({p["producerId"] for p in producers}) != len(producers):
        raise InventoryError("Multiple executions share one physical producer line; split the calls")
    if not any(p["kind"] == "csharp-callsite" for p in producers): raise InventoryError("Empty C# SQL inventory")
    # Recapture metadata/file sets after scanning: a late default-compiled helper
    # or owner script must not escape merely because existing bytes are unchanged.
    recheck_source_sets(root, set(graph_bindings), script_paths)
    # Recheck cached scan identities after the bounded graph read has completed.
    for relative in bindings:
        if digest(root/relative) != bindings[relative]: raise InventoryError("Source changed during inventory")
    return {"schema": "zeka-owner-sql-inventory/v1", "producers": producers, "sourceBindings": bindings,
            "scope": {"directories": ["Tools"], "requiredToolDirectories": list(TOOLS), "excludedFixtureDirectories": ["*.Tests", "Tools/Zeka.OwnerSqlContractTests", "bin", "obj"], "files": list(EXTRA), "linkedSourceProjects": [p.relative_to(root).as_posix() for p in projects], "sourceDiscovery": "reviewed exact source graph plus conservative SDK default-compile and recursive literal ProjectReference closure; unsupported build scope fails closed", "sourceGraphManifest": SOURCE_BOUNDARY, "sourceGraphSha256": source_graph_sha256(graph_bindings), "sourceGraphBindingPaths": sorted(graph_bindings), "runtimeOnlySources": sorted(runtime_only), "closedScriptManifest": CLOSED_SURFACE},
            "separateObligations": ["EF-generated migrations and migration-history conformance",
                                    "PL/pgSQL branch behavior and provider-specific security semantics"]}

def compare(inventory: dict, records: list[dict]) -> dict:
    expected = {p["producerId"]: p for p in inventory["producers"]}
    allowed_fields = {"schema","producerId","producerSha256","querySha256","serverMajor","executionCompleted",
                      "outcome","sqlState","processId","observedAtUtc","sourceBindings"}
    failures = []
    terminals: dict[tuple[str, int, str, str], tuple[str, set[str]]] = {}
    ambiguous = set()
    for index, receipt in enumerate(records):
        # Never echo a receipt or arbitrary caller-supplied identity into the report.
        label = "receipt["+str(index)+"]"
        if not isinstance(receipt, dict): failures.append(label+": not an object"); continue
        if set(receipt)-allowed_fields:
            failures.append(label+": unapproved receipt fields (no SQL/parameters/private locators allowed)");continue
        identity = receipt.get("producerId")
        producer = expected.get(identity) if isinstance(identity,str) else None
        if not producer: failures.append(label+": unknown producer"); continue
        schema = receipt.get("schema")
        v2 = schema == SCHEMA_V2
        outcome = receipt.get("outcome")
        allowed_outcomes = ("success", "postgres_error", "cancelled") if v2 else ("success", "postgres_error")
        expected_completion = outcome != "cancelled"
        if (schema not in (SCHEMA, SCHEMA_V2)
            or (v2 and (producer.get("kind") != "csharp-callsite" or producer.get("source") not in AUTH_EF_FILES))
            or type(receipt.get("serverMajor")) is not int or receipt["serverMajor"] != 17
            or outcome not in allowed_outcomes or receipt.get("executionCompleted") is not expected_completion
            or receipt.get("producerSha256") != producer["producerSha256"]
            or not isinstance(receipt.get("querySha256"), str)
            or not re.fullmatch(r"[0-9a-f]{64}", receipt["querySha256"])):
            failures.append(label+": schema/source/provider/execution mismatch"); continue
        if producer.get("kind") == "bootstrap-batch" and receipt["querySha256"] != producer["producerSha256"]:
            failures.append(label+": bootstrap query/source digest mismatch"); continue
        deps = producer.get("sourceBindings", {})
        if not isinstance(receipt.get("sourceBindings",{}),dict) or receipt.get("sourceBindings",{}) != deps:
            failures.append(label+": delegated producer binding mismatch"); continue
        if type(receipt.get("processId")) is not int or receipt["processId"] <= 0 or not isinstance(receipt.get("observedAtUtc"),str):
            failures.append(label+": missing native process/time provenance"); continue
        stamp = receipt["observedAtUtc"]
        if not re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)",stamp):
            failures.append(label+": invalid UTC observation timestamp");continue
        try:
            if datetime.fromisoformat(stamp.replace("Z","+00:00")).utcoffset() != timedelta(0):
                raise ValueError()
        except ValueError:
            failures.append(label+": invalid UTC observation timestamp");continue
        state = receipt.get("sqlState")
        if ((state is not None and (not isinstance(state, str) or not re.fullmatch(r"[A-Z0-9]{5}", state)))
            or (outcome != "postgres_error" and state is not None)
            or (v2 and ("sqlState" not in receipt
                        or (outcome == "postgres_error" and (state is None or state.startswith("00")))))):
            failures.append(label+": invalid SQLSTATE/outcome metadata");continue

        # The native provenance identifies a terminal observation. Normalize UTC spelling
        # while retaining the seventh fractional digit from .NET's round-trip timestamp.
        second, _, fraction = stamp.removesuffix("Z").removesuffix("+00:00").partition(".")
        terminal = (identity, receipt["processId"], second, fraction.ljust(7, "0"))
        prior = terminals.get(terminal)
        if prior is None:
            terminals[terminal] = (schema, {outcome})
        elif v2 or prior[0] == SCHEMA_V2:
            failures.append(label+": duplicate or conflicting terminal receipt")
            ambiguous.add(terminal)
        else:
            # Repeated v1 observations retain the historical comparison behavior.
            prior[1].add(outcome)

    successful, observed_errors = set(), set()
    for terminal, (_, outcomes) in terminals.items():
        if terminal in ambiguous:
            continue
        if "success" in outcomes:
            successful.add(terminal[0])
        if "postgres_error" in outcomes:
            observed_errors.add(terminal[0])
    missing = sorted(set(expected)-successful)
    return {"schema":"zeka-owner-sql-coverage/v1", "status":"PASS" if not failures and not missing else "FAIL",
            "expectedProducerCount":len(expected), "successfulProducerCount":len(successful),
            "missingSuccessfulProducers":missing, "postgresErrorProducers":sorted(observed_errors),
            "invalidReceiptReasons":failures, "separateObligations":inventory["separateObligations"],
            "claim":"bounded known-source handwritten producer execution coverage only; reflection/dynamic SQL semantics and separate obligations are not discharged"}

def read_records(paths: list[Path]) -> list[dict]:
    rows = []
    expanded = []
    for path in paths:
        if path.is_dir(): candidates = sorted(path.glob("owner-sql-*.jsonl"))
        elif path.is_file(): candidates = [path]
        else: candidates = [Path(p) for p in sorted(glob.glob(str(path), recursive=True))]
        if not candidates: raise InventoryError("No receipt files matched an explicit input")
        expanded.extend(candidates)
    for path in sorted(set(expanded)):
        text = public_bytes(path).decode("utf-8-sig")
        if text.lstrip().startswith("["):
            value = json.loads(text)
            if not isinstance(value,list): raise InventoryError("Receipt document is not an array")
            rows.extend(value)
        else:
            rows.extend(json.loads(line) for line in text.splitlines() if line.strip())
    return rows

def main() -> int:
    global SCANNER
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", required=True, type=Path)
    parser.add_argument("--sonar", help="Required deterministic scanner executable; defaults to sonar on PATH")
    parser.add_argument("--inventory-out", type=Path)
    parser.add_argument("--receipts", nargs="*", type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if args.sonar: SCANNER = args.sonar
    try:
        inventory = build_inventory(args.source_root.resolve())
        if args.inventory_out: args.inventory_out.write_text(json.dumps(inventory,indent=2)+"\n",encoding="utf-8")
        if args.receipts is not None:
            result = compare(inventory, read_records(args.receipts))
            if args.report: args.report.write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
            print(json.dumps({k:result[k] for k in ("status","expectedProducerCount","successfulProducerCount")}))
            return 0 if result["status"] == "PASS" else 2
        print(json.dumps({"status":"INVENTORIED", "producerCount":len(inventory["producers"])}))
        return 0
    except (InventoryError, OSError, ValueError, UnicodeError) as exc:
        # Error strings are solely controlled descriptors, not file/SQL contents.
        print(json.dumps({"status":"FAIL", "reason":str(exc) if isinstance(exc,InventoryError) else type(exc).__name__}))
        return 2

if __name__ == "__main__":
    raise SystemExit(main())
