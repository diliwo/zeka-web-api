#!/usr/bin/env bash
set -euo pipefail

repo_root=$(git rev-parse --show-toplevel)
source_sha=$(git -C "${repo_root}" rev-parse --verify HEAD)
test "${source_sha}" = "${ZEKA_EXPECTED_HEAD_SHA:?Exact PR head SHA required}"
results_dir="${1:-${repo_root}/TestResults/life05a-verifier}"
mkdir -p "${results_dir}"
container=$(docker run -d --rm -e POSTGRES_PASSWORD=synthetic-only \
  -e POSTGRES_HOST_AUTH_METHOD=trust postgres:17)
cleanup() { docker rm -f "${container}" >/dev/null; }
trap cleanup EXIT
ready=0
for attempt in {1..30}; do
  if docker exec "${container}" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
test "${ready}" = 1
docker exec -i "${container}" psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres \
  < "${repo_root}/Deployments/database/life05a-verifier-ci-fixture.sql"
docker exec -i "${container}" psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres \
  < "${repo_root}/Deployments/database/bootstrap-life05a-verifier-fixture.sql"
docker exec -i "${container}" psql -X -v ON_ERROR_STOP=1 -U postgres -d postgres \
  < "${repo_root}/Deployments/database/bootstrap-life05a-verifier-fixture.sql"
for verifier_role in \
  zeka_life05a_verify_auth_management \
  zeka_life05a_verify_admin_area \
  zeka_life05a_verify_admin_area_documents \
  zeka_life05a_verify_client_management; do
  docker exec -i -e PGUSER="${verifier_role}" "${container}" \
    psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -d postgres \
    < "${repo_root}/Deployments/database/life05a-verifier-assertions.sql"
done

# The SQL role smoke is a prerequisite, not a replacement for the exact-head
# application purge/verify contract. The project must already be restored.
test_project="${repo_root}/Services/AuthManager/Tests/Infrastructure.IntegrationTests/Infrastructure.IntegrationTests.csproj"
trx="${results_dir}/life05a.trx"
rm -f "${trx}"
test_status=0
if ! dotnet test "${test_project}" --configuration Release --no-restore \
    --filter 'FullyQualifiedName~Life05a' \
    --logger 'trx;LogFileName=life05a.trx' --results-directory "${results_dir}"; then
  test_status=1
fi

python3 - "${trx}" "${results_dir}/summary.json" "${source_sha}" "${test_status}" <<'PY'
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

trx, summary = map(Path, sys.argv[1:3])
sha, test_status = sys.argv[3:]
evidence = {"sourceSha": str(sha), "filter": "FullyQualifiedName~Life05a",
            "total": 0, "passed": 0, "failed": 0, "skipped": 0,
            "positive": 0, "failClosed": 0, "testProcessSucceeded": test_status == "0",
            "valid": False}
errors = []
if test_status != "0":
    errors.append("LIFE-05A test process failed")
try:
    root = ET.parse(trx).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    counters = root.find("./t:ResultSummary/t:Counters", ns)
    if counters is None:
        raise ValueError("TRX counters are missing")
    for key, attribute in (("total", "total"), ("passed", "passed"),
                           ("failed", "failed"), ("skipped", "notExecuted")):
        evidence[key] = int(counters.attrib[attribute])
    tests = {}
    for unit_test in root.findall("./t:TestDefinitions/t:UnitTest", ns):
        method = unit_test.find("t:TestMethod", ns)
        if method is None:
            continue
        tests[unit_test.attrib["id"]] = (
            method.attrib.get("className", "") + "." + method.attrib.get("name", ""))
    results = root.findall("./t:Results/t:UnitTestResult", ns)
    if evidence["total"] < 3 or len(results) != evidence["total"]:
        errors.append("Expected at least three LIFE-05A results, including positive and fail-closed negatives")
    if (evidence["passed"] != evidence["total"] or evidence["failed"] != 0
            or evidence["skipped"] != 0):
        errors.append("LIFE-05A tests failed or were skipped")
    for result in results:
        name = tests.get(result.attrib.get("testId", ""), "")
        if "life05a" not in name.lower() or result.attrib.get("outcome") != "Passed":
            errors.append("Result lacks a LIFE-05A identity or a Passed outcome")
            continue
        if "positive" in name.lower():
            evidence["positive"] += 1
        if "failclosed" in name.lower():
            evidence["failClosed"] += 1
    if evidence["positive"] < 1 or evidence["failClosed"] < 2:
        errors.append("Required positive and two fail-closed negative tests are missing")
except (OSError, ET.ParseError, ValueError, KeyError) as error:
    errors.append("Missing or malformed LIFE-05A TRX evidence: " + str(error))
evidence["valid"] = not errors
summary.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
for error in errors:
    print(error, file=sys.stderr)
sys.exit(0 if evidence["valid"] else 1)
PY
