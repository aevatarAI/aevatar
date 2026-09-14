import { authFetch } from '@/shared/auth/fetch';
import { listChannelServices } from './channelServicesApi';
import { ChannelRegistrationError, channelsApi } from './channelsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;

afterEach(() => fetchMock.mockReset());

it('maps the documented NyxID credential variants and keeps exact service IDs without retaining credential data', async () => {
  fetchMock.mockResolvedValue(
    response({
      services: [
        {
          id: 'user-service-a',
          slug: 'api-github',
          label: 'GitHub work',
          is_active: true,
          credential_source: { type: 'personal' },
          default_request_headers: [{ value: 'TEST_ONLY_SECRET' }],
        },
        {
          id: 'user-service-b',
          slug: 'drive',
          catalog_service_name: 'Google Drive',
          is_active: true,
          credential_source: {
            type: 'org',
            org_name: 'Team',
            role: 'viewer',
            allowed: false,
          },
        },
        {
          id: 'user-service-c',
          slug: 'calendar',
          is_active: false,
          credential_source: { type: 'org', org_name: 'Team', allowed: true },
        },
        {
          id: 'user-service-d',
          slug: 'future',
          is_active: true,
          credential_source: { type: 'new_source', allowed: true },
        },
      ],
    }),
  );
  const result = await listChannelServices();
  expect(fetchMock.mock.calls[0][0]).toBe(
    'https://nyx.example.test/api/v1/user-services',
  );
  expect(
    result.map(({ id, label, allowed, active, source }) => ({
      id,
      label,
      allowed,
      active,
      source,
    })),
  ).toEqual([
    {
      id: 'user-service-a',
      label: 'GitHub work',
      allowed: true,
      active: true,
      source: 'personal',
    },
    {
      id: 'user-service-b',
      label: 'Google Drive',
      allowed: false,
      active: true,
      source: 'organization',
    },
    {
      id: 'user-service-c',
      label: 'calendar',
      allowed: true,
      active: false,
      source: 'organization',
    },
    {
      id: 'user-service-d',
      label: 'future',
      allowed: false,
      active: true,
      source: 'unknown',
    },
  ]);
  expect(JSON.stringify(result)).not.toContain('TEST_ONLY_SECRET');
});

it('preserves an explicit empty allowlist and retains only the accepted registration identity', async () => {
  fetchMock.mockResolvedValue(
    response(
      {
        status: 'accepted',
        registration_id: 'registration-new',
        webhook_url: 'TEST_ONLY_SECRET',
      },
      202,
    ),
  );
  expect(
    await channelsApi.registerTelegram({
      botToken: ' TEST_ONLY_TOKEN ',
      label: ' Label ',
      skillName: ' Skill ',
      serviceIds: [],
      webhookBaseUrl: 'https://api.example.test',
    }),
  ).toEqual({ registrationId: 'registration-new' });
  expect(fetchMock.mock.calls[0][0]).toBe('/api/channels/registrations');
  expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
    platform: 'telegram',
    bot_token: 'TEST_ONLY_TOKEN',
    label: 'Label',
    default_skill_name: 'Skill',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: [],
    webhook_base_url: 'https://api.example.test',
  });
  fetchMock.mockResolvedValue(
    response(
      { status: 'accepted', registration_id: '', note: 'TEST_ONLY_SECRET' },
      202,
    ),
  );
  await expect(
    channelsApi.registerTelegram({
      botToken: 'TEST_ONLY_TOKEN',
      label: 'Label',
      skillName: 'Skill',
      serviceIds: [],
      webhookBaseUrl: 'https://api.example.test',
    }),
  ).rejects.toEqual(new ChannelRegistrationError('uncertain'));
});
