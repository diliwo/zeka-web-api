# Private package restore

The root `nuget.config` defines `nuget.org` and the private `github` source. It
must not contain a `packageSourceCredentials` section or any password entry.
NuGet authentication is supplied only to the restore process through an
environment variable resolved by 1Password CLI (`op`).

## Credential-owner prerequisite

An authorized credential owner must first revoke or otherwise render the
previously exposed credential unusable. Any replacement must be explicitly
authorized, limited to package read access and only the repositories/packages
required for `Zeka.Extensions.*`, and stored in the approved 1Password vault.
Repository configuration cannot prove a token's actual GitHub permissions, so
the owner must verify those permissions at the credential source.

Store the replacement token in a 1Password password/credential field. Do not
store a token in this repository, a shell profile, a NuGet config file, or a
command argument.

## One-time local setup

1. Install 1Password CLI using the official instructions and sign in.
2. Verify the intended account with `op whoami`.
3. Create the ignored local environment file:

   ```bash
   cp Deployments/build/local/.op/nuget.env.example \
      Deployments/build/local/.op/nuget.env
   chmod 600 Deployments/build/local/.op/nuget.env
   ```

4. Set `GITHUB_PACKAGES_USERNAME` to the GitHub account associated with the
   authorized credential. Set `GITHUB_PACKAGES_TOKEN` to its `op://` secret
   reference, not to the token value.

The local `nuget.env` path is ignored by Git. The tracked example contains
placeholders only.

## Restore

From the repository root, run:

```bash
./scripts/security/restore-private-packages.sh --force --no-cache
```

The wrapper runs `op run`, constructs NuGet's supported
`NuGetPackageSourceCredentials_github` variable inside the child process, and
then executes `dotnet restore`. The source suffix `github` deliberately matches
the key in `nuget.config`. The credential is neither persisted to a NuGet config
nor placed in a process argument.

To use a local env file at another location, set `ZEKA_NUGET_ENV_FILE` to its
path. This variable identifies a file only and must not contain a secret.

## Repository guard

Run the tracked-config guard before committing changes to NuGet configuration:

```bash
./scripts/security/check-nuget-credentials.sh
```

The guard inspects the working-tree and indexed copies of tracked files named
`nuget.config` without printing their contents. It fails if a credentials
section or NuGet password key is present. It is intentionally repository-local
so a later CI issue can invoke it without this issue selecting a CI identity or
workflow architecture.
