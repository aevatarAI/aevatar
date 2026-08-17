import { authFetch } from '@/shared/auth/fetch';
import { withQuery } from './http/client';
import {
  expectArray,
  expectRecord,
  type JsonRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalString,
  readString,
} from './http/decoders';
import { readResponseErrorDetails } from './http/error';

export type AIWorkspaceConsistency = 'independent_read_models';
export type AIWorkspaceAgentOwnerKind = 'scope' | 'system';
export type AIWorkspaceAgentCollectionAvailability =
  | 'available'
  | 'not_materialized'
  | 'unavailable';
export type AIWorkspaceActivityCollectionAvailability =
  | 'available'
  | 'unavailable';
export type AIWorkspaceAgentStatus =
  | 'active'
  | 'failed'
  | 'provisioning'
  | 'unspecified';

export type AIWorkspacePageLinks = {
  overview?: string;
  chat?: string;
  agents?: string;
  models?: string;
  channels?: string;
  capabilities?: string;
  activity?: string;
};

export type AIWorkspaceApiLinks = {
  overview?: string;
  chat?: string;
  agents?: string;
  ownedAgentProfiles?: string;
  systemAgentProfiles?: string;
  models?: string;
  personalModelSettings?: string;
  scopeModelCatalog?: string;
  channels?: string;
  channelRegistrations?: string;
  capabilities?: string;
  activity?: string;
  conversations?: string;
  runs?: string;
  auditedActions?: string;
};

export type AIWorkspaceFeature = {
  availability: 'available';
  page: string;
  api: string | null;
};

export type AIWorkspaceFeatures = {
  overview?: AIWorkspaceFeature;
  chat?: AIWorkspaceFeature;
  agents?: AIWorkspaceFeature;
  models?: AIWorkspaceFeature;
  channels?: AIWorkspaceFeature;
  capabilities?: AIWorkspaceFeature;
  activity?: AIWorkspaceFeature;
};

export type AIWorkspaceContext = {
  scopeId: string;
  consistency: AIWorkspaceConsistency;
  pages: AIWorkspacePageLinks;
  apis: AIWorkspaceApiLinks;
  features: AIWorkspaceFeatures;
};

export type AIWorkspaceAgentSummary = {
  profileId: string;
  profileSlug: string;
  displayName: string;
  purpose: string;
  publishedRevision: number;
  publishedSnapshotSha256: string | null;
  published: boolean;
  status: AIWorkspaceAgentStatus;
};

export type AIWorkspaceSourceError = {
  code: string;
  message: string;
};

export type AIWorkspaceAgentCollection<
  OwnerKind extends AIWorkspaceAgentOwnerKind = AIWorkspaceAgentOwnerKind,
> = {
  source: 'agent_profile_catalog';
  ownerKind: OwnerKind;
  scopeId: string | null;
  availability: AIWorkspaceAgentCollectionAvailability;
  items: AIWorkspaceAgentSummary[];
  nextCursor: string | null;
  totalCount: number | null;
  authorityStateVersion: number | null;
  updatedAtUtc: string | null;
  error: AIWorkspaceSourceError | null;
};

export type AIWorkspaceAgents = {
  consistency: AIWorkspaceConsistency;
  owned: AIWorkspaceAgentCollection<'scope'>;
  systemTemplates: AIWorkspaceAgentCollection<'system'>;
};

export type AIWorkspaceAgentsQuery = {
  ownedCursor?: string;
  systemCursor?: string;
  take?: number;
};

export type AIWorkspaceOverviewQuery = {
  take?: number;
};

export type AIWorkspaceOverviewSource = {
  source: 'agent_profile_catalog';
  availability: AIWorkspaceAgentCollectionAvailability;
  itemCount: number | null;
  authorityStateVersion: number | null;
  updatedAtUtc: string | null;
  error: AIWorkspaceSourceError | null;
};

export type AIWorkspaceConversationSummary = {
  conversationId: string;
  title: string;
  serviceId: string;
  serviceKind: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
  llmRoute: string | null;
  llmModel: string | null;
  taskStatus: string | null;
  attentionKind: string | null;
  attentionSinceUtc: string | null;
  activeStepSummary: string | null;
  authorityStateVersion: number;
};

export type AIWorkspaceConversationCollection = {
  source: 'chat_history';
  scopeId: string;
  availability: AIWorkspaceActivityCollectionAvailability;
  items: AIWorkspaceConversationSummary[];
  nextCursor: string | null;
  error: AIWorkspaceSourceError | null;
};

export type AIWorkspaceRunStepSummary = {
  stepId: string;
  inputSummary: string;
  availability: string;
};

export type AIWorkspaceRunFailureSummary = {
  stepId: string;
  message: string;
  availability: string;
};

export type AIWorkspaceRunWaitingSummary = {
  stepId: string;
  waitingKind: string;
  availability: string;
};

export type AIWorkspaceRunSummary = {
  runId: string;
  workflowId: string | null;
  workflowName: string;
  status: string;
  runOrigin: string;
  success: boolean | null;
  inputSummary: string;
  currentStep: AIWorkspaceRunStepSummary | null;
  firstFailure: AIWorkspaceRunFailureSummary | null;
  waiting: AIWorkspaceRunWaitingSummary | null;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  updatedAtUtc: string;
  durationMs: number | null;
  authorityStateVersion: number;
};

export type AIWorkspaceRunCollection = {
  source: 'workflow_run_observatory';
  scopeId: string;
  availability: AIWorkspaceActivityCollectionAvailability;
  items: AIWorkspaceRunSummary[];
  nextCursor: string | null;
  hasMore: boolean;
  totalCount: number | null;
  error: AIWorkspaceSourceError | null;
};

export type AIWorkspaceOverview = {
  consistency: AIWorkspaceConsistency;
  agents: {
    owned: AIWorkspaceOverviewSource;
    systemTemplates: AIWorkspaceOverviewSource;
  };
  recentConversations: AIWorkspaceConversationCollection;
  recentRuns: AIWorkspaceRunCollection;
};

export class AIWorkspaceApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'AIWorkspaceApiError';
    this.code = code;
    this.status = status;
  }
}

type Decoder<T> = (value: unknown, label?: string) => T;

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

function readOptionalNonEmptyString(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): string | undefined {
  const value = readOptionalString(record, keys, label)?.trim();
  if (value === '') {
    throw new Error(`${label} must not be empty.`);
  }
  return value;
}

function readSafeNonNegativeInteger(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): number {
  const value = readNumber(record, keys, label);
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new Error(`${label} must be a non-negative safe integer.`);
  }
  return value;
}

function readNullableSafeNonNegativeInteger(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): number | null {
  const keyList = Array.isArray(keys) ? keys : [keys];
  const presentKey = keyList.find((key) => key in record);
  if (!presentKey || record[presentKey] === null) {
    return null;
  }
  return readSafeNonNegativeInteger(record, keyList, label);
}

function readNullableBoolean(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): boolean | null {
  const keyList = Array.isArray(keys) ? keys : [keys];
  const presentKey = keyList.find((key) => key in record);
  if (!presentKey || record[presentKey] === null) {
    return null;
  }
  return readBoolean(record, keyList, label);
}

function readNullableNonNegativeNumber(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): number | null {
  const keyList = Array.isArray(keys) ? keys : [keys];
  const presentKey = keyList.find((key) => key in record);
  if (!presentKey || record[presentKey] === null) {
    return null;
  }
  const value = readNumber(record, keyList, label);
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${label} must be a non-negative finite number.`);
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

function decodeOptionalLinks<Key extends string>(
  record: JsonRecord,
  fields: ReadonlyArray<readonly [Key, readonly string[]]>,
  label: string,
): Partial<Record<Key, string>> {
  const entries: Array<[Key, string]> = [];
  for (const [key, aliases] of fields) {
    const value = readOptionalNonEmptyString(
      record,
      [...aliases],
      `${label}.${key}`,
    );
    if (value) {
      entries.push([key, value]);
    }
  }
  return Object.fromEntries(entries) as Partial<Record<Key, string>>;
}

function decodePageLinks(
  value: unknown,
  label = 'AIWorkspacePageLinks',
): AIWorkspacePageLinks {
  const record = expectRecord(value, label);
  return decodeOptionalLinks(
    record,
    [
      ['overview', ['overview', 'Overview']],
      ['chat', ['chat', 'Chat']],
      ['agents', ['agents', 'Agents']],
      ['models', ['models', 'Models']],
      ['channels', ['channels', 'Channels']],
      ['capabilities', ['capabilities', 'Capabilities']],
      ['activity', ['activity', 'Activity']],
    ],
    label,
  );
}

function decodeApiLinks(
  value: unknown,
  label = 'AIWorkspaceApiLinks',
): AIWorkspaceApiLinks {
  const record = expectRecord(value, label);
  return decodeOptionalLinks(
    record,
    [
      ['overview', ['overview', 'Overview']],
      ['chat', ['chat', 'Chat']],
      ['agents', ['agents', 'Agents']],
      ['ownedAgentProfiles', ['ownedAgentProfiles', 'OwnedAgentProfiles']],
      ['systemAgentProfiles', ['systemAgentProfiles', 'SystemAgentProfiles']],
      ['models', ['models', 'Models']],
      [
        'personalModelSettings',
        ['personalModelSettings', 'PersonalModelSettings'],
      ],
      ['scopeModelCatalog', ['scopeModelCatalog', 'ScopeModelCatalog']],
      ['channels', ['channels', 'Channels']],
      [
        'channelRegistrations',
        ['channelRegistrations', 'ChannelRegistrations'],
      ],
      ['capabilities', ['capabilities', 'Capabilities']],
      ['activity', ['activity', 'Activity']],
      ['conversations', ['conversations', 'Conversations']],
      ['runs', ['runs', 'Runs']],
      ['auditedActions', ['auditedActions', 'AuditedActions']],
    ],
    label,
  );
}

function decodeFeature(value: unknown, label: string): AIWorkspaceFeature {
  const record = expectRecord(value, label);
  const api = readNullableString(record, ['api', 'API'], `${label}.api`);
  if (api !== null && !api.trim()) {
    throw new Error(`${label}.api must not be empty.`);
  }
  return {
    availability: readEnum(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
      ['available'],
    ),
    page: readNonEmptyString(record, ['page', 'Page'], `${label}.page`),
    api: api?.trim() ?? null,
  };
}

function decodeFeatures(
  value: unknown,
  label = 'AIWorkspaceFeatures',
): AIWorkspaceFeatures {
  const record = expectRecord(value, label);
  const keys: Array<keyof AIWorkspaceFeatures> = [
    'overview',
    'chat',
    'agents',
    'models',
    'channels',
    'capabilities',
    'activity',
  ];
  const entries: Array<[keyof AIWorkspaceFeatures, AIWorkspaceFeature]> = [];
  for (const key of keys) {
    const pascalKey = `${key.charAt(0).toUpperCase()}${key.slice(1)}`;
    const featureValue = record[key] ?? record[pascalKey];
    if (featureValue !== undefined && featureValue !== null) {
      entries.push([key, decodeFeature(featureValue, `${label}.${key}`)]);
    }
  }
  return Object.fromEntries(entries) as AIWorkspaceFeatures;
}

function decodeContext(
  value: unknown,
  label = 'AIWorkspaceContext',
): AIWorkspaceContext {
  const record = expectRecord(value, label);
  return {
    scopeId: readNonEmptyString(
      record,
      ['scopeId', 'ScopeId'],
      `${label}.scopeId`,
    ),
    consistency: readEnum(
      record,
      ['consistency', 'Consistency'],
      `${label}.consistency`,
      ['independent_read_models'],
    ),
    pages: decodePageLinks(record.pages ?? record.Pages, `${label}.pages`),
    apis: decodeApiLinks(
      record.apis ?? record.apIs ?? record.APIs,
      `${label}.apis`,
    ),
    features: decodeFeatures(
      record.features ?? record.Features,
      `${label}.features`,
    ),
  };
}

function decodeAgentSummary(
  value: unknown,
  label = 'AIWorkspaceAgentSummary',
): AIWorkspaceAgentSummary {
  const record = expectRecord(value, label);
  return {
    profileId: readNonEmptyString(
      record,
      ['profileId', 'ProfileId'],
      `${label}.profileId`,
    ),
    profileSlug: readNonEmptyString(
      record,
      ['profileSlug', 'ProfileSlug'],
      `${label}.profileSlug`,
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      `${label}.displayName`,
    ),
    purpose: readString(record, ['purpose', 'Purpose'], `${label}.purpose`),
    publishedRevision: readSafeNonNegativeInteger(
      record,
      ['publishedRevision', 'PublishedRevision'],
      `${label}.publishedRevision`,
    ),
    publishedSnapshotSha256: readNullableString(
      record,
      ['publishedSnapshotSha256', 'PublishedSnapshotSha256'],
      `${label}.publishedSnapshotSha256`,
    ),
    published: readBoolean(
      record,
      ['published', 'Published'],
      `${label}.published`,
    ),
    status: readEnum(record, ['status', 'Status'], `${label}.status`, [
      'active',
      'failed',
      'provisioning',
      'unspecified',
    ]),
  };
}

function decodeSourceError(
  value: unknown,
  label: string,
): AIWorkspaceSourceError | null {
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

function decodeAgentCollection<OwnerKind extends AIWorkspaceAgentOwnerKind>(
  value: unknown,
  expectedOwnerKind: OwnerKind,
  label: string,
): AIWorkspaceAgentCollection<OwnerKind> {
  const record = expectRecord(value, label);
  const ownerKind = readEnum(
    record,
    ['ownerKind', 'OwnerKind'],
    `${label}.ownerKind`,
    ['scope', 'system'],
  );
  if (ownerKind !== expectedOwnerKind) {
    throw new Error(`${label}.ownerKind must be ${expectedOwnerKind}.`);
  }
  const scopeId = readNullableString(
    record,
    ['scopeId', 'ScopeId'],
    `${label}.scopeId`,
  );
  if (expectedOwnerKind === 'scope' && !scopeId?.trim()) {
    throw new Error(`${label}.scopeId must identify the owning scope.`);
  }
  if (expectedOwnerKind === 'system' && scopeId !== null) {
    throw new Error(`${label}.scopeId must be null for system templates.`);
  }
  const error = decodeSourceError(
    record.error ?? record.Error,
    `${label}.error`,
  );

  return {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'agent_profile_catalog',
    ]),
    ownerKind: expectedOwnerKind,
    scopeId,
    availability: readEnum(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
      ['available', 'not_materialized', 'unavailable'],
    ),
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeAgentSummary,
    ),
    nextCursor: readNullableString(
      record,
      ['nextCursor', 'NextCursor'],
      `${label}.nextCursor`,
    ),
    totalCount: readNullableSafeNonNegativeInteger(
      record,
      ['totalCount', 'TotalCount'],
      `${label}.totalCount`,
    ),
    authorityStateVersion: readNullableSafeNonNegativeInteger(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
    updatedAtUtc: readNullableString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    error,
  };
}

function decodeAgents(
  value: unknown,
  label = 'AIWorkspaceAgents',
): AIWorkspaceAgents {
  const record = expectRecord(value, label);
  return {
    consistency: readEnum(
      record,
      ['consistency', 'Consistency'],
      `${label}.consistency`,
      ['independent_read_models'],
    ),
    owned: decodeAgentCollection(
      record.owned ?? record.Owned,
      'scope',
      `${label}.owned`,
    ),
    systemTemplates: decodeAgentCollection(
      record.systemTemplates ?? record.SystemTemplates,
      'system',
      `${label}.systemTemplates`,
    ),
  };
}

function decodeOverviewSource(
  value: unknown,
  label: string,
): AIWorkspaceOverviewSource {
  const record = expectRecord(value, label);
  return {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'agent_profile_catalog',
    ]),
    availability: readEnum(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
      ['available', 'not_materialized', 'unavailable'],
    ),
    itemCount: readNullableSafeNonNegativeInteger(
      record,
      ['itemCount', 'ItemCount'],
      `${label}.itemCount`,
    ),
    authorityStateVersion: readNullableSafeNonNegativeInteger(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
    updatedAtUtc: readNullableString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    error: decodeSourceError(record.error ?? record.Error, `${label}.error`),
  };
}

function decodeConversationSummary(
  value: unknown,
  label = 'AIWorkspaceConversationSummary',
): AIWorkspaceConversationSummary {
  const record = expectRecord(value, label);
  return {
    conversationId: readNonEmptyString(
      record,
      ['conversationId', 'ConversationId'],
      `${label}.conversationId`,
    ),
    title: readString(record, ['title', 'Title'], `${label}.title`),
    serviceId: readString(
      record,
      ['serviceId', 'ServiceId'],
      `${label}.serviceId`,
    ),
    serviceKind: readString(
      record,
      ['serviceKind', 'ServiceKind'],
      `${label}.serviceKind`,
    ),
    createdAtUtc: readNonEmptyString(
      record,
      ['createdAtUtc', 'CreatedAtUtc'],
      `${label}.createdAtUtc`,
    ),
    updatedAtUtc: readNonEmptyString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    messageCount: readSafeNonNegativeInteger(
      record,
      ['messageCount', 'MessageCount'],
      `${label}.messageCount`,
    ),
    llmRoute: readNullableString(
      record,
      ['llmRoute', 'LlmRoute'],
      `${label}.llmRoute`,
    ),
    llmModel: readNullableString(
      record,
      ['llmModel', 'LlmModel'],
      `${label}.llmModel`,
    ),
    taskStatus: readNullableString(
      record,
      ['taskStatus', 'TaskStatus'],
      `${label}.taskStatus`,
    ),
    attentionKind: readNullableString(
      record,
      ['attentionKind', 'AttentionKind'],
      `${label}.attentionKind`,
    ),
    attentionSinceUtc: readNullableString(
      record,
      ['attentionSinceUtc', 'AttentionSinceUtc'],
      `${label}.attentionSinceUtc`,
    ),
    activeStepSummary: readNullableString(
      record,
      ['activeStepSummary', 'ActiveStepSummary'],
      `${label}.activeStepSummary`,
    ),
    authorityStateVersion: readSafeNonNegativeInteger(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
  };
}

function decodeConversationCollection(
  value: unknown,
  label: string,
): AIWorkspaceConversationCollection {
  const record = expectRecord(value, label);
  return {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'chat_history',
    ]),
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
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeConversationSummary,
    ),
    nextCursor: readNullableString(
      record,
      ['nextCursor', 'NextCursor'],
      `${label}.nextCursor`,
    ),
    error: decodeSourceError(record.error ?? record.Error, `${label}.error`),
  };
}

function decodeNullableRecord<T>(
  value: unknown,
  label: string,
  decoder: (record: JsonRecord, label: string) => T,
): T | null {
  if (value === null || value === undefined) {
    return null;
  }
  return decoder(expectRecord(value, label), label);
}

function decodeRunStepSummary(
  record: JsonRecord,
  label: string,
): AIWorkspaceRunStepSummary {
  return {
    stepId: readString(record, ['stepId', 'StepId'], `${label}.stepId`),
    inputSummary: readString(
      record,
      ['inputSummary', 'InputSummary'],
      `${label}.inputSummary`,
    ),
    availability: readString(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
    ),
  };
}

function decodeRunFailureSummary(
  record: JsonRecord,
  label: string,
): AIWorkspaceRunFailureSummary {
  return {
    stepId: readString(record, ['stepId', 'StepId'], `${label}.stepId`),
    message: readString(record, ['message', 'Message'], `${label}.message`),
    availability: readString(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
    ),
  };
}

function decodeRunWaitingSummary(
  record: JsonRecord,
  label: string,
): AIWorkspaceRunWaitingSummary {
  return {
    stepId: readString(record, ['stepId', 'StepId'], `${label}.stepId`),
    waitingKind: readString(
      record,
      ['waitingKind', 'WaitingKind'],
      `${label}.waitingKind`,
    ),
    availability: readString(
      record,
      ['availability', 'Availability'],
      `${label}.availability`,
    ),
  };
}

function decodeRunSummary(
  value: unknown,
  label = 'AIWorkspaceRunSummary',
): AIWorkspaceRunSummary {
  const record = expectRecord(value, label);
  return {
    runId: readNonEmptyString(record, ['runId', 'RunId'], `${label}.runId`),
    workflowId: readNullableString(
      record,
      ['workflowId', 'WorkflowId'],
      `${label}.workflowId`,
    ),
    workflowName: readString(
      record,
      ['workflowName', 'WorkflowName'],
      `${label}.workflowName`,
    ),
    status: readString(record, ['status', 'Status'], `${label}.status`),
    runOrigin: readString(
      record,
      ['runOrigin', 'RunOrigin'],
      `${label}.runOrigin`,
    ),
    success: readNullableBoolean(
      record,
      ['success', 'Success'],
      `${label}.success`,
    ),
    inputSummary: readString(
      record,
      ['inputSummary', 'InputSummary'],
      `${label}.inputSummary`,
    ),
    currentStep: decodeNullableRecord(
      record.currentStep ?? record.CurrentStep,
      `${label}.currentStep`,
      decodeRunStepSummary,
    ),
    firstFailure: decodeNullableRecord(
      record.firstFailure ?? record.FirstFailure,
      `${label}.firstFailure`,
      decodeRunFailureSummary,
    ),
    waiting: decodeNullableRecord(
      record.waiting ?? record.Waiting,
      `${label}.waiting`,
      decodeRunWaitingSummary,
    ),
    startedAtUtc: readNullableString(
      record,
      ['startedAtUtc', 'StartedAtUtc'],
      `${label}.startedAtUtc`,
    ),
    completedAtUtc: readNullableString(
      record,
      ['completedAtUtc', 'CompletedAtUtc'],
      `${label}.completedAtUtc`,
    ),
    updatedAtUtc: readNonEmptyString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    durationMs: readNullableNonNegativeNumber(
      record,
      ['durationMs', 'DurationMs'],
      `${label}.durationMs`,
    ),
    authorityStateVersion: readSafeNonNegativeInteger(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
  };
}

function decodeRunCollection(
  value: unknown,
  label: string,
): AIWorkspaceRunCollection {
  const record = expectRecord(value, label);
  return {
    source: readEnum(record, ['source', 'Source'], `${label}.source`, [
      'workflow_run_observatory',
    ]),
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
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeRunSummary,
    ),
    nextCursor: readNullableString(
      record,
      ['nextCursor', 'NextCursor'],
      `${label}.nextCursor`,
    ),
    hasMore: readBoolean(record, ['hasMore', 'HasMore'], `${label}.hasMore`),
    totalCount: readNullableSafeNonNegativeInteger(
      record,
      ['totalCount', 'TotalCount'],
      `${label}.totalCount`,
    ),
    error: decodeSourceError(record.error ?? record.Error, `${label}.error`),
  };
}

function decodeOverview(
  value: unknown,
  label = 'AIWorkspaceOverview',
): AIWorkspaceOverview {
  const record = expectRecord(value, label);
  const agents = expectRecord(
    record.agents ?? record.Agents,
    `${label}.agents`,
  );
  return {
    consistency: readEnum(
      record,
      ['consistency', 'Consistency'],
      `${label}.consistency`,
      ['independent_read_models'],
    ),
    agents: {
      owned: decodeOverviewSource(
        agents.owned ?? agents.Owned,
        `${label}.agents.owned`,
      ),
      systemTemplates: decodeOverviewSource(
        agents.systemTemplates ?? agents.SystemTemplates,
        `${label}.agents.systemTemplates`,
      ),
    },
    recentConversations: decodeConversationCollection(
      record.recentConversations ?? record.RecentConversations,
      `${label}.recentConversations`,
    ),
    recentRuns: decodeRunCollection(
      record.recentRuns ?? record.RecentRuns,
      `${label}.recentRuns`,
    ),
  };
}

async function requestAIWorkspaceJson<T>(
  input: string,
  decoder: Decoder<T>,
  signal?: AbortSignal,
): Promise<T> {
  const response = await authFetch(input, {
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) {
    const error = await readResponseErrorDetails(response);
    throw new AIWorkspaceApiError(error.message, error.status, error.code);
  }

  return decoder(await response.json());
}

export const aiWorkspaceApi = {
  getContext(signal?: AbortSignal): Promise<AIWorkspaceContext> {
    return requestAIWorkspaceJson('/api/ai/context', decodeContext, signal);
  },

  getAgents(
    query: AIWorkspaceAgentsQuery = {},
    signal?: AbortSignal,
  ): Promise<AIWorkspaceAgents> {
    return requestAIWorkspaceJson(
      withQuery('/api/ai/agents', {
        ownedCursor: query.ownedCursor?.trim() || undefined,
        systemCursor: query.systemCursor?.trim() || undefined,
        take: query.take,
      }),
      decodeAgents,
      signal,
    );
  },

  getOverview(
    query: AIWorkspaceOverviewQuery = {},
    signal?: AbortSignal,
  ): Promise<AIWorkspaceOverview> {
    return requestAIWorkspaceJson(
      withQuery('/api/ai/overview', { take: query.take }),
      decodeOverview,
      signal,
    );
  },
};
