import {
  ApiOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProList } from "@ant-design/pro-components";
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
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionSnapshot,
  ServiceTrafficEndpointSnapshot,
} from "@/shared/models/services";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  resolveAevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import { summaryFieldGridStyle, summaryFieldLabelStyle } from "@/shared/ui/proComponents";
import { theme } from "antd";

type SummaryMetricProps = {
  label: string;
  tone?: "default" | "error" | "info" | "success" | "warning";
  value: React.ReactNode;
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

const initialDraft = readServiceQueryDraft();

function buildServiceSubtitle(service: ServiceCatalogSnapshot): string {
  return `${service.namespace}/${service.serviceId}`;
}

function buildServiceSummary(service: ServiceCatalogSnapshot): string {
  const segments = [
    service.endpoints.length > 0
      ? `${service.endpoints.length} endpoint${service.endpoints.length === 1 ? "" : "s"}`
      : "No endpoints",
    service.policyIds.length > 0
      ? `${service.policyIds.length} polic${service.policyIds.length === 1 ? "y" : "ies"}`
      : "No governance policy",
    service.activeServingRevisionId || service.defaultServingRevisionId
      ? `Serving ${service.activeServingRevisionId || service.defaultServingRevisionId}`
      : "No serving revision",
  ];

  return segments.join(" · ");
}

function buildServiceDigestMetrics(services: readonly ServiceCatalogSnapshot[]) {
  return {
    activeDeployments: services.filter((item) => item.deploymentId.trim()).length,
    endpoints: services.reduce((count, item) => count + item.endpoints.length, 0),
    policies: services.reduce((count, item) => count + item.policyIds.length, 0),
    services: services.length,
  };
}

const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = "default",
  value,
}) => {
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
      <Typography.Text style={{ ...summaryFieldLabelStyle, color: visual.labelColor }}>
        {label}
      </Typography.Text>
      <Typography.Text strong style={{ color: visual.valueColor, fontSize: 18 }}>
        {value}
      </Typography.Text>
    </div>
  );
};

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text>{value}</Typography.Text>
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

  const metas = useMemo<ProListMetas<ServiceCatalogSnapshot>>(
    () => ({
      actions: {
        render: (_, service) => [
          <Button
            icon={<EyeOutlined />}
            key={`${service.serviceKey}-inspect`}
            onClick={() => setSelectedServiceId(service.serviceId)}
            type="link"
          >
            Inspect
          </Button>,
          <Button
            icon={<SafetyCertificateOutlined />}
            key={`${service.serviceKey}-governance`}
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
            type="link"
          >
            Governance
          </Button>,
        ],
      },
      avatar: {
        render: () => <ApiOutlined style={{ fontSize: 18 }} />,
      },
      content: {
        render: (_, service) => (
          <div style={{ display: "grid", gap: 8, gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", width: "100%" }}>
            <SummaryField label="Identity" value={buildServiceSubtitle(service)} />
            <SummaryField
              label="Deployment"
              value={`${service.deploymentStatus || "unknown"}${
                service.deploymentId ? ` · ${service.deploymentId}` : ""
              }`}
            />
            <SummaryField
              label="Primary actor"
              value={service.primaryActorId || "n/a"}
            />
            <SummaryField label="Updated" value={formatDateTime(service.updatedAt)} />
          </div>
        ),
      },
      description: {
        render: (_, service) => buildServiceSummary(service),
      },
      subTitle: {
        render: (_, service) => (
          <Space size={[8, 8]} wrap>
            <AevatarStatusTag
              domain="governance"
              status={service.deploymentStatus || "draft"}
            />
            <AevatarStatusTag
              domain="observation"
              label={`${service.endpoints.length} endpoints`}
              status={service.endpoints.length > 0 ? "live" : "snapshot_available"}
            />
          </Space>
        ),
      },
      title: {
        render: (_, service) => service.displayName || service.serviceId,
      },
    }),
    [],
  );

  const latestRevision = selectedRevisions[0] ?? null;
  const activeDeployment = selectedDeployments[0] ?? null;

  return (
    <AevatarPageShell
      layoutMode="document"
      title="Services"
      titleHelp="Services now live inside a single high-density workbench. Catalog rows stay on stage while lifecycle detail, deployment posture, and governance context move into the right-side inspector."
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title="Scope"
              titleHelp="Filter the raw tenant/app/namespace surface when you need operator-level diagnostics."
            >
              <ServiceQueryCard
                draft={draft}
                onChange={setDraft}
                onLoad={() => setQuery(trimServiceQuery(draft))}
                onReset={() => {
                  const nextDraft = readServiceQueryDraft("");
                  setDraft(nextDraft);
                  setQuery(trimServiceQuery(nextDraft));
                  setSelectedServiceId("");
                }}
              />
            </AevatarPanel>

            <AevatarPanel layoutMode="document" title="Control Digest">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                }}
              >
                <SummaryMetric label="Services" value={digest.services} />
                <SummaryMetric
                  label="Deployments"
                  tone="success"
                  value={digest.activeDeployments}
                />
                <SummaryMetric
                  label="Endpoints"
                  tone="info"
                  value={digest.endpoints}
                />
                <SummaryMetric
                  label="Policies"
                  tone="warning"
                  value={digest.policies}
                />
              </div>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" title="Next Actions">
              <Space direction="vertical" size={8} style={{ width: "100%" }}>
                <Button onClick={() => history.push("/deployments")}>
                  Open Deployments
                </Button>
                <Button onClick={() => history.push("/governance")}>
                  Open Governance
                </Button>
                <Button onClick={() => history.push(buildRuntimeRunsHref())}>
                  Open Runs
                </Button>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            layoutMode="document"
            title="Service Workbench"
            titleHelp="The stage keeps service identity, serving posture, and operator actions visible at once. Deep lifecycle detail only appears when you explicitly inspect a service."
          >
            {servicesQuery.error ? (
              <Alert
                title={
                  servicesQuery.error instanceof Error
                    ? servicesQuery.error.message
                    : "Failed to load services."
                }
                showIcon
                type="error"
              />
            ) : null}

            <ProList<ServiceCatalogSnapshot>
              dataSource={servicesQuery.data ?? []}
              loading={servicesQuery.isLoading}
              metas={metas}
              pagination={{ defaultPageSize: 8, showSizeChanger: false }}
              rowKey="serviceKey"
              split={false}
              showActions="hover"
              itemCardProps={{
                bodyStyle: { padding: 16 },
                style: { borderRadius: 12 },
              }}
              grid={{ gutter: 16, column: 1 }}
              locale={{
                emptyText: (
                  <Empty
                    description="No services matched the current scope."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              }}
            />
          </AevatarPanel>
        }
      />

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
                Rollout
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
                  Runtime
                </Button>
              ) : null}
            </Space>
          ) : null
        }
        onClose={() => setSelectedServiceId("")}
        open={Boolean(selectedServiceId)}
        subtitle={
          selectedService
            ? `${selectedService.namespace}/${selectedService.serviceId}`
            : "Operator inspector"
        }
        title={selectedService?.displayName || selectedServiceId || "Service Inspector"}
      >
        {!selectedService ? (
          <AevatarInspectorEmpty description="Choose a service to inspect revisions, deployments, and traffic posture without leaving the catalog." />
        ) : (
          <>
            <AevatarPanel
              title="Lifecycle Snapshot"
              titleHelp="Core service identity and current serving posture."
            >
              <div style={summaryFieldGridStyle}>
                <SummaryField label="Service key" value={selectedService.serviceKey} />
                <SummaryField
                  label="Serving revision"
                  value={
                    selectedService.activeServingRevisionId ||
                    selectedService.defaultServingRevisionId ||
                    "n/a"
                  }
                />
                <SummaryField
                  label="Deployment"
                  value={`${selectedService.deploymentStatus || "unknown"}${
                    selectedService.deploymentId ? ` · ${selectedService.deploymentId}` : ""
                  }`}
                />
                <SummaryField
                  label="Updated"
                  value={formatDateTime(selectedService.updatedAt)}
                />
              </div>
            </AevatarPanel>

            <AevatarPanel
              title="Endpoints"
              titleHelp="Endpoint surface stays compact here so the stage can focus on service cards."
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                {selectedService.endpoints.length > 0 ? (
                  selectedService.endpoints.map((endpoint) => (
                    <div
                      key={endpoint.endpointId}
                      style={{
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 12,
                        padding: 12,
                      }}
                    >
                      <Space direction="vertical" size={2}>
                        <Space wrap size={[8, 8]}>
                          <Typography.Text strong>
                            {endpoint.displayName || endpoint.endpointId}
                          </Typography.Text>
                          <AevatarStatusTag
                            domain="observation"
                            label={endpoint.kind || "endpoint"}
                            status="live"
                          />
                        </Space>
                        <Typography.Text type="secondary">
                          {endpoint.requestTypeUrl || "No request contract"}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {endpoint.description || "No endpoint description."}
                        </Typography.Text>
                      </Space>
                    </div>
                  ))
                ) : (
                  <Empty
                    description="No endpoint catalog has been attached yet."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                )}
              </div>
            </AevatarPanel>

            <AevatarPanel
              title="Rollout Posture"
              titleHelp="Revision, deployment, and traffic signals are grouped here instead of split across dedicated pages."
            >
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
    </AevatarPageShell>
  );
};

const RevisionDigestCard: React.FC<{
  revision: ServiceRevisionSnapshot;
}> = ({ revision }) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 6,
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]}>
      <Typography.Text strong>{revision.revisionId}</Typography.Text>
      <AevatarStatusTag domain="governance" status={revision.status || "draft"} />
    </Space>
    <Typography.Text type="secondary">
      {revision.implementationKind || "Unknown implementation"} · {revision.artifactHash || "No artifact hash"}
    </Typography.Text>
    <Typography.Text type="secondary">
      Published {formatDateTime(revision.publishedAt)} · Prepared {formatDateTime(revision.preparedAt)}
    </Typography.Text>
  </div>
);

const DeploymentDigestCard: React.FC<{
  deployment: ServiceDeploymentSnapshot;
}> = ({ deployment }) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 6,
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]}>
      <DeploymentUnitOutlined />
      <Typography.Text strong>{deployment.deploymentId}</Typography.Text>
      <AevatarStatusTag domain="governance" status={deployment.status || "pending"} />
    </Space>
    <Typography.Text type="secondary">
      Revision {deployment.revisionId || "n/a"} · Actor {deployment.primaryActorId || "n/a"}
    </Typography.Text>
    <Typography.Text type="secondary">
      Activated {formatDateTime(deployment.activatedAt)} · Updated {formatDateTime(deployment.updatedAt)}
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
        gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
      }}
    >
      <SummaryMetric
        label="Active deployment"
        tone={resolveAevatarSemanticTone("governance", activeDeployment?.status || "pending")}
        value={activeDeployment?.deploymentId || "n/a"}
      />
      <SummaryMetric
        label="Latest revision"
        tone={resolveAevatarSemanticTone("governance", latestRevision?.status || "draft")}
        value={latestRevision?.revisionId || "n/a"}
      />
      <SummaryMetric
        label="Traffic endpoints"
        tone="info"
        value={traffic.length}
      />
      <SummaryMetric
        label="Weight ceiling"
        tone={dominantTrafficWeight >= 100 ? "success" : "warning"}
        value={`${dominantTrafficWeight}%`}
      />
    </div>
  );
};

export default ServicesPage;
