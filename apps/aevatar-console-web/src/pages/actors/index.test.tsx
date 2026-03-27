import { screen } from '@testing-library/react';
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
  });

  it('renders the runtime explorer shell and navigation actions', async () => {
    const { container } = renderWithQueryClient(
      React.createElement(ActorsPage),
    );

    expect(container.textContent).toContain('Runtime Explorer');
    expect(container.textContent).toContain(
      'Inspect runtime actor snapshots, filter execution history, and switch across enriched, subgraph, and edges-only topology views.',
    );
    expect(
      screen.getByRole('button', { name: 'Open Runtime Runs' }),
    ).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Open Runtime Workflows' }),
    ).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Open observability' })).toBeNull();
    expect(container.textContent).toContain('Runtime actor query');
    expect(container.textContent).toContain('No recent runs yet');
    expect(container.textContent).toContain(
      'Provide a runtime actorId, or choose one from Recent runs, to load actor data.',
    );
  });

  it('shows recent run shortcuts when actorIds were observed before', async () => {
    window.localStorage.setItem(
      'aevatar-console-recent-runs',
      JSON.stringify([
        {
          id: 'cmd-1',
          recordedAt: '2026-03-26T00:00:00Z',
          workflowName: 'direct',
          prompt: 'hello',
          actorId: 'Workflow:1',
          commandId: 'cmd-1',
          runId: 'run-1',
          status: 'completed',
          lastMessagePreview: 'done',
        },
      ]),
    );

    renderWithQueryClient(React.createElement(ActorsPage));

    expect(await screen.findByText('Recent runs')).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'direct · Workflow:1' }),
    ).toBeTruthy();
  });
});
