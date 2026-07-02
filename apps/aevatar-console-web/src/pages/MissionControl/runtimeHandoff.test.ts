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

  it('blocks connecting intervention nodes until runtime state is available', () => {
    const cue = buildMissionNodeHandoffCue({
      connectionStatus: 'connecting',
      freshnessLabel: 'unavailable',
      isInterventionNode: true,
      kind: 'execution',
      label: 'input',
      observationStatus: 'unavailable',
      status: 'waiting',
    });

    expect(cue.severity).toBe('blocked');
    expect(cue.title).toBe('Operator handoff');
    expect(cue.nextStep).toContain('signal or context');
  });

  it('marks active streaming nodes as awaiting runtime confirmation', () => {
    const cue = buildMissionNodeHandoffCue({
      connectionStatus: 'live',
      freshnessLabel: '1s',
      isInterventionNode: false,
      kind: 'tool',
      label: 'tool-call',
      observationStatus: 'streaming',
      status: 'active',
    });

    expect(cue.severity).toBe('confirming');
    expect(cue.title).toBe('Runtime confirmation');
    expect(cue.nextStep).toContain('next runtime event');
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

  it('keeps terminal runtime events as settled evidence', () => {
    const cue = buildMissionEventHandoffCue({
      detail: 'workflow completed',
      runStatus: 'completed',
      type: 'workflow_completed',
    });

    expect(cue.severity).toBe('observe');
    expect(cue.title).toBe('Settled evidence');
    expect(cue.nextStep).toContain('no operator action');
  });

  it('marks execution-start runtime events as awaiting confirmation', () => {
    const cue = buildMissionEventHandoffCue({
      detail: 'step requested',
      runStatus: 'running',
      stepId: 'research',
      type: 'step_requested',
    });

    expect(cue.severity).toBe('confirming');
    expect(cue.title).toBe('Await confirmation');
    expect(cue.nextStep).toContain('matching completion');
  });

  it('keeps actor-linked non-blocking events observational', () => {
    const cue = buildMissionEventHandoffCue({
      actorId: 'actor-1',
      detail: 'role reply recorded',
      runStatus: 'running',
      type: 'workflow_role_reply_recorded',
    });

    expect(cue.severity).toBe('observe');
    expect(cue.title).toBe('Event evidence');
    expect(cue.detail).toContain('current runtime actor');
    expect(cue.detail).not.toContain('actor-1');
    expect(cue.nextStep).toContain('Keep observing');
  });

  it('keeps run-linked non-blocking events observational', () => {
    const cue = buildMissionEventHandoffCue({
      detail: 'signal buffered',
      runStatus: 'running',
      type: 'workflow_signal_buffered',
    });

    expect(cue.severity).toBe('observe');
    expect(cue.detail).toContain('current run');
  });

  it('tells the operator when runtime rejects an action request', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: false,
      kind: 'resume',
    });

    expect(message).toContain('did not accept');
    expect(message).toContain('retry after checking connection state');
  });

  it('keeps accepted actions honest until runtime publishes new evidence', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: true,
      commandId: 'cmd-1',
      kind: 'approve',
      runId: 'run-1',
    });

    expect(message).toContain('Wait for runtime to confirm');
    expect(message).toContain('Command observation is pending');
    expect(message).toContain('current run remains the evidence source');
    expect(message).not.toContain('cmd-1');
    expect(message).not.toContain('run-1');
  });

  it('keeps accepted signals honest until a new runtime snapshot arrives', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: true,
      kind: 'signal',
      signalName: 'continue',
    });

    expect(message).toContain('Signal continue was accepted');
    expect(message).toContain('next runtime snapshot');
  });

  it('keeps accepted rejections honest until runtime confirms stop or rollback', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: true,
      kind: 'reject',
    });

    expect(message).toContain('Rejection was submitted');
    expect(message).toContain('confirm stop or rollback');
  });

  it('keeps accepted resumes honest until the blocked step publishes evidence', () => {
    const message = buildMissionActionFeedbackMessage({
      accepted: true,
      kind: 'resume',
    });

    expect(message).toContain('Resume was accepted');
    expect(message).toContain('blocked step to publish new evidence');
  });
});
