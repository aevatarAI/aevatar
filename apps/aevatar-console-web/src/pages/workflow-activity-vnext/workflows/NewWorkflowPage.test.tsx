import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../../tests/reactQueryTestUtils';
import NewWorkflowPage from './NewWorkflowPage';
import WorkflowTemplatesPage from './WorkflowTemplatesPage';

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
  isStudioApiErrorCode: jest.fn(),
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
    instantiateWorkflowTemplate: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeCatalogApi', () => ({
  runtimeCatalogApi: {
    listWorkflowTemplates: jest.fn(async () => ({
      items: [
        {
          templateId: 'template-incident-triage',
          displayName: 'Incident triage',
          description: 'Classify an incident.',
          defaultDraftName: 'Incident triage',
          authorityStateVersion: 7,
          stepCount: 2,
          requiredConnections: ['pagerduty'],
          requiresLlmProvider: true,
          freshness: {
            projectionWatermark: '2026-08-18T00:00:00Z',
            lastEventId: 'event-template-7',
            versionSemantics: 'workflow-catalog-authority-state-version',
          },
        },
      ],
      nextCursor: null,
      freshness: {
        projectionWatermark: '2026-08-18T00:00:00Z',
        lastEventId: 'event-template-7',
        versionSemantics: 'workflow-catalog-authority-state-version',
      },
    })),
    getWorkflowTemplate: jest.fn(),
  },
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: { listWorkflows: jest.fn() },
}));

jest.mock('@/shared/navigation/history', () => ({
  history: { push: jest.fn(), replace: jest.fn() },
}));

jest.mock('../hooks/useDraftMaterialization', () => ({
  useDraftMaterialization: jest.fn(),
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

const mockStudioApi = jest.requireMock('@/shared/studio/api').studioApi as {
  authorWorkflow: jest.Mock;
  createWorkflowDraft: jest.Mock;
  instantiateWorkflowTemplate: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
};
const mockIsStudioApiErrorCode = jest.requireMock('@/shared/studio/api')
  .isStudioApiErrorCode as jest.Mock;

const mockRuntimeCatalogApi = jest.requireMock('@/shared/api/runtimeCatalogApi')
  .runtimeCatalogApi as {
  getWorkflowTemplate: jest.Mock;
  listWorkflowTemplates: jest.Mock;
};

const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  listWorkflows: jest.Mock;
};

const mockUseDraftMaterialization = jest.requireMock(
  '../hooks/useDraftMaterialization',
).useDraftMaterialization as jest.Mock;

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

const acceptedTemplateReceipt = {
  accepted: true,
  workflowId: 'wf-created-alpha',
  commandId: 'cmd-template-alpha',
  ackStage: 'accepted',
  actorId: 'actor-workspace-alpha',
  workspaceId: 'workspace-scope-alpha',
  expectedVersion: 1,
  ackedAtUtc: '2026-08-18T00:00:00Z',
  readiness: {
    readable: false,
    stage: 'materializing',
    message: 'Draft accepted.',
  },
} as const;

describe('New workflow save-target recovery', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockIsStudioApiErrorCode.mockReturnValue(false);
    mockUseDraftMaterialization.mockReturnValue({
      error: null,
      observe: jest.fn(async () => ({ workflowId: 'wf-created-alpha' })),
      phase: 'idle',
      receipt: null,
      reset: jest.fn(),
      retry: jest.fn(),
    });
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);
  });

  afterEach(() => cleanupTestQueryClients());

  it('keeps the creation chooser quiet and usable while save locations load', () => {
    mockStudioApi.getWorkspaceSettings.mockReturnValue(new Promise(() => {}));

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(
      screen.queryByText('Loading save locations…'),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();
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
    expect(
      screen.queryByText('Generation unavailable'),
    ).not.toBeInTheDocument();
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

  it('treats a draft-create timeout as unconfirmed without resubmitting or guessing an identity', async () => {
    const importedYaml = 'name: imported_review\nroles: []\nsteps: []\n';
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockStudioApi.listWorkflowDrafts
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        {
          directoryId: 'directory-alpha',
          fileName: 'imported-review.yaml',
          name: 'imported_review',
          workflowId: 'wf-created-after-timeout',
        },
      ]);
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'imported_review', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.createWorkflowDraft.mockRejectedValue({
      message: 'POST workflow-drafts returned 504',
      status: 504,
    });

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(await screen.findByRole('button', { name: 'Import YAML' }));
    const yamlInput = screen.getByLabelText('Workflow YAML');
    fireEvent.change(yamlInput, { target: { value: importedYaml } });
    fireEvent.click(screen.getByRole('button', { name: 'Import and open' }));

    expect(
      await screen.findByText(
        "Workflow creation couldn't be confirmed. Check Workflows before trying again.",
      ),
    ).toBeVisible();
    expect(
      screen.queryByText("Workflow couldn't be created"),
    ).not.toBeInTheDocument();
    expect(yamlInput).toHaveValue(importedYaml);
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(2),
    );
    expect(history.push).not.toHaveBeenCalled();
  });

  it('navigates to the template browser from the creation chooser', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);

    renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Use template' }),
    );

    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates',
    );
    expect(screen.queryByText('Incident triage')).not.toBeInTheDocument();
  });

  it('uses the Activity pagination control for template pages', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);

    const pagination = await screen.findByTestId('activity-pagination');

    expect(pagination).toHaveClass('ant-pagination');
    expect(
      screen.queryByRole('button', { name: 'Previous' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Next' }),
    ).not.toBeInTheDocument();
    expect(
      pagination.querySelector('.ant-pagination-options-quick-jumper input'),
    ).not.toBeInTheDocument();
  });

  it('renders template facts in aligned table columns', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);

    const browser = await screen.findByRole('region', {
      name: 'Workflow templates',
    });
    const table = await within(browser).findByRole('table', {
      name: 'Workflow template catalogue',
    });
    const headers = within(table).getAllByRole('columnheader');
    const row = within(table).getByRole('row', { name: /Incident triage/ });
    const cells = within(row).getAllByRole('cell');

    expect(headers).toHaveLength(6);
    expect(
      ['Template', 'Reads', 'Connection', 'Does', 'Updated', 'Actions'].map(
        (name) => within(table).getByRole('columnheader', { name }),
      ),
    ).toEqual(headers);
    expect(cells).toHaveLength(6);
    expect(screen.getAllByText('Reads')).toHaveLength(1);
    expect(screen.getAllByText('Connection')).toHaveLength(1);
    expect(screen.getAllByText('Does')).toHaveLength(1);
    expect(within(cells[0]).getByText('Incident triage')).toBeInTheDocument();
    expect(within(cells[1]).getByText('Workflow inputs')).toBeInTheDocument();
    expect(
      within(cells[2]).getByText('LLM provider, pagerduty'),
    ).toBeInTheDocument();
    expect(within(cells[3]).getByText('Runs 2 steps')).toBeInTheDocument();
    expect(within(cells[4]).getByText('2026/08/18')).toBeInTheDocument();
    expect(
      within(cells[5]).getByRole('button', {
        name: 'View Incident triage',
      }),
    ).toBeInTheDocument();
    expect(
      within(cells[5]).getByRole('button', {
        name: 'Use template Incident triage',
      }),
    ).toBeInTheDocument();
  });

  it('lets vertical wheel input over the template table reach the page scroller', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);

    const catalogue = await screen.findByRole('region', {
      name: 'Workflow template catalogue',
    });
    const shellStyles = Array.from(document.querySelectorAll('style')).find(
      (style) => style.textContent?.includes('wa-vnext__template-table-region'),
    );

    expect(catalogue).toBeInTheDocument();
    expect(shellStyles?.textContent).toMatch(
      /\.wa-vnext__table-wrap\.wa-vnext__template-table-region \{[^}]*overscroll-behavior-y: auto;[^}]*\}/,
    );
  });

  it('opens a known template page with its saved cursor', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockRuntimeCatalogApi.listWorkflowTemplates
      .mockResolvedValueOnce({
        items: [
          {
            templateId: 'template-page-one',
            displayName: 'Page one template',
            description: 'First page.',
            defaultDraftName: 'Page one template',
            authorityStateVersion: 1,
            stepCount: 1,
            requiredConnections: [],
            requiresLlmProvider: false,
            freshness: {
              projectionWatermark: '2026-08-18T00:00:00Z',
              lastEventId: 'event-template-one',
              versionSemantics: 'workflow-catalog-authority-state-version',
            },
          },
        ],
        nextCursor: 'cursor-two',
        freshness: {
          projectionWatermark: '2026-08-18T00:00:00Z',
          lastEventId: 'event-template-one',
          versionSemantics: 'workflow-catalog-authority-state-version',
        },
      })
      .mockResolvedValueOnce({
        items: [
          {
            templateId: 'template-page-two',
            displayName: 'Page two template',
            description: 'Second page.',
            defaultDraftName: 'Page two template',
            authorityStateVersion: 2,
            stepCount: 1,
            requiredConnections: [],
            requiresLlmProvider: false,
            freshness: {
              projectionWatermark: '2026-08-18T00:00:00Z',
              lastEventId: 'event-template-two',
              versionSemantics: 'workflow-catalog-authority-state-version',
            },
          },
        ],
        nextCursor: null,
        freshness: {
          projectionWatermark: '2026-08-18T00:00:00Z',
          lastEventId: 'event-template-two',
          versionSemantics: 'workflow-catalog-authority-state-version',
        },
      });

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);
    expect(await screen.findByText('Page one template')).toBeInTheDocument();

    fireEvent.click(
      within(await screen.findByTestId('activity-pagination')).getByTitle('2'),
    );

    expect(await screen.findByText('Page two template')).toBeInTheDocument();
    expect(screen.queryByText('Page one template')).not.toBeInTheDocument();
    expect(
      mockRuntimeCatalogApi.listWorkflowTemplates,
    ).toHaveBeenLastCalledWith(
      expect.objectContaining({
        cursor: 'cursor-two',
        take: 12,
      }),
    );
  });

  it('views template details without creating, then uses the same instantiate action from the modal', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockRuntimeCatalogApi.getWorkflowTemplate.mockResolvedValue({
      template: {
        templateId: 'template-incident-triage',
        displayName: 'Incident triage',
        description: 'Classify an incident.',
        defaultDraftName: 'Incident triage',
        authorityStateVersion: 7,
        stepCount: 1,
        requiredConnections: [],
        requiresLlmProvider: false,
        freshness: {
          projectionWatermark: '2026-08-18T00:00:00Z',
          lastEventId: 'event-template-7',
          versionSemantics: 'workflow-catalog-authority-state-version',
        },
      },
      yaml: 'name: incident_triage\nsteps: []\n',
      definition: {
        name: 'incident_triage',
        description: 'Classify an incident.',
        closedWorldMode: true,
        roles: [],
        steps: [
          {
            id: 'classify',
            type: 'llm_call',
            targetRole: '',
            parameters: {},
            next: '',
            branches: {},
            children: [],
          },
        ],
      },
      edges: [],
      authorityStateVersion: 7,
      freshness: {
        projectionWatermark: '2026-08-18T00:00:00Z',
        lastEventId: 'event-template-7',
        versionSemantics: 'workflow-catalog-authority-state-version',
      },
    });

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Incident triage' }),
    );

    expect(await screen.findByText('Workflow preview')).toBeInTheDocument();
    expect(
      screen.queryByRole('tab', { name: 'Overview' }),
    ).not.toBeInTheDocument();
    expect(screen.getByTitle('classify')).toBeInTheDocument();
    expect(screen.queryByText('Step details')).not.toBeInTheDocument();
    expect(mockStudioApi.instantiateWorkflowTemplate).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Use this template' }));
    await waitFor(() =>
      expect(mockStudioApi.instantiateWorkflowTemplate).toHaveBeenCalledWith({
        expectedAuthorityStateVersion: 7,
        scopeId: 'scope-alpha',
        templateId: 'template-incident-triage',
      }),
    );
  });

  it('uses clear sort labels instead of exposing backend sort syntax', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);

    const sort = await screen.findByRole('combobox', {
      name: 'Sort templates',
    });
    fireEvent.mouseDown(sort);

    expect(screen.getByText('Sort by')).toBeInTheDocument();
    expect(await screen.findByText('Name: A to Z')).toBeInTheDocument();
    expect(screen.getByText('Name: Z to A')).toBeInTheDocument();
    expect(screen.getByText('Last updated: oldest first')).toBeInTheDocument();
  });

  it('refreshes stale modal details before retrying template instantiation', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockIsStudioApiErrorCode.mockImplementation(
      (_error: unknown, status: number, code: string) =>
        status === 409 && code === 'WORKFLOW_TEMPLATE_VERSION_CONFLICT',
    );
    const detailVersion7 = {
      template: {
        templateId: 'template-incident-triage',
        displayName: 'Incident triage',
        description: 'Classify an incident.',
        defaultDraftName: 'Incident triage',
        authorityStateVersion: 7,
        stepCount: 1,
        requiredConnections: [],
        requiresLlmProvider: false,
        freshness: {
          projectionWatermark: '2026-08-18T00:00:00Z',
          lastEventId: 'event-template-7',
          versionSemantics: 'workflow-catalog-authority-state-version',
        },
      },
      yaml: 'name: incident_triage\nsteps: []\n',
      definition: {
        name: 'incident_triage',
        description: 'Classify an incident.',
        closedWorldMode: true,
        roles: [],
        steps: [],
      },
      edges: [],
      authorityStateVersion: 7,
      freshness: {
        projectionWatermark: '2026-08-18T00:00:00Z',
        lastEventId: 'event-template-7',
        versionSemantics: 'workflow-catalog-authority-state-version',
      },
    };
    const detailVersion8 = {
      ...detailVersion7,
      authorityStateVersion: 8,
      template: {
        ...detailVersion7.template,
        authorityStateVersion: 8,
        freshness: {
          ...detailVersion7.template.freshness,
          lastEventId: 'event-template-8',
        },
      },
      freshness: {
        ...detailVersion7.freshness,
        lastEventId: 'event-template-8',
      },
    };
    mockRuntimeCatalogApi.getWorkflowTemplate
      .mockResolvedValueOnce(detailVersion7)
      .mockResolvedValueOnce(detailVersion8);
    mockStudioApi.instantiateWorkflowTemplate
      .mockRejectedValueOnce({
        status: 409,
        code: 'WORKFLOW_TEMPLATE_VERSION_CONFLICT',
      })
      .mockResolvedValueOnce(acceptedTemplateReceipt);

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Incident triage' }),
    );

    const dialog = await screen.findByRole('dialog');
    const useThisTemplate = within(dialog).getByRole('button', {
      name: 'Use this template',
    });
    await waitFor(() => expect(useThisTemplate).toBeEnabled());
    fireEvent.click(useThisTemplate);
    await waitFor(() =>
      expect(mockStudioApi.instantiateWorkflowTemplate).toHaveBeenCalledWith({
        expectedAuthorityStateVersion: 7,
        scopeId: 'scope-alpha',
        templateId: 'template-incident-triage',
      }),
    );

    expect(
      await within(dialog).findByText('Template is out of date'),
    ).toBeVisible();
    expect(
      within(dialog).getByRole('button', { name: 'Use this template' }),
    ).toBeDisabled();

    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Refresh catalog' }),
    );
    await waitFor(() =>
      expect(mockRuntimeCatalogApi.getWorkflowTemplate).toHaveBeenCalledTimes(
        2,
      ),
    );
    await waitFor(() =>
      expect(
        within(dialog).getByRole('button', { name: 'Use this template' }),
      ).toBeEnabled(),
    );
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Use this template' }),
    );

    await waitFor(() =>
      expect(
        mockStudioApi.instantiateWorkflowTemplate,
      ).toHaveBeenLastCalledWith({
        expectedAuthorityStateVersion: 8,
        scopeId: 'scope-alpha',
        templateId: 'template-incident-triage',
      }),
    );
  });

  it('keeps stale template submission disabled when detail refresh fails', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockIsStudioApiErrorCode.mockImplementation(
      (_error: unknown, status: number, code: string) =>
        status === 409 && code === 'WORKFLOW_TEMPLATE_VERSION_CONFLICT',
    );
    const detailVersion7 = {
      template: {
        templateId: 'template-incident-triage',
        displayName: 'Incident triage',
        description: 'Classify an incident.',
        defaultDraftName: 'Incident triage',
        authorityStateVersion: 7,
        stepCount: 1,
        requiredConnections: [],
        requiresLlmProvider: false,
        freshness: {
          projectionWatermark: '2026-08-18T00:00:00Z',
          lastEventId: 'event-template-7',
          versionSemantics: 'workflow-catalog-authority-state-version',
        },
      },
      yaml: 'name: incident_triage\nsteps: []\n',
      definition: {
        name: 'incident_triage',
        description: 'Classify an incident.',
        closedWorldMode: true,
        roles: [],
        steps: [],
      },
      edges: [],
      authorityStateVersion: 7,
      freshness: {
        projectionWatermark: '2026-08-18T00:00:00Z',
        lastEventId: 'event-template-7',
        versionSemantics: 'workflow-catalog-authority-state-version',
      },
    };
    mockRuntimeCatalogApi.getWorkflowTemplate
      .mockResolvedValueOnce(detailVersion7)
      .mockRejectedValueOnce(new Error('Catalog refresh unavailable'));
    mockStudioApi.instantiateWorkflowTemplate.mockRejectedValueOnce({
      status: 409,
      code: 'WORKFLOW_TEMPLATE_VERSION_CONFLICT',
    });

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);
    fireEvent.click(
      await screen.findByRole('button', { name: 'View Incident triage' }),
    );

    const dialog = await screen.findByRole('dialog');
    const useThisTemplate = within(dialog).getByRole('button', {
      name: 'Use this template',
    });
    await waitFor(() => expect(useThisTemplate).toBeEnabled());
    fireEvent.click(useThisTemplate);
    expect(
      await within(dialog).findByText('Template is out of date'),
    ).toBeVisible();

    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Refresh catalog' }),
    );
    await waitFor(() =>
      expect(mockRuntimeCatalogApi.getWorkflowTemplate).toHaveBeenCalledTimes(
        2,
      ),
    );
    expect(
      within(dialog).getByRole('button', { name: 'Use this template' }),
    ).toBeDisabled();
  });

  it.each([
    'delayed',
    'failed',
  ] as const)('prevents duplicate template instantiation while the accepted draft is %s', async (phase) => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue(readyWorkspace);
    mockUseDraftMaterialization.mockReturnValue({
      error: phase === 'failed' ? new Error('Observation unavailable') : null,
      observe: jest.fn(),
      phase,
      receipt: acceptedTemplateReceipt,
      reset: jest.fn(),
      retry: jest.fn(),
    });

    renderWithQueryClient(<WorkflowTemplatesPage scopeId="scope-alpha" />);

    const instantiate = await screen.findByRole('button', {
      name: 'Use template Incident triage',
    });
    expect(instantiate).toBeDisabled();
    expect(instantiate).not.toHaveClass('ant-btn-loading');
    fireEvent.click(instantiate);
    expect(mockStudioApi.instantiateWorkflowTemplate).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
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
