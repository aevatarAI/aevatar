import type {
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
} from '@/shared/models/runtime/catalog';
import type {
  WorkflowTemplateDetail,
  WorkflowTemplateListResponse,
} from '@/shared/models/runtime/workflowTemplates';
import { requestJson, withQuery } from './http/client';
import {
  decodeWorkflowCatalogItemDetailResponse,
  decodeWorkflowCatalogItems,
  decodeWorkflowNames,
  decodeWorkflowTemplateDetailResponse,
  decodeWorkflowTemplateListResponse,
} from './runtimeDecoders';

export const runtimeCatalogApi = {
  listWorkflowNames(): Promise<string[]> {
    return requestJson('/api/workflows', decodeWorkflowNames);
  },

  listWorkflowCatalog(): Promise<WorkflowCatalogItem[]> {
    return requestJson('/api/workflow-catalog', decodeWorkflowCatalogItems);
  },

  getWorkflowDetail(workflowName: string): Promise<WorkflowCatalogItemDetail> {
    return requestJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse,
    );
  },

  listWorkflowTemplates(
    input: {
      query?: string;
      sort?: string;
      cursor?: string | null;
      take?: number;
    } = {},
  ): Promise<WorkflowTemplateListResponse> {
    return requestJson(
      withQuery('/api/workflow-templates', {
        query: input.query,
        sort: input.sort,
        cursor: input.cursor,
        take: input.take,
      }),
      decodeWorkflowTemplateListResponse,
    );
  },

  getWorkflowTemplate(templateId: string): Promise<WorkflowTemplateDetail> {
    return requestJson(
      `/api/workflow-templates/${encodeURIComponent(templateId)}`,
      decodeWorkflowTemplateDetailResponse,
    );
  },
};
