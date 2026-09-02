export const USER_LLM_SAVE_OBSERVATION_DELAYS_MS = [
  0, 250, 500, 1000, 2000, 3000, 5000,
] as const;
export const USER_LLM_SAVE_FINAL_ATTEMPT_SETTLE_MS = 5000;

export type UserLlmSaveObservationPhase =
  | "observed"
  | "accepted_unobserved"
  | "superseded";

export type PendingUserLlmSave<TDraft> = {
  readonly saveToken: number;
  readonly submittedRevision: number;
  readonly submittedDraft: TDraft;
  readonly expectedCommittedDraft: TDraft;
  readonly selectionLabel: string;
  readonly commandId?: string;
  readonly phase: "saving" | "accepted" | "accepted_unobserved";
};

type ObserveUserLlmSaveInput<TSample> = {
  readonly saveToken: number;
  readonly isCurrent: (saveToken: number) => boolean;
  readonly read: (signal: AbortSignal) => Promise<TSample>;
  readonly isObserved: (sample: TSample) => boolean;
  readonly onResponse?: (sample: TSample) => void;
  readonly onTransientError?: (error: unknown) => void;
};

export async function observeUserLlmSave<TSample>(
  input: ObserveUserLlmSaveInput<TSample>,
): Promise<{ readonly attempts: number; readonly phase: UserLlmSaveObservationPhase }> {
  return new Promise((resolve) => {
    let active = true;
    let attempts = 0;
    let elapsedMilliseconds = 0;
    let attemptGeneration = 0;
    let currentController: AbortController | null = null;
    const timers: number[] = [];

    const finish = (phase: UserLlmSaveObservationPhase) => {
      if (!active) {
        return;
      }

      active = false;
      attemptGeneration += 1;
      timers.forEach((timer) => {
        window.clearTimeout(timer);
      });
      currentController?.abort();
      currentController = null;
      resolve({ attempts, phase });
    };

    const launchRead = () => {
      if (!active) {
        return;
      }
      if (!input.isCurrent(input.saveToken)) {
        finish("superseded");
        return;
      }

      attempts += 1;
      currentController?.abort();
      currentController = null;
      attemptGeneration += 1;
      const generation = attemptGeneration;
      const controller = new AbortController();
      currentController = controller;
      let pendingRead: Promise<TSample>;
      try {
        pendingRead = input.read(controller.signal);
      } catch (error) {
        if (currentController === controller) {
          currentController = null;
        }
        if (!active || generation !== attemptGeneration || controller.signal.aborted) {
          return;
        }
        if (!input.isCurrent(input.saveToken)) {
          finish("superseded");
          return;
        }
        input.onTransientError?.(error);
        return;
      }

      void pendingRead.then(
        (sample) => {
          if (
            !active ||
            generation !== attemptGeneration ||
            controller.signal.aborted ||
            currentController !== controller
          ) {
            return;
          }
          currentController = null;
          if (!input.isCurrent(input.saveToken)) {
            finish("superseded");
            return;
          }

          input.onResponse?.(sample);
          if (input.isObserved(sample)) {
            finish("observed");
          }
        },
        (error) => {
          if (
            !active ||
            generation !== attemptGeneration ||
            controller.signal.aborted ||
            currentController !== controller
          ) {
            return;
          }
          currentController = null;
          if (!input.isCurrent(input.saveToken)) {
            finish("superseded");
            return;
          }
          input.onTransientError?.(error);
        },
      );
    };

    for (const delayMilliseconds of USER_LLM_SAVE_OBSERVATION_DELAYS_MS) {
      elapsedMilliseconds += delayMilliseconds;
      timers.push(window.setTimeout(launchRead, elapsedMilliseconds));
    }
    timers.push(
      window.setTimeout(() => {
        finish(
          input.isCurrent(input.saveToken)
            ? "accepted_unobserved"
            : "superseded",
        );
      }, elapsedMilliseconds + USER_LLM_SAVE_FINAL_ATTEMPT_SETTLE_MS),
    );
  });
}
