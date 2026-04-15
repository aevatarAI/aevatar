import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { chatHistoryApi } from '@/pages/chat/chatHistoryApi';
import { studioApi } from '@/shared/studio/api';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import StudioFilesPage from './StudioFilesPage';

jest.mock('@/pages/chat/chatHistoryApi', () => ({
  chatHistoryApi: {
    listConversationMetas: jest.fn(),
    loadConversation: jest.fn(),
    deleteConversation: jest.fn(),
  },
}));

jest.mock('@/shared/studio/api', () => ({
  studioApi: {
    getWorkflow: jest.fn(),
    saveSettings: jest.fn(),
    saveRoleCatalog: jest.fn(),
    saveConnectorCatalog: jest.fn(),
    addWorkflowDirectory: jest.fn(),
    removeWorkflowDirectory: jest.fn(),
  },
}));

jest.mock('@/shared/studio/scriptsApi', () => ({
  scriptsApi: {
    listScripts: jest.fn(),
  },
}));

const workspaceSettings = {
  runtimeBaseUrl: 'https://runtime.example.test',
  directories: [
    {
      directoryId: 'dir-1',
      label: 'Workspace',
      path: '/tmp/workflows',
      isBuiltIn: false,
    },
  ],
};

const workflows = [
  {
    workflowId: 'workflow-1',
    name: 'workspace-demo',
    description: 'Workspace workflow',
    fileName: 'workspace-demo.yaml',
    filePath: '/tmp/workflows/workspace-demo.yaml',
    directoryId: 'dir-1',
    directoryLabel: 'Workspace',
    stepCount: 2,
    hasLayout: true,
    updatedAtUtc: '2026-03-18T00:00:00Z',
  },
];

const roles = {
  homeDirectory: '/tmp/.aevatar',
  filePath: '/tmp/.aevatar/roles.json',
  fileExists: true,
  roles: [
    {
      id: 'assistant',
      name: 'Assistant',
      systemPrompt: 'Help the operator.',
      provider: 'tornado',
      model: 'gpt-test',
      connectors: ['web-search'],
    },
  ],
};

const connectors = {
  homeDirectory: '/tmp/.aevatar',
  filePath: '/tmp/.aevatar/connectors.json',
  fileExists: true,
  connectors: [
    {
      name: 'web-search',
      type: 'http',
      enabled: true,
      timeoutMs: 10000,
      retry: 1,
      http: {
        baseUrl: 'https://example.test',
        allowedMethods: ['GET'],
        allowedPaths: ['/search'],
        allowedInputKeys: ['query'],
        defaultHeaders: {},
      },
    },
  ],
};

const settings = {
  runtimeBaseUrl: 'https://runtime.example.test',
  defaultProviderName: 'tornado',
  providerTypes: [],
  providers: [],
};

function createProps(overrides: Record<string, unknown> = {}) {
  return {
    workflows: {
      isLoading: false,
      isError: false,
      error: null,
      data: workflows,
    },
    workspaceSettings: {
      isLoading: false,
      isError: false,
      error: null,
      data: workspaceSettings,
    },
    roles: {
      isLoading: false,
      isError: false,
      error: null,
      data: roles,
    },
    connectors: {
      isLoading: false,
      isError: false,
      error: null,
      data: connectors,
    },
    settings: {
      isLoading: false,
      isError: false,
      error: null,
      data: settings,
    },
    scopeId: 'scope-1',
    workflowStorageMode: 'workspace',
    scriptsEnabled: true,
    onOpenWorkflowInStudio: jest.fn(),
    onOpenScriptInStudio: jest.fn(),
    ...overrides,
  } as any;
}

describe('StudioFilesPage', () => {
  beforeEach(() => {
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      workflowId: 'workflow-1',
      name: 'workspace-demo',
      fileName: 'workspace-demo.yaml',
      filePath: '/tmp/workflows/workspace-demo.yaml',
      directoryId: 'dir-1',
      directoryLabel: 'Workspace',
      yaml: 'name: workspace-demo\nsteps: []\n',
      findings: [],
      updatedAtUtc: '2026-03-18T00:00:00Z',
    });
    (studioApi.saveSettings as jest.Mock).mockImplementation(async (input) => ({
      ...settings,
      runtimeBaseUrl: input.runtimeBaseUrl || settings.runtimeBaseUrl,
      defaultProviderName:
        input.defaultProviderName || settings.defaultProviderName,
      providers: input.providers || settings.providers,
    }));
    (studioApi.saveRoleCatalog as jest.Mock).mockImplementation(async (input) => ({
      ...roles,
      roles: input.roles,
    }));
    (studioApi.saveConnectorCatalog as jest.Mock).mockImplementation(
      async (input) => ({
        ...connectors,
        connectors: input.connectors,
      }),
    );
    (scriptsApi.listScripts as jest.Mock).mockResolvedValue([
      {
        available: true,
        scopeId: 'scope-1',
        script: {
          scopeId: 'scope-1',
          scriptId: 'script-alpha',
          catalogActorId: 'catalog-1',
          definitionActorId: 'definition-1',
          activeRevision: 'rev-1',
          activeSourceHash: 'hash-1',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        source: {
          sourceText: 'using System;\npublic sealed class DraftBehavior {}',
          definitionActorId: 'definition-1',
          revision: 'rev-1',
          sourceHash: 'hash-1',
        },
      },
    ]);
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      {
        id: 'conversation-1',
        actorId: 'NyxIdChat:scope-1',
        commandId: 'command-1',
        runId: 'run-1',
        title: 'Scope conversation',
        serviceId: 'service-1',
        serviceKind: 'nyxid-chat',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T01:00:00Z',
        messageCount: 2,
      },
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue([
      {
        id: 'message-1',
        role: 'user',
        content: 'hello from user',
        timestamp: Date.parse('2026-03-18T01:00:00Z'),
        status: 'complete',
      },
      {
        id: 'message-2',
        role: 'assistant',
        content: 'assistant reply',
        timestamp: Date.parse('2026-03-18T01:01:00Z'),
        status: 'complete',
      },
    ]);
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(undefined);
  });

  it('shows settings by default and saves edited settings.json content', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    expect(screen.getByText('Configuration')).toBeInTheDocument();
    const editor = screen.getByLabelText(
      'settings.json editor',
    ) as HTMLTextAreaElement;
    expect(editor.value).toContain('https://runtime.example.test');

    fireEvent.change(editor, {
      target: {
        value: editor.value.replace(
          'https://runtime.example.test',
          'https://runtime.changed.test',
        ),
      },
    });

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => {
      expect(studioApi.saveSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          runtimeBaseUrl: 'https://runtime.changed.test',
        }),
      );
    });
  });

  it('lets roles and connectors follow the cli-style catalog workflow', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: 'roles.json' }));
    expect(screen.getByText('Role Catalog')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add Role' }));
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(studioApi.saveRoleCatalog).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByRole('button', { name: 'connectors.json' }));
    expect(screen.getByText('Connector Catalog')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    fireEvent.click(screen.getByRole('button', { name: 'HTTP' }));
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(studioApi.saveConnectorCatalog).toHaveBeenCalledTimes(1);
    });
  });

  it('opens workflow and script previews from the tree', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: /workspace-demo\.yaml/i }));

    expect(await screen.findByLabelText('Workflow YAML preview')).toHaveTextContent(
      'name: workspace-demo',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Open in Studio' }));
    expect(props.onOpenWorkflowInStudio).toHaveBeenCalledWith('workflow-1');

    fireEvent.click(await screen.findByRole('button', { name: /script-alpha\.cs/i }));

    await waitFor(() => {
      expect(screen.getByLabelText('Script source preview')).toHaveTextContent(
        'DraftBehavior',
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Open Scripts Studio' }));
    expect(props.onOpenScriptInStudio).toHaveBeenCalledWith('script-alpha');
  });

  it('shows chat histories and lets users delete a conversation', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: /chat-histories\//i }));
    fireEvent.click(await screen.findByText(/NyxIdChat:scope-1/i));

    expect(await screen.findByLabelText('Chat history messages')).toHaveTextContent(
      'hello from user',
    );
    expect(screen.getByLabelText('Chat history messages')).toHaveTextContent(
      'assistant reply',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));

    await waitFor(() => {
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        'scope-1',
        'conversation-1',
      );
    });
  });
});
