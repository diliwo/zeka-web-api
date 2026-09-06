#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_dir}/../.." && pwd)"
credential_pattern='<[[:space:]]*packageSourceCredentials([[:space:]>])|key[[:space:]]*=[[:space:]]*"(ClearTextPassword|Password)"'
violation_found=0

while IFS= read -r -d '' tracked_path; do
    file_name="${tracked_path##*/}"
    if [[ "${file_name,,}" != "nuget.config" ]]; then
        continue
    fi

    if [[ -f "${repository_root}/${tracked_path}" ]] \
        && LC_ALL=C grep -Eiq "${credential_pattern}" "${repository_root}/${tracked_path}"; then
        echo "ERROR: NuGet credential material detected in working-tree file ${tracked_path}" >&2
        violation_found=1
    fi

    if LC_ALL=C git -C "${repository_root}" grep --cached -Iqi -E \
        "${credential_pattern}" -- "${tracked_path}"; then
        echo "ERROR: NuGet credential material detected in indexed file ${tracked_path}" >&2
        violation_found=1
    fi
done < <(git -C "${repository_root}" ls-files -z)

if (( violation_found != 0 )); then
    echo "Remove packageSourceCredentials and Password/ClearTextPassword entries from tracked NuGet configuration." >&2
    exit 1
fi

echo "Tracked NuGet configuration contains no prohibited credential entries."
