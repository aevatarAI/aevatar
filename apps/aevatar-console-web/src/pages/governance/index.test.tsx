import { fireEvent, screen, waitFor } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import GovernanceIndexPage from './index';
import { governanceApi } from '@/shared/api/governanceApi';

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
        endpoints: [],
        policyIds: ['policy-a'],
        activeServingRevisionId: 'rev-2',
        defaultServingRevisionId: 'rev-1',
        deploymentStatus: 'ready',
        deploymentId: 'deploy-1',
        primaryActorId: 'actor://service-alpha',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
    getRevisions: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          endpoints: [],
          createdAt: '2026-03-25T08:00:00Z',
          preparedAt: '2026-03-25T08:05:00Z',
          publishedAt: '2026-03-25T08:10:00Z',
          retiredAt: null,
        },
      ],
    })),
  },
}));

jest.mock('@/shared/api/governanceApi', () => ({
  governanceApi: {
    getBindings: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      bindings: [],
    })),
    getActivationCapability: jest.fn(async () => ({
      identity: {
        tenantId: 'tenant-a',
        appId: 'app-a',
        namespace: 'default',
        serviceId: 'service-alpha',
      },
      revisionId: 'rev-2',
      missingPolicyIds: [],
      bindings: [],
      policies: [],
      endpoints: [],
    })),
    getEndpointCatalog: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      endpoints: [
        {
          description: 'Invoke command',
          displayName: 'Invoke',
          endpointId: 'invoke',
          exposureKind: 'internal',
          kind: 'command',
          policyIds: [],
          requestTypeUrl: 'type.googleapis.com/demo.Invoke',
          responseTypeUrl: '',
        },
      ],
    })),
    getPolicies: jest.fn(async () => ({
      serviceKey: 'tenant-a/app-a/default/service-alpha',
      updatedAt: '2026-03-25T10:00:00Z',
      policies: [
        {
          activationRequiredBindingIds: [],
          displayName: 'Retired Policy',
          invokeAllowedCallerServiceKeys: [],
          invokeRequiresActiveDeployment: false,
          policyId: 'policy-retired',
          retired: true,
        },
      ],
    })),
    updateEndpointCatalog: jest.fn(async () => ({
      commandId: 'command-1',
      correlationId: 'correlation-1',
      targetActorId: 'actor://governance',
    })),
  },
}));

describe('GovernanceIndexPage', () => {
  beforeEach(() => {
    setLocale('zh-CN', false);
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=activation',
    );
  });

  it('renders the platform governance product framing', async () => {
    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('Platform')).toBeTruthy();
    expect(screen.getAllByText('Governance').length).toBeGreaterThan(0);
  });

  it('hands off a governed service to Deployments with service and deployment focus', async () => {
    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    fireEvent.click(await screen.findByRole('button', { name: '打开部署' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/deployments');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('tenantId')).toBe('tenant-a');
    expect(params.get('appId')).toBe('app-a');
    expect(params.get('namespace')).toBe('default');
    expect(params.get('serviceId')).toBe('service-alpha');
    expect(params.get('deploymentId')).toBe('deploy-1');
  });

  it('keeps create actions while showing a secondary deployments handoff in catalog views', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=bindings',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByRole('button', { name: '新建绑定' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '打开部署' })).toBeTruthy();
  });

  it.each([
    ['policies', 'getPolicies', '新建策略', '正在加载策略...'],
    ['bindings', 'getBindings', '新建绑定', '正在加载绑定...'],
    ['endpoints', 'getEndpointCatalog', '新建入口', '正在加载入口目录...'],
  ])(
    'renders a table skeleton while the %s catalog is loading',
    async (view, queryName, actionName, loadingLabel) => {
      (
        (governanceApi as unknown as Record<string, jest.Mock>)[queryName]
      ).mockImplementationOnce(
        () => new Promise(() => {}),
      );
      window.history.replaceState(
        {},
        '',
        `/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=${view}`,
      );

      renderWithQueryClient(React.createElement(GovernanceIndexPage));

      expect(
        await screen.findByRole('button', { name: actionName }),
      ).toBeInTheDocument();
      expect(await screen.findByRole('status')).toHaveAttribute(
        'data-variant',
        'table',
      );
      expect(screen.getByText(loadingLabel)).toHaveClass(
        'aevatar-loading-visually-hidden',
      );
    },
  );

  it('does not auto-select the first service when service context is missing', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('选择一个服务')).toBeTruthy();
    expect(screen.getByText('当前 Scope tenant-a / app-a / default')).toBeTruthy();
    expect(screen.getByRole('button', { name: '加载治理工作台' })).toBeDisabled();
  });

  it('hides write actions when no service is selected', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&view=bindings',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('选择一个服务')).toBeTruthy();
    expect(screen.queryByRole('button', { name: '新建绑定' })).toBeNull();
  });

  it('labels retired policy rows as view-only entries', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=policies',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    expect(await screen.findByText('Retired Policy')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '查看' })).toBeInTheDocument();
  });

  it('keeps endpoint update receipts separate from observed catalog refresh', async () => {
    window.history.replaceState(
      {},
      '',
      '/governance?tenantId=tenant-a&appId=app-a&namespace=default&serviceId=service-alpha&view=endpoints',
    );

    renderWithQueryClient(React.createElement(GovernanceIndexPage));

    fireEvent.click(await screen.findByRole('button', { name: '配置' }));
    fireEvent.click(await screen.findByRole('button', { name: '保存入口' }));

    await waitFor(() => {
      expect(governanceApi.updateEndpointCatalog).toHaveBeenCalled();
    });
    expect(await screen.findByText('治理命令已接收')).toBeInTheDocument();
    expect(screen.getByText('Endpoint was accepted for update.')).toBeInTheDocument();
    expect(screen.getByText(/Endpoint catalog/)).toBeInTheDocument();
    expect(screen.queryByText('已观察')).toBeNull();
  });
});
