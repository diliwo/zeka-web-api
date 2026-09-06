# Issue #23 package credential history assessment

Assessment date: 2026-09-06

Assessment basis: public `diliwo/zeka-web-api` Git repository, synchronized
`develop` base `2e395d1a4e4bea5686a448a18ede4a4eb6873ed4`, GitHub branch/tag/fork
metadata, and value-suppressing searches of reachable Git objects. No secret
value was printed, copied, hashed, or included in this document.

## Findings

- The exposed entry is a classic GitHub personal access token attached to the
  `github` NuGet source (`https://nuget.pkg.github.com/diliwo/index.json`). The
  classification used the token format only; no value, hash, or length was
  emitted.
- The repository currently restores four package names from that source:
  `Zeka.Extensions.Authentication`, `Zeka.Extensions.EventBus`,
  `Zeka.Extensions.MultiTenant`, and `Zeka.Extensions.Observability` (across
  their referenced versions). This is the replacement credential's required
  package scope; the repository does not establish permission to any other
  package or repository.
- Git does not record the credential's actual GitHub permission scopes. Its
  intended use is package restore, but only the credential owner can confirm
  whether it was granted broader permissions.
- The credential-bearing block first appears in root `nuget.config` at commit
  `2a03465172fb6f1a245dfbc7d936d57446ce30b5`, authored on 2026-06-29.
- On the assessed remote refs, the exact affected commit set is the 26 commits
  in `2a03465172fb6f1a245dfbc7d936d57446ce30b5^..2e395d1a4e4bea5686a448a18ede4a4eb6873ed4`
  plus the divergent Dependabot branch tip
  `335a32faa2765bb414b3c5a731f4776078f1e179`: 27 unique reachable commits.
- The only credential-bearing path found in those commits is `nuget.config` at
  the repository root.
- The remote branch tips whose checked-out `nuget.config` still contains the
  credential are `develop` and
  `dependabot/nuget/Services/AdminAreaManagement/AdminAreaManagement.Application/AutoMapper-15.1.3`.
  No tags were present. GitHub reported zero forks at assessment time.
- The repository is public. Exposure began on 2026-06-29 and continues on the
  public remote branch tips until the remediation is merged and the stale
  Dependabot branch is updated or deleted. The credential must be treated as
  compromised. GitHub cannot report every clone, fetch, cache, mirror, or
  downloaded copy.

## Required containment

The authorized credential owner must revoke the exposed credential or otherwise
render it unusable and confirm that status to Chief/Hervé. If a replacement is
approved, it must be limited to the package-read access actually required and
stored only in the approved 1Password location. Repository remediation does not
revoke a credential and is not evidence of rotation.

After the remediation PR is merged, an authorized repository owner must also
update or delete the stale Dependabot branch. Otherwise its branch tip will
continue to present the credential in an ordinary checkout even though
`develop` is clean. Issue #23 does not authorize this implementation branch to
rewrite or delete that remote branch.

## History recommendation

Do not rewrite history as the containment action. Revocation is mandatory and
time-sensitive; rewriting cannot invalidate a credential or erase existing
clones, caches, mirrors, or downloaded objects.

After revocation is confirmed, the current evidence does not justify a history
rewrite on security grounds: there are no reported forks or tags, and removing
the credential from active branch tips prevents new users from receiving it in
ordinary working-tree checkouts. Retain the history unless Hervé identifies a
separate legal, contractual, or compliance requirement to purge the canonical
Git objects.

If a rewrite is separately approved, it must cover both affected branches and
all 27 reachable commits, require force-pushing rewritten refs, invalidate
commit SHAs, disrupt open branch/PR ancestry, and require collaborators to
re-clone or carefully rebase. GitHub caches and existing external copies may
still retain the old objects. No rewrite, force-push, branch/tag deletion, or
protection change is authorized by Issue #23.
