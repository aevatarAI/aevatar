import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from '@aevatar-react-sdk/types';
import {
  parseHumanInputRequestData,
  parseStepCompletedData,
  parseStepRequestData,
  parseWaitingSignalData,
} from '@/shared/agui/customEventData';
import type {
  WorkflowActorTimelineItem,
  WorkflowAuthoringStep,
  WorkflowCatalogStep,
} from '@/shared/api/models';

type PlaygroundReferenceStep = WorkflowCatalogStep | WorkflowAuthoringStep;

export type PlaygroundStepStatus =
  | 'idle'
  | 'running'
  | 'waiting'
  | 'success'
  | 'error';

export type PlaygroundStepSummary = {
  key: string;
  stepId: string;
  stepType: string;
  targetRole: string;
  source: 'reference' | 'runtime' | 'merged';
  status: PlaygroundStepStatus;
  statusLabel: string;
  checkpointLabel: string;
  lastStage: string;
  lastMessage: string;
  agentId: string;
  observationCount: number;
  startedAt: string;
  updatedAt: string;
  referenceOrder: number;
};

export type PlaygroundStepMetrics = {
  totalReferenceSteps: number;
  observedSteps: number;
  runningSteps: number;
  waitingSteps: number;
  successfulSteps: number;
  failedSteps: number;
};

type MutableStepSummary = PlaygroundStepSummary;

function isApprovalSuspension(suspensionType?: string | null): boolean {
  return suspensionType?.toLowerCase().includes('approval') ?? false;
}

function makeDefaultSummary(
  stepId: string,
  referenceOrder: number,
): MutableStepSummary {
  return {
    key: stepId || `runtime-${referenceOrder}`,
    stepId,
    stepType: '',
    targetRole: '',
    source: 'runtime',
    status: 'idle',
    statusLabel: 'Idle',
    checkpointLabel: '',
    lastStage: '',
    lastMessage: '',
    agentId: '',
    observationCount: 0,
    startedAt: '',
    updatedAt: '',
    referenceOrder,
  };
}

function normalizeTimelineStatus(
  item: WorkflowActorTimelineItem,
): PlaygroundStepStatus {
  const normalized = `${item.stage} ${item.eventType} ${item.message}`.toLowerCase();

  if (normalized.includes('error') || normalized.includes('fail')) {
    return 'error';
  }
  if (
    normalized.includes('wait') ||
    normalized.includes('signal') ||
    normalized.includes('approval') ||
    normalized.includes('human')
  ) {
    return 'waiting';
  }
  if (
    normalized.includes('complete') ||
    normalized.includes('finish') ||
    normalized.includes('success')
  ) {
    return 'success';
  }
  if (
    normalized.includes('start') ||
    normalized.includes('run') ||
    normalized.includes('request')
  ) {
    return 'running';
  }

  return 'idle';
}

function statusLabel(status: PlaygroundStepStatus): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'waiting':
      return 'Waiting';
    case 'success':
      return 'Completed';
    case 'error':
      return 'Failed';
    default:
      return 'Idle';
  }
}

function mergeStepSource(
  source: PlaygroundStepSummary['source'],
): PlaygroundStepSummary['source'] {
  if (source === 'reference' || source === 'merged') {
    return 'merged';
  }

  return 'runtime';
}

function applyObservation(
  summary: MutableStepSummary,
  input: {
    status: PlaygroundStepStatus;
    updatedAt?: string;
    stage?: string;
    message?: string;
    agentId?: string;
    stepType?: string;
    checkpointLabel?: string;
  },
): void {
  summary.status = input.status;
  summary.statusLabel = statusLabel(input.status);
  summary.observationCount += 1;

  if (input.updatedAt) {
    summary.updatedAt = input.updatedAt;
    if (!summary.startedAt) {
      summary.startedAt = input.updatedAt;
    }
  }
  if (input.stage) {
    summary.lastStage = input.stage;
  }
  if (input.message) {
    summary.lastMessage = input.message;
  }
  if (input.agentId) {
    summary.agentId = input.agentId;
  }
  if (input.stepType) {
    summary.stepType = input.stepType;
  }
  if (input.checkpointLabel !== undefined) {
    summary.checkpointLabel = input.checkpointLabel;
  }
}

function getOrCreateStep(
  map: Map<string, MutableStepSummary>,
  stepId: string,
  nextRuntimeOrder: () => number,
): MutableStepSummary {
  if (stepId) {
    const existing = map.get(stepId);
    if (existing) {
      return existing;
    }
  }

  const allocatedOrder = nextRuntimeOrder();
  const normalizedStepId = stepId || `runtime-${allocatedOrder}`;
  const created = makeDefaultSummary(normalizedStepId, allocatedOrder);
  map.set(normalizedStepId, created);
  return created;
}

export function buildPlaygroundStepSummaries(input: {
  referenceSteps?: PlaygroundReferenceStep[];
  actorTimeline?: WorkflowActorTimelineItem[];
  events?: AGUIEvent[];
}): PlaygroundStepSummary[] {
  const stepMap = new Map<string, MutableStepSummary>();
  let runtimeOrder = input.referenceSteps?.length ?? 0;

  const nextRuntimeOrder = (): number => {
    runtimeOrder += 1;
    return runtimeOrder;
  };

  for (const [index, step] of (input.referenceSteps ?? []).entries()) {
    const summary = makeDefaultSummary(step.id, index);
    summary.stepType = step.type;
    summary.targetRole = step.targetRole;
    summary.source = 'reference';
    stepMap.set(step.id, summary);
  }

  const orderedTimeline = [...(input.actorTimeline ?? [])].sort((left, right) =>
    left.timestamp.localeCompare(right.timestamp),
  );

  for (const item of orderedTimeline) {
    if (!item.stepId) {
      continue;
    }

    const summary = getOrCreateStep(stepMap, item.stepId, nextRuntimeOrder);
    summary.source = mergeStepSource(summary.source);
    applyObservation(summary, {
      status: normalizeTimelineStatus(item),
      updatedAt: item.timestamp,
      stage: item.stage,
      message: item.message,
      agentId: item.agentId,
      stepType: item.stepType,
      checkpointLabel:
        normalizeTimelineStatus(item) === 'waiting'
          ? item.stepType || 'Checkpoint'
          : summary.checkpointLabel,
    });
  }

  const orderedEvents = [...(input.events ?? [])].sort(
    (left, right) => (left.timestamp ?? 0) - (right.timestamp ?? 0),
  );

  for (const event of orderedEvents) {
    const updatedAt = event.timestamp ? new Date(event.timestamp).toISOString() : '';

    if (event.type === AGUIEventType.HUMAN_INPUT_REQUEST) {
      const summary = getOrCreateStep(stepMap, event.stepId ?? '', nextRuntimeOrder);
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: 'waiting',
        updatedAt,
        stage: event.type,
        message: event.prompt,
        stepType: event.suspensionType,
        checkpointLabel: isApprovalSuspension(event.suspensionType)
          ? 'Approval'
          : 'Human input',
      });
      continue;
    }

    if (event.type === AGUIEventType.HUMAN_INPUT_RESPONSE) {
      const summary = getOrCreateStep(stepMap, event.stepId ?? '', nextRuntimeOrder);
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: 'running',
        updatedAt,
        stage: event.type,
        message: `Human input submitted for ${event.stepId ?? 'unknown step'}`,
      });
      continue;
    }

    if (event.type !== AGUIEventType.CUSTOM) {
      continue;
    }

    const custom = parseCustomEvent(event);
    if (custom.name === CustomEventName.StepRequest) {
      const data = parseStepRequestData(custom.data);
      const summary = getOrCreateStep(
        stepMap,
        data?.stepId ?? '',
        nextRuntimeOrder,
      );
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: 'running',
        updatedAt,
        stage: custom.name,
        message: data?.stepType || 'Step requested.',
        stepType: data?.stepType,
      });
      continue;
    }

    if (custom.name === CustomEventName.StepCompleted) {
      const data = parseStepCompletedData(custom.data);
      const summary = getOrCreateStep(
        stepMap,
        data?.stepId ?? '',
        nextRuntimeOrder,
      );
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: data?.success === false ? 'error' : 'success',
        updatedAt,
        stage: custom.name,
        message:
          data?.success === false
            ? 'Step failed.'
            : 'Step completed successfully.',
      });
      continue;
    }

    if (custom.name === CustomEventName.WaitingSignal) {
      const data = parseWaitingSignalData(custom.data);
      const summary = getOrCreateStep(
        stepMap,
        data?.stepId ?? '',
        nextRuntimeOrder,
      );
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: 'waiting',
        updatedAt,
        stage: custom.name,
        message:
          data?.prompt || `Waiting for signal ${data?.signalName ?? 'unknown'}.`,
        checkpointLabel: data?.signalName || 'Signal',
      });
      continue;
    }

    if (custom.name === CustomEventName.HumanInputRequest) {
      const data = parseHumanInputRequestData(custom.data);
      const summary = getOrCreateStep(
        stepMap,
        data?.stepId ?? '',
        nextRuntimeOrder,
      );
      summary.source = mergeStepSource(summary.source);
      applyObservation(summary, {
        status: 'waiting',
        updatedAt,
        stage: custom.name,
        message: data?.prompt,
        stepType: data?.suspensionType,
        checkpointLabel: isApprovalSuspension(data?.suspensionType)
          ? 'Approval'
          : 'Human input',
      });
    }
  }

  return [...stepMap.values()].sort((left, right) => {
    if (left.referenceOrder !== right.referenceOrder) {
      return left.referenceOrder - right.referenceOrder;
    }

    return left.stepId.localeCompare(right.stepId);
  });
}

export function summarizePlaygroundSteps(
  steps: PlaygroundStepSummary[],
): PlaygroundStepMetrics {
  return {
    totalReferenceSteps: steps.filter((item) => item.source !== 'runtime').length,
    observedSteps: steps.filter((item) => item.observationCount > 0).length,
    runningSteps: steps.filter((item) => item.status === 'running').length,
    waitingSteps: steps.filter((item) => item.status === 'waiting').length,
    successfulSteps: steps.filter((item) => item.status === 'success').length,
    failedSteps: steps.filter((item) => item.status === 'error').length,
  };
}
