import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { loadDraftRunPayload } from '@/shared/runs/draftRunSession';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeInvokePage from './invoke';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    listServices: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: 'scope-a',
      scopeSource: 'nyxid',
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Workspace Demo',
      serviceKey: 'scope-a:default:default:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'deploy-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor://scope-a/default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [],
    })),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

import { servicesApi } from '@/shared/api/servicesApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';

describe('ScopeInvokePage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/invoke?scopeId=scope-a');
    jest.clearAllMocks();
  });

  it('invokes a non-chat endpoint for the selected scope service', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'run',
            kind: 'command',
            requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
            responseTypeUrl: 'type.googleapis.com/google.protobuf.Empty',
            description: 'Submit a command run.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValue({
      accepted: true,
      commandId: 'cmd-1',
    });

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    await waitFor(() => {
      expect(servicesApi.listServices).toHaveBeenCalledWith({
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
      });
    });
    expect(screen.queryByRole('button', { name: 'Open Runs Workbench' })).toBeNull();
    expect(await screen.findByRole('button', { name: 'Invoke endpoint' })).toBeTruthy();
    fireEvent.change(
      screen.getByPlaceholderText('Describe the request or payload text.'),
      {
        target: { value: 'run the scope command' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Invoke endpoint' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-a',
        {
          endpointId: 'run',
          prompt: 'run the scope command',
          payloadTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
          payloadBase64: undefined,
        },
        {
          serviceId: 'default',
        },
      );
    });

    expect(await screen.findByText(/"accepted": true/)).toBeTruthy();
  });

  it('streams a chat endpoint for the selected scope service', async () => {
    (servicesApi.listServices as jest.Mock).mockResolvedValue([
      {
        serviceKey: 'scope-a:default:default:default',
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
        serviceId: 'default',
        displayName: 'Workspace Demo',
        defaultServingRevisionId: 'rev-2',
        activeServingRevisionId: 'rev-2',
        deploymentId: 'deploy-2',
        primaryActorId: 'actor://scope-a/default',
        deploymentStatus: 'Active',
        endpoints: [
          {
            endpointId: 'chat',
            displayName: 'chat',
            kind: 'chat',
            requestTypeUrl: 'type.googleapis.com/aevatar.ChatRequestEvent',
            responseTypeUrl: 'type.googleapis.com/aevatar.ChatResponseEvent',
            description: 'Chat with the published scope service.',
          },
        ],
        policyIds: [],
        updatedAt: '2026-03-26T08:00:00Z',
      },
    ]);
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue({ ok: true });
    (parseBackendSSEStream as jest.Mock).mockImplementation(async function* () {
      yield {
        type: 'RUN_STARTED',
        runId: 'run-1',
        threadId: 'thread-1',
        timestamp: Date.now(),
      };
      yield {
        type: 'CUSTOM',
        name: 'aevatar.run.context',
        value: {
          actorId: 'actor://scope-a/default',
          commandId: 'cmd-1',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'TEXT_MESSAGE_CONTENT',
        delta: 'hello from scope service',
        messageId: 'msg-1',
        timestamp: Date.now(),
      };
    });

    renderWithQueryClient(React.createElement(ScopeInvokePage));

    await waitFor(() => {
      expect(servicesApi.listServices).toHaveBeenCalledWith({
        tenantId: 'scope-a',
        appId: 'default',
        namespace: 'default',
      });
    });
    expect(await screen.findByRole('button', { name: 'Stream chat' })).toBeTruthy();
    fireEvent.change(
      screen.getByPlaceholderText('Describe the request or payload text.'),
      {
        target: { value: 'hello service' },
      },
    );
    fireEvent.click(screen.getByRole('button', { name: 'Stream chat' }));

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-a',
        {
          prompt: 'hello service',
        },
        expect.any(Object),
        {
          serviceId: 'default',
        },
      );
    });

    expect(await screen.findByText('Assistant stream')).toBeTruthy();
    expect(screen.getByText('hello from scope service')).toBeTruthy();
    expect(screen.getByText('run-1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Open in Runs' }));

    expect(window.location.pathname).toBe('/runtime/runs');
    const draftKey = new URLSearchParams(window.location.search).get('draftKey');
    expect(draftKey).toBeTruthy();
    expect(loadDraftRunPayload(draftKey)).toEqual(
      expect.objectContaining({
        kind: 'observed_run_session',
        scopeId: 'scope-a',
        serviceOverrideId: 'default',
        endpointId: 'chat',
        actorId: 'actor://scope-a/default',
        commandId: 'cmd-1',
        runId: 'run-1',
        events: [
          expect.objectContaining({
            type: 'RUN_STARTED',
            runId: 'run-1',
          }),
          expect.objectContaining({
            type: 'CUSTOM',
          }),
          expect.objectContaining({
            type: 'TEXT_MESSAGE_CONTENT',
            delta: 'hello from scope service',
          }),
        ],
      }),
    );
  });
});
