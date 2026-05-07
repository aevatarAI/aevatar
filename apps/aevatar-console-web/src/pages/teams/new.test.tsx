import { fireEvent, screen, waitFor } from '@testing-library/react';
import { message } from 'antd';
import React from 'react';
import { studioApi } from '@/shared/studio/api';
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
    createdAt: '2026-05-06T08:00:00Z',
    updatedAt: '2026-05-06T08:00:00Z',
  };
  let fetchMock: jest.Mock;

  beforeEach(() => {
    window.history.replaceState({}, '', '/teams/new?scopeId=scope-a');
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

  it('renders the real Team API create page with saved draft recovery kept secondary', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Aevatar / Teams')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 2, name: 'Create Team' })).toBeTruthy();
    expect(screen.getByText('数据源')).toBeTruthy();
    expect(screen.getByText('StudioTeam')).toBeTruthy();
    expect(screen.getByText('Scope context')).toBeTruthy();
    expect(screen.getByText('Team authority')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 3, name: 'Create real Team roster entry' })).toBeTruthy();
    expect(screen.getByLabelText('Team name')).toBeTruthy();
    expect(screen.getByLabelText('Team description')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: 'Create Team' }).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'View Behaviors' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to My Teams' })).toBeTruthy();
    expect(
      screen.getByText(
        'This page now creates a backend StudioTeam record. Members can be assigned later; the Teams homepage will use this roster entry as the primary team truth.',
      ),
    ).toBeTruthy();
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

    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.change(await screen.findByLabelText('Team name'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('Team description'), {
      target: { value: '处理订单异常' },
    });
    fireEvent.click(screen.getAllByRole('button', { name: 'Create Team' })[0]);

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
      expect(window.location.pathname).toBe('/teams/scope-a');
    });
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
    expect(params.get('teamId')).toBe('t-alpha');
    expect(message.success).toHaveBeenCalledWith('已创建 Team。');
  });

  it('opens Studio with only scope context from the secondary action', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    const openStudioButtons = await screen.findAllByRole('button', {
      name: 'Continue in Studio',
    });

    expect(openStudioButtons[0]).toBeEnabled();

    fireEvent.change(screen.getByLabelText('Team name'), {
      target: { value: '订单助手团队' },
    });

    fireEvent.click(openStudioButtons[0]);

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
    expect(params.get('tab')).toBe('studio');
    expect(params.get('focus')).toBeNull();
    expect(params.get('teamMode')).toBeNull();
    expect(params.get('teamName')).toBeNull();
    expect(params.get('entryName')).toBeNull();
    expect(params.get('draft')).toBeNull();
  });

  it('shows the saved draft summary and resumes that draft in Studio without legacy draft route params', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Saved Draft')).toBeTruthy();
    expect(screen.getByText('已保存草稿')).toBeTruthy();
    expect(screen.getByText('order-entry-draft')).toBeTruthy();
    expect(
      screen.getByText(
        'Delete Draft removes the linked workflow draft. Legacy labels stay in the URL so old links remain understandable.',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue Draft' }));

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('tab')).toBe('studio');
    expect(params.get('focus')).toBe('workflow:workflow-7');
    expect(params.get('teamMode')).toBeNull();
    expect(params.get('teamName')).toBeNull();
    expect(params.get('entryName')).toBeNull();
    expect(params.get('teamDraftWorkflowId')).toBeNull();
    expect(params.get('teamDraftWorkflowName')).toBeNull();
    expect(params.get('workflow')).toBeNull();
    expect(params.get('draft')).toBeNull();
  });

  it('deletes the saved draft and keeps the team form values in place', async () => {
    const deleteWorkflowSpy = jest
      .spyOn(studioApi, 'deleteWorkflow')
      .mockResolvedValue(undefined);

    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.click(await screen.findByRole('button', { name: 'Delete Draft' }));

    await waitFor(() => {
      expect(deleteWorkflowSpy).toHaveBeenCalledWith('workflow-7');
    });

    await waitFor(() => {
      expect(screen.queryByText('Saved Draft')).toBeNull();
    });

    const params = new URLSearchParams(window.location.search);
    expect(window.location.pathname).toBe('/teams/new');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBe('订单入口');
    expect(params.get('teamDraftWorkflowId')).toBeNull();
    expect(params.get('teamDraftWorkflowName')).toBeNull();
    expect(message.success).toHaveBeenCalledWith('已删除当前团队草稿。');
  });
});
