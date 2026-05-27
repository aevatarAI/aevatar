import { fireEvent, screen, waitFor } from '@testing-library/react';
import { message } from 'antd';
import React from 'react';
import {
  clearStoredAuthSession,
  persistAuthSession,
} from '@/shared/auth/session';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamCreatePage from './new';

jest.mock('antd', () => {
  const actual = jest.requireActual('antd');
  return {
    ...actual,
    message: {
      ...actual.message,
      success: jest.fn(),
      info: jest.fn(),
      warning: jest.fn(),
      error: jest.fn(),
      destroy: jest.fn(),
    },
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

  function createDeferredResponse() {
    let resolveResponse!: (response: Response) => void;
    const promise = new Promise<Response>((resolve) => {
      resolveResponse = resolve;
    });

    return {
      promise,
      resolveResponse,
    };
  }

  beforeEach(() => {
    window.history.replaceState({}, '', '/teams/new?scopeId=scope-a');
    window.sessionStorage.clear();
    clearStoredAuthSession();
    jest.clearAllMocks();
    fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        authenticated: true,
        scopeId: 'scope-a',
        scopeSource: 'nyxid',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;
  });

  it('renders the simplified team create page', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Aevatar / 团队')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 2, name: '创建团队' })).toBeTruthy();
    expect(screen.getByText('团队信息')).toBeTruthy();
    expect(screen.getByLabelText('团队名称')).toBeTruthy();
    expect(screen.getByLabelText('团队说明')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: '创建团队' }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('button', { name: '创建团队' })[0]).not.toHaveStyle({
      background: '#6c5ce7',
    });
    expect(screen.getByRole('button', { name: '返回我的团队' })).toBeTruthy();
    expect(screen.queryByText('工作空间上下文')).toBeNull();
    expect(screen.queryByText('StudioTeam')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Continue in Studio' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'View Behaviors' })).toBeNull();
    expect(screen.queryByText('Saved Draft')).toBeNull();
  });

  it('creates a backend StudioTeam and routes to team focus', async () => {
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        authenticated: true,
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

    const { queryClient } = renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.change(await screen.findByLabelText('团队名称'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('团队说明'), {
      target: { value: '处理订单异常' },
    });
    expect(screen.getAllByRole('button', { name: '创建团队' })[0]).toHaveStyle({
      background: '#6c5ce7',
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
      expect(window.location.pathname).toBe('/teams/scope-a/t-alpha');
    });
    expect(
      queryClient.getQueryData(['teams', 'team-summary', 'scope-a', 't-alpha']),
    ).toEqual(teamResponse);
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBeNull();
    expect(params.get('teamId')).toBeNull();
    expect(message.success).toHaveBeenCalledWith('已创建团队。');
  });

  it('keeps the resolved scope when returning to My Teams', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.click(await screen.findByRole('button', { name: '返回我的团队' }));

    expect(window.location.pathname).toBe('/teams');
    expect(new URLSearchParams(window.location.search).get('scopeId')).toBe('scope-a');
  });

  it('locks the create form while the Team create request is pending', async () => {
    const createResponse = createDeferredResponse();
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        authenticated: true,
        scopeId: 'scope-a',
        scopeSource: 'nyxid',
      }),
    } as Response);
    fetchMock.mockReturnValueOnce(createResponse.promise);

    renderWithQueryClient(React.createElement(TeamCreatePage));

    const teamNameInput = await screen.findByLabelText('团队名称');
    const descriptionInput = screen.getByLabelText('团队说明');
    fireEvent.change(teamNameInput, {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(descriptionInput, {
      target: { value: '处理订单异常' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: '创建团队' })[0]);

    await waitFor(() => {
      expect(teamNameInput).toBeDisabled();
    });
    expect(descriptionInput).toBeDisabled();
    expect(screen.getByRole('button', { name: '返回我的团队' })).toBeDisabled();
    expect(window.location.pathname).toBe('/teams/new');

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

    createResponse.resolveResponse({
      ok: true,
      status: 201,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => teamResponse,
    } as Response);

    await waitFor(() => {
      expect(window.location.pathname).toBe('/teams/scope-a/t-alpha');
    });
  });

  it('ignores legacy scopeId=new links and creates under the authenticated scope', async () => {
    window.history.replaceState({}, '', '/teams/new?scopeId=new&teamName=test');
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: false,
        authenticated: true,
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

    expect(await screen.findByLabelText('团队名称')).toHaveValue('test');
    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get('scopeId')).toBe(
        'scope-a',
      );
    });

    fireEvent.change(screen.getByLabelText('团队说明'), {
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

  it('drops legacy draft recovery params from old create links', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get('scopeId')).toBe(
        'scope-a',
      );
    });
    const params = new URLSearchParams(window.location.search);
    expect(window.location.pathname).toBe('/teams/new');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBeNull();
    expect(params.get('teamDraftWorkflowId')).toBeNull();
    expect(params.get('teamDraftWorkflowName')).toBeNull();
    expect(screen.queryByText('Saved Draft')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Continue Draft' })).toBeNull();
  });

  it('does not create a Team from only a locally restored scope when server auth fails', async () => {
    window.history.replaceState({}, '', '/teams/new');
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3600_000,
        refreshToken: 'refresh-token',
      },
      user: {
        sub: 'scope-a',
        name: 'Abigail Deng',
      },
    });
    fetchMock.mockRejectedValueOnce(
      new Error('Error occurred while trying to proxy: localhost:5173/api/auth/me'),
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    const teamNameInput = await screen.findByLabelText('团队名称');
    const descriptionInput = screen.getByLabelText('团队说明');

    expect(await screen.findByText('当前登录态校验失败')).toBeTruthy();
    expect(screen.getByRole('status', { name: '创建团队状态' })).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText('登录状态暂时不可用，请刷新后重试。')).toBeTruthy();
    expect(screen.getByText('需要后端确认当前登录态后才能创建团队。')).toBeTruthy();
    expect(teamNameInput).toBeDisabled();
    expect(descriptionInput).toBeDisabled();
    expect(screen.getAllByRole('button', { name: '创建团队' })[0]).toBeDisabled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not create a Team when auth resolves as unauthenticated', async () => {
    window.history.replaceState({}, '', '/teams/new?scopeId=scope-a');
    fetchMock.mockResolvedValueOnce({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({
        enabled: true,
        authenticated: false,
        loginUrl: '/auth/login?returnUrl=%2F',
        scopeId: null,
        scopeSource: null,
      }),
    } as Response);

    renderWithQueryClient(React.createElement(TeamCreatePage));

    const teamNameInput = await screen.findByLabelText('团队名称');
    const descriptionInput = screen.getByLabelText('团队说明');

    expect(await screen.findByText('当前登录态未生效')).toBeTruthy();
    expect(screen.getByRole('status', { name: '创建团队状态' })).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(
      screen.getByText('后端尚未确认当前浏览器会话，暂不允许创建团队。请重新登录后再试。'),
    ).toBeTruthy();
    expect(screen.getByText('需要后端确认当前登录态后才能创建团队。')).toBeTruthy();
    expect(teamNameInput).toBeDisabled();
    expect(descriptionInput).toBeDisabled();
    expect(screen.getAllByRole('button', { name: '创建团队' })[0]).toBeDisabled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
