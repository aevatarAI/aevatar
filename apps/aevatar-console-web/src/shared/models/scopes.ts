export interface ScopeWorkflowSummary {
  scopeId: string;
  workflowId: string;
  publishedServiceId: string;
  displayName: string;
  serviceKey: string;
  workflowName: string;
  actorId: string;
  activeRevisionId: string;
  deploymentId: string;
  deploymentStatus: string;
  serviceAppId: string;
  serviceNamespace: string;
  publishedServiceId: string;
  updatedAt: string;
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
