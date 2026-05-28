// Refactor (iter160/cluster-1200):
//   Old pattern: Studio member binding run + Script save observation each kept
//                page-local fixed wait/status mapping; timing and normalized state diverged.
//   New principle: shared helper unifies accepted receipt / observation result /
//                  query freshness / terminal failure / stale-pending normalized states;
//                  injectable scheduler keeps tests deterministic without backend contracts.
export type AsyncOperationStatus =
  | 'accepted'
  | 'pending'
  | 'observed'
  | 'failed'
  | 'stale';

export type AsyncOperationFreshness =
  | 'accepted-only'
  | 'not-yet-materialized'
  | 'observed'
  | 'stale';

export type AsyncOperationSeverity = 'success' | 'info' | 'warning' | 'error';

export type AsyncOperationObservationStatus =
  | 'pending'
  | 'applied'
  | 'succeeded'
  | 'failed'
  | 'rejected'
  | 'unknown';

export type AsyncOperationState<TObservation = unknown> = {
  readonly status: AsyncOperationStatus;
  readonly freshness: AsyncOperationFreshness;
  readonly severity: AsyncOperationSeverity;
  readonly terminal: boolean;
  readonly stateVersion: number | null;
  readonly message: string;
  readonly observation: TObservation | null;
};

export type NormalizeAsyncOperationStateInput<TObservation = unknown> = {
  readonly accepted?: boolean;
  readonly observation?: TObservation | null;
  readonly observationStatus?: AsyncOperationObservationStatus | null;
  readonly terminal?: boolean | null;
  readonly stateVersion?: number | null;
  readonly stale?: boolean;
  readonly message?: string | null;
  readonly fallbackMessage?: string;
};

export type ProbeAsyncOperationOptions<TObservation> = {
  readonly maxAttempts: number;
  readonly read: () => Promise<TObservation>;
  readonly isTerminal: (observation: TObservation) => boolean;
  readonly waitForNextAttempt?: (attempt: number) => Promise<void>;
  readonly canRetryError?: (error: unknown) => boolean;
};

export type AsyncOperationProbeResult<TObservation> = {
  readonly observation: TObservation | null;
  readonly error: unknown | null;
  readonly exhausted: boolean;
};

function isFiniteStateVersion(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function normalizeObservationStatus(
  value: AsyncOperationObservationStatus | null | undefined,
): AsyncOperationObservationStatus {
  return value || 'unknown';
}

export function normalizeAsyncOperationState<TObservation = unknown>(
  input: NormalizeAsyncOperationStateInput<TObservation>,
): AsyncOperationState<TObservation> {
  const observationStatus = normalizeObservationStatus(input.observationStatus);
  const observation = input.observation ?? null;
  const hasObservation = observation != null;
  const hasStateVersion = isFiniteStateVersion(input.stateVersion);
  const stateVersion = hasStateVersion ? input.stateVersion : null;
  const isFailure =
    observationStatus === 'failed' || observationStatus === 'rejected';
  const isObservedSuccess =
    observationStatus === 'applied' || observationStatus === 'succeeded';
  const terminal = Boolean(input.terminal || isFailure || isObservedSuccess);
  const message = input.message || input.fallbackMessage || '';

  if (isFailure) {
    return {
      status: 'failed',
      freshness: hasObservation || terminal || hasStateVersion
        ? 'observed'
        : 'accepted-only',
      severity: 'error',
      terminal: true,
      stateVersion,
      message,
      observation,
    };
  }

  if (isObservedSuccess) {
    return {
      status: 'observed',
      freshness: 'observed',
      severity: 'success',
      terminal: true,
      stateVersion,
      message,
      observation,
    };
  }

  if (input.stale) {
    return {
      status: 'stale',
      freshness: 'stale',
      severity: 'warning',
      terminal: false,
      stateVersion,
      message,
      observation,
    };
  }

  if (hasObservation) {
    return {
      status: 'pending',
      freshness: hasStateVersion ? 'observed' : 'not-yet-materialized',
      severity: 'info',
      terminal,
      stateVersion,
      message,
      observation,
    };
  }

  return {
    status: input.accepted === false ? 'pending' : 'accepted',
    freshness: 'accepted-only',
    severity: 'info',
    terminal: false,
    stateVersion,
    message,
    observation,
  };
}

export async function probeAsyncOperation<TObservation>(
  options: ProbeAsyncOperationOptions<TObservation>,
): Promise<AsyncOperationProbeResult<TObservation>> {
  let observation: TObservation | null = null;
  let error: unknown = null;
  const maxAttempts = Math.max(0, Math.floor(options.maxAttempts));

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    try {
      observation = await options.read();
      error = null;
      if (options.isTerminal(observation)) {
        return { observation, error: null, exhausted: false };
      }
    } catch (candidateError) {
      error = candidateError;
      if (!options.canRetryError?.(candidateError)) {
        throw candidateError;
      }
    }

    if (attempt < maxAttempts - 1) {
      await options.waitForNextAttempt?.(attempt + 1);
    }
  }

  return { observation, error, exhausted: true };
}
