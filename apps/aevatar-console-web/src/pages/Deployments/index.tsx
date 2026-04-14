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
import { PageContainer, ProCard, ProList } from "@ant-design/pro-components";
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
import { AevatarTitleWithHelp } from "@/shared/ui/aevatarPageShells";

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
          ? `Deployment ${service.deploymentId} is serving revision ${
              service.activeServingRevisionId ||
              service.defaultServingRevisionId ||
              "n/a"
            }.`
          : "No active deployment has been assigned yet.",
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
        label: "Revision",
        value: "Unavailable",
      },
    ];
  }

  return [
    {
      label: "Revision",
      value: revision.revisionId,
    },
    {
      label: "Status",
      value: formatAevatarStatusLabel(revision.status || "unknown"),
    },
    {
      label: "Endpoints",
      value: String(revision.endpoints.length),
    },
    {
      label: "Artifact",
      value: revision.artifactHash || "n/a",
    },
    {
      label: "Prepared",
      value: formatDateTime(revision.preparedAt),
    },
    {
      label: "Published",
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
        throw new Error("Choose a candidate revision before starting a rollout.");
      }

      return servicesApi.deployRevision(selectedServiceId, {
        ...query,
        revisionId: candidateRevisionId,
      });
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || "Failed to dispatch the candidate revision.",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "Candidate revision accepted by the deployment control plane.",
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
        message: error.message || "Failed to update serving targets.",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "Serving targets accepted by the deployment control plane.",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const rolloutMutation = useMutation({
    mutationFn: async (kind: "advance" | "pause" | "resume" | "rollback") => {
      const rolloutId = rolloutQuery.data?.rolloutId;
      if (!rolloutId) {
        throw new Error("No active rollout is available for this service.");
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
        message: error.message || "Failed to dispatch rollout action.",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "Rollout action accepted by the serving control plane.",
        tone: "success",
      });
      await invalidateDetailQueries();
    },
  });

  const deactivateMutation = useMutation({
    mutationFn: () => {
      if (!selectedDeployment?.deploymentId.trim()) {
        throw new Error("Select a deployment before deactivating it.");
      }

      return servicesApi.deactivateDeployment(
        selectedServiceId,
        selectedDeployment.deploymentId,
        query,
      );
    },
    onError: (error: Error) => {
      setNotice({
        message: error.message || "Failed to deactivate the selected deployment.",
        tone: "error",
      });
    },
    onSuccess: async () => {
      setNotice({
        message: "Deployment deactivation was accepted.",
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
            Open detail
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
              <Tag>{record.deploymentId || "Unassigned"}</Tag>
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
            {record.rolloutId ? <Tag color="blue">Rollout {record.rolloutId}</Tag> : null}
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Space orientation="vertical" size={2}>
            <Typography.Text strong>{record.title}</Typography.Text>
            <Typography.Text type="secondary">
              Last synced {record.updatedAt}
            </Typography.Text>
          </Space>
        ),
      },
    }),
    [surfaceToken],
  );

  return (
    <PageContainer
      className="aevatar-page-shell-document"
      title={
        <AevatarTitleWithHelp
          help="Deployment center focused on rollout visibility. The list stays concise, the detail column stays readable, and rollout policy, weight, and rollback controls live inside one extra-wide drawer."
          title="Deployments"
        />
      }
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
          loadLabel="Load deployments"
          onChange={setDraft}
          onLoad={() => setQuery(trimServiceQuery(draft))}
          onReset={() => {
            const nextDraft = readServiceQueryDraft("");
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
            title="Deployment Inventory"
          >
            <ProList<DeploymentWorkbenchItem>
              dataSource={items}
              locale={{
                emptyText: servicesQuery.isLoading
                  ? "Loading deployments…"
                  : "No services matched the current identity query.",
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
                        Policy & Weight
                      </Button>
                      <Button
                        icon={<SendOutlined />}
                        onClick={() => openDrawer("compare")}
                      >
                        Rollout Strategy
                      </Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        onClick={() => openDrawer("rollback")}
                      >
                        Rollback
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
                      label="Active revision"
                      tone="success"
                      value={activeRevisionId || "n/a"}
                    />
                    <MetricCard
                      label="Deployment status"
                      tone="info"
                      value={serviceDetailQuery.data.deploymentStatus || "n/a"}
                    />
                    <MetricCard
                      label="Current rollout"
                      value={rolloutQuery.data?.rolloutId || "No rollout"}
                    />
                    <MetricCard
                      label="Endpoints"
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
                    title="Dual Revision Compare"
                    extra={
                      <Select
                        options={(revisionsQuery.data?.revisions ?? []).map((revision) => ({
                          label: revision.revisionId,
                          value: revision.revisionId,
                        }))}
                        placeholder="Choose candidate revision"
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
                        label="Current Serving Revision"
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label="Candidate Revision"
                        revision={candidateRevision}
                      />
                    </div>
                    <Typography.Text type="secondary">
                      Traffic view:{" "}
                      {trafficQuery.data?.endpoints
                        .map(
                          (endpoint) =>
                            `${endpoint.endpointId} -> ${endpoint.targets
                              .map(
                                (target) =>
                                  `${target.revisionId}:${target.allocationWeight}%`,
                              )
                              .join(", ")}`,
                        )
                        .join(" | ") || "No traffic materialized yet."}
                    </Typography.Text>
                  </ProCard>

                  <ProCard
                    bodyStyle={{ display: "flex", flexDirection: "column", gap: 12 }}
                    style={buildAevatarPanelStyle(surfaceToken)}
                    title="Deployment Detail"
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
                          Actor {selectedDeployment.primaryActorId}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          Activated {formatDateTime(selectedDeployment.activatedAt)}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          Updated {formatDateTime(selectedDeployment.updatedAt)}
                        </Typography.Text>
                        <MetricCard
                          label="Current stage"
                          tone="warning"
                          value={
                            currentStage
                              ? `${currentStage.stageIndex + 1} / ${
                                  rolloutQuery.data?.stages.length || 1
                                }`
                              : "No stage"
                          }
                        />
                        <Button
                          danger
                          icon={<StopOutlined />}
                          loading={deactivateMutation.isPending}
                          onClick={() => deactivateMutation.mutate()}
                        >
                          Deactivate deployment
                        </Button>
                      </>
                    ) : (
                      <Empty
                        description="Choose a deployment from the inventory to inspect its detail."
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                  </ProCard>
                </div>
              </>
            ) : (
              <ProCard style={buildAevatarPanelStyle(surfaceToken)}>
                <Empty
                  description="Select a deployment from the left list to open the two-column detail view."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              </ProCard>
            )}
          </div>
        </div>
      </div>

      <Drawer
        open={drawerState.open}
        size="large"
        title="Deployment Control Drawer"
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
            <Typography.Paragraph
              style={{ color: surfaceToken.colorTextSecondary, marginBottom: 0, marginTop: 10 }}
            >
              Keep the detail page focused on observability. All rollout policy,
              weight shifts, and rollback actions are staged here.
            </Typography.Paragraph>
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
                        title="Candidate Selector"
                      >
                        <Select
                          options={(revisionsQuery.data?.revisions ?? []).map((revision) => ({
                            label: `${revision.revisionId} · ${formatAevatarStatusLabel(
                              revision.status,
                            )}`,
                            value: revision.revisionId,
                          }))}
                          placeholder="Choose candidate revision"
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
                          Deploy candidate revision
                        </Button>
                      </ProCard>
                      <RevisionSummaryCard
                        label="Current Serving Revision"
                        revision={activeRevision}
                      />
                      <RevisionSummaryCard
                        label="Candidate Revision"
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
                        label="Baseline"
                        targets={rolloutQuery.data?.baselineTargets ?? []}
                      />
                      <TargetGroupCard
                        label="Canary / Active Stage"
                        targets={currentStage?.targets ?? servingQuery.data?.targets ?? []}
                      />
                    </div>
                  </div>
                ),
                key: "compare",
                label: "Compare",
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
                                "All endpoints enabled"}
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
                        description="No serving targets are available to edit."
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                    <Input.TextArea
                      placeholder="Reason for this canary or weight change"
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
                      Apply weights
                    </Button>
                  </div>
                ),
                key: "weights",
                label: "Policy & Weight",
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
                      label="Rollout"
                      tone="warning"
                      value={rolloutQuery.data?.rolloutId || "No active rollout"}
                    />
                    <Input.TextArea
                      placeholder="Reason for pause or rollback"
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
                        Advance rollout
                      </Button>
                      <Button
                        icon={<PauseCircleOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("pause")}
                      >
                        Pause
                      </Button>
                      <Button
                        icon={<ReloadOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("resume")}
                      >
                        Resume
                      </Button>
                      <Button
                        danger
                        icon={<RollbackOutlined />}
                        loading={rolloutMutation.isPending}
                        onClick={() => rolloutMutation.mutate("rollback")}
                      >
                        Rollback rollout
                      </Button>
                    </Space>
                  </div>
                ),
                key: "rollback",
                label: "Rollback",
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
    </PageContainer>
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
          No revision selected for this side of the compare view.
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
        <Typography.Text type="secondary">No targets materialized.</Typography.Text>
      )}
    </div>
  );
};

export default DeploymentsPage;
