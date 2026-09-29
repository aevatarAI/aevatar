import { authFetch } from '@/shared/auth/fetch';
import { getChannelSkillServices } from './channelSkillServicesApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({ ok: status === 200, status, json: async () => value }) as Response;

afterEach(() => fetchMock.mockReset());

it('reads the backend contract with an encoded skill name and preserves exact instance identity', async () => {
  fetchMock.mockResolvedValue(
    response({
      skillName: 'team+docs',
      suggestions: [
        {
          slug: 'api-github',
          label: 'GitHub',
          evidence: 'catalog',
          instances: [
            {
              id: 'us-team',
              slug: 'api-github',
              label: 'Team GitHub',
              active: true,
              allowed: false,
              source: 'organization',
              organizationName: 'Acme',
            },
          ],
        },
      ],
    }),
  );
  const signal = new AbortController().signal;
  expect(await getChannelSkillServices('team+docs', signal)).toEqual([
    {
      slug: 'api-github',
      label: 'GitHub',
      evidence: 'catalog',
      instances: [
        {
          id: 'us-team',
          slug: 'api-github',
          label: 'Team GitHub',
          active: true,
          allowed: false,
          source: 'organization',
          organizationName: 'Acme',
        },
      ],
    },
  ]);
  expect(fetchMock).toHaveBeenCalledWith(
    '/api/skills/service-recommendations?skillName=team%2Bdocs',
    {
      signal,
      cache: 'no-store',
    },
  );
});

it('rejects mismatched and unsupported results and never surfaces upstream error contents', async () => {
  fetchMock.mockResolvedValueOnce(
    response({ skillName: 'another-skill', suggestions: [] }),
  );
  await expect(getChannelSkillServices('support')).rejects.toThrow(
    'Mismatched skill recommendations.',
  );
  fetchMock.mockResolvedValueOnce(
    response({
      skillName: 'support',
      suggestions: [
        {
          slug: 'github',
          label: 'GitHub',
          evidence: 'new-unknown-kind',
          instances: [],
        },
      ],
    }),
  );
  await expect(getChannelSkillServices('support')).rejects.toThrow(
    'Invalid service suggestion.',
  );
  fetchMock.mockResolvedValueOnce(response({ error: 'TEST_ONLY_SECRET' }, 502));
  await expect(getChannelSkillServices('support')).rejects.toThrow(
    'Channel request failed (502).',
  );
});
