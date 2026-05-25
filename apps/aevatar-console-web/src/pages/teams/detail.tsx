import { Input, Modal, Space, Typography, message, theme } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import React from "react";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { buildScopeHref } from "@/shared/navigation/scopeRoutes";
import {
  buildTeamDetailHref,
  buildTeamStudioHref,
  readTeamDetailRouteState,
  type TeamDetailTab,
} from "@/shared/navigation/teamRoutes";
import { isStudioApiStatus, studioApi } from "@/shared/studio/api";
import {
  formatStudioMemberLifecycleStage,
  formatStudioTeamLifecycleStage,
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
import { DetailPill } from "./components/TeamDetailPrimitives";
import TeamMembersTab from "./tabs/TeamMembersTab";
import TeamOverviewTab from "./tabs/TeamOverviewTab";
import { resolveWorkflowOperationalUnit } from "./workflowOperationalUnits";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

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

function resolveCompositionKindPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  kind: string,
): React.CSSProperties {
  switch (normalizeStatus(kind)) {
    case "workflow role":
      return {
        background: "rgba(24, 144, 255, 0.08)",
        color: token.colorInfo,
      };
    case "workflow":
      return {
        background: "rgba(82, 196, 26, 0.12)",
        color: token.colorSuccess,
      };
    case "service":
      return {
        background: "rgba(250, 173, 20, 0.12)",
        color: token.colorWarning,
      };
    case "actor":
    case "runtime":
    default:
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
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

function formatCompactTimestamp(value: string | null | undefined): string {
  return formatCompactDateTime(value, "暂无");
}

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
  const [preferredMemberId, setPreferredMemberId] = React.useState(routeState.memberId);
  const [preferredServiceId, setPreferredServiceId] = React.useState(
    routeState.serviceId,
  );
  const [preferredRunId, setPreferredRunId] = React.useState(routeState.runId);
  const [activeTab, setActiveTab] = React.useState<TeamDetailTab>(routeState.tab);
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
  const isTeamMembersProjectionSyncing =
    shouldRetryProjectionSync &&
    ((teamMembersQuery.failureCount > 0 &&
      isProjectionSyncing404(teamMembersQuery.failureReason)) ||
      (teamMembersQuery.isError && isProjectionSyncing404(teamMembersQuery.error)));
  const teamMemberServiceIds = React.useMemo(
    () =>
      (teamMembersQuery.data?.members ?? [])
        .map((member) => trimText(member.publishedServiceId))
        .filter(Boolean),
    [teamMembersQuery.data?.members],
  );
  const hasExplicitRuntimeFocus = Boolean(
    trimText(preferredMemberId) || trimText(preferredServiceId) || trimText(preferredRunId),
  );
  const {
    lens,
    runsQuery,
    preferredMemberSummary,
    serviceRevisionsQuery,
    servicesQuery,
    workflowsQuery,
  } = useTeamRuntimeLens(scopeId, {
    allowScopeServiceFallback: false,
    enabled: hasTeamIdentity,
    preferredMemberId,
    preferredRunId,
    preferredServiceId,
    teamMemberServiceIds,
  });

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
    const workflowNameHint = trimText(lens.activeRevision.workflowName);
    if (workflowNameHint) {
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
  }, [lens.activeRevision, workflowsQuery.data]);

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
    if (!activeWorkflowSummary || !hasExplicitRuntimeFocus) {
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
    hasExplicitRuntimeFocus,
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

  const runtimeServiceId =
    focusedOperationalUnit?.matchedService?.serviceId ||
    lens.currentService?.serviceId ||
    lens.currentRun?.serviceId ||
    undefined;
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
  const buildTeamReturnHref = React.useCallback(
    (memberId?: string) =>
      buildTeamDetailHref({
        memberId: trimText(memberId) || undefined,
        scopeId,
        tab: "members",
        teamId: selectedTeamId,
      }),
    [scopeId, selectedTeamId],
  );
  const teamRosterRows = React.useMemo(
    () =>
      (teamMembersQuery.data?.members ?? []).map((member) => ({
        buildStudioHref: buildTeamStudioHref({
          memberId: member.memberId,
          mode: "build-member",
          returnTo: buildTeamReturnHref(member.memberId),
          scopeId,
          teamId: selectedTeamId,
        }),
        description: trimText(member.description),
        editStudioHref: buildTeamStudioHref({
          memberId: member.memberId,
          mode: "edit-member",
          returnTo: buildTeamReturnHref(member.memberId),
          scopeId,
          teamId: selectedTeamId,
        }),
        implementationKind: formatCompositionKind(member.implementationKind),
        key: member.memberId,
        lifecycleLabel: formatStudioMemberLifecycleStage(member.lifecycleStage),
        lifecycleStyle: resolveStatusPillStyle(token, member.lifecycleStage),
        memberId: member.memberId,
        name: trimText(member.displayName) || member.memberId,
        serviceId: trimText(member.publishedServiceId) || "--",
      })),
    [buildTeamReturnHref, scopeId, selectedTeamId, teamMembersQuery.data?.members, token],
  );
  const createMemberHref = React.useMemo(
    () =>
      buildTeamStudioHref({
        mode: "create-member",
        returnTo: buildTeamReturnHref(),
        scopeId,
        teamId: selectedTeamId,
      }),
    [buildTeamReturnHref, scopeId, selectedTeamId],
  );
  const latestVisibleUpdate =
    teamSummaryQuery.data?.updatedAt ||
    lens.currentRun?.lastUpdatedAt ||
    activeWorkflowSummary?.updatedAt ||
    "";
  const latestVisibleUpdateNote = teamSummaryQuery.data?.updatedAt
    ? "来自 Team 更新时间"
    : lens.currentRun?.lastUpdatedAt
      ? trimText(lens.currentRun?.runId)
      ? `来自 run ${compactId(lens.currentRun?.runId)}`
      : "来自最近可见运行"
      : activeWorkflowSummary?.updatedAt
        ? "来自 workflow 更新时间"
        : "当前还没有可见更新时间";
  const activeRunId =
    lens.currentRun?.runId ||
    focusedOperationalUnit?.latestRun?.runId ||
    "";
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
        label: "版本标识",
        note: `revisionId: ${currentRevisionId}`,
        value: currentVersionFriendly,
      },
    ],
    [
      activeWorkflowId,
      currentRevisionId,
      currentServiceFriendly,
      currentServiceKey,
      currentVersionFriendly,
      lens.activeRevision?.implementationKind,
      runtimeServiceId,
      teamTitle,
      workflowNameValue,
    ],
  );
  const compositionDisplayRows = React.useMemo(() => {
    if (teamRosterRows.length > 0) {
      return teamRosterRows.map((row) => ({
        key: row.key,
        kind: row.implementationKind,
        name: row.name,
        summary: row.description || `服务入口 ${row.serviceId}`,
      }));
    }

    if (!hasExplicitRuntimeFocus) {
      return [];
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
        summary: activeRunId ? currentRunFriendly : "暂无最近运行",
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
    currentRunFriendly,
    currentServiceFriendly,
    hasExplicitRuntimeFocus,
    teamRosterRows,
    workflowNameValue,
  ]);
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
  const tabOptions: TeamTabOption[] = [
    { label: "概览", value: "overview" },
    { label: "团队成员", value: "members" },
  ];

  const initialLoading =
    serviceRevisionsQuery.isLoading ||
    servicesQuery.isLoading ||
    workflowsQuery.isLoading;

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
            undefined,
          tab,
        }),
      );
    },
    [
      activeWorkflowId,
      currentMemberId,
      lens.currentRun?.runId,
      preferredRunId,
      runtimeServiceId,
      selectedTeamId,
      scopeId,
    ],
  );

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
      void message.success("Team updated.");
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
      void message.success("Team archived.");
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
  const handleOpenTeamsList = React.useCallback(() => {
    history.push(teamsListHref);
  }, [teamsListHref]);

  const renderOverviewTab = () => {
    return (
      <TeamOverviewTab
        configurationDetailRows={configurationDetailRows}
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
      />
    );
  };

  const renderMembersTab = () => {
    return (
      <TeamMembersTab
        createMemberHref={createMemberHref}
        onNavigate={(href) => history.push(href)}
        rosterError={teamMembersQuery.isError && !isTeamMembersProjectionSyncing}
        rosterLoading={teamMembersQuery.isLoading}
        rosterSyncing={isTeamMembersProjectionSyncing}
        rosterRows={teamRosterRows}
        rosterTeamId={selectedTeamId}
      />
    );
  };

  let tabContent: React.ReactNode;
  switch (activeTab) {
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
          editTeamDisabled={!canEditSelectedTeam}
          editTeamHint={editTeamHint}
          editTeamLabel={editTeamActionLabel}
          onArchiveTeam={openTeamArchive}
          onOpenTeamEditor={openTeamEditor}
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
            This updates the Team summary. Archived Teams can still be edited
            and maintained.
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
