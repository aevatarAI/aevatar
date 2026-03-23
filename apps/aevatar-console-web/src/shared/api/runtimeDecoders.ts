import type {
  WorkflowResumeResponse,
  WorkflowSignalResponse,
} from "@aevatar-react-sdk/types";
import type {
  PlaygroundWorkflowParseResult,
  PlaygroundWorkflowSaveResult,
  WorkflowAuthoringDefinition,
  WorkflowAuthoringEdge,
  WorkflowAuthoringErrorPolicy,
  WorkflowAuthoringRetryPolicy,
  WorkflowAuthoringRole,
  WorkflowAuthoringStep,
} from "@/shared/models/runtime/authoring";
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";
import type {
  WorkflowCatalogChildStep,
  WorkflowCatalogDefinition,
  WorkflowCatalogEdge,
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
  WorkflowCatalogRole,
  WorkflowCatalogStep,
} from "@/shared/models/runtime/catalog";
import type {
  WorkflowAgentSummary,
  WorkflowCapabilities,
  WorkflowCapabilityParameter,
  WorkflowCapabilityWorkflow,
  WorkflowCapabilityWorkflowStep,
  WorkflowConnectorCapability,
  WorkflowLlmStatus,
  WorkflowPrimitiveCapability,
  WorkflowPrimitiveDescriptor,
  WorkflowPrimitiveParameterDescriptor,
} from "@/shared/models/runtime/query";
import {
  type Decoder,
  expectArray,
  expectBoolean,
  expectNullableBoolean,
  expectNullableNumber,
  expectNullableString,
  expectNumber,
  expectOptionalString,
  expectRecord,
  expectString,
  expectStringArray,
  expectStringRecord,
} from "./decodeUtils";

function decodeWorkflowCatalogItem(
  value: unknown,
  label = "WorkflowCatalogItem"
): WorkflowCatalogItem {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    category: expectString(record.category, `${label}.category`),
    group: expectString(record.group, `${label}.group`),
    groupLabel: expectString(record.groupLabel, `${label}.groupLabel`),
    sortOrder: expectNumber(record.sortOrder, `${label}.sortOrder`),
    source: expectString(record.source, `${label}.source`),
    sourceLabel: expectString(record.sourceLabel, `${label}.sourceLabel`),
    showInLibrary: expectBoolean(
      record.showInLibrary,
      `${label}.showInLibrary`
    ),
    isPrimitiveExample: expectBoolean(
      record.isPrimitiveExample,
      `${label}.isPrimitiveExample`
    ),
    requiresLlmProvider: expectBoolean(
      record.requiresLlmProvider,
      `${label}.requiresLlmProvider`
    ),
    primitives: expectStringArray(record.primitives, `${label}.primitives`),
  };
}

function decodeWorkflowCatalogRole(
  value: unknown,
  label = "WorkflowCatalogRole"
): WorkflowCatalogRole {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    name: expectString(record.name, `${label}.name`),
    systemPrompt: expectString(record.systemPrompt, `${label}.systemPrompt`),
    provider: expectString(record.provider, `${label}.provider`),
    model: expectString(record.model, `${label}.model`),
    temperature: expectNullableNumber(
      record.temperature,
      `${label}.temperature`
    ),
    maxTokens: expectNullableNumber(record.maxTokens, `${label}.maxTokens`),
    maxToolRounds: expectNullableNumber(
      record.maxToolRounds,
      `${label}.maxToolRounds`
    ),
    maxHistoryMessages: expectNullableNumber(
      record.maxHistoryMessages,
      `${label}.maxHistoryMessages`
    ),
    streamBufferCapacity: expectNullableNumber(
      record.streamBufferCapacity,
      `${label}.streamBufferCapacity`
    ),
    eventModules: expectStringArray(
      record.eventModules,
      `${label}.eventModules`
    ),
    eventRoutes: expectString(record.eventRoutes, `${label}.eventRoutes`),
    connectors: expectStringArray(record.connectors, `${label}.connectors`),
  };
}

function decodeWorkflowCatalogChildStep(
  value: unknown,
  label = "WorkflowCatalogChildStep"
): WorkflowCatalogChildStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
  };
}

function decodeWorkflowCatalogStep(
  value: unknown,
  label = "WorkflowCatalogStep"
): WorkflowCatalogStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
    parameters: expectStringRecord(record.parameters, `${label}.parameters`),
    next: expectString(record.next, `${label}.next`),
    branches: expectStringRecord(record.branches, `${label}.branches`),
    children: expectArray(
      record.children,
      `${label}.children`,
      decodeWorkflowCatalogChildStep
    ),
  };
}

function decodeWorkflowCatalogEdge(
  value: unknown,
  label = "WorkflowCatalogEdge"
): WorkflowCatalogEdge {
  const record = expectRecord(value, label);
  return {
    from: expectString(record.from, `${label}.from`),
    to: expectString(record.to, `${label}.to`),
    label: expectString(record.label, `${label}.label`),
  };
}

function decodeWorkflowCatalogDefinition(
  value: unknown,
  label = "WorkflowCatalogDefinition"
): WorkflowCatalogDefinition {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`
    ),
    roles: expectArray(
      record.roles,
      `${label}.roles`,
      decodeWorkflowCatalogRole
    ),
    steps: expectArray(
      record.steps,
      `${label}.steps`,
      decodeWorkflowCatalogStep
    ),
  };
}

function decodeWorkflowCatalogItemDetail(
  value: unknown,
  label = "WorkflowCatalogItemDetail"
): WorkflowCatalogItemDetail {
  const record = expectRecord(value, label);
  return {
    catalog: decodeWorkflowCatalogItem(record.catalog, `${label}.catalog`),
    yaml: expectString(record.yaml, `${label}.yaml`),
    definition: decodeWorkflowCatalogDefinition(
      record.definition,
      `${label}.definition`
    ),
    edges: expectArray(
      record.edges,
      `${label}.edges`,
      decodeWorkflowCatalogEdge
    ),
  };
}

function decodeWorkflowAgentSummary(
  value: unknown,
  label = "WorkflowAgentSummary"
): WorkflowAgentSummary {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    description: expectString(record.description, `${label}.description`),
  };
}

function decodeWorkflowCapabilityParameter(
  value: unknown,
  label = "WorkflowCapabilityParameter"
): WorkflowCapabilityParameter {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    required: expectBoolean(record.required, `${label}.required`),
    description: expectString(record.description, `${label}.description`),
    default: expectString(record.default, `${label}.default`),
    enum: expectStringArray(record.enum, `${label}.enum`),
  };
}

function decodeWorkflowPrimitiveCapability(
  value: unknown,
  label = "WorkflowPrimitiveCapability"
): WorkflowPrimitiveCapability {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    aliases: expectStringArray(record.aliases, `${label}.aliases`),
    category: expectString(record.category, `${label}.category`),
    description: expectString(record.description, `${label}.description`),
    closedWorldBlocked: expectBoolean(
      record.closedWorldBlocked,
      `${label}.closedWorldBlocked`
    ),
    runtimeModule: expectString(record.runtimeModule, `${label}.runtimeModule`),
    parameters: expectArray(
      record.parameters,
      `${label}.parameters`,
      decodeWorkflowCapabilityParameter
    ),
  };
}

function decodeWorkflowConnectorCapability(
  value: unknown,
  label = "WorkflowConnectorCapability"
): WorkflowConnectorCapability {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    enabled: expectBoolean(record.enabled, `${label}.enabled`),
    timeoutMs: expectNumber(record.timeoutMs, `${label}.timeoutMs`),
    retry: expectNumber(record.retry, `${label}.retry`),
    allowedInputKeys: expectStringArray(
      record.allowedInputKeys,
      `${label}.allowedInputKeys`
    ),
    allowedOperations: expectStringArray(
      record.allowedOperations,
      `${label}.allowedOperations`
    ),
    fixedArguments: expectStringArray(
      record.fixedArguments,
      `${label}.fixedArguments`
    ),
  };
}

function decodeWorkflowCapabilityWorkflowStep(
  value: unknown,
  label = "WorkflowCapabilityWorkflowStep"
): WorkflowCapabilityWorkflowStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    next: expectString(record.next, `${label}.next`),
  };
}

function decodeWorkflowCapabilityWorkflow(
  value: unknown,
  label = "WorkflowCapabilityWorkflow"
): WorkflowCapabilityWorkflow {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    source: expectString(record.source, `${label}.source`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`
    ),
    requiresLlmProvider: expectBoolean(
      record.requiresLlmProvider,
      `${label}.requiresLlmProvider`
    ),
    primitives: expectStringArray(record.primitives, `${label}.primitives`),
    requiredConnectors: expectStringArray(
      record.requiredConnectors,
      `${label}.requiredConnectors`
    ),
    workflowCalls: expectStringArray(
      record.workflowCalls,
      `${label}.workflowCalls`
    ),
    steps: expectArray(
      record.steps,
      `${label}.steps`,
      decodeWorkflowCapabilityWorkflowStep
    ),
  };
}

function decodeWorkflowCapabilities(
  value: unknown,
  label = "WorkflowCapabilities"
): WorkflowCapabilities {
  const record = expectRecord(value, label);
  return {
    schemaVersion: expectString(record.schemaVersion, `${label}.schemaVersion`),
    generatedAtUtc: expectString(
      record.generatedAtUtc,
      `${label}.generatedAtUtc`
    ),
    primitives: expectArray(
      record.primitives,
      `${label}.primitives`,
      decodeWorkflowPrimitiveCapability
    ),
    connectors: expectArray(
      record.connectors,
      `${label}.connectors`,
      decodeWorkflowConnectorCapability
    ),
    workflows: expectArray(
      record.workflows,
      `${label}.workflows`,
      decodeWorkflowCapabilityWorkflow
    ),
  };
}

function decodeWorkflowAuthoringRetryPolicy(
  value: unknown,
  label = "WorkflowAuthoringRetryPolicy"
): WorkflowAuthoringRetryPolicy {
  const record = expectRecord(value, label);
  return {
    maxAttempts: expectNumber(record.maxAttempts, `${label}.maxAttempts`),
    backoff: expectString(record.backoff, `${label}.backoff`),
    delayMs: expectNumber(record.delayMs, `${label}.delayMs`),
  };
}

function decodeWorkflowAuthoringErrorPolicy(
  value: unknown,
  label = "WorkflowAuthoringErrorPolicy"
): WorkflowAuthoringErrorPolicy {
  const record = expectRecord(value, label);
  return {
    strategy: expectString(record.strategy, `${label}.strategy`),
    fallbackStep: expectNullableString(
      record.fallbackStep,
      `${label}.fallbackStep`
    ),
    defaultOutput: expectNullableString(
      record.defaultOutput,
      `${label}.defaultOutput`
    ),
  };
}

function decodeWorkflowAuthoringRole(
  value: unknown,
  label = "WorkflowAuthoringRole"
): WorkflowAuthoringRole {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    name: expectString(record.name, `${label}.name`),
    systemPrompt: expectString(record.systemPrompt, `${label}.systemPrompt`),
    provider: expectNullableString(record.provider, `${label}.provider`),
    model: expectNullableString(record.model, `${label}.model`),
    temperature: expectNullableNumber(
      record.temperature,
      `${label}.temperature`
    ),
    maxTokens: expectNullableNumber(record.maxTokens, `${label}.maxTokens`),
    maxToolRounds: expectNullableNumber(
      record.maxToolRounds,
      `${label}.maxToolRounds`
    ),
    maxHistoryMessages: expectNullableNumber(
      record.maxHistoryMessages,
      `${label}.maxHistoryMessages`
    ),
    streamBufferCapacity: expectNullableNumber(
      record.streamBufferCapacity,
      `${label}.streamBufferCapacity`
    ),
    eventModules: expectStringArray(
      record.eventModules,
      `${label}.eventModules`
    ),
    eventRoutes: expectString(record.eventRoutes, `${label}.eventRoutes`),
    connectors: expectStringArray(record.connectors, `${label}.connectors`),
  };
}

function decodeWorkflowAuthoringStep(
  value: unknown,
  label = "WorkflowAuthoringStep"
): WorkflowAuthoringStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
    parameters: expectStringRecord(record.parameters, `${label}.parameters`),
    next: expectNullableString(record.next, `${label}.next`),
    branches: expectStringRecord(record.branches, `${label}.branches`),
    children: expectArray(
      record.children,
      `${label}.children`,
      decodeWorkflowAuthoringStep
    ),
    retry:
      record.retry === null
        ? null
        : decodeWorkflowAuthoringRetryPolicy(record.retry, `${label}.retry`),
    onError:
      record.onError === null
        ? null
        : decodeWorkflowAuthoringErrorPolicy(
            record.onError,
            `${label}.onError`
          ),
    timeoutMs: expectNullableNumber(record.timeoutMs, `${label}.timeoutMs`),
  };
}

function decodeWorkflowAuthoringDefinition(
  value: unknown,
  label = "WorkflowAuthoringDefinition"
): WorkflowAuthoringDefinition {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`
    ),
    roles: expectArray(
      record.roles,
      `${label}.roles`,
      decodeWorkflowAuthoringRole
    ),
    steps: expectArray(
      record.steps,
      `${label}.steps`,
      decodeWorkflowAuthoringStep
    ),
  };
}

function decodeWorkflowAuthoringEdge(
  value: unknown,
  label = "WorkflowAuthoringEdge"
): WorkflowAuthoringEdge {
  const record = expectRecord(value, label);
  return {
    from: expectString(record.from, `${label}.from`),
    to: expectString(record.to, `${label}.to`),
    label: expectString(record.label, `${label}.label`),
  };
}

function decodePlaygroundWorkflowParseResult(
  value: unknown,
  label = "PlaygroundWorkflowParseResult"
): PlaygroundWorkflowParseResult {
  const record = expectRecord(value, label);
  return {
    valid: expectBoolean(record.valid, `${label}.valid`),
    error: expectNullableString(record.error, `${label}.error`),
    errors: expectStringArray(record.errors, `${label}.errors`),
    definition:
      record.definition === null
        ? null
        : decodeWorkflowAuthoringDefinition(
            record.definition,
            `${label}.definition`
          ),
    edges: expectArray(
      record.edges,
      `${label}.edges`,
      decodeWorkflowAuthoringEdge
    ),
  };
}

function decodePlaygroundWorkflowSaveResult(
  value: unknown,
  label = "PlaygroundWorkflowSaveResult"
): PlaygroundWorkflowSaveResult {
  const record = expectRecord(value, label);
  return {
    saved: expectBoolean(record.saved, `${label}.saved`),
    filename: expectString(record.filename, `${label}.filename`),
    savedPath: expectString(record.savedPath, `${label}.savedPath`),
    workflowName: expectString(record.workflowName, `${label}.workflowName`),
    overwritten: expectBoolean(record.overwritten, `${label}.overwritten`),
    savedSource: expectString(record.savedSource, `${label}.savedSource`),
    effectiveSource: expectString(
      record.effectiveSource,
      `${label}.effectiveSource`
    ),
    effectivePath: expectString(record.effectivePath, `${label}.effectivePath`),
  };
}

function decodeWorkflowPrimitiveParameterDescriptor(
  value: unknown,
  label = "WorkflowPrimitiveParameterDescriptor"
): WorkflowPrimitiveParameterDescriptor {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    required: expectBoolean(record.required, `${label}.required`),
    description: expectString(record.description, `${label}.description`),
    default: expectString(record.default, `${label}.default`),
    enumValues: expectStringArray(record.enumValues, `${label}.enumValues`),
  };
}

function decodeWorkflowPrimitiveDescriptor(
  value: unknown,
  label = "WorkflowPrimitiveDescriptor"
): WorkflowPrimitiveDescriptor {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    aliases: expectStringArray(record.aliases, `${label}.aliases`),
    category: expectString(record.category, `${label}.category`),
    description: expectString(record.description, `${label}.description`),
    parameters: expectArray(
      record.parameters,
      `${label}.parameters`,
      decodeWorkflowPrimitiveParameterDescriptor
    ),
    exampleWorkflows: expectStringArray(
      record.exampleWorkflows,
      `${label}.exampleWorkflows`
    ),
  };
}

function decodeWorkflowLlmStatus(
  value: unknown,
  label = "WorkflowLlmStatus"
): WorkflowLlmStatus {
  const record = expectRecord(value, label);
  return {
    available: expectBoolean(record.available, `${label}.available`),
    provider: expectNullableString(record.provider, `${label}.provider`),
    model: expectNullableString(record.model, `${label}.model`),
    providers: expectStringArray(record.providers, `${label}.providers`),
  };
}

function decodeWorkflowActorSnapshot(
  value: unknown,
  label = "WorkflowActorSnapshot"
): WorkflowActorSnapshot {
  const record = expectRecord(value, label);
  return {
    actorId: expectString(record.actorId, `${label}.actorId`),
    workflowName: expectString(record.workflowName, `${label}.workflowName`),
    lastCommandId: expectString(record.lastCommandId, `${label}.lastCommandId`),
    stateVersion: expectNumber(record.stateVersion, `${label}.stateVersion`),
    lastEventId: expectString(record.lastEventId, `${label}.lastEventId`),
    lastUpdatedAt: expectString(record.lastUpdatedAt, `${label}.lastUpdatedAt`),
    lastSuccess: expectNullableBoolean(
      record.lastSuccess,
      `${label}.lastSuccess`
    ),
    lastOutput: expectString(record.lastOutput, `${label}.lastOutput`),
    lastError: expectString(record.lastError, `${label}.lastError`),
    totalSteps: expectNumber(record.totalSteps, `${label}.totalSteps`),
    requestedSteps: expectNumber(
      record.requestedSteps,
      `${label}.requestedSteps`
    ),
    completedSteps: expectNumber(
      record.completedSteps,
      `${label}.completedSteps`
    ),
    roleReplyCount: expectNumber(
      record.roleReplyCount,
      `${label}.roleReplyCount`
    ),
  };
}

function decodeWorkflowActorTimelineItem(
  value: unknown,
  label = "WorkflowActorTimelineItem"
): WorkflowActorTimelineItem {
  const record = expectRecord(value, label);
  return {
    timestamp: expectString(record.timestamp, `${label}.timestamp`),
    stage: expectString(record.stage, `${label}.stage`),
    message: expectString(record.message, `${label}.message`),
    agentId: expectString(record.agentId, `${label}.agentId`),
    stepId: expectString(record.stepId, `${label}.stepId`),
    stepType: expectString(record.stepType, `${label}.stepType`),
    eventType: expectString(record.eventType, `${label}.eventType`),
    data: expectStringRecord(record.data, `${label}.data`),
  };
}

function decodeWorkflowActorGraphNode(
  value: unknown,
  label = "WorkflowActorGraphNode"
): WorkflowActorGraphNode {
  const record = expectRecord(value, label);
  return {
    nodeId: expectString(record.nodeId, `${label}.nodeId`),
    nodeType: expectString(record.nodeType, `${label}.nodeType`),
    updatedAt: expectString(record.updatedAt, `${label}.updatedAt`),
    properties: expectStringRecord(record.properties, `${label}.properties`),
  };
}

function decodeWorkflowActorGraphEdge(
  value: unknown,
  label = "WorkflowActorGraphEdge"
): WorkflowActorGraphEdge {
  const record = expectRecord(value, label);
  return {
    edgeId: expectString(record.edgeId, `${label}.edgeId`),
    fromNodeId: expectString(record.fromNodeId, `${label}.fromNodeId`),
    toNodeId: expectString(record.toNodeId, `${label}.toNodeId`),
    edgeType: expectString(record.edgeType, `${label}.edgeType`),
    updatedAt: expectString(record.updatedAt, `${label}.updatedAt`),
    properties: expectStringRecord(record.properties, `${label}.properties`),
  };
}

function decodeWorkflowActorGraphSubgraph(
  value: unknown,
  label = "WorkflowActorGraphSubgraph"
): WorkflowActorGraphSubgraph {
  const record = expectRecord(value, label);
  return {
    rootNodeId: expectString(record.rootNodeId, `${label}.rootNodeId`),
    nodes: expectArray(
      record.nodes,
      `${label}.nodes`,
      decodeWorkflowActorGraphNode
    ),
    edges: expectArray(
      record.edges,
      `${label}.edges`,
      decodeWorkflowActorGraphEdge
    ),
  };
}

function decodeWorkflowActorGraphEnrichedSnapshot(
  value: unknown,
  label = "WorkflowActorGraphEnrichedSnapshot"
): WorkflowActorGraphEnrichedSnapshot {
  const record = expectRecord(value, label);
  return {
    snapshot: decodeWorkflowActorSnapshot(record.snapshot, `${label}.snapshot`),
    subgraph: decodeWorkflowActorGraphSubgraph(
      record.subgraph,
      `${label}.subgraph`
    ),
  };
}

function decodeWorkflowResumeResponse(
  value: unknown,
  label = "WorkflowResumeResponse"
): WorkflowResumeResponse {
  const record = expectRecord(value, label);
  return {
    accepted: expectBoolean(record.accepted, `${label}.accepted`),
    actorId: expectOptionalString(record.actorId, `${label}.actorId`),
    runId: expectOptionalString(record.runId, `${label}.runId`),
    stepId: expectOptionalString(record.stepId, `${label}.stepId`),
    commandId: expectOptionalString(record.commandId, `${label}.commandId`),
  };
}

function decodeWorkflowSignalResponse(
  value: unknown,
  label = "WorkflowSignalResponse"
): WorkflowSignalResponse {
  const record = expectRecord(value, label);
  return {
    accepted: expectBoolean(record.accepted, `${label}.accepted`),
    actorId: expectOptionalString(record.actorId, `${label}.actorId`),
    runId: expectOptionalString(record.runId, `${label}.runId`),
    signalName: expectOptionalString(record.signalName, `${label}.signalName`),
    stepId: expectOptionalString(record.stepId, `${label}.stepId`),
    commandId: expectOptionalString(record.commandId, `${label}.commandId`),
  };
}

export const decodeWorkflowAgentSummaries: Decoder<WorkflowAgentSummary[]> = (
  value
) => expectArray(value, "WorkflowAgentSummary[]", decodeWorkflowAgentSummary);

export const decodeWorkflowNames: Decoder<string[]> = (value) =>
  expectStringArray(value, "WorkflowNames");

export const decodeWorkflowCatalogItems: Decoder<WorkflowCatalogItem[]> = (
  value
) => expectArray(value, "WorkflowCatalogItem[]", decodeWorkflowCatalogItem);

export const decodeWorkflowCapabilitiesResponse: Decoder<
  WorkflowCapabilities
> = (value) => decodeWorkflowCapabilities(value);

export const decodePlaygroundWorkflowParseResponse: Decoder<
  PlaygroundWorkflowParseResult
> = (value) => decodePlaygroundWorkflowParseResult(value);

export const decodePlaygroundWorkflowSaveResponse: Decoder<
  PlaygroundWorkflowSaveResult
> = (value) => decodePlaygroundWorkflowSaveResult(value);

export const decodeWorkflowPrimitiveDescriptorsResponse: Decoder<
  WorkflowPrimitiveDescriptor[]
> = (value) =>
  expectArray(
    value,
    "WorkflowPrimitiveDescriptor[]",
    decodeWorkflowPrimitiveDescriptor
  );

export const decodeWorkflowLlmStatusResponse: Decoder<WorkflowLlmStatus> = (
  value
) => decodeWorkflowLlmStatus(value);

export const decodeWorkflowCatalogItemDetailResponse: Decoder<
  WorkflowCatalogItemDetail
> = (value) => decodeWorkflowCatalogItemDetail(value);

export const decodeWorkflowActorSnapshotResponse: Decoder<
  WorkflowActorSnapshot
> = (value) => decodeWorkflowActorSnapshot(value);

export const decodeWorkflowActorTimelineResponse: Decoder<
  WorkflowActorTimelineItem[]
> = (value) =>
  expectArray(
    value,
    "WorkflowActorTimelineItem[]",
    decodeWorkflowActorTimelineItem
  );

export const decodeWorkflowActorGraphEnrichedResponse: Decoder<
  WorkflowActorGraphEnrichedSnapshot
> = (value) => decodeWorkflowActorGraphEnrichedSnapshot(value);

export const decodeWorkflowActorGraphEdgesResponse: Decoder<
  WorkflowActorGraphEdge[]
> = (value) =>
  expectArray(value, "WorkflowActorGraphEdge[]", decodeWorkflowActorGraphEdge);

export const decodeWorkflowActorGraphSubgraphResponse: Decoder<
  WorkflowActorGraphSubgraph
> = (value) => decodeWorkflowActorGraphSubgraph(value);

export const decodeWorkflowResumeResponseBody: Decoder<
  WorkflowResumeResponse
> = (value) => decodeWorkflowResumeResponse(value);

export const decodeWorkflowSignalResponseBody: Decoder<
  WorkflowSignalResponse
> = (value) => decodeWorkflowSignalResponse(value);
