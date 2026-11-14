# C1 Failure Ratchet

The baseline records the ten current test identities and eleven admitted signatures in [C1-RATCHET-5ca8d97-v1](https://linear.app/zeka-saas/issue/ZEK-17#comment-0281b80b-a1a6-42d9-b8f8-02bd8d117a05). H1 remains an open historical obligation. A pass leaves the baseline unchanged.

The baseline also records the required test project for each CI profile, the expected initial base counts, and permitted Linux skips. The earlier CI did not retain complete test identities. CI reconstructs the named inventory by executing the exact base revision in the same profiles and comparing each base identity against the candidate. The first comparison accepts the independently reviewed policy bytes only at the pinned C1 anchor. After the baseline lands, the workflow reads accepted policy and tools from the exact base, or the previous ref for a direct C1 push.

A candidate may add tests. All new tests must pass and the previous base identities must still execute. Failed and skipped rows are explicit. The gate checks the native exit against each invocation's test outcomes and checks all project and job evidence. The comparison receipt includes the full sanitized policy diff. The current comparator rejects all policy changes. Adding/replacing signatures or removing obligations requires a separately reviewed change to the trusted policy and tooling with independently accepted evidence.

Existing MVP and Strict Restore remain separate. The Sonar suite profile exercises the same complete solution test command with its collector. The existing Sonar job still owns the analysis and Quality Gate. The focused gateway and owner profiles and Issue 46 project selections are recorded separately. Raw TRX, logs, dumps and attachments remain on the runner. Artifacts contain normalized test rows and hashes tied to the exact run.

## Required-check activation

1. Have Chief independently verify the source, baseline pin, complete named inventories and the live negative comparator receipt. Keep all existing required checks.
2. Protect the workflow and every other workflow that could emit the same check, the comparator and parser, baseline/schema, test runner, transitive execution policy, and the ownership file. In the current user-owned repository this requires a separately reviewed base-branch CODEOWNERS file, independent code-owner review, stale-approval dismissal and no bypass. An independently controlled required workflow is stronger where repository ownership supports it.
3. Validate equivalent-source direct C1 evidence after the workflow is in C1. Confirm the exact aggregate job appears for both events. Require **C1 Failure Ratchet** from the GitHub Actions provider on the C1 branch only after those checks pass.

Before that protection is enabled, a PR-controlled workflow can replace the workflow or emit the same named check. The first PR's immutable base and reviewed digest support independent inspection, while a PR author can replace the proposed verifier. An existing required check tied only to the GitHub Actions app does not prevent that. Keep the required-check activation and any baseline admission separate from this PR.

Use `python -m unittest discover -s scripts/ci/tests -p test_c1_ratchet.py -v` for the deterministic comparator fixtures.
