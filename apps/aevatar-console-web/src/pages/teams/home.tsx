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
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { loadRestorableAuthSession } from "@/shared/auth/session";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { studioApi } from "@/shared/studio/api";
import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  describeStudioScopeBindingRevisionTarget,
  getStudioScopeBindingCurrentRevision,
  type StudioScopeBindingStatus,
} from "@/shared/studio/models";
import { buildStudioWorkflowWorkspaceRoute } from "@/shared/studio/navigation";
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
  buildWorkflowOperationalUnits,
  collectWorkflowOperationalServiceIds,
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalAttention,
} from "./workflowOperationalUnits";

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";
const compactTeamRosterThreshold = 6;

type ScopeBackedTeamPreview = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly detailHref: string;
  readonly entryLabel: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly moreActions: Array<{ key: string; label: string; onClick: () => void }>;
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

function resolveScopePreviewService(input: {
  readonly binding: StudioScopeBindingStatus | null | undefined;
  readonly services: readonly ServiceCatalogSnapshot[];
}): ServiceCatalogSnapshot | null {
  const boundServiceId = trimOptional(input.binding?.serviceId);
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
  readonly serviceId: string;
  readonly runtimeAvailableByServiceId?: ReadonlySet<string>;
  readonly runtimeGuardrailedServiceIds?: ReadonlySet<string>;
}): boolean {
  const serviceId = trimOptional(input.serviceId);
  if (!serviceId) {
    return false;
  }

  if (input.runtimeGuardrailedServiceIds?.has(serviceId)) {
    return true;
  }

  if (!input.runtimeAvailableByServiceId) {
    return false;
  }

  return !input.runtimeAvailableByServiceId.has(serviceId);
}

function buildScopeBackedTeamPreview(input: {
  readonly binding: StudioScopeBindingStatus | null | undefined;
  readonly guardrailedServiceIds?: ReadonlySet<string>;
  readonly runsByServiceId: Readonly<Record<string, readonly ScopeServiceRunSummary[]>>;
  readonly runtimeAvailableByServiceId?: ReadonlySet<string>;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
}): ScopeBackedTeamPreview | null {
  const currentRevision = getStudioScopeBindingCurrentRevision(input.binding);
  const revisionTarget = describeStudioScopeBindingRevisionTarget(currentRevision);
  const matchedService = resolveScopePreviewService({
    binding: input.binding,
    services: input.services,
  });
  const boundServiceId = trimOptional(input.binding?.serviceId);
  const serviceId = trimOptional(matchedService?.serviceId) || boundServiceId;
  const runtimeUnavailable = resolveRuntimeUnavailable({
    runtimeAvailableByServiceId: input.runtimeAvailableByServiceId,
    runtimeGuardrailedServiceIds: input.guardrailedServiceIds,
    serviceId,
  });
  const runs =
    serviceId && !runtimeUnavailable ? input.runsByServiceId[serviceId] ?? [] : [];
  const latestRun = runs.slice().sort(compareRuns)[0] ?? null;
  const hasEntryFacts = Boolean(
    serviceId ||
      matchedService ||
      currentRevision ||
      trimOptional(input.binding?.serviceKey) ||
      trimOptional(input.binding?.displayName),
  );

  if (!hasEntryFacts) {
    return null;
  }

  const entryLabel =
    pickMeaningfulLabel(
      revisionTarget,
      trimOptional(input.binding?.serviceKey),
      trimOptional(input.binding?.displayName),
      trimOptional(matchedService?.displayName),
    ) || "未命名入口";
  const serviceLabel =
    pickMeaningfulLabel(trimOptional(matchedService?.displayName), serviceId) ||
    "未记录";
  const title =
    pickMeaningfulLabel(
      trimOptional(input.binding?.displayName),
      trimOptional(matchedService?.displayName),
      entryLabel,
    ) || "未命名团队";

  let attention: WorkflowOperationalAttention = "draft";
  let attentionDetail = "当前团队入口还没有形成可运行状态。";

  if (runtimeUnavailable) {
    attention = "runtime-unresolved";
    attentionDetail = "团队入口已经存在，但当前运行信号暂时不可见。";
  } else if (latestRun && isFailedRun(latestRun)) {
    attention = "failed";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次团队运行处于异常状态。";
  } else if (latestRun && isWaitingRun(latestRun)) {
    attention = "waiting";
    attentionDetail =
      trimOptional(latestRun.lastError) || "最近一次团队运行正在等待人工或外部信号。";
  } else if (latestRun && isSuccessfulRun(latestRun)) {
    attention = "healthy";
    attentionDetail = "最近一次团队运行正常，可继续进入详情查看。";
  } else if (serviceId || matchedService) {
    attention = "no-recent-runs";
    attentionDetail = "已存在 service 记录，但当前还没有可见的运行信号。";
  } else if (
    currentRevision ||
    trimOptional(input.binding?.serviceKey) ||
    trimOptional(input.binding?.displayName)
  ) {
    attention = "no-bound-service";
    attentionDetail = "当前团队入口已经绑定，但服务能力还没有完整就绪。";
  }

  const detailHref = buildTeamDetailHref({
    runId: latestRun?.runId || undefined,
    scopeId: input.scopeId,
    serviceId: serviceId || undefined,
  });
  const runtimeHref =
    serviceId.length > 0
      ? buildRuntimeRunsHref({
          actorId:
            latestRun?.actorId ||
            matchedService?.primaryActorId ||
            trimOptional(input.binding?.primaryActorId) ||
            undefined,
          scopeId: input.scopeId,
          serviceId,
        })
      : "";
  const builderHref = buildStudioWorkflowWorkspaceRoute({
    scopeId: input.scopeId,
    scopeLabel: input.scopeId,
  });
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
    onClick: () => history.push(builderHref),
  });

  return {
    attention,
    attentionDetail,
    detailHref,
    entryLabel,
    latestRun,
    moreActions,
    serviceId,
    serviceLabel,
    title,
    updatedAt:
      latestRun?.lastUpdatedAt ||
      matchedService?.updatedAt ||
      input.binding?.updatedAt ||
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

const ScopeBackedTeamCard: React.FC<{
  readonly preview: ScopeBackedTeamPreview;
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
        默认入口：{preview.entryLabel}
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
          查看团队
        </Button>
        <MoreActionsButton actions={preview.moreActions} />
      </Space>
    </div>
  );
};

const ScopeBackedTeamRow: React.FC<{
  readonly preview: ScopeBackedTeamPreview;
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
          默认入口：{preview.entryLabel}
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
          查看团队
        </Button>
        <MoreActionsButton actions={preview.moreActions} />
      </Space>
    </div>
  );
};

const TeamsHomePage: React.FC = () => {
  const { token } = theme.useToken();
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(initialDraft);
  const [manualRosterView, setManualRosterView] = React.useState<
    "cards" | "list" | null
  >(null);
  const [showScopePicker, setShowScopePicker] = React.useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
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

  React.useEffect(() => {
    history.replace(buildScopeHref("/teams", activeDraft));
  }, [activeDraft]);

  const bindingQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "binding", scopeId],
    queryFn: () => studioApi.getScopeBinding(scopeId),
    retry: false,
  });
  const workflowsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "workflows", scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
    retry: false,
  });
  const servicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "services", scopeId],
    queryFn: () =>
      servicesApi.listServices({
        tenantId: scopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
      }),
    retry: false,
  });

  const matchedServiceIds = React.useMemo(
    () =>
      collectWorkflowOperationalServiceIds({
        binding: bindingQuery.data ?? null,
        services: servicesQuery.data ?? [],
        workflows: workflowsQuery.data ?? [],
      }),
    [bindingQuery.data, servicesQuery.data, workflowsQuery.data],
  );
  const scopePreviewServiceId = React.useMemo(
    () => trimOptional(bindingQuery.data?.serviceId),
    [bindingQuery.data?.serviceId],
  );
  const runtimeServiceIds = React.useMemo(() => {
    const normalizedScopePreviewServiceId = trimOptional(scopePreviewServiceId);
    const ordered = normalizedScopePreviewServiceId
      ? [
          normalizedScopePreviewServiceId,
          ...matchedServiceIds.filter((serviceId) => serviceId !== normalizedScopePreviewServiceId),
        ]
      : matchedServiceIds;
    return ordered;
  }, [matchedServiceIds, scopePreviewServiceId]);
  const runtimeSampleServiceIds = runtimeServiceIds.slice(
    0,
    WORKFLOW_RUNTIME_GUARDRAIL,
  );
  const guardrailedServiceIds = React.useMemo(
    () => new Set(runtimeServiceIds.slice(WORKFLOW_RUNTIME_GUARDRAIL)),
    [runtimeServiceIds],
  );
  const serviceRunQueries = useQueries({
    queries: runtimeSampleServiceIds.map((serviceId) => ({
      enabled: scopeId.length > 0 && servicesQuery.isSuccess,
      queryKey: ["teams", "runs", scopeId, serviceId],
      queryFn: () =>
        scopeRuntimeApi.listServiceRuns(scopeId, serviceId, {
          take: 12,
        }),
      retry: false,
    })),
  });
  const runtimeAvailableByServiceId = React.useMemo(() => {
    const available = new Set<string>();
    serviceRunQueries.forEach((query, index) => {
      if (query.isSuccess) {
        available.add(runtimeSampleServiceIds[index] ?? "");
      }
    });
    return available;
  }, [runtimeSampleServiceIds, serviceRunQueries]);
  const runsByServiceId = React.useMemo(
    () =>
      Object.fromEntries(
        runtimeSampleServiceIds.map((serviceId, index) => [
          serviceId,
          serviceRunQueries[index]?.data?.runs ?? [],
        ]),
      ) as Record<string, readonly any[]>,
    [runtimeSampleServiceIds, serviceRunQueries],
  );
  const units = React.useMemo(
    () =>
      buildWorkflowOperationalUnits({
        binding: bindingQuery.data ?? null,
        runsByServiceId,
        services: servicesQuery.data ?? [],
        signals: {
          runtimeAvailableByServiceId,
          runtimeGuardrailedServiceIds: guardrailedServiceIds,
          servicesAvailable: servicesQuery.isSuccess,
        },
        workflows: workflowsQuery.data ?? [],
      }),
    [
      bindingQuery.data,
      guardrailedServiceIds,
      runsByServiceId,
      runtimeAvailableByServiceId,
      servicesQuery.data,
      servicesQuery.isSuccess,
      workflowsQuery.data,
    ],
  );
  const scopePreviewTeam = React.useMemo(
    () =>
      buildScopeBackedTeamPreview({
        binding: bindingQuery.data ?? null,
        guardrailedServiceIds,
        runsByServiceId,
        runtimeAvailableByServiceId,
        scopeId,
        services: servicesQuery.data ?? [],
      }),
    [
      bindingQuery.data,
      guardrailedServiceIds,
      runsByServiceId,
      runtimeAvailableByServiceId,
      scopeId,
      servicesQuery.data,
    ],
  );

  const draftUnits = units.filter(
    (unit) => unit.isDraftOnly || (!unit.matchedService && !unit.latestRun),
  );
  const visibleTeamCount = scopePreviewTeam ? 1 : 0;
  const resolvedRosterView =
    manualRosterView ??
    (visibleTeamCount >= compactTeamRosterThreshold ? "list" : "cards");
  const useCompactRoster = resolvedRosterView === "list";
  const healthyTeamCount = scopePreviewTeam?.attention === "healthy" ? 1 : 0;
  const attentionTeamCount =
    scopePreviewTeam && scopePreviewTeam.attention !== "healthy" ? 1 : 0;
  const draftHint =
    draftUnits.length > 0
      ? `当前 Scope 里还有 ${draftUnits.length} 个已保存的行为定义，但它们还没有形成首页团队入口。`
      : "当前 Scope 里还没有形成首页团队入口。";
  const partialIssues = [
    servicesQuery.isError ? "服务目录暂时不可见。" : null,
    bindingQuery.isError ? "当前 Scope 的团队绑定信息暂时不可见。" : null,
    ...serviceRunQueries.map((query) =>
      query.isError ? "部分运行信号暂时不可见。" : null,
    ),
    guardrailedServiceIds.size > 0
      ? `当前首页只采样前 ${WORKFLOW_RUNTIME_GUARDRAIL} 个服务的运行信号。`
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
            onClick={() => history.push(buildTeamCreateHref())}
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
                首页按这个 Scope 汇总已经形成入口的团队，Scope 只做上下文，不再直接当团队名展示。
              </Typography.Text>
            </div>
          </div>
        ) : null}

        {!scopeId ? (
          <Alert
            showIcon
            title="先导入一个 Scope，首页才能渲染出这组团队卡片。"
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
              <SummaryStatCard accent label="团队入口" value={visibleTeamCount} />
              <SummaryStatCard label="运行正常" value={healthyTeamCount} />
              <SummaryStatCard label="需要处理" value={attentionTeamCount} />
            </div>

            {draftUnits.length > 0 ? (
              <Alert
                action={
                  <Button
                    onClick={() =>
                      history.push(
                        buildStudioWorkflowWorkspaceRoute({
                          scopeId,
                          scopeLabel: scopeId,
                        }),
                      )
                    }
                    size="small"
                    type="primary"
                  >
                    打开 Studio
                  </Button>
                }
                description={`其中 ${draftUnits.length} 个行为定义还停留在草稿阶段，尚未形成首页团队入口。`}
                showIcon
                title="还有草稿待整理"
                type="info"
              />
            ) : null}

            {workflowsQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在整理当前 Scope 的团队入口。" />
            ) : workflowsQuery.isError ? (
              <Alert
                showIcon
                title="当前 Scope 的团队入口暂时无法加载。"
                type="error"
              />
            ) : scopePreviewTeam ? (
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
                      团队入口
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      当前 Scope 下已经形成首页入口的团队。
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
                    <ScopeBackedTeamRow preview={scopePreviewTeam} />
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
                    <ScopeBackedTeamCard preview={scopePreviewTeam} />
                  </div>
                )}
              </>
            ) : (
              <Empty
                description={draftHint}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              >
                <Button
                  onClick={() =>
                    history.push(
                      buildStudioWorkflowWorkspaceRoute({
                        scopeId,
                        scopeLabel: scopeId,
                      }),
                    )
                  }
                  type="primary"
                >
                  打开 Studio
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
