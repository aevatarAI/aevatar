import { authFetch } from '@/shared/auth/fetch';
import { ChannelContractUnavailableError, channelsApi } from './channelsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;
const bound = {
  id: 'reg:one/a',
  nyx_channel_bot_id: 'bot-one',
  platform: 'discord',
  label: 'Support',
  binding_status: 'bound',
  availability_status: 'available',
  nyx_status: 'active',
  owned: true,
  skill_name: 'support',
  authorization_mode: 'explicit_service_allowlist',
  service_ids: ['us-work'],
  state_version: 12,
};
afterEach(() => fetchMock.mockReset());

it('reads all visible bound and unbound bots with backend owner labels and only safe fields', async () => {
  fetchMock.mockResolvedValue(
    response([
      {
        ...bound,
        nyx_channel_bot_owner_scope_id: 'scope-personal',
        nyx_channel_bot_owner_scope_name: 'personal',
        access_token: 'TEST_SECRET',
        webhook_url: 'TEST_SECRET',
        runtime_config: { secret: 'TEST_SECRET' },
      },
      {
        ...bound,
        id: null,
        nyx_channel_bot_id: 'bot-two',
        nyx_channel_bot_owner_scope_id: 'scope-org',
        nyx_channel_bot_owner_scope_name: ' Aevatar Team ',
        binding_status: 'unbound',
        authorization_mode: null,
        skill_name: '',
      },
      {
        ...bound,
        id: 'reg-other',
        nyx_channel_bot_id: 'bot-other',
        nyx_channel_bot_owner_scope_id: 'scope-unresolved',
        nyx_channel_bot_owner_scope_name: null,
        owned: false,
      },
    ]),
  );
  const rows = await channelsApi.list();
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock).toHaveBeenCalledWith(
    '/api/channels/registrations?scope=all',
    expect.objectContaining({ signal: undefined }),
  );
  expect(rows).toHaveLength(3);
  expect(rows[0]).toMatchObject({
    id: 'reg:one/a',
    botId: 'bot-one',
    botOwnerScopeId: 'scope-personal',
    botOwnerScopeName: 'personal',
    label: 'Support',
    skill: { name: 'support', version: null },
    stateVersion: 12,
  });
  expect(rows[1]).toMatchObject({
    id: null,
    botOwnerScopeId: 'scope-org',
    botOwnerScopeName: 'Aevatar Team',
    owned: true,
    bindingStatus: 'unbound',
    skill: null,
    serviceAuthorization: { kind: 'unavailable' },
  });
  expect(rows[2]).toMatchObject({
    botId: 'bot-other',
    botOwnerScopeId: 'scope-unresolved',
    botOwnerScopeName: null,
    owned: false,
  });
  expect(JSON.stringify(rows)).not.toMatch(
    /TEST_SECRET|webhook|runtime_config/,
  );
  fetchMock.mockResolvedValue(response([{ ...bound, id: null }]));
  await expect(channelsApi.list()).rejects.toThrow();
  fetchMock.mockResolvedValue(response({ registrations: [] }));
  await expect(channelsApi.list()).rejects.toThrow();
  fetchMock.mockResolvedValue(
    response({ error: 'nyxid_channel_bots_unavailable' }, 502),
  );
  await expect(channelsApi.list()).rejects.toMatchObject({ status: 502 });
  fetchMock.mockResolvedValue(
    response([
      { ...bound, binding_status: undefined, default_skill_name: 'support' },
    ]),
  );
  await expect(channelsApi.list()).rejects.toBeInstanceOf(
    ChannelContractUnavailableError,
  );
});

it('adopts and updates through the narrow contract and keeps admission separate from completion', async () => {
  fetchMock.mockResolvedValue(
    response(
      {
        status: 'accepted',
        registration_id: bound.id,
        command_id: 'command-one',
        relay_callback_url: 'TEST_SECRET',
        nyx_agent_api_key: 'TEST_SECRET',
      },
      202,
    ),
  );
  const config = {
    skillName: ' //Support ',
    authorizationMode: 'explicit_service_allowlist' as const,
    serviceIds: ['us-work'],
  };
  expect(await channelsApi.adopt('bot-one', config)).toEqual({
    registrationId: bound.id,
    commandId: 'command-one',
  });
  expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({
    nyx_channel_bot_id: 'bot-one',
    skill_name: 'support',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['us-work'],
  });
  await channelsApi.update(bound.id, config);
  expect(fetchMock.mock.calls[1][0]).toBe(
    '/api/channels/registrations/reg%3Aone%2Fa',
  );
  expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({
    skill_name: 'support',
    authorization_mode: 'explicit_service_allowlist',
    service_ids: ['us-work'],
  });
  fetchMock.mockResolvedValue(
    response(
      { error: 'ambiguous_channel_bot_route', secret: 'TEST_SECRET' },
      409,
    ),
  );
  await expect(channelsApi.adopt('bot-one', config)).rejects.toMatchObject({
    reason: 'conflict',
  });
  fetchMock.mockRejectedValue(new Error('TEST_SECRET'));
  await expect(channelsApi.adopt('bot-one', config)).rejects.toMatchObject({
    reason: 'uncertain',
  });
});

it('requires exact owned detail identity and strips removal diagnostics', async () => {
  fetchMock.mockResolvedValue(response(bound));
  expect(await channelsApi.get(bound.id)).toMatchObject({
    id: bound.id,
    stateVersion: 12,
  });
  fetchMock.mockResolvedValue(response({ ...bound, id: 'reg-other' }));
  await expect(channelsApi.get(bound.id)).rejects.toMatchObject({
    status: 404,
  });
  fetchMock.mockResolvedValue(response({ ...bound, owned: false }));
  await expect(channelsApi.get(bound.id)).rejects.toMatchObject({
    status: 404,
  });
  fetchMock.mockResolvedValue(
    response({ status: 'deleted', warnings: ['TEST_SECRET'] }),
  );
  expect(await channelsApi.remove(bound.id)).toEqual({ hasWarnings: true });
  expect(fetchMock.mock.calls.at(-1)).toEqual([
    '/api/channels/registrations/reg%3Aone%2Fa',
    expect.objectContaining({ method: 'DELETE' }),
  ]);
});
