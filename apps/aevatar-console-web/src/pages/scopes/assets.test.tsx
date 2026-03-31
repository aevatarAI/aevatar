import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ProjectAssetsPage from './assets';

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
    listScripts: jest.fn(async () => [
      {
        scriptId: 'script-alpha',
        activeRevision: 'script-rev-1',
        activeSourceHash: 'hash-1',
        definitionActorId: 'definition://script-alpha',
        updatedAt: '2026-03-25T10:05:00Z',
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
    getScriptDetail: jest.fn(async () => ({
      script: {
        activeRevision: 'script-rev-1',
        definitionActorId: 'actor://definition/script-alpha',
        catalogActorId: 'actor://catalog/script-alpha',
      },
      source: {
        sourceText: 'export default async function main() {}',
      },
    })),
    getScriptCatalog: jest.fn(async () => ({
      activeRevision: 'script-rev-1',
      previousRevision: 'script-rev-0',
      revisionHistory: ['script-rev-0', 'script-rev-1'],
      lastProposalId: 'proposal-1',
    })),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: 'scope-a',
      scopeSource: 'nyxid',
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Workspace Demo',
    })),
  },
}));

describe('ProjectAssetsPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/assets?scopeId=scope-a');
  });

  it('shows the unified project asset summary and workflow detail drawer', async () => {
    renderWithQueryClient(React.createElement(ProjectAssetsPage));

    expect(await screen.findByText('Project asset summary')).toBeTruthy();
    expect(await screen.findByText('Workspace Demo')).toBeTruthy();
    expect(await screen.findByText('Workflow Alpha')).toBeTruthy();

    fireEvent.click(screen.getAllByRole('button', { name: 'Inspect' })[0]);

    expect(await screen.findByText('Workflow YAML')).toBeTruthy();
    expect(screen.getByText('Service key')).toBeTruthy();

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get('tab')).toBe('workflows');
      expect(params.get('workflowId')).toBe('workflow-alpha');
    });
  });

  it('switches to script assets and opens catalog detail in the same workspace', async () => {
    renderWithQueryClient(React.createElement(ProjectAssetsPage));

    expect(await screen.findByText('Project asset summary')).toBeTruthy();
    fireEvent.click(await screen.findByRole('tab', { name: 'Scripts (1)' }));
    fireEvent.click(screen.getAllByRole('button', { name: 'Inspect' })[0]);

    expect(await screen.findByText('Catalog state')).toBeTruthy();
    expect(screen.getByText('Source text')).toBeTruthy();

    await waitFor(() => {
      const params = new URLSearchParams(window.location.search);
      expect(params.get('tab')).toBe('scripts');
      expect(params.get('scriptId')).toBe('script-alpha');
    });
  });

  it('opens a selected workflow in the Studio editor route', async () => {
    renderWithQueryClient(React.createElement(ProjectAssetsPage));

    fireEvent.click(await screen.findByRole('button', { name: 'Open workflow editor' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/studio');
      expect(window.location.search).toBe('?workflow=workflow-alpha&tab=studio');
    });
  });
});
