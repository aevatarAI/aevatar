import { authFetch } from '@/shared/auth/fetch';
import {
  expectArray,
  expectRecord,
  type JsonRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
} from './http/decoders';
import { readResponseErrorDetails } from './http/error';

export type AIModelSourceAvailability = 'available' | 'unavailable';

export type AIModelSourceError = {
  code: string;
  message: string;
};

export type AIModelSelection = {
  kind: 'unspecified' | 'provider_default' | 'explicit_model' | 'unsupported';
  modelId: string | null;
};

export type AIPersonalModelSelection = {
  routeKind: 'unspecified' | 'gateway' | 'nyx_id_user_service' | 'unsupported';
  routeValue: string;
  nyxIdUserServiceId: string;
  serviceSlugSnapshot: string;
  modelSelection: AIModelSelection | null;
};

export type AIPersonalModelCatalog = {
  certainty: 'enumerated' | 'not_verifiable' | 'unavailable';
  modelIds: string[];
  defaultModelId: string | null;
  diagnostic: string;
};

export type AIPersonalModelRoute = {
  routeValue: string;
  label: string;
  source: string;
  status: 'ready' | 'unavailable' | 'unknown';
  allowed: boolean;
  ready: boolean;
  userServiceId: string | null;
  serviceSlug: string | null;
  modelCatalog: AIPersonalModelCatalog;
  description: string | null;
};

export type AIPersonalModelSettings = {
  savedSelection: AIPersonalModelSelection | null;
  savedRouteLabel: string;
  selectionStatus:
    | 'unspecified'
    | 'system_default'
    | 'ready'
    | 'verification_unavailable'
    | 'needs_repair'
    | 'legacy_repair_required';
  catalogDiagnostic: string;
  remediation:
    | 'unspecified'
    | 'none'
    | 'retry_catalog'
    | 'connect_provider'
    | 'choose_replacement'
    | 'reselect';
  routeOptions: AIPersonalModelRoute[];
  catalogStatus: 'ready' | 'empty' | 'unavailable';
  capabilities: {
    canEditRoute: boolean;
    canEditModel: boolean;
    canSave: boolean;
    canRetryCatalog: boolean;
  };
};

export type AIPersonalModelsAuthority = {
  source: 'user_llm_preferences';
  authorityKind: 'authenticated_user';
  availability: AIModelSourceAvailability;
  authorityStateVersion: number | null;
  updatedAtUtc: string | null;
  settings: AIPersonalModelSettings | null;
  error: AIModelSourceError | null;
};

export type AIScopeModelSource = {
  sourceId: string;
  serviceSlugSnapshot: string | null;
  catalogServiceId: string | null;
  userServiceId: string | null;
  modelSelectionMode: 'explicit_models';
  modelIds: string[];
};

export type AIScopeModelPolicy = {
  mode: 'inherit_platform' | 'custom_replace';
  configured: boolean;
  sources: AIScopeModelSource[];
  effectiveSource: 'scope' | 'platform';
  effectiveSources: AIScopeModelSource[];
  lastMutationId: string | null;
};

export type AIScopeModelsAuthority = {
  source: 'llm_model_catalog_policy';
  authorityKind: 'scope';
  scopeId: string;
  availability: AIModelSourceAvailability;
  authorityStateVersion: number | null;
  updatedAtUtc: string | null;
  policy: AIScopeModelPolicy | null;
  error: AIModelSourceError | null;
};

export type AIModelsView = {
  consistency: 'independent_authorities';
  personalDefault: AIPersonalModelsAuthority;
  scopeCatalog: AIScopeModelsAuthority;
};

export class AIModelsApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'AIModelsApiError';
    this.status = status;
    this.code = code;
  }
}

function readNonEmptyString(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): string {
  const value = readString(record, keys, label).trim();
  if (!value) {
    throw new Error(`${label} must not be empty.`);
  }
  return value;
}

function readEnum<T extends string>(
  record: JsonRecord,
  keys: string | string[],
  label: string,
  values: readonly T[],
): T {
  const value = readNonEmptyString(record, keys, label);
  if (!values.includes(value as T)) {
    throw new Error(`${label} must be one of ${values.join(', ')}.`);
  }
  return value as T;
}

function readNullableSafeVersion(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): number | null {
  const keyList = Array.isArray(keys) ? keys : [keys];
  const key = keyList.find((candidate) => candidate in record);
  if (!key || record[key] === null) {
    return null;
  }
  const value = readNumber(record, keyList, label);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${label} must be a non-negative safe integer.`);
  }
  return value;
}

function decodeSourceError(
  value: unknown,
  label: string,
): AIModelSourceError | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = expectRecord(value, label);
  return {
    code: readNonEmptyString(record, ['code', 'Code'], `${label}.code`),
    message: readNonEmptyString(
      record,
      ['message', 'Message'],
      `${label}.message`,
    ),
  };
}

function decodeModelSelection(
  value: unknown,
  label: string,
): AIModelSelection | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = expectRecord(value, label);
  return {
    kind: readEnum(record, ['kind', 'Kind'], `${label}.kind`, [
      'unspecified',
      'provider_default',
      'explicit_model',
      'unsupported',
    ]),
    modelId: readNullableString(
      record,
      ['modelId', 'ModelId'],
      `${label}.modelId`,
    ),
  };
}

function decodeSavedSelection(
  value: unknown,
  label: string,
): AIPersonalModelSelection | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = expectRecord(value, label);
  return {
    routeKind: readEnum(
      record,
      ['routeKind', 'RouteKind'],
      `${label}.routeKind`,
      ['unspecified', 'gateway', 'nyx_id_user_service', 'unsupported'],
    ),
    routeValue: readString(
      record,
      ['routeValue', 'RouteValue'],
      `${label}.routeValue`,
    ),
    nyxIdUserServiceId: readString(
      record,
      ['nyxIdUserServiceId', 'NyxIdUserServiceId'],
      `${label}.nyxIdUserServiceId`,
    ),
    serviceSlugSnapshot: readString(
      record,
      ['serviceSlugSnapshot', 'ServiceSlugSnapshot'],
      `${label}.serviceSlugSnapshot`,
    ),
    modelSelection: decodeModelSelection(
      record.modelSelection ?? record.ModelSelection,
      `${label}.modelSelection`,
    ),
  };
}

function decodePersonalCatalog(
  value: unknown,
  label: string,
): AIPersonalModelCatalog {
  const record = expectRecord(value, label);
  return {
    certainty: readEnum(
      record,
      ['certainty', 'Certainty'],
      `${label}.certainty`,
      ['enumerated', 'not_verifiable', 'unavailable'],
    ),
    modelIds: expectArray(
      record.modelIds ?? record.ModelIds,
      `${label}.modelIds`,
      (entry, entryLabel) =>
        readNonEmptyString({ value: entry }, 'value', entryLabel ?? 'modelId'),
    ),
    defaultModelId: readNullableString(
      record,
      ['defaultModelId', 'DefaultModelId'],
      `${label}.defaultModelId`,
    ),
    diagnostic: readNonEmptyString(
      record,
      ['diagnostic', 'Diagnostic'],
      `${label}.diagnostic`,
    ),
  };
}

function decodePersonalRoute(
  value: unknown,
  label = 'AIPersonalModelRoute',
): AIPersonalModelRoute {
  const record = expectRecord(value, label);
  return {
    routeValue: readNonEmptyString(
      record,
      ['routeValue', 'RouteValue'],
      `${label}.routeValue`,
    ),
    label: readNonEmptyString(record, ['label', 'Label'], `${label}.label`),
    source: readNonEmptyString(record, ['source', 'Source'], `${label}.source`),
    status: readEnum(record, ['status', 'Status'], `${label}.status`, [
      'ready',
      'unavailable',
      'unknown',
    ]),
    allowed: readBoolean(record, ['allowed', 'Allowed'], `${label}.allowed`),
    ready: readBoolean(record, ['ready', 'Ready'], `${label}.ready`),
    userServiceId: readNullableString(
      record,
      ['userServiceId', 'UserServiceId'],
      `${label}.userServiceId`,
    ),
    serviceSlug: readNullableString(
      record,
      ['serviceSlug', 'ServiceSlug'],
      `${label}.serviceSlug`,
    ),
    modelCatalog: decodePersonalCatalog(
      record.modelCatalog ?? record.ModelCatalog,
      `${label}.modelCatalog`,
    ),
    description: readNullableString(
      record,
      ['description', 'Description'],
      `${label}.description`,
    ),
  };
}

function decodePersonalSettings(
  value: unknown,
  label: string,
): AIPersonalModelSettings | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = expectRecord(value, label);
  const capabilities = expectRecord(
    record.capabilities ?? record.Capabilities,
    `${label}.capabilities`,
  );
  return {
    savedSelection: decodeSavedSelection(
      record.savedSelection ?? record.SavedSelection,
      `${label}.savedSelection`,
    ),
    savedRouteLabel: readString(
      record,
      ['savedRouteLabel', 'SavedRouteLabel'],
      `${label}.savedRouteLabel`,
    ),
    selectionStatus: readEnum(
      record,
      ['selectionStatus', 'SelectionStatus'],
      `${label}.selectionStatus`,
      [
        'unspecified',
        'system_default',
        'ready',
        'verification_unavailable',
        'needs_repair',
        'legacy_repair_required',
      ],
    ),
    catalogDiagnostic: readNonEmptyString(
      record,
      ['catalogDiagnostic', 'CatalogDiagnostic'],
      `${label}.catalogDiagnostic`,
    ),
    remediation: readEnum(
      record,
      ['remediation', 'Remediation'],
      `${label}.remediation`,
      [
        'unspecified',
        'none',
        'retry_catalog',
        'connect_provider',
        'choose_replacement',
        'reselect',
      ],
    ),
    routeOptions: expectArray(
      record.routeOptions ?? record.RouteOptions,
      `${label}.routeOptions`,
      decodePersonalRoute,
    ),
    catalogStatus: readEnum(
      record,
      ['catalogStatus', 'CatalogStatus'],
      `${label}.catalogStatus`,
      ['ready', 'empty', 'unavailable'],
    ),
    capabilities: {
      canEditRoute: readBoolean(
        capabilities,
        ['canEditRoute', 'CanEditRoute'],
        `${label}.capabilities.canEditRoute`,
      ),
      canEditModel: readBoolean(
        capabilities,
        ['canEditModel', 'CanEditModel'],
        `${label}.capabilities.canEditModel`,
      ),
      canSave: readBoolean(
        capabilities,
        ['canSave', 'CanSave'],
        `${label}.capabilities.canSave`,
      ),
      canRetryCatalog: readBoolean(
        capabilities,
        ['canRetryCatalog', 'CanRetryCatalog'],
        `${label}.capabilities.canRetryCatalog`,
      ),
    },
  };
}

function decodeModelSource(
  value: unknown,
  label = 'AIScopeModelSource',
): AIScopeModelSource {
  const record = expectRecord(value, label);
  const result: AIScopeModelSource = {
    sourceId: readNonEmptyString(
      record,
      ['sourceId', 'SourceId'],
      `${label}.sourceId`,
    ),
    serviceSlugSnapshot: readNullableString(
      record,
      ['serviceSlugSnapshot', 'ServiceSlugSnapshot'],
      `${label}.serviceSlugSnapshot`,
    ),
    catalogServiceId: readNullableString(
      record,
      ['catalogServiceId', 'CatalogServiceId'],
      `${label}.catalogServiceId`,
    ),
    userServiceId: readNullableString(
      record,
      ['userServiceId', 'UserServiceId'],
      `${label}.userServiceId`,
    ),
    modelSelectionMode: readEnum(
      record,
      ['modelSelectionMode', 'ModelSelectionMode'],
      `${label}.modelSelectionMode`,
      ['explicit_models'],
    ),
    modelIds: expectArray(
      record.modelIds ?? record.ModelIds,
      `${label}.modelIds`,
      (entry, entryLabel) =>
        readNonEmptyString({ value: entry }, 'value', entryLabel ?? 'modelId'),
    ),
  };
  if (result.catalogServiceId !== null && result.userServiceId !== null) {
    throw new Error(
      `${label} must not identify both a catalog service and a user service.`,
    );
  }
  return result;
}

function decodeScopePolicy(
  value: unknown,
  label: string,
): AIScopeModelPolicy | null {
  if (value === null || value === undefined) {
    return null;
  }
  const record = expectRecord(value, label);
  return {
    mode: readEnum(record, ['mode', 'Mode'], `${label}.mode`, [
      'inherit_platform',
      'custom_replace',
    ]),
    configured: readBoolean(
      record,
      ['configured', 'Configured'],
      `${label}.configured`,
    ),
    sources: expectArray(
      record.sources ?? record.Sources,
      `${label}.sources`,
      decodeModelSource,
    ),
    effectiveSource: readEnum(
      record,
      ['effectiveSource', 'EffectiveSource'],
      `${label}.effectiveSource`,
      ['scope', 'platform'],
    ),
    effectiveSources: expectArray(
      record.effectiveSources ?? record.EffectiveSources,
      `${label}.effectiveSources`,
      decodeModelSource,
    ),
    lastMutationId: readNullableString(
      record,
      ['lastMutationId', 'LastMutationId'],
      `${label}.lastMutationId`,
    ),
  };
}

function decodePersonalAuthority(
  value: unknown,
  label: string,
): AIPersonalModelsAuthority {
  const record = expectRecord(value, label);
  const result: AIPersonalModelsAuthority = {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'user_llm_preferences',
    ]),
    authorityKind: readEnum(
      record,
      ['authorityKind', 'AuthorityKind'],
      `${label}.authorityKind`,
      ['authenticated_user'],
    ),
    availability: readEnum(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
      ['available', 'unavailable'],
    ),
    authorityStateVersion: readNullableSafeVersion(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
    updatedAtUtc: readNullableString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    settings: decodePersonalSettings(
      record.settings ?? record.Settings,
      `${label}.settings`,
    ),
    error: decodeSourceError(record.error ?? record.Error, `${label}.error`),
  };
  if (result.availability === 'available' && !result.settings) {
    throw new Error(`${label}.settings is required when available.`);
  }
  if (result.availability === 'unavailable' && !result.error) {
    throw new Error(`${label}.error is required when unavailable.`);
  }
  return result;
}

function decodeScopeAuthority(
  value: unknown,
  label: string,
): AIScopeModelsAuthority {
  const record = expectRecord(value, label);
  const result: AIScopeModelsAuthority = {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'llm_model_catalog_policy',
    ]),
    authorityKind: readEnum(
      record,
      ['authorityKind', 'AuthorityKind'],
      `${label}.authorityKind`,
      ['scope'],
    ),
    scopeId: readNonEmptyString(
      record,
      ['scopeId', 'ScopeId'],
      `${label}.scopeId`,
    ),
    availability: readEnum(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
      ['available', 'unavailable'],
    ),
    authorityStateVersion: readNullableSafeVersion(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
    updatedAtUtc: readNullableString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    policy: decodeScopePolicy(
      record.policy ?? record.Policy,
      `${label}.policy`,
    ),
    error: decodeSourceError(record.error ?? record.Error, `${label}.error`),
  };
  if (result.availability === 'available' && !result.policy) {
    throw new Error(`${label}.policy is required when available.`);
  }
  if (result.availability === 'unavailable' && !result.error) {
    throw new Error(`${label}.error is required when unavailable.`);
  }
  return result;
}

export function decodeAIModels(
  value: unknown,
  label = 'AIModelsView',
): AIModelsView {
  const record = expectRecord(value, label);
  return {
    consistency: readEnum(
      record,
      ['consistency', 'Consistency'],
      `${label}.consistency`,
      ['independent_authorities'],
    ),
    personalDefault: decodePersonalAuthority(
      record.personalDefault ?? record.PersonalDefault,
      `${label}.personalDefault`,
    ),
    scopeCatalog: decodeScopeAuthority(
      record.scopeCatalog ?? record.ScopeCatalog,
      `${label}.scopeCatalog`,
    ),
  };
}

export const aiModelsApi = {
  async getModels(
    endpoint: string,
    signal?: AbortSignal,
  ): Promise<AIModelsView> {
    const path = endpoint.trim();
    if (!path.startsWith('/api/ai/')) {
      throw new Error('AI models endpoint must be an /api/ai path.');
    }
    const response = await authFetch(path, {
      headers: { Accept: 'application/json' },
      ...(signal ? { signal } : {}),
    });
    if (!response.ok) {
      const error = await readResponseErrorDetails(response);
      throw new AIModelsApiError(error.message, error.status, error.code);
    }
    return decodeAIModels(await response.json());
  },
};
