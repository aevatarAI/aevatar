#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-agent_reply_group}"

read_stdin() {
  local input
  input="$(cat || true)"
  printf '%s' "$input"
}

extract_json_field() {
  local json="$1"
  local field="$2"
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$json" | jq -r --arg field "$field" 'if type=="object" and has($field) then .[$field] // "" else "" end' 2>/dev/null || true
  else
    printf ''
  fi
}

is_json_object() {
  local input="$1"
  [[ "$input" =~ ^[[:space:]]*\{ ]]
}

require_cmd() {
  local name="$1"
  if ! command -v "$name" >/dev/null 2>&1; then
    echo "Required command not found: $name" >&2
    exit 127
  fi
}

main() {
  local raw_input prompt message chat_id prefix agent_id timeout_s
  require_cmd openclaw

  raw_input="$(read_stdin)"

  prompt="$raw_input"
  message="$raw_input"
  chat_id="${OPENCLAW_TG_CHAT_ID:-}"
  prefix="${OPENCLAW_REPLY_PREFIX:-AEVATAR_STREAM_REPLY}"
  agent_id="${OPENCLAW_AGENT_ID:-main}"
  timeout_s="${OPENCLAW_AGENT_TIMEOUT_SECONDS:-120}"

  if is_json_object "$raw_input"; then
    local json_prompt json_message json_chat_id json_prefix json_agent_id json_timeout_s
    json_prompt="$(extract_json_field "$raw_input" "prompt")"
    json_message="$(extract_json_field "$raw_input" "message")"
    json_chat_id="$(extract_json_field "$raw_input" "chat_id")"
    json_prefix="$(extract_json_field "$raw_input" "prefix")"
    json_agent_id="$(extract_json_field "$raw_input" "agent_id")"
    json_timeout_s="$(extract_json_field "$raw_input" "timeout_seconds")"

    if [[ -n "$json_prompt" ]]; then prompt="$json_prompt"; fi
    if [[ -n "$json_message" ]]; then message="$json_message"; fi
    if [[ -n "$json_chat_id" ]]; then chat_id="$json_chat_id"; fi
    if [[ -n "$json_prefix" ]]; then prefix="$json_prefix"; fi
    if [[ -n "$json_agent_id" ]]; then agent_id="$json_agent_id"; fi
    if [[ -n "$json_timeout_s" ]]; then timeout_s="$json_timeout_s"; fi
  fi

  case "$MODE" in
    agent_reply_group)
      if [[ -z "$chat_id" ]]; then
        echo "OPENCLAW_TG_CHAT_ID (or payload.chat_id) is required for mode agent_reply_group" >&2
        exit 2
      fi
      local composed_prompt
      if [[ -n "$prefix" ]]; then
        composed_prompt="Please reply in the same Telegram group and prefix your reply with ${prefix}.\n\n${prompt}"
      else
        composed_prompt="$prompt"
      fi
      exec openclaw agent \
        --agent "$agent_id" \
        --message "$composed_prompt" \
        --deliver \
        --reply-channel telegram \
        --reply-to "$chat_id" \
        --timeout "$timeout_s" \
        --json
      ;;
    message_send)
      if [[ -z "$chat_id" ]]; then
        echo "OPENCLAW_TG_CHAT_ID (or payload.chat_id) is required for mode message_send" >&2
        exit 2
      fi
      exec openclaw message send \
        --channel telegram \
        --target "$chat_id" \
        --message "$message" \
        --json
      ;;
    agent_only)
      exec openclaw agent \
        --agent "$agent_id" \
        --message "$prompt" \
        --timeout "$timeout_s" \
        --json
      ;;
    *)
      echo "Unsupported mode: $MODE" >&2
      exit 2
      ;;
  esac
}

main "$@"
