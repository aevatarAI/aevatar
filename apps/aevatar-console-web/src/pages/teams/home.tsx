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
import {
  buildStudioWorkflowWorkspaceRoute,
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
  buildWorkflowOperationalUnits,
  collectWorkflowOperationalServiceIds,
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalUnit,
  type WorkflowOperationalAttention,
} from "./workflowOperationalUnits";

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";
const compactTeamRosterThreshold = 6;
const scopeBackedTeamTitle = "当前团队";

type ScopeBackedTeamPreview = {
  readonly attention: WorkflowOperationalAttention;
  readonly attentionDetail: string;
  readonly detailHref: string;
  readonly latestRun: ScopeServiceRunSummary | null;
  readonly memberCount: number;
  readonly moreActions: Array<{ key: string; label: string; onClick: () => void }>;
  readonly publishedServiceCount: number;
  readonly recentActivityLabel: string;
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

function formatOperationalUnitLabel(unit: WorkflowOperationalUnit | null | undefined): string {
  if (!unit) {
    return "未命名成员";
  }

  return (
    pickMeaningfulLabel(
      trimOptional(unit.workflow.displayName),
      trimOptional(unit.workflow.workflowName),
      trimOptional(unit.workflow.workflowId),
      trimOptional(unit.matchedService?.displayName),
      trimOptional(unit.matchedService?.serviceId),
    ) || "未命名成员"
  );
}

function resolveAttentionPriority(attention: WorkflowOperationalAttention): number {
  switch (attention) {
    case "failed":
      return 6;
    case "waiting":
      return 5;
    case "runtime-unresolved":
      return 4;
    case "no-recent-runs":
      return 3;
    case "no-bound-service":
      return 2;
    case "draft":
      return 1;
    case "healthy":
      return 0;
    default:
      return 0;
  }
}

function resolveOperationalUnitTimestamp(unit: WorkflowOperationalUnit): number {
  return Math.max(
    parseTimestamp(unit.latestRun?.lastUpdatedAt),
    parseTimestamp(unit.matchedService?.updatedAt),
    parseTimestamp(unit.workflow.updatedAt),
  );
}

function compareOperationalUnits(
  left: WorkflowOperationalUnit,
  right: WorkflowOperationalUnit,
): number {
  const priorityDelta =
    resolveAttentionPriority(right.attention) -
    resolveAttentionPriority(left.attention);
  if (priorityDelta !== 0) {
    return priorityDelta;
  }

  return resolveOperationalUnitTimestamp(right) - resolveOperationalUnitTimestamp(left);
}

function describeTeamPreviewAttention(
  primaryUnit: WorkflowOperationalUnit,
): string {
  const unitLabel = formatOperationalUnitLabel(primaryUnit);
  switch (primaryUnit.attention) {
    case "failed":
      return `${unitLabel} 最近一次运行异常，需要先处理。`;
    case "waiting":
      return `${unitLabel} 正在等待人工或外部信号。`;
    case "healthy":
      return `${unitLabel} 最近一次运行正常，可继续进入团队详情查看。`;
    case "no-recent-runs":
      return `${unitLabel} 已经形成已发布服务，但还没有可见运行信号。`;
    case "no-bound-service":
      return `${unitLabel} 还没有形成已发布服务。`;
    case "runtime-unresolved":
      return `${unitLabel} 已有服务记录，但当前运行信号暂时不可见。`;
    case "draft":
    default:
      return `${unitLabel} 还停留在草稿阶段，团队概览尚未完全成形。`;
  }
}

function buildScopeBackedTeamPreview(input: {
  readonly guardrailedServiceIds?: ReadonlySet<string>;
  readonly runtimeAvailableByServiceId?: ReadonlySet<string>;
  readonly scopeId: string;
  readonly units: readonly WorkflowOperationalUnit[];
}): ScopeBackedTeamPreview | null {
  if (input.units.length === 0) {
    return null;
  }

  const sortedUnits = [...input.units].sort(compareOperationalUnits);
  const primaryUnit = sortedUnits[0] ?? null;
  if (!primaryUnit) {
    return null;
  }

  const latestRun =
    input.units
      .flatMap((unit) => (unit.latestRun ? [unit.latestRun] : []))
      .sort(compareRuns)[0] ?? null;
  const publishedServiceIds = new Set(
    input.units
      .map((unit) => trimOptional(unit.matchedService?.serviceId))
      .filter(Boolean),
  );
  const recentActivityLabel = latestRun
    ? `${trimOptional(latestRun.workflowName) || formatOperationalUnitLabel(primaryUnit)} · ${formatRunStatusLabel(latestRun.completionStatus)}`
    : formatOperationalUnitLabel(primaryUnit);
  const runtimeServiceId =
    trimOptional(latestRun?.serviceId) || trimOptional(primaryUnit.matchedService?.serviceId);
  const runtimeUnavailable = resolveRuntimeUnavailable({
    runtimeAvailableByServiceId: input.runtimeAvailableByServiceId,
    runtimeGuardrailedServiceIds: input.guardrailedServiceIds,
    serviceId: runtimeServiceId,
  });
  const runtimeHref =
    runtimeServiceId.length > 0 && !runtimeUnavailable
      ? buildRuntimeRunsHref({
          actorId:
            latestRun?.actorId ||
            trimOptional(primaryUnit.matchedService?.primaryActorId) ||
            undefined,
          scopeId: input.scopeId,
          serviceId: runtimeServiceId,
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
    onClick: () =>
      history.push(
        buildStudioWorkflowWorkspaceRoute({
          scopeId: input.scopeId,
        }),
      ),
  });

  return {
    attention: primaryUnit.attention,
    attentionDetail: describeTeamPreviewAttention(primaryUnit),
    detailHref: buildTeamDetailHref({
      scopeId: input.scopeId,
    }),
    latestRun,
    memberCount: input.units.length,
    moreActions,
    publishedServiceCount: publishedServiceIds.size,
    recentActivityLabel,
    title: scopeBackedTeamTitle,
    updatedAt:
      latestRun?.lastUpdatedAt ||
      primaryUnit.matchedService?.updatedAt ||
      primaryUnit.workflow.updatedAt ||
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
        最近动态：{preview.recentActivityLabel}
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
        <TeamFact label="成员数" value={preview.memberCount} />
        <TeamFact label="已发布服务" value={preview.publishedServiceCount} />
        <TeamFact label="最近更新" value={formatShortTime(preview.updatedAt)} />
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
          最近动态：{preview.recentActivityLabel}
        </Typography.Text>
      </div>

      <TeamFact label="成员" value={preview.memberCount} />
      <TeamFact label="已发布服务" value={preview.publishedServiceCount} />
      <TeamFact label="更新" value={formatShortTime(preview.updatedAt)} />

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
        services: servicesQuery.data ?? [],
        workflows: workflowsQuery.data ?? [],
      }),
    [servicesQuery.data, workflowsQuery.data],
  );
  const runtimeServiceIds = React.useMemo(() => {
    return matchedServiceIds;
  }, [matchedServiceIds]);
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
        guardrailedServiceIds,
        runtimeAvailableByServiceId,
        scopeId,
        units,
      }),
    [
      guardrailedServiceIds,
      runtimeAvailableByServiceId,
      scopeId,
      units,
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
      ? `当前 Scope 里还有 ${draftUnits.length} 个已保存的行为定义，但它们还没有汇总成稳定的团队概览。`
      : "当前 Scope 里还没有可展示的团队概览。";
  const partialIssues = [
    servicesQuery.isError ? "服务目录暂时不可见。" : null,
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
                首页按这个 Scope 汇总当前团队的成员与运行态，Scope 只做上下文，不再直接当团队名展示。
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
              <SummaryStatCard accent label="当前团队" value={visibleTeamCount} />
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
                        }),
                      )
                    }
                    size="small"
                    type="primary"
                  >
                    打开 Studio
                  </Button>
                }
                description={`其中 ${draftUnits.length} 个行为定义还停留在草稿阶段，团队概览还不完整。`}
                showIcon
                title="还有草稿待整理"
                type="info"
              />
            ) : null}

            {workflowsQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在整理当前团队概览。" />
            ) : workflowsQuery.isError ? (
              <Alert
                showIcon
                title="当前 Scope 的团队概览暂时无法加载。"
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
                      当前团队
                    </Typography.Title>
                    <Typography.Text type="secondary">
                      当前 Scope 下这个团队的成员与运行概况。
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
