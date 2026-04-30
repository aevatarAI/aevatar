import { Alert, Grid } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import type { ScopeServiceEndpointContract } from '@/shared/models/runtime/scopeServices';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import { saveObservedRunSessionPayload } from '@/shared/runs/draftRunSession';
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
  type StudioMemberBindingRevision,
} from '@/shared/studio/models';
import type { StudioObserveSessionSeed } from '@/shared/studio/observeSession';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
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
import {
  StudioMemberInvokeComposerPanel,
  StudioMemberInvokeContractPanel,
} from './StudioMemberInvokeSetupPanels';

type StudioMemberInvokePanelProps = {
  readonly scopeId: string;
  readonly memberId?: string;
  readonly memberRevision?: StudioMemberBindingRevision | null;
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

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function trimPreview(value: string, limit = 180): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '';
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

function cloneChatMessages(
  messages: readonly StudioInvokeChatMessage[],
): StudioInvokeChatMessage[] {
  return messages.map((message) => ({ ...message }));
}

function getContractStatusLabel(options: {
  hasEndpoint: boolean;
  hasMember: boolean;
}): string {
  if (!options.hasMember) {
    return '未选中成员';
  }

  if (!options.hasEndpoint) {
    return '缺少端点';
  }

  return '已就绪';
}

function getPreferredRunOutput(options: {
  assistantText: string;
  finalOutput: string;
}): string {
  return trimOptional(options.finalOutput) || trimOptional(options.assistantText);
}

function formatElapsedTime(startedAt: number | null, completedAt: number | null): string {
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
      return 'Success';
    case 'error':
      return 'Error';
    default:
      return 'Idle';
  }
}

function getRunStatusDotStyle(status: InvokeResultState['status']): React.CSSProperties {
  if (status === 'running') {
    return { background: '#1677ff' };
  }

  if (status === 'success') {
    return { background: '#22c55e' };
  }

  if (status === 'error') {
    return { background: '#ef4444' };
  }

  return { background: '#94a3b8' };
}

const surfaceStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
  minWidth: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingBottom: 12,
};

const consoleFrameStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  minHeight: 0,
  minWidth: 0,
};

const runConsolePanelStyle: React.CSSProperties = {
  display: 'grid',
  gap: 0,
  minHeight: 0,
};

const runSummaryGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateColumns: 'repeat(auto-fit, minmax(112px, 1fr))',
  marginBottom: 4,
  minWidth: 0,
};

const runSummaryCardStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  display: 'grid',
  gap: 2,
  minWidth: 0,
  padding: '7px 10px',
};

const runSummaryLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.4,
  lineHeight: '14px',
  textTransform: 'uppercase',
};

const contractDetailsStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  minWidth: 0,
  padding: '10px 12px',
};

const contractDetailsSummaryStyle: React.CSSProperties = {
  color: '#334155',
  cursor: 'pointer',
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
};

const contractDetailsBodyStyle: React.CSSProperties = {
  marginTop: 10,
};

const runSummaryValueStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#111827',
  display: 'flex',
  fontSize: 13,
  fontWeight: 700,
  gap: 6,
  lineHeight: '18px',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
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
  services,
  selectedMemberLabel,
  emptyState,
  returnTo,
  initialServiceId,
  initialEndpointId,
  onSelectionChange,
  onObserveSessionChange,
}) => {
  const screens = Grid.useBreakpoint();
  const abortControllerRef = useRef<AbortController | null>(null);
  const nyxIdChatBoundRef = useRef(false);
  const payloadTypeUrlEditedRef = useRef(false);
  const previousBindingKeyRef = useRef('');
  const transcriptAnchorRef = useRef<HTMLDivElement | null>(null);
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
  const [payloadJsonPreview, setPayloadJsonPreview] = useState('');
  const [endpointContract, setEndpointContract] =
    useState<ScopeServiceEndpointContract | null>(null);
  const [endpointContractError, setEndpointContractError] = useState('');
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [currentRunRequest, setCurrentRunRequest] = useState<CurrentRunRequest | null>(
    null,
  );
  const [chatMessages, setChatMessages] = useState<StudioInvokeChatMessage[]>([]);
  const [requestHistory, setRequestHistory] = useState<InvokeHistoryEntry[]>([]);
  const [expandedHistoryId, setExpandedHistoryId] = useState('');
  const [consoleTab, setConsoleTab] = useState<
    'conversation' | 'timeline' | 'events'
  >('conversation');
  const [activeRunCompletedAt, setActiveRunCompletedAt] = useState<number | null>(
    null,
  );

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const effectiveRequestTypeUrl =
    trimOptional(endpointContract?.requestTypeUrl) ||
    trimOptional(selectedEndpoint?.requestTypeUrl);
  const effectiveResponseTypeUrl =
    trimOptional(endpointContract?.responseTypeUrl) ||
    trimOptional(selectedEndpoint?.responseTypeUrl);
  const effectiveDefaultPrompt = trimOptional(endpointContract?.defaultSmokePrompt);
  const effectiveSampleRequestJson = trimOptional(endpointContract?.sampleRequestJson);
  const isChatEndpoint = Boolean(
    selectedEndpoint && isChatServiceEndpoint(selectedEndpoint),
  );
  const preferredServiceId = useMemo(
    () => getPreferredScopeConsoleServiceId(services),
    [services],
  );
  const currentMemberRevision = memberRevision ?? null;
  const currentPublishedContext = describeStudioMemberBindingRevisionContext(
    currentMemberRevision,
  );
  const currentRevisionId =
    trimOptional(endpointContract?.revisionId) ||
    trimOptional(currentMemberRevision?.revisionId);
  const normalizedMemberId = trimOptional(memberId);
  const currentMemberActorId = trimOptional(currentMemberRevision?.primaryActorId);
  const currentMemberLabel =
    trimOptional(selectedMemberLabel) ||
    trimOptional(selectedService?.displayName) ||
    trimOptional(selectedService?.serviceId) ||
    '当前成员';
  const canInvoke = Boolean(scopeId && selectedService && selectedEndpoint);
  const visibleRequestHistory = useMemo(() => {
    const currentServiceId =
      trimOptional(selectedService?.serviceId) || trimOptional(initialServiceId);
    if (!currentServiceId) {
      return [];
    }

    return requestHistory.filter((entry) => entry.serviceId === currentServiceId);
  }, [initialServiceId, requestHistory, selectedService?.serviceId]);
  const expandedHistoryEntry =
    visibleRequestHistory.find((entry) => entry.id === expandedHistoryId) ?? null;
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
  const currentContractStatusLabel = getContractStatusLabel({
    hasEndpoint: Boolean(selectedEndpoint) && !endpointContractError,
    hasMember: Boolean(selectedService),
  });
  const consoleMinHeight = screens.xl || screens.lg ? 280 : 220;
  const runElapsedLabel = formatElapsedTime(
    currentRunRequest?.startedAt ?? null,
    activeRunCompletedAt,
  );
  const runIdLabel =
    trimOptional(invokeResult.runId) ||
    trimOptional(invokeResult.commandId) ||
    'Not started';
  const endpointLabel = selectedEndpoint?.displayName || selectedEndpointId || '—';

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
      services.some((service) => service.serviceId === normalizedInitialServiceId)
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
    if (!scopeId || !endpointId || !serviceId || selectedService?.kind === 'nyxid-chat') {
      setEndpointContract(null);
      setEndpointContractError('');
      return;
    }

    let cancelled = false;
    setEndpointContract(null);
    setEndpointContractError('');

    const request = normalizedMemberId
      ? scopeRuntimeApi.getMemberEndpointContract(
          scopeId,
          normalizedMemberId,
          endpointId,
        )
      : scopeRuntimeApi.getServiceEndpointContract(scopeId, serviceId, endpointId);

    request
      .then((contract) => {
        if (cancelled) {
          return;
        }

        setEndpointContract(contract);
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        setEndpointContract(null);
        setEndpointContractError(
          error instanceof Error ? error.message : String(error),
        );
      });

    return () => {
      cancelled = true;
    };
  }, [
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
    payloadTypeUrlEditedRef.current = false;
  }, [scopeId, selectedEndpointId, selectedServiceId]);

  useEffect(() => {
    if (!selectedEndpoint || isChatServiceEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      setPayloadJsonPreview('');
      return;
    }

    setPayloadTypeUrl((current) => {
      if (payloadTypeUrlEditedRef.current) {
        return current;
      }

      return effectiveRequestTypeUrl;
    });
    setPayloadJsonPreview((current) => current || effectiveSampleRequestJson);
  }, [effectiveRequestTypeUrl, effectiveSampleRequestJson, selectedEndpoint]);

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
    setChatMessages([]);
    setCurrentRunRequest(null);
    setExpandedHistoryId('');
    setFormError('');
    setInvokeResult(createIdleResult());
    setPayloadJsonPreview('');
    setActiveRunCompletedAt(null);
    setConsoleTab('conversation');
  }, [scopeId, selectedEndpointId, selectedServiceId]);

  useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    [],
  );

  useEffect(() => {
    transcriptAnchorRef.current?.scrollIntoView?.({
      behavior: chatMessages.length > 1 ? 'smooth' : 'auto',
      block: 'end',
    });
  }, [chatMessages]);

  useEffect(() => {
    if (!expandedHistoryId) {
      return;
    }

    if (visibleRequestHistory.some((entry) => entry.id === expandedHistoryId)) {
      return;
    }

    setExpandedHistoryId('');
  }, [expandedHistoryId, visibleRequestHistory]);

  useEffect(() => {
    if (!formError) {
      return;
    }

    setFormError('');
  }, [payloadBase64, payloadTypeUrl, prompt, selectedEndpointId, selectedServiceId]);

  const ensureNyxIdChatBound = useCallback(async () => {
    if (!scopeId || nyxIdChatBoundRef.current) {
      return;
    }

    await studioApi.bindScopeGAgent(createNyxIdChatBindingInput(scopeId));
    nyxIdChatBoundRef.current = true;
  }, [scopeId]);

  const appendRequestHistory = useCallback(
    (entry: Omit<InvokeHistoryEntry, 'id'>) => {
      const nextEntry: InvokeHistoryEntry = {
        ...entry,
        id: createClientId('request'),
      };
      setExpandedHistoryId(nextEntry.id);
      setRequestHistory((current) => [nextEntry, ...current].slice(0, 8));
    },
    [],
  );

  const handleSelectHistoryEntry = useCallback((entryId: string) => {
    setExpandedHistoryId((current) => (current === entryId ? '' : entryId));
  }, []);

  const handleAbort = useCallback(() => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setConsoleTab('conversation');
    setInvokeResult((current) => ({
      ...current,
      error: '调用已中止。',
      status: 'error',
    }));
    setActiveRunCompletedAt(Date.now());
  }, []);

  const handleInvoke = useCallback(async () => {
    if (!scopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    const trimmedPrompt = prompt.trim();
    const trimmedPayloadTypeUrl = payloadTypeUrl.trim();
    const trimmedPayloadBase64 = payloadBase64.trim();
    const startedAt = Date.now();

    if (isChatServiceEndpoint(selectedEndpoint) && !trimmedPrompt) {
      setFormError('请输入提示词后再开始对话。');
      return;
    }

    if (
      !isChatServiceEndpoint(selectedEndpoint) &&
      !trimmedPrompt &&
      !trimmedPayloadBase64
    ) {
      setFormError('请输入提示词或载荷后再执行调用。');
      return;
    }

    setFormError('');
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setConsoleTab('conversation');
    setCurrentRunRequest({
      mode: isChatServiceEndpoint(selectedEndpoint) ? 'stream' : 'invoke',
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
      setChatMessages(buildChatRunMessages('streaming'));
      setPrompt('');
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        mode: 'stream',
        serviceId: selectedService.serviceId,
        status: 'running',
      });

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
          {
            memberId: normalizedMemberId || undefined,
            serviceId: selectedService.serviceId,
          },
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          setChatMessages(
            buildChatRunMessages(
              accumulator.errorText ? 'error' : 'streaming',
              accumulator.errorText || undefined,
            ),
          );
          setInvokeResult({
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
          });
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
          appendRequestHistory({
            completedAt,
            createdAt: completedAt,
            endpointId: selectedEndpoint.endpointId,
            endpointLabel:
              selectedEndpoint.displayName || selectedEndpoint.endpointId,
            errorDetail: accumulator.errorText,
            eventCount: accumulator.events.length,
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
              '这轮对话没有返回额外文本。',
            snapshot: {
              chatMessages: cloneChatMessages(finalChatMessages),
              result: cloneInvokeResult(finalResult),
            },
          });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          const message = error instanceof Error ? error.message : String(error);
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
          appendRequestHistory({
            completedAt,
            createdAt: completedAt,
            endpointId: selectedEndpoint.endpointId,
            endpointLabel:
              selectedEndpoint.displayName || selectedEndpoint.endpointId,
            errorDetail: message,
            eventCount: accumulator.events.length,
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
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }

      return;
    }

    setChatMessages([]);
    setInvokeResult({
      ...createIdleResult(),
      endpointId: selectedEndpoint.endpointId,
      mode: 'invoke',
      serviceId: selectedService.serviceId,
      status: 'running',
    });

    try {
      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          payloadBase64: trimmedPayloadBase64 || undefined,
          payloadTypeUrl: trimmedPayloadTypeUrl || undefined,
          prompt: trimmedPrompt,
        },
        {
          memberId: normalizedMemberId || undefined,
          serviceId: selectedService.serviceId,
        },
      );
      const completedAt = Date.now();
      const {
        actorId,
        commandId,
        correlationId,
        runId,
      } = extractRuntimeInvokeReceipt(response);
      const events: RuntimeEvent[] = [
        {
          commandId: commandId || undefined,
          correlationId: correlationId || undefined,
          runId: runId || undefined,
          threadId: correlationId || undefined,
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
      appendRequestHistory({
        completedAt,
        createdAt: completedAt,
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: selectedEndpoint.displayName || selectedEndpoint.endpointId,
        errorDetail: '',
        eventCount: events.length,
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
          '结构化调用',
        snapshot: {
          chatMessages: [],
          result: cloneInvokeResult(finalResult),
        },
      });
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
      appendRequestHistory({
        completedAt,
        createdAt: completedAt,
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: selectedEndpoint.displayName || selectedEndpoint.endpointId,
        errorDetail: message,
        eventCount: 0,
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
    }
  }, [
    appendRequestHistory,
    ensureNyxIdChatBound,
    payloadBase64,
    payloadTypeUrl,
    prompt,
    scopeId,
    selectedEndpoint,
    selectedService,
  ]);

  const handleOpenRuns = useCallback(() => {
    if (!scopeId || !selectedEndpoint) {
      return;
    }

    const currentPrompt = currentRunRequest?.prompt || prompt.trim() || '';
    const currentPayloadTypeUrl =
      currentRunRequest?.payloadTypeUrl ||
      (!isChatServiceEndpoint(selectedEndpoint) && payloadTypeUrl.trim()) ||
      '';
    const currentPayloadBase64 =
      currentRunRequest?.payloadBase64 ||
      (!isChatServiceEndpoint(selectedEndpoint) && payloadBase64.trim()) ||
      '';
    const observedDraftKey =
      invokeResult.events.length > 0
        ? saveObservedRunSessionPayload({
            actorId: invokeResult.actorId || undefined,
            commandId: invokeResult.commandId || undefined,
            correlationId: invokeResult.correlationId || undefined,
            endpointId: invokeResult.endpointId || selectedEndpoint.endpointId,
            events: invokeResult.events,
            payloadBase64: currentPayloadBase64 || undefined,
            payloadTypeUrl: currentPayloadTypeUrl || undefined,
            prompt: currentPrompt,
            runId: invokeResult.runId || undefined,
            scopeId,
            serviceOverrideId: selectedService?.serviceId,
          })
        : '';

    history.push(
      buildRuntimeRunsHref({
        actorId: invokeResult.actorId || undefined,
        draftKey: observedDraftKey || undefined,
        endpointId: selectedEndpoint.endpointId,
        payloadTypeUrl: currentPayloadTypeUrl || undefined,
        prompt: currentPrompt || undefined,
        returnTo: returnTo || undefined,
        scopeId,
        serviceId: selectedService?.serviceId,
      }),
    );
  }, [
    currentRunRequest?.payloadBase64,
    currentRunRequest?.payloadTypeUrl,
    currentRunRequest?.prompt,
    invokeResult.actorId,
    invokeResult.commandId,
    invokeResult.endpointId,
    invokeResult.events,
    invokeResult.runId,
    payloadBase64,
    payloadTypeUrl,
    prompt,
    returnTo,
    scopeId,
    selectedEndpoint,
    selectedService?.serviceId,
  ]);

  const handleClear = useCallback(() => {
    setChatMessages([]);
    setConsoleTab('conversation');
    setCurrentRunRequest(null);
    setFormError('');
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
  }, []);

  return (
    <div data-testid="studio-member-invoke-panel" style={surfaceStyle}>
      {!scopeId ? (
        <Alert
          showIcon
          message="请先确定团队作用域，再调用这个成员。"
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
          message="当前作用域里还没有可调用的已发布成员服务。"
          description="请先为成员完成绑定并发布版本，然后再回到这里调用。"
          type="warning"
        />
      ) : (
        <>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            style={runConsolePanelStyle}
            title="Conversation"
            titleHelp="Invoke 现在按 runs 页的交互组织：先看当前 run，再从底部输入 prompt 发起调用。"
          >
            <div style={consoleFrameStyle}>
              <div style={runSummaryGridStyle}>
                <div style={runSummaryCardStyle}>
                  <div style={runSummaryLabelStyle}>Status</div>
                  <div style={runSummaryValueStyle}>
                    <span
                      style={{
                        ...runStatusDotBaseStyle,
                        ...getRunStatusDotStyle(invokeResult.status),
                      }}
                    />
                    {getRunStatusLabel(invokeResult.status)}
                  </div>
                </div>
                <div style={runSummaryCardStyle}>
                  <div style={runSummaryLabelStyle}>Run ID</div>
                  <div title={runIdLabel} style={runSummaryValueStyle}>
                    {runIdLabel}
                  </div>
                </div>
                <div style={runSummaryCardStyle}>
                  <div style={runSummaryLabelStyle}>Elapsed</div>
                  <div style={runSummaryValueStyle}>{runElapsedLabel}</div>
                </div>
                <div style={runSummaryCardStyle}>
                  <div style={runSummaryLabelStyle}>Endpoint</div>
                  <div title={endpointLabel} style={runSummaryValueStyle}>
                    {endpointLabel}
                  </div>
                </div>
              </div>
              <StudioMemberCurrentRunPanel
                activeTab={consoleTab}
                chatMessages={chatMessages}
                consoleMinHeight={consoleMinHeight}
                currentRawOutput={currentRawOutput}
                currentRunHasData={currentRunHasData}
                currentRunRequest={currentRunRequest}
                invokeResult={invokeResult}
                isChatEndpoint={isChatEndpoint}
                transcriptAnchorRef={transcriptAnchorRef}
                onTabChange={setConsoleTab}
              />
              <StudioMemberInvokeComposerPanel
                canInvoke={canInvoke}
                defaultPrompt={effectiveDefaultPrompt}
                effectiveRequestTypeUrl={effectiveRequestTypeUrl}
                effectiveResponseTypeUrl={effectiveResponseTypeUrl}
                endpointKind={selectedEndpoint?.kind || 'command'}
                formError={formError}
                hasOpenRunsTarget={Boolean(scopeId && selectedEndpoint)}
                invokeStatus={invokeResult.status}
                isChatEndpoint={isChatEndpoint}
                layout="dock"
                payloadBase64={payloadBase64}
                payloadJsonPreview={payloadJsonPreview}
                payloadTypeUrl={payloadTypeUrl}
                prompt={prompt}
                onAbort={handleAbort}
                onClear={handleClear}
                onInvoke={() => void handleInvoke()}
                onOpenRuns={handleOpenRuns}
                onPayloadBase64Change={setPayloadBase64}
                onPayloadJsonPreviewChange={setPayloadJsonPreview}
                onPayloadTypeUrlChange={(value) => {
                  payloadTypeUrlEditedRef.current = true;
                  setPayloadTypeUrl(value);
                }}
                onPromptChange={setPrompt}
              />
            </div>
          </AevatarPanel>

          <StudioMemberInvokeHistoryPanel
            entries={visibleRequestHistory}
            expandedHistoryId={expandedHistoryId}
            onSelectEntry={handleSelectHistoryEntry}
          />

          <details style={contractDetailsStyle}>
            <summary style={contractDetailsSummaryStyle}>
              Advanced contract details
            </summary>
            <div style={contractDetailsBodyStyle}>
              <StudioMemberInvokeContractPanel
                actorId={currentMemberActorId}
                contractError={endpointContractError}
                endpointLabel={selectedEndpoint?.displayName || selectedEndpointId}
                memberLabel={currentMemberLabel}
                publishedContext={currentPublishedContext}
                revisionId={currentRevisionId}
                statusLabel={currentContractStatusLabel}
              />
            </div>
          </details>
        </>
      )}
    </div>
  );
};

export default StudioMemberInvokePanel;
