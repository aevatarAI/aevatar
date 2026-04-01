import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  ReactFlow,
  Controls,
  Background,
  BackgroundVariant,
  ConnectionLineType,
  Handle,
  MarkerType,
  MiniMap,
  Position,
  applyEdgeChanges,
  applyNodeChanges,
  useStore,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import {
  AlertCircle,
  ArrowRightLeft,
  Bot,
  Boxes,
  Check,
  CircleHelp,
  ChevronDown,
  ChevronLeft,
  Code2,
  Copy,
  Database,
  FileText,
  FolderOpen,
  FolderPlus,
  GitBranch,
  Globe,
  Loader2,
  LogOut,
  Moon,
  Maximize2,
  Palette,
  Play,
  Plus,
  Search,
  Settings,
  Shield,
  Square,
  Sun,
  Trash2,
  Upload,
  User,
  Workflow as WorkflowIcon,
  Wrench,
  X,
} from 'lucide-react';
import * as api from './api';
import ScriptsStudio from './ScriptsStudio';
import * as nyxid from './auth/nyxid';
import ScopePage from './runtime/ScopePage';
import GAgentPage from './runtime/GAgentPage';
import ConfigExplorerPage from './config-explorer/ConfigExplorerPage';
import {
  PRIMITIVE_CATEGORIES,
  applyConnectorDefaults,
  buildExecutionTrace,
  buildGraphFromWorkflow,
  buildLayoutDocument,
  buildWorkflowDocument,
  createDefaultRoles,
  createEdge,
  createEmptyWorkflowMeta,
  createNode,
  createRoleState,
  createUniqueConnectorName,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  formatParameterValue,
  getCategoryForType,
  getDefaultParametersForType,
  getNextConditionalBranchLabel,
  parseParameterInput,
  splitLines,
  supportsRole,
  toConnectorPayload,
  toConnectorState,
  toRolePayload,
  toRoleState,
  toFindingLevel,
  type ConnectorState,
  type ExecutionLogItem,
  type ExecutionInteractionState,
  type ExecutionTrace,
  type RightPanelTab,
  type RoleState,
  type StudioEdgeData,
  type StudioNodeData,
  type StudioView,
  type ValidationFinding,
  type WorkflowMetaState,
} from './studio';
import './index.css';

const CATEGORY_ICONS = {
  data: Database,
  control: GitBranch,
  ai: Bot,
  composition: Boxes,
  integration: ArrowRightLeft,
  human: User,
  validation: Shield,
  custom: Code2,
};

const nodeTypes: Record<string, any> = {
  aevatarNode: WorkflowNodeCard,
};

type WorkflowSummary = {
  workflowId: string;
  name: string;
  description: string;
  fileName: string;
  filePath: string;
  directoryId: string;
  directoryLabel: string;
  stepCount: number;
  hasLayout: boolean;
  updatedAtUtc: string;
};

type DirectorySummary = {
  directoryId: string;
  label: string;
  path: string;
  isBuiltIn: boolean;
};

type WorkspacePage = 'studio' | 'scripts' | 'gagents' | 'explorer' | 'console' | 'settings';
type NonSettingsWorkspacePage = Exclude<WorkspacePage, 'settings'>;
type SettingsSection = 'runtime' | 'cloud-config' | 'skills' | 'appearance';
type RoleModalTarget = 'catalog' | 'workflow';
type WorkflowStorageMode = 'workspace' | 'scope';
type ScriptStorageMode = 'draft' | 'scope';
type AppHostMode = 'embedded' | 'proxy';

const WORKSPACE_PAGE_VALUES: WorkspacePage[] = ['studio', 'scripts', 'gagents', 'explorer', 'console', 'settings'];
const NON_SETTINGS_WORKSPACE_PAGE_VALUES: NonSettingsWorkspacePage[] = ['studio', 'scripts', 'gagents', 'explorer'];

type AppContextState = {
  hostMode: AppHostMode;
  scopeId: string | null;
  scopeResolved: boolean;
  scopeSource: string;
  workflowStorageMode: WorkflowStorageMode;
  scriptStorageMode: ScriptStorageMode;
  scriptsEnabled: boolean;
  scriptContract: {
    inputType: string;
    readModelFields: string[];
  };
};

type AuthSessionState = {
  loading: boolean;
  enabled: boolean;
  authenticated: boolean;
  providerDisplayName: string;
  loginUrl: string;
  logoutUrl: string;
  name: string;
  email: string;
  picture: string;
  errorMessage: string;
};

type ProviderTypeOption = {
  id: string;
  displayName: string;
  category: string;
  description: string;
  recommended: boolean;
  defaultEndpoint: string;
  defaultModel: string;
};

type ProviderDraft = {
  key: string;
  providerName: string;
  providerType: string;
  displayName: string;
  category: string;
  description: string;
  endpoint: string;
  model: string;
  apiKey: string;
  apiKeyConfigured: boolean;
};

type RuntimeMode = 'remote' | 'local';
type UserLlmRoute = 'auto' | 'gateway' | string;

type UserConfigProviderStatus = {
  provider_slug: string;
  provider_name: string;
  status: string;
  source?: string;
};

type UserConfigState = {
  defaultModel: string;
  preferredLlmRoute: UserLlmRoute;
  loading: boolean;
  providers: UserConfigProviderStatus[];
  supportedModels: string[];
  modelsByProvider: Record<string, string[]>;
  modelsLoading: boolean;
};

type StudioSettingsState = {
  remoteRuntimeUrl: string;
  localRuntimeUrl: string;
  runtimeMode: RuntimeMode;
  ornnBaseUrl: string;
  appearanceTheme: string;
  colorMode: 'light' | 'dark';
  secretsFilePath: string;
  defaultProviderName: string;
  providerTypes: ProviderTypeOption[];
  providers: ProviderDraft[];
};

type AppearanceOption = {
  id: string;
  label: string;
  description: string;
  swatches: string[];
};

const APPEARANCE_OPTIONS: AppearanceOption[] = [
  {
    id: 'blue',
    label: 'Blue',
    description: 'Cool and calm for daily editing.',
    swatches: ['#2F6FEC', '#7FA9FF', '#EAF2FF'],
  },
  {
    id: 'coral',
    label: 'Coral',
    description: 'The warmer look you had before.',
    swatches: ['#F0483E', '#FF8A6B', '#FFF1EC'],
  },
  {
    id: 'forest',
    label: 'Forest',
    description: 'A softer green studio palette.',
    swatches: ['#2F8F6A', '#6BBC94', '#EAF7F0'],
  },
];

const DEFAULT_REMOTE_RUNTIME_URL = 'https://aevatar-console-backend-api.aevatar.ai';
const DEFAULT_LOCAL_RUNTIME_URL = 'http://127.0.0.1:5080';
const DEFAULT_RUNTIME_BASE_URL = DEFAULT_LOCAL_RUNTIME_URL;
const USER_LLM_ROUTE_AUTO = 'auto';
const USER_LLM_ROUTE_GATEWAY = 'gateway';
const USER_CONFIG_PROVIDER_SOURCE_GATEWAY = 'gateway_provider';
const USER_CONFIG_PROVIDER_SOURCE_SERVICE = 'user_service';
const WORKSPACE_PAGE_STORAGE_KEY = 'aevatar.app.workspace-page';
const PREVIOUS_WORKSPACE_PAGE_STORAGE_KEY = 'aevatar.app.previous-workspace-page';

function normalizeRuntimeMode(value: unknown): RuntimeMode {
  return String(value || '').trim().toLowerCase() === 'remote' ? 'remote' : 'local';
}

function normalizeRuntimeUrl(value: unknown, fallback: string) {
  const normalized = String(value || '').trim();
  return (normalized || fallback).replace(/\/+$/, '');
}

function normalizeUserLlmRoute(value: unknown): UserLlmRoute {
  const normalized = String(value || '').trim().toLowerCase();
  return normalized || USER_LLM_ROUTE_AUTO;
}

function resolveUserRuntimeConfig(userConfigData?: any) {
  const legacyRuntimeUrl = String(userConfigData?.runtimeBaseUrl || '').trim().replace(/\/+$/, '');
  const hasExplicitRuntimeConfig = Boolean(
    userConfigData?.runtimeMode || userConfigData?.localRuntimeBaseUrl || userConfigData?.remoteRuntimeBaseUrl,
  );
  if (!hasExplicitRuntimeConfig && legacyRuntimeUrl) {
    const isLocalRuntime = /^https?:\/\/(localhost|127\.0\.0\.1|0\.0\.0\.0)(:\d+)?/i.test(legacyRuntimeUrl);
    return {
      runtimeMode: isLocalRuntime ? 'local' as const : 'remote' as const,
      localRuntimeUrl: isLocalRuntime ? legacyRuntimeUrl : DEFAULT_LOCAL_RUNTIME_URL,
      remoteRuntimeUrl: isLocalRuntime ? DEFAULT_REMOTE_RUNTIME_URL : legacyRuntimeUrl,
      activeRuntimeUrl: legacyRuntimeUrl,
    };
  }

  const runtimeMode = normalizeRuntimeMode(userConfigData?.runtimeMode);
  const localRuntimeUrl = normalizeRuntimeUrl(userConfigData?.localRuntimeBaseUrl, DEFAULT_LOCAL_RUNTIME_URL);
  const remoteRuntimeUrl = normalizeRuntimeUrl(userConfigData?.remoteRuntimeBaseUrl, DEFAULT_REMOTE_RUNTIME_URL);
  return {
    runtimeMode,
    localRuntimeUrl,
    remoteRuntimeUrl,
    activeRuntimeUrl: runtimeMode === 'remote' ? remoteRuntimeUrl : localRuntimeUrl,
  };
}

function getActiveRuntimeUrl(runtime: Pick<StudioSettingsState, 'runtimeMode' | 'localRuntimeUrl' | 'remoteRuntimeUrl'>) {
  return runtime.runtimeMode === 'remote'
    ? normalizeRuntimeUrl(runtime.remoteRuntimeUrl, DEFAULT_REMOTE_RUNTIME_URL)
    : normalizeRuntimeUrl(runtime.localRuntimeUrl, DEFAULT_LOCAL_RUNTIME_URL);
}

function createEmptyAppContext(): AppContextState {
  return {
    hostMode: 'embedded',
    scopeId: null,
    scopeResolved: false,
    scopeSource: '',
    workflowStorageMode: 'workspace',
    scriptStorageMode: 'draft',
    scriptsEnabled: false,
    scriptContract: {
      inputType: '',
      readModelFields: [],
    },
  };
}

function resolveAppContextState(context: any): AppContextState {
  const resolvedScopeId = context?.scopeResolved && context?.scopeId ? context.scopeId : null;
  return {
    hostMode: context?.mode === 'proxy' ? 'proxy' : 'embedded',
    scopeId: resolvedScopeId,
    scopeResolved: Boolean(resolvedScopeId),
    scopeSource: context?.scopeSource || '',
    workflowStorageMode: context?.workflowStorageMode === 'scope' ? 'scope' : 'workspace',
    scriptStorageMode: context?.scriptStorageMode === 'scope' ? 'scope' : 'draft',
    scriptsEnabled: Boolean(context?.features?.scripts),
    scriptContract: {
      inputType: context?.scriptContract?.inputType || '',
      readModelFields: Array.isArray(context?.scriptContract?.readModelFields) ? context.scriptContract.readModelFields : [],
    },
  };
}

function isAuthResponseInvalid(error: any) {
  return Boolean(
    error?.status === 401 ||
    error?.loginUrl ||
    (typeof error?.message === 'string' && error.message.includes('Sign-in may be required.')) ||
    (typeof error?.rawBody === 'string' &&
      (error.rawBody.startsWith('<!DOCTYPE') || error.rawBody.startsWith('<html'))),
  );
}

function summarizeBootstrapFailures(labels: string[]) {
  if (labels.length === 0) {
    return '';
  }

  const visibleLabels = labels.slice(0, 3);
  const suffix = labels.length > visibleLabels.length
    ? `, +${labels.length - visibleLabels.length} more`
    : '';
  return `Loaded studio with defaults for ${visibleLabels.join(', ')}${suffix}.`;
}

type ExecutionLogsWindowState = {
  isPopout: boolean;
  executionId: string | null;
};

function readExecutionLogsWindowState(): ExecutionLogsWindowState {
  if (typeof window === 'undefined') {
    return {
      isPopout: false,
      executionId: null,
    };
  }

  const url = new URL(window.location.href);
  const executionId = url.searchParams.get('executionId');
  return {
    isPopout: url.searchParams.get('executionLogs') === 'popout',
    executionId: executionId && executionId.trim() ? executionId.trim() : null,
  };
}

function buildExecutionLogsWindowUrl(executionId: string) {
  const url = new URL(window.location.href);
  url.searchParams.set('executionLogs', 'popout');
  url.searchParams.set('executionId', executionId);
  return url.toString();
}

function isWorkspacePage(value: string | null): value is WorkspacePage {
  return Boolean(value && WORKSPACE_PAGE_VALUES.includes(value as WorkspacePage));
}

function isNonSettingsWorkspacePage(value: string | null): value is NonSettingsWorkspacePage {
  return Boolean(value && NON_SETTINGS_WORKSPACE_PAGE_VALUES.includes(value as NonSettingsWorkspacePage));
}

function readStoredWorkspacePage(): WorkspacePage {
  if (typeof window === 'undefined') {
    return 'studio';
  }

  try {
    const raw = window.localStorage.getItem(WORKSPACE_PAGE_STORAGE_KEY);
    return isWorkspacePage(raw) ? raw : 'studio';
  } catch {
    return 'studio';
  }
}

function readStoredPreviousWorkspacePage(): NonSettingsWorkspacePage {
  if (typeof window === 'undefined') {
    return 'studio';
  }

  try {
    const raw = window.localStorage.getItem(PREVIOUS_WORKSPACE_PAGE_STORAGE_KEY);
    return isNonSettingsWorkspacePage(raw) ? raw : 'studio';
  } catch {
    return 'studio';
  }
}

function AevatarBrandMark(props: {
  size?: number;
  className?: string;
}) {
  const size = props.size ?? 44;
  return (
    <svg
      viewBox="0 0 400 400"
      width={size}
      height={size}
      className={props.className}
      aria-hidden="true"
      shapeRendering="crispEdges"
    >
      <rect width="400" height="400" rx="28" fill="#18181B" />

      <rect x="12" y="20" width="134" height="46" fill="#FAFAFA" />
      <rect x="102" y="20" width="44" height="142" fill="#FAFAFA" />
      <rect x="0" y="66" width="70" height="30" fill="#FAFAFA" />

      <rect x="254" y="20" width="134" height="46" fill="#FAFAFA" />
      <rect x="254" y="20" width="44" height="142" fill="#FAFAFA" />
      <rect x="330" y="66" width="70" height="30" fill="#FAFAFA" />

      <rect x="0" y="181" width="170" height="32" fill="#FAFAFA" />
      <rect x="230" y="181" width="170" height="32" fill="#FAFAFA" />
      <rect x="180" y="181" width="40" height="40" fill="#FAFAFA" />

      <rect x="12" y="304" width="134" height="46" fill="#FAFAFA" />
      <rect x="102" y="242" width="44" height="109" fill="#FAFAFA" />
      <rect x="0" y="274" width="70" height="30" fill="#FAFAFA" />

      <rect x="254" y="304" width="134" height="46" fill="#FAFAFA" />
      <rect x="254" y="242" width="44" height="109" fill="#FAFAFA" />
      <rect x="330" y="274" width="70" height="30" fill="#FAFAFA" />
    </svg>
  );
}

function toExecutionSummary(detail: any) {
  const prompt = String(detail?.prompt || '').trim();
  return {
    executionId: detail?.executionId || '',
    workflowName: detail?.workflowName || '',
    status: detail?.status || 'running',
    promptPreview: prompt.length <= 120 ? prompt : `${prompt.slice(0, 117)}...`,
    startedAtUtc: detail?.startedAtUtc || new Date().toISOString(),
    completedAtUtc: detail?.completedAtUtc || null,
    actorId: detail?.actorId || null,
    error: detail?.error || null,
  };
}

function createEmptyAuthSession(): AuthSessionState {
  return {
    loading: true,
    enabled: true,
    authenticated: false,
    providerDisplayName: 'NyxID',
    loginUrl: '/auth/login',
    logoutUrl: '/auth/logout',
    name: '',
    email: '',
    picture: '',
    errorMessage: '',
  };
}

function buildWorkflowAuthoringMetadata(scopeId: string | null) {
  return {
    ...(scopeId ? { scope_id: scopeId } : {}),
    'workflow.authoring.enabled': 'true',
    'workflow.intent': 'workflow_authoring',
  };
}

function WorkflowNodeCard({ data, selected }: any) {
  const category = getCategoryForType(data.stepType);
  const Icon = CATEGORY_ICONS[category.key as keyof typeof CATEGORY_ICONS] || Code2;
  const zoom = useStore(state => state.transform[2]);
  const compact = zoom < 0.68;
  const micro = zoom < 0.42;
  const statusClass = data.executionStatus && data.executionStatus !== 'idle'
    ? `node-status-${data.executionStatus}`
    : '';
  const width = micro ? 104 : compact ? 154 : 244;
  const detailCopy = summarizeNodeParameters(data.parameters);

  return (
    <div
      className={[
        'workflow-node',
        compact ? 'compact' : '',
        micro ? 'micro' : '',
        selected ? 'selected' : '',
        data.executionFocused ? 'execution-focus' : '',
        statusClass,
      ].join(' ')}
      style={{ width }}
    >
      <Handle type="target" position={Position.Left} style={{ background: category.color }} />
      {micro ? (
        <div className="workflow-node-micro">
          <div
            className="workflow-node-icon workflow-node-icon-micro"
            style={{ background: `${category.color}18` }}
          >
            <Icon size={14} color={category.color} />
          </div>
          <div className="workflow-node-micro-meta">
            <div className="workflow-node-title">{data.stepId}</div>
          </div>
          {data.executionStatus && data.executionStatus !== 'idle' ? (
            <span className={`workflow-node-status-dot ${data.executionStatus}`} />
          ) : null}
        </div>
      ) : compact ? (
        <div className="workflow-node-compact">
          <div
            className="workflow-node-icon"
            style={{ background: `${category.color}18` }}
          >
            <Icon size={14} color={category.color} />
          </div>
          <div className="workflow-node-compact-meta">
            <div className="workflow-node-title">{data.stepId}</div>
            <div className="workflow-node-subtitle">
              {data.targetRole || data.stepType}
            </div>
          </div>
          {data.executionStatus && data.executionStatus !== 'idle' ? (
            <span className={`workflow-node-status-dot ${data.executionStatus}`} />
          ) : null}
        </div>
      ) : (
        <>
          <div className="flex items-center gap-2 px-3 py-2 border-b border-gray-100">
            <div
              className="workflow-node-icon"
              style={{ background: `${category.color}18` }}
            >
              <Icon size={15} color={category.color} />
            </div>
            <div className="flex-1 min-w-0">
              <div className="text-[13px] font-semibold text-gray-800 truncate">{data.stepId}</div>
              <div className="text-[11px] text-gray-400 leading-tight">{data.stepType}</div>
            </div>
            {data.executionStatus && data.executionStatus !== 'idle' ? (
              <span className={`node-run-pill ${data.executionStatus}`}>{data.executionStatus}</span>
            ) : null}
          </div>
          <div className="px-3 py-2 text-[11px] text-gray-500 space-y-1">
            {data.targetRole ? (
              <div>
                <span className="text-gray-400">role:</span> {data.targetRole}
              </div>
            ) : null}
            <div className="truncate">{detailCopy}</div>
          </div>
        </>
      )}
      <Handle type="source" position={Position.Right} style={{ background: category.color }} />
    </div>
  );
}

function summarizeNodeParameters(parameters: Record<string, unknown>) {
  const firstEntry = Object.entries(parameters || {}).find(([, value]) => value !== '' && value !== null && value !== undefined);
  if (!firstEntry) {
    return 'No parameters';
  }

  const [key, value] = firstEntry;
  const text = formatParameterValue(value);
  return `${key}: ${text.length > 44 ? `${text.slice(0, 41)}...` : text}`;
}

function normalizeWorkflowName(value: string | null | undefined) {
  return String(value || '').trim().toLowerCase();
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

function formatDurationBetween(startValue: string | null | undefined, endValue: string | null | undefined) {
  if (!startValue) {
    return '';
  }

  const start = new Date(startValue).getTime();
  const end = endValue ? new Date(endValue).getTime() : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    return '';
  }

  const durationMs = end - start;
  if (durationMs < 1000) {
    return `${Math.round(durationMs)}ms`;
  }

  const seconds = durationMs / 1000;
  if (seconds < 60) {
    return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)}s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainderSeconds = Math.round(seconds % 60);
  if (minutes < 60) {
    return `${minutes}m ${remainderSeconds}s`;
  }

  const hours = Math.floor(minutes / 60);
  const remainderMinutes = minutes % 60;
  return `${hours}h ${remainderMinutes}m`;
}

function formatExecutionLogClipboard(log: ExecutionLogItem) {
  const lines = [`[${formatDateTime(log.timestamp)}] ${log.title}`];
  if (log.meta) {
    lines.push(log.meta);
  }
  if (log.clipboardText) {
    lines.push(log.clipboardText);
  }
  return lines.join('\n');
}

function formatExecutionLogsClipboard(trace: ExecutionTrace | null) {
  if (!trace?.logs?.length) {
    return '';
  }

  return trace.logs.map(log => formatExecutionLogClipboard(log)).join('\n\n---\n\n');
}

const DEFAULT_ORNN_BASE_URL = 'https://ornn.chrono-ai.fun';

function createEmptyStudioSettings(): StudioSettingsState {
  return {
    remoteRuntimeUrl: DEFAULT_REMOTE_RUNTIME_URL,
    localRuntimeUrl: DEFAULT_LOCAL_RUNTIME_URL,
    runtimeMode: 'local',
    ornnBaseUrl: DEFAULT_ORNN_BASE_URL,
    appearanceTheme: 'blue',
    colorMode: 'light',
    secretsFilePath: '',
    defaultProviderName: '',
    providerTypes: [],
    providers: [],
  };
}

function getFixedProviderName(providerType: string) {
  return providerType.trim().toLowerCase() === 'nyxid' ? 'nyxid' : '';
}

function usesFixedProviderName(providerType: string) {
  return Boolean(getFixedProviderName(providerType));
}

function toProviderDraft(item: any, providerTypes: ProviderTypeOption[]): ProviderDraft {
  const profile = providerTypes.find(option => option.id === item?.providerType) || null;
  return {
    key: `provider_${crypto.randomUUID()}`,
    providerName: item?.providerName || '',
    providerType: item?.providerType || profile?.id || 'openai',
    displayName: item?.displayName || profile?.displayName || item?.providerType || 'Provider',
    category: item?.category || profile?.category || 'configured',
    description: item?.description || profile?.description || '',
    endpoint: item?.endpoint || profile?.defaultEndpoint || '',
    model: item?.model || profile?.defaultModel || '',
    apiKey: item?.apiKey || '',
    apiKeyConfigured: Boolean(item?.apiKeyConfigured),
  };
}

function createUniqueRoleId(existingRoles: RoleState[], base = 'role') {
  const normalizedBase = (base || 'role').replace(/[^a-z0-9_]+/gi, '_').toLowerCase() || 'role';
  const used = new Set(existingRoles.map(role => role.id.trim().toLowerCase()).filter(Boolean));
  let index = 1;
  let candidate = normalizedBase;

  while (used.has(candidate)) {
    index += 1;
    candidate = `${normalizedBase}_${index}`;
  }

  return candidate;
}

function matchesRoleQuery(role: RoleState, query: string) {
  const normalizedQuery = query.trim().toLowerCase();
  if (!normalizedQuery) {
    return true;
  }

  return [
    role.id,
    role.name,
    role.systemPrompt,
    role.provider,
    role.model,
    role.connectorsText,
  ].join(' ').toLowerCase().includes(normalizedQuery);
}

function createEmptyRoleDraft(): RoleState {
  return createRoleState(1, {
    id: '',
    name: '',
    systemPrompt: '',
    provider: '',
    model: '',
    connectorsText: '',
  });
}

function hasConnectorDraftContent(connector: ConnectorState | null | undefined) {
  if (!connector) {
    return false;
  }

  const httpHeaders = Object.entries(connector.http.defaultHeaders || {}).some(([key, value]) => key.trim() || String(value || '').trim());
  const cliEnv = Object.entries(connector.cli.environment || {}).some(([key, value]) => key.trim() || String(value || '').trim());
  const mcpEnv = Object.entries(connector.mcp.environment || {}).some(([key, value]) => key.trim() || String(value || '').trim());

  return Boolean(
    connector.name.trim()
    || connector.http.baseUrl.trim()
    || connector.http.allowedMethods.some(item => item.trim() && item.trim().toUpperCase() !== 'POST')
    || connector.http.allowedPaths.some(item => item.trim() && item.trim() !== '/')
    || connector.http.allowedInputKeys.some(item => item.trim())
    || httpHeaders
    || connector.cli.command.trim()
    || connector.cli.fixedArguments.some(item => item.trim())
    || connector.cli.allowedOperations.some(item => item.trim())
    || connector.cli.allowedInputKeys.some(item => item.trim())
    || connector.cli.workingDirectory.trim()
    || cliEnv
    || connector.mcp.serverName.trim()
    || connector.mcp.command.trim()
    || connector.mcp.arguments.some(item => item.trim())
    || connector.mcp.defaultTool.trim()
    || connector.mcp.allowedTools.some(item => item.trim())
    || connector.mcp.allowedInputKeys.some(item => item.trim())
    || mcpEnv
  );
}

function createWorkflowRoleDraft(settingsState: StudioSettingsState): RoleState {
  const defaultProviderName = settingsState.defaultProviderName || settingsState.providers[0]?.providerName || '';
  const defaultModel = settingsState.providers.find(provider => provider.providerName === defaultProviderName)?.model
    || settingsState.providers[0]?.model
    || '';

  return createRoleState(1, {
    id: '',
    name: '',
    systemPrompt: '',
    provider: defaultProviderName,
    model: defaultModel,
    connectorsText: '',
  });
}

function hasRoleDraftContent(role: RoleState | null | undefined) {
  if (!role) {
    return false;
  }

  return Boolean(
    role.id.trim()
    || role.name.trim()
    || role.systemPrompt.trim()
    || role.provider.trim()
    || role.model.trim()
    || role.connectorsText.trim()
  );
}

function App() {
  const executionLogsWindowState = useMemo(() => readExecutionLogsWindowState(), []);
  const isExecutionLogsPopout = executionLogsWindowState.isPopout;
  const initialExecutionLogsPopoutExecutionId = executionLogsWindowState.executionId;

  const [appContext, setAppContext] = useState<AppContextState>(createEmptyAppContext());
  const [authSession, setAuthSession] = useState<AuthSessionState>(createEmptyAuthSession());
  const [workspacePage, setWorkspacePage] = useState<WorkspacePage>(() => readStoredWorkspacePage());
  const [previousWorkspacePage, setPreviousWorkspacePage] = useState<NonSettingsWorkspacePage>(() => readStoredPreviousWorkspacePage());
  const [explorerInitialFolder, setExplorerInitialFolder] = useState<string | null>(null);
  const [studioView, setStudioView] = useState<StudioView>('editor');
  const [settingsSection, setSettingsSection] = useState<SettingsSection>('runtime');
  const [userConfigState, setUserConfigState] = useState<UserConfigState>({
    defaultModel: '',
    preferredLlmRoute: USER_LLM_ROUTE_AUTO,
    loading: false,
    providers: [],
    supportedModels: [],
    modelsByProvider: {},
    modelsLoading: false,
  });
  const [rightPanelTab, setRightPanelTab] = useState<RightPanelTab>('node');
  const [rightPanelOpen, setRightPanelOpen] = useState(false);
  const [logsCollapsed, setLogsCollapsed] = useState(false);
  const [logsDetached, setLogsDetached] = useState(false);

  const [workspaceSettings, setWorkspaceSettings] = useState<{
    runtimeBaseUrl: string;
    directories: DirectorySummary[];
  }>({
    runtimeBaseUrl: DEFAULT_RUNTIME_BASE_URL,
    directories: [],
  });
  const [workflowList, setWorkflowList] = useState<WorkflowSummary[]>([]);

  const [settingsState, setSettingsState] = useState<StudioSettingsState>(createEmptyStudioSettings());
  const [, setSelectedProviderKey] = useState<string | null>(null);

  const [workflowMeta, setWorkflowMeta] = useState<WorkflowMetaState>(createEmptyWorkflowMeta());
  const [roles, setRoles] = useState<RoleState[]>(createDefaultRoles());
  const [nodes, setNodes] = useState<Array<Node<StudioNodeData>>>([]);
  const [edges, setEdges] = useState<Array<Edge<StudioEdgeData>>>([]);
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);

  const [paletteOpen, setPaletteOpen] = useState(false);
  const [paletteSearch, setPaletteSearch] = useState('');
  const [paletteExpandedSection, setPaletteExpandedSection] = useState<string>('AI');
  const [pendingAddPosition, setPendingAddPosition] = useState({ x: 420, y: 220 });
  const [canvasMenu, setCanvasMenu] = useState<{
    open: boolean;
    x: number;
    y: number;
  }>({
    open: false,
    x: 0,
    y: 0,
  });

  const [connectors, setConnectors] = useState<ConnectorState[]>([]);
  const [, setConnectorsMeta] = useState({
    homeDirectory: '',
    filePath: '',
    fileExists: false,
  });
  const [, setSelectedConnectorKey] = useState<string | null>(null);
  const [connectorDraft, setConnectorDraft] = useState<ConnectorState | null>(null);
  const [connectorModalOpen, setConnectorModalOpen] = useState(false);

  const [roleCatalog, setRoleCatalog] = useState<RoleState[]>([]);
  const [, setRolesMeta] = useState({
    homeDirectory: '',
    filePath: '',
    fileExists: false,
  });
  const [workflowRoleSearch, setWorkflowRoleSearch] = useState('');
  const [, setSelectedCatalogRoleKey] = useState<string | null>(null);
  const [expandedWorkflowRoleKey, setExpandedWorkflowRoleKey] = useState<string | null>(null);
  const [roleDraft, setRoleDraft] = useState<RoleState | null>(null);
  const [roleModalTarget, setRoleModalTarget] = useState<RoleModalTarget>('catalog');
  const [roleModalOpen, setRoleModalOpen] = useState(false);

  const [executions, setExecutions] = useState<any[]>([]);
  const [selectedExecutionId, setSelectedExecutionId] = useState<string | null>(null);
  const [executionDetail, setExecutionDetail] = useState<any>(null);
  const [executionTrace, setExecutionTrace] = useState<ExecutionTrace | null>(null);
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] = useState<number | null>(null);
  const [copiedExecutionLogIndex, setCopiedExecutionLogIndex] = useState<number | null>(null);
  const [copiedAllExecutionLogs, setCopiedAllExecutionLogs] = useState(false);
  const [executionPrompt, setExecutionPrompt] = useState('');
  const [runModalOpen, setRunModalOpen] = useState(false);
  const [runScopeId, setRunScopeId] = useState(() => nyxid.loadSession()?.user.sub || '');
  const [bindServiceId, setBindServiceId] = useState('');
  const [bindScopeModalOpen, setBindScopeModalOpen] = useState(false);
  const [bindScopePending, setBindScopePending] = useState(false);
  const [draftRunEvents, setDraftRunEvents] = useState<any[]>([]);
  const [draftRunText, setDraftRunText] = useState('');
  const [draftRunning, setDraftRunning] = useState(false);
  const draftRunAbortRef = useRef<AbortController | null>(null);
  const [executionActionInput, setExecutionActionInput] = useState('');
  const [executionActionPendingKey, setExecutionActionPendingKey] = useState('');
  const [executionStopPending, setExecutionStopPending] = useState(false);
  const [askAiOpen, setAskAiOpen] = useState(false);
  const [askAiPrompt, setAskAiPrompt] = useState('');
  const [askAiAnswer, setAskAiAnswer] = useState('');
  const [askAiReasoning, setAskAiReasoning] = useState('');
  const [askAiGeneratedYaml, setAskAiGeneratedYaml] = useState('');
  const [askAiPending, setAskAiPending] = useState(false);
  const [ornnTestState, setOrnnTestState] = useState<{ status: 'idle' | 'testing' | 'success' | 'error'; message: string }>({ status: 'idle', message: '' });
  const [ornnSkillsCache, setOrnnSkillsCache] = useState<api.OrnnSkillSummary[]>([]);
  const [ornnSkillsLoading, setOrnnSkillsLoading] = useState(false);
  const [runtimeTestState, setRuntimeTestState] = useState<{
    status: 'idle' | 'testing' | 'success' | 'error';
    message: string;
  }>({
    status: 'idle',
    message: '',
  });

  const [statusMessage, setStatusMessage] = useState<{
    text: string;
    type: 'success' | 'error' | 'info';
  } | null>(null);

  const reactFlowInstanceRef = useRef<any>(null);
  const syncSuppressedRef = useRef(true);
  const yamlSyncRevisionRef = useRef(0);
  const yamlParseTimerRef = useRef<number | null>(null);
  const yamlEditRevisionRef = useRef(0);
  const yamlAppliedRevisionRef = useRef(0);
  const toastTimerRef = useRef<number | null>(null);
  const executionLogCopyTimerRef = useRef<number | null>(null);
  const executionActionInputRef = useRef<HTMLTextAreaElement | null>(null);
  const executionLogsPopoutMonitorRef = useRef<number | null>(null);
  const executionLogsPopoutWindowRef = useRef<Window | null>(null);
  const saveShortcutHandlerRef = useRef<() => void>(() => {});

  const selectedNode = nodes.find(node => node.id === selectedNodeId) || null;
  const workflowAuthoringMetadata = useMemo(
    () => buildWorkflowAuthoringMetadata(appContext.scopeId),
    [appContext.scopeId],
  );
  const currentWorkflowExecutions = executions.filter(item => {
    const currentName = normalizeWorkflowName(workflowMeta.name);
    return !currentName || normalizeWorkflowName(item.workflowName) === currentName;
  });
  const executionNodes = studioView === 'execution'
    ? decorateNodesForExecution(nodes, executionTrace, activeExecutionLogIndex)
    : nodes;
  const executionEdges = studioView === 'execution'
    ? decorateEdgesForExecution(edges, nodes, executionTrace, activeExecutionLogIndex)
    : edges;
  const activeExecutionLog = executionTrace && Number.isInteger(activeExecutionLogIndex)
    ? executionTrace.logs[activeExecutionLogIndex as number] || null
    : null;
  const activeExecutionInteraction = activeExecutionLog?.interaction &&
    executionDetail?.status === 'waiting' &&
    activeExecutionLog.stepId &&
    executionTrace?.stepStates.get(activeExecutionLog.stepId)?.status === 'waiting'
    ? activeExecutionLog.interaction
    : null;
  const executionActionKeyBase = selectedExecutionId && activeExecutionInteraction
    ? `${selectedExecutionId}:${activeExecutionInteraction.stepId}`
    : '';
  const executionCanStop =
    Boolean(selectedExecutionId) &&
    (executionDetail?.status === 'running' || executionDetail?.status === 'waiting');

  useEffect(() => {
    void bootstrap();
  }, []);

  useEffect(() => api.onAuthRequired(detail => {
    setRunModalOpen(false);
    setAuthSession(prev => ({
      ...prev,
      loading: false,
      enabled: true,
      authenticated: false,
      loginUrl: detail?.loginUrl || prev.loginUrl || '/auth/login',
      errorMessage: detail?.message || 'Sign in to continue.',
    }));
  }), []);

  useEffect(() => () => {
    if (executionLogCopyTimerRef.current) {
      window.clearTimeout(executionLogCopyTimerRef.current);
    }
  }, []);

  useEffect(() => () => {
    if (executionLogsPopoutMonitorRef.current) {
      window.clearInterval(executionLogsPopoutMonitorRef.current);
    }
  }, []);

  useEffect(() => {
    saveShortcutHandlerRef.current = () => {
      void handleSaveWorkflow();
    };
  });

  useEffect(() => {
    setExecutionActionInput('');
    if (!activeExecutionInteraction) {
      return undefined;
    }

    const frame = window.requestAnimationFrame(() => {
      executionActionInputRef.current?.focus();
      executionActionInputRef.current?.select();
    });

    return () => window.cancelAnimationFrame(frame);
  }, [
    activeExecutionInteraction?.kind,
    activeExecutionInteraction?.runId,
    activeExecutionInteraction?.stepId,
  ]);

  useEffect(() => {
    if (workspacePage !== 'studio') {
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
      saveShortcutHandlerRef.current();
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [workspacePage]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(WORKSPACE_PAGE_STORAGE_KEY, workspacePage);
    } catch {
      // Ignore storage errors in restricted browser contexts.
    }
  }, [workspacePage]);

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    try {
      window.localStorage.setItem(PREVIOUS_WORKSPACE_PAGE_STORAGE_KEY, previousWorkspacePage);
    } catch {
      // Ignore storage errors in restricted browser contexts.
    }
  }, [previousWorkspacePage]);

  useEffect(() => {
    const workspaceReady = !authSession.loading && (!authSession.enabled || authSession.authenticated);
    if (workspaceReady && workspacePage === 'scripts' && !appContext.scriptsEnabled) {
      setWorkspacePage('studio');
    }
  }, [
    authSession.authenticated,
    authSession.enabled,
    authSession.loading,
    appContext.scriptsEnabled,
    workspacePage,
  ]);

  useEffect(() => {
    const normalizeControlHint = (raw: string, detailed = false) => {
      const cleaned = raw.replace(/\s+/g, ' ').replace(/[.!?]+$/g, '').trim();
      if (!cleaned) {
        return '';
      }

      if (detailed) {
        return cleaned;
      }

      const normalized = cleaned.toLowerCase();
      if (normalized.startsWith('sign out')) {
        return 'Sign out';
      }
      if (normalized.startsWith('sign in')) {
        return 'Sign in';
      }
      if (normalized.startsWith('zoom in')) {
        return 'Zoom in';
      }
      if (normalized.startsWith('zoom out')) {
        return 'Zoom out';
      }
      if (normalized.startsWith('fit view')) {
        return 'Fit view';
      }

      const words = cleaned.split(' ').filter(Boolean);
      if (words.length <= 2) {
        return words.join(' ');
      }

      if (['close', 'save', 'run', 'export', 'import', 'copy', 'validate', 'open', 'edit'].includes(words[0].toLowerCase())) {
        return words[0];
      }

      return words.slice(0, 2).join(' ');
    };

    const applyButtonHoverHints = () => {
      document.querySelectorAll<HTMLElement>('button, a.ghost-action, a.solid-action, a.panel-icon-button').forEach(control => {
        const text = control.textContent?.replace(/\s+/g, ' ').trim() || '';
        const ariaLabel = control.getAttribute('aria-label')?.trim() || '';
        const existingTitle = control.getAttribute('title')?.trim() || '';
        const explicitHint = control.getAttribute('data-tooltip')?.trim() || '';
        const nextTitle = normalizeControlHint(
          explicitHint || ariaLabel || existingTitle || text,
          Boolean(explicitHint || ariaLabel || existingTitle));
        if (!nextTitle || control.getAttribute('title') === nextTitle) {
          return;
        }

        control.setAttribute('title', nextTitle);
      });
    };

    applyButtonHoverHints();
    const observer = new MutationObserver(() => {
      window.requestAnimationFrame(applyButtonHoverHints);
    });
    observer.observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true,
    });

    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    return () => {
      if (toastTimerRef.current) {
        window.clearTimeout(toastTimerRef.current);
      }

      if (yamlParseTimerRef.current) {
        window.clearTimeout(yamlParseTimerRef.current);
      }
    };
  }, []);

  useEffect(() => {
    const hasDocument = workflowMeta.name || nodes.length > 0 || roles.length > 0;
    if (!hasDocument) {
      return undefined;
    }

    if (syncSuppressedRef.current) {
      syncSuppressedRef.current = false;
      return undefined;
    }

    const timer = window.setTimeout(() => {
      void syncYaml();
    }, 280);

    return () => {
      window.clearTimeout(timer);
    };
  }, [
    workflowMeta.name,
    workflowMeta.description,
    roles,
    nodes,
    edges,
    workflowList,
  ]);

  useEffect(() => {
    if (expandedWorkflowRoleKey && !roles.some(role => role.key === expandedWorkflowRoleKey)) {
      setExpandedWorkflowRoleKey(null);
    }
  }, [roles, expandedWorkflowRoleKey]);

  useEffect(() => {
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [selectedExecutionId, activeExecutionLogIndex]);

  useEffect(() => {
    if (isExecutionLogsPopout) {
      document.title = selectedExecutionId
        ? `Execution Logs · ${executionDetail?.workflowName || 'Aevatar App'}`
        : 'Execution Logs · Aevatar App';
      return;
    }

    document.title = 'Aevatar App';
  }, [executionDetail?.workflowName, isExecutionLogsPopout, selectedExecutionId]);

  useEffect(() => {
    if (!isExecutionLogsPopout || authSession.loading) {
      return;
    }

    if (authSession.enabled && !authSession.authenticated) {
      return;
    }

    if (!initialExecutionLogsPopoutExecutionId || selectedExecutionId === initialExecutionLogsPopoutExecutionId) {
      return;
    }

    void openExecution(initialExecutionLogsPopoutExecutionId);
  }, [
    authSession.authenticated,
    authSession.enabled,
    authSession.loading,
    initialExecutionLogsPopoutExecutionId,
    isExecutionLogsPopout,
    selectedExecutionId,
  ]);

  useEffect(() => {
    if (isExecutionLogsPopout || !logsDetached) {
      return undefined;
    }

    executionLogsPopoutMonitorRef.current = window.setInterval(() => {
      const logsWindow = executionLogsPopoutWindowRef.current;
      if (logsWindow && !logsWindow.closed) {
        return;
      }

      executionLogsPopoutWindowRef.current = null;
      setLogsDetached(false);
      setLogsCollapsed(false);
      if (executionLogsPopoutMonitorRef.current) {
        window.clearInterval(executionLogsPopoutMonitorRef.current);
        executionLogsPopoutMonitorRef.current = null;
      }
    }, 500);

    return () => {
      if (executionLogsPopoutMonitorRef.current) {
        window.clearInterval(executionLogsPopoutMonitorRef.current);
        executionLogsPopoutMonitorRef.current = null;
      }
    };
  }, [isExecutionLogsPopout, logsDetached]);

  useEffect(() => {
    if (isExecutionLogsPopout || !selectedExecutionId) {
      return;
    }

    const logsWindow = executionLogsPopoutWindowRef.current;
    if (!logsWindow || logsWindow.closed) {
      return;
    }

    try {
      logsWindow.location.replace(buildExecutionLogsWindowUrl(selectedExecutionId));
    } catch {
    }
  }, [isExecutionLogsPopout, selectedExecutionId]);

  useEffect(() => {
    const shouldRefresh = executionDetail?.status === 'running' || executionDetail?.status === 'waiting';
    if (!selectedExecutionId || !shouldRefresh) {
      return undefined;
    }

    let cancelled = false;
    let timer = 0;

    const refreshExecution = async () => {
      try {
        const detail = await api.executions.get(selectedExecutionId);
        if (cancelled) {
          return;
        }

        applyExecutionDetail(detail);
        if (detail?.status === 'running' || detail?.status === 'waiting') {
          timer = window.setTimeout(() => {
            void refreshExecution();
          }, 700);
        }
      } catch {
        if (cancelled) {
          return;
        }

        timer = window.setTimeout(() => {
          void refreshExecution();
        }, 1200);
      }
    };

    timer = window.setTimeout(() => {
      void refreshExecution();
    }, 350);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [executionDetail?.status, selectedExecutionId]);

  async function bootstrap() {
    try {
      // Handle OAuth callback if we're returning from NyxID.
      if (nyxid.isAuthCallback()) {
        try {
          const { session: oauthSession, returnTo } = await nyxid.handleCallback();
          setAuthSession({
            loading: false,
            enabled: true,
            authenticated: true,
            providerDisplayName: 'NyxID',
            loginUrl: '',
            logoutUrl: '',
            name: oauthSession.user.name || '',
            email: oauthSession.user.email || '',
            picture: oauthSession.user.picture || '',
            errorMessage: '',
          });
          // Update scope ID from NyxID user.
          if (oauthSession.user.sub) setRunScopeId(oauthSession.user.sub);
          // Clean up callback URL params.
          window.history.replaceState({}, '', returnTo || '/');
        } catch (err: any) {
          setAuthSession(prev => ({
            ...prev,
            loading: false,
            enabled: true,
            authenticated: false,
            errorMessage: err?.message || 'OAuth callback failed.',
          }));
          window.history.replaceState({}, '', '/');
          return;
        }
      }

      // Check for existing NyxID session in localStorage.
      let localSession = nyxid.loadSession();
      if (localSession && !nyxid.getActiveSession()) {
        // Token expired but refresh token available.
        localSession = await nyxid.refreshSession(localSession);
      }

      // Also check backend auth status (for scope resolution).
      let backendSession: any = null;
      try {
        backendSession = await api.auth.getSession();
      } catch { /* backend may not have auth endpoint */ }

      const isAuthenticated = Boolean(localSession) || Boolean(backendSession?.authenticated);
      const nextAuthSession: AuthSessionState = {
        loading: false,
        enabled: true,
        authenticated: isAuthenticated,
        providerDisplayName: 'NyxID',
        loginUrl: '',
        logoutUrl: '',
        name: localSession?.user.name || backendSession?.name || '',
        email: localSession?.user.email || backendSession?.email || '',
        picture: localSession?.user.picture || '',
        errorMessage: '',
      };
      setAuthSession(nextAuthSession);

      if (!isAuthenticated) {
        return;
      }

      const [
        contextResult,
        workspaceResult,
        workflowsResult,
        connectorCatalogResult,
        connectorDraftResult,
        roleCatalogResult,
        roleDraftResult,
        executionListResult,
        settingsResult,
        userConfigResult,
      ] = await Promise.allSettled([
        api.app.getContext(),
        api.workspace.getSettings(),
        api.workspace.listWorkflows(),
        api.connectors.getCatalog(),
        api.connectors.getDraft(),
        api.roles.getCatalog(),
        api.roles.getDraft(),
        api.executions.list(),
        api.settings.get(),
        api.userConfig.get(),
      ]);

      const bootstrapFailures = [
        { label: 'app context', result: contextResult },
        { label: 'workspace settings', result: workspaceResult },
        { label: 'workflow list', result: workflowsResult },
        { label: 'connectors catalog', result: connectorCatalogResult },
        { label: 'connector draft', result: connectorDraftResult },
        { label: 'roles catalog', result: roleCatalogResult },
        { label: 'role draft', result: roleDraftResult },
        { label: 'execution list', result: executionListResult },
        { label: 'studio settings', result: settingsResult },
        { label: 'user config', result: userConfigResult },
      ].flatMap(item =>
        item.result.status === 'rejected'
          ? [{ label: item.label, error: item.result.reason }]
          : []);

      const authFailure = bootstrapFailures.find(item => isAuthResponseInvalid(item.error));
      if (authFailure) {
        setAuthSession(prev => ({
          ...prev,
          loading: false,
          enabled: true,
          authenticated: false,
          loginUrl: authFailure.error?.loginUrl || prev.loginUrl || '/auth/login',
          errorMessage: authFailure.error?.message || 'Sign in to continue.',
        }));
        return;
      }

      bootstrapFailures.forEach(item => {
        console.warn(`[Aevatar App] Failed to load bootstrap resource: ${item.label}`, item.error);
      });

      const context = contextResult.status === 'fulfilled' ? contextResult.value : null;
      const workspace = workspaceResult.status === 'fulfilled' ? workspaceResult.value : null;
      const workflows = workflowsResult.status === 'fulfilled' ? workflowsResult.value : [];
      const connectorCatalog = connectorCatalogResult.status === 'fulfilled' ? connectorCatalogResult.value : null;
      const connectorDraftResponse = connectorDraftResult.status === 'fulfilled' ? connectorDraftResult.value : null;
      const roleCatalogResponse = roleCatalogResult.status === 'fulfilled' ? roleCatalogResult.value : null;
      const roleDraftResponse = roleDraftResult.status === 'fulfilled' ? roleDraftResult.value : null;
      const executionList = executionListResult.status === 'fulfilled' ? executionListResult.value : [];
      const settings = settingsResult.status === 'fulfilled' ? settingsResult.value : null;
      const userConfigData = userConfigResult.status === 'fulfilled' ? userConfigResult.value : null;

      const runtimeConfig = resolveUserRuntimeConfig(userConfigData);
      const nextRuntime = runtimeConfig.activeRuntimeUrl;
      fetch('/api/_proxy/runtime-url', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ runtimeBaseUrl: nextRuntime }),
      }).catch(() => {});

      const workflowStorageMode: WorkflowStorageMode = context?.workflowStorageMode === 'scope' ? 'scope' : 'workspace';
      const resolvedScopeId = context?.scopeResolved && context?.scopeId ? context.scopeId : null;
      const nextDirectories = workflowStorageMode === 'scope' && resolvedScopeId
        ? [{
            directoryId: `scope:${resolvedScopeId}`,
            label: resolvedScopeId,
            path: `scope://${resolvedScopeId}`,
            isBuiltIn: true,
          }]
        : Array.isArray(workspace?.directories) ? workspace.directories : [];

      setAppContext(resolveAppContextState(context));
      setWorkspaceSettings({
        runtimeBaseUrl: nextRuntime,
        directories: nextDirectories,
      });
      setWorkflowList(Array.isArray(workflows) ? workflows : []);

      hydrateSettings(settings, {
        ...runtimeConfig,
        defaultModel: userConfigData?.defaultModel || '',
        preferredLlmRoute: userConfigData?.preferredLlmRoute || USER_LLM_ROUTE_AUTO,
      });
      setUserConfigState(prev => ({
        ...prev,
        defaultModel: userConfigData?.defaultModel || '',
        preferredLlmRoute: normalizeUserLlmRoute(userConfigData?.preferredLlmRoute),
        loading: false,
      }));

      hydrateConnectorCatalog(connectorCatalog);
      hydrateConnectorDraft(connectorDraftResponse);

      hydrateRoleCatalog(roleCatalogResponse);
      hydrateRoleDraft(roleDraftResponse);

      setExecutions(Array.isArray(executionList) ? executionList : []);

      const defaultDirectoryId = nextDirectories[0]?.directoryId || null;
      setWorkflowMeta(prev => ({
        ...prev,
        directoryId: defaultDirectoryId,
      }));

      if (bootstrapFailures.length > 0) {
        flash(summarizeBootstrapFailures(bootstrapFailures.map(item => item.label)), 'info');
      }
    } catch (error: any) {
      if (isAuthResponseInvalid(error)) {
        setAuthSession(prev => ({
          ...prev,
          loading: false,
          enabled: true,
          authenticated: false,
          loginUrl: error?.loginUrl || prev.loginUrl || '/auth/login',
          errorMessage: error?.message || 'Sign in to continue.',
        }));
        return;
      }

      setAuthSession(prev => ({
        ...prev,
        loading: false,
        errorMessage: prev.errorMessage || error?.message || 'Failed to load app session.',
      }));
      flash(error?.message || 'Failed to load studio', 'error');
    }
  }

  function hydrateSettings(payload: any, userConfigData?: any) {
    const providerTypes = Array.isArray(payload?.providerTypes)
      ? payload.providerTypes.map((item: any) => ({
          id: item.id,
          displayName: item.displayName,
          category: item.category,
          description: item.description,
          recommended: Boolean(item.recommended),
          defaultEndpoint: item.defaultEndpoint || '',
          defaultModel: item.defaultModel || '',
        }))
      : [];

    const providers = Array.isArray(payload?.providers)
      ? payload.providers.map((item: any) => toProviderDraft(item, providerTypes))
      : [];

    // Apply user-config defaultModel to NyxID Gateway provider
    const remoteDefaultModel = userConfigData?.defaultModel;
    if (remoteDefaultModel) {
      const nyxIdProvider = providers.find((p: any) => usesFixedProviderName(p.providerType));
      if (nyxIdProvider) {
        nyxIdProvider.model = remoteDefaultModel;
      }
    }

    const runtimeConfig = resolveUserRuntimeConfig(userConfigData);

    setSettingsState({
      remoteRuntimeUrl: runtimeConfig.remoteRuntimeUrl,
      localRuntimeUrl: runtimeConfig.localRuntimeUrl,
      runtimeMode: runtimeConfig.runtimeMode,
      ornnBaseUrl: payload?.ornnBaseUrl || DEFAULT_ORNN_BASE_URL,
      appearanceTheme: payload?.appearanceTheme || 'blue',
      colorMode: payload?.colorMode === 'dark' ? 'dark' : 'light',
      secretsFilePath: payload?.secretsFilePath || '',
      defaultProviderName: payload?.defaultProviderName || '',
      providerTypes,
      providers,
    });
    setUserConfigState(prev => ({
      ...prev,
      defaultModel: String(userConfigData?.defaultModel || '').trim(),
      preferredLlmRoute: normalizeUserLlmRoute(userConfigData?.preferredLlmRoute),
    }));
    setSelectedProviderKey(providers[0]?.key || null);
    setRuntimeTestState({
      status: 'idle',
      message: '',
    });
  }

  function hydrateConnectorCatalog(payload: any) {
    const connectorItems = Array.isArray(payload?.connectors)
      ? payload.connectors.map((item: any) => toConnectorState(item))
      : [];

    setConnectorsMeta({
      homeDirectory: payload?.homeDirectory || '',
      filePath: payload?.filePath || '',
      fileExists: Boolean(payload?.fileExists),
    });
    setConnectors(connectorItems);
    setSelectedConnectorKey(connectorItems[0]?.key || null);
  }

  function hydrateRoleCatalog(payload: any) {
    const catalogRoles = Array.isArray(payload?.roles)
      ? payload.roles.map((item: any, index: number) => toRoleState(item, index + 1))
      : [];

    setRolesMeta({
      homeDirectory: payload?.homeDirectory || '',
      filePath: payload?.filePath || '',
      fileExists: Boolean(payload?.fileExists),
    });
    setRoleCatalog(catalogRoles);
    setSelectedCatalogRoleKey(catalogRoles[0]?.key || null);
  }

  function hydrateConnectorDraft(payload: any) {
    setConnectorDraft(payload?.draft ? toConnectorState(payload.draft) : null);
  }

  function hydrateRoleDraft(payload: any) {
    setRoleDraft(payload?.draft ? toRoleState(payload.draft, 1) : null);
  }

  function flash(text: string, type: 'success' | 'error' | 'info') {
    setStatusMessage({ text, type });
    if (toastTimerRef.current) {
      window.clearTimeout(toastTimerRef.current);
    }
    toastTimerRef.current = window.setTimeout(() => {
      setStatusMessage(null);
      toastTimerRef.current = null;
    }, 2600);
  }

  function showExecutionLogCopyFeedback(mode: 'single' | 'all', index?: number) {
    if (executionLogCopyTimerRef.current) {
      window.clearTimeout(executionLogCopyTimerRef.current);
    }

    setCopiedExecutionLogIndex(mode === 'single' ? index ?? null : null);
    setCopiedAllExecutionLogs(mode === 'all');
    executionLogCopyTimerRef.current = window.setTimeout(() => {
      setCopiedExecutionLogIndex(null);
      setCopiedAllExecutionLogs(false);
      executionLogCopyTimerRef.current = null;
    }, 1600);
  }

  async function copyTextToClipboard(text: string) {
    if (!text.trim()) {
      flash('Nothing to copy', 'info');
      return false;
    }

    if (!navigator.clipboard?.writeText) {
      flash('Clipboard is unavailable in this browser context', 'error');
      return false;
    }

    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch (error: any) {
      flash(error?.message || 'Failed to copy to clipboard', 'error');
      return false;
    }
  }

  async function handleExecutionLogClick(log: ExecutionLogItem, index: number) {
    setActiveExecutionLogIndex(index);
    const copied = await copyTextToClipboard(formatExecutionLogClipboard(log));
    if (copied) {
      showExecutionLogCopyFeedback('single', index);
    }
  }

  async function handleCopyAllExecutionLogs() {
    const copied = await copyTextToClipboard(formatExecutionLogsClipboard(executionTrace));
    if (copied) {
      showExecutionLogCopyFeedback('all');
      flash('Execution logs copied', 'success');
    }
  }

  function focusExecutionLogsPopoutWindow() {
    const logsWindow = executionLogsPopoutWindowRef.current;
    if (!logsWindow || logsWindow.closed) {
      executionLogsPopoutWindowRef.current = null;
      setLogsDetached(false);
      setLogsCollapsed(false);
      return false;
    }

    logsWindow.focus();
    return true;
  }

  function handlePopOutExecutionLogs() {
    if (!selectedExecutionId) {
      flash('Pick a run first', 'info');
      return;
    }

    const nextUrl = buildExecutionLogsWindowUrl(selectedExecutionId);
    const existingWindow = executionLogsPopoutWindowRef.current;
    if (existingWindow && !existingWindow.closed) {
      existingWindow.location.replace(nextUrl);
      existingWindow.focus();
      setLogsDetached(true);
      setLogsCollapsed(true);
      return;
    }

    const popupWidth = Math.max(window.screen?.availWidth || window.innerWidth || 1440, 1280);
    const popupHeight = Math.max(window.screen?.availHeight || window.innerHeight || 960, 720);
    const popupFeatures = [
      'popup=yes',
      `width=${popupWidth}`,
      `height=${popupHeight}`,
      'left=0',
      'top=0',
      'resizable=yes',
      'scrollbars=yes',
    ].join(',');
    const logsWindow = window.open(
      nextUrl,
      'aevatar-execution-logs',
      popupFeatures);

    if (!logsWindow) {
      flash('Allow pop-ups to open execution logs in a new window', 'error');
      return;
    }

    executionLogsPopoutWindowRef.current = logsWindow;
    try {
      logsWindow.moveTo(0, 0);
      logsWindow.resizeTo(popupWidth, popupHeight);
    } catch {
      // Browser window managers may block move/resize. The popup still renders fullscreen layout.
    }
    logsWindow.focus();
    setLogsDetached(true);
    setLogsCollapsed(true);
  }

  function handleToggleExecutionLogsPanel() {
    if (logsDetached) {
      focusExecutionLogsPopoutWindow();
      return;
    }

    setLogsCollapsed(value => !value);
  }

  function upsertExecutionSummary(detail: any) {
    if (!detail?.executionId) {
      return;
    }

    const nextSummary = toExecutionSummary(detail);
    setExecutions(prev => {
      const existingIndex = prev.findIndex(item => item.executionId === nextSummary.executionId);
      if (existingIndex < 0) {
        return [nextSummary, ...prev];
      }

      return prev.map((item, index) => index === existingIndex ? { ...item, ...nextSummary } : item);
    });
  }

  function invalidateYamlSync() {
    yamlSyncRevisionRef.current += 1;
  }

  function getPreferredSelectedNodeId(nextNodes: Array<Node<StudioNodeData>>, preferredStepId?: string | null) {
    if (preferredStepId) {
      const matchedNode = nextNodes.find(node => node.data.stepId === preferredStepId);
      if (matchedNode) {
        return matchedNode.id;
      }
    }

    return nextNodes[0]?.id || null;
  }

  function markDirty() {
    setWorkflowMeta(prev => (prev.dirty ? prev : { ...prev, dirty: true }));
  }

  function openExplorerPage(initialFolder?: string) {
    setWorkspacePage('explorer');
    if (initialFolder) setExplorerInitialFolder(initialFolder);
    setRightPanelOpen(false);
    setPaletteOpen(false);
    setCanvasMenu({ open: false, x: 0, y: 0 });
  }

  function openStudioPage() {
    setWorkspacePage('studio');
    setPaletteOpen(false);
    setCanvasMenu({ open: false, x: 0, y: 0 });
  }

  function openScriptsPage() {
    setWorkspacePage('scripts');
    setRightPanelOpen(false);
    setPaletteOpen(false);
    setCanvasMenu({ open: false, x: 0, y: 0 });
  }

  function openSettingsPage(section: SettingsSection = 'runtime') {
    if (workspacePage !== 'settings') {
      setPreviousWorkspacePage(workspacePage);
    }
    setWorkspacePage('settings');
    setSettingsSection(section);
    setRightPanelOpen(false);
    setPaletteOpen(false);
    setCanvasMenu({ open: false, x: 0, y: 0 });
  }

  function closeSettingsPage() {
    setWorkspacePage(previousWorkspacePage);
  }

  function toggleRightDrawer(tab: RightPanelTab) {
    if (rightPanelOpen && rightPanelTab === tab) {
      setRightPanelOpen(false);
      return;
    }

    setRightPanelTab(tab);
    setRightPanelOpen(true);
  }


  function scheduleCanvasOverview() {
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        reactFlowInstanceRef.current?.fitView({
          padding: 0.22,
          minZoom: 0.14,
          maxZoom: 0.92,
          duration: 220,
        });
      });
    });
  }

  async function loadWorkflowList() {
    const workflows = await api.workspace.listWorkflows();
    setWorkflowList(Array.isArray(workflows) ? workflows : []);
  }

  function hydrateWorkflow(payload: any) {
    const graph = buildGraphFromWorkflow(payload?.document || payload, payload?.layout, createDefaultRoles());
    syncSuppressedRef.current = true;
    invalidateYamlSync();
    setRoles(graph.roles);
    setNodes(graph.nodes);
    setEdges(graph.edges);
    setSelectedNodeId(graph.nodes[0]?.id || null);
    setSelectedExecutionId(null);
    setRunModalOpen(false);
    setWorkflowMeta({
      workflowId: payload?.workflowId || null,
      directoryId: payload?.directoryId || workspaceSettings.directories[0]?.directoryId || null,
      fileName: payload?.fileName || '',
      filePath: payload?.filePath || '',
      name: payload?.name || payload?.document?.name || payload?.rootWorkflow?.name || 'draft',
      description: payload?.document?.description || payload?.rootWorkflow?.description || '',
      closedWorldMode: Boolean(payload?.document?.configuration?.closedWorldMode),
      yaml: payload?.yaml || '',
      findings: Array.isArray(payload?.findings) ? payload.findings : [],
      dirty: false,
      lastSavedAt: payload?.updatedAtUtc || null,
    });
    setStudioView('editor');
    scheduleCanvasOverview();
  }

  async function openWorkflow(workflowId: string) {
    try {
      const payload = await api.workspace.getWorkflow(workflowId);
      hydrateWorkflow(payload);
      setWorkspacePage('studio');
      setRightPanelOpen(false);
    } catch (error: any) {
      flash(error?.message || 'Failed to open workflow', 'error');
    }
  }

  async function syncYaml() {
    const revision = ++yamlSyncRevisionRef.current;
    try {
      const document = buildWorkflowDocument(workflowMeta, roles, nodes, edges);
      const response = await api.editor.serializeYaml(document, workflowList.map(item => item.name));
      if (revision !== yamlSyncRevisionRef.current) {
        return;
      }

      setWorkflowMeta(prev => ({
        ...prev,
        yaml: response?.yaml || '',
        findings: Array.isArray(response?.findings) ? response.findings : [],
      }));
    } catch (error: any) {
      if (revision !== yamlSyncRevisionRef.current) {
        return;
      }

      setWorkflowMeta(prev => ({
        ...prev,
        findings: [{
          level: 2,
          message: error?.message || 'Failed to sync YAML.',
          path: '/',
        }],
      }));
    }
  }

  async function syncGraphFromYaml(yaml: string, revision: number) {
    try {
      const parsed = await api.editor.parseYaml(yaml, workflowList.map(item => item.name));
      const findings = Array.isArray(parsed?.findings) ? parsed.findings : [];
      if (!parsed?.document) {
        if (revision === yamlEditRevisionRef.current) {
          setWorkflowMeta(prev => ({
            ...prev,
            findings: findings.length > 0 ? findings : [{
              level: 2,
              message: 'YAML parse returned an empty document.',
              path: '/',
            }],
          }));
        }
        return;
      }

      if (revision > yamlAppliedRevisionRef.current) {
        const preferredStepId = selectedNode?.data.stepId || null;
        const graph = buildGraphFromWorkflow(
          parsed.document,
          buildLayoutDocument(workflowMeta, nodes),
          createDefaultRoles(),
        );

        yamlAppliedRevisionRef.current = revision;
        syncSuppressedRef.current = true;
        invalidateYamlSync();
        setRoles(graph.roles);
        setNodes(graph.nodes);
        setEdges(graph.edges);
        setSelectedNodeId(getPreferredSelectedNodeId(graph.nodes, preferredStepId));
        setWorkflowMeta(prev => ({
          ...prev,
          name: parsed.document?.name || prev.name || 'draft',
          description: parsed.document?.description || '',
          closedWorldMode: Boolean(parsed.document?.configuration?.closedWorldMode),
          findings: revision === yamlEditRevisionRef.current ? findings : prev.findings,
          dirty: true,
          lastSavedAt: null,
        }));
        return;
      }

      if (revision === yamlEditRevisionRef.current) {
        setWorkflowMeta(prev => ({
          ...prev,
          findings,
        }));
      }
    } catch (error: any) {
      if (revision !== yamlEditRevisionRef.current) {
        return;
      }

      setWorkflowMeta(prev => ({
        ...prev,
        findings: Array.isArray(error?.findings) && error.findings.length > 0 ? error.findings : [{
          level: 2,
          message: error?.message || 'Failed to parse YAML.',
          path: '/',
        }],
      }));
    }
  }

  function handleYamlEditorChange(nextYaml: string) {
    const revision = ++yamlEditRevisionRef.current;
    if (yamlParseTimerRef.current) {
      window.clearTimeout(yamlParseTimerRef.current);
    }

    setWorkflowMeta(prev => ({
      ...prev,
      yaml: nextYaml,
      dirty: true,
      lastSavedAt: null,
    }));

    yamlParseTimerRef.current = window.setTimeout(() => {
      void syncGraphFromYaml(nextYaml, revision);
    }, 180);
  }

  async function serializeCurrentWorkflow() {
    const document = buildWorkflowDocument(workflowMeta, roles, nodes, edges);
    const response = await api.editor.serializeYaml(document, workflowList.map(item => item.name));
    setWorkflowMeta(prev => ({
      ...prev,
      yaml: response?.yaml || '',
      findings: Array.isArray(response?.findings) ? response.findings : [],
    }));
    return response;
  }

  async function handleSaveWorkflow() {
    const directoryId = workflowMeta.directoryId || workspaceSettings.directories[0]?.directoryId;
    if (!directoryId) {
      flash('Add a workflow directory first', 'error');
      openExplorerPage('workflows');
      return;
    }

    try {
      const serialized = await serializeCurrentWorkflow();
      const payload = await api.workspace.saveWorkflow({
        workflowId: workflowMeta.workflowId,
        directoryId,
        workflowName: workflowMeta.name.trim() || 'draft',
        fileName: workflowMeta.fileName || null,
        yaml: serialized?.yaml || workflowMeta.yaml,
        layout: buildLayoutDocument(workflowMeta, nodes),
      });

      hydrateWorkflow(payload);
      await loadWorkflowList();
      flash('Workflow saved', 'success');
    } catch (error: any) {
      flash(error?.message || 'Save failed', 'error');
    }
  }

  async function handleValidateWorkflow() {
    try {
      const parsed = await api.editor.parseYaml(workflowMeta.yaml || '', workflowList.map(item => item.name));
      const parseFindings = Array.isArray(parsed?.findings) ? parsed.findings : [];
      if (!parsed?.document) {
        setWorkflowMeta(prev => ({
          ...prev,
          findings: parseFindings.length > 0 ? parseFindings : [{
            level: 2,
            message: 'YAML parse returned an empty document.',
            path: '/',
          }],
        }));
        toggleRightDrawer('yaml');
        flash('Fix YAML errors before validating', 'error');
        return;
      }

      const response = await api.editor.validate(parsed.document, workflowList.map(item => item.name));
      setWorkflowMeta(prev => ({
        ...prev,
        findings: Array.isArray(response?.findings) ? response.findings : parseFindings,
      }));
      const errorCount = (response?.findings || []).filter((finding: ValidationFinding) => toFindingLevel(finding.level) === 'error').length;
      if (errorCount > 0) {
        toggleRightDrawer('yaml');
      }
      flash(errorCount > 0 ? `${errorCount} validation errors` : 'Validation passed', errorCount > 0 ? 'error' : 'success');
    } catch (error: any) {
      flash(error?.message || 'Validation failed', 'error');
    }
  }

  async function handleExportWorkflow() {
    try {
      const serialized = workflowMeta.yaml ? { yaml: workflowMeta.yaml } : await serializeCurrentWorkflow();
      const blob = new Blob([serialized?.yaml || ''], { type: 'text/yaml' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `${workflowMeta.name || 'workflow'}.yaml`;
      anchor.click();
      URL.revokeObjectURL(url);
      flash('YAML exported', 'success');
    } catch (error: any) {
      flash(error?.message || 'Export failed', 'error');
    }
  }


  async function applyAskAiYaml(yaml: string) {
    const parsed = await api.editor.parseYaml(yaml, workflowList.map(item => item.name));
    if (!parsed?.document) {
      throw new Error('AI did not return a valid workflow YAML.');
    }

    const graph = buildGraphFromWorkflow(parsed.document, null, createDefaultRoles());
    syncSuppressedRef.current = true;
    invalidateYamlSync();
    setRoles(graph.roles);
    setNodes(graph.nodes);
    setEdges(graph.edges);
    setSelectedNodeId(graph.nodes[0]?.id || null);
    setSelectedExecutionId(null);
    setExecutionDetail(null);
    setExecutionTrace(null);
    setActiveExecutionLogIndex(null);
    setRunModalOpen(false);
    setWorkflowMeta(prev => ({
      ...prev,
      directoryId: prev.directoryId || workspaceSettings.directories[0]?.directoryId || null,
      name: parsed.document?.name || prev.name || 'draft',
      description: parsed.document?.description || '',
      closedWorldMode: Boolean(parsed.document?.configuration?.closedWorldMode),
      yaml,
      findings: Array.isArray(parsed.findings) ? parsed.findings : [],
      dirty: true,
      lastSavedAt: null,
    }));
    setWorkspacePage('studio');
    setStudioView('editor');
    scheduleCanvasOverview();
  }

  async function handleAskAiGenerate() {
    if (!askAiPrompt.trim()) {
      flash('Describe the workflow you want to generate', 'error');
      return;
    }

    setAskAiPending(true);
    setAskAiAnswer('');
    setAskAiReasoning('');
    setAskAiGeneratedYaml('');

    try {
      const hasCanvasContent = nodes.length > 0 || edges.length > 0 || Boolean(workflowMeta.yaml.trim());
      const serialized = hasCanvasContent
        ? await api.editor.serializeYaml(buildWorkflowDocument(workflowMeta, roles, nodes, edges), workflowList.map(item => item.name))
        : null;
      const currentYaml = serialized?.yaml || workflowMeta.yaml || '';
      const answer = await api.assistant.authorWorkflow({
        prompt: askAiPrompt.trim(),
        currentYaml,
        availableWorkflowNames: workflowList.map(item => item.name),
        metadata: workflowAuthoringMetadata,
      }, {
        onText: text => setAskAiAnswer(text),
        onReasoning: text => setAskAiReasoning(text),
      });

      const nextYaml = String(answer || '').trim();
      if (!nextYaml) {
        throw new Error('AI did not return workflow YAML.');
      }

      setAskAiAnswer(nextYaml);
      setAskAiGeneratedYaml(nextYaml);
      flash('AI workflow YAML is ready to apply', 'success');
    } catch (error: any) {
      flash(error?.message || 'Failed to generate workflow YAML', 'error');
    } finally {
      setAskAiPending(false);
    }
  }

  function handleRunWorkflow() {
    setRunModalOpen(true);
  }

  function handleBindScope() {
    setBindScopeModalOpen(true);
  }

  async function handleConfirmBindScope() {
    const scopeId = runScopeId.trim() || nyxid.loadSession()?.user.sub || '';
    if (!scopeId) {
      flash('Not logged in — scope ID unavailable', 'error');
      return;
    }
    try {
      setBindScopePending(true);
      const serialized = await serializeCurrentWorkflow();
      const yamlContent = serialized?.yaml || workflowMeta.yaml;
      if (!yamlContent?.trim()) {
        flash('No workflow YAML to bind', 'error');
        return;
      }
      const sid = bindServiceId.trim() || undefined;
      const result = await api.scope.bindWorkflow(scopeId, [yamlContent], workflowMeta.name.trim() || undefined, sid);
      setBindScopeModalOpen(false);
      flash(`Bound as service "${sid || 'default'}". Revision: ${result?.revisionId || 'latest'}`, 'success');
    } catch (error: any) {
      flash(error?.message || 'Bind scope failed', 'error');
    } finally {
      setBindScopePending(false);
    }
  }

  async function handleConfirmRunWorkflow() {
    try {
      if (authSession.enabled && !authSession.authenticated) {
        setRunModalOpen(false);
        flash('Sign in before running workflows', 'error');
        return;
      }

      const serialized = await serializeCurrentWorkflow();
      const errorCount = (serialized?.findings || []).filter((finding: ValidationFinding) => toFindingLevel(finding.level) === 'error').length;
      if (errorCount > 0) {
        setRunModalOpen(false);
        toggleRightDrawer('yaml');
        setStudioView('editor');
        flash('Fix YAML errors before running', 'error');
        return;
      }

      const scopeId = runScopeId.trim() || appContext.scopeId || nyxid.loadSession()?.user.sub || '';
      if (!scopeId) {
        setRunModalOpen(false);
        flash('Not logged in — scope ID unavailable', 'error');
        return;
      }

      const prompt = executionPrompt.trim();
      const yamlContent = serialized?.yaml || workflowMeta.yaml;

      setRunModalOpen(false);
      setStudioView('execution');
      setLogsCollapsed(false);
      setLogsDetached(false);
      setDraftRunEvents([]);
      setDraftRunText('');
      setDraftRunning(true);
      setSelectedExecutionId(null);
      setExecutionDetail(null);
      setExecutionTrace(null);

      const controller = new AbortController();
      draftRunAbortRef.current = controller;

      const { normalizeBackendSseFrame } = await import('./runtime/sseUtils');

      const onFrame = (frame: any) => {
        const evt = normalizeBackendSseFrame(frame);
        if (!evt) return;
        setDraftRunEvents(prev => [...prev, evt]);
        if (evt.type === 'TEXT_MESSAGE_CONTENT') {
          setDraftRunText(prev => prev + (evt.delta as string || ''));
        }
        if (evt.type === 'RUN_ERROR') {
          flash((evt.message as string) || 'Run error', 'error');
        }
      };

      try {
        await api.scope.streamDraftRun(scopeId, prompt, yamlContent ? [yamlContent] : undefined, onFrame, controller.signal);
        flash('Draft run completed', 'success');
      } catch (err: any) {
        if (err?.name !== 'AbortError') {
          flash(err?.message || 'Draft run failed', 'error');
        }
      } finally {
        setDraftRunning(false);
        draftRunAbortRef.current = null;
      }
    } catch (error: any) {
      flash(error?.message || 'Execution failed', 'error');
      setDraftRunning(false);
    }
  }

  function handleStopDraftRun() {
    draftRunAbortRef.current?.abort();
  }

  function applyExecutionDetail(detail: any) {
    const trace = buildExecutionTrace(detail);
    setSelectedExecutionId(detail?.executionId || null);
    setExecutionDetail(detail);
    setExecutionTrace(trace);
    setActiveExecutionLogIndex(trace?.defaultLogIndex ?? null);
    upsertExecutionSummary(detail);
    scheduleCanvasOverview();
  }

  async function openExecution(executionId: string) {
    try {
      const detail = await api.executions.get(executionId);
      applyExecutionDetail(detail);
      setStudioView('execution');
      if (!logsDetached) {
        setLogsCollapsed(false);
      }
    } catch (error: any) {
      flash(error?.message || 'Failed to load execution', 'error');
    }
  }

  async function handleExecutionInteraction(
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject',
  ) {
    if (!selectedExecutionId) {
      return;
    }

    const trimmedInput = executionActionInput.trim();
    if (interaction.kind === 'human_input' && !trimmedInput) {
      flash('Input is required for this step', 'error');
      return;
    }

    const pendingKey = `${selectedExecutionId}:${interaction.stepId}:${action}`;
    setExecutionActionPendingKey(pendingKey);

    try {
      const detail = await api.executions.resume(selectedExecutionId, {
        runId: interaction.runId,
        stepId: interaction.stepId,
        approved: interaction.kind === 'human_input' ? true : action === 'approve',
        userInput: trimmedInput || null,
        suspensionType: interaction.kind,
      });
      applyExecutionDetail(detail);
      setExecutionActionInput('');
      setStudioView('execution');
      setLogsCollapsed(false);
      flash(
        interaction.kind === 'human_approval'
          ? action === 'approve'
            ? 'Approval sent'
            : 'Rejection sent'
          : 'Input submitted',
        'success',
      );
    } catch (error: any) {
      flash(error?.message || 'Failed to resume execution', 'error');
    } finally {
      setExecutionActionPendingKey('');
    }
  }

  async function handleStopExecution() {
    if (!selectedExecutionId || !executionCanStop) {
      return;
    }

    setExecutionStopPending(true);
    try {
      const detail = await api.executions.stop(selectedExecutionId, {
        reason: 'user requested stop',
      });
      applyExecutionDetail(detail);
      flash('Stop requested', 'info');
    } catch (error: any) {
      flash(error?.message || 'Failed to stop execution', 'error');
    } finally {
      setExecutionStopPending(false);
    }
  }


  async function clearConnectorDraft() {
    await api.connectors.deleteDraft();
    hydrateConnectorDraft(null);
  }

  async function persistConnectorDraftState(nextDraft: ConnectorState | null) {
    if (!hasConnectorDraftContent(nextDraft)) {
      await clearConnectorDraft();
      return;
    }

    const response = await api.connectors.saveDraft({
      draft: toConnectorPayload(nextDraft!),
    });
    hydrateConnectorDraft(response);
  }

  async function closeConnectorModal() {
    const draft = connectorDraft;
    setConnectorModalOpen(false);

    try {
      await persistConnectorDraftState(draft);
      if (hasConnectorDraftContent(draft)) {
        flash('Connector draft saved', 'info');
      }
    } catch (error: any) {
      flash(error?.message || 'Failed to save connector draft', 'error');
    }
  }

  async function handleSubmitConnectorDraft() {
    if (!connectorDraft) {
      return;
    }

    const type = connectorDraft.type || 'http';
    const connectorName = connectorDraft.name.trim() || createUniqueConnectorName(connectors, type);
    const nextConnector: ConnectorState = {
      ...connectorDraft,
      key: `connector_${crypto.randomUUID()}`,
      name: connectorName,
      type,
    };

    setConnectors(prev => [nextConnector, ...prev]);
    setSelectedConnectorKey(nextConnector.key);
    openExplorerPage();
    setConnectorDraft(null);
    setConnectorModalOpen(false);

    try {
      await clearConnectorDraft();
    } catch (error: any) {
      flash(error?.message || 'Failed to clear connector draft', 'error');
    }

    flash(`Connector ${connectorName} added`, 'success');
  }

  async function clearRoleDraft() {
    await api.roles.deleteDraft();
    hydrateRoleDraft(null);
  }

  async function persistRoleDraftState(nextDraft: RoleState | null) {
    if (!hasRoleDraftContent(nextDraft)) {
      await clearRoleDraft();
      return;
    }

    const response = await api.roles.saveDraft({
      draft: toRolePayload(nextDraft!),
    });
    hydrateRoleDraft(response);
  }

  function openRoleModal(target: RoleModalTarget = 'catalog') {
    setRoleModalTarget(target);
    setRoleDraft(prev => prev || (target === 'workflow' ? createWorkflowRoleDraft(settingsState) : createEmptyRoleDraft()));
    setRoleModalOpen(true);
  }

  async function closeRoleModal() {
    const draft = roleDraft;
    setRoleModalOpen(false);

    try {
      await persistRoleDraftState(draft);
      if (hasRoleDraftContent(draft)) {
        flash('Role draft saved', 'info');
      }
    } catch (error: any) {
      flash(error?.message || 'Failed to save role draft', 'error');
    }
  }

  async function handleSubmitRoleDraft() {
    if (!roleDraft) {
      return;
    }

    const targetRoles = roleModalTarget === 'workflow' ? roles : roleCatalog;
    const roleId = roleDraft.id.trim() || createUniqueRoleId(targetRoles, roleDraft.name || 'role');
    const roleName = roleDraft.name.trim() || roleId;
    const nextRole = createRoleState(targetRoles.length + 1, {
      id: roleId,
      name: roleName,
      systemPrompt: roleDraft.systemPrompt,
      provider: roleDraft.provider,
      model: roleDraft.model,
      connectorsText: roleDraft.connectorsText,
    });

    if (roleModalTarget === 'workflow') {
      const normalizedRoleId = roleId.trim().toLowerCase();
      const existing = roles.find(role => role.id.trim().toLowerCase() === normalizedRoleId && normalizedRoleId);
      const nextRoles = existing
        ? roles.map(role => role.key === existing.key ? {
            ...role,
            id: nextRole.id,
            name: nextRole.name,
            systemPrompt: nextRole.systemPrompt,
            provider: nextRole.provider,
            model: nextRole.model,
            connectorsText: nextRole.connectorsText,
          } : role)
        : [nextRole, ...roles];

      setRoles(nextRoles);
      setExpandedWorkflowRoleKey(existing?.key || nextRole.key);
      setRightPanelTab('roles');
      setRightPanelOpen(true);
      markDirty();
    } else {
      setRoleCatalog(prev => [nextRole, ...prev]);
      setSelectedCatalogRoleKey(nextRole.key);
      openExplorerPage();
    }

    setRoleDraft(null);
    setRoleModalOpen(false);

    try {
      await clearRoleDraft();
    } catch (error: any) {
      flash(error?.message || 'Failed to clear role draft', 'error');
    }

    flash(roleModalTarget === 'workflow' ? `Role ${roleId} added to workflow` : `Role ${roleId} added`, 'success');
  }

  function updateWorkflowRole(roleKey: string, updater: (role: RoleState) => RoleState) {
    setRoles(prev => prev.map(role => (role.key === roleKey ? updater(role) : role)));
    markDirty();
  }

  function applyCatalogRoleToWorkflow(catalogRoleKey: string) {
    const catalogRole = roleCatalog.find(role => role.key === catalogRoleKey);
    if (!catalogRole) {
      return;
    }

    const normalizedRoleId = catalogRole.id.trim().toLowerCase();
    const existing = roles.find(role => role.id.trim().toLowerCase() === normalizedRoleId && normalizedRoleId);
    const nextRoles = existing
      ? roles.map(role => role.key === existing.key ? {
          ...role,
          id: catalogRole.id,
          name: catalogRole.name,
          systemPrompt: catalogRole.systemPrompt,
          provider: catalogRole.provider,
          model: catalogRole.model,
          connectorsText: catalogRole.connectorsText,
        } : role)
      : [...roles, createRoleState(roles.length + 1, {
          id: catalogRole.id,
          name: catalogRole.name,
          systemPrompt: catalogRole.systemPrompt,
          provider: catalogRole.provider,
          model: catalogRole.model,
          connectorsText: catalogRole.connectorsText,
        })];

    setRoles(nextRoles);
    setExpandedWorkflowRoleKey(existing?.key || nextRoles[0]?.key || null);

    setRightPanelTab('roles');
    setRightPanelOpen(true);
    markDirty();
    flash(existing ? `Role ${catalogRole.id} refreshed` : `Role ${catalogRole.id} added`, 'success');
  }

  function copyWorkflowRoleToCatalog(roleKey: string) {
    const sourceRole = roles.find(role => role.key === roleKey);
    if (!sourceRole) {
      return;
    }

    const normalizedRoleId = sourceRole.id.trim().toLowerCase();
    const existing = roleCatalog.find(role => role.id.trim().toLowerCase() === normalizedRoleId && normalizedRoleId);
    const draft = existing
      ? null
      : createRoleState(roleCatalog.length + 1, {
          id: sourceRole.id || createUniqueRoleId(roleCatalog),
          name: sourceRole.name,
          systemPrompt: sourceRole.systemPrompt,
          provider: sourceRole.provider,
          model: sourceRole.model,
          connectorsText: sourceRole.connectorsText,
        });

    const nextCatalog = existing
      ? roleCatalog.map(role => role.key === existing.key ? {
          ...role,
          id: sourceRole.id,
          name: sourceRole.name,
          systemPrompt: sourceRole.systemPrompt,
          provider: sourceRole.provider,
          model: sourceRole.model,
          connectorsText: sourceRole.connectorsText,
        } : role)
      : [draft!, ...roleCatalog];

    setRoleCatalog(nextCatalog);
    setSelectedCatalogRoleKey(existing?.key || draft?.key || null);
    openExplorerPage();
    flash('Role copied to catalog. Save to persist.', 'info');
  }

  async function handleSaveSettings() {
    try {
      const nyxIdProvider = settingsState.providers.find(p => usesFixedProviderName(p.providerType));
      const runtimeConfig = {
        runtimeMode: settingsState.runtimeMode,
        localRuntimeBaseUrl: normalizeRuntimeUrl(settingsState.localRuntimeUrl, DEFAULT_LOCAL_RUNTIME_URL),
        remoteRuntimeBaseUrl: normalizeRuntimeUrl(settingsState.remoteRuntimeUrl, DEFAULT_REMOTE_RUNTIME_URL),
      };
      const activeRuntimeUrl = getActiveRuntimeUrl(settingsState);

      // Save runtime selection and defaultModel to chrono-storage (userConfig)
      const nyxIdModel = nyxIdProvider?.model.trim() || '';
      const userConfigSaveAll = api.userConfig.save({
        defaultModel: nyxIdModel,
        preferredLlmRoute: normalizeUserLlmRoute(userConfigState.preferredLlmRoute),
        ...runtimeConfig,
      }).catch(() => {});

      const response = await api.settings.save({
        appearanceTheme: settingsState.appearanceTheme,
        colorMode: settingsState.colorMode,
        defaultProviderName: settingsState.defaultProviderName || null,
        providers: settingsState.providers.map(provider => ({
          providerName: provider.providerName.trim(),
          providerType: provider.providerType.trim(),
          model: provider.model.trim(),
          endpoint: provider.endpoint.trim(),
          apiKey: provider.apiKey,
        })),
      });

      await userConfigSaveAll;

      // Update the local proxy target so subsequent requests go to the new URL
      await fetch('/api/_proxy/runtime-url', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ runtimeBaseUrl: activeRuntimeUrl }),
      }).catch(() => {});

      hydrateSettings(response, {
        defaultModel: nyxIdModel,
        preferredLlmRoute: userConfigState.preferredLlmRoute,
        ...runtimeConfig,
      });
      setWorkspaceSettings(prev => ({
        ...prev,
        runtimeBaseUrl: activeRuntimeUrl,
      }));
      flash('Settings saved', 'success');
    } catch (error: any) {
      flash(error?.message || 'Failed to save settings', 'error');
    }
  }

  async function toggleColorMode() {
    const previousColorMode = settingsState.colorMode;
    const nextColorMode = settingsState.colorMode === 'dark' ? 'light' : 'dark';
    setSettingsState(prev => ({
      ...prev,
      colorMode: nextColorMode,
    }));

    try {
      await api.settings.save({
        colorMode: nextColorMode,
      });
    } catch (error: any) {
      setSettingsState(prev => ({ ...prev, colorMode: previousColorMode }));
      flash(error?.message || 'Failed to switch theme mode', 'error');
    }
  }

  async function handleTestRuntime(targetUrl?: string) {
    const urlToTest = targetUrl ?? getActiveRuntimeUrl(settingsState);
    try {
      setRuntimeTestState({
        status: 'testing',
        message: `Testing ${urlToTest} ...`,
      });

      const response = await api.settings.testRuntime({
        runtimeBaseUrl: urlToTest,
      });

      setRuntimeTestState({
        status: response?.reachable ? 'success' : 'error',
        message: response?.message || (response?.reachable ? `Connected successfully (${urlToTest}).` : `Failed to reach ${urlToTest}.`),
      });
    } catch (error: any) {
      setRuntimeTestState({
        status: 'error',
        message: error?.message || `Failed to reach ${urlToTest}.`,
      });
    }
  }

  function updateSelectedNode(updater: (node: Node<StudioNodeData>) => Node<StudioNodeData>) {
    if (!selectedNodeId) {
      return;
    }

    setNodes(prev => prev.map(node => (node.id === selectedNodeId ? updater(node) : node)));
    markDirty();
  }

  function handleNodesChange(changes: NodeChange[]) {
    setNodes(prev => applyNodeChanges(changes, prev) as Array<Node<StudioNodeData>>);
    const moved = changes.some(change => change.type === 'position');
    if (moved) {
      markDirty();
    }
  }

  function handleEdgesChange(changes: EdgeChange[]) {
    setEdges(prev => applyEdgeChanges(changes, prev) as Array<Edge<StudioEdgeData>>);
    if (changes.length > 0) {
      markDirty();
    }
  }

  function handleConnect(connection: Connection) {
    if (!connection.source || !connection.target) {
      return;
    }

    const sourceNode = nodes.find(node => node.id === connection.source);
    let branchLabel: string | undefined;
    if (sourceNode?.data.stepType === 'conditional') {
      branchLabel = getNextConditionalBranchLabel(connection.source, edges);
    } else if (sourceNode?.data.stepType === 'switch') {
      branchLabel = window.prompt('Branch label', '_default') || '_default';
    }

    setEdges(prev => [...prev, createEdge(connection.source, connection.target, branchLabel)]);
    markDirty();
  }

  function handleAddNode(stepType: string, connectorName?: string) {
    const nextNode = createNode(
      stepType,
      pendingAddPosition,
      nodes,
      roles,
      connectors,
      connectorName ? {
        parameters: {
          connector: connectorName,
        },
      } : {},
    );

    if (connectorName) {
      applyConnectorDefaults(nextNode.data.parameters, connectorName, connectors);
    }

    setNodes(prev => [...prev, nextNode]);
    setSelectedNodeId(nextNode.id);
    setPaletteOpen(false);
    setCanvasMenu({ open: false, x: 0, y: 0 });
    setRightPanelTab('node');
    setRightPanelOpen(true);
    markDirty();
  }

  function handlePaneContextMenu(event: any) {
    if (studioView !== 'editor') {
      return;
    }

    event.preventDefault();
    if (!reactFlowInstanceRef.current) {
      return;
    }

    const position = reactFlowInstanceRef.current.screenToFlowPosition({
      x: event.clientX,
      y: event.clientY,
    });
    setPendingAddPosition(position);
    setCanvasMenu({
      open: true,
      x: event.clientX,
      y: event.clientY,
    });
  }

  function handleDeleteNode(nodeId: string) {
    setNodes(prev => prev.filter(node => node.id !== nodeId));
    setEdges(prev => prev.filter(edge => edge.source !== nodeId && edge.target !== nodeId));
    setSelectedNodeId(prev => (prev === nodeId ? null : prev));
    markDirty();
  }

  function handleCopyYaml() {
    navigator.clipboard?.writeText(workflowMeta.yaml || '');
    flash('YAML copied', 'info');
  }

  function addRole() {
    openRoleModal('workflow');
  }

  function removeRole(roleKey: string) {
    const roleToRemove = roles.find(role => role.key === roleKey);
    setRoles(prev => prev.filter(role => role.key !== roleKey));
    setNodes(prev => prev.map(node => {
      if (node.data.targetRole && roleToRemove?.id === node.data.targetRole) {
        return {
          ...node,
          data: {
            ...node.data,
            targetRole: '',
          },
        };
      }
      return node;
    }));
    markDirty();
  }

  function toggleRoleConnector(roleKey: string, connectorName: string) {
    setRoles(prev => prev.map(role => {
      if (role.key !== roleKey) {
        return role;
      }

      const values = new Set(splitLines(role.connectorsText));
      if (values.has(connectorName)) {
        values.delete(connectorName);
      } else {
        values.add(connectorName);
      }

      return {
        ...role,
        connectorsText: Array.from(values).join('\n'),
      };
    }));
    markDirty();
  }


  const filteredPrimitiveCategories = PRIMITIVE_CATEGORIES.map(category => ({
    ...category,
    items: category.items.filter(item => {
      const query = paletteSearch.trim().toLowerCase();
      return !query || item.toLowerCase().includes(query) || category.label.toLowerCase().includes(query);
    }),
  })).filter(category => category.items.length > 0);

  const filteredPaletteConnectors = connectors.filter(connector => {
    const query = paletteSearch.trim().toLowerCase();
    if (!query) {
      return true;
    }

    return [
      connector.name,
      connector.type,
      'connector',
      'configured connectors',
    ].join(' ').toLowerCase().includes(query);
  });

  const filteredWorkflowCatalogRoles = roleCatalog.filter(role => matchesRoleQuery(role, workflowRoleSearch));
  const filteredWorkflowRoles = roles.filter(role => matchesRoleQuery(role, workflowRoleSearch));

  const selectedNodeOutgoingEdges = selectedNode
    ? edges.filter(edge => edge.source === selectedNode.id)
    : [];

  const activeWorkflowDirectory = workspaceSettings.directories.find(directory => directory.directoryId === workflowMeta.directoryId) || null;
  const errorFindings = workflowMeta.findings.filter(finding => toFindingLevel(finding.level) === 'error');

  const executionSummaryLabel = executionDetail
    ? `${formatDateTime(executionDetail.startedAtUtc)} · ${executionDetail.status}`
    : currentWorkflowExecutions.length > 0
      ? `${currentWorkflowExecutions.length} runs`
      : 'No runs yet';
  const executionLogsCollapsed = !isExecutionLogsPopout && (logsCollapsed || logsDetached);
  const executionLogsToggleLabel = logsDetached ? 'Viewing in new window' : executionSummaryLabel;

  const selectedIconSurfaceStyle = {
    background: 'var(--accent-icon-surface)',
    color: 'var(--accent)',
  } as const;
  const authProviderLabel = authSession.providerDisplayName || 'NyxID';
  const authAccountLabel = authSession.name || authSession.email || authProviderLabel;
  const authAccountSecondaryLabel = authSession.email && authSession.email !== authAccountLabel
    ? authSession.email
    : `Connected with ${authProviderLabel}`;
  const authAccountInitials = authAccountLabel
    .split(/[\s@._-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map(part => part[0]?.toUpperCase() || '')
    .join('') || 'N';
  const authGuestStatusMessage = authSession.errorMessage
    || (authSession.enabled
      ? 'Sign in to load your workspace.'
      : `${authProviderLabel} sign-in is currently unavailable.`);
  const authStatusWidget = authSession.authenticated ? (
    <div className="header-auth-chip">
      {authSession.picture ? (
        <img
          src={authSession.picture}
          alt={authAccountLabel}
          className="header-auth-avatar-image"
        />
      ) : (
        <div className="header-auth-avatar">
          {authAccountInitials}
        </div>
      )}
      <div className="header-auth-copy">
        <div className="header-auth-label">{authAccountLabel}</div>
        <div className="header-auth-meta">{authAccountSecondaryLabel}</div>
      </div>
      <button
        onClick={() => { nyxid.clearSession(); window.location.reload(); }}
        className="panel-icon-button header-auth-logout"
        title="Sign out from NyxID."
      >
        <LogOut size={14} />
      </button>
    </div>
  ) : (
    <div className="header-auth-guest">
      <div className="header-auth-guest-copy">
        <div className="header-auth-label">{authProviderLabel}</div>
        <div className="header-auth-meta">
          {authGuestStatusMessage}
        </div>
      </div>
      <button onClick={() => nyxid.loginWithRedirect(window.location.pathname)} className="solid-action header-auth-link !no-underline">
        <Shield size={14} /> Sign in
      </button>
    </div>
  );

  function renderExecutionLogsSection(fullscreen = false) {
    const sectionCollapsed = !fullscreen && executionLogsCollapsed;
    const sectionClassName = [
      'execution-logs',
      sectionCollapsed ? 'collapsed' : '',
      fullscreen ? 'execution-logs-fullscreen' : '',
    ].filter(Boolean).join(' ');
    const popoutButtonVisible = !fullscreen && Boolean(selectedExecutionId);

    return (
      <section className={sectionClassName}>
        <div className="execution-logs-header">
          <div>
            <div className="text-[11px] text-gray-400 uppercase tracking-[0.16em]">Execution</div>
            <div className="text-[14px] font-semibold text-gray-800">
              {fullscreen ? 'Execution logs' : 'Logs'}
            </div>
          </div>
          <div className="execution-logs-header-actions">
            {executionTrace?.logs?.length ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-copy-action ${copiedAllExecutionLogs ? 'active' : ''}`}
                title="Copy all execution logs."
                aria-label="Copy all execution logs."
                data-tooltip="Copy logs"
                onClick={() => void handleCopyAllExecutionLogs()}
              >
                {copiedAllExecutionLogs ? <Check size={14} /> : <Copy size={14} />}
              </button>
            ) : null}
            {popoutButtonVisible ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-window-action ${logsDetached ? 'active' : ''}`}
                title="Pop out"
                aria-label="Pop out execution logs."
                data-tooltip="Pop out"
                onClick={handlePopOutExecutionLogs}
              >
                <Maximize2 size={14} />
              </button>
            ) : null}
            {fullscreen ? (
              <button
                type="button"
                className="panel-icon-button execution-logs-window-action"
                title="Close window."
                aria-label="Close logs window."
                data-tooltip="Close window"
                onClick={() => window.close()}
              >
                <X size={14} />
              </button>
            ) : (
              <button
                type="button"
                onClick={handleToggleExecutionLogsPanel}
                className="execution-logs-collapse-action"
                aria-expanded={!sectionCollapsed}
                data-tooltip={logsDetached ? 'Focus logs window.' : sectionCollapsed ? 'Expand logs.' : 'Collapse logs.'}
              >
                <span className="text-[12px] text-gray-500">{executionLogsToggleLabel}</span>
                {logsDetached ? (
                  <Maximize2 size={15} />
                ) : (
                  <ChevronDown size={16} className={`execution-logs-collapse-icon ${sectionCollapsed ? 'collapsed' : ''}`} />
                )}
              </button>
            )}
          </div>
        </div>

        {!sectionCollapsed ? (
          <div className="execution-logs-body">
            <div className="execution-runs-list">
              {currentWorkflowExecutions.length === 0 ? (
                <EmptyPanel
                  icon={<Play size={18} className="text-gray-300" />}
                  title="No runs"
                  copy="Run the current workflow to inspect execution."
                />
              ) : currentWorkflowExecutions.map(execution => (
                <button
                  key={execution.executionId}
                  onClick={() => void openExecution(execution.executionId)}
                  className={`execution-run-card ${selectedExecutionId === execution.executionId ? 'active' : ''}`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <div className="text-[13px] font-semibold text-gray-800">{formatDateTime(execution.startedAtUtc)}</div>
                    <span className="text-[10px] uppercase tracking-wide text-gray-400">{execution.status}</span>
                  </div>
                  <div className="text-[11px] text-gray-400 mt-1">{formatDurationBetween(execution.startedAtUtc, execution.completedAtUtc)}</div>
                </button>
              ))}
            </div>

            <div className="execution-log-stream">
              <div className="execution-log-list">
                {/* Live draft-run stream output — prioritize over historical logs */}
                {(draftRunning || draftRunEvents.length > 0) ? (
                  <>
                    {draftRunText && (
                      <div className="execution-log-card tone-success" style={{ marginBottom: 8 }}>
                        <div className="execution-log-card-head">
                          <div className="text-[12px] font-semibold text-gray-800">Response</div>
                        </div>
                        <pre className="whitespace-pre-wrap text-[13px] text-gray-700 mt-2 leading-6">{draftRunText}</pre>
                      </div>
                    )}
                    {draftRunEvents.map((evt, index) => (
                      <div
                        key={`draft-${index}`}
                        className={`execution-log-card tone-${evt.type === 'RUN_ERROR' ? 'error' : evt.type === 'RUN_FINISHED' ? 'success' : 'info'}`}
                      >
                        <div className="execution-log-card-head">
                          <div className="text-[12px] font-semibold text-gray-800">
                            {evt.type === 'CUSTOM' ? (evt.name as string || 'CUSTOM') : evt.type}
                          </div>
                          <div className="execution-log-card-meta">
                            <div className="text-[11px] text-gray-400">{evt.timestamp ? new Date(evt.timestamp).toLocaleTimeString() : ''}</div>
                          </div>
                        </div>
                        {evt.type === 'TEXT_MESSAGE_CONTENT' && evt.delta ? (
                          <div className="execution-log-card-preview">{evt.delta as string}</div>
                        ) : evt.type === 'STEP_STARTED' || evt.type === 'STEP_FINISHED' ? (
                          <div className="text-[11px] text-gray-400 mt-1">Step: {evt.stepName as string}</div>
                        ) : evt.type === 'RUN_ERROR' ? (
                          <div className="text-[11px] text-red-600 mt-1">{evt.message as string}</div>
                        ) : evt.type === 'CUSTOM' && evt.value ? (
                          <pre className="text-[11px] text-gray-500 mt-1 whitespace-pre-wrap max-h-[120px] overflow-auto">{typeof evt.value === 'string' ? evt.value : JSON.stringify(evt.value, null, 2)}</pre>
                        ) : null}
                      </div>
                    ))}
                    {draftRunning && !draftRunEvents.length && (
                      <div className="execution-log-card tone-info">
                        <div className="text-[12px] text-gray-500">Waiting for events...</div>
                      </div>
                    )}
                  </>
                ) : executionTrace?.logs?.length ? executionTrace.logs.map((log, index) => (
                  <button
                    key={`${log.timestamp}-${index}`}
                    onClick={() => void handleExecutionLogClick(log, index)}
                    className={`execution-log-card tone-${log.tone} ${activeExecutionLogIndex === index ? 'active' : ''}`}
                    title="Click to copy this log."
                  >
                    <div className="execution-log-card-head">
                      <div className="text-[12px] font-semibold text-gray-800">{log.title}</div>
                      <div className="execution-log-card-meta">
                        {copiedExecutionLogIndex === index ? (
                          <span className="execution-log-card-copied">
                            <Check size={12} /> Copied
                          </span>
                        ) : null}
                        <div className="text-[11px] text-gray-400">{formatDateTime(log.timestamp)}</div>
                      </div>
                    </div>
                    {log.meta ? <div className="text-[11px] text-gray-400 mt-1">{log.meta}</div> : null}
                    {log.previewText ? <div className="execution-log-card-preview">{log.previewText}</div> : null}
                  </button>
                )) : (
                  <EmptyPanel
                    icon={<FileText size={18} className="text-gray-300" />}
                    title="No logs yet"
                    copy="Pick a run to inspect frames and step transitions."
                  />
                )}
              </div>

              {activeExecutionInteraction ? (
                <div className="execution-action-panel">
                  <div className="execution-action-intro">
                    <div className="flex items-start justify-between gap-3">
                      <div>
                        <div className="text-[11px] text-gray-400 uppercase tracking-[0.14em]">Action required</div>
                        <div className="text-[15px] font-semibold text-gray-800 mt-1">
                          {activeExecutionInteraction.kind === 'human_approval' ? 'Human approval' : 'Human input'}
                        </div>
                        <div className="execution-action-subtitle">
                          {activeExecutionInteraction.kind === 'human_approval'
                            ? 'Review the pending gate and approve or reject the run.'
                            : 'Provide the missing value to resume this workflow step.'}
                        </div>
                      </div>
                      <span className="execution-action-badge">
                        {activeExecutionInteraction.stepId}
                      </span>
                    </div>

                    <div className="execution-action-meta">
                      <span className="execution-action-chip">
                        <User size={12} /> Human required
                      </span>
                      {activeExecutionInteraction.variableName ? (
                        <span className="execution-action-chip">
                          stores as {activeExecutionInteraction.variableName}
                        </span>
                      ) : null}
                      {activeExecutionInteraction.timeoutSeconds ? (
                        <span className="execution-action-chip">
                          timeout {activeExecutionInteraction.timeoutSeconds}s
                        </span>
                      ) : null}
                    </div>
                  </div>

                  {activeExecutionInteraction.prompt ? (
                    <div className="execution-action-block">
                      <div className="execution-action-block-label">Prompt</div>
                      <div className="execution-action-prompt">
                        {activeExecutionInteraction.prompt}
                      </div>
                    </div>
                  ) : null}

                  <div className="execution-action-block">
                    <div className="execution-action-field-head">
                      <label className="field-label">
                        {activeExecutionInteraction.kind === 'human_approval'
                          ? 'Feedback'
                          : activeExecutionInteraction.variableName || 'Input'}
                      </label>
                      <span className={`execution-action-requirement ${activeExecutionInteraction.kind === 'human_approval' ? 'optional' : 'required'}`}>
                        {activeExecutionInteraction.kind === 'human_approval' ? 'Optional note' : 'Required'}
                      </span>
                    </div>
                    <div className="execution-action-helper">
                      {activeExecutionInteraction.kind === 'human_approval'
                        ? 'Add context for the operator if needed, then approve or reject this gate.'
                        : activeExecutionInteraction.variableName
                          ? `The submitted value will resume the run and be available as ${activeExecutionInteraction.variableName}.`
                          : 'This response resumes the workflow immediately.'}
                    </div>
                    <textarea
                      ref={executionActionInputRef}
                      className="panel-textarea execution-action-textarea mt-1"
                      value={executionActionInput}
                      placeholder={activeExecutionInteraction.kind === 'human_approval'
                        ? 'Optional feedback'
                        : 'Enter the value to continue this step'}
                      onChange={event => setExecutionActionInput(event.target.value)}
                    />
                  </div>

                  <div className="execution-action-footer">
                    {activeExecutionInteraction.kind === 'human_approval' ? (
                      <>
                        <button
                          type="button"
                          className="ghost-action execution-danger-action"
                          disabled={executionActionPendingKey === `${executionActionKeyBase}:reject`}
                          onClick={() => void handleExecutionInteraction(activeExecutionInteraction, 'reject')}
                        >
                          <X size={14} /> {executionActionPendingKey === `${executionActionKeyBase}:reject` ? 'Rejecting...' : 'Reject'}
                        </button>
                        <button
                          type="button"
                          className="solid-action"
                          disabled={executionActionPendingKey === `${executionActionKeyBase}:approve`}
                          onClick={() => void handleExecutionInteraction(activeExecutionInteraction, 'approve')}
                        >
                          <Check size={14} /> {executionActionPendingKey === `${executionActionKeyBase}:approve` ? 'Approving...' : 'Approve'}
                        </button>
                      </>
                    ) : (
                      <button
                        type="button"
                        className="solid-action"
                        disabled={executionActionPendingKey === `${executionActionKeyBase}:submit`}
                        onClick={() => void handleExecutionInteraction(activeExecutionInteraction, 'submit')}
                      >
                        <Play size={14} /> {executionActionPendingKey === `${executionActionKeyBase}:submit` ? 'Submitting...' : 'Submit input'}
                      </button>
                    )}
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        ) : null}
      </section>
    );
  }



  if (authSession.loading) {
    return <AppLoadingScreen />;
  }

  if (authSession.enabled && !authSession.authenticated) {
    return (
      <AppAuthenticationGate
        providerDisplayName={authSession.providerDisplayName}
        loginUrl={authSession.loginUrl}
        errorMessage={authSession.errorMessage}
      />
    );
  }

  if (isExecutionLogsPopout) {
    return (
      <div
        className="studio-shell execution-logs-popout-shell relative flex min-h-screen w-full overflow-hidden bg-[#F2F1EE] text-gray-800"
        data-appearance={settingsState.appearanceTheme || 'blue'}
        data-color-mode={settingsState.colorMode || 'light'}
      >
        {renderExecutionLogsSection(true)}
      </div>
    );
  }

  return (
    <div
      className="studio-shell relative flex h-screen w-full overflow-hidden bg-[#F2F1EE] text-gray-800"
      data-appearance={settingsState.appearanceTheme || 'blue'}
      data-color-mode={settingsState.colorMode || 'light'}
      onClick={() => {
        if (canvasMenu.open) {
          setCanvasMenu({ open: false, x: 0, y: 0 });
        }
      }}
    >
      <div className="app-auth-anchor">
        {authStatusWidget}
      </div>

      <aside className="studio-rail">
        <div className="flex flex-col items-center gap-3">
          <div className="flex h-11 w-11 items-center justify-center overflow-hidden rounded-[14px] border border-black/10 bg-[#18181B]">
            <AevatarBrandMark size={44} />
          </div>

          {/* ── Service Sources ── */}
          <div className="w-8 border-t border-[#E6E3DE] my-0.5" />
          <RailButton
            active={workspacePage === 'studio'}
            label="Workflow Studio"
            icon={<WorkflowIcon size={18} />}
            onClick={openStudioPage}
          />
          {appContext.scriptsEnabled ? (
            <RailButton
              active={workspacePage === 'scripts'}
              label="Script Studio"
              icon={<Code2 size={18} />}
              onClick={openScriptsPage}
            />
          ) : null}
          <RailButton
            active={workspacePage === 'gagents'}
            label="GAgent Types"
            icon={<Bot size={18} />}
            onClick={() => setWorkspacePage('gagents')}
          />

          {/* ── Storage ── */}
          <div className="w-8 border-t border-[#E6E3DE] my-0.5" />
          <RailButton
            active={workspacePage === 'explorer'}
            label="Explorer"
            icon={<FolderPlus size={18} />}
            onClick={() => openExplorerPage()}
          />
        </div>

        <div className="mt-auto flex flex-col items-center gap-3">
          <RailButton
            active={workspacePage === 'console'}
            label="Console"
            icon={<Globe size={18} />}
            onClick={() => setWorkspacePage('console')}
          />
          <RailButton
            active={settingsState.colorMode === 'dark'}
            label={settingsState.colorMode === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            icon={settingsState.colorMode === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
            onClick={() => { void toggleColorMode(); }}
          />
          <RailButton
            active={workspacePage === 'settings'}
            label="Settings"
            icon={<Settings size={18} />}
            onClick={() => openSettingsPage('runtime')}
          />
        </div>
      </aside>

      <main className="flex-1 min-w-0 flex flex-col">
        {workspacePage === 'scripts' ? (
          <ScriptsStudio
            appContext={{
              hostMode: appContext.hostMode,
              scopeId: appContext.scopeId,
              scopeResolved: appContext.scopeResolved,
              scriptStorageMode: appContext.scriptStorageMode,
              scriptsEnabled: appContext.scriptsEnabled,
              scriptContract: appContext.scriptContract,
            }}
            onFlash={flash}
          />
        ) : workspacePage === 'explorer' ? (
          <ConfigExplorerPage
            scopeId={appContext.scopeId || nyxid.loadSession()?.user.sub || ''}
            flash={flash}
            initialFolder={explorerInitialFolder}
            onInitialFolderConsumed={() => setExplorerInitialFolder(null)}
            onOpenWorkflowInStudio={(workflowId: string) => { void openWorkflow(workflowId); }}
            onOpenScriptInStudio={() => { setWorkspacePage('scripts'); }}
          />
        ) : workspacePage === 'gagents' ? (
          <GAgentPage />
        ) : workspacePage === 'console' ? (
          <ScopePage />
        ) : workspacePage === 'settings' ? (
          <section className="flex-1 min-h-0 bg-[#ECEAE6] p-6">
            <div className="h-full min-h-0 overflow-hidden rounded-[38px] border border-[#E6E3DE] bg-white/96 shadow-[0_26px_64px_rgba(17,24,39,0.08)] grid grid-cols-[260px_minmax(0,1fr)]">
              <aside className="settings-sidebar">
                <button
                  onClick={closeSettingsPage}
                  className="inline-flex items-center gap-2 text-[13px] text-gray-400 hover:text-gray-600"
                >
                  <ChevronLeft size={16} />
                  Back to workspace
                </button>

                <div className="mt-8 space-y-2">
                  <SettingsNavButton
                    active={settingsSection === 'runtime'}
                    icon={<Globe size={16} />}
                    title="Runtime"
                    description="Base URL and connectivity"
                    onClick={() => setSettingsSection('runtime')}
                  />
                  <SettingsNavButton
                    active={settingsSection === 'cloud-config'}
                    icon={<Database size={16} />}
                    title="LLM"
                    description="Per-user settings on NyxID"
                    onClick={() => setSettingsSection('cloud-config')}
                  />
                  <SettingsNavButton
                    active={settingsSection === 'skills'}
                    icon={<Wrench size={16} />}
                    title="Skills"
                    description="Ornn skill platform"
                    onClick={() => setSettingsSection('skills')}
                  />
                  <SettingsNavButton
                    active={settingsSection === 'appearance'}
                    icon={<Palette size={16} />}
                    title="Appearance"
                    description="Studio theme and accents"
                    onClick={() => setSettingsSection('appearance')}
                  />
                </div>
              </aside>

              <div className="min-h-0 overflow-y-auto px-10 py-10">
                {settingsSection === 'runtime' ? (
                  <div className="max-w-[920px] space-y-8">
                    <div>
                      <div className="panel-eyebrow">Settings</div>
                      <div className="panel-title">Runtime</div>
                    </div>

                    <div className="settings-section-card space-y-5">
                      <div>
                        <label className="field-label">Runtime Target</label>
                        <div className="mt-2 inline-flex rounded-[14px] bg-[#F2F1EE] p-1">
                          <button
                            onClick={() => {
                              setSettingsState(prev => ({ ...prev, runtimeMode: 'local' }));
                              setRuntimeTestState({ status: 'idle', message: '' });
                            }}
                            className={`px-3 py-1.5 rounded-[10px] text-[12px] font-semibold transition-colors ${
                              settingsState.runtimeMode === 'local'
                                ? 'bg-white text-gray-800 shadow-sm'
                                : 'text-gray-500 hover:text-gray-700'
                            }`}
                          >
                            Local
                          </button>
                          <button
                            onClick={() => {
                              setSettingsState(prev => ({ ...prev, runtimeMode: 'remote' }));
                              setRuntimeTestState({ status: 'idle', message: '' });
                            }}
                            className={`px-3 py-1.5 rounded-[10px] text-[12px] font-semibold transition-colors ${
                              settingsState.runtimeMode === 'remote'
                                ? 'bg-white text-gray-800 shadow-sm'
                                : 'text-gray-500 hover:text-gray-700'
                            }`}
                          >
                            Remote
                          </button>
                        </div>
                        <div className="text-[12px] text-gray-400 mt-2">
                          {settingsState.runtimeMode === 'local'
                            ? 'Default local runtime is http://127.0.0.1:5080.'
                            : 'Use a remote runtime when you need a shared or hosted backend.'}
                        </div>
                      </div>

                      <div>
                        <label className="field-label">
                          {settingsState.runtimeMode === 'local' ? 'Local Runtime URL' : 'Remote Runtime URL'}
                        </label>
                        <input
                          className="panel-input mt-1"
                          value={settingsState.runtimeMode === 'local' ? settingsState.localRuntimeUrl : settingsState.remoteRuntimeUrl}
                          onChange={event => {
                            const value = event.target.value;
                            setSettingsState(prev => prev.runtimeMode === 'local'
                              ? { ...prev, localRuntimeUrl: value }
                              : { ...prev, remoteRuntimeUrl: value });
                            setRuntimeTestState({ status: 'idle', message: '' });
                          }}
                          placeholder={settingsState.runtimeMode === 'local' ? DEFAULT_LOCAL_RUNTIME_URL : DEFAULT_REMOTE_RUNTIME_URL}
                        />
                      </div>

                      <div className="flex gap-3">
                        <button
                          onClick={() => { void handleTestRuntime(); }}
                          className="ghost-action justify-center"
                          disabled={runtimeTestState.status === 'testing'}
                        >
                          {runtimeTestState.status === 'testing' ? 'Testing...' : 'Test connection'}
                        </button>
                        <button
                          onClick={handleSaveSettings}
                          className="solid-action justify-center"
                        >
                          Save runtime
                        </button>
                      </div>

                      {runtimeTestState.status !== 'idle' ? (
                        <div className={`settings-status-card ${runtimeTestState.status}`}>
                          <div className="flex items-center justify-between gap-3">
                            <div className="text-[13px] font-semibold text-gray-800">
                              {runtimeTestState.status === 'success' ? 'Connection succeeded' : runtimeTestState.status === 'testing' ? 'Testing runtime' : 'Connection failed'}
                            </div>
                            <SettingsStatusPill status={runtimeTestState.status} />
                          </div>
                          <div className="text-[12px] text-gray-500 mt-2 break-all">
                            {getActiveRuntimeUrl(settingsState)}
                          </div>
                          <div className="text-[13px] text-gray-600 mt-3">
                            {runtimeTestState.message}
                          </div>
                        </div>
                      ) : null}
                    </div>
                  </div>
                ) : settingsSection === 'cloud-config' ? (
                  <CloudConfigSection
                    userConfigState={userConfigState}
                    setUserConfigState={setUserConfigState}
                    runtimeConfig={{
                      runtimeMode: settingsState.runtimeMode,
                      localRuntimeUrl: settingsState.localRuntimeUrl,
                      remoteRuntimeUrl: settingsState.remoteRuntimeUrl,
                    }}
                    flash={flash}
                  />
                ) : settingsSection === 'skills' ? (
                  <div className="max-w-[920px] space-y-8">
                    <div className="flex items-start justify-between gap-4">
                      <div>
                        <div className="panel-eyebrow">Settings</div>
                        <div className="panel-title">Skills</div>
                        <div className="text-[13px] text-gray-500 mt-1">Connect to your Ornn skill library. Skills are automatically available to all agents via tool calling.</div>
                      </div>
                      <button onClick={handleSaveSettings} className="solid-action">Save</button>
                    </div>
                    <div className="settings-section-card space-y-5">
                      <div className="section-heading">Ornn Platform</div>
                      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_auto_auto] lg:items-end">
                        <div>
                          <label className="field-label">Ornn Base URL</label>
                          <input className="panel-input mt-1" value={settingsState.ornnBaseUrl} onChange={e => { setSettingsState(prev => ({ ...prev, ornnBaseUrl: e.target.value })); setOrnnTestState({ status: 'idle', message: '' }); }} placeholder={DEFAULT_ORNN_BASE_URL} />
                        </div>
                        <button onClick={async () => { setOrnnTestState({ status: 'testing', message: '' }); const ok = await api.ornn.checkHealth(settingsState.ornnBaseUrl || DEFAULT_ORNN_BASE_URL); setOrnnTestState(ok ? { status: 'success', message: 'Connected to Ornn.' } : { status: 'error', message: 'Cannot reach Ornn.' }); }} className="ghost-action justify-center" disabled={ornnTestState.status === 'testing'}>{ornnTestState.status === 'testing' ? 'Testing...' : 'Test connection'}</button>
                        <a href={settingsState.ornnBaseUrl || DEFAULT_ORNN_BASE_URL} target="_blank" rel="noopener noreferrer" className="solid-action justify-center !no-underline">Open Ornn Platform</a>
                      </div>
                      {ornnTestState.status !== 'idle' ? (<div className="settings-status-card"><div className="flex items-center justify-between gap-3"><div className="text-[13px] font-semibold text-gray-800">{ornnTestState.status === 'success' ? 'Connected' : ornnTestState.status === 'testing' ? 'Testing...' : 'Failed'}</div><SettingsStatusPill status={ornnTestState.status} /></div><div className="text-[13px] text-gray-600 mt-2">{ornnTestState.message}</div></div>) : null}
                      <div className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] px-4 py-3 text-[13px] text-gray-600">Agents automatically get <strong>ornn_search_skills</strong> and <strong>ornn_use_skill</strong> tools. To manage skills, use the Ornn platform.</div>
                    </div>
                    <div className="settings-section-card space-y-5">
                      <div className="flex items-center justify-between gap-4">
                        <div className="section-heading">Your Skills</div>
                        <button onClick={async () => { setOrnnSkillsLoading(true); try { const r = await api.ornn.searchSkills(settingsState.ornnBaseUrl || DEFAULT_ORNN_BASE_URL, '', 'mixed', 1, 100); setOrnnSkillsCache(r.items); } catch { flash('Failed to load skills.', 'error'); } finally { setOrnnSkillsLoading(false); } }} className="ghost-action text-[12px]" disabled={ornnSkillsLoading}>{ornnSkillsLoading ? 'Loading...' : 'Refresh'}</button>
                      </div>
                      {ornnSkillsCache.length === 0 ? (<div className="text-[13px] text-gray-400">{ornnSkillsLoading ? 'Loading...' : 'Click Refresh to load skills from Ornn.'}</div>) : (<div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">{ornnSkillsCache.map(s => (<div key={s.guid || s.name} className="rounded-[14px] border border-[#EAE4DB] bg-white p-3 space-y-1"><div className="flex items-center justify-between gap-2"><div className="text-[13px] font-semibold text-gray-800 truncate">{s.name}</div><span className="text-[10px] uppercase tracking-wide text-gray-400">{s.isPrivate ? 'private' : 'public'}</span></div><div className="text-[12px] text-gray-500 line-clamp-2">{s.description}</div></div>))}</div>)}
                    </div>
                  </div>
                ) : (
                  <div className="max-w-[920px] space-y-8">
                    <div className="flex items-start justify-between gap-4">
                      <div>
                        <div className="panel-eyebrow">Settings</div>
                        <div className="panel-title">Appearance</div>
                      </div>
                      <button onClick={handleSaveSettings} className="solid-action">
                        Save appearance
                      </button>
                    </div>

                    <div className="settings-section-card space-y-5">
                      <div className="space-y-3">
                        <div className="section-heading">Mode</div>
                        <div className="grid gap-3 md:grid-cols-2">
                          <button
                            type="button"
                            onClick={() => setSettingsState(prev => ({ ...prev, colorMode: 'light' }))}
                            className={`appearance-card ${settingsState.colorMode === 'light' ? 'active' : ''}`}
                            data-tooltip="Use the brighter studio surface."
                          >
                            <div className="flex items-center gap-3">
                              <div className="settings-mode-icon">
                                <Sun size={16} />
                              </div>
                              <div className="min-w-0 text-left">
                                <div className="text-[13px] font-semibold text-gray-800">Light</div>
                              </div>
                            </div>
                          </button>
                          <button
                            type="button"
                            onClick={() => setSettingsState(prev => ({ ...prev, colorMode: 'dark' }))}
                            className={`appearance-card ${settingsState.colorMode === 'dark' ? 'active' : ''}`}
                            data-tooltip="Use the darker studio surface."
                          >
                            <div className="flex items-center gap-3">
                              <div className="settings-mode-icon">
                                <Moon size={16} />
                              </div>
                              <div className="min-w-0 text-left">
                                <div className="text-[13px] font-semibold text-gray-800">Dark</div>
                              </div>
                            </div>
                          </button>
                        </div>
                      </div>

                      <div className="appearance-grid">
                        {APPEARANCE_OPTIONS.map(option => (
                          <button
                            key={option.id}
                            onClick={() => setSettingsState(prev => ({ ...prev, appearanceTheme: option.id }))}
                            data-tooltip={option.description}
                            className={`appearance-card ${settingsState.appearanceTheme === option.id ? 'active' : ''}`}
                          >
                            <div className="appearance-swatches">
                              {option.swatches.map(color => (
                                <span
                                  key={color}
                                  className="appearance-swatch"
                                  style={{ background: color }}
                                />
                              ))}
                            </div>
                            <div className="text-[13px] font-semibold text-gray-800">{option.label}</div>
                          </button>
                        ))}
                      </div>
                    </div>
                  </div>
                )}
              </div>
            </div>
          </section>
        ) : (
          <>
        <header className="studio-editor-header">
          <div className="studio-editor-toolbar">
            <div className="studio-view-switch">
              {(['editor', 'execution'] as StudioView[]).map(view => (
                <button
                  key={view}
                  onClick={() => setStudioView(view)}
                  data-tooltip={view === 'editor' ? 'Edit the current workflow.' : 'Inspect past runs and logs.'}
                  className={`studio-view-switch-button ${studioView === view ? 'active' : ''}`}
                >
                  {view === 'editor' ? 'Edit' : 'Runs'}
                </button>
              ))}
            </div>

            <div className="studio-title-bar">
              <div className="studio-title-group">
                <input
                  className="studio-title-input"
                  value={workflowMeta.name}
                  onChange={event => {
                    setWorkflowMeta(prev => ({ ...prev, name: event.target.value }));
                    markDirty();
                  }}
                  placeholder="draft"
                  aria-label="Workflow title"
                />
                <InfoPopover
                  title="Description"
                  align="left"
                  buttonTooltip="Edit the workflow description."
                  buttonClassName="header-help-button"
                  cardClassName="header-help-card header-description-card"
                  hideTitle
                  content={(
                    <textarea
                      rows={5}
                      className="panel-textarea description-editor"
                      value={workflowMeta.description}
                      placeholder="Workflow description"
                      onChange={event => {
                        setWorkflowMeta(prev => ({ ...prev, description: event.target.value }));
                        markDirty();
                      }}
                    />
                  )}
                />
              </div>

              <div className="studio-header-actions">
                <button
                  onClick={() => openExplorerPage('workflows')}
                  data-tooltip="Browse Workflows"
                  aria-label="Browse Workflows"
                  className="panel-icon-button header-toolbar-action"
                >
                  <FolderOpen size={15} />
                </button>
                <button
                  onClick={handleSaveWorkflow}
                  data-tooltip="Save"
                  aria-label="Save"
                  className="panel-icon-button header-toolbar-action header-save-action"
                >
                  <Check size={15} />
                </button>
                <button
                  onClick={handleExportWorkflow}
                  data-tooltip="Export"
                  aria-label="Export"
                  className="panel-icon-button header-toolbar-action header-export-action"
                >
                  <Upload size={15} />
                </button>
                <button
                  onClick={handleRunWorkflow}
                  data-tooltip="Draft Run"
                  aria-label="Draft Run"
                  className="panel-icon-button header-toolbar-action header-run-action"
                >
                  <Play size={15} />
                </button>
                <button
                  onClick={handleBindScope}
                  data-tooltip="Bind Scope"
                  aria-label="Bind Scope"
                  className="panel-icon-button header-toolbar-action"
                >
                  <Globe size={15} />
                </button>
                {studioView === 'execution' && (executionCanStop || draftRunning) ? (
                  <button
                    onClick={() => draftRunning ? handleStopDraftRun() : void handleStopExecution()}
                    data-tooltip="Stop"
                    aria-label="Stop"
                    disabled={executionStopPending}
                    className="panel-icon-button header-toolbar-action"
                  >
                    <Square size={15} />
                  </button>
                ) : null}
              </div>
            </div>
          </div>
        </header>

        <section className="flex-1 min-h-0 relative overflow-hidden bg-[#F2F1EE]">
          <div className="canvas-overlay-stack">
            <div className="canvas-meta-card">
              <div className="canvas-meta-label">{activeWorkflowDirectory?.label || 'No directory'}</div>
              <div className="canvas-meta-value">{nodes.length} nodes · {edges.length} edges</div>
            </div>

            {studioView === 'execution' ? (
              <div className="canvas-meta-card canvas-meta-card-wide">
                <div className="canvas-meta-label">Run</div>
                <select
                  className="canvas-meta-select"
                  value={selectedExecutionId || ''}
                  onChange={event => {
                    if (event.target.value) {
                      void openExecution(event.target.value);
                    }
                  }}
                >
                  <option value="">{executionSummaryLabel}</option>
                  {currentWorkflowExecutions.map(execution => (
                    <option key={execution.executionId} value={execution.executionId}>
                      {formatDateTime(execution.startedAtUtc)} · {execution.status}
                    </option>
                  ))}
                </select>
              </div>
            ) : null}
          </div>

          <div className="canvas-overlay-tools">
            {studioView === 'editor' ? (
              <>
                <DrawerIconButton
                  active={rightPanelOpen && rightPanelTab === 'roles'}
                  label="Roles"
                  icon={<User size={16} />}
                  onClick={() => toggleRightDrawer('roles')}
                />
                <DrawerIconButton
                  active={rightPanelOpen && rightPanelTab === 'yaml'}
                  label="YAML"
                  icon={<WorkflowIcon size={16} />}
                  onClick={() => toggleRightDrawer('yaml')}
                />
              </>
            ) : null}
          </div>

          {paletteOpen ? (
            <div className="palette-drawer absolute right-5 top-20 z-30 w-[360px] max-h-[calc(100%-180px)] overflow-hidden rounded-[28px] border border-[#E8E2D9] shadow-[0_26px_64px_rgba(17,24,39,0.16)]">
              <div className="palette-drawer-header px-5 py-4 border-b border-[#F1ECE5] flex items-center justify-between">
                <div>
                  <div className="panel-eyebrow">Canvas</div>
                  <div className="panel-title">Add node</div>
                </div>
                <button onClick={() => setPaletteOpen(false)} title="Close node picker." className="panel-icon-button">
                  <X size={14} />
                </button>
              </div>

              <div className="palette-drawer-search p-4 border-b border-[#F1ECE5]">
                <div className="search-field">
                  <Search size={14} className="text-gray-400" />
                  <input
                    className="search-input"
                    placeholder="Search primitives or connectors"
                    value={paletteSearch}
                    onChange={event => setPaletteSearch(event.target.value)}
                  />
                </div>
              </div>

              <div className="palette-drawer-body overflow-y-auto max-h-[620px]">
                {filteredPrimitiveCategories.map(category => {
                  const Icon = CATEGORY_ICONS[category.key as keyof typeof CATEGORY_ICONS] || Code2;
                  const expanded = paletteExpandedSection === category.label;
                  return (
                    <div key={category.key} className="border-b border-[#F1ECE5] last:border-b-0">
                      <button
                        onClick={() => setPaletteExpandedSection(expanded ? '' : category.label)}
                        className="w-full px-4 py-3 flex items-center gap-3 hover:bg-[#FAF8F4] transition-colors text-left"
                      >
                        <div className="w-8 h-8 rounded-[12px] flex items-center justify-center" style={{ background: `${category.color}18` }}>
                          <Icon size={15} color={category.color} />
                        </div>
                        <span className="text-[13px] font-medium text-gray-800 flex-1">{category.label}</span>
                        <ChevronDown size={14} className={`text-gray-400 transition-transform ${expanded ? 'rotate-180' : ''}`} />
                      </button>
                      {expanded ? (
                        <div className="px-4 pb-3 grid gap-2">
                          {category.items.map(item => (
                            <button
                              key={item}
                              onClick={() => handleAddNode(item)}
                              className="rounded-[18px] border border-[#EEEAE4] bg-white px-3 py-3 text-left hover:bg-[#FAF8F4]"
                            >
                              <div className="text-[13px] font-medium text-gray-800">{item}</div>
                              <div className="text-[11px] text-gray-400 mt-1">{category.label}</div>
                            </button>
                          ))}
                        </div>
                      ) : null}
                    </div>
                  );
                })}

                {filteredPaletteConnectors.length > 0 ? (
                  <div className="border-b border-[#F1ECE5] last:border-b-0">
                    <button
                      onClick={() => setPaletteExpandedSection(paletteExpandedSection === 'Configured connectors' ? '' : 'Configured connectors')}
                      className="w-full px-4 py-3 flex items-center gap-3 hover:bg-[#FAF8F4] transition-colors text-left"
                    >
                      <div className="w-8 h-8 rounded-[12px] flex items-center justify-center" style={{ background: `${'#64748b'}18` }}>
                        <ArrowRightLeft size={15} color="#64748b" />
                      </div>
                      <span className="text-[13px] font-medium text-gray-800 flex-1">Configured connectors</span>
                      <ChevronDown
                        size={14}
                        className={`text-gray-400 transition-transform ${paletteExpandedSection === 'Configured connectors' ? 'rotate-180' : ''}`}
                      />
                    </button>
                    {paletteExpandedSection === 'Configured connectors' ? (
                      <div className="px-4 pb-3 grid gap-2">
                        {filteredPaletteConnectors.map(connector => (
                          <button
                            key={connector.key}
                            onClick={() => handleAddNode('connector_call', connector.name)}
                            className="rounded-[18px] border border-[#EEEAE4] bg-white px-3 py-3 text-left hover:bg-[#FAF8F4]"
                          >
                            <div className="flex items-center justify-between gap-2">
                              <span className="text-[13px] font-semibold text-gray-800 truncate">{connector.name}</span>
                              <span className="text-[10px] uppercase tracking-wide text-gray-400">{connector.type}</span>
                            </div>
                          </button>
                        ))}
                      </div>
                    ) : null}
                  </div>
                ) : null}
              </div>
            </div>
          ) : null}

          {canvasMenu.open ? (
            <div
              className="fixed z-40 rounded-[18px] border border-[#E8E2D9] bg-white shadow-[0_22px_46px_rgba(17,24,39,0.16)]"
              style={{ left: canvasMenu.x, top: canvasMenu.y }}
            >
              <button
                onClick={() => {
                  setPaletteOpen(true);
                  setCanvasMenu({ open: false, x: 0, y: 0 });
                }}
                className="px-4 py-3 text-[13px] font-medium text-gray-700 hover:bg-[#FAF8F4] rounded-[18px]"
              >
                Add node
              </button>
            </div>
          ) : null}

          {studioView === 'editor' ? (
            <div className="absolute bottom-6 right-5 z-30 flex items-end gap-3">
              {askAiOpen ? (
                <div
                  className="ask-ai-surface w-[380px] rounded-[28px] border border-[#E8E2D9] p-4 shadow-[0_26px_64px_rgba(17,24,39,0.16)]"
                  onClick={event => event.stopPropagation()}
                >
                  <div className="flex items-center justify-between gap-3">
                    <div>
                      <div className="panel-eyebrow">Canvas</div>
                      <div className="panel-title">Ask AI</div>
                    </div>
                    <button
                      onClick={() => setAskAiOpen(false)}
                      title="Close Ask AI."
                      className="panel-icon-button"
                    >
                      <X size={14} />
                    </button>
                  </div>

                  <p className="mt-3 text-[12px] leading-6 text-gray-500">
                    Describe the workflow. AI reasoning streams here and the validated YAML stays in this panel until you apply it.
                  </p>

                  <textarea
                    rows={5}
                    className="panel-textarea mt-4"
                    placeholder="Build a workflow that triages incidents, routes risky cases to human approval, and posts the result to Slack."
                    value={askAiPrompt}
                    onChange={event => setAskAiPrompt(event.target.value)}
                  />

                    <div className="mt-3 flex items-center justify-between gap-2">
                      <div className="text-[11px] text-gray-400">
                        {askAiPending
                          ? 'Generating and validating YAML...'
                          : askAiGeneratedYaml
                            ? 'Validated YAML is ready to apply.'
                            : 'Return format: workflow YAML only'}
                      </div>
                      <div className="flex items-center gap-2">
                        <button
                          onClick={() => {
                            if (!askAiGeneratedYaml.trim()) {
                              return;
                            }

                            void copyTextToClipboard(askAiGeneratedYaml).then(copied => {
                              if (copied) {
                                flash('Workflow YAML copied', 'success');
                              }
                            });
                          }}
                          className="ghost-action !px-3"
                          disabled={!askAiGeneratedYaml.trim()}
                        >
                          <Copy size={14} /> Copy
                        </button>
                        <button
                          onClick={() => {
                            if (!askAiGeneratedYaml.trim()) {
                              return;
                            }

                            void applyAskAiYaml(askAiGeneratedYaml).then(
                              () => flash('AI workflow applied to canvas', 'success'),
                              (error: any) => flash(error?.message || 'Failed to apply workflow YAML', 'error'),
                            );
                          }}
                          className="ghost-action !px-3"
                          disabled={!askAiGeneratedYaml.trim()}
                        >
                          <Check size={14} /> Apply
                        </button>
                        <button
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
                    <pre className="mt-2 max-h-[140px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-600">
                      {askAiReasoning || 'LLM reasoning will stream here.'}
                    </pre>
                  </div>

                  <div className="mt-4 rounded-[20px] border border-[#F1ECE5] bg-[#FAF8F4] p-3">
                    <div className="flex items-center justify-between gap-3">
                      <div className="text-[11px] uppercase tracking-[0.16em] text-gray-400">YAML</div>
                      <div className="text-[10px] uppercase tracking-[0.16em] text-gray-400">
                        {askAiGeneratedYaml ? 'Ready to apply' : 'Waiting for valid YAML'}
                      </div>
                    </div>
                    <pre className="mt-2 max-h-[220px] overflow-auto whitespace-pre-wrap break-words text-[12px] leading-6 text-gray-700">
                      {askAiAnswer || 'Validated workflow YAML will appear here.'}
                    </pre>
                  </div>
                </div>
              ) : null}

              <button
                onClick={event => {
                  event.stopPropagation();
                  setAskAiOpen(value => !value);
                }}
                title="Ask AI to generate workflow YAML."
                className="ask-ai-trigger flex h-14 w-14 items-center justify-center rounded-[20px] border border-[color:var(--accent-border)] shadow-[0_24px_56px_rgba(17,24,39,0.18)] transition-transform hover:-translate-y-0.5"
                style={selectedIconSurfaceStyle}
              >
                <Bot size={20} />
              </button>
            </div>
          ) : null}

          <ReactFlow
            nodes={executionNodes}
            edges={executionEdges}
            nodeTypes={nodeTypes}
            minZoom={0.14}
            maxZoom={1.6}
            defaultEdgeOptions={{
              type: 'smoothstep',
              zIndex: 4,
              style: {
                stroke: '#2F6FEC',
                strokeWidth: 2.5,
              },
              markerEnd: {
                type: MarkerType.ArrowClosed,
                width: 11,
                height: 11,
                color: '#2F6FEC',
              },
            }}
            connectionLineType={ConnectionLineType.SmoothStep}
            connectionLineStyle={{
              stroke: '#2F6FEC',
              strokeWidth: 2.5,
            }}
            onInit={instance => { reactFlowInstanceRef.current = instance; }}
            onNodesChange={studioView === 'editor' ? handleNodesChange : undefined}
            onEdgesChange={studioView === 'editor' ? handleEdgesChange : undefined}
            onConnect={studioView === 'editor' ? handleConnect : undefined}
            onNodeClick={(_, node) => {
              if (studioView === 'execution') {
                const stepId = typeof node.data?.stepId === 'string' ? node.data.stepId : '';
                const logIndex = findExecutionLogIndexForStep(executionTrace, stepId);
                if (logIndex !== null) {
                  setActiveExecutionLogIndex(logIndex);
                }
                return;
              }

              setSelectedNodeId(node.id);
              setRightPanelTab('node');
              setRightPanelOpen(true);
            }}
            onPaneClick={() => {
              if (studioView === 'editor') {
                setSelectedNodeId(null);
              }
              setCanvasMenu({ open: false, x: 0, y: 0 });
            }}
            onPaneContextMenu={handlePaneContextMenu}
            fitView
            fitViewOptions={{
              padding: 0.2,
              minZoom: 0.14,
              maxZoom: 0.92,
            }}
            nodesDraggable={studioView === 'editor'}
            nodesConnectable={studioView === 'editor'}
            elementsSelectable
            className="studio-canvas"
          >
            <Background color="#D8D2C8" variant={BackgroundVariant.Dots} gap={24} size={1} />
            <MiniMap
              position="bottom-left"
              zoomable
              pannable
              className="studio-minimap"
              style={{ width: 164, height: 108, marginLeft: 16, marginBottom: 88 }}
              maskColor="rgba(255, 255, 255, 0.76)"
              bgColor="rgba(248, 247, 244, 0.98)"
              nodeBorderRadius={8}
              nodeColor={node => {
                const stepType = typeof node.data?.stepType === 'string' ? node.data.stepType : '';
                return getCategoryForType(stepType).color;
              }}
            />
            <Controls position="bottom-left" />
          </ReactFlow>

          {studioView === 'editor' ? (
            <aside className={`right-drawer ${rightPanelOpen ? 'open' : ''}`}>
              <div className="panel-header border-b border-[#F1ECE5]">
                <div>
                  <div className="panel-eyebrow">Inspector</div>
                  <div className="panel-title">{rightPanelTab === 'node' ? 'Node' : rightPanelTab === 'roles' ? 'Roles' : 'YAML'}</div>
                </div>
                <button onClick={() => setRightPanelOpen(false)} title="Close inspector." className="panel-icon-button">
                  <ChevronLeft size={16} />
                </button>
              </div>

              <div className="flex-1 min-h-0 overflow-y-auto p-4">
                {rightPanelTab === 'node' ? (
                  selectedNode ? (
                    <div className="space-y-4">
                      <InputField
                        label="Step ID"
                        value={selectedNode.data.stepId}
                        onChange={value => {
                          updateSelectedNode(node => ({
                            ...node,
                            data: {
                              ...node.data,
                              stepId: value,
                              label: value,
                            },
                          }));
                        }}
                      />

                      <div>
                        <label className="field-label">Primitive</label>
                        <select
                          className="panel-input mt-1"
                          value={selectedNode.data.stepType}
                          onChange={event => {
                            const nextType = event.target.value;
                            updateSelectedNode(node => {
                              const nextParameters = {
                                ...getDefaultParametersForType(nextType),
                                ...node.data.parameters,
                              };
                              if (nextType === 'connector_call' && !nextParameters.connector && connectors[0]?.name) {
                                applyConnectorDefaults(nextParameters, connectors[0].name, connectors);
                              }
                              return {
                                ...node,
                                data: {
                                  ...node.data,
                                  stepType: nextType,
                                  targetRole: supportsRole(nextType) ? (node.data.targetRole || roles[0]?.id || '') : '',
                                  parameters: nextParameters,
                                },
                              };
                            });
                          }}
                        >
                          {PRIMITIVE_CATEGORIES.map(category => (
                            <optgroup key={category.key} label={category.label}>
                              {category.items.map(item => (
                                <option key={item} value={item}>{item}</option>
                              ))}
                            </optgroup>
                          ))}
                        </select>
                      </div>

                      {supportsRole(selectedNode.data.stepType) ? (
                        <div>
                          <label className="field-label">Target role</label>
                          <select
                            className="panel-input mt-1"
                            value={selectedNode.data.targetRole}
                            onChange={event => {
                              const value = event.target.value;
                              updateSelectedNode(node => ({
                                ...node,
                                data: {
                                  ...node.data,
                                  targetRole: value,
                                },
                              }));
                            }}
                          >
                            <option value="">No role</option>
                            {roles.map(role => (
                              <option key={role.key} value={role.id}>{role.id || role.name}</option>
                            ))}
                          </select>
                        </div>
                      ) : null}

                      {selectedNode.data.stepType === 'connector_call' ? (
                        <div className="rounded-[20px] border border-[#EEEAE4] bg-[#FAF8F4] p-3 space-y-3">
                          <div className="flex items-center justify-between gap-3">
                            <div>
                              <div className="text-[12px] font-semibold text-gray-700">Connector</div>
                              <div className="text-[11px] text-gray-400">Use a configured connector</div>
                            </div>
                            <button
                              onClick={() => {
                                openExplorerPage();
                              }}
                              className="accent-inline-link text-[11px] font-medium"
                            >
                              Open
                            </button>
                          </div>
                          <select
                            className="panel-input"
                            value={String(selectedNode.data.parameters.connector || '')}
                            onChange={event => {
                              const connectorName = event.target.value;
                              updateSelectedNode(node => {
                                const nextParameters = { ...node.data.parameters };
                                applyConnectorDefaults(nextParameters, connectorName, connectors);
                                return {
                                  ...node,
                                  data: {
                                    ...node.data,
                                    parameters: nextParameters,
                                  },
                                };
                              });
                            }}
                          >
                            <option value="">Select connector</option>
                            {connectors.map(connector => (
                              <option key={connector.key} value={connector.name}>
                                {connector.name} · {connector.type}
                              </option>
                            ))}
                          </select>
                        </div>
                      ) : null}

                      <div>
                        <div className="flex items-center justify-between mb-2">
                          <label className="field-label">Parameters</label>
                          <button
                            onClick={() => {
                              updateSelectedNode(node => {
                                const keys = Object.keys(node.data.parameters || {});
                                let index = 1;
                                let key = `param_${index}`;
                                while (keys.includes(key)) {
                                  index += 1;
                                  key = `param_${index}`;
                                }
                                return {
                                  ...node,
                                  data: {
                                    ...node.data,
                                    parameters: {
                                      ...node.data.parameters,
                                      [key]: '',
                                    },
                                  },
                                };
                              });
                            }}
                            className="accent-inline-link text-[11px] font-medium"
                          >
                            Add
                          </button>
                        </div>
                        <div className="space-y-2">
                          {Object.entries(selectedNode.data.parameters || {}).length === 0 ? (
                            <div className="empty-card">No parameters</div>
                          ) : Object.entries(selectedNode.data.parameters || {}).map(([key, value]) => (
                            <div key={key} className="rounded-[18px] border border-[#EEEAE4] bg-white p-3 space-y-2">
                              <div className="flex items-center gap-2">
                                <input
                                  className="panel-input flex-1"
                                  value={key}
                                  onChange={event => {
                                    const nextKey = event.target.value;
                                    updateSelectedNode(node => {
                                      if (!nextKey || nextKey === key) {
                                        return node;
                                      }
                                      const nextParameters = { ...node.data.parameters };
                                      const currentValue = nextParameters[key];
                                      delete nextParameters[key];
                                      nextParameters[nextKey] = currentValue;
                                      return {
                                        ...node,
                                        data: {
                                          ...node.data,
                                          parameters: nextParameters,
                                        },
                                      };
                                    });
                                  }}
                                />
                                <button
                                  onClick={() => {
                                    updateSelectedNode(node => {
                                      const nextParameters = { ...node.data.parameters };
                                      delete nextParameters[key];
                                      return {
                                        ...node,
                                        data: {
                                          ...node.data,
                                          parameters: nextParameters,
                                        },
                                      };
                                    });
                                  }}
                                  title="Remove parameter."
                                  className="panel-icon-button text-red-500 hover:bg-red-50"
                                >
                                  <Trash2 size={13} />
                                </button>
                              </div>
                              <textarea
                                rows={String(value).includes('\n') ? 4 : 2}
                                className="panel-textarea"
                                value={formatParameterValue(value)}
                                onChange={event => {
                                  const nextValue = parseParameterInput(event.target.value);
                                  updateSelectedNode(node => ({
                                    ...node,
                                    data: {
                                      ...node.data,
                                      parameters: {
                                        ...node.data.parameters,
                                        [key]: nextValue,
                                      },
                                    },
                                  }));
                                }}
                              />
                            </div>
                          ))}
                        </div>
                      </div>

                      <div>
                        <div className="field-label mb-2">Connections</div>
                        <div className="space-y-2">
                          {selectedNodeOutgoingEdges.length === 0 ? (
                            <div className="empty-card">No outgoing connections</div>
                          ) : selectedNodeOutgoingEdges.map(edge => {
                            const target = nodes.find(node => node.id === edge.target);
                            return (
                              <div key={edge.id} className="rounded-[18px] border border-[#EEEAE4] bg-white px-3 py-3 flex items-center justify-between gap-3">
                                <div>
                                  <div className="text-[12px] font-medium text-gray-800">{edge.data?.branchLabel || 'next'}</div>
                                  <div className="text-[11px] text-gray-400">{target?.data.stepId || edge.target}</div>
                                </div>
                                <button
                                  onClick={() => {
                                    setEdges(prev => prev.filter(item => item.id !== edge.id));
                                    markDirty();
                                  }}
                                  title="Remove connection."
                                  className="panel-icon-button text-red-500 hover:bg-red-50"
                                >
                                  <Trash2 size={13} />
                                </button>
                              </div>
                            );
                          })}
                        </div>
                      </div>

                      <button
                        onClick={() => handleDeleteNode(selectedNode.id)}
                        className="w-full rounded-[18px] border border-red-200 bg-white px-3 py-3 text-[12px] font-medium text-red-500 hover:bg-red-50"
                      >
                        Remove node
                      </button>
                    </div>
                  ) : (
                    <EmptyPanel
                      icon={<Boxes size={18} className="text-gray-300" />}
                      title="No node selected"
                      copy="Select a node on the canvas."
                    />
                  )
                ) : rightPanelTab === 'roles' ? (
                  <div className="space-y-4">
                    <div className="flex items-center justify-between">
                      <div>
                        <div className="text-[14px] font-semibold text-gray-800">Roles</div>
                      </div>
                      <button onClick={addRole} data-tooltip="Add a role to this workflow." className="ghost-action !px-3">Add</button>
                    </div>

                    <div className="search-field">
                      <Search size={14} className="text-gray-400" />
                      <input
                        className="search-input"
                        placeholder="Search saved roles"
                        value={workflowRoleSearch}
                        onChange={event => setWorkflowRoleSearch(event.target.value)}
                      />
                    </div>

                    <div className="rounded-[22px] border border-[#EEEAE4] bg-[#FAF8F4] p-4 space-y-3">
                      <div className="flex items-center justify-between gap-3">
                        <div>
                          <div className="text-[13px] font-semibold text-gray-800">Saved roles</div>
                        </div>
                        <button
                          onClick={() => {
                            openExplorerPage();
                          }}
                          className="accent-inline-link text-[11px] font-medium"
                        >
                          Open catalog
                        </button>
                      </div>

                      {filteredWorkflowCatalogRoles.length === 0 ? (
                        <div className="empty-card">No saved roles matched</div>
                      ) : (
                        <div className="space-y-2 max-h-[220px] overflow-y-auto">
                          {filteredWorkflowCatalogRoles.map(role => (
                            <div
                              key={role.key}
                              className="rounded-[18px] border border-[#EEEAE4] bg-white px-3 py-3"
                            >
                              <div className="flex items-center justify-between gap-3">
                                <div className="min-w-0">
                                  <div className="text-[13px] font-semibold text-gray-800 truncate">{role.name || role.id || 'Role'}</div>
                                  <div className="text-[11px] text-gray-400 truncate">
                                    {role.id || 'role'}{role.provider ? ` · ${role.provider}` : ''}{role.model ? ` · ${role.model}` : ''}
                                  </div>
                                </div>
                                <button
                                  onClick={() => applyCatalogRoleToWorkflow(role.key)}
                                  className="ghost-action !min-h-[34px] !px-3"
                                >
                                  Use
                                </button>
                              </div>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>

                    <div className="flex items-center justify-between">
                      <div>
                        <div className="text-[13px] font-semibold text-gray-800">Workflow roles</div>
                      </div>
                    </div>

                    {filteredWorkflowRoles.length === 0 ? (
                      <div className="empty-card">No workflow roles matched</div>
                    ) : filteredWorkflowRoles.map(role => (
                      <div key={role.key} className="rounded-[22px] border border-[#EEEAE4] bg-[#FAF8F4] overflow-hidden">
                        <button
                          onClick={() => setExpandedWorkflowRoleKey(current => current === role.key ? null : role.key)}
                          className="w-full px-4 py-3 flex items-center justify-between gap-3 text-left bg-white/80 hover:bg-white"
                        >
                          <div className="min-w-0">
                            <div className="text-[13px] font-semibold text-gray-800 truncate">{role.id || 'role_id'}</div>
                          </div>
                          <div className="flex items-center">
                            <ChevronDown size={14} className={`text-gray-400 transition-transform ${expandedWorkflowRoleKey === role.key ? 'rotate-180' : ''}`} />
                          </div>
                        </button>

                        {expandedWorkflowRoleKey === role.key ? (
                          <div className="p-4 space-y-3 border-t border-[#EEEAE4]">
                            <div className="flex items-center justify-between gap-2">
                              <div className="text-[13px] font-semibold text-gray-800">{role.name || role.id || 'Role'}</div>
                              <div className="flex items-center gap-2">
                                <button
                                  onClick={() => copyWorkflowRoleToCatalog(role.key)}
                                  data-tooltip="Save this workflow role to the global role catalog."
                                  className="ghost-action !min-h-[34px] !px-3"
                                >
                                  Save preset
                                </button>
                                <button onClick={() => removeRole(role.key)} title="Remove role." className="panel-icon-button text-red-500 hover:bg-red-50">
                                  <Trash2 size={13} />
                                </button>
                              </div>
                            </div>

                            <div className="grid grid-cols-2 gap-2">
                              <InputField
                                label="Role ID"
                                value={role.id}
                                onChange={value => {
                                  const previousId = role.id;
                                  const nextName = role.name || value;
                                  setRoles(prev => prev.map(item => item.key === role.key ? {
                                    ...item,
                                    id: value,
                                    name: item.name || nextName,
                                  } : item));
                                  setNodes(prev => prev.map(node => node.data.targetRole === previousId
                                    ? { ...node, data: { ...node.data, targetRole: value } }
                                    : node));
                                  markDirty();
                                }}
                              />
                              <InputField
                                label="Role name"
                                value={role.name}
                                onChange={value => {
                                  updateWorkflowRole(role.key, item => ({ ...item, name: value }));
                                }}
                              />
                            </div>

                            <div className="grid grid-cols-2 gap-2">
                              <div>
                                <label className="field-label">Provider</label>
                                <select
                                  className="panel-input mt-1"
                                  value={role.provider}
                                  onChange={event => {
                                    const selectedProviderName = event.target.value;
                                    const configuredProvider = settingsState.providers.find(provider => provider.providerName === selectedProviderName);
                                    updateWorkflowRole(role.key, item => ({
                                      ...item,
                                      provider: selectedProviderName,
                                      model: configuredProvider?.model || item.model,
                                    }));
                                  }}
                                >
                                  <option value="">Default</option>
                                  {settingsState.providers.map(provider => (
                                    <option key={provider.key} value={provider.providerName}>
                                      {provider.providerName}
                                    </option>
                                  ))}
                                </select>
                              </div>

                              <InputField
                                label="Model"
                                value={role.model}
                                onChange={value => {
                                  updateWorkflowRole(role.key, item => ({ ...item, model: value }));
                                }}
                              />
                            </div>

                            <TextAreaField
                              label="System prompt"
                              value={role.systemPrompt}
                              rows={5}
                              onChange={value => {
                                updateWorkflowRole(role.key, item => ({ ...item, systemPrompt: value }));
                              }}
                            />

                            <div className="space-y-2">
                              <div className="field-label">Allowed connectors</div>
                              <div className="flex flex-wrap gap-2">
                                {connectors.length === 0 ? (
                                  <div className="text-[12px] text-gray-400">No connectors configured</div>
                                ) : connectors.map(connector => {
                                  const active = splitLines(role.connectorsText).includes(connector.name);
                                  return (
                                    <button
                                      key={connector.key}
                                      onClick={() => toggleRoleConnector(role.key, connector.name)}
                                      className={`chip-button ${active ? 'chip-button-active' : ''}`}
                                    >
                                      {connector.name}
                                    </button>
                                  );
                                })}
                              </div>
                            </div>
                          </div>
                        ) : null}
                      </div>
                    ))}
                  </div>
                ) : (
                  <div className="space-y-4">
                    <div className="flex items-center justify-between gap-3">
                      <div>
                        <div className="text-[14px] font-semibold text-gray-800">YAML</div>
                        <div className="text-[12px] text-gray-400">Edit YAML to update the canvas</div>
                      </div>
                      <div className="flex items-center gap-2">
                        <button onClick={handleValidateWorkflow} className="ghost-action !px-3"><Shield size={14} /> Validate</button>
                        <button onClick={handleCopyYaml} className="ghost-action !px-3"><Copy size={14} /> Copy</button>
                      </div>
                    </div>

                    <textarea
                      className="panel-textarea !min-h-[280px] !bg-[#FAF8F4]"
                      value={workflowMeta.yaml}
                      onChange={event => handleYamlEditorChange(event.target.value)}
                      spellCheck={false}
                    />

                    <div className="space-y-2">
                      <div className="field-label">Findings</div>
                      {workflowMeta.findings.length === 0 ? (
                        <div className="empty-card">No findings</div>
                      ) : workflowMeta.findings.map((finding, index) => (
                        <div key={`${finding.path || 'root'}-${index}`} className="rounded-[18px] border border-[#EEEAE4] bg-white px-3 py-3">
                          <div className={`text-[12px] font-semibold ${toFindingLevel(finding.level) === 'error' ? 'text-red-500' : 'text-amber-500'}`}>
                            {finding.message}
                          </div>
                          <div className="text-[11px] text-gray-400 mt-1">
                            {finding.path || '/'}{finding.code ? ` · ${finding.code}` : ''}
                          </div>
                        </div>
                      ))}
                    </div>

                    {errorFindings.length > 0 ? (
                      <div className="rounded-[18px] border border-red-200 bg-red-50 px-3 py-3 text-[12px] text-red-600 flex items-start gap-2">
                        <AlertCircle size={15} className="mt-0.5 shrink-0" />
                        <span>{errorFindings.length} errors still need fixing before execution.</span>
                      </div>
                    ) : null}
                  </div>
                )}
              </div>
            </aside>
          ) : null}
        </section>

        {studioView === 'execution' ? renderExecutionLogsSection() : null}
          </>
        )}
      </main>

      <ModalShell
        open={runModalOpen}
        title="Draft Run"
        onClose={() => setRunModalOpen(false)}
        actions={(
          <>
            <button onClick={() => setRunModalOpen(false)} className="ghost-action">Cancel</button>
            <button onClick={() => { void handleConfirmRunWorkflow(); }} className="solid-action">
              <Play size={14} /> Run
            </button>
          </>
        )}
      >
        <div className="space-y-3">
          <div className="text-[12px] text-gray-500">
            Sends the current workflow YAML as an inline bundle to <code>/api/scopes/{'{scopeId}'}/workflow/draft-run</code>.
            {runScopeId ? <span> Scope: <strong>{runScopeId}</strong></span> : <span className="text-amber-600"> Not logged in — scope unavailable.</span>}
          </div>
          <textarea
            rows={6}
            className="panel-textarea run-prompt-textarea"
            value={executionPrompt}
            placeholder="What should this run do?"
            onChange={event => setExecutionPrompt(event.target.value)}
          />
        </div>
      </ModalShell>

      <ModalShell
        open={bindScopeModalOpen}
        title="Bind Scope"
        onClose={() => setBindScopeModalOpen(false)}
        actions={(
          <>
            <button onClick={() => setBindScopeModalOpen(false)} className="ghost-action">Cancel</button>
            <button onClick={() => { void handleConfirmBindScope(); }} disabled={bindScopePending} className="solid-action">
              <Globe size={14} /> {bindScopePending ? 'Binding...' : 'Bind'}
            </button>
          </>
        )}
      >
        <div className="space-y-3">
          <div className="text-[12px] text-gray-500">
            Binds the current workflow as a scope service. After binding, invoke it from <strong>Console</strong> by selecting the service.
          </div>
          <div className="space-y-2">
            <label className="text-[11px] font-semibold text-gray-500 uppercase tracking-wider">Service ID</label>
            <input
              className="w-full rounded-lg border border-[#E6E3DE] bg-[#F7F5F2] px-3 py-2 text-[12px] font-mono text-gray-700 focus:outline-none focus:ring-1 focus:ring-blue-400"
              placeholder="default"
              value={bindServiceId}
              onChange={e => setBindServiceId(e.target.value)}
            />
            <div className="text-[10px] text-gray-400">
              Invoke path: <code className="text-gray-500">/services/{bindServiceId.trim() || 'default'}/invoke/chat:stream</code>
            </div>
          </div>
          <div className="rounded-lg border border-[#E6E3DE] bg-[#F7F5F2] px-4 py-3 text-[13px] space-y-0.5">
            <div><span className="text-gray-400">Scope:</span> <strong>{runScopeId || '(not logged in)'}</strong></div>
            <div><span className="text-gray-400">Workflow:</span> <strong>{workflowMeta.name || 'draft'}</strong></div>
          </div>
        </div>
      </ModalShell>

      <ModalShell
        open={connectorModalOpen}
        title="Add Connector"
        onClose={() => {
          void closeConnectorModal();
        }}
        actions={(
          <>
            <button onClick={() => { void closeConnectorModal(); }} className="ghost-action">Close</button>
            <button onClick={() => { void handleSubmitConnectorDraft(); }} className="solid-action">
              <Plus size={14} /> Add connector
            </button>
          </>
        )}
      >
        {connectorDraft ? (
          <div className="space-y-3">
            <div className="text-[12px] text-gray-500">
              Close this dialog at any time and the latest text will be kept as a draft.
            </div>
            <div>
              <label className="field-label">Type</label>
              <select
                className="panel-input mt-1"
                value={connectorDraft.type}
                onChange={event => setConnectorDraft(prev => prev ? { ...prev, type: event.target.value as ConnectorState['type'] } : prev)}
              >
                <option value="http">HTTP</option>
                <option value="cli">CLI</option>
                <option value="mcp">MCP</option>
              </select>
            </div>
            <InputField
              label="Name"
              value={connectorDraft.name}
              onChange={value => setConnectorDraft(prev => prev ? { ...prev, name: value } : prev)}
            />

            {connectorDraft.type === 'http' ? (
              <div className="space-y-3 rounded-[18px] border border-[#EAE4DB] bg-[#FAF8F4] p-4">
                <InputField
                  label="Base URL"
                  value={connectorDraft.http.baseUrl}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    http: { ...prev.http, baseUrl: value },
                  } : prev)}
                />
                <TextAreaField
                  label="Allowed methods"
                  rows={3}
                  value={connectorDraft.http.allowedMethods.join('\n')}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    http: { ...prev.http, allowedMethods: splitLines(value).map(item => item.toUpperCase()) },
                  } : prev)}
                />
                <TextAreaField
                  label="Allowed paths"
                  rows={3}
                  value={connectorDraft.http.allowedPaths.join('\n')}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    http: { ...prev.http, allowedPaths: splitLines(value) },
                  } : prev)}
                />
              </div>
            ) : null}

            {connectorDraft.type === 'cli' ? (
              <div className="space-y-3 rounded-[18px] border border-[#EAE4DB] bg-[#FAF8F4] p-4">
                <InputField
                  label="Command"
                  value={connectorDraft.cli.command}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    cli: { ...prev.cli, command: value },
                  } : prev)}
                />
                <TextAreaField
                  label="Fixed arguments"
                  rows={3}
                  value={connectorDraft.cli.fixedArguments.join('\n')}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    cli: { ...prev.cli, fixedArguments: splitLines(value) },
                  } : prev)}
                />
              </div>
            ) : null}

            {connectorDraft.type === 'mcp' ? (
              <div className="space-y-3 rounded-[18px] border border-[#EAE4DB] bg-[#FAF8F4] p-4">
                <InputField
                  label="Server name"
                  value={connectorDraft.mcp.serverName}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    mcp: { ...prev.mcp, serverName: value },
                  } : prev)}
                />
                <InputField
                  label="Command"
                  value={connectorDraft.mcp.command}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    mcp: { ...prev.mcp, command: value },
                  } : prev)}
                />
                <TextAreaField
                  label="Arguments"
                  rows={3}
                  value={connectorDraft.mcp.arguments.join('\n')}
                  onChange={value => setConnectorDraft(prev => prev ? {
                    ...prev,
                    mcp: { ...prev.mcp, arguments: splitLines(value) },
                  } : prev)}
                />
              </div>
            ) : null}
          </div>
        ) : null}
      </ModalShell>

      <ModalShell
        open={roleModalOpen}
        title={roleModalTarget === 'workflow' ? 'Add Workflow Role' : 'Add Role'}
        onClose={() => {
          void closeRoleModal();
        }}
        actions={(
          <>
            <button onClick={() => { void closeRoleModal(); }} className="ghost-action">Close</button>
            <button onClick={() => { void handleSubmitRoleDraft(); }} className="solid-action">
              <Plus size={14} /> {roleModalTarget === 'workflow' ? 'Add to workflow' : 'Add role'}
            </button>
          </>
        )}
      >
        {roleDraft ? (
          <div className="space-y-3">
            <div className="text-[12px] text-gray-500">
              The latest unfinished role draft is stored automatically when you close this dialog.
            </div>
            <div className="grid grid-cols-2 gap-3">
              <InputField
                label="Role ID"
                value={roleDraft.id}
                onChange={value => setRoleDraft(prev => prev ? { ...prev, id: value } : prev)}
              />
              <InputField
                label="Role name"
                value={roleDraft.name}
                onChange={value => setRoleDraft(prev => prev ? { ...prev, name: value } : prev)}
              />
            </div>

            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="field-label">Provider</label>
                <select
                  className="panel-input mt-1"
                  value={roleDraft.provider}
                  onChange={event => {
                    const selectedProviderName = event.target.value;
                    const configuredProvider = settingsState.providers.find(provider => provider.providerName === selectedProviderName);
                    setRoleDraft(prev => prev ? {
                      ...prev,
                      provider: selectedProviderName,
                      model: configuredProvider?.model || prev.model,
                    } : prev);
                  }}
                >
                  <option value="">Default</option>
                  {settingsState.providers.map(provider => (
                    <option key={provider.key} value={provider.providerName}>
                      {provider.providerName}
                    </option>
                  ))}
                </select>
              </div>

              <InputField
                label="Model"
                value={roleDraft.model}
                onChange={value => setRoleDraft(prev => prev ? { ...prev, model: value } : prev)}
              />
            </div>

            <TextAreaField
              label="System prompt"
              rows={6}
              value={roleDraft.systemPrompt}
              onChange={value => setRoleDraft(prev => prev ? { ...prev, systemPrompt: value } : prev)}
            />

            <TextAreaField
              label="Allowed connectors"
              rows={4}
              value={roleDraft.connectorsText}
              onChange={value => setRoleDraft(prev => prev ? { ...prev, connectorsText: value } : prev)}
            />
          </div>
        ) : null}
      </ModalShell>

      {statusMessage ? (
        <div className={`absolute bottom-5 left-1/2 -translate-x-1/2 px-4 py-2 rounded-[16px] shadow-[0_18px_36px_rgba(17,24,39,0.16)] text-[13px] font-medium z-50 ${statusMessage.type === 'success' ? 'bg-green-500 text-white' : statusMessage.type === 'error' ? 'bg-red-500 text-white' : 'bg-gray-900 text-white'}`}>
          {statusMessage.text}
        </div>
      ) : null}
    </div>
  );
}

function CloudConfigSection(props: {
  userConfigState: UserConfigState;
  setUserConfigState: React.Dispatch<React.SetStateAction<UserConfigState>>;
  runtimeConfig: Pick<StudioSettingsState, 'runtimeMode' | 'localRuntimeUrl' | 'remoteRuntimeUrl'>;
  flash: (msg: string, type: 'success' | 'error') => void;
}) {
  const { userConfigState, setUserConfigState, runtimeConfig, flash } = props;
  const [filterText, setFilterText] = useState('');
  const [dropdownOpen, setDropdownOpen] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setUserConfigState(prev => ({ ...prev, modelsLoading: true }));
      try {
        const result = await api.userConfig.models();
        if (!cancelled) {
          setUserConfigState(prev => ({
            ...prev,
            providers: result?.providers ?? [],
            supportedModels: result?.supported_models ?? [],
            modelsByProvider: result?.models_by_provider ?? {},
            modelsLoading: false,
          }));
        }
      } catch {
        if (!cancelled) setUserConfigState(prev => ({ ...prev, providers: [], supportedModels: [], modelsByProvider: {}, modelsLoading: false }));
      }
    })();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && e.target instanceof Node && !containerRef.current.contains(e.target)) setDropdownOpen(false);
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const readyProviders = useMemo(
    () => userConfigState.providers.filter(p => p.status === 'ready'),
    [userConfigState.providers],
  );

  const gatewayProviders = useMemo(
    () => readyProviders.filter(p => (p.source || USER_CONFIG_PROVIDER_SOURCE_GATEWAY) === USER_CONFIG_PROVIDER_SOURCE_GATEWAY),
    [readyProviders],
  );

  const serviceProviders = useMemo(
    () => userConfigState.providers.filter(p => p.source === USER_CONFIG_PROVIDER_SOURCE_SERVICE),
    [userConfigState.providers],
  );

  const readyChronoLlmProvider = useMemo(
    () => serviceProviders.find(p => p.provider_slug === 'chrono-llm' && p.status === 'ready') || null,
    [serviceProviders],
  );

  const preferredRoute = normalizeUserLlmRoute(userConfigState.preferredLlmRoute);
  const effectiveRoute = preferredRoute === USER_LLM_ROUTE_AUTO
    ? (readyChronoLlmProvider ? readyChronoLlmProvider.provider_slug : USER_LLM_ROUTE_GATEWAY)
    : preferredRoute;

  const routeOptions = useMemo(() => {
    const options: Array<{ value: string; label: string; note?: string }> = [
      {
        value: USER_LLM_ROUTE_AUTO,
        label: 'Auto',
        note: readyChronoLlmProvider ? `Currently ${readyChronoLlmProvider.provider_name || 'chrono-llm'}` : 'Currently NyxID Gateway',
      },
      {
        value: USER_LLM_ROUTE_GATEWAY,
        label: 'NyxID Gateway',
      },
    ];

    const seen = new Set(options.map(option => option.value));
    for (const provider of serviceProviders) {
      const slug = provider.provider_slug;
      if (!slug || seen.has(slug)) continue;
      seen.add(slug);
      options.push({
        value: slug,
        label: provider.provider_name || slug,
        note: provider.status === 'ready' ? 'Ready' : 'Unavailable',
      });
    }

    if (!seen.has(preferredRoute) && preferredRoute !== USER_LLM_ROUTE_AUTO && preferredRoute !== USER_LLM_ROUTE_GATEWAY) {
      options.push({
        value: preferredRoute,
        label: preferredRoute,
        note: 'Unavailable',
      });
    }

    return options;
  }, [preferredRoute, readyChronoLlmProvider, serviceProviders]);

  const groupedModels = useMemo(() => {
    const query = filterText.trim().toLowerCase();
    const providerOrder = effectiveRoute === USER_LLM_ROUTE_GATEWAY
      ? gatewayProviders
      : userConfigState.providers.filter(provider => provider.provider_slug === effectiveRoute);

    return providerOrder
      .map(provider => ({
        label: provider.provider_name || provider.provider_slug,
        models: (userConfigState.modelsByProvider[provider.provider_slug] || [])
          .filter(model => !query || model.toLowerCase().includes(query)),
      }))
      .filter(group => group.models.length > 0);
  }, [effectiveRoute, filterText, gatewayProviders, userConfigState.modelsByProvider, userConfigState.providers]);

  const hasData = groupedModels.length > 0;
  const effectiveRouteLabel = routeOptions.find(option => option.value === effectiveRoute)?.label
    || (effectiveRoute === USER_LLM_ROUTE_GATEWAY ? 'NyxID Gateway' : effectiveRoute);

  return (
    <div className="max-w-[920px] space-y-8">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="panel-eyebrow">Settings</div>
          <div className="panel-title">LLM</div>
          <div className="text-[12px] text-gray-400 mt-1">Per-user configuration stored on NyxID. Changes sync across all your devices.</div>
        </div>
        <button
          onClick={async () => {
            try {
              setUserConfigState(prev => ({ ...prev, loading: true }));
              await api.userConfig.save({
                defaultModel: userConfigState.defaultModel.trim(),
                preferredLlmRoute: normalizeUserLlmRoute(userConfigState.preferredLlmRoute),
                runtimeMode: runtimeConfig.runtimeMode,
                localRuntimeBaseUrl: normalizeRuntimeUrl(runtimeConfig.localRuntimeUrl, DEFAULT_LOCAL_RUNTIME_URL),
                remoteRuntimeBaseUrl: normalizeRuntimeUrl(runtimeConfig.remoteRuntimeUrl, DEFAULT_REMOTE_RUNTIME_URL),
              });
              flash('LLM config saved', 'success');
            } catch (error: any) {
              flash(error?.message || 'Failed to save LLM config', 'error');
            } finally {
              setUserConfigState(prev => ({ ...prev, loading: false }));
            }
          }}
          disabled={userConfigState.loading}
          className="solid-action"
        >
          {userConfigState.loading ? 'Saving...' : 'Save config'}
        </button>
      </div>

      <div className="settings-section-card space-y-4">
        <div className="section-heading">Preferred route</div>
        <div>
          <label className="field-label">LLM route</label>
          <select
            className="panel-input mt-1"
            value={preferredRoute}
            onChange={event => {
              const nextRoute = normalizeUserLlmRoute(event.target.value);
              setUserConfigState(prev => ({ ...prev, preferredLlmRoute: nextRoute }));
            }}
          >
            {routeOptions.map(option => (
              <option key={option.value} value={option.value}>
                {option.note ? `${option.label} - ${option.note}` : option.label}
              </option>
            ))}
          </select>
        </div>
        <div className="text-[11px] text-gray-400 leading-relaxed">
          {preferredRoute === USER_LLM_ROUTE_AUTO
            ? `Auto prefers chrono-llm when it is available and otherwise falls back to NyxID Gateway. It is currently using ${effectiveRouteLabel}.`
            : preferredRoute === USER_LLM_ROUTE_GATEWAY
            ? 'All Aevatar chat requests will go through NyxID Gateway.'
            : `${effectiveRouteLabel} will be used for Aevatar chat requests. If it is unavailable, Aevatar falls back to NyxID Gateway.`}
        </div>
      </div>

      {/* Providers status */}
      {(userConfigState.modelsLoading || readyProviders.length > 0) && (
        <div className="settings-section-card space-y-3">
          <div className="section-heading">Connected providers</div>
          {userConfigState.modelsLoading ? (
            <div className="flex items-center gap-2 py-2 text-[12px] text-gray-400">
              <Loader2 size={16} className="animate-spin" />
              <span>Loading providers...</span>
            </div>
          ) : (
            <div className="flex flex-wrap gap-2">
              {userConfigState.providers.map(p => (
                <span
                  key={p.provider_slug}
                  className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-[12px] font-medium ${
                    p.status === 'ready'
                      ? 'bg-green-50 text-green-700'
                      : p.status === 'expired'
                      ? 'bg-amber-50 text-amber-700'
                      : 'bg-gray-100 text-gray-400'
                  }`}
                >
                  <span className={`inline-block w-1.5 h-1.5 rounded-full ${
                    p.status === 'ready' ? 'bg-green-500' : p.status === 'expired' ? 'bg-amber-500' : 'bg-gray-300'
                  }`} />
                  {p.provider_name}
                </span>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Model selection */}
      <div className="settings-section-card space-y-4">
        <div className="section-heading">Default model</div>
        <div ref={containerRef} className="relative">
          <label className="field-label">Model</label>
          {hasData ? (
            <div className="relative mt-1">
              <input
                ref={inputRef}
                className="panel-input pr-8"
                value={dropdownOpen ? filterText : userConfigState.defaultModel}
                placeholder={userConfigState.modelsLoading ? 'Loading...' : 'Select a model...'}
                onChange={e => { setFilterText(e.target.value); if (!dropdownOpen) setDropdownOpen(true); }}
                onFocus={() => { setFilterText(''); setDropdownOpen(true); }}
              />
              <button
                type="button"
                className="absolute right-2 top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600"
                onClick={() => { setDropdownOpen(!dropdownOpen); if (!dropdownOpen) { setFilterText(''); inputRef.current?.focus(); } }}
                tabIndex={-1}
              >
                <svg width="12" height="12" viewBox="0 0 12 12" fill="none"><path d="M3 5l3 3 3-3" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round"/></svg>
              </button>
              {dropdownOpen && (
                <div className="absolute z-50 mt-1 w-full max-h-[280px] overflow-auto rounded-md border border-gray-200 bg-white shadow-lg">
                  {userConfigState.modelsLoading ? (
                    <div className="px-3 py-2 text-[12px] text-gray-400">Loading...</div>
                  ) : groupedModels.length === 0 ? (
                    <div className="px-3 py-2 text-[12px] text-gray-400">No matching models</div>
                  ) : (
                    groupedModels.map(group => (
                      <div key={group.label}>
                        <div className="px-3 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-gray-400">{group.label}</div>
                        {group.models.map(model => (
                          <button
                            key={model}
                            type="button"
                            className={`w-full text-left px-3 py-1.5 text-[13px] hover:bg-gray-50 ${model === userConfigState.defaultModel ? 'bg-blue-50 text-blue-700 font-medium' : 'text-gray-700'}`}
                            onClick={() => {
                              setUserConfigState(prev => ({ ...prev, defaultModel: model }));
                              setDropdownOpen(false);
                              setFilterText('');
                            }}
                          >
                            {model}
                          </button>
                        ))}
                      </div>
                    ))
                  )}
                </div>
              )}
            </div>
          ) : (
            <input
              className="panel-input mt-1"
              value={userConfigState.defaultModel}
              placeholder={userConfigState.modelsLoading ? 'Loading...' : 'Enter model name...'}
              onChange={e => setUserConfigState(prev => ({ ...prev, defaultModel: e.target.value }))}
            />
          )}
        </div>
        <div className="text-[11px] text-gray-400 leading-relaxed">
          The default model applied to {effectiveRouteLabel}. Select from supported models, or type a model name manually.
        </div>
      </div>
    </div>
  );
}

function AppLoadingScreen() {
  return (
    <div className="min-h-screen bg-[#F2F1EE] text-gray-800 px-6 py-8">
      <div className="mx-auto flex min-h-[calc(100vh-4rem)] max-w-[960px] items-center justify-center">
        <div className="w-full max-w-[460px] rounded-[32px] border border-[#E6E3DE] bg-white/96 p-8 shadow-[0_28px_70px_rgba(15,23,42,0.08)]">
          <div className="panel-eyebrow">Aevatar App</div>
          <div className="mt-3 flex items-center gap-3">
            <AevatarBrandMark size={48} className="shrink-0 rounded-[18px]" />
            <div>
              <div className="text-[24px] font-semibold text-gray-900">Preparing studio</div>
              <div className="text-[13px] text-gray-500">Loading workspace context, catalogs, and runtime settings.</div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function AppAuthenticationGate(props: {
  providerDisplayName: string;
  loginUrl: string;
  errorMessage: string;
}) {
  return (
    <div
      className="studio-shell min-h-screen bg-[#F2F1EE] px-6 py-8 text-gray-800"
      data-appearance="blue"
      data-color-mode="light"
    >
      <div className="mx-auto flex min-h-[calc(100vh-4rem)] max-w-[1040px] items-center justify-center">
        <div className="grid w-full max-w-[920px] gap-6 rounded-[36px] border border-[#E6E3DE] bg-white/96 p-6 shadow-[0_30px_72px_rgba(15,23,42,0.08)] md:grid-cols-[minmax(0,1.1fr)_320px] md:p-8">
          <div className="rounded-[28px] border border-[#ECE7DF] bg-[#FAF8F4] p-6 md:p-7">
            <div className="panel-eyebrow">Aevatar App</div>
            <div className="mt-3 flex items-start gap-4">
              <div className="flex h-14 w-14 flex-shrink-0 items-center justify-center rounded-[20px] bg-white text-[var(--accent,#2563eb)] shadow-[0_12px_30px_rgba(37,99,235,0.08)]">
                <Shield size={22} />
              </div>
              <div>
                <div className="text-[28px] font-semibold leading-tight text-gray-900">Sign in to open Workflow Studio</div>
                <div className="mt-3 max-w-[520px] text-[14px] leading-6 text-gray-500">
                  Studio content, workflow execution, and scope-backed assets are only available after {props.providerDisplayName || 'NyxID'} authentication succeeds.
                </div>
              </div>
            </div>

            <div className="mt-6 rounded-[22px] border border-[#E8E2D9] bg-white px-5 py-4">
              <div className="text-[12px] font-semibold uppercase tracking-[0.14em] text-gray-400">Access policy</div>
              <div className="mt-2 text-[14px] leading-6 text-gray-600">
                When the app is not authenticated, the workflow canvas, execution panel, and runtime actions stay locked. Sign in first, then Studio will load normally.
              </div>
            </div>
          </div>

          <div className="flex flex-col justify-between rounded-[28px] border border-[#ECE7DF] bg-white p-6">
            <div>
              <div className="text-[13px] font-semibold uppercase tracking-[0.14em] text-gray-400">Authentication</div>
              <div className="mt-3 text-[22px] font-semibold text-gray-900">{props.providerDisplayName || 'NyxID'}</div>
              <div className="mt-2 text-[14px] leading-6 text-gray-500">
                Use your existing identity session to continue into the app.
              </div>
              {props.errorMessage ? (
                <div className="mt-4 rounded-[18px] border border-amber-200 bg-amber-50 px-4 py-3 text-[13px] leading-5 text-amber-800">
                  {props.errorMessage}
                </div>
              ) : null}
            </div>

            <div className="mt-6 space-y-3">
              <button
                onClick={() => nyxid.loginWithRedirect('/')}
                className="solid-action w-full justify-center !no-underline"
                title={`Sign in with ${props.providerDisplayName || 'NyxID'}.`}
              >
                <Shield size={14} /> Sign in
              </button>
              <div className="text-[12px] leading-5 text-gray-400">
                After sign-in completes, the app will return to Studio automatically.
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function RailButton(props: { active: boolean; label: string; icon: ReactNode; onClick: () => void }) {
  return (
    <button
      onClick={props.onClick}
      title={props.label}
      className={`rail-button ${props.active ? 'active' : ''}`}
    >
      {props.icon}
    </button>
  );
}

function SettingsNavButton(props: {
  active: boolean;
  icon: ReactNode;
  title: string;
  description: string;
  onClick: () => void;
}) {
  return (
    <button
      onClick={props.onClick}
      title={props.description}
      className={`settings-nav-button ${props.active ? 'active' : ''}`}
    >
      <div className="settings-nav-icon">
        {props.icon}
      </div>
      <div className="min-w-0 text-left">
        <div className="text-[14px] font-semibold text-gray-800">{props.title}</div>
      </div>
    </button>
  );
}

function SettingsStatusPill(props: { status: 'idle' | 'testing' | 'success' | 'error' }) {
  const label = props.status === 'success'
    ? 'Reachable'
    : props.status === 'testing'
      ? 'Testing'
      : props.status === 'error'
        ? 'Unreachable'
        : 'Idle';

  return (
    <span className={`settings-status-pill ${props.status}`}>
      {label}
    </span>
  );
}

function InfoPopover(props: {
  title: string;
  content: ReactNode;
  align?: 'left' | 'right';
  buttonTooltip?: string;
  buttonClassName?: string;
  cardClassName?: string;
  hideTitle?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const popoverRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (!(target instanceof globalThis.Node) || !popoverRef.current?.contains(target)) {
        setOpen(false);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
    };
  }, [open]);

  return (
    <div ref={popoverRef} className="info-popover">
      <button
        type="button"
        onClick={event => {
          event.stopPropagation();
          setOpen(value => !value);
        }}
        className={`info-popover-button ${open ? 'active' : ''} ${props.buttonClassName || ''}`}
        title={props.buttonTooltip || props.title}
      >
        <CircleHelp size={14} />
      </button>
      {open ? (
        <div
          className={`info-popover-card ${props.align === 'left' ? 'left-0' : 'right-0'} ${props.cardClassName || ''}`}
          onClick={event => event.stopPropagation()}
        >
          {props.hideTitle ? null : <div className="info-popover-title">{props.title}</div>}
          <div className={props.hideTitle ? '' : 'mt-2'}>{props.content}</div>
        </div>
      ) : null}
    </div>
  );
}

function ModalShell(props: {
  open: boolean;
  title: string;
  children: ReactNode;
  actions?: ReactNode;
  onClose: () => void;
}) {
  if (!props.open) {
    return null;
  }

  return (
    <div className="modal-overlay" onClick={props.onClose}>
      <div className="modal-shell" onClick={event => event.stopPropagation()}>
        <div className="modal-header">
          <div>
            <div className="panel-eyebrow">Catalog</div>
            <div className="panel-title !mt-0">{props.title}</div>
          </div>
          <button onClick={props.onClose} title="Close dialog." className="panel-icon-button">
            <X size={16} />
          </button>
        </div>
        <div className="modal-body">{props.children}</div>
        <div className="modal-footer">{props.actions}</div>
      </div>
    </div>
  );
}

function DrawerIconButton(props: { active: boolean; label: string; icon: ReactNode; onClick: () => void }) {
  return (
    <button
      onClick={props.onClick}
      title={props.label}
      className={`drawer-icon-button ${props.active ? 'active' : ''}`}
    >
      {props.icon}
    </button>
  );
}

function InputField(props: { label: string; value: string; disabled?: boolean; onChange: (value: string) => void }) {
  return (
    <div>
      <label className="field-label">{props.label}</label>
      <input
        className="panel-input mt-1"
        disabled={props.disabled}
        value={props.value}
        onChange={event => props.onChange(event.target.value)}
      />
    </div>
  );
}

function TextAreaField(props: { label: string; value: string; rows?: number; onChange: (value: string) => void }) {
  return (
    <div>
      <label className="field-label">{props.label}</label>
      <textarea
        rows={props.rows ?? 4}
        className="panel-textarea mt-1"
        value={props.value}
        onChange={event => props.onChange(event.target.value)}
      />
    </div>
  );
}

function EmptyPanel(props: { icon: ReactNode; title: string; copy: string }) {
  return (
    <div className="h-full rounded-[22px] border border-dashed border-[#E6E0D6] bg-[#FAF8F4] px-5 py-8 text-center flex flex-col items-center justify-center">
      <div className="w-12 h-12 rounded-[16px] bg-white flex items-center justify-center shadow-[0_10px_20px_rgba(17,24,39,0.05)]">
        {props.icon}
      </div>
      <div className="text-[14px] font-semibold text-gray-700 mt-3">{props.title}</div>
      <p className="text-[12px] text-gray-400 mt-1">{props.copy}</p>
    </div>
  );
}

export default App;
