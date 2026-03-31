import { AGUIEventType, CustomEventName, type AGUIEvent } from '@aevatar-react-sdk/types';
import type {
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorGraphNode,
  WorkflowActorTimelineItem,
} from '@/shared/models/runtime/actors';
import {
  getLatestCustomEventData,
  parseStepCompletedData,
  parseStepRequestData,
  parseWaitingSignalData,
} from '@/shared/agui/customEventData';
import type {
  MissionControlSnapshot,
  MissionControlRouteContext,
  MissionExecutionEvent,
  MissionInterventionState,
  MissionNodeStatus,
  MissionObservationStatus,
  MissionRuntimeConnectionStatus,
  MissionRunStatus,
  MissionTopologyEdge,
  MissionTopologyNode,
  MissionTopologyNodeKind,
} from './models';

type MissionSessionLike = {
  context?: {
    actorId?: string;
    commandId?: string;
    workflowName?: string;
  };
  error?: {
    code?: string;
    message: string;
  };
  lastSnapshot?: unknown;
  pendingHumanInput?: {
    metadata?: Record<string, string>;
    prompt?: string;
    runId?: string;
    stepId?: string;
    suspensionType?: string;
    timeoutSeconds?: number;
  };
  runId?: string;
  status: 'idle' | 'running' | 'finished' | 'error';
};

type BuildRuntimeSnapshotInput = {
  connectionStatus: MissionRuntimeConnectionStatus;
  nowMs: number;
  recentEvents: AGUIEvent[];
  routeContext?: MissionControlRouteContext;
  resources?: {
    artifacts: {
      fetchedAtMs: number;
      graph: WorkflowActorGraphEnrichedSnapshot;
      timeline: WorkflowActorTimelineItem[];
    };
    session: MissionSessionLike;
  };
};

const COMPLETION_STATUS = {
  completed: 1,
  failed: 3,
  stopped: 4,
} as const;

const BLOCKING_TIMELINE_STAGES = new Set(['signal.waiting', 'workflow.suspended']);
const CLEARING_TIMELINE_STAGES = new Set([
  'signal.buffered',
  'workflow.resumed',
  'workflow.completed',
  'workflow.failed',
  'workflow.stopped',
]);

function trimOptional(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function mapTimelineEventType(eventType: string): MissionExecutionEvent['type'] {
  const normalized = eventType.trim().toLowerCase();
  if (!normalized) {
    return 'step_requested';
  }

  if (normalized.includes('execution_started') || normalized.includes('run_started')) {
    return 'workflow_run_execution_started';
  }

  if (normalized.includes('role_reply')) {
    return 'workflow_role_reply_recorded';
  }

  if (normalized.includes('actor_link')) {
    return 'workflow_role_actor_linked';
  }

  if (normalized.includes('signal_buffered')) {
    return 'workflow_signal_buffered';
  }

  if (normalized.includes('waiting_signal') || normalized.includes('wait_signal')) {
    return 'waiting_for_signal';
  }

  if (normalized.includes('suspend')) {
    return 'workflow_suspended';
  }

  if (normalized.includes('resume')) {
    return 'workflow_resumed';
  }

  if (normalized.includes('completed')) {
    return normalized.includes('workflow') ? 'workflow_completed' : 'step_completed';
  }

  if (normalized.includes('stopped')) {
    return normalized.includes('run') ? 'workflow_run_stopped' : 'workflow_stopped';
  }

  return 'step_requested';
}

function parseDateMs(value?: string): number | undefined {
  if (!value) {
    return undefined;
  }

  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function parseTimelineTimestampMs(item: WorkflowActorTimelineItem): number {
  return parseDateMs(item.timestamp) ?? 0;
}

function normalizeTimelineStage(item: WorkflowActorTimelineItem): string {
  return item.stage.trim().toLowerCase();
}

function formatFreshness(ageSeconds?: number): string {
  if (ageSeconds === undefined || !Number.isFinite(ageSeconds)) {
    return 'n/a';
  }

  if (ageSeconds < 60) {
    return `${Math.max(1, Math.round(ageSeconds))}s`;
  }

  if (ageSeconds < 3600) {
    return `${Math.round(ageSeconds / 60)}m`;
  }

  return `${Math.round(ageSeconds / 3600)}h`;
}

function laneForKind(kind: MissionTopologyNodeKind) {
  switch (kind) {
    case 'entrypoint':
      return 'Observe';
    case 'research':
    case 'tool':
      return 'Analyze';
    case 'risk':
    case 'approval':
      return 'Decide';
    case 'execution':
      return 'Execute';
    default:
      return 'Control';
  }
}

function inferKind(node: WorkflowActorGraphNode): MissionTopologyNodeKind {
  const nodeType = node.nodeType.toLowerCase();
  const stepType = (node.properties.stepType || '').toLowerCase();
  const targetRole = (node.properties.targetRole || '').toLowerCase();
  const stepId = (node.properties.stepId || node.nodeId).toLowerCase();

  if (nodeType === 'workflowrun') {
    return 'entrypoint';
  }

  if (nodeType === 'actor') {
    return 'coordinator';
  }

  const signalHints = ['wait_signal', 'signal', 'checkpoint'];
  if (signalHints.some((value) => stepType.includes(value) || stepId.includes(value))) {
    return 'risk';
  }

  const approvalHints = ['approval', 'approve'];
  if (
    approvalHints.some((value) => stepType.includes(value) || targetRole.includes(value) || stepId.includes(value))
  ) {
    return 'approval';
  }

  const executionHints = ['execute', 'dispatch', 'route', 'trade'];
  if (
    executionHints.some((value) => stepType.includes(value) || targetRole.includes(value) || stepId.includes(value))
  ) {
    return 'execution';
  }

  const toolHints = ['tool', 'api', 'connector', 'query'];
  if (
    toolHints.some((value) => stepType.includes(value) || targetRole.includes(value) || stepId.includes(value))
  ) {
    return 'tool';
  }

  return 'research';
}

function observationStatusFromAge(
  connectionStatus: MissionRuntimeConnectionStatus,
  ageSeconds?: number,
  terminal = false,
): MissionObservationStatus {
  if (connectionStatus === 'idle') {
    return 'unavailable';
  }

  if (connectionStatus === 'disconnected') {
    return 'delayed';
  }

  if (terminal) {
    return 'projection_settled';
  }

  if (ageSeconds === undefined || !Number.isFinite(ageSeconds)) {
    return connectionStatus === 'live' ? 'streaming' : 'snapshot_available';
  }

  if (ageSeconds <= 6) {
    return 'streaming';
  }

  if (ageSeconds <= 30) {
    return 'snapshot_available';
  }

  return 'delayed';
}

function eventTimestampMs(event: AGUIEvent): number {
  return typeof event.timestamp === 'number' && Number.isFinite(event.timestamp)
    ? event.timestamp
    : Date.now();
}

function buildActivityMap(
  graph: WorkflowActorGraphEnrichedSnapshot,
  timeline: WorkflowActorTimelineItem[],
  events: AGUIEvent[],
): Map<string, number> {
  const activity = new Map<string, number>();
  const stepNodeIds = new Map<string, string>();
  const graphNodeIds = new Set<string>();

  graph.subgraph.nodes.forEach((node) => {
    graphNodeIds.add(node.nodeId);
    const stepId = node.properties.stepId?.trim();
    if (stepId) {
      stepNodeIds.set(stepId, node.nodeId);
    }
  });

  timeline.forEach((item) => {
    const stamp = parseTimelineTimestampMs(item);
    const agentId = trimOptional(item.agentId);
    const stepId = trimOptional(item.stepId);

    if (agentId && graphNodeIds.has(agentId)) {
      activity.set(agentId, stamp);
    }

    if (stepId) {
      const stepNodeId = stepNodeIds.get(stepId);
      if (stepNodeId) {
        activity.set(stepNodeId, stamp);
      }
    }

    activity.set(graph.snapshot.actorId, stamp);
    activity.set(graph.subgraph.rootNodeId, stamp);
  });

  events.forEach((event) => {
    const stamp = eventTimestampMs(event);

    if (event.type === AGUIEventType.HUMAN_INPUT_REQUEST) {
      const stepNodeId = stepNodeIds.get(event.stepId);
      if (stepNodeId) {
        activity.set(stepNodeId, stamp);
      }
      return;
    }

    if (event.type !== AGUIEventType.CUSTOM) {
      return;
    }

    const stepRequest = parseStepRequestData(event.value);
    if (stepRequest?.stepId) {
      const nodeId = stepNodeIds.get(stepRequest.stepId);
      if (nodeId) {
        activity.set(nodeId, stamp);
      }
    }

    const stepCompleted = parseStepCompletedData(event.value);
    if (stepCompleted?.stepId) {
      const nodeId = stepNodeIds.get(stepCompleted.stepId);
      if (nodeId) {
        activity.set(nodeId, stamp);
      }
    }

    const waitingSignal = parseWaitingSignalData(event.value);
    if (waitingSignal?.stepId) {
      const nodeId = stepNodeIds.get(waitingSignal.stepId);
      if (nodeId) {
        activity.set(nodeId, stamp);
      }
    }
  });

  return activity;
}

function findLatestBlockingTimelineItem(
  timeline: WorkflowActorTimelineItem[],
): WorkflowActorTimelineItem | undefined {
  let latestBlocking: WorkflowActorTimelineItem | undefined;
  let latestBlockingMs = -1;
  let latestClearingMs = -1;

  for (const item of timeline) {
    const stage = normalizeTimelineStage(item);
    const stamp = parseTimelineTimestampMs(item);

    if (BLOCKING_TIMELINE_STAGES.has(stage) && stamp >= latestBlockingMs) {
      latestBlocking = item;
      latestBlockingMs = stamp;
    }

    if (CLEARING_TIMELINE_STAGES.has(stage) && stamp >= latestClearingMs) {
      latestClearingMs = stamp;
    }
  }

  if (!latestBlocking) {
    return undefined;
  }

  return latestClearingMs > latestBlockingMs ? undefined : latestBlocking;
}

function parseSuspensionType(item: WorkflowActorTimelineItem): string | undefined {
  const direct =
    trimOptional(item.data.suspension_type) ||
    trimOptional(item.data.suspensionType) ||
    trimOptional(item.data.type);
  if (direct) {
    return direct;
  }

  const match = item.message.match(/\(([^)]+)\)\s*$/);
  return trimOptional(match?.[1]);
}

function buildTimelineIntervention(
  graph: WorkflowActorGraphEnrichedSnapshot,
  timeline: WorkflowActorTimelineItem[],
): MissionInterventionState | undefined {
  const latestBlocking = findLatestBlockingTimelineItem(timeline);
  if (!latestBlocking) {
    return undefined;
  }

  const stepNodeIds = new Map<string, string>();
  graph.subgraph.nodes.forEach((node) => {
    const stepId = trimOptional(node.properties.stepId);
    if (stepId) {
      stepNodeIds.set(stepId, node.nodeId);
    }
  });

  const stage = normalizeTimelineStage(latestBlocking);
  const stepId = trimOptional(latestBlocking.stepId) || 'runtime-gate';
  const nodeId = stepNodeIds.get(stepId) ?? graph.subgraph.rootNodeId;

  if (stage === 'signal.waiting') {
    const signalName = trimOptional(latestBlocking.data.signal_name) || trimOptional(latestBlocking.message) || 'continue';
    const timeoutMs = Number(latestBlocking.data.timeout_ms);

    return {
      required: true,
      key: `waiting-signal/${stepId}`,
      kind: 'waiting_signal',
      nodeId,
      prompt:
        trimOptional(latestBlocking.data.prompt) ||
        `Runtime is waiting for signal ${signalName} before ${stepId} can continue.`,
      signalName,
      stepId,
      summary: 'Runtime is paused at an external signal gate and cannot continue until the signal arrives.',
      timeoutLabel:
        Number.isFinite(timeoutMs) && timeoutMs > 0
          ? `Times out in ${Math.max(1, Math.round(timeoutMs / 1000))}s`
          : undefined,
      title: `Waiting for ${signalName}`,
      primaryActionLabel: 'Send Signal',
      secondaryActionLabel: 'Inspect Gate',
    };
  }

  const suspensionType = (parseSuspensionType(latestBlocking) || '').toLowerCase();
  const isApproval = suspensionType.includes('approval') || suspensionType.includes('approve');
  const timeoutSeconds = Number(
    latestBlocking.data.timeout_seconds || latestBlocking.data.timeoutSeconds || '',
  );

  return {
    required: true,
    key: `${isApproval ? 'waiting-approval' : 'human-input'}/${stepId}`,
    kind: isApproval ? 'human_approval' : 'human_input',
    nodeId,
    prompt:
      trimOptional(latestBlocking.data.prompt) ||
      trimOptional(latestBlocking.data.reason) ||
      trimOptional(latestBlocking.data.variable_name) ||
      `${stepId} needs operator input before runtime can continue.`,
    stepId,
    summary: isApproval
      ? 'Runtime is paused and waiting for approval before it can enter the execution path.'
      : 'Runtime is paused and waiting for additional operator context before it can continue.',
    timeoutLabel:
      Number.isFinite(timeoutSeconds) && timeoutSeconds > 0
        ? `Times out in ${Math.max(1, Math.round(timeoutSeconds))}s`
        : undefined,
    title: isApproval ? 'Waiting for approval' : 'Input required',
    primaryActionLabel: isApproval ? 'Approve' : 'Resume',
    secondaryActionLabel: isApproval ? 'Reject' : 'Inspect Gate',
  };
}

function buildIntervention(
  _runId: string,
  graph: WorkflowActorGraphEnrichedSnapshot,
  session: MissionSessionLike,
  recentEvents: AGUIEvent[],
  timeline: WorkflowActorTimelineItem[],
): MissionInterventionState | undefined {
  const stepNodeIds = new Map<string, string>();
  graph.subgraph.nodes.forEach((node) => {
    if (node.properties.stepId) {
      stepNodeIds.set(node.properties.stepId, node.nodeId);
    }
  });

  const waitingSignal = getLatestCustomEventData(
    recentEvents,
    CustomEventName.WaitingSignal,
    parseWaitingSignalData,
  );
  if (waitingSignal?.stepId) {
    return {
      required: true,
      key: `waiting-signal/${waitingSignal.stepId}`,
      kind: 'waiting_signal',
      nodeId: stepNodeIds.get(waitingSignal.stepId) ?? graph.subgraph.rootNodeId,
      prompt: waitingSignal.prompt ?? 'Runtime is waiting for an external signal.',
      signalName: waitingSignal.signalName ?? 'continue',
      stepId: waitingSignal.stepId,
      summary: 'Runtime is paused and waiting for an external signal to resume control flow.',
      timeoutLabel:
        typeof waitingSignal.timeoutMs === 'number'
          ? `Times out in ${Math.max(1, Math.round(waitingSignal.timeoutMs / 1000))}s`
          : undefined,
      title: `Waiting for ${waitingSignal.signalName ?? 'signal'}`,
      primaryActionLabel: 'Send Signal',
      secondaryActionLabel: 'Inspect Gate',
    };
  }

  if (!session.pendingHumanInput?.stepId) {
    return buildTimelineIntervention(graph, timeline);
  }

  const suspensionType = (session.pendingHumanInput.suspensionType || '').toLowerCase();
  const isApproval =
    suspensionType.includes('approval') || suspensionType.includes('approve');

  return {
    required: true,
    key: `${isApproval ? 'waiting-approval' : 'human-input'}/${session.pendingHumanInput.stepId}`,
    kind: isApproval ? 'human_approval' : 'human_input',
    nodeId:
      stepNodeIds.get(session.pendingHumanInput.stepId) ?? graph.subgraph.rootNodeId,
    prompt: session.pendingHumanInput.prompt || 'This step requires operator intervention.',
    stepId: session.pendingHumanInput.stepId,
    summary: isApproval
      ? 'Runtime requires approval before it can continue into execution.'
      : 'Runtime requires additional operator context before it can continue.',
    timeoutLabel:
      typeof session.pendingHumanInput.timeoutSeconds === 'number'
        ? `Times out in ${session.pendingHumanInput.timeoutSeconds}s`
        : undefined,
    title: isApproval ? 'Waiting for approval' : 'Input required',
    primaryActionLabel: isApproval ? 'Approve' : 'Resume',
    secondaryActionLabel: isApproval ? 'Reject' : 'Pause',
  };
}

function completionStatusToRunStatus(
  completionStatusValue: number,
  session: MissionSessionLike,
  intervention?: MissionInterventionState,
): MissionRunStatus {
  if (intervention?.kind === 'waiting_signal') {
    return 'waiting_signal';
  }

  if (intervention?.kind === 'human_input') {
    return 'human_input';
  }

  if (intervention?.kind === 'human_approval') {
    return 'waiting_approval';
  }

  if (session.status === 'error' || completionStatusValue === COMPLETION_STATUS.failed) {
    return 'failed';
  }

  if (completionStatusValue === COMPLETION_STATUS.stopped) {
    return 'stopped';
  }

  if (completionStatusValue === COMPLETION_STATUS.completed || session.status === 'finished') {
    return 'completed';
  }

  return 'running';
}

function nodeStatusFromRuntime(
  node: WorkflowActorGraphNode,
  runStatus: MissionRunStatus,
  activityMap: Map<string, number>,
  nowMs: number,
  intervention?: MissionInterventionState,
): MissionNodeStatus {
  if (intervention?.nodeId === node.nodeId) {
    return 'waiting';
  }

  if (runStatus === 'completed') {
    return 'completed';
  }

  const successValue = node.properties.success?.toLowerCase();
  if (successValue === 'true') {
    return 'completed';
  }

  if (successValue === 'false' || runStatus === 'failed') {
    return 'failed';
  }

  const activityAt = activityMap.get(node.nodeId);
  if (activityAt && nowMs - activityAt <= 8_000) {
    return 'active';
  }

  return node.nodeType === 'WorkflowStep' ? 'idle' : 'active';
}

function buildReasoningChain(
  node: WorkflowActorGraphNode,
  relatedTimeline: WorkflowActorTimelineItem[],
): MissionTopologyNode['reasoningChain'] {
  const entries = relatedTimeline.slice(-3);
  if (entries.length === 0) {
    return [
      {
        id: `${node.nodeId}/reasoning/fallback`,
        title: 'No standalone reasoning yet',
        summary: 'This node does not have independent timeline evidence yet, so the inspector is showing graph properties and the latest synchronized state.',
        evidence: Object.entries(node.properties)
          .filter(([, value]) => value.length > 0)
          .slice(0, 4)
          .map(([key, value]) => `${key}: ${value}`),
      },
    ];
  }

  return entries.map((item, index) => {
    const stage = normalizeTimelineStage(item);
    const nodeLabel = node.properties.stepId || node.properties.targetRole || node.nodeId;
    const evidence = [
      trimOptional(item.stepId) ? `stepId: ${item.stepId}` : undefined,
      trimOptional(item.stepType) ? `stepType: ${item.stepType}` : undefined,
      trimOptional(item.agentId) ? `agentId: ${item.agentId}` : undefined,
      ...Object.entries(item.data)
        .slice(0, 3)
        .map(([key, value]) => `${key}: ${value}`),
    ].filter((entry): entry is string => Boolean(entry));

    let title = item.stage || item.eventType || 'Runtime insight';
    let summary = item.message || 'Runtime recorded a topology update.';

    if (stage === 'step.request') {
      title = 'Step requested';
      summary = `${item.stepId || nodeLabel} entered the runtime queue as ${item.stepType || 'workflow step'}.`;
    } else if (stage === 'step.completed') {
      title = 'Step completed';
      summary = `${item.stepId || nodeLabel} finished successfully and advanced the workflow.`;
    } else if (stage === 'step.failed') {
      title = 'Step failed';
      summary = `${item.stepId || nodeLabel} failed and propagated an error signal downstream.`;
    } else if (stage === 'workflow.suspended') {
      title = 'Workflow suspended';
      summary = `${item.stepId || nodeLabel} paused for operator input or approval before continuing.`;
    } else if (stage === 'signal.waiting') {
      title = 'Signal gate waiting';
      summary = `Runtime is blocked on signal ${item.data.signal_name || item.message || 'continue'} before the next branch can continue.`;
    } else if (stage === 'signal.buffered') {
      title = 'Signal buffered';
      summary = `Signal ${item.data.signal_name || item.message || 'continue'} was accepted and queued for workflow resumption.`;
    } else if (stage === 'tool.call') {
      title = 'Tool call recorded';
      summary = `${item.message || 'A tool call'} was materialized by runtime and linked back to this node.`;
    } else if (stage === 'role.reply') {
      title = 'Role reply recorded';
      summary = `Role ${item.message || nodeLabel} produced a reply that can influence downstream decisions.`;
    } else if (stage === 'workflow.completed') {
      title = 'Workflow completed';
      summary = 'Workflow reached a committed terminal state and published a final output.';
    } else if (stage === 'workflow.failed') {
      title = 'Workflow failed';
      summary = 'Workflow ended in a committed failure state and exposed the terminal error.';
    }

    return {
      id: `${node.nodeId}/reasoning/${index}`,
      title,
      summary,
      evidence,
    };
  });
}

function buildToolCalls(
  node: WorkflowActorGraphNode,
  relatedTimeline: WorkflowActorTimelineItem[],
): MissionTopologyNode['toolCalls'] {
  const candidates = relatedTimeline.filter((item) => {
    const normalizedStage = normalizeTimelineStage(item);
    const normalizedEventType = item.eventType.toLowerCase();
    return normalizedStage === 'tool.call' || normalizedEventType.includes('tool');
  });

  if (candidates.length === 0) {
    return [];
  }

  return candidates.slice(-3).map((item, index) => ({
    id: `${node.nodeId}/tool/${index}`,
    toolName:
      trimOptional(item.data.tool_name) ||
      trimOptional(item.data.toolName) ||
      trimOptional(item.message) ||
      item.eventType ||
      'runtime.tool',
    endpoint:
      trimOptional(item.data.endpoint) ||
      trimOptional(item.data.connector) ||
      trimOptional(item.data.call_id) ||
      'runtime.timeline',
    latencyMs:
      Number(
        item.data.latency_ms ||
          item.data.latencyMs ||
          item.data.duration_ms ||
          item.data.durationMs ||
          0,
      ) || 0,
    paramsSummary:
      Object.entries(item.data)
        .filter(([key]) => key !== 'call_id')
        .slice(0, 3)
        .map(([key, value]) => `${key}: ${value}`)
        .join(' · ') || 'Recorded by runtime timeline',
    resultSummary: item.message || 'Tool call recorded.',
    status: item.eventType.toLowerCase().includes('error') ? 'failed' : 'completed',
    summary: item.message || 'Runtime captured a tool invocation.',
  }));
}

function buildNodeSummary(
  node: WorkflowActorGraphNode,
  relatedTimeline: WorkflowActorTimelineItem[],
  snapshot: WorkflowActorGraphEnrichedSnapshot['snapshot'],
): string {
  const latest = relatedTimeline[relatedTimeline.length - 1];
  if (latest?.message) {
    const stage = normalizeTimelineStage(latest);
    if (stage === 'tool.call') {
      return `${latest.message} was invoked and persisted in the runtime timeline.`;
    }

    if (stage === 'role.reply') {
      return `Role ${latest.message} produced a reply for downstream steps.`;
    }

    return latest.message;
  }

  if (snapshot.lastError) {
    return `Runtime exposed terminal error: ${snapshot.lastError}`;
  }

  if (snapshot.lastOutput && (node.nodeId === snapshot.actorId || node.nodeType === 'WorkflowRun')) {
    return `Latest committed output: ${snapshot.lastOutput}`;
  }

  if (node.properties.success?.toLowerCase() === 'true') {
    return `${node.properties.stepId || node.nodeId} completed successfully in the committed graph.`;
  }

  if (node.properties.success?.toLowerCase() === 'false') {
    return `${node.properties.stepId || node.nodeId} finished with a failure signal.`;
  }

  if (node.nodeType === 'WorkflowStep') {
    return `${node.properties.stepType || 'Step'} is materialized in the topology and awaiting newer runtime evidence.`;
  }

  if (node.nodeType === 'Actor') {
    return `Actor ${node.nodeId} is participating in the committed workflow topology.`;
  }

  return `${node.properties.workflowName || node.nodeType} runtime state synchronized.`;
}

function buildNodeSnapshot(
  node: WorkflowActorGraphNode,
  graph: WorkflowActorGraphEnrichedSnapshot,
  session: MissionSessionLike,
): MissionTopologyNode['snapshot'] {
  return {
    headline:
      node.nodeType === 'WorkflowStep'
        ? `${node.properties.stepId || node.nodeId} current state`
        : `${node.nodeId} runtime state`,
    capturedAt: node.updatedAt || graph.snapshot.lastUpdatedAt,
    currentStepId: node.properties.stepId || graph.snapshot.lastCommandId,
    items: {
      ...node.properties,
      actorId: graph.snapshot.actorId,
      completionStatusValue: graph.snapshot.completionStatusValue,
      lastSnapshot: session.lastSnapshot,
    },
    stateVersion: graph.snapshot.stateVersion,
  };
}

function layoutNodes(nodes: MissionTopologyNode[]): MissionTopologyNode[] {
  const laneOrder = ['Observe', 'Control', 'Analyze', 'Decide', 'Execute'];
  const laneY: Record<string, number[]> = {
    Execute: [170],
    Control: [70],
    Decide: [280],
    Observe: [170],
    Analyze: [70, 280],
  };
  const laneCounts = new Map<string, number>();

  return nodes.map((node) => {
    const index = laneCounts.get(node.lane) ?? 0;
    laneCounts.set(node.lane, index + 1);
    const column = laneOrder.indexOf(node.lane);
    const yVariants = laneY[node.lane] ?? [170];
    return {
      ...node,
      position: {
        x: 60 + Math.max(0, column) * 280,
        y: yVariants[index % yVariants.length],
      },
    };
  });
}

function mapTimelineSeverity(eventType: string): MissionExecutionEvent['severity'] {
  const normalized = eventType.toLowerCase();
  if (normalized.includes('error') || normalized.includes('failed')) {
    return 'error';
  }

  if (
    normalized.includes('wait') ||
    normalized.includes('signal') ||
    normalized.includes('approval')
  ) {
    return 'warning';
  }

  if (normalized.includes('completed') || normalized.includes('finished')) {
    return 'success';
  }

  return 'info';
}

function formatTimelineStageLabel(value: string): string {
  const normalized = value.trim().toLowerCase();
  switch (normalized) {
    case 'tool.call':
      return 'Tool Call';
    case 'role.reply':
      return 'Role Reply';
    case 'signal.waiting':
      return 'Waiting for Signal';
    case 'signal.buffered':
      return 'Signal Buffered';
    case 'workflow.suspended':
      return 'Workflow Suspended';
    case 'workflow.completed':
      return 'Workflow Completed';
    case 'workflow.failed':
      return 'Workflow Failed';
    case 'step.request':
      return 'Step Requested';
    case 'step.completed':
      return 'Step Completed';
    default:
      return value;
  }
}

function buildActiveStageLabel(
  timeline: WorkflowActorTimelineItem[],
  intervention: MissionInterventionState | undefined,
  fallback: string,
): string {
  if (intervention?.title) {
    return intervention.title;
  }

  const latest = timeline[timeline.length - 1];
  if (!latest) {
    return fallback;
  }

  const stage = normalizeTimelineStage(latest);
  if (stage === 'tool.call') {
    return latest.message || 'Tool Call';
  }

  if (stage === 'role.reply') {
    return `Role Reply · ${latest.message || 'captured'}`;
  }

  if (trimOptional(latest.stepId)) {
    return `${formatTimelineStageLabel(latest.stage || latest.eventType)} · ${latest.stepId}`;
  }

  return formatTimelineStageLabel(latest.stage || latest.eventType || fallback);
}

export function buildMissionSnapshotFromRuntime(
  input: BuildRuntimeSnapshotInput,
): MissionControlSnapshot {
  if (!input.resources) {
    return buildMissionRuntimePlaceholderSnapshot({
      connectionStatus: input.connectionStatus,
      context: input.routeContext,
      nowMs: input.nowMs,
    });
  }

  const { artifacts, session } = input.resources;
  const intervention = buildIntervention(
    session.runId || artifacts.graph.snapshot.lastCommandId,
    artifacts.graph,
    session,
    input.recentEvents,
    artifacts.timeline,
  );
  const runStatus = completionStatusToRunStatus(
    artifacts.graph.snapshot.completionStatusValue,
    session,
    intervention,
  );
  const activityMap = buildActivityMap(artifacts.graph, artifacts.timeline, input.recentEvents);
  const terminal = runStatus === 'completed';

  const nodes = layoutNodes(
    artifacts.graph.subgraph.nodes.map((node) => {
      const updatedAtMs = parseDateMs(node.updatedAt);
      const ageSeconds =
        updatedAtMs !== undefined ? Math.max(0, (input.nowMs - updatedAtMs) / 1000) : undefined;
      const kind = inferKind(node);
      const relatedTimeline = artifacts.timeline.filter((item) => {
        const stepId = node.properties.stepId;
        if (stepId && item.stepId === stepId) {
          return true;
        }

        return item.agentId === node.nodeId;
      });
      const observationStatus = observationStatusFromAge(
        input.connectionStatus,
        ageSeconds,
        terminal && node.nodeType !== 'WorkflowStep',
      );

      return {
        id: node.nodeId,
        kind,
        confidence:
          typeof node.properties.success === 'string'
            ? node.properties.success === 'true'
              ? 0.92
              : node.properties.success === 'false'
                ? 0.24
                : undefined
            : undefined,
        freshnessLabel: formatFreshness(ageSeconds),
        freshnessSeconds: ageSeconds ?? Number.POSITIVE_INFINITY,
        lane: laneForKind(kind),
        label:
          node.properties.targetRole ||
          node.properties.stepId ||
          node.properties.workflowName ||
          node.nodeId,
        lastLatencyMs:
          Number(
            relatedTimeline[relatedTimeline.length - 1]?.data.durationMs ||
              relatedTimeline[relatedTimeline.length - 1]?.data.latencyMs ||
              0,
          ) || undefined,
        observationStatus,
        position: { x: 0, y: 0 },
        reasoningChain: buildReasoningChain(node, relatedTimeline),
        role:
          node.properties.stepType || node.properties.targetRole || node.nodeType,
        snapshot: buildNodeSnapshot(node, artifacts.graph, session),
        status: nodeStatusFromRuntime(node, runStatus, activityMap, input.nowMs, intervention),
        summary: buildNodeSummary(node, relatedTimeline, artifacts.graph.snapshot),
        toolCalls: buildToolCalls(node, relatedTimeline),
      } satisfies MissionTopologyNode;
    }),
  );

  const nodeById = new Map(nodes.map((node) => [node.id, node]));

  const edges: MissionTopologyEdge[] = artifacts.graph.subgraph.edges.map((edge) => {
    const sourceNode = nodeById.get(edge.fromNodeId);
    const targetNode = nodeById.get(edge.toNodeId);
    const streaming =
      input.connectionStatus === 'live' &&
      ((activityMap.get(edge.fromNodeId) ?? 0) > input.nowMs - 6_000 ||
        (activityMap.get(edge.toNodeId) ?? 0) > input.nowMs - 6_000);

    return {
      id: edge.edgeId,
      label: edge.properties.stepType || edge.edgeType,
      observationStatus:
        targetNode?.observationStatus ||
        sourceNode?.observationStatus ||
        observationStatusFromAge(
          input.connectionStatus,
          parseDateMs(edge.updatedAt)
            ? (input.nowMs - (parseDateMs(edge.updatedAt) || input.nowMs)) / 1000
            : undefined,
          terminal,
        ),
      source: edge.fromNodeId,
      streaming,
      target: edge.toNodeId,
    };
  });

  const timelineTail = artifacts.timeline.slice(-24);
  const events: MissionExecutionEvent[] = timelineTail.map((item, index) => ({
    id: `timeline-${index}-${item.timestamp}`,
    actorId: item.agentId || undefined,
    detail: item.message,
    severity: mapTimelineSeverity(item.eventType),
    stepId: item.stepId || undefined,
    timestamp: item.timestamp,
    title: item.stage || item.eventType || 'Runtime event',
    type: mapTimelineEventType(item.eventType),
  }));

  return {
    summary: {
      activeStageLabel: buildActiveStageLabel(
        artifacts.timeline,
        intervention,
        nodes.find((node) => node.status === 'active')?.label || artifacts.graph.snapshot.workflowName,
      ),
      definitionActorId: artifacts.graph.snapshot.actorId,
      observationStatus: observationStatusFromAge(
        input.connectionStatus,
        Math.max(0, (input.nowMs - artifacts.fetchedAtMs) / 1000),
        terminal,
      ),
      runId: session.runId || artifacts.graph.snapshot.lastCommandId,
      scopeId: input.routeContext?.scopeId || 'runtime',
      startedAt: artifacts.timeline[0]?.timestamp || artifacts.graph.snapshot.lastUpdatedAt,
      status: runStatus,
      updatedAt: artifacts.graph.snapshot.lastUpdatedAt,
      workflowName: artifacts.graph.snapshot.workflowName,
    },
    metrics: [
      {
        key: 'steps',
        label: 'Completed Steps',
        trend: 'steady',
        value: `${artifacts.graph.snapshot.completedSteps}/${artifacts.graph.snapshot.totalSteps}`,
      },
      {
        key: 'replies',
        label: 'Role Replies',
        trend: 'up',
        value: String(artifacts.graph.snapshot.roleReplyCount),
      },
      {
        key: 'state-version',
        label: 'State Version',
        trend: 'steady',
        value: String(artifacts.graph.snapshot.stateVersion),
      },
      {
        key: 'last-success',
        label: 'Last Success',
        tone: artifacts.graph.snapshot.lastSuccess === false ? 'warning' : 'success',
        trend: 'steady',
        value:
          artifacts.graph.snapshot.lastSuccess === null
            ? 'n/a'
            : artifacts.graph.snapshot.lastSuccess
              ? 'true'
              : 'false',
      },
    ],
    nodes,
    edges,
    events,
    intervention,
    liveLogs: timelineTail.map(
      (item) => `[${item.timestamp}] ${item.stage || item.eventType} -> ${item.message}`,
    ),
  };
}

export function buildMissionRuntimePlaceholderSnapshot(input: {
  connectionStatus: MissionRuntimeConnectionStatus;
  context?: MissionControlRouteContext;
  nowMs: number;
}): MissionControlSnapshot {
  const timestamp = new Date(input.nowMs).toISOString();
  const idle = input.connectionStatus === 'idle';
  const observationStatus: MissionObservationStatus = idle
    ? 'unavailable'
    : input.connectionStatus === 'disconnected'
      ? 'delayed'
      : 'snapshot_available';

  return {
    summary: {
      activeStageLabel: idle ? 'Awaiting runtime context' : 'Runtime connection pending',
      definitionActorId: input.context?.actorId || 'n/a',
      observationStatus,
      runId: input.context?.runId || (idle ? 'attach-run' : 'pending'),
      scopeId: input.context?.scopeId || (idle ? 'attach-scope' : 'runtime'),
      startedAt: timestamp,
      status: idle ? 'idle' : 'running',
      updatedAt: timestamp,
      workflowName: idle ? 'Mission Control' : 'Mission Control Runtime',
    },
    metrics: [
      { key: 'steps', label: 'Completed Steps', trend: 'steady', value: '--' },
      { key: 'replies', label: 'Role Replies', trend: 'steady', value: '--' },
      { key: 'state-version', label: 'State Version', trend: 'steady', value: '--' },
      {
        key: 'connection',
        label: 'Connection',
        tone: input.connectionStatus === 'disconnected' ? 'warning' : 'default',
        trend: 'steady',
        value:
          input.connectionStatus === 'idle'
            ? 'Detached'
            : input.connectionStatus === 'connecting'
              ? 'Connecting'
              : input.connectionStatus === 'live'
                ? 'Live'
                : input.connectionStatus === 'degraded'
                  ? 'Fallback Sync'
                  : 'Disconnected',
      },
    ],
    nodes: [],
    edges: [],
    events: [],
    liveLogs: [],
  };
}
