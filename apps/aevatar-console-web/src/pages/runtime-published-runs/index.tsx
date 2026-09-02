import React from "react";
import {
  getLocationSnapshot,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import MemberPublishedRunsReplay from "./MemberPublishedRunsReplay";

type TeamMemberPublishedRunsRouteState = {
  readonly actorId: string;
  readonly memberId: string;
  readonly runId: string;
  readonly scheduleId: string;
  readonly scopeId: string;
  readonly teamId: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function decodePathSegment(value: string): string {
  try {
    return decodeURIComponent(value).trim();
  } catch {
    return value.trim();
  }
}

function readTeamMemberPublishedRunsRouteState(
  search = typeof window === "undefined" ? "" : window.location.search,
  pathname = typeof window === "undefined" ? "" : window.location.pathname,
): TeamMemberPublishedRunsRouteState {
  const segments = pathname.split("/").filter(Boolean).map(decodePathSegment);
  const params = new URLSearchParams(search);
  const isScopedMemberRunsPath =
    segments[0] === "scopes" &&
    segments[2] === "teams" &&
    segments[4] === "members" &&
    segments[6] === "runs";

  if (!isScopedMemberRunsPath) {
    return {
      actorId: "",
      memberId: "",
      runId: "",
      scheduleId: "",
      scopeId: "",
      teamId: "",
    };
  }

  return {
    actorId: trimOptional(params.get("actorId")),
    memberId: trimOptional(segments[5]),
    runId: trimOptional(params.get("runId")),
    scheduleId: trimOptional(params.get("scheduleId")),
    scopeId: trimOptional(segments[1]),
    teamId: trimOptional(segments[3]),
  };
}

const TeamMemberPublishedRunsPage: React.FC = () => {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => "",
  );
  const route = React.useMemo(
    () => readTeamMemberPublishedRunsRouteState(),
    [locationSnapshot],
  );

  return (
    <MemberPublishedRunsReplay
      initialActorId={route.actorId || undefined}
      initialRunId={route.runId || undefined}
      memberId={route.memberId}
      scheduleId={route.scheduleId || undefined}
      scopeId={route.scopeId}
      teamId={route.teamId}
    />
  );
};

export default TeamMemberPublishedRunsPage;
