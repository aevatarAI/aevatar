import type { ServiceIdentityQuery } from "@/shared/models/services";

export type GovernanceWorkbenchView =
  | "audit"
  | "policies"
  | "bindings"
  | "endpoints"
  | "activation";

export type PlatformModuleKey =
  | "capabilities"
  | "accessRules"
  | "releases"
  | "runs"
  | "runtimeMap";

export type PlatformModuleDescriptor = {
  readonly ctaMessageId: string;
  readonly descriptionMessageId: string;
  readonly key: PlatformModuleKey;
  readonly labelMessageId: string;
  readonly routePath: string;
  readonly summaryFallbackMessageId: string;
};

type QueryValue = string | number | undefined;

type PlatformIdentityOptions = ServiceIdentityQuery & {
  readonly serviceId?: string;
};

const platformPaths = {
  overview: "/platform",
  services: "/services",
  governance: "/governance",
  deployments: "/deployments",
  runs: "/runtime/runs",
  runtimeMap: "/runtime/explorer",
} as const;

export const platformNavigation = platformPaths;

export const PLATFORM_MODULE_DESCRIPTORS: readonly PlatformModuleDescriptor[] = [
  {
    ctaMessageId: "platform.overview.modules.capabilities.cta",
    descriptionMessageId: "platform.overview.modules.capabilities.description",
    key: "capabilities",
    labelMessageId: "platform.overview.modules.capabilities.title",
    routePath: platformPaths.services,
    summaryFallbackMessageId: "platform.overview.modules.capabilities.summaryFallback",
  },
  {
    ctaMessageId: "platform.overview.modules.accessRules.cta",
    descriptionMessageId: "platform.overview.modules.accessRules.description",
    key: "accessRules",
    labelMessageId: "platform.overview.modules.accessRules.title",
    routePath: platformPaths.governance,
    summaryFallbackMessageId: "platform.overview.modules.accessRules.summaryFallback",
  },
  {
    ctaMessageId: "platform.overview.modules.releases.cta",
    descriptionMessageId: "platform.overview.modules.releases.description",
    key: "releases",
    labelMessageId: "platform.overview.modules.releases.title",
    routePath: platformPaths.deployments,
    summaryFallbackMessageId: "platform.overview.modules.releases.summaryFallback",
  },
  {
    ctaMessageId: "platform.overview.modules.runs.cta",
    descriptionMessageId: "platform.overview.modules.runs.description",
    key: "runs",
    labelMessageId: "platform.overview.modules.runs.title",
    routePath: platformPaths.runs,
    summaryFallbackMessageId: "platform.overview.modules.runs.summaryFallback",
  },
  {
    ctaMessageId: "platform.overview.modules.runtimeMap.cta",
    descriptionMessageId: "platform.overview.modules.runtimeMap.description",
    key: "runtimeMap",
    labelMessageId: "platform.overview.modules.runtimeMap.title",
    routePath: platformPaths.runtimeMap,
    summaryFallbackMessageId: "platform.overview.modules.runtimeMap.summaryFallback",
  },
] as const;

function buildHref(
  pathname: string,
  query?: Record<string, QueryValue>,
): string {
  if (!query) {
    return pathname;
  }

  const params = new URLSearchParams();
  Object.entries(query).forEach(([key, value]) => {
    const normalized =
      typeof value === "number" ? String(value) : value?.trim();
    if (normalized) {
      params.set(key, normalized);
    }
  });

  const suffix = params.toString();
  return suffix ? `${pathname}?${suffix}` : pathname;
}

function buildPlatformIdentityQuery(
  options?: PlatformIdentityOptions,
): Record<string, QueryValue> {
  return {
    tenantId: options?.tenantId,
    appId: options?.appId,
    namespace: options?.namespace,
    take: options?.take,
    serviceId: options?.serviceId,
  };
}

export function buildPlatformOverviewHref(): string {
  return platformPaths.overview;
}

export function buildPlatformServicesHref(
  options?: PlatformIdentityOptions,
): string {
  return buildHref(platformPaths.services, buildPlatformIdentityQuery(options));
}

export function buildPlatformGovernanceHref(options?: {
  readonly tenantId?: string;
  readonly appId?: string;
  readonly namespace?: string;
  readonly serviceId?: string;
  readonly revisionId?: string;
  readonly view?: GovernanceWorkbenchView;
}): string {
  return buildHref(platformPaths.governance, {
    tenantId: options?.tenantId,
    appId: options?.appId,
    namespace: options?.namespace,
    serviceId: options?.serviceId,
    revisionId: options?.revisionId,
    view: options?.view && options.view !== "audit" ? options.view : undefined,
  });
}

export function buildPlatformDeploymentsHref(options?: {
  readonly tenantId?: string;
  readonly appId?: string;
  readonly namespace?: string;
  readonly serviceId?: string;
  readonly deploymentId?: string;
  readonly take?: number;
}): string {
  return buildHref(platformPaths.deployments, {
    tenantId: options?.tenantId,
    appId: options?.appId,
    namespace: options?.namespace,
    serviceId: options?.serviceId,
    deploymentId: options?.deploymentId,
    take: options?.take,
  });
}
