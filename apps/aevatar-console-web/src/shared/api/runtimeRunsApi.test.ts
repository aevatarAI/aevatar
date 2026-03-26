import { runtimeRunsApi } from "./runtimeRunsApi";

describe("runtimeRunsApi", () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

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
        "service-1",
        {
          prompt: "Run it",
          workflowYamls: ["name: broken"],
        },
        new AbortController().signal
      )
    ).rejects.toThrow("invalid workflow yaml");
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
      runtimeRunsApi.resume("scope-1", "service-1", {
        actorId: "actor-1",
        runId: "run-1",
        stepId: "step-1",
        approved: true,
      })
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
      "service-1",
      {
        prompt: "Run it",
      },
      new AbortController().signal
    );

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/scopes/scope-1/services/service-1/invoke/chat:stream",
      expect.objectContaining({
        method: "POST",
      })
    );
  });
});
