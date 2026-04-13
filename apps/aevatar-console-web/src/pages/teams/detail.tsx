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
import {
  Alert,
  Button,
  Empty,
  Grid,
  Segmented,
  Space,
  Tag,
  Typography,
  theme,
} from "antd";
import { useQuery } from "@tanstack/react-query";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import { readScopeQueryDraft } from "@/shared/navigation/scopeRoutes";
import { studioApi } from "@/shared/studio/api";
import type { StudioWorkflowDocument } from "@/shared/studio/models";
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
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import {
  deriveTeamIntegrationsSummary,
  deriveTeamWorkflowRoleBindings,
} from "./runtime/teamIntegrations";
import type { TeamPlaybackSummary } from "./runtime/teamRuntimeLens";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

type ObservationStatus = "live" | "delayed" | "partial" | "unavailable" | "seeded";

type ObservationBadge = {
  label: string;
  status: ObservationStatus;
};

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

function renderObservationLabel(status: ObservationStatus): string {
  switch (status) {
    case "live":
      return "Live";
    case "delayed":
      return "Delayed";
    case "partial":
      return "Partial";
    case "seeded":
      return "Seeded";
    default:
      return "Unavailable";
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

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
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
  const { token } = theme.useToken();
  const [compactPanel, setCompactPanel] = React.useState<"activity" | "details">(
    "activity",
  );
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
  const workspaceSettingsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "workspace-settings"],
    queryFn: () => studioApi.getWorkspaceSettings(),
    retry: false,
  });
  const connectorCatalogQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "connector-catalog"],
    queryFn: () => studioApi.getConnectorCatalog(),
    retry: false,
  });
  const activeWorkflowSummary = React.useMemo(() => {
    if (lens.activeRevision?.implementationKind !== "workflow") {
      return null;
    }

    const workflows = workflowsQuery.data ?? [];
    const workflowNameHints = [
      trimText(lens.activeRevision.workflowName),
      trimText(lens.currentRun?.workflowName),
      trimText(lens.currentRunAudit?.audit.workflowName),
      trimText(lens.currentRunAudit?.summary.workflowName),
      trimText(lens.playback.workflowName),
    ].filter(Boolean);
    if (workflowNameHints.length > 0) {
      for (const workflowNameHint of workflowNameHints) {
        const matchedWorkflow =
          workflows.find(
            (workflow) =>
              trimText(workflow.workflowName) === workflowNameHint ||
              trimText(workflow.displayName) === workflowNameHint,
          ) ?? null;
        if (matchedWorkflow) {
          return matchedWorkflow;
        }
      }
    }

    return workflows.length === 1 ? workflows[0] : null;
  }, [lens.activeRevision, lens.currentRun, lens.currentRunAudit, lens.playback.workflowName, workflowsQuery.data]);
  const teamWorkflowDetailQuery = useQuery({
    enabled:
      scopeId.length > 0 &&
      lens.activeRevision?.implementationKind === "workflow" &&
      trimText(activeWorkflowSummary?.workflowId).length > 0,
    queryKey: [
      "teams",
      "workflow-detail",
      scopeId,
      activeWorkflowSummary?.workflowId ?? "",
      lens.activeRevision?.revisionId ?? "",
    ],
    queryFn: () =>
      scopesApi.getWorkflowDetail(scopeId, activeWorkflowSummary?.workflowId ?? ""),
    retry: false,
  });
  const teamWorkflowDocumentsQuery = useQuery({
    enabled:
      lens.activeRevision?.implementationKind === "workflow" &&
      Boolean(teamWorkflowDetailQuery.data?.available) &&
      trimText(teamWorkflowDetailQuery.data?.source?.workflowYaml).length > 0,
    queryKey: [
      "teams",
      "workflow-documents",
      scopeId,
      activeWorkflowSummary?.workflowId ?? "",
      lens.activeRevision?.revisionId ?? "",
    ],
    queryFn: async (): Promise<StudioWorkflowDocument[]> => {
      const source = teamWorkflowDetailQuery.data?.source;
      if (!source) {
        return [];
      }

      const workflowYamls = [
        trimText(source.workflowYaml),
        ...Object.values(source.inlineWorkflowYamls ?? {}).map((yaml) =>
          trimText(yaml),
        ),
      ].filter(Boolean);
      const uniqueWorkflowYamls = [...new Set(workflowYamls)];
      const parsedDocuments = await Promise.all(
        uniqueWorkflowYamls.map(async (yaml) => {
          const parsed = await studioApi.parseYaml({ yaml });
          return parsed.document ?? null;
        }),
      );

      return parsedDocuments.filter(
        (document): document is StudioWorkflowDocument => Boolean(document),
      );
    },
    retry: false,
  });
  const teamScopedRoleLoading =
    lens.activeRevision?.implementationKind === "workflow" &&
    (workflowsQuery.isLoading ||
      teamWorkflowDetailQuery.isLoading ||
      teamWorkflowDocumentsQuery.isLoading);
  const teamScopedRoleUnavailable =
    lens.activeRevision?.implementationKind === "workflow" &&
    !teamScopedRoleLoading &&
    (!activeWorkflowSummary ||
      teamWorkflowDetailQuery.isError ||
      teamWorkflowDocumentsQuery.isError ||
      !teamWorkflowDetailQuery.data?.available ||
      !teamWorkflowDetailQuery.data?.source);
  const integrations = React.useMemo(
    () =>
      deriveTeamIntegrationsSummary({
        bindingKind: lens.activeRevision?.implementationKind ?? "unknown",
        workspaceSettings: workspaceSettingsQuery.data ?? null,
        connectorCatalog: connectorCatalogQuery.data ?? null,
        teamWorkflowRoles:
          lens.activeRevision?.implementationKind !== "workflow"
            ? []
            : teamScopedRoleLoading
              ? undefined
              : teamScopedRoleUnavailable
                ? null
                : deriveTeamWorkflowRoleBindings(
                    teamWorkflowDocumentsQuery.data ?? [],
                  ),
        workflowDocumentCount: teamWorkflowDocumentsQuery.data?.length ?? 0,
      }),
    [
      connectorCatalogQuery.data,
      lens.activeRevision?.implementationKind,
      teamScopedRoleLoading,
      teamScopedRoleUnavailable,
      teamWorkflowDocumentsQuery.data,
      workspaceSettingsQuery.data,
    ],
  );
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
    workspaceSettingsQuery.isError ? "Workspace settings could not be loaded." : null,
    connectorCatalogQuery.isError ? "Connector catalog could not be loaded." : null,
    teamScopedRoleUnavailable
      ? "Current workflow connector usage could not be loaded."
      : null,
  ].filter((issue): issue is string => Boolean(issue));
  const activityProvenance: ObservationBadge =
    runsQuery.isError || currentRunAuditQuery.isError
      ? { label: "Unavailable", status: "unavailable" }
      : lens.currentRun
        ? { label: "Delayed", status: "delayed" }
        : { label: "Partial", status: "partial" };
  const compareProvenance: ObservationBadge =
    currentRunAuditQuery.isError || baselineRunAuditQuery.isError
      ? { label: "Unavailable", status: "unavailable" }
      : lens.baselineRun
        ? { label: "Delayed", status: "delayed" }
        : { label: "Partial", status: "partial" };
  const playbackProvenance: ObservationBadge = currentRunAuditQuery.isError
    ? { label: "Unavailable", status: "unavailable" }
    : lens.playback.available
      ? { label: "Delayed", status: "delayed" }
      : { label: "Partial", status: "partial" };
  const graphProvenance: ObservationBadge = actorGraphQuery.isError
    ? { label: "Unavailable", status: "unavailable" }
    : lens.graph.available
      ? { label: "Live", status: "live" }
      : { label: "Partial", status: "partial" };
  const contextProvenance: ObservationBadge =
    teamSignalIssues.length > 0 || lens.partialSignals.length > 0
      ? { label: "Partial", status: "partial" }
      : { label: "Delayed", status: "delayed" };
  const currentServingProvenance: ObservationBadge =
    bindingQuery.isError || servicesQuery.isError
      ? { label: "Unavailable", status: "unavailable" }
      : lens.currentBindingContext
        ? { label: "Live", status: "live" }
        : { label: "Partial", status: "partial" };
  const integrationsSignalIssues = [
    workspaceSettingsQuery.isError
      ? "Workspace settings are unavailable."
      : null,
    connectorCatalogQuery.isError
      ? "Connector catalog is unavailable."
      : null,
    teamScopedRoleUnavailable
      ? "Current workflow connector usage is unavailable."
      : null,
  ].filter((issue): issue is string => Boolean(issue));
  const integrationsProvenance: ObservationBadge =
    workspaceSettingsQuery.isError &&
    connectorCatalogQuery.isError &&
    teamScopedRoleUnavailable
      ? { label: "Unavailable", status: "unavailable" }
      : integrationsSignalIssues.length > 0 ||
          teamScopedRoleLoading
        ? { label: "Partial", status: "partial" }
        : integrations.available
          ? { label: "Delayed", status: "delayed" }
          : { label: "Partial", status: "partial" };
  const runtimeServiceId =
    lens.currentService?.serviceId || lens.currentRun?.serviceId || undefined;
  const teamBuilderRoute =
    trimText(activeWorkflowSummary?.workflowId).length > 0
      ? buildStudioWorkflowEditorRoute({
          scopeId,
          workflowId: activeWorkflowSummary?.workflowId,
        })
      : buildStudioWorkflowWorkspaceRoute({
          scopeId,
        });
  const teamBuilderLabel =
    "Open Team Builder";
  const availableActorIds = React.useMemo(
    () =>
      Array.from(
        new Set([
          ...lens.members.map((member) => member.actorId),
          ...lens.graph.nodes.map((node) => node.actorId),
        ]),
      ).filter(Boolean),
    [lens.graph.nodes, lens.members],
  );
  const defaultSelectedActorId =
    lens.graph.focusActorId || lens.members[0]?.actorId || "";
  const [selectedActorId, setSelectedActorId] = React.useState("");

  React.useEffect(() => {
    if (availableActorIds.length === 0) {
      if (selectedActorId) {
        setSelectedActorId("");
      }
      return;
    }

    if (!selectedActorId || !availableActorIds.includes(selectedActorId)) {
      setSelectedActorId(defaultSelectedActorId || availableActorIds[0]);
    }
  }, [availableActorIds, defaultSelectedActorId, selectedActorId]);

  const effectiveActorId = selectedActorId || defaultSelectedActorId;
  const selectedMember =
    lens.members.find((member) => member.actorId === effectiveActorId) || null;
  const selectedGraphNodes = lens.graph.nodes.map((node) => ({
    ...node,
    isFocused: effectiveActorId
      ? node.actorId === effectiveActorId
      : node.isFocused,
  }));
  const selectedGraphRelationships = effectiveActorId
    ? lens.graph.relationships.filter(
        (relationship) =>
          relationship.fromActorId === effectiveActorId ||
          relationship.toActorId === effectiveActorId,
      )
    : lens.graph.relationships;
  const visibleGraphRelationships =
    selectedGraphRelationships.length > 0
      ? selectedGraphRelationships
      : lens.graph.relationships;
  const selectedFocusReason =
    effectiveActorId && effectiveActorId !== lens.graph.focusActorId
      ? `Inspector focus is pinned to ${compactId(effectiveActorId)}. ${lens.graph.focusReason}`
      : lens.graph.focusReason;
  const selectedPlaybackSteps = effectiveActorId
    ? lens.playback.steps.filter((step) => step.actorId === effectiveActorId)
    : lens.playback.steps;
  const visiblePlaybackSteps =
    selectedPlaybackSteps.length > 0 ? selectedPlaybackSteps : lens.playback.steps;
  const selectedPlaybackEvents = effectiveActorId
    ? lens.playback.events.filter((event) => event.actorId === effectiveActorId)
    : lens.playback.events;
  const visiblePlaybackEvents =
    selectedPlaybackEvents.length > 0
      ? selectedPlaybackEvents
      : lens.playback.events;
  const selectedPlaybackSummary =
    effectiveActorId && selectedPlaybackSteps.length === 0 && selectedPlaybackEvents.length === 0
      ? `No actor-specific playback facts are visible for ${compactId(
          effectiveActorId,
        )} yet, so the rail is showing the latest team-wide activity.`
      : effectiveActorId
        ? `The rail is focused on ${compactId(
            effectiveActorId,
          )} whenever actor-specific playback is available.`
        : "";
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={activityProvenance.label}
            status={activityProvenance.status}
          />
        }
        title="Recent Activity"
        titleHelp="Start here when you need proof of what this team just did and whether it needs attention."
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={compareProvenance.label}
            status={compareProvenance.status}
          />
        }
        title="What Changed"
        titleHelp="Compare the latest visible run with the last solid baseline so the team story is explainable."
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={playbackProvenance.label}
            status={playbackProvenance.status}
          />
        }
        title="Human Handoff"
        titleHelp="Keep the current human gate, the recent step sequence, and the latest runtime events on one rail so the pause makes sense."
      >
        {!currentRunAuditQuery.isError && lens.playback.available ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Alert
              description={lens.playback.summary}
              showIcon
              type={lens.playback.interactionLabel ? "warning" : "info"}
            />
            {selectedPlaybackSummary ? (
              <Alert
                description={selectedPlaybackSummary}
                showIcon
                type="info"
              />
            ) : null}
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
              {visiblePlaybackSteps.map((step) => (
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
              {visiblePlaybackEvents.map((event) => (
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={graphProvenance.label}
            status={graphProvenance.status}
          />
        }
        title="Collaboration Canvas"
        titleHelp="Keep one member in focus, then show the nearby collaboration surface around that person."
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
                value={effectiveActorId || "n/a"}
                caption={selectedFocusReason}
              />
              <SignalCard
                icon={<BranchesOutlined />}
                label="Visible relations"
                value={visibleGraphRelationships.length}
                caption={`${selectedGraphNodes.length} nodes in the current focused subgraph`}
              />
            </div>
            <Alert
              description={`${lens.graph.stageSummary} ${selectedFocusReason}`}
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
                  {compactId(effectiveActorId)}
                </Typography.Title>
                <Typography.Text type="secondary">
                  {selectedFocusReason}
                </Typography.Text>
                {selectedGraphNodes
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
                {selectedGraphNodes
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
              {visibleGraphRelationships.map((relationship) => (
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={contextProvenance.label}
            status={contextProvenance.status}
          />
        }
        title="Visible Members"
        titleHelp="Keep the people, roles, and active runtime surface for this team on one stage."
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
              <button
                aria-label={`Focus member ${member.actorType} ${member.actorId}`}
                key={member.actorId}
                onClick={() => setSelectedActorId(member.actorId)}
                style={{
                  background: member.actorId === effectiveActorId
                    ? "var(--ant-color-primary-bg)"
                    : "var(--ant-color-bg-container)",
                  border: member.actorId === effectiveActorId
                    ? "1px solid var(--ant-color-primary-border)"
                    : "1px solid var(--ant-color-border-secondary)",
                  borderRadius: 12,
                  cursor: "pointer",
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                  minHeight: 0,
                  padding: 14,
                  textAlign: "left",
                }}
                type="button"
              >
                <Space align="center" wrap>
                  <Typography.Text strong>{member.actorType}</Typography.Text>
                  {member.actorId === effectiveActorId ? (
                    <Tag color="processing">focused</Tag>
                  ) : null}
                </Space>
                <Typography.Text>{member.actorId}</Typography.Text>
              </button>
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={effectiveActorId ? "Live" : "Partial"}
            status={effectiveActorId ? "live" : "partial"}
          />
        }
        title="Member Focus"
        titleHelp="Pick one visible member and keep the canvas, activity, and inspector pointed at the same person."
      >
        {selectedMember || effectiveActorId ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <SignalCard
              icon={<EyeOutlined />}
              label="Inspector focus"
              tone="info"
              value={compactId(effectiveActorId)}
              caption={selectedMember?.actorType || "Team member"}
            />
            <Typography.Text type="secondary">
              {visibleGraphRelationships.length > 0
                ? `${visibleGraphRelationships.length} visible collaboration paths currently touch this member.`
                : "No visible collaboration path is attached to this member yet."}
            </Typography.Text>
            <Space wrap size={[8, 8]}>
              <Button
                onClick={() =>
                  handleOpenPlaybackActor(
                    effectiveActorId,
                    lens.playback.currentRunId,
                  )
                }
                size="small"
                type="link"
              >
                Inspect actor
              </Button>
              {lens.playback.currentRunId ? (
                <Button
                  onClick={() => handleOpenPlaybackRun(effectiveActorId)}
                  size="small"
                >
                  Open run replay
                </Button>
              ) : null}
            </Space>
          </div>
        ) : (
          <AevatarInspectorEmpty
            description="Select a visible member to focus the canvas, playback, and inspector together."
            title="No member selected"
          />
        )}
      </AevatarPanel>

      <AevatarPanel
        extra={
          <AevatarStatusTag
            domain="observation"
            label={contextProvenance.label}
            status={contextProvenance.status}
          />
        }
        title="Team Health"
        titleHelp="Answer the simple question first: is this team healthy, blocked, degraded, or still missing critical runtime proof?"
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
        extra={
          <AevatarStatusTag
            domain="observation"
            label={currentServingProvenance.label}
            status={currentServingProvenance.status}
          />
        }
        title="Live Configuration"
        titleHelp="Keep the active workflow, revision, and service identity together before deciding whether to open Studio."
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
          <Space wrap size={[8, 8]}>
            <Button
              onClick={() => history.push(teamBuilderRoute)}
              type="primary"
            >
              {teamBuilderLabel}
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
              Review activity
            </Button>
          </Space>
          <Typography.Text type="secondary">
            Use Team Builder when the live workflow needs to change. Use activity when
            you need proof before editing.
          </Typography.Text>
        </Space>
      </AevatarPanel>

      <AevatarPanel
        extra={
          <AevatarStatusTag
            domain="observation"
            label={integrationsProvenance.label}
            status={integrationsProvenance.status}
          />
        }
        title="Connected Systems"
        titleHelp="Show the external systems and connection capabilities around this team, not extra team members."
      >
        {workspaceSettingsQuery.isLoading &&
        connectorCatalogQuery.isLoading &&
        (lens.activeRevision?.implementationKind !== "workflow" ||
          teamScopedRoleLoading) ? (
          <AevatarInspectorEmpty description="Loading team integrations." />
        ) : !integrations.available && integrationsSignalIssues.length > 0 ? (
          <AevatarInspectorEmpty
            description="Workspace settings, connector definitions, or current team connector usage could not be loaded for this team."
            title="Integrations unavailable"
          />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            {integrationsSignalIssues.length > 0 ? (
              <Alert
                description="Some integration facts are missing, so this inspector is showing the best visible workspace truth."
                showIcon
                type="warning"
              />
            ) : null}
            {teamScopedRoleLoading ? (
              <Alert
                description="Loading team-scoped connector usage from the current workflow."
                showIcon
                type="info"
              />
            ) : null}
            <SignalCard
              icon={<DeploymentUnitOutlined />}
              label="Runtime base"
              tone="info"
              value={integrations.runtimeHostLabel}
              caption={integrations.workspaceSummary}
            />
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SignalCard
                icon={<BranchesOutlined />}
                label="Connector definitions"
                value={integrations.connectorCount}
                caption="Workspace-visible connection capabilities"
              />
              <SignalCard
                icon={<ApartmentOutlined />}
                label="Team-linked connectors"
                value={integrations.linkedConnectorCount}
                caption={integrations.teamRoleUsageSummary}
              />
            </div>
            <Typography.Text type="secondary">
              {integrations.summary}
            </Typography.Text>
            {integrations.items.length > 0 ? (
              <div
                style={{
                  display: "grid",
                  gap: 10,
                  gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                }}
              >
                {integrations.items.map((connector) => (
                  <div
                    key={connector.key}
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
                      <Typography.Text strong>{connector.name}</Typography.Text>
                      <Tag>{connector.type.toUpperCase()}</Tag>
                      <Tag color={connector.enabled ? "success" : "default"}>
                        {connector.enabled ? "enabled" : "disabled"}
                      </Tag>
                    </Space>
                    <Typography.Text type="secondary">
                      {connector.summary}
                    </Typography.Text>
                    {connector.usedByRoles.length > 0 ? (
                      <Typography.Text type="secondary">
                        Used by current team roles {connector.usedByRoles.join(", ")}
                      </Typography.Text>
                    ) : integrations.teamRoleUsageStatus === "resolved" ? (
                      <Typography.Text type="secondary">
                        Current team does not reference this connector.
                      </Typography.Text>
                    ) : integrations.teamRoleUsageStatus === "not_applicable" ? (
                      <Typography.Text type="secondary">
                        This team is {integrations.bindingKind}-bound, so workflow role usage is not available.
                      </Typography.Text>
                    ) : (
                      <Typography.Text type="secondary">
                        Team-scoped role usage is not currently available.
                      </Typography.Text>
                    )}
                  </div>
                ))}
              </div>
            ) : (
              <AevatarInspectorEmpty
                description="No connector definitions are currently visible for this workspace."
                title="No connectors visible"
              />
            )}
            {integrations.unresolvedReferences.length > 0 ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <Typography.Text strong>Referenced but undefined</Typography.Text>
                {integrations.unresolvedReferences.map((connectorName) => (
                  <div
                    key={connectorName}
                    style={{
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 10,
                      padding: "10px 12px",
                    }}
                  >
                    <Typography.Text>{connectorName}</Typography.Text>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        )}
      </AevatarPanel>

      <AevatarPanel
        extra={
          <AevatarStatusTag
            domain="observation"
            label={contextProvenance.label}
            status={contextProvenance.status}
          />
        }
        title="Trust Summary"
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
        title="Team home"
        content="Open a concrete team route before entering this team home."
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
      content={`${lens.subtitle}. Start here to see whether the team needs attention, what changed most recently, and where to edit it.`}
      extra={
        <Space key="team-detail-actions" wrap>
          <Button
            onClick={() => history.push(teamBuilderRoute)}
            type="primary"
          >
            {teamBuilderLabel}
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
            Open Activity
          </Button>
          <Button
            onClick={() =>
              history.push(
                buildRuntimeExplorerHref({
                  actorId: effectiveActorId || lens.graph.focusActorId || undefined,
                  scopeId,
                  serviceId: lens.currentService?.serviceId || undefined,
                }),
              )
            }
          >
            Open Topology
          </Button>
        </Space>
      }
      titleHelp="This home keeps the team as the top-level story while grounding every module in current runtime, service, and binding truth."
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
            <div
              style={{
                background: token.colorBgLayout,
                bottom: 12,
                position: "sticky",
                zIndex: 2,
              }}
            >
              <Segmented
                block
                onChange={(value) =>
                  setCompactPanel(value as "activity" | "details")
                }
                options={[
                  {
                    label: `Activity · ${renderObservationLabel(
                      activityProvenance.status,
                    )}`,
                    value: "activity",
                  },
                  {
                    label: `Details · ${renderObservationLabel(
                      contextProvenance.status,
                    )}`,
                    value: "details",
                  },
                ]}
                value={compactPanel}
              />
            </div>
            {compactPanel === "activity" ? activityRail : contextAside}
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
