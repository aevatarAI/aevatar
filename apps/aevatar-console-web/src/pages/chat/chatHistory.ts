import type {
  ChatMessage,
  ConversationMeta,
  StoredChatMessage,
} from "./chatTypes";

type StoredConversationPayload = {
  messages: StoredChatMessage[];
};

const CHAT_HISTORY_STORAGE_PREFIX = "aevatar-console-web.chat.v1";

function readStorage(): Storage | null {
  if (typeof window === "undefined") {
    return null;
  }

  return window.localStorage;
}

function buildIndexKey(scopeId: string): string {
  return `${CHAT_HISTORY_STORAGE_PREFIX}.index.${scopeId.trim()}`;
}

function buildConversationKey(scopeId: string, conversationId: string): string {
  return `${CHAT_HISTORY_STORAGE_PREFIX}.conversation.${scopeId.trim()}.${conversationId.trim()}`;
}

function createSafeId(): string {
  return globalThis.crypto?.randomUUID?.()
    ? globalThis.crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function readJson<T>(value: string | null, fallback: T): T {
  if (!value) {
    return fallback;
  }

  try {
    return JSON.parse(value) as T;
  } catch {
    return fallback;
  }
}

export function createConversationId(): string {
  return createSafeId();
}

export function listConversationMetas(scopeId: string): ConversationMeta[] {
  const normalizedScopeId = scopeId.trim();
  if (!normalizedScopeId) {
    return [];
  }

  const storage = readStorage();
  if (!storage) {
    return [];
  }

  const items = readJson<ConversationMeta[]>(
    storage.getItem(buildIndexKey(normalizedScopeId)),
    []
  );

  return [...items].sort((left, right) =>
    right.updatedAt.localeCompare(left.updatedAt)
  );
}

export function loadConversation(
  scopeId: string,
  conversationId: string
): StoredChatMessage[] {
  const normalizedScopeId = scopeId.trim();
  const normalizedConversationId = conversationId.trim();
  if (!normalizedScopeId || !normalizedConversationId) {
    return [];
  }

  const storage = readStorage();
  if (!storage) {
    return [];
  }

  const payload = readJson<StoredConversationPayload>(
    storage.getItem(buildConversationKey(normalizedScopeId, normalizedConversationId)),
    {
      messages: [],
    }
  );

  return Array.isArray(payload.messages) ? payload.messages : [];
}

export function saveConversation(
  scopeId: string,
  meta: ConversationMeta,
  messages: StoredChatMessage[]
): void {
  const normalizedScopeId = scopeId.trim();
  const normalizedConversationId = meta.id.trim();
  if (!normalizedScopeId || !normalizedConversationId) {
    return;
  }

  const storage = readStorage();
  if (!storage) {
    return;
  }

  storage.setItem(
    buildConversationKey(normalizedScopeId, normalizedConversationId),
    JSON.stringify({
      messages,
    } satisfies StoredConversationPayload)
  );

  const previous = listConversationMetas(normalizedScopeId).filter(
    (item) => item.id !== normalizedConversationId
  );
  storage.setItem(
    buildIndexKey(normalizedScopeId),
    JSON.stringify([meta, ...previous])
  );
}

export function deleteConversation(
  scopeId: string,
  conversationId: string
): void {
  const normalizedScopeId = scopeId.trim();
  const normalizedConversationId = conversationId.trim();
  if (!normalizedScopeId || !normalizedConversationId) {
    return;
  }

  const storage = readStorage();
  if (!storage) {
    return;
  }

  storage.removeItem(
    buildConversationKey(normalizedScopeId, normalizedConversationId)
  );
  storage.setItem(
    buildIndexKey(normalizedScopeId),
    JSON.stringify(
      listConversationMetas(normalizedScopeId).filter(
        (item) => item.id !== normalizedConversationId
      )
    )
  );
}

export function serializeChatMessages(
  messages: readonly ChatMessage[]
): StoredChatMessage[] {
  return messages
    .filter((message) => message.status !== "streaming")
    .map((message) => ({
      content: message.content,
      error: message.error,
      events: message.events ? [...message.events] : undefined,
      id: message.id,
      role: message.role,
      status: message.status === "streaming" ? "complete" : message.status,
      steps: message.steps ? [...message.steps] : undefined,
      thinking: message.thinking,
      timestamp: message.timestamp,
      toolCalls: message.toolCalls ? [...message.toolCalls] : undefined,
    }));
}

export function hydrateChatMessages(
  messages: readonly StoredChatMessage[]
): ChatMessage[] {
  return messages.map((message) => ({
    content: message.content,
    error: message.error,
    events: message.events ? [...message.events] : undefined,
    id: message.id,
    role: message.role,
    status: message.status,
    steps: message.steps ? [...message.steps] : undefined,
    thinking: message.thinking,
    timestamp: message.timestamp,
    toolCalls: message.toolCalls ? [...message.toolCalls] : undefined,
  }));
}

