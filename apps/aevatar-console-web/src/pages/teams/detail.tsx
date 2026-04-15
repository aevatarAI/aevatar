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
import {
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
  buildPlatformServicesHref,
} from "@/shared/navigation/platformRoutes";
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
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import type { StudioWorkflowDocument } from "@/shared/studio/models";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  TeamActionRail,
  TeamDetailEmptyState,
  TeamDetailShell,
  type TeamTabOption,
} from "./components/TeamDetailChrome";
import {
  DetailPill,
  FactLine,
  factValueFontFamily,
  SignalCard,
} from "./components/TeamDetailPrimitives";
import TeamAdvancedTab from "./tabs/TeamAdvancedTab";
import TeamAssetsTab, { teamAssetIcons } from "./tabs/TeamAssetsTab";
import TeamBindingsTab from "./tabs/TeamBindingsTab";
import TeamEventsTab from "./tabs/TeamEventsTab";
import TeamMembersTab from "./tabs/TeamMembersTab";
import TeamOverviewTab from "./tabs/TeamOverviewTab";
import TeamTopologyTab from "./tabs/TeamTopologyTab";
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

type TopologyDetailRow = {
  badge: string;
  label: string;
  note: string;
  noteMonospace?: boolean;
  noteRows?: number;
  value: string;
  valueMonospace?: boolean;
  valueRows?: number;
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
  const compacted = segment.split(":").pop() || segment;
  return compacted.length > 24
    ? `${compacted.slice(0, 12)}…${compacted.slice(-8)}`
    : compacted;
}

function formatTeamTabLabel(tab: TeamDetailTab): string {
  switch (tab) {
    case "topology":
      return "事件拓扑";
    case "events":
      return "事件流";
    case "members":
      return "团队成员";
    case "bindings":
      return "Bindings";
    case "assets":
      return "Assets";
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

function formatPlaybackSummary(summary: string): string {
  const normalized = trimText(summary);
  switch (normalized) {
    case "No run audit is available for the current team activity.":
      return "当前还没有可见的运行审计记录。";
    case "No event timeline is available for the current team activity.":
      return "当前还没有可见的事件时间线。";
    case "Timeline reconstructed from the latest visible run steps.":
      return "当前事件流是根据最近一次可见运行步骤整理的。";
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
  const currentPlatformService =
    focusedOperationalUnit?.matchedService || lens.currentService || servicesQuery.data?.[0] || null;
  const platformRouteIdentity = React.useMemo(
    () => ({
      tenantId: trimText(currentPlatformService?.tenantId) || scopeId,
      appId: trimText(currentPlatformService?.appId) || "default",
      namespace: trimText(currentPlatformService?.namespace) || "default",
      serviceId: runtimeServiceId,
    }),
    [currentPlatformService?.appId, currentPlatformService?.namespace, currentPlatformService?.tenantId, runtimeServiceId, scopeId],
  );

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
  const currentServiceReference =
    trimText(runtimeServiceId) ||
    (currentServiceKey !== "--" ? currentServiceKey : "");
  const currentServiceCardCaption = runtimeServiceId
    ? `serviceId · ${runtimeServiceId}`
    : currentServiceKey !== "--" && currentServiceKey !== currentServiceFriendly
      ? `serviceKey · ${compactId(currentServiceKey)}`
      : "当前还没有更多服务标识";
  const currentServiceCardTooltip = runtimeServiceId
    ? `serviceId · ${runtimeServiceId}`
    : currentServiceKey !== "--" && currentServiceKey !== currentServiceFriendly
      ? `serviceKey · ${currentServiceKey}`
      : "当前还没有更多服务标识";
  const currentRunCardCaption = activeRunId
    ? `runId · ${compactId(activeRunId)}`
    : "当前还没有可见 run";
  const currentRunCardTooltip = activeRunId
    ? `runId · ${activeRunId}`
    : "当前还没有可见 run";
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
        label: "默认绑定",
        note: runtimeServiceId || currentServiceKey || "--",
        value: currentServiceFriendly !== "--" ? currentServiceFriendly : "当前还没有主服务入口",
      },
      {
        badgeText: `${currentEndpointCount} 个入口`,
        badgeTone: currentEndpointCount > 0 ? ("info" as const) : ("neutral" as const),
        label: "Endpoint 暴露",
        note: `${currentPolicyCount} 条策略`,
        value:
          currentEndpointCount > 0
            ? `${currentEndpointCount} 个 endpoint 已暴露`
            : "当前还没有可见 endpoint 暴露",
      },
    ];
  }, [
    currentEndpointCount,
    currentPolicyCount,
    currentServiceFriendly,
    currentServiceKey,
    integrations.connectorCount,
    integrations.runtimeBaseUrl,
    integrations.runtimeHostLabel,
    integrations.workspaceSummary,
    runtimeServiceId,
    selectedConnector,
  ]);
  const connectorSummaryCards = React.useMemo(
    () => [
      {
        caption: runtimeServiceId || currentServiceKey || "--",
        icon: <DeploymentUnitOutlined />,
        label: "默认绑定",
        value: currentServiceFriendly,
      },
      {
        caption: `已绑定 ${integrations.linkedConnectorCount} 个 · 工作区可见 ${integrations.items.length} 个`,
        icon: <BranchesOutlined />,
        label: "连接能力",
        value: enabledConnectorCount,
      },
      {
        caption:
          connectorHighlights.length > 0
            ? connectorHighlights.join("、")
            : "当前 workflow 还没有显式引用连接器",
        label: "团队会用到",
        value:
          integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个连接器`
            : "尚未显式引用",
      },
      {
        caption: currentServiceFriendly !== "--" ? currentServiceFriendly : "等待服务入口",
        icon: <DeploymentUnitOutlined />,
        label: "治理摘要",
        value: `${currentEndpointCount} / ${currentPolicyCount}`,
      },
    ],
    [
      connectorHighlights,
      currentEndpointCount,
      currentPolicyCount,
      currentServiceFriendly,
      currentServiceKey,
      enabledConnectorCount,
      integrations.items.length,
      integrations.linkedConnectorCount,
      runtimeServiceId,
    ],
  );
  const connectorCatalogCards = React.useMemo(
    () =>
      integrations.items.map((connector) => ({
        availabilityLabel: formatConnectorEnabledLabel(connector.enabled),
        availabilityStyle: resolveTonePillStyle(
          token,
          connector.enabled ? "success" : "neutral",
        ),
        buttonStyle: resolveSelectionCardButtonStyle(
          token,
          connector.key === selectedConnector?.key,
        ),
        key: connector.key,
        name: connector.name,
        summary: connector.summary,
        typeLabel: formatConnectorTypeLabel(connector.type),
        typeStyle: resolveTonePillStyle(token, "info"),
        usageLabel:
          connector.usedByRoles.length > 0
            ? `${connector.usedByRoles.length} 个角色在用`
            : "团队未显式引用",
        usageStyle: resolveTonePillStyle(
          token,
          connector.usedByRoles.length > 0 ? "info" : "neutral",
        ),
        usageSummary:
          connector.usedByRoles.length > 0
            ? `当前团队会用到：${connector.usedByRoles.join("、")}`
            : "当前团队还没有显式引用这个连接器。",
      })),
    [integrations.items, selectedConnector?.key, token],
  );
  const connectorDetailRows = React.useMemo(
    () =>
      selectedConnectorRows.map((row) => ({
        badgeStyle: resolveTonePillStyle(token, row.badgeTone),
        badgeText: row.badgeText,
        label: row.label,
        note: row.note,
        value: row.value,
      })),
    [selectedConnectorRows, token],
  );
  const connectorsEmptyDescription =
    "一旦 scope binding、连接器目录或治理策略可见，这里会自动展开成 Bindings 视图。";
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
  const configurationAdjustmentRows = React.useMemo(
    () => [
      {
        badge: formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
        label: "流程定义",
        note: activeWorkflowId ? `workflowId: ${activeWorkflowId}` : "当前还没有 workflowId",
        value: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
      },
      {
        badge: currentDeploymentFriendly,
        label: "服务映射",
        note:
          currentVersionFriendly !== "--"
            ? `${currentVersionFriendly} · ${currentServiceFriendly}`
            : currentServiceFriendly,
        value: currentServiceFriendly,
      },
      {
        badge:
          integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个已引用`
            : "未显式引用",
        label: "连接器引用",
        note:
          connectorHighlights.length > 0
            ? connectorHighlights.join("、")
            : "当前 workflow 还没有显式引用连接器",
        value:
          integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个连接器`
            : "当前没有显式连接器引用",
      },
    ],
    [
      activeWorkflowId,
      connectorHighlights,
      currentDeploymentFriendly,
      currentServiceFriendly,
      currentVersionFriendly,
      integrations.linkedConnectorCount,
      lens.activeRevision?.implementationKind,
      teamTitle,
      workflowNameValue,
    ],
  );
  const advancedSummaryCards = React.useMemo(
    () => [
      {
        caption: activeWorkflowId || "--",
        captionMonospace: true,
        label: "团队流程",
        value: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
      },
      {
        caption:
          currentServiceFriendly !== "--"
            ? `当前会落到 ${currentServiceFriendly}`
            : "当前还没有匹配到主服务入口",
        label: "绑定方式",
        value: formatCompositionKind(lens.activeRevision?.implementationKind || "runtime"),
      },
      {
        caption: currentDeploymentId,
        captionMonospace: true,
        label: "部署记录",
        value: currentDeploymentFriendly,
      },
      {
        caption:
          connectorHighlights.length > 0
            ? connectorHighlights.join("、")
            : "当前 workflow 还没有显式引用连接器",
        label: "连接器引用",
        value:
          integrations.linkedConnectorCount > 0
            ? `${integrations.linkedConnectorCount} 个已引用`
            : "未显式引用",
      },
    ],
    [
      activeWorkflowId,
      connectorHighlights,
      currentDeploymentFriendly,
      currentDeploymentId,
      currentServiceFriendly,
      integrations.linkedConnectorCount,
      lens.activeRevision?.implementationKind,
      teamTitle,
      workflowNameValue,
    ],
  );
  const advancedTeamImpactSummary =
    integrations.linkedConnectorCount > 0
      ? ` ${integrations.linkedConnectorCount} 个已绑定连接器`
      : " 当前还没有显式绑定的连接器";
  const workflowAssetRows = React.useMemo(
    () =>
      (workflowsQuery.data ?? []).map((workflow) => {
        const isCurrent =
          trimText(workflow.workflowId) === trimText(activeWorkflowId) ||
          trimText(workflow.workflowId) === trimText(activeWorkflowSummary?.workflowId);
        const statusLabel = formatFriendlyStatus(workflow.deploymentStatus || "draft");
        return {
          actionLabel: "进入 Workflow Studio",
          badgeLabel: isCurrent ? "当前团队流程" : statusLabel,
          badgeStyle: resolveTonePillStyle(token, isCurrent ? "success" : "neutral"),
          buttonStyle: resolveSelectionCardButtonStyle(token, isCurrent),
          key: workflow.workflowId,
          primaryMetaLabel: "Revision",
          primaryMetaValue: workflow.activeRevisionId || "n/a",
          secondaryMetaLabel: "Entrypoint",
          secondaryMetaValue: workflow.serviceKey || "未绑定",
          summary: workflow.displayName
            ? `${workflow.displayName} 已绑定到 ${workflow.serviceKey || "待发布入口"}`
            : "当前 workflow 已准备好进入 Studio。",
          subtitle: workflow.workflowName || "Workflow capability",
          title: workflow.displayName || workflow.workflowId,
        };
      }),
    [activeWorkflowId, activeWorkflowSummary?.workflowId, token, workflowsQuery.data],
  );
  const scriptAssetRows = React.useMemo(
    () =>
      (scriptsQuery.data ?? []).map((script) => {
        const isCurrent =
          trimText(script.scriptId) === trimText(lens.activeRevision?.scriptId);
        return {
          actionLabel: "进入 Script Studio",
          badgeLabel: isCurrent ? "当前绑定脚本" : script.activeRevision ? "已激活" : "草稿",
          badgeStyle: resolveTonePillStyle(token, isCurrent ? "success" : "neutral"),
          buttonStyle: resolveSelectionCardButtonStyle(token, isCurrent),
          key: script.scriptId,
          primaryMetaLabel: "Revision",
          primaryMetaValue: trimText(script.activeRevision) || "n/a",
          secondaryMetaLabel: "Catalog actor",
          secondaryMetaValue: trimText(script.catalogActorId) || "n/a",
          summary:
            trimText(script.activeSourceHash).length > 0
              ? `当前脚本 revision 已落在 ${trimText(script.activeSourceHash)}`
              : "当前脚本已经进入 Team 资产目录。",
          subtitle: "Script capability",
          title: script.scriptId || "未命名 Script",
        };
      }),
    [lens.activeRevision?.scriptId, scriptsQuery.data, token],
  );
  const assetSummaryCards = React.useMemo(
    () => [
      {
        caption: activeWorkflowId || "--",
        icon: teamAssetIcons.workflows,
        label: "Workflow 资产",
        value: workflowsQuery.data?.length ?? 0,
      },
      {
        caption: trimText(lens.activeRevision?.scriptId) || "--",
        icon: teamAssetIcons.scripts,
        label: "Script 资产",
        value: scriptsQuery.data?.length ?? 0,
      },
      {
        caption: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
        icon: teamAssetIcons.deployment,
        label: "当前主流程",
        value: activeWorkflowSummary?.displayName || workflowNameValue,
      },
      {
        caption: currentServiceFriendly !== "--" ? currentServiceFriendly : "待绑定",
        icon: <DeploymentUnitOutlined />,
        label: "服务入口",
        value: runtimeServiceId || "--",
      },
    ],
    [
      activeWorkflowId,
      activeWorkflowSummary?.displayName,
      currentServiceFriendly,
      lens.activeRevision?.scriptId,
      runtimeServiceId,
      scriptsQuery.data?.length,
      teamTitle,
      workflowNameValue,
      workflowsQuery.data?.length,
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
  const selectedTopologyRows = React.useMemo<TopologyDetailRow[]>(() => {
    if (!selectedTopologyEntity) {
      return [];
    }

    if (selectedTopologyEntity.kind === "service") {
      return [
        {
          badge: "服务",
          label: "主服务",
          note: runtimeServiceId || currentServiceKey || "--",
          noteMonospace: true,
          noteRows: 1,
          value: currentServiceFriendly,
          valueMonospace: false,
        },
        {
          badge: "部署",
          label: "部署状态",
          note: currentDeploymentId,
          noteMonospace: true,
          noteRows: 1,
          value: currentDeploymentFriendly,
          valueMonospace: false,
        },
        {
          badge: `${currentEndpointCount} 个入口`,
          label: "服务能力",
          note: `${currentPolicyCount} 条策略`,
          value: `${currentEndpointCount} 个入口`,
          valueMonospace: false,
        },
        {
          badge: `${selectedTopologyInboundCount} 条入边`,
          label: "上游来自",
          note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
          value: selectedTopologyInboundSummary,
          valueMonospace: false,
        },
        {
          badge: `${selectedTopologyOutboundCount} 条出边`,
          label: "下游连接",
          note: "当前团队通过这个入口继续流向工具或下游能力",
          value: selectedTopologyOutboundSummary,
          valueMonospace: false,
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
          valueMonospace: false,
        },
        {
          badge: formatConnectorEnabledLabel(Boolean(connector?.enabled)),
          label: "团队使用",
          note: connector?.usedByRoles.join("、") || "当前团队还没有引用它",
          value:
            connector?.usedByRoles.length
              ? `${connector.usedByRoles.length} 个角色`
              : "0 个角色",
          valueMonospace: false,
        },
        {
          badge: `${selectedTopologyInboundCount} 条入边`,
          label: "上游来自",
          note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
          value: selectedTopologyInboundSummary,
          valueMonospace: false,
        },
        {
          badge: `${selectedTopologyOutboundCount} 条出边`,
          label: "下游连接",
          note: "当前节点来自团队配置推导，不直接代表一次实时运行",
          value: selectedTopologyOutboundSummary,
          valueMonospace: false,
        },
      ];
    }

    return [
      {
        badge: formatTopologyNodeKindLabel(selectedTopologyEntity.kind),
        label: "角色定位",
        note: selectedTopologyEntity.id,
        noteMonospace: true,
        noteRows: 1,
        value: selectedTopologyEntity.title,
        valueMonospace: false,
      },
      {
        badge: selectedTopologyStep
          ? formatStepTypeLabel(selectedTopologyStep.stepType)
          : "最近事件",
        label: "最近一步",
        note: selectedTopologyLatestStepNote,
        value: selectedTopologyLatestStepLabel,
        valueMonospace: false,
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
        valueMonospace: false,
      },
      {
        badge: `${selectedTopologyInboundCount} 条入边`,
        label: "上游来自",
        note: `当前深度 ${selectedTopologyDepth} · 出边 ${selectedTopologyOutboundCount}`,
        value: selectedTopologyInboundSummary,
        valueMonospace: false,
      },
      {
        badge: `${selectedTopologyOutboundCount} 条出边`,
        label: "下游流向",
        note:
          selectedTopologyEntity.id === topologyGraph.rootActorId
            ? "这是当前焦点路径的起点"
            : "这是围绕当前焦点展开的协作节点",
        value: selectedTopologyOutboundSummary,
        valueMonospace: false,
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
  const topologyDetailRows = React.useMemo(
    () =>
      selectedTopologyRows.map((row) => ({
        ...row,
        badgeStyle: resolveTonePillStyle(token, "neutral"),
      })),
    [selectedTopologyRows, token],
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
      badge: currentRevisionFriendly,
      badgeColor: currentRevisionStatus === "Active" ? "success" : undefined,
      key: "revisionId",
      label: "当前版本",
      note:
        currentRevisionId !== "--"
          ? `revisionId · ${compactId(currentRevisionId)}`
          : "当前还没有可见版本标识",
      noteMonospace: false,
      noteTooltip:
        currentRevisionId !== "--"
          ? `revisionId · ${currentRevisionId}`
          : "当前还没有可见版本标识",
      value: currentRevisionFriendly,
    },
    {
      badge: currentServiceFriendly,
      badgeColor: runtimeServiceId ? "success" : undefined,
      key: "serviceKey",
      label: "主服务",
      note: currentServiceReference
        ? `serviceId · ${compactId(currentServiceReference)}`
        : "当前还没有主服务标识",
      noteMonospace: false,
      noteTooltip: currentServiceReference
        ? `serviceId · ${currentServiceReference}`
        : "当前还没有主服务标识",
      value: currentServiceFriendly,
    },
    {
      badge: currentRunFriendly,
      badgeColor: currentRunStatus !== "--" ? "success" : undefined,
      key: "runId",
      label: "最近状态",
      note: activeRunId
        ? `runId · ${compactId(activeRunId)}`
        : currentActorId !== "--"
          ? `actorId · ${compactId(currentActorId)}`
          : "当前还没有可见运行身份",
      noteMonospace: false,
      noteTooltip: activeRunId
        ? `runId · ${activeRunId}`
        : currentActorId !== "--"
          ? `actorId · ${currentActorId}`
          : "当前还没有可见运行身份",
      value: currentRunFriendly,
    },
    {
      badge: currentStateVersion !== "--" ? `v${currentStateVersion}` : "--",
      key: "lastUpdatedAt",
      label: "最近更新时间",
      note: latestVisibleUpdateNote,
      noteMonospace: false,
      value: latestVisibleUpdate ? formatCompactTimestamp(latestVisibleUpdate) : "--",
    },
    {
      badge: `${integrations.linkedConnectorCount}`,
      badgeColor: integrations.linkedConnectorCount > 0 ? "success" : undefined,
      key: "bindings",
      label: "Bindings",
      note:
        connectorHighlights.length > 0
          ? connectorHighlights.join("、")
          : `catalog: ${integrations.items.length}`,
      noteMonospace: false,
      value:
        integrations.linkedConnectorCount > 0
          ? `${integrations.linkedConnectorCount} 个已绑定`
          : "未配置",
    },
  ];
  const overviewCompositionRows = React.useMemo(
    () =>
      compositionDisplayRows.map((row) => ({
        key: row.key,
        kindLabel: formatCompositionKind(row.kind),
        kindStyle: resolveCompositionKindPillStyle(token, row.kind),
        name: row.name,
        summary: row.summary,
      })),
    [compositionDisplayRows, token],
  );
  const overviewRuntimeSummaryRows = runtimeSummaryRows.map((row) => ({
    ...row,
    badgeStyle:
      row.badgeColor === "success"
        ? resolveTonePillStyle(token, "success")
        : resolveStatusPillStyle(token, row.badge),
  }));
  const displayedRunId = lens.currentRun?.runId || preferredRunId || "";
  const runSwitchOptions = React.useMemo(
    () =>
      (runsQuery.data?.runs ?? []).slice(0, 4).map((run) => ({
        label: `${formatCompactTimestamp(run.lastUpdatedAt)} · ${formatFriendlyStatus(run.completionStatus)}`,
        runId: run.runId,
      })),
    [runsQuery.data?.runs],
  );
  const runSwitchDisplayOptions = React.useMemo(
    () =>
      runSwitchOptions.map((option) => ({
        ...option,
        buttonStyle: resolveSegmentedButtonStyle(
          token,
          option.runId === displayedRunId,
        ),
      })),
    [displayedRunId, runSwitchOptions, token],
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
  const eventStreamDisplayRows = React.useMemo(
    () =>
      eventStreamRows.map((row) => ({
        ...row,
        stageStyle: resolveTonePillStyle(token, row.stageTone),
      })),
    [eventStreamRows, token],
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
  const memberMappingDisplayRows = React.useMemo(
    () =>
      memberMappingRows.map((row) => ({
        ...row,
        statusStyle: resolveTonePillStyle(token, row.statusTone),
      })),
    [memberMappingRows, token],
  );
  const runtimeIdentityRows = React.useMemo(
    () =>
      lens.members.map((member) => {
        const graphNode =
          lens.graph.nodes.find((node) => node.actorId === member.actorId) ?? null;
        const implementationKind = formatCompositionKind(
          lens.activeRevision?.implementationKind || "runtime",
        );
        return {
          actorId: member.actorId,
          implementationKind: trimText(member.actorType)
            ? `${implementationKind} · ${trimText(member.actorType)}`
            : implementationKind,
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
          serviceId: runtimeServiceId || currentServiceKey || "--",
          statusLabel: formatMemberPresenceLabel(member.isFocused ? "focus" : "visible"),
          statusTone: resolveMemberPresenceTone(member.isFocused ? "focus" : "visible"),
        };
      }),
    [
      actorLabelMap,
      currentServiceKey,
      lens.activeRevision?.implementationKind,
      lens.graph.nodes,
      lens.members,
      runtimeServiceId,
    ],
  );
  const memberCompositionRows = React.useMemo(
    () =>
      teamCompositionRows.map((row) => ({
        key: row.key,
        kindLabel: formatCompositionKind(row.kind),
        kindStyle: resolveCompositionKindPillStyle(token, row.kind),
        name: row.name,
        summary: row.summary,
      })),
    [teamCompositionRows, token],
  );
  const memberIdentityRows = React.useMemo(
    () =>
      runtimeIdentityRows.map((row) => ({
        actorId: row.actorId,
        cardStyle: resolveSelectionCardButtonStyle(
          token,
          row.actorId === effectiveActorId,
        ),
        implementationKind: row.implementationKind,
        key: row.key,
        member: row.member,
        note: row.note,
        relationLabel: row.relationLabel,
        serviceId: row.serviceId,
        statusLabel: row.statusLabel,
        statusStyle: resolveTonePillStyle(token, row.statusTone),
      })),
    [effectiveActorId, runtimeIdentityRows, token],
  );

  const tabOptions: TeamTabOption[] = [
    { label: "概览", value: "overview" },
    { label: "事件拓扑", value: "topology" },
    { label: "事件流", value: "events" },
    { label: "团队成员", value: "members" },
    { label: "Bindings", value: "bindings" },
    { label: "Assets", value: "assets" },
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
  const conversationActionLabel = lens.playback.currentRunId ? "本次对话" : "运行记录";
  const serviceMappingActionLabel = "服务映射";
  const teamBuilderActionLabel = "高级编辑";
  const handleOpenTeamsList = React.useCallback(() => {
    history.push(teamsListHref);
  }, [teamsListHref]);

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
  const handleOpenServices = React.useCallback(() => {
    history.push(buildPlatformServicesHref(platformRouteIdentity));
  }, [platformRouteIdentity]);
  const handleOpenGovernance = React.useCallback(() => {
    history.push(
      buildPlatformGovernanceHref({
        ...platformRouteIdentity,
        revisionId: currentRevisionId !== "--" ? currentRevisionId : undefined,
        view: "bindings",
      }),
    );
  }, [currentRevisionId, platformRouteIdentity]);
  const handleOpenDeployments = React.useCallback(() => {
    history.push(
      buildPlatformDeploymentsHref({
        ...platformRouteIdentity,
        deploymentId: currentDeploymentId !== "--" ? currentDeploymentId : undefined,
      }),
    );
  }, [currentDeploymentId, platformRouteIdentity]);
  const handleOpenWorkflowAsset = React.useCallback(
    (workflowId: string) => {
      history.push(
        buildStudioWorkflowEditorRoute({
          scopeId,
          workflowId,
        }),
      );
    },
    [scopeId],
  );
  const handleOpenScriptAsset = React.useCallback(
    (scriptId: string) => {
      history.push(
        buildStudioScriptsWorkspaceRoute({
          scopeId,
          scriptId,
        }),
      );
    },
    [scopeId],
  );

  const renderOverviewTab = () => {
    return (
      <TeamOverviewTab
        compositionRows={overviewCompositionRows}
        currentDeploymentPillStyle={resolveStatusPillStyle(token, currentDeploymentStatus)}
        currentDeploymentPillText={currentDeploymentPillText}
        currentHeaderStatusFriendly={currentHeaderStatusFriendly}
        currentHeaderStatusStyle={resolveStatusPillStyle(token, currentHeaderStatus)}
        currentRunCardCaption={currentRunCardCaption}
        currentRunCardTooltip={currentRunCardTooltip}
        currentRunFriendly={currentRunFriendly}
        currentRunPillStyle={resolveStatusPillStyle(token, currentRunStatus)}
        currentRunPillText={currentRunPillText}
        currentServiceCardCaption={currentServiceCardCaption}
        currentServiceCardTooltip={currentServiceCardTooltip}
        currentServiceFriendly={currentServiceFriendly}
        currentServicePillStyle={{
          background: token.colorInfoBg,
          border: `1px solid ${token.colorInfoBorder}`,
          color: token.colorInfo,
        }}
        currentServicePillText={currentServicePillText}
        latestVisibleUpdateLabel={formatCompactTimestamp(latestVisibleUpdate)}
        latestVisibleUpdateNote={latestVisibleUpdateNote}
        runtimeSummaryRows={overviewRuntimeSummaryRows}
      />
    );
  };

  const renderTopologyTab = () => {
    return (
      <TeamTopologyTab
        graphDepth={graphDepth}
        graphEdgeCount={topologyGraph.edges.length}
        graphFocusLabel={`${formatTopologyDepthLabel(graphDepth)}视角 · 焦点 ${compactId(effectiveActorId)}`}
        graphNodeCount={topologyGraph.nodes.length}
        isError={actorGraphQuery.isError}
        isLoading={actorGraphQuery.isLoading}
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
        onOpenPlatformTopology={handleOpenServiceMapping}
        onSetGraphDepth={setGraphDepth}
        openPlatformTopologyButtonStyle={{
          borderRadius: 16,
          height: 40,
          paddingInline: 18,
        }}
        provenanceLabel={graphProvenance.label}
        provenanceStyle={resolveObservationPillStyle(token, graphProvenance.status)}
        selectedEntityBadgeLabel={selectedTopologyEntity?.badgeText}
        selectedEntityBadgeStyle={
          selectedTopologyEntity
            ? resolveTonePillStyle(token, selectedTopologyEntity.badgeTone)
            : undefined
        }
        selectedEntityDetailRows={topologyDetailRows}
        selectedEntityEmpty={!selectedTopologyEntity}
        selectedEntityKindLabel={
          selectedTopologyEntity
            ? formatTopologyNodeKindLabel(selectedTopologyEntity.kind)
            : undefined
        }
        selectedEntityKindStyle={
          selectedTopologyEntity
            ? resolveTonePillStyle(token, "neutral")
            : undefined
        }
        selectedEntitySummary={selectedTopologyEntity?.summary}
        selectedEntityTitle={selectedTopologyEntity?.title}
        selectedFocusReason={
          selectedFocusReason || "围绕当前焦点成员展开团队消息路径。点击左侧节点即可切换视角。"
        }
        selectedNodeId={selectedTopologyNodeId || effectiveActorId}
        topologyEdges={topologyGraph.edges}
        topologyNodes={topologyGraph.nodes}
      />
    );
  };

  const renderEventsTab = () => {
    return (
      <TeamEventsTab
        activeRunLabel={lens.currentRun?.runId || "当前还没有可见运行"}
        activeRunMetaLabel={activeRunId ? `run · ${activeRunId}` : "暂无当前 run"}
        currentRunStatusLabel={
          lens.currentRun?.completionStatus
            ? formatFriendlyStatus(lens.currentRun.completionStatus)
            : undefined
        }
        currentRunStatusStyle={
          lens.currentRun?.completionStatus
            ? resolveStatusPillStyle(token, lens.currentRun.completionStatus)
            : undefined
        }
        eventRows={eventStreamDisplayRows}
        isRunsError={runsQuery.isError}
        isRunsLoading={runsQuery.isLoading}
        memberMappingRows={memberMappingDisplayRows}
        onOpenAudit={() =>
          handleOpenPlaybackActor(lens.currentRun?.actorId, activeRunId)
        }
        onSelectRun={handleSelectRun}
        openAuditButtonStyle={resolveActionButtonStyle(token)}
        playbackSummary={formatPlaybackSummary(lens.playback.summary)}
        provenanceLabel={playbackProvenance.label}
        provenanceStyle={resolveObservationPillStyle(token, playbackProvenance.status)}
        runSwitchOptions={runSwitchDisplayOptions}
        showOpenAudit={Boolean(activeRunId)}
      />
    );
  };

  const renderMembersTab = () => {
    return (
      <TeamMembersTab
        compositionRows={memberCompositionRows}
        identityRows={memberIdentityRows}
        onOpenRuntimeExplorer={handleOpenServiceMapping}
        onOpenServices={handleOpenServices}
        onSelectActor={setSelectedActorId}
      />
    );
  };

  const renderBindingsTab = () => {
    return (
      <TeamBindingsTab
        catalogCards={connectorCatalogCards}
        emptyDescription={connectorsEmptyDescription}
        onOpenDeployments={handleOpenDeployments}
        onOpenGovernance={handleOpenGovernance}
        onOpenServices={handleOpenServices}
        onSelectBinding={setSelectedConnectorKey}
        provenanceLabel={integrationsProvenance.label}
        provenanceStyle={resolveObservationPillStyle(token, integrationsProvenance.status)}
        selectedBindingDetailRows={connectorDetailRows}
        selectedBindingEmpty={!selectedConnector}
        selectedBindingStatusLabel={
          selectedConnector
            ? formatConnectorEnabledLabel(selectedConnector.enabled)
            : ""
        }
        selectedBindingStatusStyle={resolveTonePillStyle(
          token,
          selectedConnector?.enabled ? "success" : "neutral",
        )}
        selectedBindingName={selectedConnector?.name || ""}
        selectedBindingSummary={selectedConnector?.summary || ""}
        summaryCards={connectorSummaryCards}
      />
    );
  };

  const renderAssetsTab = () => {
    return (
      <TeamAssetsTab
        onOpenScriptAsset={handleOpenScriptAsset}
        onOpenScriptsWorkspace={() =>
          history.push(
            buildStudioScriptsWorkspaceRoute({
              scopeId,
            }),
          )
        }
        onOpenWorkflowAsset={handleOpenWorkflowAsset}
        onOpenWorkflowWorkspace={() =>
          history.push(
            buildStudioWorkflowWorkspaceRoute({
              scopeId,
            }),
          )
        }
        scriptRows={scriptAssetRows}
        summaryCards={assetSummaryCards}
        workflowRows={workflowAssetRows}
      />
    );
  };

  const renderAdvancedTab = () => {
    return (
      <TeamAdvancedTab
        adjustmentBadgeStyle={resolveTonePillStyle(token, "neutral")}
        configurationAdjustmentRows={configurationAdjustmentRows}
        configurationDetailRows={configurationDetailRows}
        conversationActionLabel={conversationActionLabel}
        currentDeploymentBadgeStyle={resolveStatusPillStyle(token, currentDeploymentStatus)}
        currentDeploymentFriendly={currentDeploymentFriendly}
        currentServiceFriendly={currentServiceFriendly}
        currentVersionFriendly={currentVersionFriendly}
        onOpenConversation={handleOpenConversation}
        onOpenServiceMapping={handleOpenServiceMapping}
        onOpenTeamBuilder={() => history.push(teamBuilderRoute)}
        primaryActionButtonStyle={resolveActionButtonStyle(token, "primary")}
        secondaryActionButtonStyle={resolveActionButtonStyle(token)}
        serviceMappingActionLabel={serviceMappingActionLabel}
        summaryCards={advancedSummaryCards}
        teamBuilderActionLabel={teamBuilderActionLabel}
        teamImpactSummary={advancedTeamImpactSummary}
      />
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
    case "bindings":
      tabContent = renderBindingsTab();
      break;
    case "assets":
      tabContent = renderAssetsTab();
      break;
    case "advanced":
      tabContent = renderAdvancedTab();
      break;
    default:
      tabContent = renderOverviewTab();
      break;
  }

  if (!scopeId) {
    return <TeamDetailEmptyState />;
  }

  return (
    <TeamDetailShell
      actionRail={
        <TeamActionRail
          conversationActionLabel={conversationActionLabel}
          onOpenConversation={handleOpenConversation}
          onOpenServiceMapping={handleOpenServiceMapping}
          onOpenTeamBuilder={() => history.push(teamBuilderRoute)}
          serviceMappingActionLabel={serviceMappingActionLabel}
          teamBuilderActionLabel={teamBuilderActionLabel}
        />
      }
      activeTab={activeTab}
      activeTabLabel={formatTeamTabLabel(activeTab)}
      initialLoading={initialLoading}
      onOpenTeamsList={handleOpenTeamsList}
      onSelectTab={pushTeamTab}
      statusBadge={
        <DetailPill
          style={resolveStatusPillStyle(token, currentHeaderStatus)}
          text={currentHeaderStatusFriendly}
        />
      }
      tabOptions={tabOptions}
      teamTitle={teamTitle}
      teamsListHref={teamsListHref}
    >
      {tabContent}
    </TeamDetailShell>
  );
};

export default TeamDetailPage;
