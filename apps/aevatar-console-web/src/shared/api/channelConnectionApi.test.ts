import { authFetch } from '@/shared/auth/fetch';
import { listChannelServices } from './channelServicesApi';
import { ChannelRegistrationError, channelsApi } from './channelsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
const fetchMock = jest.mocked(authFetch);
const originalFetch = global.fetch;
const telegramFetch = jest.fn();
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;

beforeEach(() => {
  telegramFetch.mockReset();
  global.fetch = telegramFetch;
});
afterEach(() => {
  fetchMock.mockReset();
  global.fetch = originalFetch;
});

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
  expect(telegramFetch).not.toHaveBeenCalled();
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

it.each([
  {
    label: undefined,
    skillName: undefined,
    expectedLabel: 'My Bot',
    expectedSkill: 'My Bot',
  },
  {
    label: ' Custom label ',
    skillName: ' ',
    expectedLabel: 'Custom label',
    expectedSkill: 'My Bot',
  },
  {
    label: ' ',
    skillName: ' Custom skill ',
    expectedLabel: 'My Bot',
    expectedSkill: 'Custom skill',
  },
])('defaults only blank names from Telegram first_name: $expectedLabel / $expectedSkill', async ({
  label,
  skillName,
  expectedLabel,
  expectedSkill,
}) => {
  const token = '123456:TEST_ONLY_TOKEN';
  telegramFetch.mockResolvedValue(
    response({
      ok: true,
      result: {
        is_bot: true,
        first_name: ' My Bot ',
        username: 'different_username',
      },
    }),
  );
  fetchMock.mockResolvedValue(
    response(
      { status: 'accepted', registration_id: 'registration-defaults' },
      202,
    ),
  );
  await channelsApi.registerTelegram({
    botToken: ` ${token} `,
    label,
    skillName,
    serviceIds: ['user-service-a'],
    webhookBaseUrl: 'https://api.example.test',
  });
  expect(telegramFetch).toHaveBeenCalledTimes(1);
  expect(telegramFetch).toHaveBeenCalledWith(
    `https://api.telegram.org/bot${token}/getMe`,
    {
      method: 'POST',
      credentials: 'omit',
      cache: 'no-store',
      referrerPolicy: 'no-referrer',
      redirect: 'error',
      signal: expect.any(AbortSignal),
    },
  );
  expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toMatchObject({
    label: expectedLabel,
    default_skill_name: expectedSkill,
    bot_token: token,
    service_ids: ['user-service-a'],
  });
});

it('does not register using a username when Telegram omits a usable bot name', async () => {
  telegramFetch.mockResolvedValue(
    response({
      ok: true,
      result: { is_bot: true, first_name: ' ', username: 'not_the_bot_name' },
    }),
  );
  await expect(
    channelsApi.registerTelegram({
      botToken: '123456:TEST_ONLY_TOKEN',
      serviceIds: [],
      webhookBaseUrl: 'https://api.example.test',
    }),
  ).rejects.toEqual(new ChannelRegistrationError('botName'));
  expect(fetchMock).not.toHaveBeenCalled();
});

it('aborts a stalled name lookup without submitting a registration', async () => {
  jest.useFakeTimers();
  try {
    telegramFetch.mockImplementation(
      (_input: string, init: RequestInit) =>
        new Promise((_resolve, reject) => {
          init.signal?.addEventListener('abort', () =>
            reject(new DOMException('Lookup aborted', 'AbortError')),
          );
        }),
    );
    const registration = channelsApi.registerTelegram({
      botToken: '123456:TEST_ONLY_TOKEN',
      serviceIds: [],
      webhookBaseUrl: 'https://api.example.test',
    });
    const rejection = expect(registration).rejects.toEqual(
      new ChannelRegistrationError('botName'),
    );
    await jest.advanceTimersByTimeAsync(15_000);
    await rejection;
    expect(fetchMock).not.toHaveBeenCalled();
  } finally {
    jest.useRealTimers();
  }
});
