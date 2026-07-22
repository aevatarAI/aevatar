import { readResponseError } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";
import {
  deleteConversation as deleteLocalConversation,
  listConversationMetas as listLocalConversationMetas,
  loadConversation as loadLocalConversation,
  renameConversation as renameLocalConversation,
  saveConversation as saveLocalConversation,
} from "./chatHistory";
import type { ConversationMeta, StoredChatMessage } from "./chatTypes";

type JsonRecord = Record<string, unknown>;

export type ServerChatConversationRecord = {
  messages: StoredChatMessage[];
  stateVersion: number;
};

function asRecord(value: unknown): JsonRecord | undefined {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonRecord)
    : undefined;
}

function normalizeStateVersion(value: unknown): number {
  if (typeof value === "number" && Number.isFinite(value) && value > 0) {
    return Math.trunc(value);
  }

  if (typeof value === "string" && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? Math.trunc(parsed) : 0;
  }

  return 0;
}

function normalizeServerConversation(
  payload: unknown
): ServerChatConversationRecord {
  if (Array.isArray(payload)) {
    return {
      messages: payload as StoredChatMessage[],
      stateVersion: 0,
    };
  }

  const record = asRecord(payload);
  const rawMessages = record?.messages ?? record?.Messages;
  const messages = Array.isArray(rawMessages)
    ? (rawMessages as StoredChatMessage[])
    : [];
  return {
    messages,
    stateVersion: normalizeStateVersion(
      record?.stateVersion ?? record?.StateVersion
    ),
  };
}

export const chatHistoryApi = {
  async listConversationMetas(scopeId: string): Promise<ConversationMeta[]> {
    return listLocalConversationMetas(scopeId);
  },

  async loadConversation(
    scopeId: string,
    conversationId: string
  ): Promise<StoredChatMessage[]> {
    return loadLocalConversation(scopeId, conversationId);
  },

  async loadServerConversation(
    scopeId: string,
    conversationId: string
  ): Promise<ServerChatConversationRecord | null> {
    const normalizedScopeId = scopeId.trim();
    const normalizedConversationId = conversationId.trim();
    if (!normalizedScopeId || !normalizedConversationId) {
      return null;
    }

    const response = await authFetch(
      `/api/scopes/${encodeURIComponent(normalizedScopeId)}/chat-history/conversations/${encodeURIComponent(normalizedConversationId)}`,
      {
        headers: {
          Accept: "application/json",
        },
        method: "GET",
      }
    );
    if (response.status === 404) {
      return null;
    }
    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return normalizeServerConversation(await response.json());
  },

  async saveConversation(
    scopeId: string,
    meta: ConversationMeta,
    messages: StoredChatMessage[]
  ): Promise<void> {
    saveLocalConversation(scopeId, meta, messages);
  },

  async renameConversation(
    scopeId: string,
    conversationId: string,
    title: string
  ): Promise<void> {
    renameLocalConversation(scopeId, conversationId, title);
  },

  async deleteConversation(
    scopeId: string,
    conversationId: string
  ): Promise<void> {
    deleteLocalConversation(scopeId, conversationId);
  },
};
