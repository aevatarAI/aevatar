import { authFetch } from '@/shared/auth/fetch';
import {
  extractChatStreamArtifacts,
  readChatStreamFrames,
  sendChatCommand,
} from './chatApi';

jest.mock('@/shared/auth/fetch', () => ({
  authFetch: jest.fn(),
}));

function successfulStreamResponse(): Response {
  return { ok: true } as Response;
}

function createSseResponse(body: string): Response {
  const encoder = new TextEncoder();
  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(body));
        controller.close();
      },
    }),
    ok: true,
  } as Response;
}

describe('chatApi', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('sends canonical typed text with stable transport identity', async () => {
    (authFetch as jest.Mock).mockResolvedValue(successfulStreamResponse());
    const signal = new AbortController().signal;

    await sendChatCommand(
      {
        type: 'text',
        prompt: ' Create a workflow ',
        clientRequestId: ' client-first ',
      },
      signal,
    );
    await sendChatCommand(
      {
        type: 'text',
        conversationId: ' conversation-alpha ',
        prompt: ' Continue ',
        clientRequestId: ' client-next ',
      },
      signal,
    );

    expect(authFetch).toHaveBeenNthCalledWith(1, '/api/chat', {
      body: JSON.stringify({
        type: 'text',
        prompt: 'Create a workflow',
        clientRequestId: 'client-first',
      }),
      headers: {
        Accept: 'text/event-stream',
        'Content-Type': 'application/json',
        'Idempotency-Key': 'client-first',
      },
      method: 'POST',
      signal,
    });
    expect(authFetch).toHaveBeenNthCalledWith(2, '/api/chat', {
      body: JSON.stringify({
        type: 'text',
        conversationId: 'conversation-alpha',
        prompt: 'Continue',
        clientRequestId: 'client-next',
      }),
      headers: {
        Accept: 'text/event-stream',
        'Content-Type': 'application/json',
        'Idempotency-Key': 'client-next',
      },
      method: 'POST',
      signal,
    });
    for (const [, request] of (authFetch as jest.Mock).mock.calls) {
      expect(Object.keys(JSON.parse(request.body))).not.toEqual(
        expect.arrayContaining(['sessionId', 'scopeId', 'workflow']),
      );
    }
  });

  it.each([
    {
      type: 'input.resolve',
      conversationId: 'conversation-alpha',
      requestId: 'input-alpha',
      clientRequestId: 'client-input',
      answer: { selectedOptionIds: ['option-alpha'] },
      expectedStateVersion: 7,
    },
    {
      type: 'task.stop',
      conversationId: 'conversation-alpha',
      turnId: 'turn-alpha',
      stopRequestId: 'stop-alpha',
      clientRequestId: 'client-stop',
      expectedStateVersion: 9,
    },
    {
      type: 'task.steer',
      conversationId: 'conversation-alpha',
      turnId: 'turn-alpha',
      steeringId: 'steer-alpha',
      clientRequestId: 'client-steer',
      instruction: 'Use the safe path',
      expectedStateVersion: 10,
    },
    {
      type: 'step.retry',
      conversationId: 'conversation-alpha',
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-alpha',
      retryRequestId: 'retry-alpha',
      clientRequestId: 'client-retry',
      expectedOperationGeneration: 2,
      expectedStateVersion: 11,
    },
    {
      type: 'step.skip',
      conversationId: 'conversation-alpha',
      turnId: 'turn-alpha',
      taskId: 'task-alpha',
      stepId: 'step-alpha',
      skipRequestId: 'skip-alpha',
      clientRequestId: 'client-skip',
      expectedOperationGeneration: 2,
      expectedStateVersion: 12,
    },
    {
      type: 'action.continue',
      conversationId: 'conversation-alpha',
      originTurnId: 'turn-alpha',
      clientRequestId: 'client-action',
      actions: [
        {
          actionRequestId: 'action-alpha',
          originTurnId: 'turn-alpha',
          disposition: 'declined',
        },
      ],
    },
  ])('preserves the exact $type command body', async (command) => {
    (authFetch as jest.Mock).mockResolvedValue(successfulStreamResponse());

    await sendChatCommand(command as never, new AbortController().signal);

    const [, request] = (authFetch as jest.Mock).mock.calls[0];
    expect(JSON.parse(request.body)).toEqual(command);
    expect(request.headers['Idempotency-Key']).toBe(command.clientRequestId);
  });

  it('keeps SSE keepalive comments out of parsed data frames', async () => {
    const raw = {
      runFinished: {
        result: { output: 'Done' },
      },
    };
    const response = createSseResponse(
      `: keepalive\n\ndata: ${JSON.stringify(raw)}\n\n: keepalive\n\n`,
    );
    const frames = [];

    for await (const frame of readChatStreamFrames(response)) {
      frames.push(frame);
    }

    expect(frames).toHaveLength(1);
    expect(frames[0].raw).toEqual(raw);
  });

  it('extracts usage and studio target only from structured frames', () => {
    expect(
      extractChatStreamArtifacts([
        {
          runFinished: {
            result: {
              output:
                'Created team-a/member-a/workflow-a, but this text alone should not be parsed.',
            },
          },
        },
      ]),
    ).toEqual({});

    expect(
      extractChatStreamArtifacts([
        {
          runFinished: {
            result: {
              memberId: 'member-a',
              output: 'Created.',
              scopeId: 'scope-a',
              teamId: 'team-a',
              usage: {
                completionTokens: 3,
                promptTokens: 4,
                totalTokens: 7,
              },
              workflowId: 'workflow-a',
            },
          },
        },
      ]),
    ).toEqual({
      target: {
        memberId: 'member-a',
        scopeId: 'scope-a',
        teamId: 'team-a',
        workflowId: 'workflow-a',
      },
      usage: {
        completionTokens: 3,
        promptTokens: 4,
        totalTokens: 7,
      },
    });
  });

  it('extracts protobuf Struct-like custom payloads for existing run artifacts', () => {
    expect(
      extractChatStreamArtifacts([
        {
          custom: {
            name: 'aevatar.run.context',
            payload: {
              fields: {
                actorId: {
                  stringValue: 'run-a',
                },
                scopeId: {
                  stringValue: 'scope-a',
                },
                studioUrl: {
                  stringValue:
                    '/scopes/scope-a/teams/team-a/members/member-a/workflow',
                },
              },
            },
          },
        },
      ]),
    ).toEqual({
      target: {
        runId: 'run-a',
        scopeId: 'scope-a',
        studioUrl: '/scopes/scope-a/teams/team-a/members/member-a/workflow',
      },
    });
  });
});
