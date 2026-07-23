import { authFetch } from "@/shared/auth/fetch";
import {
  ChatApiError,
  ChatRetryPreparationError,
  chatRequestMayHaveBeenAccepted,
  extractChatHistoryContext,
  extractChatStreamArtifacts,
  readChatStreamFrames,
  startChatStream,
  startChatStreamWithHistoryRefreshRetry,
} from "./chatApi";
import {
  ChatHistoryApiError,
  ChatHistoryContractError,
} from "./chatHistoryApi";

jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

const CHAT_HISTORY_CONTEXT_TYPE =
  "type.googleapis.com/aevatar.workflow.runs.WorkflowChatContextPayload";

function successfulStreamResponse(): Response {
  return {
    ok: true,
  } as Response;
}

function createSseResponse(body: string): Response {
  const encoder = new TextEncoder();
  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(body));
        controller.close();
      },
    }),
    ok: true,
  } as Response;
}

function missingConversationResponse(): Response {
  return {
    ok: false,
    status: 404,
    statusText: "Not Found",
    text: jest.fn().mockResolvedValue(
      JSON.stringify({
        code: "CONVERSATION_NOT_FOUND",
        message: "Conversation was not found.",
      })
    ),
  } as unknown as Response;
}

function historyReservationUnavailableResponse(): Response {
  return {
    ok: false,
    status: 503,
    statusText: "Service Unavailable",
    text: jest.fn().mockResolvedValue(
      JSON.stringify({
        code: "CHAT_HISTORY_RESERVATION_UNAVAILABLE",
        message: "Conversation history is still materializing.",
      })
    ),
  } as unknown as Response;
}

describe("chatApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("omits scope and history intent when starting an unpersisted chat", async () => {
    (authFetch as jest.Mock).mockResolvedValue(successfulStreamResponse());

    const controller = new AbortController();
    await startChatStream(
      {
        prompt: " Create a workflow ",
        sessionId: " session-a ",
      },
      controller.signal
    );

    expect(authFetch).toHaveBeenCalledWith(
      "/api/chat",
      expect.objectContaining({
        body: JSON.stringify({
          prompt: "Create a workflow",
          sessionId: "session-a",
          workflow: "studio",
        }),
        headers: {
          Accept: "text/event-stream",
          "Content-Type": "application/json",
        },
        method: "POST",
      })
    );
  });

  it("serializes new and continuing history conversations independently of sessionId", async () => {
    (authFetch as jest.Mock).mockResolvedValue(successfulStreamResponse());
    const controller = new AbortController();

    await startChatStream(
      {
        commandId: " create-command-a ",
        conversation: { conversationId: null },
        prompt: "New conversation",
        sessionId: "runtime-session-a",
      },
      controller.signal
    );
    await startChatStream(
      {
        conversation: {
          conversationId: " conversation-a ",
          minimumStateVersion: 7,
        },
        prompt: "Continue conversation",
        sessionId: "runtime-session-b",
      },
      controller.signal
    );

    const firstBody = JSON.parse((authFetch as jest.Mock).mock.calls[0][1].body);
    const secondBody = JSON.parse((authFetch as jest.Mock).mock.calls[1][1].body);
    expect(firstBody).toEqual({
      commandId: "create-command-a",
      conversation: { conversationId: null },
      prompt: "New conversation",
      sessionId: "runtime-session-a",
      workflow: "studio",
    });
    expect(secondBody).toEqual({
      conversation: {
        conversationId: "conversation-a",
        minimumStateVersion: 7,
      },
      prompt: "Continue conversation",
      sessionId: "runtime-session-b",
      workflow: "studio",
    });
  });

  it("rejects the stale nested create identity contract", async () => {
    await expect(
      startChatStream(
        {
          conversation: {
            conversationId: "conversation-a",
            createIdempotencyKey: "create-key-a",
            minimumStateVersion: 7,
          } as never,
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal
      )
    ).rejects.toMatchObject({
      code: "INVALID_CONVERSATION_INPUT",
      status: 400,
    });
    expect(authFetch).not.toHaveBeenCalled();
  });

  it("rejects a blank conversation id as a structured request error", async () => {
    const controller = new AbortController();

    await expect(
      startChatStream(
        {
          conversation: {
            conversationId: "   ",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        controller.signal
      )
    ).rejects.toMatchObject({
      code: "INVALID_CONVERSATION_ID",
      message: "Conversation id is invalid.",
      status: 400,
    });
    expect(authFetch).not.toHaveBeenCalled();
  });

  it("rejects invalid conversation shapes without leaking a TypeError", async () => {
    await expect(
      startChatStream(
        {
          conversation: "conversation-a" as never,
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal
      )
    ).rejects.toMatchObject({
      code: "INVALID_CONVERSATION_INPUT",
      message: "Conversation input is invalid.",
      status: 400,
    });
    expect(authFetch).not.toHaveBeenCalled();
  });

  it("throws structured errors returned before the SSE stream starts", async () => {
    (authFetch as jest.Mock).mockResolvedValue(missingConversationResponse());

    let error: unknown;
    try {
      await startChatStream(
        {
          conversation: {
            conversationId: "missing",
            minimumStateVersion: 3,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal
      );
    } catch (caught) {
      error = caught;
    }

    expect(error).toBeInstanceOf(ChatApiError);
    expect(error).toMatchObject({
      code: "CONVERSATION_NOT_FOUND",
      message: "Conversation was not found.",
      status: 404,
    });
  });

  it("keeps a non-success response definitive when its error body cannot be read", async () => {
    (authFetch as jest.Mock).mockResolvedValue({
      ok: false,
      status: 409,
      statusText: "Conflict",
      text: jest.fn().mockRejectedValue(new Error("Response body disconnected")),
    } as unknown as Response);

    await expect(
      startChatStream(
        {
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal
      )
    ).rejects.toMatchObject({
      message: "HTTP 409 Conflict",
      name: "ChatApiError",
      status: 409,
    });
  });

  it("refreshes the server watermark before retrying a 503 continuation", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(successfulStreamResponse());
    const refreshMinimumStateVersion = jest.fn().mockResolvedValue(8);

    const controller = new AbortController();
    await expect(
      startChatStreamWithHistoryRefreshRetry(
        {
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        controller.signal,
        { refreshMinimumStateVersion, retryDelaysMs: [0] }
      )
    ).resolves.toEqual(successfulStreamResponse());
    expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(1);
    expect(refreshMinimumStateVersion).toHaveBeenCalledWith(controller.signal);
    expect(authFetch).toHaveBeenCalledTimes(2);
    expect(
      JSON.parse((authFetch as jest.Mock).mock.calls[0][1].body).conversation
    ).toEqual({ conversationId: "conversation-a", minimumStateVersion: 7 });
    expect(
      JSON.parse((authFetch as jest.Mock).mock.calls[1][1].body).conversation
    ).toEqual({ conversationId: "conversation-a", minimumStateVersion: 8 });
  });

  it("does not retry a missing conversation or a new conversation", async () => {
    const refreshMinimumStateVersion = jest.fn().mockResolvedValue(8);
    (authFetch as jest.Mock).mockResolvedValue(missingConversationResponse());

    await expect(
      startChatStreamWithHistoryRefreshRetry(
        {
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal,
        { refreshMinimumStateVersion, retryDelaysMs: [0, 0] }
      )
    ).rejects.toMatchObject({ code: "CONVERSATION_NOT_FOUND", status: 404 });
    expect(authFetch).toHaveBeenCalledTimes(1);
    expect(refreshMinimumStateVersion).not.toHaveBeenCalled();

    jest.clearAllMocks();
    (authFetch as jest.Mock).mockResolvedValue(
      historyReservationUnavailableResponse()
    );
    await expect(
      startChatStreamWithHistoryRefreshRetry(
        {
          conversation: { conversationId: null },
          prompt: "Create",
          sessionId: "session-b",
        },
        new AbortController().signal,
        { refreshMinimumStateVersion, retryDelaysMs: [0, 0] }
      )
    ).rejects.toMatchObject({
      code: "CHAT_HISTORY_RESERVATION_UNAVAILABLE",
      status: 503,
    });
    expect(authFetch).toHaveBeenCalledTimes(1);
    expect(refreshMinimumStateVersion).not.toHaveBeenCalled();
  });

  it("accepts a zero refresh watermark and waits for 0 -> 6 -> 8 without sending a stale retry", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(historyReservationUnavailableResponse())
      .mockResolvedValueOnce(successfulStreamResponse());
    const refreshMinimumStateVersion = jest
      .fn()
      .mockResolvedValueOnce(0)
      .mockResolvedValueOnce(6)
      .mockResolvedValueOnce(8);

    await startChatStreamWithHistoryRefreshRetry(
      {
        conversation: {
          conversationId: "conversation-a",
          minimumStateVersion: 7,
        },
        prompt: "Continue",
        sessionId: "session-a",
      },
      new AbortController().signal,
      {
        refreshMinimumStateVersion,
        retryDelaysMs: [0, 0, 0],
      }
    );

    expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(3);
    expect(authFetch).toHaveBeenCalledTimes(2);
    expect(
      JSON.parse((authFetch as jest.Mock).mock.calls[1][1].body).conversation
    ).toEqual({ conversationId: "conversation-a", minimumStateVersion: 8 });
  });

  it("preserves the reservation error when refreshed history never catches up", async () => {
    (authFetch as jest.Mock).mockResolvedValueOnce(
      historyReservationUnavailableResponse()
    );
    const refreshMinimumStateVersion = jest.fn().mockResolvedValue(6);

    await expect(
      startChatStreamWithHistoryRefreshRetry(
        {
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal,
        {
          refreshMinimumStateVersion,
          retryDelaysMs: [0, 0],
        }
      )
    ).rejects.toMatchObject({
      code: "CHAT_HISTORY_RESERVATION_UNAVAILABLE",
      status: 503,
    });
    expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(2);
    expect(authFetch).toHaveBeenCalledTimes(1);
  });

  it("does not dispatch or lock after cancellation at the refresh boundary", async () => {
    (authFetch as jest.Mock).mockResolvedValueOnce(
      historyReservationUnavailableResponse()
    );
    const controller = new AbortController();
    const refreshMinimumStateVersion = jest.fn(async () => {
      controller.abort();
      return 8;
    });

    let thrown: unknown;
    try {
      await startChatStreamWithHistoryRefreshRetry(
        {
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
          prompt: "Continue",
          sessionId: "session-a",
        },
        controller.signal,
        { refreshMinimumStateVersion, retryDelaysMs: [0] }
      );
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(ChatRetryPreparationError);
    expect(chatRequestMayHaveBeenAccepted(thrown)).toBe(false);
    expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(1);
    expect(authFetch).toHaveBeenCalledTimes(1);
  });

  it.each([
    ["transport", new Error("History detail temporarily unavailable")],
    [
      "server",
      new ChatHistoryApiError("History detail temporarily unavailable", 500),
    ],
  ])(
    "uses the remaining retry budget after a transient %s history refresh failure",
    async (_kind, refreshError) => {
      (authFetch as jest.Mock)
        .mockResolvedValueOnce(historyReservationUnavailableResponse())
        .mockResolvedValueOnce(successfulStreamResponse());
      const refreshMinimumStateVersion = jest
        .fn()
        .mockRejectedValueOnce(refreshError)
        .mockResolvedValueOnce(8);

      await expect(
        startChatStreamWithHistoryRefreshRetry(
          {
            conversation: {
              conversationId: "conversation-a",
              minimumStateVersion: 7,
            },
            prompt: "Continue",
            sessionId: "session-a",
          },
          new AbortController().signal,
          {
            refreshMinimumStateVersion,
            retryDelaysMs: [0, 0],
          }
        )
      ).resolves.toEqual(successfulStreamResponse());
      expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(2);
      expect(authFetch).toHaveBeenCalledTimes(2);
    }
  );

  it.each([
    [
      "a non-retryable history response",
      new ChatHistoryApiError("History request was rejected", 422),
    ],
    [
      "a malformed history contract",
      new ChatHistoryContractError(
        "$conversation.stateVersion",
        "a non-negative safe integer"
      ),
    ],
  ])(
    "preserves %s as a definitive retry preparation failure",
    async (_kind, refreshError) => {
      (authFetch as jest.Mock).mockResolvedValueOnce(
        historyReservationUnavailableResponse()
      );
      const refreshMinimumStateVersion = jest
        .fn()
        .mockRejectedValue(refreshError);

      let error: unknown;
      try {
        await startChatStreamWithHistoryRefreshRetry(
          {
            conversation: {
              conversationId: "conversation-a",
              minimumStateVersion: 7,
            },
            prompt: "Continue",
            sessionId: "session-a",
          },
          new AbortController().signal,
          {
            refreshMinimumStateVersion,
            retryDelaysMs: [0, 0],
          }
        );
      } catch (caught) {
        error = caught;
      }

      expect(error).toBeInstanceOf(ChatRetryPreparationError);
      expect(error).toMatchObject({
        cause: refreshError,
        mayHaveBeenAccepted: false,
        message: refreshError.message,
      });
      expect(refreshMinimumStateVersion).toHaveBeenCalledTimes(1);
      expect(authFetch).toHaveBeenCalledTimes(1);
    }
  );

  it("extracts the exact flat chat history Any context", () => {
    const frame = {
      custom: {
        name: "aevatar.chat.context",
        payload: {
          "@type": CHAT_HISTORY_CONTEXT_TYPE,
          conversationId: "conversation-a",
          scopeId: "scope-a",
          stateVersion: 7,
          turnId: "turn-a",
        },
      },
      timestamp: 1784255700000,
    };

    expect(extractChatHistoryContext(frame)).toEqual({
      conversationId: "conversation-a",
      scopeId: "scope-a",
      stateVersion: 7,
      turnId: "turn-a",
    });
    expect(extractChatStreamArtifacts([frame])).toEqual({
      chatHistoryContext: {
        conversationId: "conversation-a",
        scopeId: "scope-a",
        stateVersion: 7,
        turnId: "turn-a",
      },
    });

    expect(
      extractChatHistoryContext({
        custom: {
          name: "aevatar.run.context",
          payload: frame.custom.payload,
        },
      })
    ).toBeNull();
    expect(
      extractChatHistoryContext({
        custom: {
          name: "aevatar.chat.context",
          payload: {
            ...frame.custom.payload,
            "@type": "type.googleapis.com/example.WrongPayload",
          },
        },
      })
    ).toBeNull();
    expect(
      extractChatHistoryContext({
        custom: {
          name: "aevatar.chat.context",
          payload: {
            fields: {
              conversationId: { stringValue: "conversation-a" },
            },
          },
        },
      })
    ).toBeNull();
  });

  it("keeps SSE keepalive comments out of parsed data frames", async () => {
    const raw = {
      runFinished: {
        result: { output: "Done" },
      },
    };
    const response = createSseResponse(
      `: keepalive\n\ndata: ${JSON.stringify(raw)}\n\n: keepalive\n\n`
    );
    const frames = [];

    for await (const frame of readChatStreamFrames(response)) {
      frames.push(frame);
    }

    expect(frames).toHaveLength(1);
    expect(frames[0].raw).toEqual(raw);
  });

  it("extracts usage and studio target only from structured frames", () => {
    expect(
      extractChatStreamArtifacts([
        {
          runFinished: {
            result: {
              output:
                "Created team-a/member-a/workflow-a, but this text alone should not be parsed.",
            },
          },
        },
      ])
    ).toEqual({});

    expect(
      extractChatStreamArtifacts([
        {
          runFinished: {
            result: {
              memberId: "member-a",
              output: "Created.",
              scopeId: "scope-a",
              teamId: "team-a",
              usage: {
                completionTokens: 3,
                promptTokens: 4,
                totalTokens: 7,
              },
              workflowId: "workflow-a",
            },
          },
        },
      ])
    ).toEqual({
      target: {
        memberId: "member-a",
        scopeId: "scope-a",
        teamId: "team-a",
        workflowId: "workflow-a",
      },
      usage: {
        completionTokens: 3,
        promptTokens: 4,
        totalTokens: 7,
      },
    });
  });

  it("extracts protobuf Struct-like custom payloads for existing run artifacts", () => {
    expect(
      extractChatStreamArtifacts([
        {
          custom: {
            name: "aevatar.run.context",
            payload: {
              fields: {
                actorId: {
                  stringValue: "run-a",
                },
                scopeId: {
                  stringValue: "scope-a",
                },
                studioUrl: {
                  stringValue: "/scopes/scope-a/teams/team-a/members/member-a/workflow",
                },
              },
            },
          },
        },
      ])
    ).toEqual({
      target: {
        runId: "run-a",
        scopeId: "scope-a",
        studioUrl: "/scopes/scope-a/teams/team-a/members/member-a/workflow",
      },
    });
  });
});
