import type { ServiceIdentityQuery } from "@/shared/models/services";

export type GovernanceWorkbenchView =
  | "audit"
  | "policies"
  | "bindings"
  | "endpoints"
  | "activation";

type QueryValue = string | number | undefined;

type PlatformIdentityOptions = ServiceIdentityQuery & {
  readonly serviceId?: string;
};

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

export function buildPlatformServicesHref(
  options?: PlatformIdentityOptions,
): string {
  return buildHref("/services", buildPlatformIdentityQuery(options));
}

export function buildPlatformGovernanceHref(options?: {
  readonly tenantId?: string;
  readonly appId?: string;
  readonly namespace?: string;
  readonly serviceId?: string;
  readonly revisionId?: string;
  readonly view?: GovernanceWorkbenchView;
}): string {
  return buildHref("/governance", {
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
  return buildHref("/deployments", {
    tenantId: options?.tenantId,
    appId: options?.appId,
    namespace: options?.namespace,
    serviceId: options?.serviceId,
    deploymentId: options?.deploymentId,
    take: options?.take,
  });
}
