import { useQuery } from "@tanstack/react-query";
import React from "react";
import { t } from "@/shared/i18n/messages";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import type { StudioWorkflowBoardSnapshot } from "@/shared/studio/models";
import type {
  MissionWallLiveState,
  MissionWallSource,
} from "../models";
import {
  buildMissionWallSourceFromWorkflowBoardSnapshot,
  freshnessSecondsSince,
} from "../missionWallRuntimeData";

type MissionWallRouteOptions = {
  readonly focusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
};

export const MISSION_WALL_RUN_REFETCH_INTERVAL_MS = 5_000;
export const MISSION_WALL_SNAPSHOT_TAKE = 100;
export const MISSION_WALL_STALE_SNAPSHOT_FALLBACK_MS = 60_000;

type MissionWallSnapshotCache = {
  readonly cachedAtMs: number;
  readonly key: string;
  readonly snapshot: StudioWorkflowBoardSnapshot;
};

export interface MissionWallRuntimeData {
  readonly buildSource: () => MissionWallSource;
  readonly generatedAt: string;
  readonly isLoading: boolean;
  readonly live: MissionWallLiveState;
  readonly nowMs: number;
  readonly routeFocusRunId?: string;
  readonly scopeId?: string;
  readonly teamId?: string;
}

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

function workflowBoardSnapshotMemberCount(
  snapshot: StudioWorkflowBoardSnapshot | undefined,
): number {
  return (
    snapshot?.teams.reduce((count, team) => count + team.members.length, 0) ?? 0
  );
}

function buildSnapshotCacheKey(input: {
  readonly scopeId?: string;
  readonly teamId?: string;
}): string {
  return `${input.scopeId ?? ""}:${input.teamId ?? ""}`;
}

function useNowMs(): number {
  const [nowMs, setNowMs] = React.useState(() => Date.now());

  React.useEffect(() => {
    const intervalId = window.setInterval(() => {
      setNowMs(Date.now());
    }, 1000);

    return () => {
      window.clearInterval(intervalId);
    };
  }, []);

  return nowMs;
}

function buildLiveState(input: {
  readonly allRunsLoaded: boolean;
  readonly generatedAt: string;
  readonly hasCriticalError: boolean;
  readonly hasPartialRunError: boolean;
  readonly hasSnapshotUnavailable: boolean;
  readonly isLoading: boolean;
  readonly latestObservedAt?: string;
  readonly nowMs: number;
  readonly runCount: number;
  readonly scopeId?: string;
}): MissionWallLiveState {
  const durableFreshnessSeconds = freshnessSecondsSince(
    input.latestObservedAt,
    input.nowMs,
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

  if (input.hasSnapshotUnavailable) {
    return {
      durableFreshnessSeconds,
      lastObservedAt: input.latestObservedAt,
      message: t(
        "pages.missionwall.liveState.snapshotUnavailable",
        "Mission wall snapshot could not be loaded.",
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
  const nowMs = useNowMs();
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
  const snapshotCacheRef =
    React.useRef<MissionWallSnapshotCache | undefined>(undefined);
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
  const snapshotCacheKey = React.useMemo(
    () =>
      buildSnapshotCacheKey({
        scopeId,
        teamId: routeOptions.teamId,
      }),
    [routeOptions.teamId, scopeId],
  );
  const queriedSnapshot = snapshotQuery.data;
  const queriedRunCount = workflowBoardSnapshotMemberCount(queriedSnapshot);
  const hasCachedSnapshotForRoute =
    snapshotCacheRef.current?.key === snapshotCacheKey;
  const cachedSnapshot = hasCachedSnapshotForRoute
    ? snapshotCacheRef.current?.snapshot
    : undefined;
  const shouldUseCachedSnapshot =
    Boolean(cachedSnapshot) &&
    snapshotCacheRef.current !== undefined &&
    nowMs - snapshotCacheRef.current.cachedAtMs <=
      MISSION_WALL_STALE_SNAPSHOT_FALLBACK_MS &&
    ((snapshotQuery.isSuccess && queriedRunCount === 0) || snapshotQuery.isError);
  const effectiveSnapshot = shouldUseCachedSnapshot
    ? cachedSnapshot
    : snapshotQuery.isError
      ? undefined
      : queriedSnapshot;
  React.useEffect(() => {
    if (
      !snapshotQuery.isSuccess ||
      snapshotQuery.dataUpdatedAt <= 0 ||
      !queriedSnapshot ||
      queriedRunCount === 0
    ) {
      return;
    }

    snapshotCacheRef.current = {
      cachedAtMs: snapshotQuery.dataUpdatedAt,
      key: snapshotCacheKey,
      snapshot: queriedSnapshot,
    };
  }, [
    queriedRunCount,
    queriedSnapshot,
    snapshotCacheKey,
    snapshotQuery.dataUpdatedAt,
    snapshotQuery.isSuccess,
  ]);
  const generatedAt = React.useMemo(
    () => effectiveSnapshot?.generatedAt ?? new Date().toISOString(),
    [
      authSessionQuery.dataUpdatedAt,
      effectiveSnapshot?.generatedAt,
      snapshotQuery.dataUpdatedAt,
      snapshotQuery.errorUpdatedAt,
      snapshotQuery.fetchStatus,
    ],
  );
  const latestObservedAt =
    trimOptional(effectiveSnapshot?.lastNodeUpdatedAt) || undefined;
  const runCount = workflowBoardSnapshotMemberCount(effectiveSnapshot);
  const hasCriticalError = authSessionQuery.isError;
  const hasStaleSnapshotFallback = Boolean(shouldUseCachedSnapshot);
  const hasEffectiveSnapshot = Boolean(effectiveSnapshot);
  const hasSnapshotUnavailable =
    snapshotQuery.isError && !hasStaleSnapshotFallback && !hasEffectiveSnapshot;
  const isLoading =
    authSessionQuery.isLoading ||
    (Boolean(scopeId) && snapshotQuery.isLoading && !effectiveSnapshot);
  const live = React.useMemo(
    () =>
      buildLiveState({
        allRunsLoaded: snapshotQuery.isSuccess || snapshotQuery.isError,
        generatedAt,
        hasCriticalError,
        hasPartialRunError: snapshotQuery.isError || hasStaleSnapshotFallback,
        hasSnapshotUnavailable,
        isLoading,
        latestObservedAt,
        nowMs,
        runCount,
        scopeId,
      }),
    [
      generatedAt,
      hasCriticalError,
      hasSnapshotUnavailable,
      hasStaleSnapshotFallback,
      isLoading,
      latestObservedAt,
      nowMs,
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
        snapshot: effectiveSnapshot,
      }),
    [effectiveSnapshot, generatedAt, live],
  );

  return {
    buildSource,
    generatedAt,
    isLoading,
    live,
    nowMs,
    routeFocusRunId: routeOptions.focusRunId,
    scopeId,
    teamId: routeOptions.teamId,
  };
}
