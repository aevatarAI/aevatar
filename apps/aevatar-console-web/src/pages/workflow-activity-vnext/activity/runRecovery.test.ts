import type { WorkflowRunRecoveryCapability } from '@/shared/models/workflowActivity';
import { resolveRunRecovery } from './runRecovery';

describe('resolveRunRecovery', () => {
  it('enables only an eligible action with an authoritative starting step', () => {
    expect(resolveRunRecovery(recoveryCapability())).toEqual({
      retry: {
        enabled: true,
        mayIncurModelOrToolCost: true,
        reason: '',
        recommendedActions: [1],
        reusesPriorStepOutputs: true,
        startingStepId: 'step-failed',
      },
      runAgain: {
        enabled: false,
        mayIncurModelOrToolCost: false,
        reason: 'Fix access first.',
        recommendedActions: [3],
        reusesPriorStepOutputs: false,
        startingStepId: 'step-start',
      },
      workflowDefinitionRevisionId: 'revision-3',
      workflowDefinitionVersion: 3,
    });
  });

  it('uses the backend unavailable reason without deriving an alternative', () => {
    const capability = recoveryCapability();
    capability.retryFailedStep = {
      ...capability.retryFailedStep,
      eligibility: 3,
      unavailableReason: 'The legacy run does not contain retry facts.',
      recommendedActions: [7],
    };

    expect(resolveRunRecovery(capability).retry).toEqual(
      expect.objectContaining({
        enabled: false,
        reason: 'The legacy run does not contain retry facts.',
        recommendedActions: [7],
      }),
    );
  });

  it('keeps eligible actions disabled when the backend omits the starting step', () => {
    const capability = recoveryCapability();
    capability.retryFailedStep = {
      ...capability.retryFailedStep,
      startingStepId: '  ',
    };

    expect(resolveRunRecovery(capability).retry).toEqual(
      expect.objectContaining({
        enabled: false,
        reason: 'Recovery starting step is unavailable.',
        startingStepId: '',
      }),
    );
  });
});

function recoveryCapability(): MutableRecoveryCapability {
  return {
    retryFailedStep: {
      eligibility: 1,
      unavailableReasonCode: 1,
      unavailableReason: '',
      recommendedActions: [1],
      startingStepId: 'step-failed',
      reusesPriorStepOutputs: true,
      mayIncurModelOrToolCost: true,
    },
    runAgain: {
      eligibility: 2,
      unavailableReasonCode: 4,
      unavailableReason: 'Fix access first.',
      recommendedActions: [3],
      startingStepId: 'step-start',
      reusesPriorStepOutputs: false,
      mayIncurModelOrToolCost: false,
    },
    workflowDefinitionRevisionId: 'revision-3',
    workflowDefinitionVersion: 3,
  };
}

type MutableRecoveryCapability = {
  -readonly [Key in keyof WorkflowRunRecoveryCapability]: WorkflowRunRecoveryCapability[Key] extends object
    ? {
        -readonly [NestedKey in keyof WorkflowRunRecoveryCapability[Key]]: WorkflowRunRecoveryCapability[Key][NestedKey];
      }
    : WorkflowRunRecoveryCapability[Key];
};
