#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if [[ "${AEVATAR_AUDIT_TRAIL_GUARD_SELF_TEST:-}" == "1" ]]; then
  scan_roots=("$@")
else
  scan_roots=(
    "src/workflow/Aevatar.Workflow.Abstractions/Security"
    "src/workflow/Aevatar.Workflow.Application.Abstractions/Security"
    "src/workflow/Aevatar.Workflow.Core/WorkflowArtifactFactBuilder.cs"
    "src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionArtifactMaterializationSupport.cs"
    "src/workflow/Aevatar.Workflow.Infrastructure/Reporting/WorkflowRunReportExportWriter.cs"
    "src/workflow/Aevatar.Workflow.Abstractions/workflow_execution_messages.proto"
    "src/workflow/Aevatar.Workflow.Projection/workflow_projection_transport.proto"
  )
fi

existing_roots=()
for root in "${scan_roots[@]}"; do
  if [[ -e "${root}" ]]; then
    existing_roots+=("${root}")
  fi
done

if [[ "${#existing_roots[@]}" -eq 0 ]]; then
  echo "audit_trail_guards: no scan roots found"
  exit 0
fi

failures=""

append_failure() {
  local heading="$1"
  local body="$2"
  [[ -z "${body}" ]] && return
  failures+="${heading}"$'\n'"${body}"$'\n'
}

run_rg() {
  rg -n "$@" "${existing_roots[@]}" \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!*.g.cs' \
    -g '!*.Designer.cs' || true
}

banned_field_hits="$(run_rg '(^|[^A-Za-z0-9_])(raw_body|raw_payload|request_body|response_body|payload_snippet|payloadSnippet|RawPayload|RawBody|RequestBody|ResponseBody|PayloadSnippet)([^A-Za-z0-9_]|$)')"
append_failure "Banned raw audit/report payload field names:" "${banned_field_hits}"

raw_tool_write_hits="$(run_rg 'ArgumentsJson[[:space:]]*=[[:space:]]*(toolCall|receipt)|ResultJson[[:space:]]*=[[:space:]]*receipt|data\[[^]]*"(arguments_json|result_json)"[^]]*\][[:space:]]*=[[:space:]]*toolCall')"
raw_tool_write_hits="$(printf '%s\n' "${raw_tool_write_hits}" | rg -v 'Sanitize|Sanitized|WorkflowAuditTextSanitizer' || true)"
append_failure "Tool argument/result audit writes must pass WorkflowAuditTextSanitizer:" "${raw_tool_write_hits}"

truncation_hits="$(run_rg 'Redact[[:space:]]*\([^)]*\)|Substring[[:space:]]*\(|\[[^]]*\.\.[^]]*\][[:space:]]*\+[[:space:]]*"\.\.\."|Take[[:space:]]*\([^)]*\)[^;]*\+[[:space:]]*"\.\.\."')"
truncation_hits="$(printf '%s\n' "${truncation_hits}" | rg -v 'WorkflowAuditTextSanitizer|SanitizeForDisplay|SanitizeAuditTextForDisplay|tools/ci/audit_trail_guards.sh' || true)"
append_failure "Truncation or Redact helpers cannot stand in for audit sanitization:" "${truncation_hits}"

hmac_default_hits="$(run_rg 'HmacSecret[[:space:]]*=[[:space:]]*"(secret|changeme|default|test|dev|local)"|hmac[_-]?secret[[:space:]]*[:=][[:space:]]*"(secret|changeme|default|test|dev|local)"')"
append_failure "HMAC secret defaults are forbidden:" "${hmac_default_hits}"

sanitizer_mentions="$(run_rg 'WorkflowAuditTextSanitizer|WorkflowAuditReportSanitizer')"
if [[ -z "${sanitizer_mentions}" ]]; then
  append_failure "Audit/report sanitization entry point is required:" "WorkflowAuditTextSanitizer was not found in audit/report scan roots."
fi

if [[ -n "${failures}" ]]; then
  printf '%s' "${failures}"
  exit 1
fi

echo "Audit trail guards passed."
