import type { NyxIDRuntimeConfig } from './config';
import { stripAppBasePath } from '../navigation/appPath';

export interface NyxIDTokenSet {
  readonly accessToken: string;
  readonly tokenType: string;
  readonly expiresIn: number;
  readonly expiresAt: number;
  readonly refreshToken?: string;
  readonly idToken?: string;
  readonly scope?: string;
}

export interface NyxIDUserInfo {
  readonly sub: string;
  readonly email?: string;
  readonly email_verified?: boolean;
  readonly name?: string;
  readonly picture?: string;
  readonly roles?: string[];
  readonly groups?: string[];
  readonly permissions?: string[];
}

export interface NyxIDAuthSession {
  readonly tokens: NyxIDTokenSet;
  readonly user: NyxIDUserInfo;
}

export interface AuthInitialState {
  readonly enabled: boolean;
  readonly isAuthenticated: boolean;
  readonly config: NyxIDRuntimeConfig;
  readonly session?: NyxIDAuthSession;
}

const AUTH_SESSION_STORAGE_KEY = 'aevatar-console:nyxid:session';
const ACCESS_TOKEN_CLOCK_SKEW_MS = 30_000;
const AUTH_BLOCKED_PATHS = new Set(['/login', '/auth/callback']);

function getStorage(): Storage | undefined {
  if (typeof window === 'undefined') {
    return undefined;
  }

  return window.localStorage;
}

function safeParse<T>(raw: string | null): T | null {
  if (!raw) {
    return null;
  }

  try {
    return JSON.parse(raw) as T;
  } catch {
    return null;
  }
}

export function hasActiveAccessToken(tokens: NyxIDTokenSet | undefined): boolean {
  if (!tokens) {
    return false;
  }

  return tokens.expiresAt - ACCESS_TOKEN_CLOCK_SKEW_MS > Date.now();
}

export function readStoredAuthSession(): NyxIDAuthSession | null {
  const storage = getStorage();
  if (!storage) {
    return null;
  }

  const session = safeParse<NyxIDAuthSession>(
    storage.getItem(AUTH_SESSION_STORAGE_KEY),
  );

  if (!session) {
    storage.removeItem(AUTH_SESSION_STORAGE_KEY);
    return null;
  }

  return session;
}

export function loadStoredAuthSession(): NyxIDAuthSession | null {
  const storage = getStorage();
  const session = readStoredAuthSession();
  if (!storage || !session) {
    return null;
  }

  if (!hasActiveAccessToken(session.tokens)) {
    if (!session.tokens.refreshToken) {
      storage.removeItem(AUTH_SESSION_STORAGE_KEY);
    }
    return null;
  }

  return session;
}

export function loadRestorableAuthSession(): NyxIDAuthSession | null {
  const storage = getStorage();
  const session = readStoredAuthSession();
  if (!storage || !session) {
    return null;
  }

  if (hasActiveAccessToken(session.tokens) || session.tokens.refreshToken) {
    return session;
  }

  storage.removeItem(AUTH_SESSION_STORAGE_KEY);
  return null;
}

export function persistAuthSession(session: NyxIDAuthSession): void {
  const storage = getStorage();
  if (!storage) {
    return;
  }

  storage.setItem(AUTH_SESSION_STORAGE_KEY, JSON.stringify(session));
}

export function clearStoredAuthSession(): void {
  getStorage()?.removeItem(AUTH_SESSION_STORAGE_KEY);
}

export function getActiveAccessToken(): string | undefined {
  return loadStoredAuthSession()?.tokens.accessToken;
}

export function buildAuthInitialState(config: NyxIDRuntimeConfig): AuthInitialState {
  const session = config.enabled ? loadStoredAuthSession() : null;

  return {
    enabled: config.enabled,
    isAuthenticated: Boolean(session),
    config,
    session: session ?? undefined,
  };
}

export function sanitizeReturnTo(value?: string | null): string {
  const normalized = value?.trim();
  if (!normalized || normalized.startsWith('//')) {
    return '/overview';
  }

  if (!normalized.startsWith('/') && !hasScheme(normalized)) {
    return '/overview';
  }

  try {
    const candidate = normalized.startsWith('/')
      ? new URL(normalized, resolveWindowOrigin())
      : new URL(normalized);
    if (candidate.origin !== resolveWindowOrigin()) {
      return '/overview';
    }

    const target = stripAppBasePath(candidate.pathname);
    if (AUTH_BLOCKED_PATHS.has(target)) {
      return '/overview';
    }

    return `${target}${candidate.search}${candidate.hash}`;
  } catch {
    return '/overview';
  }
}

function resolveWindowOrigin(): string {
  if (typeof window !== 'undefined') {
    return window.location.origin;
  }

  return 'http://127.0.0.1:5173';
}

function hasScheme(value: string): boolean {
  return /^[a-zA-Z][a-zA-Z\d+.-]*:/.test(value);
}
