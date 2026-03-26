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
    endpoint: 'http://127.0.0.1:5100',
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
  runtimeBaseUrl: 'http://127.0.0.1:5100',
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
    runtimeBaseUrl: 'http://127.0.0.1:5100',
    reachable: true,
    checkedUrl: 'http://127.0.0.1:5100/health',
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
      runtimeBaseUrl: 'http://127.0.0.1:5100',
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
  it('renders the provider catalog as stable cards and selects another provider', () => {
    const onSelectProviderName = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onSelectProviderName,
        })}
      />,
    );

    expect(screen.getByText('Provider catalog')).toBeInTheDocument();
    expect(screen.getByText('Provider detail')).toBeInTheDocument();
    expect(screen.getByText('Provider routing')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));

    expect(onSelectProviderName).toHaveBeenCalledWith('zephyr');
  });

  it('opens advanced settings and keeps runtime actions available', async () => {
    const onTestRuntime = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onTestRuntime,
        })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Advanced settings' }));

    expect(await screen.findByText('Runtime connection')).toBeInTheDocument();
    expect(screen.getAllByText('Workflow sources').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('button', { name: 'Test runtime' }));

    expect(onTestRuntime).toHaveBeenCalledTimes(1);
  });
});
