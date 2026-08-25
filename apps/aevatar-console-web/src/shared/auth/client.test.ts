import { TextEncoder } from "node:util";
import {
  ensureActiveAuthSession,
  hasRestorableAuthSession,
  NyxIDAuthClient,
  SERVICE_ACCESS_REVIEW_RETURN_TO,
  type NyxIDAuthCallbackError,
  type NyxIDAuthCallbackErrorReason,
} from "./client";
import type { NyxIDRuntimeConfig } from "./config";
import { loadStoredAuthSession } from "./session";

const runtimeConfig: NyxIDRuntimeConfig = {
  enabled: true,
  baseUrl: "https://nyx.example",
  clientId: "console-client-1",
  redirectUri: "http://localhost:8000/auth/callback",
  scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
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
  const originalLocationDescriptor = Object.getOwnPropertyDescriptor(window, "location");

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
    if (originalLocationDescriptor) {
      Object.defineProperty(window, "location", originalLocationDescriptor);
    }
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it("starts authorize entirely from frontend runtime config", async () => {
    const assign = installLocationAssignSpy();
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
      returnTo: "/scopes/scope-1/teams",
    });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(assign).toHaveBeenCalledTimes(1);
    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.origin + authorizeUrl.pathname).toBe(
      "https://nyx.example/oauth/authorize",
    );
    expect(authorizeUrl.searchParams.get("client_id")).toBe("console-client-1");
    expect(authorizeUrl.searchParams.get("redirect_uri")).toBe(
      "http://localhost:8000/auth/callback",
    );
    expect(authorizeUrl.searchParams.get("scope")).toBe(
      "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
    );
    expect(authorizeUrl.searchParams.get("prompt")).toBeNull();
    expect(authorizeUrl.searchParams.getAll("resource")).toEqual([]);

    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:console-client-1",
      ) ?? "{}",
    );
    expect(pending).toEqual(
      expect.objectContaining({
        clientId: "console-client-1",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: "/scopes/scope-1/teams",
        scope: "openid profile email offline_access urn:nyxid:scope:broker_binding proxy",
        state: authorizeUrl.searchParams.get("state"),
        flow: "signIn",
      }),
    );
  });

  it("starts service access review with consent prompt and account return default", async () => {
    const assign = installLocationAssignSpy();
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
      flow: "serviceAccessReview",
    });

    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.searchParams.get("prompt")).toBe("consent");
    expect(authorizeUrl.searchParams.getAll("resource")).toEqual([]);
    expect(fetchMock).not.toHaveBeenCalled();

    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:console-client-1",
      ) ?? "{}",
    );
    expect(pending).toEqual(
      expect.objectContaining({
        flow: "serviceAccessReview",
        returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
        state: authorizeUrl.searchParams.get("state"),
      }),
    );
  });

  it("starts service access review with exact resources and a caller return", async () => {
    const assign = installLocationAssignSpy();

    await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
      flow: "serviceAccessReview",
      resources: ["https://nyx.example/api/v1/proxy/s/api-github"],
      returnTo: "/chat?conversationId=chatc-alpha&accessReview=action-alpha",
    });

    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.searchParams.get("prompt")).toBe("consent");
    expect(authorizeUrl.searchParams.getAll("resource")).toEqual([
      "https://nyx.example/api/v1/proxy/s/api-github",
    ]);

    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:console-client-1",
      ) ?? "{}",
    );
    expect(pending).toEqual(
      expect.objectContaining({
        flow: "serviceAccessReview",
        returnTo: "/chat?conversationId=chatc-alpha&accessReview=action-alpha",
      }),
    );
  });

  it("forces consent while preserving a canonical workflow return URL", async () => {
    const assign = installLocationAssignSpy();

    await new NyxIDAuthClient(runtimeConfig).loginWithRedirect({
      prompt: "consent",
      returnTo: "/scopes/scope-1/teams/team-1/members/m-alpha/automations",
    });

    const authorizeUrl = new URL(assign.mock.calls[0][0]);
    expect(authorizeUrl.searchParams.get("prompt")).toBe("consent");
    const pending = JSON.parse(
      window.localStorage.getItem(
        "aevatar-console:nyxid:pending:console-client-1",
      ) ?? "{}",
    );
    expect(pending.returnTo).toBe(
      "/scopes/scope-1/teams/team-1/members/m-alpha/automations",
    );
    expect(pending.flow).toBe("signIn");
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
      flow: "signIn",
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

  it("finalizes service access review with the stored flow flag", async () => {
    const pendingKey = "aevatar-console:nyxid:pending:broker-client-1";
    window.localStorage.setItem(
      pendingKey,
      JSON.stringify({
        clientId: "broker-client-1",
        codeVerifier: "pkce-verifier",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
        scope: "openid urn:nyxid:scope:broker_binding proxy",
        state: "state-1",
        flow: "serviceAccessReview",
      }),
    );
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tokens: {
          accessToken: "review-access-token",
          refreshToken: "review-refresh-token",
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
      flow: "serviceAccessReview",
      returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
      session: expect.objectContaining({
        tokens: expect.objectContaining({
          accessToken: "review-access-token",
          refreshToken: "review-refresh-token",
        }),
      }),
    });

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/auth/nyxid/finalize");
    expect(JSON.parse(String(init.body))).toEqual({
      code: "auth-code",
      codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback",
      serviceAccessReview: true,
    });
    expect(window.localStorage.getItem(pendingKey)).toBeNull();
    expect(loadStoredAuthSession()?.tokens.accessToken).toBe("review-access-token");
  });

  it.each<[number, string, NyxIDAuthCallbackErrorReason]>([
    [409, "required_service_access_missing", "requiredServiceAccessMissing"],
    [503, "issued_binding_invalid", "issuedBindingInvalid"],
    [503, "issued_binding_probe_failed", "issuedBindingProbeFailed"],
    [503, "binding_probe_failed", "bindingProbeFailed"],
  ])("preserves review retry state for backend error %s %s", async (status, code, reason) => {
    const pendingKey = "aevatar-console:nyxid:pending:broker-client-1";
    window.localStorage.setItem(
      pendingKey,
      JSON.stringify({
        clientId: "broker-client-1",
        codeVerifier: "pkce-verifier",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
        scope: "openid urn:nyxid:scope:broker_binding proxy",
        state: "state-1",
        flow: "serviceAccessReview",
      }),
    );
    window.localStorage.setItem(
      "aevatar-console:nyxid:session",
      JSON.stringify({
        tokens: {
          accessToken: "existing-access-token",
          tokenType: "Bearer",
          expiresIn: 3600,
          expiresAt: Date.now() + 3_600_000,
        },
        user: {
          sub: "owner-user-1",
        },
      }),
    );
    const fetchMock = jest.fn().mockResolvedValue({
      ok: false,
      status,
      statusText: "Service Unavailable",
      text: async () =>
        JSON.stringify({
          error: code,
          detail: "NyxID binding could not be verified.",
        }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      new NyxIDAuthClient(runtimeConfig).handleRedirectCallback(
        "http://localhost:8000/auth/callback?code=auth-code&state=state-1",
      ),
    ).rejects.toMatchObject({
      name: "NyxIDAuthCallbackError",
      flow: "serviceAccessReview",
      reason,
      returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
      message: code,
    } satisfies Partial<NyxIDAuthCallbackError>);

    expect(window.localStorage.getItem(pendingKey)).toBeNull();
    expect(loadStoredAuthSession()?.tokens.accessToken).toBe(
      "existing-access-token",
    );
  });

  it("keeps ordinary sign-in failures out of service access review semantics", async () => {
    const pendingKey = "aevatar-console:nyxid:pending:broker-client-1";
    window.localStorage.setItem(pendingKey, JSON.stringify({
      clientId: "broker-client-1", codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback", returnTo: "/runtime/runs",
      scope: "openid proxy", state: "state-1", flow: "signIn",
    }));
    global.fetch = jest.fn().mockResolvedValue({
      ok: false, status: 502, statusText: "Bad Gateway",
      text: async () => JSON.stringify({ error: "token_exchange_failed", detail: "Login failed." }),
    } as Response) as typeof global.fetch;

    await expect(new NyxIDAuthClient(runtimeConfig).handleRedirectCallback(
      "http://localhost:8000/auth/callback?code=auth-code&state=state-1",
    )).rejects.toMatchObject({
      flow: "signIn", message: "token_exchange_failed",
      name: "NyxIDAuthCallbackError", reason: "signInFailed", returnTo: "/runtime/runs",
    } satisfies Partial<NyxIDAuthCallbackError>);
  });

  it("preserves the existing Studio session when service access review is denied", async () => {
    const pendingKey = "aevatar-console:nyxid:pending:broker-client-1";
    window.localStorage.setItem(
      pendingKey,
      JSON.stringify({
        clientId: "broker-client-1",
        codeVerifier: "pkce-verifier",
        redirectUri: "http://localhost:8000/auth/callback",
        returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
        scope: "openid urn:nyxid:scope:broker_binding proxy",
        state: "state-1",
        flow: "serviceAccessReview",
      }),
    );
    window.localStorage.setItem(
      "aevatar-console:nyxid:session",
      JSON.stringify({
        tokens: {
          accessToken: "existing-access-token",
          tokenType: "Bearer",
          expiresIn: 3600,
          expiresAt: Date.now() + 3_600_000,
        },
        user: {
          sub: "owner-user-1",
        },
      }),
    );
    const fetchMock = jest.fn();
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      new NyxIDAuthClient(runtimeConfig).handleRedirectCallback(
        "http://localhost:8000/auth/callback?error=access_denied&state=state-1",
      ),
    ).rejects.toMatchObject({
      name: "NyxIDAuthCallbackError",
      flow: "serviceAccessReview",
      reason: "oauthDenied",
      returnTo: SERVICE_ACCESS_REVIEW_RETURN_TO,
      message: "OAuth error: access_denied",
    } satisfies Partial<NyxIDAuthCallbackError>);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(window.localStorage.getItem(pendingKey)).toBeNull();
    expect(loadStoredAuthSession()?.tokens.accessToken).toBe(
      "existing-access-token",
    );
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
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe("https://nyx.example/oauth/token");
    expect(String(fetchMock.mock.calls[0][1]?.body)).toBe(
      "grant_type=refresh_token&refresh_token=refresh-token-1&client_id=console-client-1",
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
