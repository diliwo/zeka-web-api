#!/usr/bin/env bash
set -euo pipefail
set +x

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_dir}/../.." && pwd)"
env_file="${ZEKA_NUGET_ENV_FILE:-${repository_root}/Deployments/build/local/.op/nuget.env}"

if ! command -v op >/dev/null 2>&1; then
    echo "1Password CLI (op) is required." >&2
    exit 1
fi

if [[ ! -f "${env_file}" ]]; then
    echo "Missing 1Password environment file: ${env_file}" >&2
    echo "Copy Deployments/build/local/.op/nuget.env.example to nuget.env and configure its references." >&2
    exit 1
fi

exec op run --env-file="${env_file}" -- bash -c '
    set -euo pipefail
    set +x
    : "${GITHUB_PACKAGES_USERNAME:?GITHUB_PACKAGES_USERNAME is required}"
    : "${GITHUB_PACKAGES_TOKEN:?GITHUB_PACKAGES_TOKEN is required}"

    export NuGetPackageSourceCredentials_github="Username=${GITHUB_PACKAGES_USERNAME};Password=${GITHUB_PACKAGES_TOKEN};ValidAuthenticationTypes=Basic"
    unset GITHUB_PACKAGES_TOKEN

    exec dotnet restore "$@"
' bash "${repository_root}/zeka-web-api.sln" "$@"
