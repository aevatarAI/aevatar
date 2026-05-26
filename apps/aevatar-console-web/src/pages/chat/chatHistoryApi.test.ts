jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

jest.mock("./chatHistory", () => ({
  deleteConversation: jest.fn(),
  listConversationMetas: jest.fn(),
  loadConversation: jest.fn(),
  saveConversation: jest.fn(),
}));

import { authFetch } from "@/shared/auth/fetch";
import {
  deleteConversation as deleteLocalConversation,
  listConversationMetas as listLocalConversationMetas,
  loadConversation as loadLocalConversation,
  saveConversation as saveLocalConversation,
} from "./chatHistory";
import { chatHistoryApi } from "./chatHistoryApi";

describe("chatHistoryApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("mirrors typed session metadata to the remote chat-history store while keeping the local fallback updated", async () => {
    (authFetch as jest.Mock).mockResolvedValue({
      headers: new Headers({
        "content-type": "application/json",
      }),
      ok: true,
      status: 204,
    });

    const meta = {
      createdAt: "2026-04-01T08:00:00.000Z",
      id: "conversation-1",
      llmModel: "gpt-5.4-mini",
      llmRoute: "/api/v1/proxy/s/openai",
      messageCount: 1,
      serviceId: "nyxid-chat",
      serviceKind: "nyxid-chat",
      session: {
        preferences: {
          llmModel: "gpt-5.4-mini",
          llmRoute: "/api/v1/proxy/s/openai",
        },
        runtime: {
          actorId: "actor://nyxid",
          commandId: "cmd-1",
          runId: "run-1",
        },
      },
      title: "Need routing help",
      updatedAt: "2026-04-01T08:01:00.000Z",
    };
    const messages = [
      {
        content: "Need routing help",
        id: "user-1",
        role: "user",
        status: "complete",
        timestamp: 1,
      },
    ];

    await chatHistoryApi.saveConversation("scope-a", meta as any, messages as any);

    expect(saveLocalConversation).toHaveBeenCalledWith("scope-a", meta, messages);
    expect(authFetch).toHaveBeenCalledWith(
      "/api/scopes/scope-a/chat-history/conversations/conversation-1",
      expect.objectContaining({
        body: JSON.stringify({
          meta,
          messages,
        }),
        method: "PUT",
      })
    );
  });

  it("falls back to the local history index when the remote chat-history index is unavailable", async () => {
    (authFetch as jest.Mock).mockRejectedValue(new Error("chrono storage unavailable"));
    (listLocalConversationMetas as jest.Mock).mockReturnValue([
      {
        createdAt: "2026-04-01T08:00:00.000Z",
        id: "conversation-1",
        messageCount: 1,
        serviceId: "support-service",
        serviceKind: "service",
        title: "Fallback conversation",
        updatedAt: "2026-04-01T08:01:00.000Z",
      },
    ]);

    await expect(chatHistoryApi.listConversationMetas("scope-a")).resolves.toEqual([
      {
        createdAt: "2026-04-01T08:00:00.000Z",
        id: "conversation-1",
        messageCount: 1,
        serviceId: "support-service",
        serviceKind: "service",
        title: "Fallback conversation",
        updatedAt: "2026-04-01T08:01:00.000Z",
      },
    ]);
  });

  it("falls back to local conversation content when a remote conversation cannot be loaded", async () => {
    (authFetch as jest.Mock).mockRejectedValue(new Error("remote failed"));
    (loadLocalConversation as jest.Mock).mockReturnValue([
      {
        content: "Restored locally",
        id: "assistant-1",
        role: "assistant",
        status: "complete",
        timestamp: 2,
      },
    ]);

    await expect(
      chatHistoryApi.loadConversation("scope-a", "conversation-1")
    ).resolves.toEqual([
      {
        content: "Restored locally",
        id: "assistant-1",
        role: "assistant",
        status: "complete",
        timestamp: 2,
      },
    ]);
  });

  it("keeps local state authoritative when a remote delete fails", async () => {
    (authFetch as jest.Mock).mockRejectedValue(new Error("remote failed"));

    await chatHistoryApi.deleteConversation("scope-a", "conversation-1");

    expect(deleteLocalConversation).toHaveBeenCalledWith(
      "scope-a",
      "conversation-1"
    );
  });
});
