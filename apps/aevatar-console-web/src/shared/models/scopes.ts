export type ScopeWorkflowCatalogueView = 'all' | 'drafts' | 'archived';

export interface ScopeWorkflowCatalogueActionCapability {
  available: boolean;
  unavailableReason: string | null;
}

export interface ScopeWorkflowCatalogueRowCapabilities {
  open: ScopeWorkflowCatalogueActionCapability;
  activity: ScopeWorkflowCatalogueActionCapability;
  rename: ScopeWorkflowCatalogueActionCapability;
  delete: ScopeWorkflowCatalogueActionCapability;
}

export interface ScopeWorkflowCatalogueCommittedFacts {
  serviceKey: string;
  workflowName: string;
  actorId: string;
  activeRevisionId: string;
  deploymentId: string;
  deploymentStatus: string;
  serviceAppId: string;
  serviceNamespace: string;
}

export interface ScopeWorkflowCatalogueRow {
  scopeId: string;
  workflowId: string;
  name: string;
  description: string;
  hasDraftSource: boolean;
  hasCommittedSource: boolean;
  updatedAtUtc: string;
  updatedAtSource: string;
  capabilities: ScopeWorkflowCatalogueRowCapabilities;
  sourceWatermarkUtc: string;
  committed: ScopeWorkflowCatalogueCommittedFacts | null;
  publishedServiceId: string | null;
}

export interface ScopeWorkflowCatalogueResponse {
  items: ScopeWorkflowCatalogueRow[];
  nextPageToken: string | null;
  freshness: {
    refreshWatermarkUtc: string | null;
    sourceVersionSemantics: string;
  };
  search: {
    searchableFields: string[];
    caseSemantics: string;
    unicodeNormalization: string;
    maximumQueryLength: number;
    emptyQuerySemantics: string;
    workflowIdSemantics: string;
  };
}

export interface ScopeWorkflowCatalogueQuery {
  scopeId: string;
  view: ScopeWorkflowCatalogueView;
  query?: string;
  cursor?: string;
  take?: number;
}

export interface ScopeWorkflowSummary {
  scopeId: string;
  workflowId: string;
  displayName: string;
  serviceKey: string;
  workflowName: string;
  actorId: string;
  activeRevisionId: string;
  deploymentId: string;
  deploymentStatus: string;
  updatedAt: string;
  publishedServiceId: string;
  serviceAppId: string;
  serviceNamespace: string;
}

export interface ScopeWorkflowSource {
  workflowYaml: string;
  definitionActorId: string;
  inlineWorkflowYamls: Record<string, string> | null;
}

export interface ScopeWorkflowDetail {
  available: boolean;
  scopeId: string;
  workflow: ScopeWorkflowSummary | null;
  source: ScopeWorkflowSource | null;
}

export interface ScopeWorkflowArchiveCommandHandle {
  stage: string;
  targetActorId: string;
  commandId: string;
  correlationId: string;
}

export interface ScopeWorkflowArchiveAcceptedResult {
  scopeId: string;
  workflowId: string;
  deploymentId: string;
  commandHandle: ScopeWorkflowArchiveCommandHandle;
  readModelUrl: string;
  acceptanceStage: string;
  propagationStage: string;
}

export interface ScopeScriptSummary {
  scopeId: string;
  scriptId: string;
  catalogActorId: string;
  definitionActorId: string;
  activeRevision: string;
  activeSourceHash: string;
  updatedAt: string;
}

export interface ScopeScriptSource {
  sourceText: string;
  definitionActorId: string;
  revision: string;
  sourceHash: string;
}

export interface ScopeScriptDetail {
  available: boolean;
  scopeId: string;
  script: ScopeScriptSummary | null;
  source: ScopeScriptSource | null;
}

export interface ScopeScriptCatalog {
  scriptId: string;
  activeRevision: string;
  activeDefinitionActorId: string;
  activeSourceHash: string;
  previousRevision: string;
  revisionHistory: string[];
  lastProposalId: string;
  catalogActorId: string;
  scopeId: string;
  updatedAt: string;
}
