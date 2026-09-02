#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/audit_trail_guards.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

assert_fails_with() {
  local expected="$1"
  shift
  local output="${TMP_DIR}/failure.out"

  set +e
  AEVATAR_AUDIT_TRAIL_GUARD_SELF_TEST=1 bash "$@" > "${output}" 2>&1
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "Expected command to fail: $*"
    cat "${output}"
    exit 1
  fi

  if ! rg -q "${expected}" "${output}"; then
    echo "Expected failure output to contain: ${expected}"
    cat "${output}"
    exit 1
  fi
}

write_fixture() {
  local name="$1"
  local body="$2"
  local dir="${TMP_DIR}/${name}"
  mkdir -p "${dir}"
  printf '%s\n' "${body}" > "${dir}/Fixture.cs"
  printf '%s\n' "${dir}"
}

raw_payload="$(write_fixture raw-payload '
public sealed class Fixture {
  public string raw_payload = "";
  public string request_body = "";
}
')"

raw_tool="$(write_fixture raw-tool '
public sealed class Fixture {
  public void Build(dynamic enriched, dynamic toolCall, dynamic receipt) {
    enriched.ArgumentsJson = toolCall.ArgumentsJson;
    enriched.ResultJson = receipt.ResultJson;
  }
}
')"

truncation="$(write_fixture truncation '
public sealed class Fixture {
  public string Redact(string value) => value.Length <= 5 ? value : value[..5] + "...";
}
')"

hmac_default="$(write_fixture hmac-default '
public sealed class Fixture {
  public string HmacSecret = "secret";
}
')"

safe="$(write_fixture safe '
public sealed class Fixture {
  public string Build(string value) => WorkflowAuditTextSanitizer.SanitizeForDisplay(value, 5);
  public string Secret(string value) => WorkflowAuditTextSanitizer.SanitizeValue("token", value);
}
')"

assert_fails_with "Banned raw audit/report payload field names" "${GUARD}" "${raw_payload}"
assert_fails_with "Tool argument/result audit writes" "${GUARD}" "${raw_tool}"
assert_fails_with "Truncation or Redact helpers" "${GUARD}" "${truncation}"
assert_fails_with "HMAC secret defaults" "${GUARD}" "${hmac_default}"
AEVATAR_AUDIT_TRAIL_GUARD_SELF_TEST=1 bash "${GUARD}" "${safe}"

echo "audit trail guard tests passed"
