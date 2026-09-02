#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/nyxid_chat_semantics_guard.sh"
TMP_DIR="$(mktemp -d)"
FIXTURE_ROOT="${TMP_DIR}/fixture"
GUARD_OUTPUT=""
GUARD_STATUS=0

trap 'rm -rf "${TMP_DIR}"' EXIT

nyxid_dir="${FIXTURE_ROOT}/agents/Aevatar.GAgents.NyxidChat"
proto_file="${nyxid_dir}/protos/nyxid_chat_task.proto"
readmodel_file="${FIXTURE_ROOT}/src/Aevatar.Studio.Projection/ReadModels/studio_projection_readmodels.proto"
lifecycle_file="${nyxid_dir}/NyxIdChatTaskLifecycle.cs"
controller_file="${nyxid_dir}/NyxIdChatConversationGAgent.cs"
transition_file="${nyxid_dir}/NyxIdChatTaskTransitionPolicy.cs"
registry_file="${nyxid_dir}/NyxIdAssistantActionRegistry.cs"
service_file="${nyxid_dir}/SafeOperationService.cs"

mkdir -p "$(dirname -- "${proto_file}")" "$(dirname -- "${readmodel_file}")"

write_baseline() {
  printf '%s\n' \
    'public static class NyxIdChatTaskLifecycle' \
    '{' \
    '    public static bool IsFailure(TypedReceipt receipt) => receipt.Status == TypedStatus.Error;' \
    '}' \
    > "${lifecycle_file}"
  printf '%s\n' \
    'public sealed class NyxIdChatConversationGAgent { }' \
    > "${controller_file}"
  printf '%s\n' \
    'public static class NyxIdChatTaskTransitionPolicy { }' \
    > "${transition_file}"
  printf '%s\n' \
    'public sealed class NyxIdAssistantActionRegistry' \
    '{' \
    '    private const string LegalAction = "developer_app.rotate_secret";' \
    '}' \
    > "${registry_file}"
  printf '%s\n' \
    'public sealed class SafeOperationService' \
    '{' \
    '    public void Build()' \
    '    {' \
    '        var operations = new Dictionary<string, object>();' \
    '    }' \
    '}' \
    > "${service_file}"
  printf '%s\n' \
    'syntax = "proto3";' \
    'message NyxIdDeveloperAppRotateSecretParams {' \
    '  string client_id = 1;' \
    '}' \
    'message NyxIdAssistantActionParams {' \
    '  NyxIdDeveloperAppRotateSecretParams developer_app_rotate_secret = 1;' \
    '}' \
    > "${proto_file}"
  printf '%s\n' \
    'syntax = "proto3";' \
    'message NyxIdChatConversationCurrentStateDocument {' \
    '  string actor_id = 1;' \
    '}' \
    'message ExternalProviderDocument {' \
    '  string access_token = 1;' \
    '}' \
    > "${readmodel_file}"
}

run_guard() {
  set +e
  GUARD_OUTPUT="$(
    AEVATAR_NYXID_CHAT_GUARD_ROOT="${FIXTURE_ROOT}" \
      bash "${GUARD}" 2>&1
  )"
  GUARD_STATUS=$?
  set -e
}

require_success() {
  if [[ ${GUARD_STATUS} -ne 0 ]]; then
    echo "NyxIdChat semantics guard self-test baseline failed" >&2
    printf '%s\n' "${GUARD_OUTPUT}" >&2
    exit 1
  fi
}

require_failure() {
  local expected="$1"
  if [[ ${GUARD_STATUS} -eq 0 ]]; then
    echo "NyxIdChat semantics guard self-test expected failure: ${expected}" >&2
    exit 1
  fi
  if ! printf '%s\n' "${GUARD_OUTPUT}" | rg -Fq -- "${expected}"; then
    echo "NyxIdChat semantics guard self-test missing diagnostic: ${expected}" >&2
    printf '%s\n' "${GUARD_OUTPUT}" >&2
    exit 1
  fi
}

write_baseline
run_guard
require_success

printf '%s\n' \
  'public sealed class ForbiddenOperationService' \
  '{' \
  '    private readonly Dictionary<string, object> _operations = new();' \
  '}' \
  > "${service_file}"
run_guard
require_failure "service-level operation/action/cancellation collections"
write_baseline

printf '%s\n' \
  'public static class NyxIdChatTaskLifecycle' \
  '{' \
  '    public static bool IsFailure(JsonElement root) =>' \
  '        root.TryGetProperty("error", out _);' \
  '}' \
  > "${lifecycle_file}"
run_guard
require_failure "generic JSON \"error\" inference"
write_baseline

printf '%s\n' \
  'public sealed class NyxIdAssistantActionRegistry' \
  '{' \
  '    private const string ForbiddenAction = "device.approve.user_code";' \
  '}' \
  > "${registry_file}"
run_guard
require_failure "device.approve.user_code"
write_baseline

printf '%s\n' \
  'syntax = "proto3";' \
  'message NyxIdChatConversationGAgentState {' \
  '  string access_token = 1;' \
  '}' \
  > "${proto_file}"
run_guard
require_failure "secret-bearing protobuf fields"
write_baseline

printf '%s\n' \
  'syntax = "proto3";' \
  'message NyxIdChatConversationCurrentStateDocument {' \
  '  string actor_id = 1;' \
  '  string client_secret = 2;' \
  '}' \
  > "${readmodel_file}"
run_guard
require_failure "secret-bearing read-model fields"
write_baseline

run_guard
require_success

echo "NyxIdChat semantics guard tests passed"
