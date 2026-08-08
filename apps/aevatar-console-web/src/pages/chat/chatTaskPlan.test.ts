import { ChatTaskPlanProtocolError, decodeChatTaskPlan } from './chatTaskPlan';

function taskPlanWithObservation(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 4,
    actorId: 'conversation-alpha',
    taskId: 'task-alpha',
    turnId: 'turn-alpha',
    planId: 'plan-alpha',
    planRevision: 1,
    planRevisions: [],
    title: 'Update repository',
    status: 'active',
    steps: [
      {
        stepId: 'step-update',
        order: 1,
        kind: 'tool',
        status: 'waiting',
        required: true,
        description: 'Update repository',
        source: {
          tool: {
            toolName: 'repository_update',
            serviceSlug: 'github-api',
            serviceId: 'svc-alpha',
          },
        },
        mayChangeExternalState: true,
        externalEffect: 'not_started',
        availableActions: {},
        dependsOn: [],
        substeps: [],
        approvalObservation: {
          approvalRequestId: 'nyxid-approval-alpha',
          decisionMode: 'per_request',
          receiptStatus: 'approval_required',
          observedAt: '2026-08-08T00:10:00Z',
          ...overrides,
        },
      },
    ],
  };
}

describe('decodeChatTaskPlan', () => {
  it('preserves the complete typed Tier-B approval observation', () => {
    const decoded = decodeChatTaskPlan(taskPlanWithObservation());

    expect(decoded.steps[0]?.approvalObservation).toEqual({
      approvalRequestId: 'nyxid-approval-alpha',
      decisionMode: 'per_request',
      receiptStatus: 'approval_required',
      observedAt: '2026-08-08T00:10:00Z',
    });
  });

  it.each([
    ['decisionMode', 'session'],
    ['receiptStatus', 'pending'],
  ])('rejects an unknown approval observation %s', (field, value) => {
    expect(() =>
      decodeChatTaskPlan(taskPlanWithObservation({ [field]: value })),
    ).toThrow(ChatTaskPlanProtocolError);
  });
});
