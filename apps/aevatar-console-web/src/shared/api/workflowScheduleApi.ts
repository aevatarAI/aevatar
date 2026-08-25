import { jsonBody, requestJson, withQuery } from '@/shared/api/http/client';
import {
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalArray,
  readString,
} from './http/decoders';

export type WorkflowScheduleConfigurationInput = {
  readonly scheduleId?: string;
  readonly displayName: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
  readonly prompt?: string;
  readonly headers?: Readonly<Record<string, string>>;
};

export type WorkflowSchedulePreviewInput = {
  readonly cronExpression: string;
  readonly timezone: string;
  readonly count?: number;
  readonly fromUtc?: string;
};

export type WorkflowScheduleListQuery = {
  readonly cursor?: string;
  readonly includeTotalCount?: boolean;
  readonly take?: number;
};

export type WorkflowScheduleSummary = {
  readonly scheduleId: string;
  readonly displayName: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly nextFireAt: string | null;
  readonly lastFireAt: string | null;
  readonly fireCount: number;
  readonly failureCount: number;
};

export type WorkflowScheduleFire = {
  readonly scheduledFireAt: string;
  readonly completedAt: string;
  readonly idempotencyKey: string;
  readonly runActorId: string;
  readonly error: string;
  readonly manual: boolean;
};

export type WorkflowScheduleDetail = {
  readonly schedule: WorkflowScheduleSummary;
  readonly recentFires: readonly WorkflowScheduleFire[];
};

export type WorkflowSchedulePreview = {
  readonly cronExpression: string;
  readonly timezone: string;
  readonly nextFireTimes: readonly string[];
};

export type WorkflowScheduleMutationReceipt = {
  readonly scheduleId: string;
  readonly accepted: boolean;
};

export type WorkflowScheduleRunNowReceipt = WorkflowScheduleMutationReceipt & {
  readonly scheduledFireAt: string;
};

export type WorkflowScheduleListResult = {
  readonly items: readonly WorkflowScheduleSummary[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
};

export type WorkflowScheduleApi = {
  readonly list: (
    scopeId: string,
    workflowId: string,
    query?: WorkflowScheduleListQuery,
  ) => Promise<WorkflowScheduleListResult>;
  readonly get: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
  ) => Promise<WorkflowScheduleDetail>;
  readonly preview: (
    scopeId: string,
    workflowId: string,
    input: WorkflowSchedulePreviewInput,
  ) => Promise<WorkflowSchedulePreview>;
  readonly create: (
    scopeId: string,
    workflowId: string,
    input: WorkflowScheduleConfigurationInput,
  ) => Promise<WorkflowScheduleMutationReceipt>;
  readonly update: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
    input: WorkflowScheduleConfigurationInput,
  ) => Promise<WorkflowScheduleMutationReceipt>;
  readonly enable: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
    reason?: string,
  ) => Promise<WorkflowScheduleMutationReceipt>;
  readonly disable: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
    reason?: string,
  ) => Promise<WorkflowScheduleMutationReceipt>;
  readonly runNow: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
  ) => Promise<WorkflowScheduleRunNowReceipt>;
  readonly delete: (
    scopeId: string,
    workflowId: string,
    scheduleId: string,
    reason?: string,
  ) => Promise<WorkflowScheduleMutationReceipt>;
};

function readDateTimeString(
  record: Record<string, unknown>,
  keys: [string, string],
  label: string,
): string {
  const value = record[keys[0]] ?? record[keys[1]];
  if (value instanceof Date) return value.toISOString();
  if (typeof value === 'string') return value;
  throw new Error(`${label} must be a string.`);
}

function readNullableDateTimeString(
  record: Record<string, unknown>,
  keys: [string, string],
): string | null {
  const value = record[keys[0]] ?? record[keys[1]];
  if (value === null || value === undefined) return null;
  if (value instanceof Date) return value.toISOString();
  if (typeof value === 'string') return value;
  throw new Error(`${keys[0]} must be a string or null.`);
}

function decodeWorkflowScheduleSummary(
  value: unknown,
  label = 'WorkflowScheduleSummary',
): WorkflowScheduleSummary {
  const record = expectRecord(value, label);
  return {
    scheduleId: readString(
      record,
      ['scheduleId', 'ScheduleId'],
      `${label}.scheduleId`,
    ),
    displayName: readString(
      record,
      ['displayName', 'DisplayName'],
      `${label}.displayName`,
    ),
    prompt:
      readNullableString(record, ['prompt', 'Prompt'], `${label}.prompt`) ?? '',
    cronExpression: readString(
      record,
      ['cronExpression', 'CronExpression'],
      `${label}.cronExpression`,
    ),
    timezone: readString(record, ['timezone', 'Timezone'], `${label}.timezone`),
    enabled: readBoolean(record, ['enabled', 'Enabled'], `${label}.enabled`),
    createdAt: readDateTimeString(
      record,
      ['createdAt', 'CreatedAt'],
      `${label}.createdAt`,
    ),
    updatedAt: readDateTimeString(
      record,
      ['updatedAt', 'UpdatedAt'],
      `${label}.updatedAt`,
    ),
    nextFireAt: readNullableDateTimeString(record, [
      'nextFireAt',
      'NextFireAt',
    ]),
    lastFireAt: readNullableDateTimeString(record, [
      'lastFireAt',
      'LastFireAt',
    ]),
    fireCount: readNumber(
      record,
      ['fireCount', 'FireCount'],
      `${label}.fireCount`,
    ),
    failureCount: readNumber(
      record,
      ['failureCount', 'FailureCount'],
      `${label}.failureCount`,
    ),
  };
}

function decodeWorkflowScheduleDetail(value: unknown): WorkflowScheduleDetail {
  const record = expectRecord(value, 'WorkflowScheduleDetail');
  return {
    schedule: decodeWorkflowScheduleSummary(record.schedule ?? record.Schedule),
    recentFires: readOptionalArray(
      record,
      ['recentFires', 'RecentFires'],
      'WorkflowScheduleDetail.recentFires',
      (entry, label) => {
        const entryLabel = label ?? 'WorkflowScheduleDetail.recentFires[]';
        const fire = expectRecord(entry, entryLabel);
        return {
          scheduledFireAt: readDateTimeString(
            fire,
            ['scheduledFireAt', 'ScheduledFireAt'],
            `${entryLabel}.scheduledFireAt`,
          ),
          completedAt: readDateTimeString(
            fire,
            ['completedAt', 'CompletedAt'],
            `${entryLabel}.completedAt`,
          ),
          idempotencyKey: readString(
            fire,
            ['idempotencyKey', 'IdempotencyKey'],
            `${entryLabel}.idempotencyKey`,
          ),
          runActorId:
            readNullableString(
              fire,
              ['runActorId', 'RunActorId'],
              `${entryLabel}.runActorId`,
            ) ??
            readNullableString(
              fire,
              ['targetActorId', 'TargetActorId'],
              `${entryLabel}.targetActorId`,
            ) ??
            '',
          error: readString(fire, ['error', 'Error'], `${entryLabel}.error`),
          manual: readBoolean(
            fire,
            ['manual', 'Manual'],
            `${entryLabel}.manual`,
          ),
        };
      },
    ),
  };
}

function decodeWorkflowScheduleList(
  value: unknown,
): WorkflowScheduleListResult {
  const record = expectRecord(value, 'WorkflowScheduleListResult');
  const nextCursor = record.nextCursor ?? record.NextCursor;
  const totalCount = record.totalCount ?? record.TotalCount;
  return {
    items: expectArray(
      record.items ?? record.Items,
      'WorkflowScheduleListResult.items',
      decodeWorkflowScheduleSummary,
    ),
    nextCursor:
      nextCursor === null || nextCursor === undefined
        ? null
        : readString(
            { value: nextCursor },
            'value',
            'WorkflowScheduleListResult.nextCursor',
          ),
    totalCount:
      totalCount === null || totalCount === undefined
        ? null
        : readNumber(
            { value: totalCount },
            'value',
            'WorkflowScheduleListResult.totalCount',
          ),
  };
}

function decodeWorkflowSchedulePreview(
  value: unknown,
): WorkflowSchedulePreview {
  const record = expectRecord(value, 'WorkflowSchedulePreview');
  return {
    cronExpression: readString(
      record,
      ['cronExpression', 'CronExpression'],
      'WorkflowSchedulePreview.cronExpression',
    ),
    timezone: readString(
      record,
      ['timezone', 'Timezone'],
      'WorkflowSchedulePreview.timezone',
    ),
    nextFireTimes: expectArray(
      record.nextFireTimes ?? record.NextFireTimes,
      'WorkflowSchedulePreview.nextFireTimes',
      (entry, label) => {
        if (typeof entry === 'string') return entry;
        if (entry instanceof Date) return entry.toISOString();
        throw new Error(`${label} must be a string.`);
      },
    ),
  };
}

function decodeWorkflowScheduleReceipt(
  value: unknown,
): WorkflowScheduleMutationReceipt {
  const record = expectRecord(value, 'WorkflowScheduleMutationReceipt');
  return {
    scheduleId: readString(
      record,
      ['scheduleId', 'ScheduleId'],
      'WorkflowScheduleMutationReceipt.scheduleId',
    ),
    accepted: readBoolean(
      record,
      ['accepted', 'Accepted'],
      'WorkflowScheduleMutationReceipt.accepted',
    ),
  };
}

function decodeWorkflowScheduleRunNowReceipt(
  value: unknown,
): WorkflowScheduleRunNowReceipt {
  const record = expectRecord(value, 'WorkflowScheduleRunNowReceipt');
  return {
    ...decodeWorkflowScheduleReceipt(value),
    scheduledFireAt: readDateTimeString(
      record,
      ['scheduledFireAt', 'ScheduledFireAt'],
      'WorkflowScheduleRunNowReceipt.scheduledFireAt',
    ),
  };
}

function requireIdentifier(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) throw new Error(`${label} is required.`);
  return encodeURIComponent(normalized);
}

function route(scopeId: string, workflowId: string): string {
  return `/api/scopes/${requireIdentifier(scopeId, 'scopeId')}/workflows/${requireIdentifier(workflowId, 'workflowId')}/schedules`;
}

function encodeConfiguration(input: WorkflowScheduleConfigurationInput) {
  const displayName = input.displayName.trim();
  const cronExpression = input.cronExpression.trim();
  const timezone = input.timezone.trim();
  if (!displayName) throw new Error('Schedule displayName is required.');
  if (!cronExpression) throw new Error('Schedule cronExpression is required.');
  if (!timezone) throw new Error('Schedule timezone is required.');

  return {
    ...(input.scheduleId?.trim()
      ? { scheduleId: input.scheduleId.trim() }
      : {}),
    displayName,
    cronExpression,
    timezone,
    enabled: input.enabled,
    prompt: input.prompt?.trim() ?? '',
    headers: input.headers ?? {},
  };
}

function encodePreview(input: WorkflowSchedulePreviewInput) {
  const cronExpression = input.cronExpression.trim();
  const timezone = input.timezone.trim();
  if (!cronExpression) throw new Error('Schedule cronExpression is required.');
  if (!timezone) throw new Error('Schedule timezone is required.');
  return {
    cronExpression,
    timezone,
    count: input.count ?? 5,
    ...(input.fromUtc?.trim() ? { fromUtc: input.fromUtc.trim() } : {}),
  };
}

function mutationBody(reason?: string) {
  const normalizedReason = reason?.trim() ?? '';
  return normalizedReason ? { reason: normalizedReason } : {};
}

export const workflowScheduleApi: WorkflowScheduleApi = {
  list(scopeId, workflowId, query) {
    return requestJson(
      withQuery(route(scopeId, workflowId), {
        cursor: query?.cursor,
        includeTotalCount: query?.includeTotalCount,
        take: query?.take,
      }),
      decodeWorkflowScheduleList,
    );
  },

  get(scopeId, workflowId, scheduleId) {
    return requestJson(
      `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}`,
      decodeWorkflowScheduleDetail,
    );
  },

  preview(scopeId, workflowId, input) {
    return requestJson(
      `${route(scopeId, workflowId)}/preview`,
      decodeWorkflowSchedulePreview,
      {
        method: 'POST',
        ...jsonBody(encodePreview(input)),
      },
    );
  },

  create(scopeId, workflowId, input) {
    return requestJson(
      route(scopeId, workflowId),
      decodeWorkflowScheduleReceipt,
      {
        method: 'POST',
        ...jsonBody(encodeConfiguration(input)),
      },
    );
  },

  update(scopeId, workflowId, scheduleId, input) {
    return requestJson(
      `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}`,
      decodeWorkflowScheduleReceipt,
      {
        method: 'PUT',
        ...jsonBody(encodeConfiguration({ ...input, scheduleId })),
      },
    );
  },

  enable(scopeId, workflowId, scheduleId, reason) {
    return requestJson(
      `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}:enable`,
      decodeWorkflowScheduleReceipt,
      { method: 'POST', ...jsonBody(mutationBody(reason)) },
    );
  },

  disable(scopeId, workflowId, scheduleId, reason) {
    return requestJson(
      `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}:disable`,
      decodeWorkflowScheduleReceipt,
      { method: 'POST', ...jsonBody(mutationBody(reason)) },
    );
  },

  runNow(scopeId, workflowId, scheduleId) {
    return requestJson(
      `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}:run-now`,
      decodeWorkflowScheduleRunNowReceipt,
      { method: 'POST', ...jsonBody({}) },
    );
  },

  delete(scopeId, workflowId, scheduleId, reason) {
    const normalizedReason = reason?.trim() ?? '';
    return requestJson(
      withQuery(
        `${route(scopeId, workflowId)}/${requireIdentifier(scheduleId, 'scheduleId')}`,
        normalizedReason ? { reason: normalizedReason } : undefined,
      ),
      decodeWorkflowScheduleReceipt,
      { method: 'DELETE', ...jsonBody(mutationBody(reason)) },
    );
  },
};
