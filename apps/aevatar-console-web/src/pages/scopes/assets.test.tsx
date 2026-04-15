import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { buildStudioWorkflowEditorRoute } from '@/shared/studio/navigation';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import TeamAssetsPage from './assets';

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    listWorkflows: jest.fn(async (scopeId: string) =>
      scopeId?.trim() === 'scope-b'
        ? [
            {
              workflowId: 'workflow-beta',
              displayName: 'Workflow Beta',
              workflowName: 'ops_flow',
              actorId: 'actor://workflow-beta',
              activeRevisionId: 'rev-9',
              deploymentStatus: 'Published',
              deploymentId: 'deploy-9',
              updatedAt: '2026-03-26T10:00:00Z',
            },
          ]
        : [
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
          ],
    ),
    listScripts: jest.fn(async (scopeId: string) =>
      scopeId?.trim() === 'scope-b'
        ? [
            {
              scriptId: 'script-beta',
              activeRevision: 'script-rev-2',
              activeSourceHash: 'hash-2',
              definitionActorId: 'definition://script-beta',
              updatedAt: '2026-03-26T10:05:00Z',
            },
          ]
        : [
            {
              scriptId: 'script-alpha',
              activeRevision: 'script-rev-1',
              activeSourceHash: 'hash-1',
              definitionActorId: 'definition://script-alpha',
              updatedAt: '2026-03-25T10:05:00Z',
            },
          ],
    ),
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
    getScopeBinding: jest.fn(async (scopeId: string) => ({
      available: true,
      scopeId: scopeId?.trim() || 'scope-a',
      serviceId: scopeId?.trim() === 'scope-b' ? 'ops' : 'default',
      displayName: scopeId?.trim() === 'scope-b' ? 'Workspace Beta' : 'Workspace Demo',
      serviceKey: scopeId?.trim() === 'scope-b' ? 'scope-b:ops' : 'scope-a:default',
      primaryActorId:
        scopeId?.trim() === 'scope-b'
          ? 'actor://scope-b/ops'
          : 'actor://scope-a/default',
      revisions: [],
    })),
  },
}));

describe('TeamAssetsPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/assets?scopeId=scope-a');
  });

  it('shows the unified project asset summary and workflow detail drawer', async () => {
    renderWithQueryClient(React.createElement(TeamAssetsPage));

    expect(await screen.findByText('Legacy Team Assets')).toBeTruthy();
    expect(await screen.findByText('Legacy asset summary')).toBeTruthy();
    expect(
      screen.getByText(
        'Team home now lives under /teams. Keep this page for older asset deep links, source inspection, and catalog detail while the team-first flow finishes taking over.',
      ),
    ).toBeTruthy();
    expect(
      screen.getByText(
        'Team home is now the primary surface. Use this legacy asset workspace when you need source inspection, catalog state, or older deep links.',
      ),
    ).toBeTruthy();
    expect(await screen.findByText('Workspace Demo')).toBeTruthy();
    expect(await screen.findByText('Workflow Alpha')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Team Builder' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Team Home' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Member Runtime' })).toBeTruthy();

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
    renderWithQueryClient(React.createElement(TeamAssetsPage));

    expect(await screen.findByText('Legacy asset summary')).toBeTruthy();
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

  it('opens a selected workflow in the Team Builder route with team context', async () => {
    const pushSpy = jest.spyOn(history, 'push');
    renderWithQueryClient(React.createElement(TeamAssetsPage));

    fireEvent.click(await screen.findByRole('button', { name: 'Edit in Team Builder' }));

    expect(pushSpy).toHaveBeenCalledWith(
      buildStudioWorkflowEditorRoute({
        scopeId: 'scope-a',
        workflowId: 'workflow-alpha',
      }),
    );
  });

  it('resyncs the asset workspace when the URL scope and tab change after mount', async () => {
    renderWithQueryClient(React.createElement(TeamAssetsPage));

    expect(await screen.findByText('Workflow Alpha')).toBeTruthy();

    await act(async () => {
      window.history.replaceState(
        {},
        '',
        '/scopes/assets?scopeId=scope-b&tab=scripts&scriptId=script-beta',
      );
      window.dispatchEvent(
        new PopStateEvent('popstate', { state: window.history.state }),
      );
    });

    expect(await screen.findByText('Workspace Beta')).toBeTruthy();
    expect((await screen.findAllByText('script-beta')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Workflow Alpha')).toBeNull();
  });
});
