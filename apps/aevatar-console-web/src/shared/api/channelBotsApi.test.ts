import { authFetch } from '@/shared/auth/fetch';
import { listChannelBotIdentities } from './channelBotsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({ baseUrl: 'https://nyx.example.test' }),
}));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;

it('reads personal bot labels in one request and keeps only safe identity fields', async () => {
  fetchMock.mockResolvedValue(
    response({
      bots: [
        {
          id: 'bot-alpha',
          platform: 'telegram',
          label: ' Team channel ',
          platform_bot_id: 'different-platform-id',
          access_token: 'TEST_ONLY_SECRET',
          webhook_url: 'https://example.invalid/private',
        },
        { id: 'bot-beta', platform: 'lark', label: ' ' },
      ],
      total: 2,
    }),
  );
  const signal = new AbortController().signal;
  expect(await listChannelBotIdentities(signal)).toEqual([
    { id: 'bot-alpha', platform: 'telegram', label: 'Team channel' },
    { id: 'bot-beta', platform: 'lark', label: null },
  ]);
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock).toHaveBeenCalledWith(
    'https://nyx.example.test/api/v1/channel-bots',
    {
      signal,
      credentials: 'omit',
      cache: 'no-store',
      headers: { Accept: 'application/json' },
    },
  );
});

it('rejects ambiguous identities and HTTP failures without retaining error bodies', async () => {
  const bot = { id: 'bot-alpha', platform: 'telegram', label: 'Team channel' };
  fetchMock.mockResolvedValue(response({ bots: [bot, bot] }));
  await expect(listChannelBotIdentities()).rejects.toThrow('Ambiguous');
  const json = jest.fn().mockResolvedValue({ token: 'TEST_ONLY_SECRET' });
  fetchMock.mockResolvedValue({
    ok: false,
    status: 403,
    json,
  } as unknown as Response);
  await expect(listChannelBotIdentities()).rejects.toThrow('403');
  expect(json).not.toHaveBeenCalled();
});
