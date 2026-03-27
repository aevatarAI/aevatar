import type { ServiceIdentityQuery } from '@/shared/models/services';

export type ServiceQueryDraft = {
  tenantId: string;
  namespace: string;
  take: number;
};

const defaultTake = 200;

function readString(value: string | null): string {
  return value?.trim() ?? '';
}

export function trimServiceQuery(
  draft: ServiceQueryDraft,
): ServiceIdentityQuery {
  return {
    tenantId: draft.tenantId.trim(),
    namespace: draft.namespace.trim(),
    take: draft.take,
  };
}

export function readServiceQueryDraft(
  search = typeof window === 'undefined' ? '' : window.location.search,
): ServiceQueryDraft {
  const params = new URLSearchParams(search);
  const parsedTake = Number(params.get('take'));

  return {
    tenantId: readString(params.get('tenantId')),
    namespace: readString(params.get('namespace')),
    take:
      Number.isFinite(parsedTake) && parsedTake > 0 ? parsedTake : defaultTake,
  };
}

function buildServiceSearchParams(
  query: ServiceIdentityQuery,
): URLSearchParams {
  const params = new URLSearchParams();

  if (query.tenantId?.trim()) {
    params.set('tenantId', query.tenantId.trim());
  }
  if (query.namespace?.trim()) {
    params.set('namespace', query.namespace.trim());
  }
  if (query.take && query.take > 0) {
    params.set('take', String(query.take));
  }

  return params;
}

export function buildServicesHref(query: ServiceIdentityQuery): string {
  const params = buildServiceSearchParams(query);
  const suffix = params.toString();
  return suffix ? `/services?${suffix}` : '/services';
}

export function buildServiceDetailHref(
  serviceId: string,
  query: ServiceIdentityQuery,
): string {
  const normalizedServiceId = serviceId.trim();
  const params = buildServiceSearchParams(query);
  const path = `/services/${encodeURIComponent(normalizedServiceId)}`;
  const suffix = params.toString();
  return suffix ? `${path}?${suffix}` : path;
}

export function readServiceIdFromPathname(
  pathname = typeof window === 'undefined' ? '' : window.location.pathname,
): string {
  const segments = pathname
    .split('/')
    .map((segment) => segment.trim())
    .filter(Boolean);
  const serviceId = segments.at(-1) ?? '';
  return serviceId ? decodeURIComponent(serviceId) : '';
}
