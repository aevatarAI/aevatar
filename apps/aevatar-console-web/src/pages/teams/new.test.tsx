import { screen } from '@testing-library/react';
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
    expect(screen.getByText('Team Builder')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Team Builder' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to My Teams' })).toBeTruthy();
    expect(screen.queryByText('Start Building')).toBeNull();
    expect(screen.queryByRole('button', { name: 'View Behaviors' })).toBeNull();
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
});
