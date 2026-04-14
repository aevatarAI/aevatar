export type ScriptStorageMode = 'draft' | 'scope';

export type ScriptPackageFileKind = 'csharp' | 'proto';

export type ScriptPackageFile = {
  path: string;
  content: string;
};

export type ScriptPackage = {
  format: string;
  csharpSources: ScriptPackageFile[];
  protoFiles: ScriptPackageFile[];
  entryBehaviorTypeName: string;
  entrySourcePath: string;
};

export type ScriptPackageEntry = {
  kind: ScriptPackageFileKind;
  path: string;
  content: string;
};

export type DraftRunResult = {
  accepted: boolean;
  scopeId?: string | null;
  scriptId: string;
  scriptRevision: string;
  definitionActorId: string;
  runtimeActorId: string;
  runId: string;
  sourceHash: string;
  commandTypeUrl: string;
  readModelUrl: string;
};

export type ScriptReadModelSnapshot = {
  actorId: string;
  scriptId: string;
  definitionActorId: string;
  revision: string;
  readModelTypeUrl: string;
  readModelPayloadJson: string;
  stateVersion: number;
  lastEventId: string;
  updatedAt: string;
};

export type ScriptCatalogSnapshot = {
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
};

export type ScriptValidationDiagnostic = {
  severity: 'error' | 'warning' | 'info';
  code: string;
  message: string;
  filePath: string;
  startLine: number | null;
  startColumn: number | null;
  endLine: number | null;
  endColumn: number | null;
  origin: string;
};

export type ScriptValidationResult = {
  success: boolean;
  scriptId: string;
  scriptRevision: string;
  primarySourcePath: string;
  errorCount: number;
  warningCount: number;
  diagnostics: ScriptValidationDiagnostic[];
};

export type ScopedScriptSummary = {
  scopeId: string;
  scriptId: string;
  catalogActorId: string;
  definitionActorId: string;
  activeRevision: string;
  activeSourceHash: string;
  updatedAt: string;
};

export type ScopedScriptSource = {
  sourceText: string;
  definitionActorId: string;
  revision: string;
  sourceHash: string;
};

export type ScopedScriptDetail = {
  available: boolean;
  scopeId: string;
  script: ScopedScriptSummary | null;
  source: ScopedScriptSource | null;
};

export type ScopeScriptCommandAcceptedHandle = {
  actorId: string;
  commandId: string;
  correlationId: string;
};

export type ScopeScriptAcceptedSummary = {
  scopeId: string;
  scriptId: string;
  catalogActorId: string;
  definitionActorId: string;
  revisionId: string;
  sourceHash: string;
  acceptedAt: string;
  proposalId: string;
  expectedBaseRevision: string;
};

export type ScopeScriptUpsertAcceptedResponse = {
  acceptedScript: ScopeScriptAcceptedSummary;
  definitionCommand: ScopeScriptCommandAcceptedHandle;
  catalogCommand: ScopeScriptCommandAcceptedHandle;
};

export type ScopeScriptSaveObservationRequest = {
  revisionId: string;
  definitionActorId: string;
  sourceHash: string;
  proposalId: string;
  expectedBaseRevision: string;
  acceptedAt: string;
};

export type ScopeScriptSaveObservationResult = {
  scopeId: string;
  scriptId: string;
  status: 'pending' | 'applied' | 'rejected';
  message: string;
  currentScript: ScopedScriptSummary | null;
  isTerminal: boolean;
};

export type ScriptDefinitionBindingSnapshot = {
  scriptId: string;
  revision: string;
  sourceText: string;
  sourceHash: string;
  scriptPackage?: ScriptPackage | null;
  stateTypeUrl: string;
  readModelTypeUrl: string;
  readModelSchemaVersion: string;
  readModelSchemaHash: string;
  protocolDescriptorSet?: string | null;
  stateDescriptorFullName: string;
  readModelDescriptorFullName: string;
  runtimeSemantics?: Record<string, unknown> | null;
};

export type ScriptPromotionDecision = {
  accepted: boolean;
  proposalId: string;
  scriptId: string;
  baseRevision: string;
  candidateRevision: string;
  status: string;
  failureReason: string;
  definitionActorId: string;
  catalogActorId: string;
  validationReport?: {
    isSuccess: boolean;
    diagnostics: string[];
  } | null;
  definitionSnapshot?: ScriptDefinitionBindingSnapshot | null;
};

export type ScriptDraft = {
  key: string;
  scriptId: string;
  revision: string;
  baseRevision: string;
  reason: string;
  input: string;
  package: ScriptPackage;
  selectedFilePath: string;
  definitionActorId: string;
  runtimeActorId: string;
  updatedAtUtc: string;
  lastSourceHash: string;
  lastRun: DraftRunResult | null;
  lastSnapshot: ScriptReadModelSnapshot | null;
  lastPromotion: ScriptPromotionDecision | null;
  scopeDetail: ScopedScriptDetail | null;
};

export type GeneratedScriptResult = {
  text: string;
  scriptPackage: ScriptPackage | null;
  currentFilePath: string;
};
