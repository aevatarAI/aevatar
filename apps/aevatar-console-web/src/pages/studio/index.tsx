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
  normalizeAsyncOperationState,
  probeAsyncOperation,
} from '@/shared/asyncOperations';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  extractRunFinishedOutput,
  type RuntimeEvent,
} from '@/shared/agui/runtimeEventSemantics';
import {
  buildConversationHeaders,
  resolveSavedConversationLlmConfig,
} from '../chat/chatConversationConfig';
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
  isChatServiceEndpoint,
  scopeServiceAppId,
  scopeServiceNamespace,
} from '@/shared/runs/scopeConsole';
import {
  applyStepInspectorDraft,
  cloneStudioWorkflowDocument,
  connectStepToTarget,
  insertStepByType,
  normalizeStepParametersForType,
  removeStep,
  removeSteps,
  suggestBranchLabelForStep,
  type StudioStepInspectorDraft,
} from '@/shared/studio/document';
import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
} from '@/shared/studio/graph';
import {
  confirmInteractiveExplicitRequestPreview,
  createWorkflowRevisionIdentityCandidate,
} from '@/shared/studio/explicitRequestConfirmation';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import {
  isStudioMemberNotFound,
  StudioMemberDeletionNotConfirmedError,
  waitForStudioMemberDeletion,
} from '@/shared/studio/memberDeletion';
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
import type { ScopeWorkflowSummary } from '@/shared/models/scopes';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import {
  buildRuntimeGAgentKindValue,
  matchesRuntimeGAgentKindDescriptor,
} from '@/shared/models/runtime/gagents';
import {
  getScopeServiceCurrentRevision,
  toScopeServiceRunAuditSnapshot,
  toScopeServiceRunSummary,
  type ScopeServiceRunAuditSnapshot,
  type ScopeServiceRunAuditStep,
  type ScopeServiceRunSummary,
} from '@/shared/models/runtime/scopeServices';
import type { ServiceCatalogSnapshot } from '@/shared/models/services';
import type {
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioMemberBindingAcceptedResponse,
  StudioMemberBindingRunStatusResponse,
  StudioMemberRoster,
  StudioMemberBindingRevision,
  StudioMemberSummary,
  StudioTeamSummary,
  StudioValidationFinding,
  StudioWorkflowDraftCreateAcceptedReceipt,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowDirectory,
  StudioWorkflowSaveResult,
} from '@/shared/studio/models';
import {
  formatStudioMemberLifecycleStage,
  normalizeStudioMemberLifecycleStage,
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
  AevatarBackButton,
  AevatarBreadcrumb,
  type AevatarBreadcrumbItem,
} from '@/shared/ui/aevatarPageShells';
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
  type StudioGAgentBuildState,
  type StudioScriptBuildState,
  type StudioPendingScriptDraft,
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
import { t } from "@/shared/i18n/messages";

type StudioRouteState = {
  scopeId: string;
  teamId: string;
  memberKey: string;
  memberId: string;
  legacyServiceId: string;
  step: StudioStep;
  focusKey: string;
  tab: StudioTab;
  intent: StudioIntent | '';
  prompt: string;
  executionId: string;
  logsMode: '' | 'popout';
  returnTo: string;
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
  legacyServiceId: string;
};

type BuildMode = 'workflow' | 'script' | 'gagent';
type BuildSurface = 'editor' | 'scripts' | 'gagent';
type StudioSurface = 'build' | 'bind' | 'invoke' | 'observe';

type DraftSaveNotice = {
  readonly type: 'success' | 'info' | 'error';
  readonly message: string;
};

type InventoryBusyAction = '' | 'create' | 'rename' | 'delete' | 'entry';

type DraftRunNotice = {
  readonly type: 'success' | 'error';
  readonly message: string;
};

const STUDIO_SCRIPT_DRAFT_STORAGE_KEY = 'aevatar:studio:script-drafts:v1';

type StudioNotice = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type StudioTeamEntryCandidate = {
  readonly memberId: string;
  readonly scopeId: string;
  readonly teamId: string;
};

const studioTeamEntryVisibilityAttempts = 5;
const studioTeamEntryVisibilityRetryDelayMs = 100;

type StudioBindingRunOutcome =
  | {
      readonly kind: 'succeeded';
      readonly run: StudioMemberBindingRunStatusResponse;
    }
  | {
      readonly kind: 'pending';
      readonly run: StudioMemberBindingRunStatusResponse | null;
    };

const MEMBER_BINDING_RUN_POLL_ATTEMPTS = 8;
const WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS = 10;
const WORKFLOW_DRAFT_MATERIALIZATION_DELAY_MS = 900;
const SAVED_WORKFLOW_QUERY_STALE_MS = 30_000;

// Refactor (iter160/cluster-1200): member binding run waiting uses shared
//   probeAsyncOperation normalized states instead of duplicated page-local
//   status mapping. The fixed timeout remains pre-refactor page-local pacing;
//   the shared helper accepts an injectable scheduler for deterministic tests.
function waitForAsyncOperationProbeTick(): Promise<void> {
  return new Promise((resolve) => {
    window.setTimeout(resolve, 900);
  });
}

function normalizeStudioMemberBindingRunState(
  run: StudioMemberBindingRunStatusResponse | null,
) {
  return normalizeAsyncOperationState({
    accepted: true,
    observation: run,
    observationStatus:
      run?.status === 'succeeded'
        ? 'succeeded'
        : run?.status === 'failed'
          ? 'failed'
          : run?.status === 'rejected'
            ? 'rejected'
            : run
              ? 'pending'
              : null,
    stateVersion: run?.stateVersion ?? null,
    message:
      run?.failure?.message ||
      (run?.status === 'rejected'
        ? 'Binding request was rejected by the member authority.'
        : run?.status === 'failed'
          ? 'Binding failed while publishing the member contract.'
          : ''),
  });
}

function buildStudioMemberBindingFailureMessage(
  run: StudioMemberBindingRunStatusResponse,
): string {
  return normalizeStudioMemberBindingRunState(run).message;
}

function resolveStudioMemberBindingRunOutcome(
  run: StudioMemberBindingRunStatusResponse | null,
): StudioBindingRunOutcome {
  const state = normalizeStudioMemberBindingRunState(run);
  if (state.status === 'failed' && run) {
    throw new Error(buildStudioMemberBindingFailureMessage(run));
  }

  if (state.status === 'observed' && run) {
    return { kind: 'succeeded', run };
  }

  return { kind: 'pending', run };
}

export function buildStudioMemberBindingPendingNotice(
  displayName: string,
  run: StudioMemberBindingRunStatusResponse | null,
): StudioNotice {
  const state = normalizeStudioMemberBindingRunState(run);
  const status = run?.status ? ` Current status: ${run.status}.` : '';
  const freshness =
    state.freshness === 'observed' && state.stateVersion != null
      ? ` Read model observed v${state.stateVersion}.`
      : state.freshness === 'accepted-only'
        ? ' Read model has not materialized this run yet.'
        : ' Status read model is still catching up.';
  return {
    message: t("pages.studio.index.binding.request.was.accepted.and.is", "{value1} binding request was accepted and is still running.{value2}{value3} Studio will keep refreshing the status before treating it as bound.", { value1: displayName, value2: status, value3: freshness }),
    type: 'info',
  };
}

type OrderedStudioShellMemberItem = StudioShellMemberItem & {
  readonly insertionOrder: number;
};

function studioMemberSummaryMatches(
  left: StudioMemberSummary | undefined,
  right: StudioMemberSummary,
): boolean {
  return Boolean(
    left &&
      trimOptional(left.memberId) === trimOptional(right.memberId) &&
      trimOptional(left.scopeId) === trimOptional(right.scopeId) &&
      trimOptional(left.displayName) === trimOptional(right.displayName) &&
      trimOptional(left.description) === trimOptional(right.description) &&
      normalizeStudioMemberBindingImplementationKind(left.implementationKind) ===
        normalizeStudioMemberBindingImplementationKind(right.implementationKind) &&
      trimOptional(left.teamId) === trimOptional(right.teamId) &&
      trimOptional(left.publishedServiceId) === trimOptional(right.publishedServiceId) &&
      trimOptional(left.lastBoundRevisionId) === trimOptional(right.lastBoundRevisionId),
  );
}

function upsertStudioMemberSummary(
  members: readonly StudioMemberSummary[],
  member: StudioMemberSummary,
): StudioMemberSummary[] {
  const normalizedMemberId = trimOptional(member.memberId);
  let matched = false;
  const nextMembers = members.map((currentMember) => {
    if (
      normalizedMemberId &&
      trimOptional(currentMember.memberId) === normalizedMemberId
    ) {
      matched = true;
      return {
        ...currentMember,
        ...member,
      };
    }

    return currentMember;
  });

  return matched ? nextMembers : [member, ...nextMembers];
}

function isStudioMemberVisibleForRoster(
  member: StudioMemberSummary,
  scopeId: string,
  teamId: string,
): boolean {
  if (trimOptional(member.scopeId) !== trimOptional(scopeId)) {
    return false;
  }

  const normalizedTeamId = trimOptional(teamId);
  return !normalizedTeamId || trimOptional(member.teamId) === normalizedTeamId;
}

function mergeOptimisticStudioMembers(
  members: readonly StudioMemberSummary[],
  optimisticMembers: readonly StudioMemberSummary[],
  scopeId: string,
  teamId: string,
): StudioMemberSummary[] {
  if (!optimisticMembers.length) {
    return [...members];
  }

  return optimisticMembers
    .filter((member) => isStudioMemberVisibleForRoster(member, scopeId, teamId))
    .reduce(
      (current, member) => upsertStudioMemberSummary(current, member),
      [...members],
    );
}

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
  background: 'rgba(240, 237, 228, 0.6)',
  border: '1px solid transparent',
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
  letterSpacing: 0,
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
  background: '#ffffff',
  border: '1px solid #e5e0d4',
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
  background: '#17130c',
  border: '1px solid #17130c',
  color: '#fbfaf6',
};

const inventoryActionDangerButtonStyle: React.CSSProperties = {
  ...inventoryActionButtonStyle,
  background: 'rgba(255, 245, 245, 0.98)',
  border: '1px solid rgba(248, 113, 113, 0.24)',
  color: '#b91c1c',
};

const inventoryEntryButtonStyle: React.CSSProperties = {
  ...inventoryActionButtonStyle,
  background: '#eff6ff',
  border: '1px solid #bfdbfe',
  color: '#1d4ed8',
};

const inventoryEntryPillStyle: React.CSSProperties = {
  ...inventorySelectionPillStyle,
  background: '#ecfdf3',
  border: '1px solid rgba(34, 197, 94, 0.34)',
  color: '#166534',
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
  letterSpacing: 0,
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
  letterSpacing: 0,
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

function splitWorkflowYamlBundle(workflowYamls: readonly string[]): {
  readonly inlineWorkflowYamls: Record<string, string>;
  readonly workflowYaml: string;
} {
  const [workflowYaml, ...inlineWorkflowYamls] = workflowYamls;
  if (!workflowYaml) {
    throw new Error('Workflow YAML is required.');
  }

  return {
    workflowYaml,
    inlineWorkflowYamls: Object.fromEntries(
      inlineWorkflowYamls.map((yaml, index) => [`workflow_${index + 1}`, yaml]),
    ),
  };
}

function normalizeWorkflowSaveResult(
  result: StudioWorkflowSaveResult | StudioWorkflowFile,
): StudioWorkflowSaveResult {
  if ('kind' in result) {
    return result;
  }

  return {
    kind: 'materialized',
    workflow: result,
  };
}

function describeWorkflowDraftAcceptedReceipt(
  receipt: StudioWorkflowDraftCreateAcceptedReceipt,
): string {
  return (
    trimOptional(receipt.readiness.message) ||
    'Workflow draft was accepted. Studio is waiting for the scoped workspace projection.'
  );
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    globalThis.setTimeout(resolve, ms);
  });
}

function waitForWorkflowDraftMaterializationTick(): Promise<void> {
  const testEnvironment =
    typeof process !== 'undefined' && process.env.NODE_ENV === 'test';
  return delay(testEnvironment ? 0 : WORKFLOW_DRAFT_MATERIALIZATION_DELAY_MS);
}

async function waitForWorkflowDraftMaterialized(input: {
  readonly receipt: StudioWorkflowDraftCreateAcceptedReceipt;
  readonly scopeId: string;
}): Promise<StudioWorkflowFile> {
  let lastNotFound: unknown = null;

  for (
    let attempt = 0;
    attempt < WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS;
    attempt += 1
  ) {
    try {
      return await studioApi.getWorkflowDraftFile(
        input.receipt.workflowId,
        input.scopeId,
      );
    } catch (error) {
      if (!isStudioApiStatus(error, 404)) {
        throw error;
      }

      lastNotFound = error;
      if (attempt < WORKFLOW_DRAFT_MATERIALIZATION_ATTEMPTS - 1) {
        await waitForWorkflowDraftMaterializationTick();
      }
    }
  }

  throw lastNotFound instanceof Error
    ? new Error(
        'Workflow draft was accepted but is not readable yet. Retry saving in a moment.',
        { cause: lastNotFound },
      )
    : new Error(
        'Workflow draft was accepted but is not readable yet. Retry saving in a moment.',
      );
}

function normalizeComparableText(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function hasTeamEntryMember(
  summary: StudioTeamSummary | null | undefined,
  memberId: string,
): boolean {
  return trimOptional(summary?.entryMemberId) === trimOptional(memberId);
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

function resolveWorkflowIdFromMemberWorkflowReference(
  input: {
    readonly workflowId?: string | null;
    readonly memberId?: string | null;
    readonly displayName?: string | null;
  },
  workflows: ReadonlyArray<{
    readonly workflowId: string;
    readonly name: string;
    readonly fileName: string;
    readonly description?: string;
  }>,
  workflowFile?: Pick<StudioWorkflowFile, 'workflowId' | 'name' | 'fileName'> | null,
): string {
  const typedWorkflowId = trimOptional(input.workflowId);
  const memberId = trimOptional(input.memberId);
  if (typedWorkflowId) {
    const resolvedWorkflowId = resolveWorkflowIdFromRouteValue(typedWorkflowId, workflows, {
      allowDirectIdFallback: false,
      workflowFile,
    });
    if (resolvedWorkflowId) {
      return resolvedWorkflowId;
    }

    return !memberId ||
      normalizeComparableText(typedWorkflowId) === normalizeComparableText(memberId)
      ? resolveWorkflowIdFromRouteValue(typedWorkflowId, workflows, {
          allowDirectIdFallback: true,
          workflowFile,
        })
      : '';
  }

  if (memberId) {
    const resolvedByMemberId = resolveWorkflowIdFromRouteValue(memberId, workflows, {
      allowDirectIdFallback: false,
      workflowFile,
    });
    if (resolvedByMemberId) {
      return resolvedByMemberId;
    }
  }

  const displayName = trimOptional(input.displayName);
  if (displayName) {
    const resolvedByDisplayName = resolveWorkflowIdFromRouteValue(displayName, workflows, {
      allowDirectIdFallback: false,
      workflowFile,
    });
    if (resolvedByDisplayName) {
      return resolvedByDisplayName;
    }
  }

  return memberId
    ? resolveWorkflowIdFromRouteValue(memberId, workflows, {
        allowDirectIdFallback: true,
        workflowFile,
      })
    : '';
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

  return directoryLabel || fileName || t("pages.studio.index.copy", "Current workspace");
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
          legacyServiceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '', legacyServiceId: '' };
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
          legacyServiceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '', legacyServiceId: '' };
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
          legacyServiceId: '',
        }
      : { key: '', kind: 'none', value: '', memberId: '', serviceId: '', legacyServiceId: '' };
  }

  return {
    key: '',
    kind: 'none',
    value: '',
    memberId: '',
    serviceId: '',
    legacyServiceId: '',
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

  const legacyServiceId = trimOptional(params.get('memberId'));
  return legacyServiceId
    ? {
        key: `member:${legacyServiceId}`,
        kind: 'member',
        value: legacyServiceId,
        memberId: '',
        serviceId: '',
        legacyServiceId,
      }
    : { key: '', kind: 'none', value: '', memberId: '', serviceId: '', legacyServiceId: '' };
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

function readStudioReturnToParam(params: URLSearchParams): string {
  const returnTo = trimOptional(params.get('returnTo'));
  return returnTo ? sanitizeReturnTo(returnTo) : '';
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

function buildScriptIdSlug(scriptName: string): string {
  return trimOptional(scriptName)
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64);
}

function buildInventoryScriptName(
  scripts: ReadonlyArray<ScopedScriptDetail>,
  members: ReadonlyArray<StudioMemberSummary>,
): string {
  const usedIds = new Set<string>();
  for (const detail of scripts) {
    const scriptId = buildScriptIdSlug(detail.script?.scriptId || '');
    if (scriptId) {
      usedIds.add(scriptId);
    }
  }

  for (const member of members) {
    const scriptId =
      member.implementationKind === 'script'
        ? buildScriptIdSlug(member.memberId) ||
          buildScriptIdSlug(member.displayName)
        : '';
    if (scriptId) {
      usedIds.add(scriptId);
    }
  }

  for (let index = 1; index < 1000; index += 1) {
    const candidate = `script-${index}`;
    if (!usedIds.has(candidate)) {
      return candidate;
    }
  }

  return `script-${Date.now()}`;
}

function buildInventoryGAgentName(
  members: ReadonlyArray<StudioMemberSummary>,
): string {
  const usedNames = new Set(
    members
      .map((member) => normalizeComparableText(member.displayName))
      .filter(Boolean),
  );

  for (let index = 1; index < 1000; index += 1) {
    const candidate = `gagent-${index}`;
    if (!usedNames.has(candidate)) {
      return candidate;
    }
  }

  return `gagent-${Date.now()}`;
}

function upsertStudioMemberRosterMember(
  roster: StudioMemberRoster | undefined,
  scopeId: string,
  member: StudioMemberSummary,
): StudioMemberRoster {
  const normalizedMemberId = trimOptional(member.memberId);
  const normalizedScopeId =
    trimOptional(roster?.scopeId) ||
    trimOptional(member.scopeId) ||
    trimOptional(scopeId);
  const currentMembers = roster?.members ?? [];
  let matched = false;
  const members = currentMembers.map((currentMember) => {
    if (
      normalizedMemberId &&
      trimOptional(currentMember.memberId) === normalizedMemberId
    ) {
      matched = true;
      return member;
    }

    return currentMember;
  });

  if (!matched) {
    members.push(member);
  }

  return {
    scopeId: normalizedScopeId,
    members,
    nextPageToken: roster?.nextPageToken ?? null,
  };
}

function removeStudioMemberRosterMember(
  roster: StudioMemberRoster | undefined,
  scopeId: string,
  memberId: string,
): StudioMemberRoster | undefined {
  const normalizedMemberId = trimOptional(memberId);
  if (!roster || !normalizedMemberId) {
    return roster;
  }

  return {
    scopeId: trimOptional(roster.scopeId) || trimOptional(scopeId),
    members: roster.members.filter(
      (member) => trimOptional(member.memberId) !== normalizedMemberId,
    ),
    nextPageToken: roster.nextPageToken ?? null,
  };
}

function primeStudioMemberRoster(
  queryClient: ReturnType<typeof useQueryClient>,
  queryKey: readonly unknown[],
  scopeId: string,
  member: StudioMemberSummary,
): void {
  queryClient.setQueryData<StudioMemberRoster>(
    queryKey,
    (current) =>
      upsertStudioMemberRosterMember(
        current,
        scopeId,
        member,
      ),
  );
}

function readStoredScriptDrafts(): Record<string, StudioPendingScriptDraft> {
  if (typeof window === 'undefined') {
    return {};
  }

  try {
    const parsed = JSON.parse(
      window.localStorage.getItem(STUDIO_SCRIPT_DRAFT_STORAGE_KEY) || '{}',
    ) as Record<string, StudioPendingScriptDraft>;
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function writeStoredScriptDrafts(
  drafts: Record<string, StudioPendingScriptDraft>,
): void {
  if (typeof window === 'undefined') {
    return;
  }

  window.localStorage.setItem(
    STUDIO_SCRIPT_DRAFT_STORAGE_KEY,
    JSON.stringify(drafts),
  );
}

function scriptDraftStorageKey(scopeId: string | undefined, scriptId: string): string {
  return `${scopeId || 'workspace'}:${scriptId}`;
}

function loadStoredScriptDraft(
  scopeId: string | undefined,
  scriptId: string,
): StudioPendingScriptDraft | null {
  const stored = readStoredScriptDrafts()[
    scriptDraftStorageKey(scopeId, scriptId)
  ];
  return stored?.scriptId ? stored : null;
}

function saveStoredScriptDraft(
  scopeId: string | undefined,
  draft: StudioPendingScriptDraft,
): void {
  const drafts = readStoredScriptDrafts();
  drafts[scriptDraftStorageKey(scopeId, draft.scriptId)] = draft;
  writeStoredScriptDrafts(drafts);
}

function removeStoredScriptDraft(
  scopeId: string | undefined,
  scriptId: string,
): void {
  const drafts = readStoredScriptDrafts();
  delete drafts[scriptDraftStorageKey(scopeId, scriptId)];
  writeStoredScriptDrafts(drafts);
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

function buildBackendMemberKey(memberId: string): `member:${string}` | '' {
  const normalizedMemberId = trimOptional(memberId);
  return normalizedMemberId ? `member:${normalizedMemberId}` : '';
}

function resolveStudioMemberSummaryFromKey(
  memberKey: string,
  publishedScopeMembers: readonly {
    readonly memberSummary: StudioMemberSummary | null;
  }[],
  studioScopeMembers: readonly StudioMemberSummary[],
): StudioMemberSummary | null {
  const routeMemberId = readMemberIdFromMemberKey(memberKey);
  if (!routeMemberId) {
    return null;
  }

  return (
    studioScopeMembers.find(
      (member) => trimOptional(member.memberId) === routeMemberId,
    ) ??
    publishedScopeMembers.find(
      ({ memberSummary }) => trimOptional(memberSummary?.memberId) === routeMemberId,
    )?.memberSummary ??
    null
  );
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
      teamId: '',
      memberKey: '',
      memberId: '',
      legacyServiceId: '',
      step: 'build',
      focusKey: '',
      tab: 'workflows',
      intent: '',
      prompt: '',
      executionId: '',
      logsMode: '',
      returnTo: '',
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
    teamId: trimOptional(params.get('teamId')),
    memberKey: routeMember.key,
    memberId: routeMember.memberId,
    legacyServiceId: routeMember.legacyServiceId,
    step: parseStudioStep(params.get('step')),
    focusKey: buildFocus.key,
    tab: parseStudioTab(params.get('tab')),
    intent: parseStudioIntent(params.get('intent')),
    prompt: trimOptional(params.get('prompt')),
    executionId: trimOptional(params.get('execution')),
    logsMode: parseLogsMode(params.get('logs')),
    returnTo: readStudioReturnToParam(params),
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
  const buildFocus = parseStudioBuildFocus(state.focusKey);
  const routeMember = parseStudioRouteMember(state.memberKey);
  if (buildFocus.kind === 'workflow' || routeMember.kind === 'workflow') {
    return 'editor';
  }

  if (
    state.tab === 'scripts' ||
    buildFocus.kind === 'script' ||
    routeMember.kind === 'script'
  ) {
    return 'scripts';
  }

  if (state.tab === 'gagents') {
    return 'gagent';
  }

  return 'editor';
}

function resolveRouteRequestedBuildSurface(state: StudioRouteState): BuildSurface | '' {
  if (state.step !== 'build') {
    return '';
  }

  const buildFocus = parseStudioBuildFocus(state.focusKey);
  const routeMember = parseStudioRouteMember(state.memberKey);
  if (buildFocus.kind === 'workflow' || routeMember.kind === 'workflow') {
    return 'editor';
  }

  if (buildFocus.kind === 'script' || routeMember.kind === 'script') {
    return 'scripts';
  }

  return '';
}

function findPublishedStudioMemberByMemberKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
): PublishedStudioMemberRecord | null {
  const normalizedMemberKey = trimOptional(memberKey);
  const memberToken = readMemberIdFromMemberKey(normalizedMemberKey);
  if (!memberToken) {
    return null;
  }

  return (
    publishedMembers.find(
      ({ memberSummary }) =>
        trimOptional(memberSummary?.memberId) === memberToken,
    ) ?? null
  );
}

function resolveLifecycleScriptId(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const directScriptId = readScriptIdFromMemberKey(memberKey);
  if (directScriptId) {
    return directScriptId;
  }

  const publishedScriptId = trimOptional(
    findPublishedStudioMemberByMemberKey(memberKey, publishedMembers)?.matchedScript
      ?.script?.scriptId,
  );
  if (publishedScriptId) {
    return publishedScriptId;
  }

  const memberSummary = resolveStudioMemberSummaryFromMemberKey(
    memberKey,
    publishedMembers,
    studioScopeMembers,
  );
  if (
    normalizeStudioMemberBindingImplementationKind(
      memberSummary?.implementationKind,
    ) === 'script'
  ) {
    return trimOptional(memberSummary?.publishedServiceId)
      ? ''
      : trimOptional(memberSummary?.memberId) ||
          buildScriptIdSlug(memberSummary?.displayName || '');
  }

  return '';
}

function resolveWorkflowIdForMemberSummary(
  memberSummary: StudioMemberSummary | null | undefined,
  workflows: ReadonlyArray<{
    readonly workflowId: string;
    readonly name: string;
    readonly fileName: string;
    readonly description?: string;
  }>,
  workflowFile?: Pick<StudioWorkflowFile, 'workflowId' | 'name' | 'fileName'> | null,
): string {
  if (
    normalizeStudioMemberBindingImplementationKind(
      memberSummary?.implementationKind,
    ) !== 'workflow'
  ) {
    return '';
  }

  return resolveWorkflowIdFromMemberWorkflowReference({
    memberId: memberSummary?.memberId,
    displayName: memberSummary?.displayName,
  }, workflows, workflowFile);
}

function resolveWorkflowIdForMemberDetail(
  input: {
    readonly implementationRefWorkflowId?: string | null;
    readonly memberSummary?: StudioMemberSummary | null;
  },
  workflows: ReadonlyArray<{
    readonly workflowId: string;
    readonly name: string;
    readonly fileName: string;
    readonly description?: string;
  }>,
  workflowFile?: Pick<StudioWorkflowFile, 'workflowId' | 'name' | 'fileName'> | null,
): string {
  return resolveWorkflowIdFromMemberWorkflowReference({
    workflowId: input.implementationRefWorkflowId,
    memberId: input.memberSummary?.memberId,
    displayName: input.memberSummary?.displayName,
  }, workflows, workflowFile);
}

function resolveLifecycleBuildSurface(input: {
  readonly fallback: BuildSurface;
  readonly memberKey: string;
  readonly publishedMembers: readonly PublishedStudioMemberRecord[];
  readonly studioScopeMembers: readonly StudioMemberSummary[];
}): BuildSurface {
  const normalizedMemberKey = trimOptional(input.memberKey);
  if (normalizedMemberKey.startsWith('script:')) {
    return 'scripts';
  }

  if (normalizedMemberKey.startsWith('workflow:')) {
    return 'editor';
  }

  const memberSummary = resolveStudioMemberSummaryFromMemberKey(
    normalizedMemberKey,
    input.publishedMembers,
    input.studioScopeMembers,
  );
  const implementationKind = normalizeStudioMemberBindingImplementationKind(
    memberSummary?.implementationKind,
  );
  if (implementationKind === 'script') {
    return 'scripts';
  }

  if (implementationKind === 'workflow') {
    return 'editor';
  }

  if (implementationKind === 'gagent') {
    return 'gagent';
  }

  const publishedMember = findPublishedStudioMemberByMemberKey(
    normalizedMemberKey,
    input.publishedMembers,
  );
  if (publishedMember?.matchedScript?.script?.scriptId) {
    return 'scripts';
  }

  if (publishedMember?.matchedWorkflow?.workflowId) {
    return 'editor';
  }

  if (publishedMember?.revision?.implementationKind === 'gagent') {
    return 'gagent';
  }

  return input.fallback;
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
  routeLegacyServiceId?: string;
}): string {
  const routeMemberKey = parseStudioRouteMember(input.routeMemberKey).key;
  if (routeMemberKey.startsWith('member:')) {
    return routeMemberKey;
  }

  const activeBuildFocusKey = trimOptional(input.activeBuildFocusKey);
  if (activeBuildFocusKey) {
    return activeBuildFocusKey;
  }

  if (routeMemberKey) {
    return routeMemberKey;
  }

  const routeMemberId = trimOptional(input.routeMemberId);
  if (routeMemberId) {
    return `member:${routeMemberId}`;
  }

  const routeLegacyServiceId = trimOptional(input.routeLegacyServiceId);
  if (routeLegacyServiceId) {
    return `member:${routeLegacyServiceId}`;
  }

  return '';
}

function shouldTreatRouteMemberAsBuildFocus(input: {
  routeMemberKey?: string;
  routeMemberId?: string;
  routeLegacyServiceId?: string;
}): boolean {
  return Boolean(
    trimOptional(input.routeMemberKey) ||
      trimOptional(input.routeMemberId) ||
      trimOptional(input.routeLegacyServiceId),
  );
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
  readonly revision?: StudioMemberBindingRevision | null;
};

function findDirectStudioMemberSummary(
  memberId: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): StudioMemberSummary | null {
  const normalizedMemberId = trimOptional(memberId);
  if (!normalizedMemberId) {
    return null;
  }

  return (
    studioScopeMembers.find(
      (member) => trimOptional(member.memberId) === normalizedMemberId,
    ) ??
    publishedMembers.find(
      ({ memberSummary }) =>
        trimOptional(memberSummary?.memberId) === normalizedMemberId,
    )?.memberSummary ??
    null
  );
}

function findLegacyServiceBackedStudioMemberSummary(
  serviceId: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): StudioMemberSummary | null {
  const normalizedServiceId = trimOptional(serviceId);
  if (!normalizedServiceId) {
    return null;
  }

  const directRosterMatch =
    studioScopeMembers.find(
      (member) =>
        trimOptional(member.publishedServiceId) === normalizedServiceId,
    ) ?? null;
  if (directRosterMatch) {
    return directRosterMatch;
  }

  return (
    publishedMembers.find(
      ({ service, memberSummary }) =>
        trimOptional(memberSummary?.publishedServiceId) === normalizedServiceId ||
        trimOptional(service.serviceId) === normalizedServiceId,
    )?.memberSummary ?? null
  );
}

function isKnownLegacyServiceMemberToken(
  token: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): boolean {
  const normalizedToken = trimOptional(token);
  if (!normalizedToken) {
    return false;
  }

  return (
    studioScopeMembers.some(
      (member) =>
        trimOptional(member.publishedServiceId) === normalizedToken ||
        trimOptional(member.memberId) === normalizedToken,
    ) ||
    publishedMembers.some(
      ({ service, memberSummary }) =>
        trimOptional(memberSummary?.publishedServiceId) === normalizedToken ||
        trimOptional(memberSummary?.memberId) === normalizedToken ||
        trimOptional(service.serviceId) === normalizedToken,
    )
  );
}

function resolveStudioMemberSummaryFromMemberKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): StudioMemberSummary | null {
  const parsedMember = parseStudioRouteMember(memberKey);
  if (parsedMember.kind === 'member') {
    const directMemberMatch = findDirectStudioMemberSummary(
      parsedMember.memberId,
      publishedMembers,
      studioScopeMembers,
    );
    if (directMemberMatch) {
      return directMemberMatch;
    }

    const legacyPublishedServiceMatch =
      findLegacyServiceBackedStudioMemberSummary(
        parsedMember.memberId,
        publishedMembers,
        studioScopeMembers,
      );
    if (legacyPublishedServiceMatch) {
      return legacyPublishedServiceMatch;
    }

    return null;
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

function resolvePublishedMemberIdFromLegacyServiceId(
  serviceId: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const normalizedServiceId = trimOptional(serviceId);
  if (!normalizedServiceId) {
    return '';
  }

  return trimOptional(
    findLegacyServiceBackedStudioMemberSummary(
      normalizedServiceId,
      publishedMembers,
      studioScopeMembers,
    )?.memberId,
  );
}

function resolveCanonicalMemberIdFromRouteMemberKey(
  memberKey: string,
  publishedMembers: readonly PublishedStudioMemberRecord[],
  studioScopeMembers: readonly StudioMemberSummary[],
): string {
  const parsedMember = parseStudioRouteMember(memberKey);
  if (parsedMember.kind !== 'member') {
    return '';
  }

  const directMember = findDirectStudioMemberSummary(
    parsedMember.memberId,
    publishedMembers,
    studioScopeMembers,
  );
  if (directMember) {
    return trimOptional(directMember.memberId);
  }

  const legacyMemberId = resolvePublishedMemberIdFromLegacyServiceId(
    parsedMember.memberId,
    publishedMembers,
    studioScopeMembers,
  );
  if (legacyMemberId) {
    return legacyMemberId;
  }

  return isKnownLegacyServiceMemberToken(
    parsedMember.memberId,
    publishedMembers,
    studioScopeMembers,
  )
    ? ''
    : parsedMember.memberId;
}

function resolveStudioServiceDefaultEndpointId(
  service:
    | {
        readonly endpoints?:
          | readonly {
              readonly endpointId: string;
              readonly kind: string;
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
    service.endpoints.find(isChatServiceEndpoint)?.endpointId ||
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

function buildStudioMemberDuplicateKeys(
  input: {
    readonly memberSummary?: StudioMemberSummary | null;
    readonly service?: {
      readonly serviceId?: string | null;
      readonly displayName?: string | null;
    } | null;
    readonly revision?: StudioMemberBindingRevision | null;
    readonly matchedWorkflow?: {
      readonly workflowId?: string | null;
    } | null;
    readonly matchedScript?: {
      readonly script?: {
        readonly scriptId?: string | null;
      } | null;
    } | null;
  },
): string[] {
  const memberSummary = input.memberSummary;
  const memberId = trimOptional(memberSummary?.memberId);
  const memberPublishedServiceId = trimOptional(memberSummary?.publishedServiceId);
  const serviceId = trimOptional(input.service?.serviceId);
  const matchedWorkflowKey = buildWorkflowMemberKeyFromSummary(input.matchedWorkflow);
  const matchedScriptId = trimOptional(input.matchedScript?.script?.scriptId);
  const memberImplementationKind =
    normalizeStudioMemberBindingImplementationKind(
      memberSummary?.implementationKind || input.revision?.implementationKind,
    );
  const includeImplementationAlias =
    Boolean(input.revision) ||
    Boolean(input.matchedWorkflow) ||
    Boolean(input.matchedScript);
  const memberImplementationName =
    includeImplementationAlias
      ? trimOptional(memberSummary?.displayName) ||
        trimOptional(input.revision?.workflowName) ||
        trimOptional(input.revision?.scriptId) ||
        trimOptional(input.service?.displayName)
      : '';

  return [
    memberId ? `member:${memberId}` : '',
    memberPublishedServiceId ? `member:${memberPublishedServiceId}` : '',
    serviceId ? `member:${serviceId}` : '',
    matchedWorkflowKey,
    matchedScriptId ? `script:${matchedScriptId}` : '',
    memberImplementationKind === 'workflow' && memberImplementationName
      ? `workflow:${memberImplementationName}`
      : '',
    memberImplementationKind === 'script' && memberImplementationName
      ? `script:${memberImplementationName}`
      : '',
    memberImplementationKind === 'script' && memberPublishedServiceId
      ? `script:${memberPublishedServiceId}`
      : '',
  ];
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

function buildRecentlyBoundServiceSnapshot(input: {
  readonly displayName?: string | null;
  readonly revisionId?: string | null;
  readonly scopeId: string;
  readonly selectedService?: ServiceCatalogSnapshot | null;
  readonly serviceId: string;
  readonly updatedAt?: string | null;
}): ServiceCatalogSnapshot {
  const normalizedServiceId = trimOptional(input.serviceId);
  const normalizedDisplayName =
    trimOptional(input.displayName) ||
    trimOptional(input.selectedService?.displayName) ||
    normalizedServiceId;
  const normalizedRevisionId =
    trimOptional(input.revisionId) ||
    trimOptional(input.selectedService?.activeServingRevisionId) ||
    trimOptional(input.selectedService?.defaultServingRevisionId);

  return {
    serviceKey:
      trimOptional(input.selectedService?.serviceKey) ||
      `${input.scopeId}:${scopeServiceAppId}:${scopeServiceNamespace}:${normalizedServiceId}`,
    tenantId: trimOptional(input.selectedService?.tenantId),
    appId: trimOptional(input.selectedService?.appId) || scopeServiceAppId,
    namespace:
      trimOptional(input.selectedService?.namespace) || scopeServiceNamespace,
    serviceId: normalizedServiceId,
    displayName: normalizedDisplayName,
    defaultServingRevisionId: normalizedRevisionId,
    activeServingRevisionId: normalizedRevisionId,
    deploymentId: trimOptional(input.selectedService?.deploymentId),
    primaryActorId: trimOptional(input.selectedService?.primaryActorId),
    deploymentStatus:
      trimOptional(input.selectedService?.deploymentStatus) || 'Active',
    endpoints: input.selectedService?.endpoints ?? [],
    policyIds: input.selectedService?.policyIds ?? [],
    updatedAt:
      trimOptional(input.updatedAt) ||
      trimOptional(input.selectedService?.updatedAt) ||
      new Date().toISOString(),
  };
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
  const routeSelectedMemberKey = useMemo(
    () =>
      trimOptional(routeState.memberKey),
    [routeState.memberKey],
  );
  const currentRouteMemberToken = readMemberIdFromMemberKey(routeSelectedMemberKey);
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
  const [scriptBuildState, setScriptBuildState] =
    useState<StudioScriptBuildState | null>(null);
  const [lastAppliedScriptBuildState, setLastAppliedScriptBuildState] =
    useState<StudioScriptBuildState | null>(null);
  const [pendingScriptDraft, setPendingScriptDraft] =
    useState<StudioPendingScriptDraft | null>(() => {
      const initialScriptId =
        initialBuildFocus.kind === 'script'
          ? initialBuildFocus.value
          : initialSelectedMember.kind === 'script'
            ? initialSelectedMember.value
            : '';
      return initialScriptId
        ? loadStoredScriptDraft(initialRouteState.scopeId || undefined, initialScriptId)
        : null;
    });
  const [selectedAgentKind, setSelectedAgentKind] = useState('');
  const [gAgentBuildState, setGAgentBuildState] =
    useState<StudioGAgentBuildState | null>(null);
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
  const [optimisticStudioMembers, setOptimisticStudioMembers] = useState<
    StudioMemberSummary[]
  >([]);
  const [confirmedDeletedStudioMemberIds, setConfirmedDeletedStudioMemberIds] =
    useState(() => new Set<string>());
  const [createMemberModalOpen, setCreateMemberModalOpen] = useState(false);
  const [createMemberKind, setCreateMemberKind] = useState<BuildMode>('workflow');
  const [createMemberName, setCreateMemberName] = useState('');
  const [createMemberDirectoryId, setCreateMemberDirectoryId] = useState('');
  const [createMemberTeamId, setCreateMemberTeamId] = useState('');
  const [runPrompt, setRunPrompt] = useState(() => readStudioRouteState().prompt);
  const [runPending, setRunPending] = useState(false);
  const [runNotice, setRunNotice] = useState<DraftRunNotice | null>(null);
  const [selectedGraphNodeId, setSelectedGraphNodeId] = useState('');
  const [executionStopPending, setExecutionStopPending] = useState(false);
  const [executionNotice, setExecutionNotice] = useState<StudioNotice | null>(null);
  const [logsPopoutMode] = useState(() => readStudioRouteState().logsMode);
  const [recentlyBoundMemberKey, setRecentlyBoundMemberKey] = useState('');
  const [recentlyBoundServiceId, setRecentlyBoundServiceId] = useState('');
  const [teamEntryActionBusy, setTeamEntryActionBusy] = useState(false);
  const [teamEntryCandidate, setTeamEntryCandidate] =
    useState<StudioTeamEntryCandidate | null>(null);
  const recentlyBoundServiceRef = useRef<ServiceCatalogSnapshot | null>(null);
  const legacyRouteServiceIdRef = useRef(
    trimOptional(initialRouteState.legacyServiceId),
  );
  const currentRouteLegacyServiceId = trimOptional(routeState.legacyServiceId);
  if (currentRouteLegacyServiceId) {
    legacyRouteServiceIdRef.current = currentRouteLegacyServiceId;
  }
  const pinnedRouteBackendMemberIdRef = useRef(
    trimOptional(initialRouteState.memberId),
  );
  const [pinnedRouteBackendMemberId, setPinnedRouteBackendMemberId] = useState(
    () => pinnedRouteBackendMemberIdRef.current,
  );
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
  const createMemberNameInputRef = useRef<HTMLInputElement | null>(null);
  const suppressBuildMemberRoutePersistenceRef = useRef(false);
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
    setBuildSurface((currentSurface) => {
      if (
        routeStudioSurface !== 'build' &&
        routeBuildSurface === 'editor' &&
        currentSurface !== 'editor'
      ) {
        return currentSurface;
      }

      return currentSurface === routeBuildSurface ? currentSurface : routeBuildSurface;
    });
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
    if (routeBuildFocus.kind === 'workflow' || routeSelectedMember.kind === 'workflow') {
      setBuildSurface((currentSurface) =>
        currentSurface === 'editor' ? currentSurface : 'editor',
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
  const resolvedStudioTeamId = trimOptional(routeState.teamId);
  const workflowWorkspaceContextKey = resolvedStudioScopeId || 'workspace';
  const workspaceSettingsQuery = useQuery({
    queryKey: ['studio-workspace-settings', workflowWorkspaceContextKey],
    enabled: studioHostReady,
    queryFn: () => studioApi.getWorkspaceSettings(resolvedStudioScopeId),
  });
  const userLlmSettingsQuery = useQuery({
    queryKey: ['studio-user-llm-settings'],
    enabled: studioHostReady,
    queryFn: () => studioApi.getUserLlmSettings(),
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
      scopeRuntimeApi.listServices(resolvedStudioScopeId, {
        appId: scopeServiceAppId,
      }),
  });
  const studioMembersQueryKey = useMemo(
    () =>
      [
        'studio-scope-members',
        resolvedStudioScopeId,
        resolvedStudioTeamId ? 'team' : 'scope',
        resolvedStudioTeamId,
      ] as const,
    [resolvedStudioScopeId, resolvedStudioTeamId],
  );
  const studioMembersQuery = useQuery({
    queryKey: studioMembersQueryKey,
    enabled: studioHostReady && Boolean(resolvedStudioScopeId),
    retry: false,
    queryFn: () =>
      resolvedStudioTeamId
        ? studioApi.listTeamMembers(resolvedStudioScopeId, resolvedStudioTeamId)
        : studioApi.listMembers(resolvedStudioScopeId),
  });
  const studioTeamSummaryQueryKey = useMemo(
    () =>
      [
        'studio-team-summary',
        resolvedStudioScopeId,
        resolvedStudioTeamId,
      ] as const,
    [resolvedStudioScopeId, resolvedStudioTeamId],
  );
  const studioTeamSummaryQuery = useQuery({
    queryKey: studioTeamSummaryQueryKey,
    enabled:
      studioHostReady &&
      Boolean(resolvedStudioScopeId) &&
      Boolean(resolvedStudioTeamId),
    retry: false,
    queryFn: () =>
      studioApi.getTeam(resolvedStudioScopeId, resolvedStudioTeamId),
  });
  const selectedWorkflowQuery = useQuery({
    queryKey: ['studio-workflow', workflowWorkspaceContextKey, selectedWorkflowId],
    enabled: studioHostReady && Boolean(selectedWorkflowId),
    queryFn: () => studioApi.getWorkflow(selectedWorkflowId, resolvedStudioScopeId),
    staleTime: SAVED_WORKFLOW_QUERY_STALE_MS,
  });
  const gAgentKindsQuery = useQuery({
    queryKey: ['studio-runtime-gagent-kinds'],
    enabled: studioHostReady,
    retry: false,
    queryFn: () => runtimeGAgentApi.listKinds(),
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
      (gAgentKindsQuery.data ?? []).some((descriptor) =>
        matchesRuntimeGAgentKindDescriptor(selectedAgentKind, descriptor),
      )
    ) {
      return;
    }

    const fallbackAgentKind =
      (gAgentKindsQuery.data?.[0]
        ? buildRuntimeGAgentKindValue(gAgentKindsQuery.data[0])
        : '');

    if (!fallbackAgentKind) {
      return;
    }

    setSelectedAgentKind((current) =>
      trimOptional(current) === fallbackAgentKind ? current : fallbackAgentKind,
    );
  }, [
    gAgentKindsQuery.data,
    selectedAgentKind,
  ]);
  useEffect(() => {
    const serverMembers = studioMembersQuery.data?.members ?? [];
    if (!serverMembers.length || !optimisticStudioMembers.length) {
      return;
    }

    setOptimisticStudioMembers((current) =>
      current.filter((optimisticMember) => {
        const serverMember = serverMembers.find(
          (member) =>
            trimOptional(member.memberId) ===
            trimOptional(optimisticMember.memberId),
        );
        return !studioMemberSummaryMatches(serverMember, optimisticMember);
      }),
    );
  }, [optimisticStudioMembers.length, studioMembersQuery.data?.members]);
  const studioScopeMembers = useMemo(
    () =>
      mergeOptimisticStudioMembers(
        studioMembersQuery.data?.members ?? [],
        optimisticStudioMembers,
        resolvedStudioScopeId,
        resolvedStudioTeamId,
      ).filter(
        (member) =>
          !confirmedDeletedStudioMemberIds.has(trimOptional(member.memberId)),
      ),
    [
      confirmedDeletedStudioMemberIds,
      optimisticStudioMembers,
      resolvedStudioScopeId,
      resolvedStudioTeamId,
      studioMembersQuery.data?.members,
    ],
  );
  useEffect(() => {
    setConfirmedDeletedStudioMemberIds(new Set());
  }, [resolvedStudioScopeId, resolvedStudioTeamId]);
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
  const publishedScopeServices = useMemo(() => {
    const services = scopeServicesQuery.data ?? [];
    if (!resolvedStudioTeamId) {
      return services;
    }

    return services.filter((service) => {
      const serviceId = trimOptional(service.serviceId);
      return serviceId && studioMemberByPublishedServiceId.has(serviceId);
    });
  }, [
    resolvedStudioTeamId,
    scopeServicesQuery.data,
    studioMemberByPublishedServiceId,
  ]);
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
  const suggestedCreateScriptName = useMemo(
    () => buildInventoryScriptName(availableScopeScripts, studioScopeMembers),
    [availableScopeScripts, studioScopeMembers],
  );
  const suggestedCreateGAgentName = useMemo(
    () => buildInventoryGAgentName(studioScopeMembers),
    [studioScopeMembers],
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
  const explicitRouteBackendMemberId = useMemo(() => {
    if (routeSelectedMember.kind !== 'member') {
      return '';
    }

    const routeLegacyServiceId = trimOptional(routeState.legacyServiceId);
    if (routeLegacyServiceId) {
      return resolvePublishedMemberIdFromLegacyServiceId(
        routeLegacyServiceId,
        publishedScopeMembers,
        studioScopeMembers,
      );
    }

    const legacyRouteServiceId = trimOptional(legacyRouteServiceIdRef.current);
    if (legacyRouteServiceId && currentRouteMemberToken === legacyRouteServiceId) {
      return resolvePublishedMemberIdFromLegacyServiceId(
        legacyRouteServiceId,
        publishedScopeMembers,
        studioScopeMembers,
      );
    }

    const canonicalRouteMemberToken = readMemberIdFromMemberKey(
      routeSelectedMemberKey,
    );
    const directRouteMember = studioScopeMembers.find(
      (member) => trimOptional(member.memberId) === canonicalRouteMemberToken,
    );
    if (directRouteMember) {
      return trimOptional(directRouteMember.memberId);
    }

    return resolveCanonicalMemberIdFromRouteMemberKey(
      routeSelectedMemberKey,
      publishedScopeMembers,
      studioScopeMembers,
    );
  }, [
    currentRouteMemberToken,
    publishedScopeMembers,
    routeState.legacyServiceId,
    routeSelectedMember.kind,
    routeSelectedMemberKey,
    studioScopeMembers,
  ]);
  const currentExplicitRouteBackendMemberId = trimOptional(
    explicitRouteBackendMemberId,
  );
  if (currentExplicitRouteBackendMemberId) {
    pinnedRouteBackendMemberIdRef.current = currentExplicitRouteBackendMemberId;
  }
  useEffect(() => {
    const routeMemberId = currentExplicitRouteBackendMemberId;
    if (!routeMemberId) {
      return;
    }

    pinnedRouteBackendMemberIdRef.current = routeMemberId;
    setPinnedRouteBackendMemberId((currentMemberId) =>
      currentMemberId === routeMemberId ? currentMemberId : routeMemberId,
    );
  }, [currentExplicitRouteBackendMemberId]);
  const routeSelectedBackendMemberId = useMemo(
    () => {
      const explicitMemberId = trimOptional(explicitRouteBackendMemberId);
      if (explicitMemberId) {
        return explicitMemberId;
      }

      return routeSelectedMember.kind === 'member'
        ? ''
        : trimOptional(pinnedRouteBackendMemberId);
    },
    [
      explicitRouteBackendMemberId,
      pinnedRouteBackendMemberId,
      routeSelectedMember.kind,
    ],
  );
  const routeSelectedBackendMemberKey = useMemo(
    () => buildBackendMemberKey(routeSelectedBackendMemberId),
    [routeSelectedBackendMemberId],
  );
  useEffect(() => {
    const routeMemberId = trimOptional(routeSelectedBackendMemberId);
    if (!routeMemberId || selectedScriptId) {
      return;
    }

    const routeMember = studioScopeMembers.find(
      (member) => trimOptional(member.memberId) === routeMemberId,
    );
    if (
      normalizeStudioMemberBindingImplementationKind(
        routeMember?.implementationKind,
      ) !== 'script'
    ) {
      return;
    }

    const scriptId =
      routeBuildFocus.kind === 'script'
        ? routeBuildFocus.value
        : trimOptional(routeMember?.displayName);
    if (!scriptId) {
      return;
    }
    if (selectedScriptId === scriptId) {
      return;
    }

    setSelectedWorkflowId('');
    setSelectedScriptId(scriptId);
    setTemplateWorkflow('');
  }, [
    routeBuildFocus.kind,
    routeBuildFocus.value,
    routeSelectedBackendMemberId,
    selectedScriptId,
    studioScopeMembers,
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
  const readyUserRoutes = useMemo(
    () =>
      (userLlmSettingsQuery.data?.routeOptions ?? []).filter(
        (option) => option.ready && option.allowed,
      ),
    [userLlmSettingsQuery.data?.routeOptions],
  );
  const workflowDryRunLlmConfig = useMemo(
    () => resolveSavedConversationLlmConfig(userLlmSettingsQuery.data),
    [userLlmSettingsQuery.data],
  );
  const workflowDryRunHeaders = useMemo(
    () =>
      workflowDryRunLlmConfig.status === 'ready'
        ? buildConversationHeaders(
            workflowDryRunLlmConfig.route,
            workflowDryRunLlmConfig.model,
          )
        : undefined,
    [workflowDryRunLlmConfig],
  );
  const workflowDryRunRouteLabel = workflowDryRunLlmConfig.status === 'system_default'
    ? 'Config default'
    : workflowDryRunLlmConfig.routeLabel;
  const workflowDryRunBlockedReason = useMemo(() => {
    if (userLlmSettingsQuery.isLoading) {
      return t("pages.studio.index.studio.provider", "Studio is checking available providers. Try running again shortly.");
    }

    if (workflowDryRunLlmConfig.status === 'action_required') {
      return t(
        "pages.studio.index.llm.selection.action.required",
        "The saved LLM selection needs attention in Settings before this workflow can run.",
      );
    }

    if (readyUserRoutes.length === 0) {
      return t("pages.studio.index.ready.ai.provider.provider", "No ready AI provider is available. Connect a provider, then return to run this workflow draft.");
    }

    return '';
  }, [
    readyUserRoutes.length,
    userLlmSettingsQuery.isLoading,
    workflowDryRunLlmConfig.status,
  ]);
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
      trimOptional(routeState.memberId) ||
      trimOptional(routeState.legacyServiceId)
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
    routeState.legacyServiceId,
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
                t("pages.studio.index.resolve.studio.yaml.validation.errors.4", "Resolve Studio YAML validation errors before editing the workflow graph."),
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
          message: t("pages.studio.index.studio.is.still.parsing.the.2", "Studio is still parsing the current workflow draft."),
        });
        return null;
      }

      if (hasValidationError(activeWorkflowFindings)) {
        setSaveNotice({
          type: 'error',
          message: t("pages.studio.index.resolve.studio.yaml.validation.errors.5", "Resolve Studio YAML validation errors before editing the workflow graph."),
        });
        return null;
      }

      setSaveNotice({
        type: 'error',
        message: t("pages.studio.index.load.workflow.draft.before.editing.3", "Load a workflow draft before editing the workflow graph."),
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

  const normalizeWorkflowYamlForRuntime = useCallback(
    async (
      yaml: string,
      document: StudioWorkflowDocument | null | undefined,
      availableWorkflowNames: string[],
    ): Promise<{
      readonly yaml: string;
      readonly document: StudioWorkflowDocument | null;
    }> => {
      const sourceYaml = yaml.trim();
      if (!sourceYaml) {
        return {
          yaml: sourceYaml,
          document: document ?? null,
        };
      }

      let sourceDocument = cloneStudioWorkflowDocument(document);
      if (!sourceDocument) {
        sourceDocument =
          cloneStudioWorkflowDocument(
            (
              await studioApi.parseYaml({
                yaml: sourceYaml,
                availableWorkflowNames,
                availableStepTypes,
              })
            ).document,
          ) ?? null;
      }

      if (!sourceDocument?.steps?.length) {
        return {
          yaml: sourceYaml,
          document: sourceDocument,
        };
      }

      const normalizedDocument: StudioWorkflowDocument = {
        ...sourceDocument,
        steps: sourceDocument.steps.map((step) => ({
          ...step,
          parameters: normalizeStepParametersForType(
            trimOptional(step.type ?? step.originalType),
            step.parameters && typeof step.parameters === 'object'
              ? step.parameters
              : {},
          ),
        })),
      };

      const serialized = await studioApi.serializeYaml({
        document: normalizedDocument,
        availableWorkflowNames,
        availableStepTypes,
      });

      return {
        yaml: serialized.yaml.trim() || sourceYaml,
        document:
          cloneStudioWorkflowDocument(serialized.document) ??
          normalizedDocument,
      };
    },
    [availableStepTypes],
  );

  const buildWorkflowYamlBundle = useCallback(async (
    pendingStepDraft?: {
      readonly stepId: string;
      readonly draft: StudioStepInspectorDraft;
    } | null,
  ): Promise<string[]> => {
    let rootYaml = draftYaml.trim();
    let rootDocument: StudioWorkflowDocument | null | undefined =
      activeWorkflowDocument;
    let rootRuntimeYamlReady = false;

    if (pendingStepDraft) {
      const currentStepId = pendingStepDraft.stepId.trim();
      if (!currentStepId) {
        throw new Error('Select a workflow step before running its draft changes.');
      }

      const document =
        cloneStudioWorkflowDocument(
          activeWorkflowDocument ||
            parsedWorkflowDocument ||
            activeWorkflowFile?.document ||
            null,
        ) ||
        cloneStudioWorkflowDocument(
          (
            await studioApi.parseYaml({
              yaml: rootYaml,
              availableWorkflowNames: workflowNames,
              availableStepTypes,
            })
          ).document as StudioWorkflowDocument | null,
        );
      if (!document) {
        throw new Error('Load a workflow draft before running its draft changes.');
      }

      const result = applyStepInspectorDraft(
        document,
        currentStepId,
        pendingStepDraft.draft,
      );
      const serialized = await studioApi.serializeYaml({
        document: result.document,
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      });

      rootYaml = serialized.yaml.trim();
      rootDocument = result.document;
      rootRuntimeYamlReady = true;
    }

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
      runtimeYamlReady?: boolean;
    }> = [
      {
        workflowName: activeWorkflowName.trim() || draftWorkflowName.trim(),
        yaml: rootYaml,
        document: rootDocument,
        runtimeYamlReady: rootRuntimeYamlReady,
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
      const normalizedCurrent = current.runtimeYamlReady
        ? {
            yaml: current.yaml,
            document:
              cloneStudioWorkflowDocument(current.document) ??
              cloneStudioWorkflowDocument(
                (
                  await studioApi.parseYaml({
                    yaml: current.yaml,
                    availableWorkflowNames,
                    availableStepTypes,
                  })
                ).document as StudioWorkflowDocument | null,
              ),
          }
        : await normalizeWorkflowYamlForRuntime(
            current.yaml,
            current.document,
            availableWorkflowNames,
          );
      bundle.push(normalizedCurrent.yaml);

      for (const targetWorkflow of readWorkflowCallTargets(normalizedCurrent.document)) {
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
    activeWorkflowFile?.document,
    activeWorkflowName,
    availableStepTypes,
    draftWorkflowName,
    draftYaml,
    normalizeWorkflowYamlForRuntime,
    parsedWorkflowDocument,
    resolvedStudioScopeId,
    visibleWorkflowSummaries,
    workflowNames,
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
  const isScriptBuildLaunchpadEmpty =
    isBuildScriptsSurface &&
    Boolean(resolvedStudioScopeId) &&
    Boolean(appContextQuery.data?.features.scripts) &&
    !scopeScriptsQuery.isLoading &&
    !scopeScriptsQuery.isFetching &&
    availableScopeScripts.length === 0 &&
    !trimOptional(selectedScriptId) &&
    !trimOptional(pendingScriptDraft?.scriptId) &&
    !trimOptional(scriptBuildState?.scriptId);
  const buildPendingBindCandidate = useMemo(() => {
    if (!resolvedStudioScopeId) {
      return null;
    }

    const shouldBuildScriptCandidate =
      activeBuildMode === 'script' ||
      (isBindSurface && Boolean(trimOptional(selectedScriptId)));

    if (shouldBuildScriptCandidate) {
      const selectedId = trimOptional(selectedScriptId);
      const catalogScriptState =
        selectedId
          ? availableScopeScripts.find(
              (detail) => trimOptional(detail.script?.scriptId) === selectedId,
            ) ?? null
          : null;
      const currentAppliedScriptState =
        scriptBuildState?.scriptId &&
        trimOptional(scriptBuildState.scriptId) === selectedId &&
        !scriptBuildState.dirty &&
        scriptBuildState.saveStatus === 'applied'
          ? scriptBuildState
          : null;
      const lastAppliedScriptState =
        lastAppliedScriptBuildState?.scriptId &&
        trimOptional(lastAppliedScriptBuildState.scriptId) === selectedId &&
        !lastAppliedScriptBuildState.dirty &&
        lastAppliedScriptBuildState.saveStatus === 'applied'
          ? lastAppliedScriptBuildState
          : null;
      const catalogAppliedScriptState = catalogScriptState
        ? {
            scriptId: trimOptional(catalogScriptState.script?.scriptId),
            displayName: '',
            scriptRevision:
              trimOptional(catalogScriptState.source?.revision) ||
              trimOptional(catalogScriptState.script?.activeRevision),
            revisionId:
              trimOptional(catalogScriptState.source?.revision) ||
              trimOptional(catalogScriptState.script?.activeRevision),
            dirty: false,
            saveStatus: 'applied' as const,
          }
        : null;
      const effectiveScriptState =
        currentAppliedScriptState ||
        lastAppliedScriptState ||
        catalogAppliedScriptState;
      const scriptId = trimOptional(effectiveScriptState?.scriptId);
      if (
        scriptId &&
        selectedId &&
        scriptId === selectedId &&
        !effectiveScriptState?.dirty &&
        effectiveScriptState?.saveStatus === 'applied'
      ) {
        return {
          kind: 'script' as const,
          displayName: trimOptional(effectiveScriptState.displayName) || scriptId,
          description:
            t("pages.studio.index.bind.the.catalog.applied.script.2", "Bind the catalog-applied Script revision as a callable member service. Draft-run remains a Build-only source test."),
          actionLabel: 'Bind Script member',
          scriptId,
          scriptRevision:
            trimOptional(effectiveScriptState.scriptRevision) ||
            trimOptional(effectiveScriptState.revisionId),
        };
      }
    }

    if (activeBuildMode === 'workflow') {
      if (!trimOptional(draftYaml)) {
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
          t("pages.studio.index.publish.the.current.workflow.revision.2", "Publish the current workflow revision first, then Studio can reveal the invoke URL and endpoint contract for this member."),
        actionLabel: 'Bind current revision',
      };
    }

    if (activeBuildMode === 'gagent') {
      const agentKind =
        trimOptional(gAgentBuildState?.agentKind) ||
        trimOptional(selectedAgentKind);
      if (!agentKind) {
        return null;
      }

      const displayName =
        trimOptional(gAgentBuildState?.displayName) ||
        trimOptional(
          studioScopeMembers.find(
            (member) =>
              trimOptional(member.memberId) ===
              trimOptional(routeSelectedBackendMemberId),
          )?.displayName,
        ) ||
        trimOptional(routeSelectedBackendMemberId) ||
        'GAgent member';

      return {
        kind: 'gagent' as const,
        displayName,
        description:
          t("pages.studio.index.bind.the.selected.typed.gagent.2", "Bind the selected typed GAgent as this member service, then Studio can reveal the invoke URL and endpoint contract."),
        actionLabel: 'Bind GAgent member',
        agentKind,
        endpoints: [
          {
            endpointId: 'run',
            displayName: 'Run',
            kind: 'command' as const,
            requestTypeUrl: 'type.googleapis.com/google.protobuf.StringValue',
            responseTypeUrl: '',
            description:
              trimOptional(gAgentBuildState?.initialPrompt) ||
              'Run the bound GAgent member.',
          },
        ],
      };
    }

    return null;
  }, [
    activeBuildMode,
    activeWorkflowName,
    draftWorkflowName,
    draftYaml,
    gAgentBuildState,
    resolvedStudioScopeId,
    routeSelectedBackendMemberId,
    scriptBuildState,
    selectedScriptId,
    selectedAgentKind,
    availableScopeScripts,
    isBindSurface,
    lastAppliedScriptBuildState,
  ]);
  const buildPendingMemberSummary = useMemo(() => {
    if (!buildPendingBindCandidate) {
      return null;
    }

    const routeMemberId = trimOptional(routeSelectedBackendMemberId);
    if (routeMemberId) {
      const routeMember = studioScopeMembers.find(
        (member) => trimOptional(member.memberId) === routeMemberId,
      );
      if (
        routeMember &&
        normalizeStudioMemberBindingImplementationKind(
          routeMember.implementationKind,
        ) === buildPendingBindCandidate.kind
      ) {
        return routeMember;
      }
    }

    if (buildPendingBindCandidate.kind === 'gagent') {
      const agentKind = trimOptional(buildPendingBindCandidate.agentKind);
      const routeMemberId = trimOptional(routeSelectedBackendMemberId);
      if (routeMemberId) {
        const routeMember = studioScopeMembers.find(
          (member) => trimOptional(member.memberId) === routeMemberId,
        );
        if (
          routeMember &&
          normalizeStudioMemberBindingImplementationKind(
            routeMember.implementationKind,
          ) === 'gagent'
        ) {
          return routeMember;
        }
      }

      const publishedMatch = publishedScopeMembers.find(
        ({ memberSummary, revision }) =>
          normalizeStudioMemberBindingImplementationKind(
            memberSummary?.implementationKind || revision?.implementationKind,
          ) === 'gagent' &&
          (trimOptional(revision?.staticAgentKind) === agentKind ||
            normalizeComparableText(memberSummary?.displayName) ===
              normalizeComparableText(buildPendingBindCandidate.displayName)),
      )?.memberSummary;
      if (publishedMatch) {
        return publishedMatch;
      }

      const rosterMatches = studioScopeMembers.filter(
        (member) =>
          normalizeStudioMemberBindingImplementationKind(
            member.implementationKind,
          ) === 'gagent' &&
          normalizeComparableText(member.displayName) ===
            normalizeComparableText(buildPendingBindCandidate.displayName),
      );
      return rosterMatches.length === 1 ? rosterMatches[0] : null;
    }

    if (buildPendingBindCandidate.kind === 'script') {
      const scriptId = trimOptional(buildPendingBindCandidate.scriptId);
      const normalizedCandidateName = normalizeComparableText(
        buildPendingBindCandidate.displayName || scriptId,
      );

      const publishedMatch = publishedScopeMembers.find(
        ({ matchedScript, memberSummary, service }) => {
          if (
            scriptId &&
            trimOptional(matchedScript?.script?.scriptId) === scriptId
          ) {
            return true;
          }

          if (
            scriptId &&
            trimOptional(service.serviceId) === scriptId
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

      const rosterMatches = studioScopeMembers.filter((member) => {
        if (
          normalizeStudioMemberBindingImplementationKind(
            member.implementationKind,
          ) !== 'script'
        ) {
          return false;
        }

        const displayName = normalizeComparableText(member.displayName);
        const publishedServiceId = trimOptional(member.publishedServiceId);
        const memberId = trimOptional(member.memberId);
        return (
          Boolean(displayName && displayName === normalizedCandidateName) ||
          Boolean(scriptId && publishedServiceId === scriptId) ||
          Boolean(scriptId && memberId === scriptId)
        );
      });
      return rosterMatches.length === 1 ? rosterMatches[0] : null;
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
    routeSelectedBackendMemberId,
    publishedScopeMembers,
    selectedWorkflowId,
    studioScopeMembers,
  ]);
  const observeMemberBindingRunOnce = useCallback(
    async (
      receipt: StudioMemberBindingAcceptedResponse,
    ): Promise<StudioMemberBindingRunStatusResponse | null> => {
      const result = await probeAsyncOperation({
        maxAttempts: MEMBER_BINDING_RUN_POLL_ATTEMPTS,
        read: () =>
          studioApi.getMemberBindingRun(
            receipt.scopeId,
            receipt.memberId,
            receipt.bindingRunId,
          ),
        isTerminal: (run) =>
          normalizeStudioMemberBindingRunState(run).terminal,
        canRetryError: (error) => {
          // The run status is read-model backed, so the first request can
          // legitimately arrive before projection catches up to the accepted ACK.
          return isStudioApiStatus(error, 404);
        },
        waitForNextAttempt: waitForAsyncOperationProbeTick,
      });

      return result.observation;
    },
    [],
  );

  const handleBindPendingCandidate = useCallback(async (): Promise<StudioNotice | void> => {
    if (!buildPendingBindCandidate || !resolvedStudioScopeId) {
      throw new Error('Resolve the current workspace before binding this member.');
    }

    const requestLocationSnapshot = getLocationSnapshot();
    const resolvedBuildMemberId =
      trimOptional(routeSelectedBackendMemberId) ||
      trimOptional(buildPendingMemberSummary?.memberId);
    let result = null;
    let memberBindingRunOutcome: StudioBindingRunOutcome | null = null;
    if (buildPendingBindCandidate.kind === 'workflow') {
      if (resolvedBuildMemberId) {
        const workflowIdForBinding = trimOptional(
          selectedWorkflowId || activeWorkflowFile?.workflowId,
        );
        if (!workflowIdForBinding) {
          throw new Error('Resolve a stable workflow draft id before binding this member.');
        }

        const workflowYamls = await buildWorkflowYamlBundle();
        const { workflowYaml, inlineWorkflowYamls } = splitWorkflowYamlBundle(
          workflowYamls,
        );
        const revisionIdentityCandidate = createWorkflowRevisionIdentityCandidate();
        const explicitRequestPreview = await studioApi.previewExplicitRequests({
          scopeId: resolvedStudioScopeId,
          workflowId: workflowIdForBinding,
          workflowYaml,
          inlineWorkflowYamls,
          executionMode: 'interactive',
          revisionId: revisionIdentityCandidate,
        });
        const explicitRequestConfirmations =
          await confirmInteractiveExplicitRequestPreview(explicitRequestPreview);
        if (explicitRequestConfirmations === null) {
          return;
        }

        const receipt = await studioApi.bindMemberWorkflow({
          scopeId: resolvedStudioScopeId,
          memberId: resolvedBuildMemberId,
          displayName: buildPendingBindCandidate.displayName,
          workflowId: workflowIdForBinding,
          revisionId: explicitRequestPreview.revisionId,
          workflowYamls,
          ...(explicitRequestConfirmations.length > 0
            ? { explicitRequestConfirmations }
            : {}),
        });
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        memberBindingRunOutcome = resolveStudioMemberBindingRunOutcome(
          await observeMemberBindingRunOnce(receipt),
        );
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        if (memberBindingRunOutcome.kind === 'pending') {
          return buildStudioMemberBindingPendingNotice(
            buildPendingBindCandidate.displayName,
            memberBindingRunOutcome.run,
          );
        }
      } else {
        result = await studioApi.bindScopeWorkflow({
          scopeId: resolvedStudioScopeId,
          displayName: buildPendingBindCandidate.displayName,
          workflowYamls: await buildWorkflowYamlBundle(),
        });
      }
    } else if (buildPendingBindCandidate.kind === 'script') {
      if (resolvedBuildMemberId) {
        const receipt = await studioApi.bindMemberScript({
          scopeId: resolvedStudioScopeId,
          memberId: resolvedBuildMemberId,
          displayName: buildPendingBindCandidate.displayName,
          scriptId: buildPendingBindCandidate.scriptId,
          scriptRevision: buildPendingBindCandidate.scriptRevision,
        });
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        memberBindingRunOutcome = resolveStudioMemberBindingRunOutcome(
          await observeMemberBindingRunOnce(receipt),
        );
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        if (memberBindingRunOutcome.kind === 'pending') {
          return buildStudioMemberBindingPendingNotice(
            buildPendingBindCandidate.displayName,
            memberBindingRunOutcome.run,
          );
        }
      } else {
        result = await studioApi.bindScopeScript({
          scopeId: resolvedStudioScopeId,
          displayName: buildPendingBindCandidate.displayName,
          serviceId: buildPendingBindCandidate.scriptId,
          scriptId: buildPendingBindCandidate.scriptId,
          scriptRevision: buildPendingBindCandidate.scriptRevision,
        });
      }
    } else {
      if (resolvedBuildMemberId) {
        const receipt = await studioApi.bindMemberGAgent({
          scopeId: resolvedStudioScopeId,
          memberId: resolvedBuildMemberId,
          displayName: buildPendingBindCandidate.displayName,
          agentKind: buildPendingBindCandidate.agentKind,
          endpoints: buildPendingBindCandidate.endpoints,
        });
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        memberBindingRunOutcome = resolveStudioMemberBindingRunOutcome(
          await observeMemberBindingRunOnce(receipt),
        );
        await queryClient.invalidateQueries({
          queryKey: [
            'studio-bind',
            'member-binding',
            resolvedStudioScopeId,
            resolvedBuildMemberId,
          ],
        });
        if (memberBindingRunOutcome.kind === 'pending') {
          return buildStudioMemberBindingPendingNotice(
            buildPendingBindCandidate.displayName,
            memberBindingRunOutcome.run,
          );
        }
      } else {
        result = await studioApi.bindScopeGAgent({
          scopeId: resolvedStudioScopeId,
          displayName: buildPendingBindCandidate.displayName,
          agentKind: buildPendingBindCandidate.agentKind,
          endpoints: buildPendingBindCandidate.endpoints,
        });
      }
    }
    await queryClient.invalidateQueries({
      queryKey: studioMembersQueryKey,
    });
    const servicesResult = await scopeServicesQuery.refetch();
    const observedMemberBindingResult =
      memberBindingRunOutcome?.kind === 'succeeded'
        ? memberBindingRunOutcome.run.result
        : null;
    const optimisticBoundServiceId =
      trimOptional(observedMemberBindingResult?.publishedServiceId) ||
      trimOptional(buildPendingMemberSummary?.publishedServiceId) ||
      trimOptional(buildPendingBindCandidate.displayName) ||
      trimOptional(result?.displayName) ||
      trimOptional(result?.targetName) ||
      trimOptional(result?.workflowName);
    const boundServiceId =
      trimOptional(observedMemberBindingResult?.publishedServiceId) ||
      trimOptional(result?.serviceId) ||
      resolveBoundServiceIdFromCatalog({
        services: servicesResult.data ?? [],
        candidates: [
          observedMemberBindingResult?.publishedServiceId,
          buildPendingBindCandidate.displayName,
          buildPendingBindCandidate.kind === 'script'
            ? buildPendingBindCandidate.scriptId
            : '',
          buildPendingBindCandidate.kind === 'gagent'
            ? buildPendingBindCandidate.agentKind
            : '',
          result?.displayName,
          result?.targetName,
          result?.workflowName,
          result?.script?.scriptId,
        ],
      }) ||
      optimisticBoundServiceId;

    if (boundServiceId) {
      const routeStillMatchesBindingRequest = () => {
        if (typeof window === 'undefined') {
          return true;
        }

        if (window.location.pathname !== '/studio') {
          return false;
        }

        const currentRouteState = readStudioRouteState(window.location.search);
        const currentRouteMemberKey = trimOptional(currentRouteState.memberKey);
        const requestMemberKey = buildBackendMemberKey(resolvedBuildMemberId);
        if (!requestMemberKey) {
          return getLocationSnapshot() === requestLocationSnapshot;
        }

        if (currentRouteMemberKey === requestMemberKey) {
          return true;
        }

        const currentMemberId = readMemberIdFromMemberKey(currentRouteMemberKey);
        if (currentMemberId === resolvedBuildMemberId) {
          return true;
        }

        const currentRouteMemberSummary = resolveStudioMemberSummaryFromMemberKey(
          currentRouteMemberKey,
          publishedScopeMembers,
          studioScopeMembers,
        );
        return (
          trimOptional(currentRouteMemberSummary?.memberId) ===
          resolvedBuildMemberId
        );
      };
      if (!routeStillMatchesBindingRequest()) {
        return;
      }

      const buildCandidateMemberKey =
        buildPendingBindCandidate.kind === 'script'
          ? trimOptional(selectedScriptId)
            ? `script:${trimOptional(selectedScriptId)}`
            : ''
          : buildPendingBindCandidate.kind === 'workflow'
            ? trimOptional(selectedWorkflowMemberKey)
            : '';
      const boundMemberKey =
        (resolvedBuildMemberId ? `member:${resolvedBuildMemberId}` : '') ||
        buildCandidateMemberKey ||
        trimOptional(routeState.memberKey) ||
        activeBuildFocusKey ||
        (() => {
          const resolvedBoundMemberId =
            resolvePublishedMemberIdFromLegacyServiceId(
              boundServiceId,
              publishedScopeMembers,
              studioScopeMembers,
            );
          return resolvedBoundMemberId
            ? `member:${resolvedBoundMemberId}`
            : `member:${boundServiceId}`;
        })();
      const selectedService = (servicesResult.data ?? []).find(
        (service) => service.serviceId === boundServiceId,
      );
      const recentlyBoundService = buildRecentlyBoundServiceSnapshot({
        displayName:
          trimOptional(result?.displayName) ||
          trimOptional(buildPendingBindCandidate.displayName),
        revisionId:
          trimOptional(observedMemberBindingResult?.revisionId) ||
          trimOptional(result?.revisionId) ||
          trimOptional(buildPendingMemberSummary?.lastBoundRevisionId),
        scopeId: resolvedStudioScopeId,
        selectedService,
        serviceId: boundServiceId,
        updatedAt: memberBindingRunOutcome?.run?.updatedAt,
      });
      recentlyBoundServiceRef.current = recentlyBoundService;
      const defaultEndpointId = resolveStudioServiceDefaultEndpointId(
        recentlyBoundService,
      );
      const routeMemberSummary = resolveStudioMemberSummaryFromMemberKey(
        routeSelectedBackendMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      );
      const resolvedBoundMemberId =
        resolvedBuildMemberId ||
        resolvePublishedMemberIdFromLegacyServiceId(
          boundServiceId,
          publishedScopeMembers,
          studioScopeMembers,
        ) ||
        trimOptional(routeMemberSummary?.memberId);
      if (
        resolvedStudioTeamId &&
        resolvedBoundMemberId &&
        resolvedStudioScopeId
      ) {
        setTeamEntryCandidate({
          memberId: resolvedBoundMemberId,
          scopeId: resolvedStudioScopeId,
          teamId: resolvedStudioTeamId,
        });
      }
      const routedBoundMemberKey =
        buildBackendMemberKey(resolvedBoundMemberId) || boundMemberKey;
      setRecentlyBoundMemberKey(routedBoundMemberKey);
      setRecentlyBoundServiceId(boundServiceId);

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
          teamId: routeState.teamId || undefined,
          returnTo: routeState.returnTo || undefined,
          memberKey: routedBoundMemberKey,
          focus:
            buildPendingBindCandidate.kind === 'script'
              ? `script:${buildPendingBindCandidate.scriptId}`
              : undefined,
          step: 'bind',
          tab: 'bindings',
        }),
      );
    }

  }, [
    activeBuildFocusKey,
    buildPendingMemberSummary,
    buildWorkflowYamlBundle,
    buildPendingBindCandidate,
    routeSelectedBackendMemberId,
    queryClient,
    publishedScopeMembers,
    resolvedStudioScopeId,
    resolvedStudioTeamId,
    routeSelectedBackendMemberKey,
    routeState.memberKey,
    routeState.teamId,
    routeState.returnTo,
    selectedScriptId,
    selectedWorkflowMemberKey,
    scopeServicesQuery,
    studioMembersQueryKey,
    studioScopeMembers,
    observeMemberBindingRunOnce,
  ]);

  const openWorkspaceWorkflow = useCallback((workflowId: string) => {
    const normalizedWorkflowId = trimOptional(workflowId);
    setSelectedWorkflowId(normalizedWorkflowId);
    setSelectedScriptId('');
    setTemplateWorkflow('');
    setBuildSurface('editor');
    setStudioSurface('build');
  }, []);

  const openExecution = (executionId: string) => {
    setSelectedExecutionId(executionId);
    setStudioSurface('observe');
  };

  const openScopeScript = useCallback((scriptId: string) => {
    const normalizedScriptId = trimOptional(scriptId);
    setSelectedWorkflowId('');
    setSelectedScriptId(normalizedScriptId);
    setTemplateWorkflow('');
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
        readonly document?: StudioWorkflowDocument | null;
        readonly layout?: unknown;
        readonly selectedNodeId?: string;
        readonly yaml?: string;
      },
    ) => {
      const hasDocumentOverride = options?.document !== undefined;
      const nextWorkflow: StudioWorkflowFile = {
        ...savedWorkflow,
        document: hasDocumentOverride ? options.document ?? null : savedWorkflow.document,
        yaml: options?.yaml ?? savedWorkflow.yaml,
      };

      queryClient.setQueryData(
        ['studio-workflow', workflowWorkspaceContextKey, nextWorkflow.workflowId],
        nextWorkflow,
      );
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
      });

      setSelectedWorkflowId(nextWorkflow.workflowId);
      setSelectedScriptId('');
      setTemplateWorkflow('');
      setBuildSurface('editor');
      setStudioSurface('build');
      setDraftSourceKey(
        `workflow:${workflowWorkspaceContextKey}:${nextWorkflow.workflowId}`,
      );
      setDraftYaml(nextWorkflow.yaml);
      setDraftWorkflowName(nextWorkflow.name);
      setDraftFileName(nextWorkflow.fileName);
      setDraftDirectoryId(nextWorkflow.directoryId);
      setDraftWorkflowLayout(
        nextWorkflow.layout ||
          options?.layout ||
          draftWorkflowLayout ||
          buildStudioWorkflowLayout(nextWorkflow.name, workflowGraph.nodes),
      );
      if (hasDocumentOverride) {
        setEditableWorkflowDocument(
          cloneStudioWorkflowDocument(options.document ?? null),
        );
      }
      if (options?.selectedNodeId !== undefined) {
        setSelectedGraphNodeId(options.selectedNodeId);
      }
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

  const waitForAcceptedWorkflowDraft = useCallback(
    async (
      receipt: StudioWorkflowDraftCreateAcceptedReceipt,
    ): Promise<StudioWorkflowFile> => {
      if (!resolvedStudioScopeId) {
        throw new Error(
          'Workflow draft was accepted without a workspace scope.',
        );
      }

      const noticeMessage = describeWorkflowDraftAcceptedReceipt(receipt);
      setSaveNotice({
        type: 'info',
        message: noticeMessage,
      });
      void message.info(noticeMessage);

      const materializedWorkflow = await waitForWorkflowDraftMaterialized({
        receipt,
        scopeId: resolvedStudioScopeId,
      });
      await queryClient.invalidateQueries({
        queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
      });
      return materializedWorkflow;
    },
    [
      queryClient,
      resolvedStudioScopeId,
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

  const resolveWorkflowSavePayload = useCallback(
    async (
      pendingStepDraft?: {
        readonly stepId: string;
        readonly draft: StudioStepInspectorDraft;
      } | null,
    ): Promise<{
      readonly document?: StudioWorkflowDocument | null;
      readonly yaml: string;
      readonly layout: unknown;
      readonly selectedNodeId?: string;
    } | null> => {
      if (!pendingStepDraft) {
        return {
          yaml: draftYaml,
          layout:
            draftWorkflowLayout ||
            activeWorkflowFile?.layout ||
            buildStudioWorkflowLayout(activeWorkflowName, workflowGraph.nodes),
        };
      }

      const currentStepId = pendingStepDraft.stepId.trim();
      if (!currentStepId) {
        setSaveNotice({
          type: 'error',
          message: t("pages.studio.index.select.workflow.step.before.saving", "Select a workflow step before saving its draft changes."),
        });
        return null;
      }

      const document = await resolveEditableWorkflowDocument();
      if (!document) {
        return null;
      }

      const result = applyStepInspectorDraft(
        document,
        currentStepId,
        pendingStepDraft.draft,
      );
      const serialized = await studioApi.serializeYaml({
        document: result.document,
        availableWorkflowNames: workflowNames,
        availableStepTypes,
      });
      const nextLayout =
        draftWorkflowLayout ||
        activeWorkflowFile?.layout ||
        buildStudioWorkflowLayout(activeWorkflowName, workflowGraph.nodes);

      setDraftYaml(serialized.yaml);
      setEditableWorkflowDocument(cloneStudioWorkflowDocument(serialized.document));
      setSelectedGraphNodeId(result.nodeId);

      return {
        document: serialized.document,
        yaml: serialized.yaml,
        layout: nextLayout,
        selectedNodeId: result.nodeId,
      };
    },
    [
      activeWorkflowFile?.layout,
      activeWorkflowName,
      availableStepTypes,
      draftWorkflowLayout,
      draftYaml,
      resolveEditableWorkflowDocument,
      workflowGraph.nodes,
      workflowNames,
    ],
  );

  const handleSaveDraft = async (
    pendingStepDraft?: {
      readonly stepId: string;
      readonly draft: StudioStepInspectorDraft;
    } | null,
  ) => {
    const directoryId = resolvedDraftDirectoryId;
    if (!directoryId) {
      setSaveNotice({
        type: 'error',
        message: t("pages.studio.index.add.workflow.directory.in.config.2", "Add a workflow directory in Config before saving."),
      });
      return;
    }

    const workflowName = draftWorkflowName.trim();
    if (!workflowName) {
      setSaveNotice({
        type: 'error',
        message: t("pages.studio.index.workflow.name.is.required.before.3", "Workflow name is required before saving."),
      });
      return;
    }

    setSavePending(true);
    setSaveNotice(null);

    try {
      const savePayload = await resolveWorkflowSavePayload(pendingStepDraft);
      if (!savePayload) {
        return;
      }

      const saveResult = normalizeWorkflowSaveResult(
        await studioApi.saveWorkflow({
          workflowId: activeWorkflowFile?.workflowId || undefined,
          draftExists: activeWorkflowFile?.draftExists,
          scopeId: resolvedStudioScopeId || undefined,
          directoryId,
          workflowName,
          fileName: draftFileName,
          yaml: savePayload.yaml,
          layout: savePayload.layout,
        }),
      );
      const savedWorkflow =
        saveResult.kind === 'accepted'
          ? await waitForAcceptedWorkflowDraft(saveResult.receipt)
          : saveResult.workflow;

      await applySavedWorkflowSelection(savedWorkflow, {
        document: savePayload.document,
        layout: savePayload.layout,
        selectedNodeId: savePayload.selectedNodeId,
        yaml: savePayload.yaml,
      });
      const routeMemberSummary = resolveStudioMemberSummaryFromMemberKey(
        routeSelectedBackendMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      );
      const routeMemberWorkflowId = resolveWorkflowIdForMemberSummary(
        routeMemberSummary,
        [savedWorkflow, ...visibleWorkflowSummaries],
        savedWorkflow,
      );
      if (routeMemberWorkflowId === savedWorkflow.workflowId) {
        history.replace(buildStudioRoute({
          scopeId: resolvedStudioScopeId || undefined,
          teamId: routeState.teamId || undefined,
          returnTo: routeState.returnTo || undefined,
          memberKey: routeSelectedBackendMemberKey,
          focus: buildWorkflowMemberKeyFromSummary(savedWorkflow) || undefined,
          step: 'build',
          tab: 'studio',
        }));
      }
      void message.success(
        t("pages.studio.index.copy.2", "Saved to {value1}.", { value1: describeSavedWorkflowLocation(savedWorkflow) }),
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

    setCreateMemberTeamId(trimOptional(routeState.teamId));
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
    routeState.teamId,
    suggestedCreateWorkflowName,
  ]);

  const openCreateScriptDraftFlow = useCallback(async () => {
    if (!(await confirmScriptsStudioLeave())) {
      return;
    }

    setCreateMemberTeamId(trimOptional(routeState.teamId));
    setCreateMemberName(suggestedCreateScriptName);
    setCreateMemberKind('script');
    setCreateMemberDirectoryId(
      inventoryDirectoryId || inventoryDirectoryOptions[0]?.directoryId || '',
    );
    setCreateMemberModalOpen(true);
  }, [
    confirmScriptsStudioLeave,
    inventoryDirectoryId,
    inventoryDirectoryOptions,
    routeState.teamId,
    suggestedCreateScriptName,
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
  useEffect(() => {
    if (
      !createMemberModalOpen ||
      (createMemberKind !== 'workflow' &&
        createMemberKind !== 'script' &&
        createMemberKind !== 'gagent')
    ) {
      return;
    }

    createMemberNameInputRef.current?.focus();
  }, [createMemberKind, createMemberModalOpen]);

  const closeCreateMemberFlow = useCallback(() => {
    if (inventoryBusyKey === 'create') {
      return;
    }

    setCreateMemberModalOpen(false);
    setCreateMemberTeamId('');
  }, [inventoryBusyKey]);

  const handleCreateMember = useCallback(async (selectedCreateMemberKind: BuildMode) => {
    if (selectedCreateMemberKind !== 'workflow') {
      if (selectedCreateMemberKind === 'script' && !appContextQuery.data?.features.scripts) {
        void message.warning(
          t("pages.studio.index.script.builder.not.enabled", "Script builder is not enabled for this workspace."),
        );
        return;
      }

      if (!(await confirmScriptsStudioLeave())) {
        return;
      }

      if (selectedCreateMemberKind === 'script') {
        const scriptDisplayName = trimOptional(createMemberName);
        const scriptId = buildScriptIdSlug(scriptDisplayName);
        if (!scriptId) {
          void message.warning(
            t("pages.studio.index.script.name.required", "Script name is required."),
          );
          return;
        }

        if (availableScopeScriptIds.has(scriptId)) {
          void message.warning(
            t("pages.studio.index.workspace.script.same.id.exists", "A workspace script with the same id already exists."),
          );
          return;
        }

        if (
          studioScopeMembers.some(
            (member) =>
              normalizeStudioMemberBindingImplementationKind(
                member.implementationKind,
              ) === 'script' &&
              buildScriptIdSlug(member.memberId) === scriptId,
          )
        ) {
          void message.warning(
            t("pages.studio.index.script.member.same.id.exists", "A Script member with the same id already exists."),
          );
          return;
        }

        let createdScriptMember: StudioMemberSummary | null = null;
        if (resolvedStudioScopeId) {
          setInventoryBusyKey('create');
          setInventoryBusyAction('create');
          try {
            createdScriptMember = await studioApi.createMemberWithId({
              scopeId: resolvedStudioScopeId,
              memberId: scriptId,
              displayName: scriptDisplayName,
              implementationKind: 'script',
              ...(createMemberTeamId ? { teamId: createMemberTeamId } : {}),
            });
            setOptimisticStudioMembers((current) =>
              upsertStudioMemberSummary(
                current,
                createdScriptMember as StudioMemberSummary,
              ),
            );
            queryClient.setQueryData<StudioMemberRoster>(
              studioMembersQueryKey,
              (current) =>
                upsertStudioMemberRosterMember(
                  current,
                  resolvedStudioScopeId,
                  createdScriptMember as StudioMemberSummary,
                ),
            );
            void queryClient.invalidateQueries({
              queryKey: studioMembersQueryKey,
            });
          } catch (memberError) {
            setInventoryBusyKey('');
            setInventoryBusyAction('');
            void message.error(
              memberError instanceof Error
                ? t("pages.studio.index.script.member.authority.error.with.detail", "Studio could not register the Script member authority: {detail}", { detail: memberError.message })
                : t("pages.studio.index.script.member.authority.error", "Studio could not register the Script member authority."),
            );
            return;
          }
        }

        const nextDraft = {
          scriptId,
          displayName: scriptDisplayName,
        };
        saveStoredScriptDraft(resolvedStudioScopeId || undefined, nextDraft);
        setPendingScriptDraft(nextDraft);
        setSelectedWorkflowId('');
        setSelectedScriptId(scriptId);
        setTemplateWorkflow('');
        setScriptBuildState(null);
        const createdScriptMemberId = trimOptional(createdScriptMember?.memberId);
        if (createdScriptMemberId) {
          pinnedRouteBackendMemberIdRef.current = createdScriptMemberId;
          setPinnedRouteBackendMemberId(createdScriptMemberId);
        }
        setCreateMemberModalOpen(false);
        setCreateMemberTeamId('');
        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            teamId: createMemberTeamId || undefined,
            returnTo: routeState.returnTo || undefined,
            memberKey: createdScriptMemberId
              ? `member:${createdScriptMemberId}`
              : undefined,
            focus: `script:${scriptId}`,
            step: 'build',
            tab: 'scripts',
          }),
        );
        setBuildSurface('scripts');
        setStudioSurface('build');
        setInventoryBusyKey('');
        setInventoryBusyAction('');
        void message.success(
          createdScriptMember
            ? t("pages.studio.index.created.script.member.opened.draft", "Created Script member {member} and opened its draft.", { member: createdScriptMember.displayName })
            : t("pages.studio.index.created.script.draft", "Created Script draft."),
        );
        return;
      }

      const gAgentDisplayName = trimOptional(createMemberName);
      if (!gAgentDisplayName) {
        void message.warning(
          t("pages.studio.index.gagent.member.name.required", "GAgent member name is required."),
        );
        return;
      }

      if (
        studioScopeMembers.some(
          (member) =>
            normalizeComparableText(member.displayName) ===
              normalizeComparableText(gAgentDisplayName) &&
            normalizeStudioMemberBindingImplementationKind(member.implementationKind) ===
              'gagent',
        )
      ) {
        void message.warning(
          t("pages.studio.index.gagent.member.same.name.exists", "A GAgent member with the same name already exists."),
        );
        return;
      }

      if (!resolvedStudioScopeId) {
        void message.warning(
          t("pages.studio.index.connect.workspace.before.creating.gagent.member", "Connect a workspace before creating a GAgent member."),
        );
        return;
      }

      setInventoryBusyKey('create');
      setInventoryBusyAction('create');
      try {
        const createdGAgentMember = await studioApi.createMember({
          scopeId: resolvedStudioScopeId,
          displayName: gAgentDisplayName,
          implementationKind: 'gagent',
          ...(createMemberTeamId ? { teamId: createMemberTeamId } : {}),
        });
        setOptimisticStudioMembers((current) =>
          upsertStudioMemberSummary(current, createdGAgentMember),
        );
        queryClient.setQueryData<StudioMemberRoster>(
          studioMembersQueryKey,
          (current) =>
            upsertStudioMemberRosterMember(
              current,
              resolvedStudioScopeId,
              createdGAgentMember,
            ),
        );
        void queryClient.invalidateQueries({
          queryKey: studioMembersQueryKey,
        });
        setSelectedWorkflowId('');
        setSelectedScriptId('');
        setTemplateWorkflow('');
        pinnedRouteBackendMemberIdRef.current = createdGAgentMember.memberId;
        setPinnedRouteBackendMemberId(createdGAgentMember.memberId);
        setCreateMemberModalOpen(false);
        setCreateMemberTeamId('');
        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId,
            teamId: createMemberTeamId || undefined,
            returnTo: routeState.returnTo || undefined,
            memberKey: `member:${createdGAgentMember.memberId}`,
            step: 'build',
            tab: 'gagents',
          }),
        );
        setBuildSurface('gagent');
        setStudioSurface('build');
        void message.success(
          t("pages.studio.index.created.gagent.member.opened.build", "Created GAgent member {member} and opened Build.", { member: createdGAgentMember.displayName }),
        );
      } catch (memberError) {
        void message.error(
          memberError instanceof Error
            ? t("pages.studio.index.gagent.member.authority.error.with.detail", "Studio could not register the GAgent member authority: {detail}", { detail: memberError.message })
            : t("pages.studio.index.gagent.member.authority.error", "Studio could not register the GAgent member authority."),
        );
      } finally {
        setInventoryBusyKey('');
        setInventoryBusyAction('');
      }
      return;
    }

    const workflowName = trimOptional(createMemberName);
    const directoryId = trimOptional(createMemberDirectoryId) || inventoryDirectoryId;
    if (!workflowName) {
      void message.warning(
        t("pages.studio.index.member.name.required", "Member name is required."),
      );
      return;
    }

    if (!directoryId) {
      void message.error(
        t("pages.studio.index.add.workflow.directory.before.creating.here", "Add a workflow directory in Config before creating a workflow draft here."),
      );
      return;
    }

    if (
      visibleWorkflowSummaries.some(
        (workflow) => normalizeComparableText(workflow.name) === workflowName.toLowerCase(),
      )
    ) {
      void message.warning(
        t("pages.studio.index.workflow.draft.same.name.exists", "A workflow draft with the same name already exists."),
      );
      return;
    }

    if (
      studioScopeMembers.some(
        (member) =>
          normalizeComparableText(member.displayName) === workflowName.toLowerCase(),
      )
    ) {
      void message.warning(
        t("pages.studio.index.team.member.same.name.exists", "A team member with the same name already exists."),
      );
      return;
    }

    setInventoryBusyKey('create');
    setInventoryBusyAction('create');

    try {
      const saveResult = normalizeWorkflowSaveResult(
        await studioApi.saveWorkflow({
          scopeId: resolvedStudioScopeId || undefined,
          directoryId,
          workflowName,
          fileName: buildWorkflowFileName(workflowName),
          yaml: buildBlankDraftYaml(workflowName),
          layout: buildStudioWorkflowLayout(workflowName, []),
        }),
      );
      const savedWorkflow =
        saveResult.kind === 'accepted'
          ? await waitForAcceptedWorkflowDraft(saveResult.receipt)
          : saveResult.workflow;

      await applySavedWorkflowSelection(savedWorkflow);
      setCreateMemberModalOpen(false);

      if (!resolvedStudioScopeId) {
        setCreateMemberTeamId('');
        void message.success(
          t("pages.studio.index.created.workflow.draft.connect.workspace", "Created workflow draft for member {member}. Connect a workspace to register the backend member authority.", { member: workflowName }),
        );
        return;
      }

      try {
        const createdWorkflowMember = await studioApi.createMember({
          scopeId: resolvedStudioScopeId,
          displayName: workflowName,
          implementationKind: 'workflow',
          ...(createMemberTeamId ? { teamId: createMemberTeamId } : {}),
        });
        const workflowMemberForRoster =
          createMemberTeamId && !trimOptional(createdWorkflowMember.teamId)
            ? {
                ...createdWorkflowMember,
                teamId: createMemberTeamId,
              }
            : createdWorkflowMember;
        setOptimisticStudioMembers((current) =>
          upsertStudioMemberSummary(current, workflowMemberForRoster),
        );
        primeStudioMemberRoster(
          queryClient,
          studioMembersQueryKey,
          resolvedStudioScopeId,
          workflowMemberForRoster,
        );
        void queryClient.invalidateQueries({
          queryKey: studioMembersQueryKey,
        });
        const createdWorkflowMemberId = trimOptional(
          workflowMemberForRoster.memberId,
        );
        if (createdWorkflowMemberId) {
          pinnedRouteBackendMemberIdRef.current = createdWorkflowMemberId;
          setPinnedRouteBackendMemberId(createdWorkflowMemberId);
        }
        history.push(buildStudioRoute({
          scopeId: resolvedStudioScopeId,
          teamId: createMemberTeamId || undefined,
          returnTo: routeState.returnTo || undefined,
          memberKey: createdWorkflowMemberId
            ? buildBackendMemberKey(createdWorkflowMemberId)
            : undefined,
          focus: buildWorkflowMemberKeyFromSummary(savedWorkflow) || undefined,
          step: 'build',
          tab: 'studio',
        }));
        void message.success(
          t("pages.studio.index.created.member.opened.workflow.draft", "Created member {member} and opened its workflow draft.", { member: workflowName }),
        );
      } catch (memberError) {
        void message.error(
          memberError instanceof Error
            ? t("pages.studio.index.workflow.draft.created.member.authority.error.with.detail", "Workflow draft created, but Studio could not register the member authority: {detail}", { detail: memberError.message })
            : t("pages.studio.index.workflow.draft.created.member.authority.error", "Workflow draft created, but Studio could not register the member authority."),
        );
      }
      setCreateMemberTeamId('');
    } catch (error) {
      void message.error(
        error instanceof Error
          ? error.message
          : t("pages.studio.index.failed.create.workflow.draft.member", "Failed to create a workflow draft for this member."),
      );
    } finally {
      setInventoryBusyKey('');
      setInventoryBusyAction('');
    }
  }, [
    applySavedWorkflowSelection,
    appContextQuery.data?.features.scripts,
    availableScopeScriptIds,
    confirmScriptsStudioLeave,
    createMemberDirectoryId,
    createMemberName,
    createMemberTeamId,
    history,
    inventoryDirectoryId,
    queryClient,
    resolvedStudioScopeId,
    studioMembersQueryKey,
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
        window.prompt(
          t("pages.studio.index.rename.member.label", "Rename {member}", { member: currentWorkflowName }),
          currentWorkflowName,
        ) ?? '',
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
        void message.warning(
          t("pages.studio.index.workflow.member.same.name.exists", "A workflow member with the same name already exists."),
        );
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
        const saveResult = normalizeWorkflowSaveResult(
          await studioApi.saveWorkflow({
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
          }),
        );
        const savedWorkflow =
          saveResult.kind === 'accepted'
            ? await waitForAcceptedWorkflowDraft(saveResult.receipt)
            : saveResult.workflow;

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
        void message.success(
          t("pages.studio.index.renamed.workflow.member", "Renamed workflow member to {member}.", { member: nextWorkflowName }),
        );
      } catch (error) {
        void message.error(
          error instanceof Error
            ? error.message
            : t("pages.studio.index.failed.rename.workflow.member", "Failed to rename workflow member."),
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
        cancelText: t("pages.studio.index.keep.member.2", "Keep member"),
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
              {t("pages.studio.index.remove.2", "Remove")}<strong>{workflowLabel}</strong> {t("pages.studio.index.from.the.current.member.inventory.2", "from the current member inventory?")}</Typography.Text>
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
                  letterSpacing: 0,
                }}
              >
                {t("pages.studio.index.draft.only.2", "Draft only")}</Typography.Text>
              <Typography.Text
                style={{
                  color: '#7f1d1d',
                  fontSize: 12,
                  lineHeight: '18px',
                }}
              >
                {t("pages.studio.index.this.only.deletes.the.studio.2", "This only deletes the Studio workflow draft. Published bindings, live revisions, and historical runs stay intact.")}</Typography.Text>
            </div>
          </div>
        ),
        icon: <DeleteOutlined style={{ color: '#dc2626' }} />,
        okButtonProps: {
          danger: true,
        },
        okText: t("pages.studio.index.delete.member.2", "Delete member"),
        title: t("pages.studio.index.delete.workflow.member.2", "Delete workflow member"),
        width: 460,
        onOk: async () => {
          setInventoryBusyKey(memberKey);
          setInventoryBusyAction('delete');

          try {
            try {
              await studioApi.deleteWorkflow(
                workflowId,
                resolvedStudioScopeId || undefined,
              );
            } catch (error) {
              if (!isWorkflowNotFoundError(error)) {
                void message.error(
                  error instanceof Error
                    ? error.message
                    : t("pages.studio.index.failed.delete.workflow.member", "Failed to delete workflow member."),
                );
                return;
              }
            }

            queryClient.removeQueries({
              queryKey: ['studio-workflow', workflowWorkspaceContextKey, workflowId],
            });
            await queryClient.invalidateQueries({
              queryKey: ['studio-workspace-workflows', workflowWorkspaceContextKey],
            });
            if (resolvedStudioScopeId) {
              await queryClient.invalidateQueries({
                queryKey: studioMembersQueryKey,
              });
            }

            if (selectedWorkflowId === workflowId) {
              const fallbackWorkflowId =
                visibleWorkflowSummaries.find(
                  (workflow) => workflow.workflowId !== workflowId,
                )?.workflowId || '';
              if (fallbackWorkflowId) {
                openWorkspaceWorkflow(fallbackWorkflowId);
                history.replace(
                  buildStudioRoute({
                    scopeId: resolvedStudioScopeId || undefined,
                    teamId: routeState.teamId || undefined,
                    returnTo: routeState.returnTo || undefined,
                    focus: `workflow:${fallbackWorkflowId}`,
                    step: 'build',
                    tab: 'studio',
                  }),
                );
              } else {
                clearWorkflowBuildFocus();
                history.replace(
                  buildStudioRoute({
                    scopeId: resolvedStudioScopeId || undefined,
                    teamId: routeState.teamId || undefined,
                    returnTo: routeState.returnTo || undefined,
                    step: 'build',
                    tab: 'studio',
                  }),
                );
              }
            }

            void message.success(
              t("pages.studio.index.deleted.workflow.member", "Deleted workflow member {member}.", { member: workflowLabel }),
            );
          } catch (error) {
            void message.error(
              error instanceof Error
                ? error.message
                : t("pages.studio.index.failed.delete.workflow.member", "Failed to delete workflow member."),
            );
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
      history,
      queryClient,
      resolvedStudioScopeId,
      studioMembersQueryKey,
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
        message: t("pages.studio.index.workflow.name.is.required.before.4", "Workflow name is required before starting a draft run."),
      });
      return;
    }

    if (!draftYaml.trim()) {
      setRunNotice({
        type: 'error',
        message: t("pages.studio.index.workflow.yaml.is.required.before.2", "Workflow YAML is required before starting a draft run."),
      });
      return;
    }

    if (!prompt) {
      setRunNotice({
        type: 'error',
        message: t("pages.studio.index.execution.prompt.is.required.before.2", "Execution prompt is required before starting a draft run."),
      });
      return;
    }

    if (hasValidationError(activeWorkflowFindings)) {
      setRunNotice({
        type: 'error',
        message: t("pages.studio.index.resolve.studio.yaml.validation.errors.6", "Resolve Studio YAML validation errors before starting a draft run."),
      });
      return;
    }

    if (!scopeId) {
      setRunNotice({
        type: 'error',
        message: t("pages.studio.index.resolve.the.current.workspace.before.2", "Resolve the current workspace before starting a draft run."),
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
        message: t("pages.studio.index.allow.pop.ups.to.open.2", "Allow pop-ups to open execution logs in a new window."),
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
        message: t("pages.studio.index.stop.requested.for.the.active.2", "Stop requested for the active member run."),
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
        message: t("pages.studio.index.load.workflow.draft.before.editing.4", "Load a workflow draft before editing the description."),
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
          message: t("pages.studio.index.select.workflow.step.before.applying.2", "Select a workflow step before applying changes."),
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
        message: t("pages.studio.index.select.workflow.step.before.removing.2", "Select a workflow step before removing it."),
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
    (
      nextStudioSurface: StudioSurface,
      nextBuildSurface?: BuildSurface,
      routeMemberKey?: string,
    ) => {
      const resolvedBuildSurface = nextBuildSurface ?? buildSurface;
      if (nextStudioSurface === 'build' && resolvedBuildSurface === 'editor') {
        ensureActiveWorkflowDraftLoaded();
      }
      setBuildSurface(resolvedBuildSurface);
      setStudioSurface(nextStudioSurface);
      const normalizedRouteMemberKey = trimOptional(routeMemberKey);
      const explicitRouteMemberKey =
        nextStudioSurface === 'build'
          ? ''
          : buildBackendMemberKey(pinnedRouteBackendMemberIdRef.current);
      const nextRouteMemberKey =
        explicitRouteMemberKey || normalizedRouteMemberKey;
      if (nextRouteMemberKey && nextStudioSurface !== 'build') {
        const workflowBindFocus =
          activeBuildFocusKey ||
          selectedWorkflowMemberKey ||
          (routeBuildFocus.kind === 'workflow'
            ? (`workflow:${routeBuildFocus.value}` as StudioBuildFocus)
            : '');
        history.replace(buildStudioRoute({
          scopeId: resolvedStudioScopeId || undefined,
          teamId: routeState.teamId || undefined,
          returnTo: routeState.returnTo || undefined,
          memberKey: nextRouteMemberKey,
          focus:
            nextStudioSurface === 'bind' && resolvedBuildSurface === 'editor'
              ? workflowBindFocus || undefined
              : undefined,
          step:
            nextStudioSurface === 'bind'
              ? 'bind'
              : nextStudioSurface === 'invoke'
                ? 'invoke'
                : 'observe',
          tab:
            nextStudioSurface === 'bind'
              ? 'bindings'
              : nextStudioSurface === 'invoke'
                ? 'invoke'
                : 'executions',
        }));
      }
    },
    [
      activeBuildFocusKey,
      buildSurface,
      ensureActiveWorkflowDraftLoaded,
      resolvedStudioScopeId,
      routeBuildFocus.kind,
      routeBuildFocus.value,
      routeState.teamId,
      routeState.returnTo,
      selectedWorkflowMemberKey,
    ],
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
  const handleScriptBuildStateChange = useCallback(
    (state: StudioScriptBuildState | null) => {
      setScriptBuildState(state);
      if (state?.scriptId && !state.dirty && state.saveStatus === 'applied') {
        setLastAppliedScriptBuildState(state);
      }
    },
    [],
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
      const routeMemberSummary = resolveStudioMemberSummaryFromMemberKey(
        trimOptional(routeState.memberKey) ||
          buildBackendMemberKey(routeState.memberId) ||
          buildBackendMemberKey(routeSelectedBackendMemberId),
        publishedScopeMembers,
        studioScopeMembers,
      );
      const resolvedMemberId =
        trimOptional(routeMemberSummary?.memberId) ||
        resolvePublishedMemberIdFromLegacyServiceId(
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
      if (!resolvedMemberId) {
        void message.warning(
          t("pages.studio.index.invoke.needs.backend.team.member", "Invoke needs a backend Team member identity. Select or create a member before continuing."),
        );
        return;
      }

      history.replace(
        buildStudioRoute({
          scopeId: resolvedStudioScopeId || undefined,
          teamId: routeState.teamId || undefined,
          returnTo: routeState.returnTo || undefined,
          memberKey: buildBackendMemberKey(resolvedMemberId) || undefined,
          step: 'invoke',
          tab: 'invoke',
        }),
      );
      applyStudioTarget('invoke');
    },
    [
      applyStudioTarget,
      history,
      publishedScopeMembers,
      resolvedStudioScopeId,
      routeState.legacyServiceId,
      routeState.memberId,
      routeSelectedBackendMemberId,
      routeState.memberKey,
      routeState.teamId,
      routeState.returnTo,
      studioScopeMembers,
    ],
  );
  const pageTitle =
    isBuildEditorSurface
      ? t("pages.studio.index.workflow", "Workflow Build")
      : isBuildScriptsSurface
        ? t("pages.studio.index.copy.3", "Script behavior")
      : isBuildGAgentSurface
        ? t("pages.studio.index.gagent", "GAgent Build")
      : isBindSurface
        ? t("pages.studio.index.copy.4", "Member binding")
      : isInvokeSurface
        ? t("pages.studio.index.copy.5", "Member invoke")
      : isObserveSurface
        ? t("pages.studio.index.copy.6", "Test run")
        : t("pages.studio.index.copy.7", "Behavior definition");
  const currentLifecycleStep =
    isBindSurface
      ? 'bind'
      : isInvokeSurface
        ? 'invoke'
        : isObserveSurface
          ? 'observe'
          : 'build';
  const buildSurfaceMemberKey = useMemo(() => {
    const routeMemberKey = trimOptional(routeSelectedMemberKey);
    const routeMemberId =
      trimOptional(routeSelectedBackendMemberId) ||
      trimOptional(routeState.memberId);
    const routeLegacyServiceId = trimOptional(routeState.legacyServiceId);
    if (
      shouldTreatRouteMemberAsBuildFocus({
        routeMemberKey,
        routeMemberId,
        routeLegacyServiceId,
      })
    ) {
      return buildStudioFocusKey({
        routeMemberKey,
        routeMemberId,
        routeLegacyServiceId,
      });
    }

    return buildStudioFocusKey({
      activeBuildFocusKey,
      routeMemberKey,
      routeMemberId,
      routeLegacyServiceId,
    });
  }, [
    activeBuildFocusKey,
    routeSelectedBackendMemberId,
    routeSelectedMemberKey,
    routeState.legacyServiceId,
    routeState.memberId,
  ]);
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

    if (buildSurface === 'gagent') {
      return buildBackendMemberKey(
        trimOptional(routeSelectedBackendMemberId) ||
          trimOptional(routeState.memberId),
      );
    }

    return '';
  }, [
    activeWorkflowFile,
    availableScopeScriptIds,
    buildSurface,
    draftWorkflowName,
    routeSelectedBackendMemberId,
    routeSelectedMemberKey,
    routeState.memberId,
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
    const agentKind = trimOptional(selectedAgentKind);
    if (!agentKind) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ revision }) =>
        revision?.implementationKind === 'gagent' &&
        trimOptional(revision.staticAgentKind) === agentKind,
    );
    return trimOptional(matchedMember?.service.serviceId);
  }, [publishedScopeMembers, selectedAgentKind]);
  const activeGAgentPublishedMemberId = useMemo(() => {
    const agentKind = trimOptional(selectedAgentKind);
    if (!agentKind) {
      return '';
    }

    const matchedMember = publishedScopeMembers.find(
      ({ revision }) =>
        revision?.implementationKind === 'gagent' &&
        trimOptional(revision.staticAgentKind) === agentKind,
    );
    return trimOptional(matchedMember?.memberSummary?.memberId);
  }, [publishedScopeMembers, selectedAgentKind]);
  const activeBuildPublishedServiceId =
    activeBuildMode === 'workflow'
      ? activeWorkflowPublishedServiceId
      : activeBuildMode === 'script'
        ? activeScriptPublishedServiceId
        : activeGAgentPublishedServiceId;
  const activeBuildPublishedMemberId =
    activeBuildMode === 'workflow'
      ? activeWorkflowPublishedMemberId
      : activeBuildMode === 'script'
        ? activeScriptPublishedMemberId
        : activeGAgentPublishedMemberId;
  const selectedWorkflowRepresentsPublishedMember =
    Boolean(activeWorkflowPublishedServiceId);
  const selectedScriptRepresentsPublishedMember = Boolean(
    activeScriptPublishedServiceId,
  );
  const selectedGAgentRepresentsPublishedMember = Boolean(
    activeGAgentPublishedServiceId,
  );
  const selectedBuildRepresentsPublishedMember = Boolean(
    activeBuildPublishedServiceId,
  );
  const buildSurfaceMemberKeyUsesRouteServiceId = useMemo(() => {
    if (!routeSelectedMemberKey.startsWith('member:') || !buildSurfaceMemberKey) {
      return false;
    }

    const routeMember = resolveStudioMemberSummaryFromMemberKey(
      routeSelectedMemberKey,
      publishedScopeMembers,
      studioScopeMembers,
    );
    const routePublishedServiceId = normalizeComparableText(
      routeMember?.publishedServiceId,
    );
    if (!routePublishedServiceId) {
      return false;
    }

    const buildRouteValue =
      readScriptIdFromMemberKey(buildSurfaceMemberKey) ||
      readWorkflowMemberRouteValueFromMemberKey(buildSurfaceMemberKey);
    return normalizeComparableText(buildRouteValue) === routePublishedServiceId;
  }, [
    buildSurfaceMemberKey,
    publishedScopeMembers,
    routeSelectedMemberKey,
    studioScopeMembers,
  ]);
  const lifecycleSurfaceMemberKey =
    studioSurface === 'build'
      ? routeSelectedBackendMemberKey ||
        (buildSurfaceMemberKeyUsesRouteServiceId
          ? routeSelectedMemberKey
          : buildSurfaceMemberKey) ||
        routeSelectedMemberKey ||
        (activeBuildPublishedMemberId
          ? `member:${activeBuildPublishedMemberId}`
          : '')
      : routeSelectedBackendMemberKey ||
        routeSelectedMemberKey ||
        buildSurfaceMemberKey ||
        (activeBuildPublishedMemberId
          ? `member:${activeBuildPublishedMemberId}`
          : '');
  const currentFocusMemberKey =
    studioSurface === 'build' ? buildSurfaceMemberKey : lifecycleSurfaceMemberKey;
  const routeMemberToken = readMemberIdFromMemberKey(routeSelectedMemberKey);
  const canonicalLegacyRouteMemberKey =
    routeSelectedMember.kind === 'member' &&
    routeMemberToken &&
    routeSelectedBackendMemberId &&
    routeSelectedBackendMemberId !== routeMemberToken
      ? buildBackendMemberKey(routeSelectedBackendMemberId)
      : '';
  useEffect(() => {
    if (
      studioSurface !== 'build' ||
      (buildSurface !== 'editor' && buildSurface !== 'scripts')
    ) {
      return;
    }

    const memberSummary = resolveStudioMemberSummaryFromMemberKey(
      lifecycleSurfaceMemberKey,
      publishedScopeMembers,
      studioScopeMembers,
    );
    const implementationKind = normalizeStudioMemberBindingImplementationKind(
      memberSummary?.implementationKind,
    );
    if (implementationKind === 'gagent') {
      setSelectedWorkflowId('');
      setSelectedScriptId('');
      setTemplateWorkflow('');
      setBuildSurface('gagent');
      return;
    }

    if (implementationKind !== 'script') {
      return;
    }

    const scriptId =
      (routeBuildFocus.kind === 'script' ? routeBuildFocus.value : '') ||
      trimOptional(pendingScriptDraft?.scriptId) ||
      resolveLifecycleScriptId(
        lifecycleSurfaceMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      );
    if (!scriptId) {
      return;
    }

    setSelectedWorkflowId('');
    setSelectedScriptId(scriptId);
    setTemplateWorkflow('');
    setBuildSurface('scripts');
  }, [
    buildSurface,
    lifecycleSurfaceMemberKey,
    pendingScriptDraft?.scriptId,
    publishedScopeMembers,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    studioScopeMembers,
    studioSurface,
  ]);
  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    if (!isStudioLocation) {
      return;
    }

    if (getLocationSnapshot() !== locationSnapshot) {
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

    const routeRequestedBuildSurface = resolveRouteRequestedBuildSurface(routeState);
    if (
      studioSurface === 'build' &&
      routeRequestedBuildSurface &&
      routeRequestedBuildSurface !== buildSurface
    ) {
      return;
    }

    const tab: StudioTab | undefined =
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
    const pinnedRouteBackendMemberKey = buildBackendMemberKey(
      pinnedRouteBackendMemberIdRef.current,
    );
    const routeLegacyServiceId = trimOptional(routeState.legacyServiceId);
    const routeLegacyBackendMemberKey =
      routeLegacyServiceId && routeSelectedBackendMemberId
        ? buildBackendMemberKey(routeSelectedBackendMemberId)
        : '';
    if (routeLegacyServiceId && !routeLegacyBackendMemberKey) {
      return;
    }
    const suppressBuildMemberRoutePersistence =
      suppressBuildMemberRoutePersistenceRef.current &&
      studioSurface === 'build' &&
      !trimOptional(routeSelectedMemberKey) &&
      !trimOptional(routeState.focusKey);
    const persistedMemberKey =
      canonicalLegacyRouteMemberKey ||
      (studioSurface === 'build'
        ? routeLegacyBackendMemberKey ||
          (suppressBuildMemberRoutePersistence
            ? undefined
            : trimOptional(persistableBuildMemberKey) || undefined) ||
          undefined
        : pinnedRouteBackendMemberKey ||
          trimOptional(lifecycleSurfaceMemberKey) ||
          undefined);
    const persistedLifecycleFocus =
      studioSurface === 'bind' && routeBuildFocus.kind === 'script'
        ? (`script:${routeBuildFocus.value}` as const)
        : studioSurface === 'bind' &&
            routeBuildFocus.kind === 'workflow' &&
            !selectedWorkflowRepresentsPublishedMember
          ? (`workflow:${routeBuildFocus.value}` as const)
          : undefined;
    const persistedFocus =
      suppressBuildMemberRoutePersistence
        ? undefined
        : persistedLifecycleFocus ||
          (persistBuildFocusRoute &&
          trimOptional(activeBuildFocusKey) !== trimOptional(persistedMemberKey)
            ? activeBuildFocusKey || undefined
            : undefined);

    history.replace(buildStudioRoute({
      scopeId: resolvedStudioScopeId || undefined,
      teamId: routeState.teamId || undefined,
      returnTo: routeState.returnTo || undefined,
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
    if (
      suppressBuildMemberRoutePersistence &&
      !trimOptional(selectedWorkflowId) &&
      !trimOptional(selectedScriptId) &&
      !trimOptional(templateWorkflow)
    ) {
      suppressBuildMemberRoutePersistenceRef.current = false;
    }
  }, [
    appliedRouteSnapshot,
    activeBuildFocusKey,
    buildSurface,
    canonicalLegacyRouteMemberKey,
    isStudioLocation,
    lifecycleSurfaceMemberKey,
    locationSnapshot,
    logsPopoutMode,
    persistableBuildMemberKey,
    resolvedStudioScopeId,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    routeMemberToken,
    routeSelectedBackendMemberId,
    routeSelectedMember.kind,
    routeSelectedMemberKey,
    routeState.legacyServiceId,
    routeState.focusKey,
    routeState.memberKey,
    routeState.step,
    routeState.teamId,
    routeState.returnTo,
    runPrompt,
    selectedWorkflowRepresentsPublishedMember,
    selectedWorkflowId,
    selectedScriptId,
    selectedExecutionId,
    studioSurface,
    templateWorkflow,
    visibleWorkflowSummaries,
    workflowsQuery.isLoading,
  ]);
  const workbenchMemberKey =
    routeSelectedBackendMemberKey || currentFocusMemberKey;
  const buildSurfaceSelectedMemberKey =
    studioSurface === 'build'
      ? routeSelectedBackendMemberKey ||
        (
          currentFocusMemberKey.startsWith('workflow:') ||
          currentFocusMemberKey.startsWith('script:')
            ? activeBuildPublishedMemberId
              ? `member:${activeBuildPublishedMemberId}`
              : currentFocusMemberKey
            : ''
        )
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
    () => {
      const legacyRouteServiceId = trimOptional(legacyRouteServiceIdRef.current);
      if (
        legacyRouteServiceId &&
        currentRouteMemberToken === legacyRouteServiceId &&
        !routeSelectedBackendMemberId
      ) {
        return '';
      }

      return (
        trimOptional(routeSelectedBackendMemberId) ||
        trimOptional(workbenchStudioMemberSummary?.memberId) ||
        readMemberIdFromMemberKey(workbenchMemberKey) ||
        readMemberIdFromMemberKey(routeState.memberKey) ||
        trimOptional(routeState.memberId)
      );
    },
    [
      currentRouteMemberToken,
      routeSelectedBackendMemberId,
      routeState.memberKey,
      routeState.memberId,
      workbenchMemberKey,
      workbenchStudioMemberSummary?.memberId,
    ],
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
  const workbenchStudioMemberDetailMatchesSelection =
    Boolean(workbenchStudioMemberId) &&
    trimOptional(workbenchStudioMemberDetailQuery.data?.summary?.memberId) ===
      trimOptional(workbenchStudioMemberId);
  const workbenchStudioMember = useMemo(
    () =>
      workbenchStudioMemberDetailMatchesSelection
        ? workbenchStudioMemberDetailQuery.data?.summary ?? workbenchStudioMemberSummary
        : workbenchStudioMemberSummary,
    [
      workbenchStudioMemberDetailMatchesSelection,
      workbenchStudioMemberDetailQuery.data?.summary,
      workbenchStudioMemberSummary,
    ],
  );
  useEffect(() => {
    if (!isStudioLocation || studioSurface !== 'bind') {
      return;
    }

    if (!routeSelectedBackendMemberKey || routeBuildFocus.kind === 'workflow') {
      return;
    }

    const memberSummary = workbenchStudioMember ?? workbenchStudioMemberSummary;
    const memberImplementationRef = workbenchStudioMemberDetailMatchesSelection
      ? workbenchStudioMemberDetailQuery.data?.implementationRef ?? null
      : null;
    if (
      normalizeStudioMemberBindingImplementationKind(
        memberImplementationRef?.implementationKind ||
          memberSummary?.implementationKind,
      ) !== 'workflow'
    ) {
      return;
    }

    const memberPublishedServiceId =
      trimOptional(memberSummary?.publishedServiceId) ||
      trimOptional(workbenchPublishedServiceId);
    const memberPublishedServiceExists =
      Boolean(memberPublishedServiceId) &&
      (publishedScopeServices.some(
        (service) => trimOptional(service.serviceId) === memberPublishedServiceId,
      ) ||
        trimOptional(recentlyBoundServiceId) === memberPublishedServiceId
      );
    if (memberPublishedServiceExists) {
      return;
    }

    const workflowId = resolveWorkflowIdForMemberDetail(
      {
        implementationRefWorkflowId: memberImplementationRef?.workflowId,
        memberSummary,
      },
      visibleWorkflowSummaries,
      activeWorkflowFile,
    );
    if (!workflowId) {
      return;
    }

    const memberId =
      trimOptional(memberSummary?.memberId) ||
      readMemberIdFromMemberKey(routeSelectedBackendMemberKey);
    if (!memberId) {
      return;
    }

    const selectedWorkflowSummary = visibleWorkflowSummaries.find(
      (workflow) => trimOptional(workflow.workflowId) === workflowId,
    );
    const workflowFocusKey =
      buildWorkflowMemberKeyFromSummary(selectedWorkflowSummary) ||
      (`workflow:${workflowId}` as const);
    const memberRouteKey = buildBackendMemberKey(memberId);
    pinnedRouteBackendMemberIdRef.current = memberId;
    setPinnedRouteBackendMemberId(memberId);
    setSelectedWorkflowId((currentWorkflowId) =>
      trimOptional(currentWorkflowId) === workflowId
        ? currentWorkflowId
        : workflowId,
    );
    setSelectedScriptId('');
    setTemplateWorkflow('');
    setBuildSurface('editor');
    history.replace(buildStudioRoute({
      scopeId: resolvedStudioScopeId || undefined,
      teamId: routeState.teamId || undefined,
      returnTo: routeState.returnTo || undefined,
      memberKey: memberRouteKey,
      focus: workflowFocusKey,
      step: 'bind',
      tab: 'bindings',
    }));
  }, [
    activeWorkflowFile,
    history,
    isStudioLocation,
    publishedScopeServices,
    recentlyBoundServiceId,
    resolvedStudioScopeId,
    routeBuildFocus.kind,
    routeSelectedBackendMemberKey,
    routeState.returnTo,
    routeState.teamId,
    studioSurface,
    visibleWorkflowSummaries,
    workbenchPublishedServiceId,
    workbenchStudioMember,
    workbenchStudioMemberDetailMatchesSelection,
    workbenchStudioMemberDetailQuery.data?.implementationRef,
    workbenchStudioMemberSummary,
  ]);
  const workbenchStudioMemberBinding = useMemo(
    () =>
      workbenchStudioMemberDetailMatchesSelection
        ? workbenchStudioMemberDetailQuery.data?.lastBinding ?? null
        : null,
    [
      workbenchStudioMemberDetailMatchesSelection,
      workbenchStudioMemberDetailQuery.data?.lastBinding,
    ],
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
  const lockedBuildMode = useMemo<BuildMode | ''>(() => {
    const implementationKind = normalizeStudioMemberBindingImplementationKind(
      workbenchStudioMemberDetailQuery.data?.implementationRef
        ?.implementationKind ||
        workbenchStudioMember?.implementationKind ||
        workbenchStudioMemberSummary?.implementationKind ||
        workbenchPublishedServiceRevision?.implementationKind,
    );

    return implementationKind === 'workflow' ||
      implementationKind === 'script' ||
      implementationKind === 'gagent'
      ? implementationKind
      : '';
  }, [
    workbenchPublishedServiceRevision?.implementationKind,
    workbenchStudioMember?.implementationKind,
    workbenchStudioMemberDetailQuery.data?.implementationRef?.implementationKind,
    workbenchStudioMemberSummary?.implementationKind,
  ]);
  const buildModeLocked = Boolean(lockedBuildMode);
  const handleSelectBuildMode = useCallback(
    async (nextBuildMode: BuildMode) => {
      if (nextBuildMode === activeBuildMode) {
        return;
      }

      if (buildModeLocked) {
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
      buildModeLocked,
      confirmScriptsStudioLeave,
    ],
  );
  const workbenchMemberIsTeamEntry = Boolean(
    workbenchStudioMemberId &&
      resolvedStudioTeamId &&
      trimOptional(studioTeamSummaryQuery.data?.entryMemberId) ===
        trimOptional(workbenchStudioMemberId),
  );
  const resolveTeamEntryCandidate = useCallback((): StudioTeamEntryCandidate | null => {
    const memberId =
      trimOptional(teamEntryCandidate?.memberId) ||
      trimOptional(workbenchStudioMemberId) ||
      trimOptional(workbenchStudioMember?.memberId) ||
      trimOptional(workbenchStudioMemberSummary?.memberId) ||
      trimOptional(routeSelectedBackendMemberId);
    const scope = trimOptional(teamEntryCandidate?.scopeId) || resolvedStudioScopeId;
    const team = trimOptional(teamEntryCandidate?.teamId) || resolvedStudioTeamId;

    if (!memberId || !scope || !team) {
      return null;
    }

    return {
      memberId,
      scopeId: scope,
      teamId: team,
    };
  }, [
    resolvedStudioScopeId,
    resolvedStudioTeamId,
    routeSelectedBackendMemberId,
    teamEntryCandidate,
    workbenchStudioMember?.memberId,
    workbenchStudioMemberId,
    workbenchStudioMemberSummary?.memberId,
  ]);
  const waitForTeamEntryVisibility = useCallback(
    async (candidate: StudioTeamEntryCandidate) => {
      for (
        let attempt = 0;
        attempt < studioTeamEntryVisibilityAttempts;
        attempt += 1
      ) {
        const summary = await queryClient.fetchQuery({
          queryFn: () => studioApi.getTeam(candidate.scopeId, candidate.teamId),
          queryKey: [
            'teams',
            'team-summary',
            candidate.scopeId,
            candidate.teamId,
          ],
          staleTime: 0,
        });
        if (hasTeamEntryMember(summary, candidate.memberId)) {
          return true;
        }
        if (attempt < studioTeamEntryVisibilityAttempts - 1) {
          await delay(studioTeamEntryVisibilityRetryDelayMs);
        }
      }

      return false;
    },
    [queryClient],
  );
  const handleSetTeamEntryFromStudio = useCallback(
    async (options?: { test?: boolean }) => {
      const candidate = resolveTeamEntryCandidate();
      if (!candidate) {
        void message.warning(
          t("pages.studio.index.resolve.team.member.before.entry", "Resolve a Team member before setting Team entry."),
        );
        return;
      }

      setTeamEntryActionBusy(true);
      try {
        const alreadyEntry =
          trimOptional(studioTeamSummaryQuery.data?.entryMemberId) ===
          trimOptional(candidate.memberId);
        if (alreadyEntry && options?.test) {
          history.push(
            buildTeamDetailHref({
              memberId: candidate.memberId,
              scopeId: candidate.scopeId,
              tab: 'overview',
              testTeam: true,
              teamId: candidate.teamId,
            }),
          );
          return;
        }

        const updatedTeam = await studioApi.setTeamEntryMember(
          candidate.scopeId,
          candidate.teamId,
          candidate.memberId,
        );
        if (updatedTeam) {
          queryClient.setQueryData(
            ['teams', 'team-summary', candidate.scopeId, candidate.teamId],
            updatedTeam,
          );
        }
        await Promise.all([
          queryClient.invalidateQueries({
            queryKey: ['teams', 'team-summary', candidate.scopeId, candidate.teamId],
          }),
          queryClient.invalidateQueries({
            queryKey: ['teams', 'team-members', candidate.scopeId, candidate.teamId],
          }),
          queryClient.invalidateQueries({
            queryKey: ['teams', 'roster', candidate.scopeId],
          }),
        ]);
        const entryVisible = options?.test
          ? await waitForTeamEntryVisibility(candidate)
          : false;
        const targetHref = buildTeamDetailHref({
          memberId: candidate.memberId,
          scopeId: candidate.scopeId,
          tab: 'overview',
          testTeam: options?.test && entryVisible,
          teamId: candidate.teamId,
        });
        void message.info(t("pages.studio.index.team.entry", "Team entry change submitted. Waiting for sync confirmation."));
        if (options?.test) {
          if (!entryVisible) {
            void message.warning(
              t("pages.studio.index.team.entry.team.detail", "Team entry was accepted by the backend, but the read model has not confirmed the new entry member yet. Retry Test Team from Team Detail shortly."),
            );
          }
          history.push(targetHref);
        }
      } catch (error) {
        void message.error(
          error instanceof Error ? error.message : String(error),
        );
      } finally {
        setTeamEntryActionBusy(false);
      }
    },
    [
      history,
      queryClient,
      resolveTeamEntryCandidate,
      studioTeamSummaryQuery.data?.entryMemberId,
      waitForTeamEntryVisibility,
    ],
  );
  useEffect(() => {
    if (studioSurface !== 'build') {
      return;
    }

    const implementationKind = normalizeStudioMemberBindingImplementationKind(
      workbenchStudioMemberDetailQuery.data?.implementationRef
        ?.implementationKind ||
        workbenchStudioMember?.implementationKind ||
        workbenchPublishedServiceRevision?.implementationKind,
    );
    if (implementationKind === 'gagent') {
      const agentKind =
        trimOptional(
          workbenchStudioMemberDetailQuery.data?.implementationRef?.agentKind,
        ) ||
        trimOptional(workbenchPublishedServiceRevision?.staticAgentKind);
      if (agentKind) {
        setSelectedAgentKind((current) =>
          trimOptional(current) === agentKind ? current : agentKind,
        );
      }
      if (buildSurface !== 'gagent') {
        setSelectedWorkflowId('');
        setSelectedScriptId('');
        setTemplateWorkflow('');
        setBuildSurface('gagent');
      }
      return;
    }

    if (buildSurface !== 'editor') {
      return;
    }

    if (implementationKind !== 'script') {
      return;
    }

    const scriptId =
      (routeBuildFocus.kind === 'script' ? routeBuildFocus.value : '') ||
      trimOptional(pendingScriptDraft?.scriptId) ||
      trimOptional(
        workbenchStudioMemberDetailQuery.data?.implementationRef?.scriptId,
      ) ||
      trimOptional(workbenchPublishedServiceRevision?.scriptId) ||
      (trimOptional(workbenchStudioMember?.publishedServiceId)
        ? ''
        : trimOptional(workbenchStudioMember?.displayName));

    setSelectedWorkflowId('');
    if (scriptId) {
      setSelectedScriptId(scriptId);
    }
    setTemplateWorkflow('');
    setBuildSurface('scripts');
  }, [
    buildSurface,
    pendingScriptDraft?.scriptId,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    studioSurface,
    workbenchPublishedServiceRevision?.implementationKind,
    workbenchPublishedServiceRevision?.scriptId,
    workbenchPublishedServiceRevision?.staticAgentKind,
    workbenchStudioMember?.displayName,
    workbenchStudioMember?.implementationKind,
    workbenchStudioMember?.publishedServiceId,
    workbenchStudioMemberDetailQuery.data?.implementationRef?.agentKind,
    workbenchStudioMemberDetailQuery.data?.implementationRef?.implementationKind,
    workbenchStudioMemberDetailQuery.data?.implementationRef?.scriptId,
  ]);
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
        const lifecycleMemberKey =
          lifecycleSurfaceMemberKey || currentFocusMemberKey;
        const lifecycleScriptId =
          trimOptional(
            workbenchStudioMemberDetailQuery.data?.implementationRef?.scriptId,
          ) ||
          trimOptional(workbenchPublishedServiceRevision?.scriptId) ||
          resolveLifecycleScriptId(
            lifecycleMemberKey,
            publishedScopeMembers,
            studioScopeMembers,
          ) ||
          (readScriptIdFromMemberKey(lifecycleMemberKey)
            ? trimOptional(selectedScriptId)
            : '');
        const resolvedBuildSurface = resolveLifecycleBuildSurface({
          fallback: lifecycleScriptId ? 'scripts' : buildSurface,
          memberKey: lifecycleMemberKey,
          publishedMembers: publishedScopeMembers,
          studioScopeMembers,
        });
        if (lifecycleScriptId) {
          setSelectedWorkflowId('');
          setSelectedScriptId(lifecycleScriptId);
          setTemplateWorkflow('');
        } else if (resolvedBuildSurface === 'scripts') {
          setSelectedWorkflowId('');
          setSelectedScriptId('');
          setTemplateWorkflow('');
        } else {
          const lifecycleMemberSummary = resolveStudioMemberSummaryFromMemberKey(
            lifecycleMemberKey,
            publishedScopeMembers,
            studioScopeMembers,
          );
          const lifecycleWorkflowRouteValue = readWorkflowMemberRouteValueFromMemberKey(
            lifecycleMemberKey,
          );
          const resolvedLifecycleWorkflowId = lifecycleWorkflowRouteValue
            ? resolveWorkflowIdFromRouteValue(
                lifecycleWorkflowRouteValue,
                visibleWorkflowSummaries,
                {
                  allowDirectIdFallback: true,
                  workflowFile: activeWorkflowFile,
                },
              )
            : resolveWorkflowIdForMemberSummary(
                lifecycleMemberSummary,
                visibleWorkflowSummaries,
                activeWorkflowFile,
              );
          if (resolvedLifecycleWorkflowId) {
            setSelectedWorkflowId(resolvedLifecycleWorkflowId);
            setSelectedScriptId('');
            setTemplateWorkflow('');
          }
        }
        applyStudioTarget('build', resolvedBuildSurface);
        return;
      }

      if (stepKey === 'bind') {
        applyStudioTarget('bind', undefined, lifecycleSurfaceMemberKey);
        return;
      }

      if (stepKey === 'invoke') {
        applyStudioTarget('invoke', undefined, lifecycleSurfaceMemberKey);
        return;
      }

      if (stepKey === 'observe') {
        applyStudioTarget('observe', undefined, lifecycleSurfaceMemberKey);
      }
    },
    [
      applyStudioTarget,
      activeWorkflowFile,
      buildSurface,
      confirmScriptsStudioLeave,
      currentFocusMemberKey,
      lifecycleSurfaceMemberKey,
      publishedScopeMembers,
      selectedScriptId,
      studioSurface,
      studioScopeMembers,
      visibleWorkflowSummaries,
      workbenchPublishedServiceRevision?.scriptId,
      workbenchStudioMemberDetailQuery.data?.implementationRef?.scriptId,
    ],
  );
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
      workbenchPublishedServiceId
        ? scopeRuntimeApi.listServiceRuns(
            resolvedStudioScopeId,
            workbenchPublishedServiceId,
            {
              take: 12,
            },
          )
        : scopeRuntimeApi.listMemberRuns(
            resolvedStudioScopeId,
            workbenchStudioMemberId,
            {
              take: 12,
            },
          ).then((catalog) => ({
            displayName: catalog.displayName,
            runs: catalog.runs.map(toScopeServiceRunSummary),
            scopeId: catalog.scopeId,
            serviceId: catalog.publishedServiceId,
            serviceKey: catalog.publishedServiceKey,
          })),
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
      workbenchPublishedServiceId
        ? scopeRuntimeApi.getServiceRunAudit(
            resolvedStudioScopeId,
            workbenchPublishedServiceId,
            selectedExecutionId,
            {
              actorId:
                trimOptional(selectedObserveBackendRunSummary?.actorId) || undefined,
            },
          )
        : scopeRuntimeApi.getMemberRunAudit(
            resolvedStudioScopeId,
            workbenchStudioMemberId,
            selectedExecutionId,
            {
              actorId:
                trimOptional(selectedObserveBackendRunSummary?.actorId) || undefined,
            },
          ).then(toScopeServiceRunAuditSnapshot),
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
  const currentCanonicalMemberId =
    trimOptional(workbenchStudioMemberId) ||
    trimOptional(workbenchStudioMember?.memberId) ||
    trimOptional(workbenchStudioMemberSummary?.memberId) ||
    trimOptional(routeState.memberId);
  const hasSelectedMemberFocus = Boolean(workbenchMemberKey);
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
  const recentBindMemberSummary = useMemo(
    () =>
      isBindSurface && recentBindSelectedMemberServiceId
        ? resolveStudioMemberSummaryFromMemberKey(
            recentlyBoundMemberKey,
            publishedScopeMembers,
            studioScopeMembers,
          )
        : null,
    [
      isBindSurface,
      publishedScopeMembers,
      recentBindSelectedMemberServiceId,
      recentlyBoundMemberKey,
      studioScopeMembers,
    ],
  );
  const explicitRouteBackendMemberSummary = useMemo(
    () =>
      routeSelectedBackendMemberKey
        ? resolveStudioMemberSummaryFromMemberKey(
            routeSelectedBackendMemberKey,
            publishedScopeMembers,
            studioScopeMembers,
          )
        : null,
    [
      routeSelectedBackendMemberKey,
      publishedScopeMembers,
      studioScopeMembers,
    ],
  );
  const currentMemberLabel = !hasSelectedMemberFocus
    ? t("pages.studio.index.select.member", "Select a member")
    : workbenchMemberKey.startsWith('workflow:')
        ? trimOptional(activeWorkflowName) || t("pages.studio.index.workflow.member", "Workflow member")
    : workbenchMemberKey.startsWith('script:')
        ? trimOptional(selectedScriptId) || 'Script member'
    : workbenchMemberKey.startsWith('member:')
            ? trimOptional(recentBindMemberSummary?.displayName) ||
              trimOptional(explicitRouteBackendMemberSummary?.displayName) ||
              trimOptional(workbenchStudioMemberSummary?.displayName) ||
              trimOptional(workbenchStudioMember?.displayName) ||
              trimOptional(routeSelectedBackendMemberId) ||
              trimOptional(workbenchPublishedServiceRevision?.workflowName) ||
              trimOptional(workbenchPublishedServiceRevision?.scriptId) ||
              trimOptional(workbenchPublishedServiceRevision?.staticAgentKind) ||
              trimOptional(workbenchPublishedService?.displayName) ||
              trimOptional(workbenchPublishedService?.serviceId) ||
              trimOptional(routeSelectedBackendMemberId) ||
              t("pages.studio.index.current.member", "Current member")
            : trimOptional(activeWorkflowName) ||
              (isBuildScriptsSurface ? trimOptional(selectedScriptId) : '') ||
              t("pages.studio.index.current.member", "Current member");
  const currentMemberImplementationLabel = !hasSelectedMemberFocus
    ? ''
    : workbenchMemberKey.startsWith('member:')
      ? describeMemberImplementationLabel(
          workbenchStudioMember?.implementationKind ||
            workbenchStudioMemberSummary?.implementationKind ||
            workbenchPublishedServiceRevision?.implementationKind,
        )
      : isBuildGAgentSurface
        ? 'GAgent implementation'
        : selectedWorkflowId || templateWorkflow
          ? 'Workflow implementation'
          : selectedScriptId
            ? 'Script implementation'
            : trimOptional(selectedAgentKind)
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
                  trimOptional(routeState.legacyServiceId) ||
                  (workbenchStudioMember
                    ? formatStudioMemberLifecycleStage(
                        workbenchStudioMember.lifecycleStage,
                      )
                    : '') ||
                  trimOptional(workbenchStudioMemberBinding?.revisionId) ||
                  trimOptional(workbenchPublishedServiceRevision?.revisionId) ||
                  trimOptional(workbenchPublishedService?.deploymentStatus) ||
              t("pages.studio.index.published.member", "Published member"),
              }) || t("pages.studio.index.published.member.ready", "Published member ready for callable runtime inspection.")
            : formatStudioAssetMeta({
                primary: currentMemberImplementationLabel,
                secondary:
                  trimOptional(routeState.legacyServiceId) ||
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
        trimOptional(routeState.legacyServiceId) ||
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
  const bindSelectedMemberServiceId =
    currentSelectedMemberServiceId ||
    (isBindSurface ? recentBindSelectedMemberServiceId : '');
  const bindPublishedService = useMemo(
    () => {
      if (!bindSelectedMemberServiceId) {
        return null;
      }

      const publishedService =
        publishedScopeServices.find(
          (service) => service.serviceId === bindSelectedMemberServiceId,
        ) ?? null;
      if (publishedService) {
        return publishedService;
      }

      const recentService = recentlyBoundServiceRef.current;
      return trimOptional(recentService?.serviceId) === bindSelectedMemberServiceId
        ? recentService
        : null;
    },
    [bindSelectedMemberServiceId, publishedScopeServices],
  );
  const workbenchMemberMatchesPendingCandidate = useMemo(() => {
    if (!buildPendingBindCandidate) {
      return false;
    }

    if (
      workbenchMemberKey.startsWith('workflow:') ||
      workbenchMemberKey.startsWith('script:')
    ) {
      return true;
    }

    if (!workbenchMemberKey.startsWith('member:')) {
      return false;
    }

    if (
      buildPendingBindCandidate.kind === 'script' &&
      routeBuildFocus.kind === 'script' &&
      trimOptional(routeBuildFocus.value) === trimOptional(selectedScriptId)
    ) {
      return true;
    }

    const routeMemberId = readMemberIdFromMemberKey(workbenchMemberKey);
    const workbenchMemberKind = normalizeStudioMemberBindingImplementationKind(
      workbenchStudioMember?.implementationKind ||
        workbenchStudioMemberSummary?.implementationKind,
    );
    if (workbenchMemberKind === buildPendingBindCandidate.kind) {
      return true;
    }

    const pendingSummaryKind = normalizeStudioMemberBindingImplementationKind(
      buildPendingMemberSummary?.implementationKind,
    );
    const pendingSummaryMemberId = trimOptional(
      buildPendingMemberSummary?.memberId,
    );
    return (
      pendingSummaryKind === buildPendingBindCandidate.kind &&
      (!pendingSummaryMemberId ||
        !routeMemberId ||
        pendingSummaryMemberId === routeMemberId)
    );
  }, [
    buildPendingBindCandidate,
    buildPendingMemberSummary?.implementationKind,
    buildPendingMemberSummary?.memberId,
    routeBuildFocus.kind,
    routeBuildFocus.value,
    selectedScriptId,
    workbenchMemberKey,
    workbenchStudioMember?.implementationKind,
    workbenchStudioMemberSummary?.implementationKind,
  ]);
  const bindPendingCandidate =
    buildPendingBindCandidate &&
    !bindPublishedService &&
    workbenchMemberMatchesPendingCandidate
      ? buildPendingBindCandidate
      : null;
  const bindTargetService = useMemo(
    () => {
      if (!bindSelectedMemberServiceId || bindPendingCandidate) {
        return null;
      }

      return bindPublishedService;
    },
    [
      bindPendingCandidate,
      bindPublishedService,
      bindSelectedMemberServiceId,
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
  const bindInitialEndpointId = bindSelectedMemberServiceId
    ? currentBindingSelectionServiceId === bindSelectedMemberServiceId &&
      currentBindingSelectionEndpointId
      ? currentBindingSelectionEndpointId
      : bindTargetDefaultEndpointId
    : '';
  const hasInvokeTargetMemberSelection =
    Boolean(workbenchStudioMemberId);
  const invokeTargetServiceId =
    hasInvokeTargetMemberSelection
      ? currentSelectedMemberServiceId
      : currentInvokeSelectionServiceId ||
        currentBindingSelectionServiceId ||
        currentSelectedMemberServiceId ||
        trimOptional(workbenchPublishedService?.serviceId) ||
        trimOptional(routeState.legacyServiceId);
  const invokeTargetService = useMemo(
    () => {
      if (!invokeTargetServiceId) {
        return null;
      }

      const matchedService =
        runtimeConsoleServices.find(
          (service) => service.serviceId === invokeTargetServiceId,
        ) ?? null;
      if (matchedService) {
        return matchedService;
      }

      return null;
    },
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
    return resolveStudioServiceDefaultEndpointId(invokeTargetService);
  }, [invokeTargetService]);
  const invokeTargetHasDefaultChatEndpoint = Boolean(
    invokeTargetService?.endpoints.some(isChatServiceEndpoint),
  );
  const invokeInitialEndpointId =
    currentInvokeSelectionServiceId === invokeTargetServiceId &&
    currentInvokeSelectionEndpointId &&
    !invokeTargetHasDefaultChatEndpoint
      ? currentInvokeSelectionEndpointId
      : currentBindingSelectionServiceId === invokeTargetServiceId &&
          currentBindingSelectionEndpointId &&
          !invokeTargetHasDefaultChatEndpoint
        ? currentBindingSelectionEndpointId
        : invokeTargetDefaultEndpointId;
  const invokeEmptyState = useMemo(() => {
    if (
      hasInvokeTargetMemberSelection &&
      invokeTargetService &&
      invokeTargetService.endpoints.length > 0
    ) {
      return null;
    }

    if (hasSelectedMemberFocus && !hasInvokeTargetMemberSelection) {
      return {
        message: t("pages.studio.index.copy.8", "The current selection cannot be invoked directly yet."),
        description:
          t("pages.studio.index.copy.9", "Invoke only pins to published members. Bind this member first, then return here to invoke it."),
        type: 'info' as const,
      };
    }

    if (!hasInvokeTargetMemberSelection) {
      return {
        message: t("pages.studio.index.copy.10", "Select a member to invoke."),
        description:
          t("pages.studio.index.copy.11", "Select a member from Team members first, or continue from Bind, so Invoke stays pinned to one member."),
        type: 'info' as const,
      };
    }

    return {
      message: t("pages.studio.index.copy.12", "{value1} cannot be invoked directly yet.", { value1: invokeTargetLabel || '当前成员' }),
      description:
        t("pages.studio.index.copy.13", "In the current team context, this member does not expose a published callable contract yet."),
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
      bindingSelectionRef.current.endpointId &&
      !selectedService.endpoints.some(isChatServiceEndpoint)
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
  const selectedInventoryMemberKey = useMemo(() => {
    const normalizedMemberKey = trimOptional(effectiveSelectedMemberKey);
    return normalizedMemberKey.startsWith('workflow:') ||
      normalizedMemberKey.startsWith('member:')
      ? normalizedMemberKey
      : '';
  }, [effectiveSelectedMemberKey]);
  const selectedInventoryEntryMemberId = trimOptional(currentCanonicalMemberId);
  const selectedInventoryEntryLabel =
    trimOptional(currentMemberLabel) ||
    selectedInventoryEntryMemberId ||
    'current member';
  const studioTeamEntryMemberId = trimOptional(
    studioTeamSummaryQuery.data?.entryMemberId,
  );
  const selectedInventoryIsEntryMember =
    Boolean(selectedInventoryEntryMemberId) &&
    selectedInventoryEntryMemberId === studioTeamEntryMemberId;
  const selectedInventoryEntryMemberResolved =
    Boolean(selectedInventoryEntryMemberId) &&
    trimOptional(workbenchStudioMemberSummary?.memberId) ===
      selectedInventoryEntryMemberId;
  const canSetSelectedInventoryEntryMember = Boolean(
    resolvedStudioScopeId &&
      resolvedStudioTeamId &&
      selectedInventoryEntryMemberId &&
      selectedInventoryEntryMemberResolved,
  );
  const handleSetSelectedInventoryEntryMember = useCallback(async () => {
    if (
      !canSetSelectedInventoryEntryMember ||
      selectedInventoryIsEntryMember ||
      inventoryBusyAction === 'entry'
    ) {
      return;
    }

    setInventoryBusyKey(selectedInventoryEntryMemberId);
    setInventoryBusyAction('entry');
    try {
      await studioApi.setTeamEntryMember(
        resolvedStudioScopeId,
        resolvedStudioTeamId,
        selectedInventoryEntryMemberId,
      );
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: studioTeamSummaryQueryKey,
        }),
        queryClient.invalidateQueries({
          queryKey: ['teams', 'team-summary', resolvedStudioScopeId, resolvedStudioTeamId],
        }),
        queryClient.invalidateQueries({
          queryKey: ['teams', 'roster', resolvedStudioScopeId],
        }),
      ]);
      void message.info(t("pages.studio.index.team.entry.2", "Team entry change submitted. Waiting for sync confirmation."));
    } catch (error) {
      void message.error(
        error instanceof Error
          ? t("pages.studio.index.entry.member.update.failed.with.detail", "Entry member update failed: {detail}", { detail: error.message })
          : t("pages.studio.index.entry.member.update.failed", "Entry member update failed."),
      );
    } finally {
      setInventoryBusyKey('');
      setInventoryBusyAction('');
    }
  }, [
    canSetSelectedInventoryEntryMember,
    inventoryBusyAction,
    queryClient,
    resolvedStudioScopeId,
    resolvedStudioTeamId,
    selectedInventoryEntryMemberId,
    selectedInventoryIsEntryMember,
    studioTeamSummaryQueryKey,
  ]);
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
  const selectedInventoryMemberSummary = useMemo(
    () =>
      resolveStudioMemberSummaryFromKey(
        selectedInventoryMemberKey,
        publishedScopeMembers,
        studioScopeMembers,
      ),
    [publishedScopeMembers, selectedInventoryMemberKey, studioScopeMembers],
  );
  const selectedInventoryResolvedMemberId =
    trimOptional(selectedInventoryMemberSummary?.memberId) ||
    readMemberIdFromMemberKey(selectedInventoryMemberKey);
  const selectedInventoryLabel = useMemo(() => {
    if (selectedInventoryMemberKey.startsWith('workflow:')) {
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
    }

    if (selectedInventoryMemberKey.startsWith('member:')) {
      return (
        trimOptional(selectedInventoryMemberSummary?.displayName) ||
        trimOptional(currentMemberLabel) ||
        selectedInventoryResolvedMemberId ||
        'current member'
      );
    }

    return 'current member';
  }, [
    currentMemberLabel,
    selectedInventoryMemberKey,
    selectedInventoryMemberSummary?.displayName,
    selectedInventoryResolvedMemberId,
    selectedInventoryWorkflowId,
    visibleWorkflowSummaries,
  ]);
  const selectedInventoryCanRename = Boolean(
    selectedInventoryMemberKey.startsWith('workflow:') &&
      selectedInventoryWorkflowId,
  );
  const selectedInventoryCanDelete = Boolean(
    (selectedInventoryMemberKey.startsWith('workflow:') &&
      selectedInventoryWorkflowId) ||
      (selectedInventoryMemberKey.startsWith('member:') &&
        resolvedStudioScopeId &&
        selectedInventoryResolvedMemberId),
  );
  const selectedInventoryDeleteTitle = selectedInventoryMemberKey.startsWith('member:')
    ? selectedInventoryCanDelete
      ? `Delete ${selectedInventoryLabel}`
      : 'Select a synced Studio member to delete.'
    : selectedInventoryCanDelete
      ? `Delete ${selectedInventoryLabel}`
      : 'Select a workflow draft member to delete.';
  const handleDeleteStudioMember = useCallback(
    (memberKey: string) => {
      const targetMemberSummary = resolveStudioMemberSummaryFromKey(
        memberKey,
        publishedScopeMembers,
        studioScopeMembers,
      );
      const memberId =
        trimOptional(targetMemberSummary?.memberId) ||
        readMemberIdFromMemberKey(memberKey);
      const scopeId = trimOptional(resolvedStudioScopeId);
      if (!scopeId || !memberId) {
        return;
      }

      const memberLabel =
        trimOptional(targetMemberSummary?.displayName) || memberId || 'this member';

      Modal.confirm({
        autoFocusButton: 'cancel',
        cancelText: t("pages.studio.index.keep.member.3", "Keep member"),
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
              {t("pages.studio.index.delete.member.confirm.body", "Delete")}
              <strong>{memberLabel}</strong>{" "}
              {t(
                "pages.studio.index.delete.member.confirm.body.suffix",
                "from the current member inventory?",
              )}
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
                  letterSpacing: 0,
                }}
              >
                {t("pages.studio.index.member.authority", "Member authority")}
              </Typography.Text>
              <Typography.Text
                style={{
                  color: '#7f1d1d',
                  fontSize: 12,
                  lineHeight: '18px',
                }}
              >
                {t(
                  "pages.studio.index.delete.member.authority.warning",
                  "This removes the Studio member authority and team roster entry. Published service artifacts, revisions, and historical runs stay intact.",
                )}
              </Typography.Text>
            </div>
          </div>
        ),
        icon: <DeleteOutlined style={{ color: '#dc2626' }} />,
        okButtonProps: {
          danger: true,
        },
        okText: t("pages.studio.index.delete.member.3", "Delete member"),
        title: t("pages.studio.index.delete.studio.member", "Delete Studio member"),
        width: 460,
        onOk: async () => {
          setInventoryBusyKey(memberKey);
          setInventoryBusyAction('delete');

          try {
            let alreadyDeleted = false;
            try {
              await studioApi.deleteMember({
                scopeId,
                memberId,
              });
            } catch (error) {
              if (!isStudioMemberNotFound(error)) {
                throw error;
              }
              alreadyDeleted = true;
            }

            if (!alreadyDeleted) {
              void message.info(
                t(
                  "pages.studio.index.delete.member.submitted",
                  "Deletion submitted. Waiting for confirmation.",
                ),
              );
            }
            if (!alreadyDeleted) {
              await waitForStudioMemberDeletion({ scopeId, memberId });
            }

            setConfirmedDeletedStudioMemberIds((current) => {
              const next = new Set(current);
              next.add(memberId);
              return next;
            });
            setOptimisticStudioMembers((current) =>
              current.filter(
                (member) => trimOptional(member.memberId) !== memberId,
              ),
            );

            const invalidations = [
              queryClient.invalidateQueries({
                queryKey: studioMembersQueryKey,
              }),
              queryClient.invalidateQueries({
                queryKey: ['teams', 'roster', scopeId],
              }),
            ];
            if (resolvedStudioTeamId) {
              invalidations.push(
                queryClient.invalidateQueries({
                  queryKey: studioTeamSummaryQueryKey,
                }),
                queryClient.invalidateQueries({
                  queryKey: ['teams', 'team-summary', scopeId, resolvedStudioTeamId],
                }),
                queryClient.invalidateQueries({
                  queryKey: ['teams', 'team-members', scopeId, resolvedStudioTeamId],
                }),
              );
            }
            await Promise.all(invalidations);
            queryClient.setQueryData<StudioMemberRoster>(
              studioMembersQueryKey,
              (current) =>
                removeStudioMemberRosterMember(current, scopeId, memberId),
            );

            const deletedMemberKey = buildBackendMemberKey(memberId);
            if (
              selectedInventoryMemberKey === memberKey ||
              trimOptional(deletedMemberKey) === trimOptional(workbenchMemberKey) ||
              trimOptional(deletedMemberKey) === trimOptional(routeState.memberKey)
            ) {
              pinnedRouteBackendMemberIdRef.current = '';
              suppressBuildMemberRoutePersistenceRef.current = true;
              setPinnedRouteBackendMemberId('');
              setSelectedWorkflowId('');
              setSelectedScriptId('');
              setTemplateWorkflow('');
              if (studioSurface === 'observe') {
                setSelectedExecutionId('');
              }
              history.replace(
                buildStudioRoute({
                  scopeId,
                  teamId: routeState.teamId || undefined,
                  returnTo: routeState.returnTo || undefined,
                  step: 'build',
                  tab: 'studio',
                }),
              );
            }

            void message.success(
              t("pages.studio.index.deleted.member", "Deleted member {member}.", {
                member: memberLabel,
              }),
            );
          } catch (error) {
            void message.error(
              error instanceof StudioMemberDeletionNotConfirmedError
                ? t(
                    "pages.studio.index.delete.member.not.confirmed",
                    "Deletion was not confirmed. The member remains in the list; refresh and retry.",
                  )
                : error instanceof Error
                  ? error.message
                  : t(
                      "pages.studio.index.failed.delete.member",
                      "Failed to delete member.",
                    ),
            );
          } finally {
            setInventoryBusyKey('');
            setInventoryBusyAction('');
          }
        },
      });
    },
    [
      publishedScopeMembers,
      queryClient,
      resolvedStudioScopeId,
      resolvedStudioTeamId,
      routeState.memberKey,
      routeState.returnTo,
      routeState.teamId,
      selectedInventoryMemberKey,
      studioMembersQueryKey,
      studioScopeMembers,
      studioSurface,
      studioTeamSummaryQueryKey,
      workbenchMemberKey,
    ],
  );
  const handleDeleteInventoryMember = useCallback(
    (memberKey: string) => {
      if (memberKey.startsWith('member:')) {
        handleDeleteStudioMember(memberKey);
        return;
      }

      handleDeleteWorkflowMember(memberKey);
    },
    [handleDeleteStudioMember, handleDeleteWorkflowMember],
  );
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
        pinnedRouteBackendMemberIdRef.current = '';
        setPinnedRouteBackendMemberId('');
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
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey: normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            teamId: routeState.teamId || undefined,
            returnTo: routeState.returnTo || undefined,
            memberKey: normalizedMemberKey,
            tab: 'studio',
          }),
        );
        openWorkspaceWorkflow(workflowId);
        return;
      }

      if (normalizedMemberKey.startsWith('script:')) {
        pinnedRouteBackendMemberIdRef.current = '';
        setPinnedRouteBackendMemberId('');
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
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey: normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            teamId: routeState.teamId || undefined,
            returnTo: routeState.returnTo || undefined,
            memberKey: normalizedMemberKey,
            tab: 'scripts',
          }),
        );
        openScopeScript(scriptId);
        return;
      }

      if (normalizedMemberKey.startsWith('member:')) {
        // Refactor (iter1/cluster-studio-member-routing):
        // Old: Team rail clicks treated the member implementation kind as the
        // lifecycle destination, so Workflow members jumped to Build while
        // GAgent members jumped to Bind. New: the current lifecycle step stays
        // authoritative; member clicks only swap the selected backend member and
        // the matching Build focus when Build is already active.
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
          const memberImplementationKind =
            normalizeStudioMemberBindingImplementationKind(
              selectedMemberSummary?.implementationKind,
            );
          const memberImplementationName =
            trimOptional(selectedMemberSummary?.displayName) ||
            trimOptional(selectedMemberSummary?.memberId);

          if (memberImplementationKind === 'workflow') {
            const workflowId = resolveWorkflowIdForMemberSummary(
              selectedMemberSummary,
              visibleWorkflowSummaries,
              activeWorkflowFile,
            );
            if (workflowId) {
              const selectedWorkflowSummary = visibleWorkflowSummaries.find(
                (workflow) => trimOptional(workflow.workflowId) === workflowId,
              );
              const workflowMemberKey =
                buildWorkflowMemberKeyFromSummary(selectedWorkflowSummary) ||
                (`workflow:${workflowId}` as const);
              const memberRouteKey =
                selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey;
              pinnedRouteBackendMemberIdRef.current =
                selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey);
              setPinnedRouteBackendMemberId(
                selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey),
              );
              setSelectedWorkflowId(workflowId);
              setSelectedScriptId('');
              setTemplateWorkflow('');
              setBuildSurface('editor');
              const shouldOpenWorkflowBuild = studioSurface === 'build';
              if (shouldOpenWorkflowBuild) {
                setStudioSurface('build');
              }
              history.push(
                buildStudioRoute({
                  scopeId: resolvedStudioScopeId || undefined,
                  teamId: routeState.teamId || undefined,
                  returnTo: routeState.returnTo || undefined,
                  memberKey: memberRouteKey,
                  focus: workflowMemberKey,
                  step: shouldOpenWorkflowBuild ? 'build' : currentLifecycleStep,
                  tab: shouldOpenWorkflowBuild ? 'studio' : undefined,
                }),
              );
            } else {
              void message.warning(
                t("pages.studio.index.could.not.find.workflow.draft.for.member", "Could not find a workflow draft for {member}.", { member: memberImplementationName || t("pages.studio.index.this.member", "this member") }),
              );
            }
            return;
          }

          if (memberImplementationKind === 'script') {
            const scriptId =
              trimOptional(selectedMemberSummary?.memberId) ||
              availableScopeScripts.find(
                (detail) =>
                  normalizeComparableText(detail.script?.scriptId) ===
                    normalizeComparableText(memberImplementationName) ||
                  normalizeComparableText(detail.script?.scriptId) ===
                    normalizeComparableText(selectedMemberServiceId),
              )?.script?.scriptId;
            if (scriptId) {
              const memberKey =
                selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey;
              pinnedRouteBackendMemberIdRef.current =
                selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey);
              setPinnedRouteBackendMemberId(
                selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey),
              );
              setSelectedWorkflowId('');
              setSelectedScriptId(scriptId);
              setTemplateWorkflow('');
              setBuildSurface('scripts');
              setStudioSurface('build');
              history.push(
                buildStudioRoute({
                  scopeId: resolvedStudioScopeId || undefined,
                  teamId: routeState.teamId || undefined,
                  returnTo: routeState.returnTo || undefined,
                  memberKey,
                  focus: `script:${scriptId}`,
                  step: 'build',
                  tab: 'scripts',
                }),
              );
            } else {
              void message.warning(
                t("pages.studio.index.could.not.find.workspace.script.for.member", "Could not find a workspace script for {member}.", { member: memberImplementationName || t("pages.studio.index.this.member", "this member") }),
              );
            }
            return;
          }

          if (memberImplementationKind === 'gagent') {
            const memberKey =
              selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey;
            pinnedRouteBackendMemberIdRef.current =
              selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey);
            setPinnedRouteBackendMemberId(
              selectedMemberId || readMemberIdFromMemberKey(normalizedMemberKey),
            );
            setSelectedWorkflowId('');
            setSelectedScriptId('');
            setTemplateWorkflow('');
            if (studioSurface !== 'build') {
              if (studioSurface === 'observe') {
                setSelectedExecutionId('');
              }
              history.push(
                buildStudioRoute({
                  scopeId: resolvedStudioScopeId || undefined,
                  teamId: routeState.teamId || undefined,
                  returnTo: routeState.returnTo || undefined,
                  memberKey,
                  step: currentLifecycleStep,
                }),
              );
              return;
            }

            history.push(
              buildStudioRoute({
                scopeId: resolvedStudioScopeId || undefined,
                teamId: routeState.teamId || undefined,
                returnTo: routeState.returnTo || undefined,
                memberKey,
                step: 'build',
                tab: 'gagents',
              }),
            );
            setBuildSurface('gagent');
            setStudioSurface('build');
            return;
          }

          void message.warning(
            t("pages.studio.index.could.not.find.published.service.for.member", "Could not find a published service for {member}.", { member: memberImplementationName || t("pages.studio.index.this.member", "this member") }),
          );
          return;
        }

        const selectedWorkflowIdForMember = trimOptional(
          selectedPublishedMember?.matchedWorkflow?.workflowId,
        );
        const selectedScriptIdForMember = trimOptional(
          selectedPublishedMember?.matchedScript?.script?.scriptId,
        );
        const selectedMemberImplementationKind =
          normalizeStudioMemberBindingImplementationKind(
            selectedMemberSummary?.implementationKind ||
              selectedPublishedMember?.revision?.implementationKind,
          );
        const selectedMemberOwnerKey =
          selectedWorkflowIdForMember
            ? buildWorkflowMemberKeyFromSummary(selectedPublishedMember?.matchedWorkflow)
            : selectedScriptIdForMember
              ? `script:${selectedScriptIdForMember}`
              : normalizedMemberKey;

        const defaultEndpointId = resolveStudioServiceDefaultEndpointId(
          selectedService,
        );
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
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey:
                selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey,
              step: currentLifecycleStep,
            }),
          );
          return;
        }

        if (selectedWorkflowIdForMember) {
          if (selectedMemberId) {
            pinnedRouteBackendMemberIdRef.current = selectedMemberId;
            setPinnedRouteBackendMemberId(selectedMemberId);
          }
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey: selectedMemberOwnerKey,
              tab: 'studio',
            }),
          );
          openWorkspaceWorkflow(selectedWorkflowIdForMember);
          return;
        }

        if (selectedScriptIdForMember) {
          if (selectedMemberId) {
            pinnedRouteBackendMemberIdRef.current = selectedMemberId;
            setPinnedRouteBackendMemberId(selectedMemberId);
          }
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey: selectedMemberOwnerKey,
              tab: 'scripts',
            }),
          );
          openScopeScript(selectedScriptIdForMember);
          return;
        }

        if (selectedMemberImplementationKind === 'gagent') {
          const memberKey =
            selectedMemberId ? `member:${selectedMemberId}` : normalizedMemberKey;
          const agentKind = trimOptional(
            selectedPublishedMember?.revision?.staticAgentKind,
          );
          if (agentKind) {
            setSelectedAgentKind((current) =>
              trimOptional(current) === agentKind ? current : agentKind,
            );
          }
          setSelectedWorkflowId('');
          setSelectedScriptId('');
          setTemplateWorkflow('');
          setBuildSurface('gagent');
          setStudioSurface('build');
          history.push(
            buildStudioRoute({
              scopeId: resolvedStudioScopeId || undefined,
              teamId: routeState.teamId || undefined,
              returnTo: routeState.returnTo || undefined,
              memberKey,
              step: 'build',
              tab: 'gagents',
            }),
          );
          return;
        }

        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            teamId: routeState.teamId || undefined,
            returnTo: routeState.returnTo || undefined,
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
      availableScopeScripts,
      confirmScriptsStudioLeave,
      currentFocusMemberKey,
      currentLifecycleStep,
      history,
      openScopeScript,
      openWorkspaceWorkflow,
      publishedScopeMembers,
      publishedScopeServices,
      resolvedStudioScopeId,
      routeState.teamId,
      routeState.returnTo,
      studioSurface,
      visibleWorkflowSummaries,
      workbenchMemberKey,
    ],
  );
  const memberItems = useMemo(() => {
    const items: OrderedStudioShellMemberItem[] = [];
    const seen = new Set<string>();
    const currentMemberCanRename =
      currentFocusMemberKey.startsWith('workflow:') ||
      selectedRailMemberKey.startsWith('member:') ||
      currentFocusMemberKey.startsWith('member:');
    const currentMemberItem: StudioShellMemberItem = {
      key: selectedRailMemberKey || currentFocusMemberKey,
      label: currentMemberLabel,
      canDelete:
        (currentFocusMemberKey.startsWith('workflow:') && Boolean(selectedWorkflowId)) ||
        Boolean(
          readMemberIdFromMemberKey(selectedRailMemberKey) ||
            readMemberIdFromMemberKey(currentFocusMemberKey),
        ),
      canRename: currentMemberCanRename,
      description: currentMemberDescription,
      kind: currentMemberKind,
      meta: currentMemberMeta,
      tone: currentMemberTone,
    };
    const currentMemberDuplicateKeys = [
      selectedRailMemberKey,
      currentFocusMemberKey,
      selectedWorkflowId ? selectedWorkflowMemberKey : '',
      selectedScriptId ? `script:${selectedScriptId}` : '',
      workbenchStudioMemberId ? `member:${workbenchStudioMemberId}` : '',
      workbenchPublishedServiceId ? `member:${workbenchPublishedServiceId}` : '',
    ];

    const addItem = (
      item: StudioShellMemberItem | null,
      duplicateKeys: readonly string[] = [],
    ) => {
      if (!item) {
        return;
      }

      const normalizedKey = trimOptional(item.key);
      const normalizedDuplicateKeys = Array.from(
        new Set(
          [normalizedKey, ...duplicateKeys]
            .map((key) => trimOptional(key))
            .filter(Boolean),
        ),
      );
      if (
        !normalizedKey ||
        normalizedDuplicateKeys.some((duplicateKey) => seen.has(duplicateKey))
      ) {
        return;
      }

      for (const duplicateKey of normalizedDuplicateKeys) {
        seen.add(duplicateKey);
      }
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
      const memberId = trimOptional(memberSummary?.memberId);
      const memberPublishedServiceId = trimOptional(memberSummary?.publishedServiceId);
      const serviceId = trimOptional(service.serviceId);
      const memberDuplicateKeys = buildStudioMemberDuplicateKeys({
        memberSummary,
        service,
        revision: serviceRevision,
        matchedWorkflow,
        matchedScript,
      });
      addItem({
        key:
          `member:${memberId || memberPublishedServiceId || serviceId}`,
        label:
          (resolvedStudioTeamId ? trimOptional(memberSummary?.displayName) : '') ||
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
                    trimOptional(serviceRevision.staticAgentKind),
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
        canDelete: Boolean(memberId),
        tone: resolveServiceMemberTone(service.deploymentStatus),
      }, memberDuplicateKeys);
    }

    for (const memberSummary of studioScopeMembers) {
      const memberId = trimOptional(memberSummary.memberId);
      const memberPublishedServiceId = trimOptional(memberSummary.publishedServiceId);
      if (!memberId && !memberPublishedServiceId) {
        continue;
      }

      const itemMemberKey = `member:${memberId || memberPublishedServiceId}`;
      const implementationKind =
        normalizeStudioMemberBindingImplementationKind(memberSummary.implementationKind);
      addItem({
        key: itemMemberKey,
        label:
          trimOptional(memberSummary.displayName) ||
          trimOptional(memberSummary.memberId) ||
          'Member',
        description: formatStudioAssetMeta({
          primary: describeMemberImplementationLabel(memberSummary.implementationKind),
          secondary:
            trimOptional(memberSummary.description) ||
            formatStudioMemberLifecycleStage(memberSummary.lifecycleStage) ||
            'Backend member authority',
        }) || 'Backend member authority.',
        canDelete: Boolean(memberId),
        canRename: Boolean(memberId),
        kind: 'member',
        meta: formatStudioAssetMeta({
          primary:
            trimOptional(memberSummary.lastBoundRevisionId) ||
            formatStudioMemberLifecycleStage(memberSummary.lifecycleStage),
          secondary: memberPublishedServiceId || trimOptional(memberSummary.updatedAt),
        }),
        tone:
          currentFocusMemberKey === itemMemberKey
            ? 'live'
            : implementationKind === 'gagent' && !trimOptional(memberSummary.lastBoundRevisionId)
              ? 'draft'
              : 'idle',
      }, buildStudioMemberDuplicateKeys({ memberSummary }));
    }

    if (!resolvedStudioTeamId) {
      for (const workflow of visibleWorkflowSummaries) {
        if (serviceBackedWorkflowIds.has(trimOptional(workflow.workflowId))) {
          continue;
        }

        const workflowMemberKey = buildWorkflowMemberKeyFromSummary(workflow);
        if (!workflowMemberKey) {
          continue;
        }
        const workflowDuplicateKeys = [
          workflowMemberKey,
          `workflow:${trimOptional(workflow.name)}`,
          `workflow:${trimOptional(workflow.fileName).replace(/\.(ya?ml)$/i, '')}`,
        ];

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
        }, workflowDuplicateKeys);
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
              'Workspace script behavior',
          }) || 'Workspace script behavior',
          kind: 'member',
          meta: formatStudioAssetMeta({
            primary: scriptDetail.script?.activeRevision || '',
            secondary: 'Workspace script',
          }),
          tone:
            currentFocusMemberKey === `script:${scriptId}` ? 'live' : 'idle',
        });
      }
    }

    const currentMemberIsExplicitBackendRoute =
      Boolean(resolvedStudioTeamId) &&
      Boolean(routeSelectedBackendMemberKey) &&
      selectedRailMemberKey === routeSelectedBackendMemberKey &&
      selectedRailMemberKey === currentMemberItem.key;
    const currentMemberBelongsToRailScope =
      !resolvedStudioTeamId ||
      currentMemberIsExplicitBackendRoute ||
      currentMemberDuplicateKeys
        .map((key) => trimOptional(key))
        .filter(Boolean)
        .some((key) => seen.has(key));
    if (currentMemberBelongsToRailScope) {
      addItem(currentMemberItem, currentMemberDuplicateKeys);
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
    resolvedStudioTeamId,
    routeSelectedBackendMemberKey,
    selectedRailMemberKey,
    serviceBackedScriptIds,
    serviceBackedWorkflowIds,
    selectedWorkflowId,
    selectedWorkflowMemberKey,
    selectedScriptId,
    studioScopeMembers,
    visibleWorkflowSummaries,
    studioScopeMembers,
    workbenchPublishedServiceId,
    workbenchStudioMemberId,
  ]);
  const selectedMemberCanBind =
    Boolean(workbenchMemberKey) &&
    Boolean(
      selectedWorkflowId ||
        (selectedScriptId
          ? activeScriptPublishedServiceId ||
            ((scriptBuildState?.scriptId === selectedScriptId &&
              !scriptBuildState.dirty &&
              scriptBuildState.saveStatus === 'applied') ||
              (lastAppliedScriptBuildState?.scriptId === selectedScriptId &&
                !lastAppliedScriptBuildState.dirty &&
                lastAppliedScriptBuildState.saveStatus === 'applied'))
          : false) ||
        workbenchPublishedService ||
        (isBuildGAgentSurface && trimOptional(selectedAgentKind))
    );
  const selectedMemberCanInvoke =
    selectedMemberCanBind &&
    Boolean(workbenchStudioMemberId) &&
    Boolean(invokeTargetServiceId) &&
    Boolean(invokeTargetDefaultEndpointId);
  const lifecycleSteps = useMemo<readonly StudioLifecycleStep[]>(
    () => isScriptBuildLaunchpadEmpty
      ? []
      : [
      {
        key: 'build',
        label: 'Build',
        description:
          t("pages.studio.index.edit.the.selected.member.implementation.2", "Edit the selected member implementation with workflow, script, or GAgent tools."),
        status: currentLifecycleStep === 'build' ? 'active' : 'available',
      },
      {
        key: 'bind',
        label: 'Bind',
        description:
          t("pages.studio.index.inspect.published.services.binding.revisions.2", "Inspect published services, binding revisions, and serving state for the selected member."),
        status: currentLifecycleStep === 'bind' ? 'active' : 'available',
        disabled: !resolvedStudioScopeId || !selectedMemberCanBind,
      },
      {
        key: 'invoke',
        label: 'Invoke',
        description:
          t("pages.studio.index.invoke.the.selected.member.in.2", "Invoke the selected member in-place and carry the trace forward into runtime runs."),
        status: currentLifecycleStep === 'invoke' ? 'active' : 'available',
        disabled: !resolvedStudioScopeId || !selectedMemberCanInvoke,
      },
      {
        key: 'observe',
        label: 'Observe',
        description:
          t("pages.studio.index.open.execution.traces.and.run.2", "Open execution traces and run posture for the selected member."),
        status: currentLifecycleStep === 'observe' ? 'active' : 'available',
      },
    ],
    [
      currentLifecycleStep,
      isScriptBuildLaunchpadEmpty,
      resolvedStudioScopeId,
      selectedMemberCanBind,
      selectedMemberCanInvoke,
    ],
  );
  const buildModeDefinitions = useMemo(
    () => getDefaultBuildModeCards(Boolean(appContextQuery.data?.features.scripts)),
    [appContextQuery.data?.features.scripts],
  );
  const buildModeCards = isBuildSurface && !isScriptBuildLaunchpadEmpty ? (
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
            letterSpacing: 0,
            textTransform: 'uppercase',
          }}
        >
          {t("pages.studio.index.construction.mode.2", "Construction Mode")}</div>
        <InlineInfoButton
          ariaLabel="Open construction mode help"
          content={
            <div style={{ display: 'grid', gap: 8 }}>
              <div>{t("pages.studio.index.build.member.workbench.authoring", "The Build phase first determines which implementation method is used for the current member, and then directly completes authoring and dry-run in the same workbench.")}</div>
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
          background: '#efece4',
          border: '1px solid #e5e0d4',
          borderRadius: 999,
          display: 'inline-flex',
          gap: 2,
          padding: 3,
          width: '100%',
        }}
      >
        {buildModeDefinitions.map((item) => {
          const active = activeBuildMode === item.key;
          const disabled = item.disabled || buildModeLocked;

          return (
            <button
              key={item.key}
              type="button"
              aria-pressed={active}
              className={AEVATAR_INTERACTIVE_CHIP_CLASS}
              disabled={disabled}
              onClick={() => void handleSelectBuildMode(item.key)}
              title={
                buildModeLocked
                  ? t("pages.studio.index.member.type.locked.after.creation", "Member type is fixed after this member is created.")
                  : undefined
              }
              style={{
                alignItems: 'center',
                background: active ? '#17130c' : 'transparent',
                border: '1px solid transparent',
                borderRadius: 999,
                color: active ? '#fbfaf6' : '#5f574b',
                cursor: disabled ? 'not-allowed' : 'pointer',
                display: 'inline-flex',
                flex: 1,
                fontSize: 11,
                fontWeight: 700,
                height: 28,
                justifyContent: 'center',
                minWidth: 0,
                opacity: disabled && !active ? 0.58 : 1,
                padding: '0 10px',
                transition:
                  'background-color 0.18s ease, color 0.18s ease',
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
        title: t("pages.studio.index.select.member.to.observe.2", "Select a member to observe."),
        description:
          t("pages.studio.index.choose.member.from.team.members.2", "Choose a member from Team members first so Observe stays pinned to one member context."),
      };
    }

    if (!workbenchPublishedServiceId) {
      return {
        title: t("pages.studio.index.is.not.bound.yet", "{value1} is not bound yet.", { value1: currentMemberLabel || 'Current member' }),
        description:
          t("pages.studio.index.publish.or.bind.this.member.2", "Publish or bind this member first, then Studio can load its runtime runs and audit trail here."),
      };
    }

    if (
      !observeServiceRunsQuery.isLoading &&
      currentMemberExecutions.length === 0
    ) {
      return {
        title: t("pages.studio.index.no.runs.for.yet", "No runs for {value1} yet.", { value1: currentMemberLabel || 'this member' }),
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
      Boolean(trimOptional(routeState.legacyServiceId))) &&
    (!appContextQuery.data?.features.scripts || !scopeScriptsQuery.isLoading);
  const studioContextPrimaryTitle =
    showWorkflowEntryEmptyState
      ? hasSelectedMemberFocus
        ? currentMemberLabel
      : t("pages.studio.index.select.member", "Select a member")
      : isBuildEditorSurface
        ? activeWorkflowName || templateWorkflow || t("pages.studio.index.workflow.2", "Workflow Build")
        : isBuildGAgentSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : t("pages.studio.index.gagent.2", "GAgent Build")
        : isBuildScriptsSurface
          ? isScriptBuildLaunchpadEmpty
            ? t("pages.studio.index.create.script", "Create a script")
            : selectedScriptId || t("pages.studio.index.script", "Script Build")
        : isObserveSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : t("pages.studio.index.select.member", "Select a member")
        : isBindSurface
          ? hasSelectedMemberFocus
            ? currentMemberLabel
            : t("pages.studio.index.copy.14", "Member binding")
          : isInvokeSurface
            ? currentMemberLabel || t("pages.studio.index.copy.15", "Member invoke")
            : pageTitle;
  const studioContextDescriptor =
    showWorkflowEntryEmptyState
      ? hasSelectedMemberFocus
        ? t("pages.studio.index.member.build.surface.bind", "The current member has no editable Build surface yet. You can continue to Bind or Invoke, or explicitly create a new member.")
        : memberItems.length > 0
        ? t("pages.studio.index.member.build.create.member", "Select an existing member from the left rail before continuing Build. To add one, explicitly click Create member.")
        : t("pages.studio.index.team.member.create.member", "This team has no members yet. Explicitly click Create member before entering a new implementation draft.")
      : isBuildEditorSurface
        ? t("pages.studio.index.member.workflow.canvas.step", "Continue building around the current member workflow canvas, step details, and dry-run")
        : isBuildGAgentSurface
          ? t("pages.studio.index.build.gagent.prompt", "Define the GAgent type, role, initial prompt, tools, and state persistence in Build")
        : isBuildScriptsSurface
          ? isScriptBuildLaunchpadEmpty
            ? t("pages.studio.index.start.script.draft.before.studio", "Start a script draft before Studio opens editing, validation, or run controls.")
            : t("pages.studio.index.script.source.diagnostics.dry", "Continue iterating over the current member around script source, diagnostics, and dry-run")
        : isObserveSurface
          ? t("pages.studio.index.member", "Continue observing the current member's recent runs, replay, and baseline")
        : isBindSurface
            ? t("pages.studio.index.member.published.contract.invoke", "Confirm the current member's published contract and continue to Invoke")
            : isInvokeSurface
              ? t("pages.studio.index.copy.16", "Invoke the current member and keep the run observation context")
              : t("pages.studio.index.copy.17", "Member workbench");
  const studioBoundServiceLabel =
    hasSelectedMemberFocus
      ? trimOptional(routeState.legacyServiceId) ||
        trimOptional(workbenchPublishedService?.serviceId) ||
        trimOptional(workbenchStudioMember?.publishedServiceId) ||
        trimOptional(workbenchStudioMemberSummary?.publishedServiceId) ||
        t("pages.studio.index.no.bound.service", "No bound service")
      : '';
  const studioContextMetaParts = [
    studioContextDescriptor,
    studioBoundServiceLabel,
  ]
    .map((value) => trimOptional(value))
    .filter(Boolean);
  const studioReturnHref = resolvedStudioScopeId
    ? routeState.returnTo ||
      (routeState.teamId
        ? buildTeamDetailHref({
            scopeId: resolvedStudioScopeId,
            teamId: routeState.teamId,
            tab: 'overview',
            memberId:
              currentCanonicalMemberId ||
              trimOptional(routeSelectedBackendMemberId) ||
              readMemberIdFromMemberKey(routeState.memberKey) ||
              undefined,
            serviceId: trimOptional(workbenchPublishedService?.serviceId) || undefined,
          })
        : buildTeamDetailHref({
            scopeId: resolvedStudioScopeId,
            tab: 'overview',
            serviceId:
              trimOptional(workbenchPublishedService?.serviceId) ||
              trimOptional(routeState.legacyServiceId) ||
              undefined,
          }))
    : buildTeamsHref();
  const studioReturnLabel = t("pages.studio.index.copy.18", "Back to Team");
  const studioTeamLabel =
    trimOptional(studioTeamSummaryQuery.data?.displayName) ||
    (routeState.teamId ? t("pages.studio.index.teamBreadcrumb", "Team") : "");
  const studioTeamsHref = resolvedStudioScopeId
    ? buildTeamDetailHref({ scopeId: resolvedStudioScopeId })
    : buildTeamsHref();
  const studioBreadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      href: studioTeamsHref,
      onClick: (event) => {
        event.preventDefault();
        history.push(studioTeamsHref);
      },
      title: t("pages.studio.index.teamsBreadcrumb", "Teams"),
    },
    ...(studioTeamLabel
      ? [
          {
            href: studioReturnHref,
            onClick: (event: React.MouseEvent<HTMLAnchorElement>) => {
              event.preventDefault();
              history.push(studioReturnHref);
            },
            title: studioTeamLabel,
          } satisfies AevatarBreadcrumbItem,
        ]
      : []),
    {
      current: true,
      title: t("pages.studio.index.studioBreadcrumb", "Studio"),
    },
  ];
  const currentStudioReturnTo =
    routeState.returnTo ||
    (typeof window === 'undefined'
      ? ''
      : sanitizeReturnTo(
          `${window.location.pathname}${window.location.search}${window.location.hash}`,
        ));
  const createScriptId = buildScriptIdSlug(createMemberName);
  const createScriptIdAlreadyExists = Boolean(
    createScriptId &&
      (availableScopeScriptIds.has(createScriptId) ||
        studioScopeMembers.some(
          (member) =>
            normalizeStudioMemberBindingImplementationKind(
              member.implementationKind,
            ) === 'script' &&
            buildScriptIdSlug(member.memberId) === createScriptId,
        )),
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
          aria-label={t("pages.studio.index.create.member.6", "Create member")}
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
          {t("pages.studio.index.create.member.7", "Create member")}</Button>
        <Button
          aria-label={`Rename ${selectedInventoryLabel}`}
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={!selectedInventoryCanRename || selectedInventoryMemberBusy}
          loading={selectedInventoryBusyAction === 'rename'}
          onClick={() =>
            selectedInventoryCanRename
              ? void handleRenameWorkflowMember(selectedInventoryMemberKey)
              : undefined
          }
          style={{
            ...inventoryActionButtonStyle,
            cursor:
              !selectedInventoryCanRename || selectedInventoryMemberBusy
                ? 'not-allowed'
                : 'pointer',
            opacity:
              !selectedInventoryCanRename || selectedInventoryMemberBusy
                ? 0.56
                : 1,
          }}
        >
          {t("pages.studio.index.rename.2", "Rename")}</Button>
        <Button
          aria-label={`Delete ${selectedInventoryLabel}`}
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          disabled={!selectedInventoryCanDelete || selectedInventoryMemberBusy}
          loading={selectedInventoryBusyAction === 'delete'}
          onClick={() =>
            selectedInventoryCanDelete
              ? void handleDeleteInventoryMember(selectedInventoryMemberKey)
              : undefined
          }
          title={selectedInventoryDeleteTitle}
          style={{
            ...inventoryActionDangerButtonStyle,
            cursor:
              !selectedInventoryCanDelete || selectedInventoryMemberBusy
                ? 'not-allowed'
                : 'pointer',
            opacity:
              !selectedInventoryCanDelete || selectedInventoryMemberBusy
                ? 0.56
                : 1,
          }}
        >
          {t("pages.studio.index.delete.2", "Delete")}</Button>
      </div>
      {selectedInventoryMemberKey ? (
        <div style={inventorySelectionPillStyle}>
          <span style={inventorySelectionLabelStyle}>{t("pages.studio.index.selected.2", "Selected")}</span>
          <span style={inventorySelectionValueStyle}>{selectedInventoryLabel}</span>
        </div>
      ) : (
        <div style={inventoryActionsHintStyle}>
          {t("pages.studio.index.create.workflow.script.or.gagent.2", "Create a Workflow, Script, or GAgent member from this inventory.")}</div>
      )}
      {canSetSelectedInventoryEntryMember ? (
        selectedInventoryIsEntryMember ? (
          <div style={inventoryEntryPillStyle}>
            <span style={inventorySelectionLabelStyle}>{t("pages.studio.index.team.invoke.2", "Team invoke")}</span>
            <span style={inventorySelectionValueStyle}>
              {t("pages.studio.index.entry.member.2", "Entry member ·")}{selectedInventoryEntryLabel}
            </span>
          </div>
        ) : (
          <Button
            aria-label={`Set ${selectedInventoryEntryLabel} as Team entry member`}
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            loading={
              inventoryBusyAction === 'entry' &&
              inventoryBusyKey === selectedInventoryEntryMemberId
            }
            onClick={() => void handleSetSelectedInventoryEntryMember()}
            style={inventoryEntryButtonStyle}
          >
            {t("pages.studio.index.set.as.entry.2", "Set as entry")}</Button>
        )
      ) : null}
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
            ? t("pages.studio.index.is.not.build.ready.here", "{value1} is not build-ready here", { value1: currentMemberLabel })
            : memberItems.length > 0
              ? t("pages.studio.index.select.team.member.2", "Select a team member")
              : t("pages.studio.index.create.your.first.team.member.2", "Create your first team member")}
        </h2>
        <p style={memberEmptyStateBodyStyle}>
          {hasSelectedMemberFocus
            ? t("pages.studio.index.this.selected.member.does.not.currently", "This selected member does not currently expose an editable Build surface in Studio. Continue in Bind or Invoke, or create a new member to start from Build.")
            : memberItems.length > 0
            ? t("pages.studio.index.pick.an.existing.member.from.team", "Pick an existing member from Team members to continue in Studio, or explicitly create a new member here.")
            : t("pages.studio.index.studio.no.longer.creates.an.implicit", "Studio no longer creates an implicit draft on entry. Create a member when you are ready to start building.")}
        </p>
      </div>
      <div style={memberEmptyStateActionsStyle}>
        <Button
          aria-label={t("pages.studio.index.create.member.from.empty.state.2", "Create member from empty state")}
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
          {t("pages.studio.index.create.member.8", "Create member")}</Button>
        <span style={inventoryActionsHintStyle}>
          {hasSelectedMemberFocus
            ? t("pages.studio.index.bind.and.invoke.stay.available.for", "Bind and Invoke stay available for this member even when Build is not.")
            : memberItems.length > 0
            ? t("pages.studio.index.you.can.also.pick.an.existing", "You can also pick an existing member from the left rail.")
            : t("pages.studio.index.only.explicit.create.member.should.open", "Only explicit Create member should open a new implementation draft now.")}
        </span>
      </div>
    </div>
  ) : null;
  const studioContextBar = (
    <div
      data-testid="studio-context-bar"
      style={{
        alignItems: 'center',
        borderBottom: '1px solid #e8e3d8',
        display: 'flex',
        flexWrap: 'wrap',
        gap: 12,
        padding: '12px 20px',
      }}
    >
      <AevatarBackButton
        ariaLabel={studioReturnLabel}
        onBack={() => history.push(studioReturnHref)}
        title={studioReturnLabel}
      />
      <AevatarBreadcrumb items={studioBreadcrumbItems} />
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
            color: '#17130c',
            fontSize: 15,
            fontWeight: 700,
            letterSpacing: '-0.01em',
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
      onSaveDraft={(draft) => void handleSaveDraft(draft)}
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
      dryRunModelLabel={
        workflowDryRunLlmConfig.status === 'ready'
          ? workflowDryRunLlmConfig.model || 'Provider default'
          : undefined
      }
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
      onContinueToBind={() =>
        applyStudioTarget('bind', 'editor', lifecycleSurfaceMemberKey)
      }
    />
  );

  const scriptBuildContent = appContextQuery.data?.features.scripts ? (
    <StudioScriptBuildPanel
      scopeId={resolvedStudioScopeId || undefined}
      scriptsQuery={scopeScriptsQuery}
      selectedScriptId={selectedScriptId}
      pendingScriptDraft={pendingScriptDraft}
      onCreateScriptDraft={openCreateScriptDraftFlow}
      onSelectScriptId={(scriptId) => {
        setSelectedScriptId(scriptId);
        const storedDraft = loadStoredScriptDraft(
          resolvedStudioScopeId || undefined,
          scriptId,
        );
        setPendingScriptDraft(storedDraft);
      }}
      onRefreshScripts={() => scopeScriptsQuery.refetch()}
      onPendingScriptDraftChange={(draft) => {
        setPendingScriptDraft(draft);
        if (draft) {
          saveStoredScriptDraft(resolvedStudioScopeId || undefined, draft);
        }
      }}
      onScriptDraftSaved={(scriptId) => {
        if (pendingScriptDraft?.scriptId === scriptId) {
          removeStoredScriptDraft(resolvedStudioScopeId || undefined, scriptId);
          setPendingScriptDraft(null);
        }
      }}
      onContinueToBind={() => {
        const scriptFocusId =
          trimOptional(selectedScriptId) ||
          (routeBuildFocus.kind === 'script' ? routeBuildFocus.value : '') ||
          (routeSelectedMember.kind === 'script' ? routeSelectedMember.value : '') ||
          trimOptional(scriptBuildState?.scriptId);
        const memberKeyForBind =
          routeSelectedBackendMemberKey ||
          (lifecycleSurfaceMemberKey.startsWith('member:')
            ? lifecycleSurfaceMemberKey
            : '') ||
          (routeSelectedMemberKey.startsWith('member:')
            ? routeSelectedMemberKey
            : '');
        if (!memberKeyForBind) {
          void message.warning(
            t("pages.studio.index.select.or.create.member.before.bind.script", "Select or create a member before opening Bind for this Script."),
          );
          return;
        }

        history.push(
          buildStudioRoute({
            scopeId: resolvedStudioScopeId || undefined,
            teamId: routeState.teamId || undefined,
            returnTo: routeState.returnTo || undefined,
            memberKey: memberKeyForBind || undefined,
            focus: scriptFocusId ? `script:${scriptFocusId}` : undefined,
            step: 'bind',
            tab: 'bindings',
          }),
        );
        applyStudioTarget('bind', undefined, memberKeyForBind || undefined);
      }}
      onRegisterLeaveGuard={handleRegisterScriptLeaveGuard}
      onScriptBuildStateChange={handleScriptBuildStateChange}
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
        <strong>{t("pages.studio.index.copy.19", "Script behavior is not supported in the current environment")}</strong>
      </div>
    </div>
  );

  const gAgentBuildContent = (
    <StudioGAgentBuildPanel
      scopeId={resolvedStudioScopeId || undefined}
      currentMemberLabel={currentMemberLabel}
      gAgentKinds={gAgentKindsQuery.data ?? []}
      gAgentKindsLoading={gAgentKindsQuery.isLoading}
      gAgentKindsError={gAgentKindsQuery.isError ? gAgentKindsQuery.error : null}
      selectedAgentKind={selectedAgentKind}
      onSelectAgentKind={setSelectedAgentKind}
      onBuildStateChange={setGAgentBuildState}
      onContinueToBind={(nextBuildState) => {
        setGAgentBuildState(nextBuildState);
        applyStudioTarget('bind', undefined, lifecycleSurfaceMemberKey);
      }}
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
        executionCanStop={executionCanStop}
        executionStopPending={executionStopPending}
        runPrompt={runPrompt}
        executionNotice={executionNotice}
        logsPopoutMode={logsPopoutMode === 'popout'}
        logsDetached={logsDetached}
        onOpenExecution={openExecution}
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
        teamId={routeState.teamId || undefined}
        initialServiceId={bindPendingCandidate ? '' : bindSelectedMemberServiceId}
        onBindPendingCandidate={handleBindPendingCandidate}
        onContinueToInvoke={handleUseBindingEndpoint}
        onSelectionChange={handleBindingSelectionChange}
        postBindEntryActions={
          resolvedStudioTeamId &&
          workbenchStudioMemberId &&
          !bindPendingCandidate &&
          Boolean(bindSelectedMemberServiceId)
            ? {
                busy: teamEntryActionBusy,
                isEntryMember: workbenchMemberIsTeamEntry,
                memberId: workbenchStudioMemberId,
                onSetEntryAndTest: () =>
                  void handleSetTeamEntryFromStudio({ test: true }),
              }
            : null
        }
        pendingBindingCandidate={bindPendingCandidate}
        preferredServiceId={bindPendingCandidate ? '' : bindSelectedMemberServiceId}
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
        teamId={routeState.teamId || undefined}
        initialEndpointId={invokeInitialEndpointId}
        initialServiceId={invokeTargetServiceId}
        services={invokeTargetServices}
      />
    ) : null;

  const pageContainerTitle =
    logsPopoutMode === 'popout' ? 'Execution logs' : undefined;

  return (
    <PageContainer
      breadcrumbRender={false}
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
              contentScrollMode={isInvokeSurface ? 'page' : 'contained'}
              contextBar={studioContextBar}
              currentLifecycleStep={currentLifecycleStep}
              inventoryActions={inventoryActions}
              lifecycleSteps={lifecycleSteps}
              members={memberItems}
              onSelectLifecycleStep={handleSelectLifecycleStep}
              onSelectMember={handleSelectStudioMember}
              pageTitle={pageTitle}
              selectedMemberKey={selectedRailMemberKey}
              showLifecycle={!isScriptBuildLaunchpadEmpty}
              showMemberRail={!isScriptBuildLaunchpadEmpty}
              showPageHeader={false}
            >
              {currentPageContent}
            </StudioShell>
            <Modal
              open={createMemberModalOpen}
              title={t("pages.studio.index.create.member.9", "Create member")}
              onCancel={closeCreateMemberFlow}
              onOk={() => void handleCreateMember(createMemberKind)}
              okText={t("pages.studio.index.create.member.10", "Create member")}
              okButtonProps={{
                disabled:
                  inventoryBusyAction === 'create' ||
                  (createMemberKind === 'workflow' &&
                    (!trimOptional(createMemberName) ||
                      !trimOptional(
                        trimOptional(createMemberDirectoryId) || inventoryDirectoryId,
                      ))) ||
                  (createMemberKind === 'script' &&
                    (!appContextQuery.data?.features.scripts ||
                      !createScriptId ||
                      createScriptIdAlreadyExists)) ||
                  (createMemberKind === 'gagent' &&
                    (!resolvedStudioScopeId || !trimOptional(createMemberName))),
                loading: inventoryBusyAction === 'create',
              }}
              cancelButtonProps={{
                disabled: inventoryBusyAction === 'create',
              }}
            >
              <div style={inventoryCreateModalStackStyle}>
                <div style={inventoryCreateFieldStackStyle}>
                  <div style={inventoryCreateFieldLabelStyle}>{t("pages.studio.index.member.type.2", "Member type")}</div>
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
                        onClick={() => {
                          setCreateMemberKind(kind);
                          if (kind === 'workflow') {
                            setCreateMemberName(suggestedCreateWorkflowName);
                          } else if (kind === 'script') {
                            setCreateMemberName(suggestedCreateScriptName);
                          } else {
                            setCreateMemberName(suggestedCreateGAgentName);
                          }
                        }}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  <div style={inventoryCreateHintStyle}>
                    {t("pages.studio.index.choose.the.implementation.kind.first.2", "Choose the implementation kind first. Studio creates the backend member authority, then opens the matching Build surface for Workflow, Script, or GAgent authoring.")}</div>
                </div>
                {createMemberKind === 'workflow' ||
                createMemberKind === 'script' ||
                createMemberKind === 'gagent' ? (
                  <label style={inventoryCreateFieldStackStyle}>
                    <span style={inventoryCreateFieldLabelStyle}>
                      {createMemberKind === 'script'
                        ? t("pages.studio.index.script.name.2", "Script name")
                        : createMemberKind === 'gagent'
                          ? t("pages.studio.index.gagent.name.2", "GAgent name")
                          : t("pages.studio.index.member.name.2", "Member name")}
                    </span>
                    <input
                      aria-label={
                        createMemberKind === 'script'
                          ? 'Script name'
                          : createMemberKind === 'gagent'
                            ? 'GAgent name'
                            : 'Member name'
                      }
                      onChange={(event) => setCreateMemberName(event.target.value)}
                      placeholder={
                        createMemberKind === 'workflow'
                          ? suggestedCreateWorkflowName
                          : createMemberKind === 'script'
                            ? suggestedCreateScriptName
                            : suggestedCreateGAgentName
                      }
                      ref={createMemberNameInputRef}
                      style={inventoryCreateInputStyle}
                      type="text"
                      value={createMemberName}
                    />
                    {createMemberKind === 'script' ? (
                      <div style={inventoryCreateHintStyle}>
                        {t("pages.studio.index.script.id.2", "Script id:")}{createScriptId || 'enter-a-script-name'}
                        {createScriptIdAlreadyExists
                          ? t("pages.studio.index.already.exists.in.this.workspace", "· already exists in this workspace")
                          : t("pages.studio.index.saved.after.validate.and.save.script", "· saved after Validate and Save script")}
                      </div>
                    ) : null}
                  </label>
                ) : null}
                <div style={inventoryCreateHintStyle}>
                  {createMemberKind === 'workflow'
                    ? t("pages.studio.index.workflow.members.currently.start.from.blank", "Workflow members currently start from a blank workflow draft with an empty canvas, and Studio also registers the member authority in backend once the draft is created.")
                    : createMemberKind === 'script'
                      ? t("pages.studio.index.script.creates.backend.member.and.opens", "Script creates a backend member and opens a stable script draft identity in Build. It becomes callable after Save script is catalog-applied and Bind succeeds.")
                      : resolvedStudioScopeId
                        ? t("pages.studio.index.gagent.creates.backend.member.and.opens", "GAgent creates a backend member and opens Build > GAgent for actor type, role, prompt, tools, and persistence authoring.")
                        : t("pages.studio.index.connect.workspace.before.creating.gagent.member", "Connect a workspace before creating a GAgent member.")}
                </div>
                {createMemberKind === 'workflow' ? (
                  <label style={inventoryCreateFieldStackStyle}>
                    <span style={inventoryCreateFieldLabelStyle}>{t("pages.studio.index.workflow.directory.3", "Workflow directory")}</span>
                    <select
                      aria-label={t("pages.studio.index.workflow.directory.4", "Workflow directory")}
                      onChange={(event) => setCreateMemberDirectoryId(event.target.value)}
                      style={inventoryCreateInputStyle}
                      value={createMemberDirectoryId}
                    >
                      <option value="" disabled>
                        {t("pages.studio.index.select.workflow.directory.2", "Select a workflow directory")}</option>
                      {inventoryDirectoryOptions.map((directory) => (
                        <option key={directory.directoryId} value={directory.directoryId}>
                          {directory.label}
                        </option>
                      ))}
                    </select>
                    <div style={inventoryCreateHintStyle}>
                      {selectedCreateDirectory?.path
                        ? t("pages.studio.index.copy.20", "{value1} · {value2}", { value1: selectedCreateDirectory.label, value2: selectedCreateDirectory.path })
                        : t("pages.studio.index.add.workflow.directory.in.config.before", "Add a workflow directory in Config before creating a workflow draft from this entry.")}
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
