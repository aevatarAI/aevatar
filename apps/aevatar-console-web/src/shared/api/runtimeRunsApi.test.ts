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
      runtimeRunsApi.resume({
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
});
