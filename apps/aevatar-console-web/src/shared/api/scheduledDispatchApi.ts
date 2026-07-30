import { jsonBody, requestJson, withQuery } from "./http/client";
import { authFetch } from "@/shared/auth/fetch";
import { readResponseErrorDetails } from "./http/error";
import {
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalArray,
  readString,
  readStringRecord,
} from "./http/decoders";
import type { ServiceIdentity } from "@/shared/models/services";
import {
  encodeChatRequestEventBase64,
  getChatEndpointId,
  getChatRequestEventTypeUrl,
} from "@/shared/runs/protobufPayload";

export type ScheduledDispatchTargetKind = "envelope" | "service_invocation";
export type ScheduledDispatchScheduleKind = "generic" | "workflow";
export type ScheduledDispatchOwner = {
  readonly kind: "studio_member_automation";
  readonly scopeId: string;
  readonly teamId: string;
  readonly memberId: string;
};
export const scheduledWorkflowPromptMaxLength = 4_000;

export type ScheduledWorkflowChatTargetInput = {
  readonly identity: ServiceIdentity;
  readonly prompt?: string;
  readonly sessionId?: string;
  readonly revisionId?: string;
};

export type ScheduledDispatchConfigurationInput = {
  readonly scheduleId?: string;
  readonly displayName?: string;
  readonly cronExpression: string;
  readonly timezone?: string;
  readonly enabled?: boolean;
  readonly headers?: Readonly<Record<string, string>>;
  readonly owner?: ScheduledDispatchOwner;
  readonly workflowChatTarget: ScheduledWorkflowChatTargetInput;
};

export type ScheduledDispatchPreviewInput = {
  readonly cronExpression: string;
  readonly timezone?: string;
  readonly count?: number;
  readonly fromUtc?: string;
};

export type ScheduledDispatchSummary = {
  readonly scheduleId: string;
  readonly displayName: string;
  readonly targetKind: ScheduledDispatchTargetKind;
  readonly targetActorId: string;
  readonly payloadTypeUrl: string;
  readonly serviceKey: string;
  readonly serviceId: string;
  readonly serviceEndpointId: string;
  readonly prompt: string;
  readonly cronExpression: string;
  readonly timezone: string;
  readonly enabled: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly nextFireAt: string | null;
  readonly lastFireAt: string | null;
  readonly lastTargetActorId: string;
  readonly lastCommandId: string;
  readonly lastCorrelationId: string;
  readonly lastError: string;
  readonly fireCount: number;
  readonly failureCount: number;
  readonly headers: Record<string, string>;
  readonly scheduleActorId: string;
  readonly scheduleKind: ScheduledDispatchScheduleKind;
  readonly deleted: boolean;
};

export type ScheduledDispatchFireRecord = {
  readonly scheduledFireAt: string;
  readonly completedAt: string;
  readonly idempotencyKey: string;
  readonly targetActorId: string;
  readonly commandId: string;
  readonly correlationId: string;
  readonly error: string;
  readonly manual: boolean;
};

export type ScheduledDispatchDetail = {
  readonly schedule: ScheduledDispatchSummary;
  readonly recentFires: readonly ScheduledDispatchFireRecord[];
};

export type ScheduledDispatchPreview = {
  readonly cronExpression: string;
  readonly timezone: string;
  readonly nextFireTimes: readonly string[];
};

export type ScheduledDispatchMutationReceipt = {
  readonly scheduleId: string;
  readonly scheduleActorId: string;
  readonly accepted: boolean;
  readonly commandId: string;
  readonly correlationId: string;
  readonly ackedAt: string;
  readonly ackStage: string;
};

export type ScheduledDispatchRunNowReceipt = ScheduledDispatchMutationReceipt & {
  readonly scheduledFireAt: string;
  readonly idempotencyKey: string;
};

export type ScheduledDispatchListResult = {
  readonly items: readonly ScheduledDispatchSummary[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
};

export type ScheduledDispatchListQuery = {
  readonly cursor?: string;
  readonly includeTotalCount?: boolean;
  readonly owner?: ScheduledDispatchOwner;
  readonly take?: number;
};

const missingOwnerBindingMessage =
  "NyxID binding was not found for the scheduled subject.";
const missingOwnerBindingCodePattern =
  /nyxid.*binding.*not.*found|missing.*owner.*binding|owner.*binding.*not.*found/i;
const bindingReadModelRetryDelaysMs = [400, 900] as const;
let waitForBindingReadModelRetry = (delayMs: number): Promise<void> =>
  new Promise((resolve) => {
    setTimeout(resolve, delayMs);
  });

function trimOptional(value: string | null | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function readDateTimeString(
  record: Record<string, unknown>,
  keys: string[],
  label: string,
): string {
  const value = record[keys[0]] ?? record[keys[1]];
  if (value instanceof Date) {
    return value.toISOString();
  }
  if (typeof value === "string") {
    return value;
  }

  throw new Error(`${label} must be a string.`);
}

function readNullableDateTimeString(
  record: Record<string, unknown>,
  keys: string[],
): string | null {
  const value = record[keys[0]] ?? record[keys[1]];
  if (value === null || value === undefined) {
    return null;
  }
  if (value instanceof Date) {
    return value.toISOString();
  }
  if (typeof value === "string") {
    return value;
  }

  throw new Error(`${keys[0]} must be a string or null.`);
}

function readNullableNumber(
  record: Record<string, unknown>,
  keys: string[],
  label: string,
): number | null {
  for (const key of keys) {
    if (!(key in record)) {
      continue;
    }

    const value = record[key];
    if (value === null || value === undefined) {
      return null;
    }

    if (typeof value === "number" && !Number.isNaN(value)) {
      return value;
    }

    throw new Error(`${label} must be a number or null.`);
  }

  return null;
}

function normalizeTargetKind(value: string | number): ScheduledDispatchTargetKind {
  const normalized = String(value).trim().toLowerCase();
  switch (normalized) {
    case "1":
    case "serviceinvocation":
    case "service_invocation":
      return "service_invocation";
    default:
      return "envelope";
  }
}

function normalizeScheduleKind(value: string | number): ScheduledDispatchScheduleKind {
  const normalized = String(value).trim().toLowerCase();
  switch (normalized) {
    case "1":
    case "workflow":
      return "workflow";
    default:
      return "generic";
  }
}

function encodeOwner(
  owner: ScheduledDispatchOwner | undefined,
): ScheduledDispatchOwner | undefined {
  if (!owner) {
    return undefined;
  }

  if (owner.kind !== "studio_member_automation") {
    throw new Error(`Unsupported scheduled dispatch owner kind '${owner.kind}'.`);
  }

  const scopeId = owner.scopeId.trim();
  const teamId = owner.teamId.trim();
  const memberId = owner.memberId.trim();
  if (!scopeId) {
    throw new Error("Schedule owner scopeId is required.");
  }
  if (!teamId) {
    throw new Error("Schedule owner teamId is required.");
  }
  if (!memberId) {
    throw new Error("Schedule owner memberId is required.");
  }

  return {
    kind: "studio_member_automation",
    scopeId,
    teamId,
    memberId,
  };
}

export function encodeScheduledDispatchOwnerQuery(
  owner: ScheduledDispatchOwner | undefined,
) {
  const normalizedOwner = encodeOwner(owner);
  return normalizedOwner
    ? {
        ownerKind: normalizedOwner.kind,
        ownerScopeId: normalizedOwner.scopeId,
        ownerTeamId: normalizedOwner.teamId,
        ownerMemberId: normalizedOwner.memberId,
      }
    : {};
}

function readTargetKind(
  record: Record<string, unknown>,
  label: string,
): ScheduledDispatchTargetKind {
  const value = record.targetKind ?? record.TargetKind;
  if (typeof value !== "string" && typeof value !== "number") {
    throw new Error(`${label}.targetKind must be a string or number.`);
  }

  return normalizeTargetKind(value);
}

function readScheduleKind(
  record: Record<string, unknown>,
): ScheduledDispatchScheduleKind {
  const value = record.scheduleKind ?? record.ScheduleKind ?? "generic";
  if (typeof value !== "string" && typeof value !== "number") {
    return "generic";
  }

  return normalizeScheduleKind(value);
}

export function decodeScheduledDispatchSummary(
  value: unknown,
  label = "ScheduledDispatchSummary",
): ScheduledDispatchSummary {
  const record = expectRecord(value, label);
  return {
    scheduleId: readString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    targetKind: readTargetKind(record, label),
    targetActorId: readString(record, ["targetActorId", "TargetActorId"], `${label}.targetActorId`),
    payloadTypeUrl: readString(record, ["payloadTypeUrl", "PayloadTypeUrl"], `${label}.payloadTypeUrl`),
    serviceKey: readString(record, ["serviceKey", "ServiceKey"], `${label}.serviceKey`),
    serviceId: readString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
    serviceEndpointId: readString(record, ["serviceEndpointId", "ServiceEndpointId"], `${label}.serviceEndpointId`),
    prompt: readNullableString(record, ["prompt", "Prompt"], `${label}.prompt`) ?? "",
    cronExpression: readString(record, ["cronExpression", "CronExpression"], `${label}.cronExpression`),
    timezone: readString(record, ["timezone", "Timezone"], `${label}.timezone`),
    enabled: readBoolean(record, ["enabled", "Enabled"], `${label}.enabled`),
    createdAt: readDateTimeString(record, ["createdAt", "CreatedAt"], `${label}.createdAt`),
    updatedAt: readDateTimeString(record, ["updatedAt", "UpdatedAt"], `${label}.updatedAt`),
    nextFireAt: readNullableDateTimeString(record, ["nextFireAt", "NextFireAt"]),
    lastFireAt: readNullableDateTimeString(record, ["lastFireAt", "LastFireAt"]),
    lastTargetActorId: readString(record, ["lastTargetActorId", "LastTargetActorId"], `${label}.lastTargetActorId`),
    lastCommandId: readString(record, ["lastCommandId", "LastCommandId"], `${label}.lastCommandId`),
    lastCorrelationId: readString(record, ["lastCorrelationId", "LastCorrelationId"], `${label}.lastCorrelationId`),
    lastError: readString(record, ["lastError", "LastError"], `${label}.lastError`),
    fireCount: readNumber(record, ["fireCount", "FireCount"], `${label}.fireCount`),
    failureCount: readNumber(record, ["failureCount", "FailureCount"], `${label}.failureCount`),
    headers: readStringRecord(record, ["headers", "Headers"], `${label}.headers`),
    scheduleActorId: readString(record, ["scheduleActorId", "ScheduleActorId"], `${label}.scheduleActorId`),
    scheduleKind: readScheduleKind(record),
    deleted: readBoolean({ deleted: record.deleted ?? record.Deleted ?? false }, "deleted", `${label}.deleted`),
  };
}

function decodeScheduledDispatchFireRecord(
  value: unknown,
  label = "ScheduledDispatchFireRecord",
): ScheduledDispatchFireRecord {
  const record = expectRecord(value, label);
  return {
    scheduledFireAt: readDateTimeString(record, ["scheduledFireAt", "ScheduledFireAt"], `${label}.scheduledFireAt`),
    completedAt: readDateTimeString(record, ["completedAt", "CompletedAt"], `${label}.completedAt`),
    idempotencyKey: readString(record, ["idempotencyKey", "IdempotencyKey"], `${label}.idempotencyKey`),
    targetActorId: readString(record, ["targetActorId", "TargetActorId"], `${label}.targetActorId`),
    commandId: readString(record, ["commandId", "CommandId"], `${label}.commandId`),
    correlationId: readString(record, ["correlationId", "CorrelationId"], `${label}.correlationId`),
    error: readString(record, ["error", "Error"], `${label}.error`),
    manual: readBoolean(record, ["manual", "Manual"], `${label}.manual`),
  };
}

function decodeScheduledDispatchDetail(
  value: unknown,
  label = "ScheduledDispatchDetail",
): ScheduledDispatchDetail {
  const record = expectRecord(value, label);
  return {
    schedule: decodeScheduledDispatchSummary(record.schedule ?? record.Schedule, `${label}.schedule`),
    recentFires: readOptionalArray(
      record,
      ["recentFires", "RecentFires"],
      `${label}.recentFires`,
      decodeScheduledDispatchFireRecord,
    ),
  };
}

function decodeScheduledDispatchPreview(
  value: unknown,
  label = "ScheduledDispatchPreview",
): ScheduledDispatchPreview {
  const record = expectRecord(value, label);
  return {
    cronExpression: readString(record, ["cronExpression", "CronExpression"], `${label}.cronExpression`),
    timezone: readString(record, ["timezone", "Timezone"], `${label}.timezone`),
    nextFireTimes: expectArray(
      record.nextFireTimes ?? record.NextFireTimes,
      `${label}.nextFireTimes`,
      (entry, entryLabel) => {
        if (typeof entry === "string") {
          return entry;
        }
        if (entry instanceof Date) {
          return entry.toISOString();
        }
        throw new Error(`${entryLabel} must be a string.`);
      },
    ),
  };
}

function decodeScheduledDispatchMutationReceipt(
  value: unknown,
  label = "ScheduledDispatchMutationReceipt",
): ScheduledDispatchMutationReceipt {
  const record = expectRecord(value, label);
  return {
    scheduleId: readString(record, ["scheduleId", "ScheduleId"], `${label}.scheduleId`),
    scheduleActorId: readString(record, ["scheduleActorId", "ScheduleActorId"], `${label}.scheduleActorId`),
    accepted: readBoolean(record, ["accepted", "Accepted"], `${label}.accepted`),
    commandId: readString(record, ["commandId", "CommandId"], `${label}.commandId`),
    correlationId: readString(record, ["correlationId", "CorrelationId"], `${label}.correlationId`),
    ackedAt: readDateTimeString(record, ["ackedAt", "AckedAt"], `${label}.ackedAt`),
    ackStage: readString(record, ["ackStage", "AckStage"], `${label}.ackStage`),
  };
}

function decodeScheduledDispatchRunNowReceipt(
  value: unknown,
  label = "ScheduledDispatchRunNowReceipt",
): ScheduledDispatchRunNowReceipt {
  const record = expectRecord(value, label);
  return {
    ...decodeScheduledDispatchMutationReceipt(value, label),
    scheduledFireAt: readDateTimeString(record, ["scheduledFireAt", "ScheduledFireAt"], `${label}.scheduledFireAt`),
    idempotencyKey: readString(record, ["idempotencyKey", "IdempotencyKey"], `${label}.idempotencyKey`),
  };
}

function decodeScheduledDispatchListResult(
  value: unknown,
  label = "ScheduledDispatchListResult",
): ScheduledDispatchListResult {
  const record = expectRecord(value, label);
  return {
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeScheduledDispatchSummary,
    ),
    nextCursor: readNullableString(record, ["nextCursor", "NextCursor"], `${label}.nextCursor`),
    totalCount: readNullableNumber(
      record,
      ["totalCount", "TotalCount"],
      `${label}.totalCount`,
    ),
  };
}

function encodeConfiguration(input: ScheduledDispatchConfigurationInput) {
  const identity = {
    tenantId: input.workflowChatTarget.identity.tenantId.trim(),
    appId: input.workflowChatTarget.identity.appId.trim(),
    namespace: input.workflowChatTarget.identity.namespace.trim(),
    serviceId: input.workflowChatTarget.identity.serviceId.trim(),
  };
  const prompt = input.workflowChatTarget.prompt?.trim() ?? "";
  if (prompt.length > scheduledWorkflowPromptMaxLength) {
    throw new Error(
      `Recurring prompt must be ${scheduledWorkflowPromptMaxLength} characters or fewer.`,
    );
  }
  const revisionId = trimOptional(input.workflowChatTarget.revisionId);
  const owner = encodeOwner(input.owner);
  const chatRequest = {
    prompt,
    sessionId: trimOptional(input.workflowChatTarget.sessionId),
    scopeId: identity.tenantId,
  };

  return {
    scheduleId: trimOptional(input.scheduleId),
    displayName: trimOptional(input.displayName) ?? "",
    cronExpression: input.cronExpression.trim(),
    timezone: trimOptional(input.timezone),
    enabled: input.enabled ?? true,
    headers: input.headers ?? {},
    ...(owner ? { owner } : {}),
    scheduleKind: "workflow",
    serviceInvocation: {
      identity,
      endpointId: getChatEndpointId(),
      payloadTypeUrl: getChatRequestEventTypeUrl(),
      payloadBase64: encodeChatRequestEventBase64(chatRequest),
      ...(revisionId ? { revisionId } : {}),
    },
  };
}

function encodePreview(input: ScheduledDispatchPreviewInput) {
  return {
    cronExpression: input.cronExpression.trim(),
    timezone: trimOptional(input.timezone),
    count: input.count ?? 5,
    fromUtc: trimOptional(input.fromUtc),
  };
}

export class ScheduledDispatchApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = "ScheduledDispatchApiError";
    this.code = code;
    this.status = status;
  }
}

export function configureScheduledDispatchRetryDelay(
  waitForRetry: (delayMs: number) => Promise<void>,
): () => void {
  const previous = waitForBindingReadModelRetry;
  waitForBindingReadModelRetry = waitForRetry;
  return () => {
    waitForBindingReadModelRetry = previous;
  };
}

function isMissingOwnerBindingError(error: unknown): boolean {
  if (!(error instanceof ScheduledDispatchApiError)) {
    return false;
  }

  return (
    error.message.includes(missingOwnerBindingMessage) ||
    missingOwnerBindingCodePattern.test(error.message) ||
    (Boolean(error.code) && missingOwnerBindingCodePattern.test(error.code ?? ""))
  );
}

async function requestScheduledDispatchMutation<T>(
  input: string,
  decoder: (value: unknown, label?: string) => T,
  init: RequestInit,
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new ScheduledDispatchApiError(
      details.message,
      details.status,
      details.code,
    );
  }

  return decoder(await response.json());
}

async function requestScheduleMutationWithBindingRetry<T>(
  operation: () => Promise<T>,
): Promise<T> {
  for (let attemptIndex = 0; ; attemptIndex += 1) {
    try {
      return await operation();
    } catch (error) {
      const delayMs = bindingReadModelRetryDelaysMs[attemptIndex];
      if (!isMissingOwnerBindingError(error) || delayMs === undefined) {
        throw error;
      }

      await waitForBindingReadModelRetry(delayMs);
    }
  }
}

function listScheduledDispatches(
  query?: ScheduledDispatchListQuery,
): Promise<ScheduledDispatchListResult> {
  return requestJson(
    withQuery("/api/schedules", {
      ...encodeScheduledDispatchOwnerQuery(query?.owner),
      cursor: query?.cursor,
      includeTotalCount: query?.includeTotalCount,
      take: query?.take,
    }),
    decodeScheduledDispatchListResult,
  );
}

async function listAllScheduledDispatches(
  query?: ScheduledDispatchListQuery,
): Promise<ScheduledDispatchListResult> {
  const items: ScheduledDispatchSummary[] = [];
  const seenCursors = new Set<string>();
  let cursor = query?.cursor;
  let totalCount: number | null = null;

  while (true) {
    if (cursor) {
      if (seenCursors.has(cursor)) {
        throw new Error("Scheduled dispatch list returned a repeated cursor.");
      }

      seenCursors.add(cursor);
    }

    const result = await listScheduledDispatches({
      ...query,
      cursor,
    });
    items.push(...result.items);
    if (result.totalCount !== null) {
      totalCount = result.totalCount;
    }

    if (!result.nextCursor) {
      return {
        items,
        nextCursor: null,
        totalCount,
      };
    }

    cursor = result.nextCursor;
  }
}

export const scheduledDispatchApi = {
  list: listScheduledDispatches,

  listAll: listAllScheduledDispatches,

  get(
    scheduleId: string,
    owner?: ScheduledDispatchOwner,
  ): Promise<ScheduledDispatchDetail> {
    return requestJson(
      withQuery(`/api/schedules/${encodeURIComponent(scheduleId.trim())}`, {
        ...encodeScheduledDispatchOwnerQuery(owner),
      }),
      decodeScheduledDispatchDetail,
    );
  },

  create(
    input: ScheduledDispatchConfigurationInput,
  ): Promise<ScheduledDispatchMutationReceipt> {
    const configuration = encodeConfiguration(input);
    return requestScheduleMutationWithBindingRetry(() =>
      requestScheduledDispatchMutation(
        "/api/schedules",
        decodeScheduledDispatchMutationReceipt,
        {
          method: "POST",
          ...jsonBody(configuration),
        },
      ),
    );
  },

  update(
    scheduleId: string,
    input: ScheduledDispatchConfigurationInput,
  ): Promise<ScheduledDispatchMutationReceipt> {
    const configuration = encodeConfiguration(input);
    return requestScheduleMutationWithBindingRetry(() =>
      requestScheduledDispatchMutation(
        `/api/schedules/${encodeURIComponent(scheduleId.trim())}`,
        decodeScheduledDispatchMutationReceipt,
        {
          method: "PUT",
          ...jsonBody(configuration),
        },
      ),
    );
  },

  enable(
    scheduleId: string,
    reason = "",
    owner?: ScheduledDispatchOwner,
  ): Promise<ScheduledDispatchMutationReceipt> {
    const normalizedOwner = encodeOwner(owner);
    return requestJson(
      `/api/schedules/${encodeURIComponent(scheduleId.trim())}:enable`,
      decodeScheduledDispatchMutationReceipt,
      {
        method: "POST",
        ...jsonBody({
          ...(normalizedOwner ? { owner: normalizedOwner } : {}),
          reason,
        }),
      },
    );
  },

  disable(
    scheduleId: string,
    reason = "",
    owner?: ScheduledDispatchOwner,
  ): Promise<ScheduledDispatchMutationReceipt> {
    const normalizedOwner = encodeOwner(owner);
    return requestJson(
      `/api/schedules/${encodeURIComponent(scheduleId.trim())}:disable`,
      decodeScheduledDispatchMutationReceipt,
      {
        method: "POST",
        ...jsonBody({
          ...(normalizedOwner ? { owner: normalizedOwner } : {}),
          reason,
        }),
      },
    );
  },

  delete(
    scheduleId: string,
    reason = "",
    owner?: ScheduledDispatchOwner,
  ): Promise<ScheduledDispatchMutationReceipt> {
    const normalizedReason = reason.trim();
    const normalizedOwner = encodeOwner(owner);
    return requestJson(
      withQuery(`/api/schedules/${encodeURIComponent(scheduleId.trim())}`, {
        reason: normalizedReason,
      }),
      decodeScheduledDispatchMutationReceipt,
      {
        method: "DELETE",
        ...jsonBody({
          ...(normalizedOwner ? { owner: normalizedOwner } : {}),
          reason: normalizedReason,
        }),
      },
    );
  },

  preview(input: ScheduledDispatchPreviewInput): Promise<ScheduledDispatchPreview> {
    return requestJson("/api/schedules/preview", decodeScheduledDispatchPreview, {
      method: "POST",
      ...jsonBody(encodePreview(input)),
    });
  },

  runNow(
    scheduleId: string,
    owner?: ScheduledDispatchOwner,
  ): Promise<ScheduledDispatchRunNowReceipt> {
    const normalizedOwner = encodeOwner(owner);
    return requestJson(
      `/api/schedules/${encodeURIComponent(scheduleId.trim())}:run-now`,
      decodeScheduledDispatchRunNowReceipt,
      {
        method: "POST",
        ...jsonBody(normalizedOwner ? { owner: normalizedOwner } : {}),
      },
    );
  },
};

export function previewScheduledDispatch(
  input: ScheduledDispatchPreviewInput,
): Promise<ScheduledDispatchPreview> {
  return scheduledDispatchApi.preview(input);
}
