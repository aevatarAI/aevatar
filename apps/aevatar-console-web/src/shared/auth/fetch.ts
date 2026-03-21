import { ensureActiveAuthSession } from './client';

export async function authFetch(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<Response> {
  const accessToken = (await ensureActiveAuthSession())?.tokens.accessToken;
  if (!accessToken) {
    return init === undefined ? fetch(input) : fetch(input, init);
  }

  const headers = new Headers(init?.headers);

  if (!headers.has('Authorization')) {
    headers.set('Authorization', `Bearer ${accessToken}`);
  }

  return fetch(input, {
    ...init,
    headers,
  });
}
