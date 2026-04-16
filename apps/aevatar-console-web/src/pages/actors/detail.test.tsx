import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TopologyDetailPage from './detail';

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

describe('TopologyDetailPage', () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it('loads live actor topology in the dedicated detail page', async () => {
    const { runtimeActorsApi } = jest.requireMock('@/shared/api/runtimeActorsApi') as {
      runtimeActorsApi: {
        getActorGraphEnriched: jest.Mock;
        getActorSnapshot: jest.Mock;
        getActorTimeline: jest.Mock;
      };
    };

    window.history.replaceState(
      {},
      '',
      '/runtime/explorer/detail?actorId=actor://selected&runId=run-current&scopeId=scope-route-a&serviceId=default',
    );

    runtimeActorsApi.getActorSnapshot.mockResolvedValue({
      actorId: 'actor://selected',
      completedSteps: 4,
      completionStatusValue: 1,
      lastCommandId: 'cmd-1',
      lastError: '',
      lastEventId: 'evt-1',
      lastOutput: 'Completed successfully.',
      lastSuccess: true,
      lastUpdatedAt: '2026-03-26T00:00:00Z',
      requestedSteps: 4,
      roleReplyCount: 2,
      stateVersion: 7,
      totalSteps: 4,
      workflowName: 'SupportWorkflow',
    });
    runtimeActorsApi.getActorTimeline.mockResolvedValue([
      {
        agentId: 'actor://selected',
        data: {},
        eventType: 'StepStarted',
        message: 'Step started',
        stage: 'workflow.started',
        stepId: 'step-1',
        stepType: 'chat',
        timestamp: '2026-03-26T00:00:01Z',
      },
    ]);
    runtimeActorsApi.getActorGraphEnriched.mockResolvedValue({
      snapshot: {
        actorId: 'actor://selected',
      },
      subgraph: {
        edges: [],
        nodes: [
          {
            nodeId: 'actor://selected',
            nodeType: 'Actor',
            properties: {
              workflowName: 'SupportWorkflow',
            },
            updatedAt: '2026-03-26T00:00:00Z',
          },
        ],
        rootNodeId: 'actor://selected',
      },
    });

    renderWithQueryClient(React.createElement(TopologyDetailPage));

    await waitFor(() => {
      expect(runtimeActorsApi.getActorSnapshot).toHaveBeenCalledWith(
        'actor://selected',
      );
    });
    expect(await screen.findByText('追查工作区')).toBeTruthy();
    expect(screen.getByText('最近事件')).toBeTruthy();
    expect(screen.getAllByText('SupportWorkflow').length).toBeGreaterThan(0);
    expect(screen.getByText('GraphCanvas')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: '快照' }));

    expect(screen.getByText('最近输出')).toBeTruthy();
    expect(screen.getByText('Completed successfully.')).toBeTruthy();
  });

  it('opens the fullscreen graph workspace from the detail page', async () => {
    const { runtimeActorsApi } = jest.requireMock('@/shared/api/runtimeActorsApi') as {
      runtimeActorsApi: {
        getActorGraphEnriched: jest.Mock;
        getActorSnapshot: jest.Mock;
        getActorTimeline: jest.Mock;
      };
    };

    window.history.replaceState(
      {},
      '',
      '/runtime/explorer/detail?actorId=actor://selected&runId=run-current&scopeId=scope-route-a&serviceId=default',
    );

    runtimeActorsApi.getActorSnapshot.mockResolvedValue({
      actorId: 'actor://selected',
      completedSteps: 4,
      completionStatusValue: 1,
      lastCommandId: 'cmd-1',
      lastError: '',
      lastEventId: 'evt-1',
      lastOutput: 'Completed successfully.',
      lastSuccess: true,
      lastUpdatedAt: '2026-03-26T00:00:00Z',
      requestedSteps: 4,
      roleReplyCount: 2,
      stateVersion: 7,
      totalSteps: 4,
      workflowName: 'SupportWorkflow',
    });
    runtimeActorsApi.getActorTimeline.mockResolvedValue([]);
    runtimeActorsApi.getActorGraphEnriched.mockResolvedValue({
      snapshot: {
        actorId: 'actor://selected',
      },
      subgraph: {
        edges: [],
        nodes: [
          {
            nodeId: 'actor://selected',
            nodeType: 'Actor',
            properties: {
              workflowName: 'SupportWorkflow',
            },
            updatedAt: '2026-03-26T00:00:00Z',
          },
        ],
        rootNodeId: 'actor://selected',
      },
    });

    renderWithQueryClient(React.createElement(TopologyDetailPage));

    expect(await screen.findByRole('button', { name: '全屏查看关系图' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: '全屏查看关系图' }));

    expect(await screen.findByText('全屏关系图')).toBeTruthy();
  });

  it('preserves playback explorer detail context from the incoming route', async () => {
    window.history.replaceState(
      {},
      '',
      '/runtime/explorer/detail?actorId=actor-route-a&runId=run-current&scopeId=scope-route-a&serviceId=default',
    );

    renderWithQueryClient(React.createElement(TopologyDetailPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/runtime/explorer/detail');
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get('actorId')).toBe('actor-route-a');
    expect(params.get('runId')).toBe('run-current');
    expect(params.get('scopeId')).toBe('scope-route-a');
    expect(params.get('serviceId')).toBe('default');
  });
});
