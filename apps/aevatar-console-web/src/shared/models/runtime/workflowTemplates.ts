export interface WorkflowTemplateFreshness {
  projectionWatermark: string;
  lastEventId: string;
  versionSemantics: string;
}

export interface WorkflowTemplateSummary {
  templateId: string;
  displayName: string;
  description: string;
  defaultDraftName: string;
  authorityStateVersion: number;
  stepCount: number;
  requiredConnections: string[];
  requiresLlmProvider: boolean;
  freshness: WorkflowTemplateFreshness;
}

export interface WorkflowTemplateListResponse {
  items: WorkflowTemplateSummary[];
  nextCursor: string | null;
  freshness: WorkflowTemplateFreshness;
}

export interface WorkflowTemplateDetail {
  template: WorkflowTemplateSummary;
  yaml: string;
  definition: {
    name: string;
    description: string;
    closedWorldMode: boolean;
    roles: Array<{
      id: string;
      name: string;
      connectors: string[];
    }>;
    steps: Array<{
      id: string;
      type: string;
      targetRole: string;
      parameters: Record<string, string>;
      next: string;
      branches: Record<string, string>;
      children: Array<{
        id: string;
        type: string;
        targetRole: string;
      }>;
    }>;
  };
  edges: Array<{
    from: string;
    to: string;
    label: string;
  }>;
  authorityStateVersion: number;
  freshness: WorkflowTemplateFreshness;
}
