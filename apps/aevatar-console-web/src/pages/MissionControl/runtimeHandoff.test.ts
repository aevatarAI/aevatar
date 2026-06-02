import {
  buildMissionActionFeedbackMessage,
  buildMissionEventHandoffCue,
  buildMissionNodeHandoffCue,
} from './runtimeHandoff';

describe('Mission Control runtime handoff cues', () => {
  it('marks the intervention node as the actionable handoff', () => {
    const cue = buildMissionNodeHandoffCue({
      connectionStatus: 'live',
      freshnessLabel: '2s',
      isInterventionNode: true,
      kind: 'approval',
      label: 'approval',
      observationStatus: 'streaming',
      status: 'waiting',
    });

    expect(cue.severity).toBe('action');
    expect(cue.title).toBe('Operator handoff');
    expect(cue.nextStep).toContain('approve or reject');
  });

  it('keeps disconnected nodes blocked as last-known evidence', () => {
    const cue = buildMissionNodeHandoffCue({
      connectionStatus: 'disconnected',
      freshnessLabel: '4m',
      isInterventionNode: false,
      kind: 'research',
      label: 'research',
      observationStatus: 'delayed',
      status: 'active',
    });

    expect(cue.severity).toBe('blocked');
    expect(cue.evidence).toContain('delayed');
    expect(cue.nextStep).toContain('newer runtime snapshot');
  });

  it('turns blocking runtime events into event dock action handoffs', () => {
    const cue = buildMissionEventHandoffCue({
      detail: 'approval (human_approval)',
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
      runStatus: 'waiting_approval',
      stepId: 'approval',
      type: 'workflow_suspended',
    });

    expect(cue.severity).toBe('action');
    expect(cue.title).toBe('Action handoff');
    expect(cue.nextStep).toContain('intervention panel');
  });

  it('keeps accepted actions honest until runtime publishes new evidence', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: true,
      commandId: 'cmd-1',
      kind: 'approve',
      runId: 'run-1',
    });

    expect(message).toContain('Wait for runtime to confirm');
    expect(message).toContain('Command cmd-1');
    expect(message).toContain('Run run-1');
  });
});
