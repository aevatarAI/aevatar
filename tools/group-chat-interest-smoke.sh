#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-http://127.0.0.1:5107}"
GROUP_ID="${GROUP_ID:-demo-group}"
THREAD_ID="${THREAD_ID:-interest-smoke-thread}"
MESSAGE_ID="${MESSAGE_ID:-interest-smoke-msg-001}"
TOPIC_ID="${TOPIC_ID:-}"
MESSAGE_TEXT="${MESSAGE_TEXT:-请 @agent-alpha 和 @agent-beta 看一下这个话题}"
PARTICIPANTS_CSV="${PARTICIPANTS_CSV:-agent-alpha,agent-beta}"
DIRECT_HINTS_CSV="${DIRECT_HINTS_CSV:-agent-alpha,agent-beta}"
EXPECTED_REPLY_AGENTS_CSV="${EXPECTED_REPLY_AGENTS_CSV:-agent-alpha,agent-beta}"

json_array_from_csv() {
  local csv="$1"
  python3 - "$csv" <<'PY'
import json, sys
raw = sys.argv[1]
items = [x.strip() for x in raw.split(",") if x.strip()]
print(json.dumps(items, ensure_ascii=False))
PY
}

PARTICIPANTS_JSON="$(json_array_from_csv "${PARTICIPANTS_CSV}")"
DIRECT_HINTS_JSON="$(json_array_from_csv "${DIRECT_HINTS_CSV}")"
EXPECTED_AGENTS_JSON="$(json_array_from_csv "${EXPECTED_REPLY_AGENTS_CSV}")"

create_thread_payload="$(python3 - <<PY
import json
print(json.dumps({
    "threadId": "${THREAD_ID}",
    "displayName": "${THREAD_ID}",
    "participantAgentIds": ${PARTICIPANTS_JSON},
}, ensure_ascii=False))
PY
)"

post_message_payload="$(python3 - <<PY
import json
payload = {
    "messageId": "${MESSAGE_ID}",
    "senderUserId": "user-001",
    "text": "${MESSAGE_TEXT}",
    "mentionedAgentIds": ${DIRECT_HINTS_JSON},
}
topic_id = "${TOPIC_ID}"
if topic_id:
    payload["topicId"] = topic_id
print(json.dumps(payload, ensure_ascii=False))
PY
)"

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
  if python3 - "${EXPECTED_AGENTS_JSON}" "${last_response}" <<'PY'
import json, sys
expected = sorted(json.loads(sys.argv[1]))
data = json.loads(sys.argv[2])
actual = sorted(
    x.get("senderId")
    for x in data.get("messages", [])
    if x.get("senderKind") in ("Agent", 2)
)
sys.exit(0 if actual == expected else 1)
PY
  then
    printf '%s\n' "${last_response}"
    exit 0
  fi
  sleep 1
done

printf '%s\n' "${last_response}"
echo "group chat interest smoke test timed out before expected reply agents appeared" >&2
exit 1
