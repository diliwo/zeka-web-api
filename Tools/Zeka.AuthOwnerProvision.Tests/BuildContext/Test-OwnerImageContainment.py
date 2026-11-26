"""Local-only image containment proof. No real credentials, database, push, pull or cleanup."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shutil
import subprocess
import sys
import tarfile
import time
import uuid

BASE_IMAGE = "mcr.microsoft.com/dotnet/aspnet:8.0.29"
RECEIPT_KEYS = {"schema", "status", "code", "stage", "exitCode", "sourceCommit", "binarySha256",
                "subjectId", "organisationId", "membershipId", "authorityKind", "auditMarker"}

class Refusal(Exception):
    pass

def require(condition, code):
    if not condition:
        raise Refusal(code)

def digest(path):
    with open(path, "rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()

def outside(path, repository):
    require(path.is_absolute() and ".." not in path.parts, "ABSOLUTE_NONTRAVERSING_PATH_REQUIRED")
    logical = str(path).replace("\\", "/").lower().rstrip("/")
    require("/zeka/m1/private" not in logical, "PRIVATE_PATH_REJECTED")
    for parent in (path, *path.parents):
        if parent.exists():
            info = parent.lstat()
            require(not parent.is_symlink() and not getattr(info, "st_file_attributes", 0) & 0x400,
                    "REPARSE_PATH_REJECTED")
    resolved = path.resolve()
    require(resolved != repository and repository not in resolved.parents, "REPOSITORY_PATH_REJECTED")
    private = os.environ.get("ZEKA_M1_PRIVATE_DIR")
    if private and Path(private).is_absolute():
        protected = Path(private).resolve()
        require(resolved != protected and protected not in resolved.parents, "PRIVATE_PATH_REJECTED")
    return resolved

class Native:
    def __init__(self, receipt):
        self.receipt = receipt
        self.parent = dict(os.environ)
        self.environment = dict(self.parent)
        removed = [name for name in self.environment if re.fullmatch(
            r"(?:PGPASSWORD|POSTGRES_PASSWORD|ConnectionStrings__.+)", name, re.IGNORECASE)]
        for name in removed:
            del self.environment[name]
        receipt["childEnvironmentNamesRemoved"] = sorted(removed)

    def run(self, operation, arguments, timeout=40, payload=None, expected=0, output=None):
        started = time.monotonic()
        item = {"operation": operation, "nativeExit": None, "deadlineSeconds": timeout}
        self.receipt["operations"].append(item)
        options = {"input": payload, "stdout": subprocess.PIPE if output is None else output,
                   "stderr": subprocess.PIPE, "timeout": timeout, "env": self.environment,
                   "creationflags": subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0}
        try:
            result = subprocess.run(arguments, **options)
            item.update(nativeExit=result.returncode, elapsedMs=round((time.monotonic() - started) * 1000),
                        stdoutBytes=len(result.stdout or b""), stderrBytes=len(result.stderr or b""))
        except subprocess.TimeoutExpired:
            item["deadlineExceeded"] = True
            raise Refusal("NATIVE_DEADLINE_" + operation)
        require(result.returncode == expected, "NATIVE_EXIT_" + operation)
        return result

    def docker(self, operation, *arguments, **options):
        return self.run(operation, ["docker", "--context", "default", *arguments], **options)

    def inspect(self, operation, kind, identifier, template):
        return json.loads(self.docker(operation, kind, "inspect", "--format", template, identifier).stdout)

def helper_check(native, helper, context, receipt, expected=0):
    command = ("$ErrorActionPreference='Stop';$r=[Console]::In.ReadToEnd()|ConvertFrom-Json;"
               ". $r.helper -DefinitionsOnly;try{Assert-OwnerHelperPreparedContext $r.context $r.receipt;"
               "@{status='PASS'}|ConvertTo-Json -Compress}catch{"
               "$c=if($_.Exception-is[InvalidOperationException]-and$_.Exception.Message-cmatch'^OWNER_HELPER_[A-Z_]+$')"
               "{$_.Exception.Message}else{'OWNER_HELPER_ASSERTION_FAILED'};"
               "@{status='BLOCKED';code=$c}|ConvertTo-Json -Compress;exit 1}")
    data = json.dumps({"helper": str(helper), "context": str(context), "receipt": receipt}).encode()
    result = native.run("context-preflight", ["pwsh", "-NoProfile", "-Command", command],
                        payload=data, expected=expected)
    proof = json.loads(result.stdout)
    require(proof == ({"status": "PASS"} if expected == 0 else
                      {"status": "BLOCKED", "code": "OWNER_HELPER_UNEXPECTED_FILE"}), "CONTEXT_GUARD_RECEIPT")

def prepare(native, helper, publish, context, source, binary, expected=0):
    result = native.run("prepare-context", ["pwsh", "-NoProfile", "-File", str(helper),
        "-PublishDirectory", str(publish), "-ContextDirectory", str(context),
        "-SourceCommit", source, "-BinarySha256", binary], expected=expected)
    receipt = json.loads(result.stdout)
    if expected:
        require(receipt == {"status": "BLOCKED", "code": "OWNER_HELPER_UNEXPECTED_FILE"}, "PUBLISH_GUARD_RECEIPT")
    return receipt

def contains(stream, marker, deadline):
    tail = b""
    while True:
        require(time.monotonic() < deadline, "LAYER_INSPECTION_DEADLINE")
        chunk = stream.read(1024 * 1024)
        if not chunk:
            return False
        combined = tail + chunk
        if marker in combined:
            return True
        tail = combined[-len(marker):]

def inspect_layers(archive_path, image, layers, base_layers, forbidden, marker):
    files = owned = inherited_directories = 0
    base_directories = {}
    deadline = time.monotonic() + 300
    allowed = {"app/" + item["name"] for item in image["runtimeFiles"]}
    with tarfile.open(archive_path, "r:*") as archive:
        manifest = json.load(archive.extractfile("manifest.json"))
        require(len(manifest) == 1 and len(manifest[0]["Layers"]) == len(layers), "IMAGE_MANIFEST_LAYERS")
        config_bytes = archive.extractfile(manifest[0]["Config"]).read()
        require("sha256:" + hashlib.sha256(config_bytes).hexdigest() == image["imageId"], "IMAGE_CONFIG_DIGEST")
        require(json.loads(config_bytes)["rootfs"]["diff_ids"] == layers, "IMAGE_CONFIG_ROOTFS")
        for index, name in enumerate(manifest[0]["Layers"]):
            require(not PurePosixPath(name).is_absolute() and ".." not in PurePosixPath(name).parts,
                    "UNSAFE_LAYER_PATH")
            with tarfile.open(fileobj=archive.extractfile(name), mode="r:*") as layer:
                for member in layer:
                    require(time.monotonic() < deadline, "LAYER_INSPECTION_DEADLINE")
                    path = member.name.removeprefix("./").rstrip("/")
                    require(forbidden not in path, "FORBIDDEN_FILENAME_IN_LAYER")
                    directory_metadata = (member.uid, member.gid, member.mode)
                    if index < len(base_layers) and member.isdir():
                        base_directories[path] = directory_metadata
                    if index >= len(base_layers) and path:
                        # BuildKit's RUN mount setup can repeat the inherited empty /etc directory.
                        # Admit only unchanged base metadata, never an added file/link or another directory.
                        inherited_etc = (path == "etc" and member.isdir() and member.size == 0
                                         and base_directories.get(path) == directory_metadata)
                        require((member.isdir() and path == "app") or (member.isfile() and path in allowed)
                                or inherited_etc, "UNEXPECTED_OWN_LAYER_ENTRY")
                        inherited_directories += int(inherited_etc)
                        owned += int(member.isfile())
                    if member.isfile():
                        files += 1
                        require(not contains(layer.extractfile(member), marker, deadline), "FORBIDDEN_SENTINEL_IN_LAYER")
    return {"layerCount": len(layers), "filesScanned": files, "ownFileEntries": owned,
            "inheritedEtcDirectoryMetadataEntries": inherited_directories,
            "forbiddenFilenameFound": False, "forbiddenSentinelFound": False, "archiveSha256": digest(archive_path)}

def create(native, image, name, run_id, receipt):
    result = native.docker("create", "create", "--name", name, "--interactive", "--network", "none",
        "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid,nodev,size=16777216,mode=1777", "--cap-drop", "ALL",
        "--security-opt", "no-new-privileges", "--log-driver", "none",
        "--label", "com.diliwo.zeka.scope=owner-containment-test", "--label", "com.diliwo.zeka.operation-id=" + run_id,
        image).stdout.decode().strip()
    require(re.fullmatch(r"[0-9a-f]{64}", result), "CONTAINER_ID_REJECTED")
    receipt["retainedContainers"].append(result)
    facts = native.inspect("container-admission", "container", result,
        '{"Id":{{json .Id}},"State":{{json .State.Status}},"User":{{json .Config.User}},'
        '"Network":{{json .HostConfig.NetworkMode}},"Readonly":{{json .HostConfig.ReadonlyRootfs}},'
        '"Labels":{{json .Config.Labels}}}')
    require(facts["Id"] == result and facts["State"] == "created" and facts["User"] == "app" and
            facts["Network"] == "none" and facts["Readonly"] and
            facts["Labels"].get("com.diliwo.zeka.operation-id") == run_id, "CONTAINER_ADMISSION_REJECTED")
    return result

def runtime_case(native, image, name, run_id, summary, payload, expected_exit, code, stage, bound=False):
    container = create(native, image, name, run_id, summary)
    result = native.docker("runtime-negative", "start", "-a", "-i", container,
                           payload=payload, expected=expected_exit, timeout=40)
    require(not result.stderr, "RUNTIME_STDERR_UNEXPECTED")
    proof = json.loads(result.stdout)
    require(set(proof) == RECEIPT_KEYS and proof["schema"] == "zeka-m1-owner-provision-result/v1" and
            proof["status"] == "BLOCKED" and proof["code"] == code and proof["stage"] == stage and
            proof["exitCode"] == expected_exit and proof["subjectId"] is None and
            proof["organisationId"] is None and proof["membershipId"] is None and
            proof["authorityKind"] == "OwnerAttestedLocalSynthetic" and
            proof["auditMarker"] == "operator-provisioned, no agreements", "RUNTIME_RECEIPT_REJECTED")
    require(proof["sourceCommit"] == (summary["sourceCommit"] if bound else None) and
            proof["binarySha256"] == (summary["binarySha256"] if bound else None), "RUNTIME_SOURCE_BINDING")
    summary["runtimeChecks"].append(proof)

def main(args, summary):
    repo = Path(__file__).resolve().parents[3]
    publish, evidence = outside(Path(args.publish), repo), outside(Path(args.evidence), repo)
    require(publish.is_dir() and not evidence.exists() and publish not in evidence.parents and
            evidence not in publish.parents, "INPUT_DIRECTORY_BOUNDARY")
    evidence.mkdir(parents=True)
    summary["evidenceDirectory"] = str(evidence)
    native = Native(summary)
    head = native.run("source-head", ["git", "-C", str(repo), "rev-parse", "HEAD"]).stdout.decode().strip()
    require(head == args.source_commit, "SOURCE_HEAD_MISMATCH")
    summary["worktreeClean"] = not native.run("source-status", ["git", "-C", str(repo), "status", "--porcelain"]).stdout.strip()
    require(summary["worktreeClean"], "SOURCE_WORKTREE_DIRTY")
    engine = native.docker("engine-identity", "info", "--format", "{{.ID}}").stdout.decode().strip()
    require(engine and (not args.expected_engine_id or engine == args.expected_engine_id), "ENGINE_ID_REJECTED")
    summary["dockerEngineId"] = engine
    base_id = native.inspect("cached-base", "image", BASE_IMAGE, "{{json .Id}}")
    base_layers = native.inspect("base-layers", "image", base_id, "{{json .RootFS.Layers}}")
    summary["baseImageId"] = base_id
    helper = repo / "Tools/Zeka.AuthOwnerProvision/Prepare-OwnerHelperBuildContext.ps1"
    clean_context = evidence / "context-clean"
    prepared = prepare(native, helper, publish, clean_context, args.source_commit, args.binary_sha256)
    require(prepared["status"] == "PREPARED" and prepared["sourceCommit"] == args.source_commit and
            prepared["binarySha256"] == args.binary_sha256, "PREPARED_CONTEXT_BINDING")
    summary["contextReceipt"] = prepared
    runtime_files = {item["name"]: item["sha256"] for item in prepared["runtimeFiles"]}
    require(len(runtime_files) == 31, "RUNTIME_INVENTORY_COUNT")
    run_id = uuid.uuid4().hex
    forbidden, marker = "forbidden-owner-context-" + run_id + ".txt", ("NONSECRET_FORBIDDEN_FIXTURE_" + run_id).encode()
    dirty_publish = evidence / "publish-forbidden"
    shutil.copytree(publish, dirty_publish)
    (dirty_publish / forbidden).write_bytes(marker)
    prepare(native, helper, dirty_publish, evidence / "context-must-not-exist", args.source_commit, args.binary_sha256, 1)
    require(not (evidence / "context-must-not-exist").exists(), "DIRTY_PUBLISH_CREATED_CONTEXT")
    dirty_context = evidence / "context-forbidden"
    shutil.copytree(clean_context, dirty_context)
    (dirty_context / forbidden).write_bytes(marker)
    helper_check(native, helper, dirty_context, prepared, 1)
    summary["preflightNegatives"] = {"dirtyPublishRefused": True, "dirtyContextRefused": True}
    images, layer_sets = [], []
    for label, context in (("clean", clean_context), ("forbidden", dirty_context)):
        if label == "clean":
            helper_check(native, helper, context, prepared)
        # The forbidden build deliberately bypasses the rejecting TEST preflight to prove the real ignore defense.
        tag = "zeka-owner-containment:" + args.source_commit[:12] + "-" + run_id[:12] + "-" + label
        native.docker("build-" + label, "build", "--pull=false", "--network=none", "--file", str(context / "Dockerfile"),
            "--build-arg", "SOURCE_COMMIT=" + args.source_commit, "--build-arg", "BINARY_SHA256=" + args.binary_sha256,
            "--tag", tag, str(context), timeout=180)
        image_id = native.inspect("image-identity", "image", tag, "{{json .Id}}")
        layers = native.inspect("image-layers", "image", image_id, "{{json .RootFS.Layers}}")
        require(layers[:len(base_layers)] == base_layers, "UNEXPECTED_BASE_LAYERS")
        image = {"imageId": image_id, "tag": tag, "runtimeFiles": prepared["runtimeFiles"]}
        summary["retainedImages"].append(image)
        images.append(image)
        layer_sets.append(layers)
    require(layer_sets[0] == layer_sets[1], "DIRTY_CONTEXT_CHANGED_FILESYSTEM_LAYERS")
    summary["identicalCleanAndNegativeRootFS"] = True
    exported = create(native, images[0]["imageId"], "zeka-owner-export-" + run_id, run_id, summary)
    rootfs = evidence / "image-rootfs.tar"
    with rootfs.open("wb") as target:
        native.docker("export-rootfs", "export", exported, output=target, timeout=300)
    observed = {}
    rootfs_deadline = time.monotonic() + 300
    with tarfile.open(rootfs, "r:*") as archive:
        for member in archive:
            require(time.monotonic() < rootfs_deadline, "ROOTFS_INSPECTION_DEADLINE")
            path = member.name.removeprefix("./").rstrip("/")
            require(forbidden not in path, "FORBIDDEN_FILENAME_IN_ROOTFS")
            if path.startswith("app/"):
                require(member.isfile() and path[4:] in runtime_files, "UNEXPECTED_APP_FILE")
                require(path[4:] not in observed, "DUPLICATE_APP_FILE")
                observed[path[4:]] = hashlib.file_digest(archive.extractfile(member), "sha256").hexdigest()
    require(observed == runtime_files, "APP_FILE_HASH_INVENTORY_MISMATCH")
    summary["appInventory"] = {"files": len(observed), "sha256": observed, "rootfsArchiveSha256": digest(rootfs)}
    scanned = {}
    for label, image, layers in zip(("clean", "forbidden"), images, layer_sets):
        if image["imageId"] not in scanned:
            archive_path = evidence / ("image-" + label + ".tar")
            native.docker("save-image", "image", "save", "--output", str(archive_path), image["imageId"], timeout=300)
            scanned[image["imageId"]] = inspect_layers(archive_path, image, layers, base_layers, forbidden, marker)
        summary["layerChecks"][label] = scanned[image["imageId"]]
    runtime_case(native, images[0]["imageId"], "zeka-owner-invalid-" + run_id, run_id, summary,
                 b"{}\n", 2, "INPUT_SCHEMA_REJECTED", "admission")
    password = secrets.token_urlsafe(32) + "Aa1!"
    sid = "S-1-5-21-111111111-222222222-333333333-1001"
    request = dict(schema="zeka-m1-owner-provision/v1", scope="LocalSyntheticM1OwnerProvision", project="zeka-m1",
        authorityKind="OwnerAttestedLocalSynthetic", ownerDecisionId="a7daf3ea-6370-4604-9e4b-e5eb206465c7",
        approvalCommentId="6529021741", ownerAccount="DILI_LP\\herve", expectedOwnerSid=sid, actualOwnerSid=sid,
        expectedOwnerProfile="C:\\Users\\herve", actualOwnerProfile="C:\\Users\\herve", normalizedEmail="HERVE.MBILO@GMAIL.COM",
        sourceCommit=args.source_commit, binarySha256=args.binary_sha256, databaseSourceCommit=args.source_commit,
        databaseName="zeka_m1_auth", initializationId="containment_" + run_id, subjectId=str(uuid.uuid4()),
        displayName="Network isolated synthetic boundary", slug=None, idempotencyKey="containment-" + run_id,
        processingTimeoutSeconds=120, retentionSeconds=86400, ownerPassword=password,
        authRuntimeConnectionString="Host=postgres;Port=5432;Database=zeka_m1_auth;Username=zeka_auth_runtime;Password="
        + secrets.token_urlsafe(32) + ";Timeout=2;Command Timeout=2;Pooling=false;SSL Mode=Disable")
    runtime_case(native, images[0]["imageId"], "zeka-owner-boundary-" + run_id, run_id, summary,
                 json.dumps(request).encode(), 3, "RUNTIME_IDENTITY_REJECTED", "database-identity", True)
    summary["parentEnvironmentUnchanged"] = dict(os.environ) == native.parent
    require(summary["parentEnvironmentUnchanged"], "PARENT_ENVIRONMENT_CHANGED")
    summary.update(status="PASS", actualDatabaseAccess=False, realCredentialsUsed=False, provisioningExecuted=False,
                   imagesPublished=False, cleanupExecuted=False)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("publish", "evidence", "source-commit", "binary-sha256"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--expected-engine-id")
    arguments = parser.parse_args()
    result = {"schema": "zeka-m1-owner-image-containment/v1", "status": "FAIL",
              "sourceCommit": arguments.source_commit, "binarySha256": arguments.binary_sha256,
              "operations": [], "retainedImages": [], "retainedContainers": [], "runtimeChecks": [], "layerChecks": {}}
    try:
        require(re.fullmatch(r"[0-9a-f]{40}", arguments.source_commit) and
                re.fullmatch(r"[0-9a-f]{64}", arguments.binary_sha256), "SOURCE_BINDING_FORMAT")
        main(arguments, result)
    except Exception as error:
        result["failure"] = str(error) if isinstance(error, Refusal) else "UNEXPECTED_" + type(error).__name__
    if "evidenceDirectory" in result:
        (Path(result["evidenceDirectory"]) / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result))
    sys.exit(0 if result["status"] == "PASS" else 1)
