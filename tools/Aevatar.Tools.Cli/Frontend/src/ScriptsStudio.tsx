import Editor, { loader, type BeforeMount, type OnMount } from '@monaco-editor/react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Bot,
  ChevronLeft,
  Check,
  Code2,
  Copy,
  FileText,
  Globe,
  Play,
  RefreshCw,
  Rows3,
  Save,
  X,
} from 'lucide-react';
import * as monacoEditor from 'monaco-editor/esm/vs/editor/editor.api.js';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution';
import * as api from './api';
import { InspectorPanel } from './scripts-studio/components/InspectorPanel';
import { PackageFileTree } from './scripts-studio/components/PackageFileTree';
import { ResourceRail } from './scripts-studio/components/ResourceRail';
import { EmptyState, ScriptsStudioModal, StudioResultCard } from './scripts-studio/components/StudioChrome';
import type {
  AppScopeScriptSaveAcceptedResponse,
  AppScopeScriptSaveObservationRequest,
  AppScopeScriptSaveObservationResult,
  ScriptCatalogSnapshot,
  DraftRunResult,
  ScriptPackage,
  ScriptDraft,
  ScriptRunMode,
  StudioEditorView,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
  ScopedScriptDetail,
  ScriptsStudioProps,
  SnapshotView,
  StudioResultView,
} from './scripts-studio/models';
import {
  addPackageFile,
  coerceScriptPackage,
  createScriptPackage,
  createSingleSourcePackage,
  deserializePersistedSource,
  getPackageEntries,
  getSelectedPackageEntry,
  removePackageFile,
  renamePackageFile,
  serializePersistedSource,
  setEntrySourcePath,
  updateEntryBehaviorTypeName,
  updatePackageFileContent,
} from './scripts-studio/package';
import { formatDateTime, isScopeDetailDirty } from './scripts-studio/utils';

const STORAGE_KEY = 'aevatar:scripts-studio:v4';

const STARTER_SOURCE = `using System;
using System.Threading;
using System.Threading.Tasks;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Behaviors;
using Aevatar.Studio.Application.Scripts.Contracts;

public sealed class DraftBehavior : ScriptBehavior<AppScriptReadModel, AppScriptReadModel>
{
    protected override void Configure(IScriptBehaviorBuilder<AppScriptReadModel, AppScriptReadModel> builder)
    {
        builder
            .OnCommand<AppScriptCommand>(HandleAsync)
            .OnEvent<AppScriptUpdated>(
                apply: static (_, evt, _) => evt.Current == null ? new AppScriptReadModel() : evt.Current.Clone())
            .ProjectState(static (state, _) => state == null ? new AppScriptReadModel() : state.Clone());
    }

    private static Task HandleAsync(
        AppScriptCommand input,
        ScriptCommandContext<AppScriptReadModel> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
        var text = input?.Input ?? string.Empty;
        var current = AppScriptProtocol.CreateState(
            text,
            text.Trim().ToUpperInvariant(),
            "ok",
            commandId,
            new[]
            {
                "trimmed",
                "uppercased",
            });

        context.Emit(new AppScriptUpdated
        {
            CommandId = commandId,
            Current = current,
        });
        return Task.CompletedTask;
    }
}
`;

const monacoHost = globalThis as typeof globalThis & {
  MonacoEnvironment?: monacoEditor.Environment;
};

if (!monacoHost.MonacoEnvironment) {
  monacoHost.MonacoEnvironment = {
    getWorker() {
      return new editorWorker();
    },
  };
}

loader.config({ monaco: monacoEditor });

function DrawerToggleButton(props: { active: boolean; label: string; icon: ReactNode; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={props.onClick}
      title={props.label}
      aria-label={props.label}
      className={`drawer-icon-button ${props.active ? 'active' : ''}`}
    >
      {props.icon}
    </button>
  );
}

function normalizeStudioId(value: string, fallbackPrefix: string) {
  const normalized = String(value || '')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._ -]+/g, '')
    .replace(/[._ ]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');

  if (normalized) {
    return normalized;
  }

  const timestamp = new Date()
    .toISOString()
    .replace(/-/g, '')
    .replace(/:/g, '')
    .replace(/\./g, '')
    .replace('T', '')
    .replace('Z', '')
    .slice(0, 14);
  return `${fallbackPrefix}-${timestamp}`;
}

function createStarterPackage() {
  return createSingleSourcePackage(STARTER_SOURCE);
}

function isLegacyStarterSource(source: string) {
  const normalized = String(source || '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();

  return normalized.includes('oncommand<stringvalue>') ||
    normalized.includes('scriptbehavior<struct, struct>') ||
    (normalized.includes('google.protobuf.wellknowntypes') &&
      normalized.includes('stringvalue') &&
      normalized.includes('struct'));
}

function normalizeDraftPackageForAppRuntime(rawPackage?: ScriptPackage | null, rawSource?: string) {
  const packageModel = rawPackage
    ? createScriptPackage(
        rawPackage.csharpSources,
        rawPackage.protoFiles,
        rawPackage.entryBehaviorTypeName,
        rawPackage.entrySourcePath,
      )
    : deserializePersistedSource(String(rawSource || ''));
  const activeEntry = getSelectedPackageEntry(packageModel, packageModel.entrySourcePath);
  const primarySource = activeEntry?.content || '';

  if (!primarySource.trim()) {
    return {
      package: createStarterPackage(),
      migrated: true,
    };
  }

  if (isLegacyStarterSource(primarySource)) {
    return {
      package: createStarterPackage(),
      migrated: true,
    };
  }

  return {
    package: packageModel,
    migrated: false,
  };
}

function bumpRevision(revision: string): string {
  const match = revision.match(/^(.+?)(\d+)$/);
  if (match) {
    return `${match[1]}${Number(match[2]) + 1}`;
  }
  return `${revision}-2`;
}

function getNextCandidateRevision(baseRevision: string): string {
  const match = baseRevision.match(/^(.*?)(\d+)$/);
  if (match) {
    return `${match[1]}${Number(match[2]) + 1}`;
  }
  return `${baseRevision}_new`;
}

function catalogMatchesPromotion(
  catalog: ScriptCatalogSnapshot | null | undefined,
  decision: ScriptPromotionDecision,
): boolean {
  if (!catalog || !decision.accepted) {
    return false;
  }

  if (catalog.activeRevision !== decision.candidateRevision) {
    return false;
  }

  if (
    decision.definitionActorId &&
    catalog.activeDefinitionActorId !== decision.definitionActorId
  ) {
    return false;
  }

  if (decision.proposalId && catalog.lastProposalId !== decision.proposalId) {
    return false;
  }

  return true;
}

function createDraft(index: number, seed: Partial<ScriptDraft> = {}): ScriptDraft {
  const now = new Date().toISOString();
  const normalizedPackage = normalizeDraftPackageForAppRuntime(seed.package);
  const selectedEntry = getSelectedPackageEntry(
    normalizedPackage.package,
    seed.selectedFilePath || normalizedPackage.package.entrySourcePath,
  );
  return {
    key: seed.key || `draft-${Date.now()}-${index}`,
    scriptId: seed.scriptId || `script-${index}`,
    revision: seed.revision || `draft-rev-${index}`,
    baseRevision: seed.baseRevision || '',
    reason: seed.reason || '',
    input: seed.input || '',
    runMode: seed.runMode || 'chat',
    package: normalizedPackage.package,
    selectedFilePath: selectedEntry?.path || normalizedPackage.package.entrySourcePath || 'Behavior.cs',
    definitionActorId: seed.definitionActorId || '',
    runtimeActorId: seed.runtimeActorId || '',
    updatedAtUtc: seed.updatedAtUtc || now,
    lastSourceHash: seed.lastSourceHash || '',
    lastRun: seed.lastRun || null,
    lastChatResponse: seed.lastChatResponse || null,
    lastSnapshot: seed.lastSnapshot || null,
    lastPromotion: seed.lastPromotion || null,
    scopeDetail: seed.scopeDetail || null,
  };
}

function readStoredDrafts(): ScriptDraft[] {
  if (typeof window === 'undefined') {
    return [createDraft(1)];
  }

  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return [createDraft(1)];
    }

    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed) || parsed.length === 0) {
      return [createDraft(1)];
    }

    return parsed.map((item: any, index: number) => {
      const normalizedPackage = normalizeDraftPackageForAppRuntime(item?.package || null, String(item?.source || ''));
      const selectedEntry = getSelectedPackageEntry(
        normalizedPackage.package,
        String(item?.selectedFilePath || normalizedPackage.package.entrySourcePath || ''),
      );
      return {
        key: String(item?.key || `draft-${Date.now()}-${index + 1}`),
        scriptId: String(item?.scriptId || `script-${index + 1}`),
        revision: String(item?.revision || `draft-rev-${index + 1}`),
        baseRevision: String(item?.baseRevision || ''),
        reason: String(item?.reason || ''),
        input: String(item?.input || ''),
        runMode: 'script' as ScriptRunMode,
        package: normalizedPackage.package,
        selectedFilePath: selectedEntry?.path || normalizedPackage.package.entrySourcePath || 'Behavior.cs',
        definitionActorId: normalizedPackage.migrated ? '' : String(item?.definitionActorId || ''),
        runtimeActorId: normalizedPackage.migrated ? '' : String(item?.runtimeActorId || ''),
        updatedAtUtc: String(item?.updatedAtUtc || new Date().toISOString()),
        lastSourceHash: normalizedPackage.migrated ? '' : String(item?.lastSourceHash || ''),
        lastRun: normalizedPackage.migrated ? null : item?.lastRun || null,
        lastChatResponse: normalizedPackage.migrated ? null : item?.lastChatResponse ?? null,
        lastSnapshot: normalizedPackage.migrated ? null : item?.lastSnapshot || null,
        lastPromotion: normalizedPackage.migrated ? null : item?.lastPromotion || null,
        scopeDetail: normalizedPackage.migrated ? null : item?.scopeDetail || null,
      };
    });
  } catch {
    return [createDraft(1)];
  }
}

function parseSnapshotView(snapshot: ScriptReadModelSnapshot | null): SnapshotView {
  if (!snapshot?.readModelPayloadJson) {
    return {
      input: '',
      output: '',
      status: '',
      lastCommandId: '',
      notes: [],
    };
  }

  try {
    const payload = JSON.parse(snapshot.readModelPayloadJson);
    return {
      input: typeof payload?.input === 'string' ? payload.input : '',
      output: typeof payload?.output === 'string' ? payload.output : '',
      status: typeof payload?.status === 'string' ? payload.status : '',
      lastCommandId: typeof payload?.last_command_id === 'string' ? payload.last_command_id : '',
      notes: Array.isArray(payload?.notes)
        ? payload.notes.filter((item: unknown) => typeof item === 'string')
        : [],
    };
  } catch {
    return {
      input: '',
      output: '',
      status: '',
      lastCommandId: '',
      notes: [],
    };
  }
}

function buildEditorMarkers(
  validation: ScriptValidationResult | null,
  activeFilePath: string,
): monacoEditor.editor.IMarkerData[] {
  if (!validation) {
    return [];
  }

  return validation.diagnostics
    .filter(diagnostic => {
      if (!diagnostic.startLine || !diagnostic.startColumn) {
        return false;
      }

      return !diagnostic.filePath || diagnostic.filePath === activeFilePath;
    })
    .map(diagnostic => ({
      startLineNumber: diagnostic.startLine || 1,
      startColumn: diagnostic.startColumn || 1,
      endLineNumber: Math.max(diagnostic.endLine || diagnostic.startLine || 1, diagnostic.startLine || 1),
      endColumn: Math.max(
        diagnostic.endColumn || (diagnostic.startColumn || 1) + 1,
        (diagnostic.startColumn || 1) + 1,
      ),
      severity: diagnostic.severity === 'error'
        ? monacoEditor.MarkerSeverity.Error
        : diagnostic.severity === 'warning'
          ? monacoEditor.MarkerSeverity.Warning
          : monacoEditor.MarkerSeverity.Info,
      message: diagnostic.code ? `[${diagnostic.code}] ${diagnostic.message}` : diagnostic.message,
      code: diagnostic.code || undefined,
      source: diagnostic.origin || undefined,
    }));
}

function formatProblemLocation(diagnostic: ScriptValidationDiagnostic) {
  const filePath = diagnostic.filePath || 'source';
  if (!diagnostic.startLine || !diagnostic.startColumn) {
    return filePath;
  }

  return `${filePath}:${diagnostic.startLine}:${diagnostic.startColumn}`;
}

function summarizeValidation(validation: ScriptValidationResult | null, pending: boolean) {
  if (pending || !validation) {
    return 'Checking';
  }

  if (validation.errorCount > 0) {
    return `${validation.errorCount} error${validation.errorCount === 1 ? '' : 's'}${validation.warningCount > 0 ? ` · ${validation.warningCount} warning${validation.warningCount === 1 ? '' : 's'}` : ''}`;
  }

  if (validation.warningCount > 0) {
    return `${validation.warningCount} warning${validation.warningCount === 1 ? '' : 's'}`;
  }

  return 'Clean';
}

function prettyPrintJson(rawJson: string | null | undefined) {
  if (!rawJson) {
    return '-';
  }

  try {
    return JSON.stringify(JSON.parse(rawJson), null, 2);
  } catch {
    return rawJson;
  }
}

function hydrateDraftFromScopeDetail(detail: ScopedScriptDetail, index: number, existing?: ScriptDraft): ScriptDraft {
  const normalizedPackage = normalizeDraftPackageForAppRuntime(existing?.package || null, detail.source?.sourceText || '');
  const selectedEntry = getSelectedPackageEntry(
    normalizedPackage.package,
    existing?.selectedFilePath || normalizedPackage.package.entrySourcePath,
  );
  const scriptId = detail.script?.scriptId || existing?.scriptId || `script-${index}`;
  const baseRevision = detail.script?.activeRevision || detail.source?.revision || existing?.baseRevision || '';
  const revision = existing?.revision && existing.revision !== baseRevision
    ? existing.revision
    : baseRevision
      ? bumpRevision(baseRevision)
      : `draft-rev-${index}`;

  return createDraft(index, {
    key: existing?.key,
    scriptId,
    revision,
    baseRevision,
    reason: existing?.reason || '',
    input: existing?.input || '',
    package: normalizedPackage.package,
    selectedFilePath: selectedEntry?.path || normalizedPackage.package.entrySourcePath || 'Behavior.cs',
    definitionActorId: detail.script?.definitionActorId || detail.source?.definitionActorId || existing?.definitionActorId || '',
    runtimeActorId: existing?.runtimeActorId || '',
    updatedAtUtc: detail.script?.updatedAt || existing?.updatedAtUtc,
    lastSourceHash: detail.source?.sourceHash || detail.script?.activeSourceHash || existing?.lastSourceHash || '',
    lastRun: existing?.lastRun || null,
    lastSnapshot: existing?.lastSnapshot || null,
    lastPromotion: existing?.lastPromotion || null,
    scopeDetail: detail,
  });
}

function buildSaveObservationRequest(
  accepted: AppScopeScriptSaveAcceptedResponse,
): AppScopeScriptSaveObservationRequest {
  return {
    revisionId: accepted.revisionId,
    definitionActorId: accepted.definitionActorId,
    sourceHash: accepted.sourceHash,
    proposalId: accepted.proposalId,
    expectedBaseRevision: accepted.expectedBaseRevision,
    acceptedAt: accepted.acceptedAt,
  };
}

function wait(ms: number) {
  return new Promise(resolve => window.setTimeout(resolve, ms));
}

export default function ScriptsStudio({ appContext, onFlash }: ScriptsStudioProps) {
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const validationRequestRef = useRef(0);
  const [drafts, setDrafts] = useState<ScriptDraft[]>(() => readStoredDrafts());
  const [selectedDraftKey, setSelectedDraftKey] = useState('');
  const [search, setSearch] = useState('');
  const [scopeScripts, setScopeScripts] = useState<ScopedScriptDetail[]>([]);
  const [scopeCatalogsByScriptId, setScopeCatalogsByScriptId] = useState<Record<string, ScriptCatalogSnapshot>>({});
  const [runtimeSnapshots, setRuntimeSnapshots] = useState<ScriptReadModelSnapshot[]>([]);
  const [proposalDecisionsById, setProposalDecisionsById] = useState<Record<string, ScriptPromotionDecision>>({});
  const [scopeScriptsPending, setScopeScriptsPending] = useState(false);
  const [runtimeSnapshotsPending, setRuntimeSnapshotsPending] = useState(false);
  const [proposalDecisionsPending, setProposalDecisionsPending] = useState(false);
  const [runPending, setRunPending] = useState(false);
  const [snapshotPending, setSnapshotPending] = useState(false);
  const [savePending, setSavePending] = useState(false);
  const [promotionPending, setPromotionPending] = useState(false);
  const [workspacePanelOpen, setWorkspacePanelOpen] = useState(false);
  const [workspaceSection, setWorkspaceSection] = useState<'library' | 'activity' | 'details'>('library');
  const [promotionModalOpen, setPromotionModalOpen] = useState(false);
  const [askAiOpen, setAskAiOpen] = useState(false);
  const [askAiPrompt, setAskAiPrompt] = useState('');
  const [askAiReasoning, setAskAiReasoning] = useState('');
  const [askAiAnswer, setAskAiAnswer] = useState('');
  const [askAiGeneratedSource, setAskAiGeneratedSource] = useState('');
  const [askAiGeneratedPackage, setAskAiGeneratedPackage] = useState<ScriptPackage | null>(null);
  const [askAiGeneratedFilePath, setAskAiGeneratedFilePath] = useState('');
  const [askAiTargetDraftKey, setAskAiTargetDraftKey] = useState<string | null>(null);
  const [askAiPending, setAskAiPending] = useState(false);
  const [bindModalOpen, setBindModalOpen] = useState(false);
  const [bindServiceId, setBindServiceId] = useState('');
  const [bindPending, setBindPending] = useState(false);
  const [runModalOpen, setRunModalOpen] = useState(false);
  const [runInputDraft, setRunInputDraft] = useState('');
  const [validationPending, setValidationPending] = useState(false);
  const [validationResult, setValidationResult] = useState<ScriptValidationResult | null>(null);
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [filesPaneOpen, setFilesPaneOpen] = useState(true);
  const [editorView, setEditorView] = useState<StudioEditorView>('source');
  const [resultView, setResultView] = useState<StudioResultView>('runtime');
  const [selectedRuntimeActorId, setSelectedRuntimeActorId] = useState('');
  const [selectedProposalId, setSelectedProposalId] = useState('');

  const scopeBacked = appContext.scopeResolved && appContext.scriptStorageMode === 'scope';

  useEffect(() => {
    if (!selectedDraftKey && drafts[0]?.key) {
      setSelectedDraftKey(drafts[0].key);
      return;
    }

    if (selectedDraftKey && !drafts.some(draft => draft.key === selectedDraftKey)) {
      setSelectedDraftKey(drafts[0]?.key || '');
    }
  }, [drafts, selectedDraftKey]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(drafts));
    } catch {
      // Ignore storage errors in restricted browser contexts.
    }
  }, [drafts]);

  const filteredDrafts = useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return drafts;
    }

    return drafts.filter(draft =>
      [draft.scriptId, draft.revision, draft.baseRevision]
        .join(' ')
        .toLowerCase()
        .includes(keyword));
  }, [drafts, search]);

  const filteredScopeScripts = useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return scopeScripts;
    }

    return scopeScripts.filter(detail =>
      [
        detail.script?.scriptId || '',
        detail.script?.activeRevision || '',
        detail.scopeId || '',
      ]
        .join(' ')
        .toLowerCase()
        .includes(keyword));
  }, [scopeScripts, search]);

  const selectedDraft = useMemo(
    () => drafts.find(draft => draft.key === selectedDraftKey) || drafts[0] || null,
    [drafts, selectedDraftKey],
  );
  const askAiPreviewEntry = useMemo(
    () => askAiGeneratedPackage
      ? getSelectedPackageEntry(askAiGeneratedPackage, askAiGeneratedFilePath || askAiGeneratedPackage.entrySourcePath)
      : null,
    [askAiGeneratedFilePath, askAiGeneratedPackage],
  );
  const selectedPackageEntries = useMemo(
    () => selectedDraft ? getPackageEntries(selectedDraft.package) : [],
    [selectedDraft],
  );
  const selectedPackageEntry = useMemo(
    () => selectedDraft ? getSelectedPackageEntry(selectedDraft.package, selectedDraft.selectedFilePath) : null,
    [selectedDraft],
  );
  const storedScopePackage = useMemo(
    () => selectedDraft?.scopeDetail?.source?.sourceText
      ? deserializePersistedSource(selectedDraft.scopeDetail.source.sourceText)
      : null,
    [selectedDraft?.scopeDetail?.source?.sourceText],
  );
  const activeCatalog = useMemo(() => {
    const scopeScriptId = selectedDraft?.scopeDetail?.script?.scriptId || '';
    if (scopeScriptId && scopeCatalogsByScriptId[scopeScriptId]) {
      return scopeCatalogsByScriptId[scopeScriptId];
    }

    const draftScriptId = selectedDraft?.scriptId || '';
    return draftScriptId ? scopeCatalogsByScriptId[draftScriptId] || null : null;
  }, [scopeCatalogsByScriptId, selectedDraft?.scopeDetail?.script?.scriptId, selectedDraft?.scriptId]);
  const proposalDecisions = useMemo(
    () => Object.values(proposalDecisionsById).sort((left, right) => {
      const leftActive = activeCatalog?.lastProposalId === left.proposalId ? 1 : 0;
      const rightActive = activeCatalog?.lastProposalId === right.proposalId ? 1 : 0;
      if (leftActive !== rightActive) {
        return rightActive - leftActive;
      }

      return right.candidateRevision.localeCompare(left.candidateRevision);
    }),
    [activeCatalog?.lastProposalId, proposalDecisionsById],
  );
  const activeRuntimeSnapshot = useMemo(() => {
    if (selectedRuntimeActorId) {
      return runtimeSnapshots.find(snapshot => snapshot.actorId === selectedRuntimeActorId) ||
        (selectedDraft?.lastSnapshot?.actorId === selectedRuntimeActorId ? selectedDraft.lastSnapshot : null);
    }

    if (selectedDraft?.lastSnapshot) {
      return selectedDraft.lastSnapshot;
    }

    if (selectedDraft?.runtimeActorId) {
      return runtimeSnapshots.find(snapshot => snapshot.actorId === selectedDraft.runtimeActorId) || null;
    }

    return null;
  }, [runtimeSnapshots, selectedDraft?.lastSnapshot, selectedDraft?.runtimeActorId, selectedRuntimeActorId]);
  const activeProposal = useMemo(() => {
    if (selectedProposalId) {
      return proposalDecisionsById[selectedProposalId] ||
        (selectedDraft?.lastPromotion?.proposalId === selectedProposalId ? selectedDraft.lastPromotion : null);
    }

    if (selectedDraft?.lastPromotion) {
      return selectedDraft.lastPromotion;
    }

    return activeCatalog?.lastProposalId
      ? proposalDecisionsById[activeCatalog.lastProposalId] || null
      : null;
  }, [activeCatalog?.lastProposalId, proposalDecisionsById, selectedDraft?.lastPromotion, selectedProposalId]);
  const snapshotView = parseSnapshotView(activeRuntimeSnapshot);
  const validationSummary = summarizeValidation(validationResult, validationPending);
  const validationMarkers = useMemo(
    () => buildEditorMarkers(validationResult, selectedPackageEntry?.path || validationResult?.primarySourcePath || 'Behavior.cs'),
    [selectedPackageEntry?.path, validationResult],
  );
  const visibleProblems = validationResult?.diagnostics || [];
  const showValidationBadge = validationPending || validationResult != null;
  const hasScopeChanges = isScopeDetailDirty(selectedDraft);

  useEffect(() => {
    setValidationResult(null);
    setDiagnosticsOpen(false);
  }, [selectedDraft?.key]);

  useEffect(() => {
    const model = editorRef.current?.getModel();
    if (!model) {
      return;
    }

    monacoEditor.editor.setModelMarkers(model, 'aevatar-script-validation', validationMarkers);

    return () => {
      monacoEditor.editor.setModelMarkers(model, 'aevatar-script-validation', []);
    };
  }, [validationMarkers, selectedDraft?.key]);

  useEffect(() => {
    if (!selectedDraft) {
      return;
    }

    const validationToken = validationRequestRef.current + 1;
    validationRequestRef.current = validationToken;
    const controller = new AbortController();
    const timer = window.setTimeout(async () => {
      setValidationPending(true);
      try {
        const result = await api.app.validateDraftScript({
          scriptId: selectedDraft.scriptId,
          scriptRevision: selectedDraft.revision,
          package: selectedDraft.package,
        }, controller.signal) as ScriptValidationResult;

        if (validationRequestRef.current !== validationToken) {
          return;
        }

        setValidationResult(result);
      } catch (error: any) {
        if (controller.signal.aborted || validationRequestRef.current !== validationToken) {
          return;
        }

        setValidationResult({
          success: false,
          scriptId: selectedDraft.scriptId,
          scriptRevision: selectedDraft.revision,
          primarySourcePath: selectedDraft.selectedFilePath || 'Behavior.cs',
          errorCount: 1,
          warningCount: 0,
          diagnostics: [
            {
              severity: 'error',
              code: 'SCRIPT_VALIDATION_REQUEST',
              message: error?.message || 'Validation request failed.',
              filePath: '',
              startLine: null,
              startColumn: null,
              endLine: null,
              endColumn: null,
              origin: 'host',
            },
          ],
        });
      } finally {
        if (validationRequestRef.current === validationToken) {
          setValidationPending(false);
        }
      }
    }, 320);

    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [selectedDraft?.key, selectedDraft?.scriptId, selectedDraft?.revision, selectedDraft?.selectedFilePath, selectedDraft?.package]);

  useEffect(() => {
    if (!scopeBacked) {
      setScopeScripts([]);
      setScopeCatalogsByScriptId({});
      setProposalDecisionsById({});
      return;
    }

    void loadScopeScripts(true);
  }, [scopeBacked, appContext.scopeId]);

  useEffect(() => {
    if (!appContext.scriptsEnabled) {
      setRuntimeSnapshots([]);
      return;
    }

    void loadRuntimeSnapshots(true);
  }, [appContext.scriptsEnabled]);

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.altKey || event.shiftKey || !(event.metaKey || event.ctrlKey) || event.key.toLowerCase() !== 's') {
        return;
      }

      event.preventDefault();
      void handleSaveScript();
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [selectedDraft?.key, selectedDraft?.scriptId, selectedDraft?.revision, selectedDraft?.package, selectedDraft?.baseRevision, scopeBacked]);

  useEffect(() => {
    if (!selectedDraft) {
      return;
    }

    setSelectedRuntimeActorId(selectedDraft.lastSnapshot?.actorId || selectedDraft.runtimeActorId || '');
    setSelectedProposalId(selectedDraft.lastPromotion?.proposalId || '');

    if (resultView === 'runtime' && (selectedDraft.lastRun || selectedDraft.lastChatResponse || selectedDraft.lastSnapshot)) {
      return;
    }

    if (resultView === 'save' && selectedDraft.scopeDetail) {
      return;
    }

    if (resultView === 'promotion' && selectedDraft.lastPromotion) {
      return;
    }

    if (selectedDraft.lastRun || selectedDraft.lastChatResponse || selectedDraft.lastSnapshot) {
      setResultView('runtime');
      return;
    }

    if (selectedDraft.scopeDetail) {
      setResultView('save');
      return;
    }

    if (selectedDraft.lastPromotion) {
      setResultView('promotion');
    }
  }, [
    selectedDraft?.key,
    selectedDraft?.lastRun,
    selectedDraft?.lastSnapshot,
    selectedDraft?.scopeDetail,
    selectedDraft?.lastPromotion,
    resultView,
  ]);

  const handleMonacoBeforeMount: BeforeMount = monaco => {
    monaco.editor.defineTheme('aevatar-script-light', {
      base: 'vs',
      inherit: true,
      rules: [
        { token: 'comment', foreground: '8D7B68' },
        { token: 'keyword', foreground: '9B4D19', fontStyle: 'bold' },
        { token: 'string', foreground: '356A4C' },
        { token: 'number', foreground: 'A05A24' },
        { token: 'type.identifier', foreground: '315A84' },
      ],
      colors: {
        'editor.background': '#FCFBF8',
        'editor.foreground': '#2A2723',
        'editorLineNumber.foreground': '#B6AA99',
        'editorLineNumber.activeForeground': '#6A5E4E',
        'editorLineNumber.dimmedForeground': '#D5CCC0',
        'editor.lineHighlightBackground': '#F5EFE5',
        'editor.selectionBackground': '#DCE8FF',
        'editor.inactiveSelectionBackground': '#ECF2FF',
        'editorCursor.foreground': '#C06836',
        'editorWhitespace.foreground': '#E7DED2',
        'editorIndentGuide.background1': '#EDE5D9',
        'editorIndentGuide.activeBackground1': '#D4C8B8',
        'editorOverviewRuler.border': '#00000000',
        'editorGutter.background': '#FCFBF8',
        'editorWidget.background': '#FFFCF8',
        'editorWidget.border': '#E7DED4',
        'scrollbarSlider.background': '#D8CCBD88',
        'scrollbarSlider.hoverBackground': '#C8B9A588',
        'scrollbarSlider.activeBackground': '#B7A59188',
      },
    });
  };

  const handleEditorMount: OnMount = editor => {
    editorRef.current = editor;
    const model = editor.getModel();
    if (model) {
      monacoEditor.editor.setModelMarkers(model, 'aevatar-script-validation', validationMarkers);
    }
  };

  function jumpToDiagnostic(diagnostic: ScriptValidationDiagnostic) {
    const diagnosticFilePath = diagnostic.filePath || selectedDraft?.selectedFilePath || '';
    if (selectedDraft && diagnosticFilePath && diagnosticFilePath !== selectedDraft.selectedFilePath) {
      setEditorView('source');
      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        selectedFilePath: diagnosticFilePath,
      }));
      window.setTimeout(() => jumpToDiagnostic(diagnostic), 0);
      return;
    }

    if (!diagnostic.startLine || !diagnostic.startColumn) {
      return;
    }

    setEditorView('source');
    editorRef.current?.revealPositionInCenter({
      lineNumber: diagnostic.startLine,
      column: diagnostic.startColumn,
    });
    editorRef.current?.setPosition({
      lineNumber: diagnostic.startLine,
      column: diagnostic.startColumn,
    });
    editorRef.current?.focus();
  }

  function updateDraft(targetKey: string, recipe: (draft: ScriptDraft) => ScriptDraft) {
    setDrafts(prev => prev.map(draft => (
      draft.key === targetKey
        ? {
            ...recipe(draft),
            updatedAtUtc: new Date().toISOString(),
          }
        : draft
    )));
  }

  function handleCreateDraft() {
    const nextDraft = createDraft(drafts.length + 1);
    setDrafts(prev => [nextDraft, ...prev]);
    setSelectedDraftKey(nextDraft.key);
  }

  function handleSelectDraftFile(filePath: string) {
    if (!selectedDraft) {
      return;
    }

    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      selectedFilePath: filePath,
    }));
    setEditorView('source');
    setFilesPaneOpen(true);
  }

  function handleAddPackageFile(kind: 'csharp' | 'proto') {
    if (!selectedDraft) {
      return;
    }

    const defaultPath = kind === 'csharp'
      ? `NewFile${selectedDraft.package.csharpSources.length + 1}.cs`
      : `schema${selectedDraft.package.protoFiles.length + 1}.proto`;
    const nextPath = window.prompt(`Add ${kind === 'csharp' ? 'C#' : 'proto'} file`, defaultPath);
    if (!nextPath?.trim()) {
      return;
    }

    const nextPackage = addPackageFile(selectedDraft.package, kind, nextPath.trim());
    const addedEntry = getSelectedPackageEntry(nextPackage, nextPath.trim());
    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      package: nextPackage,
      selectedFilePath: addedEntry?.path || draft.selectedFilePath,
    }));
    setEditorView('source');
    setFilesPaneOpen(true);
  }

  function handleRenamePackageFile(filePath: string) {
    if (!selectedDraft) {
      return;
    }

    const nextPath = window.prompt('Rename file', filePath);
    if (!nextPath?.trim() || nextPath.trim() === filePath) {
      return;
    }

    const nextPackage = renamePackageFile(selectedDraft.package, filePath, nextPath.trim());
    const renamedEntry = getSelectedPackageEntry(nextPackage, nextPath.trim());
    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      package: nextPackage,
      selectedFilePath: draft.selectedFilePath === filePath
        ? (renamedEntry?.path || draft.selectedFilePath)
        : draft.selectedFilePath,
    }));
    setFilesPaneOpen(true);
  }

  function handleRemovePackageFile(filePath: string) {
    if (!selectedDraft) {
      return;
    }

    const nextPackage = removePackageFile(selectedDraft.package, filePath);
    const nextSelected = getSelectedPackageEntry(nextPackage, selectedDraft.selectedFilePath);
    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      package: nextPackage,
      selectedFilePath: nextSelected?.path || '',
    }));
  }

  function handleSetEntryFile(filePath: string) {
    if (!selectedDraft) {
      return;
    }

    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      package: setEntrySourcePath(draft.package, filePath),
      selectedFilePath: filePath,
    }));
    setFilesPaneOpen(true);
  }

  async function loadScopeScripts(silent = false) {
    if (!scopeBacked) {
      return;
    }

    setScopeScriptsPending(true);
    try {
      const response = await api.app.listScripts(true) as ScopedScriptDetail[];
      const sorted = Array.isArray(response)
        ? [...response].sort((left, right) => {
          const rightStamp = Date.parse(right.script?.updatedAt || '');
          const leftStamp = Date.parse(left.script?.updatedAt || '');
          return (Number.isNaN(rightStamp) ? 0 : rightStamp) - (Number.isNaN(leftStamp) ? 0 : leftStamp);
        })
        : [];
      setScopeScripts(sorted);
      await primeScopeHistory(sorted);
      if (!silent) {
        onFlash('Scope scripts refreshed', 'success');
      }
    } catch (error: any) {
      if (!silent) {
        onFlash(error?.message || 'Failed to load saved scripts', 'error');
      }
    } finally {
      setScopeScriptsPending(false);
    }
  }

  async function primeScopeHistory(details: ScopedScriptDetail[]) {
    const scriptIds = Array.from(new Set(
      details
        .map(detail => detail.script?.scriptId || '')
        .filter(Boolean),
    ));

    if (scriptIds.length === 0) {
      setScopeCatalogsByScriptId({});
      setProposalDecisionsById({});
      return;
    }

    setProposalDecisionsPending(true);
    try {
      const catalogResults = await Promise.all(scriptIds.map(async scriptId => {
        try {
          const catalog = await api.app.getScriptCatalog(scriptId) as ScriptCatalogSnapshot;
          return [scriptId, catalog] as const;
        } catch {
          return null;
        }
      }));

      const nextCatalogs: Record<string, ScriptCatalogSnapshot> = {};
      const proposalIds = new Set<string>();
      for (const item of catalogResults) {
        if (!item?.[1]) {
          continue;
        }

        nextCatalogs[item[0]] = item[1];
        if (item[1].lastProposalId) {
          proposalIds.add(item[1].lastProposalId);
        }
      }

      setScopeCatalogsByScriptId(nextCatalogs);

      if (proposalIds.size === 0) {
        setProposalDecisionsById({});
        return;
      }

      const decisions = await Promise.all(Array.from(proposalIds).map(async proposalId => {
        try {
          const decision = await api.app.getEvolutionDecision(proposalId) as ScriptPromotionDecision;
          return [proposalId, decision] as const;
        } catch {
          return null;
        }
      }));

      const nextDecisions: Record<string, ScriptPromotionDecision> = {};
      for (const item of decisions) {
        if (!item?.[1]) {
          continue;
        }

        nextDecisions[item[0]] = item[1];
      }

      setProposalDecisionsById(nextDecisions);
    } finally {
      setProposalDecisionsPending(false);
    }
  }

  async function loadRuntimeSnapshots(silent = false) {
    setRuntimeSnapshotsPending(true);
    try {
      const response = await api.app.listScriptRuntimes(24) as ScriptReadModelSnapshot[];
      const sorted = Array.isArray(response)
        ? [...response].sort((left, right) => {
          const rightStamp = Date.parse(right.updatedAt || '');
          const leftStamp = Date.parse(left.updatedAt || '');
          return (Number.isNaN(rightStamp) ? 0 : rightStamp) - (Number.isNaN(leftStamp) ? 0 : leftStamp);
        })
        : [];
      setRuntimeSnapshots(sorted);
      if (!silent) {
        onFlash('Runtime snapshots refreshed', 'success');
      }
    } catch (error: any) {
      if (!silent) {
        onFlash(error?.message || 'Failed to load runtime snapshots', 'error');
      }
    } finally {
      setRuntimeSnapshotsPending(false);
    }
  }

  function upsertRuntimeSnapshot(snapshot: ScriptReadModelSnapshot) {
    setRuntimeSnapshots(prev => {
      const next = prev.filter(item => item.actorId !== snapshot.actorId);
      next.unshift(snapshot);
      return next.sort((left, right) => {
        const rightStamp = Date.parse(right.updatedAt || '');
        const leftStamp = Date.parse(left.updatedAt || '');
        return (Number.isNaN(rightStamp) ? 0 : rightStamp) - (Number.isNaN(leftStamp) ? 0 : leftStamp);
      });
    });
  }

  function openScopeScript(detail: ScopedScriptDetail) {
    const scriptId = detail.script?.scriptId || detail.source?.revision || `script-${drafts.length + 1}`;
    const normalizedTargetId = normalizeStudioId(scriptId, 'script');
    const existing = drafts.find(draft => normalizeStudioId(draft.scriptId, 'script') === normalizedTargetId);
    const nextDraft = hydrateDraftFromScopeDetail(detail, drafts.length + 1, existing);

    if (existing) {
      setDrafts(prev => prev.map(draft => draft.key === existing.key ? nextDraft : draft));
      setSelectedDraftKey(existing.key);
    } else {
      setDrafts(prev => [nextDraft, ...prev]);
      setSelectedDraftKey(nextDraft.key);
    }

    setResultView('save');
    setSelectedProposalId(scopeCatalogsByScriptId[scriptId]?.lastProposalId || '');
    onFlash('Saved script loaded into the editor', 'success');
  }

  async function refreshSnapshot(actorId: string, silent = false) {
    const normalizedActorId = actorId.trim();
    if (!normalizedActorId) {
      if (!silent) {
        onFlash('Run the draft first', 'info');
      }
      return null;
    }

    setSnapshotPending(true);
    try {
      const snapshot = await api.app.getRuntimeReadModel(normalizedActorId) as ScriptReadModelSnapshot;
      setDrafts(prev => prev.map(draft => (
        draft.runtimeActorId === snapshot.actorId
          ? {
              ...draft,
              lastSnapshot: snapshot,
              runtimeActorId: snapshot.actorId || draft.runtimeActorId,
              definitionActorId: snapshot.definitionActorId || draft.definitionActorId,
              updatedAtUtc: new Date().toISOString(),
            }
          : draft
      )));
      upsertRuntimeSnapshot(snapshot);
      setSelectedRuntimeActorId(snapshot.actorId || normalizedActorId);
      setResultView('runtime');
      if (!silent) {
        openWorkspaceSection('activity');
      }

      if (!silent) {
        onFlash('Runtime snapshot refreshed', 'success');
      }

      return snapshot;
    } catch (error: any) {
      if (!silent) {
        onFlash(error?.message || 'Failed to load runtime snapshot', 'error');
      }

      return null;
    } finally {
      setSnapshotPending(false);
    }
  }

  async function waitForSnapshot(actorId: string) {
    const normalizedActorId = actorId.trim();
    if (!normalizedActorId) {
      return null;
    }

    for (let attempt = 0; attempt < 6; attempt += 1) {
      const snapshot = await refreshSnapshot(normalizedActorId, true);
      if (snapshot) {
        return snapshot;
      }

      await wait(320);
    }

    return null;
  }

  async function handleSelectRuntime(actorId: string) {
    setSelectedRuntimeActorId(actorId);
    setResultView('runtime');
    openWorkspaceSection('activity');

    const knownSnapshot = runtimeSnapshots.find(snapshot => snapshot.actorId === actorId);
    if (!knownSnapshot) {
      await refreshSnapshot(actorId, true);
    }
  }

  function handleSelectProposal(proposalId: string) {
    setSelectedProposalId(proposalId);
    setResultView('promotion');
    openWorkspaceSection('activity');
  }

  async function refreshCurrentCatalog() {
    const scriptId = selectedDraft?.scopeDetail?.script?.scriptId || selectedDraft?.scriptId || '';
    if (!scopeBacked || !scriptId) {
      return;
    }

    try {
      const catalog = await api.app.getScriptCatalog(scriptId) as ScriptCatalogSnapshot;
      setScopeCatalogsByScriptId(prev => ({
        ...prev,
        [scriptId]: catalog,
      }));
      if (catalog.lastProposalId) {
        try {
          const decision = await api.app.getEvolutionDecision(catalog.lastProposalId) as ScriptPromotionDecision;
          setProposalDecisionsById(prev => ({
            ...prev,
            [catalog.lastProposalId]: decision,
          }));
        } catch {
          // Ignore secondary proposal refresh failures.
        }
      }

      onFlash('Catalog history refreshed', 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to load catalog history', 'error');
    }
  }

  async function refreshCurrentProposalDecision() {
    const proposalId = activeProposal?.proposalId || activeCatalog?.lastProposalId || selectedDraft?.lastPromotion?.proposalId || '';
    if (!proposalId) {
      return;
    }

    setProposalDecisionsPending(true);
    try {
      const decision = await api.app.getEvolutionDecision(proposalId) as ScriptPromotionDecision;
      setProposalDecisionsById(prev => ({
        ...prev,
        [proposalId]: decision,
      }));
      setSelectedProposalId(proposalId);
      onFlash('Proposal decision refreshed', 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to load proposal decision', 'error');
    } finally {
      setProposalDecisionsPending(false);
    }
  }

  async function handleConfirmBindScript() {
    const scopeId = appContext.scopeId;
    if (!scopeId || !selectedDraft) return;
    const scriptId = selectedDraft.scriptId;
    if (!scriptId) { onFlash('Save the script first', 'error'); return; }
    setBindPending(true);
    try {
      const sid = bindServiceId.trim() || scriptId;
      await api.scope.bindScript(scopeId, scriptId, scriptId, sid);
      setBindModalOpen(false);
      onFlash(`Bound script "${scriptId}" as service "${sid}"`, 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to bind script', 'error');
    } finally {
      setBindPending(false);
    }
  }

  function handleOpenBindModal() {
    if (!selectedDraft) {
      return;
    }

    const activeRevision =
      activeCatalog?.activeRevision ||
      selectedDraft.scopeDetail?.script?.activeRevision ||
      '';
    const currentDraftRevision = (selectedDraft.baseRevision || selectedDraft.revision || '').trim();
    if (activeRevision && currentDraftRevision && activeRevision === currentDraftRevision) {
      setBindModalOpen(true);
      return;
    }

    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      revision: getNextCandidateRevision(draft.baseRevision || ''),
    }));
    setPromotionModalOpen(true);
  }

  async function handleSaveScript() {
    if (!selectedDraft) {
      return;
    }

    const persistedSource = serializePersistedSource(selectedDraft.package);
    if (!persistedSource.trim()) {
      onFlash('Script source is required', 'error');
      return;
    }

    if (!scopeBacked) {
      onFlash('Draft is already stored locally on this device', 'success');
      return;
    }

    const scriptId = normalizeStudioId(selectedDraft.scriptId, 'script');
    const revision = normalizeStudioId(selectedDraft.revision, 'draft');
    const expectedBaseRevision = (selectedDraft.baseRevision || '').trim() || undefined;

    setSavePending(true);
    try {
      const accepted = await api.app.saveScript({
        scriptId,
        revisionId: revision,
        expectedBaseRevision,
        package: selectedDraft.package,
      }) as AppScopeScriptSaveAcceptedResponse;
      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        scriptId: accepted.scriptId || scriptId,
        definitionActorId: accepted.definitionActorId || draft.definitionActorId,
        lastSourceHash: accepted.sourceHash || draft.lastSourceHash,
      }));
      onFlash(`Save accepted for ${accepted.scriptId}. Waiting for the scope catalog to catch up.`, 'info');

      const observationRequest = buildSaveObservationRequest(accepted);
      let observation: AppScopeScriptSaveObservationResult | null = null;
      for (let attempt = 0; attempt < 8; attempt += 1) {
        observation = await api.app.observeScriptSave(accepted.scriptId, observationRequest) as AppScopeScriptSaveObservationResult;
        if (observation.isTerminal) {
          break;
        }

        await wait(250);
      }

      await loadScopeScripts(true);
      if (observation?.status === 'rejected') {
        throw new Error(observation.message);
      }

      if (observation?.status === 'applied') {
        const detail = await api.app.getScript(accepted.scriptId) as ScopedScriptDetail;
        const savedPackage = normalizeDraftPackageForAppRuntime(null, detail.source?.sourceText || persistedSource).package;
        const savedEntry = getSelectedPackageEntry(savedPackage, selectedDraft.selectedFilePath);

        updateDraft(selectedDraft.key, draft => ({
          ...draft,
          scriptId: detail.script?.scriptId || scriptId,
          revision: detail.script?.activeRevision || detail.source?.revision || revision,
          baseRevision: detail.script?.activeRevision || detail.source?.revision || revision,
          package: savedPackage,
          selectedFilePath: savedEntry?.path || draft.selectedFilePath,
          definitionActorId: detail.script?.definitionActorId || detail.source?.definitionActorId || draft.definitionActorId,
          lastSourceHash: detail.source?.sourceHash || detail.script?.activeSourceHash || draft.lastSourceHash,
          scopeDetail: detail,
        }));
      }

      setResultView('save');
      openWorkspaceSection('activity');
      setSelectedProposalId('');
      onFlash(
        observation?.status === 'applied'
          ? 'Script saved to the current scope'
          : observation?.message || 'Save request is still waiting to appear in the scope catalog',
        observation?.status === 'applied' ? 'success' : 'info',
      );
    } catch (error: any) {
      onFlash(error?.message || 'Failed to save script', 'error');
    } finally {
      setSavePending(false);
    }
  }

  async function copyTextToClipboard(text: string) {
    if (!text.trim()) {
      onFlash('Nothing to copy', 'info');
      return false;
    }

    if (!navigator.clipboard?.writeText) {
      onFlash('Clipboard is unavailable in this browser context', 'error');
      return false;
    }

    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch (error: any) {
      onFlash(error?.message || 'Failed to copy to clipboard', 'error');
      return false;
    }
  }

  function openRunModal() {
    if (!selectedDraft) {
      return;
    }

    setRunInputDraft(selectedDraft.input);
    setRunModalOpen(true);
  }

  async function handleConfirmRunDraft() {
    if (!selectedDraft) {
      return;
    }

    await handleRunDraft(runInputDraft);
  }

  async function handleRunDraft(inputText: string) {
    if (!selectedDraft) {
      return;
    }

    const runMode = 'script' as ScriptRunMode;

    const normalizedPackage = normalizeDraftPackageForAppRuntime(selectedDraft.package);
    const persistedSource = serializePersistedSource(normalizedPackage.package);
    if (!persistedSource.trim()) {
      onFlash('Script source is required', 'error');
      return;
    }

    const scriptId = normalizeStudioId(selectedDraft.scriptId, 'script');
    const revision = normalizeStudioId(selectedDraft.revision, 'draft');

    if (normalizedPackage.migrated) {
      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        package: normalizedPackage.package,
        selectedFilePath: normalizedPackage.package.entrySourcePath || draft.selectedFilePath,
        definitionActorId: '',
        runtimeActorId: '',
        lastSourceHash: '',
        lastRun: null,
        lastChatResponse: null,
        lastSnapshot: null,
        lastPromotion: null,
        scopeDetail: null,
      }));
    }

    updateDraft(selectedDraft.key, draft => ({
      ...draft,
      input: inputText,
    }));

    setRunPending(true);
    try {
      if (runMode === 'chat') {
        await handleRunDraftChat(inputText);
      } else {
        await handleRunDraftScript(inputText, scriptId, revision, normalizedPackage);
      }
    } catch (error: any) {
      onFlash(error?.message || 'Draft run failed', 'error');
    } finally {
      setRunPending(false);
    }
  }

  async function handleRunDraftChat(inputText: string) {
    const scopeId = appContext.scopeId;
    if (!scopeId) {
      onFlash('Scope is not resolved. Configure a scope to use chat mode.', 'error');
      return;
    }

    let accumulated = '';
    await api.scope.streamDefaultChat(scopeId, inputText, undefined, undefined, (frame: any) => {
      if (frame.textMessageContent?.delta) {
        accumulated += frame.textMessageContent.delta;
      } else if (frame.type === 'TEXT_MESSAGE_CONTENT' && frame.delta) {
        accumulated += frame.delta;
      } else if (frame.runError?.message || (frame.type === 'RUN_ERROR' && frame.message)) {
        const msg = frame.runError?.message || frame.message;
        accumulated += `\n[Error] ${msg}`;
      }
    });

    updateDraft(selectedDraft!.key, draft => ({
      ...draft,
      lastChatResponse: accumulated || '(no response)',
    }));

    setRunModalOpen(false);
    setResultView('runtime');
    openWorkspaceSection('activity');
    onFlash('Chat run completed', 'success');
  }

  async function handleRunDraftScript(
    inputText: string,
    scriptId: string,
    revision: string,
    normalizedPackage: ReturnType<typeof normalizeDraftPackageForAppRuntime>,
  ) {
    const scopeId = appContext.scopeId;
    if (!scopeId) {
      onFlash('Scope is not resolved. Configure a scope to use script draft-run.', 'error');
      return;
    }

    const response = await api.app.runDraftScript(scopeId, {
      scriptId,
      scriptRevision: revision,
      package: normalizedPackage.package,
      input: inputText,
      definitionActorId: normalizedPackage.migrated ? '' : selectedDraft!.definitionActorId,
      runtimeActorId: normalizedPackage.migrated ? '' : selectedDraft!.runtimeActorId,
    }) as DraftRunResult;

    updateDraft(selectedDraft!.key, draft => ({
      ...draft,
      input: inputText,
      scriptId: response.scriptId || scriptId,
      revision: response.scriptRevision || revision,
      definitionActorId: response.definitionActorId || draft.definitionActorId,
      runtimeActorId: response.runtimeActorId || draft.runtimeActorId,
      lastSourceHash: response.sourceHash || draft.lastSourceHash,
      lastRun: response,
    }));

    setRunModalOpen(false);
    setResultView('runtime');
    openWorkspaceSection('activity');
    setSelectedRuntimeActorId(response.runtimeActorId || '');
    const snapshot = await waitForSnapshot(response.runtimeActorId || '');
    await loadRuntimeSnapshots(true);
    onFlash(snapshot ? 'Draft run completed' : 'Draft accepted. Runtime snapshot is catching up.', snapshot ? 'success' : 'info');
  }

  async function handlePromote() {
    if (!selectedDraft) {
      return;
    }

    const persistedSource = serializePersistedSource(selectedDraft.package);
    if (!persistedSource.trim()) {
      onFlash('Script source is required', 'error');
      return;
    }

    const scriptId = normalizeStudioId(selectedDraft.scriptId, 'script');
    const candidateRevision = normalizeStudioId(selectedDraft.revision, 'draft');
    const rawBaseRevision = (selectedDraft.baseRevision || selectedDraft.scopeDetail?.script?.activeRevision || '').trim();
    const baseRevision = rawBaseRevision ? normalizeStudioId(rawBaseRevision, 'base') : '';

    setPromotionPending(true);
    try {
      const decision = await api.scripts.proposeEvolution({
        scriptId,
        baseRevision,
        candidateRevision,
        candidatePackage: selectedDraft.package,
        candidateSourceHash: '',
        reason: selectedDraft.reason,
        proposalId: `${scriptId}-${candidateRevision}-${Date.now()}`,
      }) as ScriptPromotionDecision;

      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        scriptId,
        revision: candidateRevision,
        lastPromotion: decision,
      }));
      setProposalDecisionsById(prev => ({
        ...prev,
        [decision.proposalId]: decision,
      }));
      setSelectedProposalId(decision.proposalId || '');

      setPromotionModalOpen(false);
      setResultView('promotion');
      openWorkspaceSection('activity');
      let observedCatalog: ScriptCatalogSnapshot | null = null;
      if (decision?.accepted && scopeBacked) {
        for (let attempt = 0; attempt < 8; attempt += 1) {
          try {
            const catalog = await api.app.getScriptCatalog(scriptId) as ScriptCatalogSnapshot;
            if (catalogMatchesPromotion(catalog, decision)) {
              observedCatalog = catalog;
              break;
            }
          } catch {
            // Ignore transient query failures while the catalog is catching up.
          }

          await wait(250);
        }
      }

      if (scopeBacked) {
        await loadScopeScripts(true);
      }
      if (observedCatalog) {
        const detail = await api.app.getScript(scriptId) as ScopedScriptDetail;
        updateDraft(selectedDraft.key, draft => ({
          ...draft,
          scriptId: detail.script?.scriptId || scriptId,
          revision: detail.script?.activeRevision || detail.source?.revision || draft.revision,
          baseRevision: detail.script?.activeRevision || detail.source?.revision || draft.baseRevision,
          definitionActorId: detail.script?.definitionActorId || detail.source?.definitionActorId || decision.definitionActorId || draft.definitionActorId,
          lastSourceHash: detail.source?.sourceHash || detail.script?.activeSourceHash || draft.lastSourceHash,
          scopeDetail: detail,
        }));
      }

      onFlash(
        decision?.accepted
          ? observedCatalog
            ? 'Promotion applied to the catalog'
            : 'Promotion accepted. Catalog read model is catching up.'
          : (decision?.failureReason || 'Promotion rejected'),
        decision?.accepted
          ? observedCatalog
            ? 'success'
            : 'info'
          : 'error',
      );
    } catch (error: any) {
      onFlash(error?.message || 'Promotion failed', 'error');
    } finally {
      setPromotionPending(false);
    }
  }

  async function handleAskAiGenerate() {
    if (!selectedDraft) {
      return;
    }

    if (!askAiPrompt.trim()) {
      onFlash('Describe the script you want', 'error');
      return;
    }

    const targetKey = selectedDraft.key;
    setAskAiTargetDraftKey(targetKey);
    setAskAiPending(true);
    setAskAiReasoning('');
    setAskAiAnswer('');
    setAskAiGeneratedSource('');
    setAskAiGeneratedPackage(null);
    setAskAiGeneratedFilePath('');

    try {
      const response = await api.assistant.authorScript({
        prompt: askAiPrompt.trim(),
        currentSource: selectedPackageEntry?.content || '',
        currentPackage: selectedDraft.package,
        currentFilePath: selectedDraft.selectedFilePath,
        metadata: {
          script_id: selectedDraft.scriptId,
          revision: selectedDraft.revision,
        },
      }, {
        onReasoning: text => setAskAiReasoning(text),
        onText: text => setAskAiAnswer(text),
      });

      const generatedPackage = coerceScriptPackage(response.scriptPackage);
      const generatedFilePath = response.currentFilePath || selectedDraft.selectedFilePath;
      const generatedEntry = generatedPackage
        ? getSelectedPackageEntry(generatedPackage, generatedFilePath)
        : null;
      const generatedSource = generatedEntry?.content || response.text || '';

      setAskAiAnswer(response.text || generatedSource);
      setAskAiGeneratedSource(generatedSource);
      setAskAiGeneratedPackage(generatedPackage);
      setAskAiGeneratedFilePath(generatedEntry?.path || generatedFilePath);
      onFlash(generatedPackage ? 'AI package is ready to apply' : 'AI source is ready to apply', 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to generate script source', 'error');
    } finally {
      setAskAiPending(false);
    }
  }

  function handleApplyAskAiSource() {
    const targetKey = askAiTargetDraftKey || selectedDraft?.key || '';
    if (!targetKey) {
      onFlash('Open a draft before applying generated source', 'error');
      return;
    }

    if (!askAiGeneratedSource.trim()) {
      onFlash('Generate source before applying it', 'info');
      return;
    }

    const targetDraft = drafts.find(draft => draft.key === targetKey);
    if (!targetDraft) {
      onFlash('The original draft is no longer available', 'error');
      return;
    }

    updateDraft(targetKey, draft => ({
      ...draft,
      package: askAiGeneratedPackage || updatePackageFileContent(draft.package, draft.selectedFilePath, askAiGeneratedSource),
      selectedFilePath: askAiGeneratedPackage
        ? (getSelectedPackageEntry(askAiGeneratedPackage, askAiGeneratedFilePath || draft.selectedFilePath)?.path || draft.selectedFilePath)
        : draft.selectedFilePath,
    }));
    setSelectedDraftKey(targetKey);
    setEditorView('source');
    onFlash(askAiGeneratedPackage ? 'AI package applied to the editor' : 'AI source applied to the editor', 'success');
  }

  async function handleCopyAskAiSource() {
    const copied = await copyTextToClipboard(askAiGeneratedSource);
    if (copied) {
      onFlash('Generated source copied', 'success');
    }
  }

  if (!selectedDraft) {
    return null;
  }

  if (!appContext.scriptsEnabled) {
    return (
      <section className="flex-1 min-h-0 bg-[#F2F1EE] p-6">
        <div className="flex h-full items-center justify-center rounded-[32px] border border-[#E6E3DE] bg-white/96 p-8 shadow-[0_26px_64px_rgba(17,24,39,0.08)]">
          <div className="max-w-[360px] text-center">
            <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-[18px] bg-[#F3F0EA] text-gray-400">
              <Code2 size={20} />
            </div>
            <div className="mt-4 text-[18px] font-semibold text-gray-800">Scripts unavailable</div>
          </div>
        </div>
      </section>
    );
  }

  const promotionDiagnostics = Array.isArray(activeProposal?.validationReport?.diagnostics)
    ? activeProposal?.validationReport?.diagnostics || []
    : [];
  const runtimeSummary = activeRuntimeSnapshot
    ? `${snapshotView.status || 'updated'} · ${snapshotView.output || 'output pending'}`
    : selectedDraft.lastRun
      ? `Accepted · ${selectedDraft.lastRun.runId}`
      : selectedDraft.lastChatResponse != null
        ? `Chat · ${selectedDraft.lastChatResponse.slice(0, 60)}${selectedDraft.lastChatResponse.length > 60 ? '...' : ''}`
        : 'Run the draft to materialize output.';
  const saveSummary = scopeBacked
    ? activeCatalog
      ? `${activeCatalog.scriptId} · ${activeCatalog.activeRevision}`
      : selectedDraft.scopeDetail?.script
        ? `${selectedDraft.scopeDetail.script.scriptId} · ${selectedDraft.scopeDetail.script.activeRevision}`
        : 'Save this draft into the current scope.'
    : 'Local draft only. Sign in to save it into a scope.';
  const promotionSummary = activeProposal
    ? `${activeProposal.status || 'unknown'}${activeProposal.failureReason ? ` · ${activeProposal.failureReason}` : ''}`
    : activeCatalog?.lastProposalId
      ? `Latest proposal · ${activeCatalog.lastProposalId}`
      : 'Submit a promotion proposal when this draft is ready.';
  const scopeSelectionId = selectedDraft.scopeDetail?.script?.scriptId || '';
  const showFilesPane = filesPaneOpen;
  const packageModalOpen = editorView === 'package';
  const rightDrawerTab = packageModalOpen ? 'package' : workspacePanelOpen ? 'panels' : null;
  const rightDrawerOpen = rightDrawerTab !== null;
  const surfaceActionClass = (active = false) => `rounded-full border px-3 py-1.5 text-[11px] uppercase tracking-[0.14em] transition-colors ${
    active
      ? 'border-[color:var(--accent-border)] bg-[#FFF4F1] text-[color:var(--accent-text)]'
      : 'border-[#E5DED3] bg-white text-gray-500 hover:bg-[#F9F6F0]'
  }`;

  function openWorkspaceSection(section: 'library' | 'activity' | 'details') {
    setWorkspaceSection(section);
    setWorkspacePanelOpen(true);
    setEditorView('source');
  }

  function toggleRightDrawer(tab: 'panels' | 'package') {
    if (tab === 'panels') {
      if (workspacePanelOpen && !packageModalOpen) {
        setWorkspacePanelOpen(false);
        return;
      }

      setEditorView('source');
      setWorkspacePanelOpen(true);
      return;
    }

    if (packageModalOpen) {
      setEditorView('source');
      return;
    }

    setWorkspacePanelOpen(false);
    setEditorView('package');
  }

  function closeRightDrawer() {
    if (packageModalOpen) {
      setEditorView('source');
      return;
    }

    setWorkspacePanelOpen(false);
  }

  function renderResultDetailContent() {
    if (resultView === 'runtime') {
      if (!(selectedDraft.lastRun || selectedDraft.lastChatResponse || activeRuntimeSnapshot)) {
        return (
          <EmptyState
            title="No runtime output yet"
            copy="Run the current draft. The materialized read model will appear here."
          />
        );
      }

      if (selectedDraft.lastChatResponse != null && !selectedDraft.lastRun && !activeRuntimeSnapshot) {
        return (
          <div className="space-y-4">
            <div className="flex items-center justify-between gap-3">
              <div>
                <div className="text-[14px] font-semibold text-gray-800">Chat response</div>
                <div className="mt-1 text-[12px] text-gray-400">via scope default chat endpoint</div>
              </div>
              <div className="rounded-full border border-[#E5DED3] bg-white px-3 py-1 text-[11px] uppercase tracking-[0.14em] text-gray-500">
                completed
              </div>
            </div>
            <div className="grid gap-4 xl:grid-cols-2">
              <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                <div className="section-heading">Input</div>
                <pre className="mt-2 whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">{selectedDraft.input || '-'}</pre>
              </div>
              <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                <div className="section-heading">Output</div>
                <pre className="mt-2 whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">{selectedDraft.lastChatResponse || '-'}</pre>
              </div>
            </div>
          </div>
        );
      }

      return (
        <div className="space-y-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-[14px] font-semibold text-gray-800">Runtime output</div>
              <div className="mt-1 text-[12px] text-gray-400">{activeRuntimeSnapshot?.actorId || selectedDraft.lastRun?.runId || '-'}</div>
            </div>
            <div className="flex items-center gap-2">
              {(activeRuntimeSnapshot?.actorId || selectedDraft.runtimeActorId) ? (
                <button
                  type="button"
                  onClick={() => { void refreshSnapshot(activeRuntimeSnapshot?.actorId || selectedDraft.runtimeActorId); }}
                  className="panel-icon-button execution-logs-copy-action"
                  title="Refresh runtime result"
                  aria-label="Refresh runtime result"
                  disabled={snapshotPending}
                >
                  <RefreshCw size={14} className={snapshotPending ? 'animate-spin' : ''} />
                </button>
              ) : null}
              <div className="rounded-full border border-[#E5DED3] bg-white px-3 py-1 text-[11px] uppercase tracking-[0.14em] text-gray-500">
                {snapshotView.status || (selectedDraft.lastRun?.accepted ? 'accepted' : 'pending')}
              </div>
            </div>
          </div>

          <div className="grid gap-4 xl:grid-cols-2">
            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Input</div>
              <pre className="mt-2 whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">{snapshotView.input || selectedDraft.input || '-'}</pre>
            </div>
            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Output</div>
              <pre className="mt-2 whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">{snapshotView.output || '-'}</pre>
            </div>
          </div>

          <details className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3">
            <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
              Runtime details
            </summary>
            <div className="mt-3 grid gap-4 xl:grid-cols-2">
              <div className="rounded-[18px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
                <div className="section-heading">Notes</div>
                <div className="mt-2 text-[12px] leading-6 text-gray-600">
                  {snapshotView.notes.length > 0 ? snapshotView.notes.join(', ') : '-'}
                </div>
              </div>
              <div className="rounded-[18px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
                <div className="section-heading">Metadata</div>
                <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                  <div>scriptId: {activeRuntimeSnapshot?.scriptId || selectedDraft.scriptId || '-'}</div>
                  <div>runtimeActorId: {activeRuntimeSnapshot?.actorId || selectedDraft.runtimeActorId || '-'}</div>
                  <div>definitionActorId: {activeRuntimeSnapshot?.definitionActorId || selectedDraft.definitionActorId || '-'}</div>
                  <div>stateVersion: {activeRuntimeSnapshot?.stateVersion ?? '-'}</div>
                  <div>updatedAt: {formatDateTime(activeRuntimeSnapshot?.updatedAt)}</div>
                </div>
              </div>
            </div>

            <pre className="mt-4 max-h-[320px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
              {prettyPrintJson(activeRuntimeSnapshot?.readModelPayloadJson)}
            </pre>
          </details>
        </div>
      );
    }

    if (resultView === 'save') {
      if (!scopeBacked) {
        return (
          <EmptyState
            title="Scope save unavailable"
            copy="This app session does not have a resolved scope. The draft is still kept locally in your browser storage."
          />
        );
      }

      if (!(activeCatalog || selectedDraft.scopeDetail?.script)) {
        return (
          <EmptyState
            title="Not saved into the scope"
            copy="Use Save to persist this draft and make it show up in the saved scripts list."
          />
        );
      }

      return (
        <div className="space-y-4">
          <div className="flex items-center justify-between gap-3">
            <div>
              <div className="text-[14px] font-semibold text-gray-800">Catalog state</div>
              <div className="mt-1 text-[12px] text-gray-400">{activeCatalog?.scopeId || selectedDraft.scopeDetail?.scopeId || '-'}</div>
            </div>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => { void refreshCurrentCatalog(); }}
                className="panel-icon-button execution-logs-copy-action"
                title="Refresh catalog history"
                aria-label="Refresh catalog history"
              >
                <RefreshCw size={14} />
              </button>
              <div className={`rounded-full border px-3 py-1 text-[11px] uppercase tracking-[0.14em] ${
                hasScopeChanges
                  ? 'border-[#E9D6AE] bg-[#FFF7E6] text-[#9B6A1C]'
                  : 'border-[#DCE8C8] bg-[#F5FBEE] text-[#5C7A2D]'
              }`}>
                {hasScopeChanges ? 'Unsaved changes' : 'Saved'}
              </div>
            </div>
          </div>

          <div className="grid gap-4 xl:grid-cols-2">
            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Script</div>
              <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                <div>scriptId: {activeCatalog?.scriptId || selectedDraft.scopeDetail?.script?.scriptId || '-'}</div>
                <div>revision: {activeCatalog?.activeRevision || selectedDraft.scopeDetail?.script?.activeRevision || '-'}</div>
                <div>updatedAt: {formatDateTime(activeCatalog?.updatedAt || selectedDraft.scopeDetail?.script?.updatedAt)}</div>
              </div>
            </div>
            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Actors</div>
              <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                <div>catalogActorId: {activeCatalog?.catalogActorId || selectedDraft.scopeDetail?.script?.catalogActorId || '-'}</div>
                <div>definitionActorId: {activeCatalog?.activeDefinitionActorId || selectedDraft.scopeDetail?.script?.definitionActorId || '-'}</div>
                <div>sourceHash: {activeCatalog?.activeSourceHash || selectedDraft.scopeDetail?.script?.activeSourceHash || '-'}</div>
              </div>
            </div>
          </div>

          <details className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3">
            <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
              History and stored package
            </summary>
            <div className="mt-3 grid gap-4 xl:grid-cols-2">
              <div className="rounded-[18px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
                <div className="section-heading">Revision History</div>
                <div className="mt-2 text-[12px] leading-6 text-gray-600">
                  {activeCatalog?.revisionHistory?.length
                    ? activeCatalog.revisionHistory.join(' → ')
                    : activeCatalog?.activeRevision || '-'}
                </div>
                <div className="mt-3 text-[12px] leading-6 text-gray-600">
                  latestProposal: {activeCatalog?.lastProposalId || '-'}
                </div>
              </div>
              <div className="rounded-[18px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
                <div className="section-heading">Stored Package</div>
                <div className="mt-2 text-[12px] leading-6 text-gray-600">
                  files: {storedScopePackage ? getPackageEntries(storedScopePackage).length : 0} · entry: {storedScopePackage?.entrySourcePath || '-'}
                </div>
                <pre className="mt-3 max-h-[220px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                  {storedScopePackage
                    ? (getSelectedPackageEntry(storedScopePackage, storedScopePackage.entrySourcePath)?.content || '-')
                    : '-'}
                </pre>
              </div>
            </div>
          </details>
        </div>
      );
    }

    if (!activeProposal) {
      return (
        <EmptyState
          title="No promotion submitted"
          copy={activeCatalog?.lastProposalId
            ? 'The scope catalog points at a proposal id, but no terminal decision is visible yet.'
            : 'When the draft is stable, use Promote to send an evolution proposal and inspect the decision here.'}
        />
      );
    }

    return (
      <div className="space-y-4">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="text-[14px] font-semibold text-gray-800">Promotion proposal</div>
            <div className="mt-1 text-[12px] text-gray-400">{activeProposal.proposalId || '-'}</div>
          </div>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => { void refreshCurrentProposalDecision(); }}
              className="panel-icon-button execution-logs-copy-action"
              title="Refresh proposal decision"
              aria-label="Refresh proposal decision"
              disabled={proposalDecisionsPending}
            >
              <RefreshCw size={14} className={proposalDecisionsPending ? 'animate-spin' : ''} />
            </button>
            <div className={`rounded-full border px-3 py-1 text-[11px] uppercase tracking-[0.14em] ${
              activeProposal.accepted
                ? 'border-[#DCE8C8] bg-[#F5FBEE] text-[#5C7A2D]'
                : 'border-[#F2CCC4] bg-[#FFF4F1] text-[#B15647]'
            }`}>
              {activeProposal.status || (activeProposal.accepted ? 'accepted' : 'rejected')}
            </div>
          </div>
        </div>

        <div className="grid gap-4 xl:grid-cols-2">
          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
            <div className="section-heading">Revision</div>
            <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
              <div>base: {activeProposal.baseRevision || '-'}</div>
              <div>candidate: {activeProposal.candidateRevision || '-'}</div>
              <div>scriptId: {activeProposal.scriptId || '-'}</div>
            </div>
          </div>
          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
            <div className="section-heading">Decision</div>
            <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
              <div>catalogActorId: {activeProposal.catalogActorId || activeCatalog?.catalogActorId || '-'}</div>
              <div>definitionActorId: {activeProposal.definitionActorId || '-'}</div>
              <div>failureReason: {activeProposal.failureReason || '-'}</div>
            </div>
          </div>
        </div>

        <details className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3">
          <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
            Validation diagnostics
          </summary>
          {promotionDiagnostics.length > 0 ? (
            <div className="mt-3 space-y-2">
              {promotionDiagnostics.map((diagnostic, index) => (
                <div key={`${diagnostic}-${index}`} className="rounded-[16px] border border-[#EEEAE4] bg-[#FAF8F4] px-3 py-3 text-[12px] leading-6 text-gray-600">
                  {diagnostic}
                </div>
              ))}
            </div>
          ) : (
            <div className="mt-3 text-[12px] leading-6 text-gray-600">No validation diagnostics were returned.</div>
          )}
        </details>
      </div>
    );
  }

  function renderWorkspacePanelContent() {
    if (workspaceSection === 'library') {
      return (
        <div className="h-full min-h-0 overflow-hidden p-4">
          <ResourceRail
            drafts={drafts}
            filteredDrafts={filteredDrafts}
            filteredScopeScripts={filteredScopeScripts}
            runtimeSnapshots={runtimeSnapshots}
            proposalDecisions={proposalDecisions}
            scopeCatalogsByScriptId={scopeCatalogsByScriptId}
            selectedDraft={selectedDraft}
            scopeSelectionId={scopeSelectionId}
            selectedRuntimeActorId={selectedRuntimeActorId}
            selectedProposalId={selectedProposalId}
            search={search}
            scopeBacked={scopeBacked}
            scopeId={appContext.scopeId}
            scopeScriptsPending={scopeScriptsPending}
            runtimeSnapshotsPending={runtimeSnapshotsPending}
            proposalDecisionsPending={proposalDecisionsPending}
            onSearchChange={setSearch}
            onCreateDraft={handleCreateDraft}
            onSelectDraft={setSelectedDraftKey}
            onOpenScopeScript={openScopeScript}
            onRefreshScopeScripts={() => { void loadScopeScripts(); }}
            onSelectRuntime={actorId => { void handleSelectRuntime(actorId); }}
            onRefreshRuntimeSnapshots={() => { void loadRuntimeSnapshots(); }}
            onSelectProposal={handleSelectProposal}
          />
        </div>
      );
    }

    if (workspaceSection === 'details') {
      return (
        <div className="h-full min-h-0 overflow-hidden p-4">
          <InspectorPanel
            selectedDraft={selectedDraft}
            scopeBacked={scopeBacked}
            appContext={appContext}
          />
        </div>
      );
    }

    return (
      <div className="min-h-0 flex-1 overflow-y-auto p-4">
        <div className="space-y-3">
          <StudioResultCard
            active={resultView === 'runtime'}
            title="Draft Run"
            meta={activeRuntimeSnapshot ? formatDateTime(activeRuntimeSnapshot.updatedAt) : selectedDraft.lastRun ? formatDateTime(selectedDraft.updatedAtUtc) : selectedDraft.lastChatResponse != null ? formatDateTime(selectedDraft.updatedAtUtc) : 'Not run yet'}
            summary={runtimeSummary}
            status={snapshotView.status || (selectedDraft.lastRun?.accepted ? 'accepted' : selectedDraft.lastChatResponse != null ? 'completed' : '')}
            onClick={() => setResultView('runtime')}
          />
          <StudioResultCard
            active={resultView === 'save'}
            title="Catalog"
            meta={activeCatalog ? formatDateTime(activeCatalog.updatedAt) : selectedDraft.scopeDetail?.script ? formatDateTime(selectedDraft.scopeDetail.script.updatedAt) : scopeBacked ? 'Not saved yet' : 'Local only'}
            summary={saveSummary}
            status={scopeBacked ? (hasScopeChanges ? 'dirty' : activeCatalog || selectedDraft.scopeDetail?.script ? 'saved' : 'pending') : 'local'}
            onClick={() => setResultView('save')}
          />
          <StudioResultCard
            active={resultView === 'promotion'}
            title="Promotion"
            meta={activeProposal?.candidateRevision || activeCatalog?.lastProposalId || 'No candidate'}
            summary={promotionSummary}
            status={activeProposal?.status || ''}
            onClick={() => setResultView('promotion')}
          />

          <div className="rounded-[24px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
            {renderResultDetailContent()}
          </div>
        </div>
      </div>
    );
  }

  function renderPackagePanelContent() {
    return (
      <div className="min-h-0 flex-1 overflow-y-auto p-4">
        <div className="space-y-4">
          <div className="grid gap-4">
            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Entry contract</div>
              <div className="mt-3 space-y-3">
                <div>
                  <label className="field-label">Entry Behavior Type</label>
                  <input
                    className="panel-input mt-1"
                    placeholder="DraftBehavior"
                    value={selectedDraft.package.entryBehaviorTypeName}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({
                      ...draft,
                      package: updateEntryBehaviorTypeName(draft.package, event.target.value),
                    }))}
                  />
                </div>
                <div>
                  <label className="field-label">Entry Source Path</label>
                  <div className="mt-1 break-all text-[13px] leading-6 text-gray-700">
                    {selectedDraft.package.entrySourcePath || '-'}
                  </div>
                </div>
              </div>
            </div>

            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
              <div className="section-heading">Package summary</div>
              <div className="mt-3 space-y-2 text-[12px] leading-6 text-gray-600">
                <div>format: {selectedDraft.package.format}</div>
                <div>csharp files: {selectedDraft.package.csharpSources.length}</div>
                <div>proto files: {selectedDraft.package.protoFiles.length}</div>
                <div>selected file: {selectedDraft.selectedFilePath || '-'}</div>
              </div>
            </div>
          </div>

          <details className="rounded-[20px] border border-[#EEEAE4] bg-white px-4 py-4" open>
            <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
              Persisted source preview
            </summary>
            <pre className="mt-3 max-h-[420px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
              {serializePersistedSource(selectedDraft.package) || '-'}
            </pre>
          </details>
        </div>
      </div>
    );
  }

  return (
    <>
      <header className="studio-editor-header">
        <div className="studio-editor-toolbar">
          <div className="studio-title-bar">
            <div className="studio-title-group">
              <div className="min-w-0 flex-1">
                <div className="panel-eyebrow">Scripts Studio</div>
                <input
                  className="studio-title-input mt-1"
                  value={selectedDraft.scriptId}
                  onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, scriptId: event.target.value }))}
                  placeholder="script-id"
                  aria-label="Script ID"
                />
                <div className="mt-0.5 flex items-center gap-2 overflow-hidden text-[11px] text-gray-400">
                  <span className="truncate">{selectedDraft.revision || 'draft revision'}</span>
                  <span aria-hidden="true">·</span>
                  <span className="truncate">{appContext.hostMode === 'embedded' ? 'Embedded host' : 'Proxy host'}</span>
                  <span aria-hidden="true">·</span>
                  <span className="truncate">{scopeBacked ? `Scope ${appContext.scopeId || '-'}` : 'Local draft'}</span>
                </div>
              </div>

              {showValidationBadge ? (
                <div className={`rounded-full border px-2.5 py-1 text-[10px] uppercase tracking-[0.14em] ${
                  validationPending
                    ? 'border-[#E5DED3] bg-[#F7F2E8] text-[#8E6A3D]'
                    : validationResult?.errorCount
                      ? 'border-[#F2CCC4] bg-[#FFF4F1] text-[#B15647]'
                      : validationResult?.warningCount
                        ? 'border-[#E9D6AE] bg-[#FFF7E6] text-[#9B6A1C]'
                        : 'border-[#D9E5CB] bg-[#F5FBEE] text-[#5C7A2D]'
                }`}>
                  {validationSummary}
                </div>
              ) : null}
            </div>

            <div className="studio-header-actions">
              <button
                type="button"
                onClick={() => setPromotionModalOpen(true)}
                data-tooltip="Promote"
                aria-label="Promote"
                className="panel-icon-button header-toolbar-action header-export-action"
              >
                <Check size={15} />
              </button>
              <button
                type="button"
                onClick={() => { void handleSaveScript(); }}
                data-tooltip={scopeBacked ? 'Save' : 'Save local'}
                aria-label={scopeBacked ? 'Save script' : 'Save local draft'}
                disabled={savePending}
                className="panel-icon-button header-toolbar-action header-save-action"
              >
                <Save size={15} />
              </button>
              <button
                type="button"
                onClick={handleOpenBindModal}
                data-tooltip="Bind as Service"
                aria-label="Bind as Service"
                className="panel-icon-button header-toolbar-action"
              >
                <Globe size={15} />
              </button>
              <button
                type="button"
                onClick={openRunModal}
                data-tooltip="Run"
                aria-label="Run script"
                disabled={runPending}
                className="panel-icon-button header-toolbar-action header-run-action"
              >
                <Play size={15} />
              </button>
            </div>
          </div>
        </div>
      </header>

      <section className="relative flex-1 min-h-0 overflow-hidden bg-[#F2F1EE]">
        <div className="absolute inset-0 p-4 sm:p-5">
          <section className="flex h-full min-h-0 flex-col overflow-hidden rounded-[28px] border border-[#E6E3DE] bg-white shadow-[0_10px_24px_rgba(31,28,24,0.04)]">
            <div className="flex flex-wrap items-start justify-between gap-3 border-b border-[#EEEAE4] bg-[#FAF8F4] px-5 py-4">
              <div>
                <div className="panel-eyebrow">Editor</div>
                <div className="mt-1 text-[15px] font-semibold text-gray-800">
                  {selectedPackageEntry?.path || selectedDraft.selectedFilePath || 'Behavior.cs'}
                </div>
              </div>
              <div className="flex flex-wrap items-center justify-end gap-2">
                <div className="flex items-center gap-2 text-[11px] uppercase tracking-[0.14em] text-gray-400">
                  {hasScopeChanges ? (
                    <span className="rounded-full border border-[#E9D6AE] bg-[#FFF7E6] px-3 py-1 text-[#9B6A1C]">
                      Unsaved scope changes
                    </span>
                  ) : null}
                  <span>{formatDateTime(selectedDraft.updatedAtUtc)}</span>
                </div>
              </div>
            </div>

            <div className="min-h-0 flex-1 bg-[#FCFBF8]">
              <div className="flex h-full min-h-0">
                <div className={showFilesPane ? 'w-[268px] min-w-[240px] max-w-[320px]' : 'w-[56px] min-w-[56px] max-w-[56px]'}>
                  <PackageFileTree
                    entries={selectedPackageEntries}
                    selectedFilePath={selectedPackageEntry?.path || selectedDraft.selectedFilePath}
                    entrySourcePath={selectedDraft.package.entrySourcePath}
                    collapsed={!showFilesPane}
                    onToggleCollapsed={() => setFilesPaneOpen(value => !value)}
                    onSelectFile={handleSelectDraftFile}
                    onAddFile={handleAddPackageFile}
                    onRenameFile={handleRenamePackageFile}
                    onRemoveFile={handleRemovePackageFile}
                    onSetEntry={handleSetEntryFile}
                  />
                </div>
                <div className="relative min-h-0 flex-1">
                  <div className="absolute right-4 top-4 z-20 flex items-center gap-2">
                    <DrawerToggleButton
                      active={rightDrawerOpen && rightDrawerTab === 'panels'}
                      label="Panels"
                      icon={<Rows3 size={16} />}
                      onClick={() => toggleRightDrawer('panels')}
                    />
                    <DrawerToggleButton
                      active={rightDrawerOpen && rightDrawerTab === 'package'}
                      label="Package"
                      icon={<FileText size={16} />}
                      onClick={() => toggleRightDrawer('package')}
                    />
                  </div>
                  <Editor
                    path={`file:///scripts/${selectedDraft.key}/${selectedPackageEntry?.path || validationResult?.primarySourcePath || 'Behavior.cs'}`}
                    language={selectedPackageEntry?.kind === 'proto' ? 'plaintext' : 'csharp'}
                    theme="aevatar-script-light"
                    value={selectedPackageEntry?.content || ''}
                    beforeMount={handleMonacoBeforeMount}
                    onMount={handleEditorMount}
                    onChange={value => updateDraft(selectedDraft.key, draft => ({
                      ...draft,
                      package: updatePackageFileContent(
                        draft.package,
                        draft.selectedFilePath,
                        value ?? '',
                      ),
                    }))}
                    loading={(
                      <div className="flex h-full items-center justify-center text-[12px] uppercase tracking-[0.14em] text-gray-400">
                        Loading
                      </div>
                    )}
                    options={{
                      automaticLayout: true,
                      minimap: { enabled: false },
                      scrollBeyondLastLine: false,
                      smoothScrolling: true,
                      fontSize: 13,
                      lineHeight: 23,
                      fontLigatures: true,
                      tabSize: 4,
                      insertSpaces: true,
                      renderWhitespace: 'selection',
                      renderValidationDecorations: 'on',
                      lineNumbersMinChars: 3,
                      quickSuggestions: false,
                      suggestOnTriggerCharacters: false,
                      wordWrap: 'off',
                      stickyScroll: { enabled: false },
                      bracketPairColorization: { enabled: true },
                      guides: {
                        indentation: true,
                        bracketPairs: true,
                      },
                      folding: true,
                      padding: {
                        top: 18,
                        bottom: 18,
                      },
                      scrollbar: {
                        verticalScrollbarSize: 10,
                        horizontalScrollbarSize: 10,
                      },
                    }}
                  />
                </div>
              </div>
            </div>

            <div className="border-t border-[#EEEAE4] bg-[#FFFCF8] px-4 py-3">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Compiler</div>
                  <div className="mt-1 truncate text-[13px] text-gray-700">
                    {validationPending
                      ? 'Checking'
                      : visibleProblems[0]
                        ? visibleProblems[0].message
                        : 'Clean'}
                  </div>
                </div>
                {visibleProblems.length > 0 ? (
                  <button
                    type="button"
                    onClick={() => setDiagnosticsOpen(true)}
                    className={surfaceActionClass(diagnosticsOpen)}
                  >
                    Problems {visibleProblems.length}
                  </button>
                ) : (
                  <div className="rounded-full border border-[#DCE8C8] bg-[#F5FBEE] px-3 py-1.5 text-[11px] uppercase tracking-[0.14em] text-[#5C7A2D]">
                    Clean
                  </div>
                )}
              </div>
            </div>
          </section>
        </div>

        <aside className={`right-drawer ${rightDrawerOpen ? 'open' : ''}`}>
          <div className="panel-header border-b border-[#F1ECE5]">
            <div>
              <div className="panel-eyebrow">{rightDrawerTab === 'package' ? 'Package' : 'Panels'}</div>
              <div className="panel-title">
                {rightDrawerTab === 'package'
                  ? 'Manifest'
                  : workspaceSection === 'library'
                    ? 'Library'
                    : workspaceSection === 'activity'
                      ? 'Activity'
                      : 'Details'}
              </div>
            </div>
            <button type="button" onClick={closeRightDrawer} title="Close drawer." className="panel-icon-button">
              <ChevronLeft size={16} />
            </button>
          </div>

          {rightDrawerTab === 'panels' ? (
            <>
              <div className="border-b border-[#F1ECE5] px-4 py-3">
                <div className="flex flex-wrap gap-2">
                  <button
                    type="button"
                    onClick={() => setWorkspaceSection('library')}
                    className={surfaceActionClass(workspaceSection === 'library')}
                  >
                    Library
                  </button>
                  <button
                    type="button"
                    onClick={() => setWorkspaceSection('activity')}
                    className={surfaceActionClass(workspaceSection === 'activity')}
                  >
                    Activity
                  </button>
                  <button
                    type="button"
                    onClick={() => setWorkspaceSection('details')}
                    className={surfaceActionClass(workspaceSection === 'details')}
                  >
                    Details
                  </button>
                </div>
              </div>
              <div className="min-h-0 flex-1 overflow-hidden">
                {renderWorkspacePanelContent()}
              </div>
            </>
          ) : (
            renderPackagePanelContent()
          )}
        </aside>

        <div className="absolute bottom-6 right-6 z-30 flex items-end gap-3">
          {askAiOpen ? (
            <div className="ask-ai-surface flex max-h-[calc(100%-136px)] w-[420px] flex-col overflow-hidden">
              <div className="flex items-center justify-between gap-3 border-b border-[#F1ECE5] px-4 py-4">
                <div>
                  <div className="panel-eyebrow">Source</div>
                  <div className="panel-title !mt-0">Ask AI</div>
                </div>
                <button
                  type="button"
                  onClick={() => setAskAiOpen(false)}
                  title="Close Ask AI."
                  className="panel-icon-button"
                >
                  <X size={14} />
                </button>
              </div>

              <div className="min-h-0 flex-1 overflow-y-auto p-4">
                <p className="text-[12px] leading-6 text-gray-500">
                  Describe the script change you want. Closing this panel does not stop an in-flight generation.
                </p>

                <textarea
                  rows={5}
                  className="panel-textarea mt-4"
                  placeholder="Build a script that validates an email address, normalizes it, and returns a JSON summary."
                  value={askAiPrompt}
                  onChange={event => setAskAiPrompt(event.target.value)}
                />

                <div className="mt-3 flex items-center justify-between gap-2">
                  <div className="text-[11px] text-gray-400">
                    {askAiPending
                      ? 'Generating and compiling file content...'
                      : askAiGeneratedSource
                        ? `Ready to apply ${askAiGeneratedPackage ? `${askAiGeneratedPackage.csharpSources.length + askAiGeneratedPackage.protoFiles.length} files` : 'the active file'}`
                        : 'Return format: script package JSON'}
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => { void handleCopyAskAiSource(); }}
                      className="ghost-action !px-3"
                      disabled={!askAiGeneratedSource.trim()}
                    >
                      <Copy size={14} /> Copy
                    </button>
                    <button
                      type="button"
                      onClick={handleApplyAskAiSource}
                      className="ghost-action !px-3"
                      disabled={!askAiGeneratedSource.trim()}
                    >
                      <Check size={14} /> Apply
                    </button>
                    <button
                      type="button"
                      onClick={() => { void handleAskAiGenerate(); }}
                      className="ghost-action !px-3"
                      disabled={askAiPending}
                    >
                      <Bot size={14} /> {askAiPending ? 'Thinking' : 'Generate'}
                    </button>
                  </div>
                </div>

                <div className="mt-4 rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
                  <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Thinking</div>
                  <pre className="mt-2 max-h-[180px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-600">
                    {askAiReasoning || 'LLM reasoning will stream here.'}
                  </pre>
                </div>

                <div className="mt-4 rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
                  <div className="flex items-center justify-between gap-3">
                    <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Generated Preview</div>
                    <div className="text-[10px] uppercase tracking-[0.16em] text-gray-400">
                      {askAiGeneratedSource ? 'Ready to apply' : 'Waiting for generated package'}
                    </div>
                  </div>
                  {askAiGeneratedPackage ? (
                    <div className="mt-2 text-[11px] leading-5 text-gray-400">
                      {askAiPreviewEntry?.path || askAiGeneratedFilePath || '-'} · {askAiGeneratedPackage.csharpSources.length} C# · {askAiGeneratedPackage.protoFiles.length} proto
                    </div>
                  ) : null}
                  <pre className="mt-2 max-h-[240px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                    {askAiGeneratedSource || askAiAnswer || 'Generated file content will appear here.'}
                  </pre>
                </div>
              </div>
            </div>
          ) : null}

          <button
            type="button"
            onClick={() => setAskAiOpen(value => !value)}
            title="Ask AI to generate script code."
            className={`ask-ai-trigger flex h-14 w-14 items-center justify-center rounded-[20px] border transition-transform hover:-translate-y-0.5 ${
              askAiOpen || askAiPending ? 'border-[color:var(--accent-border)] text-[color:var(--accent-text)]' : 'border-[#E8E2D9]'
            }`}
          >
            <Bot size={20} />
          </button>
        </div>
      </section>

      <ScriptsStudioModal
        open={diagnosticsOpen}
        eyebrow="Compiler"
        title="Validation diagnostics"
        onClose={() => setDiagnosticsOpen(false)}
        width="min(920px, 100%)"
        actions={<button type="button" onClick={() => setDiagnosticsOpen(false)} className="ghost-action">Close</button>}
      >
        <div className="min-h-[420px]">
          {visibleProblems.length > 0 ? (
            <div className="max-h-[560px] space-y-2 overflow-auto pr-1">
              {visibleProblems.map((diagnostic, index) => (
                <button
                  key={`${diagnostic.code}-${diagnostic.filePath}-${diagnostic.startLine}-${diagnostic.startColumn}-${index}`}
                  type="button"
                  onClick={() => {
                    jumpToDiagnostic(diagnostic);
                    setDiagnosticsOpen(false);
                  }}
                  className={`w-full rounded-[18px] border px-3 py-3 text-left transition-colors ${
                    diagnostic.severity === 'error'
                      ? 'border-[#F3D3CD] bg-[#FFF5F2] hover:bg-[#FFF0EB]'
                      : diagnostic.severity === 'warning'
                        ? 'border-[#EADBB8] bg-[#FFF8EB] hover:bg-[#FFF4DE]'
                        : 'border-[#E6E0D7] bg-white hover:bg-[#FBFAF7]'
                  }`}
                >
                  <div className="flex items-center justify-between gap-3">
                    <div className="truncate text-[12px] font-semibold uppercase tracking-[0.12em] text-gray-500">
                      {diagnostic.code || diagnostic.severity}
                    </div>
                    <div className="truncate text-[11px] text-gray-400">{formatProblemLocation(diagnostic)}</div>
                  </div>
                  <div className="mt-2 text-[13px] leading-6 text-gray-700">{diagnostic.message}</div>
                </button>
              ))}
            </div>
          ) : (
            <EmptyState title="No diagnostics" copy="The current draft validated cleanly." />
          )}
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={promotionModalOpen}
        eyebrow="Governance"
        title="Promote draft"
        onClose={() => setPromotionModalOpen(false)}
        width="min(760px, 100%)"
        actions={(
          <>
            <button type="button" onClick={() => setPromotionModalOpen(false)} className="ghost-action">Cancel</button>
            <button type="button" onClick={() => { void handlePromote(); }} disabled={promotionPending} className="solid-action">
              <Check size={14} /> {promotionPending ? 'Promoting' : 'Promote'}
            </button>
          </>
        )}
      >
        <div className="space-y-4">
          <div className="grid gap-4 md:grid-cols-2">
            <div>
              <label className="field-label">Base Revision</label>
              <input
                className="panel-input mt-1"
                placeholder="base revision"
                value={selectedDraft.baseRevision}
                onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, baseRevision: event.target.value }))}
              />
            </div>
            <div>
              <label className="field-label">Candidate Revision</label>
              <input
                className="panel-input mt-1"
                placeholder="candidate revision"
                value={selectedDraft.revision}
                onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, revision: event.target.value }))}
              />
            </div>
          </div>

          <div>
            <label className="field-label">Reason</label>
            <textarea
              rows={5}
              className="panel-textarea mt-1"
              placeholder="Describe why this revision should be promoted"
              value={selectedDraft.reason}
              onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, reason: event.target.value }))}
            />
          </div>

          <div className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
            <div className="section-heading">Latest Decision</div>
            <div className="mt-2 text-[13px] leading-6 text-gray-700">
              {selectedDraft.lastPromotion
                ? `${selectedDraft.lastPromotion.status || '-'}${selectedDraft.lastPromotion.failureReason ? ` · ${selectedDraft.lastPromotion.failureReason}` : ''}`
                : 'No promotion has been submitted for this draft.'}
            </div>

            {promotionDiagnostics.length > 0 ? (
              <div className="mt-4 space-y-2">
                {promotionDiagnostics.map((diagnostic, index) => (
                  <div key={`${diagnostic}-${index}`} className="rounded-[16px] border border-[#EEEAE4] bg-white px-3 py-3 text-[12px] leading-6 text-gray-600">
                    {diagnostic}
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={runModalOpen}
        eyebrow="Runtime"
        title="Run Draft"
        onClose={() => setRunModalOpen(false)}
        actions={(
          <>
            <button type="button" onClick={() => setRunModalOpen(false)} className="ghost-action">Cancel</button>
            <button type="button" onClick={() => { void handleConfirmRunDraft(); }} disabled={runPending} className="solid-action">
              <Play size={14} /> {runPending ? 'Running' : 'Run draft'}
            </button>
          </>
        )}
      >
        <div className="space-y-4">
          <div className="rounded-[18px] border border-[#EAE4DB] bg-[#FAF8F4] px-4 py-4 text-[12px] leading-6 text-gray-600">
            This input is passed into the script through <code className="rounded bg-white px-1.5 py-0.5 text-[11px]">AppScriptCommand</code>. The execution result will appear in the Activity dialog.
          </div>
          <div>
            <label className="field-label">{selectedDraft.scriptId}</label>
            <textarea
              rows={7}
              className="panel-textarea mt-1 run-prompt-textarea"
              placeholder="Enter the draft input to execute"
              value={runInputDraft}
              onChange={event => setRunInputDraft(event.target.value)}
            />
          </div>
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={bindModalOpen}
        eyebrow="Service"
        title="Bind as Service"
        onClose={() => setBindModalOpen(false)}
        actions={(
          <>
            <button type="button" onClick={() => setBindModalOpen(false)} className="ghost-action">Cancel</button>
            <button type="button" onClick={() => { void handleConfirmBindScript(); }} disabled={bindPending || !appContext.scopeId || !selectedDraft?.scopeDetail?.script} className="solid-action">
              <Globe size={14} /> {bindPending ? 'Binding...' : 'Bind'}
            </button>
          </>
        )}
      >
        <div className="space-y-3">
          <div className="text-[12px] text-gray-500">
            Binds the current script as a scope service. After binding, invoke it from <strong>Console</strong>.
            The script must be <strong>promoted</strong> first (use the Promote button in the toolbar).
          </div>
          <div className="space-y-2">
            <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Service ID</label>
            <input
              className="w-full rounded-lg border border-[#E6E3DE] bg-[#F7F5F2] px-3 py-2 text-[12px] font-mono text-gray-700 focus:outline-none focus:ring-1 focus:ring-blue-400"
              placeholder={selectedDraft?.scriptId || 'my-script'}
              value={bindServiceId}
              onChange={e => setBindServiceId(e.target.value)}
            />
            <div className="text-[10px] text-gray-400">
              Invoke: <code className="text-gray-500">/services/{bindServiceId.trim() || selectedDraft?.scriptId || '...'}/invoke/chat:stream</code>
            </div>
          </div>
          <div className="rounded-lg border border-[#E6E3DE] bg-[#F7F5F2] px-4 py-3 text-[13px] space-y-0.5">
            <div><span className="text-gray-400">Script:</span> <strong>{selectedDraft?.scriptId || '(unsaved)'}</strong></div>
            <div><span className="text-gray-400">Name:</span> <strong>{selectedDraft?.scriptId || 'draft'}</strong></div>
            <div><span className="text-gray-400">Scope:</span> <strong>{appContext.scopeId || '(not logged in)'}</strong></div>
          </div>
          {!appContext.scopeId && (
            <div className="text-[12px] text-amber-600">Sign in with NyxID to bind services.</div>
          )}
          {appContext.scopeId && !selectedDraft?.scopeDetail?.script && (
            <div className="text-[12px] text-amber-600">This script has not been promoted yet. Use the Promote button first.</div>
          )}
        </div>
      </ScriptsStudioModal>

    </>
  );
}
