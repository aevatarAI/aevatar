import { authFetch } from '@/shared/auth/fetch';
import { persistAuthSession } from '@/shared/auth/session';
import { createNyxIDServiceSession } from '../../../tests/fixtures/nyxidServiceSession';
import {
  getChannelSkillById,
  getChannelSkillServiceContext,
  searchChannelSkills,
} from './channelSkillsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
jest.mock('@/shared/auth/config', () => ({
  getNyxIDRuntimeConfig: () => ({
    baseUrl: 'https://nyx.example.test',
    enabled: true,
  }),
}));
const fetchMock = jest.mocked(authFetch);

it('resolves the selected name and reads its exact package with authenticated, abortable requests', async () => {
  fetchMock.mockReset();
  const session = createNyxIDServiceSession();
  persistAuthSession(session);
  fetchMock.mockResolvedValueOnce({
    ok: true,
    json: async () => ({
      data: {
        guid: 'guid+one',
        name: 'team+docs',
        description: 'Documentation',
        nyxidServiceSlug: 'api-github',
        nyxidServiceId: 'catalog-id',
      },
    }),
  } as Response);
  fetchMock.mockResolvedValueOnce({
    ok: true,
    json: async () => ({
      data: {
        name: 'team+docs',
        files: { 'SKILL.md': 'Use GitHub.', 'private.txt': 'TEST_ONLY_SECRET' },
      },
    }),
  } as Response);
  const signal = new AbortController().signal;
  expect(await getChannelSkillServiceContext('team+docs', signal)).toEqual({
    name: 'team+docs',
    description: 'Documentation',
    instructions: 'Use GitHub.',
    linkedServiceSlug: 'api-github',
  });
  expect(
    fetchMock.mock.calls.map(([url]) => new URL(String(url)).pathname),
  ).toEqual([
    '/api/v1/proxy/s/ornn-api/api/v1/skills/team%2Bdocs',
    '/api/v1/proxy/s/ornn-api/api/v1/skills/guid%2Bone/json',
  ]);
  for (const [, init] of fetchMock.mock.calls) {
    expect(init).toMatchObject({
      credentials: 'omit',
      cache: 'no-store',
      headers: { Authorization: `Bearer ${session.tokens.accessToken}` },
    });
    expect(init?.signal).toBe(signal);
  }
});

it('rejects another skill package instead of deriving recommendations from mismatched content', async () => {
  fetchMock.mockReset();
  persistAuthSession(createNyxIDServiceSession());
  fetchMock.mockResolvedValueOnce({
    ok: true,
    json: async () => ({
      data: {
        guid: 'skill-guid',
        name: 'support',
      },
    }),
  } as Response);
  fetchMock.mockResolvedValueOnce({
    ok: true,
    json: async () => ({
      data: {
        name: 'another-skill',
        files: { 'SKILL.md': 'TEST_ONLY_SECRET' },
      },
    }),
  } as Response);
  await expect(getChannelSkillServiceContext('support')).rejects.toThrow(
    'Skill identity changed.',
  );
});
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
  await expect(searchChannelSkills('')).rejects.toThrow(
    'Could not load skill data.',
  );
});

it('reads a skill by its encoded stable ID with the active user credentials and an abort signal', async () => {
  fetchMock.mockReset();
  const session = createNyxIDServiceSession();
  persistAuthSession(session);
  const id = 'opaque+id&version=2';
  fetchMock.mockResolvedValue({
    ok: true,
    json: async () => ({
      data: { guid: id, name: 'current-name', description: 'Details' },
      error: null,
    }),
  } as Response);
  const signal = new AbortController().signal;
  expect(await getChannelSkillById(id, signal)).toEqual({
    id,
    name: 'current-name',
    description: 'Details',
  });
  const [url, init] = fetchMock.mock.calls[0];
  expect(String(url)).toBe(
    'https://nyx.example.test/api/v1/proxy/s/ornn-api/api/v1/skills/opaque%2Bid%26version%3D2',
  );
  expect(init).toMatchObject({
    credentials: 'omit',
    cache: 'no-store',
    headers: { Authorization: `Bearer ${session.tokens.accessToken}` },
  });
  expect(init?.signal).toBe(signal);
});

it('rejects name fallback and unsafe path segments instead of treating them as skill IDs', async () => {
  fetchMock.mockReset();
  persistAuthSession(createNyxIDServiceSession());
  fetchMock.mockResolvedValue({
    ok: true,
    json: async () => ({
      data: { guid: 'actual-guid', name: 'booking-capacity' },
      error: null,
    }),
  } as Response);
  await expect(getChannelSkillById('booking-capacity')).rejects.toThrow(
    'Invalid skill identity.',
  );
  fetchMock.mockClear();
  await expect(getChannelSkillById('..')).rejects.toThrow('Invalid skill ID.');
  expect(fetchMock).not.toHaveBeenCalled();
});
