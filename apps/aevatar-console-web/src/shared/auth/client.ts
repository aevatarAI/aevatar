import {
  getNyxIDRuntimeConfig,
  type NyxIDRuntimeConfig,
} from './config';
import {
  finalizeBackendNyxIDLogin,
  loadBackendNyxIDLoginConfig,
  refreshNyxIDTokenSet,
  type NyxIDBackendLoginConfig,
} from './backend';
import {
  clearStoredAuthSession,
  hasActiveAccessToken,
  loadStoredAuthSession,
  persistAuthSession,
  readStoredAuthSession,
  sanitizeReturnTo,
  type NyxIDAuthSession,
} from './session';

interface PendingAuthState {
  readonly state: string;
  readonly codeVerifier: string;
  readonly redirectUri: string;
  readonly scope: string;
  readonly returnTo: string;
  readonly clientId: string;
  readonly flow: AuthFlow;
}

export type AuthFlow = "signIn" | "serviceAccessReview";

export interface LoginRedirectOptions {
  readonly returnTo?: string;
  readonly flow?: AuthFlow;
}

export interface AuthCallbackResult {
  readonly session: NyxIDAuthSession;
  readonly returnTo: string;
  readonly flow: AuthFlow;
}

const PENDING_KEY_PREFIX = 'aevatar-console:nyxid:pending:';
export const SERVICE_ACCESS_REVIEW_RETURN_TO = "/settings?section=account";
let pendingRefreshPromise: Promise<NyxIDAuthSession | null> | null = null;

export class NyxIDAuthCallbackError extends Error {
  readonly flow: AuthFlow;
  readonly returnTo: string;

  constructor(
    message: string,
    options: {
      readonly flow: AuthFlow;
      readonly returnTo: string;
    },
  ) {
    super(message);
    this.name = "NyxIDAuthCallbackError";
    this.flow = options.flow;
    this.returnTo = options.returnTo;
  }
}

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

function readAuthFlow(value: unknown): AuthFlow {
  return value === "serviceAccessReview" ? "serviceAccessReview" : "signIn";
}

function resolveReturnToForFlow(flow: AuthFlow, returnTo?: string | null): string {
  if (flow === "serviceAccessReview") {
    return SERVICE_ACCESS_REVIEW_RETURN_TO;
  }

  return sanitizeReturnTo(returnTo);
}

function describeOAuthCallbackError(
  oauthError: string,
  description: string | null,
  flow: AuthFlow,
): string {
  if (flow === "serviceAccessReview") {
    return "NyxID service access review was cancelled or denied. Your current Studio session is still active; choose Manage service access to try again.";
  }

  return description?.trim() || `OAuth error: ${oauthError}`;
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
    const loginConfig = await loadBackendNyxIDLoginConfig();
    const redirectUri = this.config.redirectUri;
    const scope = loginConfig.scope;
    const flow = readAuthFlow(options.flow);
    const returnTo = resolveReturnToForFlow(flow, options.returnTo);

    const pending: PendingAuthState = {
      state,
      codeVerifier,
      redirectUri,
      scope,
      returnTo,
      clientId: loginConfig.clientId,
      flow,
    };
    this.storage.setItem(this.resolvePendingKey(loginConfig), JSON.stringify(pending));

    const url = new URL(`${loginConfig.baseUrl}/oauth/authorize`);
    url.searchParams.set('response_type', 'code');
    url.searchParams.set('client_id', loginConfig.clientId);
    url.searchParams.set('redirect_uri', redirectUri);
    url.searchParams.set('scope', scope);
    url.searchParams.set('code_challenge', codeChallenge);
    url.searchParams.set('code_challenge_method', 'S256');
    url.searchParams.set('state', state);
    if (flow === "serviceAccessReview") {
      url.searchParams.set('prompt', 'consent');
    }

    window.location.assign(url.toString());
  }

  private resolvePendingKey(loginConfig: NyxIDBackendLoginConfig): string {
    return `${PENDING_KEY_PREFIX}${loginConfig.clientId}`;
  }

  private loadPendingState(state: string): {
    readonly key: string;
    readonly pending: PendingAuthState;
  } | null {
    const candidateKeys = new Set<string>();

    for (let index = 0; index < this.storage.length; index += 1) {
      const key = this.storage.key(index);
      if (!key?.startsWith(PENDING_KEY_PREFIX)) {
        continue;
      }

      candidateKeys.add(key);
    }

    for (const key of candidateKeys) {
      const raw = this.storage.getItem(key);
      if (!raw) {
        continue;
      }

      const pending = JSON.parse(raw) as PendingAuthState;
      if (pending.state === state) {
        return { key, pending };
      }
    }

    return null;
  }

  async handleRedirectCallback(
    currentUrl = window.location.href,
  ): Promise<AuthCallbackResult> {
    const callback = new URL(currentUrl);
    const oauthError = callback.searchParams.get('error');
    const state = callback.searchParams.get('state');
    const storedPending = state ? this.loadPendingState(state) : null;
    const pendingFlow = readAuthFlow(storedPending?.pending.flow);
    const pendingReturnTo = resolveReturnToForFlow(
      pendingFlow,
      storedPending?.pending.returnTo,
    );
    if (oauthError) {
      if (storedPending) {
        this.storage.removeItem(storedPending.key);
      }

      throw new NyxIDAuthCallbackError(
        describeOAuthCallbackError(
          oauthError,
          callback.searchParams.get('error_description'),
          pendingFlow,
        ),
        {
          flow: pendingFlow,
          returnTo: pendingReturnTo,
        },
      );
    }

    const code = callback.searchParams.get('code');
    if (!code || !state) {
      throw new Error('Missing authorization code or state');
    }

    if (!storedPending) {
      throw new Error('Missing PKCE state in storage');
    }
    const { key: pendingKey, pending } = storedPending;
    if (pending.state !== state) {
      this.storage.removeItem(pendingKey);
      throw new Error('State mismatch');
    }

    try {
      const flow = readAuthFlow(pending.flow);
      const result = await finalizeBackendNyxIDLogin({
        code,
        codeVerifier: pending.codeVerifier,
        redirectUri: pending.redirectUri,
        ...(flow === "serviceAccessReview"
          ? { serviceAccessReview: true }
          : {}),
      });
      const { session } = result;

      persistAuthSession(session);
      this.storage.removeItem(pendingKey);

      return {
        session,
        returnTo: resolveReturnToForFlow(flow, pending.returnTo),
        flow,
      };
    } catch (error) {
      this.storage.removeItem(pendingKey);
      throw error;
    }
  }
}

export function hasRestorableAuthSession(): boolean {
  const session = readStoredAuthSession();
  return Boolean(
    session &&
      (hasActiveAccessToken(session.tokens) || session.tokens.refreshToken),
  );
}

export async function ensureActiveAuthSession(
  config = getNyxIDRuntimeConfig(),
): Promise<NyxIDAuthSession | null> {
  if (!config.enabled) {
    clearStoredAuthSession();
    return null;
  }

  const activeSession = loadStoredAuthSession();
  if (activeSession) {
    return activeSession;
  }

  const expiredSession = readStoredAuthSession();
  const refreshToken = expiredSession?.tokens.refreshToken;
  if (!expiredSession || !refreshToken) {
    clearStoredAuthSession();
    return null;
  }

  if (pendingRefreshPromise) {
    return pendingRefreshPromise;
  }

  pendingRefreshPromise = refreshStoredAuthSession(expiredSession, refreshToken)
    .catch(() => {
      clearStoredAuthSession();
      return null;
    })
    .finally(() => {
      pendingRefreshPromise = null;
    });

  return pendingRefreshPromise;
}

async function refreshStoredAuthSession(
  expiredSession: NyxIDAuthSession,
  refreshToken: string,
): Promise<NyxIDAuthSession | null> {
  const loginConfig = await loadBackendNyxIDLoginConfig();
  const refreshedTokens = await refreshNyxIDTokenSet({
    baseUrl: loginConfig.baseUrl,
    clientId: loginConfig.clientId,
    refreshToken,
  });
  const currentSession = readStoredAuthSession();
  if (currentSession?.tokens.refreshToken !== refreshToken) {
    return loadStoredAuthSession();
  }

  const refreshedSession: NyxIDAuthSession = {
    ...expiredSession,
    tokens: {
      ...expiredSession.tokens,
      ...refreshedTokens,
    },
  };

  persistAuthSession(refreshedSession);
  return refreshedSession;
}
