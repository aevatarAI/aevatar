import { authFetch } from "@/shared/auth/fetch";
import { extractChatStreamArtifacts, startChatStream } from "./chatApi";

jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

describe("chatApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("posts typed conversation identity and read-model watermark to /api/chat", async () => {
    (authFetch as jest.Mock).mockResolvedValue({
      ok: true,
    } as Response);

    const controller = new AbortController();
    await startChatStream(
      {
        prompt: " Create a workflow ",
        scopeId: " scope-a ",
        sessionId: "session-a",
        commandId: "command-a",
        conversation: {
          conversationId: " conversation-a ",
          minimumStateVersion: 7,
        },
      },
      controller.signal
    );

    expect(authFetch).toHaveBeenCalledWith(
      "/api/chat",
      expect.objectContaining({
        body: JSON.stringify({
          prompt: "Create a workflow",
          scopeId: "scope-a",
          sessionId: "session-a",
          workflow: "studio",
          commandId: "command-a",
          conversation: {
            conversationId: "conversation-a",
            minimumStateVersion: 7,
          },
        }),
        method: "POST",
      })
    );
  });

  it("extracts server chat context from structured frames", () => {
    expect(
      extractChatStreamArtifacts([
        {
          custom: {
            name: "aevatar.chat.context",
            payload: {
              fields: {
                conversationId: {
                  stringValue: "conversation-alpha",
                },
                scopeId: {
                  stringValue: "scope-a",
                },
                stateVersion: {
                  numberValue: 7,
                },
                turnId: {
                  stringValue: "turn-alpha",
                },
              },
            },
          },
        },
      ])
    ).toEqual({
      chatContext: {
        conversationId: "conversation-alpha",
        scopeId: "scope-a",
        stateVersion: 7,
        turnId: "turn-alpha",
      },
    });
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

  it("extracts protobuf Struct-like custom payloads", () => {
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
