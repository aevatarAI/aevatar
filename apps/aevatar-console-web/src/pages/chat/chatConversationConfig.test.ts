import {
  buildConversationModelGroups,
  resolveReadyConversationRoute,
  routePathFromProviderSlug,
  USER_LLM_ROUTE_GATEWAY,
} from "./chatConversationConfig";

describe("resolveReadyConversationRoute", () => {
  it("keeps the preferred service route when that provider is ready", () => {
    expect(
      resolveReadyConversationRoute(
        routePathFromProviderSlug("openai"),
        null,
        [{ providerSlug: "openai" }]
      )
    ).toBe(routePathFromProviderSlug("openai"));
  });

  it("falls back to the gateway when the preferred service route is stale", () => {
    expect(
      resolveReadyConversationRoute(
        routePathFromProviderSlug("openai"),
        { providerSlug: "gateway-openai" },
        [{ providerSlug: "anthropic" }]
      )
    ).toBe(USER_LLM_ROUTE_GATEWAY);
  });

  it("falls back to the first ready service route when the gateway is unavailable", () => {
    expect(
      resolveReadyConversationRoute(
        USER_LLM_ROUTE_GATEWAY,
        null,
        [{ providerSlug: "anthropic" }, { providerSlug: "openai" }]
      )
    ).toBe(routePathFromProviderSlug("anthropic"));
  });
});

describe("buildConversationModelGroups", () => {
  it("keeps gateway route models as the union of all ready gateway providers", () => {
    expect(
      buildConversationModelGroups({
        effectiveRoute: USER_LLM_ROUTE_GATEWAY,
        models: {
          gatewayUrl: "https://nyx.example/gateway",
          modelsByProvider: {
            openai: ["gpt-4o"],
            anthropic: ["claude-3-5-sonnet"],
          },
          providers: [
            {
              providerName: "OpenAI Gateway",
              providerSlug: "openai",
              proxyUrl: "https://nyx.example/gateway/openai",
              source: "gateway_provider",
              status: "ready",
            },
            {
              providerName: "Anthropic Gateway",
              providerSlug: "anthropic",
              proxyUrl: "https://nyx.example/gateway/anthropic",
              source: "gateway_provider",
              status: "ready",
            },
          ],
          supportedModels: ["gpt-4o", "claude-3-5-sonnet"],
        },
      }).map((group) => group.label)
    ).toEqual(["OpenAI Gateway", "Anthropic Gateway"]);
  });

  it("does not reuse the global supported model union for a service route", () => {
    expect(
      buildConversationModelGroups({
        effectiveRoute: routePathFromProviderSlug("anthropic-team"),
        models: {
          gatewayUrl: "https://nyx.example/gateway",
          modelsByProvider: {
            openai: ["gpt-4o"],
          },
          providers: [
            {
              providerName: "OpenAI Gateway",
              providerSlug: "openai",
              proxyUrl: "https://nyx.example/gateway/openai",
              source: "gateway_provider",
              status: "ready",
            },
            {
              providerName: "Anthropic Team Service",
              providerSlug: "anthropic-team",
              proxyUrl: "https://nyx.example/service/anthropic-team",
              source: "user_service",
              status: "ready",
            },
          ],
          supportedModels: ["gpt-4o", "claude-3-haiku"],
        },
      })
    ).toEqual([]);
  });
});
