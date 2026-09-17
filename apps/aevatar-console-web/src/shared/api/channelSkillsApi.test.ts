import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../tests/fixtures/nyxidServiceSession';
import { searchChannelSkills } from './channelSkillsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({
    baseUrl: 'https://nyx.example.test',
    enabled: true,
  }),
}));
const fetchMock = jest.mocked(authFetch);
it('uses the current user token and private proxy search, preserving shared skills and cursor pagination', async () => {
  const session = createNyxIDServiceSession();
  persistAuthSession(session);
  fetchMock.mockResolvedValue({
    ok: true,
    json: async () => ({
      data: {
        items: [
          {
            guid: 'skill-one',
            name: 'shared-skill',
            description: 'Shared with the team',
            myAccessReason: 'shared-via-org',
            createdByEmail: 'private@example.test',
          },
        ],
        meta: { hasMore: true, nextCursor: 'cursor-two' },
      },
      error: null,
    }),
  } as Response);
  const signal = new AbortController().signal;
  expect(
    await searchChannelSkills('team & docs', 'cursor-one', signal),
  ).toEqual({
    items: [
      {
        id: 'skill-one',
        name: 'shared-skill',
        description: 'Shared with the team',
      },
    ],
    nextCursor: 'cursor-two',
  });
  const [url, init] = fetchMock.mock.calls[0];
  expect(new URL(String(url)).pathname).toBe(
    '/api/v1/proxy/s/ornn-api/api/v1/skill-search',
  );
  expect(Object.fromEntries(new URL(String(url)).searchParams)).toEqual({
    scope: 'private',
    mode: 'keyword',
    q: 'team & docs',
    limit: '50',
    cursor: 'cursor-one',
  });
  expect(init).toMatchObject({
    credentials: 'omit',
    cache: 'no-store',
    headers: { Authorization: `Bearer ${session.tokens.accessToken}` },
  });
  expect(init?.signal).toBe(signal);
  fetchMock.mockResolvedValue({
    ok: true,
    json: async () => ({ error: { message: 'TEST_SECRET' }, data: null }),
  } as Response);
  await expect(searchChannelSkills('')).rejects.toThrow('Skill search failed.');
});
