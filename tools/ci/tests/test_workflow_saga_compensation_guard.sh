#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/workflow_saga_compensation_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

stub_dir="${TMP_DIR}/bin"
mkdir -p "${stub_dir}"

cat > "${stub_dir}/dotnet" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" > "${DOTNET_STUB_ARGS_FILE:?}"
if [[ -n "${DOTNET_STUB_OUTPUT:-}" ]]; then
  printf '%b' "${DOTNET_STUB_OUTPUT}"
fi
exit "${DOTNET_STUB_EXIT_CODE:-0}"
STUB
chmod +x "${stub_dir}/dotnet"

args_file="${TMP_DIR}/dotnet.args"
PATH="${stub_dir}:${PATH}" DOTNET_STUB_ARGS_FILE="${args_file}" bash "${GUARD}"

expected='test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --nologo --filter FullyQualifiedName~WorkflowSagaCompensation'
actual="$(cat "${args_file}")"
if [[ "${actual}" != "${expected}" ]]; then
  echo "workflow saga compensation guard should delegate only to the Roslyn architecture test"
  echo "expected: ${expected}"
  echo "actual:   ${actual}"
  exit 1
fi

set +e
PATH="${stub_dir}:${PATH}" DOTNET_STUB_ARGS_FILE="${args_file}" DOTNET_STUB_EXIT_CODE=37 bash "${GUARD}" >/tmp/workflow-saga-guard-wrapper.out 2>&1
status=$?
set -e
if [[ ${status} -ne 37 ]]; then
  echo "workflow saga compensation guard should preserve dotnet test exit status"
  cat /tmp/workflow-saga-guard-wrapper.out
  exit 1
fi

set +e
PATH="${stub_dir}:${PATH}" DOTNET_STUB_ARGS_FILE="${args_file}" DOTNET_STUB_OUTPUT=$'No test matches the given testcase filter `FullyQualifiedName~WorkflowSagaCompensation`\n' bash "${GUARD}" >/tmp/workflow-saga-guard-wrapper.out 2>&1
status=$?
set -e
if [[ ${status} -eq 0 ]]; then
  echo "workflow saga compensation guard should fail when dotnet test discovers zero matching tests"
  cat /tmp/workflow-saga-guard-wrapper.out
  exit 1
fi

if ! rg -q "Roslyn architecture test filter matched zero tests" /tmp/workflow-saga-guard-wrapper.out; then
  echo "workflow saga compensation guard should report a clear zero-match error"
  cat /tmp/workflow-saga-guard-wrapper.out
  exit 1
fi

if rg -q "TryStartCompensationOrPublishTerminalFailureAsync|WorkflowCompensationFailedEvent|stepIds\\.Contains|CompensationDeadLettered|DefaultCompensationTimeoutMs|CompensationPhaseDeadlineMs|rg-compatible|ci_require_pattern" "${GUARD}"; then
  echo "workflow saga compensation guard wrapper must not duplicate semantic checks"
  exit 1
fi

echo "workflow saga compensation guard wrapper tests passed"
