import {
  BranchesOutlined,
  ClockCircleOutlined,
  PauseCircleOutlined,
} from "@ant-design/icons";
import { Alert, Button, Space, Typography } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute } from "@/shared/navigation/scopeRoutes";
import { buildStudioWorkflowWorkspaceRoute } from "@/shared/studio/navigation";
import {
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarTwoPaneLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  SharedTeamsHomeProps,
  SummaryCard,
  renderHealthLabel,
  summarizeLatestHandoff,
  summarizeOwner,
} from "./teamsHomeShared";

const LegacyTeamsHome: React.FC<SharedTeamsHomeProps> = ({
  actorGraphUnavailable,
  activityUnavailable,
  lens,
  resolvedScopeId,
  teamSignalIssues,
}) => {
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
  const stageProvenance = actorGraphUnavailable
    ? { label: "Unavailable", status: "unavailable" }
    : lens.graph.available
      ? { label: "Live", status: "live" }
      : { label: "Partial", status: "partial" };
  const activityProvenance = activityUnavailable
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
            Open Studio
          </Button>
        </Space>
      }
      title={
        <Space align="center" wrap size={12}>
          <Typography.Text strong>Teams</Typography.Text>
          <AevatarStatusTag
            domain="run"
            label={renderHealthLabel(lens.healthStatus)}
            status={
              lens.healthStatus === "healthy" ? "completed" : lens.healthStatus
            }
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
                  <Typography.Paragraph
                    style={{ marginBottom: 0, marginTop: 4 }}
                  >
                    {currentRunSummary}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Latest change</Typography.Text>
                  <Typography.Paragraph
                    style={{ marginBottom: 0, marginTop: 4 }}
                  >
                    {lens.compare.summary}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Current anomaly or risk</Typography.Text>
                  <Typography.Paragraph
                    style={{ marginBottom: 0, marginTop: 4 }}
                  >
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
                <Typography.Text type="secondary">
                  {resolvedScopeId}
                </Typography.Text>
                <Typography.Paragraph style={{ margin: 0 }}>
                  {lens.subtitle}
                </Typography.Paragraph>
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
                value={
                  lens.playback.interactionLabel ||
                  renderHealthLabel(lens.healthStatus)
                }
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

export default LegacyTeamsHome;
