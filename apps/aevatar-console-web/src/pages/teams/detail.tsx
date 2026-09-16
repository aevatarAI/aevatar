import { Input, Modal, Space, Typography, message, theme } from "antd";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from "@/shared/agui/runtimeEventSemantics";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute } from "@/shared/navigation/scopeRoutes";
import {
  buildTeamDetailHref,
  buildTeamMemberAutomationsHref,
  buildTeamMemberInvokeHref,
  buildTeamMemberPublishedRunsHref,
  buildTeamMemberWorkflowStudioHref,
  readTeamDetailRouteState,
  type TeamDetailTab,
} from "@/shared/navigation/teamRoutes";
import { isStudioApiStatus, studioApi } from "@/shared/studio/api";
import {
  isStudioMemberNotFound,
  StudioMemberDeletionNotConfirmedError,
  waitForStudioMemberDeletion,
} from "@/shared/studio/memberDeletion";
import {
  formatStudioMemberLifecycleStage,
} from "@/shared/studio/models";
import type { StudioMemberRoster, StudioTeamSummary } from "@/shared/studio/models";
import type { ScopeWorkflowSummary } from "@/shared/models/scopes";
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
import TeamActivityTab from "./tabs/TeamActivityTab";
import TeamAutomationsTab from "./tabs/TeamAutomationsTab";
import TeamMembersTab, { type TeamMembersDeleteTarget } from "./tabs/TeamMembersTab";
import TeamOverviewTab from "./tabs/TeamOverviewTab";
import TeamWorkOrdersTab from "./tabs/TeamWorkOrdersTab";
import { resolveWorkflowOperationalUnit } from "./workflowOperationalUnits";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";
import { t } from "@/shared/i18n/messages";

const teamProjectionRetryLimit = 5;
const teamProjectionRetryBaseMs = 500;
const teamProjectionRetryMaxMs = 3_000;
const entryMemberClearingId = "__clear_entry_member__";
const teamEntryVisibilityAttempts = 5;
const teamEntryVisibilityRetryDelayMs = 100;
const emptyWorkflowSummaries: readonly ScopeWorkflowSummary[] = [];

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

function workflowMatchesBoundService(
  workflow: ScopeWorkflowSummary,
  service: {
    readonly activeServingRevisionId?: string | null;
    readonly defaultServingRevisionId?: string | null;
    readonly serviceKey?: string | null;
  } | null | undefined,
): boolean {
  if (!service) {
    return false;
  }

  const workflowServiceKey = trimText(workflow.serviceKey);
  if (
    workflowServiceKey &&
    trimText(service.serviceKey) === workflowServiceKey
  ) {
    return true;
  }

  const workflowRevisionId = trimText(workflow.activeRevisionId);
  if (!workflowRevisionId) {
    return false;
  }

  return (
    trimText(service.activeServingRevisionId) === workflowRevisionId ||
    trimText(service.defaultServingRevisionId) === workflowRevisionId
  );
}

function resolveRosterMemberWorkflowId(input: {
  readonly boundService?: {
    readonly activeServingRevisionId?: string | null;
    readonly defaultServingRevisionId?: string | null;
    readonly serviceKey?: string | null;
  } | null;
  readonly isBoundMember: boolean;
  readonly member: {
    readonly implementationRef?: {
      readonly implementationKind?: string | null;
      readonly workflowId?: string | null;
    } | null;
  };
  readonly workflows: readonly ScopeWorkflowSummary[];
}): string {
  const implementationRef = input.member.implementationRef;
  const implementationWorkflowId =
    trimText(implementationRef?.implementationKind).toLowerCase() ===
    "workflow"
      ? trimText(implementationRef?.workflowId)
      : "";
  if (!input.isBoundMember) {
    return implementationWorkflowId;
  }

  const matchedWorkflow =
    input.workflows.find((workflow) =>
      workflowMatchesBoundService(workflow, input.boundService),
    ) ?? null;
  return trimText(matchedWorkflow?.workflowId);
}

function isBoundWorkflowRosterMember(member: {
  readonly implementationKind?: string | null;
  readonly lifecycleStage?: string | null;
  readonly publishedServiceId?: string | null;
}): boolean {
  return (
    trimText(member.implementationKind).toLowerCase() === "workflow" &&
    normalizeStatus(member.lifecycleStage) === "bind_ready" &&
    trimText(member.publishedServiceId).length > 0
  );
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
  if (normalizedLensTitle === t("pages.teams.detail.copy", "Current team")) {
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
    title: t("pages.teams.detail.copy.2", "Team detail"),
  };
}

function formatTeamTabLabel(
  tab: TeamDetailTab,
  intl: ReturnType<typeof useIntl>,
): string {
  switch (tab) {
    case "activity":
      return intl.formatMessage({
        defaultMessage: "Activity",
        id: "teams.detail.tabs.activity",
      });
    case "automations":
      return intl.formatMessage({
        defaultMessage: "Automations",
        id: "teams.detail.tabs.automations",
      });
    case "members":
      return intl.formatMessage({ id: "teams.detail.tabs.members" });
    case "work-orders":
      return intl.formatMessage({ id: "teams.detail.tabs.workOrders" });
    default:
      return intl.formatMessage({ id: "teams.detail.tabs.overview" });
  }
}

function normalizeStatus(value: string | null | undefined): string {
  return trimText(value).toLowerCase();
}

function formatFriendlyStatus(
  value: string | null | undefined,
  intl: ReturnType<typeof useIntl>,
): string {
  const normalized = normalizeStatus(value);
  if (!normalized) {
    return "--";
  }

  switch (normalized) {
    case "active":
    case "running":
    case "processing":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.running" });
    case "published":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.published" });
    case "default":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.default" });
    case "completed":
    case "finished":
    case "succeeded":
    case "success":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.completed" });
    case "draft":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.draft" });
    case "retired":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.retired" });
    case "failed":
    case "error":
    case "cancelled":
    case "degraded":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.failed" });
    case "waiting":
    case "waiting_signal":
    case "waiting_approval":
    case "human_input":
    case "human_approval":
    case "suspended":
    case "blocked":
      return intl.formatMessage({ id: "teams.detail.runtimeStatus.waiting" });
    default:
      return trimText(value) || "--";
  }
}

function formatTeamLifecycleLabel(value: string | null | undefined): string {
  switch (normalizeStatus(value)) {
    case "active":
      return t("teams.detail.status.active", "Active");
    case "archived":
      return t("teams.detail.status.archived", "Archived");
    case "unknown":
      return t("teams.detail.status.unknown", "Unknown status");
    default:
      return trimText(value) || t("teams.detail.status.unknown", "Unknown status");
  }
}

function formatCompositionKind(kind: string | null | undefined): string {
  switch (normalizeStatus(kind)) {
    case "":
    case "unknown":
      return t("pages.teams.detail.copy.7", "Unrecognized");
    case "workflow role":
      return t("pages.teams.detail.copy.8", "Role");
    case "workflow":
      return t("pages.teams.detail.copy.9", "Workflow");
    case "service":
      return t("pages.teams.detail.copy.10", "Service");
    case "actor":
      return "Actor";
    case "runtime":
      return t("pages.teams.detail.copy.11", "Run");
    case "script":
      return t("pages.teams.detail.copy.12", "Script");
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
  return formatCompactDateTime(value, t("pages.teams.detail.copy.13", "None"));
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
  const intl = useIntl();
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
    () => buildTeamWorkspaceRoute(scopeId),
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
  const [deletingMemberId, setDeletingMemberId] = React.useState("");
  const [confirmedDeletedMemberIds, setConfirmedDeletedMemberIds] = React.useState(
    () => new Set<string>(),
  );
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
    setDeletingMemberId("");
    setConfirmedDeletedMemberIds(new Set());
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
  const teamRuntimeServiceIds = React.useMemo(() => {
    if (activeTab !== "overview" && activeTab !== "activity") {
      return [];
    }

    const members = teamMembersQuery.data?.members ?? [];
    const entryMemberId = trimText(teamSummaryQuery.data?.entryMemberId);
    if (entryMemberId) {
      const entryMember = members.find(
        (member) => trimText(member.memberId) === entryMemberId,
      );
      const entryServiceId = trimText(entryMember?.publishedServiceId);
      return entryServiceId ? [entryServiceId] : [];
    }

    if (teamSummaryQuery.isError) {
      return members
        .map((member) => trimText(member.publishedServiceId))
        .filter(Boolean);
    }

    return [];
  }, [
    activeTab,
    teamMembersQuery.data?.members,
    teamSummaryQuery.data?.entryMemberId,
    teamSummaryQuery.isError,
  ]);
  const hasExplicitRuntimeFocus = Boolean(
    trimText(preferredMemberId) || trimText(preferredServiceId) || trimText(preferredRunId),
  );
  const shouldLoadTeamRuntimeLens =
    hasTeamIdentity && (activeTab === "overview" || activeTab === "activity");
  const {
    lens,
    runsQuery,
    preferredMemberSummary,
    serviceRevisionsQuery,
    servicesQuery,
    workflowsQuery,
  } = useTeamRuntimeLens(scopeId, {
    allowScopeServiceFallback: false,
    enabled: shouldLoadTeamRuntimeLens,
    preferredMemberId,
    preferredRunId,
    preferredServiceId,
    teamMemberServiceIds: teamRuntimeServiceIds,
  });
  const automationsServicesQuery = useQuery({
    enabled: hasTeamIdentity && activeTab === "automations",
    queryFn: () =>
      scopeRuntimeApi.listServices(scopeId, {
        appId: "default",
      }),
    queryKey: ["teams", "services", scopeId],
    retry: false,
  });
  const hasBoundWorkflowMembersForStudioLinks = React.useMemo(
    () =>
      (teamMembersQuery.data?.members ?? []).some(isBoundWorkflowRosterMember),
    [teamMembersQuery.data?.members],
  );
  const shouldLoadMemberStudioLinkCatalog =
    hasTeamIdentity &&
    activeTab === "members" &&
    hasBoundWorkflowMembersForStudioLinks;
  const memberStudioLinkWorkflowsQuery = useQuery({
    enabled: shouldLoadMemberStudioLinkCatalog,
    queryFn: () => scopesApi.listWorkflows(scopeId),
    queryKey: ["teams", "workflows", scopeId],
    retry: false,
  });
  const memberStudioLinkServicesQuery = useQuery({
    enabled: shouldLoadMemberStudioLinkCatalog,
    queryFn: () =>
      scopeRuntimeApi.listServices(scopeId, {
        appId: "default",
      }),
    queryKey: ["teams", "services", scopeId],
    retry: false,
  });
  const workflowSummariesForMemberLinks =
    activeTab === "members"
      ? memberStudioLinkWorkflowsQuery.data ??
        workflowsQuery.data ??
        emptyWorkflowSummaries
      : workflowsQuery.data ?? emptyWorkflowSummaries;
  const isResolvingMemberStudioLinks =
    hasBoundWorkflowMembersForStudioLinks &&
    ((activeTab === "members" &&
      shouldLoadMemberStudioLinkCatalog &&
      (memberStudioLinkWorkflowsQuery.isLoading ||
        memberStudioLinkServicesQuery.isLoading)) ||
      (activeTab === "overview" &&
        shouldLoadTeamRuntimeLens &&
        (workflowsQuery.isLoading || servicesQuery.isLoading)));
  const automationServiceRuntimeByServiceId = React.useMemo(() => {
    const services =
      activeTab === "automations"
        ? automationsServicesQuery.data ?? servicesQuery.data ?? []
        : activeTab === "members"
          ? memberStudioLinkServicesQuery.data ?? servicesQuery.data ?? []
        : servicesQuery.data ?? [];
    return new Map(
      services
        .map((service) => [
          trimText(service.serviceId),
          {
            activeServingRevisionId: service.activeServingRevisionId,
            defaultServingRevisionId: service.defaultServingRevisionId,
            identity: {
              appId: service.appId,
              namespace: service.namespace,
              serviceId: service.serviceId,
              tenantId: service.tenantId,
            },
            serviceKey: service.serviceKey,
          },
        ] as const)
        .filter(([serviceId]) => serviceId.length > 0),
    );
  }, [
    activeTab,
    automationsServicesQuery.data,
    memberStudioLinkServicesQuery.data,
    servicesQuery.data,
  ]);

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
  const teamTitleMeta =
    teamSummaryQuery.data || teamSummaryDescription ? (
      <Space size={[10, 6]} wrap>
        {teamSummaryQuery.data ? (
          <span>
            {intl.formatMessage(
              { id: "teams.detail.meta.memberCount" },
              { count: teamSummaryQuery.data.memberCount },
            )}
          </span>
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
    trimText(entryMemberSummary?.displayName) ||
    (entryMemberId
      ? intl.formatMessage({ id: "teams.members.unnamed" })
      : "");
  const teamRosterRows = React.useMemo(
    () =>
      (teamMembersQuery.data?.members ?? [])
        .filter(
          (member) =>
            !confirmedDeletedMemberIds.has(trimText(member.memberId)),
        )
        .map((member) => {
        const isWorkflowMember =
          trimText(member.implementationKind).toLowerCase() === "workflow";
        const publishedServiceId = trimText(member.publishedServiceId);
        const isBoundMember =
          normalizeStatus(member.lifecycleStage) === "bind_ready" &&
          publishedServiceId.length > 0;
        const automationServiceRuntime =
          automationServiceRuntimeByServiceId.get(publishedServiceId);
        const memberDraftWorkflowId = resolveRosterMemberWorkflowId({
          boundService: automationServiceRuntime,
          isBoundMember,
          member,
          workflows: workflowSummariesForMemberLinks,
        });
        const canOpenWorkflowStudio =
          isWorkflowMember &&
          (!isBoundMember || memberDraftWorkflowId.length > 0);
        const workflowStudioHref = buildTeamMemberWorkflowStudioHref({
          memberId: member.memberId,
          mode: "edit-member",
          scopeId,
          teamId: selectedTeamId,
          workflowId: memberDraftWorkflowId || undefined,
          workflowSource:
            isWorkflowMember && isBoundMember && memberDraftWorkflowId
              ? "published"
              : undefined,
        });
        const memberInvokeHref = buildTeamMemberInvokeHref({
          memberId: member.memberId,
          scopeId,
          teamId: selectedTeamId,
        });
        const memberAutomationsHref = buildTeamMemberAutomationsHref({
          memberId: member.memberId,
          scopeId,
          teamId: selectedTeamId,
        });
        const memberPublishedRunsHref = buildTeamMemberPublishedRunsHref({
          memberId: member.memberId,
          scopeId,
          teamId: selectedTeamId,
        });

        return {
          buildStudioHref: canOpenWorkflowStudio ? workflowStudioHref : "",
          description: trimText(member.description),
          canInvokeAsEntry: isBoundMember,
          canInvokeMember: isWorkflowMember && isBoundMember,
          canOpenPublishedRuns: isWorkflowMember && isBoundMember,
          canSetAsEntry: Boolean(trimText(member.memberId)),
          editStudioHref: canOpenWorkflowStudio ? workflowStudioHref : "",
          automationsHref: memberAutomationsHref,
          implementationKind: formatCompositionKind(member.implementationKind),
          implementationKindRaw: trimText(member.implementationKind),
          invokeHref: isWorkflowMember ? memberInvokeHref : "",
          isServiceBound: isBoundMember,
          key: member.memberId,
          lifecycleLabel: formatStudioMemberLifecycleStage(member.lifecycleStage),
          lifecycleStyle: resolveStatusPillStyle(token, member.lifecycleStage),
          isEntryMember: trimText(member.memberId) === entryMemberId,
          isSelectedMember: trimText(member.memberId) === selectedRosterMemberId,
          memberId: member.memberId,
          name:
            trimText(member.displayName) ||
            intl.formatMessage({ id: "teams.members.unnamed" }),
          publishedRunsDisabledReason: !isWorkflowMember
            ? t("teams.members.actions.workflowOnlyTitle", "This console currently supports workflow members only.")
            : !isBoundMember
              ? t("teams.members.actions.publishedRuns.publishFirst", "Publish this member before viewing published runs.")
              : "",
          publishedRunsHref: memberPublishedRunsHref,
          publishedServiceId,
          serviceId: publishedServiceId || "--",
          serviceIdentity: automationServiceRuntime?.identity,
          serviceRevisionId:
            trimText(automationServiceRuntime?.activeServingRevisionId) ||
            trimText(automationServiceRuntime?.defaultServingRevisionId),
          studioHref: canOpenWorkflowStudio ? workflowStudioHref : "",
          studioHrefDisabledReason:
            isWorkflowMember && isBoundMember && !memberDraftWorkflowId
              ? t("teams.detail.overview.composition.actions.workflowResolving", "Resolving the published workflow link.")
              : "",
          canAutomateMember: isWorkflowMember && isBoundMember,
          automationDisabledReason: !isWorkflowMember
            ? t("teams.automations.member.workflowOnly", "Only workflow members can have recurring work.")
            : !isBoundMember
              ? t("teams.automations.member.publishFirst", "Publish this member before adding recurring work.")
              : "",
          workflowSupported: isWorkflowMember,
        };
        }),
    [
      confirmedDeletedMemberIds,
      entryMemberId,
      selectedRosterMemberId,
      scopeId,
      selectedTeamId,
      routeState.memberId,
      routeState.workflowId,
      automationServiceRuntimeByServiceId,
      teamMembersQuery.data?.members,
      token,
      workflowSummariesForMemberLinks,
    ],
  );
  const createMemberHref = React.useMemo(
    () =>
      buildTeamMemberWorkflowStudioHref({
        mode: "create-member",
        scopeId,
        teamId: selectedTeamId,
      }),
    [scopeId, selectedTeamId],
  );
  const latestVisibleUpdate =
    teamSummaryQuery.data?.updatedAt ||
    lens.currentRun?.lastUpdatedAt ||
    activeWorkflowSummary?.updatedAt ||
    "";
  const latestVisibleUpdateNote = teamSummaryQuery.data?.updatedAt
    ? t("pages.teams.detail.team", "From Team update time")
    : lens.currentRun?.lastUpdatedAt
      ? trimText(lens.currentRun?.runId)
      ? t("pages.teams.detail.run", "From latest run")
      : t("pages.teams.detail.copy.15", "From latest visible run")
      : activeWorkflowSummary?.updatedAt
        ? t("pages.teams.detail.workflow", "From workflow update time")
        : t("pages.teams.detail.copy.16", "No visible update time yet");
  const activeRunId =
    lens.currentRun?.runId ||
    focusedOperationalUnit?.latestRun?.runId ||
    "";
  const hasVisibleRun = Boolean(activeRunId);
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
  const currentReadModelFreshnessLabel = teamSummaryQuery.data
    ? `ReadModel · ${formatCompactTimestamp(latestVisibleUpdate)}`
    : t("pages.teams.detail.readmodel", "ReadModel is not visible yet");
  const currentRevisionFriendly = formatFriendlyStatus(currentRevisionStatus, intl);
  const currentDeploymentFriendly = formatFriendlyStatus(currentDeploymentStatus, intl);
  const currentServiceKey =
    trimText(lens.currentService?.serviceKey) ||
    trimText(activeWorkflowSummary?.serviceKey) ||
    "--";
  const currentServiceDisplayName =
    trimText(lens.currentService?.displayName) || "--";
  const currentRunStatus = trimText(lens.currentRun?.completionStatus) || "--";
  const currentRunFriendly = hasVisibleRun
    ? formatFriendlyStatus(currentRunStatus, intl)
    : t("pages.teams.detail.copy.17", "Waiting for first test");
  const currentMemberLabel =
    trimText(preferredMemberSummary?.displayName) ||
    teamRosterRows.find((row) => row.memberId === currentMemberId)?.name ||
    "--";
  const currentMemberCardTooltip = currentMemberId
    ? t("teams.detail.overview.member.selectedCaption", "Selected from this team's members.")
    : t("pages.teams.detail.copy.19", "No member selected yet");
  const currentServiceFriendly =
    currentServiceDisplayName !== "--"
      ? currentServiceDisplayName
      : runtimeServiceId
        ? t("teams.detail.overview.service.boundFallback", "Bound service")
        : "--";
  const hasRunnableTeamEntry =
    Boolean(entryMemberId) ||
    Boolean(currentMemberId) ||
    currentServiceFriendly !== "--" ||
    currentServiceKey !== "--" ||
    Boolean(runtimeServiceId);
  const currentHeaderStatus = hasVisibleRun
    ? currentRunStatus
    : hasRunnableTeamEntry
      ? "waiting"
      : currentDeploymentStatus;
  const currentHeaderStatusFriendly = hasVisibleRun
    ? formatFriendlyStatus(currentRunStatus, intl)
    : hasRunnableTeamEntry
      ? t("pages.teams.detail.copy.20", "Waiting for first test")
      : formatFriendlyStatus(currentDeploymentStatus, intl);
  const currentVersionFriendly =
    currentRevisionFriendly !== "--"
      ? currentRevisionFriendly
      : currentDeploymentFriendly;
  const currentServicePillText =
    currentServiceFriendly !== "--"
      ? t("pages.teams.detail.copy.21", "Services ·{value1}", { value1: currentServiceFriendly })
      : t("pages.teams.detail.copy.22", "Service to be configured");
  const currentDeploymentPillText =
    currentVersionFriendly !== "--"
      ? t("pages.teams.detail.copy.23", "Version ·{value1}", { value1: currentVersionFriendly })
      : t("pages.teams.detail.copy.24", "Version to be confirmed");
  const currentRunPillText = hasVisibleRun
    ? t("pages.teams.detail.copy.25", "Run ·{value1}", { value1: currentRunFriendly })
    : t("pages.teams.detail.copy.26", "Next steps · Test team");
  const currentServiceCardTooltip = runtimeServiceId
    ? t("teams.detail.overview.service.boundCaption", "Traffic is routed through the bound service.")
    : currentServiceKey !== "--" && currentServiceKey !== currentServiceFriendly
      ? t("teams.detail.overview.service.configuredCaption", "Service routing is configured.")
      : t("pages.teams.detail.copy.27", "No service is visible yet.");
  const currentRunCardTooltip = hasVisibleRun
    ? t("teams.detail.overview.run.visibleCaption", "Latest run is available.")
    : t("pages.teams.detail.copy.29", "The latest runs will be displayed here after the testing team.");
  const workflowNameValue =
    trimText(activeWorkflowSummary?.workflowName) ||
    trimText(lens.activeRevision?.workflowName) ||
    "--";
  const configurationDetailRows = React.useMemo(
    () => [
      {
        label: t("teams.detail.overview.configuration.workflow", "Team workflow"),
        note: activeWorkflowId
          ? t("teams.detail.overview.configuration.workflowLinked", "Workflow draft is linked.")
          : t("teams.detail.overview.configuration.workflowPending", "Workflow draft is not linked yet."),
        value: workflowNameValue !== "--" ? workflowNameValue : teamTitle,
      },
      {
        label: t("teams.detail.overview.configuration.primaryService", "Primary service entry"),
        note: runtimeServiceId || currentServiceKey !== "--"
          ? t("teams.detail.overview.service.configuredCaption", "Service routing is configured.")
          : t("pages.teams.detail.copy.37", "Currently, the main service entrance has not been matched."),
        value: currentServiceFriendly,
      },
      {
        label: t("teams.detail.overview.configuration.versionStatus", "Version status"),
        note:
          currentRevisionId !== "--"
            ? t("teams.detail.overview.configuration.versionAvailable", "Current serving version is available.")
            : t("teams.detail.overview.configuration.versionPending", "Serving version is pending."),
        value: currentVersionFriendly,
      },
    ],
    [
      activeWorkflowId,
      currentRevisionId,
      currentServiceFriendly,
      currentServiceKey,
      currentVersionFriendly,
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
        summary:
          row.description ||
          (row.isServiceBound
            ? t("teams.detail.overview.composition.memberReady", "Bound and ready to receive traffic.")
            : t("teams.detail.overview.composition.memberDraft", "Not bound yet.")),
      }));
    }

    if (!hasExplicitRuntimeFocus) {
      return [];
    }

    return [
      {
        key: "fallback-workflow",
        kind: "workflow",
        name: t("pages.teams.detail.copy.41", "team process"),
        summary: workflowNameValue !== "--" ? workflowNameValue : activeWorkflowId || "--",
      },
      {
        key: "fallback-actor",
        kind: "actor",
        name: t("pages.teams.detail.copy.42", "current execution"),
        summary: hasVisibleRun ? currentRunFriendly : t("pages.teams.detail.copy.43", "After the test team, the most recent runs will be displayed."),
      },
      {
        key: "fallback-service",
        kind: "service",
        name: t("pages.teams.detail.copy.44", "main service"),
        summary: currentServiceFriendly,
      },
    ];
  }, [
    activeWorkflowId,
    currentRunFriendly,
    currentServiceFriendly,
    hasExplicitRuntimeFocus,
    hasVisibleRun,
    teamRosterRows,
    workflowNameValue,
  ]);
  const overviewCompositionRows = React.useMemo(
    () => {
      if (teamRosterRows.length > 0) {
        return teamRosterRows.map((row) => {
          const serviceLabel = row.isServiceBound
            ? t("teams.detail.overview.composition.serviceBound", "Service · {serviceId}", {
                serviceId: row.serviceId,
              })
            : t("teams.detail.overview.composition.serviceUnbound", "Service not bound");
          const runDisabledReason = !row.workflowSupported
            ? t("teams.members.actions.workflowOnlyTitle", "Only workflow members can use this action.")
            : !row.isServiceBound
              ? t("teams.members.actions.invokeRequiresBinding", "Bind this member to a published service before invoking it.")
              : "";
          const configureLabel = row.isServiceBound
            ? t("teams.detail.overview.composition.actions.workflow", "Workflow")
            : t("teams.detail.overview.composition.actions.bindService", "Bind service");

          return {
            canRun: row.canInvokeMember,
            canConfigure: row.workflowSupported && Boolean(row.studioHref),
            configureDisabledReason: row.studioHrefDisabledReason,
            configureHref: row.workflowSupported ? row.studioHref : "",
            configureLabel,
            entryLabel: row.isEntryMember
              ? intl.formatMessage({ id: "teams.members.entry" })
              : "",
            key: row.key,
            kindLabel: row.implementationKind,
            kindStyle: resolveCompositionKindPillStyle(
              token,
              row.implementationKindRaw,
            ),
            name: row.name,
            runDisabledReason,
            runHref: row.canInvokeMember ? row.invokeHref : "",
            selectedLabel:
              row.isSelectedMember && !row.isEntryMember
                ? intl.formatMessage({ id: "teams.members.selected" })
                : "",
            serviceLabel,
            statusLabel: row.lifecycleLabel,
            statusStyle: row.lifecycleStyle,
            summary: row.description,
          };
        });
      }

      return compositionDisplayRows.map((row) => ({
        key: row.key,
        kindLabel: formatCompositionKind(row.kind),
        kindStyle: resolveCompositionKindPillStyle(token, row.kind),
        name: row.name,
        summary: row.summary,
      }));
    },
    [compositionDisplayRows, intl, teamRosterRows, token],
  );
  const entryRosterRow = React.useMemo(
    () =>
      entryMemberId
        ? teamRosterRows.find((row) => row.memberId === entryMemberId) ?? null
        : null,
    [entryMemberId, teamRosterRows],
  );
  const isTeamArchived = normalizeStatus(teamSummaryQuery.data?.lifecycleStage) === "archived";
  const canRunTeamFromOverview = Boolean(
    !isTeamArchived && entryRosterRow?.canInvokeAsEntry,
  );
  const teamRunDisabledReason = isTeamArchived
    ? intl.formatMessage({ id: "teams.detail.test.archivedHint" })
    : !entryMemberId
      ? intl.formatMessage({ id: "teams.detail.test.entry.noneSelected" })
      : !entryRosterRow?.canInvokeAsEntry
        ? intl.formatMessage({ id: "teams.detail.test.entry.configuredNeedsBinding" })
        : "";
  const recentRunRows = React.useMemo(
    () =>
      (runsQuery.data?.runs ?? []).map((run) => {
        const runServiceId = trimText(run.serviceId);
        const matchedMember = teamRosterRows.find(
          (row) => trimText(row.publishedServiceId) === runServiceId,
        );
        const detailsHref = matchedMember?.canOpenPublishedRuns
          ? buildTeamMemberPublishedRunsHref({
              actorId: trimText(run.actorId) || undefined,
              memberId: matchedMember.memberId,
              runId: run.runId,
              scopeId,
              teamId: selectedTeamId,
            })
          : "";

        return {
          detailsHref,
          detailItems: [
            {
              label: t("teams.detail.overview.history.details.run", "Run"),
              value: run.runId,
            },
            {
              label: t("teams.detail.overview.history.details.service", "Service"),
              value:
                runServiceId ||
                t("teams.detail.overview.history.serviceUnknown", "Service unknown"),
            },
            {
              label: t("teams.detail.overview.history.details.revision", "Revision"),
              value:
                trimText(run.revisionId) ||
                t("teams.detail.overview.history.revisionUnknown", "Revision unknown"),
            },
          ],
          detailTooltipLabel: t(
            "teams.detail.overview.history.details.tooltip",
            "Run technical details",
          ),
          memberLabel:
            matchedMember?.name ||
            t("teams.detail.overview.history.memberUnknown", "Unknown member"),
          outputPreview:
            trimText(run.lastError) ||
            trimText(run.lastOutput) ||
            t("teams.detail.overview.history.noOutput", "No output snapshot captured yet."),
          runId: run.runId,
          statusKey: trimText(run.completionStatus),
          statusLabel: formatFriendlyStatus(run.completionStatus, intl),
          statusStyle: resolveStatusPillStyle(token, run.completionStatus),
          updatedLabel: formatCompactTimestamp(run.lastUpdatedAt),
          workflowLabel:
            trimText(run.workflowName) ||
            t("teams.detail.overview.history.workflowUnknown", "Workflow unknown"),
          workflowMetaLabel: t(
            "teams.detail.overview.history.workflowMeta",
            "Workflow · {workflowLabel}",
            {
              workflowLabel:
                trimText(run.workflowName) ||
                t("teams.detail.overview.history.workflowUnknown", "Workflow unknown"),
            },
          ),
        };
      }),
    [
      intl,
      runsQuery.data?.runs,
      scopeId,
      selectedTeamId,
      teamRosterRows,
      token,
    ],
  );
  const tabOptions: TeamTabOption[] = [
    { label: t("pages.teams.detail.copy.45", "Overview"), value: "overview" },
    { label: t("teams.detail.tabs.activity", "Activity"), value: "activity" },
    { label: t("teams.detail.tabs.automations", "Automations"), value: "automations" },
    { label: t("teams.detail.tabs.workOrders", "Requests"), value: "work-orders" },
    { label: t("pages.teams.detail.copy.46", "Team members"), value: "members" },
  ];

  const initialLoading =
    serviceRevisionsQuery.isLoading ||
    servicesQuery.isLoading ||
    workflowsQuery.isLoading;

  const pushTeamTab = React.useCallback(
    (tab: TeamDetailTab) => {
      const includeRuntimeContext = tab === "overview" || tab === "activity";
      setActiveTab(tab);
      history.push(
        buildTeamDetailHref({
          memberId: currentMemberId || undefined,
          scopeId,
          teamId: selectedTeamId || undefined,
          workflowId: includeRuntimeContext ? activeWorkflowId || undefined : undefined,
          serviceId: includeRuntimeContext ? runtimeServiceId : undefined,
          runId:
            includeRuntimeContext
              ? preferredRunId ||
                lens.currentRun?.runId ||
                undefined
              : undefined,
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

  const editTeamActionLabel = intl.formatMessage({ id: "teams.detail.actions.edit" });
  const canEditSelectedTeam = Boolean(teamSummaryQuery.data && selectedTeamId);
  const editTeamHint = selectedTeamId
    ? intl.formatMessage({ id: "teams.detail.edit.hint.ready" })
    : intl.formatMessage({ id: "teams.detail.edit.hint.noTeam" });
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
      void message.error(intl.formatMessage({ id: "teams.detail.messages.nameRequired" }));
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
      void message.success(intl.formatMessage({ id: "teams.detail.messages.updateSuccess" }));
      setTeamEditorOpen(false);
      void refreshTeamAuthority().catch(() => undefined);
    } catch (error) {
      void message.error(
        describeError(error, intl.formatMessage({ id: "teams.detail.messages.updateFailed" })),
      );
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
    intl,
  ]);
  const archiveTeamActionLabel =
    teamSummaryQuery.data && !isTeamArchived
      ? intl.formatMessage({ id: "teams.detail.actions.archive" })
      : "";
  const archiveTeamHint = selectedTeamId
    ? intl.formatMessage({ id: "teams.detail.archive.hint.ready" })
    : intl.formatMessage({ id: "teams.detail.archive.hint.noTeam" });
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
      void message.success(intl.formatMessage({ id: "teams.detail.messages.archiveSuccess" }));
      setTeamArchiveOpen(false);
      void refreshTeamAuthority().catch(() => undefined);
    } catch (error) {
      void message.error(
        describeError(error, intl.formatMessage({ id: "teams.detail.messages.archiveFailed" })),
      );
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
    intl,
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
          intl.formatMessage({ id: "teams.detail.messages.teamTestStopped" });
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
        intl.formatMessage({ id: "teams.detail.messages.teamTestEmpty" });
      const nextStatus = accumulator.errorText ? "error" : "success";
      setTeamTestResultText(summary);
      setTeamTestStatus(nextStatus);
      if (accumulator.errorText) {
        setTeamTestError(
          describeTeamTestError(
            accumulator.errorText,
            intl.formatMessage({ id: "teams.detail.test.errors.failed" }),
            (id, values) => intl.formatMessage({ id }, values),
          ),
        );
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
        setTeamTestError(
          describeTeamTestError(
            error,
            intl.formatMessage({ id: "teams.detail.test.errors.failed" }),
            (id, values) => intl.formatMessage({ id }, values),
          ),
        );
        setTeamTestLastResult({
          finishedAtLabel: formatLocalTimeLabel(new Date()),
          status: "stopped",
          summary: intl.formatMessage({ id: "teams.detail.messages.teamTestStopped" }),
        });
        return;
      }

      const errorDescription = describeTeamTestError(
        error,
        intl.formatMessage({ id: "teams.detail.test.errors.failed" }),
        (id, values) => intl.formatMessage({ id }, values),
      );
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
  }, [intl, isTeamArchived, scopeId, selectedTeamId, teamTestPrompt]);
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
        void message.info(
          intl.formatMessage({ id: "teams.detail.messages.entrySetSubmitted" }),
        );
        await refreshTeamAuthority();
        if (options?.test) {
          const entryVisible = await waitForTeamEntryVisibility(normalizedMemberId);
          if (!entryVisible) {
            const errorDescription: TeamTestErrorDescription = {
              actionLabel: intl.formatMessage({ id: "teams.detail.test.entrySyncing.action" }),
              description:
                intl.formatMessage({ id: "teams.detail.test.entrySyncing.description" }),
              kind: "entry_syncing",
              title: intl.formatMessage({ id: "teams.detail.test.entrySyncing.title" }),
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
          intl.formatMessage({ id: "teams.detail.messages.entrySetFailed" }),
          (id, values) => intl.formatMessage({ id }, values),
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
      intl,
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
      void message.info(
        intl.formatMessage({ id: "teams.detail.messages.entryClearSubmitted" }),
      );
      await refreshTeamAuthority();
      setTeamTestStatus("idle");
    } catch (error) {
      const errorDescription = describeTeamTestError(
        error,
        intl.formatMessage({ id: "teams.detail.messages.entryClearFailed" }),
        (id, values) => intl.formatMessage({ id }, values),
      );
      setTeamTestStatus("error");
      setTeamTestError(errorDescription);
      void message.error(errorDescription.title);
    } finally {
      setEntryActionBusyMemberId("");
    }
  }, [
    queryClient,
    intl,
    refreshTeamAuthority,
    scopeId,
    selectedTeamId,
    teamSummaryQueryKey,
  ]);

  const handleDeleteMember = React.useCallback(
    (target: TeamMembersDeleteTarget) => {
      const normalizedMemberId = trimText(target.memberId);
      if (!scopeId || !selectedTeamId || !normalizedMemberId) {
        return;
      }

      const memberLabel = trimText(target.name) || normalizedMemberId;

      Modal.confirm({
        autoFocusButton: "cancel",
        cancelText: t("teams.members.delete.keep", "Keep member"),
        centered: true,
        content: (
          <div style={{ display: "grid", gap: 12 }}>
            <Typography.Text>
              {t("teams.members.delete.confirm.body", "Delete")}{" "}
              <Typography.Text strong>{memberLabel}</Typography.Text>{" "}
              {t("teams.members.delete.confirm.body.suffix", "from this Team?")}
            </Typography.Text>
            <Typography.Text type="secondary">
              {target.isEntryMember
                ? t("teams.members.delete.entry.warning", "This member is the Team entry member. Deleting it removes the member authority and clears it from the Team roster; published service artifacts, revisions, and historical runs stay intact.")
                : t("teams.members.delete.warning", "This removes the Studio member authority and Team roster entry. Published service artifacts, revisions, and historical runs stay intact.")}
            </Typography.Text>
          </div>
        ),
        okButtonProps: { danger: true },
        okText: t("teams.members.actions.delete", "Delete member"),
        title: t("teams.members.delete.title", "Delete member"),
        onOk: async () => {
          setDeletingMemberId(normalizedMemberId);
          try {
            let alreadyDeleted = false;
            try {
              await studioApi.deleteMember({
                scopeId,
                memberId: normalizedMemberId,
              });
            } catch (error) {
              if (!isStudioMemberNotFound(error)) {
                throw error;
              }
              alreadyDeleted = true;
            }

            if (!alreadyDeleted) {
              void message.info(
                t(
                  "teams.members.delete.submitted",
                  "Deletion submitted. Waiting for confirmation.",
                ),
              );
            }
            if (!alreadyDeleted) {
              await waitForStudioMemberDeletion({
                scopeId,
                memberId: normalizedMemberId,
              });
            }
            setConfirmedDeletedMemberIds((current) => {
              const next = new Set(current);
              next.add(normalizedMemberId);
              return next;
            });
            await refreshTeamAuthority();
            queryClient.setQueryData<StudioMemberRoster | undefined>(
              teamMembersQueryKey,
              (current) =>
                current
                  ? {
                      ...current,
                      members: current.members.filter(
                        (member) =>
                          trimText(member.memberId) !== normalizedMemberId,
                      ),
                    }
                  : current,
            );
            if (
              trimText(routeState.memberId) === normalizedMemberId ||
              trimText(preferredMemberId) === normalizedMemberId
            ) {
              setPreferredMemberId("");
              setPreferredServiceId("");
              setPreferredRunId("");
              history.replace(
                buildTeamDetailHref({
                  scopeId,
                  teamId: selectedTeamId,
                  tab: "members",
                }),
              );
            }
            void message.success(
              t("teams.members.delete.success", "Deleted member {member}.", {
                member: memberLabel,
              }),
            );
          } catch (error) {
            void message.error(
              error instanceof StudioMemberDeletionNotConfirmedError
                ? t(
                    "teams.members.delete.notConfirmed",
                    "Deletion was not confirmed. The member remains in the list; refresh and retry.",
                  )
                : describeError(
                    error,
                    t("teams.members.delete.failed", "Failed to delete member."),
                  ),
            );
          } finally {
            setDeletingMemberId("");
          }
        },
      });
    },
    [
      preferredMemberId,
      queryClient,
      refreshTeamAuthority,
      routeState.memberId,
      scopeId,
      selectedTeamId,
      teamMembersQueryKey,
    ],
  );

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
      onSetEntry={(memberId, options) => void handleSetEntry(memberId, options)}
      onStop={handleStopTeamTest}
      onTest={() => void streamTeamTest()}
      prompt={teamTestPrompt}
      resultText={teamTestResultText}
      rosterError={teamMembersQuery.isError && !isTeamMembersProjectionSyncing}
      rosterLoading={teamMembersQuery.isLoading || isResolvingMemberStudioLinks}
      rosterRows={teamRosterRows}
      rosterSyncing={isTeamMembersProjectionSyncing}
      status={teamTestStatus}
    />
  );

  const renderOverviewTab = () => {
    return (
      <TeamOverviewTab
        configurationDetailRows={configurationDetailRows}
        compositionRows={overviewCompositionRows}
        currentDeploymentPillStyle={resolveStatusPillStyle(token, currentDeploymentStatus)}
        currentDeploymentPillText={currentDeploymentPillText}
        currentHeaderStatusFriendly={currentReadModelFreshnessLabel}
        currentHeaderStatusStyle={{
          background: token.colorFillQuaternary,
          color: token.colorTextSecondary,
        }}
        currentMemberCardTooltip={currentMemberCardTooltip}
        currentMemberLabel={currentMemberLabel}
        currentRunCardTooltip={currentRunCardTooltip}
        currentRunFriendly={currentRunFriendly}
        currentRunPillStyle={resolveStatusPillStyle(token, currentHeaderStatus)}
        currentRunPillText={currentRunPillText}
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
        latestRuns={recentRunRows.slice(0, 3)}
        latestVisibleUpdateLabel={formatCompactTimestamp(latestVisibleUpdate)}
        latestVisibleUpdateNote={latestVisibleUpdateNote}
        onClearEntryMember={
          teamSummaryQuery.data && !isTeamArchived && entryMemberId
            ? () => void handleClearEntry()
            : undefined
        }
        onNavigate={(href) => history.push(href)}
        onOpenTeamTest={openTeamTestModal}
        teamRunDisabled={!canRunTeamFromOverview}
        teamRunDisabledReason={teamRunDisabledReason}
      />
    );
  };

  const renderActivityTab = () => {
    return (
      <TeamActivityTab
        error={runsQuery.isError}
        loading={runsQuery.isLoading}
        onNavigate={(href) => history.push(href)}
        onOpenTeamTest={openTeamTestModal}
        onRefresh={() => void runsQuery.refetch()}
        refreshing={runsQuery.isFetching}
        runs={recentRunRows}
        teamRunDisabled={!canRunTeamFromOverview}
        teamRunDisabledReason={teamRunDisabledReason}
      />
    );
  };

  const renderMembersTab = () => {
    return (
      <TeamMembersTab
        createMemberHref={createMemberHref}
        deletingMemberId={deletingMemberId}
        entryActionBusyMemberId={entryActionBusyMemberId}
        onClearEntry={
          teamSummaryQuery.data && !isTeamArchived && entryMemberId
            ? () => void handleClearEntry()
            : undefined
        }
        onDeleteMember={
          teamSummaryQuery.data && !isTeamArchived
            ? (memberId) => handleDeleteMember(memberId)
            : undefined
        }
        onNavigate={(href) => history.push(href)}
        onSetEntry={
          teamSummaryQuery.data && !isTeamArchived
            ? (memberId) => void handleSetEntry(memberId)
            : undefined
        }
        rosterError={teamMembersQuery.isError && !isTeamMembersProjectionSyncing}
        rosterLoading={teamMembersQuery.isLoading || isResolvingMemberStudioLinks}
        rosterSyncing={isTeamMembersProjectionSyncing}
        rosterRows={teamRosterRows}
      />
    );
  };

  const renderAutomationsTab = () => {
    return (
      <TeamAutomationsTab
        key={JSON.stringify([
          scopeId,
          selectedTeamId,
          routeState.routeMemberId,
        ])}
        members={teamRosterRows.map((row) => ({
          canAutomateMember: row.canAutomateMember,
          disabledReason: row.automationDisabledReason,
          implementationKind: row.implementationKind,
          key: row.key,
          lifecycleLabel: row.lifecycleLabel,
          lifecycleStyle: row.lifecycleStyle,
          memberId: row.memberId,
          name: row.name,
          serviceId: row.serviceId,
          serviceIdentity: row.serviceIdentity,
          serviceRevisionId: row.serviceRevisionId,
          workflowSupported: row.workflowSupported,
        }))}
        routeMemberId={routeState.routeMemberId}
        scopeId={scopeId}
        serviceIdentitiesLoading={automationsServicesQuery.isLoading}
        teamId={selectedTeamId}
      />
    );
  };

  const renderWorkOrdersTab = () => (
    <TeamWorkOrdersTab
      onNavigate={(href) => history.push(href)}
      scopeId={scopeId}
      teamId={selectedTeamId}
    />
  );

  let tabContent: React.ReactNode;
  switch (activeTab) {
    case "activity":
      tabContent = renderActivityTab();
      break;
    case "automations":
      tabContent = renderAutomationsTab();
      break;
    case "members":
      tabContent = renderMembersTab();
      break;
    case "work-orders":
      tabContent = renderWorkOrdersTab();
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
        testTeamHint={intl.formatMessage({ id: "teams.detail.test.archivedHint" })}
        testTeamLabel={intl.formatMessage({ id: "teams.detail.actions.test" })}
      />
      }
      activeTab={activeTab}
      activeTabLabel={formatTeamTabLabel(activeTab, intl)}
      breadcrumbTeamTitle={teamTitle}
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
            text={formatFriendlyStatus(lens.currentRun.completionStatus, intl)}
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
        title={intl.formatMessage({ id: "teams.detail.test.modal.title" })}
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
        okText={intl.formatMessage({ id: "teams.detail.edit.modal.save" })}
        onCancel={closeTeamEditor}
        onOk={() => void saveTeamEditor()}
        open={teamEditorOpen}
        title={intl.formatMessage({ id: "teams.detail.edit.modal.title" })}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>
              {intl.formatMessage({ id: "teams.detail.edit.modal.name" })}
            </Typography.Text>
            <Input
              aria-label={intl.formatMessage({ id: "teams.detail.edit.modal.nameAria" })}
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorName(event.target.value)}
              value={teamEditorName}
            />
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            <Typography.Text strong>
              {intl.formatMessage({ id: "teams.detail.edit.modal.description" })}
            </Typography.Text>
            <Input.TextArea
              aria-label={intl.formatMessage({ id: "teams.detail.edit.modal.descriptionAria" })}
              autoSize={{ minRows: 3, maxRows: 5 }}
              disabled={teamEditorSaving}
              onChange={(event) => setTeamEditorDescription(event.target.value)}
              value={teamEditorDescription}
            />
          </div>
          <Typography.Text type="secondary">
            {intl.formatMessage({ id: "teams.detail.edit.modal.help" })}
          </Typography.Text>
        </div>
      </Modal>
      <Modal
        confirmLoading={teamArchiving}
        okText={intl.formatMessage({ id: "teams.detail.actions.archive" })}
        okButtonProps={{ danger: true }}
        onCancel={closeTeamArchive}
        onOk={() => void confirmTeamArchive()}
        open={teamArchiveOpen}
        title={intl.formatMessage({ id: "teams.detail.archive.modal.title" })}
      >
        <Typography.Text>
          {intl.formatMessage({ id: "teams.detail.archive.modal.content" })}
        </Typography.Text>
      </Modal>
    </TeamDetailShell>
  );
};

export default TeamDetailPage;
