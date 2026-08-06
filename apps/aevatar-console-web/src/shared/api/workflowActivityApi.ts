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
  WorkflowActivityRunDetail,
  WorkflowActivityRunFilter,
  WorkflowActivityRunGraph,
  WorkflowActivityRunStatistics,
  WorkflowActivityRunSummary,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
  WorkflowActivityToolApproval,
  WorkflowActivityToolCall,
  WorkflowActivityUsageTotals,
  WorkflowRunForkAcceptedReceipt,
  WorkflowRunForkRequest,
} from '@/shared/models/workflowActivity';

const JSON_HEADERS = {
  Accept: 'application/json',
  'Content-Type': 'application/json',
};

export class WorkflowActivityApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'WorkflowActivityApiError';
    this.code = code;
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
