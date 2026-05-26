import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ServicesPage from './index';

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    listServices: jest.fn(async () => [
      {
        serviceKey: 'tenant-a/app-a/default/service-alpha',
        serviceId: 'service-alpha',
        displayName: 'Service Alpha',
        tenantId: 'tenant-a',
        appId: 'app-a',
        namespace: 'default',
        endpoints: [{ endpointId: 'chat' }],
        policyIds: ['policy-a', 'policy-b'],
        activeServingRevisionId: 'rev-2',
        defaultServingRevisionId: 'rev-1',
        deploymentStatus: 'ready',
        deploymentId: 'deploy-1',
        primaryActorId: 'actor-1',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
    getDeployments: jest.fn(async () => ({
      deployments: [
        {
          activatedAt: '2026-03-25T09:00:00Z',
          deploymentId: 'deploy-1',
          primaryActorId: 'actor-1',
          revisionId: 'rev-2',
          status: 'ready',
          updatedAt: '2026-03-25T10:00:00Z',
        },
      ],
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getRevisions: jest.fn(async () => ({
      revisions: [
        {
          artifactHash: 'hash-2',
          createdAt: '2026-03-25T08:00:00Z',
          endpoints: [],
          failureReason: '',
          implementationKind: 'workflow',
          preparedAt: '2026-03-25T08:05:00Z',
          publishedAt: '2026-03-25T08:10:00Z',
          retiredAt: null,
          revisionId: 'rev-2',
          status: 'Published',
        },
      ],
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getService: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      serviceId: 'service-alpha',
      displayName: 'Service Alpha',
      tenantId: 'tenant-a',
      appId: 'app-a',
      namespace: 'default',
      endpoints: [
        {
          description: '',
          displayName: 'Chat',
          endpointId: 'chat',
          kind: 'endpoint',
          requestTypeUrl: 'aevatar.services.ChatRequest',
          responseTypeUrl: 'aevatar.services.ChatReply',
        },
      ],
      policyIds: ['policy-a', 'policy-b'],
      activeServingRevisionId: 'rev-2',
      defaultServingRevisionId: 'rev-1',
      deploymentStatus: 'ready',
      deploymentId: 'deploy-1',
      primaryActorId: 'actor-1',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
    getTraffic: jest.fn(async () => ({
      activeRolloutId: '',
      endpoints: [
        {
          endpointId: 'chat',
          targets: [
            {
              allocationWeight: 100,
              deploymentId: 'deploy-1',
              primaryActorId: 'actor-1',
              revisionId: 'rev-2',
              servingState: 'active',
            },
          ],
        },
      ],
      generation: 1,
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
    })),
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

describe('ServicesPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/services');
  });

  it('renders the reframed services authority workbench with inventory and empty detail state', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    expect(await screen.findByText('Aevatar / Platform')).toBeTruthy();
    expect(screen.getAllByText('Services').length).toBeGreaterThan(0);
    expect(await screen.findByText('可见服务')).toBeTruthy();
    expect((await screen.findAllByText('已挂 Serving')).length).toBeGreaterThan(0);
    expect(await screen.findByText('缺主 Actor')).toBeTruthy();
    expect(await screen.findByText('无公开入口')).toBeTruthy();
    expect(await screen.findByText('查找服务')).toBeTruthy();
    expect(await screen.findByText('Service Alpha')).toBeTruthy();
    expect(screen.getByText('Services 是 Platform 的权威服务目录，回答当前范围内有什么服务、它当前挂到哪、由谁承载，并指引你继续进入 Governance、Deployments 或 Topology。')).toBeTruthy();
    expect(screen.getByText('服务目录')).toBeTruthy();
    expect(screen.getByText('按行扫描状态、部署和入口，点击行或按钮在抽屉里查看详情。')).toBeTruthy();
    expect(screen.getByText('状态')).toBeTruthy();
    expect(screen.getByText('身份')).toBeTruthy();
    expect(screen.getByRole('button', { name: '查看详情' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '打开治理' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '筛选服务' })).toBeTruthy();
    expect(screen.queryByText('对象摘要')).toBeNull();
  });

  it('renders authority detail in drawer after selecting a service', async () => {
    renderWithQueryClient(React.createElement(ServicesPage));

    fireEvent.click(await screen.findByRole('button', { name: '查看详情' }));

    expect(await screen.findByText('对象摘要')).toBeTruthy();
    expect(await screen.findByText('服务工作区')).toBeTruthy();
    expect(screen.getByRole('button', { name: '打开 Governance' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '打开 Deployments' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '打开 Topology' })).toBeTruthy();
    expect(screen.getAllByText('tenant-a/app-a/default/service-alpha').length).toBeGreaterThan(0);
    expect(screen.getByText('当前 serving 版本')).toBeTruthy();
    expect(screen.getByText('权威对象')).toBeTruthy();
    expect(screen.getByRole('tab', { name: '入口' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: '版本与部署' })).toBeTruthy();
    expect(screen.getAllByText('1 个入口').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('tab', { name: '版本与部署' }));

    expect(await screen.findByText('最新版本')).toBeTruthy();
    expect(screen.getByText('流量入口')).toBeTruthy();
  });
});
