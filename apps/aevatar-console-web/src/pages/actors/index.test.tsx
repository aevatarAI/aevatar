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
  it('renders the runtime explorer shell and navigation actions', async () => {
    const { container } = renderWithQueryClient(
      React.createElement(ActorsPage),
    );

    expect(container.textContent).toContain('Runtime Explorer');
    expect(container.textContent).toContain(
      'Inspect runtime actor snapshots, filter execution history, and switch across enriched, subgraph, and edges-only topology views.',
    );
    expect(screen.getByRole('button', { name: 'Open runs' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open workflows' })).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Open observability' }),
    ).toBeTruthy();
    expect(container.textContent).toContain('Runtime actor query');
    expect(container.textContent).toContain(
      'Provide a runtime actorId to load actor data.',
    );
  });
});
