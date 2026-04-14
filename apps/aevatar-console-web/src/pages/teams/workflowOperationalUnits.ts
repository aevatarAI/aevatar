import type { ScopeWorkflowSummary } from "@/shared/models/scopes";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  getStudioScopeBindingCurrentRevision,
  type StudioScopeBindingStatus,
} from "@/shared/studio/models";

export const WORKFLOW_RUNTIME_GUARDRAIL = 12;

export type WorkflowOperationalAttention =
  | "failed"
  | "waiting"
  | "no-bound-service"
  | "no-recent-runs"
  | "runtime-unresolved"
  | "healthy"
  | "draft";

export type WorkflowOperationalUnit = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly attentionLabel: string;
  readonly baselineRun: ScopeServiceRunSummary | null;
  readonly isDraftOnly: boolean;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly matchedService: ServiceCatalogSnapshot | null;
  readonly missingFacts: readonly string[];
  readonly staleHints: {
    readonly runId: boolean;
    readonly serviceId: boolean;
  };
  readonly workflow: ScopeWorkflowSummary;
};

type WorkflowOperationalSignals = {
  readonly runtimeAvailableByServiceId?: ReadonlySet<string>;
  readonly runtimeGuardrailedServiceIds?: ReadonlySet<string>;
  readonly servicesAvailable?: boolean;
};

type ResolveWorkflowOperationalUnitInput = {
  readonly binding?: StudioScopeBindingStatus | null;
  readonly preferredRunId?: string;
  readonly preferredServiceId?: string;
  readonly runs?: readonly ScopeServiceRunSummary[];
  readonly services?: readonly ServiceCatalogSnapshot[];
  readonly signals?: WorkflowOperationalSignals;
  readonly workflow: ScopeWorkflowSummary;
};

type BuildWorkflowOperationalUnitsInput = {
  readonly binding?: StudioScopeBindingStatus | null;
  readonly runsByServiceId?: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly services?: readonly ServiceCatalogSnapshot[];
  readonly signals?: WorkflowOperationalSignals;
  readonly workflows: readonly ScopeWorkflowSummary[];
};

type ServiceMatchResult = {
  readonly matchedService: ServiceCatalogSnapshot | null;
  readonly stalePreferredServiceId: boolean;
};

type RunSelectionResult = {
  readonly baselineRun: ScopeServiceRunSummary | null;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly stalePreferredRunId: boolean;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function normalizeStatus(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function parseTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

function compareRuns(
  left: ScopeServiceRunSummary,
  right: ScopeServiceRunSummary,
): number {
  const rightTime = parseTimestamp(right.lastUpdatedAt);
  const leftTime = parseTimestamp(left.lastUpdatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  if (right.stateVersion !== left.stateVersion) {
    return right.stateVersion - left.stateVersion;
  }

  return right.runId.localeCompare(left.runId);
}

function compareServices(
  left: ServiceCatalogSnapshot,
  right: ServiceCatalogSnapshot,
): number {
  const rightTime = parseTimestamp(right.updatedAt);
  const leftTime = parseTimestamp(left.updatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  return right.serviceId.localeCompare(left.serviceId);
}

function isSuccessfulRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === true) {
    return true;
  }

  return ["completed", "finished", "success", "succeeded"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function isWaitingRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  return [
    "waiting",
    "waiting_approval",
    "waiting_signal",
    "blocked",
    "human_approval",
    "human_input",
    "suspended",
  ].includes(normalizeStatus(run.completionStatus));
}

function isFailedRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (isWaitingRun(run)) {
    return false;
  }

  if (run.lastSuccess === false) {
    return true;
  }

  return ["failed", "error", "stopped", "timed_out", "timedout"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function workflowMatchesBindingRevision(
  workflow: ScopeWorkflowSummary,
  binding: StudioScopeBindingStatus | null | undefined,
): boolean {
  const activeRevision = getStudioScopeBindingCurrentRevision(binding);
  if (!activeRevision) {
    return false;
  }

  const workflowName = trimOptional(workflow.workflowName);
  const revisionId = trimOptional(workflow.activeRevisionId);
  return (
    (workflowName.length > 0 &&
      trimOptional(activeRevision.workflowName) === workflowName) ||
    (revisionId.length > 0 &&
      trimOptional(activeRevision.revisionId) === revisionId)
  );
}

function workflowMatchesService(
  workflow: ScopeWorkflowSummary,
  service: ServiceCatalogSnapshot,
  binding: StudioScopeBindingStatus | null | undefined,
): boolean {
  const workflowServiceKey = trimOptional(workflow.serviceKey);
  const workflowRevisionId = trimOptional(workflow.activeRevisionId);

  if (
    workflowServiceKey.length > 0 &&
    trimOptional(service.serviceKey) === workflowServiceKey
  ) {
    return true;
  }

  if (
    workflowRevisionId.length > 0 &&
    (trimOptional(service.activeServingRevisionId) === workflowRevisionId ||
      trimOptional(service.defaultServingRevisionId) === workflowRevisionId)
  ) {
    return true;
  }

  return (
    trimOptional(binding?.serviceId) === trimOptional(service.serviceId) &&
    workflowMatchesBindingRevision(workflow, binding ?? null)
  );
}

function matchWorkflowOperationalService(input: {
  readonly binding: StudioScopeBindingStatus | null | undefined;
  readonly preferredServiceId?: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly workflow: ScopeWorkflowSummary;
}): ServiceMatchResult {
  const { binding, services, workflow } = input;
  const preferredServiceId = trimOptional(input.preferredServiceId);
  const preferredService =
    preferredServiceId.length > 0
      ? services.find(
          (service) => trimOptional(service.serviceId) === preferredServiceId,
        ) ?? null
      : null;
  if (
    preferredService &&
    workflowMatchesService(workflow, preferredService, binding ?? null)
  ) {
    return {
      matchedService: preferredService,
      stalePreferredServiceId: false,
    };
  }

  const matchedService =
    services
      .filter((service) =>
        workflowMatchesService(workflow, service, binding ?? null),
      )
      .sort(compareServices)[0] ?? null;

  return {
    matchedService,
    stalePreferredServiceId:
      preferredServiceId.length > 0 &&
      preferredService !== null &&
      matchedService?.serviceId !== preferredService.serviceId,
  };
}

function matchesWorkflowRun(
  workflow: ScopeWorkflowSummary,
  run: ScopeServiceRunSummary,
): boolean {
  const workflowName = trimOptional(workflow.workflowName);
  const revisionId = trimOptional(workflow.activeRevisionId);

  return (
    (workflowName.length > 0 && trimOptional(run.workflowName) === workflowName) ||
    (revisionId.length > 0 && trimOptional(run.revisionId) === revisionId)
  );
}

function selectWorkflowOperationalRuns(input: {
  readonly preferredRunId?: string;
  readonly runs: readonly ScopeServiceRunSummary[];
  readonly workflow: ScopeWorkflowSummary;
}): RunSelectionResult {
  const preferredRunId = trimOptional(input.preferredRunId);
  const matchingRuns = input.runs
    .filter((run) => matchesWorkflowRun(input.workflow, run))
    .sort(compareRuns);
  const preferredRun =
    preferredRunId.length > 0
      ? matchingRuns.find((run) => trimOptional(run.runId) === preferredRunId) ??
        null
      : null;
  const latestRun = preferredRun ?? matchingRuns[0] ?? null;
  const baselineRun =
    matchingRuns.find(
      (run) => run.runId !== latestRun?.runId && isSuccessfulRun(run),
    ) ||
    matchingRuns.find((run) => run.runId !== latestRun?.runId) ||
    null;

  return {
    baselineRun,
    latestRun,
    stalePreferredRunId:
      preferredRunId.length > 0 &&
      preferredRun === null &&
      input.runs.some((run) => trimOptional(run.runId) === preferredRunId),
  };
}

function deriveAttention(input: {
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly matchedService: ServiceCatalogSnapshot | null;
  readonly runtimeUnavailable: boolean;
  readonly servicesAvailable: boolean;
  readonly workflow: ScopeWorkflowSummary;
}): Pick<
  WorkflowOperationalUnit,
  "attention" | "attentionDetail" | "attentionLabel" | "isDraftOnly"
> {
  const { latestRun, matchedService, runtimeUnavailable, servicesAvailable, workflow } =
    input;

  if (!servicesAvailable || runtimeUnavailable) {
    return {
      attention: "runtime-unresolved",
      attentionDetail:
        "The service catalog or runtime sample for this workflow-backed team is incomplete right now.",
      attentionLabel: "Runtime unresolved",
      isDraftOnly: false,
    };
  }

  if (latestRun && isFailedRun(latestRun)) {
    return {
      attention: "failed",
      attentionDetail:
        trimOptional(latestRun.lastError) ||
        "The latest team run ended in a failed state.",
      attentionLabel: "Failed",
      isDraftOnly: false,
    };
  }

  if (latestRun && isWaitingRun(latestRun)) {
    return {
      attention: "waiting",
      attentionDetail:
        trimOptional(latestRun.lastError) ||
        "The latest team run is waiting on a human or upstream signal.",
      attentionLabel: "Waiting",
      isDraftOnly: false,
    };
  }

  if (latestRun && isSuccessfulRun(latestRun)) {
    return {
      attention: "healthy",
      attentionDetail: "Recent runtime proof looks healthy for this team.",
      attentionLabel: "Healthy",
      isDraftOnly: false,
    };
  }

  if (matchedService) {
    return {
      attention: "no-recent-runs",
      attentionDetail:
        "This team has a published service, but no recent workflow-specific run is visible yet.",
      attentionLabel: "No recent runs",
      isDraftOnly: false,
    };
  }

  if (trimOptional(workflow.serviceKey)) {
    return {
      attention: "no-bound-service",
      attentionDetail:
        "This workflow advertises a service key, but no matching bound service is visible in the current scope.",
      attentionLabel: "No bound service",
      isDraftOnly: false,
    };
  }

  return {
    attention: "draft",
    attentionDetail:
      "This workflow has not been turned into a live operational team yet.",
    attentionLabel: "Draft",
    isDraftOnly: true,
  };
}

function resolveRuntimeAvailability(input: {
  readonly matchedService: ServiceCatalogSnapshot | null;
  readonly signals: WorkflowOperationalSignals | undefined;
}): boolean {
  const serviceId = trimOptional(input.matchedService?.serviceId);
  if (!serviceId) {
    return false;
  }

  if (input.signals?.runtimeGuardrailedServiceIds?.has(serviceId)) {
    return true;
  }

  const availableSet = input.signals?.runtimeAvailableByServiceId;
  if (!availableSet) {
    return false;
  }

  return !availableSet.has(serviceId);
}

export function collectWorkflowOperationalServiceIds(input: {
  readonly binding?: StudioScopeBindingStatus | null;
  readonly services?: readonly ServiceCatalogSnapshot[];
  readonly workflows: readonly ScopeWorkflowSummary[];
}): string[] {
  const services = input.services ?? [];
  const serviceIds = new Set<string>();

  input.workflows.forEach((workflow) => {
    const matchedService = matchWorkflowOperationalService({
      binding: input.binding ?? null,
      services,
      workflow,
    }).matchedService;
    const serviceId = trimOptional(matchedService?.serviceId);
    if (serviceId) {
      serviceIds.add(serviceId);
    }
  });

  return [...serviceIds];
}

export function resolveWorkflowOperationalUnit(
  input: ResolveWorkflowOperationalUnitInput,
): WorkflowOperationalUnit {
  const services = input.services ?? [];
  const servicesAvailable = input.signals?.servicesAvailable ?? true;
  const serviceMatch = servicesAvailable
    ? matchWorkflowOperationalService({
        binding: input.binding ?? null,
        preferredServiceId: input.preferredServiceId,
        services,
        workflow: input.workflow,
      })
    : {
        matchedService: null,
        stalePreferredServiceId: trimOptional(input.preferredServiceId).length > 0,
      };
  const runtimeUnavailable = resolveRuntimeAvailability({
    matchedService: serviceMatch.matchedService,
    signals: input.signals,
  });
  const runSelection =
    serviceMatch.matchedService && !runtimeUnavailable
      ? selectWorkflowOperationalRuns({
          preferredRunId: input.preferredRunId,
          runs: input.runs ?? [],
          workflow: input.workflow,
        })
      : {
          baselineRun: null,
          latestRun: null,
          stalePreferredRunId: trimOptional(input.preferredRunId).length > 0,
        };
  const missingFacts: string[] = [];
  if (!servicesAvailable) {
    missingFacts.push("Service catalog unavailable");
  }
  if (runtimeUnavailable) {
    missingFacts.push("Runtime signal unavailable");
  }
  const attention = deriveAttention({
    latestRun: runSelection.latestRun,
    matchedService: serviceMatch.matchedService,
    runtimeUnavailable,
    servicesAvailable,
    workflow: input.workflow,
  });

  return {
    attention: attention.attention,
    attentionDetail: attention.attentionDetail,
    attentionLabel: attention.attentionLabel,
    baselineRun: runSelection.baselineRun,
    isDraftOnly: attention.isDraftOnly,
    latestRun: runSelection.latestRun,
    matchedService: serviceMatch.matchedService,
    missingFacts,
    staleHints: {
      runId: runSelection.stalePreferredRunId,
      serviceId: serviceMatch.stalePreferredServiceId,
    },
    workflow: input.workflow,
  };
}

const attentionPriority: Record<WorkflowOperationalAttention, number> = {
  failed: 0,
  waiting: 1,
  "no-bound-service": 2,
  "no-recent-runs": 3,
  "runtime-unresolved": 4,
  healthy: 5,
  draft: 6,
};

export function sortWorkflowOperationalUnits(
  units: readonly WorkflowOperationalUnit[],
): WorkflowOperationalUnit[] {
  return [...units].sort((left, right) => {
    const priorityDelta =
      attentionPriority[left.attention] - attentionPriority[right.attention];
    if (priorityDelta !== 0) {
      return priorityDelta;
    }

    const rightTimestamp = Math.max(
      parseTimestamp(right.latestRun?.lastUpdatedAt),
      parseTimestamp(right.matchedService?.updatedAt),
      parseTimestamp(right.workflow.updatedAt),
    );
    const leftTimestamp = Math.max(
      parseTimestamp(left.latestRun?.lastUpdatedAt),
      parseTimestamp(left.matchedService?.updatedAt),
      parseTimestamp(left.workflow.updatedAt),
    );
    if (rightTimestamp !== leftTimestamp) {
      return rightTimestamp - leftTimestamp;
    }

    return right.workflow.workflowId.localeCompare(left.workflow.workflowId);
  });
}

export function buildWorkflowOperationalUnits(
  input: BuildWorkflowOperationalUnitsInput,
): WorkflowOperationalUnit[] {
  const units = input.workflows.map((workflow) => {
    const unitWithoutRuns = resolveWorkflowOperationalUnit({
      binding: input.binding ?? null,
      services: input.services ?? [],
      signals: input.signals,
      workflow,
    });
    const serviceId = trimOptional(unitWithoutRuns.matchedService?.serviceId);

    return resolveWorkflowOperationalUnit({
      binding: input.binding ?? null,
      runs:
        serviceId.length > 0
          ? input.runsByServiceId?.[serviceId] ?? []
          : [],
      services: input.services ?? [],
      signals: input.signals,
      workflow,
    });
  });

  return sortWorkflowOperationalUnits(units);
}

export function findWorkflowOperationalUnit(
  units: readonly WorkflowOperationalUnit[],
  workflowId: string,
): WorkflowOperationalUnit | null {
  const normalizedWorkflowId = trimOptional(workflowId);
  if (!normalizedWorkflowId) {
    return null;
  }

  return (
    units.find((unit) => trimOptional(unit.workflow.workflowId) === normalizedWorkflowId) ??
    null
  );
}
