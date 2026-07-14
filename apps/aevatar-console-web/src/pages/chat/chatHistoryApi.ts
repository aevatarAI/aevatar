import {
  deleteConversation as deleteLocalConversation,
  listConversationMetas as listLocalConversationMetas,
  loadConversation as loadLocalConversation,
  renameConversation as renameLocalConversation,
  saveConversation as saveLocalConversation,
} from "./chatHistory";
import type { ConversationMeta, StoredChatMessage } from "./chatTypes";

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
