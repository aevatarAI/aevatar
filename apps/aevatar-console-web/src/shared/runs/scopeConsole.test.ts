import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  buildScopeConsoleServiceOptions,
  createNyxIdChatBindingInput,
  extractRuntimeInvokeReceipt,
  getPreferredScopeConsoleServiceId,
  nyxIdChatServiceId,
} from "./scopeConsole";

const sampleServices: ServiceCatalogSnapshot[] = [
  {
    activeServingRevisionId: "rev-alpha",
    appId: "default",
    defaultServingRevisionId: "rev-alpha",
    deploymentId: "deploy-alpha",
    deploymentStatus: "Active",
    displayName: "Alpha",
    endpoints: [
      {
        description: "Chat endpoint",
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
        requestTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
        responseTypeUrl: "type.googleapis.com/google.protobuf.StringValue",
      },
      {
        description: "Refresh endpoint",
        displayName: "Refresh",
        endpointId: "refresh",
        kind: "command",
        requestTypeUrl: "type.googleapis.com/example.Refresh",
        responseTypeUrl: "type.googleapis.com/example.RefreshReply",
      },
    ],
    namespace: "default",
    policyIds: [],
    primaryActorId: "actor://alpha",
    serviceId: "svc-alpha",
    serviceKey: "scope-a:default:default:svc-alpha",
    tenantId: "scope-a",
    updatedAt: "2026-04-07T01:00:00Z",
  },
  {
    activeServingRevisionId: "rev-beta",
    appId: "default",
    defaultServingRevisionId: "rev-beta",
    deploymentId: "deploy-beta",
    deploymentStatus: "Active",
    displayName: "Beta",
    endpoints: [
      {
        description: "Refresh endpoint",
        displayName: "Refresh",
        endpointId: "refresh",
        kind: "command",
        requestTypeUrl: "type.googleapis.com/example.Refresh",
        responseTypeUrl: "type.googleapis.com/example.RefreshReply",
      },
    ],
    namespace: "default",
    policyIds: [],
    primaryActorId: "actor://beta",
    serviceId: "svc-beta",
    serviceKey: "scope-a:default:default:svc-beta",
    tenantId: "scope-a",
    updatedAt: "2026-04-07T02:00:00Z",
  },
];

describe("scopeConsole", () => {
  it("builds built-in and published service options for chat and invoke surfaces", () => {
    const invokeServices = buildScopeConsoleServiceOptions(sampleServices, "svc-alpha", {
      sortBy: "serviceId",
    });
    expect(invokeServices.map((service) => service.serviceId)).toEqual([
      nyxIdChatServiceId,
      "svc-alpha",
      "svc-beta",
    ]);

    const chatServices = buildScopeConsoleServiceOptions(sampleServices, "svc-alpha", {
      chatOnly: true,
    });
    expect(chatServices.map((service) => service.serviceId)).toEqual([
      nyxIdChatServiceId,
      "svc-alpha",
    ]);
    expect(chatServices[1]?.endpoints.map((endpoint) => endpoint.endpointId)).toEqual([
      "chat",
    ]);
  });

  it("creates the shared NyxID binding request shape", () => {
    expect(createNyxIdChatBindingInput("scope-a")).toEqual({
      actorTypeName: "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent",
      displayName: "NyxID Chat",
      endpoints: [
        {
          description:
            "Chat with NyxID about services, credentials, and configuration.",
          displayName: "Chat",
          endpointId: "chat",
          kind: "chat",
          requestTypeUrl: "",
          responseTypeUrl: "",
        },
      ],
      scopeId: "scope-a",
      serviceId: "nyxid-chat",
    });
  });

  it("extracts invoke receipt identifiers from mixed response keys", () => {
    expect(
      extractRuntimeInvokeReceipt({
        commandId: "cmd-1",
        correlation_id: "corr-1",
        request_id: "run-1",
        targetActorId: "actor://svc",
      })
    ).toEqual({
      actorId: "actor://svc",
      commandId: "cmd-1",
      correlationId: "corr-1",
      runId: "run-1",
    });

    expect(
      getPreferredScopeConsoleServiceId(
        [
          { serviceId: "svc-alpha" },
          { serviceId: nyxIdChatServiceId },
        ],
        "missing"
      )
    ).toBe(nyxIdChatServiceId);
  });
});
