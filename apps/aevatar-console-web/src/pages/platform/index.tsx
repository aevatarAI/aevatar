import {
  ApiOutlined,
  BranchesOutlined,
  DeploymentUnitOutlined,
  PlayCircleOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Button, Grid, Space, Tag, Typography, theme } from "antd";
import React, { useMemo } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type { ServiceBindingCatalogSnapshot } from "@/shared/models/governance";
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentCatalogSnapshot,
  ServiceTrafficViewSnapshot,
} from "@/shared/models/services";
import { history } from "@/shared/navigation/history";
import {
  buildPlatformDeploymentsHref,
  buildPlatformGovernanceHref,
  buildPlatformServicesHref,
  PLATFORM_MODULE_DESCRIPTORS,
  type PlatformModuleDescriptor,
  type PlatformModuleKey,
} from "@/shared/navigation/platformRoutes";
import { buildRuntimeExplorerHref, buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import { loadRecentRuns } from "@/shared/runs/recentRuns";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { t } from "@/shared/i18n/messages";

type PlatformModuleSummary = {
  readonly detail?: string;
  readonly status: "loading" | "ready" | "fallback" | "unavailable";
  readonly text: string;
  readonly tone: "default" | "info" | "success" | "warning";
};

type PlatformModuleCardProps = {
  readonly descriptor: PlatformModuleDescriptor;
  readonly icon: React.ReactNode;
  readonly summary: PlatformModuleSummary;
};

const serviceQuery = {
  take: 100,
} as const;

const pageSurfaceStyle: React.CSSProperties = {
  background: "transparent",
  borderRadius: 0,
  boxShadow: "none",
  gap: 20,
  padding: 0,
};

const overviewGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(min(100%, 260px), 1fr))",
  minWidth: 0,
  width: "100%",
};

const moduleCardStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #e5e7eb",
  borderRadius: 8,
  boxShadow: "0 14px 32px rgba(15, 23, 42, 0.05)",
  boxSizing: "border-box",
  display: "flex",
  flexDirection: "column",
  gap: 16,
  minHeight: 300,
  minWidth: 0,
  padding: 20,
};

const moduleHeaderStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  minWidth: 0,
};

const moduleIconStyle: React.CSSProperties = {
  alignItems: "center",
  background: "#eef6ff",
  border: "1px solid #d6e8ff",
  borderRadius: 8,
  color: "#135fb8",
  display: "inline-flex",
  flex: "0 0 auto",
  fontSize: 20,
  height: 40,
  justifyContent: "center",
  width: 40,
};

const moduleTitleBlockStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 6,
  minWidth: 0,
};

const moduleDescriptionStyle: React.CSSProperties = {
  color: "#4b5563",
  fontSize: 14,
  lineHeight: 1.55,
  margin: 0,
};

const moduleSummaryStyle: React.CSSProperties = {
  background: "#f8fafc",
  border: "1px solid #edf2f7",
  borderRadius: 8,
  display: "flex",
  flexDirection: "column",
  gap: 6,
  marginTop: "auto",
  minWidth: 0,
  padding: 12,
};

const cardButtonStyle: React.CSSProperties = {
  justifyContent: "center",
  minHeight: 40,
  whiteSpace: "normal",
  width: "100%",
};

const summaryBandStyle: React.CSSProperties = {
  background: "#0f172a",
  border: "1px solid rgba(148, 163, 184, 0.22)",
  borderRadius: 8,
  boxShadow: "0 18px 36px rgba(15, 23, 42, 0.12)",
  boxSizing: "border-box",
  color: "#f8fafc",
  display: "grid",
  gap: 16,
  gridTemplateColumns: "minmax(0, 1.2fr) repeat(3, minmax(0, 160px))",
  minWidth: 0,
  padding: 20,
  width: "100%",
};

const compactSummaryBandStyle: React.CSSProperties = {
  ...summaryBandStyle,
  gridTemplateColumns: "1fr",
};

const summaryMetricStyle: React.CSSProperties = {
  background: "rgba(255, 255, 255, 0.08)",
  border: "1px solid rgba(255, 255, 255, 0.12)",
  borderRadius: 8,
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
  padding: 12,
};

function getFirstService(services: readonly ServiceCatalogSnapshot[]): ServiceCatalogSnapshot | null {
  return services[0] ?? null;
}

function hasServiceQueryError(...queries: readonly { readonly isError: boolean }[]): boolean {
  return queries.some((query) => query.isError);
}

function buildServiceIdentityQuery(service: ServiceCatalogSnapshot | null) {
  return service
    ? {
        appId: service.appId,
        namespace: service.namespace,
        serviceId: service.serviceId,
        tenantId: service.tenantId,
      }
    : undefined;
}

function buildCapabilitiesSummary(
  services: readonly ServiceCatalogSnapshot[] | undefined,
  isLoading: boolean,
  isError: boolean,
): PlatformModuleSummary {
  if (isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (isError) {
    return {
      status: "unavailable",
      text: t("platform.overview.modules.capabilities.summaryUnavailable", "Capability catalog is temporarily unavailable."),
      tone: "warning",
    };
  }

  const catalog = services ?? [];
  if (!catalog.length) {
    return {
      status: "fallback",
      text: t("platform.overview.modules.capabilities.summaryEmpty", "No capabilities are visible in the current workspace yet."),
      tone: "default",
    };
  }

  const servingCount = catalog.filter((service) => service.deploymentId.trim()).length;
  const endpointCount = catalog.reduce(
    (total, service) => total + service.endpoints.length,
    0,
  );

  return {
    detail: t(
      "platform.overview.modules.capabilities.summaryDetail",
      "{endpointCount} callable endpoints are listed across the catalog.",
      { endpointCount },
    ),
    status: "ready",
    text: t(
      "platform.overview.modules.capabilities.summary",
      "{serviceCount} capabilities, {servingCount} currently attached to serving.",
      { serviceCount: catalog.length, servingCount },
    ),
    tone: servingCount > 0 ? "success" : "info",
  };
}

function buildAccessRulesSummary(
  bindings: ServiceBindingCatalogSnapshot | null | undefined,
  service: ServiceCatalogSnapshot | null,
  isLoading: boolean,
  isError: boolean,
): PlatformModuleSummary {
  if (!service && isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (!service) {
    return {
      status: "fallback",
      text: t("platform.overview.modules.accessRules.summaryFallback", "Choose a capability to inspect who can call it and which rules apply."),
      tone: "default",
    };
  }

  if (isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (isError) {
    return {
      status: "unavailable",
      text: t("platform.overview.modules.accessRules.summaryUnavailable", "Access and rule catalogs are temporarily unavailable."),
      tone: "warning",
    };
  }

  const activeBindings = (bindings?.bindings ?? []).filter((binding) => !binding.retired);
  const policyCount = service.policyIds.length;

  return {
    detail: t(
      "platform.overview.modules.accessRules.summaryDetail",
      "Sampled from {serviceName}.",
      { serviceName: service.displayName || service.serviceId },
    ),
    status: "ready",
    text: t(
      "platform.overview.modules.accessRules.summary",
      "{policyCount} policies and {bindingCount} active bindings on the first visible capability.",
      { bindingCount: activeBindings.length, policyCount },
    ),
    tone: policyCount || activeBindings.length ? "success" : "info",
  };
}

function buildReleasesSummary(
  deployments: ServiceDeploymentCatalogSnapshot | null | undefined,
  traffic: ServiceTrafficViewSnapshot | null | undefined,
  service: ServiceCatalogSnapshot | null,
  isLoading: boolean,
  isError: boolean,
): PlatformModuleSummary {
  if (!service && isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (!service) {
    return {
      status: "fallback",
      text: t("platform.overview.modules.releases.summaryFallback", "Release controls appear after a capability has a serving target."),
      tone: "default",
    };
  }

  if (isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (isError) {
    return {
      status: "unavailable",
      text: t("platform.overview.modules.releases.summaryUnavailable", "Release and traffic evidence are temporarily unavailable."),
      tone: "warning",
    };
  }

  const deploymentCount = deployments?.deployments.length ?? 0;
  const trafficTargetCount =
    traffic?.endpoints.reduce((total, endpoint) => total + endpoint.targets.length, 0) ?? 0;

  return {
    detail: t(
      "platform.overview.modules.releases.summaryDetail",
      "Traffic evidence covers {trafficTargetCount} active target links.",
      { trafficTargetCount },
    ),
    status: "ready",
    text: t(
      "platform.overview.modules.releases.summary",
      "{deploymentCount} deployments are visible for the first capability.",
      { deploymentCount },
    ),
    tone: deploymentCount > 0 ? "success" : "info",
  };
}

function buildRunsSummary(): PlatformModuleSummary {
  const recentRuns = loadRecentRuns();

  if (!recentRuns.length) {
    return {
      status: "fallback",
      text: t("platform.overview.modules.runs.summaryFallback", "No recent local run handoff has been recorded in this browser."),
      tone: "default",
    };
  }

  const latestRun = recentRuns[0];
  return {
    detail: latestRun.recordedAt
      ? t("platform.overview.modules.runs.summaryDetail", "Latest local record: {time}.", {
          time: formatDateTime(latestRun.recordedAt),
        })
      : undefined,
    status: "ready",
    text: t(
      "platform.overview.modules.runs.summary",
      "{runCount} recent local runs, latest status {status}.",
      { runCount: recentRuns.length, status: latestRun.status || "unknown" },
    ),
    tone: "success",
  };
}

function buildRuntimeMapSummary(
  service: ServiceCatalogSnapshot | null,
  isLoading: boolean,
  isError: boolean,
): PlatformModuleSummary {
  if (isLoading) {
    return {
      status: "loading",
      text: t("platform.overview.summary.loading", "Reading current workspace signals."),
      tone: "info",
    };
  }

  if (isError) {
    return {
      status: "unavailable",
      text: t("platform.overview.modules.runtimeMap.summaryUnavailable", "Runtime map seed signals are temporarily unavailable."),
      tone: "warning",
    };
  }

  if (!service?.primaryActorId.trim()) {
    return {
      status: "fallback",
      text: t("platform.overview.modules.runtimeMap.summaryFallback", "Open the runtime map to inspect actors and relationships after a run exists."),
      tone: "default",
    };
  }

  return {
    detail: t(
      "platform.overview.modules.runtimeMap.summaryDetail",
      "First visible capability has an actor seed ready for map inspection.",
    ),
    status: "ready",
    text: t("platform.overview.modules.runtimeMap.summary", "Runtime map can start from the current capability owner."),
    tone: "success",
  };
}

function getModuleIcon(key: PlatformModuleKey): React.ReactNode {
  switch (key) {
    case "capabilities":
      return <ApiOutlined />;
    case "accessRules":
      return <SafetyCertificateOutlined />;
    case "releases":
      return <DeploymentUnitOutlined />;
    case "runs":
      return <PlayCircleOutlined />;
    case "runtimeMap":
      return <BranchesOutlined />;
    default:
      return <ApiOutlined />;
  }
}

function getSummaryToneColor(tone: PlatformModuleSummary["tone"]): string {
  switch (tone) {
    case "success":
      return "success";
    case "warning":
      return "warning";
    case "info":
      return "processing";
    default:
      return "default";
  }
}

function getSummaryStatusLabel(status: PlatformModuleSummary["status"]): string {
  switch (status) {
    case "loading":
      return t("platform.overview.summary.status.loading", "Loading");
    case "ready":
      return t("platform.overview.summary.status.ready", "Live signal");
    case "unavailable":
      return t("platform.overview.summary.status.unavailable", "Unavailable");
    case "fallback":
    default:
      return t("platform.overview.summary.status.fallback", "Guidance");
  }
}

const PlatformModuleCard: React.FC<PlatformModuleCardProps> = ({
  descriptor,
  icon,
  summary,
}) => {
  const title = t(descriptor.labelMessageId, descriptor.labelMessageId);
  return (
    <article aria-labelledby={`platform-module-${descriptor.key}`} style={moduleCardStyle}>
      <div style={moduleHeaderStyle}>
        <span aria-hidden style={moduleIconStyle}>
          {icon}
        </span>
        <div style={moduleTitleBlockStyle}>
          <Typography.Title
            id={`platform-module-${descriptor.key}`}
            level={3}
            style={{ fontSize: 18, lineHeight: 1.3, margin: 0, overflowWrap: "anywhere" }}
          >
            {title}
          </Typography.Title>
          <Typography.Paragraph style={moduleDescriptionStyle}>
            {t(descriptor.descriptionMessageId, descriptor.descriptionMessageId)}
          </Typography.Paragraph>
        </div>
      </div>
      <div style={moduleSummaryStyle}>
        <Tag color={getSummaryToneColor(summary.tone)} style={{ alignSelf: "flex-start", marginInlineEnd: 0 }}>
          {getSummaryStatusLabel(summary.status)}
        </Tag>
        <Typography.Text style={{ color: "#1f2937", fontWeight: 600, overflowWrap: "anywhere" }}>
          {summary.text || t(descriptor.summaryFallbackMessageId, descriptor.summaryFallbackMessageId)}
        </Typography.Text>
        {summary.detail ? (
          <Typography.Text style={{ color: "#667085", fontSize: 12, lineHeight: 1.45, overflowWrap: "anywhere" }}>
            {summary.detail}
          </Typography.Text>
        ) : null}
      </div>
      <Button
        block
        onClick={() => history.push(descriptor.routePath)}
        style={cardButtonStyle}
        type="primary"
      >
        {t(descriptor.ctaMessageId, descriptor.ctaMessageId)}
      </Button>
    </article>
  );
};

const PlatformOverviewPage: React.FC = () => {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const servicesQuery = useQuery({
    queryFn: () => servicesApi.listServices(serviceQuery),
    queryKey: ["platform-overview", "services", serviceQuery.take],
  });

  const services = servicesQuery.data ?? [];
  const firstService = getFirstService(services);
  const firstServiceIdentity = buildServiceIdentityQuery(firstService);

  const bindingsQuery = useQuery({
    enabled: Boolean(firstServiceIdentity?.serviceId),
    queryFn: () =>
      governanceApi.getBindings(
        firstServiceIdentity?.serviceId ?? "",
        firstServiceIdentity ?? serviceQuery,
      ),
    queryKey: ["platform-overview", "bindings", firstServiceIdentity],
  });

  const deploymentsQuery = useQuery({
    enabled: Boolean(firstServiceIdentity?.serviceId),
    queryFn: () =>
      servicesApi.getDeployments(
        firstServiceIdentity?.serviceId ?? "",
        firstServiceIdentity ?? serviceQuery,
      ),
    queryKey: ["platform-overview", "deployments", firstServiceIdentity],
  });

  const trafficQuery = useQuery({
    enabled: Boolean(firstServiceIdentity?.serviceId),
    queryFn: () =>
      servicesApi.getTraffic(
        firstServiceIdentity?.serviceId ?? "",
        firstServiceIdentity ?? serviceQuery,
      ),
    queryKey: ["platform-overview", "traffic", firstServiceIdentity],
  });

  const moduleSummaries = useMemo<Record<PlatformModuleKey, PlatformModuleSummary>>(
    () => ({
      accessRules: buildAccessRulesSummary(
        bindingsQuery.data,
        firstService,
        servicesQuery.isLoading || bindingsQuery.isLoading,
        hasServiceQueryError(servicesQuery, bindingsQuery),
      ),
      capabilities: buildCapabilitiesSummary(
        servicesQuery.data,
        servicesQuery.isLoading,
        servicesQuery.isError,
      ),
      releases: buildReleasesSummary(
        deploymentsQuery.data,
        trafficQuery.data,
        firstService,
        servicesQuery.isLoading || deploymentsQuery.isLoading || trafficQuery.isLoading,
        hasServiceQueryError(servicesQuery, deploymentsQuery, trafficQuery),
      ),
      runs: buildRunsSummary(),
      runtimeMap: buildRuntimeMapSummary(
        firstService,
        servicesQuery.isLoading,
        servicesQuery.isError,
      ),
    }),
    [
      bindingsQuery.data,
      bindingsQuery.isError,
      bindingsQuery.isLoading,
      deploymentsQuery.data,
      deploymentsQuery.isError,
      deploymentsQuery.isLoading,
      firstService,
      servicesQuery.data,
      servicesQuery.isError,
      servicesQuery.isLoading,
      trafficQuery.data,
      trafficQuery.isError,
      trafficQuery.isLoading,
    ],
  );

  const activeServiceCount = services.filter((service) => service.deploymentId.trim()).length;
  const governedServiceCount = services.filter((service) => service.policyIds.length > 0).length;
  const latestServiceUpdate = services
    .map((service) => service.updatedAt)
    .filter(Boolean)
    .sort()
    .at(-1);
  const capabilitiesHref = buildPlatformServicesHref(firstServiceIdentity);
  const governanceHref = buildPlatformGovernanceHref(firstServiceIdentity);
  const deploymentsHref = buildPlatformDeploymentsHref(firstServiceIdentity);
  const runtimeRunsHref = buildRuntimeRunsHref(
    firstService
      ? {
          actorId: firstService.primaryActorId || undefined,
          serviceId: firstService.serviceId,
        }
      : undefined,
  );
  const runtimeMapHref = buildRuntimeExplorerHref(
    firstService?.primaryActorId
      ? {
          actorId: firstService.primaryActorId,
          serviceId: firstService.serviceId,
        }
      : undefined,
  );
  const hrefByModule: Record<PlatformModuleKey, string> = {
    accessRules: governanceHref,
    capabilities: capabilitiesHref,
    releases: deploymentsHref,
    runs: runtimeRunsHref,
    runtimeMap: runtimeMapHref,
  };

  return (
    <ConsoleMenuPageShell
      breadcrumb={t("platform.overview.breadcrumb", "Aevatar / Platform")}
      description={t(
        "platform.overview.description",
        "Publish capabilities, govern access, release changes, inspect runs, and understand runtime relationships from one task-oriented entry point.",
      )}
      surfaceStyle={pageSurfaceStyle}
      title={t("platform.overview.title", "Platform overview")}
    >
      <section
        aria-label={t("platform.overview.summary.title", "Platform summary")}
        style={screens.md ? summaryBandStyle : compactSummaryBandStyle}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 8, minWidth: 0 }}>
          <Typography.Text style={{ color: "#cbd5e1", fontSize: 12, fontWeight: 700, letterSpacing: 0 }}>
            {t("platform.overview.summary.eyebrow", "Publish and run workflow")}
          </Typography.Text>
          <Typography.Title
            level={2}
            style={{ color: "#ffffff", fontSize: 22, lineHeight: 1.25, margin: 0, overflowWrap: "anywhere" }}
          >
            {t("platform.overview.summary.heading", "Move from callable capability to observable runtime without switching mental models.")}
          </Typography.Title>
          <Typography.Paragraph style={{ color: "#d1d5db", lineHeight: 1.55, margin: 0 }}>
            {servicesQuery.isError
              ? t("platform.overview.summary.catalogUnavailable", "Current workspace summary is using guidance because the capability catalog could not be read.")
              : t("platform.overview.summary.catalogReady", "Summaries use existing frontend reads and local run handoffs, so weak signals stay labeled as guidance.")}
          </Typography.Paragraph>
        </div>
        <div style={summaryMetricStyle}>
          <Typography.Text style={{ color: "#cbd5e1", fontSize: 12 }}>
            {t("platform.overview.metrics.capabilities", "Capabilities")}
          </Typography.Text>
          <Typography.Text style={{ color: "#ffffff", fontSize: 28, fontWeight: 700, lineHeight: 1 }}>
            {servicesQuery.isLoading ? "..." : services.length}
          </Typography.Text>
        </div>
        <div style={summaryMetricStyle}>
          <Typography.Text style={{ color: "#cbd5e1", fontSize: 12 }}>
            {t("platform.overview.metrics.releases", "Serving")}
          </Typography.Text>
          <Typography.Text style={{ color: "#ffffff", fontSize: 28, fontWeight: 700, lineHeight: 1 }}>
            {servicesQuery.isLoading ? "..." : activeServiceCount}
          </Typography.Text>
        </div>
        <div style={summaryMetricStyle}>
          <Typography.Text style={{ color: "#cbd5e1", fontSize: 12 }}>
            {t("platform.overview.metrics.rules", "Governed")}
          </Typography.Text>
          <Typography.Text style={{ color: "#ffffff", fontSize: 28, fontWeight: 700, lineHeight: 1 }}>
            {servicesQuery.isLoading ? "..." : governedServiceCount}
          </Typography.Text>
          {latestServiceUpdate ? (
            <Typography.Text style={{ color: "#cbd5e1", fontSize: 11, overflowWrap: "anywhere" }}>
              {t("platform.overview.metrics.updated", "Updated {time}", {
                time: formatDateTime(latestServiceUpdate),
              })}
            </Typography.Text>
          ) : null}
        </div>
      </section>

      <section
        aria-label={t("platform.overview.modules.title", "Platform modules")}
        style={overviewGridStyle}
      >
        {PLATFORM_MODULE_DESCRIPTORS.map((descriptor) => (
          <PlatformModuleCard
            descriptor={{
              ...descriptor,
              routePath: hrefByModule[descriptor.key],
            }}
            icon={getModuleIcon(descriptor.key)}
            key={descriptor.key}
            summary={moduleSummaries[descriptor.key]}
          />
        ))}
      </section>

      <Space
        align="start"
        direction="vertical"
        size={8}
        style={{
          background: token.colorBgContainer,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: 8,
          boxSizing: "border-box",
          padding: 16,
          width: "100%",
        }}
      >
        <Typography.Text strong>
          {t("platform.overview.footer.title", "Deep links stay unchanged")}
        </Typography.Text>
        <Typography.Text style={{ color: token.colorTextSecondary, lineHeight: 1.55 }}>
          {t(
            "platform.overview.footer.description",
            "Existing links for capabilities, access rules, releases, runs, and the runtime map remain available; this page only adds a task-first starting point.",
          )}
        </Typography.Text>
      </Space>
    </ConsoleMenuPageShell>
  );
};

export default PlatformOverviewPage;
