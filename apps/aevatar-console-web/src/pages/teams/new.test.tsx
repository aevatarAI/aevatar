import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import {
  saveTeamCreateDraftPointer,
} from '@/shared/navigation/teamCreateDraftPointer';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamCreatePage from './new';

describe('TeamCreatePage', () => {
  const clickModeCard = (label: string) => {
    const button = screen.getByText(label).closest('button');
    expect(button).toBeTruthy();
    fireEvent.click(button!);
  };

  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, '', '/teams/new');
  });

  it('renders the focused new-team flow when there is no saved draft', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText('Aevatar / Teams')).toBeTruthy();
    expect(screen.getByText('Create Team')).toBeTruthy();
    expect(screen.getByText('Choose a Path')).toBeTruthy();
    expect(
      screen.getByRole('heading', { level: 3, name: 'Create a New Team' }),
    ).toBeTruthy();
    expect(screen.getByLabelText('团队名称')).toBeTruthy();
    expect(screen.getByLabelText('入口名称')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Create in Studio' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'View Workflows' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to My Teams' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Open Studio' })).toBeNull();
    expect(screen.queryByText('Saved Draft')).toBeNull();
    expect(screen.queryByText('Create New Team')).toBeNull();
    expect(screen.queryByText('Resume Saved Draft')).toBeNull();
  });

  it('opens Studio in create-team mode and carries the entered names into the route', async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    const createButton = await screen.findByRole('button', {
      name: 'Create in Studio',
    });

    expect(createButton).toBeDisabled();

    fireEvent.change(screen.getByLabelText('团队名称'), {
      target: { value: '订单助手团队' },
    });
    fireEvent.change(screen.getByLabelText('入口名称'), {
      target: { value: '订单入口' },
    });

    expect(createButton).toBeEnabled();
    fireEvent.click(createButton);

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('teamMode')).toBe('create');
    expect(params.get('teamName')).toBe('订单助手团队');
    expect(params.get('entryName')).toBe('订单入口');
    expect(params.get('tab')).toBe('studio');
    expect(params.get('draft')).toBe('new');
  });

  it('keeps the current scope context when opening Studio', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.change(await screen.findByLabelText('团队名称'), {
      target: { value: '订单助手团队' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Create in Studio' }));

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
    expect(params.get('scopeLabel')).toBe('团队 A');
  });

  it('keeps the current scope context when navigating back to Teams', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A',
    );
    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.click(screen.getByRole('button', { name: 'Back to My Teams' }));

    expect(window.location.pathname).toBe('/teams');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('scopeId')).toBe('scope-a');
  });

  it('requires choosing between a new team and a saved draft when a draft already exists', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByRole('heading', {
        level: 3,
        name: 'Choose What You Want To Do',
      }),
    ).toBeTruthy();
    expect(screen.getByText('Create New Team')).toBeTruthy();
    expect(screen.getByText('Resume Saved Draft')).toBeTruthy();
    expect(screen.getByText('订单助手团队')).toBeTruthy();
    expect(screen.getByText('订单入口')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Choose a Path First' })).toBeDisabled();
    expect(screen.queryByLabelText('团队名称')).toBeNull();
    expect(screen.queryByText('Saved Draft')).toBeNull();
  });

  it('restores the saved draft choice from local storage even when the route is blank', async () => {
    saveTeamCreateDraftPointer({
      teamName: '订单助手团队',
      entryName: '订单入口',
      teamDraftWorkflowId: 'workflow-7',
      teamDraftWorkflowName: 'order-entry-draft',
      sourceBehaviorDefinitionId: 'workflow-hello-chat',
      sourceBehaviorDefinitionName: 'hello-chat',
    });

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByRole('heading', {
        level: 3,
        name: 'Choose What You Want To Do',
      }),
    ).toBeTruthy();
    expect(screen.getByText('Resume Saved Draft')).toBeTruthy();
    expect(screen.getByText('订单助手团队')).toBeTruthy();
    expect(screen.getByText('订单入口')).toBeTruthy();
    expect(screen.queryByText('hello-chat')).toBeNull();
  });

  it('shows multiple saved drafts and lets the user choose which one to resume', async () => {
    saveTeamCreateDraftPointer({
      teamName: '测试',
      entryName: '测试',
      teamDraftWorkflowId: 'joker',
      teamDraftWorkflowName: 'joker',
    });
    saveTeamCreateDraftPointer({
      teamName: '订单助手',
      entryName: '订单助手',
      teamDraftWorkflowId: 'test03',
      teamDraftWorkflowName: 'test03',
    });

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Resume Saved Draft');
    clickModeCard('Resume Saved Draft');

    expect(screen.getByText('Saved Drafts')).toBeTruthy();
    expect(screen.getAllByText('测试').length).toBeGreaterThan(0);
    expect(screen.getAllByText('订单助手').length).toBeGreaterThan(0);

    fireEvent.click(screen.getAllByText('测试')[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Continue Draft' }));

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('workflow')).toBe('joker');
    expect(params.get('teamDraftWorkflowId')).toBe('joker');
    expect(params.get('teamName')).toBe('测试');
    expect(params.get('entryName')).toBe('测试');
  });

  it('shows only saved drafts that belong to the current scope', async () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-a',
      teamName: '范围 A',
      entryName: '范围 A',
      teamDraftWorkflowId: 'scope-a-draft',
      teamDraftWorkflowName: 'scope-a-draft',
    });
    saveTeamCreateDraftPointer({
      scopeId: 'scope-b',
      teamName: '范围 B',
      entryName: '范围 B',
      teamDraftWorkflowId: 'scope-b-draft',
      teamDraftWorkflowName: 'scope-b-draft',
    });
    window.history.replaceState(
      {},
      '',
      '/teams/new?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Resume Saved Draft');
    clickModeCard('Resume Saved Draft');

    expect(screen.getAllByText('范围 A').length).toBeGreaterThan(0);
    expect(screen.queryByText('范围 B')).toBeNull();
  });

  it('explains when saved drafts exist in other scopes but not in the current one', async () => {
    saveTeamCreateDraftPointer({
      scopeId: 'scope-b',
      teamName: '范围 B',
      entryName: '范围 B',
      teamDraftWorkflowId: 'scope-b-draft',
      teamDraftWorkflowName: 'scope-b-draft',
    });
    window.history.replaceState(
      {},
      '',
      '/teams/new?scopeId=scope-a&scopeLabel=%E5%9B%A2%E9%98%9F+A',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByRole('heading', { level: 3, name: 'Create a New Team' }),
    ).toBeTruthy();
    expect(screen.getByText('当前 Scope 下没有 saved draft')).toBeTruthy();
    expect(
      screen.getByText('另有 1 份草稿属于其他 Scope，因此这里不会显示。切回对应 Scope 后再恢复它们。'),
    ).toBeTruthy();
    expect(screen.queryByText('Resume Saved Draft')).toBeNull();
  });

  it('keeps saved draft summary separate from the current new-team form values', async () => {
    saveTeamCreateDraftPointer({
      teamName: '订单助手团队',
      entryName: '已保存入口',
      teamDraftWorkflowId: 'workflow-7',
      teamDraftWorkflowName: 'order-entry-draft',
    });
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E5%85%A8%E6%96%B0%E5%9B%A2%E9%98%9F&entryName=%E4%B8%B4%E6%97%B6%E5%85%A5%E5%8F%A3',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Create New Team');
    expect(screen.getByText('订单助手团队')).toBeTruthy();
    expect(screen.getByText('已保存入口')).toBeTruthy();

    clickModeCard('Create New Team');

    expect(screen.getByDisplayValue('全新团队')).toBeTruthy();
    expect(screen.getByDisplayValue('临时入口')).toBeTruthy();
  });

  it('can start a fresh team flow without reusing the saved draft', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Create New Team');
    clickModeCard('Create New Team');

    expect(screen.getByLabelText('团队名称')).toBeTruthy();
    expect(screen.getByLabelText('入口名称')).toBeTruthy();
    expect(
      screen.getByText(/saved draft 不会自动绑定到这次新建流程/),
    ).toBeTruthy();

    fireEvent.change(screen.getByLabelText('团队名称'), {
      target: { value: '全新团队' },
    });
    fireEvent.change(screen.getByLabelText('入口名称'), {
      target: { value: '全新入口' },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Create in Studio' }));

    expect(window.location.pathname).toBe('/studio');
    const params = new URLSearchParams(window.location.search);
    expect(params.get('teamMode')).toBe('create');
    expect(params.get('teamName')).toBe('全新团队');
    expect(params.get('entryName')).toBe('全新入口');
    expect(params.get('workflow')).toBeNull();
    expect(params.get('teamDraftWorkflowId')).toBeNull();
    expect(params.get('teamDraftWorkflowName')).toBeNull();
    expect(params.get('draft')).toBe('new');
  });

  it('shows the saved draft summary and resumes that draft in Studio', async () => {
    window.history.replaceState(
      {},
      '',
      '/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft',
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Resume Saved Draft');
    clickModeCard('Resume Saved Draft');

    expect(screen.getByText('Saved Draft')).toBeTruthy();
    expect(screen.getAllByText('订单助手团队').length).toBeGreaterThan(0);
    expect(screen.getAllByText('订单入口').length).toBeGreaterThan(0);
    expect(screen.queryByText('order-entry-draft')).toBeNull();
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

  it('keeps source behavior metadata hidden on Create Team while preserving it for resume', async () => {
    saveTeamCreateDraftPointer({
      teamName: '订单测试',
      entryName: '订单测试',
      teamDraftWorkflowId: 'team-draft-1',
      teamDraftWorkflowName: '订单测试',
      sourceBehaviorDefinitionId: 'workflow-hello-chat',
      sourceBehaviorDefinitionName: 'hello-chat',
    });

    renderWithQueryClient(React.createElement(TeamCreatePage));

    await screen.findByText('Resume Saved Draft');
    clickModeCard('Resume Saved Draft');

    expect(screen.getAllByText('订单测试').length).toBeGreaterThan(0);
    expect(screen.queryByText('来源 workflow')).toBeNull();
    expect(screen.queryByText('hello-chat')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Continue Draft' }));

    const params = new URLSearchParams(window.location.search);
    expect(params.get('sourceBehaviorDefinitionId')).toBe('workflow-hello-chat');
    expect(params.get('sourceBehaviorDefinitionName')).toBe('hello-chat');
  });
});
