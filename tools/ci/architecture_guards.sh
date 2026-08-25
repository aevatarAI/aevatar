#!/usr/bin/env bash

set -euo pipefail

scan_projection_document_reader_list_async() {
  local projection_document_reader_scan_roots=("$@")
  local projection_document_reader_file
  local projection_document_reader_names
  local projection_document_reader_name

  while IFS= read -r projection_document_reader_file; do
    [[ -z "${projection_document_reader_file}" ]] && continue

    projection_document_reader_names="$(
      {
        rg --no-filename --no-line-number -o \
          'IProjectionDocumentReader<[^>]+>[[:space:]]+_?[A-Za-z][A-Za-z0-9_]*' \
          "${projection_document_reader_file}" \
          | rg -o '_?[A-Za-z][A-Za-z0-9_]*$' \
          | sort -u
      } || true
    )"

    while IFS= read -r projection_document_reader_name; do
      [[ -z "${projection_document_reader_name}" ]] && continue

      rg -n --with-filename \
        "(^|[^A-Za-z0-9_])${projection_document_reader_name}\\.ListAsync\\(" \
        "${projection_document_reader_file}" || true
    done <<< "${projection_document_reader_names}"
  done < <(rg -l "IProjectionDocumentReader<" "${projection_document_reader_scan_roots[@]}" 2>/dev/null)
}

if [[ "${AEVATAR_ARCHITECTURE_GUARDS_RUN_PROJECTION_DOCUMENT_READER_SCAN_ONLY:-}" == "1" ]]; then
  scan_projection_document_reader_list_async "$@"
  exit 0
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ZERO_SHA="0000000000000000000000000000000000000000"
DIFF_MODE="worktree"
DIFF_RANGE_VALUE="${DIFF_RANGE:-}"

if [[ -n "${DIFF_RANGE_VALUE}" ]]; then
  DIFF_MODE="range"
elif [[ "${GITHUB_EVENT_NAME:-}" == "pull_request" && -n "${GITHUB_BASE_REF:-}" ]]; then
  if ! git rev-parse --verify "origin/${GITHUB_BASE_REF}" >/dev/null 2>&1; then
    git fetch --no-tags --depth=1 origin "+refs/heads/${GITHUB_BASE_REF}:refs/remotes/origin/${GITHUB_BASE_REF}"
  fi
  DIFF_RANGE_VALUE="origin/${GITHUB_BASE_REF}...HEAD"
  DIFF_MODE="range"
elif [[ -n "${GITHUB_EVENT_BEFORE:-}" && "${GITHUB_EVENT_BEFORE}" != "${ZERO_SHA}" && -n "${GITHUB_SHA:-}" ]]; then
  DIFF_RANGE_VALUE="${GITHUB_EVENT_BEFORE}...${GITHUB_SHA}"
  DIFF_MODE="range"
fi

if [[ "${DIFF_MODE}" == "range" ]]; then
  echo "Architecture guards diff mode: range (${DIFF_RANGE_VALUE})"
else
  echo "Architecture guards diff mode: worktree (HEAD vs working tree)"
fi

if [ -d "src/Aevatar.Host.Api" ] || [ -d "src/Aevatar.Host.Gateway" ]; then
  echo "Legacy host projects are forbidden. Use subsystem hosts only."
  exit 1
fi

# Refactor (v1/issue1454-first):
#   Old: 无中央 .NET 版本源,各项目自带或默认 1.0.0.0,无中央事实
#   New: Directory.Build.props 中央 v0.1.0-beta + guard 防回归
check_directory_build_version_guard() {
  local props_file="Directory.Build.props"

  if ! rg -q "<Version>0\.1\.0-beta</Version>" "${props_file}"; then
    echo "Directory.Build.props must define <Version>0.1.0-beta</Version>."
    exit 1
  fi

  if ! rg -q "<VersionPrefix>0\.1\.0</VersionPrefix>" "${props_file}"; then
    echo "Directory.Build.props must define <VersionPrefix>0.1.0</VersionPrefix>."
    exit 1
  fi

  if ! rg -q "<VersionSuffix>beta</VersionSuffix>" "${props_file}"; then
    echo "Directory.Build.props must define <VersionSuffix>beta</VersionSuffix>."
    exit 1
  fi

  if ! rg -q "<PackageVersion>0\.1\.0-beta</PackageVersion>" "${props_file}"; then
    echo "Directory.Build.props must define <PackageVersion>0.1.0-beta</PackageVersion>."
    exit 1
  fi

  if ! rg -q "<InformationalVersion>0\.1\.0-beta</InformationalVersion>" "${props_file}"; then
    echo "Directory.Build.props must define <InformationalVersion>0.1.0-beta</InformationalVersion>."
    exit 1
  fi

  if ! rg -q "<AssemblyVersion>0\.1\.0\.0</AssemblyVersion>" "${props_file}"; then
    echo "Directory.Build.props must define <AssemblyVersion>0.1.0.0</AssemblyVersion>."
    exit 1
  fi

  if ! rg -q "<FileVersion>0\.1\.0\.0</FileVersion>" "${props_file}"; then
    echo "Directory.Build.props must define <FileVersion>0.1.0.0</FileVersion>."
    exit 1
  fi
}

check_directory_build_version_guard

check_code_execution_nyxid_route_boundary() {
  local expected_files
  local resolver_files
  local parser_files

  expected_files="$(printf '%s\n' \
    'src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdCodeExecutionPort.Durable.cs' \
    'src/Aevatar.AI.Infrastructure.ChronoSandbox/NyxIdCodeExecutionPort.cs' \
    'src/Aevatar.AI.ToolProviders.NyxId/NyxIdCodeExecutionRoutePolicyReconciler.cs' \
    'src/Aevatar.AI.ToolProviders.NyxId/NyxIdCodeExecutionRouteResolver.cs' \
    'src/Aevatar.AI.ToolProviders.NyxId/NyxIdCodeExecutionWorkflowCapabilitySource.cs' \
    | sort)"

  resolver_files="$(
    rg -l 'NyxIdCodeExecutionRouteResolver' src \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      | sort
  )"
  if [[ "${resolver_files}" != "${expected_files}" ]]; then
    printf '%s\n' "${resolver_files}"
    echo "The NyxID code-execution route resolver is private to code_execute runtime and readiness."
    exit 1
  fi

  parser_files="$(
    rg -l 'ParseUserServiceRoutes' src \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      | sort
  )"
  local expected_parser_files
  expected_parser_files="$(printf '%s\n' \
    'src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiAccessContracts.cs' \
    'src/Aevatar.AI.ToolProviders.NyxId/NyxIdUserServiceRouteConverger.cs' \
    | sort)"
  if [[ "${parser_files}" != "${expected_parser_files}" ]]; then
    printf '%s\n' "${parser_files}"
    echo "Exact NyxID route parsing must stay behind the generic route authority reader and must not change ordinary inventory consumers."
    exit 1
  fi
}

check_code_execution_nyxid_route_boundary

# Refactor (v1/issue1466-first):
#   Old: ChannelInboundEvent.registration_token looked like a durable runtime credential carrier.
#   New: proto field 9/name are reserved and mappers/docs stay credential-free.
#   Principle: channel inbound durable facts must not contain runtime tokens.
check_channel_inbound_no_runtime_credential() {
  local proto_file="agents/Aevatar.GAgents.Channel.Runtime/protos/channel_bot_registration.proto"
  local runner_file="agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs"
  local canon_dir="docs/canon"

  if ! awk '
    /message ChannelInboundEvent[[:space:]]*\{/ { in_msg = 1 }
    in_msg && /reserved[[:space:]]+9[[:space:]]*;/ { has_reserved_number = 1 }
    in_msg && /reserved[[:space:]]+"registration_token"[[:space:]]*;/ { has_reserved_name = 1 }
    in_msg && /^[[:space:]]*\}/ { in_msg = 0 }
    END { exit(has_reserved_number && has_reserved_name ? 0 : 1) }
  ' "${proto_file}"; then
    echo "ChannelInboundEvent must reserve field 9 and name registration_token."
    exit 1
  fi

  if awk '
    /message ChannelInboundEvent[[:space:]]*\{/ { in_msg = 1; next }
    in_msg && /^[[:space:]]*\}/ { in_msg = 0 }
    in_msg && /^[[:space:]]*string[[:space:]]+registration_token[[:space:]]*=/ { print FILENAME ":" FNR ":" $0; found = 1 }
    END { exit(found ? 0 : 1) }
  ' "${proto_file}"; then
    echo "ChannelInboundEvent.registration_token is forbidden; keep runtime credentials out of durable inbound facts."
    exit 1
  fi

  if rg -n "RegistrationToken[[:space:]]*=" "${runner_file}"; then
    echo "ToInboundEvent must not write RegistrationToken. Runtime tokens belong in transient context only."
    exit 1
  fi

  if rg -n "registration_token" "${canon_dir}"; then
    echo "Canonical channel inbound durable facts docs must not list registration_token."
    exit 1
  fi
}

check_channel_inbound_no_runtime_credential

check_voice_tool_credential_ref_boundary() {
  local report

  set +e
  report="$(
    rg -n "IVoiceSessionCredentialStore|VoiceVolatileSessionCredentialStore|VoiceCallerCredentialScope|google\.protobuf\.Any[[:space:]]+tool_context" \
      src test docs \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      -g '!*.g.cs' \
      -g '!*.Designer.cs' \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (file == "tools/ci/architecture_guards.sh")
    next;

  if (text ~ /ShouldNotContain\("nyx_id_access_token"\)/)
    next;

  print $0;
}'
  )"
  local status=$?
  set -e

  if [[ ${status} -ne 0 && ${status} -ne 1 ]]; then
    echo "Voice tool credential boundary guard execution failed."
    exit "${status}"
  fi

  if [ -n "${report}" ]; then
    echo "${report}"
    echo "Voice tool caller credentials must use typed VoiceToolExecutionContext. Process-local session token stores, VoiceCallerCredentialScope, and raw-bearing Any tool_context are forbidden."
    exit 1
  fi
}

check_voice_tool_credential_ref_boundary

check_workflow_core_interaction_boundary() {
  local workflow_core_dir="src/workflow/Aevatar.Workflow.Core"
  local report

  set +e
  report="$(
    rg -n "Aevatar\.GAgents\.Channel\.Abstractions|MessageContent|LarkMessageComposer|open-apis/im/v1/messages|interactive_card|raw Lark|raw card JSON|\"type\"[[:space:]]*:[[:space:]]*\"template\"" "${workflow_core_dir}" \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      -g '!*.g.cs' \
      -g '!*.Designer.cs'
  )"
  local status=$?
  set -e

  if [[ ${status} -ne 0 && ${status} -ne 1 ]]; then
    echo "Workflow Core interaction boundary guard execution failed."
    exit "${status}"
  fi

  if [ -n "${report}" ]; then
    echo "${report}"
    echo "Workflow Core must carry typed InteractionSpec only; channel content and raw card payloads belong at the channel boundary."
    exit 1
  fi

  set +e
  report="$(
    rg -n "Metadata[[:space:]]*\[[[:space:]]*\"interaction\"|metadata[[:space:]]*\[[[:space:]]*\"interaction\"" \
      src/workflow agents \
      -g '*Human*Interaction*.cs' \
      -g '*Notify*.cs' \
      -g '*Notification*.cs' \
      -g '!**/bin/**' \
      -g '!**/obj/**' \
      -g '!*.g.cs' \
      -g '!*.Designer.cs'
  )"
  status=$?
  set -e

  if [[ ${status} -ne 0 && ${status} -ne 1 ]]; then
    echo "HITL/notify metadata interaction guard execution failed."
    exit "${status}"
  fi

  if [ -n "${report}" ]; then
    echo "${report}"
    echo "HITL/notify control must consume typed interaction contracts, not Metadata[\"interaction\"] bags."
    exit 1
  fi
}

check_workflow_core_interaction_boundary

bash tools/ci/aevatar_oauth_client_es_acl_guard.sh
bash tools/ci/static_service_activation_guard.sh || exit $?
bash tools/ci/nyxid_conformance_guard.sh

# Refactor (iter158/cluster-001): stream-RPC outcome abstractions were deleted in PR #1165.
# Do not reintroduce stream subscribe + first-outcome wait abstractions as an RPC reply path.
set +e
stream_rpc_report="$(
  rg -n "StreamActorOutcomeChannel|DefaultCommandOutcomeDispatchService|class.*OutcomeChannel.*: Subscription|Outcome\.WaitAsync\(ct\)" \
    src \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!*.g.cs' \
    -g '!*.Designer.cs' \
    | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;

  print $0;
}'
)"
stream_rpc_status=$?
set -e

if [[ ${stream_rpc_status} -ne 0 && ${stream_rpc_status} -ne 1 ]]; then
  echo "Stream-RPC regression guard execution failed."
  exit "${stream_rpc_status}"
fi

if [ -n "${stream_rpc_report}" ]; then
  echo "ERROR: stream-RPC abstraction reintroduced (forbidden per PR #1165 / issue #1161)"
  echo "${stream_rpc_report}"
  exit 1
fi

if rg -n "Aevatar\.Host\.Api|Aevatar\.Host\.Gateway" aevatar.slnx; then
  echo "Solution must not include legacy host projects."
  exit 1
fi

set +e
# Refactor (iter92/cluster-644):
#   Old pattern: workflow-local Telegram bridge GAgents/proto owned Telegram send/wait-reply behavior inside workflow extensions.
#   New principle: workflow must not reintroduce those bridge actors; Telegram traffic stays on the NyxID relay path.
workflow_telegram_bridge_report="$(
  rg -n "workflow\.telegram-(bridge|user-bridge|wait-reply)|TelegramBridgeGAgent|TelegramUserBridgeGAgent|TelegramWaitReplyGAgent|TelegramGetUpdatesExternalLinkTransport|telegram_wait_reply|AddWorkflowBridgeExtensions|Aevatar\.Workflow\.Extensions\.Bridge" \
    src test tools docs .cursor/skills aevatar.slnx \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!tools/ci/architecture_guards.sh'
)"
workflow_telegram_bridge_status=$?
set -e

if [[ ${workflow_telegram_bridge_status} -ne 0 && ${workflow_telegram_bridge_status} -ne 1 ]]; then
  echo "Workflow Telegram bridge retired-token guard execution failed."
  exit "${workflow_telegram_bridge_status}"
fi

if [ -n "${workflow_telegram_bridge_report}" ]; then
  echo "${workflow_telegram_bridge_report}"
  echo "Workflow-local Telegram bridge is retired. Keep Telegram traffic on the NyxID relay path."
  exit 1
fi

set +e
nyx_relay_agent_builder_flow_report="$(
  rg -n "NyxRelayAgentBuilderFlow" \
    agents src test \
    -g '!**/bin/**' \
    -g '!**/obj/**'
)"
nyx_relay_agent_builder_flow_status=$?
set -e

if [[ ${nyx_relay_agent_builder_flow_status} -ne 0 && ${nyx_relay_agent_builder_flow_status} -ne 1 ]]; then
  echo "Retired Nyx relay agent-builder flow guard execution failed."
  exit "${nyx_relay_agent_builder_flow_status}"
fi

if [ -n "${nyx_relay_agent_builder_flow_report}" ]; then
  echo "${nyx_relay_agent_builder_flow_report}"
  echo "NyxRelayAgentBuilderFlow was deleted in #1306. Do not reintroduce it in production code or tests."
  exit 1
fi

if rg -n "docs\\\\SOLUTION_AUDIT_REPORT_" aevatar.slnx; then
  echo "Working audit documents must not be added to solution."
  exit 1
fi

if rg -n "docs[\\\\/]agents-working-space[\\\\/]" aevatar.slnx; then
  echo "Working documents under docs/agents-working-space must not be added to solution."
  exit 1
fi

if rg -n "GetAwaiter\(\)\.GetResult\(\)" src; then
  echo "Found sync-over-async usage."
  exit 1
fi

# Refactor (v1/issue1468-first):
#   Old pattern: query/read files could grow hidden event replay, state rebuild, or
#   projection materialization calls in request paths.
#   New principle: QueryPort/QueryService/ApplicationService files only read
#   materialized read models; owner-change bootstrap is a v1 non-goal.
check_no_query_time_replay() {
  local query_read_files=()
  while IFS= read -r -d '' query_read_file; do
    query_read_files+=("${query_read_file}")
  done < <(
    find src agents \
      -type f \
      \( -name '*QueryPort.cs' -o -name '*QueryService.cs' -o -name '*ApplicationService.cs' \) \
      -not -path '*/bin/*' \
      -not -path '*/obj/*' \
      -not -name '*.g.cs' \
      -not -name '*.Designer.cs' \
      -print0
  )

  if (( ${#query_read_files[@]} == 0 )); then
    return
  fi

  set +e
  local query_time_replay_report
  query_time_replay_report="$(
    rg -n "\b(ReadEventsAsync|GetEventsAsync|RebuildAsync|MaterializeAsync)\b" "${query_read_files[@]}" \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;

  print $0;
}'
  )"
  local query_time_replay_status=$?
  set -e

  if [[ ${query_time_replay_status} -ne 0 && ${query_time_replay_status} -ne 1 ]]; then
    echo "Query-time replay guard execution failed."
    exit "${query_time_replay_status}"
  fi

  if [ -n "${query_time_replay_report}" ]; then
    echo "${query_time_replay_report}"
    echo "Query/read paths must not replay events, rebuild state, or materialize projections in request paths. Use committed projection/readmodel materialization outside the query call stack."
    exit 1
  fi
}

check_no_query_time_replay

# Refactor (iter56/cluster-920-workflow-catalog-async-query):
#   Old pattern: production workflow query ports could hide async readmodel I/O behind sync .Result/.Wait calls.
#   New principle: catalog/capabilities query seams are async end-to-end, and query ports await readmodel readers.
workflow_query_port_files=()
while IFS= read -r query_port_file; do
  workflow_query_port_files+=("${query_port_file}")
done < <(
  find src/workflow \
    -type f \
    \( -name '*QueryPort.cs' -o -path '*/Queries/*.cs' -o -path '*/Workflows/*ReadModelQueryPort.cs' \) \
    -not -path '*/bin/*' \
    -not -path '*/obj/*' \
    -not -name '*.g.cs' \
    -not -name '*.Designer.cs' \
    | sort
)

if (( ${#workflow_query_port_files[@]} > 0 )); then
  set +e
  workflow_query_port_sync_blocking_report="$(
    rg -n "\.Result|\.Wait[[:space:]]*\(|GetAwaiter\(\)\.GetResult\(\)" "${workflow_query_port_files[@]}" \
      | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;

  print $0;
}'
  )"
  workflow_query_port_sync_blocking_status=$?
  set -e

  if [[ ${workflow_query_port_sync_blocking_status} -ne 0 && ${workflow_query_port_sync_blocking_status} -ne 1 ]]; then
    echo "Workflow query-port sync-blocking guard execution failed."
    exit "${workflow_query_port_sync_blocking_status}"
  fi

  if [ -n "${workflow_query_port_sync_blocking_report}" ]; then
    echo "${workflow_query_port_sync_blocking_report}"
    echo "Workflow production query ports must not sync-block async reads. Use async query seams and await readmodel readers."
    exit 1
  fi
fi

# Refactor (iter18/cluster-001):
#   Old pattern: ILLMProvider 仍暴露 ChatAsync 非流式入口,provider/failover 可绕过流式链路
#   New principle: Provider contract 只暴露 ChatStreamAsync;非流式聚合用现有 ChatStreamContentAggregator;无新 offline adapter
if rg -n "Task<LLMResponse>[[:space:]]+ChatAsync[[:space:]]*\(" \
  src/Aevatar.AI.Abstractions/LLMProviders/ILLMProvider.cs \
  src/Aevatar.AI.Core/LLMProviders \
  src/Aevatar.AI.LLMProviders.* \
  -g '*.cs'
then
  echo "Provider-level Task<LLMResponse> ChatAsync is forbidden. Use ChatStreamAsync plus ChatStreamContentAggregator for offline aggregation."
  exit 1
fi

if rg -n "CommandContext\.Metadata|AgentRunContext\.Metadata|LLMCallContext\.Metadata|ToolCallContext\.Metadata|GAgentExecutionHookContext\.Metadata" \
  src test
then
  echo "Legacy internal metadata bags are forbidden in core contexts. Use Headers / Items / typed fields where semantics are explicit."
  exit 1
fi

if rg -n "EventEnvelope\.Metadata|StepCompletedEvent\.Metadata|CompletionMetadata|WorkflowRunCommandMetadataKeys\.SessionId|EventEnvelope\.CorrelationId" \
  docs src/Aevatar.Foundation.Core/README.md \
  -g '!docs/architecture/archive/**' \
  -g '!docs/architecture/*blueprint*.md'
then
  echo "Legacy documentation terminology is forbidden. Use typed envelope fields, Annotations, and current session sourcing."
  exit 1
fi

set +e
workflow_trusted_control_metadata_report="$(
  rg -n "BuildLegacyMetadata|WorkflowChatRunRequest\.Metadata\[[^]]*(scope|nyx|model|tool|control|owner|response)|Metadata:\s*.*(LLMRequestMetadataKeys|scope_id)" \
    src/Aevatar.AI.ToolProviders.AevatarInvocation \
    src/workflow \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;

  print $0;
}'
)"
workflow_trusted_control_metadata_status=$?
set -e

if [[ ${workflow_trusted_control_metadata_status} -ne 0 && ${workflow_trusted_control_metadata_status} -ne 1 ]]; then
  echo "Workflow trusted-control metadata guard execution failed."
  exit "${workflow_trusted_control_metadata_status}"
fi

if [ -n "${workflow_trusted_control_metadata_report}" ]; then
  echo "${workflow_trusted_control_metadata_report}"
  echo "Workflow/team trusted control must use typed ScopeId / ToolContext / LlmControl fields, not Metadata bag entries."
  exit 1
fi

if rg -n "IProjectionReadModelBindingResolver|ProjectionReadModelBindingResolver|ProjectionReadModelBindingException" src test; then
  echo "BindingResolver-based projection routing is forbidden. Use capability-based Document/Graph routing."
  exit 1
fi

# Refactor (iter56/cluster-933-channel-registration-rebuild-narrow):
#   Old: channel registration projection rebuild was exposed through HTTP, tool,
#   prompts, docs, facade, and relay-local DI.
#   New: rebuild is only the internal Runtime startup refresh path.
set +e
channel_registration_public_rebuild_report="$(
  rg -n "rebuild_projection|/registrations/rebuild|RebuildProjectionAsync|ChannelBotRebuildProjectionCommand" \
    src/Aevatar.AI.ToolProviders.ChannelAdmin \
    agents/channels/Aevatar.GAgents.Channel.NyxIdRelay \
    agents/Aevatar.GAgents.NyxidChat \
    docs/operations \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (text ~ /Refactor \(iter56\/cluster-933-channel-registration-rebuild-narrow\)/)
    next;

  print $0;
}'
)"
channel_registration_public_rebuild_status=$?
set -e

if [[ ${channel_registration_public_rebuild_status} -ne 0 && ${channel_registration_public_rebuild_status} -ne 1 ]]; then
  echo "Channel registration public rebuild guard execution failed."
  exit "${channel_registration_public_rebuild_status}"
fi

if [ -n "${channel_registration_public_rebuild_report}" ]; then
  echo "${channel_registration_public_rebuild_report}"
  echo "Channel registration projection rebuild must not be exposed through public/tool/prompt/docs/facade/relay DI surfaces."
  exit 1
fi

if rg -n "IGAgentActorStore|ActorBackedGAgentActorStore" src agents; then
  echo "Legacy GAgent actor store is forbidden. Use registry command/query/admission ports."
  exit 1
fi

# Final SkillRunner cleanup (#2733): scheduled workflow/team execution must stay on
# ScheduledDispatch + workflow/team service invocation. Do not reintroduce the legacy
# SkillRunnerGAgent runtime, ports, proto, readmodel, tests, or schedule-kind branch.
skill_runner_reintro_report="$(
  rg -n "SkillRunnerGAgent|ISkillRunner(CommandPort|CronSchedulePort|ExecutionQueryPort)|SkillRunner(CommandPort|CronSchedulePort|ExecutionDocument|ExecutionProjector|ExecutionQueryPort|State|LegacyAliases|OutboundDeliveryPort|StreamingReplySink|OutputChunker|ToolFailureCounter|InteractiveDeliveryTrackingMiddleware)|InitializeSkillRunnerCommand|TriggerSkillRunnerExecutionCommand|AdmitSkillRunnerExternalTriggerCommand|ScheduledDispatchScheduleKind\\.SkillRunner|ScheduledDispatchScheduleKindState\\.SkillRunner" \
    agents src test \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"
if [ -n "${skill_runner_reintro_report}" ]; then
  echo "${skill_runner_reintro_report}"
  echo "Legacy SkillRunner scheduled runtime/model is forbidden; use ScheduledDispatch + workflow/team service invocation."
  exit 1
fi

# Refactor (iter92/cluster-645):
#   Old pattern: no guard blocked new production consumers from depending on StreamingProxy.
#   New principle: this guard prevents new consumers, wrappers, or re-maps; direct model streaming goes through /v1/responses.
set +e
streaming_proxy_consumer_report="$(
  rg -n "streaming-proxy|MapStreamingProxyEndpoints|AddStreamingProxy|Aevatar\.GAgents\.StreamingProxy" \
    src agents apps \
    -g '*.{cs,ts,tsx,js,jsx,md,json,yml,yaml,sh}' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!**/wwwroot/**' \
    | awk -F: '
BEGIN {
  allowed["src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"] = 1;
  allowed["src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs"] = 1;
  allowed["tools/ci/architecture_guards.sh"] = 1;
  allowed["tools/ci/README.md"] = 1;
}

{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (file in allowed)
    next;
  if (file ~ /^agents\/Aevatar\.GAgents\.StreamingProxy\//)
    next;
  if (file ~ /^agents\/Aevatar\.GAgents\.StreamingProxy\./)
    next;
  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;
  print $0;
}'
)"
streaming_proxy_consumer_status=$?
set -e

if [[ ${streaming_proxy_consumer_status} -ne 0 && ${streaming_proxy_consumer_status} -ne 1 ]]; then
  echo "StreamingProxy consumer guard execution failed."
  exit "${streaming_proxy_consumer_status}"
fi

if [ -n "${streaming_proxy_consumer_report}" ]; then
  echo "${streaming_proxy_consumer_report}"
  echo "StreamingProxy is deprecated compatibility surface only. Do not add new production consumers; use /v1/responses for direct model streaming."
  exit 1
fi

# Issue #643:
#   Old: Foundation MultiAgent experimental actors/protos stayed visible as
#   production GAgent surface without production callers. Studio empty-state
#   generators previously appeared as endpoint GAgents.
#   New: MultiAgent is retired; Studio generation remains Application-layer
#   authoring preview helpers unless a future ADR reopens the actor model.
if rg -n "Aevatar\.Foundation\.(Core|Abstractions)\.MultiAgent|MultiAgent/multi_agent_(state|messages)\.proto|package[[:space:]]+aevatar\.multiagent|TaskBoardGAgent|TeamManagerGAgent" \
  src agents \
  -g '!**/bin/**' \
  -g '!**/obj/**' \
  -g '!*.g.cs' \
  -g '!*.Designer.cs'
then
  echo "Retired Foundation MultiAgent actor/proto surface is forbidden without a new ADR reopening the model."
  exit 1
fi

if [ -d "src/Aevatar.Studio.Hosting/Endpoints" ] && rg -n "ScriptGenerateGAgent|WorkflowGenerateGAgent|AIGAgentBase[[:space:]]*<[[:space:]]*Empty[[:space:]]*>" \
  src/Aevatar.Studio.Hosting/Endpoints \
  -g '*.cs' \
  -g '!**/bin/**' \
  -g '!**/obj/**' \
  -g '!*.g.cs' \
  -g '!*.Designer.cs'
then
  echo "Studio empty-state generation must stay demoted to Application authoring preview helpers, not endpoint GAgents."
  exit 1
fi

# Refactor (iter7/cluster-016):
#   Old: Direct actor.HandleEventAsync calls and raw SubscribeAsync<EventEnvelope>
#   subscriptions were guarded only by capability-local source assertions, so new
#   Host/Application endpoints could reintroduce the same dispatch/projection
#   boundary bypasses outside those tests.
#   New: Keep the runtime-owned transport internals as a short exact allowlist;
#   every other production path must use dispatch/projection ports instead of
#   directly invoking actor handlers or subscribing to raw event-envelope streams.
set +e
dispatch_projection_boundary_report="$(
  rg -n "actor\.HandleEventAsync|\.HandleEventAsync\(|SubscribeAsync<EventEnvelope>" \
    src agents tools \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!**/wwwroot/**' \
    -g '!*.g.cs' \
    -g '!*.Designer.cs' \
    | awk -F: '
BEGIN {
  allowed["src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs"] = 1;
  allowed["src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs"] = 1;
}

{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  if (file in allowed)
    next;
  if (file ~ /(^|\/)test\// || file ~ /Tests\.cs$/ || file ~ /(^|\/)[^\/]*\.Tests\//)
    next;
  if (file ~ /\.g\.cs$/ || file ~ /\.Designer\.cs$/)
    next;
  if (text ~ /^[[:space:]]*\/\/\/?/)
    next;
  if (text ~ /^[[:space:]]*Task[[:space:]]+HandleEventAsync[[:space:]]*\(/)
    next;

  print $0;
}'
)"
dispatch_projection_boundary_status=$?
set -e

if [[ ${dispatch_projection_boundary_status} -ne 0 && ${dispatch_projection_boundary_status} -ne 1 ]]; then
  echo "Dispatch/projection source-regression guard execution failed."
  exit "${dispatch_projection_boundary_status}"
fi

if [ -n "${dispatch_projection_boundary_report}" ]; then
  echo "${dispatch_projection_boundary_report}"
  echo "Direct actor HandleEventAsync dispatch and raw SubscribeAsync<EventEnvelope> subscriptions are forbidden outside runtime transport internals."
  exit 1
fi

# Refactor (phase9/issue-1132):
#   Old: a handled-dispatch side contract could make accepted-only dispatch
#   receipts look stronger than the runtime-neutral command admission contract.
#   New: production code uses IActorDispatchPort only; handled-dispatch shims and
#   typed dispatchers are retired.
handled_dispatch_pattern="IActor""HandledDispatchPort|DispatchAndWait""HandledAsync|HandledActor""CommandTargetDispatcher|LocalActor""HandledDispatchPort|OrleansActor""HandledDispatchPort"
if rg -n "${handled_dispatch_pattern}" \
  src agents tools \
  -g '*.cs' \
  -g '*.sh' \
  -g '!**/bin/**' \
  -g '!**/obj/**' \
  -g '!**/wwwroot/**' \
  -g '!*.g.cs' \
  -g '!*.Designer.cs'
then
  echo "Handled actor dispatch side contracts are forbidden in production paths; use IActorDispatchPort accepted-only dispatch."
  exit 1
fi

# Refactor (iter95/cluster-095-dispatch-port-runtime-ack-drift):
#   Old: Local IActorDispatchPort awaited actor.HandleEventAsync while Orleans
#   only awaited transport admission, so DispatchAsync ACK semantics drifted by
#   runtime.
#   New: DispatchAsync returns DispatchAdmission only. It may await runtime/inbox
#   admission, but must not synchronously wait for actor handler completion.
dispatch_admission_handler_wait_report=""
while IFS= read -r dispatch_port_file; do
  [ -z "${dispatch_port_file}" ] && continue

  hits="$(rg -n "\.HandleEventAsync[[:space:]]*\(" "${dispatch_port_file}" || true)"

  if [ -n "${hits}" ]; then
    dispatch_admission_handler_wait_report="${dispatch_admission_handler_wait_report}${hits}"$'\n'
  fi
done < <(
  find src agents \
    -type f \
    -name '*ActorDispatchPort.cs' \
    -not -path '*/bin/*' \
    -not -path '*/obj/*' \
    -not -name '*.g.cs' \
    -not -name '*.Designer.cs' \
    | sort
)

if [ -n "${dispatch_admission_handler_wait_report}" ]; then
  echo "${dispatch_admission_handler_wait_report}"
  echo "Actor dispatch ports must return DispatchAdmission after runtime/inbox admission; do not call or await actor HandleEventAsync in DispatchAsync."
  exit 1
fi

# Refactor (iter8/cluster-018):
#   Old: Web/API examples and SDK/test default-port values could reintroduce
#   forbidden local API port tokens without any CI enforcement.
#   New: Scan only URL/defaultPort-shaped port semantics for the forbidden
#   Web/API ports, while leaving generic numeric values such as timeouts,
#   page sizes, and histogram buckets outside the match surface.
forbidden_web_api_port_scan_roots=()
for scan_root in README*.md LOCAL_DEV_SETUP.md src test tools docs demos apps; do
  if [ -e "${scan_root}" ]; then
    forbidden_web_api_port_scan_roots+=("${scan_root}")
  fi
done

set +e
forbidden_web_api_port_report="$(
  rg -n "localhost:50(00|50)|127\.0\.0\.1:50(00|50)|defaultPort:\s*50(00|50)|defaultPort\s*=\s*50(00|50)" \
    "${forbidden_web_api_port_scan_roots[@]}" \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    -g '!**/node_modules/**' \
    -g '!docs/audit-scorecard/**' \
    -g '!docs/history/**' \
    -g '!tools/ci/README.md' \
    | awk -F: '
{
  file = $1;
  line_no = $2;
  text = substr($0, length(file) + length(line_no) + 3);

  # Allow self-references inside this guard script (the regex literal).
  if (file == "tools/ci/architecture_guards.sh" && text ~ /^[[:space:]]*#/)
    next;

  print $0;
}'
)"
forbidden_web_api_port_status=$?
set -e

if [[ ${forbidden_web_api_port_status} -ne 0 && ${forbidden_web_api_port_status} -ne 1 ]]; then
  echo "Forbidden Web/API port guard execution failed."
  exit "${forbidden_web_api_port_status}"
fi

if [ -n "${forbidden_web_api_port_report}" ]; then
  echo "${forbidden_web_api_port_report}"
  echo "Forbidden Web/API port tokens found. Use repo-approved local API defaults such as 5100 instead of 5000 or 5050."
  exit 1
fi

# Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
#   Old: workflow-run current-state endpoints presented actor-scoped
#   readmodels as run-scoped resources.
#   New: actor-scoped current-state readmodel endpoints must be honestly named
#   and protected by explicit .RequireAuthorization() or security-allowlist.
workflow_actor_current_state_endpoint_files=()
while IFS= read -r endpoint_file; do
  workflow_actor_current_state_endpoint_files+=("${endpoint_file}")
done < <(
  find src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi \
    -type f \
    -name '*Endpoint*.cs' \
    | sort
)

if [ "${#workflow_actor_current_state_endpoint_files[@]}" -gt 0 ]; then
  set +e
  workflow_actor_current_state_authz_report="$(
    awk '
function trim(value) {
  sub(/^[[:space:]]+/, "", value);
  sub(/[[:space:]]+$/, "", value);
  return value;
}

function record_if_unprotected() {
  if (in_endpoint == 0)
    return;
  if (requires_authorization == 0 && has_allowlist == 0)
    print endpoint_file ":" endpoint_line ":" endpoint_text;
}

BEGIN {
  in_endpoint = 0;
  has_actor_current_state_route = 0;
}

FNR == 1 {
  record_if_unprotected();
  in_endpoint = 0;
}

{
  line = $0;

  if (line ~ /MapGet[[:space:]]*\([[:space:]]*"\/(api\/)?workflow-runs\/\{[^}]+}\/current-state"/) {
    print FILENAME ":" FNR ":" trim(line);
    print "Workflow current-state endpoints must not be exposed under workflow-runs until a real run-id readmodel exists.";
    exit 2;
  }

  if (line ~ /MapGet[[:space:]]*\([[:space:]]*"\/(api\/)?agents"/ ||
      line ~ /MapGet[[:space:]]*\([[:space:]]*"\/(api\/)?workflow-actors\/\{[^}]+}\/current-state"/) {
    if (line ~ /MapGet[[:space:]]*\([[:space:]]*"\/(api\/)?workflow-actors\/\{actorId}\/current-state"/)
      has_actor_current_state_route = 1;
    record_if_unprotected();
    in_endpoint = 1;
    endpoint_file = FILENAME;
    endpoint_line = FNR;
    endpoint_text = trim(line);
    requires_authorization = line ~ /\.RequireAuthorization[[:space:]]*\(/;
    has_allowlist = line ~ /security-allowlist:/;
    next;
  }

  if (in_endpoint == 1) {
    if (line ~ /\.RequireAuthorization[[:space:]]*\(/)
      requires_authorization = 1;
    if (line ~ /security-allowlist:/)
      has_allowlist = 1;
    if (line ~ /;[[:space:]]*$/) {
      record_if_unprotected();
      in_endpoint = 0;
    }
  }
}

END {
  record_if_unprotected();
  if (has_actor_current_state_route == 0) {
    print "Workflow actor current-state route must be exposed as /workflow-actors/{ActorId}/current-state.";
    exit 2;
  }
}
' "${workflow_actor_current_state_endpoint_files[@]}"
  )"
  workflow_actor_current_state_authz_status=$?
  set -e

  if [[ ${workflow_actor_current_state_authz_status} -ne 0 ]]; then
    echo "Workflow actor current-state authorization guard execution failed."
    exit "${workflow_actor_current_state_authz_status}"
  fi

  if [ -n "${workflow_actor_current_state_authz_report}" ]; then
    echo "${workflow_actor_current_state_authz_report}"
    echo "Workflow actor current-state endpoints must call .RequireAuthorization() or carry a per-endpoint security-allowlist comment."
    exit 1
  fi
fi

bash "${SCRIPT_DIR}/query_projection_priming_guard.sh"
bash "${SCRIPT_DIR}/nyxid_chat_semantics_guard.sh"
bash "${SCRIPT_DIR}/workflow_call_context_guard.sh"
bash "${SCRIPT_DIR}/command_observation_attach_only_guard.sh"
bash "${SCRIPT_DIR}/projection_attach_existing_side_read_guard.sh"
bash "${SCRIPT_DIR}/public_projection_ensure_ports_guard.sh"
bash "${SCRIPT_DIR}/scripting_write_path_cqrs_guard.sh"
bash "${SCRIPT_DIR}/projection_state_version_guard.sh"
bash "${SCRIPT_DIR}/projection_state_mirror_current_state_guard.sh"
bash "${SCRIPT_DIR}/agent_kind_naming_guard.sh"
bash "${SCRIPT_DIR}/gagent_registry_kind_guard.sh"
bash "${SCRIPT_DIR}/proto_lint_guard.sh"
bash "${SCRIPT_DIR}/channel_mega_interface_guard.sh"
bash "${SCRIPT_DIR}/channel_native_sdk_import_guard.sh"
bash "${SCRIPT_DIR}/channel_platform_project_reference_guard.sh"
echo "Running catch exception observability guard..."
bash "${SCRIPT_DIR}/catch_exception_observability_guard.sh"
python3 "${REPO_ROOT}/tools/ci/guards/project_reference_layer_guard.py" \
  --root "${REPO_ROOT}" \
  --allowlist "${REPO_ROOT}/tools/ci/project_reference_layer_allowlist.tsv" \
  --mode fail
bash "${SCRIPT_DIR}/channel_inbox_gagent_guard.sh"
bash "${SCRIPT_DIR}/channel_relay_nyx_chat_direct_create_guard.sh"
bash "${SCRIPT_DIR}/channel_tombstone_proto_field_guard.sh"
bash "${SCRIPT_DIR}/agent_tool_delivery_target_reader_guard.sh"
bash "${SCRIPT_DIR}/studio_projection_readmodel_registration_guard.sh"
bash "${SCRIPT_DIR}/studio_fact_owner_guard.sh"
bash "${SCRIPT_DIR}/studio_catalog_storage_serializer_guard.sh"
bash "${SCRIPT_DIR}/frontend_static_boundary_guard.sh"
bash "${SCRIPT_DIR}/workflow_observatory_readonly_guard.sh"
bash "${SCRIPT_DIR}/backend_console_static_asset_guard.sh"

studio_catalog_query_ports=(
  "src/Aevatar.Studio.Application/Studio/Abstractions/IConnectorCatalogQueryPort.cs"
  "src/Aevatar.Studio.Application/Studio/Abstractions/IRoleCatalogQueryPort.cs"
)

for query_port in "${studio_catalog_query_ports[@]}"; do
  if [ ! -f "${query_port}" ]; then
    echo "Studio catalog query port is missing: ${query_port}"
    exit 1
  fi
done

if rg -n "Task(<[^>]+>)?[[:space:]]+(Import|Save|Delete|Create|Update|Ensure|Dispatch|Send)[A-Za-z0-9_]*Async[[:space:]]*\(" \
  "${studio_catalog_query_ports[@]}"
then
  echo "Studio catalog query ports must remain read-only. Move mutating methods to catalog command ports."
  exit 1
fi

secret_store_scan_roots=()
while IFS= read -r host_dir; do
  secret_store_scan_roots+=("${host_dir}")
done < <(find src -maxdepth 1 -type d -name 'Aevatar.*Host*' | sort)
while IFS= read -r service_extension; do
  secret_store_scan_roots+=("${service_extension}")
done < <(find agents -type f -name 'ServiceCollectionExtensions.cs' | sort)

if [ "${#secret_store_scan_roots[@]}" -gt 0 ]; then
  secret_store_di_hits="$(
    rg -n "(AddSingleton|TryAddSingleton)<IAevatarSecretsStore" \
      "${secret_store_scan_roots[@]}" \
      -g '*.cs' || true
  )"
  if [ -n "${secret_store_di_hits}" ]; then
    echo "${secret_store_di_hits}"
    echo "Service hosts and agent ServiceCollectionExtensions must not default-register IAevatarSecretsStore."
    exit 1
  fi
fi

if rg -n "ExecuteDeclaredQueryAsync|ExecuteReadModelQueryAsync" src; then
  echo "Declared readmodel query execution is forbidden. Query must read persisted snapshots/documents only."
  exit 1
fi

if rg -n "OnQuery<|ScriptQuerySemanticsSpec|QueryTypeUrls|QueryResultTypeUrls|ExecuteQueryAsync\(|GetReadModelSnapshotAsync\(" src/Aevatar.Scripting.*; then
  echo "Scripting declared-query authoring/runtime contracts and runtime readmodel side-reads are forbidden on the production path."
  exit 1
fi

if rg -n "ProjectReadModel\(" src test/Aevatar.Scripting.Core.Tests test/Aevatar.Integration.Tests; then
  echo "Legacy event-driven scripting readmodel projection is forbidden. Use ProjectState(...) and BuildReadModel(...)."
  exit 1
fi

if rg -n "project:\s*static|project:\s*\(" test/Aevatar.Scripting.Core.Tests test/Aevatar.Integration.Tests; then
  echo "Legacy OnEvent(..., project: ...) scripting authoring is forbidden. Register ProjectState(...) explicitly."
  exit 1
fi

if rg -n "IProjectionEventReducer|AddAIDefaultProjectionLayer|AddAllAIProjectionEventReducers|EnableWorkflowAIProjection" src; then
  echo "Reducer-era projection abstractions and workflow AI projection toggles are forbidden on the production path."
  exit 1
fi

if rg -n "WorkflowExecutionReadModelProjector|IWorkflowProjectionReadModelUpdater|WorkflowProjectionReadModelUpdater|WorkflowExecutionReportDocumentMetadataProvider|AddWorkflowExecutionProjectionReducer|AddWorkflowExecutionProjectionProjector|AddWorkflowExecutionProjectionExtensionsFromAssembly|WorkflowExecutionReportSnapshotMapper|WorkflowExecutionEventReducerBase|WorkflowExecutionProjectionMutations" src/workflow test/Aevatar.Workflow.Host.Api.Tests; then
  echo "Legacy workflow readmodel naming is forbidden. Use artifact-oriented workflow projection names."
  exit 1
fi

if rg -n "IWorkflowExecutionReportArtifactSink|NoopWorkflowExecutionReportArtifactSink|FileSystemWorkflowExecutionReportArtifactSink|WorkflowExecutionReportArtifactOptions|WorkflowExecutionReportArtifacts" src test docs -g '!docs/architecture/archive/**'; then
  echo "Legacy workflow report artifact export naming is forbidden. Use WorkflowRunReportExport terminology."
  exit 1
fi

if rg -n "Projection:ReadModel:Bindings" src test; then
  echo "Projection:ReadModel:Bindings is forbidden. Use Projection:Document:* and Projection:Graph:* options."
  exit 1
fi

set +e
# Check only ListAsync() calls on variables declared as IProjectionDocumentReader.
# Business query ports may expose ListAsync and must not be inferred from file or path names.
projection_document_reader_scan_roots=(src test)
if [[ -d demos ]]; then
  projection_document_reader_scan_roots+=(demos)
fi
projection_document_reader_list_report="$(
  scan_projection_document_reader_list_async "${projection_document_reader_scan_roots[@]}"
)"
projection_document_reader_list_status=$?
set -e

if [[ ${projection_document_reader_list_status} -eq 0 && -n "${projection_document_reader_list_report}" ]]; then
  echo "${projection_document_reader_list_report}"
  echo "IProjectionDocumentReader-based document querying must use QueryAsync. Legacy ListAsync is forbidden."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/DefaultAutoPersistedStateEventFactory.cs" ]; then
  echo "DefaultAutoPersistedStateEventFactory is forbidden. EventStore must persist domain events, not snapshot-state events."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/IAutoPersistedStateEventFactory.cs" ]; then
  echo "IAutoPersistedStateEventFactory is forbidden. Use IDomainEventDeriver<TState> for semantic event derivation."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/EventSourcingAutoPersistenceOptions.cs" ]; then
  echo "EventSourcingAutoPersistenceOptions is forbidden. Stateful actors must emit explicit domain events."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/IDomainEventDeriver.cs" ]; then
  echo "IDomainEventDeriver is forbidden on runtime path. Domain events must be produced explicitly by command handlers."
  exit 1
fi

if rg -n "ConfirmStateAsync\(" src/Aevatar.Foundation.Core/EventSourcing; then
  echo "ConfirmStateAsync is forbidden."
  exit 1
fi

if rg -n "ConfirmDerivedEventsAsync\(" src/Aevatar.Foundation.Core/EventSourcing; then
  echo "ConfirmDerivedEventsAsync is forbidden. Domain events must be raised explicitly via RaiseEvent + ConfirmEventsAsync."
  exit 1
fi

if rg -n "TryUnpack<TState>|Unpack<TState>" src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs; then
  echo "EventSourcingBehavior must not unpack TState snapshots from persisted events."
  exit 1
fi

if rg -n "StateStore\.LoadAsync|StateStore\.SaveAsync" src/Aevatar.Foundation.Core/GAgentBase.TState.cs; then
  echo "GAgentBase<TState> must not use StateStore as the fact source. Recovery must come from EventStore replay."
  exit 1
fi

if rg -n "public\s+IStateStore<" src/Aevatar.Foundation.Core/GAgentBase.TState.cs; then
  echo "GAgentBase<TState> must not expose IStateStore property. Recovery/writes must go through EventStore semantics."
  exit 1
fi

if rg -n "GetService\(typeof\(IEventStore\)\)|GetService<EventSourcingRuntimeOptions>|GetService<IEventSourcingSnapshotStore<|GetService<IEventSourcingBehaviorFactory<|new\s+AgentBackedEventSourcingBehavior|EventSourcingBehaviorFactory\s*\?\?=" \
  src/Aevatar.Foundation.Core/GAgentBase.TState.cs
then
  echo "GAgentBase<TState> must not compose EventSourcing behavior via Service Locator internals. Use IEventSourcingBehaviorFactory<TState>."
  exit 1
fi

set +e
state_direct_mutation_report="$(
  rg --files -0 src -g '*.cs' -g '!*.g.cs' \
    | xargs -0 awk '
function trim(value)
{
  gsub(/^[[:space:]]+/, "", value);
  gsub(/[[:space:]]+$/, "", value);
  return value;
}

function normalize_base(value)
{
  value = trim(value);
  sub(/<.*/, "", value);
  sub(/^.*\./, "", value);
  gsub(/[[:space:]]+/, "", value);
  return value;
}

function register_base(class_name, base_clause, parts, first_base)
{
  if (class_name == "")
    return;

  split(base_clause, parts, ",");
  first_base = normalize_base(parts[1]);
  if (first_base != "")
    class_base[class_name] = first_base;

  pending_class[FILENAME] = "";
}

{
  line = $0;

  if (pending_class[FILENAME] != "")
  {
    if (line ~ /^[[:space:]]*:/)
    {
      base_clause = line;
      sub(/^[[:space:]]*:[[:space:]]*/, "", base_clause);
      sub(/\{.*/, "", base_clause);
      register_base(pending_class[FILENAME], base_clause);
    }
    else if (line ~ /\{/)
    {
      pending_class[FILENAME] = "";
    }
  }

  if (match(line, /[[:space:]]class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*/))
  {
    class_decl = substr(line, RSTART, RLENGTH);
    sub(/^.*class[[:space:]]+/, "", class_decl);
    class_name = class_decl;
    file_class[FILENAME SUBSEP class_name] = 1;

    tail = substr(line, RSTART + RLENGTH);
    if (tail ~ /:/)
    {
      base_clause = tail;
      sub(/^.*:[[:space:]]*/, "", base_clause);
      sub(/\{.*/, "", base_clause);
      register_base(class_name, base_clause);
    }
    else if (tail !~ /\{/)
    {
      pending_class[FILENAME] = class_name;
    }
    else
    {
      pending_class[FILENAME] = "";
    }
  }

  if (line ~ /^[[:space:]]*\/\//)
    next;

  # match State.Foo = / += / -= / *= / /= / %= / ++ / -- (real mutations)
  # exclude State.Foo == (comparison) — `=` must NOT be followed by another `=`
  if (line ~ /(^|[[:space:](;])(this\.)?State\.[A-Za-z_][A-Za-z0-9_]*[[:space:]]*(\+\+|--|[+*%\/-]?=[^=])/)
  {
    state_mutation[FILENAME] = state_mutation[FILENAME] sprintf("%s:%d:%s\n", FILENAME, FNR, line);
  }
}

END {
  stateful["GAgentBase"] = 1;
  changed = 1;
  while (changed)
  {
    changed = 0;
    for (class_name in class_base)
    {
      base_name = class_base[class_name];
      if ((base_name in stateful) && !(class_name in stateful))
      {
        stateful[class_name] = 1;
        changed = 1;
      }
    }
  }

  violations = "";
  for (file in state_mutation)
  {
    is_stateful_file = 0;
    for (key in file_class)
    {
      split(key, tokens, SUBSEP);
      if (tokens[1] != file)
        continue;

      declared_class = tokens[2];
      if (declared_class in stateful)
      {
        is_stateful_file = 1;
        break;
      }
    }

    if (is_stateful_file)
      violations = violations "\n" file "\n" state_mutation[file];
  }

  if (violations != "")
  {
    printf "%s", violations;
    printf "Stateful GAgent implementations must not mutate State directly. Emit domain events and apply state in TransitionState/appliers.\n";
    exit 1;
  }
}
'
)"
state_direct_mutation_status=$?
set -e

if [ "${state_direct_mutation_status}" -ne 0 ] && [ -z "${state_direct_mutation_report}" ]; then
  echo "State direct mutation guard execution failed."
  exit "${state_direct_mutation_status}"
fi

if [ -n "${state_direct_mutation_report}" ]; then
  echo "${state_direct_mutation_report}"
  exit 1
fi

if rg -n "IEventSourcingBehavior<>\\)\\.MakeGenericType|EventSourcingBehavior<>\\)\\.MakeGenericType|GetProperty\\(\"EventSourcing\"\\)|GetProperty\\(\"StateStore\"\\)" \
  src/Aevatar.Foundation.Runtime \
  src/Aevatar.Foundation.Runtime.Implementations.Orleans
then
  echo "Runtime must not use reflection-based stateful ES binding. Use static generic construction in GAgentBase<TState>."
  exit 1
fi

type_url_scan_roots=(src)
if [[ -d demos ]]; then
  type_url_scan_roots+=(demos)
fi
if rg -n "TypeUrl\.Contains|typeUrl\.Contains\(" "${type_url_scan_roots[@]}"; then
  echo "Found string-based event type matching."
  exit 1
fi

actor_id_parsing_hits="$(
  rg -n "[A-Za-z0-9_]*ActorId[A-Za-z0-9_]*\s*\.(StartsWith|EndsWith|Contains|Split|Substring)\(" \
    src/Aevatar.Mainnet.Host.Api \
    src/workflow/Aevatar.Workflow.Host.Api \
    src/workflow/Aevatar.Workflow.Application \
    src/Aevatar.Scripting.Application \
    -g '*.cs' || true
)"

if [ -n "${actor_id_parsing_hits}" ]; then
  echo "${actor_id_parsing_hits}"
  echo "Host/Application must not parse actorId strings for source/type branching. Use actor-owned binding queries or typed target resolvers."
  exit 1
fi

source_named_runtime_port_hits="$(
  rg -n "\b(interface|class|record)\s+I?(Workflow|Script|Scripting|Static)[A-Za-z0-9_]*(GAgentRuntimePort|ActorRuntimePort|ActorCommunicationPort|ActorInvocationPort|ActorDispatchPort)\b" \
    src test \
    -g '*.cs' || true
)"

if [ -n "${source_named_runtime_port_hits}" ]; then
  echo "${source_named_runtime_port_hits}"
  echo "Source-named generic actor communication abstractions are forbidden. Keep lifecycle on IActorRuntime and use subsystem-local typed/contextual adapters."
  exit 1
fi

legacy_runtime_port_hits="$(
  rg -n "\b(interface|class|record)\s+(IGAgentRuntimePort|RuntimeGAgentRuntimePort)\b" \
    src test \
    -g '*.cs' || true
)"

if [ -n "${legacy_runtime_port_hits}" ]; then
  echo "${legacy_runtime_port_hits}"
  echo "Legacy fat runtime-port abstractions are forbidden. Foundation must keep lifecycle/topology on IActorRuntime and message execution on context/publisher."
  exit 1
fi

if rg -n "IActorMessagingPort|IActorMessagingSession|IActorMessagingSessionFactory|RuntimeActorMessagingPort|RuntimeActorMessagingSessionFactory" \
  src test docs/canon/architecture.md docs/canon/scripting.md src/workflow/README.md \
  -g '!docs/architecture/*'
then
  echo "Public actor messaging port/session abstractions are forbidden. Use IActorRuntime + IActorDispatchPort + IEventContext/IEventPublisher or subsystem-local typed adapters."
  exit 1
fi

if rg -n "\bgagent_query\b|GAgentQueryState|GAgentQueryResultState|GAgentDispatchState|\bgagent_send\b" \
  src test docs/canon/architecture.md docs/canon/scripting.md src/workflow/README.md \
  -g '!docs/architecture/*'
then
  echo "Legacy gagent_* generic communication modules/states are forbidden. Use actor_send plus protocol-specific typed query/reply paths."
  exit 1
fi

if rg -n "Dictionary<|ConcurrentDictionary<|HashSet<|Queue<" src/workflow/Aevatar.Workflow.Core/Modules/WorkflowCallModule.cs; then
  echo "WorkflowCallModule must stay stateless; workflow_call fact state must live in WorkflowGAgent persisted state."
  exit 1
fi

tool_context_metadata_hits="$(
  rg -n "AgentToolRequestContext\\.(CurrentMetadata|TryGet\\()|\\.ToLegacyMetadata\\(|HttpAuthorizationMetadataKey" src agents \
    -g '*.cs' || true
)"

if [ -n "${tool_context_metadata_hits}" ]; then
  echo "${tool_context_metadata_hits}"
  echo "Public legacy metadata control shims are forbidden. Use typed context fields and local scrub-only blocked keys."
  exit 1
fi

transition_override_without_matcher=""
while IFS= read -r transition_file; do
  [ -z "${transition_file}" ] && continue

  if ! rg -n "StateTransitionMatcher" "${transition_file}" >/dev/null; then
    transition_override_without_matcher="${transition_override_without_matcher}${transition_file}\n"
  fi
done < <(rg -l "override\\s+[^\\n]*TransitionState\\(" \
  src \
  -g '*.cs' \
  -g '!*.g.cs' \
  -g '!src/Aevatar.Foundation.Core/EventSourcing/DefaultEventSourcingBehaviorFactory.cs' || true)

if [ -n "${transition_override_without_matcher}" ]; then
  printf '%b' "${transition_override_without_matcher}"
  echo "Stateful TransitionState overrides in src must use StateTransitionMatcher for Any-safe replay semantics."
  exit 1
fi

if rg -n "<MassTransitVersion>9\." Directory.Packages.props; then
  echo "MassTransit v9 is forbidden in this repository. Keep MassTransitVersion on v8.x."
  exit 1
fi

mass_transit_v9_refs="$(
  rg -n "<Package(Reference|Version) Include=\"MassTransit(\.[^\"]*)?\" Version=\"9\." \
    Directory.Packages.props src test tools \
    -g 'Directory.Packages.props' -g '*.csproj' || true
)"

if [ -n "${mass_transit_v9_refs}" ]; then
  echo "${mass_transit_v9_refs}"
  echo "MassTransit package references must stay on v8.x. v9 references are forbidden."
  exit 1
fi

reducer_test_coverage_violations=""
reducer_test_scan_roots=(
  "src/Aevatar.AI.Projection/Reducers"
  "src/workflow/Aevatar.Workflow.Projection/Reducers"
)

for root in "${reducer_test_scan_roots[@]}"; do
  if [ ! -d "${root}" ]; then
    continue
  fi

  while IFS= read -r file; do
    while IFS= read -r class_line; do
      if [ -z "${class_line}" ]; then
        continue
      fi

      line_no="$(echo "${class_line}" | cut -d: -f1)"
      decl="$(echo "${class_line}" | cut -d: -f2-)"

      if echo "${decl}" | rg -q "\babstract\s+class\b"; then
        continue
      fi

      class_name="$(echo "${decl}" | sed -E 's/.*class[[:space:]]+([A-Za-z0-9_]+).*/\1/')"
      if [ -z "${class_name}" ]; then
        continue
      fi

      if ! rg -n "\b${class_name}\b" test -g '*Tests.cs' >/dev/null; then
        reducer_test_coverage_violations="${reducer_test_coverage_violations}${file}:${line_no}:${class_name}\n"
      fi
    done < <(rg -n "^\s*(public|internal)\s+(sealed\s+)?(abstract\s+)?class\s+[A-Za-z0-9_]*Reducer[A-Za-z0-9_]*\b" "${file}" || true)
  done < <(rg --files "${root}" -g '*Reducer*.cs')
done

if [ -n "${reducer_test_coverage_violations}" ]; then
  printf '%b' "${reducer_test_coverage_violations}"
  echo "Projection reducer coverage guard failed: each non-abstract reducer class must be referenced by tests."
  exit 1
fi

echo "Running deterministic compute handler guard..."
bash tools/ci/deterministic_compute_handler_guard.sh

stateful_replay_contract_requirements=(
  "WorkflowGAgent:test/Aevatar.Integration.Tests/WorkflowGAgentReplayContractTests.cs"
  "RoleGAgent:test/Aevatar.AI.Tests/RoleGAgentReplayContractTests.cs"
)

for requirement in "${stateful_replay_contract_requirements[@]}"; do
  actor_name="${requirement%%:*}"
  contract_file="${requirement#*:}"

  if [ ! -f "${contract_file}" ]; then
    echo "Missing replay contract test file for ${actor_name}: ${contract_file}"
    exit 1
  fi

  if ! rg -n "\\b${actor_name}\\b" "${contract_file}" >/dev/null; then
    echo "${contract_file}"
    echo "Replay contract test file must reference actor ${actor_name}."
    exit 1
  fi

  if ! rg -n "Persist.*Event|Replay|Reactivate|ActivateAsync|DeactivateAsync" "${contract_file}" >/dev/null; then
    echo "${contract_file}"
    echo "Replay contract test file for ${actor_name} must assert persisted-event replay semantics."
    exit 1
  fi
done

echo "Running projection route-mapping guard..."
bash tools/ci/projection_route_mapping_guard.sh

echo "Running closed-world workflow guards..."
bash tools/ci/workflow_closed_world_guards.sh

echo "Running workflow saga compensation guard..."
bash tools/ci/workflow_saga_compensation_guard.sh

echo "Running workflow run-id guard..."
bash tools/ci/workflow_runid_guard.sh

echo "Running workflow binding boundary guard..."
bash tools/ci/workflow_binding_boundary_guard.sh

echo "Running script inheritance guard..."
bash tools/ci/script_inheritance_guard.sh

echo "Running scripting interaction boundary guard..."
bash tools/ci/scripting_interaction_boundary_guard.sh

echo "Running scripting read model wrapper guard..."
bash tools/ci/scripting_readmodel_wrapper_guard.sh

if rg -n "Aevatar\.AI\.Core\.csproj" src/workflow/Aevatar.Workflow.Core/Aevatar.Workflow.Core.csproj; then
  echo "Workflows.Core must not reference AI.Core."
  exit 1
fi

if rg -n "Aevatar\.AI\.(Abstractions|Core|LLMProviders\.MEAI|LLMProviders\.Tornado|ToolProviders\.MCP|ToolProviders\.Skills)\.csproj" \
  src/Aevatar.Bootstrap/Aevatar.Bootstrap.csproj
then
  echo "Aevatar.Bootstrap must not directly reference AI implementations. Use Bootstrap.Extensions.AI composition package."
  exit 1
fi

if rg -n "Aevatar\.AI\.Projection\.csproj" src/workflow/Aevatar.Workflow.Projection/Aevatar.Workflow.Projection.csproj; then
  echo "Workflow.Projection must not directly reference AI.Projection. Use workflow extension composition."
  exit 1
fi

if rg -n "AddAIDefaultProjectionLayer<" src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs; then
  echo "Workflow.Projection must not directly register AI default projection layer."
  exit 1
fi

workflow_to_maker_violations="$(
  rg -n "Aevatar\.Maker\..*\.csproj" src/workflow -g '*.csproj' || true
)"

if [ -n "${workflow_to_maker_violations}" ]; then
  echo "${workflow_to_maker_violations}"
  echo "Workflow projects must not reference Maker projects."
  exit 1
fi

for legacy_maker_project in \
  src/maker/Aevatar.Maker.Application.Abstractions/Aevatar.Maker.Application.Abstractions.csproj \
  src/maker/Aevatar.Maker.Application/Aevatar.Maker.Application.csproj \
  src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj \
  src/maker/Aevatar.Maker.Host.Api/Aevatar.Maker.Host.Api.csproj \
  src/maker/Aevatar.Maker.Core/Aevatar.Maker.Core.csproj
do
  if [ -f "${legacy_maker_project}" ]; then
    echo "Legacy Maker capability project must be removed: ${legacy_maker_project}"
    exit 1
  fi
done

if rg -n "AddMakerCapability\(" src -g '*.cs'; then
  echo "AddMakerCapability is forbidden. Maker must be wired as workflow extension plugin."
  exit 1
fi

if rg -n "MapMakerCapabilityEndpoints|/api/maker" src -g '*.cs'; then
  echo "Maker standalone capability endpoints are forbidden."
  exit 1
fi

mainnet_program="src/Aevatar.Mainnet.Host.Api/Program.cs"
mainnet_host_extensions="src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"

if ! rg -n "AddAevatarMainnetHost\(" "${mainnet_program}" >/dev/null; then
  echo "Mainnet Program.cs must call AddAevatarMainnetHost()."
  exit 1
fi

if ! rg -n "MapAevatarMainnetHost\(" "${mainnet_program}" >/dev/null; then
  echo "Mainnet Program.cs must call MapAevatarMainnetHost()."
  exit 1
fi

if ! rg -n "AddAevatarPlatform\(" "${mainnet_host_extensions}" >/dev/null; then
  echo "Mainnet host must register platform capabilities via AddAevatarPlatform(...)."
  exit 1
fi

if ! rg -n "EnableMakerExtensions\s*=\s*true" "${mainnet_host_extensions}" >/dev/null; then
  echo "Mainnet host must enable Maker via AddAevatarPlatform(options => { options.EnableMakerExtensions = true; })."
  exit 1
fi

if ! rg -n "AddAevatarPlatform\(" src/workflow/Aevatar.Workflow.Host.Api/Program.cs >/dev/null; then
  echo "Workflow host must register platform capabilities via AddAevatarPlatform(...)."
  exit 1
fi

if ! rg -n "AddWorkflowProjectionReadModelProviders\(" \
  src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/AevatarPlatformHostBuilderExtensions.cs >/dev/null
then
  echo "Platform hosting extension must register read-model providers via AddWorkflowProjectionReadModelProviders()."
  exit 1
fi

if [ -f "src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerModuleFactory.cs" ]; then
  echo "Maker extension must use unified module pack model; MakerModuleFactory is forbidden."
  exit 1
fi

if rg -n "IEventModuleFactory" src/workflow/extensions/Aevatar.Workflow.Extensions.Maker -g '*.cs'; then
  echo "Maker extension must not register standalone IEventModuleFactory. Use IWorkflowModulePack."
  exit 1
fi

if ! rg -n "AddWorkflowModulePack<MakerModulePack>\(" src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/ServiceCollectionExtensions.cs >/dev/null; then
  echo "Maker extension must register MakerModulePack via AddWorkflowModulePack<MakerModulePack>()."
  exit 1
fi

for host_composition_file in \
  "${mainnet_host_extensions}" \
  src/workflow/Aevatar.Workflow.Host.Api
do
  if ! rg -n "AddAevatarDefaultHost\(" "${host_composition_file}" -g '*.cs' >/dev/null; then
    echo "Missing AddAevatarDefaultHost in ${host_composition_file}"
    exit 1
  fi

  if ! rg -n "UseAevatarDefaultHost\(" "${host_composition_file}" -g '*.cs' >/dev/null; then
    echo "Missing UseAevatarDefaultHost in ${host_composition_file}"
    exit 1
  fi
done

if rg -n "AddCqrsCore\(" \
  src/Aevatar.Mainnet.Host.Api \
  src/workflow/Aevatar.Workflow.Host.Api \
  src/workflow/Aevatar.Workflow.Infrastructure
then
  echo "Direct AddCqrsCore wiring in hosts/infrastructure is forbidden. Use Aevatar.CQRS.Runtime.Hosting."
  exit 1
fi

if rg -n "Aevatar\.CQRS\.Projection\.Providers\..*\.csproj" \
  src/workflow/Aevatar.Workflow.Infrastructure/Aevatar.Workflow.Infrastructure.csproj
then
  echo "Workflow.Infrastructure must not reference projection provider implementation projects. Register providers in host/extensions layer."
  exit 1
fi

if rg -n "using\s+Aevatar\.CQRS\.Projection\.Providers\." \
  src/workflow/Aevatar.Workflow.Infrastructure \
  -g '*.cs'
then
  echo "Workflow.Infrastructure source must not reference projection provider namespaces. Register providers in host/extensions layer."
  exit 1
fi

if rg -n "TryGetContext\(" src; then
  echo "Projection context reverse lookup is forbidden. Use explicit projection lease/session handles."
  exit 1
fi

if rg -n "SemaphoreSlim" src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionPort.cs; then
  echo "WorkflowExecutionProjectionPort must not use process-local SemaphoreSlim for projection start arbitration."
  exit 1
fi

if [ -f "src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSubscriptionRegistry.cs" ] && \
  rg -n "Dictionary<|ConcurrentDictionary<" src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSubscriptionRegistry.cs; then
  echo "ProjectionSubscriptionRegistry must not keep in-memory dictionary state."
  exit 1
fi

lifecycle_port="src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs"
current_state_query_port="src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionCurrentStateQueryPort.cs"
artifact_query_port="src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionArtifactQueryPort.cs"

if [ ! -f "${lifecycle_port}" ] || [ ! -f "${current_state_query_port}" ] || [ ! -f "${artifact_query_port}" ]; then
  echo "Workflow projection ports must be split into lifecycle/query contracts."
  exit 1
fi

if rg -n "Task\s+AttachLiveSinkAsync\(\s*string\s+actorId|Task\s+DetachLiveSinkAsync\(\s*string\s+actorId|Task\s+ReleaseActorProjectionAsync\(\s*string\s+actorId" \
  "${lifecycle_port}"
then
  echo "Workflow projection lifecycle port must use lease/session handles instead of actorId context lookup."
  exit 1
fi

if ! rg -n "IWorkflowExecutionProjectionLease" "${lifecycle_port}" >/dev/null; then
  echo "Workflow projection lifecycle port must depend on IWorkflowExecutionProjectionLease."
  exit 1
fi

if rg -n "EnsureActorProjectionAsync|AttachLiveSinkAsync|DetachLiveSinkAsync|ReleaseActorProjectionAsync" \
  "${current_state_query_port}" \
  "${artifact_query_port}"
then
  echo "Workflow projection query port must not include lifecycle operations."
  exit 1
fi

if rg -n "ListActorTimelineAsync|GetActorGraphEdgesAsync|GetActorGraphSubgraphAsync" \
  "${current_state_query_port}"
then
  echo "Workflow current-state query port must not include artifact queries."
  exit 1
fi

if rg -n "GetActorSnapshotAsync|ListActorSnapshotsAsync|GetActorProjectionStateAsync" \
  "${artifact_query_port}"
then
  echo "Workflow artifact query port must not include current-state queries."
  exit 1
fi

id_mapping_state_pattern='^\s*(private|protected|internal)\s+((readonly|static)\s+)*((global::)?System\.Collections\.(Concurrent\.)?)?(ConcurrentDictionary|Dictionary|HashSet|Queue)\s*<[^>]+>\s+_[A-Za-z0-9_]*(actor|entity|run|session)[A-Za-z0-9_]*\b'
id_mapping_state_hits=""

id_mapping_scan_roots=(
  "src/Aevatar.CQRS.Projection.Core"
  "src/Aevatar.Foundation.Projection"
  "src/Aevatar.AI.Projection"
  "src/workflow/Aevatar.Workflow.Projection"
  "src/workflow/Aevatar.Workflow.Application"
  "src/Aevatar.Scripting.Application"
  "src/Aevatar.Scripting.Infrastructure"
  "src/Aevatar.Scripting.Projection"
)

scan_args=()
for root in "${id_mapping_scan_roots[@]}"; do
  if [ -d "${root}" ]; then
    scan_args+=("${root}")
  fi
done

if [ "${#scan_args[@]}" -gt 0 ]; then
  while IFS= read -r file; do
    if [ -z "${file}" ]; then
      continue
    fi

    case "${file}" in
      *InMemory*|*inmemory*|*Tests.cs|*.g.cs)
        continue
        ;;
    esac

    hits="$(rg -n -i "${id_mapping_state_pattern}" "${file}" || true)"
    if [ -n "${hits}" ]; then
      id_mapping_state_hits="${id_mapping_state_hits}
${file}
${hits}
"
    fi
  done < <(rg --files "${scan_args[@]}" -g '*.cs')
fi

if [ -n "${id_mapping_state_hits}" ]; then
  echo "${id_mapping_state_hits}"
  echo "Full-scan violation: middle-layer actor/entity/run/session ID-mapping in-memory dictionary state is forbidden. Use actorized orchestration, lease/session handles, or distributed state abstractions."
  exit 1
fi

runtime_relay_hits="$(
  rg -n "ListBySourceAsync\(|StreamForwardingRules\.TryBuildForwardedEnvelope\(" \
    src/Aevatar.Foundation.Runtime/Actor \
    src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors \
    -g '*.cs' || true
)"

if [ -n "${runtime_relay_hits}" ]; then
  echo "${runtime_relay_hits}"
  echo "Runtime actor layer must not execute relay graph traversal. Relay execution must stay in stream/message-queue infrastructure."
  exit 1
fi

implementation_ref_violations="$(
  rg -n "Aevatar\.CQRS\.Runtime\.Implementations\.(Wolverine|MassTransit)" src -g '*.csproj' \
  | rg -v "^src/Aevatar.CQRS.Runtime.Hosting/" \
  | rg -v "^src/Aevatar.CQRS.Runtime.Implementations.Wolverine/" \
  | rg -v "^src/Aevatar.CQRS.Runtime.Implementations.MassTransit/" \
  || true
)"

if [ -n "${implementation_ref_violations}" ]; then
  echo "${implementation_ref_violations}"
  echo "Only Runtime.Hosting may reference Runtime.Implementations.* directly."
  exit 1
fi

projection_provider_business_dependency_hits="$(
  rg -n "Aevatar\.(Workflow|AI)\..*\.csproj" \
    src/Aevatar.CQRS.Projection.Providers.InMemory \
    src/Aevatar.CQRS.Projection.Providers.Elasticsearch \
    src/Aevatar.CQRS.Projection.Providers.Neo4j \
    -g '*.csproj' || true
)"

if [ -n "${projection_provider_business_dependency_hits}" ]; then
  echo "${projection_provider_business_dependency_hits}"
  echo "Projection provider projects must remain business-agnostic. Workflow/AI project references are forbidden."
  exit 1
fi

projection_provider_business_using_hits="$(
  rg -n "using\s+Aevatar\.(Workflow|AI)\." \
    src/Aevatar.CQRS.Projection.Providers.InMemory \
    src/Aevatar.CQRS.Projection.Providers.Elasticsearch \
    src/Aevatar.CQRS.Projection.Providers.Neo4j \
    -g '*.cs' || true
)"

if [ -n "${projection_provider_business_using_hits}" ]; then
  echo "${projection_provider_business_using_hits}"
  echo "Projection provider source files must not reference Workflow/AI namespaces."
  exit 1
fi

agent_projection_provider_hits="$(
  rg -n "Aevatar\.CQRS\.Projection\.Providers\.(Elasticsearch|InMemory|Neo4j)|Projection\.Providers\.(Elasticsearch|InMemory|Neo4j)" \
    agents \
    -g '*.cs' \
    -g '*.csproj' \
    -g '!**/bin/**' \
    -g '!**/obj/**' || true
)"

if [ -n "${agent_projection_provider_hits}" ]; then
  echo "${agent_projection_provider_hits}"
  echo "Agent projects must not reference concrete projection providers. Wire provider-specific document stores in the host composition root."
  exit 1
fi

projection_provider_store_files=(
  "src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionDocumentStore.cs"
  "src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchOptimisticWriter.cs"
)

for provider_store_file in "${projection_provider_store_files[@]}"; do
  if [ ! -f "${provider_store_file}" ]; then
    echo "Missing provider store file: ${provider_store_file}"
    exit 1
  fi

  if ! rg -F "Projection read-model write completed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result}" "${provider_store_file}" >/dev/null; then
    echo "${provider_store_file}"
    echo "Provider write path must emit structured success log with provider/readModelType/key/elapsedMs/result."
    exit 1
  fi

  if ! rg -F "Projection read-model write failed. provider={Provider} readModelType={ReadModelType} key={Key} elapsedMs={ElapsedMs} result={Result} errorType={ErrorType}" "${provider_store_file}" >/dev/null; then
    echo "${provider_store_file}"
    echo "Provider write path must emit structured failure log with provider/readModelType/key/elapsedMs/result/errorType."
    exit 1
  fi
done

command_side_readmodel_violations="$(
  rg -n "IProjectionDocumentStore<|IProjectionGraphStore|ProjectionDocumentStore|ProjectionGraphStore" \
    src/workflow/Aevatar.Workflow.Application \
    src/workflow/Aevatar.Workflow.Host.Api \
    src/Aevatar.Mainnet.Host.Api \
    -g '*.cs' \
    -g '!src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs' \
    -g '!src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs' \
    -g '!src/Aevatar.Mainnet.Host.Api/Hosting/HttpOAuthClientEsAclProbe.cs' || true
)"

if [ -n "${command_side_readmodel_violations}" ]; then
  echo "${command_side_readmodel_violations}"
  echo "Command-side services/endpoints must not depend on projection read-model stores directly."
  exit 1
fi

# Refactor (iter57/cluster-065-lark-card-signal-only):
#   Old pattern: ConversationGAgent Lark CardKit Task.Run helpers built rich business
#   continuation payloads outside the actor turn.
#   New principle: helpers only dispatch LarkCardOperationCompletedEvent with minimal raw
#   operation result; actor handlers own error-code interpretation, timestamps, and lifecycle
#   field mapping.
lark_card_streaming_file="agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.LarkCardStreaming.cs"
if [ -f "${lark_card_streaming_file}" ]; then
  lark_card_taskrun_helper_report="$(
    awk '
      /private async Task ExecuteLarkCard(Create|Stream|Finalize)OperationAsync/ {
        in_helper = 1
        body_started = 0
        brace_depth = 0
        completed_signal_pending = 0
        completed_signal_depth = -1
      }
      in_helper {
        line = $0
        hard_forbidden = "LarkCard(Create|Stream|Finalize)ContinuationEvent|To(Create|Stream|Finalize)Continuation|CompletedAtUnixMs|DateTimeOffset\\.UtcNow\\.ToUnixTimeMilliseconds\\(\\)|ErrorCode[[:space:]]*=|\\$\"(create|stream|finalize)_threw"
        lifecycle_mapping = "CardMessageId[[:space:]]*=|FinalTextWritten[[:space:]]*="
        inside_completed_signal = completed_signal_depth >= 0 || line ~ /new[[:space:]]+LarkCardOperationCompletedEvent/

        if (body_started && line ~ hard_forbidden) {
          print FILENAME ":" FNR ":" $0
        }
        if (body_started && !inside_completed_signal && line ~ lifecycle_mapping) {
          print FILENAME ":" FNR ":" $0
        }

        if (line ~ /new[[:space:]]+LarkCardOperationCompletedEvent/) {
          completed_signal_pending = 1
        }

        opens = gsub(/\{/, "{", line)
        closes = gsub(/\}/, "}", line)
        if (!body_started && opens > 0) {
          body_started = 1
        }
        brace_depth += opens - closes

        if (completed_signal_pending && opens > 0) {
          completed_signal_depth = brace_depth
          completed_signal_pending = 0
        }
        if (completed_signal_depth >= 0 && brace_depth < completed_signal_depth) {
          completed_signal_depth = -1
        }
        if (body_started && brace_depth == 0) {
          in_helper = 0
          body_started = 0
          completed_signal_pending = 0
          completed_signal_depth = -1
        }
      }
    ' "${lark_card_streaming_file}"
  )"
  if [ -n "${lark_card_taskrun_helper_report}" ]; then
    echo "${lark_card_taskrun_helper_report}"
    echo "ConversationGAgent Lark CardKit Task.Run helpers must stay signal-only: no rich continuation types, business error-code strings, timestamps, or lifecycle field mapping in ExecuteLarkCard*OperationAsync."
    exit 1
  fi
fi

# Refactor (iter57/cluster-068-lark-extend-signal):
#   Old pattern: ConversationGAgent Nyx relay text streaming awaited external relay writes
#   inside the actor turn and interpreted their result inline.
#   New principle: text helpers only dispatch NyxRelayTextOperationCompletedEvent with
#   minimal raw result; actor handlers own lifecycle, timestamps, terminal reasons, and
#   completion persistence.
conversation_gagent_file="agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs"
if [ -f "${conversation_gagent_file}" ]; then
  nyx_relay_text_helper_report="$(
    awk '
      /private async Task ExecuteNyxRelayTextOperationAsync/ {
        in_helper = 1
        body_started = 0
        brace_depth = 0
        completed_signal_pending = 0
        completed_signal_depth = -1
      }
      in_helper {
        line = $0
        hard_forbidden = "TransitionNyxRelayStreamingPhaseAsync|PersistStreamedCompletionAsync|DateTimeOffset\\.UtcNow\\.ToUnixTimeMilliseconds\\(\\)|TerminalSucceeded|TerminalPartial|DisabledPreSend|SuppressingInterim|terminalReason|failed_self_heal|final_edit_failed|interim_edit_failed|first_send_failed"
        inside_completed_signal = completed_signal_depth >= 0 || line ~ /new[[:space:]]+NyxRelayTextOperationCompletedEvent/

        if (body_started && !inside_completed_signal && line ~ hard_forbidden) {
          print FILENAME ":" FNR ":" $0
        }

        if (line ~ /new[[:space:]]+NyxRelayTextOperationCompletedEvent/) {
          completed_signal_pending = 1
        }

        opens = gsub(/\{/, "{", line)
        closes = gsub(/\}/, "}", line)
        if (!body_started && opens > 0) {
          body_started = 1
        }
        brace_depth += opens - closes

        if (completed_signal_pending && opens > 0) {
          completed_signal_depth = brace_depth
          completed_signal_pending = 0
        }
        if (completed_signal_depth >= 0 && brace_depth < completed_signal_depth) {
          completed_signal_depth = -1
        }
        if (body_started && brace_depth == 0) {
          in_helper = 0
          body_started = 0
          completed_signal_pending = 0
          completed_signal_depth = -1
        }
      }
    ' "${conversation_gagent_file}"
  )"
  if [ -n "${nyx_relay_text_helper_report}" ]; then
    echo "${nyx_relay_text_helper_report}"
    echo "ConversationGAgent Nyx relay text helper must stay signal-only: no lifecycle transitions, terminal reasons, timestamps, or persistence in ExecuteNyxRelayTextOperationAsync."
    exit 1
  fi
fi

check_orchestration_class_guard() {
  local file_path="$1"
  local max_non_empty_lines="$2"
  local max_private_dependencies="$3"

  if [ ! -f "${file_path}" ]; then
    return 0
  fi

  local non_empty_lines
  non_empty_lines="$(awk 'NF && $1 !~ /^\/\// {count++} END {print count + 0}' "${file_path}")"
  if [ "${non_empty_lines}" -gt "${max_non_empty_lines}" ]; then
    echo "${file_path}: ${non_empty_lines} non-empty lines exceeds limit ${max_non_empty_lines}."
    echo "Orchestration classes must stay thin. Extract strategy/resource details into dedicated components."
    exit 1
  fi

  local dependency_count
  dependency_count="$(rg -n "^\s*private readonly " "${file_path}" | wc -l | tr -d '[:space:]')"
  if [ "${dependency_count}" -gt "${max_private_dependencies}" ]; then
    echo "${file_path}: ${dependency_count} private dependencies exceeds limit ${max_private_dependencies}."
    echo "Orchestration classes must not accumulate too many direct collaborators."
    exit 1
  fi
}

check_system_skill_overlay_dual_seam_injection() {
  local nyxid_chat_gagent_file="agents/Aevatar.GAgents.NyxidChat/NyxIdChatGAgent.cs"
  local conversation_reply_generator_file="agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs"
  local nyxid_di_file="agents/Aevatar.GAgents.NyxidChat/ServiceCollectionExtensions.cs"
  local ornn_di_file="src/Aevatar.AI.ToolProviders.Ornn/ServiceCollectionExtensions.cs"
  local ornn_provider_file="src/Aevatar.AI.ToolProviders.Ornn/SystemSkillOverlay/OrnnSystemSkillOverlayProvider.cs"
  local prompt_injection_test_file="test/Aevatar.AI.Tests/SystemSkillOverlayPromptInjectionTests.cs"

  if ! rg -q "override string DecorateSystemPrompt" "${nyxid_chat_gagent_file}" \
    || ! rg -q "SystemSkillOverlayRequest\.DirectChat" "${nyxid_chat_gagent_file}" \
    || ! rg -q "SystemPromptLayerComposer\.Compose" "${nyxid_chat_gagent_file}" \
    || ! rg -q "IBuiltInPromptFloorProvider" "${nyxid_chat_gagent_file}"; then
    echo "NyxIdChatGAgent.DecorateSystemPrompt must compose the typed kernel, mandatory floor, dm global layer, and runtime facts."
    exit 1
  fi

  if ! awk '
    /private string BuildSystemPrompt[[:space:]]*\(/ { in_func = 1; body_started = 0; brace_depth = 0 }
    in_func {
      line = $0
      if (line ~ /SystemPromptLayerComposer\.Compose[[:space:]]*\(/) found_composer = 1
      if (line ~ /_builtInPromptFloorProvider\.GetFloor[[:space:]]*\(/) found_floor = 1
      opens = gsub(/\{/, "{", line)
      closes = gsub(/\}/, "}", line)
      if (!body_started && opens > 0) body_started = 1
      brace_depth += opens - closes
      if (body_started && brace_depth == 0) in_func = 0
    }
    END { exit(found_composer && found_floor ? 0 : 1) }
  ' "${conversation_reply_generator_file}"; then
    echo "ConversationReplyGenerator.BuildSystemPrompt must compose the typed kernel and mandatory floor through SystemPromptLayerComposer."
    exit 1
  fi

  if rg -q -e 'AppendSystemSkillOverlay|LoadBaseSystemPrompt' \
    "${nyxid_chat_gagent_file}" "${conversation_reply_generator_file}"; then
    echo "Direct and relay prompt seams must not retain hand-written overlay append or duplicate kernel loading paths."
    exit 1
  fi

  if rg -q -e 'Services\.GetService<(IBuiltInPromptFloorProvider|ISystemSkillOverlayProvider)>' \
    -e 'new[[:space:]]+BuiltInPromptFloorProvider[[:space:]]*\(' \
    "${nyxid_chat_gagent_file}" "${conversation_reply_generator_file}"; then
    echo "Direct and relay prompt seams must receive prompt providers through explicit constructor dependencies."
    exit 1
  fi

  if [ ! -f "${prompt_injection_test_file}" ]; then
    echo "System skill overlay prompt injection tests are required."
    exit 1
  fi

  if ! rg -q 'TryAddSingleton<IBuiltInPromptFloorProvider>' "${nyxid_di_file}"; then
    echo "AddNyxIdChat must always register the mandatory built-in prompt floor."
    exit 1
  fi

  if ! rg -q 'TryAddSingleton<.*ISystemSkillOverlayProvider' "${ornn_di_file}"; then
    echo "The optional Ornn global prompt layer must have an independent production DI registration."
    exit 1
  fi

  if rg -q -e 'ISystemSkillOverlayFallback|IBuiltInPromptFloorProvider' "${ornn_provider_file}"; then
    echo "The Ornn global provider must not depend on or return the mandatory built-in floor."
    exit 1
  fi

  if rg -q 'ISystemSkillOverlayFallback' agents src -g '*.cs'; then
    echo "The retired remote-or-floor fallback contract must not be reintroduced."
    exit 1
  fi

  # Core owns only pure composition and must not resolve host providers.
  if rg -q -e 'Get(Service|RequiredService)<(ISystemSkillOverlayProvider|IBuiltInPromptFloorProvider)>' \
    src/Aevatar.AI.Core -g '*.cs'; then
    echo "Aevatar.AI.Core must remain provider-neutral; host seams supply all typed prompt layers."
    exit 1
  fi
}

check_system_skill_overlay_eval_docs() {
  local eval_file="tools/eval/system_skill_overlay_golden_tasks.md"

  if [ ! -s "${eval_file}" ]; then
    echo "System skill overlay golden-tasks document is required (tools/eval/system_skill_overlay_golden_tasks.md)."
    exit 1
  fi
}

check_system_skill_overlay_set_source() {
  local options_file="src/Aevatar.AI.Abstractions/ToolProviders/SystemSkillOverlayOptions.cs"
  local provider_file="src/Aevatar.AI.ToolProviders.Ornn/SystemSkillOverlay/OrnnSystemSkillOverlayProvider.cs"
  local provider_interface="src/Aevatar.AI.Abstractions/ToolProviders/ISystemSkillOverlayProvider.cs"

  # The overlay source is a public, org-owned skillset resolved by a non-secret name — never an org
  # service token secret (issue #2498). Reintroducing OrgServiceToken re-adds a secret and a squat vector.
  if rg -q -e 'OrgServiceToken' agents src -g '*.cs'; then
    echo "System skill overlay must not reintroduce OrgServiceToken; the public org-owned set is read with no secret."
    exit 1
  fi
  if ! rg -q -e '\bSetName\b' "${options_file}"; then
    echo "System skill overlay options must expose the non-secret SetName as the overlay source."
    exit 1
  fi

  # Members come from the skillset (membership = trust anchor), not a squattable tag search.
  if ! rg -q -e 'GetSkillSetAsync' "${provider_file}"; then
    echo "The Ornn overlay provider must resolve members from the skillset (GetSkillSetAsync)."
    exit 1
  fi
  if rg -q -e 'SearchSkillsAsync' "${provider_file}"; then
    echo "The Ornn overlay provider must not fall back to a tag search (SearchSkillsAsync); the set is the source."
    exit 1
  fi

  # Never query-time: the seam read GetCurrent must be a synchronous cached read, not an awaited fetch.
  if rg -q -e 'Task<[^>]*>[[:space:]]+GetCurrent' "${provider_interface}"; then
    echo "ISystemSkillOverlayProvider.GetCurrent must be a synchronous cached read (never a query-time fetch)."
    exit 1
  fi

  if rg -q -e 'ISystemSkillOverlayFallback|IBuiltInPromptFloorProvider' "${provider_file}"; then
    echo "Remote gate, TTL, and last-known-good behavior must remain confined to the optional global layer."
    exit 1
  fi
}

check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs" \
  60 \
  4
check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunExecutionEngine.cs" \
  120 \
  6
check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs" \
  190 \
  10
check_system_skill_overlay_dual_seam_injection
check_system_skill_overlay_eval_docs
check_system_skill_overlay_set_source

echo "Running agent profile governance guard..."
bash tools/ci/agent_profile_governance_guard.sh

echo "Running CQRS/EventSourcing boundary guard..."
bash tools/ci/cqrs_eventsourcing_boundary_guard.sh

echo "Running committed-state projection guard..."
bash tools/ci/committed_state_projection_guard.sh

echo "Running audit trail guard..."
bash tools/ci/audit_trail_guards.sh

echo "Running projection activation provider coverage guard..."
bash tools/ci/projection_activation_provider_coverage_guard.sh

echo "Running scripting runtime snapshot guard..."
bash tools/ci/scripting_runtime_snapshot_guard.sh

echo "Running runtime callback guards..."
bash tools/ci/runtime_callback_guards.sh

echo "Running channel card literal guard..."
bash tools/ci/channel_card_literal_guard.sh

echo "Running Nyx relay replay authority guard..."
python3 tools/ci/guards/nyx_relay_replay_authority_guard.py

echo "Running Lark agent path contract guard..."
bash tools/ci/lark_agent_path_contract_guard.sh

echo "Running agent turn tool catalog guard..."
bash tools/ci/agent_turn_tool_catalog_guard.sh

echo "Running docs lint guard..."
bash tools/docs/lint.sh

echo "Architecture guards passed."
