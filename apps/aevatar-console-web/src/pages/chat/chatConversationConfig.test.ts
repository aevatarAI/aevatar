import {
  buildConversationModelGroups,
  buildConversationRouteOptions,
  USER_LLM_ROUTE_GATEWAY,
} from "./chatConversationConfig";

const llmSettings = {
  savedRoute: USER_LLM_ROUTE_GATEWAY,
  savedRouteLabel: "NyxID Gateway",
  effectiveRoute: USER_LLM_ROUTE_GATEWAY,
  effectiveRouteLabel: "NyxID Gateway",
  routeFallbackActive: false,
  fallbackReason: null,
  catalogStatus: "ready",
  defaultModel: "",
  capabilities: {
    canEditRoute: true,
    canEditModel: true,
    canSave: true,
    canRetryCatalog: false,
  },
  routeOptions: [
    {
      routeValue: USER_LLM_ROUTE_GATEWAY,
      label: "NyxID Gateway",
      source: "gateway_provider",
      status: "ready",
      allowed: true,
      ready: true,
      serviceId: null,
      serviceSlug: null,
      description: null,
    },
    {
      routeValue: "/api/v1/proxy/s/anthropic-team",
      label: "Anthropic Team Service",
      source: "user_service",
      status: "ready",
      allowed: true,
      ready: true,
      serviceId: "svc-anthropic",
      serviceSlug: "anthropic-team",
      description: null,
    },
  ],
  modelGroupsByRoute: [
    {
      routeValue: USER_LLM_ROUTE_GATEWAY,
      groupId: "openai",
      label: "OpenAI Gateway",
      models: ["gpt-4o"],
    },
    {
      routeValue: USER_LLM_ROUTE_GATEWAY,
      groupId: "anthropic",
      label: "Anthropic Gateway",
      models: ["claude-3-5-sonnet"],
    },
    {
      routeValue: "/api/v1/proxy/s/anthropic-team",
      groupId: "anthropic-team",
      label: "Anthropic Team Service",
      models: ["claude-3-haiku"],
    },
  ],
};

describe("buildConversationRouteOptions", () => {
  it("uses backend route options without deriving routes from provider slugs", () => {
    expect(buildConversationRouteOptions(llmSettings).map((option) => option.label)).toEqual([
      "NyxID Gateway",
      "Anthropic Team Service",
    ]);
  });
});

describe("buildConversationModelGroups", () => {
  it("keeps gateway route models from backend-provided model groups", () => {
    expect(
      buildConversationModelGroups({
        effectiveRoute: USER_LLM_ROUTE_GATEWAY,
        settings: llmSettings,
      }).map((group) => group.label)
    ).toEqual(["OpenAI Gateway", "Anthropic Gateway"]);
  });

  it("does not invent a service-specific catalog when backend sends no group for the route", () => {
    expect(
      buildConversationModelGroups({
        effectiveRoute: "/api/v1/proxy/s/missing",
        settings: llmSettings,
      })
    ).toEqual([]);
  });
});
