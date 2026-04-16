import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ActorsPage from './index';

jest.mock('@/shared/api/runtimeActorsApi', () => ({
  runtimeActorsApi: {
    getActorSnapshot: jest.fn(),
    getActorTimeline: jest.fn(),
    getActorGraphEnriched: jest.fn(),
    getActorGraphEdges: jest.fn(),
    getActorGraphSubgraph: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeQueryApi', () => ({
  runtimeQueryApi: {
    listAgents: jest.fn(async () => []),
  },
}));

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: () => {
    const React = require('react');
    return React.createElement('div', null, 'GraphCanvas');
  },
}));

describe('ActorsPage', () => {
  const findActorRow = (needle: string) =>
    screen
      .getAllByRole('row')
      .find((row) => row.textContent?.includes(needle)) ?? null;

  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, '', '/runtime/explorer');
  });

  it('renders the runtime explorer shell and navigation actions', async () => {
    const { container } = renderWithQueryClient(
      React.createElement(ActorsPage),
    );

    expect(container.textContent).toContain('Aevatar / Platform');
    expect(container.textContent).toContain('Topology');
    expect(container.textContent).toContain('Topology 是 Platform 的运行关系追查台，用单个 workflow run actor 为焦点还原 run、step、child actor 和最近事件证据。');
    expect(container.textContent).toContain('追查入口');
    expect(container.textContent).toContain('可追查对象');
    expect(container.textContent).toContain('示例数据');
    expect(screen.getByPlaceholderText('输入 Actor ID')).toBeTruthy();
    expect(screen.getByPlaceholderText('筛选 Actor')).toBeTruthy();
    expect(screen.getByRole('button', { name: '加载追查视图' })).toBeTruthy();
    expect(screen.getByRole('button', { name: '切回真实数据' })).toBeTruthy();
    expect(container.textContent).toContain('CustomerSupport...');
    expect(screen.getAllByRole('button', { name: '查看概览' }).length).toBeGreaterThan(0);
  });

  it('opens a quick preview drawer and navigates to the dedicated detail page', async () => {
    renderWithQueryClient(React.createElement(ActorsPage));

    const plannerRow = findActorRow('acto...nner');

    expect(plannerRow).toBeTruthy();

    fireEvent.click(
      within(plannerRow as HTMLElement).getByRole('button', { name: '查看概览' }),
    );

    expect(await screen.findByText('对象快速概览')).toBeTruthy();
    expect(screen.getAllByText('运行上下文').length).toBeGreaterThan(0);
    expect(screen.getAllByText('最近事件').length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: '进入追查工作台' })).toBeTruthy();
    expect(
      screen.getByText(
        'Classification completed: refund-review. Next action is retrieve-history.',
      ),
    ).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '进入追查工作台' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/runtime/explorer/detail');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('actorId')).toContain('/planner');
    expect(params.get('mode')).toBe('sample');
    expect(params.get('runId')).toBe('run-20260415-213928');
    expect(params.get('scopeId')).toBe('1626c177-917b-4fcc-a5ee-aa74a171b0d6');
    expect(params.get('serviceId')).toBe('draft');
  });

  it('keeps the list page route without a detail actor selection', async () => {
    renderWithQueryClient(React.createElement(ActorsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/runtime/explorer');
    });
    expect(window.location.search).toBe('');
  });
});
