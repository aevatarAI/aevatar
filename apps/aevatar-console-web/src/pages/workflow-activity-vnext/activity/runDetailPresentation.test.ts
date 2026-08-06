import type {
  WorkflowActivityDiagnostic,
  WorkflowActivityStep,
  WorkflowActivityTimelineEvent,
  WorkflowActivityUsageTotals,
} from '@/shared/models/workflowActivity';
import {
  buildFailurePresentation,
  buildStepMetrics,
  buildUsagePresentation,
  getStepDisplayName,
  getTimelineEventLabel,
} from './runDetailPresentation';

function usage(
  overrides: Partial<WorkflowActivityUsageTotals> = {},
): WorkflowActivityUsageTotals {
  return {
    promptTokens: 0,
    completionTokens: 0,
    totalTokens: 0,
    cost: 0,
    ...overrides,
  };
}

function step(
  overrides: Partial<WorkflowActivityStep> = {},
): WorkflowActivityStep {
  return {
    stepId: 'step-internal-alpha',
    stepType: 'human_approval',
    targetRole: '',
    requestedAtUtc: '2026-08-04T10:00:00Z',
    completedAtUtc: '2026-08-04T10:01:00Z',
    success: false,
    durationMs: 60_000,
    outputPreview: '',
    error: 'Approval timed out',
    requestParameters: {},
    nextStepId: '',
    branchKey: '',
    suspensionType: 'approval',
    suspensionPrompt: 'Approve?',
    suspensionContent: '',
    suspensionTimeoutSeconds: 60,
    toolApproval: null,
    usage: usage(),
    ...overrides,
  };
}

function diagnostic(
  overrides: Partial<WorkflowActivityDiagnostic> = {},
): WorkflowActivityDiagnostic {
  return {
    timestampUtc: '2026-08-04T10:01:00Z',
    severity: 'error',
    code: 'APPROVAL_TIMEOUT',
    source: 'workflow',
    message: 'Approval did not arrive before the deadline',
    hint: 'Review the approval channel',
    stepId: 'step-internal-alpha',
    stepType: 'human_approval',
    targetRole: '',
    ...overrides,
  };
}

describe('run detail presentation', () => {
  it('reconciles a failed one-step run without calling the failed step completed', () => {
    expect(buildStepMetrics([step()])).toEqual({
      attempted: 1,
      failed: 1,
      skipped: null,
      succeeded: 0,
      waiting: 0,
    });
  });

  it('groups equivalent failure evidence behind one primary cause', () => {
    expect(
      buildFailurePresentation({
        diagnostics: [diagnostic(), diagnostic({ source: 'projection' })],
        finalError: 'Approval did not arrive before the deadline',
        steps: [step({ error: 'Approval timed out' })],
      }),
    ).toEqual({
      evidence: [
        'Approval did not arrive before the deadline',
        'Approval timed out',
      ],
      primaryCause: 'Approval did not arrive before the deadline',
    });
  });

  it('treats all-zero usage as not reported because the contract has no measurement provenance', () => {
    expect(buildUsagePresentation(usage(), [], [])).toEqual({
      completionTokens: null,
      cost: null,
      currency: null,
      promptTokens: null,
      state: 'not_reported',
      toolCalls: null,
      totalTokens: null,
    });
  });

  it('does not turn a timeline without tool-call evidence into measured zero', () => {
    const runStarted = {
      kind: 'RunStarted',
      toolCall: null,
    } as WorkflowActivityTimelineEvent;

    expect(
      buildUsagePresentation(usage(), [], [runStarted]).toolCalls,
    ).toBeNull();
  });

  it('keeps reported usage and tool calls without inventing a cost currency', () => {
    const toolEvent = {
      toolCall: { success: true },
    } as WorkflowActivityTimelineEvent;

    expect(
      buildUsagePresentation(
        usage({
          completionTokens: 8,
          cost: 0.02,
          promptTokens: 4,
          totalTokens: 12,
        }),
        [step({ usage: usage({ totalTokens: 12 }) })],
        [toolEvent],
      ),
    ).toEqual({
      completionTokens: 8,
      cost: 0.02,
      currency: null,
      promptTokens: 4,
      state: 'reported',
      toolCalls: 1,
      totalTokens: 12,
    });
  });

  it('names execution steps from product fields instead of raw IDs', () => {
    expect(
      getStepDisplayName(
        step({ stepType: 'llm_call', targetRole: 'incident responder' }),
      ),
    ).toBe('Incident responder · LLM call');
    expect(getStepDisplayName(step())).toBe('Human approval');
  });

  it('maps machine timeline events to user language', () => {
    expect(getTimelineEventLabel({ kind: 'RunStarted', stage: '' })).toBe(
      'Run started',
    );
    expect(
      getTimelineEventLabel({ kind: 'UnknownEvent', stage: 'role.reply' }),
    ).toBe('Step produced a response');
    expect(
      getTimelineEventLabel({ kind: 'UnknownEvent', stage: 'runtime.signal' }),
    ).toBe('Run updated');
  });
});
