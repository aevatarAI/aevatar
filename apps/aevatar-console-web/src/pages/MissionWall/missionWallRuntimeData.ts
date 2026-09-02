import { t } from "@/shared/i18n/messages";
import type {
  StudioWorkflowBoardCompletedNode,
  StudioWorkflowBoardCurrentNode,
  StudioWorkflowBoardFailedNode,
  StudioWorkflowBoardMemberSnapshot,
  StudioWorkflowBoardPendingNode,
  StudioWorkflowBoardSnapshot,
  StudioWorkflowBoardTeamSnapshot,
} from "@/shared/studio/models";
import type {
  MissionWallLiveState,
  MissionWallRunSource,
  MissionWallRunStatus,
  MissionWallSource,
  MissionWallStepSource,
  MissionWallStepStatus,
} from "./models";

type MissionWallWorkflowBoardSourceInput = {
  readonly generatedAt: string;
  readonly live: MissionWallLiveState;
  readonly snapshot?: StudioWorkflowBoardSnapshot;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function normalizeStatus(value: string | null | undefined): string {
  return (
    trimOptional(value)
      .toLowerCase()
      .replace(/[\s-]+/g, "_") || "unknown"
  );
}

function parseTimeMs(value: string | null | undefined): number | undefined {
  const parsed = Date.parse(trimOptional(value));
  return Number.isFinite(parsed) ? parsed : undefined;
}

function positiveFiniteNumber(
  value: number | null | undefined,
): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? value
    : undefined;
}

function positiveDurationBetween(
  startedAt: string | null | undefined,
  endedAt: string | null | undefined,
): number | undefined {
  const startedAtMs = parseTimeMs(startedAt);
  const endedAtMs = parseTimeMs(endedAt);
  if (startedAtMs === undefined || endedAtMs === undefined) {
    return undefined;
  }

  return positiveFiniteNumber(endedAtMs - startedAtMs);
}

export function toMissionWallRunStatus(
  completionStatus: string | null | undefined,
): MissionWallRunStatus {
  switch (normalizeStatus(completionStatus)) {
    case "completed":
    case "done":
    case "succeeded":
    case "success":
      return "completed";
    case "failed":
    case "not_found":
      return "failed";
    case "timed_out":
    case "timeout":
      return "timed_out";
    case "retrying":
    case "retry_pending":
      return "retrying";
    case "waiting":
    case "awaiting_input":
    case "human_input_required":
    case "suspended":
    case "suspension":
      return "waiting";
    case "stopped":
    case "cancelled":
    case "canceled":
    case "disabled":
      return "stopped";
    case "running":
    case "active":
    case "in_progress":
      return "running";
    default:
      return "unknown";
  }
}

function statusLabel(status: MissionWallRunStatus): string {
  switch (status) {
    case "completed":
      return t("pages.missionwall.runtimeData.completed", "Completed");
    case "failed":
      return t("pages.missionwall.runtimeData.failed", "Failed");
    case "retrying":
      return t("pages.missionwall.runtimeData.retrying", "Retrying");
    case "running":
      return t("pages.missionwall.runtimeData.running", "Running");
    case "stopped":
      return t("pages.missionwall.runtimeData.stopped", "Stopped");
    case "timed_out":
      return t("pages.missionwall.runtimeData.timedOut", "Timed out");
    case "waiting":
      return t("pages.missionwall.runtimeData.waiting", "Waiting");
    default:
      return t("pages.missionwall.runtimeData.unknown", "Unknown");
  }
}

function resolveCurrentStep(
  steps: readonly MissionWallStepSource[],
): MissionWallStepSource | undefined {
  return (
    steps.find((step) => step.status === "failed") ??
    steps.find((step) => step.status === "waiting") ??
    steps.find((step) => step.status === "active") ??
    [...steps].reverse().find((step) => step.status === "completed") ??
    steps[0]
  );
}

function toWorkflowBoardStepStatus(
  status: string | null | undefined,
): MissionWallStepStatus {
  switch (normalizeStatus(status)) {
    case "completed":
    case "done":
    case "succeeded":
      return "completed";
    case "failed":
      return "failed";
    case "retrying":
      return "retrying";
    case "running":
    case "active":
    case "in_progress":
      return "active";
    case "waiting":
    case "pending":
    case "queued":
      return "waiting";
    default:
      return "unknown";
  }
}

function toCompletedNodeStep(
  node: StudioWorkflowBoardCompletedNode,
): MissionWallStepSource {
  return {
    latencyMs: positiveFiniteNumber(node.durationMs),
    status: "completed",
    stepId: node.nodeId,
    stepType: "workflow_node",
    targetRole: trimOptional(node.name) || undefined,
  };
}

function toPendingNodeStep(
  node: StudioWorkflowBoardPendingNode,
): MissionWallStepSource {
  return {
    parametersSummary: trimOptional(node.reason) || undefined,
    status: toWorkflowBoardStepStatus(node.status),
    stepId: node.nodeId,
    stepType: "workflow_node",
    targetRole: trimOptional(node.name) || undefined,
  };
}

function toFailedNodeStep(
  node: StudioWorkflowBoardFailedNode,
): MissionWallStepSource {
  return {
    status: "failed",
    stepId: node.nodeId,
    stepType: "workflow_node",
    targetRole: trimOptional(node.name) || undefined,
  };
}

function toCurrentNodeStep(
  node: StudioWorkflowBoardCurrentNode,
): MissionWallStepSource {
  return {
    latencyMs: positiveFiniteNumber(node.durationMs),
    status: toWorkflowBoardStepStatus(node.status),
    stepId: node.nodeId,
    stepType: "workflow_node",
    targetRole: trimOptional(node.name) || undefined,
  };
}

function withSequentialNextSteps(
  steps: readonly MissionWallStepSource[],
): MissionWallStepSource[] {
  return steps.map((step, index) => {
    if (step.nextStepId || step.branchTargets) {
      return step;
    }

    const nextStepId = steps[index + 1]?.stepId;
    return nextStepId ? { ...step, nextStepId } : step;
  });
}

function buildWorkflowBoardSteps(
  member: StudioWorkflowBoardMemberSnapshot,
): MissionWallStepSource[] {
  const stepsById = new Map<string, MissionWallStepSource>();
  const pushStep = (step: MissionWallStepSource): void => {
    const stepId = trimOptional(step.stepId);
    if (!stepId) {
      return;
    }

    stepsById.set(stepId, { ...step, stepId });
  };

  member.completedNodes.forEach((node) => pushStep(toCompletedNodeStep(node)));
  if (member.currentNode) {
    pushStep(toCurrentNodeStep(member.currentNode));
  }
  member.pendingNodes.forEach((node) => pushStep(toPendingNodeStep(node)));
  member.failedNodes.forEach((node) => pushStep(toFailedNodeStep(node)));

  return withSequentialNextSteps([...stepsById.values()]);
}

function workflowBoardMemberUpdatedAt(
  member: StudioWorkflowBoardMemberSnapshot,
  generatedAt: string,
): string | undefined {
  return (
    trimOptional(member.lastNodeUpdatedAt) ||
    trimOptional(member.currentNode?.updatedAt) ||
    trimOptional(member.currentNode?.startedAt) ||
    trimOptional(generatedAt) ||
    undefined
  );
}

function calculateWorkflowBoardDurationMs(
  member: StudioWorkflowBoardMemberSnapshot,
): number | undefined {
  const completedNodesDurationMs = positiveFiniteNumber(
    member.completedNodes.reduce(
      (total, node) => total + (positiveFiniteNumber(node.durationMs) ?? 0),
      0,
    ),
  );

  return (
    positiveFiniteNumber(member.currentNode?.durationMs) ??
    completedNodesDurationMs ??
    positiveDurationBetween(
      member.currentNode?.startedAt,
      member.currentNode?.updatedAt ?? member.lastNodeUpdatedAt,
    )
  );
}

function workflowBoardRunId(
  member: StudioWorkflowBoardMemberSnapshot,
): string {
  const currentExecutionId = trimOptional(member.currentExecutionId);
  if (currentExecutionId) {
    return currentExecutionId;
  }

  const publishedServiceId = trimOptional(member.publishedServiceId);
  if (publishedServiceId) {
    return `published:${publishedServiceId}`;
  }

  return `member:${member.memberId}`;
}

function toWorkflowBoardSource(input: {
  readonly generatedAt: string;
  readonly scopeId: string;
  readonly team: StudioWorkflowBoardTeamSnapshot;
  readonly member: StudioWorkflowBoardMemberSnapshot;
}): MissionWallRunSource {
  const status = toMissionWallRunStatus(input.member.executionStatus);
  const steps = buildWorkflowBoardSteps(input.member);
  const currentStep =
    steps.find((step) => step.stepId === input.member.currentNode?.nodeId) ??
    resolveCurrentStep(steps);
  const hasRuntimeRun = Boolean(trimOptional(input.member.currentExecutionId));
  const displayName =
    trimOptional(input.member.displayName) ||
    trimOptional(input.member.roleSummary) ||
    t("pages.missionwall.runtimeData.unnamedWorkflow", "Unnamed workflow");
  const workflowName =
    trimOptional(input.member.workflowName) ||
    displayName;
  const durationMs = calculateWorkflowBoardDurationMs(input.member);
  const updatedAt = workflowBoardMemberUpdatedAt(input.member, input.generatedAt);

  return {
    completedSteps: Math.max(0, input.member.progress.completedSteps),
    currentMemberId: trimOptional(currentStep?.targetRole) || undefined,
    currentMemberName: trimOptional(currentStep?.targetRole) || undefined,
    currentStepId:
      trimOptional(input.member.currentNode?.nodeId) ||
      currentStep?.stepId,
    currentStepLabel:
      trimOptional(input.member.currentNode?.name) ||
      currentStep?.stepId ||
      (hasRuntimeRun
        ? statusLabel(status)
        : t(
            "pages.missionwall.runtimeData.noRuntimeRun",
            "No visible run",
          )),
    durationMs,
    entryMemberId: trimOptional(input.member.memberId) || undefined,
    entryMemberName: displayName,
    hasRuntimeRun,
    publishedServiceId:
      trimOptional(input.member.publishedServiceId) || undefined,
    runId: workflowBoardRunId(input.member),
    runtimeActorId: trimOptional(input.member.actorId) || undefined,
    scopeId: input.scopeId,
    startedAt: trimOptional(input.member.currentNode?.startedAt) || undefined,
    status,
    steps,
    teamId: trimOptional(input.team.teamId) || undefined,
    teamName:
      trimOptional(input.team.teamName) ||
      trimOptional(input.team.teamId) ||
      undefined,
    totalSteps: Math.max(0, input.member.progress.totalSteps),
    updatedAt,
    workflowName,
  };
}

export function buildMissionWallSourceFromWorkflowBoardSnapshot(
  input: MissionWallWorkflowBoardSourceInput,
): MissionWallSource {
  const scopeId = trimOptional(input.snapshot?.scopeId);
  const runs =
    input.snapshot?.teams.flatMap((team) =>
      team.members.map((member) =>
        toWorkflowBoardSource({
          generatedAt: input.generatedAt,
          member,
          scopeId: scopeId || "",
          team,
        }),
      ),
    ) ?? [];

  return {
    generatedAt: input.generatedAt,
    live: input.live,
    runs,
  };
}

export function freshnessSecondsSince(
  observedAt: string | undefined,
  nowMs: number,
): number | undefined {
  const observedAtMs = parseTimeMs(observedAt);
  if (observedAtMs === undefined) {
    return undefined;
  }

  return Math.max(0, Math.round((nowMs - observedAtMs) / 1000));
}
