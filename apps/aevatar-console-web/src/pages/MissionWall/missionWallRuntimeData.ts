import { t } from "@/shared/i18n/messages";
import type {
  ScopeServiceRunAuditSnapshot,
  ScopeServiceRunCatalogSnapshot,
  ScopeServiceRunSummary,
  ScopeServiceRunAuditStep,
} from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import type {
  StudioMemberSummary,
  StudioTeamSummary,
} from "@/shared/studio/models";
import type {
  MissionWallLiveState,
  MissionWallRunSource,
  MissionWallRunStatus,
  MissionWallSource,
  MissionWallStepSource,
  MissionWallStepStatus,
} from "./models";

export const MISSION_WALL_SERVICE_TAKE = 200;
export const MISSION_WALL_SERVICE_RUN_TAKE = 50;

export type MissionWallServiceRunTarget = {
  readonly member: StudioMemberSummary;
  readonly service: ServiceCatalogSnapshot;
};

type ServiceRunCatalogResult = MissionWallServiceRunTarget & {
  readonly catalog?: ScopeServiceRunCatalogSnapshot;
};

type MissionWallSourceInput = {
  readonly generatedAt: string;
  readonly live: MissionWallLiveState;
  readonly serviceRunCatalogs: readonly ServiceRunCatalogResult[];
  readonly selectedAudit?: ScopeServiceRunAuditSnapshot;
  readonly teams?: readonly StudioTeamSummary[];
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

function runUpdatedAt(run: ScopeServiceRunSummary): string | undefined {
  return (
    trimOptional(run.lastUpdatedAt) ||
    trimOptional(run.boundAt) ||
    trimOptional(run.bindingUpdatedAt) ||
    undefined
  );
}

function runTimestamp(run: ScopeServiceRunSummary): number {
  return parseTimeMs(runUpdatedAt(run)) ?? 0;
}

function calculateDurationMs(run: ScopeServiceRunSummary): number | undefined {
  const startedAtMs = parseTimeMs(run.boundAt);
  const updatedAtMs = parseTimeMs(run.lastUpdatedAt);
  if (startedAtMs === undefined || updatedAtMs === undefined) {
    return undefined;
  }

  const durationMs = updatedAtMs - startedAtMs;
  return durationMs >= 0 ? durationMs : undefined;
}

export function isPublishedWorkflowMember(
  member: StudioMemberSummary,
): boolean {
  const implementationKind = normalizeStatus(member.implementationKind);
  if (implementationKind !== "workflow") {
    return false;
  }

  return Boolean(
    trimOptional(member.publishedServiceId) ||
    trimOptional(member.lastBoundRevisionId),
  );
}

export function filterMissionWallWorkflowMembers(
  members: readonly StudioMemberSummary[],
  teamId?: string,
): StudioMemberSummary[] {
  const normalizedTeamId = trimOptional(teamId);
  return members.filter((member) => {
    if (!isPublishedWorkflowMember(member)) {
      return false;
    }

    return (
      !normalizedTeamId || trimOptional(member.teamId) === normalizedTeamId
    );
  });
}

export function selectMissionWallServiceRunTargets(
  workflowMembers: readonly StudioMemberSummary[],
  services: readonly ServiceCatalogSnapshot[],
): MissionWallServiceRunTarget[] {
  const memberByPublishedServiceId = new Map<string, StudioMemberSummary>();
  workflowMembers.forEach((member) => {
    const publishedServiceId = trimOptional(member.publishedServiceId);
    if (
      !publishedServiceId ||
      memberByPublishedServiceId.has(publishedServiceId)
    ) {
      return;
    }

    memberByPublishedServiceId.set(publishedServiceId, member);
  });

  return services
    .map((service) => {
      const serviceId = trimOptional(service.serviceId);
      if (!serviceId) {
        return null;
      }

      const member = memberByPublishedServiceId.get(serviceId);
      if (!member) {
        return null;
      }

      return { member, service };
    })
    .filter((target): target is MissionWallServiceRunTarget => Boolean(target));
}

export function sortServiceRuns(
  runs: readonly ScopeServiceRunSummary[],
): ScopeServiceRunSummary[] {
  return [...runs].sort((left, right) => {
    const timestampDelta = runTimestamp(right) - runTimestamp(left);
    if (timestampDelta !== 0) {
      return timestampDelta;
    }

    return right.runId.localeCompare(left.runId);
  });
}

export function toMissionWallRunStatus(
  completionStatus: string | null | undefined,
): MissionWallRunStatus {
  switch (normalizeStatus(completionStatus)) {
    case "completed":
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

function stepTimestamp(step: ScopeServiceRunAuditStep): number {
  return parseTimeMs(step.requestedAt) ?? parseTimeMs(step.completedAt) ?? 0;
}

function sortAuditSteps(
  steps: readonly ScopeServiceRunAuditStep[],
): ScopeServiceRunAuditStep[] {
  return [...steps].sort((left, right) => {
    const timestampDelta = stepTimestamp(left) - stepTimestamp(right);
    if (timestampDelta !== 0) {
      return timestampDelta;
    }

    return left.stepId.localeCompare(right.stepId);
  });
}

function toMissionWallStepStatus(
  step: ScopeServiceRunAuditStep,
): MissionWallStepStatus {
  if (step.success === true) {
    return "completed";
  }

  if (step.success === false || trimOptional(step.error)) {
    return "failed";
  }

  if (
    trimOptional(step.suspensionType) ||
    trimOptional(step.suspensionPrompt)
  ) {
    return "waiting";
  }

  if (trimOptional(step.requestedAt) && !trimOptional(step.completedAt)) {
    return "active";
  }

  return "idle";
}

function summarizeStepParameters(
  step: ScopeServiceRunAuditStep,
): string | undefined {
  const entries = Object.entries(step.requestParameters).filter(
    ([key, value]) => trimOptional(key) || trimOptional(value),
  );
  if (!entries.length) {
    return trimOptional(step.targetRole) || undefined;
  }

  return entries
    .slice(0, 2)
    .map(([key, value]) => `${key}: ${value}`)
    .join(" | ");
}

function toMissionWallStepSource(
  step: ScopeServiceRunAuditStep,
): MissionWallStepSource {
  const branchKey = trimOptional(step.branchKey);
  const nextStepId = trimOptional(step.nextStepId);

  return {
    branchTargets:
      branchKey && nextStepId ? { [branchKey]: nextStepId } : undefined,
    error: trimOptional(step.error) || undefined,
    latencyMs:
      typeof step.durationMs === "number" && Number.isFinite(step.durationMs)
        ? step.durationMs
        : undefined,
    nextStepId: branchKey ? undefined : nextStepId || undefined,
    outputPreview: trimOptional(step.outputPreview) || undefined,
    parametersSummary: summarizeStepParameters(step),
    status: toMissionWallStepStatus(step),
    stepId: step.stepId,
    stepType: trimOptional(step.stepType) || "step",
    targetRole: trimOptional(step.targetRole) || undefined,
  };
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

function buildTeamNameById(
  teams: readonly StudioTeamSummary[] | undefined,
): ReadonlyMap<string, string> {
  return new Map(
    (teams ?? [])
      .map(
        (team) =>
          [trimOptional(team.teamId), trimOptional(team.displayName)] as const,
      )
      .filter(([teamId]) => Boolean(teamId)),
  );
}

function withSelectedAudit(
  run: ScopeServiceRunSummary,
  selectedAudit?: ScopeServiceRunAuditSnapshot,
): {
  readonly run: ScopeServiceRunSummary;
  readonly steps: readonly MissionWallStepSource[];
  readonly durationMs?: number;
  readonly commandId?: string;
} {
  if (
    !selectedAudit ||
    selectedAudit.summary.runId !== run.runId ||
    selectedAudit.summary.serviceId !== run.serviceId
  ) {
    return {
      durationMs: calculateDurationMs(run),
      run,
      steps: [],
    };
  }

  const audit = selectedAudit.audit;
  return {
    commandId: trimOptional(audit.commandId) || undefined,
    durationMs:
      typeof audit.durationMs === "number" && Number.isFinite(audit.durationMs)
        ? audit.durationMs
        : calculateDurationMs(selectedAudit.summary),
    run: selectedAudit.summary,
    steps: sortAuditSteps(audit.steps).map(toMissionWallStepSource),
  };
}

function toRunSource(input: {
  readonly member: StudioMemberSummary;
  readonly run: ScopeServiceRunSummary;
  readonly selectedAudit?: ScopeServiceRunAuditSnapshot;
  readonly service: ServiceCatalogSnapshot;
  readonly teamNameById: ReadonlyMap<string, string>;
}): MissionWallRunSource {
  const { commandId, durationMs, run, steps } = withSelectedAudit(
    input.run,
    input.selectedAudit,
  );
  const status = toMissionWallRunStatus(run.completionStatus);
  const currentStep = resolveCurrentStep(steps);
  const teamId = trimOptional(input.member?.teamId) || undefined;
  const serviceId =
    trimOptional(run.serviceId) ||
    trimOptional(input.service.serviceId) ||
    undefined;
  const workflowName =
    trimOptional(run.workflowName) ||
    trimOptional(input.member?.displayName) ||
    trimOptional(input.service.displayName) ||
    t("pages.missionwall.runtimeData.unnamedWorkflow", "Unnamed workflow");

  return {
    commandId,
    completedSteps: Math.max(0, run.completedSteps),
    currentMemberId: trimOptional(currentStep?.targetRole) || undefined,
    currentMemberName: trimOptional(currentStep?.targetRole) || undefined,
    currentStepId: currentStep?.stepId,
    currentStepLabel: currentStep?.stepId || statusLabel(status),
    durationMs,
    entryMemberId: trimOptional(input.member?.memberId) || undefined,
    entryMemberName:
      trimOptional(input.member?.displayName) ||
      trimOptional(input.service.displayName) ||
      undefined,
    lastEventId: trimOptional(run.lastEventId) || undefined,
    hasRuntimeRun: true,
    publishedServiceId: serviceId,
    runId: run.runId,
    runtimeActorId: trimOptional(run.actorId) || undefined,
    scopeId: run.scopeId,
    startedAt: trimOptional(run.boundAt) || undefined,
    stateVersion: Number.isFinite(run.stateVersion)
      ? run.stateVersion
      : undefined,
    status,
    steps,
    teamId,
    teamName: teamId ? input.teamNameById.get(teamId) || teamId : undefined,
    totalSteps: Math.max(0, run.totalSteps),
    updatedAt: runUpdatedAt(run),
    workflowName,
  };
}

function toPublishedWorkflowSource(input: {
  readonly catalog?: ScopeServiceRunCatalogSnapshot;
  readonly member: StudioMemberSummary;
  readonly service: ServiceCatalogSnapshot;
  readonly teamNameById: ReadonlyMap<string, string>;
}): MissionWallRunSource {
  const teamId = trimOptional(input.member.teamId) || undefined;
  const serviceId = trimOptional(input.service.serviceId);
  const displayName =
    trimOptional(input.member.displayName) ||
    trimOptional(input.service.displayName) ||
    t("pages.missionwall.runtimeData.unnamedWorkflow", "Unnamed workflow");
  const updatedAt =
    trimOptional(input.catalog?.runs[0]?.lastUpdatedAt) ||
    trimOptional(input.service.updatedAt) ||
    trimOptional(input.member.updatedAt) ||
    undefined;

  return {
    completedSteps: 0,
    currentStepLabel: t(
      "pages.missionwall.runtimeData.noRuntimeRun",
      "No visible run",
    ),
    entryMemberId: trimOptional(input.member.memberId) || undefined,
    entryMemberName: displayName,
    hasRuntimeRun: false,
    publishedServiceId: serviceId || undefined,
    runId: serviceId
      ? `published:${serviceId}`
      : `member:${input.member.memberId}`,
    scopeId: trimOptional(input.member.scopeId) || undefined,
    status: "unknown",
    steps: [],
    teamId,
    teamName: teamId ? input.teamNameById.get(teamId) || teamId : undefined,
    totalSteps: 0,
    updatedAt,
    workflowName: displayName,
  };
}

export function buildMissionWallSourceFromRuntime(
  input: MissionWallSourceInput,
): MissionWallSource {
  const teamNameById = buildTeamNameById(input.teams);
  const runs = input.serviceRunCatalogs.flatMap(
    ({ catalog, member, service }) => {
      const sortedRuns = sortServiceRuns(catalog?.runs ?? []);
      if (!sortedRuns.length) {
        return [
          toPublishedWorkflowSource({
            catalog,
            member,
            service,
            teamNameById,
          }),
        ];
      }

      return sortedRuns.map((run) =>
        toRunSource({
          member,
          run,
          service,
          selectedAudit: input.selectedAudit,
          teamNameById,
        }),
      );
    },
  );

  return {
    generatedAt: input.generatedAt,
    live: input.live,
    runs,
  };
}

export function latestRunObservedAt(
  catalogs: readonly (ScopeServiceRunCatalogSnapshot | undefined)[],
): string | undefined {
  const latest = catalogs
    .flatMap((catalog) => catalog?.runs ?? [])
    .map(runUpdatedAt)
    .filter((value): value is string => Boolean(value))
    .sort(
      (left, right) => (parseTimeMs(right) ?? 0) - (parseTimeMs(left) ?? 0),
    )[0];

  return latest;
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
