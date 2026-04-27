import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import StudioMemberBindPanel from './StudioMemberBindPanel';

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
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue({ ok: true });
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
    const bindSurfaceStyle =
      screen.getByTestId('studio-bind-surface').getAttribute('style') || '';
    expect(bindSurfaceStyle).not.toContain('overflow');
    expect(bindSurfaceStyle).not.toContain('height');
    const primaryGrid = screen.getByTestId('studio-bind-primary-grid');
    expect(primaryGrid).toHaveStyle({
      alignItems: 'start',
      display: 'grid',
    });
    expect(primaryGrid.contains(supportingDetailsTitle)).toBe(false);
    const primaryGridStyle = primaryGrid.getAttribute('style') || '';
    expect(primaryGridStyle).not.toContain('height');
    expect(primaryGridStyle).not.toContain('grid-auto-rows');
    expect(screen.getByTestId('studio-bind-contract-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-smoke-test-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-snippet-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-supporting-section')).toBeTruthy();
    fireEvent.click(screen.getByText('Published contract source'));
    expect(await screen.findByText('Published service')).toBeTruthy();
    expect(primaryGrid.contains(screen.getByText('Published service'))).toBe(false);
    expect(screen.queryByText('Binding Contract')).toBeNull();
    expect(screen.queryByText('Current contract')).toBeNull();
    expect(screen.queryByText('Published contract context')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open published service' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open Runs' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Activate' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retire' })).toBeNull();
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
      expect(screen.getByTestId('studio-bind-contract-card').textContent).toContain(
        '/api/scopes/scope-1/members/default/invoke/chat:stream',
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
    const buildWorkflowYamls = jest.fn().mockResolvedValue([
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
    expect(screen.getByText('Second node final output.')).toBeTruthy();
    expect(
      screen.getByText(
        'Current draft',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue to Invoke' }));
    expect(handleContinueToInvoke).toHaveBeenCalledWith('default', 'chat');
  });

  it('does not block current draft smoke tests on published endpoint auth state', async () => {
    const buildWorkflowYamls = jest.fn().mockResolvedValue(['name: workspace-demo']);

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

    const smokeButton = await screen.findByRole('button', { name: 'Send smoke test' });
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
    expect(screen.getByText('Publish current member')).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Bind current revision' }));
    });

    expect(handleBindPendingCandidate).toHaveBeenCalledTimes(1);
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
      fireEvent.click(screen.getByRole('button', { name: 'Bind current revision' }));
    });

    expect(await screen.findByText('draft1 is now bound. Review the invoke contract below.')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Switch candidate' }));

    expect(await screen.findByText('No published contract exists for joker yet.')).toBeTruthy();
    expect(
      screen.queryByText('draft1 is now bound. Review the invoke contract below.'),
    ).toBeNull();
    expect(
      screen.queryByText('joker is now bound. Review the invoke contract below.'),
    ).toBeNull();
  });
});
