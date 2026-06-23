import {
  finalizeBackendNyxIDLogin,
  loadBackendNyxIDLoginConfig,
} from "./backend";

describe("NyxID backend auth API", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    jest.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
  });

  it("loads the broker OAuth client config used by backend finalization", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        baseUrl: "https://nyx.example/",
        clientId: "broker-client-1",
        redirectUri: "https://dashboard.example/auth/callback",
        scope: "openid urn:nyxid:scope:broker_binding proxy",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(loadBackendNyxIDLoginConfig()).resolves.toEqual({
      baseUrl: "https://nyx.example",
      clientId: "broker-client-1",
      redirectUri: "https://dashboard.example/auth/callback",
      scope: "openid urn:nyxid:scope:broker_binding proxy",
    });

    expect(fetchMock).toHaveBeenCalledWith("/api/auth/nyxid/config", {
      headers: {
        Accept: "application/json",
      },
    });
  });

  it("finalizes an authorization code through the backend and preserves accepted-dispatch semantics", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tokens: {
          accessToken: "access-token",
          tokenType: "Bearer",
          expiresIn: 1800,
          idToken: "id-token",
          scope: "openid profile proxy",
        },
        user: {
          sub: "owner-user-1",
          email: "owner@example.com",
          emailVerified: true,
          roles: ["owner"],
        },
        bindingDispatchAccepted: true,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      finalizeBackendNyxIDLogin({
        code: "auth-code",
        codeVerifier: "pkce-verifier",
        redirectUri: "http://localhost:8000/auth/callback",
      }),
    ).resolves.toEqual({
      bindingDispatchAccepted: true,
      session: {
        tokens: {
          accessToken: "access-token",
          tokenType: "Bearer",
          expiresIn: 1800,
          expiresAt: 1_700_000_000_000 + 1_800_000,
          idToken: "id-token",
          scope: "openid profile proxy",
          refreshToken: undefined,
        },
        user: {
          sub: "owner-user-1",
          email: "owner@example.com",
          email_verified: true,
          name: undefined,
          picture: undefined,
          roles: ["owner"],
          groups: undefined,
          permissions: undefined,
        },
      },
    });

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/auth/nyxid/finalize");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      code: "auth-code",
      codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback",
    });
  });
});
