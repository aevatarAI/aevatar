export const USER_LLM_SAVE_OBSERVATION_DELAYS_MS = [
  0, 250, 500, 1000, 2000, 3000, 5000,
] as const;

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
  readonly phase: "saving" | "accepted" | "accepted_unobserved";
};

type ObserveUserLlmSaveInput<TSample> = {
  readonly saveToken: number;
  readonly isCurrent: (saveToken: number) => boolean;
  readonly read: () => Promise<TSample>;
  readonly isObserved: (sample: TSample) => boolean;
  readonly onResponse?: (sample: TSample) => void;
  readonly onTransientError?: (error: unknown) => void;
};

export function expectedCommittedUserLlmModel(input: {
  readonly kind: "gateway" | "nyx_id_user_service";
  readonly submittedModel: string;
  readonly optionDefaultModel?: string | null;
}): string {
  const explicitModel = input.submittedModel.trim();
  if (explicitModel) {
    return explicitModel;
  }

  return input.kind === "gateway"
    ? ""
    : String(input.optionDefaultModel ?? "").trim();
}

function delay(milliseconds: number): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, milliseconds);
  });
}

export async function observeUserLlmSave<TSample>(
  input: ObserveUserLlmSaveInput<TSample>,
): Promise<{ readonly attempts: number; readonly phase: UserLlmSaveObservationPhase }> {
  let attempts = 0;

  for (const milliseconds of USER_LLM_SAVE_OBSERVATION_DELAYS_MS) {
    if (!input.isCurrent(input.saveToken)) {
      return { attempts, phase: "superseded" };
    }

    await delay(milliseconds);
    if (!input.isCurrent(input.saveToken)) {
      return { attempts, phase: "superseded" };
    }

    attempts += 1;
    try {
      const sample = await input.read();
      if (!input.isCurrent(input.saveToken)) {
        return { attempts, phase: "superseded" };
      }

      input.onResponse?.(sample);
      if (input.isObserved(sample)) {
        return { attempts, phase: "observed" };
      }
    } catch (error) {
      if (!input.isCurrent(input.saveToken)) {
        return { attempts, phase: "superseded" };
      }
      input.onTransientError?.(error);
    }
  }

  return { attempts, phase: "accepted_unobserved" };
}
