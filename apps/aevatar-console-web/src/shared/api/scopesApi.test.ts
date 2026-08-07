import { persistAuthSession } from '@/shared/auth/session';
import { scopesApi } from './scopesApi';

describe('scopesApi', () => {
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
          displayName: 'Workflow alpha',
          serviceKey: 'scope-alpha:workflow-alpha',
          workflowName: 'Workflow alpha',
          actorId: 'actor-workflow-alpha',
          activeRevisionId: 'rev-alpha',
          publishedServiceId: 'svc-alpha',
          deploymentId: 'deployment-alpha',
          deploymentStatus: 'Available',
          updatedAt: '2026-08-07T00:00:00Z',
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
      },
    });

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/scopes/scope-alpha/workflows/wf-alpha',
      expect.objectContaining({ headers: expect.any(Headers) }),
    );
  });
});
