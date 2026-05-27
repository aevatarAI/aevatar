import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { studioApi } from '@/shared/studio/api';
import { renderWithQueryClient } from '../../../../../tests/reactQueryTestUtils';
import StudioMemberBindPanel from './StudioMemberBindPanel';

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getServiceBindings: jest.fn(),
    getServiceRevisions: jest.fn(),
  },
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
    const currentContractTitle = screen.getByText('当前成员合约');
    const snippetsTitle = screen.getByText('集成代码片段');
    const supportingDetailsTitle = screen.getByText('补充明细');
    expect(currentContractTitle).toBeTruthy();
    expect(snippetsTitle).toBeTruthy();
    expect(supportingDetailsTitle).toBeTruthy();
    expect(
      currentContractTitle.compareDocumentPosition(snippetsTitle) &
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
      alignItems: 'stretch',
      display: 'grid',
    });
    expect(primaryGrid.contains(supportingDetailsTitle)).toBe(false);
    const primaryGridStyle = primaryGrid.getAttribute('style') || '';
    expect(primaryGridStyle).not.toContain('height');
    expect(primaryGridStyle).not.toContain('grid-auto-rows');
    const contractSection = screen.getByTestId('studio-bind-contract-section');
    const nextStepSection = screen.getByTestId('studio-bind-next-step-section');
    expect(contractSection).toBeTruthy();
    expect(nextStepSection).toBeTruthy();
    expect(contractSection.contains(nextStepSection)).toBe(true);
    expect(screen.getByTestId('studio-bind-snippet-section')).toBeTruthy();
    expect(screen.getByTestId('studio-bind-supporting-section')).toBeTruthy();
    expect(screen.getByText('当前成员发布')).toBeTruthy();
    expect(screen.getByText('member:default')).toBeTruthy();
    expect(screen.queryByRole('combobox')).toBeNull();
    expect(
      screen.queryByText('选择发布服务'),
    ).toBeNull();
    expect(screen.getByRole('button', { name: '打开调用' })).not.toBeDisabled();
    expect(screen.queryByText('Quick smoke test')).toBeNull();
    expect(screen.queryByText('Test mode')).toBeNull();
    expect(
      screen.queryByText(
        '普通测试直接输入一句话即可；需要固定格式时再选高级输入。',
      ),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: 'Send smoke test' })).toBeNull();
    expect(
      screen.queryByRole('button', {
        name: 'Chat 默认测试 id · chat Chat with the published workflow.',
      }),
    ).toBeNull();
    fireEvent.click(screen.getByText('合约明细'));
    expect(await screen.findByText('发布服务')).toBeTruthy();
    expect(primaryGrid.contains(screen.getByText('发布服务'))).toBe(false);
    expect(screen.queryByText('Binding Contract')).toBeNull();
    expect(screen.queryByText('Current contract')).toBeNull();
    expect(screen.queryByText('Published contract context')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open published service' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open Runs' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Activate' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retire' })).toBeNull();
    expect(screen.queryByText('Need auth for a smoke test?')).toBeNull();
    expect(screen.getAllByText('鉴权').length).toBeGreaterThan(0);
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

  it('offers post-bind Team entry actions when provided', async () => {
    const handleSetEntryAndTest = jest.fn();

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
        postBindEntryActions: {
          memberId: 'default',
          onSetEntryAndTest: handleSetEntryAndTest,
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

    expect(await screen.findByText('这个成员可以设为团队入口。')).toBeTruthy();
    expect(
      screen.queryByRole('button', { name: '设为团队入口' }),
    ).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '设为入口并测试团队' }));

    expect(handleSetEntryAndTest).toHaveBeenCalledTimes(1);
  });

  it('shows a direct Team test action when the member is already the Team entry', async () => {
    const handleSetEntryAndTest = jest.fn();

    renderWithQueryClient(
      React.createElement(StudioMemberBindPanel, {
        authSession: {
          enabled: true,
          authenticated: true,
          scopeId: 'scope-1',
          scopeSource: 'nyxid',
        },
        memberId: 'default',
        scopeId: 'scope-1',
        preferredServiceId: 'default',
        postBindEntryActions: {
          isEntryMember: true,
          memberId: 'default',
          onSetEntryAndTest: handleSetEntryAndTest,
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

    expect(await screen.findByText('这个成员是团队入口。')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '设为入口并测试团队' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '测试团队' }));

    expect(handleSetEntryAndTest).toHaveBeenCalledTimes(1);
  });

  it('opens Invoke from the bind next step', async () => {
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

    await waitFor(() => {
      expect(screen.getByRole('button', { name: '打开调用' })).not.toBeDisabled();
    });

    fireEvent.click(screen.getByRole('button', { name: '打开调用' }));
    expect(handleContinueToInvoke).toHaveBeenCalledWith('default', 'chat');
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

    expect(await screen.findByText('暂无接口数据')).toBeTruthy();
    const continueButton = screen.getByRole('button', {
      name: '打开调用',
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

    expect(await screen.findByText('使用调用前请先选择团队成员。')).toBeTruthy();
    expect(screen.queryByTestId('studio-bind-contract-card')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Send smoke test' })).toBeNull();
    const continueButton = screen.getByRole('button', { name: '打开调用' });
    expect(continueButton).toBeDisabled();

    fireEvent.click(continueButton);

    expect(handleContinueToInvoke).not.toHaveBeenCalled();
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
          description: '先发布当前流程版本，工作室随后会展示这个成员的调用 URL 和接口合约。',
          actionLabel: '绑定当前版本',
        },
        onBindPendingCandidate: handleBindPendingCandidate,
        services: [],
      }),
    );

    expect(await screen.findByTestId('studio-bind-surface')).toBeTruthy();
    expect(
      screen.getByText('draft 暂时还没有发布合约。'),
    ).toBeTruthy();
    expect(screen.getByText('发布当前成员')).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '绑定当前版本' }));
    });

    expect(handleBindPendingCandidate).toHaveBeenCalledTimes(1);
  });

  it('clears the previous member bind notice when the bind candidate changes', async () => {
    const handleBindPendingCandidate = jest.fn().mockResolvedValue(undefined);
    const CandidateHarness = () => {
      const [candidate, setCandidate] = React.useState({
        kind: 'workflow' as const,
        displayName: 'draft1',
        description: '先发布当前流程版本，工作室随后会展示这个成员的调用 URL 和接口合约。',
        actionLabel: '绑定当前版本',
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
                description: '先发布当前流程版本，工作室随后会展示这个成员的调用 URL 和接口合约。',
                actionLabel: '绑定当前版本',
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
      fireEvent.click(screen.getByRole('button', { name: '绑定当前版本' }));
    });

    expect(
      await screen.findByText(
        'draft1 的绑定请求已受理。运行完成后，工作室会展示发布后的调用合约。',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Switch candidate' }));

    expect(await screen.findByText('joker 暂时还没有发布合约。')).toBeTruthy();
    expect(
      screen.queryByText(
        'draft1 的绑定请求已受理。运行完成后，工作室会展示发布后的调用合约。',
      ),
    ).toBeNull();
    expect(
      screen.queryByText(
        'joker 的绑定请求已受理。运行完成后，工作室会展示发布后的调用合约。',
      ),
    ).toBeNull();
  });
});
