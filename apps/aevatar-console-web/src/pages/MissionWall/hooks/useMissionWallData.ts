import { useQueries, useQuery } from "@tanstack/react-query";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { t } from "@/shared/i18n/messages";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import type { ScopeServiceRunAuditSnapshot } from "@/shared/models/runtime/scopeServices";
import { studioApi } from "@/shared/studio/api";
import type {
  StudioMemberSummary,
  StudioTeamSummary,
} from "@/shared/studio/models";
import type {
  MissionWallLiveState,
  MissionWallRun,
  MissionWallSource,
} from "../models";
import {
  buildMissionWallSourceFromRuntime,
  filterMissionWallWorkflowMembers,
  freshnessSecondsSince,
  latestRunObservedAt,
  MISSION_WALL_SERVICE_RUN_TAKE,
  MISSION_WALL_SERVICE_TAKE,
  missionWallRunAuditKey,
  selectMissionWallServiceRunTargets,
  toMissionWallRunStatus,
} from "../missionWallRuntimeData";

type MissionWallRouteOptions = {
  readonly focusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
};

type RuntimeDataVersionInput = {
  readonly authUpdatedAt: number;
  readonly memberUpdatedAt: number;
  readonly runQueryVersion: string;
  readonly serviceUpdatedAt: number;
  readonly teamUpdatedAt: number;
};

export const MISSION_WALL_ROSTER_REFETCH_INTERVAL_MS = 15_000;
export const MISSION_WALL_RUN_REFETCH_INTERVAL_MS = 5_000;
export const MISSION_WALL_AUDIT_REFETCH_INTERVAL_MS = 3_000;

export interface MissionWallRuntimeData {
  readonly buildSource: () => MissionWallSource;
  readonly generatedAt: string;
  readonly isLoading: boolean;
  readonly live: MissionWallLiveState;
  readonly routeFocusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
  readonly workflowMembers: readonly StudioMemberSummary[];
}

type MissionWallRunAuditTarget = {
  readonly actorId?: string;
  readonly runId: string;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly status: MissionWallRun["status"];
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function parseRouteOptions(locationSnapshot: string): MissionWallRouteOptions {
  const queryIndex = locationSnapshot.indexOf("?");
  const hashIndex = locationSnapshot.indexOf("#");
  const search =
    queryIndex >= 0
      ? locationSnapshot.slice(
          queryIndex,
          hashIndex > queryIndex ? hashIndex : undefined,
        )
      : "";
  const params = new URLSearchParams(search);

  return {
    focusRunId: trimOptional(params.get("focusRunId")) || undefined,
    scopeId: trimOptional(params.get("scopeId")) || undefined,
    teamId: trimOptional(params.get("teamId")) || undefined,
  };
}

function runtimeDataVersion(input: RuntimeDataVersionInput): string {
  return [
    input.authUpdatedAt,
    input.memberUpdatedAt,
    input.serviceUpdatedAt,
    input.teamUpdatedAt,
    input.runQueryVersion,
  ].join(":");
}

function missionWallRefetchInterval(intervalMs: number): number | false {
  const isTest =
    typeof process !== "undefined" && process.env.NODE_ENV === "test";
  return isTest ? false : intervalMs;
}

function shouldBackfillRunAudit(target: MissionWallRunAuditTarget): boolean {
  return (
    target.status === "completed" ||
    target.status === "failed" ||
    target.status === "timed_out" ||
    target.status === "running" ||
    target.status === "waiting" ||
    target.status === "retrying"
  );
}

function auditRefetchIntervalForStatus(
  status: MissionWallRun["status"],
): number | false {
  if (status === "running" || status === "waiting" || status === "retrying") {
    return missionWallRefetchInterval(MISSION_WALL_AUDIT_REFETCH_INTERVAL_MS);
  }

  return false;
}

function buildLiveState(input: {
  readonly allRunsLoaded: boolean;
  readonly generatedAt: string;
  readonly hasCriticalError: boolean;
  readonly hasPartialRunError: boolean;
  readonly isLoading: boolean;
  readonly latestObservedAt?: string;
  readonly runCount: number;
  readonly scopeId?: string;
}): MissionWallLiveState {
  const generatedAtMs = Date.parse(input.generatedAt);
  const durableFreshnessSeconds = freshnessSecondsSince(
    input.latestObservedAt,
    Number.isFinite(generatedAtMs) ? generatedAtMs : Date.now(),
  );

  if (input.isLoading) {
    return {
      durableFreshnessSeconds,
      lastObservedAt: input.latestObservedAt,
      message: t(
        "pages.missionwall.liveState.loading",
        "Loading published workflow runs.",
      ),
      status: "idle",
    };
  }

  if (input.hasCriticalError || !input.scopeId) {
    return {
      durableFreshnessSeconds,
      lastObservedAt: input.latestObservedAt,
      message: t(
        "pages.missionwall.liveState.scopeUnavailable",
        "Mission wall could not load the authenticated scope.",
      ),
      status: "disconnected",
    };
  }

  if (input.hasPartialRunError) {
    return {
      durableFreshnessSeconds,
      lastObservedAt: input.latestObservedAt,
      message: t(
        "pages.missionwall.liveState.partialRunError",
        "Some published service runs could not be loaded.",
      ),
      status: "degraded",
    };
  }

  if (input.allRunsLoaded && input.runCount === 0) {
    return {
      durableFreshnessSeconds,
      lastObservedAt: input.latestObservedAt,
      message: t(
        "pages.missionwall.liveState.empty",
        "No published workflow runs are visible yet.",
      ),
      status: "idle",
    };
  }

  return {
    durableFreshnessSeconds,
    lastObservedAt: input.latestObservedAt,
    message: t(
      "pages.missionwall.liveState.connected",
      "Connected to published workflow run read models.",
    ),
    status: "live",
  };
}

export function useMissionWallRuntimeData(): MissionWallRuntimeData {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    getLocationSnapshot,
  );
  const routeOptions = React.useMemo(
    () => parseRouteOptions(locationSnapshot),
    [locationSnapshot],
  );
  const authSessionQuery = useQuery({
    queryFn: () => studioApi.getAuthSession(),
    queryKey: ["mission-wall", "auth-session"],
    retry: false,
  });
  const sessionScopeContext = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );
  const scopeId = routeOptions.scopeId ?? sessionScopeContext?.scopeId;
  const membersQuery = useQuery({
    enabled: Boolean(scopeId),
    queryFn: () => studioApi.listMembers(scopeId ?? ""),
    queryKey: ["mission-wall", "members", scopeId],
    refetchInterval: missionWallRefetchInterval(
      MISSION_WALL_ROSTER_REFETCH_INTERVAL_MS,
    ),
    refetchIntervalInBackground: true,
    retry: false,
  });
  const teamsQuery = useQuery({
    enabled: Boolean(scopeId),
    queryFn: () => studioApi.listTeams(scopeId ?? ""),
    queryKey: ["mission-wall", "teams", scopeId],
    refetchInterval: missionWallRefetchInterval(
      MISSION_WALL_ROSTER_REFETCH_INTERVAL_MS,
    ),
    refetchIntervalInBackground: true,
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: Boolean(scopeId),
    queryFn: () =>
      scopeRuntimeApi.listServices(scopeId ?? "", {
        take: MISSION_WALL_SERVICE_TAKE,
      }),
    queryKey: ["mission-wall", "services", scopeId],
    refetchInterval: missionWallRefetchInterval(
      MISSION_WALL_ROSTER_REFETCH_INTERVAL_MS,
    ),
    refetchIntervalInBackground: true,
    retry: false,
  });
  const workflowMembers = React.useMemo(
    () =>
      filterMissionWallWorkflowMembers(
        membersQuery.data?.members ?? [],
        routeOptions.teamId,
      ),
    [membersQuery.data?.members, routeOptions.teamId],
  );
  const serviceRunTargets = React.useMemo(
    () =>
      selectMissionWallServiceRunTargets(
        workflowMembers,
        servicesQuery.data ?? [],
      ),
    [servicesQuery.data, workflowMembers],
  );
  const runQueries = useQueries({
    queries: serviceRunTargets.map((target) => {
      const serviceId = trimOptional(target.service.serviceId);
      return {
        enabled: Boolean(scopeId && serviceId),
        queryFn: () =>
          scopeRuntimeApi.listServiceRuns(scopeId ?? "", serviceId, {
            take: MISSION_WALL_SERVICE_RUN_TAKE,
          }),
        queryKey: ["mission-wall", "service-runs", scopeId, serviceId],
        refetchInterval: missionWallRefetchInterval(
          MISSION_WALL_RUN_REFETCH_INTERVAL_MS,
        ),
        refetchIntervalInBackground: true,
        retry: false,
      };
    }),
  });
  const runQueryVersion = runQueries
    .map((query, index) => {
      const serviceId =
        trimOptional(serviceRunTargets[index]?.service.serviceId) || `${index}`;
      return `${serviceId}:${query.dataUpdatedAt}:${query.errorUpdatedAt}:${query.fetchStatus}`;
    })
    .join("|");
  const dataVersion = runtimeDataVersion({
    authUpdatedAt: authSessionQuery.dataUpdatedAt,
    memberUpdatedAt: membersQuery.dataUpdatedAt,
    runQueryVersion,
    serviceUpdatedAt: servicesQuery.dataUpdatedAt,
    teamUpdatedAt: teamsQuery.dataUpdatedAt,
  });
  const generatedAt = React.useMemo(
    () => new Date().toISOString(),
    [dataVersion],
  );
  const serviceRunCatalogs = React.useMemo(
    () =>
      serviceRunTargets.map((target, index) => ({
        ...target,
        catalog: runQueries[index]?.data,
      })),
    [runQueryVersion, serviceRunTargets],
  );
  const runAuditTargets = React.useMemo(() => {
    const targets = new Map<string, MissionWallRunAuditTarget>();
    serviceRunCatalogs.forEach(({ catalog, service }) => {
      const fallbackServiceId = trimOptional(service.serviceId);
      (catalog?.runs ?? []).forEach((run) => {
        const runId = trimOptional(run.runId);
        const serviceId = trimOptional(run.serviceId) || fallbackServiceId;
        const runScopeId = trimOptional(run.scopeId) || trimOptional(scopeId);
        if (!runId || !serviceId || !runScopeId) {
          return;
        }

        const target: MissionWallRunAuditTarget = {
          actorId: trimOptional(run.actorId) || undefined,
          runId,
          scopeId: runScopeId,
          serviceId,
          status: toMissionWallRunStatus(run.completionStatus),
        };
        if (!shouldBackfillRunAudit(target)) {
          return;
        }

        targets.set(missionWallRunAuditKey(target), target);
      });
    });

    return [...targets.values()];
  }, [runQueryVersion, scopeId, serviceRunCatalogs]);
  const runAuditQueries = useQueries({
    queries: runAuditTargets.map((target) => ({
      enabled: Boolean(target.scopeId && target.serviceId && target.runId),
      queryFn: () =>
        scopeRuntimeApi.getServiceRunAudit(
          target.scopeId,
          target.serviceId,
          target.runId,
          {
            actorId: target.actorId,
          },
        ),
      queryKey: [
        "mission-wall",
        "service-run-audit",
        "window",
        target.scopeId,
        target.serviceId,
        target.runId,
        target.actorId,
      ],
      refetchInterval: auditRefetchIntervalForStatus(target.status),
      refetchIntervalInBackground: true,
      retry: false,
    })),
  });
  const runAuditVersion = runAuditQueries
    .map((query, index) => {
      const target = runAuditTargets[index];
      return `${missionWallRunAuditKey(target)}:${query.dataUpdatedAt}:${query.errorUpdatedAt}:${query.fetchStatus}`;
    })
    .join("|");
  const runAudits = React.useMemo(
    () =>
      runAuditQueries
        .map((query) => query.data)
        .filter(
          (audit): audit is ScopeServiceRunAuditSnapshot => audit !== undefined,
        ),
    [runAuditVersion],
  );
  const teams = React.useMemo<readonly StudioTeamSummary[]>(
    () => teamsQuery.data?.teams ?? [],
    [teamsQuery.data?.teams],
  );
  const runCatalogSnapshots = serviceRunCatalogs.map((entry) => entry.catalog);
  const latestObservedAt = latestRunObservedAt(runCatalogSnapshots);
  const runCount = runCatalogSnapshots.reduce(
    (count, catalog) => count + (catalog?.runs.length ?? 0),
    0,
  );
  const anyRunQueryLoading = runQueries.some((query) => query.isLoading);
  const anyRunQueryError = runQueries.some((query) => query.isError);
  const hasCriticalError =
    authSessionQuery.isError || membersQuery.isError || servicesQuery.isError;
  const isLoading =
    authSessionQuery.isLoading ||
    (Boolean(scopeId) && membersQuery.isLoading) ||
    (Boolean(scopeId) && servicesQuery.isLoading) ||
    anyRunQueryLoading;
  const live = React.useMemo(
    () =>
      buildLiveState({
        allRunsLoaded: runQueries.every(
          (query) => query.isSuccess || query.isError,
        ),
        generatedAt,
        hasCriticalError,
        hasPartialRunError: anyRunQueryError,
        isLoading,
        latestObservedAt,
        runCount,
        scopeId,
      }),
    [
      anyRunQueryError,
      generatedAt,
      hasCriticalError,
      isLoading,
      latestObservedAt,
      runCount,
      runQueryVersion,
      scopeId,
    ],
  );
  const buildSource = React.useCallback(
    () =>
      buildMissionWallSourceFromRuntime({
        generatedAt,
        live,
        runAudits,
        serviceRunCatalogs,
        teams,
      }),
    [generatedAt, live, runAudits, serviceRunCatalogs, teams],
  );

  return {
    buildSource,
    generatedAt,
    isLoading,
    live,
    routeFocusRunId: routeOptions.focusRunId,
    scopeId,
    teamId: routeOptions.teamId,
    workflowMembers,
  };
}
