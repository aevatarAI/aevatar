import type { QueryClient, QueryKey } from "@tanstack/react-query";
import type { ServiceIdentityQuery } from "@/shared/models/services";

const serviceResourceRoot = "service-resources";

const legacyDeploymentResourceKeys = new Set([
  "catalog",
  "revisions",
  "rollout",
  "service",
  "services",
  "serving",
  "traffic",
]);

// Refactor (iter90/cluster-090-console-service-cache-invalidation):
// Old: service catalog and deployment pages owned separate query-key roots, so
// mutation invalidation could refresh one page while leaving sibling resource
// views stale.
// New: shared service-resource query keys plus one alias matcher invalidate both
// the unified keys and legacy page-scoped aliases without touching auth-session
// cache entries.
export const serviceResourceQueryKeys = {
  deployments: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "deployments", query, serviceId] as const,
  detail: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "detail", query, serviceId] as const,
  list: (query: ServiceIdentityQuery) =>
    [serviceResourceRoot, "list", query] as const,
  revisions: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "revisions", query, serviceId] as const,
  rollout: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "rollout", query, serviceId] as const,
  serving: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "serving", query, serviceId] as const,
  traffic: (query: ServiceIdentityQuery, serviceId: string) =>
    [serviceResourceRoot, "traffic", query, serviceId] as const,
};

export function isServiceResourceQueryKeyAlias(queryKey: QueryKey): boolean {
  const [resource, alias] = queryKey;

  if (resource === serviceResourceRoot) {
    return true;
  }

  if (resource === "deployments" && typeof alias === "string") {
    return legacyDeploymentResourceKeys.has(alias);
  }

  if (resource !== "services") {
    return false;
  }

  return alias !== "auth-session";
}

export async function invalidateServiceResourceQueries(
  queryClient: Pick<QueryClient, "invalidateQueries">,
): Promise<void> {
  await queryClient.invalidateQueries({
    predicate: ({ queryKey }) => isServiceResourceQueryKeyAlias(queryKey),
  });
}
