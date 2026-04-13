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
  ScopeScriptSaveObservationRequest,
  ScopeScriptSaveObservationResult,
  ScriptValidationDiagnostic,
  ScriptValidationResult,
  ScopedScriptDetail,
  ScopeScriptUpsertAcceptedResponse,
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
    return 'Checking';
  }

  if (validation.errorCount > 0) {
    return `${validation.errorCount} error${validation.errorCount === 1 ? '' : 's'}${
      validation.warningCount > 0
        ? ` · ${validation.warningCount} warning${validation.warningCount === 1 ? '' : 's'}`
        : ''
    }`;
  }

  if (validation.warningCount > 0) {
    return `${validation.warningCount} warning${
      validation.warningCount === 1 ? '' : 's'
    }`;
  }

  return 'Clean';
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

function buildSaveObservationRequest(
  accepted: ScopeScriptUpsertAcceptedResponse,
): ScopeScriptSaveObservationRequest {
  return {
    revisionId: accepted.acceptedScript.revisionId,
    definitionActorId: accepted.acceptedScript.definitionActorId,
    sourceHash: accepted.acceptedScript.sourceHash,
    proposalId: accepted.acceptedScript.proposalId,
    expectedBaseRevision: accepted.acceptedScript.expectedBaseRevision,
    acceptedAt: accepted.acceptedScript.acceptedAt,
  };
}

function wait(ms: number) {
  return new Promise((resolve) => window.setTimeout(resolve, ms));
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
    React.useState<WorkspaceSection>('library');
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
  const askAiUnavailableMessage = 'Ask AI requires an embedded host.';
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
        message: `Loaded ${detail.script?.scriptId || 'scope script'} into the active draft list.`,
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
      message: `Removed ${scriptId}.`,
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
        title: kind === 'csharp' ? 'Add C# file' : 'Add proto file',
        confirmLabel: 'Add file',
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
        title: 'Rename file',
        confirmLabel: 'Rename file',
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
        message: `Added ${nextFilePath}.`,
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
        message: `Renamed ${fileDialog.originalPath} to ${nextFilePath}.`,
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
        message: `Removed ${removalTarget.filePath}.`,
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
          ? 'Validation completed without blocking errors.'
          : 'Validation returned blocking errors.',
      });
    } catch (error) {
      setValidationResult(null);
      setValidationError(
        error instanceof Error ? error.message : 'Validation failed.',
      );
      setNotice({
        type: 'error',
        message: error instanceof Error ? error.message : 'Validation failed.',
      });
    } finally {
      setValidationPending(false);
    }
  }, [openWorkspaceSection, selectedDraft]);

  const saveCurrentDraftToScope = React.useCallback(async () => {
    if (!selectedDraft) {
      throw new Error('Select a script draft before saving.');
    }

    if (!scopeBacked) {
      throw new Error('Save is only available after Studio resolves the current scope.');
    }

    const persistedSource = serializePersistedSource(selectedDraft.package);
    const accepted = await scriptsApi.saveScript(resolvedScopeId, {
      scriptId: normalizeStudioId(selectedDraft.scriptId, 'script'),
      revisionId: normalizeStudioId(selectedDraft.revision, 'rev'),
      expectedBaseRevision: selectedDraft.baseRevision || undefined,
      sourceText: persistedSource,
    });

    updateSelectedDraft((draft) => ({
      ...draft,
      scriptId: accepted.acceptedScript.scriptId || draft.scriptId,
      definitionActorId:
        accepted.acceptedScript.definitionActorId || draft.definitionActorId,
      lastSourceHash: accepted.acceptedScript.sourceHash || draft.lastSourceHash,
    }));
    onSelectScriptId?.(accepted.acceptedScript.scriptId || selectedDraft.scriptId);

    return accepted;
  }, [onSelectScriptId, scopeBacked, selectedDraft, updateSelectedDraft]);

  const observeAcceptedSave = React.useCallback(async (
    accepted: ScopeScriptUpsertAcceptedResponse,
  ): Promise<ScopeScriptSaveObservationResult> => {
    const request = buildSaveObservationRequest(accepted);
    let lastObservation: ScopeScriptSaveObservationResult | null = null;

    for (let attempt = 0; attempt < 8; attempt += 1) {
      const observation = await scriptsApi.observeSaveScript(
        resolvedScopeId,
        accepted.acceptedScript.scriptId,
        request,
      );
      lastObservation = observation;
      if (observation.isTerminal) {
        return observation;
      }

      await wait(250);
    }

    return lastObservation ?? {
      scopeId: accepted.acceptedScript.scopeId,
      scriptId: accepted.acceptedScript.scriptId,
      status: 'pending',
      message: `Save request for ${accepted.acceptedScript.scriptId} is still waiting to appear in the scope catalog.`,
      currentScript: null,
      isTerminal: false,
    };
  }, [resolvedScopeId]);

  const waitForPromotionCatalog = React.useCallback(async (
    decision: ScriptPromotionDecision,
  ): Promise<ScriptCatalogSnapshot | null> => {
    if (!decision.accepted) {
      return null;
    }

    for (let attempt = 0; attempt < 8; attempt += 1) {
      try {
        const catalog = await scriptsApi.getScriptCatalog(
          resolvedScopeId,
          decision.scriptId,
        );
        if (catalogMatchesPromotion(catalog, decision)) {
          return catalog;
        }
      } catch {
        // Ignore transient query failures while the catalog is catching up.
      }

      await wait(250);
    }

    return null;
  }, [resolvedScopeId]);

  const handleSave = React.useCallback(async () => {
    setSavePending(true);
    setNotice(null);
    try {
      const accepted = await saveCurrentDraftToScope();
      setNotice({
        type: 'info',
        message: `Save accepted for ${accepted.acceptedScript.scriptId}. Waiting for the scope catalog to catch up.`,
      });
      const observation = await observeAcceptedSave(accepted);
      await refreshScopeScripts();
      if (observation.status === 'rejected') {
        throw new Error(observation.message);
      }

      if (observation.status === 'applied') {
        const detail = await scriptsApi.getScript(
          resolvedScopeId,
          accepted.acceptedScript.scriptId,
        );
        updateSelectedDraft((draft) => ({
          ...draft,
          scriptId: detail.script?.scriptId || draft.scriptId,
          revision:
            detail.script?.activeRevision || detail.source?.revision || draft.revision,
          baseRevision:
            detail.script?.activeRevision || detail.source?.revision || draft.baseRevision,
          definitionActorId:
            detail.script?.definitionActorId ||
            detail.source?.definitionActorId ||
            draft.definitionActorId,
          lastSourceHash:
            detail.source?.sourceHash ||
            detail.script?.activeSourceHash ||
            draft.lastSourceHash,
          scopeDetail: detail,
        }));
      }

      setActiveResultTab('save');
      openWorkspaceSection('activity');
      setNotice({
        type: observation.status === 'applied' ? 'success' : 'warning',
        message: observation.status === 'applied'
          ? `Saved ${accepted.acceptedScript.scriptId} into current scope ${accepted.acceptedScript.scopeId}.`
          : observation.message,
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message: error instanceof Error ? error.message : 'Failed to save the active draft.',
      });
    } finally {
      setSavePending(false);
    }
  }, [observeAcceptedSave, openWorkspaceSection, refreshScopeScripts, resolvedScopeId, saveCurrentDraftToScope, updateSelectedDraft]);

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
        message: `Updated scope ${result.scopeId} to serve script ${result.targetName} on revision ${result.revisionId}.`,
        description:
          'Review the active binding, revision rollout, and saved script assets from the scope views.',
        actions: [
          {
            label: 'Open Scope Scripts',
            href: buildScopePageHref('/scopes/scripts', resolvedScopeId, {
              scriptId,
            }),
          },
          {
            label: 'Open Scope Overview',
            href: buildScopePageHref('/scopes/overview', resolvedScopeId),
          },
        ],
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to bind the saved script.',
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
        message: 'Test Run requires an embedded Studio host.',
      });
      return;
    }

    if (!resolvedScopeId) {
      setNotice({
        type: 'warning',
        message: 'Test Run requires the current scope.',
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
        message: `Started draft run ${result.runId} on runtime ${result.runtimeActorId}.`,
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to start the script draft run.',
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
        message: 'Promotion is only available after Studio resolves the current scope.',
      });
      return;
    }

    const baseRevision =
      selectedDraft.scopeDetail?.script?.activeRevision || selectedDraft.baseRevision;
    if (!baseRevision) {
      setNotice({
        type: 'warning',
        message: 'Save the script into the current scope before proposing a promotion.',
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
      const observedCatalog = response.accepted
        ? await waitForPromotionCatalog(response)
        : null;
      if (observedCatalog) {
        const detail = await scriptsApi.getScript(
          resolvedScopeId,
          response.scriptId,
        );
        updateSelectedDraft((draft) => ({
          ...draft,
          scriptId: detail.script?.scriptId || draft.scriptId,
          revision:
            detail.script?.activeRevision || detail.source?.revision || draft.revision,
          baseRevision:
            detail.script?.activeRevision ||
            detail.source?.revision ||
            draft.baseRevision,
          definitionActorId:
            detail.script?.definitionActorId ||
            detail.source?.definitionActorId ||
            response.definitionActorId ||
            draft.definitionActorId,
          lastSourceHash:
            detail.source?.sourceHash ||
            detail.script?.activeSourceHash ||
            draft.lastSourceHash,
          scopeDetail: detail,
        }));
      }
      await refreshScopeScripts();
      await queryClient.invalidateQueries({
        queryKey: ['studio-scripts-proposals'],
      });
      setNotice({
        type: response.accepted
          ? observedCatalog
            ? 'success'
            : 'info'
          : 'warning',
        message: response.accepted
          ? observedCatalog
            ? `Promoted ${response.scriptId} to ${response.candidateRevision}.`
            : `Promotion accepted for ${response.scriptId} ${response.candidateRevision}. Waiting for the catalog read model to catch up.`
          : response.failureReason || 'Promotion proposal was rejected.',
      });
    } catch (error) {
      setNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to promote the active draft.',
      });
    } finally {
      setPromotionPending(false);
    }
  }, [
    promotionReasonDraft,
    queryClient,
    openWorkspaceSection,
    refreshScopeScripts,
    resolvedScopeId,
    scopeBacked,
    selectedDraft,
    updateSelectedDraft,
    waitForPromotionCatalog,
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
      message: 'Cancelled AI generation.',
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
        message: 'Generated script changes are ready to review and apply.',
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
            : 'Failed to generate script changes.',
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
      message: 'Applied AI-generated script changes to the active draft.',
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
  const packageModalOpen = editorView === 'package';
  const rightDrawerTab = packageModalOpen
    ? 'package'
    : workspacePanelOpen
      ? 'panels'
      : null;
  const rightDrawerOpen = rightDrawerTab !== null;
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
    ? 'Checking'
    : validationError ||
      visibleProblems[0]?.message ||
      'Clean';
  const headerRevisionLabel = selectedDraft?.revision || 'draft revision';
  const headerScopeLabel = scopeBacked
    ? `Scope ${compactHeaderValue(resolvedScopeId)}`
    : 'Local draft';
  const headerScopeTooltip = scopeBacked
    ? `Scope ${resolvedScopeId || '-'}`
    : 'Local draft';
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
                <Space direction="vertical" size={12}>
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
              <Tooltip
                title={scopeBacked ? 'Save the active draft into the current scope catalog' : 'Requires the current scope'}
                placement="bottom"
              >
                <span className="console-scripts-tooltip-anchor">
                  <button
                    type="button"
                    className="console-scripts-solid-action console-scripts-header-text-action"
                    onClick={() => void handleSave()}
                    disabled={!canSave}
                    aria-label="Save"
                  >
                    <SaveOutlined />
                    <span>{savePending ? 'Saving' : 'Save'}</span>
                  </button>
                </span>
              </Tooltip>
              <Tooltip
                title={
                  canBindScope
                    ? 'Bind the saved script to the default service for this scope.'
                    : 'Save the current script into the scope before binding it.'
                }
                placement="bottom"
              >
                <span className="console-scripts-tooltip-anchor">
                  <button
                    type="button"
                    className="console-scripts-solid-action console-scripts-header-text-action"
                    onClick={handleOpenBindScope}
                    disabled={!canBindScope}
                    aria-label="Bind scope"
                  >
                    <SafetyCertificateOutlined />
                    <span>Bind</span>
                  </button>
                </span>
              </Tooltip>
              <Dropdown
                menu={{ items: moreActions }}
                placement="bottomRight"
                trigger={['click']}
              >
                <button
                  type="button"
                  className="console-scripts-ghost-action console-scripts-header-text-action"
                  aria-label="More script actions"
                  disabled={!selectedDraft}
                >
                  <AppstoreOutlined />
                  <span>More</span>
                  <DownOutlined />
                </button>
              </Dropdown>
            </div>
          </div>
        </div>
      </header>

      <section className="console-scripts-main" ref={floatingViewportRef}>
        <div className="console-scripts-main-inner">
          <section className="console-scripts-editor-shell">
            <div className="console-scripts-editor-head">
              <div>
                <div className="console-scripts-eyebrow">Editor</div>
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
                    Unsaved scope changes
                  </span>
                ) : null}
                <span>{formatScriptDateTime(selectedDraft?.updatedAtUtc)}</span>
              </div>
            </div>

            <div className="console-scripts-editor-body">
              <div className="console-scripts-editor-layout">
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
                  <div className="console-scripts-editor-drawer-toggles">
                    <button
                      type="button"
                      onClick={() => toggleRightDrawer('panels')}
                      className={`console-scripts-icon-button ${
                        rightDrawerOpen && rightDrawerTab === 'panels' ? 'active' : ''
                      }`}
                      title="Panels"
                      aria-label="Panels"
                    >
                      <AppstoreOutlined />
                    </button>
                    <button
                      type="button"
                      onClick={() => toggleRightDrawer('package')}
                      className={`console-scripts-icon-button ${
                        rightDrawerOpen && rightDrawerTab === 'package' ? 'active' : ''
                      }`}
                      title="Package"
                      aria-label="Package"
                    >
                      <FolderOpenOutlined />
                    </button>
                  </div>

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
                        title="Create or select a script draft"
                        copy="Add a file to the package or select an existing draft to start editing."
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
                      openWorkspaceSection('activity');
                    }}
                    className={surfaceActionClass(
                      workspacePanelOpen &&
                        workspaceSection === 'activity' &&
                        activeResultTab === 'diagnostics',
                    )}
                  >
                    <FileSearchOutlined />
                    Problems {visibleProblems.length}
                  </button>
                ) : (
                  <div className="console-scripts-validation-pill">Clean</div>
                )}
              </div>
            </div>
          </section>
        </div>

        <aside className={`console-scripts-right-drawer ${rightDrawerOpen ? 'open' : ''}`}>
          <div className="console-scripts-drawer-header">
            <div>
              <div className="console-scripts-eyebrow">
                {rightDrawerTab === 'package' ? 'Package' : 'Panels'}
              </div>
              <div className="console-scripts-drawer-title">
                {rightDrawerTab === 'package'
                  ? 'Manifest'
                  : workspaceSection === 'library'
                    ? 'Library'
                    : workspaceSection === 'activity'
                      ? 'Activity'
                      : 'Details'}
              </div>
            </div>
            <button
              type="button"
              onClick={closeRightDrawer}
              className="console-scripts-icon-button"
              title="Close drawer"
              aria-label="Close drawer"
            >
              <CloseOutlined />
            </button>
          </div>

          {rightDrawerTab === 'panels' ? (
            <>
              <div className="console-scripts-drawer-switches">
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
              <div className="console-scripts-drawer-body">
                <div style={{ height: '100%', minHeight: 0, overflow: 'hidden', padding: 16 }}>
                  {workspaceSection === 'library' ? (
                    <ScriptsResourceRail
                      drafts={drafts}
                      filteredDrafts={filteredDrafts}
                      selectedDraftKey={selectedDraft?.key || ''}
                      search={search}
                      scopeBacked={scopeBacked}
                      scopeSelectionId={scopeSelectionId}
                      scopeScripts={filteredScopeScripts}
                      scopeScriptsLoading={
                        scopeScriptsQuery.isFetching || scopeScriptsQuery.isLoading
                      }
                      runtimeSnapshots={filteredRuntimeSnapshots}
                      runtimeSnapshotsLoading={
                        runtimeSnapshotsQuery.isFetching ||
                        runtimeSnapshotsQuery.isLoading
                      }
                      selectedRuntimeActorId={selectedRuntimeActorId}
                      proposalDecisions={filteredProposalDecisions}
                      selectedProposalId={selectedProposalId}
                      onCreateDraft={createNewDraft}
                      onSearchChange={setSearch}
                      onSelectDraft={(draft) => {
                        setSelectedDraftKey(draft.key);
                        onSelectScriptId?.(draft.scopeDetail?.script?.scriptId || '');
                      }}
                      onRefreshScopeScripts={() => void refreshScopeScripts()}
                      onOpenScopeScript={openScopeScript}
                      onRefreshRuntimeSnapshots={() => void refreshRuntimeSnapshots()}
                      onSelectRuntime={(snapshot) => {
                        setSelectedRuntimeActorId(snapshot.actorId);
                        setActiveResultTab('runtime');
                        setWorkspaceSection('activity');
                      }}
                      onSelectProposal={(decision) => {
                        setSelectedProposalId(decision.proposalId);
                        setActiveResultTab('promotion');
                        setWorkspaceSection('activity');
                      }}
                    />
                  ) : workspaceSection === 'activity' ? (
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
                    />
                  )}
                </div>
              </div>
            </>
          ) : (
            <div className="console-scripts-drawer-body">
              <ScriptsPackagePanel
                selectedDraft={selectedDraft}
                onRevisionChange={(value) =>
                  updateSelectedDraft((draft) => ({
                    ...draft,
                    revision: normalizeStudioId(value, 'rev'),
                  }))
                }
                onBaseRevisionChange={(value) =>
                  updateSelectedDraft((draft) => ({
                    ...draft,
                    baseRevision: normalizeStudioId(value, 'rev'),
                  }))
                }
                onEntryBehaviorTypeChange={(value) =>
                  updateSelectedDraft((draft) => ({
                    ...draft,
                    package: updateEntryBehaviorTypeName(draft.package, value),
                  }))
                }
                onDeleteDraft={() =>
                  selectedDraft
                    ? setRemovalTarget({
                        kind: 'draft',
                        draftKey: selectedDraft.key,
                        scriptId: selectedDraft.scriptId,
                      })
                    : undefined
                }
                canDeleteDraft={drafts.length > 1}
              />
            </div>
          )}
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
                  <div className="console-scripts-eyebrow">Source</div>
                  <div className="console-scripts-section-title">Ask AI</div>
                </div>
                <button
                  type="button"
                  onClick={closeAskAiComposer}
                  onPointerDown={(event) => event.stopPropagation()}
                  className="console-scripts-icon-button"
                  title="Close Ask AI"
                  aria-label="Close Ask AI"
                >
                  <CloseOutlined />
                </button>
              </div>
              <div className="console-scripts-ask-ai-body">
                <div className="console-scripts-ask-ai-copy">
                  Describe the script change you want. Cancel stops the current
                  generation without touching the active draft.
                </div>

                <textarea
                  rows={5}
                  className="console-scripts-textarea"
                  placeholder="Build a script that validates an email address, normalizes it, and returns a JSON summary."
                  value={askAiPrompt}
                  onChange={(event) => setAskAiPrompt(event.target.value)}
                  style={{ marginTop: 16 }}
                />

                <div className="console-scripts-ask-ai-toolbar">
                  <div className="console-scripts-ask-ai-copy">
                    {askAiPending
                      ? 'Generating and compiling file content...'
                      : askAiGeneratedSource
                        ? `Ready to apply ${askAiGeneratedPackage ? `${askAiGeneratedPackage.csharpSources.length + askAiGeneratedPackage.protoFiles.length} files` : 'the active file'}`
                        : 'Return format: script package JSON'}
                  </div>
                  <div className="console-scripts-inline-actions">
                    {askAiPending ? (
                      <button
                        type="button"
                        onClick={cancelAskAiGeneration}
                        className="console-scripts-ghost-action"
                      >
                        Cancel
                      </button>
                    ) : null}
                    <button
                      type="button"
                      onClick={handleApplyAskAiSource}
                      className="console-scripts-ghost-action"
                      disabled={askAiPending || !askAiGeneratedPackage}
                    >
                      Apply
                    </button>
                    <button
                      type="button"
                      onClick={() => void handleAskAiGenerate()}
                      className="console-scripts-solid-action"
                      disabled={askAiPending || !askAiPrompt.trim()}
                    >
                      {askAiPending ? 'Thinking' : 'Generate'}
                    </button>
                  </div>
                </div>

                <div className="console-scripts-ai-preview">
                  <div className="console-scripts-section-label">Thinking</div>
                  <pre className="console-scripts-pre" style={{ marginTop: 8 }}>
                    {askAiReasoning || 'LLM reasoning will stream here.'}
                  </pre>
                </div>

                <div className="console-scripts-ai-preview">
                  <div className="console-scripts-inline-actions" style={{ justifyContent: 'space-between' }}>
                    <div className="console-scripts-section-label">
                      Generated Preview
                    </div>
                    <div className="console-scripts-eyebrow">
                      {askAiGeneratedSource ? 'Ready to apply' : 'Waiting'}
                    </div>
                  </div>
                  {askAiGeneratedPackage ? (
                    <div className="console-scripts-detail-copy">
                      {askAiPreviewEntry?.path || askAiGeneratedFilePath || '-'} ·{' '}
                      {askAiGeneratedPackage.csharpSources.length} C# ·{' '}
                      {askAiGeneratedPackage.protoFiles.length} proto
                    </div>
                  ) : null}
                  <pre className="console-scripts-pre" style={{ marginTop: 8 }}>
                    {askAiGeneratedSource ||
                      askAiAnswer ||
                      'Generated file content will appear here.'}
                  </pre>
                </div>
              </div>
            </div>
          ) : null}

          <Tooltip
            title={
              canUseAskAi
                ? 'Ask AI to generate script code.'
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
                    ? 'Ask AI to generate script code.'
                    : askAiUnavailableMessage
                }
                aria-label="Ask AI to generate script code."
                disabled={!canUseAskAi}
              >
                <RobotOutlined />
              </button>
            </span>
          </Tooltip>
        </div>

        <ScriptsStudioModal
          open={runModalOpen}
          eyebrow="Runtime"
          title="Test Run"
          onClose={() => setRunModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setRunModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handleRun()}
                className="console-scripts-solid-action"
                disabled={runPending}
              >
                {runPending ? 'Running' : 'Run draft'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            Test Run executes the current draft directly through
            <code style={{ marginInline: 4 }}>/api/scopes/{'{scopeId}'}/scripts/draft-run</code>
            without rebinding the scope default service.
          </div>
          <textarea
            aria-label="Script test run input"
            rows={5}
            className="console-scripts-textarea"
            value={runInputDraft}
            onChange={(event) => setRunInputDraft(event.target.value)}
            style={{ marginTop: 16 }}
          />
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={promotionModalOpen}
          eyebrow="Evolution"
          title="Promote draft"
          onClose={() => setPromotionModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setPromotionModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handlePromote()}
                className="console-scripts-solid-action"
                disabled={promotionPending}
              >
                {promotionPending ? 'Promoting' : 'Promote'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            Promotion keeps test-run iteration separate from scope rollout. The current
            scope revision is used as the base.
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
          eyebrow="Scope"
          title="Bind saved script"
          onClose={() => setBindModalOpen(false)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setBindModalOpen(false)}
                className="console-scripts-ghost-action"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handleBindScope()}
                className="console-scripts-solid-action"
                disabled={bindPending}
              >
                {bindPending ? 'Binding' : 'Bind'}
              </button>
            </>
          }
        >
          <div className="console-scripts-detail-copy">
            Bind the saved scope script to the default service for this scope.
            Existing workflow authoring stays available alongside this flow.
          </div>
          <div style={{ marginTop: 16 }}>
            <div className="console-scripts-eyebrow">Script</div>
            <div className="console-scripts-detail-copy">
              {scopeBindingScript?.scriptId || selectedDraft?.scriptId || '-'} ·{' '}
              {scopeBindingScript?.activeRevision || selectedDraft?.revision || '-'}
            </div>
          </div>
          <div style={{ marginTop: 16 }}>
            <div className="console-scripts-eyebrow">Display name</div>
            <Input
              value={bindDisplayNameDraft}
              onChange={(event) => setBindDisplayNameDraft(event.target.value)}
              placeholder="Script display name"
              style={{ marginTop: 8 }}
            />
          </div>
        </ScriptsStudioModal>

        <ScriptsStudioModal
          open={Boolean(fileDialog)}
          eyebrow="Package"
          title={fileDialog?.title || 'Edit file'}
          onClose={() => setFileDialog(null)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setFileDialog(null)}
                className="console-scripts-ghost-action"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleConfirmFileDialog}
                className="console-scripts-solid-action"
                disabled={!fileDialog || Boolean(fileDialogError)}
              >
                {fileDialog?.confirmLabel || 'Save'}
              </button>
            </>
          }
          width={560}
        >
          <label className="console-scripts-field">
            <div className="console-scripts-field-label">File path</div>
            <input
              autoFocus
              className="console-scripts-input"
              value={fileDialog?.value || ''}
              aria-label="File path"
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
                ? 'C# files should end with .cs.'
                : 'Proto files should end with .proto.'}
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
          eyebrow={removalTarget?.kind === 'draft' ? 'Draft' : 'Package'}
          title={
            removalTarget?.kind === 'draft'
              ? 'Remove local draft'
              : 'Remove file'
          }
          onClose={() => setRemovalTarget(null)}
          actions={
            <>
              <button
                type="button"
                onClick={() => setRemovalTarget(null)}
                className="console-scripts-ghost-action"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={handleConfirmRemoval}
                className="console-scripts-solid-action"
              >
                Remove
              </button>
            </>
          }
          width={520}
        >
          <div className="console-scripts-detail-copy" style={{ marginTop: 0 }}>
            {removalTarget?.kind === 'draft'
              ? `Remove ${removalTarget.scriptId} from the local draft list?`
              : `Remove ${removalTarget?.filePath || 'this file'} from the current package?`}
          </div>
        </ScriptsStudioModal>
      </section>
    </div>
  );
};

export default ScriptsWorkbenchPage;
