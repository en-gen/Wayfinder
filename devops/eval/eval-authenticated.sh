#!/usr/bin/env bash
# ADO #32/#33 (sub-unit 4, P1/First Light) - exercises the REAL authenticated case-operation API
# (/api/v1/... - work item #32/#33) against the devops/eval docker-compose stack, the same way
# #49's cluster-status curl walkthrough exercises cluster formation. Unlike that walkthrough, this
# script needs a real Zitadel-issued bearer token as input - Zitadel's FIRSTINSTANCE_* bootstrap
# (docker-compose.yml) only gets as far as one org + one service-account machine key
# automatically; it does NOT create the Wayfinder project/API application, a second org for
# tenant B, or human/service users with a token you can hand this script. See
# devops/eval/README.md's "Authenticated API walkthrough (First Light)" section for the manual
# one-time bootstrap steps that produce TENANT_A_TOKEN (and, optionally, TENANT_B_TOKEN).
#
# Usage:
#   TENANT_A_TOKEN="eyJ..." ./devops/eval/eval-authenticated.sh
#   TENANT_A_TOKEN="eyJ..." TENANT_B_TOKEN="eyJ..." ./devops/eval/eval-authenticated.sh
#
# With only TENANT_A_TOKEN set, this proves the authenticated happy path (deploy -> create ->
# read). With TENANT_B_TOKEN also set, it additionally proves the cross-tenant isolation wall
# (tenant B reading tenant A's case -> 404) - the same property
# MultiTenantIsolationApiTests.TenantB__Given_TenantAsCase__Then_GetReturns404 proves at the
# in-process TestServer level (Flow.Grains.Tests.Integration/Api), demonstrated here instead
# against the real containerized stack with a real Zitadel-issued token.
#
# Prerequisites: curl, python3 (used only for JSON field extraction - no other dependency).
set -euo pipefail

SILO_URL="${SILO_URL:-http://localhost:8081}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CMMN_SAMPLE="$REPO_ROOT/src/Wayfinder.Grains.Tests.Integration/Interchange/Samples/MilestoneSentryCase.cmmn"

if [[ -z "${TENANT_A_TOKEN:-}" ]]; then
  echo "TENANT_A_TOKEN is required - see devops/eval/README.md's manual bootstrap steps for how to obtain one." >&2
  exit 1
fi

if [[ ! -f "$CMMN_SAMPLE" ]]; then
  echo "Expected CMMN sample not found at $CMMN_SAMPLE" >&2
  exit 1
fi

json_field() {
  # $1: JSON text on stdin is not used - $1 is the field path (dot-separated), $2 is the JSON text.
  python3 -c "
import json, sys
data = json.loads(sys.argv[2])
for key in sys.argv[1].split('.'):
    data = data[key]
print(data)
" "$1" "$2"
}

echo "==> Deploying MilestoneSentryCase.cmmn as tenant A"
deploy_response="$(curl -sS -X POST "$SILO_URL/api/v1/definitions" \
  -H "Authorization: Bearer $TENANT_A_TOKEN" \
  -H "Content-Type: application/xml" \
  --data-binary @"$CMMN_SAMPLE")"
echo "$deploy_response"
definition_id="$(json_field definitionId "$deploy_response")"
echo "    definitionId = $definition_id"

echo "==> Creating a case from that definition as tenant A"
create_response="$(curl -sS -X POST "$SILO_URL/api/v1/cases" \
  -H "Authorization: Bearer $TENANT_A_TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"definitionId\":\"$definition_id\"}")"
echo "$create_response"
case_id="$(json_field caseId "$create_response")"
echo "    caseId = $case_id"

echo "==> Reading the case back as tenant A (expect 200)"
get_status="$(curl -sS -o /tmp/case-flow-eval-get.json -w '%{http_code}' "$SILO_URL/api/v1/cases($case_id)" \
  -H "Authorization: Bearer $TENANT_A_TOKEN")"
cat /tmp/case-flow-eval-get.json
echo
if [[ "$get_status" != "200" ]]; then
  echo "FAIL: expected 200 reading own case, got $get_status" >&2
  exit 1
fi
echo "    OK (200)"

if [[ -z "${TENANT_B_TOKEN:-}" ]]; then
  echo "==> TENANT_B_TOKEN not set - skipping the cross-tenant isolation check. Happy path verified."
  exit 0
fi

echo "==> Reading tenant A's case AS TENANT B (expect 404 - the isolation wall)"
cross_status="$(curl -sS -o /tmp/case-flow-eval-cross.json -w '%{http_code}' "$SILO_URL/api/v1/cases($case_id)" \
  -H "Authorization: Bearer $TENANT_B_TOKEN")"
cat /tmp/case-flow-eval-cross.json
echo
if [[ "$cross_status" != "404" ]]; then
  echo "FAIL: expected 404 (cross-tenant isolation), got $cross_status" >&2
  exit 1
fi
echo "    OK (404) - tenant B cannot see tenant A's case."

echo "==> All checks passed: authenticated happy path + cross-tenant isolation both verified against the real stack."
