import { authFetch } from '@/shared/auth/fetch';
import { channelRuntimeConfigFixture } from '../../../tests/fixtures/channelRuntimeConfig';
import { channelRuntimeConfigApi } from './channelRuntimeConfigApi';
import { ChannelApiError } from './channelsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown) =>
  ({ ok: true, status: 200, json: async () => value }) as Response;

it('reads the exact identity and legacy skill while discarding unexpected credential fields', async () => {
  fetchMock.mockResolvedValue(
    response({
      ...channelRuntimeConfigFixture,
      registration_id: 'registration/a',
      runtime_config: {
        ...channelRuntimeConfigFixture.runtime_config,
        default_skill: { name: '', version: '' },
        access_token: 'TEST_ONLY_SECRET',
      },
      agent_key: { raw_key: 'TEST_ONLY_SECRET' },
      webhook_url: 'TEST_ONLY_SECRET',
    }),
  );
  const detail = await channelRuntimeConfigApi.get(
    'registration/a',
    'scope-alpha',
  );
  expect(fetchMock.mock.calls[0][0]).toBe(
    '/api/channels/registrations/registration%2Fa/runtime-config',
  );
  expect(detail.runtimeConfig.defaultSkill).toEqual({
    name: 'team-helper',
    version: '2.1',
  });
  expect(JSON.stringify(detail)).not.toContain('TEST_ONLY_SECRET');
  fetchMock.mockResolvedValue(
    response({ ...channelRuntimeConfigFixture, scope_id: 'scope-other' }),
  );
  await expect(
    channelRuntimeConfigApi.get('registration-alpha', 'scope-alpha'),
  ).rejects.toEqual(new ChannelApiError(404));
});

it('rejects an unrecognized config or receipt instead of replacing it with defaults', async () => {
  fetchMock.mockResolvedValue(response(channelRuntimeConfigFixture));
  const detail = await channelRuntimeConfigApi.get(
    'registration-alpha',
    'scope-alpha',
  );
  fetchMock.mockResolvedValue(
    response({
      ...channelRuntimeConfigFixture,
      runtime_config: {
        ...channelRuntimeConfigFixture.runtime_config,
        credential_source_mode: 'future_mode',
      },
    }),
  );
  await expect(
    channelRuntimeConfigApi.get('registration-alpha', 'scope-alpha'),
  ).rejects.toThrow('Unsupported channel credential source.');
  fetchMock.mockResolvedValue(
    response({
      status: 'accepted',
      registration_id: 'registration-other',
      command_id: 'command-alpha',
    }),
  );
  await expect(
    channelRuntimeConfigApi.update('registration-alpha', detail),
  ).rejects.toThrow('not acknowledged');
});
