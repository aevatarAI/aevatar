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
  beforeEach(() => {
    window.history.replaceState({}, '', '/teams/new');
    jest.clearAllMocks();
  });

  it('renders the hidden compatibility page for saved draft recovery', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Aevatar / Teams')).toBeTruthy();
    expect(screen.getByText('Saved Draft Recovery')).toBeTruthy();
    expect(screen.getByText('用途')).toBeTruthy();
    expect(screen.getByText('恢复对象')).toBeTruthy();
    expect(screen.getByText('继续位置')).toBeTruthy();
    expect(screen.getByText('新增后端事实')).toBeTruthy();
    expect(screen.getByText('Continue initial member draft')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 3, name: 'Saved draft recovery' })).toBeTruthy();
    expect(screen.getByLabelText('Legacy team label')).toBeTruthy();
    expect(screen.getByLabelText('Initial member label')).toBeTruthy();
    expect(
      screen.getAllByRole('button', { name: 'Continue in Studio' }).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'View Behaviors' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to My Teams' })).toBeTruthy();
    expect(
      screen.getByText(
        'This compatibility page preserves old Create Team links and saved draft recovery. New team creation now starts in Studio by creating the first member.',
      ),
    ).toBeTruthy();
    expect(screen.queryByText('Team Builder Entry')).toBeNull();
    expect(screen.queryByText('Start Building')).toBeNull();
    expect(screen.queryByText('Open Studio')).toBeNull();
    expect(
      screen.queryByText(
        '当前实现不会新增一套独立后端流程，而是复用现有 Studio 工作区先组建团队，再进入 Team Details 查看拓扑和事件流。',
      ),
    ).toBeNull();
    expect(screen.queryByText('Next Steps')).toBeNull();
    expect(screen.queryByText('Builder 模式')).toBeNull();
    expect(screen.queryByText('默认入口')).toBeNull();
    expect(screen.queryByText('后续页')).toBeNull();
    expect(screen.queryByText('数据源')).toBeNull();
  });

  it('opens Studio in member-first build mode without persisting create-team draft params', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    const openStudioButtons = await screen.findAllByRole('button', {
      name: 'Continue in Studio',
    });

    expect(openStudioButtons[0]).toBeDisabled();

    fireEvent.change(screen.getByLabelText('Legacy team label'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('Initial member label'), {
      target: { value: '订单入口' },
    });

    expect(openStudioButtons[0]).toBeEnabled();
    fireEvent.click(openStudioButtons[0]);

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('tab')).toBe('studio');
    expect(params.get('member')).toBeNull();
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
    expect(params.get('focus')).toBeNull();
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
