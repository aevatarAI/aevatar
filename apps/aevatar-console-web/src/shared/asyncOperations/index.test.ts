import {
  normalizeAsyncOperationState,
  probeAsyncOperation,
} from './index';

describe('async operation state helper', () => {
  it('normalizes an accepted receipt without observation as accepted-only', () => {
    const state = normalizeAsyncOperationState({
      accepted: true,
      fallbackMessage: 'Accepted.',
    });

    expect(state.status).toBe('accepted');
    expect(state.freshness).toBe('accepted-only');
    expect(state.severity).toBe('info');
    expect(state.terminal).toBe(false);
    expect(state.message).toBe('Accepted.');
  });

  it('normalizes a pending observation without StateVersion as not yet materialized', () => {
    const observation = { status: 'pending' };
    const state = normalizeAsyncOperationState({
      observation,
      observationStatus: 'pending',
      terminal: false,
      fallbackMessage: 'Still waiting.',
    });

    expect(state.status).toBe('pending');
    expect(state.freshness).toBe('not-yet-materialized');
    expect(state.observation).toBe(observation);
    expect(state.stateVersion).toBeNull();
  });

  it('normalizes an observed result with StateVersion', () => {
    const observation = { status: 'succeeded', stateVersion: 12 };
    const state = normalizeAsyncOperationState({
      observation,
      observationStatus: 'succeeded',
      stateVersion: observation.stateVersion,
      fallbackMessage: 'Observed.',
    });

    expect(state.status).toBe('observed');
    expect(state.freshness).toBe('observed');
    expect(state.severity).toBe('success');
    expect(state.terminal).toBe(true);
    expect(state.stateVersion).toBe(12);
  });

  it.each(['failed', 'rejected'] as const)(
    'normalizes terminal %s observations as failed',
    (status) => {
      const state = normalizeAsyncOperationState({
        observation: { status },
        observationStatus: status,
        message: `${status} message`,
      });

      expect(state.status).toBe('failed');
      expect(state.freshness).toBe('observed');
      expect(state.severity).toBe('error');
      expect(state.terminal).toBe(true);
      expect(state.message).toBe(`${status} message`);
    },
  );

  it('normalizes an exhausted pending probe as stale', () => {
    const state = normalizeAsyncOperationState({
      accepted: true,
      stale: true,
      fallbackMessage: 'Still pending.',
    });

    expect(state.status).toBe('stale');
    expect(state.freshness).toBe('stale');
    expect(state.severity).toBe('warning');
    expect(state.terminal).toBe(false);
  });

  it('probes deterministically without timing waits', async () => {
    type ProbeObservation = { readonly status: 'pending' | 'succeeded' };
    const reads = [
      { status: 'pending' },
      { status: 'succeeded' },
    ] satisfies ProbeObservation[];
    const waitForNextAttempt = jest.fn().mockResolvedValue(undefined);

    const result = await probeAsyncOperation<ProbeObservation>({
      maxAttempts: 4,
      read: jest.fn(async (): Promise<ProbeObservation> =>
        reads.shift() ?? { status: 'succeeded' },
      ),
      isTerminal: (observation) => observation.status === 'succeeded',
      waitForNextAttempt,
    });

    expect(result.observation).toEqual({ status: 'succeeded' });
    expect(result.exhausted).toBe(false);
    expect(waitForNextAttempt).toHaveBeenCalledTimes(1);
  });

  it('returns retryable probe errors after attempts are exhausted', async () => {
    const probeError = new Error('Read model is not visible yet.');
    const read = jest.fn(async () => {
      throw probeError;
    });
    const waitForNextAttempt = jest.fn().mockResolvedValue(undefined);

    const result = await probeAsyncOperation<{ readonly status: 'pending' }>({
      maxAttempts: 3,
      read,
      isTerminal: (observation) => observation.status !== 'pending',
      canRetryError: () => true,
      waitForNextAttempt,
    });

    expect(result.observation).toBeNull();
    expect(result.error).toBe(probeError);
    expect(result.exhausted).toBe(true);
    expect(read).toHaveBeenCalledTimes(3);
    expect(waitForNextAttempt).toHaveBeenCalledTimes(2);
  });

  it('probeAsyncOperation rejects immediately for non-retryable error', async () => {
    const nonRetryableError = new Error('non-404 API error');
    const read = jest.fn().mockRejectedValue(nonRetryableError);
    const waitForNextAttempt = jest.fn().mockResolvedValue(undefined);
    const canRetryError = jest.fn().mockReturnValue(false);

    await expect(
      probeAsyncOperation<{ readonly status: 'pending' }>({
        maxAttempts: 3,
        read,
        isTerminal: () => false,
        canRetryError,
        waitForNextAttempt,
      }),
    ).rejects.toBe(nonRetryableError);
    expect(read).toHaveBeenCalledTimes(1);
    expect(canRetryError).toHaveBeenCalledWith(nonRetryableError);
    expect(waitForNextAttempt).not.toHaveBeenCalled();
  });

  it('probeAsyncOperation rejects when canRetryError is absent and read fails', async () => {
    const err = new Error('any error');
    const read = jest.fn().mockRejectedValue(err);
    const waitForNextAttempt = jest.fn().mockResolvedValue(undefined);

    await expect(
      probeAsyncOperation<{ readonly status: 'pending' }>({
        maxAttempts: 3,
        read,
        isTerminal: () => false,
        waitForNextAttempt,
      }),
    ).rejects.toBe(err);
    expect(read).toHaveBeenCalledTimes(1);
    expect(waitForNextAttempt).not.toHaveBeenCalled();
  });

  it('returns the latest pending observation when attempts are exhausted', async () => {
    type ProbeObservation = { readonly status: 'pending' };
    const read = jest.fn(
      async (): Promise<ProbeObservation> => ({ status: 'pending' }),
    );
    const waitForNextAttempt = jest.fn().mockResolvedValue(undefined);

    const result = await probeAsyncOperation<ProbeObservation>({
      maxAttempts: 2,
      read,
      isTerminal: () => false,
      waitForNextAttempt,
    });

    expect(result.observation).toEqual({ status: 'pending' });
    expect(result.error).toBeNull();
    expect(result.exhausted).toBe(true);
    expect(read).toHaveBeenCalledTimes(2);
    expect(waitForNextAttempt).toHaveBeenCalledTimes(1);
  });
});
