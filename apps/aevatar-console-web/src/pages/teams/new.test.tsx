import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamCreatePage from './new';

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

describe('TeamCreatePage', () => {
  const teamResponse = {
    teamId: 't-alpha',
    scopeId: 'scope-a',
    displayName: '订单助手团队',
    description: '处理订单异常',
    lifecycleStage: 'active',
    memberCount: 0,
    entryMemberId: null,
    createdAt: '2026-05-06T08:00:00Z',
    updatedAt: '2026-05-06T08:00:00Z',
  };
  let fetchMock: jest.Mock;

  beforeEach(() => {
    setLocale('zh-CN', false);
    window.history.replaceState({}, '', '/scopes/scope-a/teams/new');
    jest.clearAllMocks();
    fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        scopeId: 'scope-a',
        scopeSource: 'nyxid',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;
  });

  afterEach(() => {
    setLocale('en-US', false);
  });

  it('renders the simplified Team create page', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    const breadcrumb = await screen.findByRole('navigation', {
      name: '面包屑',
    });
    expect(screen.getAllByRole('navigation')).toHaveLength(1);
    expect(breadcrumb).toHaveTextContent('团队');
    expect(breadcrumb).toHaveTextContent('创建团队');
    const teamsBreadcrumbLink = within(breadcrumb).getByRole('link', {
      name: '团队',
    });
    expect(teamsBreadcrumbLink).toHaveAttribute(
      'href',
      '/scopes/scope-a/teams',
    );
    expect(
      screen.getByRole('heading', { level: 2, name: '创建团队' }),
    ).toBeTruthy();
    expect(screen.getByText('团队信息')).toBeTruthy();
    expect(screen.getByLabelText('队名')).toBeTruthy();
    expect(screen.getByLabelText('团队描述')).toBeTruthy();
    expect(
      screen.getAllByRole('button', { name: '创建团队' }).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: '返回我的团队' })).toBeTruthy();
    expect(screen.queryByText('工作空间上下文')).toBeNull();
    expect(screen.queryByText('StudioTeam')).toBeNull();
    expect(
      screen.queryByRole('button', { name: '继续在 Studio 中编辑' }),
    ).toBeNull();
    expect(screen.queryByRole('button', { name: '查看 Behaviors' })).toBeNull();
    expect(screen.queryByText('已保存草稿')).toBeNull();

    fireEvent.click(teamsBreadcrumbLink);
    expect(window.location.pathname).toBe('/scopes/scope-a/teams');
    expect(window.location.search).toBe('');
  });

  it('creates a backend StudioTeam and routes to team focus', async () => {
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        scopeId: 'scope-a',
        scopeSource: 'nyxid',
      }),
    } as Response);
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 201,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => teamResponse,
    } as Response);

    const { queryClient } = renderWithQueryClient(
      React.createElement(TeamCreatePage),
    );

    fireEvent.change(await screen.findByLabelText('队名'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('团队描述'), {
      target: { value: '处理订单异常' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: '创建团队' })[0]);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/scopes/scope-a/teams',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({
            displayName: '订单助手团队',
            description: '处理订单异常',
          }),
        }),
      );
    });

    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/scope-a/teams/t-alpha');
    });
    expect(
      queryClient.getQueryData(['teams', 'team-summary', 'scope-a', 't-alpha']),
    ).toEqual(teamResponse);
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBeNull();
    expect(params.get('teamId')).toBeNull();
    expect(mockConsoleToast.success).toHaveBeenCalledWith('已创建团队。');
  });

  it('uses a localized generic error toast without exposing server details when creation fails', async () => {
    fetchMock.mockReset();
    fetchMock
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({
          enabled: false,
          scopeId: 'scope-a',
          scopeSource: 'nyxid',
        }),
      } as Response)
      .mockRejectedValueOnce(
        new Error('backend request correlation: hidden-detail'),
      );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.change(await screen.findByLabelText('队名'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: '创建团队' })[0]);

    await waitFor(() => {
      expect(mockConsoleToast.error).toHaveBeenCalledWith('创建团队失败。');
    });
    expect(mockConsoleToast.error).not.toHaveBeenCalledWith(
      'backend request correlation: hidden-detail',
    );
    expect(window.location.pathname).toBe('/scopes/scope-a/teams/new');
  });

  it('ignores scopeId=new query hints on scoped create links and creates under the route scope', async () => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-a/teams/new?scopeId=new&teamName=test',
    );
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        scopeId: 'scope-a',
        scopeSource: 'nyxid',
      }),
    } as Response);
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 201,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        ...teamResponse,
        displayName: 'test',
        description: 'test',
      }),
    } as Response);

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByLabelText('队名')).toHaveValue('test');
    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/scope-a/teams/new');
      expect(
        new URLSearchParams(window.location.search).get('scopeId'),
      ).toBeNull();
    });

    fireEvent.change(screen.getByLabelText('团队描述'), {
      target: { value: 'test' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: '创建团队' })[0]);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/scopes/scope-a/teams',
        expect.objectContaining({
          method: 'POST',
          body: JSON.stringify({
            displayName: 'test',
            description: 'test',
          }),
        }),
      );
    });
    expect(fetchMock).not.toHaveBeenCalledWith(
      '/api/scopes/new/teams',
      expect.anything(),
    );
  });

  it('drops stale draft recovery params from scoped create links', async () => {
    window.history.replaceState(
      {},
      '',
      '/scopes/scope-a/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/scope-a/teams/new');
      expect(
        new URLSearchParams(window.location.search).get('scopeId'),
      ).toBeNull();
    });
    const params = new URLSearchParams(window.location.search);
    expect(window.location.pathname).toBe('/scopes/scope-a/teams/new');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBeNull();
    expect(params.get('teamDraftWorkflowId')).toBeNull();
    expect(params.get('teamDraftWorkflowName')).toBeNull();
    expect(screen.queryByText('已保存草稿')).toBeNull();
    expect(screen.queryByRole('button', { name: '继续草稿' })).toBeNull();
  });
});
