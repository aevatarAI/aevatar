export type FlashType = 'success' | 'error' | 'info';
export type ScriptStorageMode = 'draft' | 'scope';
export type StudioResultView = 'runtime' | 'save' | 'promotion';

export type ScriptsStudioProps = {
  appContext: {
    hostMode: 'embedded' | 'proxy';
    scopeId: string | null;
    scopeResolved: boolean;
    scriptStorageMode: ScriptStorageMode;
    scriptsEnabled: boolean;
    scriptContract: {
      inputType: string;
      readModelFields: string[];
    };
  };
  onFlash: (text: string, type: FlashType) => void;
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

export type SnapshotView = {
  input: string;
  output: string;
  status: string;
  lastCommandId: string;
  notes: string[];
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
};

export type ScriptDraft = {
  key: string;
  scriptId: string;
  revision: string;
  baseRevision: string;
  reason: string;
  input: string;
  source: string;
  definitionActorId: string;
  runtimeActorId: string;
  updatedAtUtc: string;
  lastSourceHash: string;
  lastRun: DraftRunResult | null;
  lastSnapshot: ScriptReadModelSnapshot | null;
  lastPromotion: ScriptPromotionDecision | null;
  scopeDetail: ScopedScriptDetail | null;
};
