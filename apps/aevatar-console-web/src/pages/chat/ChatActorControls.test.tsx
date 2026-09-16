import { act, fireEvent, render, screen, within } from '@testing-library/react';
import React from 'react';
import { ChatActorControls } from './ChatActorControls';
import {
  type ChatActorProjection,
  chatActionIdentityKey,
  createChatActorProjection,
} from './chatActorState';
import type { ChatActorStep, ChatTaskPlan } from './chatTaskPlan';

function stepFixture(overrides: Partial<ChatActorStep> = {}): ChatActorStep {
  return {
    stepId: 'step-read',
    order: 1,
    kind: 'tool',
    status: 'running',
    required: true,
    description: 'Inspect the connected repository',
    source: {
      kind: 'tool',
      label: 'repository_read',
      serviceSlug: 'github-api',
      serviceId: 'svc-alpha',
    },
    mayChangeExternalState: false,
    externalEffect: 'not_started',
    availableActions: { retry: false, skip: false, stop: true },
    updatedAt: '2026-08-08T00:00:00Z',
    addedBy: 'initial',
    addedInPlanRevision: 3,
    dependsOn: [],
    substeps: [
      { substepId: 'substep-access', title: 'Check access', status: 'done' },
      {
        substepId: 'substep-read',
        title: 'Read repository',
        status: 'running',
      },
    ],
    operation: {
      conversationActorId: 'conversation-alpha',
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-read',
      operationId: 'operation-alpha',
      operationGeneration: 2,
      kind: 'tool',
      phase: 'running',
      lastProgressAt: '2026-08-08T00:00:00Z',
      stalledAt: '2026-08-08T00:02:00Z',
    },
    ...overrides,
  };
}

function planFixture(steps: readonly ChatActorStep[]): ChatTaskPlan {
  return {
    schemaVersion: 4,
    actorId: 'conversation-alpha',
    taskId: 'task-alpha',
    turnId: 'turn-alpha',
    planId: 'plan-alpha',
    planRevision: 3,
    planRevisionHistoryStart: 1,
    planRevisions: [
      {
        planRevision: 3,
        revisionCause: 'steering',
        committedAt: '2026-08-08T00:00:00Z',
        addedStepIds: ['step-read'],
        cancelledStepIds: [],
      },
    ],
    title: 'Inspect and verify the repository',
    status: 'active',
    activeStepId: steps[0]?.stepId,
    steps,
  };
}

function projectionFixture(
  steps: readonly ChatActorStep[] = [stepFixture()],
): ChatActorProjection {
  const projection = createChatActorProjection('conversation-alpha');
  projection.stateVersion = 17;
  projection.activeTurn = {
    turnId: 'turn-alpha',
    taskId: 'task-alpha',
    status: 'active',
  };
  projection.task = planFixture(steps);
  projection.steps = new Map(steps.map((step) => [step.stepId, step]));
  return projection;
}

function callbacks() {
  return {
    onActionOpen: jest.fn(),
    onActionConnectCredential: jest.fn(),
    onActionRefresh: jest.fn(),
    onActionReport: jest.fn(),
    onInputResolve: jest.fn(),
    onRetry: jest.fn(),
    onSkip: jest.fn(),
    onSteer: jest.fn(),
    onStop: jest.fn(),
  };
}

describe('ChatActorControls', () => {
  beforeEach(() => {
    jest.useFakeTimers();
    jest.setSystemTime(new Date('2026-08-08T00:02:00Z'));
  });

  afterEach(() => {
    jest.useRealTimers();
  });

  it('renders the complete actor-owned plan ledger', () => {
    const verify = stepFixture({
      stepId: 'step-verify',
      order: 2,
      kind: 'postcondition',
      status: 'done',
      description: 'Verify repository access',
      source: { kind: 'postcondition', label: 'service.connected' },
      externalEffect: 'confirmed',
      availableActions: { retry: false, skip: false, stop: false },
      substeps: [],
      operation: null,
      updatedAt: '2026-08-08T00:00:05Z',
    });
    const projection = projectionFixture([stepFixture(), verify]);
    const handlers = callbacks();
    render(<ChatActorControls projection={projection} {...handlers} />);

    expect(screen.getByRole('region', { name: 'Task plan' })).toHaveTextContent(
      'Inspect and verify the repository',
    );
    expect(screen.getByText('repository_read')).toBeInTheDocument();
    expect(screen.getByText('not_started')).toBeInTheDocument();
    expect(screen.getByText('Check access · done')).toBeInTheDocument();
    expect(screen.getByText('Stalled')).toBeInTheDocument();
    expect(
      screen.getByText('Verified against service.connected'),
    ).toBeInTheDocument();
  });

  it('renders a committed NyxID approval observation from actor facts', () => {
    const step = stepFixture({
      approvalObservation: {
        approvalRequestId: 'nyxid-decision-alpha',
        decisionMode: 'per_request',
        receiptStatus: 'denied',
        observedAt: '2026-08-08T00:03:00Z',
      },
    });
    const projection = projectionFixture([step]);
    render(<ChatActorControls projection={projection} {...callbacks()} />);

    expect(
      screen.getByRole('region', { name: 'NyxID approval observation' }),
    ).toBeInTheDocument();
    expect(screen.getByText('nyxid-decision-alpha')).toBeInTheDocument();
    expect(screen.getAllByText('denied')).toHaveLength(2);
  });

  it('submits option identities and directs free text to the shared composer', () => {
    const projection = projectionFixture();
    projection.pendingInput = {
      requestId: 'input-alpha',
      prompt: 'Choose a region or override the threshold',
      options: [
        { optionId: 'option-sg', label: 'Singapore' },
        { optionId: 'option-fra', label: 'Frankfurt' },
      ],
      allowFreeText: true,
      multiSelect: false,
    };
    const handlers = callbacks();
    render(<ChatActorControls projection={projection} {...handlers} />);

    expect(
      screen.getByText('Type the answer in the composer below.'),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('Free text answer')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('radio', { name: 'Singapore' }));
    fireEvent.click(screen.getByRole('button', { name: 'Submit answer' }));
    expect(handlers.onInputResolve).toHaveBeenCalledWith(
      { selectedOptionIds: ['option-sg'] },
      expect.objectContaining({ requestId: 'input-alpha' }),
    );
  });

  it('submits a typed numeric threshold from the actor control', () => {
    const projection = projectionFixture();
    projection.pendingInput = {
      requestId: 'input-threshold',
      prompt: 'Choose the numeric threshold',
      options: [],
      allowFreeText: true,
      multiSelect: false,
      numericThreshold: {
        suggestedValue: 70,
        minimumValue: 0,
        maximumValue: 100,
      },
    };
    const handlers = callbacks();
    render(<ChatActorControls projection={projection} {...handlers} />);

    fireEvent.change(
      screen.getByRole('spinbutton', { name: 'Numeric threshold' }),
      {
        target: { value: '75' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Submit answer' }));

    expect(handlers.onInputResolve).toHaveBeenCalledWith(
      { freeText: '75' },
      expect.objectContaining({ requestId: 'input-threshold' }),
    );
    expect(screen.getByText('Suggested 70')).toBeInTheDocument();
    expect(
      screen.queryByText('Type the answer in the composer below.'),
    ).not.toBeInTheDocument();
  });

  it('renders committed condition and guarded-tool facts after reload', () => {
    const condition = stepFixture({
      stepId: 'step-condition',
      order: 1,
      kind: 'condition',
      status: 'done',
      description: 'Evaluate the observed value',
      source: {
        kind: 'condition',
        label: '80 >= 75',
        condition: {
          conditionId: 'condition-alpha',
          sourceInputRequestId: 'input-threshold',
          suggestedThreshold: 70,
          effectiveThreshold: 75,
          thresholdOrigin: 'user_override',
          observedValue: 80,
          comparison: 'gte',
          outcome: 'true',
          guardedToolName: 'external_record_create',
        },
      },
      mayChangeExternalState: false,
      externalEffect: 'not_applied',
      availableActions: { retry: false, skip: false, stop: false },
      dependsOn: ['step-input'],
      substeps: [],
      operation: null,
    });
    const guarded = stepFixture({
      stepId: 'step-write',
      order: 2,
      status: 'planned',
      description: 'Create the verified record',
      source: { kind: 'tool', label: 'external_record_create' },
      guard: {
        conditionStepId: 'step-condition',
        requiredOutcome: 'true',
      },
      dependsOn: ['step-condition'],
      operation: null,
    });

    render(
      <ChatActorControls
        projection={projectionFixture([condition, guarded])}
        {...callbacks()}
      />,
    );

    const facts = screen.getByRole('region', {
      name: 'Committed condition facts',
    });
    expect(facts).toHaveTextContent('80 >= 75');
    expect(facts).toHaveTextContent('true');
    expect(facts).toHaveTextContent('user_override');
    expect(facts).toHaveTextContent('external_record_create');
    expect(
      screen.getByText('Guard step-condition requires true'),
    ).toBeInTheDocument();
  });

  it('shows only actor-authored recovery controls and moves steering to the composer', () => {
    const retry = stepFixture({
      status: 'failed',
      externalEffect: 'not_applied',
      availableActions: { retry: true, skip: true, stop: true },
    });
    const handlers = callbacks();
    render(
      <ChatActorControls
        projection={projectionFixture([retry])}
        {...handlers}
      />,
    );

    fireEvent.click(
      screen.getByRole('button', {
        name: 'Retry Inspect the connected repository',
      }),
    );
    fireEvent.click(
      screen.getByRole('button', {
        name: 'Skip Inspect the connected repository',
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Stop task' }));
    expect(handlers.onRetry).toHaveBeenCalledWith(retry);
    expect(handlers.onSkip).toHaveBeenCalledWith(retry);
    expect(handlers.onStop).toHaveBeenCalledTimes(1);
    expect(
      screen.getByText('Type a steering instruction in the composer.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByLabelText('Steering instruction'),
    ).not.toBeInTheDocument();
  });

  it('shows a Tier-B receipt only inside its step after the observation exists', () => {
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls projection={projectionFixture()} {...handlers} />,
    );
    expect(
      screen.queryByRole('region', { name: 'NyxID approval observation' }),
    ).not.toBeInTheDocument();

    const observedStep = stepFixture({
      approvalObservation: {
        approvalRequestId: 'nyxid-approval-alpha',
        decisionMode: 'per_request',
        receiptStatus: 'approval_required',
        observedAt: '2026-08-08T00:10:00Z',
      },
    });
    rerender(
      <ChatActorControls
        projection={projectionFixture([observedStep])}
        {...handlers}
      />,
    );
    const step = screen
      .getByText('Inspect the connected repository')
      .closest('li');
    if (!step) throw new Error('Missing observed task step.');
    const observation = within(step).getByRole('region', {
      name: 'NyxID approval observation',
    });
    expect(observation).toHaveTextContent('NyxID request observed');
    expect(observation).toHaveTextContent('nyxid-approval-alpha');
    expect(observation).toHaveTextContent('per_request');
    expect(observation).toHaveTextContent('approval_required');
    expect(observation).toHaveTextContent('2026-08-08T00:10:00Z');
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Approval reason')).not.toBeInTheDocument();
  });

  it('keeps action completion pending until exact committed postcondition proof arrives', () => {
    const projection = projectionFixture([]);
    const request = {
      schemaVersion: 4 as const,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect' as const,
      params: {
        catalogService: {
          serviceSlug: 'api-github',
          requestedScopes: ['repo:read'],
        },
      },
    };
    projection.actions.set('action-alpha', {
      ...request,
      request,
      reports: [
        {
          actionRequestId: 'action-alpha',
          originTurnId: 'turn-alpha',
          disposition: 'completed',
          resource: { userService: { userServiceId: 'user-service-alpha' } },
        },
      ],
      postconditionResult: null,
    });
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls
        actionJourneys={
          new Map([
            [chatActionIdentityKey('conversation-alpha', 'action-alpha'), {}],
          ])
        }
        projection={projection}
        {...handlers}
      />,
    );
    expect(
      screen.getByText(/Reported; waiting for actor verification/),
    ).toBeInTheDocument();
    expect(screen.getByText('repo:read')).toBeInTheDocument();

    const action = projection.actions.get('action-alpha');
    if (!action) throw new Error('Missing action fixture.');
    projection.actions.set('action-alpha', {
      ...action,
      postconditionResult: {
        actionRequestId: 'action-alpha',
        disposition: 'completed',
        verified: true,
        resource: { userService: { userServiceId: 'user-service-alpha' } },
      },
    });
    rerender(<ChatActorControls projection={projection} {...handlers} />);
    expect(screen.getByText('Actor verified')).toBeInTheDocument();
  });

  it.each([
    'declined',
    'failed',
    'cancelled',
    'expired',
  ] as const)('renders %s as terminal instead of waiting for verification', (disposition) => {
    const projection = projectionFixture([]);
    const request = {
      schemaVersion: 4 as const,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect' as const,
      params: { catalogService: { serviceSlug: 'api-github' } },
    };
    projection.actions.set('action-alpha', {
      ...request,
      request,
      reports: [
        {
          actionRequestId: 'action-alpha',
          originTurnId: 'turn-alpha',
          disposition,
        },
      ],
      postconditionResult: null,
    });

    render(<ChatActorControls projection={projection} {...callbacks()} />);

    expect(screen.getByText(disposition)).toBeInTheDocument();
    expect(
      screen.queryByText(/waiting for actor verification/i),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh connection' }),
    ).not.toBeInTheDocument();
  });

  it('keeps the wire inspector gated and redacts secret keys and values', () => {
    const projection = projectionFixture();
    const handlers = callbacks();
    const wire = {
      custom: {
        name: 'nyxid.task.snapshot',
        payload: {
          actorId: 'conversation-alpha',
          authorization: 'Bearer secret-bearer',
          nested: { apiKey: 'nyxid_secretvalue' },
          note: 'Authorization was Bearer another-secret',
        },
      },
    };
    const view = render(
      <ChatActorControls
        diagnosticWire={wire}
        projection={projection}
        {...handlers}
      />,
    );
    expect(
      screen.queryByRole('button', { name: 'Show wire' }),
    ).not.toBeInTheDocument();

    view.rerender(
      <ChatActorControls
        diagnosticWire={wire}
        projection={projection}
        wireInspectorEnabled
        {...handlers}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Show wire' }));
    const inspector = screen.getByRole('region', { name: 'Wire inspector' });
    expect(inspector).toHaveTextContent('conversation-alpha');
    expect(inspector).toHaveTextContent('Bearer [REDACTED]');
    expect(inspector).toHaveTextContent('[REDACTED]');
    expect(inspector).not.toHaveTextContent('secret-bearer');
    expect(inspector).not.toHaveTextContent('nyxid_secretvalue');
  });

  it('presents typed idempotent and stale control outcomes without enabling controls', () => {
    const projection = projectionFixture([]);
    projection.activeTurn = null;
    projection.task = null;
    projection.latestTurn = {
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      status: 'stopped',
    };
    projection.latestControlResult = {
      outcome: 'rejected',
      reasonCode: 'NYXID_CHAT_STATE_VERSION_MISMATCH',
    };
    projection.latestStepControlResult = {
      outcome: 'idempotent',
      reasonCode: 'NYXID_CHAT_OPERATION_ALREADY_RECONCILED',
    };
    render(<ChatActorControls projection={projection} {...callbacks()} />);

    const committed = screen.getByRole('region', {
      name: 'Committed results',
    });
    expect(committed).toHaveTextContent('rejected');
    expect(committed).toHaveTextContent('NYXID_CHAT_STATE_VERSION_MISMATCH');
    expect(committed).toHaveTextContent('idempotent');
    expect(committed).toHaveTextContent(
      'NYXID_CHAT_OPERATION_ALREADY_RECONCILED',
    );
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('renders a committed reload summary without browser-cached action parameters', () => {
    const projection = projectionFixture([]);
    projection.actions.set('action-alpha', {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      reports: [],
      postconditionResult: null,
      request: null,
    });
    render(<ChatActorControls projection={projection} {...callbacks()} />);

    expect(
      screen.getByText('Waiting for the connection decision'),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/current-state contract does not expose/),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Open NyxID connection' }),
    ).not.toBeInTheDocument();
  });

  it('does not infer a stall from browser time without an actor-owned stalled fact', () => {
    const baseStep = stepFixture();
    if (!baseStep.operation) throw new Error('Missing operation fixture.');
    const runningStep = stepFixture({
      operation: {
        ...baseStep.operation,
        lastProgressAt: new Date(Date.now()).toISOString(),
        stalledAt: undefined,
      },
    });
    render(
      <ChatActorControls
        projection={projectionFixture([runningStep])}
        {...callbacks()}
      />,
    );

    act(() => jest.advanceTimersByTime(10 * 60_000));
    expect(screen.queryByText('Stalled')).not.toBeInTheDocument();
  });
});
