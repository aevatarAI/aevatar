import {
  ApiOutlined,
  AppstoreOutlined,
  BranchesOutlined,
  DeploymentUnitOutlined,
  NodeIndexOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Tabs, Tag, Typography, theme } from "antd";
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
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
} from "@/shared/navigation/platformRoutes";
import { serviceResourceQueryKeys } from "@/shared/query/serviceResourceQueryKeys";
import { buildRuntimeExplorerHref } from "@/shared/navigation/runtimeRoutes";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
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
import { AevatarCompactText, aevatarMonoFontFamily } from "@/shared/ui/compactText";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import {
  codeBlockStyle,
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { t } from "@/shared/i18n/messages";

const initialDraft = readServiceQueryDraft();
const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";
const serviceDisplayFontFamily =
  '"Avenir Next", "SF Pro Display", "Segoe UI", sans-serif';
const serviceMonoFontFamily = aevatarMonoFontFamily;
const servicesSurfaceShadow = "0 12px 28px rgba(15, 23, 42, 0.05)";

function buildServiceDigestMetrics(services: readonly ServiceCatalogSnapshot[]) {
  return {
    services: services.length,
    servingServices: services.filter((item) => item.deploymentId.trim()).length,
    servicesWithoutEndpoints: services.filter((item) => item.endpoints.length === 0)
      .length,
    servicesWithoutOwner: services.filter((item) => !item.primaryActorId.trim()).length,
  };
}

function buildServiceSubtitle(service: ServiceCatalogSnapshot): string {
  return `${service.tenantId}/${service.appId}/${service.namespace}`;
}

function buildServingTagLabel(deploymentId: string): string {
  return deploymentId.trim() ? t("pages.services.index.serving.has.been.suspended", "serving has been suspended") : t("pages.services.index.waiting.serving", "Waiting serving");
}

function buildOwnerTagLabel(primaryActorId: string): string {
  return primaryActorId.trim() ? t("pages.services.index.main.actor.is.associated", "Main actor is associated") : t("pages.services.index.missing.main.actor", "Missing main actor");
}

function buildEndpointTagLabel(endpoints: readonly ServiceEndpointSnapshot[]): string {
  return endpoints.length > 0 ? t("pages.services.index.entries", "{value1} entries", { value1: endpoints.length }) : t("pages.services.index.no.public.entrance", "No public entrance");
}

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        color: "var(--ant-color-text)",
        fontWeight: 600,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const drawerListItemStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background:
    "linear-gradient(180deg, var(--ant-color-bg-container) 0%, var(--ant-color-fill-quaternary) 100%)",
  boxShadow: servicesSurfaceShadow,
  display: "flex",
  flexDirection: "column",
  gap: 8,
  padding: 12,
};

const drawerCardMetaRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "space-between",
};

const drawerCodeBlockStyle: React.CSSProperties = {
  ...codeBlockStyle,
  marginTop: 0,
  maxHeight: "none",
  padding: "8px 10px",
};

const tableHeaderCellStyle: React.CSSProperties = {
  background: "var(--ant-color-fill-alter)",
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.24,
  padding: "12px 14px",
  textAlign: "left",
  textTransform: "uppercase",
  whiteSpace: "nowrap",
};

const tableCellStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  padding: "12px 14px",
  verticalAlign: "top",
};

const compactHintTagStyle: React.CSSProperties = {
  borderRadius: 999,
  fontWeight: 600,
  marginInlineEnd: 0,
};

type ServiceSignalTone = "default" | "info" | "success" | "warning";

const ServiceSignalCard: React.FC<{
  caption: string;
  icon: React.ReactNode;
  label: string;
  tone: ServiceSignalTone;
  value: React.ReactNode;
}> = ({ caption, icon, label, tone, value }) => {
  const { token } = theme.useToken();
  const visuals: Record<
    ServiceSignalTone,
    {
      accent: string;
      iconBackground: string;
      iconColor: string;
      labelColor: string;
    }
  > = {
    default: {
      accent: token.colorBorderSecondary,
      iconBackground: token.colorFillAlter,
      iconColor: token.colorTextSecondary,
      labelColor: token.colorTextSecondary,
    },
    info: {
      accent: token.colorPrimaryBorder,
      iconBackground: token.colorPrimaryBg,
      iconColor: token.colorPrimary,
      labelColor: token.colorTextSecondary,
    },
    success: {
      accent: token.colorSuccessBorder,
      iconBackground: token.colorSuccessBg,
      iconColor: token.colorSuccess,
      labelColor: token.colorTextSecondary,
    },
    warning: {
      accent: token.colorWarningBorder,
      iconBackground: token.colorWarningBg,
      iconColor: token.colorWarning,
      labelColor: token.colorTextSecondary,
    },
  };
  const visual = visuals[tone];

  return (
    <div
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 18,
        boxShadow: "0 1px 2px rgba(15, 23, 42, 0.04)",
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minHeight: 0,
        padding: "12px 14px",
        position: "relative",
      }}
    >
      <div
        aria-hidden
        style={{
          background: visual.accent,
          borderRadius: 999,
          height: 3,
          left: 14,
          position: "absolute",
          right: 14,
          top: 0,
        }}
      />
      <div style={{ alignItems: "center", display: "flex", gap: 10 }}>
        <div
          style={{
            alignItems: "center",
            background: visual.iconBackground,
            borderRadius: 12,
            color: visual.iconColor,
            display: "inline-flex",
            fontSize: 15,
            height: 32,
            justifyContent: "center",
            width: 32,
          }}
        >
          {icon}
        </div>
        <div style={{ display: "flex", flex: 1, flexDirection: "column", gap: 2 }}>
          <Typography.Text
            style={{
              color: visual.labelColor,
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: 0.24,
              textTransform: "uppercase",
            }}
          >
            {label}
          </Typography.Text>
          <Typography.Paragraph
            style={{
              color: token.colorTextSecondary,
              fontSize: 11,
              lineHeight: 1.45,
              margin: 0,
            }}
          >
            {caption}
          </Typography.Paragraph>
        </div>
      </div>
      <Typography.Text
        style={{
          color: token.colorTextHeading,
          fontFamily: serviceDisplayFontFamily,
          fontSize: 24,
          fontWeight: 700,
          lineHeight: 1,
        }}
      >
        {value}
      </Typography.Text>
    </div>
  );
};

const DrawerMetric: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      ...summaryMetricStyle,
      background:
        "linear-gradient(180deg, var(--ant-color-bg-container) 0%, var(--ant-color-fill-quaternary) 100%)",
      boxShadow: servicesSurfaceShadow,
      gap: 6,
      minHeight: 0,
      padding: "12px 14px",
    }}
  >
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        fontFamily: serviceDisplayFontFamily,
        fontSize: 15,
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
    <div style={drawerCardMetaRowStyle}>
      <Space size={[8, 8]} wrap>
        <Tag color="cyan" style={compactHintTagStyle} variant="filled">
          {t("pages.services.index.entrance", "Entrance")}</Tag>
        <Typography.Text strong>
          {endpoint.displayName || endpoint.endpointId}
        </Typography.Text>
      </Space>
      <AevatarStatusTag
        domain="observation"
        label={endpoint.kind || "endpoint"}
        status="live"
      />
    </div>
    <Typography.Text style={summaryFieldLabelStyle}>{t("pages.services.index.request.type", "Request type")}</Typography.Text>
    <div
      style={{
        ...drawerCodeBlockStyle,
        color: "var(--ant-color-text-secondary)",
        fontFamily: serviceMonoFontFamily,
        fontSize: 11,
        overflowWrap: "anywhere",
        wordBreak: "break-word",
      }}
    >
      {endpoint.requestTypeUrl || endpoint.endpointId}
    </div>
  </div>
);

const RevisionDigestCard: React.FC<{
  revision: ServiceRevisionSnapshot;
}> = ({ revision }) => (
  <div style={drawerListItemStyle}>
    <div style={drawerCardMetaRowStyle}>
      <Space wrap size={[8, 8]}>
        <Tag color="purple" style={compactHintTagStyle} variant="filled">
          {t("pages.services.index.version", "Version")}</Tag>
        <Typography.Text strong>{revision.revisionId}</Typography.Text>
      </Space>
      <AevatarStatusTag domain="governance" status={revision.status || "draft"} />
    </div>
    <Typography.Text style={summaryFieldLabelStyle}>
      {revision.implementationKind || "workflow"}
    </Typography.Text>
    <div
      style={{
        ...drawerCodeBlockStyle,
        color: "var(--ant-color-text-secondary)",
        fontFamily: serviceMonoFontFamily,
        fontSize: 11,
        overflowWrap: "anywhere",
      }}
    >
      {revision.artifactHash || "n/a"}
    </div>
    <Typography.Text type="secondary">
      {t("pages.services.index.published", "Published")}{formatDateTime(revision.publishedAt)}
    </Typography.Text>
  </div>
);

const DeploymentDigestCard: React.FC<{
  deployment: ServiceDeploymentSnapshot;
}> = ({ deployment }) => (
  <div style={drawerListItemStyle}>
    <div style={drawerCardMetaRowStyle}>
      <Space wrap size={[8, 8]}>
        <Tag color="blue" style={compactHintTagStyle} variant="filled">
          {t("pages.services.index.deploy", "deploy")}</Tag>
        <DeploymentUnitOutlined />
        <Typography.Text strong>{deployment.deploymentId}</Typography.Text>
      </Space>
      <AevatarStatusTag domain="governance" status={deployment.status || "pending"} />
    </div>
    <Typography.Text style={summaryFieldLabelStyle}>
      {t("pages.services.index.version.2", "Version")}{deployment.revisionId || t("pages.services.index.unpublished", "Unpublished")}
    </Typography.Text>
    <Typography.Text type="secondary">
      {t("pages.services.index.main.actor", "Main actor")}{deployment.primaryActorId || t("pages.services.index.not.declared", "Not declared")}
    </Typography.Text>
    <Typography.Text type="secondary">
      {t("pages.services.index.activated.in", "activated in")}{formatDateTime(deployment.activatedAt)}
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
        gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))",
      }}
    >
      <DrawerMetric
        label={t("pages.services.index.current.deployment", "current deployment")}
        value={activeDeployment?.deploymentId || t("pages.services.index.not.hung.serving", "Not hung serving")}
      />
      <DrawerMetric
        label={t("pages.services.index.latest.version", "latest version")}
        value={latestRevision?.revisionId || t("pages.services.index.unpublished.2", "Unpublished")}
      />
      <DrawerMetric label={t("pages.services.index.traffic.entrance", "Traffic entrance")} value={traffic.length} />
      <DrawerMetric label={t("pages.services.index.highest.weight", "highest weight")} value={`${dominantTrafficWeight}%`} />
    </div>
  );
};

const ServicesPage: React.FC = () => {
  const { token } = theme.useToken();
  const [draft, setDraft] = useState<ServiceQueryDraft>(initialDraft);
  const [query, setQuery] = useState<ServiceIdentityQuery>(
    trimServiceQuery(initialDraft),
  );
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    readServiceIdFromPathname(),
  );
  const [detailTabKey, setDetailTabKey] = useState("endpoints");

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
    queryKey: serviceResourceQueryKeys.list(query),
    queryFn: () => servicesApi.listServices(query),
  });
  const selectedServiceQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: serviceResourceQueryKeys.detail(query, selectedServiceId),
    queryFn: () => servicesApi.getService(selectedServiceId, query),
  });
  const revisionsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: serviceResourceQueryKeys.revisions(query, selectedServiceId),
    queryFn: () => servicesApi.getRevisions(selectedServiceId, query),
  });
  const deploymentsQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: serviceResourceQueryKeys.deployments(query, selectedServiceId),
    queryFn: () => servicesApi.getDeployments(selectedServiceId, query),
  });
  const trafficQuery = useQuery({
    enabled: selectedServiceId.trim().length > 0,
    queryKey: serviceResourceQueryKeys.traffic(query, selectedServiceId),
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
    setDetailTabKey("endpoints");
  }, [selectedServiceId]);

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
  const drawerSubtitle = selectedService
    ? buildServiceSubtitle(selectedService)
    : "Service Authority";

  const scopeSignals = [
    {
      label: "Team",
      value: query.tenantId || resolvedScope?.scopeId || "All visible",
    },
    {
      label: "App",
      value: query.appId || defaultScopeServiceAppId,
    },
    {
      label: "Namespace",
      value: query.namespace || defaultScopeServiceNamespace,
    },
  ];

  const handleReset = () => {
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
  };

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      description={t("pages.services.index.services.is.the.authoritative", "Services is the authoritative service directory of Platform. It answers what services are in the current scope, where it is currently hung, and who hosts it, and guides you to continue to Governance, Deployments or Topology.")}
      title="Services"
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
        <AevatarPanel
          description={t("pages.services.index.lock.team.app.and", "Lock team, App and Namespace first, then select service objects from the table.")}
          layoutMode="document"
          title={t("pages.services.index.find.services", "Find services")}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <ServiceQueryCard
              draft={draft}
              onChange={setDraft}
              onLoad={() => setQuery(trimServiceQuery(draft))}
              onReset={handleReset}
            />

            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <ServiceSignalCard
                caption={t("pages.services.index.current.scope", "Current scope: {value1}", { value1: scopeSignals[0]?.value || "All visible" })}
                icon={<AppstoreOutlined />}
                label={t("pages.services.index.visible.services", "Visible services")}
                tone="info"
                value={digest.services}
              />
              <ServiceSignalCard
                caption={t(
                  "pages.services.index.services.attached.to.serving",
                  "Services already attached to serving",
                )}
                icon={<DeploymentUnitOutlined />}
                label={t("pages.services.index.serving.has.been.suspended.2", "serving has been suspended")}
                tone="success"
                value={digest.servingServices}
              />
              <ServiceSignalCard
                caption={t(
                  "pages.services.index.services.missing.primary.actor",
                  "Services missing a primary actor",
                )}
                icon={<NodeIndexOutlined />}
                label={t("pages.services.index.missing.actor", "Missing actor")}
                tone="warning"
                value={digest.servicesWithoutOwner}
              />
              <ServiceSignalCard
                caption={t(
                  "pages.services.index.currently.no.public.endpoint",
                  "No public endpoint currently",
                )}
                icon={<ApiOutlined />}
                label={t("pages.services.index.no.public.entrance.2", "No public entrance")}
                tone="default"
                value={digest.servicesWithoutEndpoints}
              />
            </div>
          </div>
        </AevatarPanel>

        <AevatarPanel
          description={t("pages.services.index.scan.statuses.deployments.and", "Scan statuses, deployments, and portals by row, and click a row or button to see details in the drawer.")}
          layoutMode="document"
          padding={0}
          title={t("pages.services.index.service.catalog", "Service catalog")}
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

          {servicesQuery.data?.length ? (
            <div style={{ overflowX: "auto" }}>
              <table
                style={{
                  background: token.colorBgContainer,
                  borderCollapse: "separate",
                  borderSpacing: 0,
                  width: "100%",
                }}
              >
                <thead>
                  <tr>
                    {[t("pages.services.index.state", "state"), t("pages.services.index.serve", "Serve"), t("pages.services.index.identity", "identity"), t("pages.services.index.main.actor.2", "Main actor"), "Serving", t("pages.services.index.entrance.2", "Entrance"), t("pages.services.index.update.time", "Update time"), t("pages.services.index.action", "action")].map(
                      (label) => (
                        <th key={label} style={tableHeaderCellStyle}>
                          {label}
                        </th>
                      ),
                    )}
                  </tr>
                </thead>
                <tbody>
                  {(servicesQuery.data ?? []).map((service) => {
                    const selected = service.serviceId === selectedServiceId;

                    return (
                      <tr
                        key={service.serviceKey}
                        onClick={() => setSelectedServiceId(service.serviceId)}
                        style={{
                          background: selected ? token.colorPrimaryBg : token.colorBgContainer,
                          cursor: "pointer",
                        }}
                      >
                        <td style={tableCellStyle}>
                          <AevatarStatusTag
                            domain="governance"
                            status={service.deploymentStatus || "draft"}
                          />
                        </td>
                        <td style={tableCellStyle}>
                          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                            <Typography.Text
                              style={{
                                color: token.colorTextHeading,
                                fontSize: 13,
                                fontWeight: 700,
                              }}
                            >
                              {service.displayName || service.serviceId}
                            </Typography.Text>
                            <Typography.Text
                              style={{
                                color: token.colorTextSecondary,
                                fontFamily: serviceMonoFontFamily,
                                fontSize: 10.5,
                              }}
                            >
                              <AevatarCompactText
                                head={4}
                                maxWidth={220}
                                monospace
                                tail={4}
                                value={service.serviceKey}
                              />
                            </Typography.Text>
                          </div>
                        </td>
                        <td style={tableCellStyle}>
                          <AevatarCompactText
                            color={token.colorTextSecondary}
                            head={4}
                            maxWidth={220}
                            monospace
                            style={{ fontSize: 11 }}
                            tail={4}
                            value={buildServiceSubtitle(service)}
                          />
                        </td>
                        <td style={tableCellStyle}>
                          {service.primaryActorId ? (
                            <AevatarCompactText
                              head={4}
                              maxWidth={180}
                              monospace
                              tail={4}
                              value={service.primaryActorId}
                            />
                          ) : (
                            <Typography.Text>{t("pages.services.index.not.declared.2", "Not declared")}</Typography.Text>
                          )}
                        </td>
                        <td style={tableCellStyle}>
                          <Tag
                            color={service.deploymentId ? "blue" : "default"}
                            style={compactHintTagStyle}
                          >
                            {buildServingTagLabel(service.deploymentId)}
                          </Tag>
                        </td>
                        <td style={tableCellStyle}>
                          <Tag
                            color={service.endpoints.length > 0 ? "cyan" : "default"}
                            style={compactHintTagStyle}
                          >
                            {service.endpoints.length}
                          </Tag>
                        </td>
                        <td style={{ ...tableCellStyle, whiteSpace: "nowrap" }}>
                          <Typography.Text style={{ color: token.colorTextSecondary }}>
                            {formatDateTime(service.updatedAt)}
                          </Typography.Text>
                        </td>
                        <td style={tableCellStyle}>
                          <Space size={[8, 8]} wrap>
                            <Button
                              onClick={(event) => {
                                event.stopPropagation();
                                setSelectedServiceId(service.serviceId);
                              }}
                              size="small"
                              type={selected ? "primary" : "default"}
                            >
                              {t("pages.services.index.check.the.details", "check the details")}</Button>
                            <Button
                              onClick={(event) => {
                                event.stopPropagation();
                                history.push(
                                  buildPlatformGovernanceHref({
                                    appId: service.appId,
                                    namespace: service.namespace,
                                    serviceId: service.serviceId,
                                    tenantId: service.tenantId,
                                    view: "bindings",
                                  }),
                                );
                              }}
                              size="small"
                            >
                              {t("pages.services.index.open.governance", "Open governance")}</Button>
                          </Space>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          ) : (
            <Empty
              description={t("pages.services.index.there.are.no.services", "There are no services in the current scope")}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              style={{ padding: 24 }}
            />
          )}
        </AevatarPanel>
      </div>

      <AevatarContextDrawer
        extra={
          selectedService ? (
            <Space wrap size={[8, 8]}>
              <Button
                onClick={() =>
                  history.push(
                    buildPlatformGovernanceHref({
                      appId: selectedService.appId,
                      namespace: selectedService.namespace,
                      serviceId: selectedService.serviceId,
                      tenantId: selectedService.tenantId,
                      view: "bindings",
                    }),
                  )
                }
                type="primary"
              >
                {t("pages.services.index.open.governance.2", "Open Governance")}</Button>
              <Button
                onClick={() =>
                  history.push(
                    buildPlatformDeploymentsHref({
                      appId: selectedService.appId,
                      namespace: selectedService.namespace,
                      serviceId: selectedService.serviceId,
                      tenantId: selectedService.tenantId,
                    }),
                  )
                }
              >
                {t("pages.services.index.open.deployments", "Open Deployments")}</Button>
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
                  {t("pages.services.index.open.topology", "Open Topology")}</Button>
              ) : null}
            </Space>
          ) : null
        }
        onClose={() => setSelectedServiceId("")}
        open={Boolean(selectedServiceId)}
        subtitle={drawerSubtitle}
        title={selectedService?.displayName || selectedServiceId || "Service"}
        width={820}
      >
        {selectedServiceQuery.isLoading && !selectedService ? (
          <AevatarInspectorEmpty description={t("pages.services.index.loading.service.details", "Loading service details")} title={t("pages.services.index.loading.service", "Loading service")} />
        ) : !selectedService ? (
          <AevatarInspectorEmpty description={t("pages.services.index.choose.service", "Choose a service")} />
        ) : (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel title={t("pages.services.index.object.summary", "object summary")}>
              <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                <div style={{ alignItems: "center", display: "flex", gap: 8, flexWrap: "wrap" }}>
                  <Tag color="blue" style={compactHintTagStyle} variant="filled">
                    {t("pages.services.index.authority.object", "authority object")}</Tag>
                  <AevatarStatusTag
                    domain="governance"
                    status={selectedService.deploymentStatus || "draft"}
                  />
                  <Tag
                    color={selectedService.primaryActorId ? "green" : "gold"}
                    style={compactHintTagStyle}
                    variant="filled"
                  >
                    {buildOwnerTagLabel(selectedService.primaryActorId)}
                  </Tag>
                  <Tag
                    color={selectedService.endpoints.length > 0 ? "cyan" : "orange"}
                    style={compactHintTagStyle}
                    variant="filled"
                  >
                    {buildEndpointTagLabel(selectedService.endpoints)}
                  </Tag>
                  <Tag
                    color={selectedService.deploymentId ? "geekblue" : "default"}
                    style={compactHintTagStyle}
                    variant="filled"
                  >
                    {buildServingTagLabel(selectedService.deploymentId)}
                  </Tag>
                </div>

                <div>
                  <Typography.Text style={summaryFieldLabelStyle}>{t("pages.services.index.service.id", "Service ID")}</Typography.Text>
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
                    label={t("pages.services.index.current.serving.version", "Current serving version")}
                    value={(() => {
                      const revisionId =
                        selectedService.activeServingRevisionId ||
                        selectedService.defaultServingRevisionId;

                      return revisionId ? (
                        <AevatarCompactText
                          head={4}
                          maxWidth="100%"
                          monospace
                          tail={4}
                          value={revisionId}
                        />
                      ) : (
                        t("pages.services.index.unpublished.3", "Unpublished")
                      );
                    })()}
                  />
                  <SummaryField
                    label={t("pages.services.index.current.deployment.2", "current deployment")}
                    value={
                      selectedService.deploymentId ? (
                        <AevatarCompactText
                          head={4}
                          maxWidth="100%"
                          monospace
                          tail={4}
                          value={selectedService.deploymentId}
                        />
                      ) : (
                        t("pages.services.index.not.hung.serving.2", "Not hung serving")
                      )
                    }
                  />
                  <SummaryField
                    label={t("pages.services.index.main.actor.3", "Main actor")}
                    value={
                      selectedService.primaryActorId ? (
                        <AevatarCompactText
                          head={4}
                          maxWidth="100%"
                          monospace
                          tail={4}
                          value={selectedService.primaryActorId}
                        />
                      ) : (
                        t("pages.services.index.not.declared.3", "Not declared")
                      )
                    }
                  />
                  <SummaryField
                    label={t("pages.services.index.latest.updates", "Latest updates")}
                    value={formatDateTime(selectedService.updatedAt)}
                  />
                </div>
              </div>
            </AevatarPanel>

            <AevatarPanel title={t("pages.services.index.service.workspace", "Service workspace")}>
              <Tabs
                activeKey={detailTabKey}
                items={[
                  {
                    key: "endpoints",
                    label: t("pages.services.index.entrance.3", "Entrance"),
                    children: (
                      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                        {selectedService.endpoints.length > 0 ? (
                          selectedService.endpoints.map((endpoint) => (
                            <EndpointRow endpoint={endpoint} key={endpoint.endpointId} />
                          ))
                        ) : (
                          <Empty
                            description={t("pages.services.index.there.is.no.public", "There is no public entrance to the current service")}
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                          />
                        )}
                      </div>
                    ),
                  },
                  {
                    key: "serving",
                    label: t("pages.services.index.versions.and.deployments", "Versions and Deployments"),
                    children: (
                      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                        <Space size={[8, 8]} wrap>
                          <Tag color="blue" style={compactHintTagStyle} variant="filled">
                            <DeploymentUnitOutlined /> {t("pages.services.index.current.serving", "Current serving")}</Tag>
                          <Tag color="cyan" style={compactHintTagStyle} variant="filled">
                            <ApiOutlined /> {t("pages.services.index.flow", "flow")}</Tag>
                          <Tag color="purple" style={compactHintTagStyle} variant="filled">
                            <BranchesOutlined /> {t("pages.services.index.version.3", "Version")}</Tag>
                          <Tag color="gold" style={compactHintTagStyle} variant="filled">
                            <SafetyCertificateOutlined /> {t("pages.services.index.deploy.2", "deploy")}</Tag>
                        </Space>
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
                    ),
                  },
                ]}
                onChange={setDetailTabKey}
                tabBarStyle={{ marginBottom: 16 }}
              />
            </AevatarPanel>
          </div>
        )}
      </AevatarContextDrawer>
    </ConsoleMenuPageShell>
  );
};

export default ServicesPage;
