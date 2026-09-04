import { type RuntimeRunsApiError, runtimeRunsApi } from "./runtimeRunsApi";
import {
  encodeAppScriptCommandBase64,
  encodeStringValueBase64,
  getAppScriptCommandEndpointId,
  getAppScriptCommandTypeUrl,
  getStringValueTypeUrl,
} from "@/shared/runs/protobufPayload";

describe("runtimeRunsApi", () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  function readBlobText(blob: Blob): Promise<string> {
    if (typeof blob.text === "function") {
      return blob.text();
    }

    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onerror = () => reject(reader.error);
      reader.onload = () => resolve(String(reader.result ?? ""));
      reader.readAsText(blob);
    });
  }

  it("surfaces non-OK streamChat responses from the runtime boundary", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 400,
      text: async () => '{"message":"invalid workflow yaml"}',
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.streamChat(
        "scope-1",
        {
          prompt: "Run it",
          workflowYamls: ["name: broken"],
        },
        new AbortController().signal,
        { serviceId: "service-1" }
      )
    ).rejects.toThrow("invalid workflow yaml");
  });

  it("collapses HTML error pages for streaming runtime responses", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 502,
      statusText: "Bad Gateway",
      text: async () => `<!DOCTYPE html>
<html lang="en-US">
  <head>
    <title>runtime gateway | 502: Bad gateway</title>
  </head>
  <body>
    <h1>Bad gateway</h1>
  </body>
</html>`,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.streamChat(
        "scope-1",
        {
          prompt: "Run it",
        },
        new AbortController().signal
      )
    ).rejects.toThrow("HTTP 502 Bad Gateway");
  });

  it("collapses HTML error pages for JSON runtime requests", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 502,
      statusText: "Bad Gateway",
      text: async () => `<!DOCTYPE html>
<html lang="en-US">
  <head>
    <title>runtime gateway | 502: Bad gateway</title>
  </head>
  <body>
    <h1>Bad gateway</h1>
  </body>
</html>`,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.resume("scope-1", {
        actorId: "actor-1",
        runId: "run-1",
        stepId: "step-1",
        approved: true,
      })
    ).rejects.toThrow("HTTP 502 Bad Gateway");
  });

  it("decodes resume responses from the runtime boundary", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        accepted: true,
        actorId: "actor-1",
        runId: "run-1",
        stepId: "step-1",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.resume("scope-1", {
        actorId: "actor-1",
        runId: "run-1",
        stepId: "step-1",
        approved: true,
      }, { serviceId: "service-1" })
    ).resolves.toEqual({
      accepted: true,
      actorId: "actor-1",
      runId: "run-1",
      stepId: "step-1",
    });
  });

  it("routes streamChat through the scoped service stream endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamChat(
      "scope-1",
      {
        prompt: "Run it",
        workflow: "direct",
        agentId: "actor-1",
        workflowYamls: ["name: direct"],
        metadata: { source: "runs" },
      },
      new AbortController().signal,
      { serviceId: "service-1" }
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/services/service-1/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
    expect(JSON.parse(String(init.body))).toEqual({
      prompt: "Run it",
      headers: { source: "runs" },
    });
  });

  it("routes streamChat through the member stream endpoint when memberId is provided", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamChat(
      "scope-1",
      {
        prompt: "Run it",
      },
      new AbortController().signal,
      { memberId: "joker", serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/members/joker/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
  });

  it("routes streamTeamChat through the scoped team stream endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamTeamChat(
      "scope-1",
      "team-alpha",
      {
        prompt: "Test the team",
        metadata: { source: "team-detail" },
        sessionId: "session-1",
      } as Parameters<typeof runtimeRunsApi.streamTeamChat>[2],
      new AbortController().signal
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/teams/team-alpha/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
    expect(JSON.parse(String(init.body))).toEqual({
      prompt: "Test the team",
      sessionId: "session-1",
      headers: { source: "team-detail" },
    });
  });

  it("surfaces non-OK streamTeamChat responses from the team runtime boundary", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 409,
      statusText: "Conflict",
      text: async () =>
        '{"code":"TEAM_ENTRY_MEMBER_NOT_CONFIGURED","message":"team has no entry member configured."}',
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    const act = runtimeRunsApi.streamTeamChat(
      "scope-1",
      "team-alpha",
      {
        prompt: "Test the team",
      },
      new AbortController().signal
    );

    await expect(act).rejects.toThrow("team has no entry member configured.");
    await expect(act).rejects.toMatchObject({
      code: "TEAM_ENTRY_MEMBER_NOT_CONFIGURED",
      name: "RuntimeRunsApiError",
      status: 409,
    } satisfies Partial<RuntimeRunsApiError>);
  });

  it("keeps router team-not-found errors distinct from missing routes", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status: 404,
      statusText: "Not Found",
      text: async () => '{"code":"TEAM_NOT_FOUND","message":"team not found."}',
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.streamTeamChat(
        "scope-1",
        "team-missing",
        {
          prompt: "Test the team",
        },
        new AbortController().signal
      )
    ).rejects.toMatchObject({
      code: "TEAM_NOT_FOUND",
      message: "team not found.",
      status: 404,
    });
  });

  it("routes streamChat through the team stream endpoint when teamId is provided", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamChat(
      "scope-1",
      {
        prompt: "Run it",
      },
      new AbortController().signal,
      { teamId: "team-1", serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/teams/team-1/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
  });

  it("routes scoped streamChat through the scope default service endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamChat(
      "scope-1",
      {
        prompt: "Run it",
        metadata: { source: "runs" },
      },
      new AbortController().signal
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
    expect(JSON.parse(String(init.body))).toEqual({
      prompt: "Run it",
      headers: { source: "runs" },
    });
  });

  it("forwards the sessionId when chat streaming resumes an existing conversation", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamChat(
      "scope-1",
      {
        prompt: "Resume the conversation",
        sessionId: "conversation-1",
      } as Parameters<typeof runtimeRunsApi.streamChat>[1],
      new AbortController().signal,
      { serviceId: "service-1" }
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({
      prompt: "Resume the conversation",
      sessionId: "conversation-1",
    });
  });

  it("sends member stream endpoint file inputs as multipart form data", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);
    const image = new File(["image-bytes"], "cat.png", { type: "image/png" });

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamEndpoint(
      "scope-1",
      {
        endpointId: "chat",
        files: [image],
        headers: { source: "invoke-page" },
        prompt: "Describe this image",
        sessionId: "session-1",
      },
      new AbortController().signal,
      { memberId: "member-alpha", serviceId: "svc-alpha" },
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    const formData = init.body as FormData;
    const payload = formData.get("payload");
    const uploadedFile = formData.get("file") as File;

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/members/member-alpha/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      }),
    );
    expect(headers.get("Accept")).toBe("text/event-stream");
    expect(headers.has("Content-Type")).toBe(false);
    expect(formData).toBeInstanceOf(FormData);
    expect(typeof payload).toBe("string");
    expect(JSON.parse(String(payload))).toEqual({
      prompt: "Describe this image",
      sessionId: "session-1",
      headers: { source: "invoke-page" },
    });
    expect(uploadedFile.name).toBe("cat.png");
    expect(uploadedFile.type).toBe("image/png");
    expect(await readBlobText(uploadedFile)).toBe("image-bytes");
  });

  it("routes draft runs through the scope draft endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamDraftRun(
      "scope-1",
      {
        prompt: "Run draft",
        workflowYamls: ["name: draft"],
        metadata: { source: "studio" },
      },
      new AbortController().signal
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/workflow/draft-run",
      expect.objectContaining({
        body: JSON.stringify({
          eventFormat: "agui",
          prompt: "Run draft",
          workflowYamls: ["name: draft"],
          headers: {
            source: "studio",
          },
        }),
        headers: {
          "Content-Type": "application/json",
          Accept: "text/event-stream",
        },
        method: "POST",
      })
    );
  });

  it("sends draft run file inputs as multipart form data", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
    } satisfies Partial<Response>);
    const image = new File(["image-bytes"], "draft.png", { type: "image/png" });

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.streamDraftRun(
      "scope-1",
      {
        files: [image],
        metadata: { source: "studio-draft" },
        prompt: "Describe this image",
        workflowYamls: ["name: draft"],
      },
      new AbortController().signal,
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    const formData = init.body as FormData;
    const payload = formData.get("payload");
    const uploadedFile = formData.get("file") as File;

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/workflow/draft-run",
      expect.objectContaining({
        method: "POST",
      }),
    );
    expect(headers.get("Accept")).toBe("text/event-stream");
    expect(headers.has("Content-Type")).toBe(false);
    expect(formData).toBeInstanceOf(FormData);
    expect(typeof payload).toBe("string");
    expect(JSON.parse(String(payload))).toEqual({
      eventFormat: "agui",
      prompt: "Describe this image",
      workflowYamls: ["name: draft"],
      headers: {
        source: "studio-draft",
      },
    });
    expect(uploadedFile.name).toBe("draft.png");
    expect(uploadedFile.type).toBe("image/png");
    expect(await readBlobText(uploadedFile)).toBe("image-bytes");
  });

  it("routes getRunSummary through the scope run endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        actorId: "actor-1",
        runId: "run-1",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.getRunSummary(
      "scope-1",
      "run-1"
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/runs/run-1?",
      expect.objectContaining({
        method: "GET",
        headers: {
          Accept: "application/json",
        },
      })
    );
  });

  it("routes workflow actor current-state queries through the workflow actor endpoint", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        actorId: "scope-workflow:scope-1:run:run-1",
        lastOutput: "Done",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.getWorkflowActorCurrentState("scope-workflow:scope-1:run:run-1");

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/workflow-actors/scope-workflow%3Ascope-1%3Arun%3Arun-1/current-state",
      expect.objectContaining({
        method: "GET",
        headers: {
          Accept: "application/json",
        },
      })
    );
  });

  it("routes scoped getRunSummary through the service run endpoint with actor filters", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        actorId: "actor-1",
        runId: "run-1",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.getRunSummary(
      "scope-1",
      "run-1",
      { serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/services/service-1/runs/run-1?",
      expect.objectContaining({
        method: "GET",
      })
    );
  });

  it("routes getRunSummary through the member run endpoint when memberId is provided", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        actorId: "actor-1",
        runId: "run-1",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.getRunSummary("scope-1", "run-1", {
      memberId: "joker",
      serviceId: "service-1",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/members/joker/runs/run-1?",
      expect.objectContaining({
        method: "GET",
      })
    );
  });

  it("forwards actor filters when resolving run summaries", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        actorId: "actor-1",
        runId: "run-1",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.getRunSummary("scope-1", "run-1", {
      actorId: "actor-1",
      serviceId: "service-1",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/services/service-1/runs/run-1?actorId=actor-1",
      expect.objectContaining({
        method: "GET",
      })
    );
  });

  it("routes generic endpoint invokes through the scope endpoint path with a default string payload", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        requestId: "cmd-1",
        targetActorId: "actor-1",
        endpointId: "run",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.invokeEndpoint("scope-1", {
      endpointId: "run",
      prompt: "Launch the endpoint",
    });

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/invoke/run",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          payloadTypeUrl: getStringValueTypeUrl(),
          payloadBase64: encodeStringValueBase64("Launch the endpoint"),
        }),
      })
    );
  });

  it("routes scoped generic endpoint invokes through the service endpoint path", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        requestId: "cmd-2",
        targetActorId: "actor-2",
        endpointId: "run",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.invokeEndpoint(
      "scope-1",
      {
        endpointId: "run",
        prompt: "Launch the endpoint",
        commandId: "cmd-2",
      },
      { serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/services/service-1/invoke/run",
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          commandId: "cmd-2",
          correlationId: "cmd-2",
          payloadTypeUrl: getStringValueTypeUrl(),
          payloadBase64: encodeStringValueBase64("Launch the endpoint"),
        }),
      })
    );
  });

  it("routes generic endpoint invokes through the member endpoint path when memberId is provided", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        requestId: "cmd-2",
        targetActorId: "actor-2",
        endpointId: "run",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.invokeEndpoint(
      "scope-1",
      {
        endpointId: "run",
        prompt: "Launch the endpoint",
        commandId: "cmd-2",
      },
      { memberId: "joker", serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/members/joker/invoke/run",
      expect.objectContaining({
        method: "POST",
      })
    );
  });

  it("routes generic endpoint invokes through the team endpoint path when teamId is provided", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        requestId: "cmd-2",
        targetActorId: "actor-2",
        endpointId: "run",
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.invokeEndpoint(
      "scope-1",
      {
        endpointId: "run",
        prompt: "Launch the endpoint",
        commandId: "cmd-2",
      },
      { teamId: "team-1", serviceId: "service-1" }
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/teams/team-1/invoke/run",
      expect.objectContaining({
        method: "POST",
      })
    );
  });

  it("encodes script invokes with AppScriptCommand payloads on the scope-first endpoint path", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      json: async () => ({
        requestId: "cmd-3",
        targetActorId: "runtime-1",
        endpointId: getAppScriptCommandEndpointId(),
      }),
    } satisfies Partial<Response>);

    global.fetch = fetchMock as typeof global.fetch;

    await runtimeRunsApi.invokeEndpoint("scope-1", {
      endpointId: getAppScriptCommandEndpointId(),
      prompt: "print('hello')",
      payloadTypeUrl: getAppScriptCommandTypeUrl(),
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const body = JSON.parse(String(init.body));

    expect(fetchMock).toHaveBeenCalledWith(
      `/api/scopes/scope-1/invoke/${encodeURIComponent(
        getAppScriptCommandEndpointId()
      )}`,
      expect.objectContaining({
        method: "POST",
      })
    );
    expect(body.payloadTypeUrl).toBe(getAppScriptCommandTypeUrl());
    expect(body.commandId).toEqual(expect.any(String));
    expect(body.correlationId).toBe(body.commandId);
    expect(body.payloadBase64).toBe(
      encodeAppScriptCommandBase64({
        commandId: body.commandId,
        input: "print('hello')",
      })
    );
  });

  it("rejects custom payload type URLs without explicit payload bytes", async () => {
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      runtimeRunsApi.invokeEndpoint("scope-1", {
        endpointId: "orders.run",
        prompt: "Launch the endpoint",
        payloadTypeUrl: "type.googleapis.com/example.CustomCommand",
      })
    ).rejects.toThrow(
      "payloadBase64 is required for payloadTypeUrl 'type.googleapis.com/example.CustomCommand'."
    );

    expect(fetchMock).not.toHaveBeenCalled();
  });
});
