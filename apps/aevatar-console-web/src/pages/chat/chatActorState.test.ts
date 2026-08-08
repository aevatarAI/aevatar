import {
  actorCan,
  applyCurrentStateResult,
  createChatActorProjection,
  decodeActorFrame,
  reduceActorFrame,
  validateActionRequest,
} from './chatActorState';

function taskPlan(stepStatus: 'running' | 'failed' = 'running') {
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
        addedStepIds: ['step-alpha'],
        cancelledStepIds: [],
      },
    ],
    title: 'Inspect repository',
    status: 'active',
    activeStepId: 'step-alpha',
    gate: {
      mode: 'confirm',
      status: 'satisfied',
      requestId: 'gate-alpha',
      taskId: 'task-alpha',
      planId: 'plan-alpha',
      planRevision: 3,
    },
    steps: [
      {
        stepId: 'step-alpha',
        order: 1,
        kind: 'tool',
        status: stepStatus,
        required: true,
        description: 'Inspect repository',
        source: {
          tool: {
            toolName: 'repository_read',
            serviceSlug: 'github-api',
            serviceId: 'svc-alpha',
          },
        },
        mayChangeExternalState: false,
        externalEffect: stepStatus === 'failed' ? 'not_applied' : 'not_started',
        availableActions: {
          retry: stepStatus === 'failed',
          skip: false,
          stop: stepStatus === 'running',
        },
        updatedAt: '2026-08-08T00:00:00Z',
        addedBy: 'steering',
        addedInPlanRevision: 3,
        dependsOn: [],
        substeps: [
          {
            substepId: 'substep-alpha',
            title: 'Read',
            status: stepStatus === 'failed' ? 'failed' : 'running',
          },
        ],
        operation: {
          conversationActorId: 'conversation-alpha',
          turnId: 'turn-alpha',
          taskId: 'task-alpha',
          stepId: 'step-alpha',
          operationId: 'operation-alpha',
          operationGeneration: 2,
          phase: stepStatus,
        },
      },
    ],
  };
}

const actionRequest = {
  schemaVersion: 4,
  actorId: 'conversation-alpha',
  originTurnId: 'turn-alpha',
  taskId: 'task-alpha',
  stepId: 'step-connect',
  actionRequestId: 'action-alpha',
  action: 'service.connect',
  params: {
    catalogService: { serviceSlug: 'api-github', requestedScopes: ['repo'] },
  },
} as const;

describe('chatActorState', () => {
  it('uses the same typed TaskPlan decoder for live frames and current-state reload', () => {
    const live = reduceActorFrame(
      createChatActorProjection('conversation-alpha'),
      decodeActorFrame({
        type: 'CUSTOM',
        sequence: 7,
        custom: { name: 'nyxid.task.snapshot', payload: taskPlan() },
      }),
    );
    const reloaded = applyCurrentStateResult(
      createChatActorProjection('conversation-alpha'),
      {
        status: 'current',
        stateVersion: 17,
        snapshot: {
          actorId: 'conversation-alpha',
          scopeId: 'scope-alpha',
          stateVersion: 17,
          progressSequence: 7,
          activeTurn: {
            turnId: 'turn-alpha',
            taskId: 'task-alpha',
            status: 'active',
          },
          latestTurn: null,
          recentTerminalTurns: [],
          activeTask: taskPlan(),
          pendingInput: null,
          pendingApproval: null,
          pendingActions: [],
        },
      },
    ).projection;

    expect(reloaded.task).toEqual(live.task);
    expect([...reloaded.steps.values()]).toEqual([...live.steps.values()]);
    expect(actorCan(reloaded, 'stop')).toBe(true);

    const changed = reduceActorFrame(
      reloaded,
      decodeActorFrame({
        type: 'CUSTOM',
        sequence: 8,
        custom: {
          name: 'nyxid.task.step.changed',
          payload: {
            taskId: 'task-alpha',
            planRevision: 3,
            step: {
              ...taskPlan('failed').steps[0],
              updatedAt: '2026-08-08T00:02:00Z',
            },
            changeKind: 'status',
          },
        },
      }),
    );
    expect(changed.steps.get('step-alpha')?.status).toBe('failed');
    expect(changed.steps.get('step-alpha')?.updatedAt).toBe(
      '2026-08-08T00:02:00Z',
    );
    expect(actorCan(changed, 'retry', 'step-alpha')).toBe(true);
  });

  it('fails closed on invalid closed vocabulary instead of constructing browser state', () => {
    expect(() =>
      decodeActorFrame({
        type: 'CUSTOM',
        sequence: 7,
        custom: {
          name: 'nyxid.task.snapshot',
          payload: { ...taskPlan(), status: 'almost_done' },
        },
      }),
    ).not.toThrow();
    expect(() =>
      reduceActorFrame(
        createChatActorProjection('conversation-alpha'),
        decodeActorFrame({
          type: 'CUSTOM',
          sequence: 7,
          custom: {
            name: 'nyxid.task.snapshot',
            payload: { ...taskPlan(), status: 'almost_done' },
          },
        }),
      ),
    ).toThrow(expect.objectContaining({ code: 'NYXID_TASK_PLAN_INVALID' }));
  });

  it('accepts only secret-free exact action identities and never reloads params from browser storage', () => {
    expect(validateActionRequest(actionRequest)).toEqual(actionRequest);
    expect(() =>
      validateActionRequest({ ...actionRequest, apiKey: 'secret' }),
    ).toThrow(expect.objectContaining({ code: 'NYXID_FIELD_UNDECLARED' }));

    const live = reduceActorFrame(
      createChatActorProjection('conversation-alpha'),
      decodeActorFrame({
        sequence: 1,
        custom: { name: 'nyxid.action.request', payload: actionRequest },
      }),
    );
    expect(live.actions.get('action-alpha')?.request).toEqual(actionRequest);

    const reload = applyCurrentStateResult(
      createChatActorProjection('conversation-alpha'),
      {
        status: 'current',
        stateVersion: 5,
        snapshot: {
          actorId: 'conversation-alpha',
          scopeId: 'scope-alpha',
          stateVersion: 5,
          progressSequence: 2,
          activeTurn: null,
          latestTurn: null,
          recentTerminalTurns: [],
          activeTask: null,
          pendingInput: null,
          pendingApproval: null,
          pendingActions: [
            {
              schemaVersion: 4,
              originTurnId: 'turn-alpha',
              taskId: 'task-alpha',
              stepId: 'step-connect',
              actionRequestId: 'action-alpha',
              action: 'service.connect',
              reports: [],
              postconditionResult: null,
            },
          ],
        },
      },
    ).projection;
    expect(reload.actions.get('action-alpha')?.request).toBeUndefined();
  });
});
