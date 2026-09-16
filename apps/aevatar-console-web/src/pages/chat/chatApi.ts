import type { AGUIEvent } from '@aevatar-react-sdk/types';
import { normalizeBackendSseFrame } from '@/shared/agui/sseFrameNormalizer';
import { readResponseErrorDetails } from '@/shared/api/http/error';
import { authFetch } from '@/shared/auth/fetch';
import type { ChatStudioTarget, ChatUsageSummary } from './chatTypes';

type JsonRecord = Record<string, unknown>;

export type ChatStreamFrame = {
  event: AGUIEvent | null;
  raw: unknown;
};

export type ChatTextCommand = {
  readonly type: 'text';
  readonly clientRequestId: string;
  readonly conversationId?: string;
  readonly prompt: string;
};

export type ChatInputAnswer =
  | { readonly freeText: string }
  | { readonly selectedOptionIds: readonly string[] };

export type ChatActionResource =
  | { readonly userService: { readonly userServiceId: string } }
  | { readonly key: { readonly keyId: string } }
  | { readonly node: { readonly nodeId: string } }
  | { readonly serviceAccount: { readonly serviceAccountId: string } }
  | { readonly developerApp: { readonly clientId: string } }
  | { readonly device: { readonly deviceId: string } };

export type ChatCommand =
  | ChatTextCommand
  | {
      readonly type: 'input.resolve';
      readonly conversationId: string;
      readonly requestId: string;
      readonly clientRequestId: string;
      readonly answer: ChatInputAnswer;
      readonly expectedStateVersion: number;
    }
  | {
      readonly type: 'task.stop';
      readonly conversationId: string;
      readonly turnId: string;
      readonly stopRequestId: string;
      readonly clientRequestId: string;
      readonly expectedStateVersion: number;
    }
  | {
      readonly type: 'task.steer';
      readonly conversationId: string;
      readonly turnId: string;
      readonly steeringId: string;
      readonly clientRequestId: string;
      readonly instruction: string;
      readonly expectedStateVersion: number;
    }
  | {
      readonly type: 'step.retry';
      readonly conversationId: string;
      readonly turnId: string;
      readonly taskId: string;
      readonly stepId: string;
      readonly retryRequestId: string;
      readonly clientRequestId: string;
      readonly expectedOperationGeneration: number;
      readonly expectedStateVersion: number;
    }
  | {
      readonly type: 'step.skip';
      readonly conversationId: string;
      readonly turnId: string;
      readonly taskId: string;
      readonly stepId: string;
      readonly skipRequestId: string;
      readonly clientRequestId: string;
      readonly expectedOperationGeneration: number;
      readonly expectedStateVersion: number;
    }
  | {
      readonly type: 'action.continue';
      readonly conversationId: string;
      readonly originTurnId: string;
      readonly clientRequestId: string;
      readonly actions: readonly {
        readonly actionRequestId: string;
        readonly originTurnId: string;
        readonly disposition:
          | 'completed'
          | 'declined'
          | 'failed'
          | 'cancelled'
          | 'expired';
        readonly resource?: ChatActionResource;
      }[];
    };

export class ChatApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'ChatApiError';
    this.code = code;
    this.status = status;
  }
}

export async function sendChatCommand(
  command: ChatCommand,
  signal: AbortSignal,
): Promise<Response> {
  const clientRequestId = command.clientRequestId.trim();
  const body = Object.fromEntries(
    Object.entries(command).map(([key, value]) => [
      key,
      typeof value === 'string' ? value.trim() : value,
    ]),
  );
  const response = await authFetch('/api/chat', {
    body: JSON.stringify(compactObject(body)),
    headers: {
      Accept: 'text/event-stream',
      'Content-Type': 'application/json',
      'Idempotency-Key': clientRequestId,
    },
    method: 'POST',
    signal,
  });

  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw new ChatApiError(details.message, details.status, details.code);
  }

  return response;
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined),
  ) as T;
}

function asRecord(value: unknown): JsonRecord | undefined {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? (value as JsonRecord)
    : undefined;
}

function readString(record: JsonRecord | undefined, ...keys: string[]): string {
  if (!record) return '';
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'string' && value.trim()) return value.trim();
  }
  return '';
}

function readNumber(
  record: JsonRecord | undefined,
  ...keys: string[]
): number | undefined {
  if (!record) return undefined;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (typeof value === 'string' && value.trim()) {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) return parsed;
    }
  }
  return undefined;
}

function normalizeUsage(
  record: JsonRecord | undefined,
): ChatUsageSummary | null {
  if (!record) return null;
  const usage: ChatUsageSummary = {
    completionTokens: readNumber(
      record,
      'completionTokens',
      'completion_tokens',
    ),
    cost: readNumber(record, 'cost'),
    latencyMs: readNumber(record, 'latencyMs', 'latency_ms'),
    model: readString(record, 'model') || undefined,
    promptTokens: readNumber(record, 'promptTokens', 'prompt_tokens'),
    totalTokens: readNumber(record, 'totalTokens', 'total_tokens'),
  };
  return Object.values(usage).some(
    (value) => value !== undefined && value !== '',
  )
    ? usage
    : null;
}

function mergeUsage(
  current: ChatUsageSummary | undefined,
  next: ChatUsageSummary | null,
): ChatUsageSummary | undefined {
  return next
    ? {
        ...current,
        ...Object.fromEntries(
          Object.entries(next).filter(([, value]) => value !== undefined),
        ),
      }
    : current;
}

function normalizeTarget(
  record: JsonRecord | undefined,
): ChatStudioTarget | null {
  if (!record) return null;
  const target: ChatStudioTarget = {
    memberId: readString(record, 'memberId', 'member_id') || undefined,
    runId:
      readString(record, 'runId', 'run_id', 'actorId', 'actor_id') || undefined,
    scopeId: readString(record, 'scopeId', 'scope_id') || undefined,
    studioUrl: readString(record, 'studioUrl', 'studio_url') || undefined,
    teamId: readString(record, 'teamId', 'team_id') || undefined,
    workflowId: readString(record, 'workflowId', 'workflow_id') || undefined,
  };
  return Object.values(target).some(Boolean) ? target : null;
}

function mergeTarget(
  current: ChatStudioTarget | undefined,
  next: ChatStudioTarget | null,
): ChatStudioTarget | undefined {
  return next
    ? {
        ...current,
        ...Object.fromEntries(
          Object.entries(next).filter(([, value]) => Boolean(value)),
        ),
      }
    : current;
}

function unpackStruct(value: unknown): JsonRecord | undefined {
  const record = asRecord(value);
  if (!record) return undefined;
  const fields = asRecord(record.fields);
  if (!fields) return record;

  const unpacked: JsonRecord = {};
  for (const [key, fieldValue] of Object.entries(fields)) {
    const field = asRecord(fieldValue);
    if (!field) continue;
    if (typeof field.stringValue === 'string') {
      unpacked[key] = field.stringValue;
    } else if (typeof field.numberValue === 'number') {
      unpacked[key] = field.numberValue;
    } else if (typeof field.boolValue === 'boolean') {
      unpacked[key] = field.boolValue;
    } else if (field.structValue) {
      unpacked[key] = unpackStruct(field.structValue);
    }
  }
  return unpacked;
}

export function extractChatStreamArtifacts(frames: readonly unknown[]): {
  target?: ChatStudioTarget;
  usage?: ChatUsageSummary;
} {
  let target: ChatStudioTarget | undefined;
  let usage: ChatUsageSummary | undefined;

  for (const raw of frames) {
    const frame = asRecord(raw);
    if (!frame) continue;
    usage = mergeUsage(usage, normalizeUsage(asRecord(frame.usage)));
    target = mergeTarget(target, normalizeTarget(frame));

    const result = asRecord(asRecord(frame.runFinished)?.result);
    usage = mergeUsage(usage, normalizeUsage(asRecord(result?.usage)));
    target = mergeTarget(target, normalizeTarget(result));

    const customPayload = unpackStruct(asRecord(frame.custom)?.payload);
    usage = mergeUsage(usage, normalizeUsage(asRecord(customPayload?.usage)));
    target = mergeTarget(target, normalizeTarget(customPayload));

    const rawObserved = asRecord(customPayload?.payload);
    usage = mergeUsage(usage, normalizeUsage(asRecord(rawObserved?.usage)));
    target = mergeTarget(target, normalizeTarget(rawObserved));
  }

  return compactObject({ target, usage });
}

export async function* readChatStreamFrames(
  response: Response,
  options?: { signal?: AbortSignal },
): AsyncGenerator<ChatStreamFrame, void, undefined> {
  const body = response.body;
  if (!body) throw new Error('Chat response has no readable stream.');

  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  const dataLines: string[] = [];

  try {
    while (!options?.signal?.aborted) {
      const { done, value } = await reader.read();
      buffer += done ? '\n' : decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = done ? '' : (lines.pop() ?? '');

      for (const line of lines) {
        const normalizedLine = line.endsWith('\r') ? line.slice(0, -1) : line;
        if (normalizedLine === '') {
          if (dataLines.length > 0) {
            const data = dataLines.splice(0).join('\n').trim();
            if (data && data !== '[DONE]') {
              try {
                const raw = JSON.parse(data);
                yield { event: normalizeBackendSseFrame(raw), raw };
              } catch {
                // A malformed frame does not invalidate the rest of the stream.
              }
            }
          }
          continue;
        }
        if (normalizedLine.startsWith('data:')) {
          const payload = normalizedLine.slice(5);
          dataLines.push(payload.startsWith(' ') ? payload.slice(1) : payload);
        }
      }
      if (done) break;
    }
  } finally {
    reader.releaseLock();
  }
}
