import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamCreatePage from './new';

describe('TeamCreatePage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/teams/new');
  });

  it('renders the create-team page with the same summary-plus-main-card rhythm as teams home', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Aevatar / Teams')).toBeTruthy();
    expect(screen.getByText('Create Team')).toBeTruthy();
    expect(screen.getByText('入口')).toBeTruthy();
    expect(screen.getByText('构建对象')).toBeTruthy();
    expect(screen.getByText('完成后')).toBeTruthy();
    expect(screen.getByText('新增后端流')).toBeTruthy();
    expect(screen.getByText('Start Building')).toBeTruthy();
    expect(screen.getByRole('heading', { level: 3, name: 'Studio' })).toBeTruthy();
    expect(screen.getByLabelText('团队名称')).toBeTruthy();
    expect(screen.getByLabelText('入口名称')).toBeTruthy();
    expect(
      screen.getAllByRole('button', { name: 'Open Studio' }).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: 'View Behaviors' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to My Teams' })).toBeTruthy();
    expect(screen.queryByText('Team Builder Entry')).toBeNull();
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

  it('opens Studio in create-team mode and carries the entered names into the route', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    const openStudioButtons = await screen.findAllByRole('button', {
      name: 'Open Studio',
    });

    expect(openStudioButtons[0]).toBeDisabled();

    fireEvent.change(screen.getByLabelText('团队名称'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('入口名称'), {
      target: { value: '订单入口' },
    });

    expect(openStudioButtons[0]).toBeEnabled();
    fireEvent.click(openStudioButtons[0]);

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('teamMode')).toBe('create');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBe('订单入口');
    expect(params.get('tab')).toBe('studio');
    expect(params.get('draft')).toBe('new');
  });

  it('shows the saved draft summary and resumes that draft in Studio', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Saved Draft')).toBeTruthy();
    expect(screen.getByText('已保存草稿')).toBeTruthy();
    expect(screen.getByText('order-entry-draft')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Delete Draft' })).toBeDisabled();
    expect(
      screen.getByText(
        'Delete Draft 需要后端删除 workflow 接口，当前前端先不提供假删除。',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Continue Draft' }));

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('teamMode')).toBe('create');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBe('订单入口');
    expect(params.get('teamDraftWorkflowId')).toBe('workflow-7');
    expect(params.get('teamDraftWorkflowName')).toBe('order-entry-draft');
    expect(params.get('workflow')).toBe('workflow-7');
    expect(params.get('draft')).toBeNull();
  });
});
