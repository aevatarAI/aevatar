import {
  DeploymentUnitOutlined,
  EyeOutlined,
  PauseCircleOutlined,
  PercentageOutlined,
  ReloadOutlined,
  RollbackOutlined,
  SendOutlined,
  StopOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProCard, ProList } from "@ant-design/pro-components";
import {
  useMutation,
  useQuery,
  useQueryClient,
} from "@tanstack/react-query";
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Input,
  InputNumber,
  Select,
  Space,
  Tabs,
  Tag,
  Typography,
  theme,
} from "antd";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import ServiceQueryCard from "@/pages/services/components/ServiceQueryCard";
import {
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from "@/pages/services/components/serviceQuery";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import type {
  ServiceCatalogSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionSnapshot,
  ServiceServingTargetInput,
  ServiceServingTargetSnapshot,
} from "@/shared/models/services";
import {
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarMetricCardStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  resolveAevatarMetricVisual,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import ConsoleMetricCard from "@/shared/ui/ConsoleMetricCard";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { describeError } from "@/shared/ui/errorText";

type DeploymentDrawerTab = "compare" | "rollback" | "weights";

type DeploymentDrawerState = {
  open: boolean;
  tab: DeploymentDrawerTab;
};

type DeploymentNotice = {
  message: string;
  tone: "error" | "info" | "success" | "warning";
};

type DeploymentWorkbenchItem = {
  activeRevisionId: string;
  deploymentId: string;
  rolloutId: string;
  serviceId: string;
  serviceKey: string;
  status: string;
  subtitle: string;
  summary: string;
  title: string;
  updatedAt: string;
};

const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";

function readSelectedServiceId(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("serviceId")?.trim() ?? ""
  );
}

function readSelectedDeploymentId(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("deploymentId")?.trim() ?? ""
  );
}

function buildDeploymentsHref(
  query: ServiceIdentityQuery,
  serviceId?: string,
  deploymentId?: string,
): string {
  const params = new URLSearchParams();

  if (query.tenantId?.trim()) {
    params.set("tenantId", query.tenantId.trim());
  }
  if (query.appId?.trim()) {
    params.set("appId", query.appId.trim());
  }
  if (query.namespace?.trim()) {
    params.set("namespace", query.namespace.trim());
  }
  if (query.take && query.take > 0) {
    params.set("take", String(query.take));
  }
  if (serviceId?.trim()) {
    params.set("serviceId", serviceId.trim());
  }
  if (deploymentId?.trim()) {
    params.set("deploymentId", deploymentId.trim());
  }

  const suffix = params.toString();
  return suffix ? `/deployments?${suffix}` : "/deployments";
}

function buildDeploymentItems(
  services: ServiceCatalogSnapshot[] | undefined,
  rolloutIdByService: Record<string, string>,
): DeploymentWorkbenchItem[] {
  return (services ?? [])
    .map((service) => ({
      activeRevisionId:
        service.activeServingRevisionId ||
        service.defaultServingRevisionId ||
        "n/a",
      deploymentId: service.deploymentId || "",
      rolloutId: rolloutIdByService[service.serviceId] || "",
      serviceId: service.serviceId,
      serviceKey: service.serviceKey,
      status: service.deploymentStatus || "pending",
      subtitle: `${service.namespace}/${service.serviceId}`,
      summary:
        service.deploymentId.trim().length > 0
          ? `部署 ${service.deploymentId} 正在提供版本 ${
              service.activeServingRevisionId ||
              service.defaultServingRevisionId ||
              "暂无"
            }。`
          : "暂无已启用部署。",
      title: service.displayName || service.serviceId,
      updatedAt: formatDateTime(service.updatedAt),
    }))
    .sort((left, right) => left.title.localeCompare(right.title));
}

function buildRevisionSummary(
  revision: ServiceRevisionSnapshot | null | undefined,
): Array<{ label: string; value: string }> {
  if (!revision) {
    return [
      {
        label: "版本",
        value: "暂无",
      },
    ];
  }

  return [
    {
      label: "版本",
      value: revision.revisionId,
    },
    {
      label: "状态",
      value: formatAevatarStatusLabel(revision.status || "unknown"),
    },
    {
      label: "入口数",
      value: String(revision.endpoints.length),
    },
    {
      label: "制品",
      value: revision.artifactHash || "暂无",
    },
    {
      label: "准备时间",
      value: formatDateTime(revision.preparedAt),
    },
    {
      label: "发布时间",
      value: formatDateTime(revision.publishedAt),
    },
  ];
}

const WorkbenchStatusTag: React.FC<{
  domain: AevatarStatusDomain;
  status: string;
}> = ({ domain, status }) => {
  const { token } = theme.useToken();

  return (
    <Tag
      bordered
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        domain,
        status,
      )}
    >
      {formatAevatarStatusLabel(status)}
    </Tag>
  );
};

const MetricCard: React.FC<{
  label: string;
  tone?: "default" | "info" | "success" | "warning";
  value: string;
}> = ({ label, tone = "default", value }) => {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={buildAevatarMetricCardStyle(
        token as AevatarThemeSurfaceToken,
        tone,
      )}
    >
      <Typography.Text style={{ color: visual.labelColor }}>{label}</Typography.Text>
      <Typography.Text strong style={{ color: visual.valueColor }}>
        {value}
      </Typography.Text>
    </div>
  );
};

const DeploymentEmptyStateCard: React.FC<{
  title: string;
  description: string;
  highlights?: Array<{ label: string; value: string }>;
}> = ({ title, description, highlights = [] }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        ...buildAevatarPanelStyle(surfaceToken, {
          background: surfaceToken.colorFillAlter,
          padding: 20,
        }),
        boxShadow: "none",
      }}
    >
      <Space direction="vertical" size={14} style={{ display: "flex" }}>
        <Space direction="vertical" size={4} style={{ display: "flex" }}>
          <Typography.Text strong>{title}</Typography.Text>
          <Typography.Text type="secondary">{description}</Typography.Text>
        </Space>
        {highlights.length > 0 ? (
          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
            }}
          >
            {highlights.map((item) => (
              <MetricCard key={item.label} label={item.label} value={item.value} />
            ))}
          </div>
        ) : null}
      </Space>
    </div>
  );
};

const DeploymentsPage: React.FC = () => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const queryClient = useQueryClient();

  const [draft, setDraft] = useState<ServiceQueryDraft>(() =>
    readServiceQueryDraft(),
  );
  const [query, setQuery] = useState<ServiceIdentityQuery>(() =>
    trimServiceQuery(readServiceQueryDraft()),
  );
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    readSelectedServiceId(),
  );
  const [selectedDeploymentId, setSelectedDeploymentId] = useState(() =>
    readSelectedDeploymentId(),
  );
  const [drawerState, setDrawerState] = useState<DeploymentDrawerState>({
    open: false,
    tab: "weights",
  });
  const [drawerReason, setDrawerReason] = useState("");
  const [editableTargets, setEditableTargets] = useState<ServiceServingTargetInput[]>(
    [],
  );
  const [candidateRevisionId, setCandidateRevisionId] = useState("");
  const [notice, setNotice] = useState<DeploymentNotice | null>(null);
  const authSessionQuery = useQuery({
    queryKey: ["deployments", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    if (
      draft.tenantId.trim() ||
      draft.appId.trim() ||
      draft.namespace.trim() ||
      !resolvedScope?.scopeId?.trim()
    ) {
      return;
    }

    const nextDraft = {
      ...draft,
      appId: defaultScopeServiceAppId,
      namespace: defaultScopeServiceNamespace,
      tenantId: resolvedScope.scopeId.trim(),
    };
    setDraft(nextDraft);
    setQuery(trimServiceQuery(nextDraft));
  }, [draft, resolvedScope?.scopeId]);

  const servicesQuery = useQuery({
    queryFn: () => servicesApi.listServices(query),
    queryKey: ["deployments", "services", query],
  });

  const serviceDetailQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getService(selectedServiceId, query),
    queryKey: ["deployments", "service", query, selectedServiceId],
  });
  const revisionsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRevisions(selectedServiceId, query),
    queryKey: ["deployments", "revisions", query, selectedServiceId],
  });
  const deploymentsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getDeployments(selectedServiceId, query),
    queryKey: ["deployments", "catalog", query, selectedServiceId],
  });
  const servingQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getServingSet(selectedServiceId, query),
    queryKey: ["deployments", "serving", query, selectedServiceId],
  });
  const rolloutQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getRollout(selectedServiceId, query),
    queryKey: ["deployments", "rollout", query, selectedServiceId],
  });
  const trafficQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryFn: () => servicesApi.getTraffic(selectedServiceId, query),
    queryKey: ["deployments", "traffic", query, selectedServiceId],
  });

  useEffect(() => {
    const services = servicesQuery.data ?? [];
    if (!services.length) {
      if (selectedServiceId) {
        setSelectedServiceId("");
      }
      if (selectedDeploymentId) {
        setSelectedDeploymentId("");
      }
      return;
    }

    if (!selectedServiceId.trim()) {
      return;
    }

    if (services.some((service) => service.serviceId === selectedServiceId)) {
      return;
    }

    setSelectedServiceId("");
    if (selectedDeploymentId) {
      setSelectedDeploymentId("");
    }
  }, [selectedDeploymentId, selectedServiceId, servicesQuery.data]);

  useEffect(() => {
    history.replace(
      buildDeploymentsHref(query, selectedServiceId, selectedDeploymentId),
    );
  }, [query, selectedDeploymentId, selectedServiceId]);

  useEffect(() => {
    const deployments = deploymentsQuery.data?.deployments ?? [];
    if (!selectedServiceId.trim()) {
      if (selectedDeploymentId) {
        setSelectedDeploymentId("");
      }
      return;
    }

    if (!selectedDeploymentId) {
      return;
    }

    if (
      deployments.some(
        (deployment) => deployment.deploymentId === selectedDeploymentId,
      )
    ) {
      return;
    }

    setSelectedDeploymentId("");
  }, [
    deploymentsQuery.data?.deployments,
    selectedDeploymentId,
    selectedServiceId,
  ]);

  useEffect(() => {
    setEditableTargets(
      (servingQuery.data?.targets ?? []).map((target) => ({
        allocationWeight: target.allocationWeight,
        enabledEndpointIds: target.enabledEndpointIds,
        revisionId: target.revisionId,
        servingState: target.servingState,
      })),
    );
  }, [servingQuery.data?.updatedAt]);

  const activeRevisionId =
    serviceDetailQuery.data?.activeServingRevisionId ||
    serviceDetailQuery.data?.defaultServingRevisionId ||
    "";

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!revisions.length) {
      return;
    }

    if (
      candidateRevisionId.trim() &&
      revisions.some((revision) => revision.revisionId === candidateRevisionId)
    ) {
      return;
    }

    const preferred =
      revisions.find((revision) => revision.revisionId !== activeRevisionId) ??
      revisions[0];

    setCandidateRevisionId(preferred?.revisionId ?? "");
  }, [activeRevisionId, candidateRevisionId, revisionsQuery.data?.revisions]);

  const rolloutIdByService = useMemo(
    () =>
      selectedServiceId.trim() && rolloutQuery.data?.rolloutId
        ? { [selectedServiceId]: rolloutQuery.data.rolloutId }
        : {},
    [rolloutQuery.data?.rolloutId, selectedServiceId],
  );

  const items = useMemo(
    () => buildDeploymentItems(servicesQuery.data, rolloutIdByService),
    [rolloutIdByService, servicesQuery.data],
  );
  const deploymentDigest = useMemo(
    () => ({
      deployments: items.filter((item) => item.deploymentId.trim()).length,
      revisions: revisionsQuery.data?.revisions.length ?? 0,
      rollouts: items.filter((item) => item.rolloutId.trim()).length,
      services: items.length,
    }),
    [items, revisionsQuery.data?.revisions.length],
  );

  const selectedDeployment = useMemo(
    () =>
      deploymentsQuery.data?.deployments.find(
        (deployment) => deployment.deploymentId === selectedDeploymentId,
      ) ?? null,
    [deploymentsQuery.data?.deployments, selectedDeploymentId],
  );

  const currentStage = useMemo(() => {
    const rollout = rolloutQuery.data;
    if (!rollout?.stages.length) {
      return null;
    }

    return (
      rollout.stages.find(
        (stage) => stage.stageIndex === rollout.currentStageIndex,
      ) ?? rollout.stages[rollout.stages.length - 1]
    );
  }, [rolloutQuery.data]);

  const activeRevision = useMemo(
    () =>
      revisionsQuery.data?.revisions.find(
        (revision) => revision.revisionId === activeRevisionId,
      ) ?? null,
    [activeRevisionId, revisionsQuery.data?.revisions],
  );

  const candidateRevision = useMemo(
    () =>
      revisionsQuery.data?.revisions.find(
        (revision) => revision.revisionId === candidateRevisionId,
      ) ?? null,
    [candidateRevisionId, revisionsQuery.data?.revisions],
  );

  const invalidateDetailQueries = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["deployments", "service"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "revisions"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "catalog"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "serving"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "rollout"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "traffic"] }),
      queryClient.invalidateQueries({ queryKey: ["deployments", "services"] }),
    ]);
  }, [queryClient]);

  const openDrawer = useCallback((tab: DeploymentDrawerTab) => {
    setDrawerState({
      open: true,
      tab,
    });
  }, []);

  const deployMutation = useMutation({
    mutationFn: () => {
      if (!candidateRevisionId.trim()) {
        throw new Error("请先选择候选版本。");
      }

      return servicesApi.deployRevision(selectedServiceId, {
        ...query,
        revisionId: candidateRevisionId,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: describeError(error, "发布候选版本失败。"),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "候选版本已进入部署流程。",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const weightsMutation = useMutation({
    mutationFn: () =>
      servicesApi.replaceServingTargets(selectedServiceId, {
        ...query,
        reason: drawerReason,
        rolloutId: rolloutQuery.data?.rolloutId,
        targets: editableTargets,
      }),
    onError: (error: Error) => {
      setNotice({
        message: describeError(error, "更新流量目标失败。"),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "流量目标已更新。",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const rolloutMutation = useMutation({
    mutationFn: async (kind: "advance" | "pause" | "resume" | "rollback") => {
      const rolloutId = rolloutQuery.data?.rolloutId;
      if (!rolloutId) {
        throw new Error("当前服务没有进行中的发布。");
      }

      if (kind === "advance") {
        return servicesApi.advanceRollout(selectedServiceId, rolloutId, query);
      }

      if (kind === "pause") {
        return servicesApi.pauseRollout(selectedServiceId, rolloutId, {
          ...query,
          reason: drawerReason,
        });
      }

      if (kind === "resume") {
        return servicesApi.resumeRollout(selectedServiceId, rolloutId, query);
      }

      return servicesApi.rollbackRollout(selectedServiceId, rolloutId, {
        ...query,
        reason: drawerReason,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: describeError(error, "发布操作失败。"),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "发布操作已生效。",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const deactivateMutation = useMutation({
    mutationFn: () => {
      if (!selectedDeployment?.deploymentId.trim()) {
        throw new Error("请先选择部署。");
      }

      return servicesApi.deactivateDeployment(
        selectedServiceId,
        selectedDeployment.deploymentId,
        query,
      );
    },
    onError: (error: Error) => {
      setNotice({
        message: describeError(error, "停用部署失败。"),
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "部署已停用。",
        tone: "warning",
      });
      await invalidateDetailQueries();
    },
  });

  const listMetas = useMemo<ProListMetas<DeploymentWorkbenchItem>>(
    () => ({
      actions: {
        render: (_, record) => [
          <Button
            key={`detail-${record.serviceId}`}
            icon={<EyeOutlined />}
            onClick={() => {
              setSelectedServiceId(record.serviceId);
              setSelectedDeploymentId(record.deploymentId);
            }}
          >
            详情
          </Button>,
        ],
      },
      avatar: {
        render: () => (
          <div
            style={{
              alignItems: "center",
              background: surfaceToken.colorFillTertiary,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadiusLG,
              color: surfaceToken.colorPrimary,
              display: "inline-flex",
              height: 36,
              justifyContent: "center",
              width: 36,
            }}
          >
            <DeploymentUnitOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
              {record.summary}
            </Typography.Text>
            <Space wrap size={[8, 8]}>
              <Tag>{record.serviceKey}</Tag>
              <Tag>{record.deploymentId || "未分配"}</Tag>
              <Tag>{record.activeRevisionId}</Tag>
            </Space>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.subtitle}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag domain="governance" status={record.status} />
            {record.rolloutId ? <Tag color="blue">发布 {record.rolloutId}</Tag> : null}
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>{record.title}</Typography.Text>
            <Typography.Text type="secondary">
              最近同步 {record.updatedAt}
            </Typography.Text>
          </Space>
        ),
      },
    }),
    [surfaceToken],
  );

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      title="Deployments"
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {notice ? (
          <Alert
            closable
            message={notice.message}
            showIcon
            type={notice.tone}
            onClose={() => setNotice(null)}
          />
        ) : null}

        <ServiceQueryCard
          draft={draft}
          loadLabel="加载部署"
          onChange={setDraft}
          onLoad={() => setQuery(trimServiceQuery(draft))}
          onReset={() => {
            const nextDraft = resolvedScope?.scopeId?.trim()
              ? {
                  ...readServiceQueryDraft(""),
                  appId: defaultScopeServiceAppId,
                  namespace: defaultScopeServiceNamespace,
                  tenantId: resolvedScope.scopeId.trim(),
                }
              : readServiceQueryDraft("");
            setDraft(nextDraft);
            setQuery(trimServiceQuery(nextDraft));
            setSelectedServiceId("");
            setSelectedDeploymentId("");
            setCandidateRevisionId("");
          }}
        />

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(4, minmax(0, 1fr))",
          }}
        >
          <ConsoleMetricCard label="服务数" tone="purple" value={deploymentDigest.services} />
          <ConsoleMetricCard label="活跃部署" value={deploymentDigest.deployments} />
          <ConsoleMetricCard label="滚动发布" value={deploymentDigest.rollouts} />
          <ConsoleMetricCard label="版本数" tone="green" value={deploymentDigest.revisions} />
        </div>

        <div
          style={{
            display: "grid",
            gap: 16,
            alignItems: "start",
            gridTemplateColumns: "minmax(360px, 420px) minmax(0, 1fr)",
          }}
        >
          <ProCard
            bodyStyle={{
              display: "flex",
              flexDirection: "column",
              minHeight: 0,
              padding: 16,
            }}
            style={buildAevatarPanelStyle(surfaceToken)}
            title="部署清单"
          >
            <ProList<DeploymentWorkbenchItem>
              dataSource={items}
              grid={{ gutter: 16, column: 1 }}
              itemCardProps={{
                bodyStyle: { padding: 16 },
                style: { borderRadius: 12 },
              }}
              locale={{
                emptyText: servicesQuery.isLoading ? (
                  "加载中..."
                ) : (
                  <DeploymentEmptyStateCard
                    description="先选择一个团队上下文，或等第一个版本开始部署。"
                    highlights={[
                      { label: "服务数", value: "0" },
                      { label: "活跃部署", value: "0" },
                    ]}
                    title="当前还没有部署"
                  />
                ),
              }}
              metas={listMetas}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey="serviceId"
              search={false}
              showActions="always"
              split={false}
              toolBarRender={false}
            />
          </ProCard>

          <div style={{ display: "flex", flexDirection: "column", gap: 16, minHeight: 0 }}>
            {serviceDetailQuery.data ? (
              <>
                <ProCard
                  bodyStyle={{ padding: 18 }}
                  style={buildAevatarPanelStyle(surfaceToken)}
                  title={serviceDetailQuery.data.displayName || serviceDetailQuery.data.serviceId}
                  extra={
                    <Space wrap size={[8, 8]}>
                      <Button
                        icon={<PercentageOutlined />}
                        onClick={() => openDrawer("weights")}
                      >
                        权重
                      </Button>
                      <Button
                        icon={<SendOutlined />}
                        onClick={() => openDrawer("compare")}
                      >
                        发布
                      </Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        onClick={() => openDrawer("rollback")}
                      >
                        回滚
                      </Button>
                    </Space>
                  }
                >
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
                    }}
                  >
                    <MetricCard
                      label="当前版本"
                      tone="success"
                      value={activeRevisionId || "暂无"}
                    />
                    <MetricCard
                      label="部署状态"
                      tone="info"
                      value={serviceDetailQuery.data.deploymentStatus || "暂无"}
                    />
                    <MetricCard
                      label="当前发布"
                      value={rolloutQuery.data?.rolloutId || "暂无发布"}
                    />
                    <MetricCard
                      label="入口数"
                      value={String(serviceDetailQuery.data.endpoints.length)}
                    />
                  </div>
                </ProCard>

                <div
                  style={{
                    display: "grid",
                    gap: 16,
                    gridTemplateColumns: "minmax(0, 1.2fr) minmax(320px, 0.8fr)",
                    minHeight: 0,
                  }}
                >
                  <ProCard
                    bodyStyle={{ display: "flex", flexDirection: "column", gap: 16 }}
                    style={buildAevatarPanelStyle(surfaceToken)}
                    title="版本对比"
                    extra={
                      <Select
                        options={(revisionsQuery.data?.revisions ?? []).map((revision) => ({
                          label: revision.revisionId,
                          value: revision.revisionId,
                        }))}
                        placeholder="选择候选版本"
                        style={{ minWidth: 220 }}
                        value={candidateRevisionId || undefined}
                        onChange={setCandidateRevisionId}
                      />
                    }
                  >
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
                      }}
                    >
                      <RevisionSummaryCard
                        label="当前版本"
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label="候选版本"
                        revision={candidateRevision}
                      />
                    </div>
                    <Typography.Text type="secondary">
                      {trafficQuery.data?.endpoints
                        .map(
                          (endpoint) =>
                            `${endpoint.endpointId}: ${endpoint.targets
                              .map(
                                (target) =>
                                  `${target.revisionId} ${target.allocationWeight}%`,
                              )
                              .join(", ")}`,
                        )
                        .join(" | ") || "暂无流量信息"}
                    </Typography.Text>
                  </ProCard>

                  <ProCard
                    bodyStyle={{ display: "flex", flexDirection: "column", gap: 12 }}
                    style={buildAevatarPanelStyle(surfaceToken)}
                    title="当前部署"
                  >
                    {selectedDeployment ? (
                      <>
                        <Space wrap size={[8, 8]}>
                          <WorkbenchStatusTag
                            domain="governance"
                            status={selectedDeployment.status}
                          />
                          <Tag>{selectedDeployment.deploymentId}</Tag>
                          <Tag>{selectedDeployment.revisionId}</Tag>
                        </Space>
                        <Typography.Text>
                          主成员 {selectedDeployment.primaryActorId}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          启用时间 {formatDateTime(selectedDeployment.activatedAt)}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          最近更新 {formatDateTime(selectedDeployment.updatedAt)}
                        </Typography.Text>
                        <MetricCard
                          label="当前阶段"
                          tone="warning"
                          value={
                            currentStage
                              ? `${currentStage.stageIndex + 1} / ${
                                  rolloutQuery.data?.stages.length || 1
                                }`
                              : "暂无阶段"
                          }
                        />
                        <Button
                          danger
                          icon={<StopOutlined />}
                          loading={deactivateMutation.isPending}
                          onClick={() => deactivateMutation.mutate()}
                        >
                          停用部署
                        </Button>
                      </>
                    ) : (
                      <DeploymentEmptyStateCard
                        description="当前服务还没有已启用部署，发布第一个版本后会在这里看到状态、阶段和停用入口。"
                        highlights={[
                          {
                            label: "当前版本",
                            value: activeRevisionId || "待发布",
                          },
                          {
                            label: "发布阶段",
                            value: rolloutQuery.data?.rolloutId ? "进行中" : "未开始",
                          },
                        ]}
                        title="当前还没有已启用部署"
                      />
                    )}
                  </ProCard>
                </div>
              </>
            ) : (
              <ProCard style={buildAevatarPanelStyle(surfaceToken)}>
                <DeploymentEmptyStateCard
                  description="先从左侧部署清单选择一个服务，再查看版本、权重、发布和回滚。"
                  highlights={[
                    { label: "版本对比", value: "待选择" },
                    { label: "流量权重", value: "待选择" },
                    { label: "回滚操作", value: "待选择" },
                  ]}
                  title="还没有选中部署"
                />
              </ProCard>
            )}
          </div>
        </div>
      </div>

      <Drawer
        open={drawerState.open}
        size="large"
        title="部署操作"
        styles={{
          body: aevatarDrawerBodyStyle,
          wrapper: {
            maxWidth: "94vw",
            width: 1040,
          },
        }}
        onClose={() =>
          setDrawerState((current) => ({
            ...current,
            open: false,
          }))
        }
      >
        <div style={aevatarDrawerScrollStyle}>
          <div
            style={{
              background: surfaceToken.colorFillAlter,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadiusLG,
              padding: 14,
            }}
          >
            <Space wrap size={[8, 8]}>
              <WorkbenchStatusTag
                domain="governance"
                status={serviceDetailQuery.data?.deploymentStatus || "pending"}
              />
              {rolloutQuery.data?.rolloutId ? (
                <Tag color="blue">{rolloutQuery.data.rolloutId}</Tag>
              ) : null}
              {selectedDeployment?.revisionId ? (
                <Tag>{selectedDeployment.revisionId}</Tag>
              ) : null}
            </Space>
          </div>

          <Tabs
            activeKey={drawerState.tab}
            items={[
              {
                children: (
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 16,
                    }}
                  >
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "minmax(260px, 320px) repeat(auto-fit, minmax(220px, 1fr))",
                      }}
                    >
                      <ProCard
                        bodyStyle={{ display: "flex", flexDirection: "column", gap: 12 }}
                        style={buildAevatarPanelStyle(surfaceToken)}
                        title="候选版本"
                      >
                        <Select
                          options={(revisionsQuery.data?.revisions ?? []).map((revision) => ({
                            label: `${revision.revisionId} · ${formatAevatarStatusLabel(
                              revision.status,
                            )}`,
                            value: revision.revisionId,
                          }))}
                          placeholder="选择候选版本"
                          value={candidateRevisionId || undefined}
                          onChange={setCandidateRevisionId}
                        />
                        <Button
                          disabled={
                            !candidateRevisionId.trim() ||
                            candidateRevisionId === activeRevisionId
                          }
                          icon={<SendOutlined />}
                          loading={deployMutation.isPending}
                          onClick={() => deployMutation.mutate()}
                          type="primary"
                        >
                          发布候选版本
                        </Button>
                      </ProCard>
                      <RevisionSummaryCard
                        label="当前版本"
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label="候选版本"
                        revision={candidateRevision}
                      />
                    </div>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
                      }}
                    >
                      <TargetGroupCard
                        label="基线"
                        targets={rolloutQuery.data?.baselineTargets ?? []}
                      />
                      <TargetGroupCard
                        label="灰度 / 当前阶段"
                        targets={currentStage?.targets ?? servingQuery.data?.targets ?? []}
                      />
                    </div>
                  </div>
                ),
                key: "compare",
                label: "对比",
              },
              {
                children: (
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 12,
                    }}
                  >
                    {editableTargets.length ? (
                      editableTargets.map((target, index) => (
                        <div
                          key={`${target.revisionId}-${target.servingState || "unset"}`}
                          style={{
                            background: surfaceToken.colorFillAlter,
                            border: `1px solid ${surfaceToken.colorBorderSecondary}`,
                            borderRadius: surfaceToken.borderRadiusLG,
                            display: "grid",
                            gap: 12,
                            gridTemplateColumns: "minmax(0, 1fr) 140px 160px",
                            padding: 14,
                          }}
                        >
                          <div>
                            <Typography.Text strong>{target.revisionId}</Typography.Text>
                            <Typography.Paragraph
                              style={{
                                color: surfaceToken.colorTextSecondary,
                                marginBottom: 0,
                                marginTop: 4,
                              }}
                            >
                              {target.enabledEndpointIds?.join(", ") ||
                                "已开启全部入口"}
                            </Typography.Paragraph>
                          </div>
                          <InputNumber
                            max={100}
                            min={0}
                            value={target.allocationWeight}
                            onChange={(value) =>
                              setEditableTargets((current) =>
                                current.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? {
                                        ...item,
                                        allocationWeight: Number(value) || 0,
                                      }
                                    : item,
                                ),
                              )
                            }
                          />
                          <Input
                            value={target.servingState}
                            onChange={(event) =>
                              setEditableTargets((current) =>
                                current.map((item, itemIndex) =>
                                  itemIndex === index
                                    ? {
                                        ...item,
                                        servingState: event.target.value,
                                      }
                                    : item,
                                ),
                              )
                            }
                          />
                        </div>
                      ))
                    ) : (
                      <Empty
                        description="暂无目标"
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                    <Input.TextArea
                      placeholder="填写本次权重调整原因"
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Button
                      icon={<PercentageOutlined />}
                      loading={weightsMutation.isPending}
                      onClick={() => weightsMutation.mutate()}
                      type="primary"
                    >
                      应用权重
                    </Button>
                  </div>
                ),
                key: "weights",
                label: "权重",
              },
              {
                children: (
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 12,
                    }}
                  >
                    <MetricCard
                      label="发布单"
                      tone="warning"
                      value={rolloutQuery.data?.rolloutId || "暂无进行中的发布"}
                    />
                    <Input.TextArea
                      placeholder="填写暂停或回滚原因"
                      rows={3}
                      value={drawerReason}
                      onChange={(event) => setDrawerReason(event.target.value)}
                    />
                    <Space wrap size={[8, 8]}>
                      <Button
                        icon={<SendOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("advance")}
                        type="primary"
                      >
                        推进发布
                      </Button>
                      <Button
                        icon={<PauseCircleOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("pause")}
                      >
                        暂停
                      </Button>
                      <Button
                        icon={<ReloadOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("resume")}
                      >
                        继续
                      </Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("rollback")}
                      >
                        回滚发布
                      </Button>
                    </Space>
                  </div>
                ),
                key: "rollback",
                label: "回滚",
              },
            ]}
            onChange={(key) =>
              setDrawerState({
                open: true,
                tab: key as DeploymentDrawerTab,
              })
            }
          />
        </div>
      </Drawer>
    </ConsoleMenuPageShell>
  );
};

const RevisionSummaryCard: React.FC<{
  label: string;
  revision: ServiceRevisionSnapshot | null | undefined;
}> = ({ label, revision }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        background: surfaceToken.colorFillAlter,
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: surfaceToken.borderRadiusLG,
        display: "flex",
        flexDirection: "column",
        gap: 10,
        padding: 14,
      }}
    >
      <Typography.Text strong style={{ color: surfaceToken.colorTextHeading }}>
        {label}
      </Typography.Text>
      {revision ? (
        <>
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag domain="governance" status={revision.status} />
            <Tag>{revision.revisionId}</Tag>
          </Space>
          <div
            style={{
              display: "grid",
              gap: 8,
              gridTemplateColumns: "repeat(auto-fit, minmax(120px, 1fr))",
            }}
          >
            {buildRevisionSummary(revision).map((item) => (
              <MetricCard key={`${label}-${item.label}`} label={item.label} value={item.value} />
            ))}
          </div>
        </>
      ) : (
        <Typography.Text type="secondary">
          当前侧还没有可对比的版本。
        </Typography.Text>
      )}
    </div>
  );
};

const TargetGroupCard: React.FC<{
  label: string;
  targets: readonly ServiceServingTargetSnapshot[];
}> = ({ label, targets }) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  return (
    <div
      style={{
        background: surfaceToken.colorFillAlter,
        border: `1px solid ${surfaceToken.colorBorderSecondary}`,
        borderRadius: surfaceToken.borderRadiusLG,
        display: "flex",
        flexDirection: "column",
        gap: 10,
        padding: 14,
      }}
    >
      <Typography.Text strong style={{ color: surfaceToken.colorTextHeading }}>
        {label}
      </Typography.Text>
      {targets.length ? (
        targets.map((target) => (
          <div
            key={`${label}-${target.revisionId}-${target.deploymentId}`}
            style={{
              borderTop: `1px solid ${surfaceToken.colorBorderSecondary}`,
              paddingTop: 10,
            }}
          >
            <Space wrap size={[8, 8]} style={{ marginBottom: 4 }}>
              <Tag>{target.revisionId}</Tag>
              <WorkbenchStatusTag domain="governance" status={target.servingState} />
              <Tag>{target.allocationWeight}%</Tag>
            </Space>
            <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
              {target.deploymentId} · {target.primaryActorId}
            </Typography.Text>
          </div>
        ))
      ) : (
        <Typography.Text type="secondary">暂无可见目标。</Typography.Text>
      )}
    </div>
  );
};

export default DeploymentsPage;
