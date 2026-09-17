import { authFetch } from '@/shared/auth/fetch';
import {
  expectArray,
  expectBoolean,
  expectRecord,
  readOptionalString,
  readString,
} from './http/decoders';

export type ChannelServiceAuthorization =
  | { readonly kind: 'explicit'; readonly serviceIds: readonly string[] }
  | { readonly kind: 'nyxidDefault' }
  | { readonly kind: 'unavailable' };

export interface ChannelRegistration {
  readonly id: string | null;
  readonly botId: string;
  readonly label: string | null;
  readonly platform: string;
  readonly bindingStatus: 'bound' | 'unbound';
  readonly availabilityStatus: string | null;
  readonly nyxStatus: string | null;
  readonly providerSlug: string | null;
  readonly agentKeyId: string | null;
  readonly skill: {
    readonly name: string;
    readonly version: string | null;
  } | null;
  readonly workflowDeliveryStatus: string | null;
  readonly stateVersion: number | null;
  readonly owned: boolean;
  readonly serviceAuthorization: ChannelServiceAuthorization;
}

export interface ChannelConfiguration {
  readonly skillName: string;
  readonly authorizationMode: 'nyxid_default' | 'explicit_service_allowlist';
  readonly serviceIds: readonly string[];
}

export interface ChannelReceipt {
  readonly registrationId: string;
  readonly commandId: string;
}

export class ChannelApiError extends Error {
  constructor(readonly status: number) {
    // Never retain provisioning diagnostics or credential material.
    super(`Channel request failed (${status}).`);
    this.name = 'ChannelApiError';
  }
}

export type ChannelRegistrationFailure =
  | 'services'
  | 'skill'
  | 'authorization'
  | 'conflict'
  | 'configuration'
  | 'rejected'
  | 'uncertain';
export class ChannelRegistrationError extends ChannelApiError {
  constructor(
    status: number,
    readonly reason: ChannelRegistrationFailure,
  ) {
    super(status);
    this.name = 'ChannelRegistrationError';
  }
}

export function normalizeChannelSkill(value: string): string {
  return value.trim().replace(/^\//, '').toLowerCase();
}

function decodeRegistration(value: unknown): ChannelRegistration {
  const row = expectRecord(value, 'Channel registration');
  const id = readOptionalString(row, 'id', 'Registration ID') || null;
  const botId = readString(row, 'nyx_channel_bot_id', 'Bot ID');
  const bindingStatus = row.binding_status;
  if (
    !botId.trim() ||
    !['bound', 'unbound'].includes(String(bindingStatus)) ||
    (bindingStatus === 'bound' && !id?.trim())
  )
    throw new Error('Invalid channel binding identity.');
  const skillName = readOptionalString(row, 'skill_name', 'Skill name')?.trim();
  const key =
    row.agent_key == null ? null : expectRecord(row.agent_key, 'Agent key');
  const version = row.state_version;
  if (
    version != null &&
    (typeof version !== 'number' ||
      !Number.isSafeInteger(version) ||
      version < 0)
  )
    throw new Error('Invalid channel state version.');
  return {
    id,
    botId,
    platform: readString(row, 'platform', 'Channel platform'),
    label: readOptionalString(row, 'label', 'Channel label')?.trim() || null,
    bindingStatus: bindingStatus === 'bound' ? 'bound' : 'unbound',
    availabilityStatus:
      readOptionalString(row, 'availability_status', 'Availability') || null,
    nyxStatus: readOptionalString(row, 'nyx_status', 'NyxID status') || null,
    providerSlug:
      readOptionalString(row, 'nyx_provider_slug', 'Provider') || null,
    agentKeyId:
      (key
        ? readOptionalString(key, 'api_key_id', 'Agent key ID')
        : readOptionalString(row, 'nyx_agent_api_key_id', 'Agent key ID')) ||
      null,
    skill: skillName ? { name: skillName, version: null } : null,
    workflowDeliveryStatus:
      readOptionalString(
        row,
        'workflow_result_delivery_status',
        'Workflow delivery status',
      ) || null,
    stateVersion: typeof version === 'number' ? version : null,
    owned: expectBoolean(row.owned, 'Channel ownership'),
    serviceAuthorization: decodeAuthorization(row),
  };
}

function decodeAuthorization(
  row: Record<string, unknown>,
): ChannelServiceAuthorization {
  if (row.authorization_mode === 'nyxid_default')
    return { kind: 'nyxidDefault' };
  if (
    row.authorization_mode !== 'explicit_service_allowlist' ||
    row.service_ids == null
  )
    return { kind: 'unavailable' };
  const serviceIds = expectArray(row.service_ids, 'Service IDs', (id) => {
    if (typeof id !== 'string' || !id.trim())
      throw new Error('Invalid service identity.');
    return id;
  });
  if (new Set(serviceIds).size !== serviceIds.length)
    throw new Error('Duplicate service identity.');
  return { kind: 'explicit', serviceIds };
}

export function channelConfiguration(
  row: ChannelRegistration,
): ChannelConfiguration | null {
  if (row.serviceAuthorization.kind === 'unavailable') return null;
  return {
    skillName: row.skill?.name ?? '',
    authorizationMode:
      row.serviceAuthorization.kind === 'explicit'
        ? 'explicit_service_allowlist'
        : 'nyxid_default',
    serviceIds:
      row.serviceAuthorization.kind === 'explicit'
        ? row.serviceAuthorization.serviceIds
        : [],
  };
}

export function channelConfigPayload(input: ChannelConfiguration) {
  return {
    skill_name: normalizeChannelSkill(input.skillName),
    authorization_mode: input.authorizationMode,
    service_ids:
      input.authorizationMode === 'nyxid_default'
        ? []
        : [...input.serviceIds].sort(),
  };
}

export function channelConfigMatches(
  actual: ChannelRegistration,
  expected: ChannelConfiguration,
): boolean {
  const config = channelConfiguration(actual);
  return (
    config !== null &&
    JSON.stringify(channelConfigPayload(config)) ===
      JSON.stringify(channelConfigPayload(expected))
  );
}

async function request<T>(
  path: string,
  decode: (value: unknown) => T,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(path, {
    ...init,
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) throw new ChannelApiError(response.status);
  return decode(await response.json());
}

export function registrationPath(registrationId: string): string {
  if (!registrationId.trim())
    throw new Error('Missing channel registration ID.');
  return `/api/channels/registrations/${encodeURIComponent(registrationId)}`;
}

async function submit(
  path: string,
  body: object,
  registrationId?: string,
): Promise<ChannelReceipt> {
  let response: Response;
  try {
    response = await authFetch(path, {
      method: 'POST',
      headers: {
        Accept: 'application/json',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(body),
    });
  } catch {
    throw new ChannelRegistrationError(0, 'uncertain');
  }
  const value: unknown = await response.json().catch(() => null);
  if (!response.ok) {
    const code =
      value && typeof value === 'object' && 'error' in value
        ? value.error
        : null;
    const reason =
      code === 'invalid_service_ids' ||
      code === 'service_owner_forbidden' ||
      code === 'channel_authorization_contract_invalid'
        ? 'services'
        : code === 'invalid_runtime_config' || code === 'skill_not_found'
          ? 'skill'
          : [401, 403].includes(response.status)
            ? 'authorization'
            : response.status === 409
              ? 'conflict'
              : code === 'insecure_webhook_base_url'
                ? 'configuration'
                : response.status >= 500
                  ? 'uncertain'
                  : 'rejected';
    throw new ChannelRegistrationError(response.status, reason);
  }
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new ChannelRegistrationError(response.status, 'uncertain');
  const receipt = expectRecord(value, 'Channel receipt');
  if (
    receipt.status !== 'accepted' ||
    typeof receipt.registration_id !== 'string' ||
    !receipt.registration_id.trim() ||
    typeof receipt.command_id !== 'string' ||
    !receipt.command_id.trim() ||
    (registrationId && receipt.registration_id !== registrationId)
  )
    throw new ChannelRegistrationError(response.status, 'uncertain');
  return {
    registrationId: receipt.registration_id,
    commandId: receipt.command_id,
  };
}

export const channelsApi = {
  adopt(botId: string, input: ChannelConfiguration): Promise<ChannelReceipt> {
    if (!botId.trim()) throw new Error('Missing bot identity.');
    return submit('/api/channels/registrations', {
      nyx_channel_bot_id: botId,
      ...channelConfigPayload(input),
    });
  },
  update(
    registrationId: string,
    input: ChannelConfiguration,
  ): Promise<ChannelReceipt> {
    return submit(
      registrationPath(registrationId),
      channelConfigPayload(input),
      registrationId,
    );
  },
  list(signal?: AbortSignal): Promise<ChannelRegistration[]> {
    return request(
      '/api/channels/registrations',
      (value) => {
        const rows = expectArray(
          value,
          'Channel registrations',
          decodeRegistration,
        ).filter((row) => row.owned);
        if (new Set(rows.map((row) => row.botId)).size !== rows.length)
          throw new Error('Ambiguous channel bot inventory.');
        return rows;
      },
      { signal },
    );
  },
  get(
    registrationId: string,
    signal?: AbortSignal,
  ): Promise<ChannelRegistration> {
    return request(
      registrationPath(registrationId),
      (value) => {
        const row = decodeRegistration(value);
        if (
          row.id !== registrationId ||
          !row.owned ||
          row.bindingStatus !== 'bound'
        )
          throw new ChannelApiError(404);
        if (row.stateVersion === null)
          throw new Error('Missing channel detail state version.');
        return row;
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
