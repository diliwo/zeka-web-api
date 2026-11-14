#!/usr/bin/env bash
set -euo pipefail
umask 077

if (( $# != 5 )); then
  echo 'usage: run-c1-ratchet.sh <prepare|execute> <context> <lane> <role> <private-results>' >&2
  exit 2
fi

phase=$1
context=$2
lane=$3
role=$4
raw=$5
auth=Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj
admin=Services/AdminAreaManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj
client=Services/ClientManagement/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj
migrator=Tools/Zeka.DbMigrate.Tests/Zeka.DbMigrate.Tests.csproj
solution=zeka-web-api.sln

case $role in
  base) source=$(jq -er .base "$context"); tree=$(jq -er .baseTree "$context") ;;
  candidate) source=$(jq -er .candidate "$context"); tree=$(jq -er .testTree "$context") ;;
  *) echo 'Invalid ratchet source role.' >&2; exit 2 ;;
esac
[[ $source =~ ^[0-9a-f]{40}$ && $tree =~ ^[0-9a-f]{40}$ ]]
[[ $(git rev-parse HEAD) == "$source" && $(git rev-parse 'HEAD^{tree}') == "$tree" ]]
[[ $(dotnet --version) == 8.0.129 ]]
[[ -d $raw && ! -L $raw ]]

case $lane in
  non-postgres)
    projects=(
      Services/AuthManager/Tests/Domain.UnitTests/Domain.UnitTests.csproj
      Services/AuthManager/Tests/Application.UnitTests/Application.UnitTests.csproj
      Tools/Zeka.AuthOwnerProvision.Tests/Zeka.AuthOwnerProvision.Tests.csproj
      Services/AuthManager/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj
      Services/AdminAreaManagement/Tests/Domain.UnitTests/Domain.UnitTests.csproj
      Services/AdminAreaManagement/Tests/Application.UnitTests/Application.UnitTests.csproj
      Services/ClientManagement/Tests/Domain.UnitTests/Domain.UnitTests.csproj
      Services/ClientManagement/Tests/Application.UnitTests/Application.UnitTests.csproj
    )
    ;;
  postgresql)
    projects=(
      "$auth"
      "$admin"
      Services/AdminAreaManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj
      Services/ClientManagement/Tests/Application.IntegrationTests/Application.IntegrationTests.csproj
      "$client"
    )
    ;;
  sonar-suite) projects=("$solution") ;;
  production-conformance) projects=("$auth" "$admin" "$client" "$migrator") ;;
  owner-provision | gateway) projects=("$auth") ;;
  *) echo 'Invalid ratchet test profile.' >&2; exit 2 ;;
esac

quiet() {
  local log_name=$1
  shift
  local status=0
  "$@" >"$raw/$log_name.private.log" 2>&1 || status=$?
  if (( status != 0 )); then
    printf '%s exited with %s. Native logs are private to this runner.\n' "$log_name" "$status" >&2
  fi
  return "$status"
}

if [[ $phase == prepare ]]; then
  [[ -z $(git status --porcelain=v1 --untracked-files=all) ]]
  : "$PACKAGE_READ_TOKEN" "$PACKAGE_USERNAME"
  (
    export NuGetPackageSourceCredentials_github="Username=$PACKAGE_USERNAME;Password=$PACKAGE_READ_TOKEN;ValidAuthenticationTypes=Basic"
    if [[ $lane == non-postgres || $lane == sonar-suite ]]; then
      restore_targets=("$solution")
    else
      restore_targets=("${projects[@]}")
    fi
    for project in "${restore_targets[@]}"; do
      quiet "restore-${project//\//_}" dotnet restore "$project" --locked-mode \
        --configfile scripts/ci/nuget.strict.config -p:Configuration=Release \
        -p:RestorePackagesWithLockFile=true -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectSources=
    done
  )
  unset PACKAGE_READ_TOKEN PACKAGE_USERNAME
  case $lane in
    non-postgres)
      quiet build dotnet build "$solution" --configuration Release --no-restore
      ;;
    sonar-suite)
      quiet build dotnet build "$solution" --configuration Release --no-restore --no-incremental
      ;;
    owner-provision)
      quiet build timeout --signal=TERM --kill-after=10s 1800s dotnet build \
        "$auth" --configuration Release --no-restore -m:1
      ;;
  esac
  exit 0
fi

[[ $phase == execute ]]
[[ -z ${PACKAGE_READ_TOKEN+x}${NuGetPackageSourceCredentials_github+x} ]]
invocations="$raw/invocations.ndjson"
[[ ! -e $invocations && ! -e $raw/invocations.json ]]
: >"$invocations"
failed=0
index=0

report_invocation() {
  local project=$1 status=$2 directory=$3
  jq -nc --arg id "$project" --arg project "$project" --arg path "$directory" \
    --argjson status "$status" '{id:$id,project:$project,exitCode:$status,resultsDir:$path}' >>"$invocations"
  if (( status != 0 )); then
    failed=1
  fi
}

run_project() {
  local project=$1
  shift
  index=$((index + 1))
  local directory="result-$index"
  mkdir "$raw/$directory"
  local status=0
  quiet "test-$index" dotnet test "$project" --configuration Release --no-restore \
    --logger 'trx;LogFilePrefix=ratchet' --results-directory "$raw/$directory" \
    --blame --blame-hang-timeout 15m --blame-hang-dump-type none "$@" || status=$?
  report_invocation "$project" "$status" "$directory"
}

run_gateway() {
  # Reuse the original native TLS, test-discovery and cleanup checks byte-for-byte.
  # Changes to those checks require separately coordinated review of this binding.
  local original="$raw/gateway-mvp.yml"
  local workflow='.github/workflows/mvp-validation.yml'
  local approved_blob=e7043f42dfd10cd6ef2b63a96ce6fba9ea5a872c
  git cat-file blob "$approved_blob" >"$original"
  [[ $(git hash-object "$original") == "$approved_blob" ]]
  [[ $(git rev-parse "HEAD:$workflow") == "$approved_blob" ]]
  python3 - "$original" "$raw" <<'PY'
from pathlib import Path
import re
import sys

source, output = map(Path, sys.argv[1:])
steps = {
    "Record exact gateway source and prepare private evidence paths": "setup",
    "Build exact-head AuthManager gateway test assembly": "build",
    "Discover the exact gateway class cases": "discovery",
    "Run the gateway class once with native TLS and cleanup receipts": "focused",
    "Export and enforce safe candidate-bound focused evidence": "export",
}
section = source.read_text(encoding="utf-8").split("  postgresql:\n", 1)[1].split(
    "\n  life05a_verifier:\n", 1
)[0]
blocks = re.split(r"(?m)(?=^      - name: )", section)
for name, key in steps.items():
    matches = [block for block in blocks if block.startswith(f"      - name: {name}\n")]
    if len(matches) != 1:
        raise SystemExit("Pinned gateway step missing or ambiguous.")
    block = matches[0].split("\n        run: |\n", 1)[1].splitlines()
    body = []
    for line in block:
        if line and not line.startswith("          "):
            break
        body.append(line[10:])
    if not body or any("${{" in line for line in body):
        raise SystemExit("Pinned gateway step does not contain a standalone shell body.")
    (output / f"gateway-{key}.sh").write_text("\n".join(body) + "\n", encoding="utf-8")
PY
  export EXPECTED_HEAD=$source
  export EXPECTED_BASE
  EXPECTED_BASE=$(jq -er .base "$context")
  export ZEKA_GATEWAY_SHA=$source
  export ZEKA_EVENT_BASE_SHA=$EXPECTED_BASE
  export GATEWAY_RAW="$RUNNER_TEMP/pr93-gateway-private"
  export GATEWAY_SAFE="$RUNNER_TEMP/pr93-gateway-safe"
  GITHUB_ENV="$raw/gateway.private.env" quiet gateway-setup bash "$raw/gateway-setup.sh"
  quiet gateway-build bash "$raw/gateway-build.sh"
  quiet gateway-discovery bash "$raw/gateway-discovery.sh"

  local status=0
  quiet gateway-focused bash "$raw/gateway-focused.sh" || status=$?
  quiet gateway-export bash "$raw/gateway-export.sh"
  mkdir "$raw/result-1"
  cp "$GATEWAY_RAW/results/focused.trx" "$raw/result-1/gateway.trx"
  report_invocation "$auth" "$status" result-1
}

case $lane in
  non-postgres)
    for project in "${projects[@]}"; do run_project "$project" --no-build; done
    ;;
  postgresql)
    quiet docker docker info
    for project in "${projects[@]}"; do run_project "$project"; done
    ;;
  sonar-suite)
    quiet docker docker info
    run_project "$solution" -m:1 --no-build --collect 'XPlat Code Coverage' -- \
      DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
    ;;
  production-conformance)
    quiet docker docker info
    status=0
    quiet conformance bash scripts/ci/run-production-conformance.sh "$raw/conformance" || status=$?
    for row in "auth|$auth" "adminarea|$admin" "client|$client" "migration|$migrator"; do
      suite=${row%%|*}
      project=${row#*|}
      mkdir "$raw/result-$suite"
      if [[ -f $raw/conformance/$suite.trx ]]; then
        cp "$raw/conformance/$suite.trx" "$raw/result-$suite/$suite.trx"
      fi
      report_invocation "$project" "$status" "result-$suite"
    done
    ;;
  owner-provision)
    quiet docker docker info
    mkdir "$raw/result-1"
    status=0
    quiet owner-provision timeout --signal=TERM --kill-after=10s 300s \
      dotnet test "$auth" --configuration Release --no-build --no-restore \
      --filter 'FullyQualifiedName~PostgreSqlOrganisationProvisioningTests' \
      --logger 'trx;LogFileName=owner-provider.trx' --results-directory "$raw/result-1" \
      --blame || status=$?
    report_invocation "$auth" "$status" result-1
    ;;
  gateway)
    quiet docker docker info
    run_gateway
    ;;
esac

[[ $(git rev-parse HEAD) == "$source" && $(git rev-parse 'HEAD^{tree}') == "$tree" ]]
git diff --quiet --
git diff --cached --quiet --
jq -s --arg head "$source" --arg tree "$tree" \
  '{schema:1,sourceSha:$head,tree:$tree,started:true,completed:true,invocations:.}' \
  "$invocations" >"$raw/invocations.json"
exit "$failed"
