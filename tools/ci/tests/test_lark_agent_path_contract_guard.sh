#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/lark_agent_path_contract_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

assert_fails_with() {
  local fixture="$1"
  local expected="$2"
  local output="${TMP_DIR}/${fixture}.out"

  set +e
  AEVATAR_LARK_AGENT_PATH_ROOT="${TMP_DIR}/${fixture}" bash "${GUARD}" > "${output}" 2>&1
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "Expected lark agent path guard to fail for ${fixture}."
    cat "${output}"
    exit 1
  fi

  if ! rg -q "${expected}" "${output}"; then
    echo "Expected failure output to contain: ${expected}"
    cat "${output}"
    exit 1
  fi
}

create_fixture() {
  local fixture="$1"
  local root="${TMP_DIR}/${fixture}"
  mkdir -p \
    "${root}/tools/ci" \
    "${root}/test/Aevatar.GAgents.ChannelRuntime.Tests" \
    "${root}/test/Aevatar.AI.Tests" \
    "${root}/agents/Aevatar.GAgents.NyxidChat"

  cat > "${root}/tools/ci/lark_agent_path_protected_tests.tsv" <<'TSV'
test_file	required_symbol	reason
test/Aevatar.GAgents.ChannelRuntime.Tests/ProtectedTests.cs	Protected_Test_Symbol	Fixture protected symbol.
TSV

  cat > "${root}/test/Aevatar.GAgents.ChannelRuntime.Tests/ProtectedTests.cs" <<'CS'
public sealed class ProtectedTests
{
    public void Protected_Test_Symbol() {}
}
CS

  cat > "${root}/test/Aevatar.AI.Tests/EndpointTests.cs" <<'CS'
public sealed class EndpointTests
{
    public void Endpoint_Protected_Symbol() {}
}
CS

  cat > "${root}/agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs" <<'CS'
public static class Relay
{
    public static void ResolveRelayScopeIdAsync()
    {
        ResolveScopeIdByApiKeyAsync();
        ResolveScopeIdFromUserToken();
    }

    private static void ResolveScopeIdByApiKeyAsync() {}
    private static void ResolveScopeIdFromUserToken() {}
}
CS

  cat > "${root}/agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs" <<'CS'
public sealed class ConversationReplyGenerator
{
    public async Task RunAsync(dynamic runtime)
    {
        await foreach (var chunk in runtime.ChatStreamAsync())
        {
        }
    }
}
CS
}

create_fixture "passing"
AEVATAR_LARK_AGENT_PATH_ROOT="${TMP_DIR}/passing" bash "${GUARD}" >/dev/null

create_fixture "missing-symbol"
perl -0pi -e 's/Protected_Test_Symbol/Renamed_Test_Symbol/g' \
  "${TMP_DIR}/missing-symbol/test/Aevatar.GAgents.ChannelRuntime.Tests/ProtectedTests.cs"
assert_fails_with "missing-symbol" "Protected Lark agent path behavior tests"

create_fixture "missing-api-key-scope"
perl -0pi -e 's/ResolveScopeIdByApiKeyAsync/ResolveScopeFromSomewhereElse/g' \
  "${TMP_DIR}/missing-api-key-scope/agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs"
assert_fails_with "missing-api-key-scope" "registered NyxID agent api key"

create_fixture "missing-owner-fallback"
perl -0pi -e 's/ResolveScopeIdFromUserToken/ResolveScopeFromNoUserToken/g' \
  "${TMP_DIR}/missing-owner-fallback/agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs"
assert_fails_with "missing-owner-fallback" "bot-owner scope"

create_fixture "chat-async"
perl -0pi -e 's/ChatStreamAsync/ChatAsync/g' \
  "${TMP_DIR}/chat-async/agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs"
assert_fails_with "chat-async" "ChatStreamAsync"

create_fixture "aggregator"
printf '\npublic sealed class ChatStreamContentAggregator {}\n' >> \
  "${TMP_DIR}/aggregator/agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs"
assert_fails_with "aggregator" "ChatStreamContentAggregator"

echo "lark agent path contract guard tests passed"
