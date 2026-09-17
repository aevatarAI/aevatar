import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../tests/fixtures/nyxidServiceSession';
import {
  listChannelServiceIdentities,
  listChannelServices,
} from './channelServicesApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({
    baseUrl: 'https://nyx.example.test',
    clientId: 'test-client',
    enabled: true,
  }),
}));
const fetchMock = jest.mocked(authFetch);
const originalFetch = global.fetch;
const inventoryPath = 'https://nyx.example.test/api/v1/user-services';
const personal = {
  id: 'us-work',
  slug: 'api-github',
  label: 'GitHub work',
  is_active: true,
  credential_source: { type: 'personal' },
  default_request_headers: [{ value: 'TEST_ONLY_SECRET' }],
};
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;

it('reads safe service names independently of current selectable grants and activity', async () => {
  persistAuthSession(createNyxIDServiceSession({ allowed_service_ids: [] }));
  fetchMock.mockResolvedValue(
    response({
      services: [
        personal,
        {
          ...personal,
          id: 'us-inactive',
          is_active: false,
          label: 'Old service',
        },
      ],
    }),
  );
  expect(await listChannelServiceIdentities()).toEqual([
    { id: 'us-work', slug: 'api-github', label: 'GitHub work' },
    { id: 'us-inactive', slug: 'api-github', label: 'Old service' },
  ]);
});

afterEach(() => {
  fetchMock.mockReset();
  global.fetch = originalFetch;
});

it('includes authorized LLM services and matches exact UserService grants without requiring an MCP tool entry', async () => {
  const session = createNyxIDServiceSession({
    allowed_service_ids: [
      'us-work',
      'us-model',
      'us-inactive',
      'us-org',
      'us-viewer',
      'us-unknown',
      'us-not-in-inventory',
      'catalog-github',
    ],
  });
  persistAuthSession(session);
  fetchMock.mockResolvedValue(
    response({
      services: [
        personal,
        { ...personal, id: 'us-other-account-key' },
        {
          ...personal,
          id: 'us-catalog-reference',
          catalog_service_id: 'catalog-github',
        },
        { ...personal, id: 'us-inactive', is_active: false },
        {
          ...personal,
          id: 'us-model',
          slug: 'chrono-llm-public',
          label: 'Chrono Public',
          service_type: 'llm',
        },
        {
          ...personal,
          id: 'us-org',
          slug: 'drive',
          label: '',
          catalog_service_name: 'Google Drive',
          credential_source: { type: 'org', org_name: 'Team', allowed: true },
        },
        {
          ...personal,
          id: 'us-viewer',
          credential_source: { type: 'org', allowed: false },
        },
        {
          ...personal,
          id: 'us-unknown',
          credential_source: { type: 'future', allowed: true },
        },
      ],
    }),
  );
  const signal = new AbortController().signal;
  const result = await listChannelServices(signal);
  expect(result.map(({ id, label }) => ({ id, label }))).toEqual([
    { id: 'us-work', label: 'GitHub work' },
    { id: 'us-model', label: 'Chrono Public' },
    { id: 'us-org', label: 'Google Drive' },
  ]);
  expect(JSON.stringify(result)).not.toContain('TEST_ONLY');
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock).toHaveBeenCalledWith(inventoryPath, {
    signal,
    credentials: 'omit',
    cache: 'no-store',
    headers: {
      Accept: 'application/json',
      Authorization: `Bearer ${session.tokens.accessToken}`,
    },
  });
});

it.each([
  true,
  false,
])('respects explicit allow_all_services=%s with an empty ID list', async (allowAll) => {
  persistAuthSession(
    createNyxIDServiceSession({
      allow_all_services: allowAll,
      allowed_service_ids: [],
    }),
  );
  fetchMock.mockResolvedValue(response({ services: [personal] }));
  const result = await listChannelServices();
  expect(result.map(({ id }) => id)).toEqual(allowAll ? ['us-work'] : []);
});

it.each([
  ['missing grant mode', { allow_all_services: undefined }],
  ['malformed ID list', { allowed_service_ids: ['us-work', null] }],
  ['wrong account subject', { sub: 'another-user' }],
  ['expired JWT', { exp: 1 }],
] as const)('fails closed for %s without exposing token payloads or reading the inventory', async (_name, claims) => {
  persistAuthSession(
    createNyxIDServiceSession({ ...claims, note: 'TEST_ONLY_SECRET' }),
  );
  await expect(listChannelServices()).rejects.toThrow(
    'Could not read the current NyxID service authorization.',
  );
  expect(fetchMock).not.toHaveBeenCalled();
});

it('does not treat locally decoded claims as successful server authorization', async () => {
  persistAuthSession(createNyxIDServiceSession({ allow_all_services: true }));
  fetchMock.mockResolvedValue(response({ services: [personal] }, 401));
  await expect(listChannelServices()).rejects.toMatchObject({ status: 401 });
});

it('refreshes an expired session before filtering and pins the inventory request to the refreshed bearer', async () => {
  const old = createNyxIDServiceSession({ allowed_service_ids: ['us-old'] });
  const fresh = createNyxIDServiceSession({ allowed_service_ids: ['us-work'] });
  persistAuthSession({
    ...old,
    tokens: {
      ...old.tokens,
      expiresAt: Date.now() - 1,
      refreshToken: 'TEST_ONLY_REFRESH',
    },
  });
  const tokenFetch = jest.fn().mockResolvedValue(
    response({
      access_token: fresh.tokens.accessToken,
      token_type: 'Bearer',
      expires_in: 3600,
    }),
  );
  global.fetch = tokenFetch;
  fetchMock.mockResolvedValue(
    response({ services: [personal, { ...personal, id: 'us-old' }] }),
  );
  expect((await listChannelServices()).map(({ id }) => id)).toEqual([
    'us-work',
  ]);
  expect(tokenFetch).toHaveBeenCalledTimes(1);
  expect(fetchMock).toHaveBeenCalledWith(
    inventoryPath,
    expect.objectContaining({
      headers: {
        Accept: 'application/json',
        Authorization: `Bearer ${fresh.tokens.accessToken}`,
      },
    }),
  );
});
