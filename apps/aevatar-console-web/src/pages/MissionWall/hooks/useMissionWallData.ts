import { useQuery } from "@tanstack/react-query";
import React from "react";
import { t } from "@/shared/i18n/messages";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
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
        nowMs,
        runCount,
        scopeId,
      }),
    [
      generatedAt,
      hasCriticalError,
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
        snapshot: snapshotQuery.data,
      }),
    [generatedAt, live, snapshotQuery.data],
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
