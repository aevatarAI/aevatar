import { fireEvent, screen, waitFor } from '@testing-library/react';
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
    expect(container.textContent).toContain('Topology 是 Platform 的专家工具，用于按 Actor、run、service 追查真实运行态。它通常从 Teams 或 Services 深链进入，用来解释究竟是谁在处理请求。');
    expect(container.textContent).toContain('定位 Actor');
    expect(container.textContent).toContain('可见 Actor');
    expect(container.textContent).toContain('可见 Actor');
    expect(container.textContent).toContain('当前焦点');
    expect(screen.getByPlaceholderText('输入 Actor ID')).toBeTruthy();
    expect(screen.getByPlaceholderText('筛选 Actor')).toBeTruthy();
    expect(screen.getByRole('button', { name: /清\s*空/ })).toBeTruthy();
    expect(container.textContent).toContain('当前范围没有 Actor');
    expect(container.textContent).not.toContain('Actor Focus');
    expect(container.textContent).not.toContain('Observed Actors');
  });

  it('opens the actor inspector when an actor ID is provided', async () => {
    const { runtimeActorsApi } = jest.requireMock('@/shared/api/runtimeActorsApi') as {
      runtimeActorsApi: {
        getActorGraphEnriched: jest.Mock;
        getActorSnapshot: jest.Mock;
        getActorTimeline: jest.Mock;
      };
    };

    runtimeActorsApi.getActorSnapshot.mockResolvedValue({
      actorId: 'actor://selected',
      completedSteps: 4,
      completionStatusValue: 100,
      lastOutput: 'Completed successfully.',
      lastUpdatedAt: '2026-03-26T00:00:00Z',
      roleReplyCount: 2,
      stateVersion: 7,
      workflowName: 'SupportWorkflow',
    });
    runtimeActorsApi.getActorTimeline.mockResolvedValue([
      {
        eventType: 'StepStarted',
        message: 'Step started',
        stage: 'workflow.started',
        stepId: 'step-1',
        stepType: 'chat',
        timestamp: '2026-03-26T00:00:01Z',
      },
    ]);
    runtimeActorsApi.getActorGraphEnriched.mockResolvedValue({
      subgraph: {
        edges: [],
        nodes: [
          {
            nodeId: 'actor://selected',
            nodeType: 'WorkflowAgent',
          },
        ],
      },
    });

    renderWithQueryClient(React.createElement(ActorsPage));

    fireEvent.change(screen.getByPlaceholderText('输入 Actor ID'), {
      target: { value: 'actor://selected' },
    });

    await waitFor(() => {
      expect(runtimeActorsApi.getActorSnapshot).toHaveBeenCalledWith(
        'actor://selected',
      );
    });
    expect(await screen.findByText('运行摘要')).toBeTruthy();
    expect(screen.getByText('事件时间线')).toBeTruthy();
    expect(screen.getAllByText('Topology').length).toBeGreaterThan(0);
    expect(screen.getAllByText('SupportWorkflow').length).toBeGreaterThan(0);
    expect(screen.getByText('最近输出')).toBeTruthy();
    expect(screen.getByText('Completed successfully.')).toBeTruthy();
  });

  it('preserves playback explorer context from the incoming route', async () => {
    window.history.replaceState(
      {},
      '',
      '/runtime/explorer?actorId=actor-route-a&runId=run-current&scopeId=scope-route-a&serviceId=default',
    );

    renderWithQueryClient(React.createElement(ActorsPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/runtime/explorer');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('actorId')).toBe('actor-route-a');
    expect(params.get('runId')).toBe('run-current');
    expect(params.get('scopeId')).toBe('scope-route-a');
    expect(params.get('serviceId')).toBe('default');
  });
});
