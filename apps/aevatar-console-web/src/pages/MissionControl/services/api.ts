import type {
  AGUIEvent,
  ChatRunRequest,
  RunContextData,
  WorkflowResumeRequest,
  WorkflowSignalRequest,
} from '@aevatar-react-sdk/types';
import type {
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorTimelineItem,
} from '@/shared/models/runtime/actors';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeActorsApi } from '@/shared/api/runtimeActorsApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import type {
  MissionControlRouteContext,
  MissionInterventionActionRequest,
  MissionInterventionActionResult,
  MissionInterventionState,
} from '../models';

export interface MissionControlRuntimeArtifacts {
  actorId: string;
  graph: WorkflowActorGraphEnrichedSnapshot;
  fetchedAtMs: number;
  timeline: WorkflowActorTimelineItem[];
}

export interface MissionObservedRunContext {
  actorId?: string;
  commandId?: string;
  workflowName?: string;
}

function readBoolean(value: string | null): boolean | undefined {
  if (value === null) {
    return undefined;
  }

  const normalized = value.trim().toLowerCase();
  if (normalized === 'true' || normalized === '1') {
    return true;
  }

  if (normalized === 'false' || normalized === '0') {
    return false;
  }

  return undefined;
}

function trimOptional(value: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

export function readMissionControlRouteContext(
  search = typeof window === 'undefined' ? '' : window.location.search,
): MissionControlRouteContext {
  const params = new URLSearchParams(search);
  return {
    actorId: trimOptional(params.get('actorId')),
    autoStream: readBoolean(params.get('autoStream')),
    endpointId: trimOptional(params.get('endpointId')) || 'chat',
    prompt: trimOptional(params.get('prompt')),
    runId: trimOptional(params.get('runId')),
    scopeId: trimOptional(params.get('scopeId')),
    serviceId: trimOptional(params.get('serviceId')),
  };
}

export function hasMissionControlLiveContext(
  context: MissionControlRouteContext,
): boolean {
  return Boolean(context.actorId || (context.scopeId && context.runId));
}

export async function fetchMissionControlRuntimeArtifacts(
  context: MissionControlRouteContext,
): Promise<MissionControlRuntimeArtifacts> {
  const actorId =
    context.actorId ||
    (
      await (async () => {
        if (!context.scopeId || !context.runId) {
          return undefined;
        }

        const summary = await runtimeRunsApi.getRunSummary(
          context.scopeId,
          context.runId,
          {
            actorId: context.actorId,
            serviceId: context.serviceId,
          },
        );
        return summary.actorId?.trim() || undefined;
      })()
    );
  if (!actorId) {
    throw new Error('Missing actor identity. Mission Control cannot load runtime data.');
  }

  const [graph, timeline] = await Promise.all([
    runtimeActorsApi.getActorGraphEnriched(actorId, {
      depth: 4,
      direction: 'Both',
      take: 240,
    }),
    runtimeActorsApi.getActorTimeline(actorId, {
      take: 240,
    }),
  ]);

  return {
    actorId,
    fetchedAtMs: Date.now(),
    graph,
    timeline,
  };
}

export async function* streamMissionControlEvents(
  context: MissionControlRouteContext,
  signal: AbortSignal,
): AsyncGenerator<AGUIEvent, void, undefined> {
  if (!context.scopeId || !context.prompt) {
    return;
  }

  const response = await runtimeRunsApi.streamChat(
    context.scopeId,
    {
      prompt: context.prompt,
      metadata: undefined,
    } satisfies ChatRunRequest,
    signal,
    {
      serviceId: context.serviceId,
    },
  );

  for await (const event of parseBackendSSEStream(response, { signal })) {
    yield event;
  }
}

export function readMissionObservedRunContext(
  event: AGUIEvent,
): MissionObservedRunContext | undefined {
  if (event.type !== 'CUSTOM' || event.name !== 'aevatar.run.context') {
    return undefined;
  }

  const value = event.value as RunContextData | undefined;
  if (!value) {
    return undefined;
  }

  return {
    actorId: value.actorId?.trim() || undefined,
    commandId: value.commandId?.trim() || undefined,
    workflowName: value.workflowName?.trim() || undefined,
  };
}

function buildResumeRequest(
  context: MissionControlRouteContext,
  intervention: MissionInterventionState,
  action: MissionInterventionActionRequest,
): WorkflowResumeRequest {
  return {
    actorId: context.actorId || '',
    approved: action.kind !== 'reject',
    commandId: undefined,
    metadata: undefined,
    runId: context.runId ?? '',
    stepId: intervention.stepId,
    userInput: action.comment?.trim() || undefined,
  };
}

function buildSignalRequest(
  context: MissionControlRouteContext,
  intervention: MissionInterventionState,
  action: MissionInterventionActionRequest,
): WorkflowSignalRequest {
  return {
    actorId: context.actorId || '',
    commandId: undefined,
    payload: action.payload?.trim() || undefined,
    runId: context.runId ?? '',
    signalName: intervention.signalName ?? 'continue',
    stepId: intervention.stepId,
  };
}

export async function submitMissionControlIntervention(
  context: MissionControlRouteContext,
  intervention: MissionInterventionState,
  action: MissionInterventionActionRequest,
): Promise<MissionInterventionActionResult> {
  if (!context.actorId) {
    throw new Error('Missing actor identity. Mission Control cannot submit actions.');
  }

  if (!context.scopeId) {
    throw new Error('Missing scope identity. Mission Control cannot submit actions.');
  }

  if (!context.runId) {
    throw new Error('Missing run identity. Mission Control cannot submit actions.');
  }

  if (action.kind === 'signal') {
    const result = await runtimeRunsApi.signal(
      context.scopeId,
      buildSignalRequest(context, intervention, action),
      {
        serviceId: context.serviceId,
      },
    );
    return {
      accepted: result.accepted,
      commandId: result.commandId,
      kind: action.kind,
      runId: result.runId,
      signalName: result.signalName,
      stepId: result.stepId,
    };
  }

  const result = await runtimeRunsApi.resume(
    context.scopeId,
    buildResumeRequest(context, intervention, action),
    {
      serviceId: context.serviceId,
    },
  );

  return {
    accepted: result.accepted,
    commandId: result.commandId,
    kind: action.kind,
    runId: result.runId,
    stepId: result.stepId,
  };
}
