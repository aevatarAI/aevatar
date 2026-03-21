import {
  MarkerType,
  type Edge,
  type Node,
} from '@xyflow/react';
import type { StudioGraphEdgeData, StudioGraphNodeData } from './graph';
import type { StudioExecutionDetail } from './models';

export type ExecutionLogItem = {
  readonly tone: 'started' | 'completed' | 'failed' | 'run' | 'pending';
  readonly title: string;
  readonly meta: string;
  readonly previewText: string;
  readonly clipboardText: string;
  readonly timestamp: string;
  readonly stepId: string | null;
  readonly interaction: ExecutionInteractionState | null;
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
  readonly kind: 'human_input' | 'human_approval';
  readonly runId: string;
  readonly stepId: string;
  readonly prompt: string;
  readonly timeoutSeconds: number | null;
  readonly variableName: string;
};

export type ExecutionTrace = {
  readonly stepStates: Map<string, StepExecutionState>;
  readonly traversedEdges: Set<string>;
  readonly logs: ExecutionLogItem[];
  readonly latestStepId: string | null;
  readonly defaultLogIndex: number | null;
};

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

function buildExecutionLogText(value: unknown): string {
  return formatParameterValue(value).trim();
}

function buildExecutionLogPreview(value: unknown): string {
  const text = buildExecutionLogText(value);
  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function normalizeExecutionInteractionKind(
  value: unknown,
): ExecutionInteractionState['kind'] | null {
  const text = String(value || '').trim().toLowerCase();
  if (text === 'human_input' || text === 'human_approval') {
    return text;
  }

  return null;
}

function normalizeExecutionTimeout(value: unknown): number | null {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

function getExecutionFocusStepId(
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

    const custom = (parsed.custom as Record<string, unknown> | undefined) || {};
    const customName = String(custom.name || '').trim();
    const customPayload =
      (custom.payload as Record<string, unknown> | null | undefined) || null;

    if (customName === 'aevatar.step.request') {
      const parsedStepStarted = (parsed.stepStarted as Record<string, unknown> | undefined) || {};
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
        tone: 'started',
        title: `${stepId} started`,
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
      });
      continue;
    }

    if (customName === 'aevatar.human_input.request') {
      const stepId = String(customPayload?.stepId || '').trim();
      const runId = String(customPayload?.runId || '').trim();
      const interactionKind = normalizeExecutionInteractionKind(
        customPayload?.suspensionType,
      );
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
      };

      logs.push({
        tone: 'pending',
        title:
          interactionKind === 'human_approval'
            ? `${stepId} waiting for approval`
            : `${stepId} waiting for input`,
        meta: [
          interactionKind === 'human_approval' ? 'human approval' : 'human input',
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
      });
      continue;
    }

    if (customName === 'aevatar.step.completed') {
      const parsedStepFinished =
        (parsed.stepFinished as Record<string, unknown> | undefined) || {};
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
        tone: customPayload?.success === false ? 'failed' : 'completed',
        title: `${stepId} ${
          customPayload?.success === false ? 'failed' : 'completed'
        }`,
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
        tone: 'run',
        title:
          interactionKind === 'human_approval'
            ? `${stepId} ${approved ? 'approved' : 'rejected'}`
            : `${stepId} input submitted`,
        meta:
          interactionKind === 'human_approval'
            ? `human approval · ${approved ? 'approved' : 'rejected'}`
            : 'human input submitted',
        previewText: buildExecutionLogPreview(customPayload?.userInput),
        clipboardText: buildExecutionLogText(customPayload?.userInput),
        timestamp,
        stepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'studio.run.stop.requested') {
      logs.push({
        tone: 'pending',
        title: 'Stop requested',
        meta: '',
        previewText: buildExecutionLogPreview(customPayload?.reason),
        clipboardText: buildExecutionLogText(customPayload?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'aevatar.run.stopped') {
      logs.push({
        tone: 'run',
        title: 'Run stopped',
        meta: '',
        previewText: buildExecutionLogPreview(customPayload?.reason),
        clipboardText: buildExecutionLogText(customPayload?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    const runError =
      (parsed.runError as Record<string, unknown> | undefined) || null;
    if (runError?.message) {
      logs.push({
        tone: 'failed',
        title: 'Run failed',
        meta: String(runError.code || ''),
        previewText: buildExecutionLogPreview(runError.message),
        clipboardText: buildExecutionLogText(runError.message),
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (parsed.runStopped) {
      const runStopped =
        parsed.runStopped as Record<string, unknown> | undefined;
      logs.push({
        tone: 'run',
        title: 'Run stopped',
        meta: '',
        previewText: buildExecutionLogPreview(runStopped?.reason),
        clipboardText: buildExecutionLogText(runStopped?.reason),
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (parsed.runFinished) {
      logs.push({
        tone: 'run',
        title: 'Run finished',
        meta: '',
        previewText: '',
        clipboardText: '',
        timestamp,
        stepId: latestStepId,
        interaction: null,
      });
      continue;
    }

    if (customName === 'aevatar.run.context') {
      logs.push({
        tone: 'run',
        title: 'Run started',
        meta: String(customPayload?.workflowName || detail.workflowName || ''),
        previewText: '',
        clipboardText: '',
        timestamp,
        stepId: null,
        interaction: null,
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
      draggable: false,
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
