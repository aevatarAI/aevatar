import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeScriptsPage from './scripts';

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    listScripts: jest.fn(async () => [
      {
        scriptId: 'script-alpha',
        activeRevision: 'rev-3',
        activeSourceHash: 'hash-123',
        definitionActorId: 'actor://definition/script-alpha',
        updatedAt: '2026-03-25T10:00:00Z',
      },
    ]),
    getScriptDetail: jest.fn(async () => ({
      script: {
        activeRevision: 'rev-3',
        definitionActorId: 'actor://definition/script-alpha',
        catalogActorId: 'actor://catalog/script-alpha',
      },
      source: {
        sourceText: 'export default async function main() {}',
      },
    })),
    getScriptCatalog: jest.fn(async () => ({
      activeRevision: 'rev-3',
      previousRevision: 'rev-2',
      revisionHistory: ['rev-1', 'rev-2', 'rev-3'],
      lastProposalId: 'proposal-1',
    })),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({ enabled: false })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Script Service',
      serviceKey: 'scope-a/default',
      defaultServingRevisionId: 'rev-3',
      activeServingRevisionId: 'rev-3',
      deploymentId: 'deploy-3',
      deploymentStatus: 'Active',
      primaryActorId: 'actor://scope/default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-3',
          implementationKind: 'script',
          status: 'Published',
          artifactHash: 'hash-3',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deploy-3',
          primaryActorId: 'actor://scope/default',
          createdAt: '2026-03-26T07:00:00Z',
          preparedAt: '2026-03-26T07:01:00Z',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
        },
        {
          revisionId: 'rev-2',
          implementationKind: 'script',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: false,
          isServingTarget: false,
          allocationWeight: 0,
          servingState: 'Inactive',
          deploymentId: '',
          primaryActorId: '',
          createdAt: '2026-03-25T07:00:00Z',
          preparedAt: '2026-03-25T07:01:00Z',
          publishedAt: '2026-03-25T07:02:00Z',
          retiredAt: null,
        },
      ],
    })),
    activateScopeBindingRevision: jest.fn(async () => ({
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Script Service',
      revisionId: 'rev-2',
    })),
  },
}));

import { studioApi } from '@/shared/studio/api';

describe('ScopeScriptsPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/scripts?scopeId=scope-a');
  });

  it('opens script detail with catalog summary and source text', async () => {
    renderWithQueryClient(React.createElement(ScopeScriptsPage));

    fireEvent.change(screen.getByPlaceholderText('Enter scopeId'), {
      target: { value: 'scope-a' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Load script assets' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Inspect' }));

    expect(await screen.findByText('Catalog state')).toBeTruthy();
    expect(screen.getAllByText('Active revision').length).toBeGreaterThan(0);
    expect(screen.getByText('Source text')).toBeTruthy();
  });

  it('shows scope binding status and revision rollout for scripts', async () => {
    renderWithQueryClient(React.createElement(ScopeScriptsPage));

    fireEvent.change(screen.getByPlaceholderText('Enter scopeId'), {
      target: { value: 'scope-a' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Load script assets' }));

    expect(await screen.findByText('Scope Binding Status')).toBeTruthy();
    expect(screen.getByText('Binding Revisions')).toBeTruthy();
    expect(await screen.findByRole('button', { name: 'Activate rev-2' })).toBeTruthy();
  });

  it('activates an older script-backed revision from the scope page', async () => {
    renderWithQueryClient(React.createElement(ScopeScriptsPage));

    fireEvent.change(screen.getByPlaceholderText('Enter scopeId'), {
      target: { value: 'scope-a' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Load script assets' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Activate rev-2' }));

    await waitFor(() => {
      expect(studioApi.activateScopeBindingRevision).toHaveBeenCalledWith({
        scopeId: 'scope-a',
        revisionId: 'rev-2',
      });
    });
  });
});
