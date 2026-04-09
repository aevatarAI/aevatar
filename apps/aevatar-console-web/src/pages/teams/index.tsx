import { PageLoading, ProCard } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Typography } from "antd";
import React, { useEffect, useMemo } from "react";
import { studioApi } from "@/shared/studio/api";
import { history } from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute, readScopeQueryDraft } from "@/shared/navigation/scopeRoutes";
import { resolveStudioScopeContext } from "@/shared/scope/context";

const TeamsIndexPage: React.FC = () => {
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

  useEffect(() => {
    if (!resolvedScopeId) {
      return;
    }

    history.replace(buildTeamWorkspaceRoute(resolvedScopeId));
  }, [resolvedScopeId]);

  if (resolvedScopeId || authSessionQuery.isLoading) {
    return <PageLoading fullscreen />;
  }

  const teamResolutionDescription = authSessionQuery.isError
    ? "The current session could not be refreshed into a usable team context. Retry, or open the legacy workspace while the session context is repaired."
    : "No current team context is available.";

  return (
    <div
      style={{
        padding: "48px 24px",
      }}
      >
        <ProCard>
          <Space orientation="vertical" size={16} style={{ width: "100%" }}>
            <Typography.Title level={2} style={{ margin: 0 }}>
              Team context unavailable
            </Typography.Title>
          <Typography.Paragraph style={{ margin: 0 }}>
            The console could not resolve a current team from the active session.
            Open the legacy project workspace or retry after your session context is restored.
          </Typography.Paragraph>
          {authSessionQuery.isError ? (
            <Alert
              title="Failed to resolve the current team"
              description={teamResolutionDescription}
              showIcon
              type="warning"
            />
          ) : null}
          <Empty description={teamResolutionDescription} />
          <Space wrap>
            <Button onClick={() => void authSessionQuery.refetch()} type="primary">
              Retry
            </Button>
            <Button onClick={() => history.push("/scopes/overview")}>
              Open legacy project workspace
            </Button>
          </Space>
        </Space>
      </ProCard>
    </div>
  );
};

export default TeamsIndexPage;
