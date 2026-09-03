import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { authFetch } from '@/shared/auth/fetch';
import { history } from '@/shared/navigation/history';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import { chatHistoryApi } from './chatHistoryApi';
import ChatPage, { hydrateStoredMessages } from './index';
import { createNyxIdCatalogKey, listNyxIdConnectors } from './nyxIdServiceApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('./chatHistoryApi', () => ({
  chatHistoryApi: {
    deleteConversation: jest.fn(),
    listConversationMetas: jest.fn(),
    loadConversation: jest.fn(),
    loadConversationState: jest.fn(),
  },
}));
jest.mock('./nyxIdServiceApi', () => ({
  buildNyxIdConnectUrl: jest.fn(
    () => 'https://nyx.example/keys?slug=api-github',
  ),
  createNyxIdCatalogKey: jest.fn(),
  listNyxIdConnectors: jest.fn(),
  matchNewUserServiceId:
    jest.requireActual('./nyxIdServiceApi').matchNewUserServiceId,
  matchingUserServiceIds:
    jest.requireActual('./nyxIdServiceApi').matchingUserServiceIds,
}));
jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(),
  },
}));
jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn() },
}));
jest.mock('@/shared/ui/aevatarPageShells', () => {
  const mockReact = require('react');
  return {
    AevatarPageShell: ({ children, title }: any) =>
      mockReact.createElement(
        'section',
        null,
        title ? mockReact.createElement('h1', null, title) : null,
        children,
      ),
  };
});

const realChatHistoryApi =
  jest.requireActual('./chatHistoryApi').chatHistoryApi;

const serverConversation = {
  createdAt: '2026-08-04T02:30:00+00:00',
  id: 'conversation-alpha',
  messageCount: 1,
  title: 'Canonical conversation',
  updatedAt: '2026-08-04T02:35:00+00:00',
};

function currentState(
  overrides: Record<string, unknown> = {},
  stateVersion = 7,
) {
  return {
    status: 'current',
    stateVersion,
    turnId: 'turn-alpha',
    snapshot: {
      actorId: 'conversation-alpha',
      scopeId: 'scope-alpha',
      stateVersion,
      progressSequence: stateVersion,
      activeTurn: null,
      latestTurn: {
        turnId: 'turn-alpha',
        taskId: 'task-alpha',
        status: 'succeeded',
      },
      recentTerminalTurns: [],
      activeTask: null,
      pendingInput: null,
      pendingApproval: null,
      pendingActions: [],
      recentActions: [],
      ...overrides,
    },
  };
}

function taskStep(stepId: string, overrides: Record<string, unknown> = {}) {
  return {
    stepId,
    order: 1,
    kind: 'tool',
    status: 'running',
    required: true,
    description: stepId,
    source: {
      tool: {
        toolName: 'repository_read',
        serviceSlug: 'github-api',
        serviceId: 'svc-alpha',
      },
    },
    mayChangeExternalState: false,
    externalEffect: 'not_started',
    availableActions: { stop: true },
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

function taskPlan(
  steps: readonly Record<string, unknown>[],
  overrides: Record<string, unknown> = {},
) {
  const planRevision = Number(overrides.planRevision ?? 1);
  const { planRevisions, ...planOverrides } = overrides;
  return {
    schemaVersion: 4,
    actorId: 'conversation-alpha',
    taskId: 'task-alpha',
    turnId: 'turn-alpha',
    planId: 'plan-alpha',
    planRevision,
    planRevisionHistoryStart: 1,
    planRevisions: planRevisions ?? [
      {
        planRevision,
        revisionCause: planRevision === 1 ? 'initial' : 'failure_recovery',
        addedStepIds: steps.map((step) => String(step.stepId)),
        cancelledStepIds: [],
      },
    ],
    title: 'Milestone 40 task',
    status: 'active',
    activeStepId: steps.find((step) =>
      ['running', 'waiting', 'uncertain'].includes(String(step.status)),
    )?.stepId,
    steps,
    ...planOverrides,
  };
}

function numericCondition(observedValue: number, outcome: 'true' | 'false') {
  return {
    condition: {
      condition: {
        conditionId: `condition-${outcome}`,
        sourceInputRequestId: 'input-threshold',
        suggestedThreshold: 70,
        effectiveThreshold: 75,
        thresholdOrigin: 'user_override',
        observedValue,
        comparison: 'gte',
        outcome,
        evaluatedAt: '2026-08-11T13:01:00Z',
        guardedToolName: 'external_record_create',
      },
    },
  };
}

function sseResponse(frames: readonly unknown[]): Response {
  const encoder = new TextEncoder();
  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(
          encoder.encode(
            frames
              .map((frame) => `data: ${JSON.stringify(frame)}\n\n`)
              .join(''),
          ),
        );
        controller.close();
      },
    }),
    ok: true,
    status: 200,
  } as Response;
}

function jsonResponse(payload: unknown, status = 200): Response {
  return {
    headers: new Headers({ 'content-type': 'application/json' }),
    json: jest.fn().mockResolvedValue(payload),
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 202 ? 'Accepted' : 'OK',
  } as unknown as Response;
}

function runStarted(
  conversationId = 'conversation-alpha',
  turnId = 'turn-alpha',
) {
  return {
    type: 'RUN_STARTED',
    actorId: conversationId,
    turnId,
    runStarted: { threadId: conversationId, runId: turnId },
  };
}

function completedStream(
  output: string,
  conversationId = 'conversation-alpha',
  turnId = 'turn-alpha',
  extra: readonly unknown[] = [],
): Response {
  return sseResponse([
    runStarted(conversationId, turnId),
    ...extra,
    {
      type: 'TEXT_MESSAGE_CONTENT',
      textMessageContent: { delta: output },
    },
    { type: 'RUN_FINISHED', runFinished: { runId: turnId } },
  ]);
}

function openSseResponse(frames: readonly unknown[]): {
  close: () => void;
  response: Response;
} {
  const encoder = new TextEncoder();
  let close = () => {};
  const response = {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(
          encoder.encode(
            frames
              .map((frame) => `data: ${JSON.stringify(frame)}\n\n`)
              .join(''),
          ),
        );
        close = () => controller.close();
      },
    }),
    ok: true,
    status: 200,
  } as Response;
  return { close: () => close(), response };
}

function requestBodies(): Record<string, unknown>[] {
  return (authFetch as jest.Mock).mock.calls
    .filter(([path]) => path === '/api/chat')
    .map(([, request]) => JSON.parse(request.body));
}

function useRealChatHistoryApi(): void {
  (chatHistoryApi.listConversationMetas as jest.Mock).mockImplementation(
    realChatHistoryApi.listConversationMetas,
  );
  (chatHistoryApi.loadConversation as jest.Mock).mockImplementation(
    realChatHistoryApi.loadConversation,
  );
  (chatHistoryApi.loadConversationState as jest.Mock).mockImplementation(
    realChatHistoryApi.loadConversationState,
  );
}

async function sendPrompt(prompt: string): Promise<void> {
  await screen.findByText('Scope scope-alpha');
  const input = await screen.findByPlaceholderText(
    'Describe the workflow you want, or ask about the current setup...',
  );
  fireEvent.change(input, { target: { value: prompt } });
  await waitFor(() =>
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled(),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Send' }));
}

const activeTurn = {
  turnId: 'turn-alpha',
  taskId: 'task-alpha',
  status: 'active',
};

function activeTaskState(
  plan: Record<string, unknown>,
  stateVersion: number,
  overrides: Record<string, unknown> = {},
) {
  return currentState(
    {
      activeTurn,
      latestTurn: null,
      activeTask: plan,
      ...overrides,
    },
    stateVersion,
  );
}

async function openCanonicalConversation(): Promise<void> {
  fireEvent.click(
    await screen.findByRole('button', { name: 'Canonical conversation' }),
  );
}

function taskRow(description: string): HTMLElement {
  const row = screen
    .getAllByText(description)
    .map((element) => element.closest('li'))
    .find((element): element is HTMLLIElement => element !== null);
  if (!row) throw new Error(`Expected ${description} to render in a task row.`);
  return row;
}

describe('ChatPage canonical NyxID Assistant', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.replaceState({}, '', '/chat');
    window.sessionStorage.clear();
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      authenticated: true,
      enabled: true,
      scopeId: 'scope-alpha',
      scopeSource: 'nyxid',
    });
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue({
      messages: [],
      stateVersion: 0,
    });
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue({
      status: 'not_found',
    });
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(
      undefined,
    );
    (listNyxIdConnectors as jest.Mock).mockResolvedValue({
      connected: [],
      available: [],
    });
  });

  it('sends typed first and continuation turns using RUN_STARTED identity', async () => {
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(completedStream('First answer'))
      .mockResolvedValueOnce(
        completedStream('Second answer', 'conversation-alpha', 'turn-beta'),
      );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Create a workflow');
    expect(await screen.findByText('First answer')).toBeInTheDocument();
    await sendPrompt('Continue safely');
    expect(await screen.findByText('Second answer')).toBeInTheDocument();

    const [first, second] = requestBodies();
    expect(first).toEqual({
      type: 'text',
      prompt: 'Create a workflow',
      clientRequestId: expect.any(String),
    });
    expect(second).toEqual({
      type: 'text',
      conversationId: 'conversation-alpha',
      prompt: 'Continue safely',
      clientRequestId: expect.any(String),
    });
    expect(second.clientRequestId).not.toBe(first.clientRequestId);
    expect(first).not.toHaveProperty('scopeId');
    expect(first).not.toHaveProperty('sessionId');
    expect(first).not.toHaveProperty('workflow');
    expect(first).not.toHaveProperty('conversation');
  });

  it('renders recovered workflow signal continuation accepted through chat API', async () => {
    let chatCallCount = 0;
    (authFetch as jest.Mock).mockImplementation((path: string) => {
      if (path === '/api/chat') {
        chatCallCount += 1;
        if (chatCallCount === 1) {
          return Promise.resolve(
            completedStream('Please choose one dinner option.', 'conversation-alpha', 'turn-alpha'),
          );
        }
        return Promise.resolve(
          jsonResponse(
            {
              accepted: true,
              actorId: 'scope-workflow-alpha',
              runId: 'run-dinner-alpha',
              routed: 'workflow_signal_continuation',
              signalName: 'dinner_date_user_choice_after_timeout',
              stepId: 'wait_for_post_timeout_choice',
            },
            202,
          ),
        );
      }
      if (path === '/api/workflow-actors/scope-workflow-alpha/current-state') {
        return Promise.resolve(
          jsonResponse({
            actorId: 'scope-workflow-alpha',
            completionStatus: 'Completed',
            lastOutput: JSON.stringify({ kept: 'Tipo Pasta Bar' }),
            runId: 'run-dinner-alpha',
          }),
        );
      }
      throw new Error(`Unexpected authFetch path: ${path}`);
    });

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Book dinner tonight');
    expect(await screen.findByText('Please choose one dinner option.')).toBeInTheDocument();
    await sendPrompt('2');

    expect(await screen.findByText('2')).toBeInTheDocument();
    expect(
      await screen.findByText('Tipo Pasta Bar is selected.'),
    ).toBeInTheDocument();
    expect(requestBodies()).toHaveLength(2);
    expect(requestBodies()[1]).toMatchObject({
      conversationId: 'conversation-alpha',
      prompt: '2',
      type: 'text',
    });
  });

  it('submits pending workflow signal from the composer after the chat turn completes', async () => {
    const waitingSignal = {
      actorId: 'scope-workflow-alpha',
      prompt: 'Choose one dinner option.',
      runId: 'run-dinner-alpha',
      signalName: 'dinner_date_user_choice_after_timeout',
      stepId: 'wait_for_post_timeout_choice',
      timeoutMs: 60_000,
    };
    (authFetch as jest.Mock).mockImplementation(
      (path: string, request: RequestInit) => {
        if (path === '/api/chat') {
          return Promise.resolve(
            completedStream('The workflow is waiting for your choice.', 'conversation-alpha', 'turn-alpha', [
              {
                type: 'CUSTOM',
                custom: {
                  data: waitingSignal,
                  name: 'aevatar.workflow.waiting_signal',
                  payload: waitingSignal,
                },
              },
            ]),
          );
        }
        if (path === '/api/scopes/scope-alpha/runs/run-dinner-alpha:signal') {
          return Promise.resolve(
            jsonResponse({
              accepted: true,
              runId: 'run-dinner-alpha',
              signalName: 'dinner_date_user_choice_after_timeout',
              stepId: 'wait_for_post_timeout_choice',
            }),
          );
        }
        if (path === '/api/workflow-actors/scope-workflow-alpha/current-state') {
          return Promise.resolve(
            jsonResponse({
              actorId: 'scope-workflow-alpha',
              completionStatus: 'Completed',
              lastOutput: JSON.stringify({ kept: 'Tipo Pasta Bar' }),
              runId: 'run-dinner-alpha',
            }),
          );
        }
        throw new Error(`Unexpected authFetch path: ${path}`);
      },
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Book dinner tonight');
    const signalInput = await screen.findByPlaceholderText(
      'Send your workflow choice...',
    );
    fireEvent.change(signalInput, { target: { value: 'Use option 2' } });
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled(),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() =>
      expect(authFetch).toHaveBeenCalledWith(
        '/api/scopes/scope-alpha/runs/run-dinner-alpha:signal',
        expect.objectContaining({
          body: JSON.stringify({
            actorId: 'scope-workflow-alpha',
            runId: 'run-dinner-alpha',
            signalName: 'dinner_date_user_choice_after_timeout',
            stepId: 'wait_for_post_timeout_choice',
            payload: 'Use option 2',
          }),
          method: 'POST',
        }),
      ),
    );
    expect(await screen.findByText('Use option 2')).toBeInTheDocument();
    expect(
      await screen.findByText('Tipo Pasta Bar is selected.'),
    ).toBeInTheDocument();
    expect(requestBodies()).toHaveLength(1);
  });

  it('routes workflow start receipt waiting signal from the composer without opening a new chat turn', async () => {
    const result = JSON.stringify({
      actor_id: 'scope-workflow-alpha',
      command_id: 'command-alpha',
      mutation_stage: 'read_model_observed',
      run_id: 'run-dinner-alpha',
      status: 'waiting_for_signal',
      waiting_signal: {
        prompt: 'Choose one dinner option.',
        run_id: 'run-dinner-alpha',
        signal_name: 'dinner_date_user_choice',
        step_id: 'wait_for_user_choice_timeout',
        timeout_ms: 10000,
      },
    });
    (authFetch as jest.Mock).mockImplementation(
      (path: string, request: RequestInit) => {
        if (path === '/api/chat') {
          return Promise.resolve(
            completedStream('The workflow is waiting for your choice.', 'conversation-alpha', 'turn-alpha', [
              {
                type: 'TOOL_CALL_START',
                toolCallStart: {
                  toolCallId: 'call-workflow-start',
                  toolName: 'aevatar_start_workflow',
                },
              },
              {
                type: 'TOOL_CALL_END',
                toolCallEnd: {
                  result,
                  toolCallId: 'call-workflow-start',
                },
              },
            ]),
          );
        }
        if (path === '/api/scopes/scope-alpha/runs/run-dinner-alpha:signal') {
          return Promise.resolve(
            jsonResponse({
              accepted: true,
              runId: 'run-dinner-alpha',
              signalName: 'dinner_date_user_choice',
              stepId: 'wait_for_user_choice_timeout',
            }),
          );
        }
        if (path === '/api/workflow-actors/scope-workflow-alpha/current-state') {
          return Promise.resolve(
            jsonResponse({
              completionStatus: 'Completed',
              lastOutput: 'Pasta Bar is selected.',
              runId: 'run-dinner-alpha',
            }),
          );
        }
        throw new Error(`Unexpected authFetch path: ${path}`);
      },
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Book dinner tonight');
    const signalInput = await screen.findByPlaceholderText(
      'Send your workflow choice...',
    );
    fireEvent.change(signalInput, { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));

    await waitFor(() =>
      expect(authFetch).toHaveBeenCalledWith(
        '/api/scopes/scope-alpha/runs/run-dinner-alpha:signal',
        expect.objectContaining({
          body: JSON.stringify({
            actorId: 'scope-workflow-alpha',
            runId: 'run-dinner-alpha',
            signalName: 'dinner_date_user_choice',
            stepId: 'wait_for_user_choice_timeout',
            payload: '1',
          }),
          method: 'POST',
        }),
      ),
    );
    expect(await screen.findByText('1')).toBeInTheDocument();
    expect(await screen.findByText('Pasta Bar is selected.')).toBeInTheDocument();
    expect(requestBodies()).toHaveLength(1);
  });

  it('enables actor-authorized stop after current state materializes during an active SSE', async () => {
    const activeTurnId = 'turn-active';
    const activePlan = taskPlan(
      [
        taskStep('step-active', {
          operation: {
            conversationActorId: 'conversation-alpha',
            turnId: activeTurnId,
            taskId: 'task-active',
            stepId: 'step-active',
            operationId: 'operation-step-active',
            operationGeneration: 1,
            kind: 'tool',
            phase: 'running',
          },
        }),
      ],
      { taskId: 'task-active', turnId: activeTurnId },
    );
    (chatHistoryApi.loadConversationState as jest.Mock)
      .mockResolvedValueOnce({ status: 'not_found' })
      .mockResolvedValue(
        activeTaskState(activePlan, 10, {
          activeTurn: {
            turnId: activeTurnId,
            taskId: 'task-active',
            status: 'active',
          },
        }),
      );
    const stream = openSseResponse([
      runStarted('conversation-alpha', activeTurnId),
      {
        type: 'CUSTOM',
        sequence: 10,
        custom: { name: 'nyxid.task.snapshot', payload: activePlan },
      },
      {
        type: 'TEXT_MESSAGE_CONTENT',
        textMessageContent: { delta: 'Still working.' },
      },
    ]);
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(stream.response)
      .mockResolvedValueOnce({ ok: true, status: 202 });

    renderWithQueryClient(<ChatPage />);
    try {
      await sendPrompt('Continue the active work');
      await waitFor(() => expect(requestBodies()).toHaveLength(1));
      const stop = await screen.findByRole('button', { name: 'Stop task' });
      await waitFor(() => expect(stop).toBeEnabled());
      expect(chatHistoryApi.loadConversationState).toHaveBeenCalledTimes(2);
      fireEvent.click(stop);
      await waitFor(() => expect(requestBodies()).toHaveLength(2));
      expect(requestBodies()[1]).toEqual({
        type: 'task.stop',
        conversationId: 'conversation-alpha',
        turnId: activeTurnId,
        stopRequestId: expect.any(String),
        clientRequestId: expect.any(String),
        expectedStateVersion: 10,
      });
    } finally {
      stream.close();
      await waitFor(() =>
        expect(screen.getByRole('button', { name: 'New Chat' })).toBeEnabled(),
      );
    }
  });

  it('routes Studio with distinct member, workflow, and published service identities', async () => {
    const memberId = 'm-alpha';
    const workflowId = 'wf-alpha';
    const publishedServiceId = 'svc-alpha';
    const plan = taskPlan([
      taskStep('step-published-service', {
        source: {
          tool: {
            toolName: 'repository_read',
            serviceSlug: 'github-api',
            serviceId: publishedServiceId,
          },
        },
      }),
    ]);
    (authFetch as jest.Mock).mockResolvedValue(
      sseResponse([
        runStarted(),
        {
          type: 'CUSTOM',
          sequence: 1,
          custom: { name: 'nyxid.task.snapshot', payload: plan },
        },
        {
          type: 'TEXT_MESSAGE_CONTENT',
          textMessageContent: { delta: 'Studio target ready' },
        },
        {
          type: 'RUN_FINISHED',
          runFinished: {
            runId: 'turn-alpha',
            result: {
              scopeId: 'scope-alpha',
              teamId: 'team-alpha',
              memberId,
              workflowId,
              publishedServiceId,
            },
          },
        },
      ]),
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Open the published service in Studio');
    fireEvent.click(
      await screen.findByRole('button', { name: 'Open Workflow Studio' }),
    );

    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/teams/team-alpha/members/m-alpha/workflow?workflowId=wf-alpha',
    );
    expect((history.push as jest.Mock).mock.calls[0][0]).not.toContain(
      publishedServiceId,
    );
  });

  it('restores transcript and current state from canonical conversation resources', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue({
      messages: [
        {
          id: 'turn-alpha:user',
          turnId: 'turn-alpha',
          role: 'user',
          content: 'Hello',
          timestamp: 1,
          status: 'complete',
        },
        {
          id: 'turn-alpha:assistant',
          turnId: 'turn-alpha',
          role: 'assistant',
          content: 'Restored answer',
          timestamp: 2,
          status: 'complete',
        },
      ],
      stateVersion: 7,
    });
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState(),
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole('button', { name: 'Canonical conversation' }),
    );

    expect(await screen.findByText('Restored answer')).toBeInTheDocument();
    expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledWith();
    expect(chatHistoryApi.loadConversation).toHaveBeenCalledWith(
      'conversation-alpha',
    );
    expect(chatHistoryApi.loadConversationState).toHaveBeenCalledWith(
      'conversation-alpha',
    );
  });

  it('UC1a renders live and reloaded committed connect requests identically', async () => {
    useRealChatHistoryApi();
    let reloading = false;
    const connectStep = taskStep('step-connect', {
      kind: 'browser_action',
      status: 'waiting',
      description: 'Connect GitHub',
      source: { browserAction: { action: 'service.connect' } },
      actionRequestId: 'action-alpha',
      operation: null,
    });
    const connectPlan = taskPlan([connectStep], {
      title: 'Connect and inspect GitHub',
    });
    const action = {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      params: {
        catalogService: {
          serviceSlug: 'api-github',
          requestedScopes: ['repo:read'],
        },
      },
    };
    const pendingState = activeTaskState(connectPlan, 7, {
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
          request: action,
        },
      ],
    });
    const stream = completedStream(
      'Connection required',
      'conversation-alpha',
      'turn-alpha',
      [
        {
          type: 'CUSTOM',
          sequence: 1,
          custom: { name: 'nyxid.task.snapshot', payload: connectPlan },
        },
        {
          type: 'CUSTOM',
          sequence: 2,
          custom: { name: 'nyxid.action.request', payload: action },
        },
      ],
    );
    (authFetch as jest.Mock).mockImplementation((path: string) => {
      if (path === '/api/chat') return Promise.resolve(stream);
      if (path === '/api/chat/conversations') {
        return Promise.resolve(
          jsonResponse({
            conversations: reloading ? [serverConversation] : [],
            nextCursor: null,
          }),
        );
      }
      if (path === '/api/chat/conversations/conversation-alpha/state') {
        return Promise.resolve(jsonResponse(pendingState));
      }
      if (path === '/api/chat/conversations/conversation-alpha') {
        return Promise.resolve(
          jsonResponse({
            messages: [],
            projectionStatus: 'current',
            stateVersion: 7,
          }),
        );
      }
      throw new Error(`Unexpected authFetch path: ${path}`);
    });

    const liveView = renderWithQueryClient(<ChatPage />);
    await sendPrompt('Connect GitHub and inspect the repository');
    expect(
      await screen.findByRole('button', { name: 'Open NyxID connection' }),
    ).toBeInTheDocument();
    expect(screen.getByText('repo:read')).toBeInTheDocument();
    expect(requestBodies()).toEqual([
      {
        type: 'text',
        prompt: 'Connect GitHub and inspect the repository',
        clientRequestId: expect.any(String),
      },
    ]);

    liveView.unmount();
    reloading = true;
    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(
      await screen.findByRole('button', { name: 'Open NyxID connection' }),
    ).toBeInTheDocument();
    expect(screen.getByText('repo:read')).toBeInTheDocument();
    expect(screen.getAllByText('Connect GitHub')).toHaveLength(2);
    expect(
      screen.queryByText(/current-state contract does not expose/),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Task result')).not.toBeInTheDocument();
    expect(authFetch).toHaveBeenCalledWith(
      '/api/chat/conversations/conversation-alpha',
      expect.objectContaining({ method: 'GET' }),
    );
    expect(authFetch).toHaveBeenCalledWith(
      '/api/chat/conversations/conversation-alpha/state',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it.each([
    'pendingActions',
    'recentActions',
  ] as const)('fails closed on a reloaded %s request identity mismatch', async (collection) => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    const connectPlan = taskPlan([
      taskStep('step-connect', {
        kind: 'browser_action',
        status: 'waiting',
        description: 'Connect GitHub',
        source: { browserAction: { action: 'service.connect' } },
        actionRequestId: 'action-alpha',
        operation: null,
      }),
    ]);
    const conflictingSummary = {
      schemaVersion: 4,
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      reports: [],
      postconditionResult: null,
      request: {
        schemaVersion: 4,
        actorId: 'conversation-alpha',
        originTurnId: 'turn-alpha',
        taskId: 'task-alpha',
        stepId: 'step-connect',
        actionRequestId: 'action-other',
        action: 'service.connect',
        params: {
          catalogService: { serviceSlug: 'api-github' },
        },
      },
    };
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      activeTaskState(connectPlan, 8, {
        [collection]: [conflictingSummary],
      }),
    );

    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();

    expect(
      await screen.findByText(
        'Action identity conflict; this browser journey is disabled.',
      ),
    ).toHaveAttribute('role', 'alert');
    expect(
      screen.queryByRole('button', { name: 'Open NyxID connection' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh connection' }),
    ).not.toBeInTheDocument();
  });

  it('UC1b reloads the verified connection and terminal fact exactly once', async () => {
    useRealChatHistoryApi();
    let reloading = false;
    const completedPlan = taskPlan(
      [
        taskStep('step-connect', {
          kind: 'browser_action',
          status: 'done',
          description: 'Connect GitHub',
          source: { browserAction: { action: 'service.connect' } },
          externalEffect: 'confirmed',
          availableActions: undefined,
          actionRequestId: 'action-alpha',
          operation: null,
        }),
        taskStep('step-verify', {
          order: 2,
          kind: 'postcondition',
          status: 'done',
          description: 'Verify GitHub connection',
          source: { postcondition: { check: 'service.connected' } },
          externalEffect: 'confirmed',
          availableActions: undefined,
          actionRequestId: 'action-alpha',
          operation: null,
        }),
      ],
      {
        title: 'Connect and verify GitHub',
        status: 'succeeded',
      },
    );
    const action = {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      params: {
        catalogService: {
          serviceSlug: 'api-github',
          requestedScopes: ['repo:read'],
        },
      },
    };
    const completedState = currentState(
      {
        latestTurn: {
          turnId: 'turn-alpha',
          taskId: 'task-alpha',
          status: 'succeeded',
          safeMessage: 'GitHub connection verified.',
        },
        activeTask: completedPlan,
        pendingActions: [],
        recentActions: [
          {
            schemaVersion: 4,
            originTurnId: 'turn-alpha',
            taskId: 'task-alpha',
            stepId: 'step-connect',
            actionRequestId: 'action-alpha',
            action: 'service.connect',
            reports: [
              {
                actionRequestId: 'action-alpha',
                originTurnId: 'turn-alpha',
                disposition: 'completed',
                resource: {
                  userService: { userServiceId: 'user-service-alpha' },
                },
              },
            ],
            postconditionResult: {
              actionRequestId: 'action-alpha',
              disposition: 'completed',
              verified: true,
              resource: {
                userService: { userServiceId: 'user-service-alpha' },
              },
            },
            request: action,
          },
        ],
      },
      9,
    );
    const stream = completedStream(
      'GitHub connection verified.',
      'conversation-alpha',
      'turn-alpha',
      [
        {
          type: 'CUSTOM',
          sequence: 1,
          custom: { name: 'nyxid.task.snapshot', payload: completedPlan },
        },
        {
          type: 'CUSTOM',
          sequence: 2,
          custom: { name: 'nyxid.action.request', payload: action },
        },
      ],
    );
    (authFetch as jest.Mock).mockImplementation((path: string) => {
      if (path === '/api/chat') return Promise.resolve(stream);
      if (path === '/api/chat/conversations') {
        return Promise.resolve(
          jsonResponse({
            conversations: reloading ? [serverConversation] : [],
            nextCursor: null,
          }),
        );
      }
      if (path === '/api/chat/conversations/conversation-alpha/state') {
        return Promise.resolve(jsonResponse(completedState));
      }
      if (path === '/api/chat/conversations/conversation-alpha') {
        return Promise.resolve(
          jsonResponse({
            messages: [],
            projectionStatus: 'current',
            stateVersion: 9,
          }),
        );
      }
      throw new Error(`Unexpected authFetch path: ${path}`);
    });

    const firstView = renderWithQueryClient(<ChatPage />);
    await sendPrompt('Connect GitHub and verify the connection');
    expect(await screen.findByText('Actor verified')).toBeInTheDocument();
    expect(
      screen.getByText('Verified against service.connected'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);
    expect(screen.getAllByText('GitHub connection verified.')).toHaveLength(2);
    expect(requestBodies()).toEqual([
      {
        type: 'text',
        prompt: 'Connect GitHub and verify the connection',
        clientRequestId: expect.any(String),
      },
    ]);

    firstView.unmount();
    reloading = true;
    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(await screen.findByText('Actor verified')).toBeInTheDocument();
    expect(
      screen.getByText('Verified against service.connected'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);
    expect(screen.getByText('GitHub connection verified.')).toBeInTheDocument();
    expect(authFetch).toHaveBeenCalledWith(
      '/api/chat/conversations/conversation-alpha',
      expect.objectContaining({ method: 'GET' }),
    );
    expect(authFetch).toHaveBeenCalledWith(
      '/api/chat/conversations/conversation-alpha/state',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('UC2 steers, stops, reloads, and starts a later goal as a new task', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    const completedInput = taskStep('step-uc2-gaps', {
      kind: 'input',
      status: 'done',
      description: 'Answer logistics and agree to research-only scope',
      source: { input: { requestId: 'input-uc2-gaps' } },
      externalEffect: 'not_applied',
      availableActions: undefined,
      operation: null,
    });
    const original = taskStep('step-original', {
      order: 3,
      description: 'Compare original candidates',
    });
    const completedEvidence = taskStep('step-uc2-search', {
      order: 2,
      status: 'done',
      description: 'Aevatar web_search found the candidate set',
      source: { tool: { toolName: 'web_search' } },
      externalEffect: 'not_applied',
      availableActions: undefined,
      operation: {
        conversationActorId: 'conversation-alpha',
        turnId: 'turn-alpha',
        taskId: 'task-alpha',
        stepId: 'step-uc2-search',
        operationId: 'operation-uc2-search',
        operationGeneration: 1,
        kind: 'tool',
        phase: 'succeeded',
      },
      substeps: [
        {
          substepId: 'prepare-operation',
          title: 'Build search query',
          status: 'done',
        },
        {
          substepId: 'execute-operation',
          title: 'Search current web results',
          status: 'done',
        },
      ],
    });
    const steered = taskPlan(
      [
        completedInput,
        completedEvidence,
        {
          ...original,
          status: 'cancelled',
          cancelledInPlanRevision: 2,
          availableActions: undefined,
          operation: null,
        },
        taskStep('step-steered', {
          order: 3,
          description: 'Compare for 7 PM and a private room',
          addedBy: 'steering',
          addedInPlanRevision: 2,
        }),
      ],
      {
        planRevision: 2,
        planRevisions: [
          {
            planRevision: 1,
            revisionCause: 'initial',
            addedStepIds: ['step-uc2-gaps', 'step-original', 'step-uc2-search'],
            cancelledStepIds: [],
          },
          {
            planRevision: 2,
            revisionCause: 'steering',
            addedStepIds: ['step-steered'],
            cancelledStepIds: ['step-original'],
          },
        ],
        title: 'Dinner research',
      },
    );
    const stopReceipt =
      'Stopped. Partial-work receipt: 2 completed steps were retained. ' +
      'Retained: Answer logistics and agree to research-only scope; ' +
      'Aevatar web_search found the candidate set. ' +
      'Unfinished work was fenced; the in-flight operation could not be proven cancelled. ' +
      'Fenced: Compare for 7 PM and a private room. No external effect was applied. ' +
      'Late evidence cannot advance this stopped task.';
    const stopped = {
      ...steered,
      status: 'stopped',
      activeStepId: undefined,
      steps: (steered.steps as Record<string, unknown>[]).map((step) => ({
        ...step,
        status: step.status === 'running' ? 'cancelled' : step.status,
        availableActions: undefined,
      })),
    };
    const stoppedState = currentState(
      {
        latestTurn: {
          turnId: 'turn-alpha',
          taskId: 'task-alpha',
          status: 'stopped',
          safeMessage: stopReceipt,
        },
        activeTask: stopped,
        controlFence: {
          kind: 'stop',
          requestId: 'stop-uc2-1',
          clientRequestId: 'client-stop-uc2-1',
          turnId: 'turn-alpha',
          taskId: 'task-alpha',
          outcome: 'uncancellable',
          safeMessage: stopReceipt,
        },
      },
      12,
    );
    const newGoalStep = taskStep('step-uc2b-search', {
      description: 'Find Friday dinner options',
      source: { tool: { toolName: 'web_search' } },
      operation: {
        conversationActorId: 'conversation-alpha',
        turnId: 'turn-uc2b-1',
        taskId: 'task-uc2b',
        stepId: 'step-uc2b-search',
        operationId: 'operation-uc2b-search',
        operationGeneration: 1,
        kind: 'tool',
        phase: 'running',
      },
    });
    const newGoalPlan = taskPlan([newGoalStep], {
      taskId: 'task-uc2b',
      turnId: 'turn-uc2b-1',
      planId: 'plan-uc2b',
      title: 'Friday dinner research',
    });
    const newGoalState = {
      ...currentState(
        {
          activeTurn: {
            turnId: 'turn-uc2b-1',
            taskId: 'task-uc2b',
            status: 'active',
          },
          latestTurn: null,
          recentTerminalTurns: [
            {
              turnId: 'turn-alpha',
              taskId: 'task-alpha',
              status: 'stopped',
              safeMessage: stopReceipt,
            },
          ],
          activeTask: newGoalPlan,
        },
        13,
      ),
      turnId: 'turn-uc2b-1',
    };
    (chatHistoryApi.loadConversationState as jest.Mock)
      .mockResolvedValueOnce(
        activeTaskState(
          taskPlan([completedInput, original, completedEvidence], {
            title: 'Dinner research',
          }),
          10,
        ),
      )
      .mockResolvedValueOnce(activeTaskState(steered, 11))
      .mockResolvedValueOnce(stoppedState)
      .mockResolvedValueOnce(stoppedState)
      .mockResolvedValue(newGoalState);
    (authFetch as jest.Mock).mockImplementation(
      (_path: string, request: RequestInit) => {
        const body = JSON.parse(String(request.body));
        return Promise.resolve(
          body.type === 'text'
            ? completedStream(
                'Friday dinner research started.',
                'conversation-alpha',
                'turn-uc2b-1',
                [
                  {
                    type: 'CUSTOM',
                    sequence: 13,
                    custom: {
                      name: 'nyxid.task.snapshot',
                      payload: newGoalPlan,
                    },
                  },
                ],
              )
            : ({ ok: true, status: 202 } as Response),
        );
      },
    );

    const firstView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    const steering = await screen.findByPlaceholderText(
      'Steer the active task...',
    );
    fireEvent.change(steering, {
      target: { value: 'Use 7 PM and require a private room' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText('Plan revision 2')).toBeInTheDocument();
    expect(
      screen.getByText('Compare for 7 PM and a private room'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Compare for 7 PM and a private room')).getByText(
        'addedBy: steering',
      ),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Compare for 7 PM and a private room')).getByText('r2'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Compare original candidates')).getByText('cancelled'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Aevatar web_search found the candidate set')).getByText(
        'done',
      ),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Aevatar web_search found the candidate set')).getByText(
        'web_search',
      ),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Aevatar web_search found the candidate set')).getByText(
        'Build search query · done',
      ),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Aevatar web_search found the candidate set')).getByText(
        'Search current web results · done',
      ),
    ).toBeInTheDocument();
    const stop = screen.getByRole('button', { name: 'Stop task' });
    await waitFor(() => expect(stop).toBeEnabled());
    fireEvent.click(stop);
    expect(await screen.findByText(stopReceipt)).toBeInTheDocument();
    expect(requestBodies()).toEqual([
      {
        type: 'task.steer',
        conversationId: 'conversation-alpha',
        turnId: 'turn-alpha',
        steeringId: expect.any(String),
        clientRequestId: expect.any(String),
        instruction: 'Use 7 PM and require a private room',
        expectedStateVersion: 10,
      },
      {
        type: 'task.stop',
        conversationId: 'conversation-alpha',
        turnId: 'turn-alpha',
        stopRequestId: expect.any(String),
        clientRequestId: expect.any(String),
        expectedStateVersion: 11,
      },
    ]);

    firstView.unmount();
    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(await screen.findByText(stopReceipt)).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);
    expect(
      within(taskRow('Aevatar web_search found the candidate set')).getByText(
        'done',
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Stop task' }),
    ).not.toBeInTheDocument();

    await sendPrompt('Dinner is back on for Friday');
    expect(
      await screen.findByText('Friday dinner research'),
    ).toBeInTheDocument();
    expect(screen.getByText('Plan revision 1')).toBeInTheDocument();
    expect(newGoalState).toEqual(
      expect.objectContaining({
        snapshot: expect.objectContaining({
          activeTurn: expect.objectContaining({ taskId: 'task-uc2b' }),
          activeTask: expect.objectContaining({ taskId: 'task-uc2b' }),
        }),
      }),
    );
    expect(requestBodies()[2]).toEqual({
      type: 'text',
      conversationId: 'conversation-alpha',
      prompt: 'Dinner is back on for Friday',
      clientRequestId: expect.any(String),
    });
  });

  it('unlocks only actor-authorized controls after reconciliation and retries at N+1', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    const uncertain = taskStep('step-effect', {
      status: 'uncertain',
      description: 'Submit external record',
      mayChangeExternalState: true,
      externalEffect: 'may_have_changed',
      availableActions: { stop: true },
      operation: {
        turnId: 'turn-alpha',
        taskId: 'task-alpha',
        stepId: 'step-effect',
        operationId: 'operation-effect',
        operationGeneration: 1,
        kind: 'effect',
        phase: 'uncertain',
      },
    });
    const reconciling = taskStep('step-reconcile', {
      order: 2,
      description: 'Reconcile provider receipt',
      addedBy: 'replan',
      addedInPlanRevision: 2,
      availableActions: undefined,
      operation: {
        operationId: 'operation-reconcile',
        operationGeneration: 1,
        kind: 'reconcile',
        phase: 'running',
      },
    });
    const reconciled = {
      ...uncertain,
      status: 'failed',
      externalEffect: 'not_applied',
      availableActions: { retry: true, skip: true },
      operation: {
        ...(uncertain.operation as Record<string, unknown>),
        phase: 'failed',
      },
    };
    const reconcileDone = {
      ...reconciling,
      status: 'done',
      externalEffect: 'not_applied',
      operation: null,
    };
    const retryPlan = taskPlan(
      [
        {
          ...reconciled,
          status: 'running',
          externalEffect: 'not_started',
          availableActions: { stop: true },
          operation: {
            ...(reconciled.operation as Record<string, unknown>),
            operationGeneration: 2,
            phase: 'running',
          },
        },
        reconcileDone,
      ],
      {
        planRevision: 3,
        planRevisions: [
          {
            planRevision: 1,
            revisionCause: 'initial',
            addedStepIds: ['step-effect'],
            cancelledStepIds: [],
          },
          {
            planRevision: 2,
            revisionCause: 'failure_recovery',
            addedStepIds: ['step-reconcile'],
            cancelledStepIds: [],
          },
          {
            planRevision: 3,
            revisionCause: 'failure_recovery',
            addedStepIds: [],
            cancelledStepIds: [],
          },
        ],
        title: 'External record retry',
      },
    );
    const retryStartedResult = {
      kind: 'retry',
      requestId: 'retry-alpha',
      clientRequestId: 'retry-client-alpha',
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-effect',
      expectedOperationGeneration: 1,
      operationGeneration: 2,
      outcome: 'retry_started',
    };
    const retryState = activeTaskState(retryPlan, 22, {
      latestStepControlResult: retryStartedResult,
      recentStepControlResults: [retryStartedResult],
    });
    const approvalRetryPlan = {
      ...retryPlan,
      activeOperationId: undefined,
      steps: (retryPlan.steps as Record<string, unknown>[]).map((step) =>
        step.stepId === 'step-effect'
          ? {
              ...step,
              status: 'failed',
              externalEffect: 'not_applied',
              availableActions: { retry: true, skip: true },
              approvalObservation: {
                approvalRequestId: 'approval-generation-two',
                decisionMode: 'per_request',
                receiptStatus: 'approval_required',
                observedAt: '2026-08-08T00:04:00Z',
              },
              operation: {
                ...(step.operation as Record<string, unknown>),
                phase: 'failed',
              },
            }
          : step,
      ),
    };
    const approvalRetryState = activeTaskState(approvalRetryPlan, 24, {
      latestStepControlResult: retryStartedResult,
      recentStepControlResults: [retryStartedResult],
    });
    (chatHistoryApi.loadConversationState as jest.Mock)
      .mockResolvedValueOnce(
        activeTaskState(
          taskPlan([uncertain, reconciling], {
            planRevision: 2,
            planRevisions: [
              {
                planRevision: 1,
                revisionCause: 'initial',
                addedStepIds: ['step-effect'],
                cancelledStepIds: [],
              },
              {
                planRevision: 2,
                revisionCause: 'failure_recovery',
                addedStepIds: ['step-reconcile'],
                cancelledStepIds: [],
              },
            ],
            title: 'External record submission',
          }),
          20,
        ),
      )
      .mockResolvedValueOnce(
        activeTaskState(
          taskPlan([reconciled, reconcileDone], {
            planRevision: 2,
            planRevisions: [
              {
                planRevision: 1,
                revisionCause: 'initial',
                addedStepIds: ['step-effect'],
                cancelledStepIds: [],
              },
              {
                planRevision: 2,
                revisionCause: 'failure_recovery',
                addedStepIds: ['step-reconcile'],
                cancelledStepIds: [],
              },
            ],
            title: 'External record reconciled',
          }),
          21,
        ),
      )
      .mockResolvedValueOnce(retryState)
      .mockRejectedValueOnce(new Error('Current state is not visible yet.'))
      .mockResolvedValueOnce(retryState)
      .mockResolvedValueOnce(retryState)
      .mockResolvedValue(approvalRetryState);
    (authFetch as jest.Mock).mockImplementation(
      (_path: string, request: RequestInit) => {
        const body = JSON.parse(String(request.body));
        return Promise.resolve(
          body.type === 'text'
            ? completedStream(
                'Generation 2 is running.',
                'conversation-alpha',
                'turn-alpha',
                [
                  {
                    type: 'CUSTOM',
                    sequence: 23,
                    custom: {
                      name: 'nyxid.task.snapshot',
                      payload: retryPlan,
                    },
                  },
                ],
              )
            : ({ ok: true, status: 202 } as Response),
        );
      },
    );

    const uncertainView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    await screen.findByText('External record submission');
    expect(
      within(taskRow('Submit external record')).getByText('may_have_changed'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Reconcile provider receipt')).getByText(
        'addedBy: replan',
      ),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Reconcile provider receipt')).getByText('r2'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Retry Submit external record' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Skip Submit external record' }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Stop task' }),
    ).toBeInTheDocument();

    uncertainView.unmount();
    const reconciledView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    await screen.findByText('External record reconciled');
    expect(
      within(taskRow('Reconcile provider receipt')).getByText('done'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Submit external record')).getByText('not_applied'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Retry Submit external record' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Skip Submit external record' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Stop task' }),
    ).not.toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', { name: 'Retry Submit external record' }),
    );
    expect(await screen.findByText(/generation 2/)).toBeInTheDocument();
    expect(
      within(taskRow('Submit external record')).getByText('running'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'NyxID approval observation' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();
    expect(requestBodies()).toEqual([
      {
        type: 'step.retry',
        conversationId: 'conversation-alpha',
        turnId: 'turn-alpha',
        taskId: 'task-alpha',
        stepId: 'step-effect',
        retryRequestId: expect.any(String),
        clientRequestId: expect.any(String),
        expectedOperationGeneration: 1,
        expectedStateVersion: 21,
      },
    ]);

    reconciledView.unmount();
    const liveRetryView = renderWithQueryClient(<ChatPage />);
    await sendPrompt('Observe the external record retry');
    expect(await screen.findByText(/generation 2/)).toBeInTheDocument();
    expect(
      within(taskRow('Submit external record')).getByText('running'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'NyxID approval observation' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();

    liveRetryView.unmount();
    const retryReloadView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(await screen.findByText(/generation 2/)).toBeInTheDocument();
    expect(screen.getByText('Plan revision 3')).toBeInTheDocument();
    expect(screen.getByText('retry_started')).toBeInTheDocument();
    expect(
      within(taskRow('Submit external record')).getByText('running'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'NyxID approval observation' }),
    ).not.toBeInTheDocument();

    retryReloadView.unmount();
    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    await screen.findAllByText('Submit external record');
    const retryApproval = within(taskRow('Submit external record')).getByRole(
      'region',
      { name: 'NyxID approval observation' },
    );
    expect(retryApproval).toHaveTextContent('approval-generation-two');
    expect(retryApproval).toHaveTextContent('per_request');
    expect(retryApproval).toHaveTextContent('approval_required');
    expect(
      screen.getByRole('button', { name: 'Retry Submit external record' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Skip Submit external record' }),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();
  });

  it('renders a below-threshold conditional write as skipped', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    const belowThresholdPlan = taskPlan(
      [
        taskStep('step-threshold', {
          kind: 'input',
          status: 'done',
          description: 'Use metric threshold 75',
          source: { input: {} },
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
        }),
        taskStep('step-condition', {
          order: 2,
          kind: 'condition',
          status: 'done',
          description: 'Observed value 72 is below 75',
          source: numericCondition(72, 'false'),
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
        }),
        taskStep('step-write', {
          order: 3,
          status: 'skipped',
          description: 'Write matching record',
          source: { tool: { toolName: 'external_record_create' } },
          mayChangeExternalState: true,
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
          guard: {
            conditionStepId: 'step-condition',
            requiredOutcome: 'true',
          },
        }),
        taskStep('step-verify', {
          order: 4,
          kind: 'postcondition',
          status: 'skipped',
          description: 'Read matching record back',
          source: { postcondition: { check: 'external_record.exists' } },
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
          guard: {
            conditionStepId: 'step-condition',
            requiredOutcome: 'true',
          },
        }),
      ],
      {
        status: 'succeeded',
        title: 'Conditional record write at 75',
      },
    );
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState(
        {
          latestTurn: {
            turnId: 'turn-alpha',
            taskId: 'task-alpha',
            status: 'succeeded',
            safeMessage: 'Observed value did not meet the write threshold.',
          },
          activeTask: belowThresholdPlan,
        },
        29,
      ),
    );

    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();

    expect(
      await screen.findByText('Observed value 72 is below 75'),
    ).toBeInTheDocument();
    const write = within(taskRow('Write matching record'));
    expect(write.getByText('skipped')).toBeInTheDocument();
    expect(write.getByText('not_applied')).toBeInTheDocument();
    const conditionFacts = screen.getByRole('region', {
      name: 'Committed condition facts',
    });
    expect(conditionFacts).toHaveTextContent('72 >= 75');
    expect(conditionFacts).toHaveTextContent('false');
    expect(conditionFacts).toHaveTextContent('user_override');
    expect(conditionFacts).toHaveTextContent('external_record_create');
    expect(
      write.getByText('Guard step-condition requires true'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Verified against external_record.exists'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'NyxID approval observation' }),
    ).not.toBeInTheDocument();
  });

  it('preserves the 75 override across Tier-B stall, return, and verified reload', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    const finalPlan = taskPlan(
      [
        taskStep('step-threshold', {
          kind: 'input',
          status: 'done',
          description: 'Use metric threshold 75',
          source: { input: {} },
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
        }),
        taskStep('step-condition-true', {
          order: 2,
          kind: 'condition',
          status: 'done',
          description: 'Condition at least 75 executed',
          source: numericCondition(80, 'true'),
          externalEffect: 'not_applied',
          availableActions: undefined,
          operation: null,
        }),
        taskStep('step-write', {
          order: 3,
          status: 'done',
          description: 'Write matching record',
          source: { tool: { toolName: 'external_record_create' } },
          mayChangeExternalState: true,
          externalEffect: 'confirmed',
          availableActions: undefined,
          operation: null,
          guard: {
            conditionStepId: 'step-condition-true',
            requiredOutcome: 'true',
          },
        }),
        taskStep('step-verify', {
          order: 4,
          kind: 'postcondition',
          status: 'done',
          description: 'Read matching record back',
          source: { postcondition: { check: 'external_record.exists' } },
          externalEffect: 'confirmed',
          availableActions: undefined,
          operation: null,
          guard: {
            conditionStepId: 'step-condition-true',
            requiredOutcome: 'true',
          },
        }),
      ],
      {
        status: 'succeeded',
        planRevision: 2,
        planRevisions: [
          {
            planRevision: 1,
            revisionCause: 'initial',
            addedStepIds: ['step-threshold'],
            cancelledStepIds: [],
          },
          {
            planRevision: 2,
            revisionCause: 'scope_resolution',
            addedStepIds: [
              'step-condition-true',
              'step-write',
              'step-verify',
            ],
            cancelledStepIds: [],
          },
        ],
        title: 'Conditional record write at 75',
      },
    );
    const waitingPlan = taskPlan(
      (finalPlan.steps as Record<string, unknown>[]).map((step) => {
        if (step.stepId === 'step-write') {
          return {
            ...step,
            status: 'waiting',
            externalEffect: 'not_started',
            availableActions: { stop: true },
            operation: {
              conversationActorId: 'conversation-alpha',
              turnId: 'turn-alpha',
              taskId: 'task-alpha',
              stepId: 'step-write',
              operationId: 'operation-write',
              operationGeneration: 1,
              kind: 'effect',
              phase: 'waiting',
              lastProgressAt: '2026-08-08T00:00:00Z',
              stalledAt: '2026-08-08T00:02:00Z',
            },
          };
        }
        if (step.stepId === 'step-verify') {
          return {
            ...step,
            status: 'planned',
            externalEffect: 'not_started',
          };
        }
        return step;
      }),
      {
        planRevision: 2,
        planRevisions: finalPlan.planRevisions,
        title: 'Conditional record write at 75',
      },
    );
    const waitingState = activeTaskState(waitingPlan, 31, {
      latestInputResolution: {
        requestId: 'input-threshold',
        outcome: 'resolved',
      },
    });
    const postReturnApprovalObservation = {
      approvalRequestId: 'nyxid-approval-write-alpha',
      decisionMode: 'per_request',
      receiptStatus: 'approval_required',
      observedAt: '2026-08-08T00:10:00Z',
    };
    const returnedPlan = taskPlan(
      (waitingPlan.steps as Record<string, unknown>[]).map((step) =>
        step.stepId === 'step-write'
          ? {
              ...step,
              status: 'failed',
              externalEffect: 'not_applied',
              availableActions: { retry: true, skip: true },
              approvalObservation: postReturnApprovalObservation,
              operation: {
                conversationActorId: 'conversation-alpha',
                turnId: 'turn-alpha',
                taskId: 'task-alpha',
                stepId: 'step-write',
                operationId: 'operation-write',
                operationGeneration: 1,
                kind: 'effect',
                phase: 'failed',
                lastProgressAt: '2026-08-08T00:10:00Z',
              },
            }
          : step,
      ),
      {
        planRevision: 2,
        planRevisions: finalPlan.planRevisions,
        title: 'Conditional record write at 75',
      },
    );
    const returnedState = activeTaskState(returnedPlan, 32, {
      latestInputResolution: {
        requestId: 'input-threshold',
        outcome: 'resolved',
      },
    });
    const terminalPlan = {
      ...finalPlan,
      steps: (finalPlan.steps as Record<string, unknown>[]).map((step) =>
        step.stepId === 'step-write'
          ? {
              ...step,
              approvalObservation: postReturnApprovalObservation,
            }
          : step,
      ),
    };
    const finalState = currentState(
      {
        latestTurn: {
          turnId: 'turn-alpha',
          taskId: 'task-alpha',
          status: 'succeeded',
        },
        activeTask: terminalPlan,
        latestInputResolution: {
          requestId: 'input-threshold',
          outcome: 'resolved',
        },
      },
      33,
    );
    (chatHistoryApi.loadConversationState as jest.Mock)
      .mockResolvedValueOnce(
        activeTaskState(
          taskPlan(
            [
              taskStep('step-threshold', {
                kind: 'input',
                status: 'waiting',
                description: 'Suggested metric threshold: 70',
                source: { input: {} },
                operation: null,
              }),
            ],
            { title: 'Conditional record write at suggested 70' },
          ),
          30,
          {
            pendingInput: {
              requestId: 'input-threshold',
              prompt: 'Suggested threshold is 70. Set the metric threshold.',
              options: [],
              allowFreeText: true,
              multiSelect: false,
            },
          },
        ),
      )
      .mockResolvedValueOnce(waitingState)
      .mockRejectedValueOnce(new Error('Current state is not visible yet.'))
      .mockResolvedValueOnce(returnedState)
      .mockResolvedValueOnce(returnedState)
      .mockResolvedValue(finalState);
    (authFetch as jest.Mock).mockImplementation(
      (_path: string, request: RequestInit) => {
        const body = JSON.parse(String(request.body));
        return Promise.resolve(
          body.type === 'text'
            ? completedStream(
                'NyxID returned an approval request.',
                'conversation-alpha',
                'turn-alpha',
                [
                  {
                    type: 'CUSTOM',
                    sequence: 32,
                    custom: {
                      name: 'nyxid.task.snapshot',
                      payload: returnedPlan,
                    },
                  },
                ],
              )
            : ({ ok: true, status: 202 } as Response),
        );
      },
    );

    const firstView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(
      await screen.findByText(
        'Suggested threshold is 70. Set the metric threshold.',
      ),
    ).toBeInTheDocument();
    const answer = screen.getByPlaceholderText(
      'Answer the current question...',
    );
    fireEvent.change(answer, { target: { value: '75' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send' }));
    expect(
      await screen.findByText('Conditional record write at 75'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Write matching record')).getByText('Stalled'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('NyxID request observed'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(requestBodies()).toEqual([
      {
        type: 'input.resolve',
        conversationId: 'conversation-alpha',
        requestId: 'input-threshold',
        clientRequestId: expect.any(String),
        answer: { freeText: '75' },
        expectedStateVersion: 30,
      },
    ]);

    firstView.unmount();
    const liveReturnedView = renderWithQueryClient(<ChatPage />);
    await sendPrompt('Observe the conditional write return');
    expect(
      await screen.findByText('Conditional record write at 75'),
    ).toBeInTheDocument();
    const liveApprovalObservation = within(
      taskRow('Write matching record'),
    ).getByRole('region', { name: 'NyxID approval observation' });
    expect(liveApprovalObservation).toHaveTextContent(
      'nyxid-approval-write-alpha',
    );
    expect(liveApprovalObservation).toHaveTextContent('approval_required');
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();

    liveReturnedView.unmount();
    const returnedView = renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(
      await screen.findByText('Conditional record write at 75'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Write matching record')).getByText('failed'),
    ).toBeInTheDocument();
    expect(screen.getByText(/generation 1/)).toBeInTheDocument();
    expect(
      within(taskRow('Write matching record')).queryByText('Stalled'),
    ).not.toBeInTheDocument();
    const approvalObservation = within(
      taskRow('Write matching record'),
    ).getByRole('region', { name: 'NyxID approval observation' });
    expect(approvalObservation).toHaveTextContent('NyxID request observed');
    expect(approvalObservation).toHaveTextContent('nyxid-approval-write-alpha');
    expect(approvalObservation).toHaveTextContent('approval_required');
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('approved')).not.toBeInTheDocument();

    returnedView.unmount();
    renderWithQueryClient(<ChatPage />);
    await openCanonicalConversation();
    expect(
      await screen.findByText('Conditional record write at 75'),
    ).toBeInTheDocument();
    expect(
      within(taskRow('Condition at least 75 executed')).getByText('done'),
    ).toBeInTheDocument();
    const conditionFacts = screen.getByRole('region', {
      name: 'Committed condition facts',
    });
    expect(conditionFacts).toHaveTextContent('80 >= 75');
    expect(conditionFacts).toHaveTextContent('true');
    expect(conditionFacts).toHaveTextContent('user_override');
    expect(conditionFacts).toHaveTextContent('external_record_create');
    expect(
      screen.getByText('Verified against external_record.exists'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Task result')).toHaveLength(1);
    const terminalApprovalObservation = within(
      taskRow('Write matching record'),
    ).getByRole('region', { name: 'NyxID approval observation' });
    expect(terminalApprovalObservation).toHaveTextContent(
      'nyxid-approval-write-alpha',
    );
    expect(terminalApprovalObservation).toHaveTextContent('approval_required');
    expect(
      within(taskRow('Write matching record')).getByText('confirmed'),
    ).toBeInTheDocument();
    expect(screen.queryByText('approved')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Approve' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Reject' }),
    ).not.toBeInTheDocument();
  });

  it('reports exactly one newly connected UserService through action.continue', async () => {
    const action = {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      params: { catalogService: { serviceSlug: 'api-github' } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream('Connect GitHub', 'conversation-alpha', 'turn-alpha', [
          {
            type: 'CUSTOM',
            sequence: 4,
            custom: { name: 'nyxid.action.request', payload: action },
          },
        ]),
      )
      .mockResolvedValueOnce(
        completedStream(
          'Connection reported',
          'conversation-alpha',
          'turn-beta',
        ),
      );
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState({
        pendingActions: [
          {
            schemaVersion: 4,
            originTurnId: 'turn-alpha',
            taskId: 'task-alpha',
            stepId: 'step-connect',
            actionRequestId: 'action-alpha',
            action: 'service.connect',
            request: action,
            reports: [],
            postconditionResult: null,
          },
        ],
      }),
    );
    (listNyxIdConnectors as jest.Mock)
      .mockResolvedValueOnce({ connected: [], available: [] })
      .mockResolvedValueOnce({
        connected: [
          {
            slug: 'api-github',
            name: 'GitHub',
            description: '',
            authKind: 'oauth',
            userServices: [
              {
                userServiceId: 'user-service-new',
                apiKeyId: 'api-key-not-resource',
                endpointUrl: 'https://api.github.com',
                label: 'GitHub',
              },
            ],
          },
        ],
        available: [],
      });
    const open = jest.spyOn(window, 'open').mockReturnValue({
      focus: jest.fn(),
    } as unknown as Window);

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Connect GitHub');
    fireEvent.click(
      await screen.findByRole('button', { name: 'Open NyxID connection' }),
    );
    await waitFor(() => expect(open).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Refresh connection' }));
    await waitFor(() => expect(requestBodies()).toHaveLength(2));

    expect(requestBodies()[1]).toEqual({
      type: 'action.continue',
      conversationId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      clientRequestId: expect.any(String),
      actions: [
        {
          actionRequestId: 'action-alpha',
          originTurnId: 'turn-alpha',
          disposition: 'completed',
          resource: {
            userService: { userServiceId: 'user-service-new' },
          },
        },
      ],
    });
    expect(JSON.stringify(requestBodies()[1])).not.toContain(
      'api-key-not-resource',
    );
  });

  it('connects a catalog credential directly to NyxID without persisting it', async () => {
    const action = {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      params: { catalogService: { serviceSlug: 'api-github' } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream('Connect GitHub', 'conversation-alpha', 'turn-alpha', [
          {
            type: 'CUSTOM',
            sequence: 4,
            custom: { name: 'nyxid.action.request', payload: action },
          },
        ]),
      )
      .mockResolvedValueOnce(
        completedStream(
          'Credential reported',
          'conversation-alpha',
          'turn-beta',
        ),
      );
    (createNyxIdCatalogKey as jest.Mock).mockResolvedValue(
      'user-service-created',
    );
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState({
        pendingActions: [
          {
            schemaVersion: 4,
            originTurnId: 'turn-alpha',
            taskId: 'task-alpha',
            stepId: 'step-connect',
            actionRequestId: 'action-alpha',
            action: 'service.connect',
            request: action,
            reports: [],
            postconditionResult: null,
          },
        ],
      }),
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Connect GitHub');
    const credential = await screen.findByLabelText('api-github credential');
    fireEvent.change(credential, { target: { value: 'secret-value' } });
    fireEvent.click(screen.getByRole('button', { name: 'Connect api-github' }));
    await waitFor(() =>
      expect(createNyxIdCatalogKey).toHaveBeenCalledWith({
        serviceSlug: 'api-github',
        credential: 'secret-value',
        label: 'api-github',
      }),
    );
    await waitFor(() => expect(requestBodies()).toHaveLength(2));
    expect(JSON.stringify(requestBodies()[1])).not.toContain('secret-value');
    expect(window.sessionStorage.getItem('secret-value')).toBeNull();
    expect(credential).toHaveValue('');
  });

  it('does not present a rejected action report as accepted', async () => {
    const action = {
      schemaVersion: 4,
      actorId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-connect',
      actionRequestId: 'action-alpha',
      action: 'service.connect',
      params: { catalogService: { serviceSlug: 'api-github' } },
    };
    (authFetch as jest.Mock)
      .mockResolvedValueOnce(
        completedStream('Connect GitHub', 'conversation-alpha', 'turn-alpha', [
          {
            type: 'CUSTOM',
            sequence: 4,
            custom: { name: 'nyxid.action.request', payload: action },
          },
        ]),
      )
      .mockRejectedValueOnce(new Error('network unavailable'));
    (chatHistoryApi.loadConversationState as jest.Mock).mockResolvedValue(
      currentState({
        pendingActions: [
          {
            schemaVersion: 4,
            originTurnId: 'turn-alpha',
            taskId: 'task-alpha',
            stepId: 'step-connect',
            actionRequestId: 'action-alpha',
            action: 'service.connect',
            request: action,
            reports: [],
            postconditionResult: null,
          },
        ],
      }),
    );

    renderWithQueryClient(<ChatPage />);
    await sendPrompt('Connect GitHub');
    fireEvent.click(await screen.findByRole('button', { name: 'Decline' }));

    expect(
      await screen.findByText('Action report was not accepted.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Reported; waiting for actor verification'),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Decline' })).toBeInTheDocument();
  });

  it('submits canonical delete and never infers approval from assistant prose', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      serverConversation,
    ]);
    (authFetch as jest.Mock).mockResolvedValue(
      completedStream('Please confirm this explanation only.'),
    );

    renderWithQueryClient(<ChatPage />);
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Delete Canonical conversation',
      }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        'conversation-alpha',
      ),
    );

    await sendPrompt('Explain confirmation');
    expect(
      await screen.findByText('Please confirm this explanation only.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Confirm and create' }),
    ).not.toBeInTheDocument();
  });
});

describe('hydrateStoredMessages', () => {
  it('preserves extensible roles and maps stored errors to presentation errors', () => {
    expect(
      hydrateStoredMessages([
        {
          authorName: 'Automation',
          content: 'Queued',
          id: 'observer',
          role: 'observer',
          status: 'queued',
          timestamp: 1,
        },
        {
          content: '',
          error: 'Stopped',
          id: 'assistant',
          role: 'assistant',
          status: 'complete',
          timestamp: 2,
        },
      ]),
    ).toEqual([
      expect.objectContaining({ role: 'observer', status: 'queued' }),
      expect.objectContaining({ role: 'assistant', status: 'error' }),
    ]);
  });
});
