import {
  act,
  fireEvent,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import * as React from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { renderWithQueryClient } from '../../../../tests/reactQueryTestUtils';
import ScopeServiceRuntimeWorkbench from './ScopeServiceRuntimeWorkbench';

jest.mock('@/shared/api/scopeRuntimeApi', () => ({
  scopeRuntimeApi: {
    createServiceBinding: jest.fn(),
    getServiceBindingCatalogSnapshot: jest.fn(),
    getServiceBindings: jest.fn(),
    getServiceRevision: jest.fn(),
    getServiceRevisions: jest.fn(),
    getServiceRunAudit: jest.fn(),
    listServiceRuns: jest.fn(),
    retireServiceBinding: jest.fn(),
    retireServiceRevision: jest.fn(),
    updateServiceBinding: jest.fn(),
  },
}));

const mockGetServiceBindings = scopeRuntimeApi.getServiceBindings as jest.Mock;
const mockGetServiceBindingCatalogSnapshot =
  scopeRuntimeApi.getServiceBindingCatalogSnapshot as jest.Mock;
const mockGetServiceRevision = scopeRuntimeApi.getServiceRevision as jest.Mock;
const mockGetServiceRevisions =
  scopeRuntimeApi.getServiceRevisions as jest.Mock;
const mockListServiceRuns = scopeRuntimeApi.listServiceRuns as jest.Mock;
const mockCreateServiceBinding =
  scopeRuntimeApi.createServiceBinding as jest.Mock;
const mockRetireServiceBinding =
  scopeRuntimeApi.retireServiceBinding as jest.Mock;

const service = {
  activeServingRevisionId: 'rev-alpha',
  appId: 'default',
  defaultServingRevisionId: 'rev-alpha',
  deploymentId: 'deployment-alpha',
  deploymentStatus: 'Active',
  displayName: 'Alpha service',
  endpoints: [
    {
      description: 'Runs the alpha service.',
      displayName: 'Chat',
      endpointId: 'chat',
      kind: 'http',
      requestTypeUrl: 'type.googleapis.com/example.Request',
      responseTypeUrl: 'type.googleapis.com/example.Response',
    },
  ],
  namespace: 'default',
  policyIds: [],
  primaryActorId: 'actor-alpha',
  serviceId: 'svc-alpha',
  serviceKey: 'scope-alpha:default:default:svc-alpha',
  tenantId: 'scope-alpha',
  updatedAt: '2026-08-05T00:00:00Z',
};

const bindings = {
  bindings: [
    {
      bindingId: 'binding-alpha',
      bindingKind: 'service',
      connectorRef: null,
      displayName: 'Alpha dependency',
      policyIds: [],
      retired: false,
      secretRef: null,
      serviceRef: {
        endpointId: 'chat',
        identity: {
          appId: 'default',
          namespace: 'default',
          serviceId: 'svc-target',
          tenantId: 'scope-alpha',
        },
      },
    },
  ],
  serviceKey: service.serviceKey,
  updatedAt: '2026-08-05T00:00:00Z',
};

const revisions = {
  activeServingRevisionId: 'rev-alpha',
  catalogLastEventId: 'event-alpha',
  catalogStateVersion: 1,
  defaultServingRevisionId: 'rev-alpha',
  deploymentId: 'deployment-alpha',
  deploymentStatus: 'Active',
  displayName: 'Alpha service',
  primaryActorId: 'actor-alpha',
  revisions: [],
  scopeId: 'scope-alpha',
  serviceId: 'svc-alpha',
  serviceKey: service.serviceKey,
  updatedAt: '2026-08-05T00:00:00Z',
};

const revisionsWithRetirableRevision = {
  ...revisions,
  revisions: [
    {
      allocationWeight: 100,
      artifactHash: 'artifact-retire',
      createdAt: '2026-08-05T00:00:00Z',
      deploymentId: 'deployment-alpha',
      failureReason: '',
      implementationKind: 'workflow',
      inlineWorkflowCount: 0,
      isActiveServing: false,
      isDefaultServing: false,
      isServingTarget: false,
      preparedAt: '2026-08-05T00:00:00Z',
      primaryActorId: 'actor-alpha',
      publishedAt: '2026-08-05T00:00:00Z',
      retiredAt: null,
      revisionId: 'rev-retire',
      scriptDefinitionActorId: '',
      scriptId: '',
      scriptRevision: '',
      scriptSourceHash: '',
      servingState: 'ready',
      staticActorTypeName: '',
      status: 'ready',
      workflowDefinitionActorId: 'definition-alpha',
      workflowName: 'alpha-workflow',
    },
  ],
};

const runs = {
  displayName: 'Alpha service',
  runs: [],
  serviceId: 'svc-alpha',
  serviceKey: service.serviceKey,
  scopeId: 'scope-alpha',
};

const betaService = {
  ...service,
  displayName: 'Beta service',
  primaryActorId: 'actor-beta',
  serviceId: 'svc-beta',
  serviceKey: 'scope-alpha:default:default:svc-beta',
};

function createDeferred<T>() {
  let rejectPromise: (reason?: unknown) => void = () => undefined;
  let resolvePromise: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve, reject) => {
    resolvePromise = resolve;
    rejectPromise = reject;
  });

  return {
    promise,
    reject: rejectPromise,
    resolve: resolvePromise,
  };
}

describe('ScopeServiceRuntimeWorkbench', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockGetServiceBindingCatalogSnapshot.mockResolvedValue({
      kind: 'available',
      snapshot: bindings,
    });
    mockGetServiceBindings.mockResolvedValue(bindings);
    mockGetServiceRevision.mockResolvedValue(
      revisionsWithRetirableRevision.revisions[0],
    );
    mockGetServiceRevisions.mockResolvedValue(revisions);
    mockListServiceRuns.mockResolvedValue(runs);
    mockRetireServiceBinding.mockResolvedValue({
      commandId: 'command-alpha',
      correlationId: 'correlation-alpha',
      targetActorId: 'actor-alpha',
    });
    mockCreateServiceBinding.mockResolvedValue({
      commandId: 'command-alpha',
      correlationId: 'correlation-alpha',
      targetActorId: 'actor-alpha',
    });
  });

  it('keeps a binding retirement visible until the bindings list confirms it', async () => {
    mockGetServiceBindingCatalogSnapshot
      .mockResolvedValueOnce({ kind: 'available', snapshot: bindings })
      .mockResolvedValueOnce({ kind: 'available', snapshot: bindings })
      .mockResolvedValueOnce({
        kind: 'available',
        snapshot: {
          ...bindings,
          bindings: [{ ...bindings.bindings[0], retired: true }],
        },
      });

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));

    expect(
      await screen.findByText('Update is still pending.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText('Update confirmed.')).toBeInTheDocument();
  });

  it('keeps an empty binding id error next to the field', async () => {
    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));
    fireEvent.click(
      await screen.findByRole('button', { name: 'Create binding' }),
    );

    const bindingId = await screen.findByRole('textbox', {
      name: 'Binding ID',
    });
    expect(await screen.findByText('Enter a binding ID.')).toBeInTheDocument();
    expect(bindingId).toHaveAttribute('aria-invalid', 'true');
    expect(bindingId).toHaveAttribute(
      'aria-describedby',
      'scope-runtime-binding-id-error',
    );
  });

  it('treats a not-yet-materialized first binding as pending without sending a second create', async () => {
    const createdBinding = {
      bindingId: 'binding-cache',
      bindingKind: 'connector',
      connectorRef: {
        connectorId: 'cache-primary',
        connectorType: 'redis',
      },
      displayName: '',
      policyIds: [],
      retired: false,
      secretRef: null,
      serviceRef: null,
    };
    mockGetServiceBindingCatalogSnapshot
      .mockResolvedValueOnce({
        kind: 'available',
        snapshot: { ...bindings, bindings: [] },
      })
      .mockResolvedValueOnce({ kind: 'not_materialized' })
      .mockResolvedValueOnce({
        kind: 'available',
        snapshot: { ...bindings, bindings: [createdBinding] },
      });

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Binding ID' }), {
      target: { value: 'binding-cache' },
    });
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Binding type' }));
    fireEvent.click(await screen.findByText('Connector'));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector type' }),
      {
        target: { value: 'redis' },
      },
    );
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector ID' }),
      {
        target: { value: 'cache-primary' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Create binding' }),
    );

    expect(
      await screen.findByText('Update is still pending.'),
    ).toBeInTheDocument();
    expect(mockCreateServiceBinding).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(await screen.findByText('Update confirmed.')).toBeInTheDocument();
    expect(mockCreateServiceBinding).toHaveBeenCalledTimes(1);
  });

  it('keeps the binding editor open while a write is in flight and preserves its draft on failure', async () => {
    const createRequest = createDeferred<{
      commandId: string;
      correlationId: string;
      targetActorId: string;
    }>();
    mockCreateServiceBinding.mockReturnValueOnce(createRequest.promise);

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Binding ID' }), {
      target: { value: 'binding-cache' },
    });
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Binding type' }));
    fireEvent.click(await screen.findByText('Connector'));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector type' }),
      {
        target: { value: 'redis' },
      },
    );
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector ID' }),
      {
        target: { value: 'cache-primary' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Create binding' }),
    );
    await waitFor(() =>
      expect(mockCreateServiceBinding).toHaveBeenCalledTimes(1),
    );

    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();
    expect(
      screen.queryByRole('button', { name: 'Close' }),
    ).not.toBeInTheDocument();

    await act(async () => {
      createRequest.reject(new Error('write failed'));
    });

    expect(
      await screen.findByText('Could not confirm the update'),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('dialog', { name: 'Create binding' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Binding ID' })).toHaveValue(
      'binding-cache',
    );
  });

  it('keeps a binding save failure available after its editor is closed', async () => {
    mockCreateServiceBinding.mockRejectedValueOnce(new Error('write failed'));

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Add binding' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Binding ID' }), {
      target: { value: 'binding-cache' },
    });
    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Binding type' }));
    fireEvent.click(await screen.findByText('Connector'));
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector type' }),
      {
        target: { value: 'redis' },
      },
    );
    fireEvent.change(
      await screen.findByRole('textbox', { name: 'Connector ID' }),
      {
        target: { value: 'cache-primary' },
      },
    );
    fireEvent.click(
      await screen.findByRole('button', { name: 'Create binding' }),
    );

    expect(
      await screen.findByText('Could not confirm the update'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(
      screen.queryByRole('dialog', { name: 'Create binding' }),
    ).not.toBeInTheDocument();
    expect(
      await screen.findByText('Could not confirm the update'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
  });

  it('blocks competing binding and revision writes while an action is unresolved', async () => {
    mockGetServiceRevisions.mockResolvedValue(revisionsWithRetirableRevision);

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));

    expect(
      await screen.findByText('Update is still pending.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add binding' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Edit binding' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Retire' })).toBeDisabled();

    fireEvent.click(screen.getByRole('tab', { name: /Revisions/ }));

    expect(
      await screen.findByRole('button', { name: 'Retire revision' }),
    ).toBeDisabled();
  });

  it('keeps pending binding feedback visible after changing runtime tabs', async () => {
    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));
    expect(await screen.findByText('Update is still pending.')).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: /Revisions/ }));

    expect(await screen.findByText('Update is still pending.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeVisible();
  });

  it('does not restore dismissed feedback after a deferred refresh resolves', async () => {
    const refreshRequest = createDeferred<{
      kind: 'available';
      snapshot: typeof bindings;
    }>();
    mockGetServiceBindingCatalogSnapshot
      .mockResolvedValueOnce({ kind: 'available', snapshot: bindings })
      .mockResolvedValueOnce({ kind: 'available', snapshot: bindings })
      .mockReturnValueOnce(refreshRequest.promise);

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));
    expect(
      await screen.findByText('Update is still pending.'),
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(
      await screen.findByText('Refreshing current status'),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }));

    await act(async () => {
      refreshRequest.resolve({
        kind: 'available',
        snapshot: {
          ...bindings,
          bindings: [{ ...bindings.bindings[0], retired: true }],
        },
      });
    });

    expect(screen.queryByText('Update confirmed.')).not.toBeInTheDocument();
    expect(
      screen.queryByText('Update is still pending.'),
    ).not.toBeInTheDocument();
  });

  it('keeps a pending service change available after switching to another service and back', async () => {
    const alphaRetirement = createDeferred<{
      commandId: string;
      correlationId: string;
      targetActorId: string;
    }>();
    mockRetireServiceBinding.mockReturnValueOnce(alphaRetirement.promise);

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service, betaService]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));
    await waitFor(() =>
      expect(mockRetireServiceBinding).toHaveBeenCalledWith(
        'scope-alpha',
        'svc-alpha',
        'binding-alpha',
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Inspect service' }));
    await waitFor(() =>
      expect(mockGetServiceBindingCatalogSnapshot).toHaveBeenCalledWith(
        'scope-alpha',
        'svc-beta',
      ),
    );

    await act(async () => {
      alphaRetirement.resolve({
        commandId: 'command-alpha',
        correlationId: 'correlation-alpha',
        targetActorId: 'actor-alpha',
      });
    });

    fireEvent.click(screen.getByRole('button', { name: 'Inspect service' }));

    expect(
      await screen.findByText('Update is still pending.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
  });

  it('keeps a newer service binding retirement isolated from a previous service receipt', async () => {
    const alphaRetirement = createDeferred<{
      commandId: string;
      correlationId: string;
      targetActorId: string;
    }>();
    const betaRetirement = createDeferred<{
      commandId: string;
      correlationId: string;
      targetActorId: string;
    }>();
    const betaBindings = {
      ...bindings,
      serviceKey: betaService.serviceKey,
    };
    mockGetServiceBindingCatalogSnapshot.mockImplementation(
      (_scopeId: string, serviceId: string) =>
        Promise.resolve({
          kind: 'available',
          snapshot:
            serviceId === betaService.serviceId ? betaBindings : bindings,
        }),
    );
    mockRetireServiceBinding
      .mockReturnValueOnce(alphaRetirement.promise)
      .mockReturnValueOnce(betaRetirement.promise);

    renderWithQueryClient(
      <ScopeServiceRuntimeWorkbench
        onUseEndpoint={jest.fn()}
        scopeId="scope-alpha"
        services={[service, betaService]}
      />,
    );

    fireEvent.click(
      await screen.findByRole('button', { name: 'Review bindings' }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));
    await waitFor(() =>
      expect(mockRetireServiceBinding).toHaveBeenCalledWith(
        'scope-alpha',
        'svc-alpha',
        'binding-alpha',
      ),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Inspect service' }));
    await waitFor(() =>
      expect(mockGetServiceBindingCatalogSnapshot).toHaveBeenCalledWith(
        'scope-alpha',
        'svc-beta',
      ),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Retire' }));
    await waitFor(() =>
      expect(mockRetireServiceBinding).toHaveBeenLastCalledWith(
        'scope-alpha',
        'svc-beta',
        'binding-alpha',
      ),
    );
    const betaRetireButton = screen.getByRole('button', { name: /Retire$/ });
    expect(
      within(betaRetireButton).getByRole('img', { name: 'loading' }),
    ).toBeInTheDocument();
    const betaReadsBeforeReceipt =
      mockGetServiceBindingCatalogSnapshot.mock.calls.filter(
        ([, serviceId]) => serviceId === 'svc-beta',
      ).length;

    await act(async () => {
      alphaRetirement.resolve({
        commandId: 'command-alpha',
        correlationId: 'correlation-alpha',
        targetActorId: 'actor-alpha',
      });
    });

    await waitFor(() =>
      expect(
        mockGetServiceBindingCatalogSnapshot.mock.calls.filter(
          ([, serviceId]) => serviceId === 'svc-beta',
        ),
      ).toHaveLength(betaReadsBeforeReceipt),
    );
    expect(
      within(screen.getByRole('button', { name: /Retire$/ })).getByRole('img', {
        name: 'loading',
      }),
    ).toBeInTheDocument();
    expect(
      screen.queryByText('Update is still pending.'),
    ).not.toBeInTheDocument();
  });
});
