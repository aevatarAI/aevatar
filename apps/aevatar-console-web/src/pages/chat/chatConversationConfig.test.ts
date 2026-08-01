import type { StudioUserLlmSettings } from "@/shared/studio/models";
import {
  buildConversationRouteOptions,
  buildConversationModelGroups,
  USER_LLM_ROUTE_GATEWAY,
} from "./chatConversationConfig";

const llmSettings: StudioUserLlmSettings = {
  userConfigStateVersion: 10,
  savedRoute: USER_LLM_ROUTE_GATEWAY,
  savedRouteLabel: "Company LLM Gateway",
  savedRouteKind: "gateway",
  savedUserServiceId: null,
  savedServiceSlug: null,
  effectiveRoute: USER_LLM_ROUTE_GATEWAY,
  effectiveRouteLabel: "Company LLM Gateway",
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
      defaultModel: null,
      label: "Company LLM Gateway",
      source: "gateway_provider",
      status: "ready",
      allowed: true,
      ready: true,
      userServiceId: null,
      serviceSlug: null,
      description: null,
    },
    {
      routeValue: "/api/v1/proxy/s/anthropic-team",
      defaultModel: "claude-3-haiku",
      label: "Anthropic Team Service",
      source: "user_service",
      status: "ready",
      allowed: true,
      ready: true,
      userServiceId: "us-anthropic",
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
  it("uses the explicit Gateway route for conversation overrides", () => {
    expect(USER_LLM_ROUTE_GATEWAY).toBe("/api/v1/llm/gateway/v1");
  });

  it("uses backend route options without deriving routes from provider slugs", () => {
    expect(buildConversationRouteOptions(llmSettings).map((option) => option.label)).toEqual([
      "Company LLM Gateway",
      "Anthropic Team Service",
    ]);
  });

  it("uses a generic gateway label when backend sends a blank gateway label", () => {
    expect(
      buildConversationRouteOptions({
        ...llmSettings,
        routeOptions: [
          {
            ...llmSettings.routeOptions[0],
            label: " ",
          },
        ],
      }).map((option) => option.label)
    ).toEqual(["Gateway"]);
  });

  it("does not add saved global or conversation routes when backend omits them", () => {
    const options = buildConversationRouteOptions({
      ...llmSettings,
      routeOptions: [llmSettings.routeOptions[0]],
    });

    expect(options).toEqual([
      {
        label: "Company LLM Gateway",
        value: USER_LLM_ROUTE_GATEWAY,
      },
    ]);
    expect(options.map((option) => option.value)).not.toContain(
      "/api/v1/proxy/s/retired-team"
    );
  });

  it("keeps conversation overrides route-based when exact services share a route", () => {
    const sharedRoute = "/api/v1/proxy/s/shared-anthropic";
    const options = buildConversationRouteOptions({
      ...llmSettings,
      routeOptions: [
        llmSettings.routeOptions[0],
        {
          ...llmSettings.routeOptions[1],
          routeValue: sharedRoute,
          label: "Anthropic alpha",
          userServiceId: "us-alpha",
        },
        {
          ...llmSettings.routeOptions[1],
          routeValue: sharedRoute,
          label: "Anthropic beta",
          userServiceId: "us-beta",
        },
      ],
    });

    expect(options.filter((option) => option.value === sharedRoute)).toEqual([
      { label: "Anthropic alpha", value: sharedRoute },
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

  it("does not emit a current group from selected or default models outside backend groups", () => {
    expect(
      buildConversationModelGroups({
        effectiveRoute: USER_LLM_ROUTE_GATEWAY,
        settings: {
          ...llmSettings,
          defaultModel: "retired-default-model",
          modelGroupsByRoute: [],
        },
      })
    ).toEqual([]);
  });
});
