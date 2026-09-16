import { ensureActiveAuthSession } from "@/shared/auth/client";
import { getNyxIDRuntimeConfig } from "@/shared/auth/config";

type JsonRecord = Record<string, unknown>;

export class NyxIdServiceApiError extends Error {
  readonly code: string;
  readonly status: number;

  constructor(message: string, code: string, status = 0) {
    super(message);
    this.name = "NyxIdServiceApiError";
    this.code = code;
    this.status = status;
  }
}

export type NyxIdUserService = {
  userServiceId: string;
  apiKeyId: string;
  endpointUrl: string;
  label: string;
};

export type NyxIdConnector = {
  slug: string;
  name: string;
  description: string;
  authKind: string;
  custom?: boolean;
  userServices: NyxIdUserService[];
};

export type NyxIdAvailableConnector = Omit<NyxIdConnector, "userServices"> & {
  apiKeyUrl: string;
  apiKeyInstructions: string;
  docsUrl: string;
};

export type NyxIdConnectors = {
  connected: NyxIdConnector[];
  available: NyxIdAvailableConnector[];
};

export type CreateNyxIdCatalogKeyInput = {
  serviceSlug: string;
  credential: string;
  label: string;
};

export async function listNyxIdConnectors(): Promise<NyxIdConnectors> {
  const [keysPayload, catalogPayload] = await Promise.all([
    nyxIdRequest("/api/v1/keys"),
    nyxIdRequest("/api/v1/catalog"),
  ]);
  const keys = Array.isArray(keysPayload.keys)
    ? keysPayload.keys.filter(isRecord)
    : [];
  const catalog = Array.isArray(catalogPayload.entries)
    ? catalogPayload.entries.filter(isRecord)
    : Array.isArray(catalogPayload)
      ? catalogPayload.filter(isRecord)
      : [];
  return deriveConnectors(keys, catalog);
}

export async function createNyxIdCatalogKey(
  input: CreateNyxIdCatalogKeyInput
): Promise<string> {
  const payload = await nyxIdRequest("/api/v1/keys", {
    method: "POST",
    body: {
      service_slug: input.serviceSlug.trim(),
      credential: input.credential,
      label: input.label.trim() || input.serviceSlug.trim(),
    },
  });
  const userServiceId = readString(payload.id);
  if (!userServiceId) {
    throw new NyxIdServiceApiError(
      "NyxID did not return the created UserService identity.",
      "NYXID_USER_SERVICE_ID_MISSING",
      502
    );
  }
  return userServiceId;
}

export function matchingUserServiceIds(
  request: {
    params:
      | { catalogService: { serviceSlug: string } }
      | { customService: { name: string; endpointUrl: string } };
  },
  connectors: NyxIdConnectors
): Set<string> {
  const catalog = "catalogService" in request.params
    ? request.params.catalogService
    : null;
  const custom = "customService" in request.params
    ? request.params.customService
    : null;
  const ids = new Set<string>();
  for (const connector of connectors.connected) {
    const connectorMatches = catalog
      ? connector.slug === catalog.serviceSlug
      : connector.custom === true &&
        connector.name.trim().toLowerCase() === custom?.name.trim().toLowerCase();
    if (!connectorMatches) continue;
    for (const service of connector.userServices) {
      if (
        custom &&
        (normalizeEndpoint(service.endpointUrl) !==
          normalizeEndpoint(custom.endpointUrl) ||
          service.label.trim().toLowerCase() !== custom.name.trim().toLowerCase())
      ) {
        continue;
      }
      if (service.userServiceId) ids.add(service.userServiceId);
    }
  }
  return ids;
}

export function matchNewUserServiceId(
  before: ReadonlySet<string>,
  after: ReadonlySet<string>
): string | null {
  const candidates = [...after].filter((id) => !before.has(id));
  return candidates.length === 1 ? candidates[0] : null;
}

export function buildNyxIdConnectUrl(serviceSlug?: string): string {
  const url = new URL("/keys", getNyxIDRuntimeConfig().baseUrl);
  if (serviceSlug?.trim()) url.searchParams.set("slug", serviceSlug.trim());
  return url.toString();
}

function deriveConnectors(
  keys: readonly JsonRecord[],
  catalog: readonly JsonRecord[]
): NyxIdConnectors {
  const catalogBySlug = new Map(
    catalog
      .map((entry) => [readString(entry.slug), entry] as const)
      .filter(([slug]) => Boolean(slug))
  );
  const groups = new Map<string, JsonRecord[]>();
  const custom: JsonRecord[] = [];
  for (const key of keys) {
    const slug =
      readString(key.catalog_service_slug) || readString(key.catalogServiceSlug);
    if (!slug) {
      custom.push(key);
      continue;
    }
    groups.set(slug, [...(groups.get(slug) ?? []), key]);
  }

  const connected: NyxIdConnector[] = [];
  for (const [slug, group] of groups) {
    const entry = catalogBySlug.get(slug);
    connected.push({
      slug,
      name:
        readString(entry?.name) ||
        readString(group[0]?.catalog_service_name) ||
        readString(group[0]?.label) ||
        slug,
      description: readString(entry?.description),
      authKind: readString(entry?.auth_method) || "api_key",
      userServices: group.map(toUserService).filter(hasUserServiceId),
    });
  }
  for (const key of custom) {
    connected.push({
      slug: readString(key.slug),
      name: readString(key.label) || readString(key.slug) || "Custom service",
      description: readString(key.description),
      authKind: readString(key.auth_method) || "custom",
      custom: true,
      userServices: [toUserService(key)].filter(hasUserServiceId),
    });
  }

  const available = catalog
    .filter((entry) => {
      const slug = readString(entry.slug);
      return slug && !groups.has(slug);
    })
    .map((entry) => ({
      slug: readString(entry.slug),
      name: readString(entry.name) || readString(entry.slug),
      description: readString(entry.description),
      authKind: readString(entry.auth_method) || "api_key",
      apiKeyUrl: readString(entry.api_key_url),
      apiKeyInstructions: readString(entry.api_key_instructions),
      docsUrl: readString(entry.documentation_url),
    }));
  return {
    connected: connected.sort((a, b) => a.name.localeCompare(b.name)),
    available: available.sort((a, b) => a.name.localeCompare(b.name)),
  };
}

async function nyxIdRequest(
  path: string,
  options: { method?: string; body?: JsonRecord } = {}
): Promise<JsonRecord> {
  const config = getNyxIDRuntimeConfig();
  const accessToken = (await ensureActiveAuthSession(config))?.tokens.accessToken;
  if (!config.enabled || !config.baseUrl || !accessToken) {
    throw new NyxIdServiceApiError(
      "An active NyxID session is required.",
      "NYXID_AUTH_REQUIRED",
      401
    );
  }
  const headers: Record<string, string> = {
    Authorization: `Bearer ${accessToken}`,
  };
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch(`${config.baseUrl}${path}`, {
    headers,
    ...(options.method ? { method: options.method } : {}),
    ...(options.body !== undefined
      ? { body: JSON.stringify(options.body) }
      : {}),
  });
  const text = await response.text();
  let payload: unknown = {};
  try {
    payload = text ? JSON.parse(text) : {};
  } catch {
    throw new NyxIdServiceApiError(
      "NyxID returned an invalid response.",
      "NYXID_RESPONSE_INVALID",
      response.status
    );
  }
  if (!response.ok) {
    const record = isRecord(payload) ? payload : {};
    throw new NyxIdServiceApiError(
      "NyxID request failed.",
      readString(record.code) || "NYXID_API_FAILED",
      response.status
    );
  }
  return isRecord(payload) ? payload : {};
}

function toUserService(key: JsonRecord): NyxIdUserService {
  return {
    userServiceId: readString(key.id),
    apiKeyId: readString(key.api_key_id),
    endpointUrl: readString(key.endpoint_url),
    label:
      readString(key.label) ||
      readString(key.catalog_service_name) ||
      readString(key.slug) ||
      "Service",
  };
}

function hasUserServiceId(value: NyxIdUserService): boolean {
  return Boolean(value.userServiceId);
}

function normalizeEndpoint(value: string): string {
  try {
    const url = new URL(value);
    return url.protocol === "https:" &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash
      ? url.toString().replace(/\/$/, "")
      : "";
  } catch {
    return "";
  }
}

function isRecord(value: unknown): value is JsonRecord {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function readString(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}
