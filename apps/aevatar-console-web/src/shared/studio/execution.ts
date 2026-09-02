import {
  MarkerType,
  type Edge,
  type Node,
} from '@xyflow/react';
import { AGUIEventType } from '@aevatar-react-sdk/types';
import type { RuntimeEvent } from "@/shared/agui/runtimeEventSemantics";
import {
  formatStudioStepTypeLabel,
  type StudioGraphEdgeData,
  type StudioGraphNodeData,
} from './graph';
import type {
  StudioExecutionDetail,
  StudioExecutionFrame,
  StudioWorkflowDocument,
} from './models';
import { t } from "@/shared/i18n/messages";

export type WorkflowExecutionNodeSnapshot = {
  readonly stepId: string;
  readonly stepType: string;
  readonly subtitle: string;
  readonly targetRole: string;
  readonly title: string;
};

export function buildWorkflowExecutionNodeSnapshots(
  document: StudioWorkflowDocument,
): WorkflowExecutionNodeSnapshot[] {
  if (!Array.isArray(document.steps)) {
    return [];
  }

  return document.steps.flatMap((step) => {
    const stepId = String(step?.id ?? '').trim();
    if (!stepId) {
      return [];
    }

    const stepType =
      String(step.type ?? '').trim() ||
      String(step.originalType ?? '').trim() ||
      'step';
    const targetRole = String(step.targetRole ?? step.target_role ?? '').trim();

    return [
      {
        stepId,
        stepType,
        subtitle: formatStudioStepTypeLabel(stepType),
        targetRole,
        title: stepId,
      },
    ];
  });
}

export type ExecutionLogItem = {
  readonly category?:
    | 'lifecycle'
    | 'step'
    | 'output'
    | 'usage'
    | 'snapshot'
    | 'raw'
    | 'custom';
  readonly tone: 'started' | 'completed' | 'failed' | 'run' | 'pending';
  readonly title: string;
  readonly meta: string;
  readonly previewText: string;
  readonly clipboardText: string;
  readonly timestamp: string;
  readonly stepId: string | null;
  readonly interaction: ExecutionInteractionState | null;
  readonly payloadText?: string;
  readonly rawText?: string;
  readonly eventType?: string;
};

export type StepExecutionState = {
  readonly stepId: string;
  status: 'idle' | 'active' | 'waiting' | 'completed' | 'failed';
  stepType: string;
  targetRole: string;
  startedAt: string | null;
  completedAt: string | null;
  success: boolean | null;
  error: string;
  nextStepId: string;
  branchKey: string;
};

export type ExecutionInteractionState = {
  readonly kind: 'human_input' | 'human_approval' | 'wait_signal';
  readonly runId: string;
  readonly stepId: string;
  readonly prompt: string;
  readonly timeoutSeconds: number | null;
  readonly variableName: string;
  readonly signalName: string;
};

export type ExecutionTrace = {
  readonly stepStates: Map<string, StepExecutionState>;
  readonly traversedEdges: Set<string>;
  readonly logs: ExecutionLogItem[];
  readonly latestStepId: string | null;
  readonly defaultLogIndex: number | null;
};

export type ExecutionLogStatus =
  | 'waiting'
  | 'running'
  | 'success'
  | 'error'
  | 'recorded';

function readRuntimeEventString(
  event: RuntimeEvent,
  ...keys: string[]
): string {
  const record = event as unknown as Record<string, unknown>;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.trim()) {
      return value;
    }
  }

  return '';
}

export function readRuntimeEventTimestamp(event: RuntimeEvent): number {
  const value = (event as unknown as { timestamp?: unknown }).timestamp;
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string') {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return Date.now();
}

export function formatRuntimeEventTimestamp(event: RuntimeEvent): string {
  const date = new Date(readRuntimeEventTimestamp(event));
  return Number.isFinite(date.getTime())
    ? date.toISOString()
    : new Date().toISOString();
}

export function serializeRuntimeEventFrame(event: RuntimeEvent): string {
  const record = event as unknown as Record<string, unknown>;
  const timestamp = readRuntimeEventTimestamp(event);

  switch (event.type) {
    case AGUIEventType.CUSTOM:
      return JSON.stringify({
        timestamp,
        custom: {
          name: readRuntimeEventString(event, 'name'),
          payload: record.payload ?? record.value,
        },
      });
    case AGUIEventType.RUN_STARTED:
      return JSON.stringify({
        timestamp,
        runStarted: {
          actorId: readRuntimeEventString(event, 'actorId', 'threadId'),
          commandId: readRuntimeEventString(event, 'commandId', 'command_id'),
          correlationId: readRuntimeEventString(
            event,
            'correlationId',
            'correlation_id',
          ),
          runId: readRuntimeEventString(event, 'runId'),
          threadId: readRuntimeEventString(event, 'threadId', 'actorId'),
        },
      });
    case AGUIEventType.RUN_FINISHED:
      return JSON.stringify({
        timestamp,
        runFinished: {
          commandId: readRuntimeEventString(event, 'commandId', 'command_id'),
          correlationId: readRuntimeEventString(
            event,
            'correlationId',
            'correlation_id',
          ),
          result: record.result,
          runId: readRuntimeEventString(event, 'runId'),
          threadId: readRuntimeEventString(event, 'threadId', 'actorId'),
        },
      });
    case AGUIEventType.RUN_ERROR:
      return JSON.stringify({
        timestamp,
        runError: {
          code: readRuntimeEventString(event, 'code', 'errorCode', 'error_code'),
          commandId: readRuntimeEventString(event, 'commandId', 'command_id'),
          correlationId: readRuntimeEventString(
            event,
            'correlationId',
            'correlation_id',
          ),
          message: readRuntimeEventString(event, 'message'),
          runId: readRuntimeEventString(event, 'runId'),
        },
      });
    case AGUIEventType.STEP_STARTED:
      return JSON.stringify({
        timestamp,
        stepStarted: {
          stepName: readRuntimeEventString(event, 'stepName'),
        },
      });
    case AGUIEventType.STEP_FINISHED:
      return JSON.stringify({
        timestamp,
        stepFinished: {
          stepName: readRuntimeEventString(event, 'stepName'),
        },
      });
    default:
      return JSON.stringify({
        ...record,
        timestamp,
      });
  }
}

export function createStudioExecutionFrame(
  event: RuntimeEvent,
): StudioExecutionFrame {
  return {
    payload: serializeRuntimeEventFrame(event),
    receivedAtUtc: formatRuntimeEventTimestamp(event),
  };
}

function formatParameterValue(value: unknown): string {
  if (value === null || value === undefined) {
    return '';
  }

  if (
    typeof value === 'string' ||
    typeof value === 'boolean' ||
    typeof value === 'number'
  ) {
    return String(value);
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function getOrCreateExecutionStepState(
  stepStates: Map<string, StepExecutionState>,
  stepId: string,
): StepExecutionState {
  const existing = stepStates.get(stepId);
  if (existing) {
    return existing;
  }

  const nextState: StepExecutionState = {
    stepId,
    status: 'idle',
    stepType: '',
    targetRole: '',
    startedAt: null,
    completedAt: null,
    success: null,
    error: '',
    nextStepId: '',
    branchKey: '',
  };
  stepStates.set(stepId, nextState);
  return nextState;
}

function safeJsonParse(value: string): Record<string, unknown> | null {
  try {
    return JSON.parse(value) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function asExecutionRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
}

function readRecordString(
  record: Record<string, unknown> | null | undefined,
  ...keys: string[]
): string {
  if (!record) {
    return '';
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.trim()) {
      return value.trim();
    }
  }

  return '';
}

function readRecordNumber(
  record: Record<string, unknown> | null | undefined,
  ...keys: string[]
): number | null {
  if (!record) {
    return null;
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }

    if (typeof value === 'string') {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        return parsed;
      }
    }
  }

  return null;
}

function buildExecutionLogText(value: unknown): string {
  return formatParameterValue(value).trim();
}

function buildExecutionLogPreview(value: unknown): string {
  const text = buildExecutionLogText(value);
  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function buildExecutionLogPreviewFromText(value: string): string {
  const text = value.trim();
  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function readCustomFrame(parsed: Record<string, unknown>): {
  name: string;
  payload: unknown;
} {
  const custom = asExecutionRecord(parsed.custom);
  if (custom) {
    return {
      name: String(custom.name || '').trim(),
      payload: custom.payload ?? custom.value ?? null,
    };
  }

  if (String(parsed.type || '').trim().toUpperCase() === 'CUSTOM') {
    return {
      name: String(parsed.name || '').trim(),
      payload: parsed.payload ?? parsed.value ?? null,
    };
  }

  return {
    name: '',
    payload: null,
  };
}

function buildUsageMeta(payload: unknown): string {
  const record = asExecutionRecord(payload);
  if (!record) {
    return 'usage';
  }

  const model = readRecordString(record, 'model', 'modelName', 'model_name');
  const promptTokens = readRecordNumber(record, 'promptTokens', 'prompt_tokens');
  const completionTokens = readRecordNumber(
    record,
    'completionTokens',
    'completion_tokens',
  );
  const totalTokens = readRecordNumber(record, 'totalTokens', 'total_tokens');
  return [
    model,
    promptTokens !== null ? `prompt ${promptTokens}` : null,
    completionTokens !== null ? `completion ${completionTokens}` : null,
    totalTokens !== null ? `total ${totalTokens}` : null,
  ]
    .filter(Boolean)
    .join(' · ') || 'usage';
}

function buildEvidencePreview(payload: unknown): string {
  const record = asExecutionRecord(payload);
  if (!record) {
    return buildExecutionLogPreview(payload);
  }

  const summary = [
    readRecordString(record, 'evidenceId', 'evidence_id', 'id'),
    readRecordString(record, 'source', 'sourceName', 'source_name'),
    readRecordString(record, 'status'),
    readRecordString(record, 'currentStepId', 'current_step_id', 'stepId'),
  ]
    .filter(Boolean)
    .join(' · ');

  return summary || buildExecutionLogPreview(payload);
}

function readBusinessOutputText(value: unknown): string {
  if (typeof value === 'string') {
    return value.trim();
  }

  const record = asExecutionRecord(value);
  if (!record) {
    return '';
  }

  const directOutput = readRecordString(
    record,
    'output',
    'Output',
    'message',
    'Message',
    'text',
    'Text',
  );
  if (directOutput) {
    return directOutput;
  }

  const nestedResult = record.result ?? record.Result;
  if (nestedResult && nestedResult !== value) {
    return readBusinessOutputText(nestedResult);
  }

  return '';
}

function isRawObservedEventName(value: string): boolean {
  return value === 'aevatar.raw.observed' || value === 'aevatar.observed.raw';
}

function normalizeExecutionInteractionKind(
  value: unknown,
): ExecutionInteractionState['kind'] | null {
  const text = String(value || '').trim().toLowerCase();
  if (
    text === 'human_input' ||
    text === 'human_approval' ||
    text === 'wait_signal'
  ) {
    return text;
  }

  return null;
}

function normalizeExecutionTimeout(value: unknown): number | null {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

export function getExecutionFocusStepId(
  trace: ExecutionTrace | null,
  activeLogIndex: number | null,
): string | null {
  if (!trace) {
    return null;
  }

  const activeLog = Number.isInteger(activeLogIndex)
    ? trace.logs[activeLogIndex as number]
    : null;

  return activeLog?.stepId || trace.latestStepId || null;
}

export function normalizeExecutionLogStatus(
  log: ExecutionLogItem | null | undefined,
): ExecutionLogStatus {
  switch (log?.tone) {
    case 'completed':
      return 'success';
    case 'failed':
      return 'error';
    case 'pending':
      return 'waiting';
    case 'run':
    case 'started':
    default:
      return 'running';
  }
}

export function findExecutionLogIndexForStep(
  trace: ExecutionTrace | null,
  stepId: string,
): number | null {
  if (!trace?.logs?.length || !stepId) {
    return null;
  }

  for (let index = trace.logs.length - 1; index >= 0; index -= 1) {
    if (trace.logs[index].stepId === stepId) {
      return index;
    }
  }

  return null;
}

export function formatDurationBetween(
  startValue: string | null | undefined,
  endValue: string | null | undefined,
): string {
  if (!startValue) {
    return '';
  }

  const start = new Date(startValue).getTime();
  const end = endValue ? new Date(endValue).getTime() : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    return '';
  }

  const durationMs = end - start;
  if (durationMs < 1000) {
    return `${Math.round(durationMs)}ms`;
  }

  const seconds = durationMs / 1000;
  if (seconds < 60) {
    return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)}s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainderSeconds = Math.round(seconds % 60);
  if (minutes < 60) {
    return `${minutes}m ${remainderSeconds}s`;
  }

  const hours = Math.floor(minutes / 60);
  const remainderMinutes = minutes % 60;
  return `${hours}h ${remainderMinutes}m`;
}

export function formatExecutionLogClipboard(log: ExecutionLogItem): string {
  const lines = [`[${log.timestamp}] ${log.title}`];
  if (log.meta) {
    lines.push(log.meta);
  }
  if (log.clipboardText) {
    lines.push(log.clipboardText);
  }
  return lines.join('\n');
}

export function formatExecutionLogsClipboard(
  trace: ExecutionTrace | null,
): string {
  if (!trace?.logs?.length) {
    return '';
  }

  return trace.logs.map((log) => formatExecutionLogClipboard(log)).join('\n\n---\n\n');
}

export function buildExecutionTrace(
  detail: StudioExecutionDetail | null | undefined,
): ExecutionTrace | null {
  if (!detail) {
    return null;
  }

  const stepStates = new Map<string, StepExecutionState>();
  const traversedEdges = new Set<string>();
  const logs: ExecutionLogItem[] = [];
  let latestStepId: string | null = null;

  for (const frame of detail.frames || []) {
    const parsed = safeJsonParse(frame.payload);
    const timestamp = frame.receivedAtUtc;
    if (!parsed) {
      continue;
    }

    const rawText = buildExecutionLogText(parsed);
    const { name: customName, payload: customPayloadValue } =
      readCustomFrame(parsed);
    const customPayload = asExecutionRecord(customPayloadValue);

    if (customName === 'aevatar.step.request') {
      const parsedStepStarted = asExecutionRecord(parsed.stepStarted) || {};
      const stepId =
        String(customPayload?.stepId || parsedStepStarted.stepName || '').trim();
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'active';
      stepState.stepType = String(customPayload?.stepType || stepState.stepType || '');
      stepState.targetRole = String(
        customPayload?.targetRole || stepState.targetRole || '',
      );
      stepState.startedAt = timestamp;
      latestStepId = stepId;
      logs.push({
        category: 'step',
        tone: 'started',
        title: t("shared.studio.execution.started", "{value1} started", { value1: stepId }),
        meta: [
          String(customPayload?.stepType || '').trim(),
          String(customPayload?.targetRole || '').trim(),
        ]
          .filter(Boolean)
          .join(' · '),
        previewText: buildExecutionLogPreview(customPayload?.input),
        clipboardText: buildExecutionLogText(customPayload?.input),
        timestamp,
        stepId,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (
      customName === 'aevatar.human_input.request' ||
      customName === 'aevatar.wait_signal.request'
    ) {
      const stepId = String(customPayload?.stepId || '').trim();
      const runId = String(customPayload?.runId || '').trim();
      const interactionKind =
        customName === 'aevatar.wait_signal.request'
          ? 'wait_signal'
          : normalizeExecutionInteractionKind(customPayload?.suspensionType);
      if (!stepId || !runId || !interactionKind) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'waiting';
      stepState.stepType = stepState.stepType || interactionKind;
      latestStepId = stepId;

      const timeoutSeconds = normalizeExecutionTimeout(customPayload?.timeoutSeconds);
      const interaction: ExecutionInteractionState = {
        kind: interactionKind,
        runId,
        stepId,
        prompt: String(customPayload?.prompt || '').trim(),
        timeoutSeconds,
        variableName: String(customPayload?.variableName || '').trim(),
        signalName: String(customPayload?.signalName || '').trim(),
      };

      logs.push({
        category: 'step',
        tone: 'pending',
        title:
          interactionKind === 'human_approval'
            ? `${stepId} waiting for approval`
            : interactionKind === 'wait_signal'
              ? `${stepId} waiting for signal`
            : `${stepId} waiting for input`,
        meta: [
          interactionKind === 'human_approval'
            ? 'human approval'
            : interactionKind === 'wait_signal'
              ? `wait signal${
                  interaction.signalName ? ` ${interaction.signalName}` : ''
                }`
              : 'human input',
          interaction.variableName ? `variable ${interaction.variableName}` : null,
          timeoutSeconds ? `timeout ${timeoutSeconds}s` : null,
        ]
          .filter(Boolean)
          .join(' · '),
        previewText: buildExecutionLogPreview(interaction.prompt),
        clipboardText: buildExecutionLogText(interaction.prompt),
        timestamp,
        stepId,
        interaction,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (customName === 'aevatar.step.completed') {
      const parsedStepFinished =
        asExecutionRecord(parsed.stepFinished) || {};
      const stepId =
        String(customPayload?.stepId || parsedStepFinished.stepName || '').trim();
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status =
        customPayload?.success === false ? 'failed' : 'completed';
      stepState.completedAt = timestamp;
      stepState.success = customPayload?.success !== false;
      stepState.error = String(customPayload?.error || '');
      stepState.nextStepId = String(customPayload?.nextStepId || '');
      stepState.branchKey = String(customPayload?.branchKey || '');

      if (stepState.nextStepId) {
        traversedEdges.add(`${stepId}->${stepState.nextStepId}`);
      }

      latestStepId = stepId;
      logs.push({
        category: 'step',
        tone: customPayload?.success === false ? 'failed' : 'completed',
        title: t("shared.studio.execution.copy", "{value1} {value2}", { value1: stepId, value2: customPayload?.success === false ? 'failed' : 'completed' }),
        meta: [
          stepState.stepType,
          stepState.branchKey ? `branch ${stepState.branchKey}` : null,
          stepState.nextStepId ? `next ${stepState.nextStepId}` : null,
        ]
          .filter(Boolean)
          .join(' · '),
        previewText: buildExecutionLogPreview(
          customPayload?.error || customPayload?.output,
        ),
        clipboardText: buildExecutionLogText(
          customPayload?.error || customPayload?.output,
        ),
        timestamp,
        stepId,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (customName === 'studio.human.resume') {
      const stepId = String(customPayload?.stepId || '').trim();
      if (!stepId) {
        continue;
      }

      const stepState = getOrCreateExecutionStepState(stepStates, stepId);
      stepState.status = 'active';
      latestStepId = stepId;
      const interactionKind = normalizeExecutionInteractionKind(
        customPayload?.suspensionType,
      );
      const approved = customPayload?.approved !== false;
      logs.push({
        category: 'step',
        tone: 'run',
        title:
          interactionKind === 'human_approval'
            ? `${stepId} ${approved ? 'approved' : 'rejected'}`
            : interactionKind === 'wait_signal'
              ? `${stepId} signal sent`
            : `${stepId} input submitted`,
        meta:
          interactionKind === 'human_approval'
            ? `human approval · ${approved ? 'approved' : 'rejected'}`
            : interactionKind === 'wait_signal'
              ? `wait signal${
                  String(customPayload?.signalName || '').trim()
                    ? ` · ${String(customPayload?.signalName || '').trim()}`
                    : ''
                }`
            : 'human input submitted',
        previewText: buildExecutionLogPreview(customPayload?.userInput),
        clipboardText: buildExecutionLogText(customPayload?.userInput),
        timestamp,
        stepId,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (customName === 'studio.run.stop.requested') {
      logs.push({
        category: 'lifecycle',
        tone: 'pending',
        title: t("shared.studio.execution.stop.requested", "Stop requested"),
        meta: '',
        previewText: buildExecutionLogPreview(customPayload?.reason),
        clipboardText: buildExecutionLogText(customPayload?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (customName === 'aevatar.run.stopped') {
      logs.push({
        category: 'lifecycle',
        tone: 'run',
        title: t("shared.studio.execution.run.stopped", "Run stopped"),
        meta: '',
        previewText: buildExecutionLogPreview(customPayload?.reason),
        clipboardText: buildExecutionLogText(customPayload?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    const runStarted = asExecutionRecord(parsed.runStarted);
    if (runStarted) {
      logs.push({
        category: 'lifecycle',
        tone: 'run',
        title: t("shared.studio.execution.run.started", "Run started"),
        meta: String(detail.workflowName || ''),
        previewText: '',
        clipboardText: buildExecutionLogText(runStarted),
        timestamp,
        stepId: null,
        interaction: null,
        payloadText: buildExecutionLogText(runStarted),
        rawText,
        eventType: 'RUN_STARTED',
      });
      continue;
    }

    const runError =
      asExecutionRecord(parsed.runError) || null;
    if (runError?.message) {
      logs.push({
        category: 'lifecycle',
        tone: 'failed',
        title: t("shared.studio.execution.run.failed", "Run failed"),
        meta: String(runError.code || ''),
        previewText: buildExecutionLogPreview(runError.message),
        clipboardText: buildExecutionLogText(runError.message),
        timestamp,
        stepId: latestStepId,
        interaction: null,
        payloadText: buildExecutionLogText(runError),
        rawText,
        eventType: 'RUN_ERROR',
      });
      continue;
    }

    if (parsed.runStopped) {
      const runStopped =
        asExecutionRecord(parsed.runStopped);
      logs.push({
        category: 'lifecycle',
        tone: 'run',
        title: t("shared.studio.execution.run.stopped.2", "Run stopped"),
        meta: '',
        previewText: buildExecutionLogPreview(runStopped?.reason),
        clipboardText: buildExecutionLogText(runStopped?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
        payloadText: buildExecutionLogText(runStopped),
        rawText,
        eventType: 'RUN_STOPPED',
      });
      continue;
    }

    if (parsed.runFinished) {
      const runFinished = asExecutionRecord(parsed.runFinished);
      const runResult = runFinished?.result ?? runFinished;
      const businessOutput = readBusinessOutputText(runResult);
      const outputText = businessOutput || buildExecutionLogText(runResult);
      logs.push({
        category: 'output',
        tone: 'run',
        title: t("shared.studio.execution.run.finished", "Run finished"),
        meta: '',
        previewText: buildExecutionLogPreview(outputText),
        clipboardText: outputText,
        timestamp,
        stepId: latestStepId,
        interaction: null,
        payloadText: businessOutput ? buildExecutionLogText(runResult) : '',
        rawText,
        eventType: 'RUN_FINISHED',
      });
      continue;
    }

    if (customName === 'aevatar.run.context') {
      const existingRunStartIndex = logs.findIndex(
        (log) =>
          log.category === 'lifecycle' &&
          log.title === t("shared.studio.execution.run.started", "Run started"),
      );
      if (existingRunStartIndex >= 0) {
        continue;
      }

      logs.push({
        category: 'lifecycle',
        tone: 'run',
        title: t("shared.studio.execution.run.started", "Run started"),
        meta: String(customPayload?.workflowName || detail.workflowName || ''),
        previewText: '',
        clipboardText: '',
        timestamp,
        stepId: null,
        interaction: null,
        payloadText: buildExecutionLogText(customPayloadValue),
        rawText,
        eventType: customName,
      });
      continue;
    }

    const snapshot = parsed.stateSnapshot ?? parsed.snapshot;
    if (snapshot) {
      const payloadText = buildExecutionLogText(snapshot);
      logs.push({
        category: 'snapshot',
        tone: 'run',
        title: 'STATE_SNAPSHOT',
        meta: 'runtime evidence',
        previewText: buildExecutionLogPreviewFromText(payloadText),
        clipboardText: payloadText,
        timestamp,
        stepId: null,
        interaction: null,
        payloadText,
        rawText,
        eventType: 'STATE_SNAPSHOT',
      });
      continue;
    }

    if (customName === 'aevatar.usage') {
      const payloadText = buildExecutionLogText(customPayloadValue);
      logs.push({
        category: 'usage',
        tone: 'run',
        title: customName,
        meta: buildUsageMeta(customPayloadValue),
        previewText: buildExecutionLogPreviewFromText(payloadText),
        clipboardText: payloadText,
        timestamp,
        stepId: null,
        interaction: null,
        payloadText,
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (isRawObservedEventName(customName)) {
      const payloadText = buildExecutionLogText(customPayloadValue);
      logs.push({
        category: 'raw',
        tone: 'run',
        title: customName,
        meta: 'runtime observation',
        previewText: buildEvidencePreview(customPayloadValue),
        clipboardText: payloadText,
        timestamp,
        stepId: null,
        interaction: null,
        payloadText,
        rawText,
        eventType: customName,
      });
      continue;
    }

    if (customName) {
      const payloadText = buildExecutionLogText(customPayloadValue);
      logs.push({
        category: 'custom',
        tone: 'run',
        title: customName,
        meta: 'custom event',
        previewText: buildExecutionLogPreviewFromText(payloadText),
        clipboardText: payloadText,
        timestamp,
        stepId: null,
        interaction: null,
        payloadText,
        rawText,
        eventType: customName,
      });
    }
  }

  let defaultLogIndex: number | null = null;
  for (let index = logs.length - 1; index >= 0; index -= 1) {
    const stepId = logs[index].stepId;
    if (
      logs[index].interaction &&
      stepId &&
      stepStates.get(stepId)?.status === 'waiting'
    ) {
      defaultLogIndex = index;
      break;
    }
  }

  for (let index = logs.length - 1; index >= 0 && defaultLogIndex === null; index -= 1) {
    if (logs[index].stepId) {
      defaultLogIndex = index;
      break;
    }
  }

  return {
    stepStates,
    traversedEdges,
    logs,
    latestStepId,
    defaultLogIndex,
  };
}

export function decorateNodesForExecution(
  nodes: Array<Node<StudioGraphNodeData>>,
  trace: ExecutionTrace | null,
  activeLogIndex: number | null,
): Array<Node<StudioGraphNodeData>> {
  const focusedStepId = getExecutionFocusStepId(trace, activeLogIndex);

  return nodes.map((node) => {
    const stepState = trace?.stepStates.get(node.data.stepId);
    return {
      ...node,
      selectable: true,
      data: {
        ...node.data,
        executionStatus: stepState?.status || 'idle',
        executionFocused: focusedStepId === node.data.stepId,
      },
    };
  });
}

export function decorateEdgesForExecution(
  edges: Array<Edge<StudioGraphEdgeData>>,
  nodes: Array<Node<StudioGraphNodeData>>,
  trace: ExecutionTrace | null,
  activeLogIndex: number | null,
): Array<Edge<StudioGraphEdgeData>> {
  const focusedStepId = getExecutionFocusStepId(trace, activeLogIndex);
  const stepIdByNodeId = new Map(nodes.map((node) => [node.id, node.data.stepId]));

  return edges.map((edge) => {
    const sourceStepId = stepIdByNodeId.get(edge.source);
    const targetStepId = stepIdByNodeId.get(edge.target);
    const traversed =
      sourceStepId && targetStepId
        ? trace?.traversedEdges.has(`${sourceStepId}->${targetStepId}`)
        : false;
    const isFocused = Boolean(
      focusedStepId &&
        (sourceStepId === focusedStepId || targetStepId === focusedStepId),
    );

    const color = isFocused
      ? '#2F6FEC'
      : traversed
        ? '#22C55E'
        : edge.data?.kind === 'branch'
          ? '#8B5CF6'
          : '#94A3B8';

    return {
      ...edge,
      type: edge.type || 'smoothstep',
      animated: isFocused,
      style: {
        ...edge.style,
        stroke: color,
        strokeWidth: isFocused ? 2.8 : 2.5,
      },
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 11,
        height: 11,
        color,
      },
      zIndex: 4,
    };
  });
}
