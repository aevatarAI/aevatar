import {
  AppstoreOutlined,
  CheckCircleOutlined,
  CloseOutlined,
  DownOutlined,
  ExperimentOutlined,
  FileSearchOutlined,
  FolderOpenOutlined,
  PlayCircleOutlined,
  RobotOutlined,
  SaveOutlined,
  SafetyCertificateOutlined,
  SyncOutlined,
  WarningOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Dropdown,
  Input,
  Space,
  Tooltip,
} from 'antd';
import type { MenuProps } from 'antd';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { buildTeamWorkspaceRoute } from '@/shared/navigation/scopeRoutes';
import type { StudioAppContext } from '@/shared/studio/models';
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
} from '@/shared/studio/scriptPackage';
import {
  formatStudioHostModeLabel,
  getStudioHostModeTooltip,
} from '@/shared/studio/scriptHostCapabilities';
import { formatScriptDateTime, isScopeDetailDirty } from '@/shared/studio/scriptUtils';
import { studioApi } from '@/shared/studio/api';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import type {
  ScriptCatalogSnapshot,
  ScriptDraft,
  ScriptPackage,
  ScriptPromotionDecision,
  ScriptReadModelSnapshot,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
  ScopedScriptDetail,
} from '@/shared/studio/scriptsModels';
import {
  ScriptsStudioEmptyState,
  ScriptsStudioModal,
} from './ScriptsStudioChrome';
import ScriptInspectorPanel from './components/ScriptInspectorPanel';
import ScriptsPackageFileTree from './components/ScriptsPackageFileTree';
import ScriptsPackagePanel from './components/ScriptsPackagePanel';
import ScriptResultsPanel from './components/ScriptResultsPanel';
import ScriptsResourceRail from './components/ScriptsResourceRail';
import {
  clampFloatingOffset,
  DEFAULT_FLOATING_OFFSET,
  readFloatingOffsetFromStorage,
  type FloatingBounds,
  type FloatingOffset,
} from './floatingLayout';
import './scriptsStudio.css';

const STORAGE_KEY = 'aevatar:console:scripts-studio:v1';
const FLOATING_STORAGE_KEY = 'aevatar:console:scripts-studio:floating:v1';

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
                apply: static (_, evt, _) => evt.Current?.Clone() ?? new AppScriptReadModel())
            .ProjectState(static (state, _) => state?.Clone() ?? new AppScriptReadModel());
    }

    private static Task HandleAsync(
        AppScriptCommand input,
        ScriptCommandContext<AppScriptReadModel> context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var commandId = context.CommandId ?? input?.CommandId ?? string.Empty;
        var rawInput = input?.Input ?? string.Empty;
        var normalized = rawInput.Trim();
        var current = new AppScriptReadModel
        {
            Input = rawInput,
            Output = normalized.ToUpperInvariant(),
            Status = normalized.Length == 0 ? "empty" : "ok",
            LastCommandId = commandId,
        };

        current.Notes.Add(normalized.Length == 0 ? "no-input" : "trimmed");
        current.Notes.Add("uppercased");

        context.Emit(new AppScriptUpdated
        {
            CommandId = commandId,
            Current = current,
        });
        return Task.CompletedTask;
    }
}
`;

type NoticeState = {
  type: 'success' | 'info' | 'warning' | 'error';
  message: string;
  description?: string;
  actions?: Array<{
    label: string;
    href: string;
  }>;
};

type FloatingDragState = {
  bounds: FloatingBounds;
  moved: boolean;
  pointerId: number;
  startClientX: number;
  startClientY: number;
  startOffset: FloatingOffset;
};

type SnapshotView = {
  input: string;
  output: string;
  status: string;
  lastCommandId: string;
  notes: string[];
};

type ScriptEditorMarker = {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  severity: 'error' | 'warning' | 'info';
  message: string;
  code?: string;
  source?: string;
};

type ScriptEditorFocusTarget = {
  filePath: string;
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  token: string;
};

type ScriptCodeEditorProps = {
  value: string;
  filePath: string;
  language: 'csharp' | 'plaintext';
  focusTarget?: ScriptEditorFocusTarget | null;
  markers: ScriptEditorMarker[];
  onChange: (value: string) => void;
};

type WorkspaceSection = 'library' | 'activity' | 'details';
type EditorView = 'source' | 'package';

type FileDialogState = {
  confirmLabel: string;
  kind: 'csharp' | 'proto';
  mode: 'add' | 'rename';
  originalPath: string;
  title: string;
  value: string;
};

type RemovalTargetState =
  | {
      filePath: string;
      kind: 'file';
    }
  | {
      draftKey: string;
      kind: 'draft';
      scriptId: string;
    }
  | null;

const ScriptCodeEditorComponent: React.ComponentType<ScriptCodeEditorProps> =
  process.env.NODE_ENV === 'test'
    ? ({ value, onChange }) => (
        <Input.TextArea
          autoSize={{ minRows: 18, maxRows: 24 }}
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
      )
    : (
        require('./ScriptCodeEditor') as {
          default: React.ComponentType<ScriptCodeEditorProps>;
        }
      ).default;

type ScriptsWorkbenchPageProps = {
  appContext: StudioAppContext;
  initialScriptId?: string;
  onUnsavedChangesChange?: (hasUnsavedChanges: boolean) => void;
  onSelectScriptId?: (scriptId: string) => void;
};

let draftCounter = 0;

function createDraftKey(prefix: string): string {
  const uuid = globalThis.crypto?.randomUUID?.();
  if (uuid) {
    return `${prefix}_${uuid}`;
  }

  draftCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${draftCounter.toString(36)}`;
}

function createRevisionSeed(): string {
  return `draft-${new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14)}`;
}

function buildScopePageHref(
  path: string,
  scopeId: string,
  query: Record<string, string | undefined> = {},
): string {
  const params = new URLSearchParams();
  params.set('scopeId', scopeId);

  for (const [key, value] of Object.entries(query)) {
    const normalized = String(value || '').trim();
    if (!normalized) {
      continue;
    }

    params.set(key, normalized);
  }

  return `${path}?${params.toString()}`;
}

function normalizeStudioId(value: string, fallbackPrefix: string): string {
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

  return `${fallbackPrefix}-${new Date()
    .toISOString()
    .replace(/[-:.TZ]/g, '')
    .slice(0, 14)}`;
}

function buildScopeScriptBindingRevisionId(
  scriptId: string,
  scriptRevision: string,
): string {
  return normalizeStudioId(`${scriptId}-${scriptRevision}`, 'rev');
}

function createStarterPackage(): ScriptPackage {
  return createSingleSourcePackage(STARTER_SOURCE);
}

function isLegacyStarterSource(source: string): boolean {
  const normalized = String(source || '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
  const matchesBrokenAppStarter =
    normalized.includes('public sealed class draftbehavior') &&
    normalized.includes(
      'scriptbehavior<appscriptreadmodel, appscriptreadmodel>',
    ) &&
    normalized.includes('oncommand<appscriptcommand>(handleasync)') &&
    normalized.includes('context.emit(new appscriptupdated') &&
    (normalized.includes('using aevatar.tools.cli.hosting;') ||
      normalized.includes('using aevatar.studio.hosting.endpoints;'));

  return (
    matchesBrokenAppStarter ||
    normalized.includes('oncommand<stringvalue>') ||
    normalized.includes('scriptbehavior<struct, struct>') ||
    (normalized.includes('google.protobuf.wellknowntypes') &&
      normalized.includes('stringvalue') &&
      normalized.includes('struct'))
  );
}

function normalizeDraftPackageForAppRuntime(
  rawPackage?: ScriptPackage | null,
  rawSource?: string,
): { package: ScriptPackage; migrated: boolean } {
  const packageModel = rawPackage
    ? createScriptPackage(
        rawPackage.csharpSources,
        rawPackage.protoFiles,
        rawPackage.entryBehaviorTypeName,
        rawPackage.entrySourcePath,
      )
    : deserializePersistedSource(String(rawSource || ''));
  const activeEntry = getSelectedPackageEntry(
    packageModel,
    packageModel.entrySourcePath,
  );
  const primarySource = activeEntry?.content || '';

  if (!primarySource.trim() || isLegacyStarterSource(primarySource)) {
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
    key: seed.key || createDraftKey('script_draft'),
    scriptId: seed.scriptId || `script-${index}`,
    revision: seed.revision || createRevisionSeed(),
    baseRevision: seed.baseRevision || '',
    reason: seed.reason || '',
    input: seed.input || '',
    package: normalizedPackage.package,
    selectedFilePath:
      selectedEntry?.path ||
      normalizedPackage.package.entrySourcePath ||
      'Behavior.cs',
    definitionActorId: normalizedPackage.migrated
      ? ''
      : seed.definitionActorId || '',
    runtimeActorId: normalizedPackage.migrated ? '' : seed.runtimeActorId || '',
    updatedAtUtc: seed.updatedAtUtc || now,
    lastSourceHash: normalizedPackage.migrated ? '' : seed.lastSourceHash || '',
    lastRun: normalizedPackage.migrated ? null : seed.lastRun || null,
    lastSnapshot: normalizedPackage.migrated ? null : seed.lastSnapshot || null,
    lastPromotion:
      normalizedPackage.migrated ? null : seed.lastPromotion || null,
    scopeDetail: normalizedPackage.migrated ? null : seed.scopeDetail || null,
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

    const parsed = JSON.parse(raw) as Array<
      Partial<ScriptDraft> & {
        source?: string;
      }
    >;
    if (!Array.isArray(parsed) || parsed.length === 0) {
      return [createDraft(1)];
    }

    return parsed.map((item, index) => {
      const normalizedPackage = normalizeDraftPackageForAppRuntime(
        item.package || null,
        String(item.source || ''),
      );
      const selectedEntry = getSelectedPackageEntry(
        normalizedPackage.package,
        String(
          item.selectedFilePath || normalizedPackage.package.entrySourcePath || '',
        ),
      );

      return {
        key: String(item.key || createDraftKey(`script_draft_${index + 1}`)),
        scriptId: String(item.scriptId || `script-${index + 1}`),
        revision: String(item.revision || createRevisionSeed()),
        baseRevision: String(item.baseRevision || ''),
        reason: String(item.reason || ''),
        input: String(item.input || ''),
        package: normalizedPackage.package,
        selectedFilePath:
          selectedEntry?.path ||
          normalizedPackage.package.entrySourcePath ||
          'Behavior.cs',
        definitionActorId: normalizedPackage.migrated
          ? ''
          : String(item.definitionActorId || ''),
        runtimeActorId: normalizedPackage.migrated
          ? ''
          : String(item.runtimeActorId || ''),
        updatedAtUtc: String(item.updatedAtUtc || new Date().toISOString()),
        lastSourceHash: normalizedPackage.migrated
          ? ''
          : String(item.lastSourceHash || ''),
        lastRun: normalizedPackage.migrated ? null : item.lastRun || null,
        lastSnapshot: normalizedPackage.migrated
          ? null
          : item.lastSnapshot || null,
        lastPromotion: normalizedPackage.migrated
          ? null
          : item.lastPromotion || null,
        scopeDetail: normalizedPackage.migrated ? null : item.scopeDetail || null,
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
    const payload = JSON.parse(snapshot.readModelPayloadJson) as Record<
      string,
      unknown
    >;
    return {
      input: typeof payload.input === 'string' ? payload.input : '',
      output: typeof payload.output === 'string' ? payload.output : '',
      status: typeof payload.status === 'string' ? payload.status : '',
      lastCommandId:
        typeof payload.last_command_id === 'string'
          ? payload.last_command_id
          : '',
      notes: Array.isArray(payload.notes)
        ? payload.notes.filter(
            (item): item is string => typeof item === 'string',
          )
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

function isSameReadModelSnapshot(
  left: ScriptReadModelSnapshot | null | undefined,
  right: ScriptReadModelSnapshot | null | undefined,
): boolean {
  if (!left && !right) {
    return true;
  }

  if (!left || !right) {
    return false;
  }

  return (
    left.actorId === right.actorId &&
    left.scriptId === right.scriptId &&
    left.definitionActorId === right.definitionActorId &&
    left.revision === right.revision &&
    left.readModelTypeUrl === right.readModelTypeUrl &&
    left.readModelPayloadJson === right.readModelPayloadJson &&
    left.stateVersion === right.stateVersion &&
    left.lastEventId === right.lastEventId &&
    left.updatedAt === right.updatedAt
  );
}

function hasValidationError(findings: ScriptValidationDiagnostic[]): boolean {
  return findings.some((item) => item.severity === 'error');
}

function buildEditorMarkers(
  validation: ScriptValidationResult | null,
  activeFilePath: string,
): ScriptEditorMarker[] {
  if (!validation) {
    return [];
  }

  return validation.diagnostics
    .filter((diagnostic) => {
      if (!diagnostic.startLine || !diagnostic.startColumn) {
        return false;
      }

      return !diagnostic.filePath || diagnostic.filePath === activeFilePath;
    })
    .map((diagnostic) => ({
      startLineNumber: diagnostic.startLine || 1,
      startColumn: diagnostic.startColumn || 1,
      endLineNumber: Math.max(
        diagnostic.endLine || diagnostic.startLine || 1,
        diagnostic.startLine || 1,
      ),
      endColumn: Math.max(
        diagnostic.endColumn || (diagnostic.startColumn || 1) + 1,
        (diagnostic.startColumn || 1) + 1,
      ),
      severity: diagnostic.severity,
      message: diagnostic.code
        ? `[${diagnostic.code}] ${diagnostic.message}`
        : diagnostic.message,
      code: diagnostic.code || undefined,
      source: diagnostic.origin || undefined,
    }));
}

function summarizeValidation(
  validation: ScriptValidationResult | null,
  pending: boolean,
): string {
  if (pending || !validation) {
    return '校验中';
  }

  if (validation.errorCount > 0) {
    return `${validation.errorCount} 个错误${
      validation.warningCount > 0
        ? ` · ${validation.warningCount} 个警告`
        : ''
    }`;
  }

  if (validation.warningCount > 0) {
    return `${validation.warningCount} 个警告`;
  }

  return '通过';
}

function compactHeaderValue(value: string, leading = 8, trailing = 4): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '-';
  }

  if (trimmed.length <= leading + trailing + 1) {
    return trimmed;
  }

  return `${trimmed.slice(0, leading)}…${trimmed.slice(-trailing)}`;
}

function hydrateDraftFromScopeDetail(
  detail: ScopedScriptDetail,
  index: number,
  existing?: ScriptDraft,
): ScriptDraft {
  const normalizedPackage = normalizeDraftPackageForAppRuntime(
    existing?.package || null,
    detail.source?.sourceText || '',
  );
  const selectedEntry = getSelectedPackageEntry(
    normalizedPackage.package,
    existing?.selectedFilePath || normalizedPackage.package.entrySourcePath,
  );
  const scriptId =
    detail.script?.scriptId || existing?.scriptId || `script-${index}`;
  const revision =
    detail.script?.activeRevision ||
    detail.source?.revision ||
    existing?.revision ||
    createRevisionSeed();

  return createDraft(index, {
    key: existing?.key,
    scriptId,
    revision,
    baseRevision:
      detail.script?.activeRevision ||
      detail.source?.revision ||
      existing?.baseRevision ||
      '',
    reason: existing?.reason || '',
    input: existing?.input || '',
    package: normalizedPackage.package,
    selectedFilePath:
      selectedEntry?.path ||
      normalizedPackage.package.entrySourcePath ||
      'Behavior.cs',
    definitionActorId:
      detail.script?.definitionActorId ||
      detail.source?.definitionActorId ||
      existing?.definitionActorId ||
      '',
    runtimeActorId: existing?.runtimeActorId || '',
    updatedAtUtc: detail.script?.updatedAt || existing?.updatedAtUtc,
    lastSourceHash:
      detail.source?.sourceHash ||
      detail.script?.activeSourceHash ||
      existing?.lastSourceHash ||
      '',
    lastRun: existing?.lastRun || null,
    lastSnapshot: existing?.lastSnapshot || null,
    lastPromotion: existing?.lastPromotion || null,
    scopeDetail: detail,
  });
}

function compactScriptIdList(details: ScopedScriptDetail[] | undefined): string[] {
  return (details ?? [])
    .map((detail) => detail.script?.scriptId || '')
    .filter(Boolean);
}

function normalizePackageFilePath(value: string): string {
  return String(value || '')
    .trim()
    .replace(/\\/g, '/')
    .replace(/\/+/g, '/')
    .replace(/^\.\//, '');
}

function validatePackageFilePath(
  packageModel: ScriptPackage | null,
  kind: 'csharp' | 'proto',
  nextFilePath: string,
  originalPath = '',
): string {
  const normalizedPath = normalizePackageFilePath(nextFilePath);
  if (!normalizedPath) {
    return 'File path is required.';
  }

  const expectedExtension = kind === 'csharp' ? '.cs' : '.proto';
  if (!normalizedPath.toLowerCase().endsWith(expectedExtension)) {
    return kind === 'csharp'
      ? 'C# files must end with .cs.'
      : 'Proto files must end with .proto.';
  }

  const duplicate = getPackageEntries(packageModel || createStarterPackage()).some(
    (entry) => entry.path === normalizedPath && entry.path !== originalPath,
  );
  if (duplicate) {
    return 'A file with this path already exists.';
  }

  return '';
}

const ScriptsWorkbenchPage: React.FC<ScriptsWorkbenchPageProps> = ({
  appContext,
  initialScriptId = '',
  onUnsavedChangesChange,
  onSelectScriptId,
}) => {
  const queryClient = useQueryClient();
  const initialScriptIdRef = React.useRef(initialScriptId.trim());
  const [drafts, setDrafts] = React.useState<ScriptDraft[]>(() => readStoredDrafts());
  const [selectedDraftKey, setSelectedDraftKey] = React.useState('');
  const [search, setSearch] = React.useState('');
  const [validationPending, setValidationPending] = React.useState(false);
  const [validationResult, setValidationResult] =
    React.useState<ScriptValidationResult | null>(null);
  const [validationError, setValidationError] = React.useState<string>('');
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const [runInputDraft, setRunInputDraft] = React.useState('');
  const [promotionModalOpen, setPromotionModalOpen] = React.useState(false);
  const [promotionReasonDraft, setPromotionReasonDraft] = React.useState('');
  const [bindModalOpen, setBindModalOpen] = React.useState(false);
  const [bindDisplayNameDraft, setBindDisplayNameDraft] = React.useState('');
  const [askAiOpen, setAskAiOpen] = React.useState(false);
  const [askAiPrompt, setAskAiPrompt] = React.useState('');
  const [askAiReasoning, setAskAiReasoning] = React.useState('');
  const [askAiAnswer, setAskAiAnswer] = React.useState('');
  const [askAiGeneratedSource, setAskAiGeneratedSource] = React.useState('');
  const [askAiGeneratedPackage, setAskAiGeneratedPackage] =
    React.useState<ScriptPackage | null>(null);
  const [askAiGeneratedFilePath, setAskAiGeneratedFilePath] = React.useState('');
  const [fileDialog, setFileDialog] = React.useState<FileDialogState | null>(null);
  const [removalTarget, setRemovalTarget] =
    React.useState<RemovalTargetState>(null);
  const [selectedDiagnosticKey, setSelectedDiagnosticKey] = React.useState('');
  const [editorFocusTarget, setEditorFocusTarget] =
    React.useState<ScriptEditorFocusTarget | null>(null);
  const [selectedRuntimeActorId, setSelectedRuntimeActorId] = React.useState('');
  const [selectedProposalId, setSelectedProposalId] = React.useState('');
  const [activeResultTab, setActiveResultTab] = React.useState('diagnostics');
  const [notice, setNotice] = React.useState<NoticeState | null>(null);
  const [savePending, setSavePending] = React.useState(false);
  const [runPending, setRunPending] = React.useState(false);
  const [promotionPending, setPromotionPending] = React.useState(false);
  const [bindPending, setBindPending] = React.useState(false);
  const [askAiPending, setAskAiPending] = React.useState(false);
  const [workspaceSection, setWorkspaceSection] =
    React.useState<WorkspaceSection>('details');
  const [workspacePanelOpen, setWorkspacePanelOpen] = React.useState(false);
  const [filesPaneOpen, setFilesPaneOpen] = React.useState(true);
  const [editorView, setEditorView] = React.useState<EditorView>('source');
  const [floatingOffset, setFloatingOffset] = React.useState<FloatingOffset>(() => {
    if (typeof window === 'undefined') {
      return DEFAULT_FLOATING_OFFSET;
    }

    return readFloatingOffsetFromStorage(
      window.localStorage.getItem(FLOATING_STORAGE_KEY),
    );
  });
  const [floatingDragging, setFloatingDragging] = React.useState(false);
  const floatingViewportRef = React.useRef<HTMLElement | null>(null);
  const floatingRef = React.useRef<HTMLDivElement | null>(null);
  const floatingOffsetRef = React.useRef<FloatingOffset>({ x: 0, y: 0 });
  const floatingDragStateRef = React.useRef<FloatingDragState | null>(null);
  const suppressAskAiToggleRef = React.useRef(false);
  const askAiAbortRef = React.useRef<AbortController | null>(null);
  const saveShortcutActionRef = React.useRef<(() => void) | null>(null);

  const scopeBacked =
    appContext.scopeResolved && appContext.scriptStorageMode === 'scope';
  const isEmbeddedMode = appContext.mode === 'embedded';
  const resolvedScopeId = appContext.scopeId?.trim() || '';
  const askAiUnavailableMessage = 'AI 辅助需要在嵌入式 Studio Host 中使用。';
  const headerHostLabel = formatStudioHostModeLabel(appContext.mode);
  const headerHostTooltip = getStudioHostModeTooltip(appContext.mode);

  React.useEffect(() => {
    if (!selectedDraftKey && drafts[0]?.key) {
      setSelectedDraftKey(drafts[0].key);
      return;
    }

    if (selectedDraftKey && !drafts.some((draft) => draft.key === selectedDraftKey)) {
      setSelectedDraftKey(drafts[0]?.key || '');
    }
  }, [drafts, selectedDraftKey]);

  React.useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(drafts));
    } catch {
      // Ignore storage failures in restricted browser contexts.
    }
  }, [drafts]);

  React.useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(
        FLOATING_STORAGE_KEY,
        JSON.stringify(floatingOffset),
      );
    } catch {
      // Ignore storage failures in restricted browser contexts.
    }
  }, [floatingOffset]);

  React.useEffect(() => {
    floatingOffsetRef.current = floatingOffset;
  }, [floatingOffset]);

  const selectedDraft = React.useMemo(
    () => drafts.find((draft) => draft.key === selectedDraftKey) || drafts[0] || null,
    [drafts, selectedDraftKey],
  );
  const selectedPackageEntry = React.useMemo(
    () =>
      selectedDraft
        ? getSelectedPackageEntry(
            selectedDraft.package,
            selectedDraft.selectedFilePath,
          )
        : null,
    [selectedDraft],
  );

  const filteredDrafts = React.useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return drafts;
    }

    return drafts.filter((draft) =>
      [draft.scriptId, draft.revision, draft.baseRevision]
        .join(' ')
        .toLowerCase()
        .includes(keyword),
    );
  }, [drafts, search]);

  const scopeScriptsQuery = useQuery({
    queryKey: ['studio-scripts-scope', appContext.scopeId],
    enabled: scopeBacked,
    queryFn: () => scriptsApi.listScripts(resolvedScopeId, true),
  });

  const runtimeSnapshotsQuery = useQuery({
    queryKey: ['studio-scripts-runtimes'],
    queryFn: () => scriptsApi.listRuntimes(24),
  });

  const scopeScriptIds = React.useMemo(
    () => compactScriptIdList(scopeScriptsQuery.data),
    [scopeScriptsQuery.data],
  );

  const scopeCatalogsQuery = useQuery({
    queryKey: [
      'studio-scripts-catalogs',
      appContext.scopeId,
      scopeScriptIds.join('|'),
    ],
    enabled: scopeBacked && scopeScriptIds.length > 0,
    queryFn: async () => {
      const entries = await Promise.all(
        scopeScriptIds.map(async (scriptId) => {
          try {
            const catalog = await scriptsApi.getScriptCatalog(resolvedScopeId, scriptId);
            return [scriptId, catalog] as const;
          } catch {
            return null;
          }
        }),
      );

      return Object.fromEntries(
        entries.filter(
          (entry): entry is readonly [string, ScriptCatalogSnapshot] =>
            entry !== null,
        ),
      ) as Record<string, ScriptCatalogSnapshot>;
    },
  });

  const proposalIds = React.useMemo(() => {
    const catalogs = scopeCatalogsQuery.data ?? {};
    return Array.from(
      new Set(
        Object.values(catalogs)
          .map((catalog) => catalog.lastProposalId)
          .filter(Boolean),
      ),
    );
  }, [scopeCatalogsQuery.data]);

  const proposalDecisionsQuery = useQuery({
    queryKey: ['studio-scripts-proposals', proposalIds.join('|')],
    enabled: proposalIds.length > 0,
    queryFn: async () => {
      const items = await Promise.all(
        proposalIds.map(async (proposalId) => {
          try {
            return await scriptsApi.getEvolutionDecision(proposalId);
          } catch {
            return null;
          }
        }),
      );

      return items.filter(
        (item): item is ScriptPromotionDecision => item !== null,
      );
    },
  });

  const filteredScopeScripts = React.useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return scopeScriptsQuery.data ?? [];
    }

    return (scopeScriptsQuery.data ?? []).filter((detail) =>
      [
        detail.script?.scriptId || '',
        detail.script?.activeRevision || '',
        detail.scopeId || '',
      ]
        .join(' ')
        .toLowerCase()
        .includes(keyword),
    );
  }, [scopeScriptsQuery.data, search]);

  const filteredRuntimeSnapshots = React.useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return runtimeSnapshotsQuery.data ?? [];
    }

    return (runtimeSnapshotsQuery.data ?? []).filter((snapshot) =>
      [snapshot.scriptId, snapshot.revision, snapshot.actorId]
        .join(' ')
        .toLowerCase()
        .includes(keyword),
    );
  }, [runtimeSnapshotsQuery.data, search]);

  const filteredProposalDecisions = React.useMemo(() => {
    const keyword = search.trim().toLowerCase();
    if (!keyword) {
      return proposalDecisionsQuery.data ?? [];
    }

    return (proposalDecisionsQuery.data ?? []).filter((decision) =>
      [
        decision.scriptId,
        decision.candidateRevision,
        decision.baseRevision,
        decision.proposalId,
      ]
        .join(' ')
        .toLowerCase()
        .includes(keyword),
    );
  }, [proposalDecisionsQuery.data, search]);

  const selectedRuntimeQuery = useQuery({
    queryKey: ['studio-scripts-runtime', selectedRuntimeActorId],
    enabled: Boolean(selectedRuntimeActorId),
    retry: 4,
    retryDelay: (attempt) => Math.min(attempt * 800, 2400),
    queryFn: () => scriptsApi.getRuntimeReadModel(selectedRuntimeActorId),
  });

  const selectedProposalQuery = useQuery({
    queryKey: ['studio-scripts-proposal', selectedProposalId],
    enabled: Boolean(selectedProposalId),
    queryFn: () => scriptsApi.getEvolutionDecision(selectedProposalId),
  });

  React.useEffect(() => {
    if (!runtimeSnapshotsQuery.data?.length) {
      return;
    }

    if (
      selectedRuntimeActorId &&
      runtimeSnapshotsQuery.data.some(
        (snapshot) => snapshot.actorId === selectedRuntimeActorId,
      )
    ) {
      return;
    }

    setSelectedRuntimeActorId(runtimeSnapshotsQuery.data[0].actorId);
  }, [runtimeSnapshotsQuery.data, selectedRuntimeActorId]);

  React.useEffect(() => {
    if (!proposalDecisionsQuery.data?.length) {
      return;
    }

    if (
      selectedProposalId &&
      proposalDecisionsQuery.data.some(
        (decision) => decision.proposalId === selectedProposalId,
      )
    ) {
      return;
    }

    setSelectedProposalId(proposalDecisionsQuery.data[0].proposalId);
  }, [proposalDecisionsQuery.data, selectedProposalId]);

  React.useEffect(() => {
    if (!scopeScriptsQuery.data?.length || !initialScriptIdRef.current) {
      return;
    }

    const matching = scopeScriptsQuery.data.find(
      (detail) => detail.script?.scriptId === initialScriptIdRef.current,
    );
    if (!matching) {
      return;
    }

    initialScriptIdRef.current = '';
    const existing =
      drafts.find((draft) => draft.scriptId === matching.script?.scriptId) || undefined;
    const nextDraft = hydrateDraftFromScopeDetail(
      matching,
      drafts.length + 1,
      existing,
    );
    setDrafts((current) => {
      const next = existing
        ? current.map((draft) => (draft.key === existing.key ? nextDraft : draft))
        : [nextDraft, ...current];
      return next;
    });
    setSelectedDraftKey(nextDraft.key);
    onSelectScriptId?.(matching.script?.scriptId || '');
  }, [drafts, onSelectScriptId, scopeScriptsQuery.data]);

  const validationPayload = React.useMemo(() => {
    if (!selectedDraft) {
      return '';
    }

    return JSON.stringify({
      scriptId: selectedDraft.scriptId,
      scriptRevision: selectedDraft.revision,
      source: serializePersistedSource(selectedDraft.package),
      package: selectedDraft.package,
    });
  }, [selectedDraft]);
  const deferredValidationPayload = React.useDeferredValue(validationPayload);

  React.useEffect(() => {
    if (!selectedDraft || !deferredValidationPayload) {
      setValidationResult(null);
      setValidationPending(false);
      setValidationError('');
      return;
    }

    const controller = new AbortController();
    const timeoutId = window.setTimeout(async () => {
      setValidationPending(true);
      setValidationError('');
      try {
        const payload = JSON.parse(deferredValidationPayload) as {
          scriptId: string;
          scriptRevision: string;
          source: string;
          package: ScriptPackage;
        };
        const result = await scriptsApi.validateDraft(payload, controller.signal);
        setValidationResult(result);
      } catch (error) {
        if (controller.signal.aborted) {
          return;
        }

        setValidationResult(null);
        setValidationError(
          error instanceof Error ? error.message : 'Validation failed.',
        );
      } finally {
        if (!controller.signal.aborted) {
          setValidationPending(false);
        }
      }
    }, 400);

    return () => {
      controller.abort();
      window.clearTimeout(timeoutId);
    };
  }, [deferredValidationPayload, selectedDraft]);

  React.useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        saveShortcutActionRef.current?.();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, []);

  const syncDraft = React.useCallback(
    (draftKey: string, updater: (draft: ScriptDraft) => ScriptDraft) => {
      setDrafts((current) =>
        current.map((draft) =>
          draft.key === draftKey
            ? (() => {
                const nextDraft = updater(draft);
                if (nextDraft === draft) {
                  return draft;
                }

                return {
                  ...nextDraft,
                  updatedAtUtc: new Date().toISOString(),
                };
              })()
            : draft,
        ),
      );
    },
    [],
  );

  const updateSelectedDraft = React.useCallback(
    (updater: (draft: ScriptDraft) => ScriptDraft) => {
      if (!selectedDraftKey) {
        return;
      }

      syncDraft(selectedDraftKey, updater);
    },
    [selectedDraftKey, syncDraft],
  );

  const selectedSnapshot =
    selectedRuntimeQuery.data || selectedDraft?.lastSnapshot || null;
  const selectedSnapshotView = React.useMemo(
    () => parseSnapshotView(selectedSnapshot),
    [selectedSnapshot],
  );
  const selectedDecision =
    selectedProposalQuery.data || selectedDraft?.lastPromotion || null;
  const selectedCatalog = React.useMemo(() => {
    const scriptId = selectedDraft?.scopeDetail?.script?.scriptId || '';
    if (!scriptId) {
      return null;
    }

    return scopeCatalogsQuery.data?.[scriptId] || null;
  }, [scopeCatalogsQuery.data, selectedDraft?.scopeDetail?.script?.scriptId]);
  const editorMarkers = React.useMemo(
    () =>
      buildEditorMarkers(
        validationResult,
        selectedPackageEntry?.path || validationResult?.primarySourcePath || '',
      ),
    [selectedPackageEntry?.path, validationResult],
  );
  const fileDialogError = React.useMemo(() => {
    if (!fileDialog || !selectedDraft) {
      return '';
    }

    return validatePackageFilePath(
      selectedDraft.package,
      fileDialog.kind,
      fileDialog.value,
      fileDialog.originalPath,
    );
  }, [fileDialog, selectedDraft]);

  React.useEffect(() => {
    if (!selectedDiagnosticKey) {
      return;
    }

    const hasMatch = (validationResult?.diagnostics ?? []).some(
      (diagnostic) =>
        `${diagnostic.filePath || ''}:${diagnostic.startLine || 0}:${diagnostic.startColumn || 0}:${diagnostic.message}` ===
        selectedDiagnosticKey,
    );
    if (!hasMatch) {
      setSelectedDiagnosticKey('');
    }
  }, [selectedDiagnosticKey, validationResult]);

  const createNewDraft = React.useCallback(() => {
    const nextDraft = createDraft(drafts.length + 1);
    setDrafts((current) => [nextDraft, ...current]);
    setSelectedDraftKey(nextDraft.key);
    setNotice({
      type: 'info',
      message: `Created ${nextDraft.scriptId}.`,
    });
  }, [drafts.length]);

  const openScopeScript = React.useCallback(
    (detail: ScopedScriptDetail) => {
      const existing =
        drafts.find((draft) => draft.scriptId === detail.script?.scriptId) || undefined;
      const nextDraft = hydrateDraftFromScopeDetail(
        detail,
        drafts.length + 1,
        existing,
      );
      setDrafts((current) =>
        existing
          ? current.map((draft) =>
              draft.key === existing.key ? nextDraft : draft,
            )
          : [nextDraft, ...current],
      );
      setSelectedDraftKey(nextDraft.key);
      onSelectScriptId?.(detail.script?.scriptId || '');
      setNotice({
        type: 'info',
        message: `已将 ${detail.script?.scriptId || '团队脚本'} 加入当前草稿列表。`,
      });
    },
    [drafts, onSelectScriptId],
  );

  const removeDraft = React.useCallback((draftKey: string, scriptId: string) => {
    setDrafts((current) => {
      const next = current.filter((draft) => draft.key !== draftKey);
      return next.length > 0 ? next : [createDraft(1)];
    });
    setSelectedDraftKey((current) => (current === draftKey ? '' : current));
    setNotice({
      type: 'info',
      message: `已移除 ${scriptId}。`,
    });
  }, []);

  const handleAddFile = React.useCallback(
    (kind: 'csharp' | 'proto') => {
      if (!selectedDraft) {
        return;
      }

      setFileDialog({
        mode: 'add',
        kind,
        originalPath: '',
        title: kind === 'csharp' ? '添加 C# 文件' : '添加 Proto 文件',
        confirmLabel: '添加文件',
        value: kind === 'csharp' ? 'Behavior.cs' : 'schema.proto',
      });
    },
    [selectedDraft],
  );

  const handleRenameFile = React.useCallback(
    (filePath: string) => {
      if (!selectedDraft) {
        return;
      }

      const entry = getPackageEntries(selectedDraft.package).find(
        (item) => item.path === filePath,
      );
      if (!entry) {
        return;
      }

      setFileDialog({
        mode: 'rename',
        kind: entry.kind,
        originalPath: filePath,
        title: '重命名文件',
        confirmLabel: '确认重命名',
        value: filePath,
      });
    },
    [selectedDraft],
  );

  const handleRemoveFile = React.useCallback(
    (filePath: string) => {
      if (!selectedDraft) {
        return;
      }

      updateSelectedDraft((draft) => {
        const nextPackage = removePackageFile(draft.package, filePath);
        const nextEntry =
          getSelectedPackageEntry(nextPackage, draft.selectedFilePath) ||
          getSelectedPackageEntry(nextPackage, nextPackage.entrySourcePath);

        return {
          ...draft,
          package: nextPackage,
          selectedFilePath:
            nextEntry?.path || nextPackage.entrySourcePath || 'Behavior.cs',
          lastRun: null,
          lastSnapshot: null,
          lastPromotion: null,
        };
      });
    },
    [selectedDraft, updateSelectedDraft],
  );

  const handleConfirmFileDialog = React.useCallback(() => {
    if (!selectedDraft || !fileDialog || fileDialogError) {
      return;
    }

    const nextFilePath = normalizePackageFilePath(fileDialog.value);
    if (fileDialog.mode === 'add') {
      updateSelectedDraft((draft) => {
        const nextPackage = addPackageFile(draft.package, fileDialog.kind, nextFilePath);
        const nextEntry =
          getSelectedPackageEntry(nextPackage, nextFilePath) ||
          getSelectedPackageEntry(nextPackage, draft.selectedFilePath);

        return {
          ...draft,
          package: nextPackage,
          selectedFilePath:
            nextEntry?.path || nextPackage.entrySourcePath || draft.selectedFilePath,
          lastRun: null,
          lastSnapshot: null,
          lastPromotion: null,
        };
      });
      setNotice({
        type: 'success',
        message: `已添加 ${nextFilePath}。`,
      });
    } else {
      updateSelectedDraft((draft) => {
        const nextPackage = renamePackageFile(
          draft.package,
          fileDialog.originalPath,
          nextFilePath,
        );
        return {
          ...draft,
          package: nextPackage,
          selectedFilePath:
            draft.selectedFilePath === fileDialog.originalPath
              ? nextFilePath
              : draft.selectedFilePath,
          lastRun: null,
          lastSnapshot: null,
          lastPromotion: null,
        };
      });
      setNotice({
        type: 'success',
        message: `已将 ${fileDialog.originalPath} 重命名为 ${nextFilePath}。`,
      });
    }

    setFileDialog(null);
  }, [fileDialog, fileDialogError, selectedDraft, updateSelectedDraft]);

  const handleConfirmRemoval = React.useCallback(() => {
    if (!removalTarget) {
      return;
    }

    if (removalTarget.kind === 'file') {
      handleRemoveFile(removalTarget.filePath);
      setNotice({
        type: 'info',
        message: `已删除 ${removalTarget.filePath}。`,
      });
    } else {
      removeDraft(removalTarget.draftKey, removalTarget.scriptId);
    }

    setRemovalTarget(null);
  }, [handleRemoveFile, removalTarget, removeDraft]);

  const handleSelectDiagnostic = React.useCallback(
    (diagnostic: ScriptValidationDiagnostic) => {
      if (!selectedDraft) {
        return;
      }

      const fallbackFilePath =
        diagnostic.filePath ||
        selectedPackageEntry?.path ||
        validationResult?.primarySourcePath ||
        selectedDraft.selectedFilePath;
      const targetEntry = getSelectedPackageEntry(
        selectedDraft.package,
        fallbackFilePath,
      );
      const targetFilePath =
        targetEntry?.path ||
        selectedPackageEntry?.path ||
        selectedDraft.selectedFilePath;

      if (targetEntry && targetFilePath !== selectedDraft.selectedFilePath) {
        updateSelectedDraft((draft) => ({
          ...draft,
          selectedFilePath: targetFilePath,
        }));
      }

      setSelectedDiagnosticKey(
        `${diagnostic.filePath || ''}:${diagnostic.startLine || 0}:${diagnostic.startColumn || 0}:${diagnostic.message}`,
      );
      setEditorFocusTarget({
        filePath: targetFilePath,
        startLineNumber: Math.max(diagnostic.startLine || 1, 1),
        startColumn: Math.max(diagnostic.startColumn || 1, 1),
        endLineNumber: Math.max(
          diagnostic.endLine || diagnostic.startLine || 1,
          diagnostic.startLine || 1,
        ),
        endColumn: Math.max(
          diagnostic.endColumn || (diagnostic.startColumn || 1) + 1,
          (diagnostic.startColumn || 1) + 1,
        ),
        token: `${Date.now()}_${Math.random().toString(36).slice(2, 8)}`,
      });
    },
    [selectedDraft, selectedPackageEntry?.path, updateSelectedDraft, validationResult?.primarySourcePath],
  );

  const refreshScopeScripts = React.useCallback(async () => {
    await queryClient.invalidateQueries({
      queryKey: ['studio-scripts-scope', appContext.scopeId],
    });
    await queryClient.invalidateQueries({
      queryKey: ['studio-scripts-catalogs', appContext.scopeId],
    });
    const bindingScopeIds = Array.from(
      new Set([appContext.scopeId, resolvedScopeId].filter(Boolean)),
    );
    for (const scopeId of bindingScopeIds) {
      await queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', scopeId],
      });
    }
  }, [appContext.scopeId, queryClient, resolvedScopeId]);

  const refreshRuntimeSnapshots = React.useCallback(async () => {
    await queryClient.invalidateQueries({
      queryKey: ['studio-scripts-runtimes'],
    });
    if (selectedRuntimeActorId) {
      await queryClient.invalidateQueries({
        queryKey: ['studio-scripts-runtime', selectedRuntimeActorId],
      });
    }
  }, [queryClient, selectedRuntimeActorId]);

  const openWorkspaceSection = React.useCallback((section: WorkspaceSection) => {
    setWorkspaceSection(section);
    setWorkspacePanelOpen(true);
    setEditorView('source');
  }, []);

  const toggleRightDrawer = React.useCallback((tab: 'panels' | 'package') => {
    if (tab === 'panels') {
      if (workspacePanelOpen && editorView === 'source') {
        setWorkspacePanelOpen(false);
        return;
      }

      setEditorView('source');
      setWorkspacePanelOpen(true);
      return;
    }

    if (editorView === 'package') {
      setEditorView('source');
      return;
    }

    setWorkspacePanelOpen(false);
    setEditorView('package');
  }, [editorView, workspacePanelOpen]);

  const closeRightDrawer = React.useCallback(() => {
    if (editorView === 'package') {
      setEditorView('source');
      return;
    }

    setWorkspacePanelOpen(false);
  }, [editorView]);

  const handleManualValidate = React.useCallback(async () => {
    if (!selectedDraft) {
      return;
    }

    setValidationPending(true);
    setValidationError('');
    try {
      const result = await scriptsApi.validateDraft({
        scriptId: selectedDraft.scriptId,
        scriptRevision: selectedDraft.revision,
        source: serializePersistedSource(selectedDraft.package),
        package: selectedDraft.package,
      });
      setValidationResult(result);
      setActiveResultTab('diagnostics');
      openWorkspaceSection('activity');
      setNotice({
        type: result.success ? 'success' : 'warning',
        message: result.success
          ? '校验完成，没有阻塞性错误。'
          : '校验返回了阻塞性错误。',
      });
    } catch (error) {
      setValidationResult(null);
      setValidationError(
        error instanceof Error ? error.message : 'Validation failed.',
      );
      setNotice({
        type: 'error',
        message: error instanceof Error ? error.message : '校验失败。',
      });
    } finally {
      setValidationPending(false);
    }
  }, [openWorkspaceSection, selectedDraft]);

  const saveCurrentDraftToScope = React.useCallback(async () => {
    if (!selectedDraft) {
      throw new Error('请先选择一个脚本草稿再保存。');
    }

    if (!scopeBacked) {
      throw new Error('只有绑定到当前团队后，才能保存脚本。');
    }

    const response = await scriptsApi.saveScript(resolvedScopeId, {
      scriptId: normalizeStudioId(selectedDraft.scriptId, 'script'),
      revisionId: normalizeStudioId(selectedDraft.revision, 'rev'),
      expectedBaseRevision: selectedDraft.baseRevision || undefined,
      sourceText: serializePersistedSource(selectedDraft.package),
    });

    updateSelectedDraft((draft) => ({
      ...draft,
      scriptId: response.script?.scriptId || draft.scriptId,
      revision:
        response.script?.activeRevision || response.source?.revision || draft.revision,
      baseRevision:
        response.script?.activeRevision || response.source?.revision || draft.baseRevision,
      definitionActorId:
        response.script?.definitionActorId ||
        response.source?.definitionActorId ||
        draft.definitionActorId,
      lastSourceHash:
        response.source?.sourceHash ||
        response.script?.activeSourceHash ||
        draft.lastSourceHash,
      scopeDetail: response,
    }));
    onSelectScriptId?.(response.script?.scriptId || selectedDraft.scriptId);

    return response;
  }, [onSelectScriptId, scopeBacked, selectedDraft, updateSelectedDraft]);

  const handleSave = React.useCallback(async () => {
    setSavePending(true);
    setNotice(null);
    try {
      const response = await saveCurrentDraftToScope();
      await refreshScopeScripts();
      setActiveResultTab('save');
      openWorkspaceSection('activity');
      setNotice({
        type: 'success',
        message: `已将 ${response.script?.scriptId || selectedDraft?.scriptId || 'script'} 保存到团队 ${response.scopeId}。`,
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message: error instanceof Error ? error.message : '保存当前草稿失败。',
      });
    } finally {
      setSavePending(false);
    }
  }, [openWorkspaceSection, refreshScopeScripts, saveCurrentDraftToScope, selectedDraft?.scriptId]);

  const handleOpenBindScope = React.useCallback(() => {
    const scopeScript = selectedDraft?.scopeDetail?.script;
    if (!scopeScript) {
      return;
    }

    setBindDisplayNameDraft(scopeScript.scriptId || selectedDraft.scriptId);
    setBindModalOpen(true);
  }, [selectedDraft]);

  const handleBindScope = React.useCallback(async () => {
    const scopeScript = selectedDraft?.scopeDetail?.script;
    if (!scopeScript || !resolvedScopeId) {
      return;
    }

    const scriptId = normalizeStudioId(scopeScript.scriptId || selectedDraft.scriptId, 'script');
    const scriptRevision = normalizeStudioId(
      scopeScript.activeRevision || selectedDraft.revision,
      'rev',
    );
    const bindingRevisionId = buildScopeScriptBindingRevisionId(
      scriptId,
      scriptRevision,
    );

    setBindPending(true);
    setNotice(null);
    try {
      const result = await studioApi.bindScopeScript({
        scopeId: resolvedScopeId,
        displayName: bindDisplayNameDraft.trim() || scriptId,
        scriptId,
        scriptRevision,
        revisionId: bindingRevisionId,
      });

      setBindModalOpen(false);
      await refreshScopeScripts();
      setActiveResultTab('save');
      openWorkspaceSection('activity');
      setNotice({
        type: 'success',
        message: `已将团队 ${result.scopeId} 的默认脚本入口更新为 ${result.targetName} · ${result.revisionId}。`,
        description: '你可以回到团队页查看当前入口、版本发布和已保存脚本。',
        actions: [
          {
            label: '打开团队资产',
            href: buildScopePageHref('/scopes/assets', resolvedScopeId, {
              tab: 'scripts',
              scriptId,
            }),
          },
          {
            label: '打开团队工作区',
            href: buildTeamWorkspaceRoute(resolvedScopeId),
          },
        ],
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : '绑定已保存脚本失败。',
      });
    } finally {
      setBindPending(false);
    }
  }, [
    bindDisplayNameDraft,
    openWorkspaceSection,
    refreshScopeScripts,
    resolvedScopeId,
    selectedDraft,
  ]);

  React.useEffect(() => {
    saveShortcutActionRef.current = () => {
      void handleSave();
    };
  }, [handleSave]);

  const handleRun = React.useCallback(async () => {
    if (!selectedDraft) {
      return;
    }

    if (!isEmbeddedMode) {
      setNotice({
        type: 'warning',
        message: '测试运行需要在嵌入式 Studio Host 中使用。',
      });
      return;
    }

    if (!resolvedScopeId) {
      setNotice({
        type: 'warning',
        message: '测试运行需要绑定到当前团队。',
      });
      return;
    }

    setRunPending(true);
    setNotice(null);
    try {
      const result = await scriptsApi.runDraftScript({
        scopeId: resolvedScopeId,
        scriptId: normalizeStudioId(selectedDraft.scriptId, 'script'),
        scriptRevision: normalizeStudioId(selectedDraft.revision, 'draft'),
        source: serializePersistedSource(selectedDraft.package),
        package: selectedDraft.package,
        input: runInputDraft,
        definitionActorId: selectedDraft.definitionActorId || undefined,
        runtimeActorId: selectedDraft.runtimeActorId || undefined,
      });

      updateSelectedDraft((draft) => ({
        ...draft,
        input: runInputDraft,
        runtimeActorId: result.runtimeActorId || draft.runtimeActorId,
        definitionActorId: result.definitionActorId || draft.definitionActorId,
        revision: result.scriptRevision || draft.revision,
        lastSourceHash: result.sourceHash || draft.lastSourceHash,
        lastRun: result,
      }));
      setSelectedRuntimeActorId(result.runtimeActorId || '');
      await queryClient.invalidateQueries({
        queryKey: ['studio-scripts-runtimes'],
      });
      if (result.runtimeActorId) {
        await queryClient.invalidateQueries({
          queryKey: ['studio-scripts-runtime', result.runtimeActorId],
        });
      }

      setRunModalOpen(false);
      setActiveResultTab('runtime');
      openWorkspaceSection('activity');
      setNotice({
        type: 'success',
        message: `已启动测试运行 ${result.runId}，运行实例 ${result.runtimeActorId}。`,
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : '启动脚本测试运行失败。',
      });
    } finally {
      setRunPending(false);
    }
  }, [
    isEmbeddedMode,
    openWorkspaceSection,
    queryClient,
    resolvedScopeId,
    runInputDraft,
    selectedDraft,
    updateSelectedDraft,
  ]);

  const handlePromote = React.useCallback(async () => {
    if (!selectedDraft) {
      return;
    }

    if (!scopeBacked) {
      setNotice({
        type: 'warning',
        message: '只有绑定到当前团队后，才能发布脚本。',
      });
      return;
    }

    const baseRevision =
      selectedDraft.scopeDetail?.script?.activeRevision || selectedDraft.baseRevision;
    if (!baseRevision) {
      setNotice({
        type: 'warning',
        message: '请先把脚本保存到当前团队，再继续发布。',
      });
      return;
    }

    setPromotionPending(true);
    setNotice(null);
    try {
      const scriptId = normalizeStudioId(selectedDraft.scriptId, 'script');
      const response = await scriptsApi.proposeEvolution(resolvedScopeId, scriptId, {
        baseRevision,
        candidateRevision: normalizeStudioId(selectedDraft.revision, 'rev'),
        candidateSource: serializePersistedSource(selectedDraft.package),
        reason: promotionReasonDraft || selectedDraft.reason || undefined,
      });

      updateSelectedDraft((draft) => ({
        ...draft,
        lastPromotion: response,
        reason: promotionReasonDraft || draft.reason,
      }));
      setSelectedProposalId(response.proposalId);
      setPromotionModalOpen(false);
      setActiveResultTab('promotion');
      openWorkspaceSection('activity');
      await refreshScopeScripts();
      await queryClient.invalidateQueries({
        queryKey: ['studio-scripts-proposals'],
      });
      setNotice({
        type: response.accepted ? 'success' : 'warning',
        message: response.accepted
          ? `已将 ${response.scriptId} 发布为 ${response.candidateRevision}。`
          : response.failureReason || '发布提案被拒绝。',
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : '发布当前草稿失败。',
      });
    } finally {
      setPromotionPending(false);
    }
  }, [
    promotionReasonDraft,
    queryClient,
    openWorkspaceSection,
    refreshScopeScripts,
    scopeBacked,
    selectedDraft,
    updateSelectedDraft,
  ]);

  const resetAskAiOutput = React.useCallback(() => {
    setAskAiReasoning('');
    setAskAiAnswer('');
    setAskAiGeneratedSource('');
    setAskAiGeneratedPackage(null);
    setAskAiGeneratedFilePath('');
  }, []);

  const cancelAskAiGeneration = React.useCallback(() => {
    if (!askAiAbortRef.current) {
      return;
    }

    askAiAbortRef.current.abort();
    askAiAbortRef.current = null;
    setAskAiPending(false);
      setNotice({
        type: 'info',
        message: '已取消 AI 生成。',
      });
  }, []);

  const closeAskAiComposer = React.useCallback(() => {
    if (askAiAbortRef.current) {
      cancelAskAiGeneration();
    }

    setAskAiOpen(false);
  }, [cancelAskAiGeneration]);

  const handleAskAiGenerate = React.useCallback(async () => {
    if (!selectedDraft || !askAiPrompt.trim()) {
      return;
    }

    if (!isEmbeddedMode) {
      setNotice({
        type: 'warning',
        message: askAiUnavailableMessage,
      });
      return;
    }

    askAiAbortRef.current?.abort();
    const controller = new AbortController();
    askAiAbortRef.current = controller;

    setAskAiPending(true);
    resetAskAiOutput();
    setNotice(null);
    try {
      const response = await scriptsApi.generateScript(
        {
          prompt: askAiPrompt.trim(),
          currentSource: serializePersistedSource(selectedDraft.package),
          currentPackage: selectedDraft.package,
          currentFilePath: selectedDraft.selectedFilePath,
          metadata: {
            source: 'aevatar-console-web',
            surface: 'studio-scripts',
          },
        },
        {
          signal: controller.signal,
          onReasoning: setAskAiReasoning,
          onText: setAskAiAnswer,
        },
      );

      const nextPackage =
        coerceScriptPackage(response.scriptPackage) ||
        deserializePersistedSource(response.text);
      const nextFilePath =
        response.currentFilePath ||
        getSelectedPackageEntry(nextPackage, selectedDraft.selectedFilePath)?.path ||
        nextPackage.entrySourcePath ||
        selectedDraft.selectedFilePath;

      const previewEntry = getSelectedPackageEntry(nextPackage, nextFilePath);
      setAskAiGeneratedPackage(nextPackage);
      setAskAiGeneratedFilePath(nextFilePath);
      setAskAiGeneratedSource(
        previewEntry?.content || response.text || serializePersistedSource(nextPackage),
      );
      setNotice({
        type: 'success',
        message: 'AI 已生成脚本修改，可以预览并应用到当前草稿。',
      });
    } catch (error) {
      if (controller.signal.aborted) {
        return;
      }

      setNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'AI 生成脚本失败。',
      });
    } finally {
      if (askAiAbortRef.current === controller) {
        askAiAbortRef.current = null;
        setAskAiPending(false);
      }
    }
  }, [
    askAiPrompt,
    askAiUnavailableMessage,
    isEmbeddedMode,
    resetAskAiOutput,
    selectedDraft,
  ]);

  const handleApplyAskAiSource = React.useCallback(() => {
    if (!askAiGeneratedPackage || !selectedDraft) {
      return;
    }

    updateSelectedDraft((draft) => ({
      ...draft,
      package: askAiGeneratedPackage,
      selectedFilePath:
        askAiGeneratedFilePath ||
        getSelectedPackageEntry(askAiGeneratedPackage, draft.selectedFilePath)?.path ||
        askAiGeneratedPackage.entrySourcePath ||
        draft.selectedFilePath,
      lastRun: null,
      lastSnapshot: null,
      lastPromotion: null,
    }));
    setAskAiOpen(false);
    resetAskAiOutput();
    setNotice({
      type: 'success',
      message: '已把 AI 生成的修改应用到当前草稿。',
    });
  }, [askAiGeneratedFilePath, askAiGeneratedPackage, resetAskAiOutput, selectedDraft, updateSelectedDraft]);

  const openAskAiComposer = React.useCallback(() => {
    if (!isEmbeddedMode) {
      setNotice({
        type: 'warning',
        message: askAiUnavailableMessage,
      });
      return;
    }

    setAskAiOpen(true);
    setAskAiPrompt('');
    resetAskAiOutput();
  }, [askAiUnavailableMessage, isEmbeddedMode, resetAskAiOutput]);

  const toggleAskAiComposer = React.useCallback(() => {
    if (askAiOpen) {
      closeAskAiComposer();
      return;
    }

    openAskAiComposer();
  }, [askAiOpen, closeAskAiComposer, openAskAiComposer]);

  function removeFloatingDragListeners() {
    if (typeof window !== 'undefined') {
      window.removeEventListener('pointermove', handleFloatingPointerMove);
      window.removeEventListener('pointerup', stopFloatingDrag);
      window.removeEventListener('pointercancel', stopFloatingDrag);
    }
  }

  function stopFloatingDrag() {
    removeFloatingDragListeners();

    setFloatingDragging(false);

    if (floatingDragStateRef.current?.moved) {
      suppressAskAiToggleRef.current = true;
    }

    floatingDragStateRef.current = null;
  }

  function handleFloatingPointerMove(event: PointerEvent) {
    const dragState = floatingDragStateRef.current;
    if (!dragState || dragState.pointerId !== event.pointerId) {
      return;
    }

    const deltaX = event.clientX - dragState.startClientX;
    const deltaY = event.clientY - dragState.startClientY;
    if (!dragState.moved && (Math.abs(deltaX) > 4 || Math.abs(deltaY) > 4)) {
      dragState.moved = true;
    }

    if (dragState.moved) {
      event.preventDefault();
    }

    setFloatingOffset(
      clampFloatingOffset(
        {
          x: dragState.startOffset.x + deltaX,
          y: dragState.startOffset.y + deltaY,
        },
        dragState.bounds,
      ),
    );
  }

  const beginFloatingDrag = React.useCallback(
    (event: React.PointerEvent<HTMLElement>) => {
      if (event.button !== 0) {
        return;
      }

      const viewport = floatingViewportRef.current;
      const floating = floatingRef.current;
      if (!viewport || !floating) {
        return;
      }

      const viewportRect = viewport.getBoundingClientRect();
      const floatingRect = floating.getBoundingClientRect();
      const currentOffset = floatingOffsetRef.current;

      floatingDragStateRef.current = {
        bounds: {
          baseLeft: floatingRect.left - viewportRect.left - currentOffset.x,
          baseTop: floatingRect.top - viewportRect.top - currentOffset.y,
          containerWidth: viewportRect.width,
          containerHeight: viewportRect.height,
          floatingWidth: floatingRect.width,
          floatingHeight: floatingRect.height,
        },
        moved: false,
        pointerId: event.pointerId,
        startClientX: event.clientX,
        startClientY: event.clientY,
        startOffset: currentOffset,
      };
      setFloatingDragging(true);

      if (typeof window !== 'undefined') {
        window.addEventListener('pointermove', handleFloatingPointerMove, {
          passive: false,
        });
        window.addEventListener('pointerup', stopFloatingDrag);
        window.addEventListener('pointercancel', stopFloatingDrag);
      }
    },
    [stopFloatingDrag],
  );

  const clampFloatingSurface = React.useCallback(() => {
    const viewport = floatingViewportRef.current;
    const floating = floatingRef.current;
    if (!viewport || !floating) {
      return;
    }

    const viewportRect = viewport.getBoundingClientRect();
    const floatingRect = floating.getBoundingClientRect();

    setFloatingOffset((current) => {
      const next = clampFloatingOffset(current, {
        baseLeft: floatingRect.left - viewportRect.left - current.x,
        baseTop: floatingRect.top - viewportRect.top - current.y,
        containerWidth: viewportRect.width,
        containerHeight: viewportRect.height,
        floatingWidth: floatingRect.width,
        floatingHeight: floatingRect.height,
      });

      if (next.x === current.x && next.y === current.y) {
        return current;
      }

      return next;
    });
  }, []);

  React.useEffect(() => {
    clampFloatingSurface();
  }, [askAiOpen, clampFloatingSurface]);

  React.useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    window.addEventListener('resize', clampFloatingSurface);
    return () => {
      window.removeEventListener('resize', clampFloatingSurface);
      removeFloatingDragListeners();
      floatingDragStateRef.current = null;
    };
  }, [clampFloatingSurface]);

  React.useEffect(
    () => () => {
      askAiAbortRef.current?.abort();
      askAiAbortRef.current = null;
    },
    [],
  );

  React.useEffect(() => {
    if (!selectedDraft || !selectedRuntimeQuery.data) {
      return;
    }

    updateSelectedDraft((draft) => {
      if (selectedRuntimeQuery.data?.actorId !== draft.runtimeActorId) {
        return draft;
      }

      if (isSameReadModelSnapshot(draft.lastSnapshot, selectedRuntimeQuery.data)) {
        return draft;
      }

      return {
        ...draft,
        lastSnapshot: selectedRuntimeQuery.data,
      };
    });
  }, [selectedDraft, selectedRuntimeQuery.data, updateSelectedDraft]);

  const packageEntries = selectedDraft ? getPackageEntries(selectedDraft.package) : [];
  const askAiPreviewEntry = React.useMemo(
    () =>
      askAiGeneratedPackage
        ? getSelectedPackageEntry(
            askAiGeneratedPackage,
            askAiGeneratedFilePath || askAiGeneratedPackage.entrySourcePath,
          )
        : null,
    [askAiGeneratedFilePath, askAiGeneratedPackage],
  );
  const canSave = Boolean(
    selectedDraft &&
      scopeBacked &&
      !savePending &&
      selectedDraft.scriptId.trim() &&
      selectedDraft.revision.trim(),
  );
  const canRun = Boolean(
    selectedDraft &&
      isEmbeddedMode &&
      !runPending &&
      !validationPending &&
      !hasValidationError(validationResult?.diagnostics ?? []),
  );
  const canUseAskAi = Boolean(selectedDraft && isEmbeddedMode);
  const canPromote = Boolean(
    selectedDraft &&
      scopeBacked &&
      !promotionPending &&
      (selectedDraft.scopeDetail?.script?.activeRevision || selectedDraft.baseRevision),
  );
  const scopeBindingScript = selectedDraft?.scopeDetail?.script || null;
  const canBindScope = Boolean(
    selectedDraft && scopeBacked && scopeBindingScript && !bindPending,
  );
  const scopeSelectionId = selectedDraft?.scopeDetail?.script?.scriptId || '';
  const hasScopeChanges = isScopeDetailDirty(selectedDraft);
  const hasUnsavedScopeChanges = React.useMemo(
    () => drafts.some((draft) => isScopeDetailDirty(draft)),
    [drafts],
  );
  const visibleProblems = validationResult?.diagnostics ?? [];
  const showFilesPane = filesPaneOpen;
  const scriptsInspectorTab =
    workspaceSection === 'activity' ? 'diagnostics' : 'info';
  const validationSummary = summarizeValidation(validationResult, validationPending);
  const validationPillClass = `console-scripts-validation-pill${
    validationPending
      ? ' pending'
      : validationResult?.errorCount
        ? ' error'
        : validationResult?.warningCount
          ? ' warning'
          : ''
  }`;
  const compilerSummary = validationPending
    ? '校验中'
    : validationError ||
      visibleProblems[0]?.message ||
      '通过';
  const headerRevisionLabel = selectedDraft?.revision || '草稿版本';
  const headerScopeLabel = scopeBacked
    ? `团队 ${compactHeaderValue(resolvedScopeId)}`
    : '本地草稿';
  const headerScopeTooltip = scopeBacked
    ? `团队 ${resolvedScopeId || '-'}`
    : '本地草稿';
  const moreActions: NonNullable<MenuProps['items']> = [
    {
      key: 'validate',
      label: 'Validate',
      icon: <ExperimentOutlined />,
      disabled: !selectedDraft || validationPending,
      onClick: () => void handleManualValidate(),
    },
    {
      key: 'promote',
      label: 'Promote',
      icon: <SafetyCertificateOutlined />,
      disabled: !canPromote,
      onClick: () => {
        setPromotionReasonDraft(
          selectedDraft?.reason ||
            `Promote ${selectedDraft?.revision || 'candidate'}`,
        );
        setPromotionModalOpen(true);
      },
    },
    {
      key: 'test-run',
      label: 'Test Run',
      icon: <PlayCircleOutlined />,
      disabled: !canRun,
      onClick: () => {
        setRunInputDraft(selectedDraft?.input || '');
        setRunModalOpen(true);
      },
    },
  ];
  const surfaceActionClass = (active = false) =>
    `console-scripts-surface-action ${active ? 'active' : ''}`;

  React.useEffect(() => {
    if (isEmbeddedMode) {
      return;
    }

    setAskAiOpen(false);
  }, [isEmbeddedMode]);

  React.useEffect(() => {
    onUnsavedChangesChange?.(hasUnsavedScopeChanges);
  }, [hasUnsavedScopeChanges, onUnsavedChangesChange]);

  React.useEffect(
    () => () => {
      onUnsavedChangesChange?.(false);
    },
    [onUnsavedChangesChange],
  );

  React.useEffect(() => {
    if (!hasUnsavedScopeChanges) {
      return undefined;
    }

    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };

    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => window.removeEventListener('beforeunload', handleBeforeUnload);
  }, [hasUnsavedScopeChanges]);

  return (
    <div className="console-scripts-page">
      {notice ? (
        <div style={{ padding: '16px 20px 0' }}>
          <Alert
            showIcon
            type={notice.type}
            title={notice.message}
            description={
              notice.description || notice.actions?.length ? (
                <Space orientation="vertical" size={12}>
                  {notice.description ? <span>{notice.description}</span> : null}
                  {notice.actions?.length ? (
                    <Space wrap size={[8, 8]}>
                      {notice.actions.map((action) => (
                        <Button
                          key={action.href}
                          size="small"
                          onClick={() => history.push(action.href)}
                        >
                          {action.label}
                        </Button>
                      ))}
                    </Space>
                  ) : null}
                </Space>
              ) : undefined
            }
            closable
            onClose={() => setNotice(null)}
          />
        </div>
      ) : null}

      <header className="console-scripts-header">
        <div className="console-scripts-toolbar">
          <div className="console-scripts-title-bar">
            <div className="console-scripts-title-group">
              <div className="console-scripts-title-copy">
                <div className="console-scripts-eyebrow">正在编辑</div>
                <input
                  className="console-scripts-title-input"
                  value={selectedDraft?.scriptId || ''}
                  placeholder="script-id"
                  aria-label="Script ID"
                  onChange={(event) =>
                    updateSelectedDraft((draft) => ({
                      ...draft,
                      scriptId: normalizeStudioId(event.target.value, 'script'),
                    }))
                  }
                />
                <div className="console-scripts-title-meta">
                  <Tooltip title={headerRevisionLabel} placement="bottom">
                    <span className="console-scripts-chip">{headerRevisionLabel}</span>
                  </Tooltip>
                  <Tooltip title={headerHostTooltip} placement="bottom">
                    <span className="console-scripts-chip muted">{headerHostLabel}</span>
                  </Tooltip>
                  <Tooltip title={headerScopeTooltip} placement="bottom">
                    <span className={`console-scripts-chip ${scopeBacked ? 'scope' : ''}`.trim()}>
                      {headerScopeLabel}
                    </span>
                  </Tooltip>
                </div>
              </div>
            </div>

            <div className="console-scripts-header-actions">
              {selectedDraft ? (
                <span className={validationPillClass}>
                  {validationPending ? (
                    <SyncOutlined spin />
                  ) : validationResult?.errorCount ? (
                    <WarningOutlined />
                  ) : (
                    <CheckCircleOutlined />
                  )}
                  <span style={{ marginLeft: 6 }}>{validationSummary}</span>
                </span>
              ) : null}
              <button
                type="button"
                className="console-scripts-ghost-action console-scripts-header-text-action"
                onClick={() => void handleManualValidate()}
                disabled={!selectedDraft || validationPending}
                aria-label="校验"
              >
                <ExperimentOutlined />
                <span>校验</span>
              </button>
              <button
                type="button"
                className="console-scripts-ghost-action console-scripts-header-text-action"
                onClick={() => {
                  setRunInputDraft(selectedDraft?.input || '');
                  setRunModalOpen(true);
                }}
                disabled={!canRun}
                aria-label="测试运行"
              >
                <PlayCircleOutlined />
                <span>测试</span>
              </button>
              <button
                type="button"
                className="console-scripts-solid-action console-scripts-header-text-action"
                onClick={() => {
                    setPromotionReasonDraft(
                      selectedDraft?.reason ||
                      `发布 ${selectedDraft?.revision || 'candidate'}`,
                  );
                  setPromotionModalOpen(true);
                }}
                disabled={!canPromote}
                aria-label="发布"
              >
                <SafetyCertificateOutlined />
                <span>{promotionPending ? '发布中' : '发布'}</span>
              </button>
              <button
                type="button"
                className="console-scripts-solid-action console-scripts-header-text-action"
                onClick={() => void handleSave()}
                disabled={!canSave}
                aria-label="保存"
              >
                <SaveOutlined />
                <span>{savePending ? '保存中' : '保存'}</span>
              </button>
            </div>
          </div>
        </div>
      </header>

      <section className="console-scripts-main" ref={floatingViewportRef}>
        <div className="console-scripts-main-inner">
          <section className="console-scripts-editor-shell">
            <div className="console-scripts-editor-head">
              <div>
                <div className="console-scripts-eyebrow">脚本编辑器</div>
                <div className="console-scripts-editor-title">
                  {selectedPackageEntry?.path ||
                    validationResult?.primarySourcePath ||
                    selectedDraft?.selectedFilePath ||
                    'Behavior.cs'}
                </div>
              </div>
              <div className="console-scripts-meta-strip">
                {hasScopeChanges ? (
                  <span className="console-scripts-chip dirty">
                    团队内有未保存变更
                  </span>
                ) : null}
                <span>{formatScriptDateTime(selectedDraft?.updatedAtUtc)}</span>
              </div>
            </div>

            <div className="console-scripts-editor-body">
              <div
                className="console-scripts-editor-layout"
                style={{ paddingRight: 336 }}
              >
                <div className={`console-scripts-file-pane ${showFilesPane ? '' : 'collapsed'}`}>
                  <ScriptsPackageFileTree
                    entries={packageEntries}
                    selectedFilePath={
                      selectedPackageEntry?.path || selectedDraft?.selectedFilePath || ''
                    }
                    entrySourcePath={selectedDraft?.package.entrySourcePath || ''}
                    collapsed={!showFilesPane}
                    onToggleCollapsed={() => setFilesPaneOpen((value) => !value)}
                    onSelectFile={(filePath) =>
                      updateSelectedDraft((draft) => ({
                        ...draft,
                        selectedFilePath: filePath,
                      }))
                    }
                    onAddFile={handleAddFile}
                    onRenameFile={handleRenameFile}
                    onRemoveFile={(filePath) =>
                      setRemovalTarget({
                        kind: 'file',
                        filePath,
                      })
                    }
                    onSetEntry={(filePath) =>
                      updateSelectedDraft((draft) => ({
                        ...draft,
                        package: setEntrySourcePath(draft.package, filePath),
                      }))
                    }
                  />
                </div>
                <div className="console-scripts-editor-pane">
                  {selectedPackageEntry ? (
                    <ScriptCodeEditorComponent
                      filePath={selectedPackageEntry.path}
                      focusTarget={editorFocusTarget}
                      language={
                        selectedPackageEntry.kind === 'csharp'
                          ? 'csharp'
                          : 'plaintext'
                      }
                      markers={editorMarkers}
                      onChange={(value) =>
                        updateSelectedDraft((draft) => ({
                          ...draft,
                          package: updatePackageFileContent(
                            draft.package,
                            selectedPackageEntry.path,
                            value,
                          ),
                        }))
                      }
                      value={selectedPackageEntry.content}
                    />
                  ) : (
                    <div
                      style={{
                        display: 'flex',
                        height: '100%',
                        alignItems: 'center',
                        justifyContent: 'center',
                        padding: 24,
                      }}
                    >
                      <ScriptsStudioEmptyState
                        title="创建或选择一个脚本草稿"
                        copy="先添加文件，或者选择已有草稿后再开始编辑。"
                      />
                    </div>
                  )}
                </div>
              </div>
            </div>

            <div className="console-scripts-editor-footer">
              <div className="console-scripts-compiler-copy">
                <div className="console-scripts-eyebrow">Compiler</div>
                <div className="console-scripts-compiler-summary">
                  {compilerSummary}
                </div>
              </div>
              <div className="console-scripts-inline-actions">
                {visibleProblems.length > 0 ? (
                  <button
                    type="button"
                    onClick={() => {
                      setActiveResultTab('diagnostics');
                      setWorkspaceSection('activity');
                    }}
                    className={surfaceActionClass(
                      workspaceSection === 'activity' &&
                        activeResultTab === 'diagnostics',
                    )}
                  >
                    <FileSearchOutlined />
                    诊断 {visibleProblems.length}
                  </button>
                ) : (
                  <div className="console-scripts-validation-pill">通过</div>
                )}
              </div>
            </div>
          </section>
        </div>

        <aside className="console-scripts-right-drawer open">
          <div className="console-scripts-drawer-header">
            <div>
              <div className="console-scripts-eyebrow">侧边面板</div>
              <div className="console-scripts-drawer-title">
                {scriptsInspectorTab === 'diagnostics' ? '诊断' : '脚本信息'}
              </div>
            </div>
            <div className="console-scripts-inline-actions">
              <button
                type="button"
                onClick={() => setWorkspaceSection('details')}
                className={surfaceActionClass(scriptsInspectorTab === 'info')}
              >
                脚本信息
              </button>
              <button
                type="button"
                onClick={() => setWorkspaceSection('activity')}
                className={surfaceActionClass(scriptsInspectorTab === 'diagnostics')}
              >
                诊断
              </button>
            </div>
          </div>

          <div className="console-scripts-drawer-body">
            <div style={{ height: '100%', minHeight: 0, overflow: 'hidden', padding: 16 }}>
              {scriptsInspectorTab === 'diagnostics' ? (
                <ScriptResultsPanel
                  activeResultTab={activeResultTab}
                  validationPending={validationPending}
                  validationError={validationError}
                  validationResult={validationResult}
                  selectedSnapshot={selectedSnapshot}
                  selectedSnapshotView={selectedSnapshotView}
                  activeDiagnosticKey={selectedDiagnosticKey}
                  selectedCatalog={selectedCatalog}
                  scopeDetail={selectedDraft?.scopeDetail || null}
                  selectedDecision={selectedDecision}
                  onChangeActiveResultTab={setActiveResultTab}
                  onSelectDiagnostic={handleSelectDiagnostic}
                />
              ) : (
                <ScriptInspectorPanel
                  appContext={appContext}
                  scopeBacked={scopeBacked}
                  selectedDraft={selectedDraft}
                  canAskAi={canUseAskAi}
                  canBindScope={canBindScope}
                  onOpenAskAi={openAskAiComposer}
                  onOpenBindScope={handleOpenBindScope}
                />
              )}
            </div>
          </div>
        </aside>

        <div
          ref={floatingRef}
          className={`console-scripts-floating ${
            floatingDragging ? 'dragging' : ''
          }`}
          style={{
            transform: `translate(${floatingOffset.x}px, ${floatingOffset.y}px)`,
          }}
        >
          {askAiOpen ? (
            <div className="console-scripts-ask-ai">
              <div
                className="console-scripts-ask-ai-head"
                onPointerDown={beginFloatingDrag}
              >
                <div>
                  <div className="console-scripts-eyebrow">AI 辅助</div>
                  <div className="console-scripts-section-title">生成脚本</div>
                </div>
                <button
                  type="button"
                  onClick={closeAskAiComposer}
                  onPointerDown={(event) => event.stopPropagation()}
                  className="console-scripts-icon-button"
                  title="关闭 AI 辅助"
                  aria-label="关闭 AI 辅助"
                >
                  <CloseOutlined />
                </button>
              </div>
              <div className="console-scripts-ask-ai-body">
                <div className="console-scripts-ask-ai-copy">
                  描述你想要的脚本修改内容。取消只会停止当前生成，不会改动现有草稿。
                </div>

                <textarea
                  rows={5}
                  className="console-scripts-textarea"
                  placeholder="构建一个脚本：校验邮箱、完成标准化处理，并返回 JSON 摘要。"
                  value={askAiPrompt}
                  onChange={(event) => setAskAiPrompt(event.target.value)}
                  style={{ marginTop: 16 }}
                />

                <div className="console-scripts-ask-ai-toolbar">
                  <div className="console-scripts-ask-ai-copy">
                    {askAiPending
                      ? '正在生成并编译文件内容...'
                      : askAiGeneratedSource
                        ? `可应用 ${askAiGeneratedPackage ? `${askAiGeneratedPackage.csharpSources.length + askAiGeneratedPackage.protoFiles.length} 个文件` : '当前文件'}`
                        : '返回格式：script package JSON'}
                  </div>
                  <div className="console-scripts-inline-actions">
                    {askAiPending ? (
                      <button
                        type="button"
                        onClick={cancelAskAiGeneration}
                        className="console-scripts-ghost-action"
                      >
                        取消
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={handleApplyAskAiSource}
                      className="console-scripts-ghost-action"
                      disabled={askAiPending || !askAiGeneratedPackage}
                    >
                      应用
                    </button>
                    <button
                      type="button"
                      onClick={() => void handleAskAiGenerate()}
                      className="console-scripts-solid-action"
                      disabled={askAiPending || !askAiPrompt.trim()}
                    >
                      {askAiPending ? '生成中' : '生成'}
                    </button>
                  </div>
                </div>

                <div className="console-scripts-ai-preview">
                  <div className="console-scripts-section-label">推理过程</div>
                  <pre className="console-scripts-pre" style={{ marginTop: 8 }}>
                    {askAiReasoning || '模型推理会显示在这里。'}
                  </pre>
                </div>

                <div className="console-scripts-ai-preview">
                  <div className="console-scripts-inline-actions" style={{ justifyContent: 'space-between' }}>
                    <div className="console-scripts-section-label">
                      生成预览
                    </div>
                    <div className="console-scripts-eyebrow">
                      {askAiGeneratedSource ? '可应用' : '等待中'}
                    </div>
                  </div>
                  {askAiGeneratedPackage ? (
                    <div className="console-scripts-detail-copy">
                      {askAiPreviewEntry?.path || askAiGeneratedFilePath || '-'} ·{' '}
                      {askAiGeneratedPackage.csharpSources.length} 个 C# 文件 ·{' '}
                      {askAiGeneratedPackage.protoFiles.length} 个 Proto 文件
                    </div>
                  ) : null}
                  <pre className="console-scripts-pre" style={{ marginTop: 8 }}>
                    {askAiGeneratedSource ||
                      askAiAnswer ||
                      '生成的文件内容会显示在这里。'}
                  </pre>
                </div>
              </div>
            </div>
          ) : null}

          <Tooltip
            title={
              canUseAskAi
                ? '使用 AI 生成脚本代码。'
                : askAiUnavailableMessage
            }
            placement="left"
          >
            <span
              className="console-scripts-floating-trigger-shell"
              onPointerDown={beginFloatingDrag}
            >
              <button
                type="button"
                onClick={() => {
                  if (suppressAskAiToggleRef.current) {
                    suppressAskAiToggleRef.current = false;
                    return;
                  }

                  toggleAskAiComposer();
                }}
                className={`console-scripts-ask-ai-trigger ${askAiOpen ? 'active' : ''}`}
                title={
                  canUseAskAi
                    ? '使用 AI 生成脚本代码。'
                    : askAiUnavailableMessage
                }
                aria-label="使用 AI 生成脚本代码。"
                disabled={!canUseAskAi}
              >
                <RobotOutlined />
              </button>
            </span>
          </Tooltip>
        </div>

        <ScriptsStudioModal
          open={runModalOpen}
          eyebrow="测试运行"
          title="开始测试"
          onClose={() => setRunModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setRunModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                取消
              </button>
              <button
                type="button"
                onClick={() => void handleRun()}
                className="console-scripts-solid-action"
                disabled={runPending}
              >
                {runPending ? '运行中' : '开始运行'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            测试运行会直接通过
            <code style={{ marginInline: 4 }}>/api/scopes/{'{scopeId}'}/scripts/draft-run</code>
            执行当前草稿，不会改动团队默认入口。
          </div>
          <textarea
            aria-label="脚本测试输入"
            rows={5}
            className="console-scripts-textarea"
            value={runInputDraft}
            onChange={(event) => setRunInputDraft(event.target.value)}
            style={{ marginTop: 16 }}
          />
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={promotionModalOpen}
          eyebrow="发布"
          title="发布当前草稿"
          onClose={() => setPromotionModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setPromotionModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                取消
              </button>
              <button
                type="button"
                onClick={() => void handlePromote()}
                className="console-scripts-solid-action"
                disabled={promotionPending}
              >
                {promotionPending ? '发布中' : '确认发布'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            发布会把测试中的草稿和团队线上版本区分开。当前团队版本会作为基线。
          </div>
          <textarea
            rows={4}
            className="console-scripts-textarea"
            value={promotionReasonDraft}
            onChange={(event) => setPromotionReasonDraft(event.target.value)}
            style={{ marginTop: 16 }}
          />
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={bindModalOpen}
          eyebrow="团队入口"
          title="绑定已保存脚本"
          onClose={() => setBindModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setBindModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                取消
              </button>
              <button
                type="button"
                onClick={() => void handleBindScope()}
                className="console-scripts-solid-action"
                disabled={bindPending}
              >
                {bindPending ? '绑定中' : '确认绑定'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            把当前已保存脚本绑定成这个团队的默认脚本入口。
          </div>
          <div style={{ marginTop: 16 }}>
            <div className="console-scripts-eyebrow">脚本版本</div>
            <div className="console-scripts-detail-copy">
              {scopeBindingScript?.scriptId || selectedDraft?.scriptId || '-'} ·{' '}
              {scopeBindingScript?.activeRevision || selectedDraft?.revision || '-'}
            </div>
          </div>
          <div style={{ marginTop: 16 }}>
            <div className="console-scripts-eyebrow">展示名称</div>
            <Input
              value={bindDisplayNameDraft}
              onChange={(event) => setBindDisplayNameDraft(event.target.value)}
              placeholder="脚本展示名称"
              style={{ marginTop: 8 }}
            />
          </div>
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={Boolean(fileDialog)}
          eyebrow="文件"
          title={fileDialog?.title || '编辑文件'}
          onClose={() => setFileDialog(null)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setFileDialog(null)}
                className="console-scripts-ghost-action"
              >
                取消
              </button>
              <button
                type="button"
                onClick={handleConfirmFileDialog}
                className="console-scripts-solid-action"
                disabled={!fileDialog || Boolean(fileDialogError)}
              >
                {fileDialog?.confirmLabel || '保存'}
              </button>
            </>
          }
          width={560}
        >
          <label className="console-scripts-field">
            <div className="console-scripts-field-label">文件路径</div>
            <input
              autoFocus
              className="console-scripts-input"
              value={fileDialog?.value || ''}
              aria-label="文件路径"
              onChange={(event) =>
                setFileDialog((current) =>
                  current
                    ? {
                        ...current,
                        value: event.target.value,
                      }
                    : current,
                )
              }
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !fileDialogError) {
                  event.preventDefault();
                  handleConfirmFileDialog();
                }
              }}
            />
          </label>
          {fileDialog ? (
            <div className="console-scripts-detail-copy">
              {fileDialog.kind === 'csharp'
                ? 'C# 文件需要以 .cs 结尾。'
                : 'Proto 文件需要以 .proto 结尾。'}
            </div>
          ) : null}
          {fileDialogError ? (
            <Alert
              showIcon
              type="warning"
              title={fileDialogError}
              style={{ marginTop: 16 }}
            />
          ) : null}
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={Boolean(removalTarget)}
          eyebrow={removalTarget?.kind === 'draft' ? '草稿' : '文件'}
          title={
            removalTarget?.kind === 'draft'
              ? '删除本地草稿'
              : '删除文件'
          }
          onClose={() => setRemovalTarget(null)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setRemovalTarget(null)}
                className="console-scripts-ghost-action"
              >
                取消
              </button>
              <button
                type="button"
                onClick={handleConfirmRemoval}
                className="console-scripts-solid-action"
              >
                删除
              </button>
            </>
          }
          width={520}
        >
          <div className="console-scripts-detail-copy" style={{ marginTop: 0 }}>
            {removalTarget?.kind === 'draft'
              ? `确认把 ${removalTarget.scriptId} 从本地草稿列表里删除吗？`
              : `确认把 ${removalTarget?.filePath || '这个文件'} 从当前脚本包里删除吗？`}
          </div>
        </ScriptsStudioModal>
      </section>
    </div>
  );
};

export default ScriptsWorkbenchPage;
