import { PageContainer } from '@ant-design/pro-components';
import { useQuery, useQueryClient } from '@tanstack/react-query';
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
  buildRuntimeWorkflowsHref,
} from '@/shared/navigation/runtimeRoutes';
import type { Node } from '@xyflow/react';
import {
  Button,
  Modal,
  Space,
} from 'antd';
import React, {
  useCallback,
  useDeferredValue,
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
import { loadPlaygroundDraft } from '@/shared/playground/playgroundDraft';
import {
  saveEndpointInvocationDraftPayload,
  saveScopeDraftRunPayload,
} from '@/shared/runs/draftRunSession';
import {
  getStringValueTypeUrl,
  isAutoEncodableTextPayloadTypeUrl,
} from '@/shared/runs/protobufPayload';
import {
  applyRoleInspectorDraft,
  applyStepInspectorDraft,
  addWorkflowRole,
  cloneStudioWorkflowDocument,
  connectStepToTarget,
  createRoleInspectorDraft,
  createStepInspectorDraft,
  insertStepAfter,
  insertStepByType,
  insertCatalogRoleInWorkflow,
  removeStepConnection,
  removeWorkflowRole,
  removeStep,
  suggestBranchLabelForStep,
  updateWorkflowRole,
  type StudioNodeInspectorDraft,
} from '@/shared/studio/document';
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
  type StudioGraphNodeData,
  type StudioGraphRole,
  type StudioGraphStep,
  type StudioWorkflowLayoutDocument,
} from '@/shared/studio/graph';
import { studioApi } from '@/shared/studio/api';
import {
  buildStudioRoute,
  type StudioTab,
} from '@/shared/studio/navigation';
import type {
  WorkflowCatalogDefinition,
} from '@/shared/models/runtime/catalog';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import type {
  StudioConnectorDefinition,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioProviderSettings,
  StudioProviderType,
  StudioRoleDefinition,
  StudioScopeGAgentEndpointInput,
  StudioRuntimeTestResult,
  StudioValidationFinding,
  StudioWorkflowDocument,
  StudioWorkflowDirectory,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import { getStudioScopeBindingCurrentRevision } from '@/shared/studio/models';
import { embeddedPanelStyle } from '@/shared/ui/proComponents';
import StudioBootstrapGate from './components/StudioBootstrapGate';
import StudioInspectorPane from './components/StudioInspectorPane';
import StudioShell, {
  type StudioShellNavItem,
  type StudioWorkspacePage,
} from './components/StudioShell';
import ScriptsWorkbenchPage from '@/modules/studio/scripts/ScriptsWorkbenchPage';
import {
  dedupeStudioWorkflowSummaries,
  type StudioCatalogDraftMeta,
  type StudioConnectorCatalogItem,
  type StudioConnectorDraftItem,
  type StudioConnectorType,
  StudioConnectorsPage,
  StudioEditorPage,
  StudioExecutionPage,
  type StudioRoleCatalogItem,
  type StudioRoleDraftItem,
  StudioRolesPage,
  StudioSettingsPage,
  StudioWorkflowsPage,
} from './components/StudioWorkbenchSections';

type StudioRouteState = {
  scopeId: string;
  scopeLabel: string;
  memberId: string;
  memberLabel: string;
  workflowId: string;
  scriptId: string;
  templateWorkflow: string;
  tab: StudioTab;
  draftMode: '' | 'new';
  prompt: string;
  legacySource: '' | 'playground';
  executionId: string;
  logsMode: '' | 'popout';
};

type StudioViewMode = 'editor' | 'execution';
type StudioInspectorTab = 'node' | 'roles' | 'yaml';
type StudioSelectedGraphEdge = {
  readonly edgeId: string;
  readonly sourceStepId: string;
  readonly targetStepId: string;
  readonly branchLabel: string | null;
  readonly kind: 'next' | 'branch';
  readonly implicit: boolean;
};

type DraftSaveNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type DraftRunNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type InspectorNotice = {
  readonly type: 'success' | 'warning' | 'error';
  readonly message: string;
};

type StudioNotice = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type StudioSettingsDraft = {
  readonly runtimeBaseUrl: string;
  readonly defaultProviderName: string;
  readonly providerTypes: StudioProviderType[];
  readonly providers: StudioProviderSettings[];
};

type StudioAppearanceTheme = 'blue' | 'coral' | 'forest';
type StudioColorMode = 'light' | 'dark';

type StudioAppearancePreferences = {
  readonly appearanceTheme: StudioAppearanceTheme;
  readonly colorMode: StudioColorMode;
};

const defaultStudioAppearance: StudioAppearancePreferences = {
  appearanceTheme: 'blue',
  colorMode: 'light',
};

let studioLocalKeyCounter = 0;
const STUDIO_AUTO_RELOGIN_ATTEMPT_KEY =
  'aevatar-console:studio:auto-relogin:';

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

function describeScopeBindingTarget(result: {
  readonly displayName?: string;
  readonly serviceId?: string;
  readonly targetKind?: string;
  readonly targetName?: string;
}): string {
  const targetName =
    trimOptional(result.targetName) ||
    trimOptional(result.displayName) ||
    trimOptional(result.serviceId);

  if (!targetName) {
    return 'the default binding';
  }

  switch (trimOptional(result.targetKind).toLowerCase()) {
    case 'workflow':
      return `workflow ${targetName}`;
    case 'script':
      return `script ${targetName}`;
    case 'gagent':
      return `GAgent ${targetName}`;
    default:
      return targetName;
  }
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
    case 'scripts':
    case 'executions':
    case 'roles':
    case 'connectors':
    case 'settings':
      return value;
    default:
      return 'workflows';
  }
}

function parseDraftMode(value: string | null): '' | 'new' {
  return value === 'new' ? 'new' : '';
}

function parseLegacySource(value: string | null): '' | 'playground' {
  return value === 'playground' ? 'playground' : '';
}

function parseLogsMode(value: string | null): '' | 'popout' {
  return value === 'popout' ? 'popout' : '';
}

function readDefaultDirectoryId(
  directories: StudioWorkflowDirectory[] | undefined,
): string {
  return directories?.[0]?.directoryId ?? '';
}

function createStudioLocalKey(prefix: string): string {
  const randomUuid = globalThis.crypto?.randomUUID?.();
  if (randomUuid) {
    return `${prefix}_${randomUuid}`;
  }

  studioLocalKeyCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${studioLocalKeyCounter.toString(36)}`;
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

function splitCatalogLines(value: string): string[] {
  return String(value || '')
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function normalizeCatalogInteger(value: string, fallback: number): number {
  const parsed = Number.parseInt(String(value || '').trim(), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function createEmptyConnectorDraft(
  type: StudioConnectorType = 'http',
  name = '',
): StudioConnectorDraftItem {
  return {
    name,
    type,
    enabled: true,
    timeoutMs: '30000',
    retry: '0',
    http: {
      baseUrl: '',
      allowedMethods: ['POST'],
      allowedPaths: ['/'],
      allowedInputKeys: [],
      defaultHeaders: {},
    },
    cli: {
      command: '',
      fixedArguments: [],
      allowedOperations: [],
      allowedInputKeys: [],
      workingDirectory: '',
      environment: {},
    },
    mcp: {
      serverName: '',
      command: '',
      arguments: [],
      environment: {},
      defaultTool: '',
      allowedTools: [],
      allowedInputKeys: [],
    },
  };
}

function toConnectorCatalogItem(
  connector: StudioConnectorDefinition,
): StudioConnectorCatalogItem {
  const empty = createEmptyConnectorDraft(
    (connector.type || 'http') as StudioConnectorType,
  );

  return {
    key: createStudioLocalKey('connector'),
    name: connector.name || '',
    type: (connector.type || 'http') as StudioConnectorType,
    enabled: connector.enabled !== false,
    timeoutMs: String(connector.timeoutMs ?? 30000),
    retry: String(connector.retry ?? 0),
    http: {
      baseUrl: connector.http?.baseUrl ?? empty.http.baseUrl,
      allowedMethods:
        connector.http?.allowedMethods ?? empty.http.allowedMethods,
      allowedPaths: connector.http?.allowedPaths ?? empty.http.allowedPaths,
      allowedInputKeys:
        connector.http?.allowedInputKeys ?? empty.http.allowedInputKeys,
      defaultHeaders:
        connector.http?.defaultHeaders ?? empty.http.defaultHeaders,
    },
    cli: {
      command: connector.cli?.command ?? empty.cli.command,
      fixedArguments:
        connector.cli?.fixedArguments ?? empty.cli.fixedArguments,
      allowedOperations:
        connector.cli?.allowedOperations ?? empty.cli.allowedOperations,
      allowedInputKeys:
        connector.cli?.allowedInputKeys ?? empty.cli.allowedInputKeys,
      workingDirectory:
        connector.cli?.workingDirectory ?? empty.cli.workingDirectory,
      environment: connector.cli?.environment ?? empty.cli.environment,
    },
    mcp: {
      serverName: connector.mcp?.serverName ?? empty.mcp.serverName,
      command: connector.mcp?.command ?? empty.mcp.command,
      arguments: connector.mcp?.arguments ?? empty.mcp.arguments,
      environment: connector.mcp?.environment ?? empty.mcp.environment,
      defaultTool: connector.mcp?.defaultTool ?? empty.mcp.defaultTool,
      allowedTools: connector.mcp?.allowedTools ?? empty.mcp.allowedTools,
      allowedInputKeys:
        connector.mcp?.allowedInputKeys ?? empty.mcp.allowedInputKeys,
    },
  };
}

function toConnectorDraftItem(
  connector: StudioConnectorDefinition | null | undefined,
): StudioConnectorDraftItem {
  if (!connector) {
    return createEmptyConnectorDraft();
  }

  const catalogItem = toConnectorCatalogItem(connector);
  return {
    name: catalogItem.name,
    type: catalogItem.type,
    enabled: catalogItem.enabled,
    timeoutMs: catalogItem.timeoutMs,
    retry: catalogItem.retry,
    http: { ...catalogItem.http },
    cli: { ...catalogItem.cli },
    mcp: { ...catalogItem.mcp },
  };
}

function toConnectorDefinition(
  connector: StudioConnectorCatalogItem | StudioConnectorDraftItem,
): StudioConnectorDefinition {
  return {
    name: connector.name.trim(),
    type: connector.type,
    enabled: connector.enabled,
    timeoutMs: normalizeCatalogInteger(connector.timeoutMs, 30000),
    retry: normalizeCatalogInteger(connector.retry, 0),
    http: {
      baseUrl: connector.http.baseUrl.trim(),
      allowedMethods: connector.http.allowedMethods
        .map((item) => item.trim().toUpperCase())
        .filter(Boolean),
      allowedPaths: connector.http.allowedPaths
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.http.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
      defaultHeaders: connector.http.defaultHeaders,
    },
    cli: {
      command: connector.cli.command.trim(),
      fixedArguments: connector.cli.fixedArguments
        .map((item) => item.trim())
        .filter(Boolean),
      allowedOperations: connector.cli.allowedOperations
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.cli.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
      workingDirectory: connector.cli.workingDirectory.trim(),
      environment: connector.cli.environment,
    },
    mcp: {
      serverName: connector.mcp.serverName.trim(),
      command: connector.mcp.command.trim(),
      arguments: connector.mcp.arguments
        .map((item) => item.trim())
        .filter(Boolean),
      environment: connector.mcp.environment,
      defaultTool: connector.mcp.defaultTool.trim(),
      allowedTools: connector.mcp.allowedTools
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.mcp.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
    },
  };
}

function createUniqueConnectorName(
  connectors: readonly StudioConnectorCatalogItem[],
  type: StudioConnectorType,
): string {
  const used = new Set(
    connectors.map((connector) => connector.name.trim().toLowerCase()),
  );
  const base = `${type}_connector`;
  let index = 1;
  let candidate = base;

  while (used.has(candidate.toLowerCase())) {
    index += 1;
    candidate = `${base}_${index}`;
  }

  return candidate;
}

function hasConnectorDraftContent(
  connector: StudioConnectorDraftItem | null | undefined,
): boolean {
  if (!connector) {
    return false;
  }

  const httpHeaders = Object.entries(connector.http.defaultHeaders || {}).some(
    ([key, value]) => key.trim() || String(value || '').trim(),
  );
  const cliEnv = Object.entries(connector.cli.environment || {}).some(
    ([key, value]) => key.trim() || String(value || '').trim(),
  );
  const mcpEnv = Object.entries(connector.mcp.environment || {}).some(
    ([key, value]) => key.trim() || String(value || '').trim(),
  );

  return Boolean(
    connector.name.trim() ||
      connector.http.baseUrl.trim() ||
      connector.http.allowedMethods.some(
        (item) => item.trim() && item.trim().toUpperCase() !== 'POST',
      ) ||
      connector.http.allowedPaths.some(
        (item) => item.trim() && item.trim() !== '/',
      ) ||
      connector.http.allowedInputKeys.some((item) => item.trim()) ||
      httpHeaders ||
      connector.cli.command.trim() ||
      connector.cli.fixedArguments.some((item) => item.trim()) ||
      connector.cli.allowedOperations.some((item) => item.trim()) ||
      connector.cli.allowedInputKeys.some((item) => item.trim()) ||
      connector.cli.workingDirectory.trim() ||
      cliEnv ||
      connector.mcp.serverName.trim() ||
      connector.mcp.command.trim() ||
      connector.mcp.arguments.some((item) => item.trim()) ||
      connector.mcp.defaultTool.trim() ||
      connector.mcp.allowedTools.some((item) => item.trim()) ||
      connector.mcp.allowedInputKeys.some((item) => item.trim()) ||
      mcpEnv
  );
}

function createRoleCatalogItem(role: StudioRoleDefinition): StudioRoleCatalogItem {
  return {
    key: createStudioLocalKey('role'),
    id: role.id || '',
    name: role.name || role.id || '',
    systemPrompt: role.systemPrompt || '',
    provider: role.provider || '',
    model: role.model || '',
    connectorsText: Array.isArray(role.connectors)
      ? role.connectors.join('\n')
      : '',
  };
}

function createEmptyRoleDraft(): StudioRoleDraftItem {
  return {
    id: '',
    name: '',
    systemPrompt: '',
    provider: '',
    model: '',
    connectorsText: '',
  };
}

function toRoleDraftItem(
  role: StudioRoleDefinition | null | undefined,
): StudioRoleDraftItem {
  if (!role) {
    return createEmptyRoleDraft();
  }

  const catalogItem = createRoleCatalogItem(role);
  return {
    id: catalogItem.id,
    name: catalogItem.name,
    systemPrompt: catalogItem.systemPrompt,
    provider: catalogItem.provider,
    model: catalogItem.model,
    connectorsText: catalogItem.connectorsText,
  };
}

function toRoleDefinition(
  role: StudioRoleCatalogItem | StudioRoleDraftItem,
): StudioRoleDefinition {
  return {
    id: role.id.trim(),
    name: (role.name || role.id).trim(),
    systemPrompt: role.systemPrompt || '',
    provider: role.provider.trim(),
    model: role.model.trim(),
    connectors: splitCatalogLines(role.connectorsText),
  };
}

function createUniqueRoleId(
  existingRoles: readonly StudioRoleCatalogItem[],
  base = 'role',
): string {
  const normalizedBase =
    (base || 'role').replace(/[^a-z0-9_]+/gi, '_').toLowerCase() || 'role';
  const used = new Set(
    existingRoles.map((role) => role.id.trim().toLowerCase()).filter(Boolean),
  );
  let index = 1;
  let candidate = normalizedBase;

  while (used.has(candidate)) {
    index += 1;
    candidate = `${normalizedBase}_${index}`;
  }

  return candidate;
}

function hasRoleDraftContent(role: StudioRoleDraftItem | null | undefined): boolean {
  if (!role) {
    return false;
  }

  return Boolean(
    role.id.trim() ||
      role.name.trim() ||
      role.systemPrompt.trim() ||
      role.provider.trim() ||
      role.model.trim() ||
      role.connectorsText.trim(),
  );
}

function createSettingsDraft(
  settings: StudioSettingsDraft | null | undefined,
): StudioSettingsDraft | null {
  if (!settings) {
    return null;
  }

  return {
    runtimeBaseUrl: settings.runtimeBaseUrl || '',
    defaultProviderName: settings.defaultProviderName || '',
    providerTypes: [...settings.providerTypes],
    providers: settings.providers.map((provider) => ({
      ...provider,
      apiKey: provider.apiKey || '',
      apiKeyConfigured: Boolean(provider.apiKeyConfigured),
      clearApiKeyRequested: false,
    })),
  };
}

function normalizeSettingsDraftForHostMode(
  settings: StudioSettingsDraft | null | undefined,
  _hostMode: 'embedded' | 'proxy',
): StudioSettingsDraft | null {
  if (!settings) {
    return null;
  }

  return settings;
}

function createProviderDraft(
  providerTypes: StudioProviderType[],
  existingProviders: StudioProviderSettings[],
): StudioProviderSettings {
  const preferredType =
    providerTypes.find((item) => item.recommended) ??
    providerTypes[0] ?? {
      id: 'openai',
      displayName: 'OpenAI',
      category: 'llm',
      description: '',
      recommended: true,
      defaultEndpoint: '',
      defaultModel: '',
    };
  const used = new Set(
    existingProviders.map((provider) => provider.providerName.trim().toLowerCase()),
  );
  const baseName = preferredType.id || 'provider';
  let index = 1;
  let nextName = `${baseName}-${index}`;
  while (used.has(nextName.toLowerCase())) {
    index += 1;
    nextName = `${baseName}-${index}`;
  }

  return {
    providerName: nextName,
    providerType: preferredType.id,
    displayName: preferredType.displayName,
    category: preferredType.category,
    description: preferredType.description,
    model: preferredType.defaultModel,
    endpoint: preferredType.defaultEndpoint,
    apiKey: '',
    apiKeyConfigured: false,
    clearApiKeyRequested: false,
  };
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
      scopeLabel: '',
      memberId: '',
      memberLabel: '',
      workflowId: '',
      scriptId: '',
      templateWorkflow: '',
      tab: 'workflows',
      draftMode: '',
      prompt: '',
      legacySource: '',
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
  return {
    scopeId: trimOptional(params.get('scopeId')),
    scopeLabel: trimOptional(params.get('scopeLabel')),
    memberId: trimOptional(params.get('memberId')),
    memberLabel: trimOptional(params.get('memberLabel')),
    workflowId: trimOptional(params.get('workflow')),
    scriptId: trimOptional(params.get('script')),
    templateWorkflow: trimOptional(params.get('template')),
    tab: parseStudioTab(params.get('tab')),
    draftMode: parseDraftMode(params.get('draft')),
    prompt: trimOptional(params.get('prompt')),
    legacySource: parseLegacySource(params.get('legacy')),
    executionId: trimOptional(params.get('execution')),
    logsMode: parseLogsMode(params.get('logs')),
  };
}

function readInitialWorkspacePage(state: StudioRouteState): StudioWorkspacePage {
  switch (state.tab) {
    case 'scripts':
      return 'scripts';
    case 'roles':
    case 'connectors':
    case 'settings':
      return state.tab;
    case 'studio':
    case 'executions':
      return 'studio';
    default:
      if (state.scriptId) {
        return 'scripts';
      }

      return state.workflowId ||
        state.templateWorkflow ||
        state.draftMode === 'new' ||
        state.executionId ||
        state.prompt
        ? 'studio'
        : 'workflows';
}
}

function readInitialStudioView(state: StudioRouteState): StudioViewMode {
  return state.tab === 'executions' ? 'execution' : 'editor';
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

function readValidationSummary(
  findings: StudioValidationFinding[],
  messages?: {
    success: string;
    warning: string;
    error: string;
  },
): InspectorNotice {
  if (findings.length === 0) {
    return {
      type: 'success',
      message: messages?.success || 'Applied inspector changes to the YAML draft.',
    };
  }

  return hasValidationError(findings)
    ? {
        type: 'error',
        message:
          messages?.error ||
          'Applied changes, but Studio validation now reports blocking errors.',
      }
    : {
        type: 'warning',
        message:
          messages?.warning ||
          'Applied changes, but Studio validation returned warnings.',
      };
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
  const routeWorkspacePage = useMemo(
    () => readInitialWorkspacePage(routeState),
    [routeState],
  );
  const routeStudioView = useMemo(
    () => readInitialStudioView(routeState),
    [routeState],
  );
  const isStudioLocation =
    typeof window !== 'undefined' && window.location.pathname === '/studio';
  const nyxIdConfig = useMemo(() => getNyxIDRuntimeConfig(), []);
  const queryClient = useQueryClient();
  const [workspacePage, setWorkspacePage] = useState<StudioWorkspacePage>(
    () => readInitialWorkspacePage(readStudioRouteState()),
  );
  const [studioView, setStudioView] = useState<StudioViewMode>(
    () => readInitialStudioView(readStudioRouteState()),
  );
  const [workflowSearch, setWorkflowSearch] = useState('');
  const [showWorkflowDirectoryForm, setShowWorkflowDirectoryForm] =
    useState(false);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState(
    () => readStudioRouteState().workflowId,
  );
  const [selectedScriptId, setSelectedScriptId] = useState(
    () => readStudioRouteState().scriptId,
  );
  const [selectedExecutionId, setSelectedExecutionId] = useState(
    () => readStudioRouteState().executionId,
  );
  const [templateWorkflow, setTemplateWorkflow] = useState(
    () => readStudioRouteState().templateWorkflow,
  );
  const [draftMode, setDraftMode] = useState<'' | 'new'>(
    () => readStudioRouteState().draftMode,
  );
  const [legacySource, setLegacySource] = useState<'' | 'playground'>(
    () => readStudioRouteState().legacySource,
  );
  const [draftYaml, setDraftYaml] = useState('');
  const [draftWorkflowName, setDraftWorkflowName] = useState('');
  const [draftFileName, setDraftFileName] = useState('');
  const [draftDirectoryId, setDraftDirectoryId] = useState('');
  const [draftWorkflowLayout, setDraftWorkflowLayout] = useState<unknown | null>(
    null,
  );
  const [draftSourceKey, setDraftSourceKey] = useState('');
  const [savePending, setSavePending] = useState(false);
  const [saveNotice, setSaveNotice] = useState<DraftSaveNotice | null>(null);
  const [runPrompt, setRunPrompt] = useState(() => readStudioRouteState().prompt);
  const [runPending, setRunPending] = useState(false);
  const [runNotice, setRunNotice] = useState<DraftRunNotice | null>(null);
  const [publishPending, setPublishPending] = useState(false);
  const [publishNotice, setPublishNotice] = useState<StudioNotice | null>(null);
  const [bindingActivationRevisionId, setBindingActivationRevisionId] =
    useState('');
  const [bindingRetirementRevisionId, setBindingRetirementRevisionId] =
    useState('');
  const [workflowImportPending, setWorkflowImportPending] = useState(false);
  const [workflowImportNotice, setWorkflowImportNotice] =
    useState<StudioNotice | null>(null);
  const [askAiPrompt, setAskAiPrompt] = useState('');
  const [askAiPending, setAskAiPending] = useState(false);
  const [askAiNotice, setAskAiNotice] = useState<StudioNotice | null>(null);
  const [askAiReasoning, setAskAiReasoning] = useState('');
  const [askAiAnswer, setAskAiAnswer] = useState('');
  const [inspectorTab, setInspectorTab] = useState<StudioInspectorTab>('node');
  const [selectedGraphNodeId, setSelectedGraphNodeId] = useState('');
  const [selectedGraphEdgeId, setSelectedGraphEdgeId] = useState('');
  const [nodeInspectorDraft, setNodeInspectorDraft] =
    useState<StudioNodeInspectorDraft | null>(null);
  const [inspectorPending, setInspectorPending] = useState(false);
  const [inspectorNotice, setInspectorNotice] = useState<InspectorNotice | null>(
    null,
  );
  const [executionStopPending, setExecutionStopPending] = useState(false);
  const [executionNotice, setExecutionNotice] = useState<StudioNotice | null>(null);
  const [connectorCatalogDraft, setConnectorCatalogDraft] = useState<
    StudioConnectorCatalogItem[]
  >([]);
  const [selectedConnectorKey, setSelectedConnectorKey] = useState('');
  const [connectorSearch, setConnectorSearch] = useState('');
  const [connectorModalOpen, setConnectorModalOpen] = useState(false);
  const [connectorDraft, setConnectorDraft] =
    useState<StudioConnectorDraftItem | null>(null);
  const [connectorCatalogPending, setConnectorCatalogPending] = useState(false);
  const [connectorImportPending, setConnectorImportPending] = useState(false);
  const [connectorCatalogNotice, setConnectorCatalogNotice] =
    useState<StudioNotice | null>(null);
  const [roleCatalogDraft, setRoleCatalogDraft] = useState<
    StudioRoleCatalogItem[]
  >([]);
  const [selectedRoleKey, setSelectedRoleKey] = useState('');
  const [roleSearch, setRoleSearch] = useState('');
  const [roleModalOpen, setRoleModalOpen] = useState(false);
  const [roleDraft, setRoleDraft] = useState<StudioRoleDraftItem | null>(null);
  const [roleCatalogPending, setRoleCatalogPending] = useState(false);
  const [roleImportPending, setRoleImportPending] = useState(false);
  const [roleCatalogNotice, setRoleCatalogNotice] =
    useState<StudioNotice | null>(null);
  const [settingsDraft, setSettingsDraft] = useState<StudioSettingsDraft | null>(
    null,
  );
  const [selectedProviderName, setSelectedProviderName] = useState('');
  const [settingsPending, setSettingsPending] = useState(false);
  const [settingsNotice, setSettingsNotice] = useState<StudioNotice | null>(null);
  const [runtimeTestPending, setRuntimeTestPending] = useState(false);
  const [runtimeTestResult, setRuntimeTestResult] =
    useState<StudioRuntimeTestResult | null>(null);
  const [directoryPath, setDirectoryPath] = useState('');
  const [directoryLabel, setDirectoryLabel] = useState('');
  const [logsPopoutMode] = useState(() => readStudioRouteState().logsMode);
  const [appliedRouteSnapshot, setAppliedRouteSnapshot] = useState(
    locationSnapshot,
  );
  const [promptHistory, setPromptHistory] = useState<
    PlaygroundPromptHistoryEntry[]
  >(() => loadPlaygroundPromptHistory());
  const [scriptsHasUnsavedChanges, setScriptsHasUnsavedChanges] = useState(false);
  const [pendingWorkspacePage, setPendingWorkspacePage] =
    useState<StudioWorkspacePage | null>(null);
  const handledLocationSnapshotRef = useRef(locationSnapshot);
  const legacyPlaygroundDraft = useMemo(() => loadPlaygroundDraft(), []);
  const workflowImportInputRef = useRef<HTMLInputElement | null>(null);
  const connectorImportInputRef = useRef<HTMLInputElement | null>(null);
  const roleImportInputRef = useRef<HTMLInputElement | null>(null);
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
  const studioAppearance = defaultStudioAppearance;

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
    setWorkspacePage((currentPage) =>
      currentPage === routeWorkspacePage ? currentPage : routeWorkspacePage,
    );
    setStudioView((currentView) =>
      currentView === routeStudioView ? currentView : routeStudioView,
    );
    setSelectedWorkflowId((currentWorkflowId) =>
      trimOptional(currentWorkflowId) === routeState.workflowId
        ? currentWorkflowId
        : routeState.workflowId,
    );
    setSelectedScriptId((currentScriptId) =>
      trimOptional(currentScriptId) === routeState.scriptId
        ? currentScriptId
        : routeState.scriptId,
    );
    setSelectedExecutionId((currentExecutionId) =>
      trimOptional(currentExecutionId) === routeState.executionId
        ? currentExecutionId
        : routeState.executionId,
    );
    setTemplateWorkflow((currentTemplateWorkflow) =>
      trimOptional(currentTemplateWorkflow) === routeState.templateWorkflow
        ? currentTemplateWorkflow
        : routeState.templateWorkflow,
    );
    setDraftMode((currentDraftMode) =>
      currentDraftMode === routeState.draftMode
        ? currentDraftMode
        : routeState.draftMode,
    );
    setLegacySource((currentLegacySource) =>
      currentLegacySource === routeState.legacySource
        ? currentLegacySource
        : routeState.legacySource,
    );
    setRunPrompt((currentPrompt) =>
      currentPrompt === routeState.prompt ? currentPrompt : routeState.prompt,
    );
  }, [
    locationSnapshot,
    routeState.draftMode,
    routeState.executionId,
    routeState.legacySource,
    routeState.prompt,
    routeState.scriptId,
    routeState.templateWorkflow,
    routeState.workflowId,
    routeStudioView,
    routeWorkspacePage,
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
  const workspaceSettingsQuery = useQuery({
    queryKey: ['studio-workspace-settings'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getWorkspaceSettings(),
  });
  const workflowsQuery = useQuery({
    queryKey: ['studio-workspace-workflows'],
    enabled: studioHostReady,
    queryFn: () => studioApi.listWorkflows(),
  });
  const executionsQuery = useQuery({
    queryKey: ['studio-executions'],
    enabled: studioHostReady,
    queryFn: () => studioApi.listExecutions(),
  });
  const connectorsQuery = useQuery({
    queryKey: ['studio-connectors'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getConnectorCatalog(),
  });
  const connectorDraftQuery = useQuery({
    queryKey: ['studio-connectors-draft'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getConnectorDraft(),
  });
  const rolesQuery = useQuery({
    queryKey: ['studio-roles'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getRoleCatalog(),
  });
  const roleDraftQuery = useQuery({
    queryKey: ['studio-roles-draft'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getRoleDraft(),
  });
  const settingsQuery = useQuery({
    queryKey: ['studio-settings'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getSettings(),
  });
  const selectedWorkflowQuery = useQuery({
    queryKey: ['studio-workflow', selectedWorkflowId],
    enabled: studioHostReady && Boolean(selectedWorkflowId),
    queryFn: () => studioApi.getWorkflow(selectedWorkflowId),
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
    () =>
      dedupeStudioWorkflowSummaries(
        workflowsQuery.data ?? [],
        selectedWorkflowId,
      ),
    [selectedWorkflowId, workflowsQuery.data],
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
  const deferredDraftYaml = useDeferredValue(draftYaml);
  const defaultDirectoryId = useMemo(
    () => readDefaultDirectoryId(workspaceSettingsQuery.data?.directories),
    [workspaceSettingsQuery.data?.directories],
  );
  const activeWorkflowSourceKey = selectedWorkflowId
    ? `workspace:${selectedWorkflowId}`
    : templateWorkflow
      ? `template:${templateWorkflow}`
      : draftMode === 'new' && legacySource === 'playground'
        ? 'legacy:playground'
      : draftMode === 'new'
        ? 'draft:new'
      : '';
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
    if (draftMode === 'new' && legacySource === 'playground') {
      return legacyPlaygroundDraft.yaml.trim()
        ? legacyPlaygroundDraft.yaml
        : buildBlankDraftYaml(legacyPlaygroundDraft.sourceWorkflow || 'draft');
    }
    if (draftMode === 'new') {
      return buildBlankDraftYaml('draft');
    }

    return '';
  }, [
    activeTemplate?.yaml,
    activeWorkflowFile?.yaml,
    draftMode,
    legacyPlaygroundDraft.sourceWorkflow,
    legacyPlaygroundDraft.yaml,
    legacySource,
  ]);
  const sourceWorkflowName =
    activeWorkflowFile?.name ||
    activeTemplate?.catalog.name ||
    (draftMode === 'new' && legacySource === 'playground'
      ? legacyPlaygroundDraft.sourceWorkflow || 'draft'
      : draftMode === 'new'
        ? 'draft'
        : '');
  const sourceFileName = activeWorkflowFile?.fileName || '';
  const sourceDirectoryId = activeWorkflowFile?.directoryId || defaultDirectoryId;
  const sourceWorkflowLayout = activeWorkflowFile?.layout ?? null;

  const parseYamlQuery = useQuery({
    queryKey: [
      'studio-parse-yaml',
      deferredDraftYaml,
      workflowNames.join('|'),
      availableStepTypes.join('|'),
    ],
    enabled: studioHostReady && Boolean(deferredDraftYaml.trim()),
    retry: false,
    queryFn: () =>
      studioApi.parseYaml({
        yaml: deferredDraftYaml,
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      }),
  });

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
      setLegacySource('');
      setSaveNotice(null);
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setLegacySource('');
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
    setLegacySource('');
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
    if (!connectorsQuery.data) {
      return;
    }

    const currentSelectedName =
      connectorCatalogDraft.find((connector) => connector.key === selectedConnectorKey)
        ?.name || '';
    const nextConnectors = connectorsQuery.data.connectors.map((connector) =>
      toConnectorCatalogItem(connector),
    );
    setConnectorCatalogDraft(nextConnectors);
    setSelectedConnectorKey(
      nextConnectors.find(
        (connector) => connector.name === currentSelectedName,
      )?.key ||
        nextConnectors[0]?.key ||
        '',
    );
  }, [connectorsQuery.data]);

  useEffect(() => {
    if (!rolesQuery.data) {
      return;
    }

    const currentSelectedRoleId =
      roleCatalogDraft.find((role) => role.key === selectedRoleKey)?.id || '';
    const nextRoles = rolesQuery.data.roles.map((role) => createRoleCatalogItem(role));
    setRoleCatalogDraft(nextRoles);
    setSelectedRoleKey(
      nextRoles.find((role) => role.id === currentSelectedRoleId)?.key ||
        nextRoles[0]?.key ||
        '',
    );
  }, [rolesQuery.data]);

  useEffect(() => {
    if (!settingsQuery.data) {
      return;
    }

    const nextDraft = createSettingsDraft(settingsQuery.data);
    setSettingsDraft(nextDraft);
    setSelectedProviderName(
      nextDraft?.providers.find(
        (provider) => provider.providerName === selectedProviderName,
      )?.providerName ||
        nextDraft?.providers[0]?.providerName ||
        '',
    );
  }, [selectedProviderName, settingsQuery.data]);

  useEffect(() => {
    if (!activeWorkflowSourceKey) {
      setDraftSourceKey('');
      setDraftYaml('');
      setDraftWorkflowName('');
      setDraftFileName('');
      setDraftDirectoryId(defaultDirectoryId);
      setDraftWorkflowLayout(null);
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
      workspacePage === 'studio'
        ? studioView === 'execution'
          ? 'executions'
          : 'studio'
        : workspacePage === 'execution'
          ? 'executions'
          : workspacePage;

    window.history.replaceState(null, '', buildStudioRoute({
      scopeId: resolvedStudioScopeId || undefined,
      scopeLabel: routeState.scopeLabel || undefined,
      memberId: routeState.memberId || undefined,
      memberLabel: routeState.memberLabel || undefined,
      workflowId: selectedWorkflowId || undefined,
      scriptId: selectedScriptId || undefined,
      template: !selectedWorkflowId ? templateWorkflow || undefined : undefined,
      tab,
      draftMode:
        !selectedWorkflowId &&
        !templateWorkflow &&
        draftMode === 'new'
          ? 'new'
          : undefined,
      prompt: runPrompt || undefined,
      legacySource:
        !selectedWorkflowId &&
        !templateWorkflow &&
        draftMode === 'new' &&
        legacySource === 'playground'
          ? 'playground'
          : undefined,
      executionId: selectedExecutionId || undefined,
      logsMode: logsPopoutMode === 'popout' ? 'popout' : undefined,
    }));
  }, [
    appliedRouteSnapshot,
    draftMode,
    isStudioLocation,
    legacySource,
    locationSnapshot,
    logsPopoutMode,
    resolvedStudioScopeId,
    runPrompt,
    routeState.memberId,
    routeState.memberLabel,
    routeState.scopeLabel,
    selectedExecutionId,
    selectedScriptId,
    selectedWorkflowId,
    studioView,
    templateWorkflow,
    workspacePage,
  ]);

  const activeWorkflowName = draftWorkflowName || sourceWorkflowName;
  const activeDirectoryLabel =
    workspaceSettingsQuery.data?.directories.find(
      (item) => item.directoryId === draftDirectoryId,
    )?.label ||
    activeWorkflowFile?.directoryLabel ||
    'No directory';
  const activeWorkflowDescription =
    parseYamlQuery.data?.document?.description ||
    activeWorkflowFile?.document?.description ||
    activeTemplate?.catalog.description ||
    '';
  const parsedWorkflowDocument = parseYamlQuery.data?.document ?? null;
  const templateWorkflowDocument = useMemo(
    () => buildTemplateWorkflowDocument(activeTemplate?.definition),
    [activeTemplate?.definition],
  );
  const useTemplateWorkflowFallback =
    Boolean(templateWorkflow) &&
    trimOptional(draftYaml) === trimOptional(sourceYaml) &&
    !hasWorkflowGraphContent(parsedWorkflowDocument) &&
    hasWorkflowGraphContent(templateWorkflowDocument);
  const activeWorkflowDocument = useMemo(() => {
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
    parsedWorkflowDocument,
    templateWorkflowDocument,
    useTemplateWorkflowFallback,
  ]);
  const activeWorkflowFindings = parseYamlQuery.data?.findings ?? [];
  const workflowGraph = useMemo(
    () => buildStudioGraphElements(activeWorkflowDocument, draftWorkflowLayout),
    [activeWorkflowDocument, draftWorkflowLayout],
  );

  useEffect(() => {
    setSelectedGraphNodeId('');
    setSelectedGraphEdgeId('');
  }, [activeWorkflowSourceKey]);

  const isDraftDirty =
    Boolean(activeWorkflowSourceKey) &&
    (draftYaml !== sourceYaml ||
      draftWorkflowName !== sourceWorkflowName ||
      draftFileName !== sourceFileName ||
      draftDirectoryId !== sourceDirectoryId);
  const canSaveWorkflow =
    Boolean(draftYaml.trim()) &&
    Boolean(draftWorkflowName.trim()) &&
    Boolean(draftDirectoryId) &&
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
  const canPublishWorkflow =
    Boolean(draftYaml.trim()) &&
    Boolean(activeWorkflowName.trim()) &&
    Boolean(resolvedStudioScopeId) &&
    !publishPending &&
    !parseYamlQuery.isLoading &&
    !hasValidationError(activeWorkflowFindings);
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

        const workflowFile = await studioApi.getWorkflow(workflowId);
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
    visibleWorkflowSummaries,
  ]);
  const recentPromptHistory = useMemo(
    () => promptHistory.slice(0, 3),
    [promptHistory],
  );
  const selectedGraphRole = useMemo<StudioGraphRole | null>(() => {
    if (!selectedGraphNodeId.startsWith('role:')) {
      return null;
    }

    const roleId = selectedGraphNodeId.slice('role:'.length);
    return workflowGraph.roles.find((role) => role.id === roleId) ?? null;
  }, [selectedGraphNodeId, workflowGraph.roles]);
  const selectedGraphStep = useMemo<StudioGraphStep | null>(() => {
    if (!selectedGraphNodeId.startsWith('step:')) {
      return null;
    }

    const stepId = selectedGraphNodeId.slice('step:'.length);
    return workflowGraph.steps.find((step) => step.id === stepId) ?? null;
  }, [selectedGraphNodeId, workflowGraph.steps]);
  const selectedGraphEdge = useMemo<StudioSelectedGraphEdge | null>(() => {
    if (!selectedGraphEdgeId) {
      return null;
    }

    const edge = workflowGraph.edges.find((item) => item.id === selectedGraphEdgeId);
    if (!edge) {
      return null;
    }

    const sourceStepId = edge.source.startsWith('step:')
      ? edge.source.slice('step:'.length)
      : edge.source;
    const targetStepId = edge.target.startsWith('step:')
      ? edge.target.slice('step:'.length)
      : edge.target;

    return {
      edgeId: edge.id,
      sourceStepId,
      targetStepId,
      branchLabel: edge.data?.branchLabel ?? null,
      kind: edge.data?.kind ?? 'next',
      implicit: Boolean(edge.data?.implicit),
    };
  }, [selectedGraphEdgeId, workflowGraph.edges]);
  useEffect(() => {
    if (selectedGraphEdgeId && !selectedGraphEdge) {
      setSelectedGraphEdgeId('');
    }
  }, [selectedGraphEdge, selectedGraphEdgeId]);
  const workflowRoleIds = useMemo(
    () => workflowGraph.roles.map((role) => role.id),
    [workflowGraph.roles],
  );
  const workflowStepIds = useMemo(
    () => workflowGraph.steps.map((step) => step.id),
    [workflowGraph.steps],
  );
  const selectedConnector = useMemo(
    () =>
      connectorCatalogDraft.find(
        (connector) => connector.key === selectedConnectorKey,
      ) ?? null,
    [connectorCatalogDraft, selectedConnectorKey],
  );
  const selectedRole = useMemo(
    () => roleCatalogDraft.find((role) => role.key === selectedRoleKey) ?? null,
    [roleCatalogDraft, selectedRoleKey],
  );
  const selectedProvider = useMemo(
    () =>
      settingsDraft?.providers.find(
        (provider) => provider.providerName === selectedProviderName,
      ) ?? null,
    [selectedProviderName, settingsDraft?.providers],
  );
  const settingsProviders = useMemo(
    () =>
      (settingsDraft?.providers ?? []).map((provider) => ({
        providerName: provider.providerName,
        model: provider.model,
      })),
    [settingsDraft?.providers],
  );
  const connectorCatalogMeta = useMemo(
    () => ({
      filePath: connectorsQuery.data?.filePath || '',
      fileExists: Boolean(connectorsQuery.data?.fileExists),
    }),
    [connectorsQuery.data?.fileExists, connectorsQuery.data?.filePath],
  );
  const connectorDraftMeta = useMemo<StudioCatalogDraftMeta>(
    () => ({
      filePath: connectorDraftQuery.data?.filePath || '',
      fileExists: Boolean(connectorDraftQuery.data?.fileExists),
      updatedAtUtc: connectorDraftQuery.data?.updatedAtUtc || null,
    }),
    [
      connectorDraftQuery.data?.fileExists,
      connectorDraftQuery.data?.filePath,
      connectorDraftQuery.data?.updatedAtUtc,
    ],
  );
  const connectorCatalogIsRemote = connectorCatalogMeta.filePath.startsWith(
    'chrono-storage://',
  );
  const roleCatalogMeta = useMemo(
    () => ({
      filePath: rolesQuery.data?.filePath || '',
      fileExists: Boolean(rolesQuery.data?.fileExists),
    }),
    [rolesQuery.data?.fileExists, rolesQuery.data?.filePath],
  );
  const roleDraftMeta = useMemo<StudioCatalogDraftMeta>(
    () => ({
      filePath: roleDraftQuery.data?.filePath || '',
      fileExists: Boolean(roleDraftQuery.data?.fileExists),
      updatedAtUtc: roleDraftQuery.data?.updatedAtUtc || null,
    }),
    [
      roleDraftQuery.data?.fileExists,
      roleDraftQuery.data?.filePath,
      roleDraftQuery.data?.updatedAtUtc,
    ],
  );
  const roleCatalogIsRemote = roleCatalogMeta.filePath.startsWith(
    'chrono-storage://',
  );
  const connectorCatalogDirty = useMemo(
    () =>
      JSON.stringify(connectorCatalogDraft.map((connector) => toConnectorDefinition(connector))) !==
      JSON.stringify(
        (connectorsQuery.data?.connectors ?? []).map((connector) =>
          toConnectorDefinition(toConnectorCatalogItem(connector)),
        ),
      ),
    [connectorCatalogDraft, connectorsQuery.data?.connectors],
  );
  const roleCatalogDirty = useMemo(
    () =>
      JSON.stringify(roleCatalogDraft.map((role) => toRoleDefinition(role))) !==
      JSON.stringify(
        (rolesQuery.data?.roles ?? []).map((role) => toRoleDefinition(createRoleCatalogItem(role))),
      ),
    [roleCatalogDraft, rolesQuery.data?.roles],
  );
  const studioHostMode = appContextQuery.data?.mode ?? 'embedded';
  const settingsDirty = useMemo(
    () =>
      JSON.stringify(
        normalizeSettingsDraftForHostMode(settingsDraft, studioHostMode),
      ) !==
      JSON.stringify(
        normalizeSettingsDraftForHostMode(
          createSettingsDraft(settingsQuery.data),
          studioHostMode,
        ),
      ),
    [settingsDraft, settingsQuery.data, studioHostMode],
  );
  const executionCanStop = isExecutionStopAllowed(selectedExecutionQuery.data?.status);

  useEffect(() => {
    if (!roleCatalogNotice) {
      return undefined;
    }

    const timeoutId = window.setTimeout(() => {
      setRoleCatalogNotice(null);
    }, 3200);
    return () => window.clearTimeout(timeoutId);
  }, [roleCatalogNotice]);

  useEffect(() => {
    if (!connectorCatalogNotice) {
      return undefined;
    }

    const timeoutId = window.setTimeout(() => {
      setConnectorCatalogNotice(null);
    }, 3200);
    return () => window.clearTimeout(timeoutId);
  }, [connectorCatalogNotice]);

  useEffect(() => {
    if (selectedGraphStep) {
      setNodeInspectorDraft(createStepInspectorDraft(selectedGraphStep));
      setInspectorNotice(null);
      return;
    }

    if (selectedGraphRole) {
      setNodeInspectorDraft(createRoleInspectorDraft(selectedGraphRole));
      setInspectorNotice(null);
      return;
    }

    setNodeInspectorDraft(null);
    setInspectorNotice(null);
  }, [selectedGraphRole, selectedGraphStep]);

  const openWorkspaceWorkflow = (workflowId: string) => {
    setSelectedWorkflowId(workflowId);
    setTemplateWorkflow('');
    setDraftMode('');
    setLegacySource('');
    setWorkspacePage('studio');
    setStudioView('editor');
  };

  const openExecution = (executionId: string) => {
    setSelectedExecutionId(executionId);
    setWorkspacePage('studio');
    setStudioView('execution');
  };

  const startBlankDraft = () => {
    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setLegacySource('');
    setDraftWorkflowLayout(null);
    setDraftDirectoryId((current) => current || defaultDirectoryId);
    setWorkspacePage('studio');
    setStudioView('editor');
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
    setLegacySource('');
    setWorkspacePage('studio');
    setStudioView('editor');
  };

  const resetDraftFromSource = () => {
    setDraftSourceKey(activeWorkflowSourceKey);
    setDraftYaml(sourceYaml);
    setDraftWorkflowName(sourceWorkflowName);
    setDraftFileName(sourceFileName);
    setDraftDirectoryId(sourceDirectoryId);
    setDraftWorkflowLayout(sourceWorkflowLayout);
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
      setLegacySource('');
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setLegacySource('');
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

  const handleSwitchStudioView = useCallback(
    (view: StudioViewMode) => {
      if (view === 'editor') {
        ensureActiveWorkflowDraftLoaded();
      }
      setStudioView(view);
    },
    [ensureActiveWorkflowDraftLoaded],
  );

  const handleSaveDraft = async () => {
    const directoryId = draftDirectoryId || defaultDirectoryId;
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
        ['studio-workflow', savedWorkflow.workflowId],
        savedWorkflow,
      );
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows'],
      });

      setSelectedWorkflowId(savedWorkflow.workflowId);
      setTemplateWorkflow('');
      setDraftMode('');
      setLegacySource('');
      setDraftSourceKey(`workspace:${savedWorkflow.workflowId}`);
      setDraftYaml(savedWorkflow.yaml);
      setDraftWorkflowName(savedWorkflow.name);
      setDraftFileName(savedWorkflow.fileName);
      setDraftDirectoryId(savedWorkflow.directoryId);
      setDraftWorkflowLayout(
        savedWorkflow.layout ||
          draftWorkflowLayout ||
          buildStudioWorkflowLayout(savedWorkflow.name, workflowGraph.nodes),
      );
      setSaveNotice({
        type: 'success',
        message: `Saved ${savedWorkflow.name} to ${savedWorkflow.directoryLabel}.`,
      });
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
    if (workspacePage !== 'studio' || studioView !== 'editor') {
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
    savePending,
    studioView,
    workspacePage,
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

  const handlePublishWorkflow = async () => {
    const workflowName = activeWorkflowName.trim();
    const scopeId = resolvedStudioScopeId;
    if (!workflowName) {
      setPublishNotice({
        type: 'error',
        message: 'Workflow name is required before binding the current scope.',
      });
      return;
    }

    if (!draftYaml.trim()) {
      setPublishNotice({
        type: 'error',
        message: 'Workflow YAML is required before binding the current scope.',
      });
      return;
    }

    if (hasValidationError(activeWorkflowFindings)) {
      setPublishNotice({
        type: 'error',
        message: 'Resolve Studio YAML validation errors before binding the current scope.',
      });
      return;
    }

    if (!scopeId) {
      setPublishNotice({
        type: 'error',
        message: 'Resolve the current scope before binding the current workflow.',
      });
      return;
    }

    setPublishPending(true);
    setPublishNotice(null);

    try {
      const workflowYamls = await buildWorkflowYamlBundle();
      const result = await studioApi.bindScopeWorkflow({
        scopeId,
        displayName: workflowName,
        workflowYamls,
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', scopeId],
      });
      setPublishNotice({
        type: 'success',
        message: `Updated scope ${result.scopeId} to serve ${describeScopeBindingTarget(result)} on revision ${result.revisionId}.`,
      });
    } catch (error) {
      setPublishNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to bind the current workflow to the scope.',
      });
    } finally {
      setPublishPending(false);
    }
  };

  const handleBindGAgent = async (input: {
    displayName?: string;
    actorTypeName: string;
    endpoints?: Array<{
      endpointId: string;
      displayName?: string;
      kind?: 'command' | 'chat';
      requestTypeUrl?: string;
      responseTypeUrl?: string;
      description?: string;
    }>;
    openRunsEndpointId?: string;
    endpointId?: string;
    endpointDisplayName?: string;
    requestTypeUrl?: string;
    responseTypeUrl?: string;
    description?: string;
    prompt?: string;
    payloadBase64?: string;
  }, options?: {
    openRuns?: boolean;
  }) => {
    const scopeId = resolvedStudioScopeId;
    const actorTypeName = input.actorTypeName.trim();
    const normalizedEndpoints: StudioScopeGAgentEndpointInput[] = (
      input.endpoints?.length
        ? input.endpoints
        : input.endpointId?.trim()
        ? [
            {
              endpointId: input.endpointId,
              displayName: input.endpointDisplayName,
              kind: 'command',
              requestTypeUrl: input.requestTypeUrl,
              responseTypeUrl: input.responseTypeUrl,
              description: input.description,
            },
          ]
        : []
    )
      .map((endpoint) => {
        const endpointId = trimOptional(endpoint.endpointId);
        const kind: StudioScopeGAgentEndpointInput['kind'] =
          endpoint.kind === 'chat' ? 'chat' : 'command';
        const requestTypeUrl = trimOptional(endpoint.requestTypeUrl);
        const responseTypeUrl = trimOptional(endpoint.responseTypeUrl);
        const description = trimOptional(endpoint.description);
        return {
          endpointId,
          displayName:
            trimOptional(endpoint.displayName) || endpointId || undefined,
          kind,
          requestTypeUrl:
            requestTypeUrl || (kind === 'command' ? getStringValueTypeUrl() : undefined),
          responseTypeUrl: responseTypeUrl || undefined,
          description: description || undefined,
        };
      })
      .filter((endpoint) => endpoint.endpointId.length > 0);
    const launchEndpoint =
      normalizedEndpoints.find(
        (endpoint) =>
          endpoint.endpointId === trimOptional(input.openRunsEndpointId),
      ) ||
      normalizedEndpoints[0] ||
      null;
    const launchPayloadTypeUrl =
      trimOptional(launchEndpoint?.requestTypeUrl) || getStringValueTypeUrl();

    if (!scopeId) {
      setPublishNotice({
        type: 'error',
        message: 'Resolve the current scope before binding a GAgent service.',
      });
      return;
    }

    if (!actorTypeName) {
      setPublishNotice({
        type: 'error',
        message: 'Actor type name is required before binding a GAgent service.',
      });
      return;
    }

    if (normalizedEndpoints.length === 0) {
      setPublishNotice({
        type: 'error',
        message: 'At least one endpoint is required before binding a GAgent service.',
      });
      return;
    }

    if (
      options?.openRuns &&
      launchEndpoint?.kind !== 'chat' &&
      !isAutoEncodableTextPayloadTypeUrl(launchPayloadTypeUrl) &&
      !input.payloadBase64?.trim()
    ) {
      setPublishNotice({
        type: 'error',
        message: 'Custom request payload types require payload base64 before opening Runs.',
      });
      return;
    }

    setPublishPending(true);
    setPublishNotice(null);

    try {
      const result = await studioApi.bindScopeGAgent({
        scopeId,
        displayName:
          input.displayName?.trim() ||
          launchEndpoint?.displayName?.trim() ||
          launchEndpoint?.endpointId ||
          actorTypeName,
        actorTypeName,
        endpoints: normalizedEndpoints,
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', scopeId],
      });

      if (options?.openRuns) {
        const launchEndpointKind =
          launchEndpoint?.kind === 'chat' ? 'chat' : 'command';
        if (launchEndpoint?.kind === 'chat') {
          history.push(
            buildRuntimeRunsHref({
              scopeId,
              endpointId: launchEndpoint.endpointId,
              endpointKind: launchEndpointKind,
              prompt: input.prompt?.trim() || undefined,
              returnTo: currentStudioReturnTo || undefined,
            }),
          );
        } else if (launchEndpoint) {
          const draftKey = saveEndpointInvocationDraftPayload({
            endpointId: launchEndpoint.endpointId,
            endpointKind: launchEndpointKind,
            prompt: input.prompt?.trim() || '',
            payloadTypeUrl: launchPayloadTypeUrl,
            payloadBase64: input.payloadBase64?.trim() || undefined,
          });
          if (!draftKey) {
            throw new Error('Failed to prepare the GAgent run draft.');
          }

          history.push(
            buildRuntimeRunsHref({
              scopeId,
              endpointId: launchEndpoint.endpointId,
              endpointKind: launchEndpointKind,
              prompt: input.prompt?.trim() || undefined,
              draftKey,
              returnTo: currentStudioReturnTo || undefined,
            }),
          );
        }
      }

      setPublishNotice({
        type: 'success',
        message: options?.openRuns
          ? `Updated scope ${result.scopeId} to serve ${describeScopeBindingTarget(result)} and opened Runs for endpoint ${launchEndpoint?.endpointId || normalizedEndpoints[0]?.endpointId || 'run'}.`
          : `Updated scope ${result.scopeId} to serve ${describeScopeBindingTarget(result)} on revision ${result.revisionId}.`,
      });
    } catch (error) {
      setPublishNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to bind the current scope to the GAgent service.',
      });
    } finally {
      setPublishPending(false);
    }
  };

  const handleActivateBindingRevision = async (revisionId: string) => {
    const scopeId = resolvedStudioScopeId;
    const normalizedRevisionId = revisionId.trim();
    if (!scopeId || !normalizedRevisionId) {
      setPublishNotice({
        type: 'error',
        message: 'Resolve the current scope and revision before activating a binding.',
      });
      return;
    }

    setBindingActivationRevisionId(normalizedRevisionId);
    setPublishNotice(null);

    try {
      const result = await studioApi.activateScopeBindingRevision({
        scopeId,
        revisionId: normalizedRevisionId,
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', scopeId],
      });
      setPublishNotice({
        type: 'success',
        message: `Scope ${result.scopeId} is now serving revision ${result.revisionId}.`,
      });
    } catch (error) {
      setPublishNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to activate the selected binding revision.',
      });
    } finally {
      setBindingActivationRevisionId('');
    }
  };

  const handleRetireBindingRevision = async (revisionId: string) => {
    const scopeId = resolvedStudioScopeId;
    const normalizedRevisionId = revisionId.trim();
    if (!scopeId || !normalizedRevisionId) {
      setPublishNotice({
        type: 'error',
        message: 'Resolve the current scope and revision before retiring a binding.',
      });
      return;
    }

    setBindingRetirementRevisionId(normalizedRevisionId);
    setPublishNotice(null);

    try {
      const result = await studioApi.retireScopeBindingRevision({
        scopeId,
        revisionId: normalizedRevisionId,
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', scopeId],
      });
      setPublishNotice({
        type: 'success',
        message: `Scope ${result.scopeId} accepted revision ${result.revisionId} for retirement.`,
      });
    } catch (error) {
      setPublishNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to retire the selected binding revision.',
      });
    } finally {
      setBindingRetirementRevisionId('');
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

  const applyImportedDraft = async (
    yaml: string,
    options?: {
      workflowName?: string;
      notice?: StudioNotice;
    },
  ) => {
    const parsed = await studioApi.parseYaml({
      yaml,
      availableWorkflowNames: workflowNames,
      availableStepTypes,
    });

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftMode('new');
    setLegacySource('');
    setDraftSourceKey('draft:new');
    setDraftYaml(yaml);
    setDraftWorkflowName(
      options?.workflowName ||
        trimOptional(parsed.document?.name) ||
        draftWorkflowName ||
        'draft',
    );
    setDraftFileName('');
    setDraftDirectoryId(defaultDirectoryId);
    setDraftWorkflowLayout(null);
    setWorkspacePage('studio');
    setStudioView('editor');
    setSaveNotice(null);
    setRunNotice(null);
    if (options?.notice) {
      setWorkflowImportNotice(options.notice);
    }
  };

  const handleWorkflowImport = async (
    event: React.ChangeEvent<HTMLInputElement>,
  ) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) {
      return;
    }

    setWorkflowImportPending(true);
    setWorkflowImportNotice(null);
    try {
      const yaml = await file.text();
      await applyImportedDraft(yaml, {
        workflowName: file.name.replace(/\.(ya?ml)$/i, ''),
        notice: {
          type: 'success',
          message: `Imported ${file.name} into a new Studio draft.`,
        },
      });
    } catch (error) {
      setWorkflowImportNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to import the workflow YAML file.',
      });
    } finally {
      setWorkflowImportPending(false);
    }
  };

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

  const handleAskAiGenerate = async () => {
    if (!askAiPrompt.trim()) {
      setAskAiNotice({
        type: 'error',
        message: 'Describe the workflow you want Studio to generate.',
      });
      return;
    }

    setAskAiPending(true);
    setAskAiNotice(null);
    setAskAiAnswer('');
    setAskAiReasoning('');

    try {
      const generatedYaml = await studioApi.authorWorkflow(
        {
          prompt: askAiPrompt.trim(),
          currentYaml: draftYaml.trim() ? draftYaml : undefined,
          availableWorkflowNames: workflowNames,
          metadata: {
            source: 'aevatar-console-web',
            surface: 'studio',
          },
        },
        {
          onText: (text) => setAskAiAnswer(text),
          onReasoning: (text) => setAskAiReasoning(text),
        },
      );

      const normalizedYaml = generatedYaml.trim();
      if (!normalizedYaml) {
        throw new Error('Studio AI did not return workflow YAML.');
      }

      setAskAiAnswer(normalizedYaml);
      await applyImportedDraft(normalizedYaml, {
        notice: {
          type: 'success',
          message: 'Applied AI-generated workflow YAML to the current Studio draft.',
        },
      });
      setAskAiNotice({
        type: 'success',
        message: 'Applied AI-generated workflow YAML to the current Studio draft.',
      });
    } catch (error) {
      setAskAiNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to generate workflow YAML in Studio.',
      });
    } finally {
      setAskAiPending(false);
    }
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

  const updateConnectorCatalogDraft = (
    connectorKey: string,
    updater: (
      connector: StudioConnectorCatalogItem,
    ) => StudioConnectorCatalogItem,
  ) => {
    setConnectorCatalogDraft((current) =>
      current.map((connector) =>
        connector.key === connectorKey ? updater(connector) : connector,
      ),
    );
  };

  const updateRoleCatalogDraft = (
    roleKey: string,
    updater: (role: StudioRoleCatalogItem) => StudioRoleCatalogItem,
  ) => {
    setRoleCatalogDraft((current) =>
      current.map((role) => (role.key === roleKey ? updater(role) : role)),
    );
  };

  const resetConnectorDraftQuery = () => {
    queryClient.setQueryData(['studio-connectors-draft'], {
      homeDirectory: connectorDraftQuery.data?.homeDirectory || '',
      filePath: '',
      fileExists: false,
      updatedAtUtc: null,
      draft: null,
    });
  };

  const resetRoleDraftQuery = () => {
    queryClient.setQueryData(['studio-roles-draft'], {
      homeDirectory: roleDraftQuery.data?.homeDirectory || '',
      filePath: '',
      fileExists: false,
      updatedAtUtc: null,
      draft: null,
    });
  };

  const persistConnectorDraft = async (
    nextDraft: StudioConnectorDraftItem | null,
  ) => {
    if (!hasConnectorDraftContent(nextDraft)) {
      await studioApi.deleteConnectorDraft();
      resetConnectorDraftQuery();
      return;
    }

    const draft = nextDraft as StudioConnectorDraftItem;
    const response = await studioApi.saveConnectorDraft({
      draft: toConnectorDefinition(draft),
    });
    queryClient.setQueryData(['studio-connectors-draft'], response);
  };

  const persistRoleDraft = async (nextDraft: StudioRoleDraftItem | null) => {
    if (!hasRoleDraftContent(nextDraft)) {
      await studioApi.deleteRoleDraft();
      resetRoleDraftQuery();
      return;
    }

    const draft = nextDraft as StudioRoleDraftItem;
    const response = await studioApi.saveRoleDraft({
      draft: toRoleDefinition(draft),
    });
    queryClient.setQueryData(['studio-roles-draft'], response);
  };

  const handleOpenConnectorModal = () => {
    setConnectorDraft(toConnectorDraftItem(connectorDraftQuery.data?.draft));
    setConnectorModalOpen(true);
  };

  const handleCloseConnectorModal = async () => {
    const draft = connectorDraft;
    setConnectorModalOpen(false);

    try {
      await persistConnectorDraft(draft);
      if (hasConnectorDraftContent(draft)) {
        setConnectorCatalogNotice({
          type: 'info',
          message: 'Connector draft saved.',
        });
      }
    } catch (error) {
      setConnectorCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save the connector draft.',
      });
    }
  };

  const handleSubmitConnectorDraft = async () => {
    if (!connectorDraft) {
      return;
    }

    const type = connectorDraft.type || 'http';
    const connectorName =
      connectorDraft.name.trim() ||
      createUniqueConnectorName(connectorCatalogDraft, type);
    const nextConnector: StudioConnectorCatalogItem = {
      key: createStudioLocalKey('connector'),
      ...connectorDraft,
      name: connectorName,
      type,
    };

    setConnectorCatalogDraft((current) => [nextConnector, ...current]);
    setSelectedConnectorKey(nextConnector.key);
    setConnectorDraft(null);
    setConnectorModalOpen(false);

    try {
      await studioApi.deleteConnectorDraft();
      resetConnectorDraftQuery();
    } catch (error) {
      setConnectorCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to clear the connector draft.',
      });
      return;
    }

    setConnectorCatalogNotice({
      type: 'success',
      message: `Connector ${connectorName} added.`,
    });
  };

  const handleDeleteConnector = (connectorKey: string) => {
    setConnectorCatalogDraft((current) => {
      const next = current.filter((connector) => connector.key !== connectorKey);
      setSelectedConnectorKey(next[0]?.key || '');
      return next;
    });
    setConnectorCatalogNotice(null);
  };

  const handleSaveConnectors = async () => {
    setConnectorCatalogPending(true);
    setConnectorCatalogNotice(null);
    try {
      const currentConnectorName = selectedConnector?.name || '';
      const response = await studioApi.saveConnectorCatalog({
        connectors: connectorCatalogDraft.map((connector) =>
          toConnectorDefinition(connector),
        ),
      });
      const nextConnectors = response.connectors.map((connector) =>
        toConnectorCatalogItem(connector),
      );
      queryClient.setQueryData(['studio-connectors'], response);
      setConnectorCatalogDraft(nextConnectors);
      setSelectedConnectorKey(
        nextConnectors.find((connector) => connector.name === currentConnectorName)
          ?.key ||
          nextConnectors[0]?.key ||
          '',
      );
      setConnectorCatalogNotice({
        type: 'success',
        message: 'Saved the Studio connector catalog.',
      });
    } catch (error) {
      setConnectorCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save the connector catalog.',
      });
    } finally {
      setConnectorCatalogPending(false);
    }
  };

  const handleConnectorImport = async (
    event: React.ChangeEvent<HTMLInputElement>,
  ) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) {
      return;
    }

    setConnectorImportPending(true);
    setConnectorCatalogNotice(null);
    try {
      const response = await studioApi.importConnectorCatalog(file);
      const nextConnectors = response.connectors.map((connector) =>
        toConnectorCatalogItem(connector),
      );
      queryClient.setQueryData(['studio-connectors'], response);
      setConnectorCatalogDraft(nextConnectors);
      setSelectedConnectorKey(nextConnectors[0]?.key || '');
      setConnectorCatalogNotice({
        type: 'success',
        message: `Imported ${response.importedCount} connector(s) from ${response.sourceFilePath || file.name}.`,
      });
    } catch (error) {
      setConnectorCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to import the connector catalog.',
      });
    } finally {
      setConnectorImportPending(false);
    }
  };

  const handleOpenRoleModal = () => {
    setRoleDraft(toRoleDraftItem(roleDraftQuery.data?.draft));
    setRoleModalOpen(true);
  };

  const handleCloseRoleModal = async () => {
    const draft = roleDraft;
    setRoleModalOpen(false);

    try {
      await persistRoleDraft(draft);
      if (hasRoleDraftContent(draft)) {
        setRoleCatalogNotice({
          type: 'info',
          message: 'Role draft saved.',
        });
      }
    } catch (error) {
      setRoleCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save the role draft.',
      });
    }
  };

  const handleSubmitRoleDraft = async () => {
    if (!roleDraft) {
      return;
    }

    const roleId =
      roleDraft.id.trim() ||
      createUniqueRoleId(roleCatalogDraft, roleDraft.name || 'role');
    const roleName = roleDraft.name.trim() || roleId;
    const nextRole: StudioRoleCatalogItem = {
      key: createStudioLocalKey('role'),
      id: roleId,
      name: roleName,
      systemPrompt: roleDraft.systemPrompt,
      provider: roleDraft.provider,
      model: roleDraft.model,
      connectorsText: roleDraft.connectorsText,
    };

    setRoleCatalogDraft((current) => [nextRole, ...current]);
    setSelectedRoleKey(nextRole.key);
    setRoleDraft(null);
    setRoleModalOpen(false);

    try {
      await studioApi.deleteRoleDraft();
      resetRoleDraftQuery();
    } catch (error) {
      setRoleCatalogNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to clear the role draft.',
      });
      return;
    }

    setRoleCatalogNotice({
      type: 'success',
      message: `Role ${roleId} added.`,
    });
  };

  const handleDeleteRole = (roleKey: string) => {
    setRoleCatalogDraft((current) => {
      const next = current.filter((role) => role.key !== roleKey);
      setSelectedRoleKey(next[0]?.key || '');
      return next;
    });
    setRoleCatalogNotice(null);
  };

  const handleSaveRoles = async () => {
    setRoleCatalogPending(true);
    setRoleCatalogNotice(null);
    try {
      const currentRoleId = selectedRole?.id || '';
      const response = await studioApi.saveRoleCatalog({
        roles: roleCatalogDraft.map((role) => toRoleDefinition(role)),
      });
      const nextRoles = response.roles.map((role) => createRoleCatalogItem(role));
      queryClient.setQueryData(['studio-roles'], response);
      setRoleCatalogDraft(nextRoles);
      setSelectedRoleKey(
        nextRoles.find((role) => role.id === currentRoleId)?.key ||
          nextRoles[0]?.key ||
          '',
      );
      setRoleCatalogNotice({
        type: 'success',
        message: 'Saved the Studio role catalog.',
      });
    } catch (error) {
      setRoleCatalogNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to save the role catalog.',
      });
    } finally {
      setRoleCatalogPending(false);
    }
  };

  const handleRoleImport = async (
    event: React.ChangeEvent<HTMLInputElement>,
  ) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) {
      return;
    }

    setRoleImportPending(true);
    setRoleCatalogNotice(null);
    try {
      const response = await studioApi.importRoleCatalog(file);
      const nextRoles = response.roles.map((role) => createRoleCatalogItem(role));
      queryClient.setQueryData(['studio-roles'], response);
      setRoleCatalogDraft(nextRoles);
      setSelectedRoleKey(nextRoles[0]?.key || '');
      setRoleCatalogNotice({
        type: 'success',
        message: `Imported ${response.importedCount} role(s) from ${response.sourceFilePath || file.name}.`,
      });
    } catch (error) {
      setRoleCatalogNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to import the role catalog.',
      });
    } finally {
      setRoleImportPending(false);
    }
  };

  const handleSaveSettings = async () => {
    if (!settingsDraft) {
      return;
    }

    setSettingsPending(true);
    setSettingsNotice(null);
    try {
      const response = await studioApi.saveSettings({
        runtimeBaseUrl: settingsDraft.runtimeBaseUrl,
        defaultProviderName: settingsDraft.defaultProviderName,
        providers: settingsDraft.providers.map((provider) => ({
          providerName: provider.providerName,
          providerType: provider.providerType,
          model: provider.model,
          endpoint: provider.endpoint,
          apiKey: provider.apiKey,
          clearApiKey: provider.clearApiKeyRequested ? true : undefined,
        })),
      });
      queryClient.setQueryData(['studio-settings'], response);
      queryClient.setQueryData(
        ['studio-workspace-settings'],
        (current: StudioWorkspaceSettings | undefined) =>
          current
            ? {
                ...current,
                runtimeBaseUrl: response.runtimeBaseUrl,
              }
            : current,
      );
      setSettingsDraft(createSettingsDraft(response));
      setSelectedProviderName(
        response.providers.find(
          (provider) =>
            provider.providerName === response.defaultProviderName,
        )?.providerName ||
          response.providers[0]?.providerName ||
          '',
      );
      setSettingsNotice({
        type: 'success',
        message: 'Saved workbench config.',
      });
    } catch (error) {
      setSettingsNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save workbench config.',
      });
    } finally {
      setSettingsPending(false);
    }
  };

  const handleTestRuntime = async () => {
    if (!settingsDraft) {
      return;
    }

    setRuntimeTestPending(true);
    setRuntimeTestResult(null);
    try {
      const response =
        await studioApi.testRuntimeConnection({
              runtimeBaseUrl: settingsDraft.runtimeBaseUrl,
            });
      setRuntimeTestResult(response);
    } catch (error) {
      setSettingsNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to test the Studio runtime connection.',
      });
    } finally {
      setRuntimeTestPending(false);
    }
  };

  const handleAddDirectory = async () => {
    if (!directoryPath.trim()) {
      setSettingsNotice({
        type: 'error',
        message: 'Directory path is required before adding a workflow directory.',
      });
      return;
    }

    setSettingsPending(true);
    setSettingsNotice(null);
    try {
      await studioApi.addWorkflowDirectory({
        path: directoryPath.trim(),
        label: directoryLabel.trim(),
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-settings'],
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows'],
      });
      setDirectoryPath('');
      setDirectoryLabel('');
      setSettingsNotice({
        type: 'success',
        message: 'Added a new Studio workflow directory.',
      });
    } catch (error) {
      setSettingsNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to add the workflow directory.',
      });
    } finally {
      setSettingsPending(false);
    }
  };

  const handleRemoveDirectory = async (directoryId: string) => {
    setSettingsPending(true);
    setSettingsNotice(null);
    try {
      await studioApi.removeWorkflowDirectory(directoryId);
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-settings'],
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows'],
      });
      setSettingsNotice({
        type: 'info',
        message: 'Removed the Studio workflow directory.',
      });
    } catch (error) {
      setSettingsNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to remove the workflow directory.',
      });
    } finally {
      setSettingsPending(false);
    }
  };

  const serializeDocumentMutation = async (nextPayload: {
    document: StudioWorkflowDocument;
    nodeId: string;
  }, options?: {
    readonly selectedNodeId?: string;
    readonly selectedEdgeId?: string;
  }) => {
    const serialized = await studioApi.serializeYaml({
      document: nextPayload.document,
      availableWorkflowNames: workflowNames,
      availableStepTypes,
    });

    setDraftYaml(serialized.yaml);
    setDraftWorkflowName(
      trimOptional(serialized.document.name) || draftWorkflowName || 'draft',
    );
    setSelectedGraphNodeId(options?.selectedNodeId ?? nextPayload.nodeId);
    setSelectedGraphEdgeId(options?.selectedEdgeId ?? '');
    setSaveNotice(null);
    setRunNotice(null);

    return serialized;
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
      await serializeDocumentMutation(
        {
          document: {
            ...document,
            description: value.trim() || undefined,
          },
          nodeId: selectedGraphNodeId,
        },
        {
          selectedNodeId: selectedGraphNodeId,
          selectedEdgeId: selectedGraphEdgeId,
        },
      );
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

  const handleApplyRoleCatalogToWorkflow = async (roleKey: string) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    const savedRole = roleCatalogDraft.find((item) => item.key === roleKey) ?? null;
    if (!document || !savedRole) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft and saved roles before using a catalog role.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        insertCatalogRoleInWorkflow(document, toRoleDefinition(savedRole)),
      );
      setWorkspacePage('studio');
      setStudioView('editor');
      setInspectorTab('roles');
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Added saved role ${savedRole.id} to the workflow.`,
          warning: `Added saved role ${savedRole.id}, but Studio returned warnings.`,
          error: `Added saved role ${savedRole.id}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to use the saved role.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleGraphLayoutChange = (
    nodes: Node[],
  ) => {
    setDraftWorkflowLayout((current: unknown | null) =>
      buildStudioWorkflowLayout(
        activeWorkflowName || draftWorkflowName || 'draft',
        nodes as Node<StudioGraphNodeData>[],
        current,
      ),
    );
  };

  const handleGraphConnect = async (sourceId: string, targetId: string) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before editing graph connections.',
      });
      return;
    }

    const sourceStepId = sourceId.startsWith('step:')
      ? sourceId.slice('step:'.length)
      : '';
    const targetStepId = targetId.startsWith('step:')
      ? targetId.slice('step:'.length)
      : '';
    const sourceStep =
      workflowGraph.steps.find((step) => step.id === sourceStepId) ?? null;

    if (!sourceStepId || !targetStepId || !sourceStep) {
      setInspectorNotice({
        type: 'error',
        message: 'Studio graph connections currently support step-to-step links only.',
      });
      return;
    }

    let branchLabel = suggestBranchLabelForStep(
      sourceStep.type,
      sourceStep.branches,
    );
    if (
      branchLabel === '_default' &&
      typeof window !== 'undefined' &&
      sourceStep.type.trim().toLowerCase() === 'switch'
    ) {
      branchLabel = window.prompt('Branch label', '_default')?.trim() || '_default';
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        connectStepToTarget(document, sourceStepId, targetStepId, branchLabel),
      );
      setInspectorTab('node');
      setSelectedGraphNodeId(sourceId);
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: branchLabel
            ? `Connected ${sourceStepId} to ${targetStepId} on branch ${branchLabel}.`
            : `Connected ${sourceStepId} to ${targetStepId}.`,
          warning: branchLabel
            ? `Connected ${sourceStepId} to ${targetStepId} on branch ${branchLabel}, but Studio returned warnings.`
            : `Connected ${sourceStepId} to ${targetStepId}, but Studio returned warnings.`,
          error: branchLabel
            ? `Connected ${sourceStepId} to ${targetStepId} on branch ${branchLabel}, but Studio returned blocking errors.`
            : `Connected ${sourceStepId} to ${targetStepId}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to update the graph connection.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleAddGraphNode = async (
    stepType: string,
    connectorName?: string,
    preferredPosition?: { x: number; y: number } | null,
  ) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before adding graph nodes.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const nextPayload = insertStepByType(document, stepType, {
          afterStepId: selectedGraphStep?.id || null,
          targetRoleId:
            selectedGraphRole?.id ||
            selectedGraphStep?.targetRole ||
            null,
          connectorName,
          connectors: connectorsQuery.data?.connectors ?? [],
        });
      const serialized = await serializeDocumentMutation(nextPayload);
      const insertedStepId = nextPayload.nodeId.startsWith('step:')
        ? nextPayload.nodeId.slice('step:'.length)
        : '';
      if (preferredPosition && insertedStepId) {
        setDraftWorkflowLayout((current: unknown | null) => {
          const previousLayout =
            current && typeof current === 'object'
              ? (current as StudioWorkflowLayoutDocument)
              : {};

          return {
            ...previousLayout,
            layoutVersion: previousLayout.layoutVersion ?? 1,
            nodePositions: {
              ...(previousLayout.nodePositions ?? {}),
              [insertedStepId]: preferredPosition,
            },
          };
        });
      }
      setInspectorTab('node');
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Added ${stepType} to the workflow draft.`,
          warning: `Added ${stepType}, but Studio returned warnings.`,
          error: `Added ${stepType}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to add the graph node.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleApplyInspectorDraft = async () => {
    if (!nodeInspectorDraft) {
      return;
    }

    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before applying node changes.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const nextPayload =
        nodeInspectorDraft.kind === 'step'
          ? applyStepInspectorDraft(
              document,
              selectedGraphStep?.id || nodeInspectorDraft.id,
              nodeInspectorDraft,
            )
          : applyRoleInspectorDraft(
              document,
              selectedGraphRole?.id || nodeInspectorDraft.id,
              nodeInspectorDraft,
            );
      const serialized = await serializeDocumentMutation(nextPayload);
      setInspectorNotice(readValidationSummary(serialized.findings));
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to apply inspector changes.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleRemoveStepConnection = async (
    targetStepId: string,
    branchLabel?: string | null,
  ) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document || !selectedGraphStep) {
      setInspectorNotice({
        type: 'error',
        message: 'Select a workflow step before removing a connection.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        removeStepConnection(
          document,
          selectedGraphStep.id,
          targetStepId,
          branchLabel,
        ),
      );
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: branchLabel
            ? `Removed branch ${branchLabel} from ${selectedGraphStep.id}.`
            : `Removed next connection from ${selectedGraphStep.id}.`,
          warning: branchLabel
            ? `Removed branch ${branchLabel} from ${selectedGraphStep.id}, but Studio returned warnings.`
            : `Removed next connection from ${selectedGraphStep.id}, but Studio returned warnings.`,
          error: branchLabel
            ? `Removed branch ${branchLabel} from ${selectedGraphStep.id}, but Studio returned blocking errors.`
            : `Removed next connection from ${selectedGraphStep.id}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to remove the graph connection.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleRemoveSelectedGraphEdge = async () => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document || !selectedGraphEdge) {
      setInspectorNotice({
        type: 'error',
        message: 'Select a workflow connection before removing it.',
      });
      return;
    }
    if (selectedGraphEdge.implicit) {
      setInspectorNotice({
        type: 'warning',
        message:
          'This connection is part of the canvas fallback flow. Edit the surrounding steps to change it.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        removeStepConnection(
          document,
          selectedGraphEdge.sourceStepId,
          selectedGraphEdge.targetStepId,
          selectedGraphEdge.branchLabel,
        ),
        {
          selectedNodeId: '',
          selectedEdgeId: '',
        },
      );
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: selectedGraphEdge.branchLabel
            ? `Removed branch ${selectedGraphEdge.branchLabel} from ${selectedGraphEdge.sourceStepId}.`
            : `Removed connection from ${selectedGraphEdge.sourceStepId} to ${selectedGraphEdge.targetStepId}.`,
          warning: selectedGraphEdge.branchLabel
            ? `Removed branch ${selectedGraphEdge.branchLabel} from ${selectedGraphEdge.sourceStepId}, but Studio returned warnings.`
            : `Removed connection from ${selectedGraphEdge.sourceStepId} to ${selectedGraphEdge.targetStepId}, but Studio returned warnings.`,
          error: selectedGraphEdge.branchLabel
            ? `Removed branch ${selectedGraphEdge.branchLabel} from ${selectedGraphEdge.sourceStepId}, but Studio returned blocking errors.`
            : `Removed connection from ${selectedGraphEdge.sourceStepId} to ${selectedGraphEdge.targetStepId}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to remove the selected graph connection.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleAddWorkflowRole = async () => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before adding workflow roles.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(addWorkflowRole(document));
      setInspectorTab('roles');
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: 'Added a new workflow role.',
          warning: 'Added a new workflow role, but Studio returned warnings.',
          error: 'Added a new workflow role, but Studio returned blocking errors.',
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to add the workflow role.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleUseSavedRole = async (roleId: string) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    const savedRole = rolesQuery.data?.roles.find((item) => item.id === roleId) ?? null;
    if (!document || !savedRole) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft and saved roles before using a catalog role.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        insertCatalogRoleInWorkflow(document, savedRole),
      );
      setInspectorTab('roles');
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Added saved role ${savedRole.id} to the workflow.`,
          warning: `Added saved role ${savedRole.id}, but Studio returned warnings.`,
          error: `Added saved role ${savedRole.id}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to use the saved role.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleUpdateWorkflowRole = async (
    currentRoleId: string,
    nextRole: {
      readonly id: string;
      readonly name: string;
      readonly provider: string;
      readonly model: string;
      readonly systemPrompt: string;
      readonly connectors: readonly string[];
    },
  ) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before editing workflow roles.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        updateWorkflowRole(document, currentRoleId, nextRole),
      );
      setInspectorTab('roles');
      setInspectorNotice(readValidationSummary(serialized.findings));
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to update the workflow role.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleRemoveWorkflowRole = async (roleId: string) => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document) {
      setInspectorNotice({
        type: 'error',
        message: 'Load a workflow draft before removing workflow roles.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        removeWorkflowRole(document, roleId),
      );
      setInspectorTab('roles');
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Removed workflow role ${roleId}.`,
          warning: `Removed workflow role ${roleId}, but Studio returned warnings.`,
          error: `Removed workflow role ${roleId}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to remove the workflow role.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleInsertStep = async () => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document || !selectedGraphStep) {
      setInspectorNotice({
        type: 'error',
        message: 'Select a workflow step before inserting a new step.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const serialized = await serializeDocumentMutation(
        insertStepAfter(document, selectedGraphStep.id),
      );
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Inserted a new step after ${selectedGraphStep.id}.`,
          warning: `Inserted a new step after ${selectedGraphStep.id}, but Studio returned warnings.`,
          error: `Inserted a new step after ${selectedGraphStep.id}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to insert a new step.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const handleRemoveStep = async () => {
    const document = cloneStudioWorkflowDocument(
      activeWorkflowDocument as StudioWorkflowDocument | null,
    );
    if (!document || !selectedGraphStep) {
      setInspectorNotice({
        type: 'error',
        message: 'Select a workflow step before removing it.',
      });
      return;
    }

    setInspectorPending(true);
    setInspectorNotice(null);

    try {
      const removedStepId = selectedGraphStep.id;
      const serialized = await serializeDocumentMutation(
        removeStep(document, removedStepId),
      );
      setInspectorNotice(
        readValidationSummary(serialized.findings, {
          success: `Removed ${removedStepId} from the workflow draft.`,
          warning: `Removed ${removedStepId}, but Studio returned warnings.`,
          error: `Removed ${removedStepId}, but Studio returned blocking errors.`,
        }),
      );
    } catch (error) {
      setInspectorNotice({
        type: 'error',
        message:
          error instanceof Error ? error.message : 'Failed to remove the step.',
      });
    } finally {
      setInspectorPending(false);
    }
  };

  const navItems: StudioShellNavItem[] = [
    {
      key: 'workflows',
      label: '行为定义',
      description: '浏览团队可用的行为定义并开始新的草稿。',
      count: visibleWorkflowSummaries.length,
    },
    {
      key: 'roles',
      label: 'Agent 角色',
      description: '编辑、导入并保存 Agent 角色定义。',
      count: rolesQuery.data?.roles.length ?? 0,
    },
    {
      key: 'connectors',
      label: '集成',
      description: '编辑、导入并保存团队可用集成。',
      count: connectorsQuery.data?.connectors.length ?? 0,
    },
    {
      key: 'execution',
      label: '测试运行',
      description: '查看执行状态、执行图和运行日志。',
      count: executionsQuery.data?.length ?? 0,
    },
    {
      key: 'settings',
      label: '编辑器设置',
      description: '管理 AI Provider，并检查运行时与行为定义配置。',
      count: workspaceSettingsQuery.data?.directories.length ?? 0,
    },
  ];
  if (appContextQuery.data?.features.scripts) {
    navItems.splice(2, 0, {
      key: 'scripts',
      label: '脚本行为',
      description: '编写、校验、测试并发布 scope 感知脚本。',
    });
  }

  const applyWorkspacePageSelection = React.useCallback(
    (page: StudioWorkspacePage) => {
      if (page === 'studio' || page === 'execution') {
        ensureActiveWorkflowDraftLoaded();
      }
      if (page === 'execution') {
        setWorkspacePage('studio');
        setStudioView('execution');
        return;
      }
      setWorkspacePage(page);
      if (page === 'studio') {
        setStudioView('editor');
      }
      if (page === 'scripts') {
        setSelectedWorkflowId('');
        setTemplateWorkflow('');
        setDraftMode('');
        setLegacySource('');
      }
    },
    [ensureActiveWorkflowDraftLoaded, studioView],
  );

  const pageTitle =
    workspacePage === 'workflows'
      ? '行为定义'
      : workspacePage === 'scripts'
        ? '脚本行为'
      : workspacePage === 'studio'
        ? studioView === 'execution'
          ? '测试运行'
          : '行为定义'
        : workspacePage === 'roles'
          ? 'Agent 角色'
          : workspacePage === 'connectors'
            ? '集成'
            : '编辑器设置';

  const studioContextActions =
    workspacePage === 'studio' && studioView === 'execution' ? (
      <Space wrap size={[8, 8]}>
        <Button
          loading={runPending}
          onClick={() => void handleStartExecution()}
          type="primary"
          disabled={!canRunWorkflow || runPending}
        >
          重新运行
        </Button>
      </Space>
    ) : workspacePage === 'studio' ? (
      <Space wrap size={[8, 8]}>
        <Button onClick={() => handleSwitchStudioView('execution')}>
          测试运行
        </Button>
        <Button
          disabled={!canSaveWorkflow}
          loading={savePending}
          onClick={() => void handleSaveDraft()}
          type="primary"
        >
          保存
        </Button>
      </Space>
    ) : undefined;

  const studioContextScopeLabel =
    routeState.scopeLabel ||
    scopeBindingQuery.data?.displayName ||
    resolvedStudioScopeId;
  const studioContextMemberLabel =
    routeState.memberLabel || activeWorkflowName || templateWorkflow;
  const currentStudioReturnTo =
    typeof window === 'undefined'
      ? ''
      : sanitizeReturnTo(
          `${window.location.pathname}${window.location.search}${window.location.hash}`,
        );
  const studioContextCode = [
    resolvedStudioScopeId,
    scopeBindingQuery.data?.serviceId,
  ]
    .map((value) => trimOptional(value))
    .filter(Boolean)
    .join(' · ');
  const studioContextBar = (
    <div
      style={{
        alignItems: 'center',
        background: '#ffffff',
        borderBottom: '1px solid #f0f0f0',
        display: 'flex',
        gap: 12,
        minHeight: 44,
        padding: '0 16px',
      }}
    >
      <button
        type="button"
        onClick={() =>
          history.push(
            resolvedStudioScopeId
              ? buildTeamDetailHref({
                  scopeId: resolvedStudioScopeId,
                  tab: 'advanced',
                  serviceId: scopeBindingQuery.data?.serviceId || undefined,
                })
              : buildTeamsHref(),
          )
        }
        style={{
          alignItems: 'center',
          background: 'transparent',
          border: 'none',
          color: '#1890ff',
          cursor: 'pointer',
          display: 'inline-flex',
          flexShrink: 0,
          fontSize: 12,
          gap: 4,
          padding: 0,
        }}
      >
        ← {studioContextScopeLabel || '返回团队'}
      </button>
      <div
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'wrap',
          gap: 8,
          minWidth: 0,
        }}
      >
        <span
          style={{
            color: '#8c8c8c',
            fontSize: 13,
            lineHeight: '20px',
          }}
        >
          / <b style={{ color: '#1d2129' }}>
            {studioContextMemberLabel || studioContextScopeLabel || '当前团队'}
          </b>{' '}
          / {pageTitle}
        </span>
        {studioContextCode ? (
          <code
            style={{
              color: '#8c8c8c',
              fontFamily: '"SF Mono", "JetBrains Mono", monospace',
              fontSize: 11,
            }}
          >
            {studioContextCode}
          </code>
        ) : null}
      </div>
      {studioContextActions ? (
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 8,
            marginLeft: 'auto',
          }}
        >
          {studioContextActions}
        </div>
      ) : null}
    </div>
  );

  const inspectorContent = (
    <StudioInspectorPane
      draftYaml={draftYaml}
      inspectorTab={inspectorTab}
      showTabSwitcher={false}
      workflowRoleIds={workflowRoleIds}
      workflowStepIds={workflowStepIds}
      workflowRoles={workflowGraph.roles}
      workflowSteps={workflowGraph.steps}
      connectors={connectorsQuery.data?.connectors ?? []}
      savedRoles={rolesQuery.data?.roles ?? []}
      selectedGraphRole={selectedGraphRole}
      selectedGraphStep={selectedGraphStep}
      nodeInspectorDraft={nodeInspectorDraft}
      inspectorPending={inspectorPending}
      inspectorNotice={inspectorNotice}
      validationLoading={parseYamlQuery.isLoading}
      validationError={parseYamlQuery.isError ? parseYamlQuery.error : null}
      validationFindings={activeWorkflowFindings}
      parsedWorkflowName={parseYamlQuery.data?.document?.name || ''}
      activeWorkflowName={activeWorkflowName}
      activeWorkflowDescription={activeWorkflowDescription}
      onSetInspectorTab={setInspectorTab}
      onSetDraftYaml={(value) => {
        setDraftYaml(value);
        setSaveNotice(null);
      }}
      onValidateDraft={() => {
        void parseYamlQuery.refetch();
      }}
      onChangeNodeInspectorDraft={setNodeInspectorDraft}
      onApplyNodeChanges={() => void handleApplyInspectorDraft()}
      onInsertStep={() => void handleInsertStep()}
      onAddWorkflowRole={() => void handleAddWorkflowRole()}
      onUseSavedRole={(roleId) => void handleUseSavedRole(roleId)}
      onUpdateWorkflowRole={(roleId, nextRole) =>
        void handleUpdateWorkflowRole(roleId, nextRole)
      }
      onDeleteConnection={(targetStepId, branchLabel) =>
        void handleRemoveStepConnection(targetStepId, branchLabel)
      }
      onDeleteWorkflowRole={(roleId) => void handleRemoveWorkflowRole(roleId)}
      onDeleteStep={() => void handleRemoveStep()}
      onResetSelectedNode={() => {
        if (selectedGraphStep) {
          setNodeInspectorDraft(createStepInspectorDraft(selectedGraphStep));
        } else if (selectedGraphRole) {
          setNodeInspectorDraft(createRoleInspectorDraft(selectedGraphRole));
        }
        setInspectorNotice(null);
      }}
    />
  );

  const currentPageContent =
    workspacePage === 'workflows' ? (
      <div
        data-testid="studio-workflows-viewport"
        style={{
          display: 'flex',
          flexDirection: 'column',
          minWidth: 0,
        }}
      >
        <StudioWorkflowsPage
          workflows={{
            isLoading: workflowsQuery.isLoading,
            isError: workflowsQuery.isError,
            error: workflowsQuery.error,
            data: visibleWorkflowSummaries,
          }}
          workspaceSettings={workspaceSettingsQuery}
          workflowStorageMode={
            appContextQuery.data?.workflowStorageMode || 'workspace'
          }
          selectedWorkflowId={selectedWorkflowId}
          selectedDirectoryId={draftDirectoryId || defaultDirectoryId}
          templateWorkflow={templateWorkflow}
          draftMode={draftMode}
          activeWorkflowName={activeWorkflowName}
          activeWorkflowDescription={activeWorkflowDescription}
          activeWorkflowSourceKey={activeWorkflowSourceKey}
          workflowSearch={workflowSearch}
          showDirectoryForm={showWorkflowDirectoryForm}
          directoryPath={directoryPath}
          directoryLabel={directoryLabel}
          workflowImportPending={workflowImportPending}
          workflowImportInputRef={workflowImportInputRef}
          onOpenWorkflow={openWorkspaceWorkflow}
          onStartBlankDraft={startBlankDraft}
          onOpenCurrentDraft={() => {
            ensureActiveWorkflowDraftLoaded();
            setWorkspacePage('studio');
            setStudioView('editor');
          }}
          onSelectDirectoryId={setDraftDirectoryId}
          onSetWorkflowSearch={setWorkflowSearch}
          onToggleDirectoryForm={() =>
            setShowWorkflowDirectoryForm((current) => !current)
          }
          onSetDirectoryPath={setDirectoryPath}
          onSetDirectoryLabel={setDirectoryLabel}
          onAddDirectory={() => void handleAddDirectory()}
          onRemoveDirectory={(directoryId) => void handleRemoveDirectory(directoryId)}
          onWorkflowImportClick={() => workflowImportInputRef.current?.click()}
          onWorkflowImportChange={handleWorkflowImport}
        />
      </div>
    ) : workspacePage === 'studio' ? (
      studioView === 'execution' ? (
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
          onSwitchStudioView={handleSwitchStudioView}
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
      ) : (
        <StudioEditorPage
          workflows={{
            isLoading: workflowsQuery.isLoading,
            isError: workflowsQuery.isError,
            error: workflowsQuery.error,
            data: visibleWorkflowSummaries,
          }}
          selectedWorkflow={selectedWorkflowQuery}
          templateWorkflow={templateWorkflowQuery}
          connectors={connectorsQuery}
          draftYaml={draftYaml}
          draftWorkflowName={draftWorkflowName}
          draftDirectoryId={draftDirectoryId}
          draftFileName={draftFileName}
          draftMode={draftMode}
          selectedWorkflowId={selectedWorkflowId}
          templateWorkflowName={templateWorkflow}
          activeWorkflowDescription={activeWorkflowDescription}
          activeWorkflowFile={activeWorkflowFile}
          isDraftDirty={isDraftDirty}
          workflowGraph={workflowGraph}
          parseYaml={parseYamlQuery}
          selectedGraphNodeId={selectedGraphNodeId}
          selectedGraphEdge={selectedGraphEdge}
          workflowRoleIds={workflowRoleIds}
          workflowStepIds={workflowStepIds}
          inspectorTab={inspectorTab}
          inspectorContent={inspectorContent}
          workspaceSettings={workspaceSettingsQuery}
          savePending={savePending}
          canSaveWorkflow={canSaveWorkflow}
          saveNotice={saveNotice}
          workflowImportPending={workflowImportPending}
          workflowImportNotice={workflowImportNotice}
          workflowImportInputRef={workflowImportInputRef}
          askAiPrompt={askAiPrompt}
          askAiPending={askAiPending}
          askAiNotice={askAiNotice}
          askAiReasoning={askAiReasoning}
          askAiAnswer={askAiAnswer}
          runPrompt={runPrompt}
          recentPromptHistory={recentPromptHistory}
          promptHistoryCount={promptHistory.length}
          runPending={runPending}
          canOpenRunWorkflow={canOpenRunWorkflow}
          canRunWorkflow={canRunWorkflow}
          runNotice={runNotice}
          resolvedScopeId={resolvedStudioScopeId || undefined}
          publishPending={publishPending}
          canPublishWorkflow={canPublishWorkflow}
          publishNotice={publishNotice}
          scopeBinding={scopeBindingQuery.data}
          scopeBindingLoading={scopeBindingQuery.isLoading}
          scopeBindingError={scopeBindingQuery.isError ? scopeBindingQuery.error : null}
          gAgentTypes={gAgentTypesQuery.data ?? []}
          gAgentTypesLoading={gAgentTypesQuery.isLoading}
          gAgentTypesError={gAgentTypesQuery.isError ? gAgentTypesQuery.error : null}
          bindingActivationRevisionId={bindingActivationRevisionId}
          bindingRetirementRevisionId={bindingRetirementRevisionId}
          onSwitchStudioView={handleSwitchStudioView}
          onExportDraft={() => void handleExportDraft()}
          onSelectGraphNode={(nodeId) => {
            setSelectedGraphNodeId(nodeId);
            setSelectedGraphEdgeId('');
            setInspectorTab('node');
          }}
          onClearGraphSelection={() => {
            setSelectedGraphNodeId('');
            setSelectedGraphEdgeId('');
            setInspectorNotice(null);
          }}
          onSelectGraphEdge={(edgeId) => {
            setSelectedGraphNodeId('');
            setSelectedGraphEdgeId(edgeId);
            setInspectorNotice(null);
          }}
          onAddGraphNode={(stepType, connectorName) =>
            void handleAddGraphNode(stepType, connectorName)
          }
          onConnectGraphNodes={(sourceId, targetId) =>
            void handleGraphConnect(sourceId, targetId)
          }
          onUpdateGraphLayout={handleGraphLayoutChange}
          onDeleteSelectedGraphEdge={() => void handleRemoveSelectedGraphEdge()}
          onSetWorkflowDescription={(value) =>
            void handleSetWorkflowDescription(value)
          }
          onSetDraftYaml={(value) => {
            setDraftYaml(value);
            setSaveNotice(null);
          }}
          onSetDraftWorkflowName={(value) => {
            setDraftWorkflowName(value);
            setSaveNotice(null);
          }}
          onSetDraftDirectoryId={(value) => {
            setDraftDirectoryId(value);
            setSaveNotice(null);
          }}
          onSetDraftFileName={(value) => {
            setDraftFileName(value);
            setSaveNotice(null);
          }}
          onSetInspectorTab={setInspectorTab}
          onValidateDraft={() => {
            setInspectorTab('yaml');
            void parseYamlQuery.refetch();
          }}
          onWorkflowImportClick={() => workflowImportInputRef.current?.click()}
          onWorkflowImportChange={handleWorkflowImport}
          onResetDraft={resetDraftFromSource}
          onSaveDraft={() => void handleSaveDraft()}
          onPublishWorkflow={() => void handlePublishWorkflow()}
          onOpenWorkflow={openWorkspaceWorkflow}
          onStartBlankDraft={startBlankDraft}
          onBindGAgent={(input, options) =>
            handleBindGAgent(input, options)
          }
          onActivateBindingRevision={(revisionId) =>
            void handleActivateBindingRevision(revisionId)
          }
          onRetireBindingRevision={(revisionId) =>
            void handleRetireBindingRevision(revisionId)
          }
          onInspectPublishedWorkflow={() =>
            history.push(
              buildRuntimeWorkflowsHref({
                workflow: templateWorkflow,
              }),
            )
          }
          onRunInConsole={async () => {
            const workflowName = (activeWorkflowName || templateWorkflow || '').trim();
            const scopeId = resolvedStudioScopeId;
            try {
              const workflowYamls = await buildWorkflowYamlBundle();
              const draftKey = saveScopeDraftRunPayload({
                bundleName: workflowName,
                bundleYamls: workflowYamls,
              });
              history.push(
                buildRuntimeRunsHref({
                  scopeId: scopeId || undefined,
                  route: workflowName || undefined,
                  prompt: runPrompt || undefined,
                  draftKey,
                  returnTo: currentStudioReturnTo || undefined,
                }),
              );
            } catch {
              history.push(
                buildRuntimeRunsHref({
                  scopeId: scopeId || undefined,
                  route: workflowName || undefined,
                  prompt: runPrompt || undefined,
                  returnTo: currentStudioReturnTo || undefined,
                }),
              );
            }
          }}
          onAskAiPromptChange={(value) => {
            setAskAiPrompt(value);
            setAskAiNotice(null);
          }}
          onAskAiGenerate={() => void handleAskAiGenerate()}
          onRunPromptChange={applyRunPrompt}
          onClearPromptHistory={clearPromptHistory}
          onReusePrompt={applyRunPrompt}
          onOpenWorkflowFromHistory={openWorkflowFromHistory}
          onStartExecution={() => void handleStartExecution()}
          onOpenProjectOverview={() => {
            history.push(
              resolvedStudioScopeId
                ? buildTeamDetailHref({
                    scopeId: resolvedStudioScopeId,
                    tab: 'advanced',
                    serviceId: scopeBindingQuery.data?.serviceId || undefined,
                  })
                : buildTeamsHref(),
            );
          }}
          onOpenProjectInvoke={() => {
            history.push(
              resolvedStudioScopeId
                ? `/scopes/invoke?scopeId=${encodeURIComponent(resolvedStudioScopeId)}`
                : '/scopes/invoke',
            );
          }}
          onOpenExecutions={() => {
            setWorkspacePage('studio');
            setStudioView('execution');
          }}
        />
      )
    ) : workspacePage === 'scripts' ? (
      appContextQuery.data?.features.scripts ? (
        <ScriptsWorkbenchPage
          appContext={appContextQuery.data}
          initialScriptId={selectedScriptId}
          onUnsavedChangesChange={setScriptsHasUnsavedChanges}
          onSelectScriptId={setSelectedScriptId}
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
              gap: 8,
            }}
          >
            <strong>Scripts Studio is unavailable in the current host.</strong>
            <span style={{ color: 'var(--ant-color-text-secondary)' }}>
              The current Studio host does not expose the Scripts capability for
              this session.
            </span>
          </div>
        </div>
      )
    ) : workspacePage === 'roles' ? (
      <StudioRolesPage
        roles={rolesQuery}
        appearanceTheme={studioAppearance.appearanceTheme}
        colorMode={studioAppearance.colorMode}
        roleCatalogDraft={roleCatalogDraft}
        roleCatalogMeta={roleCatalogMeta}
        roleCatalogIsRemote={roleCatalogIsRemote}
        roleCatalogDirty={roleCatalogDirty}
        roleCatalogPending={roleCatalogPending}
        roleCatalogNotice={roleCatalogNotice}
        roleImportPending={roleImportPending}
        roleImportInputRef={roleImportInputRef}
        roleSearch={roleSearch}
        roleModalOpen={roleModalOpen}
        roleDraft={roleDraft}
        roleDraftMeta={roleDraftMeta}
        selectedRole={selectedRole}
        connectors={connectorCatalogDraft.map((connector) => ({
          name: connector.name,
        }))}
        settingsProviders={settingsProviders}
        onRoleSearchChange={setRoleSearch}
        onOpenRoleModal={handleOpenRoleModal}
        onCloseRoleModal={() => void handleCloseRoleModal()}
        onRoleDraftChange={(updater) =>
          setRoleDraft((current) => updater(current ?? createEmptyRoleDraft()))
        }
        onSubmitRoleDraft={() => void handleSubmitRoleDraft()}
        onRoleImportClick={() => roleImportInputRef.current?.click()}
        onRoleImportChange={handleRoleImport}
        onSaveRoles={() => void handleSaveRoles()}
        onSelectRoleKey={setSelectedRoleKey}
        onDeleteRole={handleDeleteRole}
        onApplyRoleToWorkflow={(roleKey) =>
          void handleApplyRoleCatalogToWorkflow(roleKey)
        }
        onUpdateRoleCatalog={updateRoleCatalogDraft}
      />
    ) : workspacePage === 'connectors' ? (
      <StudioConnectorsPage
        connectors={connectorsQuery}
        appearanceTheme={studioAppearance.appearanceTheme}
        colorMode={studioAppearance.colorMode}
        connectorCatalogDraft={connectorCatalogDraft}
        connectorCatalogMeta={connectorCatalogMeta}
        connectorCatalogIsRemote={connectorCatalogIsRemote}
        connectorCatalogDirty={connectorCatalogDirty}
        connectorCatalogPending={connectorCatalogPending}
        connectorImportPending={connectorImportPending}
        connectorCatalogNotice={connectorCatalogNotice}
        connectorImportInputRef={connectorImportInputRef}
        connectorSearch={connectorSearch}
        connectorModalOpen={connectorModalOpen}
        connectorDraft={connectorDraft}
        connectorDraftMeta={connectorDraftMeta}
        selectedConnector={selectedConnector}
        onConnectorSearchChange={setConnectorSearch}
        onOpenConnectorModal={handleOpenConnectorModal}
        onCloseConnectorModal={() => void handleCloseConnectorModal()}
        onConnectorDraftChange={(updater) =>
          setConnectorDraft((current) =>
            updater(current ?? createEmptyConnectorDraft())
          )
        }
        onSubmitConnectorDraft={() => void handleSubmitConnectorDraft()}
        onConnectorImportClick={() => connectorImportInputRef.current?.click()}
        onConnectorImportChange={handleConnectorImport}
        onSaveConnectors={() => void handleSaveConnectors()}
        onSelectConnectorKey={setSelectedConnectorKey}
        onDeleteConnector={handleDeleteConnector}
        onUpdateConnectorCatalog={updateConnectorCatalogDraft}
      />
    ) : (
      <div
        style={{
          display: 'flex',
          flex: 1,
          flexDirection: 'column',
          minHeight: 0,
          overflow: 'hidden',
        }}
      >
        <StudioSettingsPage
          workspaceSettings={workspaceSettingsQuery}
          settings={settingsQuery}
          settingsDraft={settingsDraft}
          selectedProvider={selectedProvider}
          hostMode={studioHostMode}
          workflowStorageMode={
            appContextQuery.data?.workflowStorageMode ?? 'workspace'
          }
          settingsDirty={settingsDirty}
          settingsPending={settingsPending}
          runtimeTestPending={runtimeTestPending}
          settingsNotice={settingsNotice}
          runtimeTestResult={runtimeTestResult}
          directoryPath={directoryPath}
          directoryLabel={directoryLabel}
          onSaveSettings={() => void handleSaveSettings()}
          onTestRuntime={() => void handleTestRuntime()}
          onSetSettingsDraft={setSettingsDraft}
          onAddProvider={() => {
            if (!settingsDraft) {
              return;
            }

            const nextProvider = createProviderDraft(
              settingsDraft.providerTypes,
              settingsDraft.providers,
            );
            setSettingsDraft({
              ...settingsDraft,
              providers: [nextProvider, ...settingsDraft.providers],
              defaultProviderName:
                settingsDraft.defaultProviderName || nextProvider.providerName,
            });
            setSelectedProviderName(nextProvider.providerName);
          }}
          onSelectProviderName={setSelectedProviderName}
          onDeleteSelectedProvider={() => {
            if (!settingsDraft || !selectedProvider) {
              return;
            }

            const nextProviders = settingsDraft.providers.filter(
              (provider) =>
                provider.providerName !== selectedProvider.providerName,
            );
            setSettingsDraft({
              ...settingsDraft,
              providers: nextProviders,
              defaultProviderName:
                settingsDraft.defaultProviderName ===
                selectedProvider.providerName
                  ? nextProviders[0]?.providerName || ''
                  : settingsDraft.defaultProviderName,
            });
            setSelectedProviderName(nextProviders[0]?.providerName || '');
          }}
          onSetDefaultProvider={() => {
            if (!settingsDraft || !selectedProvider) {
              return;
            }

            setSettingsDraft({
              ...settingsDraft,
              defaultProviderName: selectedProvider.providerName,
            });
          }}
          onSetDirectoryPath={setDirectoryPath}
          onSetDirectoryLabel={setDirectoryLabel}
          onAddDirectory={() => void handleAddDirectory()}
          onRemoveDirectory={(directoryId) =>
            void handleRemoveDirectory(directoryId)
          }
        />
      </div>
    );

  const pageContainerTitle =
    logsPopoutMode === 'popout' ? 'Execution logs' : undefined;

  return (
    <PageContainer
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
            contextBar={studioContextBar}
            currentPage={
              workspacePage === 'studio'
                ? studioView === 'execution'
                  ? 'execution'
                  : 'workflows'
                : workspacePage
            }
            navItems={navItems}
            onSelectPage={(page: StudioWorkspacePage) => {
              if (
                workspacePage === 'scripts' &&
                page !== 'scripts' &&
                scriptsHasUnsavedChanges
              ) {
                setPendingWorkspacePage(page);
                return;
              }

              applyWorkspacePageSelection(page);
            }}
            pageTitle={pageTitle}
            showPageHeader={false}
          >
            {currentPageContent}
          </StudioShell>
        )}
        <Modal
          open={Boolean(pendingWorkspacePage)}
          title="Leave Scripts Studio?"
          okText="Leave page"
          cancelText="Continue editing"
          onOk={() => {
            if (pendingWorkspacePage) {
              applyWorkspacePageSelection(pendingWorkspacePage);
            }
            setPendingWorkspacePage(null);
          }}
          onCancel={() => setPendingWorkspacePage(null)}
        >
          <div style={{ color: '#4b5563', lineHeight: 1.7 }}>
            The current script changes have not been saved to Scope yet. Your
            local draft will still be kept in this browser, but these changes
            will not be visible in Scope until you save them.
          </div>
        </Modal>
      </StudioBootstrapGate>
    </PageContainer>
  );
};

export default StudioPage;
