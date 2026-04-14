import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from "@aevatar-react-sdk/types";
import {
  BranchesOutlined,
  DeploymentUnitOutlined,
} from "@ant-design/icons";
import type { Edge, Node } from "@xyflow/react";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import { useQuery } from "@tanstack/react-query";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import { buildActorGraphElements } from "@/shared/graphs/buildGraphElements";
import { history } from "@/shared/navigation/history";
import { buildScopeHref } from "@/shared/navigation/scopeRoutes";
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

type TopologyNodeKind = "actor" | "connector" | "service";

type TopologyEntitySummary = {
  badgeText: string;
  badgeTone: PillTone;
  id: string;
  kind: TopologyNodeKind;
  note: string;
  summary: string;
  title: string;
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
      return "配置";
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

type MemberPresenceKind = "focus" | "run" | "visible";

function formatMemberPresenceLabel(kind: MemberPresenceKind): string {
  switch (kind) {
    case "focus":
      return "当前焦点";
    case "run":
      return "参与本次 Run";
    default:
      return "可见 Actor";
  }
}

function resolveMemberPresenceTone(kind: MemberPresenceKind): PillTone {
  switch (kind) {
    case "focus":
      return "info";
    case "run":
      return "success";
    default:
      return "neutral";
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

function formatTopologyNodeKindLabel(kind: TopologyNodeKind): string {
  switch (kind) {
    case "service":
      return "服务节点";
    case "connector":
      return "连接器";
    default:
      return "团队成员";
  }
}

function formatTopologyDepthLabel(depth: number): string {
  switch (depth) {
    case 1:
      return "近邻";
    case 3:
      return "全景";
    default:
      return "扩展";
  }
}

function summarizeTopologyTitles(
  titles: readonly string[],
  emptyLabel: string,
): string {
  const visible = [...new Set(titles.map((title) => trimText(title)).filter(Boolean))];
  if (visible.length === 0) {
    return emptyLabel;
  }
  if (visible.length <= 3) {
    return visible.join("、");
  }
  return `${visible.slice(0, 3).join("、")} 等 ${visible.length} 个`;
}

function formatTopologyFocusReason(reason: string): string {
  const normalized = trimText(reason);
  switch (normalized) {
    case "Focused on the actor behind the most recent team activity.":
      return "当前焦点跟随最近一次团队运行里的实际执行成员。";
    case "Focused on the currently serving revision actor because no active run was selected.":
      return "当前还没有选中运行，所以先对齐到正在 serving 的版本成员。";
    case "Focused on the team primary actor from the current binding.":
      return "当前还没有更强的运行信号，所以先对齐到团队主 Actor。";
    case "Focused on the first known team member because no stronger runtime signal was available.":
      return "当前运行信号不足，先落在当前可见的第一位团队成员。";
    case "No actor focus is available yet.":
      return "当前还没有可用的团队成员焦点。";
    default:
      return normalized;
  }
}

function buildDepthMap(
  rootId: string,
  edges: readonly { source: string; target: string }[],
): Map<string, number> {
  const normalizedRootId = trimText(rootId);
  if (!normalizedRootId) {
    return new Map();
  }

  const adjacency = new Map<string, string[]>();
  edges.forEach((edge) => {
    const source = trimText(edge.source);
    const target = trimText(edge.target);
    if (!source || !target) {
      return;
    }

    const sourceNeighbors = adjacency.get(source) ?? [];
    sourceNeighbors.push(target);
    adjacency.set(source, sourceNeighbors);

    const targetNeighbors = adjacency.get(target) ?? [];
    targetNeighbors.push(source);
    adjacency.set(target, targetNeighbors);
  });

  const depths = new Map<string, number>([[normalizedRootId, 0]]);
  const queue = [normalizedRootId];

  while (queue.length > 0) {
    const current = queue.shift()!;
    const nextDepth = (depths.get(current) ?? 0) + 1;
    for (const next of adjacency.get(current) ?? []) {
      if (depths.has(next)) {
        continue;
      }
      depths.set(next, nextDepth);
      queue.push(next);
    }
  }

  return depths;
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

function resolveActionButtonStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  tone: "primary" | "secondary" = "secondary",
): React.CSSProperties {
  return {
    background: tone === "secondary" ? token.colorBgContainer : undefined,
    borderColor: tone === "secondary" ? token.colorBorderSecondary : undefined,
    borderRadius: 16,
    boxShadow: "none",
    color: tone === "secondary" ? token.colorText : undefined,
    fontWeight: 600,
    height: 40,
    paddingInline: 18,
  };
}

function resolveSegmentedButtonStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  active: boolean,
): React.CSSProperties {
  return {
    background: active ? token.colorPrimary : "transparent",
    border: "none",
    borderRadius: 999,
    color: active ? token.colorWhite : token.colorTextSecondary,
    cursor: "pointer",
    fontFamily: factValueFontFamily,
    fontSize: 12,
    fontWeight: 700,
    padding: "8px 12px",
    transition: "all 160ms ease",
  };
}

function resolveSelectionCardButtonStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  selected: boolean,
): React.CSSProperties {
  return {
    background: selected ? token.colorPrimaryBg : token.colorBgContainer,
    border: `1px solid ${selected ? token.colorPrimaryBorder : token.colorBorderSecondary}`,
    borderRadius: 18,
    boxShadow: selected ? token.boxShadowSecondary : "none",
    cursor: "pointer",
    transition: "all 160ms ease",
  };
}

function formatEventStreamStageLabel(
  stage: string | null | undefined,
  stepType?: string | null,
): string {
  const normalizedStage = normalizeStatus(stage);
  const normalizedStepType = normalizeStatus(stepType);

  const asStageCode = (value: string): string =>
    trimText(value)
      .replace(/[^a-zA-Z0-9]+/g, "_")
      .replace(/^_+|_+$/g, "")
      .toUpperCase();

  if (
    normalizedStage.includes("human") ||
    normalizedStage.includes("wait") ||
    normalizedStage.includes("suspend") ||
    normalizedStepType === "human_approval" ||
    normalizedStepType === "human_input"
  ) {
    return "HUMAN_GATE";
  }

  if (normalizedStepType === "llm_call") {
    return "LLM_CALL";
  }

  if (normalizedStepType === "tool_call") {
    return "TOOL_CALL";
  }

  if (normalizedStage.includes("route") || normalizedStage.includes("dispatch")) {
    return "ROUTED";
  }

  if (normalizedStage.includes("reply")) {
    return "REPLY";
  }

  if (normalizedStage.includes("schedule")) {
    return "SCHEDULE";
  }

  if (
    normalizedStage.includes("input") ||
    normalizedStage.includes("ingress") ||
    normalizedStage.includes("receive") ||
    normalizedStage.includes("message")
  ) {
    return "MSG_IN";
  }

  if (normalizedStage === "step") {
    return "STEP";
  }

  if (trimText(stage)) {
    return asStageCode(trimText(stage));
  }

  return stepType ? asStageCode(stepType) : "RUNTIME";
}

function resolveEventStreamTone(
  stage: string | null | undefined,
  tone: string | null | undefined,
  stepType?: string | null,
): PillTone {
  const normalizedTone = normalizeStatus(tone);
  if (normalizedTone === "error") {
    return "danger";
  }
  if (normalizedTone === "warning") {
    return "warning";
  }

  const normalizedStage = normalizeStatus(stage);
  const normalizedStepType = normalizeStatus(stepType);
  if (
    normalizedStage.includes("human") ||
    normalizedStage.includes("wait") ||
    normalizedStage.includes("suspend") ||
    normalizedStepType === "human_approval" ||
    normalizedStepType === "human_input"
  ) {
    return "warning";
  }
  if (normalizedStage.includes("reply")) {
    return "success";
  }
  if (
    normalizedStage.includes("route") ||
    normalizedStage.includes("dispatch") ||
    normalizedStepType === "llm_call" ||
    normalizedStepType === "tool_call"
  ) {
    return "info";
  }
  return "neutral";
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

function formatClockTimestamp(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized) {
    return "--";
  }

  const parsed = new Date(normalized);
  if (Number.isNaN(parsed.getTime())) {
    return "--";
  }

  return parsed.toLocaleTimeString("zh-CN", {
    hour12: false,
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
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

const TopologyNodeCard: React.FC<{
  entity: TopologyEntitySummary;
}> = ({ entity }) => {
  const { token } = theme.useToken();
  const kindStyle =
    entity.kind === "service"
      ? resolveTonePillStyle(token, "info")
      : entity.kind === "connector"
        ? resolveTonePillStyle(token, "warning")
        : resolveTonePillStyle(token, "neutral");
  const kindLabel =
    entity.kind === "service"
      ? "服务节点"
      : entity.kind === "connector"
        ? "连接器"
        : "团队成员";

  return (
    <div
      style={{
        background: token.colorBgContainer,
        borderRadius: 20,
        display: "flex",
        flexDirection: "column",
        gap: 12,
        minHeight: 132,
        minWidth: 0,
        padding: 18,
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          gap: 10,
          justifyContent: "space-between",
        }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 8, minWidth: 0 }}>
          <div>
            <DetailPill compact style={kindStyle} text={kindLabel} />
          </div>
          <Typography.Text
            strong
            style={{
              color: token.colorText,
              display: "block",
              fontSize: 17,
              lineHeight: 1.2,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
            }}
          >
            {entity.title}
          </Typography.Text>
          <Typography.Text
            style={{
              color: token.colorTextTertiary,
              display: "block",
              fontSize: 12,
              lineHeight: 1.45,
              overflow: "hidden",
              textOverflow: "ellipsis",
              whiteSpace: "nowrap",
            }}
          >
            {entity.summary}
          </Typography.Text>
        </div>
        <DetailPill
          compact
          style={resolveTonePillStyle(token, entity.badgeTone)}
          text={entity.badgeText}
        />
      </div>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          display: "block",
          fontFamily: factValueFontFamily,
          fontSize: 12,
          lineHeight: 1.5,
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
        }}
      >
        {entity.note}
      </Typography.Text>
    </div>
  );
};

const TeamDetailPage: React.FC = () => {
  const routeState = React.useMemo(() => readTeamDetailRouteState(), []);
  const scopeId = routeState.scopeId.trim();
  const teamsListHref = React.useMemo(
    () => buildScopeHref("/teams", { scopeId }),
    [scopeId],
  );
  const [graphDepth, setGraphDepth] = React.useState(2);
  const [preferredServiceId, setPreferredServiceId] = React.useState(
    routeState.serviceId,
  );
  const [preferredRunId, setPreferredRunId] = React.useState(routeState.runId);
  const [activeTab, setActiveTab] = React.useState<TeamDetailTab>(routeState.tab);
  const [selectedActorId, setSelectedActorId] = React.useState("");
  const [selectedConnectorKey, setSelectedConnectorKey] = React.useState("");
  const [selectedTopologyNodeId, setSelectedTopologyNodeId] = React.useState("");
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
    graphDepth,
    preferredActorId: selectedActorId || undefined,
    preferredRunId,
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
      preferredRunId,
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
    preferredRunId,
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

  const defaultSelectedConnectorKey =
    integrations.items.find((item) => item.usedByRoles.length > 0)?.key ||
    integrations.items[0]?.key ||
    "";

  React.useEffect(() => {
    if (integrations.items.length === 0) {
      setSelectedConnectorKey("");
      return;
    }

    if (
      !selectedConnectorKey ||
      !integrations.items.some((item) => item.key === selectedConnectorKey)
    ) {
      setSelectedConnectorKey(defaultSelectedConnectorKey);
    }
  }, [defaultSelectedConnectorKey, integrations.items, selectedConnectorKey]);

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
  const localizedFocusReason = formatTopologyFocusReason(lens.graph.focusReason);
  const selectedFocusReason =
    effectiveActorId && effectiveActorId !== lens.graph.focusActorId
      ? `当前视角已锁定在 ${compactId(effectiveActorId)}。${localizedFocusReason}`
      : localizedFocusReason;

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
  const latestVisibleUpdateNote = lens.currentRun?.lastUpdatedAt
    ? trimText(lens.currentRun?.runId)
      ? `来自 run ${compactId(lens.currentRun?.runId)}`
      : "来自最近可见运行"
    : lens.currentRunAudit?.summary.lastUpdatedAt
      ? "来自最近审计摘要"
      : activeWorkflowSummary?.updatedAt
        ? "来自 workflow 更新时间"
        : "当前还没有可见更新时间";
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
  const selectedConnector =
    integrations.items.find((item) => item.key === selectedConnectorKey) ??
    integrations.items[0] ??
    null;
  const selectedConnectorRows = React.useMemo(() => {
    if (!selectedConnector) {
      return [];
    }

    return [
      {
        badgeText: formatConnectorTypeLabel(selectedConnector.type),
        badgeTone: "info" as const,
        label: "连接器类型",
        note: selectedConnector.summary,
        value: selectedConnector.name,
      },
      {
        badgeText: formatConnectorEnabledLabel(selectedConnector.enabled),
        badgeTone: selectedConnector.enabled ? ("success" as const) : ("neutral" as const),
        label: "团队使用",
        note:
          selectedConnector.usedByRoles.length > 0
            ? `${selectedConnector.usedByRoles.length} 个角色正在引用`
            : "当前团队还没有显式引用它",
        value:
          selectedConnector.usedByRoles.length > 0
            ? selectedConnector.usedByRoles.join("、")
            : "尚未显式引用",
      },
      {
        badgeText: integrations.runtimeHostLabel || "--",
        badgeTone: integrations.runtimeBaseUrl ? ("info" as const) : ("neutral" as const),
        label: "工作区环境",
        note: integrations.workspaceSummary,
        value:
          integrations.connectorCount > 0
            ? `已加载 ${integrations.connectorCount} 个连接器定义`
            : "当前还没有加载连接器定义",
      },
      {
        badgeText: currentServiceFriendly !== "--" ? "服务入口" : "待配置",
        badgeTone: currentServiceFriendly !== "--" ? ("success" as const) : ("neutral" as const),
        label: "接入位置",
        note: runtimeServiceId || currentServiceKey || "--",
        value: currentServiceFriendly !== "--" ? currentServiceFriendly : "当前还没有主服务入口",
      },
    ];
  }, [
    currentServiceFriendly,
    currentServiceKey,
    integrations.connectorCount,
    integrations.runtimeBaseUrl,
    integrations.runtimeHostLabel,
    integrations.workspaceSummary,
    runtimeServiceId,
    selectedConnector,
  ]);
  const configurationDetailRows = React.useMemo(
    () => [
      {
        label: "团队流程",
        note: `workflowId: ${activeWorkflowId || "--"}`,
        value: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
      },
      {
        label: "绑定方式",
        note:
          currentServiceFriendly !== "--"
            ? `当前会落到 ${currentServiceFriendly}`
            : "当前还没有匹配到主服务入口",
        value: formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
      },
      {
        label: "主服务入口",
        note: `serviceId: ${runtimeServiceId || "--"} · serviceKey: ${currentServiceKey}`,
        value: currentServiceFriendly,
      },
      {
        label: "部署记录",
        note: `deploymentId: ${currentDeploymentId}`,
        value: currentDeploymentFriendly,
      },
      {
        label: "版本标识",
        note: `revisionId: ${currentRevisionId}`,
        value: currentVersionFriendly,
      },
      {
        label: "连接器引用",
        note:
          connectorHighlights.length > 0
            ? connectorHighlights.join("、")
            : integrations.items.length > 0
              ? `工作区可见 ${integrations.items.length} 个连接器定义`
              : "当前工作区还没有可见连接器定义",
        value:
          integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个已引用`
            : "未显式引用",
      },
      {
        label: "服务能力",
        note: `${currentPolicyCount} 条策略`,
        value: `${currentEndpointCount} 个入口`,
      },
    ],
    [
      activeWorkflowId,
      currentDeploymentFriendly,
      currentDeploymentId,
      currentEndpointCount,
      currentPolicyCount,
      currentRevisionId,
      currentServiceFriendly,
      currentServiceKey,
      currentVersionFriendly,
      connectorHighlights,
      integrations.items.length,
      integrations.linkedConnectorCount,
      lens.activeRevision?.implementationKind,
      runtimeServiceId,
      teamTitle,
      workflowNameValue,
    ],
  );
  const topologyConnectors = React.useMemo(
    () =>
      integrations.items
        .filter((item) => item.usedByRoles.length > 0)
        .slice(0, 2),
    [integrations.items],
  );
  const actorLabelMap = React.useMemo(() => {
    const entries = new Map<string, string>();

    lens.members.forEach((member) => {
      if (!trimText(member.actorId)) {
        return;
      }
      entries.set(
        member.actorId,
        trimText(member.actorType) || compactId(member.actorId),
      );
    });

    (actorGraphQuery.data?.subgraph.nodes ?? []).forEach((node) => {
      const label =
        trimText(node.properties.label) ||
        trimText(node.properties.role) ||
        entries.get(node.nodeId) ||
        compactId(node.nodeId);
      entries.set(node.nodeId, label);
    });

    return entries;
  }, [actorGraphQuery.data?.subgraph.nodes, lens.members]);
  const topologyGraph = React.useMemo(() => {
    const subgraph = actorGraphQuery.data?.subgraph;
    const rootActorId =
      trimText(subgraph?.rootNodeId) || effectiveActorId || defaultSelectedActorId;
    const actorNodes = subgraph?.nodes ?? [];
    const actorEdges = subgraph?.edges ?? [];
    const actorNodeMap = new Map(actorNodes.map((node) => [node.nodeId, node]));
    const playbackStepsByActor = new Map<string, TeamPlaybackSummary["steps"]>();
    lens.playback.steps.forEach((step) => {
      const actorId = trimText(step.actorId);
      if (!actorId) {
        return;
      }
      const currentSteps = playbackStepsByActor.get(actorId) ?? [];
      currentSteps.push(step);
      playbackStepsByActor.set(actorId, currentSteps);
    });
    const playbackEventsByActor = new Map<string, TeamPlaybackSummary["events"]>();
    lens.playback.events.forEach((event) => {
      const actorId = trimText(event.actorId);
      if (!actorId) {
        return;
      }
      const currentEvents = playbackEventsByActor.get(actorId) ?? [];
      currentEvents.push(event);
      playbackEventsByActor.set(actorId, currentEvents);
    });

    const baseElements =
      actorNodes.length > 0
        ? buildActorGraphElements(actorNodes, actorEdges, rootActorId)
        : { edges: [] as Edge[], nodes: [] as Node[] };

    const actorDisplayNodes = baseElements.nodes.map((node) => {
      const rawNode = actorNodeMap.get(node.id);
      const latestStep = (playbackStepsByActor.get(node.id) ?? [])[0];
      const label =
        trimText(rawNode?.properties.label) ||
        trimText(rawNode?.properties.role) ||
        actorLabelMap.get(node.id) ||
        compactId(node.id);
      const summary =
        trimText(rawNode?.properties.role) ||
        (trimText(rawNode?.nodeType) ? `团队成员 · ${trimText(rawNode?.nodeType)}` : "") ||
        actorLabelMap.get(node.id) ||
        "团队成员";
      const badgeText = latestStep
        ? formatFriendlyStatus(latestStep.status)
        : node.id === rootActorId
          ? "焦点成员"
          : "团队成员";
      const badgeTone: PillTone = latestStep
        ? latestStep.status === "failed"
          ? "danger"
          : latestStep.status === "waiting"
            ? "warning"
            : latestStep.status === "completed"
              ? "success"
              : "info"
        : node.id === rootActorId
          ? "info"
          : "neutral";
      const entity: TopologyEntitySummary = {
        badgeText,
        badgeTone,
        id: node.id,
        kind: "actor",
        note: trimText(node.id) || "--",
        summary,
        title: label,
      };

      return {
        ...node,
        data: {
          label: React.createElement(TopologyNodeCard, { entity }),
        },
        style: {
          background: "transparent",
          border: `1px solid ${
            node.id === rootActorId ? token.colorPrimaryBorder : token.colorBorderSecondary
          }`,
          borderRadius: 22,
          boxShadow:
            node.id === rootActorId
              ? `0 0 0 2px ${token.colorPrimaryBorder}55, ${token.boxShadowSecondary}`
              : token.boxShadowSecondary,
          padding: 0,
          width: 244,
        },
      } satisfies Node;
    });

    const actorDisplayEdges = baseElements.edges.map((edge, index) => ({
      ...edge,
      animated: false,
      label: "",
      style: {
        stroke:
          index % 3 === 0
            ? token.colorPrimary
            : index % 3 === 1
              ? token.colorSuccess
              : token.colorWarning,
        strokeWidth: 2.5,
      },
    }));

    const positionedActorNodes = actorDisplayNodes;
    const maxActorX =
      positionedActorNodes.length > 0
        ? Math.max(...positionedActorNodes.map((node) => node.position.x))
        : 0;
    const focusActorNode =
      positionedActorNodes.find((node) => node.id === rootActorId) ||
      positionedActorNodes[0];
    const focusPosition = focusActorNode?.position ?? { x: 0, y: 0 };
    const serviceNodeId = trimText(runtimeServiceId)
      ? `topology-service:${runtimeServiceId}`
      : "";
    const hasServiceNode =
      serviceNodeId.length > 0 && currentServiceFriendly !== "--";
    const serviceNodeX = maxActorX + 280;
    const serviceNodeY = focusPosition.y + 70;

    const serviceNode = hasServiceNode
      ? ({
          data: {
            label: React.createElement(TopologyNodeCard, {
              entity: {
                badgeText: currentDeploymentFriendly,
                badgeTone:
                  currentDeploymentStatus !== "--" ? "success" : "neutral",
                id: serviceNodeId,
                kind: "service",
                note: currentServiceKey,
                summary: "对外服务入口",
                title: currentServiceFriendly,
              },
            }),
          },
          id: serviceNodeId,
          position: {
            x: serviceNodeX,
            y: serviceNodeY,
          },
          style: {
            background: "transparent",
            border: `1px solid ${token.colorInfoBorder}`,
            borderRadius: 22,
            boxShadow: token.boxShadowSecondary,
            padding: 0,
            width: 244,
          },
          type: "default",
        } satisfies Node)
      : null;

    const connectorNodes = topologyConnectors.map((connector, index) => {
      const connectorNodeId = `topology-connector:${connector.key}`;
      return {
        data: {
          label: React.createElement(TopologyNodeCard, {
            entity: {
              badgeText: formatConnectorEnabledLabel(connector.enabled),
              badgeTone: connector.enabled ? "warning" : "neutral",
              id: connectorNodeId,
              kind: "connector",
              note: connector.usedByRoles.join("、") || connector.summary,
              summary: `${formatConnectorTypeLabel(connector.type)} 连接器`,
              title: connector.name,
            },
          }),
        },
        id: connectorNodeId,
        position: {
          x: serviceNodeX + 280,
          y: serviceNodeY - 90 + index * 180,
        },
        style: {
          background: "transparent",
          border: `1px solid ${token.colorWarningBorder}`,
          borderRadius: 22,
          boxShadow: token.boxShadowSecondary,
          padding: 0,
          width: 244,
        },
        type: "default",
      } satisfies Node;
    });

    const derivedEdges: Edge[] = [];
    if (hasServiceNode && rootActorId) {
      derivedEdges.push({
        id: `derived-actor-service:${rootActorId}`,
        source: rootActorId,
        target: serviceNodeId,
        label: "",
        style: {
          stroke: token.colorInfo,
          strokeDasharray: "6 6",
          strokeWidth: 2,
        },
      });
    }
    connectorNodes.forEach((connectorNode) => {
      if (!hasServiceNode) {
        return;
      }
      derivedEdges.push({
        id: `derived-service-connector:${connectorNode.id}`,
        source: serviceNodeId,
        target: connectorNode.id,
        label: "",
        style: {
          stroke: token.colorWarning,
          strokeDasharray: "6 6",
          strokeWidth: 2,
        },
      });
    });

    const nodes = [...positionedActorNodes, ...(serviceNode ? [serviceNode] : []), ...connectorNodes];
    const edges = [...actorDisplayEdges, ...derivedEdges];
    const depthMap = buildDepthMap(
      rootActorId,
      edges.map((edge) => ({
        source: String(edge.source),
        target: String(edge.target),
      })),
    );

    const entityMap = new Map<string, TopologyEntitySummary>();
    positionedActorNodes.forEach((node) => {
      const rawNode = actorNodeMap.get(node.id);
      const latestEvent = (playbackEventsByActor.get(node.id) ?? [])[0];
      const latestStep = (playbackStepsByActor.get(node.id) ?? [])[0];
      entityMap.set(node.id, {
        badgeText: latestStep
          ? formatFriendlyStatus(latestStep.status)
          : node.id === rootActorId
            ? "焦点成员"
            : "团队成员",
        badgeTone: latestStep
          ? latestStep.status === "failed"
            ? "danger"
            : latestStep.status === "waiting"
              ? "warning"
              : latestStep.status === "completed"
                ? "success"
                : "info"
          : node.id === rootActorId
            ? "info"
            : "neutral",
        id: node.id,
        kind: "actor",
        note: latestEvent?.message || trimText(node.id) || "--",
        summary:
          trimText(rawNode?.properties.role) ||
          (trimText(rawNode?.nodeType) ? `团队成员 · ${trimText(rawNode?.nodeType)}` : "") ||
          actorLabelMap.get(node.id) ||
          "团队成员",
        title:
          trimText(rawNode?.properties.label) ||
          trimText(rawNode?.properties.role) ||
          actorLabelMap.get(node.id) ||
          compactId(node.id),
      });
    });
    if (serviceNode) {
      entityMap.set(serviceNode.id, {
        badgeText: currentDeploymentFriendly,
        badgeTone: currentDeploymentStatus !== "--" ? "success" : "neutral",
        id: serviceNode.id,
        kind: "service",
        note: currentServiceKey,
        summary: "对外服务入口",
        title: currentServiceFriendly,
      });
    }
    topologyConnectors.forEach((connector) => {
      entityMap.set(`topology-connector:${connector.key}`, {
        badgeText: formatConnectorEnabledLabel(connector.enabled),
        badgeTone: connector.enabled ? "warning" : "neutral",
        id: `topology-connector:${connector.key}`,
        kind: "connector",
        note: connector.usedByRoles.join("、") || connector.summary,
        summary: `${formatConnectorTypeLabel(connector.type)} 连接器`,
        title: connector.name,
      });
    });

    return {
      depthMap,
      entityMap,
      eventMap: playbackEventsByActor,
      nodes,
      rootActorId,
      stepMap: playbackStepsByActor,
      edges,
    };
  }, [
    actorGraphQuery.data?.subgraph,
    actorLabelMap,
    currentDeploymentFriendly,
    currentDeploymentStatus,
    currentServiceFriendly,
    currentServiceKey,
    defaultSelectedActorId,
    effectiveActorId,
    integrations.items,
    lens.members,
    lens.playback.events,
    lens.playback.steps,
    runtimeServiceId,
    token.boxShadowSecondary,
    token.colorBorderSecondary,
    token.colorInfo,
    token.colorInfoBorder,
    token.colorPrimary,
    token.colorPrimaryBorder,
    token.colorSuccess,
    token.colorWarning,
    token.colorWarningBorder,
    topologyConnectors,
  ]);
  const topologyNodeIds = React.useMemo(
    () => topologyGraph.nodes.map((node) => node.id),
    [topologyGraph.nodes],
  );
  React.useEffect(() => {
    if (topologyNodeIds.length === 0) {
      setSelectedTopologyNodeId("");
      return;
    }
    if (!selectedTopologyNodeId || !topologyNodeIds.includes(selectedTopologyNodeId)) {
      setSelectedTopologyNodeId(
        topologyNodeIds.includes(effectiveActorId)
          ? effectiveActorId
          : topologyNodeIds[0],
      );
    }
  }, [effectiveActorId, selectedTopologyNodeId, topologyNodeIds]);
  const selectedTopologyEntity =
    topologyGraph.entityMap.get(selectedTopologyNodeId) ??
    topologyGraph.entityMap.get(effectiveActorId) ??
    null;
  const selectedTopologyEvent =
    (selectedTopologyEntity?.kind === "actor"
      ? topologyGraph.eventMap.get(selectedTopologyEntity.id)?.[0]
      : null) ?? null;
  const selectedTopologyStep =
    (selectedTopologyEntity?.kind === "actor"
      ? topologyGraph.stepMap.get(selectedTopologyEntity.id)?.[0]
      : null) ?? null;
  const selectedTopologyInboundCount = topologyGraph.edges.filter(
    (edge) => edge.target === selectedTopologyEntity?.id,
  ).length;
  const selectedTopologyOutboundCount = topologyGraph.edges.filter(
    (edge) => edge.source === selectedTopologyEntity?.id,
  ).length;
  const selectedTopologyDepth =
    topologyGraph.depthMap.get(selectedTopologyEntity?.id || "") ?? 0;
  const selectedTopologyInboundTitles = React.useMemo(
    () =>
      selectedTopologyEntity
        ? topologyGraph.edges
            .filter((edge) => edge.target === selectedTopologyEntity.id)
            .map(
              (edge) =>
                topologyGraph.entityMap.get(String(edge.source))?.title ||
                compactId(String(edge.source)),
            )
        : [],
    [selectedTopologyEntity, topologyGraph.edges, topologyGraph.entityMap],
  );
  const selectedTopologyOutboundTitles = React.useMemo(
    () =>
      selectedTopologyEntity
        ? topologyGraph.edges
            .filter((edge) => edge.source === selectedTopologyEntity.id)
            .map(
              (edge) =>
                topologyGraph.entityMap.get(String(edge.target))?.title ||
                compactId(String(edge.target)),
            )
        : [],
    [selectedTopologyEntity, topologyGraph.edges, topologyGraph.entityMap],
  );
  const selectedTopologyInboundSummary = summarizeTopologyTitles(
    selectedTopologyInboundTitles,
    "当前没有上游节点",
  );
  const selectedTopologyOutboundSummary = summarizeTopologyTitles(
    selectedTopologyOutboundTitles,
    "当前没有下游节点",
  );
  const selectedTopologyLatestStepLabel = selectedTopologyStep
    ? `${selectedTopologyStep.stepId} · ${formatStepTypeLabel(selectedTopologyStep.stepType)}`
    : "当前还没有可见步骤";
  const selectedTopologyLatestStepNote = selectedTopologyStep
    ? [
        selectedTopologyStep.detail,
        selectedTopologyStep.timestamp
          ? `发生于 ${formatCompactTimestamp(selectedTopologyStep.timestamp)}`
          : "",
      ]
        .filter(Boolean)
        .join(" · ")
    : selectedTopologyEvent?.message || "当前还没有更多节点运行细节。";
  const selectedTopologyRows = React.useMemo(() => {
    if (!selectedTopologyEntity) {
      return [];
    }

    if (selectedTopologyEntity.kind === "service") {
      return [
        {
          badge: "服务",
          label: "主服务",
          note: currentServiceKey,
          value: currentServiceFriendly,
        },
        {
          badge: "部署",
          label: "部署状态",
          note: currentDeploymentId,
          value: currentDeploymentFriendly,
        },
        {
          badge: `${currentEndpointCount} 个入口`,
          label: "服务能力",
          note: `${currentPolicyCount} 条策略`,
          value: runtimeServiceId || "--",
        },
        {
          badge: `${selectedTopologyInboundCount} 条入边`,
          label: "上游来自",
          note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
          value: selectedTopologyInboundSummary,
        },
        {
          badge: `${selectedTopologyOutboundCount} 条出边`,
          label: "下游连接",
          note: "当前团队通过这个入口继续流向工具或下游能力",
          value: selectedTopologyOutboundSummary,
        },
      ];
    }

    if (selectedTopologyEntity.kind === "connector") {
      const connector = topologyConnectors.find(
        (item) => `topology-connector:${item.key}` === selectedTopologyEntity.id,
      );
      return [
        {
          badge: formatConnectorTypeLabel(connector?.type || "--"),
          label: "连接器",
          note: connector?.summary || "--",
          value: connector?.name || selectedTopologyEntity.title,
        },
        {
          badge: formatConnectorEnabledLabel(Boolean(connector?.enabled)),
          label: "团队使用",
          note: connector?.usedByRoles.join("、") || "当前团队还没有引用它",
          value:
            connector?.usedByRoles.length
              ? `${connector.usedByRoles.length} 个角色`
              : "0 个角色",
        },
        {
          badge: `${selectedTopologyInboundCount} 条入边`,
          label: "上游来自",
          note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
          value: selectedTopologyInboundSummary,
        },
        {
          badge: `${selectedTopologyOutboundCount} 条出边`,
          label: "下游连接",
          note: "当前节点来自团队配置推导，不直接代表一次实时运行",
          value: selectedTopologyOutboundSummary,
        },
      ];
    }

    return [
      {
        badge: formatTopologyNodeKindLabel(selectedTopologyEntity.kind),
        label: "角色定位",
        note: selectedTopologyEntity.id,
        value: selectedTopologyEntity.title,
      },
      {
        badge: selectedTopologyStep
          ? formatStepTypeLabel(selectedTopologyStep.stepType)
          : "最近事件",
        label: "最近一步",
        note: selectedTopologyLatestStepNote,
        value: selectedTopologyLatestStepLabel,
      },
      {
        badge: selectedTopologyStep
          ? formatFriendlyStatus(selectedTopologyStep.status)
          : selectedTopologyEntity.badgeText,
        label: "当前状态",
        note:
          selectedTopologyEvent?.message ||
          selectedTopologyEntity.note ||
          "当前还没有更多节点运行细节。",
        value: selectedTopologyEntity.summary,
      },
      {
        badge: `${selectedTopologyInboundCount} 条入边`,
        label: "上游来自",
        note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
        value: selectedTopologyInboundSummary,
      },
      {
        badge: `${selectedTopologyOutboundCount} 条出边`,
        label: "下游流向",
        note:
          selectedTopologyEntity.id === topologyGraph.rootActorId
            ? "这是当前焦点路径的起点"
            : "这是围绕当前焦点展开的协作节点",
        value: selectedTopologyOutboundSummary,
      },
    ];
  }, [
    currentDeploymentFriendly,
    currentDeploymentId,
    currentEndpointCount,
    currentPolicyCount,
    currentServiceFriendly,
    currentServiceKey,
    runtimeServiceId,
    selectedTopologyDepth,
    selectedTopologyEntity,
    selectedTopologyEvent,
    selectedTopologyInboundCount,
    selectedTopologyInboundSummary,
    selectedTopologyOutboundCount,
    selectedTopologyOutboundSummary,
    selectedTopologyLatestStepLabel,
    selectedTopologyLatestStepNote,
    selectedTopologyStep,
    topologyConnectors,
    topologyGraph.rootActorId,
  ]);
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
  const displayedRunId = lens.currentRun?.runId || preferredRunId || "";
  const runSwitchOptions = React.useMemo(
    () =>
      (runsQuery.data?.runs ?? []).slice(0, 4).map((run) => ({
        label: `${formatCompactTimestamp(run.lastUpdatedAt)} · ${formatFriendlyStatus(run.completionStatus)}`,
        runId: run.runId,
      })),
    [runsQuery.data?.runs],
  );
  const playbackStepMap = React.useMemo(
    () => new Map(lens.playback.steps.map((step) => [step.stepId, step])),
    [lens.playback.steps],
  );
  const eventStreamRows = React.useMemo(
    () =>
      lens.playback.events.map((event) => {
        const relatedStep = event.stepId ? playbackStepMap.get(event.stepId) ?? null : null;
        const sourceLabel =
          trimText(event.actorId ? actorLabelMap.get(event.actorId) : "") ||
          compactId(event.actorId) ||
          "当前团队";
        const targetLabel =
          trimText(relatedStep?.owner) ||
          trimText(relatedStep?.actorId ? actorLabelMap.get(relatedStep.actorId) : "") ||
          "";
        const flowLabel =
          targetLabel && normalizeStatus(targetLabel) !== normalizeStatus(sourceLabel)
            ? `${sourceLabel} -> ${targetLabel}`
            : sourceLabel;
        const detailNote = [event.detail, relatedStep?.summary || ""]
          .map((part) => trimText(part))
          .filter(Boolean)
          .filter((part, index, items) => items.indexOf(part) === index)
          .join(" · ");

        return {
          detail: event.message,
          detailNote,
          flowLabel,
          key: event.key,
          stageLabel: formatEventStreamStageLabel(event.stage, relatedStep?.stepType),
          stageTone: resolveEventStreamTone(event.stage, event.tone, relatedStep?.stepType),
          timeLabel: formatClockTimestamp(event.timestamp),
        };
      }),
    [actorLabelMap, lens.playback.events, playbackStepMap],
  );
  const memberMappingRows = React.useMemo(() => {
    const memberByActorId = new Map(lens.members.map((member) => [member.actorId, member]));
    const rows = new Map<
      string,
      {
        implementation: string;
        key: string;
        member: string;
        responsibility: string;
        serviceLabel: string;
        serviceNote: string;
        statusLabel: string;
        statusNote?: string;
        statusTone: PillTone;
      }
    >();

    lens.playback.steps.forEach((step) => {
      const actorId = trimText(step.actorId);
      const member = actorId ? memberByActorId.get(actorId) ?? null : null;
      const actorLabel =
        trimText(actorId ? actorLabelMap.get(actorId) : "") ||
        trimText(step.owner) ||
        trimText(member?.actorType) ||
        compactId(actorId) ||
        "当前成员";
      const matchingRole =
        teamCompositionRows.find(
          (row) =>
            normalizeStatus(row.name) === normalizeStatus(step.owner) ||
            normalizeStatus(row.name) === normalizeStatus(actorLabel),
        ) || null;
      const rowKey =
        actorId || `owner:${normalizeStatus(step.owner) || normalizeStatus(actorLabel)}`;

      if (rows.has(rowKey)) {
        return;
      }

      rows.set(rowKey, {
        implementation:
          trimText(member?.actorType) ||
          formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
        key: rowKey,
        member: actorLabel,
        responsibility: matchingRole?.summary || step.summary || step.detail || "--",
        serviceLabel: currentServiceFriendly,
        serviceNote: runtimeServiceId || currentServiceKey || "--",
        statusLabel: formatMemberPresenceLabel(member?.isFocused ? "focus" : "run"),
        statusNote: formatFriendlyStatus(step.status),
        statusTone: resolveMemberPresenceTone(member?.isFocused ? "focus" : "run"),
      });
    });

    if (rows.size > 0) {
      return [...rows.values()];
    }

    lens.playback.events.forEach((event) => {
      const actorId = trimText(event.actorId);
      const member = actorId ? memberByActorId.get(actorId) ?? null : null;
      const actorLabel =
        trimText(actorId ? actorLabelMap.get(actorId) : "") ||
        trimText(member?.actorType) ||
        compactId(actorId) ||
        "当前成员";
      const rowKey = actorId || `event:${event.key}`;
      if (rows.has(rowKey)) {
        return;
      }

      rows.set(rowKey, {
        implementation:
          trimText(member?.actorType) ||
          formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
        key: rowKey,
        member: actorLabel,
        responsibility: event.message || event.detail || "--",
        serviceLabel: currentServiceFriendly,
        serviceNote: runtimeServiceId || currentServiceKey || "--",
        statusLabel: formatMemberPresenceLabel(member?.isFocused ? "focus" : "run"),
        statusNote: formatEventStreamStageLabel(event.stage),
        statusTone: resolveMemberPresenceTone(member?.isFocused ? "focus" : "run"),
      });
    });

    return [...rows.values()];
  }, [
    actorLabelMap,
    currentServiceFriendly,
    currentServiceKey,
    lens.activeRevision?.implementationKind,
    lens.members,
    lens.playback.events,
    lens.playback.steps,
    runtimeServiceId,
    teamCompositionRows,
  ]);
  const runtimeIdentityRows = React.useMemo(
    () =>
      lens.members.map((member) => {
        const graphNode =
          lens.graph.nodes.find((node) => node.actorId === member.actorId) ?? null;
        return {
          actorId: member.actorId,
          implementation:
            trimText(member.actorType) ||
            formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
          key: member.actorId,
          member:
            trimText(actorLabelMap.get(member.actorId)) ||
            trimText(member.actorType) ||
            compactId(member.actorId),
          note:
            graphNode?.caption ||
            "当前还没有更多可见的运行时协作关系。",
          relationLabel:
            graphNode != null
              ? `${graphNode.relationCount} 条可见关系`
              : "暂无可见关系",
          statusLabel: formatMemberPresenceLabel(member.isFocused ? "focus" : "visible"),
          statusTone: resolveMemberPresenceTone(member.isFocused ? "focus" : "visible"),
        };
      }),
    [actorLabelMap, lens.activeRevision?.implementationKind, lens.graph.nodes, lens.members],
  );

  const tabOptions: TeamTabOption[] = [
    { label: "概览", value: "overview" },
    { label: "事件拓扑", value: "topology" },
    { label: "事件流", value: "events" },
    { label: "团队成员", value: "members" },
    { label: "连接器", value: "connectors" },
    { label: "配置", value: "advanced" },
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
          runId:
            preferredRunId ||
            lens.currentRun?.runId ||
            lens.playback.currentRunId ||
            undefined,
          tab,
        }),
      );
    },
    [
      activeWorkflowId,
      lens.currentRun?.runId,
      lens.playback.currentRunId,
      preferredRunId,
      runtimeServiceId,
      scopeId,
    ],
  );

  const handleSelectRun = React.useCallback(
    (runId: string) => {
      const normalizedRunId = trimText(runId);
      setPreferredRunId(normalizedRunId);
      setSelectedActorId("");
      setSelectedTopologyNodeId("");
      history.push(
        buildTeamDetailHref({
          scopeId,
          workflowId: activeWorkflowId || undefined,
          serviceId: runtimeServiceId,
          runId: normalizedRunId || undefined,
          tab: activeTab,
        }),
      );
    },
    [activeTab, activeWorkflowId, runtimeServiceId, scopeId],
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
              caption={latestVisibleUpdateNote}
            />
          </div>
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
      <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
        <div
          style={{
            alignItems: "flex-start",
            background: token.colorBgContainer,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 24,
            boxShadow: token.boxShadowSecondary,
            display: "flex",
            flexWrap: "wrap",
            gap: 16,
            justifyContent: "space-between",
            padding: 20,
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <Space wrap size={8}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                当前拓扑视角
              </Typography.Text>
              <DetailPill
                compact
                style={resolveObservationPillStyle(token, graphProvenance.status)}
                text={graphProvenance.label}
              />
            </Space>
            <Typography.Text style={{ fontSize: 13 }} type="secondary">
              {selectedFocusReason || "围绕当前焦点成员展开团队消息路径。点击左侧节点即可切换视角。"}
            </Typography.Text>
          </div>
          <Space size={10} wrap>
            <div
              aria-label="拓扑深度"
              role="group"
              style={{
                alignItems: "center",
                background: token.colorFillAlter,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 999,
                display: "inline-flex",
                gap: 4,
                padding: 4,
              }}
            >
              {[1, 2, 3].map((depth) => {
                const active = graphDepth === depth;
                return (
                  <button
                    key={depth}
                    onClick={() => setGraphDepth(depth)}
                    style={{
                      background: active ? token.colorPrimaryBg : "transparent",
                      border: "none",
                      borderRadius: 999,
                      color: active ? token.colorPrimary : token.colorTextSecondary,
                      cursor: "pointer",
                      fontSize: 13,
                      fontWeight: active ? 700 : 500,
                      height: 32,
                      padding: "0 14px",
                      transition: "all 140ms ease",
                    }}
                    type="button"
                  >
                    {formatTopologyDepthLabel(depth)}
                  </button>
                );
              })}
            </div>
            <Button
              onClick={handleOpenServiceMapping}
              style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
              type="primary"
            >
              打开平台拓扑
            </Button>
          </Space>
        </div>
        {actorGraphQuery.isLoading ? (
          <AevatarInspectorEmpty description="正在加载团队拓扑。" />
        ) : actorGraphQuery.isError ? (
          <AevatarInspectorEmpty
            title="拓扑暂不可用"
            description="当前无法读取团队拓扑，请稍后重试。"
          />
        ) : (
          <div
            style={{
              display: "grid",
              gap: 18,
              gridTemplateColumns: "minmax(0, 1.2fr) minmax(320px, 0.88fr)",
            }}
          >
            <AevatarPanel title="团队事件路径">
              {topologyGraph.nodes.length > 0 ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
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
                      <Typography.Text strong style={{ fontSize: 15 }}>
                        从当前焦点成员出发，查看消息如何流向服务与连接器
                      </Typography.Text>
                      <Typography.Text style={{ fontSize: 13 }} type="secondary">
                        {`当前视图包含 ${topologyGraph.nodes.length} 个节点，${topologyGraph.edges.length} 条连线。`}
                      </Typography.Text>
                    </div>
                    <Typography.Text style={{ fontSize: 13 }} type="secondary">
                      {`${formatTopologyDepthLabel(graphDepth)}视角 · 焦点 ${compactId(effectiveActorId)}`}
                    </Typography.Text>
                  </div>
                  <GraphCanvas
                    edges={topologyGraph.edges}
                    height={384}
                    nodes={topologyGraph.nodes}
                    onCanvasSelect={() =>
                      setSelectedTopologyNodeId(
                        topologyGraph.entityMap.has(effectiveActorId)
                          ? effectiveActorId
                          : topologyGraph.nodes[0]?.id || "",
                      )
                    }
                    onNodeSelect={(nodeId) => {
                      setSelectedTopologyNodeId(nodeId);
                      if (topologyGraph.entityMap.get(nodeId)?.kind === "actor") {
                        setSelectedActorId(nodeId);
                      }
                    }}
                    selectedNodeId={selectedTopologyNodeId || effectiveActorId}
                  />
                  <Space size={[8, 8]} wrap>
                    <DetailPill
                      compact
                      style={resolveTonePillStyle(token, "info")}
                      text="实线关系 = 运行事实"
                    />
                    <DetailPill
                      compact
                      style={resolveTonePillStyle(token, "warning")}
                      text="虚线关系 = 配置推导"
                    />
                    <DetailPill
                      compact
                      style={resolveTonePillStyle(token, "neutral")}
                      text="节点语义来自成员、服务、连接器的当前事实"
                    />
                  </Space>
                </div>
              ) : (
                <AevatarInspectorEmpty
                  title="暂无可见关系"
                  description="当前没有更多可见的事件拓扑关系。"
                />
              )}
            </AevatarPanel>
            <AevatarPanel
              title="当前选中节点"
              extra={
                selectedTopologyEntity ? (
                  <DetailPill
                    compact
                    style={resolveTonePillStyle(token, selectedTopologyEntity.badgeTone)}
                    text={selectedTopologyEntity.badgeText}
                  />
                ) : undefined
              }
            >
              {selectedTopologyEntity ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
                  <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    <Space wrap size={8}>
                      <Typography.Title level={3} style={{ margin: 0 }}>
                        {selectedTopologyEntity.title}
                      </Typography.Title>
                      <DetailPill
                        compact
                        style={resolveTonePillStyle(token, "neutral")}
                        text={formatTopologyNodeKindLabel(selectedTopologyEntity.kind)}
                      />
                    </Space>
                    <Typography.Text type="secondary">
                      {selectedTopologyEntity.summary}
                    </Typography.Text>
                  </div>
                  <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                    {selectedTopologyRows.map((row, index) => (
                      <div
                        key={`${row.label}-${index}`}
                        style={{
                          alignItems: "start",
                          borderTop:
                            index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                          display: "grid",
                          gap: 12,
                          gridTemplateColumns: "minmax(88px, 120px) minmax(0, 1fr) max-content",
                          paddingTop: index === 0 ? 0 : 14,
                        }}
                      >
                        <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                          {row.label}
                        </Typography.Text>
                        <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                          <FactLine rows={2} text={String(row.value)} />
                          <FactLine rows={2} secondary text={String(row.note)} />
                        </div>
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(token, "neutral")}
                          text={row.badge}
                        />
                      </div>
                    ))}
                  </div>
                </div>
              ) : (
                <AevatarInspectorEmpty
                  title="当前还没有选中节点"
                  description="请先从左侧团队事件路径里选择一个节点。"
                />
              )}
            </AevatarPanel>
          </div>
        )}
      </div>
    );
  };

  const renderEventsTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel
          title="当前任务事件流"
          extra={
            <Space size={8} wrap>
              <DetailPill
                compact
                style={resolveObservationPillStyle(token, playbackProvenance.status)}
                text={playbackProvenance.label}
              />
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {activeRunId ? `run · ${activeRunId}` : "暂无当前 run"}
              </Typography.Text>
            </Space>
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
                  alignItems: "flex-start",
                  background: token.colorBgContainerDisabled,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 18,
                  display: "flex",
                  flexWrap: "wrap",
                  gap: 12,
                  justifyContent: "space-between",
                  padding: 16,
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 8, minWidth: 0 }}>
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
                  {runSwitchOptions.length > 1 ? (
                    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        切换 Run
                      </Typography.Text>
                      <div
                        style={{
                          alignItems: "center",
                          background: token.colorBgContainer,
                          border: `1px solid ${token.colorBorderSecondary}`,
                          borderRadius: 999,
                          display: "inline-flex",
                          flexWrap: "wrap",
                          gap: 6,
                          padding: 6,
                        }}
                      >
                        {runSwitchOptions.map((option) => {
                          const selected = option.runId === displayedRunId;
                          return (
                            <button
                              aria-label={`切换到 ${option.runId}`}
                              key={option.runId}
                              onClick={() => handleSelectRun(option.runId)}
                              style={{
                                ...resolveSegmentedButtonStyle(token, selected),
                              }}
                              type="button"
                            >
                              {option.label}
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  ) : null}
                </div>
                <Space wrap>
                  <Button
                    disabled={!activeRunId}
                    onClick={() => handleOpenPlaybackActor(lens.currentRun?.actorId, activeRunId)}
                    style={resolveActionButtonStyle(token)}
                  >
                    打开完整审计
                  </Button>
                  <Button onClick={handleOpenConversation} style={resolveActionButtonStyle(token)}>
                    进入 Chat
                  </Button>
                  <Button
                    onClick={handleOpenServiceMapping}
                    style={resolveActionButtonStyle(token, "primary")}
                    type="primary"
                  >
                    查看服务映射
                  </Button>
                </Space>
              </div>
              {eventStreamRows.length > 0 ? (
                <div
                  style={{
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 18,
                    overflow: "hidden",
                  }}
                >
                  <div style={{ overflowX: "auto" }}>
                    <div style={{ minWidth: 920 }}>
                      <div
                        style={{
                          background: token.colorBgContainerDisabled,
                          borderBottom: `1px solid ${token.colorBorderSecondary}`,
                          color: token.colorTextSecondary,
                          display: "grid",
                          fontSize: 12,
                          fontWeight: 600,
                          gap: 16,
                          gridTemplateColumns:
                            "96px 112px minmax(200px, 1.25fr) minmax(280px, 2fr)",
                          padding: "12px 16px",
                        }}
                      >
                        <span>时间</span>
                        <span>事件</span>
                        <span>流向</span>
                        <span>说明</span>
                      </div>
                      {eventStreamRows.map((row, index) => (
                        <div
                          key={row.key}
                          style={{
                            borderTop:
                              index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                            display: "grid",
                            gap: 16,
                            gridTemplateColumns:
                              "96px 112px minmax(200px, 1.25fr) minmax(280px, 2fr)",
                            padding: "14px 16px",
                          }}
                        >
                          <Typography.Text
                            strong
                            style={{ fontFamily: factValueFontFamily, whiteSpace: "nowrap" }}
                          >
                            {row.timeLabel}
                          </Typography.Text>
                          <DetailPill
                            compact
                            style={resolveTonePillStyle(token, row.stageTone)}
                            text={row.stageLabel}
                          />
                          <FactLine rows={2} text={row.flowLabel} />
                          <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                            <Typography.Text>{row.detail}</Typography.Text>
                            {row.detailNote ? (
                              <FactLine rows={2} secondary text={row.detailNote} />
                            ) : null}
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              ) : (
                <Typography.Text type="secondary">
                  当前还没有更多可见的事件事实。
                </Typography.Text>
              )}
            </div>
          )}
        </AevatarPanel>
        <AevatarPanel
          title="本次 Run 成员映射"
          extra={
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              仅展示当前 run 命中的成员、职责与关联服务
            </Typography.Text>
          }
        >
          {memberMappingRows.length > 0 ? (
            <div
              style={{
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                overflow: "hidden",
              }}
            >
              <div style={{ overflowX: "auto" }}>
                <div style={{ minWidth: 920 }}>
                  <div
                    style={{
                      background: token.colorBgContainerDisabled,
                      borderBottom: `1px solid ${token.colorBorderSecondary}`,
                      color: token.colorTextSecondary,
                      display: "grid",
                      fontSize: 12,
                      fontWeight: 600,
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(120px, 1fr) minmax(220px, 1.8fr) minmax(120px, 0.9fr) minmax(200px, 1.2fr) minmax(132px, 0.95fr)",
                      padding: "12px 16px",
                    }}
                  >
                    <span>成员</span>
                    <span>职责</span>
                    <span>实现</span>
                    <span>关联服务</span>
                    <span>状态</span>
                  </div>
                  {memberMappingRows.map((row, index) => (
                    <div
                      key={row.key}
                      style={{
                        alignItems: "center",
                        borderTop:
                          index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                        display: "grid",
                        gap: 16,
                        gridTemplateColumns:
                          "minmax(120px, 1fr) minmax(220px, 1.8fr) minmax(120px, 0.9fr) minmax(200px, 1.2fr) minmax(132px, 0.95fr)",
                        padding: "14px 16px",
                      }}
                    >
                      <Typography.Text strong>{row.member}</Typography.Text>
                      <FactLine rows={2} text={row.responsibility} />
                      <Typography.Text style={{ fontFamily: factValueFontFamily }}>
                        {row.implementation}
                      </Typography.Text>
                      <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                        <Typography.Text strong>{row.serviceLabel}</Typography.Text>
                        <FactLine rows={1} secondary text={row.serviceNote} />
                      </div>
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          flexDirection: "column",
                          gap: 4,
                          minWidth: 0,
                        }}
                      >
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(token, row.statusTone)}
                          text={row.statusLabel}
                        />
                        {row.statusNote ? (
                          <Typography.Text style={{ fontSize: 12 }} type="secondary">
                            {row.statusNote}
                          </Typography.Text>
                        ) : null}
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="当前 run 还没有命中可见成员"
              description="等这支团队产生运行步骤或事件后，这里才会显示本次 run 的参与成员。"
            />
          )}
        </AevatarPanel>
      </div>
    );
  };

  const renderMembersTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel
          title="团队结构"
          extra={
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              角色 · 职责 · 实现
            </Typography.Text>
          }
        >
          {teamCompositionRows.length > 0 ? (
            <div
              style={{
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                overflow: "hidden",
              }}
            >
              <div style={{ overflowX: "auto" }}>
                <div style={{ minWidth: 720 }}>
                  <div
                    style={{
                      background: token.colorBgContainerDisabled,
                      borderBottom: `1px solid ${token.colorBorderSecondary}`,
                      color: token.colorTextSecondary,
                      display: "grid",
                      fontSize: 12,
                      fontWeight: 600,
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(140px, 1fr) minmax(280px, 2fr) minmax(120px, 0.9fr)",
                      padding: "12px 16px",
                    }}
                  >
                    <span>角色</span>
                    <span>职责</span>
                    <span>实现</span>
                  </div>
                  {teamCompositionRows.map((row, index) => (
                    <div
                      key={row.key}
                      style={{
                        alignItems: "center",
                        borderTop:
                          index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                        display: "grid",
                        gap: 16,
                        gridTemplateColumns:
                          "minmax(140px, 1fr) minmax(280px, 2fr) minmax(120px, 0.9fr)",
                        padding: "14px 16px",
                      }}
                    >
                      <Typography.Text strong>{row.name}</Typography.Text>
                      <FactLine rows={2} text={row.summary} />
                      <DetailPill
                        compact
                        style={resolveCompositionKindPillStyle(token, row.kind)}
                        text={formatCompositionKind(row.kind)}
                      />
                    </div>
                  ))}
                </div>
              </div>
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="暂时还没有团队结构"
              description="当前还没有 workflow 角色定义或可见的团队结构信息。"
            />
          )}
        </AevatarPanel>
        <AevatarPanel
          title="可见 Actor 身份"
          extra={
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              运行时实体 · actorId · 焦点状态
            </Typography.Text>
          }
        >
          {runtimeIdentityRows.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
              {runtimeIdentityRows.map((row) => (
                <button
                  aria-label={`选择成员 ${row.member} ${row.actorId}`}
                  key={row.key}
                  onClick={() => setSelectedActorId(row.actorId)}
                  style={{
                    ...resolveSelectionCardButtonStyle(
                      token,
                      row.actorId === effectiveActorId,
                    ),
                    alignItems: "center",
                    display: "grid",
                    gap: 16,
                    gridTemplateColumns:
                      "minmax(140px, 1fr) minmax(220px, 1.6fr) minmax(120px, 0.9fr) max-content",
                    padding: "14px 16px",
                    textAlign: "left",
                  }}
                  type="button"
                >
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    <Typography.Text strong>{row.member}</Typography.Text>
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      {row.relationLabel}
                    </Typography.Text>
                  </div>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    <FactLine rows={1} text={row.actorId} />
                    <FactLine rows={2} secondary text={row.note} />
                  </div>
                  <Typography.Text style={{ fontFamily: factValueFontFamily }}>
                    {row.implementation}
                  </Typography.Text>
                  <DetailPill
                    compact
                    style={resolveTonePillStyle(token, row.statusTone)}
                    text={row.statusLabel}
                  />
                </button>
              ))}
            </div>
          ) : (
            <AevatarInspectorEmpty
              title="暂时还没有可见 Actor"
              description="当前还没有观察到这支团队的运行时实体身份。"
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
          title="当前连接方式"
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
              label="主服务入口"
              value={currentServiceFriendly}
              captionMonospace
              caption={runtimeServiceId || currentServiceKey || "--"}
            />
            <SignalCard
              icon={<BranchesOutlined />}
              label="当前可用连接器"
              value={enabledConnectorCount}
              caption={`已绑定 ${integrations.linkedConnectorCount} 个 · 工作区可见 ${integrations.items.length} 个`}
            />
            <SignalCard
              label="团队会用到"
              value={
                integrations.linkedConnectorCount > 0
                  ? `${integrations.linkedConnectorCount} 个连接器`
                  : "尚未显式引用"
              }
              caption={
                connectorHighlights.length > 0
                  ? connectorHighlights.join("、")
                  : "当前 workflow 还没有显式引用连接器"
              }
            />
          </div>
        </AevatarPanel>
        {integrations.items.length > 0 ? (
          <div
            style={{
              display: "grid",
              gap: 16,
              gridTemplateColumns: "minmax(0, 1.2fr) minmax(320px, 0.8fr)",
            }}
          >
            <AevatarPanel
              title="连接器目录"
              extra={
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  点击卡片查看这支团队如何使用它
                </Typography.Text>
              }
            >
              <div
                style={{
                  display: "grid",
                  gap: 10,
                  gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
                }}
              >
                {integrations.items.map((connector) => {
                  const isSelected = connector.key === selectedConnector?.key;
                  return (
                    <button
                      aria-label={`选择连接器 ${connector.name}`}
                      key={connector.key}
                      onClick={() => setSelectedConnectorKey(connector.key)}
                      style={{
                        ...resolveSelectionCardButtonStyle(token, isSelected),
                        display: "flex",
                        flexDirection: "column",
                        gap: 10,
                        padding: 16,
                        textAlign: "left",
                      }}
                      type="button"
                    >
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          gap: 10,
                          justifyContent: "space-between",
                        }}
                      >
                        <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                          <Typography.Text strong>{connector.name}</Typography.Text>
                          <Typography.Text style={{ fontSize: 12 }} type="secondary">
                            {connector.summary}
                          </Typography.Text>
                        </div>
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(token, "info")}
                          text={formatConnectorTypeLabel(connector.type)}
                        />
                      </div>
                      <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(
                            token,
                            connector.enabled ? "success" : "neutral",
                          )}
                          text={formatConnectorEnabledLabel(connector.enabled)}
                        />
                        <DetailPill
                          compact
                          style={resolveTonePillStyle(
                            token,
                            connector.usedByRoles.length > 0 ? "info" : "neutral",
                          )}
                          text={
                            connector.usedByRoles.length > 0
                              ? `${connector.usedByRoles.length} 个角色在用`
                              : "团队未显式引用"
                          }
                        />
                      </div>
                      <Typography.Text type="secondary">
                        {connector.usedByRoles.length > 0
                          ? `当前团队会用到：${connector.usedByRoles.join("、")}`
                          : "当前团队还没有显式引用这个连接器。"}
                      </Typography.Text>
                    </button>
                  );
                })}
              </div>
            </AevatarPanel>
            <AevatarPanel
              title="当前选中连接器"
              extra={
                selectedConnector ? (
                  <DetailPill
                    compact
                    style={resolveTonePillStyle(
                      token,
                      selectedConnector.enabled ? "success" : "neutral",
                    )}
                    text={formatConnectorEnabledLabel(selectedConnector.enabled)}
                  />
                ) : null
              }
            >
              {selectedConnector ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <Typography.Title level={3} style={{ margin: 0 }}>
                      {selectedConnector.name}
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      {selectedConnector.summary}
                    </Typography.Text>
                  </div>
                  {selectedConnectorRows.map((row) => (
                    <div
                      key={row.label}
                      style={{
                        borderTop: `1px solid ${token.colorBorderSecondary}`,
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "minmax(96px, 120px) minmax(0, 1fr) max-content",
                        paddingTop: 14,
                      }}
                    >
                      <Typography.Text type="secondary">{row.label}</Typography.Text>
                      <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                        <Typography.Text strong>{row.value}</Typography.Text>
                        <FactLine rows={2} secondary text={row.note} />
                      </div>
                      <DetailPill
                        compact
                        style={resolveTonePillStyle(token, row.badgeTone)}
                        text={row.badgeText}
                      />
                    </div>
                  ))}
                </div>
              ) : (
                <AevatarInspectorEmpty
                  title="请选择一个连接器"
                  description="点击左侧卡片，查看它在这支团队里的接入方式。"
                />
              )}
            </AevatarPanel>
          </div>
        ) : (
          <AevatarPanel title="连接器目录">
            <AevatarInspectorEmpty
              title="暂无连接器"
              description="当前工作区还没有可见的连接器定义。"
            />
          </AevatarPanel>
        )}
      </div>
    );
  };

  const renderAdvancedTab = () => {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel title="当前配置主线">
          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
            }}
          >
            <SignalCard
              label="团队流程"
              value={workflowNameValue !== "--" ? workflowNameValue : teamTitle}
              captionMonospace
              caption={activeWorkflowId || "--"}
            />
            <SignalCard
              label="绑定方式"
              value={formatCompositionKind(lens.activeRevision?.implementationKind || "runtime")}
              caption={
                currentServiceFriendly !== "--"
                  ? `当前会落到 ${currentServiceFriendly}`
                  : "当前还没有匹配到主服务入口"
              }
            />
            <SignalCard
              label="部署记录"
              value={currentDeploymentFriendly}
              captionMonospace
              caption={currentDeploymentId}
            />
            <SignalCard
              label="连接器引用"
              value={
                integrations.linkedConnectorCount > 0
                  ? `${integrations.linkedConnectorCount} 个已引用`
                  : "未显式引用"
              }
              caption={
                connectorHighlights.length > 0
                  ? connectorHighlights.join("、")
                  : "当前 workflow 还没有显式引用连接器"
              }
            />
          </div>
        </AevatarPanel>
        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "minmax(0, 1fr) minmax(320px, 0.78fr)",
          }}
        >
          <AevatarPanel
            title="当前配置明细"
            extra={
              <DetailPill
                compact
                style={resolveStatusPillStyle(token, currentDeploymentStatus)}
                text={currentDeploymentFriendly}
              />
            }
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
              {configurationDetailRows.map((row) => (
                <div
                  key={row.label}
                  style={{
                    borderTop: `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(96px, 120px) minmax(0, 1fr)",
                    paddingTop: 14,
                  }}
                >
                  <Typography.Text type="secondary">{row.label}</Typography.Text>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    <Typography.Text strong>{row.value}</Typography.Text>
                    <FactLine rows={2} secondary text={row.note} />
                  </div>
                </div>
              ))}
            </div>
          </AevatarPanel>
          <AevatarPanel title="继续调整这支团队">
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <Typography.Text strong>
                  先确认这次要调整的是流程、服务映射，还是连接器引用。
                </Typography.Text>
                <Typography.Text type="secondary">
                  当前会影响 {currentServiceFriendly}、{currentVersionFriendly}，以及
                  {integrations.linkedConnectorCount > 0
                    ? ` ${integrations.linkedConnectorCount} 个已绑定连接器`
                    : " 当前还没有显式绑定的连接器"}
                  。
                </Typography.Text>
              </div>
              <div
                style={{
                  display: "grid",
                  gap: 10,
                  gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
                }}
              >
                <SignalCard
                  label="发布状态"
                  value={currentDeploymentFriendly}
                  caption={currentDeploymentStatus}
                />
                <SignalCard
                  label="服务能力"
                  value={`${currentEndpointCount} 个入口`}
                  caption={`${currentPolicyCount} 条策略`}
                />
                <SignalCard
                  label="连接器绑定"
                  value={
                    integrations.linkedConnectorCount > 0
                      ? `${integrations.linkedConnectorCount} 个`
                      : "未显式绑定"
                  }
                  caption={
                    connectorHighlights.length > 0
                      ? connectorHighlights.join("、")
                      : "当前 workflow 还没有显式引用连接器"
                  }
                />
              </div>
              <Space wrap>
                <Button
                  onClick={handleOpenServiceMapping}
                  style={resolveActionButtonStyle(token, "primary")}
                  type="primary"
                >
                  查看服务映射
                </Button>
                <Button
                  onClick={() => history.push(teamBuilderRoute)}
                  style={resolveActionButtonStyle(token)}
                >
                  打开 Team Builder
                </Button>
                <Button onClick={handleOpenConversation} style={resolveActionButtonStyle(token)}>
                  进入 Chat
                </Button>
              </Space>
            </div>
          </AevatarPanel>
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
      breadcrumbRender={false}
      title={
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <Typography.Text
            style={{
              color: token.colorTextTertiary,
              fontSize: 13,
              fontWeight: 500,
              lineHeight: 1.4,
            }}
          >
            <Typography.Link
              href={teamsListHref}
              onClick={(event) => {
                event.preventDefault();
                history.push(teamsListHref);
              }}
              style={{
                color: token.colorTextTertiary,
                fontSize: "inherit",
                fontWeight: "inherit",
              }}
            >
              Aevatar
            </Typography.Link>
            {" / "}
            <Typography.Link
              href={teamsListHref}
              onClick={(event) => {
                event.preventDefault();
                history.push(teamsListHref);
              }}
              style={{
                color: token.colorTextTertiary,
                fontSize: "inherit",
                fontWeight: "inherit",
              }}
            >
              Teams
            </Typography.Link>
            {` / 团队详情 / ${formatTeamTabLabel(activeTab)}`}
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
            进入 Chat
          </Button>
          <Button
            onClick={() => history.push(teamBuilderRoute)}
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
          >
            进入 Team Builder
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
