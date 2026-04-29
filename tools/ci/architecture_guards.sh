#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ZERO_SHA="0000000000000000000000000000000000000000"
DIFF_MODE="worktree"
DIFF_RANGE_VALUE="${DIFF_RANGE:-}"

if [[ -n "${DIFF_RANGE_VALUE}" ]]; then
  DIFF_MODE="range"
elif [[ "${GITHUB_EVENT_NAME:-}" == "pull_request" && -n "${GITHUB_BASE_REF:-}" ]]; then
  git fetch --no-tags --depth=1 origin "${GITHUB_BASE_REF}"
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

if rg -n "Aevatar\.Host\.Api|Aevatar\.Host\.Gateway" aevatar.slnx; then
  echo "Solution must not include legacy host projects."
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

if rg -n "IProjectionReadModelBindingResolver|ProjectionReadModelBindingResolver|ProjectionReadModelBindingException" src test; then
  echo "BindingResolver-based projection routing is forbidden. Use capability-based Document/Graph routing."
  exit 1
fi

if rg -n "IGAgentActorStore|ActorBackedGAgentActorStore" src agents; then
  echo "Legacy GAgent actor store is forbidden. Use registry command/query/admission ports."
  exit 1
fi

bash "${SCRIPT_DIR}/query_projection_priming_guard.sh"
bash "${SCRIPT_DIR}/scripting_write_path_cqrs_guard.sh"
bash "${SCRIPT_DIR}/projection_state_version_guard.sh"
bash "${SCRIPT_DIR}/projection_state_mirror_current_state_guard.sh"
bash "${SCRIPT_DIR}/proto_lint_guard.sh"
bash "${SCRIPT_DIR}/channel_mega_interface_guard.sh"
bash "${SCRIPT_DIR}/channel_native_sdk_import_guard.sh"
bash "${SCRIPT_DIR}/channel_platform_project_reference_guard.sh"
bash "${SCRIPT_DIR}/channel_inbox_gagent_guard.sh"
bash "${SCRIPT_DIR}/channel_relay_nyx_chat_direct_create_guard.sh"
bash "${SCRIPT_DIR}/channel_tombstone_proto_field_guard.sh"
bash "${SCRIPT_DIR}/agent_tool_delivery_target_reader_guard.sh"
bash "${SCRIPT_DIR}/studio_projection_readmodel_registration_guard.sh"

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

if rg -n "IScriptBehaviorArtifactResolver|ScriptBehaviorArtifactRequest|IScriptReadModelMaterializationCompiler" src/Aevatar.Scripting.Projection; then
  echo "Scripting projection must not resolve behavior artifacts or compile native materialization plans. Consume committed durable facts only."
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
# Check for reader.ListAsync() calls (dot-prefixed) in files that use IProjectionDocumentReader.
# Business-domain ListAsync methods (e.g., IStreamingProxyParticipantStore.ListAsync) are excluded
# by requiring the call to be on a reader/document field (dot prefix pattern).
projection_document_reader_list_report="$(
  rg -l "IProjectionDocumentReader<" src test demos \
    | xargs -r rg -n "\.ListAsync\(" \
    | rg -i "(reader|document|projection).*\.ListAsync"
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

  if (line ~ /(^|[[:space:](;])(this\.)?State\.[A-Za-z_][A-Za-z0-9_]*[[:space:]]*(\+\+|--|[+*%\/-]?=)/)
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

if rg -n "TypeUrl\.Contains|typeUrl\.Contains\(" src demos; then
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
    Directory.Packages.props src test demos tools \
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

stateful_replay_contract_requirements=(
  "WorkflowGAgent:test/Aevatar.Integration.Tests/WorkflowGAgentCoverageTests.cs"
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

echo "Running workflow run-id guard..."
bash tools/ci/workflow_runid_guard.sh

echo "Running workflow binding boundary guard..."
bash tools/ci/workflow_binding_boundary_guard.sh

echo "Running playground asset drift guard..."
bash tools/ci/playground_asset_drift_guard.sh

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
    -g '*.cs' || true
)"

if [ -n "${command_side_readmodel_violations}" ]; then
  echo "${command_side_readmodel_violations}"
  echo "Command-side services/endpoints must not depend on projection read-model stores directly."
  exit 1
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

echo "Running CQRS/EventSourcing boundary guard..."
bash tools/ci/cqrs_eventsourcing_boundary_guard.sh

echo "Running committed-state projection guard..."
bash tools/ci/committed_state_projection_guard.sh

echo "Running scripting runtime snapshot guard..."
bash tools/ci/scripting_runtime_snapshot_guard.sh

echo "Running runtime callback guards..."
bash tools/ci/runtime_callback_guards.sh

echo "Running channel card literal guard..."
bash tools/ci/channel_card_literal_guard.sh

echo "Running docs lint guard..."
bash tools/docs/lint.sh

echo "Architecture guards passed."
