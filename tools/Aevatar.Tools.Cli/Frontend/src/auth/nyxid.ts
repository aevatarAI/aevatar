/**
 * NyxID OAuth 2.0 PKCE client for CLI frontend.
 * Ported from apps/aevatar-console-web/src/shared/auth/.
 */

const NYXID_BASE_URL = (import.meta as any).env?.VITE_NYXID_BASE_URL?.trim() || 'https://nyx.chrono-ai.fun';
/** NyxID REST API base URL (service management, proxy, etc.) */
export const NYXID_API_URL = (import.meta as any).env?.VITE_NYXID_API_URL?.trim() || 'https://nyx-api.chrono-ai.fun';
const NYXID_CLIENT_ID = (import.meta as any).env?.VITE_NYXID_CLIENT_ID?.trim() || '37a93189-2734-406e-bca1-7dbdf25c5a53';
const NYXID_SCOPE = (import.meta as any).env?.VITE_NYXID_SCOPE?.trim() || 'openid profile email proxy';
const SESSION_KEY = 'aevatar-cli:nyxid:session';
const PENDING_KEY = 'aevatar-cli:nyxid:pending';
const CLOCK_SKEW_MS = 30_000;

export interface NyxIDTokenSet {
  accessToken: string;
  tokenType: string;
  expiresIn: number;
  expiresAt: number;
  refreshToken?: string;
  idToken?: string;
  scope?: string;
}

export interface NyxIDUserInfo {
  sub: string;
  email?: string;
  name?: string;
  picture?: string;
  roles?: string[];
  groups?: string[];
}

export interface NyxIDSession {
  tokens: NyxIDTokenSet;
  user: NyxIDUserInfo;
}

interface PendingAuth {
  state: string;
  codeVerifier: string;
  redirectUri: string;
  returnTo: string;
}

// ─── PKCE helpers ───

function base64UrlEncode(input: Uint8Array): string {
  let binary = '';
  for (const byte of input) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function randomUrlSafe(bytes = 32): string {
  const data = new Uint8Array(bytes);
  crypto.getRandomValues(data);
  return base64UrlEncode(data);
}

async function sha256Base64Url(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return base64UrlEncode(new Uint8Array(digest));
}

// ─── Session storage ───

export function loadSession(): NyxIDSession | null {
  try {
    const raw = localStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    const session = JSON.parse(raw) as NyxIDSession;
    if (session.tokens.expiresAt - CLOCK_SKEW_MS > Date.now()) return session;
    if (session.tokens.refreshToken) return session; // can refresh
    localStorage.removeItem(SESSION_KEY);
    return null;
  } catch {
    return null;
  }
}

export function getActiveSession(): NyxIDSession | null {
  const session = loadSession();
  if (!session) return null;
  if (session.tokens.expiresAt - CLOCK_SKEW_MS > Date.now()) return session;
  return null; // expired, need refresh
}

export function persistSession(session: NyxIDSession): void {
  localStorage.setItem(SESSION_KEY, JSON.stringify(session));
}

export function clearSession(): void {
  localStorage.removeItem(SESSION_KEY);
}

export function getAccessToken(): string | undefined {
  return getActiveSession()?.tokens.accessToken;
}

// ─── OAuth flows ───

function getRedirectUri(): string {
  return `${window.location.origin}/auth/callback`;
}

export async function loginWithRedirect(returnTo = '/'): Promise<void> {
  const codeVerifier = randomUrlSafe(48);
  const codeChallenge = await sha256Base64Url(codeVerifier);
  const state = randomUrlSafe(24);
  const redirectUri = getRedirectUri();

  const pending: PendingAuth = { state, codeVerifier, redirectUri, returnTo };
  localStorage.setItem(PENDING_KEY, JSON.stringify(pending));

  const url = new URL(`${NYXID_BASE_URL}/oauth/authorize`);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('client_id', NYXID_CLIENT_ID);
  url.searchParams.set('redirect_uri', redirectUri);
  url.searchParams.set('scope', NYXID_SCOPE);
  url.searchParams.set('code_challenge', codeChallenge);
  url.searchParams.set('code_challenge_method', 'S256');
  url.searchParams.set('state', state);

  window.location.assign(url.toString());
}

export async function handleCallback(): Promise<{ session: NyxIDSession; returnTo: string }> {
  const params = new URL(window.location.href).searchParams;
  const error = params.get('error');
  if (error) throw new Error(params.get('error_description') ?? `OAuth error: ${error}`);

  const code = params.get('code');
  const state = params.get('state');
  if (!code || !state) throw new Error('Missing authorization code or state');

  const raw = localStorage.getItem(PENDING_KEY);
  const pending = raw ? JSON.parse(raw) as PendingAuth : null;
  if (!pending) throw new Error('Missing PKCE state');
  if (pending.state !== state) {
    localStorage.removeItem(PENDING_KEY);
    throw new Error('State mismatch');
  }

  const form = new URLSearchParams();
  form.set('grant_type', 'authorization_code');
  form.set('code', code);
  form.set('redirect_uri', pending.redirectUri);
  form.set('client_id', NYXID_CLIENT_ID);
  form.set('code_verifier', pending.codeVerifier);

  const tokenRes = await fetch(`${NYXID_BASE_URL}/oauth/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: form.toString(),
  });

  if (!tokenRes.ok) {
    const payload = await tokenRes.json().catch(() => null);
    localStorage.removeItem(PENDING_KEY);
    throw new Error(`Token exchange failed: ${(payload as any)?.error_description || tokenRes.statusText}`);
  }

  const body = await tokenRes.json() as any;
  const tokens: NyxIDTokenSet = {
    accessToken: body.access_token,
    tokenType: body.token_type,
    expiresIn: body.expires_in,
    expiresAt: Date.now() + body.expires_in * 1000,
    refreshToken: body.refresh_token,
    idToken: body.id_token,
    scope: body.scope,
  };

  const userRes = await fetch(`${NYXID_BASE_URL}/oauth/userinfo`, {
    headers: { Authorization: `Bearer ${tokens.accessToken}` },
  });
  const user: NyxIDUserInfo = userRes.ok ? await userRes.json() : { sub: '' };

  const session: NyxIDSession = { tokens, user };
  persistSession(session);
  localStorage.removeItem(PENDING_KEY);

  return { session, returnTo: pending.returnTo || '/' };
}

export async function refreshSession(session: NyxIDSession): Promise<NyxIDSession | null> {
  if (!session.tokens.refreshToken) return null;

  const form = new URLSearchParams();
  form.set('grant_type', 'refresh_token');
  form.set('refresh_token', session.tokens.refreshToken);

  try {
    const res = await fetch(`${NYXID_BASE_URL}/oauth/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: form.toString(),
    });
    if (!res.ok) return null;

    const body = await res.json() as any;
    const tokens: NyxIDTokenSet = {
      accessToken: body.access_token,
      tokenType: body.token_type,
      expiresIn: body.expires_in,
      expiresAt: Date.now() + body.expires_in * 1000,
      refreshToken: body.refresh_token ?? session.tokens.refreshToken,
      idToken: body.id_token ?? session.tokens.idToken,
      scope: body.scope ?? session.tokens.scope,
    };

    let user = session.user;
    try {
      const userRes = await fetch(`${NYXID_BASE_URL}/oauth/userinfo`, {
        headers: { Authorization: `Bearer ${tokens.accessToken}` },
      });
      if (userRes.ok) user = await userRes.json();
    } catch { /* keep old user */ }

    const refreshed: NyxIDSession = { tokens, user };
    persistSession(refreshed);
    return refreshed;
  } catch {
    return null;
  }
}

/** Check if current URL is the OAuth callback */
export function isAuthCallback(): boolean {
  return window.location.pathname === '/auth/callback' &&
    Boolean(new URLSearchParams(window.location.search).get('code'));
}
