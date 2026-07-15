import { TextEncoder } from "node:util";
import {
  ensureActiveAuthSession,
  hasRestorableAuthSession,
  NyxIDAuthClient,
} from "./client";
import type { NyxIDRuntimeConfig } from "./config";
import { loadStoredAuthSession } from "./session";

const runtimeConfig: NyxIDRuntimeConfig = {
  enabled: true,
  baseUrl: "https://legacy-console-client.example",
  clientId: "console-client-1",
  redirectUri: "http://localhost:8000/auth/callback",
  scope: "openid profile email",
  defaultServiceSlugs: [
    "aevatar",
    "ornn-api",
    "chrono-llm-public",
    "chrono-sandbox",
  ],
};

function installLocationAssignSpy() {
  const assign = jest.fn();
  Object.defineProperty(window, "location", {
    configurable: true,
    value: {
      ...window.location,
      assign,
      href: window.location.href,
      origin: window.location.origin,
    },
  });
  return assign;
}

describe("NyxIDAuthClient", () => {
  const originalFetch = global.fetch;
  const originalLocation = window.location;

  beforeEach(() => {
    window.localStorage.clear();
    window.history.replaceState({}, "", "/login");
    jest.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    Object.defineProperty(globalThis, "TextEncoder", {
      configurable: true,
      value: TextEncoder,
    });
    Object.defineProperty(globalThis, "crypto", {
      configurable: true,
      value: {
        getRandomValues: (array: Uint8Array) => {
          array.fill(7);
          return array;
        },
        subtle: {
          digest: jest.fn(async () => new Uint8Array(32).fill(9).buffer),
        },
      },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    Object.defineProperty(window, "location", {
      configurable: true,
      value: originalLocation,
    });
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it("starts authorize with the backend broker OAuth client config", async () => {
    const assign = installLocationAssignSpy();
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        baseUrl: "https://nyx.example",
        clientId: "broker-client-1",
        scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
        redirectUri: "https://backend.example/auth/callback",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
      returnTo: "/scopes/scope-1/teams",
    });

    expect(fetchMock).toHaveBeenCalledWith("/api/auth/nyxid/config", {
      headers: {
        Accept: "application/json",
      },
    });
    expect(assign).toHaveBeenCalledTimes(1);
    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.origin + authorizeUrl.pathname).toBe(
      "https://nyx.example/oauth/authorize",
    );
    expect(authorizeUrl.searchParams.get("client_id")).toBe("broker-client-1");
    expect(authorizeUrl.searchParams.get("redirect_uri")).toBe(
      "http://localhost:8000/auth/callback",
    );
    expect(authorizeUrl.searchParams.get("scope")).toBe(
      "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
    );
    expect(authorizeUrl.searchParams.getAll("resource")).toEqual([
      "https://nyx.example/api/v1/proxy/s/aevatar",
      "https://nyx.example/api/v1/proxy/s/ornn-api",
      "https://nyx.example/api/v1/proxy/s/chrono-llm-public",
      "https://nyx.example/api/v1/proxy/s/chrono-sandbox",
    ]);

    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:broker-client-1",
      ) ?? "{}",
    );
    expect(pending).toEqual(
      expect.objectContaining({
        clientId: "broker-client-1",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: "/scopes/scope-1/teams",
        scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
        state: authorizeUrl.searchParams.get("state"),
      }),
    );
  });

  it("finalizes callback through the backend without a browser token exchange", async () => {
    const pendingKey = "aevatar-console:nyxid:pending:broker-client-1";
    window.localStorage.setItem(
      pendingKey,
      JSON.stringify({
        clientId: "broker-client-1",
        codeVerifier: "pkce-verifier",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: "/scopes/scope-1/teams",
        scope: "openid urn:nyxid:scope:broker_binding proxy",
        state: "state-1",
      }),
    );
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tokens: {
          accessToken: "access-token",
          refreshToken: "refresh-token",
          tokenType: "Bearer",
          expiresIn: 1800,
          scope: "openid profile email offline_access proxy",
        },
        user: {
          sub: "owner-user-1",
        },
        bindingDispatchAccepted: true,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      new NyxIDAuthClient(runtimeConfig).handleRedirectCallback(
        "http://localhost:8000/auth/callback?code=auth-code&state=state-1",
      ),
    ).resolves.toEqual({
      returnTo: "/scopes/scope-1/teams",
      session: expect.objectContaining({
        tokens: expect.objectContaining({
          accessToken: "access-token",
          refreshToken: "refresh-token",
        }),
        user: {
          sub: "owner-user-1",
          email: undefined,
          email_verified: undefined,
          groups: undefined,
          name: undefined,
          permissions: undefined,
          picture: undefined,
          roles: undefined,
        },
      }),
    });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/auth/nyxid/finalize");
    expect(JSON.parse(String(init.body))).toEqual({
      code: "auth-code",
      codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback",
    });
    expect(String(input)).not.toContain("/oauth/token");
    expect(window.localStorage.getItem(pendingKey)).toBeNull();
    expect(loadStoredAuthSession()?.tokens.accessToken).toBe("access-token");
    expect(loadStoredAuthSession()?.tokens.refreshToken).toBe("refresh-token");
  });

  it("refreshes an expired local session before returning it as active", async () => {
    window.localStorage.setItem(
      "aevatar-console:nyxid:session",
      JSON.stringify({
        tokens: {
          accessToken: "expired-token",
          tokenType: "Bearer",
          expiresIn: 3600,
          expiresAt: Date.now() - 1,
          refreshToken: "refresh-token-1",
          idToken: "old-id-token",
          scope: "openid profile email offline_access proxy",
        },
        user: {
          sub: "owner-user-1",
          email: "owner@example.com",
        },
      }),
    );
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          baseUrl: "https://nyx.example/",
          clientId: "broker-client-1",
          scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          access_token: "access-token-2",
          refresh_token: "refresh-token-2",
          token_type: "Bearer",
          expires_in: 300,
          id_token: "id-token-2",
          scope: "openid profile email offline_access proxy",
        }),
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(ensureActiveAuthSession(runtimeConfig)).resolves.toEqual({
      tokens: {
        accessToken: "access-token-2",
        refreshToken: "refresh-token-2",
        tokenType: "Bearer",
        expiresIn: 300,
        expiresAt: Date.now() + 300_000,
        idToken: "id-token-2",
        scope: "openid profile email offline_access proxy",
      },
      user: {
        sub: "owner-user-1",
        email: "owner@example.com",
      },
    });

    expect(hasRestorableAuthSession()).toBe(true);
    expect(loadStoredAuthSession()?.tokens.accessToken).toBe("access-token-2");
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock.mock.calls[0][0]).toBe("/api/auth/nyxid/config");
    expect(fetchMock.mock.calls[1][0]).toBe("https://nyx.example/oauth/token");
    expect(String(fetchMock.mock.calls[1][1]?.body)).toBe(
      "grant_type=refresh_token&refresh_token=refresh-token-1&client_id=broker-client-1",
    );
  });

  it("clears expired local sessions when refresh token issuance is unavailable", async () => {
    window.localStorage.setItem(
      "aevatar-console:nyxid:session",
      JSON.stringify({
        tokens: {
          accessToken: "expired-token",
          tokenType: "Bearer",
          expiresIn: 3600,
          expiresAt: Date.now() - 1,
        },
        user: {
          sub: "owner-user-1",
        },
      }),
    );
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    expect(hasRestorableAuthSession()).toBe(false);
    await expect(ensureActiveAuthSession(runtimeConfig)).resolves.toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(window.localStorage.getItem("aevatar-console:nyxid:session")).toBeNull();
  });

  it("does not restore a stale session if local storage changes during refresh", async () => {
    const sessionKey = "aevatar-console:nyxid:session";
    window.localStorage.setItem(
      sessionKey,
      JSON.stringify({
        tokens: {
          accessToken: "expired-token",
          tokenType: "Bearer",
          expiresIn: 3600,
          expiresAt: Date.now() - 1,
          refreshToken: "refresh-token-1",
        },
        user: {
          sub: "owner-user-1",
        },
      }),
    );
    const fetchMock = jest
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => ({
          baseUrl: "https://nyx.example/",
          clientId: "broker-client-1",
          scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
        }),
      } as Response)
      .mockResolvedValueOnce({
        ok: true,
        status: 200,
        json: async () => {
          window.localStorage.removeItem(sessionKey);
          return {
            access_token: "access-token-2",
            refresh_token: "refresh-token-2",
            token_type: "Bearer",
            expires_in: 300,
          };
        },
      } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(ensureActiveAuthSession(runtimeConfig)).resolves.toBeNull();
    expect(window.localStorage.getItem(sessionKey)).toBeNull();
  });
});
