import {
  AppstoreOutlined,
  BarsOutlined,
  MoreOutlined,
  PlusOutlined,
} from "@ant-design/icons";
import { useQueries, useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Dropdown,
  Empty,
  Space,
  Tooltip,
  Typography,
  theme,
} from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { ensureActiveAuthSession } from "@/shared/auth/client";
import { getNyxIDRuntimeConfig } from "@/shared/auth/config";
import { loadRestorableAuthSession } from "@/shared/auth/session";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  formatStudioMemberLifecycleStage,
  type StudioMemberSummary,
} from "@/shared/studio/models";
import {
  findStudioMemberServiceIdInCatalog,
  resolveStudioMemberRuntimeServiceId,
} from "@/shared/studio/memberRuntime";
import {
  buildStudioRoute,
} from "@/shared/studio/navigation";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { describeError } from "@/shared/ui/errorText";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import ScopeQueryCard from "../scopes/components/ScopeQueryCard";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "../scopes/components/scopeQuery";
import {
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalAttention,
} from "./workflowOperationalUnits";

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";
const compactTeamRosterThreshold = 6;

type MemberRosterPreview = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly detailHref: string;
  readonly entryLabel: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberId: string;
  readonly moreActions: Array<{ key: string; label: string; onClick: () => void }>;
  readonly primaryActionLabel: string;
  readonly serviceId: string;
  readonly serviceLabel: string;
  readonly title: string;
  readonly updatedAt: string | null;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function isPlaceholderTeamLabel(value: string | null | undefined): boolean {
  const normalized = trimOptional(value).toLowerCase();
  if (!normalized) {
    return true;
  }

  return ["not configured", "unconfigured", "unknown", "n/a"].includes(normalized);
}

function pickMeaningfulLabel(
  ...candidates: Array<string | null | undefined>
): string {
  for (const candidate of candidates) {
    const normalized = trimOptional(candidate);
    if (normalized && !isPlaceholderTeamLabel(normalized)) {
      return normalized;
    }
  }

  return "";
}

function formatRunStatusLabel(status: string | null | undefined): string {
  switch (trimOptional(status).toLowerCase()) {
    case "waiting":
    case "waiting_approval":
    case "waiting_signal":
      return "待关注";
    case "failed":
    case "error":
      return "异常";
    case "completed":
      return "稳定";
    default:
      return trimOptional(status) || "未知";
  }
}

function formatOperationalStatusLabel(
  status: string | null | undefined,
  attention: WorkflowOperationalAttention,
): string {
  const normalizedStatus = trimOptional(status);
  if (normalizedStatus) {
    return formatRunStatusLabel(normalizedStatus);
  }

  switch (attention) {
    case "healthy":
      return "运行中";
    case "waiting":
      return "待关注";
    case "failed":
      return "异常";
    case "draft":
      return "草稿中";
    case "no-bound-service":
      return "待绑定";
    case "no-recent-runs":
      return "待运行";
    case "runtime-unresolved":
      return "待确认";
    default:
      return "未知";
  }
}

function formatAttentionLabel(attention: WorkflowOperationalAttention): string {
  switch (attention) {
    case "failed":
      return "待处理";
    case "waiting":
      return "待关注";
    case "healthy":
      return "运行中";
    case "draft":
      return "草稿中";
    case "no-bound-service":
      return "待绑定";
    case "no-recent-runs":
      return "待运行";
    default:
      return "待确认";
  }
}

function resolveAttentionPillStyle(
  token: ReturnType<typeof theme.useToken>["token"],
  attention: WorkflowOperationalAttention,
): React.CSSProperties {
  switch (attention) {
    case "healthy":
      return {
        background: "rgba(24, 144, 255, 0.08)",
        color: token.colorInfo,
      };
    case "waiting":
    case "no-bound-service":
    case "no-recent-runs":
      return {
        background: "rgba(250, 173, 20, 0.12)",
        color: token.colorWarning,
      };
    case "failed":
      return {
        background: "rgba(255, 77, 79, 0.12)",
        color: token.colorError,
      };
    case "draft":
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
    default:
      return {
        background: token.colorFillQuaternary,
        color: token.colorTextSecondary,
      };
  }
}

function formatShortTime(value: string | null | undefined): string {
  return formatCompactDateTime(value, "--");
}

function parseTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

function normalizeStatus(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function compareRuns(
  left: ScopeServiceRunSummary,
  right: ScopeServiceRunSummary,
): number {
  const rightTime = parseTimestamp(right.lastUpdatedAt);
  const leftTime = parseTimestamp(left.lastUpdatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  if (right.stateVersion !== left.stateVersion) {
    return right.stateVersion - left.stateVersion;
  }

  return right.runId.localeCompare(left.runId);
}

function isSuccessfulRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === true) {
    return true;
  }

  return ["completed", "finished", "success", "succeeded"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function isWaitingRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  return [
    "waiting",
    "waiting_approval",
    "waiting_signal",
    "blocked",
    "human_approval",
    "human_input",
    "suspended",
  ].includes(normalizeStatus(run.completionStatus));
}

function isFailedRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (isWaitingRun(run)) {
    return false;
  }

  if (run.lastSuccess === false) {
    return true;
  }

  return ["failed", "error", "stopped", "timed_out", "timedout"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function stopEvent<T extends (...args: any[]) => void>(handler: T): T {
  return ((event: React.MouseEvent<HTMLElement>) => {
    event.stopPropagation();
    handler();
  }) as T;
}

const SummaryStatCard: React.FC<{
  readonly accent?: boolean;
  readonly label: string;
  readonly value: React.ReactNode;
}> = ({ accent = false, label, value }) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 22,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minHeight: 104,
        padding: 18,
      }}
    >
      <Typography.Title
        level={2}
        style={{
          color: accent ? token.colorPrimary : token.colorText,
          fontSize: 24,
          margin: 0,
        }}
      >
        {value}
      </Typography.Title>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        {label}
      </Typography.Text>
    </div>
  );
};

const TeamFact: React.FC<{
  readonly label: string;
  readonly value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
    }}
  >
    <Typography.Text
      strong
      style={{
        fontSize: 16,
        margin: 0,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </Typography.Text>
    <Typography.Text style={{ fontSize: 13 }} type="secondary">
      {label}
    </Typography.Text>
  </div>
);

function compareMembers(
  left: StudioMemberSummary,
  right: StudioMemberSummary,
): number {
  const rightTime = parseTimestamp(right.updatedAt);
  const leftTime = parseTimestamp(left.updatedAt);
  if (rightTime !== leftTime) {
    return rightTime - leftTime;
  }

  return right.memberId.localeCompare(left.memberId);
}

function resolveMemberPreviewService(input: {
  readonly member: StudioMemberSummary;
  readonly services: readonly ServiceCatalogSnapshot[];
}): ServiceCatalogSnapshot | null {
  const boundServiceId = findStudioMemberServiceIdInCatalog(
    input.member,
    input.services,
  );
  if (!boundServiceId) {
    return null;
  }

  return (
    input.services.find(
      (service) => trimOptional(service.serviceId) === boundServiceId,
    ) ?? null
  );
}

function resolveRuntimeUnavailable(input: {
  readonly memberId: string;
  readonly runtimeAvailableByMemberId?: ReadonlySet<string>;
  readonly runtimeGuardrailedMemberIds?: ReadonlySet<string>;
}): boolean {
  const memberId = trimOptional(input.memberId);
  if (!memberId) {
    return false;
  }

  if (input.runtimeGuardrailedMemberIds?.has(memberId)) {
    return true;
  }

  if (!input.runtimeAvailableByMemberId) {
    return false;
  }

  return !input.runtimeAvailableByMemberId.has(memberId);
}

function buildMemberRosterPreview(input: {
  readonly guardrailedMemberIds?: ReadonlySet<string>;
  readonly member: StudioMemberSummary;
  readonly runsByMemberId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly runtimeAvailableByMemberId?: ReadonlySet<string>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
}): MemberRosterPreview {
  const matchedService = resolveMemberPreviewService({
    member: input.member,
    services: input.services,
  });
  const memberId = trimOptional(input.member.memberId);
  const visibleServiceId =
    trimOptional(matchedService?.serviceId) ||
    findStudioMemberServiceIdInCatalog(input.member, input.services);
  const serviceId =
    visibleServiceId ||
    resolveStudioMemberRuntimeServiceId(input.member, input.services);
  const runtimeRelevant = Boolean(
    visibleServiceId || trimOptional(input.member.lastBoundRevisionId),
  );
  const runtimeUnavailable =
    runtimeRelevant &&
    resolveRuntimeUnavailable({
      memberId,
      runtimeAvailableByMemberId: input.runtimeAvailableByMemberId,
      runtimeGuardrailedMemberIds: input.guardrailedMemberIds,
    });
  const runs =
    memberId && !runtimeUnavailable ? input.runsByMemberId[memberId] ?? [] : [];
  const latestRun = runs.slice().sort(compareRuns)[0] ?? null;
  const entryLabel = pickMeaningfulLabel(input.member.memberId, input.member.displayName) || "未命名成员";
  const serviceLabel =
    pickMeaningfulLabel(trimOptional(matchedService?.displayName), serviceId) ||
    (trimOptional(input.member.lastBoundRevisionId) ? "已绑定待确认" : "未绑定");
  const title = pickMeaningfulLabel(input.member.displayName, input.member.memberId) || "未命名成员";
  const studioHref = buildStudioRoute({
    scopeId: input.scopeId,
    memberId,
    tab: "studio",
  });

  let attention: WorkflowOperationalAttention = "draft";
  let attentionDetail = `当前成员还处于 ${formatStudioMemberLifecycleStage(input.member.lifecycleStage)} 阶段。`;

  if (runtimeUnavailable) {
    attention = "runtime-unresolved";
    attentionDetail = "当前成员已经存在绑定事实，但本页暂时没有拿到它的运行信号。";
  } else if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次成员运行处于异常状态。";
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次成员运行正在等待人工或外部信号。";
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = "最近一次成员运行正常，可继续进入详情查看。";
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = "当前成员已经形成绑定，但还没有可见的运行信号。";
  } else if (
    trimOptional(input.member.lastBoundRevisionId) ||
    input.member.lifecycleStage === "bind_ready"
  ) {
    attention = "no-bound-service";
    attentionDetail = "当前成员已经准备好绑定，但还没有稳定的可调用入口。";
  }

  const detailHref = serviceId
    ? buildTeamDetailHref({
        memberId,
        runId: latestRun?.runId || undefined,
        scopeId: input.scopeId,
        serviceId: serviceId || undefined,
      })
    : studioHref;
  const runtimeHref =
    serviceId.length > 0
      ? buildRuntimeRunsHref({
          actorId:
            latestRun?.actorId ||
            matchedService?.primaryActorId ||
            undefined,
          scopeId: input.scopeId,
          serviceId,
        })
      : "";
  const moreActions: Array<{ key: string; label: string; onClick: () => void }> = [];
  if (runtimeHref) {
    moreActions.push({
      key: "runtime",
      label: "查看运行",
      onClick: () => history.push(runtimeHref),
    });
  }
  moreActions.push({
    key: "builder",
    label: "进入 Studio",
    onClick: () => history.push(studioHref),
  });

  return {
    attention,
    attentionDetail,
    detailHref,
    entryLabel,
    latestRun,
    memberId,
    moreActions,
    primaryActionLabel: serviceId ? "查看团队" : "打开 Studio",
    serviceId,
    serviceLabel,
    title,
    updatedAt:
      latestRun?.lastUpdatedAt ||
      matchedService?.updatedAt ||
      input.member.updatedAt ||
      null,
  };
}

const MoreActionsButton: React.FC<{
  readonly actions: Array<{ key: string; label: string; onClick: () => void }>;
}> = ({ actions }) => (
  <Dropdown
    menu={{
      items: actions.map((action) => ({
        key: action.key,
        label: action.label,
      })),
      onClick: ({ key, domEvent }) => {
        domEvent.stopPropagation();
        const matchedAction = actions.find((action) => action.key === key);
        if (!matchedAction) {
          return;
        }

        matchedAction.onClick();
      },
    }}
    trigger={["click"]}
  >
    <Button
      icon={<MoreOutlined />}
      onClick={(event) => event.stopPropagation()}
      size="large"
    >
      更多
    </Button>
  </Dropdown>
);

const MemberRosterCard: React.FC<{
  readonly preview: MemberRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();

  return (
    <div
      onClick={() => history.push(preview.detailHref)}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          history.push(preview.detailHref);
        }
      }}
      role="button"
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 24,
        boxShadow: token.boxShadowTertiary,
        cursor: "pointer",
        display: "flex",
        flexDirection: "column",
        gap: 14,
        minWidth: 0,
        padding: 18,
      }}
      tabIndex={0}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          gap: 16,
          justifyContent: "space-between",
        }}
      >
        <div style={{ minWidth: 0 }}>
          <Typography.Title
            level={3}
            style={{
              fontSize: 22,
              margin: 0,
              overflowWrap: "anywhere",
            }}
          >
            {preview.title}
          </Typography.Title>
          <Typography.Paragraph
            ellipsis={{ rows: 1, tooltip: preview.attentionDetail }}
            style={{
              color: token.colorTextSecondary,
              fontSize: 14,
              marginBottom: 0,
              marginTop: 6,
            }}
          >
            {preview.attentionDetail}
          </Typography.Paragraph>
        </div>
        <span
          style={{
            ...resolveAttentionPillStyle(token, preview.attention),
            borderRadius: 999,
            display: "inline-flex",
            fontSize: 12,
            fontWeight: 600,
            lineHeight: 1,
            padding: "8px 12px",
            whiteSpace: "nowrap",
          }}
        >
          {formatAttentionLabel(preview.attention)}
        </span>
      </div>

      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 13,
        }}
      >
        成员标识：{preview.entryLabel}
      </Typography.Text>

      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 14,
          gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
          paddingTop: 14,
        }}
      >
        <TeamFact
          label="当前状态"
          value={formatOperationalStatusLabel(
            preview.latestRun?.completionStatus,
            preview.attention,
          )}
        />
        <TeamFact
          label="最近更新"
          value={formatShortTime(preview.updatedAt)}
        />
        <TeamFact label="关联服务" value={preview.serviceLabel} />
      </div>

      <Space wrap>
        <Button
          onClick={stopEvent(() => history.push(preview.detailHref))}
          size="large"
          type="primary"
        >
          {preview.primaryActionLabel}
        </Button>
        <MoreActionsButton actions={preview.moreActions} />
      </Space>
    </div>
  );
};

const MemberRosterRow: React.FC<{
  readonly preview: MemberRosterPreview;
}> = ({ preview }) => {
  const { token } = theme.useToken();

  return (
    <div
      onClick={() => history.push(preview.detailHref)}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          history.push(preview.detailHref);
        }
      }}
      role="button"
      style={{
        alignItems: "center",
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 20,
        boxShadow: token.boxShadowTertiary,
        cursor: "pointer",
        display: "grid",
        gap: 16,
        gridTemplateColumns: "minmax(0, 1.8fr) repeat(3, minmax(88px, 120px)) auto",
        minWidth: 0,
        padding: 16,
      }}
      tabIndex={0}
    >
      <div style={{ minWidth: 0 }}>
        <Space size={[8, 8]} wrap style={{ marginBottom: 6 }}>
          <Typography.Title
            level={4}
            style={{
              margin: 0,
              overflowWrap: "anywhere",
            }}
          >
            {preview.title}
          </Typography.Title>
          <span
            style={{
              ...resolveAttentionPillStyle(token, preview.attention),
              borderRadius: 999,
              display: "inline-flex",
              fontSize: 12,
              fontWeight: 600,
              lineHeight: 1,
              padding: "7px 10px",
              whiteSpace: "nowrap",
            }}
          >
            {formatAttentionLabel(preview.attention)}
          </span>
        </Space>
        <Typography.Paragraph
          ellipsis={{ rows: 1, tooltip: preview.attentionDetail }}
          style={{
            color: token.colorTextSecondary,
            fontSize: 13,
            marginBottom: 0,
            marginTop: 0,
          }}
        >
          {preview.attentionDetail}
        </Typography.Paragraph>
        <Typography.Text
          style={{
            color: token.colorTextSecondary,
            fontSize: 13,
          }}
        >
          成员标识：{preview.entryLabel}
        </Typography.Text>
      </div>

      <TeamFact
        label="状态"
        value={formatOperationalStatusLabel(
          preview.latestRun?.completionStatus,
          preview.attention,
        )}
      />
      <TeamFact label="更新" value={formatShortTime(preview.updatedAt)} />
      <TeamFact label="服务" value={preview.serviceLabel} />

      <Space wrap>
        <Button
          onClick={stopEvent(() => history.push(preview.detailHref))}
          type="primary"
        >
          {preview.primaryActionLabel}
        </Button>
        <MoreActionsButton actions={preview.moreActions} />
      </Space>
    </div>
  );
};

const TeamsHomePage: React.FC = () => {
  const { token } = theme.useToken();
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );
  const [manualRosterView, setManualRosterView] = React.useState<
    "cards" | "list" | null
  >(null);
  const [showScopePicker, setShowScopePicker] = React.useState(false);
  const [authRecoveryAttempted, setAuthRecoveryAttempted] = React.useState(false);
  const [authRecoveryPending, setAuthRecoveryPending] = React.useState(false);
  const isMountedRef = React.useRef(true);
  const nyxIdConfig = React.useMemo(() => getNyxIDRuntimeConfig(), []);

  React.useEffect(() => {
    return () => {
      isMountedRef.current = false;
    };
  }, []);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const refetchAuthSession = authSessionQuery.refetch;
  const localScopeId = trimOptional(loadRestorableAuthSession()?.user.sub);
  const locallyResolvedScope = React.useMemo(() => {
    if (!localScopeId) {
      return null;
    }

    return {
      scopeId: localScopeId,
      scopeSource: "local-session",
    };
  }, [localScopeId]);
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data) ?? locallyResolvedScope,
    [authSessionQuery.data, locallyResolvedScope],
  );
  const authSessionIssue = React.useMemo(() => {
    if (!authSessionQuery.isError) {
      return "";
    }

    return describeError(
      authSessionQuery.error,
      "登录状态暂时不可用，请刷新后重试。",
    );
  }, [authSessionQuery.error, authSessionQuery.isError]);
  const authSessionAccessResolved =
    !authSessionQuery.isLoading && !authSessionQuery.isError;
  const authSessionAuthenticated =
    authSessionQuery.data?.enabled === false ||
    Boolean(authSessionQuery.data?.authenticated);
  const authenticatedScopeId = trimOptional(authSessionQuery.data?.scopeId);

  React.useEffect(() => {
    if (authSessionQuery.isLoading || authSessionQuery.isError) {
      return;
    }

    if (!authSessionQuery.data?.enabled || authSessionQuery.data.authenticated) {
      setAuthRecoveryAttempted(false);
      setAuthRecoveryPending(false);
      return;
    }

    if (!nyxIdConfig.enabled || authRecoveryAttempted) {
      return;
    }

    setAuthRecoveryAttempted(true);
    setAuthRecoveryPending(true);

    void (async () => {
      try {
        await ensureActiveAuthSession(nyxIdConfig);
        await refetchAuthSession();
      } finally {
        if (isMountedRef.current) {
          setAuthRecoveryPending(false);
        }
      }
    })();
  }, [
    authRecoveryAttempted,
    authSessionQuery.data?.authenticated,
    authSessionQuery.data?.enabled,
    authSessionQuery.isError,
    authSessionQuery.isLoading,
    nyxIdConfig,
    refetchAuthSession,
  ]);

  React.useEffect(() => {
    if (
      !authSessionAccessResolved ||
      !authSessionAuthenticated ||
      authSessionQuery.data?.enabled === false ||
      !authenticatedScopeId
    ) {
      return;
    }

    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim() === authenticatedScopeId
        ? currentDraft
        : { scopeId: authenticatedScopeId },
    );
    setDraft((currentDraft) =>
      showScopePicker || currentDraft.scopeId.trim() === authenticatedScopeId
        ? currentDraft
        : { scopeId: authenticatedScopeId },
    );
  }, [
    authSessionAccessResolved,
    authSessionAuthenticated,
    authSessionQuery.data?.enabled,
    authenticatedScopeId,
    showScopePicker,
  ]);

  React.useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);

  const scopeId = activeDraft.scopeId.trim();
  const rosterScopeMismatch =
    scopeId.length > 0 &&
    authSessionAccessResolved &&
    authSessionAuthenticated &&
    authSessionQuery.data?.enabled !== false &&
    authenticatedScopeId.length > 0 &&
    scopeId !== authenticatedScopeId;
  const rosterMissingAuthorizedScope =
    scopeId.length > 0 &&
    !authSessionQuery.isError &&
    authSessionAccessResolved &&
    authSessionAuthenticated &&
    authSessionQuery.data?.enabled !== false &&
    !authenticatedScopeId &&
    !authRecoveryPending;
  const scopeQueriesEnabled =
    scopeId.length > 0 &&
    !authRecoveryPending &&
    !rosterScopeMismatch &&
    !rosterMissingAuthorizedScope &&
    (authSessionQuery.isError ||
      (authSessionAccessResolved && authSessionAuthenticated));
  const rosterAuthPending =
    scopeId.length > 0 &&
    (authSessionQuery.isLoading || authRecoveryPending || rosterScopeMismatch);
  const rosterAuthUnavailable =
    scopeId.length > 0 &&
    !authSessionQuery.isError &&
    authSessionAccessResolved &&
    !authSessionAuthenticated &&
    !authRecoveryPending;

  React.useEffect(() => {
    history.replace(buildScopeHref("/teams", activeDraft));
  }, [activeDraft]);

  const membersQuery = useQuery({
    enabled: scopeQueriesEnabled,
    queryKey: ["teams", "members", scopeId],
    queryFn: () => studioApi.listMembers(scopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: scopeQueriesEnabled,
    queryKey: ["teams", "services", scopeId],
    queryFn: () =>
      servicesApi.listServices({
        tenantId: scopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
    }),
    retry: false,
  });
  const memberRosterIssue = React.useMemo(() => {
    if (!membersQuery.isError) {
      return "";
    }

    return describeError(
      membersQuery.error,
      "当前 Scope 的成员清单暂时无法加载。",
    );
  }, [membersQuery.error, membersQuery.isError]);

  const studioMembers = React.useMemo(
    () => [...(membersQuery.data?.members ?? [])].sort(compareMembers),
    [membersQuery.data?.members],
  );
  const runtimeTrackableMembers = React.useMemo(
    () =>
      studioMembers.filter(
        (member) =>
          Boolean(
            findStudioMemberServiceIdInCatalog(
              member,
              servicesQuery.data ?? [],
            ),
          ) ||
          Boolean(trimOptional(member.lastBoundRevisionId)),
      ),
    [servicesQuery.data, studioMembers],
  );
  const runtimeSampleMembers = React.useMemo(
    () => runtimeTrackableMembers.slice(0, WORKFLOW_RUNTIME_GUARDRAIL),
    [runtimeTrackableMembers],
  );
  const guardrailedMemberIds = React.useMemo(
    () =>
      new Set(
        runtimeTrackableMembers
          .slice(WORKFLOW_RUNTIME_GUARDRAIL)
          .map((member) => trimOptional(member.memberId))
          .filter(Boolean),
      ),
    [runtimeTrackableMembers],
  );
  const memberRunQueries = useQueries({
    queries: runtimeSampleMembers.map((member) => ({
      enabled: scopeQueriesEnabled && membersQuery.isSuccess,
      queryKey: ["teams", "member-runs", scopeId, member.memberId],
      queryFn: () =>
        scopeRuntimeApi.listMemberRuns(scopeId, member.memberId, {
          take: 12,
        }),
      retry: false,
    })),
  });
  const memberRosterIssue = React.useMemo(() => {
    if (!membersQuery.isError) {
      return "";
    }

    return describeError(
      membersQuery.error,
      "当前 Scope 的成员清单暂时无法加载。",
    );
  }, [membersQuery.error, membersQuery.isError]);
  const runtimeAvailableByMemberId = React.useMemo(() => {
    const available = new Set<string>();
    memberRunQueries.forEach((query, index) => {
      if (query.isSuccess) {
        available.add(trimOptional(runtimeSampleMembers[index]?.memberId));
      }
    });
    return available;
  }, [memberRunQueries, runtimeSampleMembers]);
  const runsByMemberId = React.useMemo(
    () =>
      Object.fromEntries(
        runtimeSampleMembers.map((member, index) => [
          trimOptional(member.memberId),
          memberRunQueries[index]?.data?.runs ?? [],
        ]),
      ) as Record<string, readonly any[]>,
    [memberRunQueries, runtimeSampleMembers],
  );
  const memberPreviews = React.useMemo(
    () =>
      studioMembers.map((member) =>
        buildMemberRosterPreview({
          guardrailedMemberIds,
          member,
          runsByMemberId,
          runtimeAvailableByMemberId,
          scopeId,
          services: servicesQuery.data ?? [],
        }),
      ),
    [
      guardrailedMemberIds,
      runsByMemberId,
      runtimeAvailableByMemberId,
      scopeId,
      servicesQuery.data,
      studioMembers,
    ],
  );
  const membersPendingBindingCount = React.useMemo(
    () =>
      studioMembers.filter(
        (member) =>
          !findStudioMemberServiceIdInCatalog(
            member,
            servicesQuery.data ?? [],
          ) ||
          !trimOptional(member.lastBoundRevisionId),
      ).length,
    [servicesQuery.data, studioMembers],
  );
  const visibleTeamCount = memberPreviews.length;
  const resolvedRosterView =
    manualRosterView ??
    (visibleTeamCount >= compactTeamRosterThreshold ? "list" : "cards");
  const useCompactRoster = resolvedRosterView === "list";
  const healthyTeamCount = memberPreviews.filter(
    (preview) => preview.attention === "healthy",
  ).length;
  const attentionTeamCount = memberPreviews.filter(
    (preview) => preview.attention !== "healthy",
  ).length;
  const emptyRosterHint =
    scopeId.length > 0
      ? "当前 Scope 下还没有创建任何 team。进入 Studio 创建 team 后，再到 team 里添加 member。"
      : "先导入一个 Scope，首页才能渲染出这组成员卡片。";
  const partialIssues = [
    servicesQuery.isError ? "服务目录暂时不可见。" : null,
    membersQuery.isError ? "当前 Scope 的成员清单暂时不可见。" : null,
    ...memberRunQueries.map((query) =>
      query.isError ? "部分成员运行信号暂时不可见。" : null,
    ),
    guardrailedMemberIds.size > 0
      ? `当前首页只采样前 ${WORKFLOW_RUNTIME_GUARDRAIL} 个已绑定成员的运行信号。`
      : null,
  ].filter((issue): issue is string => Boolean(issue));

  const titleNode = (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 14,
        }}
      >
        Aevatar / Teams
      </Typography.Text>
      <Typography.Title
        level={1}
        style={{
          margin: 0,
        }}
      >
        我的 AI 团队
      </Typography.Title>
    </div>
  );
  const canCancelScopePicker = showScopePicker && scopeId.length > 0;

  return (
    <AevatarPageShell
      extra={
        <Space wrap>
          <Button
            icon={<PlusOutlined />}
            onClick={() =>
              history.push(
                buildStudioRoute({
                  scopeId:
                    scopeId ||
                    readScopeQueryDraft().scopeId ||
                    resolvedScope?.scopeId ||
                    localScopeId,
                  tab: "studio",
                  intent: "create-member",
                }),
              )
            }
            style={{ borderRadius: 16, height: 40, paddingInline: 18 }}
            type="primary"
          >
            组建新团队
          </Button>
        </Space>
      }
      layoutMode="document"
      title={titleNode}
    >
      <div
        style={{
          display: "flex",
          flexDirection: "column",
          gap: 20,
        }}
      >
        {(showScopePicker || !scopeId) && (
          <AevatarPanel
            extra={
              canCancelScopePicker ? (
                <Button
                  onClick={() => {
                    setDraft(normalizeScopeDraft(activeDraft));
                    setShowScopePicker(false);
                  }}
                >
                  取消
                </Button>
              ) : null
            }
            title="Scope 上下文"
            titleHelp="这一步只负责锁定你当前要查看的 Scope，不把它抢成首页主角。"
          >
            <ScopeQueryCard
              activeScopeId={scopeId}
              draft={draft}
              loadLabel="导入团队视图"
              onChange={setDraft}
              onLoad={() => {
                const nextDraft = normalizeScopeDraft(draft);
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                setShowScopePicker(false);
              }}
              onReset={() => {
                const nextDraft = normalizeScopeDraft({
                  scopeId: resolvedScope?.scopeId ?? "",
                });
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
              }}
              onUseResolvedScope={() => {
                if (!resolvedScope?.scopeId) {
                  return;
                }

                const nextDraft = normalizeScopeDraft({
                  scopeId: resolvedScope.scopeId,
                });
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                setShowScopePicker(false);
              }}
              resetDisabled={
                normalizeScopeDraft(draft).scopeId ===
                  (resolvedScope?.scopeId?.trim() ?? "") &&
                scopeId === (resolvedScope?.scopeId?.trim() ?? "")
              }
              resolvedScopeId={resolvedScope?.scopeId}
              resolvedScopeSource={resolvedScope?.scopeSource}
            />
          </AevatarPanel>
        )}

        {scopeId && !showScopePicker ? (
          <div
            style={{
              alignItems: "flex-start",
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 22,
              boxShadow: token.boxShadowTertiary,
              display: "flex",
              flexWrap: "wrap",
              gap: 16,
              justifyContent: "space-between",
              padding: 18,
            }}
          >
            <div
              style={{
                display: "flex",
                flexDirection: "column",
                gap: 6,
                minWidth: 0,
              }}
            >
              <Typography.Text type="secondary">当前 Scope</Typography.Text>
              <Typography.Text
                strong
                style={{
                  fontSize: 16,
                  overflowWrap: "anywhere",
                }}
              >
                {scopeId}
              </Typography.Text>
              <Typography.Text type="secondary">
                Scope 只提供团队访问上下文；首页先展示当前 Scope 下的 teams，再从 team 进入 member 工作台。
              </Typography.Text>
            </div>
          </div>
        ) : null}

        {!scopeId ? (
          <Alert
            showIcon
            title="先导入一个 Scope，首页才能渲染出这组成员卡片。"
            type="info"
          />
        ) : null}

        {partialIssues.length > 0 ? (
          <Alert
            description={partialIssues.join(" ")}
            showIcon
            title="部分团队信号暂时不可见"
            type="warning"
          />
        ) : null}

        {authSessionIssue ? (
          <Alert
            description={
              resolvedScope?.scopeId
                ? `${authSessionIssue} 当前已回退到本地会话里的 Scope ${resolvedScope.scopeId}。`
                : authSessionIssue
            }
            showIcon
            title={
              resolvedScope?.scopeId
                ? "当前登录态校验失败，已回退到本地 Scope"
                : "当前登录态校验失败"
            }
            type="warning"
          />
        ) : null}

        {scopeId ? (
          <>
            <div
              style={{
                display: "grid",
                gap: 16,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryStatCard accent label="团队成员" value={visibleTeamCount} />
              <SummaryStatCard label="运行正常" value={healthyTeamCount} />
              <SummaryStatCard label="需要处理" value={attentionTeamCount} />
            </div>

            {membersPendingBindingCount > 0 ? (
              <Alert
                action={
                  <Button
                    onClick={() =>
                      history.push(
                        buildStudioRoute({
                          scopeId,
                          intent: "create-member",
                          tab: "studio",
                        }),
                      )
                    }
                    size="small"
                    type="primary"
                  >
                    进入 Studio 创建 Team
                  </Button>
                }
                description={`其中 ${membersPendingBindingCount} 个成员还没有完成独立绑定，或还没有形成稳定的可调用入口。`}
                showIcon
                title="还有成员待整理"
                type="info"
              />
            ) : null}

            {rosterAuthPending || membersQuery.isLoading ? (
              <AevatarInspectorEmpty
                description={
                  rosterScopeMismatch
                    ? "正在对齐当前登录态允许访问的 Scope。"
                    : authRecoveryPending
                    ? "正在恢复当前登录态并整理成员清单。"
                    : "正在整理当前 Scope 的成员清单。"
                }
              />
            ) : rosterMissingAuthorizedScope ? (
              <Alert
                description="当前登录态已经通过认证，但没有解析出 canonical scope_id，所以无法读取受保护的 Scope 资源。请重新登录；如果仍然失败，请检查 NyxID claims transformer 是否生效。"
                showIcon
                title="当前登录态缺少 Scope 绑定"
                type="warning"
              />
            ) : rosterAuthUnavailable ? (
              <Alert
                description="成员清单会在登录态恢复完成后再加载；如果长时间停留在这里，请重新登录。"
                showIcon
                title="当前登录态尚未准备好"
                type="info"
              />
            ) : membersQuery.isError ? (
              <Alert
                description={memberRosterIssue}
                showIcon
                title="当前 Scope 的成员清单暂时无法加载。"
                type="error"
              />
            ) : memberPreviews.length > 0 ? (
              <>
                <div
                  style={{
                    alignItems: "center",
                    display: "flex",
                    flexWrap: "wrap",
                    gap: 12,
                    justifyContent: "space-between",
                  }}
                >
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 4,
                    }}
                  >
                    <Typography.Title
                      level={4}
                      style={{
                        margin: 0,
                      }}
                    >
                      团队成员
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      当前 Scope 下已经登记的成员，以及它们各自的绑定和运行状态。
                    </Typography.Text>
                  </div>
                  {visibleTeamCount > 1 ? (
                    <Space.Compact>
                      <Tooltip title="卡片视图">
                        <Button
                          aria-label="切换到卡片视图"
                          icon={<AppstoreOutlined />}
                          onClick={() => setManualRosterView("cards")}
                          type={resolvedRosterView === "cards" ? "primary" : "default"}
                        />
                      </Tooltip>
                      <Tooltip title="列表视图">
                        <Button
                          aria-label="切换到列表视图"
                          icon={<BarsOutlined />}
                          onClick={() => setManualRosterView("list")}
                          type={resolvedRosterView === "list" ? "primary" : "default"}
                        />
                      </Tooltip>
                    </Space.Compact>
                  ) : null}
                </div>
                {useCompactRoster ? (
                  <div
                    aria-label="团队紧凑视图"
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 14,
                    }}
                  >
                    {memberPreviews.map((preview) => (
                      <MemberRosterRow key={preview.memberId} preview={preview} />
                    ))}
                  </div>
                ) : (
                  <div
                    aria-label="团队卡片视图"
                    style={{
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns: "repeat(auto-fit, minmax(340px, 1fr))",
                    }}
                  >
                    {memberPreviews.map((preview) => (
                      <MemberRosterCard key={preview.memberId} preview={preview} />
                    ))}
                  </div>
                )}
              </>
            ) : (
              <Empty
                description={emptyRosterHint}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              >
                <Button
                  onClick={() =>
                    history.push(
                      buildStudioRoute({
                        scopeId,
                        intent: "create-member",
                        tab: "studio",
                      }),
                    )
                  }
                  type="primary"
                >
                  进入 Studio 创建 Team
                </Button>
              </Empty>
            )}

          </>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default TeamsHomePage;
