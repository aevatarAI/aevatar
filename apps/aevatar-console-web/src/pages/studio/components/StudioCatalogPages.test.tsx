import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import {
  type StudioConnectorCatalogItem,
  type StudioConnectorType,
  StudioConnectorsPage,
  type StudioRoleCatalogItem,
  StudioRolesPage,
} from './StudioWorkbenchSections';

type RolesProps = React.ComponentProps<typeof StudioRolesPage>;
type ConnectorsProps = React.ComponentProps<typeof StudioConnectorsPage>;

const settingsProviders = [
  {
    providerName: 'tornado',
    model: 'gpt-test',
  },
];

const roleCatalogDraft: StudioRoleCatalogItem[] = [
  {
    key: 'role-1',
    id: 'assistant',
    name: 'Assistant',
    systemPrompt: 'Help the operator.',
    provider: 'tornado',
    model: 'gpt-test',
    connectorsText: 'web-search',
  },
];

const connectorCatalogDraft: StudioConnectorCatalogItem[] = [
  {
    key: 'connector-1',
    name: 'web-search',
    type: 'http' as StudioConnectorType,
    enabled: true,
    timeoutMs: '10000',
    retry: '1',
    http: {
      baseUrl: 'https://example.test',
      allowedMethods: ['GET'],
      allowedPaths: ['/search'],
      allowedInputKeys: ['query'],
      defaultHeaders: {},
    },
    cli: {
      command: '',
      fixedArguments: [],
      allowedOperations: [],
      allowedInputKeys: [],
      workingDirectory: '',
      environment: {},
    },
    mcp: {
      serverName: '',
      command: '',
      arguments: [],
      environment: {},
      defaultTool: '',
      allowedTools: [],
      allowedInputKeys: [],
    },
  },
];

function createRolesProps(overrides: Partial<RolesProps> = {}): RolesProps {
  return {
    roles: {
      isLoading: false,
      isError: false,
      error: null,
      data: {
        roles: [],
      },
    },
    appearanceTheme: 'blue',
    colorMode: 'light',
    roleCatalogDraft,
    roleCatalogMeta: {
      filePath: '/tmp/.aevatar/roles.json',
      fileExists: true,
    },
    roleCatalogIsRemote: false,
    roleCatalogDirty: false,
    roleCatalogPending: false,
    roleCatalogNotice: null,
    roleImportPending: false,
    roleImportInputRef: React.createRef<HTMLInputElement>(),
    roleSearch: '',
    roleModalOpen: false,
    roleDraft: null,
    roleDraftMeta: {
      filePath: '',
      fileExists: false,
      updatedAtUtc: '',
    },
    selectedRole: roleCatalogDraft[0],
    connectors: [{ name: 'web-search' }],
    settingsProviders,
    onRoleSearchChange: jest.fn(),
    onOpenRoleModal: jest.fn(),
    onCloseRoleModal: jest.fn(),
    onRoleDraftChange: jest.fn(),
    onSubmitRoleDraft: jest.fn(),
    onRoleImportClick: jest.fn(),
    onRoleImportChange: jest.fn(),
    onSaveRoles: jest.fn(),
    onSelectRoleKey: jest.fn(),
    onDeleteRole: jest.fn(),
    onApplyRoleToWorkflow: jest.fn(),
    onUpdateRoleCatalog: jest.fn(),
    ...overrides,
  };
}

function createConnectorsProps(
  overrides: Partial<ConnectorsProps> = {},
): ConnectorsProps {
  return {
    connectors: {
      isLoading: false,
      isError: false,
      error: null,
      data: {
        connectors: [],
      },
    },
    appearanceTheme: 'blue',
    colorMode: 'light',
    connectorCatalogDraft,
    connectorCatalogMeta: {
      filePath: '/tmp/.aevatar/connectors.json',
      fileExists: true,
    },
    connectorCatalogIsRemote: false,
    connectorCatalogDirty: false,
    connectorCatalogPending: false,
    connectorImportPending: false,
    connectorCatalogNotice: null,
    connectorImportInputRef: React.createRef<HTMLInputElement>(),
    connectorSearch: '',
    connectorModalOpen: false,
    connectorDraft: null,
    connectorDraftMeta: {
      filePath: '',
      fileExists: false,
      updatedAtUtc: '',
    },
    selectedConnector: connectorCatalogDraft[0],
    onConnectorSearchChange: jest.fn(),
    onOpenConnectorModal: jest.fn(),
    onCloseConnectorModal: jest.fn(),
    onConnectorDraftChange: jest.fn(),
    onSubmitConnectorDraft: jest.fn(),
    onConnectorImportClick: jest.fn(),
    onConnectorImportChange: jest.fn(),
    onSaveConnectors: jest.fn(),
    onSelectConnectorKey: jest.fn(),
    onDeleteConnector: jest.fn(),
    onUpdateConnectorCatalog: jest.fn(),
    ...overrides,
  };
}

describe('Studio catalog pages', () => {
  it('updates and imports role entries without mounting the full Studio route', () => {
    const onSaveRoles = jest.fn();
    const onRoleImportChange = jest.fn();
    const onUpdateRoleCatalog = jest.fn();
    const props = createRolesProps({
      onSaveRoles,
      onRoleImportChange,
      onUpdateRoleCatalog,
    });

    const { container } = render(<StudioRolesPage {...props} />);

    fireEvent.change(screen.getByLabelText('System prompt'), {
      target: {
        value: 'Answer carefully and keep responses concise.',
      },
    });

    expect(onUpdateRoleCatalog).toHaveBeenCalled();
    expect(onUpdateRoleCatalog.mock.calls[0]?.[0]).toBe('role-1');
    expect(onUpdateRoleCatalog.mock.calls[0]?.[1](roleCatalogDraft[0])).toEqual(
      expect.objectContaining({
        systemPrompt: 'Answer carefully and keep responses concise.',
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSaveRoles).toHaveBeenCalledTimes(1);

    const file = new File(['{"roles":[]}'], 'roles-import.json', {
      type: 'application/json',
    });
    const importInput = container.querySelector('input[type="file"]');
    expect(importInput).toBeTruthy();

    fireEvent.change(importInput as HTMLInputElement, {
      target: {
        files: [file],
      },
    });

    expect(onRoleImportChange).toHaveBeenCalledTimes(1);
  });

  it('updates and imports connector entries without the full Studio bootstrap path', () => {
    const onSaveConnectors = jest.fn();
    const onConnectorImportChange = jest.fn();
    const onUpdateConnectorCatalog = jest.fn();
    const props = createConnectorsProps({
      onSaveConnectors,
      onConnectorImportChange,
      onUpdateConnectorCatalog,
    });

    const { container } = render(<StudioConnectorsPage {...props} />);

    fireEvent.change(screen.getByLabelText('Base URL'), {
      target: {
        value: 'https://console.example.test',
      },
    });

    expect(onUpdateConnectorCatalog).toHaveBeenCalled();
    expect(onUpdateConnectorCatalog.mock.calls[0]?.[0]).toBe('connector-1');
    expect(
      onUpdateConnectorCatalog.mock.calls[0]?.[1](connectorCatalogDraft[0]),
    ).toEqual(
      expect.objectContaining({
        http: expect.objectContaining({
          baseUrl: 'https://console.example.test',
        }),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(onSaveConnectors).toHaveBeenCalledTimes(1);

    const file = new File(['{"connectors":[]}'], 'connectors-import.json', {
      type: 'application/json',
    });
    const importInput = container.querySelector('input[type="file"]');
    expect(importInput).toBeTruthy();

    fireEvent.change(importInput as HTMLInputElement, {
      target: {
        files: [file],
      },
    });

    expect(onConnectorImportChange).toHaveBeenCalledTimes(1);
  });
});
