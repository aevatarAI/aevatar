import {
  getNyxIDRuntimeConfig,
  type NyxIDRuntimeConfig,
} from './config';
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
  loadStoredAuthSession,
  persistAuthSession,
  sanitizeReturnTo,
  type NyxIDAuthSession,
  type NyxIDTokenSet,
  type NyxIDUserInfo,
} from './session';

interface PendingAuthState {
  readonly state: string;
  readonly codeVerifier: string;
  readonly redirectUri: string;
  readonly scope: string;
  readonly returnTo: string;
}

interface TokenResponse {
  readonly access_token: string;
  readonly token_type: string;
  readonly expires_in: number;
  readonly refresh_token?: string;
  readonly id_token?: string;
  readonly scope?: string;
}

export interface LoginRedirectOptions {
  readonly returnTo?: string;
  readonly prompt?: 'none' | 'consent' | 'login' | (string & {});
}

export interface AuthCallbackResult {
  readonly session: NyxIDAuthSession;
  readonly returnTo: string;
}

const PENDING_KEY_PREFIX = 'aevatar-console:nyxid:pending:';
let pendingRefreshPromise: Promise<NyxIDAuthSession | null> | null = null;

function base64UrlEncode(input: Uint8Array): string {
  let binary = '';
  for (const byte of input) {
    binary += String.fromCharCode(byte);
  }

  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function randomUrlSafeString(bytes = 32): string {
  const data = new Uint8Array(bytes);
  crypto.getRandomValues(data);
  return base64UrlEncode(data);
}

async function sha256Base64Url(input: string): Promise<string> {
  const digest = await crypto.subtle.digest(
    'SHA-256',
    new TextEncoder().encode(input),
  );
  return base64UrlEncode(new Uint8Array(digest));
}

function readErrorDetail(payload: unknown, fallback: string): string {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
    return fallback;
  }

  const record = payload as Record<string, unknown>;
  const detail = record.error_description || record.error || record.message;
  return typeof detail === 'string' && detail.trim().length > 0 ? detail : fallback;
}

export class NyxIDAuthClient {
  private readonly config: NyxIDRuntimeConfig;

  constructor(config: NyxIDRuntimeConfig) {
    if (!config.enabled) {
      throw new Error(
        config.configurationError ?? 'NyxID login is not configured.',
      );
    }

    this.config = config;
  }

  private get pendingKey(): string {
    return `${PENDING_KEY_PREFIX}${this.config.clientId}`;
  }

  private get storage(): Storage {
    if (typeof window === 'undefined' || !window.localStorage) {
      throw new Error('NyxID auth requires browser localStorage');
    }

    return window.localStorage;
  }

  async loginWithRedirect(options: LoginRedirectOptions = {}): Promise<void> {
    if (typeof window === 'undefined') {
      throw new Error('loginWithRedirect requires a browser environment');
    }

    const codeVerifier = randomUrlSafeString(48);
    const codeChallenge = await sha256Base64Url(codeVerifier);
    const state = randomUrlSafeString(24);
    const redirectUri = this.config.redirectUri;
    const scope = this.config.scope;
    const returnTo = sanitizeReturnTo(options.returnTo);

    const pending: PendingAuthState = {
      state,
      codeVerifier,
      redirectUri,
      scope,
      returnTo,
    };
    this.storage.setItem(this.pendingKey, JSON.stringify(pending));

    const url = new URL(`${this.config.baseUrl}/oauth/authorize`);
    url.searchParams.set('response_type', 'code');
    url.searchParams.set('client_id', this.config.clientId);
    url.searchParams.set('redirect_uri', redirectUri);
    url.searchParams.set('scope', scope);
    url.searchParams.set('code_challenge', codeChallenge);
    url.searchParams.set('code_challenge_method', 'S256');
    url.searchParams.set('state', state);
    if (options.prompt) {
      url.searchParams.set('prompt', options.prompt);
    }

    window.location.assign(url.toString());
  }

  async handleRedirectCallback(
    currentUrl = window.location.href,
  ): Promise<AuthCallbackResult> {
    const callback = new URL(currentUrl);
    const oauthError = callback.searchParams.get('error');
    if (oauthError) {
      throw new Error(
        callback.searchParams.get('error_description') ?? `OAuth error: ${oauthError}`,
      );
    }

    const code = callback.searchParams.get('code');
    const state = callback.searchParams.get('state');
    if (!code || !state) {
      throw new Error('Missing authorization code or state');
    }

    const rawPending = this.storage.getItem(this.pendingKey);
    const pending = rawPending ? (JSON.parse(rawPending) as PendingAuthState) : null;
    if (!pending) {
      throw new Error('Missing PKCE state in storage');
    }
    if (pending.state !== state) {
      this.storage.removeItem(this.pendingKey);
      throw new Error('State mismatch');
    }

    const form = new URLSearchParams();
    form.set('grant_type', 'authorization_code');
    form.set('code', code);
    form.set('redirect_uri', pending.redirectUri);
    form.set('client_id', this.config.clientId);
    form.set('code_verifier', pending.codeVerifier);

    try {
      const response = await fetch(`${this.config.baseUrl}/oauth/token`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/x-www-form-urlencoded',
        },
        body: form.toString(),
      });

      if (!response.ok) {
        const payload = (await response.json().catch(() => null)) as
          | Record<string, unknown>
          | null;
        throw new Error(
          `Token exchange failed: ${readErrorDetail(payload, response.statusText)}`,
        );
      }

      const body = (await response.json()) as TokenResponse;
      const tokens: NyxIDTokenSet = {
        accessToken: body.access_token,
        tokenType: body.token_type,
        expiresIn: body.expires_in,
        expiresAt: Date.now() + body.expires_in * 1000,
        refreshToken: body.refresh_token,
        idToken: body.id_token,
        scope: body.scope,
      };
      const user = await this.getUserInfo(tokens.accessToken);
      const session: NyxIDAuthSession = {
        tokens,
        user,
      };

      persistAuthSession(session);
      this.storage.removeItem(this.pendingKey);

      return {
        session,
        returnTo: sanitizeReturnTo(pending.returnTo),
      };
    } catch (error) {
      this.storage.removeItem(this.pendingKey);
      throw error;
    }
  }

  async getUserInfo(accessToken: string): Promise<NyxIDUserInfo> {
    const response = await fetch(`${this.config.baseUrl}/oauth/userinfo`, {
      method: 'GET',
      headers: {
        Authorization: `Bearer ${accessToken}`,
      },
    });

    if (!response.ok) {
      throw new Error('Failed to fetch user information');
    }

    return (await response.json()) as NyxIDUserInfo;
  }

  async refreshSession(session: NyxIDAuthSession): Promise<NyxIDAuthSession> {
    const refreshToken = session.tokens.refreshToken;
    if (!refreshToken) {
      throw new Error('Missing refresh token');
    }

    const form = new URLSearchParams();
    form.set('grant_type', 'refresh_token');
    form.set('refresh_token', refreshToken);

    const response = await fetch(`${this.config.baseUrl}/oauth/token`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
      },
      body: form.toString(),
    });

    if (!response.ok) {
      const payload = (await response.json().catch(() => null)) as
        | Record<string, unknown>
        | null;
      throw new Error(
        `Token refresh failed: ${readErrorDetail(payload, response.statusText)}`,
      );
    }

    const body = (await response.json()) as TokenResponse;
    const tokens: NyxIDTokenSet = {
      accessToken: body.access_token,
      tokenType: body.token_type,
      expiresIn: body.expires_in,
      expiresAt: Date.now() + body.expires_in * 1000,
      refreshToken: body.refresh_token ?? refreshToken,
      idToken: body.id_token ?? session.tokens.idToken,
      scope: body.scope ?? session.tokens.scope,
    };

    let user = session.user;
    try {
      user = await this.getUserInfo(tokens.accessToken);
    } catch {
      user = session.user;
    }

    const refreshedSession: NyxIDAuthSession = {
      tokens,
      user,
    };
    persistAuthSession(refreshedSession);
    return refreshedSession;
  }
}

export function hasRestorableAuthSession(): boolean {
  return Boolean(loadRestorableAuthSession());
}

export async function ensureActiveAuthSession(
  config = getNyxIDRuntimeConfig(),
): Promise<NyxIDAuthSession | null> {
  const activeSession = loadStoredAuthSession();
  if (activeSession) {
    return activeSession;
  }

  if (!config.enabled) {
    return null;
  }

  const restorableSession = loadRestorableAuthSession();
  if (!restorableSession?.tokens.refreshToken) {
    clearStoredAuthSession();
    return null;
  }

  if (!pendingRefreshPromise) {
    const client = new NyxIDAuthClient(config);
    pendingRefreshPromise = client
      .refreshSession(restorableSession)
      .catch(() => {
        clearStoredAuthSession();
        return null;
      })
      .finally(() => {
        pendingRefreshPromise = null;
      });
  }

  return pendingRefreshPromise;
}
