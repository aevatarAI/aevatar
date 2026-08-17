#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/guards/project_reference_layer_guard.py"
FIXTURES="${REPO_ROOT}/tools/ci/tests/project_reference_layer_guard_fixtures"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

write_allowlist() {
  local path="$1"
  shift
  {
    printf 'source_project\ttarget_project\trule\towner_issue\texpires_on\treason\n'
    for line in "$@"; do
      printf '%s\n' "${line}"
    done
  } > "${path}"
}

assert_fails_with() {
  local expected="$1"
  shift
  local output="${TMP_DIR}/failure.out"

  set +e
  "$@" > "${output}" 2>&1
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

valid_allowlist="${TMP_DIR}/valid.tsv"
empty_allowlist="${TMP_DIR}/empty.tsv"
missing_field_allowlist="${TMP_DIR}/missing-field.tsv"
duplicate_allowlist="${TMP_DIR}/duplicate.tsv"
expired_allowlist="${TMP_DIR}/expired.tsv"
forbidden_allowlist="${TMP_DIR}/forbidden.tsv"
warning_output="${TMP_DIR}/warning.out"
report_output="${TMP_DIR}/report.out"

write_allowlist "${empty_allowlist}"
write_allowlist "${valid_allowlist}" \
  $'Feature.Abstractions\tFeature.Core\tabstractions-contracts-purity\t#9999\t2099-12-31\tfixture exception' \
  $'Service.Contracts\tService.Infrastructure\tabstractions-contracts-purity\t#9999\t2099-12-31\tfixture exception' \
  $'App.Abstractions\tPresentation.AGUI\tabstractions-contracts-purity\t#9999\t2099-12-31\tfixture exception'
write_allowlist "${missing_field_allowlist}" \
  $'Feature.Abstractions\tFeature.Core\tabstractions-contracts-purity\t#9999\t2099-12-31'
write_allowlist "${duplicate_allowlist}" \
  $'Feature.Abstractions\tFeature.Core\tabstractions-contracts-purity\t#9999\t2099-12-31\tfixture exception' \
  $'Feature.Abstractions\tFeature.Core\tabstractions-contracts-purity\t#9999\t2099-12-31\tfixture exception'
write_allowlist "${expired_allowlist}" \
  $'Feature.Abstractions\tFeature.Core\tabstractions-contracts-purity\t#9999\t2000-01-01\tfixture exception'
write_allowlist "${forbidden_allowlist}" \
  $'Aevatar.GAgents.Channel.Abstractions\tAevatar.CQRS.Projection.Core\tabstractions-contracts-purity\t#9999\t2099-12-31\tmust not hide acceptance edge'

python3.12 "${GUARD}" --root "${FIXTURES}/compliant" --allowlist "${empty_allowlist}"

assert_fails_with \
  "abstractions-contracts-purity" \
  python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${empty_allowlist}"

python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${empty_allowlist}" --mode report > "${report_output}" 2>&1
if ! rg -q "WARNING report-only" "${report_output}"; then
  echo "Expected report-mode run to print report-only warnings."
  cat "${report_output}"
  exit 1
fi
if ! rg -q "Feature\\.Abstractions -> Feature\\.Core" "${report_output}"; then
  echo "Expected report-mode output to include the fixture violation edge."
  cat "${report_output}"
  exit 1
fi

assert_fails_with \
  "expected 6 tab-separated fields" \
  python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${missing_field_allowlist}"

assert_fails_with \
  "duplicate allowlist entry" \
  python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${duplicate_allowlist}"

assert_fails_with \
  "allowlist entry is expired" \
  python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${expired_allowlist}"

assert_fails_with \
  "must not be allowlisted" \
  python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${forbidden_allowlist}"

python3.12 "${GUARD}" --root "${FIXTURES}/violating" --allowlist "${valid_allowlist}" > "${warning_output}" 2>&1
if ! rg -q "WARNING allowlisted" "${warning_output}"; then
  echo "Expected complete allowlist run to print warnings."
  cat "${warning_output}"
  exit 1
fi

echo "project_reference_layer_guard tests passed"
