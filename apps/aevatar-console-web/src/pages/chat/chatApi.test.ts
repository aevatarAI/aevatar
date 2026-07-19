import { authFetch } from "@/shared/auth/fetch";
import { extractChatStreamArtifacts, startChatStream } from "./chatApi";

jest.mock("@/shared/auth/fetch", () => ({
  authFetch: jest.fn(),
}));

describe("chatApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("posts directly to /api/chat without body scope", async () => {
    (authFetch as jest.Mock).mockResolvedValue({
      ok: true,
    } as Response);

    const controller = new AbortController();
    const requestWithStaleScope = {
      prompt: " Create a workflow ",
      scopeId: " scope-a ",
      sessionId: "session-a",
    };

    await startChatStream(requestWithStaleScope, controller.signal);

    expect(authFetch).toHaveBeenCalledWith(
      "/api/chat",
      expect.objectContaining({
        body: JSON.stringify({
          prompt: "Create a workflow",
          sessionId: "session-a",
          workflow: "studio",
        }),
        method: "POST",
      })
    );
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
