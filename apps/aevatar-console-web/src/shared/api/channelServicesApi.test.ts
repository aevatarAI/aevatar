import { authFetch } from '@/shared/auth/fetch';
import { listChannelServices } from './channelServicesApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
const fetchMock = jest.mocked(authFetch);
const inventoryPath = 'https://nyx.example.test/api/v1/user-services';
const catalogPath = 'https://nyx.example.test/api/v1/mcp/config';
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

afterEach(() => fetchMock.mockReset());

it('returns only active account services authorized by the caller catalog, matching exact UserService IDs', async () => {
  fetchMock.mockImplementation(async (input) =>
    input === inventoryPath
      ? response({
          services: [
            personal,
            { ...personal, id: 'us-other-account-key' },
            { ...personal, id: 'catalog-github', slug: 'platform-copy' },
            { ...personal, id: 'us-inactive', is_active: false },
            {
              ...personal,
              id: 'us-org',
              slug: 'drive',
              label: '',
              catalog_service_name: 'Google Drive',
              credential_source: {
                type: 'org',
                org_name: 'Team',
                allowed: true,
              },
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
        })
      : response({
          contract_version: '1.0',
          services: [
            { service_id: 'us-work', is_user_service: true },
            { service_id: 'us-org', is_user_service: true },
            { service_id: 'us-inactive', is_user_service: true },
            { service_id: 'us-viewer', is_user_service: true },
            { service_id: 'us-unknown', is_user_service: true },
            { service_id: 'catalog-github', is_user_service: false },
            { service_id: 'us-not-in-inventory', is_user_service: true },
          ],
        }),
  );
  const signal = new AbortController().signal;
  const result = await listChannelServices(signal);
  expect(result).toEqual([
    {
      id: 'us-work',
      slug: 'api-github',
      label: 'GitHub work',
      active: true,
      allowed: true,
      source: 'personal',
      organizationName: null,
    },
    {
      id: 'us-org',
      slug: 'drive',
      label: 'Google Drive',
      active: true,
      allowed: true,
      source: 'organization',
      organizationName: 'Team',
    },
  ]);
  expect(JSON.stringify(result)).not.toContain('TEST_ONLY_SECRET');
  expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
    inventoryPath,
    catalogPath,
  ]);
  for (const path of [inventoryPath, catalogPath]) {
    expect(fetchMock).toHaveBeenCalledWith(path, {
      signal,
      credentials: 'omit',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
    });
  }
});

it('keeps an empty caller catalog empty even when the account owns services', async () => {
  fetchMock.mockImplementation(async (input) =>
    response(
      input === inventoryPath
        ? { services: [personal] }
        : { contract_version: '1.0', services: [] },
    ),
  );
  await expect(listChannelServices()).resolves.toEqual([]);
});

it.each([
  ['denied access', {}, 403],
  ['unsupported contract', { contract_version: 'future', services: [] }, 200],
  [
    'missing identity kind',
    { contract_version: '1.0', services: [{ service_id: 'us-work' }] },
    200,
  ],
  [
    'ambiguous identity',
    {
      contract_version: '1.0',
      services: [
        { service_id: 'us-work', is_user_service: true },
        { service_id: 'us-work', is_user_service: false },
      ],
    },
    200,
  ],
] as const)('never falls back to the account inventory after %s', async (_reason, catalog, status) => {
  fetchMock.mockImplementation(async (input) =>
    input === inventoryPath
      ? response({ services: [personal] })
      : response(catalog, status),
  );
  await expect(listChannelServices()).rejects.toThrow();
});
