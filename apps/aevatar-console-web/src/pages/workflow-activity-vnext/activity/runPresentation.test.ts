import {
  getRunStatusPresentation,
  isRunStatusInProgress,
  isRunStatusTerminal,
} from './runPresentation';

describe('test-run status presentation', () => {
  it('keeps authoritative queued, running, completed, failed, cancelled, and unknown states distinct', () => {
    expect(getRunStatusPresentation('queued').label).toBe('Queued');
    expect(getRunStatusPresentation('running').label).toBe('Running');
    expect(getRunStatusPresentation('completed').label).toBe('Completed');
    expect(getRunStatusPresentation('failed').label).toBe('Failed');
    expect(getRunStatusPresentation('stopped').label).toBe('Cancelled');
    expect(getRunStatusPresentation('future_status').label).toBe('Unknown');

    expect(isRunStatusInProgress('queued')).toBe(true);
    expect(isRunStatusInProgress('running')).toBe(true);
    expect(isRunStatusTerminal('completed')).toBe(true);
    expect(isRunStatusTerminal('failed')).toBe(true);
    expect(isRunStatusTerminal('stopped')).toBe(true);
    expect(isRunStatusTerminal('future_status')).toBe(false);
  });
});
