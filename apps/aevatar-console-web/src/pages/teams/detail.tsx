import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from "@aevatar-react-sdk/types";
import type { Edge, Node } from "@xyflow/react";
import { Input, Modal, Space, Typography, message, theme } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import React from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import {
  formatCompactDateTime,
  formatTimeOnly,
} from "@/shared/datetime/dateTime";
import { buildActorGraphElements } from "@/shared/graphs/buildGraphElements";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
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
  buildRuntimeMissionControlHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import { isStudioApiStatus, studioApi } from "@/shared/studio/api";
import {
  buildStudioWorkflowMemberKey,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
  resolveStudioMemberRouteKey,
} from "@/shared/studio/navigation";
import {
  formatStudioMemberLifecycleStage,
  formatStudioTeamLifecycleStage,
  type StudioWorkflowDocument,
} from "@/shared/studio/models";
import type { StudioTeamSummary } from "@/shared/studio/models";
import { AevatarCompactText } from "@/shared/ui/compactText";
import { describeError } from "@/shared/ui/errorText";
import {
  TeamActionRail,
  TeamDetailEmptyState,
  TeamDetailShell,
  type TeamTabOption,
} from "./components/TeamDetailChrome";
import {
  DetailPill,
  factValueFontFamily,
} from "./components/TeamDetailPrimitives";
import TeamEventsTab from "./tabs/TeamEventsTab";
import TeamMembersTab from "./tabs/TeamMembersTab";
import TeamOverviewTab from "./tabs/TeamOverviewTab";
import TeamTopologyTab from "./tabs/TeamTopologyTab";
import { resolveWorkflowOperationalUnit } from "./workflowOperationalUnits";
import {
  deriveTeamIntegrationsSummary,
  deriveTeamWorkflowRoleBindings,
} from "./runtime/teamIntegrations";
import type {
  TeamHealthStatus,
  TeamHealthTone,
  TeamPlaybackSummary,
} from "./runtime/teamRuntimeLens";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

type ObservationStatus = "live" | "delayed" | "partial" | "unavailable";

const teamProjectionRetryLimit = 5;
const teamProjectionRetryBaseMs = 500;
const teamProjectionRetryMaxMs = 3_000;

function isProjectionSyncing404(error: unknown): boolean {
  return isStudioApiStatus(error, 404);
}

function projectionRetryDelay(attemptIndex: number): number {
  return Math.min(
    teamProjectionRetryBaseMs * 2 ** attemptIndex,
    teamProjectionRetryMaxMs,
  );
}

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

function resolveTeamHeading(input: {
  displayName?: string | null;
  lensTitle?: string | null;
  scopeId: string | null | undefined;
  workflowId?: string | null;
  workflowName?: string | null;
}): {
  metaScopeId?: string;
  title: string;
} {
  const normalizedScopeId = trimText(input.scopeId);
  const normalizedDisplayName = trimText(input.displayName);
  const normalizedWorkflowId = trimText(input.workflowId);
  const normalizedWorkflowName = trimText(input.workflowName);
  const normalizedLensTitle = trimText(input.lensTitle);

  if (
    normalizedDisplayName &&
    normalizedDisplayName !== normalizedWorkflowId
  ) {
    return {
      title: normalizedDisplayName,
    };
  }

  if (
    normalizedWorkflowName &&
    normalizedWorkflowName !== normalizedWorkflowId
  ) {
    return {
      title: normalizedWorkflowName,
    };
  }

  const genericLensTitle =
    normalizedScopeId ? `Team ${normalizedScopeId}` : "";
  if (normalizedLensTitle === "当前团队") {
    return {
      metaScopeId: normalizedScopeId || undefined,
      title: normalizedLensTitle,
    };
  }

  if (
    normalizedLensTitle &&
    normalizedLensTitle !== normalizedScopeId &&
    normalizedLensTitle !== genericLensTitle
  ) {
    return {
      title: normalizedLensTitle,
    };
  }

  return {
    metaScopeId: normalizedScopeId || undefined,
    title: "团队详情",
  };
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
    case "Focused on the currently selected service revision actor because no active run was selected.":
      return "当前还没有选中运行，所以先对齐到当前所选服务版本对应的成员。";
    case "Focused on the currently selected service actor because no stronger runtime signal was available.":
      return "当前还没有更强的运行信号，所以先对齐到当前所选服务对应的成员。";
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
    const current = queue.shift();
    if (!current) {
      continue;
    }
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

function formatTeamHealthStatusLabel(status: TeamHealthStatus): string {
  switch (status) {
    case "healthy":
      return "可信运行";
    case "attention":
      return "需要核验";
    case "degraded":
      return "运行降级";
    case "blocked":
      return "等待人工";
    case "human-overridden":
      return "人工接管";
    default:
      return "状态未知";
  }
}

function formatTeamHealthActionLabel(status: TeamHealthStatus): string {
  switch (status) {
    case "healthy":
      return "可继续观察或小步调整";
    case "attention":
      return "变更前先核验信号";
    case "degraded":
      return "先修复运行异常";
    case "blocked":
      return "需要人工处理后继续";
    case "human-overridden":
      return "人工介入正在影响判断";
    default:
      return "等待更多运行事实";
  }
}

function resolveTeamHealthToneStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  tone: TeamHealthTone,
): React.CSSProperties {
  switch (tone) {
    case "success":
      return resolveTonePillStyle(token, "success");
    case "warning":
      return resolveTonePillStyle(token, "warning");
    case "error":
      return resolveTonePillStyle(token, "danger");
    case "info":
      return resolveTonePillStyle(token, "info");
    default:
      return resolveTonePillStyle(token, "neutral");
  }
}

function formatTeamHealthDetail(detail: string): string {
  const normalized = trimText(detail);
  if (!normalized) {
    return "--";
  }

  if (normalized === "A human-in-the-loop step is visible in the current run.") {
    return "当前运行可见人工介入步骤。";
  }

  if (normalized.startsWith("Service deployment is ")) {
    return `服务部署状态：${normalized.replace("Service deployment is ", "").replace(/\.$/, "")}。`;
  }

  if (normalized === "No recent team activity is available to verify the active deployment.") {
    return "当前还没有近期团队运行来证明 active deployment 的健康状态。";
  }

  if (normalized.startsWith("Current run ")) {
    return normalized
      .replace(/^Current run /, "当前运行 ")
      .replace(" is ", " 状态为 ");
  }

  if (normalized.startsWith("Latest error: ")) {
    return normalized.replace("Latest error: ", "最近错误：");
  }

  if (normalized.startsWith("Current run status is ")) {
    return normalized.replace("Current run status is ", "当前运行状态为 ");
  }

  return normalized;
}

function formatPartialSignal(signal: string): string {
  switch (trimText(signal)) {
    case "Actor graph unavailable":
      return "事件拓扑暂不可用";
    case "No successful baseline run":
      return "暂无成功基线运行";
    case "No recent runs":
      return "暂无近期运行";
    default:
      return trimText(signal) || "--";
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

  if (normalized === "archived") {
    return {
      background: token.colorFillQuaternary,
      color: token.colorTextSecondary,
    };
  }

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
  return formatCompactDateTime(value, "暂无");
}

function formatClockTimestamp(value: string | null | undefined): string {
  return formatTimeOnly(value, "--");
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
  const queryClient = useQueryClient();
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => "",
  );
  const routeState = React.useMemo(() => {
    if (typeof window === "undefined") {
      return readTeamDetailRouteState("", "");
    }

    return readTeamDetailRouteState(window.location.search, window.location.pathname);
  }, [locationSnapshot]);
  const scopeId = routeState.scopeId.trim();
  const selectedTeamId = trimText(routeState.teamId);
  const hasTeamIdentity = scopeId.length > 0 && selectedTeamId.length > 0;
  const teamSummaryQueryKey = React.useMemo(
    () => ["teams", "team-summary", scopeId, selectedTeamId] as const,
    [scopeId, selectedTeamId],
  );
  const teamMembersQueryKey = React.useMemo(
    () => ["teams", "team-members", scopeId, selectedTeamId] as const,
    [scopeId, selectedTeamId],
  );
  const cachedTeamSummary = queryClient.getQueryData<StudioTeamSummary>(
    teamSummaryQueryKey,
  );
  const shouldRetryProjectionSync = Boolean(
    hasTeamIdentity && cachedTeamSummary?.teamId === selectedTeamId,
  );
  const teamsListHref = React.useMemo(
    () => buildScopeHref("/teams", { scopeId }),
    [scopeId],
  );
  const [graphDepth, setGraphDepth] = React.useState(2);
  const [preferredMemberId, setPreferredMemberId] = React.useState(routeState.memberId);
  const [preferredServiceId, setPreferredServiceId] = React.useState(
    routeState.serviceId,
  );
  const [preferredRunId, setPreferredRunId] = React.useState(routeState.runId);
  const [activeTab, setActiveTab] = React.useState<TeamDetailTab>(routeState.tab);
  const [selectedActorId, setSelectedActorId] = React.useState("");
  const [selectedTopologyNodeId, setSelectedTopologyNodeId] = React.useState("");
  const [teamEditorOpen, setTeamEditorOpen] = React.useState(false);
  const [teamArchiveOpen, setTeamArchiveOpen] = React.useState(false);
  const [teamEditorName, setTeamEditorName] = React.useState("");
  const [teamEditorDescription, setTeamEditorDescription] = React.useState("");
  const [teamEditorSaving, setTeamEditorSaving] = React.useState(false);
  const [teamArchiving, setTeamArchiving] = React.useState(false);
  const { token } = theme.useToken();

  React.useEffect(() => {
    const nextMemberId = trimText(routeState.memberId);
    const nextServiceId = trimText(routeState.serviceId);
    const nextRunId = trimText(routeState.runId);

    setPreferredMemberId((currentMemberId) =>
      trimText(currentMemberId) === nextMemberId ? currentMemberId : nextMemberId,
    );
    setPreferredServiceId((currentServiceId) =>
      trimText(currentServiceId) === nextServiceId ? currentServiceId : nextServiceId,
    );
    setPreferredRunId((currentRunId) =>
      trimText(currentRunId) === nextRunId ? currentRunId : nextRunId,
    );
    setActiveTab((currentTab) =>
      currentTab === routeState.tab ? currentTab : routeState.tab,
    );
  }, [routeState.memberId, routeState.runId, routeState.serviceId, routeState.tab]);

  const {
    actorGraphQuery,
    actorsQuery,
    currentRunAuditQuery,
    lens,
    runsQuery,
    preferredMemberSummary,
    serviceRevisionsQuery,
    scriptsQuery,
    servicesQuery,
    workflowsQuery,
  } = useTeamRuntimeLens(scopeId, {
    enabled: hasTeamIdentity,
    graphDepth,
    preferredActorId: selectedActorId || undefined,
    preferredMemberId,
    preferredRunId,
    preferredServiceId,
  });

  const workspaceSettingsQuery = useQuery({
    enabled: hasTeamIdentity,
    queryFn: () => studioApi.getWorkspaceSettings(),
    queryKey: ["teams", "workspace-settings"],
    retry: false,
  });

  const connectorCatalogQuery = useQuery({
    enabled: hasTeamIdentity,
    queryFn: () => studioApi.getConnectorCatalog(),
    queryKey: ["teams", "connector-catalog"],
    retry: false,
  });

  const teamMembersQuery = useQuery({
    enabled: hasTeamIdentity,
    queryFn: () => studioApi.listTeamMembers(scopeId, selectedTeamId),
    queryKey: teamMembersQueryKey,
    retry: (failureCount, error) =>
      shouldRetryProjectionSync &&
      isProjectionSyncing404(error) &&
      failureCount < teamProjectionRetryLimit,
    retryDelay: projectionRetryDelay,
  });
  const teamSummaryQuery = useQuery({
    enabled: hasTeamIdentity,
    queryFn: () => studioApi.getTeam(scopeId, selectedTeamId),
    queryKey: teamSummaryQueryKey,
    retry: (failureCount, error) =>
      shouldRetryProjectionSync &&
      isProjectionSyncing404(error) &&
      failureCount < teamProjectionRetryLimit,
    retryDelay: projectionRetryDelay,
  });
  const isTeamSummaryProjectionSyncing =
    shouldRetryProjectionSync &&
    ((teamSummaryQuery.failureCount > 0 &&
      isProjectionSyncing404(teamSummaryQuery.failureReason)) ||
      (teamSummaryQuery.isError && isProjectionSyncing404(teamSummaryQuery.error)));
  const isTeamMembersProjectionSyncing =
    shouldRetryProjectionSync &&
    ((teamMembersQuery.failureCount > 0 &&
      isProjectionSyncing404(teamMembersQuery.failureReason)) ||
      (teamMembersQuery.isError && isProjectionSyncing404(teamMembersQuery.error)));

  React.useEffect(() => {
    if (!teamEditorOpen || !teamSummaryQuery.data) {
      return;
    }

    setTeamEditorName(teamSummaryQuery.data.displayName);
    setTeamEditorDescription(teamSummaryQuery.data.description);
  }, [teamEditorOpen, teamSummaryQuery.data]);

  const refreshTeamAuthority = React.useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: teamSummaryQueryKey,
      }),
      queryClient.invalidateQueries({ queryKey: ["teams", "roster", scopeId] }),
    ]);
  }, [queryClient, scopeId, teamSummaryQueryKey]);

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
      hasTeamIdentity &&
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
      hasTeamIdentity &&
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
    hasTeamIdentity &&
    lens.activeRevision?.implementationKind === "workflow" &&
    (workflowsQuery.isLoading ||
      teamWorkflowDetailQuery.isLoading ||
      teamWorkflowDocumentsQuery.isLoading);
  const teamScopedRoleUnavailable =
    hasTeamIdentity &&
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
  const routeBackendMemberId = trimText(routeState.memberId);
  const firstTeamRosterMemberId = trimText(teamMembersQuery.data?.members?.[0]?.memberId);
  const currentMemberId =
    trimText(preferredMemberSummary?.memberId) ||
    trimText(preferredMemberId);
  React.useEffect(() => {
    const canonicalMemberId = trimText(currentMemberId);
    if (
      !hasTeamIdentity ||
      !canonicalMemberId ||
      !trimText(routeState.serviceId) ||
      trimText(routeState.memberId)
    ) {
      return;
    }

    history.replace(
      buildTeamDetailHref({
        memberId: canonicalMemberId,
        scopeId,
        teamId: selectedTeamId || undefined,
        workflowId: trimText(routeState.workflowId) || undefined,
        serviceId: trimText(routeState.serviceId) || undefined,
        runId: trimText(routeState.runId) || undefined,
        tab: routeState.tab === "overview" ? undefined : routeState.tab,
      }),
    );
  }, [
    currentMemberId,
    routeState.memberId,
    routeState.runId,
    routeState.serviceId,
    routeState.tab,
    routeState.workflowId,
    selectedTeamId,
    hasTeamIdentity,
    scopeId,
  ]);
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
  const selectedStudioBackendMemberId =
    routeBackendMemberId ||
    firstTeamRosterMemberId ||
    (hasTeamIdentity ? "" : currentMemberId);
  const selectedStudioLegacyMemberId =
    hasTeamIdentity
      ? ""
      : trimText(runtimeServiceId) ||
        trimText(serviceRevisionsQuery.data?.serviceId) ||
        trimText(preferredServiceId) ||
        trimText(servicesQuery.data?.[0]?.serviceId) ||
        trimText(activeWorkflowSummary?.serviceKey).split(":").pop()?.trim() ||
        "";
  const selectedStudioMemberId =
    selectedStudioBackendMemberId || selectedStudioLegacyMemberId;
  const selectedStudioWorkflowMemberKey = buildStudioWorkflowMemberKey({
    workflowId: activeWorkflowSummary?.workflowId,
    workflowName:
      trimText(activeWorkflowSummary?.displayName) ||
      trimText(activeWorkflowSummary?.workflowName),
  });
  const selectedStudioMemberKey =
    resolveStudioMemberRouteKey({
      memberId: selectedStudioBackendMemberId,
      memberKey: selectedStudioWorkflowMemberKey,
    }) ||
    resolveStudioMemberRouteKey({
      memberId: selectedStudioLegacyMemberId,
    });

  const teamBuilderRoute =
    trimText(activeWorkflowSummary?.workflowId).length > 0
      ? buildStudioWorkflowEditorRoute({
          scopeId,
          teamId: selectedTeamId || undefined,
          memberKey: selectedStudioMemberKey,
          workflowId: activeWorkflowSummary?.workflowId,
        })
      : buildStudioWorkflowWorkspaceRoute({
          scopeId,
          teamId: selectedTeamId || undefined,
          memberKey: selectedStudioMemberKey,
        });

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
    if (!hasTeamIdentity) {
      setSelectedActorId("");
      return;
    }

    if (availableActorIds.length === 0) {
      setSelectedActorId("");
      return;
    }
    if (!selectedActorId || !availableActorIds.includes(selectedActorId)) {
      setSelectedActorId(defaultSelectedActorId || availableActorIds[0]);
    }
  }, [availableActorIds, defaultSelectedActorId, hasTeamIdentity, selectedActorId]);

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
          runId,
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

  const missionControlPrompt =
    trimText(lens.currentRunAudit?.audit.input) ||
    trimText(lens.playback.launchPrompt) ||
    trimText(lens.playback.prompt);

  const handleOpenMissionControl = React.useCallback(() => {
    const runId =
      trimText(lens.currentRun?.runId) || trimText(lens.playback.currentRunId);
    if (!scopeId || !runId) {
      return;
    }

    const actorId =
      trimText(lens.currentRun?.actorId) ||
      trimText(lens.playback.rootActorId) ||
      trimText(lens.graph.focusActorId) ||
      undefined;

    history.push(
      buildRuntimeMissionControlHref({
        actorId,
        autoStream: Boolean(missionControlPrompt),
        endpointId: "chat",
        prompt: missionControlPrompt || undefined,
        runId,
        scopeId,
        serviceId: runtimeServiceId,
      }),
    );
  }, [
    lens.currentRun?.actorId,
    lens.currentRun?.runId,
    lens.graph.focusActorId,
    lens.playback.currentRunId,
    lens.playback.rootActorId,
    missionControlPrompt,
    runtimeServiceId,
    scopeId,
  ]);

  const teamHeading = resolveTeamHeading({
    scopeId,
    workflowId: activeWorkflowSummary?.workflowId,
    workflowName: activeWorkflowSummary?.workflowName,
    displayName:
      trimText(teamSummaryQuery.data?.displayName) ||
      trimText(preferredMemberSummary?.displayName) ||
      activeWorkflowSummary?.displayName,
    lensTitle: lens.title,
  });
  const teamTitle = teamHeading.title;
  const teamLifecycleStatus = trimText(teamSummaryQuery.data?.lifecycleStage);
  const teamLifecycleLabel = teamSummaryQuery.data
    ? formatStudioTeamLifecycleStage(teamSummaryQuery.data.lifecycleStage)
    : "";
  const teamSummaryDescription = trimText(teamSummaryQuery.data?.description);
  const teamMetaScopeId = teamHeading.metaScopeId || (selectedTeamId ? scopeId : "");
  const teamTitleMeta =
    selectedTeamId || teamMetaScopeId || teamSummaryQuery.data ? (
      <Space size={[10, 6]} wrap>
        {selectedTeamId ? (
          <Space size={6} wrap>
            <span style={{ textTransform: "none" }}>teamId</span>
            <AevatarCompactText
              color="inherit"
              head={8}
              maxWidth={320}
              monospace
              tail={6}
              value={selectedTeamId}
            />
          </Space>
        ) : null}
        {teamMetaScopeId ? (
          <Space size={6} wrap>
            <span style={{ textTransform: "none" }}>scopeId</span>
            <AevatarCompactText
              color="inherit"
              head={8}
              maxWidth={320}
              monospace
              tail={6}
              value={teamMetaScopeId}
            />
          </Space>
        ) : null}
        {teamSummaryQuery.data ? (
          <span>{teamSummaryQuery.data.memberCount} 个成员</span>
        ) : null}
        {teamSummaryDescription ? <span>{teamSummaryDescription}</span> : null}
      </Space>
    ) : null;
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
  const teamRosterRows = React.useMemo(
    () =>
      (teamMembersQuery.data?.members ?? []).map((member) => ({
        description: trimText(member.description),
        implementationKind: formatCompositionKind(member.implementationKind),
        key: member.memberId,
        lifecycleLabel: formatStudioMemberLifecycleStage(member.lifecycleStage),
        lifecycleStyle: resolveStatusPillStyle(token, member.lifecycleStage),
        memberId: member.memberId,
        name: trimText(member.displayName) || member.memberId,
        serviceId: trimText(member.publishedServiceId) || "--",
      })),
    [teamMembersQuery.data?.members, token],
  );
  const latestVisibleUpdate =
    teamSummaryQuery.data?.updatedAt ||
    lens.currentRun?.lastUpdatedAt ||
    lens.currentRunAudit?.summary.lastUpdatedAt ||
    activeWorkflowSummary?.updatedAt ||
    "";
  const latestVisibleUpdateNote = teamSummaryQuery.data?.updatedAt
    ? "来自 Team authority 更新时间"
    : lens.currentRun?.lastUpdatedAt
      ? trimText(lens.currentRun?.runId)
      ? `来自 run ${compactId(lens.currentRun?.runId)}`
      : "来自最近可见运行"
      : lens.currentRunAudit?.summary.lastUpdatedAt
        ? "来自最近审计摘要"
        : activeWorkflowSummary?.updatedAt
          ? "来自 workflow 更新时间"
          : "当前还没有可见更新时间";
  const activeRunId =
    lens.currentRun?.runId ||
    lens.playback.currentRunId ||
    focusedOperationalUnit?.latestRun?.runId ||
    "";
  const currentRevisionId = trimText(lens.activeRevision?.revisionId) || "--";
  const currentGovernanceRevisionId =
    trimText(lens.activeRevision?.revisionId) ||
    trimText(lens.currentService?.activeServingRevisionId) ||
    trimText(lens.currentService?.defaultServingRevisionId) ||
    "";
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
  const currentEndpointCount = lens.currentService?.endpoints.length ?? 0;
  const currentPolicyCount = lens.currentService?.policyIds.length ?? 0;
  const connectorHighlights = React.useMemo(
    () =>
      integrations.items
        .filter((item) => item.usedByRoles.length > 0)
        .slice(0, 3)
        .map((item) => item.name),
    [integrations.items],
  );
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
    if (!hasTeamIdentity) {
      setSelectedTopologyNodeId("");
      return;
    }

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
  }, [effectiveActorId, hasTeamIdentity, selectedTopologyNodeId, topologyNodeIds]);
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
  const teamAuthorityStatusLabel = teamSummaryQuery.data
    ? teamLifecycleLabel
    : isTeamSummaryProjectionSyncing
      ? "Projection 同步中"
    : teamSummaryQuery.isError
      ? "Team summary 不可用"
      : selectedTeamId
        ? "加载中"
        : "未绑定 Team";
  const teamAuthorityStatusStyle = teamSummaryQuery.data
    ? resolveStatusPillStyle(token, teamLifecycleStatus)
    : isTeamSummaryProjectionSyncing
      ? resolveTonePillStyle(token, "warning")
    : teamSummaryQuery.isError
      ? resolveTonePillStyle(token, "warning")
      : resolveTonePillStyle(token, "neutral");
  const teamAuthorityTitle = teamSummaryQuery.data
    ? teamTitle
    : isTeamSummaryProjectionSyncing
      ? "Team projection 正在同步"
    : selectedTeamId
      ? "Team summary 暂不可用"
      : teamTitle;
  const teamAuthorityDescription = teamSummaryQuery.data
    ? teamSummaryDescription || "Team authority 当前没有描述。"
    : isTeamSummaryProjectionSyncing
      ? "Team 已创建，后端正在把 committed state 物化到查询用 read model。这里会自动刷新。"
    : selectedTeamId
      ? "当前仍会显示运行时视图；Team authority summary 暂时无法读取。"
      : "当前详情来自运行时上下文，还没有明确的 Team authority 绑定。";
  const teamAuthorityRows = [
    {
      badge: teamLifecycleLabel || teamAuthorityStatusLabel,
      badgeStyle: teamAuthorityStatusStyle,
      key: "teamLifecycle",
      label: "生命周期",
      note: teamSummaryQuery.data
        ? `teamId · ${compactId(teamSummaryQuery.data.teamId)}`
        : selectedTeamId
          ? `teamId · ${compactId(selectedTeamId)}`
          : "当前路由没有 Team ID",
      noteMonospace: false,
      noteTooltip: teamSummaryQuery.data
        ? `teamId · ${teamSummaryQuery.data.teamId}`
        : selectedTeamId
          ? `teamId · ${selectedTeamId}`
          : "当前路由没有 Team ID",
      value: teamAuthorityStatusLabel,
    },
    {
      badge: teamSummaryQuery.data ? `${teamSummaryQuery.data.memberCount}` : "--",
      badgeStyle: teamSummaryQuery.data
        ? resolveTonePillStyle(token, "info")
        : resolveTonePillStyle(token, "neutral"),
      key: "teamMembers",
      label: "成员规模",
      note: teamSummaryQuery.data
        ? "来自 Team authority memberCount"
        : "成员数会在 Team summary 可用后显示",
      noteMonospace: false,
      value: teamSummaryQuery.data ? `${teamSummaryQuery.data.memberCount} 个成员` : "--",
    },
    {
      badge: teamSummaryQuery.data?.updatedAt ? "已同步" : "--",
      badgeStyle: teamSummaryQuery.data?.updatedAt
        ? resolveTonePillStyle(token, "success")
        : resolveTonePillStyle(token, "neutral"),
      key: "teamUpdatedAt",
      label: "Team 更新时间",
      note: teamSummaryQuery.data?.updatedAt
        ? "来自 Team authority updatedAt"
        : "当前使用运行时更新时间作为降级参考",
      noteMonospace: false,
      value: teamSummaryQuery.data?.updatedAt
        ? formatCompactTimestamp(teamSummaryQuery.data.updatedAt)
        : "--",
    },
  ];
  const overviewGovernanceRows = [
    {
      badge: currentRevisionId !== "--" ? "serving" : "unknown",
      badgeStyle:
        currentRevisionId !== "--"
          ? resolveTonePillStyle(token, "info")
          : resolveTonePillStyle(token, "neutral"),
      key: "servingRevision",
      label: "Serving",
      note:
        lens.governance.servingRevision !== "Unknown"
          ? `serving revision · ${compactId(lens.governance.servingRevision)}`
          : "当前还没有可见 serving revision。",
      value:
        lens.governance.servingRevision !== "Unknown"
          ? compactId(lens.governance.servingRevision)
          : "Unknown",
    },
    {
      badge: lens.currentRun ? "可追踪" : "暂无运行",
      badgeStyle: lens.currentRun
        ? resolveTonePillStyle(token, "success")
        : resolveTonePillStyle(token, "neutral"),
      key: "traceability",
      label: "审计链路",
      note: lens.governance.traceability,
      value: lens.currentRun ? `run ${compactId(lens.currentRun.runId)}` : "暂无近期运行",
    },
    {
      badge: lens.humanInterventionDetected ? "人工介入" : "未介入",
      badgeStyle: lens.humanInterventionDetected
        ? resolveTonePillStyle(token, "warning")
        : resolveTonePillStyle(token, "success"),
      key: "humanIntervention",
      label: "人工介入",
      note: lens.governance.humanIntervention,
      value: lens.humanInterventionDetected ? "Manual gate active" : "No active override",
    },
    {
      badge: lens.baselineRun ? "有基线" : "无基线",
      badgeStyle: lens.baselineRun
        ? resolveTonePillStyle(token, "success")
        : resolveTonePillStyle(token, "warning"),
      key: "fallback",
      label: "回退基线",
      note: lens.governance.fallback,
      value: lens.baselineRun ? `run ${compactId(lens.baselineRun.runId)}` : "Fallback unavailable",
    },
    {
      badge: currentDeploymentFriendly,
      badgeStyle: resolveStatusPillStyle(token, currentDeploymentStatus),
      key: "rollout",
      label: "Rollout",
      note: lens.governance.rollout,
      value: currentDeploymentFriendly,
    },
  ];
  const overviewCompareStatusLabel = lens.compare.available
    ? "基线可用"
    : lens.currentRun
      ? "等待基线"
      : "等待运行";
  const overviewCompareStatusStyle = lens.compare.available
    ? resolveTonePillStyle(token, "success")
    : lens.currentRun
      ? resolveTonePillStyle(token, "warning")
      : resolveTonePillStyle(token, "info");
  const overviewHealthDetails = lens.healthDetails.map(formatTeamHealthDetail);
  const overviewPartialSignals = lens.partialSignals.map(formatPartialSignal);
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
  ];

  const initialLoading =
    serviceRevisionsQuery.isLoading ||
    servicesQuery.isLoading ||
    actorsQuery.isLoading ||
    workflowsQuery.isLoading ||
    scriptsQuery.isLoading;

  const pushTeamTab = React.useCallback(
    (tab: TeamDetailTab) => {
      setActiveTab(tab);
      history.push(
        buildTeamDetailHref({
          memberId: currentMemberId || undefined,
          scopeId,
          teamId: selectedTeamId || undefined,
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
      currentMemberId,
      lens.currentRun?.runId,
      lens.playback.currentRunId,
      preferredRunId,
      runtimeServiceId,
      selectedTeamId,
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
          memberId: currentMemberId || undefined,
          scopeId,
          teamId: selectedTeamId || undefined,
          workflowId: activeWorkflowId || undefined,
          serviceId: runtimeServiceId,
          runId: normalizedRunId || undefined,
          tab: activeTab,
        }),
      );
    },
    [
      activeTab,
      activeWorkflowId,
      currentMemberId,
      runtimeServiceId,
      selectedTeamId,
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
        runId: lens.currentRun?.runId || undefined,
      }),
    );
  }, [
    handleOpenPlaybackRun,
    lens.currentRun?.actorId,
    lens.playback.currentRunId,
    runtimeServiceId,
    scopeId,
  ]);
  const conversationActionLabel = lens.playback.currentRunId
    ? lens.healthStatus === "blocked"
      ? "处理等待 Run"
      : lens.healthStatus === "degraded"
        ? "排查失败 Run"
        : "本次成员对话"
    : "成员运行记录";
  const serviceMappingActionLabel = "服务映射";
  const governanceActionLabel = "治理绑定";
  const deploymentsActionLabel = "部署记录";
  const teamBuilderActionLabel = "高级编辑";
  const editTeamActionLabel = "Edit Team";
  const canEditSelectedTeam = Boolean(teamSummaryQuery.data && selectedTeamId);
  const editTeamHint = selectedTeamId
    ? "Team summary 读取完成后才能编辑。"
    : "当前路由还没有选中真实 Team。";
  const openTeamEditor = React.useCallback(() => {
    if (!teamSummaryQuery.data) {
      return;
    }

    setTeamEditorName(teamSummaryQuery.data.displayName);
    setTeamEditorDescription(teamSummaryQuery.data.description);
    setTeamEditorOpen(true);
  }, [teamSummaryQuery.data]);
  const closeTeamEditor = React.useCallback(() => {
    if (teamEditorSaving) {
      return;
    }

    setTeamEditorOpen(false);
  }, [teamEditorSaving]);
  const saveTeamEditor = React.useCallback(async () => {
    if (!teamSummaryQuery.data || teamEditorSaving) {
      return;
    }

    const displayName = teamEditorName.trim();
    if (!displayName) {
      void message.error("Team name is required.");
      return;
    }

    setTeamEditorSaving(true);
    try {
      await studioApi.updateTeam({
        scopeId,
        teamId: selectedTeamId,
        displayName,
        description: teamEditorDescription.trim() || null,
      });
      void message.success("Team update accepted.");
      setTeamEditorOpen(false);
      await refreshTeamAuthority();
    } catch (error) {
      void message.error(describeError(error, "Team update failed."));
    } finally {
      setTeamEditorSaving(false);
    }
  }, [
    refreshTeamAuthority,
    scopeId,
    selectedTeamId,
    teamEditorDescription,
    teamEditorName,
    teamEditorSaving,
    teamSummaryQuery.data,
  ]);
  const isTeamArchived = normalizeStatus(teamSummaryQuery.data?.lifecycleStage) === "archived";
  const archiveTeamActionLabel = teamSummaryQuery.data && !isTeamArchived ? "Archive Team" : "";
  const archiveTeamHint = selectedTeamId
    ? "Team summary 读取完成后才能归档。"
    : "当前路由还没有选中真实 Team。";
  const openTeamArchive = React.useCallback(() => {
    if (!teamSummaryQuery.data || isTeamArchived) {
      return;
    }

    setTeamArchiveOpen(true);
  }, [isTeamArchived, teamSummaryQuery.data]);
  const closeTeamArchive = React.useCallback(() => {
    if (teamArchiving) {
      return;
    }

    setTeamArchiveOpen(false);
  }, [teamArchiving]);
  const confirmTeamArchive = React.useCallback(async () => {
    if (!teamSummaryQuery.data || isTeamArchived || teamArchiving) {
      return;
    }

    setTeamArchiving(true);
    try {
      await studioApi.archiveTeam(scopeId, selectedTeamId);
      void message.success("Team archive accepted.");
      setTeamArchiveOpen(false);
      await refreshTeamAuthority();
    } catch (error) {
      void message.error(describeError(error, "Team archive failed."));
    } finally {
      setTeamArchiving(false);
    }
  }, [
    isTeamArchived,
    refreshTeamAuthority,
    scopeId,
    selectedTeamId,
    teamArchiving,
    teamSummaryQuery.data,
  ]);
  const topologyFocusActorId =
    trimText(effectiveActorId) ||
    trimText(lens.graph.focusActorId) ||
    trimText(lens.playback.rootActorId) ||
    trimText(lens.currentRun?.actorId) ||
    "";
  const canAdjustTopologyDepth = topologyFocusActorId.length > 0;
  const canOpenPlatformTopology = topologyFocusActorId.length > 0;
  const topologyDepthLabel = formatTopologyDepthLabel(graphDepth);
  const topologyControlsHint = canAdjustTopologyDepth
    ? ""
    : "当前还没有可用的团队成员焦点，待成员或运行信号可见后再切换视角。";
  const platformTopologyHint = canOpenPlatformTopology
    ? ""
    : "当前还没有可打开的平台拓扑焦点。";
  const topologyEmptyDescription = canAdjustTopologyDepth
    ? `当前在${topologyDepthLabel}视角下还没有更多可见的事件拓扑关系。`
    : "当前还没有可用的团队成员焦点，所以暂时没有可展开的事件拓扑关系。";
  const handleOpenTeamsList = React.useCallback(() => {
    history.push(teamsListHref);
  }, [teamsListHref]);

  const handleOpenServiceMapping = React.useCallback(() => {
    handleOpenPlaybackActor(
      topologyFocusActorId,
      lens.currentRun?.runId || lens.playback.currentRunId,
    );
  }, [
    handleOpenPlaybackActor,
    lens.currentRun?.actorId,
    lens.currentRun?.runId,
    lens.playback.currentRunId,
    topologyFocusActorId,
  ]);
  const handleOpenServices = React.useCallback(() => {
    history.push(buildPlatformServicesHref(platformRouteIdentity));
  }, [platformRouteIdentity]);
  const handleOpenGovernance = React.useCallback(() => {
    history.push(
      buildPlatformGovernanceHref({
        ...platformRouteIdentity,
        revisionId: currentGovernanceRevisionId || undefined,
        view: "bindings",
      }),
    );
  }, [currentGovernanceRevisionId, platformRouteIdentity]);
  const handleOpenDeployments = React.useCallback(() => {
    history.push(
      buildPlatformDeploymentsHref({
        ...platformRouteIdentity,
        deploymentId: currentDeploymentId !== "--" ? currentDeploymentId : undefined,
      }),
    );
  }, [currentDeploymentId, platformRouteIdentity]);

  const renderOverviewTab = () => {
    return (
      <TeamOverviewTab
        configurationDetailRows={configurationDetailRows}
        compareAvailable={lens.compare.available}
        compareSections={lens.compare.sections}
        compareStatusLabel={overviewCompareStatusLabel}
        compareStatusStyle={overviewCompareStatusStyle}
        compareSummary={lens.compare.summary}
        compareTitle={lens.compare.title}
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
        governanceRows={overviewGovernanceRows}
        healthActionLabel={formatTeamHealthActionLabel(lens.healthStatus)}
        healthDetails={overviewHealthDetails}
        healthStatusLabel={formatTeamHealthStatusLabel(lens.healthStatus)}
        healthStatusStyle={resolveTeamHealthToneStyle(token, lens.healthTone)}
        healthSummary={lens.healthSummary}
        latestVisibleUpdateLabel={formatCompactTimestamp(latestVisibleUpdate)}
        latestVisibleUpdateNote={latestVisibleUpdateNote}
        partialSignals={overviewPartialSignals}
        runtimeSummaryRows={overviewRuntimeSummaryRows}
        teamAuthorityDescription={teamAuthorityDescription}
        teamAuthorityRows={teamAuthorityRows}
        teamAuthorityStatusLabel={teamAuthorityStatusLabel}
        teamAuthorityStatusStyle={teamAuthorityStatusStyle}
        teamAuthorityTitle={teamAuthorityTitle}
      />
    );
  };

  const renderTopologyTab = () => {
    return (
      <TeamTopologyTab
        depthControlDisabled={!canAdjustTopologyDepth}
        depthControlHint={topologyControlsHint || undefined}
        graphDepth={graphDepth}
        graphEdgeCount={topologyGraph.edges.length}
        graphFocusLabel={`${topologyDepthLabel}视角 · 焦点 ${topologyFocusActorId ? compactId(topologyFocusActorId) : "未选中"}`}
        graphNodeCount={topologyGraph.nodes.length}
        isError={actorGraphQuery.isError}
        isLoading={actorGraphQuery.isLoading}
        onCanvasSelect={() =>
          setSelectedTopologyNodeId(
            topologyGraph.entityMap.has(topologyFocusActorId)
              ? topologyFocusActorId
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
        platformTopologyDisabled={!canOpenPlatformTopology}
        platformTopologyHint={platformTopologyHint || undefined}
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
        selectedNodeId={selectedTopologyNodeId || topologyFocusActorId}
        topologyEmptyDescription={topologyEmptyDescription}
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
        onOpenMissionControl={handleOpenMissionControl}
        onSelectRun={handleSelectRun}
        openAuditButtonStyle={resolveActionButtonStyle(token)}
        playbackSummary={formatPlaybackSummary(lens.playback.summary)}
        provenanceLabel={playbackProvenance.label}
        provenanceStyle={resolveObservationPillStyle(token, playbackProvenance.status)}
        runSwitchOptions={runSwitchDisplayOptions}
        showOpenAudit={Boolean(activeRunId)}
        showOpenMissionControl={Boolean(activeRunId && missionControlPrompt)}
      />
    );
  };

  const renderMembersTab = () => {
    return (
      <TeamMembersTab
        compositionRows={memberCompositionRows}
        identityRows={memberIdentityRows}
        openRuntimeExplorerDisabled={!canOpenPlatformTopology}
        openRuntimeExplorerHint={platformTopologyHint || undefined}
        onOpenRuntimeExplorer={handleOpenServiceMapping}
        onOpenServices={handleOpenServices}
        onSelectActor={setSelectedActorId}
        rosterError={teamMembersQuery.isError && !isTeamMembersProjectionSyncing}
        rosterLoading={teamMembersQuery.isLoading}
        rosterSyncing={isTeamMembersProjectionSyncing}
        rosterRows={teamRosterRows}
        rosterTeamId={selectedTeamId}
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
    default:
      tabContent = renderOverviewTab();
      break;
  }

  if (!hasTeamIdentity) {
    return <TeamDetailEmptyState />;
  }

  return (
    <TeamDetailShell
      actionRail={
        <TeamActionRail
          archiveTeamActionLabel={archiveTeamActionLabel || undefined}
          archiveTeamDisabled={!teamSummaryQuery.data || teamArchiving}
          archiveTeamHint={archiveTeamHint}
          conversationActionLabel={conversationActionLabel}
          deploymentsActionLabel={deploymentsActionLabel}
          deploymentsDisabled={currentDeploymentId === "--"}
          deploymentsHint="当前还没有可定位的部署记录。"
          editTeamDisabled={!canEditSelectedTeam}
          editTeamHint={editTeamHint}
          editTeamLabel={editTeamActionLabel}
          governanceActionLabel={governanceActionLabel}
          governanceDisabled={!runtimeServiceId}
          governanceHint="当前还没有可定位的服务绑定。"
          onArchiveTeam={openTeamArchive}
          onOpenConversation={handleOpenConversation}
          onOpenDeployments={handleOpenDeployments}
          onOpenGovernance={handleOpenGovernance}
          onOpenServiceMapping={handleOpenServiceMapping}
          onOpenTeamEditor={openTeamEditor}
          onOpenTeamBuilder={() => history.push(teamBuilderRoute)}
          serviceMappingDisabled={!canOpenPlatformTopology}
          serviceMappingHint={platformTopologyHint || undefined}
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
        teamSummaryQuery.data ? (
          <DetailPill
            style={resolveStatusPillStyle(token, teamLifecycleStatus)}
            text={teamLifecycleLabel}
          />
        ) : currentHeaderStatusFriendly !== "--" ? (
          <DetailPill
            style={resolveStatusPillStyle(token, currentHeaderStatus)}
            text={currentHeaderStatusFriendly}
          />
        ) : null
      }
      tabOptions={tabOptions}
      teamMeta={teamTitleMeta}
      teamTitle={teamTitle}
      teamsListHref={teamsListHref}
    >
      {tabContent}
      <Modal
        confirmLoading={teamEditorSaving}
        okButtonProps={{ disabled: !teamEditorName.trim() }}
        okText="Save Team"
        onCancel={closeTeamEditor}
        onOk={() => void saveTeamEditor()}
        open={teamEditorOpen}
        title="Edit Team"
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>Team name</Typography.Text>
            <Input
              aria-label="Edit team name"
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorName(event.target.value)}
              value={teamEditorName}
            />
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>Description</Typography.Text>
            <Input.TextArea
              aria-label="Edit team description"
              autoSize={{ minRows: 3, maxRows: 5 }}
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorDescription(event.target.value)}
              value={teamEditorDescription}
            />
          </div>
          <Typography.Text type="secondary">
            This updates the Team authority summary. Archived Teams can still be
            edited and maintained.
          </Typography.Text>
        </div>
      </Modal>
      <Modal
        confirmLoading={teamArchiving}
        okText="Archive Team"
        okButtonProps={{ danger: true }}
        onCancel={closeTeamArchive}
        onOk={() => void confirmTeamArchive()}
        open={teamArchiveOpen}
        title="Archive this Team?"
      >
        <Typography.Text>
          This marks the Team as archived and de-emphasizes it in the active
          roster. You can still edit its configuration and view its history.
        </Typography.Text>
      </Modal>
    </TeamDetailShell>
  );
};

export default TeamDetailPage;
