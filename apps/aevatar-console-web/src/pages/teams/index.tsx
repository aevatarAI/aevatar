import {
  BranchesOutlined,
  ClockCircleOutlined,
  PauseCircleOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Skeleton, Space, Typography, theme } from "antd";
import React, { useMemo } from "react";
import { studioApi } from "@/shared/studio/api";
import { history } from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute, readScopeQueryDraft } from "@/shared/navigation/scopeRoutes";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import {
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import {
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarTwoPaneLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

type SummaryCardProps = {
  caption?: React.ReactNode;
  icon?: React.ReactNode;
  label: React.ReactNode;
  tone?: AevatarSemanticTone;
  value: React.ReactNode;
};

function renderHealthLabel(status: string): string {
  switch (status) {
    case "human-overridden":
      return "Human Override";
    case "blocked":
      return "Blocked";
    case "degraded":
      return "Degraded";
    case "healthy":
      return "Healthy";
    default:
      return "Attention";
  }
}

function summarizeOwner(
  focusActorId?: string,
  fallbackActorId?: string,
): string {
  const resolvedActorId = focusActorId?.trim() || fallbackActorId?.trim() || "";
  if (!resolvedActorId) {
    return "No current owner visible";
  }

  return resolvedActorId;
}

function summarizeLatestHandoff(
  fromActorId?: string,
  toActorId?: string,
): string {
  const from = fromActorId?.trim() || "";
  const to = toActorId?.trim() || "";
  if (!from || !to) {
    return "No visible handoff yet";
  }

  return `${from} -> ${to}`;
}

function SummaryCard({
  caption,
  icon,
  label,
  tone = "default",
  value,
}: SummaryCardProps) {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={{
        ...buildAevatarMetricCardStyle(token as AevatarThemeSurfaceToken, tone),
        display: "flex",
        flexDirection: "column",
        gap: 10,
        minHeight: 120,
        padding: 16,
      }}
    >
      <Space align="center" size={10}>
        {icon ? (
          <span
            style={{
              alignItems: "center",
              color: visual.iconColor,
              display: "inline-flex",
              fontSize: 18,
              justifyContent: "center",
            }}
          >
            {icon}
          </span>
        ) : null}
        <Typography.Text
          style={{
            color: visual.labelColor,
          }}
        >
          {label}
        </Typography.Text>
      </Space>
      <Typography.Title
        level={4}
        style={{
          color: visual.valueColor,
          margin: 0,
        }}
      >
        {value}
      </Typography.Title>
      {caption ? (
        <Typography.Text
          style={{
            color: visual.secondaryColor,
          }}
        >
          {caption}
        </Typography.Text>
      ) : null}
    </div>
  );
}

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
  } = useTeamRuntimeLens(resolvedScopeId);
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
    return (
      <AevatarPageShell
        title="Teams"
        content="Start from the current team workspace so the console tells one team story before it exposes deeper runtime controls."
      >
        <AevatarTwoPaneLayout
          rail={
            <AevatarPanel
              extra={
                <AevatarStatusTag
                  domain="observation"
                  label="Loading"
                  status="delayed"
                />
              }
              title="Recent activity summary"
            >
              <Skeleton active paragraph={{ rows: 6 }} title />
            </AevatarPanel>
          }
          railWidth={320}
          stage={
            <AevatarPanel
              extra={
                <AevatarStatusTag
                  domain="observation"
                  label="Loading"
                  status="delayed"
                />
              }
              title="Current collaboration snapshot"
            >
              <Skeleton active paragraph={{ rows: 8 }} title />
            </AevatarPanel>
          }
        />
      </AevatarPageShell>
    );
  }

  if (!resolvedScopeId) {
    return (
      <AevatarPageShell
        title="Teams"
        content="Open a current team when one is available, or start the first team from Team Builder."
      >
        <AevatarPanel
          extra={
            <AevatarStatusTag
              domain="observation"
              label={authSessionQuery.isError ? "Unavailable" : "Empty"}
              status={authSessionQuery.isError ? "unavailable" : "partial"}
            />
          }
          title="Team context unavailable"
        >
          <Space orientation="vertical" size={16} style={{ width: "100%" }}>
            <Typography.Paragraph style={{ margin: 0 }}>
              The console could not resolve a current team from the active session.
              Open Settings, retry, or start the first team from Team Builder.
            </Typography.Paragraph>
            {authSessionQuery.isError ? (
              <Alert
                description={teamResolutionDescription}
                showIcon
                type="warning"
              />
            ) : null}
            <Empty description={teamResolutionDescription} />
            <Space wrap>
              {authSessionQuery.isError ? (
                <Button
                  onClick={() => void authSessionQuery.refetch()}
                  type="primary"
                >
                  Retry
                </Button>
              ) : (
                <Button
                  onClick={() =>
                    history.push(
                      buildStudioWorkflowEditorRoute({
                        draftMode: "new",
                      }),
                    )
                  }
                  type="primary"
                >
                  Build first team
                </Button>
              )}
              {authSessionQuery.isError ? (
                <Button
                  onClick={() =>
                    history.push(
                      buildStudioWorkflowEditorRoute({
                        draftMode: "new",
                      }),
                    )
                  }
                >
                  Build first team
                </Button>
              ) : null}
              <Button onClick={() => history.push("/settings")}>
                Open Settings
              </Button>
            </Space>
          </Space>
        </AevatarPanel>
      </AevatarPageShell>
    );
  }

  const latestRelationship = lens.graph.relationships[0];
  const currentRunSummary = lens.currentRun
    ? `${lens.currentRun.runId} · ${lens.currentRun.completionStatus || "unknown"}`
    : "No recent run is visible yet.";
  const primaryActionLabel =
    lens.healthStatus === "blocked" || lens.healthStatus === "human-overridden"
      ? "Handle current blockage"
      : lens.healthStatus === "degraded" || lens.healthStatus === "attention"
        ? "Review current attention"
        : "View current team";
  const stageProvenance =
    actorGraphQuery.isError
      ? { label: "Unavailable", status: "unavailable" }
      : lens.graph.available
        ? { label: "Live", status: "live" }
        : { label: "Partial", status: "partial" };
  const activityProvenance =
    runsQuery.isError || currentRunAuditQuery.isError
      ? { label: "Unavailable", status: "unavailable" }
      : lens.currentRun
        ? { label: "Delayed", status: "delayed" }
        : { label: "Partial", status: "partial" };

  return (
    <AevatarPageShell
      content={`${lens.subtitle}. Teams Home keeps the first screen anchored on the current team so users see ownership, activity, and the next best action before they enter deeper runtime tools.`}
      extra={
        <Space wrap>
          <Button
            onClick={() => history.push(buildTeamWorkspaceRoute(resolvedScopeId))}
            type="primary"
          >
            {primaryActionLabel}
          </Button>
          <Button
            onClick={() =>
              history.push(
                buildStudioWorkflowWorkspaceRoute({
                  scopeId: resolvedScopeId,
                }),
              )
            }
          >
            Open Team Builder
          </Button>
        </Space>
      }
      title={
        <Space align="center" wrap size={12}>
          <Typography.Text strong>Teams</Typography.Text>
          <AevatarStatusTag
            domain="run"
            label={renderHealthLabel(lens.healthStatus)}
            status={lens.healthStatus === "healthy" ? "completed" : lens.healthStatus}
          />
        </Space>
      }
      titleHelp="Teams Home is the current-team workbench. It should tell users who owns the flow, what just happened, and what to do next without sending them straight into a builder."
    >
      {teamSignalIssues.length > 0 ? (
        <Alert
          description={teamSignalIssues.join(" ")}
          title="Some team signals are currently unavailable"
          showIcon
          type="warning"
        />
      ) : null}
      <AevatarTwoPaneLayout
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              extra={
                <AevatarStatusTag
                  domain="observation"
                  label={activityProvenance.label}
                  status={activityProvenance.status}
                />
              }
              title="Recent activity summary"
              titleHelp="This rail stays secondary on purpose. It explains what changed recently without taking over the team story."
            >
              <Space orientation="vertical" size={12} style={{ width: "100%" }}>
                <div>
                  <Typography.Text strong>Current run</Typography.Text>
                  <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
                    {currentRunSummary}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Latest change</Typography.Text>
                  <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
                    {lens.compare.summary}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Current anomaly or risk</Typography.Text>
                  <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
                    {lens.playback.prompt || lens.healthSummary}
                  </Typography.Paragraph>
                </div>
              </Space>
            </AevatarPanel>
            <AevatarPanel
              extra={
                <AevatarStatusTag
                  domain="observation"
                  label={lens.partialSignals.length > 0 ? "Partial" : "Live"}
                  status={lens.partialSignals.length > 0 ? "partial" : "live"}
                />
              }
              title="Current team identity"
            >
              <Space orientation="vertical" size={8} style={{ width: "100%" }}>
                <Typography.Title level={4} style={{ margin: 0 }}>
                  {lens.title}
                </Typography.Title>
                <Typography.Text type="secondary">{resolvedScopeId}</Typography.Text>
                <Typography.Paragraph style={{ margin: 0 }}>
                  {lens.currentBindingTarget}
                </Typography.Paragraph>
                {lens.currentBindingContext ? (
                  <Alert
                    description={lens.currentBindingContext}
                    showIcon
                    type="info"
                  />
                ) : null}
              </Space>
            </AevatarPanel>
          </div>
        }
        railWidth={340}
        stage={
          <AevatarPanel
            extra={
              <AevatarStatusTag
                domain="observation"
                label={stageProvenance.label}
                status={stageProvenance.status}
              />
            }
            title="Current collaboration snapshot"
            titleHelp="The first screen should answer who owns the flow, the latest visible handoff, and the current risk before the user decides where to click."
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryCard
                caption={lens.graph.focusReason}
                icon={<BranchesOutlined />}
                label="Current owner"
                tone="info"
                value={summarizeOwner(
                  lens.graph.focusActorId,
                  lens.currentRun?.actorId,
                )}
              />
              <SummaryCard
                caption={lens.graph.stageSummary}
                icon={<ClockCircleOutlined />}
                label="Latest handoff"
                tone="default"
                value={summarizeLatestHandoff(
                  latestRelationship?.fromActorId,
                  latestRelationship?.toActorId,
                )}
              />
              <SummaryCard
                caption={lens.healthSummary}
                icon={<PauseCircleOutlined />}
                label="Current risk"
                tone={lens.healthTone}
                value={lens.playback.interactionLabel || renderHealthLabel(lens.healthStatus)}
              />
            </div>
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <Alert
                description={lens.playback.summary}
                title="Current collaboration posture"
                showIcon
                type={lens.healthStatus === "healthy" ? "info" : "warning"}
              />
              <div>
                <Typography.Text strong>Mission</Typography.Text>
                <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
                  {lens.subtitle}
                </Typography.Paragraph>
              </div>
              <div>
                <Typography.Text strong>Next best move</Typography.Text>
                <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
                  {primaryActionLabel === "View current team"
                    ? "The team is stable enough to enter the team workspace."
                    : "The team needs operator attention before more changes are introduced."}
                </Typography.Paragraph>
              </div>
              {lens.partialSignals.length > 0 ? (
                <Alert
                  description={lens.partialSignals.join(" · ")}
                  title="Partial team truth"
                  showIcon
                  type="info"
                />
              ) : null}
            </div>
          </AevatarPanel>
        }
      />
    </AevatarPageShell>
  );
};

export default TeamsIndexPage;
