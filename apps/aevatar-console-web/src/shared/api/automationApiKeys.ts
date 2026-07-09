import { jsonBody, requestJson, withQuery } from "./http/client";
import {
  expectArray,
  expectRecord,
  readNullableString,
  readOptionalString,
  readString,
} from "./http/decoders";
import { authFetch } from "@/shared/auth/fetch";
import { readResponseError } from "./http/error";

export type AutomationApiKeyStatus =
  | "active"
  | "expired"
  | "missing"
  | "revoked";

export type AutomationCredentialRef = {
  readonly subject: {
    readonly platform: string;
    readonly tenant?: string;
    readonly externalUserId: string;
  };
  readonly scope: string;
};

export type AutomationApiKeyMetadata = {
  readonly apiKeyId: string;
  readonly displayName: string;
  readonly scopeId: string;
  readonly status: AutomationApiKeyStatus;
  readonly keySuffix: string;
  readonly createdAt: string;
  readonly lastUsedAt: string | null;
  readonly expiresAt: string | null;
  readonly revokedAt: string | null;
  readonly allowedMemberId: string | null;
  readonly allowedServiceId: string | null;
  readonly credentialRef: AutomationCredentialRef;
};

export type AutomationApiKeyListResult = {
  readonly items: readonly AutomationApiKeyMetadata[];
  readonly totalCount: number | null;
};

export type AutomationApiKeyCreateInput = {
  readonly scopeId: string;
  readonly displayName: string;
  readonly allowedMemberId?: string;
  readonly allowedServiceId?: string;
  readonly expiresAt?: string;
  readonly scopes?: readonly string[];
};

export type AutomationApiKeyCreateResult = {
  readonly apiKey: AutomationApiKeyMetadata;
  readonly credentialRef: AutomationCredentialRef;
  readonly rawKey: string;
};

export type AutomationCredentialStatusInput = {
  readonly scopeId: string;
  readonly memberId?: string;
  readonly serviceId?: string;
};

export type AutomationCredentialStatusResult = {
  readonly status: AutomationApiKeyStatus;
  readonly apiKey: AutomationApiKeyMetadata | null;
  readonly credentialRef: AutomationCredentialRef | null;
};

export type AutomationApiKeyRevokeInput = {
  readonly scopeId: string;
  readonly apiKeyId: string;
};

function trimRequired(value: string, label: string): string {
  const normalized = value.trim();
  if (!normalized) {
    throw new Error(`${label} is required.`);
  }

  return normalized;
}

function trimOptional(value: string | null | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function normalizeStatus(value: string): AutomationApiKeyStatus {
  const normalized = value.trim().toLowerCase();
  switch (normalized) {
    case "active":
    case "expired":
    case "missing":
    case "revoked":
      return normalized;
    default:
      throw new Error(`Automation API key status ${value} is not supported.`);
  }
}

function readNullableNumber(
  record: Record<string, unknown>,
  keys: string[],
): number | null {
  for (const key of keys) {
    if (!(key in record)) {
      continue;
    }

    const value = record[key];
    if (value === null || value === undefined) {
      return null;
    }
    if (typeof value === "number" && !Number.isNaN(value)) {
      return value;
    }
    throw new Error(`${key} must be a number or null.`);
  }

  return null;
}

function readCredentialRef(
  value: unknown,
  label = "AutomationCredentialRef",
): AutomationCredentialRef {
  const record = expectRecord(value, label);
  const subjectRecord = expectRecord(
    record.subject ?? record.Subject,
    `${label}.subject`,
  );
  const tenant = readOptionalString(
    subjectRecord,
    ["tenant", "Tenant"],
    `${label}.subject.tenant`,
  );

  return {
    subject: {
      platform: readString(
        subjectRecord,
        ["platform", "Platform"],
        `${label}.subject.platform`,
      ).trim(),
      ...(tenant ? { tenant: tenant.trim() } : {}),
      externalUserId: readString(
        subjectRecord,
        ["externalUserId", "ExternalUserId"],
        `${label}.subject.externalUserId`,
      ).trim(),
    },
    scope: readString(record, ["scope", "Scope"], `${label}.scope`).trim(),
  };
}

function decodeAutomationApiKeyMetadata(
  value: unknown,
  label = "AutomationApiKeyMetadata",
): AutomationApiKeyMetadata {
  const record = expectRecord(value, label);
  return {
    apiKeyId: readString(record, ["apiKeyId", "ApiKeyId"], `${label}.apiKeyId`),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    status: normalizeStatus(readString(record, ["status", "Status"], `${label}.status`)),
    keySuffix: readString(record, ["keySuffix", "KeySuffix"], `${label}.keySuffix`),
    createdAt: readString(record, ["createdAt", "CreatedAt"], `${label}.createdAt`),
    lastUsedAt: readNullableString(record, ["lastUsedAt", "LastUsedAt"], `${label}.lastUsedAt`),
    expiresAt: readNullableString(record, ["expiresAt", "ExpiresAt"], `${label}.expiresAt`),
    revokedAt: readNullableString(record, ["revokedAt", "RevokedAt"], `${label}.revokedAt`),
    allowedMemberId: readNullableString(record, ["allowedMemberId", "AllowedMemberId"], `${label}.allowedMemberId`),
    allowedServiceId: readNullableString(record, ["allowedServiceId", "AllowedServiceId"], `${label}.allowedServiceId`),
    credentialRef: readCredentialRef(
      record.credentialRef ?? record.CredentialRef,
      `${label}.credentialRef`,
    ),
  };
}

function decodeAutomationApiKeyListResult(
  value: unknown,
  label = "AutomationApiKeyListResult",
): AutomationApiKeyListResult {
  const record = expectRecord(value, label);
  return {
    items: expectArray(
      record.items ?? record.Items,
      `${label}.items`,
      decodeAutomationApiKeyMetadata,
    ),
    totalCount: readNullableNumber(record, ["totalCount", "TotalCount"]),
  };
}

function decodeAutomationApiKeyCreateResult(
  value: unknown,
  label = "AutomationApiKeyCreateResult",
): AutomationApiKeyCreateResult {
  const record = expectRecord(value, label);
  const apiKey = decodeAutomationApiKeyMetadata(
    record.apiKey ?? record.ApiKey,
    `${label}.apiKey`,
  );
  return {
    apiKey,
    credentialRef: readCredentialRef(
      record.credentialRef ?? record.CredentialRef ?? apiKey.credentialRef,
      `${label}.credentialRef`,
    ),
    rawKey: readString(record, ["rawKey", "RawKey"], `${label}.rawKey`),
  };
}

function decodeAutomationCredentialStatus(
  value: unknown,
  label = "AutomationCredentialStatus",
): AutomationCredentialStatusResult {
  const record = expectRecord(value, label);
  const apiKeyValue = record.apiKey ?? record.ApiKey;
  const credentialRefValue = record.credentialRef ?? record.CredentialRef;
  return {
    status: normalizeStatus(readString(record, ["status", "Status"], `${label}.status`)),
    apiKey:
      apiKeyValue === null || apiKeyValue === undefined
        ? null
        : decodeAutomationApiKeyMetadata(apiKeyValue, `${label}.apiKey`),
    credentialRef:
      credentialRefValue === null || credentialRefValue === undefined
        ? null
        : readCredentialRef(credentialRefValue, `${label}.credentialRef`),
  };
}

function buildScopedPath(scopeId: string, suffix: string): string {
  const normalizedScopeId = trimRequired(scopeId, "scopeId");
  return `/api/scopes/${encodeURIComponent(normalizedScopeId)}/${suffix}`;
}

function list(scopeId: string): Promise<AutomationApiKeyListResult> {
  return requestJson(
    buildScopedPath(scopeId, "automation-api-keys"),
    decodeAutomationApiKeyListResult,
  );
}

function create(
  input: AutomationApiKeyCreateInput,
): Promise<AutomationApiKeyCreateResult> {
  const body = {
    displayName: trimRequired(input.displayName, "displayName"),
    ...(trimOptional(input.allowedMemberId)
      ? { allowedMemberId: trimOptional(input.allowedMemberId) }
      : {}),
    ...(trimOptional(input.allowedServiceId)
      ? { allowedServiceId: trimOptional(input.allowedServiceId) }
      : {}),
    ...(trimOptional(input.expiresAt) ? { expiresAt: trimOptional(input.expiresAt) } : {}),
    scopes: (input.scopes ?? ["proxy"])
      .map((scope) => scope.trim())
      .filter(Boolean),
  };

  return requestJson(
    buildScopedPath(input.scopeId, "automation-api-keys"),
    decodeAutomationApiKeyCreateResult,
    {
      method: "POST",
      ...jsonBody(body),
    },
  );
}

function getStatus(
  input: AutomationCredentialStatusInput,
): Promise<AutomationCredentialStatusResult> {
  return requestJson(
    withQuery(buildScopedPath(input.scopeId, "automation-api-keys/status"), {
      memberId: trimOptional(input.memberId),
      serviceId: trimOptional(input.serviceId),
    }),
    decodeAutomationCredentialStatus,
  );
}

async function revoke(input: AutomationApiKeyRevokeInput): Promise<void> {
  const response = await authFetch(
    `${buildScopedPath(input.scopeId, "automation-api-keys")}/${encodeURIComponent(trimRequired(input.apiKeyId, "apiKeyId"))}`,
    {
      method: "DELETE",
    },
  );
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }
}

export const automationApiKeysApi = {
  create,
  getStatus,
  list,
  revoke,
};
