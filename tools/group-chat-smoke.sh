#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://127.0.0.1:5107}"
GROUP_ID="${GROUP_ID:-demo-group}"
THREAD_ID="${THREAD_ID:-demo-thread}"
MESSAGE_ID="${MESSAGE_ID:-msg-001}"

create_thread_payload='{
  "threadId": "'"${THREAD_ID}"'",
  "displayName": "demo-thread",
  "participantAgentIds": ["agent-alpha", "agent-beta"]
}'

post_message_payload='{
  "messageId": "'"${MESSAGE_ID}"'",
  "senderUserId": "user-001",
  "text": "大家好，@agent-alpha 和 @agent-beta 一起来聊聊吧",
  "mentionedAgentIds": ["agent-alpha", "agent-beta"]
}'

curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  -d "${create_thread_payload}" \
  "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads" >/dev/null || true

create_deadline=$((SECONDS + 15))
while (( SECONDS < create_deadline )); do
  if curl -fsS "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  -d "${post_message_payload}" \
  "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}/messages" >/dev/null

deadline=$((SECONDS + 30))
last_response=""
while (( SECONDS < deadline )); do
  last_response="$(curl -fsS "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}")"
  agent_message_count="$(printf '%s' "${last_response}" | python3 -c 'import json,sys; data=json.load(sys.stdin); print(sum(1 for x in data.get("messages", []) if x.get("senderKind") == "Agent" or x.get("senderKind") == 2))')"
  if [[ "${agent_message_count}" -ge 1 ]]; then
    printf '%s\n' "${last_response}"
    exit 0
  fi
  sleep 1
done

printf '%s\n' "${last_response}"
echo "group chat smoke test timed out before any feed-selected agent replied" >&2
exit 1
