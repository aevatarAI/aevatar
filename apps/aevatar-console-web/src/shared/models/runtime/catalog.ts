export interface WorkflowCatalogItem {
  name: string;
  description: string;
  category: string;
  group: string;
  groupLabel: string;
  sortOrder: number;
  source: string;
  sourceLabel: string;
  showInLibrary: boolean;
  isPrimitiveExample: boolean;
  requiresLlmProvider: boolean;
  primitives: string[];
}

export interface WorkflowCatalogRole {
  id: string;
  name: string;
  systemPrompt: string;
  provider: string;
  model: string;
  temperature: number | null;
  maxTokens: number | null;
  maxToolRounds: number | null;
  maxHistoryMessages: number | null;
  streamBufferCapacity: number | null;
  eventModules: string[];
  eventRoutes: string;
  connectors: string[];
}

export interface WorkflowCatalogChildStep {
  id: string;
  type: string;
  targetRole: string;
}

export interface WorkflowCatalogStep {
  id: string;
  type: string;
  targetRole: string;
  parameters: Record<string, string>;
  next: string;
  branches: Record<string, string>;
  children: WorkflowCatalogChildStep[];
}

export interface WorkflowCatalogEdge {
  from: string;
  to: string;
  label: string;
}

export interface WorkflowCatalogDefinition {
  name: string;
  description: string;
  closedWorldMode: boolean;
  roles: WorkflowCatalogRole[];
  steps: WorkflowCatalogStep[];
}

export interface WorkflowCatalogItemDetail {
  catalog: WorkflowCatalogItem;
  yaml: string;
  definition: WorkflowCatalogDefinition;
  edges: WorkflowCatalogEdge[];
}
