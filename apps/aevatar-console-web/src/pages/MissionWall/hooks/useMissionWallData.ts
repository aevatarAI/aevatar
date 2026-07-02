import { useQueries, useQuery } from "@tanstack/react-query";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { t } from "@/shared/i18n/messages";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import {
  toScopeServiceRunAuditSnapshot,
  type ScopeServiceRunAuditSnapshot,
} from "@/shared/models/runtime/scopeServices";
import { studioApi } from "@/shared/studio/api";
import type {
  MissionWallLiveState,
  MissionWallRun,
  MissionWallSource,
} from "../models";
import {
  buildMissionWallSourceFromWorkflowBoardSnapshot,
  freshnessSecondsSince,
  missionWallRunAuditKey,
  toMissionWallRunStatus,
} from "../missionWallRuntimeData";

type MissionWallRouteOptions = {
  readonly focusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
};

export const MISSION_WALL_RUN_REFETCH_INTERVAL_MS = 5_000;
export const MISSION_WALL_AUDIT_REFETCH_INTERVAL_MS = 3_000;
export const MISSION_WALL_SNAPSHOT_TAKE = 100;

export interface MissionWallRuntimeData {
  readonly buildSource: () => MissionWallSource;
  readonly generatedAt: string;
  readonly isLoading: boolean;
  readonly live: MissionWallLiveState;
  readonly routeFocusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
}

type MissionWallRunAuditTarget = {
  readonly actorId?: string;
  readonly memberId: string;
  readonly publishedServiceId?: string;
  readonly runId: string;
  readonly scopeId: string;
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
        "Loading workflow board snapshot.",
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
        "Mission wall snapshot could not be loaded.",
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
        "No workflow board members are visible yet.",
      ),
      status: "idle",
    };
  }

  return {
    durableFreshnessSeconds,
    lastObservedAt: input.latestObservedAt,
    message: t(
      "pages.missionwall.liveState.connected",
      "Connected to workflow board read model.",
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
  const snapshotQuery = useQuery({
    enabled: Boolean(scopeId),
    queryFn: () =>
      studioApi.getWorkflowBoardSnapshot(scopeId ?? "", {
        take: MISSION_WALL_SNAPSHOT_TAKE,
        teamId: routeOptions.teamId,
      }),
    queryKey: [
      "mission-wall",
      "workflow-board-snapshot",
      scopeId,
      routeOptions.teamId,
    ],
    refetchInterval: missionWallRefetchInterval(
      MISSION_WALL_RUN_REFETCH_INTERVAL_MS,
    ),
    refetchIntervalInBackground: true,
    retry: false,
  });
  const generatedAt = React.useMemo(
    () => snapshotQuery.data?.generatedAt ?? new Date().toISOString(),
    [
      authSessionQuery.dataUpdatedAt,
      snapshotQuery.data?.generatedAt,
      snapshotQuery.dataUpdatedAt,
      snapshotQuery.errorUpdatedAt,
      snapshotQuery.fetchStatus,
    ],
  );
  const runAuditTargets = React.useMemo(() => {
    const targets = new Map<string, MissionWallRunAuditTarget>();
    snapshotQuery.data?.teams.forEach((team) => {
      team.members.forEach((member) => {
        const runId = trimOptional(member.currentExecutionId);
        const memberId = trimOptional(member.memberId);
        const publishedServiceId = trimOptional(member.publishedServiceId);
        const runScopeId =
          trimOptional(snapshotQuery.data?.scopeId) || trimOptional(scopeId);
        if (!runId || !memberId || !publishedServiceId || !runScopeId) {
          return;
        }

        const target: MissionWallRunAuditTarget = {
          actorId: trimOptional(member.actorId) || undefined,
          memberId,
          publishedServiceId,
          runId,
          scopeId: runScopeId,
          status: toMissionWallRunStatus(member.executionStatus),
        };
        if (!shouldBackfillRunAudit(target)) {
          return;
        }

        targets.set(
          [
            target.scopeId,
            target.memberId,
            target.publishedServiceId,
            target.runId,
          ].join(":"),
          target,
        );
      });
    });

    return [...targets.values()];
  }, [scopeId, snapshotQuery.data]);
  const runAuditQueries = useQueries({
    queries: runAuditTargets.map((target) => ({
      enabled: Boolean(target.scopeId && target.memberId && target.runId),
      queryFn: async () => {
        const audit = await scopeRuntimeApi.getMemberRunAudit(
          target.scopeId,
          target.memberId,
          target.runId,
          {
            actorId: target.actorId,
          },
        );
        return toScopeServiceRunAuditSnapshot(audit);
      },
      queryKey: [
        "mission-wall",
        "member-run-audit",
        "window",
        target.scopeId,
        target.memberId,
        target.publishedServiceId,
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
      const auditKey = missionWallRunAuditKey({
        runId: target?.runId,
        scopeId: target?.scopeId,
        serviceId: target?.publishedServiceId,
      });
      return `${auditKey}:${target?.memberId ?? index}:${query.dataUpdatedAt}:${query.errorUpdatedAt}:${query.fetchStatus}`;
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
  const latestObservedAt =
    trimOptional(snapshotQuery.data?.lastNodeUpdatedAt) || undefined;
  const runCount =
    snapshotQuery.data?.teams.reduce(
      (count, team) => count + team.members.length,
      0,
    ) ?? 0;
  const hasCriticalError = authSessionQuery.isError;
  const isLoading =
    authSessionQuery.isLoading ||
    (Boolean(scopeId) && snapshotQuery.isLoading);
  const live = React.useMemo(
    () =>
      buildLiveState({
        allRunsLoaded: snapshotQuery.isSuccess || snapshotQuery.isError,
        generatedAt,
        hasCriticalError,
        hasPartialRunError: snapshotQuery.isError,
        isLoading,
        latestObservedAt,
        runCount,
        scopeId,
      }),
    [
      generatedAt,
      hasCriticalError,
      isLoading,
      latestObservedAt,
      runCount,
      snapshotQuery.isError,
      snapshotQuery.isSuccess,
      scopeId,
    ],
  );
  const buildSource = React.useCallback(
    () =>
      buildMissionWallSourceFromWorkflowBoardSnapshot({
        generatedAt,
        live,
        runAudits,
        snapshot: snapshotQuery.data,
      }),
    [generatedAt, live, runAudits, snapshotQuery.data],
  );

  return {
    buildSource,
    generatedAt,
    isLoading,
    live,
    routeFocusRunId: routeOptions.focusRunId,
    scopeId,
    teamId: routeOptions.teamId,
  };
}
