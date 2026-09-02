#!/usr/bin/env bash
set -euo pipefail

# Reproduce a scope-owner scheduled workflow run against a local or remote Aevatar API.
# Required:
#   AEVATAR_SCOPE_ID      Scope/tenant id to create or use the workflow service in.
# Required for direct HTTP mode:
#   AEVATAR_BEARER_TOKEN  NyxID bearer for the scope owner used by schedule admission.
# Optional:
#   AEVATAR_BASE_URL      Default: http://127.0.0.1:5094
#   AEVATAR_NYXID_SERVICE_SLUG  Use NyxID proxy instead of direct HTTP, e.g. aevatar-local-diag-catalog.
#   AEVATAR_SERVICE_ID    Existing workflow service id. If omitted, script save-and-binds a workflow.
#   AEVATAR_APP_ID        Service identity app id. Default: default
#   AEVATAR_NAMESPACE     Service identity namespace. Default: default
#   AEVATAR_WORKFLOW_YAML Workflow YAML file. If omitted, uses a minimal llm_call workflow.
#   AEVATAR_WORKFLOW_ID   Default: repro-scope-owner-llm-call
#   AEVATAR_WORKFLOW_NAME Default: repro_scope_owner_llm_call
#   AEVATAR_SCHEDULE_ID   Default: repro-scope-owner-llm-call-$RANDOM
#   AEVATAR_LLM_ROUTE     Optional NyxID LLM route preference for the initial ChatRequestEvent.
#   AEVATAR_NYXID_CAPABILITY_SCOPE  Default: proxy
#   AEVATAR_NYXID_SUBJECT_PLATFORM  Default: nyxid
#   AEVATAR_NYXID_SUBJECT_TENANT    Default: empty
#   AEVATAR_NYXID_SUBJECT_USER_ID   Default: AEVATAR_SCOPE_ID
#   AEVATAR_START_HOST=1  Unsupported; start Mainnet separately in Distributed/Orleans/Garnet/Kafka mode.

BASE_URL=${AEVATAR_BASE_URL:-http://127.0.0.1:5094}
SCOPE_ID=${AEVATAR_SCOPE_ID:?AEVATAR_SCOPE_ID is required}
NYXID_SERVICE_SLUG=${AEVATAR_NYXID_SERVICE_SLUG:-}
BEARER_TOKEN=${AEVATAR_BEARER_TOKEN:-}
if [[ -z "$NYXID_SERVICE_SLUG" && -z "$BEARER_TOKEN" ]]; then
  echo "AEVATAR_BEARER_TOKEN is required unless AEVATAR_NYXID_SERVICE_SLUG is set" >&2
  exit 2
fi
WORKFLOW_ID=${AEVATAR_WORKFLOW_ID:-repro-scope-owner-llm-call}
WORKFLOW_NAME=${AEVATAR_WORKFLOW_NAME:-repro_scope_owner_llm_call}
APP_ID=${AEVATAR_APP_ID:-default}
NAMESPACE=${AEVATAR_NAMESPACE:-default}
SERVICE_ID=${AEVATAR_SERVICE_ID:-}
REVISION_ID=${AEVATAR_REVISION_ID:-}
SCHEDULE_ID=${AEVATAR_SCHEDULE_ID:-repro-scope-owner-llm-call-$RANDOM}
PAYLOAD_TYPE_URL=${AEVATAR_PAYLOAD_TYPE_URL:-type.googleapis.com/aevatar.ai.ChatRequestEvent}
LLM_ROUTE=${AEVATAR_LLM_ROUTE:-}
NYXID_CAPABILITY_SCOPE=${AEVATAR_NYXID_CAPABILITY_SCOPE:-proxy}
NYXID_SUBJECT_PLATFORM=${AEVATAR_NYXID_SUBJECT_PLATFORM:-nyxid}
NYXID_SUBJECT_TENANT=${AEVATAR_NYXID_SUBJECT_TENANT:-}
NYXID_SUBJECT_USER_ID=${AEVATAR_NYXID_SUBJECT_USER_ID:-$SCOPE_ID}
START_HOST=${AEVATAR_START_HOST:-0}

if [[ "$START_HOST" == "1" ]]; then
  echo "AEVATAR_START_HOST=1 is no longer supported by this repro; start Mainnet separately in Distributed/Orleans/Garnet/Kafka mode." >&2
  exit 2
fi

api() {
  local method=$1
  local path=$2
  local body=${3:-}
  if [[ -n "$NYXID_SERVICE_SLUG" ]]; then
    if [[ -n "$body" ]]; then
      nyxid proxy request "$NYXID_SERVICE_SLUG" "$path" --method "$method" --header "Content-Type:application/json" --data @"$body" --output json || return $?
    else
      nyxid proxy request "$NYXID_SERVICE_SLUG" "$path" --method "$method" --output json || return $?
    fi
    return
  fi

  if [[ -n "$body" ]]; then
    curl -fsS -X "$method" "$BASE_URL$path" \
      -H "Authorization: Bearer $BEARER_TOKEN" \
      -H "Content-Type: application/json" \
      --data-binary @"$body"
  else
    curl -fsS -X "$method" "$BASE_URL$path" \
      -H "Authorization: Bearer $BEARER_TOKEN"
  fi
}

json_get() {
  python3 - "$1" "$2" <<'PY'
import json, sys
path = sys.argv[2].split('.')
with open(sys.argv[1], 'r', encoding='utf-8') as f:
    cur = json.load(f)
for part in path:
    if not part:
        continue
    cur = cur[part]
print(cur)
PY
}

WORK_DIR=$(mktemp -d)
WORKFLOW_YAML_FILE=${AEVATAR_WORKFLOW_YAML:-$WORK_DIR/workflow.yaml}

if [[ -z "${AEVATAR_WORKFLOW_YAML:-}" ]]; then
  cat > "$WORKFLOW_YAML_FILE" <<YAML
name: $WORKFLOW_NAME
steps:
  - id: nyxid_probe
    type: tool_call
    parameters:
      tool: nyxid_proxy
      arguments: '{"slug":"chrono-llm-public","path":"/models","method":"GET"}'
    next: summarize
  - id: summarize
    type: llm_call
    parameters:
      prompt_prefix: |
        The previous step called a NyxID proxied service. Reply with exactly: scheduled nyxid and llm ok
YAML
fi

if [[ -z "$SERVICE_ID" ]]; then
  SAVE_BODY=$WORK_DIR/save-and-bind.json
  python3 - "$SAVE_BODY" "$WORKFLOW_ID" "$WORKFLOW_NAME" "$WORKFLOW_YAML_FILE" <<'PY'
import json, pathlib, sys
out, workflow_id, workflow_name, yaml_path = sys.argv[1:]
body = {
    "workflowId": workflow_id,
    "workflowName": workflow_name,
    "displayName": workflow_name,
    "workflowYaml": pathlib.Path(yaml_path).read_text(encoding="utf-8"),
    "exposureDesired": True,
}
pathlib.Path(out).write_text(json.dumps(body), encoding="utf-8")
PY
  SAVE_RESPONSE=$WORK_DIR/save-and-bind.response.json
  api POST "/api/scopes/$SCOPE_ID/workflows:save-and-bind" "$SAVE_BODY" > "$SAVE_RESPONSE"
  SERVICE_ID=$(json_get "$SAVE_RESPONSE" "binding.serviceId")
  REVISION_ID=$(json_get "$SAVE_RESPONSE" "binding.revisionId")
  echo "serviceId=$SERVICE_ID"
  echo "revisionId=$REVISION_ID"
fi

# Give catalog/projection a short window to observe the newly bound service.
if [[ -z "${AEVATAR_SERVICE_ID:-}" ]]; then
  sleep "${AEVATAR_BINDING_SETTLE_SECONDS:-3}"
fi

SCHEDULE_BODY=$WORK_DIR/schedule.json
python3 - "$SCHEDULE_BODY" "$SCHEDULE_ID" "$SCOPE_ID" "$APP_ID" "$NAMESPACE" "$SERVICE_ID" "$REVISION_ID" "$PAYLOAD_TYPE_URL" "$LLM_ROUTE" "$NYXID_CAPABILITY_SCOPE" "$NYXID_SUBJECT_PLATFORM" "$NYXID_SUBJECT_TENANT" "$NYXID_SUBJECT_USER_ID" <<'PY'
import json, sys, pathlib
out, schedule_id, scope_id, app_id, namespace, service_id, revision_id, payload_type_url, llm_route, nyxid_capability_scope, nyxid_subject_platform, nyxid_subject_tenant, nyxid_subject_user_id = sys.argv[1:]
import base64


def varint(value):
    out = bytearray()
    while value > 0x7F:
        out.append((value & 0x7F) | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


def string_field(field_number, value):
    data = value.encode("utf-8")
    return varint((field_number << 3) | 2) + varint(len(data)) + data


payload = bytearray()
payload += string_field(1, "Run the scheduled llm_call repro.")
payload += string_field(2, schedule_id)
if llm_route:
    llm_control = string_field(5, llm_route)
    payload += varint((10 << 3) | 2) + varint(len(llm_control)) + llm_control
body = {
    "scheduleId": schedule_id,
    "displayName": schedule_id,
    "cronExpression": "17 3 * * *",
    "timezone": "UTC",
    "enabled": True,
    "serviceInvocation": {
        "identity": {"tenantId": scope_id, "appId": app_id, "namespace": namespace, "serviceId": service_id},
        "revisionId": revision_id,
        "endpointId": "chat",
        "payloadTypeUrl": payload_type_url,
        "payloadBase64": base64.b64encode(bytes(payload)).decode("ascii"),
        "auth": {
            "scopeOwnerNyxId": {
                "scope": nyxid_capability_scope,
                "ownerSubject": {
                    "platform": nyxid_subject_platform,
                    "tenant": nyxid_subject_tenant,
                    "externalUserId": nyxid_subject_user_id,
                },
            }
        },
    },
}
pathlib.Path(out).write_text(json.dumps(body), encoding="utf-8")
PY

SCHEDULE_RESPONSE=$WORK_DIR/schedule.response.json
api POST "/api/schedules" "$SCHEDULE_BODY" > "$SCHEDULE_RESPONSE"
echo "scheduleId=$(json_get "$SCHEDULE_RESPONSE" "scheduleId")"

RUN_NOW_RESPONSE=$WORK_DIR/run-now.response.json
api POST "/api/schedules/$SCHEDULE_ID:run-now" > "$RUN_NOW_RESPONSE"
echo "runNowCommandId=$(json_get "$RUN_NOW_RESPONSE" "commandId")"

echo "responses=$WORK_DIR"
echo "Inspect: curl -H 'Authorization: Bearer ***' '$BASE_URL/api/schedules/$SCHEDULE_ID'"
