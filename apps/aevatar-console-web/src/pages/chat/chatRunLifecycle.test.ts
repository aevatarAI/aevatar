import { resolveChatRunLifecycle } from './chatRunLifecycle';
import type {
  ChatMessage,
  ChatSessionState,
  LocalChatStatus,
} from './chatTypes';

const messages: ChatMessage[] = [
  {
    content: 'Structured runtime output',
    id: 'assistant-alpha',
    role: 'assistant',
    status: 'complete',
    timestamp: 1,
  },
];

function session(runId = ''): ChatSessionState {
  return {
    actorId: runId ? 'actor-alpha' : '',
    commandId: '',
    endpointId: 'chat',
    eventCount: 0,
    runId,
    scopeId: 'scope-a',
    serviceId: 'chat',
    status: runId ? 'running' : 'idle',
  };
}

describe('chat Run lifecycle', () => {
  it.each<{
    expectedState: string;
    expectedTitle: string;
    lifecycleRunId?: string;
    sessionRunId?: string;
    status: LocalChatStatus;
  }>([
    {
      expectedState: 'pending',
      expectedTitle: 'Run pending',
      status: 'streaming',
    },
    {
      expectedState: 'failed',
      expectedTitle: 'Run failed',
      sessionRunId: 'run-failed',
      status: 'error',
    },
    {
      expectedState: 'stopped',
      expectedTitle: 'Run stopped',
      sessionRunId: 'run-stopped',
      status: 'stopped',
    },
    {
      expectedState: 'completed',
      expectedTitle: 'Run completed',
      lifecycleRunId: 'run-completed',
      status: 'completed_text',
    },
  ])('maps structured $expectedState state without reading assistant prose', (example) => {
    expect(
      resolveChatRunLifecycle({
        messages,
        runId: example.lifecycleRunId,
        session: session(example.sessionRunId),
        status: example.status,
      }),
    ).toMatchObject({
      state: example.expectedState,
      title: example.expectedTitle,
    });
  });
});
