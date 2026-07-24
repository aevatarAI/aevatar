import {
  USER_LLM_SAVE_OBSERVATION_DELAYS_MS,
  expectedCommittedUserLlmModel,
  observeUserLlmSave,
} from "./userLlmSaveObservation";

describe("userLlmSaveObservation", () => {
  beforeEach(() => {
    jest.useFakeTimers({ now: 0 });
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it("normalizes blank model intent against the exact selected option", () => {
    expect(
      expectedCommittedUserLlmModel({
        kind: "nyx_id_user_service",
        submittedModel: " ",
        optionDefaultModel: " gpt-service-default ",
      }),
    ).toBe("gpt-service-default");
    expect(
      expectedCommittedUserLlmModel({
        kind: "gateway",
        submittedModel: " ",
        optionDefaultModel: "must-not-be-used",
      }),
    ).toBe("");
  });

  it("observes at the fixed sequential delays and exhausts as accepted_unobserved", async () => {
    const observedAt: number[] = [];
    const observation = observeUserLlmSave({
      saveToken: 7,
      isCurrent: (saveToken) => saveToken === 7,
      read: async () => {
        observedAt.push(Date.now());
        return { observed: false };
      },
      isObserved: (sample) => sample.observed,
    });

    await jest.runAllTimersAsync();

    await expect(observation).resolves.toEqual({
      attempts: 7,
      phase: "accepted_unobserved",
    });
    expect(USER_LLM_SAVE_OBSERVATION_DELAYS_MS).toEqual([
      0, 250, 500, 1000, 2000, 3000, 5000,
    ]);
    expect(observedAt).toEqual([0, 250, 750, 1750, 3750, 6750, 11750]);
  });

  it("continues after transient reads and reaches observed without unrelated activity", async () => {
    const applied: string[] = [];
    const read = jest
      .fn<Promise<{ value: string }>, []>()
      .mockRejectedValueOnce(new Error("catalog unavailable"))
      .mockResolvedValueOnce({ value: "stale" })
      .mockResolvedValueOnce({ value: "observed" });
    const observation = observeUserLlmSave({
      saveToken: 3,
      isCurrent: (saveToken) => saveToken === 3,
      read,
      isObserved: (sample) => sample.value === "observed",
      onResponse: (sample) => applied.push(sample.value),
    });

    await jest.runAllTimersAsync();

    await expect(observation).resolves.toEqual({ attempts: 3, phase: "observed" });
    expect(applied).toEqual(["stale", "observed"]);
  });

  it("drops a response that belongs to an invalidated save token", async () => {
    let currentToken = 11;
    let resolveRead!: (value: { observed: boolean }) => void;
    const read = new Promise<{ observed: boolean }>((resolve) => {
      resolveRead = resolve;
    });
    const onResponse = jest.fn();
    const observation = observeUserLlmSave({
      saveToken: 11,
      isCurrent: (saveToken) => saveToken === currentToken,
      read: () => read,
      isObserved: (sample) => sample.observed,
      onResponse,
    });

    await jest.advanceTimersByTimeAsync(0);
    currentToken = 12;
    resolveRead({ observed: true });

    await expect(observation).resolves.toEqual({ attempts: 1, phase: "superseded" });
    expect(onResponse).not.toHaveBeenCalled();
  });

  it("reaches the fixed deadline when reads hang and ignores late responses and errors", async () => {
    const observedAt: number[] = [];
    const resolvers: Array<(value: { observed: boolean }) => void> = [];
    const rejecters: Array<(reason?: unknown) => void> = [];
    const signals: AbortSignal[] = [];
    const onResponse = jest.fn();
    const onTransientError = jest.fn();
    let settled:
      | { readonly attempts: number; readonly phase: string }
      | undefined;
    const observation = observeUserLlmSave({
      saveToken: 13,
      isCurrent: (saveToken) => saveToken === 13,
      read: (signal) =>
        new Promise<{ observed: boolean }>((resolve, reject) => {
          observedAt.push(Date.now());
          signals.push(signal);
          resolvers.push(resolve);
          rejecters.push(reject);
        }),
      isObserved: (sample) => sample.observed,
      onResponse,
      onTransientError,
    });
    void observation.then((result) => {
      settled = result;
    });

    await jest.advanceTimersByTimeAsync(0);
    expect(signals).toHaveLength(1);
    expect(signals[0]?.aborted).toBe(false);

    await jest.advanceTimersByTimeAsync(250);
    expect(signals).toHaveLength(2);
    expect(signals[0]?.aborted).toBe(true);
    expect(signals[1]?.aborted).toBe(false);

    await jest.advanceTimersByTimeAsync(11_500);

    expect(observedAt).toEqual([0, 250, 750, 1750, 3750, 6750, 11750]);
    expect(settled).toEqual({ attempts: 7, phase: "accepted_unobserved" });
    expect(signals).toHaveLength(7);
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    resolvers[0]?.({ observed: true });
    rejecters[1]?.(new Error("late failure"));
    await Promise.resolve();
    expect(onResponse).not.toHaveBeenCalled();
    expect(onTransientError).not.toHaveBeenCalled();
  });
});
