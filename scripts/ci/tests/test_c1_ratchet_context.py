"""Exercise the C1 event and installation contract on real isolated Git histories."""

from copy import deepcopy
from hashlib import sha256
import json
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from unittest import TestCase, main

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from c1_ratchet_context import (  # noqa: E402
    AUTHORITY_KEYS, BASELINE, BRANCH, INSTALL_PATHS, LANES, POLICY_PATHS, REPOSITORY, RESOLVER,
    TARGET_REF, WORKFLOW, ContextError, Git, policy_bundle, resolve, verify_tooling,
)


SOURCE = Path(__file__).resolve().parents[3]


class ContextTests(TestCase):
    @classmethod
    def setUpClass(cls):
        temp = TemporaryDirectory(prefix="c1-ratchet-git-")
        cls.addClassCleanup(temp.cleanup)
        cls.cwd = Path(temp.name)
        cls.git("init", "--quiet", "-b", "fixture")
        cls.git("config", "user.email", "c1-context-fixture@example.invalid")
        cls.git("config", "user.name", "C1 Context Fixture")
        cls.git("config", "commit.gpgsign", "false")
        cls.git("config", "core.autocrlf", "false")
        cls.git("config", "core.hooksPath", str(cls.cwd / "no-hooks"))
        cls.repo = Git(cls.cwd)
        for path in set(POLICY_PATHS) - set(INSTALL_PATHS):
            cls.write(path, f"Reviewed shared input {path}\n".encode())
        cls.virgin = cls.commit("Virgin protected target")
        cls.git("checkout", "--quiet", "--detach", cls.virgin)
        for path in set(INSTALL_PATHS) - {BASELINE, RESOLVER, WORKFLOW}:
            cls.write(path, f"Reviewed installation input {path}\n".encode())
        cls.write(BASELINE, (SOURCE / BASELINE).read_bytes())
        cls.workflow = (SOURCE / WORKFLOW).read_bytes()
        cls.write(WORKFLOW, cls.workflow)
        resolver_source = (SOURCE / RESOLVER).read_bytes()
        before = b'if __name__ == "__main__":\n'
        after = before + b'    if "C1_TEST_SENTINEL" in os.environ:\n        Path(os.environ["C1_TEST_SENTINEL"]).touch()\n'
        if resolver_source.count(before) != 1:
            raise RuntimeError("Test resolver sentinel anchor unavailable.")
        cls.write(RESOLVER, resolver_source.replace(before, after))
        cls.reviewed = cls.commit("Reviewed installation source")
        cls.installed = cls.merge(cls.virgin, cls.reviewed, cls.repo.tree(cls.reviewed))
        cls.git("checkout", "--quiet", "--detach", cls.installed)
        cls.write("candidate.txt", b"ordinary application or test patch\n")
        cls.ordinary = cls.commit("Ordinary post-installation candidate")

    @classmethod
    def git(cls, *arguments):
        return subprocess.run(["git", *arguments], cwd=cls.cwd, check=True, capture_output=True,
                              text=True, timeout=30).stdout.strip()

    @classmethod
    def write(cls, name, content):
        path = cls.cwd / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)

    @classmethod
    def commit(cls, message):
        cls.git("add", "-A")
        cls.git("commit", "--quiet", "-m", message)
        return cls.git("rev-parse", "HEAD")

    @classmethod
    def merge(cls, base, head, tree=None):
        return cls.git("commit-tree", tree or cls.repo.tree(head), "-p", base, "-p", head,
                       "-m", f"Synthetic evaluation of {head} against {base}")

    def authority(self, base=None, source=None):
        base = base or self.virgin
        source = source or self.reviewed
        entries = self.repo.bundle_entries(source)
        authority = {
            "schema": 1, "repository": REPOSITORY, "ref": TARGET_REF,
            "base": base, "baseTree": self.repo.tree(base),
            "source": source, "sourceTree": self.repo.tree(source),
            "bundleSha256": sha256(policy_bundle(self.repo, source)).hexdigest(),
            "resolverSha256": sha256(self.repo.blob(entries[RESOLVER][2])).hexdigest(),
            "baselineSha256": sha256(self.repo.blob(entries[BASELINE][2])).hexdigest(),
        }
        self.assertEqual(set(authority), set(AUTHORITY_KEYS))
        return authority

    def common_env(self, event_name, event_sha, ref, tip, authority=None):
        env = {
            "GITHUB_REPOSITORY": REPOSITORY, "GITHUB_EVENT_NAME": event_name,
            "GITHUB_REF": ref, "GITHUB_SHA": event_sha,
            "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/{WORKFLOW}@{ref}",
            "GITHUB_WORKFLOW_SHA": event_sha, "GITHUB_RUN_ID": "27", "GITHUB_RUN_ATTEMPT": "2",
            "C1_RATCHET_PROTECTED_TARGET": TARGET_REF, "C1_RATCHET_PROTECTED_HEAD": tip,
        }
        if authority is not None:
            env["C1_RATCHET_BOOTSTRAP_AUTHORITY"] = json.dumps(authority)
        return env

    def pr(self, base, head, tip=None, authority=None, event_sha=None):
        event_sha = event_sha or self.merge(base, head)
        self.git("checkout", "--quiet", "--detach", event_sha)
        topic = "codex/fixture-candidate"
        env = self.common_env("pull_request", event_sha, "refs/pull/111/merge", tip or base, authority)
        env.update(GITHUB_BASE_REF=BRANCH, GITHUB_HEAD_REF=topic)
        event = {
            "action": "synchronize", "number": 111, "repository": {"full_name": REPOSITORY},
            "pull_request": {
                "number": 111, "state": "open", "merged": False,
                "head": {"sha": head, "ref": topic, "repo": {"full_name": REPOSITORY}},
                "base": {"sha": base, "ref": BRANCH, "repo": {"full_name": REPOSITORY}},
            },
        }
        return event, env

    def push(self, before, after, tip=None, authority=None):
        self.git("checkout", "--quiet", "--detach", after)
        env = self.common_env("push", after, TARGET_REF, tip or after, authority)
        event = {
            "ref": TARGET_REF, "before": before, "after": after, "created": False, "deleted": False,
            "forced": False, "repository": {"full_name": REPOSITORY},
        }
        return event, env

    def changed(self, parent, name, content):
        self.git("checkout", "--quiet", "--detach", parent)
        if content is None:
            (self.cwd / name).unlink()
        else:
            self.write(name, content)
        return self.commit(f"Change fixture {name}")

    def context(self, event, env):
        return json.loads(resolve(self.repo, event, env)["context"])

    def assert_refused(self, code, event, env):
        with self.assertRaisesRegex(ContextError, f"^{code}$"):
            self.context(event, env)

    def test_reviewed_initial_pr_binds_fixed_run_and_reviewed_bundle(self):
        authority = self.authority()
        event, env = self.pr(self.virgin, self.reviewed, authority=authority)
        result = resolve(self.repo, event, env)
        self.assertEqual(json.loads(result["matrix"]), {"role": ["base", "candidate"], "lane": list(LANES)})
        self.assertEqual(json.loads(result["context"]), {
            "schema": 2, "event": "pull_request", "candidate": self.reviewed, "base": self.virgin,
            "testTree": authority["sourceTree"], "baseTree": authority["baseTree"], "runId": "27",
            "runAttempt": "2", "repository": REPOSITORY, "mode": "bootstrap", "tooling": self.reviewed,
            "policySha256": authority["bundleSha256"], "baselineSha256": authority["baselineSha256"],
        })

    def test_initialization_requires_separate_approval(self):
        event, env = self.pr(self.virgin, self.reviewed)
        self.assert_refused("BOOTSTRAP_AUTHORITY_MISSING", event, env)

    def test_initialization_rejects_unreviewed_same_tree_commit(self):
        alternate = self.git("commit-tree", self.repo.tree(self.reviewed), "-p", self.virgin,
                             "-m", "Unreviewed commit with byte-identical tree")
        event, env = self.pr(self.virgin, alternate, authority=self.authority())
        self.assert_refused("BOOTSTRAP_UNREVIEWED_OR_STALE_PR", event, env)

    def test_initialization_rejects_each_incorrect_external_digest(self):
        authority = self.authority()
        event, env = self.pr(self.virgin, self.reviewed, authority=authority)
        for key, code in (
            ("bundleSha256", "BOOTSTRAP_POLICY_DIGEST_MISMATCH"),
            ("resolverSha256", "BOOTSTRAP_POLICY_DIGEST_MISMATCH"),
            ("baselineSha256", "INVALID_BOOTSTRAP_AUTHORITY"),
        ):
            with self.subTest(key=key):
                env["C1_RATCHET_BOOTSTRAP_AUTHORITY"] = json.dumps({**authority, key: "a" * 64})
                self.assert_refused(code, event, env)

    def test_initialization_rejects_an_unknown_virgin_base_tree(self):
        authority = {**self.authority(), "baseTree": "a" * 40}
        event, env = self.pr(self.virgin, self.reviewed, authority=authority)
        self.assert_refused("INVALID_BOOTSTRAP_AUTHORITY", event, env)

    def test_reviewed_installation_push_recognizes_exact_source_normal_merge_and_fast_forward(self):
        for after in (self.reviewed, self.installed):
            with self.subTest(after=after):
                event, env = self.push(self.virgin, after, authority=self.authority())
                result = self.context(event, env)
                self.assertEqual(result["mode"], "bootstrap")
                self.assertEqual((result["base"], result["candidate"], result["tooling"]),
                                 (self.virgin, after, self.reviewed))

    def test_unreviewed_installation_push_rejects_changed_merge_tree(self):
        after = self.merge(self.virgin, self.reviewed, self.repo.tree(self.ordinary))
        event, env = self.push(self.virgin, after, authority=self.authority())
        self.assert_refused("BOOTSTRAP_UNREVIEWED_OR_STALE_PUSH", event, env)

    def test_initialization_refuses_replay_after_current_target_installation(self):
        event, env = self.pr(self.virgin, self.reviewed, tip=self.installed, authority=self.authority())
        self.assert_refused("BOOTSTRAP_UNREVIEWED_OR_STALE_PR", event, env)
        event, env = self.push(self.virgin, self.installed, tip=self.ordinary, authority=self.authority())
        self.assert_refused("BOOTSTRAP_UNREVIEWED_OR_STALE_PUSH", event, env)

    def test_installed_target_uses_exact_base_or_before_and_ignores_retired_grant(self):
        for build_event in (self.pr, self.push):
            with self.subTest(event=build_event.__name__):
                event, env = build_event(self.installed, self.ordinary)
                env["C1_RATCHET_BOOTSTRAP_AUTHORITY"] = "invalid and retired"
                result = self.context(event, env)
                self.assertEqual((result["mode"], result["tooling"], result["base"]),
                                 ("installed", self.installed, self.installed))

    def test_partial_deletion_does_not_reopen_bootstrap(self):
        broken = self.changed(self.installed, BASELINE, None)
        event, env = self.pr(broken, self.ordinary, authority=self.authority())
        self.assert_refused("INSTALLED_POLICY_MISSING_OR_DELETED", event, env)

    def test_complete_deletion_and_branch_readdition_do_not_reopen_bootstrap(self):
        self.git("checkout", "--quiet", "--detach", self.installed)
        self.git("rm", "--quiet", "--", *INSTALL_PATHS)
        deinstalled = self.commit("Remove all installation paths")
        self.assertFalse(self.repo.entries(deinstalled, INSTALL_PATHS))
        event, env = self.pr(deinstalled, self.ordinary, authority=self.authority())
        self.assert_refused("BOOTSTRAP_PERMANENTLY_RETIRED", event, env)
        # A re-addition with byte-for-byte accepted files still cannot override recorded lineage.
        readded = self.merge(deinstalled, self.reviewed)
        event, env = self.push(deinstalled, readded, authority=self.authority(deinstalled))
        self.assert_refused("BOOTSTRAP_PERMANENTLY_RETIRED", event, env)

    def test_candidate_baseline_verifier_policy_and_workflow_tampering_fail(self):
        for tamper in (BASELINE, "scripts/ci/c1_ratchet.py", RESOLVER, WORKFLOW,
                       "scripts/ci/c1_ratchet_trx.py", "scripts/ci/run-c1-ratchet.sh",
                       "scripts/ci/run-production-conformance.sh",
                       "Deployments/database/production-conformance.targets.json", "Directory.Build.props"):
            with self.subTest(path=tamper):
                candidate = self.changed(self.ordinary, tamper, b"unapproved policy rewrite\n")
                event, env = self.pr(self.installed, candidate)
                self.assert_refused("CANDIDATE_EXECUTION_POLICY_CHANGED", event, env)

    def test_candidate_policy_deletion_fails_even_with_valid_base(self):
        candidate = self.changed(self.ordinary, BASELINE, None)
        event, env = self.pr(self.installed, candidate)
        self.assert_refused("INCOMPLETE_EXECUTION_POLICY", event, env)

    def test_pr_event_rejects_other_repository_wrong_base_and_synthetic_parent_drift(self):
        event, env = self.pr(self.installed, self.ordinary)
        foreign = deepcopy(event)
        foreign["pull_request"]["head"]["repo"]["full_name"] = "somewhere/foreign"
        self.assert_refused("PR_REF_OR_REPOSITORY_MISMATCH", foreign, env)
        self.assert_refused("PR_REF_OR_REPOSITORY_MISMATCH", event, {**env, "GITHUB_BASE_REF": "develop"})
        reversed_parent_merge = self.merge(self.ordinary, self.installed)
        event, env = self.pr(self.installed, self.ordinary, event_sha=reversed_parent_merge)
        self.assert_refused("PR_SYNTHETIC_MERGE_MISMATCH", event, env)

    def test_unrelated_executed_workflow_is_rejected_even_when_its_blob_matches(self):
        event, env = self.pr(self.installed, self.ordinary)
        env["GITHUB_WORKFLOW_SHA"] = self.installed
        self.assert_refused("EXECUTED_WORKFLOW_POLICY_MISMATCH", event, env)

    def test_push_event_rejects_unprotected_created_deleted_forced_and_mismatched_events(self):
        event, env = self.push(self.installed, self.ordinary)
        for key, value in (("forced", True), ("created", True), ("deleted", True), ("after", self.reviewed)):
            with self.subTest(key=key):
                self.assert_refused("INVALID_PROTECTED_PUSH", {**event, key: value}, env)
        self.assert_refused("UNAPPROVED_TARGET", event, {**env, "C1_RATCHET_PROTECTED_TARGET": "develop"})
        self.assert_refused("INVALID_EVENT_COMMIT", {**event, "before": "0" * 40}, env)

    def test_event_and_workflow_must_bind_exact_checked_out_commit(self):
        event, env = self.pr(self.installed, self.ordinary)
        self.assert_refused("EVENT_CHECKOUT_MISMATCH", event, {**env, "GITHUB_SHA": self.reviewed})
        self.assert_refused("EXECUTED_WORKFLOW_POLICY_MISMATCH", event,
                            {**env, "GITHUB_WORKFLOW_REF": f"{REPOSITORY}/{WORKFLOW}@refs/heads/develop"})

    def test_tooling_checkout_uses_reviewed_head_and_refuses_changed_on_disk_policy(self):
        event, env = self.push(self.installed, self.ordinary)
        context = self.context(event, env)
        with self.assertRaisesRegex(ContextError, "^TOOLING_CHECKOUT_IDENTITY_MISMATCH$"):
            verify_tooling(context, self.cwd)
        self.git("checkout", "--quiet", "--detach", self.installed)
        verify_tooling(context, self.cwd)
        self.addCleanup(self.git, "restore", "--", RESOLVER)
        self.write(RESOLVER, b"locally tampered resolver\n")
        with self.assertRaisesRegex(ContextError, "^CHANGED_TOOLING_CHECKOUT_BYTES$"):
            verify_tooling(context, self.cwd)

    def test_tooling_checkout_rejects_baseline_and_bundle_receipt_changes(self):
        event, env = self.push(self.installed, self.ordinary)
        context = self.context(event, env)
        self.git("checkout", "--quiet", "--detach", self.installed)
        for key in ("baselineSha256", "policySha256"):
            with self.subTest(field=key):
                with self.assertRaisesRegex(ContextError, "^TOOLING_CHECKOUT_DIGEST_MISMATCH$"):
                    verify_tooling({**context, key: "a" * 64}, self.cwd)

    def run_workflow_loader(self, event, env, destination):
        workflow = self.workflow.decode("utf-8")
        start = "          python3 -I - <<'PY'\n"
        body, ending, _ = workflow.split(start, maxsplit=1)[1].partition("\n          PY\n")
        self.assertEqual(ending, "\n          PY\n")
        lines = body.splitlines()
        self.assertTrue(all(not line or line.startswith(" " * 10) for line in lines))
        loader = "\n".join(line[10:] for line in lines) + "\n"
        destination.mkdir()
        event_file = destination / "event.json"
        event_file.write_text(json.dumps(event), encoding="utf-8")
        run = subprocess.run(
            [sys.executable, "-I", "-"], cwd=self.cwd, input=loader, check=False,
            capture_output=True, text=True, timeout=90,
            env={**os.environ, "C1_RATCHET_BOOTSTRAP_AUTHORITY": "", **env,
                 "GITHUB_EVENT_PATH": str(event_file), "GITHUB_OUTPUT": str(destination / "outputs"),
                 "C1_TEST_SENTINEL": str(destination / "resolver-started"), "RUNNER_TEMP": str(destination)},
        )
        return run

    def test_real_inline_loader_authenticates_before_bootstrap_resolver_execution(self):
        authority = self.authority()
        event, env = self.pr(self.virgin, self.reviewed, authority=authority)
        with TemporaryDirectory(prefix="c1-bootstrap-loader-") as temporary:
            staging = Path(temporary)
            for field in ("resolverSha256", "bundleSha256", "baselineSha256", "source"):
                with self.subTest(field=field):
                    wrong = {**authority, field: self.ordinary if field == "source" else "a" * 64}
                    wrong_env = {**env, "C1_RATCHET_BOOTSTRAP_AUTHORITY": json.dumps(wrong)}
                    failed = staging / field
                    run = self.run_workflow_loader(event, wrong_env, failed)
                    self.assertEqual(run.returncode, 1, run.stderr)
                    self.assertFalse((failed / "resolver-started").exists())
                    self.assertFalse((failed / "outputs").exists())
            approved = staging / "approved"
            run = self.run_workflow_loader(event, env, approved)
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertTrue((approved / "resolver-started").exists())
            outputs = dict(line.split("=", maxsplit=1) for line in
                           (approved / "outputs").read_text(encoding="utf-8").splitlines())
            self.assertEqual(json.loads(outputs["context"])["mode"], "bootstrap")
            self.assertEqual(outputs["tooling"], self.reviewed)

    def test_real_inline_loader_selects_base_resolver_for_installed_push(self):
        event, env = self.push(self.installed, self.ordinary)
        with TemporaryDirectory(prefix="c1-installed-loader-") as temporary:
            staging = Path(temporary) / "installed"
            run = self.run_workflow_loader(event, env, staging)
            self.assertEqual(run.returncode, 0, run.stderr)
            self.assertTrue((staging / "resolver-started").exists())
            outputs = dict(line.split("=", maxsplit=1) for line in
                           (staging / "outputs").read_text(encoding="utf-8").splitlines())
            self.assertEqual(json.loads(outputs["context"])["mode"], "installed")
            self.assertEqual(outputs["tooling"], self.installed)

    def test_cli_reads_payload_and_writes_only_expected_output_names(self):
        event, env = self.pr(self.virgin, self.reviewed, authority=self.authority())
        with TemporaryDirectory(prefix="c1-ratchet-event-") as temporary:
            path = Path(temporary)
            event_file = path / "event.json"
            output_file = path / "runner-output"
            event_file.write_text(json.dumps(event), encoding="utf-8")
            run = subprocess.run(
                [sys.executable, "-I", str(SOURCE / RESOLVER)], cwd=self.cwd, check=False,
                capture_output=True, text=True, timeout=30,
                env={**os.environ, **env, "GITHUB_EVENT_PATH": str(event_file), "GITHUB_OUTPUT": str(output_file)},
            )
            self.assertEqual(run.returncode, 0, run.stderr)
            output_lines = output_file.read_text(encoding="utf-8").splitlines()
            self.assertEqual({line.partition("=")[0] for line in output_lines},
                             {"candidate", "base", "tooling", "context", "matrix"})
            context_file = path / "context.json"
            clean_checkout = path / "tooling"
            context_file.write_text(json.dumps(self.context(event, env)), encoding="utf-8")
            self.git("worktree", "add", "--quiet", "--detach", str(clean_checkout), self.reviewed)
            self.addCleanup(self.git, "worktree", "prune")
            check = subprocess.run(
                [sys.executable, "-I", str(SOURCE / RESOLVER), "verify-tooling",
                 str(context_file), str(clean_checkout)], check=False, capture_output=True,
                text=True, timeout=30,
            )
            self.assertEqual(check.returncode, 0, check.stderr)


if __name__ == "__main__":
    main()
