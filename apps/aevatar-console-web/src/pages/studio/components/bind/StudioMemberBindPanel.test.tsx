import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import StudioMemberBindPanel from './StudioMemberBindPanel';

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getServiceBindings: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(),
    invokeEndpoint: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

describe('StudioMemberBindPanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (scopeRuntimeApi.getServiceBindings as jest.Mock).mockResolvedValue({
      serviceKey: 'scope-1:default:workspace-demo',
      bindings: [
        {
          bindingId: 'binding-1',
          displayName: 'Knowledge connector',
          bindingKind: 'connector',
          policyIds: ['policy-a'],
          serviceRef: null,
          connectorRef: {
            connectorType: 'mcp',
            connectorId: 'knowledge-base',
          },
          secretRef: null,
          retired: false,
        },
      ],
      updatedAt: '2026-03-26T08:00:00Z',
    });
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
          actorId: 'actor-default',
          commandId: 'cmd-1',
        },
        timestamp: Date.now(),
      };
      yield {
        type: 'TEXT_MESSAGE_CONTENT',
        delta: 'Smoke test succeeded.',
        messageId: 'msg-1',
        timestamp: Date.now(),
      };
    });
  });

  it('renders a contract-first bind layout and reports the default selection', async () => {
    const handleSelectionChange = jest.fn();

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          email: 'abigail@example.com',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        scopeId: 'scope-1',
        preferredServiceId: 'default',
        onSelectionChange: handleSelectionChange,
        scopeBinding: {
          available: true,
          scopeId: 'scope-1',
          serviceId: 'default',
          displayName: 'workspace-demo',
          serviceKey: 'scope-1:default:workspace-demo',
          defaultServingRevisionId: 'rev-2',
          activeServingRevisionId: 'rev-2',
          deploymentId: 'dep-2',
          deploymentStatus: 'Active',
          primaryActorId: 'actor-default',
          updatedAt: '2026-03-26T08:00:00Z',
          revisions: [],
        },
        services: [
          {
            serviceKey: 'scope-1:default:workspace-demo',
            tenantId: 'scope-1',
            appId: 'default',
            namespace: 'default',
            serviceId: 'default',
            displayName: 'workspace-demo',
            defaultServingRevisionId: 'rev-2',
            activeServingRevisionId: 'rev-2',
            deploymentId: 'dep-2',
            primaryActorId: 'actor-default',
            deploymentStatus: 'Active',
            endpoints: [
              {
                endpointId: 'chat',
                displayName: 'Chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
                description: 'Chat with the published workflow.',
              },
            ],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(screen.getByText('Invoke URL')).toBeTruthy();
    expect(screen.getByText('Binding parameters')).toBeTruthy();
    expect(screen.getByText('Snippets')).toBeTruthy();
    expect(screen.getByText('Smoke-test')).toBeTruthy();
    expect(screen.queryByText('Binding Contract')).toBeNull();
    expect(screen.queryByText('Current contract')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open Runs' })).toBeNull();
    expect(screen.queryByText(/^Revisions/)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Activate' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retire' })).toBeNull();
    expect(screen.queryByText('Need auth for a smoke test?')).toBeNull();
    expect(screen.getAllByText('Authorization').length).toBeGreaterThan(0);
    expect(screen.getByText('Authenticated')).toBeTruthy();
    expect(screen.getByText('Resolved from nyxid.')).toBeTruthy();
    expect(screen.queryByText('Environment')).toBeNull();
    expect(screen.queryByText('Rate limit')).toBeNull();
    expect(screen.queryByText('Allowed origins')).toBeNull();
    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-contract-card').textContent).toContain(
        '/api/scopes/scope-1/services/default/invoke/chat:stream',
      );
    });
    await waitFor(() => {
      expect(handleSelectionChange).toHaveBeenCalledWith({
        serviceId: 'default',
        endpointId: 'chat',
      });
    });
  });

  it('runs a chat smoke test and offers a continue-to-invoke action', async () => {
    const handleContinueToInvoke = jest.fn();

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        scopeId: 'scope-1',
        preferredServiceId: 'default',
        onContinueToInvoke: handleContinueToInvoke,
        services: [
          {
            serviceKey: 'scope-1:default:workspace-demo',
            tenantId: 'scope-1',
            appId: 'default',
            namespace: 'default',
            serviceId: 'default',
            displayName: 'workspace-demo',
            defaultServingRevisionId: 'rev-2',
            activeServingRevisionId: 'rev-2',
            deploymentId: 'dep-2',
            primaryActorId: 'actor-default',
            deploymentStatus: 'Active',
            endpoints: [
              {
                endpointId: 'chat',
                displayName: 'Chat',
                kind: 'chat',
                requestTypeUrl: '',
                responseTypeUrl: '',
                description: 'Chat with the published workflow.',
              },
            ],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('Bind smoke test input'), {
      target: {
        value: 'Give me a quick health summary.',
      },
    });

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Send test request' }));
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          prompt: 'Give me a quick health summary.',
        }),
        expect.any(AbortSignal),
        {
          serviceId: 'default',
        },
      );
    });
    expect(await screen.findByText(/Smoke test passed in \d+ms/)).toBeTruthy();
    expect(screen.getByText('Smoke test succeeded.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));
    expect(handleContinueToInvoke).toHaveBeenCalledWith('default', 'chat');
  });
});
