import { persistAuthSession } from "@/shared/auth/session";
import { automationApiKeysApi } from "./automationApiKeys";

describe("automationApiKeysApi", () => {
  const originalFetch = global.fetch;

  beforeEach(() => {
    window.localStorage.clear();
    jest.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    persistAuthSession({
      tokens: {
        accessToken: "browser-oauth-access-token",
        tokenType: "Bearer",
        expiresIn: 3600,
        expiresAt: Date.now() + 3_600_000,
      },
      user: {
        sub: "user-1",
      },
    });
  });

  afterEach(() => {
    global.fetch = originalFetch;
    jest.restoreAllMocks();
    window.localStorage.clear();
  });

  it("lists automation API key metadata without returning one-time secret material", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        items: [
          {
            apiKeyId: "ak-alpha",
            displayName: "Daily workflow automation",
            scopeId: "scope-1",
            status: "active",
            keySuffix: "9af0",
            createdAt: "2026-07-09T08:00:00Z",
            lastUsedAt: null,
            expiresAt: null,
            revokedAt: null,
            allowedMemberId: "m-alpha",
            allowedServiceId: "svc-alpha",
            credentialRef: {
              subject: {
                platform: "nyxid",
                tenant: "scope-1",
                externalUserId: "ak-alpha",
              },
              scope: "proxy",
            },
            rawKey: "should-not-be-decoded-from-list",
          },
        ],
        totalCount: 1,
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(automationApiKeysApi.list(" scope-1 ")).resolves.toEqual({
      items: [
        {
          apiKeyId: "ak-alpha",
          displayName: "Daily workflow automation",
          scopeId: "scope-1",
          status: "active",
          keySuffix: "9af0",
          createdAt: "2026-07-09T08:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          revokedAt: null,
          allowedMemberId: "m-alpha",
          allowedServiceId: "svc-alpha",
          credentialRef: {
            subject: {
              platform: "nyxid",
              tenant: "scope-1",
              externalUserId: "ak-alpha",
            },
            scope: "proxy",
          },
        },
      ],
      totalCount: 1,
    });

    const [input, init] = fetchMock.mock.calls[0] as [
      string,
      RequestInit | undefined,
    ];
    expect(input).toBe("/api/scopes/scope-1/automation-api-keys");
    expect(new Headers(init?.headers).get("Authorization")).toBe(
      "Bearer browser-oauth-access-token",
    );
  });

  it("creates a scoped automation API key and keeps the raw key as a one-time response value", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 201,
      json: async () => ({
        apiKey: {
          apiKeyId: "ak-alpha",
          displayName: "Daily workflow automation",
          scopeId: "scope-1",
          status: "active",
          keySuffix: "9af0",
          createdAt: "2026-07-09T08:00:00Z",
          lastUsedAt: null,
          expiresAt: null,
          revokedAt: null,
          allowedMemberId: "m-alpha",
          allowedServiceId: "svc-alpha",
          credentialRef: {
            subject: {
              platform: "nyxid",
              tenant: "scope-1",
              externalUserId: "ak-alpha",
            },
            scope: "proxy",
          },
        },
        rawKey: "aevatar_automation_secret_once",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      automationApiKeysApi.create({
        scopeId: " scope-1 ",
        displayName: " Daily workflow automation ",
        allowedMemberId: " m-alpha ",
        allowedServiceId: " svc-alpha ",
        scopes: [" proxy ", "", "workflow:schedule"],
      }),
    ).resolves.toEqual({
      apiKey: expect.objectContaining({
        apiKeyId: "ak-alpha",
        allowedMemberId: "m-alpha",
        allowedServiceId: "svc-alpha",
      }),
      credentialRef: {
        subject: {
          platform: "nyxid",
          tenant: "scope-1",
          externalUserId: "ak-alpha",
        },
        scope: "proxy",
      },
      rawKey: "aevatar_automation_secret_once",
    });

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/scopes/scope-1/automation-api-keys");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      displayName: "Daily workflow automation",
      allowedMemberId: "m-alpha",
      allowedServiceId: "svc-alpha",
      scopes: ["proxy", "workflow:schedule"],
    });
    expect(window.localStorage.getItem("aevatar_automation_secret_once")).toBeNull();
    expect(JSON.stringify(window.localStorage)).not.toContain(
      "aevatar_automation_secret_once",
    );
  });

  it("reads automation credential status for a member and service without raw key material", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        status: "active",
        apiKey: {
          apiKeyId: "ak-alpha",
          displayName: "Daily workflow automation",
          scopeId: "scope-1",
          status: "active",
          keySuffix: "9af0",
          createdAt: "2026-07-09T08:00:00Z",
          lastUsedAt: "2026-07-09T08:30:00Z",
          expiresAt: null,
          revokedAt: null,
          allowedMemberId: "m-alpha",
          allowedServiceId: "svc-alpha",
          credentialRef: {
            subject: {
              platform: "nyxid",
              tenant: "scope-1",
              externalUserId: "ak-alpha",
            },
            scope: "proxy",
          },
        },
        credentialRef: {
          subject: {
            platform: "nyxid",
            tenant: "scope-1",
            externalUserId: "ak-alpha",
          },
          scope: "proxy",
        },
        rawKey: "should-not-be-decoded-from-status",
      }),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      automationApiKeysApi.getStatus({
        scopeId: "scope-1",
        memberId: "m-alpha",
        serviceId: "svc-alpha",
      }),
    ).resolves.toEqual({
      status: "active",
      apiKey: expect.objectContaining({
        apiKeyId: "ak-alpha",
        lastUsedAt: "2026-07-09T08:30:00Z",
      }),
      credentialRef: {
        subject: {
          platform: "nyxid",
          tenant: "scope-1",
          externalUserId: "ak-alpha",
        },
        scope: "proxy",
      },
    });

    expect(fetchMock.mock.calls[0][0]).toBe(
      "/api/scopes/scope-1/automation-api-keys/status?memberId=m-alpha&serviceId=svc-alpha",
    );
  });

  it("revokes a user-created automation API key by key id", async () => {
    const fetchMock = jest.fn().mockResolvedValue({
      ok: true,
      status: 204,
      json: async () => ({}),
    } as Response);
    global.fetch = fetchMock as typeof global.fetch;

    await expect(
      automationApiKeysApi.revoke({
        scopeId: " scope-1 ",
        apiKeyId: " ak-alpha ",
      }),
    ).resolves.toBeUndefined();

    const [input, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(input).toBe("/api/scopes/scope-1/automation-api-keys/ak-alpha");
    expect(init.method).toBe("DELETE");
  });
});
