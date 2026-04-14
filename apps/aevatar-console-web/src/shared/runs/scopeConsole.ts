import type { ServiceCatalogSnapshot, ServiceEndpointSnapshot } from "@/shared/models/services";
import type { StudioScopeGAgentBindingInput } from "@/shared/studio/models";
import { normalizeRunEndpointKind } from "./endpointKinds";

export const scopeServiceAppId = "default";
export const scopeServiceNamespace = "default";
export const nyxIdChatActorTypeName =
  "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent";
export const nyxIdChatServiceId = "nyxid-chat";
export const nyxIdChatLabel = "NyxID Chat";
export const nyxIdChatEndpointDescription =
  "Chat with NyxID about services, credentials, and configuration.";

export type ScopeConsoleServiceOption = {
  deploymentStatus?: string;
  displayName: string;
  endpoints: ServiceEndpointSnapshot[];
  kind: "nyxid-chat" | "service";
  namespace: string;
  primaryActorId?: string;
  serviceId: string;
};

export type ScopeConsoleServiceSort = "displayName" | "serviceId";

export type RuntimeInvokeReceipt = {
  actorId: string;
  commandId: string;
  correlationId: string;
  runId: string;
};

function cloneServiceEndpoint(
  endpoint: ServiceEndpointSnapshot
): ServiceEndpointSnapshot {
  return {
    description: endpoint.description,
    displayName: endpoint.displayName,
    endpointId: endpoint.endpointId,
    kind: endpoint.kind,
    requestTypeUrl: endpoint.requestTypeUrl,
    responseTypeUrl: endpoint.responseTypeUrl,
  };
}

function readResponseField(
  response: Record<string, unknown>,
  ...keys: string[]
): string {
  for (const key of keys) {
    const candidate = response[key];
    if (typeof candidate === "string" && candidate.trim().length > 0) {
      return candidate.trim();
    }
  }

  return "";
}

export function isChatServiceEndpoint(
  endpoint?: Pick<ServiceEndpointSnapshot, "endpointId" | "kind"> | null
): boolean {
  return normalizeRunEndpointKind(endpoint?.kind, endpoint?.endpointId) === "chat";
}

export function createNyxIdChatServiceOption(): ScopeConsoleServiceOption {
  return {
    displayName: nyxIdChatLabel,
    endpoints: [
      {
        description: nyxIdChatEndpointDescription,
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
        requestTypeUrl: "",
        responseTypeUrl: "",
      },
    ],
    kind: "nyxid-chat",
    namespace: scopeServiceNamespace,
    serviceId: nyxIdChatServiceId,
  };
}

export function buildScopeConsoleServiceOptions(
  services: readonly ServiceCatalogSnapshot[],
  defaultServiceId?: string,
  options?: {
    chatOnly?: boolean;
    sortBy?: ScopeConsoleServiceSort;
  }
): ScopeConsoleServiceOption[] {
  const builtInNyxIdService = createNyxIdChatServiceOption();
  const sortBy = options?.sortBy ?? "displayName";
  const remoteServices = services
    .map((service) => {
      const endpoints = (options?.chatOnly
        ? service.endpoints.filter(isChatServiceEndpoint)
        : service.endpoints
      ).map(cloneServiceEndpoint);

      return {
        deploymentStatus: service.deploymentStatus,
        displayName: service.displayName || service.serviceId,
        endpoints,
        kind: "service" as const,
        namespace: service.namespace,
        primaryActorId: service.primaryActorId || undefined,
        serviceId: service.serviceId,
      };
    })
    .filter((service) => service.serviceId !== builtInNyxIdService.serviceId)
    .filter((service) => service.endpoints.length > 0)
    .sort((left, right) => {
      const leftIsDefault = left.serviceId === defaultServiceId ? 1 : 0;
      const rightIsDefault = right.serviceId === defaultServiceId ? 1 : 0;

      if (leftIsDefault !== rightIsDefault) {
        return rightIsDefault - leftIsDefault;
      }

      return sortBy === "serviceId"
        ? left.serviceId.localeCompare(right.serviceId)
        : left.displayName.localeCompare(right.displayName);
    });

  return [builtInNyxIdService, ...remoteServices];
}

export function getPreferredScopeConsoleServiceId(
  services: readonly Pick<ScopeConsoleServiceOption, "serviceId">[],
  defaultServiceId?: string
): string {
  return (
    services.find((service) => service.serviceId === defaultServiceId)
      ?.serviceId ||
    services.find((service) => service.serviceId === nyxIdChatServiceId)
      ?.serviceId ||
    services[0]?.serviceId ||
    ""
  );
}

export function createNyxIdChatBindingInput(
  scopeId: string
): StudioScopeGAgentBindingInput {
  return {
    actorTypeName: nyxIdChatActorTypeName,
    displayName: nyxIdChatLabel,
    endpoints: [
      {
        description: nyxIdChatEndpointDescription,
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
        requestTypeUrl: "",
        responseTypeUrl: "",
      },
    ],
    scopeId,
    serviceId: nyxIdChatServiceId,
  };
}

export function extractRuntimeInvokeReceipt(
  response: Record<string, unknown>
): RuntimeInvokeReceipt {
  const runId = readResponseField(
    response,
    "request_id",
    "requestId",
    "command_id",
    "commandId"
  );

  return {
    actorId: readResponseField(
      response,
      "target_actor_id",
      "targetActorId",
      "actorId"
    ),
    commandId: readResponseField(response, "command_id", "commandId") || runId,
    correlationId:
      readResponseField(response, "correlation_id", "correlationId") || runId,
    runId,
  };
}
