import { PageContainer } from '@ant-design/pro-components';
import { InfoCircleOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { Node } from '@xyflow/react';
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';
import {
  buildTeamDetailHref,
  buildTeamsHref,
} from '@/shared/navigation/teamRoutes';
import {
  buildRuntimeRunsHref,
} from '@/shared/navigation/runtimeRoutes';
import { formatCompactDateTime } from '@/shared/datetime/dateTime';
import {
  buildConversationHeaders,
  formatConversationProviderLabel,
  normalizeUserLlmRoute,
  resolveReadyConversationRoute,
  routePathFromProviderSlug,
  USER_CONFIG_PROVIDER_SOURCE_GATEWAY,
  USER_CONFIG_PROVIDER_SOURCE_SERVICE,
  USER_LLM_ROUTE_GATEWAY,
} from '../chat/chatConversationConfig';
import { servicesApi } from '@/shared/api/servicesApi';
import {
  Button,
  Popover,
  message,
} from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { ensureActiveAuthSession } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { sanitizeReturnTo } from '@/shared/auth/session';
import {
  clearPlaygroundPromptHistory,
  loadPlaygroundPromptHistory,
  savePlaygroundPromptHistoryEntry,
  type PlaygroundPromptHistoryEntry,
} from '@/shared/playground/promptHistory';
import {
  saveScopeDraftRunPayload,
} from '@/shared/runs/draftRunSession';
import {
  buildScopeConsoleServiceOptions,
  scopeServiceAppId,
  scopeServiceNamespace,
} from '@/shared/runs/scopeConsole';
import {
  applyStepInspectorDraft,
  cloneStudioWorkflowDocument,
  connectStepToTarget,
  insertStepByType,
  removeStep,
  suggestBranchLabelForStep,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
} from '@/shared/studio/graph';
import { studioApi } from '@/shared/studio/api';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import type { ScopedScriptDetail } from '@/shared/studio/scriptsModels';
import {
  buildStudioRoute,
  type StudioBuildFocus,
  type StudioStep,
  type StudioTab,
} from '@/shared/studio/navigation';
import type {
  WorkflowCatalogDefinition,
} from '@/shared/models/runtime/catalog';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import {
  buildRuntimeGAgentAssemblyQualifiedName,
  matchesRuntimeGAgentTypeDescriptor,
} from '@/shared/models/runtime/gagents';
import type {
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioValidationFinding,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowDirectory,
} from '@/shared/studio/models';
import { getStudioScopeBindingCurrentRevision } from '@/shared/studio/models';
import { embeddedPanelStyle } from '@/shared/ui/proComponents';
import StudioBootstrapGate from './components/StudioBootstrapGate';
import StudioMemberInvokePanel from './components/StudioMemberInvokePanel';
import {
  getDefaultBuildModeCards,
  StudioGAgentBuildPanel,
  StudioScriptBuildPanel,
  StudioWorkflowBuildPanel,
} from './components/StudioBuildPanels';
import StudioShell, {
  type StudioLifecycleStep,
  type StudioShellMemberKind,
  type StudioShellMemberItem,
} from './components/StudioShell';
import StudioMemberBindPanel from './components/bind/StudioMemberBindPanel';
import {
  dedupeStudioWorkflowSummaries,
  StudioExecutionPage,
} from './components/StudioWorkbenchSections';

type StudioRouteState = {
  scopeId: string;
  memberId: string;
  step: StudioStep;
  focusKey: string;
  tab: StudioTab;
  draftMode: '' | 'new';
  prompt: string;
  executionId: string;
  logsMode: '' | 'popout';
};

type StudioBuildFocusKind = 'workflow' | 'script' | 'template' | 'none';
type StudioBuildFocusState = {
  key: string;
  kind: StudioBuildFocusKind;
  value: string;
};

type BuildMode = 'workflow' | 'script' | 'gagent';
type BuildSurface = 'editor' | 'scripts' | 'gagent';
type StudioSurface = 'build' | 'bind' | 'invoke' | 'observe';

type DraftSaveNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type DraftRunNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type StudioNotice = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type InlineInfoButtonProps = {
  readonly ariaLabel: string;
  readonly buttonStyle?: React.CSSProperties;
  readonly content: React.ReactNode;
  readonly placement?: 'bottomLeft' | 'bottomRight' | 'topLeft' | 'topRight';
};

const STUDIO_AUTO_RELOGIN_ATTEMPT_KEY =
  'aevatar-console:studio:auto-relogin:';

const inlineInfoButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #d8ddca',
  borderRadius: 999,
  color: '#7c6f5c',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 11,
  height: 22,
  justifyContent: 'center',
  padding: 0,
  width: 22,
};

const inlineInfoPopoverStyle: React.CSSProperties = {
  color: '#5f5b53',
  fontSize: 12,
  lineHeight: '18px',
  maxWidth: 240,
};

const InlineInfoButton: React.FC<InlineInfoButtonProps> = ({
  ariaLabel,
  buttonStyle,
  content,
  placement = 'bottomLeft',
}) => (
  <Popover
    content={<div style={inlineInfoPopoverStyle}>{content}</div>}
    placement={placement}
    trigger="click"
  >
    <button
      aria-label={ariaLabel}
      onClick={(event) => event.stopPropagation()}
      style={{ ...inlineInfoButtonStyle, ...buttonStyle }}
      type="button"
    >
      <InfoCircleOutlined />
    </button>
  </Popover>
);

function hasValidationError(findings: StudioValidationFinding[]): boolean {
  return findings.some((item) =>
    String(item.level ?? '').toLowerCase().includes('error'),
  );
}

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function normalizeComparableText(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function describeSavedWorkflowLocation(
  workflow: Pick<StudioWorkflowFile, 'directoryLabel' | 'fileName' | 'filePath'>,
): string {
  const directoryLabel = trimOptional(workflow.directoryLabel);
  const fileName = trimOptional(workflow.fileName);
  if (directoryLabel && fileName) {
    return `${directoryLabel}/${fileName}`;
  }

  const filePath = trimOptional(workflow.filePath);
  if (filePath) {
    return filePath;
  }

  return directoryLabel || fileName || '当前工作区';
}

function hasWorkflowGraphContent(
  document: StudioWorkflowDocument | null | undefined,
): boolean {
  const roleCount = Array.isArray(document?.roles) ? document.roles.length : 0;
  const stepCount = Array.isArray(document?.steps) ? document.steps.length : 0;
  return roleCount > 0 || stepCount > 0;
}

function buildTemplateWorkflowDocument(
  definition: WorkflowCatalogDefinition | null | undefined,
): StudioWorkflowDocument | null {
  if (!definition) {
    return null;
  }

  return {
    name: trimOptional(definition.name) || undefined,
    description: trimOptional(definition.description) || undefined,
    roles: definition.roles.map((role) => ({
      id: trimOptional(role.id) || undefined,
      name: trimOptional(role.name) || undefined,
      systemPrompt: trimOptional(role.systemPrompt) || undefined,
      provider: trimOptional(role.provider) || undefined,
      model: trimOptional(role.model) || undefined,
      connectors: role.connectors.filter((connector) => connector.trim().length > 0),
    })),
    steps: definition.steps.map((step) => ({
      id: trimOptional(step.id) || undefined,
      type: trimOptional(step.type) || undefined,
      targetRole: trimOptional(step.targetRole) || undefined,
      parameters: step.parameters,
      next: trimOptional(step.next) || null,
      branches: step.branches,
    })),
  };
}

function readWorkflowCallTargets(
  document: StudioWorkflowDocument | null | undefined,
): string[] {
  const steps = Array.isArray(document?.steps) ? document.steps : [];
  const seen = new Set<string>();
  const targets: string[] = [];

  for (const step of steps) {
    const normalizedType = trimOptional(
      typeof step?.type === 'string'
        ? step.type
        : typeof step?.originalType === 'string'
          ? step.originalType
          : '',
    );
    if (normalizedType !== 'workflow_call') {
      continue;
    }

    const parameters =
      step?.parameters && typeof step.parameters === 'object'
        ? (step.parameters as Record<string, unknown>)
        : null;
    const target = trimOptional(
      typeof parameters?.workflow === 'string' ? parameters.workflow : '',
    );
    if (!target || seen.has(target)) {
      continue;
    }

    seen.add(target);
    targets.push(target);
  }

  return targets;
}

function parseStudioTab(value: string | null): StudioTab {
  switch (value) {
    case 'studio':
    case 'bindings':
    case 'invoke':
    case 'scripts':
    case 'gagents':
    case 'executions':
      return value;
    default:
      return 'workflows';
  }
}

function parseStudioStep(value: string | null): StudioStep {
  switch (value) {
    case 'bind':
    case 'invoke':
    case 'observe':
      return value;
    default:
      return 'build';
  }
}

function parseDraftMode(value: string | null): '' | 'new' {
  return value === 'new' ? 'new' : '';
}

function parseLogsMode(value: string | null): '' | 'popout' {
  return value === 'popout' ? 'popout' : '';
}

function parseStudioBuildFocus(
  value: string | null | undefined,
): StudioBuildFocusState {
  const normalizedValue = trimOptional(value);
  if (normalizedValue.startsWith('workflow:')) {
    const workflowId = trimOptional(
      normalizedValue.slice('workflow:'.length),
    );
    return workflowId
      ? {
          key: `workflow:${workflowId}`,
          kind: 'workflow',
          value: workflowId,
        }
      : { key: '', kind: 'none', value: '' };
  }

  if (normalizedValue.startsWith('script:')) {
    const scriptId = trimOptional(normalizedValue.slice('script:'.length));
    return scriptId
      ? {
          key: `script:${scriptId}`,
          kind: 'script',
          value: scriptId,
        }
      : { key: '', kind: 'none', value: '' };
  }

  if (normalizedValue.startsWith('template:')) {
    const templateWorkflow = trimOptional(
      normalizedValue.slice('template:'.length),
    );
    return templateWorkflow
      ? {
          key: `template:${templateWorkflow}`,
          kind: 'template',
          value: templateWorkflow,
        }
      : { key: '', kind: 'none', value: '' };
  }

  return {
    key: '',
    kind: 'none',
    value: '',
  };
}

function readStudioBuildFocusFromParams(
  params: URLSearchParams,
): StudioBuildFocusState {
  return parseStudioBuildFocus(params.get('focus'));
}

function buildStudioBuildFocusKey(input: {
  buildSurface: BuildSurface;
  selectedWorkflowId?: string;
  selectedScriptId?: string;
  templateWorkflow?: string;
}): StudioBuildFocus | '' {
  if (input.buildSurface === 'gagent') {
    return '';
  }

  if (input.buildSurface === 'scripts') {
    const scriptId = trimOptional(input.selectedScriptId);
    return scriptId ? (`script:${scriptId}` as const) : '';
  }

  const workflowId = trimOptional(input.selectedWorkflowId);
  if (workflowId) {
    return `workflow:${workflowId}`;
  }

  const templateWorkflow = trimOptional(input.templateWorkflow);
  return templateWorkflow ? (`template:${templateWorkflow}` as const) : '';
}

function readDefaultDirectoryId(
  directories: StudioWorkflowDirectory[] | undefined,
): string {
  return directories?.[0]?.directoryId ?? '';
}

function buildStudioLoginRoute(returnTo: string): string {
  const params = new URLSearchParams({
    redirect: sanitizeReturnTo(returnTo),
  });
  return `/login?${params.toString()}`;
}

function getCurrentStudioReturnTo(): string {
  if (typeof window === 'undefined') {
    return '/studio';
  }

  return sanitizeReturnTo(
    `${window.location.pathname}${window.location.search}${window.location.hash}`,
  );
}

function getStudioAutoReloginStorageKey(returnTo: string): string {
  return `${STUDIO_AUTO_RELOGIN_ATTEMPT_KEY}${returnTo}`;
}

function hasStudioAutoReloginAttempt(returnTo: string): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  try {
    return (
      window.sessionStorage.getItem(getStudioAutoReloginStorageKey(returnTo)) ===
      '1'
    );
  } catch {
    return false;
  }
}

function markStudioAutoReloginAttempt(returnTo: string): void {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.sessionStorage.setItem(
      getStudioAutoReloginStorageKey(returnTo),
      '1',
    );
  } catch {
    // Ignore sessionStorage failures and continue with best-effort auth recovery.
  }
}

function clearStudioAutoReloginAttempt(returnTo: string): void {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.sessionStorage.removeItem(getStudioAutoReloginStorageKey(returnTo));
  } catch {
    // Ignore sessionStorage failures and continue with best-effort auth recovery.
  }
}

function isExecutionStopAllowed(status: string | undefined): boolean {
  const normalized = status?.trim().toLowerCase() ?? '';
  return !['completed', 'failed', 'stopped', 'cancelled'].includes(normalized);
}

function buildBlankDraftYaml(workflowName: string): string {
  const normalizedName = workflowName.trim() || 'draft';
  return `name: ${normalizedName}\nsteps: []\n`;
}

function readStudioRouteState(search?: string): StudioRouteState {
  if (typeof window === 'undefined' && typeof search !== 'string') {
    return {
      scopeId: '',
      memberId: '',
      step: 'build',
      focusKey: '',
      tab: 'workflows',
      draftMode: '',
      prompt: '',
      executionId: '',
      logsMode: '',
    };
  }

  const params = new URLSearchParams(
    typeof search === 'string'
      ? search
      : typeof window === 'undefined'
        ? ''
        : window.location.search,
  );
  const buildFocus = readStudioBuildFocusFromParams(params);
  return {
    scopeId: trimOptional(params.get('scopeId')),
    memberId: trimOptional(params.get('memberId')),
    step: parseStudioStep(params.get('step')),
    focusKey: buildFocus.key,
    tab: parseStudioTab(params.get('tab')),
    draftMode: parseDraftMode(params.get('draft')),
    prompt: trimOptional(params.get('prompt')),
    executionId: trimOptional(params.get('execution')),
    logsMode: parseLogsMode(params.get('logs')),
  };
}

function readInitialStudioSurface(state: StudioRouteState): StudioSurface {
  if (state.step === 'bind') {
    return 'bind';
  }

  if (state.step === 'invoke') {
    return 'invoke';
  }

  if (state.step === 'observe') {
    return 'observe';
  }

  if (state.tab === 'bindings') {
    return 'bind';
  }

  if (state.tab === 'invoke') {
    return 'invoke';
  }

  if (state.tab === 'executions' || state.executionId) {
    return 'observe';
  }

  return 'build';
}

function readInitialBuildSurface(state: StudioRouteState): BuildSurface {
  if (state.tab === 'gagents') {
    return 'gagent';
  }

  const buildFocus = parseStudioBuildFocus(state.focusKey);
  if (state.tab === 'scripts' || buildFocus.kind === 'script') {
    return 'scripts';
  }

  return 'editor';
}

function toExecutionSummary(
  execution: StudioExecutionDetail,
): StudioExecutionSummary {
  return {
    executionId: execution.executionId,
    workflowName: execution.workflowName,
    prompt: execution.prompt,
    status: execution.status,
    startedAtUtc: execution.startedAtUtc,
    completedAtUtc: execution.completedAtUtc,
    actorId: execution.actorId,
    error: execution.error,
  };
}

function buildStudioFocusKey(input: {
  activeBuildFocusKey?: string;
  routeMemberId?: string;
  currentBindingRevisionId?: string;
}): string {
  const activeBuildFocusKey = trimOptional(input.activeBuildFocusKey);
  if (activeBuildFocusKey) {
    return activeBuildFocusKey;
  }

  const routeMemberId = trimOptional(input.routeMemberId);
  if (routeMemberId) {
    return `member:${routeMemberId}`;
  }

  const currentBindingRevisionId = trimOptional(input.currentBindingRevisionId);
  if (currentBindingRevisionId) {
    return `binding:${currentBindingRevisionId}`;
  }

  return 'member:current';
}

function formatStudioAssetMeta(input: {
  primary?: string | null;
  secondary?: string | null;
}): string {
  return [trimOptional(input.primary), trimOptional(input.secondary)]
    .filter(Boolean)
    .join(' · ');
}

type StudioContextBadgeTone = 'accent' | 'default' | 'success' | 'warning';

function formatStudioMemberKindLabel(
  kind: StudioShellMemberKind | '' | undefined,
): string {
  switch (kind) {
    case 'workflow':
      return 'Workflow';
    case 'script':
      return 'Script';
    case 'gagent':
      return 'GAgent';
    case 'member':
      return 'Member';
    default:
      return 'Focus';
  }
}

function formatStudioLifecycleLabel(
  step: StudioSurface | StudioStep | string,
): string {
  switch (trimOptional(step).toLowerCase()) {
    case 'build':
      return 'Build';
    case 'bind':
      return 'Bind';
    case 'invoke':
      return 'Invoke';
    case 'observe':
      return 'Observe';
    default:
      return 'Workbench';
  }
}

function resolveStudioContextBadgeStyle(
  tone: StudioContextBadgeTone,
): React.CSSProperties {
  switch (tone) {
    case 'accent':
      return {
        background: 'rgba(225, 235, 255, 0.96)',
        borderColor: '#c8dafd',
        color: '#2452b5',
      };
    case 'success':
      return {
        background: 'rgba(232, 247, 236, 0.98)',
        borderColor: '#c8e7d0',
        color: '#23613e',
      };
    case 'warning':
      return {
        background: 'rgba(250, 239, 224, 0.98)',
        borderColor: '#ead0a6',
        color: '#8a5317',
      };
    default:
      return {
        background: 'rgba(245, 239, 228, 0.98)',
        borderColor: '#e5dccb',
        color: '#645946',
      };
  }
}

function resolveExecutionTone(status: string | null | undefined): StudioContextBadgeTone {
  const normalized = trimOptional(status).toLowerCase();
  if (
    normalized.includes('fail') ||
    normalized.includes('error') ||
    normalized.includes('stop')
  ) {
    return 'warning';
  }

  if (
    normalized.includes('run') ||
    normalized.includes('wait') ||
    normalized.includes('pending')
  ) {
    return 'accent';
  }

  if (
    normalized.includes('success') ||
    normalized.includes('complete') ||
    normalized.includes('done')
  ) {
    return 'success';
  }

  return 'default';
}

function isWorkflowNotFoundError(error: unknown): boolean {
  if (!(error instanceof Error)) {
    return false;
  }

  return /not found/i.test(error.message);
}

const StudioPage: React.FC = () => {
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => '',
  );
  const routeState = useMemo(() => {
    if (typeof window === 'undefined') {
      return readStudioRouteState('');
    }

    return readStudioRouteState(window.location.search);
  }, [locationSnapshot]);
  const routeStudioSurface = useMemo(
    () => readInitialStudioSurface(routeState),
    [routeState],
  );
  const routeBuildSurface = useMemo(
    () => readInitialBuildSurface(routeState),
    [routeState],
  );
  const routeBuildFocus = useMemo(
    () => parseStudioBuildFocus(routeState.focusKey),
    [routeState.focusKey],
  );
  const isStudioLocation =
    typeof window !== 'undefined' && window.location.pathname === '/studio';
  const nyxIdConfig = useMemo(() => getNyxIDRuntimeConfig(), []);
  const queryClient = useQueryClient();
  const [studioSurface, setStudioSurface] = useState<StudioSurface>(
    () => readInitialStudioSurface(readStudioRouteState()),
  );
  const [buildSurface, setBuildSurface] = useState<BuildSurface>(
    () => readInitialBuildSurface(readStudioRouteState()),
  );
  const initialBuildFocus = parseStudioBuildFocus(readStudioRouteState().focusKey);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState(
    () => (initialBuildFocus.kind === 'workflow' ? initialBuildFocus.value : ''),
  );
  const [selectedScriptId, setSelectedScriptId] = useState(
    () => (initialBuildFocus.kind === 'script' ? initialBuildFocus.value : ''),
  );
  const [selectedGAgentTypeName, setSelectedGAgentTypeName] = useState('');
  const [selectedExecutionId, setSelectedExecutionId] = useState(
    () => readStudioRouteState().executionId,
  );
  const [templateWorkflow, setTemplateWorkflow] = useState(
    () => (initialBuildFocus.kind === 'template' ? initialBuildFocus.value : ''),
  );
  const [draftMode, setDraftMode] = useState<'' | 'new'>(
    () => readStudioRouteState().draftMode,
  );
  const [draftYaml, setDraftYaml] = useState('');
  const [draftWorkflowName, setDraftWorkflowName] = useState('');
  const [draftFileName, setDraftFileName] = useState('');
  const [draftDirectoryId, setDraftDirectoryId] = useState('');
  const [draftWorkflowLayout, setDraftWorkflowLayout] = useState<unknown | null>(
    null,
  );
  const [editableWorkflowDocument, setEditableWorkflowDocument] =
    useState<StudioWorkflowDocument | null>(null);
  const [draftSourceKey, setDraftSourceKey] = useState('');
  const [savePending, setSavePending] = useState(false);
  const [saveNotice, setSaveNotice] = useState<DraftSaveNotice | null>(null);
  const [runPrompt, setRunPrompt] = useState(() => readStudioRouteState().prompt);
  const [runPending, setRunPending] = useState(false);
  const [runNotice, setRunNotice] = useState<DraftRunNotice | null>(null);
  const [selectedGraphNodeId, setSelectedGraphNodeId] = useState('');
  const [executionStopPending, setExecutionStopPending] = useState(false);
  const [executionNotice, setExecutionNotice] = useState<StudioNotice | null>(null);
  const [logsPopoutMode] = useState(() => readStudioRouteState().logsMode);
  const [appliedRouteSnapshot, setAppliedRouteSnapshot] = useState(
    locationSnapshot,
  );
  const [promptHistory, setPromptHistory] = useState<
    PlaygroundPromptHistoryEntry[]
  >(() => loadPlaygroundPromptHistory());
  const bindingSelectionRef = useRef<{
    serviceId: string;
    endpointId: string;
  }>({
    serviceId: '',
    endpointId: '',
  });
  const invokeSelectionRef = useRef<{
    serviceId: string;
    endpointId: string;
  }>({
    serviceId: '',
    endpointId: '',
  });
  const scriptLeaveGuardRef = useRef<(() => Promise<boolean>) | null>(null);
  const handledLocationSnapshotRef = useRef(locationSnapshot);
  const executionLogsWindowRef = useRef<Window | null>(null);
  const [logsDetached, setLogsDetached] = useState(false);
  const [authRecoveryPending, setAuthRecoveryPending] = useState(false);
  const authSessionQuery = useQuery({
    queryKey: ['studio-auth-session'],
    queryFn: () => studioApi.getAuthSession(),
  });
  const refetchAuthSession = authSessionQuery.refetch;
  const studioHostAccessResolved =
    !authSessionQuery.isLoading && !authSessionQuery.isError;
  const studioHostAuthenticated =
    authSessionQuery.data?.enabled === false ||
    Boolean(authSessionQuery.data?.authenticated);
  const studioHostReady =
    studioHostAccessResolved && studioHostAuthenticated;
  useEffect(() => {
    if (!isStudioLocation) {
      return;
    }

    if (handledLocationSnapshotRef.current === locationSnapshot) {
      return;
    }

    handledLocationSnapshotRef.current = locationSnapshot;
    setAppliedRouteSnapshot((currentSnapshot) =>
      currentSnapshot === locationSnapshot ? currentSnapshot : locationSnapshot,
    );
    setStudioSurface((currentSurface) =>
      currentSurface === routeStudioSurface ? currentSurface : routeStudioSurface,
    );
    setBuildSurface((currentSurface) =>
      currentSurface === routeBuildSurface ? currentSurface : routeBuildSurface,
    );
    if (routeBuildFocus.kind === 'workflow') {
      setSelectedWorkflowId((currentWorkflowId) =>
        trimOptional(currentWorkflowId) === routeBuildFocus.value
          ? currentWorkflowId
          : routeBuildFocus.value,
      );
    }
    if (routeBuildFocus.kind === 'script') {
      setSelectedScriptId((currentScriptId) =>
        trimOptional(currentScriptId) === routeBuildFocus.value
          ? currentScriptId
          : routeBuildFocus.value,
      );
    }
    setSelectedExecutionId((currentExecutionId) =>
      trimOptional(currentExecutionId) === routeState.executionId
        ? currentExecutionId
        : routeState.executionId,
    );
    if (routeBuildFocus.kind === 'template') {
      setTemplateWorkflow((currentTemplateWorkflow) =>
        trimOptional(currentTemplateWorkflow) === routeBuildFocus.value
          ? currentTemplateWorkflow
          : routeBuildFocus.value,
      );
    }
    setDraftMode((currentDraftMode) =>
      currentDraftMode === routeState.draftMode
        ? currentDraftMode
        : routeState.draftMode,
    );
    setRunPrompt((currentPrompt) =>
      currentPrompt === routeState.prompt ? currentPrompt : routeState.prompt,
    );
  }, [
    locationSnapshot,
    routeState.draftMode,
    routeState.executionId,
    routeState.prompt,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    routeBuildSurface,
    routeStudioSurface,
    isStudioLocation,
  ]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    if (authSessionQuery.isLoading || authSessionQuery.isError) {
      return;
    }

    const returnTo = getCurrentStudioReturnTo();
    if (!authSessionQuery.data?.enabled || authSessionQuery.data.authenticated) {
      clearStudioAutoReloginAttempt(returnTo);
      setAuthRecoveryPending(false);
      return;
    }

    if (!nyxIdConfig.enabled || hasStudioAutoReloginAttempt(returnTo)) {
      setAuthRecoveryPending(false);
      return;
    }

    let cancelled = false;
    setAuthRecoveryPending(true);

    void (async () => {
      await ensureActiveAuthSession(nyxIdConfig);
      if (cancelled) {
        return;
      }

      const refreshedAuth = await refetchAuthSession();
      if (cancelled) {
        return;
      }

      if (
        refreshedAuth.data?.enabled === false ||
        Boolean(refreshedAuth.data?.authenticated)
      ) {
        clearStudioAutoReloginAttempt(returnTo);
        setAuthRecoveryPending(false);
        return;
      }

      markStudioAutoReloginAttempt(returnTo);
      setAuthRecoveryPending(false);
      history.replace(buildStudioLoginRoute(returnTo));
    })();

    return () => {
      cancelled = true;
    };
  }, [
    authSessionQuery.data?.authenticated,
    authSessionQuery.data?.enabled,
    authSessionQuery.isError,
    authSessionQuery.isLoading,
    nyxIdConfig,
    refetchAuthSession,
  ]);

  const appContextQuery = useQuery({
    queryKey: ['studio-app-context'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getAppContext(),
  });
  const resolvedStudioScopeId =
    routeState.scopeId ||
    trimOptional(appContextQuery.data?.scopeId) ||
    trimOptional(authSessionQuery.data?.scopeId) ||
    '';
  const workflowWorkspaceContextKey = resolvedStudioScopeId || 'workspace';
  const workspaceSettingsQuery = useQuery({
    queryKey: ['studio-workspace-settings', workflowWorkspaceContextKey],
    enabled: studioHostReady,
    queryFn: () => studioApi.getWorkspaceSettings(resolvedStudioScopeId),
  });
  const userConfigQuery = useQuery({
    queryKey: ['studio-user-config'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getUserConfig(),
  });
  const userConfigModelsQuery = useQuery({
    queryKey: ['studio-user-config-models'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getUserConfigModels(),
  });
  const workflowsQuery = useQuery({
    queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
    enabled: studioHostReady,
    queryFn: () => studioApi.listWorkflows(resolvedStudioScopeId),
  });
  const scopeScriptsQuery = useQuery({
    queryKey: ['studio-scope-scripts', resolvedStudioScopeId],
    enabled:
      studioHostReady &&
      Boolean(resolvedStudioScopeId) &&
      Boolean(appContextQuery.data?.features.scripts),
    queryFn: () => scriptsApi.listScripts(resolvedStudioScopeId, true),
  });
  const scopeServicesQuery = useQuery({
    queryKey: ['studio-scope-services', resolvedStudioScopeId],
    enabled: studioHostReady && Boolean(resolvedStudioScopeId),
    queryFn: () =>
      servicesApi.listServices({
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
        tenantId: resolvedStudioScopeId,
      }),
  });
  const executionsQuery = useQuery({
    queryKey: ['studio-executions'],
    enabled: studioHostReady,
    queryFn: () => studioApi.listExecutions(),
  });
  const selectedWorkflowQuery = useQuery({
    queryKey: ['studio-workflow', workflowWorkspaceContextKey, selectedWorkflowId],
    enabled: studioHostReady && Boolean(selectedWorkflowId),
    queryFn: () => studioApi.getWorkflow(selectedWorkflowId, resolvedStudioScopeId),
  });
  const selectedExecutionQuery = useQuery({
    queryKey: ['studio-execution', selectedExecutionId],
    enabled: studioHostReady && Boolean(selectedExecutionId),
    queryFn: () => studioApi.getExecution(selectedExecutionId),
  });
  const scopeBindingQuery = useQuery({
    queryKey: ['studio-scope-binding', resolvedStudioScopeId],
    enabled: studioHostReady && Boolean(resolvedStudioScopeId),
    queryFn: () => studioApi.getScopeBinding(resolvedStudioScopeId),
  });
  const gAgentTypesQuery = useQuery({
    queryKey: ['studio-runtime-gagent-types'],
    enabled: studioHostReady,
    retry: false,
    queryFn: () => runtimeGAgentApi.listTypes(),
  });
  const runtimePrimitivesQuery = useQuery({
    queryKey: ['studio-runtime-primitives'],
    enabled: studioHostReady,
    retry: false,
    queryFn: () => runtimeQueryApi.listPrimitives(),
  });
  const visibleWorkflowSummaries = useMemo(
    () => dedupeStudioWorkflowSummaries(workflowsQuery.data ?? []),
    [workflowsQuery.data],
  );
  const currentScopeBindingRevision = useMemo(
    () => getStudioScopeBindingCurrentRevision(scopeBindingQuery.data ?? null),
    [scopeBindingQuery.data],
  );
  const boundWorkflowLookupKey = useMemo(() => {
    if (
      currentScopeBindingRevision?.implementationKind !== 'workflow'
    ) {
      return '';
    }

    return trimOptional(currentScopeBindingRevision.workflowName);
  }, [currentScopeBindingRevision]);
  useEffect(() => {
    if (
      (gAgentTypesQuery.data ?? []).some((descriptor) =>
        matchesRuntimeGAgentTypeDescriptor(selectedGAgentTypeName, descriptor),
      )
    ) {
      return;
    }

    const currentBindingTypeName =
      currentScopeBindingRevision?.implementationKind === 'gagent'
        ? trimOptional(currentScopeBindingRevision.staticActorTypeName)
        : '';
    const fallbackTypeName =
      currentBindingTypeName ||
      (gAgentTypesQuery.data?.[0]
        ? buildRuntimeGAgentAssemblyQualifiedName(gAgentTypesQuery.data[0])
        : '');

    if (!fallbackTypeName) {
      return;
    }

    setSelectedGAgentTypeName((current) =>
      trimOptional(current) === fallbackTypeName ? current : fallbackTypeName,
    );
  }, [
    currentScopeBindingRevision?.implementationKind,
    currentScopeBindingRevision?.staticActorTypeName,
    gAgentTypesQuery.data,
  ]);
  const preferredScopeWorkflow = useMemo(() => {
    const normalizedLookupKey = normalizeComparableText(boundWorkflowLookupKey);
    if (!normalizedLookupKey) {
      return null;
    }

    return (
      visibleWorkflowSummaries.find((item) => {
        const fileStem = item.fileName.replace(/\.(ya?ml)$/i, '');
        return (
          normalizeComparableText(item.workflowId) === normalizedLookupKey ||
          normalizeComparableText(item.name) === normalizedLookupKey ||
          normalizeComparableText(fileStem) === normalizedLookupKey
        );
      }) ?? null
    );
  }, [boundWorkflowLookupKey, visibleWorkflowSummaries]);
  const publishedScopeServices = useMemo(
    () => scopeServicesQuery.data ?? [],
    [scopeServicesQuery.data],
  );
  const runtimeConsoleServices = useMemo(
    () =>
      resolvedStudioScopeId
        ? buildScopeConsoleServiceOptions(
            publishedScopeServices,
            scopeBindingQuery.data?.available
              ? scopeBindingQuery.data.serviceId
              : undefined,
            {
              sortBy: 'serviceId',
            },
          )
        : [],
    [
      publishedScopeServices,
      resolvedStudioScopeId,
      scopeBindingQuery.data?.available,
      scopeBindingQuery.data?.serviceId,
    ],
  );
  const readyUserProviders = useMemo(
    () =>
      (userConfigModelsQuery.data?.providers ?? []).filter(
        (provider) => provider.status.trim().toLowerCase() === 'ready',
      ),
    [userConfigModelsQuery.data?.providers],
  );
  const readyGatewayProvider = useMemo(
    () =>
      readyUserProviders.find(
        (provider) =>
          (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) ===
          USER_CONFIG_PROVIDER_SOURCE_GATEWAY,
      ) ?? null,
    [readyUserProviders],
  );
  const readyServiceProviders = useMemo(
    () =>
      readyUserProviders.filter(
        (provider) =>
          (provider.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) ===
          USER_CONFIG_PROVIDER_SOURCE_SERVICE,
      ),
    [readyUserProviders],
  );
  const preferredDryRunRoute = useMemo(
    () => normalizeUserLlmRoute(userConfigQuery.data?.preferredLlmRoute),
    [userConfigQuery.data?.preferredLlmRoute],
  );
  const effectiveWorkflowDryRunRoute = useMemo(() => {
    return resolveReadyConversationRoute(
      preferredDryRunRoute,
      readyGatewayProvider,
      readyServiceProviders,
    );
  }, [preferredDryRunRoute, readyGatewayProvider, readyServiceProviders]);
  const effectiveWorkflowDryRunProvider = useMemo(() => {
    if (effectiveWorkflowDryRunRoute === USER_LLM_ROUTE_GATEWAY) {
      return readyGatewayProvider;
    }

    return (
      readyServiceProviders.find(
        (provider) =>
          routePathFromProviderSlug(provider.providerSlug) ===
          effectiveWorkflowDryRunRoute,
      ) ?? null
    );
  }, [
    effectiveWorkflowDryRunRoute,
    readyGatewayProvider,
    readyServiceProviders,
  ]);
  const effectiveWorkflowProviderModels = useMemo(
    () =>
      effectiveWorkflowDryRunProvider?.providerSlug
        ? (
            userConfigModelsQuery.data?.modelsByProvider?.[
              effectiveWorkflowDryRunProvider.providerSlug
            ] ?? []
          ).filter((model) => trimOptional(model))
        : [],
    [
      effectiveWorkflowDryRunProvider?.providerSlug,
      userConfigModelsQuery.data?.modelsByProvider,
    ],
  );
  const effectiveWorkflowDryRunModel = useMemo(() => {
    const preferredModel = trimOptional(userConfigQuery.data?.defaultModel);
    const canReusePreferredModel =
      Boolean(preferredModel) &&
      (
        effectiveWorkflowDryRunRoute === preferredDryRunRoute ||
        effectiveWorkflowProviderModels.length === 0 ||
        effectiveWorkflowProviderModels.includes(preferredModel)
      );

    if (preferredModel && canReusePreferredModel) {
      return preferredModel;
    }

    return (
      effectiveWorkflowProviderModels[0] ||
      userConfigModelsQuery.data?.supportedModels.find((model) =>
        trimOptional(model),
      ) ||
      preferredModel ||
      ''
    );
  }, [
    effectiveWorkflowDryRunRoute,
    effectiveWorkflowProviderModels,
    preferredDryRunRoute,
    userConfigModelsQuery.data?.supportedModels,
    userConfigQuery.data?.defaultModel,
  ]);
  const workflowDryRunHeaders = useMemo(
    () =>
      buildConversationHeaders(
        effectiveWorkflowDryRunRoute,
        effectiveWorkflowDryRunModel,
      ),
    [effectiveWorkflowDryRunModel, effectiveWorkflowDryRunRoute],
  );
  const workflowDryRunRouteLabel = useMemo(() => {
    if (effectiveWorkflowDryRunRoute === USER_LLM_ROUTE_GATEWAY) {
      return effectiveWorkflowDryRunProvider
        ? `NyxID Gateway · ${formatConversationProviderLabel(
            effectiveWorkflowDryRunProvider,
          )}`
        : 'NyxID Gateway';
    }

    return effectiveWorkflowDryRunProvider
      ? formatConversationProviderLabel(effectiveWorkflowDryRunProvider)
      : effectiveWorkflowDryRunRoute || 'Config default';
  }, [effectiveWorkflowDryRunProvider, effectiveWorkflowDryRunRoute]);
  const workflowDryRunBlockedReason = useMemo(() => {
    if (userConfigModelsQuery.isLoading) {
      return 'Studio 正在检查可用 provider，请稍后再运行。';
    }

    if (readyUserProviders.length === 0) {
      return '当前没有 ready 的 AI provider。先连接 provider，再回来运行这个 workflow draft。';
    }

    return '';
  }, [readyUserProviders.length, userConfigModelsQuery.isLoading]);
  const matchingWorkspaceWorkflow = useMemo(
    () =>
      visibleWorkflowSummaries.find((item) => item.name === templateWorkflow) ??
      null,
    [templateWorkflow, visibleWorkflowSummaries],
  );
  const templateWorkflowQuery = useQuery({
    queryKey: ['studio-template-workflow', templateWorkflow],
    enabled:
      studioHostReady &&
      Boolean(templateWorkflow) &&
      !matchingWorkspaceWorkflow,
    queryFn: () => studioApi.getTemplateWorkflow(templateWorkflow),
  });

  const activeWorkflowFile = selectedWorkflowQuery.data ?? null;
  const activeTemplate = templateWorkflowQuery.data ?? null;
  const workflowNames = useMemo(
    () => visibleWorkflowSummaries.map((item) => item.name),
    [visibleWorkflowSummaries],
  );
  const availableStepTypes = useMemo(() => {
    const stepTypes = new Set<string>();
    for (const primitive of runtimePrimitivesQuery.data ?? []) {
      const primitiveName = primitive.name.trim();
      if (primitiveName) {
        stepTypes.add(primitiveName);
      }

      for (const alias of primitive.aliases) {
        const normalizedAlias = alias.trim();
        if (normalizedAlias) {
          stepTypes.add(normalizedAlias);
        }
      }
    }

    return Array.from(stepTypes).sort((left, right) =>
      left.localeCompare(right),
    );
  }, [runtimePrimitivesQuery.data]);
  const defaultDirectoryId = useMemo(
    () => readDefaultDirectoryId(workspaceSettingsQuery.data?.directories),
    [workspaceSettingsQuery.data?.directories],
  );
  const activeWorkflowSourceKey = selectedWorkflowId
    ? `workflow:${workflowWorkspaceContextKey}:${selectedWorkflowId}`
    : templateWorkflow
      ? `template:${templateWorkflow}`
      : draftMode === 'new'
        ? 'draft:new'
      : '';
  const activeBuildFocusKey = useMemo(
    () =>
      buildStudioBuildFocusKey({
        buildSurface,
        selectedWorkflowId,
        selectedScriptId,
        templateWorkflow,
      }),
    [buildSurface, selectedScriptId, selectedWorkflowId, templateWorkflow],
  );
  const activeSourceReady = selectedWorkflowId
    ? Boolean(activeWorkflowFile)
    : templateWorkflow
      ? Boolean(activeTemplate)
      : true;

  const sourceYaml = useMemo(() => {
    if (activeWorkflowFile?.yaml?.trim()) {
      return activeWorkflowFile.yaml;
    }
    if (activeTemplate?.yaml?.trim()) {
      return activeTemplate.yaml;
    }
    if (draftMode === 'new') {
      return buildBlankDraftYaml('draft');
    }

    return '';
  }, [activeTemplate?.yaml, activeWorkflowFile?.yaml, draftMode]);
  const sourceWorkflowName =
    activeWorkflowFile?.name ||
    activeTemplate?.catalog.name ||
    (draftMode === 'new' ? 'draft' : '');
  const sourceFileName = activeWorkflowFile?.fileName || '';
  const sourceDirectoryId = activeWorkflowFile?.directoryId || defaultDirectoryId;
  const sourceWorkflowLayout = activeWorkflowFile?.layout ?? null;

  const templateWorkflowDocument = useMemo(
    () => buildTemplateWorkflowDocument(activeTemplate?.definition),
    [activeTemplate?.definition],
  );

  const parseYamlQuery = useQuery({
    queryKey: [
      'studio-parse-yaml',
      draftYaml,
      workflowNames.join('|'),
      availableStepTypes.join('|'),
    ],
    enabled: studioHostReady && Boolean(draftYaml.trim()),
    retry: false,
    queryFn: () =>
      studioApi.parseYaml({
        yaml: draftYaml,
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      }),
  });

  useEffect(() => {
    if (!draftYaml.trim()) {
      return;
    }

    if (!parseYamlQuery.data?.document) {
      return;
    }

    const nextParsedDocument = cloneStudioWorkflowDocument(
      parseYamlQuery.data.document as StudioWorkflowDocument | null,
    );
    const shouldUseTemplateFallback =
      Boolean(templateWorkflow) &&
      trimOptional(draftYaml) === trimOptional(sourceYaml) &&
      !hasWorkflowGraphContent(nextParsedDocument) &&
      hasWorkflowGraphContent(templateWorkflowDocument);

    setEditableWorkflowDocument(
      shouldUseTemplateFallback
        ? cloneStudioWorkflowDocument(templateWorkflowDocument)
        : nextParsedDocument,
    );
  }, [
    draftYaml,
    parseYamlQuery.data?.document,
    sourceYaml,
    templateWorkflow,
    templateWorkflowDocument,
  ]);

  useEffect(() => {
    if (
      selectedWorkflowId ||
      templateWorkflow ||
      draftMode === 'new'
    ) {
      return;
    }

    const preferredWorkflowId =
      preferredScopeWorkflow?.workflowId ||
      boundWorkflowLookupKey ||
      visibleWorkflowSummaries[0]?.workflowId ||
      '';
    if (!preferredWorkflowId) {
      return;
    }

    setSelectedWorkflowId(preferredWorkflowId);
  }, [
    boundWorkflowLookupKey,
    draftMode,
    preferredScopeWorkflow,
    selectedWorkflowId,
    templateWorkflow,
    visibleWorkflowSummaries,
  ]);

  useEffect(() => {
    if (
      !selectedWorkflowId ||
      !selectedWorkflowQuery.isError ||
      !isWorkflowNotFoundError(selectedWorkflowQuery.error)
    ) {
      return;
    }

    const fallbackWorkflowId =
      visibleWorkflowSummaries.find(
        (workflow) => workflow.workflowId !== selectedWorkflowId,
      )?.workflowId ?? '';

    if (fallbackWorkflowId) {
      setSelectedWorkflowId(fallbackWorkflowId);
      setTemplateWorkflow('');
      setDraftMode('');
      setSaveNotice(null);
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setDraftDirectoryId((current) => current || defaultDirectoryId);
    setDraftWorkflowLayout(null);
    setSaveNotice(null);
  }, [
    defaultDirectoryId,
    selectedWorkflowId,
    selectedWorkflowQuery.error,
    selectedWorkflowQuery.isError,
    visibleWorkflowSummaries,
  ]);

  useEffect(() => {
    if (!templateWorkflow || selectedWorkflowId || !matchingWorkspaceWorkflow) {
      return;
    }

    setSelectedWorkflowId(matchingWorkspaceWorkflow.workflowId);
    setTemplateWorkflow('');
    setDraftMode('');
  }, [
    matchingWorkspaceWorkflow,
    selectedWorkflowId,
    templateWorkflow,
  ]);

  useEffect(() => {
    if (selectedExecutionId || (executionsQuery.data?.length ?? 0) === 0) {
      return;
    }

    setSelectedExecutionId(executionsQuery.data?.[0]?.executionId ?? '');
  }, [executionsQuery.data, selectedExecutionId]);

  useEffect(() => {
    if (!activeWorkflowSourceKey) {
      setDraftSourceKey('');
      setDraftYaml('');
      setDraftWorkflowName('');
      setDraftFileName('');
      setDraftDirectoryId(defaultDirectoryId);
      setDraftWorkflowLayout(null);
      setEditableWorkflowDocument(null);
      setSaveNotice(null);
      return;
    }

    if (!activeSourceReady) {
      return;
    }
    if (draftSourceKey === activeWorkflowSourceKey && draftYaml.trim()) {
      return;
    }

    let disposed = false;
    const hydrateDraftFromSource = async () => {
      let nextYaml = sourceYaml;

      if (!nextYaml.trim() && activeWorkflowFile?.document) {
        try {
          const serialized = await studioApi.serializeYaml({
            document: activeWorkflowFile.document,
            availableWorkflowNames: workflowNames,
            availableStepTypes,
          });
          nextYaml = serialized?.yaml || '';
        } catch {
          // Keep the final fallback below when Studio cannot serialize the loaded document.
        }
      }

      if (!nextYaml.trim() && selectedWorkflowId) {
        nextYaml = buildBlankDraftYaml(
          sourceWorkflowName || activeWorkflowFile?.name || 'draft',
        );
      }

      if (disposed) {
        return;
      }

      setDraftSourceKey(activeWorkflowSourceKey);
      setDraftYaml(nextYaml);
      setDraftWorkflowName(sourceWorkflowName);
      setDraftFileName(sourceFileName);
      setDraftDirectoryId(sourceDirectoryId);
      setDraftWorkflowLayout(sourceWorkflowLayout);
      setEditableWorkflowDocument(
        cloneStudioWorkflowDocument(
          activeWorkflowFile?.document ??
            templateWorkflowDocument ??
            null,
        ),
      );
      setSaveNotice(null);
    };

    void hydrateDraftFromSource();

    return () => {
      disposed = true;
    };
  }, [
    activeWorkflowFile?.document,
    activeSourceReady,
    activeWorkflowSourceKey,
    availableStepTypes,
    defaultDirectoryId,
    draftSourceKey,
    draftYaml,
    selectedWorkflowId,
    sourceDirectoryId,
    sourceFileName,
    sourceWorkflowLayout,
    sourceWorkflowName,
    sourceYaml,
    workflowNames,
  ]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    if (!isStudioLocation) {
      return;
    }

    if (appliedRouteSnapshot !== locationSnapshot) {
      return;
    }

    const tab: StudioTab =
      studioSurface === 'bind'
        ? 'bindings'
        : studioSurface === 'invoke'
          ? 'invoke'
          : studioSurface === 'observe'
            ? 'executions'
            : buildSurface === 'gagent'
              ? 'gagents'
            : buildSurface === 'scripts'
              ? 'scripts'
              : 'studio';
    const step: StudioStep =
      studioSurface === 'bind'
        ? 'bind'
        : studioSurface === 'invoke'
          ? 'invoke'
          : studioSurface === 'observe'
            ? 'observe'
            : 'build';
    const persistWorkflowDraftRoute =
      studioSurface === 'build' && buildSurface === 'editor';
    const persistExecutionRoute = studioSurface === 'observe';
    const persistScriptRoute =
      studioSurface === 'build' && buildSurface === 'scripts';
    const persistBuildFocusRoute =
      studioSurface === 'build' &&
      ((persistWorkflowDraftRoute && Boolean(activeBuildFocusKey)) ||
        (persistScriptRoute && Boolean(activeBuildFocusKey)));

    history.replace(buildStudioRoute({
      scopeId: resolvedStudioScopeId || undefined,
      memberId: routeState.memberId || undefined,
      step,
      focus: persistBuildFocusRoute ? activeBuildFocusKey || undefined : undefined,
      tab,
      draftMode:
        persistWorkflowDraftRoute &&
        !activeBuildFocusKey &&
        draftMode === 'new'
          ? 'new'
          : undefined,
      prompt:
        studioSurface === 'build' && buildSurface === 'editor'
          ? runPrompt || undefined
          : undefined,
      executionId: persistExecutionRoute ? selectedExecutionId || undefined : undefined,
      logsMode: logsPopoutMode === 'popout' ? 'popout' : undefined,
    }));
  }, [
    appliedRouteSnapshot,
    activeBuildFocusKey,
    draftMode,
    isStudioLocation,
    locationSnapshot,
    logsPopoutMode,
    resolvedStudioScopeId,
    runPrompt,
    routeState.memberId,
    buildSurface,
    selectedExecutionId,
    studioSurface,
  ]);

  const activeWorkflowName = draftWorkflowName || sourceWorkflowName;
  const resolvedDraftDirectoryId = draftDirectoryId || defaultDirectoryId;
  const activeDirectoryLabel =
    workspaceSettingsQuery.data?.directories.find(
      (item) => item.directoryId === resolvedDraftDirectoryId,
    )?.label ||
    activeWorkflowFile?.directoryLabel ||
    'No directory';
  const activeWorkflowDescription =
    parseYamlQuery.data?.document?.description ||
    activeWorkflowFile?.document?.description ||
    activeTemplate?.catalog.description ||
    '';
  const parsedWorkflowDocument = parseYamlQuery.data?.document ?? null;
  const useTemplateWorkflowFallback =
    Boolean(templateWorkflow) &&
    trimOptional(draftYaml) === trimOptional(sourceYaml) &&
    !hasWorkflowGraphContent(parsedWorkflowDocument) &&
    hasWorkflowGraphContent(templateWorkflowDocument);
  const activeWorkflowDocument = useMemo(() => {
    if (
      editableWorkflowDocument &&
      draftSourceKey === activeWorkflowSourceKey
    ) {
      return editableWorkflowDocument;
    }

    if (useTemplateWorkflowFallback) {
      return templateWorkflowDocument;
    }

    if (parsedWorkflowDocument) {
      return parsedWorkflowDocument;
    }

    if (activeWorkflowFile?.document) {
      return activeWorkflowFile.document;
    }

    return templateWorkflowDocument;
  }, [
    activeWorkflowFile?.document,
    activeWorkflowSourceKey,
    draftSourceKey,
    editableWorkflowDocument,
    parsedWorkflowDocument,
    templateWorkflowDocument,
    useTemplateWorkflowFallback,
  ]);
  const activeWorkflowFindings = parseYamlQuery.data?.findings ?? [];
  const workflowGraph = useMemo(
    () => buildStudioGraphElements(activeWorkflowDocument, draftWorkflowLayout),
    [activeWorkflowDocument, draftWorkflowLayout],
  );
  const workflowRoleOptions = useMemo(
    () =>
      Array.isArray(activeWorkflowDocument?.roles)
        ? activeWorkflowDocument.roles
            .map((role) => ({
              id: trimOptional(role.id),
              name: trimOptional(role.name) || trimOptional(role.id),
            }))
            .filter(
              (role): role is { id: string; name: string } => Boolean(role.id),
            )
        : [],
    [activeWorkflowDocument?.roles],
  );

  useEffect(() => {
    setSelectedGraphNodeId('');
  }, [activeWorkflowSourceKey]);

  const isDraftDirty =
    Boolean(activeWorkflowSourceKey) &&
    (draftYaml !== sourceYaml ||
      draftWorkflowName !== sourceWorkflowName ||
      draftFileName !== sourceFileName ||
      resolvedDraftDirectoryId !== sourceDirectoryId);
  const canSaveWorkflow =
    studioHostReady &&
    Boolean(draftYaml.trim()) &&
    Boolean(draftWorkflowName.trim()) &&
    Boolean(resolvedDraftDirectoryId) &&
    !savePending;
  const canOpenRunWorkflow =
    Boolean(draftYaml.trim()) &&
    Boolean(activeWorkflowName.trim()) &&
    Boolean(resolvedStudioScopeId) &&
    !runPending &&
    !parseYamlQuery.isLoading &&
    !hasValidationError(activeWorkflowFindings);
  const canRunWorkflow =
    canOpenRunWorkflow && Boolean(runPrompt.trim());

  const resolveEditableWorkflowDocument = useCallback(
    async (): Promise<StudioWorkflowDocument | null> => {
      const currentEditableDocument = cloneStudioWorkflowDocument(
        editableWorkflowDocument as StudioWorkflowDocument | null,
      );
      if (currentEditableDocument) {
        return currentEditableDocument;
      }

      const normalizedDraftYaml = draftYaml.trim();
      if (normalizedDraftYaml) {
        try {
          const parsed = await studioApi.parseYaml({
            yaml: draftYaml,
            availableWorkflowNames: workflowNames,
            availableStepTypes,
          });
          const document = cloneStudioWorkflowDocument(
            parsed.document as StudioWorkflowDocument | null,
          );

          if (document) {
            return document;
          }

          if (hasValidationError(parsed.findings ?? [])) {
            setSaveNotice({
              type: 'error',
              message:
                'Resolve Studio YAML validation errors before editing the workflow graph.',
            });
            return null;
          }
        } catch (error) {
          setSaveNotice({
            type: 'error',
            message:
              error instanceof Error
                ? error.message
                : 'Failed to parse the current workflow draft.',
          });
          return null;
        }
      }

      const document = cloneStudioWorkflowDocument(
        activeWorkflowDocument as StudioWorkflowDocument | null,
      );
      if (document) {
        return document;
      }

      if (parseYamlQuery.isLoading) {
        setSaveNotice({
          type: 'error',
          message: 'Studio is still parsing the current workflow draft.',
        });
        return null;
      }

      if (hasValidationError(activeWorkflowFindings)) {
        setSaveNotice({
          type: 'error',
          message: 'Resolve Studio YAML validation errors before editing the workflow graph.',
        });
        return null;
      }

      setSaveNotice({
        type: 'error',
        message: 'Load a workflow draft before editing the workflow graph.',
      });
      return null;
    },
    [
      editableWorkflowDocument,
      activeWorkflowDocument,
      activeWorkflowFindings,
      availableStepTypes,
      draftYaml,
      parseYamlQuery.isLoading,
      workflowNames,
    ],
  );

  const applySerializedWorkflowDocument = useCallback(
    async (
      nextDocument: StudioWorkflowDocument,
      options?: {
        readonly layout?: unknown;
        readonly selectedNodeId?: string;
      },
    ) => {
      const serialized = await studioApi.serializeYaml({
        document: nextDocument,
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      });

      setDraftYaml(serialized.yaml);
      setEditableWorkflowDocument(cloneStudioWorkflowDocument(serialized.document));
      setDraftWorkflowName(
        trimOptional(serialized.document.name) || draftWorkflowName || 'draft',
      );
      if (options && 'layout' in options) {
        setDraftWorkflowLayout(options.layout ?? null);
      }
      if (options?.selectedNodeId !== undefined) {
        setSelectedGraphNodeId(options.selectedNodeId);
      }
      setSaveNotice(null);
      setRunNotice(null);
    },
    [availableStepTypes, draftWorkflowName, workflowNames],
  );

  const buildWorkflowYamlBundle = useCallback(async (): Promise<string[]> => {
    const rootYaml = draftYaml.trim();
    if (!rootYaml) {
      throw new Error('Workflow YAML is required.');
    }

    const workspaceWorkflows = visibleWorkflowSummaries;
    const availableWorkflowNames = workspaceWorkflows.map((item) => item.name);
    const workflowIdsByName = new Map(
      workspaceWorkflows.map((item) => [item.name, item.workflowId]),
    );
    const bundle: string[] = [];
    const seen = new Set<string>();
    const queue: Array<{
      workflowName: string;
      yaml: string;
      document: StudioWorkflowDocument | null | undefined;
    }> = [
      {
        workflowName: activeWorkflowName.trim() || draftWorkflowName.trim(),
        yaml: rootYaml,
        document: activeWorkflowDocument,
      },
    ];

    while (queue.length > 0) {
      const current = queue.shift();
      if (!current) {
        continue;
      }

      const normalizedWorkflowName = trimOptional(current.workflowName);
      if (normalizedWorkflowName && seen.has(normalizedWorkflowName)) {
        continue;
      }

      if (normalizedWorkflowName) {
        seen.add(normalizedWorkflowName);
      }
      bundle.push(current.yaml);

      for (const targetWorkflow of readWorkflowCallTargets(current.document)) {
        if (seen.has(targetWorkflow)) {
          continue;
        }

        const workflowId = workflowIdsByName.get(targetWorkflow);
        if (!workflowId) {
          throw new Error(
            `workflow_call references '${targetWorkflow}', but Studio could not resolve it from the workspace.`,
          );
        }

        const workflowFile = await studioApi.getWorkflow(
          workflowId,
          resolvedStudioScopeId,
        );
        const childDocument =
          workflowFile.document ??
          (
            await studioApi.parseYaml({
              yaml: workflowFile.yaml,
              availableWorkflowNames,
              availableStepTypes,
            })
          ).document ??
          null;

        queue.push({
          workflowName: trimOptional(workflowFile.name) || targetWorkflow,
          yaml: workflowFile.yaml,
          document: childDocument,
        });
      }
    }

    return bundle;
  }, [
    activeWorkflowDocument,
    activeWorkflowName,
    availableStepTypes,
    draftWorkflowName,
    draftYaml,
    resolvedStudioScopeId,
    visibleWorkflowSummaries,
  ]);
  const recentPromptHistory = useMemo(
    () => promptHistory.slice(0, 3),
    [promptHistory],
  );
  const executionCanStop = isExecutionStopAllowed(selectedExecutionQuery.data?.status);
  const isBuildSurface = studioSurface === 'build';
  const isBuildEditorSurface =
    studioSurface === 'build' && buildSurface === 'editor';
  const isBuildScriptsSurface =
    studioSurface === 'build' && buildSurface === 'scripts';
  const isBuildGAgentSurface =
    studioSurface === 'build' && buildSurface === 'gagent';
  const isBindSurface = studioSurface === 'bind';
  const isInvokeSurface = studioSurface === 'invoke';
  const isObserveSurface = studioSurface === 'observe';
  const activeBuildMode: BuildMode =
    buildSurface === 'scripts'
      ? 'script'
      : buildSurface === 'gagent'
        ? 'gagent'
        : 'workflow';

  const openWorkspaceWorkflow = (workflowId: string) => {
    const normalizedWorkflowId = trimOptional(workflowId);
    setSelectedWorkflowId(normalizedWorkflowId);
    setTemplateWorkflow('');
    setDraftMode('');
    setBuildSurface('editor');
    setStudioSurface('build');
  };

  const openExecution = (executionId: string) => {
    setSelectedExecutionId(executionId);
    setStudioSurface('observe');
  };

  const openScopeScript = useCallback((scriptId: string) => {
    const normalizedScriptId = trimOptional(scriptId);
    setSelectedScriptId(normalizedScriptId);
    setBuildSurface('scripts');
    setStudioSurface('build');
  }, []);

  const startBlankDraft = () => {
    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setDraftWorkflowLayout(null);
    setDraftDirectoryId((current) => current || defaultDirectoryId);
    setBuildSurface('editor');
    setStudioSurface('build');
  };

  const applyRunPrompt = (prompt: string) => {
    setRunPrompt(prompt);
    setRunNotice(null);
  };

  const clearPromptHistory = () => {
    setPromptHistory(clearPlaygroundPromptHistory());
  };

  const openWorkflowFromHistory = (workflowName: string, prompt: string) => {
    const normalizedWorkflowName = workflowName.trim();
    applyRunPrompt(prompt);

    if (!normalizedWorkflowName) {
      return;
    }

    const workspaceWorkflow = visibleWorkflowSummaries.find(
      (item) => item.name === normalizedWorkflowName,
    );
    if (workspaceWorkflow) {
      openWorkspaceWorkflow(workspaceWorkflow.workflowId);
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow(normalizedWorkflowName);
    setDraftMode('');
    setBuildSurface('editor');
    setStudioSurface('build');
  };

  const resetDraftFromSource = () => {
    setDraftSourceKey(activeWorkflowSourceKey);
    setDraftYaml(sourceYaml);
    setDraftWorkflowName(sourceWorkflowName);
    setDraftFileName(sourceFileName);
    setDraftDirectoryId(sourceDirectoryId);
    setDraftWorkflowLayout(sourceWorkflowLayout);
    setEditableWorkflowDocument(
      cloneStudioWorkflowDocument(
        activeWorkflowFile?.document ?? templateWorkflowDocument ?? null,
      ),
    );
    setSaveNotice(null);
    void parseYamlQuery.refetch();
  };

  const ensureActiveWorkflowDraftLoaded = useCallback(() => {
    if (activeWorkflowSourceKey && activeSourceReady) {
      if (
        draftSourceKey !== activeWorkflowSourceKey ||
        !draftYaml.trim() ||
        !draftWorkflowName.trim()
      ) {
        setDraftSourceKey(activeWorkflowSourceKey);
        setDraftYaml(sourceYaml);
        setDraftWorkflowName(sourceWorkflowName);
        setDraftFileName(sourceFileName);
        setDraftDirectoryId(sourceDirectoryId);
        setDraftWorkflowLayout(sourceWorkflowLayout);
        setSaveNotice(null);
      }
      return;
    }

    const fallbackWorkflowId =
      selectedWorkflowId || visibleWorkflowSummaries[0]?.workflowId || '';
    if (fallbackWorkflowId) {
      setSelectedWorkflowId(fallbackWorkflowId);
      setTemplateWorkflow('');
      setDraftMode('');
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setDraftDirectoryId((current) => current || defaultDirectoryId);
    setDraftWorkflowLayout(null);
  }, [
    activeSourceReady,
    activeWorkflowSourceKey,
    defaultDirectoryId,
    draftSourceKey,
    draftWorkflowName,
    draftYaml,
    selectedWorkflowId,
    sourceDirectoryId,
    sourceFileName,
    sourceWorkflowLayout,
    sourceWorkflowName,
    sourceYaml,
    visibleWorkflowSummaries,
  ]);

  const handleSaveDraft = async () => {
    const directoryId = resolvedDraftDirectoryId;
    if (!directoryId) {
      setSaveNotice({
        type: 'error',
        message: 'Add a workflow directory in Config before saving.',
      });
      return;
    }

    const workflowName = draftWorkflowName.trim();
    if (!workflowName) {
      setSaveNotice({
        type: 'error',
        message: 'Workflow name is required before saving.',
      });
      return;
    }

    setSavePending(true);
    setSaveNotice(null);

    try {
      const savedWorkflow = await studioApi.saveWorkflow({
        workflowId: activeWorkflowFile?.workflowId || undefined,
        scopeId: resolvedStudioScopeId || undefined,
        directoryId,
        workflowName,
        fileName: draftFileName,
        yaml: draftYaml,
        layout:
          draftWorkflowLayout ||
          activeWorkflowFile?.layout ||
          buildStudioWorkflowLayout(activeWorkflowName, workflowGraph.nodes),
      });

      queryClient.setQueryData(
        ['studio-workflow', workflowWorkspaceContextKey, savedWorkflow.workflowId],
        savedWorkflow,
      );
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
      });

      setSelectedWorkflowId(savedWorkflow.workflowId);
      setTemplateWorkflow('');
      setDraftMode('');
      setDraftSourceKey(
        `workflow:${workflowWorkspaceContextKey}:${savedWorkflow.workflowId}`,
      );
      setDraftYaml(savedWorkflow.yaml);
      setDraftWorkflowName(savedWorkflow.name);
      setDraftFileName(savedWorkflow.fileName);
      setDraftDirectoryId(savedWorkflow.directoryId);
      setDraftWorkflowLayout(
        savedWorkflow.layout ||
          draftWorkflowLayout ||
          buildStudioWorkflowLayout(savedWorkflow.name, workflowGraph.nodes),
      );
      setSaveNotice(null);
      void message.success(
        `已保存到 ${describeSavedWorkflowLocation(savedWorkflow)}。`,
      );
    } catch (error) {
      setSaveNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to save workflow.',
      });
    } finally {
      setSavePending(false);
    }
  };

  useEffect(() => {
    if (!isBuildEditorSurface) {
      return undefined;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.altKey || event.shiftKey) {
        return;
      }

      if (!(event.metaKey || event.ctrlKey) || event.key.toLowerCase() !== 's') {
        return;
      }

      event.preventDefault();
      if (canSaveWorkflow && !savePending) {
        void handleSaveDraft();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [
    canSaveWorkflow,
    handleSaveDraft,
    isBuildEditorSurface,
    savePending,
  ]);

  const handleStartExecution = async () => {
    const workflowName = activeWorkflowName.trim();
    const prompt = runPrompt.trim();
    const scopeId = resolvedStudioScopeId;
    if (!workflowName) {
      setRunNotice({
        type: 'error',
        message: 'Workflow name is required before starting a draft run.',
      });
      return;
    }

    if (!draftYaml.trim()) {
      setRunNotice({
        type: 'error',
        message: 'Workflow YAML is required before starting a draft run.',
      });
      return;
    }

    if (!prompt) {
      setRunNotice({
        type: 'error',
        message: 'Execution prompt is required before starting a draft run.',
      });
      return;
    }

    if (hasValidationError(activeWorkflowFindings)) {
      setRunNotice({
        type: 'error',
        message: 'Resolve Studio YAML validation errors before starting a draft run.',
      });
      return;
    }

    if (!scopeId) {
      setRunNotice({
        type: 'error',
        message: 'Resolve the current scope before starting a draft run.',
      });
      return;
    }

    setRunPending(true);
    setRunNotice(null);

    try {
      const workflowYamls = await buildWorkflowYamlBundle();
      const draftKey = saveScopeDraftRunPayload({
        bundleName: workflowName,
        bundleYamls: workflowYamls,
      });
      setPromptHistory(
        savePlaygroundPromptHistoryEntry({
          prompt,
          workflowName,
        }),
      );
      history.push(
        buildRuntimeRunsHref({
          scopeId,
          route: workflowName,
          prompt,
          draftKey,
          returnTo: currentStudioReturnTo || undefined,
        }),
      );
    } catch (error) {
      setRunNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to open the draft run console.',
      });
    } finally {
      setRunPending(false);
    }
  };

  const handlePopOutExecutionLogs = () => {
    if (!selectedExecutionId || typeof window === 'undefined') {
      return;
    }

    const url = new URL(window.location.href);
    url.searchParams.set('tab', 'executions');
    url.searchParams.set('execution', selectedExecutionId);
    url.searchParams.set('logs', 'popout');
    const nextUrl = `${url.pathname}${url.search}`;
    const existingWindow = executionLogsWindowRef.current;
    if (existingWindow && !existingWindow.closed) {
      existingWindow.location.replace(nextUrl);
      existingWindow.focus();
      setLogsDetached(true);
      return;
    }

    const popupWidth = Math.max(
      window.screen?.availWidth || window.innerWidth || 1440,
      1280,
    );
    const popupHeight = Math.max(
      window.screen?.availHeight || window.innerHeight || 960,
      720,
    );
    const popupFeatures = [
      'popup=yes',
      `width=${popupWidth}`,
      `height=${popupHeight}`,
      'left=0',
      'top=0',
      'resizable=yes',
      'scrollbars=yes',
    ].join(',');
    const nextWindow = window.open(
      nextUrl,
      'aevatar-console-execution-logs',
      popupFeatures,
    );

    if (!nextWindow) {
      setExecutionNotice({
        type: 'error',
        message: 'Allow pop-ups to open execution logs in a new window.',
      });
      return;
    }

    executionLogsWindowRef.current = nextWindow;
    setLogsDetached(true);
    nextWindow.focus();
  };

  useEffect(() => {
    if (logsPopoutMode === 'popout' || !logsDetached || typeof window === 'undefined') {
      return undefined;
    }

    const monitorId = window.setInterval(() => {
      const currentWindow = executionLogsWindowRef.current;
      if (currentWindow && !currentWindow.closed) {
        return;
      }

      executionLogsWindowRef.current = null;
      setLogsDetached(false);
      window.clearInterval(monitorId);
    }, 1000);

    return () => {
      window.clearInterval(monitorId);
    };
  }, [logsDetached, logsPopoutMode]);

  useEffect(() => {
    if (logsPopoutMode === 'popout' || !logsDetached || !selectedExecutionId || typeof window === 'undefined') {
      return;
    }

    const currentWindow = executionLogsWindowRef.current;
    if (!currentWindow || currentWindow.closed) {
      executionLogsWindowRef.current = null;
      setLogsDetached(false);
      return;
    }

    const url = new URL(window.location.href);
    url.searchParams.set('tab', 'executions');
    url.searchParams.set('execution', selectedExecutionId);
    url.searchParams.set('logs', 'popout');
    currentWindow.location.replace(`${url.pathname}${url.search}`);
  }, [logsDetached, logsPopoutMode, selectedExecutionId]);

  const handleExportDraft = async () => {
    const serializedYaml = draftYaml.trim() ? draftYaml : sourceYaml;
    const blob = new Blob([serializedYaml], { type: 'text/yaml' });
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = `${(draftWorkflowName || activeWorkflowName || 'workflow').trim() || 'workflow'}.yaml`;
    anchor.click();
    URL.revokeObjectURL(objectUrl);
  };

  const handleStopExecution = async () => {
    if (!selectedExecutionId || !executionCanStop) {
      return;
    }

    setExecutionStopPending(true);
    setExecutionNotice(null);
    try {
      const detail = await studioApi.stopExecution(selectedExecutionId, {
        reason: 'user requested stop',
      });
      queryClient.setQueryData(['studio-execution', selectedExecutionId], detail);
      queryClient.setQueryData(
        ['studio-executions'],
        (current: StudioExecutionSummary[] | undefined) =>
          (current ?? []).map((item) =>
            item.executionId === detail.executionId
              ? toExecutionSummary(detail)
              : item,
          ),
      );
      setExecutionNotice({
        type: 'info',
        message: 'Stop requested for the active Studio execution.',
      });
    } catch (error) {
      setExecutionNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to stop the Studio execution.',
      });
    } finally {
      setExecutionStopPending(false);
    }
  };

  const handleResumeExecution = async (
    interaction: {
      readonly kind: 'human_input' | 'human_approval';
      readonly runId: string;
      readonly stepId: string;
    },
    action: 'submit' | 'approve' | 'reject',
    userInput: string,
  ) => {
    if (!selectedExecutionId) {
      return;
    }

    setExecutionNotice(null);
    try {
      const detail = await studioApi.resumeExecution(selectedExecutionId, {
        runId: interaction.runId,
        stepId: interaction.stepId,
        approved: interaction.kind === 'human_input' ? true : action === 'approve',
        userInput: userInput.trim() || null,
        suspensionType: interaction.kind,
      });
      queryClient.setQueryData(['studio-execution', selectedExecutionId], detail);
      queryClient.setQueryData(
        ['studio-executions'],
        (current: StudioExecutionSummary[] | undefined) =>
          (current ?? []).map((item) =>
            item.executionId === detail.executionId
              ? toExecutionSummary(detail)
              : item,
          ),
      );
      setExecutionNotice({
        type: 'success',
        message:
          interaction.kind === 'human_approval'
            ? action === 'approve'
              ? 'Approval submitted for the active execution.'
              : 'Rejection submitted for the active execution.'
            : 'Input submitted for the active execution.',
      });
    } catch (error) {
      setExecutionNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to resume the Studio execution.',
      });
      throw error;
    }
  };

  const handleSetWorkflowDescription = async (value: string) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setSaveNotice({
        type: 'error',
        message: 'Load a workflow draft before editing the description.',
      });
      return;
    }

    try {
      const serialized = await studioApi.serializeYaml({
        document: {
          ...document,
          description: value.trim() || undefined,
        },
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      });
      setDraftYaml(serialized.yaml);
      setDraftWorkflowName(
        trimOptional(serialized.document.name) || draftWorkflowName || 'draft',
      );
      setSaveNotice(null);
      setRunNotice(null);
    } catch (error) {
      setSaveNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to update the workflow description.',
      });
    }
  };
  const handleInsertWorkflowStep = useCallback(
    async (stepType: string) => {
      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return;
      }

      const afterStepId = selectedGraphNodeId.startsWith('step:')
        ? selectedGraphNodeId.slice('step:'.length)
        : null;
      const result = insertStepByType(document, stepType, {
        afterStepId,
        targetRoleId: workflowRoleOptions[0]?.id || null,
      });

      await applySerializedWorkflowDocument(result.document, {
        selectedNodeId: result.nodeId,
      });
    },
    [
      applySerializedWorkflowDocument,
      resolveEditableWorkflowDocument,
      selectedGraphNodeId,
      workflowRoleOptions,
    ],
  );
  const handleApplyWorkflowStepDraft = useCallback(
    async (draft: StudioStepInspectorDraft) => {
      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return;
      }

      const currentStepId = selectedGraphNodeId.startsWith('step:')
        ? selectedGraphNodeId.slice('step:'.length)
        : '';
      if (!currentStepId) {
        setSaveNotice({
          type: 'error',
          message: 'Select a workflow step before applying changes.',
        });
        return;
      }

      const result = applyStepInspectorDraft(document, currentStepId, draft);
      await applySerializedWorkflowDocument(result.document, {
        selectedNodeId: result.nodeId,
      });
    },
    [
      applySerializedWorkflowDocument,
      resolveEditableWorkflowDocument,
      selectedGraphNodeId,
    ],
  );
  const handleRemoveWorkflowStep = useCallback(async () => {
    const document = await resolveEditableWorkflowDocument();
    if (!document) {
      return;
    }

    const currentStepId = selectedGraphNodeId.startsWith('step:')
      ? selectedGraphNodeId.slice('step:'.length)
      : '';
    if (!currentStepId) {
      setSaveNotice({
        type: 'error',
        message: 'Select a workflow step before removing it.',
      });
      return;
    }

    const result = removeStep(document, currentStepId);
    await applySerializedWorkflowDocument(result.document, {
      selectedNodeId: result.nodeId,
    });
  }, [
    applySerializedWorkflowDocument,
    resolveEditableWorkflowDocument,
    selectedGraphNodeId,
  ]);
  const handleAutoLayoutWorkflow = useCallback(() => {
    setDraftWorkflowLayout(null);
  }, []);
  const handleWorkflowNodeLayoutChange = useCallback(
    (nodes: Node[]) => {
      setDraftWorkflowLayout((current: unknown) =>
        buildStudioWorkflowLayout(
          activeWorkflowName.trim() || draftWorkflowName.trim() || 'draft',
          nodes as any,
          current ?? sourceWorkflowLayout ?? undefined,
        ),
      );
    },
    [activeWorkflowName, draftWorkflowName, sourceWorkflowLayout],
  );
  const handleWorkflowConnectNodes = useCallback(
    async (sourceNodeId: string, targetNodeId: string) => {
      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return;
      }

      const sourceStepId = sourceNodeId.startsWith('step:')
        ? sourceNodeId.slice('step:'.length)
        : '';
      const targetStepId = targetNodeId.startsWith('step:')
        ? targetNodeId.slice('step:'.length)
        : '';
      if (!sourceStepId || !targetStepId) {
        return;
      }

      const sourceStep =
        Array.isArray(document.steps)
          ? document.steps.find((step) => trimOptional(step.id) === sourceStepId)
          : null;
      const branchLabel = suggestBranchLabelForStep(
        trimOptional(sourceStep?.type),
        sourceStep?.branches ?? {},
      );
      const result = connectStepToTarget(
        document,
        sourceStepId,
        targetStepId,
        branchLabel,
      );
      await applySerializedWorkflowDocument(result.document, {
        selectedNodeId: result.nodeId,
      });
    },
    [applySerializedWorkflowDocument, resolveEditableWorkflowDocument],
  );
  const applyStudioTarget = React.useCallback(
    (nextStudioSurface: StudioSurface, nextBuildSurface?: BuildSurface) => {
      const resolvedBuildSurface = nextBuildSurface ?? buildSurface;
      if (nextStudioSurface === 'build' && resolvedBuildSurface === 'editor') {
        ensureActiveWorkflowDraftLoaded();
      }
      setBuildSurface(resolvedBuildSurface);
      setStudioSurface(nextStudioSurface);
    },
    [buildSurface, ensureActiveWorkflowDraftLoaded],
  );
  const handleBindingSelectionChange = useCallback(
    (selection: { serviceId: string; endpointId: string }) => {
      bindingSelectionRef.current = selection;
      if (
        !invokeSelectionRef.current.serviceId ||
        invokeSelectionRef.current.serviceId !== selection.serviceId
      ) {
        invokeSelectionRef.current = selection;
      }
    },
    [],
  );
  const handleRegisterScriptLeaveGuard = useCallback(
    (guard: (() => Promise<boolean>) | null) => {
      scriptLeaveGuardRef.current = guard;
    },
    [],
  );
  const confirmScriptsStudioLeave = useCallback(async () => {
    if (!isBuildScriptsSurface) {
      return true;
    }

    const leaveGuard = scriptLeaveGuardRef.current;
    return leaveGuard ? await leaveGuard() : true;
  }, [isBuildScriptsSurface]);
  const handleSelectBuildMode = useCallback(
    async (nextBuildMode: BuildMode) => {
      if (nextBuildMode === activeBuildMode) {
        return;
      }

      if (!(await confirmScriptsStudioLeave())) {
        return;
      }

      if (nextBuildMode === 'workflow') {
        applyStudioTarget('build', 'editor');
        return;
      }

      if (nextBuildMode === 'script') {
        if (!appContextQuery.data?.features.scripts) {
          return;
        }

        applyStudioTarget('build', 'scripts');
        return;
      }

      applyStudioTarget('build', 'gagent');
    },
    [
      activeBuildMode,
      appContextQuery.data?.features.scripts,
      applyStudioTarget,
      confirmScriptsStudioLeave,
    ],
  );
  const handleInvokeSelectionChange = useCallback(
    (selection: { serviceId: string; endpointId: string }) => {
      invokeSelectionRef.current = selection;
    },
    [],
  );
  const handleUseBindingEndpoint = useCallback(
    (serviceId: string, endpointId: string) => {
      bindingSelectionRef.current = {
        serviceId,
        endpointId,
      };
      invokeSelectionRef.current = {
        serviceId,
        endpointId,
      };
      applyStudioTarget('invoke');
    },
    [applyStudioTarget],
  );
  const handleSelectLifecycleStep = useCallback(
    async (stepKey: string) => {
      const normalizedStep = stepKey.trim().toLowerCase();
      const targetStudioSurface: StudioSurface =
        normalizedStep === 'observe'
          ? 'observe'
          : normalizedStep === 'bind'
            ? 'bind'
            : normalizedStep === 'invoke'
              ? 'invoke'
              : 'build';
      const isCurrentBuildSurface =
        targetStudioSurface === 'build' && studioSurface === 'build';
      if (isCurrentBuildSurface) {
        return;
      }
      if (!(await confirmScriptsStudioLeave())) {
        return;
      }

      if (stepKey === 'build') {
        applyStudioTarget('build', buildSurface);
        return;
      }

      if (stepKey === 'bind') {
        applyStudioTarget('bind');
        return;
      }

      if (stepKey === 'invoke') {
        applyStudioTarget('invoke');
        return;
      }

      if (stepKey === 'observe') {
        applyStudioTarget('observe');
      }
    },
    [
      applyStudioTarget,
      buildSurface,
      confirmScriptsStudioLeave,
      studioSurface,
    ],
  );

  const pageTitle =
    isBuildEditorSurface
      ? 'Workflow 构建'
      : isBuildScriptsSurface
        ? '脚本行为'
      : isBuildGAgentSurface
        ? 'GAgent 构建'
      : isBindSurface
        ? '成员绑定'
      : isInvokeSurface
        ? '成员调用'
      : isObserveSurface
        ? '测试运行'
        : '行为定义';
  const availableScopeScripts = useMemo(
    () =>
      (scopeScriptsQuery.data ?? []).filter(
        (detail): detail is ScopedScriptDetail =>
          Boolean(detail.available && detail.script),
      ),
    [scopeScriptsQuery.data],
  );
  const currentLifecycleStep =
    isBindSurface
      ? 'bind'
      : isInvokeSurface
        ? 'invoke'
        : isObserveSurface
          ? 'observe'
          : 'build';
  const currentMemberLabel =
    trimOptional(scopeBindingQuery.data?.displayName) ||
    trimOptional(activeWorkflowName) ||
    (isBuildScriptsSurface ? trimOptional(selectedScriptId) : '') ||
    'Current member';
  const currentMemberDescription =
    trimOptional(routeState.memberId) ||
    trimOptional(scopeBindingQuery.data?.serviceId) ||
    (activeBuildFocusKey
      ? activeBuildFocusKey.startsWith('script:')
        ? `Script ${trimOptional(selectedScriptId)}`
        : activeWorkflowName
          ? `Workflow ${activeWorkflowName}`
          : 'Studio is tracking the current member focus.'
      : 'Studio is tracking the current member focus.');
  const currentMemberKind: StudioShellMemberKind =
    isBuildScriptsSurface
      ? 'script'
      : isBuildGAgentSurface
        ? 'gagent'
      : currentScopeBindingRevision?.implementationKind === 'gagent'
        ? 'gagent'
        : currentScopeBindingRevision?.implementationKind === 'script'
          ? 'script'
          : currentScopeBindingRevision?.implementationKind === 'workflow'
            ? 'workflow'
            : selectedWorkflowId || templateWorkflow
              ? 'workflow'
              : 'member';
  const currentMemberTone: 'live' | 'draft' | 'idle' =
    currentScopeBindingRevision?.isActiveServing
      ? 'live'
      : activeBuildFocusKey
        ? 'draft'
        : 'idle';
  const currentMemberMeta = formatStudioAssetMeta({
    primary:
      isObserveSurface
        ? 'Recent run focus'
        : isBuildScriptsSurface
          ? 'Script behavior'
          : isBindSurface
            ? 'Binding focus'
            : isInvokeSurface
              ? 'Invoke focus'
              : 'Build focus',
    secondary:
      currentScopeBindingRevision?.revisionId ||
      trimOptional(routeState.memberId) ||
      activeBuildFocusKey,
  });
  const currentFocusMemberKey = useMemo(
    () =>
      buildStudioFocusKey({
        activeBuildFocusKey,
        routeMemberId: routeState.memberId,
        currentBindingRevisionId: currentScopeBindingRevision?.revisionId,
      }),
    [
      activeBuildFocusKey,
      currentScopeBindingRevision?.revisionId,
      routeState.memberId,
    ],
  );
  const handleSelectStudioMember = useCallback(
    async (memberKey: string) => {
      const normalizedMemberKey = trimOptional(memberKey);
      if (!normalizedMemberKey || normalizedMemberKey === currentFocusMemberKey) {
        return;
      }

      if (!(await confirmScriptsStudioLeave())) {
        return;
      }

      if (normalizedMemberKey.startsWith('workflow:')) {
        openWorkspaceWorkflow(normalizedMemberKey.slice('workflow:'.length));
        return;
      }

      if (normalizedMemberKey.startsWith('script:')) {
        openScopeScript(normalizedMemberKey.slice('script:'.length));
        return;
      }

      if (normalizedMemberKey.startsWith('template:')) {
        setSelectedWorkflowId('');
        setTemplateWorkflow(normalizedMemberKey.slice('template:'.length));
        setDraftMode('');
        setBuildSurface('editor');
        setStudioSurface('build');
        return;
      }

      if (
        normalizedMemberKey.startsWith('binding:') &&
        preferredScopeWorkflow?.workflowId
      ) {
        openWorkspaceWorkflow(preferredScopeWorkflow.workflowId);
      }
    },
    [
      confirmScriptsStudioLeave,
      currentFocusMemberKey,
      openScopeScript,
      openWorkspaceWorkflow,
      preferredScopeWorkflow?.workflowId,
    ],
  );
  const memberItems = useMemo(() => {
    const items: StudioShellMemberItem[] = [];
    const seen = new Set<string>();
    const currentMemberItem: StudioShellMemberItem = {
      key: currentFocusMemberKey,
      label: currentMemberLabel,
      description: currentMemberDescription,
      kind: currentMemberKind,
      meta: currentMemberMeta,
      tone: currentMemberTone,
    };

    const addItem = (item: StudioShellMemberItem | null) => {
      if (!item) {
        return;
      }

      const normalizedKey = trimOptional(item.key);
      if (!normalizedKey || seen.has(normalizedKey)) {
        return;
      }

      seen.add(normalizedKey);
      items.push({
        ...item,
        key: normalizedKey,
      });
    };

    if (preferredScopeWorkflow) {
      addItem({
        key: `workflow:${preferredScopeWorkflow.workflowId}`,
        label: preferredScopeWorkflow.name,
        description:
          trimOptional(preferredScopeWorkflow.description) ||
          'Scope-backed workflow draft ready for the current member context.',
        kind: 'workflow',
        meta: formatStudioAssetMeta({
          primary: `${preferredScopeWorkflow.stepCount} steps`,
          secondary:
            trimOptional(currentScopeBindingRevision?.revisionId) || 'Bound member',
        }),
        tone: currentScopeBindingRevision?.isActiveServing ? 'live' : 'draft',
      });
    }

    for (const workflow of visibleWorkflowSummaries.slice(0, 6)) {
      addItem({
        key: `workflow:${workflow.workflowId}`,
        label: workflow.name,
        description:
          trimOptional(workflow.description) ||
          trimOptional(workflow.fileName) ||
          'Workspace workflow draft',
        kind: 'workflow',
        meta: formatStudioAssetMeta({
          primary: `${workflow.stepCount} steps`,
          secondary: workflow.directoryLabel || workflow.fileName,
        }),
        tone:
          currentFocusMemberKey === `workflow:${workflow.workflowId}`
            ? 'live'
            : 'idle',
      });
    }

    for (const scriptDetail of availableScopeScripts.slice(0, 4)) {
      const scriptId = trimOptional(scriptDetail.script?.scriptId);
      if (!scriptId) {
        continue;
      }

      addItem({
        key: `script:${scriptId}`,
        label: scriptId,
        description:
          trimOptional(scriptDetail.script?.definitionActorId) ||
          'Scope-backed script behavior',
        kind: 'script',
        meta: formatStudioAssetMeta({
          primary: scriptDetail.script?.activeRevision || '',
          secondary: 'Scope script',
        }),
        tone:
          currentFocusMemberKey === `script:${scriptId}` ? 'live' : 'idle',
      });
    }

    if (!seen.has(trimOptional(currentMemberItem.key))) {
      addItem(currentMemberItem);
    }

    return items.slice(0, 8);
  }, [
    activeWorkflowName,
    availableScopeScripts,
    currentFocusMemberKey,
    currentScopeBindingRevision?.isActiveServing,
    currentScopeBindingRevision?.revisionId,
    currentMemberDescription,
    currentMemberKind,
    currentMemberLabel,
    currentMemberMeta,
    currentMemberTone,
    preferredScopeWorkflow,
    templateWorkflow,
    visibleWorkflowSummaries,
  ]);
  const lifecycleSteps = useMemo<readonly StudioLifecycleStep[]>(
    () => [
      {
        key: 'build',
        label: 'Build',
        description:
          'Edit the selected member implementation with workflow, script, or GAgent tools.',
        status: currentLifecycleStep === 'build' ? 'active' : 'available',
      },
      {
        key: 'bind',
        label: 'Bind',
        description:
          'Inspect published services, binding revisions, and serving state for the selected member.',
        status: currentLifecycleStep === 'bind' ? 'active' : 'available',
        disabled: !resolvedStudioScopeId,
      },
      {
        key: 'invoke',
        label: 'Invoke',
        description:
          'Invoke the selected member in-place and carry the trace forward into runtime runs.',
        status: currentLifecycleStep === 'invoke' ? 'active' : 'available',
        disabled: !resolvedStudioScopeId,
      },
      {
        key: 'observe',
        label: 'Observe',
        description:
          'Open execution traces and run posture for the selected member.',
        status: currentLifecycleStep === 'observe' ? 'active' : 'available',
      },
    ],
    [currentLifecycleStep, resolvedStudioScopeId],
  );
  const buildModeDefinitions = useMemo(
    () => getDefaultBuildModeCards(Boolean(appContextQuery.data?.features.scripts)),
    [appContextQuery.data?.features.scripts],
  );
  const buildModeCards = isBuildSurface ? (
    <div
      data-testid="studio-build-mode-switcher"
      style={{
        display: 'grid',
        gap: 4,
      }}
    >
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          gap: 8,
        }}
      >
        <div
          style={{
            color: '#8b7b63',
            fontSize: 10,
            fontWeight: 700,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
          }}
        >
          Construction Mode
        </div>
        <InlineInfoButton
          ariaLabel="Open construction mode help"
          content={
            <div style={{ display: 'grid', gap: 8 }}>
              <div>Build 阶段先确定当前 member 采用哪种实现方式，然后在同一块 workbench 里直接完成 authoring 和 dry-run。</div>
              {buildModeDefinitions.map((item) => (
                <div
                  key={item.key}
                  style={{
                    display: 'grid',
                    gap: 2,
                  }}
                >
                  <strong style={{ color: '#1f2937', fontSize: 12 }}>
                    {item.label}
                  </strong>
                  <span>{item.description}</span>
                  <span style={{ color: '#8b7b63', fontSize: 11 }}>
                    {item.hint}
                  </span>
                </div>
              ))}
            </div>
          }
        />
      </div>
      <div
        style={{
          display: 'inline-flex',
          gap: 4,
          width: '100%',
        }}
      >
        {buildModeDefinitions.map((item) => {
          const active = activeBuildMode === item.key;

          return (
            <button
              key={item.key}
              type="button"
              aria-pressed={active}
              disabled={item.disabled}
              onClick={() => void handleSelectBuildMode(item.key)}
              style={{
                alignItems: 'center',
                background: active ? '#eef4ff' : '#faf7f0',
                border: active ? '1px solid #6b8cff' : '1px solid #eadfcd',
                borderRadius: 999,
                color: active ? '#2f54eb' : '#1f2937',
                cursor: item.disabled ? 'not-allowed' : 'pointer',
                display: 'inline-flex',
                flex: 1,
                fontSize: 11,
                fontWeight: 700,
                height: 28,
                justifyContent: 'center',
                minWidth: 0,
                opacity: item.disabled ? 0.58 : 1,
                padding: '0 10px',
                transition:
                  'border-color 0.18s ease, background-color 0.18s ease, color 0.18s ease',
              }}
            >
              {item.label}
            </button>
          );
        })}
      </div>
    </div>
  ) : null;

  const selectedExecutionSummary =
    selectedExecutionQuery.data ??
    executionsQuery.data?.find(
      (item) => item.executionId === selectedExecutionId,
    ) ??
    null;
  const latestExecutionSummary = executionsQuery.data?.[0] ?? null;
  const activeExecutionSummary =
    selectedExecutionSummary ?? latestExecutionSummary;
  const studioContextPrimaryTitle =
    isBuildEditorSurface
      ? activeWorkflowName || templateWorkflow || 'Workflow 构建'
      : isBuildGAgentSurface
        ? scopeBindingQuery.data?.displayName || 'GAgent 构建'
      : isBuildScriptsSurface
        ? selectedScriptId || 'Script 构建'
      : isObserveSurface
        ? activeWorkflowName || templateWorkflow || '测试运行'
        : isBindSurface
          ? scopeBindingQuery.data?.displayName || '成员绑定'
          : isInvokeSurface
            ? scopeBindingQuery.data?.displayName || '成员调用'
            : pageTitle;
  const studioContextDescriptor =
    isBuildEditorSurface
      ? '围绕当前 member 的 workflow canvas、step detail 和 dry-run 继续构建'
      : isBuildGAgentSurface
        ? '在 Build 内定义 GAgent 类型、角色、初始 prompt、工具和状态持久化'
      : isBuildScriptsSurface
        ? '围绕 script source、diagnostics 和 dry-run 继续迭代当前 member'
      : isObserveSurface
        ? '测试运行'
        : isBindSurface
          ? '查看绑定版本、运行态入口与 serving 状态'
          : isInvokeSurface
            ? '调用当前成员并保留运行观察上下文'
            : '成员工作台';
  const studioTeamLabel =
    trimOptional(resolvedStudioScopeId) || 'Local draft workspace';
  const studioBoundServiceLabel =
    trimOptional(routeState.memberId) ||
    trimOptional(scopeBindingQuery.data?.serviceId) ||
    'No bound service';
  const studioBindingLabel =
    trimOptional(currentScopeBindingRevision?.revisionId) ||
    (resolvedStudioScopeId ? 'Awaiting binding revision' : 'Scope not resolved');
  const studioBindingTone: StudioContextBadgeTone =
    currentScopeBindingRevision?.isActiveServing
      ? 'success'
      : currentScopeBindingRevision?.revisionId
        ? 'warning'
        : 'default';
  const studioHealthLabel =
    trimOptional(scopeBindingQuery.data?.deploymentStatus) ||
    trimOptional(currentScopeBindingRevision?.status) ||
    'Draft only';
  const studioHealthTone = resolveExecutionTone(
    scopeBindingQuery.data?.deploymentStatus ||
      currentScopeBindingRevision?.status,
  );
  const studioLastRunLabel = activeExecutionSummary?.startedAtUtc
    ? formatCompactDateTime(activeExecutionSummary.startedAtUtc)
    : 'No recent run';
  const studioObservationLabel =
    trimOptional(activeExecutionSummary?.status) || 'No active execution';
  const studioObservationTone = resolveExecutionTone(
    activeExecutionSummary?.status,
  );
  const studioRuntimeBadgeStyle = resolveStudioContextBadgeStyle(
    studioObservationTone,
  );
  const studioObservationNote = activeExecutionSummary
    ? [
        trimOptional(activeExecutionSummary.workflowName) || currentMemberLabel,
        trimOptional(activeExecutionSummary.actorId),
      ]
        .filter(Boolean)
        .join(' · ')
    : 'Run a member through Invoke or Observe to keep runtime posture visible here.';
  const studioContextMetaParts = [
    studioContextDescriptor,
    studioBoundServiceLabel,
  ]
    .map((value) => trimOptional(value))
    .filter(Boolean);
  const studioReturnHref = resolvedStudioScopeId
    ? buildTeamDetailHref({
        scopeId: resolvedStudioScopeId,
        tab: 'advanced',
        serviceId: scopeBindingQuery.data?.serviceId || undefined,
      })
    : buildTeamsHref();
  const studioReturnLabel = '返回团队';
  const currentStudioReturnTo =
    typeof window === 'undefined'
      ? ''
      : sanitizeReturnTo(
          `${window.location.pathname}${window.location.search}${window.location.hash}`,
        );
  const studioRuntimeRunsHref = buildRuntimeRunsHref({
    route:
      trimOptional(activeWorkflowName) ||
      trimOptional(currentScopeBindingRevision?.workflowName) ||
      undefined,
    scopeId: resolvedStudioScopeId || undefined,
    serviceId: scopeBindingQuery.data?.serviceId || undefined,
    prompt: trimOptional(runPrompt) || undefined,
    returnTo: currentStudioReturnTo || undefined,
  });
  const studioContextBadges: readonly {
    readonly label: string;
    readonly value: string;
    readonly tone: StudioContextBadgeTone;
  }[] = [
    {
      label: 'Type',
      value: formatStudioMemberKindLabel(currentMemberKind),
      tone: 'default',
    },
    {
      label: 'Binding',
      value: studioBindingLabel,
      tone: studioBindingTone,
    },
    {
      label: 'Health',
      value: studioHealthLabel,
      tone: studioHealthTone,
    },
  ];
  const studioPrimaryAction =
    isObserveSurface
      ? {
          disabled: !canRunWorkflow || runPending,
          label: '重新运行',
          loading: runPending,
          onClick: () => void handleStartExecution(),
          type: 'primary' as const,
        }
      : isBindSurface
        ? {
            disabled: !resolvedStudioScopeId,
            label: '进入调用',
            loading: false,
            onClick: () => applyStudioTarget('invoke'),
            type: 'primary' as const,
          }
        : isInvokeSurface
          ? {
              disabled: !resolvedStudioScopeId,
              label: '查看绑定',
              loading: false,
              onClick: () => applyStudioTarget('bind'),
              type: 'default' as const,
            }
          : resolvedStudioScopeId
            ? {
                disabled: false,
                label: '进入绑定',
                loading: false,
                onClick: () => applyStudioTarget('bind'),
                type: 'default' as const,
              }
            : null;
  const studioRailCardStyle: React.CSSProperties = {
    background: 'rgba(255, 255, 255, 0.96)',
    border: '1px solid #ece3d5',
    borderRadius: 14,
    display: 'grid',
    gap: 6,
    padding: '10px 11px',
  };
  const studioRailLabelStyle: React.CSSProperties = {
    color: '#7b6e5a',
    fontSize: 10,
    fontWeight: 700,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
  };
  const studioRailValueStyle: React.CSSProperties = {
    color: '#16120d',
    fontSize: 11.5,
    fontWeight: 700,
    lineHeight: '17px',
  };
  const studioRailMetaStyle: React.CSSProperties = {
    color: '#6f6250',
    fontSize: 10.5,
    lineHeight: '16px',
  };
  const studioRailFooter = (
    <>
      <div style={studioRailCardStyle}>
        <div style={studioRailLabelStyle}>Current team</div>
        <div style={studioRailValueStyle}>{studioTeamLabel}</div>
        <div style={studioRailMetaStyle}>
          {studioBoundServiceLabel}
          {currentScopeBindingRevision?.revisionId
            ? ` · ${currentScopeBindingRevision.revisionId}`
            : ''}
        </div>
      </div>
      <div style={studioRailCardStyle}>
        <div style={studioRailLabelStyle}>Observe posture</div>
        <div
          style={{
            ...studioRailValueStyle,
            color: resolveStudioContextBadgeStyle(studioObservationTone).color,
          }}
        >
          {studioObservationLabel}
        </div>
        <div style={studioRailMetaStyle}>{studioObservationNote}</div>
      </div>
    </>
  );
  const studioContextBar = (
    <div
      data-testid="studio-context-bar"
      style={{
        background: 'rgba(255, 252, 247, 0.98)',
        borderBottom: '1px solid rgba(229, 220, 203, 0.88)',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 12,
        justifyContent: 'space-between',
        padding: '10px 16px',
      }}
    >
      <div
        style={{
          display: 'grid',
          flex: '1 1 540px',
          gap: 6,
          minWidth: 0,
        }}
      >
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 10,
          }}
        >
          <button
            aria-label={studioReturnLabel}
            type="button"
            onClick={() => history.push(studioReturnHref)}
            style={{
              alignItems: 'center',
              background: 'transparent',
              border: 'none',
              color: '#2452b5',
              cursor: 'pointer',
              display: 'inline-flex',
              flexShrink: 0,
              fontSize: 11,
              fontWeight: 700,
              gap: 4,
              letterSpacing: '0.02em',
              padding: 0,
            }}
          >
            ← {studioReturnLabel}
          </button>
          <div
            style={{
              color: '#8b7b63',
              fontSize: 10,
              fontWeight: 700,
              letterSpacing: '0.08em',
              textTransform: 'uppercase',
            }}
          >
            Aevatar Studio
          </div>
          <div
            data-testid="studio-context-title"
            style={{
              color: '#1d2129',
              fontSize: 17,
              fontWeight: 700,
              letterSpacing: '-0.02em',
              lineHeight: '22px',
              minWidth: 0,
            }}
          >
            {studioContextPrimaryTitle}
          </div>
        </div>
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 6,
            minWidth: 0,
          }}
        >
          {studioContextMetaParts.length > 0 ? (
            <div
              data-testid="studio-context-meta"
              style={{
                color: '#6f6250',
                fontSize: 11,
                lineHeight: '17px',
                minWidth: 0,
              }}
            >
              {studioContextMetaParts.join(' · ')}
            </div>
          ) : null}
          {studioContextBadges.map((badge) => {
            const badgeStyle = resolveStudioContextBadgeStyle(badge.tone);

            return (
              <span
                key={badge.label}
                style={{
                  alignItems: 'center',
                  background: badgeStyle.background,
                  border: `1px solid ${badgeStyle.borderColor}`,
                  borderRadius: 999,
                  color: badgeStyle.color,
                  display: 'inline-flex',
                  fontSize: 10,
                  gap: 5,
                  lineHeight: '15px',
                  minHeight: 22,
                  padding: '0 7px',
                }}
              >
                <span
                  style={{
                    fontWeight: 700,
                  }}
                >
                  {badge.label}
                </span>
                <span>{badge.value}</span>
              </span>
            );
          })}
        </div>
      </div>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flex: '0 1 auto',
          flexWrap: 'wrap',
          gap: 8,
          justifyContent: 'flex-end',
        }}
      >
        <span
          style={{
            alignItems: 'center',
            background: studioRuntimeBadgeStyle.background,
            border: `1px solid ${studioRuntimeBadgeStyle.borderColor}`,
            borderRadius: 999,
            color: studioRuntimeBadgeStyle.color,
            display: 'inline-flex',
            fontSize: 10.5,
            gap: 5,
            lineHeight: '16px',
            minHeight: 24,
            padding: '0 9px',
          }}
        >
          <span
            style={{
              fontWeight: 700,
            }}
          >
            Runtime
          </span>
          <span>{studioObservationLabel}</span>
        </span>
        {activeExecutionSummary?.startedAtUtc ? (
          <span
            style={{
              color: '#8b7b63',
              fontSize: 10.5,
              lineHeight: '16px',
            }}
          >
            {studioLastRunLabel}
          </span>
        ) : null}
        <Button onClick={() => history.push(studioRuntimeRunsHref)}>
          Runtime Runs
        </Button>
        {studioPrimaryAction ? (
          <Button
            disabled={studioPrimaryAction.disabled}
            loading={studioPrimaryAction.loading}
            onClick={studioPrimaryAction.onClick}
            type={studioPrimaryAction.type}
          >
            {studioPrimaryAction.label}
          </Button>
        ) : null}
      </div>
    </div>
  );

  const workflowBuildContent = (
    <StudioWorkflowBuildPanel
      draftYaml={draftYaml}
      onSetDraftYaml={(value) => {
        setDraftYaml(value);
        setEditableWorkflowDocument(null);
        setSaveNotice(null);
      }}
      onSaveDraft={() => void handleSaveDraft()}
      savePending={savePending}
      canSaveWorkflow={canSaveWorkflow}
      saveNotice={saveNotice}
      workflowGraph={workflowGraph}
      selectedGraphNodeId={selectedGraphNodeId}
      onSelectGraphNode={setSelectedGraphNodeId}
      runtimePrimitives={runtimePrimitivesQuery.data ?? []}
      scopeId={resolvedStudioScopeId || undefined}
      workflowName={activeWorkflowName || draftWorkflowName || templateWorkflow || 'workflow'}
      runPrompt={runPrompt}
      onRunPromptChange={applyRunPrompt}
      buildWorkflowYamls={buildWorkflowYamlBundle}
      runMetadata={workflowDryRunHeaders}
      dryRunRouteLabel={workflowDryRunRouteLabel}
      dryRunModelLabel={effectiveWorkflowDryRunModel || undefined}
      dryRunBlockedReason={workflowDryRunBlockedReason || undefined}
      onOpenRunSetup={() => history.push('/chat')}
      availableStepTypes={availableStepTypes}
      workflowRoles={workflowRoleOptions}
      onInsertStep={handleInsertWorkflowStep}
      onApplyStepDraft={handleApplyWorkflowStepDraft}
      onRemoveSelectedStep={handleRemoveWorkflowStep}
      onAutoLayout={handleAutoLayoutWorkflow}
      onConnectNodes={handleWorkflowConnectNodes}
      onNodeLayoutChange={handleWorkflowNodeLayoutChange}
      onContinueToBind={() => applyStudioTarget('bind')}
    />
  );

  const scriptBuildContent = appContextQuery.data?.features.scripts ? (
    <StudioScriptBuildPanel
      scopeId={resolvedStudioScopeId || undefined}
      scriptsQuery={scopeScriptsQuery}
      selectedScriptId={selectedScriptId}
      onSelectScriptId={setSelectedScriptId}
      onRefreshScripts={() => scopeScriptsQuery.refetch()}
      onContinueToBind={() => applyStudioTarget('bind')}
      onRegisterLeaveGuard={handleRegisterScriptLeaveGuard}
    />
  ) : (
    <div
      style={{
        ...embeddedPanelStyle,
        background: 'rgba(255, 251, 230, 0.96)',
        borderColor: 'rgba(250, 173, 20, 0.28)',
      }}
    >
      <div
        style={{
          display: 'flex',
          flexDirection: 'column',
          gap: 4,
        }}
      >
        <strong>当前环境暂不支持脚本行为</strong>
      </div>
    </div>
  );

  const gAgentBuildContent = (
    <StudioGAgentBuildPanel
      scopeId={resolvedStudioScopeId || undefined}
      currentMemberLabel={
        trimOptional(scopeBindingQuery.data?.displayName) ||
        trimOptional(routeState.memberId) ||
        'Current member'
      }
      gAgentTypes={gAgentTypesQuery.data ?? []}
      gAgentTypesLoading={gAgentTypesQuery.isLoading}
      gAgentTypesError={gAgentTypesQuery.isError ? gAgentTypesQuery.error : null}
      selectedGAgentTypeName={selectedGAgentTypeName}
      onSelectGAgentTypeName={setSelectedGAgentTypeName}
      onContinueToBind={() => applyStudioTarget('bind')}
    />
  );

  const buildPageContent = isBuildSurface ? (
    <div
      style={{
        display: 'flex',
        flex: 1,
        flexDirection: 'column',
        gap: 16,
        minHeight: 0,
        minWidth: 0,
      }}
    >
      {buildModeCards}
      <div
        style={{
          display: 'flex',
          flex: 1,
          flexDirection: 'column',
          minHeight: 0,
          minWidth: 0,
          overflow: 'auto',
        }}
      >
        {activeBuildMode === 'workflow'
          ? workflowBuildContent
          : activeBuildMode === 'script'
            ? scriptBuildContent
            : gAgentBuildContent}
      </div>
    </div>
  ) : null;

  const currentPageContent =
    isBuildSurface ? (
      buildPageContent
    ) : isObserveSurface ? (
      <StudioExecutionPage
        executions={executionsQuery}
        selectedExecution={selectedExecutionQuery}
        workflowGraph={workflowGraph}
        draftWorkflowName={draftWorkflowName}
        activeWorkflowName={activeWorkflowName}
        activeWorkflowDescription={activeWorkflowDescription}
        activeDirectoryLabel={activeDirectoryLabel}
        savePending={savePending}
        canSaveWorkflow={canSaveWorkflow}
        runPending={runPending}
        canOpenRunWorkflow={canOpenRunWorkflow}
        canRunWorkflow={canRunWorkflow}
        executionCanStop={executionCanStop}
        executionStopPending={executionStopPending}
        runPrompt={runPrompt}
        executionNotice={executionNotice}
        logsPopoutMode={logsPopoutMode === 'popout'}
        logsDetached={logsDetached}
        onOpenExecution={openExecution}
        onSaveDraft={() => void handleSaveDraft()}
        onExportDraft={() => void handleExportDraft()}
        onSetDraftWorkflowName={setDraftWorkflowName}
        onSetWorkflowDescription={(value) =>
          void handleSetWorkflowDescription(value)
        }
        onRunPromptChange={setRunPrompt}
        onStartExecution={() => void handleStartExecution()}
        onResumeExecution={handleResumeExecution}
        onStopExecution={() => void handleStopExecution()}
        onPopOutLogs={handlePopOutExecutionLogs}
      />
    ) : isBindSurface ? (
      <div
        style={{
          display: 'flex',
          flex: 1,
          flexDirection: 'column',
          height: '100%',
          minHeight: 0,
          overflow: 'hidden',
        }}
      >
        <StudioMemberBindPanel
          authSession={authSessionQuery.data}
          initialEndpointId={bindingSelectionRef.current.endpointId}
          initialServiceId={bindingSelectionRef.current.serviceId}
          onContinueToInvoke={handleUseBindingEndpoint}
          onSelectionChange={handleBindingSelectionChange}
          preferredServiceId={
            scopeBindingQuery.data?.available
              ? scopeBindingQuery.data.serviceId
              : ''
          }
          scopeBinding={scopeBindingQuery.data}
          scopeId={resolvedStudioScopeId}
          services={publishedScopeServices}
        />
      </div>
    ) : isInvokeSurface ? (
      <StudioMemberInvokePanel
        onSelectionChange={handleInvokeSelectionChange}
        returnTo={currentStudioReturnTo || undefined}
        scopeBinding={scopeBindingQuery.data}
        scopeId={resolvedStudioScopeId}
        initialEndpointId={
          invokeSelectionRef.current.endpointId ||
          bindingSelectionRef.current.endpointId
        }
        initialServiceId={
          invokeSelectionRef.current.serviceId ||
          bindingSelectionRef.current.serviceId
        }
        services={runtimeConsoleServices}
      />
    ) : null;

  const pageContainerTitle =
    logsPopoutMode === 'popout' ? 'Execution logs' : undefined;

  return (
    <PageContainer
      childrenContentStyle={{
        margin: 0,
        minHeight: '100%',
        padding: 0,
      }}
      pageHeaderRender={false}
      style={{ minHeight: '100%' }}
      title={pageContainerTitle}
    >
      <StudioBootstrapGate
        appContextLoading={appContextQuery.isLoading}
        appContextError={appContextQuery.isError ? appContextQuery.error : null}
        authLoading={authSessionQuery.isLoading || authRecoveryPending}
        authError={authSessionQuery.isError ? authSessionQuery.error : null}
        workspaceLoading={workspaceSettingsQuery.isLoading}
        workspaceError={
          workspaceSettingsQuery.isError ? workspaceSettingsQuery.error : null
        }
      >
        {logsPopoutMode === 'popout' ? (
          currentPageContent
        ) : (
          <StudioShell
            contentOverflow="auto"
            contextBar={studioContextBar}
            currentLifecycleStep={currentLifecycleStep}
            lifecycleSteps={lifecycleSteps}
            members={memberItems}
            onSelectLifecycleStep={handleSelectLifecycleStep}
            onSelectMember={handleSelectStudioMember}
            pageTitle={pageTitle}
            railFooter={studioRailFooter}
            selectedMemberKey={currentFocusMemberKey}
            showPageHeader={false}
          >
            {currentPageContent}
          </StudioShell>
        )}
      </StudioBootstrapGate>
    </PageContainer>
  );
};

export default StudioPage;
