import type { MissionControlSnapshot } from './models';
import { buildMissionOperatorHandoff } from './operatorHandoff';

function createSnapshot(
  overrides: Partial<MissionControlSnapshot> = {},
): MissionControlSnapshot {
  return {
    edges: [],
    events: [
      {
        detail: 'approval requested',
        handoff: {
          detail: 'Waiting for approval at step approval.',
          evidence: 'approval requested',
          nextStep: 'Open the intervention panel and decide with the latest event dock evidence.',
          severity: 'action',
          title: 'Action handoff',
        },
        id: 'event-1',
        severity: 'warning',
        stepId: 'approval',
        timestamp: '2026-03-30T08:00:20.000Z',
        title: 'workflow.suspended',
        type: 'workflow_suspended',
      },
    ],
    liveLogs: [],
    metrics: [],
    nodes: [],
    summary: {
      activeStageLabel: 'Waiting for approval',
      definitionActorId: 'actor-1',
      observationStatus: 'streaming',
      runId: 'run-1',
      scopeId: 'scope-1',
      startedAt: '2026-03-30T08:00:00.000Z',
      status: 'waiting_approval',
      updatedAt: '2026-03-30T08:00:20.000Z',
      workflowName: 'mission-workflow',
    },
    ...overrides,
  };
}

describe('buildMissionOperatorHandoff', () => {
  it('describes observation-only runs as read-only evidence', () => {
    const handoff = buildMissionOperatorHandoff(
      createSnapshot({
        events: [],
        summary: {
          activeStageLabel: 'Execution running',
          definitionActorId: 'actor-1',
          observationStatus: 'streaming',
          runId: 'run-1',
          scopeId: 'scope-1',
          startedAt: '2026-03-30T08:00:00.000Z',
          status: 'running',
          updatedAt: '2026-03-30T08:00:20.000Z',
          workflowName: 'mission-workflow',
        },
      }),
      'live',
    );

    expect(handoff.isActionable).toBe(false);
    expect(handoff.actionLabel).toBe('No operator action');
    expect(handoff.evidenceDetail).toContain('read-only evidence');
    expect(handoff.expectedResult).toContain('Continue observing');
  });

  it('blocks intervention actions while runtime is disconnected', () => {
    const handoff = buildMissionOperatorHandoff(
      createSnapshot({
        intervention: {
          key: 'waiting-approval/approval',
          kind: 'human_approval',
          nodeId: 'node-approval',
          primaryActionLabel: 'Approve',
          prompt: 'Approve guarded execution.',
          required: true,
          secondaryActionLabel: 'Reject',
          stepId: 'approval',
          summary: 'Runtime is paused for approval.',
          title: 'Waiting for approval',
        },
      }),
      'disconnected',
    );

    expect(handoff.isActionable).toBe(false);
    expect(handoff.actionLabel).toBe('Approval Required');
    expect(handoff.connectionDetail).toContain('blocked because runtime is disconnected');
    expect(handoff.inputLabel).toContain('Approve or reject');
  });

  it('explains signal payload submission and waits for runtime confirmation', () => {
    const handoff = buildMissionOperatorHandoff(
      createSnapshot({
        intervention: {
          key: 'waiting-signal/risk-gate',
          kind: 'waiting_signal',
          nodeId: 'node-risk',
          primaryActionLabel: 'Send Signal',
          prompt: 'Send market open signal.',
          required: true,
          signalName: 'market-open',
          stepId: 'risk-gate',
          summary: 'Runtime is waiting for a signal.',
          title: 'Waiting for market-open',
        },
        summary: {
          activeStageLabel: 'Waiting for market-open',
          definitionActorId: 'actor-1',
          observationStatus: 'streaming',
          runId: 'run-1',
          scopeId: 'scope-1',
          startedAt: '2026-03-30T08:00:00.000Z',
          status: 'waiting_signal',
          updatedAt: '2026-03-30T08:00:20.000Z',
          workflowName: 'mission-workflow',
        },
      }),
      'live',
    );

    expect(handoff.isActionable).toBe(true);
    expect(handoff.actionLabel).toBe('Waiting for Signal');
    expect(handoff.inputLabel).toContain('signal payload');
    expect(handoff.expectedResult).toContain('next runtime snapshot');
  });
});
