import { readResponseErrorDetails } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";
import type {
  ChatConversationDetail,
  ChatHistoryIndex,
  ConversationMeta,
  StoredChatMessage,
} from "./chatTypes";

type JsonRecord = Record<string, unknown>;

const JSON_HEADERS = { Accept: "application/json" };

export class ChatHistoryApiError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = "ChatHistoryApiError";
    this.code = code;
    this.status = status;
  }
}

export class ChatHistoryContractError extends Error {
  readonly code = "INVALID_CHAT_HISTORY_RESPONSE";
  readonly path: string;

  constructor(path: string, expectation: string) {
    super(`Invalid Chat History response at ${path}: expected ${expectation}.`);
    this.name = "ChatHistoryContractError";
    this.path = path;
  }
}

function failContract(path: string, expectation: string): never {
  throw new ChatHistoryContractError(path, expectation);
}

function asRecord(value: unknown, path: string): JsonRecord {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return failContract(path, "an object");
  }
  return value as JsonRecord;
}

function readString(record: JsonRecord, key: string, path: string): string {
  const value = record[key];
  return typeof value === "string"
    ? value
    : failContract(`${path}.${key}`, "a string");
}

function readNumber(record: JsonRecord, key: string, path: string): number {
  const value = record[key];
  return typeof value === "number" && Number.isFinite(value)
    ? value
    : failContract(`${path}.${key}`, "a finite number");
}

function readOptional<T>(
  record: JsonRecord,
  key: string,
  path: string,
  read: (value: unknown) => T | undefined,
  expectation: string
): T | undefined {
  const value = record[key];
  if (value === undefined) return undefined;
  const decoded = read(value);
  return decoded === undefined
    ? failContract(`${path}.${key}`, expectation)
    : decoded;
}

function readOptionalString(
  record: JsonRecord,
  key: string,
  path: string
): string | undefined {
  return readOptional(
    record,
    key,
    path,
    (value) => (typeof value === "string" ? value : undefined),
    "a string or omission"
  );
}

function readOptionalNullableString(
  record: JsonRecord,
  key: string,
  path: string
): string | null | undefined {
  if (!(key in record) || record[key] === undefined) return undefined;
  const value = record[key];
  return value === null || typeof value === "string"
    ? value
    : failContract(`${path}.${key}`, "a string, null, or omission");
}

function readOptionalNonNegativeInteger(
  record: JsonRecord,
  key: string,
  path: string
): number | undefined {
  return readOptional(
    record,
    key,
    path,
    (value) =>
      typeof value === "number" &&
      Number.isSafeInteger(value) &&
      value >= 0
        ? value
        : undefined,
    "a non-negative safe integer or omission"
  );
}

function assignOptional(
  target: Record<string, unknown>,
  key: string,
  value: unknown
): void {
  if (value !== undefined) target[key] = value;
}

function decodeConversationMeta(value: unknown, path: string): ConversationMeta {
  const record = asRecord(value, path);
  const messageCount = readNumber(record, "messageCount", path);
  if (!Number.isInteger(messageCount) || messageCount < 0) {
    return failContract(`${path}.messageCount`, "a non-negative integer");
  }

  const meta = {
    createdAt: readString(record, "createdAt", path),
    id: readString(record, "id", path),
    messageCount,
    title: readString(record, "title", path),
    updatedAt: readString(record, "updatedAt", path),
  } as ConversationMeta & Record<string, unknown>;

  for (const key of ["serviceId", "serviceKind"] as const) {
    assignOptional(meta, key, readOptionalString(record, key, path));
  }
  for (const key of [
    "activeStepSummary",
    "attentionKind",
    "attentionSince",
    "taskStatus",
  ] as const) {
    assignOptional(meta, key, readOptionalNullableString(record, key, path));
  }
  assignOptional(
    meta,
    "llmRoute",
    readOptionalNullableString(record, "llmRoute", path)
  );
  assignOptional(
    meta,
    "llmModel",
    readOptionalNullableString(record, "llmModel", path)
  );
  assignOptional(
    meta,
    "stateVersion",
    readOptionalNonNegativeInteger(record, "stateVersion", path)
  );
  return meta;
}

function decodeStoredChatMessage(
  value: unknown,
  path: string
): StoredChatMessage {
  const record = asRecord(value, path);
  const message = {
    content: readString(record, "content", path),
    id: readString(record, "id", path),
    role: readString(record, "role", path),
    status: readString(record, "status", path),
    timestamp: readNumber(record, "timestamp", path),
  } as StoredChatMessage & Record<string, unknown>;

  for (const key of [
    "error",
    "thinking",
    "authorId",
    "authorName",
    "turnId",
  ] as const) {
    assignOptional(
      message,
      key,
      readOptionalNullableString(record, key, path)
    );
  }
  return message;
}

export function decodeChatHistoryIndex(value: unknown): ChatHistoryIndex {
  const record = asRecord(value, "$index");
  if (!Array.isArray(record.conversations)) {
    return failContract("$index.conversations", "an array");
  }
  const nextCursor = readOptionalNullableString(record, "nextCursor", "$index");
  return {
    conversations: record.conversations.map((conversation, index) =>
      decodeConversationMeta(conversation, `$index.conversations[${index}]`)
    ),
    ...(nextCursor !== undefined ? { nextCursor } : {}),
  };
}

export function decodeChatConversationDetail(
  value: unknown
): ChatConversationDetail {
  const record = asRecord(value, "$conversation");
  const projectionStatus = readString(
    record,
    "projectionStatus",
    "$conversation"
  );
  if (projectionStatus !== "current" && projectionStatus !== "pending") {
    return failContract(
      "$conversation.projectionStatus",
      '"current" or "pending"'
    );
  }
  const stateVersion = readNumber(record, "stateVersion", "$conversation");
  if (!Number.isSafeInteger(stateVersion) || stateVersion < 0) {
    return failContract(
      "$conversation.stateVersion",
      "a non-negative safe integer"
    );
  }
  if (!Array.isArray(record.messages)) {
    return failContract("$conversation.messages", "an array");
  }
  return {
    messages: record.messages.map((message, index) =>
      decodeStoredChatMessage(message, `$conversation.messages[${index}]`)
    ),
    projectionStatus,
    stateVersion,
  };
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

function conversationPath(conversationId: string): string {
  return `/api/chat/conversations/${encodeSegment(conversationId)}`;
}

async function createApiError(response: Response): Promise<ChatHistoryApiError> {
  const details = await readResponseErrorDetails(response);
  return new ChatHistoryApiError(details.message, details.status, details.code);
}

async function requestJson<T>(
  path: string,
  decoder: (value: unknown) => T,
  signal?: AbortSignal,
  acceptNotFound = false
): Promise<T> {
  const response = await authFetch(path, {
    headers: JSON_HEADERS,
    method: "GET",
    ...(signal ? { signal } : {}),
  });
  if (!response.ok && !(acceptNotFound && response.status === 404)) {
    throw await createApiError(response);
  }
  try {
    return decoder(await response.json());
  } catch (error) {
    if (error instanceof ChatHistoryContractError) throw error;
    throw new ChatHistoryContractError("$response", "valid JSON");
  }
}

export type ChatStateCursor = {
  afterStateVersion?: number;
  turnId?: string;
};

export const chatHistoryApi = {
  async listConversationMetas(signal?: AbortSignal): Promise<ConversationMeta[]> {
    const conversations: ConversationMeta[] = [];
    const seenCursors = new Set<string>();
    let cursor: string | undefined;
    do {
      const path = `/api/chat/conversations${
        cursor ? `?cursor=${encodeURIComponent(cursor)}` : ""
      }`;
      const page = await requestJson(path, decodeChatHistoryIndex, signal);
      conversations.push(...page.conversations);
      const next = page.nextCursor?.trim() || undefined;
      if (next && seenCursors.has(next)) {
        throw new ChatHistoryContractError(
          "$index.nextCursor",
          "a cursor that advances to the next page"
        );
      }
      if (next) seenCursors.add(next);
      cursor = next;
    } while (cursor);
    return conversations;
  },

  async loadConversation(
    conversationId: string,
    signal?: AbortSignal
  ): Promise<ChatConversationDetail> {
    return requestJson(
      conversationPath(conversationId),
      decodeChatConversationDetail,
      signal
    );
  },

  async loadConversationState(
    conversationId: string,
    cursor: ChatStateCursor = {},
    signal?: AbortSignal
  ): Promise<unknown> {
    const query = new URLSearchParams();
    if (cursor.afterStateVersion !== undefined) {
      if (
        !Number.isSafeInteger(cursor.afterStateVersion) ||
        cursor.afterStateVersion < 0
      ) {
        throw new ChatHistoryContractError(
          "$cursor.afterStateVersion",
          "a non-negative safe integer"
        );
      }
      query.set("afterStateVersion", String(cursor.afterStateVersion));
    }
    if (cursor.turnId?.trim()) query.set("turnId", cursor.turnId.trim());
    const queryString = query.toString();
    const suffix = queryString ? `?${queryString}` : "";
    return requestJson(
      `${conversationPath(conversationId)}/state${suffix}`,
      (value) => value,
      signal,
      true
    );
  },

  async deleteConversation(conversationId: string): Promise<void> {
    const response = await authFetch(conversationPath(conversationId), {
      headers: JSON_HEADERS,
      method: "DELETE",
    });
    if (!response.ok) throw await createApiError(response);
  },
};
