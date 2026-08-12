import type { StudioUserLlmRouteOption } from "@/shared/studio/models";
import {
  buildUserLlmSelectionOptions,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
} from "./userLlmSelection";

const duplicateRoute = "/api/v1/proxy/s/shared-openai";

const routeOptions: StudioUserLlmRouteOption[] = [
  {
    routeValue: "/api/v1/llm/gateway/v1",
    label: "Gateway",
    source: "gateway_provider",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: null,
    serviceSlug: null,
    defaultModel: null,
    description: null,
  },
  {
    routeValue: duplicateRoute,
    label: "Shared OpenAI alpha",
    source: "user_service",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: "us-alpha",
    serviceSlug: "shared-openai",
    defaultModel: "gpt-alpha",
    description: null,
  },
  {
    routeValue: duplicateRoute,
    label: "Shared OpenAI beta",
    source: "user_service",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: "us-beta",
    serviceSlug: "shared-openai",
    defaultModel: "gpt-beta",
    description: null,
  },
  {
    routeValue: "/api/v1/proxy/s/diagnostic-only",
    label: "Provider diagnostic",
    source: "provider_diagnostic",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: "diag-health",
    serviceSlug: "diagnostic-only",
    defaultModel: "diagnostic-model",
    description: "Visible in health details only",
  },
];

describe("userLlmSelection", () => {
  it("keeps duplicate-route inventory services distinct and excludes diagnostics", () => {
    const options = buildUserLlmSelectionOptions(routeOptions);

    expect(options.map((option) => option.value)).toEqual([
      "gateway",
      "user-service:us-alpha",
      "user-service:us-beta",
    ]);
    expect(options.map((option) => option.label)).not.toContain(
      "Provider diagnostic",
    );
    expect(
      decodeUserLlmSelectionValue("user-service:us-beta", options),
    ).toEqual({
      kind: "nyx_id_user_service",
      userServiceId: "us-beta",
      routeValue: duplicateRoute,
    });
    expect(options.find((option) => option.value === "user-service:us-beta"))
      .toMatchObject({ defaultModel: "gpt-beta" });
  });

  it("encodes exact user service IDs without using their route", () => {
    expect(
      encodeUserLlmSelectionValue({
        kind: "nyx_id_user_service",
        userServiceId: "us/team beta",
        routeValue: duplicateRoute,
      }),
    ).toBe("user-service:us%2Fteam%20beta");
  });

  it("does not recover a saved service identity from a matching route", () => {
    expect(
      resolveSavedUserLlmSelection({
        savedRoute: duplicateRoute,
        savedRouteKind: "nyx_id_user_service",
        savedUserServiceId: null,
      }),
    ).toBeUndefined();
  });

  it("uses the explicit Gateway route for a saved Gateway selection", () => {
    expect(
      resolveSavedUserLlmSelection({
        savedRoute: "",
        savedRouteKind: "gateway",
        savedUserServiceId: null,
      }),
    ).toEqual({
      kind: "gateway",
      routeValue: "/api/v1/llm/gateway/v1",
    });
  });

  it("resolves an exact saved ID through its current inventory route", () => {
    const settings = {
      savedRoute: "/api/v1/proxy/s/shared-openai-old",
      savedRouteKind: "nyx_id_user_service" as const,
      savedUserServiceId: "us-alpha",
      routeOptions: [
        {
          ...routeOptions[1],
          routeValue: "/api/v1/proxy/s/shared-openai-current",
        },
      ],
    };

    expect(resolveSavedUserLlmSelection(settings)).toEqual({
      kind: "nyx_id_user_service",
      userServiceId: "us-alpha",
      routeValue: "/api/v1/proxy/s/shared-openai-current",
    });
  });
});
