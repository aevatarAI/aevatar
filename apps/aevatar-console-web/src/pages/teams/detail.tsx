import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from "@aevatar-react-sdk/types";
import {
  BranchesOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
} from "@ant-design/icons";
import { Alert, Button, Space, Tooltip, Typography, theme } from "antd";
import { useQuery } from "@tanstack/react-query";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  readTeamDetailRouteState,
  type TeamDetailTab,
} from "@/shared/navigation/teamRoutes";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import { studioApi } from "@/shared/studio/api";
import {
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import type { StudioWorkflowDocument } from "@/shared/studio/models";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { resolveWorkflowOperationalUnit } from "./workflowOperationalUnits";
import {
  deriveTeamIntegrationsSummary,
  deriveTeamWorkflowRoleBindings,
} from "./runtime/teamIntegrations";
import type { TeamPlaybackSummary } from "./runtime/teamRuntimeLens";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

type ObservationStatus = "live" | "delayed" | "partial" | "unavailable";

type ObservationBadge = {
  label: string;
  status: ObservationStatus;
};

type TeamCompositionRow = {
  key: string;
  name: string;
  summary: string;
  kind: string;
};

type TeamTabOption = {
  label: string;
  value: TeamDetailTab;
};

type MemberLike = {
  actorId: string;
  actorType: string;
};

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function compactId(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized) {
    return "n/a";
  }

  const segment = normalized.split("/").pop() || normalized;
  return segment.split(":").pop() || segment;
}

function formatTeamTabLabel(tab: TeamDetailTab): string {
  switch (tab) {
    case "topology":
      return "事件拓扑";
    case "events":
      return "事件流";
    case "members":
      return "团队成员";
    case "connectors":
      return "连接器";
    case "advanced":
      return "高级编辑";
    default:
      return "概览";
  }
}

function normalizeStatus(value: string | null | undefined): string {
  return trimText(value).toLowerCase();
}

function formatFriendlyStatus(value: string | null | undefined): string {
  const normalized = normalizeStatus(value);
  if (!normalized) {
    return "--";
  }

  switch (normalized) {
    case "active":
    case "running":
    case "processing":
      return "运行中";
    case "published":
      return "已发布";
    case "default":
      return "默认版本";
    case "completed":
    case "finished":
    case "succeeded":
    case "success":
      return "已完成";
    case "draft":
      return "草稿";
    case "retired":
      return "已停用";
    case "failed":
    case "error":
    case "cancelled":
    case "degraded":
      return "运行异常";
    case "waiting":
    case "waiting_signal":
    case "waiting_approval":
    case "human_input":
    case "human_approval":
    case "suspended":
    case "blocked":
      return "等待处理";
    default:
      return trimText(value) || "--";
  }
}

function formatCompositionKind(kind: string): string {
  switch (normalizeStatus(kind)) {
    case "workflow role":
      return "角色";
    case "workflow":
      return "流程";
    case "service":
      return "服务";
    case "actor":
      return "Actor";
    case "runtime":
      return "运行";
    case "script":
      return "脚本";
    case "gagent":
      return "Agent";
    default:
      return kind || "--";
  }
}

function formatConnectorTypeLabel(kind: string): string {
  switch (normalizeStatus(kind)) {
    case "http":
      return "HTTP API";
    case "cli":
      return "CLI";
    case "mcp":
      return "MCP";
    default:
      return kind || "--";
  }
}

function formatConnectorEnabledLabel(enabled: boolean): string {
  return enabled ? "可用" : "停用";
}

function formatObservationLabel(status: ObservationStatus): string {
  switch (status) {
    case "live":
      return "实时";
    case "delayed":
      return "稍有延迟";
    case "partial":
      return "部分可见";
    default:
      return "暂不可用";
  }
}

function formatEdgeTypeLabel(value: string | null | undefined): string {
  const normalized = normalizeStatus(value);
  switch (normalized) {
    case "handoff":
      return "交接";
    case "depends_on":
      return "依赖";
    case "invokes":
      return "调用";
    default:
      return trimText(value) || "--";
  }
}

function formatStepTypeLabel(value: string | null | undefined): string {
  const normalized = normalizeStatus(value);
  switch (normalized) {
    case "human_approval":
      return "人工确认";
    case "human_input":
      return "人工输入";
    case "llm_call":
      return "LLM 调用";
    case "tool_call":
      return "工具调用";
    case "branch":
      return "条件分支";
    default:
      return trimText(value) || "--";
  }
}

type PillTone = "danger" | "info" | "neutral" | "success" | "warning";

function resolveTonePillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  tone: PillTone,
): React.CSSProperties {
  switch (tone) {
    case "success":
      return {
        background: "rgba(82, 196, 26, 0.12)",
        color: token.colorSuccess,
      };
    case "info":
      return {
        background: "rgba(24, 144, 255, 0.08)",
        color: token.colorInfo,
      };
    case "warning":
      return {
        background: "rgba(250, 173, 20, 0.12)",
        color: token.colorWarning,
      };
    case "danger":
      return {
        background: "rgba(255, 77, 79, 0.12)",
        color: token.colorError,
      };
    default:
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
  }
}

function resolveObservationPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  status: ObservationStatus,
): React.CSSProperties {
  switch (status) {
    case "live":
      return resolveTonePillStyle(token, "success");
    case "delayed":
      return resolveTonePillStyle(token, "warning");
    case "partial":
      return resolveTonePillStyle(token, "info");
    default:
      return resolveTonePillStyle(token, "neutral");
  }
}

function resolveCompositionKindPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  kind: string,
): React.CSSProperties {
  switch (normalizeStatus(kind)) {
    case "workflow role":
      return resolveTonePillStyle(token, "info");
    case "workflow":
      return resolveTonePillStyle(token, "success");
    case "service":
      return resolveTonePillStyle(token, "warning");
    case "actor":
    case "runtime":
      return resolveTonePillStyle(token, "neutral");
    default:
      return resolveTonePillStyle(token, "neutral");
  }
}

function resolveStatusPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  value: string | null | undefined,
): React.CSSProperties {
  const normalized = normalizeStatus(value);

  if (
    [
      "active",
      "running",
      "processing",
      "completed",
      "finished",
      "succeeded",
      "success",
      "published",
      "default",
    ].includes(normalized)
  ) {
    return {
      background: "rgba(24, 144, 255, 0.08)",
      color: token.colorInfo,
    };
  }

  if (
    [
      "draft",
      "waiting",
      "waiting_signal",
      "waiting_approval",
      "human_input",
      "human_approval",
      "suspended",
      "blocked",
    ].includes(normalized)
  ) {
    return {
      background: "rgba(250, 173, 20, 0.12)",
      color: token.colorWarning,
    };
  }

  if (
    ["failed", "error", "cancelled", "degraded", "retired"].includes(normalized)
  ) {
    return {
      background: "rgba(255, 77, 79, 0.12)",
      color: token.colorError,
    };
  }

  return {
    background: token.colorFillQuaternary,
    color: token.colorTextSecondary,
  };
}

function formatCompactTimestamp(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized) {
    return "暂无";
  }

  const parsed = new Date(normalized);
  if (Number.isNaN(parsed.getTime())) {
    return "暂无";
  }

  return parsed.toLocaleString("zh-CN", {
    day: "2-digit",
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    month: "2-digit",
  });
}

function formatDetailedTimestamp(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized) {
    return "--";
  }

  const parsed = new Date(normalized);
  if (Number.isNaN(parsed.getTime())) {
    return "--";
  }

  return parsed.toLocaleString("zh-CN", {
    day: "2-digit",
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    month: "2-digit",
    second: "2-digit",
    year: "numeric",
  });
}

function readWorkflowRoleName(role: Record<string, unknown>, index: number): string {
  return (
    trimText(typeof role.name === "string" ? role.name : "") ||
    trimText(typeof role.id === "string" ? role.id : "") ||
    `role-${index + 1}`
  );
}

function readWorkflowRoleConnectors(role: Record<string, unknown>): string[] {
  const connectors = Array.isArray(role.connectors) ? role.connectors : [];
  return connectors
    .map((entry) => {
      if (typeof entry === "string") {
        return trimText(entry);
      }
      if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
        return "";
      }
      const record = entry as Record<string, unknown>;
      return trimText(
        typeof record.name === "string"
          ? record.name
          : typeof record.id === "string"
            ? record.id
            : "",
      );
    })
    .filter(Boolean);
}

function deriveTeamCompositionRows(input: {
  documents: readonly StudioWorkflowDocument[];
  fallbackMembers: readonly MemberLike[];
  implementationKind: string | null | undefined;
}): TeamCompositionRow[] {
  const rows: TeamCompositionRow[] = [];
  const seen = new Set<string>();

  input.documents.forEach((document) => {
    const roles = Array.isArray(document.roles) ? document.roles : [];
    roles.forEach((role, index) => {
      if (!role || typeof role !== "object" || Array.isArray(role)) {
        return;
      }

      const record = role as Record<string, unknown>;
      const name = readWorkflowRoleName(record, index);
      const dedupeKey = name.toLowerCase();
      if (seen.has(dedupeKey)) {
        return;
      }
      seen.add(dedupeKey);

      const connectors = readWorkflowRoleConnectors(record);
      const provider = trimText(
        typeof record.provider === "string" ? record.provider : "",
      );
      const model = trimText(typeof record.model === "string" ? record.model : "");
      const summaryParts: string[] = [];

      if (connectors.length > 0) {
        summaryParts.push(connectors.slice(0, 3).join("、"));
      }
      if (provider || model) {
        summaryParts.push([provider, model].filter(Boolean).join(" / "));
      }
      if (summaryParts.length === 0) {
        summaryParts.push("--");
      }

      rows.push({
        key: `role:${name}`,
        kind: "workflow role",
        name,
        summary: summaryParts.join("，"),
      });
    });
  });

  if (rows.length > 0) {
    return rows;
  }

  return input.fallbackMembers.map((member) => ({
    key: `member:${member.actorId}`,
    kind: trimText(input.implementationKind) || "runtime",
    name: trimText(member.actorType) || compactId(member.actorId),
    summary: trimText(member.actorId) || "--",
  }));
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

const SignalCard: React.FC<{
  caption?: React.ReactNode;
  captionMonospace?: boolean;
  icon?: React.ReactNode;
  label: React.ReactNode;
  value: React.ReactNode;
}> = ({ caption, captionMonospace = false, icon, label, value }) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        background: token.colorFillAlter,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 22,
        boxShadow: token.boxShadowSecondary,
        display: "flex",
        flexDirection: "column",
        gap: 10,
        minHeight: 120,
        padding: 18,
      }}
    >
      <Space align="center" size={10}>
        {icon ? <span style={{ color: token.colorPrimary }}>{icon}</span> : null}
        <Typography.Text style={{ fontSize: 13 }} type="secondary">
          {label}
        </Typography.Text>
      </Space>
      <Typography.Title level={4} style={{ margin: 0 }}>
        {value}
      </Typography.Title>
      {typeof caption === "string" ? (
        <Tooltip placement="topLeft" title={caption}>
          <Typography.Text
            ellipsis
            style={{
              display: "block",
              fontFamily: captionMonospace ? factValueFontFamily : undefined,
              fontSize: 13,
              maxWidth: "100%",
            }}
            type="secondary"
          >
            {caption}
          </Typography.Text>
        </Tooltip>
      ) : caption ? (
        <Typography.Text style={{ fontSize: 13 }} type="secondary">
          {caption}
        </Typography.Text>
      ) : null}
    </div>
  );
};

const OverviewMetricCard: React.FC<{
  accent?: boolean;
  label: string;
  value: React.ReactNode;
}> = ({ accent = false, label, value }) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 24,
        boxShadow: token.boxShadowSecondary,
        display: "flex",
        flexDirection: "column",
        gap: 12,
        minHeight: 132,
        padding: 24,
      }}
    >
      <Typography.Title
        level={2}
        style={{
          color: accent ? token.colorPrimary : token.colorText,
          margin: 0,
        }}
      >
        {value}
      </Typography.Title>
      <Typography.Text style={{ fontSize: 13 }} type="secondary">
        {label}
      </Typography.Text>
    </div>
  );
};

const DetailPill: React.FC<{
  compact?: boolean;
  style?: React.CSSProperties;
  text: string;
}> = ({ compact = false, style, text }) => (
  <span
    style={{
      borderRadius: 999,
      display: "inline-flex",
      fontSize: compact ? 12 : 13,
      fontWeight: 600,
      lineHeight: 1,
      padding: compact ? "7px 10px" : "10px 14px",
      whiteSpace: "nowrap",
      ...style,
    }}
  >
    {text}
  </span>
);

const factValueFontFamily =
  '"SFMono-Regular", "SF Mono", Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace';

const FactLine: React.FC<{
  rows?: number;
  secondary?: boolean;
  text: string;
}> = ({ rows = 1, secondary = false, text }) => {
  const normalized = text || "--";

  return (
    <Tooltip placement="topLeft" title={normalized}>
      <Typography.Text
        strong={!secondary}
        style={{
          display: "-webkit-box",
          fontFamily: factValueFontFamily,
          overflow: "hidden",
          overflowWrap: "anywhere",
          textOverflow: "ellipsis",
          WebkitBoxOrient: "vertical",
          WebkitLineClamp: rows,
          wordBreak: "break-word",
        }}
        type={secondary ? "secondary" : undefined}
      >
        {normalized}
      </Typography.Text>
    </Tooltip>
  );
};

const TeamDetailPage: React.FC = () => {
  const routeState = React.useMemo(() => readTeamDetailRouteState(), []);
  const scopeId = routeState.scopeId.trim();
  const [preferredServiceId, setPreferredServiceId] = React.useState(
    routeState.serviceId,
  );
  const [activeTab, setActiveTab] = React.useState<TeamDetailTab>(routeState.tab);
  const [selectedActorId, setSelectedActorId] = React.useState("");
  const { token } = theme.useToken();

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
  } = useTeamRuntimeLens(scopeId, {
    preferredRunId: routeState.runId,
    preferredServiceId,
  });

  const workspaceSettingsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryFn: () => studioApi.getWorkspaceSettings(),
    queryKey: ["teams", "workspace-settings"],
    retry: false,
  });

  const connectorCatalogQuery = useQuery({
    enabled: scopeId.length > 0,
    queryFn: () => studioApi.getConnectorCatalog(),
    queryKey: ["teams", "connector-catalog"],
    retry: false,
  });

  const fallbackWorkflowSummary = React.useMemo(() => {
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

    for (const workflowNameHint of workflowNameHints) {
      const matched =
        workflows.find(
          (workflow) =>
            trimText(workflow.workflowName) === workflowNameHint ||
            trimText(workflow.displayName) === workflowNameHint,
        ) ?? null;
      if (matched) {
        return matched;
      }
    }

    return workflows.length === 1 ? workflows[0] : null;
  }, [
    lens.activeRevision,
    lens.currentRun,
    lens.currentRunAudit,
    lens.playback.workflowName,
    workflowsQuery.data,
  ]);

  const activeWorkflowSummary = React.useMemo(() => {
    if (trimText(routeState.workflowId)) {
      return (
        workflowsQuery.data?.find(
          (workflow) => trimText(workflow.workflowId) === trimText(routeState.workflowId),
        ) ?? null
      );
    }

    return fallbackWorkflowSummary;
  }, [fallbackWorkflowSummary, routeState.workflowId, workflowsQuery.data]);

  const focusedOperationalUnit = React.useMemo(() => {
    if (!activeWorkflowSummary) {
      return null;
    }

    const loadedServiceId =
      trimText(lens.currentService?.serviceId) || trimText(preferredServiceId);
    return resolveWorkflowOperationalUnit({
      binding: bindingQuery.data ?? null,
      preferredRunId: routeState.runId,
      preferredServiceId,
      runs: runsQuery.data?.runs ?? [],
      services: servicesQuery.data ?? [],
      signals: {
        runtimeAvailableByServiceId:
          runsQuery.isSuccess && loadedServiceId
            ? new Set([loadedServiceId])
            : new Set<string>(),
        servicesAvailable: servicesQuery.isSuccess,
      },
      workflow: activeWorkflowSummary,
    });
  }, [
    activeWorkflowSummary,
    bindingQuery.data,
    lens.currentService?.serviceId,
    preferredServiceId,
    routeState.runId,
    runsQuery.data?.runs,
    runsQuery.isSuccess,
    servicesQuery.data,
    servicesQuery.isSuccess,
  ]);

  React.useEffect(() => {
    const nextServiceId = trimText(focusedOperationalUnit?.matchedService?.serviceId);
    if (!trimText(routeState.workflowId) || !nextServiceId) {
      return;
    }
    if (nextServiceId !== trimText(preferredServiceId)) {
      setPreferredServiceId(nextServiceId);
    }
  }, [
    focusedOperationalUnit?.matchedService?.serviceId,
    preferredServiceId,
    routeState.workflowId,
  ]);

  const teamWorkflowDetailQuery = useQuery({
    enabled:
      scopeId.length > 0 &&
      lens.activeRevision?.implementationKind === "workflow" &&
      trimText(activeWorkflowSummary?.workflowId).length > 0,
    queryFn: () =>
      scopesApi.getWorkflowDetail(scopeId, activeWorkflowSummary?.workflowId ?? ""),
    queryKey: [
      "teams",
      "workflow-detail",
      scopeId,
      activeWorkflowSummary?.workflowId ?? "",
      lens.activeRevision?.revisionId ?? "",
    ],
    retry: false,
  });

  const teamWorkflowDocumentsQuery = useQuery({
    enabled:
      lens.activeRevision?.implementationKind === "workflow" &&
      Boolean(teamWorkflowDetailQuery.data?.available) &&
      trimText(teamWorkflowDetailQuery.data?.source?.workflowYaml).length > 0,
    queryFn: async (): Promise<StudioWorkflowDocument[]> => {
      const source = teamWorkflowDetailQuery.data?.source;
      if (!source) {
        return [];
      }

      const workflowYamls = [
        trimText(source.workflowYaml),
        ...Object.values(source.inlineWorkflowYamls ?? {}).map((yaml) => trimText(yaml)),
      ].filter(Boolean);
      const parsedDocuments = await Promise.all(
        [...new Set(workflowYamls)].map(async (yaml) => {
          const parsed = await studioApi.parseYaml({ yaml });
          return parsed.document ?? null;
        }),
      );

      return parsedDocuments.filter(
        (document): document is StudioWorkflowDocument => Boolean(document),
      );
    },
    queryKey: [
      "teams",
      "workflow-documents",
      scopeId,
      activeWorkflowSummary?.workflowId ?? "",
      lens.activeRevision?.revisionId ?? "",
    ],
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
        connectorCatalog: connectorCatalogQuery.data ?? null,
        teamWorkflowRoles:
          lens.activeRevision?.implementationKind !== "workflow"
            ? []
            : teamScopedRoleLoading
              ? undefined
              : teamScopedRoleUnavailable
                ? null
                : deriveTeamWorkflowRoleBindings(teamWorkflowDocumentsQuery.data ?? []),
        workflowDocumentCount: teamWorkflowDocumentsQuery.data?.length ?? 0,
        workspaceSettings: workspaceSettingsQuery.data ?? null,
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

  const runtimeServiceId =
    focusedOperationalUnit?.matchedService?.serviceId ||
    lens.currentService?.serviceId ||
    lens.currentRun?.serviceId ||
    undefined;

  const teamBuilderRoute =
    trimText(activeWorkflowSummary?.workflowId).length > 0
      ? buildStudioWorkflowEditorRoute({
          scopeId,
          workflowId: activeWorkflowSummary?.workflowId,
        })
      : buildStudioWorkflowWorkspaceRoute({ scopeId });

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

  React.useEffect(() => {
    if (availableActorIds.length === 0) {
      setSelectedActorId("");
      return;
    }
    if (!selectedActorId || !availableActorIds.includes(selectedActorId)) {
      setSelectedActorId(defaultSelectedActorId || availableActorIds[0]);
    }
  }, [availableActorIds, defaultSelectedActorId, selectedActorId]);

  const effectiveActorId = selectedActorId || defaultSelectedActorId;
  const selectedGraphNodes = lens.graph.nodes.map((node) => ({
    ...node,
    isFocused: effectiveActorId ? node.actorId === effectiveActorId : node.isFocused,
  }));
  const visibleGraphRelationships = lens.graph.relationships.filter(
    (relationship) =>
      !effectiveActorId ||
      relationship.fromActorId === effectiveActorId ||
      relationship.toActorId === effectiveActorId,
  );
  const selectedFocusReason =
    effectiveActorId && effectiveActorId !== lens.graph.focusActorId
      ? `当前视角已锁定在 ${compactId(effectiveActorId)}。${lens.graph.focusReason}`
      : lens.graph.focusReason;
  const visiblePlaybackSteps =
    lens.playback.steps.filter(
      (step) => !effectiveActorId || step.actorId === effectiveActorId,
    ) || lens.playback.steps;

  const graphProvenance: ObservationBadge = actorGraphQuery.isError
    ? { label: formatObservationLabel("unavailable"), status: "unavailable" }
    : lens.graph.available
      ? { label: formatObservationLabel("live"), status: "live" }
      : { label: formatObservationLabel("partial"), status: "partial" };
  const playbackProvenance: ObservationBadge = currentRunAuditQuery.isError
    ? { label: formatObservationLabel("unavailable"), status: "unavailable" }
    : lens.playback.available
      ? { label: formatObservationLabel("delayed"), status: "delayed" }
      : { label: formatObservationLabel("partial"), status: "partial" };
  const integrationsProvenance: ObservationBadge =
    workspaceSettingsQuery.isError && connectorCatalogQuery.isError
      ? { label: formatObservationLabel("unavailable"), status: "unavailable" }
      : integrations.available
        ? { label: formatObservationLabel("delayed"), status: "delayed" }
        : { label: formatObservationLabel("partial"), status: "partial" };

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
    [lens.currentRun?.actorId, lens.playback, runtimeServiceId, scopeId],
  );

  const handleOpenPlaybackActor = React.useCallback(
    (actorId?: string | null, runId?: string | null) => {
      const resolvedActorId =
        actorId?.trim() || lens.playback.rootActorId?.trim() || "";
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

  const teamTitle = activeWorkflowSummary?.displayName || lens.title;
  const activeWorkflowId =
    trimText(activeWorkflowSummary?.workflowId) || trimText(routeState.workflowId);
  const teamCompositionRows = React.useMemo(
    () =>
      deriveTeamCompositionRows({
        documents: teamWorkflowDocumentsQuery.data ?? [],
        fallbackMembers: lens.members,
        implementationKind: lens.activeRevision?.implementationKind,
      }),
    [lens.activeRevision?.implementationKind, lens.members, teamWorkflowDocumentsQuery.data],
  );
  const latestVisibleUpdate =
    lens.currentRun?.lastUpdatedAt ||
    lens.currentRunAudit?.summary.lastUpdatedAt ||
    activeWorkflowSummary?.updatedAt ||
    "";
  const activeRunId =
    lens.currentRun?.runId || focusedOperationalUnit?.latestRun?.runId || "";
  const currentRevisionId = trimText(lens.activeRevision?.revisionId) || "--";
  const currentRevisionStatus =
    trimText(lens.activeRevision?.servingState) ||
    trimText(lens.activeRevision?.status) ||
    "--";
  const currentDeploymentStatus =
    trimText(lens.currentService?.deploymentStatus) ||
    trimText(lens.activeRevision?.status) ||
    "--";
  const currentHeaderStatus =
    trimText(lens.currentRun?.completionStatus) || currentDeploymentStatus;
  const currentHeaderStatusFriendly = formatFriendlyStatus(currentHeaderStatus);
  const currentRevisionFriendly = formatFriendlyStatus(currentRevisionStatus);
  const currentDeploymentFriendly = formatFriendlyStatus(currentDeploymentStatus);
  const currentDeploymentId =
    trimText(lens.activeRevision?.deploymentId) ||
    trimText(lens.currentService?.deploymentId) ||
    "--";
  const currentServiceKey =
    trimText(lens.currentService?.serviceKey) ||
    trimText(activeWorkflowSummary?.serviceKey) ||
    "--";
  const currentServiceDisplayName =
    trimText(lens.currentService?.displayName) || "--";
  const currentRunStatus = trimText(lens.currentRun?.completionStatus) || "--";
  const currentRunFriendly = activeRunId
    ? formatFriendlyStatus(currentRunStatus)
    : "暂无运行";
  const currentServiceFriendly =
    currentServiceDisplayName !== "--"
      ? currentServiceDisplayName
      : runtimeServiceId || "--";
  const currentVersionFriendly =
    currentRevisionFriendly !== "--"
      ? currentRevisionFriendly
      : currentDeploymentFriendly;
  const currentServicePillText =
    currentServiceFriendly !== "--"
      ? `服务 · ${currentServiceFriendly}`
      : "服务待配置";
  const currentDeploymentPillText =
    currentVersionFriendly !== "--"
      ? `版本 · ${currentVersionFriendly}`
      : "版本待确认";
  const currentRunPillText = activeRunId
    ? `运行 · ${currentRunFriendly}`
    : "暂无近期运行";
  const workflowNameValue =
    trimText(activeWorkflowSummary?.workflowName) ||
    trimText(lens.activeRevision?.workflowName) ||
    "--";
  const currentActorId =
    trimText(lens.currentRun?.actorId) ||
    trimText(lens.activeRevision?.primaryActorId) ||
    "--";
  const currentStateVersion =
    lens.currentRun?.stateVersion != null ? String(lens.currentRun.stateVersion) : "--";
  const currentLastEventId = trimText(lens.currentRun?.lastEventId) || "--";
  const currentEndpointCount = lens.currentService?.endpoints.length ?? 0;
  const currentPolicyCount = lens.currentService?.policyIds.length ?? 0;
  const enabledConnectorCount = integrations.items.filter((item) => item.enabled).length;
  const connectorHighlights = React.useMemo(
    () =>
      integrations.items
        .filter((item) => item.usedByRoles.length > 0)
        .slice(0, 3)
        .map((item) => item.name),
    [integrations.items],
  );
  const compositionDisplayRows = React.useMemo(() => {
    if (teamCompositionRows.length > 0) {
      return teamCompositionRows;
    }

    return [
      {
        key: "fallback-workflow",
        kind: "workflow",
        name: "团队流程",
        summary: workflowNameValue !== "--" ? workflowNameValue : activeWorkflowId || "--",
      },
      {
        key: "fallback-actor",
        kind: "actor",
        name: "当前执行",
        summary: activeRunId ? `${currentRunFriendly} · ${compactId(currentActorId)}` : "暂无最近运行",
      },
      {
        key: "fallback-service",
        kind: "service",
        name: "主服务",
        summary: currentServiceFriendly,
      },
    ];
  }, [
    activeWorkflowId,
    activeRunId,
    currentActorId,
    currentRunFriendly,
    currentServiceKey,
    currentServiceFriendly,
    runtimeServiceId,
    teamCompositionRows,
    workflowNameValue,
  ]);
  const runtimeSummaryRows = [
    {
      badge: currentRevisionStatus,
      badgeColor: currentRevisionStatus === "Active" ? "success" : undefined,
      key: "revisionId",
      label: "当前版本",
      note: `revisionId: ${currentRevisionId}`,
      value: currentRevisionFriendly,
    },
    {
      badge: runtimeServiceId || "--",
      badgeColor: runtimeServiceId ? "success" : undefined,
      key: "serviceKey",
      label: "主服务",
      note: `serviceId: ${runtimeServiceId || "--"}`,
      value: currentServiceFriendly,
    },
    {
      badge: currentRunStatus,
      badgeColor: currentRunStatus !== "--" ? "success" : undefined,
      key: "runId",
      label: "最近状态",
      note: activeRunId ? `runId: ${activeRunId}` : `actorId: ${currentActorId}`,
      value: currentRunFriendly,
    },
    {
      badge: currentStateVersion !== "--" ? `v${currentStateVersion}` : "--",
      key: "lastUpdatedAt",
      label: "最近更新时间",
      note: `lastEventId: ${currentLastEventId}`,
      value: latestVisibleUpdate ? formatCompactTimestamp(latestVisibleUpdate) : "--",
    },
    {
      badge: `${integrations.linkedConnectorCount}`,
      badgeColor: integrations.linkedConnectorCount > 0 ? "success" : undefined,
      key: "connectors",
      label: "连接器",
      note:
        connectorHighlights.length > 0
          ? connectorHighlights.join("、")
          : `catalog: ${integrations.items.length}`,
      value:
        integrations.linkedConnectorCount > 0
          ? `${integrations.linkedConnectorCount} 个已绑定`
          : "未配置",
    },
  ];

  const tabOptions: TeamTabOption[] = [
    { label: "概览", value: "overview" },
    { label: "事件拓扑", value: "topology" },
    { label: "事件流", value: "events" },
    { label: "团队成员", value: "members" },
    { label: "连接器", value: "connectors" },
    { label: "团队配置", value: "advanced" },
  ];

  const initialLoading =
    bindingQuery.isLoading ||
    servicesQuery.isLoading ||
    actorsQuery.isLoading ||
    workflowsQuery.isLoading ||
    scriptsQuery.isLoading;

  const pushTeamTab = React.useCallback(
    (tab: TeamDetailTab) => {
      setActiveTab(tab);
      history.push(
        buildTeamDetailHref({
          scopeId,
          workflowId: activeWorkflowId || undefined,
          serviceId: runtimeServiceId,
          runId: lens.currentRun?.runId || lens.playback.currentRunId || undefined,
          tab,
        }),
      );
    },
    [
      activeWorkflowId,
      lens.currentRun?.runId,
      lens.playback.currentRunId,
      runtimeServiceId,
      scopeId,
    ],
  );

  const handleOpenConversation = React.useCallback(() => {
    if (lens.playback.currentRunId) {
      handleOpenPlaybackRun();
      return;
    }
    history.push(
      buildRuntimeRunsHref({
        scopeId,
        serviceId: runtimeServiceId,
        actorId: lens.currentRun?.actorId || undefined,
      }),
    );
  }, [
    handleOpenPlaybackRun,
    lens.currentRun?.actorId,
    lens.playback.currentRunId,
    runtimeServiceId,
    scopeId,
  ]);

  const handleOpenServiceMapping = React.useCallback(() => {
    handleOpenPlaybackActor(
      effectiveActorId ||
        lens.graph.focusActorId ||
        lens.playback.rootActorId ||
        lens.currentRun?.actorId,
      lens.currentRun?.runId || lens.playback.currentRunId,
    );
  }, [
    effectiveActorId,
    handleOpenPlaybackActor,
    lens.currentRun?.actorId,
    lens.currentRun?.runId,
    lens.graph.focusActorId,
    lens.playback.currentRunId,
    lens.playback.rootActorId,
  ]);

  const renderOverviewTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
        <div
          style={{
            background: token.colorBgContainer,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 24,
            boxShadow: token.boxShadowSecondary,
            display: "flex",
            flexDirection: "column",
            gap: 18,
            padding: 24,
          }}
        >
          <div
            style={{
              alignItems: "flex-start",
              display: "flex",
              flexWrap: "wrap",
              gap: 12,
              justifyContent: "space-between",
            }}
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <Space wrap size={8}>
                <Typography.Text strong style={{ fontSize: 16 }}>
                  当前态势
                </Typography.Text>
                <DetailPill
                  style={resolveStatusPillStyle(token, currentHeaderStatus)}
                  text={currentHeaderStatusFriendly}
                />
              </Space>
            </div>
            <Space wrap size={[8, 8]}>
              <DetailPill
                style={{
                  background: token.colorInfoBg,
                  border: `1px solid ${token.colorInfoBorder}`,
                  color: token.colorInfo,
                }}
                text={currentServicePillText}
              />
              <DetailPill
                style={resolveStatusPillStyle(token, currentDeploymentStatus)}
                text={currentDeploymentPillText}
              />
              <DetailPill
                style={resolveStatusPillStyle(token, currentRunStatus)}
                text={currentRunPillText}
              />
            </Space>
          </div>
          <div
            style={{
              display: "grid",
              gap: 14,
              gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
            }}
          >
            <SignalCard
              label="这支团队现在服务于"
              captionMonospace
              value={currentServiceFriendly}
              caption={currentServiceKey}
            />
            <SignalCard
              label="当前运行状态"
              value={currentRunFriendly}
              caption={activeRunId || "--"}
            />
            <SignalCard
              label="最近一次更新"
              value={formatCompactTimestamp(latestVisibleUpdate)}
              caption={formatDetailedTimestamp(latestVisibleUpdate)}
            />
          </div>
        </div>
        <div
          style={{
            display: "grid",
            gap: 18,
            gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
          }}
        >
          <OverviewMetricCard
            label="团队在做什么"
            value={workflowNameValue !== "--" ? workflowNameValue : teamTitle}
          />
          <OverviewMetricCard label="主服务入口" value={currentServiceFriendly} />
          <OverviewMetricCard label="当前状态" value={currentRunFriendly} />
          <OverviewMetricCard
            accent
            label="当前版本状态"
            value={currentVersionFriendly}
          />
        </div>

        <div
          style={{
            display: "grid",
            gap: 18,
            gridTemplateColumns: "repeat(auto-fit, minmax(360px, 1fr))",
          }}
        >
          <div
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 24,
              boxShadow: token.boxShadowSecondary,
              display: "flex",
              flexDirection: "column",
              gap: 18,
              padding: 24,
            }}
          >
            <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
              <div>
                <Typography.Title level={3} style={{ margin: 0 }}>
                  团队构成
                </Typography.Title>
              </div>
            </div>
            {compositionDisplayRows.length > 0 ? (
              compositionDisplayRows.map((row, index) => (
                <div
                  key={row.key}
                  style={{
                    alignItems: "start",
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(120px, 180px) minmax(0, 1fr) max-content",
                    paddingTop: index === 0 ? 0 : 16,
                  }}
                >
                  <Typography.Text strong>{row.name}</Typography.Text>
                  <FactLine rows={3} secondary text={row.summary} />
                  <DetailPill
                    compact
                    style={resolveCompositionKindPillStyle(token, row.kind)}
                    text={formatCompositionKind(row.kind)}
                  />
                </div>
              ))
            ) : (
              <AevatarInspectorEmpty
                title="暂无团队构成"
                description="当前还没有足够事实来生成团队构成。"
              />
            )}
          </div>

          <div
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 24,
              boxShadow: token.boxShadowSecondary,
              display: "flex",
              flexDirection: "column",
              gap: 18,
              padding: 24,
            }}
          >
            <div>
              <Typography.Title level={3} style={{ margin: 0 }}>
                运行摘要
              </Typography.Title>
            </div>
            {runtimeSummaryRows.map((row, index) => (
              <div
                key={row.key}
                style={{
                  alignItems: "start",
                  borderTop:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "minmax(96px, 128px) minmax(0, 1fr) max-content",
                  paddingTop: index === 0 ? 0 : 16,
                }}
              >
                <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                  {row.label}
                </Typography.Text>
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <FactLine rows={2} text={String(row.value)} />
                  <FactLine rows={3} secondary text={String(row.note)} />
                </div>
                <div
                  style={{
                    alignSelf: "start",
                    display: "flex",
                    justifyContent: "flex-end",
                    minWidth: 0,
                    paddingTop: 2,
                  }}
                >
                  <DetailPill
                    compact
                    style={
                      row.badgeColor === "success"
                        ? resolveTonePillStyle(token, "success")
                        : resolveStatusPillStyle(token, row.badge)
                    }
                    text={row.badge}
                  />
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    );
  };

  const renderTopologyTab = () => {
    return (
      <AevatarPanel
        title="事件拓扑"
        extra={
          <DetailPill
            compact
            style={resolveObservationPillStyle(token, graphProvenance.status)}
            text={graphProvenance.label}
          />
        }
      >
        {actorGraphQuery.isLoading ? (
          <AevatarInspectorEmpty description="正在加载团队拓扑。" />
        ) : actorGraphQuery.isError ? (
          <AevatarInspectorEmpty
            title="拓扑暂不可用"
            description="当前无法读取团队拓扑，请稍后重试。"
          />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              <SignalCard
                icon={<EyeOutlined />}
                label="焦点成员"
                value={compactId(effectiveActorId)}
                caption={selectedFocusReason}
              />
              <SignalCard
                icon={<BranchesOutlined />}
                label="可见关系"
                value={visibleGraphRelationships.length}
                caption={`${selectedGraphNodes.length} 个节点参与当前焦点视图`}
              />
            </div>
            <Alert description={lens.graph.stageSummary} showIcon type="info" />
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              {visibleGraphRelationships.length > 0 ? (
                visibleGraphRelationships.map((relationship) => (
                  <div
                    key={relationship.key}
                    style={{
                      border: `1px solid ${token.colorBorderSecondary}`,
                      borderRadius: 14,
                      padding: 14,
                    }}
                  >
                    <Typography.Text strong>
                      {compactId(relationship.fromActorId)} → {compactId(relationship.toActorId)}
                    </Typography.Text>
                    <div style={{ marginTop: 8 }}>
                      <DetailPill
                        compact
                        style={resolveTonePillStyle(token, "info")}
                        text={formatEdgeTypeLabel(relationship.edgeType)}
                      />
                    </div>
                  </div>
                ))
              ) : (
                <AevatarInspectorEmpty
                  title="暂无可见关系"
                  description="当前没有更多可见的事件拓扑关系。"
                />
              )}
            </div>
          </div>
        )}
      </AevatarPanel>
    );
  };

  const renderEventsTab = () => {
    return (
      <AevatarPanel
        title="事件流"
        extra={
          <DetailPill
            compact
            style={resolveObservationPillStyle(token, playbackProvenance.status)}
            text={playbackProvenance.label}
          />
        }
      >
        {runsQuery.isLoading ? (
          <AevatarInspectorEmpty description="正在加载最近运行。" />
        ) : runsQuery.isError ? (
          <AevatarInspectorEmpty
            title="运行信号暂不可用"
            description="当前无法读取最近运行。"
          />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div
              style={{
                background: token.colorBgContainerDisabled,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 16,
                display: "flex",
                flexDirection: "column",
                gap: 8,
                padding: 16,
              }}
            >
              <Space wrap>
                <Typography.Text strong>
                  {lens.currentRun?.runId || "当前还没有可见运行"}
                </Typography.Text>
                {lens.currentRun?.completionStatus ? (
                  <DetailPill
                    compact
                    style={resolveStatusPillStyle(token, lens.currentRun.completionStatus)}
                    text={formatFriendlyStatus(lens.currentRun.completionStatus)}
                  />
                ) : null}
              </Space>
              <Typography.Text type="secondary">
                {lens.playback.summary}
              </Typography.Text>
              <Space wrap>
                <Button onClick={handleOpenConversation}>测试对话</Button>
                <Button onClick={handleOpenServiceMapping} type="link">
                  查看服务映射
                </Button>
              </Space>
            </div>
            {visiblePlaybackSteps.length > 0 ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                {visiblePlaybackSteps.slice(0, 4).map((step) => (
                  <div
                    key={step.key}
                    style={{
                      border: `1px solid ${token.colorBorderSecondary}`,
                      borderRadius: 14,
                      padding: 14,
                    }}
                    >
                      <Space wrap>
                        <Typography.Text strong>{step.stepId}</Typography.Text>
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(token, "neutral")}
                          text={formatStepTypeLabel(step.stepType)}
                        />
                      </Space>
                      <Typography.Paragraph style={{ marginBottom: 0, marginTop: 8 }}>
                        {step.detail}
                    </Typography.Paragraph>
                  </div>
                ))}
              </div>
            ) : (
              <Typography.Text type="secondary">
                当前还没有更多可见的步骤事实。
              </Typography.Text>
            )}
          </div>
        )}
      </AevatarPanel>
    );
  };

  const renderMembersTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel title="这支团队里有哪些角色">
          {compositionDisplayRows.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {compositionDisplayRows.map((row) => (
                <div
                  key={row.key}
                  style={{
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 14,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(120px, 168px) minmax(0, 1fr) max-content",
                    padding: 14,
                  }}
                >
                  <Typography.Text strong>{row.name}</Typography.Text>
                  <FactLine rows={3} secondary text={row.summary} />
                  <DetailPill
                    compact
                    style={resolveCompositionKindPillStyle(token, row.kind)}
                    text={formatCompositionKind(row.kind)}
                  />
                </div>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="暂时还没有角色信息"
              description="当前还没有足够事实来说明这支团队由谁组成。"
            />
          )}
        </AevatarPanel>
        <AevatarPanel title="当前参与运行">
          {lens.members.length > 0 ? (
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              {lens.members.map((member) => (
                <button
                  aria-label={`选择成员 ${member.actorType} ${member.actorId}`}
                  key={member.actorId}
                  onClick={() => setSelectedActorId(member.actorId)}
                  style={{
                    background:
                      member.actorId === effectiveActorId
                        ? token.colorPrimaryBg
                        : token.colorBgContainer,
                    border: `1px solid ${
                      member.actorId === effectiveActorId
                        ? token.colorPrimaryBorder
                        : token.colorBorderSecondary
                    }`,
                    borderRadius: 14,
                    cursor: "pointer",
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                    padding: 14,
                    textAlign: "left",
                  }}
                  type="button"
                >
                  <Space wrap>
                    <Typography.Text strong>
                      {member.actorType || compactId(member.actorId)}
                    </Typography.Text>
                    {member.actorId === effectiveActorId ? (
                      <DetailPill
                        compact
                        style={resolveTonePillStyle(token, "info")}
                        text="当前焦点"
                      />
                    ) : null}
                  </Space>
                  <FactLine rows={2} secondary text={member.actorId} />
                </button>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="暂时还没有可见参与者"
              description="当前还没有观察到这支团队里的运行参与者。"
            />
          )}
        </AevatarPanel>
      </div>
    );
  };

  const renderConnectorsTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel
          title="服务与连接器"
          extra={
            <DetailPill
              compact
              style={resolveObservationPillStyle(token, integrationsProvenance.status)}
              text={integrationsProvenance.label}
            />
          }
        >
          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
            }}
          >
            <SignalCard
              icon={<DeploymentUnitOutlined />}
              label="这支团队对外提供"
              value={currentServiceFriendly}
              captionMonospace
              caption={runtimeServiceId || "--"}
            />
            <SignalCard
              icon={<BranchesOutlined />}
              label="当前可用工具"
              value={enabledConnectorCount}
              caption={`已绑定 ${integrations.linkedConnectorCount} 个 · 工作区可见 ${integrations.items.length} 个`}
            />
          </div>
        </AevatarPanel>
        <AevatarPanel title="可用连接器">
          {integrations.items.length > 0 ? (
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
              }}
            >
              {integrations.items.map((connector) => (
                <div
                  key={connector.key}
                  style={{
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 14,
                    display: "flex",
                    flexDirection: "column",
                    gap: 8,
                    padding: 14,
                  }}
                >
                  <Space wrap>
                    <Typography.Text strong>{connector.name}</Typography.Text>
                    <DetailPill
                      compact
                      style={resolveTonePillStyle(token, "info")}
                      text={formatConnectorTypeLabel(connector.type)}
                    />
                    <DetailPill
                      compact
                      style={resolveTonePillStyle(
                        token,
                        connector.enabled ? "success" : "neutral",
                      )}
                      text={formatConnectorEnabledLabel(connector.enabled)}
                    />
                  </Space>
                  <Typography.Text type="secondary">{connector.summary}</Typography.Text>
                  <Typography.Text type="secondary">
                    {connector.usedByRoles.length > 0
                      ? `当前团队会用到：${connector.usedByRoles.join("、")}`
                      : "当前团队还没有用到这个连接器。"}
                  </Typography.Text>
                </div>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="暂无连接器"
              description="当前工作区还没有可见的连接器定义。"
            />
          )}
        </AevatarPanel>
      </div>
    );
  };

  const renderAdvancedTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel title="如何继续调整这支团队">
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Typography.Text strong>
              团队流程：{workflowNameValue !== "--" ? workflowNameValue : teamTitle}
            </Typography.Text>
            <Typography.Text type="secondary">
              主服务：{currentServiceFriendly}
            </Typography.Text>
            <Typography.Text type="secondary">
              当前版本：{currentVersionFriendly}
            </Typography.Text>
            <Space wrap>
              <Button onClick={handleOpenServiceMapping} type="primary">
                查看服务映射
              </Button>
              <Button onClick={() => history.push(teamBuilderRoute)}>
                打开 Team Builder
              </Button>
              <Button onClick={handleOpenConversation}>测试对话</Button>
            </Space>
          </div>
        </AevatarPanel>
        <AevatarPanel title="当前配置记录">
          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
            }}
          >
            <SignalCard
              label="发布状态"
              value={currentDeploymentFriendly}
              caption={currentDeploymentStatus}
            />
            <SignalCard
              label="当前版本"
              value={currentVersionFriendly}
              caption={currentRevisionId}
            />
            <SignalCard
              label="服务能力"
              value={`${currentEndpointCount} 个入口`}
              caption={`${currentPolicyCount} 条策略`}
            />
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <div
              style={{
                alignItems: "start",
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(92px, 120px) minmax(0, 1fr)",
              }}
            >
              <Typography.Text type="secondary">workflowId</Typography.Text>
              <FactLine rows={1} secondary text={activeWorkflowId || "--"} />
            </div>
            <div
              style={{
                alignItems: "start",
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(92px, 120px) minmax(0, 1fr)",
              }}
            >
              <Typography.Text type="secondary">serviceKey</Typography.Text>
              <FactLine rows={1} secondary text={currentServiceKey} />
            </div>
            <div
              style={{
                alignItems: "start",
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(92px, 120px) minmax(0, 1fr)",
              }}
            >
              <Typography.Text type="secondary">deploymentId</Typography.Text>
              <FactLine rows={1} secondary text={currentDeploymentId} />
            </div>
          </div>
        </AevatarPanel>
      </div>
    );
  };

  let tabContent: React.ReactNode = renderOverviewTab();
  switch (activeTab) {
    case "topology":
      tabContent = renderTopologyTab();
      break;
    case "events":
      tabContent = renderEventsTab();
      break;
    case "members":
      tabContent = renderMembersTab();
      break;
    case "connectors":
      tabContent = renderConnectorsTab();
      break;
    case "advanced":
      tabContent = renderAdvancedTab();
      break;
    default:
      tabContent = renderOverviewTab();
      break;
  }

  if (!scopeId) {
    return (
      <AevatarPageShell
        title="团队详情"
        content="请先进入一个具体团队，再查看详情。"
      >
        <AevatarPanel title="未选择团队">
          <AevatarInspectorEmpty description="当前需要一个明确的 scope 才能渲染团队详情。" />
        </AevatarPanel>
      </AevatarPageShell>
    );
  }

  return (
    <AevatarPageShell
      title={
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <Typography.Text
            style={{
              color: token.colorTextSecondary,
              fontSize: 14,
            }}
          >
            Aevatar / Teams
          </Typography.Text>
          <Space align="center" wrap size={12}>
            <Typography.Title level={1} style={{ margin: 0 }}>
              {teamTitle}
            </Typography.Title>
            <DetailPill
              style={resolveStatusPillStyle(token, currentHeaderStatus)}
              text={currentHeaderStatusFriendly}
            />
          </Space>
          <Typography.Text
            style={{
              color: token.colorTextSecondary,
              fontSize: 14,
            }}
          >
            团队详情 / {formatTeamTabLabel(activeTab)}
          </Typography.Text>
        </div>
      }
      extra={
        <Space key="team-detail-actions" wrap>
          <Button
            onClick={handleOpenServiceMapping}
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
            type="primary"
          >
            查看服务映射
          </Button>
          <Button
            onClick={handleOpenConversation}
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
          >
            测试对话
          </Button>
          <Button
            onClick={() => history.push(teamBuilderRoute)}
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
          >
            高级编辑
          </Button>
        </Space>
      }
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div
          role="tablist"
          aria-label="团队详情标签"
          style={{
            alignItems: "center",
            background: token.colorBgContainer,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 20,
            boxShadow: token.boxShadowSecondary,
            display: "flex",
            flexWrap: "wrap",
            gap: 10,
            padding: 8,
          }}
        >
          {tabOptions.map((option) => {
            const active = option.value === activeTab;
            return (
              <button
                aria-current={active ? "page" : undefined}
                key={option.value}
                onClick={() => pushTeamTab(option.value)}
                style={{
                  background: active ? token.colorPrimary : "transparent",
                  border: `1px solid ${
                    active ? token.colorPrimary : "transparent"
                  }`,
                  borderRadius: 999,
                  color: active ? token.colorWhite : token.colorTextSecondary,
                  cursor: "pointer",
                  fontSize: 14,
                  fontWeight: active ? 700 : 500,
                  padding: "10px 16px",
                  transition: "all 160ms ease",
                }}
                type="button"
              >
                {option.label}
              </button>
            );
          })}
        </div>
        {tabContent}
        {initialLoading ? (
          <Typography.Text type="secondary">正在加载团队详情...</Typography.Text>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamDetailPage;
