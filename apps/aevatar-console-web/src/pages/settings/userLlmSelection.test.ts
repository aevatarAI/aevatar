import type {
  StudioLlmModelCatalog,
  StudioUserLlmRouteOption,
} from "@/shared/studio/models";
import {
  buildUserLlmSelectionOptions,
  decodeUserLlmSelectionValue,
  encodeUserLlmSelectionValue,
  resolveSavedUserLlmSelection,
  userLlmSelectionsEqual,
} from "./userLlmSelection";

const duplicateRoute = "/api/v1/proxy/s/shared-openai";
const enumeratedCatalog = (modelId: string): StudioLlmModelCatalog => ({
  certainty: "enumerated",
  modelIds: [modelId],
  defaultModelId: modelId,
  diagnostic: "unspecified",
});

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
    modelCatalog: {
      certainty: "not_verifiable",
      modelIds: [],
      defaultModelId: null,
      diagnostic: "not_published",
    },
    description: null,
  },
  ...["alpha", "beta"].map((suffix) => ({
    routeValue: duplicateRoute,
    label: `Shared OpenAI ${suffix}`,
    source: "user_service",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: `us-${suffix}`,
    serviceSlug: "shared-openai",
    modelCatalog: enumeratedCatalog(`gpt-${suffix}`),
    description: null,
  })),
  {
    routeValue: "/api/v1/proxy/s/diagnostic-only",
    label: "Provider diagnostic",
    source: "provider_diagnostic",
    status: "ready",
    allowed: true,
    ready: true,
    userServiceId: "diag-health",
    serviceSlug: "diagnostic-only",
    modelCatalog: enumeratedCatalog("diagnostic-model"),
    description: "Visible in health details only",
  },
];

describe("userLlmSelection", () => {
  it("keeps duplicate-route inventory identities distinct and starts with Provider default", () => {
    const options = buildUserLlmSelectionOptions(routeOptions);

    expect(options.map((option) => option.value)).toEqual([
      "gateway",
      "user-service:us-alpha",
      "user-service:us-beta",
    ]);
    expect(
      decodeUserLlmSelectionValue("user-service:us-beta", options),
    ).toEqual({
      routeKind: "nyx_id_user_service",
      routeValue: duplicateRoute,
      nyxIdUserServiceId: "us-beta",
      serviceSlugSnapshot: "shared-openai",
      modelSelection: { kind: "provider_default" },
    });
  });

  it("encodes exact user service IDs without using their route", () => {
    expect(
      encodeUserLlmSelectionValue({
        routeKind: "nyx_id_user_service",
        routeValue: duplicateRoute,
        nyxIdUserServiceId: "us/team beta",
        serviceSlugSnapshot: "shared-openai",
        modelSelection: { kind: "provider_default" },
      }),
    ).toBe("user-service:us%2Fteam%20beta");
  });

  it("retains the exact saved selection without replacing its route from inventory", () => {
    const savedSelection = {
      routeKind: "nyx_id_user_service" as const,
      routeValue: "/api/v1/proxy/s/shared-openai-old",
      nyxIdUserServiceId: "us-alpha",
      serviceSlugSnapshot: "shared-openai-old",
      modelSelection: {
        kind: "explicit_model" as const,
        modelId: "gpt-old",
      },
    };

    expect(resolveSavedUserLlmSelection({ savedSelection })).toEqual(
      savedSelection,
    );
  });

  it("does not turn System default into Gateway", () => {
    expect(
      resolveSavedUserLlmSelection({
        savedSelection: {
          routeKind: "unspecified",
          modelSelection: { kind: "unspecified" },
        },
      }),
    ).toBeUndefined();
  });

  it("compares the full route identity and model selection", () => {
    const left = resolveSavedUserLlmSelection({
      savedSelection: {
        routeKind: "nyx_id_user_service",
        routeValue: duplicateRoute,
        nyxIdUserServiceId: "us-alpha",
        serviceSlugSnapshot: "shared-openai",
        modelSelection: { kind: "explicit_model", modelId: "gpt-alpha" },
      },
    });

    expect(
      userLlmSelectionsEqual(left, {
        ...left!,
        modelSelection: { kind: "explicit_model", modelId: "gpt-beta" },
      }),
    ).toBe(false);
  });
});
