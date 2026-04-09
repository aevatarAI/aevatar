import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from "@aevatar-react-sdk/types";
import {
  ApartmentOutlined,
  BranchesOutlined,
  ClockCircleOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  MessageOutlined,
  PauseCircleOutlined,
  SafetyCertificateOutlined,
  SwapOutlined,
} from "@ant-design/icons";
import { Alert, Button, Empty, Grid, Space, Tag, Typography, theme } from "antd";
import React from "react";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import { readScopeQueryDraft } from "@/shared/navigation/scopeRoutes";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import {
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import type { TeamPlaybackSummary } from "./runtime/teamRuntimeLens";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

type SignalCardProps = {
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

function renderPlaybackLabel(status: string): string {
  switch (status) {
    case "waiting":
      return "Waiting";
    case "failed":
      return "Failed";
    case "completed":
      return "Completed";
    default:
      return "Active";
  }
}

function renderPlaybackTagColor(status: string): string {
  switch (status) {
    case "waiting":
      return "warning";
    case "failed":
      return "error";
    case "completed":
      return "success";
    default:
      return "processing";
  }
}

function renderDirectionLabel(direction: string): string {
  switch (direction) {
    case "inbound":
      return "Into focus";
    case "outbound":
      return "From focus";
    default:
      return "Peer";
  }
}

function compactId(value: string): string {
  const normalized = value.trim();
  if (!normalized) {
    return "n/a";
  }

  const segment = normalized.split("/").pop() || normalized;
  return segment.split(":").pop() || segment;
}

function createObservedPlaybackEvents(
  playback: Pick<TeamPlaybackSummary, "commandId" | "currentRunId" | "rootActorId">,
): AGUIEvent[] {
  const events: AGUIEvent[] = [];
  const runId = playback.currentRunId?.trim() || "";
  const actorId = playback.rootActorId?.trim() || "";
  const commandId = playback.commandId?.trim() || "";

  if (runId) {
    events.push({
      runId,
      threadId: commandId || runId,
      timestamp: Date.now(),
      type: AGUIEventType.RUN_STARTED,
    } as AGUIEvent);
  }

  if (actorId || commandId) {
    events.push({
      name: CustomEventName.RunContext,
      timestamp: Date.now(),
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: actorId || undefined,
        commandId: commandId || undefined,
      },
    } as AGUIEvent);
  }

  return events;
}

const SignalCard: React.FC<SignalCardProps> = ({
  caption,
  icon,
  label,
  tone = "default",
  value,
}) => {
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
};

const TeamDetailPage: React.FC = () => {
  const requestedScope = readScopeQueryDraft();
  const scopeId = requestedScope.scopeId.trim();
  const screens = Grid.useBreakpoint();
  const isCompactTeamLayout = !screens.lg;
  const {
    actorGraphQuery,
    actorsQuery,
    baselineRunAuditQuery,
    bindingQuery,
    currentRunAuditQuery,
    lens,
    runsQuery,
    scriptsQuery,
    servicesQuery,
    workflowsQuery,
  } = useTeamRuntimeLens(scopeId);
  const initialLoading =
    bindingQuery.isLoading ||
    servicesQuery.isLoading ||
    actorsQuery.isLoading ||
    workflowsQuery.isLoading ||
    scriptsQuery.isLoading;
  const teamSignalIssues = [
    bindingQuery.isError ? "Team binding could not be loaded." : null,
    servicesQuery.isError ? "Published services could not be loaded." : null,
    actorsQuery.isError ? "Team members could not be loaded." : null,
    workflowsQuery.isError ? "Workflow assets could not be loaded." : null,
    scriptsQuery.isError ? "Scripts could not be loaded." : null,
    runsQuery.isError ? "Recent team activity could not be loaded." : null,
    currentRunAuditQuery.isError ? "Current run audit could not be loaded." : null,
    baselineRunAuditQuery.isError ? "Baseline run audit could not be loaded." : null,
    actorGraphQuery.isError ? "Collaboration graph could not be loaded." : null,
  ].filter((issue): issue is string => Boolean(issue));
  const runtimeServiceId =
    lens.currentService?.serviceId || lens.currentRun?.serviceId || undefined;
  const handleOpenPlaybackRun = React.useCallback(
    (preferredActorId?: string | null) => {
      const runId = lens.playback.currentRunId?.trim() || "";
      if (!scopeId || !runId) {
        return;
      }

      const actorId =
        preferredActorId?.trim() ||
        lens.playback.rootActorId?.trim() ||
        lens.currentRun?.actorId?.trim() ||
        "";
      const observedDraftKey = saveObservedRunSessionPayload({
        actorId: actorId || undefined,
        commandId: lens.playback.commandId || undefined,
        endpointId: "chat",
        endpointKind: "chat",
        events: createObservedPlaybackEvents(lens.playback),
        prompt:
          lens.playback.launchPrompt ||
          lens.playback.prompt ||
          lens.playback.summary,
        routeName: lens.playback.workflowName || undefined,
        runId,
        scopeId,
        serviceOverrideId: runtimeServiceId,
      });

      history.push(
        buildRuntimeRunsHref({
          actorId: actorId || undefined,
          draftKey: observedDraftKey || undefined,
          endpointId: "chat",
          endpointKind: "chat",
          prompt: lens.playback.launchPrompt || undefined,
          route: lens.playback.workflowName || undefined,
          scopeId,
          serviceId: runtimeServiceId,
        }),
      );
    },
    [
      lens.currentRun?.actorId,
      lens.playback,
      runtimeServiceId,
      scopeId,
    ],
  );
  const handleOpenPlaybackActor = React.useCallback(
    (actorId?: string | null, runId?: string | null) => {
      const resolvedActorId = actorId?.trim() || lens.playback.rootActorId?.trim() || "";
      if (!scopeId || !resolvedActorId) {
        return;
      }

      history.push(
        buildRuntimeExplorerHref({
          actorId: resolvedActorId,
          runId: runId?.trim() || lens.playback.currentRunId || undefined,
          scopeId,
          serviceId: runtimeServiceId,
        }),
      );
    },
    [lens.playback.currentRunId, lens.playback.rootActorId, runtimeServiceId, scopeId],
  );

  const activityRail = (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="Team Activity"
        titleHelp="Recent service runs are the shortest path from the team shell to real operational truth."
      >
        {runsQuery.isLoading ? (
          <AevatarInspectorEmpty description="Loading recent team activity." />
        ) : runsQuery.isError ? (
          <AevatarInspectorEmpty
            description="Recent team activity could not be loaded for this team."
            title="Activity unavailable"
          />
        ) : lens.currentRun || lens.baselineRun ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            {[lens.currentRun, ...[lens.baselineRun].filter(Boolean)].map((run, index) => {
              if (!run) {
                return null;
              }

              return (
                <div
                  key={run.runId}
                  style={{
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 12,
                    display: "flex",
                    flexDirection: "column",
                    gap: 10,
                    padding: 14,
                  }}
                >
                  <Space align="center" wrap>
                    <Typography.Text strong>
                      {index === 0 ? "Current run" : "Baseline run"}
                    </Typography.Text>
                    <AevatarStatusTag
                      domain="run"
                      status={run.completionStatus || "unknown"}
                      label={run.completionStatus || "unknown"}
                    />
                    {run.lastSuccess === true ? (
                      <Tag color="success">prior good</Tag>
                    ) : null}
                  </Space>
                  <Typography.Text>{run.runId}</Typography.Text>
                  <Typography.Text type="secondary">
                    Revision {run.revisionId || "unknown"} · Actor {run.actorId || "n/a"}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    Updated {formatDateTime(run.lastUpdatedAt)}
                  </Typography.Text>
                </div>
              );
            })}
          </div>
        ) : (
          <AevatarInspectorEmpty description="No recent team activity is available yet." />
        )}
      </AevatarPanel>

      <AevatarPanel
        title="Run Compare / Change Diff"
        titleHelp="Compare the latest visible team run with the closest prior good run so operators can explain what changed."
      >
        {!currentRunAuditQuery.isError && !baselineRunAuditQuery.isError && lens.compare.available ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Alert description={lens.compare.summary} showIcon type="info" />
            {lens.compare.sections.map((section) => (
              <div
                key={section.key}
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                }}
              >
                <Space align="center" size={8}>
                  <SwapOutlined />
                  <Typography.Text strong>{section.title}</Typography.Text>
                </Space>
                <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                  {section.items.map((detail) => (
                    <div
                      key={`${section.key}-${detail}`}
                      style={{
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 10,
                        padding: "10px 12px",
                      }}
                    >
                      <Typography.Text>{detail}</Typography.Text>
                    </div>
                  ))}
                </div>
              </div>
            ))}
          </div>
        ) : (
          <AevatarInspectorEmpty
            description={
              currentRunAuditQuery.isError || baselineRunAuditQuery.isError
                ? "Run audit data could not be loaded for the latest comparison."
                : lens.compare.summary
            }
            title="Compare unavailable"
          />
        )}
      </AevatarPanel>

      <AevatarPanel
        title="Human Escalation Playback"
        titleHelp="Playback keeps the current human gate, the recent step sequence, and the latest runtime events on one rail so operators can explain why the team is paused."
      >
        {!currentRunAuditQuery.isError && lens.playback.available ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Alert
              description={lens.playback.summary}
              showIcon
              type={lens.playback.interactionLabel ? "warning" : "info"}
            />
            {lens.playback.currentRunId || lens.playback.rootActorId ? (
              <Space wrap size={[8, 8]}>
                {lens.playback.currentRunId ? (
                  <Button
                    onClick={() => handleOpenPlaybackRun()}
                    size="small"
                  >
                    Open current run replay
                  </Button>
                ) : null}
                {lens.playback.rootActorId ? (
                  <Button
                    onClick={() =>
                      handleOpenPlaybackActor(
                        lens.playback.rootActorId,
                        lens.playback.currentRunId,
                      )
                    }
                    size="small"
                    type="link"
                  >
                    Inspect root actor
                  </Button>
                ) : null}
              </Space>
            ) : null}
            {lens.playback.prompt ? (
              <div
                style={{
                  background: "var(--ant-color-warning-bg)",
                  border: "1px solid var(--ant-color-warning-border)",
                  borderRadius: 12,
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                  padding: 12,
                }}
              >
                <Space align="center" size={8}>
                  <PauseCircleOutlined />
                  <Typography.Text strong>
                    {lens.playback.interactionLabel || "Current gate"}
                  </Typography.Text>
                  {lens.playback.timeoutLabel ? (
                    <Tag>{lens.playback.timeoutLabel}</Tag>
                  ) : null}
                </Space>
                <Typography.Text>{lens.playback.prompt}</Typography.Text>
              </div>
            ) : null}
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              {lens.playback.steps.map((step) => (
                <div
                  key={step.key}
                  style={{
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 12,
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <Space align="center" wrap>
                    <Typography.Text strong>{step.stepId}</Typography.Text>
                    <Tag color={renderPlaybackTagColor(step.status)}>
                      {renderPlaybackLabel(step.status)}
                    </Tag>
                    <Tag>{step.stepType}</Tag>
                  </Space>
                  <Typography.Text type="secondary">{step.summary}</Typography.Text>
                  <Typography.Text>{step.detail}</Typography.Text>
                  {step.timestamp ? (
                    <Typography.Text type="secondary">
                      {formatDateTime(step.timestamp)}
                    </Typography.Text>
                  ) : null}
                  {step.runId || step.actorId ? (
                    <Space wrap size={[8, 8]}>
                      {step.runId ? (
                        <Button
                          onClick={() => handleOpenPlaybackRun(step.actorId)}
                          size="small"
                        >
                          Open run replay
                        </Button>
                      ) : null}
                      {step.actorId ? (
                        <Button
                          onClick={() =>
                            handleOpenPlaybackActor(step.actorId, step.runId)
                          }
                          size="small"
                          type="link"
                        >
                          Inspect actor
                        </Button>
                      ) : null}
                    </Space>
                  ) : null}
                </div>
              ))}
            </div>
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <Space align="center" size={8}>
                <ClockCircleOutlined />
                <Typography.Text strong>Recent runtime events</Typography.Text>
              </Space>
              {lens.playback.events.map((event) => (
                <div
                  key={event.key}
                  style={{
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 10,
                    padding: "10px 12px",
                  }}
                >
                  <Space align="center" wrap>
                    <Tag color={event.tone === "error" ? "error" : event.tone === "warning" ? "warning" : "processing"}>
                      {event.stage}
                    </Tag>
                    {event.timestamp ? (
                      <Typography.Text type="secondary">
                        {formatDateTime(event.timestamp)}
                      </Typography.Text>
                    ) : null}
                  </Space>
                  <Typography.Paragraph style={{ margin: "8px 0 0" }}>
                    {event.message}
                  </Typography.Paragraph>
                  <Typography.Text type="secondary">{event.detail}</Typography.Text>
                  {event.runId || event.actorId ? (
                    <Space style={{ marginTop: 8 }} wrap size={[8, 8]}>
                      {event.runId ? (
                        <Button
                          onClick={() => handleOpenPlaybackRun(event.actorId)}
                          size="small"
                        >
                          Open run replay
                        </Button>
                      ) : null}
                      {event.actorId ? (
                        <Button
                          onClick={() =>
                            handleOpenPlaybackActor(event.actorId, event.runId)
                          }
                          size="small"
                          type="link"
                        >
                          Inspect actor
                        </Button>
                      ) : null}
                    </Space>
                  ) : null}
                </div>
              ))}
            </div>
            {lens.playback.roleReplies.length > 0 ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <Space align="center" size={8}>
                  <MessageOutlined />
                  <Typography.Text strong>Recent replies</Typography.Text>
                </Space>
                {lens.playback.roleReplies.map((reply) => (
                  <div
                    key={reply}
                    style={{
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 10,
                      padding: "10px 12px",
                    }}
                  >
                    <Typography.Text>{reply}</Typography.Text>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        ) : (
          <AevatarInspectorEmpty
            description={
              currentRunAuditQuery.isError
                ? "Run audit data could not be loaded for the latest playback."
                : lens.playback.summary
            }
            title="Playback unavailable"
          />
        )}
      </AevatarPanel>
    </div>
  );

  const collaborationStage = (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="Collaboration Canvas"
        titleHelp="The canvas stays focused on the actor implied by the latest run or current serving revision, then shows the nearby relationship surface around it."
      >
        {actorGraphQuery.isLoading ? (
          <AevatarInspectorEmpty description="Loading the current team collaboration focus." />
        ) : actorGraphQuery.isError ? (
          <AevatarInspectorEmpty
            description="The collaboration graph could not be loaded, so the team shell falls back to member and run truth."
            title="Collaboration graph unavailable"
          />
        ) : lens.graph.available ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SignalCard
                icon={<EyeOutlined />}
                label="Focused actor"
                tone="info"
                value={lens.graph.focusActorId || "n/a"}
                caption={lens.graph.focusReason}
              />
              <SignalCard
                icon={<BranchesOutlined />}
                label="Visible relations"
                value={lens.graph.edgeCount}
                caption={`${lens.graph.nodeCount} nodes in the current focused subgraph`}
              />
            </div>
            <Alert
              description={`${lens.graph.stageSummary} ${lens.graph.focusReason}`}
              showIcon
              type="info"
            />
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(220px, 1.1fr) minmax(240px, 1fr)",
              }}
            >
              <div
                style={{
                  background: "var(--ant-color-primary-bg)",
                  border: "1px solid var(--ant-color-primary-border)",
                  borderRadius: 14,
                  display: "flex",
                  flexDirection: "column",
                  gap: 12,
                  padding: 14,
                }}
              >
                <Typography.Text strong>Focused actor</Typography.Text>
                <Typography.Title level={4} style={{ margin: 0 }}>
                  {compactId(lens.graph.focusActorId)}
                </Typography.Title>
                <Typography.Text type="secondary">
                  {lens.graph.focusReason}
                </Typography.Text>
                {lens.graph.nodes
                  .filter((node) => node.isFocused)
                  .slice(0, 1)
                  .map((node) => (
                    <Space key={node.actorId} align="center" wrap>
                      <Tag color="processing">{node.actorType}</Tag>
                      <Tag>{node.relationCount} relations</Tag>
                      <Typography.Text type="secondary">{node.caption}</Typography.Text>
                    </Space>
                  ))}
              </div>
              <div
                style={{
                  display: "grid",
                  gap: 10,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                {lens.graph.nodes
                  .filter((node) => !node.isFocused)
                  .map((node) => (
                    <div
                      key={node.actorId}
                      style={{
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 12,
                        display: "flex",
                        flexDirection: "column",
                        gap: 8,
                        padding: 12,
                      }}
                    >
                      <Space align="center" wrap>
                        <Typography.Text strong>{compactId(node.actorId)}</Typography.Text>
                        <Tag>{node.actorType}</Tag>
                      </Space>
                      <Typography.Text type="secondary">{node.caption}</Typography.Text>
                      <Typography.Text type="secondary">
                        {node.relationCount} visible relations
                      </Typography.Text>
                    </div>
                  ))}
              </div>
            </div>
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              {lens.graph.relationships.map((relationship) => (
                <div
                  key={relationship.key}
                  style={{
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 12,
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <Space align="center" wrap>
                    <BranchesOutlined />
                    <Typography.Text strong>
                      {compactId(relationship.fromActorId)} → {compactId(relationship.toActorId)}
                    </Typography.Text>
                  </Space>
                  <Space align="center" wrap size={[6, 6]}>
                    <Tag color="processing">{renderDirectionLabel(relationship.direction)}</Tag>
                    <Tag>{relationship.edgeType}</Tag>
                  </Space>
                </div>
              ))}
            </div>
          </div>
        ) : (
          <AevatarInspectorEmpty
            description="The actor graph is unavailable, so the team shell falls back to member and run truth."
            title="Collaboration graph unavailable"
          />
        )}
      </AevatarPanel>

      <AevatarPanel
        title="Team Composition"
        titleHelp="The team shell keeps members, service surface, and current binding context visible on one stage."
      >
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          <SignalCard
            icon={<ApartmentOutlined />}
            label="Workflow assets"
            value={lens.workflowCount}
            caption="Visible workflow assets in the current team scope"
          />
          <SignalCard
            icon={<DeploymentUnitOutlined />}
            label="Scripts"
            value={lens.scriptCount}
            caption="Scope-aware scripts currently visible"
          />
          <SignalCard
            icon={<SafetyCertificateOutlined />}
            label="Services"
            value={lens.serviceCount}
            caption="Published service surfaces attached to this team"
          />
        </div>
        <div
          style={{
            display: "grid",
            gap: 10,
            gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
          }}
        >
          {lens.members.length > 0 ? (
            lens.members.map((member) => (
              <div
                key={member.actorId}
                style={{
                  background: member.isFocused
                    ? "var(--ant-color-primary-bg)"
                    : "var(--ant-color-bg-container)",
                  border: member.isFocused
                    ? "1px solid var(--ant-color-primary-border)"
                    : "1px solid var(--ant-color-border-secondary)",
                  borderRadius: 12,
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                  padding: 14,
                }}
              >
                <Space align="center" wrap>
                  <Typography.Text strong>{member.actorType}</Typography.Text>
                  {member.isFocused ? <Tag color="processing">focused</Tag> : null}
                </Space>
                <Typography.Text>{member.actorId}</Typography.Text>
              </div>
            ))
          ) : (
            <Empty
              description="No team members are visible yet."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </div>
      </AevatarPanel>
    </div>
  );

  const contextAside = (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="Health / Trust Rail"
        titleHelp="This rail answers whether the team is healthy, blocked, degraded, human-overridden, or still missing critical runtime signals."
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <SignalCard
            icon={<SafetyCertificateOutlined />}
            label="Current state"
            tone={lens.healthTone}
            value={renderHealthLabel(lens.healthStatus)}
            caption={lens.healthSummary}
          />
          {lens.healthDetails.length > 0 ? (
            lens.healthDetails.map((detail) => (
              <div
                key={detail}
                style={{
                  border: "1px solid var(--ant-color-border-secondary)",
                  borderRadius: 10,
                  padding: "10px 12px",
                }}
              >
                <Typography.Text>{detail}</Typography.Text>
              </div>
            ))
          ) : (
            <Typography.Text type="secondary">
              No extra health detail is currently available.
            </Typography.Text>
          )}
        </div>
      </AevatarPanel>

      <AevatarPanel
        title="Current Serving"
        titleHelp="This keeps the active target, revision, and service identity in one place so operators do not need to jump into Studio immediately."
      >
        <Space orientation="vertical" size={8} style={{ width: "100%" }}>
          <Typography.Text strong>{lens.currentBindingTarget}</Typography.Text>
          <Typography.Text type="secondary">
            Revision {lens.activeRevision?.revisionId || "unknown"}
          </Typography.Text>
          <Typography.Text type="secondary">
            Service {lens.currentService?.serviceId || lens.currentRun?.serviceId || "n/a"}
          </Typography.Text>
          {lens.currentBindingContext ? (
            <Alert
              description={lens.currentBindingContext}
              showIcon
              type="info"
            />
          ) : null}
        </Space>
      </AevatarPanel>

      <AevatarPanel
        title="Governance Snapshot"
        titleHelp="This is the buyer-readable trust summary, not a replacement for the full Governance console."
      >
        <Space orientation="vertical" size={10} style={{ width: "100%" }}>
          <div>
            <Typography.Text strong>Who is serving now</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.currentBindingTarget} on revision {lens.governance.servingRevision}
            </Typography.Paragraph>
          </div>
          <div>
            <Typography.Text strong>What changed recently</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.compare.summary}
            </Typography.Paragraph>
          </div>
          <div>
            <Typography.Text strong>Can we trace the current runtime</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.governance.traceability}
            </Typography.Paragraph>
          </div>
          <div>
            <Typography.Text strong>Can a human intervene</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.governance.humanIntervention}
            </Typography.Paragraph>
          </div>
          <div>
            <Typography.Text strong>Is there a fallback</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.governance.fallback}
            </Typography.Paragraph>
          </div>
          <div>
            <Typography.Text strong>Rollout posture</Typography.Text>
            <Typography.Paragraph style={{ marginBottom: 0, marginTop: 4 }}>
              {lens.governance.rollout}
            </Typography.Paragraph>
          </div>
        </Space>
      </AevatarPanel>
    </div>
  );

  if (!scopeId) {
    return (
      <AevatarPageShell
        title="Team workspace"
        content="Open a concrete team route before entering the Team-first workspace."
      >
        <AevatarPanel title="No team selected">
          <AevatarInspectorEmpty description="A concrete team scope is required to render this workspace." />
        </AevatarPanel>
      </AevatarPageShell>
    );
  }

  return (
    <AevatarPageShell
      title={
        <Space align="center" wrap size={12}>
          <Typography.Text strong>{lens.title}</Typography.Text>
          <AevatarStatusTag
            domain="run"
            status={lens.healthStatus === "healthy" ? "completed" : lens.healthStatus}
            label={renderHealthLabel(lens.healthStatus)}
          />
          <Tag>{scopeId}</Tag>
        </Space>
      }
      content={`${lens.subtitle}. Team-first keeps the team shell readable while proving runtime truth with runs, revisions, and focused collaboration context.`}
      extra={
        <Space key="team-detail-actions" wrap>
          <Button
            onClick={() =>
              history.push(
                buildStudioWorkflowWorkspaceRoute({
                  scopeId,
                }),
              )
            }
            type="primary"
          >
            Open Team Builder
          </Button>
          <Button
            onClick={() =>
              history.push(
                buildRuntimeRunsHref({
                  scopeId,
                  serviceId: lens.currentService?.serviceId || undefined,
                  actorId: lens.currentRun?.actorId || undefined,
                }),
              )
            }
          >
            Open Runs
          </Button>
          <Button
            onClick={() =>
              history.push(
                buildRuntimeExplorerHref({
                  actorId: lens.graph.focusActorId || undefined,
                  scopeId,
                  serviceId: lens.currentService?.serviceId || undefined,
                }),
              )
            }
          >
            Open Explorer
          </Button>
        </Space>
      }
      titleHelp="This workspace keeps the team as the surface-level story while grounding every module in current runtime, service, and binding truth."
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {teamSignalIssues.length > 0 ? (
          <Alert
            title="Some team signals are currently unavailable"
            description={teamSignalIssues.join(" ")}
            showIcon
            type="warning"
          />
        ) : null}
        {lens.partialSignals.length > 0 ? (
          <Alert
            title="Partial runtime truth"
            description={lens.partialSignals.join(" · ")}
            showIcon
            type="info"
          />
        ) : null}
        {isCompactTeamLayout ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {collaborationStage}
            {activityRail}
            {contextAside}
          </div>
        ) : (
          <AevatarWorkbenchLayout
            rail={activityRail}
            railWidth={340}
            stage={collaborationStage}
            stageAside={contextAside}
            stageAsideWidth={340}
          />
        )}
        {initialLoading ? (
          <Typography.Text type="secondary">
            Loading team shell signals...
          </Typography.Text>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamDetailPage;
