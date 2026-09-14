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
