import { fireEvent, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import NewWorkflowPage from './NewWorkflowPage';

jest.mock('@umijs/max', () => ({
  getIntl: () => ({
    formatMessage: (
      { defaultMessage, id }: { defaultMessage?: string; id: string },
      values?: Record<string, unknown>,
    ) =>
      (defaultMessage ?? id).replace(
        /\{(\w+)\}/g,
        (_match: string, key: string) => String(values?.[key] ?? ''),
      ),
  }),
  getLocale: () => 'en-US',
  history: {},
  setLocale: jest.fn(),
  useIntl: () => ({
    formatMessage: ({
      defaultMessage,
      id,
    }: {
      defaultMessage?: string;
      id: string;
    }) => defaultMessage ?? id,
  }),
  useModel: () => ({ initialState: { auth: { authenticated: true } } }),
}));

jest.mock('@/shared/studio/api', () => ({
  isStudioApiStatus: (error: unknown, status: number) =>
    Boolean(
      error &&
        typeof error === 'object' &&
        'status' in error &&
        error.status === status,
    ),
  studioApi: {
    authorWorkflow: jest.fn(),
    createWorkflowDraft: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    parseYaml: jest.fn(),
  },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock('../hooks/useDraftMaterialization', () => ({
  useDraftMaterialization: () => ({
    error: null,
    observe: jest.fn(),
    phase: 'idle',
    retry: jest.fn(),
  }),
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

const mockStudioApi = jest.requireMock('@/shared/studio/api').studioApi as {
  authorWorkflow: jest.Mock;
  createWorkflowDraft: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  parseYaml: jest.Mock;
};

const readyWorkspace = {
  runtimeBaseUrl: '',
  directories: [
    {
      directoryId: 'directory-alpha',
      isBuiltIn: true,
      label: 'Workflows',
      path: '/workflows',
    },
  ],
};

describe('New workflow save-target recovery', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  afterEach(() => cleanupTestQueryClients());

  it('allows method selection and input while save locations are loading', () => {
    mockStudioApi.getWorkspaceSettings.mockReturnValue(new Promise(() => {}));

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(screen.getByText('Loading save locations…')).toBeVisible();
    const importYaml = screen.getByRole('button', { name: 'Import YAML' });
    expect(importYaml).toBeEnabled();
    fireEvent.click(importYaml);
    fireEvent.change(screen.getByLabelText('Workflow YAML'), {
      target: { value: 'name: prepared_workflow' },
    });
    expect(screen.getByLabelText('Workflow YAML')).toHaveValue(
      'name: prepared_workflow',
    );
  });

  it('keeps every creation method available after a network failure', async () => {
    mockStudioApi.getWorkspaceSettings.mockRejectedValue(
      new TypeError('Failed to fetch'),
    );

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText(
        "The current workspace's save location couldn't be loaded.",
      ),
    ).toBeVisible();
    expect(
      screen.getByText(
        'Choose a creation method now. Your input stays on this page while you restore access.',
      ),
    ).toBeVisible();
    for (const name of [
      'Describe',
      'Start blank',
      'Import YAML',
      'Use template',
    ]) {
      expect(screen.getByRole('button', { name })).toBeEnabled();
    }
  });

  it('names an unauthorized save target and provides access recovery', async () => {
    mockStudioApi.getWorkspaceSettings.mockRejectedValue({ status: 403 });

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText(
        "You don't have access to a save location in the current workspace.",
      ),
    ).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Review access' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/settings',
    );
  });

  it('keeps preparation available but prevents implicit ownership when no directory exists', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [],
    });

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText(
        'No save location is available in the current workspace.',
      ),
    ).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Start blank' }));
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Prepared workflow' },
    });
    expect(
      screen.getByRole('button', { name: 'Create workflow' }),
    ).toBeDisabled();
    expect(mockStudioApi.createWorkflowDraft).not.toHaveBeenCalled();
  });

  it('preserves the selected method and input when retry discovers a save location', async () => {
    mockStudioApi.getWorkspaceSettings
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(readyWorkspace);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(
      await screen.findByText(
        "The current workspace's save location couldn't be loaded.",
      ),
    ).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Import YAML' }));
    fireEvent.change(screen.getByLabelText('Workflow YAML'), {
      target: { value: 'name: prepared_workflow' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() =>
      expect(mockStudioApi.getWorkspaceSettings).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(
        screen.queryByText(
          "The current workspace's save location couldn't be loaded.",
        ),
      ).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('heading', { name: 'Import YAML' })).toBeVisible();
    expect(screen.getByLabelText('Workflow YAML')).toHaveValue(
      'name: prepared_workflow',
    );
  });
});
