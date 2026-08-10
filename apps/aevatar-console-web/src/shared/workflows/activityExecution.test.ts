import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';
import { adaptActivityRunToExecutionLogs } from './activityExecution';

function createRun(): WorkflowActivityRunDetail {
  return {
    diagnostics: [],
    finalError: '',
    finalOutput: 'Order 42 is ready.',
    input: 'Review order 42',
    statistics: {
      completedSteps: 1,
      requestedSteps: 1,
      roleReplyCount: 0,
      stepTypeCounts: { llm_call: 1 },
      totalSteps: 1,
    },
    steps: [
      {
        branchKey: '',
        completedAtUtc: '2026-08-10T10:00:01Z',
        durationMs: 1000,
        error: '',
        nextStepId: '',
        outputPreview: 'Order 42 is ready.',
        requestedAtUtc: '2026-08-10T10:00:00Z',
        requestParameters: { prompt: 'Review order 42' },
        stepId: 'step-verify',
        stepType: 'llm_call',
        success: true,
        suspensionContent: '',
        suspensionPrompt: '',
        suspensionTimeoutSeconds: null,
        suspensionType: '',
        targetRole: 'reviewer',
        toolApproval: null,
        usage: {
          completionTokens: 3,
          cost: 0,
          promptTokens: 5,
          totalTokens: 8,
        },
      },
    ],
    summary: {
      runId: 'run-alpha',
      runOrigin: 'published-service',
      scopeId: 'scope-alpha',
      startedAtUtc: '2026-08-10T10:00:00Z',
      stateVersion: 4,
      status: 'completed',
      success: true,
      updatedAtUtc: '2026-08-10T10:00:01Z',
      workflowName: 'Order review',
    },
    timeline: [],
    usageTotals: {
      completionTokens: 3,
      cost: 0,
      promptTokens: 5,
      totalTokens: 8,
    },
  };
}

describe('adaptActivityRunToExecutionLogs', () => {
  it('maps authoritative Activity steps into the shared execution trace', () => {
    const execution = adaptActivityRunToExecutionLogs(createRun());

    expect(execution.status).toBe('completed');
    expect(execution.outputText).toBe('Order 42 is ready.');
    expect(execution.trace.stepStates.get('step-verify')).toMatchObject({
      status: 'completed',
      stepType: 'llm_call',
      targetRole: 'reviewer',
    });
    expect(execution.trace.logs).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ stepId: 'step-verify', tone: 'started' }),
        expect.objectContaining({ stepId: 'step-verify', tone: 'completed' }),
      ]),
    );
  });
});
