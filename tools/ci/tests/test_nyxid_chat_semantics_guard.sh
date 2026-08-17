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
actor_semantics_file="${nyxid_dir}/NyxIdChatRouteCandidate.cs"
system_prompt_file="${nyxid_dir}/Skills/system-prompt.md"
overlay_file="${nyxid_dir}/Skills/system-skill-overlay-default.md"
projector_file="${FIXTURE_ROOT}/src/Aevatar.Studio.Projection/Projectors/NyxIdChatRouteCandidateProjector.cs"
shared_projector_file="${FIXTURE_ROOT}/src/Aevatar.Studio.Projection/Projectors/ExternalDirectoryProjector.cs"
query_contract_file="${FIXTURE_ROOT}/src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/INyxIdChatRouteCandidateQueryPort.cs"
query_adapter_file="${FIXTURE_ROOT}/src/Aevatar.Studio.Infrastructure/ActorBacked/ProjectionNyxIdChatRouteCandidateQueryPort.cs"
tool_provider_file="${FIXTURE_ROOT}/src/Aevatar.AI.ToolProviders.Web/RouteCandidateAgentToolSource.cs"
shared_tool_provider_file="${FIXTURE_ROOT}/src/Aevatar.AI.ToolProviders.Web/ExternalDirectoryAgentToolSource.cs"
frontend_file="${FIXTURE_ROOT}/apps/aevatar-console-web/src/pages/chat/chatRouteCandidate.ts"
frontend_test_file="${FIXTURE_ROOT}/apps/aevatar-console-web/src/pages/chat/chatRouteCandidate.test.ts"
frontend_spec_file="${FIXTURE_ROOT}/apps/aevatar-console-web/src/pages/chat/chatUserPayload.spec.tsx"
frontend_doc_file="${FIXTURE_ROOT}/apps/aevatar-console-web/docs/prototypes/business-identity.html"
locale_file="${FIXTURE_ROOT}/apps/aevatar-console-web/src/locales/projectMessages.en-US.ts"
workflow_delivery_package_file="${FIXTURE_ROOT}/workflow-delivery-packages/workflow-alpha.yaml"
demo_file="${FIXTURE_ROOT}/demos/lark-interaction-probe/structured-review-shadow.yaml"
backend_test_file="${FIXTURE_ROOT}/test/Aevatar.Integration.Tests/GenericTemplateTests.cs"
nyxid_backend_test_file="${FIXTURE_ROOT}/test/Aevatar.AI.Tests/NyxIdChatUserPayloadTests.cs"
runbook_file="${FIXTURE_ROOT}/docs/operations/generic-canary.md"
workflow_file="${FIXTURE_ROOT}/workflows/generic-transform.yaml"
studio_design_cache_file="${FIXTURE_ROOT}/src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/StudioAssistant/.firecrawl/nyx-chat-wf-branding.json"

mkdir -p \
  "$(dirname -- "${proto_file}")" \
  "$(dirname -- "${readmodel_file}")" \
  "$(dirname -- "${system_prompt_file}")" \
  "$(dirname -- "${projector_file}")" \
  "$(dirname -- "${query_contract_file}")" \
  "$(dirname -- "${query_adapter_file}")" \
  "$(dirname -- "${tool_provider_file}")" \
  "$(dirname -- "${frontend_file}")" \
  "$(dirname -- "${frontend_spec_file}")" \
  "$(dirname -- "${frontend_doc_file}")" \
  "$(dirname -- "${locale_file}")" \
  "$(dirname -- "${workflow_delivery_package_file}")" \
  "$(dirname -- "${demo_file}")" \
  "$(dirname -- "${backend_test_file}")" \
  "$(dirname -- "${nyxid_backend_test_file}")" \
  "$(dirname -- "${runbook_file}")" \
  "$(dirname -- "${workflow_file}")" \
  "$(dirname -- "${studio_design_cache_file}")"

write_baseline() {
  rm -f \
    "${frontend_doc_file}" \
    "${workflow_delivery_package_file}" \
    "${studio_design_cache_file}"
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
    'public sealed record NyxIdChatRouteCandidate(string Route);' \
    > "${actor_semantics_file}"
  printf '%s\n' \
    'Use typed input, guarded operation admission, and verified read-back.' \
    > "${system_prompt_file}"
  printf '%s\n' \
    'Select the best route candidate from the loaded skill contract.' \
    > "${overlay_file}"
  printf '%s\n' \
    'syntax = "proto3";' \
    'message NyxIdDeveloperAppRotateSecretParams {' \
    '  string client_id = 1;' \
    '}' \
    'message NyxIdAssistantActionParams {' \
    '  NyxIdDeveloperAppRotateSecretParams developer_app_rotate_secret = 1;' \
    '}' \
    'message NyxIdChatRouteCandidate {' \
    '  string route_candidate = 1;' \
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
  printf '%s\n' \
    'public sealed class NyxIdChatRouteCandidateProjector { }' \
    > "${projector_file}"
  printf '%s\n' \
    'public sealed record ExternalDirectoryRow(string roleTitle, string costCenter);' \
    > "${shared_projector_file}"
  printf '%s\n' \
    'public interface INyxIdChatRouteCandidateQueryPort { }' \
    > "${query_contract_file}"
  printf '%s\n' \
    'public sealed class ProjectionNyxIdChatRouteCandidateQueryPort { }' \
    > "${query_adapter_file}"
  printf '%s\n' \
    'public sealed class RouteCandidateAgentToolSource' \
    '{' \
    '    public const string ToolName = "condition_evaluate";' \
    '}' \
    > "${tool_provider_file}"
  printf '%s\n' \
    'public sealed record ExternalDirectoryAgentToolSource(string roleTitle, string costCenter);' \
    > "${shared_tool_provider_file}"
  printf '%s\n' \
    'export type ChatRouteCandidate = { route: string };' \
    > "${frontend_file}"
  printf '%s\n' \
    'type ChatConditionalWriteFixture = { observedValue: number };' \
    'const externalOperation = "invoice_review";' \
    'const userPrompt = "Compare the candidate score to the screening threshold.";' \
    'test("domain-neutral fixtures remain valid", () => {});' \
    > "${frontend_test_file}"
  printf '%s\n' \
    'const externalOperation = "submit_invoice";' \
    'const externalEffect = "invoice.submit";' \
    'test("external operations remain opaque", () => {});' \
    > "${frontend_spec_file}"
  printf '%s\n' \
    'export default { routeCandidate: "Route candidate" };' \
    > "${locale_file}"
  printf '%s\n' \
    'name: structured_review_shadow_probe' \
    'description: Domain-neutral interaction fixture.' \
    > "${demo_file}"
  printf '%s\n' \
    'public sealed class GenericTemplateTests' \
    '{' \
    '    public const string Prompt = "Review this invoice.";' \
    '    public const string FileName = "invoice.pdf";' \
    '    public const string ImageFileName = "synthetic-invoice.png";' \
    '    public const string HyphenatedFileName = "invoice-review.pdf";' \
    '    public const string UnderscoredFileName = "invoice_approval.pdf";' \
    '    public const string SchemaName = "invoice_summary";' \
    '    public const string ProviderOperation = "list_invoices";' \
    '    public const string ProviderReviewOperation = "invoice_review";' \
    '    public const string ProviderApprovalOperation = "invoice_approval";' \
    '    public const string ProviderSubmitOperation = "submit_invoice";' \
    '    public const string ProviderSideEffect = "invoice.submit";' \
    '    public const string ExternalFixturePath = "P1-invoice-approval/probe.json";' \
    '    public const string ExternalCategory = "Expense Approval / Finance";' \
    '}' \
    > "${backend_test_file}"
  printf '%s\n' \
    'public sealed class NyxIdChatUserPayloadTests' \
    '{' \
    '    public const string Prompt = "Compare the candidate score to the screening threshold.";' \
    '    public const string ReviewOperation = "invoice_review";' \
    '    public const string ApprovalOperation = "invoice_approval";' \
    '    public const string SubmitOperation = "submit_invoice";' \
    '    public const string SideEffect = "invoice.submit";' \
    '}' \
    > "${nyxid_backend_test_file}"
  printf '%s\n' \
    '# Generic conditional-write canary' \
    > "${runbook_file}"
  printf '%s\n' \
    'name: generic_transform' \
    'description: Domain-neutral workflow fixture.' \
    > "${workflow_file}"
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

require_business_fixture_failure() {
  local target="$1"
  shift
  printf '%s\n' "$@" > "${target}"
  run_guard
  require_failure "business-specific NyxIdChat semantics"
  write_baseline
}

write_baseline
run_guard
require_success

require_business_fixture_failure \
  "${frontend_file}" \
  'public sealed class CandidateFixture { public const string Prompt = "Check the candidate score"; }'

require_business_fixture_failure \
  "${frontend_file}" \
  'public sealed class ThresholdFixture { public const string Prompt = "Choose the screening threshold"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class SkillFixture { public const string Name = "invoice-ocr-policy-review"; }'

require_business_fixture_failure \
  "${system_prompt_file}" \
  'public sealed class ToolFixture { public const string Name = "submit_invoice"; }'

require_business_fixture_failure \
  "${system_prompt_file}" \
  'public sealed class SideEffectFixture { public const string Kind = "invoice.submit"; }'

require_business_fixture_failure \
  "${system_prompt_file}" \
  'Use the fixed /invoice-approval route for every request.'

require_business_fixture_failure \
  "${system_prompt_file}" \
  'Always call the invoice_review operation before replying.'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class WorkflowFixture { public const string Name = "synthetic_invoice_review"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class HyphenatedWorkflowFixture { public const string Name = "synthetic-invoice-review"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class ExtractFixture { public const string Name = "invoice_file_extract"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class MatchFixture { public const string Name = "invoice_match"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class WorkflowNameFixture { public const string Name = "invoice_pdf_workflow"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class WorkflowIdFixture { public const string Id = "invoice-pdf-extraction-workflow"; }'

require_business_fixture_failure \
  "${backend_test_file}" \
  'public sealed class RunFixture { public const string Id = "run-finance-alpha"; }'

require_business_fixture_failure \
  "${frontend_doc_file}" \
  '<div class="run-name">Invoice Classifier</div>'

require_business_fixture_failure \
  "${frontend_doc_file}" \
  '<div class="run-team">Finance Ops</div>'

require_business_fixture_failure \
  "${frontend_doc_file}" \
  '<div class="run-name">Invoice PDF Workflow</div>'

require_business_fixture_failure \
  "${frontend_doc_file}" \
  '<div class="history-title">Invoice PDF intake flow</div>'

printf '%s\n' \
  'syntax = "proto3";' \
  'message NyxIdChatCandidateScreeningEvidence {' \
  '  string evidence_id = 1;' \
  '}' \
  > "${proto_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class NyxIdChatReimbursementEvidence { }' \
  > "${actor_semantics_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class NyxIdChatExpenseClaimEvidence { }' \
  > "${actor_semantics_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class NyxIdChatApplicantEvidence { }' \
  > "${actor_semantics_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class NyxIdChatTaskDomainDocument { }' \
  > "${projector_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public interface INyxIdChatRouteCandidateQueryPort' \
  '{' \
  '    string trackerTableId { get; }' \
  '}' \
  > "${query_contract_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class ProjectionNyxIdChatRouteCandidateQueryPort' \
  '{' \
  '    public string cost_center = string.Empty;' \
  '}' \
  > "${query_adapter_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class DomainToolSource' \
  '{' \
  '    public const string ToolName = "candidate_screening_evidence_commit";' \
  '}' \
  > "${tool_provider_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'Commit typed reimbursement evidence before any provider write.' \
  > "${overlay_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'Always select the FIN-01 workflow route.' \
  > "${system_prompt_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'name: expense_claim_approval' \
  'description: Renamed concrete business package.' \
  > "${workflow_delivery_package_file}"
run_guard
require_failure "bundled workflow delivery packages"
write_baseline

printf '%s\n' \
  'export type ChatCandidateScreeningEvidence = { score: number };' \
  > "${frontend_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'type ChatApplicantScreeningEvidence = { observedValue: number };' \
  'test("forbidden fixture", () => {});' \
  > "${frontend_test_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'name: reimbursement_review_probe' \
  'description: Concrete business demo.' \
  > "${demo_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'public sealed class BudgetVarianceFixture { }' \
  > "${backend_test_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  '# Candidate screening production runbook' \
  > "${runbook_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  'name: expense_claim_review' \
  'description: Concrete business workflow.' \
  > "${workflow_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

printf '%s\n' \
  '{"markdown":"domain-neutral generated design output"}' \
  > "${studio_design_cache_file}"
run_guard
require_failure "production-source design cache artifacts"
write_baseline

printf '%s\n' \
  'export default { duplicatePolicy: "Preserve exact duplicate relationships" };' \
  > "${locale_file}"
run_guard
require_failure "business-specific NyxIdChat semantics"
write_baseline

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
