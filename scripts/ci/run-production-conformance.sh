#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
source_sha=$(git -C "${repo_root}" rev-parse --verify HEAD)
inventory="${repo_root}/Deployments/database/production-conformance.targets.json"
results_dir="${1:-${repo_root}/TestResults/production-conformance}"

expected=(
  ROLE-01 ROLE-02 ROLE-03 ROLE-04
  POOL-01 POOL-02 POOL-03 POOL-04 POOL-05 POOL-06 POOL-07 POOL-08 POOL-09
  COV-02 COV-03 COV-05 PRIV-01 PRIV-02 PRIV-03
  OPS-01 OPS-02 OPS-03 OPS-04 OPS-05 OPS-06 OPS-07 OPS-08
)
mapfile -t actual < <(jq -r '.targets[] | select(.required == true) | .id' "${inventory}" | sort)
mapfile -t required < <(printf '%s\n' "${expected[@]}" | sort)
if ! diff -u <(printf '%s\n' "${required[@]}") <(printf '%s\n' "${actual[@]}"); then
  echo "Production-conformance target inventory is incomplete or contains an unauthorized target." >&2
  exit 1
fi

if ! jq -e '
  .schemaVersion == 1 and
  .statusVocabulary == ["passed","failed","blocked","untested"] and
  ([.targets[].id] | length == (unique | length)) and
  all(.targets[];
      .required == true and
      (.evidence | type == "array") and
      (if (.evidence | length) > 0 then
         (.disposition == null) and
         all(.evidence[]; (.suite | IN("auth","adminarea","client","migration")) and (.test | length > 0))
       else
         (.disposition.status | IN("blocked","untested")) and
         (.disposition.reason | type == "string" and length > 0)
       end))
' "${inventory}" >/dev/null; then
  echo "Production-conformance target inventory failed structural validation." >&2
  exit 1
fi

mkdir -p "${results_dir}"
results_file="${results_dir}/target-results.json"
results_lines="${results_dir}/target-results.ndjson"
: >"${results_lines}"
projects=(
  "auth|Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
  "adminarea|Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
  "client|Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
  "migration|Tools/Zeka.DbMigrate.Tests/Zeka.DbMigrate.Tests.csproj"
)

test_status=0
for entry in "${projects[@]}"; do
  suite=${entry%%|*}
  project=${entry#*|}
  echo "::group::Issue #46 production conformance: ${suite}"
  if ! dotnet test "${repo_root}/${project}" --configuration Release --no-restore \
      /p:ZekaSourceSha="${source_sha}" \
      --filter 'Issue=46&Evidence=PlatformConformance' \
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

incomplete_status=0
while IFS= read -r target; do
  disposition_status=$(jq -r --arg target "${target}" \
    '.targets[] | select(.id == $target) | .disposition.status // empty' "${inventory}")
  if [[ -n "${disposition_status}" ]]; then
    reason=$(jq -r --arg target "${target}" \
      '.targets[] | select(.id == $target) | .disposition.reason' "${inventory}")
    jq -cn --arg id "${target}" --arg status "${disposition_status}" --arg reason "${reason}" \
      '{id:$id,status:$status,reason:$reason,evidence:[]}' >>"${results_lines}"
    echo "${target}: ${disposition_status} - ${reason}"
    incomplete_status=1
    continue
  fi

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

jq -s '{schemaVersion:1,targets:.}' "${results_lines}" >"${results_file}"
rm "${results_lines}"
jq -r '([.targets[].status] | group_by(.) | map("\(.[0])=\(length)") | join(", "))' "${results_file}"

if (( test_status != 0 )); then
  exit 1
fi
if (( incomplete_status != 0 )); then
  echo "Production-conformance evidence is valid but incomplete." >&2
  exit 2
fi
