import {
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
