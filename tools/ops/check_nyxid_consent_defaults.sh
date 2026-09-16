#!/usr/bin/env bash
# Verify that a deployment's NyxID OAuth client preselects every service the
# Aevatar runtime requires.
#
# Channel /init sends no RFC 8707 `resource` at /oauth/authorize (ADR-0018,
# 2026-08-05): sending it narrowed the authorization code — and the durable
# broker binding minted from it — to Aevatar's core set, silently dropping every
# optional UserService the user had approved. The cost of omitting it is that
# NyxID's consent page no longer marks the core services as non-deselectable, so
# the app's `default_service_catalog_slugs` is what preselects them. When those
# defaults drift, users still cannot create an under-granted binding — the
# callback probe rejects it with 409 — but every /init turns into a repair loop.
#
# Consent defaults preselect checkboxes. They are never an authorization fact.
#
# Usage:
#   NYXID_DEVELOPER_TOKEN=<owner access token> \
#     tools/ops/check_nyxid_consent_defaults.sh <aevatar-base-url> <nyxid-api-base-url>
#
# Example:
#   NYXID_DEVELOPER_TOKEN=... tools/ops/check_nyxid_consent_defaults.sh \
#     https://aevatar-console-backend-api.aevatar.ai https://nyx-api.chrono-ai.fun
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: NYXID_DEVELOPER_TOKEN=<token> $0 <aevatar-base-url> <nyxid-api-base-url>" >&2
  exit 2
fi

aevatar_base_url="${1%/}"
nyxid_api_base_url="${2%/}"

if [[ -z "${NYXID_DEVELOPER_TOKEN:-}" ]]; then
  echo "NYXID_DEVELOPER_TOKEN must hold an access token for the NyxID account that owns the app." >&2
  exit 2
fi

# The deployed host is the authority for its own runtime floor; re-deriving the
# slugs from appsettings here would be a second, driftable copy of the provider
# defaults the host already resolves.
client_status="$(curl -fsS "${aevatar_base_url}/api/oauth/aevatar-client/status")"
client_id="$(jq -er '.client_id' <<<"${client_status}")"
# No `-e`: an empty list is a reportable finding below, not a script failure.
required_slugs="$(jq -r '.required_service_slugs[]?' <<<"${client_status}" | sort)"

if [[ -z "${required_slugs}" ]]; then
  echo "FAIL: ${aevatar_base_url} reported no required_service_slugs." >&2
  exit 1
fi

app="$(curl -fsS \
  -H "Authorization: Bearer ${NYXID_DEVELOPER_TOKEN}" \
  "${nyxid_api_base_url}/api/v1/developer/oauth-clients/${client_id}")"
default_slugs="$(jq -r '.default_service_catalog_slugs[]?' <<<"${app}" | sort)"

missing="$(comm -23 <(echo "${required_slugs}") <(echo "${default_slugs}"))"

echo "oauth client:      ${client_id}"
echo "required slugs:    $(tr '\n' ' ' <<<"${required_slugs}")"
echo "consent defaults:  $(tr '\n' ' ' <<<"${default_slugs}")"

if [[ -n "${missing}" ]]; then
  echo >&2
  echo "FAIL: consent defaults are missing required service slug(s): $(tr '\n' ' ' <<<"${missing}")" >&2
  echo "Users completing /init will not see these preselected, and any binding that omits them" >&2
  echo "is rejected at the callback with 409 required_service_access_missing." >&2
  echo "Fix: PATCH ${nyxid_api_base_url}/api/v1/developer/oauth-clients/${client_id}" >&2
  echo "     with default_service_catalog_slugs covering every required slug above." >&2
  exit 1
fi

echo
echo "PASS: consent defaults preselect every required service slug."
