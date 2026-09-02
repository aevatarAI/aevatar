jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

import { authFetch } from "@/shared/auth/fetch";
import {
  ChatHistoryApiError,
  ChatHistoryContractError,
  chatHistoryApi,
  decodeChatConversationDetail,
  decodeChatCreateRecovery,
  decodeChatHistoryIndex,
} from "./chatHistoryApi";

function jsonResponse(payload: unknown): Response {
  return {
    json: jest.fn().mockResolvedValue(payload),
    ok: true,
    status: 200,
    statusText: "OK",
  } as unknown as Response;
}

describe("chatHistoryApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("loads and decodes the authenticated conversation index", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      jsonResponse({
        conversations: [
          {
            createdAt: "2026-07-17T02:30:00+00:00",
            id: "conversation-a",
            llmModel: null,
            llmRoute: "/api/v1/proxy/s/openai",
            messageCount: 2,
            title: "Create support workflow",
            updatedAt: "2026-07-17T02:35:00+00:00",
          },
        ],
      })
    );

    await expect(
      chatHistoryApi.listConversationMetas(" scope/a ")
    ).resolves.toEqual([
      {
        createdAt: "2026-07-17T02:30:00+00:00",
        id: "conversation-a",
        llmModel: null,
        llmRoute: "/api/v1/proxy/s/openai",
        messageCount: 2,
        title: "Create support workflow",
        updatedAt: "2026-07-17T02:35:00+00:00",
      },
    ]);
    expect(authFetch).toHaveBeenCalledWith(
      "/api/scopes/scope%2Fa/chat-history",
      {
        headers: { Accept: "application/json" },
        method: "GET",
      }
    );
  });

  it("follows opaque index cursors and combines every page", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        jsonResponse({
          conversations: [
            {
              createdAt: "2026-07-17T02:30:00+00:00",
              id: "conversation-new",
              messageCount: 2,
              title: "New conversation",
              updatedAt: "2026-07-17T02:35:00+00:00",
            },
          ],
          nextCursor: "opaque+/cursor==",
        })
      )
      .mockResolvedValueOnce(
        jsonResponse({
          conversations: [
            {
              createdAt: "2026-07-16T02:30:00+00:00",
              id: "conversation-old",
              messageCount: 4,
              title: "Old conversation",
              updatedAt: "2026-07-16T02:35:00+00:00",
            },
          ],
          nextCursor: null,
        })
      );

    const controller = new AbortController();
    await expect(
      chatHistoryApi.listConversationMetas("scope-a", controller.signal)
    ).resolves.toEqual(
      expect.arrayContaining([
        expect.objectContaining({ id: "conversation-new" }),
        expect.objectContaining({ id: "conversation-old" }),
      ])
    );
    expect(authFetch).toHaveBeenNthCalledWith(
      1,
      "/api/scopes/scope-a/chat-history",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
    expect(authFetch).toHaveBeenNthCalledWith(
      2,
      "/api/scopes/scope-a/chat-history?cursor=opaque%2B%2Fcursor%3D%3D",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
  });

  it("loads and validates create recovery identity", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      jsonResponse({
        conversationId: "conversation-a",
        stateVersion: 3,
        status: "append_committed",
        turnId: "turn-a",
      })
    );

    await expect(
      chatHistoryApi.recoverCreate("scope/a", "create/key")
    ).resolves.toEqual({
      conversationId: "conversation-a",
      stateVersion: 3,
      status: "append_committed",
      turnId: "turn-a",
    });
    expect(authFetch).toHaveBeenCalledWith(
      "/api/scopes/scope%2Fa/chat-history/create-recovery/create%2Fkey",
      {
        headers: { Accept: "application/json" },
        method: "GET",
      }
    );
    expect(() =>
      decodeChatCreateRecovery({
        conversationId: "conversation-a",
        stateVersion: -1,
        status: "reserved",
        turnId: "turn-a",
      })
    ).toThrow(ChatHistoryContractError);
  });

  it("preserves documented message fields and unknown role or status strings", async () => {
    (authFetch as jest.Mock).mockResolvedValue(
      jsonResponse({
        messages: [
          {
            authorId: null,
            authorName: "Automation",
            content: "Queued for review",
            error: null,
            id: "turn-a:observer",
            role: "observer",
            status: "queued",
            thinking: null,
            timestamp: 1784255700000,
            turnId: "turn-a",
          },
        ],
        stateVersion: 7,
      })
    );

    const controller = new AbortController();
    await expect(
      chatHistoryApi.loadConversation(
        "scope/a",
        "conversation/a",
        controller.signal
      )
    ).resolves.toEqual({
      messages: [
        {
          authorId: null,
          authorName: "Automation",
          content: "Queued for review",
          error: null,
          id: "turn-a:observer",
          role: "observer",
          status: "queued",
          thinking: null,
          timestamp: 1784255700000,
          turnId: "turn-a",
        },
      ],
      stateVersion: 7,
    });
    expect(authFetch).toHaveBeenCalledWith(
      "/api/scopes/scope%2Fa/chat-history/conversations/conversation%2Fa",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
  });

  it("accepts empty index and detail responses", () => {
    expect(decodeChatHistoryIndex({ conversations: [] })).toEqual({
      conversations: [],
    });
    expect(
      decodeChatConversationDetail({ messages: [], stateVersion: 0 })
    ).toEqual({ messages: [], stateVersion: 0 });
  });

  it("rejects malformed successful response bodies explicitly", () => {
    expect(() => decodeChatHistoryIndex({ conversations: {} })).toThrow(
      ChatHistoryContractError
    );
    expect(() => decodeChatConversationDetail([])).toThrow(
      ChatHistoryContractError
    );
    expect(() =>
      decodeChatConversationDetail({ messages: [], stateVersion: -1 })
    ).toThrow(expect.objectContaining({ path: "$conversation.stateVersion" }));
    expect(() =>
      decodeChatConversationDetail({
        messages: [
          {
            content: "hello",
            id: "message-a",
            role: "user",
            status: "complete",
            timestamp: "not-a-number",
          },
        ],
        stateVersion: 7,
      })
    ).toThrow(
      expect.objectContaining({
        code: "INVALID_CHAT_HISTORY_RESPONSE",
        path: "$conversation.messages[0].timestamp",
      })
    );
  });

  it("deletes remotely without parsing the empty success body", async () => {
    const json = jest.fn().mockRejectedValue(new Error("body is empty"));
    (authFetch as jest.Mock).mockResolvedValue({
      json,
      ok: true,
      status: 200,
      statusText: "OK",
    } as unknown as Response);

    await expect(
      chatHistoryApi.deleteConversation("scope/a", "conversation/a")
    ).resolves.toBeUndefined();
    expect(json).not.toHaveBeenCalled();
    expect(authFetch).toHaveBeenCalledWith(
      "/api/scopes/scope%2Fa/chat-history/conversations/conversation%2Fa",
      {
        headers: { Accept: "application/json" },
        method: "DELETE",
      }
    );
  });

  it("throws structured HTTP errors", async () => {
    (authFetch as jest.Mock).mockResolvedValue({
      ok: false,
      status: 403,
      statusText: "Forbidden",
      text: jest.fn().mockResolvedValue(
        JSON.stringify({
          code: "SCOPE_ACCESS_DENIED",
          message: "Authenticated scope does not match requested scope.",
        })
      ),
    } as unknown as Response);

    let error: unknown;
    try {
      await chatHistoryApi.listConversationMetas("scope-a");
    } catch (caught) {
      error = caught;
    }

    expect(error).toBeInstanceOf(ChatHistoryApiError);
    expect(error).toMatchObject({
      code: "SCOPE_ACCESS_DENIED",
      message: "Authenticated scope does not match requested scope.",
      status: 403,
    });
  });

});
