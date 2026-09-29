import { persistAuthSession } from '@/shared/auth/session';
import { scopesApi } from './scopesApi';

describe('scopesApi workflow catalogue', () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, 'now').mockReturnValue(1_700_000_000_000);
    persistAuthSession({
      tokens: {
        accessToken: 'access-token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: { sub: 'user-1' },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it('preserves the explicit published service identity on workflow summaries', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      json: async () => [
        {
          scopeId: 'scope-alpha',
          workflowId: 'legacy-catalogue-row-alpha',
          publishedServiceId: 'svc-alpha',
          serviceAppId: 'workflow-app',
          serviceNamespace: 'workflow-namespace',
          displayName: 'Published workflow',
          serviceKey: 'scope-alpha:studio:default:svc-alpha',
          workflowName: 'published_workflow',
          actorId: 'actor-alpha',
          activeRevisionId: 'rev-alpha',
          deploymentId: 'dep-alpha',
          deploymentStatus: 'Active',
          updatedAt: '2026-08-06T10:00:00Z',
        },
      ],
      ok: true,
      status: 200,
      statusText: 'OK',
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(scopesApi.listWorkflows('scope-alpha')).resolves.toEqual([
      expect.objectContaining({
        workflowId: 'legacy-catalogue-row-alpha',
        publishedServiceId: 'svc-alpha',
      }),
    ]);
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-alpha/workflows?includeSource=false',
      expect.objectContaining({ headers: expect.any(Headers) }),
    );
  });

  it('queries and decodes the backend-owned workflow catalogue contract', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [
          {
            scopeId: 'scope alpha',
            workflowId: 'wf-alpha',
            name: '审批 workflow',
            description: 'Handles approvals',
            hasDraftSource: true,
            hasCommittedSource: true,
            updatedAtUtc: '2026-08-04T10:00:00Z',
            updatedAtSource: 'committed',
            publishedServiceId: 'svc-alpha',
            capabilities: {
              open: { available: true, unavailableReason: null },
              activity: { available: true, unavailableReason: null },
              rename: { available: true, unavailableReason: null },
              delete: { available: false, unavailableReason: 'policy_denied' },
            },
            sourceWatermarkUtc: '2026-08-04T10:00:00Z',
            committed: {
              serviceKey: 'scope-alpha:workflow:default:svc-alpha',
              workflowName: 'approval_flow',
              actorId: 'actor-alpha',
              activeRevisionId: 'rev-alpha',
              deploymentId: 'dep-alpha',
              deploymentStatus: 'Active',
              serviceAppId: 'workflow-app',
              serviceNamespace: 'workflow-namespace',
            },
          },
          {
            scopeId: 'scope alpha',
            workflowId: 'wf-draft',
            name: 'Draft only',
            description: '',
            hasDraftSource: true,
            hasCommittedSource: false,
            updatedAtUtc: '2026-08-03T10:00:00Z',
            updatedAtSource: 'draft',
            capabilities: {
              open: { available: true, unavailableReason: null },
              activity: {
                available: false,
                unavailableReason: 'committed_source_missing',
              },
              rename: { available: true, unavailableReason: null },
              delete: { available: true, unavailableReason: null },
            },
            sourceWatermarkUtc: '2026-08-03T10:00:00Z',
            committed: null,
          },
        ],
        nextPageToken: 'next token',
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
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;
    const controller = new AbortController();

    const response = await scopesApi.queryWorkflowCatalogue(
      {
        scopeId: 'scope alpha',
        view: 'drafts',
        query: '审批 flow',
        cursor: 'next token',
        take: 25,
      },
      controller.signal,
    );

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe(
      '/api/scopes/scope%20alpha/workflow-catalogue?view=drafts&query=%E5%AE%A1%E6%89%B9+flow&cursor=next+token&take=25',
    );
    expect(init?.signal).toBe(controller.signal);
    expect(response.nextPageToken).toBe('next token');
    expect(response.items[0]).toMatchObject({
      workflowId: 'wf-alpha',
      publishedServiceId: 'svc-alpha',
      capabilities: {
        open: { available: true, unavailableReason: null },
        activity: { available: true, unavailableReason: null },
        rename: { available: true, unavailableReason: null },
        delete: { available: false, unavailableReason: 'policy_denied' },
      },
      committed: {
        serviceKey: 'scope-alpha:workflow:default:svc-alpha',
        actorId: 'actor-alpha',
        deploymentId: 'dep-alpha',
        serviceAppId: 'workflow-app',
        serviceNamespace: 'workflow-namespace',
      },
    });
    expect(response.items[1]?.committed).toBeNull();
    expect(response.freshness.refreshWatermarkUtc).toBe('2026-08-04T10:00:00Z');
    expect(response.search.maximumQueryLength).toBe(128);
  });

  it('reads the published service identity from the workflow detail read model', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
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
          deploymentId: 'dep-alpha',
          deploymentStatus: 'Active',
          updatedAt: '2026-08-04T10:00:00Z',
          publishedServiceId: 'svc-alpha',
          serviceAppId: 'workflow-app',
          serviceNamespace: 'workflow-namespace',
        },
        source: null,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopesApi.getWorkflowDetail('scope-alpha', 'wf-alpha'),
    ).resolves.toMatchObject({
      available: true,
      scopeId: 'scope-alpha',
      workflow: {
        workflowId: 'wf-alpha',
        activeRevisionId: 'rev-alpha',
        publishedServiceId: 'svc-alpha',
        serviceAppId: 'workflow-app',
        serviceNamespace: 'workflow-namespace',
      },
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-alpha/workflows/wf-alpha',
      expect.objectContaining({ headers: expect.any(Headers) }),
    );
  });

  it('archives a workflow through the scope-owned command boundary', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 202,
      json: async () => ({
        scopeId: 'scope alpha',
        workflowId: 'wf/alpha',
        deploymentId: 'dep-alpha',
        commandHandle: {
          stage: 'deactivate_deployment',
          targetActorId: 'deployment-actor-alpha',
          commandId: 'cmd-archive-alpha',
          correlationId: 'corr-archive-alpha',
        },
        readModelUrl: '/api/scopes/scope%20alpha/workflows/wf%2Falpha',
        acceptanceStage: 'accepted',
        propagationStage: 'readmodel_propagating',
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      scopesApi.archiveWorkflow('scope alpha', 'wf/alpha'),
    ).resolves.toMatchObject({
      scopeId: 'scope alpha',
      workflowId: 'wf/alpha',
      deploymentId: 'dep-alpha',
      commandHandle: {
        stage: 'deactivate_deployment',
        commandId: 'cmd-archive-alpha',
      },
      acceptanceStage: 'accepted',
    });
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope%20alpha/workflows/wf%2Falpha:archive',
      expect.objectContaining({ method: 'POST', headers: expect.any(Headers) }),
    );
  });
});
