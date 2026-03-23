import type { ServiceIdentityQuery } from '@/shared/models/services';

export type GovernanceDraft = {
  tenantId: string;
  appId: string;
  namespace: string;
  serviceId: string;
  revisionId: string;
};

function readString(value: string | null): string {
  return value?.trim() ?? '';
}

export function normalizeGovernanceQuery(
  draft: GovernanceDraft,
): ServiceIdentityQuery {
  return {
    tenantId: draft.tenantId.trim(),
    appId: draft.appId.trim(),
    namespace: draft.namespace.trim(),
  };
}

export function normalizeGovernanceDraft(
  draft: GovernanceDraft,
): GovernanceDraft {
  return {
    tenantId: draft.tenantId.trim(),
    appId: draft.appId.trim(),
    namespace: draft.namespace.trim(),
    serviceId: draft.serviceId.trim(),
    revisionId: draft.revisionId.trim(),
  };
}

export function readGovernanceDraft(
  search = typeof window === 'undefined' ? '' : window.location.search,
): GovernanceDraft {
  const params = new URLSearchParams(search);
  return {
    tenantId: readString(params.get('tenantId')),
    appId: readString(params.get('appId')),
    namespace: readString(params.get('namespace')),
    serviceId: readString(params.get('serviceId')),
    revisionId: readString(params.get('revisionId')),
  };
}

export function buildGovernanceHref(
  path: string,
  draft: GovernanceDraft,
): string {
  const params = new URLSearchParams();

  if (draft.tenantId.trim()) {
    params.set('tenantId', draft.tenantId.trim());
  }
  if (draft.appId.trim()) {
    params.set('appId', draft.appId.trim());
  }
  if (draft.namespace.trim()) {
    params.set('namespace', draft.namespace.trim());
  }
  if (draft.serviceId.trim()) {
    params.set('serviceId', draft.serviceId.trim());
  }
  if (draft.revisionId.trim()) {
    params.set('revisionId', draft.revisionId.trim());
  }

  const suffix = params.toString();
  return suffix ? `${path}?${suffix}` : path;
}
