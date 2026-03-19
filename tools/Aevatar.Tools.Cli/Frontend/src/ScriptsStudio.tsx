import Editor, { loader, type BeforeMount, type OnMount } from '@monaco-editor/react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Bot,
  Check,
  ChevronDown,
  Code2,
  FolderOpen,
  Play,
  Plus,
  RefreshCw,
  Save,
  Search,
  SlidersHorizontal,
  X,
} from 'lucide-react';
import * as monacoEditor from 'monaco-editor/esm/vs/editor/editor.api.js';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution';
import * as api from './api';

type FlashType = 'success' | 'error' | 'info';
type ScriptStorageMode = 'draft' | 'scope';
type StudioResultView = 'runtime' | 'save' | 'promotion';

type ScriptsStudioProps = {
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

type DraftRunResult = {
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

type ScriptReadModelSnapshot = {
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

type SnapshotView = {
  input: string;
  output: string;
  status: string;
  lastCommandId: string;
  notes: string[];
};

type ScriptValidationDiagnostic = {
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

type ScriptValidationResult = {
  success: boolean;
  scriptId: string;
  scriptRevision: string;
  primarySourcePath: string;
  errorCount: number;
  warningCount: number;
  diagnostics: ScriptValidationDiagnostic[];
};

type ScopedScriptSummary = {
  scopeId: string;
  scriptId: string;
  catalogActorId: string;
  definitionActorId: string;
  activeRevision: string;
  activeSourceHash: string;
  updatedAt: string;
};

type ScopedScriptSource = {
  sourceText: string;
  definitionActorId: string;
  revision: string;
  sourceHash: string;
};

type ScopedScriptDetail = {
  available: boolean;
  scopeId: string;
  script: ScopedScriptSummary | null;
  source: ScopedScriptSource | null;
};

type ScriptPromotionDecision = {
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

type ScriptDraft = {
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

const STORAGE_KEY = 'aevatar:scripts-studio:v3';

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

function formatDateTime(value: string | null | undefined) {
  if (!value) {
    return '-';
  }

  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value));
}

function createStarterSource() {
  return STARTER_SOURCE;
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

function normalizeDraftSourceForAppRuntime(source: string) {
  const text = String(source || '');
  if (!text.trim()) {
    return {
      source: STARTER_SOURCE,
      migrated: true,
    };
  }

  if (isLegacyStarterSource(text)) {
    return {
      source: STARTER_SOURCE,
      migrated: true,
    };
  }

  return {
    source: text,
    migrated: false,
  };
}

function createDraft(index: number, seed: Partial<ScriptDraft> = {}): ScriptDraft {
  const now = new Date().toISOString();
  return {
    key: seed.key || `draft-${Date.now()}-${index}`,
    scriptId: seed.scriptId || `script-${index}`,
    revision: seed.revision || `draft-rev-${index}`,
    baseRevision: seed.baseRevision || '',
    reason: seed.reason || '',
    input: seed.input || '',
    source: seed.source || createStarterSource(),
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
      const normalizedSource = normalizeDraftSourceForAppRuntime(String(item?.source || ''));
      return {
        key: String(item?.key || `draft-${Date.now()}-${index + 1}`),
        scriptId: String(item?.scriptId || `script-${index + 1}`),
        revision: String(item?.revision || `draft-rev-${index + 1}`),
        baseRevision: String(item?.baseRevision || ''),
        reason: String(item?.reason || ''),
        input: String(item?.input || ''),
        source: normalizedSource.source,
        definitionActorId: normalizedSource.migrated ? '' : String(item?.definitionActorId || ''),
        runtimeActorId: normalizedSource.migrated ? '' : String(item?.runtimeActorId || ''),
        updatedAtUtc: String(item?.updatedAtUtc || new Date().toISOString()),
        lastSourceHash: normalizedSource.migrated ? '' : String(item?.lastSourceHash || ''),
        lastRun: normalizedSource.migrated ? null : item?.lastRun || null,
        lastSnapshot: normalizedSource.migrated ? null : item?.lastSnapshot || null,
        lastPromotion: normalizedSource.migrated ? null : item?.lastPromotion || null,
        scopeDetail: normalizedSource.migrated ? null : item?.scopeDetail || null,
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

function buildEditorMarkers(validation: ScriptValidationResult | null): monacoEditor.editor.IMarkerData[] {
  if (!validation) {
    return [];
  }

  return validation.diagnostics
    .filter(diagnostic => {
      if (!diagnostic.startLine || !diagnostic.startColumn) {
        return false;
      }

      return !diagnostic.filePath || diagnostic.filePath === validation.primarySourcePath;
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
  const sourceText = detail.source?.sourceText || existing?.source || createStarterSource();
  const normalizedSource = normalizeDraftSourceForAppRuntime(sourceText);
  const scriptId = detail.script?.scriptId || existing?.scriptId || `script-${index}`;
  const revision = detail.script?.activeRevision || detail.source?.revision || existing?.revision || `draft-rev-${index}`;

  return createDraft(index, {
    key: existing?.key,
    scriptId,
    revision,
    baseRevision: detail.script?.activeRevision || detail.source?.revision || existing?.baseRevision || '',
    reason: existing?.reason || '',
    input: existing?.input || '',
    source: normalizedSource.source,
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

function isScopeDetailDirty(draft: ScriptDraft | null) {
  if (!draft?.scopeDetail?.source) {
    return false;
  }

  const savedSource = draft.scopeDetail.source.sourceText || '';
  const savedRevision = draft.scopeDetail.script?.activeRevision || draft.scopeDetail.source.revision || '';
  return savedSource !== draft.source || (savedRevision && savedRevision !== draft.revision);
}

function wait(ms: number) {
  return new Promise(resolve => window.setTimeout(resolve, ms));
}

function EmptyState(props: { title: string; copy: string; }) {
  return (
    <div className="flex h-full min-h-[180px] items-center justify-center rounded-[24px] border border-[#EEEAE4] bg-[#FAF8F4] p-6 text-center">
      <div className="max-w-[360px]">
        <div className="text-[14px] font-semibold text-gray-800">{props.title}</div>
        <div className="mt-2 text-[12px] leading-6 text-gray-500">{props.copy}</div>
      </div>
    </div>
  );
}

function StudioResultCard(props: {
  active: boolean;
  title: string;
  summary: string;
  meta: string;
  status?: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={props.onClick}
      className={`execution-run-card ${props.active ? 'active' : ''}`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-[13px] font-semibold text-gray-800">{props.title}</div>
          <div className="mt-1 text-[11px] text-gray-400">{props.meta}</div>
        </div>
        {props.status ? (
          <span className="rounded-full border border-[#E5DED3] bg-[#F7F2E8] px-2.5 py-1 text-[10px] uppercase tracking-[0.14em] text-[#8E6A3D]">
            {props.status}
          </span>
        ) : null}
      </div>
      <div className="mt-3 text-[12px] leading-6 text-gray-600">{props.summary}</div>
    </button>
  );
}

function ScriptsStudioModal(props: {
  open: boolean;
  eyebrow: string;
  title: string;
  onClose: () => void;
  children: ReactNode;
  actions?: ReactNode;
  width?: string;
}) {
  if (!props.open) {
    return null;
  }

  return (
    <div className="modal-overlay" onClick={props.onClose}>
      <div className="modal-shell" style={props.width ? { width: props.width } : undefined} onClick={event => event.stopPropagation()}>
        <div className="modal-header">
          <div>
            <div className="panel-eyebrow">{props.eyebrow}</div>
            <div className="panel-title !mt-0">{props.title}</div>
          </div>
          <button type="button" onClick={props.onClose} title="Close dialog." className="panel-icon-button">
            <X size={16} />
          </button>
        </div>
        <div className="modal-body">{props.children}</div>
        <div className="modal-footer">{props.actions}</div>
      </div>
    </div>
  );
}

export default function ScriptsStudio({ appContext, onFlash }: ScriptsStudioProps) {
  const editorRef = useRef<monacoEditor.editor.IStandaloneCodeEditor | null>(null);
  const validationRequestRef = useRef(0);
  const [drafts, setDrafts] = useState<ScriptDraft[]>(() => readStoredDrafts());
  const [selectedDraftKey, setSelectedDraftKey] = useState('');
  const [search, setSearch] = useState('');
  const [scopeScripts, setScopeScripts] = useState<ScopedScriptDetail[]>([]);
  const [scopeScriptsPending, setScopeScriptsPending] = useState(false);
  const [runPending, setRunPending] = useState(false);
  const [snapshotPending, setSnapshotPending] = useState(false);
  const [savePending, setSavePending] = useState(false);
  const [promotionPending, setPromotionPending] = useState(false);
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [promotionModalOpen, setPromotionModalOpen] = useState(false);
  const [askAiOpen, setAskAiOpen] = useState(false);
  const [askAiPrompt, setAskAiPrompt] = useState('');
  const [askAiReasoning, setAskAiReasoning] = useState('');
  const [askAiAnswer, setAskAiAnswer] = useState('');
  const [askAiPending, setAskAiPending] = useState(false);
  const [runModalOpen, setRunModalOpen] = useState(false);
  const [runInputDraft, setRunInputDraft] = useState('');
  const [validationPending, setValidationPending] = useState(false);
  const [validationResult, setValidationResult] = useState<ScriptValidationResult | null>(null);
  const [problemsOpen, setProblemsOpen] = useState(false);
  const [resultsCollapsed, setResultsCollapsed] = useState(true);
  const [resultView, setResultView] = useState<StudioResultView>('runtime');

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
  const snapshotView = parseSnapshotView(selectedDraft?.lastSnapshot || null);
  const validationSummary = summarizeValidation(validationResult, validationPending);
  const validationMarkers = useMemo(
    () => buildEditorMarkers(validationResult),
    [validationResult],
  );
  const visibleProblems = validationResult?.diagnostics || [];
  const showValidationBadge = validationPending || validationResult != null;
  const hasScopeChanges = isScopeDetailDirty(selectedDraft);

  useEffect(() => {
    setValidationResult(null);
    setProblemsOpen(false);
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
          source: selectedDraft.source,
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
          primarySourcePath: 'Behavior.cs',
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
  }, [selectedDraft?.key, selectedDraft?.scriptId, selectedDraft?.revision, selectedDraft?.source]);

  useEffect(() => {
    if (!scopeBacked) {
      setScopeScripts([]);
      return;
    }

    void loadScopeScripts(true);
  }, [scopeBacked, appContext.scopeId]);

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
  }, [selectedDraft?.key, selectedDraft?.scriptId, selectedDraft?.revision, selectedDraft?.source, selectedDraft?.baseRevision, scopeBacked]);

  useEffect(() => {
    if (!selectedDraft) {
      return;
    }

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
    if (!diagnostic.startLine || !diagnostic.startColumn) {
      return;
    }

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
    setResultsCollapsed(true);
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
    setLibraryOpen(false);
    setResultsCollapsed(true);
    onFlash('Saved script loaded into the editor', 'success');
  }

  async function refreshSnapshot(targetDraft: ScriptDraft, silent = false) {
    const actorId = targetDraft.runtimeActorId.trim();
    if (!actorId) {
      if (!silent) {
        onFlash('Run the draft first', 'info');
      }
      return null;
    }

    setSnapshotPending(true);
    try {
      const snapshot = await api.scripts.getReadModel(actorId) as ScriptReadModelSnapshot;
      updateDraft(targetDraft.key, draft => ({
        ...draft,
        lastSnapshot: snapshot,
        runtimeActorId: snapshot.actorId || draft.runtimeActorId,
        definitionActorId: snapshot.definitionActorId || draft.definitionActorId,
      }));
      setResultView('runtime');
      setResultsCollapsed(false);

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

  async function waitForSnapshot(targetDraft: ScriptDraft) {
    const actorId = targetDraft.runtimeActorId.trim();
    if (!actorId) {
      return null;
    }

    for (let attempt = 0; attempt < 6; attempt += 1) {
      const snapshot = await refreshSnapshot(
        attempt === 0
          ? targetDraft
          : {
              ...targetDraft,
              runtimeActorId: actorId,
            },
        true,
      );
      if (snapshot) {
        return snapshot;
      }

      await wait(320);
    }

    return null;
  }

  async function handleSaveScript() {
    if (!selectedDraft) {
      return;
    }

    if (!selectedDraft.source.trim()) {
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
        sourceText: selectedDraft.source,
      }) as ScopedScriptDetail;

      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        scriptId: detail.script?.scriptId || scriptId,
        revision: detail.script?.activeRevision || detail.source?.revision || revision,
        baseRevision: detail.script?.activeRevision || detail.source?.revision || revision,
        source: detail.source?.sourceText || draft.source,
        definitionActorId: detail.script?.definitionActorId || detail.source?.definitionActorId || draft.definitionActorId,
        lastSourceHash: detail.source?.sourceHash || detail.script?.activeSourceHash || draft.lastSourceHash,
        scopeDetail: detail,
      }));
      setResultView('save');
      setResultsCollapsed(false);
      await loadScopeScripts(true);
      onFlash('Script saved to the current scope', 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to save script', 'error');
    } finally {
      setSavePending(false);
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

    const normalizedSource = normalizeDraftSourceForAppRuntime(selectedDraft.source);
    if (!normalizedSource.source.trim()) {
      onFlash('Script source is required', 'error');
      return;
    }

    const scriptId = normalizeStudioId(selectedDraft.scriptId, 'script');
    const revision = normalizeStudioId(selectedDraft.revision, 'draft');

    if (normalizedSource.migrated) {
      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        source: normalizedSource.source,
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
        source: normalizedSource.source,
        input: inputText,
        definitionActorId: normalizedSource.migrated ? '' : selectedDraft.definitionActorId,
        runtimeActorId: normalizedSource.migrated ? '' : selectedDraft.runtimeActorId,
      }) as DraftRunResult;

      const nextDraft = {
        ...selectedDraft,
        input: inputText,
        scriptId: response.scriptId || scriptId,
        revision: response.scriptRevision || revision,
        definitionActorId: response.definitionActorId || selectedDraft.definitionActorId,
        runtimeActorId: response.runtimeActorId || selectedDraft.runtimeActorId,
      };

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
      setResultsCollapsed(false);
      const snapshot = await waitForSnapshot(nextDraft);
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

    if (!selectedDraft.source.trim()) {
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
        candidateSource: selectedDraft.source,
        candidateSourceHash: selectedDraft.lastSourceHash,
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

      setPromotionModalOpen(false);
      setResultView('promotion');
      setResultsCollapsed(false);
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
    setAskAiPending(true);
    setAskAiReasoning('');
    setAskAiAnswer('');

    try {
      const source = await api.assistant.authorScript({
        prompt: askAiPrompt.trim(),
        currentSource: selectedDraft.source,
        metadata: {
          script_id: selectedDraft.scriptId,
          revision: selectedDraft.revision,
        },
      }, {
        onReasoning: text => setAskAiReasoning(text),
        onText: text => setAskAiAnswer(text),
      });

      updateDraft(targetKey, draft => ({
        ...draft,
        source,
      }));
      onFlash('AI source applied to the editor', 'success');
    } catch (error: any) {
      onFlash(error?.message || 'Failed to generate script source', 'error');
    } finally {
      setAskAiPending(false);
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

  const promotionDiagnostics = Array.isArray(selectedDraft.lastPromotion?.validationReport?.diagnostics)
    ? selectedDraft.lastPromotion?.validationReport?.diagnostics || []
    : [];
  const runtimeSummary = selectedDraft.lastSnapshot
    ? `${snapshotView.status || 'updated'} · ${snapshotView.output || 'output pending'}`
    : selectedDraft.lastRun
      ? `Accepted · ${selectedDraft.lastRun.runId}`
      : 'Run the draft to materialize output.';
  const saveSummary = scopeBacked
    ? selectedDraft.scopeDetail?.script
      ? `${selectedDraft.scopeDetail.script.scriptId} · ${selectedDraft.scopeDetail.script.activeRevision}`
      : 'Save this draft into the current scope.'
    : 'Local draft only. Sign in to save it into a scope.';
  const promotionSummary = selectedDraft.lastPromotion
    ? `${selectedDraft.lastPromotion.status || 'unknown'}${selectedDraft.lastPromotion.failureReason ? ` · ${selectedDraft.lastPromotion.failureReason}` : ''}`
    : 'Submit a promotion proposal when this draft is ready.';
  const scopeSelectionId = selectedDraft.scopeDetail?.script?.scriptId || '';

  return (
    <>
      <header className="studio-editor-header">
        <div className="studio-editor-toolbar">
          <div className="studio-view-switch">
            <button
              type="button"
              onClick={() => setResultsCollapsed(true)}
              className={`studio-view-switch-button ${resultsCollapsed ? 'active' : ''}`}
            >
              Editor
            </button>
            <button
              type="button"
              onClick={() => setResultsCollapsed(false)}
              className={`studio-view-switch-button ${resultsCollapsed ? '' : 'active'}`}
            >
              Activity
            </button>
          </div>

          <div className="studio-title-bar">
            <div className="studio-title-group">
              <div className="min-w-0 flex-1">
                <input
                  className="studio-title-input"
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
            <div className="flex items-center justify-between gap-3 border-b border-[#EEEAE4] bg-[#FAF8F4] px-5 py-4">
              <div>
                <div className="panel-eyebrow">Editor</div>
                <div className="mt-1 text-[15px] font-semibold text-gray-800">Behavior.cs</div>
              </div>
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => setLibraryOpen(true)}
                  data-tooltip="Library"
                  aria-label="Open drafts and scope library"
                  className={`panel-icon-button border border-[#E8E4DD] bg-white text-gray-600 transition hover:bg-[#FFF7F3] ${
                    libraryOpen ? 'border-[color:var(--accent-border)] bg-[#FFF4F1] text-[color:var(--accent-text)]' : ''
                  }`}
                >
                  <FolderOpen size={15} />
                </button>
                <button
                  type="button"
                  onClick={() => setDetailsOpen(true)}
                  data-tooltip="Details"
                  aria-label="Open draft details"
                  className={`panel-icon-button border border-[#E8E4DD] bg-white text-gray-600 transition hover:bg-[#FFF7F3] ${
                    detailsOpen ? 'border-[color:var(--accent-border)] bg-[#FFF4F1] text-[color:var(--accent-text)]' : ''
                  }`}
                >
                  <SlidersHorizontal size={15} />
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
              <Editor
                path={`file:///scripts/${selectedDraft.key}/${validationResult?.primarySourcePath || 'Behavior.cs'}`}
                language="csharp"
                theme="aevatar-script-light"
                value={selectedDraft.source}
                beforeMount={handleMonacoBeforeMount}
                onMount={handleEditorMount}
                onChange={value => updateDraft(selectedDraft.key, draft => ({ ...draft, source: value ?? '' }))}
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
                    onClick={() => setProblemsOpen(value => !value)}
                    className="rounded-full border border-[#E5DED3] bg-white px-3 py-1.5 text-[11px] uppercase tracking-[0.14em] text-gray-500 transition-colors hover:bg-[#F9F6F0]"
                  >
                    {problemsOpen ? 'Hide Problems' : `Problems ${visibleProblems.length}`}
                  </button>
                ) : null}
              </div>

              {problemsOpen && visibleProblems.length > 0 ? (
                <div className="mt-3 max-h-[180px] space-y-2 overflow-auto pr-1">
                  {visibleProblems.map((diagnostic, index) => (
                    <button
                      key={`${diagnostic.code}-${diagnostic.filePath}-${diagnostic.startLine}-${diagnostic.startColumn}-${index}`}
                      type="button"
                      onClick={() => jumpToDiagnostic(diagnostic)}
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
              ) : null}
            </div>
          </section>
        </div>

        <div className="absolute bottom-6 right-5 z-30 flex items-end gap-3">
          <button
            type="button"
            onClick={() => setAskAiOpen(true)}
            title="Ask AI to rewrite the current draft."
            className="ask-ai-trigger flex h-14 w-14 items-center justify-center rounded-[20px] border border-[color:var(--accent-border)] text-[color:var(--accent-text)] transition-transform hover:-translate-y-0.5"
          >
            <Bot size={20} />
          </button>
        </div>
      </section>

      <section className={`execution-logs ${resultsCollapsed ? 'collapsed' : ''}`}>
        <div className="execution-logs-header">
          <div>
            <div className="text-[11px] text-gray-400 uppercase tracking-[0.16em]">Execution</div>
            <div className="text-[14px] font-semibold text-gray-800">Draft activity</div>
          </div>
          <div className="execution-logs-header-actions">
            {resultView === 'runtime' && selectedDraft.runtimeActorId ? (
              <button
                type="button"
                onClick={() => { void refreshSnapshot(selectedDraft); }}
                className="panel-icon-button execution-logs-copy-action"
                title="Refresh runtime result"
                aria-label="Refresh runtime result"
                disabled={snapshotPending}
              >
                <RefreshCw size={14} className={snapshotPending ? 'animate-spin' : ''} />
              </button>
            ) : null}
            <button
              type="button"
              onClick={() => setResultsCollapsed(value => !value)}
              className="execution-logs-collapse-action"
              aria-expanded={!resultsCollapsed}
            >
              <span className="text-[12px] text-gray-500">{resultsCollapsed ? 'Expand activity' : 'Collapse activity'}</span>
              <ChevronDown size={16} className={`execution-logs-collapse-icon ${resultsCollapsed ? 'collapsed' : ''}`} />
            </button>
          </div>
        </div>

        {!resultsCollapsed ? (
          <div className="execution-logs-body">
            <div className="execution-runs-list">
              <StudioResultCard
                active={resultView === 'runtime'}
                title="Draft Run"
                meta={selectedDraft.lastSnapshot ? formatDateTime(selectedDraft.lastSnapshot.updatedAt) : selectedDraft.lastRun ? formatDateTime(selectedDraft.updatedAtUtc) : 'Not run yet'}
                summary={runtimeSummary}
                status={snapshotView.status || (selectedDraft.lastRun?.accepted ? 'accepted' : '')}
                onClick={() => setResultView('runtime')}
              />
              <StudioResultCard
                active={resultView === 'save'}
                title="Scope Save"
                meta={selectedDraft.scopeDetail?.script ? formatDateTime(selectedDraft.scopeDetail.script.updatedAt) : scopeBacked ? 'Not saved yet' : 'Local only'}
                summary={saveSummary}
                status={scopeBacked ? (hasScopeChanges ? 'dirty' : selectedDraft.scopeDetail?.script ? 'saved' : 'pending') : 'local'}
                onClick={() => setResultView('save')}
              />
              <StudioResultCard
                active={resultView === 'promotion'}
                title="Promotion"
                meta={selectedDraft.lastPromotion?.candidateRevision || 'No candidate'}
                summary={promotionSummary}
                status={selectedDraft.lastPromotion?.status || ''}
                onClick={() => setResultView('promotion')}
              />
            </div>

            <div className="execution-log-stream">
              <div className="execution-log-list">
                <div className="execution-action-panel">
                  {resultView === 'runtime' ? (
                    selectedDraft.lastRun || selectedDraft.lastSnapshot ? (
                      <div className="space-y-4">
                        <div className="flex items-center justify-between gap-3">
                          <div>
                            <div className="text-[14px] font-semibold text-gray-800">Runtime output</div>
                            <div className="mt-1 text-[12px] text-gray-400">{selectedDraft.lastRun?.runId || selectedDraft.lastSnapshot?.actorId || '-'}</div>
                          </div>
                          <div className="rounded-full border border-[#E5DED3] bg-white px-3 py-1 text-[11px] uppercase tracking-[0.14em] text-gray-500">
                            {snapshotView.status || (selectedDraft.lastRun?.accepted ? 'accepted' : 'pending')}
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

                        <div className="grid gap-4 xl:grid-cols-2">
                          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                            <div className="section-heading">Notes</div>
                            <div className="mt-2 text-[12px] leading-6 text-gray-600">
                              {snapshotView.notes.length > 0 ? snapshotView.notes.join(', ') : '-'}
                            </div>
                          </div>
                          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                            <div className="section-heading">Metadata</div>
                            <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                              <div>runtimeActorId: {selectedDraft.runtimeActorId || '-'}</div>
                              <div>definitionActorId: {selectedDraft.definitionActorId || '-'}</div>
                              <div>stateVersion: {selectedDraft.lastSnapshot?.stateVersion ?? '-'}</div>
                              <div>updatedAt: {formatDateTime(selectedDraft.lastSnapshot?.updatedAt)}</div>
                            </div>
                          </div>
                        </div>

                        <details className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3">
                          <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
                            Raw Read Model
                          </summary>
                          <pre className="mt-3 max-h-[320px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                            {prettyPrintJson(selectedDraft.lastSnapshot?.readModelPayloadJson)}
                          </pre>
                        </details>
                      </div>
                    ) : (
                      <EmptyState
                        title="No runtime output yet"
                        copy="Run the current draft. The materialized read model will appear here."
                      />
                    )
                  ) : resultView === 'save' ? (
                    scopeBacked ? (
                      selectedDraft.scopeDetail?.script ? (
                        <div className="space-y-4">
                          <div className="flex items-center justify-between gap-3">
                            <div>
                              <div className="text-[14px] font-semibold text-gray-800">Scope save</div>
                              <div className="mt-1 text-[12px] text-gray-400">{selectedDraft.scopeDetail.scopeId}</div>
                            </div>
                            <div className={`rounded-full border px-3 py-1 text-[11px] uppercase tracking-[0.14em] ${
                              hasScopeChanges
                                ? 'border-[#E9D6AE] bg-[#FFF7E6] text-[#9B6A1C]'
                                : 'border-[#DCE8C8] bg-[#F5FBEE] text-[#5C7A2D]'
                            }`}>
                              {hasScopeChanges ? 'Unsaved changes' : 'Saved'}
                            </div>
                          </div>

                          <div className="grid gap-4 xl:grid-cols-2">
                            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                              <div className="section-heading">Script</div>
                              <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                                <div>scriptId: {selectedDraft.scopeDetail.script.scriptId}</div>
                                <div>revision: {selectedDraft.scopeDetail.script.activeRevision}</div>
                                <div>updatedAt: {formatDateTime(selectedDraft.scopeDetail.script.updatedAt)}</div>
                              </div>
                            </div>
                            <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                              <div className="section-heading">Actors</div>
                              <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                                <div>catalogActorId: {selectedDraft.scopeDetail.script.catalogActorId || '-'}</div>
                                <div>definitionActorId: {selectedDraft.scopeDetail.script.definitionActorId || '-'}</div>
                                <div>sourceHash: {selectedDraft.scopeDetail.script.activeSourceHash || '-'}</div>
                              </div>
                            </div>
                          </div>

                          <details className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3">
                            <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
                              Stored Source
                            </summary>
                            <pre className="mt-3 max-h-[320px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                              {selectedDraft.scopeDetail.source?.sourceText || '-'}
                            </pre>
                          </details>
                        </div>
                      ) : (
                        <EmptyState
                          title="Not saved into the scope"
                          copy="Use Save to persist this draft and make it show up in the saved scripts list."
                        />
                      )
                    ) : (
                      <EmptyState
                        title="Scope save unavailable"
                        copy="This app session does not have a resolved scope. The draft is still kept locally in your browser storage."
                      />
                    )
                  ) : (
                    selectedDraft.lastPromotion ? (
                      <div className="space-y-4">
                        <div className="flex items-center justify-between gap-3">
                          <div>
                            <div className="text-[14px] font-semibold text-gray-800">Promotion proposal</div>
                            <div className="mt-1 text-[12px] text-gray-400">{selectedDraft.lastPromotion.proposalId || '-'}</div>
                          </div>
                          <div className={`rounded-full border px-3 py-1 text-[11px] uppercase tracking-[0.14em] ${
                            selectedDraft.lastPromotion.accepted
                              ? 'border-[#DCE8C8] bg-[#F5FBEE] text-[#5C7A2D]'
                              : 'border-[#F2CCC4] bg-[#FFF4F1] text-[#B15647]'
                          }`}>
                            {selectedDraft.lastPromotion.status || (selectedDraft.lastPromotion.accepted ? 'accepted' : 'rejected')}
                          </div>
                        </div>

                        <div className="grid gap-4 xl:grid-cols-2">
                          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                            <div className="section-heading">Revision</div>
                            <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                              <div>base: {selectedDraft.lastPromotion.baseRevision || '-'}</div>
                              <div>candidate: {selectedDraft.lastPromotion.candidateRevision || '-'}</div>
                              <div>scriptId: {selectedDraft.lastPromotion.scriptId || '-'}</div>
                            </div>
                          </div>
                          <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                            <div className="section-heading">Decision</div>
                            <div className="mt-2 space-y-1 break-all text-[12px] leading-6 text-gray-600">
                              <div>catalogActorId: {selectedDraft.lastPromotion.catalogActorId || '-'}</div>
                              <div>definitionActorId: {selectedDraft.lastPromotion.definitionActorId || '-'}</div>
                              <div>failureReason: {selectedDraft.lastPromotion.failureReason || '-'}</div>
                            </div>
                          </div>
                        </div>

                        <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                          <div className="section-heading">Validation</div>
                          {promotionDiagnostics.length > 0 ? (
                            <div className="mt-3 space-y-2">
                              {promotionDiagnostics.map((diagnostic, index) => (
                                <div key={`${diagnostic}-${index}`} className="rounded-[16px] border border-[#EEEAE4] bg-[#FAF8F4] px-3 py-3 text-[12px] leading-6 text-gray-600">
                                  {diagnostic}
                                </div>
                              ))}
                            </div>
                          ) : (
                            <div className="mt-2 text-[12px] leading-6 text-gray-600">No validation diagnostics were returned.</div>
                          )}
                        </div>
                      </div>
                    ) : (
                      <EmptyState
                        title="No promotion submitted"
                        copy="When the draft is stable, use Promote to send an evolution proposal and inspect the decision here."
                      />
                    )
                  )}
                </div>
              </div>
            </div>
          </div>
        ) : null}
      </section>

      <ScriptsStudioModal
        open={libraryOpen}
        eyebrow="Scripts"
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
        <div className="space-y-5">
          <div className="search-field !min-h-[40px] !rounded-[18px] !border-[#E8E1D8] !bg-white">
            <Search size={14} className="text-gray-400" />
            <input
              className="search-input"
              placeholder="Search drafts or saved scripts"
              value={search}
              onChange={event => setSearch(event.target.value)}
            />
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <section className="rounded-[24px] border border-[#E6E3DE] bg-[#FAF8F4] p-4">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <div className="panel-eyebrow">Drafts</div>
                  <div className="mt-1 text-[14px] font-semibold text-gray-800">{drafts.length} local draft{drafts.length === 1 ? '' : 's'}</div>
                </div>
                <button type="button" onClick={handleCreateDraft} className="panel-icon-button" title="New draft">
                  <Plus size={14} />
                </button>
              </div>

              <div className="mt-4 max-h-[420px] space-y-2 overflow-y-auto pr-1">
                {filteredDrafts.length === 0 ? (
                  <EmptyState title="No drafts matched" copy="Try a different search, or create a new draft." />
                ) : filteredDrafts.map(draft => {
                  const dirty = isScopeDetailDirty(draft);
                  return (
                    <button
                      key={draft.key}
                      type="button"
                      onClick={() => {
                        setSelectedDraftKey(draft.key);
                        setLibraryOpen(false);
                      }}
                      className={`execution-run-card ${draft.key === selectedDraft.key ? 'active' : ''}`}
                    >
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                          <div className="truncate text-[13px] font-semibold text-gray-800">{draft.scriptId}</div>
                          <div className="mt-1 truncate text-[11px] text-gray-400">{draft.revision}</div>
                        </div>
                        <div className="flex shrink-0 flex-col items-end gap-1">
                          {draft.scopeDetail?.script ? (
                            <span className="rounded-full border border-[#DCE8C8] bg-[#F5FBEE] px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] text-[#5C7A2D]">
                              scope
                            </span>
                          ) : null}
                          {dirty ? (
                            <span className="rounded-full border border-[#E9D6AE] bg-[#FFF7E6] px-2 py-0.5 text-[10px] uppercase tracking-[0.14em] text-[#9B6A1C]">
                              dirty
                            </span>
                          ) : null}
                        </div>
                      </div>
                      <div className="mt-2 text-[11px] text-gray-400">{formatDateTime(draft.updatedAtUtc)}</div>
                    </button>
                  );
                })}
              </div>
            </section>

            <section className="rounded-[24px] border border-[#E6E3DE] bg-[#FAF8F4] p-4">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <div className="panel-eyebrow">Saved in Scope</div>
                  <div className="mt-1 text-[14px] font-semibold text-gray-800">{scopeBacked ? (appContext.scopeId || '-') : 'Unavailable'}</div>
                </div>
                {scopeBacked ? (
                  <button
                    type="button"
                    onClick={() => { void loadScopeScripts(); }}
                    className="panel-icon-button"
                    title="Refresh saved scripts"
                    disabled={scopeScriptsPending}
                  >
                    <RefreshCw size={14} className={scopeScriptsPending ? 'animate-spin' : ''} />
                  </button>
                ) : null}
              </div>

              <div className="mt-4 max-h-[420px] space-y-2 overflow-y-auto pr-1">
                {!scopeBacked ? (
                  <EmptyState
                    title="Scope save unavailable"
                    copy="This session is not bound to a resolved scope, so only local drafts are available."
                  />
                ) : filteredScopeScripts.length === 0 ? (
                  <EmptyState
                    title={scopeScriptsPending ? 'Loading scope scripts' : 'No saved scripts matched'}
                    copy={scopeScriptsPending ? 'Pulling the scope catalog now.' : 'Try a different search or save the active draft.'}
                  />
                ) : filteredScopeScripts.map(detail => {
                  const script = detail.script;
                  if (!script) {
                    return null;
                  }

                  return (
                    <button
                      key={`${detail.scopeId}:${script.scriptId}`}
                      type="button"
                      onClick={() => openScopeScript(detail)}
                      className={`execution-run-card ${scopeSelectionId === script.scriptId ? 'active' : ''}`}
                    >
                      <div className="truncate text-[13px] font-semibold text-gray-800">{script.scriptId}</div>
                      <div className="mt-1 truncate text-[11px] text-gray-400">{script.activeRevision}</div>
                      <div className="mt-2 text-[11px] text-gray-400">{formatDateTime(script.updatedAt)}</div>
                    </button>
                  );
                })}
              </div>
            </section>
          </div>
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
        <div className="grid gap-4 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,0.9fr)]">
          <div className="space-y-4">
            <section className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
              <div className="panel-eyebrow">Identity</div>
              <div className="mt-4 grid gap-3 md:grid-cols-2">
                <div className="md:col-span-2">
                  <label className="field-label">Script ID</label>
                  <input
                    className="panel-input mt-1"
                    placeholder="script id"
                    value={selectedDraft.scriptId}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, scriptId: event.target.value }))}
                  />
                </div>
                <div>
                  <label className="field-label">Draft Revision</label>
                  <input
                    className="panel-input mt-1"
                    placeholder="revision"
                    value={selectedDraft.revision}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, revision: event.target.value }))}
                  />
                </div>
                <div>
                  <label className="field-label">Base Revision</label>
                  <input
                    className="panel-input mt-1"
                    placeholder="base revision"
                    value={selectedDraft.baseRevision}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, baseRevision: event.target.value }))}
                  />
                </div>
              </div>
            </section>

            <section className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
              <div className="panel-eyebrow">Actors</div>
              <div className="mt-3 space-y-2 break-all text-[12px] leading-6 text-gray-600">
                <div>definitionActorId: {selectedDraft.definitionActorId || '-'}</div>
                <div>runtimeActorId: {selectedDraft.runtimeActorId || '-'}</div>
                <div>lastSourceHash: {selectedDraft.lastSourceHash || '-'}</div>
                <div>updatedAt: {formatDateTime(selectedDraft.updatedAtUtc)}</div>
              </div>
            </section>
          </div>

          <div className="space-y-4">
            <section className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
              <div className="panel-eyebrow">Contract</div>
              <div className="mt-3 space-y-3 text-[12px] leading-6 text-gray-600">
                <div>
                  <div className="section-heading">Storage</div>
                  <div className="mt-1 break-all text-[13px] text-gray-700">
                    {scopeBacked ? `Scope-backed · ${appContext.scopeId}` : 'Local-only draft'}
                  </div>
                </div>
                <div>
                  <div className="section-heading">Input Type</div>
                  <div className="mt-1 break-all text-[13px] text-gray-700">{appContext.scriptContract.inputType}</div>
                </div>
                <div>
                  <div className="section-heading">Read Model Fields</div>
                  <div className="mt-1 break-all text-[13px] text-gray-700">{appContext.scriptContract.readModelFields.join(', ')}</div>
                </div>
              </div>
            </section>

            <section className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-4">
              <div className="panel-eyebrow">Scope Snapshot</div>
              {selectedDraft.scopeDetail?.script ? (
                <div className="mt-3 space-y-2 break-all text-[12px] leading-6 text-gray-600">
                  <div>scriptId: {selectedDraft.scopeDetail.script.scriptId}</div>
                  <div>revision: {selectedDraft.scopeDetail.script.activeRevision}</div>
                  <div>catalogActorId: {selectedDraft.scopeDetail.script.catalogActorId || '-'}</div>
                  <div>updatedAt: {formatDateTime(selectedDraft.scopeDetail.script.updatedAt)}</div>
                </div>
              ) : (
                <div className="mt-3 text-[12px] leading-6 text-gray-500">
                  This draft has not been saved into the current scope yet.
                </div>
              )}
            </section>
          </div>
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
            The execution result will appear in the activity panel below the editor.
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
        open={askAiOpen}
        eyebrow="Source"
        title="Ask AI"
        onClose={() => setAskAiOpen(false)}
        actions={(
          <>
            <button type="button" onClick={() => setAskAiOpen(false)} className="ghost-action">Close</button>
            <button type="button" onClick={() => { void handleAskAiGenerate(); }} disabled={askAiPending} className="solid-action">
              <Bot size={14} /> {askAiPending ? 'Thinking' : 'Generate'}
            </button>
          </>
        )}
      >
        <div className="space-y-4">
          <div className="text-[12px] leading-6 text-gray-500">
            Describe the script change you want. The generated source is applied directly into the editor.
          </div>

          <textarea
            rows={5}
            className="panel-textarea"
            placeholder="Build a script that validates an email address, normalizes it, and returns a JSON summary."
            value={askAiPrompt}
            onChange={event => setAskAiPrompt(event.target.value)}
          />

          <div className="grid gap-4 xl:grid-cols-2">
            <div className="rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
              <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Thinking</div>
              <pre className="mt-2 max-h-[180px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-600">
                {askAiReasoning || '-'}
              </pre>
            </div>

            <div className="rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
              <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Generated Source</div>
              <pre className="mt-2 max-h-[180px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                {askAiAnswer || '-'}
              </pre>
            </div>
          </div>
        </div>
      </ScriptsStudioModal>
    </>
  );
}
