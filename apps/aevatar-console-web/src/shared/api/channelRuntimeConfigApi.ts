import { authFetch } from '@/shared/auth/fetch';
import { ChannelApiError, registrationPath } from './channelsApi';
import { expectArray, expectRecord, readString } from './http/decoders';

export interface ChannelRuntimeConfig {
  readonly instructions: string;
  readonly defaultSkill: { readonly name: string; readonly version: string };
  readonly toolSetRefs: readonly string[];
  readonly extraToolNames: readonly string[];
  readonly serviceSelectors: readonly {
    readonly serviceSlug: string;
    readonly endpointNames: readonly string[];
  }[];
  readonly credentialSourceMode:
    | 'registration_agent_key'
    | 'sender_binding'
    | 'unspecified';
}

export interface ChannelConfigUpdate {
  readonly authorizationMode: 'nyxid_default' | 'explicit_service_allowlist';
  readonly serviceIds: readonly string[];
  readonly runtimeConfig: ChannelRuntimeConfig;
}

export interface ChannelConfigDetail extends ChannelConfigUpdate {
  readonly registrationId: string;
  readonly scopeId: string;
  readonly platform: string;
  readonly stateVersion: number;
}

export interface ChannelConfigReceipt {
  readonly registrationId: string;
  readonly commandId: string;
}

export type ChannelConfigField =
  | 'skill'
  | 'version'
  | 'instructions'
  | 'services';
export class ChannelConfigError extends ChannelApiError {
  constructor(
    status: number,
    readonly fields: readonly ChannelConfigField[],
  ) {
    super(status);
    this.name = 'ChannelConfigError';
  }
}

function strings(value: unknown, label: string): string[] {
  return expectArray(value, label, (item) => {
    if (typeof item !== 'string') throw new Error(`Invalid ${label}.`);
    return item;
  });
}

function decodeDetail(
  value: unknown,
  registrationId: string,
  scopeId: string,
): ChannelConfigDetail {
  const row = expectRecord(value, 'Channel configuration');
  if (row.registration_id !== registrationId || row.scope_id !== scopeId)
    throw new ChannelApiError(404);
  if (
    row.authorization_mode !== 'nyxid_default' &&
    row.authorization_mode !== 'explicit_service_allowlist'
  )
    throw new Error('Unsupported channel authorization.');
  if (
    typeof row.state_version !== 'number' ||
    !Number.isSafeInteger(row.state_version) ||
    row.state_version < 0
  )
    throw new Error('Invalid channel configuration version.');
  const config = expectRecord(
    row.runtime_config,
    'Channel runtime configuration',
  );
  // The top-level skill includes the backend's legacy registration fallback.
  const skill = expectRecord(
    row.default_skill ?? config.default_skill,
    'Channel skill',
  );
  const mode = config.credential_source_mode;
  if (
    mode !== 'registration_agent_key' &&
    mode !== 'sender_binding' &&
    mode !== 'unspecified'
  )
    throw new Error('Unsupported channel credential source.');
  return {
    registrationId,
    scopeId,
    platform: readString(row, 'platform', 'Channel platform'),
    stateVersion: row.state_version,
    authorizationMode: row.authorization_mode,
    serviceIds:
      row.authorization_mode === 'nyxid_default'
        ? []
        : strings(row.service_ids, 'Service IDs'),
    // Project only the editable contract; never cache the transport object.
    runtimeConfig: {
      instructions: readString(config, 'instructions', 'Bot instructions'),
      defaultSkill: {
        name: readString(skill, 'name', 'Skill name'),
        version: readString(skill, 'version', 'Skill version'),
      },
      toolSetRefs: strings(config.tool_set_refs, 'Tool set references'),
      extraToolNames: strings(config.extra_tool_names, 'Extra tool names'),
      serviceSelectors: expectArray(
        config.nyxid_service_selectors,
        'Service selectors',
        (value) => {
          const selector = expectRecord(value, 'Service selector');
          return {
            serviceSlug: readString(selector, 'service_slug', 'Service slug'),
            endpointNames: strings(selector.endpoint_names, 'Endpoint names'),
          };
        },
      ),
      credentialSourceMode: mode,
    },
  };
}

export function channelConfigPayload(input: ChannelConfigUpdate) {
  const config = input.runtimeConfig;
  return {
    authorization_mode: input.authorizationMode,
    service_ids: [...input.serviceIds].sort(),
    runtime_config: runtimeConfigPayload(config),
  };
}

function runtimeConfigPayload(config: ChannelRuntimeConfig) {
  return {
    instructions: config.instructions,
    default_skill: {
      name: config.defaultSkill.name.trim(),
      version: config.defaultSkill.version,
    },
    tool_set_refs: [...config.toolSetRefs],
    extra_tool_names: [...config.extraToolNames],
    nyxid_service_selectors: config.serviceSelectors.map((selector) => ({
      service_slug: selector.serviceSlug,
      endpoint_names: [...selector.endpointNames],
    })),
    credential_source_mode: config.credentialSourceMode,
  };
}

export function channelConfigMatches(
  actual: ChannelConfigUpdate,
  expected: ChannelConfigUpdate,
): boolean {
  return (
    JSON.stringify(channelConfigPayload(actual)) ===
    JSON.stringify(channelConfigPayload(expected))
  );
}

function decodeFieldErrors(value: unknown): ChannelConfigField[] {
  if (!value || typeof value !== 'object') return [];
  const fields = new Set<ChannelConfigField>();
  if (
    'error' in value &&
    typeof value.error === 'string' &&
    [
      'invalid_service_ids',
      'nyxid_user_service_not_accessible',
      'service_owner_forbidden',
      'channel_authorization_contract_invalid',
    ].includes(value.error)
  )
    fields.add('services');
  if ('field_errors' in value && Array.isArray(value.field_errors)) {
    for (const error of value.field_errors) {
      if (!error || typeof error !== 'object' || !('field' in error)) continue;
      const field = error.field;
      if (field === 'runtime_config.instructions') fields.add('instructions');
      else if (field === 'runtime_config.default_skill.name')
        fields.add('skill');
      else if (field === 'runtime_config.default_skill.version')
        fields.add('version');
      else if (
        typeof field === 'string' &&
        (field === 'service_ids' ||
          field.startsWith('runtime_config.nyxid_service_selectors'))
      )
        fields.add('services');
    }
  }
  return [...fields];
}

export const channelRuntimeConfigApi = {
  async get(
    registrationId: string,
    scopeId: string,
    signal?: AbortSignal,
  ): Promise<ChannelConfigDetail> {
    const response = await authFetch(
      `${registrationPath(registrationId)}/runtime-config`,
      {
        signal,
        cache: 'no-store',
        headers: { Accept: 'application/json' },
      },
    );
    if (!response.ok) throw new ChannelApiError(response.status);
    return decodeDetail(await response.json(), registrationId, scopeId);
  },
  async update(
    registrationId: string,
    input: ChannelRuntimeConfig,
  ): Promise<ChannelConfigReceipt> {
    const response = await authFetch(
      `${registrationPath(registrationId)}/runtime-config`,
      {
        method: 'POST',
        headers: {
          Accept: 'application/json',
          'Content-Type': 'application/json',
        },
        // Omitting service selection preserves the current backend authorization.
        body: JSON.stringify({ runtime_config: runtimeConfigPayload(input) }),
      },
    );
    const value: unknown = await response.json().catch(() => null);
    if (!response.ok)
      throw new ChannelConfigError(response.status, decodeFieldErrors(value));
    const receipt = expectRecord(value, 'Channel configuration update');
    if (
      receipt.status !== 'accepted' ||
      receipt.registration_id !== registrationId
    )
      throw new Error('Channel configuration update was not acknowledged.');
    const commandId = readString(receipt, 'command_id', 'Channel command');
    if (!commandId.trim()) throw new Error('Missing channel command.');
    return { registrationId, commandId };
  },
};
