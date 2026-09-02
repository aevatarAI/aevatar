#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

fail() {
  echo "Agent turn tool catalog guard failed: $*"
  exit 1
}

require_pattern() {
  local pattern="$1"
  local file="$2"
  local message="$3"
  rg -q -- "${pattern}" "${file}" || fail "${message}"
}

host_composition="src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"
catalog_contract="src/Aevatar.AI.Abstractions/ToolProviders/AgentTurnToolCatalog.cs"
discovery_contract="src/Aevatar.AI.Abstractions/ToolProviders/AgentToolDiscoveryService.cs"
telemetry_contract="src/Aevatar.AI.Abstractions/ToolProviders/AgentTurnToolCatalogTelemetry.cs"
catalog_tests="test/Aevatar.AI.Tests/AgentTurnToolCatalogTests.cs"
discovery_tests="test/Aevatar.AI.ToolProviders.ToolSetRegistry.Tests/AgentToolDiscoveryServiceTests.cs"
composition_tests="test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs"
workflow_policy="src/workflow/Aevatar.Workflow.Abstractions/WorkflowToolCatalogPolicies.cs"
voice_policy="src/Aevatar.Foundation.VoicePresence.Abstractions/VoiceToolCatalogSnapshotValidator.cs"
baseline_manifest="tools/ci/agent_turn_tool_catalog_baseline.json"
profile_policy="src/platform/Aevatar.GAgentService.Abstractions/AgentProfiles/AgentProfilePolicies.cs"
profile_binding_proto="src/platform/Aevatar.GAgentService.Abstractions/Protos/agent_profiles.proto"
profile_binding_actor="src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileNamespaceGAgent.cs"
agent_run_proto="agents/Aevatar.GAgents.NyxidChat/protos/agent_run.proto"
agent_run_executor="agents/Aevatar.GAgents.NyxidChat/AgentRunReplyGenerationExecutor.cs"
agent_run_actor="agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs"
agent_run_card_actor="agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.LarkCardDelivery.cs"
conversation_state_proto="agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_state.proto"
conversation_events_proto="agents/Aevatar.GAgents.Channel.Runtime/protos/conversation_events.proto"
conversation_actor="agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs"
channel_executor_tests="test/Aevatar.GAgents.ChannelRuntime.Tests/AgentRunReplyGenerationExecutorTests.cs"
conversation_tests="test/Aevatar.GAgents.ChannelRuntime.Tests/ConversationGAgentTargetActorIdTests.cs"
connected_selector="agents/Aevatar.GAgents.NyxidChat/AgentProfiles/StreamingAgentProfileConnectedOperationSelector.cs"
connected_materializer="agents/Aevatar.GAgents.NyxidChat/AgentProfiles/AgentTurnToolCatalogMaterializer.cs"
connected_selector_tests="test/Aevatar.AI.Tests/StreamingAgentProfileConnectedOperationSelectorTests.cs"
connected_materializer_tests="test/Aevatar.AI.Tests/AgentTurnToolCatalogMaterializerTests.cs"

workspace_block="$({
  awk '
    /options\.AddToolSet\(/ {
      block = $0
      capture = 1
      next
    }
    capture {
      block = block ORS $0
      if ($0 ~ /\);[[:space:]]*$/) {
        if (block ~ /ToolSetNames\.WorkspaceDefault,/) {
          print block
          exit
        }
        block = ""
        capture = 0
      }
    }
  ' "${host_composition}"
} || true)"

[[ -n "${workspace_block}" ]] || fail "workspace.default registration block is missing."

actual_workspace_members="$({
  printf '%s\n' "${workspace_block}" \
    | rg -o 'ToolSetNames\.[A-Za-z0-9_]+' \
    | sort -u
} || true)"
expected_workspace_members="$(printf '%s\n' \
  'ToolSetNames.AevatarInvoke' \
  'ToolSetNames.AevatarObserve' \
  'ToolSetNames.ChatCore' \
  'ToolSetNames.SkillRuntime' \
  'ToolSetNames.WebRuntime' \
  'ToolSetNames.WorkspaceDefault' \
  | sort)"

if [[ "${actual_workspace_members}" != "${expected_workspace_members}" ]]; then
  printf '%s\n' "${actual_workspace_members}"
  fail "workspace.default must contain only chat.core, web.runtime, skill.runtime, aevatar.invoke, and aevatar.observe."
fi

for forbidden in \
  SkillAuthoring \
  NyxIdPrivileged \
  NyxIdExecution \
  StorageRead \
  StorageWrite \
  ChannelCore \
  ChannelLark \
  ChannelTelegram \
  StudioLocal \
  ResponsesState \
  NyxIdConnectedServices; do
  if printf '%s\n' "${workspace_block}" | rg -q "ToolSetNames\.${forbidden}\b"; then
    fail "workspace.default contains forbidden opt-in member ToolSetNames.${forbidden}."
  fi
done

duplicate_tool_set_names="$({
  sed -n 's/.*public const string [A-Za-z0-9_]* = "\([^"]*\)";.*/\1/p' \
    src/Aevatar.AI.ToolProviders.ToolSetRegistry/ToolSetNames.cs \
    | sort \
    | uniq -d
} || true)"
[[ -z "${duplicate_tool_set_names}" ]] || {
  printf '%s\n' "${duplicate_tool_set_names}"
  fail "ToolSetNames contains duplicate registered names."
}

require_pattern \
  'StringComparer\.OrdinalIgnoreCase' \
  "${discovery_contract}" \
  "request-local discovery must compare names case-insensitively."
require_pattern \
  'ReferenceEquals\(existing\.Tool, tool\)' \
  "${discovery_contract}" \
  "request-local discovery must distinguish exact objects from repeated includes."
require_pattern \
  'AgentToolDiscoveryFailureCode\.ToolNameCollision' \
  "${discovery_contract}" \
  "request-local duplicate names must return a typed collision."
require_pattern \
  'DiscoverAsync_ShouldFailClosedOnCaseInsensitiveDifferentObjectCollision' \
  "${discovery_tests}" \
  "the case-insensitive exact-object collision proof test is missing."

require_pattern 'new\(8, 48 \* 1024\)' "${catalog_contract}" \
  "ordinary text catalog optimization target / schema budget must remain 8 tools / 48 KiB."
require_pattern \
  'new\(8, 48 \* 1024, MaximumConnectedReadToolCount: 3, MaximumConnectedWriteToolCount: 1\)' \
  "${catalog_contract}" \
  "connected operations must retain the 8-tool target / 48 KiB schema limit with 3 reads and 1 write."
require_pattern 'MaximumCandidates = 64' "${connected_selector}" \
  "the connected-operation selector presentation index must remain bounded."
require_pattern 'Tools = null' "${connected_selector}" \
  "the connected-operation selector must remain tool-free."
require_pattern 'ChatStreamAsync' "${connected_selector}" \
  "the connected-operation selector must use the streaming LLM path."
require_pattern 'eligibleToolNames: available' "${connected_materializer}" \
  "the connected-operation selector must receive only the authority-filtered ceiling."
require_pattern 'connected_service_connection_ambiguous' "${connected_materializer}" \
  "multi-connection connected-service selectors must fail to clarification."
require_pattern 'PrepareAsync_BroadConnectedSelector_ShouldCommitOnlyBoundedExactSelection' \
  "${connected_materializer_tests}" \
  "the actor-owned bounded exact-selection replay proof test is missing."
require_pattern 'MaterializeAsync_BroadSelectorAcrossConnections_ShouldRequireClarification' \
  "${connected_materializer_tests}" \
  "the multi-connection clarification proof test is missing."
require_pattern 'MaterializeAsync_MultipleBroadWrites_ShouldRequireClarificationWithoutSelector' \
  "${connected_materializer_tests}" \
  "multiple broad write candidates must require clarification before selection."
require_pattern 'VerifiedAuthorizationContinuation_BroadProfile_ShouldSelectInsideExactVerifiedService' \
  "${connected_materializer_tests}" \
  "authorization continuation must narrow to the verified UserService before bounded selection."
require_pattern 'VerifiedAuthorizationContinuation_ProfileMemberWithoutSkill_ShouldKeepCommittedTaskPolicy' \
  "${connected_materializer_tests}" \
  "authorization continuation must preserve a committed non-skill task policy."
require_pattern 'AuthorityFilteredExistingOperation_ShouldNotRequestConnectionAgain' \
  "${connected_materializer_tests}" \
  "authority-filtered existing operations must not be mistaken for a missing connection."
require_pattern 'SelectAsync_InvalidOutput_ShouldFailClosed' "${connected_selector_tests}" \
  "the connected-operation selector malformed-output proof test is missing."
require_pattern 'new\(6, 32 \* 1024\)' "${catalog_contract}" \
  "voice catalog optimization target / schema budget must remain 6 tools / 32 KiB."
require_pattern 'new\(16, 128 \* 1024\)' "${catalog_contract}" \
  "workflow/admin catalog optimization target / schema budget must remain 16 tools / 128 KiB."
require_pattern 'new\(6, 64 \* 1024\)' "${catalog_contract}" \
  "coding catalog optimization target / schema budget must remain 6 tools / 64 KiB."
require_pattern 'MaximumWorkflowToolCount = 16' "${workflow_policy}" \
  "workflow proof must retain its reviewed 16-tool optimization target."
require_pattern 'MaximumWorkflowSchemaBytes = 128 \* 1024' "${workflow_policy}" \
  "workflow publication schema budget must remain pinned to 128 KiB."
require_pattern 'MaximumToolCount = 6' "${voice_policy}" \
  "voice persisted proof must retain its reviewed 6-tool optimization target."
require_pattern 'MaximumSchemaBytes = 32 \* 1024' "${voice_policy}" \
  "voice persisted proof validation must remain pinned to 32 KiB."
require_pattern 'WorkflowCatalog_ShouldAcceptToolCountAboveOptimizationTargetWithoutTruncating' "${catalog_tests}" \
  "catalog tool-count targets must have an acceptance-without-truncation proof test."

require_pattern 'CatalogDigest_ShouldBeStableAcrossOneHundredInputPermutations' "${catalog_tests}" \
  "the 100-permutation canonical digest proof test is missing."
require_pattern 'CatalogDigest_ShouldMatchCanonicalSnapshot' "${catalog_tests}" \
  "the canonical digest snapshot test is missing."
require_pattern \
  'sha256:ac8a952508a88afb07f3ab8cbcfa47688a65e394bff4f6720fbf60eec498e6c0' \
  "${catalog_tests}" \
  "the reviewed canonical digest snapshot changed without an explicit migration."

require_pattern 'AddAevatarMainnetHost_ShouldRegisterDefaultToolSets' "${composition_tests}" \
  "the Mainnet route topology snapshot test is missing."
require_pattern 'total:13:11744' "${composition_tests}" \
  "the reviewed workspace.default count/schema snapshot changed without an explicit migration."
require_pattern \
  'sha256:9109bc325b6c4eea8693c8a0f6bf023ec74a7bdef4e0c521fb2a930797b71fd7' \
  "${composition_tests}" \
  "the reviewed workspace.default catalog digest changed without an explicit migration."
require_pattern '"unique_tool_count": 68' "${baseline_manifest}" \
  "the measured pre-change tool-count baseline is missing."
require_pattern '"canonical_schema_bytes": 48328' "${baseline_manifest}" \
  "the measured pre-change schema-byte baseline is missing."
command -v jq >/dev/null 2>&1 || fail "jq is required to validate the catalog baseline manifest."
jq -e '
  . as $root
  | (.before.tools | length) == $root.before.unique_tool_count
    and ([.before.tools[].schema_bytes] | add) == $root.before.canonical_schema_bytes
    and (.after.tools | length) == $root.after.unique_tool_count
    and ([.after.tools[].schema_bytes] | add) == $root.after.canonical_schema_bytes
' "${baseline_manifest}" >/dev/null \
  || fail "the baseline manifest tool names/schema bytes do not match its measured totals."
require_pattern '"unique_tool_percent": 80.9' "${baseline_manifest}" \
  "the baseline manifest must prove the reviewed tool-count reduction."
require_pattern '"canonical_schema_bytes_percent": 75.9' "${baseline_manifest}" \
  "the baseline manifest must prove at least a 60% schema-byte reduction."
for forbidden_type in \
  NyxIdAgentToolSource \
  NyxIdExecutionAgentToolSource \
  ChronoStorageWriteAgentToolSource \
  OrnnAuthoringAgentToolSource; do
  require_pattern \
    "workspace\.Sources\.Should\(\)\.NotContain\(source => source is ${forbidden_type}\)" \
    "${composition_tests}" \
    "the Mainnet test must reject ${forbidden_type} from workspace.default."
done

for metric in \
  registered \
  discovered \
  authority \
  final \
  forwarded \
  filtered \
  rejected \
  restricted_empty \
  schema_bytes \
  degradation \
  tool_round \
  outcome \
  time_to_first_output; do
  require_pattern \
    "aevatar\.agent_turn_tool_catalog\.${metric}" \
    "${telemetry_contract}" \
    "catalog telemetry is missing ${metric}."
done
require_pattern 'CatalogTelemetry_ShouldEmitLowCardinalityCountsAndKeepDigestOnTrace' "${catalog_tests}" \
  "catalog metric/digest cardinality proof test is missing."

require_pattern 'max_owned_tool_count' src/Aevatar.AI.Abstractions/ai_messages.proto \
  "sealed profiles must persist their owned-tool count ceiling."
require_pattern 'max_schema_bytes' src/Aevatar.AI.Abstractions/ai_messages.proto \
  "sealed profiles must persist their schema-byte ceiling."
require_pattern 'workflow-agent-turn-tool-catalog/v1' "${workflow_policy}" \
  "new workflow definitions and runs must pin the current catalog policy."
require_pattern 'CanaryCohortBasisPoints = 500' "${profile_policy}" \
  "Agent Profile rollout must start at 5%."
require_pattern 'ExpandedCohortBasisPoints = 2_500' "${profile_policy}" \
  "Agent Profile rollout must advance through 25%."
require_pattern 'previous_reviewed_target' "${profile_binding_proto}" \
  "system rollout must persist the previous reviewed profile target."
require_pattern 'ROLLOUT_BASELINE_REQUIRED' "${profile_binding_actor}" \
  "partial rollout must fail closed without a reviewed baseline."
require_pattern 'IsNextRolloutStage' "${profile_binding_actor}" \
  "system rollout must enforce the 5% to 25% to 100% sequence."
require_pattern 'RecordShadowCandidate' "${telemetry_contract}" \
  "shadow mode must observe candidate proof/digest without final injection."

require_pattern 'agent_profile_snapshot = 28' "${agent_run_proto}" \
  "Channel AgentRun state must pin the sealed profile snapshot."
require_pattern 'agent_profile_turn_authority = 29' "${agent_run_proto}" \
  "Channel AgentRun state must pin the selected turn authority."
require_pattern 'tool_catalog_proof = 30' "${agent_run_proto}" \
  "Channel AgentRun state must pin the final tool catalog proof."
require_pattern 'tool_catalog_policy_version = 31' "${agent_run_proto}" \
  "Channel AgentRun state must pin the catalog policy version."
require_pattern 'ResolvePersistedTurnCatalogAsync' "${agent_run_executor}" \
  "Channel continuation execution must rematerialize and verify its persisted catalog."
require_pattern 'AgentProfileSnapshotCodec\.Verify\(replyRequest\.AgentProfile\)' "${agent_run_executor}" \
  "conversation-pinned profile snapshots must be verified before channel catalog planning."
require_pattern 'ready\.AgentProfile = State\.GenerationStep\.AgentProfileSnapshot\.Clone\(\)' "${agent_run_actor}" \
  "ordinary Channel ready events must carry the run profile snapshot back to Conversation."
require_pattern 'completed\.AgentProfile = State\.GenerationStep\.AgentProfileSnapshot\.Clone\(\)' "${agent_run_card_actor}" \
  "CardKit completion must carry the run profile snapshot back to Conversation."
require_pattern 'AgentProfileSnapshot agent_profile = 18' "${conversation_state_proto}" \
  "Conversation actor state must own its immutable profile pin."
require_pattern 'ConversationAgentProfilePinnedEvent' "${conversation_events_proto}" \
  "Conversation profile pin must be a committed typed event."
require_pattern 'runCopy\.AgentProfile = State\.AgentProfile\?\.Clone\(\)' "${conversation_actor}" \
  "later Channel runs must receive the Conversation-owned profile snapshot."
require_pattern 'EnsureAgentProfilePinnedAsync\(evt\.AgentProfile, evt\.RunId\)' "${conversation_actor}" \
  "Conversation must reconcile every terminal run profile against its pin."
require_pattern 'BuildInitialStepState_WhenConversationCarriesPinnedProfile_ShouldNotResolveCurrentBinding' "${channel_executor_tests}" \
  "the Channel binding-drift replay proof test is missing."
require_pattern 'HandleLlmReplyReadyAsync_WhenProfileDiffersFromConversationPin_ShouldFailClosed' "${conversation_tests}" \
  "the Conversation profile mismatch fail-closed proof test is missing."

echo "Agent turn tool catalog guard passed."
