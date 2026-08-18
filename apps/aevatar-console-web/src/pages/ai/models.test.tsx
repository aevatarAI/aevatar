import { screen } from '@testing-library/react';
import React from 'react';
import { aiModelsApi } from '@/shared/api/aiModelsApi';
import { renderWithQueryClient } from '../../../tests/reactQueryTestUtils';
import AIModelsPage from './models';

function createWorkspaceContext() {
  return {
    context: {
      apis: { models: '/api/ai/models' },
      consistency: 'independent_read_models',
      features: {
        models: {
          api: '/api/ai/models',
          availability: 'available',
          page: '/ai/models',
        },
      },
      pages: { models: '/ai/models' },
      scopeId: 'scope-alpha',
    },
    queryAuthority: {
      principalId: 'user-alpha',
      sessionExpiresAt: 1_700_003_600_000,
    },
    scopeId: 'scope-alpha',
  };
}

let mockWorkspaceContext = createWorkspaceContext();

jest.mock('@/shared/api/aiModelsApi', () => ({
  aiModelsApi: { getModels: jest.fn() },
}));

jest.mock('./components/AIWorkspaceShell', () => {
  const mockReact = jest.requireActual('react');
  return {
    __esModule: true,
    default: ({ children }: { children: never }) =>
      mockReact.createElement(mockReact.Fragment, null, children),
    useAIWorkspaceContext: () => mockWorkspaceContext,
  };
});

const mockedGetModels = aiModelsApi.getModels as jest.Mock;

function modelsView(): import('@/shared/api/aiModelsApi').AIModelsView {
  return {
    consistency: 'independent_authorities',
    personalDefault: {
      source: 'user_llm_preferences',
      authorityKind: 'authenticated_user',
      availability: 'available',
      authorityStateVersion: null,
      updatedAtUtc: null,
      settings: {
        savedSelection: {
          routeKind: 'nyx_id_user_service',
          routeValue: 'route-personal',
          nyxIdUserServiceId: 'user-service-personal',
          serviceSlugSnapshot: 'personal-runtime',
          modelSelection: { kind: 'explicit_model', modelId: 'gpt-personal' },
        },
        savedRouteLabel: 'Personal runtime',
        selectionStatus: 'ready',
        catalogDiagnostic: 'unspecified',
        remediation: 'none',
        routeOptions: [],
        catalogStatus: 'ready',
        capabilities: {
          canEditRoute: true,
          canEditModel: true,
          canSave: true,
          canRetryCatalog: true,
        },
      },
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
        sources: [],
        effectiveSource: 'scope',
        effectiveSources: [
          {
            sourceId: 'user:user-service-scope',
            serviceSlugSnapshot: '',
            catalogServiceId: null,
            userServiceId: 'user-service-scope',
            modelSelectionMode: 'explicit_models',
            modelIds: ['gpt-scope'],
          },
          {
            sourceId: 'future:source-alpha',
            serviceSlugSnapshot: null,
            catalogServiceId: null,
            userServiceId: null,
            modelSelectionMode: 'explicit_models',
            modelIds: ['gpt-future'],
          },
        ],
        lastMutationId: 'mutation-alpha',
      },
      error: null,
    },
  };
}

describe('AIModelsPage', () => {
  beforeEach(() => {
    mockedGetModels.mockReset();
    mockWorkspaceContext = createWorkspaceContext();
  });

  it('renders personal default and scope catalog as separate authorities', async () => {
    mockedGetModels.mockResolvedValue(modelsView());

    renderWithQueryClient(React.createElement(AIModelsPage));

    expect(
      await screen.findByRole('heading', {
        level: 2,
        name: 'My default model',
      }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 1, name: 'Models' }),
    ).toBeInTheDocument();
    expect(screen.getByText('gpt-personal')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 2, name: 'Available models' }),
    ).toBeInTheDocument();
    expect(screen.getAllByText('Service unavailable')).toHaveLength(2);
    expect(screen.getByText('user-service-scope')).toBeInTheDocument();
    expect(screen.getByText('Unknown source')).toBeInTheDocument();
    expect(screen.getByText('gpt-scope')).toBeInTheDocument();
    expect(screen.getByText('gpt-future')).toBeInTheDocument();
    expect(mockedGetModels).toHaveBeenCalledWith(
      '/api/ai/models',
      expect.any(AbortSignal),
    );
  });

  it('keeps personal settings usable when the scope catalog is unavailable', async () => {
    const view = modelsView();
    view.scopeCatalog = {
      ...view.scopeCatalog,
      availability: 'unavailable',
      authorityStateVersion: null,
      updatedAtUtc: null,
      policy: null,
      error: {
        code: 'CATALOG_READ_UNAVAILABLE',
        message: 'Catalog projection is unavailable.',
      },
    };
    mockedGetModels.mockResolvedValue(view);

    renderWithQueryClient(React.createElement(AIModelsPage));

    expect(await screen.findByText('gpt-personal')).toBeInTheDocument();
    expect(screen.getByText('CATALOG_READ_UNAVAILABLE')).toBeInTheDocument();
    expect(
      screen.getByText('Catalog projection is unavailable.'),
    ).toBeInTheDocument();
  });

  it('does not query Models when its capability contract is incomplete', async () => {
    mockWorkspaceContext = {
      ...createWorkspaceContext(),
      context: {
        ...createWorkspaceContext().context,
        apis: { models: '' },
      },
    };

    renderWithQueryClient(React.createElement(AIModelsPage));

    expect(await screen.findByText('Models not available')).toBeInTheDocument();
    expect(mockedGetModels).not.toHaveBeenCalled();
  });

  it('fails closed when the returned catalog belongs to another scope', async () => {
    const view = modelsView();
    view.scopeCatalog.scopeId = 'scope-beta';
    mockedGetModels.mockResolvedValue(view);

    renderWithQueryClient(React.createElement(AIModelsPage));

    expect(
      await screen.findByText('Model catalog scope mismatch'),
    ).toBeInTheDocument();
    expect(screen.queryByText('gpt-personal')).not.toBeInTheDocument();
    expect(screen.queryByText('gpt-scope')).not.toBeInTheDocument();
  });

  it('does not describe an unsupported saved selection as a system default', async () => {
    const view = modelsView();
    const savedSelection = view.personalDefault.settings?.savedSelection;
    if (!savedSelection) {
      throw new Error('Expected a saved selection fixture.');
    }
    savedSelection.modelSelection = { kind: 'unsupported', modelId: null };
    mockedGetModels.mockResolvedValue(view);

    renderWithQueryClient(React.createElement(AIModelsPage));

    expect(
      await screen.findByText('Unsupported selection'),
    ).toBeInTheDocument();
    expect(screen.queryByText('System default')).not.toBeInTheDocument();
  });
});
