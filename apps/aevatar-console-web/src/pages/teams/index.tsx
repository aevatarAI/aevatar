import { useQuery } from "@tanstack/react-query";
import React, { useMemo } from "react";
import { isTeamFirstEnabled } from "@/shared/config/consoleFeatures";
import { readScopeQueryDraft } from "@/shared/navigation/scopeRoutes";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import LegacyTeamsHome from "./LegacyTeamsHome";
import TeamsHomeRosterV0 from "./TeamsHomeRosterV0";
import {
  TeamContextUnavailable,
  TeamsHomeLoading,
  type SharedTeamsHomeProps,
} from "./teamsHomeShared";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

const TeamsIndexPage: React.FC = () => {
  const teamFirstEnabled = isTeamFirstEnabled();
  const requestedDraft = useMemo(() => readScopeQueryDraft(), []);
  const authSessionQuery = useQuery({
    queryKey: ["teams", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });

  const resolvedScopeId = useMemo(() => {
    const requestedScopeId = requestedDraft.scopeId.trim();
    if (requestedScopeId) {
      return requestedScopeId;
    }

    return resolveStudioScopeContext(authSessionQuery.data)?.scopeId ?? "";
  }, [authSessionQuery.data, requestedDraft.scopeId]);

  const {
    actorGraphQuery,
    baselineRunAuditQuery,
    bindingQuery,
    currentRunAuditQuery,
    lens,
    runsQuery,
    scriptsQuery,
    servicesQuery,
    workflowsQuery,
  } = useTeamRuntimeLens(resolvedScopeId, {
    includeCatalogSignals: false,
  });

  const lensLoading =
    resolvedScopeId.length > 0 &&
    (bindingQuery.isLoading ||
      servicesQuery.isLoading ||
      workflowsQuery.isLoading ||
      scriptsQuery.isLoading);

  const teamSignalIssues = [
    bindingQuery.isError ? "Team binding could not be loaded." : null,
    servicesQuery.isError ? "Published services could not be loaded." : null,
    runsQuery.isError ? "Recent team activity could not be loaded." : null,
    currentRunAuditQuery.isError ? "Current run audit could not be loaded." : null,
    baselineRunAuditQuery.isError ? "Baseline run audit could not be loaded." : null,
    actorGraphQuery.isError ? "Collaboration graph could not be loaded." : null,
  ].filter((issue): issue is string => Boolean(issue));

  const teamResolutionDescription = authSessionQuery.isError
    ? "The current session could not be refreshed into a usable team context. Retry, or open Settings while the team context is repaired."
    : "No current team context is available.";

  if (authSessionQuery.isLoading || lensLoading) {
    return <TeamsHomeLoading />;
  }

  if (!resolvedScopeId) {
    return (
      <TeamContextUnavailable
        authSessionErrored={authSessionQuery.isError}
        onRetry={() => void authSessionQuery.refetch()}
        teamResolutionDescription={teamResolutionDescription}
      />
    );
  }

  const sharedProps: SharedTeamsHomeProps = {
    actorGraphUnavailable: actorGraphQuery.isError,
    activityUnavailable: runsQuery.isError || currentRunAuditQuery.isError,
    lens,
    resolvedScopeId,
    teamSignalIssues,
  };

  return teamFirstEnabled ? (
    <TeamsHomeRosterV0 {...sharedProps} />
  ) : (
    <LegacyTeamsHome {...sharedProps} />
  );
};

export default TeamsIndexPage;
