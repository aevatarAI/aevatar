import { scopesApi } from './scopesApi';

describe('scopesApi', () => {
  const originalFetch = global.fetch;

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it('preserves the explicit published service identity on workflow summaries', async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      json: async () => [
        {
          scopeId: 'scope-alpha',
          workflowId: 'legacy-catalogue-row-alpha',
          publishedServiceId: 'svc-alpha',
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
    );
  });
});
