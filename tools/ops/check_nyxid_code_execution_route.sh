#!/usr/bin/env bash
# Read-only ADR-0050 drift canary for the caller-visible shared chrono-sandbox route.
#
# Usage:
#   NYXID_ROUTE_DRIFT_READ_TOKEN=<read-only bearer> \
#     tools/ops/check_nyxid_code_execution_route.sh <nyxid-api-base-url>
#
# The command performs one GET and emits only contract differences. It never prints the
# credential, UserService id, catalog id, owner identity, or provider response body.
{ set +x; } 2>/dev/null
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: NYXID_ROUTE_DRIFT_READ_TOKEN=<token> $0 <nyxid-api-base-url>" >&2
  exit 2
fi

nyxid_api_base_url="${1%/}"
if [[ -z "${NYXID_ROUTE_DRIFT_READ_TOKEN:-}" ]]; then
  echo "NYXID_ROUTE_DRIFT_READ_TOKEN must hold a read-only bearer for the canary account." >&2
  exit 2
fi
if [[ "${nyxid_api_base_url}" != https://* ]]; then
  echo "nyxid-api-base-url must use HTTPS." >&2
  exit 2
fi
nyxid_authority="${nyxid_api_base_url#https://}"
nyxid_authority="${nyxid_authority%%/*}"
if [[ -z "${nyxid_authority}" || "${nyxid_authority}" == *"@"* ]]; then
  echo "nyxid-api-base-url must not contain user information." >&2
  exit 2
fi

read_token="${NYXID_ROUTE_DRIFT_READ_TOKEN}"
unset NYXID_ROUTE_DRIFT_READ_TOKEN
if [[ ! "${read_token}" =~ ^[A-Za-z0-9._~+/-]+=*$ ]]; then
  echo "NYXID_ROUTE_DRIFT_READ_TOKEN contains unsupported characters." >&2
  exit 2
fi

inventory="$(
  printf 'header = "Authorization: Bearer %s"\n' "${read_token}" |
    curl --disable -fsS \
      --proto '=https' \
      --tlsv1.2 \
      --connect-timeout 10 \
      --max-time 30 \
      --max-filesize 1048576 \
      --config - \
      "${nyxid_api_base_url}/api/v1/user-services"
)"
unset read_token

shared_routes="$(jq -ce '[
  .services[]?
  | select(
      .slug == "chrono-sandbox"
      and (.catalog_service_id | type == "string")
      and (.catalog_service_id | length > 0))
]' <<<"${inventory}")"
shared_count="$(jq -r 'length' <<<"${shared_routes}")"
if [[ "${shared_count}" != "1" ]]; then
  echo "FAIL: expected exactly one caller-visible catalog-backed shared chrono-sandbox route; observed ${shared_count}." >&2
  exit 1
fi

route="$(jq -c '.[0]' <<<"${shared_routes}")"
is_active="$(jq -r 'if (.is_active | type) == "boolean" then (.is_active | tostring) else "invalid" end' <<<"${route}")"
forward_access_token="$(jq -r 'if (.forward_access_token | type) == "boolean" then (.forward_access_token | tostring) else "invalid" end' <<<"${route}")"
inject_delegation_token="$(jq -r 'if (.inject_delegation_token | type) == "boolean" then (.inject_delegation_token | tostring) else "invalid" end' <<<"${route}")"
delegation_scopes="$(jq -r '(.delegation_token_scope // "") | scan("\\S+")' <<<"${route}")"

failures=()
if [[ "${is_active}" != "true" ]]; then
  failures+=("is_active: ${is_active} -> true")
fi
if [[ "${forward_access_token}" != "true" ]]; then
  failures+=("forward_access_token: ${forward_access_token} -> true")
fi
if [[ "${inject_delegation_token}" != "true" ]]; then
  failures+=("inject_delegation_token: ${inject_delegation_token} -> true")
fi
if ! grep -Fxq "proxy:*" <<<"${delegation_scopes}"; then
  failures+=("delegation_token_scope: missing proxy:*")
fi
if ! grep -Fxq "sandbox:execute" <<<"${delegation_scopes}"; then
  failures+=("delegation_token_scope: missing sandbox:execute")
fi

if [[ ${#failures[@]} -gt 0 ]]; then
  echo "FAIL: shared chrono-sandbox route drifted from ADR-0050:" >&2
  for failure in "${failures[@]}"; do
    echo "  - ${failure}" >&2
  done
  exit 1
fi

echo "PASS: shared chrono-sandbox route satisfies ADR-0050."
