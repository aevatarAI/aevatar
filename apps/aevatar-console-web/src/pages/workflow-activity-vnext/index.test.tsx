import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from './index';

let mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';

const readMockUrl = () => new URL(mockLocation, 'http://console.local');

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
  useLocation: () => ({
    hash: '',
    pathname: readMockUrl().pathname,
    search: readMockUrl().search,
  }),
  useModel: () => ({ initialState: { auth: { authenticated: true } } }),
  useParams: () => ({ scopeId: 'scope-alpha' }),
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
    deleteWorkflowDraft: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    getAuthSession: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    getWorkflow: jest.fn(),
    getWorkflowDraftFile: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
    saveWorkflow: jest.fn(),
    saveUserLlmSettings: jest.fn(),
    serializeYaml: jest.fn(),
  },
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    getWorkflowDetail: jest.fn(),
    listWorkflows: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamDraftRun: jest.fn(),
  },
}));

jest.mock('@/pages/settings/userLlmSaveObservation', () => ({
  observeUserLlmSave: jest.fn(),
}));

jest.mock('@/shared/navigation/history', () => ({
  getLocationSnapshot: () => `${readMockUrl().pathname}${readMockUrl().search}`,
  history: { push: jest.fn(), replace: jest.fn() },
  subscribeToLocationChanges: () => () => undefined,
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: () => <button type="button">Account</button>,
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock(
  '@/pages/team-member-workflow-studio/components/WorkflowStudioCanvas',
  () => ({
    __esModule: true,
    default: ({
      nodes,
      onNodeSelect,
    }: {
      nodes: readonly { readonly id: string }[];
      onNodeSelect?: (nodeId: string) => void;
    }) => (
      <div data-testid="workflow-studio-canvas">
        {nodes.map((node) => (
          <button
            key={node.id}
            onClick={() => onNodeSelect?.(node.id)}
            type="button"
          >
            Select {node.id}
          </button>
        ))}
      </div>
    ),
  }),
);

jest.mock(
  '@/pages/team-member-workflow-studio/components/WorkflowStudioNodeDetailPanel',
  () => ({
    __esModule: true,
    default: ({
      onConfigurationChange,
      stepDraft,
    }: {
      onConfigurationChange: (parametersText: string) => void;
      stepDraft: { readonly id: string } | null;
    }) =>
      stepDraft ? (
        <section aria-label="Node configuration">
          <span>Configuring {stepDraft.id}</span>
          <button
            onClick={() =>
              onConfigurationChange('{"prompt_prefix":"Updated prompt"}')
            }
            type="button"
          >
            Apply node configuration
          </button>
        </section>
      ) : null,
  }),
);

const mockStudioApi = jest.requireMock('@/shared/studio/api').studioApi as {
  authorWorkflow: jest.Mock;
  createWorkflowDraft: jest.Mock;
  deleteWorkflowDraft: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  getAuthSession: jest.Mock;
  getUserConfigRuntime: jest.Mock;
  getUserLlmSettings: jest.Mock;
  getWorkflow: jest.Mock;
  getWorkflowDraftFile: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
  saveWorkflow: jest.Mock;
  saveUserLlmSettings: jest.Mock;
  serializeYaml: jest.Mock;
};
const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  getWorkflowDetail: jest.Mock;
  listWorkflows: jest.Mock;
};
const mockRuntimeRunsApi = jest.requireMock('@/shared/api/runtimeRunsApi')
  .runtimeRunsApi as {
  streamDraftRun: jest.Mock;
};
const mockObserveUserLlmSave = jest.requireMock(
  '@/pages/settings/userLlmSaveObservation',
).observeUserLlmSave as jest.Mock;

describe('Workflow Activity vNext catalogue', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';
    jest.clearAllMocks();
  });

  afterEach(() => cleanupTestQueryClients());

  it('renders authoritative draft and committed rows, searches, and navigates by the real workflow id', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: null,
      source: {
        workflowYaml: '',
        definitionActorId: 'definition-beta',
        inlineWorkflowYamls: null,
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(screen.getByText('Loading workflows')).toBeInTheDocument();
    expect(await screen.findByText('Support triage')).toBeInTheDocument();
    expect(screen.getByText('Invoice review')).toBeInTheDocument();

    const workflowRegion = screen.getByRole('region', { name: 'Workflows' });
    expect(workflowRegion).toHaveAttribute('tabindex', '0');
    expect(
      within(workflowRegion).getByText('Support triage').closest('td'),
    ).toHaveAttribute('data-label', 'Workflow');

    fireEvent.change(
      screen.getByRole('searchbox', { name: 'Search workflows' }),
      {
        target: { value: 'invoice' },
      },
    );
    expect(screen.queryByText('Support triage')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Activity' }));
    await waitFor(() => {
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledWith(
        'scope-alpha',
        'wf-committed-beta',
      );
      expect(history.push).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/activity?definition=definition-beta',
      );
    });

    fireEvent.click(
      screen.getByRole('button', { name: 'Open Invoice review' }),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-beta',
    );
  });

  it('opens unfiltered Activity with an unavailable notice for a draft-only row', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Support triage')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Activity' }));
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowFilter=unavailable',
    );
  });

  it('runs an exact draft from the editor and omits Run for committed-only rows', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const draftRow = (await screen.findByText('Support triage')).closest('tr');
    const committedRow = screen.getByText('Invoice review').closest('tr');
    expect(draftRow).not.toBeNull();
    expect(committedRow).not.toBeNull();
    const runDraft = within(draftRow as HTMLElement).getByRole('button', {
      name: 'Run Support triage',
    });
    expect(runDraft).toBeEnabled();
    expect(
      within(committedRow as HTMLElement).queryByRole('button', {
        name: /Run/,
      }),
    ).not.toBeInTheDocument();

    fireEvent.click(runDraft);
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha?run=1',
    );
  });

  it('deletes only the editable draft and refreshes authoritative draft membership', async () => {
    let draftRows = [
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ];
    mockStudioApi.listWorkflowDrafts.mockImplementation(async () => draftRows);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Committed support source',
        serviceKey: 'svc-alpha',
        workflowName: 'committed_support_source',
        actorId: 'definition-alpha',
        activeRevisionId: 'revision-alpha',
        deploymentId: 'deployment-alpha',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);
    mockStudioApi.deleteWorkflowDraft.mockImplementation(async () => {
      draftRows = [];
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const draftName = await screen.findByText('Support triage');
    const row = draftName.closest('tr');
    expect(row).not.toBeNull();
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    expect(screen.getByText('Delete editable draft?')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    await waitFor(() =>
      expect(mockStudioApi.deleteWorkflowDraft).toHaveBeenCalledWith(
        'wf-draft-alpha',
        'scope-alpha',
      ),
    );
    await waitFor(() =>
      expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(2),
    );
    expect(
      await screen.findByText('Committed support source'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('button', {
        name: 'More actions for Committed support source',
      }),
    ).not.toBeInTheDocument();
  });

  it('keeps the draft and offers retry when deletion fails', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);
    mockStudioApi.deleteWorkflowDraft.mockRejectedValue(
      new Error('DELETE returned 503'),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const draftName = await screen.findByText('Support triage');
    const row = draftName.closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    expect(
      await screen.findByText("Draft couldn't be deleted"),
    ).toBeInTheDocument();
    expect(screen.getByText('DELETE returned 503')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled();
    expect(screen.getByText('Support triage')).toBeInTheDocument();
    expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(1);
  });

  it('keeps successful rows and names the failed source', async () => {
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(
      new Error('draft source down'),
    );
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'definition-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Invoice review')).toBeInTheDocument();
    expect(
      screen.getByText("Some workflows couldn't be loaded"),
    ).toBeInTheDocument();
    expect(screen.queryByText(/catalogue/i)).not.toBeInTheDocument();
    expect(screen.queryByText('No workflows yet')).not.toBeInTheDocument();
  });

  it('renders a successful empty result', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
  });

  it('restores URL search and filters Drafts by exact draft API membership', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?q=support&view=drafts';
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Support triage',
        description: 'Route support requests',
        fileName: 'support.yaml',
        filePath: '/support.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Committed support source',
        serviceKey: '',
        workflowName: 'committed_support_source',
        actorId: 'summary-actor-alpha',
        activeRevisionId: 'revision-alpha',
        deploymentId: 'deployment-alpha',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Support triage')).toBeInTheDocument();
    expect(screen.queryByText('Invoice review')).not.toBeInTheDocument();
    expect(
      screen.getByRole('searchbox', { name: 'Search workflows' }),
    ).toHaveValue('support');
    expect(screen.getByText('Drafts')).toBeInTheDocument();

    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: 'Workflow view' }),
    );
    expect(
      await screen.findByRole('option', { name: 'All workflows' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Drafts' })).toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'Committed' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'Published' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'Failing' }),
    ).not.toBeInTheDocument();
  });

  it('writes Workflow filters to the URL and clears a filtered empty result', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    expect(await screen.findByText('Invoice review')).toBeInTheDocument();

    fireEvent.change(
      screen.getByRole('searchbox', { name: 'Search workflows' }),
      { target: { value: 'missing' } },
    );

    expect(
      await screen.findByText('No matching workflows'),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows?q=missing',
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(await screen.findByText('Invoice review')).toBeInTheDocument();
    await waitFor(() =>
      expect(history.replace).toHaveBeenLastCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows',
      ),
    );
  });

  it('shows Drafts as unavailable instead of empty when the draft API fails', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=drafts';
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(
      new Error('draft source down'),
    );
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-beta',
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-beta',
        activeRevisionId: 'revision-beta',
        deploymentId: 'deployment-beta',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('Draft workflows unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Retry workflows' }),
    ).toBeEnabled();
    expect(screen.queryByText('No workflows yet')).not.toBeInTheDocument();
  });

  it('renders total source failure and supports retry', async () => {
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(new Error('offline'));
    mockScopesApi.listWorkflows.mockRejectedValue(new Error('offline'));
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('Workflows unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Retry workflows' }),
    ).toBeEnabled();
  });
});

describe('Workflow Activity vNext settings', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/settings';
    jest.clearAllMocks();
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: null,
      savedRouteLabel: 'System default',
      selectionStatus: 'system_default',
      catalogDiagnostic: 'unspecified',
      remediation: 'none',
      routeOptions: [],
      modelGroupsByRoute: [],
      catalogStatus: 'empty',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
    });
    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      providerDisplayName: 'NyxID',
      profile: {
        subject: 'user-subject-alpha',
        name: 'Ada Operator',
        email: 'ada@example.test',
        emailVerified: true,
        picture: null,
        roles: ['operator'],
        groups: ['platform'],
      },
      session: {
        authenticated: true,
        scopeId: 'scope-alpha',
        scopeSource: 'nyxid-session',
        expiresAtUtc: '2026-08-05T10:00:00Z',
      },
    });
    mockStudioApi.getUserConfigRuntime.mockResolvedValue({
      runtimeMode: 'remote',
      activeRuntimeBaseUrl: 'https://runtime.example.test',
      localRuntimeBaseUrl: 'http://localhost:5100',
      remoteRuntimeBaseUrl: 'https://runtime.example.test',
      runtimeDefaults: {
        localRuntimeBaseUrl: 'http://localhost:5100',
        remoteRuntimeBaseUrl: 'https://runtime.example.test',
        localMode: 'local',
        remoteMode: 'remote',
      },
    });
    mockObserveUserLlmSave.mockResolvedValue({ phase: 'observed' });
  });

  afterEach(() => cleanupTestQueryClients());

  it('renders account facts while keeping runtime connection values behind technical details', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      (await screen.findAllByText('System default')).length,
    ).toBeGreaterThan(0);
    const accountLink = screen.getByRole('link', { name: 'Account' });
    expect(accountLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account',
    );
    fireEvent.click(accountLink);
    expect(accountLink).toHaveAttribute('aria-current', 'page');
    expect(await screen.findByText('Ada Operator')).toBeInTheDocument();
    expect(screen.getByText('NyxID')).toBeInTheDocument();
    expect(screen.getByText('operator')).toBeInTheDocument();
    expect(screen.queryByText('user-subject-alpha')).not.toBeInTheDocument();
    expect(screen.queryByText('platform')).not.toBeInTheDocument();
    expect(screen.queryByText('nyxid-session')).not.toBeInTheDocument();
    expect(screen.queryByText('scope-alpha')).not.toBeInTheDocument();
    expect(
      screen.getByText(
        new Intl.DateTimeFormat('en-US', {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(new Date('2026-08-05T10:00:00Z')),
      ),
    ).toBeInTheDocument();

    const advancedLink = screen.getByRole('link', { name: 'Advanced' });
    fireEvent.click(advancedLink);
    expect(advancedLink).toHaveAttribute('aria-current', 'page');
    expect(
      screen.getAllByText('https://runtime.example.test')[0],
    ).not.toBeVisible();
    fireEvent.click(screen.getByText('Technical details'));
    expect(
      await screen.findAllByText('https://runtime.example.test'),
    ).toHaveLength(2);
    expect(screen.getByText('remote')).toBeInTheDocument();
  });

  it('keeps an AI defaults decoding failure compact and actionable', async () => {
    mockStudioApi.getUserLlmSettings.mockRejectedValue(
      new Error(
        'StudioUserLlmSettings.savedSelection.modelSelection must be an object.',
      ),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('AI defaults unavailable'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Try loading this section again.'),
    ).toBeInTheDocument();
    expect(screen.getByText('Technical details')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
  });

  it('explains service and model inheritance for System default', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByRole('combobox', { name: 'Preferred service' });
    expect(screen.getAllByText('System default').length).toBeGreaterThan(0);
    expect(screen.getByText('Default model')).toBeInTheDocument();
    expect(
      screen.getByText('Uses the system-selected service and model.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('combobox', { name: 'Default model' }),
    ).not.toBeInTheDocument();
  });

  it('keeps connected services selectable without an enumerated model catalogue', async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: {
        routeKind: 'gateway',
        routeValue: '/api/v1/llm/gateway/v1',
        modelSelection: { kind: 'provider_default' },
      },
      savedRouteLabel: 'Gateway',
      selectionStatus: 'ready',
      catalogDiagnostic: 'unspecified',
      remediation: 'none',
      catalogStatus: 'ready',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
      routeOptions: [
        {
          routeValue: '/api/v1/llm/gateway/v1',
          label: 'Gateway',
          source: 'gateway_provider',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: null,
          serviceSlug: null,
          modelCatalog: {
            certainty: 'not_verifiable',
            modelIds: [],
            defaultModelId: null,
            diagnostic: 'not_published',
          },
          description: null,
        },
        {
          routeValue: '/api/v1/proxy/s/service-alpha',
          label: 'Service alpha',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: 'us-alpha',
          serviceSlug: 'service-alpha',
          modelCatalog: {
            certainty: 'enumerated',
            modelIds: ['model-alpha'],
            defaultModelId: 'model-alpha',
            diagnostic: 'unspecified',
          },
          description: null,
        },
        {
          routeValue: '/api/v1/proxy/s/storage-alpha',
          label: 'Storage alpha',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: 'us-storage-alpha',
          serviceSlug: 'storage-alpha',
          modelCatalog: {
            certainty: 'not_verifiable',
            modelIds: [],
            defaultModelId: null,
            diagnostic: 'not_published',
          },
          description: null,
        },
      ],
      modelGroupsByRoute: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const routeSelect = await screen.findByRole('combobox', {
      name: 'Preferred service',
    });
    expect(
      screen.queryByRole('combobox', { name: 'Default model' }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByText('Uses the service default model.'),
    ).toBeInTheDocument();
    fireEvent.mouseDown(routeSelect);
    expect(await screen.findByText('Service alpha')).toBeInTheDocument();
    fireEvent.click(await screen.findByText('Storage alpha'));
    expect(
      screen.queryByRole('combobox', { name: 'Default model' }),
    ).not.toBeInTheDocument();
    expect(
      screen.getByText('Uses the service default model.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();
    fireEvent.click(
      screen.getByRole('button', { name: 'Restore saved settings' }),
    );
    expect(
      screen.queryByRole('button', { name: 'Save changes' }),
    ).not.toBeInTheDocument();
    expect(mockStudioApi.saveUserLlmSettings).not.toHaveBeenCalled();
  });

  it('guards dirty navigation with Stay and Discard and leave', async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: null,
      savedRouteLabel: 'System default',
      selectionStatus: 'system_default',
      catalogDiagnostic: 'unspecified',
      remediation: 'none',
      catalogStatus: 'ready',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
      routeOptions: [
        {
          routeValue: '/api/v1/proxy/s/service-alpha',
          label: 'Service alpha',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: 'us-alpha',
          serviceSlug: 'service-alpha',
          modelCatalog: {
            certainty: 'enumerated',
            modelIds: ['model-alpha'],
            defaultModelId: 'model-alpha',
            diagnostic: 'unspecified',
          },
          description: null,
        },
      ],
      modelGroupsByRoute: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const routeSelect = await screen.findByRole('combobox', {
      name: 'Preferred service',
    });
    fireEvent.mouseDown(routeSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(screen.getAllByRole('link', { name: 'Workflows' })[0]);

    expect(screen.getByText('Unsaved AI default changes')).toBeInTheDocument();
    expect(history.push).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(history.push).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled();

    fireEvent.click(screen.getAllByRole('link', { name: 'Workflows' })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Discard and leave' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows',
    );
    expect(mockStudioApi.saveUserLlmSettings).not.toHaveBeenCalled();
  });

  it('waits for authoritative save observation before leaving Settings', async () => {
    mockStudioApi.getUserLlmSettings.mockResolvedValue({
      savedSelection: null,
      savedRouteLabel: 'System default',
      selectionStatus: 'system_default',
      catalogDiagnostic: 'unspecified',
      remediation: 'none',
      catalogStatus: 'ready',
      capabilities: {
        canEditRoute: true,
        canEditModel: true,
        canSave: true,
        canRetryCatalog: true,
      },
      routeOptions: [
        {
          routeValue: '/api/v1/proxy/s/service-alpha',
          label: 'Service alpha',
          source: 'user_service',
          status: 'ready',
          allowed: true,
          ready: true,
          userServiceId: 'us-alpha',
          serviceSlug: 'service-alpha',
          modelCatalog: {
            certainty: 'enumerated',
            modelIds: ['model-alpha'],
            defaultModelId: 'model-alpha',
            diagnostic: 'unspecified',
          },
          description: null,
        },
      ],
      modelGroupsByRoute: [],
    });
    mockStudioApi.saveUserLlmSettings.mockResolvedValue({
      accepted: true,
      commandId: 'command-alpha',
      ackStage: 'accepted',
      actorId: 'config-actor-alpha',
      correlationId: 'correlation-alpha',
      ackedAtUtc: '2026-08-05T10:00:00Z',
    });
    let observeSaved: ((value: { phase: 'observed' }) => void) | undefined;
    mockObserveUserLlmSave.mockReturnValue(
      new Promise((resolve) => {
        observeSaved = resolve;
      }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const routeSelect = await screen.findByRole('combobox', {
      name: 'Preferred service',
    });
    fireEvent.mouseDown(routeSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(screen.getAllByRole('link', { name: 'Workflows' })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Save and leave' }));

    await waitFor(() =>
      expect(mockStudioApi.saveUserLlmSettings).toHaveBeenCalledTimes(1),
    );
    expect(history.push).not.toHaveBeenCalled();
    observeSaved?.({ phase: 'observed' });
    await waitFor(() =>
      expect(history.push).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows',
      ),
    );
  });
});

describe('Workflow Activity vNext editor', () => {
  beforeEach(() => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source';
    jest.clearAllMocks();
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [
        {
          directoryId: 'directory-alpha',
          label: 'Workflows',
          path: '/workflows',
          isBuiltIn: true,
        },
      ],
    });
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Original prompt\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [
          {
            id: 'step-root',
            type: 'llm_call',
            parameters: { prompt_prefix: 'Original prompt' },
          },
        ],
      },
      draftExists: false,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'committed_source', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      document: { name: 'committed_source', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.saveWorkflow.mockResolvedValue({
      kind: 'materialized',
      workflow: {
        workflowId: 'wf-draft-new',
        name: 'Committed source',
        fileName: 'committed-source.yaml',
        filePath: '/workflows/committed-source.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        yaml: 'name: committed_source\nroles: []\nsteps: []\n',
        updatedAtUtc: '2026-08-04T10:01:00Z',
        document: { name: 'committed_source', roles: [], steps: [] },
        draftExists: true,
        findings: [],
      },
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it('creates on first save for committed-only source and adopts the API-returned draft id', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(
        'Committed source · first Save creates a scoped draft',
      ),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('YAML'));
    fireEvent.change(screen.getByLabelText('Workflow YAML'), {
      target: { value: 'name: committed_source\nroles: []\nsteps: []\n\n' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.saveWorkflow).toHaveBeenCalledWith(
      expect.objectContaining({
        draftExists: false,
        workflowId: 'wf-committed-source',
        directoryId: 'directory-alpha',
      }),
    );
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-new',
    );
  });

  it('parses authoritative YAML when the workflow response omits its document', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-imported',
      name: 'Imported workflow',
      fileName: 'imported.yaml',
      filePath: '/workflows/imported.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: imported\nsteps:\n  - id: step-root\n    type: llm_call\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: null,
      draftExists: true,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'imported',
        roles: [],
        steps: [{ id: 'step-root', type: 'llm_call' }],
      },
      findings: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    ).toBeInTheDocument();
    expect(mockStudioApi.parseYaml).toHaveBeenCalledWith({
      yaml: 'name: imported\nsteps:\n  - id: step-root\n    type: llm_call\n',
    });
    expect(screen.getByRole('button', { name: 'Run' })).toBeEnabled();
  });

  it('renders loaded nodes inside the sized Workflow Studio canvas region', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const canvasRegion = await screen.findByRole('region', {
      name: 'Workflow canvas',
    });

    expect(canvasRegion).toHaveStyle({
      display: 'flex',
      minHeight: '440px',
    });
    const canvas = within(canvasRegion).getByTestId('workflow-studio-canvas');
    expect(canvas.parentElement).toHaveStyle({
      display: 'flex',
      height: '100%',
      minHeight: '0',
      width: '100%',
    });
    expect(
      within(canvas).getByRole('button', {
        name: 'Select step:step-root',
      }),
    ).toBeInTheDocument();
  });

  it('opens the existing test run panel after a valid requested draft loads', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha?run=1';
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Support triage',
      fileName: 'support.yaml',
      filePath: '/support.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: support\nsteps:\n  - id: step-root\n    type: llm_call\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: {
        name: 'support',
        roles: [],
        steps: [{ id: 'step-root', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByRole('region', { name: 'Test run' }),
    ).toBeInTheDocument();
    expect(mockRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it('does not open or submit a requested run for an invalid draft', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha?run=1';
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Empty draft',
      fileName: 'empty.yaml',
      filePath: '/empty.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: empty\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: { name: 'empty', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByDisplayValue('Empty draft')).toBeInTheDocument();
    expect(
      screen.queryByRole('region', { name: 'Test run' }),
    ).not.toBeInTheDocument();
    expect(mockRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it("keeps a save failure's server detail out of the primary editor message", async () => {
    mockStudioApi.saveWorkflow.mockRejectedValue(
      new Error('PUT /api/studio/workflows/wf-committed-source returned 500'),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    expect(
      await screen.findByText("Workflow couldn't be saved"),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(
        'PUT /api/studio/workflows/wf-committed-source returned 500',
      ),
    ).not.toBeVisible();
  });

  it('edits a selected canvas node through the shared document state', async () => {
    mockStudioApi.serializeYaml.mockImplementation(async ({ document }) => ({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Updated prompt\n',
      document,
      findings: [],
    }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    expect(screen.getByText('Configuring step-root')).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('button', { name: 'Apply node configuration' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
        document: expect.objectContaining({
          steps: [
            expect.objectContaining({
              id: 'step-root',
              parameters: { prompt_prefix: 'Updated prompt' },
            }),
          ],
        }),
      }),
    );
  });

  it('offers Save, Discard, and Stay before vNext navigation with unsaved changes', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Unsaved title' },
    });
    fireEvent.click(screen.getAllByRole('link', { name: 'Activity' })[0]);

    expect(history.push).not.toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity',
    );
    expect(
      screen.getByRole('dialog', { name: 'Unsaved workflow changes' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Save and leave' }),
    ).toBeEnabled();
    expect(
      screen.getByRole('button', { name: 'Discard and leave' }),
    ).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Stay' }));
    expect(
      screen.queryByRole('dialog', { name: 'Unsaved workflow changes' }),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('link', { name: 'Activity' })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Discard and leave' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity',
    );
  });
});

describe('Workflow Activity vNext creation', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows/new';
    jest.clearAllMocks();
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [
        {
          directoryId: 'directory-alpha',
          label: 'Workflows',
          path: '/workflows',
          isBuiltIn: true,
        },
      ],
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it('creates a blank draft with a server directory and navigates only after materialization', async () => {
    mockStudioApi.createWorkflowDraft.mockResolvedValue({
      kind: 'materialized',
      workflow: {
        workflowId: 'wf-created-alpha',
        name: 'Incident review',
        fileName: 'incident-review.yaml',
        filePath: '/workflows/incident-review.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        yaml: 'name: incident_review\nroles: []\nsteps: []\n',
        updatedAtUtc: '2026-08-04T10:00:00Z',
        document: { name: 'incident_review', roles: [], steps: [] },
        draftExists: true,
        findings: [],
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    expect(
      screen.getByText('Choose how you want to start.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/persisted workflow draft/i),
    ).not.toBeInTheDocument();
    const blankButton = await screen.findByRole('button', {
      name: 'Start blank',
    });
    await waitFor(() => expect(blankButton).toBeEnabled());
    fireEvent.click(blankButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident review' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({
        directoryId: 'directory-alpha',
        scopeId: 'scope-alpha',
      }),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
    );
  });

  it('does not expose the scope id as the built-in save location label', async () => {
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [
        {
          directoryId: 'directory-alpha',
          label: 'scope-alpha',
          path: '/workflows',
          isBuiltIn: true,
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const blankButton = await screen.findByRole('button', {
      name: 'Start blank',
    });
    await waitFor(() => expect(blankButton).toBeEnabled());
    fireEvent.click(blankButton);

    expect(screen.getByText('Default workspace')).toBeInTheDocument();
    expect(screen.queryByText('scope-alpha')).not.toBeInTheDocument();
  });

  it('clears a submission failure when changing creation methods', async () => {
    mockStudioApi.authorWorkflow.mockRejectedValue(
      new Error('LLM service rejected the request'),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const describeButton = await screen.findByRole('button', {
      name: 'Describe',
    });
    await waitFor(() => expect(describeButton).toBeEnabled());
    fireEvent.click(describeButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Weekly review' },
    });
    fireEvent.change(screen.getByLabelText('Automation goal'), {
      target: { value: 'Summarize this week' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate workflow' }));

    expect(
      await screen.findByText("Workflow couldn't be created"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Change method' }));
    fireEvent.click(screen.getByRole('button', { name: 'Start blank' }));

    expect(
      screen.queryByText("Workflow couldn't be created"),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText('Workflow name')).toHaveValue('Weekly review');
  });

  it('keeps bundled template version metadata out of the primary interface', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const templateButton = await screen.findByRole('button', {
      name: 'Use template',
    });
    await waitFor(() => expect(templateButton).toBeEnabled());
    fireEvent.click(templateButton);

    expect(screen.queryByText(/2026\.08\.1/)).not.toBeInTheDocument();
  });

  it('submits bundled template YAML with the backend parser field names', async () => {
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'incident_triage',
        roles: [],
        steps: [{ id: 'classify', type: 'llm_call' }],
      },
      findings: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const templateButton = await screen.findByRole('button', {
      name: 'Use template',
    });
    await waitFor(() => expect(templateButton).toBeEnabled());
    fireEvent.click(templateButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident triage QA' },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'Create from template' }),
    );

    await waitFor(() => expect(mockStudioApi.parseYaml).toHaveBeenCalled());
    const submittedYaml = mockStudioApi.parseYaml.mock.calls[0][0].yaml;
    expect(submittedYaml).toContain('system_prompt:');
    expect(submittedYaml).toContain('target_role:');
    expect(submittedYaml).not.toContain('systemPrompt:');
    expect(submittedYaml).not.toContain('targetRole:');
  });

  it('validates imported YAML before creating and preserves invalid input', async () => {
    mockStudioApi.parseYaml.mockResolvedValue({
      document: null,
      findings: [
        { level: 'error', code: 'YAML_INVALID', message: 'Invalid YAML' },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const importButton = await screen.findByRole('button', {
      name: 'Import YAML',
    });
    await waitFor(() => expect(importButton).toBeEnabled());
    fireEvent.click(importButton);
    fireEvent.change(screen.getByLabelText('Workflow YAML'), {
      target: { value: 'name: [broken' },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'Validate and create' }),
    );

    expect(await screen.findByText('Invalid YAML')).toBeInTheDocument();
    expect(mockStudioApi.createWorkflowDraft).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Workflow YAML')).toHaveValue('name: [broken');
  });
});
