import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ScopeOverviewPage from './overview';

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    listWorkflows: jest.fn(async () => [
      {
        scopeId: 'scope-a',
        workflowId: 'workflow-alpha',
        displayName: 'Workflow Alpha',
        serviceKey: 'scope-a/default',
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
        scopeId: 'scope-a',
        scriptId: 'script-alpha',
        catalogActorId: 'catalog://script-alpha',
        definitionActorId: 'definition://script-alpha',
        activeRevision: 'script-rev-1',
        activeSourceHash: 'hash-1',
        updatedAt: '2026-03-25T10:05:00Z',
      },
    ]),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({ enabled: false })),
    getAppContext: jest.fn(async () => ({
      scopeId: 'scope-a',
      scopeResolved: true,
      scopeSource: 'nyxid',
    })),
    getScopeBinding: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Workspace Demo',
      serviceKey: 'scope-a:default',
      defaultServingRevisionId: 'rev-2',
      activeServingRevisionId: 'rev-2',
      deploymentId: 'deploy-2',
      deploymentStatus: 'Active',
      primaryActorId: 'actor://scope-a/default',
      updatedAt: '2026-03-26T08:00:00Z',
      revisions: [
        {
          revisionId: 'rev-2',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-2',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deploy-2',
          primaryActorId: 'actor://scope-a/default',
          createdAt: '2026-03-26T07:00:00Z',
          preparedAt: '2026-03-26T07:01:00Z',
          publishedAt: '2026-03-26T07:02:00Z',
          retiredAt: null,
        },
        {
          revisionId: 'rev-1',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'hash-1',
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
      displayName: 'Workspace Demo',
      revisionId: 'rev-1',
    })),
  },
}));

import { studioApi } from '@/shared/studio/api';

describe('ScopeOverviewPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/overview?scopeId=scope-a');
    jest.clearAllMocks();
  });

  it('renders the scope binding snapshot and asset summaries', async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText('Binding Snapshot')).toBeTruthy();
    expect(screen.getByText('Revision Rollout')).toBeTruthy();
    expect(await screen.findByText('Workflow Alpha')).toBeTruthy();
    expect(await screen.findByText('script-alpha')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Runs' })).toBeTruthy();
  });

  it('activates a historical revision from the overview page', async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText('Revision Rollout')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Activate rev-1' }));

    await waitFor(() => {
      expect(studioApi.activateScopeBindingRevision).toHaveBeenCalledWith({
        scopeId: 'scope-a',
        revisionId: 'rev-1',
      });
    });
  });
});
