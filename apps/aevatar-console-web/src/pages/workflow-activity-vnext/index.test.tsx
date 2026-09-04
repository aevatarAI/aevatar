import { QueryClient } from '@tanstack/react-query';
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import {
  clearStoredAuthSession,
  persistAuthSession,
} from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from '../../../tests/reactQueryTestUtils';
import WorkflowActivityVNextPage from './index';
import { workflowActivityVNextCss } from './styles';

jest.mock('@/shared/api/workflowScheduleApi', () => ({
  workflowScheduleApi: {
    create: jest.fn(),
    delete: jest.fn(),
    disable: jest.fn(),
    enable: jest.fn(),
    get: jest.fn(),
    list: jest.fn(),
    preview: jest.fn(),
    runNow: jest.fn(),
    update: jest.fn(),
  },
}));

type SerializableWorkflowDocument = {
  readonly name?: string;
};

type SaveWorkflowRequestProbe = {
  readonly workflowName: string;
  readonly yaml: string;
};

let mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';
let mockHistoryMutatesLocation = false;
const mockLocationSubscribers = new Set<() => void>();
const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

const readMockUrl = () => new URL(mockLocation, 'http://console.local');

type CatalogueRowFixture = {
  readonly capabilities?: {
    readonly open: {
      readonly available: boolean;
      readonly unavailableReason: string | null;
    };
    readonly activity: {
      readonly available: boolean;
      readonly unavailableReason: string | null;
    };
    readonly rename: {
      readonly available: boolean;
      readonly unavailableReason: string | null;
    };
    readonly delete: {
      readonly available: boolean;
      readonly unavailableReason: string | null;
    };
  };
  readonly name: string;
  readonly workflowId: string;
  readonly committed?: {
    readonly serviceKey: string;
    readonly workflowName: string;
    readonly actorId: string;
    readonly activeRevisionId: string;
    readonly deploymentId: string;
    readonly deploymentStatus: string;
  } | null;
  readonly description?: string;
  readonly hasCommittedSource?: boolean;
  readonly hasDraftSource?: boolean;
  readonly updatedAtSource?: string;
  readonly updatedAtUtc?: string;
};

function createCatalogueRow(overrides: CatalogueRowFixture) {
  const committed = overrides.committed ?? null;
  return {
    scopeId: 'scope-alpha',
    description: '',
    hasDraftSource: true,
    hasCommittedSource: Boolean(committed),
    updatedAtUtc: '2026-08-04T10:00:00Z',
    updatedAtSource: committed ? 'committed' : 'draft',
    capabilities: {
      open: { available: true, unavailableReason: null },
      activity: {
        available: Boolean(committed),
        unavailableReason: committed ? null : 'committed_source_missing',
      },
      rename: { available: true, unavailableReason: null },
      delete: { available: true, unavailableReason: null },
    },
    sourceWatermarkUtc: '2026-08-04T10:00:00Z',
    committed,
    ...overrides,
  };
}

function createCatalogueResponse(
  items: ReturnType<typeof createCatalogueRow>[],
  nextPageToken: string | null = null,
) {
  return {
    items,
    nextPageToken,
    freshness: {
      refreshWatermarkUtc: '2026-08-04T10:00:00Z',
      sourceVersionSemantics: 'max source timestamp',
    },
    search: {
      searchableFields: ['name', 'description', 'workflowId'],
      caseSemantics: 'ordinal ignore case',
      unicodeNormalization: 'FormKC',
      maximumQueryLength: 128,
      emptyQuerySemantics: 'no filter',
      workflowIdSemantics: 'exact or prefix',
    },
  };
}

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
  isStudioApiErrorCode: (error: unknown, status: number, code: string) =>
    Boolean(
      error &&
        typeof error === 'object' &&
        'status' in error &&
        error.status === status &&
        'code' in error &&
        error.code === code,
    ),
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
    getAuthSession: jest.fn(async () => ({
      authenticated: true,
      enabled: true,
      profile: null,
      session: { authenticated: true },
      subject: 'test-user',
    })),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    getWorkflow: jest.fn(),
    getWorkflowDraft: jest.fn(),
    getWorkflowDraftFile: jest.fn(),
    instantiateWorkflowTemplate: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    parseYaml: jest.fn(),
    previewExplicitRequests: jest.fn(),
    publishWorkflow: jest.fn(),
    saveWorkflow: jest.fn(),
    saveAndBindWorkflow: jest.fn(),
    saveUserLlmSettings: jest.fn(),
    serializeYaml: jest.fn(),
    updateWorkflowDraft: jest.fn(),
  },
}));

jest.mock('@/shared/api/runtimeCatalogApi', () => ({
  runtimeCatalogApi: {
    getWorkflowTemplate: jest.fn(),
    listWorkflowTemplates: jest.fn(),
  },
}));

jest.mock('@/shared/studio/explicitRequestConfirmation', () => ({
  createWorkflowRevisionIdentityCandidate: jest.fn(),
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
    archiveWorkflow: jest.fn(),
    getWorkflowDetail: jest.fn(),
    listWorkflows: jest.fn(),
    queryWorkflowCatalogue: jest.fn(),
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
    streamEndpoint: jest.fn(),
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
  history: {
    push: jest.fn(),
    replace: jest.fn((target: string) => {
      if (!mockHistoryMutatesLocation) return;
      mockLocation = target;
      for (const listener of mockLocationSubscribers) listener();
    }),
  },
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
      picture: string | null;
    } | null;
  }) => (
    <button
      data-auth-source={principal === undefined ? 'stored' : 'account'}
      data-picture={principal?.picture ?? ''}
      title={principal?.displayName}
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

const mockWorkflowStudioCanvasProps = jest.fn();

jest.mock(
  '@/pages/team-member-workflow-studio/components/WorkflowStudioCanvas',
  () => ({
    __esModule: true,
    default: ({
      nodes,
      onAddFirstStep,
      onCanvasSelect,
      onConnectNodes,
      onDeleteEdges,
      onDeleteNodes,
      onEdgeSelect,
      onNodeLayoutChange,
      onNodeSelect,
    }: {
      nodes: readonly { readonly id: string }[];
      onAddFirstStep?: () => void;
      onCanvasSelect?: () => void;
      onConnectNodes?: (sourceNodeId: string, targetNodeId: string) => void;
      onDeleteEdges?: (edgeIds: string[]) => Promise<void> | void;
      onDeleteNodes?: (nodeIds: string[]) => Promise<void> | void;
      onEdgeSelect?: (edgeId: string) => void;
      onNodeLayoutChange?: (
        nodes: readonly {
          readonly id: string;
          readonly position?: { readonly x: number; readonly y: number };
        }[],
      ) => void;
      onNodeSelect?: (nodeId: string) => void;
    }) => {
      mockWorkflowStudioCanvasProps({
        onAddFirstStep,
        onCanvasSelect,
        onConnectNodes,
        onDeleteEdges,
        onDeleteNodes,
        onEdgeSelect,
        onNodeLayoutChange,
        onNodeSelect,
      });
      return (
        <div
          data-connectable={String(Boolean(onConnectNodes))}
          data-deletable={String(Boolean(onDeleteEdges && onDeleteNodes))}
          data-edge-selectable={String(Boolean(onEdgeSelect))}
          data-layout-editable={String(Boolean(onNodeLayoutChange))}
          data-testid="workflow-studio-canvas"
        >
          {nodes.map((node) => (
            <button
              key={node.id}
              onClick={() => onNodeSelect?.(node.id)}
              type="button"
            >
              Select {node.id}
            </button>
          ))}
          <button
            disabled={!onConnectNodes || nodes.length < 2}
            onClick={() => onConnectNodes?.(nodes[0].id, nodes[1].id)}
            type="button"
          >
            Connect first two nodes
          </button>
        </div>
      );
    },
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
  instantiateWorkflowTemplate: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  parseYaml: jest.Mock;
  previewExplicitRequests: jest.Mock;
  publishWorkflow: jest.Mock;
  saveWorkflow: jest.Mock;
  saveAndBindWorkflow: jest.Mock;
  saveUserLlmSettings: jest.Mock;
  serializeYaml: jest.Mock;
  updateWorkflowDraft: jest.Mock;
};
const mockRuntimeCatalogApi = jest.requireMock('@/shared/api/runtimeCatalogApi')
  .runtimeCatalogApi as {
  getWorkflowTemplate: jest.Mock;
  listWorkflowTemplates: jest.Mock;
};
const mockCreateWorkflowRevisionIdentityCandidate = jest.requireMock(
  '@/shared/studio/explicitRequestConfirmation',
).createWorkflowRevisionIdentityCandidate as jest.Mock;
const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
  archiveWorkflow: jest.Mock;
  getWorkflowDetail: jest.Mock;
  listWorkflows: jest.Mock;
  queryWorkflowCatalogue: jest.Mock;
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
  streamEndpoint: jest.Mock;
};
const mockWorkflowActivityApi = jest.requireMock(
  '@/shared/api/workflowActivityApi',
).workflowActivityApi as {
  getRun: jest.Mock;
};
const mockWorkflowScheduleApi = jest.requireMock(
  '@/shared/api/workflowScheduleApi',
).workflowScheduleApi as {
  list: jest.Mock;
};
const mockObserveUserLlmSave = jest.requireMock(
  '@/pages/settings/userLlmSaveObservation',
).observeUserLlmSave as jest.Mock;

describe('Workflow Activity vNext catalogue', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';
    mockHistoryMutatesLocation = false;
    jest.clearAllMocks();
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([]),
    );
    mockWorkflowScheduleApi.list.mockResolvedValue({
      items: [],
      nextCursor: null,
      totalCount: 0,
    });
    mockScopesApi.archiveWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-alpha',
      deploymentId: 'dep-alpha',
      commandHandle: {
        stage: 'deactivate_deployment',
        targetActorId: 'deployment-manager-alpha',
        commandId: 'cmd-archive-alpha',
        correlationId: 'corr-archive-alpha',
      },
      readModelUrl: '/api/scopes/scope-alpha/workflows/wf-alpha',
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
    mockServicesApi.deactivateDeployment.mockResolvedValue({
      targetActorId: 'deployment-manager-alpha',
      commandId: 'cmd-archive-alpha',
      correlationId: 'corr-archive-alpha',
    });
  });

  afterEach(() => {
    jest.useRealTimers();
    cleanupTestQueryClients();
  });

  it('uses backend catalogue views and preserves backend row order', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-older-first',
          name: 'Older first',
          updatedAtUtc: '2026-08-01T10:00:00Z',
        }),
        createCatalogueRow({
          workflowId: 'wf-newer-second',
          name: 'Newer second',
          updatedAtUtc: '2026-08-05T10:00:00Z',
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Older first')).toBeInTheDocument();
    const workflowNames = screen
      .getAllByRole('row')
      .slice(1)
      .map((row) => within(row).getAllByRole('cell')[0]?.textContent);
    expect(workflowNames).toEqual(['Older first', 'Newer second']);
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
      {
        scopeId: 'scope-alpha',
        view: 'all',
        query: undefined,
        cursor: undefined,
        take: 50,
      },
      expect.any(AbortSignal),
    );
    expect(mockStudioApi.listWorkflowDrafts).not.toHaveBeenCalled();
    expect(mockScopesApi.listWorkflows).not.toHaveBeenCalled();

    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: 'Workflow view' }),
    );
    expect(
      await screen.findByRole('option', { name: 'All workflows' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Drafts' })).toBeInTheDocument();
    expect(
      screen.getByRole('option', { name: 'Show archived workflows' }),
    ).toBeInTheDocument();
  });

  it('reserves a stable action column for published workflow schedules', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-published',
          name: 'Published workflow',
          committed: {
            serviceKey: 'svc-published',
            workflowName: 'published_workflow',
            actorId: 'actor-published',
            activeRevisionId: 'revision-published',
            deploymentId: 'deployment-published',
            deploymentStatus: 'Running',
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const table = await screen.findByRole('table');
    const columns = table.querySelectorAll('col');
    expect(table).toHaveClass('wa-vnext__table--workflow-catalogue');
    expect(columns).toHaveLength(4);
    expect(columns[3]).toHaveStyle({ width: '500px' });
    expect(workflowActivityVNextCss).toContain(
      '.wa-vnext__table--workflow-catalogue { min-width: 1160px; }',
    );
  });

  it('opens the published Workflow Schedule action as a management modal', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-published',
          name: 'Published workflow',
          committed: {
            serviceKey: 'svc-published',
            workflowName: 'published_workflow',
            actorId: 'actor-published',
            activeRevisionId: 'revision-published',
            deploymentId: 'deployment-published',
            deploymentStatus: 'Running',
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Manage schedules for Published workflow',
      }),
    );

    await waitFor(() =>
      expect(
        screen.getByText('Schedules for Published workflow', {
          selector: '.ant-modal-title',
        }),
      ).toBeVisible(),
    );
    expect(screen.getByText('No schedules yet')).toBeVisible();
    expect(screen.getByRole('button', { name: 'New schedule' })).toBeVisible();
    expect(
      screen.queryByText('Recurring runs for Published workflow'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('WORKFLOW SCHEDULE')).not.toBeInTheDocument();
    expect(screen.queryByText('What will happen')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Review schedule' }),
    ).not.toBeInTheDocument();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
  });

  it('refreshes the workflow catalogue when returning from the editor', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          gcTime: Infinity,
          refetchOnWindowFocus: false,
          retry: false,
          staleTime: 30_000,
        },
      },
    });
    mockStudioApi.getWorkspaceSettings.mockResolvedValue({
      runtimeBaseUrl: '',
      directories: [],
    });
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-alpha',
      name: 'Workflow alpha',
      fileName: 'workflow-alpha.yaml',
      filePath: '/workflows/workflow-alpha.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: workflow_alpha\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-11T10:00:00Z',
      document: { name: 'workflow_alpha', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    });
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: false,
      scopeId: 'scope-alpha',
      workflow: null,
      source: null,
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />, queryClient);

    await waitFor(() =>
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(1),
    );

    setMockLocation(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-alpha',
    );
    expect(await screen.findByDisplayValue('Workflow alpha')).toBeVisible();

    setMockLocation('/scopes/scope-alpha/workflow-activity-vnext/workflows');

    await waitFor(() =>
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2),
    );
  });

  it('reports only the specific delete refresh failure after the draft was removed', async () => {
    mockScopesApi.queryWorkflowCatalogue
      .mockResolvedValueOnce(
        createCatalogueResponse([
          createCatalogueRow({
            workflowId: 'wf-draft-alpha',
            name: 'Support triage',
          }),
        ]),
      )
      .mockRejectedValueOnce(new Error('refresh returned 503'));
    mockStudioApi.deleteWorkflowDraft.mockResolvedValue(undefined);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const draftName = await screen.findByText('Support triage');
    fireEvent.click(
      within(draftName.closest('tr') as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in Workspace',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Draft was deleted, but the workflow list could not refresh. Please try again.',
      ),
    );
    expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.deleteWorkflowDraft).toHaveBeenCalledTimes(1);
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
  });

  it('keeps the archived workflow view on the frontend and backend', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=archived';
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-archived',
          name: 'Archived workflow',
          committed: {
            serviceKey: 'svc-archived',
            workflowName: 'archived_workflow',
            actorId: 'actor-archived',
            activeRevisionId: 'rev-archived',
            deploymentId: 'dep-archived',
            deploymentStatus: 'Deactivated',
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Archived workflow')).toBeInTheDocument();
    expect(screen.getByText('Show archived workflows')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
      expect.objectContaining({ view: 'archived' }),
      expect.any(AbortSignal),
    );
    expect(history.replace).not.toHaveBeenCalled();
  });

  it('sends the drafts view and restored search to the backend', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?q=support&view=drafts';
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-support',
          name: 'Support triage',
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Support triage')).toBeInTheDocument();
    expect(
      screen.getByRole('searchbox', { name: 'Search workflows' }),
    ).toHaveValue('support');
    expect(screen.getByText('Drafts')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
      {
        scopeId: 'scope-alpha',
        view: 'drafts',
        query: 'support',
        cursor: undefined,
        take: 50,
      },
      expect.any(AbortSignal),
    );
  });

  it('excludes published workflows from the Drafts product view', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=drafts';
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-draft-alpha',
          name: 'Support triage draft',
        }),
        createCatalogueRow({
          workflowId: 'wf-published-alpha',
          name: 'Published support workflow',
          committed: {
            serviceKey: 'service-key-alpha',
            workflowName: 'published_support_workflow',
            actorId: 'actor-alpha',
            activeRevisionId: 'rev-alpha',
            deploymentId: 'dep-alpha',
            deploymentStatus: 'Active',
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Support triage draft')).toBeInTheDocument();
    expect(
      screen.queryByText('Published support workflow'),
    ).not.toBeInTheDocument();
  });

  it('keeps Drafts pagination available when a page contains only published workflows', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=drafts';
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { cursor?: string }) =>
        Promise.resolve(
          input.cursor === 'page-2'
            ? createCatalogueResponse([
                createCatalogueRow({
                  workflowId: 'wf-draft-beta',
                  name: 'Billing draft',
                }),
              ])
            : createCatalogueResponse(
                [
                  createCatalogueRow({
                    workflowId: 'wf-published-alpha',
                    name: 'Published support workflow',
                    committed: {
                      serviceKey: 'service-key-alpha',
                      workflowName: 'published_support_workflow',
                      actorId: 'actor-alpha',
                      activeRevisionId: 'rev-alpha',
                      deploymentId: 'dep-alpha',
                      deploymentStatus: 'Active',
                    },
                  }),
                ],
                'page-2',
              ),
        ),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('No matching workflows'),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Published support workflow'),
    ).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));

    expect(await screen.findByText('Billing draft')).toBeInTheDocument();
  });

  it('shows table loading while manually refreshing the catalogue', async () => {
    let resolveRefresh!: (
      response: ReturnType<typeof createCatalogueResponse>,
    ) => void;
    mockScopesApi.queryWorkflowCatalogue
      .mockResolvedValueOnce(
        createCatalogueResponse([
          createCatalogueRow({
            workflowId: 'wf-draft-alpha',
            name: 'Support triage',
          }),
        ]),
      )
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveRefresh = resolve;
          }),
      );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('Support triage')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh workflows' }));

    await waitFor(() =>
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2),
    );
    expect(screen.getByText('Loading workflows')).toHaveClass(
      'aevatar-loading-visually-hidden',
    );

    act(() => resolveRefresh(createCatalogueResponse([])));
    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
  });

  it('debounces search despite URL synchronization and aborts an obsolete request', async () => {
    let supportSignal: AbortSignal | undefined;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { query?: string }, signal?: AbortSignal) => {
        if (input.query === 'support') {
          supportSignal = signal;
          return new Promise(() => undefined);
        }
        return Promise.resolve(createCatalogueResponse([]));
      },
    );

    jest.useFakeTimers();
    const rendered = renderWithQueryClient(<WorkflowActivityVNextPage />);
    try {
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(1);

      mockHistoryMutatesLocation = true;
      const search = screen.getByRole('searchbox', {
        name: 'Search workflows',
      });
      fireEvent.change(search, { target: { value: ' support ' } });
      await act(async () => {
        await jest.advanceTimersByTimeAsync(299);
      });
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(1);
      expect(search).toHaveValue(' support ');
      await act(async () => {
        await jest.advanceTimersByTimeAsync(1);
      });
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
        expect.objectContaining({ query: 'support' }),
        expect.any(AbortSignal),
      );
      expect(supportSignal?.aborted).toBe(false);

      fireEvent.change(search, { target: { value: 'billing' } });
      await act(async () => {
        await jest.advanceTimersByTimeAsync(300);
      });
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
        expect.objectContaining({ query: 'billing' }),
        expect.any(AbortSignal),
      );
      expect(supportSignal?.aborted).toBe(true);
    } finally {
      rendered.unmount();
      jest.clearAllTimers();
      jest.useRealTimers();
    }
  });

  it('loads the next backend page and appends it without reordering', async () => {
    let resolveNextPage!: (
      response: ReturnType<typeof createCatalogueResponse>,
    ) => void;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { cursor?: string }) =>
        input.cursor === 'page-2'
          ? new Promise((resolve) => {
              resolveNextPage = resolve;
            })
          : Promise.resolve(
              createCatalogueResponse(
                [
                  createCatalogueRow({
                    workflowId: 'wf-first',
                    name: 'First page row',
                  }),
                ],
                'page-2',
              ),
            ),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('First page row')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));

    await waitFor(() =>
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(screen.getByText('Load more').closest('button')).toHaveClass(
        'ant-btn-loading',
      ),
    );
    expect(screen.getByText('First page row')).toBeInTheDocument();
    expect(screen.queryByText('Loading workflows')).not.toBeInTheDocument();

    act(() =>
      resolveNextPage(
        createCatalogueResponse([
          createCatalogueRow({
            workflowId: 'wf-second',
            name: 'Second page row',
          }),
        ]),
      ),
    );
    expect(await screen.findByText('Second page row')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
      expect.objectContaining({ cursor: 'page-2', take: 50 }),
      expect.any(AbortSignal),
    );
    const workflowNames = screen
      .getAllByRole('row')
      .slice(1)
      .map((row) => within(row).getAllByRole('cell')[0]?.textContent);
    expect(workflowNames).toEqual(['First page row', 'Second page row']);
  });

  it('keeps loaded rows visible and retries after a next-page failure', async () => {
    let nextPageAttempts = 0;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { cursor?: string }) => {
        if (input.cursor !== 'page-2') {
          return Promise.resolve(
            createCatalogueResponse(
              [
                createCatalogueRow({
                  workflowId: 'wf-first',
                  name: 'First page row',
                }),
              ],
              'page-2',
            ),
          );
        }
        nextPageAttempts += 1;
        return nextPageAttempts === 1
          ? Promise.reject(new Error('page unavailable'))
          : Promise.resolve(
              createCatalogueResponse([
                createCatalogueRow({
                  workflowId: 'wf-second',
                  name: 'Second page row',
                }),
              ]),
            );
      },
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('First page row')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(
      await screen.findByText("More workflows couldn't be loaded"),
    ).toBeInTheDocument();
    expect(screen.getByText('First page row')).toBeInTheDocument();
    expect(screen.queryByText('Workflows unavailable')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('Second page row')).toBeInTheDocument();
    expect(nextPageAttempts).toBe(2);
  });

  it('uses backend capabilities and the workflow id for row actions', async () => {
    const identities = {
      memberId: 'm-alpha',
      workflowId: 'wf-alpha',
      publishedServiceId: 'svc-alpha',
    };
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-unavailable',
          name: 'Unavailable workflow',
          capabilities: {
            open: {
              available: false,
              unavailableReason: 'draft_source_missing',
            },
            activity: {
              available: false,
              unavailableReason: 'committed_source_missing',
            },
            rename: {
              available: false,
              unavailableReason: 'draft_source_missing',
            },
            delete: {
              available: false,
              unavailableReason: 'draft_source_missing',
            },
          },
        }),
        createCatalogueRow({
          workflowId: identities.workflowId,
          name: 'Invoice review',
          committed: {
            serviceKey: 'opaque-service-key',
            workflowName: 'invoice_review',
            actorId: identities.memberId,
            activeRevisionId: 'rev-alpha',
            deploymentId: identities.publishedServiceId,
            deploymentStatus: 'Deactivated',
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const unavailableRow = (
      await screen.findByText('Unavailable workflow')
    ).closest('tr');
    expect(unavailableRow).not.toBeNull();
    expect(
      within(unavailableRow as HTMLElement).getByRole('button', {
        name: 'Open Unavailable workflow in Workspace',
      }),
    ).toBeDisabled();
    expect(
      within(unavailableRow as HTMLElement).getByRole('button', {
        name: 'View activity for Unavailable workflow in Workspace',
      }),
    ).toBeDisabled();
    fireEvent.click(
      within(unavailableRow as HTMLElement).getByRole('button', {
        name: 'More actions for Unavailable workflow in Workspace',
      }),
    );
    expect(
      await screen.findByRole('menuitem', {
        name: 'Copy workflow reference',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Rename' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: 'Delete draft' })).toBeNull();

    const actionableRow = screen.getByText('Invoice review').closest('tr');
    expect(
      within(actionableRow as HTMLElement).getByRole('link', {
        name: 'Open Invoice review in Workspace',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-alpha',
    );
    expect(
      within(actionableRow as HTMLElement).getByRole('link', {
        name: 'View activity for Invoice review in Workspace',
      }),
    ).toHaveAttribute(
      'href',
      '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha',
    );
    expect(document.body.textContent).not.toContain(identities.memberId);
    expect(document.body.textContent).not.toContain(
      identities.publishedServiceId,
    );
  });

  it('shows Delete draft without Archive for a draft-only workflow', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({ workflowId: 'wf-draft', name: 'Draft workflow' }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Draft workflow')).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Draft workflow in Workspace',
      }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Archive' })).toBeNull();
  });

  it('shows Delete draft without Archive when committed history has no active revision', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-draft-with-history',
          name: 'Draft workflow with history',
          committed: {
            serviceKey: 'opaque-service-key',
            workflowName: 'draft_with_history',
            actorId: 'm-alpha',
            activeRevisionId: '',
            deploymentId: '',
            deploymentStatus: '',
          },
          hasDraftSource: true,
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (
      await screen.findByText('Draft workflow with history')
    ).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Draft workflow with history in Workspace',
      }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Archive' })).toBeNull();
  });

  it('shows Archive without Delete draft for a published workflow that still has a draft', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-published-draft',
          name: 'Published draft workflow',
          committed: {
            serviceKey: 'opaque-service-key',
            workflowName: 'published_draft',
            actorId: 'm-alpha',
            activeRevisionId: 'rev-alpha',
            deploymentId: 'dep-alpha',
            deploymentStatus: 'Active',
          },
          hasDraftSource: true,
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Published draft workflow')).closest(
      'tr',
    );
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Published draft workflow in Workspace',
      }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Archive' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Delete draft' })).toBeNull();
  });

  it('shows Archive without Delete draft for a published-only workflow', async () => {
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([
        createCatalogueRow({
          workflowId: 'wf-published',
          name: 'Published workflow',
          committed: {
            serviceKey: 'opaque-service-key',
            workflowName: 'published',
            actorId: 'm-alpha',
            activeRevisionId: 'rev-alpha',
            deploymentId: 'dep-alpha',
            deploymentStatus: 'Active',
          },
          hasDraftSource: false,
          capabilities: {
            open: { available: true, unavailableReason: null },
            activity: { available: true, unavailableReason: null },
            rename: {
              available: false,
              unavailableReason: 'draft_source_missing',
            },
            delete: {
              available: false,
              unavailableReason: 'draft_source_missing',
            },
          },
        }),
      ]),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Published workflow')).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Published workflow in Workspace',
      }),
    );
    expect(
      await screen.findByRole('menuitem', { name: 'Archive' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Delete draft' })).toBeNull();
  });

  it('refreshes the catalogue after a successful rename', async () => {
    const originalYaml = 'name: support_triage\nroles: []\nsteps: []\n';
    const renamedYaml = 'name: APAC support triage\nroles: []\nsteps: []\n';
    let renamed = false;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(() =>
      Promise.resolve(
        createCatalogueResponse([
          createCatalogueRow({
            workflowId: 'wf-support',
            name: renamed ? 'APAC support triage' : 'Support triage',
          }),
        ]),
      ),
    );
    mockStudioApi.getWorkflowDraft.mockImplementation(async () => ({
      workflowId: 'wf-support',
      name: renamed ? 'APAC support triage' : 'Support triage',
      fileName: 'support.yaml',
      filePath: '/support.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: renamed ? renamedYaml : originalYaml,
      layout: { nodes: [] },
      updatedAtUtc: '2026-08-05T11:30:00Z',
    }));
    mockStudioApi.parseYaml.mockResolvedValue({
      document: { name: 'support_triage', roles: [], steps: [] },
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      document: { name: 'APAC support triage', roles: [], steps: [] },
      findings: [],
      yaml: renamedYaml,
    });
    mockStudioApi.updateWorkflowDraft.mockImplementation(async () => {
      renamed = true;
      return {};
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Support triage')).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in Workspace',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Workflow name' }), {
      target: { value: 'APAC support triage' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save name' }));

    expect(await screen.findByText('APAC support triage')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
    expect(mockConsoleToast.success).toHaveBeenCalledWith('Workflow renamed');
  });

  it('refreshes the catalogue after a successful delete', async () => {
    let deleted = false;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(() =>
      Promise.resolve(
        createCatalogueResponse(
          deleted
            ? []
            : [
                createCatalogueRow({
                  workflowId: 'wf-support',
                  name: 'Support triage',
                }),
              ],
        ),
      ),
    );
    mockStudioApi.deleteWorkflowDraft.mockImplementation(async () => {
      deleted = true;
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Support triage')).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Support triage in Workspace',
      }),
    );
    fireEvent.click(
      await screen.findByRole('menuitem', { name: 'Delete draft' }),
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete draft' }));

    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
    expect(mockStudioApi.deleteWorkflowDraft).toHaveBeenCalledWith(
      'wf-support',
      'scope-alpha',
    );
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(3);
  });

  it('archives by workflow identity and observes the exact row across catalogue pages', async () => {
    const activeCommitted = {
      serviceKey: 'svc-alpha',
      workflowName: 'workflow_alpha',
      actorId: 'actor-alpha',
      activeRevisionId: 'rev-alpha',
      deploymentId: 'dep-alpha',
      deploymentStatus: 'Active',
    };
    const archivedCommitted = {
      ...activeCommitted,
      deploymentStatus: 'Deactivated',
    };
    let archived = false;
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { cursor?: string; query?: string; view?: string }) => {
        if (
          input.view === 'archived' &&
          input.query === 'wf-alpha' &&
          input.cursor !== 'archive-page-2'
        ) {
          return Promise.resolve(
            createCatalogueResponse(
              [
                createCatalogueRow({
                  workflowId: 'wf-alpha-related',
                  name: 'Prefix match',
                  committed: archivedCommitted,
                }),
              ],
              'archive-page-2',
            ),
          );
        }
        return Promise.resolve(
          createCatalogueResponse([
            createCatalogueRow({
              workflowId: 'wf-alpha',
              name: 'Workflow Alpha',
              committed: archived ? archivedCommitted : activeCommitted,
            }),
          ]),
        );
      },
    );
    mockObserveWorkflowArchival.mockImplementation(
      async (input: { readWorkflows: () => Promise<unknown[]> }) => {
        archived = true;
        return {
          kind: 'observed',
          workflows: await input.readWorkflows(),
        };
      },
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const row = (await screen.findByText('Workflow Alpha')).closest('tr');
    fireEvent.click(
      within(row as HTMLElement).getByRole('button', {
        name: 'More actions for Workflow Alpha in Workspace',
      }),
    );
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Archive' }));
    fireEvent.click(screen.getByRole('button', { name: 'Archive workflow' }));

    await waitFor(() =>
      expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith({
        scopeId: 'scope-alpha',
        view: 'archived',
        query: 'wf-alpha',
        cursor: undefined,
        take: 100,
      }),
    );
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      view: 'archived',
      query: 'wf-alpha',
      cursor: 'archive-page-2',
      take: 100,
    });
    expect(mockScopesApi.listWorkflows).not.toHaveBeenCalled();
    expect(mockScopesApi.archiveWorkflow).toHaveBeenCalledWith(
      'scope-alpha',
      'wf-alpha',
    );
    expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
    expect(mockServicesApi.deactivateDeployment).not.toHaveBeenCalled();
    expect(await screen.findByText('Archived')).toBeInTheDocument();
    expect(mockConsoleToast.success).toHaveBeenCalledWith('Workflow archived');
  });

  it('renders one catalogue failure state and retries the same query', async () => {
    mockScopesApi.queryWorkflowCatalogue
      .mockRejectedValueOnce(new Error('offline'))
      .mockResolvedValueOnce(createCatalogueResponse([]));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText('Workflows unavailable'),
    ).toBeInTheDocument();
    expect(mockConsoleToast.error).toHaveBeenCalledWith(
      'Workflows unavailable',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Retry workflows' }));
    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
  });

  it('renders the backend empty result without consulting legacy sources', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByText('No workflows yet')).toBeInTheDocument();
    expect(mockStudioApi.listWorkflowDrafts).not.toHaveBeenCalled();
    expect(mockScopesApi.listWorkflows).not.toHaveBeenCalled();
  });

  it('keeps language and account actions available inside the mobile navigation drawer', async () => {
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
});
describe('Workflow Activity vNext settings', () => {
  beforeEach(() => {
    mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/settings';
    jest.clearAllMocks();
    clearStoredAuthSession();
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

  afterEach(() => {
    clearStoredAuthSession();
    cleanupTestQueryClients();
  });

  it('uses the matching NyxID profile when the account endpoint omits header fields', async () => {
    persistAuthSession({
      tokens: {
        accessToken: 'token',
        expiresAt: Date.now() + 60_000,
        expiresIn: 60,
        tokenType: 'Bearer',
      },
      user: {
        email: 'abigail@example.test',
        name: 'Abigail Deng',
        picture: 'https://example.test/abigail.png',
        sub: 'user-abigail',
      },
    });
    mockStudioApi.getAuthSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      providerDisplayName: 'NyxID',
      subject: 'user-abigail',
      profile: null,
      session: {
        authenticated: true,
        scopeId: 'scope-alpha',
        scopeSource: 'nyxid-session',
        expiresAtUtc: '2099-08-05T10:00:00Z',
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(await screen.findByTitle('Abigail Deng')).toBeInTheDocument();
    expect(screen.getByTitle('Abigail Deng')).toHaveAttribute(
      'data-picture',
      'https://example.test/abigail.png',
    );
  });

  it('renders authoritative identity and the effective workflow execution target', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByRole('combobox', { name: 'Preferred service' });
    expect(
      screen
        .getByRole('link', { name: 'Settings' })
        .querySelector('[data-icon-mock="SettingOutlined"]'),
    ).not.toBeNull();
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
    expect(screen.getByText('Verified')).toBeInTheDocument();
    expect(screen.getByText('user-subject-alpha')).toBeVisible();
    expect(screen.getByText('operator')).toBeVisible();
    expect(screen.getByText('platform')).toBeVisible();
    expect(screen.queryByText('Support details')).not.toBeInTheDocument();
    expect(screen.queryByText('Product access')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Refresh status' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByText('nyxid-session')).not.toBeInTheDocument();

    const advancedLink = screen.getByRole('link', { name: 'Advanced' });
    fireEvent.click(advancedLink);
    expect(advancedLink).toHaveAttribute('aria-current', 'page');
    expect(screen.getByText('Workflow execution')).toBeInTheDocument();
    expect(screen.getByText('Execution target')).toBeInTheDocument();
    expect(screen.getByText('Remote runtime')).toBeInTheDocument();
    expect(screen.getByText('Runtime URL')).toBeInTheDocument();
    expect(
      await screen.findAllByText('https://runtime.example.test'),
    ).toHaveLength(1);
    expect(screen.queryByText('Local connection URL')).not.toBeInTheDocument();
    expect(screen.queryByText('Remote connection URL')).not.toBeInTheDocument();
    expect(screen.queryByText('Technical details')).not.toBeInTheDocument();
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

    expect(
      await screen.findByRole('heading', { name: 'Ada Operator' }),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Profile details are unavailable.'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Not provided')).not.toBeInTheDocument();
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

  it('only offers backend services that publish at least one model', async () => {
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
    fireEvent.mouseDown(routeSelect);
    expect(await screen.findByText('Service alpha')).toBeInTheDocument();
    expect(screen.queryByText('System default')).not.toBeInTheDocument();
    expect(screen.queryByText('Gateway')).not.toBeInTheDocument();
    expect(screen.queryByText('Storage alpha')).not.toBeInTheDocument();
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
    expect(screen.queryByText('Saving changes…')).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();
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
  const unavailableWorkflowDetail = {
    available: false,
    scopeId: 'scope-alpha',
    workflow: null,
    source: null,
  } as const;

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
    mockScopesApi.getWorkflowDetail.mockResolvedValue(
      unavailableWorkflowDetail,
    );
  });

  afterEach(() => cleanupTestQueryClients());

  it('keeps the editor header focused on one inline workflow name', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByDisplayValue('Committed source');
    const workflowNameEditors = screen.getAllByRole('textbox', {
      name: 'Workflow name',
    });

    expect(workflowNameEditors).toHaveLength(1);
    expect(workflowNameEditors[0].closest('h1')).not.toBeNull();
    expect(
      screen.queryByText('Build, test, and refine this workflow.'),
    ).not.toBeInTheDocument();
  });

  it('enables Save workflow only while the loaded workflow has unsaved changes', async () => {
    mockStudioApi.serializeYaml.mockImplementationOnce(
      async ({
        document: nextDocument,
      }: {
        document: SerializableWorkflowDocument;
      }) => ({
        yaml: `name: ${nextDocument.name}\nroles: []\nsteps: []\n`,
        document: nextDocument,
        findings: [],
      }),
    );
    mockStudioApi.saveWorkflow.mockImplementationOnce(
      async (input: SaveWorkflowRequestProbe) => ({
        kind: 'materialized',
        workflow: {
          workflowId: 'wf-draft-new',
          name: input.workflowName,
          fileName: 'committed-source.yaml',
          filePath: '/workflows/committed-source.yaml',
          directoryId: 'directory-alpha',
          directoryLabel: 'Workflows',
          yaml: input.yaml,
          updatedAtUtc: '2026-08-04T10:01:00Z',
          document: { name: input.workflowName, roles: [], steps: [] },
          draftExists: true,
          findings: [],
        },
      }),
    );
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const workflowName = await screen.findByRole('textbox', {
      name: 'Workflow name',
    });
    const saveWorkflowButton = screen.getByRole('button', {
      name: 'Save workflow',
    });
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Saved at 2026-08-04 10:00:00 UTC');
    expect(saveWorkflowButton).toBeDisabled();

    fireEvent.change(workflowName, {
      target: { value: 'Committed source updated' },
    });
    expect(saveWorkflowButton).toBeEnabled();
    fireEvent.click(saveWorkflowButton);

    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1),
    );
    expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
      document: expect.objectContaining({
        name: 'Committed source updated',
      }),
    });
    expect(mockStudioApi.saveWorkflow).toHaveBeenCalledWith(
      expect.objectContaining({
        workflowName: 'Committed source updated',
        yaml: 'name: Committed source updated\nroles: []\nsteps: []\n',
      }),
    );
    await waitFor(() => expect(saveWorkflowButton).toBeDisabled());
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Saved at 2026-08-04 10:01:00 UTC');
  });

  it('opens an instantiated template draft as already saved', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source';

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const saveWorkflowButton = await screen.findByRole('button', {
      name: 'Save workflow',
    });
    expect(saveWorkflowButton).toBeDisabled();
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Saved at 2026-08-04 10:00:00 UTC');
    expect(mockStudioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(history.replace).not.toHaveBeenCalled();
  });

  it('preserves template tool set scopes through serialize and save', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-committed-source';
    const parsedDocument = {
      name: 'committed_source',
      roles: [
        {
          id: 'studio',
          toolSets: ['studio.local', 'nyxid.connected_services'],
        },
      ],
      steps: [
        {
          id: 'reply',
          type: 'llm_call',
          targetRole: 'studio',
          toolSets: ['nyxid.connected_services'],
        },
      ],
    };
    mockStudioApi.parseYaml.mockResolvedValueOnce({
      document: parsedDocument,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockImplementationOnce(
      async ({ document }) => ({
        yaml: 'name: committed_source\nroles:\n  - id: studio\n    tool_sets: [studio.local, nyxid.connected_services]\nsteps:\n  - id: reply\n    type: llm_call\n    target_role: studio\n    tool_sets: [nyxid.connected_services]\n',
        document,
        findings: [],
      }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const workflowName = await screen.findByDisplayValue('Committed source');
    const saveWorkflowButton = screen.getByRole('button', {
      name: 'Save workflow',
    });
    fireEvent.change(workflowName, {
      target: { value: 'Committed source updated' },
    });
    await waitFor(() => expect(saveWorkflowButton).toBeEnabled());
    fireEvent.click(saveWorkflowButton);

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
        document: expect.objectContaining({
          roles: [
            expect.objectContaining({
              id: 'studio',
              toolSets: ['studio.local', 'nyxid.connected_services'],
            }),
          ],
          steps: [
            expect.objectContaining({
              id: 'reply',
              toolSets: ['nyxid.connected_services'],
            }),
          ],
        }),
      }),
    );
    await waitFor(() =>
      expect(mockStudioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          yaml: expect.stringContaining(
            'tool_sets: [nyxid.connected_services]',
          ),
        }),
      ),
    );
  });

  it('publishes a saved workflow in one click and waits for observed evidence before showing Published', async () => {
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
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.publishWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-draft-alpha',
      serviceKey: 'service-alpha',
      revisionId: 'rev-preview-alpha',
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
    const observedWorkflow = {
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: 'rev-preview-alpha',
        publishedServiceId: 'svc-alpha',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    };
    let resolveWorkflowObservation:
      | ((workflow: typeof observedWorkflow) => void)
      | undefined;
    mockScopesApi.getWorkflowDetail
      .mockResolvedValueOnce(unavailableWorkflowDetail)
      .mockImplementation(
        () =>
          new Promise((resolve) => {
            resolveWorkflowObservation = resolve;
          }),
      );
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    expect(
      screen.queryByRole('dialog', { name: 'Publish workflow' }),
    ).not.toBeInTheDocument();

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
    await waitFor(() =>
      expect(mockStudioApi.publishWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          revisionId: 'rev-preview-alpha',
          scopeId: 'scope-alpha',
          workflowId: 'wf-draft-alpha',
          workflowYaml:
            'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
        }),
      ),
    );
    expect(mockStudioApi.saveAndBindWorkflow).not.toHaveBeenCalled();
    expect(mockScopeRuntimeApi.listServices).not.toHaveBeenCalled();
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Publishing' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(screen.queryByText('Publication accepted')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('status', {
        name: 'Workflow publication status',
      }),
    ).not.toBeInTheDocument();

    resolveWorkflowObservation?.(observedWorkflow);

    const publicationStatus = await screen.findByRole('status', {
      name: 'Workflow publication status',
    });
    const saveStatus = screen.getByRole('status', {
      name: 'Workflow save status',
    });
    expect(publicationStatus).toHaveTextContent('Published');
    expect(publicationStatus.parentElement).toBe(saveStatus.parentElement);
    expect(
      publicationStatus.compareDocumentPosition(saveStatus) &
        Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    expect(
      screen.queryByRole('button', { name: 'Publish' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Published' }),
    ).not.toBeInTheDocument();
    await waitFor(() =>
      expect(mockConsoleToast.success).toHaveBeenCalledWith(
        'Workflow published',
        expect.objectContaining({
          key: expect.stringContaining('workflow-publication:'),
        }),
      ),
    );
    expect(mockConsoleToast.success).toHaveBeenCalledTimes(1);
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
  });

  it('publishes saved runtime YAML without reprocessing it through the Studio parser', async () => {
    arrangeObservedWorkflowPublication();
    const savedYaml =
      'name: studio\nroles:\n  - id: studio\n    tool_sets: [studio.local]\nsteps:\n  - id: reply\n    type: llm_call\n    role: studio\n    tool_sets: [studio.local]\n';
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      name: 'studio',
      fileName: 'studio.yaml',
      filePath: '/workflows/studio.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: savedYaml,
      updatedAtUtc: '2026-08-20T15:25:00Z',
      document: {
        name: 'studio',
        roles: [{ id: 'studio', toolSets: ['studio.local'] }],
        steps: [
          {
            id: 'reply',
            type: 'llm_call',
            targetRole: 'studio',
            toolSets: ['studio.local'],
          },
        ],
      },
      draftExists: true,
      findings: [
        {
          code: 'unknown_field',
          level: 2,
          message: "Unknown field 'tool_sets'.",
        },
      ],
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: null,
      findings: [
        {
          code: 'unknown_field',
          level: 'error',
          message: "Unknown field 'tool_sets'.",
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledWith(
        expect.objectContaining({ workflowYaml: savedYaml }),
      ),
    );
    await waitFor(() =>
      expect(mockStudioApi.publishWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({ workflowYaml: savedYaml }),
      ),
    );
    expect(mockStudioApi.parseYaml).not.toHaveBeenCalled();
    expect(mockStudioApi.serializeYaml).not.toHaveBeenCalled();
    expect(
      await screen.findByRole('status', {
        name: 'Workflow publication status',
      }),
    ).toHaveTextContent('Published');
    expect(screen.getByRole('button', { name: 'Run' })).toBeEnabled();
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
  ])('keeps a returned $mismatch mismatch visible without starting observation', async ({
    returnedRevisionId,
    returnedWorkflowId,
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
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.publishWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: returnedWorkflowId,
      revisionId: returnedRevisionId,
      serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(
      await screen.findByText("Workflow couldn't be submitted"),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1);
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(1);
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
      'scope-alpha',
      'wf-draft-alpha',
    );
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
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.publishWorkflow.mockResolvedValue({
      scopeId: 'scope-alpha',
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
      acceptanceStage: 'accepted',
      propagationStage: 'readmodel_propagating',
    });
  }

  function arrangeObservedWorkflowPublication(): void {
    arrangeSavedDraftPublication();
    const observedWorkflow = {
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: 'rev-preview-alpha',
        publishedServiceId: 'svc-alpha',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    };
    mockScopesApi.getWorkflowDetail
      .mockResolvedValueOnce(unavailableWorkflowDetail)
      .mockResolvedValue(observedWorkflow);
  }

  async function publishObservedWorkflow(): Promise<void> {
    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    await screen.findByRole('status', {
      name: 'Workflow publication status',
    });
  }

  async function renderPublishedWorkflowPage(): Promise<void> {
    arrangeObservedWorkflowPublication();
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();
  }

  it('shows publish blockers in the standard compact tooltip', async () => {
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
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip.closest('.ant-tooltip')).not.toBeNull();
    expect(tooltip.closest('.ant-popover')).toBeNull();
    expect(within(tooltip).getAllByRole('listitem')).toHaveLength(3);
    expect(within(tooltip).queryByRole('region')).not.toBeInTheDocument();
    expect(within(tooltip).queryByRole('button')).not.toBeInTheDocument();
    expect(
      within(tooltip).getByText('Workflow name is required.'),
    ).toBeInTheDocument();
    expect(
      within(tooltip).getByText('Step instruction is required.'),
    ).toBeInTheDocument();
    expect(mockStudioApi.previewExplicitRequests).not.toHaveBeenCalled();
  });

  it('continues publication observation after a transient read failure without another action', async () => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail
      .mockResolvedValueOnce(unavailableWorkflowDetail)
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
          activeRevisionId: 'rev-preview-alpha',
          publishedServiceId: 'svc-alpha',
          deploymentId: 'deployment-workflow-alpha',
          deploymentStatus: 'Available',
          updatedAt: '2026-08-06T10:00:00Z',
        },
        source: null,
      });
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(
      await screen.findByRole('status', {
        name: 'Workflow publication status',
      }),
    ).toHaveTextContent('Published');
    expect(
      screen.queryByText("Publication couldn't be confirmed"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Check again' }),
    ).not.toBeInTheDocument();
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(3);
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
      'scope-alpha',
      'wf-draft-alpha',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1);
    expect(mockConsoleToast.error).not.toHaveBeenCalled();
    expect(mockConsoleToast.success).toHaveBeenCalledTimes(1);
  });

  it.each([
    [401, 'Sign in to continue'],
    [403, "You don't have access to this workspace"],
  ])('keeps an accepted publication receipt mutation-locked after a %i observation without a manual check action', async (status, message) => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockRejectedValue(
      Object.assign(new Error(`HTTP ${status}`), { status }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(await screen.findByText(message)).toBeInTheDocument();
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        message,
        expect.objectContaining({
          key: expect.stringContaining('workflow-publication:'),
        }),
      ),
    );
    expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
    expect(
      screen.getByRole('button', { name: 'Publish blocked · 1 issue' }),
    ).toHaveAttribute('aria-disabled', 'true');
    expect(
      screen.queryByRole('button', { name: 'Check again' }),
    ).not.toBeInTheDocument();
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(2);
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
      'scope-alpha',
      'wf-draft-alpha',
    );
    expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1);
  });

  it('reports one error toast when publication observation reaches a terminal failure', async () => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockRejectedValue(
      Object.assign(new Error('HTTP 400'), { status: 400 }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(
      await screen.findByText("Publication couldn't be confirmed"),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Publication couldn't be confirmed",
        expect.objectContaining({
          key: expect.stringContaining('workflow-publication:'),
        }),
      ),
    );
    expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
  });

  it('presents a publication validation rejection as workflow configuration to fix', async () => {
    arrangeSavedDraftPublication();
    const validationMessage =
      "Step 'draft_weekly_report' can invoke llm_call and must declare an explicit allowed_tools scope on the step or its target role.";
    mockStudioApi.previewExplicitRequests.mockRejectedValue(
      Object.assign(new Error(validationMessage), {
        code: 'INVALID_USER_WORKFLOW_REQUEST',
        status: 400,
      }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(
      await screen.findByText("Workflow isn't ready to publish"),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        'Fix the workflow configuration below, then publish again.',
      ),
    ).toBeInTheDocument();
    const technicalDetails = screen.getByText('Technical details');
    expect(screen.getByText(validationMessage)).not.toBeVisible();
    fireEvent.click(technicalDetails);
    expect(screen.getByText(validationMessage)).toBeVisible();
    expect(
      screen.queryByText("Publication couldn't be confirmed"),
    ).not.toBeInTheDocument();
    expect(mockStudioApi.publishWorkflow).not.toHaveBeenCalled();
    expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(1);
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow isn't ready to publish",
        expect.objectContaining({
          key: expect.stringContaining('workflow-publication:'),
        }),
      ),
    );
  });

  it('creates a fresh revision before retrying a publication that was not accepted', async () => {
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
    mockStudioApi.publishWorkflow
      .mockRejectedValueOnce(
        Object.assign(new Error('HTTP 503'), { status: 503 }),
      )
      .mockResolvedValueOnce({
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        revisionId: freshRevisionId,
        serviceKey: 'tenant-alpha/app-alpha/scope-alpha/svc-alpha',
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      });
    const observedWorkflow = {
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        displayName: 'Workflow alpha',
        serviceKey: 'workflow-alpha',
        workflowName: 'Workflow alpha',
        actorId: 'actor-workflow-alpha',
        activeRevisionId: freshRevisionId,
        publishedServiceId: 'svc-alpha',
        deploymentId: 'deployment-workflow-alpha',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-06T10:00:00Z',
      },
      source: null,
    };
    mockScopesApi.getWorkflowDetail
      .mockResolvedValueOnce(unavailableWorkflowDetail)
      .mockResolvedValue(observedWorkflow);
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    expect(
      await screen.findByText("Workflow couldn't be submitted"),
    ).toBeInTheDocument();
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow couldn't be submitted",
        expect.objectContaining({
          key: expect.stringContaining('workflow-publication:'),
        }),
      ),
    );
    expect(mockConsoleToast.error).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(mockStudioApi.publishWorkflow).toHaveBeenLastCalledWith(
        expect.objectContaining({ revisionId: freshRevisionId }),
      ),
    );
    expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(2);
    expect(
      await screen.findByRole('status', {
        name: 'Workflow publication status',
      }),
    ).toHaveTextContent('Published');
    expect(mockConsoleToast.success).toHaveBeenCalledTimes(1);
  });

  it('continues delayed publication observation automatically without sending a second PUT', async () => {
    arrangeSavedDraftPublication();
    mockScopesApi.getWorkflowDetail.mockRejectedValue(
      Object.assign(new Error('HTTP 404'), { status: 404 }),
    );
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const publish = await screen.findByRole('button', { name: 'Publish' });

    jest.useFakeTimers();
    try {
      fireEvent.click(publish);
      await act(async () => {
        await jest.advanceTimersByTimeAsync(0);
      });
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenCalledTimes(2);
      expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
      await act(async () => {
        await jest.advanceTimersByTimeAsync(5_000);
      });

      expect(
        screen.getByRole('button', { name: 'Publishing' }),
      ).toHaveAttribute('aria-disabled', 'true');
      expect(
        screen.queryByText('Publication is taking longer to appear'),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByRole('button', { name: 'Check again' }),
      ).not.toBeInTheDocument();
      await act(async () => {
        await jest.advanceTimersByTimeAsync(2_000);
      });

      expect(mockScopesApi.getWorkflowDetail.mock.calls.length).toBeGreaterThan(
        5,
      );
      expect(mockScopesApi.getWorkflowDetail).toHaveBeenLastCalledWith(
        'scope-alpha',
        'wf-draft-alpha',
      );
      expect(mockScopeRuntimeApi.getServiceRevisions).not.toHaveBeenCalled();
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
      expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1);
    } finally {
      jest.useRealTimers();
    }
  });

  it('submits only one publication from rapid repeated clicks', async () => {
    arrangeSavedDraftPublication();
    let resolvePreview: (preview: unknown) => void = () => undefined;
    mockStudioApi.previewExplicitRequests.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolvePreview = resolve;
        }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const publish = await screen.findByRole('button', { name: 'Publish' });
    await act(async () => {
      publish.click();
      publish.click();
      await Promise.resolve();
    });

    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);

    await act(async () => {
      resolvePreview({
        workflowId: 'wf-draft-alpha',
        revisionId: 'rev-preview-alpha',
        items: [],
      });
      await Promise.resolve();
    });

    await waitFor(() =>
      expect(mockStudioApi.publishWorkflow).toHaveBeenCalledTimes(1),
    );
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
    expect(document.querySelector('.ant-alert-info')).toBeNull();

    await act(async () => {
      resolveSave?.();
      await Promise.resolve();
    });
    expect(
      await screen.findByText('Saved at 2026-08-04 10:05:00 UTC'),
    ).toBeInTheDocument();
    expect(mockConsoleToast.success).not.toHaveBeenCalledWith('Workflow saved');
    expect(
      screen.queryByRole('status', {
        name: 'Workflow publication status',
      }),
    ).not.toBeInTheDocument();
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
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      { workflowId: 'wf-draft-api' },
    ]);

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

  it('renders workflow validation alerts without deprecated Ant Design props', async () => {
    const consoleError = jest
      .spyOn(console, 'error')
      .mockImplementation(() => undefined);
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-validation-alert',
      name: 'Validation alert workflow',
      fileName: 'validation-alert.yaml',
      filePath: '/workflows/validation-alert.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: validation_alert\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: { name: 'validation_alert', roles: [], steps: [] },
      draftExists: true,
      findings: [
        {
          code: 'WORKFLOW_UNKNOWN_STEP',
          level: 'warning',
          message: 'A workflow step needs review.',
        },
      ],
    });

    try {
      renderWithQueryClient(<WorkflowActivityVNextPage />);

      expect(
        await screen.findByText('A workflow step needs review.'),
      ).toBeInTheDocument();
      expect(consoleError).not.toHaveBeenCalledWith(
        expect.stringContaining(
          '[antd: Alert] `message` is deprecated. Please use `title` instead.',
        ),
      );
    } finally {
      consoleError.mockRestore();
    }
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

  it('reuses the complete Studio canvas editing contract', async () => {
    const sourceDocument = {
      name: 'committed_source',
      roles: [],
      steps: [
        { id: 'step-root', type: 'conditional' },
        { id: 'step-next', type: 'transform' },
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
      document: sourceDocument,
      draftExists: false,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockImplementation(async ({ document }) => ({
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      document,
      findings: [],
    }));

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const canvas = await screen.findByTestId('workflow-studio-canvas');
    expect(canvas).toHaveAttribute('data-connectable', 'true');
    expect(canvas).toHaveAttribute('data-deletable', 'true');
    expect(canvas).toHaveAttribute('data-edge-selectable', 'true');
    expect(canvas).toHaveAttribute('data-layout-editable', 'true');

    fireEvent.click(
      within(canvas).getByRole('button', { name: 'Connect first two nodes' }),
    );

    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledWith({
        document: expect.objectContaining({
          steps: expect.arrayContaining([
            expect.objectContaining({
              id: 'step-root',
              branches: { true: 'step-next' },
              next: null,
            }),
          ]),
        }),
      }),
    );
  });

  it('keeps canvas editing callbacks stable when the node library opens', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByTestId('workflow-studio-canvas');
    const initialProps = mockWorkflowStudioCanvasProps.mock.calls.at(-1)?.[0];

    fireEvent.click(screen.getByRole('button', { name: 'Add node' }));

    await waitFor(() =>
      expect(mockWorkflowStudioCanvasProps).toHaveBeenCalledTimes(2),
    );
    const nodeLibraryProps =
      mockWorkflowStudioCanvasProps.mock.calls.at(-1)?.[0];

    expect(nodeLibraryProps?.onConnectNodes).toBe(initialProps?.onConnectNodes);
    expect(nodeLibraryProps?.onAddFirstStep).toBe(initialProps?.onAddFirstStep);
    expect(nodeLibraryProps?.onCanvasSelect).toBe(initialProps?.onCanvasSelect);
    expect(nodeLibraryProps?.onDeleteEdges).toBe(initialProps?.onDeleteEdges);
    expect(nodeLibraryProps?.onDeleteNodes).toBe(initialProps?.onDeleteNodes);
    expect(nodeLibraryProps?.onEdgeSelect).toBe(initialProps?.onEdgeSelect);
    expect(nodeLibraryProps?.onNodeLayoutChange).toBe(
      initialProps?.onNodeLayoutChange,
    );
    expect(nodeLibraryProps?.onNodeSelect).toBe(initialProps?.onNodeSelect);
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
      screen.queryByRole('complementary', { name: 'Published run panel' }),
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
      screen.queryByRole('complementary', { name: 'Published run panel' }),
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

  it('reports a parse validation save failure instead of failing silently', async () => {
    mockStudioApi.parseYaml.mockResolvedValueOnce({
      document: {
        name: 'committed_source',
        roles: [],
        steps: [],
      },
      findings: [{ level: 'error', message: 'Workflow steps are invalid.' }],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByDisplayValue('Committed source');
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow couldn't be saved",
      ),
    );
    expect(mockStudioApi.serializeYaml).not.toHaveBeenCalled();
    expect(mockStudioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Save failed');
  });

  it('reports a serialization validation save failure instead of failing silently', async () => {
    mockStudioApi.serializeYaml.mockResolvedValueOnce({
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [],
      },
      findings: [{ level: 'error', message: 'Workflow steps are invalid.' }],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByDisplayValue('Committed source');
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow couldn't be saved",
      ),
    );
    expect(mockStudioApi.saveWorkflow).not.toHaveBeenCalled();
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
    expect(within(inspector).getByText('Advanced JSON')).toBeVisible();
    fireEvent.change(within(inspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });
    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply step' }),
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
    fireEvent.click(within(inspector).getByText('Advanced JSON'));
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
    expect(within(inspector).getByText('Error details')).toBeVisible();
    expect(within(inspector).getByText(parserError)).not.toBeVisible();

    fireEvent.click(within(inspector).getByText('Error details'));

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
      within(inspector).getByText(
        'Apply this step before saving the workflow.',
      ),
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

  it('keeps an accepted save quiet while waiting for a readable draft', async () => {
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
    mockStudioApi.getWorkflowDraftFile.mockReturnValue(new Promise(() => {}));
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      { workflowId: 'wf-draft-api' },
    ]);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByDisplayValue('Committed source');
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Updated source' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save workflow' }));

    await waitFor(() =>
      expect(mockStudioApi.getWorkflowDraftFile).toHaveBeenCalled(),
    );
    expect(
      screen.getByRole('status', { name: 'Workflow save status' }),
    ).toHaveTextContent('Saving workflow…');
    expect(document.querySelector('.ant-alert-info')).toBeNull();
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
    expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual(
      [
        expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
        expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
        expect.objectContaining({ id: 'assign_step', next: null }),
      ],
    );
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
    expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual(
      [
        expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
        expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
        expect.objectContaining({ id: 'assign_step', next: 'publish_step' }),
        expect.objectContaining({ id: 'publish_step', next: null }),
      ],
    );
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

    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Committed source updated' },
    });
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
    expect(
      screen.queryByText(serializeFailure.message),
    ).not.toBeInTheDocument();
    const [content] = mockConsoleToast.error.mock.calls[0];
    const toastContent = render(content).container;
    expect(within(toastContent).getByText("Couldn't add node")).toBeVisible();
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
      within(inspector).getByRole('button', { name: 'Apply step' }),
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

  it('restores an authoritative publication when opening a published workflow', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '/workflows/committed-source.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
      updatedAtUtc: '2026-08-10T03:20:32Z',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [{ id: 'step-root', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    });
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-committed-source',
        displayName: 'Committed source',
        serviceKey: 'opaque-service-key',
        workflowName: 'committed_source',
        actorId: 'actor-existing',
        activeRevisionId: 'rev-existing',
        deploymentId: 'deployment-existing',
        deploymentStatus: 'Available',
        updatedAt: '2026-08-10T03:20:32Z',
        publishedServiceId: 'svc-existing',
        serviceAppId: 'workflow-app',
        serviceNamespace: 'workflow-namespace',
      },
      source: {
        workflowYaml:
          'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: llm_call\n',
        definitionActorId: 'definition-existing',
        inlineWorkflowYamls: null,
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const run = await screen.findByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    expect(run).not.toHaveAttribute('title');
    expect(
      screen.queryByRole('button', { name: 'Publish' }),
    ).not.toBeInTheDocument();
    fireEvent.click(run);

    const runPanel = await screen.findByRole('complementary', {
      name: 'Published run panel',
    });
    expect(within(runPanel).getByText('Published run')).toBeInTheDocument();
    expect(
      within(runPanel).getByRole('textbox', { name: 'Published run input' }),
    ).toBeInTheDocument();
    expect(
      within(runPanel).queryByText('svc-existing'),
    ).not.toBeInTheDocument();
    expect(
      within(runPanel).queryByText('rev-existing'),
    ).not.toBeInTheDocument();
    expect(mockStudioApi.publishWorkflow).not.toHaveBeenCalled();
  });

  it('opens the shared run input panel after publication is observed', async () => {
    arrangeObservedWorkflowPublication();
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    const run = screen.getByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    fireEvent.click(run);

    const runPanel = await screen.findByRole('complementary', {
      name: 'Published run panel',
    });
    expect(runPanel).toBeVisible();
    expect(
      within(runPanel).getByRole('button', { name: 'Start published run' }),
    ).toBeInTheDocument();
    expect(within(runPanel).queryByText('svc-alpha')).not.toBeInTheDocument();
    expect(
      within(runPanel).queryByText('rev-preview-alpha'),
    ).not.toBeInTheDocument();
  });

  it('bounds and resizes the published run panel beside the canvas', async () => {
    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));

    const runPanel = await screen.findByRole('complementary', {
      name: 'Published run panel',
    });
    const resizeHandle = screen.getByRole('separator', {
      name: 'Resize published run panel',
    });
    expect(runPanel).toHaveStyle({ height: '100%', width: '420px' });
    expect(resizeHandle).toHaveAttribute('aria-orientation', 'vertical');
    expect(resizeHandle).toHaveAttribute('aria-valuemin', '320');
    expect(resizeHandle).toHaveAttribute('aria-valuemax', '640');
    expect(resizeHandle).toHaveAttribute('aria-valuenow', '420');

    fireEvent.keyDown(resizeHandle, { key: 'ArrowLeft' });
    expect(runPanel).toHaveStyle({ width: '444px' });
    expect(resizeHandle).toHaveAttribute('aria-valuenow', '444');

    const workspace = document.querySelector('.wa-vnext__run-workspace');
    expect(workspace).toBeInstanceOf(HTMLElement);
    Object.defineProperty(workspace, 'clientWidth', {
      configurable: true,
      value: 700,
    });
    fireEvent(window, new Event('resize'));

    await waitFor(() => {
      expect(runPanel).toHaveStyle({ width: '340px' });
      expect(resizeHandle).toHaveAttribute('aria-valuemax', '340');
      expect(resizeHandle).toHaveAttribute('aria-valuenow', '340');
    });
  });

  it('keeps Logs collapsed across later SSE frames and restores toggle focus', async () => {
    const encoder = new TextEncoder();
    let streamController:
      | ReadableStreamDefaultController<Uint8Array>
      | undefined;
    mockRuntimeRunsApi.streamChat.mockResolvedValue({
      body: new ReadableStream({
        start(controller) {
          streamController = controller;
        },
      }),
      ok: true,
    } as Response);
    await renderPublishedWorkflowPage();

    const initiallyCollapsedToggle = screen.getByRole('button', {
      name: 'Expand workflow logs',
    });
    expect(initiallyCollapsedToggle).toHaveAttribute('aria-expanded', 'false');
    expect(initiallyCollapsedToggle).toHaveAttribute(
      'aria-controls',
      'workflow-published-run-console',
    );
    expect(
      screen.queryByRole('complementary', { name: 'Workflow run console' }),
    ).not.toBeInTheDocument();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      { target: { value: 'Review order 42' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    await act(async () => {
      streamController?.enqueue(
        encoder.encode(
          'data: {"runStarted":{"runId":"run-resizable-alpha"}}\n\n',
        ),
      );
    });

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    const resizeHandle = screen.getByRole('separator', {
      name: 'Resize workflow run console',
    });
    expect(logs).toHaveStyle({ height: '310px' });
    expect(resizeHandle).toHaveAttribute('aria-orientation', 'horizontal');
    expect(resizeHandle).toHaveAttribute('aria-valuenow', '310');

    fireEvent.keyDown(resizeHandle, { key: 'ArrowUp' });
    expect(logs).toHaveStyle({ height: '334px' });
    expect(resizeHandle).toHaveAttribute('aria-valuenow', '334');

    const collapseToggle = within(logs).getByRole('button', {
      name: 'Collapse workflow logs',
    });
    expect(collapseToggle).toHaveAttribute('aria-expanded', 'true');
    expect(collapseToggle).toHaveAttribute(
      'aria-controls',
      'workflow-published-run-console',
    );
    collapseToggle.focus();
    fireEvent.click(collapseToggle);

    const expandToggle = screen.getByRole('button', {
      name: 'Expand workflow logs',
    });
    await waitFor(() => expect(expandToggle).toHaveFocus());
    expect(
      screen.queryByRole('complementary', { name: 'Workflow run console' }),
    ).not.toBeInTheDocument();

    await act(async () => {
      streamController?.enqueue(
        encoder.encode(
          'data: {"custom":{"name":"aevatar.step.request","payload":{"input":"Review order 42","stepId":"step-live","stepType":"llm_call"}}}\n\n',
        ),
      );
      streamController?.enqueue(
        encoder.encode(
          'data: {"runFinished":{"runId":"run-resizable-alpha"}}\n\n',
        ),
      );
    });
    expect(
      screen.queryByRole('complementary', { name: 'Workflow run console' }),
    ).not.toBeInTheDocument();

    fireEvent.click(expandToggle);
    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Collapse workflow logs' }),
      ).toHaveFocus(),
    );
    expect(
      await screen.findByTestId('workflow-execution-log-row-node-step-live'),
    ).toBeInTheDocument();

    await act(async () => {
      streamController?.close();
    });
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
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

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

  it('starts the exact published workflow service with empty input', async () => {
    arrangeObservedWorkflowPublication();
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    const run = screen.getByRole('button', { name: 'Run' });
    await waitFor(() => expect(run).toBeEnabled());
    fireEvent.click(run);

    const startRun = await screen.findByRole('button', {
      name: 'Start published run',
    });
    expect(startRun).toBeEnabled();
    fireEvent.click(startRun);

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-alpha',
        { prompt: '' },
        expect.any(AbortSignal),
        { serviceId: 'svc-alpha' },
      ),
    );
    expect(mockRuntimeRunsApi.streamEndpoint).not.toHaveBeenCalled();
    expect(mockRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it('uploads published run files through the exact published service', async () => {
    arrangeObservedWorkflowPublication();
    mockRuntimeRunsApi.streamEndpoint.mockResolvedValue(createSseResponse([]));
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    const runPanel = await screen.findByRole('complementary', {
      name: 'Published run panel',
    });
    expect(
      within(runPanel).getByTestId('workflow-run-file-drop-zone'),
    ).toBeInTheDocument();
    const image = new File(['image-bytes'], 'invoice.png', {
      type: 'image/png',
    });
    fireEvent.change(within(runPanel).getByTestId('workflow-run-file-input'), {
      target: { files: [image] },
    });
    expect(
      await within(runPanel).findByText('invoice.png'),
    ).toBeInTheDocument();

    const startRun = within(runPanel).getByRole('button', {
      name: 'Start published run',
    });
    expect(startRun).toBeEnabled();
    fireEvent.click(startRun);

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamEndpoint).toHaveBeenCalledWith(
        'scope-alpha',
        { endpointId: 'chat', files: [image], prompt: '' },
        expect.any(AbortSignal),
        { serviceId: 'svc-alpha' },
      ),
    );
    expect(mockRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it('keeps live completion facts ahead of a stale running Activity projection', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        runId: 'run-observed-alpha',
        stateVersion: 7,
        status: 'running',
      }),
    );
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([
        {
          runStarted: { runId: 'run-observed-alpha' },
          timestamp: 1786356000000,
        },
        {
          runFinished: {
            result: { output: 'Weekly report is ready.' },
            runId: 'run-observed-alpha',
          },
          timestamp: 1786356001000,
        },
      ]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      { target: { value: 'Review order 42' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    expect(within(logs).getByText('Logs')).toBeInTheDocument();
    await waitFor(() => {
      expect(within(logs).getByText('Run finished')).toBeInTheDocument();
      expect(within(logs).getByText('succeeded')).toBeInTheDocument();
    });
    fireEvent.click(within(logs).getByText('Run finished'));
    expect(
      await within(logs).findByText(/Weekly report is ready\./),
    ).toBeInTheDocument();
    expect(within(logs).queryByText('Pending')).not.toBeInTheDocument();
    expect(within(logs).queryByText('step-verify')).not.toBeInTheDocument();
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-observed-alpha',
    );
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
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    const startRun = await screen.findByRole('button', {
      name: 'Start published run',
    });

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

  it('clears the published run console without aborting an active run', async () => {
    let streamController:
      | ReadableStreamDefaultController<Uint8Array>
      | undefined;
    let runSignal: AbortSignal | undefined;
    mockRuntimeRunsApi.streamChat.mockImplementation(
      (_scopeId, _request, signal) => {
        runSignal = signal;
        return Promise.resolve({
          body: new ReadableStream({
            start(controller) {
              streamController = controller;
            },
          }),
          ok: true,
        } as Response);
      },
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      { target: { value: 'Review order 42' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    const clearLogs = within(logs).getByRole('button', {
      name: 'Clear logs',
    });
    expect(clearLogs).toBeEnabled();
    fireEvent.click(clearLogs);

    expect(
      screen.queryByRole('complementary', { name: 'Workflow run console' }),
    ).not.toBeInTheDocument();
    expect(runSignal?.aborted).toBe(false);
    expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(1);

    await act(async () => {
      streamController?.close();
    });
  });

  it('allows a published workflow to start empty and still submits typed input', async () => {
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const input = await screen.findByRole('textbox', {
      name: 'Published run input',
    });
    const startRun = screen.getByRole('button', {
      name: 'Start published run',
    });

    expect(startRun).toBeEnabled();
    fireEvent.click(startRun);

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        'scope-alpha',
        { prompt: '' },
        expect.any(AbortSignal),
        { serviceId: 'svc-alpha' },
      ),
    );

    fireEvent.change(input, { target: { value: 'Review order 42' } });
    expect(startRun).toBeEnabled();

    fireEvent.click(startRun);

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(2),
    );
    expect(mockRuntimeRunsApi.streamChat).toHaveBeenLastCalledWith(
      'scope-alpha',
      { prompt: 'Review order 42' },
      expect.any(AbortSignal),
      { serviceId: 'svc-alpha' },
    );
  });

  it('keeps empty files blocked before invoking a published workflow', async () => {
    mockRuntimeRunsApi.streamEndpoint.mockResolvedValue(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const emptyFile = new File([''], 'empty.txt', { type: 'text/plain' });
    fireEvent.change(await screen.findByTestId('workflow-run-file-input'), {
      target: { files: [emptyFile] },
    });
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    expect(
      await screen.findByText(
        'Remove empty file empty.txt before starting the published run.',
      ),
    ).toBeInTheDocument();
    expect(mockRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockRuntimeRunsApi.streamEndpoint).not.toHaveBeenCalled();
  });

  it('maps backend prompt validation to the run input without losing it', async () => {
    mockRuntimeRunsApi.streamChat.mockRejectedValue(
      Object.assign(new Error('The request could not be validated.'), {
        fieldErrors: { Prompt: ['Use at least three characters.'] },
      }),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const input = await screen.findByRole('textbox', {
      name: 'Published run input',
    });
    fireEvent.change(input, { target: { value: 'x' } });
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    expect(
      await screen.findByText('Use at least three characters.'),
    ).toBeInTheDocument();
    const currentInput = screen.getByRole('textbox', {
      name: 'Published run input',
    });
    expect(currentInput).toHaveValue('x');
    expect(currentInput).toHaveAttribute('aria-invalid', 'true');
  });

  it('keeps Logs visible while the run panel closes and refreshes Activity progress', async () => {
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
      .mockResolvedValueOnce(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    const input = await screen.findByRole('textbox', {
      name: 'Published run input',
    });
    fireEvent.change(input, { target: { value: 'Review order 42' } });
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    await waitFor(() =>
      expect(within(logs).getAllByText('step-verify').length).toBeGreaterThan(
        0,
      ),
    );

    fireEvent.click(
      screen.getByRole('button', { name: 'Close published run panel' }),
    );
    expect(
      screen.queryByRole('complementary', { name: 'Published run panel' }),
    ).not.toBeInTheDocument();
    expect(logs).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Run' }));
    expect(
      await screen.findByRole('textbox', { name: 'Published run input' }),
    ).toHaveValue('Review order 42');

    expect(
      await within(logs).findByText(
        'Order 42 is ready for approval.',
        {},
        {
          timeout: 2500,
        },
      ),
    ).toBeInTheDocument();
    fireEvent.click(within(logs).getByRole('button', { name: 'Clear logs' }));
    expect(
      screen.queryByRole('complementary', { name: 'Workflow run console' }),
    ).not.toBeInTheDocument();

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Published run input' }),
      { target: { value: 'A different input' } },
    );
    fireEvent.click(
      screen.getByRole('button', { name: 'Start published run' }),
    );
    expect(
      await screen.findByRole('complementary', {
        name: 'Workflow run console',
      }),
    ).toBeInTheDocument();

    await waitFor(() =>
      expect(mockRuntimeRunsApi.streamChat).toHaveBeenCalledTimes(2),
    );
    expect(mockRuntimeRunsApi.streamChat.mock.calls[1]).toEqual([
      'scope-alpha',
      { prompt: 'A different input' },
      expect.any(AbortSignal),
      { serviceId: 'svc-alpha' },
    ]);
  });

  it('releases the published run action when live updates end without a run id', async () => {
    mockRuntimeRunsApi.streamChat.mockResolvedValue(createSseResponse([]));

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    const startRun = await screen.findByRole('button', {
      name: 'Start published run',
    });
    await waitFor(() => expect(startRun).toBeEnabled());
    expect(
      screen.queryByText(
        'Live updates ended. Open Activity to check the latest status.',
      ),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();
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
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );
    await waitFor(() => expect(firstReadStarted).toBe(true));
    expect(screen.queryByText('Run accepted')).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();

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

  it('replaces live logs when terminal Activity is observed', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        runId: 'run-observed-alpha',
        stateVersion: 7,
        status: 'completed',
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
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    await waitFor(() =>
      expect(within(logs).getAllByText('step-verify').length).toBeGreaterThan(
        0,
      ),
    );
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-observed-alpha',
    );
  });

  it('keeps activity observation quiet while polling for the run', async () => {
    mockWorkflowActivityApi.getRun.mockReturnValue(new Promise(() => {}));
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([
        { runStarted: { runId: 'run-pending-alpha' } },
        { runFinished: { runId: 'run-pending-alpha' } },
      ]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    await waitFor(() =>
      expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
        'scope-alpha',
        'run-pending-alpha',
      ),
    );
    expect(
      screen.queryByText('Checking Activity for this run…'),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();
  });

  it('does not show an informational fallback for steps without guided fields', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: 'name: committed_source\nroles: []\nsteps:\n  - id: step-root\n    type: vote\n',
      updatedAtUtc: '2026-08-04T10:00:00Z',
      document: {
        name: 'committed_source',
        roles: [],
        steps: [{ id: 'step-root', type: 'vote' }],
      },
      draftExists: false,
      findings: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const inspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    expect(
      within(inspector).queryByText(
        'Guided options are not available for this step yet.',
      ),
    ).not.toBeInTheDocument();
    expect(inspector.querySelector('.ant-alert-info')).toBeNull();
  });

  it('keeps a stream run error as an execution failure even when no message is supplied', async () => {
    const encoder = new TextEncoder();
    let streamController:
      | ReadableStreamDefaultController<Uint8Array>
      | undefined;
    mockRuntimeRunsApi.streamChat.mockResolvedValue({
      body: new ReadableStream({
        start(controller) {
          streamController = controller;
        },
      }),
      ok: true,
    } as Response);

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    await act(async () => {
      streamController?.enqueue(encoder.encode('data: {"runError":{}}\n\n'));
    });
    expect(await screen.findByText('Run failed')).toBeInTheDocument();
    const startRun = screen.getByRole('button', {
      name: 'Start published run',
    });
    expect(startRun).toBeDisabled();
    expect(
      screen.queryByText(
        'Live updates ended. Open Activity to check the latest status.',
      ),
    ).not.toBeInTheDocument();
    await act(async () => {
      streamController?.close();
    });
    await waitFor(() => expect(startRun).toBeEnabled());
  });

  it('keeps a stopped live run ahead of stale running Activity', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        runId: 'run-stopped-alpha',
        stateVersion: 9,
        status: 'running',
      }),
    );
    mockRuntimeRunsApi.streamChat.mockResolvedValue(
      createSseResponse([
        { runStarted: { runId: 'run-stopped-alpha' } },
        {
          runStopped: {
            reason: 'Stopped by operator.',
            runId: 'run-stopped-alpha',
          },
        },
      ]),
    );

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    const stoppedLog = await within(logs).findByText('Run stopped');
    expect(within(logs).getByText('stopped')).toBeInTheDocument();
    fireEvent.click(stoppedLog);
    expect(within(logs).getByLabelText('Log details')).toHaveTextContent(
      'Stopped by operator.',
    );
    expect(within(logs).queryByText('succeeded')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
        'scope-alpha',
        'run-stopped-alpha',
      ),
    );
  });

  it('keeps a live transport failure visible ahead of running Activity', async () => {
    mockWorkflowActivityApi.getRun.mockResolvedValue(
      createEditorRunDetail({
        runId: 'run-disconnected-alpha',
        stateVersion: 10,
        status: 'running',
      }),
    );
    const encoder = new TextEncoder();
    let streamController:
      | ReadableStreamDefaultController<Uint8Array>
      | undefined;
    mockRuntimeRunsApi.streamChat.mockResolvedValue({
      body: new ReadableStream({
        start(controller) {
          streamController = controller;
        },
      }),
      ok: true,
    } as Response);

    await renderPublishedWorkflowPage();

    fireEvent.click(await screen.findByRole('button', { name: 'Run' }));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    await act(async () => {
      streamController?.enqueue(
        encoder.encode(
          'data: {"runStarted":{"runId":"run-disconnected-alpha"}}\n\n',
        ),
      );
    });
    await waitFor(() =>
      expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
        'scope-alpha',
        'run-disconnected-alpha',
      ),
    );
    await act(async () => {
      streamController?.enqueue(
        encoder.encode(
          'data: {"custom":{"name":"aevatar.step.request","payload":{"input":"Review order 42","stepId":"step-live","stepType":"llm_call"}}}\n\n',
        ),
      );
    });
    const logs = await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    await within(logs).findByTestId(
      'workflow-execution-log-row-node-step-live',
    );

    await act(async () => {
      streamController?.error(new Error('Published run stream disconnected.'));
    });

    expect(
      within(logs).getByText('Published run stream disconnected.'),
    ).toBeInTheDocument();
    expect(
      within(logs).getByTestId('workflow-execution-log-row-node-step-live'),
    ).toBeInTheDocument();
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
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Published run input' }),
      {
        target: { value: 'Review order 42' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Start published run' }),
    );

    await screen.findByRole('complementary', {
      name: 'Workflow run console',
    });
    await waitFor(() =>
      expect(
        screen.getAllByText('The workflow failed.').length,
      ).toBeGreaterThan(0),
    );
    expect(mockWorkflowActivityApi.getRun).toHaveBeenCalledWith(
      'scope-alpha',
      'run-failed-alpha',
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
      within(inspector).getByRole('button', { name: 'Apply step' }),
    );

    expect(
      await screen.findByText("Couldn't apply configuration"),
    ).toBeVisible();
    expect(screen.getByText(serializeFailure.message)).not.toBeVisible();
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );
    expect(
      within(inspector).getByRole('button', { name: 'Apply step' }),
    ).toBeEnabled();

    fireEvent.click(
      within(inspector).getByRole('button', { name: 'Apply step' }),
    );
    await waitFor(() =>
      expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(
        within(inspector).getByRole('button', { name: 'Apply step' }),
      ).toBeDisabled(),
    );
  });

  it('keeps unapplied node configuration when the selected node is clicked again', async () => {
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
      screen.getByRole('button', { name: 'Select step:step-root' }),
    );

    expect(
      screen.queryByRole('dialog', { name: 'Discard node changes?' }),
    ).not.toBeInTheDocument();
    expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );
  });

  it('asks before switching from a node with unapplied configuration', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '',
      directoryId: '',
      directoryLabel: '',
      yaml: [
        'name: committed_source',
        'roles: []',
        'steps:',
        '  - id: step-root',
        '    type: llm_call',
        '    parameters:',
        '      prompt_prefix: Original prompt',
        '  - id: step-second',
        '    type: transform',
        '    parameters:',
        '      operation: trim',
        '',
      ].join('\n'),
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
          {
            id: 'step-second',
            type: 'transform',
            parameters: { operation: 'trim' },
          },
        ],
      },
      draftExists: false,
      findings: [],
    });
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(
      await screen.findByRole('button', { name: 'Select step:step-root' }),
    );
    const rootInspector = await screen.findByRole('complementary', {
      name: 'Configure step-root',
    });
    fireEvent.change(within(rootInspector).getByLabelText('Instruction'), {
      target: { value: 'Updated prompt' },
    });
    const secondNode = await screen.findByRole('button', {
      name: 'Select step:step-second',
    });

    fireEvent.click(secondNode);

    const discardDialog = await screen.findByRole('dialog', {
      name: 'Discard node changes?',
    });
    expect(within(rootInspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );
    fireEvent.click(
      within(discardDialog).getByRole('button', { name: 'Cancel' }),
    );
    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', { name: 'Discard node changes?' }),
      ).not.toBeInTheDocument(),
    );
    expect(within(rootInspector).getByLabelText('Instruction')).toHaveValue(
      'Updated prompt',
    );

    fireEvent.click(secondNode);
    fireEvent.click(
      within(
        await screen.findByRole('dialog', {
          name: 'Discard node changes?',
        }),
      ).getByRole('button', { name: 'Discard changes' }),
    );

    expect(
      await screen.findByRole('complementary', {
        name: 'Configure step-second',
      }),
    ).toBeVisible();
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
    mockRuntimeCatalogApi.listWorkflowTemplates.mockResolvedValue({
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
    });
    mockStudioApi.instantiateWorkflowTemplate.mockResolvedValue({
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
    });
    mockStudioApi.getWorkflowDraftFile.mockResolvedValue({
      workflowId: 'wf-created-alpha',
      name: 'Incident triage',
      fileName: 'incident-triage.yaml',
      filePath: '/workflows/incident-triage.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: incident_triage\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-18T00:00:00Z',
      document: { name: 'incident_triage', roles: [], steps: [] },
      draftExists: true,
      findings: [],
    });
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

    const describeButton = await screen.findByRole('button', {
      name: 'Describe',
    });
    await waitFor(() => expect(describeButton).toBeEnabled());
    fireEvent.click(describeButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: ' incident REVIEW ' },
    });

    expect(
      await screen.findByText(
        'Another workflow already uses this name. Duplicate names are allowed.',
      ),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Generate and open' }),
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
    const describeButton = await screen.findByRole('button', {
      name: 'Describe',
    });
    await waitFor(() => expect(describeButton).toBeEnabled());
    fireEvent.click(describeButton);
    fireEvent.change(screen.getByLabelText('Workflow name'), {
      target: { value: 'Incident review' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

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
    const describeButton = await screen.findByRole('button', {
      name: 'Describe',
    });
    await waitFor(() => expect(describeButton).toBeEnabled());
    fireEvent.click(describeButton);

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
    fireEvent.click(screen.getByRole('button', { name: 'Describe' }));

    expect(
      screen.queryByText("Workflow couldn't be created"),
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText('Workflow name')).toHaveValue('Weekly review');
  });

  it('keeps implementation version metadata out of the public template browser', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const templateButton = await screen.findByRole('button', {
      name: 'Use template',
    });
    await waitFor(() => expect(templateButton).toBeEnabled());
    fireEvent.click(templateButton);

    expect(screen.queryByText(/2026\.08\.1/)).not.toBeInTheDocument();
  });

  it('navigates to the canonical template route from the creation chooser', async () => {
    renderWithQueryClient(<WorkflowActivityVNextPage />);

    const templateButton = await screen.findByRole('button', {
      name: 'Use template',
    });
    await waitFor(() => expect(templateButton).toBeEnabled());
    fireEvent.click(templateButton);

    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates',
    );
    expect(screen.queryByText('Incident triage')).not.toBeInTheDocument();
  });

  it('renders the template route with one page heading and returns to the chooser', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates';

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByRole('heading', { name: 'Start from a template' }),
    ).toBeInTheDocument();
    expect(
      screen.getAllByRole('heading', { name: 'Start from a template' }),
    ).toHaveLength(1);
    expect(
      screen.getByText(
        'Browse public templates, inspect details, or create a draft directly.',
      ),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Change method' }));
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new',
    );
  });

  it('explains when the template contract is unavailable and keeps the raw error in technical details', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates';
    mockRuntimeCatalogApi.listWorkflowTemplates.mockRejectedValue(
      Object.assign(new Error('HTTP 404 Not Found'), { status: 404 }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    expect(
      await screen.findByText(
        'Templates are not available in this environment.',
      ),
    ).toBeVisible();
    expect(screen.getByText('HTTP 404 Not Found')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
  });

  it('instantiates a public template with its authority version and opens the materialized draft', async () => {
    mockStudioApi.listWorkflowDrafts.mockResolvedValue([
      { workflowId: 'wf-created-alpha' },
    ]);
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    const templateButton = await screen.findByRole('button', {
      name: 'Use template',
    });
    await waitFor(() => expect(templateButton).toBeEnabled());
    fireEvent.click(templateButton);
    setMockLocation(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates',
    );
    fireEvent.click(
      await screen.findByRole('button', {
        name: 'Use template Incident triage',
      }),
    );

    await waitFor(() =>
      expect(mockStudioApi.instantiateWorkflowTemplate).toHaveBeenCalledWith({
        expectedAuthorityStateVersion: 7,
        scopeId: 'scope-alpha',
        templateId: 'template-incident-triage',
      }),
    );
    expect(mockStudioApi.parseYaml).not.toHaveBeenCalled();
    expect(history.push).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
    );
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
