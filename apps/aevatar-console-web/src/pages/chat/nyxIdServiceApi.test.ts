jest.mock("@/shared/auth/client", () => ({
  ensureActiveAuthSession: jest.fn(),
}));
jest.mock("@/shared/auth/config", () => ({
  getNyxIDRuntimeConfig: jest.fn(() => ({
    enabled: true,
    baseUrl: "https://nyx.example",
  })),
}));

import { ensureActiveAuthSession } from "@/shared/auth/client";
import {
  createNyxIdCatalogKey,
  listNyxIdConnectors,
  matchNewUserServiceId,
} from "./nyxIdServiceApi";

function jsonResponse(payload: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: jest.fn().mockResolvedValue(JSON.stringify(payload)),
  } as unknown as Response;
}

describe("nyxIdServiceApi", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    (ensureActiveAuthSession as jest.Mock).mockResolvedValue({
      tokens: { accessToken: "access-token" },
      user: { sub: "user-alpha" },
    });
  });

  it("lists canonical keys and catalog through the existing NyxID session", async () => {
    jest
      .spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(
        jsonResponse({
            keys: [
              {
                id: "user-service-alpha",
                api_key_id: "api-key-alpha",
                catalog_service_slug: "api-github",
                label: "GitHub",
                endpoint_url: "https://api.github.com",
                is_active: true,
              },
            ],
          })
      )
      .mockResolvedValueOnce(
        jsonResponse({
            entries: [
              {
                slug: "api-github",
                name: "GitHub",
                description: "GitHub API",
                auth_method: "bearer",
              },
            ],
          })
      );

    await expect(listNyxIdConnectors()).resolves.toEqual({
      connected: [
        expect.objectContaining({
          slug: "api-github",
          userServices: [
            expect.objectContaining({
              userServiceId: "user-service-alpha",
              apiKeyId: "api-key-alpha",
            }),
          ],
        }),
      ],
      available: [],
    });
    expect(globalThis.fetch).toHaveBeenNthCalledWith(
      1,
      "https://nyx.example/api/v1/keys",
      { headers: { Authorization: "Bearer access-token" } }
    );
    expect(globalThis.fetch).toHaveBeenNthCalledWith(
      2,
      "https://nyx.example/api/v1/catalog",
      { headers: { Authorization: "Bearer access-token" } }
    );
  });

  it("uses only the top-level created UserService id", async () => {
    jest.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({
          id: "user-service-created",
          api_key_id: "api-key-created",
          slug: "api-github",
        })
    );

    await expect(
      createNyxIdCatalogKey({
        serviceSlug: "api-github",
        credential: "transient-secret",
        label: "GitHub",
      })
    ).resolves.toBe("user-service-created");

    jest.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({ api_key_id: "api-key-only" })
    );
    await expect(
      createNyxIdCatalogKey({
        serviceSlug: "api-github",
        credential: "transient-secret",
        label: "GitHub",
      })
    ).rejects.toMatchObject({ code: "NYXID_USER_SERVICE_ID_MISSING" });
  });

  it("never exposes a submitted credential through a NyxID error", async () => {
    jest.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse(
        {
          code: "INVALID_CREDENTIAL",
          message: "Credential transient-secret was rejected",
        },
        400
      )
    );

    await expect(
      createNyxIdCatalogKey({
        serviceSlug: "api-github",
        credential: "transient-secret",
        label: "GitHub",
      })
    ).rejects.toMatchObject({
      code: "INVALID_CREDENTIAL",
      message: "NyxID request failed.",
    });
  });

  it("never guesses completion when inventory difference is ambiguous", () => {
    expect(
      matchNewUserServiceId(
        new Set(["existing"]),
        new Set(["existing", "created"])
      )
    ).toBe("created");
    expect(
      matchNewUserServiceId(
        new Set(["existing"]),
        new Set(["existing", "created-a", "created-b"])
      )
    ).toBeNull();
    expect(
      matchNewUserServiceId(new Set(["existing"]), new Set(["existing"]))
    ).toBeNull();
  });
});
