import { authFetch } from '@/shared/auth/fetch';
import { aiModelsApi, decodeAIModels } from './aiModelsApi';

jest.mock('@/shared/auth/fetch', () => ({
  authFetch: jest.fn(),
}));

const mockedAuthFetch = authFetch as jest.Mock;

function settings() {
  return {
    savedSelection: {
      routeKind: 'nyx_id_user_service',
      routeValue: 'route-alpha',
      nyxIdUserServiceId: 'user-service-personal',
      serviceSlugSnapshot: 'personal-runtime',
      modelSelection: { kind: 'explicit_model', modelId: 'gpt-personal' },
    },
    savedRouteLabel: 'Personal runtime',
    selectionStatus: 'ready',
    catalogDiagnostic: 'unspecified',
    remediation: 'none',
    routeOptions: [
      {
        routeValue: 'route-alpha',
        label: 'Personal runtime',
        source: 'user_service',
        status: 'ready',
        allowed: true,
        ready: true,
        userServiceId: 'user-service-personal',
        serviceSlug: 'personal-runtime',
        modelCatalog: {
          certainty: 'enumerated',
          modelIds: ['gpt-personal'],
          defaultModelId: 'gpt-personal',
          diagnostic: 'unspecified',
        },
        description: null,
      },
    ],
    modelGroupsByRoute: [],
    catalogStatus: 'ready',
    capabilities: {
      canEditRoute: true,
      canEditModel: true,
      canSave: true,
      canRetryCatalog: true,
    },
    setupHint: null,
  };
}

function availablePayload() {
  return {
    consistency: 'independent_authorities',
    personalDefault: {
      source: 'user_llm_preferences',
      authorityKind: 'authenticated_user',
      availability: 'available',
      authorityStateVersion: null,
      updatedAtUtc: null,
      settings: settings(),
      error: null,
    },
    scopeCatalog: {
      source: 'llm_model_catalog_policy',
      authorityKind: 'scope',
      scopeId: 'scope-alpha',
      availability: 'available',
      authorityStateVersion: 31,
      updatedAtUtc: '2026-08-18T03:00:00Z',
      policy: {
        mode: 'custom_replace',
        configured: true,
        sources: [
          {
            sourceId: 'user:user-service-scope',
            serviceSlugSnapshot: 'scope-runtime',
            catalogServiceId: null,
            userServiceId: 'user-service-scope',
            modelSelectionMode: 'explicit_models',
            modelIds: ['gpt-scope'],
          },
        ],
        effectiveSource: 'scope',
        effectiveSources: [
          {
            sourceId: 'user:user-service-scope',
            serviceSlugSnapshot: 'scope-runtime',
            catalogServiceId: null,
            userServiceId: 'user-service-scope',
            modelSelectionMode: 'explicit_models',
            modelIds: ['gpt-scope'],
          },
        ],
        lastMutationId: 'mutation-alpha',
      },
      error: null,
    },
  };
}

function okJson(payload: unknown): Response {
  return {
    json: async () => payload,
    ok: true,
    status: 200,
    statusText: 'OK',
  } as Response;
}

describe('aiModelsApi', () => {
  beforeEach(() => {
    mockedAuthFetch.mockReset();
  });

  it('keeps personal and scope model identities in independent authorities', async () => {
    mockedAuthFetch.mockResolvedValueOnce(okJson(availablePayload()));

    await expect(
      aiModelsApi.getModels('/api/ai/models'),
    ).resolves.toMatchObject({
      personalDefault: {
        settings: {
          savedSelection: {
            nyxIdUserServiceId: 'user-service-personal',
            modelSelection: { modelId: 'gpt-personal' },
          },
        },
      },
      scopeCatalog: {
        scopeId: 'scope-alpha',
        authorityStateVersion: 31,
        policy: {
          effectiveSources: [
            {
              userServiceId: 'user-service-scope',
              modelIds: ['gpt-scope'],
            },
          ],
        },
      },
    });
    expect(mockedAuthFetch).toHaveBeenCalledWith(
      '/api/ai/models',
      expect.objectContaining({ headers: { Accept: 'application/json' } }),
    );
  });

  it('preserves an unavailable scope catalog without discarding personal settings', () => {
    const available = availablePayload();
    const payload = {
      ...available,
      scopeCatalog: {
        ...available.scopeCatalog,
        availability: 'unavailable',
        authorityStateVersion: null,
        updatedAtUtc: null,
        policy: null,
        error: {
          code: 'CATALOG_READ_UNAVAILABLE',
          message: 'Catalog projection is unavailable.',
        },
      },
    };

    expect(decodeAIModels(payload)).toMatchObject({
      personalDefault: {
        availability: 'available',
        settings: expect.any(Object),
      },
      scopeCatalog: {
        availability: 'unavailable',
        error: { code: 'CATALOG_READ_UNAVAILABLE' },
      },
    });
  });

  it('accepts an empty service slug snapshot without replacing it in the API model', () => {
    const available = availablePayload();
    const source = available.scopeCatalog.policy.effectiveSources[0];
    const payload = {
      ...available,
      scopeCatalog: {
        ...available.scopeCatalog,
        policy: {
          ...available.scopeCatalog.policy,
          effectiveSources: [{ ...source, serviceSlugSnapshot: '' }],
        },
      },
    };

    expect(decodeAIModels(payload)).toMatchObject({
      scopeCatalog: {
        policy: {
          effectiveSources: [{ serviceSlugSnapshot: '' }],
        },
      },
    });
  });

  it('accepts a future source with a null slug and no typed service identity', () => {
    const available = availablePayload();
    const source = available.scopeCatalog.policy.effectiveSources[0];
    const payload = {
      ...available,
      scopeCatalog: {
        ...available.scopeCatalog,
        policy: {
          ...available.scopeCatalog.policy,
          effectiveSources: [
            {
              ...source,
              sourceId: 'future:source-alpha',
              serviceSlugSnapshot: null,
              catalogServiceId: null,
              userServiceId: null,
            },
          ],
        },
      },
    };

    expect(decodeAIModels(payload)).toMatchObject({
      scopeCatalog: {
        policy: {
          effectiveSources: [
            {
              sourceId: 'future:source-alpha',
              serviceSlugSnapshot: null,
              catalogServiceId: null,
              userServiceId: null,
            },
          ],
        },
      },
    });
  });

  it('rejects a scope source that conflates catalog and user-service identities', () => {
    const available = availablePayload();
    const source = available.scopeCatalog.policy?.effectiveSources[0];
    if (!source) {
      throw new Error('fixture source is required');
    }
    const payload = {
      ...available,
      scopeCatalog: {
        ...available.scopeCatalog,
        policy: {
          ...available.scopeCatalog.policy,
          effectiveSources: [
            { ...source, catalogServiceId: 'catalog-service-alpha' },
          ],
        },
      },
    };

    expect(() => decodeAIModels(payload)).toThrow(
      'must not identify both a catalog service and a user service',
    );
  });
});
