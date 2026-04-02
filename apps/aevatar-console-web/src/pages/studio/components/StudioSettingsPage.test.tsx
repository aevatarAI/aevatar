import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import type {
  StudioProviderSettings,
  StudioProviderType,
  StudioRuntimeTestResult,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import { StudioSettingsPage } from './StudioWorkbenchSections';

type SettingsProps = React.ComponentProps<typeof StudioSettingsPage>;

const providerTypes: StudioProviderType[] = [
  {
    id: 'openai',
    displayName: 'OpenAI',
    category: 'llm',
    description: 'OpenAI compatible provider',
    recommended: true,
    defaultEndpoint: 'https://api.openai.test',
    defaultModel: 'gpt-5.4-mini',
  },
];

const providers: StudioProviderSettings[] = [
  {
    providerName: 'tornado',
    providerType: 'openai',
    displayName: 'Tornado',
    category: 'llm',
    description: 'Default local provider',
    model: 'gpt-5.4',
    endpoint: 'https://aevatar-console-backend-api.aevatar.ai',
    apiKey: '',
    apiKeyConfigured: true,
  },
  {
    providerName: 'zephyr',
    providerType: 'openai',
    displayName: 'Zephyr',
    category: 'llm',
    description: 'Secondary provider',
    model: 'gpt-5.4-mini',
    endpoint: 'https://api.openai.test',
    apiKey: '',
    apiKeyConfigured: false,
  },
];

const workspaceSettings: StudioWorkspaceSettings = {
  runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
  directories: [
    {
      directoryId: 'dir-1',
      label: 'Workspace',
      path: '/tmp/workflows',
      isBuiltIn: false,
    },
  ],
};

function createBaseProps(overrides: Partial<SettingsProps> = {}): SettingsProps {
  const runtimeTestResult: StudioRuntimeTestResult = {
    runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
    reachable: true,
    checkedUrl: 'https://aevatar-console-backend-api.aevatar.ai/health',
    statusCode: 200,
    message: 'Runtime responded successfully',
  };

  return {
    workspaceSettings: {
      isLoading: false,
      isError: false,
      error: null,
      data: workspaceSettings,
    },
    settings: {
      isLoading: false,
      isError: false,
      error: null,
      data: {},
    },
    settingsDraft: {
      runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
      defaultProviderName: 'tornado',
      providerTypes,
      providers,
    },
    selectedProvider: providers[0],
    hostMode: 'proxy',
    workflowStorageMode: 'workspace',
    settingsDirty: true,
    settingsPending: false,
    runtimeTestPending: false,
    settingsNotice: null,
    runtimeTestResult,
    directoryPath: '',
    directoryLabel: '',
    onSaveSettings: jest.fn(),
    onTestRuntime: jest.fn(),
    onSetSettingsDraft: jest.fn(),
    onAddProvider: jest.fn(),
    onSelectProviderName: jest.fn(),
    onDeleteSelectedProvider: jest.fn(),
    onSetDefaultProvider: jest.fn(),
    onSetDirectoryPath: jest.fn(),
    onSetDirectoryLabel: jest.fn(),
    onAddDirectory: jest.fn(),
    onRemoveDirectory: jest.fn(),
    ...overrides,
  };
}

describe('StudioSettingsPage', () => {
  it('shows sectioned settings navigation and selects another provider', () => {
    const onSelectProviderName = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onSelectProviderName,
        })}
      />,
    );

    expect(screen.getByRole('tab', { name: 'Runtime' })).toBeInTheDocument();
    expect(
      screen.getByRole('tab', { name: 'AI Providers' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('tab', { name: 'Workflow Sources' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Advanced' })).toBeInTheDocument();
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Studio provider API key')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'AI Providers' }));

    expect(screen.getByText('Provider catalog')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

    expect(onSelectProviderName).toHaveBeenCalledWith('zephyr');
  });

  it('keeps provider connection and secrets in Advanced and runtime actions in Runtime', () => {
    const onTestRuntime = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onTestRuntime,
        })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Test runtime' }));

    expect(onTestRuntime).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Advanced' }));

    expect(screen.getByText('Workflow source management')).toBeInTheDocument();
    expect(screen.getByText('Provider connection')).toBeInTheDocument();
    expect(screen.getByText('Provider secrets')).toBeInTheDocument();
    expect(screen.getByLabelText('Studio provider endpoint')).toBeInTheDocument();
    expect(screen.getByLabelText('Studio provider API key')).toBeInTheDocument();
  });

  it('treats runtime editing as host-managed in embedded mode', () => {
    render(
      <StudioSettingsPage
        {...createBaseProps({
          hostMode: 'embedded',
        })}
      />,
    );

    expect(
      screen.getByText('Runtime is host-managed in embedded mode'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Studio runtime base URL')).toBeDisabled();
    expect(
      screen.getByRole('button', { name: 'Check host runtime' }),
    ).toBeInTheDocument();
  });
});
