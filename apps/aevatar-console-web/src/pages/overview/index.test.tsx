import { waitFor } from '@testing-library/react';
import React from 'react';
import { consoleApi } from '@/shared/api/consoleApi';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import OverviewPage from './index';

jest.mock('@/shared/api/consoleApi', () => ({
  consoleApi: {
    listWorkflows: jest.fn(async () => []),
    listWorkflowCatalog: jest.fn(async () => []),
    listAgents: jest.fn(async () => []),
    getCapabilities: jest.fn(async () => ({
      schemaVersion: 'capabilities.v1',
      generatedAtUtc: '2026-03-12T00:00:00Z',
      primitives: [],
      connectors: [],
      workflows: [],
    })),
  },
}));

describe('OverviewPage', () => {
  it('renders the overview title', async () => {
    const { container } = renderWithQueryClient(React.createElement(OverviewPage));

    expect(container.textContent).toContain('Overview');
    expect(container.textContent).toContain(
      'Overview of workflows, runtime capabilities, actors, and observability.',
    );
    expect(container.textContent).toContain('Runtime capability snapshot');
    expect(container.textContent).toContain('Workflow tools');
    expect(container.textContent).toContain('Workflow library');
    expect(container.textContent).toContain('New Studio draft');
    await waitFor(() => {
      expect(consoleApi.listWorkflows).toHaveBeenCalled();
      expect(consoleApi.listAgents).toHaveBeenCalled();
      expect(consoleApi.getCapabilities).toHaveBeenCalled();
    });
  });
});
