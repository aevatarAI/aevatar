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
  readStringArray,
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

export type AIWorkspaceActivity = {
  consistency: AIWorkspaceConsistency;
  conversations: AIWorkspaceConversationCollection;
  runs: AIWorkspaceRunCollection;
};

export type AIWorkspaceConversationsQuery = {
  take?: number;
  cursor?: string;
};

export type AIWorkspaceRunsQuery = {
  status?: string;
  origins?: readonly string[];
  workflowId?: string;
  q?: string;
  from?: string;
  to?: string;
  take?: number;
  cursor?: string;
  includeTotalCount?: boolean;
};

export type AIWorkspaceRunDetailSectionVersionStatus =
  | 'unknown'
  | 'aligned'
  | 'unavailable'
  | 'version_mismatch';

export type AIWorkspaceRunDetailSectionVersion = {
  detailStateVersion: number;
  sourceStateVersion: number;
  versionStatus: AIWorkspaceRunDetailSectionVersionStatus;
  reason: string | null;
};

export type AIWorkspaceRunDetailSectionVersions = {
  overview: AIWorkspaceRunDetailSectionVersion;
  steps: AIWorkspaceRunDetailSectionVersion;
  timeline: AIWorkspaceRunDetailSectionVersion;
  executionPath: AIWorkspaceRunDetailSectionVersion;
};

export type AIWorkspaceUsageTotals = {
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  cost: number;
};

export type AIWorkspaceRunStepDetail = {
  stepId: string;
  displayName: string;
  requestedAtUtc: string | null;
  completedAtUtc: string | null;
  success: boolean | null;
  outcome: string;
  durationMs: number | null;
  failureOutputTruncated: boolean;
  nextStepId: string;
  branchKey: string;
  suspensionType: string;
  suspensionTimeoutSeconds: number | null;
  usage: AIWorkspaceUsageTotals;
};

export type AIWorkspaceRunToolCall = {
  toolName: string;
  callId: string;
  success: boolean;
};

export type AIWorkspaceRunTimelineEvent = {
  kind: string;
  timestampUtc: string;
  stage: string;
  stepId: string;
  toolCall: AIWorkspaceRunToolCall | null;
};

export type AIWorkspaceRunOperation = {
  operationId: string;
  kind: string;
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  model: string;
  provider: string;
  availableToolNames: string[];
  finishReason: string;
  usage: AIWorkspaceUsageTotals;
  success: boolean | null;
  toolCallId: string;
  toolName: string;
  durationMs: number | null;
};

export type AIWorkspaceRunStatistics = {
  totalSteps: number;
  requestedSteps: number;
  completedSteps: number;
  roleReplyCount: number;
  stepTypeCounts: Record<string, number>;
};

export type AIWorkspaceRunDetail = {
  source: 'workflow_run_observatory';
  scopeId: string;
  authorityStateVersion: number;
  updatedAtUtc: string;
  reportVersion: string | null;
  sections: AIWorkspaceRunDetailSectionVersions;
  summary: AIWorkspaceRunSummary;
  finalOutput: string;
  steps: AIWorkspaceRunStepDetail[];
  timeline: AIWorkspaceRunTimelineEvent[];
  operations: AIWorkspaceRunOperation[];
  statistics: AIWorkspaceRunStatistics;
  usageTotals: AIWorkspaceUsageTotals;
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

function decodeActivity(
  value: unknown,
  label = 'AIWorkspaceActivity',
): AIWorkspaceActivity {
  const record = expectRecord(value, label);
  return {
    consistency: readEnum(
      record,
      ['consistency', 'Consistency'],
      `${label}.consistency`,
      ['independent_read_models'],
    ),
    conversations: decodeConversationCollection(
      record.conversations ?? record.Conversations,
      `${label}.conversations`,
    ),
    runs: decodeRunCollection(record.runs ?? record.Runs, `${label}.runs`),
  };
}

function decodeUsageTotals(
  value: unknown,
  label = 'AIWorkspaceUsageTotals',
): AIWorkspaceUsageTotals {
  const record = expectRecord(value, label);
  return {
    promptTokens: readSafeNonNegativeInteger(
      record,
      ['promptTokens', 'PromptTokens'],
      `${label}.promptTokens`,
    ),
    completionTokens: readSafeNonNegativeInteger(
      record,
      ['completionTokens', 'CompletionTokens'],
      `${label}.completionTokens`,
    ),
    totalTokens: readSafeNonNegativeInteger(
      record,
      ['totalTokens', 'TotalTokens'],
      `${label}.totalTokens`,
    ),
    cost: readNonNegativeNumber(record, ['cost', 'Cost'], `${label}.cost`),
  };
}

function readNonNegativeNumber(
  record: JsonRecord,
  keys: string | string[],
  label: string,
): number {
  const value = readNumber(record, keys, label);
  if (!Number.isFinite(value) || value < 0) {
    throw new Error(`${label} must be a non-negative finite number.`);
  }
  return value;
}

function decodeRunDetailSectionVersion(
  value: unknown,
  label: string,
): AIWorkspaceRunDetailSectionVersion {
  const record = expectRecord(value, label);
  return {
    detailStateVersion: readSafeNonNegativeInteger(
      record,
      ['detailStateVersion', 'DetailStateVersion'],
      `${label}.detailStateVersion`,
    ),
    sourceStateVersion: readSafeNonNegativeInteger(
      record,
      ['sourceStateVersion', 'SourceStateVersion'],
      `${label}.sourceStateVersion`,
    ),
    versionStatus: readEnum(
      record,
      ['versionStatus', 'VersionStatus'],
      `${label}.versionStatus`,
      ['unknown', 'aligned', 'unavailable', 'version_mismatch'],
    ),
    reason: readNullableString(record, ['reason', 'Reason'], `${label}.reason`),
  };
}

function decodeRunDetailSections(
  value: unknown,
  label: string,
): AIWorkspaceRunDetailSectionVersions {
  const record = expectRecord(value, label);
  return {
    overview: decodeRunDetailSectionVersion(
      record.overview ?? record.Overview,
      `${label}.overview`,
    ),
    steps: decodeRunDetailSectionVersion(
      record.steps ?? record.Steps,
      `${label}.steps`,
    ),
    timeline: decodeRunDetailSectionVersion(
      record.timeline ?? record.Timeline,
      `${label}.timeline`,
    ),
    executionPath: decodeRunDetailSectionVersion(
      record.executionPath ?? record.ExecutionPath,
      `${label}.executionPath`,
    ),
  };
}

function decodeRunStepDetail(
  value: unknown,
  label = 'AIWorkspaceRunStepDetail',
): AIWorkspaceRunStepDetail {
  const record = expectRecord(value, label);
  return {
    stepId: readString(record, ['stepId', 'StepId'], `${label}.stepId`),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      `${label}.displayName`,
    ),
    requestedAtUtc: readNullableString(
      record,
      ['requestedAtUtc', 'RequestedAtUtc'],
      `${label}.requestedAtUtc`,
    ),
    completedAtUtc: readNullableString(
      record,
      ['completedAtUtc', 'CompletedAtUtc'],
      `${label}.completedAtUtc`,
    ),
    success: readNullableBoolean(
      record,
      ['success', 'Success'],
      `${label}.success`,
    ),
    outcome: readString(record, ['outcome', 'Outcome'], `${label}.outcome`),
    durationMs: readNullableNonNegativeNumber(
      record,
      ['durationMs', 'DurationMs'],
      `${label}.durationMs`,
    ),
    failureOutputTruncated: readBoolean(
      record,
      ['failureOutputTruncated', 'FailureOutputTruncated'],
      `${label}.failureOutputTruncated`,
    ),
    nextStepId: readString(
      record,
      ['nextStepId', 'NextStepId'],
      `${label}.nextStepId`,
    ),
    branchKey: readString(
      record,
      ['branchKey', 'BranchKey'],
      `${label}.branchKey`,
    ),
    suspensionType: readString(
      record,
      ['suspensionType', 'SuspensionType'],
      `${label}.suspensionType`,
    ),
    suspensionTimeoutSeconds: readNullableSafeNonNegativeInteger(
      record,
      ['suspensionTimeoutSeconds', 'SuspensionTimeoutSeconds'],
      `${label}.suspensionTimeoutSeconds`,
    ),
    usage: decodeUsageTotals(record.usage ?? record.Usage, `${label}.usage`),
  };
}

function decodeRunToolCall(
  value: unknown,
  label = 'AIWorkspaceRunToolCall',
): AIWorkspaceRunToolCall {
  const record = expectRecord(value, label);
  return {
    toolName: readString(record, ['toolName', 'ToolName'], `${label}.toolName`),
    callId: readString(record, ['callId', 'CallId'], `${label}.callId`),
    success: readBoolean(record, ['success', 'Success'], `${label}.success`),
  };
}

function decodeRunTimelineEvent(
  value: unknown,
  label = 'AIWorkspaceRunTimelineEvent',
): AIWorkspaceRunTimelineEvent {
  const record = expectRecord(value, label);
  return {
    kind: readString(record, ['kind', 'Kind'], `${label}.kind`),
    timestampUtc: readNonEmptyString(
      record,
      ['timestampUtc', 'TimestampUtc'],
      `${label}.timestampUtc`,
    ),
    stage: readString(record, ['stage', 'Stage'], `${label}.stage`),
    stepId: readString(record, ['stepId', 'StepId'], `${label}.stepId`),
    toolCall: decodeNullableRecord(
      record.toolCall ?? record.ToolCall,
      `${label}.toolCall`,
      (nested, nestedLabel) => decodeRunToolCall(nested, nestedLabel),
    ),
  };
}

function decodeRunOperation(
  value: unknown,
  label = 'AIWorkspaceRunOperation',
): AIWorkspaceRunOperation {
  const record = expectRecord(value, label);
  return {
    operationId: readString(
      record,
      ['operationId', 'OperationId'],
      `${label}.operationId`,
    ),
    kind: readString(record, ['kind', 'Kind'], `${label}.kind`),
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
    model: readString(record, ['model', 'Model'], `${label}.model`),
    provider: readString(record, ['provider', 'Provider'], `${label}.provider`),
    availableToolNames: readStringArray(
      record,
      ['availableToolNames', 'AvailableToolNames'],
      `${label}.availableToolNames`,
    ),
    finishReason: readString(
      record,
      ['finishReason', 'FinishReason'],
      `${label}.finishReason`,
    ),
    usage: decodeUsageTotals(record.usage ?? record.Usage, `${label}.usage`),
    success: readNullableBoolean(
      record,
      ['success', 'Success'],
      `${label}.success`,
    ),
    toolCallId: readString(
      record,
      ['toolCallId', 'ToolCallId'],
      `${label}.toolCallId`,
    ),
    toolName: readString(record, ['toolName', 'ToolName'], `${label}.toolName`),
    durationMs: readNullableNonNegativeNumber(
      record,
      ['durationMs', 'DurationMs'],
      `${label}.durationMs`,
    ),
  };
}

function decodeStepTypeCounts(
  value: unknown,
  label: string,
): Record<string, number> {
  const record = expectRecord(value, label);
  return Object.fromEntries(
    Object.entries(record).map(([key, entry]) => {
      if (
        typeof entry !== 'number' ||
        !Number.isSafeInteger(entry) ||
        entry < 0
      ) {
        throw new Error(`${label}.${key} must be a non-negative safe integer.`);
      }
      return [key, entry];
    }),
  );
}

function decodeRunStatistics(
  value: unknown,
  label = 'AIWorkspaceRunStatistics',
): AIWorkspaceRunStatistics {
  const record = expectRecord(value, label);
  return {
    totalSteps: readSafeNonNegativeInteger(
      record,
      ['totalSteps', 'TotalSteps'],
      `${label}.totalSteps`,
    ),
    requestedSteps: readSafeNonNegativeInteger(
      record,
      ['requestedSteps', 'RequestedSteps'],
      `${label}.requestedSteps`,
    ),
    completedSteps: readSafeNonNegativeInteger(
      record,
      ['completedSteps', 'CompletedSteps'],
      `${label}.completedSteps`,
    ),
    roleReplyCount: readSafeNonNegativeInteger(
      record,
      ['roleReplyCount', 'RoleReplyCount'],
      `${label}.roleReplyCount`,
    ),
    stepTypeCounts: decodeStepTypeCounts(
      record.stepTypeCounts ?? record.StepTypeCounts,
      `${label}.stepTypeCounts`,
    ),
  };
}

function decodeRunDetail(
  value: unknown,
  label = 'AIWorkspaceRunDetail',
): AIWorkspaceRunDetail {
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
    authorityStateVersion: readSafeNonNegativeInteger(
      record,
      ['authorityStateVersion', 'AuthorityStateVersion'],
      `${label}.authorityStateVersion`,
    ),
    updatedAtUtc: readNonEmptyString(
      record,
      ['updatedAtUtc', 'UpdatedAtUtc'],
      `${label}.updatedAtUtc`,
    ),
    reportVersion: readNullableString(
      record,
      ['reportVersion', 'ReportVersion'],
      `${label}.reportVersion`,
    ),
    sections: decodeRunDetailSections(
      record.sections ?? record.Sections,
      `${label}.sections`,
    ),
    summary: decodeRunSummary(
      record.summary ?? record.Summary,
      `${label}.summary`,
    ),
    finalOutput: readString(
      record,
      ['finalOutput', 'FinalOutput'],
      `${label}.finalOutput`,
    ),
    steps: expectArray(
      record.steps ?? record.Steps,
      `${label}.steps`,
      decodeRunStepDetail,
    ),
    timeline: expectArray(
      record.timeline ?? record.Timeline,
      `${label}.timeline`,
      decodeRunTimelineEvent,
    ),
    operations: expectArray(
      record.operations ?? record.Operations,
      `${label}.operations`,
      decodeRunOperation,
    ),
    statistics: decodeRunStatistics(
      record.statistics ?? record.Statistics,
      `${label}.statistics`,
    ),
    usageTotals: decodeUsageTotals(
      record.usageTotals ?? record.UsageTotals,
      `${label}.usageTotals`,
    ),
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

function readCollectionErrorPayload(value: unknown): {
  code?: string;
  message?: string;
} {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {};
  }
  const record = value as JsonRecord;
  const nested =
    record.error &&
    typeof record.error === 'object' &&
    !Array.isArray(record.error)
      ? (record.error as JsonRecord)
      : undefined;
  const code = nested?.code ?? nested?.Code ?? record.code ?? record.Code;
  const message =
    nested?.message ?? nested?.Message ?? record.message ?? record.Message;
  return {
    code: typeof code === 'string' && code.trim() ? code.trim() : undefined,
    message:
      typeof message === 'string' && message.trim()
        ? message.trim()
        : undefined,
  };
}

async function readResponseJsonBody(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    if (typeof response.text !== 'function') {
      return undefined;
    }
    const text = await response.text();
    if (!text.trim()) {
      return undefined;
    }
    try {
      return JSON.parse(text) as unknown;
    } catch {
      return undefined;
    }
  }
}

async function requestAIWorkspaceCollectionJson<
  T extends { availability: AIWorkspaceActivityCollectionAvailability },
>(input: string, decoder: Decoder<T>, signal?: AbortSignal): Promise<T> {
  const response = await authFetch(input, {
    headers: { Accept: 'application/json' },
    ...(signal ? { signal } : {}),
  });
  if (response.ok) {
    return decoder(await response.json());
  }

  if (response.status === 503) {
    const payload = await readResponseJsonBody(response);
    try {
      const decoded = decoder(payload);
      if (decoded.availability === 'unavailable') {
        return decoded;
      }
    } catch {
      // Fall through to the regular typed HTTP error for malformed bodies.
    }

    const bodyError = readCollectionErrorPayload(payload);
    throw new AIWorkspaceApiError(
      bodyError.message ??
        `HTTP ${response.status} ${response.statusText}`.trim(),
      response.status,
      bodyError.code,
    );
  }

  const error = await readResponseErrorDetails(response);
  throw new AIWorkspaceApiError(error.message, error.status, error.code);
}

function trimOptionalQueryValue(value: string | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized || undefined;
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

  getActivity(
    query: {
      take?: number;
      conversationCursor?: string;
      runCursor?: string;
    } = {},
    signal?: AbortSignal,
  ): Promise<AIWorkspaceActivity> {
    return requestAIWorkspaceJson(
      withQuery('/api/ai/activity', {
        take: query.take,
        conversationCursor: trimOptionalQueryValue(query.conversationCursor),
        runCursor: trimOptionalQueryValue(query.runCursor),
      }),
      decodeActivity,
      signal,
    );
  },

  getConversations(
    query: AIWorkspaceConversationsQuery = {},
    signal?: AbortSignal,
  ): Promise<AIWorkspaceConversationCollection> {
    return requestAIWorkspaceCollectionJson(
      withQuery('/api/ai/activity/conversations', {
        take: query.take,
        cursor: trimOptionalQueryValue(query.cursor),
      }),
      (value, label) =>
        decodeConversationCollection(
          value,
          label ?? 'AIWorkspaceConversationCollection',
        ),
      signal,
    );
  },

  getRuns(
    query: AIWorkspaceRunsQuery = {},
    signal?: AbortSignal,
  ): Promise<AIWorkspaceRunCollection> {
    const origins = query.origins
      ?.map((origin) => origin.trim())
      .filter(Boolean)
      .join(',');
    return requestAIWorkspaceCollectionJson(
      withQuery('/api/ai/activity/runs', {
        status: trimOptionalQueryValue(query.status),
        origins: origins || undefined,
        workflowId: trimOptionalQueryValue(query.workflowId),
        q: trimOptionalQueryValue(query.q),
        from: trimOptionalQueryValue(query.from),
        to: trimOptionalQueryValue(query.to),
        take: query.take,
        cursor: trimOptionalQueryValue(query.cursor),
        includeTotalCount: query.includeTotalCount,
      }),
      (value, label) =>
        decodeRunCollection(value, label ?? 'AIWorkspaceRunCollection'),
      signal,
    );
  },

  async getRun(
    runId: string,
    signal?: AbortSignal,
  ): Promise<AIWorkspaceRunDetail> {
    const normalizedRunId = runId.trim();
    if (!normalizedRunId) {
      throw new AIWorkspaceApiError(
        'Workflow run was not found.',
        404,
        'WORKFLOW_RUN_NOT_FOUND',
      );
    }

    const detail = await requestAIWorkspaceJson(
      `/api/ai/activity/runs/${encodeURIComponent(normalizedRunId)}`,
      decodeRunDetail,
      signal,
    );
    if (detail.summary.runId !== normalizedRunId) {
      throw new AIWorkspaceApiError(
        'The workflow run detail did not match the requested run.',
        502,
        'WORKFLOW_RUN_ID_MISMATCH',
      );
    }
    return detail;
  },
};
