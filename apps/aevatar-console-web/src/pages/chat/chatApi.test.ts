import { authFetch } from "@/shared/auth/fetch";
import {
  ChatApiError,
  extractChatHistoryContext,
  extractChatStreamArtifacts,
  readChatStreamFrames,
  startChatStream,
  startChatStreamWithProjectionRetry,
} from "./chatApi";

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

  it("serializes new and continuing history conversations with operation command ids", async () => {
    (authFetch as jest.Mock).mockResolvedValue(successfulStreamResponse());
    const controller = new AbortController();

    await startChatStream(
      {
        commandId: " create-key-a ",
        conversation: {
          conversationId: null,
          createIdempotencyKey: " create-key-a ",
        },
        prompt: "New conversation",
        sessionId: "runtime-session-a",
      },
      controller.signal
    );
    await startChatStream(
      {
        commandId: " turn-command-b ",
        conversation: { conversationId: " conversation-a " },
        prompt: "Continue conversation",
        sessionId: "runtime-session-b",
      },
      controller.signal
    );

    const firstBody = JSON.parse((authFetch as jest.Mock).mock.calls[0][1].body);
    const secondBody = JSON.parse((authFetch as jest.Mock).mock.calls[1][1].body);
    expect(firstBody).toEqual({
      commandId: "create-key-a",
      conversation: { conversationId: null },
      prompt: "New conversation",
      sessionId: "runtime-session-a",
      workflow: "studio",
    });
    expect(secondBody).toEqual({
      commandId: "turn-command-b",
      conversation: { conversationId: "conversation-a" },
      prompt: "Continue conversation",
      sessionId: "runtime-session-b",
      workflow: "studio",
    });
  });

  it("rejects a create key on a continuation request", async () => {
    await expect(
      startChatStream(
        {
          conversation: {
            conversationId: "conversation-a",
            createIdempotencyKey: "create-key-a",
          },
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
          conversation: { conversationId: "   " },
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
          conversation: { conversationId: "missing" },
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

  it("retries only a continuing conversation while its projection is unavailable", async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(missingConversationResponse())
      .mockResolvedValueOnce(missingConversationResponse())
      .mockResolvedValueOnce(successfulStreamResponse());

    await expect(
      startChatStreamWithProjectionRetry(
        {
          conversation: { conversationId: "conversation-a" },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal,
        [0, 0, 0]
      )
    ).resolves.toEqual(successfulStreamResponse());
    expect(authFetch).toHaveBeenCalledTimes(3);
  });

  it("caps continuation retries and never retries a new conversation", async () => {
    (authFetch as jest.Mock).mockImplementation(async () =>
      missingConversationResponse()
    );

    await expect(
      startChatStreamWithProjectionRetry(
        {
          conversation: { conversationId: "conversation-a" },
          prompt: "Continue",
          sessionId: "session-a",
        },
        new AbortController().signal,
        [0, 0, 0]
      )
    ).rejects.toMatchObject({ code: "CONVERSATION_NOT_FOUND", status: 404 });
    expect(authFetch).toHaveBeenCalledTimes(3);

    jest.clearAllMocks();
    (authFetch as jest.Mock).mockResolvedValue(missingConversationResponse());
    await expect(
      startChatStreamWithProjectionRetry(
        {
          conversation: {},
          prompt: "Create",
          sessionId: "session-b",
        },
        new AbortController().signal,
        [0, 0, 0]
      )
    ).rejects.toMatchObject({ code: "CONVERSATION_NOT_FOUND", status: 404 });
    expect(authFetch).toHaveBeenCalledTimes(1);
  });

  it("extracts the exact flat chat history Any context", () => {
    const frame = {
      custom: {
        name: "aevatar.chat.context",
        payload: {
          "@type": CHAT_HISTORY_CONTEXT_TYPE,
          conversationId: "conversation-a",
          scopeId: "scope-a",
          turnId: "turn-a",
        },
      },
      timestamp: 1784255700000,
    };

    expect(extractChatHistoryContext(frame)).toEqual({
      conversationId: "conversation-a",
      scopeId: "scope-a",
      turnId: "turn-a",
    });
    expect(extractChatStreamArtifacts([frame])).toEqual({
      chatHistoryContext: {
        conversationId: "conversation-a",
        scopeId: "scope-a",
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
