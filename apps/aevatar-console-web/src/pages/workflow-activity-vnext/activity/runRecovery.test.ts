import type { WorkflowActivityRunRecovery } from '@/shared/models/workflowActivity';
import { resolveRunRecovery } from './runRecovery';

function buildRecovery(): WorkflowActivityRunRecovery {
  return {
    recommendedAction: 'retry_failed_step',
    definitionRevision: 'revision-42',
    retry: {
      eligible: true,
      unavailableReason: '',
      startAtStepId: 'step-failed',
      reusesPriorStepOutputs: true,
      mayIncurCost: true,
    },
    runAgain: {
      eligible: false,
      unavailableReason: 'Run again is not safe for this record.',
      startAtStepId: null,
      reusesPriorStepOutputs: false,
      mayIncurCost: false,
    },
    lineage: {
      parentRunId: null,
      childRunIds: [],
    },
  };
}

describe('resolveRunRecovery', () => {
  it('exposes only explicitly eligible typed actions', () => {
    const resolved = resolveRunRecovery(buildRecovery());

    expect(resolved.retryAction?.startAtStepId).toBe('step-failed');
    expect(resolved.runAgainAction).toBeNull();
    expect(resolved.runAgainUnavailableReason).toBe(
      'Run again is not safe for this record.',
    );
  });

  it('does not invent actions when the recovery contract is absent', () => {
    expect(resolveRunRecovery(null)).toEqual({
      retryAction: null,
      retryUnavailableReason: null,
      runAgainAction: null,
      runAgainUnavailableReason: null,
    });
  });
});
