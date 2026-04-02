import { readResponseError } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";
import {
  deleteConversation as deleteLocalConversation,
  listConversationMetas as listLocalConversationMetas,
  loadConversation as loadLocalConversation,
  saveConversation as saveLocalConversation,
} from "./chatHistory";
import type { ConversationMeta, StoredChatMessage } from "./chatTypes";

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

async function readJson<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const contentType = response.headers.get("content-type") || "";
  if (!contentType.includes("json")) {
    return undefined as T;
  }

  const text = await response.text();
  if (!text.trim()) {
    return undefined as T;
  }

  return JSON.parse(text) as T;
}

type RemoteChatHistoryIndex = {
  conversations?: ConversationMeta[];
};

export const chatHistoryApi = {
  async listConversationMetas(scopeId: string): Promise<ConversationMeta[]> {
    const normalizedScopeId = scopeId.trim();
    if (!normalizedScopeId) {
      return [];
    }

    try {
      const payload = await readJson<RemoteChatHistoryIndex>(
        `/api/scopes/${encodeSegment(normalizedScopeId)}/chat-history`
      );
      return Array.isArray(payload.conversations)
        ? [...payload.conversations].sort((left, right) =>
            right.updatedAt.localeCompare(left.updatedAt)
          )
        : [];
    } catch {
      return listLocalConversationMetas(normalizedScopeId);
    }
  },

  async loadConversation(
    scopeId: string,
    conversationId: string
  ): Promise<StoredChatMessage[]> {
    const normalizedScopeId = scopeId.trim();
    const normalizedConversationId = conversationId.trim();
    if (!normalizedScopeId || !normalizedConversationId) {
      return [];
    }

    try {
      const payload = await readJson<StoredChatMessage[]>(
        `/api/scopes/${encodeSegment(
          normalizedScopeId
        )}/chat-history/conversations/${encodeSegment(normalizedConversationId)}`
      );
      return Array.isArray(payload) ? payload : [];
    } catch {
      return loadLocalConversation(normalizedScopeId, normalizedConversationId);
    }
  },

  async saveConversation(
    scopeId: string,
    meta: ConversationMeta,
    messages: StoredChatMessage[]
  ): Promise<void> {
    const normalizedScopeId = scopeId.trim();
    const normalizedConversationId = meta.id.trim();
    if (!normalizedScopeId || !normalizedConversationId) {
      return;
    }

    saveLocalConversation(normalizedScopeId, meta, messages);

    try {
      await readJson<void>(
        `/api/scopes/${encodeSegment(
          normalizedScopeId
        )}/chat-history/conversations/${encodeSegment(normalizedConversationId)}`,
        {
          method: "PUT",
          headers: {
            Accept: "application/json",
            "Content-Type": "application/json",
          },
          body: JSON.stringify({
            meta,
            messages,
          }),
        }
      );
    } catch {
      // Keep the local fallback so chat history remains usable even if the
      // remote history store is temporarily unavailable.
    }
  },

  async deleteConversation(
    scopeId: string,
    conversationId: string
  ): Promise<void> {
    const normalizedScopeId = scopeId.trim();
    const normalizedConversationId = conversationId.trim();
    if (!normalizedScopeId || !normalizedConversationId) {
      return;
    }

    deleteLocalConversation(normalizedScopeId, normalizedConversationId);

    try {
      await readJson<void>(
        `/api/scopes/${encodeSegment(
          normalizedScopeId
        )}/chat-history/conversations/${encodeSegment(normalizedConversationId)}`,
        {
          method: "DELETE",
          headers: {
            Accept: "application/json",
          },
        }
      );
    } catch {
      // Ignore remote delete failures and keep the local state authoritative
      // for this browser session.
    }
  },
};
