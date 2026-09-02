import { t } from "@/shared/i18n/messages";
import type {
  MissionWallFocusReason,
  MissionWallRun,
  MissionWallRunSource,
  MissionWallRunStatus,
  MissionWallSnapshot,
  MissionWallSource,
  MissionWallStepStatus,
  MissionWallSummary,
  MissionWallTopology,
  MissionWallVisibilityReason,
  MissionWallWorkflowGraph,
  MissionWallWorkflowStepEdge,
  MissionWallWorkflowStepNode,
} from "./models";

export const COMPLETED_RETENTION_MS = 5 * 60 * 1000;
export const PRIORITY_PIN_RETENTION_MS = 30 * 60 * 1000;
const WORKFLOW_GRAPH_WINDOW_SIZE = 5;

const FOCUS_PRIORITY: Record<MissionWallFocusReason, number> = {
  failed: 1000,
  timed_out: 1000,
  waiting_human: 900,
  stale_projection: 800,
  stale_live: 800,
  retrying: 700,
  latest_running: 500,
  recently_completed: 300,
};

function parseTimeMs(value?: string): number | undefined {
  if (!value) {
    return undefined;
  }

  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function isPriorityStatus(status: MissionWallRunStatus): boolean {
  return (
    status === "failed" ||
    status === "timed_out" ||
    status === "waiting" ||
    status === "retrying" ||
    status === "stale"
  );
}

function resolveVisibilityReason(
  source: MissionWallRunSource,
  nowMs: number,
): MissionWallVisibilityReason {
  const updatedAtMs = parseTimeMs(source.updatedAt);

  if (source.hasRuntimeRun === false) {
    return "published_workflow";
  }

  if (source.status === "running") {
    return "running";
  }

  if (
    source.status === "completed" &&
    updatedAtMs !== undefined &&
    nowMs - updatedAtMs <= COMPLETED_RETENTION_MS
  ) {
    return "recently_completed";
  }

  if (
    isPriorityStatus(source.status) &&
    (updatedAtMs === undefined ||
      nowMs - updatedAtMs <= PRIORITY_PIN_RETENTION_MS)
  ) {
    return "priority_pinned";
  }

  return "published_workflow";
}

function resolveFocusReason(
  source: MissionWallRunSource,
): MissionWallFocusReason | undefined {
  switch (source.status) {
    case "failed":
      return "failed";
    case "timed_out":
      return "timed_out";
    case "waiting":
      return "waiting_human";
    case "stale":
      return "stale_projection";
    case "retrying":
      return "retrying";
    case "running":
      return "latest_running";
    case "completed":
      return "recently_completed";
    default:
      return undefined;
  }
}

function priorityLevelForStatus(
  status: MissionWallRunStatus,
): MissionWallRun["priorityLevel"] {
  if (status === "failed" || status === "timed_out") {
    return "error";
  }

  if (status === "waiting" || status === "retrying" || status === "stale") {
    return "warning";
  }

  if (status === "completed") {
    return "info";
  }

  return "none";
}

function visibleUntilFor(
  source: MissionWallRunSource,
  visibilityReason: MissionWallVisibilityReason,
): string | undefined {
  const updatedAtMs = parseTimeMs(source.updatedAt);
  if (updatedAtMs === undefined) {
    return undefined;
  }

  if (visibilityReason === "recently_completed") {
    return new Date(updatedAtMs + COMPLETED_RETENTION_MS).toISOString();
  }

  if (visibilityReason === "priority_pinned") {
    return new Date(updatedAtMs + PRIORITY_PIN_RETENTION_MS).toISOString();
  }

  return undefined;
}

function toWallRun(
  source: MissionWallRunSource,
  nowMs: number,
): MissionWallRun {
  const visibilityReason = resolveVisibilityReason(source, nowMs);
  const focusReason = resolveFocusReason(source);

  return {
    commandId: source.commandId,
    currentInformationCategory: source.currentInformationCategory,
    currentMemberId: source.currentMemberId,
    currentMemberName: source.currentMemberName,
    currentStepId: source.currentStepId,
    currentStepLabel: source.currentStepLabel,
    durationMs: source.durationMs,
    entryMemberId: source.entryMemberId,
    entryMemberName: source.entryMemberName,
    focusPriority: focusReason ? FOCUS_PRIORITY[focusReason] : 0,
    focusReason,
    hasRuntimeRun: source.hasRuntimeRun,
    id: source.runId,
    lastEventId: source.lastEventId,
    progress: {
      completedSteps: source.completedSteps,
      totalSteps: source.totalSteps,
    },
    priorityLevel: priorityLevelForStatus(source.status),
    publishedServiceId: source.publishedServiceId,
    runId: source.runId,
    runtimeActorId: source.runtimeActorId,
    scopeId: source.scopeId,
    startedAt: source.startedAt,
    stateVersion: source.stateVersion,
    status: source.status,
    teamId: source.teamId,
    teamName: source.teamName,
    updatedAt: source.updatedAt,
    visibilityReason,
    visibleUntil: visibleUntilFor(source, visibilityReason),
    workflowName: source.workflowName,
  };
}

function sortWallRuns(left: MissionWallRun, right: MissionWallRun): number {
  const leftRunningRank = left.status === "running" ? 1 : 0;
  const rightRunningRank = right.status === "running" ? 1 : 0;
  const runningDelta = rightRunningRank - leftRunningRank;
  if (runningDelta !== 0) {
    return runningDelta;
  }

  const priorityDelta = right.focusPriority - left.focusPriority;
  if (priorityDelta !== 0) {
    return priorityDelta;
  }

  return (
    (parseTimeMs(right.updatedAt) ?? 0) - (parseTimeMs(left.updatedAt) ?? 0)
  );
}

function newest(runs: readonly MissionWallRun[]): MissionWallRun | undefined {
  return [...runs].sort(
    (left, right) =>
      (parseTimeMs(right.updatedAt) ?? 0) - (parseTimeMs(left.updatedAt) ?? 0),
  )[0];
}

function oldest(runs: readonly MissionWallRun[]): MissionWallRun | undefined {
  return [...runs].sort(
    (left, right) =>
      (parseTimeMs(left.updatedAt) ?? 0) - (parseTimeMs(right.updatedAt) ?? 0),
  )[0];
}

function stalest(runs: readonly MissionWallRun[]): MissionWallRun | undefined {
  return oldest(runs);
}

export function isWallVisible(run: MissionWallRun): boolean {
  return (
    run.visibilityReason === "running" ||
    run.visibilityReason === "recently_completed" ||
    run.visibilityReason === "priority_pinned" ||
    run.visibilityReason === "published_workflow"
  );
}

function isFocusCandidate(run: MissionWallRun): boolean {
  return (
    run.hasRuntimeRun !== false && run.visibilityReason !== "published_workflow"
  );
}

export function chooseFocusRun(
  runs: readonly MissionWallRun[],
): MissionWallRun | undefined {
  const visible = runs.filter(isFocusCandidate);
  return (
    newest(
      visible.filter(
        (run) =>
          run.focusReason === "failed" || run.focusReason === "timed_out",
      ),
    ) ??
    oldest(visible.filter((run) => run.focusReason === "waiting_human")) ??
    stalest(
      visible.filter(
        (run) =>
          run.focusReason === "stale_projection" ||
          run.focusReason === "stale_live",
      ),
    ) ??
    newest(visible.filter((run) => run.focusReason === "retrying")) ??
    newest(visible.filter((run) => run.focusReason === "latest_running")) ??
    newest(visible.filter((run) => run.focusReason === "recently_completed"))
  );
}

function buildSummary(
  runs: readonly MissionWallRun[],
  source: MissionWallSource,
): MissionWallSummary {
  const wallVisibleRuns = runs.filter(isWallVisible).length;
  const runningRuns = source.runs.filter(
    (run) => run.status === "running",
  ).length;
  const waitingHuman = source.runs.filter(
    (run) => run.status === "waiting",
  ).length;
  const failedRuns = source.runs.filter(
    (run) => run.status === "failed" || run.status === "timed_out",
  ).length;
  const retryingRuns = source.runs.filter(
    (run) => run.status === "retrying",
  ).length;
  const recentlyCompletedRuns = runs.filter(
    (run) => run.visibilityReason === "recently_completed",
  ).length;
  const latencySamples = runs
    .filter((run) => run.hasRuntimeRun !== false)
    .map((run) => run.durationMs)
    .filter((value): value is number => typeof value === "number");
  const avgLatencyMs = latencySamples.length
    ? Math.round(
        latencySamples.reduce((sum, value) => sum + value, 0) /
          latencySamples.length,
      )
    : undefined;

  return {
    avgLatencyMs,
    failedRuns,
    projectionFreshnessSeconds: source.live.durableFreshnessSeconds,
    recentlyCompletedRuns,
    retryingRuns,
    runningRuns,
    waitingHuman,
    wallVisibleRuns,
  };
}

function stepStatusToExecutionStatus(
  status: MissionWallStepStatus,
): "idle" | "active" | "waiting" | "completed" | "failed" {
  if (status === "active") return "active";
  if (status === "waiting") return "waiting";
  if (status === "completed") return "completed";
  if (status === "failed") return "failed";
  if (status === "retrying") return "active";
  return "idle";
}

function isLiveFlowStatus(status: MissionWallRunStatus): boolean {
  return status === "running" || status === "waiting" || status === "retrying";
}

function isFocusedLiveEdge(
  source: MissionWallRunSource,
  fromStepId: string,
  toStepId: string,
): boolean {
  return (
    isLiveFlowStatus(source.status) &&
    (source.currentStepId === fromStepId || source.currentStepId === toStepId)
  );
}

function buildStepEdges(
  nodes: readonly MissionWallWorkflowStepNode[],
  source: MissionWallRunSource,
): MissionWallWorkflowStepEdge[] {
  const nodeIds = new Set(nodes.map((node) => node.stepId));
  const edges: MissionWallWorkflowStepEdge[] = [];
  const connectedStepIds = new Set<string>();

  for (const [index, step] of source.steps.entries()) {
    if (!nodeIds.has(step.stepId)) {
      continue;
    }

    let hasExplicitOutgoingEdge = false;

    if (step.nextStepId && nodeIds.has(step.nextStepId)) {
      edges.push({
        focused: isFocusedLiveEdge(source, step.stepId, step.nextStepId),
        fromStepId: step.stepId,
        id: `edge:${step.stepId}:${step.nextStepId}:next`,
        kind: "next",
        toStepId: step.nextStepId,
        traversed: step.status === "completed",
      });
      connectedStepIds.add(`${step.stepId}->${step.nextStepId}`);
      hasExplicitOutgoingEdge = true;
    }

    for (const [branchLabel, targetStepId] of Object.entries(
      step.branchTargets ?? {},
    )) {
      if (!nodeIds.has(targetStepId)) {
        continue;
      }

      edges.push({
        branchLabel,
        focused: isFocusedLiveEdge(source, step.stepId, targetStepId),
        fromStepId: step.stepId,
        id: `edge:${step.stepId}:${targetStepId}:branch:${branchLabel}`,
        kind: "branch",
        toStepId: targetStepId,
        traversed: step.status === "completed",
      });
      connectedStepIds.add(`${step.stepId}->${targetStepId}`);
      hasExplicitOutgoingEdge = true;
    }

    const nextStep = source.steps[index + 1];
    const shouldUseSequentialFallback =
      !hasExplicitOutgoingEdge &&
      nextStep &&
      nodeIds.has(nextStep.stepId) &&
      !connectedStepIds.has(`${step.stepId}->${nextStep.stepId}`);

    if (shouldUseSequentialFallback) {
      edges.push({
        focused: isFocusedLiveEdge(source, step.stepId, nextStep.stepId),
        fromStepId: step.stepId,
        id: `edge:${step.stepId}:${nextStep.stepId}:sequence`,
        kind: "next",
        toStepId: nextStep.stepId,
        traversed: step.status === "completed",
      });
      connectedStepIds.add(`${step.stepId}->${nextStep.stepId}`);
    }
  }

  return edges;
}

function buildWorkflowGraph(
  source?: MissionWallRunSource,
): MissionWallWorkflowGraph {
  if (!source) {
    return {
      edges: [],
      nodes: [],
    };
  }

  const activeStepIndex = source.steps.findIndex(
    (step) => step.stepId === source.currentStepId,
  );
  const activeIndex = activeStepIndex >= 0 ? activeStepIndex : 0;
  const maxWindowStartIndex = Math.max(
    0,
    source.steps.length - WORKFLOW_GRAPH_WINDOW_SIZE,
  );
  const requestedWindowStartIndex =
    typeof source.windowStartIndex === "number"
      ? Math.max(0, Math.min(source.windowStartIndex, maxWindowStartIndex))
      : undefined;
  const centeredWindowStartIndex = Math.max(
    0,
    activeIndex - Math.floor(WORKFLOW_GRAPH_WINDOW_SIZE / 2),
  );
  const windowStartIndex =
    source.steps.length <= WORKFLOW_GRAPH_WINDOW_SIZE
      ? 0
      : (requestedWindowStartIndex ??
        Math.min(centeredWindowStartIndex, maxWindowStartIndex));
  const windowEndIndex = Math.min(
    source.steps.length - 1,
    windowStartIndex + WORKFLOW_GRAPH_WINDOW_SIZE - 1,
  );
  const defaultViewportSteps = source.steps.slice(
    windowStartIndex,
    windowEndIndex + 1,
  );
  const nodes: MissionWallWorkflowStepNode[] = source.steps.map(
    (step, index) => ({
      error: step.error,
      focused: step.stepId === source.currentStepId,
      id: `step:${step.stepId}`,
      latencyMs: step.latencyMs,
      outputPreview: step.outputPreview,
      parametersSummary: step.parametersSummary,
      position: {
        x: index * 372,
        y:
          step.status === "waiting" || step.status === "failed"
            ? 168
            : index % 2 === 0
              ? 36
              : 92,
      },
      runId: source.runId,
      runtimeActorId: source.runtimeActorId,
      status: step.status,
      stepId: step.stepId,
      stepType: step.stepType,
      targetRole: step.targetRole,
    }),
  );

  return {
    edges: buildStepEdges(nodes, source),
    layout: {
      direction: "right",
      engine: "manual",
      stepOverview: source.steps.map((step, index) => ({
        index,
        status: step.status,
        stepId: step.stepId,
      })),
      totalSteps: source.steps.length,
      viewportStepIds: defaultViewportSteps.map((step) => step.stepId),
      windowEndIndex,
      windowStartIndex,
    },
    nodes,
    selectedStepId: source.currentStepId,
  };
}

export function buildMissionWallSnapshot(
  source: MissionWallSource,
  options?: {
    focusRunId?: string;
    nowMs?: number;
  },
): MissionWallSnapshot {
  const nowMs = options?.nowMs ?? Date.parse(source.generatedAt);
  const runs = source.runs
    .map((run) => toWallRun(run, nowMs))
    .sort(sortWallRuns);
  const focusRun =
    runs.find((run) => run.runId === options?.focusRunId) ??
    chooseFocusRun(runs);
  const sourceFocusRun = source.runs.find(
    (run) => run.runId === focusRun?.runId,
  );
  const selectedAt = new Date(nowMs).toISOString();
  const topology: MissionWallTopology = {
    mode: "workflow_step_graph",
    scope: sourceFocusRun?.teamId ? "team" : "global",
    selectedRunId: focusRun?.runId,
    workflowGraph: buildWorkflowGraph(sourceFocusRun),
  };

  return {
    focus: focusRun
      ? {
          reason: focusRun.focusReason,
          runId: focusRun.runId,
          selectedAt,
        }
      : {},
    generatedAt: source.generatedAt,
    live: source.live,
    runs,
    summary: buildSummary(runs, source),
    topology,
  };
}

export function toStudioExecutionStatus(status: MissionWallStepStatus) {
  return stepStatusToExecutionStatus(status);
}
