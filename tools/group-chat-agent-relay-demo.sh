#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

BASE_URL="${1:-http://127.0.0.1:5100}"
GROUP_ID="${GROUP_ID:-g-agent-demo-$(date +%s)}"
THREAD_ID="${THREAD_ID:-t-agent-demo-$$}"
MESSAGE_ID="${MESSAGE_ID:-m-agent-1}"
AI_WAIT_SECONDS="${AI_WAIT_SECONDS:-45}"
WORKFLOW_NAME="${WORKFLOW_NAME:-simple_qa}"
WORKFLOW_SCOPE_ID="${WORKFLOW_SCOPE_ID:-group-chat-ai}"
HOST_PROJECT="${HOST_PROJECT:-${REPO_ROOT}/src/workflow/Aevatar.Workflow.Host.Api}"
HOST_LOG_FILE="${HOST_LOG_FILE:-/tmp/group-chat-agent-relay-demo.log}"
REUSE_EXISTING_HOST="${REUSE_EXISTING_HOST:-false}"

host_pid=""
started_host="false"

cleanup() {
  if [[ -n "${host_pid}" ]] && kill -0 "${host_pid}" >/dev/null 2>&1; then
    kill "${host_pid}" >/dev/null 2>&1 || true
    wait "${host_pid}" >/dev/null 2>&1 || true
  fi
}

trap cleanup EXIT

wait_for_http() {
  local deadline=$((SECONDS + 30))
  while (( SECONDS < deadline )); do
    if curl -s -o /dev/null --max-time 2 "${BASE_URL}"; then
      return 0
    fi
    sleep 1
  done

  return 1
}

ensure_host() {
  if curl -s -o /dev/null --max-time 2 "${BASE_URL}"; then
    if [[ "${REUSE_EXISTING_HOST}" == "true" ]]; then
      echo "Using existing host at ${BASE_URL}"
      return 0
    fi

    cat >&2 <<EOF
Host already exists at ${BASE_URL}.
This demo needs a host started with the expected GroupChat demo environment variables.
To avoid silently reusing a mismatched host, the script stops here.

Choose one:
  1. Run on a different port:
     ./group-chat-agent-relay-demo.sh http://127.0.0.1:5110
  2. Explicitly reuse the current host:
     REUSE_EXISTING_HOST=true ./group-chat-agent-relay-demo.sh ${BASE_URL}
EOF
    exit 1
  fi

  echo "Starting workflow host at ${BASE_URL}"
  (
    cd "${REPO_ROOT}"
    ASPNETCORE_URLS="${BASE_URL}" \
    GroupChat__EnableDemoReplyGeneration=false \
    GroupChat__ParticipantAgentIds__0=agent-alpha \
    GroupChat__ParticipantAgentIds__1=agent-beta \
    GroupChat__ParticipantAgentIds__2=agent-gamma \
    GroupChat__ParticipantInterestProfiles__0__ParticipantAgentId=agent-alpha \
    GroupChat__ParticipantInterestProfiles__0__MinimumInterestScore=100 \
    GroupChat__ParticipantInterestProfiles__0__DirectHintScore=100 \
    GroupChat__ParticipantInterestProfiles__1__ParticipantAgentId=agent-beta \
    GroupChat__ParticipantInterestProfiles__1__MinimumInterestScore=100 \
    GroupChat__ParticipantInterestProfiles__1__DirectHintScore=100 \
    GroupChat__ParticipantInterestProfiles__2__ParticipantAgentId=agent-gamma \
    GroupChat__ParticipantInterestProfiles__2__MinimumInterestScore=100 \
    GroupChat__ParticipantInterestProfiles__2__DirectHintScore=100 \
      dotnet run --project "${HOST_PROJECT}" >"${HOST_LOG_FILE}" 2>&1
  ) &

  host_pid=$!
  started_host="true"

  if ! wait_for_http; then
    echo "Host failed to start. Recent log output:" >&2
    tail -n 120 "${HOST_LOG_FILE}" >&2 || true
    exit 1
  fi
}

create_thread_payload="$(python3 - <<PY
import json
print(json.dumps({
    "threadId": "${THREAD_ID}",
    "displayName": "Agent relay demo",
    "participantAgentIds": ["agent-alpha", "agent-beta", "agent-gamma"],
    "participantRuntimeBindings": [
        {
            "participantAgentId": "agent-alpha",
            "workflowTarget": {
                "workflowName": "${WORKFLOW_NAME}",
                "scopeId": "${WORKFLOW_SCOPE_ID}",
            },
        },
        {
            "participantAgentId": "agent-beta",
            "workflowTarget": {
                "workflowName": "${WORKFLOW_NAME}",
                "scopeId": "${WORKFLOW_SCOPE_ID}",
            },
        },
        {
            "participantAgentId": "agent-gamma",
            "workflowTarget": {
                "workflowName": "${WORKFLOW_NAME}",
                "scopeId": "${WORKFLOW_SCOPE_ID}",
            },
        },
    ],
}, ensure_ascii=False))
PY
)"

post_agent_message_payload="$(python3 - <<PY
import json
print(json.dumps({
    "messageId": "${MESSAGE_ID}",
    "participantAgentId": "agent-alpha",
    "text": "@agent-beta @agent-gamma，请你们分别从风险和交付节奏角度点评这个 group chat 改造方案，每人三句话内。",
    "topicId": "agent-relay-demo",
    "signalKind": 2,
    "directHintAgentIds": ["agent-beta", "agent-gamma"],
}, ensure_ascii=False))
PY
)"

wait_for_thread() {
  local deadline=$((SECONDS + 15))
  while (( SECONDS < deadline )); do
    if curl -fsS "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done

  echo "Timed out waiting for thread ${THREAD_ID} to materialize" >&2
  exit 1
}

wait_for_replies() {
  local deadline=$((SECONDS + AI_WAIT_SECONDS))
  local last_response=""

  while (( SECONDS < deadline )); do
    last_response="$(curl -fsS "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}")"
    if python3 - "${last_response}" <<'PY'
import json, sys
data = json.loads(sys.argv[1])
messages = data.get("messages", [])
agent_replies = [
    x for x in messages
    if x.get("senderKind") in ("Agent", 2) and x.get("senderId") in ("agent-beta", "agent-gamma")
]
sys.exit(0 if len(agent_replies) >= 2 else 1)
PY
    then
      printf '%s\n' "${last_response}"
      return 0
    fi
    sleep 1
  done

  printf '%s\n' "${last_response}"
  echo "Timed out waiting for agent-beta and agent-gamma replies" >&2
  exit 1
}

print_messages() {
  local snapshot_json="$1"
  python3 - "${snapshot_json}" <<'PY'
import json, sys
data = json.loads(sys.argv[1])
messages = data.get("messages", [])

print("")
print("Messages:")
for index, message in enumerate(messages, start=1):
    sender = message.get("senderId") or "unknown"
    reply_to = message.get("replyToMessageId") or "-"
    text = message.get("text") or ""
    print(f"[{index}] sender={sender} replyTo={reply_to}")
    print(text)
    print("")

print(f"Total messages: {len(messages)}")
PY
}

ensure_host

echo "Creating thread ${GROUP_ID}/${THREAD_ID}"
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  -d "${create_thread_payload}" \
  "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads" >/dev/null || true

wait_for_thread

echo "Posting agent-authored question from agent-alpha"
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  -d "${post_agent_message_payload}" \
  "${BASE_URL}/api/group-chat/groups/${GROUP_ID}/threads/${THREAD_ID}/agent-messages" >/dev/null

echo "Waiting for agent-beta and agent-gamma to reply"
snapshot_json="$(wait_for_replies)"

echo "Demo completed for ${GROUP_ID}/${THREAD_ID}"
if [[ "${started_host}" == "true" ]]; then
  echo "Host log: ${HOST_LOG_FILE}"
fi
print_messages "${snapshot_json}"
