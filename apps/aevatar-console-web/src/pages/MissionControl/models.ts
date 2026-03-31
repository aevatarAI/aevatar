export const workflowExecutionEventTypes = [
  'workflow_run_execution_started',
  'step_requested',
  'step_completed',
  'workflow_suspended',
  'workflow_resumed',
  'workflow_completed',
  'workflow_stopped',
  'workflow_run_stopped',
  'waiting_for_signal',
  'workflow_signal_buffered',
  'workflow_role_reply_recorded',
  'workflow_role_actor_linked',
] as const;

export type WorkflowExecutionEventType =
  (typeof workflowExecutionEventTypes)[number];

export const scriptEvolutionStatuses = [
  'pending',
  'proposed',
  'build_requested',
  'validated',
  'validation_failed',
  'rejected',
  'promotion_failed',
  'promoted',
  'rollback_requested',
  'rolled_back',
] as const;

export type ScriptEvolutionStatus = (typeof scriptEvolutionStatuses)[number];

export type MissionRunStatus =
  | 'idle'
  | 'draft'
  | 'published'
  | 'running'
  | 'waiting_signal'
  | 'human_input'
  | 'waiting_approval'
  | 'suspended'
  | 'completed'
  | 'failed'
  | 'stopped';

export type MissionObservationStatus =
  | 'unavailable'
  | 'streaming'
  | 'snapshot_available'
  | 'projection_settled'
  | 'delayed';

export type MissionNodeStatus =
  | 'idle'
  | 'active'
  | 'waiting'
  | 'completed'
  | 'failed';

export type MissionTopologyNodeKind =
  | 'entrypoint'
  | 'coordinator'
  | 'research'
  | 'tool'
  | 'risk'
  | 'approval'
  | 'execution';

export type MissionInspectorMode = 'node' | 'intervention';

export type MissionInspectorPresentation = 'overlay' | 'push';

export type MissionInterventionKind =
  | 'waiting_signal'
  | 'human_input'
  | 'human_approval';

export type MissionRuntimeConnectionStatus =
  | 'idle'
  | 'connecting'
  | 'live'
  | 'degraded'
  | 'disconnected';

export type MissionInterventionActionKind =
  | 'approve'
  | 'reject'
  | 'resume'
  | 'signal';

export type MissionFeedbackTone = 'info' | 'success' | 'warning' | 'error';

export interface MissionMetric {
  key: string;
  label: string;
  value: string;
  trend?: 'up' | 'down' | 'steady';
  tone?: 'default' | 'success' | 'warning' | 'danger';
}

export interface MissionToolCall {
  id: string;
  toolName: string;
  endpoint: string;
  status: 'queued' | 'running' | 'completed' | 'failed';
  latencyMs: number;
  paramsSummary: string;
  resultSummary: string;
  summary: string;
}

export interface MissionStateSnapshot {
  headline: string;
  currentStepId: string;
  stateVersion: number;
  capturedAt: string;
  items: Record<string, unknown>;
}

export interface MissionReasoningInsight {
  id: string;
  title: string;
  summary: string;
  evidence: string[];
  confidence?: number;
}

export interface MissionCanvasPosition {
  x: number;
  y: number;
}

export interface MissionTopologyNode {
  id: string;
  label: string;
  role: string;
  lane: string;
  kind: MissionTopologyNodeKind;
  status: MissionNodeStatus;
  observationStatus: MissionObservationStatus;
  freshnessLabel: string;
  freshnessSeconds: number;
  summary: string;
  lastLatencyMs?: number;
  confidence?: number;
  position: MissionCanvasPosition;
  snapshot: MissionStateSnapshot;
  toolCalls: MissionToolCall[];
  reasoningChain: MissionReasoningInsight[];
}

export interface MissionTopologyEdge {
  id: string;
  source: string;
  target: string;
  label?: string;
  observationStatus: MissionObservationStatus;
  streaming: boolean;
}

export interface MissionExecutionEvent {
  id: string;
  type: WorkflowExecutionEventType;
  title: string;
  detail: string;
  stepId?: string;
  actorId?: string;
  timestamp: string;
  severity: 'info' | 'success' | 'warning' | 'error';
}

export interface MissionInterventionState {
  required: boolean;
  key: string;
  kind: MissionInterventionKind;
  nodeId: string;
  signalName?: string;
  title: string;
  summary: string;
  stepId: string;
  prompt: string;
  timeoutLabel?: string;
  primaryActionLabel: string;
  secondaryActionLabel?: string;
}

export interface MissionRunSummary {
  runId: string;
  workflowName: string;
  scopeId: string;
  definitionActorId: string;
  status: MissionRunStatus;
  observationStatus: MissionObservationStatus;
  startedAt: string;
  updatedAt: string;
  activeStageLabel: string;
  scriptEvolutionStatus?: ScriptEvolutionStatus;
}

export interface MissionControlSnapshot {
  summary: MissionRunSummary;
  metrics: MissionMetric[];
  nodes: MissionTopologyNode[];
  edges: MissionTopologyEdge[];
  events: MissionExecutionEvent[];
  liveLogs: string[];
  intervention?: MissionInterventionState;
}

export interface MissionControlRouteContext {
  actorId?: string;
  endpointId?: string;
  prompt?: string;
  runId?: string;
  scopeId?: string;
  serviceId?: string;
  autoStream?: boolean;
}

export interface MissionInterventionActionRequest {
  comment?: string;
  kind: MissionInterventionActionKind;
  payload?: string;
}

export interface MissionInterventionActionResult {
  accepted: boolean;
  commandId?: string;
  kind: MissionInterventionActionKind;
  runId?: string;
  signalName?: string;
  stepId?: string;
}

export interface MissionActionFeedback {
  message: string;
  tone: MissionFeedbackTone;
}

export interface MissionRuntimeViewState {
  actionFeedback?: MissionActionFeedback;
  connectionMessage?: string;
  connectionStatus: MissionRuntimeConnectionStatus;
  liveMode: boolean;
  loading: boolean;
  resuming: boolean;
  signaling: boolean;
  snapshot: MissionControlSnapshot;
  submittingActionKind?: MissionInterventionActionKind;
}
