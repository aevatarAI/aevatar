import { PageContainer } from '@ant-design/pro-components';
import { AGUIEventType } from '@aevatar-react-sdk/types';
import { DeleteOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
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
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  extractRunFinishedOutput,
  type RuntimeEvent,
} from '@/shared/agui/runtimeEventSemantics';
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
  Modal,
  Popover,
  Typography,
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
  removeSteps,
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
  buildStudioWorkflowMemberKey,
  buildStudioRoute,
  resolveStudioWorkflowMemberRouteValue,
  type StudioBuildFocus,
  type StudioIntent,
  type StudioStep,
  type StudioTab,
} from '@/shared/studio/navigation';
import type {
  WorkflowCatalogDefinition,
} from '@/shared/models/runtime/catalog';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import {
  buildRuntimeGAgentAssemblyQualifiedName,
  matchesRuntimeGAgentTypeDescriptor,
} from '@/shared/models/runtime/gagents';
import {
  getScopeServiceCurrentRevision,
  type ScopeServiceRunAuditSnapshot,
  type ScopeServiceRunAuditStep,
  type ScopeServiceRunSummary,
} from '@/shared/models/runtime/scopeServices';
import type { ServiceCatalogSnapshot } from '@/shared/models/services';
import type {
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioMemberSummary,
  StudioValidationFinding,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowDirectory,
} from '@/shared/studio/models';
import {
  formatStudioMemberLifecycleStage,
  normalizeStudioMemberBindingImplementationKind,
} from '@/shared/studio/models';
import {
  clearStudioObserveSessionSeed,
  isStudioObserveSessionSeedFresh,
  loadStudioObserveSessionSeed,
  saveStudioObserveSessionSeed,
  type StudioObserveSessionSeed,
} from '@/shared/studio/observeSession';
import { embeddedPanelStyle } from '@/shared/ui/proComponents';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
} from '@/shared/ui/interactionStandards';
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
  memberKey: string;
  memberId: string;
  step: StudioStep;
  focusKey: string;
  tab: StudioTab;
  intent: StudioIntent | '';
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

type StudioRouteMemberKind = 'workflow' | 'script' | 'member' | 'none';
type StudioRouteMemberState = {
  key: string;
  kind: StudioRouteMemberKind;
  value: string;
  memberId: string;
  serviceId: string;
};

type BuildMode = 'workflow' | 'script' | 'gagent';
type BuildSurface = 'editor' | 'scripts' | 'gagent';
type StudioSurface = 'build' | 'bind' | 'invoke' | 'observe';

type DraftSaveNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type InventoryBusyAction = '' | 'create' | 'rename' | 'delete';

type DraftRunNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

type StudioNotice = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type OrderedStudioShellMemberItem = StudioShellMemberItem & {
  readonly insertionOrder: number;
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

const visuallyHiddenStyle: React.CSSProperties = {
  border: 0,
  clip: 'rect(0 0 0 0)',
  height: 1,
  margin: -1,
  overflow: 'hidden',
  padding: 0,
  position: 'absolute',
  whiteSpace: 'nowrap',
  width: 1,
};

const inventoryActionsStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
};

const inventoryActionsHintStyle: React.CSSProperties = {
  color: '#7a6d59',
  fontSize: 11,
  lineHeight: '16px',
};

const inventorySelectionPillStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255, 250, 244, 0.96)',
  border: '1px solid #e7dece',
  borderRadius: 999,
  color: '#5f574b',
  display: 'inline-flex',
  fontSize: 10.5,
  fontWeight: 700,
  gap: 6,
  lineHeight: '16px',
  maxWidth: '100%',
  minHeight: 24,
  padding: '0 9px',
};

const inventorySelectionLabelStyle: React.CSSProperties = {
  color: '#9a8b73',
  flexShrink: 0,
  fontSize: 9.5,
  letterSpacing: '0.06em',
  textTransform: 'uppercase',
};

const inventorySelectionValueStyle: React.CSSProperties = {
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const inventoryActionRowStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
};

const inventoryActionButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255, 252, 246, 0.98)',
  border: '1px solid #e6decd',
  borderRadius: 999,
  color: '#5f574b',
  cursor: 'pointer',
  display: 'inline-flex',
  flexShrink: 0,
  fontSize: 10.5,
  fontWeight: 700,
  gap: 6,
  minHeight: 28,
  padding: '0 10px',
};

const inventoryActionPrimaryButtonStyle: React.CSSProperties = {
  ...inventoryActionButtonStyle,
  background: 'rgba(17, 24, 39, 0.96)',
  border: '1px solid rgba(17, 24, 39, 0.96)',
  color: '#fbfaf6',
};

const inventoryActionDangerButtonStyle: React.CSSProperties = {
  ...inventoryActionButtonStyle,
  background: 'rgba(255, 245, 245, 0.98)',
  border: '1px solid rgba(248, 113, 113, 0.24)',
  color: '#b91c1c',
};

const memberEmptyStatePanelStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  alignItems: 'flex-start',
  background: 'rgba(255, 252, 246, 0.98)',
  borderColor: 'rgba(229, 220, 203, 0.92)',
  display: 'grid',
  gap: 16,
  justifyContent: 'center',
  marginTop: 8,
  minHeight: 280,
  padding: '28px 28px 24px',
};

const memberEmptyStateTitleStyle: React.CSSProperties = {
  color: '#1f2937',
  fontSize: 24,
  fontWeight: 700,
  letterSpacing: '-0.02em',
  lineHeight: '30px',
  margin: 0,
};

const memberEmptyStateBodyStyle: React.CSSProperties = {
  color: '#6b7280',
  fontSize: 13,
  lineHeight: '20px',
  margin: 0,
  maxWidth: 520,
};

const memberEmptyStateActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
};

const inventoryCreateModalStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
};

const inventoryCreateTypeRowStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
};

const inventoryCreateTypeChipStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'rgba(255, 252, 246, 0.98)',
  border: '1px solid #e6decd',
  borderRadius: 999,
  color: '#5f574b',
  display: 'inline-flex',
  fontSize: 10.5,
  fontWeight: 700,
  gap: 6,
  minHeight: 30,
  padding: '0 10px',
};

const inventoryCreateTypeChipActiveStyle: React.CSSProperties = {
  ...inventoryCreateTypeChipStyle,
  background: '#eef4ff',
  border: '1px solid #6b8cff',
  color: '#2f54eb',
};

const inventoryCreateFieldStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 6,
};

const inventoryCreateFieldLabelStyle: React.CSSProperties = {
  color: '#6b5f4f',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.04em',
  textTransform: 'uppercase',
};

const inventoryCreateInputStyle: React.CSSProperties = {
  background: 'rgba(255, 252, 246, 0.98)',
  border: '1px solid #e5dccb',
  borderRadius: 10,
  color: '#1f2937',
  fontSize: 13,
  minWidth: 0,
  outline: 'none',
  padding: '10px 12px',
  width: '100%',
};

const inventoryCreateHintStyle: React.CSSProperties = {
  color: '#7b6e5a',
  fontSize: 11.5,
  lineHeight: '18px',
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
      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
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

function findWorkflowSummaryByLookupValue(
  workflows: ReadonlyArray<{
    readonly workflowId: string;
    readonly name: string;
    readonly fileName: string;
    readonly description?: string;
  }>,
  lookupValue: string | null | undefined,
) {
  const normalizedLookupValue = normalizeComparableText(lookupValue);
  if (!normalizedLookupValue) {
    return null;
  }

  return (
    workflows.find((workflow) => {
      const fileStem = workflow.fileName.replace(/\.(ya?ml)$/i, '');
      return (
        normalizeComparableText(workflow.workflowId) === normalizedLookupValue ||
        normalizeComparableText(workflow.name) === normalizedLookupValue ||
        normalizeComparableText(fileStem) === normalizedLookupValue
      );
    }) ?? null
  );
}

function buildWorkflowMemberKeyFromSummary(input?: {
  readonly workflowId?: string | null;
  readonly name?: string | null;
  readonly fileName?: string | null;
} | null): `workflow:${string}` | '' {
  const memberKey = buildStudioWorkflowMemberKey({
      workflowId: trimOptional(input?.workflowId),
      workflowName: trimOptional(input?.name),
      fileName: trimOptional(input?.fileName),
    });
  return memberKey?.startsWith('workflow:')
    ? (memberKey as `workflow:${string}`)
    : '';
}

function resolveWorkflowIdFromRouteValue(
  routeValue: string | null | undefined,
  workflows: ReadonlyArray<{
    readonly workflowId: string;
    readonly name: string;
    readonly fileName: string;
    readonly description?: string;
  }>,
  options?: {
    readonly allowDirectIdFallback?: boolean;
    readonly workflowFile?: Pick<StudioWorkflowFile, 'workflowId' | 'name' | 'fileName'> | null;
  },
): string {
  const normalizedRouteValue = trimOptional(routeValue);
  if (!normalizedRouteValue) {
    return '';
  }

  const matchedWorkflow = findWorkflowSummaryByLookupValue(
    workflows,
    normalizedRouteValue,
  );
  if (matchedWorkflow) {
    return trimOptional(matchedWorkflow.workflowId);
  }

  const workflowFile = options?.workflowFile;
  const fileRouteValue = resolveStudioWorkflowMemberRouteValue({
    workflowId: workflowFile?.workflowId,
    workflowName: workflowFile?.name,
    fileName: workflowFile?.fileName,
  });
  if (
    fileRouteValue &&
    normalizeComparableText(fileRouteValue) ===
      normalizeComparableText(normalizedRouteValue)
  ) {
    return trimOptional(workflowFile?.workflowId);
  }

  return options?.allowDirectIdFallback ? normalizedRouteValue : '';
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

function parseStudioRouteMember(
  value: string | null | undefined,
): StudioRouteMemberState {
  const normalizedValue = trimOptional(value);
  if (normalizedValue.startsWith('workflow:')) {
    const workflowRouteValue = readWorkflowMemberRouteValueFromMemberKey(
      normalizedValue,
    );
    return workflowRouteValue
      ? {
          key: `workflow:${workflowRouteValue}`,
          kind: 'workflow',
          value: workflowRouteValue,
          memberId: '',
          serviceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '' };
  }

  if (normalizedValue.startsWith('script:')) {
    const scriptId = readScriptIdFromMemberKey(normalizedValue);
    return scriptId
      ? {
          key: `script:${scriptId}`,
          kind: 'script',
          value: scriptId,
          memberId: '',
          serviceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '' };
  }

  if (normalizedValue.startsWith('member:')) {
    const memberId = readMemberIdFromMemberKey(normalizedValue);
    return memberId
      ? {
          key: `member:${memberId}`,
          kind: 'member',
          value: memberId,
          memberId,
          serviceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '' };
  }

  return {
    key: '',
    kind: 'none',
    value: '',
    memberId: '',
    serviceId: '',
  };
}

function readStudioBuildFocusFromParams(
  params: URLSearchParams,
): StudioBuildFocusState {
  return parseStudioBuildFocus(params.get('focus'));
}

function readStudioRouteMemberFromParams(
  params: URLSearchParams,
): StudioRouteMemberState {
  const explicitMember = parseStudioRouteMember(params.get('member'));
  if (explicitMember.key) {
    return explicitMember;
  }

  const legacyMemberId = trimOptional(params.get('memberId'));
  return legacyMemberId
    ? parseStudioRouteMember(`member:${legacyMemberId}`)
    : { key: '', kind: 'none', value: '', memberId: '', serviceId: '' };
}

function buildStudioBuildFocusKey(input: {
  buildSurface: BuildSurface;
  selectedWorkflowMemberKey?: string;
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

  const workflowMemberKey = trimOptional(input.selectedWorkflowMemberKey);
  if (workflowMemberKey.startsWith('workflow:')) {
    return workflowMemberKey as StudioBuildFocus;
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

function buildInventoryWorkflowName(
  workflows: readonly { name: string }[],
  baseName = 'draft',
): string {
  const normalizedBaseName = trimOptional(baseName) || 'draft';
  const names = new Set(
    workflows
      .map((workflow) => trimOptional(workflow.name)?.toLowerCase())
      .filter(Boolean),
  );

  if (!names.has(normalizedBaseName.toLowerCase())) {
    return normalizedBaseName;
  }

  let nextIndex = 2;
  while (names.has(`${normalizedBaseName}-${nextIndex}`.toLowerCase())) {
    nextIndex += 1;
  }

  return `${normalizedBaseName}-${nextIndex}`;
}

function buildWorkflowFileName(workflowName: string): string {
  const normalizedWorkflowName = trimOptional(workflowName) || 'workflow';
  return `${normalizedWorkflowName}.yaml`;
}

function parseStudioIntent(value: string | null | undefined): StudioIntent | '' {
  return trimOptional(value) === 'create-member' ? 'create-member' : '';
}

function readWorkflowMemberRouteValueFromMemberKey(memberKey: string): string {
  const normalizedMemberKey = trimOptional(memberKey);
  if (!normalizedMemberKey.startsWith('workflow:')) {
    return '';
  }

  return trimOptional(normalizedMemberKey.slice('workflow:'.length));
}

function readMemberIdFromMemberKey(memberKey: string): string {
  const normalizedMemberKey = trimOptional(memberKey);
  if (!normalizedMemberKey.startsWith('member:')) {
    return '';
  }

  return trimOptional(normalizedMemberKey.slice('member:'.length));
}

function readScriptIdFromMemberKey(memberKey: string): string {
  const normalizedMemberKey = trimOptional(memberKey);
  if (!normalizedMemberKey.startsWith('script:')) {
    return '';
  }

  return trimOptional(normalizedMemberKey.slice('script:'.length));
}

function resolveServiceMemberTone(
  deploymentStatus: string | null | undefined,
): 'live' | 'draft' | 'idle' {
  const normalizedStatus = trimOptional(deploymentStatus).toLowerCase();
  if (
    normalizedStatus === 'active' ||
    normalizedStatus === 'live' ||
    normalizedStatus === 'serving' ||
    normalizedStatus === 'ready'
  ) {
    return 'live';
  }

  if (
    normalizedStatus === 'draft' ||
    normalizedStatus === 'pending' ||
    normalizedStatus === 'preparing'
  ) {
    return 'draft';
  }

  return 'idle';
}

function readStudioRouteState(search?: string): StudioRouteState {
  if (typeof window === 'undefined' && typeof search !== 'string') {
    return {
      scopeId: '',
      memberKey: '',
      memberId: '',
      step: 'build',
      focusKey: '',
      tab: 'workflows',
      intent: '',
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
  const routeMember = readStudioRouteMemberFromParams(params);
  return {
    scopeId: trimOptional(params.get('scopeId')),
    memberKey: routeMember.key,
    memberId: routeMember.memberId,
    step: parseStudioStep(params.get('step')),
    focusKey: buildFocus.key,
    tab: parseStudioTab(params.get('tab')),
    intent: parseStudioIntent(params.get('intent')),
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

function normalizeObserveRunStatus(status: string | null | undefined): string {
  const normalizedStatus = trimOptional(status).toLowerCase();
  if (!normalizedStatus) {
    return 'pending';
  }

  if (
    normalizedStatus.includes('wait') ||
    normalizedStatus.includes('running') ||
    normalizedStatus.includes('approval') ||
    normalizedStatus.includes('input') ||
    normalizedStatus.includes('signal') ||
    normalizedStatus.includes('progress')
  ) {
    return 'running';
  }

  if (
    normalizedStatus.includes('complete') ||
    normalizedStatus.includes('success')
  ) {
    return 'completed';
  }

  if (
    normalizedStatus.includes('fail') ||
    normalizedStatus.includes('error') ||
    normalizedStatus.includes('timeout')
  ) {
    return 'failed';
  }

  if (
    normalizedStatus.includes('stop') ||
    normalizedStatus.includes('cancel')
  ) {
    return 'stopped';
  }

  return normalizedStatus;
}

function isObserveRunTerminal(status: string | null | undefined): boolean {
  return ['completed', 'failed', 'stopped', 'cancelled', 'canceled'].includes(
    normalizeObserveRunStatus(status),
  );
}

function readObserveRunStartedAt(
  run: Pick<
    ScopeServiceRunSummary,
    'lastUpdatedAt' | 'bindingUpdatedAt' | 'boundAt'
  >,
): string {
  return (
    trimOptional(run.boundAt) ||
    trimOptional(run.bindingUpdatedAt) ||
    trimOptional(run.lastUpdatedAt) ||
    ''
  );
}

function readObserveStepInputPreview(step: ScopeServiceRunAuditStep): string {
  return (
    trimOptional(step.suspensionPrompt) ||
    trimOptional(step.requestParameters.prompt) ||
    trimOptional(step.requestParameters.input) ||
    trimOptional(step.requestParameters.signalName) ||
    trimOptional(step.requestParameters.signal_name) ||
    trimOptional(step.requestedVariableName) ||
    trimOptional(step.assignedValue) ||
    ''
  );
}

function readObserveSignalName(step: ScopeServiceRunAuditStep): string {
  return (
    trimOptional(step.requestParameters.signalName) ||
    trimOptional(step.requestParameters.signal_name) ||
    trimOptional(step.requestedVariableName) ||
    trimOptional(step.assignedVariable) ||
    'continue'
  );
}

function buildObserveFrame(
  receivedAtUtc: string,
  payload: Record<string, unknown>,
): { receivedAtUtc: string; payload: string } {
  return {
    receivedAtUtc,
    payload: JSON.stringify(payload),
  };
}

function buildObserveExecutionFrames(
  snapshot: ScopeServiceRunAuditSnapshot,
): StudioExecutionDetail['frames'] {
  const startedAt =
    trimOptional(snapshot.audit.startedAt) ||
    readObserveRunStartedAt(snapshot.summary) ||
    new Date().toISOString();
  const runId = trimOptional(snapshot.summary.runId);
  const frames: Array<{ receivedAtUtc: string; payload: string }> = [
    buildObserveFrame(startedAt, {
      custom: {
        name: 'aevatar.run.context',
        payload: {
          workflowName:
            trimOptional(snapshot.audit.workflowName) ||
            trimOptional(snapshot.summary.workflowName),
        },
      },
    }),
  ];

  const steps = [...snapshot.audit.steps].sort((left, right) => {
    const leftTimestamp =
      Date.parse(trimOptional(left.requestedAt) || trimOptional(left.completedAt) || '') || 0;
    const rightTimestamp =
      Date.parse(trimOptional(right.requestedAt) || trimOptional(right.completedAt) || '') || 0;
    return leftTimestamp - rightTimestamp;
  });

  for (const step of steps) {
    const requestedAt =
      trimOptional(step.requestedAt) ||
      trimOptional(step.completedAt) ||
      startedAt;
    frames.push(
      buildObserveFrame(requestedAt, {
        custom: {
          name: 'aevatar.step.request',
          payload: {
            stepId: step.stepId,
            stepType: step.stepType,
            targetRole: step.targetRole,
            input: readObserveStepInputPreview(step),
          },
        },
      }),
    );

    const suspensionType = trimOptional(step.suspensionType).toLowerCase();
    if (suspensionType) {
      frames.push(
        buildObserveFrame(requestedAt, {
          custom: {
            name:
              suspensionType === 'wait_signal'
                ? 'aevatar.wait_signal.request'
                : 'aevatar.human_input.request',
            payload: {
              runId,
              stepId: step.stepId,
              suspensionType,
              prompt: trimOptional(step.suspensionPrompt),
              timeoutSeconds: step.suspensionTimeoutSeconds,
              variableName: trimOptional(step.requestedVariableName),
              signalName:
                suspensionType === 'wait_signal'
                  ? readObserveSignalName(step)
                  : '',
            },
          },
        }),
      );
    }

    if (trimOptional(step.completedAt) || step.success !== null || trimOptional(step.error)) {
      const completedAt = trimOptional(step.completedAt) || requestedAt;
      if (suspensionType) {
        frames.push(
          buildObserveFrame(completedAt, {
            custom: {
              name: 'studio.human.resume',
              payload: {
                stepId: step.stepId,
                suspensionType,
                approved: suspensionType === 'human_approval' ? step.success !== false : true,
                userInput:
                  trimOptional(step.assignedValue) ||
                  trimOptional(step.outputPreview) ||
                  '',
                signalName:
                  suspensionType === 'wait_signal'
                    ? readObserveSignalName(step)
                    : '',
              },
            },
          }),
        );
      }

      frames.push(
        buildObserveFrame(completedAt, {
          custom: {
            name: 'aevatar.step.completed',
            payload: {
              stepId: step.stepId,
              success: step.success !== false,
              error: trimOptional(step.error),
              output: trimOptional(step.outputPreview),
              nextStepId: trimOptional(step.nextStepId),
              branchKey: trimOptional(step.branchKey),
            },
          },
        }),
      );
    }
  }

  const terminalTimestamp =
    trimOptional(snapshot.audit.endedAt) ||
    trimOptional(snapshot.audit.updatedAt) ||
    trimOptional(snapshot.summary.lastUpdatedAt) ||
    startedAt;
  if (trimOptional(snapshot.audit.finalError)) {
    frames.push(
      buildObserveFrame(terminalTimestamp, {
        runError: {
          code: trimOptional(snapshot.audit.completionStatus),
          message: snapshot.audit.finalError,
        },
      }),
    );
  } else if (normalizeObserveRunStatus(snapshot.audit.completionStatus) === 'stopped') {
    frames.push(
      buildObserveFrame(terminalTimestamp, {
        runStopped: {
          reason:
            trimOptional(snapshot.audit.finalError) ||
            trimOptional(snapshot.summary.lastError) ||
            '',
        },
      }),
    );
  } else if (isObserveRunTerminal(snapshot.audit.completionStatus)) {
    frames.push(
      buildObserveFrame(terminalTimestamp, {
        runFinished: {
          output: trimOptional(snapshot.audit.finalOutput),
        },
      }),
    );
  }

  const timelineEvents = [...snapshot.audit.timeline]
    .filter(
      (event) =>
        Boolean(trimOptional(event.message)) &&
        !steps.some(
          (step) =>
            trimOptional(step.stepId) === trimOptional(event.stepId) &&
            trimOptional(step.requestedAt) === trimOptional(event.timestamp),
        ),
    )
    .sort((left, right) => {
      const leftTimestamp = Date.parse(trimOptional(left.timestamp) || '') || 0;
      const rightTimestamp = Date.parse(trimOptional(right.timestamp) || '') || 0;
      return leftTimestamp - rightTimestamp;
    });
  for (const event of timelineEvents) {
    frames.push(
      buildObserveFrame(trimOptional(event.timestamp) || terminalTimestamp, {
        custom: {
          name: 'aevatar.step.completed',
          payload: {
            stepId: trimOptional(event.stepId) || trimOptional(event.stage),
            success:
              !trimOptional(event.stage).toLowerCase().includes('error') &&
              !trimOptional(event.eventType).toLowerCase().includes('error'),
            error:
              trimOptional(event.stage).toLowerCase().includes('error') ||
              trimOptional(event.eventType).toLowerCase().includes('error')
                ? trimOptional(event.message)
                : '',
            output: trimOptional(event.message),
            nextStepId: '',
            branchKey: '',
          },
        },
      }),
    );
  }

  return frames.sort((left, right) => {
    const leftTimestamp = Date.parse(left.receivedAtUtc) || 0;
    const rightTimestamp = Date.parse(right.receivedAtUtc) || 0;
    return leftTimestamp - rightTimestamp;
  });
}

function formatObserveRuntimeEventTimestamp(
  value: unknown,
  fallbackTimestamp: string,
): string {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return new Date(value).toISOString();
  }

  if (typeof value === 'string' && value.trim()) {
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? new Date(parsed).toISOString() : value;
  }

  return fallbackTimestamp;
}

function buildObserveExecutionFramesFromRuntimeEvents(input: {
  events: readonly RuntimeEvent[];
  fallbackTimestamp: string;
}): StudioExecutionDetail['frames'] {
  const frames = input.events.flatMap((event) => {
    const receivedAtUtc = formatObserveRuntimeEventTimestamp(
      (event as { readonly timestamp?: unknown }).timestamp,
      input.fallbackTimestamp,
    );

    if (event.type === AGUIEventType.CUSTOM) {
      const customName = trimOptional(
        String((event as { readonly name?: unknown }).name || ''),
      );
      if (!customName) {
        return [];
      }

      return [
        buildObserveFrame(receivedAtUtc, {
          custom: {
            name: customName,
            payload:
              (event as { readonly payload?: unknown }).payload ??
              (event as { readonly value?: unknown }).value ??
              {},
          },
        }),
      ];
    }

    if (event.type === AGUIEventType.RUN_FINISHED) {
      return [
        buildObserveFrame(receivedAtUtc, {
          runFinished: {
            output: extractRunFinishedOutput(event) || '',
          },
        }),
      ];
    }

    if (event.type === AGUIEventType.RUN_ERROR) {
      return [
        buildObserveFrame(receivedAtUtc, {
          runError: {
            code: trimOptional(
              String((event as { readonly code?: unknown }).code || ''),
            ),
            message: trimOptional(
              String((event as { readonly message?: unknown }).message || ''),
            ),
          },
        }),
      ];
    }

    if ((event as { readonly type?: string }).type === 'RUN_STOPPED') {
      return [
        buildObserveFrame(receivedAtUtc, {
          runStopped: {
            reason: trimOptional(
              String((event as { readonly reason?: unknown }).reason || ''),
            ),
          },
        }),
      ];
    }

    return [];
  });

  return frames.sort((left, right) => {
    const leftTimestamp = Date.parse(left.receivedAtUtc) || 0;
    const rightTimestamp = Date.parse(right.receivedAtUtc) || 0;
    return leftTimestamp - rightTimestamp;
  });
}

function normalizeObserveInvokeSessionStatus(value: string): string {
  const normalizedValue = trimOptional(value).toLowerCase();
  if (normalizedValue === 'success') {
    return 'completed';
  }

  if (normalizedValue === 'error') {
    return 'failed';
  }

  if (normalizedValue === 'idle') {
    return 'pending';
  }

  return normalizedValue || 'running';
}

function toObserveExecutionFromSessionSeed(
  seed: StudioObserveSessionSeed,
  options?: {
    workflowName?: string | null;
  },
): StudioExecutionDetail {
  const fallbackTimestamp =
    trimOptional(seed.startedAtUtc) ||
    trimOptional(seed.completedAtUtc) ||
    new Date().toISOString();
  const runtimeAccumulator = createRuntimeEventAccumulator({
    actorId: trimOptional(seed.actorId) || undefined,
  });
  seed.events.forEach((event) => {
    applyRuntimeEvent(runtimeAccumulator, event);
  });
  const lastEventTimestamp = [...seed.events]
    .reverse()
    .map((event) =>
      formatObserveRuntimeEventTimestamp(
        (event as { readonly timestamp?: unknown }).timestamp,
        fallbackTimestamp,
      ),
    )
    .find(Boolean);
  const status = normalizeObserveInvokeSessionStatus(seed.status);
  const startedAtUtc = trimOptional(seed.startedAtUtc) || fallbackTimestamp;
  const completedAtUtc =
    status === 'running'
      ? null
      : trimOptional(seed.completedAtUtc) || lastEventTimestamp || startedAtUtc;
  const updatedAtUtc =
    trimOptional(seed.completedAtUtc) || lastEventTimestamp || startedAtUtc;
  const workflowName =
    trimOptional(options?.workflowName) ||
    trimOptional(seed.serviceLabel) ||
    trimOptional(seed.serviceId) ||
    'member';
  const completedSteps = runtimeAccumulator.steps.filter(
    (step) => step.status === 'done',
  ).length;

  return {
    executionId:
      trimOptional(seed.runId) ||
      `invoke-session:${trimOptional(seed.serviceId)}:${startedAtUtc}`,
    workflowName,
    prompt: trimOptional(seed.prompt),
    status,
    startedAtUtc,
    completedAtUtc,
    actorId:
      trimOptional(seed.actorId) ||
      trimOptional(runtimeAccumulator.actorId) ||
      null,
    error:
      trimOptional(seed.error) ||
      trimOptional(runtimeAccumulator.errorText) ||
      null,
    serviceId: trimOptional(seed.serviceId) || null,
    revisionId: null,
    definitionActorId: null,
    stateVersion: null,
    lastEventId: null,
    updatedAtUtc,
    totalSteps:
      runtimeAccumulator.steps.length > 0
        ? runtimeAccumulator.steps.length
        : null,
    completedSteps:
      runtimeAccumulator.steps.length > 0 ? completedSteps : null,
    roleReplyCount: null,
    output:
      trimOptional(seed.finalOutput) ||
      trimOptional(runtimeAccumulator.finalOutput) ||
      trimOptional(seed.assistantText) ||
      null,
    auditUpdatedAtUtc: updatedAtUtc,
    auditSource: 'invoke-session',
    frames: buildObserveExecutionFramesFromRuntimeEvents({
      events: seed.events,
      fallbackTimestamp: startedAtUtc,
    }),
  };
}

function toObserveExecutionSummary(
  run: ScopeServiceRunSummary,
): StudioExecutionSummary {
  const startedAtUtc = readObserveRunStartedAt(run);
  return {
    executionId: run.runId,
    workflowName: trimOptional(run.workflowName) || trimOptional(run.serviceId),
    prompt: '',
    status: normalizeObserveRunStatus(run.completionStatus),
    startedAtUtc,
    completedAtUtc: isObserveRunTerminal(run.completionStatus)
      ? trimOptional(run.lastUpdatedAt) || startedAtUtc || null
      : null,
    actorId: trimOptional(run.actorId) || null,
    error: trimOptional(run.lastError) || null,
    serviceId: trimOptional(run.serviceId) || null,
    revisionId: trimOptional(run.revisionId) || null,
    definitionActorId: trimOptional(run.definitionActorId) || null,
    stateVersion:
      typeof run.stateVersion === 'number' ? run.stateVersion : null,
    lastEventId: trimOptional(run.lastEventId) || null,
    updatedAtUtc: trimOptional(run.lastUpdatedAt) || null,
    totalSteps: typeof run.totalSteps === 'number' ? run.totalSteps : null,
    completedSteps:
      typeof run.completedSteps === 'number' ? run.completedSteps : null,
    roleReplyCount:
      typeof run.roleReplyCount === 'number' ? run.roleReplyCount : null,
    output: trimOptional(run.lastOutput) || null,
    auditUpdatedAtUtc: null,
    auditSource: 'service-run-summary',
  };
}

function toObserveExecutionDetail(
  snapshot: ScopeServiceRunAuditSnapshot,
): StudioExecutionDetail {
  const startedAtUtc =
    trimOptional(snapshot.audit.startedAt) ||
    readObserveRunStartedAt(snapshot.summary);
  const completedAtUtc = isObserveRunTerminal(snapshot.audit.completionStatus)
    ? trimOptional(snapshot.audit.endedAt) ||
      trimOptional(snapshot.audit.updatedAt) ||
      trimOptional(snapshot.summary.lastUpdatedAt) ||
      null
    : null;
  return {
    executionId: snapshot.summary.runId,
    workflowName:
      trimOptional(snapshot.audit.workflowName) ||
      trimOptional(snapshot.summary.workflowName),
    prompt: trimOptional(snapshot.audit.input),
    status: normalizeObserveRunStatus(snapshot.audit.completionStatus),
    startedAtUtc,
    completedAtUtc,
    actorId:
      trimOptional(snapshot.audit.rootActorId) ||
      trimOptional(snapshot.summary.actorId) ||
      null,
    error:
      trimOptional(snapshot.audit.finalError) ||
      trimOptional(snapshot.summary.lastError) ||
      null,
    serviceId: trimOptional(snapshot.summary.serviceId) || null,
    revisionId: trimOptional(snapshot.summary.revisionId) || null,
    definitionActorId:
      trimOptional(snapshot.summary.definitionActorId) || null,
    stateVersion:
      typeof snapshot.audit.stateVersion === 'number'
        ? snapshot.audit.stateVersion
        : typeof snapshot.summary.stateVersion === 'number'
          ? snapshot.summary.stateVersion
          : null,
    lastEventId:
      trimOptional(snapshot.audit.lastEventId) ||
      trimOptional(snapshot.summary.lastEventId) ||
      null,
    updatedAtUtc:
      trimOptional(snapshot.summary.lastUpdatedAt) ||
      trimOptional(snapshot.audit.updatedAt) ||
      null,
    totalSteps:
      typeof snapshot.audit.summary.totalSteps === 'number'
        ? snapshot.audit.summary.totalSteps
        : typeof snapshot.summary.totalSteps === 'number'
          ? snapshot.summary.totalSteps
          : null,
    completedSteps:
      typeof snapshot.audit.summary.completedSteps === 'number'
        ? snapshot.audit.summary.completedSteps
        : typeof snapshot.summary.completedSteps === 'number'
          ? snapshot.summary.completedSteps
          : null,
    roleReplyCount:
      typeof snapshot.audit.summary.roleReplyCount === 'number'
        ? snapshot.audit.summary.roleReplyCount
        : typeof snapshot.summary.roleReplyCount === 'number'
          ? snapshot.summary.roleReplyCount
          : null,
    output:
      trimOptional(snapshot.audit.finalOutput) ||
      trimOptional(snapshot.summary.lastOutput) ||
      null,
    auditUpdatedAtUtc:
      trimOptional(snapshot.audit.updatedAt) ||
      trimOptional(snapshot.summary.lastUpdatedAt) ||
      null,
    auditSource: 'run-audit',
    frames: buildObserveExecutionFrames(snapshot),
  };
}

function buildStudioFocusKey(input: {
  activeBuildFocusKey?: string;
  routeMemberKey?: string;
  routeMemberId?: string;
}): string {
  const activeBuildFocusKey = trimOptional(input.activeBuildFocusKey);
  if (activeBuildFocusKey) {
    return activeBuildFocusKey;
  }

  const routeMemberKey = parseStudioRouteMember(input.routeMemberKey).key;
  if (routeMemberKey) {
    return routeMemberKey;
  }

  const routeMemberId = trimOptional(input.routeMemberId);
  if (routeMemberId) {
    return `member:${routeMemberId}`;
  }

  return '';
}

type PublishedStudioMemberRecord = {
  readonly memberSummary?: StudioMemberSummary | null;
  readonly service: {
    readonly serviceId?: string | null;
  };
  readonly matchedWorkflow?: {
    readonly workflowId?: string | null;
  } | null;
  readonly matchedScript?: {
    readonly script?: {
      readonly scriptId?: string | null;
    } | null;
  } | null;
};

function resolveStudioMemberSummaryFromMemberKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): StudioMemberSummary | null {
  const parsedMember = parseStudioRouteMember(memberKey);
  if (parsedMember.kind === 'member') {
    const directMemberMatch =
      studioScopeMembers.find(
        (member) => trimOptional(member.memberId) === parsedMember.memberId,
      ) ?? null;
    if (directMemberMatch) {
      return directMemberMatch;
    }

    const legacyPublishedServiceMatch =
      studioScopeMembers.find(
        (member) =>
          trimOptional(member.publishedServiceId) === parsedMember.memberId,
      ) ?? null;
    if (legacyPublishedServiceMatch) {
      return legacyPublishedServiceMatch;
    }

    return (
      publishedMembers.find(
        ({ service, memberSummary }) =>
          trimOptional(memberSummary?.memberId) === parsedMember.memberId ||
          trimOptional(memberSummary?.publishedServiceId) === parsedMember.memberId ||
          trimOptional(service.serviceId) === parsedMember.memberId,
      )?.memberSummary ?? null
    );
  }

  const workflowRouteValue = readWorkflowMemberRouteValueFromMemberKey(memberKey);
  if (workflowRouteValue) {
    return (
      publishedMembers.find(
        ({ matchedWorkflow }) =>
          buildWorkflowMemberKeyFromSummary(matchedWorkflow) ===
          `workflow:${workflowRouteValue}`,
      )?.memberSummary ?? null
    );
  }

  const scriptId = readScriptIdFromMemberKey(memberKey);
  if (scriptId) {
    return (
      publishedMembers.find(
        ({ matchedScript }) =>
          trimOptional(matchedScript?.script?.scriptId) === scriptId,
      )?.memberSummary ?? null
    );
  }

  return null;
}

function resolvePublishedMemberIdFromServiceId(
  serviceId: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const normalizedServiceId = trimOptional(serviceId);
  if (!normalizedServiceId) {
    return '';
  }

  const directRosterMatch =
    studioScopeMembers.find(
      (member) => trimOptional(member.publishedServiceId) === normalizedServiceId,
    ) ?? null;
  if (directRosterMatch) {
    return trimOptional(directRosterMatch.memberId);
  }

  return trimOptional(
    publishedMembers.find(
      ({ memberSummary, service }) =>
        trimOptional(memberSummary?.publishedServiceId) === normalizedServiceId ||
        trimOptional(service.serviceId) === normalizedServiceId,
    )?.memberSummary?.memberId,
  );
}

function resolveStudioServiceDefaultEndpointId(
  service:
    | {
        readonly endpoints?:
          | readonly {
              readonly endpointId: string;
            }[]
          | null;
      }
    | null
    | undefined,
): string {
  if (!service?.endpoints?.length) {
    return '';
  }

  return (
    service.endpoints.find((endpoint) => endpoint.endpointId === 'chat')
      ?.endpointId ||
    service.endpoints[0]?.endpointId ||
    ''
  );
}

function resolvePublishedServiceIdFromMemberKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const memberSummary = resolveStudioMemberSummaryFromMemberKey(
    memberKey,
    publishedMembers,
    studioScopeMembers,
  );
  const resolvedPublishedServiceId = trimOptional(memberSummary?.publishedServiceId);
  if (resolvedPublishedServiceId) {
    return resolvedPublishedServiceId;
  }

  const legacyMemberToken = readMemberIdFromMemberKey(memberKey);
  if (legacyMemberToken) {
    return (
      trimOptional(
        publishedMembers.find(
          ({ service }) => trimOptional(service.serviceId) === legacyMemberToken,
        )?.service.serviceId,
      ) || legacyMemberToken
    );
  }

  const workflowRouteValue = readWorkflowMemberRouteValueFromMemberKey(memberKey);
  if (workflowRouteValue) {
    return trimOptional(
      publishedMembers.find(
        ({ matchedWorkflow }) =>
          buildWorkflowMemberKeyFromSummary(matchedWorkflow) ===
          `workflow:${workflowRouteValue}`,
      )?.service.serviceId,
    );
  }

  const scriptId = readScriptIdFromMemberKey(memberKey);
  if (scriptId) {
    return trimOptional(
      publishedMembers.find(
        ({ matchedScript }) =>
          trimOptional(matchedScript?.script?.scriptId) === scriptId,
      )?.service.serviceId,
    );
  }

  return '';
}

function resolveStudioMemberOwnerKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const parsedMember = parseStudioRouteMember(memberKey);
  if (parsedMember.kind !== 'member') {
    return parsedMember.key;
  }

  const matchedMemberSummary = resolveStudioMemberSummaryFromMemberKey(
    memberKey,
    publishedMembers,
    studioScopeMembers,
  );
  if (matchedMemberSummary) {
    return `member:${trimOptional(matchedMemberSummary.memberId)}`;
  }

  const matchedPublishedMember = publishedMembers.find(
    ({ service }) =>
      trimOptional(service.serviceId) === parsedMember.memberId ||
      trimOptional(service.serviceId) === parsedMember.serviceId,
  );
  const matchedWorkflowId = trimOptional(
    buildWorkflowMemberKeyFromSummary(matchedPublishedMember?.matchedWorkflow),
  );
  if (matchedWorkflowId) {
    return matchedWorkflowId;
  }

  const matchedScriptId = trimOptional(
    matchedPublishedMember?.matchedScript?.script?.scriptId,
  );
  if (matchedScriptId) {
    return `script:${matchedScriptId}`;
  }

  return parsedMember.key;
}

function resolveBoundServiceIdFromCatalog(input: {
  services: readonly Pick<ServiceCatalogSnapshot, 'serviceId' | 'displayName'>[];
  candidates: Array<string | null | undefined>;
}): string {
  const candidateValues = Array.from(
    new Set(
      input.candidates
        .map((candidate) => trimOptional(candidate))
        .filter(Boolean),
    ),
  );
  if (candidateValues.length === 0) {
    return '';
  }

  const matchedServiceIds = Array.from(
    new Set(
      input.services.flatMap((service) => {
        const serviceId = trimOptional(service.serviceId);
        const displayName = trimOptional(service.displayName);
        return candidateValues.some(
          (candidate) => candidate === serviceId || candidate === displayName,
        )
          ? [serviceId]
          : [];
      }),
    ),
  );

  return matchedServiceIds.length === 1 ? matchedServiceIds[0] : '';
}

function formatStudioAssetMeta(input: {
  primary?: string | null;
  secondary?: string | null;
}): string {
  return [trimOptional(input.primary), trimOptional(input.secondary)]
    .filter(Boolean)
    .join(' · ');
}

function describeMemberImplementationLabel(
  kind: string | null | undefined,
): string {
  switch (normalizeStudioMemberBindingImplementationKind(kind)) {
    case 'workflow':
      return 'Workflow implementation';
    case 'script':
      return 'Script implementation';
    case 'gagent':
      return 'GAgent implementation';
    default:
      return 'Member implementation';
  }
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
  const routeSelectedMember = useMemo(
    () => parseStudioRouteMember(routeState.memberKey),
    [routeState.memberKey],
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
  const initialRouteState = readStudioRouteState();
  const initialBuildFocus = parseStudioBuildFocus(initialRouteState.focusKey);
  const initialSelectedMember = parseStudioRouteMember(initialRouteState.memberKey);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState('');
  const [selectedScriptId, setSelectedScriptId] = useState(
    () =>
      initialBuildFocus.kind === 'script'
        ? initialBuildFocus.value
        : initialSelectedMember.kind === 'script'
          ? initialSelectedMember.value
          : '',
  );
  const [selectedGAgentTypeName, setSelectedGAgentTypeName] = useState('');
  const [selectedExecutionId, setSelectedExecutionId] = useState(
    () => initialRouteState.executionId,
  );
  const [templateWorkflow, setTemplateWorkflow] = useState(
    () => (initialBuildFocus.kind === 'template' ? initialBuildFocus.value : ''),
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
  const [inventoryBusyKey, setInventoryBusyKey] = useState('');
  const [inventoryBusyAction, setInventoryBusyAction] = useState<InventoryBusyAction>('');
  const [memberRecencyOrder, setMemberRecencyOrder] = useState<string[]>([]);
  const [createMemberModalOpen, setCreateMemberModalOpen] = useState(false);
  const [createMemberKind, setCreateMemberKind] = useState<BuildMode>('workflow');
  const [createMemberName, setCreateMemberName] = useState('');
  const [createMemberDirectoryId, setCreateMemberDirectoryId] = useState('');
  const [runPrompt, setRunPrompt] = useState(() => readStudioRouteState().prompt);
  const [runPending, setRunPending] = useState(false);
  const [runNotice, setRunNotice] = useState<DraftRunNotice | null>(null);
  const [selectedGraphNodeId, setSelectedGraphNodeId] = useState('');
  const [executionStopPending, setExecutionStopPending] = useState(false);
  const [executionNotice, setExecutionNotice] = useState<StudioNotice | null>(null);
  const [logsPopoutMode] = useState(() => readStudioRouteState().logsMode);
  const [recentlyBoundMemberKey, setRecentlyBoundMemberKey] = useState('');
  const [recentlyBoundServiceId, setRecentlyBoundServiceId] = useState('');
  const [appliedRouteSnapshot, setAppliedRouteSnapshot] = useState(
    locationSnapshot,
  );
  const [pendingCreateMemberIntentSnapshot, setPendingCreateMemberIntentSnapshot] =
    useState(() =>
      readStudioRouteState().intent === 'create-member'
        ? getLocationSnapshot()
        : '',
    );
  const [promptHistory, setPromptHistory] = useState<
    PlaygroundPromptHistoryEntry[]
  >(() => loadPlaygroundPromptHistory());
  const [observeSessionSeedsByServiceId, setObserveSessionSeedsByServiceId] =
    useState<Record<string, StudioObserveSessionSeed>>({});
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
  const handledCreateMemberIntentSnapshotRef = useRef('');
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
    if (routeState.intent === 'create-member') {
      setPendingCreateMemberIntentSnapshot(locationSnapshot);
    }
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
      setSelectedScriptId('');
      setTemplateWorkflow('');
    } else if (routeBuildFocus.kind === 'script') {
      setSelectedScriptId((currentScriptId) =>
        trimOptional(currentScriptId) === routeBuildFocus.value
          ? currentScriptId
          : routeBuildFocus.value,
      );
    } else if (routeSelectedMember.kind === 'script') {
      setSelectedScriptId((currentScriptId) =>
        trimOptional(currentScriptId) === routeSelectedMember.value
          ? currentScriptId
          : routeSelectedMember.value,
      );
      setSelectedWorkflowId('');
      setTemplateWorkflow('');
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
    setRunPrompt((currentPrompt) =>
      currentPrompt === routeState.prompt ? currentPrompt : routeState.prompt,
    );
  }, [
    locationSnapshot,
    routeState.executionId,
    routeState.intent,
    routeSelectedMember.kind,
    routeSelectedMember.value,
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
  const studioMembersQuery = useQuery({
    queryKey: ['studio-scope-members', resolvedStudioScopeId],
    enabled: studioHostReady && Boolean(resolvedStudioScopeId),
    retry: false,
    queryFn: () => studioApi.listMembers(resolvedStudioScopeId),
  });
  const selectedWorkflowQuery = useQuery({
    queryKey: ['studio-workflow', workflowWorkspaceContextKey, selectedWorkflowId],
    enabled: studioHostReady && Boolean(selectedWorkflowId),
    queryFn: () => studioApi.getWorkflow(selectedWorkflowId, resolvedStudioScopeId),
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
  const suggestedCreateWorkflowName = useMemo(
    () => buildInventoryWorkflowName(visibleWorkflowSummaries),
    [visibleWorkflowSummaries],
  );
  useEffect(() => {
    if (
      (gAgentTypesQuery.data ?? []).some((descriptor) =>
        matchesRuntimeGAgentTypeDescriptor(selectedGAgentTypeName, descriptor),
      )
    ) {
      return;
    }

    const fallbackTypeName =
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
    gAgentTypesQuery.data,
    selectedGAgentTypeName,
  ]);
  const publishedScopeServices = useMemo(
    () => scopeServicesQuery.data ?? [],
    [scopeServicesQuery.data],
  );
  const studioScopeMembers = useMemo(
    () => studioMembersQuery.data?.members ?? [],
    [studioMembersQuery.data?.members],
  );
  const studioMemberByPublishedServiceId = useMemo(() => {
    const members = new Map<string, (typeof studioScopeMembers)[number]>();
    for (const member of studioScopeMembers) {
      const publishedServiceId = trimOptional(member.publishedServiceId);
      if (!publishedServiceId) {
        continue;
      }

      members.set(publishedServiceId, member);
    }

    return members;
  }, [studioScopeMembers]);
  const availableScopeScripts = useMemo(
    () =>
      (scopeScriptsQuery.data ?? []).filter(
        (detail): detail is ScopedScriptDetail =>
          Boolean(detail.available && detail.script),
      ),
    [scopeScriptsQuery.data],
  );
  const availableScopeScriptIds = useMemo(
    () =>
      new Set(
        availableScopeScripts
          .map((detail) => normalizeComparableText(detail.script?.scriptId))
          .filter(Boolean),
      ),
    [availableScopeScripts],
  );
  const publishedScopeServiceRevisionQueries = useQueries({
    queries: publishedScopeServices.map((service) => {
      const serviceId = trimOptional(service.serviceId);
      return {
        queryKey: [
          'studio-scope-service-revisions',
          resolvedStudioScopeId,
          serviceId,
        ],
        enabled:
          studioHostReady &&
          Boolean(resolvedStudioScopeId) &&
          Boolean(serviceId),
        queryFn: () =>
          scopeRuntimeApi.getServiceRevisions(resolvedStudioScopeId, serviceId),
      };
    }),
  });
  const currentServiceRevisionByServiceId = useMemo(() => {
    const revisions = new Map<string, ReturnType<typeof getScopeServiceCurrentRevision>>();

    publishedScopeServices.forEach((service, index) => {
      const serviceId = trimOptional(service.serviceId);
      if (!serviceId) {
        return;
      }

      const revision = getScopeServiceCurrentRevision(
        publishedScopeServiceRevisionQueries[index]?.data,
      );

      if (revision) {
        revisions.set(serviceId, revision);
      }
    });

    return revisions;
  }, [publishedScopeServiceRevisionQueries, publishedScopeServices]);
  const publishedScopeMembers = useMemo(() => {
    return publishedScopeServices.map((service) => {
      const serviceId = trimOptional(service.serviceId);
      const memberSummary = serviceId
        ? studioMemberByPublishedServiceId.get(serviceId) ?? null
        : null;
      const revision = serviceId
        ? currentServiceRevisionByServiceId.get(serviceId) ?? null
        : null;
      const revisionWorkflowName = trimOptional(revision?.workflowName);
      const matchedWorkflow =
        revision?.implementationKind === 'workflow' && revisionWorkflowName
          ? findWorkflowSummaryByLookupValue(
              visibleWorkflowSummaries,
              revisionWorkflowName,
            )
          : null;
      const matchedScriptId =
        revision?.implementationKind === 'script'
          ? trimOptional(revision.scriptId)
          : '';
      const matchedScript =
        matchedScriptId
          ? availableScopeScripts.find(
              (scriptDetail) =>
                trimOptional(scriptDetail.script?.scriptId) === matchedScriptId,
            ) ?? null
          : null;

      return {
        memberSummary,
        service,
        revision,
        matchedWorkflow,
        matchedScript,
      };
    });
  }, [
    availableScopeScripts,
    currentServiceRevisionByServiceId,
    publishedScopeServices,
    studioMemberByPublishedServiceId,
    visibleWorkflowSummaries,
  ]);
  const serviceBackedWorkflowIds = useMemo(
    () =>
      new Set(
        publishedScopeMembers.flatMap((item) =>
          item.matchedWorkflow?.workflowId
            ? [trimOptional(item.matchedWorkflow.workflowId)]
            : [],
        ),
      ),
    [publishedScopeMembers],
  );
  const serviceBackedScriptIds = useMemo(
    () =>
      new Set(
        publishedScopeMembers.flatMap((item) => {
          const scriptId = trimOptional(item.matchedScript?.script?.scriptId);
          return scriptId ? [scriptId] : [];
        }),
      ),
    [publishedScopeMembers],
  );
  const runtimeConsoleServices = useMemo(
    () =>
      resolvedStudioScopeId
        ? buildScopeConsoleServiceOptions(
            publishedScopeServices,
            undefined,
            {
              sortBy: 'serviceId',
            },
          )
        : [],
    [
      publishedScopeServices,
      resolvedStudioScopeId,
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
  useEffect(() => {
    if (!isStudioLocation) {
      return;
    }

    const routeWorkflowLookupValue =
      routeBuildFocus.kind === 'workflow'
        ? routeBuildFocus.value
        : routeSelectedMember.kind === 'workflow'
          ? routeSelectedMember.value
          : '';
    if (!routeWorkflowLookupValue) {
      return;
    }

    const resolvedWorkflowId = resolveWorkflowIdFromRouteValue(
      routeWorkflowLookupValue,
      visibleWorkflowSummaries,
      {
        allowDirectIdFallback: !workflowsQuery.isLoading,
      },
    );
    if (!resolvedWorkflowId) {
      return;
    }

    setSelectedWorkflowId((currentWorkflowId) =>
      trimOptional(currentWorkflowId) === resolvedWorkflowId
        ? currentWorkflowId
        : resolvedWorkflowId,
    );
    setSelectedScriptId('');
    setTemplateWorkflow('');
  }, [
    isStudioLocation,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    routeSelectedMember.kind,
    routeSelectedMember.value,
    visibleWorkflowSummaries,
    workflowsQuery.isLoading,
  ]);
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
  const selectedWorkflowRouteSummary = useMemo(
    () =>
      visibleWorkflowSummaries.find(
        (workflow) =>
          trimOptional(workflow.workflowId) === trimOptional(selectedWorkflowId),
      ) ?? null,
    [selectedWorkflowId, visibleWorkflowSummaries],
  );
  const selectedWorkflowMemberKey = useMemo(
    () =>
      buildWorkflowMemberKeyFromSummary({
        workflowId: selectedWorkflowId || activeWorkflowFile?.workflowId,
        name: activeWorkflowFile?.name || selectedWorkflowRouteSummary?.name,
        fileName:
          activeWorkflowFile?.fileName || selectedWorkflowRouteSummary?.fileName,
      }),
    [
      activeWorkflowFile?.fileName,
      activeWorkflowFile?.name,
      activeWorkflowFile?.workflowId,
      selectedWorkflowId,
      selectedWorkflowRouteSummary?.fileName,
      selectedWorkflowRouteSummary?.name,
    ],
  );
  const activeWorkflowSourceKey = selectedWorkflowId
    ? `workflow:${workflowWorkspaceContextKey}:${selectedWorkflowId}`
    : templateWorkflow
      ? `template:${templateWorkflow}`
      : '';
  const activeBuildFocusKey = useMemo(
    () =>
      buildStudioBuildFocusKey({
        buildSurface,
        selectedWorkflowMemberKey,
        selectedScriptId,
        templateWorkflow,
      }),
    [
      buildSurface,
      selectedScriptId,
      selectedWorkflowMemberKey,
      templateWorkflow,
    ],
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

    return '';
  }, [activeTemplate?.yaml, activeWorkflowFile?.yaml]);
  const sourceWorkflowName =
    activeWorkflowFile?.name ||
    activeTemplate?.catalog.name ||
    '';
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
      routeBuildFocus.kind === 'workflow' ||
      routeSelectedMember.kind === 'workflow' ||
      trimOptional(routeState.memberId)
    ) {
      return;
    }

    const preferredWorkflowId =
      visibleWorkflowSummaries[0]?.workflowId ||
      '';
    if (!preferredWorkflowId) {
      return;
    }

    setSelectedWorkflowId(preferredWorkflowId);
  }, [
    routeBuildFocus.kind,
    routeSelectedMember.kind,
    routeState.memberId,
    selectedWorkflowId,
    templateWorkflow,
    visibleWorkflowSummaries,
  ]);

  useEffect(() => {
    if (trimOptional(selectedWorkflowId).toLowerCase() !== 'default') {
      return;
    }

    const resolvedWorkflowId = trimOptional(activeWorkflowFile?.workflowId);
    if (resolvedWorkflowId && resolvedWorkflowId.toLowerCase() !== 'default') {
      setSelectedWorkflowId(resolvedWorkflowId);
      return;
    }

    if (!selectedWorkflowQuery.isError) {
      return;
    }

    if (
      visibleWorkflowSummaries.some(
        (workflow) => trimOptional(workflow.workflowId).toLowerCase() === 'default',
      )
    ) {
      return;
    }

    const fallbackWorkflowId = trimOptional(visibleWorkflowSummaries[0]?.workflowId);
    if (!fallbackWorkflowId) {
      return;
    }

    setSelectedWorkflowId(fallbackWorkflowId);
  }, [
    activeWorkflowFile?.workflowId,
    selectedWorkflowId,
    selectedWorkflowQuery.isError,
    visibleWorkflowSummaries,
  ]);

  const clearWorkflowBuildFocus = useCallback(() => {
    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    setDraftSourceKey('');
    setDraftYaml('');
    setDraftWorkflowName('');
    setDraftFileName('');
    setDraftWorkflowLayout(null);
    setEditableWorkflowDocument(null);
    setSelectedGraphNodeId('');
    setSaveNotice(null);
  }, []);

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
      setSaveNotice(null);
      return;
    }

    clearWorkflowBuildFocus();
  }, [
    clearWorkflowBuildFocus,
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
  }, [
    matchingWorkspaceWorkflow,
    selectedWorkflowId,
    templateWorkflow,
  ]);

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

  const activeWorkflowName = draftWorkflowName || sourceWorkflowName;
  const resolvedDraftDirectoryId = draftDirectoryId || defaultDirectoryId;
  const inventoryDirectoryId =
    resolvedDraftDirectoryId ||
    activeWorkflowFile?.directoryId ||
    visibleWorkflowSummaries[0]?.directoryId ||
    '';
  const activeDirectoryLabel =
    workspaceSettingsQuery.data?.directories.find(
      (item) => item.directoryId === resolvedDraftDirectoryId,
    )?.label ||
    activeWorkflowFile?.directoryLabel ||
    'No directory';
  const inventoryDirectoryOptions = useMemo(() => {
    const directories = workspaceSettingsQuery.data?.directories ?? [];
    if (
      inventoryDirectoryId &&
      !directories.some((item) => item.directoryId === inventoryDirectoryId)
    ) {
      return [
        {
          directoryId: inventoryDirectoryId,
          label: activeDirectoryLabel,
          path: '',
          isBuiltIn: false,
        },
        ...directories,
      ];
    }

    return directories;
  }, [
    activeDirectoryLabel,
    inventoryDirectoryId,
    workspaceSettingsQuery.data?.directories,
  ]);
  const selectedCreateDirectory = inventoryDirectoryOptions.find(
    (item) => item.directoryId === createMemberDirectoryId,
  );
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
  const effectiveSelectedGraphNodeId = useMemo(() => {
    const currentNodeId = trimOptional(selectedGraphNodeId);
    if (
      currentNodeId &&
      workflowGraph.nodes.some((node) => node.id === currentNodeId)
    ) {
      return currentNodeId;
    }

    const firstStepId = trimOptional(workflowGraph.steps[0]?.id);
    if (firstStepId) {
      return `step:${firstStepId}`;
    }

    return trimOptional(workflowGraph.nodes[0]?.id);
  }, [selectedGraphNodeId, workflowGraph.nodes, workflowGraph.steps]);
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

  useEffect(() => {
    if (trimOptional(selectedGraphNodeId) === effectiveSelectedGraphNodeId) {
      return;
    }

    setSelectedGraphNodeId(effectiveSelectedGraphNodeId);
  }, [effectiveSelectedGraphNodeId, selectedGraphNodeId]);

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
  const buildPendingBindCandidate = useMemo(() => {
    if (
      activeBuildMode !== 'workflow' ||
      !resolvedStudioScopeId ||
      !trimOptional(draftYaml)
    ) {
      return null;
    }

    const displayName =
      trimOptional(activeWorkflowName) ||
      trimOptional(draftWorkflowName) ||
      'draft';

    return {
      kind: 'workflow' as const,
      displayName,
      description:
        'Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member.',
      actionLabel: 'Bind current revision',
    };
  }, [
    activeBuildMode,
    activeWorkflowName,
    draftWorkflowName,
    draftYaml,
    resolvedStudioScopeId,
  ]);
  const buildPendingMemberSummary = useMemo(() => {
    if (buildPendingBindCandidate?.kind !== 'workflow') {
      return null;
    }

    const candidateWorkflowId = trimOptional(
      selectedWorkflowId || activeWorkflowFile?.workflowId,
    );
    const normalizedCandidateName = normalizeComparableText(
      buildPendingBindCandidate.displayName,
    );

    const publishedMatch = publishedScopeMembers.find(
      ({ matchedWorkflow, memberSummary }) => {
        if (
          candidateWorkflowId &&
          trimOptional(matchedWorkflow?.workflowId) === candidateWorkflowId
        ) {
          return true;
        }

        const workflowName = trimOptional(matchedWorkflow?.name);
        if (
          workflowName &&
          normalizeComparableText(workflowName) === normalizedCandidateName
        ) {
          return true;
        }

        const memberDisplayName = trimOptional(memberSummary?.displayName);
        return (
          Boolean(memberDisplayName) &&
          normalizeComparableText(memberDisplayName) === normalizedCandidateName
        );
      },
    )?.memberSummary;
    if (publishedMatch) {
      return publishedMatch;
    }

    const rosterMatches = studioScopeMembers.filter(
      (member) =>
        member.implementationKind === 'workflow' &&
        normalizeComparableText(member.displayName) === normalizedCandidateName,
    );
    return rosterMatches.length === 1 ? rosterMatches[0] : null;
  }, [
    activeWorkflowFile?.workflowId,
    buildPendingBindCandidate,
    publishedScopeMembers,
    selectedWorkflowId,
    studioScopeMembers,
  ]);
  const handleBindPendingCandidate = useCallback(async () => {
    if (!buildPendingBindCandidate || !resolvedStudioScopeId) {
      throw new Error('Resolve the current scope before binding this member.');
    }

    if (buildPendingBindCandidate.kind !== 'workflow') {
      throw new Error(
        'Studio can only publish workflow revisions from this surface right now.',
      );
    }

    const resolvedBuildMemberId = trimOptional(buildPendingMemberSummary?.memberId);
    let boundServiceCandidates: (string | null | undefined)[];
    let optimisticBoundServiceId: string;
    if (resolvedBuildMemberId) {
      const accepted = await studioApi.bindMemberWorkflow({
        scopeId: resolvedStudioScopeId,
        memberId: resolvedBuildMemberId,
        displayName: buildPendingBindCandidate.displayName,
        workflowYamls: await buildWorkflowYamlBundle(),
      });
      boundServiceCandidates = [
        buildPendingMemberSummary?.publishedServiceId,
        buildPendingBindCandidate.displayName,
        accepted.memberId,
      ];
      optimisticBoundServiceId =
        trimOptional(buildPendingMemberSummary?.publishedServiceId) ||
        trimOptional(buildPendingBindCandidate.displayName) ||
        trimOptional(accepted.memberId);
    } else {
      const result = await studioApi.bindScopeWorkflow({
        scopeId: resolvedStudioScopeId,
        displayName: buildPendingBindCandidate.displayName,
        workflowYamls: await buildWorkflowYamlBundle(),
      });
      boundServiceCandidates = [
        buildPendingBindCandidate.displayName,
        result.displayName,
        result.targetName,
        result.workflowName,
      ];
      optimisticBoundServiceId =
        trimOptional(result.serviceId) ||
        trimOptional(buildPendingBindCandidate.displayName) ||
        trimOptional(result.displayName) ||
        trimOptional(result.targetName) ||
        trimOptional(result.workflowName);
    }
    await queryClient.invalidateQueries({
      queryKey: ['studio-scope-members', resolvedStudioScopeId],
    });
    const servicesResult = await scopeServicesQuery.refetch();
    const boundServiceId =
      resolveBoundServiceIdFromCatalog({
        services: servicesResult.data ?? [],
        candidates: boundServiceCandidates,
      }) ||
      optimisticBoundServiceId;

    if (boundServiceId) {
      const boundMemberKey =
        (resolvedBuildMemberId ? `member:${resolvedBuildMemberId}` : '') ||
        trimOptional(selectedWorkflowMemberKey) ||
        (trimOptional(selectedScriptId)
          ? `script:${trimOptional(selectedScriptId)}`
          : '') ||
        trimOptional(routeState.memberKey) ||
        (trimOptional(routeState.memberId)
          ? `member:${trimOptional(routeState.memberId)}`
          : '') ||
        activeBuildFocusKey ||
        (() => {
          const resolvedBoundMemberId = resolvePublishedMemberIdFromServiceId(
            boundServiceId,
            publishedScopeMembers,
            studioScopeMembers,
          );
          return resolvedBoundMemberId
            ? `member:${resolvedBoundMemberId}`
            : `member:${boundServiceId}`;
        })();
      setRecentlyBoundMemberKey(boundMemberKey);
      setRecentlyBoundServiceId(boundServiceId);
      const selectedService = (servicesResult.data ?? []).find(
        (service) => service.serviceId === boundServiceId,
      );
      const defaultEndpointId = resolveStudioServiceDefaultEndpointId(
        selectedService,
      );

      bindingSelectionRef.current = {
        serviceId: boundServiceId,
        endpointId: defaultEndpointId,
      };
      invokeSelectionRef.current = {
        serviceId: boundServiceId,
        endpointId: defaultEndpointId,
      };

      history.replace(
        buildStudioRoute({
          scopeId: resolvedStudioScopeId || undefined,
          memberKey: boundMemberKey,
          step: 'bind',
        }),
      );
    }
  }, [
    activeBuildFocusKey,
    buildPendingMemberSummary,
    buildWorkflowYamlBundle,
    buildPendingBindCandidate,
    queryClient,
    publishedScopeMembers,
    resolvedStudioScopeId,
    routeState.memberId,
    routeState.memberKey,
    selectedScriptId,
    selectedWorkflowMemberKey,
    scopeServicesQuery,
    studioScopeMembers,
  ]);

  const openWorkspaceWorkflow = (workflowId: string) => {
    const normalizedWorkflowId = trimOptional(workflowId);
    setSelectedWorkflowId(normalizedWorkflowId);
    setTemplateWorkflow('');
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
      return;
    }

    setSelectedWorkflowId('');
    setTemplateWorkflow('');
    clearWorkflowBuildFocus();
  }, [
    activeSourceReady,
    activeWorkflowSourceKey,
    clearWorkflowBuildFocus,
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

  const applySavedWorkflowSelection = useCallback(
    async (
      savedWorkflow: StudioWorkflowFile,
      options?: {
        readonly layout?: unknown;
      },
    ) => {
      queryClient.setQueryData(
        ['studio-workflow', workflowWorkspaceContextKey, savedWorkflow.workflowId],
        savedWorkflow,
      );
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
      });

      setSelectedWorkflowId(savedWorkflow.workflowId);
      setSelectedScriptId('');
      setTemplateWorkflow('');
      setBuildSurface('editor');
      setStudioSurface('build');
      setDraftSourceKey(
        `workflow:${workflowWorkspaceContextKey}:${savedWorkflow.workflowId}`,
      );
      setDraftYaml(savedWorkflow.yaml);
      setDraftWorkflowName(savedWorkflow.name);
      setDraftFileName(savedWorkflow.fileName);
      setDraftDirectoryId(savedWorkflow.directoryId);
      setDraftWorkflowLayout(
        savedWorkflow.layout ||
          options?.layout ||
          draftWorkflowLayout ||
          buildStudioWorkflowLayout(savedWorkflow.name, workflowGraph.nodes),
      );
      setSaveNotice(null);
      setRunNotice(null);
    },
    [
      draftWorkflowLayout,
      queryClient,
      workflowGraph.nodes,
      workflowWorkspaceContextKey,
    ],
  );
  const confirmScriptsStudioLeave = useCallback(async () => {
    if (!isBuildScriptsSurface) {
      return true;
    }

    const leaveGuard = scriptLeaveGuardRef.current;
    return leaveGuard ? await leaveGuard() : true;
  }, [isBuildScriptsSurface]);

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
        draftExists: activeWorkflowFile?.draftExists,
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

      await applySavedWorkflowSelection(savedWorkflow, {
        layout: draftWorkflowLayout,
      });
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

  const openCreateMemberFlow = useCallback(async () => {
    if (!(await confirmScriptsStudioLeave())) {
      return;
    }

    setCreateMemberName(suggestedCreateWorkflowName);
    setCreateMemberKind('workflow');
    setCreateMemberDirectoryId(
      inventoryDirectoryId || inventoryDirectoryOptions[0]?.directoryId || '',
    );
    setCreateMemberModalOpen(true);
  }, [
    confirmScriptsStudioLeave,
    inventoryDirectoryId,
    inventoryDirectoryOptions,
    suggestedCreateWorkflowName,
  ]);

  useEffect(() => {
    if (!isStudioLocation || !pendingCreateMemberIntentSnapshot) {
      return;
    }

    if (!studioHostReady || createMemberModalOpen) {
      return;
    }

    if (
      handledCreateMemberIntentSnapshotRef.current ===
      pendingCreateMemberIntentSnapshot
    ) {
      return;
    }

    handledCreateMemberIntentSnapshotRef.current = pendingCreateMemberIntentSnapshot;
    setPendingCreateMemberIntentSnapshot('');
    void openCreateMemberFlow();
  }, [
    createMemberModalOpen,
    isStudioLocation,
    openCreateMemberFlow,
    pendingCreateMemberIntentSnapshot,
    studioHostReady,
  ]);

  const closeCreateMemberFlow = useCallback(() => {
    if (inventoryBusyKey === 'create') {
      return;
    }

    setCreateMemberModalOpen(false);
  }, [inventoryBusyKey]);

  const handleCreateMember = useCallback(async () => {
    if (createMemberKind !== 'workflow') {
      void message.info(
        createMemberKind === 'script'
          ? 'Script member authority exists on the backend now, but this modal still continues through Build > Script.'
          : 'GAgent member authority exists on the backend now, but this modal still continues through Build > GAgent.',
      );
      return;
    }

    const workflowName = trimOptional(createMemberName);
    const directoryId = trimOptional(createMemberDirectoryId) || inventoryDirectoryId;
    if (!workflowName) {
      void message.warning('Member name is required.');
      return;
    }

    if (!directoryId) {
      void message.error(
        'Add a workflow directory in Config before creating a workflow draft here.',
      );
      return;
    }

    if (
      visibleWorkflowSummaries.some(
        (workflow) => normalizeComparableText(workflow.name) === workflowName.toLowerCase(),
      )
    ) {
      void message.warning('A workflow draft with the same name already exists.');
      return;
    }

    if (
      studioScopeMembers.some(
        (member) =>
          normalizeComparableText(member.displayName) === workflowName.toLowerCase(),
      )
    ) {
      void message.warning('A team member with the same name already exists.');
      return;
    }

    setInventoryBusyKey('create');
    setInventoryBusyAction('create');

    try {
      const savedWorkflow = await studioApi.saveWorkflow({
        scopeId: resolvedStudioScopeId || undefined,
        directoryId,
        workflowName,
        fileName: buildWorkflowFileName(workflowName),
        yaml: buildBlankDraftYaml(workflowName),
        layout: buildStudioWorkflowLayout(workflowName, []),
      });

      await applySavedWorkflowSelection(savedWorkflow);
      setCreateMemberModalOpen(false);

      if (!resolvedStudioScopeId) {
        void message.success(
          `Created workflow draft for member ${workflowName}. Connect a scope to register the backend member authority.`,
        );
        return;
      }

      try {
        await studioApi.createMember({
          scopeId: resolvedStudioScopeId,
          displayName: workflowName,
          implementationKind: 'workflow',
        });
        await queryClient.invalidateQueries({
          queryKey: ['studio-scope-members', resolvedStudioScopeId],
        });
        void message.success(
          `Created member ${workflowName} and opened its workflow draft.`,
        );
      } catch (memberError) {
        void message.error(
          memberError instanceof Error
            ? `Workflow draft created, but Studio could not register the member authority: ${memberError.message}`
            : 'Workflow draft created, but Studio could not register the member authority.',
        );
      }
    } catch (error) {
      void message.error(
        error instanceof Error
          ? error.message
          : 'Failed to create a workflow draft for this member.',
      );
    } finally {
      setInventoryBusyKey('');
      setInventoryBusyAction('');
    }
  }, [
    applySavedWorkflowSelection,
    createMemberKind,
    createMemberDirectoryId,
    createMemberName,
    inventoryDirectoryId,
    queryClient,
    resolvedStudioScopeId,
    studioScopeMembers,
    visibleWorkflowSummaries,
  ]);

  const handleRenameWorkflowMember = useCallback(
    async (memberKey: string) => {
      const workflowId = resolveWorkflowIdFromRouteValue(
        readWorkflowMemberRouteValueFromMemberKey(memberKey),
        visibleWorkflowSummaries,
        {
          allowDirectIdFallback: true,
          workflowFile: activeWorkflowFile,
        },
      );
      if (!workflowId) {
        return;
      }

      const currentWorkflowSummary = visibleWorkflowSummaries.find(
        (workflow) => workflow.workflowId === workflowId,
      );
      const currentWorkflowName =
        trimOptional(currentWorkflowSummary?.name) ||
        (selectedWorkflowId === workflowId
          ? trimOptional(draftWorkflowName) || trimOptional(activeWorkflowName)
          : '') ||
        'workflow';
      const nextWorkflowName = trimOptional(
        window.prompt('Rename workflow member', currentWorkflowName) ?? '',
      );

      if (!nextWorkflowName || nextWorkflowName === currentWorkflowName) {
        return;
      }

      if (
        visibleWorkflowSummaries.some(
          (workflow) =>
            workflow.workflowId !== workflowId &&
            workflow.name.trim().toLowerCase() === nextWorkflowName.toLowerCase(),
        )
      ) {
        void message.warning('A workflow member with the same name already exists.');
        return;
      }

      setInventoryBusyKey(memberKey);
      setInventoryBusyAction('rename');

      try {
        const isSelectedWorkflow = selectedWorkflowId === workflowId;
        const fallbackWorkflowFile =
          !isSelectedWorkflow || !activeWorkflowFile
            ? await studioApi.getWorkflow(workflowId, resolvedStudioScopeId)
            : activeWorkflowFile;
        const baseDocument =
          isSelectedWorkflow && activeWorkflowDocument
            ? cloneStudioWorkflowDocument(activeWorkflowDocument)
            : cloneStudioWorkflowDocument(
                fallbackWorkflowFile.document ??
                  (
                    await studioApi.parseYaml({
                      yaml: fallbackWorkflowFile.yaml,
                      availableWorkflowNames: workflowNames,
                      availableStepTypes,
                    })
                  ).document ??
                  null,
              );

        if (!baseDocument) {
          throw new Error('Failed to load the workflow document for rename.');
        }

        const nextDocument: StudioWorkflowDocument = {
          ...baseDocument,
        };
        nextDocument.name = nextWorkflowName;
        const serialized = await studioApi.serializeYaml({
          document: nextDocument,
          availableWorkflowNames: workflowNames.filter(
            (name) => name.trim().toLowerCase() !== currentWorkflowName.toLowerCase(),
          ),
          availableStepTypes,
        });
        const savedWorkflow = await studioApi.saveWorkflow({
          workflowId,
          scopeId: resolvedStudioScopeId || undefined,
          directoryId:
            (isSelectedWorkflow ? draftDirectoryId : '') ||
            fallbackWorkflowFile.directoryId ||
            currentWorkflowSummary?.directoryId ||
            inventoryDirectoryId,
          workflowName: nextWorkflowName,
          fileName: buildWorkflowFileName(nextWorkflowName),
          yaml: serialized.yaml,
          layout:
            (isSelectedWorkflow ? draftWorkflowLayout : null) ||
            fallbackWorkflowFile.layout,
        });

        if (isSelectedWorkflow) {
          setEditableWorkflowDocument(
            cloneStudioWorkflowDocument(serialized.document),
          );
        }

        await applySavedWorkflowSelection(savedWorkflow, {
          layout:
            (isSelectedWorkflow ? draftWorkflowLayout : null) ||
            fallbackWorkflowFile.layout,
        });
        void message.success(`Renamed workflow member to ${nextWorkflowName}.`);
      } catch (error) {
        void message.error(
          error instanceof Error ? error.message : 'Failed to rename workflow member.',
        );
      } finally {
        setInventoryBusyKey('');
        setInventoryBusyAction('');
      }
    },
    [
      activeWorkflowDocument,
      activeWorkflowFile,
      activeWorkflowName,
      applySavedWorkflowSelection,
      availableStepTypes,
      draftDirectoryId,
      draftWorkflowLayout,
      draftWorkflowName,
      inventoryDirectoryId,
      resolvedStudioScopeId,
      selectedWorkflowId,
      visibleWorkflowSummaries,
      workflowNames,
    ],
  );

  const handleDeleteWorkflowMember = useCallback(
    (memberKey: string) => {
      const workflowId = resolveWorkflowIdFromRouteValue(
        readWorkflowMemberRouteValueFromMemberKey(memberKey),
        visibleWorkflowSummaries,
        {
          allowDirectIdFallback: true,
          workflowFile: activeWorkflowFile,
        },
      );
      if (!workflowId) {
        return;
      }

      const workflowLabel =
        visibleWorkflowSummaries.find(
          (workflow) => workflow.workflowId === workflowId,
        )?.name || 'this workflow member';

      Modal.confirm({
        autoFocusButton: 'cancel',
        cancelText: 'Keep member',
        centered: true,
        content: (
          <div style={{ display: 'grid', gap: 12 }}>
            <Typography.Text
              style={{
                color: '#111827',
                fontSize: 13,
                lineHeight: '20px',
              }}
            >
              Remove <strong>{workflowLabel}</strong> from the current member
              inventory?
            </Typography.Text>
            <div
              style={{
                background: 'rgba(254, 242, 242, 0.92)',
                border: '1px solid rgba(248, 113, 113, 0.18)',
                borderRadius: 12,
                display: 'grid',
                gap: 4,
                padding: '10px 12px',
              }}
            >
              <Typography.Text
                strong
                style={{
                  color: '#991b1b',
                  fontSize: 12,
                  letterSpacing: '0.02em',
                }}
              >
                Draft only
              </Typography.Text>
              <Typography.Text
                style={{
                  color: '#7f1d1d',
                  fontSize: 12,
                  lineHeight: '18px',
                }}
              >
                This only deletes the Studio workflow draft. Published bindings,
                live revisions, and historical runs stay intact.
              </Typography.Text>
            </div>
          </div>
        ),
        icon: <DeleteOutlined style={{ color: '#dc2626' }} />,
        okButtonProps: {
          danger: true,
        },
        okText: 'Delete member',
        title: 'Delete workflow member',
        width: 460,
        onOk: async () => {
          setInventoryBusyKey(memberKey);
          setInventoryBusyAction('delete');

          try {
            await studioApi.deleteWorkflow(
              workflowId,
              resolvedStudioScopeId || undefined,
            );
            queryClient.removeQueries({
              queryKey: ['studio-workflow', workflowWorkspaceContextKey, workflowId],
            });
            await queryClient.invalidateQueries({
              queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
            });

            if (selectedWorkflowId === workflowId) {
              const fallbackWorkflowId =
                visibleWorkflowSummaries.find(
                  (workflow) => workflow.workflowId !== workflowId,
                )?.workflowId || '';
              if (fallbackWorkflowId) {
                openWorkspaceWorkflow(fallbackWorkflowId);
              } else {
                clearWorkflowBuildFocus();
              }
            }

            void message.success(`Deleted workflow member ${workflowLabel}.`);
          } catch (error) {
            void message.error(
              error instanceof Error
                ? error.message
                : 'Failed to delete workflow member.',
            );
            throw error;
          } finally {
            setInventoryBusyKey('');
            setInventoryBusyAction('');
          }
        },
      });
    },
    [
      activeWorkflowFile,
      openWorkspaceWorkflow,
      clearWorkflowBuildFocus,
      queryClient,
      resolvedStudioScopeId,
      selectedWorkflowId,
      visibleWorkflowSummaries,
      workflowWorkspaceContextKey,
    ],
  );

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
    if (
      !selectedExecutionId ||
      !executionCanStop ||
      !resolvedStudioScopeId ||
      !workbenchPublishedServiceId
    ) {
      return;
    }

    setExecutionStopPending(true);
    setExecutionNotice(null);
    try {
      await runtimeRunsApi.stop(
        resolvedStudioScopeId,
        {
          actorId: trimOptional(selectedObserveRunSummary?.actorId) || undefined,
          runId: selectedExecutionId,
          reason: 'user requested stop',
        },
        {
          memberId: workbenchStudioMemberId || undefined,
          serviceId: workbenchPublishedServiceId,
        },
      );
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['studio-observe-runs', resolvedStudioScopeId],
        }),
        queryClient.invalidateQueries({
          queryKey: ['studio-observe-run-audit', resolvedStudioScopeId],
        }),
      ]);
      setExecutionNotice({
        type: 'info',
        message: 'Stop requested for the active member run.',
      });
    } catch (error) {
      setExecutionNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to stop the active member run.',
      });
    } finally {
      setExecutionStopPending(false);
    }
  };

  const handleResumeExecution = async (
    interaction: {
      readonly kind: 'human_input' | 'human_approval' | 'wait_signal';
      readonly runId: string;
      readonly stepId: string;
      readonly signalName?: string;
    },
    action: 'submit' | 'approve' | 'reject' | 'signal',
    userInput: string,
  ) => {
    if (
      !selectedExecutionId ||
      !resolvedStudioScopeId ||
      !workbenchPublishedServiceId
    ) {
      return;
    }

    setExecutionNotice(null);
    try {
      const actorId = trimOptional(selectedObserveRunSummary?.actorId);
      if (!actorId) {
        throw new Error(
          'Studio could not resolve the actor id for the active member run.',
        );
      }

      if (interaction.kind === 'wait_signal' || action === 'signal') {
        await runtimeRunsApi.signal(
          resolvedStudioScopeId,
          {
            actorId,
            runId: interaction.runId,
            signalName: trimOptional(interaction.signalName) || 'continue',
            stepId: interaction.stepId,
            payload: userInput.trim() || undefined,
          },
          {
            memberId: workbenchStudioMemberId || undefined,
            serviceId: workbenchPublishedServiceId,
          },
        );
      } else {
        await runtimeRunsApi.resume(
          resolvedStudioScopeId,
          {
            actorId,
            runId: interaction.runId,
            stepId: interaction.stepId,
            approved:
              interaction.kind === 'human_input'
                ? true
                : action === 'approve',
            userInput: userInput.trim() || undefined,
          },
          {
            memberId: workbenchStudioMemberId || undefined,
            serviceId: workbenchPublishedServiceId,
          },
        );
      }
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: ['studio-observe-runs', resolvedStudioScopeId],
        }),
        queryClient.invalidateQueries({
          queryKey: ['studio-observe-run-audit', resolvedStudioScopeId],
        }),
      ]);
      setExecutionNotice({
        type: 'success',
        message:
          interaction.kind === 'wait_signal' || action === 'signal'
            ? 'Signal submitted for the active member run.'
            : interaction.kind === 'human_approval'
              ? action === 'approve'
                ? 'Approval submitted for the active member run.'
                : 'Rejection submitted for the active member run.'
              : 'Input submitted for the active member run.',
      });
    } catch (error) {
      setExecutionNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to continue the active member run.',
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

      const afterStepId = effectiveSelectedGraphNodeId.startsWith('step:')
        ? effectiveSelectedGraphNodeId.slice('step:'.length)
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
      effectiveSelectedGraphNodeId,
      resolveEditableWorkflowDocument,
      workflowRoleOptions,
    ],
  );
  const handleApplyWorkflowStepDraft = useCallback(
    async (draft: StudioStepInspectorDraft) => {
      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return;
      }

      const currentStepId = effectiveSelectedGraphNodeId.startsWith('step:')
        ? effectiveSelectedGraphNodeId.slice('step:'.length)
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
      effectiveSelectedGraphNodeId,
      resolveEditableWorkflowDocument,
    ],
  );
  const handleRemoveWorkflowStep = useCallback(async () => {
    const document = await resolveEditableWorkflowDocument();
    if (!document) {
      return;
    }

    const currentStepId = effectiveSelectedGraphNodeId.startsWith('step:')
      ? effectiveSelectedGraphNodeId.slice('step:'.length)
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
    effectiveSelectedGraphNodeId,
    resolveEditableWorkflowDocument,
  ]);
  const handleRemoveWorkflowNodes = useCallback(
    async (nodeIds: string[]) => {
      const stepIds = Array.from(
        new Set(
          nodeIds
            .map((nodeId) =>
              trimOptional(nodeId).startsWith('step:')
                ? trimOptional(nodeId).slice('step:'.length)
                : '',
            )
            .filter(Boolean),
        ),
      );
      if (stepIds.length === 0) {
        return;
      }

      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return;
      }

      const result = removeSteps(document, stepIds);
      await applySerializedWorkflowDocument(result.document, {
        selectedNodeId: result.nodeId,
      });
    },
    [applySerializedWorkflowDocument, resolveEditableWorkflowDocument],
  );
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
      invokeSelectionRef.current = selection;
    },
    [],
  );
  const handleRegisterScriptLeaveGuard = useCallback(
    (guard: (() => Promise<boolean>) | null) => {
      scriptLeaveGuardRef.current = guard;
    },
    [],
  );
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
  const handleObserveSessionChange = useCallback(
    (session: StudioObserveSessionSeed | null) => {
      const serviceId = trimOptional(session?.serviceId);
      if (!session || !serviceId) {
        return;
      }

      setObserveSessionSeedsByServiceId((current) => {
        const existing = current[serviceId];
        if (
          existing &&
          existing.runId === session.runId &&
          existing.status === session.status &&
          existing.events.length === session.events.length &&
          existing.completedAtUtc === session.completedAtUtc &&
          existing.startedAtUtc === session.startedAtUtc
        ) {
          return current;
        }

        return {
          ...current,
          [serviceId]: session,
        };
      });
      if (resolvedStudioScopeId) {
        saveStudioObserveSessionSeed({
          scopeId: resolvedStudioScopeId,
          session,
        });
      }
    },
    [resolvedStudioScopeId],
  );
  const handleUseBindingEndpoint = useCallback(
    (serviceId: string, endpointId: string) => {
      const resolvedMemberId =
        trimOptional(routeState.memberId) ||
        resolvePublishedMemberIdFromServiceId(
          serviceId,
          publishedScopeMembers,
          studioScopeMembers,
        );
      bindingSelectionRef.current = {
        serviceId,
        endpointId,
      };
      invokeSelectionRef.current = {
        serviceId,
        endpointId,
      };
      history.replace(
        buildStudioRoute({
          scopeId: resolvedStudioScopeId || undefined,
          memberKey:
            trimOptional(routeState.memberKey) ||
            (resolvedMemberId
              ? `member:${resolvedMemberId}`
              : '') ||
            activeBuildFocusKey ||
            (serviceId ? `member:${serviceId}` : undefined),
          step: 'invoke',
          tab: 'invoke',
        }),
      );
      applyStudioTarget('invoke');
    },
    [
      activeBuildFocusKey,
      applyStudioTarget,
      history,
      publishedScopeMembers,
      resolvedStudioScopeId,
      routeState.memberId,
      routeState.memberKey,
      studioScopeMembers,
    ],
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
  const currentLifecycleStep =
    isBindSurface
      ? 'bind'
      : isInvokeSurface
        ? 'invoke'
        : isObserveSurface
          ? 'observe'
          : 'build';
  const routeSelectedMemberKey = useMemo(
    () =>
      trimOptional(routeState.memberKey) ||
      (trimOptional(routeState.memberId)
        ? `member:${trimOptional(routeState.memberId)}`
        : ''),
    [routeState.memberId, routeState.memberKey],
  );
  const buildSurfaceMemberKey = useMemo(
    () =>
      buildStudioFocusKey({
        activeBuildFocusKey,
        routeMemberKey: routeSelectedMemberKey,
        routeMemberId: routeState.memberId,
      }),
    [activeBuildFocusKey, routeSelectedMemberKey, routeState.memberId],
  );
  const selectedWorkflowSummary = useMemo(
    () =>
      visibleWorkflowSummaries.find(
        (workflow) =>
          trimOptional(workflow.workflowId) === trimOptional(selectedWorkflowId),
      ) ?? null,
    [selectedWorkflowId, visibleWorkflowSummaries],
  );
  const persistableBuildMemberKey = useMemo(() => {
    const explicitRouteMemberKey = trimOptional(routeSelectedMemberKey);
    if (explicitRouteMemberKey) {
      return explicitRouteMemberKey;
    }

    if (buildSurface === 'editor') {
      const workflowId = trimOptional(selectedWorkflowId);
      if (!workflowId) {
        return '';
      }

      return activeWorkflowFile ||
        visibleWorkflowSummaries.some(
          (workflow) => trimOptional(workflow.workflowId) === workflowId,
        )
        ? buildWorkflowMemberKeyFromSummary({
            workflowId,
            name:
              activeWorkflowFile?.name ||
              selectedWorkflowSummary?.name ||
              draftWorkflowName,
            fileName:
              activeWorkflowFile?.fileName || selectedWorkflowSummary?.fileName,
          })
        : '';
    }

    if (buildSurface === 'scripts') {
      const scriptId = trimOptional(selectedScriptId);
      if (!scriptId) {
        return '';
      }

      return availableScopeScriptIds.has(normalizeComparableText(scriptId))
        ? `script:${scriptId}`
        : '';
    }

    return '';
  }, [
    activeWorkflowFile,
    availableScopeScriptIds,
    buildSurface,
    draftWorkflowName,
    routeSelectedMemberKey,
    selectedScriptId,
    selectedWorkflowId,
    selectedWorkflowSummary?.fileName,
    selectedWorkflowSummary?.name,
    visibleWorkflowSummaries,
  ]);
  const activeWorkflowPublishedServiceId = useMemo(() => {
    const workflowId = trimOptional(selectedWorkflowId);
    if (!workflowId) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ matchedWorkflow }) =>
        trimOptional(matchedWorkflow?.workflowId) === workflowId,
    );
    return trimOptional(matchedMember?.service.serviceId);
  }, [publishedScopeMembers, selectedWorkflowId]);
  const activeWorkflowPublishedMemberId = useMemo(() => {
    const workflowId = trimOptional(selectedWorkflowId);
    if (!workflowId) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ matchedWorkflow }) =>
        trimOptional(matchedWorkflow?.workflowId) === workflowId,
    );
    return trimOptional(matchedMember?.memberSummary?.memberId);
  }, [publishedScopeMembers, selectedWorkflowId]);
  const activeScriptPublishedServiceId = useMemo(() => {
    const scriptId = trimOptional(selectedScriptId);
    if (!scriptId) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ matchedScript }) =>
        trimOptional(matchedScript?.script?.scriptId) === scriptId,
    );
    return trimOptional(matchedMember?.service.serviceId);
  }, [publishedScopeMembers, selectedScriptId]);
  const activeScriptPublishedMemberId = useMemo(() => {
    const scriptId = trimOptional(selectedScriptId);
    if (!scriptId) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ matchedScript }) =>
        trimOptional(matchedScript?.script?.scriptId) === scriptId,
    );
    return trimOptional(matchedMember?.memberSummary?.memberId);
  }, [publishedScopeMembers, selectedScriptId]);
  const activeGAgentPublishedServiceId = useMemo(() => {
    const actorTypeName = trimOptional(selectedGAgentTypeName);
    if (!actorTypeName) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ revision }) =>
        revision?.implementationKind === 'gagent' &&
        trimOptional(revision.staticActorTypeName) === actorTypeName,
    );
    return trimOptional(matchedMember?.service.serviceId);
  }, [publishedScopeMembers, selectedGAgentTypeName]);
  const activeGAgentPublishedMemberId = useMemo(() => {
    const actorTypeName = trimOptional(selectedGAgentTypeName);
    if (!actorTypeName) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ revision }) =>
        revision?.implementationKind === 'gagent' &&
        trimOptional(revision.staticActorTypeName) === actorTypeName,
    );
    return trimOptional(matchedMember?.memberSummary?.memberId);
  }, [publishedScopeMembers, selectedGAgentTypeName]);
  const activeBuildPublishedServiceId =
    activeWorkflowPublishedServiceId ||
    activeScriptPublishedServiceId ||
    activeGAgentPublishedServiceId;
  const activeBuildPublishedMemberId =
    activeWorkflowPublishedMemberId ||
    activeScriptPublishedMemberId ||
    activeGAgentPublishedMemberId;
  const selectedWorkflowRepresentsPublishedMember =
    Boolean(activeWorkflowPublishedServiceId);
  const selectedScriptRepresentsPublishedMember = Boolean(
    activeScriptPublishedServiceId,
  );
  const selectedGAgentRepresentsPublishedMember = Boolean(
    activeGAgentPublishedServiceId,
  );
  const selectedBuildRepresentsPublishedMember =
    selectedWorkflowRepresentsPublishedMember ||
    selectedScriptRepresentsPublishedMember ||
    selectedGAgentRepresentsPublishedMember;
  const lifecycleSurfaceMemberKey =
    routeSelectedMemberKey ||
    buildSurfaceMemberKey ||
    (activeBuildPublishedMemberId
      ? `member:${activeBuildPublishedMemberId}`
      : '');
  const currentFocusMemberKey =
    studioSurface === 'build' ? buildSurfaceMemberKey : lifecycleSurfaceMemberKey;
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

    const matchedRouteWorkflowId =
      routeBuildFocus.kind === 'workflow'
        ? resolveWorkflowIdFromRouteValue(
            routeBuildFocus.value,
            visibleWorkflowSummaries,
            {
              allowDirectIdFallback: false,
            },
          )
        : '';
    const routeWorkflowSelectionPending =
      studioSurface === 'build' &&
      !trimOptional(routeSelectedMemberKey) &&
      routeBuildFocus.kind === 'workflow' &&
      Boolean(routeBuildFocus.value) &&
      (
        workflowsQuery.isLoading
          ? !trimOptional(selectedWorkflowId)
          : Boolean(matchedRouteWorkflowId) &&
            matchedRouteWorkflowId !== trimOptional(selectedWorkflowId)
      );
    if (routeWorkflowSelectionPending) {
      return;
    }

    const tab: StudioTab | undefined =
      studioSurface === 'bind'
        ? undefined
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
    const persistedMemberKey =
      studioSurface === 'build'
        ? trimOptional(persistableBuildMemberKey) || undefined
        : trimOptional(lifecycleSurfaceMemberKey) || undefined;
    const persistedFocus =
      persistBuildFocusRoute &&
      trimOptional(activeBuildFocusKey) !== trimOptional(persistedMemberKey)
        ? activeBuildFocusKey || undefined
        : undefined;

    history.replace(buildStudioRoute({
      scopeId: resolvedStudioScopeId || undefined,
      memberKey: persistedMemberKey,
      step,
      focus: persistedFocus,
      tab,
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
    buildSurface,
    isStudioLocation,
    lifecycleSurfaceMemberKey,
    locationSnapshot,
    logsPopoutMode,
    persistableBuildMemberKey,
    resolvedStudioScopeId,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    routeSelectedMemberKey,
    runPrompt,
    selectedWorkflowId,
    selectedExecutionId,
    studioSurface,
    visibleWorkflowSummaries,
    workflowsQuery.isLoading,
  ]);
  const workbenchMemberKey = currentFocusMemberKey;
  const buildSurfaceSelectedMemberKey =
    studioSurface === 'build' &&
    (currentFocusMemberKey.startsWith('workflow:') ||
      currentFocusMemberKey.startsWith('script:'))
      ? activeBuildPublishedMemberId
        ? `member:${activeBuildPublishedMemberId}`
        : currentFocusMemberKey
      : '';
  const workbenchPublishedServiceId = useMemo(
    () =>
      resolvePublishedServiceIdFromMemberKey(
        workbenchMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      ),
    [publishedScopeMembers, studioScopeMembers, workbenchMemberKey],
  );
  const workbenchStudioMemberSummary = useMemo(
    () =>
      resolveStudioMemberSummaryFromMemberKey(
        workbenchMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      ),
    [publishedScopeMembers, studioScopeMembers, workbenchMemberKey],
  );
  const workbenchStudioMemberId = useMemo(
    () => trimOptional(workbenchStudioMemberSummary?.memberId),
    [workbenchStudioMemberSummary?.memberId],
  );
  const workbenchStudioMemberDetailQuery = useQuery({
    queryKey: ['studio-scope-member', resolvedStudioScopeId, workbenchStudioMemberId],
    enabled:
      studioHostReady &&
      Boolean(resolvedStudioScopeId) &&
      Boolean(workbenchStudioMemberId),
    retry: false,
    queryFn: () => studioApi.getMember(resolvedStudioScopeId, workbenchStudioMemberId),
  });
  const workbenchStudioMember = useMemo(
    () => workbenchStudioMemberDetailQuery.data?.summary ?? workbenchStudioMemberSummary,
    [workbenchStudioMemberDetailQuery.data?.summary, workbenchStudioMemberSummary],
  );
  const workbenchStudioMemberBinding = useMemo(
    () => workbenchStudioMemberDetailQuery.data?.lastBinding ?? null,
    [workbenchStudioMemberDetailQuery.data?.lastBinding],
  );
  const workbenchPublishedService = useMemo(
    () =>
      workbenchPublishedServiceId
        ? publishedScopeServices.find(
            (service) => service.serviceId === workbenchPublishedServiceId,
          ) ?? null
        : null,
    [publishedScopeServices, workbenchPublishedServiceId],
  );
  const workbenchPublishedServiceRevision = useMemo(() => {
    const serviceId = trimOptional(workbenchPublishedService?.serviceId);
    return serviceId
      ? currentServiceRevisionByServiceId.get(serviceId) ?? null
      : null;
  }, [currentServiceRevisionByServiceId, workbenchPublishedService?.serviceId]);
  useEffect(() => {
    if (!resolvedStudioScopeId || !workbenchPublishedServiceId) {
      return;
    }

    const persistedSession = loadStudioObserveSessionSeed({
      scopeId: resolvedStudioScopeId,
      serviceId: workbenchPublishedServiceId,
    });
    if (!persistedSession) {
      return;
    }

    if (!isStudioObserveSessionSeedFresh(persistedSession)) {
      clearStudioObserveSessionSeed({
        scopeId: resolvedStudioScopeId,
        serviceId: workbenchPublishedServiceId,
      });
      return;
    }

    setObserveSessionSeedsByServiceId((current) => {
      const existing = current[workbenchPublishedServiceId];
      if (
        existing &&
        trimOptional(existing.runId) === trimOptional(persistedSession.runId) &&
        trimOptional(existing.completedAtUtc) ===
          trimOptional(persistedSession.completedAtUtc) &&
        trimOptional(existing.startedAtUtc) ===
          trimOptional(persistedSession.startedAtUtc)
      ) {
        return current;
      }

      return {
        ...current,
        [workbenchPublishedServiceId]: persistedSession,
      };
    });
  }, [resolvedStudioScopeId, workbenchPublishedServiceId]);
  const observeCurrentSessionSeed = useMemo(
    () => {
      if (!workbenchPublishedServiceId) {
        return null;
      }

      const session = observeSessionSeedsByServiceId[workbenchPublishedServiceId] ?? null;
      return isStudioObserveSessionSeedFresh(session) ? session : null;
    },
    [observeSessionSeedsByServiceId, workbenchPublishedServiceId],
  );
  const observeFallbackExecution = useMemo(
    () =>
      observeCurrentSessionSeed
        ? toObserveExecutionFromSessionSeed(observeCurrentSessionSeed, {
            workflowName:
              trimOptional(workbenchPublishedServiceRevision?.workflowName) ||
              trimOptional(workbenchPublishedService?.displayName) ||
              trimOptional(observeCurrentSessionSeed.serviceLabel),
          })
        : null,
    [
      observeCurrentSessionSeed,
      workbenchPublishedService?.displayName,
      workbenchPublishedServiceRevision?.workflowName,
    ],
  );
  const observeServiceRunsQuery = useQuery({
    queryKey: [
      'studio-observe-runs',
      resolvedStudioScopeId,
      workbenchStudioMemberId,
      workbenchPublishedServiceId,
    ],
    enabled:
      studioSurface === 'observe' &&
      studioHostReady &&
      Boolean(resolvedStudioScopeId) &&
      Boolean(workbenchStudioMemberId || workbenchPublishedServiceId),
    queryFn: () =>
      workbenchStudioMemberId
        ? scopeRuntimeApi.listMemberRuns(
            resolvedStudioScopeId,
            workbenchStudioMemberId,
            {
              take: 12,
            },
          )
        : scopeRuntimeApi.listServiceRuns(
            resolvedStudioScopeId,
            workbenchPublishedServiceId,
            {
              take: 12,
            },
          ),
    retry: false,
  });
  const observeServiceRuns = useMemo(() => {
    const runs = [...(observeServiceRunsQuery.data?.runs ?? [])];
    return runs.sort((left, right) => {
      const leftTimestamp =
        Date.parse(
          trimOptional(left.lastUpdatedAt) || readObserveRunStartedAt(left) || '',
        ) || 0;
      const rightTimestamp =
        Date.parse(
          trimOptional(right.lastUpdatedAt) || readObserveRunStartedAt(right) || '',
        ) || 0;
      return rightTimestamp - leftTimestamp;
    });
  }, [observeServiceRunsQuery.data?.runs]);
  const selectedObserveBackendRunSummary = useMemo(
    () =>
      selectedExecutionId
        ? observeServiceRuns.find(
            (run) => trimOptional(run.runId) === trimOptional(selectedExecutionId),
          ) ?? null
        : null,
    [observeServiceRuns, selectedExecutionId],
  );
  const selectedObserveFallbackExecution = useMemo(() => {
    if (!selectedExecutionId || !observeFallbackExecution) {
      return null;
    }

    return trimOptional(observeFallbackExecution.executionId) ===
      trimOptional(selectedExecutionId)
      ? observeFallbackExecution
      : null;
  }, [observeFallbackExecution, selectedExecutionId]);
  const selectedObserveRunSummary =
    selectedObserveBackendRunSummary || selectedObserveFallbackExecution;
  const selectedObserveRunAuditQuery = useQuery({
    queryKey: [
      'studio-observe-run-audit',
      resolvedStudioScopeId,
      workbenchStudioMemberId,
      workbenchPublishedServiceId,
      selectedExecutionId,
      trimOptional(selectedObserveRunSummary?.actorId),
    ],
    enabled:
      studioSurface === 'observe' &&
      studioHostReady &&
      Boolean(resolvedStudioScopeId) &&
      Boolean(workbenchStudioMemberId || workbenchPublishedServiceId) &&
      Boolean(selectedExecutionId) &&
      Boolean(selectedObserveBackendRunSummary),
    queryFn: () =>
      workbenchStudioMemberId
        ? scopeRuntimeApi.getMemberRunAudit(
            resolvedStudioScopeId,
            workbenchStudioMemberId,
            selectedExecutionId,
            {
              actorId:
                trimOptional(selectedObserveBackendRunSummary?.actorId) || undefined,
            },
          )
        : scopeRuntimeApi.getServiceRunAudit(
            resolvedStudioScopeId,
            workbenchPublishedServiceId,
            selectedExecutionId,
            {
              actorId:
                trimOptional(selectedObserveBackendRunSummary?.actorId) || undefined,
            },
          ),
    retry: false,
  });
  useEffect(() => {
    if (
      studioSurface !== 'observe' ||
      observeServiceRunsQuery.isLoading ||
      observeServiceRunsQuery.isFetching ||
      !observeCurrentSessionSeed ||
      !workbenchPublishedServiceId
    ) {
      return;
    }

    const sessionRunId = trimOptional(observeCurrentSessionSeed.runId);
    if (!sessionRunId) {
      return;
    }

    if (
      observeServiceRuns.some(
        (run) => trimOptional(run.runId) === sessionRunId,
      )
    ) {
      return;
    }

    const freshnessSource =
      trimOptional(observeCurrentSessionSeed.completedAtUtc) ||
      trimOptional(observeCurrentSessionSeed.startedAtUtc);
    const freshnessTimestamp = Date.parse(freshnessSource);
    if (
      !Number.isFinite(freshnessTimestamp) ||
      Date.now() - freshnessTimestamp > 30_000
    ) {
      return;
    }

    const timeoutId = window.setTimeout(() => {
      void observeServiceRunsQuery.refetch();
    }, 1500);
    return () => window.clearTimeout(timeoutId);
  }, [
    observeCurrentSessionSeed,
    observeServiceRuns,
    observeServiceRunsQuery,
    studioSurface,
    workbenchPublishedServiceId,
  ]);
  useEffect(() => {
    if (
      !resolvedStudioScopeId ||
      !workbenchPublishedServiceId ||
      !observeCurrentSessionSeed
    ) {
      return;
    }

    const sessionRunId = trimOptional(observeCurrentSessionSeed.runId);
    if (
      !sessionRunId ||
      !observeServiceRuns.some(
        (run) => trimOptional(run.runId) === sessionRunId,
      )
    ) {
      return;
    }

    clearStudioObserveSessionSeed({
      scopeId: resolvedStudioScopeId,
      serviceId: workbenchPublishedServiceId,
    });
    setObserveSessionSeedsByServiceId((current) => {
      if (!current[workbenchPublishedServiceId]) {
        return current;
      }

      const next = { ...current };
      delete next[workbenchPublishedServiceId];
      return next;
    });
  }, [
    observeCurrentSessionSeed,
    observeServiceRuns,
    resolvedStudioScopeId,
    workbenchPublishedServiceId,
  ]);
  const lifecycleSurfaceSelectedMemberKey =
    studioSurface !== 'build' &&
    (workbenchMemberKey.startsWith('workflow:') ||
      workbenchMemberKey.startsWith('script:'))
      ? workbenchStudioMemberId
        ? `member:${workbenchStudioMemberId}`
        : workbenchMemberKey
      : '';
  const selectedRailMemberKey =
    buildSurfaceSelectedMemberKey ||
    lifecycleSurfaceSelectedMemberKey ||
    (studioSurface === 'build' ? lifecycleSurfaceMemberKey : workbenchMemberKey);
  const effectiveSelectedMemberKey = trimOptional(
    selectedRailMemberKey || currentFocusMemberKey,
  );
  const hasSelectedMemberFocus = Boolean(workbenchMemberKey);
  const currentMemberLabel = !hasSelectedMemberFocus
    ? 'Select a member'
    : workbenchMemberKey.startsWith('workflow:')
        ? trimOptional(activeWorkflowName) || 'Workflow member'
    : workbenchMemberKey.startsWith('script:')
        ? trimOptional(selectedScriptId) || 'Script member'
        : workbenchMemberKey.startsWith('member:')
            ? trimOptional(workbenchPublishedServiceRevision?.workflowName) ||
              trimOptional(workbenchPublishedServiceRevision?.scriptId) ||
              trimOptional(workbenchPublishedServiceRevision?.staticActorTypeName) ||
              trimOptional(workbenchPublishedService?.displayName) ||
              trimOptional(workbenchStudioMember?.displayName) ||
              trimOptional(workbenchPublishedService?.serviceId) ||
              trimOptional(routeState.memberId) ||
              'Current member'
            : trimOptional(activeWorkflowName) ||
              (isBuildScriptsSurface ? trimOptional(selectedScriptId) : '') ||
              'Current member';
  const currentMemberImplementationLabel = !hasSelectedMemberFocus
    ? ''
    : workbenchMemberKey.startsWith('member:')
      ? describeMemberImplementationLabel(
          workbenchPublishedServiceRevision?.implementationKind,
        )
      : isBuildGAgentSurface
        ? 'GAgent implementation'
        : selectedWorkflowId || templateWorkflow
          ? 'Workflow implementation'
          : selectedScriptId
            ? 'Script implementation'
            : trimOptional(selectedGAgentTypeName)
              ? 'GAgent implementation'
              : 'Member implementation';
  const currentMemberDescription = !hasSelectedMemberFocus
    ? 'Choose a member from Team members, or create a new member to start building.'
    : workbenchMemberKey.startsWith('workflow:')
        ? formatStudioAssetMeta({
            primary: currentMemberImplementationLabel,
            secondary:
              trimOptional(activeWorkflowName) ||
              trimOptional(selectedWorkflowSummary?.fileName) ||
              'Current workflow draft',
          }) || 'Studio is tracking the current workflow-backed member.'
        : workbenchMemberKey.startsWith('script:')
          ? formatStudioAssetMeta({
              primary: currentMemberImplementationLabel,
              secondary: trimOptional(selectedScriptId) || 'Current script member',
            }) || 'Studio is tracking the current script-backed member.'
        : workbenchMemberKey.startsWith('member:')
            ? formatStudioAssetMeta({
                primary: currentMemberImplementationLabel,
                secondary:
                  trimOptional(workbenchStudioMemberBinding?.publishedServiceId) ||
                  trimOptional(workbenchStudioMember?.publishedServiceId) ||
                  trimOptional(workbenchPublishedService?.serviceId) ||
                  trimOptional(routeState.memberId) ||
                  (workbenchStudioMember
                    ? formatStudioMemberLifecycleStage(
                        workbenchStudioMember.lifecycleStage,
                      )
                    : '') ||
                  trimOptional(workbenchStudioMemberBinding?.revisionId) ||
                  trimOptional(workbenchPublishedServiceRevision?.revisionId) ||
                  trimOptional(workbenchPublishedService?.deploymentStatus) ||
                  'Published member',
              }) || 'Published member ready for Bind, Invoke, or Observe.'
            : formatStudioAssetMeta({
                primary: currentMemberImplementationLabel,
                secondary:
                  trimOptional(routeState.memberId) ||
                  activeBuildFocusKey ||
                  'Current member focus',
              }) || 'Studio is tracking the current member focus.';
  const currentMemberKind: StudioShellMemberKind = 'member';
  const currentMemberTone: 'live' | 'draft' | 'idle' =
    !hasSelectedMemberFocus
      ? 'idle'
      : workbenchMemberKey.startsWith('member:')
        ? resolveServiceMemberTone(workbenchPublishedService?.deploymentStatus)
        : activeBuildFocusKey
          ? 'draft'
          : 'idle';
  const currentMemberMeta = formatStudioAssetMeta({
    primary: hasSelectedMemberFocus
      ? currentMemberImplementationLabel || 'Member focus'
      : '',
    secondary: hasSelectedMemberFocus
      ? trimOptional(workbenchStudioMemberBinding?.revisionId) ||
        trimOptional(workbenchStudioMember?.lastBoundRevisionId) ||
        trimOptional(workbenchPublishedServiceRevision?.revisionId) ||
        trimOptional(workbenchPublishedService?.serviceId) ||
        trimOptional(routeState.memberId) ||
        activeBuildFocusKey
      : '',
  });
  const currentBindingSelectionServiceId = trimOptional(
    bindingSelectionRef.current.serviceId,
  );
  const currentBindingSelectionEndpointId = trimOptional(
    bindingSelectionRef.current.endpointId,
  );
  const currentInvokeSelectionServiceId = trimOptional(
    invokeSelectionRef.current.serviceId,
  );
  const currentInvokeSelectionEndpointId = trimOptional(
    invokeSelectionRef.current.endpointId,
  );
  const currentSelectedMemberServiceId =
    workbenchPublishedServiceId;
  const comparableWorkbenchMemberKey = useMemo(
    () =>
      resolveStudioMemberOwnerKey(
        workbenchMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      ),
    [publishedScopeMembers, studioScopeMembers, workbenchMemberKey],
  );
  const comparableRecentlyBoundMemberKey = useMemo(
    () =>
      resolveStudioMemberOwnerKey(
        recentlyBoundMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      ),
    [publishedScopeMembers, recentlyBoundMemberKey, studioScopeMembers],
  );
  const recentBindSelectedMemberServiceId =
    trimOptional(comparableRecentlyBoundMemberKey) ===
    trimOptional(comparableWorkbenchMemberKey)
      ? trimOptional(recentlyBoundServiceId)
      : '';
  const bindSelectedMemberServiceId =
    currentSelectedMemberServiceId ||
    (isBindSurface ? recentBindSelectedMemberServiceId : '');
  const bindTargetService = useMemo(
    () => {
      if (!bindSelectedMemberServiceId) {
        return null;
      }

      const matchedPublishedService =
        publishedScopeServices.find(
          (service) => service.serviceId === bindSelectedMemberServiceId,
        ) ?? null;
      if (matchedPublishedService) {
        return matchedPublishedService;
      }

      return {
        serviceKey: resolvedStudioScopeId
          ? `${resolvedStudioScopeId}:${scopeServiceAppId}:${scopeServiceNamespace}:${bindSelectedMemberServiceId}`
          : bindSelectedMemberServiceId,
        tenantId: resolvedStudioScopeId || '',
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
        serviceId: bindSelectedMemberServiceId,
        displayName: currentMemberLabel || bindSelectedMemberServiceId,
        defaultServingRevisionId: '',
        activeServingRevisionId: '',
        deploymentId: '',
        primaryActorId: '',
        deploymentStatus: '',
        endpoints: [],
        policyIds: [],
        updatedAt: '',
      };
    },
    [
      bindSelectedMemberServiceId,
      currentMemberLabel,
      publishedScopeServices,
      resolvedStudioScopeId,
    ],
  );
  const bindTargetServices = useMemo(
    () => (bindTargetService ? [bindTargetService] : []),
    [bindTargetService],
  );
  const bindTargetDefaultEndpointId = useMemo(
    () => resolveStudioServiceDefaultEndpointId(bindTargetService),
    [bindTargetService],
  );
  const bindPendingCandidate =
    bindSelectedMemberServiceId ||
    !workbenchMemberKey.startsWith('workflow:')
      ? null
      : buildPendingBindCandidate;
  const bindInitialEndpointId = bindSelectedMemberServiceId
    ? currentBindingSelectionServiceId === bindSelectedMemberServiceId &&
      currentBindingSelectionEndpointId
      ? currentBindingSelectionEndpointId
      : bindTargetDefaultEndpointId
    : '';
  const hasInvokeTargetMemberSelection =
    Boolean(trimOptional(routeState.memberId)) ||
    Boolean(currentSelectedMemberServiceId) ||
    Boolean(currentInvokeSelectionServiceId) ||
    Boolean(currentBindingSelectionServiceId);
  const invokeTargetServiceId =
    currentInvokeSelectionServiceId ||
    currentBindingSelectionServiceId ||
    trimOptional(routeState.memberId) ||
    currentSelectedMemberServiceId;
  const invokeTargetService = useMemo(
    () =>
      invokeTargetServiceId
        ? runtimeConsoleServices.find(
            (service) => service.serviceId === invokeTargetServiceId,
          ) ?? null
        : null,
    [invokeTargetServiceId, runtimeConsoleServices],
  );
  const invokeTargetServices = useMemo(
    () => (invokeTargetService ? [invokeTargetService] : []),
    [invokeTargetService],
  );
  const invokeTargetLabel =
    trimOptional(invokeTargetService?.displayName) ||
    trimOptional(invokeTargetService?.serviceId) ||
    invokeTargetServiceId ||
    '';
  const invokeTargetDefaultEndpointId = useMemo(() => {
    if (!invokeTargetService) {
      return '';
    }

    return (
      invokeTargetService.endpoints.find((endpoint) => endpoint.endpointId === 'chat')
        ?.endpointId ||
      invokeTargetService.endpoints[0]?.endpointId ||
      ''
    );
  }, [invokeTargetService]);
  const invokeInitialEndpointId =
    currentInvokeSelectionServiceId === invokeTargetServiceId &&
    currentInvokeSelectionEndpointId
      ? currentInvokeSelectionEndpointId
      : currentBindingSelectionServiceId === invokeTargetServiceId &&
          currentBindingSelectionEndpointId
        ? currentBindingSelectionEndpointId
        : invokeTargetDefaultEndpointId;
  const invokeEmptyState = useMemo(() => {
    if (hasInvokeTargetMemberSelection && invokeTargetService) {
      return null;
    }

    if (hasSelectedMemberFocus && !hasInvokeTargetMemberSelection) {
      return {
        message: '当前选择还不能直接调用。',
        description:
          '调用页面只会固定到已发布的成员。请先为这个成员完成绑定，再回到这里继续调用。',
        type: 'info' as const,
      };
    }

    if (!hasInvokeTargetMemberSelection) {
      return {
        message: '请选择要调用的成员。',
        description:
          '请先在“团队成员”里选择成员，或从绑定页面继续进入，这样调用页面才会稳定固定到单个成员。',
        type: 'info' as const,
      };
    }

    return {
      message: `${
        invokeTargetLabel || '当前成员'
      } 还不能直接调用。`,
      description:
        '当前团队上下文里，这个成员还没有暴露可调用的已发布调用契约。',
      type: 'warning' as const,
    };
  }, [
    hasInvokeTargetMemberSelection,
    hasSelectedMemberFocus,
    invokeTargetLabel,
    invokeTargetService,
  ]);
  const touchMemberRecency = useCallback((memberKey: string) => {
    const normalizedMemberKey = trimOptional(memberKey);
    if (!normalizedMemberKey) {
      return;
    }

    setMemberRecencyOrder((current) => {
      if (current[0] === normalizedMemberKey) {
        return current;
      }

      const next = [
        normalizedMemberKey,
        ...current.filter((item) => item !== normalizedMemberKey),
      ];
      return next.slice(0, 32);
    });
  }, []);
  useEffect(() => {
    if (!effectiveSelectedMemberKey) {
      return;
    }

    touchMemberRecency(effectiveSelectedMemberKey);
  }, [effectiveSelectedMemberKey, touchMemberRecency]);
  useEffect(() => {
    const preferredServiceId = currentSelectedMemberServiceId;
    if (!preferredServiceId) {
      if (scopeServicesQuery.isLoading || scopeServicesQuery.isFetching) {
        return;
      }

      if (
        bindingSelectionRef.current.serviceId ||
        bindingSelectionRef.current.endpointId
      ) {
        bindingSelectionRef.current = {
          serviceId: '',
          endpointId: '',
        };
      }
      return;
    }

    const selectedService = publishedScopeServices.find(
      (service) => service.serviceId === preferredServiceId,
    );
    if (!selectedService) {
      if (scopeServicesQuery.isLoading || scopeServicesQuery.isFetching) {
        return;
      }

      bindingSelectionRef.current = {
        serviceId: '',
        endpointId: '',
      };
      return;
    }

    const fallbackEndpointId = resolveStudioServiceDefaultEndpointId(
      selectedService,
    );
    if (!fallbackEndpointId) {
      return;
    }

    const currentBindingSelection =
      bindingSelectionRef.current.serviceId === preferredServiceId &&
      bindingSelectionRef.current.endpointId
        ? bindingSelectionRef.current.endpointId
        : fallbackEndpointId;

    if (
      bindingSelectionRef.current.serviceId !== preferredServiceId ||
      bindingSelectionRef.current.endpointId !== currentBindingSelection
    ) {
      bindingSelectionRef.current = {
        serviceId: preferredServiceId,
        endpointId: currentBindingSelection,
      };
    }
  }, [
    currentSelectedMemberServiceId,
    publishedScopeServices,
    scopeServicesQuery.isFetching,
    scopeServicesQuery.isLoading,
  ]);
  const selectedInventoryMemberKey = useMemo(
    () =>
      effectiveSelectedMemberKey.startsWith('workflow:')
        ? effectiveSelectedMemberKey
        : '',
    [effectiveSelectedMemberKey],
  );
  const selectedInventoryWorkflowId = useMemo(
    () =>
      resolveWorkflowIdFromRouteValue(
        readWorkflowMemberRouteValueFromMemberKey(selectedInventoryMemberKey),
        visibleWorkflowSummaries,
        {
          allowDirectIdFallback: true,
          workflowFile: activeWorkflowFile,
        },
      ),
    [activeWorkflowFile, selectedInventoryMemberKey, visibleWorkflowSummaries],
  );
  const renameableWorkflowLabel = useMemo(() => {
    if (!selectedInventoryWorkflowId) {
      return 'current workflow member';
    }

    const selectedWorkflowSummaryForInventory = visibleWorkflowSummaries.find(
      (workflow) =>
        trimOptional(workflow.workflowId) === selectedInventoryWorkflowId,
    );
    return (
      trimOptional(selectedWorkflowSummaryForInventory?.name) ||
      trimOptional(selectedWorkflowSummaryForInventory?.fileName) ||
      'current workflow member'
    );
  }, [selectedInventoryWorkflowId, visibleWorkflowSummaries]);
  const handleSelectStudioMember = useCallback(
    async (memberKey: string) => {
      const normalizedMemberKey = trimOptional(memberKey);
      const currentSelectableMemberKey =
        studioSurface === 'build' ? currentFocusMemberKey : workbenchMemberKey;
      if (
        !normalizedMemberKey ||
        normalizedMemberKey === trimOptional(currentSelectableMemberKey)
      ) {
        return;
      }

      if (!(await confirmScriptsStudioLeave())) {
        return;
      }
      if (normalizedMemberKey.startsWith('workflow:')) {
        const workflowId = resolveWorkflowIdFromRouteValue(
          readWorkflowMemberRouteValueFromMemberKey(normalizedMemberKey),
          visibleWorkflowSummaries,
          {
            allowDirectIdFallback: true,
            workflowFile: activeWorkflowFile,
          },
        );
        if (!workflowId) {
          return;
        }

        if (studioSurface !== 'build') {
          bindingSelectionRef.current = {
            serviceId: '',
            endpointId: '',
          };
          invokeSelectionRef.current = {
            serviceId: '',
            endpointId: '',
          };
          if (studioSurface === 'observe') {
            setSelectedExecutionId('');
          }
          setSelectedWorkflowId(workflowId);
          setSelectedScriptId('');
          setTemplateWorkflow('');
          setBuildSurface('editor');
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              memberKey: normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        openWorkspaceWorkflow(workflowId);
        return;
      }

      if (normalizedMemberKey.startsWith('script:')) {
        const scriptId = normalizedMemberKey.slice('script:'.length);
        if (studioSurface !== 'build') {
          bindingSelectionRef.current = {
            serviceId: '',
            endpointId: '',
          };
          invokeSelectionRef.current = {
            serviceId: '',
            endpointId: '',
          };
          if (studioSurface === 'observe') {
            setSelectedExecutionId('');
          }
          setSelectedWorkflowId('');
          setSelectedScriptId(scriptId);
          setTemplateWorkflow('');
          setBuildSurface('scripts');
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              memberKey: normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        openScopeScript(scriptId);
        return;
      }

      if (normalizedMemberKey.startsWith('member:')) {
        const selectedMemberSummary = resolveStudioMemberSummaryFromMemberKey(
          normalizedMemberKey,
          publishedScopeMembers,
          studioScopeMembers,
        );
        const selectedMemberId = trimOptional(selectedMemberSummary?.memberId);
        const selectedMemberServiceId =
          trimOptional(selectedMemberSummary?.publishedServiceId) ||
          resolvePublishedServiceIdFromMemberKey(
            normalizedMemberKey,
            publishedScopeMembers,
            studioScopeMembers,
          );
        const selectedService = publishedScopeServices.find(
          (service) => service.serviceId === selectedMemberServiceId,
        );
        const selectedPublishedMember = publishedScopeMembers.find(
          ({ memberSummary, service }) =>
            trimOptional(memberSummary?.memberId) === selectedMemberId ||
            trimOptional(service.serviceId) === selectedMemberServiceId,
        );
        if (!selectedMemberServiceId || !selectedService) {
          return;
        }

        const selectedWorkflowIdForMember = trimOptional(
          selectedPublishedMember?.matchedWorkflow?.workflowId,
        );
        const selectedScriptIdForMember = trimOptional(
          selectedPublishedMember?.matchedScript?.script?.scriptId,
        );
        const selectedMemberOwnerKey =
          selectedWorkflowIdForMember
            ? buildWorkflowMemberKeyFromSummary(selectedPublishedMember?.matchedWorkflow)
            : selectedScriptIdForMember
              ? `script:${selectedScriptIdForMember}`
              : normalizedMemberKey;

        const defaultEndpointId =
          selectedService.endpoints.find((endpoint) => endpoint.endpointId === 'chat')
            ?.endpointId ||
          selectedService.endpoints[0]?.endpointId ||
          '';
        bindingSelectionRef.current = {
          serviceId: selectedMemberServiceId,
          endpointId: defaultEndpointId,
        };
        invokeSelectionRef.current = {
          serviceId: selectedMemberServiceId,
          endpointId: defaultEndpointId,
        };

        if (studioSurface !== 'build') {
          if (studioSurface === 'observe') {
            setSelectedExecutionId('');
          }

          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              memberKey:
                selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        if (selectedWorkflowIdForMember) {
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              memberKey: selectedMemberOwnerKey,
              tab: 'studio',
            }),
          );
          openWorkspaceWorkflow(selectedWorkflowIdForMember);
          return;
        }

        if (selectedScriptIdForMember) {
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              memberKey: selectedMemberOwnerKey,
              tab: 'scripts',
            }),
          );
          openScopeScript(selectedScriptIdForMember);
          return;
        }

        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            memberKey: selectedMemberOwnerKey,
            step: 'bind',
          }),
        );
        return;
      }

      if (normalizedMemberKey.startsWith('template:')) {
        setSelectedWorkflowId('');
        setTemplateWorkflow(normalizedMemberKey.slice('template:'.length));
        setBuildSurface('editor');
        setStudioSurface('build');
        return;
      }

    },
    [
      activeWorkflowFile,
      confirmScriptsStudioLeave,
      currentFocusMemberKey,
      currentLifecycleStep,
      openScopeScript,
      openWorkspaceWorkflow,
      publishedScopeMembers,
      publishedScopeServices,
      resolvedStudioScopeId,
      studioSurface,
      visibleWorkflowSummaries,
      workbenchMemberKey,
    ],
  );
  const memberItems = useMemo(() => {
    const items: OrderedStudioShellMemberItem[] = [];
    const seen = new Set<string>();
    const currentMemberItem: StudioShellMemberItem = {
      key: selectedRailMemberKey || currentFocusMemberKey,
      label: currentMemberLabel,
      canDelete:
        currentFocusMemberKey.startsWith('workflow:') && Boolean(selectedWorkflowId),
      canRename: currentFocusMemberKey.startsWith('workflow:'),
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
        insertionOrder: items.length,
        key: normalizedKey,
      });
    };

    for (const {
      memberSummary,
      service,
      revision: serviceRevision,
      matchedWorkflow,
      matchedScript,
    } of publishedScopeMembers) {
      const memberLifecycleLabel = memberSummary
        ? formatStudioMemberLifecycleStage(memberSummary.lifecycleStage)
        : '';
      addItem({
        key:
          `member:${trimOptional(memberSummary?.memberId) || trimOptional(memberSummary?.publishedServiceId) || service.serviceId}`,
        label:
          trimOptional(matchedWorkflow?.name) ||
          trimOptional(matchedScript?.script?.scriptId) ||
          trimOptional(memberSummary?.displayName) ||
          trimOptional(service.displayName) ||
          trimOptional(service.serviceId) ||
          'Member',
        description: formatStudioAssetMeta({
          primary: describeMemberImplementationLabel(
            memberSummary?.implementationKind || serviceRevision?.implementationKind,
          ),
          secondary:
            trimOptional(memberSummary?.description) ||
            trimOptional(matchedWorkflow?.description) ||
            trimOptional(matchedWorkflow?.fileName) ||
            trimOptional(matchedScript?.script?.definitionActorId) ||
            (serviceRevision
              ? formatStudioAssetMeta({
                  primary:
                    trimOptional(serviceRevision.workflowName) ||
                    trimOptional(serviceRevision.scriptId) ||
                    trimOptional(serviceRevision.staticActorTypeName),
                  secondary:
                    trimOptional(serviceRevision.primaryActorId) ||
                    trimOptional(service.primaryActorId),
                })
              : '') ||
            'Published member service',
        }) || 'Published member service.',
        kind: 'member',
        meta: formatStudioAssetMeta({
          primary: trimOptional(service.serviceId) || 'Published service',
          secondary:
            trimOptional(memberSummary?.lastBoundRevisionId) ||
            trimOptional(serviceRevision?.revisionId) ||
            trimOptional(memberLifecycleLabel) ||
            trimOptional(service.activeServingRevisionId) ||
            trimOptional(service.defaultServingRevisionId) ||
            trimOptional(service.deploymentStatus),
        }),
        tone: resolveServiceMemberTone(service.deploymentStatus),
      });
    }

    for (const workflow of visibleWorkflowSummaries) {
      if (serviceBackedWorkflowIds.has(trimOptional(workflow.workflowId))) {
        continue;
      }

      const workflowMemberKey = buildWorkflowMemberKeyFromSummary(workflow);
      if (!workflowMemberKey) {
        continue;
      }

      addItem({
        key: workflowMemberKey,
        label: workflow.name,
        description: formatStudioAssetMeta({
          primary: 'Workflow implementation',
          secondary:
            trimOptional(workflow.description) ||
            trimOptional(workflow.fileName) ||
            'Workspace workflow draft',
        }) || 'Workspace workflow draft',
        canDelete: true,
        canRename: true,
        kind: 'member',
        meta: formatStudioAssetMeta({
          primary: `${workflow.stepCount} steps`,
          secondary: workflow.directoryLabel || workflow.fileName,
        }),
        tone:
          currentFocusMemberKey === workflowMemberKey
            ? 'live'
            : 'idle',
      });
    }

    for (const scriptDetail of availableScopeScripts) {
      const scriptId = trimOptional(scriptDetail.script?.scriptId);
      if (!scriptId || serviceBackedScriptIds.has(scriptId)) {
        continue;
      }

      addItem({
        key: `script:${scriptId}`,
        label: scriptId,
        description: formatStudioAssetMeta({
          primary: 'Script implementation',
          secondary:
            trimOptional(scriptDetail.script?.definitionActorId) ||
            'Scope-backed script behavior',
        }) || 'Scope-backed script behavior',
        kind: 'member',
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

    const recencyIndexByKey = new Map(
      memberRecencyOrder.map((memberKey, index) => [memberKey, index]),
    );

    return [...items]
      .sort((left, right) => {
        const leftKey = trimOptional(left.key);
        const rightKey = trimOptional(right.key);
        const leftIsSelected = leftKey === effectiveSelectedMemberKey;
        const rightIsSelected = rightKey === effectiveSelectedMemberKey;
        if (leftIsSelected !== rightIsSelected) {
          return leftIsSelected ? -1 : 1;
        }

        const leftRecencyIndex = recencyIndexByKey.get(leftKey);
        const rightRecencyIndex = recencyIndexByKey.get(rightKey);
        const leftHasRecency = leftRecencyIndex !== undefined;
        const rightHasRecency = rightRecencyIndex !== undefined;
        if (leftHasRecency !== rightHasRecency) {
          return leftHasRecency ? -1 : 1;
        }

        if (
          leftRecencyIndex !== undefined &&
          rightRecencyIndex !== undefined &&
          leftRecencyIndex !== rightRecencyIndex
        ) {
          return leftRecencyIndex - rightRecencyIndex;
        }

        return left.insertionOrder - right.insertionOrder;
      })
      .map(({ insertionOrder: _insertionOrder, ...item }) => item);
  }, [
    availableScopeScripts,
    currentFocusMemberKey,
    currentMemberDescription,
    currentMemberKind,
    currentMemberLabel,
    currentMemberMeta,
    currentMemberTone,
    effectiveSelectedMemberKey,
    memberRecencyOrder,
    publishedScopeMembers,
    selectedRailMemberKey,
    serviceBackedScriptIds,
    serviceBackedWorkflowIds,
    selectedWorkflowId,
    visibleWorkflowSummaries,
  ]);
  const selectedMemberCanBind =
    Boolean(workbenchMemberKey) &&
    Boolean(
      selectedWorkflowId ||
        selectedScriptId ||
        workbenchPublishedService ||
        (isBuildGAgentSurface && trimOptional(selectedGAgentTypeName))
    );
  const selectedMemberCanInvoke =
    selectedMemberCanBind &&
    Boolean(invokeTargetServiceId) &&
    Boolean(invokeTargetDefaultEndpointId);
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
        disabled: !resolvedStudioScopeId || !selectedMemberCanBind,
      },
      {
        key: 'invoke',
        label: 'Invoke',
        description:
          'Invoke the selected member in-place and carry the trace forward into runtime runs.',
        status: currentLifecycleStep === 'invoke' ? 'active' : 'available',
        disabled: !resolvedStudioScopeId || !selectedMemberCanInvoke,
      },
      {
        key: 'observe',
        label: 'Observe',
        description:
          'Open execution traces and run posture for the selected member.',
        status: currentLifecycleStep === 'observe' ? 'active' : 'available',
      },
    ],
    [
      currentLifecycleStep,
      resolvedStudioScopeId,
      selectedMemberCanBind,
      selectedMemberCanInvoke,
    ],
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
              className={AEVATAR_INTERACTIVE_CHIP_CLASS}
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

  const currentMemberExecutions = useMemo(
    () => {
      const executions = observeServiceRuns.map((run) => toObserveExecutionSummary(run));
      if (!observeFallbackExecution) {
        return executions;
      }

      return executions.some(
        (execution) =>
          trimOptional(execution.executionId) ===
          trimOptional(observeFallbackExecution.executionId),
      )
        ? executions
        : [observeFallbackExecution, ...executions];
    },
    [observeFallbackExecution, observeServiceRuns],
  );
  const currentMemberExecutionIds = useMemo(
    () => new Set(currentMemberExecutions.map((item) => item.executionId)),
    [currentMemberExecutions],
  );
  useEffect(() => {
    if (studioSurface !== 'observe') {
      return;
    }

    if (observeServiceRunsQuery.isLoading) {
      return;
    }

    if (!workbenchPublishedServiceId) {
      if (selectedExecutionId) {
        setSelectedExecutionId('');
      }
      return;
    }

    if (currentMemberExecutions.length === 0) {
      if (selectedExecutionId) {
        setSelectedExecutionId('');
      }
      return;
    }

    if (
      !selectedExecutionId ||
      !currentMemberExecutionIds.has(selectedExecutionId)
    ) {
      setSelectedExecutionId(currentMemberExecutions[0]?.executionId ?? '');
    }
  }, [
    currentMemberExecutionIds,
    currentMemberExecutions,
    observeServiceRunsQuery.isLoading,
    selectedExecutionId,
    studioSurface,
    workbenchPublishedServiceId,
  ]);
  const selectedExecutionInCurrentMember =
    Boolean(selectedExecutionId) &&
    currentMemberExecutionIds.has(selectedExecutionId);
  const selectedExecutionQuery = {
    data:
      selectedExecutionInCurrentMember && selectedObserveRunAuditQuery.data
        ? toObserveExecutionDetail(selectedObserveRunAuditQuery.data)
        : selectedExecutionInCurrentMember && selectedObserveFallbackExecution
          ? selectedObserveFallbackExecution
        : undefined,
    error: selectedObserveRunAuditQuery.error,
    isError: selectedObserveRunAuditQuery.isError,
    isLoading: selectedObserveRunAuditQuery.isLoading,
  };
  const observeSelectedExecution = selectedExecutionInCurrentMember
    ? selectedExecutionQuery
    : {
        data: undefined,
        error: null,
        isError: false,
        isLoading: false,
      };
  const observeExecutionList = {
    data: currentMemberExecutions,
    error: observeServiceRunsQuery.error,
    isError: observeServiceRunsQuery.isError,
    isLoading: observeServiceRunsQuery.isLoading,
  };
  const observeImplementationKind = normalizeStudioMemberBindingImplementationKind(
    workbenchPublishedServiceRevision?.implementationKind,
  );
  const observeCurrentImplementationLabel =
    trimOptional(observeSelectedExecution.data?.workflowName) ||
    (observeImplementationKind === 'workflow'
      ? trimOptional(workbenchPublishedServiceRevision?.workflowName)
      : '') ||
    currentMemberImplementationLabel;
  const executionCanStop = isExecutionStopAllowed(
    selectedExecutionQuery.data?.status ||
      (selectedObserveBackendRunSummary
        ? normalizeObserveRunStatus(selectedObserveBackendRunSummary.completionStatus)
        : selectedObserveFallbackExecution?.status),
  );
  const observeEmptyState = useMemo(() => {
    if (!hasSelectedMemberFocus) {
      return {
        title: 'Select a member to observe.',
        description:
          'Choose a member from Team members first so Observe stays pinned to one member context.',
      };
    }

    if (!workbenchPublishedServiceId) {
      return {
        title: `${currentMemberLabel || 'Current member'} is not bound yet.`,
        description:
          'Publish or bind this member first, then Studio can load its runtime runs and audit trail here.',
      };
    }

    if (
      !observeServiceRunsQuery.isLoading &&
      currentMemberExecutions.length === 0
    ) {
      return {
        title: `No runs for ${currentMemberLabel || 'this member'} yet.`,
        description:
          observeImplementationKind === 'workflow'
            ? 'Invoke this member, or start a workflow draft run, then return here to inspect the current member history.'
            : 'Invoke this member first, then return here to inspect the current member history.',
      };
    }

    return null;
  }, [
    currentMemberExecutions.length,
    currentMemberLabel,
    hasSelectedMemberFocus,
    observeImplementationKind,
    observeServiceRunsQuery.isLoading,
    workbenchPublishedServiceId,
  ]);
  const showWorkflowEntryEmptyState =
    isBuildEditorSurface &&
    !selectedWorkflowId &&
    !templateWorkflow &&
    !workflowsQuery.isLoading &&
    (visibleWorkflowSummaries.length === 0 ||
      Boolean(trimOptional(routeState.memberId))) &&
    (!appContextQuery.data?.features.scripts || !scopeScriptsQuery.isLoading);
  const studioContextPrimaryTitle =
    showWorkflowEntryEmptyState
      ? hasSelectedMemberFocus
        ? currentMemberLabel
        : 'Select a member'
      : isBuildEditorSurface
        ? activeWorkflowName || templateWorkflow || 'Workflow 构建'
      : isBuildGAgentSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : 'GAgent 构建'
        : isBuildScriptsSurface
          ? selectedScriptId || 'Script 构建'
        : isObserveSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : 'Select a member'
        : isBindSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : '成员绑定'
          : isInvokeSurface
            ? currentMemberLabel || '成员调用'
            : pageTitle;
  const studioContextDescriptor =
    showWorkflowEntryEmptyState
      ? hasSelectedMemberFocus
        ? '当前 member 还没有可继续编辑的 Build surface。你可以先去 Bind / Invoke，或显式创建新的 member。'
        : memberItems.length > 0
        ? '先从左侧选一个已有 member，再继续 Build；如果要新增，再显式点击 Create member。'
        : '这个 team 还没有 member。显式点击 Create member，再进入新的实现草稿。'
      : isBuildEditorSurface
        ? '围绕当前 member 的 workflow canvas、step detail 和 dry-run 继续构建'
        : isBuildGAgentSurface
          ? '在 Build 内定义 GAgent 类型、角色、初始 prompt、工具和状态持久化'
        : isBuildScriptsSurface
          ? '围绕 script source、diagnostics 和 dry-run 继续迭代当前 member'
        : isObserveSurface
          ? '围绕当前 member 的最近运行、回放和基线继续观察'
          : isBindSurface
            ? '确认当前 member 的 published contract，并继续去 Invoke'
            : isInvokeSurface
              ? '调用当前成员并保留运行观察上下文'
              : '成员工作台';
  const studioBoundServiceLabel =
    hasSelectedMemberFocus
      ? trimOptional(routeState.memberId) ||
        trimOptional(workbenchPublishedService?.serviceId) ||
        'No bound service'
      : '';
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
        serviceId:
          trimOptional(routeState.memberId) ||
          trimOptional(workbenchPublishedService?.serviceId) ||
          undefined,
      })
    : buildTeamsHref();
  const studioReturnLabel = '返回团队';
  const currentStudioReturnTo =
    typeof window === 'undefined'
      ? ''
      : sanitizeReturnTo(
          `${window.location.pathname}${window.location.search}${window.location.hash}`,
        );
  const createMemberButtonDisabled = inventoryBusyKey === 'create';
  const selectedInventoryMemberBusy =
    inventoryBusyKey === selectedInventoryMemberKey;
  const selectedInventoryBusyAction = selectedInventoryMemberBusy
    ? inventoryBusyAction
    : '';
  const inventoryActions = (
    <div style={inventoryActionsStyle}>
      <div style={inventoryActionRowStyle}>
        <Button
          aria-label="Create member"
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={createMemberButtonDisabled}
          loading={inventoryBusyAction === 'create'}
          onClick={() => void openCreateMemberFlow()}
          style={{
            ...inventoryActionPrimaryButtonStyle,
            cursor: createMemberButtonDisabled ? 'not-allowed' : 'pointer',
            opacity: createMemberButtonDisabled ? 0.56 : 1,
          }}
        >
          Create member
        </Button>
        <Button
          aria-label={`Rename ${renameableWorkflowLabel}`}
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={!selectedInventoryMemberKey || selectedInventoryMemberBusy}
          loading={selectedInventoryBusyAction === 'rename'}
          onClick={() =>
            selectedInventoryMemberKey
              ? void handleRenameWorkflowMember(selectedInventoryMemberKey)
              : undefined
          }
          style={{
            ...inventoryActionButtonStyle,
            cursor:
              !selectedInventoryMemberKey || selectedInventoryMemberBusy
                ? 'not-allowed'
                : 'pointer',
            opacity:
              !selectedInventoryMemberKey || selectedInventoryMemberBusy ? 0.56 : 1,
          }}
        >
          Rename
        </Button>
        <Button
          aria-label={`Delete ${renameableWorkflowLabel}`}
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={!selectedInventoryMemberKey || selectedInventoryMemberBusy}
          loading={selectedInventoryBusyAction === 'delete'}
          onClick={() =>
            selectedInventoryMemberKey
              ? void handleDeleteWorkflowMember(selectedInventoryMemberKey)
              : undefined
          }
          style={{
            ...inventoryActionDangerButtonStyle,
            cursor:
              !selectedInventoryMemberKey || selectedInventoryMemberBusy
                ? 'not-allowed'
                : 'pointer',
            opacity:
              !selectedInventoryMemberKey || selectedInventoryMemberBusy ? 0.56 : 1,
          }}
        >
          Delete
        </Button>
      </div>
      {selectedInventoryMemberKey ? (
        <div style={inventorySelectionPillStyle}>
          <span style={inventorySelectionLabelStyle}>Selected</span>
          <span style={inventorySelectionValueStyle}>{renameableWorkflowLabel}</span>
        </div>
      ) : (
        <div style={inventoryActionsHintStyle}>
          Create a member here. Workflow now registers backend member authority;
          Script / GAgent will move into the same flow next.
        </div>
      )}
    </div>
  );
  const buildEmptyStateContent = showWorkflowEntryEmptyState ? (
    <div
      data-testid="studio-empty-member-state"
      style={memberEmptyStatePanelStyle}
    >
      <div style={{ display: 'grid', gap: 8 }}>
        <h2 style={memberEmptyStateTitleStyle}>
          {hasSelectedMemberFocus
            ? `${currentMemberLabel} is not build-ready here`
            : memberItems.length > 0
              ? 'Select a team member'
              : 'Create your first team member'}
        </h2>
        <p style={memberEmptyStateBodyStyle}>
          {hasSelectedMemberFocus
            ? 'This selected member does not currently expose an editable Build surface in Studio. Continue in Bind or Invoke, or create a new member to start from Build.'
            : memberItems.length > 0
            ? 'Pick an existing member from Team members to continue in Studio, or explicitly create a new member here.'
            : 'Studio no longer creates an implicit draft on entry. Create a member when you are ready to start building.'}
        </p>
      </div>
      <div style={memberEmptyStateActionsStyle}>
        <Button
          aria-label="Create member from empty state"
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={createMemberButtonDisabled}
          loading={inventoryBusyAction === 'create'}
          onClick={() => void openCreateMemberFlow()}
          style={{
            ...inventoryActionPrimaryButtonStyle,
            cursor: createMemberButtonDisabled ? 'not-allowed' : 'pointer',
            opacity: createMemberButtonDisabled ? 0.56 : 1,
          }}
        >
          Create member
        </Button>
        <span style={inventoryActionsHintStyle}>
          {hasSelectedMemberFocus
            ? 'Bind and Invoke stay available for this member even when Build is not.'
            : memberItems.length > 0
            ? 'You can also pick an existing member from the left rail.'
            : 'Only explicit Create member should open a new implementation draft now.'}
        </span>
      </div>
    </div>
  ) : null;
  const studioContextBar = (
    <div
      data-testid="studio-context-bar"
      style={{
        alignItems: 'center',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 12,
        padding: '8px 16px 4px',
      }}
    >
      <button
        aria-label={studioReturnLabel}
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
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
          alignItems: 'center',
          display: 'flex',
          gap: 10,
          minWidth: 0,
        }}
      >
        <div
          data-testid="studio-context-title"
          style={{
            color: '#1d2129',
            fontSize: 16,
            fontWeight: 700,
            letterSpacing: '-0.02em',
            lineHeight: '22px',
            minWidth: 0,
          }}
        >
          {studioContextPrimaryTitle}
        </div>
      </div>
      {studioContextMetaParts.length > 0 ? (
        <div
          data-testid="studio-context-meta"
          style={visuallyHiddenStyle}
        >
          {studioContextMetaParts.join(' · ')}
        </div>
      ) : null}
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
      selectedGraphNodeId={effectiveSelectedGraphNodeId}
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
      onDeleteWorkflowNodes={handleRemoveWorkflowNodes}
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
      currentMemberLabel={currentMemberLabel}
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
      {showWorkflowEntryEmptyState ? (
        buildEmptyStateContent
      ) : (
        <>
          {buildModeCards}
          <div
            style={{
              display: 'flex',
              flex: 1,
              flexDirection: 'column',
              minHeight: 0,
              minWidth: 0,
            }}
          >
            {activeBuildMode === 'workflow'
              ? workflowBuildContent
              : activeBuildMode === 'script'
                ? scriptBuildContent
                : gAgentBuildContent}
          </div>
        </>
      )}
    </div>
  ) : null;

  const currentPageContent =
    isBuildSurface ? (
      buildPageContent
    ) : isObserveSurface ? (
      <StudioExecutionPage
        executions={observeExecutionList}
        selectedExecution={observeSelectedExecution}
        workflowGraph={workflowGraph}
        draftWorkflowName={draftWorkflowName}
        activeWorkflowName={activeWorkflowName}
        activeWorkflowDescription={activeWorkflowDescription}
        activeDirectoryLabel={activeDirectoryLabel}
        selectedMemberLabel={currentMemberLabel}
        currentImplementationLabel={observeCurrentImplementationLabel}
        currentImplementationKind={observeImplementationKind}
        emptyState={observeEmptyState}
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
      <StudioMemberBindPanel
        authSession={authSessionQuery.data}
        buildWorkflowYamls={
          activeBuildMode === 'workflow' &&
          selectedBuildRepresentsPublishedMember &&
          trimOptional(draftYaml)
            ? buildWorkflowYamlBundle
            : null
        }
        initialEndpointId={bindInitialEndpointId}
        memberId={workbenchStudioMemberId || undefined}
        initialServiceId={bindSelectedMemberServiceId}
        onBindPendingCandidate={handleBindPendingCandidate}
        onContinueToInvoke={handleUseBindingEndpoint}
        onSelectionChange={handleBindingSelectionChange}
        pendingBindingCandidate={bindPendingCandidate}
        preferredServiceId={bindSelectedMemberServiceId}
        scopeId={resolvedStudioScopeId}
        servicesLoading={scopeServicesQuery.isLoading || scopeServicesQuery.isFetching}
        services={bindTargetServices}
      />
    ) : isInvokeSurface ? (
      <StudioMemberInvokePanel
        emptyState={invokeEmptyState}
        memberId={workbenchStudioMemberId || undefined}
        memberRevision={invokeTargetServiceId
          ? currentServiceRevisionByServiceId.get(invokeTargetServiceId) ?? null
          : null}
        onObserveSessionChange={handleObserveSessionChange}
        onSelectionChange={handleInvokeSelectionChange}
        returnTo={currentStudioReturnTo || undefined}
        selectedMemberLabel={invokeTargetLabel || undefined}
        scopeId={resolvedStudioScopeId}
        initialEndpointId={invokeInitialEndpointId}
        initialServiceId={invokeTargetServiceId}
        services={invokeTargetServices}
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
          <>
            <StudioShell
              contentOverflow="auto"
              contextBar={studioContextBar}
              currentLifecycleStep={currentLifecycleStep}
              inventoryActions={inventoryActions}
              lifecycleSteps={lifecycleSteps}
              members={memberItems}
              onSelectLifecycleStep={handleSelectLifecycleStep}
              onSelectMember={handleSelectStudioMember}
              pageTitle={pageTitle}
              selectedMemberKey={selectedRailMemberKey}
              showPageHeader={false}
            >
              {currentPageContent}
            </StudioShell>
            <Modal
              open={createMemberModalOpen}
              title="Create member"
              onCancel={closeCreateMemberFlow}
              onOk={() => void handleCreateMember()}
              okText="Create member"
              okButtonProps={{
                disabled:
                  inventoryBusyAction === 'create' ||
                  !trimOptional(createMemberName) ||
                  createMemberKind !== 'workflow' ||
                  !trimOptional(
                    trimOptional(createMemberDirectoryId) || inventoryDirectoryId,
                  ),
                loading: inventoryBusyAction === 'create',
              }}
              cancelButtonProps={{
                disabled: inventoryBusyAction === 'create',
              }}
            >
              <div style={inventoryCreateModalStackStyle}>
                <div style={inventoryCreateFieldStackStyle}>
                  <div style={inventoryCreateFieldLabelStyle}>Member type</div>
                  <div style={inventoryCreateTypeRowStyle}>
                    {(
                      [
                        ['workflow', 'Workflow'],
                        ['script', 'Script'],
                        ['gagent', 'GAgent'],
                      ] as const
                    ).map(([kind, label]) => (
                      <button
                        key={kind}
                        aria-label={`Create ${label} member`}
                        aria-pressed={createMemberKind === kind}
                        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                        type="button"
                        style={
                          createMemberKind === kind
                            ? {
                                ...inventoryCreateTypeChipActiveStyle,
                                cursor: 'pointer',
                              }
                            : {
                                ...inventoryCreateTypeChipStyle,
                                cursor: 'pointer',
                              }
                        }
                        onClick={() => setCreateMemberKind(kind)}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  <div style={inventoryCreateHintStyle}>
                    Choose the implementation kind first. Workflow entry now
                    registers a backend member authority; Script and GAgent will
                    move into this modal once their Build handoff is ready.
                  </div>
                </div>
                <label style={inventoryCreateFieldStackStyle}>
                  <span style={inventoryCreateFieldLabelStyle}>Member name</span>
                  <input
                    aria-label="Member name"
                    autoFocus
                    onChange={(event) => setCreateMemberName(event.target.value)}
                    placeholder={suggestedCreateWorkflowName}
                    style={inventoryCreateInputStyle}
                    type="text"
                    value={createMemberName}
                  />
                </label>
                <div style={inventoryCreateHintStyle}>
                  {createMemberKind === 'workflow'
                    ? 'Workflow members currently start from a blank workflow draft with an empty canvas, and Studio also registers the member authority in backend once the draft is created.'
                    : createMemberKind === 'script'
                      ? 'Script member authority exists on backend, but this modal still hands off through Build > Script for implementation editing.'
                      : 'GAgent member authority exists on backend, but this modal still hands off through Build > GAgent for implementation editing.'}
                </div>
                {createMemberKind === 'workflow' ? (
                  <label style={inventoryCreateFieldStackStyle}>
                    <span style={inventoryCreateFieldLabelStyle}>Workflow directory</span>
                    <select
                      aria-label="Workflow directory"
                      onChange={(event) => setCreateMemberDirectoryId(event.target.value)}
                      style={inventoryCreateInputStyle}
                      value={createMemberDirectoryId}
                    >
                      <option value="" disabled>
                        Select a workflow directory
                      </option>
                      {inventoryDirectoryOptions.map((directory) => (
                        <option key={directory.directoryId} value={directory.directoryId}>
                          {directory.label}
                        </option>
                      ))}
                    </select>
                    <div style={inventoryCreateHintStyle}>
                      {selectedCreateDirectory?.path
                        ? `${selectedCreateDirectory.label} · ${selectedCreateDirectory.path}`
                        : 'Add a workflow directory in Config before creating a workflow draft from this entry.'}
                    </div>
                  </label>
                ) : null}
              </div>
            </Modal>
          </>
        )}
      </StudioBootstrapGate>
    </PageContainer>
  );
};

export default StudioPage;
