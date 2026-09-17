import { fireEvent, screen, waitFor } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import DeploymentsPage from './index';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    advanceRollout: jest.fn(),
    deactivateDeployment: jest.fn(),
    deployRevision: jest.fn(),
    getDeployments: jest.fn(),
    getRevisions: jest.fn(),
    getRollout: jest.fn(),
    getService: jest.fn(),
    getServingSet: jest.fn(),
    getTraffic: jest.fn(),
    listServices: jest.fn(),
    pauseRollout: jest.fn(),
    replaceServingTargets: jest.fn(),
    resumeRollout: jest.fn(),
    rollbackRollout: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      scope: {
        id: 'scope-1',
      },
    })),
  },
}));

const { servicesApi: mockServicesApi } = jest.requireMock(
  '@/shared/api/servicesApi',
) as {
  servicesApi: {
    advanceRollout: jest.Mock;
    deactivateDeployment: jest.Mock;
    deployRevision: jest.Mock;
    getDeployments: jest.Mock;
    getRevisions: jest.Mock;
    getRollout: jest.Mock;
    getService: jest.Mock;
    getServingSet: jest.Mock;
    getTraffic: jest.Mock;
    listServices: jest.Mock;
    pauseRollout: jest.Mock;
    replaceServingTargets: jest.Mock;
    resumeRollout: jest.Mock;
    rollbackRollout: jest.Mock;
  };
};

function renderDeploymentsPage(path = '/deployments?tenantId=scope-1') {
  window.history.replaceState({}, '', path);
  return renderWithQueryClient(React.createElement(DeploymentsPage));
}

beforeEach(() => {
  jest.clearAllMocks();
  setLocale('zh-CN', false);

  mockServicesApi.listServices.mockResolvedValue([
    {
      serviceKey: 'scope-1:trade-agent',
      tenantId: 'scope-1',
      appId: 'trade-app',
      namespace: 'cn.market',
      serviceId: 'trade-agent',
      displayName: 'Trade Agent',
      defaultServingRevisionId: 'rev-11',
      activeServingRevisionId: 'rev-11',
      deploymentId: 'dep-1',
      primaryActorId: 'actor-1',
      deploymentStatus: 'active',
      endpoints: [],
      policyIds: ['policy-1'],
      updatedAt: '2026-03-30T10:00:00Z',
    },
  ]);

  mockServicesApi.getService.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    tenantId: 'scope-1',
    appId: 'trade-app',
    namespace: 'cn.market',
    serviceId: 'trade-agent',
    displayName: 'Trade Agent',
    defaultServingRevisionId: 'rev-11',
    activeServingRevisionId: 'rev-11',
    deploymentId: 'dep-1',
    primaryActorId: 'actor-1',
    deploymentStatus: 'active',
    endpoints: [],
    policyIds: ['policy-1'],
    updatedAt: '2026-03-30T10:00:00Z',
  });

  mockServicesApi.getRevisions.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    revisions: [
      {
        revisionId: 'rev-12',
        implementationKind: 'workflow',
        status: 'validated',
        artifactHash: 'hash-12',
        failureReason: '',
        endpoints: [],
        createdAt: '2026-03-30T10:00:00Z',
        preparedAt: '2026-03-30T10:02:00Z',
        publishedAt: '2026-03-30T10:05:00Z',
        retiredAt: null,
      },
      {
        revisionId: 'rev-11',
        implementationKind: 'workflow',
        status: 'active',
        artifactHash: 'hash-11',
        failureReason: '',
        endpoints: [],
        createdAt: '2026-03-29T10:00:00Z',
        preparedAt: '2026-03-29T10:02:00Z',
        publishedAt: '2026-03-29T10:05:00Z',
        retiredAt: null,
      },
    ],
    updatedAt: '2026-03-30T10:00:00Z',
  });

  mockServicesApi.getDeployments.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    deployments: [
      {
        deploymentId: 'dep-1',
        revisionId: 'rev-11',
        primaryActorId: 'actor-1',
        status: 'active',
        activatedAt: '2026-03-29T10:05:00Z',
        updatedAt: '2026-03-30T10:00:00Z',
      },
    ],
    updatedAt: '2026-03-30T10:00:00Z',
  });

  mockServicesApi.getServingSet.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    generation: 3,
    activeRolloutId: 'rollout-1',
    targets: [
      {
        deploymentId: 'dep-1',
        revisionId: 'rev-11',
        primaryActorId: 'actor-1',
        allocationWeight: 90,
        servingState: 'active',
        enabledEndpointIds: ['chat'],
      },
      {
        deploymentId: 'dep-2',
        revisionId: 'rev-12',
        primaryActorId: 'actor-2',
        allocationWeight: 10,
        servingState: 'canary',
        enabledEndpointIds: ['chat'],
      },
    ],
    updatedAt: '2026-03-30T10:00:00Z',
  });

  mockServicesApi.getRollout.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    rolloutId: 'rollout-1',
    displayName: 'March Canary',
    status: 'canary',
    currentStageIndex: 1,
    stages: [
      {
        stageId: 'stage-0',
        stageIndex: 0,
        targets: [],
      },
      {
        stageId: 'stage-1',
        stageIndex: 1,
        targets: [
          {
            deploymentId: 'dep-1',
            revisionId: 'rev-11',
            primaryActorId: 'actor-1',
            allocationWeight: 90,
            servingState: 'active',
            enabledEndpointIds: ['chat'],
          },
          {
            deploymentId: 'dep-2',
            revisionId: 'rev-12',
            primaryActorId: 'actor-2',
            allocationWeight: 10,
            servingState: 'canary',
            enabledEndpointIds: ['chat'],
          },
        ],
      },
    ],
    baselineTargets: [
      {
        deploymentId: 'dep-1',
        revisionId: 'rev-11',
        primaryActorId: 'actor-1',
        allocationWeight: 100,
        servingState: 'active',
        enabledEndpointIds: ['chat'],
      },
    ],
    failureReason: '',
    startedAt: '2026-03-30T10:01:00Z',
    updatedAt: '2026-03-30T10:05:00Z',
  });

  mockServicesApi.getTraffic.mockResolvedValue({
    serviceKey: 'scope-1:trade-agent',
    generation: 3,
    activeRolloutId: 'rollout-1',
    endpoints: [
      {
        endpointId: 'chat',
        targets: [
          {
            deploymentId: 'dep-1',
            revisionId: 'rev-11',
            primaryActorId: 'actor-1',
            allocationWeight: 90,
            servingState: 'active',
          },
          {
            deploymentId: 'dep-2',
            revisionId: 'rev-12',
            primaryActorId: 'actor-2',
            allocationWeight: 10,
            servingState: 'canary',
          },
        ],
      },
    ],
    updatedAt: '2026-03-30T10:05:00Z',
  });

  mockServicesApi.deployRevision.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-1',
    correlationId: 'corr-1',
  });

  mockServicesApi.replaceServingTargets.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-2',
    correlationId: 'corr-2',
  });

  mockServicesApi.advanceRollout.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-3',
    correlationId: 'corr-3',
  });
  mockServicesApi.pauseRollout.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-4',
    correlationId: 'corr-4',
  });
  mockServicesApi.resumeRollout.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-5',
    correlationId: 'corr-5',
  });
  mockServicesApi.rollbackRollout.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-6',
    correlationId: 'corr-6',
  });
  mockServicesApi.deactivateDeployment.mockResolvedValue({
    targetActorId: 'actor-1',
    commandId: 'cmd-7',
    correlationId: 'corr-7',
  });
});

afterEach(() => {
  cleanupTestQueryClients();
});

describe('DeploymentsPage', () => {
  it('shows the service deployment list before an operator opens a service', async () => {
    renderDeploymentsPage();

    expect(await screen.findByText('Platform')).toBeInTheDocument();
    expect(
      await screen.findByRole('heading', { name: 'Deployments' }),
    ).toBeInTheDocument();
    expect(
      await screen.findByText(
        '部署是 Platform 的发布工作台，聚焦当前服务态、发布推进进度和流量分配。',
      ),
    ).toBeInTheDocument();
    expect(await screen.findByText('发布服务列表')).toBeInTheDocument();
    expect(await screen.findByText('Trade Agent')).toBeInTheDocument();
    expect(screen.queryByText('发布摘要')).toBeNull();
    expect(screen.queryByText('正在加载发布服务')).toBeNull();
  });

  it('keeps the deployment inventory in a loading state until the first response resolves', async () => {
    let resolveServices: (value: unknown[]) => void = () => {};
    mockServicesApi.listServices.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveServices = resolve;
        }),
    );

    renderDeploymentsPage();

    expect(await screen.findByRole('status')).toHaveAttribute(
      'data-variant',
      'table',
    );
    expect(screen.getByText('正在加载发布服务')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(
      screen.queryByText('发布对象清单仍在加载，返回前不会把当前范围误判为空。'),
    ).toBeNull();
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(4);
    expect(screen.queryByText('当前范围没有服务')).toBeNull();

    resolveServices([]);

    expect(await screen.findByText('当前范围没有服务')).toBeInTheDocument();
  });

  it('separates deployment inventory failures from a true empty scope', async () => {
    mockServicesApi.listServices.mockRejectedValueOnce(
      new Error('deployment inventory unavailable'),
    );

    renderDeploymentsPage();

    expect(await screen.findByText('发布服务列表暂不可用')).toBeInTheDocument();
    expect(
      screen.getByText('deployment inventory unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '重试发布列表' }),
    ).toBeInTheDocument();
    expect(screen.queryByText('当前范围没有服务')).toBeNull();
  });

  it('shows an actionable deployment empty state only after an empty response', async () => {
    mockServicesApi.listServices.mockResolvedValueOnce([]);

    renderDeploymentsPage();

    expect(await screen.findByText('当前范围没有服务')).toBeInTheDocument();
    expect(
      screen.getByText(
        '当前团队、App 和 Namespace 下没有可发布服务。可以调整范围后重新加载。',
      ),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: '调整发布范围' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/deployments');
    });
  });

  it('warns when scope edits have not been loaded yet', async () => {
    renderDeploymentsPage();

    expect(await screen.findByText('Trade Agent')).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText('命名空间'), {
      target: {
        value: 'cn.changed',
      },
    });

    expect(await screen.findByText('范围已编辑但尚未加载')).toBeInTheDocument();
    expect(screen.getByText('显示上次加载范围')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '加载范围变更' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Trade Agent').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: '重置' }));

    expect(await screen.findByText('已加载范围已锁定')).toBeInTheDocument();
    expect(screen.getByText('显示已加载范围')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '加载发布列表' }),
    ).toBeInTheDocument();
  });

  it('renders the selected service workbench from URL context', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    expect(await screen.findByText('部署数')).toBeInTheDocument();
    expect(
      await screen.findByRole('tab', { name: '部署目录', selected: true }),
    ).toBeInTheDocument();
    expect(
      await screen.findByRole('tab', { name: 'Serving' }),
    ).toBeInTheDocument();
    expect(
      await screen.findByRole('tab', { name: 'Rollout' }),
    ).toBeInTheDocument();
  });

  it('opens the selected deployment from a governance handoff URL', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent&deploymentId=dep-1',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    expect(await screen.findByText('部署数')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '部署候选版本' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Trade Agent').length).toBeGreaterThan(0);
    expect(screen.queryByText('dep-1')).toBeNull();
  });

  it('opens the service deployment drawer from the service list row', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market',
    );

    fireEvent.click(await screen.findByText('Trade Agent'));

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    expect(
      await screen.findByRole('button', { name: '部署候选版本' }),
    ).toBeInTheDocument();
  });

  it('opens the rollout control drawer from the workbench header', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(await screen.findByRole('button', { name: '发布控制' }));

    expect(await screen.findByText('推进发布推进')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '回滚发布推进' }),
    ).toBeInTheDocument();
  });

  it('does not present rollout control as an action when no rollout is active', async () => {
    mockServicesApi.getRollout.mockResolvedValueOnce(null);

    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '发布控制' })).toBeNull();
    expect(
      screen.getByRole('button', { name: '无活动控制' }),
    ).toBeDisabled();
  });

  it('does not present traffic adjustment as an action when no serving targets exist', async () => {
    mockServicesApi.getServingSet.mockResolvedValueOnce({
      activeRolloutId: '',
      generation: 0,
      serviceKey: 'scope-1:trade-agent',
      targets: [],
      updatedAt: '2026-03-30T10:00:00Z',
    });

    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '调整流量' })).toBeNull();
    expect(
      screen.getAllByRole('button', { name: '查看流量状态' })[0],
    ).toBeDisabled();
  });

  it('dispatches the candidate revision from the candidate drawer', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(
      await screen.findByRole('button', { name: '部署候选版本' }),
    );
    fireEvent.click(
      await screen.findByRole('button', { name: '发布候选版本' }),
    );

    await waitFor(() => {
      expect(mockServicesApi.deployRevision).toHaveBeenCalledWith(
        'trade-agent',
        expect.objectContaining({
          revisionId: 'rev-12',
        }),
      );
    });
  });

  it('keeps a release handoff after candidate submission without marking serving observed', async () => {
    mockServicesApi.getServingSet.mockResolvedValueOnce({
      activeRolloutId: 'rollout-1',
      generation: 3,
      serviceKey: 'scope-1:trade-agent',
      targets: [
        {
          allocationWeight: 100,
          deploymentId: 'dep-1',
          enabledEndpointIds: ['chat'],
          primaryActorId: 'actor-1',
          revisionId: 'rev-11',
          servingState: 'active',
        },
      ],
      updatedAt: '2026-03-30T10:00:00Z',
    });
    mockServicesApi.getTraffic.mockResolvedValueOnce({
      activeRolloutId: 'rollout-1',
      endpoints: [
        {
          endpointId: 'chat',
          targets: [
            {
              allocationWeight: 100,
              deploymentId: 'dep-1',
              primaryActorId: 'actor-1',
              revisionId: 'rev-11',
              servingState: 'active',
            },
          ],
        },
      ],
      generation: 3,
      serviceKey: 'scope-1:trade-agent',
      updatedAt: '2026-03-30T10:00:00Z',
    });

    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(
      await screen.findByRole('button', { name: '部署候选版本' }),
    );
    fireEvent.click(
      await screen.findByRole('button', { name: '发布候选版本' }),
    );

    expect(await screen.findByText('候选版本部署已提交')).toBeInTheDocument();
    expect(screen.getByText('已提交，不代表已完成')).toBeInTheDocument();
    expect(
      screen.getByText(
        '这只表示候选版本部署命令已接收，尚未说明候选修订已经被服务态观察到。',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        '3 项证据需要人工核对，避免把旧 ReadModel 当作本次完成。',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText('已观察')).toBeNull();
    expect(screen.getAllByText('需核对').length).toBeGreaterThanOrEqual(3);
    expect(screen.getByText('Serving evidence')).toBeInTheDocument();
    expect(screen.getByText('Traffic evidence')).toBeInTheDocument();
    expect(screen.getByText('候选修订')).toBeInTheDocument();
    expect(screen.getAllByText('rev-12').length).toBeGreaterThan(0);
    expect(screen.queryByText('候选版本已在服务态生效')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: '查看发布推进证据' }));

    expect(
      await screen.findByRole('tab', { name: 'Rollout', selected: true }),
    ).toBeInTheDocument();
  });

  it('shows rollback as a pending baseline evidence handoff', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(await screen.findByRole('button', { name: '发布控制' }));
    fireEvent.click(
      await screen.findByRole('button', { name: '回滚发布推进' }),
    );

    expect(await screen.findByText('发布推进回滚已提交')).toBeInTheDocument();
    expect(screen.getByText('已提交，不代表已完成')).toBeInTheDocument();
    expect(
      screen.getByText(
        '这只表示回滚命令已接收，不代表服务态已经回到基线。',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText('发布推进回滚请求已提交，等待基线证据刷新。'),
    ).toBeInTheDocument();
    expect(screen.getAllByText('待观察').length).toBeGreaterThanOrEqual(3);
    expect(screen.getByText('Traffic split')).toBeInTheDocument();
  });

  it('opens the deployment detail drawer from the catalog table', async () => {
    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(await screen.findByRole('tab', { name: '部署目录' }));
    fireEvent.click(await screen.findByRole('button', { name: '查看详情' }));

    expect(await screen.findByText('部署详情')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: '停用部署' }),
    ).toBeInTheDocument();
  });

  it('does not present deactivate as an action for inactive deployments', async () => {
    mockServicesApi.getDeployments.mockResolvedValueOnce({
      deployments: [
        {
          activatedAt: '2026-03-29T10:05:00Z',
          deploymentId: 'dep-1',
          primaryActorId: 'actor-1',
          revisionId: 'rev-11',
          status: 'inactive',
          updatedAt: '2026-03-30T10:00:00Z',
        },
      ],
      serviceKey: 'scope-1:trade-agent',
      updatedAt: '2026-03-30T10:00:00Z',
    });

    renderDeploymentsPage(
      '/deployments?tenantId=scope-1&appId=trade-app&namespace=cn.market&serviceId=trade-agent&deploymentId=dep-1',
    );

    expect(await screen.findByText('发布摘要')).toBeInTheDocument();
    fireEvent.click(await screen.findByRole('tab', { name: '部署目录' }));
    fireEvent.click(await screen.findByRole('button', { name: '查看详情' }));

    expect(await screen.findByText('部署详情')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: '停用部署' })).toBeNull();
    expect(screen.getByRole('button', { name: '不可停用' })).toBeDisabled();
  });
});
