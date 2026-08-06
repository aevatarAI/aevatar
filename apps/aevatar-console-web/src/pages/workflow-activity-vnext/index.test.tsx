import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from './index';

let mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';
const mockLocationSubscribers = new Set<() => void>();
const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

const readMockUrl = () => new URL(mockLocation, 'http://console.local');

function createSseResponse(frames: readonly unknown[]): Response {
  const encoder = new TextEncoder();
  const body = frames
    .map((frame) => `data: ${JSON.stringify(frame)}\n\n`)
    .join('');

  return {
    body: new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(body));
        controller.close();
      },
    }),
    ok: true,
  } as Response;
}

function createEditorRunDetail(input: {
  readonly finalError?: string;
  readonly finalOutput?: string;
  readonly runId: string;
  readonly stateVersion: number;
  readonly status: string;
}) {
  return {
    summary: {
      runId: input.runId,
      workflowName: 'Committed source',
      status: input.status,
      success:
        input.status === 'completed'
          ? true
          : input.status === 'failed'
            ? false
            : null,
      startedAtUtc: '2026-08-05T10:00:00Z',
      updatedAtUtc: '2026-08-05T10:00:01Z',
      stateVersion: input.stateVersion,
      scopeId: 'scope-alpha',
      runOrigin: 'draft',
    },
    input: 'Review order 42',
    finalOutput: input.finalOutput ?? '',
    finalError: input.finalError ?? '',
    diagnostics: [],
    steps: [
      {
        stepId: 'step-verify',
        stepType: 'llm_call',
        targetRole: 'reviewer',
        requestedAtUtc: '2026-08-05T10:00:00Z',
        completedAtUtc:
          input.status === 'running' ? null : '2026-08-05T10:00:01Z',
        success:
          input.status === 'running' ? null : input.status === 'completed',
        durationMs: input.status === 'running' ? null : 1000,
        outputPreview: input.finalOutput ?? '',
        error: input.finalError ?? '',
        requestParameters: {},
        nextStepId: '',
        branchKey: '',
        suspensionType: '',
        suspensionPrompt: '',
        suspensionContent: '',
        suspensionTimeoutSeconds: null,
        toolApproval: null,
        usage: {
          promptTokens: 0,
          completionTokens: 0,
          totalTokens: 0,
          cost: 0,
        },
      },
    ],
    timeline: [],
    statistics: {
      totalSteps: 1,
      requestedSteps: 1,
      completedSteps: input.status === 'running' ? 0 : 1,
      roleReplyCount: 0,
      stepTypeCounts: { llm_call: 1 },
    },
    usageTotals: {
      promptTokens: 0,
      completionTokens: 0,
      totalTokens: 0,
      cost: 0,
    },
  };
}

function setMockLocation(nextLocation: string): void {
  mockLocation = nextLocation;
  act(() => {
    for (const listener of mockLocationSubscribers) listener();
  });
}

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
    hash: readMockUrl().hash,
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
    getWorkflowDraft: jest.fn(),
    getWorkflowDraftFile: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
    previewExplicitRequests: jest.fn(),
    saveWorkflow: jest.fn(),
    saveAndBindWorkflow: jest.fn(),
    saveUserLlmSettings: jest.fn(),
    serializeYaml: jest.fn(),
    updateWorkflowDraft: jest.fn(),
  },
}));

jest.mock('@/shared/studio/explicitRequestConfirmation', () => ({
  createWorkflowRevisionIdentityCandidate: jest.fn(),
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    getWorkflowDetail: jest.fn(),
    listWorkflows: jest.fn(),
  },
}));

jest.mock('@/shared/api/servicesApi', () => ({
  servicesApi: {
    deactivateDeployment: jest.fn(),
  },
}));

jest.mock('./workflows/workflowArchival', () => {
  const actual = jest.requireActual('./workflows/workflowArchival');
  return {
    ...actual,
    observeWorkflowArchival: jest.fn(),
  };
});

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    getServiceRevisions: jest.fn(),
    listServices: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeRunsApi', () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(),
    streamDraftRun: jest.fn(),
  },
}));

jest.mock('@/shared/api/workflowActivityApi', () => ({
  workflowActivityApi: {
    getRun: jest.fn(),
  },
}));

jest.mock('@/pages/settings/userLlmSaveObservation', () => ({
  observeUserLlmSave: jest.fn(),
}));

jest.mock('@/shared/navigation/history', () => ({
  getLocationSnapshot: () =>
    `${readMockUrl().pathname}${readMockUrl().search}${readMockUrl().hash}`,
  history: { push: jest.fn(), replace: jest.fn() },
  subscribeToLocationChanges: (listener: () => void) => {
    mockLocationSubscribers.add(listener);
    return () => mockLocationSubscribers.delete(listener);
  },
}));

jest.mock('@/shared/ui/ConsoleHeaderActions', () => ({
  ConsoleAuthActions: ({
    principal,
  }: {
    principal?: {
      authenticated: boolean;
      displayName: string;
    } | null;
  }) => (
    <button
      data-auth-source={principal === undefined ? 'stored' : 'account'}
      type="button"
    >
      {principal?.authenticated ? principal.displayName : 'Account'}
    </button>
  ),
  ConsoleLanguageSwitch: () => <button type="button">Language</button>,
}));

jest.mock('@/shared/ui/ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
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

const mockStudioApi = jest.requireMock('@/shared/studio/api').studioApi as {
  authorWorkflow: jest.Mock;
  createWorkflowDraft: jest.Mock;
  deleteWorkflowDraft: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  getAuthSession: jest.Mock;
  getUserConfigRuntime: jest.Mock;
  getUserLlmSettings: jest.Mock;
  getWorkflow: jest.Mock;
  getWorkflowDraft: jest.Mock;
  getWorkflowDraftFile: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
  previewExplicitRequests: jest.Mock;
  saveWorkflow: jest.Mock;
  saveAndBindWorkflow: jest.Mock;
  saveUserLlmSettings: jest.Mock;
  serializeYaml: jest.Mock;
  updateWorkflowDraft: jest.Mock;
};
const mockCreateWorkflowRevisionIdentityCandidate = jest.requireMock(
  '@/shared/studio/explicitRequestConfirmation',
).createWorkflowRevisionIdentityCandidate as jest.Mock;
const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  getWorkflowDetail: jest.Mock;
  listWorkflows: jest.Mock;
};
const mockServicesApi = jest.requireMock('@/shared/api/servicesApi')
  .servicesApi as {
  deactivateDeployment: jest.Mock;
};
const mockObserveWorkflowArchival = jest.requireMock(
  './workflows/workflowArchival',
).observeWorkflowArchival as jest.Mock;
const mockScopeRuntimeApi = jest.requireMock('@/shared/api/scopeRuntimeApi')
  .scopeRuntimeApi as {
  getServiceRevisions: jest.Mock;
  listServices: jest.Mock;
};
const mockRuntimeRunsApi = jest.requireMock('@/shared/api/runtimeRunsApi')
  .runtimeRunsApi as {
  streamChat: jest.Mock;
  streamDraftRun: jest.Mock;
};
const mockWorkflowActivityApi = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).workflowActivityApi as {
  getRun: jest.Mock;
};
const mockObserveUserLlmSave = jest.requireMock(
  '@/pages/settings/userLlmSaveObservation',
).observeUserLlmSave as jest.Mock;

describe('Workflow Activity vNext catalogue', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';
    jest.clearAllMocks();
    mockServicesApi.deactivateDeployment.mockResolvedValue({
      targetActorId: 'deployment-manager-alpha',
      commandId: 'cmd-archive-alpha',
      correlationId: 'corr-archive-alpha',
    });
  });

  afterEach(() => cleanupTestQueryClients());

  it('keeps language and account actions available inside the mobile navigation drawer', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(screen.getByLabelText('Open navigation'));

    const drawer = await screen.findByRole('dialog');
    expect(
      within(drawer).getByRole('button', { name: 'Language' }),
    ).toBeInTheDocument();
    expect(
      within(drawer).getByRole('button', { name: 'Account' }),
    ).toBeInTheDocument();
  });

  it('keeps modified brand clicks as native link navigation', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const brand = screen.getByRole('link', { name: 'Aevatar' });
    let preventedByComponent = false;
    const preventJsdomNavigation = (event: MouseEvent) => {
      if (event.target !== brand) return;
      preventedByComponent = event.defaultPrevented;
      event.preventDefault();
    };
    document.addEventListener('click', preventJsdomNavigation);
    fireEvent.click(brand, { metaKey: true });
    document.removeEventListener('click', preventJsdomNavigation);

    expect(preventedByComponent).toBe(false);
    expect(history.push).not.toHaveBeenCalled();
  });

  it('opens row Activity with only the catalogue workflow id', async () => {
    const rowIdentities = {
      memberId: 'm-alpha',
      publishedServiceId: 'svc-alpha',
      workflowId: 'wf-alpha',
    };
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
        workflowId: rowIdentities.workflowId,
        displayName: 'Invoice review',
        serviceKey: '',
        workflowName: 'invoice_review',
        actorId: 'summary-actor-alpha',
        activeRevisionId: 'revision-alpha',
        deploymentId: 'deployment-alpha',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T10:00:00Z',
      },
    ]);

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

    const activityLink = screen.getByRole('link', {
      name: 'View activity for Invoice review in Workspace',
    });
    expect(activityLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha',
    );
    fireEvent.click(activityLink);
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha',
    );
    expect(history.push).not.toHaveBeenCalledWith(
      expect.stringContaining(rowIdentities.memberId),
    );
    expect(history.push).not.toHaveBeenCalledWith(
      expect.stringContaining(rowIdentities.publishedServiceId),
    );

    const openLink = screen.getByRole('link', {
      name: 'Open Invoice review in Workspace',
    });
    expect(openLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-alpha',
    );
    const pushCallsBeforeModifiedClick = (history.push as jest.Mock).mock.calls
      .length;
    let modifiedClickPrevented = true;
    const observeModifiedClick = (event: MouseEvent) => {
      if (event.target !== openLink) return;
      modifiedClickPrevented = event.defaultPrevented;
      event.preventDefault();
    };
    document.addEventListener('click', observeModifiedClick);
    fireEvent.click(openLink, { metaKey: true });
    document.removeEventListener('click', observeModifiedClick);
    expect(modifiedClickPrevented).toBe(false);
    expect(history.push).toHaveBeenCalledTimes(pushCallsBeforeModifiedClick);

    fireEvent.click(openLink);
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-alpha',
    );
  });

  it('separates workflow status and last update while disclosing descriptions on demand', async () => {
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.defineProperty(window.navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });
    const emeaDescription = 'Route urgent EMEA requests to the on-call queue';
    const apacDescription = 'Escalate APAC billing requests to finance';
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-support-emea',
        name: 'Support triage',
        description: emeaDescription,
        fileName: 'support-emea.yaml',
        filePath: '/emea/support-emea.yaml',
        directoryId: 'directory-emea',
        directoryLabel: 'EMEA operations',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
      {
        activeRevisionId: 'rev-support-apac-7',
        serviceKey: 'svc-support-apac',
        workflowId: 'wf-support-apac',
        name: 'Support triage',
        description: apacDescription,
        fileName: 'support-apac.yaml',
        filePath: '/apac/support-apac.yaml',
        directoryId: 'directory-apac',
        directoryLabel: 'APAC operations',
        stepCount: 5,
        hasLayout: true,
        updatedAtUtc: '2026-08-05T11:30:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-support-apac',
        displayName: 'Support triage',
        serviceKey: 'svc-support-apac',
        workflowName: 'support_triage_apac',
        actorId: 'definition-support-apac',
        activeRevisionId: 'rev-support-apac-7',
        deploymentId: 'deployment-support-apac',
        deploymentStatus: 'active',
        updatedAt: '2026-08-05T11:30:00Z',
      },
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-nightly-reconciliation',
        displayName: 'Nightly reconciliation',
        serviceKey: 'svc-nightly-reconciliation',
        workflowName: 'nightly_reconciliation',
        actorId: 'definition-nightly-reconciliation',
        activeRevisionId: 'rev-nightly-reconciliation-2',
        deploymentId: 'deployment-nightly-reconciliation',
        deploymentStatus: 'active',
        updatedAt: '2026-08-03T09:15:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const duplicateNames = await screen.findAllByText('Support triage');
    expect(duplicateNames).toHaveLength(2);
    const apacRow = duplicateNames[0].closest('tr');
    const emeaRow = duplicateNames[1].closest('tr');
    const noDescriptionRow = screen
      .getByText('Nightly reconciliation')
      .closest('tr');
    expect(emeaRow).not.toBeNull();
    expect(apacRow).not.toBeNull();
    expect(noDescriptionRow).not.toBeNull();

    const workflowTable = screen.getByRole('table');
    expect(within(workflowTable).getAllByRole('columnheader')).toHaveLength(4);
    expect(
      within(workflowTable).getByRole('columnheader', { name: 'Status' }),
    ).toBeVisible();
    expect(
      within(workflowTable).getByRole('columnheader', {
        name: 'Last updated',
      }),
    ).toBeVisible();
    expect(
      within(workflowTable).queryByRole('columnheader', { name: 'Created' }),
    ).not.toBeInTheDocument();

    expect(within(emeaRow as HTMLElement).getByText('Draft')).toBeVisible();
    expect(within(apacRow as HTMLElement).getByText('Published')).toBeVisible();
    expect(
      within(apacRow as HTMLElement).queryByText('rev-support-apac-7'),
    ).not.toBeInTheDocument();
    expect(
      within(workflowTable).queryByText('scope-alpha'),
    ).not.toBeInTheDocument();
    expect(
      within(apacRow as HTMLElement).queryByText('wf-support-apac'),
    ).not.toBeInTheDocument();
    expect(
      within(apacRow as HTMLElement).queryByText('svc-support-apac'),
    ).not.toBeInTheDocument();
    expect(
      within(emeaRow as HTMLElement).getByRole('link', {
        name: 'Open Support triage in EMEA operations',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-support-emea',
    );
    expect(
      within(apacRow as HTMLElement).getByRole('link', {
        name: 'Open Support triage in APAC operations',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-support-apac',
    );
    expect(
      within(emeaRow as HTMLElement).getByRole('link', {
        name: 'View activity for Support triage in EMEA operations',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-support-emea',
    );
    expect(
      within(apacRow as HTMLElement).getByRole('link', {
        name: 'View activity for Support triage in APAC operations',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-support-apac',
    );
    expect(
      within(emeaRow as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in EMEA operations',
      }),
    ).toBeEnabled();

    const apacUpdatedAt = new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date('2026-08-05T11:30:00Z'));
    expect(
      within(apacRow as HTMLElement).getByText(apacUpdatedAt),
    ).toBeVisible();
    expect(
      within(apacRow as HTMLElement)
        .getByText(apacUpdatedAt)
        .closest('td'),
    ).toHaveAttribute('data-label', 'Last updated');

    expect(screen.queryByText(emeaDescription)).not.toBeInTheDocument();
    expect(screen.queryByText(apacDescription)).not.toBeInTheDocument();
    const descriptionTriggers = within(workflowTable).getAllByRole('button', {
      name: 'Description for Support triage',
    });
    expect(descriptionTriggers).toHaveLength(2);
    expect(
      within(noDescriptionRow as HTMLElement).queryByRole('button', {
        name: 'Description for Nightly reconciliation',
      }),
    ).not.toBeInTheDocument();

    fireEvent.mouseEnter(descriptionTriggers[1]);
    const disclosedDescription = await screen.findByText(emeaDescription);
    await waitFor(() => expect(disclosedDescription).toBeVisible());
    expect(disclosedDescription).toHaveClass('wa-vnext__workflow-description');
    expect(disclosedDescription.closest('.ant-popover')).toHaveClass(
      'ant-popover-placement-bottomLeft',
    );

    fireEvent.mouseLeave(descriptionTriggers[1]);
    await waitFor(() => expect(disclosedDescription).not.toBeVisible());
    fireEvent.focus(descriptionTriggers[1]);
    await waitFor(() => expect(disclosedDescription).toBeVisible());

    fireEvent.click(
      within(apacRow as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in APAC operations',
      }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Rename' }),
    ).toBeInTheDocument();
    fireEvent.click(
      screen.getByRole('menuitem', { name: 'Copy workflow reference' }),
    );
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith('wf-support-apac'),
    );
    expect(writeText).not.toHaveBeenCalledWith('svc-support-apac');
  });

  it('renames a duplicate workflow without changing its definition identity', async () => {
    const originalYaml = 'name: support_triage\nroles: []\nsteps: []\n';
    const renamedYaml = 'name: APAC support triage\nroles: []\nsteps: []\n';
    let authoritativeName = 'Support triage';
    let authoritativeYaml = originalYaml;
    let pendingName = '';
    let pendingYaml = '';
    let postUpdateReads = 0;
    const drafts = [
      {
        workflowId: 'wf-support-emea',
        name: 'Support triage',
        description: 'Route urgent EMEA requests',
        fileName: 'support-emea.yaml',
        filePath: '/emea/support-emea.yaml',
        directoryId: 'directory-emea',
        directoryLabel: 'EMEA operations',
        stepCount: 3,
        hasLayout: true,
        updatedAtUtc: '2026-08-04T10:00:00Z',
      },
      {
        workflowId: 'wf-support-apac',
        name: 'Support triage',
        description: 'Escalate APAC billing requests',
        fileName: 'support-apac.yaml',
        filePath: '/apac/support-apac.yaml',
        directoryId: 'directory-apac',
        directoryLabel: 'APAC operations',
        stepCount: 5,
        hasLayout: true,
        updatedAtUtc: '2026-08-05T11:30:00Z',
      },
    ];
    mockStudioApi.listWorkflowDrafts.mockImplementation(async () =>
      drafts.map((draft) =>
        draft.workflowId === 'wf-support-apac'
          ? { ...draft, name: authoritativeName }
          : draft,
      ),
    );
    mockScopesApi.listWorkflows.mockResolvedValue([]);
    mockStudioApi.getWorkflowDraft.mockImplementation(async () => {
      if (pendingName && postUpdateReads++ > 0) {
        authoritativeName = pendingName;
        authoritativeYaml = pendingYaml;
      }
      return {
        workflowId: 'wf-support-apac',
        name: authoritativeName,
        fileName: 'support-apac.yaml',
        filePath: '/apac/support-apac.yaml',
        directoryId: 'directory-apac',
        directoryLabel: 'APAC operations',
        yaml: authoritativeYaml,
        layout: { nodes: [] },
        updatedAtUtc: '2026-08-05T11:30:00Z',
      };
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'support_triage', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      document: { name: 'APAC support triage', roles: [], steps: [] },
      findings: [],
      yaml: renamedYaml,
    });
    mockStudioApi.updateWorkflowDraft.mockImplementation(async (input) => {
      pendingYaml = input.yaml;
      pendingName =
        input.yaml === renamedYaml ? 'APAC support triage' : 'Support triage';
      return {
        workflowId: 'wf-support-apac',
        name: pendingName,
        fileName: 'support-apac.yaml',
        filePath: '/apac/support-apac.yaml',
        directoryId: 'directory-apac',
        directoryLabel: 'APAC operations',
        yaml: pendingYaml,
        layout: { nodes: [] },
        updatedAtUtc: '2026-08-05T11:31:00Z',
      };
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const [apacDescriptionTrigger] = await screen.findAllByRole('button', {
      name: 'Description for Support triage',
    });
    const apacRow = apacDescriptionTrigger.closest('tr');
    fireEvent.click(
      within(apacRow as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in APAC operations',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    expect(
      await screen.findByText(
        'Another workflow already uses this name. Duplicate names are allowed.',
      ),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Workflow name' }), {
      target: { value: 'APAC support triage' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() =>
      expect(mockStudioApi.parseYaml).toHaveBeenCalledWith({
        yaml: originalYaml,
      }),
    );
    expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
      document: {
        name: 'APAC support triage',
        roles: [],
        steps: [],
      },
    });
    await waitFor(() =>
      expect(mockStudioApi.updateWorkflowDraft).toHaveBeenCalledWith({
        directoryId: 'directory-apac',
        fileName: 'support-apac.yaml',
        layout: { nodes: [] },
        scopeId: 'scope-alpha',
        workflowId: 'wf-support-apac',
        workflowName: 'APAC support triage',
        yaml: renamedYaml,
      }),
    );
    expect(await screen.findByText('APAC support triage')).toBeVisible();
    expect(mockStudioApi.getWorkflowDraft).toHaveBeenCalledTimes(3);
    expect(mockConsoleToast.success).toHaveBeenCalledWith('Workflow renamed');
  });

  it('keeps archived workflows out of Active and offers Archive for active published rows', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      {
        workflowId: 'wf-draft-alpha',
        name: 'Draft workflow',
        description: 'Editable only',
        fileName: 'draft.yaml',
        filePath: '/draft.yaml',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        stepCount: 1,
        hasLayout: true,
        updatedAtUtc: '2026-08-06T09:00:00Z',
      },
    ]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-active-alpha',
        displayName: 'Active workflow',
        serviceKey: 'scope-alpha:default:default:wf-active-alpha',
        workflowName: 'active_workflow',
        actorId: 'actor-active-alpha',
        activeRevisionId: 'rev-active-alpha',
        deploymentId: 'dep-active-alpha',
        deploymentStatus: 'Active',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-archived-alpha',
        displayName: 'Archived workflow',
        serviceKey: 'scope-alpha:default:default:wf-archived-alpha',
        workflowName: 'archived_workflow',
        actorId: 'actor-archived-alpha',
        activeRevisionId: 'rev-archived-alpha',
        deploymentId: 'dep-archived-alpha',
        deploymentStatus: 'Deactivated',
        updatedAt: '2026-08-05T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const activeName = await screen.findByText('Active workflow');
    expect(screen.getByText('Draft workflow')).toBeInTheDocument();
    expect(screen.queryByText('Archived workflow')).not.toBeInTheDocument();
    expect(screen.getByText('Active workflows')).toBeInTheDocument();

    const activeActions = within(
      activeName.closest('tr') as HTMLElement,
    ).getByRole('button', {
      name: 'More actions for Active workflow in Workspace',
    });
    fireEvent.click(activeActions);
    expect(
      await screen.findByRole('menuitem', { name: 'Archive' }),
    ).toBeInTheDocument();
  });

  it('restores the Archived view without offering Archive again', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=archived';
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-active-alpha',
        displayName: 'Active workflow',
        serviceKey: 'scope-alpha:default:default:wf-active-alpha',
        workflowName: 'active_workflow',
        actorId: 'actor-active-alpha',
        activeRevisionId: 'rev-active-alpha',
        deploymentId: 'dep-active-alpha',
        deploymentStatus: 'Active',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-archived-alpha',
        displayName: 'Archived workflow',
        serviceKey: 'scope-alpha:default:default:wf-archived-alpha',
        workflowName: 'archived_workflow',
        actorId: 'actor-archived-alpha',
        activeRevisionId: 'rev-archived-alpha',
        deploymentId: 'dep-archived-alpha',
        deploymentStatus: 'Deactivated',
        updatedAt: '2026-08-05T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const archivedName = await screen.findByText('Archived workflow');
    expect(screen.queryByText('Active workflow')).not.toBeInTheDocument();
    expect(screen.getAllByText('Archived')).toHaveLength(2);
    expect(
      within(archivedName.closest('tr') as HTMLElement).getByText('Archived'),
    ).toBeVisible();

    fireEvent.click(
      within(archivedName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Archived workflow in Workspace',
      }),
    );
    expect(
      screen.queryByRole('menuitem', { name: 'Archive' }),
    ).not.toBeInTheDocument();
  });

  it('reports Archive only after the accepted deployment deactivation is observed', async () => {
    const activeWorkflow = {
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      displayName: 'Workflow Alpha',
      serviceKey: 'scope-alpha:default:default:wf-alpha',
      workflowName: 'workflow_alpha',
      actorId: 'actor-alpha',
      activeRevisionId: 'rev-alpha',
      deploymentId: 'dep-alpha',
      deploymentStatus: 'Active',
      updatedAt: '2026-08-06T10:00:00Z',
    };
    const archivedWorkflow = {
      ...activeWorkflow,
      deploymentStatus: 'Deactivated',
      updatedAt: '2026-08-06T10:01:00Z',
    };
    let committedRows = [activeWorkflow];
    let resolveObservation!: (value: {
      kind: 'observed';
      workflows: (typeof archivedWorkflow)[];
    }) => void;
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockImplementation(async () => committedRows);
    mockObserveWorkflowArchival.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveObservation = resolve;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const workflowName = await screen.findByText('Workflow Alpha');
    fireEvent.click(
      within(workflowName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Workflow Alpha in Workspace',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));

    expect(screen.getByText('Archive this workflow?')).toBeInTheDocument();
    expect(mockServicesApi.deactivateDeployment).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Archive workflow' }));

    await waitFor(() =>
      expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledWith(
        'wf-alpha',
        'dep-alpha',
        {
          tenantId: 'scope-alpha',
          appId: 'default',
          namespace: 'default',
        },
      ),
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalledWith(
      'Workflow archived',
    );
    expect(screen.getByText('Archive this workflow?')).toBeInTheDocument();

    committedRows = [archivedWorkflow];
    act(() => {
      resolveObservation({
        kind: 'observed',
        workflows: [archivedWorkflow],
      });
    });

    await waitFor(() =>
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Workflow archived',
      ),
    );
    expect(
      screen.queryByText('Archive this workflow?'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('Workflow Alpha')).not.toBeInTheDocument();
  });

  it('checks an accepted Archive again without resubmitting deactivation', async () => {
    const activeWorkflow = {
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      displayName: 'Workflow Alpha',
      serviceKey: 'scope-alpha:default:default:wf-alpha',
      workflowName: 'workflow_alpha',
      actorId: 'actor-alpha',
      activeRevisionId: 'rev-alpha',
      deploymentId: 'dep-alpha',
      deploymentStatus: 'Active',
      updatedAt: '2026-08-06T10:00:00Z',
    };
    const archivedWorkflow = {
      ...activeWorkflow,
      deploymentStatus: 'Deactivated',
      updatedAt: '2026-08-06T10:01:00Z',
    };
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([activeWorkflow]);
    mockObserveWorkflowArchival
      .mockResolvedValueOnce({ kind: 'delayed' })
      .mockResolvedValueOnce({
        kind: 'observed',
        workflows: [archivedWorkflow],
      });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const workflowName = await screen.findByText('Workflow Alpha');
    fireEvent.click(
      within(workflowName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Workflow Alpha in Workspace',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    fireEvent.click(screen.getByRole('button', { name: 'Archive workflow' }));

    expect(
      await screen.findByText(
        "Archive was accepted, but it hasn't been confirmed yet",
      ),
    ).toBeInTheDocument();
    expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledTimes(1);
    expect(mockObserveWorkflowArchival).toHaveBeenCalledTimes(1);

    fireEvent.click(await screen.findByRole('button', { name: 'Check again' }));

    await waitFor(() =>
      expect(mockObserveWorkflowArchival).toHaveBeenCalledTimes(2),
    );
    expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledTimes(1);
  });

  it('resubmits Archive when the deactivation request was not accepted', async () => {
    const activeWorkflow = {
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      displayName: 'Workflow Alpha',
      serviceKey: 'scope-alpha:default:default:wf-alpha',
      workflowName: 'workflow_alpha',
      actorId: 'actor-alpha',
      activeRevisionId: 'rev-alpha',
      deploymentId: 'dep-alpha',
      deploymentStatus: 'Active',
      updatedAt: '2026-08-06T10:00:00Z',
    };
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([activeWorkflow]);
    mockServicesApi.deactivateDeployment
      .mockRejectedValueOnce(new Error('request failed'))
      .mockResolvedValueOnce({
        targetActorId: 'deployment-manager-alpha',
        commandId: 'cmd-archive-alpha',
        correlationId: 'corr-archive-alpha',
      });
    mockObserveWorkflowArchival.mockResolvedValue({ kind: 'delayed' });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const workflowName = await screen.findByText('Workflow Alpha');
    fireEvent.click(
      within(workflowName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Workflow Alpha in Workspace',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    fireEvent.click(screen.getByRole('button', { name: 'Archive workflow' }));

    expect(
      await screen.findByText("Workflow couldn't be archived"),
    ).toBeInTheDocument();
    expect(mockObserveWorkflowArchival).not.toHaveBeenCalled();
    expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledTimes(1);

    fireEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    await waitFor(() =>
      expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledTimes(2),
    );
    expect(mockObserveWorkflowArchival).toHaveBeenCalledTimes(1);
  });

  it('deep-links draft-only row Activity with its workflow id', async () => {
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
    const activityLink = screen.getByRole('link', {
      name: 'View activity for Support triage in Workflows',
    });
    expect(activityLink).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-draft-alpha',
    );
    fireEvent.click(activityLink);
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-draft-alpha',
    );
  });

  it('uses one editor entry point and exposes draft-only deletion with row actions', async () => {
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
    const openDraft = within(draftRow as HTMLElement).getByRole('link', {
      name: 'Open Support triage in Workflows',
    });
    expect(openDraft).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha',
    );
    expect(openDraft).toHaveClass(
      'aevatar-interactive-button',
      'ant-btn-primary',
    );
    const viewActivity = within(draftRow as HTMLElement).getByRole('link', {
      name: 'View activity for Support triage in Workflows',
    });
    expect(viewActivity).toHaveClass('aevatar-interactive-button');
    expect(viewActivity).not.toHaveClass('ant-btn-primary');
    expect(
      within(draftRow as HTMLElement).queryByRole('button', {
        name: 'Run Support triage',
      }),
    ).not.toBeInTheDocument();
    expect(
      within(draftRow as HTMLElement).queryByRole('button', {
        name: 'Delete Support triage',
      }),
    ).not.toBeInTheDocument();
    const moreActions = within(draftRow as HTMLElement).getByRole('button', {
      name: 'More actions for Support triage in Workflows',
    });
    expect(moreActions).toBeEnabled();
    expect(moreActions).toHaveClass('aevatar-interactive-button');
    expect(moreActions).not.toHaveClass('ant-btn-primary');
    expect(
      within(committedRow as HTMLElement).queryByRole('button', {
        name: 'Delete Invoice review',
      }),
    ).not.toBeInTheDocument();

    fireEvent.click(openDraft);
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha',
    );

    fireEvent.click(moreActions);
    expect(
      (await screen.findAllByRole('menuitem')).map((item) => item.textContent),
    ).toEqual(['Rename', 'Copy workflow reference', 'Delete draft']);
    expect(screen.getByRole('separator')).toBeInTheDocument();
    const deleteMenuItem = screen.getByRole('menuitem', {
      name: 'Delete draft',
    });
    expect(deleteMenuItem).toHaveClass('ant-dropdown-menu-item-danger');
    fireEvent.click(deleteMenuItem);
    expect(screen.getByText('Delete editable draft?')).toBeInTheDocument();
    expect(mockStudioApi.deleteWorkflowDraft).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    fireEvent.click(
      within(committedRow as HTMLElement).getByRole('button', {
        name: 'More actions for Invoice review in Workspace',
      }),
    );
    expect(
      (await screen.findAllByRole('menuitem')).map((item) => item.textContent),
    ).toEqual(['Copy workflow reference', 'Archive']);
    expect(screen.getByRole('menuitem', { name: 'Archive' })).toHaveClass(
      'ant-dropdown-menu-item-danger',
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
        name: 'More actions for Support triage in Workflows',
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
      screen.getByRole('button', {
        name: 'More actions for Committed support source in Workspace',
      }),
    ).toBeEnabled();
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
        name: 'More actions for Support triage in Workflows',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Draft couldn't be deleted",
      ),
    );
    expect(
      screen.queryByText("Draft couldn't be deleted"),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('DELETE returned 503')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled(),
    );
    expect(screen.getByText('Support triage')).toBeInTheDocument();
    expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(1);
  });

  it('reports only the specific delete refresh failure after the draft was removed', async () => {
    mockStudioApi.listWorkflowDrafts
      .mockResolvedValueOnce([
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
      ])
      .mockRejectedValueOnce(new Error('refresh returned 503'));
    mockScopesApi.listWorkflows.mockResolvedValue([]);
    mockStudioApi.deleteWorkflowDraft.mockResolvedValue(undefined);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const draftName = await screen.findByText('Support triage');
    fireEvent.click(
      within(draftName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in Workflows',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Draft was deleted, but workflows couldn't refresh",
      ),
    );
    expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.deleteWorkflowDraft).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(2);
  });

  it('waits for both workflow sources before reporting a list failure', async () => {
    let resolveCommitted!: (rows: unknown[]) => void;
    mockStudioApi.listWorkflowDrafts.mockRejectedValue(
      new Error('draft source down'),
    );
    mockScopesApi.listWorkflows.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveCommitted = resolve;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await waitFor(() =>
      expect(mockStudioApi.listWorkflowDrafts).toHaveBeenCalledTimes(1),
    );
    expect(screen.getByText('Loading workflows')).toBeInTheDocument();
    expect(
      screen.queryByText("Some workflows couldn't be loaded"),
    ).not.toBeInTheDocument();
    expect(mockConsoleToast.error).not.toHaveBeenCalled();

    act(() => {
      resolveCommitted([
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
    });

    expect(await screen.findByText('Invoice review')).toBeInTheDocument();
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Some workflows couldn't be loaded",
      ),
    );
  });

  it('keeps successful rows and reports the failed source with a toast', async () => {
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
      screen.queryByText("Some workflows couldn't be loaded"),
    ).not.toBeInTheDocument();
    expect(mockConsoleToast.error).toHaveBeenCalledWith(
      "Some workflows couldn't be loaded",
    );
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
      await screen.findByRole('option', { name: 'Active workflows' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Drafts' })).toBeInTheDocument();
    expect(
      screen.getByRole('option', { name: 'Archived' }),
    ).toBeInTheDocument();
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
        expiresAtUtc: '2099-08-05T10:00:00Z',
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

  it('renders the same authoritative identity in the shell and Account while keeping support values secondary', async () => {
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
    expect(await screen.findAllByText('Ada Operator')).toHaveLength(2);
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('ada@example.test')).toBeInTheDocument();
    expect(screen.getByText('NyxID')).toBeInTheDocument();
    expect(screen.getByText('scope-alpha')).toBeInTheDocument();
    expect(screen.getByText(/GMT|UTC/)).toHaveTextContent(/in .+ days/);
    expect(screen.getByText('Support details')).toBeInTheDocument();
    expect(screen.getByText('user-subject-alpha')).not.toBeVisible();
    expect(screen.getByText('operator')).not.toBeVisible();
    expect(screen.getByText('platform')).not.toBeVisible();
    expect(screen.queryByText('nyxid-session')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Support details'));
    expect(screen.getByText('user-subject-alpha')).toBeVisible();
    expect(screen.getByText('operator')).toBeVisible();
    expect(screen.getByText('platform')).toBeVisible();

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

  it.each([
    {
      label: 'expired',
      session: {
        authenticated: false,
        scopeId: 'scope-alpha',
        expiresAtUtc: '2000-01-01T00:00:00Z',
      },
      authenticated: false,
      expected: 'Expired',
    },
    {
      label: 'invalid',
      session: {
        authenticated: false,
        scopeId: 'scope-alpha',
        expiresAtUtc: '2099-08-05T10:00:00Z',
      },
      authenticated: true,
      expected: 'Invalid',
    },
  ])('offers direct sign-in recovery for an $label session', async ({
    authenticated,
    expected,
    session,
  }) => {
    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated,
      providerDisplayName: 'NyxID',
      name: 'Ada Operator',
      profile: null,
      session,
    });
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account';

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText(expected)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign in again' })).toBeEnabled();
    expect(screen.queryByText('Active')).not.toBeInTheDocument();
  });

  it('renders optional missing fields without guessing that policy hid them', async () => {
    mockStudioApi.getAuthSession.mockResolvedValueOnce({
      enabled: true,
      authenticated: true,
      providerDisplayName: 'NyxID',
      profile: {
        subject: 'user-subject-alpha',
        name: 'Ada Operator',
        email: null,
        emailVerified: null,
        picture: null,
        roles: [],
        groups: [],
      },
      session: {
        authenticated: true,
        scopeId: null,
        expiresAtUtc: null,
      },
    });
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account';
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect((await screen.findAllByText('Not provided')).length).toBeGreaterThan(
      1,
    );
    expect(screen.queryByText('Hidden by policy')).not.toBeInTheDocument();
  });

  it.each([
    {
      label: 'capability denial',
      error: Object.assign(new Error('forbidden'), { status: 403 }),
      expected: 'Unauthorized',
    },
    {
      label: 'transient load failure',
      error: Object.assign(new Error('temporarily unavailable'), {
        status: 503,
      }),
      expected: 'Not loaded',
    },
  ])('keeps $label distinct from absent profile data', async ({
    error,
    expected,
  }) => {
    mockStudioApi.getAuthSession.mockRejectedValue(error);
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/settings?section=account';

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText(expected)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Account' })).toHaveAttribute(
      'data-auth-source',
      'account',
    );
    if (expected === 'Not loaded') {
      expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    }
    expect(screen.queryByText('Not provided')).not.toBeInTheDocument();
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

  it('keeps dirty save actions outside the scrolling AI defaults panel', async () => {
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

    expect(
      screen.queryByRole('region', { name: 'Unsaved settings actions' }),
    ).not.toBeInTheDocument();
    const routeSelect = await screen.findByRole('combobox', {
      name: 'Preferred service',
    });
    fireEvent.mouseDown(routeSelect);
    fireEvent.click(await screen.findByText('Service alpha'));

    expect(routeSelect).toBeInTheDocument();
    const aiDefaultsPanel = screen.getByRole('region', {
      name: 'AI defaults',
    });
    const saveActions = screen.getByRole('region', {
      name: 'Unsaved settings actions',
    });
    expect(aiDefaultsPanel).not.toContainElement(saveActions);
    expect(
      within(saveActions).getByRole('button', { name: 'Save changes' }),
    ).toBeEnabled();

    fireEvent.click(
      within(saveActions).getByRole('button', {
        name: 'Restore saved settings',
      }),
    );
    expect(
      screen.queryByRole('region', { name: 'Unsaved settings actions' }),
    ).not.toBeInTheDocument();
    expect(mockStudioApi.saveUserLlmSettings).not.toHaveBeenCalled();
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
    expect(mockConsoleToast.success).not.toHaveBeenCalled();
    observeSaved?.({ phase: 'observed' });
    await waitFor(() =>
      expect(history.push).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows',
      ),
    );
    expect(mockConsoleToast.success).toHaveBeenCalledWith('Settings saved');
  });
});

describe('Workflow Activity vNext editor', () => {
  beforeEach(() => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source';
    mockLocationSubscribers.clear();
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

  it('keeps the editor header focused on the workflow name', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Build, test, and refine this workflow.'),
    ).not.toBeInTheDocument();
  });

  it('requires an explicitly selected real scope service before publishing a saved workflow', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha';
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Workflow alpha',
      fileName: 'workflow-alpha.yaml',
      filePath: '/workflows/workflow-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      updatedAtUtc: '2026-08-06T10:00:00Z',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
        tenantId: 'tenant-alpha',
        appId: 'app-alpha',
        namespace: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Service alpha',
        defaultServingRevisionId: 'rev-existing',
        activeServingRevisionId: 'rev-existing',
        deploymentId: 'deployment-existing',
        primaryActorId: 'actor-existing',
        deploymentStatus: 'active',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-08-06T10:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const publish = await screen.findByRole('button', { name: 'Publish' });
    expect(publish).toBeEnabled();
    fireEvent.click(publish);

    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    expect(mockScopeRuntimeApi.listServices).toHaveBeenCalledWith(
      'scope-alpha',
      {
        take: 200,
      },
    );
    const continueButton = within(dialog).getByRole('button', {
      name: 'Review and publish',
    });
    expect(continueButton).toBeDisabled();

    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));

    expect(continueButton).toBeEnabled();
  });

  it('submits a saved draft only to the explicitly selected real scope service', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha';
    mockCreateWorkflowRevisionIdentityCandidate.mockReturnValue(
      'rev-preview-alpha',
    );
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Workflow alpha',
      fileName: 'workflow-alpha.yaml',
      filePath: '/workflows/workflow-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      updatedAtUtc: '2026-08-06T10:00:00Z',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
        tenantId: 'tenant-alpha',
        appId: 'app-alpha',
        namespace: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Service alpha',
        defaultServingRevisionId: 'rev-existing',
        activeServingRevisionId: 'rev-existing',
        deploymentId: 'deployment-existing',
        primaryActorId: 'actor-existing',
        deploymentStatus: 'active',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-08-06T10:00:00Z',
      },
    ]);
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.saveAndBindWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      binding: {
        scopeId: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Workflow alpha',
        revisionId: 'rev-preview-alpha',
        targetKind: 'workflow',
        targetName: 'Workflow alpha',
      },
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: 'workflow-revision-alpha',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    });
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: 'rev-preview-alpha',
      activeServingRevisionId: 'rev-preview-alpha',
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:00Z',
      revisions: [
        {
          revisionId: 'rev-preview-alpha',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'artifact-publication-alpha',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deployment-service-alpha',
          primaryActorId: 'actor-service-alpha',
          createdAt: '2026-08-06T10:00:00Z',
          preparedAt: '2026-08-06T10:00:01Z',
          publishedAt: '2026-08-06T10:00:02Z',
          retiredAt: null,
          workflowName: 'Workflow alpha',
          workflowDefinitionActorId: 'actor-workflow-alpha',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledWith(
        expect.objectContaining({
          executionMode: 'interactive',
          scopeId: 'scope-alpha',
          workflowId: 'wf-draft-alpha',
          workflowYaml:
            'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
        }),
      ),
    );
    expect(mockStudioApi.saveAndBindWorkflow).not.toHaveBeenCalled();

    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );
    await waitFor(() =>
      expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          revisionId: 'rev-preview-alpha',
          scopeId: 'scope-alpha',
          serviceId: 'svc-alpha',
          workflowId: 'wf-draft-alpha',
          workflowYaml:
            'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
        }),
      ),
    );
    expect(await screen.findByText('Workflow published')).toBeInTheDocument();
    expect(screen.getByText('Workflow ID')).toBeInTheDocument();
    expect(screen.getByText('wf-draft-alpha')).toBeInTheDocument();
    expect(screen.getByText('Published service ID')).toBeInTheDocument();
    expect(screen.getByText('svc-alpha')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Published' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalledWith(
      'Workflow published',
    );
  });

  it.each([
    {
      returnedRevisionId: 'rev-returned-other',
      returnedWorkflowId: 'wf-draft-alpha',
      mismatch: 'revision',
    },
    {
      returnedRevisionId: 'rev-preview-alpha',
      returnedWorkflowId: 'wf-returned-other',
      mismatch: 'workflow ID',
    },
    {
      returnedRevisionId: 'rev-preview-alpha',
      returnedWorkflowId: 'wf-draft-alpha',
      bindingTargetKind: 'script',
      mismatch: 'binding target',
    },
  ])('keeps a returned $mismatch mismatch visible without starting observation', async ({
    returnedRevisionId,
    returnedWorkflowId,
    bindingTargetKind,
  }) => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha';
    mockCreateWorkflowRevisionIdentityCandidate.mockReturnValue(
      'rev-preview-alpha',
    );
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Workflow alpha',
      fileName: 'workflow-alpha.yaml',
      filePath: '/workflows/workflow-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      updatedAtUtc: '2026-08-06T10:00:00Z',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
        tenantId: 'tenant-alpha',
        appId: 'app-alpha',
        namespace: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Service alpha',
        defaultServingRevisionId: 'rev-existing',
        activeServingRevisionId: 'rev-existing',
        deploymentId: 'deployment-existing',
        primaryActorId: 'actor-existing',
        deploymentStatus: 'active',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-08-06T10:00:00Z',
      },
    ]);
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.saveAndBindWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: returnedWorkflowId,
      revisionId: returnedRevisionId,
      binding: {
        scopeId: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Workflow alpha',
        revisionId: returnedRevisionId,
        targetKind: bindingTargetKind ?? 'workflow',
        targetName: 'Workflow alpha',
      },
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );

    expect(
      await screen.findByText("Publication couldn't be confirmed"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('dialog', { name: 'Publish workflow' }),
    ).toBeInTheDocument();
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });

  function arrangeSavedDraftPublication(): void {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha';
    mockCreateWorkflowRevisionIdentityCandidate.mockReturnValue(
      'rev-preview-alpha',
    );
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'Workflow alpha',
      fileName: 'workflow-alpha.yaml',
      filePath: '/workflows/workflow-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      updatedAtUtc: '2026-08-06T10:00:00Z',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
      document: {
        name: 'workflow_alpha',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      findings: [],
    });
    mockScopeRuntimeApi.listServices.mockResolvedValue([
      {
        serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
        tenantId: 'tenant-alpha',
        appId: 'app-alpha',
        namespace: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Service alpha',
        defaultServingRevisionId: 'rev-existing',
        activeServingRevisionId: 'rev-existing',
        deploymentId: 'deployment-existing',
        primaryActorId: 'actor-existing',
        deploymentStatus: 'active',
        endpoints: [],
        policyIds: [],
        updatedAt: '2026-08-06T10:00:00Z',
      },
    ]);
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.saveAndBindWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      binding: {
        scopeId: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Workflow alpha',
        revisionId: 'rev-preview-alpha',
        targetKind: 'workflow',
        targetName: 'Workflow alpha',
      },
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
  }

  function arrangeObservedWorkflowPublication(): void {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: 'workflow-revision-alpha',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    });
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: 'rev-preview-alpha',
      activeServingRevisionId: 'rev-preview-alpha',
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:03Z',
      revisions: [
        {
          revisionId: 'rev-preview-alpha',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'artifact-publication-alpha',
          failureReason: '',
          isDefaultServing: true,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deployment-service-alpha',
          primaryActorId: 'actor-service-alpha',
          createdAt: '2026-08-06T10:00:00Z',
          preparedAt: '2026-08-06T10:00:01Z',
          publishedAt: '2026-08-06T10:00:02Z',
          retiredAt: null,
          workflowName: 'Workflow alpha',
          workflowDefinitionActorId: 'actor-workflow-alpha',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });
  }

  async function publishObservedWorkflow(): Promise<void> {
    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    fireEvent.mouseDown(
      await within(dialog).findByRole('combobox', { name: 'Service' }),
    );
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );
    await screen.findByText('Workflow published');
  }

  async function renderPublishedWorkflowPage(): Promise<void> {
    arrangeObservedWorkflowPublication();
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();
  }

  it('keeps every publish blocker focusable and links validation issues to their fields and steps', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [{ id: 'step-root', type: 'llm_call' }],
      },
      draftExists: false,
      findings: [
        {
          code: 'WORKFLOW_NAME_REQUIRED',
          level: 'error',
          message: 'Workflow name is required.',
          path: '/name',
        },
        {
          code: 'STEP_INSTRUCTION_REQUIRED',
          level: 'error',
          message: 'Step instruction is required.',
          path: '/steps/0/parameters/prompt_prefix',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const publish = await screen.findByRole('button', {
      name: 'Publish blocked · 3 issues',
    });
    expect(publish).toHaveAttribute('aria-disabled', 'true');
    expect(publish).toBeEnabled();

    fireEvent.click(publish);
    expect(
      screen.queryByRole('dialog', { name: 'Publish workflow' }),
    ).not.toBeInTheDocument();

    fireEvent.focus(publish);
    const checklist = await screen.findByRole('region', {
      name: 'Publish readiness issues',
    });
    expect(within(checklist).getAllByRole('listitem')).toHaveLength(3);

    fireEvent.click(
      within(checklist).getByRole('button', {
        name: 'Workflow name is required.',
      }),
    );
    expect(
      screen.getByRole('textbox', { name: 'Workflow name' }),
    ).toHaveFocus();

    fireEvent.click(
      within(checklist).getByRole('button', {
        name: 'Step instruction is required.',
      }),
    );
    expect(
      await screen.findByRole('complementary', { name: 'Configure step-root' }),
    ).toBeVisible();
    expect(mockStudioApi.previewExplicitRequests).not.toHaveBeenCalled();
  });

  it('retries a failed publication observation without sending a second POST', async () => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail
      .mockRejectedValueOnce(
        Object.assign(new Error('HTTP 503'), { status: 503 }),
      )
      .mockResolvedValueOnce({
        available: true,
        scopeId: 'scope-alpha',
        workflow: {
          scopeId: 'scope-alpha',
          workflowId: 'wf-draft-alpha',
          displayName: 'Workflow alpha',
          serviceKey: 'workflow-alpha',
          workflowName: 'Workflow alpha',
          actorId: 'actor-workflow-alpha',
          activeRevisionId: 'workflow-revision-alpha',
          deploymentId: 'deployment-workflow-alpha',
          deploymentStatus: 'Available',
          updatedAt: '2026-08-06T10:00:00Z',
        },
        source: null,
      });
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: 'rev-preview-alpha',
      activeServingRevisionId: 'rev-preview-alpha',
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:00Z',
      revisions: [
        {
          revisionId: 'rev-preview-alpha',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'artifact-publication-alpha',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deployment-service-alpha',
          primaryActorId: 'actor-service-alpha',
          createdAt: '2026-08-06T10:00:00Z',
          preparedAt: '2026-08-06T10:00:01Z',
          publishedAt: '2026-08-06T10:00:02Z',
          retiredAt: null,
          workflowName: 'Workflow alpha',
          workflowDefinitionActorId: 'actor-workflow-alpha',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );

    expect(
      await screen.findByText("Publication couldn't be confirmed"),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Publish blocked · 1 issue' }),
    ).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));

    await waitFor(() =>
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(2),
    );
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
      'scope-alpha',
      'wf-draft-alpha',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledTimes(2);
    expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenLastCalledWith(
      'scope-alpha',
      'svc-alpha',
    );
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
  });

  it.each([
    [401, 'Sign in to continue'],
    [403, "You don't have access to this workspace"],
  ])('keeps an accepted publication receipt mutation-locked after a %i observation', async (status, message) => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockRejectedValue(
      Object.assign(new Error(`HTTP ${status}`), { status }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Publish' }),
    );

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Publish blocked · 1 issue' }),
    ).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));

    await waitFor(() =>
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(2),
    );
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
      'scope-alpha',
      'wf-draft-alpha',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledTimes(2);
    expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenLastCalledWith(
      'scope-alpha',
      'svc-alpha',
    );
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
  });

  it('requires a fresh review before republishing a failed publication receipt', async () => {
    arrangeSavedDraftPublication();
    const receiptRevisionId = 'rev-receipt-alpha';
    const freshRevisionId = 'rev-fresh-beta';
    mockCreateWorkflowRevisionIdentityCandidate
      .mockReturnValueOnce(receiptRevisionId)
      .mockReturnValueOnce(freshRevisionId);
    mockStudioApi.previewExplicitRequests
      .mockResolvedValueOnce({
        workflowId: 'wf-draft-alpha',
        revisionId: receiptRevisionId,
        items: [],
      })
      .mockResolvedValueOnce({
        workflowId: 'wf-draft-alpha',
        revisionId: freshRevisionId,
        items: [],
      });
    mockStudioApi.saveAndBindWorkflow
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        revisionId: receiptRevisionId,
        binding: {
          scopeId: 'scope-alpha',
          serviceId: 'svc-alpha',
          displayName: 'Workflow alpha',
          revisionId: receiptRevisionId,
          targetKind: 'workflow',
          targetName: 'Workflow alpha',
        },
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      })
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        revisionId: freshRevisionId,
        binding: {
          scopeId: 'scope-alpha',
          serviceId: 'svc-alpha',
          displayName: 'Workflow alpha',
          revisionId: freshRevisionId,
          targetKind: 'workflow',
          targetName: 'Workflow alpha',
        },
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      });
    mockScopesApi.getWorkflowDetail
      .mockRejectedValueOnce(
        Object.assign(new Error('HTTP 503'), { status: 503 }),
      )
      .mockResolvedValueOnce({
        available: true,
        scopeId: 'scope-alpha',
        workflow: {
          scopeId: 'scope-alpha',
          workflowId: 'wf-draft-alpha',
          displayName: 'Workflow alpha',
          serviceKey: 'workflow-alpha',
          workflowName: 'Workflow alpha',
          actorId: 'actor-workflow-alpha',
          activeRevisionId: 'workflow-revision-beta',
          deploymentId: 'deployment-workflow-alpha',
          deploymentStatus: 'Available',
          updatedAt: '2026-08-06T10:00:00Z',
        },
        source: null,
      });
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: freshRevisionId,
      activeServingRevisionId: freshRevisionId,
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:00Z',
      revisions: [
        {
          revisionId: freshRevisionId,
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'artifact-publication-beta',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deployment-service-alpha',
          primaryActorId: 'actor-service-alpha',
          createdAt: '2026-08-06T10:00:00Z',
          preparedAt: '2026-08-06T10:00:01Z',
          publishedAt: '2026-08-06T10:00:02Z',
          retiredAt: null,
          workflowName: 'Workflow alpha',
          workflowDefinitionActorId: 'actor-workflow-alpha',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const initialDialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const initialServiceSelect = await within(initialDialog).findByRole(
      'combobox',
      { name: 'Service' },
    );
    fireEvent.mouseDown(initialServiceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(initialDialog).getByRole('button', {
        name: 'Review and publish',
      }),
    );
    fireEvent.click(
      await within(initialDialog).findByRole('button', { name: 'Publish' }),
    );

    expect(
      await screen.findByText("Publication couldn't be confirmed"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Review again' }));

    const freshDialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const freshReview = within(freshDialog).getByRole('button', {
      name: 'Review and publish',
    });
    expect(freshReview).toBeDisabled();
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);

    const freshServiceSelect = await within(freshDialog).findByRole(
      'combobox',
      { name: 'Service' },
    );
    fireEvent.mouseDown(freshServiceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    expect(freshReview).toBeEnabled();
    fireEvent.click(freshReview);

    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(2),
    );
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
    fireEvent.click(
      await within(freshDialog).findByRole('button', { name: 'Publish' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenLastCalledWith(
        expect.objectContaining({ revisionId: freshRevisionId }),
      ),
    );
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(2);
    expect(await screen.findByText('Workflow published')).toBeInTheDocument();
  });

  it('retries delayed publication observation without sending a second POST', async () => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockRejectedValue(
      Object.assign(new Error('HTTP 404'), { status: 404 }),
    );
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: 'rev-existing',
      activeServingRevisionId: 'rev-existing',
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:00Z',
      revisions: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    const finalPublish = await within(dialog).findByRole('button', {
      name: 'Publish',
    });

    jest.useFakeTimers();
    try {
      fireEvent.click(finalPublish);
      await act(async () => {
        await jest.advanceTimersByTimeAsync(0);
      });
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(1);
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledTimes(1);
      await act(async () => {
        await jest.advanceTimersByTimeAsync(5_000);
      });

      expect(
        screen.getByText('Publication is taking longer to appear'),
      ).toBeInTheDocument();
      fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
      await act(async () => {
        await jest.advanceTimersByTimeAsync(5_000);
      });

      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(10);
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
        'scope-alpha',
        'wf-draft-alpha',
      );
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenCalledTimes(10);
      expect(mockScopeRuntimeApi.getServiceRevisions).toHaveBeenLastCalledWith(
        'scope-alpha',
        'svc-alpha',
      );
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
      expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
    } finally {
      jest.useRealTimers();
    }
  });

  it('retains the latest reviewed publication when an earlier review resolves late', async () => {
    arrangeSavedDraftPublication();
    const workflowYamlA =
      'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-a\n    type: llm_call\n';
    const workflowYamlB =
      'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-b\n    type: llm_call\n';
    const previewA = {
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-A',
      items: [
        {
          callSiteId: 'call-A',
          requestContractDigest: 'digest-A',
          userServiceId: 'external-A',
          method: 'get',
          pathTemplate: '/requests/a',
          bodyMode: 'none',
          bodyRequired: false,
          responseMode: 'text',
          effectiveRisk: 'read_only',
          approvalRequired: false,
          allowedExecutionModes: ['interactive'],
        },
      ],
    };
    const previewB = {
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-B',
      items: [
        {
          callSiteId: 'call-B',
          requestContractDigest: 'digest-B',
          userServiceId: 'external-B',
          method: 'post',
          pathTemplate: '/requests/b',
          bodyMode: 'json',
          bodyRequired: true,
          responseMode: 'text',
          effectiveRisk: 'write',
          approvalRequired: true,
          allowedExecutionModes: ['interactive'],
        },
      ],
    };
    let resolvePreviewA: (preview: unknown) => void = () => undefined;
    let resolvePreviewB: (preview: unknown) => void = () => undefined;
    const previewAPromise = new Promise((resolve) => {
      resolvePreviewA = resolve;
    });
    const previewBPromise = new Promise((resolve) => {
      resolvePreviewB = resolve;
    });
    mockCreateWorkflowRevisionIdentityCandidate
      .mockReturnValueOnce('rev-A')
      .mockReturnValueOnce('rev-B');
    mockStudioApi.serializeYaml
      .mockResolvedValueOnce({
        yaml: workflowYamlA,
        document: {
          name: 'workflow_alpha',
          roles: [],
          steps: [{ id: 'step-a', type: 'llm_call' }],
        },
        findings: [],
      })
      .mockResolvedValueOnce({
        yaml: workflowYamlB,
        document: {
          name: 'workflow_alpha',
          roles: [],
          steps: [{ id: 'step-b', type: 'llm_call' }],
        },
        findings: [],
      });
    mockStudioApi.previewExplicitRequests
      .mockImplementationOnce(() => previewAPromise)
      .mockImplementationOnce(() => previewBPromise);
    mockStudioApi.saveAndBindWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-B',
      binding: {
        scopeId: 'scope-alpha',
        serviceId: 'svc-alpha',
        displayName: 'Workflow alpha',
        revisionId: 'rev-B',
        targetKind: 'workflow',
        targetName: 'Workflow alpha',
      },
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: 'workflow-revision-B',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    });
    mockScopeRuntimeApi.getServiceRevisions.mockResolvedValue({
      scopeId: 'scope-alpha',
      serviceId: 'svc-alpha',
      serviceKey: 'service-alpha',
      displayName: 'Service alpha',
      defaultServingRevisionId: 'rev-B',
      activeServingRevisionId: 'rev-B',
      deploymentId: 'deployment-service-alpha',
      deploymentStatus: 'Active',
      primaryActorId: 'actor-service-alpha',
      catalogStateVersion: 12,
      catalogLastEventId: 'evt-service-alpha',
      updatedAt: '2026-08-06T10:00:00Z',
      revisions: [
        {
          revisionId: 'rev-B',
          implementationKind: 'workflow',
          status: 'Published',
          artifactHash: 'artifact-publication-B',
          failureReason: '',
          isDefaultServing: false,
          isActiveServing: true,
          isServingTarget: true,
          allocationWeight: 100,
          servingState: 'Active',
          deploymentId: 'deployment-service-alpha',
          primaryActorId: 'actor-service-alpha',
          createdAt: '2026-08-06T10:00:00Z',
          preparedAt: '2026-08-06T10:00:01Z',
          publishedAt: '2026-08-06T10:00:02Z',
          retiredAt: null,
          workflowName: 'Workflow alpha',
          workflowDefinitionActorId: 'actor-workflow-alpha',
          inlineWorkflowCount: 0,
          scriptId: '',
          scriptRevision: '',
          scriptDefinitionActorId: '',
          scriptSourceHash: '',
          staticActorTypeName: '',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Publish workflow',
    });
    const serviceSelect = await within(dialog).findByRole('combobox', {
      name: 'Service',
    });
    fireEvent.mouseDown(serviceSelect);
    fireEvent.click(await screen.findByText('Service alpha'));
    fireEvent.click(
      within(dialog).getByRole('button', { name: 'Review and publish' }),
    );
    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1),
    );

    fireEvent.click(within(dialog).getByRole('button', { name: 'Back' }));
    fireEvent.click(
      await within(dialog).findByRole('button', { name: 'Review and publish' }),
    );
    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(2),
    );
    expect(mockStudioApi.previewExplicitRequests.mock.calls[0][0]).toEqual(
      expect.objectContaining({
        revisionId: 'rev-A',
        workflowYaml: workflowYamlA,
      }),
    );
    expect(mockStudioApi.previewExplicitRequests.mock.calls[1][0]).toEqual(
      expect.objectContaining({
        revisionId: 'rev-B',
        workflowYaml: workflowYamlB,
      }),
    );

    await act(async () => {
      resolvePreviewB(previewB);
      await Promise.resolve();
    });
    expect(
      await within(dialog).findByText('POST /requests/b'),
    ).toBeInTheDocument();

    await act(async () => {
      resolvePreviewA(previewA);
      await Promise.resolve();
    });
    expect(within(dialog).getByText('POST /requests/b')).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Publish' }));

    await waitFor(() =>
      expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          explicitRequestConfirmations: [
            {
              workflowId: 'wf-draft-alpha',
              revisionId: 'rev-B',
              callSiteId: 'call-B',
              requestContractDigest: 'digest-B',
              attestedRisk: 'write',
            },
          ],
          revisionId: 'rev-B',
          workflowYaml: workflowYamlB,
        }),
      ),
    );
    expect(mockStudioApi.saveAndBindWorkflow).toHaveBeenCalledTimes(1);
    expect(await screen.findByText('Workflow published')).toBeInTheDocument();
  });

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

  it('preserves a requested run target when first save adopts a draft id', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source?run=1#run-panel';

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Committed source saved' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-new?run=1#run-panel',
      ),
    );
  });

  it('confirms a workflow save only after the server returns a readable draft', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(
        screen.getByRole('status', { name: 'Workflow save status' }),
      ).toHaveTextContent(/^Saved at /),
    );
    expect(mockConsoleToast.success).not.toHaveBeenCalledWith('Workflow saved');
    expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1);
  });

  it('announces one persistent save lifecycle without implying publication', async () => {
    const parsedDocument = {
      name: 'committed_source',
      roles: [],
      steps: [{ id: 'step-root', type: 'llm_call' }],
    };
    let resolveParse: (() => void) | undefined;
    let resolveSave: (() => void) | undefined;
    mockStudioApi.parseYaml.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveParse = () =>
            resolve({ document: parsedDocument, findings: [] });
        }),
    );
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
      document: parsedDocument,
      findings: [],
    });
    mockStudioApi.saveWorkflow.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveSave = () =>
            resolve({
              kind: 'materialized',
              workflow: {
                workflowId: 'wf-draft-saved',
                name: 'Updated source',
                fileName: 'committed-source.yaml',
                filePath: '/workflows/committed-source.yaml',
                directoryId: 'directory-alpha',
                directoryLabel: 'Workflows',
                yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
                updatedAtUtc: '2026-08-04T10:05:00Z',
                document: parsedDocument,
                draftExists: true,
                findings: [],
              },
            });
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const saveStatus = await screen.findByRole('status', {
      name: 'Workflow save status',
    });
    expect(saveStatus).toHaveTextContent('Saved at 2026-08-04 10:00:00 UTC');
    expect(saveStatus).not.toHaveTextContent('Published');

    fireEvent.change(screen.getByRole('textbox', { name: 'Workflow name' }), {
      target: { value: 'Updated source' },
    });
    expect(saveStatus).toHaveTextContent('Unsaved changes');
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));
    expect(saveStatus).toHaveTextContent('Validating workflow…');

    await act(async () => {
      resolveParse?.();
      await Promise.resolve();
    });
    await waitFor(() => expect(mockStudioApi.saveWorkflow).toHaveBeenCalled());
    expect(saveStatus).toHaveTextContent('Saving workflow…');

    await act(async () => {
      resolveSave?.();
      await Promise.resolve();
    });
    expect(
      await screen.findByText('Saved at 2026-08-04 10:05:00 UTC'),
    ).toBeInTheDocument();
    expect(mockConsoleToast.success).not.toHaveBeenCalledWith('Workflow saved');
    expect(screen.queryByText('Published')).not.toBeInTheDocument();
  });

  it('keeps later edits while retrying a failed create receipt', async () => {
    const unavailable = Object.assign(
      new Error('GET /api/workspace/workflow-drafts/wf-draft-api returned 503'),
      { status: 503 },
    );
    const adoptedDraft = {
      workflowId: 'wf-draft-api',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '/workflows/committed-source.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:02:00Z',
      document: { name: 'committed_source', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    };
    mockStudioApi.saveWorkflow
      .mockResolvedValueOnce({
        kind: 'accepted',
        receipt: {
          accepted: true,
          workflowId: 'wf-draft-api',
          commandId: 'command-draft-api',
          ackStage: 'accepted',
          actorId: 'actor-draft-api',
          workspaceId: 'workspace-alpha',
          ackedAtUtc: '2026-08-04T10:01:00Z',
          readiness: {
            readable: false,
            stage: 'projection_pending',
            message: 'pending',
          },
        },
      })
      .mockResolvedValueOnce({ kind: 'materialized', workflow: adoptedDraft });
    mockStudioApi.getWorkflowDraftFile
      .mockRejectedValueOnce(unavailable)
      .mockResolvedValueOnce(adoptedDraft);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    expect(
      await screen.findByText("Workflow was saved but couldn't be reopened"),
    ).toBeInTheDocument();
    expect(mockStudioApi.getWorkflowDraftFile).toHaveBeenCalledWith(
      'wf-draft-api',
      'scope-alpha',
    );
    const workflowName = screen.getByLabelText('Workflow name');
    expect(workflowName).toBeEnabled();
    fireEvent.change(workflowName, {
      target: { value: 'Updated draft' },
    });
    expect(workflowName).toHaveValue('Updated draft');
    expect(
      screen.getByRole('button', { name: 'Save workflow' }),
    ).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));
    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-api',
      ),
    );
    expect(workflowName).toHaveValue('Updated draft');
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(2),
    );
    expect(mockStudioApi.saveWorkflow).toHaveBeenLastCalledWith(
      expect.objectContaining({
        draftExists: true,
        workflowId: 'wf-draft-api',
      }),
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
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Run' })).toHaveAttribute(
      'title',
      'Publish this workflow before running it.',
    );
  });

  it('keeps a workflow YAML parser error out of the primary editor message', async () => {
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
      findings: [
        {
          code: 'WORKFLOW_UNKNOWN_STEP',
          level: 'warning',
          message: 'A workflow step needs review.',
        },
      ],
    });
    mockStudioApi.parseYaml.mockRejectedValue(
      new Error('yaml parser: unexpected token on line 3'),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('Workflow YAML could not be read.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText('A workflow step needs review.'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('yaml parser: unexpected token on line 3'),
    ).not.toBeInTheDocument();
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

  it('keeps the Canvas/YAML editor view switch discoverable and keyboard operable', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const viewSwitch = await screen.findByRole('radiogroup', {
      name: 'Canvas / YAML',
    });
    const canvas = within(viewSwitch).getByRole('radio', { name: 'Canvas' });

    expect(canvas).toBeChecked();
    canvas.focus();
    fireEvent.keyDown(canvas, { key: 'ArrowRight' });

    expect(await screen.findByLabelText('Workflow YAML')).toBeVisible();
  });

  it('does not honor a requested Run until publication is observed', async () => {
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
      await screen.findByDisplayValue('Support triage'),
    ).toBeInTheDocument();
    expect(
      screen.queryByRole('dialog', { name: 'Run published workflow' }),
    ).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
    expect(mockRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
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
      screen.queryByRole('dialog', { name: 'Run published workflow' }),
    ).not.toBeInTheDocument();
    expect(mockRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it('reports a save failure with a toast instead of a page alert', async () => {
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

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow couldn't be saved",
      ),
    );
    expect(
      screen.queryByText("Workflow couldn't be saved"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText(
        'PUT /api/studio/workflows/wf-committed-source returned 500',
      ),
    ).not.toBeInTheDocument();
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Save failed');
  });

  it('puts editable node configuration first and keeps raw JSON advanced', async () => {
    mockStudioApi.serializeYaml.mockImplementation(async ({ document }) => ({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Updated prompt\n',
      document,
      findings: [],
    }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Original prompt',
    );
    expect(
      within(inspector).queryByLabelText('Raw configuration'),
    ).not.toBeInTheDocument();
    expect(within(inspector).getByText('Advanced options')).toBeVisible();
    fireEvent.change(within(inspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });
    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply changes' }),
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

  it('keeps a raw JSON parser error behind technical details', async () => {
    const invalidRawConfiguration = '{';
    let parserError = '';
    try {
      JSON.parse(invalidRawConfiguration);
    } catch (error) {
      parserError = error instanceof Error ? error.message : String(error);
    }

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.click(within(inspector).getByText('Advanced options'));
    const rawConfiguration =
      await within(inspector).findByLabelText('Raw configuration');

    fireEvent.change(rawConfiguration, {
      target: { value: invalidRawConfiguration },
    });

    expect(
      await within(inspector).findByText(
        'Configuration must be a JSON object.',
      ),
    ).toBeVisible();
    expect(within(inspector).getByText('Technical details')).toBeVisible();
    expect(within(inspector).getByText(parserError)).not.toBeVisible();

    fireEvent.click(within(inspector).getByText('Technical details'));

    expect(within(inspector).getByText(parserError)).toBeVisible();
  });

  it('requires applying or discarding local node changes before saving or running', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.change(within(inspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });

    expect(
      within(inspector).getByText('Apply changes before saving this workflow.'),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Save workflow' }),
    ).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
  });

  it('keeps node configuration editable while the current save awaits a readable draft', async () => {
    const pendingDocument = {
      name: 'committed_source',
      roles: [],
      steps: [
        {
          id: 'step-root',
          type: 'llm_call',
          parameters: { prompt_prefix: 'Original prompt' },
        },
      ],
    };
    const unavailable = Object.assign(
      new Error('GET /api/workspace/workflow-drafts/wf-draft-api returned 503'),
      { status: 503 },
    );
    mockStudioApi.parseYaml.mockResolvedValue({
      document: pendingDocument,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockImplementation(async ({ document }) => ({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
      document,
      findings: [],
    }));
    mockStudioApi.saveWorkflow.mockResolvedValue({
      kind: 'accepted',
      receipt: {
        accepted: true,
        workflowId: 'wf-draft-api',
        commandId: 'command-draft-api',
        ackStage: 'accepted',
        actorId: 'actor-draft-api',
        workspaceId: 'workspace-alpha',
        ackedAtUtc: '2026-08-04T10:01:00Z',
        readiness: {
          readable: false,
          stage: 'projection_pending',
          message: 'pending',
        },
      },
    });
    mockStudioApi.getWorkflowDraftFile.mockRejectedValue(unavailable);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    expect(
      await screen.findByText("Workflow was saved but couldn't be reopened"),
    ).toBeInTheDocument();
    expect(within(inspector).getByLabelText('Instruction')).toBeEnabled();
  });

  it('waits for a node insertion to finish before allowing a competing save', async () => {
    let resolveInsertion:
      | ((result: {
          yaml: string;
          document: unknown;
          findings: readonly [];
        }) => void)
      | undefined;
    mockStudioApi.serializeYaml.mockImplementationOnce(
      ({ document }) =>
        new Promise((resolve) => {
          resolveInsertion = resolve;
          void document;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByRole('button', { name: 'Add node' }),
    ).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Add node' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Insert LLM call node' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
      document: expect.objectContaining({
        steps: expect.arrayContaining([
          expect.objectContaining({ type: 'llm_call' }),
        ]),
      }),
    });
    expect(
      screen.getByRole('button', { name: 'Save workflow' }),
    ).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();

    resolveInsertion?.({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [{ id: 'step-root', type: 'llm_call' }],
      },
      findings: [],
    });

    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Save workflow' }),
      ).toBeEnabled(),
    );
  });

  it('adds a node after the final step and materializes the existing implicit chain', async () => {
    const document = {
      name: 'committed_source',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          next: null,
          branches: {},
        },
        {
          id: 'review_step',
          type: 'human_approval',
          next: null,
          branches: {},
        },
      ],
    };
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document,
      draftExists: false,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockImplementation(
      async ({ document: submittedDocument }) => ({
        yaml: 'serialized',
        document: submittedDocument,
        findings: [],
      }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Add node' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Insert Assign node' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual([
      expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
      expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
      expect.objectContaining({ id: 'assign_step', next: null }),
    ]);
  });

  it('inserts a node after the selected middle step and preserves its successor', async () => {
    const document = {
      name: 'committed_source',
      roles: [],
      steps: [
        {
          id: 'draft_step',
          type: 'llm_call',
          next: 'review_step',
          branches: {},
        },
        {
          id: 'review_step',
          type: 'human_approval',
          next: 'publish_step',
          branches: {},
        },
        {
          id: 'publish_step',
          type: 'emit',
          next: null,
          branches: {},
        },
      ],
    };
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document,
      draftExists: false,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockImplementation(
      async ({ document: submittedDocument }) => ({
        yaml: 'serialized',
        document: submittedDocument,
        findings: [],
      }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:review_step' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Add node' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Insert Assign node' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual([
      expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
      expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
      expect.objectContaining({ id: 'assign_step', next: 'publish_step' }),
      expect.objectContaining({ id: 'publish_step', next: null }),
    ]);
  });

  it('locks structural editing while a save is still in flight', async () => {
    let resolveSave: ((result: unknown) => void) | undefined;
    mockStudioApi.saveWorkflow.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveSave = resolve;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByRole('button', { name: 'Add node' }),
    ).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Add node' }));
    expect(
      await screen.findByRole('button', { name: 'Insert LLM call node' }),
    ).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1),
    );
    expect(screen.getByLabelText('Workflow name')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Add node' })).toBeDisabled();
    expect(
      screen.queryByRole('button', { name: 'Insert LLM call node' }),
    ).not.toBeInTheDocument();

    resolveSave?.({
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

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Add node' })).toBeEnabled(),
    );
  });

  it('reports a failed node insertion with a retryable toast', async () => {
    const serializeFailure = new Error(
      'POST /api/editor/serialize-yaml returned 500',
    );
    mockStudioApi.serializeYaml
      .mockRejectedValueOnce(serializeFailure)
      .mockImplementation(async ({ document }) => ({
        yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
        document,
        findings: [],
      }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Add node' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Insert LLM call node' }),
    );

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledTimes(1),
    );
    expect(screen.queryByText("Couldn't add node")).not.toBeInTheDocument();
    expect(screen.queryByText(serializeFailure.message)).not.toBeInTheDocument();
    const [content] = mockConsoleToast.error.mock.calls[0];
    const toastContent = render(content).container;
    expect(
      within(toastContent).getByText("Couldn't add node"),
    ).toBeVisible();
    const retryButton = within(toastContent).getByRole('button', {
      name: 'Retry',
    });
    expect(retryButton).toBeEnabled();

    fireEvent.click(retryButton);

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(2),
    );
  });

  it('locks node configuration fields while Apply is in flight', async () => {
    let resolveApply: ((result: unknown) => void) | undefined;
    mockStudioApi.serializeYaml.mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveApply = resolve;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    const instruction = within(inspector).getByLabelText('Instruction');
    fireEvent.change(instruction, { target: { value: 'Updated prompt' } });
    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply changes' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(1),
    );
    expect(instruction).toBeDisabled();

    resolveApply?.({
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Updated prompt\n',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [
          {
            id: 'step-root',
            type: 'llm_call',
            parameters: { prompt_prefix: 'Updated prompt' },
          },
        ],
      },
      findings: [],
    });

    await waitFor(() => expect(instruction).toBeEnabled());
  });

  it('requires an observed publication before enabling Run', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const run = await screen.findByRole('button', { name: 'Run' });
    expect(run).toBeDisabled();
    expect(run).toHaveAttribute(
      'title',
      'Publish this workflow before running it.',
    );
  });

  it('opens the published run drawer after publication is observed', async () => {
    arrangeObservedWorkflowPublication();
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    const run = screen.getByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    fireEvent.click(run);

    const drawer = await screen.findByRole('dialog', {
      name: 'Run published workflow',
    });
    expect(drawer).toBeVisible();
    expect(within(drawer).getByText('svc-alpha')).toBeInTheDocument();
    expect(within(drawer).getByText('rev-preview-alpha')).toBeInTheDocument();
  });

  it('locks Run again when the published workflow is edited', async () => {
    await renderPublishedWorkflowPage();

    const run = screen.getByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Workflow alpha updated' },
    });

    expect(run).toBeDisabled();
    expect(run).toHaveAttribute(
      'title',
      'Save and publish the latest changes before running.',
    );
  });

  it('invokes the exact published service instead of running draft YAML', async () => {
    arrangeObservedWorkflowPublication();
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    const run = screen.getByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    fireEvent.click(run);
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-alpha',
        { prompt: 'Review order 42' },
        expect.any(AbortSignal),
        { serviceId: 'svc-alpha' },
      ),
    );
    expect(mockRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it('submits only one published run from a rapid double action', async () => {
    const streamResolvers: Array<(response: Response) => void> = [];
    mockRuntimeRunsApi.streamChat.mockImplementation(
      () =>
        new Promise<Response>((resolve) => {
          streamResolvers.push(resolve);
        }),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    const startRun = await screen.findByRole('button', { name: 'Start run' });

    await act(async () => {
      startRun.click();
      startRun.click();
      await Promise.resolve();
    });

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(1),
    );

    streamResolvers[0]?.({
      ok: true,
      body: {
        getReader: () => ({
          read: async () => ({ done: true, value: undefined }),
          releaseLock: () => undefined,
        }),
      },
    } as unknown as Response);
  });

  it('requires the current string input contract before invoking a published workflow', async () => {
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const input = await screen.findByRole('textbox', { name: 'Input' });
    const startRun = screen.getByRole('button', { name: 'Start run' });

    expect(startRun).toBeDisabled();
    expect(screen.getByText('Required')).toBeInTheDocument();
    fireEvent.change(input, { target: { value: 'Review order 42' } });
    expect(startRun).toBeEnabled();

    fireEvent.click(startRun);

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-alpha',
        { prompt: 'Review order 42' },
        expect.any(AbortSignal),
        { serviceId: 'svc-alpha' },
      ),
    );
  });

  it('maps backend prompt validation to the run input without losing it', async () => {
    mockRuntimeRunsApi.streamChat.mockRejectedValue(
      Object.assign(new Error('The request could not be validated.'), {
        fieldErrors: { Prompt: ['Use at least three characters.'] },
      }),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const input = await screen.findByRole('textbox', { name: 'Input' });
    fireEvent.change(input, { target: { value: 'x' } });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    expect(
      await screen.findByText('Use at least three characters.'),
    ).toBeInTheDocument();
    expect(input).toHaveValue('x');
    expect(input).toHaveAttribute('aria-invalid', 'true');
  });

  it('retains the accepted run while the panel is closed and follows its observed result', async () => {
    mockWorkflowActivityApi.getRun
      .mockResolvedValueOnce(
        createEditorRunDetail({
          runId: 'run-observed-alpha',
          stateVersion: 7,
          status: 'running',
        }),
      )
      .mockResolvedValueOnce(
        createEditorRunDetail({
          finalOutput: 'Order 42 is ready for approval.',
          runId: 'run-observed-alpha',
          stateVersion: 8,
          status: 'completed',
        }),
      );
    mockRuntimeRunsApi.streamChat
      .mockResolvedValueOnce(
        createSseResponse([
          { runStarted: { runId: 'run-observed-alpha' } },
          { runFinished: { runId: 'run-observed-alpha' } },
        ]),
      )
      .mockResolvedValueOnce(
        createSseResponse([
          { runStarted: { runId: 'run-again-beta' } },
          { runFinished: { runId: 'run-again-beta' } },
        ]),
      );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Start run' }));

    expect(await screen.findByText('Running')).toBeInTheDocument();
    expect(screen.getByText('step-verify')).toBeInTheDocument();
    expect(
      screen.getByRole('link', { name: 'Open run details' }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-observed-alpha',
    );

    fireEvent.click(screen.getByText('Close'));
    expect(
      screen.queryByRole('dialog', { name: 'Run published workflow' }),
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(await screen.findByText('Running')).toBeInTheDocument();
    expect(
      within(screen.getByRole('region', { name: 'Run result' })).getByText(
        'Review order 42',
      ),
    ).toBeInTheDocument();

    fireEvent.click(
      screen.getByRole('button', { name: 'Check latest status' }),
    );

    expect(await screen.findByText('Completed')).toBeInTheDocument();
    expect(
      screen.getByText('Order 42 is ready for approval.'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox', { name: 'Input' }), {
      target: { value: 'A different input' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Run again' }));

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(2),
    );
    expect(mockRuntimeRunsApi.streamChat.mock.calls[1]).toEqual([
      'scope-alpha',
      { prompt: 'Review order 42' },
      expect.any(AbortSignal),
      { serviceId: 'svc-alpha' },
    ]);
  });

  it('keeps an unidentified published run from being submitted again after live updates end', async () => {
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    expect(
      await screen.findByText(
        'Live updates ended. Open Activity to check the latest status.',
      ),
    ).toBeInTheDocument();
    const startRun = await screen.findByRole('button', { name: 'Start run' });
    expect(startRun).toBeDisabled();
    fireEvent.click(startRun);
    expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(1);
  });

  it('ignores a buffered old run event after opening and starting another workflow', async () => {
    arrangeObservedWorkflowPublication();
    const otherWorkflow = {
      workflowId: 'wf-draft-beta',
      name: 'Other workflow',
      fileName: 'other-workflow.yaml',
      filePath: '/workflows/other-workflow.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: other_workflow\nroles: []\nsteps:\n  - id: step-beta\n    type: llm_call\n',
      updatedAtUtc: '2026-08-05T10:00:00Z',
      document: {
        name: 'other_workflow',
        roles: [],
        steps: [{ id: 'step-beta', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    };
    mockStudioApi.getWorkflow.mockImplementation((requestedWorkflowId) =>
      Promise.resolve(
        requestedWorkflowId === otherWorkflow.workflowId
          ? otherWorkflow
          : {
              workflowId: 'wf-draft-alpha',
              name: 'Workflow alpha',
              fileName: 'workflow-alpha.yaml',
              filePath: '/workflows/workflow-alpha.yaml',
              directoryId: 'directory-alpha',
              directoryLabel: 'Workflows',
              yaml: 'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
              updatedAtUtc: '2026-08-06T10:00:00Z',
              document: {
                name: 'workflow_alpha',
                roles: [],
                steps: [{ id: 'step-alpha', type: 'llm_call' }],
              },
              draftExists: true,
              findings: [],
            },
      ),
    );

    const encoder = new TextEncoder();
    let firstReadStarted = false;
    let resolveFirstRead:
      | ((value: ReadableStreamReadResult<Uint8Array>) => void)
      | undefined;
    let resolveFirstStreamReleased: (() => void) | undefined;
    const firstStreamReleased = new Promise<void>((resolve) => {
      resolveFirstStreamReleased = resolve;
    });
    const deferredFirstResponse = {
      body: {
        getReader: () => ({
          read: () => {
            firstReadStarted = true;
            return new Promise<ReadableStreamReadResult<Uint8Array>>(
              (resolve) => {
                resolveFirstRead = resolve;
              },
            );
          },
          releaseLock: () => resolveFirstStreamReleased?.(),
        }),
      },
      ok: true,
    } as unknown as Response;
    mockRuntimeRunsApi.streamChat.mockResolvedValueOnce(deferredFirstResponse);

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));
    expect(await screen.findByText('Run accepted')).toBeInTheDocument();
    await waitFor(() => expect(firstReadStarted).toBe(true));

    setMockLocation(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-beta',
    );
    expect(await screen.findByDisplayValue('Other workflow')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Run' })).toHaveAttribute(
      'title',
      'Publish this workflow before running it.',
    );
    expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(1);

    await act(async () => {
      resolveFirstRead?.({
        done: false,
        value: encoder.encode(
          'data: {"runError":{"message":"Old workflow failed."}}\n\n',
        ),
      });
      await firstStreamReleased;
    });

    expect(screen.queryByText('Run failed')).not.toBeInTheDocument();
    expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(1);
  });

  it('opens a run detail only after the SSE run id is observed by the activity API', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        runId: 'run-observed-alpha',
        stateVersion: 7,
        status: 'running',
      }),
    );
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([
        { runStarted: { runId: 'run-observed-alpha' } },
        { runFinished: { runId: 'run-observed-alpha' } },
      ]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    expect(await screen.findByText('Observed in Activity')).toBeInTheDocument();
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-observed-alpha',
    );

    fireEvent.click(screen.getByRole('link', { name: 'Open run details' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-observed-alpha',
    );
  });

  it('keeps a stream run error as an execution failure even when no message is supplied', async () => {
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([{ runError: {} }]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    expect(await screen.findByText('Run failed')).toBeInTheDocument();
    expect(
      screen.queryByText(
        'Live updates ended. Open Activity to check the latest status.',
      ),
    ).not.toBeInTheDocument();
  });

  it('observes the exact failed run when its SSE error includes a run id', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        finalError: 'The workflow failed.',
        runId: 'run-failed-alpha',
        stateVersion: 8,
        status: 'failed',
      }),
    );
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([
        {
          runError: {
            message: 'The workflow failed.',
            runId: 'run-failed-alpha',
          },
        },
      ]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

    expect(await screen.findByText('Failed')).toBeInTheDocument();
    expect(await screen.findByText('Observed in Activity')).toBeInTheDocument();
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-failed-alpha',
    );

    fireEvent.click(screen.getByRole('link', { name: 'Open run details' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity/run-failed-alpha',
    );
  });

  it('keeps a failed node configuration apply retryable without exposing the transport error', async () => {
    const serializeFailure = new Error(
      'PUT /api/editor/serialize-yaml returned 500',
    );
    mockStudioApi.serializeYaml
      .mockRejectedValueOnce(serializeFailure)
      .mockImplementation(async ({ document }) => ({
        yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n    parameters:\n      prompt_prefix: Updated prompt\n',
        document,
        findings: [],
      }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.change(within(inspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });
    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply changes' }),
    );

    expect(
      await screen.findByText("Couldn't apply configuration"),
    ).toBeVisible();
    expect(screen.getByText(serializeFailure.message)).not.toBeVisible();
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );
    expect(
      within(inspector).getByRole('button', { name: 'Apply changes' }),
    ).toBeEnabled();

    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply changes' }),
    );
    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(
        within(inspector).getByRole('button', { name: 'Apply changes' }),
      ).toBeDisabled(),
    );
  });

  it('asks before discarding unapplied node changes during vNext navigation', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.change(within(inspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });
    fireEvent.click(screen.getAllByRole('link', { name: 'Activity' })[0]);

    expect(history.push).not.toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity',
    );
    const discardDialog = await screen.findByRole('dialog', {
      name: 'Discard node changes?',
    });
    await waitFor(() =>
      expect(
        within(discardDialog).getByText(/unapplied changes/i),
      ).toBeVisible(),
    );
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );

    fireEvent.click(
      within(discardDialog).getByRole('button', { name: 'Cancel' }),
    );
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );

    fireEvent.click(screen.getAllByRole('link', { name: 'Activity' })[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/activity',
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

  it('requires an explicit decision before switching a dirty editor to another workflow route', async () => {
    const otherWorkflow = {
      workflowId: 'wf-draft-other',
      name: 'Other workflow',
      fileName: 'other-workflow.yaml',
      filePath: '/workflows/other-workflow.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: other_workflow\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:03:00Z',
      document: { name: 'other_workflow', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    };
    mockStudioApi.getWorkflow.mockImplementation((requestedWorkflowId) =>
      Promise.resolve(
        requestedWorkflowId === otherWorkflow.workflowId
          ? otherWorkflow
          : {
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
            },
      ),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Unsaved source changes' },
    });

    setMockLocation(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-other',
    );

    expect(
      await screen.findByRole('dialog', { name: 'Unsaved workflow changes' }),
    ).toBeInTheDocument();
    expect(screen.getByDisplayValue('Unsaved source changes')).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Discard and leave' }));

    expect(
      await screen.findByDisplayValue('Other workflow'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Other workflow saved' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenLastCalledWith(
        expect.objectContaining({ workflowId: 'wf-draft-other' }),
      ),
    );
  });

  it('requires an explicit decision before switching a dirty editor to another scope', async () => {
    const scopeBetaWorkflow = {
      workflowId: 'wf-shared-route',
      name: 'Scope beta workflow',
      fileName: 'scope-beta.yaml',
      filePath: '/workflows/scope-beta.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: scope_beta_workflow\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:03:00Z',
      document: { name: 'scope_beta_workflow', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    };
    const scopeAlphaWorkflow = {
      workflowId: 'wf-shared-route',
      name: 'Scope alpha workflow',
      fileName: 'scope-alpha.yaml',
      filePath: '/workflows/scope-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: scope_alpha_workflow\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: { name: 'scope_alpha_workflow', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    };
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-shared-route';
    mockStudioApi.getWorkflow.mockImplementation(
      (_requestedWorkflowId, requestedScopeId) =>
        Promise.resolve(
          requestedScopeId === 'scope-beta'
            ? scopeBetaWorkflow
            : scopeAlphaWorkflow,
        ),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Scope alpha workflow'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Unsaved scope alpha changes' },
    });

    setMockLocation(
      '/scopes/scope-beta/workflow-activity-vnext/workflows/wf-shared-route',
    );

    expect(
      await screen.findByRole('dialog', { name: 'Unsaved workflow changes' }),
    ).toBeInTheDocument();
    expect(
      screen.getByDisplayValue('Unsaved scope alpha changes'),
    ).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Discard and leave' }));

    expect(
      await screen.findByDisplayValue('Scope beta workflow'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Scope beta workflow saved' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenLastCalledWith(
        expect.objectContaining({
          scopeId: 'scope-beta',
          workflowId: 'wf-shared-route',
        }),
      ),
    );
  });

  it('continues to the requested workflow after saving a committed source creates a draft', async () => {
    mockStudioApi.getWorkflow.mockImplementation((requestedWorkflowId) =>
      Promise.resolve({
        workflowId: requestedWorkflowId,
        name:
          requestedWorkflowId === 'wf-draft-other'
            ? 'Other workflow'
            : 'Committed source',
        fileName: 'workflow.yaml',
        filePath: '',
        directoryId: 'directory-alpha',
        directoryLabel: 'Workflows',
        yaml: 'name: workflow\nroles: []\nsteps: []\n',
        updatedAtUtc: '2026-08-04T10:00:00Z',
        document: { name: 'workflow', roles: [], steps: [] },
        draftExists: requestedWorkflowId === 'wf-draft-other',
        findings: [],
      }),
    );
    (history.replace as jest.Mock).mockImplementation((target: string) => {
      setMockLocation(target);
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByDisplayValue('Committed source'),
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Committed source updated' },
    });
    setMockLocation(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-other?run=1#requested-run',
    );
    expect(
      await screen.findByRole('dialog', { name: 'Unsaved workflow changes' }),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Save and leave' }));

    await waitFor(() =>
      expect(history.replace).toHaveBeenCalledWith(
        '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-new?run=1#requested-run',
      ),
    );
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-other?run=1#requested-run',
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
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([]);
    mockScopesApi.listWorkflows.mockResolvedValue([]);
  });

  afterEach(() => cleanupTestQueryClients());

  it('warns about a duplicate workflow name without blocking creation', async () => {
    mockScopesApi.listWorkflows.mockResolvedValue([
      {
        scopeId: 'scope-alpha',
        workflowId: 'wf-existing-incident-review',
        displayName: 'Incident review',
        serviceKey: 'svc-incident-review',
        workflowName: 'incident_review',
        actorId: 'definition-incident-review',
        activeRevisionId: 'rev-incident-review-3',
        deploymentId: 'deployment-incident-review',
        deploymentStatus: 'active',
        updatedAt: '2026-08-04T09:00:00Z',
      },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const blankButton = await screen.findByRole('button', {
      name: 'Start blank',
    });
    await waitFor(() => expect(blankButton).toBeEnabled());
    fireEvent.click(blankButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: ' incident REVIEW ' },
    });

    expect(
      await screen.findByText(
        'Another workflow already uses this name. Duplicate names are allowed.',
      ),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Create and open' }),
    ).toBeEnabled();
  });

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
    fireEvent.click(screen.getByRole('button', { name: 'Create and open' }));

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

  it('does not expose the only built-in save location', async () => {
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

    expect(screen.queryByLabelText('Save to')).not.toBeInTheDocument();
    expect(screen.queryByText('Default workspace')).not.toBeInTheDocument();
    expect(screen.queryByText('scope-alpha')).not.toBeInTheDocument();
  });

  it('reports a submission failure with a toast and keeps method changes usable', async () => {
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
    fireEvent.change(screen.getByLabelText('What should this workflow do?'), {
      target: { value: 'Summarize this week' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow couldn't be created",
      ),
    );
    expect(
      screen.queryByText("Workflow couldn't be created"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText('LLM service rejected the request'),
    ).not.toBeInTheDocument();
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
    fireEvent.click(
      screen.getByRole('button', { name: 'Use template and open' }),
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
    fireEvent.click(screen.getByRole('button', { name: 'Import and open' }));

    expect(await screen.findByText('Invalid YAML')).toBeInTheDocument();
    expect(mockStudioApi.createWorkflowDraft).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Workflow YAML')).toHaveValue('name: [broken');
  });
});
