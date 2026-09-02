import {
  finalizeBackendNyxIDLogin,
  refreshNyxIDTokenSet,
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

  it("finalizes an authorization code through the backend and preserves accepted-dispatch semantics", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tokens: {
          accessToken: "access-token",
          refreshToken: "refresh-token",
          tokenType: "Bearer",
          expiresIn: 1800,
          idToken: "id-token",
          scope: "openid profile email offline_access proxy",
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
          refreshToken: "refresh-token",
          idToken: "id-token",
          scope: "openid profile email offline_access proxy",
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

  it("sends the service access review finalization flag only when requested", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tokens: {
          accessToken: "access-token",
          tokenType: "Bearer",
          expiresIn: 1800,
        },
        user: {
          sub: "owner-user-1",
        },
        bindingDispatchAccepted: true,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await finalizeBackendNyxIDLogin({
      code: "auth-code",
      codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback",
      serviceAccessReview: true,
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({
      code: "auth-code",
      codeVerifier: "pkce-verifier",
      redirectUri: "http://localhost:8000/auth/callback",
      serviceAccessReview: true,
    });
  });

  it.each([
    [409, "required_service_access_missing", "Keep required services selected."],
    [503, "issued_binding_invalid", "The issued binding was unavailable."],
    [503, "issued_binding_probe_failed", "The issued binding could not be verified."],
    [503, "binding_probe_failed", "The current binding could not be verified."],
  ])(
    "preserves typed service access review backend error %s %s",
    async (status, code, detail) => {
      const fetchMock = jest.fn().mockResolvedValue({
        ok: false,
        status,
        statusText: status === 409 ? "Conflict" : "Service Unavailable",
        text: async () =>
          JSON.stringify({
            error: code,
            detail,
          }),
      } as Response);
      global.fetch = fetchMock as typeof global.fetch;

      await expect(
        finalizeBackendNyxIDLogin({
          code: "auth-code",
          codeVerifier: "pkce-verifier",
          redirectUri: "http://localhost:8000/auth/callback",
          serviceAccessReview: true,
        }),
      ).rejects.toMatchObject({
        code,
        message: code,
        name: "NyxIDLoginFinalizationError",
        status,
      });
    },
  );

  it("refreshes a NyxID token set through the OAuth refresh grant", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
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

    await expect(
      refreshNyxIDTokenSet({
        baseUrl: "https://nyx.example/",
        clientId: "broker-client-1",
        refreshToken: "refresh-token-1",
      }),
    ).resolves.toEqual({
      accessToken: "access-token-2",
      refreshToken: "refresh-token-2",
      tokenType: "Bearer",
      expiresIn: 300,
      expiresAt: 1_700_000_000_000 + 300_000,
      idToken: "id-token-2",
      scope: "openid profile email offline_access proxy",
    });

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("https://nyx.example/oauth/token");
    expect(init.method).toBe("POST");
    expect(init.headers).toEqual({
      Accept: "application/json",
      "Content-Type": "application/x-www-form-urlencoded",
    });
    expect(String(init.body)).toBe(
      "grant_type=refresh_token&refresh_token=refresh-token-1&client_id=broker-client-1",
    );
  });

  it("keeps the existing refresh token when NyxID refresh does not rotate it", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        accessToken: "access-token-2",
        tokenType: "Bearer",
        expiresIn: 300,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      refreshNyxIDTokenSet({
        baseUrl: "https://nyx.example",
        clientId: "broker-client-1",
        refreshToken: "refresh-token-1",
      }),
    ).resolves.toEqual(
      expect.objectContaining({
        accessToken: "access-token-2",
        refreshToken: "refresh-token-1",
      }),
    );
  });
});
