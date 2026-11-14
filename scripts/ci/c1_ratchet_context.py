"""Bind a ratchet event to an authenticated installation or the exact protected base."""

from hashlib import sha256
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys


REPOSITORY = "diliwo/zeka-web-api"
BRANCH = "codex/m1-c1-integration"
TARGET_REF = f"refs/heads/{BRANCH}"
WORKFLOW = ".github/workflows/c1-validation.yml"
RESOLVER = "scripts/ci/c1_ratchet_context.py"
BASELINE = "scripts/ci/c1-ratchet/baseline-v1.json"
INITIAL_BASELINE_SHA256 = "81e9f33157c0933d892e22042d63939d35cef8e6ab9aff8ed968d1230e48f85b"
INSTALL_PATHS = (
    WORKFLOW,
    BASELINE,
    "scripts/ci/c1-ratchet/baseline.schema.json",
    "scripts/ci/c1_ratchet.py",
    RESOLVER,
    "scripts/ci/c1_ratchet_jobs.py",
    "scripts/ci/c1_ratchet_trx.py",
    "scripts/ci/run-c1-ratchet.sh",
)
HISTORY_PATHS = tuple(path for path in INSTALL_PATHS if "/c1-ratchet/" not in path) + (
    "scripts/ci/c1-ratchet",
)
POLICY_PATHS = tuple(sorted(INSTALL_PATHS + (
    ".github/workflows/mvp-validation.yml",
    "Deployments/database/production-conformance.targets.json",
    "Directory.Build.props",
    "global.json",
    "scripts/ci/nuget.strict.config",
    "scripts/ci/run-production-conformance.sh",
    "scripts/security/check-nuget-credentials.sh",
)))
LANES = (
    "non-postgres", "postgresql", "sonar-suite", "production-conformance", "owner-provision", "gateway",
)
PR_ACTIONS = ("opened", "reopened", "synchronize", "ready_for_review")
AUTHORITY_KEYS = (
    "schema", "repository", "ref", "base", "baseTree", "source", "sourceTree",
    "bundleSha256", "resolverSha256", "baselineSha256",
)


class ContextError(ValueError):
    """A safe diagnostic code, independent of payload contents."""


def require(condition, code):
    if not condition:
        raise ContextError(code)


def object_id(value, length=40):
    return isinstance(value, str) and re.fullmatch(rf"[a-f0-9]{{{length}}}", value) and int(value, 16)


def object_pairs(pairs):
    value = dict(pairs)
    require(len(value) == len(pairs), "DUPLICATE_JSON_KEY")
    return value


def invalid_json_constant(_):
    raise ContextError("MALFORMED_JSON")


def read_json(contents, max_size, code):
    require(len(contents) <= max_size, code)
    try:
        return json.loads(contents, object_pairs_hook=object_pairs, parse_constant=invalid_json_constant)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ContextError(code) from error


class Git:
    def __init__(self, cwd):
        self.cwd = cwd

    def run(self, *arguments):
        result = subprocess.run(
            ["git", "--no-replace-objects", *arguments], cwd=self.cwd, check=False,
            capture_output=True, timeout=30, env={**os.environ, "GIT_NO_LAZY_FETCH": "1"},
        )
        require(result.returncode == 0, "GIT_OBJECT_OR_HISTORY_UNAVAILABLE")
        return result.stdout

    def text(self, *arguments):
        return self.run(*arguments).decode("ascii").strip()

    def verify_history(self):
        require(self.text("rev-parse", "--is-shallow-repository") == "false", "SHALLOW_HISTORY")
        grafts = Path(os.fsdecode(self.run("rev-parse", "--git-path", "info/grafts").rstrip(b"\r\n")))
        if not grafts.is_absolute():
            grafts = self.cwd / grafts
        require("GIT_GRAFT_FILE" not in os.environ and not grafts.exists() and not grafts.is_symlink(),
                "GIT_GRAFTS_PRESENT")

    def commit(self, revision):
        require(object_id(revision) and self.text("cat-file", "-t", revision) == "commit",
                "INVALID_EVENT_COMMIT")
        return revision

    def tree(self, commit):
        return self.text("rev-parse", "--verify", f"{commit}^{{tree}}")

    def parents(self, commit):
        return self.text("rev-list", "--parents", "-n", "1", commit).split()[1:]

    def require_ancestor(self, ancestor, descendant):
        if ancestor != descendant:
            require(self.text("merge-base", ancestor, descendant) == ancestor, "BASE_ANCESTRY_MISMATCH")

    def entries(self, revision, paths):
        data = self.run("ls-tree", "-z", "--full-tree", revision, "--", *paths)
        entries = {}
        for entry in data.split(b"\0")[:-1]:
            metadata, path = entry.decode("utf-8").split("\t", maxsplit=1)
            mode, kind, oid = metadata.split()
            entries[path] = (mode, kind, oid)
        return entries

    def bundle_entries(self, revision):
        entries = self.entries(revision, POLICY_PATHS)
        require(set(entries) == set(POLICY_PATHS), "INCOMPLETE_EXECUTION_POLICY")
        require(all(mode in ("100644", "100755") and kind == "blob" and object_id(oid)
                    for mode, kind, oid in entries.values()), "UNSAFE_EXECUTION_POLICY")
        return entries

    def blob(self, oid):
        return self.run("cat-file", "blob", oid)

    def has_installation_history(self, base):
        return bool(self.text("log", "--full-history", "-n", "1", "--format=%H", base, "--", *HISTORY_PATHS))


def bundle_manifest(repo, entries):
    return "".join(
        f"{path}\t{entries[path][0]}\t{sha256(repo.blob(entries[path][2])).hexdigest()}\n"
        for path in POLICY_PATHS
    ).encode("ascii")


def policy_bundle(repo, revision):
    """Return the canonical review manifest of exact path/mode/content identities."""
    return bundle_manifest(repo, repo.bundle_entries(revision))


def repository_name(event):
    repo = event.get("repository")
    return repo.get("full_name") if isinstance(repo, dict) else None


def read_event(repo, event, env):
    require(isinstance(event, dict) and repository_name(event) == REPOSITORY
            and env.get("GITHUB_REPOSITORY") == REPOSITORY
            and env.get("C1_RATCHET_PROTECTED_TARGET") == TARGET_REF, "UNAPPROVED_TARGET")
    event_sha = repo.commit(env.get("GITHUB_SHA"))
    require(repo.text("rev-parse", "--verify", "HEAD") == event_sha, "EVENT_CHECKOUT_MISMATCH")
    event_name = env.get("GITHUB_EVENT_NAME")
    if event_name == "pull_request":
        require(event.get("action") in PR_ACTIONS, "UNSUPPORTED_PR_ACTION")
        pull = event.get("pull_request")
        number = event.get("number")
        require(isinstance(pull, dict) and type(number) is int and number > 0
                and pull.get("number") == number and pull.get("state") == "open"
                and pull.get("merged") is False, "INVALID_PULL_REQUEST")
        head, target = pull.get("head"), pull.get("base")
        require(isinstance(head, dict) and isinstance(target, dict)
                and all(isinstance(side.get("repo"), dict)
                        and side["repo"].get("full_name") == REPOSITORY for side in (head, target))
                and target.get("ref") == BRANCH and env.get("GITHUB_BASE_REF") == BRANCH
                and isinstance(head.get("ref"), str) and head["ref"] not in ("", BRANCH)
                and env.get("GITHUB_HEAD_REF") == head["ref"]
                and env.get("GITHUB_REF") == f"refs/pull/{number}/merge", "PR_REF_OR_REPOSITORY_MISMATCH")
        base, candidate = repo.commit(target.get("sha")), repo.commit(head.get("sha"))
        require(base != candidate and repo.parents(event_sha) == [base, candidate],
                "PR_SYNTHETIC_MERGE_MISMATCH")
    else:
        require(event_name == "push" and env.get("GITHUB_REF") == TARGET_REF
                and event.get("ref") == TARGET_REF
                and event.get("created") is False and event.get("deleted") is False
                and event.get("forced") is False and event.get("after") == event_sha, "INVALID_PROTECTED_PUSH")
        base, candidate = repo.commit(event.get("before")), event_sha
        require(base != candidate, "INVALID_PROTECTED_PUSH")
        repo.require_ancestor(base, candidate)
    return event_name, event_sha, base, candidate


def bootstrap(repo, env, event, event_sha, base, candidate, tip):
    raw = env.get("C1_RATCHET_BOOTSTRAP_AUTHORITY", "")
    require(raw, "BOOTSTRAP_AUTHORITY_MISSING")
    authority = read_json(raw, 8192, "INVALID_BOOTSTRAP_AUTHORITY")
    require(isinstance(authority, dict) and set(authority) == set(AUTHORITY_KEYS)
            and type(authority["schema"]) is int and authority["schema"] == 1
            and authority["repository"] == REPOSITORY and authority["ref"] == TARGET_REF
            and authority["base"] == base and authority["baseTree"] == repo.tree(base)
            and all(object_id(authority[key], 64) for key in ("bundleSha256", "resolverSha256", "baselineSha256"))
            and authority["baselineSha256"] == INITIAL_BASELINE_SHA256, "INVALID_BOOTSTRAP_AUTHORITY")
    source = repo.commit(authority["source"])
    require(repo.tree(source) == authority["sourceTree"], "BOOTSTRAP_SOURCE_TREE_MISMATCH")
    repo.require_ancestor(base, source)
    if event == "pull_request":
        require(tip == base and candidate == source and repo.tree(event_sha) == authority["sourceTree"],
                "BOOTSTRAP_UNREVIEWED_OR_STALE_PR")
    else:
        require(tip == candidate and repo.tree(candidate) == authority["sourceTree"]
                and (candidate == source or repo.parents(candidate) == [base, source]),
                "BOOTSTRAP_UNREVIEWED_OR_STALE_PUSH")
    bundle = repo.bundle_entries(source)
    require(sha256(bundle_manifest(repo, bundle)).hexdigest() == authority["bundleSha256"]
            and sha256(repo.blob(bundle[RESOLVER][2])).hexdigest() == authority["resolverSha256"]
            and sha256(repo.blob(bundle[BASELINE][2])).hexdigest() == authority["baselineSha256"],
            "BOOTSTRAP_POLICY_DIGEST_MISMATCH")
    return source, bundle


def resolve(repo, event, env):
    """Return GitHub step outputs after independently checking every policy input."""
    repo.verify_history()
    event_name, event_sha, base, candidate = read_event(repo, event, env)
    tip = repo.commit(env.get("C1_RATCHET_PROTECTED_HEAD"))
    repo.run("rev-list", "--objects", "--missing=error", "--quiet", base, tip, event_sha)
    repo.require_ancestor(base if event_name == "pull_request" else candidate, tip)
    present = repo.entries(base, (*INSTALL_PATHS, "scripts/ci/c1-ratchet"))
    if not present:
        require(not repo.has_installation_history(base), "BOOTSTRAP_PERMANENTLY_RETIRED")
        mode = "bootstrap"
        tooling, accepted = bootstrap(repo, env, event_name, event_sha, base, candidate, tip)
    else:
        require(set(INSTALL_PATHS) <= present.keys(), "INSTALLED_POLICY_MISSING_OR_DELETED")
        mode, tooling = "installed", base
        accepted = repo.bundle_entries(tooling)

    require(repo.bundle_entries(candidate) == accepted and repo.bundle_entries(event_sha) == accepted,
            "CANDIDATE_EXECUTION_POLICY_CHANGED")
    workflow_sha = repo.commit(env.get("GITHUB_WORKFLOW_SHA"))
    workflow_ref = env.get("GITHUB_WORKFLOW_REF", "")
    prefix = f"{REPOSITORY}/{WORKFLOW}@"
    admitted_refs = [env["GITHUB_REF"]]
    if event_name == "pull_request" and workflow_sha == candidate:
        admitted_refs.append(f"refs/heads/{env['GITHUB_HEAD_REF']}")
    expected_workflow_shas = (event_sha, candidate) if event_name == "pull_request" else (event_sha,)
    require(workflow_sha in expected_workflow_shas
            and workflow_ref.startswith(prefix) and workflow_ref[len(prefix):] in admitted_refs
            and repo.entries(workflow_sha, (WORKFLOW,)).get(WORKFLOW) == accepted[WORKFLOW],
            "EXECUTED_WORKFLOW_POLICY_MISMATCH")
    run_id, attempt = env.get("GITHUB_RUN_ID"), env.get("GITHUB_RUN_ATTEMPT")
    require(all(isinstance(value, str) and re.fullmatch(r"[1-9][0-9]*", value)
                for value in (run_id, attempt)), "INVALID_RUN_IDENTITY")
    baseline_bytes = repo.blob(accepted[BASELINE][2])
    baseline = read_json(baseline_bytes, 1048576, "INVALID_BASELINE_POLICY")
    require(isinstance(baseline, dict) and isinstance(baseline.get("jobs"), dict)
            and set(baseline["jobs"]) == set(LANES), "INVALID_BASELINE_PROFILE_MATRIX")
    context = {
        "schema": 2, "event": event_name, "candidate": candidate, "base": base,
        "testTree": repo.tree(candidate), "baseTree": repo.tree(base),
        "runId": run_id, "runAttempt": attempt, "repository": REPOSITORY, "mode": mode,
        "tooling": tooling, "policySha256": sha256(bundle_manifest(repo, accepted)).hexdigest(),
        "baselineSha256": sha256(baseline_bytes).hexdigest(),
    }
    return {
        "candidate": candidate, "base": base, "tooling": tooling,
        "context": json.dumps(context, separators=(",", ":")),
        "matrix": json.dumps({"role": ["base", "candidate"], "lane": LANES}, separators=(",", ":")),
    }


def file_bytes(path, limit, code):
    with path.open("rb") as handle:
        value = handle.read(limit + 1)
    require(len(value) <= limit, code)
    return value


def unreparsed_file_status(path):
    status = path.lstat()
    require(not stat.S_ISLNK(status.st_mode)
            and not getattr(status, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT,
            "UNSAFE_TOOLING_PATH")
    return status.st_mode


def verify_tooling(context, tooling_path):
    """Verify an isolated checkout still corresponds to the selected policy and bytes."""
    require(isinstance(context, dict) and context.get("schema") == 2
            and context.get("mode") in ("bootstrap", "installed")
            and object_id(context.get("tooling")) and object_id(context.get("base"))
            and (context["mode"] == "bootstrap" or context["tooling"] == context["base"])
            and all(object_id(context.get(key), 64) for key in ("policySha256", "baselineSha256")),
            "INVALID_TOOLING_CONTEXT")
    tooling_path = Path(os.path.abspath(tooling_path))
    require(stat.S_ISDIR(unreparsed_file_status(tooling_path)), "UNSAFE_TOOLING_PATH")
    repo = Git(tooling_path)
    repo.verify_history()
    require(Path(os.fsdecode(repo.run("rev-parse", "--show-toplevel").rstrip(b"\r\n"))) == tooling_path
            and repo.text("rev-parse", "--verify", "HEAD") == context["tooling"],
            "TOOLING_CHECKOUT_IDENTITY_MISMATCH")
    entries = repo.bundle_entries(context["tooling"])
    require(sha256(bundle_manifest(repo, entries)).hexdigest() == context["policySha256"]
            and sha256(repo.blob(entries[BASELINE][2])).hexdigest() == context["baselineSha256"],
            "TOOLING_CHECKOUT_DIGEST_MISMATCH")
    for relative, (mode, _, oid) in entries.items():
        path = tooling_path / relative
        for parent in path.parents:
            if parent == tooling_path:
                break
            require(stat.S_ISDIR(unreparsed_file_status(parent)), "UNSAFE_TOOLING_PATH")
        actual = unreparsed_file_status(path)
        require(stat.S_ISREG(actual) and (os.name == "nt" or bool(actual & stat.S_IXUSR) == (mode == "100755")),
                "UNSAFE_TOOLING_PATH")
        expected = repo.blob(oid)
        require(file_bytes(path, len(expected), "CHANGED_TOOLING_CHECKOUT_BYTES") == expected,
                "CHANGED_TOOLING_CHECKOUT_BYTES")


def main():
    try:
        if len(sys.argv) == 4 and sys.argv[1] == "verify-tooling":
            context_file = Path(sys.argv[2])
            context = read_json(file_bytes(context_file, 8192, "INVALID_TOOLING_CONTEXT"),
                                8192, "INVALID_TOOLING_CONTEXT")
            verify_tooling(context, Path(sys.argv[3]))
        else:
            require(len(sys.argv) == 1, "INVALID_CONTEXT_COMMAND")
            event_file = Path(os.environ["GITHUB_EVENT_PATH"])
            event = read_json(file_bytes(event_file, 4194304, "INVALID_GITHUB_EVENT"),
                              4194304, "INVALID_GITHUB_EVENT")
            outputs = resolve(Git(Path.cwd()), event, os.environ)
            with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8", newline="\n") as output:
                output.writelines(f"{key}={value}\n" for key, value in outputs.items())
    except (ContextError, OSError, KeyError, UnicodeError, subprocess.SubprocessError) as error:
        code = str(error) if isinstance(error, ContextError) else "CONTEXT_IO_OR_ENVIRONMENT_UNAVAILABLE"
        print(f"C1 ratchet context refused: {code}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
