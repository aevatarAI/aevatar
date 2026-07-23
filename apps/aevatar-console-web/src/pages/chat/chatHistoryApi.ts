import { readResponseErrorDetails } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";
import type {
  ChatConversationDetail,
  ChatCreateRecovery,
  ChatHistoryIndex,
  ConversationMeta,
  StoredChatMessage,
} from "./chatTypes";

type JsonRecord = Record<string, unknown>;

const JSON_HEADERS = {
  Accept: "application/json",
};

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
  if (typeof value !== "string") {
    return failContract(`${path}.${key}`, "a string");
  }

  return value;
}

function readNumber(record: JsonRecord, key: string, path: string): number {
  const value = record[key];
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return failContract(`${path}.${key}`, "a finite number");
  }

  return value;
}

function readOptionalString(
  record: JsonRecord,
  key: string,
  path: string
): string | undefined {
  const value = record[key];
  if (value === undefined || value === null) {
    return undefined;
  }
  if (typeof value !== "string") {
    return failContract(`${path}.${key}`, "a string, null, or omission");
  }

  return value;
}

function readOptionalNullableString(
  record: JsonRecord,
  key: string,
  path: string
): string | null | undefined {
  if (!(key in record) || record[key] === undefined) {
    return undefined;
  }

  const value = record[key];
  if (value === null || typeof value === "string") {
    return value;
  }

  return failContract(`${path}.${key}`, "a string, null, or omission");
}

function withOptionalField(
  value: Record<string, unknown>,
  key: string,
  field: unknown
): void {
  if (field !== undefined) {
    value[key] = field;
  }
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

  withOptionalField(meta, "serviceId", readOptionalString(record, "serviceId", path));
  withOptionalField(
    meta,
    "serviceKind",
    readOptionalString(record, "serviceKind", path)
  );
  withOptionalField(
    meta,
    "llmRoute",
    readOptionalNullableString(record, "llmRoute", path)
  );
  withOptionalField(
    meta,
    "llmModel",
    readOptionalNullableString(record, "llmModel", path)
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
    withOptionalField(
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

export function decodeChatCreateRecovery(value: unknown): ChatCreateRecovery {
  const record = asRecord(value, "$recovery");
  const stateVersion = readNumber(record, "stateVersion", "$recovery");
  if (!Number.isInteger(stateVersion) || stateVersion < 0) {
    return failContract("$recovery.stateVersion", "a non-negative integer");
  }

  return {
    conversationId: readString(record, "conversationId", "$recovery"),
    stateVersion,
    status: readString(record, "status", "$recovery"),
    turnId: readString(record, "turnId", "$recovery"),
  };
}

function decodeStoredChatMessagesAtPath(
  value: unknown,
  path: string
): StoredChatMessage[] {
  if (!Array.isArray(value)) {
    return failContract(path, "an array");
  }

  return value.map((message, index) =>
    decodeStoredChatMessage(message, `${path}[${index}]`)
  );
}

export function decodeChatConversationDetail(
  value: unknown
): ChatConversationDetail {
  const record = asRecord(value, "$conversation");
  const stateVersion = readNumber(record, "stateVersion", "$conversation");
  if (!Number.isSafeInteger(stateVersion) || stateVersion < 0) {
    return failContract(
      "$conversation.stateVersion",
      "a non-negative safe integer"
    );
  }

  return {
    messages: decodeStoredChatMessagesAtPath(
      record.messages,
      "$conversation.messages"
    ),
    stateVersion,
  };
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

function buildHistoryPath(scopeId: string): string {
  return `/api/scopes/${encodeSegment(scopeId)}/chat-history`;
}

function buildConversationPath(scopeId: string, conversationId: string): string {
  return `${buildHistoryPath(scopeId)}/conversations/${encodeSegment(
    conversationId
  )}`;
}

function buildCreateRecoveryPath(
  scopeId: string,
  commandId: string
): string {
  return `${buildHistoryPath(scopeId)}/create-recovery/${encodeSegment(
    commandId
  )}`;
}

function buildIndexPagePath(scopeId: string, cursor?: string): string {
  const path = buildHistoryPath(scopeId);
  return cursor ? `${path}?cursor=${encodeURIComponent(cursor)}` : path;
}

async function createApiError(response: Response): Promise<ChatHistoryApiError> {
  const details = await readResponseErrorDetails(response);
  return new ChatHistoryApiError(details.message, details.status, details.code);
}

async function requestJson<T>(
  path: string,
  decoder: (value: unknown) => T,
  signal?: AbortSignal
): Promise<T> {
  const response = await authFetch(path, {
    headers: JSON_HEADERS,
    method: "GET",
    ...(signal ? { signal } : {}),
  });
  if (!response.ok) {
    throw await createApiError(response);
  }

  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    throw new ChatHistoryContractError("$response", "valid JSON");
  }

  return decoder(payload);
}

export const chatHistoryApi = {
  async listConversationMetas(
    scopeId: string,
    signal?: AbortSignal
  ): Promise<ConversationMeta[]> {
    const conversations: ConversationMeta[] = [];
    const seenCursors = new Set<string>();
    let cursor: string | undefined;
    do {
      const index = await requestJson(
        buildIndexPagePath(scopeId, cursor),
        decodeChatHistoryIndex,
        signal
      );
      conversations.push(...index.conversations);
      const nextCursor = index.nextCursor?.trim() || undefined;
      if (nextCursor && seenCursors.has(nextCursor)) {
        throw new ChatHistoryContractError(
          "$index.nextCursor",
          "a cursor that advances to the next page"
        );
      }
      if (nextCursor) {
        seenCursors.add(nextCursor);
      }
      cursor = nextCursor;
    } while (cursor);
    return conversations;
  },

  async recoverCreate(
    scopeId: string,
    commandId: string,
    signal?: AbortSignal
  ): Promise<ChatCreateRecovery> {
    return requestJson(
      buildCreateRecoveryPath(scopeId, commandId),
      decodeChatCreateRecovery,
      signal
    );
  },

  async loadConversation(
    scopeId: string,
    conversationId: string,
    signal?: AbortSignal
  ): Promise<ChatConversationDetail> {
    return requestJson(
      buildConversationPath(scopeId, conversationId),
      decodeChatConversationDetail,
      signal
    );
  },

  async deleteConversation(
    scopeId: string,
    conversationId: string
  ): Promise<void> {
    const response = await authFetch(
      buildConversationPath(scopeId, conversationId),
      {
        headers: JSON_HEADERS,
        method: "DELETE",
      }
    );
    if (!response.ok) {
      throw await createApiError(response);
    }
  },
};
