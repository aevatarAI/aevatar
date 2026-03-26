import { fireEvent, screen } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeWorkflowsPage from './workflows';

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    listWorkflows: jest.fn(async () => [
      {
        workflowId: 'workflow-alpha',
        displayName: 'Workflow Alpha',
        workflowName: 'direct_chat',
        actorId: 'actor://workflow-alpha',
        activeRevisionId: 'rev-2',
        deploymentStatus: 'Published',
        deploymentId: 'deploy-1',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
    getWorkflowDetail: jest.fn(async () => ({
      workflow: {
        displayName: 'Workflow Alpha',
        serviceKey: 'scope-a/workflow-alpha',
      },
      source: {
        definitionActorId: 'actor://definition/workflow-alpha',
        workflowYaml: 'name: workflow-alpha\nsteps: []',
      },
    })),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({ enabled: false })),
    getAppContext: jest.fn(async () => ({})),
  },
}));

describe('ScopeWorkflowsPage', () => {
  beforeEach(() => {
    window.history.replaceState(
      {},
      '',
      '/scopes/workflows?scopeId=scope-a',
    );
  });

  it('opens workflow detail inside the drawer with summary fields', async () => {
    renderWithQueryClient(React.createElement(ScopeWorkflowsPage));

    fireEvent.change(screen.getByPlaceholderText('Enter scopeId'), {
      target: { value: 'scope-a' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Load workflow assets' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Inspect' }));

    expect(await screen.findByText('Workflow YAML')).toBeTruthy();
    expect(screen.getByText('Display name')).toBeTruthy();
    expect(screen.getByText('Service key')).toBeTruthy();
  });
});
