import type {
  ServiceCatalogSnapshot,
  ServiceIdentityQuery,
} from '@/shared/models/services';

export type GovernanceDraft = {
  tenantId: string;
  appId: string;
  namespace: string;
  serviceId: string;
  revisionId: string;
};

export type GovernanceServiceOption = {
  label: string;
  value: string;
  tenantId: string;
  appId: string;
  namespace: string;
  serviceId: string;
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

export function hasGovernanceScope(draft: GovernanceDraft): boolean {
  return (
    draft.tenantId.trim().length > 0 &&
    draft.appId.trim().length > 0 &&
    draft.namespace.trim().length > 0
  );
}

export function buildGovernanceServiceOptions(
  services: readonly ServiceCatalogSnapshot[],
): GovernanceServiceOption[] {
  return services.map((service) => {
    const identityLabel = [
      service.tenantId,
      service.appId,
      service.namespace,
      service.serviceId,
    ].join('/');

    return {
      label: service.displayName
        ? `${service.displayName} (${identityLabel})`
        : identityLabel,
      value: service.serviceKey,
      tenantId: service.tenantId,
      appId: service.appId,
      namespace: service.namespace,
      serviceId: service.serviceId,
    };
  });
}

export function findGovernanceServiceOption(
  serviceOptions: readonly GovernanceServiceOption[],
  draft: GovernanceDraft,
): GovernanceServiceOption | null {
  const normalizedServiceId = draft.serviceId.trim();
  if (!normalizedServiceId) {
    return null;
  }

  const tenantId = draft.tenantId.trim();
  const appId = draft.appId.trim();
  const namespace = draft.namespace.trim();

  if (tenantId && appId && namespace) {
    const exactMatch = serviceOptions.find(
      (option) =>
        option.serviceId === normalizedServiceId &&
        option.tenantId === tenantId &&
        option.appId === appId &&
        option.namespace === namespace,
    );
    if (exactMatch) {
      return exactMatch;
    }
  }

  const serviceIdMatches = serviceOptions.filter(
    (option) => option.serviceId === normalizedServiceId,
  );
  return serviceIdMatches.length === 1 ? serviceIdMatches[0] : null;
}

export function applyGovernanceServiceSelection(
  draft: GovernanceDraft,
  serviceOption: GovernanceServiceOption,
): GovernanceDraft {
  return {
    ...draft,
    tenantId: serviceOption.tenantId,
    appId: serviceOption.appId,
    namespace: serviceOption.namespace,
    serviceId: serviceOption.serviceId,
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
