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
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
  },
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: { listWorkflows: jest.fn() },
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
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
};

const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  listWorkflows: jest.Mock;
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

const materializedWorkflow = {
  kind: 'materialized',
  workflow: {
    directoryId: 'directory-alpha',
    directoryLabel: 'Workflows',
    document: { name: 'incident_review', roles: [], steps: [] },
    draftExists: true,
    fileName: 'incident-review.yaml',
    filePath: '/workflows/incident-review.yaml',
    findings: [],
    name: 'Incident review',
    updatedAtUtc: '2026-08-06T10:00:00Z',
    workflowId: 'wf-created-alpha',
    yaml: 'name: incident_review\nroles: []\nsteps: []\n',
  },
} as const;

describe('New workflow save-target recovery', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);
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

  it('hides the only save target while still using its directory id', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
    expect(screen.queryByLabelText('Save to')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Save location')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident review' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
        expect.objectContaining({
          directoryId: 'directory-alpha',
          workflowName: 'Incident review',
          yaml: 'name: Incident_review\ndescription: \nroles: []\nsteps: []\n',
        }),
      ),
    );
    expect(mockStudioApi.authorWorkflow).not.toHaveBeenCalled();
    expect(mockStudioApi.parseYaml).not.toHaveBeenCalled();
  });

  it('shows Save to only when the workspace has multiple directories', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [
        readyWorkspace.directories[0],
        {
          directoryId: 'directory-beta',
          isBuiltIn: false,
          label: 'Operations',
          path: '/operations',
        },
      ],
    });
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
    const directorySelect = screen.getByLabelText('Save to');
    fireEvent.mouseDown(directorySelect);
    fireEvent.click(await screen.findByText('Operations'));
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident review' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
        expect.objectContaining({ directoryId: 'directory-beta' }),
      ),
    );
  });

  it('uses the first available YAML filename without changing the display name', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        directoryId: 'directory-alpha',
        fileName: 'incident-review.yaml',
        name: 'Other workflow',
      },
      {
        directoryId: 'directory-alpha',
        fileName: 'incident-review-2.yaml',
        name: 'Another workflow',
      },
      {
        directoryId: 'directory-beta',
        fileName: 'incident-review-3.yaml',
        name: 'Different directory workflow',
      },
    ]);
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident review' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
        expect.objectContaining({
          fileName: 'incident-review-3.yaml',
          workflowName: 'Incident review',
        }),
      ),
    );
  });

  it('generates, saves, and opens a described workflow with one action', async () => {
    const generatedYaml =
      'name: weekly_review\ndescription: Summarize the week.\nroles: []\nsteps: []\n';
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.authorWorkflow.mockResolvedValue(generatedYaml);
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'weekly_review', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
    const workflowName = screen.getByLabelText('Workflow name');
    expect(screen.queryByLabelText('Generated YAML')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('What should this workflow do?'), {
      target: { value: 'Summarize this week' },
    });
    const generateAndOpen = screen.getByRole('button', {
      name: 'Generate and open',
    });
    expect(generateAndOpen).toBeDisabled();
    fireEvent.change(workflowName, { target: { value: 'Weekly review' } });
    expect(generateAndOpen).toBeEnabled();
    fireEvent.click(generateAndOpen);

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith({
        directoryId: 'directory-alpha',
        fileName: 'weekly-review.yaml',
        scopeId: 'scope-alpha',
        workflowName: 'Weekly review',
        yaml: generatedYaml,
      }),
    );
    expect(mockStudioApi.authorWorkflow).toHaveBeenCalledWith(
      { prompt: 'Summarize this week' },
      expect.any(Object),
    );
    expect(mockStudioApi.parseYaml).toHaveBeenCalledWith({
      yaml: generatedYaml,
    });
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
    );
  });

  it('preserves the name and description and retries generation without an earlier save', async () => {
    const generatedYaml = 'name: weekly_review\nroles: []\nsteps: []\n';
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.authorWorkflow
      .mockRejectedValueOnce(new Error('Generation unavailable'))
      .mockResolvedValueOnce(generatedYaml);
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'weekly_review', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
    const workflowName = screen.getByLabelText('Workflow name');
    const description = screen.getByLabelText('What should this workflow do?');
    fireEvent.change(workflowName, {
      target: { value: 'Weekly review' },
    });
    fireEvent.change(description, {
      target: { value: 'Summarize this week' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

    expect(
      await screen.findByText("Workflow couldn't be created"),
    ).toBeVisible();
    expect(screen.getByText('Generation unavailable')).toBeInTheDocument();
    expect(workflowName).toHaveValue('Weekly review');
    expect(description).toHaveValue('Summarize this week');
    expect(mockStudioApi.createWorkflowDraft).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));
    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ workflowName: 'Weekly review' }),
    );
  });

  it('imports and opens YAML using the parsed document name', async () => {
    const importedYaml = 'name: imported_review\nroles: []\nsteps: []\n';
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'imported_review', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Import YAML' }));
    expect(screen.queryByLabelText('Workflow name')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow YAML'), {
      target: { value: importedYaml },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Import and open' }));

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
        expect.objectContaining({
          fileName: 'imported-review.yaml',
          workflowName: 'imported_review',
          yaml: importedYaml,
        }),
      ),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
    );
  });

  it('creates and opens an independently named template copy', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'incident_triage', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Use template' }),
    );
    expect(screen.queryByLabelText('Workflow name')).not.toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', { name: 'Use template and open' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
        expect.objectContaining({
          fileName: 'incident-triage-copy.yaml',
          workflowName: 'Incident triage copy',
        }),
      ),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
    );
  });

  it('keeps all three creation methods available after a network failure', async () => {
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
    for (const name of ['Describe', 'Import YAML', 'Use template']) {
      expect(screen.getByRole('button', { name })).toBeEnabled();
    }
    expect(
      screen.queryByRole('button', { name: 'Start blank' }),
    ).not.toBeInTheDocument();
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
    fireEvent.click(screen.getByRole('button', { name: 'Describe' }));
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Prepared workflow' },
    });
    expect(
      screen.getByRole('button', { name: 'Generate and open' }),
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
