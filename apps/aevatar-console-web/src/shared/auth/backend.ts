import {
  readResponseErrorDetails,
  type ResponseErrorDetails,
} from "@/shared/api/http/error";
import type { NyxIDAuthSession, NyxIDTokenSet, NyxIDUserInfo } from "./session";

export type NyxIDLoginFinalizationRequest = {
  readonly code: string;
  readonly codeVerifier: string;
  readonly redirectUri: string;
  readonly serviceAccessReview?: boolean;
};

export type NyxIDLoginFinalizationResult = {
  readonly bindingDispatchAccepted: boolean;
  readonly session: NyxIDAuthSession;
};

export type NyxIDTokenRefreshRequest = {
  readonly baseUrl: string;
  readonly clientId: string;
  readonly refreshToken: string;
};

export class NyxIDLoginFinalizationError extends Error {
  readonly code?: string;
  readonly status: number;

  constructor(details: ResponseErrorDetails) {
    super(details.message);
    this.name = "NyxIDLoginFinalizationError";
    this.code = details.code;
    this.status = details.status;
  }
}

type BackendFinalizationResponse = {
  readonly bindingDispatchAccepted?: unknown;
  readonly BindingDispatchAccepted?: unknown;
  readonly tokens?: unknown;
  readonly Tokens?: unknown;
  readonly user?: unknown;
  readonly User?: unknown;
};

type BackendTokenSetResponse = {
  readonly accessToken?: unknown;
  readonly AccessToken?: unknown;
  readonly access_token?: unknown;
  readonly tokenType?: unknown;
  readonly TokenType?: unknown;
  readonly token_type?: unknown;
  readonly expiresIn?: unknown;
  readonly ExpiresIn?: unknown;
  readonly expires_in?: unknown;
  readonly refreshToken?: unknown;
  readonly RefreshToken?: unknown;
  readonly refresh_token?: unknown;
  readonly idToken?: unknown;
  readonly IdToken?: unknown;
  readonly id_token?: unknown;
  readonly scope?: unknown;
  readonly Scope?: unknown;
};

type NyxIDTokenRefreshResponse = BackendTokenSetResponse;

type BackendUserInfoResponse = {
  readonly sub?: unknown;
  readonly Sub?: unknown;
  readonly email?: unknown;
  readonly Email?: unknown;
  readonly email_verified?: unknown;
  readonly emailVerified?: unknown;
  readonly EmailVerified?: unknown;
  readonly name?: unknown;
  readonly Name?: unknown;
  readonly picture?: unknown;
  readonly Picture?: unknown;
  readonly roles?: unknown;
  readonly Roles?: unknown;
  readonly groups?: unknown;
  readonly Groups?: unknown;
  readonly permissions?: unknown;
  readonly Permissions?: unknown;
};

function expectRecord(value: unknown, label: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }

  return value as Record<string, unknown>;
}

function readString(value: unknown, label: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new Error(`${label} must be a non-empty string.`);
  }

  return value.trim();
}

function readOptionalString(value: unknown): string | undefined {
  return typeof value === "string" && value.trim().length > 0
    ? value.trim()
    : undefined;
}

function readNumber(value: unknown, label: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`${label} must be a number.`);
  }

  return value;
}

function readBoolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") {
    throw new Error(`${label} must be a boolean.`);
  }

  return value;
}

function readOptionalBoolean(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function readOptionalStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }

  return value
    .filter((entry): entry is string => typeof entry === "string")
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function decodeTokenSet(value: unknown): NyxIDTokenSet {
  const record = expectRecord(value, "NyxID finalized tokens") as
    BackendTokenSetResponse;
  const expiresIn = readNumber(
    record.expiresIn ?? record.ExpiresIn ?? record.expires_in,
    "tokens.expiresIn",
  );

  return {
    accessToken: readString(
      record.accessToken ?? record.AccessToken ?? record.access_token,
      "tokens.accessToken",
    ),
    tokenType: readString(
      record.tokenType ?? record.TokenType ?? record.token_type,
      "tokens.tokenType",
    ),
    expiresIn,
    expiresAt: Date.now() + expiresIn * 1000,
    refreshToken: readOptionalString(
      record.refreshToken ?? record.RefreshToken ?? record.refresh_token,
    ),
    idToken: readOptionalString(
      record.idToken ?? record.IdToken ?? record.id_token,
    ),
    scope: readOptionalString(record.scope ?? record.Scope),
  };
}

function decodeRefreshTokenSet(
  value: unknown,
  fallbackRefreshToken: string,
): NyxIDTokenSet {
  const record = expectRecord(value, "NyxID refreshed tokens") as
    NyxIDTokenRefreshResponse;
  const expiresIn = readNumber(
    record.expiresIn ?? record.ExpiresIn ?? record.expires_in,
    "tokens.expiresIn",
  );

  return {
    accessToken: readString(
      record.accessToken ?? record.AccessToken ?? record.access_token,
      "tokens.accessToken",
    ),
    tokenType:
      readOptionalString(
        record.tokenType ?? record.TokenType ?? record.token_type,
      ) ?? "Bearer",
    expiresIn,
    expiresAt: Date.now() + expiresIn * 1000,
    refreshToken:
      readOptionalString(
        record.refreshToken ?? record.RefreshToken ?? record.refresh_token,
      ) ?? fallbackRefreshToken,
    idToken: readOptionalString(
      record.idToken ?? record.IdToken ?? record.id_token,
    ),
    scope: readOptionalString(record.scope ?? record.Scope),
  };
}

function decodeUserInfo(value: unknown): NyxIDUserInfo {
  const record = expectRecord(value, "NyxID finalized user") as
    BackendUserInfoResponse;

  return {
    sub: readString(record.sub ?? record.Sub, "user.sub"),
    email: readOptionalString(record.email ?? record.Email),
    email_verified: readOptionalBoolean(
      record.email_verified ?? record.emailVerified ?? record.EmailVerified,
    ),
    name: readOptionalString(record.name ?? record.Name),
    picture: readOptionalString(record.picture ?? record.Picture),
    roles: readOptionalStringArray(record.roles ?? record.Roles),
    groups: readOptionalStringArray(record.groups ?? record.Groups),
    permissions: readOptionalStringArray(
      record.permissions ?? record.Permissions,
    ),
  };
}

function decodeFinalizationResult(
  value: unknown,
): NyxIDLoginFinalizationResult {
  const record = expectRecord(value, "NyxID login finalization") as
    BackendFinalizationResponse;

  return {
    bindingDispatchAccepted: readBoolean(
      record.bindingDispatchAccepted ?? record.BindingDispatchAccepted,
      "bindingDispatchAccepted",
    ),
    session: {
      tokens: decodeTokenSet(record.tokens ?? record.Tokens),
      user: decodeUserInfo(record.user ?? record.User),
    },
  };
}

async function requestBackendJson<T>(
  input: string,
  decoder: (value: unknown) => T,
  init?: RequestInit,
  createError?: (details: ResponseErrorDetails) => Error,
): Promise<T> {
  const response = await fetch(input, init);
  if (!response.ok) {
    const details = await readResponseErrorDetails(response);
    throw createError?.(details) ?? new Error(details.message);
  }

  return decoder(await response.json());
}

export function finalizeBackendNyxIDLogin(
  request: NyxIDLoginFinalizationRequest,
): Promise<NyxIDLoginFinalizationResult> {
  return requestBackendJson(
    "/api/auth/nyxid/finalize",
    decodeFinalizationResult,
    {
      body: JSON.stringify(request),
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
      },
      method: "POST",
    },
    (details) => new NyxIDLoginFinalizationError(details),
  );
}

export function refreshNyxIDTokenSet(
  request: NyxIDTokenRefreshRequest,
): Promise<NyxIDTokenSet> {
  const baseUrl = readString(request.baseUrl, "baseUrl").replace(/\/+$/, "");
  const clientId = readString(request.clientId, "clientId");
  const refreshToken = readString(request.refreshToken, "refreshToken");
  const body = new URLSearchParams({
    grant_type: "refresh_token",
    refresh_token: refreshToken,
    client_id: clientId,
  });

  return requestBackendJson(
    `${baseUrl}/oauth/token`,
    (value) => decodeRefreshTokenSet(value, refreshToken),
    {
      body,
      headers: {
        Accept: "application/json",
        "Content-Type": "application/x-www-form-urlencoded",
      },
      method: "POST",
    },
  );
}
