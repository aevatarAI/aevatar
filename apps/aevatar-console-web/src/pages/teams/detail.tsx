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
      return "成员";
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

type TeamEventLogTone = "error" | "msg" | "reply" | "route" | "sched";

type TeamEventLogRow = {
  detail: string;
  flow: string;
  key: string;
  time: string;
  tone: TeamEventLogTone;
  type: string;
};

type TeamTopologyNodeLayout = {
  actorId: string;
  actorType: string;
  caption: string;
  external: boolean;
  isFocused: boolean;
  relationCount: number;
  x: number;
  y: number;
};

function formatPreviewTime(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized) {
    return "--:--:--";
  }

  const parsed = new Date(normalized);
  if (Number.isNaN(parsed.getTime())) {
    return "--:--:--";
  }

  return parsed.toLocaleTimeString("zh-CN", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

function formatEventLogType(stepType: string): TeamEventLogTone {
  const normalized = normalizeStatus(stepType);
  if (normalized.includes("error") || normalized.includes("failed")) {
    return "error";
  }
  if (normalized.includes("reply") || normalized.includes("completed")) {
    return "reply";
  }
  if (
    normalized.includes("human") ||
    normalized.includes("schedule") ||
    normalized.includes("signal")
  ) {
    return "sched";
  }
  if (normalized.includes("llm") || normalized.includes("route")) {
    return "route";
  }
  return "msg";
}

function formatEventLogTypeLabel(stepType: string): string {
  const normalized = normalizeStatus(stepType);
  if (normalized.includes("error") || normalized.includes("failed")) {
    return "ERROR";
  }
  if (normalized.includes("reply") || normalized.includes("completed")) {
    return "REPLY";
  }
  if (normalized.includes("human") || normalized.includes("signal")) {
    return "SCHED";
  }
  if (normalized.includes("llm")) {
    return "LLM";
  }
  if (normalized.includes("route")) {
    return "ROUTED";
  }
  return "MSG_IN";
}

function buildTopologyNodeLayouts(
  nodes: readonly {
    actorId: string;
    actorType: string;
    caption: string;
    isFocused: boolean;
    relationCount: number;
  }[],
): TeamTopologyNodeLayout[] {
  if (nodes.length === 0) {
    return [];
  }

  if (nodes.length === 1) {
    const node = nodes[0];
    return [
      {
        ...node,
        external: false,
        x: 380,
        y: 112,
      },
    ];
  }

  return nodes.map((node, index) => {
    const lane = index % 2;
    const column = Math.floor(index / 2);
    const external = /(connector|telegram|http|mcp|llm)/i.test(
      `${node.actorType} ${node.caption}`,
    );

    return {
      ...node,
      external,
      x: 84 + column * 220,
      y: lane === 0 ? 42 : 176,
    };
  });
}

function buildTeamEventLogRows(
  playback: TeamPlaybackSummary,
): TeamEventLogRow[] {
  const eventRows = playback.events.map((event) => {
    const type = formatEventLogTypeLabel(event.stage);
    return {
      detail: event.detail || event.message,
      flow: compactId(event.actorId) || event.stage || "team",
      key: `event-${event.key}`,
      time: formatPreviewTime(event.timestamp),
      tone: formatEventLogType(event.stage),
      type,
    };
  });

  const stepRows = playback.steps.map((step) => ({
    detail: step.detail || step.summary,
    flow: step.owner || compactId(step.actorId) || "team",
    key: `step-${step.key}`,
    time: formatPreviewTime(step.timestamp),
    tone: formatEventLogType(step.stepType || step.status),
    type: formatEventLogTypeLabel(step.stepType || step.status),
  }));

  return [...eventRows, ...stepRows]
    .sort((left, right) => right.time.localeCompare(left.time))
    .slice(0, 24);
}

const TeamDetailPage: React.FC = () => {
  const routeState = React.useMemo(() => readTeamDetailRouteState(), []);
  const scopeId = routeState.scopeId.trim();
  const [preferredServiceId, setPreferredServiceId] = React.useState(
    routeState.serviceId,
  );
  const [activeTab, setActiveTab] = React.useState<TeamDetailTab>(routeState.tab);
  const [memberSearch, setMemberSearch] = React.useState("");
  const [memberStatusFilter, setMemberStatusFilter] = React.useState("all");
  const [memberTypeFilter, setMemberTypeFilter] = React.useState("all");
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
      const runId =
        lens.playback.currentRunId?.trim() ||
        lens.currentRun?.runId?.trim() ||
        "";
      if (!scopeId || !runId) {
        return;
      }

      const actorId =
        preferredActorId?.trim() ||
        lens.playback.rootActorId?.trim() ||
        lens.currentRun?.actorId?.trim() ||
        "";
      const commandId = lens.playback.commandId?.trim() || "";
      const routeName =
        lens.playback.workflowName?.trim() ||
        lens.currentRun?.workflowName?.trim() ||
        undefined;
      const observedDraftKey = saveObservedRunSessionPayload({
        actorId: actorId || undefined,
        commandId: commandId || undefined,
        endpointId: "chat",
        endpointKind: "chat",
        events: createObservedPlaybackEvents({
          commandId: commandId || null,
          currentRunId: runId,
          rootActorId: actorId || null,
        }),
        prompt:
          lens.playback.launchPrompt ||
          lens.playback.prompt ||
          lens.playback.summary ||
          lens.currentRun?.lastOutput ||
          "",
        routeName,
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
          route: routeName,
          scopeId,
          serviceId: runtimeServiceId,
        }),
      );
    },
    [
      lens.currentRun?.actorId,
      lens.currentRun?.lastOutput,
      lens.currentRun?.runId,
      lens.currentRun?.workflowName,
      lens.playback,
      runtimeServiceId,
      scopeId,
    ],
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
  const detailStripItems = [
    {
      label: "成员",
      value: `${Math.max(lens.members.length, compositionDisplayRows.length)} agents`,
    },
    {
      label: "类型",
      value: `${lens.workflowCount} workflow, ${lens.scriptCount} scripting`,
    },
    {
      label: "连接器",
      value:
        connectorHighlights.length > 0
          ? connectorHighlights.join("、")
          : integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个已绑定`
            : "未配置",
    },
    {
      label: "事件 (24h)",
      value: String(lens.playback.events.length + lens.playback.steps.length),
    },
    {
      label: "当前状态",
      value: currentHeaderStatusFriendly,
    },
  ];
  const eventLogRows = React.useMemo(
    () => buildTeamEventLogRows(lens.playback),
    [lens.playback],
  );
  const topologyNodeLayouts = React.useMemo(
    () => buildTopologyNodeLayouts(selectedGraphNodes),
    [selectedGraphNodes],
  );
  const topologyLayoutByActorId = React.useMemo(
    () =>
      Object.fromEntries(
        topologyNodeLayouts.map((node) => [node.actorId, node]),
      ) as Record<string, TeamTopologyNodeLayout>,
    [topologyNodeLayouts],
  );
  const memberRows = React.useMemo(() => {
    return lens.members.map((member, index) => {
      const roleRow = compositionDisplayRows[index];
      const actorEventCount =
        lens.playback.steps.filter((step) => step.actorId === member.actorId).length +
        lens.playback.events.filter((event) => event.actorId === member.actorId).length;
      const implementationType = formatCompositionKind(
        roleRow?.kind || member.actorType || "actor",
      );
      const runtimeStatus =
        member.actorId === effectiveActorId
          ? currentHeaderStatusFriendly
          : lens.healthStatus === "healthy"
            ? "运行中"
            : "已观察";

      return {
        actionsLabel: member.actorId,
        actorId: member.actorId,
        governanceService: runtimeServiceId || "--",
        implementationType,
        key: member.actorId,
        messageCount: actorEventCount,
        name: member.actorType || compactId(member.actorId),
        role:
          roleRow?.summary ||
          topologyLayoutByActorId[member.actorId]?.caption ||
          "当前还没有更细的角色说明。",
        status: runtimeStatus,
        uptime:
          lens.healthStatus === "healthy"
            ? "99.9%"
            : lens.healthStatus === "blocked"
              ? "等待处理"
              : "可见",
      };
    });
  }, [
    compositionDisplayRows,
    currentHeaderStatusFriendly,
    effectiveActorId,
    lens.healthStatus,
    lens.members,
    lens.playback.events,
    lens.playback.steps,
    runtimeServiceId,
    topologyLayoutByActorId,
  ]);
  const filteredMemberRows = React.useMemo(() => {
    return memberRows.filter((row) => {
      const searchTarget = `${row.name} ${row.actorId} ${row.role}`.toLowerCase();
      const searchPassed =
        memberSearch.trim().length === 0 ||
        searchTarget.includes(memberSearch.trim().toLowerCase());
      const typePassed =
        memberTypeFilter === "all" ||
        normalizeStatus(row.implementationType) === normalizeStatus(memberTypeFilter);
      const statusPassed =
        memberStatusFilter === "all" ||
        normalizeStatus(row.status) === normalizeStatus(memberStatusFilter);

      return searchPassed && typePassed && statusPassed;
    });
  }, [memberRows, memberSearch, memberStatusFilter, memberTypeFilter]);

  const tabOptions: TeamTabOption[] = [
    { label: "概览", value: "overview" },
    { label: "事件拓扑", value: "topology" },
    { label: "事件流", value: "events" },
    { label: "成员", value: "members" },
    { label: "连接器", value: "connectors" },
    { label: "高级编辑", value: "advanced" },
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
    if (lens.playback.currentRunId || lens.currentRun?.runId) {
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
      lens.currentRun?.runId,
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
    const summaryCards = [
      {
        caption: currentServiceKey,
        label: "主服务入口",
        value: currentServiceFriendly,
      },
      {
        caption: currentRevisionId,
        label: "当前版本",
        value: currentRevisionFriendly,
      },
      {
        caption: activeRunId || currentActorId,
        label: "最近状态",
        value: currentRunFriendly,
      },
      {
        caption: formatDetailedTimestamp(latestVisibleUpdate),
        label: "最近更新时间",
        value: formatCompactTimestamp(latestVisibleUpdate),
      },
    ];

    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div
          style={{
            background: "#ffffff",
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 8,
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: 20,
          }}
        >
          <div
          style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 12,
              justifyContent: "space-between",
            }}
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
              <Space wrap size={8}>
                <Typography.Text strong style={{ fontSize: 16 }}>
                  团队状态
                </Typography.Text>
                <DetailPill
                  style={resolveStatusPillStyle(token, currentHeaderStatus)}
                  text={currentHeaderStatusFriendly}
                />
              </Space>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                当前团队的主服务、发布版本和最近一次可见运行信号。
              </Typography.Text>
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
              gap: 12,
              gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
            }}
          >
            {summaryCards.map((card) => (
              <div
                key={card.label}
                style={{
                  background: "#fafafa",
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 8,
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                  minHeight: 116,
                  padding: 16,
                }}
              >
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {card.label}
                </Typography.Text>
                <Typography.Title level={3} style={{ margin: 0 }}>
                  {card.value}
                </Typography.Title>
                <Tooltip placement="topLeft" title={card.caption}>
                  <Typography.Text
                    ellipsis
                    style={{
                      display: "block",
                      fontFamily: factValueFontFamily,
                      fontSize: 12,
                    }}
                    type="secondary"
                  >
                    {card.caption}
                  </Typography.Text>
                </Tooltip>
              </div>
            ))}
          </div>
        </div>
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))",
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
              background: "#ffffff",
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 8,
              display: "flex",
              flexDirection: "column",
              gap: 16,
              padding: 20,
            }}
          >
            <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
              <div>
                <Typography.Title level={4} style={{ margin: 0 }}>
                  团队构成
                </Typography.Title>
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  当前可见的流程角色、脚本成员和主服务映射。
                </Typography.Text>
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
                    paddingTop: index === 0 ? 0 : 14,
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
              background: "#ffffff",
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 8,
              display: "flex",
              flexDirection: "column",
              gap: 16,
              padding: 20,
            }}
          >
            <div>
              <Typography.Title level={4} style={{ margin: 0 }}>
                运行摘要
              </Typography.Title>
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                汇总当前团队的发布版本、可见运行信号和连接器状态。
              </Typography.Text>
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
                  paddingTop: index === 0 ? 0 : 14,
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
      <div
        style={{
          background: "#ffffff",
          border: "1px solid #e8e8e8",
          borderRadius: 4,
          overflow: "hidden",
        }}
      >
        <div
          style={{
            alignItems: "center",
            background: "#fafafa",
            borderBottom: "1px solid #f0f0f0",
            display: "flex",
            fontSize: 12,
            fontWeight: 500,
            gap: 12,
            padding: "10px 14px",
          }}
        >
          EventEnvelope 流转拓扑
          <span
            style={{
              color: "#8c8c8c",
              fontSize: 11,
              fontWeight: 400,
              marginLeft: "auto",
            }}
          >
            {graphProvenance.label} · 点击节点查看详情
          </span>
        </div>
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
                background: "#fafafa",
                minHeight: 320,
                padding: 16,
                position: "relative",
              }}
            >
              <svg
                viewBox="0 0 920 300"
                style={{
                  inset: 0,
                  pointerEvents: "none",
                  position: "absolute",
                }}
              >
                {visibleGraphRelationships.map((relationship) => {
                  const from = topologyLayoutByActorId[relationship.fromActorId];
                  const to = topologyLayoutByActorId[relationship.toActorId];
                  if (!from || !to) {
                    return null;
                  }

                  const stroke =
                    relationship.direction === "inbound"
                      ? "#1890ff"
                      : relationship.direction === "peer"
                        ? "#fa8c16"
                        : "#52c41a";
                  const dashArray =
                    relationship.direction === "peer" ? "3 3" : "5 3";

                  return (
                    <line
                      key={relationship.key}
                      x1={from.x + 54}
                      x2={to.x + 54}
                      y1={from.y + 30}
                      y2={to.y + 30}
                      stroke={stroke}
                      strokeDasharray={dashArray}
                      strokeWidth={relationship.direction === "inbound" ? 1 : 1.5}
                    />
                  );
                })}
              </svg>
              {topologyNodeLayouts.length > 0 ? (
                topologyNodeLayouts.map((node) => (
                  <button
                    key={node.actorId}
                    type="button"
                    onClick={() => setSelectedActorId(node.actorId)}
                    style={{
                      background: "#ffffff",
                      border: `1.5px ${node.external ? "dashed" : "solid"} ${
                        node.isFocused ? "#52c41a" : node.external ? "#1890ff" : "#d9d9d9"
                      }`,
                      borderRadius: 6,
                      boxShadow: "0 1px 2px rgba(0,0,0,0.04)",
                      cursor: "pointer",
                      left: node.x,
                      minWidth: 108,
                      padding: "8px 12px",
                      position: "absolute",
                      textAlign: "center",
                      top: node.y,
                    }}
                  >
                    <div
                      style={{
                        color: "#262626",
                        fontSize: 12,
                        fontWeight: 600,
                      }}
                    >
                      {node.actorType || compactId(node.actorId)}
                    </div>
                    <div
                      style={{
                        color: "#8c8c8c",
                        fontSize: 9,
                        marginTop: 1,
                      }}
                    >
                      {compactId(node.actorId)}
                    </div>
                    <div
                      style={{
                        color: node.external ? "#1890ff" : "#52c41a",
                        fontSize: 10,
                        marginTop: 3,
                      }}
                    >
                      {node.relationCount} relations
                    </div>
                  </button>
                ))
              ) : (
                <AevatarInspectorEmpty
                  title="暂无可见关系"
                  description="当前没有更多可见的事件拓扑关系。"
                />
              )}
              <div
                style={{
                  background: "rgba(255,255,255,0.92)",
                  borderRadius: 3,
                  bottom: 10,
                  color: "#8c8c8c",
                  display: "flex",
                  fontSize: 9,
                  gap: 12,
                  left: 10,
                  padding: "3px 8px",
                  position: "absolute",
                }}
              >
                <span style={{ color: "#52c41a" }}>—— Event</span>
                <span style={{ color: "#1890ff" }}>--- Reply</span>
                <span style={{ color: "#fa8c16" }}>··· Peer</span>
              </div>
            </div>
            <div style={{ padding: "0 16px 16px" }}>
              <Alert description={selectedFocusReason || lens.graph.stageSummary} showIcon type="info" />
            </div>
          </div>
        )}
      </div>
    );
  };

  const renderEventsTab = () => {
    return (
      <div
        style={{
          background: "#ffffff",
          border: "1px solid #e8e8e8",
          borderRadius: 4,
          overflow: "hidden",
        }}
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
                alignItems: "center",
                background: "#fafafa",
                borderBottom: "1px solid #f0f0f0",
                display: "flex",
                flexWrap: "wrap",
                fontSize: 12,
                fontWeight: 500,
                gap: 8,
                padding: "8px 12px",
              }}
            >
              EventEnvelope Stream
              <div
                style={{
                  display: "flex",
                  gap: 6,
                  marginLeft: 12,
                }}
              >
                <select
                  aria-label="事件类型过滤"
                  style={{
                    border: "1px solid #d9d9d9",
                    borderRadius: 3,
                    fontSize: 10,
                    height: 22,
                    padding: "0 4px",
                  }}
                >
                  <option>Type: All</option>
                </select>
                <select
                  aria-label="成员过滤"
                  style={{
                    border: "1px solid #d9d9d9",
                    borderRadius: 3,
                    fontSize: 10,
                    height: 22,
                    padding: "0 4px",
                  }}
                >
                  <option>Agent: All</option>
                </select>
                <input
                  aria-label="事件关键词过滤"
                  placeholder="Filter..."
                  style={{
                    border: "1px solid #d9d9d9",
                    borderRadius: 3,
                    fontSize: 10,
                    height: 22,
                    padding: "0 6px",
                    width: 140,
                  }}
                />
              </div>
              <span
                style={{
                  color: "#8c8c8c",
                  fontSize: 11,
                  fontWeight: 400,
                  marginLeft: "auto",
                }}
              >
                {playbackProvenance.label} · {eventLogRows.length} 条可见事件
              </span>
            </div>
            <div
              style={{
                background: "#ffffff",
                fontFamily: '"SF Mono", "JetBrains Mono", monospace',
                fontSize: 11,
              }}
            >
              {eventLogRows.length > 0 ? (
                eventLogRows.map((row) => (
                  <div
                    key={row.key}
                    style={{
                      background: row.tone === "error" ? "#fff2f0" : "#ffffff",
                      borderBottom: "1px solid #fafafa",
                      display: "flex",
                      gap: 8,
                      lineHeight: 1.6,
                      padding: "4px 12px",
                    }}
                  >
                    <span style={{ color: "#bfbfbf", minWidth: 60 }}>
                      {row.time}
                    </span>
                    <span
                      style={{
                        color:
                          row.tone === "error"
                            ? "#ff4d4f"
                            : row.tone === "reply"
                              ? "#1890ff"
                              : row.tone === "sched"
                                ? "#faad14"
                                : row.tone === "route"
                                  ? "#52c41a"
                                  : "#722ed1",
                        fontWeight: 600,
                        minWidth: 52,
                      }}
                    >
                      {row.type}
                    </span>
                    <span style={{ color: "#8c8c8c", minWidth: 120 }}>
                      {row.flow}
                    </span>
                    <span style={{ color: "#434343", flex: 1 }}>{row.detail}</span>
                  </div>
                ))
              ) : visiblePlaybackSteps.length > 0 ? (
                visiblePlaybackSteps.map((step) => (
                  <div
                    key={step.key}
                    style={{
                      borderBottom: "1px solid #fafafa",
                      display: "flex",
                      gap: 8,
                      lineHeight: 1.6,
                      padding: "4px 12px",
                    }}
                  >
                    <span style={{ color: "#bfbfbf", minWidth: 60 }}>
                      {formatPreviewTime(step.timestamp)}
                    </span>
                    <span style={{ color: "#52c41a", fontWeight: 600, minWidth: 52 }}>
                      {formatEventLogTypeLabel(step.stepType)}
                    </span>
                    <span style={{ color: "#8c8c8c", minWidth: 120 }}>
                      {step.owner || compactId(step.actorId)}
                    </span>
                    <span style={{ color: "#434343", flex: 1 }}>{step.detail}</span>
                  </div>
                ))
              ) : (
                <AevatarInspectorEmpty
                  title="当前还没有更多可见的步骤事实。"
                  description={lens.playback.summary}
                />
              )}
            </div>
            <div style={{ padding: "0 12px 12px" }}>
              <Space wrap>
                <Button onClick={handleOpenConversation}>测试对话</Button>
                <Button onClick={handleOpenServiceMapping} type="link">
                  查看服务映射
                </Button>
              </Space>
            </div>
          </div>
        )}
      </div>
    );
  };

  const renderMembersTab = () => {
    return (
      <div
        style={{
          background: "#ffffff",
          border: "1px solid #e8e8e8",
          borderRadius: 4,
          overflow: "hidden",
        }}
      >
        <div
          style={{
            alignItems: "center",
            background: "#ffffff",
            borderBottom: "1px solid #e8e8e8",
            display: "flex",
            gap: 8,
            padding: "8px 14px",
          }}
        >
          <input
            aria-label="搜索成员"
            placeholder="搜索成员或 Actor ID..."
            value={memberSearch}
            onChange={(event) => setMemberSearch(event.target.value)}
            style={{
              border: "1px solid #d9d9d9",
              borderRadius: 3,
              fontSize: 11,
              height: 26,
              padding: "0 8px",
              width: 220,
            }}
          />
          <select
            aria-label="成员类型过滤"
            value={memberTypeFilter}
            onChange={(event) => setMemberTypeFilter(event.target.value)}
            style={{
              border: "1px solid #d9d9d9",
              borderRadius: 3,
              fontSize: 11,
              height: 26,
              padding: "0 6px",
            }}
          >
            <option value="all">类型: All</option>
            {Array.from(
              new Set(memberRows.map((row) => normalizeStatus(row.implementationType))),
            )
              .filter(Boolean)
              .map((type) => (
                <option key={type} value={type}>
                  {formatCompositionKind(type)}
                </option>
              ))}
          </select>
          <select
            aria-label="成员状态过滤"
            value={memberStatusFilter}
            onChange={(event) => setMemberStatusFilter(event.target.value)}
            style={{
              border: "1px solid #d9d9d9",
              borderRadius: 3,
              fontSize: 11,
              height: 26,
              padding: "0 6px",
            }}
          >
            <option value="all">状态: All</option>
            <option value="运行中">运行中</option>
            <option value="已观察">已观察</option>
            <option value="等待处理">等待处理</option>
          </select>
        </div>
        <div style={{ overflowX: "auto" }}>
          <table
            style={{
              background: "#ffffff",
              borderCollapse: "collapse",
              width: "100%",
            }}
          >
            <thead>
              <tr>
                {[
                  "Status",
                  "Name",
                  "Role",
                  "Type",
                  "Msgs (24h)",
                  "Uptime",
                  "Actor ID",
                  "Governance Service",
                  "Actions",
                ].map((header) => (
                  <th
                    key={header}
                    style={{
                      background: "#fafafa",
                      borderBottom: "1px solid #f0f0f0",
                      color: "#8c8c8c",
                      fontSize: 11,
                      fontWeight: 500,
                      letterSpacing: 0.3,
                      padding: "9px 14px",
                      textAlign: "left",
                      textTransform: "uppercase",
                      whiteSpace: "nowrap",
                    }}
                  >
                    {header}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {filteredMemberRows.length > 0 ? (
                filteredMemberRows.map((row) => (
                  <tr
                    key={row.key}
                    style={{
                      background:
                        row.actorId === effectiveActorId ? "#fafcff" : "#ffffff",
                    }}
                  >
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      <span
                        style={{
                          background:
                            row.status === "运行中"
                              ? "#52c41a"
                              : row.status === "等待处理"
                                ? "#faad14"
                                : "#1890ff",
                          borderRadius: "50%",
                          display: "inline-block",
                          height: 6,
                          marginRight: 8,
                          width: 6,
                        }}
                      />
                      {row.status}
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      <strong>{row.name}</strong>
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      {row.role}
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      <span
                        style={{
                          background: "#f5f5f5",
                          border: "1px solid #e8e8e8",
                          borderRadius: 3,
                          color: "#595959",
                          fontSize: 10,
                          padding: "1px 6px",
                        }}
                      >
                        {row.implementationType}
                      </span>
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      {row.messageCount}
                    </td>
                    <td
                      style={{
                        borderBottom: "1px solid #f5f5f5",
                        color: row.uptime === "99.9%" ? "#52c41a" : "#595959",
                        padding: "10px 14px",
                      }}
                    >
                      {row.uptime}
                    </td>
                    <td
                      style={{
                        borderBottom: "1px solid #f5f5f5",
                        color: "#8c8c8c",
                        fontFamily: factValueFontFamily,
                        fontSize: 11,
                        padding: "10px 14px",
                      }}
                    >
                      {compactId(row.actorId)}
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      <button
                        type="button"
                        onClick={() => history.push(buildRuntimeExplorerHref({
                          actorId: row.actorId,
                        }))}
                        style={{
                          background: "transparent",
                          border: "none",
                          color: "#1890ff",
                          cursor: "pointer",
                          fontFamily: factValueFontFamily,
                          fontSize: 11,
                          padding: 0,
                        }}
                      >
                        {row.governanceService}
                      </button>
                    </td>
                    <td style={{ borderBottom: "1px solid #f5f5f5", padding: "10px 14px" }}>
                      <Space size={4}>
                        <Button
                          size="small"
                          type="link"
                          onClick={() => history.push(teamBuilderRoute)}
                        >
                          Edit
                        </Button>
                        <Button
                          size="small"
                          type="link"
                          onClick={() => pushTeamTab("events")}
                        >
                          Logs
                        </Button>
                        <Button
                          size="small"
                          type="link"
                          onClick={() => {
                            setSelectedActorId(row.actorId);
                            pushTeamTab("topology");
                          }}
                        >
                          Graph
                        </Button>
                      </Space>
                    </td>
                  </tr>
                ))
              ) : (
                <tr>
                  <td
                    colSpan={9}
                    style={{
                      padding: 24,
                    }}
                  >
                    <AevatarInspectorEmpty
                      title="暂时还没有可见参与者"
                      description="当前过滤条件下没有成员记录。"
                    />
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        <div
          style={{
            background: "#e6f7ff",
            borderTop: "1px solid #91d5ff",
            color: "#1890ff",
            fontSize: 11,
            padding: "8px 12px",
          }}
        >
          Governance Service ID 可跳转到 Platform → Topology，Actor ID 当前指向焦点成员。
        </div>
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
    const studioContextRows = [
      {
        label: "团队",
        value: teamTitle,
      },
      {
        label: "当前流程",
        value: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
      },
      {
        label: "主服务",
        value: currentServiceFriendly,
      },
      {
        label: "当前版本",
        value: currentVersionFriendly,
      },
    ];

    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div
          style={{
            background: "#ffffff",
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 8,
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: 20,
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <Typography.Text strong style={{ fontSize: 16 }}>
              团队构建器
            </Typography.Text>
            <Typography.Text type="secondary">
              从这里进入 Studio，在当前团队上下文下继续编辑行为定义、脚本行为、Agent 角色、集成和测试运行。
            </Typography.Text>
            <Typography.Text type="secondary">
              当前入口会自动带上 scopeId，如果已经锁定了 workflow，也会直接打开对应成员的编辑视图。
            </Typography.Text>
          </div>
          <Space wrap>
            <Button type="primary" onClick={() => history.push(teamBuilderRoute)}>
              打开团队构建器
            </Button>
            <Button onClick={handleOpenConversation}>测试对话</Button>
            <Button onClick={handleOpenServiceMapping}>查看服务映射</Button>
          </Space>
        </div>
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))",
          }}
        >
          {studioContextRows.map((item) => (
            <div
              key={item.label}
              style={{
                background: "#ffffff",
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 8,
                display: "flex",
                flexDirection: "column",
                gap: 8,
                minHeight: 108,
                padding: 16,
              }}
            >
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {item.label}
              </Typography.Text>
              <Typography.Title level={3} style={{ margin: 0 }}>
                {item.value}
              </Typography.Title>
            </div>
          ))}
        </div>
        <div
          style={{
            background: "#ffffff",
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 8,
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: 20,
          }}
        >
          <Typography.Text strong style={{ fontSize: 16 }}>
            当前编辑上下文
          </Typography.Text>
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
        </div>
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
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <div
            style={{
              color: "#00000073",
              fontSize: 12,
            }}
          >
            Teams / <b style={{ color: "#1d2129" }}>{teamTitle}</b>
            {activeTab !== "overview" ? (
              <span> / <b style={{ color: "#1d2129" }}>{formatTeamTabLabel(activeTab)}</b></span>
            ) : null}
          </div>
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 8,
            }}
          >
            <div
              style={{
                color: "#1d2129",
                fontSize: 18,
                fontWeight: 600,
                lineHeight: 1.4,
              }}
            >
              {teamTitle}
            </div>
            <span
              style={{
                background:
                  currentHeaderStatusFriendly === "运行中"
                    ? "#f6ffed"
                    : currentHeaderStatusFriendly === "等待处理"
                      ? "#fffbe6"
                      : "#f5f5f5",
                border: `1px solid ${
                  currentHeaderStatusFriendly === "运行中"
                    ? "#b7eb8f"
                    : currentHeaderStatusFriendly === "等待处理"
                      ? "#ffe58f"
                      : "#e8e8e8"
                }`,
                borderRadius: 4,
                color:
                  currentHeaderStatusFriendly === "运行中"
                    ? "#52c41a"
                    : currentHeaderStatusFriendly === "等待处理"
                      ? "#faad14"
                      : "#595959",
                display: "inline-flex",
                fontSize: 10,
                fontWeight: 500,
                padding: "2px 8px",
              }}
            >
              {currentHeaderStatusFriendly}
            </span>
          </div>
        </div>
      }
      extra={
        <Space key="team-detail-actions" wrap>
          <Button
            onClick={handleOpenConversation}
            style={{ borderRadius: 6, height: 30, paddingInline: 14 }}
          >
            测试对话
          </Button>
          <Button
            onClick={() => history.push(teamBuilderRoute)}
            style={{ borderRadius: 6, height: 30, paddingInline: 14 }}
          >
            编辑
          </Button>
          <Button
            danger
            onClick={handleOpenServiceMapping}
            style={{ borderRadius: 6, height: 30, paddingInline: 14 }}
          >
            查看服务映射
          </Button>
        </Space>
      }
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 0 }}>
        <div
          style={{
            background: "#fafafa",
            borderBottom: "1px solid #f0f0f0",
            display: "flex",
            flexWrap: "wrap",
            fontSize: 12,
            gap: 24,
            padding: "10px 24px",
          }}
        >
          {detailStripItems.map((item) => (
            <div key={item.label}>
              <span style={{ color: "#8c8c8c" }}>{item.label}</span>
              <span style={{ color: "#262626", fontWeight: 500, marginLeft: 4 }}>
                {item.value}
              </span>
            </div>
          ))}
        </div>
        <div
          role="tablist"
          aria-label="团队详情标签"
          style={{
            background: "#ffffff",
            borderBottom: "1px solid #f0f0f0",
            display: "flex",
            padding: "0 24px",
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
                  background: "transparent",
                  border: "none",
                  borderBottom: `2px solid ${active ? "#1890ff" : "transparent"}`,
                  color: active ? "#1890ff" : "#595959",
                  cursor: "pointer",
                  fontSize: 13,
                  fontWeight: active ? 500 : 400,
                  marginRight: 28,
                  padding: "12px 0",
                }}
                type="button"
              >
                {option.label}
              </button>
            );
          })}
        </div>
        <div
          style={{
            background: activeTab === "overview" ? "#ffffff" : "#f0f2f5",
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: "16px 24px",
          }}
        >
          {tabContent}
        </div>
        {initialLoading ? (
          <Typography.Text style={{ padding: "0 24px 16px" }} type="secondary">
            正在加载团队详情...
          </Typography.Text>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamDetailPage;
