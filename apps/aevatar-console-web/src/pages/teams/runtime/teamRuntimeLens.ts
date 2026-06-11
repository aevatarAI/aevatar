import type {
  ScopeServiceRevisionCatalogSnapshot,
  ScopeServiceRunSummary,
} from "@/shared/models/runtime/scopeServices";
import { getScopeServiceCurrentRevision } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import type { StudioMemberBindingRevision } from "@/shared/studio/models";
import { t } from "@/shared/i18n/messages";

export type TeamRuntimeLens = {
  scopeId: string;
  title: string;
  subtitle: string;
  activeRevision: StudioMemberBindingRevision | null;
  currentService: ServiceCatalogSnapshot | null;
  currentRun: ScopeServiceRunSummary | null;
};

export type TeamRuntimeLensInput = {
  scopeId: string;
  allowServiceFallback?: boolean;
  focusedServiceId: string | null;
  serviceRevisionCatalog: ScopeServiceRevisionCatalogSnapshot | null;
  services: readonly ServiceCatalogSnapshot[];
  runs: readonly ScopeServiceRunSummary[];
  currentRun?: ScopeServiceRunSummary | null;
  workflowCount: number;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function sortRuns(runs: readonly ScopeServiceRunSummary[]): ScopeServiceRunSummary[] {
  return [...runs].sort((left, right) => {
    const leftTime = Date.parse(left.lastUpdatedAt || "");
    const rightTime = Date.parse(right.lastUpdatedAt || "");
    return (Number.isFinite(rightTime) ? rightTime : 0) - (Number.isFinite(leftTime) ? leftTime : 0);
  });
}

function sortServices(
  services: readonly ServiceCatalogSnapshot[],
): ServiceCatalogSnapshot[] {
  return [...services].sort((left, right) => {
    const leftDisplayName = trimOptional(left.displayName);
    const rightDisplayName = trimOptional(right.displayName);
    if (leftDisplayName && rightDisplayName && leftDisplayName !== rightDisplayName) {
      return leftDisplayName.localeCompare(rightDisplayName);
    }

    if (leftDisplayName && !rightDisplayName) {
      return -1;
    }

    if (!leftDisplayName && rightDisplayName) {
      return 1;
    }

    return trimOptional(left.serviceId).localeCompare(trimOptional(right.serviceId));
  });
}

export function selectCurrentTeamRun(
  runs: readonly ScopeServiceRunSummary[],
  options?: {
    preferredRunId?: string | null;
  },
): ScopeServiceRunSummary | null {
  const sortedRuns = sortRuns(runs);
  const preferredRunId = trimOptional(options?.preferredRunId);
  return (
    (preferredRunId
      ? sortedRuns.find((run) => trimOptional(run.runId) === preferredRunId) ?? null
      : null) ||
    sortedRuns[0] ||
    null
  );
}

export function deriveTeamRuntimeLens(
  input: TeamRuntimeLensInput,
): TeamRuntimeLens {
  const allowServiceFallback = input.allowServiceFallback ?? true;
  const activeRevision = getScopeServiceCurrentRevision(input.serviceRevisionCatalog);
  const sortedServices = sortServices(input.services);
  const currentRun =
    input.currentRun !== undefined
      ? input.currentRun
      : selectCurrentTeamRun(input.runs);
  const currentService =
    sortedServices.find(
      (service) => trimOptional(service.serviceId) === trimOptional(input.focusedServiceId),
    ) ||
    sortedServices.find(
      (service) =>
        trimOptional(service.serviceId) ===
        trimOptional(input.serviceRevisionCatalog?.serviceId),
    ) ||
    sortedServices.find(
      (service) => trimOptional(service.serviceId) === trimOptional(currentRun?.serviceId),
    ) ||
    (allowServiceFallback ? sortedServices[0] : null) ||
    null;
  const subtitleParts = [
    input.workflowCount > 0 ? t("pages.teams.teamruntimelens.workflows", "{value1} workflows", { value1: input.workflowCount }) : "",
    input.services.length > 0 ? t("pages.teams.teamruntimelens.services", "{value1} services", { value1: input.services.length }) : "",
  ].filter(Boolean);

  return {
    scopeId: input.scopeId,
    title: t("pages.teams.teamruntimelens.current.team", "Current team"),
    subtitle:
      subtitleParts.length > 0
        ? t("pages.teams.teamruntimelens.team.container", "team container · {value1}", { value1: subtitleParts.join(" / ") })
        : t("pages.teams.teamruntimelens.team.containers.member.bindings", "team containers, member bindings and running signals are summarized here."),
    activeRevision,
    currentService,
    currentRun,
  };
}
