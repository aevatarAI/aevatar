import { InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Button, Tooltip, message } from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import type {
  ScopeMemberEndpointContract,
  ScopeServiceEndpointContract,
} from '@/shared/models/runtime/scopeServices';
import { isAutoEncodableTextPayloadTypeUrl } from '@/shared/runs/protobufPayload';
import {
  createNyxIdChatBindingInput,
  extractRuntimeInvokeReceipt,
  getPreferredScopeConsoleServiceId,
  isChatServiceEndpoint,
  type ScopeConsoleServiceOption,
} from '@/shared/runs/scopeConsole';
import { studioApi } from '@/shared/studio/api';
import {
  normalizeStudioMemberBindingImplementationKind,
  type StudioMemberBindingRevision,
} from '@/shared/studio/models';
import type { StudioObserveSessionSeed } from '@/shared/studio/observeSession';
import {
  buildStudioInvokeCurrentRunViewModel,
  cloneInvokeResult,
  createIdleInvokeResult as createIdleResult,
  type CurrentRunRequest,
  type InvokeHistoryEntry,
  type InvokeResultState,
  type StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import StudioMemberCurrentRunPanel from './StudioMemberCurrentRunPanel';
import StudioMemberInvokeHistoryPanel from './StudioMemberInvokeHistoryPanel';
import { StudioMemberInvokeComposerPanel } from './StudioMemberInvokeSetupPanels';
import StudioInvokeDiagnosticsDrawer from './StudioInvokeDiagnosticsDrawer';
import {
  getInvokeStatusTone,
  studioInvokeColors,
  trimOptional,
  trimPreview,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokePanelProps = {
  readonly authoritativeEndpointContract?:
    | ScopeMemberEndpointContract
    | ScopeServiceEndpointContract
    | null;
  readonly enableFileAttachments?: boolean;
  readonly scopeId: string;
  readonly memberId?: string;
  readonly memberRevision?: StudioMemberBindingRevision | null;
  readonly teamId?: string;
  readonly runtimeTarget?: 'default' | 'member' | 'service' | 'team';
  readonly services: readonly ScopeConsoleServiceOption[];
  readonly selectedMemberLabel?: string;
  readonly presentation?: 'default' | 'member-run';
  readonly targetSummaryVariant?: 'default' | 'member-run';
  readonly emptyState?: {
    readonly description?: string;
    readonly message: string;
    readonly type?: 'error' | 'info' | 'success' | 'warning';
  } | null;
  readonly returnTo?: string;
  readonly initialServiceId?: string;
  readonly initialEndpointId?: string;
  readonly onSelectionChange?: (selection: {
    serviceId: string;
    endpointId: string;
  }) => void;
  readonly onObserveSessionChange?: (
    session: StudioObserveSessionSeed | null,
  ) => void;
};

type TargetMetaItem = {
  readonly key: string;
  readonly label?: string;
  readonly value: string;
};

function createClientId(prefix: string): string {
  const generated = globalThis.crypto?.randomUUID?.();
  if (generated) {
    return `${prefix}_${generated}`;
  }

  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
}

function formatAttachmentSummary(files: readonly File[]): string {
  if (files.length === 0) {
    return '';
  }

  const names = files.map((file) => file.name.trim()).filter(Boolean);
  if (names.length <= 2) {
    return names.join(', ');
  }

  return `${names.slice(0, 2).join(', ')} +${names.length - 2}`;
}

function formatRunInputDisplay(prompt: string, files: readonly File[]): string {
  const attachmentSummary = formatAttachmentSummary(files);
  if (!attachmentSummary) {
    return prompt;
  }

  if (!prompt) {
    return attachmentSummary;
  }

  return `${prompt}\n\nFiles: ${attachmentSummary}`;
}

function resolveInvokeRouteTarget(input: {
  memberId: string;
  runtimeTarget: NonNullable<StudioMemberInvokePanelProps['runtimeTarget']>;
  selectedServiceId?: string;
  teamId: string;
}) {
  switch (input.runtimeTarget) {
    case 'member':
      return input.memberId ? { memberId: input.memberId } : {};
    case 'service':
      return input.selectedServiceId ? { serviceId: input.selectedServiceId } : {};
    case 'team':
      return input.teamId ? { teamId: input.teamId } : {};
    default:
      if (input.teamId) {
        return { teamId: input.teamId };
      }

      return input.selectedServiceId
        ? { serviceId: input.selectedServiceId }
        : {};
  }
}

function cloneChatMessages(
  messages: readonly StudioInvokeChatMessage[],
): StudioInvokeChatMessage[] {
  return messages.map((message) => ({ ...message }));
}

function getPreferredRunOutput(options: {
  assistantText: string;
  finalOutput: string;
}): string {
  return (
    trimOptional(options.finalOutput) || trimOptional(options.assistantText)
  );
}

function formatElapsedTime(
  startedAt: number | null,
  completedAt: number | null,
): string {
  if (!startedAt) {
    return '00:00';
  }

  const endedAt = completedAt || Date.now();
  const elapsedSeconds = Math.max(0, Math.floor((endedAt - startedAt) / 1000));
  const minutes = Math.floor(elapsedSeconds / 60)
    .toString()
    .padStart(2, '0');
  const seconds = (elapsedSeconds % 60).toString().padStart(2, '0');
  return `${minutes}:${seconds}`;
}

function getRunStatusLabel(status: InvokeResultState['status']): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'success':
      return 'Succeeded';
    case 'error':
      return 'Failed';
    case 'cancelled':
      return 'Cancelled';
    default:
      return 'Ready';
  }
}

function getLifecycleLabel(
  revision: StudioMemberBindingRevision | null | undefined,
): string {
  return (
    trimOptional(revision?.servingState) ||
    trimOptional(revision?.status) ||
    'Unknown'
  );
}

function getHistoryOutputText(entry: InvokeHistoryEntry): string {
  const assistantMessage = [...entry.snapshot.chatMessages]
    .reverse()
    .find((message) => message.role === 'assistant');

  return (
    trimOptional(entry.snapshot.result.finalOutput) ||
    trimOptional(assistantMessage?.content) ||
    trimOptional(entry.snapshot.result.assistantText) ||
    trimOptional(entry.errorDetail) ||
    trimOptional(entry.snapshot.result.error)
  );
}

function renderTargetMetaItem(item: TargetMetaItem): React.ReactNode {
  const content = item.label ? `${item.label}: ${item.value}` : item.value;

  return (
    <Tooltip key={item.key} placement="topLeft" title={content}>
      <span style={targetMetaItemWrapStyle}>
        <span style={targetMetaItemStyle}>{content}</span>
      </span>
    </Tooltip>
  );
}

function renderTargetMetaItems(items: readonly TargetMetaItem[]): React.ReactNode {
  return items.flatMap((item, index) => {
    const rendered = renderTargetMetaItem(item);
    return index === 0
      ? [rendered]
      : [
          <span aria-hidden key={`${item.key}-separator`}>
            ·
          </span>,
          rendered,
        ];
  });
}

function createPendingRunResult(input: {
  readonly endpointId: string;
  readonly mode: InvokeResultState['mode'];
  readonly serviceId: string;
}): InvokeResultState {
  return {
    ...createIdleResult(),
    endpointId: input.endpointId,
    mode: input.mode,
    serviceId: input.serviceId,
    status: 'running',
  };
}

function createPendingHistoryEntry(input: {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly endpointId: string;
  readonly endpointLabel: string;
  readonly id: string;
  readonly mode: InvokeHistoryEntry['mode'];
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly result: InvokeResultState;
  readonly serviceId: string;
  readonly startedAt: number;
}): InvokeHistoryEntry {
  return {
    completedAt: input.startedAt,
    createdAt: input.startedAt,
    endpointId: input.endpointId,
    endpointLabel: input.endpointLabel,
    errorDetail: '',
    eventCount: input.result.eventCount || input.result.events.length,
    id: input.id,
    mode: input.mode,
    payloadBase64: input.payloadBase64,
    payloadTypeUrl: input.payloadTypeUrl,
    prompt: input.prompt,
    runId: input.result.runId,
    serviceId: input.serviceId,
    startedAt: input.startedAt,
    status: 'running',
    summary: trimPreview(input.prompt, 72) || 'Running run',
    snapshot: {
      chatMessages: cloneChatMessages(input.chatMessages),
      result: cloneInvokeResult(input.result),
    },
  };
}

function writeClipboardText(value: string, label: string): boolean {
  const normalized = trimOptional(value);
  if (!normalized) {
    void message.warning(
      t("pages.studio.studiomemberinvokepanel.no.value.available.to.copy", "No {label} available to copy.", { label }),
    );
    return false;
  }

  void globalThis.navigator?.clipboard?.writeText(normalized);
  void message.success(
    t("pages.studio.studiomemberinvokepanel.value.copied", "{label} copied.", { label }),
  );
  return true;
}

const surfaceStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const runConsolePanelStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  gap: 0,
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const targetSummaryStyle: React.CSSProperties = {
  alignItems: 'center',
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 10,
  display: 'flex',
  flex: '0 0 auto',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
  marginBottom: 10,
  minWidth: 0,
  padding: '10px 12px',
};

const memberRunWorkspaceStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  margin: '0 auto',
  maxWidth: 1240,
  minWidth: 0,
  width: '100%',
};

const memberRunTargetSummaryStyle: React.CSSProperties = {
  ...targetSummaryStyle,
  background: '#f7f8fb',
  borderColor: '#d8dee9',
  borderRadius: 12,
  boxShadow: '0 1px 0 rgba(15, 23, 42, 0.04)',
  gridColumn: '1 / -1',
  marginBottom: 0,
  padding: '14px 16px',
};

const targetTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 15,
  fontWeight: 800,
  lineHeight: '22px',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const targetTitleWrapStyle: React.CSSProperties = {
  flex: '1 1 320px',
  minWidth: 0,
};

const memberRunTargetTitleWrapStyle: React.CSSProperties = {
  flex: '1 1 520px',
  minWidth: 0,
};

const memberRunTargetTitleStyle: React.CSSProperties = {
  ...targetTitleStyle,
  color: '#0f172a',
  fontSize: 20,
  letterSpacing: 0,
  lineHeight: '28px',
};

const targetMetaItemStyle: React.CSSProperties = {
  display: 'inline-block',
  maxWidth: '100%',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  verticalAlign: 'bottom',
  whiteSpace: 'nowrap',
};

const targetMetaItemWrapStyle: React.CSSProperties = {
  display: 'inline-flex',
  maxWidth: 'min(420px, 100%)',
  minWidth: 0,
};

const targetMetaStyle: React.CSSProperties = {
  alignItems: 'center',
  color: studioInvokeColors.meta,
  display: 'flex',
  flexWrap: 'wrap',
  fontSize: 12,
  gap: 6,
  lineHeight: '18px',
  minWidth: 0,
};

const memberRunTargetMetaStyle: React.CSSProperties = {
  ...targetMetaStyle,
  color: '#475569',
  fontSize: 12,
  gap: 8,
  marginTop: 3,
};

const targetPillStyle: React.CSSProperties = {
  alignItems: 'center',
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 999,
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  gap: 6,
  lineHeight: '18px',
  padding: '3px 9px',
};

const memberRunTargetPillStyle: React.CSSProperties = {
  ...targetPillStyle,
  background: '#ffffff',
  borderColor: '#d8dee9',
  boxShadow: '0 1px 0 rgba(15, 23, 42, 0.04)',
  minHeight: 32,
  padding: '5px 11px',
};

const memberRunMetricPillStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #d8dee9',
  borderRadius: 8,
  color: '#334155',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  gap: 6,
  lineHeight: '18px',
  minHeight: 32,
  padding: '5px 10px',
};

const memberRunMetricLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontWeight: 700,
};

const targetActionStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '0 1 auto',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
  maxWidth: '100%',
  minWidth: 0,
};

const memberRunTargetActionStyle: React.CSSProperties = {
  ...targetActionStyle,
  alignSelf: 'stretch',
};

const invokeSectionPanelBaseStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 10,
  boxShadow: '0 8px 20px rgba(15, 23, 42, 0.06)',
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const invokeSectionTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 15,
  fontWeight: 800,
  lineHeight: '20px',
};

const invokeSectionBodyStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
  padding: '0 14px 14px',
};

const invokeWorkspaceStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const mainDebugAreaStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  gap: 10,
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const invokeRunOutputSectionStyle: React.CSSProperties = {
  ...invokeSectionPanelBaseStyle,
  flex: '0 0 auto',
  minHeight: 0,
  minWidth: 0,
};

const memberRunWorkbenchStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #d8dee9',
  borderRadius: 14,
  boxShadow: '0 12px 30px rgba(15, 23, 42, 0.07)',
  display: 'grid',
  gap: 0,
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
};

const memberRunLauncherStripStyle: React.CSSProperties = {
  background: '#f8fafc',
  borderBottom: '1px solid #e2e8f0',
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '14px 16px',
};

const memberRunLauncherHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
  minWidth: 0,
};

const memberRunLauncherTitleStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 13,
  fontWeight: 800,
  lineHeight: '20px',
};

const memberRunLauncherHintStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 12,
  lineHeight: '18px',
};

const invokeRunOutputBodyStyle: React.CSSProperties = {
  ...invokeSectionBodyStyle,
  gap: 10,
};

const memberRunSectionBodyStyle: React.CSSProperties = {
  ...invokeSectionBodyStyle,
  gap: 12,
  padding: '12px 18px 18px',
};

const memberRunMainAreaStyle: React.CSSProperties = {
  ...mainDebugAreaStyle,
  gap: 0,
};

const memberRunOutputSectionStyle: React.CSSProperties = {
  background: '#ffffff',
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const currentRunViewportStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '0 0 auto',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const memberRunCurrentRunViewportStyle: React.CSSProperties = {
  ...currentRunViewportStyle,
  minHeight: 0,
};

const invokeHistoryPanelStyle: React.CSSProperties = {
  flex: '0 0 auto',
  minHeight: 0,
};

const memberRunHistoryPanelStyle: React.CSSProperties = {
  ...invokeHistoryPanelStyle,
  gridColumn: '1 / -1',
};

const invokeComposerDockStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 10,
  flex: '0 0 auto',
  marginBottom: 10,
  minWidth: 0,
  overflow: 'hidden',
  padding: '8px 10px',
};

const memberRunComposerShellStyle: React.CSSProperties = {
  minWidth: 0,
};

const runStatusDotBaseStyle: React.CSSProperties = {
  borderRadius: 999,
  display: 'inline-block',
  flex: '0 0 auto',
  height: 7,
  width: 7,
};

const StudioMemberInvokePanel: React.FC<StudioMemberInvokePanelProps> = ({
  authoritativeEndpointContract,
  scopeId,
  memberId,
  memberRevision,
  enableFileAttachments = false,
  teamId,
  runtimeTarget = 'default',
  services,
  selectedMemberLabel,
  emptyState,
  initialServiceId,
  initialEndpointId,
  onSelectionChange,
  onObserveSessionChange,
  presentation,
  targetSummaryVariant = 'default',
}) => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const activeHistoryEntryIdRef = useRef('');
  const nyxIdChatBoundRef = useRef(false);
  const previousBindingKeyRef = useRef('');
  const composerDockRef = useRef<HTMLDivElement | null>(null);
  const transcriptViewportRef = useRef<HTMLDivElement | null>(null);
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    trimOptional(initialServiceId),
  );
  const [selectedEndpointId, setSelectedEndpointId] = useState(() =>
    trimOptional(initialEndpointId),
  );
  const [prompt, setPrompt] = useState('');
  const [attachedFiles, setAttachedFiles] = useState<File[]>([]);
  const [submittedFiles, setSubmittedFiles] = useState<File[]>([]);
  const [formError, setFormError] = useState('');
  const [payloadTypeUrl, setPayloadTypeUrl] = useState('');
  const [payloadBase64, setPayloadBase64] = useState('');
  const [loadedEndpointContract, setLoadedEndpointContract] =
    useState<
      ScopeMemberEndpointContract | ScopeServiceEndpointContract | null
    >(null);
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [currentRunRequest, setCurrentRunRequest] =
    useState<CurrentRunRequest | null>(null);
  const [chatMessages, setChatMessages] = useState<StudioInvokeChatMessage[]>(
    [],
  );
  const [requestHistory, setRequestHistory] = useState<InvokeHistoryEntry[]>(
    [],
  );
  const [selectedHistoryId, setSelectedHistoryId] = useState('');
  const [diagnosticsDrawerOpen, setDiagnosticsDrawerOpen] = useState(false);
  const [diagnosticsHistoryId, setDiagnosticsHistoryId] = useState('');
  const [activeRunCompletedAt, setActiveRunCompletedAt] = useState<
    number | null
  >(null);

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const endpointContract =
    authoritativeEndpointContract !== undefined
      ? authoritativeEndpointContract
      : loadedEndpointContract;
  const effectiveRequestTypeUrl =
    trimOptional(endpointContract?.requestTypeUrl) ||
    trimOptional(selectedEndpoint?.requestTypeUrl);
  const effectiveDefaultPrompt = trimOptional(
    endpointContract?.defaultSmokePrompt,
  );
  const isChatEndpoint = Boolean(
    selectedEndpoint && isChatServiceEndpoint(selectedEndpoint),
  );
  const normalizedMemberId = trimOptional(memberId);
  const normalizedTeamId = trimOptional(teamId);
  const selectedPublishedServiceId = trimOptional(selectedService?.serviceId);
  const memberEndpointContractMatchesSelection = Boolean(
    endpointContract &&
      trimOptional(endpointContract.scopeId) === trimOptional(scopeId) &&
      trimOptional(endpointContract.memberId) === normalizedMemberId &&
      trimOptional(endpointContract.publishedServiceId) ===
        selectedPublishedServiceId &&
      trimOptional(endpointContract.endpointId) === selectedEndpointId,
  );
  const memberEndpointContractAllowsInvoke =
    runtimeTarget !== 'member' || memberEndpointContractMatchesSelection;
  const canStartWithoutInput = Boolean(
    isChatEndpoint &&
      runtimeTarget === 'member' &&
      normalizedMemberId &&
      selectedPublishedServiceId &&
      normalizeStudioMemberBindingImplementationKind(
        memberRevision?.implementationKind,
      ) === 'workflow' &&
      trimOptional(memberRevision?.workflowDefinitionActorId) &&
      memberEndpointContractMatchesSelection,
  );
  const preferredServiceId = useMemo(
    () => getPreferredScopeConsoleServiceId(services),
    [services],
  );
  const currentMemberLabel =
    trimOptional(selectedMemberLabel) ||
    trimOptional(selectedService?.displayName) ||
    t("pages.studio.studiomemberinvokepanel.current.member", "Member");
  const canInvoke = Boolean(
    scopeId &&
      normalizedMemberId &&
      selectedService &&
      selectedEndpoint &&
      memberEndpointContractAllowsInvoke,
  );
  const invokeRouteTarget = useMemo(
    () =>
      resolveInvokeRouteTarget({
        memberId: normalizedMemberId,
        runtimeTarget,
        selectedServiceId: selectedService?.serviceId,
        teamId: normalizedTeamId,
      }),
    [
      normalizedMemberId,
      normalizedTeamId,
      runtimeTarget,
      selectedService?.serviceId,
    ],
  );
  const visibleRequestHistory = useMemo(() => {
    const currentServiceId =
      trimOptional(selectedService?.serviceId) ||
      trimOptional(initialServiceId);
    if (!currentServiceId) {
      return [];
    }

    return requestHistory.filter(
      (entry) => entry.serviceId === currentServiceId,
    );
  }, [initialServiceId, requestHistory, selectedService?.serviceId]);
  const currentRunViewModel = useMemo(() => {
    return buildStudioInvokeCurrentRunViewModel({
      activeRunCompletedAt,
      chatMessageCount: chatMessages.length,
      currentMemberLabel,
      currentRunRequest,
      invokeResult,
      isChatEndpoint,
      payloadBase64,
      payloadTypeUrl,
      selectedEndpointId,
      selectedServiceDisplayName: selectedService?.displayName,
      selectedServiceId,
    });
  }, [
    activeRunCompletedAt,
    chatMessages.length,
    currentMemberLabel,
    currentRunRequest,
    invokeResult,
    isChatEndpoint,
    payloadBase64,
    payloadTypeUrl,
    selectedEndpointId,
    selectedService?.displayName,
    selectedServiceId,
  ]);
  const currentRunHasData = currentRunViewModel.hasData;
  const currentObserveSessionSeed = currentRunViewModel.observeSessionSeed;
  const currentRawOutput = currentRunViewModel.rawOutput;
  const runElapsedLabel = formatElapsedTime(
    currentRunRequest?.startedAt ?? null,
    activeRunCompletedAt,
  );
  const endpointLabel =
    selectedEndpoint?.displayName || selectedEndpointId || '—';
  const endpointSummaryLabel =
    selectedEndpoint?.displayName ||
    t("pages.studio.studiomemberinvokepanel.endpoint", "Endpoint");
  const invokePresentation = presentation ?? targetSummaryVariant;
  const currentImplementationKind =
    normalizeStudioMemberBindingImplementationKind(
      memberRevision?.implementationKind,
    );
  const lifecycleLabel = getLifecycleLabel(memberRevision);
  const targetMetaItems = useMemo<TargetMetaItem[]>(() => {
    if (invokePresentation === 'member-run') {
      return [
        {
          key: 'endpoint',
          label: t("pages.studio.studiomemberinvokepanel.endpoint", "Endpoint"),
          value: endpointSummaryLabel,
        },
        {
          key: 'status',
          label: t("pages.studio.studiomemberinvokepanel.status", "Status"),
          value: lifecycleLabel,
        },
      ];
    }

    return [
      ...(normalizedTeamId
        ? [
            {
              key: 'team',
              label: t("pages.studio.studiomemberinvokepanel.team", "Team"),
              value: t("pages.studio.studiomemberinvokepanel.team.context", "Team context"),
            },
          ]
        : []),
      {
        key: 'member',
        label: t("pages.studio.studiomemberinvokepanel.member", "Member"),
        value: currentMemberLabel,
      },
      {
        key: 'service',
        label: t("pages.studio.studiomemberinvokepanel.service", "Service"),
        value:
          selectedService?.displayName ||
          t("pages.studio.studiomemberinvokepanel.bound.service", "Bound service"),
      },
      {
        key: 'endpoint',
        label: t("pages.studio.studiomemberinvokepanel.endpoint", "Endpoint"),
        value: endpointSummaryLabel,
      },
      {
        key: 'implementation',
        value: currentImplementationKind,
      },
      {
        key: 'lifecycle',
        label: t("pages.studio.studiomemberinvokepanel.lifecycle", "Lifecycle"),
        value: lifecycleLabel,
      },
    ];
  }, [
    currentImplementationKind,
    currentMemberLabel,
    endpointSummaryLabel,
    lifecycleLabel,
    normalizedTeamId,
    selectedService?.displayName,
    invokePresentation,
  ]);
  const invokeBlockedReason = !scopeId
    ? t("pages.studio.studiomemberinvokepanel.missing.workspace.scope", "Missing workspace scope.")
    : !normalizedMemberId
      ? t("pages.studio.studiomemberinvokepanel.missing.team.member.target", "Missing Team member target.")
      : !selectedService
        ? t("pages.studio.studiomemberinvokepanel.select.published.member.service", "Select a published member service before running.")
        : !selectedEndpoint
          ? t("pages.studio.studiomemberinvokepanel.select.endpoint.before.invoking", "Select an endpoint before running.")
          : !memberEndpointContractAllowsInvoke
            ? t(
                "pages.studio.studiomemberinvokepanel.endpoint.contract.changed",
                "The selected member endpoint is unavailable or no longer matches this published service.",
              )
          : '';
  const selectedHistoryEntry =
    visibleRequestHistory.find((entry) => entry.id === selectedHistoryId) ??
    null;
  const diagnosticsHistoryEntry =
    visibleRequestHistory.find((entry) => entry.id === diagnosticsHistoryId) ??
    null;
  const runViewMode = 'latest';
  const diagnosticsRunViewMode = diagnosticsHistoryEntry
    ? 'historical'
    : 'latest';
  const diagnosticsRunRequest = diagnosticsHistoryEntry
    ? {
        mode: diagnosticsHistoryEntry.mode,
        payloadBase64: diagnosticsHistoryEntry.payloadBase64,
        payloadTypeUrl: diagnosticsHistoryEntry.payloadTypeUrl,
        prompt: diagnosticsHistoryEntry.prompt,
        startedAt: diagnosticsHistoryEntry.startedAt,
      }
    : currentRunRequest;
  const diagnosticsInvokeResult =
    diagnosticsHistoryEntry?.snapshot.result ?? invokeResult;
  const diagnosticsChatMessages =
    diagnosticsHistoryEntry?.snapshot.chatMessages ?? chatMessages;
  const diagnosticsCompletedAt =
    diagnosticsHistoryEntry?.completedAt ?? activeRunCompletedAt;
  const diagnosticsRawOutput =
    diagnosticsHistoryEntry?.snapshot.result.responseJson
      ? diagnosticsHistoryEntry.snapshot.result.responseJson
      : diagnosticsHistoryEntry
        ? JSON.stringify(
            {
              endpointId: diagnosticsHistoryEntry.endpointId || undefined,
              error: diagnosticsHistoryEntry.errorDetail || undefined,
              eventCount:
                diagnosticsHistoryEntry.snapshot.result.eventCount ||
                diagnosticsHistoryEntry.snapshot.result.events.length,
              mode: diagnosticsHistoryEntry.mode,
              runId: diagnosticsHistoryEntry.runId || undefined,
              serviceId: diagnosticsHistoryEntry.serviceId || undefined,
              status: diagnosticsHistoryEntry.status,
            },
            null,
            2,
          )
        : currentRawOutput;
  const diagnosticsRunElapsedLabel = diagnosticsHistoryEntry
    ? formatElapsedTime(
        diagnosticsHistoryEntry.startedAt,
        diagnosticsHistoryEntry.completedAt,
      )
    : runElapsedLabel;
  const diagnosticsEndpointLabel =
    diagnosticsHistoryEntry?.endpointLabel || endpointLabel;
  const diagnosticsHasData = diagnosticsHistoryEntry ? true : currentRunHasData;
  const isMemberRunSurface = invokePresentation === 'member-run';
  const canAttachFiles =
    enableFileAttachments && isMemberRunSurface && isChatEndpoint;

  useEffect(() => {
    if (!services.length) {
      setSelectedServiceId('');
      return;
    }

    if (
      selectedServiceId &&
      services.some((service) => service.serviceId === selectedServiceId)
    ) {
      return;
    }

    const normalizedInitialServiceId = trimOptional(initialServiceId);
    if (
      normalizedInitialServiceId &&
      services.some(
        (service) => service.serviceId === normalizedInitialServiceId,
      )
    ) {
      setSelectedServiceId(normalizedInitialServiceId);
      return;
    }

    setSelectedServiceId(preferredServiceId);
  }, [initialServiceId, preferredServiceId, selectedServiceId, services]);

  useEffect(() => {
    if (!selectedService) {
      setSelectedEndpointId('');
      return;
    }

    if (
      selectedEndpointId &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === selectedEndpointId,
      )
    ) {
      return;
    }

    const normalizedInitialServiceId = trimOptional(initialServiceId);
    const normalizedInitialEndpointId = trimOptional(initialEndpointId);
    if (
      normalizedInitialServiceId === selectedService.serviceId &&
      normalizedInitialEndpointId &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === normalizedInitialEndpointId,
      )
    ) {
      setSelectedEndpointId(normalizedInitialEndpointId);
      return;
    }

    setSelectedEndpointId(
      selectedService.endpoints.find(
        (endpoint) => endpoint.endpointId === 'chat',
      )?.endpointId ||
        selectedService.endpoints[0]?.endpointId ||
        '',
    );
  }, [
    initialEndpointId,
    initialServiceId,
    selectedEndpointId,
    selectedService,
  ]);

  useEffect(() => {
    onSelectionChange?.({
      endpointId: selectedEndpointId,
      serviceId: selectedServiceId,
    });
  }, [onSelectionChange, selectedEndpointId, selectedServiceId]);

  useEffect(() => {
    if (!onObserveSessionChange || !currentObserveSessionSeed) {
      return;
    }

    onObserveSessionChange(currentObserveSessionSeed);
  }, [currentObserveSessionSeed, onObserveSessionChange]);

  useEffect(() => {
    const endpointId = trimOptional(selectedEndpoint?.endpointId);
    const serviceId = trimOptional(selectedService?.serviceId);
    if (
      authoritativeEndpointContract !== undefined ||
      !scopeId ||
      !normalizedMemberId ||
      !endpointId ||
      !serviceId ||
      selectedService?.kind === 'nyxid-chat'
    ) {
      setLoadedEndpointContract(null);
      return;
    }

    let cancelled = false;
    setLoadedEndpointContract(null);

    const request = scopeRuntimeApi.getMemberEndpointContract(
      scopeId,
      normalizedMemberId,
      endpointId,
    );

    request
      .then((contract) => {
        if (cancelled) {
          return;
        }

        setLoadedEndpointContract(contract);
      })
      .catch(() => {
        if (cancelled) {
          return;
        }

        setLoadedEndpointContract(null);
      });

    return () => {
      cancelled = true;
    };
  }, [
    authoritativeEndpointContract,
    normalizedMemberId,
    scopeId,
    selectedEndpoint?.endpointId,
    selectedService?.kind,
    selectedService?.serviceId,
  ]);

  useEffect(() => {
    nyxIdChatBoundRef.current = false;
  }, [scopeId]);

  useEffect(() => {
    if (!selectedEndpoint || isChatServiceEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      return;
    }

    setPayloadTypeUrl(effectiveRequestTypeUrl);
  }, [effectiveRequestTypeUrl, selectedEndpoint]);

  useEffect(() => {
    const nextBindingKey = `${scopeId}::${selectedServiceId}::${selectedEndpointId}`;
    if (!previousBindingKeyRef.current) {
      previousBindingKeyRef.current = nextBindingKey;
      return;
    }

    if (previousBindingKeyRef.current === nextBindingKey) {
      return;
    }

    previousBindingKeyRef.current = nextBindingKey;
    activeHistoryEntryIdRef.current = '';
    setChatMessages([]);
    setCurrentRunRequest(null);
    setSelectedHistoryId('');
    setDiagnosticsHistoryId('');
    setDiagnosticsDrawerOpen(false);
    setFormError('');
    setAttachedFiles([]);
    setSubmittedFiles([]);
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
  }, [scopeId, selectedEndpointId, selectedServiceId]);

  useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    [],
  );

  useEffect(() => {
    const transcriptViewport = transcriptViewportRef.current;
    if (!transcriptViewport) {
      return;
    }

    transcriptViewport.scrollTo({
      behavior: chatMessages.length > 1 ? 'smooth' : 'auto',
      top: transcriptViewport.scrollHeight,
    });
  }, [chatMessages]);

  useEffect(() => {
    if (!selectedHistoryId) {
      return;
    }

    if (visibleRequestHistory.some((entry) => entry.id === selectedHistoryId)) {
      return;
    }

    setSelectedHistoryId('');
    setDiagnosticsHistoryId((current) =>
      current === selectedHistoryId ? '' : current,
    );
    setDiagnosticsDrawerOpen(false);
  }, [selectedHistoryId, visibleRequestHistory]);

  useEffect(() => {
    if (!diagnosticsHistoryId) {
      return;
    }

    if (
      visibleRequestHistory.some((entry) => entry.id === diagnosticsHistoryId)
    ) {
      return;
    }

    setDiagnosticsHistoryId('');
    setDiagnosticsDrawerOpen(false);
  }, [diagnosticsHistoryId, visibleRequestHistory]);

  useEffect(() => {
    if (!formError) {
      return;
    }

    setFormError('');
  }, [
    attachedFiles,
    payloadBase64,
    payloadTypeUrl,
    prompt,
    selectedEndpointId,
    selectedServiceId,
  ]);

  const ensureNyxIdChatBound = useCallback(async () => {
    if (!scopeId || nyxIdChatBoundRef.current) {
      return;
    }

    await studioApi.bindScopeGAgent(createNyxIdChatBindingInput(scopeId));
    nyxIdChatBoundRef.current = true;
  }, [scopeId]);

  const upsertRequestHistory = useCallback((entry: InvokeHistoryEntry) => {
    setRequestHistory((current) => [
      entry,
      ...current.filter((item) => item.id !== entry.id),
    ].slice(0, 8));
  }, []);

  const updateRequestHistoryEntry = useCallback(
    (
      entryId: string,
      updater: (entry: InvokeHistoryEntry) => InvokeHistoryEntry,
    ) => {
      if (!entryId) {
        return;
      }

      setRequestHistory((current) =>
        current.map((entry) => (entry.id === entryId ? updater(entry) : entry)),
      );
    },
    [],
  );

  const handleSelectHistoryEntry = useCallback(
    (entryId: string) => {
      const selectedEntry = requestHistory.find(
        (entry) => entry.id === entryId,
      );

      if (!selectedEntry) {
        return;
      }

      if (selectedEntry.status === 'running') {
        setSelectedHistoryId('');
        setDiagnosticsHistoryId('');
        setDiagnosticsDrawerOpen(false);
        return;
      }

      setSelectedHistoryId(entryId);
      setDiagnosticsHistoryId(entryId);
      setDiagnosticsDrawerOpen(true);
    },
    [requestHistory],
  );

  const restorePromptForNewRun = useCallback((nextPrompt: string) => {
    const normalizedPrompt = trimOptional(nextPrompt);
    if (!normalizedPrompt) {
      void message.warning(
        t("pages.studio.studiomemberinvokepanel.no.input.available.to.retry", "No input available to retry."),
      );
      return;
    }

    setPrompt(normalizedPrompt);
    setAttachedFiles([]);
    setSubmittedFiles([]);
    composerDockRef.current?.scrollIntoView({
      behavior: 'smooth',
      block: 'start',
    });
    window.setTimeout(() => {
      composerDockRef.current
        ?.querySelector<HTMLTextAreaElement>('textarea')
        ?.focus();
    }, 0);
    void message.info(
      isMemberRunSurface
        ? t(
            "pages.studio.studiomemberinvokepanel.input.restored.start.run",
            "Input restored. Start run to create a separate run.",
          )
        : t("pages.studio.studiomemberinvokepanel.prompt.restored.click.invoke", "Request restored. Run workflow to create a new run."),
    );
  }, [isMemberRunSurface]);

  const handleAttachmentsAdd = useCallback((files: readonly File[]) => {
    if (!files.length) {
      return;
    }

    setAttachedFiles((current) => [...current, ...files]);
  }, []);

  const handleAttachmentRemove = useCallback((index: number) => {
    setAttachedFiles((current) => current.filter((_, itemIndex) => itemIndex !== index));
  }, []);

  const handleAbort = useCallback(() => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    const completedAt = Date.now();
    setInvokeResult((current) => ({
      ...current,
      error: t("pages.studio.studiomemberinvokepanel.the.call.was.aborted", "The call was aborted."),
      status: 'cancelled',
    }));
    setActiveRunCompletedAt(completedAt);
    updateRequestHistoryEntry(activeHistoryEntryIdRef.current, (entry) => {
      const cancelledResult: InvokeResultState = {
        ...entry.snapshot.result,
        error: entry.snapshot.result.error || t("pages.studio.studiomemberinvokepanel.the.call.was.aborted.2", "The call was aborted."),
        status: 'cancelled',
      };

      return {
        ...entry,
        completedAt,
        errorDetail: cancelledResult.error,
        eventCount:
          cancelledResult.eventCount || cancelledResult.events.length,
        status: 'cancelled',
        summary: t("pages.studio.studiomemberinvokepanel.the.run.has.stopped", "The run has stopped and only partial output may currently be displayed."),
        snapshot: {
          chatMessages: cloneChatMessages(entry.snapshot.chatMessages),
          result: cloneInvokeResult(cancelledResult),
        },
      };
    });
    activeHistoryEntryIdRef.current = '';
  }, [updateRequestHistoryEntry]);

  const handleInvoke = useCallback(async () => {
    if (
      !canInvoke ||
      !scopeId ||
      !normalizedMemberId ||
      !selectedService ||
      !selectedEndpoint
    ) {
      return;
    }

    const trimmedPrompt = prompt.trim();
    const trimmedPayloadTypeUrl = payloadTypeUrl.trim();
    const trimmedPayloadBase64 = payloadBase64.trim();
    const runFiles = canAttachFiles ? [...attachedFiles] : [];
    const displayRunInput = formatRunInputDisplay(trimmedPrompt, runFiles);
    const startedAt = Date.now();
    const currentEndpointLabel =
      selectedEndpoint.displayName || selectedEndpoint.endpointId;
    const currentRunMode = isChatServiceEndpoint(selectedEndpoint)
      ? 'stream'
      : 'invoke';
    const emptyFile = runFiles.find((file) => file.size <= 0);

    if (
      isChatServiceEndpoint(selectedEndpoint) &&
      !canStartWithoutInput &&
      !trimmedPrompt &&
      runFiles.length === 0
    ) {
      setFormError(t("pages.studio.studiomemberinvokepanel.please.enter.prompt.before", "Enter a request before running this workflow."));
      return;
    }

    if (emptyFile) {
      setFormError(
        t(
          "pages.studio.studiomemberinvokepanel.remove.empty.file.before.running",
          "Remove empty file {name} before starting the run.",
          { name: emptyFile.name || t("pages.studio.studiomemberinvokepanel.this.file", "this file") },
        ),
      );
      return;
    }

    if (
      !isChatServiceEndpoint(selectedEndpoint) &&
      !trimmedPrompt &&
      !trimmedPayloadBase64
    ) {
      setFormError(t("pages.studio.studiomemberinvokepanel.please.enter.prompt.before.2", "Enter a request before running this workflow."));
      return;
    }

    if (
      !isChatServiceEndpoint(selectedEndpoint) &&
      trimmedPayloadTypeUrl &&
      !trimmedPayloadBase64 &&
      !isAutoEncodableTextPayloadTypeUrl(trimmedPayloadTypeUrl)
    ) {
      setFormError(
        `payloadBase64 is required for payloadTypeUrl '${trimmedPayloadTypeUrl}'.`,
      );
      return;
    }

    setFormError('');
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    const historyEntryId = createClientId('run');
    activeHistoryEntryIdRef.current = historyEntryId;
    setSelectedHistoryId('');
    setDiagnosticsHistoryId('');
    setDiagnosticsDrawerOpen(false);
    setCurrentRunRequest({
      mode: currentRunMode,
      payloadBase64: trimmedPayloadBase64,
      payloadTypeUrl: trimmedPayloadTypeUrl,
      prompt: displayRunInput,
      startedAt,
    });
    setActiveRunCompletedAt(null);
    setSubmittedFiles(runFiles);

    if (isChatServiceEndpoint(selectedEndpoint)) {
      const userMessageId = createClientId('user');
      const assistantMessageId = createClientId('assistant');
      const controller = new AbortController();
      const accumulator = createRuntimeEventAccumulator();
      const getAssistantContent = () =>
        getPreferredRunOutput({
          assistantText: accumulator.assistantText,
          finalOutput: accumulator.finalOutput,
        });
      const buildChatRunMessages = (
        assistantStatus: StudioInvokeChatMessage['status'],
        assistantError?: string,
      ): StudioInvokeChatMessage[] => [
        {
          content: displayRunInput,
          id: userMessageId,
          role: 'user',
          status: 'complete',
          timestamp: startedAt,
        },
        {
          content: getAssistantContent(),
          error: assistantError,
          id: assistantMessageId,
          role: 'assistant',
          status: assistantStatus,
          thinking: accumulator.thinking || undefined,
          timestamp: startedAt + 1,
        },
      ];

      abortControllerRef.current = controller;
      const pendingMessages = buildChatRunMessages('streaming');
      const pendingResult = createPendingRunResult({
        endpointId: selectedEndpoint.endpointId,
        mode: 'stream',
        serviceId: selectedService.serviceId,
      });
      setChatMessages(pendingMessages);
      setPrompt('');
      setAttachedFiles([]);
      setInvokeResult(pendingResult);
      upsertRequestHistory(
        createPendingHistoryEntry({
          chatMessages: pendingMessages,
          endpointId: selectedEndpoint.endpointId,
          endpointLabel: currentEndpointLabel,
          id: historyEntryId,
          mode: 'stream',
          payloadBase64: '',
          payloadTypeUrl: '',
          prompt: displayRunInput,
          result: pendingResult,
          serviceId: selectedService.serviceId,
          startedAt,
        }),
      );

      try {
        if (selectedService.kind === 'nyxid-chat') {
          await ensureNyxIdChatBound();
        }

        const response = await runtimeRunsApi.streamEndpoint(
          scopeId,
          {
            endpointId: selectedEndpoint.endpointId,
            files: runFiles.length > 0 ? runFiles : undefined,
            prompt: trimmedPrompt,
          },
          controller.signal,
          invokeRouteTarget,
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          const nextChatMessages = buildChatRunMessages(
            accumulator.errorText ? 'error' : 'streaming',
            accumulator.errorText || undefined,
          );
          const nextResult: InvokeResultState = {
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            correlationId: accumulator.correlationId,
            endpointId: selectedEndpoint.endpointId,
            errorCode: accumulator.errorCode,
            error: accumulator.errorText,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
            finalOutput: accumulator.finalOutput,
            mode: 'stream',
            responseJson: '',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            status: accumulator.errorText ? 'error' : 'running',
            steps: [...accumulator.steps],
            thinking: accumulator.thinking,
            toolCalls: [...accumulator.toolCalls],
          };
          setChatMessages(nextChatMessages);
          setInvokeResult(nextResult);
          updateRequestHistoryEntry(historyEntryId, (entry) => ({
            ...entry,
            eventCount: nextResult.eventCount || nextResult.events.length,
            runId: nextResult.runId,
            snapshot: {
              chatMessages: cloneChatMessages(nextChatMessages),
              result: cloneInvokeResult(nextResult),
            },
            summary:
              accumulator.errorText ||
              trimOptional(accumulator.finalOutput) ||
              accumulator.assistantText ||
              displayRunInput ||
              entry.summary,
          }));
        }

        if (!controller.signal.aborted) {
          const completedAt = Date.now();
          const finalChatMessages = buildChatRunMessages(
            accumulator.errorText ? 'error' : 'complete',
            accumulator.errorText || undefined,
          );
          const finalResult: InvokeResultState = {
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            correlationId: accumulator.correlationId,
            endpointId: selectedEndpoint.endpointId,
            errorCode: accumulator.errorCode,
            error: accumulator.errorText,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
            finalOutput: accumulator.finalOutput,
            mode: 'stream',
            responseJson: '',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            status: accumulator.errorText ? 'error' : 'success',
            steps: [...accumulator.steps],
            thinking: accumulator.thinking,
            toolCalls: [...accumulator.toolCalls],
          };
          setChatMessages(finalChatMessages);
          setInvokeResult(finalResult);
          setActiveRunCompletedAt(completedAt);
          upsertRequestHistory({
            completedAt,
            createdAt: completedAt,
            endpointId: selectedEndpoint.endpointId,
            endpointLabel: currentEndpointLabel,
            errorDetail: accumulator.errorText,
            eventCount: accumulator.events.length,
            id: historyEntryId,
            mode: 'stream',
            payloadBase64: '',
            payloadTypeUrl: '',
            prompt: displayRunInput,
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            startedAt,
            status: accumulator.errorText ? 'error' : 'success',
            summary:
              accumulator.errorText ||
              trimOptional(accumulator.finalOutput) ||
              accumulator.assistantText ||
              displayRunInput ||
              t("pages.studio.studiomemberinvokepanel.this.run.returns.no", "This Run returns no additional text."),
            snapshot: {
              chatMessages: cloneChatMessages(finalChatMessages),
              result: cloneInvokeResult(finalResult),
            },
          });
          activeHistoryEntryIdRef.current = '';
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          const message =
            error instanceof Error ? error.message : String(error);
          const completedAt = Date.now();
          const finalChatMessages = buildChatRunMessages('error', message);
          const finalResult: InvokeResultState = {
            ...createIdleResult(),
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            correlationId: accumulator.correlationId,
            endpointId: selectedEndpoint.endpointId,
            errorCode: accumulator.errorCode,
            error: message,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
            finalOutput: accumulator.finalOutput,
            mode: 'stream',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            status: 'error',
            steps: [...accumulator.steps],
            thinking: accumulator.thinking,
            toolCalls: [...accumulator.toolCalls],
          };
          setChatMessages(finalChatMessages);
          setInvokeResult(finalResult);
          setActiveRunCompletedAt(completedAt);
          upsertRequestHistory({
            completedAt,
            createdAt: completedAt,
            endpointId: selectedEndpoint.endpointId,
            endpointLabel: currentEndpointLabel,
            errorDetail: message,
            eventCount: accumulator.events.length,
            id: historyEntryId,
            mode: 'stream',
            payloadBase64: '',
            payloadTypeUrl: '',
            prompt: displayRunInput,
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            startedAt,
            status: 'error',
            summary:
              message ||
              trimOptional(accumulator.finalOutput) ||
              accumulator.assistantText ||
              displayRunInput,
            snapshot: {
              chatMessages: cloneChatMessages(finalChatMessages),
              result: cloneInvokeResult(finalResult),
            },
          });
          activeHistoryEntryIdRef.current = '';
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }

      return;
    }

    const pendingResult = createPendingRunResult({
      endpointId: selectedEndpoint.endpointId,
      mode: 'invoke',
      serviceId: selectedService.serviceId,
    });
    setChatMessages([]);
    setInvokeResult(pendingResult);
    upsertRequestHistory(
      createPendingHistoryEntry({
        chatMessages: [],
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: currentEndpointLabel,
        id: historyEntryId,
        mode: 'invoke',
        payloadBase64: trimmedPayloadBase64,
        payloadTypeUrl: trimmedPayloadTypeUrl,
        prompt: trimmedPrompt,
        result: pendingResult,
        serviceId: selectedService.serviceId,
        startedAt,
      }),
    );

    try {
      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          payloadBase64: trimmedPayloadBase64 || undefined,
          payloadTypeUrl: trimmedPayloadTypeUrl || undefined,
          prompt: trimmedPrompt,
        },
        invokeRouteTarget,
      );
      const completedAt = Date.now();
      const { actorId, commandId, correlationId, runId } =
        extractRuntimeInvokeReceipt(response);
      const events: RuntimeEvent[] = [
        {
          commandId: commandId || undefined,
          correlationId: correlationId || undefined,
          runId: runId || undefined,
          threadId: actorId || undefined,
          timestamp: completedAt,
          type: 'RUN_STARTED',
        } as RuntimeEvent,
      ];

      if (actorId || commandId || correlationId) {
        events.push({
          name: 'aevatar.run.context',
          timestamp: completedAt,
          type: 'CUSTOM',
          value: {
            actorId: actorId || undefined,
            commandId: commandId || undefined,
            correlationId: correlationId || undefined,
          },
        } as RuntimeEvent);
      }

      const finalResult: InvokeResultState = {
        ...createIdleResult(),
        actorId,
        commandId,
        correlationId,
        endpointId: selectedEndpoint.endpointId,
        eventCount: events.length,
        events,
        finalOutput: '',
        mode: 'invoke',
        responseJson: JSON.stringify(response, null, 2),
        runId,
        serviceId: selectedService.serviceId,
        status: 'success',
      };
      setInvokeResult(finalResult);
      setActiveRunCompletedAt(completedAt);
      upsertRequestHistory({
        completedAt,
        createdAt: completedAt,
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: currentEndpointLabel,
        errorDetail: '',
        eventCount: events.length,
        id: historyEntryId,
        mode: 'invoke',
        payloadBase64: trimmedPayloadBase64,
        payloadTypeUrl: trimmedPayloadTypeUrl,
        prompt: trimmedPrompt,
        runId,
        serviceId: selectedService.serviceId,
        startedAt,
        status: 'success',
        summary:
          trimPreview(trimmedPrompt, 72) ||
          trimPreview(trimmedPayloadTypeUrl, 72) ||
          t("pages.studio.studiomemberinvokepanel.structured.call", "structured call"),
        snapshot: {
          chatMessages: [],
          result: cloneInvokeResult(finalResult),
        },
      });
      activeHistoryEntryIdRef.current = '';
    } catch (error) {
      const completedAt = Date.now();
      const message = error instanceof Error ? error.message : String(error);
      const finalResult: InvokeResultState = {
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        error: message,
        finalOutput: '',
        mode: 'invoke',
        serviceId: selectedService.serviceId,
        status: 'error',
      };
      setInvokeResult(finalResult);
      setActiveRunCompletedAt(completedAt);
      upsertRequestHistory({
        completedAt,
        createdAt: completedAt,
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: currentEndpointLabel,
        errorDetail: message,
        eventCount: 0,
        id: historyEntryId,
        mode: 'invoke',
        payloadBase64: trimmedPayloadBase64,
        payloadTypeUrl: trimmedPayloadTypeUrl,
        prompt: trimmedPrompt,
        runId: '',
        serviceId: selectedService.serviceId,
        startedAt,
        status: 'error',
        summary: message,
        snapshot: {
          chatMessages: [],
          result: cloneInvokeResult(finalResult),
        },
      });
      activeHistoryEntryIdRef.current = '';
    }
  }, [
    ensureNyxIdChatBound,
    normalizedMemberId,
    payloadBase64,
    payloadTypeUrl,
    prompt,
    attachedFiles,
    canAttachFiles,
    canInvoke,
    canStartWithoutInput,
    invokeRouteTarget,
    scopeId,
    selectedEndpoint,
    selectedService,
    updateRequestHistoryEntry,
    upsertRequestHistory,
  ]);

  const handleClear = useCallback(() => {
    setChatMessages([]);
    setCurrentRunRequest(null);
    setFormError('');
    setAttachedFiles([]);
    setSubmittedFiles([]);
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
    setSelectedHistoryId('');
    setDiagnosticsHistoryId('');
    setDiagnosticsDrawerOpen(false);
  }, []);

  const renderHistoryPanel = (
    style: React.CSSProperties,
    variant?: 'default' | 'ledger',
  ) => (
    <StudioMemberInvokeHistoryPanel
      entries={visibleRequestHistory}
      getEntryOutputText={(entryId) => {
        const entry = requestHistory.find((item) => item.id === entryId);
        return entry ? getHistoryOutputText(entry) : '';
      }}
      selectedHistoryId={selectedHistoryId}
      style={style}
      variant={variant}
      onCopyInput={(entryId) => {
        const entry = requestHistory.find((item) => item.id === entryId);
        writeClipboardText(entry?.prompt || '', 'Input');
      }}
      onCopyOutput={(entryId) => {
        const entry = requestHistory.find((item) => item.id === entryId);
        writeClipboardText(entry ? getHistoryOutputText(entry) : '', 'Output');
      }}
      onRetryAsNewRun={(entryId) => {
        const entry = requestHistory.find((item) => item.id === entryId);
        restorePromptForNewRun(entry?.prompt || '');
      }}
      onSelectEntry={handleSelectHistoryEntry}
    />
  );

  return (
    <div data-testid="studio-member-invoke-panel" style={surfaceStyle}>
      {!scopeId ? (
        <Alert
          showIcon
          message={t("pages.studio.studiomemberinvokepanel.please.determine.the.team", "Please determine the team scope first before calling this member.")}
          type="info"
        />
      ) : emptyState ? (
        <Alert
          showIcon
          message={emptyState.message}
          description={emptyState.description}
          type={emptyState.type || 'info'}
        />
      ) : services.length === 0 ? (
        <Alert
          showIcon
          message={t("pages.studio.studiomemberinvokepanel.there.are.no.published", "There are no published member services that can be called in the current scope.")}
          description={t("pages.studio.studiomemberinvokepanel.please.complete.the.binding", "Please complete the binding and release the version for the member before calling back here.")}
          type="warning"
        />
      ) : (
        <div
          data-testid="studio-invoke-workspace"
          style={
            isMemberRunSurface ? memberRunWorkspaceStyle : invokeWorkspaceStyle
          }
        >
          <div
            data-testid="studio-invoke-target-summary"
            style={
              isMemberRunSurface
                ? memberRunTargetSummaryStyle
                : targetSummaryStyle
            }
          >
            <div
              style={
                isMemberRunSurface
                  ? memberRunTargetTitleWrapStyle
                  : targetTitleWrapStyle
              }
            >
              <Tooltip placement="topLeft" title={currentMemberLabel}>
                <div
                  style={
                    isMemberRunSurface
                      ? memberRunTargetTitleStyle
                      : targetTitleStyle
                  }
                >
                  {currentMemberLabel}
                </div>
              </Tooltip>
              <div
                style={
                  isMemberRunSurface
                    ? memberRunTargetMetaStyle
                    : targetMetaStyle
                }
              >
                {renderTargetMetaItems(targetMetaItems)}
                {invokeBlockedReason ? (
                  <>
                    <span>·</span>
                    <Tooltip placement="topLeft" title={invokeBlockedReason}>
                      <span style={targetMetaItemStyle}>
                        {invokeBlockedReason}
                      </span>
                    </Tooltip>
                  </>
                ) : null}
              </div>
            </div>
            <div
              data-testid="studio-invoke-target-actions"
              style={
                isMemberRunSurface
                  ? memberRunTargetActionStyle
                  : targetActionStyle
              }
            >
              <div
                style={
                  isMemberRunSurface ? memberRunTargetPillStyle : targetPillStyle
                }
              >
                <span
                  style={{
                    ...runStatusDotBaseStyle,
                    background: getInvokeStatusTone(invokeResult.status).dot,
                  }}
                />
                {getRunStatusLabel(invokeResult.status)}
              </div>
              {isMemberRunSurface ? (
                <>
                  <div style={memberRunMetricPillStyle}>
                    <span style={memberRunMetricLabelStyle}>
                      {t(
                        "pages.studio.studiomemberinvokepanel.elapsed",
                        "Elapsed",
                      )}
                    </span>
                    <span>{runElapsedLabel}</span>
                  </div>
                  <div style={memberRunMetricPillStyle}>
                    <span style={memberRunMetricLabelStyle}>
                      {t(
                        "pages.studio.studiomemberinvokepanel.runs",
                        "Runs",
                      )}
                    </span>
                    <span>{visibleRequestHistory.length}</span>
                  </div>
                </>
              ) : null}
              <Button
                icon={<InfoCircleOutlined />}
                onClick={() => {
                  setDiagnosticsHistoryId('');
                  setDiagnosticsDrawerOpen(true);
                }}
              >
                {isMemberRunSurface
                  ? t(
                      "pages.studio.studiomemberinvokepanel.technical.details",
                      "Technical details",
                    )
                  : t(
                      "pages.studio.studiomemberinvokepanel.inspector",
                      "Details",
                    )}
              </Button>
            </div>
          </div>

          {isMemberRunSurface ? (
            <div
              data-testid="studio-invoke-member-run-workbench"
              style={memberRunWorkbenchStyle}
            >
              <div
                data-testid="studio-invoke-composer-dock"
                ref={composerDockRef}
                style={memberRunLauncherStripStyle}
              >
                <div style={memberRunLauncherHeaderStyle}>
                  <span style={memberRunLauncherTitleStyle}>
                    {t(
                      "pages.studio.studiomemberinvokepanel.launch.run",
                      "Launch run",
                    )}
                  </span>
                  <span style={memberRunLauncherHintStyle}>
                    {t(
                      "pages.studio.studiomemberinvokepanel.isolated.run.hint",
                      "One input creates one isolated run.",
                    )}
                  </span>
                </div>
                <div style={memberRunComposerShellStyle}>
                  <StudioMemberInvokeComposerPanel
                    acceptedFileTypes="image/png,image/jpeg,image/webp,audio/mpeg,audio/wav,audio/wave,audio/x-wav,video/mp4,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,text/csv,text/plain,text/markdown,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                    attachments={
                      invokeResult.status === 'running'
                        ? submittedFiles
                        : attachedFiles
                    }
                    blockedReason={invokeBlockedReason}
                    canInvoke={canInvoke}
                    currentRunPrompt={currentRunRequest?.prompt || ''}
                    defaultPrompt={effectiveDefaultPrompt}
                    enableFileAttachments={enableFileAttachments}
                    formError={formError}
                    invokeStatus={invokeResult.status}
                    isHistoricalRunSelected={Boolean(selectedHistoryEntry)}
                    isChatEndpoint={isChatEndpoint}
                    layout="member-run"
                    prompt={prompt}
                    onAbort={handleAbort}
                    onAttachmentsAdd={handleAttachmentsAdd}
                    onAttachmentRemove={handleAttachmentRemove}
                    onClear={handleClear}
                    onInvoke={() => void handleInvoke()}
                    onPromptChange={setPrompt}
                  />
                </div>
              </div>
              <div
                data-testid="studio-invoke-main-debug-area"
                style={memberRunMainAreaStyle}
              >
                <div
                  data-testid="studio-invoke-run-output-section"
                  style={memberRunOutputSectionStyle}
                >
                  <div
                    data-testid="studio-invoke-run-output-body"
                    style={memberRunSectionBodyStyle}
                  >
                    <div style={runConsolePanelStyle}>
                      <div
                        data-testid="studio-invoke-current-run-viewport"
                        style={memberRunCurrentRunViewportStyle}
                      >
                        <StudioMemberCurrentRunPanel
                          chatMessages={chatMessages}
                          currentRunHasData={currentRunHasData}
                          currentRunRequest={currentRunRequest}
                          endpointLabel={endpointLabel}
                          invokeResult={invokeResult}
                          presentation="member-run"
                          runElapsedLabel={runElapsedLabel}
                          runViewMode={runViewMode}
                          transcriptViewportRef={transcriptViewportRef}
                          onCopyError={() =>
                            writeClipboardText(invokeResult.error, 'Error')
                          }
                          onOpenDiagnostics={() => {
                            setDiagnosticsHistoryId('');
                            setDiagnosticsDrawerOpen(true);
                          }}
                          onRetryAsNewRun={() => {
                            restorePromptForNewRun(
                              currentRunRequest?.prompt || '',
                            );
                          }}
                        />
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          ) : (
            <>
              <div
                data-testid="studio-invoke-composer-dock"
                ref={composerDockRef}
                style={invokeComposerDockStyle}
              >
                <StudioMemberInvokeComposerPanel
                  blockedReason={invokeBlockedReason}
                  canInvoke={canInvoke}
                  defaultPrompt={effectiveDefaultPrompt}
                  formError={formError}
                  invokeStatus={invokeResult.status}
                  isHistoricalRunSelected={Boolean(selectedHistoryEntry)}
                  isChatEndpoint={isChatEndpoint}
                  layout="dock"
                  prompt={prompt}
                  onAbort={handleAbort}
                  onClear={handleClear}
                  onInvoke={() => void handleInvoke()}
                  onPromptChange={setPrompt}
                />
              </div>

              <div
                data-testid="studio-invoke-main-debug-area"
                style={mainDebugAreaStyle}
              >
                <div
                  data-testid="studio-invoke-run-output-section"
                  style={invokeRunOutputSectionStyle}
                >
                  <div style={{ flex: '0 0 auto', padding: '12px 14px 0' }}>
                    <span style={invokeSectionTitleStyle}>
                      {t(
                        "pages.studio.studiomemberinvokepanel.run.output",
                        "Response",
                      )}
                    </span>
                  </div>
                  <div
                    data-testid="studio-invoke-run-output-body"
                    style={invokeRunOutputBodyStyle}
                  >
                    <div style={runConsolePanelStyle}>
                      <div
                        data-testid="studio-invoke-current-run-viewport"
                        style={currentRunViewportStyle}
                      >
                        <StudioMemberCurrentRunPanel
                          chatMessages={chatMessages}
                          currentRunHasData={currentRunHasData}
                          currentRunRequest={currentRunRequest}
                          endpointLabel={endpointLabel}
                          invokeResult={invokeResult}
                          presentation="default"
                          runElapsedLabel={runElapsedLabel}
                          runViewMode={runViewMode}
                          transcriptViewportRef={transcriptViewportRef}
                          onCopyError={() =>
                            writeClipboardText(invokeResult.error, 'Error')
                          }
                          onOpenDiagnostics={() => {
                            setDiagnosticsHistoryId('');
                            setDiagnosticsDrawerOpen(true);
                          }}
                          onRetryAsNewRun={() => {
                            restorePromptForNewRun(
                              currentRunRequest?.prompt || '',
                            );
                          }}
                        />
                      </div>
                    </div>
                  </div>
                </div>

                {renderHistoryPanel(invokeHistoryPanelStyle)}
              </div>
            </>
          )}

          {isMemberRunSurface
            ? renderHistoryPanel(memberRunHistoryPanelStyle, 'ledger')
            : null}

          <StudioInvokeDiagnosticsDrawer
            activeRunCompletedAt={diagnosticsCompletedAt}
            chatMessages={diagnosticsChatMessages}
            currentRawOutput={diagnosticsRawOutput}
            currentRunHasData={diagnosticsHasData}
            currentRunRequest={diagnosticsRunRequest}
            endpointLabel={diagnosticsEndpointLabel}
            historyEntry={diagnosticsHistoryEntry}
            invokeResult={diagnosticsInvokeResult}
            isChatEndpoint={isChatEndpoint}
            open={diagnosticsDrawerOpen}
            payloadBase64={payloadBase64}
            payloadTypeUrl={payloadTypeUrl}
            runElapsedLabel={diagnosticsRunElapsedLabel}
            runViewMode={diagnosticsRunViewMode}
            title={
              isMemberRunSurface
                ? t(
                    "pages.studio.studiomemberinvokepanel.technical.details",
                    "Technical details",
                  )
                : undefined
            }
            onClose={() => {
              setDiagnosticsDrawerOpen(false);
            }}
            onPayloadBase64Change={setPayloadBase64}
            onPayloadTypeUrlChange={setPayloadTypeUrl}
          />
        </div>
      )}
    </div>
  );
};

export default StudioMemberInvokePanel;
