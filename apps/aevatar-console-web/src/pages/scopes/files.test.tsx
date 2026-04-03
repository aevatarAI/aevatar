import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import ProjectFilesPage from './files';

jest.mock('@/pages/studio/components/StudioFilesPage', () => ({
  __esModule: true,
  default: (props: any) => {
    const React = require('react');
    return React.createElement('div', null, [
      React.createElement('h2', { key: 'title' }, 'Files explorer'),
      React.createElement('div', { key: 'scope' }, props.scopeId || 'workspace'),
      React.createElement(
        'div',
        { key: 'header' },
        props.showHeader === false ? 'Header hidden' : 'Header shown',
      ),
      React.createElement(
        'button',
        {
          key: 'workflow',
          type: 'button',
          onClick: () => props.onOpenWorkflowInStudio?.('workflow-alpha'),
        },
        'Open workflow',
      ),
    ]);
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getAuthSession: jest.fn(async () => ({
      enabled: false,
      scopeId: 'scope-a',
      scopeSource: 'nyxid',
    })),
    getAppContext: jest.fn(async () => ({
      mode: 'proxy',
      scopeId: 'scope-a',
      scopeResolved: true,
      scopeSource: 'nyxid',
      workflowStorageMode: 'workspace',
      scriptStorageMode: 'scope',
      features: {
        publishedWorkflows: true,
        scripts: true,
      },
      scriptContract: {
        inputType: 'type.googleapis.com/example.Command',
        readModelFields: ['input', 'output'],
      },
    })),
    getWorkspaceSettings: jest.fn(async () => ({
      runtimeBaseUrl: 'https://runtime.example.test',
      directories: [],
    })),
    listWorkflows: jest.fn(async () => []),
    getConnectorCatalog: jest.fn(async () => ({
      homeDirectory: '/tmp/.aevatar',
      filePath: '/tmp/.aevatar/connectors.json',
      fileExists: true,
      connectors: [],
    })),
    getRoleCatalog: jest.fn(async () => ({
      homeDirectory: '/tmp/.aevatar',
      filePath: '/tmp/.aevatar/roles.json',
      fileExists: true,
      roles: [],
    })),
    getSettings: jest.fn(async () => ({
      runtimeBaseUrl: 'https://runtime.example.test',
      defaultProviderName: 'tornado',
      providerTypes: [],
      providers: [],
    })),
    addWorkflowDirectory: jest.fn(async () => undefined),
    removeWorkflowDirectory: jest.fn(async () => undefined),
  },
}));

describe('ProjectFilesPage', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/scopes/files');
  });

  it('mounts Files as a top-level page beside Assets and hydrates the resolved scope', async () => {
    renderWithQueryClient(React.createElement(ProjectFilesPage));

    expect(await screen.findByText('Files explorer')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getAllByText('scope-a').length).toBeGreaterThan(0);
    });
    expect(screen.getByText('Header hidden')).toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Load project files' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByPlaceholderText('Enter project scopeId'),
    ).not.toBeInTheDocument();
  });

  it('keeps the resolved scope in the Files route', async () => {
    renderWithQueryClient(React.createElement(ProjectFilesPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/scopes/files');
      expect(window.location.search).toBe('?scopeId=scope-a');
    });
  });

  it('opens workflow editing from the top-level Files route', async () => {
    renderWithQueryClient(React.createElement(ProjectFilesPage));

    fireEvent.click(await screen.findByRole('button', { name: 'Open workflow' }));

    await waitFor(() => {
      expect(window.location.pathname).toBe('/studio');
      expect(window.location.search).toBe('?workflow=workflow-alpha&tab=studio');
    });
  });
});
