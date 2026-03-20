import Editor, { loader, type BeforeMount, type OnMount } from '@monaco-editor/react';
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Bot,
  Check,
  Code2,
  Copy,
  Play,
  Plus,
  RefreshCw,
  Save,
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
  ScriptCatalogSnapshot,
  DraftRunResult,
  ScriptPackage,
  ScriptDraft,
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
using Aevatar.Tools.Cli.Hosting;

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
    package: normalizedPackage.package,
    selectedFilePath: selectedEntry?.path || normalizedPackage.package.entrySourcePath || 'Behavior.cs',
    definitionActorId: seed.definitionActorId || '',
    runtimeActorId: seed.runtimeActorId || '',
    updatedAtUtc: seed.updatedAtUtc || now,
    lastSourceHash: seed.lastSourceHash || '',
    lastRun: seed.lastRun || null,
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
        package: normalizedPackage.package,
        selectedFilePath: selectedEntry?.path || normalizedPackage.package.entrySourcePath || 'Behavior.cs',
        definitionActorId: normalizedPackage.migrated ? '' : String(item?.definitionActorId || ''),
        runtimeActorId: normalizedPackage.migrated ? '' : String(item?.runtimeActorId || ''),
        updatedAtUtc: String(item?.updatedAtUtc || new Date().toISOString()),
        lastSourceHash: normalizedPackage.migrated ? '' : String(item?.lastSourceHash || ''),
        lastRun: normalizedPackage.migrated ? null : item?.lastRun || null,
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
  const revision = detail.script?.activeRevision || detail.source?.revision || existing?.revision || `draft-rev-${index}`;

  return createDraft(index, {
    key: existing?.key,
    scriptId,
    revision,
    baseRevision: detail.script?.activeRevision || detail.source?.revision || existing?.baseRevision || '',
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
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [workspacePanelOpen, setWorkspacePanelOpen] = useState(false);
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
  const [runModalOpen, setRunModalOpen] = useState(false);
  const [runInputDraft, setRunInputDraft] = useState('');
  const [validationPending, setValidationPending] = useState(false);
  const [validationResult, setValidationResult] = useState<ScriptValidationResult | null>(null);
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [activityOpen, setActivityOpen] = useState(false);
  const [filesPaneOpen, setFilesPaneOpen] = useState(false);
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

    if (resultView === 'runtime' && (selectedDraft.lastRun || selectedDraft.lastSnapshot)) {
      return;
    }

    if (resultView === 'save' && selectedDraft.scopeDetail) {
      return;
    }

    if (resultView === 'promotion' && selectedDraft.lastPromotion) {
      return;
    }

    if (selectedDraft.lastRun || selectedDraft.lastSnapshot) {
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
    setLibraryOpen(false);
    setActivityOpen(false);
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
    setLibraryOpen(false);
    setActivityOpen(false);
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
        setActivityOpen(true);
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
    setActivityOpen(true);

    const knownSnapshot = runtimeSnapshots.find(snapshot => snapshot.actorId === actorId);
    if (!knownSnapshot) {
      await refreshSnapshot(actorId, true);
    }
  }

  function handleSelectProposal(proposalId: string) {
    setSelectedProposalId(proposalId);
    setResultView('promotion');
    setActivityOpen(true);
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
      const detail = await api.app.saveScript({
        scriptId,
        revisionId: revision,
        expectedBaseRevision,
        package: selectedDraft.package,
      }) as ScopedScriptDetail;

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
      setResultView('save');
      setActivityOpen(true);
      setSelectedProposalId('');
      await loadScopeScripts(true);
      onFlash('Script saved to the current scope', 'success');
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
      const response = await api.app.runDraftScript({
        scriptId,
        scriptRevision: revision,
        package: normalizedPackage.package,
        input: inputText,
        definitionActorId: normalizedPackage.migrated ? '' : selectedDraft.definitionActorId,
        runtimeActorId: normalizedPackage.migrated ? '' : selectedDraft.runtimeActorId,
      }) as DraftRunResult;

      updateDraft(selectedDraft.key, draft => ({
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
      setActivityOpen(true);
      setSelectedRuntimeActorId(response.runtimeActorId || '');
      const snapshot = await waitForSnapshot(response.runtimeActorId || '');
      await loadRuntimeSnapshots(true);
      onFlash(snapshot ? 'Draft run completed' : 'Draft accepted. Runtime snapshot is catching up.', snapshot ? 'success' : 'info');
    } catch (error: any) {
      onFlash(error?.message || 'Draft run failed', 'error');
    } finally {
      setRunPending(false);
    }
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
        baseRevision: decision?.accepted ? candidateRevision : draft.baseRevision,
        lastPromotion: decision,
      }));
      setProposalDecisionsById(prev => ({
        ...prev,
        [decision.proposalId]: decision,
      }));
      setSelectedProposalId(decision.proposalId || '');

      setPromotionModalOpen(false);
      setResultView('promotion');
      setActivityOpen(true);
      if (scopeBacked) {
        await loadScopeScripts(true);
      }
      onFlash(
        decision?.accepted ? 'Promotion accepted' : (decision?.failureReason || 'Promotion rejected'),
        decision?.accepted ? 'success' : 'error',
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
    setAskAiOpen(false);
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
  const surfaceActionClass = (active = false) => `rounded-full border px-3 py-1.5 text-[11px] uppercase tracking-[0.14em] transition-colors ${
    active
      ? 'border-[color:var(--accent-border)] bg-[#FFF4F1] text-[color:var(--accent-text)]'
      : 'border-[#E5DED3] bg-white text-gray-500 hover:bg-[#F9F6F0]'
  }`;

  function renderResultDetailContent() {
    if (resultView === 'runtime') {
      if (!(selectedDraft.lastRun || activeRuntimeSnapshot)) {
        return (
          <EmptyState
            title="No runtime output yet"
            copy="Run the current draft. The materialized read model will appear here."
          />
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
                <button type="button" onClick={() => setFilesPaneOpen(value => !value)} className={surfaceActionClass(showFilesPane)}>
                  {showFilesPane ? 'Hide files' : 'Files'}
                </button>
                <button type="button" onClick={() => setWorkspacePanelOpen(true)} className={surfaceActionClass(workspacePanelOpen || libraryOpen || activityOpen || detailsOpen)}>
                  Panels
                </button>
                <button type="button" onClick={() => setEditorView('package')} className={surfaceActionClass(packageModalOpen)}>
                  Package
                </button>
                <button type="button" onClick={() => setAskAiOpen(true)} className={surfaceActionClass(askAiOpen)}>
                  Ask AI
                </button>
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
                {showFilesPane ? (
                  <div className="w-[268px] min-w-[240px] max-w-[320px]">
                    <PackageFileTree
                      entries={selectedPackageEntries}
                      selectedFilePath={selectedPackageEntry?.path || selectedDraft.selectedFilePath}
                      entrySourcePath={selectedDraft.package.entrySourcePath}
                      onSelectFile={handleSelectDraftFile}
                      onAddFile={handleAddPackageFile}
                      onRenameFile={handleRenamePackageFile}
                      onRemoveFile={handleRemovePackageFile}
                      onSetEntry={handleSetEntryFile}
                    />
                  </div>
                ) : null}
                <div className="min-h-0 flex-1">
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
      </section>

      <ScriptsStudioModal
        open={workspacePanelOpen}
        eyebrow="Workspace"
        title="Panels"
        onClose={() => setWorkspacePanelOpen(false)}
        width="min(680px, 100%)"
        actions={<button type="button" onClick={() => setWorkspacePanelOpen(false)} className="ghost-action">Close</button>}
      >
        <div className="grid gap-3 md:grid-cols-3">
          <button
            type="button"
            onClick={() => {
              setWorkspacePanelOpen(false);
              setLibraryOpen(true);
            }}
            className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] px-4 py-4 text-left transition-colors hover:bg-white"
          >
            <div className="text-[11px] uppercase tracking-[0.14em] text-gray-400">Library</div>
            <div className="mt-2 text-[14px] font-semibold text-gray-800">Drafts and saved scripts</div>
            <div className="mt-2 text-[12px] leading-6 text-gray-500">Browse local drafts, scope scripts, runtimes, and proposal decisions.</div>
          </button>

          <button
            type="button"
            onClick={() => {
              setWorkspacePanelOpen(false);
              setActivityOpen(true);
            }}
            className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] px-4 py-4 text-left transition-colors hover:bg-white"
          >
            <div className="text-[11px] uppercase tracking-[0.14em] text-gray-400">Activity</div>
            <div className="mt-2 text-[14px] font-semibold text-gray-800">Run, save, promote</div>
            <div className="mt-2 text-[12px] leading-6 text-gray-500">Inspect runtime output, catalog state, and promotion results.</div>
          </button>

          <button
            type="button"
            onClick={() => {
              setWorkspacePanelOpen(false);
              setDetailsOpen(true);
            }}
            className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] px-4 py-4 text-left transition-colors hover:bg-white"
          >
            <div className="text-[11px] uppercase tracking-[0.14em] text-gray-400">Details</div>
            <div className="mt-2 text-[14px] font-semibold text-gray-800">Metadata and contract</div>
            <div className="mt-2 text-[12px] leading-6 text-gray-500">Check actor ids, app contract, package facts, and saved scope state.</div>
          </button>
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={packageModalOpen}
        eyebrow="Package"
        title="Package manifest"
        onClose={() => setEditorView('source')}
        width="min(980px, 100%)"
        actions={<button type="button" onClick={() => setEditorView('source')} className="ghost-action">Close</button>}
      >
        <div className="space-y-4">
          <div className="grid gap-4 xl:grid-cols-2">
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

          <details className="rounded-[20px] border border-[#EEEAE4] bg-white px-4 py-4">
            <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
              Persisted source preview
            </summary>
            <pre className="mt-3 max-h-[420px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
              {serializePersistedSource(selectedDraft.package) || '-'}
            </pre>
          </details>
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={activityOpen}
        eyebrow="Activity"
        title="Draft activity"
        onClose={() => setActivityOpen(false)}
        width="min(1180px, 100%)"
        actions={<button type="button" onClick={() => setActivityOpen(false)} className="ghost-action">Close</button>}
      >
        <div className="min-h-[620px] grid gap-4 xl:grid-cols-[240px_minmax(0,1fr)]">
          <div className="space-y-3">
            <StudioResultCard
              active={resultView === 'runtime'}
              title="Draft Run"
              meta={activeRuntimeSnapshot ? formatDateTime(activeRuntimeSnapshot.updatedAt) : selectedDraft.lastRun ? formatDateTime(selectedDraft.updatedAtUtc) : 'Not run yet'}
              summary={runtimeSummary}
              status={snapshotView.status || (selectedDraft.lastRun?.accepted ? 'accepted' : '')}
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
          </div>

          <div className="min-h-0 overflow-y-auto rounded-[24px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
            {renderResultDetailContent()}
          </div>
        </div>
      </ScriptsStudioModal>

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
        open={askAiOpen}
        eyebrow="Source"
        title="Ask AI"
        onClose={() => setAskAiOpen(false)}
        width="min(1040px, 100%)"
        actions={
          <>
            <button type="button" onClick={() => setAskAiOpen(false)} className="ghost-action">Close</button>
            <button type="button" onClick={() => { void handleAskAiGenerate(); }} className="solid-action" disabled={askAiPending}>
              <Bot size={14} /> {askAiPending ? 'Thinking' : 'Generate'}
            </button>
          </>
        }
      >
        <div className="space-y-4">
          <p className="text-[12px] leading-6 text-gray-500">
            Describe the script change you want. Ask AI returns a full script package and keeps the generated file preview here until you apply it.
          </p>

          <textarea
            rows={5}
            className="panel-textarea"
            placeholder="Build a script that validates an email address, normalizes it, and returns a JSON summary."
            value={askAiPrompt}
            onChange={event => setAskAiPrompt(event.target.value)}
          />

          <div className="text-[11px] text-gray-400">
            {askAiPending
              ? 'Generating and compiling file content...'
              : askAiGeneratedSource
                ? `Ready to apply ${askAiGeneratedPackage ? `${askAiGeneratedPackage.csharpSources.length + askAiGeneratedPackage.protoFiles.length} files` : 'the active file'}`
                : 'Return format: script package JSON'}
          </div>

          <div className="grid gap-4 xl:grid-cols-2">
            <div className="rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
              <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Thinking</div>
              <pre className="mt-2 max-h-[220px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-600">
                {askAiReasoning || 'LLM reasoning will stream here.'}
              </pre>
            </div>

            <div className="rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
              <div className="flex items-center justify-between gap-3">
                <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Generated Preview</div>
                <div className="flex items-center gap-2">
                  <button
                    type="button"
                    onClick={handleApplyAskAiSource}
                    disabled={!askAiGeneratedSource.trim()}
                    title="Apply generated source to the editor."
                    aria-label="Apply generated source to the editor"
                    className="panel-icon-button"
                  >
                    <Check size={14} />
                  </button>
                  <button
                    type="button"
                    onClick={() => { void handleCopyAskAiSource(); }}
                    disabled={!askAiGeneratedSource.trim()}
                    title="Copy generated source."
                    aria-label="Copy generated source"
                    className="panel-icon-button"
                  >
                    <Copy size={14} />
                  </button>
                </div>
              </div>
              {askAiGeneratedPackage ? (
                <div className="mt-2 text-[11px] leading-5 text-gray-400">
                  {askAiPreviewEntry?.path || askAiGeneratedFilePath || '-'} · {askAiGeneratedPackage.csharpSources.length} C# · {askAiGeneratedPackage.protoFiles.length} proto
                </div>
              ) : null}
              <pre className="mt-2 max-h-[220px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                {askAiGeneratedSource || askAiAnswer || 'Generated file content will appear here.'}
              </pre>
            </div>
          </div>
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={libraryOpen}
        eyebrow="Scripts Studio"
        title="Draft library"
        onClose={() => setLibraryOpen(false)}
        width="min(980px, 100%)"
        actions={(
          <>
            <button type="button" onClick={() => setLibraryOpen(false)} className="ghost-action">Close</button>
            <button type="button" onClick={handleCreateDraft} className="solid-action">
              <Plus size={14} /> New draft
            </button>
          </>
        )}
      >
        <div className="min-h-[560px]">
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
            onSelectDraft={draftKey => {
              setSelectedDraftKey(draftKey);
              setLibraryOpen(false);
            }}
            onOpenScopeScript={detail => {
              openScopeScript(detail);
              setLibraryOpen(false);
            }}
            onRefreshScopeScripts={() => { void loadScopeScripts(); }}
            onSelectRuntime={actorId => {
              void handleSelectRuntime(actorId);
              setLibraryOpen(false);
            }}
            onRefreshRuntimeSnapshots={() => { void loadRuntimeSnapshots(); }}
            onSelectProposal={proposalId => {
              handleSelectProposal(proposalId);
              setLibraryOpen(false);
            }}
          />
        </div>
      </ScriptsStudioModal>

      <ScriptsStudioModal
        open={detailsOpen}
        eyebrow="Script"
        title="Draft details"
        onClose={() => setDetailsOpen(false)}
        width="min(880px, 100%)"
        actions={<button type="button" onClick={() => setDetailsOpen(false)} className="ghost-action">Close</button>}
      >
        <div className="min-h-[520px]">
          <InspectorPanel
            selectedDraft={selectedDraft}
            scopeBacked={scopeBacked}
            appContext={appContext}
          />
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
            This input is passed into the script through <code className="rounded bg-white px-1.5 py-0.5 text-[11px]">AppScriptCommand</code>.
            The execution result will appear in the Activity dialog.
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

    </>
  );
}
