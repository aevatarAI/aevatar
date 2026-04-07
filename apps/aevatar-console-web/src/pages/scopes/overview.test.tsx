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
    getWorkflowDetail: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      workflow: {
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
      source: {
        workflowYaml: 'name: workflow-alpha',
        definitionActorId: 'definition://workflow-alpha',
        inlineWorkflowYamls: null,
      },
    })),
    getScriptDetail: jest.fn(async () => ({
      available: true,
      scopeId: 'scope-a',
      script: {
        scopeId: 'scope-a',
        scriptId: 'script-alpha',
        catalogActorId: 'catalog://script-alpha',
        definitionActorId: 'definition://script-alpha',
        activeRevision: 'script-rev-1',
        activeSourceHash: 'hash-1',
        updatedAt: '2026-03-25T10:05:00Z',
      },
      source: {
        sourceText: 'print(\"hello\")',
        definitionActorId: 'definition://script-alpha',
        revision: 'script-rev-1',
        sourceHash: 'hash-1',
      },
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
          workflowName: 'Workspace Demo',
          workflowDefinitionActorId: 'definition://workflow/workspace-demo',
          inlineWorkflowCount: 1,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
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
          workflowName: 'Workspace Demo v1',
          workflowDefinitionActorId: 'definition://workflow/workspace-demo-v1',
          inlineWorkflowCount: 1,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    })),
    activateScopeBindingRevision: jest.fn(async () => ({
      scopeId: 'scope-a',
      serviceId: 'default',
      displayName: 'Workspace Demo',
      revisionId: 'rev-1',
    })),
    retireScopeBindingRevision: jest.fn(async () => ({
      scopeId: 'scope-a',
      serviceId: 'default',
      revisionId: 'rev-1',
      status: 'Retiring',
    })),
  },
}));

import { studioApi } from '@/shared/studio/api';

describe('ScopeOverviewPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/overview?scopeId=scope-a');
    jest.clearAllMocks();
  });

  it('renders the scope status board and asset summaries', async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText('Scope Status Board')).toBeTruthy();
    expect(
      screen.queryByText(
        'Project Overview is now a true scope-level status board: binding posture, asset surface, and next-step actions all live on one stage.',
      ),
    ).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Show help' }).length).toBeGreaterThan(0);
    expect(await screen.findByText('Current Binding')).toBeTruthy();
    expect(await screen.findByText('Revision Rollout')).toBeTruthy();
    expect(screen.getByText('Revision Rollout')).toBeTruthy();
    expect(await screen.findByText('Workflow Alpha')).toBeTruthy();
    expect(await screen.findByText('script-alpha')).toBeTruthy();
    expect(
      screen.getByRole('button', { name: 'Open workflow workspace' })
    ).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open assets' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open invoke lab' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Invoke Services' })).toBeNull();
  });

  it('activates a historical revision from the overview page', async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText('Revision Rollout')).toBeTruthy();
    const activateButtons = await screen.findAllByRole('button', {
      name: 'Activate',
    });
    fireEvent.click(activateButtons[1]);

    await waitFor(() => {
      expect(studioApi.activateScopeBindingRevision).toHaveBeenCalledWith({
        scopeId: 'scope-a',
        revisionId: 'rev-1',
      });
    });
  });

  it('retires a historical revision from the overview page', async () => {
    renderWithQueryClient(React.createElement(ScopeOverviewPage));

    expect(await screen.findByText('Revision Rollout')).toBeTruthy();
    const retireButtons = await screen.findAllByRole('button', {
      name: 'Retire',
    });
    fireEvent.click(retireButtons[1]);

    await waitFor(() => {
      expect(studioApi.retireScopeBindingRevision).toHaveBeenCalledWith({
        scopeId: 'scope-a',
        revisionId: 'rev-1',
      });
    });
  });
});
