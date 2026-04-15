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
    endpoint: '',
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

    fireEvent.click(screen.getByRole('button', { name: 'Save workspace settings' }));
    expect(onSaveSettings).toHaveBeenCalledTimes(1);
  });

  it('shows provider catalog controls while keeping connection settings in Advanced', () => {
    render(<StudioSettingsPage {...createBaseProps()} />);

    fireEvent.click(screen.getByRole('tab', { name: 'AI Providers' }));

    expect(screen.getByText('Provider catalog')).toBeInTheDocument();
    expect(screen.getByText('Provider detail')).toBeInTheDocument();
    expect(
      screen.queryByLabelText('Studio provider endpoint'),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByLabelText('Studio provider API key'),
    ).not.toBeInTheDocument();
  });

  it('opens Advanced to edit provider connection details and workflow sources', () => {
    const onSetSettingsDraft = jest.fn();

    render(
      <StudioSettingsPage
        {...createBaseProps({
          onSetSettingsDraft,
        })}
      />,
    );

    fireEvent.click(screen.getByRole('tab', { name: 'AI Providers' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open Advanced' }));

    expect(screen.getByText('Workflow source management')).toBeInTheDocument();
    expect(screen.getByText('Provider connection')).toBeInTheDocument();
    expect(screen.getByLabelText('Studio provider endpoint')).toHaveValue(
      'https://aevatar-console-backend-api.aevatar.ai',
    );

    fireEvent.change(screen.getByLabelText('Studio provider endpoint'), {
      target: { value: 'https://runtime.example' },
    });

    expect(onSetSettingsDraft).toHaveBeenCalledWith(expect.any(Function));
  });

  it('shows host-managed runtime copy in embedded mode', () => {
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
