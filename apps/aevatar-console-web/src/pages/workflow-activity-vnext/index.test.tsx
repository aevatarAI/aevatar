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
  isStudioApiStatus: (error: unknown, status: number) =>
    Boolean(
      error &&
        typeof error === 'object' &&
        'status' in error &&
        error.status === status,
    ),
  studioApi: {
    archiveTeam: jest.fn(),
    authorWorkflow: jest.fn(),
    createMember: jest.fn(),
    createTeam: jest.fn(),
    createWorkflowDraft: jest.fn(),
    deleteMember: jest.fn(),
    deleteWorkflowDraft: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    getAuthSession: jest.fn(),
    getMember: jest.fn(),
    getMemberBindingRun: jest.fn(),
    getTeam: jest.fn(),
    getUserConfigRuntime: jest.fn(),
    getUserLlmSettings: jest.fn(),
    getWorkflow: jest.fn(),
    getWorkflowDraft: jest.fn(),
    getWorkflowDraftFile: jest.fn(),
    listMembers: jest.fn(),
    listWorkflowDrafts: jest.fn(),
    bindMemberWorkflow: jest.fn(),
    parseYaml: jest.fn(),
    previewExplicitRequests: jest.fn(),
    publishWorkflow: jest.fn(),
    saveWorkflow: jest.fn(),
    saveAndBindWorkflow: jest.fn(),
    saveUserLlmSettings: jest.fn(),
    serializeYaml: jest.fn(),
    updateMemberDisplayName: jest.fn(),
    updateMemberImplementationRef: jest.fn(),
    updateWorkflowDraft: jest.fn(),
  },
}));

jest.mock('@/shared/studio/explicitRequestConfirmation', () => ({
  confirmInteractiveExplicitRequestPreview: jest.fn(),
  createWorkflowRevisionIdentityCandidate: jest.fn(),
}));

jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: {
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
      onConnectNodes,
      onDeleteEdges,
      onDeleteNodes,
      onEdgeSelect,
      onNodeLayoutChange,
      onNodeSelect,
    }: {
      nodes: readonly { readonly id: string }[];
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
    }) => (
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
    ),
  }),
);

const mockStudioApi = jest.requireMock('@/shared/studio/api').studioApi as {
  archiveTeam: jest.Mock;
  authorWorkflow: jest.Mock;
  createMember: jest.Mock;
  createTeam: jest.Mock;
  createWorkflowDraft: jest.Mock;
  deleteMember: jest.Mock;
  deleteWorkflowDraft: jest.Mock;
  getWorkspaceSettings: jest.Mock;
  getAuthSession: jest.Mock;
  getMember: jest.Mock;
  getMemberBindingRun: jest.Mock;
  getTeam: jest.Mock;
  getUserConfigRuntime: jest.Mock;
  getUserLlmSettings: jest.Mock;
  getWorkflow: jest.Mock;
  getWorkflowDraft: jest.Mock;
  getWorkflowDraftFile: jest.Mock;
  listMembers: jest.Mock;
  listWorkflowDrafts: jest.Mock;
  bindMemberWorkflow: jest.Mock;
  parseYaml: jest.Mock;
  previewExplicitRequests: jest.Mock;
  publishWorkflow: jest.Mock;
  saveWorkflow: jest.Mock;
  saveAndBindWorkflow: jest.Mock;
  saveUserLlmSettings: jest.Mock;
  serializeYaml: jest.Mock;
  updateMemberDisplayName: jest.Mock;
  updateMemberImplementationRef: jest.Mock;
  updateWorkflowDraft: jest.Mock;
};
const mockCreateWorkflowRevisionIdentityCandidate = jest.requireMock(
  '@/shared/studio/explicitRequestConfirmation',
).createWorkflowRevisionIdentityCandidate as jest.Mock;
const mockConfirmInteractiveExplicitRequestPreview = jest.requireMock(
  '@/shared/studio/explicitRequestConfirmation',
).confirmInteractiveExplicitRequestPreview as jest.Mock;
const mockScopesApi = jest.requireMock('@/shared/api/scopesApi').scopesApi as {
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
    mockHistoryMutatesLocation = false;
    jest.clearAllMocks();
    mockScopesApi.queryWorkflowCatalogue.mockResolvedValue(
      createCatalogueResponse([]),
    );
    mockServicesApi.deactivateDeployment.mockResolvedValue({
      targetActorId: 'deployment-manager-alpha',
      commandId: 'cmd-archive-alpha',
      correlationId: 'corr-archive-alpha',
    });
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [],
      nextPageToken: null,
    });
    mockStudioApi.deleteMember.mockResolvedValue({
      status: 'delete_accepted',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
    });
    mockStudioApi.archiveTeam.mockResolvedValue({
      status: 'accepted',
      scopeId: 'scope-alpha',
      teamId: 't-alpha',
    });
    mockStudioApi.updateMemberDisplayName.mockResolvedValue({
      status: 'accepted',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
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
      screen.queryByRole('option', { name: 'Active workflows' }),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('option', { name: 'Archived' }),
    ).not.toBeInTheDocument();
  });

  it('maps unsupported legacy views to the backend all view', async () => {
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
    expect(screen.getByText('All workflows')).toBeInTheDocument();
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
      expect.objectContaining({ view: 'all' }),
      expect.any(AbortSignal),
    );
    expect(history.replace).toHaveBeenCalledWith(
      '/scopes/scope-alpha/workflow-activity-vnext/workflows',
    );
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
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
  });

  it('resolves archive identity and observes the exact row across catalogue pages', async () => {
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
    mockScopesApi.getWorkflowDetail.mockResolvedValue({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        scopeId: 'scope-alpha',
        workflowId: 'wf-alpha',
        displayName: 'Workflow Alpha',
        serviceKey: 'opaque-service-key',
        workflowName: 'workflow_alpha',
        actorId: 'actor-alpha',
        activeRevisionId: 'rev-alpha',
        deploymentId: 'dep-authoritative',
        deploymentStatus: 'Active',
        updatedAt: '2026-08-04T10:00:00Z',
        publishedServiceId: 'svc-alpha',
        serviceAppId: 'workflow-app',
        serviceNamespace: 'workflow-namespace',
      },
      source: null,
    });
    mockScopesApi.queryWorkflowCatalogue.mockImplementation(
      (input: { cursor?: string; query?: string }) => {
        if (input.query === 'wf-alpha' && input.cursor !== 'archive-page-2') {
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
        view: 'all',
        query: 'wf-alpha',
        cursor: undefined,
        take: 100,
      }),
    );
    expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith({
      scopeId: 'scope-alpha',
      view: 'all',
      query: 'wf-alpha',
      cursor: 'archive-page-2',
      take: 100,
    });
    expect(mockScopesApi.listWorkflows).not.toHaveBeenCalled();
    expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledWith(
      'svc-alpha',
      'dep-authoritative',
      {
        tenantId: 'scope-alpha',
        appId: 'workflow-app',
        namespace: 'workflow-namespace',
      },
    );
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
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          scopeId: 'scope-alpha',
          displayName: 'Committed source',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-committed-source',
          },
          lifecycleStage: 'draft',
          publishedServiceId: '',
          lastBoundRevisionId: null,
          teamId: 't-alpha',
          createdAt: '2026-08-07T09:00:00Z',
          updatedAt: '2026-08-07T09:00:00Z',
        },
      ],
      nextPageToken: null,
    });
    mockConfirmInteractiveExplicitRequestPreview.mockResolvedValue([]);
    mockStudioApi.bindMemberWorkflow.mockResolvedValue({
      status: 'accepted',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      ackStage: 'dispatch_accepted',
    });
    mockStudioApi.getMemberBindingRun.mockResolvedValue({
      status: 'succeeded',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      result: {
        publishedServiceId: 'svc-alpha',
        revisionId: 'rev-preview-alpha',
        implementationKind: 'workflow',
      },
      failure: null,
    });
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

  it('saves dirty content and publishes through the typed backing member binding run', async () => {
    mockLocation =
      '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-alpha';
    mockCreateWorkflowRevisionIdentityCandidate.mockReturnValue(
      'rev-preview-alpha',
    );
    const savedYaml =
      'name: approval_flow_updated\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n';
    const savedWorkflow = {
      workflowId: 'wf-alpha',
      name: 'Approval flow updated',
      fileName: 'approval-flow.yaml',
      filePath: '/workflows/approval-flow.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: savedYaml,
      updatedAtUtc: '2026-08-07T10:00:00Z',
      document: {
        name: 'approval_flow_updated',
        roles: [],
        steps: [{ id: 'step-alpha', type: 'llm_call' }],
      },
      draftExists: true,
      findings: [],
    };
    mockStudioApi.getWorkflow.mockResolvedValue({
      ...savedWorkflow,
      name: 'Approval flow',
      yaml: savedYaml.replaceAll('updated', ''),
    });
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          scopeId: 'scope-alpha',
          displayName: 'Approval flow',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-alpha',
          },
          lifecycleStage: 'draft',
          publishedServiceId: '',
          lastBoundRevisionId: null,
          teamId: 't-alpha',
          createdAt: '2026-08-07T09:00:00Z',
          updatedAt: '2026-08-07T09:00:00Z',
        },
      ],
      nextPageToken: null,
    });
    mockStudioApi.parseYaml.mockResolvedValue({
      document: savedWorkflow.document,
      findings: [],
    });
    mockStudioApi.serializeYaml.mockResolvedValue({
      yaml: savedYaml,
      document: savedWorkflow.document,
      findings: [],
    });
    mockStudioApi.saveWorkflow.mockResolvedValue({
      kind: 'materialized',
      workflow: savedWorkflow,
    });
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.change(await screen.findByLabelText('Workflow name'), {
      target: { value: 'Approval flow updated' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Publish' }));

    await waitFor(() =>
      expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        displayName: 'Approval flow updated',
        workflowId: 'wf-alpha',
        revisionId: 'rev-preview-alpha',
        workflowYamls: [savedYaml],
      }),
    );
    expect(mockStudioApi.saveWorkflow).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.saveWorkflow.mock.invocationCallOrder[0]).toBeLessThan(
      mockStudioApi.previewExplicitRequests.mock.invocationCallOrder[0],
    );
    expect(mockConfirmInteractiveExplicitRequestPreview).toHaveBeenCalledWith({
      workflowId: 'wf-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    expect(mockStudioApi.publishWorkflow).not.toHaveBeenCalled();
    expect(mockStudioApi.getMemberBindingRun).toHaveBeenCalledWith(
      'scope-alpha',
      'm-alpha',
      'bind-alpha',
    );
    expect(
      await screen.findByRole('button', { name: 'Published' }),
    ).toBeInTheDocument();
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
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          scopeId: 'scope-alpha',
          displayName: 'Workflow alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-draft-alpha',
          },
          lifecycleStage: 'active',
          publishedServiceId: '',
          lastBoundRevisionId: null,
          teamId: 't-alpha',
          createdAt: '2026-08-06T09:00:00Z',
          updatedAt: '2026-08-06T09:00:00Z',
        },
      ],
      nextPageToken: null,
    });
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.bindMemberWorkflow.mockResolvedValue({
      status: 'accepted',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      ackStage: 'dispatch_accepted',
    });
    const observedBindingRun = {
      status: 'succeeded',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      result: {
        publishedServiceId: 'svc-alpha',
        revisionId: 'rev-preview-alpha',
        implementationKind: 'workflow',
      },
      failure: null,
    };
    let resolveBindingRunObservation:
      | ((run: typeof observedBindingRun) => void)
      | undefined;
    mockStudioApi.getMemberBindingRun.mockImplementation(
      () =>
        new Promise((resolve) => {
          resolveBindingRunObservation = resolve;
        }),
    );
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
      expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: 'Workflow alpha',
        memberId: 'm-alpha',
        revisionId: 'rev-preview-alpha',
        scopeId: 'scope-alpha',
        workflowId: 'wf-draft-alpha',
        workflowYamls: [
          'name: workflow_alpha\nroles: []\nsteps:\n  - id: step-alpha\n    type: llm_call\n',
        ],
      }),
    );
    expect(mockStudioApi.saveAndBindWorkflow).not.toHaveBeenCalled();
    expect(mockScopeRuntimeApi.listServices).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Publishing' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    expect(screen.queryByText('Publication accepted')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Published' }),
    ).not.toBeInTheDocument();

    resolveBindingRunObservation?.(observedBindingRun);

    expect(
      await screen.findByRole('button', { name: 'Published' }),
    ).toHaveAttribute('aria-disabled', 'true');
    expect(screen.queryByText('Workflow published')).not.toBeInTheDocument();
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
      returnedBindingRunId: 'bind-alpha',
      returnedMemberId: 'm-other',
      returnedScopeId: 'scope-alpha',
      mismatch: 'member ID',
    },
    {
      returnedBindingRunId: 'bind-alpha',
      returnedMemberId: 'm-alpha',
      returnedScopeId: 'scope-other',
      mismatch: 'scope ID',
    },
  ])('keeps a returned $mismatch mismatch visible without starting observation', async ({
    returnedBindingRunId,
    returnedMemberId,
    returnedScopeId,
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
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          scopeId: 'scope-alpha',
          displayName: 'Workflow alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-draft-alpha',
          },
          lifecycleStage: 'active',
          publishedServiceId: '',
          lastBoundRevisionId: null,
          teamId: 't-alpha',
          createdAt: '2026-08-06T09:00:00Z',
          updatedAt: '2026-08-06T09:00:00Z',
        },
      ],
      nextPageToken: null,
    });
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockStudioApi.bindMemberWorkflow.mockResolvedValue({
      status: 'accepted',
      bindingRunId: returnedBindingRunId,
      scopeId: returnedScopeId,
      memberId: returnedMemberId,
      ackStage: 'dispatch_accepted',
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(mockConsoleToast.error).toHaveBeenCalled());
    expect(screen.getByRole('button', { name: 'Retry' })).toBeEnabled();
    expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.getMemberBindingRun).not.toHaveBeenCalled();
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
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [
        {
          memberId: 'm-alpha',
          scopeId: 'scope-alpha',
          displayName: 'Workflow alpha',
          description: '',
          implementationKind: 'workflow',
          implementationRef: {
            implementationKind: 'workflow',
            workflowId: 'wf-draft-alpha',
          },
          lifecycleStage: 'active',
          publishedServiceId: '',
          lastBoundRevisionId: null,
          teamId: 't-alpha',
          createdAt: '2026-08-06T09:00:00Z',
          updatedAt: '2026-08-06T09:00:00Z',
        },
      ],
      nextPageToken: null,
    });
    mockStudioApi.previewExplicitRequests.mockResolvedValue({
      workflowId: 'wf-draft-alpha',
      revisionId: 'rev-preview-alpha',
      items: [],
    });
    mockConfirmInteractiveExplicitRequestPreview.mockResolvedValue([]);
    mockStudioApi.bindMemberWorkflow.mockResolvedValue({
      status: 'accepted',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      ackStage: 'dispatch_accepted',
    });
  }

  function arrangeObservedWorkflowPublication(): void {
    arrangeSavedDraftPublication();
    mockStudioApi.getMemberBindingRun.mockResolvedValue({
      status: 'succeeded',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      result: {
        publishedServiceId: 'svc-alpha',
        revisionId: 'rev-preview-alpha',
        implementationKind: 'workflow',
      },
      failure: null,
    });
  }

  async function publishObservedWorkflow(): Promise<void> {
    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));
    await screen.findByRole('button', { name: 'Published' });
  }

  async function renderPublishedWorkflowPage(): Promise<void> {
    arrangeObservedWorkflowPublication();
    renderWithQueryClient(<WorkflowActivityVNextPage />);
    await publishObservedWorkflow();
  }

  it('returns to idle without an error when explicit request confirmation is cancelled', async () => {
    arrangeSavedDraftPublication();
    mockConfirmInteractiveExplicitRequestPreview.mockResolvedValue(null);

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() =>
      expect(mockConfirmInteractiveExplicitRequestPreview).toHaveBeenCalled(),
    );
    expect(mockStudioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(mockStudioApi.publishWorkflow).not.toHaveBeenCalled();
    expect(mockConsoleToast.error).not.toHaveBeenCalled();
    expect(
      await screen.findByRole('button', { name: 'Publish' }),
    ).toBeEnabled();
  });

  it('reports validation errors and warnings as deduplicated toasts without page alerts', async () => {
    mockStudioApi.getWorkflow.mockResolvedValue({
      workflowId: 'wf-committed-source',
      name: 'Committed source',
      fileName: 'committed-source.yaml',
      filePath: '/workflows/committed-source.yaml',
      directoryId: 'directory-alpha',
      directoryLabel: 'Workflows',
      yaml: 'name: committed_source\nroles: []\nsteps: []\n',
      updatedAtUtc: '2026-08-07T10:00:00Z',
      document: { name: 'committed_source', roles: [], steps: [] },
      draftExists: true,
      findings: [
        {
          code: 'STEP_INVALID',
          level: 'error',
          message: 'The step is invalid.',
          path: '/steps/0',
        },
        {
          code: 'STEP_REVIEW',
          level: 'warning',
          message: 'Review the optional step.',
          path: '/steps/1',
        },
      ],
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    await screen.findByDisplayValue('Committed source');
    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'The step is invalid.',
      ),
    );
    expect(mockConsoleToast.warning).toHaveBeenCalledWith(
      'Review the optional step.',
    );
    expect(document.querySelector('.wa-vnext__editor-alerts')).toBeNull();
    expect(document.querySelector('.ant-alert-error')).toBeNull();
    expect(document.querySelector('.ant-alert-warning')).toBeNull();
  });

  it('reports a rejected binding run with a toast instead of a page alert', async () => {
    arrangeSavedDraftPublication();
    mockStudioApi.getMemberBindingRun.mockResolvedValue({
      status: 'rejected',
      bindingRunId: 'bind-alpha',
      scopeId: 'scope-alpha',
      memberId: 'm-alpha',
      result: null,
      failure: {
        code: 'BINDING_REJECTED',
        message: 'Binding was rejected.',
      },
    });

    renderWithQueryClient(<WorkflowActivityVNextPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(mockConsoleToast.error).toHaveBeenCalled());
    expect(document.querySelector('#workflow-publication-status')).toBeNull();
    expect(document.querySelector('.ant-alert-error')).toBeNull();
    expect(screen.getByRole('button', { name: 'Check again' })).toBeEnabled();
  });

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

  it('retries a failed publication observation without sending a second POST', async () => {
    arrangeSavedDraftPublication();
    mockStudioApi.getMemberBindingRun
      .mockRejectedValueOnce(
        Object.assign(new Error('HTTP 503'), { status: 503 }),
      )
      .mockResolvedValueOnce({
        status: 'succeeded',
        bindingRunId: 'bind-alpha',
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        result: {
          publishedServiceId: 'svc-alpha',
          revisionId: 'rev-preview-alpha',
          implementationKind: 'workflow',
        },
        failure: null,
      });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(mockConsoleToast.error).toHaveBeenCalled());
    expect(
      screen.getByRole('button', { name: 'Publish blocked · 1 issue' }),
    ).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));

    await waitFor(() =>
      expect(mockStudioApi.getMemberBindingRun).toHaveBeenCalledTimes(2),
    );
    expect(mockStudioApi.getMemberBindingRun).toHaveBeenLastCalledWith(
      'scope-alpha',
      'm-alpha',
      'bind-alpha',
    );
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1);
  });

  it.each([
    [401, 'Sign in to continue'],
    [403, "You don't have access to this workspace"],
  ])('keeps an accepted publication receipt mutation-locked after a %i observation', async (status, message) => {
    arrangeSavedDraftPublication();
    mockStudioApi.getMemberBindingRun.mockRejectedValue(
      Object.assign(new Error(`HTTP ${status}`), { status }),
    );

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(message),
    );
    expect(
      screen.getByRole('button', { name: 'Publish blocked · 1 issue' }),
    ).toHaveAttribute('aria-disabled', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Check again' }));

    await waitFor(() =>
      expect(mockStudioApi.getMemberBindingRun).toHaveBeenCalledTimes(2),
    );
    expect(mockStudioApi.getMemberBindingRun).toHaveBeenLastCalledWith(
      'scope-alpha',
      'm-alpha',
      'bind-alpha',
    );
    expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
    expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1);
  });

  it('creates a fresh revision before republishing a failed publication receipt', async () => {
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
    mockStudioApi.bindMemberWorkflow
      .mockResolvedValueOnce({
        status: 'accepted',
        bindingRunId: 'bind-alpha',
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        ackStage: 'dispatch_accepted',
      })
      .mockResolvedValueOnce({
        status: 'accepted',
        bindingRunId: 'bind-beta',
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        ackStage: 'dispatch_accepted',
      });
    mockStudioApi.getMemberBindingRun
      .mockResolvedValueOnce({
        status: 'rejected',
        bindingRunId: 'bind-alpha',
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        result: null,
        failure: { code: 'BINDING_REJECTED', message: 'Binding rejected.' },
      })
      .mockResolvedValueOnce({
        status: 'succeeded',
        bindingRunId: 'bind-beta',
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
        result: {
          publishedServiceId: 'svc-alpha',
          revisionId: freshRevisionId,
          implementationKind: 'workflow',
        },
        failure: null,
      });

    renderWithQueryClient(<WorkflowActivityVNextPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Publish' }));

    await waitFor(() => expect(mockConsoleToast.error).toHaveBeenCalled());
    expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));

    await waitFor(() =>
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(2),
    );
    await waitFor(() =>
      expect(mockStudioApi.bindMemberWorkflow).toHaveBeenLastCalledWith(
        expect.objectContaining({ revisionId: freshRevisionId }),
      ),
    );
    expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(2);
    expect(
      await screen.findByRole('button', { name: 'Published' }),
    ).toBeInTheDocument();
  });

  it('retries delayed publication observation without sending a second POST', async () => {
    arrangeSavedDraftPublication();
    mockStudioApi.getMemberBindingRun.mockRejectedValue(
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
      expect(mockStudioApi.getMemberBindingRun).toHaveBeenCalledTimes(1);
      await act(async () => {
        await jest.advanceTimersByTimeAsync(5_000);
      });

      expect(mockConsoleToast.warning).toHaveBeenCalledWith(
        'Publication is taking longer to appear',
      );
      fireEvent.click(screen.getByRole('button', { name: 'Check again' }));
      await act(async () => {
        await jest.advanceTimersByTimeAsync(5_000);
      });

      expect(mockStudioApi.getMemberBindingRun).toHaveBeenCalledTimes(10);
      expect(mockStudioApi.getMemberBindingRun).toHaveBeenLastCalledWith(
        'scope-alpha',
        'm-alpha',
        'bind-alpha',
      );
      expect(mockStudioApi.previewExplicitRequests).toHaveBeenCalledTimes(1);
      expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1);
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
      expect(mockStudioApi.bindMemberWorkflow).toHaveBeenCalledTimes(1),
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

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow was saved but couldn't be reopened",
      ),
    );
    expect(
      screen.queryByText("Workflow was saved but couldn't be reopened"),
    ).not.toBeInTheDocument();
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

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Workflow YAML could not be read.',
      ),
    );
    expect(mockConsoleToast.warning).toHaveBeenCalledWith(
      'A workflow step needs review.',
    );
    expect(
      screen.queryByText('Workflow YAML could not be read.'),
    ).not.toBeInTheDocument();
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

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        "Workflow was saved but couldn't be reopened",
      ),
    );
    expect(
      screen.queryByText("Workflow was saved but couldn't be reopened"),
    ).not.toBeInTheDocument();
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

    const startRun = await screen.findByRole('button', { name: 'Start run' });
    await waitFor(() => expect(startRun).toBeDisabled());
    expect(
      screen.queryByText(
        'Live updates ended. Open Activity to check the latest status.',
      ),
    ).not.toBeInTheDocument();
    expect(document.querySelector('.ant-alert-info')).toBeNull();
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
    fireEvent.change(await screen.findByRole('textbox', { name: 'Input' }), {
      target: { value: 'Review order 42' },
    });
    fireEvent.click(await screen.findByRole('button', { name: 'Start run' }));

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
    mockStudioApi.listMembers.mockResolvedValue({
      scopeId: 'scope-alpha',
      members: [],
      nextPageToken: null,
    });
    mockStudioApi.createTeam.mockResolvedValue({
      teamId: 't-created-alpha',
      scopeId: 'scope-alpha',
      displayName: 'Incident review',
      description: '',
      lifecycleStage: 'active',
      memberCount: 0,
      createdAt: '2026-08-07T10:00:00Z',
      updatedAt: '2026-08-07T10:00:00Z',
    });
    mockStudioApi.getTeam.mockResolvedValue({
      teamId: 't-created-alpha',
      scopeId: 'scope-alpha',
      displayName: 'Incident review',
      description: '',
      lifecycleStage: 'active',
      memberCount: 1,
      createdAt: '2026-08-07T10:00:00Z',
      updatedAt: '2026-08-07T10:00:00Z',
    });
    const linkedMember = {
      memberId: 'm-created-alpha',
      scopeId: 'scope-alpha',
      displayName: 'Incident review',
      description: '',
      implementationKind: 'workflow',
      implementationRef: {
        implementationKind: 'workflow',
        workflowId: 'wf-created-alpha',
      },
      lifecycleStage: 'active',
      publishedServiceId: '',
      lastBoundRevisionId: null,
      teamId: 't-created-alpha',
      createdAt: '2026-08-07T10:00:00Z',
      updatedAt: '2026-08-07T10:00:00Z',
    };
    mockStudioApi.createMember.mockResolvedValue(linkedMember);
    mockStudioApi.getMember.mockResolvedValue({
      summary: linkedMember,
      implementationRef: linkedMember.implementationRef,
      lastBinding: null,
      currentBindingRun: null,
    });
    mockStudioApi.updateMemberImplementationRef.mockResolvedValue({
      status: 'accepted',
      scopeId: 'scope-alpha',
      memberId: 'm-created-alpha',
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
