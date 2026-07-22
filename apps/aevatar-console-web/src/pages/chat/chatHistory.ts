import type {
  ChatMessage,
  ConversationMeta,
  LocalChatConversation,
  LocalChatStatus,
  StoredChatMessage,
} from "./chatTypes";

const CHAT_HISTORY_STORAGE_PREFIX = "aevatar.chat.localHistory.v1";

function readStorage(): Storage | null {
  if (typeof window === "undefined") {
    return null;
  }

  return window.localStorage;
}

function buildIndexKey(scopeId: string): string {
  return `${CHAT_HISTORY_STORAGE_PREFIX}:${scopeId.trim()}`;
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

  const items = readJson<LocalChatConversation[]>(
    storage.getItem(buildIndexKey(normalizedScopeId)),
    []
  );

  return items
    .map(toConversationMeta)
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
}

export function listConversations(scopeId: string): LocalChatConversation[] {
  const normalizedScopeId = scopeId.trim();
  if (!normalizedScopeId) {
    return [];
  }

  const storage = readStorage();
  if (!storage) {
    return [];
  }

  const items = readJson<LocalChatConversation[]>(
    storage.getItem(buildIndexKey(normalizedScopeId)),
    []
  );

  return items
    .filter((item) => item.scopeId === normalizedScopeId && item.id.trim())
    .map((item) => ({
      ...item,
      messages: Array.isArray(item.messages) ? item.messages : [],
      pendingReadModelStateVersionFloor: normalizeStateVersionFloor(
        item.pendingReadModelStateVersionFloor
      ),
      serverConversationId: item.serverConversationId?.trim() || undefined,
      stateVersion: normalizeStateVersion(item.stateVersion),
      status: normalizeStatus(item.status),
    }))
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt));
}

export function loadConversationRecord(
  scopeId: string,
  conversationId: string
): LocalChatConversation | null {
  const normalizedScopeId = scopeId.trim();
  const normalizedConversationId = conversationId.trim();
  if (!normalizedScopeId || !normalizedConversationId) {
    return null;
  }

  return (
    listConversations(normalizedScopeId).find(
      (item) => item.id === normalizedConversationId
    ) ?? null
  );
}

export function loadConversation(
  scopeId: string,
  conversationId: string
): StoredChatMessage[] {
  return loadConversationRecord(scopeId, conversationId)?.messages ?? [];
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

  const previous = listConversations(normalizedScopeId).filter(
    (item) => item.id !== normalizedConversationId
  );
  const conversation = fromMeta(normalizedScopeId, meta, messages);
  storage.setItem(
    buildIndexKey(normalizedScopeId),
    JSON.stringify([conversation, ...previous])
  );
}

export function saveConversationRecord(
  scopeId: string,
  conversation: LocalChatConversation
): void {
  saveConversation(
    scopeId,
    toConversationMeta(conversation),
    conversation.messages
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

  storage.setItem(
    buildIndexKey(normalizedScopeId),
    JSON.stringify(
      listConversations(normalizedScopeId).filter(
        (item) => item.id !== normalizedConversationId
      )
    )
  );
}

export function renameConversation(
  scopeId: string,
  conversationId: string,
  title: string
): void {
  const normalizedScopeId = scopeId.trim();
  const normalizedConversationId = conversationId.trim();
  const normalizedTitle = title.trim();
  if (!normalizedScopeId || !normalizedConversationId || !normalizedTitle) {
    return;
  }

  const storage = readStorage();
  if (!storage) {
    return;
  }

  const nextItems = listConversations(normalizedScopeId).map((item) =>
    item.id === normalizedConversationId
      ? {
          ...item,
          title: normalizedTitle,
          updatedAt: new Date().toISOString(),
        }
      : item
  );

  storage.setItem(buildIndexKey(normalizedScopeId), JSON.stringify(nextItems));
}

export function serializeChatMessages(
  messages: readonly ChatMessage[]
): StoredChatMessage[] {
  return messages
    .filter(
      (message) =>
        message.status !== "streaming" || hasVisibleStreamingMessage(message)
    )
    .map((message) => ({
      content: message.content,
      error: message.error,
      events: message.events ? [...message.events] : undefined,
      id: message.id,
      pendingApproval: message.pendingApproval
        ? { ...message.pendingApproval }
        : undefined,
      pendingRunIntervention: message.pendingRunIntervention
        ? { ...message.pendingRunIntervention }
        : undefined,
      role: message.role,
      status: message.status === "streaming" ? "complete" : message.status,
      steps: message.steps ? [...message.steps] : undefined,
      thinking: message.thinking,
      timestamp: message.timestamp,
      toolCalls: message.toolCalls ? [...message.toolCalls] : undefined,
    }));
}

function hasVisibleStreamingMessage(message: ChatMessage): boolean {
  return Boolean(
    message.content.trim() ||
      message.error?.trim() ||
      message.thinking?.trim() ||
      message.pendingApproval ||
      message.pendingRunIntervention ||
      message.events?.length ||
      message.steps?.length ||
      message.toolCalls?.length
  );
}

export function hydrateChatMessages(
  messages: readonly StoredChatMessage[]
): ChatMessage[] {
  return messages.map((message) => ({
    content: message.content,
    error: message.error,
    events: message.events ? [...message.events] : undefined,
    id: message.id,
    pendingApproval: message.pendingApproval
      ? { ...message.pendingApproval }
      : undefined,
    pendingRunIntervention: message.pendingRunIntervention
      ? { ...message.pendingRunIntervention }
      : undefined,
    role: message.role,
    status: message.status,
    steps: message.steps ? [...message.steps] : undefined,
    thinking: message.thinking,
    timestamp: message.timestamp,
    toolCalls: message.toolCalls ? [...message.toolCalls] : undefined,
  }));
}

function normalizeStatus(status: LocalChatStatus | undefined): LocalChatStatus {
  switch (status) {
    case "draft":
    case "streaming":
    case "needs_confirmation":
    case "creating":
    case "completed_text":
    case "completed_with_studio_target":
    case "error":
      return status;
    default:
      return "draft";
  }
}

function normalizeStateVersion(value: number | undefined): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? Math.trunc(value)
    : undefined;
}

function normalizeStateVersionFloor(
  value: number | undefined
): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value >= 0
    ? Math.trunc(value)
    : undefined;
}

function toConversationMeta(
  conversation: LocalChatConversation
): ConversationMeta {
  return {
    createdAt: conversation.createdAt,
    id: conversation.id,
    messageCount: conversation.messages.length,
    pendingReadModelStateVersionFloor: normalizeStateVersionFloor(
      conversation.pendingReadModelStateVersionFloor
    ),
    scopeId: conversation.scopeId,
    serviceId: "chat",
    serviceKind: "chat",
    serverConversationId: conversation.serverConversationId?.trim() || undefined,
    stateVersion: normalizeStateVersion(conversation.stateVersion),
    status: normalizeStatus(conversation.status),
    target: conversation.target,
    title: conversation.title,
    updatedAt: conversation.updatedAt,
    usage: conversation.usage,
  };
}

function fromMeta(
  scopeId: string,
  meta: ConversationMeta,
  messages: StoredChatMessage[]
): LocalChatConversation {
  return {
    createdAt: meta.createdAt,
    id: meta.id,
    messages,
    pendingReadModelStateVersionFloor: normalizeStateVersionFloor(
      meta.pendingReadModelStateVersionFloor
    ),
    scopeId,
    serverConversationId: meta.serverConversationId?.trim() || undefined,
    status: normalizeStatus(meta.status),
    stateVersion: normalizeStateVersion(meta.stateVersion),
    target: meta.target,
    title: meta.title,
    updatedAt: meta.updatedAt,
    usage: meta.usage,
  };
}
