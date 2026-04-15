import {
  DeploymentUnitOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import ServiceQueryCard from "@/pages/services/components/ServiceQueryCard";
import {
  buildServiceDetailHref,
  buildServicesHref,
  readServiceIdFromPathname,
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from "@/pages/services/components/serviceQuery";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import {
  buildRuntimeExplorerHref,
} from "@/shared/navigation/runtimeRoutes";
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentSnapshot,
  ServiceIdentityQuery,
  ServiceEndpointSnapshot,
  ServiceRevisionSnapshot,
  ServiceTrafficEndpointSnapshot,
} from "@/shared/models/services";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";
import ConsoleMetricCard from "@/shared/ui/ConsoleMetricCard";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { describeError } from "@/shared/ui/errorText";
import {
  codeBlockStyle,
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

const initialDraft = readServiceQueryDraft();
const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";

function buildServiceSubtitle(service: ServiceCatalogSnapshot): string {
  return `${service.namespace}/${service.serviceId}`;
}

function buildServiceDigestMetrics(services: readonly ServiceCatalogSnapshot[]) {
  return {
    activeDeployments: services.filter((item) => item.deploymentId.trim()).length,
    endpoints: services.reduce((count, item) => count + item.endpoints.length, 0),
    policies: services.reduce((count, item) => count + item.policyIds.length, 0),
    services: services.length,
  };
}

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text>{value}</Typography.Text>
  </div>
);

const drawerListItemStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: "var(--ant-color-fill-quaternary)",
  display: "flex",
  flexDirection: "column",
  gap: 8,
  padding: 12,
};

const DrawerMetric: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div style={{ ...summaryMetricStyle, gap: 6, minHeight: 0 }}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        fontSize: 14,
        overflowWrap: "anywhere",
        wordBreak: "break-word",
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const EndpointRow: React.FC<{
  endpoint: ServiceEndpointSnapshot;
}> = ({ endpoint }) => (
  <div style={drawerListItemStyle}>
    <div
      style={{
        alignItems: "center",
        display: "flex",
        gap: 8,
        justifyContent: "space-between",
      }}
    >
      <Typography.Text strong>
        {endpoint.displayName || endpoint.endpointId}
      </Typography.Text>
      <AevatarStatusTag
        domain="observation"
        label={endpoint.kind || "endpoint"}
        status="live"
      />
    </div>
    <Typography.Text
      style={{
        color: "var(--ant-color-text-secondary)",
        fontFamily: '"SF Mono", monospace',
        fontSize: 11,
        overflowWrap: "anywhere",
        wordBreak: "break-word",
      }}
    >
      {endpoint.requestTypeUrl || endpoint.endpointId}
    </Typography.Text>
    <Typography.Text type="secondary">
      {endpoint.responseTypeUrl || "暂无响应契约"}
    </Typography.Text>
  </div>
);

const ServicesPage: React.FC = () => {
  const [draft, setDraft] = useState<ServiceQueryDraft>(initialDraft);
  const [query, setQuery] = useState<ServiceIdentityQuery>(
    trimServiceQuery(initialDraft),
  );
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    readServiceIdFromPathname(),
  );
  const authSessionQuery = useQuery({
    queryKey: ["services", "auth-session"],
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
    queryKey: ["services", query],
    queryFn: () => servicesApi.listServices(query),
  });
  const selectedServiceQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: ["services", "detail", selectedServiceId, query],
    queryFn: () => servicesApi.getService(selectedServiceId, query),
  });
  const revisionsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: ["services", "revisions", selectedServiceId, query],
    queryFn: () => servicesApi.getRevisions(selectedServiceId, query),
  });
  const deploymentsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: ["services", "deployments", selectedServiceId, query],
    queryFn: () => servicesApi.getDeployments(selectedServiceId, query),
  });
  const trafficQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: ["services", "traffic", selectedServiceId, query],
    queryFn: () => servicesApi.getTraffic(selectedServiceId, query),
  });

  useEffect(() => {
    history.replace(
      selectedServiceId
        ? buildServiceDetailHref(selectedServiceId, query)
        : buildServicesHref(query),
    );
  }, [query, selectedServiceId]);

  useEffect(() => {
    if (
      selectedServiceId &&
      !(servicesQuery.data ?? []).some(
        (service) => service.serviceId === selectedServiceId,
      )
    ) {
      setSelectedServiceId("");
    }
  }, [selectedServiceId, servicesQuery.data]);

  const digest = useMemo(
    () => buildServiceDigestMetrics(servicesQuery.data ?? []),
    [servicesQuery.data],
  );

  const selectedService = selectedServiceQuery.data;
  const selectedRevisions = revisionsQuery.data?.revisions ?? [];
  const selectedDeployments = deploymentsQuery.data?.deployments ?? [];
  const selectedTraffic = trafficQuery.data?.endpoints ?? [];

  const latestRevision = selectedRevisions[0] ?? null;
  const activeDeployment = selectedDeployments[0] ?? null;

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      title="Services"
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
        <ServiceQueryCard
          draft={draft}
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
          }}
        />

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(4, minmax(0, 1fr))",
          }}
        >
          <ConsoleMetricCard label="服务数" value={digest.services} tone="purple" />
          <ConsoleMetricCard
            label="运行中部署"
            value={digest.activeDeployments}
          />
          <ConsoleMetricCard label="可用入口" value={digest.endpoints} />
          <ConsoleMetricCard
            label="治理策略"
            tone="green"
            value={digest.policies}
          />
        </div>

        <AevatarPanel
          layoutMode="document"
          padding={0}
          title="服务清单"
        >
          {servicesQuery.error ? (
            <Alert
              description="请稍后刷新，或先切换到其他团队上下文。"
              showIcon
              title={describeError(servicesQuery.error, "服务目录暂时不可用。")}
              type="warning"
            />
          ) : null}

          {servicesQuery.data?.length ? (
            <div style={{ overflowX: "auto" }}>
              <table
                style={{
                  background: "#ffffff",
                  borderCollapse: "collapse",
                  borderTop: "1px solid #f0f0f0",
                  width: "100%",
                }}
              >
                <thead>
                  <tr>
                    {["状态", "名称", "标识", "部署", "入口", "更新时间", "操作"].map(
                      (label) => (
                        <th
                          key={label}
                          style={{
                            background: "#fafafa",
                            borderBottom: "1px solid #f0f0f0",
                            color: "#8c8c8c",
                            fontSize: 11,
                            fontWeight: 500,
                            letterSpacing: 0.3,
                            padding: "10px 14px",
                            textAlign: "left",
                            textTransform: "uppercase",
                            whiteSpace: "nowrap",
                          }}
                        >
                          {label}
                        </th>
                      ),
                    )}
                  </tr>
                </thead>
                <tbody>
                  {(servicesQuery.data ?? []).map((service) => (
                    <tr key={service.serviceKey}>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          padding: "12px 14px",
                          verticalAlign: "top",
                        }}
                      >
                        <AevatarStatusTag
                          domain="governance"
                          status={service.deploymentStatus || "draft"}
                        />
                      </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          padding: "12px 14px",
                          verticalAlign: "top",
                        }}
                      >
                        <div
                          style={{
                            color: "#1d2129",
                            fontSize: 13,
                            fontWeight: 600,
                          }}
                        >
                          {service.displayName || service.serviceId}
                        </div>
                      </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          color: "#595959",
                          fontFamily: '"SF Mono", monospace',
                          fontSize: 11,
                          padding: "12px 14px",
                          verticalAlign: "top",
                        }}
                      >
                        {buildServiceSubtitle(service)}
                      </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          padding: "12px 14px",
                          verticalAlign: "top",
                        }}
                        >
                          {service.deploymentId || "暂无"}
                        </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          padding: "12px 14px",
                          verticalAlign: "top",
                        }}
                      >
                        {service.endpoints.length}
                      </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          color: "#595959",
                          padding: "12px 14px",
                          verticalAlign: "top",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {formatDateTime(service.updatedAt)}
                      </td>
                      <td
                        style={{
                          borderBottom: "1px solid #f5f5f5",
                          padding: "12px 14px",
                          verticalAlign: "top",
                          whiteSpace: "nowrap",
                        }}
                      >
                        <Space size={[8, 8]} wrap>
                          <Button
                            onClick={() => setSelectedServiceId(service.serviceId)}
                            size="small"
                          >
                            详情
                          </Button>
                          <Button
                            onClick={() =>
                              history.push(
                                `/governance/bindings?tenantId=${encodeURIComponent(
                                  service.tenantId,
                                )}&appId=${encodeURIComponent(
                                  service.appId,
                                )}&namespace=${encodeURIComponent(
                                  service.namespace,
                                )}&serviceId=${encodeURIComponent(service.serviceId)}`,
                              )
                            }
                            size="small"
                          >
                            治理
                          </Button>
                        </Space>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <Empty
              description="暂无服务"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              style={{ padding: 24 }}
            />
          )}
        </AevatarPanel>
      </div>

      <AevatarContextDrawer
        extra={
          selectedService ? (
            <Space>
              <Button
                onClick={() =>
                  history.push(
                    `/deployments?tenantId=${encodeURIComponent(
                      selectedService.tenantId,
                    )}&appId=${encodeURIComponent(
                      selectedService.appId,
                    )}&namespace=${encodeURIComponent(
                      selectedService.namespace,
                    )}&serviceId=${encodeURIComponent(selectedService.serviceId)}`,
                  )
                }
              >
                发布管理
              </Button>
              {selectedService.primaryActorId ? (
                <Button
                  onClick={() =>
                    history.push(
                      buildRuntimeExplorerHref({
                        actorId: selectedService.primaryActorId,
                      }),
                    )
                  }
                >
                  运行记录
                </Button>
              ) : null}
            </Space>
          ) : null
        }
        onClose={() => setSelectedServiceId("")}
        open={Boolean(selectedServiceId)}
        width={820}
        subtitle={
          selectedService
            ? `${selectedService.namespace}/${selectedService.serviceId}`
            : "服务"
        }
        title={selectedService?.displayName || selectedServiceId || "服务"}
      >
        {!selectedService ? (
          <AevatarInspectorEmpty description="请选择服务" />
        ) : (
          <>
            <AevatarPanel title="当前信息">
              <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                <div>
                  <Typography.Text style={summaryFieldLabelStyle}>
                    服务 Key
                  </Typography.Text>
                  <div
                    style={{
                      ...codeBlockStyle,
                      marginTop: 8,
                      maxHeight: "none",
                      whiteSpace: "pre-wrap",
                    }}
                  >
                    {selectedService.serviceKey}
                  </div>
                </div>

                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="服务版本"
                    value={
                      selectedService.activeServingRevisionId ||
                      selectedService.defaultServingRevisionId ||
                      "暂无"
                    }
                  />
                  <SummaryField
                    label="当前部署"
                    value={selectedService.deploymentId || "暂无"}
                  />
                  <SummaryField
                    label="主成员"
                    value={selectedService.primaryActorId || "暂无"}
                  />
                  <SummaryField
                    label="最近更新"
                    value={formatDateTime(selectedService.updatedAt)}
                  />
                </div>
              </div>
            </AevatarPanel>

            <AevatarPanel title="入口">
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                {selectedService.endpoints.length > 0 ? (
                  selectedService.endpoints.map((endpoint) => (
                    <EndpointRow endpoint={endpoint} key={endpoint.endpointId} />
                  ))
                ) : (
                  <Empty
                    description="暂无入口"
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                )}
              </div>
            </AevatarPanel>

            <AevatarPanel title="部署与版本">
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                <RolloutDigestSection
                  activeDeployment={activeDeployment}
                  latestRevision={latestRevision}
                  traffic={selectedTraffic}
                />
                {selectedRevisions.slice(0, 3).map((revision) => (
                  <RevisionDigestCard key={revision.revisionId} revision={revision} />
                ))}
                {selectedDeployments.slice(0, 2).map((deployment) => (
                  <DeploymentDigestCard
                    deployment={deployment}
                    key={deployment.deploymentId}
                  />
                ))}
              </div>
            </AevatarPanel>
          </>
        )}
      </AevatarContextDrawer>
    </ConsoleMenuPageShell>
  );
};

const RevisionDigestCard: React.FC<{
  revision: ServiceRevisionSnapshot;
}> = ({ revision }) => (
  <div style={drawerListItemStyle}>
    <Space wrap size={[8, 8]}>
      <Typography.Text strong>{revision.revisionId}</Typography.Text>
      <AevatarStatusTag domain="governance" status={revision.status || "draft"} />
    </Space>
    <Typography.Text style={summaryFieldLabelStyle}>
      {revision.implementationKind || "工作流"}
    </Typography.Text>
    <Typography.Text
      style={{
        color: "var(--ant-color-text-secondary)",
        fontFamily: '"SF Mono", monospace',
        fontSize: 11,
        overflowWrap: "anywhere",
      }}
    >
      {revision.artifactHash || "暂无"}
    </Typography.Text>
    <Typography.Text type="secondary">
      发布时间 {formatDateTime(revision.publishedAt)}
    </Typography.Text>
  </div>
);

const DeploymentDigestCard: React.FC<{
  deployment: ServiceDeploymentSnapshot;
}> = ({ deployment }) => (
  <div style={drawerListItemStyle}>
    <Space wrap size={[8, 8]}>
      <DeploymentUnitOutlined />
      <Typography.Text strong>{deployment.deploymentId}</Typography.Text>
      <AevatarStatusTag domain="governance" status={deployment.status || "pending"} />
    </Space>
    <Typography.Text type="secondary">
      版本 {deployment.revisionId || "暂无"}
    </Typography.Text>
    <Typography.Text type="secondary">
      主成员 {deployment.primaryActorId || "暂无"}
    </Typography.Text>
    <Typography.Text type="secondary">
      启用时间 {formatDateTime(deployment.activatedAt)}
    </Typography.Text>
  </div>
);

const RolloutDigestSection: React.FC<{
  activeDeployment: ServiceDeploymentSnapshot | null;
  latestRevision: ServiceRevisionSnapshot | null;
  traffic: ServiceTrafficEndpointSnapshot[];
}> = ({ activeDeployment, latestRevision, traffic }) => {
  const dominantTrafficWeight = traffic.reduce((maxWeight, endpoint) => {
    const endpointWeight = endpoint.targets.reduce(
      (total, target) => total + target.allocationWeight,
      0,
    );
    return Math.max(maxWeight, endpointWeight);
  }, 0);

  return (
    <div
      style={{
        display: "grid",
        gap: 12,
        gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
      }}
    >
      <DrawerMetric
        label="当前部署"
        value={activeDeployment?.deploymentId || "暂无"}
      />
      <DrawerMetric
        label="最新版本"
        value={latestRevision?.revisionId || "暂无"}
      />
      <DrawerMetric label="入口数" value={traffic.length} />
      <DrawerMetric label="最大流量权重" value={`${dominantTrafficWeight}%`} />
    </div>
  );
};

export default ServicesPage;
