import { PlusOutlined } from "@ant-design/icons";
import { useQueries, useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Typography, theme } from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { studioApi } from "@/shared/studio/api";
import {
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import ScopeQueryCard from "./components/ScopeQueryCard";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "./components/scopeQuery";
import {
  buildWorkflowOperationalUnits,
  collectWorkflowOperationalServiceIds,
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalAttention,
  type WorkflowOperationalUnit,
} from "../teams/workflowOperationalUnits";

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
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

function formatCardDescription(unit: WorkflowOperationalUnit): string {
  switch (unit.attention) {
    case "waiting":
      return "最近一次执行正在等待人工确认或外部信号。";
    case "failed":
      return "最近一次执行出现异常，建议先进入详情排查。";
    case "healthy":
      return "最近一次执行正常，可继续查看运行状态和配置。";
    case "no-bound-service":
      return "已存在 workflow 定义，但当前还没有匹配到可运行的服务入口。";
    case "no-recent-runs":
      return "已存在 service 记录，但当前还没有可见的运行信号。";
    case "draft":
      return "当前仍处在搭建阶段，建议先完成服务绑定或首次运行。";
    case "runtime-unresolved":
      return "当前运行信号暂时不完整，建议稍后刷新或进入详情继续查看。";
    default:
      return unit.attentionDetail;
  }
}

function formatShortTime(value: string | null | undefined): string {
  const normalized = trimOptional(value);
  if (!normalized) {
    return "--";
  }

  const parsed = new Date(normalized);
  if (Number.isNaN(parsed.getTime())) {
    return "--";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
  }).format(parsed);
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
        borderRadius: 24,
        boxShadow: token.boxShadowTertiary,
        display: "flex",
        flexDirection: "column",
        gap: 10,
        minHeight: 128,
        padding: 22,
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
      <Typography.Text
        style={{
          color: token.colorTextSecondary,
          fontSize: 15,
        }}
      >
        {label}
      </Typography.Text>
    </div>
  );
};

const EvidencePill: React.FC<{
  readonly text: string;
}> = ({ text }) => {
  const { token } = theme.useToken();

  return (
    <span
      style={{
        background: token.colorInfoBg,
        border: `1px solid ${token.colorInfoBorder}`,
        borderRadius: 999,
        color: token.colorInfo,
        display: "inline-flex",
        fontSize: 13,
        fontWeight: 500,
        lineHeight: 1,
        padding: "9px 12px",
      }}
    >
      {text}
    </span>
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
      gap: 6,
      minWidth: 0,
    }}
  >
    <Typography.Title
      level={4}
      style={{
        margin: 0,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </Typography.Title>
    <Typography.Text type="secondary">{label}</Typography.Text>
  </div>
);

const WorkflowTeamCard: React.FC<{
  readonly scopeId: string;
  readonly unit: WorkflowOperationalUnit;
}> = ({ scopeId, unit }) => {
  const { token } = theme.useToken();
  const detailHref = buildTeamDetailHref({
    scopeId,
    workflowId: unit.workflow.workflowId,
    serviceId: unit.matchedService?.serviceId,
    runId: unit.latestRun?.runId,
  });
  const builderHref = buildStudioWorkflowEditorRoute({
    scopeId,
    workflowId: unit.workflow.workflowId,
  });
  const runtimeHref = unit.matchedService
    ? buildRuntimeRunsHref({
        actorId: unit.latestRun?.actorId || undefined,
        route: unit.workflow.workflowName || undefined,
        scopeId,
        serviceId: unit.matchedService.serviceId,
      })
    : "";
  const description = formatCardDescription(unit);
  const factChips = [
    trimOptional(unit.workflow.workflowName),
    unit.matchedService?.displayName || "",
    formatRunStatusLabel(unit.latestRun?.completionStatus),
  ].filter(Boolean);

  return (
    <div
      onClick={() => history.push(detailHref)}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          history.push(detailHref);
        }
      }}
      role="button"
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 28,
        boxShadow: token.boxShadowTertiary,
        cursor: "pointer",
        display: "flex",
        flexDirection: "column",
        gap: 18,
        minWidth: 0,
        padding: 22,
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
              margin: 0,
              overflowWrap: "anywhere",
            }}
          >
            {unit.workflow.displayName || unit.workflow.workflowId}
          </Typography.Title>
          <Typography.Paragraph
            style={{
              color: token.colorTextSecondary,
              marginBottom: 0,
              marginTop: 8,
            }}
          >
            {description}
          </Typography.Paragraph>
        </div>
        <span
          style={{
            ...resolveAttentionPillStyle(token, unit.attention),
            borderRadius: 999,
            display: "inline-flex",
            fontSize: 13,
            fontWeight: 600,
            lineHeight: 1,
            padding: "10px 14px",
            whiteSpace: "nowrap",
          }}
        >
          {formatAttentionLabel(unit.attention)}
        </span>
      </div>

      <Space size={[10, 10]} wrap>
        {factChips.map((chip) => (
          <EvidencePill key={chip} text={chip} />
        ))}
      </Space>

      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: "grid",
          gap: 18,
          gridTemplateColumns: "repeat(auto-fit, minmax(130px, 1fr))",
          paddingTop: 18,
        }}
      >
        <TeamFact
          label="最近步骤"
          value={unit.latestRun?.totalSteps ?? "--"}
        />
        <TeamFact
          label="角色响应"
          value={unit.latestRun?.roleReplyCount ?? "--"}
        />
        <TeamFact
          label="最近更新"
          value={formatShortTime(unit.latestRun?.lastUpdatedAt || unit.workflow.updatedAt)}
        />
        <TeamFact
          label="主服务"
          value={unit.matchedService?.serviceId || "未发布"}
        />
      </div>

      <Space wrap>
        <Button
          onClick={stopEvent(() => history.push(detailHref))}
          type="primary"
        >
          查看团队
        </Button>
        {runtimeHref ? (
          <Button onClick={stopEvent(() => history.push(runtimeHref))}>
            查看运行
          </Button>
        ) : null}
        <Button onClick={stopEvent(() => history.push(builderHref))}>
          高级编辑
        </Button>
      </Space>
    </div>
  );
};

const ScopeOverviewPage: React.FC = () => {
  const { token } = theme.useToken();
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(initialDraft);
  const [showDrafts, setShowDrafts] = React.useState(false);
  const [showScopePicker, setShowScopePicker] = React.useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

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
  const runtimeSampleServiceIds = matchedServiceIds.slice(
    0,
    WORKFLOW_RUNTIME_GUARDRAIL,
  );
  const guardrailedServiceIds = React.useMemo(
    () => new Set(matchedServiceIds.slice(WORKFLOW_RUNTIME_GUARDRAIL)),
    [matchedServiceIds],
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

  const draftUnits = units.filter((unit) => unit.isDraftOnly);
  const visibleUnits = showDrafts
    ? units
    : units.filter((unit) => !unit.isDraftOnly);
  const activeUnits = units.filter((unit) => !unit.isDraftOnly);
  const healthyUnits = activeUnits.filter((unit) => unit.attention === "healthy");
  const runningMembers = new Set(
    activeUnits.map((unit) =>
      trimOptional(unit.latestRun?.actorId || unit.workflow.actorId),
    ),
  );
  runningMembers.delete("");
  const visibleRuns = activeUnits.filter((unit) => unit.latestRun).length;
  const healthRate =
    activeUnits.length > 0
      ? `${((healthyUnits.length / activeUnits.length) * 100).toFixed(1)}%`
      : "--";
  const partialIssues = [
    servicesQuery.isError ? "服务目录暂时不可见。" : null,
    bindingQuery.isError ? "当前 Scope 绑定信息暂时不可见。" : null,
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

        {scopeId ? (
          <>
            <div
              style={{
                display: "grid",
                gap: 16,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              <SummaryStatCard label="活跃团队" value={activeUnits.length} />
              <SummaryStatCard label="运行中成员" value={runningMembers.size} />
              <SummaryStatCard label="可见运行" value={visibleRuns} />
              <SummaryStatCard accent label="健康团队率" value={healthRate} />
            </div>

            {workflowsQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在整理当前 Scope 下的团队卡片。" />
            ) : workflowsQuery.isError ? (
              <Alert
                showIcon
                title="当前 Scope 下的团队列表暂时无法加载。"
                type="error"
              />
            ) : visibleUnits.length > 0 ? (
              <div
                style={{
                  display: "grid",
                  gap: 18,
                  gridTemplateColumns: "repeat(auto-fit, minmax(420px, 1fr))",
                }}
              >
                {visibleUnits.map((unit) => (
                  <WorkflowTeamCard
                    key={unit.workflow.workflowId}
                    scopeId={scopeId}
                    unit={unit}
                  />
                ))}
              </div>
            ) : (
              <Empty
                description="当前 Scope 里还没有可展示的团队。"
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
                  打开工作流空间
                </Button>
              </Empty>
            )}

            {draftUnits.length > 0 ? (
              <div style={{ display: "flex", justifyContent: "center" }}>
                <Button onClick={() => setShowDrafts((value) => !value)}>
                  {showDrafts
                    ? `隐藏草稿团队 (${draftUnits.length})`
                    : `显示草稿团队 (${draftUnits.length})`}
                </Button>
              </div>
            ) : null}

          </>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

export default ScopeOverviewPage;
