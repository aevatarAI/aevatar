import { authFetch } from '@/shared/auth/fetch';
import {
  expectArray,
  expectBoolean,
  expectRecord,
  readOptionalString,
  readString,
} from './http/decoders';

export interface ChannelRegistration {
  readonly id: string;
  readonly platform: string;
  readonly scopeId: string;
  readonly botId: string | null;
  readonly providerSlug: string | null;
  readonly agentKeyId: string | null;
  readonly skill: {
    readonly name: string;
    readonly version: string | null;
  } | null;
  readonly workflowDeliveryStatus: string | null;
  readonly owned: boolean;
}

export interface ChannelStatus {
  readonly registrationId: string;
  readonly status: string;
  readonly workflowDeliveryStatus: string | null;
}

export class ChannelApiError extends Error {
  constructor(readonly status: number) {
    // Backend error bodies can contain provisioning diagnostics. Keep them out
    // of the query cache and use localized recovery copy in the page.
    super(`Channel request failed (${status}).`);
    this.name = 'ChannelApiError';
  }
}

export type ChannelRegistrationFailure =
  | 'token'
  | 'services'
  | 'skill'
  | 'authorization'
  | 'conflict'
  | 'configuration'
  | 'rejected'
  | 'uncertain';

export class ChannelRegistrationError extends Error {
  constructor(readonly reason: ChannelRegistrationFailure) {
    super('Could not connect Telegram.');
    this.name = 'ChannelRegistrationError';
  }
}

export interface TelegramRegistrationInput {
  readonly botToken: string;
  readonly label: string;
  readonly skillName: string;
  readonly serviceIds: readonly string[];
  readonly webhookBaseUrl: string;
}

function registrationFailure(status: number, value: unknown) {
  const code =
    value && typeof value === 'object' && 'error' in value
      ? value.error
      : undefined;
  if (code === 'missing_bot_token' || code === 'invalid_bot_token')
    return 'token';
  if (code === 'invalid_service_ids') return 'services';
  if (code === 'invalid_default_skill' || code === 'skill_not_found')
    return 'skill';
  if (status === 401 || status === 403) return 'authorization';
  if (status === 409) return 'conflict';
  if (
    code === 'missing_webhook_base_url' ||
    code === 'insecure_webhook_base_url' ||
    code === 'nyx_base_url_not_configured'
  )
    return 'configuration';
  return status >= 500 ? 'uncertain' : 'rejected';
}

function decodeRegistration(value: unknown): ChannelRegistration {
  const row = expectRecord(value, 'Channel registration');
  const skill =
    row.default_skill == null
      ? null
      : expectRecord(row.default_skill, 'Channel default skill');
  const key =
    row.agent_key == null
      ? null
      : expectRecord(row.agent_key, 'Channel agent key');
  const skillName = skill
    ? readOptionalString(skill, 'name', 'Skill name')?.trim()
    : readOptionalString(row, 'default_skill_name', 'Skill name')?.trim();
  const id = readString(row, 'id', 'Registration ID');
  if (!id.trim()) throw new Error('Missing channel registration ID.');
  // Explicitly project safe display fields. Never retain the transport object,
  // webhook URL, runtime configuration, or unexpected credential fields.
  return {
    id,
    platform: readString(row, 'platform', 'Channel platform'),
    scopeId: readString(row, 'scope_id', 'Channel scope'),
    botId: readOptionalString(row, 'nyx_channel_bot_id', 'Bot ID') || null,
    providerSlug:
      readOptionalString(row, 'nyx_provider_slug', 'Provider') || null,
    agentKeyId:
      (key
        ? readOptionalString(key, 'api_key_id', 'Agent key ID')
        : readOptionalString(row, 'nyx_agent_api_key_id', 'Agent key ID')) ||
      null,
    skill: skillName
      ? {
          name: skillName,
          version: skill
            ? readOptionalString(skill, 'version', 'Skill version')?.trim() ||
              null
            : null,
        }
      : null,
    workflowDeliveryStatus:
      readOptionalString(
        row,
        'workflow_result_delivery_status',
        'Workflow delivery status',
      ) || null,
    owned: expectBoolean(row.owned, 'Channel ownership'),
  };
}

async function request<T>(
  path: string,
  decode: (value: unknown) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(path, {
    ...init,
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  return decode(await response.json());
}

function registrationPath(registrationId: string): string {
  if (!registrationId.trim())
    throw new Error('Missing channel registration ID.');
  return `/api/channels/registrations/${encodeURIComponent(registrationId)}`;
}

export const channelsApi = {
  async registerTelegram(
    input: TelegramRegistrationInput,
  ): Promise<{ readonly registrationId: string }> {
    // This request carries the token only in the authenticated POST body.
    // Do not use a mutation cache, navigation state, or persistent draft.
    let response: Response;
    try {
      response = await authFetch('/api/channels/registrations', {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          platform: 'telegram',
          bot_token: input.botToken.trim(),
          label: input.label.trim(),
          default_skill_name: input.skillName.trim(),
          webhook_base_url: input.webhookBaseUrl,
          authorization_mode: 'explicit_service_allowlist',
          service_ids: [...input.serviceIds],
        }),
      });
    } catch {
      throw new ChannelRegistrationError('uncertain');
    }
    const body: unknown = await response.json().catch(() => null);
    if (!response.ok)
      throw new ChannelRegistrationError(
        registrationFailure(response.status, body),
      );
    if (
      !body ||
      typeof body !== 'object' ||
      !('status' in body) ||
      body.status !== 'accepted' ||
      !('registration_id' in body) ||
      typeof body.registration_id !== 'string' ||
      !body.registration_id.trim()
    )
      throw new ChannelRegistrationError('uncertain');
    // Retain the identity needed for readback, not raw provisioning output.
    return { registrationId: body.registration_id };
  },
  list(signal?: AbortSignal): Promise<ChannelRegistration[]> {
    return request(
      '/api/channels/registrations',
      (value) =>
        expectArray(value, 'Channel registrations', decodeRegistration).filter(
          (row) => row.owned,
        ),
      { signal },
    );
  },
  status(registrationId: string, signal?: AbortSignal): Promise<ChannelStatus> {
    return request(
      `${registrationPath(registrationId)}/status`,
      (value) => {
        const row = expectRecord(value, 'Channel status');
        const returnedId = readString(
          row,
          'registration_id',
          'Registration ID',
        );
        if (returnedId !== registrationId)
          throw new Error('Channel status identity mismatch.');
        return {
          registrationId: returnedId,
          status: readString(row, 'status', 'Inbound status'),
          workflowDeliveryStatus:
            readOptionalString(
              row,
              'workflow_result_delivery_status',
              'Workflow delivery status',
            ) || null,
        };
      },
      { signal },
    );
  },
  async remove(
    registrationId: string,
  ): Promise<{ readonly hasWarnings: boolean }> {
    return request(
      registrationPath(registrationId),
      (value) => {
        const row = expectRecord(value, 'Channel removal');
        if (row.status !== 'deleted')
          throw new Error('Channel removal was not confirmed.');
        return {
          hasWarnings: Array.isArray(row.warnings) && row.warnings.length > 0,
        };
      },
      { method: 'DELETE' },
    );
  },
};
