import { withQuery } from '@/shared/api/http/client';
import {
  expectArray,
  expectRecord,
  expectString,
  readBoolean,
  readNumber,
  readString,
  readStringRecord,
} from '@/shared/api/http/decoders';
import { readResponseErrorDetails } from '@/shared/api/http/error';
import { authFetch } from '@/shared/auth/fetch';
import type {
  WorkflowActivityDiagnostic,
  WorkflowActivityGraphEdge,
  WorkflowActivityGraphNode,
  WorkflowActivityRunCurrentStep,
  WorkflowActivityRunDetail,
  WorkflowActivityRunFeedFilter,
  WorkflowActivityRunFeedPage,
  WorkflowActivityRunFeedRow,
  WorkflowActivityRunFilter,
  WorkflowActivityRunFirstFailure,
  WorkflowActivityRunGraph,
  WorkflowActivityRunInitiator,
  WorkflowActivityRunStatistics,
  WorkflowActivityRunSummary,
  WorkflowActivityRunWaiting,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
  WorkflowActivityToolApproval,
  WorkflowActivityToolCall,
  WorkflowActivityUsageTotals,
  WorkflowRecoveryActionCapability,
  WorkflowRecoveryEligibility,
  WorkflowRecoveryRecommendedAction,
  WorkflowRecoveryUnavailableReasonCode,
  WorkflowRunForkAcceptedReceipt,
  WorkflowRunForkRequest,
  WorkflowRunLineage,
  WorkflowRunLineageAvailability,
  WorkflowRunLineageRelationKind,
  WorkflowRunLineageRunRef,
  WorkflowRunRecoveryCapability,
  WorkflowRunRetryForkLineage,
  WorkflowRunSubWorkflowLineage,
} from '@/shared/models/workflowActivity';

const JSON_HEADERS = {
  Accept: 'application/json',
  'Content-Type': 'application/json',
};

export class WorkflowActivityApiError extends Error {
  readonly code?: string;
  readonly correlationId?: string;
  readonly retryAfterSeconds?: number;
  readonly status: number;

  constructor(
    message: string,
    status: number,
    code?: string,
    guidance?: {
      readonly correlationId?: string;
      readonly retryAfterSeconds?: number;
    },
  ) {
    super(message);
    this.name = 'WorkflowActivityApiError';
    this.code = code;
    this.correlationId = guidance?.correlationId;
    this.retryAfterSeconds = guidance?.retryAfterSeconds;
    this.status = status;
  }
}

function requireNonBlank(value: string, label: string): string {
  if (!value.trim()) {
    throw new Error(`${label} must not be blank.`);
  }
  return value;
}

function readNonBlank(
  record: Record<string, unknown>,
  key: string,
  label: string,
): string {
  return requireNonBlank(readString(record, key, label), label);
}

function readNullableStringValue(value: unknown, label: string): string | null {
  return value === null || value === undefined
    ? null
    : expectString(value, label);
}

function readNullableBooleanValue(
  value: unknown,
  label: string,
): boolean | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== 'boolean')
    throw new Error(`${label} must be a boolean.`);
  return value;
}

function readNullableNumberValue(value: unknown, label: string): number | null {
  if (value === null || value === undefined) return null;
  if (typeof value !== 'number' || Number.isNaN(value)) {
    throw new Error(`${label} must be a number.`);
  }
  return value;
}

function decodeNumericEnum<T extends number>(
  value: unknown,
  label: string,
  allowed: readonly number[],
): T {
  if (
    typeof value !== 'number' ||
    !Number.isInteger(value) ||
    !allowed.includes(value)
  ) {
    throw new Error(`${label} must be a supported numeric enum value.`);
  }
  return value as T;
}

function decodeRecoveryActionCapability(
  value: unknown,
  label = 'WorkflowRecoveryActionCapability',
): WorkflowRecoveryActionCapability {
  const record = expectRecord(value, label);
  return {
    eligibility: decodeNumericEnum<WorkflowRecoveryEligibility>(
      record.eligibility,
      `${label}.eligibility`,
      [0, 1, 2, 3],
    ),
    unavailableReasonCode:
      decodeNumericEnum<WorkflowRecoveryUnavailableReasonCode>(
        record.unavailableReasonCode,
        `${label}.unavailableReasonCode`,
        [0, 1, 2, 3, 4, 5, 6, 7],
      ),
    unavailableReason: readString(
      record,
      'unavailableReason',
      `${label}.unavailableReason`,
    ),
    recommendedActions: expectArray(
      record.recommendedActions,
      `${label}.recommendedActions`,
      (entry, entryLabel = `${label}.recommendedActions[]`) =>
        decodeNumericEnum<WorkflowRecoveryRecommendedAction>(
          entry,
          entryLabel,
          [0, 1, 2, 3, 4, 5, 6, 7],
        ),
    ),
    startingStepId: readString(
      record,
      'startingStepId',
      `${label}.startingStepId`,
    ),
    reusesPriorStepOutputs: readBoolean(
      record,
      'reusesPriorStepOutputs',
      `${label}.reusesPriorStepOutputs`,
    ),
    mayIncurModelOrToolCost: readBoolean(
      record,
      'mayIncurModelOrToolCost',
      `${label}.mayIncurModelOrToolCost`,
    ),
  };
}

function decodeRecoveryCapability(
  value: unknown,
  label = 'WorkflowRunRecoveryCapability',
): WorkflowRunRecoveryCapability {
  const record = expectRecord(value, label);
  return {
    retryFailedStep: decodeRecoveryActionCapability(
      record.retryFailedStep,
      `${label}.retryFailedStep`,
    ),
    runAgain: decodeRecoveryActionCapability(
      record.runAgain,
      `${label}.runAgain`,
    ),
    workflowDefinitionRevisionId: readString(
      record,
      'workflowDefinitionRevisionId',
      `${label}.workflowDefinitionRevisionId`,
    ),
    workflowDefinitionVersion: readNumber(
      record,
      'workflowDefinitionVersion',
      `${label}.workflowDefinitionVersion`,
    ),
  };
}

function decodeLineageRunRef(
  value: unknown,
  label = 'WorkflowRunLineageRunRef',
): WorkflowRunLineageRunRef {
  const record = expectRecord(value, label);
  return {
    runId: readString(record, 'runId', `${label}.runId`),
    actorId: readString(record, 'actorId', `${label}.actorId`),
    relationshipId: readString(
      record,
      'relationshipId',
      `${label}.relationshipId`,
    ),
    stepId: readString(record, 'stepId', `${label}.stepId`),
    attempt: readNumber(record, 'attempt', `${label}.attempt`),
    relationKind: decodeNumericEnum<WorkflowRunLineageRelationKind>(
      record.relationKind,
      `${label}.relationKind`,
      [0, 1, 2],
    ),
  };
}

function decodeRetryForkLineage(
  value: unknown,
  label = 'WorkflowRunRetryForkLineage',
): WorkflowRunRetryForkLineage {
  const record = expectRecord(value, label);
  return {
    availability: decodeNumericEnum<WorkflowRunLineageAvailability>(
      record.availability,
      `${label}.availability`,
      [0, 1, 2, 3],
    ),
    sourceRunId: readString(record, 'sourceRunId', `${label}.sourceRunId`),
    originalRunId: readString(
      record,
      'originalRunId',
      `${label}.originalRunId`,
    ),
    attempt: readNumber(record, 'attempt', `${label}.attempt`),
    startAtStepId: readString(
      record,
      'startAtStepId',
      `${label}.startAtStepId`,
    ),
    childRuns: expectArray(
      record.childRuns,
      `${label}.childRuns`,
      decodeLineageRunRef,
    ),
  };
}

function decodeSubWorkflowLineage(
  value: unknown,
  label = 'WorkflowRunSubWorkflowLineage',
): WorkflowRunSubWorkflowLineage {
  const record = expectRecord(value, label);
  return {
    availability: decodeNumericEnum<WorkflowRunLineageAvailability>(
      record.availability,
      `${label}.availability`,
      [0, 1, 2, 3],
    ),
    parentRunId: readString(record, 'parentRunId', `${label}.parentRunId`),
    parentActorId: readString(
      record,
      'parentActorId',
      `${label}.parentActorId`,
    ),
    parentStepId: readString(record, 'parentStepId', `${label}.parentStepId`),
    rootRunId: readString(record, 'rootRunId', `${label}.rootRunId`),
    depth: readNumber(record, 'depth', `${label}.depth`),
    childRuns: expectArray(
      record.childRuns,
      `${label}.childRuns`,
      decodeLineageRunRef,
    ),
  };
}

function decodeLineage(
  value: unknown,
  label = 'WorkflowRunLineage',
): WorkflowRunLineage {
  const record = expectRecord(value, label);
  return {
    availability: decodeNumericEnum<WorkflowRunLineageAvailability>(
      record.availability,
      `${label}.availability`,
      [0, 1, 2, 3],
    ),
    retryFork: decodeRetryForkLineage(record.retryFork, `${label}.retryFork`),
    subWorkflow: decodeSubWorkflowLineage(
      record.subWorkflow,
      `${label}.subWorkflow`,
    ),
    unavailableReason: readString(
      record,
      'unavailableReason',
      `${label}.unavailableReason`,
    ),
  };
}

function decodeActivityInitiator(
  value: unknown,
  label = 'WorkflowActivityRunInitiator',
): WorkflowActivityRunInitiator {
  const record = expectRecord(value, label);
  return {
    platform: readString(record, 'platform', `${label}.platform`),
    tenant: readString(record, 'tenant', `${label}.tenant`),
    externalUserId: readString(
      record,
      'externalUserId',
      `${label}.externalUserId`,
    ),
    scope: readString(record, 'scope', `${label}.scope`),
    bindingId: readString(record, 'bindingId', `${label}.bindingId`),
    displayValue: readString(record, 'displayValue', `${label}.displayValue`),
    availability: readString(record, 'availability', `${label}.availability`),
  };
}

function decodeActivityCurrentStep(
  value: unknown,
  label = 'WorkflowActivityRunCurrentStep',
): WorkflowActivityRunCurrentStep {
  const record = expectRecord(value, label);
  return {
    stepId: readString(record, 'stepId', `${label}.stepId`),
    inputSummary: readString(record, 'inputSummary', `${label}.inputSummary`),
    availability: readString(record, 'availability', `${label}.availability`),
  };
}

function decodeActivityFirstFailure(
  value: unknown,
  label = 'WorkflowActivityRunFirstFailure',
): WorkflowActivityRunFirstFailure {
  const record = expectRecord(value, label);
  return {
    stepId: readString(record, 'stepId', `${label}.stepId`),
    message: readString(record, 'message', `${label}.message`),
    availability: readString(record, 'availability', `${label}.availability`),
  };
}

function decodeActivityWaiting(
  value: unknown,
  label = 'WorkflowActivityRunWaiting',
): WorkflowActivityRunWaiting {
  const record = expectRecord(value, label);
  return {
    stepId: readString(record, 'stepId', `${label}.stepId`),
    waitingKind: readString(record, 'waitingKind', `${label}.waitingKind`),
    prompt: readString(record, 'prompt', `${label}.prompt`),
    availability: readString(record, 'availability', `${label}.availability`),
  };
}

function decodeActivityRunFeedRow(
  value: unknown,
  label = 'WorkflowActivityRunFeedRow',
): WorkflowActivityRunFeedRow {
  const record = expectRecord(value, label);
  return {
    runId: readNonBlank(record, 'runId', `${label}.runId`),
    actorId: readNonBlank(record, 'actorId', `${label}.actorId`),
    workflowId: readString(record, 'workflowId', `${label}.workflowId`),
    workflowName: readString(record, 'workflowName', `${label}.workflowName`),
    scopeId: readNonBlank(record, 'scopeId', `${label}.scopeId`),
    status: readString(record, 'status', `${label}.status`),
    runOrigin: readString(record, 'runOrigin', `${label}.runOrigin`),
    success: readNullableBooleanValue(record.success, `${label}.success`),
    initiator: decodeActivityInitiator(record.initiator, `${label}.initiator`),
    inputSummary: readString(record, 'inputSummary', `${label}.inputSummary`),
    currentStep: decodeActivityCurrentStep(
      record.currentStep,
      `${label}.currentStep`,
    ),
    firstFailure: decodeActivityFirstFailure(
      record.firstFailure,
      `${label}.firstFailure`,
    ),
    waiting: decodeActivityWaiting(record.waiting, `${label}.waiting`),
    startedAtUtc: readNullableStringValue(
      record.startedAtUtc,
      `${label}.startedAtUtc`,
    ),
    completedAtUtc: readNullableStringValue(
      record.completedAtUtc,
      `${label}.completedAtUtc`,
    ),
    updatedAtUtc: readNonBlank(record, 'updatedAtUtc', `${label}.updatedAtUtc`),
    durationMs: readNullableNumberValue(
      record.durationMs,
      `${label}.durationMs`,
    ),
    stateVersion: readNumber(record, 'stateVersion', `${label}.stateVersion`),
    recoveryCapability: decodeRecoveryCapability(
      record.recoveryCapability,
      `${label}.recoveryCapability`,
    ),
    lineage: decodeLineage(record.lineage, `${label}.lineage`),
  };
}

function decodeActivityRunFeedPage(
  value: unknown,
): WorkflowActivityRunFeedPage {
  const label = 'WorkflowActivityRunFeedPage';
  const record = expectRecord(value, label);
  return {
    items: expectArray(
      record.items,
      `${label}.items`,
      decodeActivityRunFeedRow,
    ),
    nextCursor: readNullableStringValue(
      record.nextCursor,
      `${label}.nextCursor`,
    ),
    hasMore: readBoolean(record, 'hasMore', `${label}.hasMore`),
    totalCount: readNullableNumberValue(
      record.totalCount,
      `${label}.totalCount`,
    ),
  };
}

function decodeUsage(
  value: unknown,
  label: string,
): WorkflowActivityUsageTotals {
  const record = expectRecord(value, label);
  return {
    promptTokens: readNumber(record, 'promptTokens', `${label}.promptTokens`),
    completionTokens: readNumber(
      record,
      'completionTokens',
      `${label}.completionTokens`,
    ),
    totalTokens: readNumber(record, 'totalTokens', `${label}.totalTokens`),
    cost: readNumber(record, 'cost', `${label}.cost`),
  };
}

function decodeSummary(
  value: unknown,
  label = 'WorkflowActivityRunSummary',
): WorkflowActivityRunSummary {
  const record = expectRecord(value, label);
  return {
    runId: readNonBlank(record, 'runId', `${label}.runId`),
    workflowName: readString(record, 'workflowName', `${label}.workflowName`),
    status: readString(record, 'status', `${label}.status`),
    success: readNullableBooleanValue(record.success, `${label}.success`),
    startedAtUtc: readNullableStringValue(
      record.startedAtUtc,
      `${label}.startedAtUtc`,
    ),
    updatedAtUtc: readNonBlank(record, 'updatedAtUtc', `${label}.updatedAtUtc`),
    stateVersion: readNumber(record, 'stateVersion', `${label}.stateVersion`),
    scopeId: readNonBlank(record, 'scopeId', `${label}.scopeId`),
    runOrigin: readString(record, 'runOrigin', `${label}.runOrigin`),
  };
}

function decodeDiagnostic(
  value: unknown,
  label = 'WorkflowActivityDiagnostic',
): WorkflowActivityDiagnostic {
  const record = expectRecord(value, label);
  return {
    timestampUtc: readNullableStringValue(
      record.timestampUtc,
      `${label}.timestampUtc`,
    ),
    severity: readString(record, 'severity', `${label}.severity`),
    code: readString(record, 'code', `${label}.code`),
    source: readString(record, 'source', `${label}.source`),
    message: readString(record, 'message', `${label}.message`),
    hint: readString(record, 'hint', `${label}.hint`),
    stepId: readString(record, 'stepId', `${label}.stepId`),
    stepType: readString(record, 'stepType', `${label}.stepType`),
    targetRole: readString(record, 'targetRole', `${label}.targetRole`),
  };
}

function decodeToolApproval(
  value: unknown,
  label: string,
): WorkflowActivityToolApproval {
  const record = expectRecord(value, label);
  return {
    executionId: readString(record, 'executionId', `${label}.executionId`),
    toolName: readString(record, 'toolName', `${label}.toolName`),
    toolCallId: readString(record, 'toolCallId', `${label}.toolCallId`),
    approvalRequestId: readString(
      record,
      'approvalRequestId',
      `${label}.approvalRequestId`,
    ),
  };
}

function decodeStep(
  value: unknown,
  label = 'WorkflowActivityStep',
): WorkflowActivityStep {
  const record = expectRecord(value, label);
  return {
    stepId: readNonBlank(record, 'stepId', `${label}.stepId`),
    stepType: readString(record, 'stepType', `${label}.stepType`),
    targetRole: readString(record, 'targetRole', `${label}.targetRole`),
    requestedAtUtc: readNullableStringValue(
      record.requestedAtUtc,
      `${label}.requestedAtUtc`,
    ),
    completedAtUtc: readNullableStringValue(
      record.completedAtUtc,
      `${label}.completedAtUtc`,
    ),
    success: readNullableBooleanValue(record.success, `${label}.success`),
    durationMs: readNullableNumberValue(
      record.durationMs,
      `${label}.durationMs`,
    ),
    outputPreview: readString(
      record,
      'outputPreview',
      `${label}.outputPreview`,
    ),
    error: readString(record, 'error', `${label}.error`),
    requestParameters: readStringRecord(
      record,
      'requestParameters',
      `${label}.requestParameters`,
    ),
    nextStepId: readString(record, 'nextStepId', `${label}.nextStepId`),
    branchKey: readString(record, 'branchKey', `${label}.branchKey`),
    suspensionType: readString(
      record,
      'suspensionType',
      `${label}.suspensionType`,
    ),
    suspensionPrompt: readString(
      record,
      'suspensionPrompt',
      `${label}.suspensionPrompt`,
    ),
    suspensionContent: readString(
      record,
      'suspensionContent',
      `${label}.suspensionContent`,
    ),
    suspensionTimeoutSeconds: readNullableNumberValue(
      record.suspensionTimeoutSeconds,
      `${label}.suspensionTimeoutSeconds`,
    ),
    toolApproval:
      record.toolApproval === null || record.toolApproval === undefined
        ? null
        : decodeToolApproval(record.toolApproval, `${label}.toolApproval`),
    usage: decodeUsage(record.usage, `${label}.usage`),
  };
}

function decodeToolCall(
  value: unknown,
  label: string,
): WorkflowActivityToolCall {
  const record = expectRecord(value, label);
  return {
    toolName: readString(record, 'toolName', `${label}.toolName`),
    callId: readString(record, 'callId', `${label}.callId`),
    argumentsJson: readString(
      record,
      'argumentsJson',
      `${label}.argumentsJson`,
    ),
    resultJson: readString(record, 'resultJson', `${label}.resultJson`),
    success: readBoolean(record, 'success', `${label}.success`),
    error: readString(record, 'error', `${label}.error`),
  };
}

function decodeTimelineEvent(
  value: unknown,
  label = 'WorkflowActivityTimelineEvent',
): WorkflowActivityTimelineEvent {
  const record = expectRecord(value, label);
  return {
    kind: readString(record, 'kind', `${label}.kind`),
    timestampUtc: readNonBlank(record, 'timestampUtc', `${label}.timestampUtc`),
    stage: readString(record, 'stage', `${label}.stage`),
    message: readString(record, 'message', `${label}.message`),
    agentId: readString(record, 'agentId', `${label}.agentId`),
    stepId: readString(record, 'stepId', `${label}.stepId`),
    stepType: readString(record, 'stepType', `${label}.stepType`),
    toolCall:
      record.toolCall === null || record.toolCall === undefined
        ? null
        : decodeToolCall(record.toolCall, `${label}.toolCall`),
    content: readString(record, 'content', `${label}.content`),
    data: readStringRecord(record, 'data', `${label}.data`),
  };
}

function decodeStatistics(
  value: unknown,
  label: string,
): WorkflowActivityRunStatistics {
  const record = expectRecord(value, label);
  const counts = expectRecord(record.stepTypeCounts, `${label}.stepTypeCounts`);
  return {
    totalSteps: readNumber(record, 'totalSteps', `${label}.totalSteps`),
    requestedSteps: readNumber(
      record,
      'requestedSteps',
      `${label}.requestedSteps`,
    ),
    completedSteps: readNumber(
      record,
      'completedSteps',
      `${label}.completedSteps`,
    ),
    roleReplyCount: readNumber(
      record,
      'roleReplyCount',
      `${label}.roleReplyCount`,
    ),
    stepTypeCounts: Object.fromEntries(
      Object.entries(counts).map(([key, entry]) => {
        if (typeof entry !== 'number' || Number.isNaN(entry)) {
          throw new Error(`${label}.stepTypeCounts.${key} must be a number.`);
        }
        return [key, entry];
      }),
    ),
  };
}

function decodeDetail(value: unknown): WorkflowActivityRunDetail {
  const record = expectRecord(value, 'WorkflowActivityRunDetail');
  return {
    summary: decodeSummary(record.summary, 'WorkflowActivityRunDetail.summary'),
    input: readString(record, 'input', 'WorkflowActivityRunDetail.input'),
    finalOutput: readString(
      record,
      'finalOutput',
      'WorkflowActivityRunDetail.finalOutput',
    ),
    finalError: readString(
      record,
      'finalError',
      'WorkflowActivityRunDetail.finalError',
    ),
    diagnostics: expectArray(
      record.diagnostics,
      'WorkflowActivityRunDetail.diagnostics',
      decodeDiagnostic,
    ),
    steps: expectArray(
      record.steps,
      'WorkflowActivityRunDetail.steps',
      decodeStep,
    ),
    timeline: expectArray(
      record.timeline,
      'WorkflowActivityRunDetail.timeline',
      decodeTimelineEvent,
    ),
    statistics: decodeStatistics(
      record.statistics,
      'WorkflowActivityRunDetail.statistics',
    ),
    usageTotals: decodeUsage(
      record.usageTotals,
      'WorkflowActivityRunDetail.usageTotals',
    ),
    recoveryCapability: decodeRecoveryCapability(
      record.recoveryCapability,
      'WorkflowActivityRunDetail.recoveryCapability',
    ),
    lineage: decodeLineage(record.lineage, 'WorkflowActivityRunDetail.lineage'),
  };
}

function decodeGraphNode(
  value: unknown,
  label = 'WorkflowActivityGraphNode',
): WorkflowActivityGraphNode {
  const record = expectRecord(value, label);
  return {
    nodeId: readNonBlank(record, 'nodeId', `${label}.nodeId`),
    nodeType: readString(record, 'nodeType', `${label}.nodeType`),
    stepId: readString(record, 'stepId', `${label}.stepId`),
  };
}

function decodeGraphEdge(
  value: unknown,
  label = 'WorkflowActivityGraphEdge',
): WorkflowActivityGraphEdge {
  const record = expectRecord(value, label);
  return {
    edgeId: readNonBlank(record, 'edgeId', `${label}.edgeId`),
    fromNodeId: readNonBlank(record, 'fromNodeId', `${label}.fromNodeId`),
    toNodeId: readNonBlank(record, 'toNodeId', `${label}.toNodeId`),
    edgeType: readString(record, 'edgeType', `${label}.edgeType`),
    branchKey: readString(record, 'branchKey', `${label}.branchKey`),
  };
}

function decodeGraph(value: unknown): WorkflowActivityRunGraph {
  const record = expectRecord(value, 'WorkflowActivityRunGraph');
  return {
    rootNodeId: readString(
      record,
      'rootNodeId',
      'WorkflowActivityRunGraph.rootNodeId',
    ),
    nodes: expectArray(
      record.nodes,
      'WorkflowActivityRunGraph.nodes',
      decodeGraphNode,
    ),
    edges: expectArray(
      record.edges,
      'WorkflowActivityRunGraph.edges',
      decodeGraphEdge,
    ),
  };
}

function decodeForkReceipt(value: unknown): WorkflowRunForkAcceptedReceipt {
  const record = expectRecord(value, 'WorkflowRunForkAcceptedReceipt');
  if (
    readBoolean(
      record,
      'accepted',
      'WorkflowRunForkAcceptedReceipt.accepted',
    ) !== true
  ) {
    throw new Error('WorkflowRunForkAcceptedReceipt.accepted must be true.');
  }
  return {
    accepted: true,
    sourceRunId: readNonBlank(
      record,
      'sourceRunId',
      'WorkflowRunForkAcceptedReceipt.sourceRunId',
    ),
    newRunId: readNonBlank(
      record,
      'newRunId',
      'WorkflowRunForkAcceptedReceipt.newRunId',
    ),
    newRunActorId: readNonBlank(
      record,
      'newRunActorId',
      'WorkflowRunForkAcceptedReceipt.newRunActorId',
    ),
    workflowName: readString(
      record,
      'workflowName',
      'WorkflowRunForkAcceptedReceipt.workflowName',
    ),
    acceptedCommandId: readNonBlank(
      record,
      'acceptedCommandId',
      'WorkflowRunForkAcceptedReceipt.acceptedCommandId',
    ),
    correlationId: readNonBlank(
      record,
      'correlationId',
      'WorkflowRunForkAcceptedReceipt.correlationId',
    ),
    statusUrl: readNonBlank(
      record,
      'statusUrl',
      'WorkflowRunForkAcceptedReceipt.statusUrl',
    ),
  };
}

async function requestActivityJson<T>(
  input: string,
  decode: (value: unknown) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new WorkflowActivityApiError(
      details.message,
      details.status,
      details.code,
      {
        correlationId: details.correlationId,
        retryAfterSeconds: details.retryAfterSeconds,
      },
    );
  }
  return decode(await response.json());
}

function joinFilter(values?: readonly string[]): string | undefined {
  const normalized = values?.map((value) => value.trim()).filter(Boolean) ?? [];
  return normalized.length > 0 ? normalized.join(',') : undefined;
}

function compactForkRequest(
  input: WorkflowRunForkRequest,
): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries({
      sourceRunId: input.sourceRunId.trim(),
      startAtStepId: input.startAtStepId.trim(),
      inlineYaml: input.inlineYaml,
      inlineSubYamls: input.inlineSubYamls,
      variableOverrides: input.variableOverrides,
      input: input.input,
      commandId: input.commandId?.trim() || undefined,
      correlationId: input.correlationId?.trim() || undefined,
    }).filter(([, value]) => value !== undefined),
  );
}

export const workflowActivityApi = {
  listRuns(
    scopeId: string,
    filter: WorkflowActivityRunFilter = {},
  ): Promise<WorkflowActivityRunSummary[]> {
    const url = withQuery('/api/workflow/observatory/runs', {
      scope: scopeId.trim(),
      status: filter.status?.trim(),
      origin: joinFilter(filter.origins),
      definition: joinFilter(filter.definitionActorIds),
      schedule: joinFilter(filter.scheduleIds),
      from: filter.fromUtc?.trim(),
      to: filter.toUtc?.trim(),
      take: filter.take,
    });
    return requestActivityJson(url, (value) =>
      expectArray(value, 'WorkflowActivityRunSummary[]', decodeSummary),
    );
  },

  listActivityRuns(
    scopeId: string,
    filter: WorkflowActivityRunFeedFilter = {},
  ): Promise<WorkflowActivityRunFeedPage> {
    const url = withQuery('/api/workflow/observatory/activity-runs', {
      scope: scopeId.trim(),
      status: filter.status?.trim(),
      origin: joinFilter(filter.origins),
      definition: joinFilter(filter.definitionActorIds),
      schedule: joinFilter(filter.scheduleIds),
      workflowId: filter.workflowId?.trim(),
      q: filter.searchText?.trim(),
      from: filter.fromUtc?.trim(),
      to: filter.toUtc?.trim(),
      take: filter.take,
      cursor: filter.cursor?.trim(),
      includeTotalCount: filter.includeTotalCount,
    });
    return requestActivityJson(url, decodeActivityRunFeedPage);
  },

  getRun(scopeId: string, runId: string): Promise<WorkflowActivityRunDetail> {
    return requestActivityJson(
      withQuery(
        `/api/workflow/observatory/runs/${encodeURIComponent(runId.trim())}`,
        { scope: scopeId.trim() },
      ),
      decodeDetail,
    );
  },

  getRunGraph(
    scopeId: string,
    runId: string,
  ): Promise<WorkflowActivityRunGraph> {
    return requestActivityJson(
      withQuery(
        `/api/workflow/observatory/runs/${encodeURIComponent(runId.trim())}/graph`,
        { scope: scopeId.trim() },
      ),
      decodeGraph,
    );
  },

  forkRun(
    input: WorkflowRunForkRequest,
  ): Promise<WorkflowRunForkAcceptedReceipt> {
    return requestActivityJson('/api/workflow/runs/fork', decodeForkReceipt, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(compactForkRequest(input)),
    });
  },
};
