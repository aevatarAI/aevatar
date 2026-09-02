#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
REPO_ROOT="${AEVATAR_LARK_AGENT_PATH_ROOT:-${DEFAULT_REPO_ROOT}}"
MANIFEST="${AEVATAR_LARK_AGENT_PATH_MANIFEST:-${REPO_ROOT}/tools/ci/lark_agent_path_protected_tests.tsv}"

fail_contract() {
  local invariant="$1"
  local incident="$2"
  local fix="$3"

  echo "Invariant: ${invariant}"
  echo "Incident: ${incident}"
  echo "Fix: ${fix}"
  exit 1
}

require_rg() {
  if ! command -v rg >/dev/null 2>&1; then
    fail_contract \
      "The Lark agent path contract guard requires ripgrep." \
      "Without rg the protected test and source checks cannot run in CI." \
      "Install ripgrep or provide it on PATH."
  fi
}

require_file_contains() {
  local file_path="$1"
  local literal="$2"
  local invariant="$3"
  local incident="$4"
  local fix="$5"
  local absolute="${REPO_ROOT}/${file_path}"

  if [[ ! -f "${absolute}" ]]; then
    fail_contract \
      "${invariant}" \
      "${incident}: missing ${file_path}." \
      "${fix}"
  fi

  if ! rg -Fq -- "${literal}" "${absolute}"; then
    fail_contract \
      "${invariant}" \
      "${incident}: ${file_path} no longer contains '${literal}'." \
      "${fix}"
  fi
}

reject_file_pattern() {
  local file_path="$1"
  local pattern="$2"
  local invariant="$3"
  local incident="$4"
  local fix="$5"
  local absolute="${REPO_ROOT}/${file_path}"

  if [[ ! -f "${absolute}" ]]; then
    fail_contract \
      "${invariant}" \
      "${incident}: missing ${file_path}." \
      "${fix}"
  fi

  if rg -n -- "${pattern}" "${absolute}"; then
    fail_contract \
      "${invariant}" \
      "${incident}: ${file_path} matched forbidden pattern '${pattern}'." \
      "${fix}"
  fi
}

check_manifest() {
  if [[ ! -f "${MANIFEST}" ]]; then
    fail_contract \
      "Protected Lark agent path test manifest must exist." \
      "The anti-removal test list is missing: ${MANIFEST}." \
      "Restore tools/ci/lark_agent_path_protected_tests.tsv or set AEVATAR_LARK_AGENT_PATH_MANIFEST to a valid test manifest."
  fi

  local line_number=0
  local test_file required_symbol reason extra
  while IFS=$'\t' read -r test_file required_symbol reason extra || [[ -n "${test_file:-}" ]]; do
    line_number=$((line_number + 1))

    if [[ -z "${test_file//[[:space:]]/}" ]] || [[ "${test_file}" == \#* ]]; then
      continue
    fi

    if [[ "${line_number}" -eq 1 && "${test_file}" == "test_file" && "${required_symbol:-}" == "required_symbol" ]]; then
      continue
    fi

    if [[ -n "${extra:-}" || -z "${required_symbol:-}" || -z "${reason:-}" ]]; then
      fail_contract \
        "Protected test manifest rows must have exactly three tab-separated fields." \
        "Malformed row ${line_number} in ${MANIFEST}." \
        "Use: test_file<TAB>required_symbol<TAB>reason."
    fi

    if [[ "${test_file}" = /* || "${test_file}" == *".."* ]]; then
      fail_contract \
        "Protected test manifest paths must be repository-relative." \
        "Row ${line_number} uses an unsafe path: ${test_file}." \
        "Use a relative test path inside this repository."
    fi

    require_file_contains \
      "${test_file}" \
      "${required_symbol}" \
      "Protected Lark agent path behavior tests must not be removed silently." \
      "${reason}" \
      "Restore the behavior test '${required_symbol}' in ${test_file}, or update the manifest in the same change if the contract was intentionally renamed."
  done < "${MANIFEST}"
}

check_relay_scope_resolution_contract() {
  local relay_file="agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs"
  require_file_contains \
    "${relay_file}" \
    "ResolveScopeIdByApiKeyAsync(" \
    "Relay callbacks must resolve tenant scope from the registered NyxID agent api key before owner fallback." \
    "Lark relay traffic can route to the wrong or missing tenant if api-key scope lookup is removed." \
    "Keep ResolveRelayScopeIdAsync using INyxIdRelayScopeResolver.ResolveScopeIdByApiKeyAsync before owner-token fallback."

  require_file_contains \
    "${relay_file}" \
    "ResolveScopeIdFromUserToken(" \
    "Relay callbacks for directly registered NyxID bots must fall back to bot-owner scope from the user token." \
    "Direct NyxID Lark bots go silent when resolver returns null and owner-token fallback is removed." \
    "Keep ResolveRelayScopeIdAsync falling back to ResolveScopeIdFromUserToken(userAccessToken)."
}

check_streaming_reply_contract() {
  local generator_file="agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs"
  require_file_contains \
    "${generator_file}" \
    "runtime.ChatStreamAsync(" \
    "The Lark agent conversation reply path must use the streaming LLM entry." \
    "Text, reasoning, tool calls, tool results, and completion state split if this path stops streaming." \
    "Use runtime.ChatStreamAsync in ConversationReplyGenerator."

  reject_file_pattern \
    "${generator_file}" \
    "\\.ChatAsync\\(" \
    "The Lark agent conversation reply path must not use offline ChatAsync." \
    "ChatAsync would bypass the authoritative streaming path for real-time relay conversations." \
    "Keep ConversationReplyGenerator on ChatStreamAsync."

  reject_file_pattern \
    "${generator_file}" \
    "ChatStreamContentAggregator" \
    "The Lark agent conversation reply path must not collapse the stream with ChatStreamContentAggregator." \
    "Aggregating the stream hides incremental text/tool events and regresses the relay UX." \
    "Consume ChatStreamAsync chunks directly and publish through the streaming sink."
}

require_rg
check_manifest
check_relay_scope_resolution_contract
check_streaming_reply_contract

echo "Lark agent path contract guard passed."
