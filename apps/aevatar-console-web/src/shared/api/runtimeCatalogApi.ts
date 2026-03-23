import {
  decodeWorkflowCatalogItemDetailResponse,
  decodeWorkflowCatalogItems,
  decodeWorkflowNames,
} from "./runtimeDecoders";
import { requestJson } from "./http/client";
import type {
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
} from "@/shared/models/runtime/catalog";

export const runtimeCatalogApi = {
  listWorkflowNames(): Promise<string[]> {
    return requestJson("/api/workflows", decodeWorkflowNames);
  },

  listWorkflowCatalog(): Promise<WorkflowCatalogItem[]> {
    return requestJson("/api/workflow-catalog", decodeWorkflowCatalogItems);
  },

  getWorkflowDetail(workflowName: string): Promise<WorkflowCatalogItemDetail> {
    return requestJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse
    );
  },
};
