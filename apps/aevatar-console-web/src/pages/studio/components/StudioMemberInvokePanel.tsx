import { Alert, message } from 'antd';
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
import type { ScopeServiceEndpointContract } from '@/shared/models/runtime/scopeServices';
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
  describeStudioMemberBindingRevisionContext,
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
import {
  getInvokeStatusTone,
  studioInvokeColors,
  trimOptional,
  trimPreview,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokePanelProps = {
  readonly scopeId: string;
  readonly memberId?: string;
  readonly memberRevision?: StudioMemberBindingRevision | null;
  readonly teamId?: string;
  readonly services: readonly ScopeConsoleServiceOption[];
  readonly selectedMemberLabel?: string;
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

function createClientId(prefix: string): string {
  const generated = globalThis.crypto?.randomUUID?.();
  if (generated) {
    return `${prefix}_${generated}`;
  }

  return `${prefix}_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
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

function getInvocationReadinessMessage(
  readiness: ScopeServiceEndpointContract['invocationReadiness'],
): string {
  switch (readiness?.reasonCode || readiness?.status) {
    case 'prepared_artifact_missing':
      return '绑定已完成，但当前版本的运行时 artifact 尚未准备好，暂时不能 Invoke。请重新 Bind 或等待后端完成准备。';
    case 'service_catalog_missing':
      return '成员服务尚未同步到服务目录，暂时不能 Invoke。';
    case 'serving_set_missing':
    case 'eligible_serving_target_missing':
      return '成员服务尚未进入可调用 serving target，暂时不能 Invoke。';
    case 'traffic_view_target_missing':
      return '运行时还未观测到可调用流量目标，暂时不能 Invoke。';
    case 'service_catalog_target_missing':
      return '当前 endpoint 尚未同步到服务目录，暂时不能 Invoke。';
    default:
      return readiness?.message || '后端尚未确认该成员可调用，暂时不能 Invoke。';
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

const targetTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 15,
  fontWeight: 800,
  lineHeight: '22px',
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

const invokeRunOutputBodyStyle: React.CSSProperties = {
  ...invokeSectionBodyStyle,
  gap: 10,
};

const currentRunViewportStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  flex: '0 0 auto',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const invokeHistoryPanelStyle: React.CSSProperties = {
  flex: '0 0 auto',
  minHeight: 0,
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

const runStatusDotBaseStyle: React.CSSProperties = {
  borderRadius: 999,
  display: 'inline-block',
  flex: '0 0 auto',
  height: 7,
  width: 7,
};

const StudioMemberInvokePanel: React.FC<StudioMemberInvokePanelProps> = ({
  scopeId,
  memberId,
  memberRevision,
  teamId,
  services,
  selectedMemberLabel,
  emptyState,
  initialServiceId,
  initialEndpointId,
  onSelectionChange,
  onObserveSessionChange,
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
  const [formError, setFormError] = useState('');
  const [payloadTypeUrl, setPayloadTypeUrl] = useState('');
  const [payloadBase64, setPayloadBase64] = useState('');
  const [endpointContract, setEndpointContract] =
    useState<ScopeServiceEndpointContract | null>(null);
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
  const [consoleTab, setConsoleTab] = useState<
    'output' | 'timeline' | 'events' | 'metadata'
  >('output');
  const [activeRunCompletedAt, setActiveRunCompletedAt] = useState<
    number | null
  >(null);

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const effectiveRequestTypeUrl =
    trimOptional(endpointContract?.requestTypeUrl) ||
    trimOptional(selectedEndpoint?.requestTypeUrl);
  const effectiveDefaultPrompt = trimOptional(
    endpointContract?.defaultSmokePrompt,
  );
  const isChatEndpoint = Boolean(
    selectedEndpoint && isChatServiceEndpoint(selectedEndpoint),
  );
  const preferredServiceId = useMemo(
    () => getPreferredScopeConsoleServiceId(services),
    [services],
  );
  const normalizedMemberId = trimOptional(memberId);
  const normalizedTeamId = trimOptional(teamId);
  const currentMemberLabel =
    trimOptional(selectedMemberLabel) ||
    trimOptional(selectedService?.displayName) ||
    trimOptional(selectedService?.serviceId) ||
    t("pages.studio.studiomemberinvokepanel.current.members", "current members");
  const invocationReadiness = endpointContract?.invocationReadiness ?? null;
  const isNyxIdChatService = selectedService?.kind === 'nyxid-chat';
  const invocationReady =
    isNyxIdChatService || invocationReadiness?.canInvoke === true;
  const canInvoke = Boolean(
    scopeId &&
      normalizedMemberId &&
      selectedService &&
      selectedEndpoint &&
      invocationReady,
  );
  const readinessBlockMessage = invocationReady
    ? ''
    : getInvocationReadinessMessage(invocationReadiness);
  const invokeRouteTarget = useMemo(
    () =>
      normalizedTeamId
        ? { teamId: normalizedTeamId }
        : { serviceId: selectedService?.serviceId },
    [normalizedTeamId, selectedService?.serviceId],
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
    endpointLabel === selectedEndpointId
      ? endpointLabel
      : `${endpointLabel} (${selectedEndpointId || '—'})`;
  const currentPublishedContext =
    describeStudioMemberBindingRevisionContext(memberRevision) || '';
  const currentImplementationKind =
    normalizeStudioMemberBindingImplementationKind(
      memberRevision?.implementationKind,
    );
  const currentRevisionId =
    trimOptional(endpointContract?.revisionId) ||
    trimOptional(memberRevision?.revisionId);
  const lifecycleLabel = getLifecycleLabel(memberRevision);
  const invokeBlockedReason = !scopeId
    ? t("pages.studio.studiomemberinvokepanel.missing.workspace.scope", "Missing workspace scope.")
    : !normalizedMemberId
      ? t("pages.studio.studiomemberinvokepanel.missing.team.member.target", "Missing Team member target.")
      : !selectedService
        ? t("pages.studio.studiomemberinvokepanel.select.published.member.service", "Select a published member service before invoking.")
        : !selectedEndpoint
          ? t("pages.studio.studiomemberinvokepanel.select.endpoint.before.invoking", "Select an endpoint before invoking.")
          : '';
  const runViewMode = selectedHistoryId ? 'historical' : 'latest';

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
      !scopeId ||
      !normalizedMemberId ||
      !endpointId ||
      !serviceId ||
      isNyxIdChatService
    ) {
      setEndpointContract(null);
      return;
    }

    let cancelled = false;
    setEndpointContract(null);

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

        setEndpointContract(contract);
      })
      .catch(() => {
        if (cancelled) {
          return;
        }

        setEndpointContract(null);
      });

    return () => {
      cancelled = true;
    };
  }, [
    normalizedMemberId,
    scopeId,
    isNyxIdChatService,
    selectedEndpoint?.endpointId,
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
    setFormError('');
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
    setConsoleTab('output');
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
  }, [selectedHistoryId, visibleRequestHistory]);

  useEffect(() => {
    if (!formError) {
      return;
    }

    setFormError('');
  }, [
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
        setConsoleTab('output');
        return;
      }

      setSelectedHistoryId(entryId);
      setChatMessages(cloneChatMessages(selectedEntry.snapshot.chatMessages));
      setCurrentRunRequest({
        mode: selectedEntry.mode,
        payloadBase64: selectedEntry.payloadBase64,
        payloadTypeUrl: selectedEntry.payloadTypeUrl,
        prompt: selectedEntry.prompt,
        startedAt: selectedEntry.startedAt,
      });
      setInvokeResult(cloneInvokeResult(selectedEntry.snapshot.result));
      setActiveRunCompletedAt(selectedEntry.completedAt);
      setConsoleTab('output');
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
      t("pages.studio.studiomemberinvokepanel.prompt.restored.click.invoke", "Prompt restored. Click Invoke to create a new Run."),
    );
  }, []);

  const handleAbort = useCallback(() => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    const completedAt = Date.now();
    setConsoleTab('output');
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
      !scopeId ||
      !normalizedMemberId ||
      !selectedService ||
      !selectedEndpoint
    ) {
      return;
    }

    if (!invocationReady) {
      setFormError(readinessBlockMessage);
      return;
    }

    const trimmedPrompt = prompt.trim();
    const trimmedPayloadTypeUrl = payloadTypeUrl.trim();
    const trimmedPayloadBase64 = payloadBase64.trim();
    const startedAt = Date.now();
    const currentEndpointLabel =
      selectedEndpoint.displayName || selectedEndpoint.endpointId;
    const currentRunMode = isChatServiceEndpoint(selectedEndpoint)
      ? 'stream'
      : 'invoke';

    if (isChatServiceEndpoint(selectedEndpoint) && !trimmedPrompt) {
      setFormError(t("pages.studio.studiomemberinvokepanel.please.enter.prompt.before", "Please enter Prompt before initiating Invoke."));
      return;
    }

    if (
      !isChatServiceEndpoint(selectedEndpoint) &&
      !trimmedPrompt &&
      !trimmedPayloadBase64
    ) {
      setFormError(t("pages.studio.studiomemberinvokepanel.please.enter.prompt.before.2", "Please enter Prompt before initiating Invoke."));
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
    setConsoleTab('output');
    setCurrentRunRequest({
      mode: currentRunMode,
      payloadBase64: trimmedPayloadBase64,
      payloadTypeUrl: trimmedPayloadTypeUrl,
      prompt: trimmedPrompt,
      startedAt,
    });
    setActiveRunCompletedAt(null);

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
          content: trimmedPrompt,
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
          prompt: trimmedPrompt,
          result: pendingResult,
          serviceId: selectedService.serviceId,
          startedAt,
        }),
      );

      try {
        if (selectedService.kind === 'nyxid-chat') {
          await ensureNyxIdChatBound();
        }

        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
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
            prompt: trimmedPrompt,
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            startedAt,
            status: accumulator.errorText ? 'error' : 'success',
            summary:
              accumulator.errorText ||
              trimOptional(accumulator.finalOutput) ||
              accumulator.assistantText ||
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
            prompt: trimmedPrompt,
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            startedAt,
            status: 'error',
            summary:
              message ||
              trimOptional(accumulator.finalOutput) ||
              accumulator.assistantText,
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
    invokeRouteTarget,
    invocationReady,
    readinessBlockMessage,
    scopeId,
    selectedEndpoint,
    selectedService,
    updateRequestHistoryEntry,
    upsertRequestHistory,
  ]);

  const handleClear = useCallback(() => {
    setChatMessages([]);
    setConsoleTab('output');
    setCurrentRunRequest(null);
    setFormError('');
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
    setSelectedHistoryId('');
  }, []);

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
        <div data-testid="studio-invoke-workspace" style={invokeWorkspaceStyle}>
          <div
            data-testid="studio-invoke-target-summary"
            style={targetSummaryStyle}
          >
              <div style={{ minWidth: 0 }}>
                <div title={currentMemberLabel} style={targetTitleStyle}>
                  {currentMemberLabel}
                </div>
                <div style={targetMetaStyle}>
                  {normalizedTeamId ? (
                    <>
                      <span>Team: {normalizedTeamId}</span>
                      <span>·</span>
                    </>
                  ) : null}
                  <span>Member: {normalizedMemberId || t("pages.studio.studiomemberinvokepanel.not.selected", "not selected")}</span>
                  <span>·</span>
                  <span>Service: {selectedService?.displayName || selectedServiceId || t("pages.studio.studiomemberinvokepanel.not.selected.2", "not selected")}</span>
                  <span>·</span>
                  <span>Endpoint: {endpointSummaryLabel}</span>
                  <span>·</span>
                  <span>{currentImplementationKind}</span>
                  <span>·</span>
                  <span>Lifecycle: {lifecycleLabel}</span>
                  {invokeBlockedReason ? (
                    <>
                      <span>·</span>
                      <span>{invokeBlockedReason}</span>
                    </>
                  ) : null}
                </div>
              </div>
            <div style={targetPillStyle}>
              <span
                style={{
                  ...runStatusDotBaseStyle,
                  background: getInvokeStatusTone(invokeResult.status).dot,
                }}
              />
              {getRunStatusLabel(invokeResult.status)}
            </div>
          </div>

          {!invocationReady ? (
            <Alert
              showIcon
              type="warning"
              message={t("pages.studio.studiomemberinvokepanel.member.not.invokable", "Member is not invokable yet")}
              description={readinessBlockMessage}
            />
          ) : null}

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
              isHistoricalRunSelected={runViewMode === 'historical'}
              isChatEndpoint={isChatEndpoint}
              layout="dock"
              payloadBase64={payloadBase64}
              payloadTypeUrl={payloadTypeUrl}
              prompt={prompt}
              onAbort={handleAbort}
              onClear={handleClear}
              onInvoke={() => void handleInvoke()}
              onPayloadBase64Change={setPayloadBase64}
              onPayloadTypeUrlChange={setPayloadTypeUrl}
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
                <span style={invokeSectionTitleStyle}>{t("pages.studio.studiomemberinvokepanel.run.output", "Run output")}</span>
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
                      activeRunCompletedAt={activeRunCompletedAt}
                      activeTab={consoleTab}
                      chatMessages={chatMessages}
                      currentRawOutput={currentRawOutput}
                      currentRunHasData={currentRunHasData}
                      currentRunRequest={currentRunRequest}
                      endpointLabel={endpointLabel}
                      invokeResult={invokeResult}
                      memberId={normalizedMemberId}
                      publishedContext={currentPublishedContext}
                      revisionId={currentRevisionId}
                      runElapsedLabel={runElapsedLabel}
                      runViewMode={runViewMode}
                      transcriptViewportRef={transcriptViewportRef}
                      onCopyError={() =>
                        writeClipboardText(invokeResult.error, 'Error')
                      }
                      onRetryAsNewRun={() => {
                        restorePromptForNewRun(currentRunRequest?.prompt || '');
                      }}
                      onTabChange={setConsoleTab}
                    />
                  </div>
                </div>
              </div>
            </div>

            <StudioMemberInvokeHistoryPanel
              entries={visibleRequestHistory}
              getEntryOutputText={(entryId) => {
                const entry = requestHistory.find((item) => item.id === entryId);
                return entry ? getHistoryOutputText(entry) : '';
              }}
              selectedHistoryId={selectedHistoryId}
              style={invokeHistoryPanelStyle}
              onCopyInput={(entryId) => {
                const entry = requestHistory.find((item) => item.id === entryId);
                writeClipboardText(entry?.prompt || '', 'Input');
              }}
              onCopyOutput={(entryId) => {
                const entry = requestHistory.find((item) => item.id === entryId);
                writeClipboardText(
                  entry ? getHistoryOutputText(entry) : '',
                  'Output',
                );
              }}
              onCopyRunId={(entryId) => {
                const entry = requestHistory.find((item) => item.id === entryId);
                writeClipboardText(
                  entry?.runId || entry?.snapshot.result.runId || '',
                  'Run id',
                );
              }}
              onRetryAsNewRun={(entryId) => {
                const entry = requestHistory.find((item) => item.id === entryId);
                restorePromptForNewRun(entry?.prompt || '');
              }}
              onSelectEntry={handleSelectHistoryEntry}
            />
          </div>
        </div>
      )}
    </div>
  );
};

export default StudioMemberInvokePanel;
