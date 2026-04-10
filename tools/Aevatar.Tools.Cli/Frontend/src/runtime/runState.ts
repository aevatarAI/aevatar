import type { RuntimeEvent } from './sseUtils';

export type ActiveRunStatus =
  | 'starting'
  | 'accepted'
  | 'running'
  | 'waiting'
  | 'completed'
  | 'error'
  | 'stopped';

export type ActiveRunWaitingKind =
  | 'human-input'
  | 'signal'
  | 'tool-approval';

export type ActiveRunStepStatus =
  | 'pending'
  | 'active'
  | 'waiting'
  | 'completed'
  | 'error';

export type ActiveRunStep = {
  id: string;
  label: string;
  stepType?: string;
  targetRole?: string;
  status: ActiveRunStepStatus;
  startedAt?: number;
  finishedAt?: number;
  output?: string;
  error?: string;
  branchKey?: string;
};

export type ActiveRunTimelineItem = {
  id: string;
  timestamp: number;
  title: string;
  detail?: string;
  tone: 'info' | 'success' | 'warning' | 'error';
  eventType: string;
};

export type ActiveRunState = {
  actorId?: string;
  runId?: string;
  workflowName?: string;
  commandId?: string;
  serviceId?: string;
  serviceLabel?: string;
  status: ActiveRunStatus;
  waitingKind?: ActiveRunWaitingKind;
  waitingPrompt?: string;
  waitingSignalName?: string;
  currentStepId?: string;
  currentStepType?: string;
  currentStepLabel?: string;
  currentToolName?: string;
  lastOutputPreview?: string;
  error?: string;
  startedAt: number;
  lastEventAt: number;
  completedAt?: number;
  steps: ActiveRunStep[];
  timeline: ActiveRunTimelineItem[];
};

type JsonRecord = Record<string, unknown>;

export type ActiveRunContext = {
  serviceId?: string;
  serviceLabel?: string;
  fallbackActorId?: string;
};

const ACTIVE_RUN_TIMELINE_LIMIT = 40;

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return undefined;
  }

  return value as JsonRecord;
}

function str(record: JsonRecord | undefined, ...keys: string[]): string {
  if (!record) {
    return '';
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string') {
      return value;
    }
  }

  return '';
}

function eventTimestamp(evt: RuntimeEvent): number {
  if (typeof evt.timestamp === 'number' && Number.isFinite(evt.timestamp)) {
    return evt.timestamp;
  }

  const parsed = Number(evt.timestamp);
  return Number.isFinite(parsed) ? parsed : Date.now();
}

function ensureRunState(state: ActiveRunState | null, context: ActiveRunContext, timestamp: number): ActiveRunState {
  if (state) {
    return {
      ...state,
      lastEventAt: timestamp,
      serviceId: state.serviceId || context.serviceId,
      serviceLabel: state.serviceLabel || context.serviceLabel,
      actorId: state.actorId || context.fallbackActorId,
    };
  }

  return {
    actorId: context.fallbackActorId,
    serviceId: context.serviceId,
    serviceLabel: context.serviceLabel,
    status: 'starting',
    startedAt: timestamp,
    lastEventAt: timestamp,
    steps: [],
    timeline: [],
  };
}

function getCustomPayload(evt: RuntimeEvent): JsonRecord | undefined {
  if (evt.type !== 'CUSTOM') {
    return undefined;
  }

  const direct = asRecord(evt.payload);
  const directValue = asRecord(evt.value);
  const nestedValue = direct?.value;
  if (nestedValue && typeof nestedValue === 'object' && !Array.isArray(nestedValue)) {
    return nestedValue as JsonRecord;
  }

  return directValue || direct;
}

function isWorkflowSignal(evt: RuntimeEvent): boolean {
  if (
    evt.type === 'RUN_STARTED' ||
    evt.type === 'RUN_FINISHED' ||
    evt.type === 'RUN_ERROR' ||
    evt.type === 'RUN_STOPPED' ||
    evt.type === 'STEP_STARTED' ||
    evt.type === 'STEP_FINISHED' ||
    evt.type === 'HUMAN_INPUT_REQUEST' ||
    evt.type === 'STATE_SNAPSHOT'
  ) {
    return true;
  }

  if (evt.type === 'CUSTOM') {
    const name = String(evt.name || '');
    return name.startsWith('aevatar.');
  }

  return false;
}

function upsertStep(
  steps: ActiveRunStep[],
  stepId: string,
  update: (current: ActiveRunStep | undefined) => ActiveRunStep,
): ActiveRunStep[] {
  if (!stepId.trim()) {
    return steps;
  }

  const existingIndex = steps.findIndex(step => step.id === stepId);
  if (existingIndex < 0) {
    return [...steps, update(undefined)];
  }

  return steps.map((step, index) => (index === existingIndex ? update(step) : step));
}

function markOtherActiveStepsCompleted(steps: ActiveRunStep[], activeStepId: string) {
  return steps.map(step => (
    step.status === 'active' && step.id !== activeStepId
      ? { ...step, status: 'completed' as const, finishedAt: step.finishedAt || Date.now() }
      : step
  ));
}

function pushTimeline(
  state: ActiveRunState,
  item: ActiveRunTimelineItem,
): ActiveRunState {
  return {
    ...state,
    timeline: [...state.timeline, item].slice(-ACTIVE_RUN_TIMELINE_LIMIT),
  };
}

function withTimeline(
  state: ActiveRunState,
  evt: RuntimeEvent,
  title: string,
  detail: string | undefined,
  tone: ActiveRunTimelineItem['tone'],
): ActiveRunState {
  return pushTimeline(state, {
    id: `${eventTimestamp(evt)}:${state.timeline.length}:${title}`,
    timestamp: eventTimestamp(evt),
    title,
    detail: detail?.trim() || undefined,
    tone,
    eventType: evt.type === 'CUSTOM' ? String(evt.name || evt.type) : evt.type,
  });
}

export function createPendingRunState(context: ActiveRunContext = {}): ActiveRunState {
  const now = Date.now();
  return {
    actorId: context.fallbackActorId,
    serviceId: context.serviceId,
    serviceLabel: context.serviceLabel,
    status: 'starting',
    startedAt: now,
    lastEventAt: now,
    steps: [],
    timeline: [
      {
        id: `pending:${now}`,
        timestamp: now,
        title: 'Prompt sent',
        detail: context.serviceLabel ? `Routing to ${context.serviceLabel}` : undefined,
        tone: 'info',
        eventType: 'prompt.sent',
      },
    ],
  };
}

export function applyRuntimeEventToActiveRun(
  current: ActiveRunState | null,
  evt: RuntimeEvent,
  context: ActiveRunContext = {},
): ActiveRunState | null {
  if (!current && !isWorkflowSignal(evt)) {
    return current;
  }

  const timestamp = eventTimestamp(evt);
  let next = ensureRunState(current, context, timestamp);

  switch (evt.type) {
    case 'RUN_STARTED': {
      next = {
        ...next,
        status: next.status === 'waiting' ? 'waiting' : 'running',
        actorId: String(evt.threadId || '').trim() || next.actorId,
        runId: String(evt.runId || '').trim() || next.runId,
        startedAt: next.startedAt || timestamp,
      };
      return withTimeline(next, evt, 'Run started', next.runId || next.actorId, 'info');
    }

    case 'RUN_FINISHED': {
      next = {
        ...next,
        status: 'completed',
        waitingKind: undefined,
        waitingPrompt: undefined,
        waitingSignalName: undefined,
        completedAt: timestamp,
      };
      return withTimeline(next, evt, 'Run finished', next.currentStepLabel || next.workflowName, 'success');
    }

    case 'RUN_STOPPED': {
      next = {
        ...next,
        status: 'stopped',
        waitingKind: undefined,
        waitingPrompt: undefined,
        waitingSignalName: undefined,
        completedAt: timestamp,
      };
      return withTimeline(next, evt, 'Run stopped', String(evt.reason || '').trim() || undefined, 'warning');
    }

    case 'RUN_ERROR': {
      const error = String(evt.message || '').trim() || 'Unknown workflow error';
      next = {
        ...next,
        status: 'error',
        error,
        waitingKind: undefined,
        waitingPrompt: undefined,
        waitingSignalName: undefined,
      };
      if (next.currentStepId) {
        next = {
          ...next,
          steps: upsertStep(next.steps, next.currentStepId, step => ({
            id: next.currentStepId || '',
            label: step?.label || next.currentStepLabel || next.currentStepId || 'Current step',
            stepType: step?.stepType || next.currentStepType,
            targetRole: step?.targetRole,
            status: 'error',
            startedAt: step?.startedAt,
            finishedAt: timestamp,
            output: step?.output,
            error,
            branchKey: step?.branchKey,
          })),
        };
      }
      return withTimeline(next, evt, 'Run failed', error, 'error');
    }

    case 'STEP_STARTED': {
      const stepId = String(evt.stepName || '').trim();
      if (!stepId) {
        return next;
      }

      next = {
        ...next,
        status: 'running',
        waitingKind: undefined,
        waitingPrompt: undefined,
        waitingSignalName: undefined,
        currentStepId: stepId,
        currentStepLabel: stepId,
        steps: upsertStep(markOtherActiveStepsCompleted(next.steps, stepId), stepId, step => ({
          id: stepId,
          label: step?.label || stepId,
          stepType: step?.stepType,
          targetRole: step?.targetRole,
          status: 'active',
          startedAt: step?.startedAt || timestamp,
          finishedAt: undefined,
          output: step?.output,
          error: undefined,
          branchKey: step?.branchKey,
        })),
      };
      return withTimeline(next, evt, 'Step started', stepId, 'info');
    }

    case 'STEP_FINISHED': {
      const stepId = String(evt.stepName || '').trim();
      if (!stepId) {
        return next;
      }

      next = {
        ...next,
        steps: upsertStep(next.steps, stepId, step => ({
          id: stepId,
          label: step?.label || stepId,
          stepType: step?.stepType,
          targetRole: step?.targetRole,
          status: step?.status === 'error' ? 'error' : 'completed',
          startedAt: step?.startedAt,
          finishedAt: step?.finishedAt || timestamp,
          output: step?.output,
          error: step?.error,
          branchKey: step?.branchKey,
        })),
      };
      return withTimeline(next, evt, 'Step finished', stepId, 'success');
    }

    case 'TOOL_CALL_START': {
      const toolName = String(evt.toolName || '').trim();
      next = {
        ...next,
        currentToolName: toolName || next.currentToolName,
      };
      return withTimeline(next, evt, 'Tool call started', toolName || String(evt.toolCallId || '').trim(), 'info');
    }

    case 'TOOL_CALL_END': {
      const detail = [String(evt.toolCallId || '').trim(), String(evt.result || '').trim()].filter(Boolean).join(' · ');
      return withTimeline(next, evt, 'Tool call finished', detail || next.currentToolName, 'success');
    }

    case 'TOOL_APPROVAL_REQUEST': {
      const toolName = String(evt.toolName || '').trim();
      next = {
        ...next,
        status: 'waiting',
        waitingKind: 'tool-approval',
        waitingPrompt: toolName ? `Approval required for ${toolName}` : next.waitingPrompt,
        currentToolName: toolName || next.currentToolName,
      };
      return withTimeline(next, evt, 'Waiting for tool approval', toolName || String(evt.toolCallId || '').trim(), 'warning');
    }

    case 'HUMAN_INPUT_REQUEST': {
      const stepId = String(evt.stepId || '').trim();
      const prompt = String(evt.prompt || '').trim();
      next = {
        ...next,
        status: 'waiting',
        waitingKind: 'human-input',
        waitingPrompt: prompt || undefined,
        currentStepId: stepId || next.currentStepId,
        currentStepLabel: stepId || next.currentStepLabel,
        steps: stepId
          ? upsertStep(next.steps, stepId, step => ({
              id: stepId,
              label: step?.label || stepId,
              stepType: step?.stepType,
              targetRole: step?.targetRole,
              status: 'waiting',
              startedAt: step?.startedAt || timestamp,
              finishedAt: undefined,
              output: step?.output,
              error: undefined,
              branchKey: step?.branchKey,
            }))
          : next.steps,
      };
      return withTimeline(next, evt, 'Waiting for input', prompt || stepId, 'warning');
    }

    default:
      break;
  }

  if (evt.type !== 'CUSTOM') {
    return next;
  }

  const name = String(evt.name || '');
  const payload = getCustomPayload(evt);

  if (name === 'aevatar.run.context') {
    next = {
      ...next,
      status: next.status === 'starting' ? 'accepted' : next.status,
      actorId: str(payload, 'actorId', 'actor_id') || next.actorId,
      workflowName: str(payload, 'workflowName', 'workflow_name') || next.workflowName,
      commandId: str(payload, 'commandId', 'command_id') || next.commandId,
    };
    return withTimeline(next, evt, 'Run accepted', next.workflowName || next.commandId, 'info');
  }

  if (name === 'aevatar.step.request') {
    const stepId = str(payload, 'stepId', 'step_id');
    const stepType = str(payload, 'stepType', 'step_type');
    const targetRole = str(payload, 'targetRole', 'target_role');
    const input = str(payload, 'input', 'Input');
    next = {
      ...next,
      status: 'running',
      waitingKind: undefined,
      waitingPrompt: undefined,
      waitingSignalName: undefined,
      currentStepId: stepId || next.currentStepId,
      currentStepType: stepType || next.currentStepType,
      currentStepLabel: stepId || next.currentStepLabel,
      steps: stepId
        ? upsertStep(markOtherActiveStepsCompleted(next.steps, stepId), stepId, step => ({
            id: stepId,
            label: step?.label || stepId,
            stepType: stepType || step?.stepType,
            targetRole: targetRole || step?.targetRole,
            status: 'active',
            startedAt: step?.startedAt || timestamp,
            finishedAt: undefined,
            output: step?.output,
            error: undefined,
            branchKey: step?.branchKey,
          }))
        : next.steps,
    };
    return withTimeline(
      next,
      evt,
      stepType ? `${stepType} started` : 'Step requested',
      [stepId, targetRole].filter(Boolean).join(' -> ') || input,
      'info',
    );
  }

  if (name === 'aevatar.step.completed') {
    const stepId = str(payload, 'stepId', 'step_id');
    const stepType = str(payload, 'stepType', 'step_type');
    const output = str(payload, 'output', 'Output');
    const error = str(payload, 'error', 'Error');
    const branchKey = str(payload, 'branchKey', 'branch_key');
    const nextStepId = str(payload, 'nextStepId', 'next_step_id');
    const successRaw = payload?.success;
    const success = typeof successRaw === 'boolean'
      ? successRaw
      : String(successRaw || '').toLowerCase() !== 'false';

    next = {
      ...next,
      status: success ? 'running' : 'error',
      error: success ? next.error : (error || next.error),
      lastOutputPreview: output || next.lastOutputPreview,
      currentStepId: nextStepId || next.currentStepId,
      currentStepLabel: nextStepId || next.currentStepLabel,
      steps: stepId
        ? upsertStep(next.steps, stepId, step => ({
            id: stepId,
            label: step?.label || stepId,
            stepType: stepType || step?.stepType,
            targetRole: step?.targetRole,
            status: success ? 'completed' : 'error',
            startedAt: step?.startedAt,
            finishedAt: timestamp,
            output: output || step?.output,
            error: success ? undefined : (error || step?.error),
            branchKey: branchKey || step?.branchKey,
          }))
        : next.steps,
    };
    return withTimeline(
      next,
      evt,
      success ? 'Step completed' : 'Step failed',
      [stepId, branchKey].filter(Boolean).join(' · ') || error || output,
      success ? 'success' : 'error',
    );
  }

  if (name === 'aevatar.human_input.request') {
    const stepId = str(payload, 'stepId', 'step_id');
    const prompt = str(payload, 'prompt', 'Prompt');
    next = {
      ...next,
      status: 'waiting',
      waitingKind: 'human-input',
      waitingPrompt: prompt || next.waitingPrompt,
      currentStepId: stepId || next.currentStepId,
      currentStepLabel: stepId || next.currentStepLabel,
      steps: stepId
        ? upsertStep(next.steps, stepId, step => ({
            id: stepId,
            label: step?.label || stepId,
            stepType: step?.stepType,
            targetRole: step?.targetRole,
            status: 'waiting',
            startedAt: step?.startedAt || timestamp,
            finishedAt: undefined,
            output: step?.output,
            error: undefined,
            branchKey: step?.branchKey,
          }))
        : next.steps,
    };
    return withTimeline(next, evt, 'Waiting for human input', prompt || stepId, 'warning');
  }

  if (name === 'aevatar.workflow.waiting_signal') {
    const stepId = str(payload, 'stepId', 'step_id');
    const signalName = str(payload, 'signalName', 'signal_name');
    const prompt = str(payload, 'prompt', 'Prompt');
    next = {
      ...next,
      status: 'waiting',
      waitingKind: 'signal',
      waitingPrompt: prompt || next.waitingPrompt,
      waitingSignalName: signalName || next.waitingSignalName,
      currentStepId: stepId || next.currentStepId,
      currentStepLabel: stepId || next.currentStepLabel,
      steps: stepId
        ? upsertStep(next.steps, stepId, step => ({
            id: stepId,
            label: step?.label || stepId,
            stepType: step?.stepType,
            targetRole: step?.targetRole,
            status: 'waiting',
            startedAt: step?.startedAt || timestamp,
            finishedAt: undefined,
            output: step?.output,
            error: undefined,
            branchKey: step?.branchKey,
          }))
        : next.steps,
    };
    return withTimeline(
      next,
      evt,
      'Waiting for signal',
      [signalName, prompt].filter(Boolean).join(' · '),
      'warning',
    );
  }

  if (name === 'aevatar.llm.reasoning') {
    return withTimeline(next, evt, 'Reasoning update', str(payload, 'role', 'Role') || next.currentStepLabel, 'info');
  }

  if (name === 'TOOL_APPROVAL_REQUEST') {
    const toolName = str(payload, 'toolName', 'tool_name');
    next = {
      ...next,
      status: 'waiting',
      waitingKind: 'tool-approval',
      waitingPrompt: toolName ? `Approval required for ${toolName}` : next.waitingPrompt,
      currentToolName: toolName || next.currentToolName,
    };
    return withTimeline(next, evt, 'Waiting for tool approval', toolName || str(payload, 'toolCallId', 'tool_call_id'), 'warning');
  }

  return next;
}
