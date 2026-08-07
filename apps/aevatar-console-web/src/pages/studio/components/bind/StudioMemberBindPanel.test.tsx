import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import StudioMemberBindPanel from './StudioMemberBindPanel';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('@/shared/ui/ConsoleToast', () => {
  const actual = jest.requireActual('@/shared/ui/ConsoleToast');
  return {
    ...actual,
    useConsoleToast: () => mockConsoleToast,
  };
});

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getServiceBindings: jest.fn(),
    getServiceRevisions: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamDraftRun: jest.fn(),
    streamChat: jest.fn(),
    invokeEndpoint: jest.fn(),
  },
}));

jest.mock('@/shared/agui/sseFrameNormalizer', () => ({
  parseBackendSSEStream: jest.fn(),
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getMemberBinding: jest.fn(),
  },
}));

describe('StudioMemberBindPanel', () => {
  beforeEach(() => {
    setLocale('en-US', false);
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
    (scopeRuntimeApi.getServiceRevisions as jest.Mock).mockResolvedValue({
      scopeId: 'scope-1',
      serviceId: 'default',
      serviceKey: 'scope-1:default:workspace-demo',
      displayName: 'workspace-demo',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'dep-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-default',
      catalogStateVersion: 2,
      catalogLastEventId: 'evt-2',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'active',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'active',
          deploymentId: 'dep-2',
          primaryActorId: 'actor-default',
          createdAt: '2026-03-26T07:50:00Z',
          preparedAt: '2026-03-26T07:55:00Z',
          publishedAt: '2026-03-26T08:00:00Z',
          retiredAt: null,
          workflowName: 'workspace-demo',
          workflowDefinitionActorId: 'workflow-def-1',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
        {
          revisionId: 'rev-1',
          implementationKind: 'workflow',
          status: 'retired',
          artifactHash: 'hash-1',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: false,
          isServingTarget: false,
          allocationWeight: 0,
          servingState: 'retired',
          deploymentId: 'dep-1',
          primaryActorId: 'actor-default',
          createdAt: '2026-03-25T07:50:00Z',
          preparedAt: '2026-03-25T07:55:00Z',
          publishedAt: '2026-03-25T08:00:00Z',
          retiredAt: '2026-03-26T06:00:00Z',
          workflowName: 'workspace-demo',
          workflowDefinitionActorId: 'workflow-def-1',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValue({
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
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'active',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'active',
          deploymentId: 'dep-2',
          primaryActorId: 'actor-default',
          createdAt: '2026-03-26T07:50:00Z',
          preparedAt: '2026-03-26T07:55:00Z',
          publishedAt: '2026-03-26T08:00:00Z',
          retiredAt: null,
          workflowName: 'workspace-demo',
          workflowDefinitionActorId: 'workflow-def-1',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue({
      ok: true,
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
        delta: 'First node output.',
        messageId: 'msg-1',
        timestamp: Date.now(),
      };
      yield {
        type: 'RUN_FINISHED',
        result: {
          output: 'Second node final output.',
        },
        runId: 'run-1',
        threadId: 'thread-1',
        timestamp: Date.now(),
      };
    });
  });

  it('renders a current-member contract layout and reports the default selection', async () => {
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
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
        onSelectionChange: handleSelectionChange,
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
    const currentContractTitle = screen.getByText('Current member contract');
    const smokeTestTitle = screen.getByText('Quick smoke test');
    const snippetsTitle = screen.getByText('Integration snippets');
    const supportingDetailsTitle = screen.getByText('Supporting details');
    expect(currentContractTitle).toBeTruthy();
    expect(smokeTestTitle).toBeTruthy();
    expect(snippetsTitle).toBeTruthy();
    expect(supportingDetailsTitle).toBeTruthy();
    expect(
      currentContractTitle.compareDocumentPosition(smokeTestTitle) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      smokeTestTitle.compareDocumentPosition(snippetsTitle) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(
      snippetsTitle.compareDocumentPosition(supportingDetailsTitle) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    const primaryGrid = screen.getByTestId('studio-bind-primary-grid');
    expect(primaryGrid.contains(supportingDetailsTitle)).toBe(false);
    expect(screen.getByTestId('studio-bind-contract-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-smoke-test-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-snippet-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-supporting-section')).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
        'Bind is ready. Run a quick smoke test or continue to Invoke for the full transcript and Observe handoff.',
      );
    });
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'ready',
    );
    expect(screen.getByText('Current member publication')).toBeTruthy();
    expect(screen.queryByText('member:default')).toBeNull();
    expect(screen.queryByRole('combobox')).toBeNull();
    expect(screen.queryByText('Select a published service')).toBeNull();
    expect(
      screen.getByRole('button', {
        name: 'Chat Default test Endpoint ready Chat with the published workflow.',
      }),
    ).toHaveAttribute('aria-pressed', 'true');
    expect(
      screen.getByText(
        'For ordinary tests, you can directly enter a sentence; when you need a fixed format, choose advanced input.',
      ),
    ).toBeTruthy();
    fireEvent.click(screen.getByText('Contract details'));
    expect(await screen.findByText('Published service')).toBeTruthy();
    expect(primaryGrid.contains(screen.getByText('Published service'))).toBe(
      false,
    );
    expect(screen.queryByText('Binding Contract')).toBeNull();
    expect(screen.queryByText('Current contract')).toBeNull();
    expect(screen.queryByText('Published contract context')).toBeNull();
    expect(
      screen.queryByRole('button', { name: 'Open published service' }),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open Runs' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Activate' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retire' })).toBeNull();
    expect(
      screen.queryByRole('button', { name: '设为入口并测试 Team' }),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: '测试 Team' })).toBeNull();
    expect(screen.queryByText('Need auth for a smoke test?')).toBeNull();
    expect(screen.getAllByText('Authorization').length).toBeGreaterThan(0);
    await waitFor(() => {
      expect(scopeRuntimeApi.getServiceRevisions).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
    });
    await waitFor(() => {
      expect(studioApi.getMemberBinding).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
    });
    expect(screen.queryByText('Environment')).toBeNull();
    expect(screen.queryByText('Rate limit')).toBeNull();
    expect(screen.queryByText('Allowed origins')).toBeNull();
    await waitFor(() => {
      expect(
        screen.getByTestId('studio-bind-contract-card').textContent,
      ).toContain('/api/scopes/scope-1/members/default/invoke/chat:stream');
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
    const buildWorkflowYamls = jest
      .fn()
      .mockResolvedValue([
        'name: workspace-demo',
        'steps:\n  tell_joke:\n    type: llm_call',
      ]);

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        buildWorkflowYamls,
        memberId: 'default',
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
      fireEvent.click(screen.getByRole('button', { name: 'Send smoke test' }));
    });

    await waitFor(() => {
      expect(buildWorkflowYamls).toHaveBeenCalledTimes(1);
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          prompt: 'Give me a quick health summary.',
          workflowYamls: [
            'name: workspace-demo',
            'steps:\n  tell_joke:\n    type: llm_call',
          ],
        }),
        expect.any(AbortSignal),
      );
    });
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(await screen.findByText(/Smoke test passed in \d+ms/)).toBeTruthy();
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'Smoke test passed. Continue to Invoke for a full run transcript, then use Observe for backend events.',
    );
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'ready',
    );
    expect(screen.queryByText('Run run-1')).toBeNull();
    expect(screen.queryByText('run-1')).toBeNull();
    expect(
      screen.queryByText('The current Studio draft accepted the request.'),
    ).toBeNull();
    expect(screen.getByText('Second node final output.')).toBeTruthy();
    expect(screen.getByText('Current draft')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));
    expect(handleContinueToInvoke).toHaveBeenCalledWith('default', 'chat');
  });

  it('describes non-chat smoke test success as a completed contract response', async () => {
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValueOnce({
      ok: true,
    });

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
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
                endpointId: 'submit',
                displayName: 'Submit',
                kind: 'command',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
                description: 'Submit a request.',
              },
            ],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    await act(async () => {
      fireEvent.click(
        await screen.findByRole('button', { name: 'Send smoke test' }),
      );
    });

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
        }),
        expect.objectContaining({
          serviceId: 'default',
        }),
      );
    });
    expect(await screen.findByText(/Smoke test passed in \d+ms/)).toBeTruthy();
    expect(
      screen.getByText('The selected contract returned without an error.'),
    ).toBeTruthy();
    expect(
      screen.queryByText('The selected contract accepted the request.'),
    ).toBeNull();
  });

  it('blocks continuing to Invoke when the published service has no endpoints', async () => {
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
        memberId: 'script-4',
        scopeId: 'scope-1',
        preferredServiceId: 'script-4',
        onContinueToInvoke: handleContinueToInvoke,
        services: [
          {
            serviceKey: 'scope-1:default:script-4',
            tenantId: 'scope-1',
            appId: 'default',
            namespace: 'default',
            serviceId: 'script-4',
            displayName: 'script-4',
            defaultServingRevisionId: 'rev-script-1',
            activeServingRevisionId: 'rev-script-1',
            deploymentId: '',
            primaryActorId: 'actor-script-4',
            deploymentStatus: 'Active',
            endpoints: [],
            policyIds: [],
            updatedAt: '2026-04-29T08:00:00Z',
          },
        ],
      }),
    );

    expect(await screen.findByText('No endpoint data available')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'The member is published, but Studio has no callable endpoint yet. Wait for the contract to refresh before continuing.',
    );
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'waiting',
    );
    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueButton).toBeDisabled();

    fireEvent.click(continueButton);

    expect(handleContinueToInvoke).not.toHaveBeenCalled();
  });

  it('keeps published Invoke unavailable until a backend member is selected', async () => {
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

    expect(
      await screen.findByText('Select a Team member before using Invoke.'),
    ).toBeTruthy();
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'Bind can inspect this service, but Invoke stays blocked until Studio resolves a Team member target.',
    );
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'blocked',
    );
    expect(screen.queryByTestId('studio-bind-contract-card')).toBeNull();
    expect(
      screen.getByRole('button', { name: 'Send smoke test' }),
    ).toBeDisabled();
    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueButton).toBeDisabled();

    fireEvent.click(continueButton);

    expect(handleContinueToInvoke).not.toHaveBeenCalled();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(runtimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
  });

  it('does not block current draft smoke tests on published endpoint auth state', async () => {
    const buildWorkflowYamls = jest
      .fn()
      .mockResolvedValue(['name: workspace-demo']);

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: false,
          name: '',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        buildWorkflowYamls,
        scopeId: 'scope-1',
        preferredServiceId: 'default',
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

    const smokeButton = await screen.findByRole('button', {
      name: 'Send smoke test',
    });
    expect(smokeButton).not.toBeDisabled();

    await act(async () => {
      fireEvent.click(smokeButton);
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          workflowYamls: ['name: workspace-demo'],
        }),
        expect.any(AbortSignal),
      );
    });
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it('offers a bind action for the current workflow draft before any published service exists', async () => {
    const handleBindPendingCandidate = jest.fn().mockResolvedValue(undefined);

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
        pendingBindingCandidate: {
          kind: 'workflow',
          displayName: 'draft',
          description:
            'Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member.',
          actionLabel: 'Bind current revision',
        },
        onBindPendingCandidate: handleBindPendingCandidate,
        services: [],
      }),
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(
      screen.getByText('No published contract exists for draft yet.'),
    ).toBeTruthy();
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'This member still needs Bind. Publish the current revision before trying Invoke or Observe.',
    );
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'blocked',
    );
    expect(screen.getByText('Publish current member')).toBeTruthy();

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current revision' }),
      );
    });

    expect(handleBindPendingCandidate).toHaveBeenCalledTimes(1);
  });

  it('explains that a pending bind is not ready for Invoke until the contract materializes', async () => {
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce({
      available: false,
      scopeId: 'scope-1',
      serviceId: '',
      displayName: '',
      serviceKey: '',
      defaultServingRevisionId: '',
      activeServingRevisionId: '',
      deploymentId: '',
      deploymentStatus: '',
      primaryActorId: '',
      updatedAt: '',
      revisions: [],
      currentBindingRun: {
        bindingRunId: 'bind-member-1',
        completedAtUtc: null,
        createdAtUtc: '2026-06-02T03:30:00Z',
        failure: null,
        memberId: 'draft',
        message: '',
        scopeId: 'scope-1',
        stateVersion: null,
        status: 'platform_binding_pending',
        targetServiceId: null,
        updatedAtUtc: '2026-06-02T03:30:05Z',
      },
    });

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'draft',
        scopeId: 'scope-1',
        pendingBindingCandidate: {
          kind: 'workflow',
          displayName: 'draft',
          description:
            'Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member.',
          actionLabel: 'Bind current revision',
        },
        onBindPendingCandidate: jest.fn(),
        services: [],
      }),
    );

    expect(
      await screen.findByText(
        /Platform publication is still running; Invoke is not ready/,
      ),
    ).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
        'Bind accepted the publication request. Stay here until Studio observes the published contract, then continue to Invoke.',
      );
    });
  });

  it('explains a failed bind run and keeps the previous published contract actions available', async () => {
    const handleContinueToInvoke = jest.fn();
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce({
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
      lastBinding: {
        boundAt: '2026-03-26T08:00:00Z',
        implementationKind: 'workflow',
        revisionId: 'rev-2',
      },
      currentBindingRun: {
        bindingRunId: 'bind-member-failed',
        completedAtUtc: '2026-06-02T03:32:00Z',
        createdAtUtc: '2026-06-02T03:30:00Z',
        failure: {
          code: 'publish_failed',
          message: 'Publication rejected by platform.',
        },
        memberId: 'default',
        message: '',
        scopeId: 'scope-1',
        stateVersion: 5,
        status: 'failed',
        targetServiceId: 'default',
        updatedAtUtc: '2026-06-02T03:32:00Z',
      },
    });

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
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
                description: 'Chat with the previous workflow contract.',
              },
            ],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
        'Bind did not publish this member. Return to Build to adjust the member definition before retrying publication.',
      );
    });
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'failed',
    );
    expect(
      screen.getByRole('button', { name: 'Send smoke test' }),
    ).toBeEnabled();
    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueButton).toBeEnabled();

    fireEvent.click(continueButton);

    expect(handleContinueToInvoke).toHaveBeenCalledWith('default', 'chat');
  });

  it('waits for endpoint data after a succeeded bind run materializes without a callable contract', async () => {
    const handleContinueToInvoke = jest.fn();
    (studioApi.getMemberBinding as jest.Mock).mockResolvedValueOnce({
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
      lastBinding: {
        boundAt: '2026-03-26T08:00:00Z',
        implementationKind: 'workflow',
        revisionId: 'rev-2',
      },
      currentBindingRun: {
        bindingRunId: 'bind-member-succeeded',
        completedAtUtc: '2026-06-02T03:32:00Z',
        createdAtUtc: '2026-06-02T03:30:00Z',
        failure: null,
        memberId: 'default',
        message: '',
        scopeId: 'scope-1',
        stateVersion: 6,
        status: 'succeeded',
        targetServiceId: 'default',
        updatedAtUtc: '2026-06-02T03:32:00Z',
      },
    });

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
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
            endpoints: [],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
        'Bind completed, and Studio is refreshing the published contract. Continue to Invoke after endpoint data appears.',
      );
    });
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'waiting',
    );
    expect(
      screen.getByRole('button', { name: 'Send smoke test' }),
    ).toBeDisabled();
    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(continueButton).toBeDisabled();

    fireEvent.click(continueButton);

    expect(handleContinueToInvoke).not.toHaveBeenCalled();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it('blocks smoke test and Invoke while a newer bind run is still publishing over an old contract', async () => {
    const handleContinueToInvoke = jest.fn();
    (studioApi.getMemberBinding as jest.Mock).mockImplementationOnce(
      async () => ({
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
        lastBinding: {
          boundAt: '2026-03-26T08:00:00Z',
          implementationKind: 'workflow',
          revisionId: 'rev-2',
        },
        currentBindingRun: {
          bindingRunId: 'bind-member-2',
          completedAtUtc: null,
          createdAtUtc: '2026-06-02T03:30:00Z',
          failure: null,
          memberId: 'default',
          message: '',
          scopeId: 'scope-1',
          stateVersion: 4,
          status: 'platform_binding_pending',
          targetServiceId: 'default',
          updatedAtUtc: '2026-06-02T03:30:05Z',
        },
      }),
    );

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
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
                description: 'Chat with the previous workflow contract.',
              },
            ],
            policyIds: [],
            updatedAt: '2026-03-26T08:00:00Z',
          },
        ],
      }),
    );

    await waitFor(() => {
      expect(studioApi.getMemberBinding).toHaveBeenCalledWith(
        'scope-1',
        'default',
      );
    });
    await waitFor(() => {
      expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
        'Bind accepted the publication request. Stay here until Studio observes the published contract, then continue to Invoke.',
      );
    });

    const smokeButton = screen.getByRole('button', { name: 'Send smoke test' });
    const continueButton = screen.getByRole('button', {
      name: 'Continue to Invoke',
    });
    expect(smokeButton).toBeDisabled();
    expect(continueButton).toBeDisabled();

    fireEvent.click(smokeButton);
    fireEvent.click(continueButton);

    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(handleContinueToInvoke).not.toHaveBeenCalled();
  });

  it('explains that a smoke test failure is scoped to the contract check', async () => {
    (parseBackendSSEStream as jest.Mock).mockImplementationOnce(
      async function* () {
        yield {
          type: 'RUN_ERROR',
          code: 'ERR_RUNTIME',
          message: 'The workflow run failed.',
          runId: 'run-1',
        };
      },
    );

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
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

    await act(async () => {
      fireEvent.click(
        await screen.findByRole('button', { name: 'Send smoke test' }),
      );
    });

    expect(await screen.findByText('Smoke test failed')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'Smoke test failed only this contract check. Retry here, or use Invoke when you need full events and typed payload debugging.',
    );
    expect(screen.getByTestId('studio-bind-flow-guidance')).toHaveTextContent(
      'failed',
    );
    expect(
      screen.getByRole('button', { name: 'Send smoke test' }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', { name: 'Continue to Invoke' }),
    ).toBeEnabled();
  });

  it('uses a generic shared toast when a smoke-test transport request fails', async () => {
    (runtimeRunsApi.streamChat as jest.Mock).mockRejectedValueOnce(
      new Error('Backend rejected the smoke prompt.'),
    );

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          name: 'Abigail Deng',
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
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

    await act(async () => {
      fireEvent.click(
        await screen.findByRole('button', { name: 'Send smoke test' }),
      );
    });

    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Could not complete the smoke test. Try again.',
      );
    });
    expect(mockConsoleToast.error).not.toHaveBeenCalledWith(
      'Backend rejected the smoke prompt.',
    );
    expect(
      screen.queryByText('Backend rejected the smoke prompt.'),
    ).not.toBeInTheDocument();
  });

  it('clears the previous member bind notice when the bind candidate changes', async () => {
    const handleBindPendingCandidate = jest.fn().mockResolvedValue(undefined);
    const CandidateHarness = () => {
      const [candidate, setCandidate] = React.useState({
        kind: 'workflow' as const,
        displayName: 'draft1',
        description:
          'Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member.',
        actionLabel: 'Bind current revision',
      });

      return React.createElement(React.Fragment, null, [
        React.createElement(
          'button',
          {
            key: 'switch',
            type: 'button',
            onClick: () =>
              setCandidate({
                kind: 'workflow',
                displayName: 'joker',
                description:
                  'Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member.',
                actionLabel: 'Bind current revision',
              }),
          },
          'Switch candidate',
        ),
        React.createElement(StudioMemberBindPanel, {
          key: 'panel',
          authSession: {
            enabled: true,
            authenticated: true,
            name: 'Abigail Deng',
            scopeId: 'scope-1',
            scopeSource: 'nyxid',
          },
          scopeId: 'scope-1',
          pendingBindingCandidate: candidate,
          onBindPendingCandidate: handleBindPendingCandidate,
          services: [],
        }),
      ]);
    };

    renderWithQueryClient(React.createElement(CandidateHarness));

    await act(async () => {
      fireEvent.click(
        screen.getByRole('button', { name: 'Bind current revision' }),
      );
    });

    expect(
      await screen.findByText(
        'draft1 binding request was accepted. Studio will show the published contract after the run completes.',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Switch candidate' }));

    expect(
      await screen.findByText('No published contract exists for joker yet.'),
    ).toBeTruthy();
    expect(
      screen.queryByText(
        'draft1 binding request was accepted. Studio will show the published contract after the run completes.',
      ),
    ).toBeNull();
    expect(
      screen.queryByText(
        'joker binding request was accepted. Studio will show the published contract after the run completes.',
      ),
    ).toBeNull();
  });

  it('reports pending bind failures with a safe toast', async () => {
    const handleBindPendingCandidate = jest
      .fn()
      .mockRejectedValue(new Error('POST /api/studio/bind returned 500'));

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
        pendingBindingCandidate: {
          kind: 'workflow',
          displayName: 'draft1',
          description: 'Publish the current workflow revision first.',
          actionLabel: 'Bind current revision',
        },
        onBindPendingCandidate: handleBindPendingCandidate,
        services: [],
      }),
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Bind current revision' }),
    );

    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Binding action could not be completed. Try again.',
      );
    });
    expect(
      screen.queryByText('POST /api/studio/bind returned 500'),
    ).not.toBeInTheDocument();
  });
});
