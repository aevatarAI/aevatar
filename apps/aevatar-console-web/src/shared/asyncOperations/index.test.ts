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
});
