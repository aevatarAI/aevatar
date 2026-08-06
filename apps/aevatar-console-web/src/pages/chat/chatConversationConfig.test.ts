import type { StudioUserLlmSettings } from "@/shared/studio/models";
import {
  buildConversationModelGroups,
  buildConversationRouteOptions,
  resolveSavedConversationLlmConfig,
  USER_LLM_ROUTE_GATEWAY,
} from "./chatConversationConfig";

const llmSettings: StudioUserLlmSettings = {
  savedSelection: {
    routeKind: "nyx_id_user_service",
    routeValue: "/api/v1/proxy/s/anthropic-team",
    nyxIdUserServiceId: "us-anthropic",
    serviceSlugSnapshot: "anthropic-team",
    modelSelection: {
      kind: "explicit_model",
      modelId: "claude-3-haiku",
    },
  },
  savedRouteLabel: "Anthropic Team Service",
  selectionStatus: "ready",
  catalogDiagnostic: "unspecified",
  remediation: "none",
  catalogStatus: "ready",
  capabilities: {
    canEditRoute: true,
    canEditModel: true,
    canSave: true,
    canRetryCatalog: false,
  },
  routeOptions: [
    {
      routeValue: USER_LLM_ROUTE_GATEWAY,
      label: "Company LLM Gateway",
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
    {
      routeValue: "/api/v1/proxy/s/anthropic-team",
      label: "Anthropic Team Service",
      source: "user_service",
      status: "ready",
      allowed: true,
      ready: true,
      userServiceId: "us-anthropic",
      serviceSlug: "anthropic-team",
      modelCatalog: {
        certainty: "enumerated",
        modelIds: ["claude-3-haiku"],
        defaultModelId: "claude-3-haiku",
        diagnostic: "unspecified",
      },
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
      routeValue: "/api/v1/proxy/s/anthropic-team",
      groupId: "anthropic-team",
      label: "Anthropic Team Service",
      models: ["claude-3-haiku"],
    },
  ],
};

describe("chatConversationConfig", () => {
  it("uses backend routes without deriving identities from provider labels", () => {
    expect(USER_LLM_ROUTE_GATEWAY).toBe("/api/v1/llm/gateway/v1");
    expect(buildConversationRouteOptions(llmSettings)).toEqual([
      { label: "Company LLM Gateway", value: USER_LLM_ROUTE_GATEWAY },
      {
        label: "Anthropic Team Service",
        value: "/api/v1/proxy/s/anthropic-team",
      },
    ]);
  });

  it("uses a generic Gateway label only for an explicit Gateway option", () => {
    expect(
      buildConversationRouteOptions({
        ...llmSettings,
        routeOptions: [{ ...llmSettings.routeOptions[0], label: " " }],
      }),
    ).toEqual([{ label: "Gateway", value: USER_LLM_ROUTE_GATEWAY }]);
  });

  it("uses only backend model groups for the requested route", () => {
    expect(
      buildConversationModelGroups({
        route: "/api/v1/proxy/s/anthropic-team",
        settings: llmSettings,
      }).map((group) => group.models),
    ).toEqual([["claude-3-haiku"]]);
    expect(
      buildConversationModelGroups({
        route: "/api/v1/proxy/s/missing",
        settings: llmSettings,
      }),
    ).toEqual([]);
  });

  it("derives ready conversation settings only from savedSelection", () => {
    expect(resolveSavedConversationLlmConfig(llmSettings)).toEqual({
      status: "ready",
      route: "/api/v1/proxy/s/anthropic-team",
      model: "claude-3-haiku",
      routeLabel: "Anthropic Team Service",
    });
  });

  it("surfaces repair action without silently substituting Gateway", () => {
    const result = resolveSavedConversationLlmConfig({
      ...llmSettings,
      selectionStatus: "needs_repair",
      remediation: "choose_replacement",
    });

    expect(result).toEqual({
      status: "action_required",
      remediation: "choose_replacement",
      routeLabel: "Anthropic Team Service",
    });
    expect(result).not.toHaveProperty("route", USER_LLM_ROUTE_GATEWAY);
  });

  it("keeps System default distinct from Gateway", () => {
    expect(
      resolveSavedConversationLlmConfig({
        ...llmSettings,
        savedSelection: {
          routeKind: "unspecified",
          modelSelection: { kind: "unspecified" },
        },
        savedRouteLabel: "System default",
        selectionStatus: "system_default",
      }),
    ).toEqual({ status: "system_default" });
  });
});
