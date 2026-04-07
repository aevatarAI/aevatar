import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import type {
  StudioOrnnHealthResult,
  StudioOrnnSkillSearchResult,
  StudioProviderSettings,
  StudioProviderType,
  StudioRuntimeTestResult,
  StudioUserConfig,
  StudioUserConfigModelsResponse,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import { StudioSettingsPage } from './StudioWorkbenchSections';

type SettingsProps = React.ComponentProps<typeof StudioSettingsPage>;
type SettingsDraftUpdate = Parameters<SettingsProps['onSetSettingsDraft']>[0];

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

const userConfig: StudioUserConfig = {
  defaultModel: 'gpt-5.4-mini',
  runtimeBaseUrl: '',
};

const userConfigModels: StudioUserConfigModelsResponse = {
  providers: [
    {
      providerSlug: 'openai',
      providerName: 'OpenAI',
      status: 'ready',
      proxyUrl: 'https://nyx-api.example/openai',
    },
    {
      providerSlug: 'deepseek',
      providerName: 'DeepSeek',
      status: 'ready',
      proxyUrl: 'https://nyx-api.example/deepseek',
    },
  ],
  gatewayUrl: 'https://nyx-api.example/gateway',
  supportedModels: ['gpt-5.4-mini', 'deepseek-chat'],
};

const ornnHealth: StudioOrnnHealthResult = {
  baseUrl: 'https://ornn.chrono-ai.fun',
  reachable: true,
  message: 'Connected to Ornn.',
};

const ornnSkills: StudioOrnnSkillSearchResult = {
  baseUrl: 'https://ornn.chrono-ai.fun',
  total: 1,
  totalPages: 1,
  page: 1,
  pageSize: 100,
  items: [
    {
      guid: 'skill-1',
      name: 'ornn-search',
      description: 'Search Ornn for reusable skills.',
      isPrivate: false,
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
    userConfig: {
      isLoading: false,
      isFetching: false,
      isError: false,
      error: null,
      data: userConfig,
    },
    userConfigModels: {
      isLoading: false,
      isFetching: false,
      isError: false,
      error: null,
      data: userConfigModels,
    },
    settingsDraft: {
      runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
      defaultProviderName: 'tornado',
      providerTypes,
      providers,
    },
    userConfigDraft: userConfig,
    selectedProvider: providers[0],
    hostMode: 'proxy',
    workflowStorageMode: 'workspace',
    settingsDirty: true,
    settingsPending: false,
    userConfigDirty: true,
    userConfigPending: false,
    runtimeTestPending: false,
    settingsNotice: null,
    userConfigNotice: null,
    runtimeTestResult,
    ornnHealth: {
      isLoading: false,
      isFetching: false,
      isError: false,
      error: null,
      data: ornnHealth,
    },
    ornnSkills: {
      isLoading: false,
      isFetching: false,
      isError: false,
      error: null,
      data: ornnSkills,
    },
    directoryPath: '',
    directoryLabel: '',
    onSaveSettings: jest.fn(),
    onSaveUserConfig: jest.fn(),
    onTestRuntime: jest.fn(),
    onSetSettingsDraft: jest.fn(),
    onSetUserConfigDraft: jest.fn(),
    onRefreshOrnnHealth: jest.fn(),
    onRefreshOrnnSkills: jest.fn(),
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
  it('edits runtime settings through runtime actions and saves the draft', () => {
    let capturedDraft: NonNullable<SettingsProps['settingsDraft']> | null = null;
    const onSetSettingsDraft = jest.fn((updater: SettingsDraftUpdate) => {
      capturedDraft =
        typeof updater === 'function'
          ? updater({
              runtimeBaseUrl: 'https://aevatar-console-backend-api.aevatar.ai',
              defaultProviderName: 'tornado',
              providerTypes,
              providers,
            })
          : updater;
    });
    const onSaveSettings = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onSetSettingsDraft,
          onSaveSettings,
        })}
      />,
    );

    fireEvent.change(screen.getByLabelText('Studio runtime base URL'), {
      target: {
        value: 'http://127.0.0.1:5111',
      },
    });

    expect(onSetSettingsDraft).toHaveBeenCalledWith(expect.any(Function));
    expect(capturedDraft).toEqual(
      expect.objectContaining({
        runtimeBaseUrl: 'http://127.0.0.1:5111',
        defaultProviderName: 'tornado',
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Save runtime' }));

    expect(onSaveSettings).toHaveBeenCalledTimes(1);
  });

  it('shows CLI-style settings navigation and loads NyxID user config into LLM', () => {
    const onSetUserConfigDraft = jest.fn();
    const onSaveUserConfig = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onSaveUserConfig,
          onSetUserConfigDraft,
        })}
      />,
    );

    expect(screen.getByRole('button', { name: 'Runtime' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'LLM' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Skills' })).toBeInTheDocument();
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Studio provider API key')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'LLM' }));

    expect(screen.getByText('Connected providers')).toBeInTheDocument();
    expect(screen.getByText('Default model')).toBeInTheDocument();
    expect(screen.getByLabelText('Studio LLM default model')).toHaveValue('gpt-5.4-mini');
    expect(screen.getByText('OpenAI')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save config' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Save config' }));
    expect(onSaveUserConfig).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Toggle model list' }));
    fireEvent.click(screen.getByRole('button', { name: 'deepseek-chat' }));
    expect(onSetUserConfigDraft).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Provider catalog')).not.toBeInTheDocument();
  });

  it('renders the LLM config notice only once', () => {
    render(
      <StudioSettingsPage
        {...createBaseProps({
          userConfigNotice: {
            type: 'success',
            message: 'Saved LLM config.',
          },
        })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'LLM' }));

    expect(screen.getAllByText('LLM config updated')).toHaveLength(1);
    expect(screen.getAllByText('Saved LLM config.')).toHaveLength(1);
  });

  it('keeps runtime checks in Runtime and exposes live Ornn skills actions', () => {
    const onTestRuntime = jest.fn();
    const onRefreshOrnnHealth = jest.fn();
    const onRefreshOrnnSkills = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onTestRuntime,
          onRefreshOrnnHealth,
          onRefreshOrnnSkills,
        })}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(onTestRuntime).toHaveBeenCalledTimes(1);
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Skills' }));

    expect(screen.getByText('Ornn Platform')).toBeInTheDocument();
    expect(screen.getByText('Your Skills')).toBeInTheDocument();
    expect(screen.getByLabelText('Studio skills Ornn base URL')).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(onRefreshOrnnHealth).toHaveBeenCalledTimes(1);
    expect(onRefreshOrnnSkills).toHaveBeenCalledTimes(1);
    expect(screen.getByText('ornn-search')).toBeInTheDocument();
    expect(
      screen.getByText(/Agents automatically get/i),
    ).toBeInTheDocument();
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
