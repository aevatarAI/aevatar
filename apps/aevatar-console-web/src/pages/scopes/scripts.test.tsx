import { fireEvent, screen } from '@testing-library/react';
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
    getAppContext: jest.fn(async () => ({})),
  },
}));

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
    expect(screen.getByText('Active revision')).toBeTruthy();
    expect(screen.getByText('Source text')).toBeTruthy();
  });
});
