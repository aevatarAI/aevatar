jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

import { authFetch } from "@/shared/auth/fetch";
import {
  ChatHistoryContractError,
  chatHistoryApi,
  decodeChatConversationDetail,
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

  it("loads every canonical conversation page with opaque cursors", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        jsonResponse({
          conversations: [
            {
              activeStepSummary: "Connect GitHub",
              attentionKind: "action",
              attentionSince: "2026-08-04T02:35:00+00:00",
              createdAt: "2026-08-04T02:30:00+00:00",
              id: "conversation-new",
              messageCount: 2,
              stateVersion: 7,
              taskStatus: "blocked",
              title: "New conversation",
              updatedAt: "2026-08-04T02:35:00+00:00",
            },
          ],
          nextCursor: "opaque+/cursor==",
        })
      )
      .mockResolvedValueOnce(
        jsonResponse({
          conversations: [
            {
              createdAt: "2026-08-03T02:30:00+00:00",
              id: "conversation-old",
              messageCount: 4,
              title: "Old conversation",
              updatedAt: "2026-08-03T02:35:00+00:00",
            },
          ],
          nextCursor: null,
        })
      );

    const controller = new AbortController();
    await expect(
      chatHistoryApi.listConversationMetas(controller.signal)
    ).resolves.toEqual([
      expect.objectContaining({
        attentionKind: "action",
        id: "conversation-new",
        stateVersion: 7,
        taskStatus: "blocked",
      }),
      expect.objectContaining({ id: "conversation-old" }),
    ]);
    expect(authFetch).toHaveBeenNthCalledWith(
      1,
      "/api/chat/conversations",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
    expect(authFetch).toHaveBeenNthCalledWith(
      2,
      "/api/chat/conversations?cursor=opaque%2B%2Fcursor%3D%3D",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
  });

  it("accepts null actor attention fields for conversations without active attention", () => {
    expect(
      decodeChatHistoryIndex({
        conversations: [
          {
            activeStepSummary: null,
            attentionKind: null,
            attentionSince: null,
            createdAt: "2026-08-04T02:30:00+00:00",
            id: "conversation-idle",
            messageCount: 2,
            taskStatus: null,
            title: "Idle conversation",
            updatedAt: "2026-08-04T02:35:00+00:00",
          },
        ],
        nextCursor: null,
      })
    ).toEqual({
      conversations: [
        {
          activeStepSummary: null,
          attentionKind: null,
          attentionSince: null,
          createdAt: "2026-08-04T02:30:00+00:00",
          id: "conversation-idle",
          messageCount: 2,
          taskStatus: null,
          title: "Idle conversation",
          updatedAt: "2026-08-04T02:35:00+00:00",
        },
      ],
      nextCursor: null,
    });
  });

  it("loads the canonical transcript and conditional current state", async () => {
    const transcript = {
      messages: [],
      projectionStatus: "current",
      stateVersion: 7,
    };
    const state = {
      snapshot: {
        actorId: "conversation/a",
        progressSequence: 8,
        scopeId: "scope-a",
        stateVersion: 8,
      },
      stateVersion: 8,
      status: "current",
      turnId: "turn/a",
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(jsonResponse(transcript))
      .mockResolvedValueOnce(jsonResponse(state));
    const controller = new AbortController();

    await expect(
      chatHistoryApi.loadConversation(" conversation/a ", controller.signal)
    ).resolves.toEqual(transcript);
    await expect(
      chatHistoryApi.loadConversationState(
        " conversation/a ",
        { afterStateVersion: 7, turnId: " turn/a " },
        controller.signal
      )
    ).resolves.toEqual(state);

    expect(authFetch).toHaveBeenNthCalledWith(
      1,
      "/api/chat/conversations/conversation%2Fa",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
    expect(authFetch).toHaveBeenNthCalledWith(
      2,
      "/api/chat/conversations/conversation%2Fa/state?afterStateVersion=7&turnId=turn%2Fa",
      {
        headers: { Accept: "application/json" },
        method: "GET",
        signal: controller.signal,
      }
    );
  });

  it("preserves the typed not_found state carried by HTTP 404", async () => {
    const payload = { status: "not_found" };
    (authFetch as jest.Mock).mockResolvedValue({
      json: jest.fn().mockResolvedValue(payload),
      ok: false,
      status: 404,
      statusText: "Not Found",
      text: jest.fn().mockResolvedValue(JSON.stringify(payload)),
    } as unknown as Response);

    await expect(
      chatHistoryApi.loadConversationState("conversation-alpha")
    ).resolves.toEqual({ status: "not_found" });
  });

  it("preserves documented transcript fields and extensible role strings", () => {
    expect(
      decodeChatConversationDetail({
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
        projectionStatus: "current",
        stateVersion: 7,
      })
    ).toEqual({
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
      projectionStatus: "current",
      stateVersion: 7,
    });
  });

  it("preserves pending status for an acknowledged conversation without a projected turn", () => {
    expect(
      decodeChatConversationDetail({
        messages: [],
        projectionStatus: "pending",
        stateVersion: 0,
      })
    ).toEqual({
      messages: [],
      projectionStatus: "pending",
      stateVersion: 0,
    });
  });

  it("rejects malformed successful response bodies explicitly", () => {
    expect(() => decodeChatHistoryIndex({ conversations: {} })).toThrow(
      ChatHistoryContractError
    );
    expect(() => decodeChatConversationDetail([])).toThrow(
      ChatHistoryContractError
    );
    expect(() =>
      decodeChatConversationDetail({
        messages: [],
        projectionStatus: "current",
        stateVersion: -1,
      })
    ).toThrow(expect.objectContaining({ path: "$conversation.stateVersion" }));
    expect(() =>
      decodeChatConversationDetail({
        messages: [],
        projectionStatus: "stale",
        stateVersion: 0,
      })
    ).toThrow(
      expect.objectContaining({ path: "$conversation.projectionStatus" })
    );
  });

  it("submits canonical deletion without parsing the accepted body", async () => {
    const json = jest.fn().mockRejectedValue(new Error("body is not needed"));
    (authFetch as jest.Mock).mockResolvedValue({
      json,
      ok: true,
      status: 202,
      statusText: "Accepted",
    } as unknown as Response);

    await expect(
      chatHistoryApi.deleteConversation(" conversation/a ")
    ).resolves.toBeUndefined();
    expect(json).not.toHaveBeenCalled();
    expect(authFetch).toHaveBeenCalledWith(
      "/api/chat/conversations/conversation%2Fa",
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
          code: "CONVERSATION_ACCESS_DENIED",
          message: "Conversation access denied.",
        })
      ),
    } as unknown as Response);

    await expect(chatHistoryApi.listConversationMetas()).rejects.toMatchObject({
      code: "CONVERSATION_ACCESS_DENIED",
      message: "Conversation access denied.",
      status: 403,
    });
  });
});
