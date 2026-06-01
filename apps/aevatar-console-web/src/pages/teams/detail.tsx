import { Input, Modal, Space, Typography, message, theme } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import React from "react";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from "@/shared/agui/runtimeEventSemantics";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
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
import TeamTestPanel, {
  type TeamTestLastResult,
  type TeamTestStatus,
} from "./components/TeamTestPanel";
import { DetailPill } from "./components/TeamDetailPrimitives";
import {
  describeTeamTestError,
  isAbortLikeError,
  type TeamTestErrorDescription,
} from "./components/teamTestErrors";
import TeamMembersTab from "./tabs/TeamMembersTab";
import TeamOverviewTab from "./tabs/TeamOverviewTab";
import { resolveWorkflowOperationalUnit } from "./workflowOperationalUnits";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

const teamProjectionRetryLimit = 5;
const teamProjectionRetryBaseMs = 500;
const teamProjectionRetryMaxMs = 3_000;
const entryMemberClearingId = "__clear_entry_member__";
const teamEntryVisibilityAttempts = 5;
const teamEntryVisibilityRetryDelayMs = 100;

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

function compactOptionalId(value: string | null | undefined): string {
  const normalized = trimText(value);
  if (!normalized || normalized === "--") {
    return "--";
  }

  return compactId(normalized);
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

function formatTeamLifecycleLabel(value: string | null | undefined): string {
  switch (normalizeStatus(value)) {
    case "active":
      return "已启用";
    case "archived":
      return "已归档";
    case "unknown":
      return "状态未知";
    default:
      return trimText(value) || "状态未知";
  }
}

function formatCompositionKind(kind: string | null | undefined): string {
  switch (normalizeStatus(kind)) {
    case "":
    case "unknown":
      return "暂未识别";
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

function formatLocalTimeLabel(date: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(date);
}

function hasTeamEntryMember(
  summary: StudioTeamSummary | null | undefined,
  memberId: string,
): boolean {
  return trimText(summary?.entryMemberId) === trimText(memberId);
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    globalThis.setTimeout(resolve, ms);
  });
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
  const [teamTestPrompt, setTeamTestPrompt] = React.useState("");
  const [teamTestResultText, setTeamTestResultText] = React.useState("");
  const [teamTestStatus, setTeamTestStatus] = React.useState<TeamTestStatus>("idle");
  const [teamTestError, setTeamTestError] =
    React.useState<TeamTestErrorDescription | null>(null);
  const [teamTestLastResult, setTeamTestLastResult] =
    React.useState<TeamTestLastResult | null>(null);
  const [teamTestModalOpen, setTeamTestModalOpen] = React.useState(false);
  const [entryActionBusyMemberId, setEntryActionBusyMemberId] = React.useState("");
  const teamTestAbortRef = React.useRef<AbortController | null>(null);
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
    if (routeState.testTeam) {
      setTeamTestModalOpen(true);
    }
  }, [
    routeState.memberId,
    routeState.runId,
    routeState.serviceId,
    routeState.tab,
    routeState.testTeam,
  ]);

  React.useEffect(() => {
    teamTestAbortRef.current?.abort();
    teamTestAbortRef.current = null;
    setTeamTestError(null);
    setTeamTestLastResult(null);
    setTeamTestResultText("");
    setTeamTestStatus("idle");
    setTeamTestModalOpen(routeState.testTeam);
    setEntryActionBusyMemberId("");
  }, [routeState.testTeam, scopeId, selectedTeamId]);

  React.useEffect(
    () => () => {
      teamTestAbortRef.current?.abort();
    },
    [],
  );

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
      queryClient.invalidateQueries({
        queryKey: teamMembersQueryKey,
      }),
      queryClient.invalidateQueries({ queryKey: ["teams", "roster", scopeId] }),
    ]);
  }, [queryClient, scopeId, teamMembersQueryKey, teamSummaryQueryKey]);

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
  const selectedRosterMemberId = currentMemberId;
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
    ? formatTeamLifecycleLabel(teamSummaryQuery.data.lifecycleStage)
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
  const entryMemberId = trimText(teamSummaryQuery.data?.entryMemberId);
  const entryMemberSummary = React.useMemo(
    () =>
      entryMemberId
        ? (teamMembersQuery.data?.members ?? []).find(
            (member) => trimText(member.memberId) === entryMemberId,
          ) ?? null
        : null,
    [entryMemberId, teamMembersQuery.data?.members],
  );
  const entryMemberLabel =
    trimText(entryMemberSummary?.displayName) || entryMemberId;
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
        canInvokeAsEntry:
          normalizeStatus(member.lifecycleStage) === "bind_ready" &&
          trimText(member.publishedServiceId).length > 0,
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
        isEntryMember: trimText(member.memberId) === entryMemberId,
        isSelectedMember: trimText(member.memberId) === selectedRosterMemberId,
        memberId: member.memberId,
        name: trimText(member.displayName) || member.memberId,
        serviceId: trimText(member.publishedServiceId) || "--",
      })),
    [
      buildTeamReturnHref,
      entryMemberId,
      selectedRosterMemberId,
      scopeId,
      selectedTeamId,
      teamMembersQuery.data?.members,
      token,
    ],
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
  // Refactor (iterv1/issue1444-first):
  //   Old pattern: Team workbench rendered completed as stable and mixed run/deployment status.
  //   New principle: expose run completion, deployment serving, and readmodel freshness as separate facts.
  const currentHeaderStatusFriendly = teamSummaryQuery.data
    ? `ReadModel · ${formatCompactTimestamp(latestVisibleUpdate)}`
    : "ReadModel 暂不可见";
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
    : "暂无可见运行";
  const currentMemberLabel =
    trimText(preferredMemberSummary?.displayName) ||
    teamRosterRows.find((row) => row.memberId === currentMemberId)?.name ||
    currentMemberId ||
    "--";
  const currentMemberCardCaption = currentMemberId
    ? `memberId · ${compactOptionalId(currentMemberId)}`
    : "当前还没有选中成员";
  const currentMemberCardTooltip = currentMemberId
    ? `memberId · ${currentMemberId}`
    : "当前还没有选中成员";
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
    : "暂无近期可见运行";
  const currentServiceCardCaption = runtimeServiceId
    ? `serviceId · ${compactOptionalId(runtimeServiceId)}`
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
    : "当前还没有同步到可见运行";
  const currentRunCardTooltip = activeRunId
    ? `runId · ${activeRunId}`
    : "当前还没有同步到可见运行";
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
        value: formatCompositionKind(lens.activeRevision?.implementationKind),
      },
      {
        label: "主服务入口",
        note: `serviceId: ${compactOptionalId(runtimeServiceId)} · serviceKey: ${compactOptionalId(currentServiceKey)}`,
        noteTooltip: `serviceId: ${runtimeServiceId || "--"} · serviceKey: ${currentServiceKey}`,
        value: currentServiceFriendly,
      },
      {
        label: "版本标识",
        note: `revisionId: ${compactOptionalId(currentRevisionId)}`,
        noteTooltip: `revisionId: ${currentRevisionId}`,
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
        summary: row.description || `服务入口 ${compactOptionalId(row.serviceId)}`,
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
  const isTeamArchived =
    normalizeStatus(teamSummaryQuery.data?.lifecycleStage) === "archived";
  const hasInvokableTeamMember = teamRosterRows.some((row) => row.canInvokeAsEntry);
  const teamNextStep = React.useMemo(() => {
    if (!teamSummaryQuery.data) {
      return {
        description: "Team read model 可见后，再选择入口成员并发起测试。",
        label: "等待 Team",
        title: "先等待 Team summary 同步",
      };
    }

    if (isTeamArchived) {
      return {
        description: "归档 Team 不再发起新测试；可以继续查看最近运行和历史结果。",
        label: "已归档",
        title: "观察历史 Run",
      };
    }

    if (teamMembersQuery.isLoading || isTeamMembersProjectionSyncing) {
      return {
        description: "成员清单同步完成后，在团队成员里选择可调用成员作为入口。",
        label: "同步成员",
        title: "等待 roster 后设置入口成员",
      };
    }

    if (teamRosterRows.length === 0) {
      return {
        description: "创建第一个成员，再在 Studio 完成 Build / Bind。",
        label: "Step 1",
        title: "创建 Team 成员",
      };
    }

    if (!hasInvokableTeamMember) {
      return {
        description: "打开 Studio，把成员 Build / Bind 到可调用服务后再回到 Team 测试。",
        label: "Step 2",
        title: "Build / Bind 成员",
      };
    }

    if (!entryMemberId) {
      return {
        description: "在团队成员里把可调用成员设为入口，Team 测试会从它开始。",
        label: "Step 3",
        title: "设置入口成员",
      };
    }

    if (!activeRunId) {
      return {
        description: "点击测试团队，输入问题并创建一次独立 Run。",
        label: "Step 4",
        title: "发起 Team 测试",
      };
    }

    return {
      description: "最近 Run 已可见，可继续测试或观察当前 Run 状态。",
      label: "Step 5",
      title: "观察 Run 结果",
    };
  }, [
    activeRunId,
    entryMemberId,
    hasInvokableTeamMember,
    isTeamArchived,
    isTeamMembersProjectionSyncing,
    teamMembersQuery.isLoading,
    teamRosterRows.length,
    teamSummaryQuery.data,
  ]);
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

  const editTeamActionLabel = "编辑团队";
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
  const openTeamTestModal = React.useCallback(() => {
    setTeamTestModalOpen(true);
  }, []);
  const closeTeamTestModal = React.useCallback(() => {
    setTeamTestModalOpen(false);
  }, []);
  const streamTeamTest = React.useCallback(async (promptOverride?: string) => {
    const prompt = trimText(promptOverride) || teamTestPrompt.trim();
    if (!prompt || !scopeId || !selectedTeamId || isTeamArchived) {
      return;
    }

    teamTestAbortRef.current?.abort();
    const controller = new AbortController();
    teamTestAbortRef.current = controller;
    const accumulator = createRuntimeEventAccumulator();
    setTeamTestStatus("running");
    setTeamTestError(null);
    setTeamTestResultText("");

    try {
      const response = await runtimeRunsApi.streamTeamChat(
        scopeId,
        selectedTeamId,
        {
          prompt,
          metadata: {
            source: "team-detail",
            teamId: selectedTeamId,
          },
        },
        controller.signal,
      );

      for await (const event of parseBackendSSEStream(response, {
        signal: controller.signal,
      })) {
        applyRuntimeEvent(accumulator, event);
        setTeamTestResultText(
          accumulator.errorText ||
            accumulator.finalOutput ||
            accumulator.assistantText ||
            accumulator.thinking,
        );
      }

      if (controller.signal.aborted) {
        const stoppedSummary =
          accumulator.assistantText ||
          accumulator.finalOutput ||
          accumulator.errorText ||
          "Team Test stopped.";
        setTeamTestStatus("stopped");
        setTeamTestLastResult({
          finishedAtLabel: formatLocalTimeLabel(new Date()),
          runId: accumulator.runId || undefined,
          status: "stopped",
          summary: stoppedSummary,
        });
        return;
      }

      const summary =
        accumulator.errorText ||
        accumulator.finalOutput ||
        accumulator.assistantText ||
        "Team returned an empty response.";
      const nextStatus = accumulator.errorText ? "error" : "success";
      setTeamTestResultText(summary);
      setTeamTestStatus(nextStatus);
      if (accumulator.errorText) {
        setTeamTestError(describeTeamTestError(accumulator.errorText));
      }
      setTeamTestLastResult({
        finishedAtLabel: formatLocalTimeLabel(new Date()),
        runId: accumulator.runId || undefined,
        status: nextStatus,
        summary,
      });
    } catch (error) {
      if (controller.signal.aborted || isAbortLikeError(error)) {
        setTeamTestStatus("stopped");
        setTeamTestError(describeTeamTestError(error));
        setTeamTestLastResult({
          finishedAtLabel: formatLocalTimeLabel(new Date()),
          status: "stopped",
          summary: "Team Test stopped.",
        });
        return;
      }

      const errorDescription = describeTeamTestError(error);
      setTeamTestStatus("error");
      setTeamTestError(errorDescription);
      setTeamTestResultText(errorDescription.description);
      setTeamTestLastResult({
        finishedAtLabel: formatLocalTimeLabel(new Date()),
        status: "error",
        summary: errorDescription.description,
      });
    } finally {
      if (teamTestAbortRef.current === controller) {
        teamTestAbortRef.current = null;
      }
    }
  }, [isTeamArchived, scopeId, selectedTeamId, teamTestPrompt]);
  const handleStopTeamTest = React.useCallback(() => {
    teamTestAbortRef.current?.abort();
  }, []);
  const waitForTeamEntryVisibility = React.useCallback(
    async (memberId: string) => {
      const normalizedMemberId = trimText(memberId);
      if (!scopeId || !selectedTeamId || !normalizedMemberId) {
        return false;
      }

      for (let attempt = 0; attempt < teamEntryVisibilityAttempts; attempt += 1) {
        const summary = await queryClient.fetchQuery({
          queryFn: () => studioApi.getTeam(scopeId, selectedTeamId),
          queryKey: teamSummaryQueryKey,
          staleTime: 0,
        });
        if (hasTeamEntryMember(summary, normalizedMemberId)) {
          return true;
        }
        if (attempt < teamEntryVisibilityAttempts - 1) {
          await delay(teamEntryVisibilityRetryDelayMs);
        }
      }

      return false;
    },
    [queryClient, scopeId, selectedTeamId, teamSummaryQueryKey],
  );
  const handleSetEntry = React.useCallback(
    async (memberId: string, options?: { test?: boolean }) => {
      const normalizedMemberId = trimText(memberId);
      const promptSnapshot = teamTestPrompt.trim();
      if (!scopeId || !selectedTeamId || !normalizedMemberId) {
        return;
      }

      setEntryActionBusyMemberId(normalizedMemberId);
      setTeamTestStatus("setting-entry");
      setTeamTestError(null);
      try {
        const updatedTeam = await studioApi.setTeamEntryMember(
          scopeId,
          selectedTeamId,
          normalizedMemberId,
        );
        if (updatedTeam) {
          queryClient.setQueryData<StudioTeamSummary | undefined>(
            teamSummaryQueryKey,
            updatedTeam,
          );
        }
        void message.info("Team entry 变更已提交，正在等待同步确认。");
        await refreshTeamAuthority();
        if (options?.test) {
          const entryVisible = await waitForTeamEntryVisibility(normalizedMemberId);
          if (!entryVisible) {
            const errorDescription: TeamTestErrorDescription = {
              actionLabel: "Retry",
              description:
                "Team entry 已被后端受理，但读模型还没有确认新入口成员。请稍后重试测试团队。",
              kind: "entry_syncing",
              title: "Team entry 正在同步",
            };
            setTeamTestStatus("error");
            setTeamTestError(errorDescription);
            setTeamTestResultText(errorDescription.description);
            setTeamTestLastResult({
              finishedAtLabel: formatLocalTimeLabel(new Date()),
              status: "error",
              summary: errorDescription.description,
            });
            return;
          }
          await streamTeamTest(promptSnapshot);
        } else {
          setTeamTestStatus("idle");
        }
      } catch (error) {
        const errorDescription = describeTeamTestError(
          error,
          "Team entry update failed.",
        );
        setTeamTestStatus("error");
        setTeamTestError(errorDescription);
        void message.error(errorDescription.title);
      } finally {
        setEntryActionBusyMemberId("");
      }
    },
    [
      queryClient,
      refreshTeamAuthority,
      scopeId,
      selectedTeamId,
      streamTeamTest,
      teamTestPrompt,
      teamSummaryQueryKey,
      waitForTeamEntryVisibility,
    ],
  );
  const handleClearEntry = React.useCallback(async () => {
    if (!scopeId || !selectedTeamId) {
      return;
    }

    setEntryActionBusyMemberId(entryMemberClearingId);
    setTeamTestError(null);
    try {
      const updatedTeam = await studioApi.clearTeamEntryMember(scopeId, selectedTeamId);
      if (updatedTeam) {
        queryClient.setQueryData<StudioTeamSummary | undefined>(
          teamSummaryQueryKey,
          updatedTeam,
        );
      }
      void message.info("Team entry 清除已提交，正在等待同步确认。");
      await refreshTeamAuthority();
      setTeamTestStatus("idle");
    } catch (error) {
      const errorDescription = describeTeamTestError(
        error,
        "Team entry update failed.",
      );
      setTeamTestStatus("error");
      setTeamTestError(errorDescription);
      void message.error(errorDescription.title);
    } finally {
      setEntryActionBusyMemberId("");
    }
  }, [
    queryClient,
    entryMemberId,
    isTeamArchived,
    refreshTeamAuthority,
    scopeId,
    selectedTeamId,
    teamSummaryQueryKey,
  ]);
  const teamTestPanel = (
    <TeamTestPanel
      createMemberHref={createMemberHref}
      currentMemberId={currentMemberId || null}
      currentMemberLabel={currentMemberLabel}
      disabled={isTeamArchived}
      entryActionBusyMemberId={entryActionBusyMemberId}
      entryMemberId={teamSummaryQuery.data?.entryMemberId}
      error={teamTestError}
      lastResult={teamTestLastResult}
      onClearEntry={handleClearEntry}
      onNavigate={(href) => history.push(href)}
      onPromptChange={setTeamTestPrompt}
      onSetEntryAndTest={(memberId) => void handleSetEntry(memberId, { test: true })}
      onStop={handleStopTeamTest}
      onTest={() => void streamTeamTest()}
      prompt={teamTestPrompt}
      resultText={teamTestResultText}
      rosterError={teamMembersQuery.isError && !isTeamMembersProjectionSyncing}
      rosterLoading={teamMembersQuery.isLoading}
      rosterRows={teamRosterRows}
      rosterSyncing={isTeamMembersProjectionSyncing}
      status={teamTestStatus}
      teamId={selectedTeamId}
    />
  );

  const renderOverviewTab = () => {
    return (
      <TeamOverviewTab
        configurationDetailRows={configurationDetailRows}
        compositionRows={overviewCompositionRows}
        currentDeploymentPillStyle={resolveStatusPillStyle(token, currentDeploymentStatus)}
        currentDeploymentPillText={currentDeploymentPillText}
        currentHeaderStatusFriendly={currentHeaderStatusFriendly}
        currentHeaderStatusStyle={{
          background: token.colorFillQuaternary,
          color: token.colorTextSecondary,
        }}
        currentMemberCardCaption={currentMemberCardCaption}
        currentMemberCardTooltip={currentMemberCardTooltip}
        currentMemberLabel={currentMemberLabel}
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
        entryMemberId={entryMemberId || null}
        entryMemberLabel={entryMemberLabel}
        entryMemberUpdating={entryActionBusyMemberId === entryMemberClearingId}
        latestVisibleUpdateLabel={formatCompactTimestamp(latestVisibleUpdate)}
        latestVisibleUpdateNote={latestVisibleUpdateNote}
        nextStepDescription={teamNextStep.description}
        nextStepLabel={teamNextStep.label}
        nextStepTitle={teamNextStep.title}
        onClearEntryMember={
          teamSummaryQuery.data && !isTeamArchived && entryMemberId
            ? () => void handleClearEntry()
            : undefined
        }
      />
    );
  };

  const renderMembersTab = () => {
    return (
      <TeamMembersTab
        createMemberHref={createMemberHref}
        entryActionBusyMemberId={entryActionBusyMemberId}
        onClearEntry={
          teamSummaryQuery.data && !isTeamArchived && entryMemberId
            ? () => void handleClearEntry()
            : undefined
        }
        onNavigate={(href) => history.push(href)}
        onSetEntry={
          teamSummaryQuery.data && !isTeamArchived
            ? (memberId) => void handleSetEntry(memberId)
            : undefined
        }
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
          onOpenTeamTest={openTeamTestModal}
          testTeamDisabled={isTeamArchived}
          testTeamHint="归档后的 Team 不能继续发起测试。"
          testTeamLabel="测试团队"
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
        ) : lens.currentRun?.completionStatus ? (
          <DetailPill
            style={resolveStatusPillStyle(token, lens.currentRun.completionStatus)}
            text={formatFriendlyStatus(lens.currentRun.completionStatus)}
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
        footer={null}
        onCancel={closeTeamTestModal}
        open={teamTestModalOpen}
        title="测试团队"
        width={960}
        styles={{
          body: {
            maxHeight: "calc(100vh - 180px)",
            overflowY: "auto",
            padding: 0,
          },
        }}
      >
        <div data-testid="team-test-modal-body">{teamTestPanel}</div>
      </Modal>
      <Modal
        confirmLoading={teamEditorSaving}
        okButtonProps={{ disabled: !teamEditorName.trim() }}
        okText="保存团队"
        onCancel={closeTeamEditor}
        onOk={() => void saveTeamEditor()}
        open={teamEditorOpen}
        title="编辑团队"
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>团队名称</Typography.Text>
            <Input
              aria-label="编辑团队名称"
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorName(event.target.value)}
              value={teamEditorName}
            />
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>团队说明</Typography.Text>
            <Input.TextArea
              aria-label="编辑团队说明"
              autoSize={{ minRows: 3, maxRows: 5 }}
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorDescription(event.target.value)}
              value={teamEditorDescription}
            />
          </div>
          <Typography.Text type="secondary">
            这里更新的是 Team summary。即使团队已归档，仍然可以继续编辑和维护。
          </Typography.Text>
        </div>
      </Modal>
      <Modal
        confirmLoading={teamArchiving}
        okText="归档团队"
        okButtonProps={{ danger: true }}
        onCancel={closeTeamArchive}
        onOk={() => void confirmTeamArchive()}
        open={teamArchiveOpen}
        title="归档这支团队？"
      >
        <Typography.Text>
          归档后，这支 Team 会从活跃 roster 中降权显示，但你仍然可以继续编辑配置并查看历史。
        </Typography.Text>
      </Modal>
    </TeamDetailShell>
  );
};

export default TeamDetailPage;
