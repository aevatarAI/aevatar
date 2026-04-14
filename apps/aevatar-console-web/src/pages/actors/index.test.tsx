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

    expect(container.textContent).toContain('Runtime Explorer');
    expect(container.textContent).toContain('Actor Focus');
    expect(container.textContent).toContain('Explorer Digest');
    expect(container.textContent).toContain('Observed Actors');
    expect(screen.getByPlaceholderText('Enter actor ID')).toBeTruthy();
    expect(screen.getByPlaceholderText('Filter discovered actors')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Clear focus' })).toBeTruthy();
    expect(container.textContent).toContain('0 actor entries in view');
    expect(container.textContent).toContain(
      'Snapshot, timeline, and subgraph all resolve from the same actor-focused inspector.',
    );
    expect(container.textContent).toContain('No runtime actors matched the current filter.');
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

    fireEvent.change(screen.getByPlaceholderText('Enter actor ID'), {
      target: { value: 'actor://selected' },
    });

    await waitFor(() => {
      expect(runtimeActorsApi.getActorSnapshot).toHaveBeenCalledWith(
        'actor://selected',
      );
    });
    expect(await screen.findByText('Snapshot')).toBeTruthy();
    expect(screen.getByText('Timeline')).toBeTruthy();
    expect(screen.getByText('Topology Digest')).toBeTruthy();
    expect(screen.getByText('SupportWorkflow')).toBeTruthy();
    expect(screen.getByText(/Last output:/i).textContent).toContain(
      'Completed successfully.',
    );
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
