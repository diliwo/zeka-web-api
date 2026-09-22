#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
inventory="${repo_root}/Deployments/database/application-conformance.targets.json"
results_dir="${1:-${repo_root}/TestResults/application-conformance}"

expected=(
  CTX-01 CTX-02 CTX-03 CTX-04 CTX-05 CTX-06 CTX-07 AUTH-01
  XTEN-01 XTEN-02 XTEN-03 XTEN-04 REL-01 REL-02 ESC-01 ESC-02 ESC-03 ESC-04
  PAR-01 PAR-02 PAR-03 COV-01 COV-04
  NDB-01 NDB-02 NDB-04 NDB-06 NDB-07 NDB-08 NDB-09 NDB-10 NDB-11
)
mapfile -t actual < <(jq -r '.targets[] | select(.required == true) | .id' "${inventory}" | sort)
mapfile -t required < <(printf '%s\n' "${expected[@]}" | sort)
if ! diff -u <(printf '%s\n' "${required[@]}") <(printf '%s\n' "${actual[@]}"); then
  echo "Application-conformance target inventory is incomplete or contains an unauthorized target." >&2
  exit 1
fi

if ! jq -e '
  .schemaVersion == 1 and
  .statusVocabulary == ["passed","failed","blocked","untested"] and
  ([.targets[].id] | length == (unique | length)) and
  all(.targets[];
      .required == true and (.evidence | type == "array") and (.evidence | length > 0) and
      all(.evidence[];
        (.suite | IN("auth-infra","adminarea-unit","adminarea-infra","client-unit","client-infra")) and
        (.test | type == "string" and length > 0))) and
  ([.blockedBoundaries[].id] | length == (unique | length)) and
  all(.blockedBoundaries[];
      .status == "blocked" and (.reason | type == "string" and length > 0))
' "${inventory}" >/dev/null; then
  echo "Application-conformance target inventory failed structural validation." >&2
  exit 1
fi

mkdir -p "${results_dir}"
results_file="${results_dir}/target-results.json"
results_lines="${results_dir}/target-results.ndjson"
: >"${results_lines}"
projects=(
  "auth-infra|Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
  "adminarea-unit|Services/AdminAreaManagement/Tests/Application.UnitTests/Application.UnitTests.csproj"
  "adminarea-infra|Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
  "client-unit|Services/ClientManagement/Tests/Application.UnitTests/Application.UnitTests.csproj"
  "client-infra|Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
)

test_status=0
for entry in "${projects[@]}"; do
  suite=${entry%%|*}
  project=${entry#*|}
  echo "::group::Issue #46 application conformance: ${suite}"
  if ! dotnet test "${repo_root}/${project}" --configuration Release --no-restore \
      --filter 'Issue=46&Evidence=ApplicationConformance' \
      --logger "trx;LogFileName=${suite}.trx" --results-directory "${results_dir}"; then
    test_status=1
  fi
  trx="${results_dir}/${suite}.trx"
  if [[ ! -s "${trx}" ]] || ! grep -Eq 'total="[1-9][0-9]*"' "${trx}" \
      || ! grep -q 'failed="0"' "${trx}" || ! grep -q 'notExecuted="0"' "${trx}"; then
    echo "Required ${suite} conformance tests failed, skipped, or produced no executable evidence." >&2
    test_status=1
  fi
  echo "::endgroup::"
done

while IFS= read -r target; do
  target_status=passed
  target_reason=""
  mapfile -t target_evidence < <(jq -r --arg target "${target}" \
    '.targets[] | select(.id == $target) | .evidence[] | [.suite,.test] | join("|")' "${inventory}")
  for evidence in "${target_evidence[@]}"; do
    suite=${evidence%%|*}
    test_name=${evidence#*|}
    trx="${results_dir}/${suite}.trx"
    if [[ ! -s "${trx}" ]] || ! grep -Fq "${test_name}" "${trx}"; then
      target_status=failed
      target_reason="No executed ${suite} evidence was found for ${test_name}."
      test_status=1
      break
    fi
  done

  if [[ "${target_status}" == "passed" ]]; then
    jq -cn --arg id "${target}" --argjson evidence \
      "$(jq -c --arg target "${target}" '.targets[] | select(.id == $target) | .evidence' "${inventory}")" \
      '{id:$id,status:"passed",reason:null,evidence:$evidence}' >>"${results_lines}"
    echo "${target}: passed"
  else
    jq -cn --arg id "${target}" --arg reason "${target_reason}" \
      '{id:$id,status:"failed",reason:$reason,evidence:[]}' >>"${results_lines}"
    echo "${target}: failed - ${target_reason}" >&2
  fi
done < <(jq -r '.targets[].id' "${inventory}")

jq -s --argjson blocked "$(jq -c '.blockedBoundaries' "${inventory}")" \
  '{schemaVersion:1,targets:.,blockedBoundaries:$blocked}' "${results_lines}" >"${results_file}"
rm "${results_lines}"
jq -r '([.targets[].status] | group_by(.) | map("\(.[0])=\(length)") | join(", "))' "${results_file}"

if (( test_status != 0 )); then
  exit 1
fi
