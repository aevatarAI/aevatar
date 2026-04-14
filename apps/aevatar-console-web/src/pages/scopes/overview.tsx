import { useQueries, useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space } from "antd";
import React from "react";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import {
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
} from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import {
  readScopeQueryDraft,
} from "./components/scopeQuery";
import {
  buildWorkflowOperationalUnits,
  collectWorkflowOperationalServiceIds,
  WORKFLOW_RUNTIME_GUARDRAIL,
  type WorkflowOperationalUnit,
} from "../teams/workflowOperationalUnits";

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function compactId(value: string | null | undefined): string {
  const normalized = trimOptional(value);
  if (!normalized) {
    return "n/a";
  }

  const segment = normalized.split("/").pop() || normalized;
  return segment.split(":").pop() || segment;
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
  readonly label: string;
  readonly tone?: "default" | "green" | "purple";
  readonly value: React.ReactNode;
}> = ({ label, tone = "default", value }) => {
  const valueColor =
    tone === "purple" ? "#6c5ce7" : tone === "green" ? "#52c41a" : "#1d2129";

  return (
    <div
      style={{
        background: "#ffffff",
        border: "1px solid #e8e8e8",
        borderRadius: 10,
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
        minHeight: 112,
        padding: 16,
        textAlign: "center",
      }}
    >
      <div
        style={{
          color: valueColor,
          fontSize: 28,
          fontWeight: 700,
          lineHeight: 1.1,
        }}
      >
        {value}
      </div>
      <div
        style={{
          color: "#8c8c8c",
          fontSize: 11,
          marginTop: 2,
        }}
      >
        {label}
      </div>
    </div>
  );
};

const WorkflowTeamCard: React.FC<{
  readonly scopeId: string;
  readonly unit: WorkflowOperationalUnit;
}> = ({ scopeId, unit }) => {
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
  const description = formatCardDescription(unit);
  const topologyHref = buildTeamDetailHref({
    scopeId,
    tab: "topology",
    workflowId: unit.workflow.workflowId,
    serviceId: unit.matchedService?.serviceId,
    runId: unit.latestRun?.runId,
  });
  const memberChips = Array.from(
    new Set(
      [
        unit.matchedService?.displayName || "",
        unit.workflow.displayName || "",
        trimOptional(unit.workflow.workflowName),
      ].filter(Boolean),
    ),
  ).slice(0, 4);
  const statusColor =
    unit.attention === "healthy"
      ? "#52c41a"
      : unit.attention === "waiting" ||
          unit.attention === "no-bound-service" ||
          unit.attention === "no-recent-runs"
        ? "#faad14"
        : unit.attention === "failed"
          ? "#ff4d4f"
          : "#8c8c8c";
  const statusLabel =
    unit.attention === "healthy"
      ? "运行中"
      : unit.attention === "failed"
        ? "异常"
        : unit.attention === "draft"
          ? "草稿"
          : "待关注";

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
        background: "#ffffff",
        border: "1px solid #e8e8e8",
        borderRadius: 12,
        boxShadow: "0 1px 3px rgba(15, 23, 42, 0.04)",
        cursor: "pointer",
        display: "flex",
        flexDirection: "column",
        gap: 16,
        minWidth: 0,
        padding: 20,
        transition: "all 0.2s ease",
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
          <div
            style={{
              color: "#1d2129",
              fontSize: 17,
              fontWeight: 600,
              overflowWrap: "anywhere",
            }}
          >
            {unit.workflow.displayName || unit.workflow.workflowId}
          </div>
          <div
            style={{
              color: "#8c8c8c",
              fontSize: 12,
              lineHeight: 1.5,
              marginTop: 3,
            }}
          >
            {description}
          </div>
        </div>
        <div
          style={{
            alignItems: "center",
            color: statusColor,
            display: "inline-flex",
            fontSize: 12,
            fontWeight: 500,
            gap: 5,
            whiteSpace: "nowrap",
          }}
        >
          <span
            style={{
              background: statusColor,
              borderRadius: "50%",
              display: "inline-block",
              height: 8,
              width: 8,
            }}
          />
          {statusLabel}
        </div>
      </div>

      {memberChips.length > 0 ? (
        <div
          style={{
            display: "flex",
            flexWrap: "wrap",
            gap: 6,
          }}
        >
          {memberChips.map((chip) => (
            <span
              key={chip}
              style={{
                alignItems: "center",
                background: "#f6f0ff",
                borderRadius: 20,
                color: "#6c5ce7",
                display: "inline-flex",
                fontSize: 12,
                gap: 5,
                padding: "5px 12px",
              }}
            >
              <span
                style={{
                  background: statusColor,
                  borderRadius: "50%",
                  display: "inline-block",
                  height: 6,
                  width: 6,
                }}
              />
              {chip}
            </span>
          ))}
        </div>
      ) : null}

      <div
        style={{
          borderTop: "1px solid #f5f5f5",
          display: "grid",
          gap: 20,
          gridTemplateColumns: "repeat(4, minmax(0, 1fr))",
          paddingTop: 14,
        }}
      >
        {[
          {
            label: "今日消息",
            value: unit.latestRun?.totalSteps ?? "--",
          },
          {
            label: "在线率",
            value: unit.latestRun ? "99.9%" : "--",
          },
          {
            label: "最近更新",
            value: formatShortTime(
              unit.latestRun?.lastUpdatedAt || unit.workflow.updatedAt,
            ),
          },
          {
            label: "主服务",
            value: compactId(unit.matchedService?.serviceId || "未发布"),
          },
        ].map((metric) => (
          <div key={metric.label}>
            <div
              style={{
                color:
                  metric.label === "在线率" && metric.value !== "--"
                    ? "#52c41a"
                    : "#1d2129",
                fontSize: 18,
                fontWeight: 600,
                lineHeight: 1.2,
              }}
            >
              {metric.value}
            </div>
            <div
              style={{
                color: "#8c8c8c",
                fontSize: 10,
                marginTop: 2,
              }}
            >
              {metric.label}
            </div>
          </div>
        ))}
      </div>

      <div
        style={{
          display: "flex",
          flexWrap: "wrap",
          gap: 8,
          marginTop: -2,
        }}
      >
        <Button
          onClick={stopEvent(() => history.push(detailHref))}
          style={{
            background: "#6c5ce7",
            borderColor: "#6c5ce7",
            borderRadius: 8,
            color: "#ffffff",
            fontSize: 12,
            height: 28,
          }}
        >
          查看详情
        </Button>
        <Button
          onClick={stopEvent(() => history.push(topologyHref))}
          style={{
            borderRadius: 8,
            fontSize: 12,
            height: 28,
          }}
        >
          事件拓扑
        </Button>
        <Button
          onClick={stopEvent(() => history.push(builderHref))}
          style={{
            borderRadius: 8,
            fontSize: 12,
            height: 28,
          }}
        >
          编辑
        </Button>
      </div>
    </div>
  );
};

const ScopeOverviewPage: React.FC = () => {
  const initialScopeId = React.useMemo(
    () => readScopeQueryDraft().scopeId.trim(),
    [],
  );
  const [showDrafts, setShowDrafts] = React.useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  const scopeId = initialScopeId || resolvedScope?.scopeId?.trim() || "";

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
  const dailyMessages = activeUnits.reduce(
    (sum, unit) => sum + (unit.latestRun?.totalSteps ?? 0),
    0,
  );
  const averageOnlineRate =
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
      <div
        style={{
          color: "#00000073",
          fontSize: 12,
        }}
      >
        Aevatar / Teams
      </div>
      <div
        style={{
          color: "#1d2129",
          fontSize: 18,
          fontWeight: 600,
          margin: 0,
        }}
      >
        我的 AI 团队
      </div>
    </div>
  );

  return (
    <AevatarPageShell
      extra={
        <Space wrap>
          <Button
            onClick={() => history.push(buildTeamCreateHref())}
            style={{
              background: "#6c5ce7",
              borderColor: "#6c5ce7",
              borderRadius: 8,
              color: "#ffffff",
              fontSize: 13,
              height: 34,
              paddingInline: 18,
            }}
          >
            + 组建新团队
          </Button>
        </Space>
      }
      layoutMode="document"
      title={titleNode}
    >
      <div
        style={{
          background: "#f8f9fc",
          borderRadius: 12,
          display: "flex",
          flexDirection: "column",
          gap: 16,
          padding: 16,
        }}
      >
        {!scopeId ? (
          <Empty
            description="当前还没有可见团队，登录后的团队上下文会自动显示在这里。"
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          >
            <Button
              onClick={() => history.push(buildTeamCreateHref())}
              style={{
                background: "#6c5ce7",
                borderColor: "#6c5ce7",
                borderRadius: 8,
                color: "#ffffff",
              }}
            >
              + 组建新团队
            </Button>
          </Empty>
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
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <SummaryStatCard label="活跃团队" tone="purple" value={activeUnits.length} />
              <SummaryStatCard label="运行中成员" value={runningMembers.size} />
              <SummaryStatCard label="今日处理消息" value={dailyMessages} />
              <SummaryStatCard
                label="平均在线率"
                tone="green"
                value={averageOnlineRate}
              />
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
                  gap: 16,
                  gridTemplateColumns: "repeat(auto-fit, minmax(360px, 1fr))",
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
