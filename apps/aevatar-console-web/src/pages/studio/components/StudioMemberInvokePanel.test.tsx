import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import StudioMemberInvokePanel from './StudioMemberInvokePanel';

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    bindScopeGAgent: jest.fn(),
  },
}));

describe('StudioMemberInvokePanel', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (runtimeRunsApi.invokeEndpoint as jest.Mock).mockResolvedValue({
      accepted: true,
      commandId: 'cmd-1',
      requestId: 'run-1',
      targetActorId: 'actor-1',
    });
  });

  it('renders the invoke workbench skeleton with a compact contract and a persistent console', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeBinding: {
          activeServingRevisionId: 'rev-2',
          available: true,
          defaultServingRevisionId: 'rev-2',
          deploymentId: 'dep-2',
          deploymentStatus: 'Active',
          displayName: 'workspace-demo',
          primaryActorId: 'actor-default',
          revisions: [
            {
              allocationWeight: 100,
              artifactHash: 'hash-2',
              createdAt: '2026-03-26T07:00:00Z',
              deploymentId: 'dep-2',
              failureReason: '',
              implementationKind: 'workflow',
              inlineWorkflowCount: 1,
              isActiveServing: true,
              isDefaultServing: true,
              isServingTarget: true,
              preparedAt: '2026-03-26T07:01:00Z',
              primaryActorId: 'actor-default',
              publishedAt: '2026-03-26T07:02:00Z',
              retiredAt: null,
              revisionId: 'rev-2',
              scriptDefinitionActorId: '',
              scriptId: '',
              scriptRevision: '',
              scriptSourceHash: '',
              servingState: 'Active',
              staticActorTypeName: '',
              status: 'Published',
              workflowDefinitionActorId: 'scope-workflow:scope-1:default',
              workflowName: 'workspace-demo',
            },
          ],
          scopeId: 'scope-1',
          serviceId: 'default',
          serviceKey: 'scope-1:default:workspace-demo',
          updatedAt: '2026-03-26T08:00:00Z',
        },
        scopeId: 'scope-1',
        selectedMemberLabel: 'workspace-demo',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    expect(await screen.findByTestId('studio-member-invoke-panel')).toBeTruthy();
    expect(screen.getByText('调用契约')).toBeTruthy();
    expect(screen.getByText('调试台')).toBeTruthy();
    expect(screen.getByText('当前结果')).toBeTruthy();
    expect(screen.getByText('Member')).toBeTruthy();
    expect(screen.getByText('Binding Context')).toBeTruthy();
    expect(screen.getByText('Revision')).toBeTruthy();
    expect(screen.getByText('已就绪')).toBeTruthy();
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.getByText('结果')).toBeTruthy();
    expect(screen.getByText('追踪')).toBeTruthy();
    expect(screen.getByText('原始')).toBeTruthy();
    expect(screen.getByTestId('studio-invoke-playground-actions')).toBeTruthy();
    expect(
      screen.getByText('还没有开始调用。先在上方输入提示词或载荷，再发起一次调用。'),
    ).toBeTruthy();
    expect(screen.queryByText('Runs（0）')).toBeNull();
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();
    expect(screen.queryByText('Published service')).toBeNull();
  });

  it('keeps prompt validation local and does not create a failed run for empty chat input', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Chat with the member.',
                displayName: 'Chat',
                endpointId: 'chat',
                kind: 'invoke',
                requestTypeUrl: '',
                responseTypeUrl: '',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: '开始对话' }));

    expect(await screen.findByText('请输入提示词后再开始对话。')).toBeTruthy();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(screen.getByText('已就绪')).toBeTruthy();
    expect(screen.queryByText('缺少提示词')).toBeNull();
    expect(screen.getByText('当前结果')).toBeTruthy();
    expect(
      screen.getByText('还没有开始调用。先在上方输入提示词或载荷，再发起一次调用。'),
    ).toBeTruthy();
    expect(screen.queryByText(/Runs（/)).toBeNull();
    expect(screen.queryByText('这次调用失败了。')).toBeNull();
  });

  it('records runs into the merged Runs area and shows technical detail inline', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        scopeId: 'scope-1',
        services: [
          {
            deploymentStatus: 'Active',
            displayName: 'workspace-demo',
            endpoints: [
              {
                description: 'Send a structured request into the member.',
                displayName: 'Submit',
                endpointId: 'submit',
                kind: 'invoke',
                requestTypeUrl: 'type.googleapis.com/example.Submit',
                responseTypeUrl: 'type.googleapis.com/example.SubmitResult',
              },
            ],
            kind: 'service',
            namespace: 'default',
            primaryActorId: 'actor-default',
            serviceId: 'default',
          },
        ],
      }),
    );

    fireEvent.change(await screen.findByLabelText('调用请求输入'), {
      target: {
        value: 'Route this escalation to billing review.',
      },
    });
    fireEvent.change(screen.getByPlaceholderText('type.googleapis.com/example.Command'), {
      target: {
        value: 'type.googleapis.com/example.Submit',
      },
    });
    fireEvent.change(
      screen.getByPlaceholderText('如需类型化调用，请粘贴预编码的 protobuf payload。'),
      {
        target: {
          value: 'ZXhhbXBsZS1wYXlsb2Fk',
        },
      },
    );

    fireEvent.click(screen.getByRole('button', { name: '执行调用' }));

    await waitFor(() => {
      expect(runtimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        'scope-1',
        expect.objectContaining({
          endpointId: 'submit',
          payloadBase64: 'ZXhhbXBsZS1wYXlsb2Fk',
          payloadTypeUrl: 'type.googleapis.com/example.Submit',
          prompt: 'Route this escalation to billing review.',
        }),
        {
          serviceId: 'default',
        },
      );
    });

    expect(await screen.findByText('Runs（1）')).toBeTruthy();
    expect(
      screen.getByText('这次结构化调用已经返回结果。切到“原始”可以查看完整返回体。'),
    ).toBeTruthy();
    expect(screen.queryByText('运行详情')).toBeNull();
    expect(screen.queryByText('最新输出')).toBeNull();

    const inlineDetail = await screen.findByTestId('studio-invoke-inline-detail');
    const inlineScope = within(inlineDetail);
    expect(inlineScope.getByText('Command ID')).toBeTruthy();
    expect(inlineScope.getByText('cmd-1')).toBeTruthy();
    expect(inlineScope.getByText('Actor ID')).toBeTruthy();
    expect(inlineScope.getByText('actor-1')).toBeTruthy();
    expect(inlineScope.getByText('Duration')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('调用请求输入'), {
      target: {
        value: 'Overwrite prompt',
      },
    });

    expect(screen.getByLabelText('调用请求输入')).toHaveValue('Overwrite prompt');
  });

  it('renders a clear empty state when no selected member is available for invoke', async () => {
    render(
      React.createElement(StudioMemberInvokePanel, {
        emptyState: {
          description:
            '请先在“团队成员”里选择成员，或从绑定页面继续进入，这样调用页面才会稳定固定到单个成员。',
          message: '请选择要调用的成员。',
          type: 'info',
        },
        scopeId: 'scope-1',
        services: [],
      }),
    );

    expect(await screen.findByText('请选择要调用的成员。')).toBeTruthy();
    expect(
      screen.getByText(
        '请先在“团队成员”里选择成员，或从绑定页面继续进入，这样调用页面才会稳定固定到单个成员。',
      ),
    ).toBeTruthy();
    expect(screen.queryByText('调用契约')).toBeNull();
  });
});
