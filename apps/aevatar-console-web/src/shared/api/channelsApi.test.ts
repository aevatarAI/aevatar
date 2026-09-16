import { authFetch } from '@/shared/auth/fetch';
import { ChannelApiError, channelsApi } from './channelsApi';

jest.mock('@/shared/auth/fetch', () => ({ authFetch: jest.fn() }));
const fetchMock = jest.mocked(authFetch);
const response = (value: unknown, status = 200) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => value,
  }) as Response;

const registration = {
  id: 'registration-alpha',
  platform: 'telegram',
  scope_id: 'scope-alpha',
  owned: true,
  nyx_channel_bot_id: 'bot-alpha',
  nyx_provider_slug: 'telegram-provider',
  nyx_agent_api_key_id: 'key-alpha',
  workflow_result_delivery_status: 'enabled',
};

afterEach(() => fetchMock.mockReset());

describe('channel API boundary', () => {
  it('reads the owner list and keeps only safe display fields with the exact typed skill', async () => {
    fetchMock.mockResolvedValue(
      response([
        {
          ...registration,
          authorization_mode: 'explicit_service_allowlist',
          service_ids: ['us-work', 'us-model'],
          default_skill: { name: 'review.skill', version: '2.4' },
          agent_key: { api_key_id: 'key-current', raw_key: 'TEST_ONLY_SECRET' },
          runtime_config: { instructions: 'private instructions' },
          access_token: 'TEST_ONLY_SECRET',
          webhook_url: 'https://example.invalid/private',
        },
        { ...registration, id: 'registration-other-owner', owned: false },
      ]),
    );
    const result = await channelsApi.list();
    expect(fetchMock.mock.calls[0][0]).toBe('/api/channels/registrations');
    expect(result).toEqual([
      {
        id: 'registration-alpha',
        platform: 'telegram',
        scopeId: 'scope-alpha',
        botId: 'bot-alpha',
        providerSlug: 'telegram-provider',
        agentKeyId: 'key-current',
        skill: { name: 'review.skill', version: '2.4' },
        workflowDeliveryStatus: 'enabled',
        owned: true,
        serviceAuthorization: {
          kind: 'explicit',
          serviceIds: ['us-work', 'us-model'],
        },
      },
    ]);
    expect(JSON.stringify(result)).not.toMatch(
      /TEST_ONLY_SECRET|instructions|webhook/,
    );
  });

  it('accepts the deployed name-only contract without inventing a version', async () => {
    fetchMock.mockResolvedValue(
      response([
        { ...registration, default_skill_name: 'legacy-skill' },
        {
          ...registration,
          id: 'registration-unset',
          default_skill: { name: '', version: '7' },
        },
      ]),
    );
    expect((await channelsApi.list()).map((row) => row.skill)).toEqual([
      { name: 'legacy-skill', version: null },
      null,
    ]);
  });

  it('rejects malformed list responses instead of presenting an empty collection', async () => {
    fetchMock.mockResolvedValue(response({ registrations: [] }));
    await expect(channelsApi.list()).rejects.toThrow();
  });

  it('distinguishes explicit empty, NyxID default, missing and unknown service authorization', async () => {
    fetchMock.mockResolvedValue(
      response([
        {
          ...registration,
          authorization_mode: 'explicit_service_allowlist',
          service_ids: [],
        },
        { ...registration, authorization_mode: 'nyxid_default' },
        { ...registration, authorization_mode: 'explicit_service_allowlist' },
        {
          ...registration,
          authorization_mode: 'future_mode',
          service_ids: ['us-work'],
        },
      ]),
    );
    expect(
      (await channelsApi.list()).map((row) => row.serviceAuthorization),
    ).toEqual([
      { kind: 'explicit', serviceIds: [] },
      { kind: 'nyxidDefault' },
      { kind: 'unavailable' },
      { kind: 'unavailable' },
    ]);
  });

  it('rejects malformed saved service IDs rather than inventing an authorization list', async () => {
    fetchMock.mockResolvedValue(
      response([
        {
          ...registration,
          authorization_mode: 'explicit_service_allowlist',
          service_ids: ['us-work', null],
        },
      ]),
    );
    await expect(channelsApi.list()).rejects.toThrow(
      'Invalid authorized service identity.',
    );
  });

  it('encodes opaque registration IDs and rejects a status for a different registration', async () => {
    const id = 'registration:alpha/one + two';
    fetchMock.mockResolvedValue(
      response({
        registration_id: id,
        status: 'new_backend_state',
        workflow_result_delivery_status: 'enabled',
      }),
    );
    expect(await channelsApi.status(id)).toEqual({
      registrationId: id,
      status: 'new_backend_state',
      workflowDeliveryStatus: 'enabled',
    });
    expect(fetchMock.mock.calls[0][0]).toBe(
      '/api/channels/registrations/registration%3Aalpha%2Fone%20%2B%20two/status',
    );
    fetchMock.mockResolvedValue(
      response({ registration_id: 'registration-other', status: 'active' }),
    );
    await expect(channelsApi.status(id)).rejects.toThrow('identity mismatch');
  });

  it('uses the registration identity for deletion and discards cleanup diagnostics', async () => {
    fetchMock.mockResolvedValue(
      response({ status: 'deleted', warnings: ['TEST_ONLY_SECRET'] }),
    );
    await expect(channelsApi.remove('registration:alpha/one')).resolves.toEqual(
      { hasWarnings: true },
    );
    expect(fetchMock).toHaveBeenCalledWith(
      '/api/channels/registrations/registration%3Aalpha%2Fone',
      expect.objectContaining({ method: 'DELETE' }),
    );
    fetchMock.mockResolvedValue(response({ status: 'accepted' }, 202));
    await expect(channelsApi.remove('registration-alpha')).rejects.toThrow(
      'not confirmed',
    );
  });

  it('preserves HTTP failure classification without retaining backend secret material', async () => {
    const json = jest.fn().mockResolvedValue({ message: 'TEST_ONLY_SECRET' });
    fetchMock.mockResolvedValue({
      ok: false,
      status: 404,
      json,
    } as unknown as Response);
    await expect(channelsApi.status('registration-alpha')).rejects.toEqual(
      new ChannelApiError(404),
    );
    expect(json).not.toHaveBeenCalled();
  });
});
