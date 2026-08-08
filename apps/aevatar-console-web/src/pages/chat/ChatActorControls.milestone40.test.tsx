import { fireEvent, render, screen, within } from '@testing-library/react';
import React from 'react';
import { ChatActorControls } from './ChatActorControls';
import {
  applyCurrentStateResult,
  type ChatActorProjection,
  createChatActorProjection,
  decodeActorFrame,
  reduceActorFrame,
} from './chatActorState';
import type { ChatActorStep, ChatTaskPlan } from './chatTaskPlan';

const resourceIds = {
  memberId: 'm-alpha',
  workflowId: 'wf-alpha',
  publishedServiceId: 'svc-alpha',
} as const;

function stepFixture(
  stepId: string,
  overrides: Partial<ChatActorStep> = {},
): ChatActorStep {
  return {
    stepId,
    order: 1,
    kind: 'tool',
    status: 'running',
    required: true,
    description: stepId,
    source: {
      kind: 'tool',
      label: 'repository_read',
      serviceSlug: 'github-api',
      serviceId: resourceIds.publishedServiceId,
    },
    mayChangeExternalState: false,
    externalEffect: 'not_started',
    availableActions: { retry: false, skip: false, stop: true },
    updatedAt: '2026-08-08T00:00:00Z',
    addedBy: 'initial',
    addedInPlanRevision: 1,
    dependsOn: [],
    substeps: [],
    operation: {
      conversationActorId: 'conversation-alpha',
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId,
      operationId: `operation-${stepId}`,
      operationGeneration: 1,
      kind: 'tool',
      phase: 'running',
    },
    ...overrides,
  };
}

function planFixture(
  steps: readonly ChatActorStep[],
  overrides: Partial<ChatTaskPlan> = {},
): ChatTaskPlan {
  const planRevision = overrides.planRevision ?? 1;
  const taskId = overrides.taskId ?? 'task-alpha';
  const turnId = overrides.turnId ?? 'turn-alpha';
  return {
    schemaVersion: 4,
    actorId: 'conversation-alpha',
    taskId,
    turnId,
    planId: `plan-${taskId}`,
    planRevision,
    planRevisionHistoryStart: 1,
    planRevisions: [
      {
        planRevision,
        revisionCause: planRevision === 1 ? 'initial' : 'steering',
        committedAt: '2026-08-08T00:00:00Z',
        addedStepIds: steps
          .filter((step) => step.addedInPlanRevision === planRevision)
          .map((step) => step.stepId),
        cancelledStepIds: steps
          .filter((step) => step.cancelledInPlanRevision === planRevision)
          .map((step) => step.stepId),
      },
    ],
    title: 'Milestone 40 task',
    status: 'active',
    activeStepId: steps.find((step) =>
      ['running', 'waiting'].includes(step.status),
    )?.stepId,
    gate: { mode: 'auto', status: 'satisfied' },
    steps,
    ...overrides,
  };
}

function liveProjection(plan: ChatTaskPlan): ChatActorProjection {
  return reduceActorFrame(
    createChatActorProjection('conversation-alpha'),
    decodeActorFrame({
      type: 'CUSTOM',
      sequence: 1,
      custom: { name: 'nyxid.task.snapshot', payload: wirePlan(plan) },
    }),
  );
}

function reloadProjection(
  plan: ChatTaskPlan,
  snapshotOverrides: Record<string, unknown> = {},
  stateVersion = 17,
): ChatActorProjection {
  return applyCurrentStateResult(
    createChatActorProjection('conversation-alpha'),
    {
      status: 'current',
      stateVersion,
      snapshot: {
        actorId: 'conversation-alpha',
        scopeId: 'scope-alpha',
        stateVersion,
        progressSequence: stateVersion,
        activeTurn:
          plan.status === 'active'
            ? { turnId: plan.turnId, taskId: plan.taskId, status: 'active' }
            : null,
        latestTurn: null,
        recentTerminalTurns: [],
        activeTask: wirePlan(plan),
        pendingInput: null,
        pendingApproval: null,
        pendingActions: [],
        ...snapshotOverrides,
      },
    },
  ).projection;
}

function wirePlan(plan: ChatTaskPlan): Record<string, unknown> {
  return {
    ...plan,
    steps: plan.steps.map((step) => ({
      ...step,
      source: wireSource(step),
    })),
  };
}

function wireSource(step: ChatActorStep): Record<string, unknown> {
  switch (step.source.kind) {
    case 'llm':
      return { llm: { model: step.source.label } };
    case 'tool':
      return {
        tool: {
          toolName: step.source.label,
          ...(step.source.serviceSlug
            ? { serviceSlug: step.source.serviceSlug }
            : {}),
          ...(step.source.serviceId
            ? { serviceId: step.source.serviceId }
            : {}),
        },
      };
    case 'browserAction':
      return { browserAction: { action: step.source.label } };
    case 'postcondition':
      return { postcondition: { check: step.source.label } };
    case 'input':
      return { input: {} };
    case 'approval':
      return { approval: {} };
    default:
      return { web: {} };
  }
}

function callbacks() {
  return {
    onActionConnectCredential: jest.fn(),
    onActionOpen: jest.fn(),
    onActionRefresh: jest.fn(),
    onActionReport: jest.fn(),
    onInputResolve: jest.fn(),
    onPlanResolve: jest.fn(),
    onRetry: jest.fn(),
    onSkip: jest.fn(),
    onSteer: jest.fn(),
    onStop: jest.fn(),
  };
}

describe('Milestone 40 actor-owned Studio fixtures', () => {
  it('UC1a keeps a disconnected connect action honest live and after reload', () => {
    expect(new Set(Object.values(resourceIds)).size).toBe(3);
    const connectStep = stepFixture('step-connect', {
      kind: 'browser_action',
      status: 'waiting',
      description: 'Connect GitHub',
      source: { kind: 'browserAction', label: 'service.connect' },
      actionRequestId: 'action-github-alpha',
      operation: null,
    });
    const plan = planFixture([connectStep], { title: 'Connect GitHub' });
    const action = {
      schemaVersion: 4 as const,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-github-alpha',
      action: 'service.connect' as const,
      params: {
        catalogService: {
          serviceSlug: 'api-github',
          requestedScopes: ['repo:read'],
        },
      },
    };
    const live = reduceActorFrame(
      liveProjection(plan),
      decodeActorFrame({
        type: 'CUSTOM',
        sequence: 2,
        custom: { name: 'nyxid.action.request', payload: action },
      }),
    );
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls projection={live} {...handlers} />,
    );

    expect(screen.getAllByText('Connect GitHub')).toHaveLength(3);
    expect(screen.getByText('repo:read')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Open NyxID connection' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('Task result')).not.toBeInTheDocument();

    const reloaded = reloadProjection(plan, {
      pendingActions: [
        {
          schemaVersion: 4,
          originTurnId: 'turn-alpha',
          taskId: 'task-alpha',
          stepId: 'step-connect',
          actionRequestId: 'action-github-alpha',
          action: 'service.connect',
          reports: [],
          postconditionResult: null,
        },
      ],
    });
    rerender(<ChatActorControls projection={reloaded} {...handlers} />);
    expect(screen.getAllByText('Connect GitHub')).toHaveLength(3);
    expect(
      screen.getByText('Waiting for the connection decision'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Open NyxID connection' }),
    ).not.toBeInTheDocument();
  });

  it('UC1b converges verified connection evidence and exactly one terminal fact', () => {
    const connectStep = stepFixture('step-connect', {
      kind: 'browser_action',
      status: 'done',
      description: 'Connect GitHub',
      source: { kind: 'browserAction', label: 'service.connect' },
      actionRequestId: 'action-github-alpha',
      externalEffect: 'confirmed',
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    });
    const verifyStep = stepFixture('step-verify', {
      order: 2,
      kind: 'postcondition',
      status: 'done',
      description: 'Verify GitHub connection',
      source: { kind: 'postcondition', label: 'service.connected' },
      externalEffect: 'confirmed',
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    });
    const plan = planFixture([connectStep, verifyStep], {
      status: 'succeeded',
      title: 'Use connected GitHub',
    });
    const terminal = {
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      status: 'completed',
      terminalAt: '2026-08-08T00:02:00Z',
    };
    const actionSummary = {
      schemaVersion: 4,
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-github-alpha',
      action: 'service.connect',
      reports: [
        {
          actionRequestId: 'action-github-alpha',
          originTurnId: 'turn-alpha',
          disposition: 'completed',
          resource: { userService: { userServiceId: 'usvc-github-alpha' } },
        },
      ],
      postconditionResult: {
        actionRequestId: 'action-github-alpha',
        disposition: 'completed',
        verified: true,
        resource: { userService: { userServiceId: 'usvc-github-alpha' } },
      },
    };
    const live = liveProjection(plan);
    live.latestTurn = terminal;
    live.actions.set('action-github-alpha', {
      ...actionSummary,
      actorId: 'conversation-alpha',
    });
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls projection={live} {...handlers} />,
    );
    expect(screen.getByText('Actor verified')).toBeInTheDocument();
    expect(
      screen.getByText('Verified against service.connected'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);

    const reloaded = reloadProjection(plan, {
      latestTurn: terminal,
      recentTerminalTurns: [terminal, terminal],
      pendingActions: [actionSummary],
    });
    rerender(<ChatActorControls projection={reloaded} {...handlers} />);
    expect(screen.getByText('Actor verified')).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);
  });

  it('UC2 preserves task identity through steering and starts a new task after stop', () => {
    const completedSearch = stepFixture('step-search', {
      status: 'done',
      description: 'Search dinner options',
      externalEffect: 'not_applied',
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    });
    const replaced = stepFixture('step-compare-original', {
      order: 2,
      status: 'cancelled',
      description: 'Compare original candidates',
      availableActions: { retry: false, skip: false, stop: false },
      cancelledInPlanRevision: 2,
      operation: null,
    });
    const replacement = stepFixture('step-compare-steered', {
      order: 3,
      description: 'Compare for 7 PM and a private room',
      addedBy: 'steering',
      addedInPlanRevision: 2,
      operation: {
        operationId: 'operation-steered',
        operationGeneration: 1,
        phase: 'running',
      },
    });
    const steeredPlan = planFixture([completedSearch, replaced, replacement], {
      planRevision: 2,
      planRevisions: [
        {
          planRevision: 2,
          revisionCause: 'steering',
          addedStepIds: ['step-compare-steered'],
          cancelledStepIds: ['step-compare-original'],
        },
      ],
      title: 'Dinner research',
    });
    const stoppedPlan = {
      ...steeredPlan,
      status: 'stopped' as const,
      activeStepId: undefined,
      steps: steeredPlan.steps.map((step) =>
        step.stepId === 'step-compare-steered'
          ? {
              ...step,
              status: 'cancelled' as const,
              availableActions: { retry: false, skip: false, stop: false },
            }
          : step,
      ),
    };
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls
        projection={liveProjection(steeredPlan)}
        {...handlers}
      />,
    );
    expect(screen.getByText('Plan revision 2')).toBeInTheDocument();
    expect(screen.getByText('addedBy: steering')).toBeInTheDocument();
    expect(screen.getByText('Compare original candidates')).toBeInTheDocument();
    expect(screen.getByText('cancelled')).toBeInTheDocument();
    expect(steeredPlan.taskId).toBe('task-alpha');

    const stopped = reloadProjection(stoppedPlan, {
      latestTurn: {
        turnId: 'turn-alpha',
        taskId: 'task-alpha',
        status: 'stopped',
        safeMessage: 'Partial research preserved.',
      },
    });
    rerender(<ChatActorControls projection={stopped} {...handlers} />);
    expect(screen.getAllByText('Task result')).toHaveLength(1);
    expect(screen.getByText('Partial research preserved.')).toBeInTheDocument();

    const newTask = planFixture(
      [
        stepFixture('step-new-search', {
          description: 'Restart dinner research',
        }),
      ],
      {
        taskId: 'task-dinner-new',
        turnId: 'turn-dinner-new',
        planId: 'plan-dinner-new',
        title: 'Dinner is back on',
      },
    );
    expect(newTask.taskId).not.toBe(stoppedPlan.taskId);
    rerender(
      <ChatActorControls
        projection={reloadProjection(newTask, {}, 18)}
        {...handlers}
      />,
    );
    expect(screen.getByText('Dinner is back on')).toBeInTheDocument();
    expect(
      screen.queryByText('Partial research preserved.'),
    ).not.toBeInTheDocument();
  });

  it('UC3 reconciles uncertain effects before actor-authorized generation N+1 retry', () => {
    const uncertain = stepFixture('step-submit-expense', {
      status: 'uncertain',
      description: 'Submit reimbursement',
      mayChangeExternalState: true,
      externalEffect: 'may_have_changed',
      availableActions: { retry: false, skip: false, stop: true },
      operation: {
        operationId: 'operation-expense',
        operationGeneration: 1,
        phase: 'uncertain',
      },
    });
    const handlers = callbacks();
    const { rerender } = render(
      <ChatActorControls
        projection={liveProjection(
          planFixture([uncertain], { title: 'Reimbursement' }),
        )}
        {...handlers}
      />,
    );
    expect(screen.getByText('may_have_changed')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: /Retry/ }),
    ).not.toBeInTheDocument();

    const reconciled = stepFixture('step-submit-expense', {
      status: 'failed',
      description: 'Submit reimbursement',
      mayChangeExternalState: true,
      externalEffect: 'not_applied',
      availableActions: { retry: true, skip: true, stop: true },
      addedBy: 'replan',
      addedInPlanRevision: 2,
      operation: {
        operationId: 'operation-expense',
        operationGeneration: 2,
        phase: 'failed',
      },
    });
    const reconcileRead = stepFixture('step-reconcile-expense', {
      order: 2,
      status: 'done',
      description: 'Reconcile Lark Approval',
      source: {
        kind: 'tool',
        label: 'lark_approval_list',
        serviceId: 'svc-lark-alpha',
      },
      externalEffect: 'not_applied',
      availableActions: { retry: false, skip: false, stop: false },
      addedBy: 'replan',
      addedInPlanRevision: 2,
      operation: null,
    });
    const reconciledPlan = planFixture([reconciled, reconcileRead], {
      planRevision: 2,
      title: 'Reimbursement',
    });
    rerender(
      <ChatActorControls
        projection={reloadProjection(reconciledPlan)}
        {...handlers}
      />,
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Retry Submit reimbursement' }),
    );
    expect(handlers.onRetry).toHaveBeenCalledWith(
      expect.objectContaining({
        stepId: 'step-submit-expense',
        operation: expect.objectContaining({ operationGeneration: 2 }),
      }),
    );

    const retried = {
      ...reconciled,
      status: 'running' as const,
      externalEffect: 'not_started' as const,
      availableActions: { retry: false, skip: false, stop: true },
      operation: {
        ...reconciled.operation,
        operationGeneration: 3,
        phase: 'running',
      },
    };
    const retryPlan = planFixture([retried, reconcileRead], {
      planRevision: 3,
      title: 'Reimbursement retry',
    });
    const retryReload = reloadProjection(retryPlan, {
      pendingApproval: {
        approvalRequestId: 'approval-expense-retry',
        toolName: 'lark_approval_create',
        grantBoundary: 'nyxid_step_up',
        nyxidRequestId: 'nyxid-expense-retry',
      },
    });
    rerender(<ChatActorControls projection={retryReload} {...handlers} />);
    expect(screen.getByText(/generation 3/)).toBeInTheDocument();
    expect(screen.getByText('NyxID request observed')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
  });

  it('UC4 preserves threshold override, conditional outcomes, Tier-B reload, and actor stall facts', () => {
    const waitingWrite = stepFixture('step-write-candidate', {
      order: 2,
      status: 'waiting',
      description: 'Write candidate when score is at least 75',
      mayChangeExternalState: true,
      source: {
        kind: 'tool',
        label: 'lark_bitable_create',
        serviceId: 'svc-lark-alpha',
      },
      operation: {
        operationId: 'operation-candidate',
        operationGeneration: 1,
        phase: 'waiting',
        lastProgressAt: '2026-08-08T00:03:00Z',
        stalledAt: '2026-08-08T00:05:00Z',
      },
    });
    const waitingPlan = planFixture(
      [
        stepFixture('step-score', {
          status: 'done',
          description: 'Score candidate against supplied rubric',
          availableActions: { retry: false, skip: false, stop: false },
          operation: null,
        }),
        waitingWrite,
      ],
      { planRevision: 2, title: 'Candidate screening at 75' },
    );
    const handlers = callbacks();
    const waiting = liveProjection(waitingPlan);
    waiting.pendingInput = {
      requestId: 'input-threshold',
      prompt: 'Suggested threshold is 70. Set the screening threshold.',
      options: [],
      allowFreeText: true,
      multiSelect: false,
    };
    waiting.pendingApproval = {
      approvalRequestId: 'approval-candidate',
      toolName: 'lark_bitable_create',
      grantBoundary: 'nyxid_step_up',
    };
    const { rerender } = render(
      <ChatActorControls projection={waiting} {...handlers} />,
    );
    expect(screen.getByText(/Suggested threshold is 70/)).toBeInTheDocument();
    expect(screen.getByText('Candidate screening at 75')).toBeInTheDocument();
    expect(screen.getByText('Stalled')).toBeInTheDocument();
    expect(
      screen.getByText('Last progress 2026-08-08T00:03:00Z'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Stalled since 2026-08-08T00:05:00Z'),
    ).toBeInTheDocument();
    expect(screen.queryByText('NyxID decision')).not.toBeInTheDocument();

    const skippedWrite = {
      ...waitingWrite,
      status: 'skipped' as const,
      externalEffect: 'not_applied' as const,
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    };
    rerender(
      <ChatActorControls
        projection={reloadProjection(
          planFixture([waitingPlan.steps[0], skippedWrite], {
            status: 'succeeded',
            planRevision: 2,
            title: 'Candidate below 75',
          }),
        )}
        {...handlers}
      />,
    );
    expect(screen.getByText('skipped')).toBeInTheDocument();
    expect(screen.getByText('not_applied')).toBeInTheDocument();

    const executedWrite = {
      ...waitingWrite,
      status: 'done' as const,
      externalEffect: 'confirmed' as const,
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    };
    const verify = stepFixture('step-verify-candidate', {
      order: 3,
      kind: 'postcondition',
      status: 'done',
      description: 'Read candidate row back',
      source: { kind: 'postcondition', label: 'bitable.row.exists' },
      externalEffect: 'confirmed',
      availableActions: { retry: false, skip: false, stop: false },
      operation: null,
    });
    const executedPlan = planFixture(
      [waitingPlan.steps[0], executedWrite, verify],
      { status: 'succeeded', planRevision: 2, title: 'Candidate above 75' },
    );
    const executed = reloadProjection(executedPlan, {
      pendingApproval: {
        approvalRequestId: 'approval-candidate',
        toolName: 'lark_bitable_create',
        grantBoundary: 'nyxid_step_up',
        nyxidRequestId: 'nyxid-candidate-alpha',
      },
      latestApprovalResolution: {
        requestId: 'approval-candidate',
        outcome: 'approved',
        committedAt: '2026-08-08T00:08:00Z',
      },
    });
    rerender(<ChatActorControls projection={executed} {...handlers} />);
    expect(screen.getByText('NyxID request observed')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByText('Verified against bitable.row.exists'),
    ).toBeInTheDocument();
    expect(
      within(screen.getByLabelText('Committed results')).getByText('approved'),
    ).toBeInTheDocument();
  });
});
