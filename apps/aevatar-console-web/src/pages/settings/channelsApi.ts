import { authFetch } from "@/shared/auth/fetch";

type JsonRecord = Record<string, unknown>;

export type ChannelAuthorizationMode =
  | "nyxid_default"
  | "explicit_service_allowlist";

export type ChannelRuntimeDefaultSkill = {
  readonly name: string;
  readonly version: string;
};

export type ChannelRuntimeNyxIdServiceSelector = {
  readonly serviceSlug: string;
  readonly endpointNames: readonly string[];
};

export type ChannelRuntimeConfig = {
  readonly instructions: string;
  readonly defaultSkill: ChannelRuntimeDefaultSkill;
  readonly toolSetRefs: readonly string[];
  readonly extraToolNames: readonly string[];
  readonly nyxidServiceSelectors: readonly ChannelRuntimeNyxIdServiceSelector[];
};

export type ChannelRegistrationSummary = {
  readonly id: string;
  readonly platform: string;
  readonly scopeId: string;
  readonly authorizationMode: ChannelAuthorizationMode;
  readonly serviceIds: readonly string[];
  readonly defaultSkill: ChannelRuntimeDefaultSkill;
  readonly hasInstructions: boolean;
  readonly hasToolSetRefs: boolean;
  readonly hasExtraToolNames: boolean;
  readonly nyxidServiceSelectors: readonly ChannelRuntimeNyxIdServiceSelector[];
  readonly agentKey: ChannelAgentKeyStatus;
  readonly workflowResultDeliveryStatus: string;
  readonly stateVersion: number;
  readonly owned: boolean;
};

export type ChannelAgentKeyStatus = {
  readonly apiKeyId: string;
  readonly ready: boolean;
  readonly status: string;
};

export type ChannelRuntimeConfigDetail = {
  readonly registrationId: string;
  readonly platform: string;
  readonly scopeId: string;
  readonly authorizationMode: ChannelAuthorizationMode;
  readonly serviceIds: readonly string[];
  readonly runtimeConfig: ChannelRuntimeConfig;
  readonly agentKey: ChannelAgentKeyStatus;
  readonly workflowResultDeliveryStatus: string;
  readonly stateVersion: number;
};

export type ChannelServiceChoice = {
  readonly id: string;
  readonly slug: string;
  readonly label: string;
  readonly catalogServiceName: string;
  readonly active: boolean;
  readonly credentialSource: {
    readonly kind: string;
    readonly organizationId: string;
    readonly organizationRole: string;
    readonly allowed: boolean;
  };
};

export type ChannelRuntimeConfigSaveInput = {
  readonly registrationId: string;
  readonly authorizationMode: ChannelAuthorizationMode;
  readonly serviceIds: readonly string[];
  readonly runtimeConfig: ChannelRuntimeConfig;
};

export type ChannelRuntimeConfigReceipt = {
  readonly status: string;
  readonly registrationId: string;
  readonly commandId: string;
  readonly correlationId: string;
};

export type ChannelRuntimeConfigFieldError = {
  readonly field: string;
  readonly code: string;
  readonly message: string;
};

export class ChannelSettingsApiError extends Error {
  readonly fieldErrors: readonly ChannelRuntimeConfigFieldError[];
  readonly status: number;

  constructor(
    message: string,
    status: number,
    fieldErrors: readonly ChannelRuntimeConfigFieldError[] = [],
  ) {
    super(message);
    this.name = "ChannelSettingsApiError";
    this.status = status;
    this.fieldErrors = fieldErrors;
  }
}

const jsonHeaders = {
  Accept: "application/json",
  "Content-Type": "application/json",
};

function asRecord(value: unknown): JsonRecord {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonRecord)
    : {};
}

function readString(record: JsonRecord, key: string): string {
  const value = record[key];
  return typeof value === "string" ? value : "";
}

function readBoolean(record: JsonRecord, key: string): boolean {
  return record[key] === true;
}

function readNumber(record: JsonRecord, key: string): number {
  const value = record[key];
  return typeof value === "number" ? value : 0;
}

function readStringArray(record: JsonRecord, key: string): string[] {
  const value = record[key];
  return Array.isArray(value)
    ? value.filter((entry): entry is string => typeof entry === "string")
    : [];
}

function readAuthorizationMode(record: JsonRecord): ChannelAuthorizationMode {
  return record.authorization_mode === "explicit_service_allowlist"
    ? "explicit_service_allowlist"
    : "nyxid_default";
}

function decodeDefaultSkill(value: unknown): ChannelRuntimeDefaultSkill {
  const record = asRecord(value);
  return {
    name: readString(record, "name"),
    version: readString(record, "version"),
  };
}

function decodeSelectors(value: unknown): ChannelRuntimeNyxIdServiceSelector[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.map((entry) => {
    const record = asRecord(entry);
    return {
      serviceSlug: readString(record, "service_slug"),
      endpointNames: readStringArray(record, "endpoint_names"),
    };
  });
}

function decodeAgentKey(value: unknown): ChannelAgentKeyStatus {
  const record = asRecord(value);
  return {
    apiKeyId: readString(record, "api_key_id"),
    ready: readBoolean(record, "ready"),
    status: readString(record, "status") || "missing",
  };
}

function decodeRuntimeConfig(value: unknown): ChannelRuntimeConfig {
  const record = asRecord(value);
  return {
    instructions: readString(record, "instructions"),
    defaultSkill: decodeDefaultSkill(record.default_skill),
    toolSetRefs: readStringArray(record, "tool_set_refs"),
    extraToolNames: readStringArray(record, "extra_tool_names"),
    nyxidServiceSelectors: decodeSelectors(record.nyxid_service_selectors),
  };
}

function decodeRegistrationSummary(value: unknown): ChannelRegistrationSummary {
  const record = asRecord(value);
  return {
    id: readString(record, "id"),
    platform: readString(record, "platform"),
    scopeId: readString(record, "scope_id"),
    authorizationMode: readAuthorizationMode(record),
    serviceIds: readStringArray(record, "service_ids"),
    defaultSkill: decodeDefaultSkill(record.default_skill),
    hasInstructions: readBoolean(record, "has_instructions"),
    hasToolSetRefs: readBoolean(record, "has_tool_set_refs"),
    hasExtraToolNames: readBoolean(record, "has_extra_tool_names"),
    nyxidServiceSelectors: decodeSelectors(record.nyxid_service_selectors),
    agentKey: decodeAgentKey(record.agent_key),
    workflowResultDeliveryStatus: readString(record, "workflow_result_delivery_status"),
    stateVersion: readNumber(record, "state_version"),
    owned: record.owned !== false,
  };
}

function decodeServiceChoice(value: unknown): ChannelServiceChoice {
  const record = asRecord(value);
  const credentialSource = asRecord(record.credential_source);
  return {
    id: readString(record, "id"),
    slug: readString(record, "slug"),
    label: readString(record, "label"),
    catalogServiceName: readString(record, "catalog_service_name"),
    active: readBoolean(record, "active"),
    credentialSource: {
      kind: readString(credentialSource, "kind"),
      organizationId: readString(credentialSource, "organization_id"),
      organizationRole: readString(credentialSource, "organization_role"),
      allowed: readBoolean(credentialSource, "allowed"),
    },
  };
}

function decodeRuntimeConfigDetail(value: unknown): ChannelRuntimeConfigDetail {
  const record = asRecord(value);
  return {
    registrationId: readString(record, "registration_id"),
    platform: readString(record, "platform"),
    scopeId: readString(record, "scope_id"),
    authorizationMode: readAuthorizationMode(record),
    serviceIds: readStringArray(record, "service_ids"),
    runtimeConfig: decodeRuntimeConfig(record.runtime_config),
    agentKey: decodeAgentKey(record.agent_key),
    workflowResultDeliveryStatus: readString(record, "workflow_result_delivery_status"),
    stateVersion: readNumber(record, "state_version"),
  };
}

function decodeReceipt(value: unknown): ChannelRuntimeConfigReceipt {
  const record = asRecord(value);
  return {
    status: readString(record, "status"),
    registrationId: readString(record, "registration_id"),
    commandId: readString(record, "command_id"),
    correlationId: readString(record, "correlation_id"),
  };
}

function decodeFieldErrors(value: unknown): ChannelRuntimeConfigFieldError[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.map((entry) => {
    const record = asRecord(entry);
    return {
      field: readString(record, "field"),
      code: readString(record, "code"),
      message: readString(record, "message"),
    };
  });
}

async function readJson(response: Response): Promise<unknown> {
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

async function requestJson(input: string, init?: RequestInit): Promise<unknown> {
  const response = await authFetch(input, init);
  const payload = await readJson(response);
  if (response.ok) {
    return payload;
  }

  const record = asRecord(payload);
  const message = readString(record, "message") || readString(record, "error") || "Channel request failed.";
  throw new ChannelSettingsApiError(
    message,
    response.status,
    decodeFieldErrors(record.field_errors),
  );
}

function encodeRuntimeConfig(config: ChannelRuntimeConfig): JsonRecord {
  return {
    instructions: config.instructions,
    default_skill: {
      name: config.defaultSkill.name,
      version: config.defaultSkill.version,
    },
    tool_set_refs: [...config.toolSetRefs],
    extra_tool_names: [...config.extraToolNames],
    nyxid_service_selectors: config.nyxidServiceSelectors.map((selector) => ({
      service_slug: selector.serviceSlug,
      endpoint_names: [...selector.endpointNames],
    })),
  };
}

export const channelSettingsApi = {
  async listServices(): Promise<ChannelServiceChoice[]> {
    const payload = await requestJson("/api/channels/services");
    return Array.isArray(payload) ? payload.map(decodeServiceChoice) : [];
  },

  async listRegistrations(): Promise<ChannelRegistrationSummary[]> {
    const payload = await requestJson("/api/channels/registrations");
    return Array.isArray(payload) ? payload.map(decodeRegistrationSummary) : [];
  },

  async getRuntimeConfig(registrationId: string): Promise<ChannelRuntimeConfigDetail> {
    return decodeRuntimeConfigDetail(
      await requestJson(
        `/api/channels/registrations/${encodeURIComponent(registrationId)}/runtime-config`,
      ),
    );
  },

  async saveRuntimeConfig(
    input: ChannelRuntimeConfigSaveInput,
  ): Promise<ChannelRuntimeConfigReceipt> {
    return decodeReceipt(
      await requestJson(
        `/api/channels/registrations/${encodeURIComponent(input.registrationId)}/runtime-config`,
        {
          body: JSON.stringify({
            authorization_mode: input.authorizationMode,
            service_ids: [...input.serviceIds],
            runtime_config: encodeRuntimeConfig(input.runtimeConfig),
          }),
          headers: jsonHeaders,
          method: "POST",
        },
      ),
    );
  },

  async repairWorkflowResultDelivery(registrationId: string): Promise<void> {
    await requestJson(
      `/api/channels/registrations/${encodeURIComponent(registrationId)}/workflow-result-delivery/repair`,
      { headers: jsonHeaders, method: "POST" },
    );
  },

  async testReply(registrationId: string): Promise<void> {
    await requestJson(
      `/api/channels/registrations/${encodeURIComponent(registrationId)}/test-reply`,
      { headers: jsonHeaders, method: "POST" },
    );
  },

  async deleteRegistration(registrationId: string): Promise<void> {
    await requestJson(
      `/api/channels/registrations/${encodeURIComponent(registrationId)}`,
      { headers: { Accept: "application/json" }, method: "DELETE" },
    );
  },
};
