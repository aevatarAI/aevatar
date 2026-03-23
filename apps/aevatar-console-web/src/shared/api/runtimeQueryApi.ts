import {
  decodeWorkflowAgentSummaries,
  decodeWorkflowCapabilitiesResponse,
  decodeWorkflowPrimitiveDescriptorsResponse,
} from "./runtimeDecoders";
import { requestJson } from "./http/client";
import type {
  WorkflowAgentSummary,
  WorkflowCapabilities,
  WorkflowPrimitiveDescriptor,
} from "@/shared/models/runtime/query";

export const runtimeQueryApi = {
  listAgents(): Promise<WorkflowAgentSummary[]> {
    return requestJson("/api/agents", decodeWorkflowAgentSummaries);
  },

  getCapabilities(): Promise<WorkflowCapabilities> {
    return requestJson("/api/capabilities", decodeWorkflowCapabilitiesResponse);
  },

  listPrimitives(): Promise<WorkflowPrimitiveDescriptor[]> {
    return requestJson(
      "/api/primitives",
      decodeWorkflowPrimitiveDescriptorsResponse
    );
  },
};
