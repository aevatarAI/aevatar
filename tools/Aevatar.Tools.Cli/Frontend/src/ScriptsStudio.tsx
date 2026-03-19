import Editor, { loader, type BeforeMount, type OnMount } from '@monaco-editor/react';
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Bot,
  Check,
  ChevronDown,
  Code2,
  Play,
  Plus,
  RefreshCw,
  Search,
  X,
} from 'lucide-react';
import * as monacoEditor from 'monaco-editor/esm/vs/editor/editor.api.js';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker.js?worker';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution';
import * as api from './api';

type FlashType = 'success' | 'error' | 'info';

type ScriptsStudioProps = {
  appContext: {
    hostMode: 'embedded' | 'proxy';
    scriptsEnabled: boolean;
    scriptContract: {
      inputType: string;
      readModelFields: string[];
    };
  };
  onFlash: (text: string, type: FlashType) => void;
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
  lastPromotion: any | null;
};

type DraftRunResult = {
  accepted: boolean;
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

const STORAGE_KEY = 'aevatar:scripts-studio:v2';

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

function createDraft(index: number): ScriptDraft {
  const now = new Date().toISOString();
  return {
    key: `draft-${Date.now()}-${index}`,
    scriptId: `script-${index}`,
    revision: `draft-rev-${index}`,
    baseRevision: '',
    reason: '',
    input: '',
    source: createStarterSource(),
    definitionActorId: '',
    runtimeActorId: '',
    updatedAtUtc: now,
    lastSourceHash: '',
    lastRun: null,
    lastSnapshot: null,
    lastPromotion: null,
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
      endColumn: Math.max(diagnostic.endColumn || (diagnostic.startColumn || 1) + 1, (diagnostic.startColumn || 1) + 1),
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

function FoldCard(props: {
  title: string;
  eyebrow?: string;
  summary?: string;
  open: boolean;
  onToggle: () => void;
  children: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-[22px] border border-[#EEEAE4] bg-[#FAF8F4]">
      <div className="flex items-center justify-between gap-3 px-4 py-4">
        <button type="button" onClick={props.onToggle} className="flex min-w-0 flex-1 items-center gap-3 text-left">
          <div className="min-w-0 flex-1">
            {props.eyebrow ? <div className="panel-eyebrow">{props.eyebrow}</div> : null}
            <div className="mt-1 text-[14px] font-semibold text-gray-800">{props.title}</div>
            {props.summary ? <div className="mt-1 truncate text-[12px] text-gray-400">{props.summary}</div> : null}
          </div>
          <ChevronDown size={16} className={`text-gray-400 transition-transform ${props.open ? 'rotate-180' : ''}`} />
        </button>
        {props.actions ? <div className="shrink-0">{props.actions}</div> : null}
      </div>
      {props.open ? <div className="border-t border-[#EEE7DF] px-4 py-4">{props.children}</div> : null}
    </section>
  );
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

  const [draftsOpen, setDraftsOpen] = useState(true);
  const [contractOpen, setContractOpen] = useState(false);
  const [metaOpen, setMetaOpen] = useState(false);
  const [runtimeOpen, setRuntimeOpen] = useState(false);
  const [runtimeAdvancedOpen, setRuntimeAdvancedOpen] = useState(false);
  const [promotionOpen, setPromotionOpen] = useState(false);
  const [promotionAdvancedOpen, setPromotionAdvancedOpen] = useState(false);

  const [runPending, setRunPending] = useState(false);
  const [snapshotPending, setSnapshotPending] = useState(false);
  const [promotionPending, setPromotionPending] = useState(false);

  const [askAiOpen, setAskAiOpen] = useState(false);
  const [askAiPrompt, setAskAiPrompt] = useState('');
  const [askAiReasoning, setAskAiReasoning] = useState('');
  const [askAiAnswer, setAskAiAnswer] = useState('');
  const [askAiPending, setAskAiPending] = useState(false);
  const [validationPending, setValidationPending] = useState(false);
  const [validationResult, setValidationResult] = useState<ScriptValidationResult | null>(null);
  const [problemsOpen, setProblemsOpen] = useState(false);

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
      draft.scriptId.toLowerCase().includes(keyword) ||
      draft.revision.toLowerCase().includes(keyword));
  }, [drafts, search]);

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
    setDraftsOpen(true);
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
      if (!silent) {
        onFlash('Snapshot refreshed', 'success');
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

  async function handleRunDraft() {
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
      }));
    }

    setRunPending(true);
    try {
      const response = await api.app.runDraftScript({
        scriptId,
        scriptRevision: revision,
        source: normalizedSource.source,
        input: selectedDraft.input,
        definitionActorId: normalizedSource.migrated ? '' : selectedDraft.definitionActorId,
        runtimeActorId: normalizedSource.migrated ? '' : selectedDraft.runtimeActorId,
      }) as DraftRunResult;

      const nextDraft = {
        ...selectedDraft,
        scriptId: response.scriptId || scriptId,
        revision: response.scriptRevision || revision,
        definitionActorId: response.definitionActorId || selectedDraft.definitionActorId,
        runtimeActorId: response.runtimeActorId || selectedDraft.runtimeActorId,
      };

      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        scriptId: response.scriptId || scriptId,
        revision: response.scriptRevision || revision,
        definitionActorId: response.definitionActorId || draft.definitionActorId,
        runtimeActorId: response.runtimeActorId || draft.runtimeActorId,
        lastSourceHash: response.sourceHash || draft.lastSourceHash,
        lastRun: response,
      }));

      setRuntimeOpen(true);
      const snapshot = await waitForSnapshot(nextDraft);
      onFlash(snapshot ? 'Draft run completed' : 'Draft accepted. Snapshot is catching up.', snapshot ? 'success' : 'info');
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
    const rawBaseRevision = (selectedDraft.baseRevision || selectedDraft.lastPromotion?.candidateRevision || '').trim();
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
      });

      updateDraft(selectedDraft.key, draft => ({
        ...draft,
        scriptId,
        revision: candidateRevision,
        baseRevision: decision?.accepted ? candidateRevision : draft.baseRevision,
        lastPromotion: decision,
      }));

      setPromotionOpen(true);
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
      setMetaOpen(false);
      onFlash('AI source applied to editor', 'success');
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
    ? selectedDraft.lastPromotion.validationReport.diagnostics
    : [];
  const workspaceGridClass = draftsOpen
    ? 'grid-cols-1 lg:grid-cols-[240px_minmax(0,1fr)] xl:grid-cols-[240px_minmax(0,1fr)_248px]'
    : 'grid-cols-1 lg:grid-cols-[92px_minmax(0,1fr)] xl:grid-cols-[92px_minmax(0,1fr)_248px]';

  return (
    <>
      <header className="workspace-page-header h-[88px] flex-shrink-0 border-b border-[#E6E3DE] bg-white/92 px-6 backdrop-blur-sm">
        <div className="flex h-full items-center justify-between gap-4">
          <div>
            <div className="panel-eyebrow">{appContext.hostMode === 'embedded' ? 'Embedded runtime' : 'Proxy runtime'}</div>
            <div className="panel-title !mt-0">Scripts Studio</div>
          </div>

          <div className="flex flex-wrap items-center justify-end gap-2">
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
            <div className="rounded-full border border-[#E5E1DA] bg-white px-2.5 py-1 text-[10px] uppercase tracking-[0.14em] text-gray-400">
              AppScriptCommand -&gt; AppScriptReadModel
            </div>
            <button type="button" onClick={() => { void handleRunDraft(); }} disabled={runPending} className="solid-action">
              <Play size={14} /> {runPending ? 'Running' : 'Draft Run'}
            </button>
          </div>
        </div>
      </header>

      <section className="relative flex-1 min-h-0 overflow-hidden bg-[#F2F1EE]">
        <div className={`grid h-full min-h-0 ${workspaceGridClass}`}>
          <aside className="min-h-0 overflow-hidden border-r border-[#E6E3DE] bg-white/94">
            <div className="flex h-full min-h-0 flex-col p-4">
              <div className={`flex gap-3 ${draftsOpen ? 'items-start justify-between pb-4' : 'flex-col items-center pb-3'}`}>
                {draftsOpen ? (
                  <div className="min-w-0">
                    <div className="text-[13px] font-semibold text-gray-800">Drafts</div>
                    <div className="mt-1 text-[11px] text-gray-400">{drafts.length} draft{drafts.length === 1 ? '' : 's'}</div>
                  </div>
                ) : (
                  <div className="rounded-[18px] border border-[#EEEAE4] bg-[#FAF8F4] px-3 py-2 text-[11px] font-semibold uppercase tracking-[0.14em] text-gray-400">
                    {drafts.length}
                  </div>
                )}

                <div className={`flex items-center gap-2 ${draftsOpen ? '' : 'flex-col'}`}>
                  <button type="button" onClick={() => setDraftsOpen(value => !value)} className="panel-icon-button" title="Toggle drafts">
                    <ChevronDown size={14} className={`transition-transform ${draftsOpen ? 'rotate-90' : '-rotate-90'}`} />
                  </button>
                  <button type="button" onClick={handleCreateDraft} className="panel-icon-button" title="New draft">
                    <Plus size={14} />
                  </button>
                </div>
              </div>

              {draftsOpen ? (
                <>
                  <div className="search-field !min-h-[38px] !rounded-[18px] !border-[#E8E1D8] !bg-white">
                    <Search size={14} className="text-gray-400" />
                    <input
                      className="search-input"
                      placeholder="Search drafts"
                      value={search}
                      onChange={event => setSearch(event.target.value)}
                    />
                  </div>

                  <div className="mt-4 min-h-0 flex-1 space-y-2 overflow-y-auto pr-1">
                    {filteredDrafts.map(draft => (
                      <button
                        key={draft.key}
                        type="button"
                        onClick={() => setSelectedDraftKey(draft.key)}
                        className={`w-full rounded-[20px] border px-4 py-4 text-left transition-colors ${
                          draft.key === selectedDraft.key
                            ? 'border-[#C7D6FF] bg-[#EAF0FF]'
                            : 'border-[#EEEAE4] bg-[#FAF8F4] hover:bg-white'
                        }`}
                      >
                        <div className="truncate text-[14px] font-semibold text-gray-800">{draft.scriptId}</div>
                        <div className="mt-1 truncate text-[12px] text-gray-400">{draft.revision}</div>
                        <div className="mt-2 text-[11px] text-gray-400">{formatDateTime(draft.updatedAtUtc)}</div>
                      </button>
                    ))}
                  </div>
                </>
              ) : (
                <div className="min-h-0 flex-1 overflow-y-auto pt-2">
                  <div className="space-y-2">
                    {filteredDrafts.map(draft => (
                      <button
                        key={draft.key}
                        type="button"
                        title={draft.scriptId}
                        onClick={() => setSelectedDraftKey(draft.key)}
                        className={`flex h-12 w-full items-center justify-center rounded-[18px] border text-[11px] font-semibold uppercase tracking-[0.12em] transition-colors ${
                          draft.key === selectedDraft.key
                            ? 'border-[#C7D6FF] bg-[#EAF0FF] text-[#315EDE]'
                            : 'border-[#EEEAE4] bg-[#FAF8F4] text-gray-500 hover:bg-white'
                        }`}
                      >
                        {(draft.scriptId || 'S').slice(0, 2)}
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </aside>

          <div className="min-h-0 flex flex-col">
            <div className="border-b border-[#E6E3DE] bg-white/72 px-5 py-4">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate text-[17px] font-semibold text-gray-800">{selectedDraft.scriptId}</div>
                  <div className="mt-1 text-[12px] text-gray-400">{selectedDraft.revision}</div>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    onClick={() => setMetaOpen(value => !value)}
                    className="ghost-action !px-3"
                  >
                    {metaOpen ? 'Hide Meta' : 'Meta'}
                  </button>
                  <button
                    type="button"
                    onClick={() => setContractOpen(value => !value)}
                    className="ghost-action !px-3"
                  >
                    {contractOpen ? 'Hide Contract' : 'Contract'}
                  </button>
                </div>
              </div>
            </div>

            {metaOpen ? (
              <div className="border-b border-[#E6E3DE] bg-[#F7F5F0] px-5 py-4">
                <div className="grid gap-3 lg:grid-cols-2">
                  <input
                    className="panel-input"
                    placeholder="script id"
                    value={selectedDraft.scriptId}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, scriptId: event.target.value }))}
                  />
                  <input
                    className="panel-input"
                    placeholder="revision"
                    value={selectedDraft.revision}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, revision: event.target.value }))}
                  />
                  <input
                    className="panel-input"
                    placeholder="base revision"
                    value={selectedDraft.baseRevision}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, baseRevision: event.target.value }))}
                  />
                  <input
                    className="panel-input"
                    placeholder="promotion reason"
                    value={selectedDraft.reason}
                    onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, reason: event.target.value }))}
                  />
                </div>
              </div>
            ) : null}

            <div className="min-h-0 flex-1 p-5">
              <section className="flex h-full min-h-0 flex-col overflow-hidden rounded-[28px] border border-[#E6E3DE] bg-white shadow-[0_10px_24px_rgba(31,28,24,0.04)]">
                {contractOpen ? (
                  <div className="border-b border-[#EEEAE4] bg-[#FAF8F4] px-5 py-4">
                    <div className="grid gap-3 lg:grid-cols-2">
                      <div>
                        <div className="section-heading">Input Type</div>
                        <div className="mt-1 break-all text-[13px] text-gray-700">{appContext.scriptContract.inputType}</div>
                      </div>
                      <div>
                        <div className="section-heading">Read Model Fields</div>
                        <div className="mt-1 break-all text-[13px] text-gray-700">{appContext.scriptContract.readModelFields.join(', ')}</div>
                      </div>
                    </div>
                  </div>
                ) : null}

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
          </div>

          <aside className="min-h-0 overflow-hidden border-t border-[#E6E3DE] bg-white/94 lg:col-span-2 xl:col-span-1 xl:border-l xl:border-t-0">
            <div className="h-full min-h-0 overflow-y-auto p-5">
              <div className="space-y-4">
                <FoldCard
                  eyebrow="Contract"
                  title="AppScriptCommand -> AppScriptReadModel"
                  summary={appContext.scriptContract.readModelFields.join(' · ')}
                  open={contractOpen}
                  onToggle={() => setContractOpen(value => !value)}
                >
                  <div className="space-y-3 text-[12px] text-gray-600">
                    <div>
                      <div className="section-heading">Input Type</div>
                      <div className="mt-1 break-all">{appContext.scriptContract.inputType}</div>
                    </div>
                    <div>
                      <div className="section-heading">Read Model Fields</div>
                      <div className="mt-1 break-all">{appContext.scriptContract.readModelFields.join(', ')}</div>
                    </div>
                  </div>
                </FoldCard>

                <FoldCard
                  eyebrow="Runtime"
                  title="Snapshot"
                  summary={snapshotView.status ? `${snapshotView.status} -> ${snapshotView.output || '-'}` : 'input -> snapshot'}
                  open={runtimeOpen}
                  onToggle={() => setRuntimeOpen(value => !value)}
                  actions={(
                    <button
                      type="button"
                      onClick={() => { void refreshSnapshot(selectedDraft); }}
                      className="panel-icon-button"
                      disabled={snapshotPending}
                      title="Refresh snapshot"
                    >
                      <RefreshCw size={14} className={snapshotPending ? 'animate-spin' : ''} />
                    </button>
                  )}
                >
                  <div className="space-y-4">
                    <textarea
                      rows={5}
                      className="panel-textarea"
                      placeholder="draft input"
                      value={selectedDraft.input}
                      onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, input: event.target.value }))}
                    />

                    <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                      <div className="grid gap-3">
                        <div>
                          <div className="section-heading">Status</div>
                          <div className="mt-1 text-[13px] text-gray-800">{snapshotView.status || '-'}</div>
                        </div>
                        <div>
                          <div className="section-heading">Output</div>
                          <pre className="mt-1 whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">{snapshotView.output || '-'}</pre>
                        </div>
                        <div>
                          <div className="section-heading">Notes</div>
                          <div className="mt-1 text-[12px] text-gray-600">
                            {snapshotView.notes.length > 0 ? snapshotView.notes.join(', ') : '-'}
                          </div>
                        </div>
                      </div>
                    </div>

                    <details
                      open={runtimeAdvancedOpen}
                      onToggle={event => setRuntimeAdvancedOpen(event.currentTarget.open)}
                      className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3"
                    >
                      <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
                        Advanced
                      </summary>
                      <div className="mt-3 space-y-2 break-all text-[12px] text-gray-600">
                        <div>runtimeActorId: {selectedDraft.runtimeActorId || '-'}</div>
                        <div>definitionActorId: {selectedDraft.definitionActorId || '-'}</div>
                        <div>runId: {selectedDraft.lastRun?.runId || '-'}</div>
                        <div>stateVersion: {selectedDraft.lastSnapshot?.stateVersion ?? '-'}</div>
                        <div>updatedAt: {formatDateTime(selectedDraft.lastSnapshot?.updatedAt)}</div>
                      </div>
                    </details>
                  </div>
                </FoldCard>

                <FoldCard
                  eyebrow="Governance"
                  title="Promote"
                  summary={selectedDraft.lastPromotion?.candidateRevision || 'base · candidate · reason'}
                  open={promotionOpen}
                  onToggle={() => setPromotionOpen(value => !value)}
                  actions={(
                    <button type="button" onClick={() => { void handlePromote(); }} disabled={promotionPending} className="panel-icon-button" title="Promote">
                      <Check size={14} />
                    </button>
                  )}
                >
                  <div className="space-y-4">
                    <input
                      className="panel-input"
                      placeholder="base revision"
                      value={selectedDraft.baseRevision}
                      onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, baseRevision: event.target.value }))}
                    />
                    <input
                      className="panel-input"
                      placeholder="candidate revision"
                      value={selectedDraft.revision}
                      onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, revision: event.target.value }))}
                    />
                    <textarea
                      rows={4}
                      className="panel-textarea"
                      placeholder="reason"
                      value={selectedDraft.reason}
                      onChange={event => updateDraft(selectedDraft.key, draft => ({ ...draft, reason: event.target.value }))}
                    />

                    <div className="rounded-[20px] border border-[#EEEAE4] bg-white p-4">
                      <div className="section-heading">Decision</div>
                      <div className="mt-2 text-[13px] text-gray-800">
                        {selectedDraft.lastPromotion
                          ? `${selectedDraft.lastPromotion.status || '-'}${selectedDraft.lastPromotion.failureReason ? ` · ${selectedDraft.lastPromotion.failureReason}` : ''}`
                          : '-'}
                      </div>
                    </div>

                    <details
                      open={promotionAdvancedOpen}
                      onToggle={event => setPromotionAdvancedOpen(event.currentTarget.open)}
                      className="rounded-[18px] border border-[#EEEAE4] bg-white px-4 py-3"
                    >
                      <summary className="cursor-pointer text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">
                        Advanced
                      </summary>
                      <div className="mt-3 space-y-2 text-[12px] text-gray-600">
                        <div>proposalId: {selectedDraft.lastPromotion?.proposalId || '-'}</div>
                        <div>catalogActorId: {selectedDraft.lastPromotion?.catalogActorId || '-'}</div>
                        <div>definitionActorId: {selectedDraft.lastPromotion?.definitionActorId || '-'}</div>
                        <div>
                          diagnostics: {promotionDiagnostics.length > 0 ? promotionDiagnostics.join(' | ') : '-'}
                        </div>
                      </div>
                    </details>
                  </div>
                </FoldCard>
              </div>
            </div>
          </aside>
        </div>

        <div className="pointer-events-none fixed bottom-6 right-6 z-30 flex items-end gap-3">
          {askAiOpen ? (
            <div className="ask-ai-surface pointer-events-auto w-[380px] rounded-[28px] border border-[#E8E2D9] p-4 shadow-[0_26px_64px_rgba(17,24,39,0.16)]">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <div className="panel-eyebrow">Source</div>
                  <div className="panel-title">Ask AI</div>
                </div>
                <button type="button" onClick={() => setAskAiOpen(false)} className="panel-icon-button" title="Close Ask AI">
                  <X size={14} />
                </button>
              </div>

              <textarea
                rows={4}
                className="panel-textarea mt-4"
                placeholder="Describe the change"
                value={askAiPrompt}
                onChange={event => setAskAiPrompt(event.target.value)}
              />

              <div className="mt-3 flex items-center justify-end gap-2">
                <button type="button" onClick={() => { void handleAskAiGenerate(); }} disabled={askAiPending} className="ghost-action !px-3">
                  <Bot size={14} /> {askAiPending ? 'Thinking' : 'Generate'}
                </button>
              </div>

              <div className="mt-4 rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
                <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Thinking</div>
                <pre className="mt-2 max-h-[120px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-600">
                  {askAiReasoning || '-'}
                </pre>
              </div>

              <div className="mt-4 rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
                <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">Source</div>
                <pre className="mt-2 max-h-[220px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                  {askAiAnswer || '-'}
                </pre>
              </div>
            </div>
          ) : null}

          <button
            type="button"
            onClick={event => {
              event.stopPropagation();
              setAskAiOpen(value => !value);
            }}
            className="ask-ai-trigger pointer-events-auto flex h-14 w-14 items-center justify-center rounded-[20px] border border-[color:var(--accent-border)] shadow-[0_24px_56px_rgba(17,24,39,0.18)] transition-transform hover:-translate-y-0.5"
            title="Ask AI to generate script source"
          >
            <Bot size={20} />
          </button>
        </div>
      </section>
    </>
  );
}
