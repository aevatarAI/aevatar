import { fireEvent, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { chatHistoryApi } from '@/pages/chat/chatHistoryApi';
import { explorerApi } from '@/shared/api/explorerApi';
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
    saveRoleCatalog: jest.fn(),
    saveConnectorCatalog: jest.fn(),
  },
}));

jest.mock('@/shared/api/explorerApi', () => ({
  explorerApi: {
    getManifest: jest.fn(),
    getFile: jest.fn(),
    putFile: jest.fn(),
    deleteFile: jest.fn(),
  },
}));

jest.mock('@/shared/studio/scriptsApi', () => ({
  scriptsApi: {
    listScripts: jest.fn(),
  },
}));

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
  homeDirectory: 'actor://role-catalog',
  filePath: 'actor://role-catalog/roles',
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
  homeDirectory: 'actor://connector-catalog',
  filePath: 'actor://connector-catalog/connectors',
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

function createProps(overrides: Record<string, unknown> = {}) {
  return {
    workflows: {
      isLoading: false,
      isError: false,
      error: null,
      data: workflows,
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
    scopeId: 'scope-1',
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
        title: 'Scope conversation',
        serviceId: 'service-1',
        serviceKind: 'nyxid-chat',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T01:00:00Z',
        messageCount: 2,
      },
    ]);
    (chatHistoryApi.loadConversation as jest.Mock).mockResolvedValue({
      messages: [
        {
          id: 'message-1',
          role: 'user',
          content: 'hello from user',
          authorName: 'Alice',
          timestamp: Date.parse('2026-03-18T01:00:00Z'),
          status: 'complete',
        },
        {
          id: 'message-2',
          role: 'assistant',
          content: 'assistant reply',
          thinking: 'Planning the response',
          timestamp: Date.parse('2026-03-18T01:01:00Z'),
          status: 'complete',
        },
        {
          id: 'message-3',
          role: 'user',
          content: 'run it now',
          timestamp: Date.parse('2026-03-18T01:02:00Z'),
          status: 'complete',
        },
        {
          id: 'message-4',
          role: 'assistant',
          content: '',
          error: 'workflow_run_error: Dispatch failed.',
          timestamp: Date.parse('2026-03-18T01:03:00Z'),
          status: 'error',
        },
      ],
      stateVersion: 7,
    });
    (chatHistoryApi.deleteConversation as jest.Mock).mockResolvedValue(undefined);
    (explorerApi.putFile as jest.Mock).mockResolvedValue(undefined);
    (explorerApi.deleteFile as jest.Mock).mockResolvedValue(undefined);
    (explorerApi.getManifest as jest.Mock).mockResolvedValue({
      version: 1,
      files: [
        {
          key: 'notes.txt',
          type: 'config',
          name: 'notes.txt',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        {
          key: 'workflows/workflow-1.yaml',
          type: 'workflow',
          name: 'workflow-1.yaml',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        {
          key: 'scripts/script-alpha.cs',
          type: 'script',
          name: 'script-alpha.cs',
          updatedAt: '2026-03-18T00:00:00Z',
        },
        {
          key: 'chat-histories/conversation-1.jsonl',
          type: 'chat-history',
          name: 'conversation-1.jsonl',
          updatedAt: '2026-03-18T01:00:00Z',
        },
      ],
    });
    (explorerApi.getFile as jest.Mock).mockImplementation(async (key: string) => {
      if (key === 'workflows/workflow-1.yaml') {
        return 'name: workflow-1\nsteps: []\n';
      }

      if (key === 'scripts/script-alpha.cs') {
        return JSON.stringify({
          format: 'aevatar.scripting.package.v1',
          entrySourcePath: 'Behavior.cs',
          csharpSources: [{ path: 'Behavior.cs', content: 'public sealed class DraftBehavior {}' }],
          protoFiles: [],
        });
      }

      if (key === 'chat-histories/conversation-1.jsonl') {
        return JSON.stringify([
          {
            id: 'message-1',
            role: 'user',
            content: 'hello from explorer',
            timestamp: Date.parse('2026-03-18T01:00:00Z'),
            status: 'complete',
          },
          {
            id: 'message-2',
            role: 'assistant',
            content: 'explorer reply',
            timestamp: Date.parse('2026-03-18T01:01:00Z'),
            status: 'complete',
          },
        ]);
      }

      return 'draft content';
    });
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('renders tree skeletons for loading workflow and script folders', async () => {
    (scriptsApi.listScripts as jest.Mock).mockImplementationOnce(
      () => new Promise(() => {}),
    );
    const props = createProps({
      workflows: {
        isLoading: true,
        isError: false,
        error: null,
        data: undefined,
      },
    });

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    const loadingFolders = await screen.findAllByRole('status');
    expect(loadingFolders).toHaveLength(2);
    for (const folder of loadingFolders) {
      expect(folder).toHaveAttribute('data-list-layout', 'tree');
      expect(folder).toHaveAttribute('data-variant', 'list');
    }
    expect(screen.getByText('Loading workflows...')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
    expect(screen.getByText('Loading scripts...')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );
  });

  it('does not expose host provider settings as an editable file', () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    expect(screen.queryByText('settings.json')).not.toBeInTheDocument();
    expect(screen.queryByText('Configuration')).not.toBeInTheDocument();
  });

  it('falls back to the first visible catalog when search hides the selected panel', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));
    fireEvent.click(screen.getByRole('button', { name: 'Connector Catalog' }));
    fireEvent.change(screen.getByLabelText('Search files'), {
      target: { value: 'Role Catalog' },
    });

    expect(await screen.findByRole('button', { name: 'Add Role' })).toBeInTheDocument();
    expect(screen.getAllByText('Role Catalog').length).toBeGreaterThan(1);
  });

  it('lets roles and connectors follow the cli-style catalog workflow', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: 'Role Catalog' }));
    // Both the tree button and the detail pane title render the label — ensure at least the panel is mounted.
    expect(screen.getAllByText('Role Catalog').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Add Role' }));
    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(studioApi.saveRoleCatalog).toHaveBeenCalledTimes(1);
    });

    fireEvent.click(screen.getByRole('button', { name: 'Connector Catalog' }));
    expect(screen.getAllByText('Connector Catalog').length).toBeGreaterThan(0);
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

    fireEvent.click(await screen.findByRole('button', { name: /script-1\.cs/i }));

    await waitFor(() => {
      expect(screen.getByLabelText('Script source preview')).toHaveTextContent(
        'DraftBehavior',
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Open Script Build' }));
    expect(props.onOpenScriptInStudio).toHaveBeenCalledWith('script-alpha');
  });

  it('shows chat-history turns and confirms conversation deletion', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: /chat-histories\//i }));
    fireEvent.click(await screen.findByText('Scope conversation'));

    expect(await screen.findByLabelText('Chat history messages')).toHaveTextContent(
      'hello from user',
    );
    expect(screen.getByLabelText('Chat history messages')).toHaveTextContent(
      'assistant reply',
    );
    expect(screen.getAllByText(/2 turns/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/2 messages/)).toBeNull();
    expect(screen.getByLabelText('Chat history messages')).toHaveTextContent(
      'Alice',
    );
    expect(screen.getByLabelText('Chat history messages')).toHaveTextContent(
      'Planning the response',
    );
    expect(screen.getByLabelText('Chat history messages')).toHaveTextContent(
      'workflow_run_error: Dispatch failed.',
    );
    expect(screen.queryByText('(empty message)')).toBeNull();
    expect(screen.queryByText(/NyxIdChat:scope-1/i)).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(chatHistoryApi.deleteConversation).not.toHaveBeenCalled();
    expect(screen.getByText('Delete this conversation?')).toBeInTheDocument();
    expect(screen.getByText(/Scope conversation will be removed/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Keep conversation' }));
    expect(screen.queryByText('Delete this conversation?')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete now' }));

    await waitFor(() => {
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        'scope-1',
        'conversation-1',
      );
    });
    expect(screen.queryByText('Scope conversation')).toBeNull();
  });

  it('shows chat-history list failures and retries them from the tree', async () => {
    (chatHistoryApi.listConversationMetas as jest.Mock)
      .mockRejectedValueOnce(new Error('History access denied'))
      .mockResolvedValueOnce([]);
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));
    fireEvent.click(screen.getByRole('button', { name: /chat-histories\//i }));

    expect(await screen.findByText('Failed to load conversations')).toBeInTheDocument();
    expect(screen.getByText('History access denied')).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', { name: 'Retry conversations' }),
    );

    expect(await screen.findByText('No conversations matched.')).toBeInTheDocument();
    expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledTimes(2);
  });

  it('does not apply a delayed chat deletion to a new scope', async () => {
    let resolveDelete = (): void => undefined;
    let switchScope = (_scopeId: string): void => undefined;
    const deletePromise = new Promise<void>((resolve) => {
      resolveDelete = resolve;
    });
    (chatHistoryApi.deleteConversation as jest.Mock).mockReturnValue(deletePromise);

    function ScopeHarness() {
      const [scopeId, setScopeId] = React.useState('scope-1');
      switchScope = setScopeId;
      return React.createElement(StudioFilesPage, createProps({ scopeId }));
    }

    renderWithQueryClient(React.createElement(ScopeHarness));
    fireEvent.click(screen.getByRole('button', { name: /chat-histories\//i }));
    fireEvent.click(await screen.findByText('Scope conversation'));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete now' }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        'scope-1',
        'conversation-1',
      ),
    );

    React.act(() => switchScope('scope-2'));
    await waitFor(() =>
      expect(chatHistoryApi.listConversationMetas).toHaveBeenCalledWith('scope-2'),
    );
    expect(await screen.findByText('Scope conversation')).toBeInTheDocument();
    await React.act(async () => resolveDelete());

    expect(screen.getByText('Scope conversation')).toBeInTheDocument();
  });

  it('keeps a delayed deletion bound to its original conversation', async () => {
    let resolveDelete = (): void => undefined;
    const deletePromise = new Promise<void>((resolve) => {
      resolveDelete = resolve;
    });
    (chatHistoryApi.deleteConversation as jest.Mock).mockReturnValue(deletePromise);
    (chatHistoryApi.listConversationMetas as jest.Mock).mockResolvedValue([
      {
        id: 'conversation-1',
        title: 'Scope conversation',
        serviceId: 'service-1',
        serviceKind: 'nyxid-chat',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T01:00:00Z',
        messageCount: 2,
      },
      {
        id: 'conversation-2',
        title: 'Second conversation',
        serviceId: 'service-1',
        serviceKind: 'nyxid-chat',
        createdAt: '2026-03-18T00:00:00Z',
        updatedAt: '2026-03-18T02:00:00Z',
        messageCount: 2,
      },
    ]);

    renderWithQueryClient(React.createElement(StudioFilesPage, createProps()));
    fireEvent.click(screen.getByRole('button', { name: /chat-histories\//i }));
    fireEvent.click(await screen.findByText('Scope conversation'));
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete now' }));
    await waitFor(() =>
      expect(chatHistoryApi.deleteConversation).toHaveBeenCalledWith(
        'scope-1',
        'conversation-1',
      ),
    );

    fireEvent.click(await screen.findByText('Second conversation'));
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled(),
    );

    await React.act(async () => resolveDelete());

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Delete' })).toBeEnabled(),
    );
    expect(screen.queryByText('Scope conversation')).toBeNull();
    expect(screen.getAllByText('Second conversation').length).toBeGreaterThan(0);
    expect(screen.queryByText('Conversation deleted.')).toBeNull();
    expect(chatHistoryApi.deleteConversation).toHaveBeenCalledTimes(1);
  });

  it('switches to explorer and previews chrono-storage files', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: 'Storage Explorer' }));

    expect(await screen.findByRole('button', { name: /workflow-1\.yaml/i })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /workflow-1\.yaml/i }));

    expect(await screen.findByText('Read-only in Explorer')).toBeInTheDocument();
    expect(await screen.findByLabelText('Explorer file preview')).toHaveTextContent(
      'name: workflow-1',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Open in Studio' }));
    expect(props.onOpenWorkflowInStudio).toHaveBeenCalledWith('workflow-1');

    fireEvent.click(screen.getByRole('button', { name: /conversation-1\.jsonl/i }));
    expect(await screen.findByLabelText('Explorer chat history preview')).toHaveTextContent(
      'hello from explorer',
    );
    expect(screen.getByLabelText('Explorer chat history preview')).toHaveTextContent(
      'explorer reply',
    );
  });

  it('saves editable explorer files and blocks file switches with unsaved changes', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: 'Storage Explorer' }));

    const editor = (await screen.findByLabelText(
      'Explorer file editor',
    )) as HTMLTextAreaElement;
    expect(editor.value).toContain('draft content');

    fireEvent.change(editor, {
      target: {
        value: editor.value.replace('draft content', 'updated content'),
      },
    });

    expect(screen.getByText('Unsaved changes')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /workflow-1\.yaml/i }));

    expect(screen.queryByText('Read-only in Explorer')).not.toBeInTheDocument();
    expect(
      (screen.getByLabelText('Explorer file editor') as HTMLTextAreaElement).value,
    ).toContain('updated content');
    expect(
      screen.getByText('You have unsaved Explorer changes'),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.queryByText('You have unsaved Explorer changes')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(explorerApi.putFile).toHaveBeenCalledWith(
        'notes.txt',
        expect.stringContaining('updated content'),
      );
    });
  });

  it('deletes editable explorer files after confirmation', async () => {
    const props = createProps();

    renderWithQueryClient(React.createElement(StudioFilesPage, props));

    fireEvent.click(screen.getByRole('button', { name: 'Storage Explorer' }));
    fireEvent.click(await screen.findByRole('button', { name: /conversation-1\.jsonl/i }));

    await screen.findByLabelText('Explorer chat history preview');
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    expect(
      screen.getByText('Delete this file from Storage Explorer?'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Delete now' }));

    await waitFor(() => {
      expect(explorerApi.deleteFile).toHaveBeenCalledWith(
        'chat-histories/conversation-1.jsonl',
      );
    });
  });
});
