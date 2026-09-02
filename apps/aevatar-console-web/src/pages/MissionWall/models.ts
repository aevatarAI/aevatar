export type MissionWallLiveStatus =
  "live" | "degraded" | "disconnected" | "idle";

export type MissionWallRunStatus =
  | "running"
  | "completed"
  | "waiting"
  | "failed"
  | "timed_out"
  | "retrying"
  | "stopped"
  | "stale"
  | "unknown";

export type MissionWallVisibilityReason =
  "running" | "recently_completed" | "priority_pinned" | "published_workflow";

export type MissionWallPriorityLevel = "none" | "info" | "warning" | "error";

export type MissionWallFocusReason =
  | "failed"
  | "timed_out"
  | "waiting_human"
  | "stale_projection"
  | "stale_live"
  | "retrying"
  | "latest_running"
  | "recently_completed";

export type MissionWallTopologyMode =
  "workflow_step_graph" | "runtime_topology";

export type MissionWallStepStatus =
  | "idle"
  | "active"
  | "completed"
  | "waiting"
  | "failed"
  | "retrying"
  | "unknown";

export interface MissionWallLiveState {
  readonly status: MissionWallLiveStatus;
  readonly message: string;
  readonly lastObservedAt?: string;
  readonly durableFreshnessSeconds?: number;
}

export interface MissionWallSummary {
  readonly runningRuns: number;
  readonly wallVisibleRuns: number;
  readonly waitingHuman: number;
  readonly failedRuns: number;
  readonly retryingRuns: number;
  readonly recentlyCompletedRuns?: number;
  readonly completedToday?: number;
  readonly avgLatencyMs?: number;
  readonly projectionFreshnessSeconds?: number;
}

export interface MissionWallProgress {
  readonly completedSteps: number;
  readonly totalSteps: number;
}

export interface MissionWallRun {
  readonly id: string;
  readonly runId: string;
  readonly commandId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
  readonly teamName?: string;
  readonly entryMemberId?: string;
  readonly entryMemberName?: string;
  readonly publishedServiceId?: string;
  readonly workflowName: string;
  readonly status: MissionWallRunStatus;
  readonly currentStepId?: string;
  readonly currentStepLabel?: string;
  readonly currentMemberId?: string;
  readonly currentMemberName?: string;
  readonly currentInformationCategory?: string;
  readonly startedAt?: string;
  readonly updatedAt?: string;
  readonly durationMs?: number;
  readonly progress?: MissionWallProgress;
  readonly stateVersion?: number;
  readonly lastEventId?: string;
  readonly runtimeActorId?: string;
  readonly hasRuntimeRun?: boolean;
  readonly visibilityReason: MissionWallVisibilityReason;
  readonly visibleUntil?: string;
  readonly priorityLevel: MissionWallPriorityLevel;
  readonly focusPriority: number;
  readonly focusReason?: MissionWallFocusReason;
}

export interface MissionWallFocus {
  readonly runId?: string;
  readonly reason?: MissionWallFocusReason;
  readonly selectedAt?: string;
}

export interface MissionWallWorkflowOverviewStep {
  readonly stepId: string;
  readonly index: number;
  readonly status: MissionWallStepStatus;
}

export interface MissionWallWorkflowLayout {
  readonly engine: "manual" | "elk_layered";
  readonly direction: "right" | "down";
  readonly totalSteps?: number;
  readonly windowStartIndex?: number;
  readonly windowEndIndex?: number;
  readonly viewportStepIds?: readonly string[];
  readonly stepOverview?: readonly MissionWallWorkflowOverviewStep[];
}

export interface MissionWallWorkflowStepNode {
  readonly id: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole?: string;
  readonly parametersSummary?: string;
  readonly status: MissionWallStepStatus;
  readonly focused?: boolean;
  readonly runId?: string;
  readonly runtimeActorId?: string;
  readonly outputPreview?: string;
  readonly error?: string;
  readonly latencyMs?: number;
  readonly position?: {
    readonly x: number;
    readonly y: number;
  };
}

export interface MissionWallWorkflowStepEdge {
  readonly id: string;
  readonly fromStepId: string;
  readonly toStepId: string;
  readonly kind: "next" | "branch";
  readonly branchLabel?: string;
  readonly traversed?: boolean;
  readonly focused?: boolean;
}

export interface MissionWallWorkflowGraph {
  readonly nodes: readonly MissionWallWorkflowStepNode[];
  readonly edges: readonly MissionWallWorkflowStepEdge[];
  readonly layout?: MissionWallWorkflowLayout;
  readonly selectedStepId?: string;
}

export interface MissionWallRuntimeNode {
  readonly id: string;
  readonly label: string;
  readonly kind: string;
  readonly status: MissionWallStepStatus;
  readonly runtimeActorId?: string;
  readonly summary?: string;
}

export interface MissionWallRuntimeEdge {
  readonly id: string;
  readonly source: string;
  readonly target: string;
  readonly label?: string;
  readonly streaming?: boolean;
}

export interface MissionWallRuntimeTopology {
  readonly nodes: readonly MissionWallRuntimeNode[];
  readonly edges: readonly MissionWallRuntimeEdge[];
}

export interface MissionWallTopology {
  readonly scope: "global" | "team" | "run";
  readonly mode: MissionWallTopologyMode;
  readonly selectedRunId?: string;
  readonly workflowGraph?: MissionWallWorkflowGraph;
  readonly runtimeTopology?: MissionWallRuntimeTopology;
}

export interface MissionWallSnapshot {
  readonly generatedAt: string;
  readonly live: MissionWallLiveState;
  readonly summary: MissionWallSummary;
  readonly runs: readonly MissionWallRun[];
  readonly focus: MissionWallFocus;
  readonly topology: MissionWallTopology;
}

export interface MissionWallStepSource {
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole?: string;
  readonly parametersSummary?: string;
  readonly status: MissionWallStepStatus;
  readonly outputPreview?: string;
  readonly error?: string;
  readonly latencyMs?: number;
  readonly nextStepId?: string;
  readonly branchTargets?: Readonly<Record<string, string>>;
}

export interface MissionWallRunSource {
  readonly runId: string;
  readonly commandId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
  readonly teamName?: string;
  readonly entryMemberId?: string;
  readonly entryMemberName?: string;
  readonly publishedServiceId?: string;
  readonly workflowName: string;
  readonly status: MissionWallRunStatus;
  readonly currentStepId?: string;
  readonly currentStepLabel?: string;
  readonly currentMemberId?: string;
  readonly currentMemberName?: string;
  readonly currentInformationCategory?: string;
  readonly startedAt?: string;
  readonly updatedAt?: string;
  readonly durationMs?: number;
  readonly completedSteps: number;
  readonly totalSteps: number;
  readonly windowStartIndex?: number;
  readonly stateVersion?: number;
  readonly lastEventId?: string;
  readonly runtimeActorId?: string;
  readonly hasRuntimeRun?: boolean;
  readonly steps: readonly MissionWallStepSource[];
}

export interface MissionWallSource {
  readonly generatedAt: string;
  readonly live: MissionWallLiveState;
  readonly runs: readonly MissionWallRunSource[];
}
