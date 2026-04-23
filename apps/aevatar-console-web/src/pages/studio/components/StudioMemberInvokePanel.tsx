import {
  ClearOutlined,
  CodeOutlined,
  DeploymentUnitOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Alert, Button, Empty, Input, Select, Space, Tabs, Tag, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { RuntimeEventPreviewPanel } from '@/shared/agui/runtimeConversationPresentation';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
  type RuntimeStepInfo,
  type RuntimeToolCallInfo,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import {
  saveObservedRunSessionPayload,
} from '@/shared/runs/draftRunSession';
import {
  createNyxIdChatBindingInput,
  extractRuntimeInvokeReceipt,
  getPreferredScopeConsoleServiceId,
  isChatServiceEndpoint,
  type ScopeConsoleServiceOption,
} from '@/shared/runs/scopeConsole';
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  getStudioScopeBindingCurrentRevision,
  type StudioScopeBindingStatus,
} from '@/shared/studio/models';
import { studioApi } from '@/shared/studio/api';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { AEVATAR_PRESSABLE_CARD_CLASS } from '@/shared/ui/interactionStandards';

type InvokeDockTab = 'chat' | 'events' | 'output';

type StudioMemberInvokePanelProps = {
  readonly scopeId: string;
  readonly scopeBinding?: StudioScopeBindingStatus | null;
  readonly services: readonly ScopeConsoleServiceOption[];
  readonly returnTo?: string;
  readonly initialServiceId?: string;
  readonly initialEndpointId?: string;
  readonly onSelectionChange?: (selection: {
    serviceId: string;
    endpointId: string;
  }) => void;
};

type InvokeResultState = {
  readonly actorId: string;
  readonly assistantText: string;
  readonly commandId: string;
  readonly endpointId: string;
  readonly error: string;
  readonly eventCount: number;
  readonly events: RuntimeEvent[];
  readonly mode: 'stream' | 'invoke';
  readonly responseJson: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly status: 'idle' | 'running' | 'success' | 'error';
  readonly steps: RuntimeStepInfo[];
  readonly thinking: string;
  readonly toolCalls: RuntimeToolCallInfo[];
};

type StudioInvokeChatMessage = {
  readonly content: string;
  readonly error?: string;
  readonly id: string;
  readonly role: 'assistant' | 'user';
  readonly status: 'complete' | 'error' | 'streaming';
  readonly thinking?: string;
  readonly timestamp: number;
};

type InvokeHistoryEntry = {
  readonly createdAt: number;
  readonly endpointId: string;
  readonly endpointLabel: string;
  readonly eventCount: number;
  readonly id: string;
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly responsePreview: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly serviceLabel: string;
  readonly status: 'success' | 'error';
};

const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";

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

function createIdleResult(): InvokeResultState {
  return {
    actorId: '',
    assistantText: '',
    commandId: '',
    endpointId: '',
    error: '',
    eventCount: 0,
    events: [],
    mode: 'invoke',
    responseJson: '',
    runId: '',
    serviceId: '',
    status: 'idle',
    steps: [],
    thinking: '',
    toolCalls: [],
  };
}

const surfaceStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
  overflow: 'auto',
  paddingBottom: 8,
};

const sectionStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 14,
  display: 'flex',
  flexDirection: 'column',
  gap: 14,
  padding: 16,
};

const summaryGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(132px, 1fr))',
};

const summaryLabelStyle: React.CSSProperties = {
  color: '#6b7280',
  fontSize: 12,
  fontWeight: 600,
  lineHeight: '18px',
  textTransform: 'uppercase',
};

const summaryValueStyle: React.CSSProperties = {
  color: '#111827',
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  overflowWrap: 'anywhere',
};

const controlsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))',
};

const bodyGridStyle: React.CSSProperties = {
  alignItems: 'start',
  display: 'grid',
  flex: 1,
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1.1fr) minmax(280px, 340px)',
  minHeight: 0,
};

const listColumnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
  minWidth: 0,
};

const transcriptStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  maxHeight: 420,
  minHeight: 220,
  overflowY: 'auto',
  paddingRight: 4,
};

const bubbleBaseStyle: React.CSSProperties = {
  border: '1px solid #e5e7eb',
  borderRadius: 14,
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  maxWidth: '88%',
  padding: '12px 14px',
};

const sidePanelStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
};

const metricCardsStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(110px, 1fr))',
};

const metricCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 12,
  display: 'grid',
  gap: 4,
  minWidth: 0,
  padding: 12,
};

const historyCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 12,
  cursor: 'pointer',
  display: 'grid',
  gap: 8,
  padding: 12,
  textAlign: 'left',
  width: '100%',
};

const compactEmptyStateStyle: React.CSSProperties = {
  alignItems: 'center',
  border: '1px dashed #d9d9d9',
  borderRadius: 12,
  color: '#6b7280',
  display: 'grid',
  gap: 6,
  justifyItems: 'center',
  padding: '18px 16px',
  textAlign: 'center',
};

const receiptStyle: React.CSSProperties = {
  background: '#0f172a',
  borderRadius: 14,
  color: '#e2e8f0',
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  maxHeight: 420,
  minHeight: 220,
  overflow: 'auto',
  padding: 16,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const metricRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: '20px minmax(0, 1fr)',
};

function trimPreview(value: string, limit = 180): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '';
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

function formatHistoryTimestamp(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return 'just now';
  }

  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    month: 'short',
    day: 'numeric',
  }).format(value);
}

const StudioMemberInvokePanel: React.FC<StudioMemberInvokePanelProps> = ({
  scopeId,
  scopeBinding,
  services,
  returnTo,
  initialServiceId,
  initialEndpointId,
  onSelectionChange,
}) => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const nyxIdChatBoundRef = useRef(false);
  const previousBindingKeyRef = useRef('');
  const transcriptAnchorRef = useRef<HTMLDivElement | null>(null);
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    trimOptional(initialServiceId),
  );
  const [selectedEndpointId, setSelectedEndpointId] = useState(() =>
    trimOptional(initialEndpointId),
  );
  const [prompt, setPrompt] = useState('');
  const [payloadTypeUrl, setPayloadTypeUrl] = useState('');
  const [payloadBase64, setPayloadBase64] = useState('');
  const [activeTab, setActiveTab] = useState<InvokeDockTab>('output');
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [chatMessages, setChatMessages] = useState<StudioInvokeChatMessage[]>([]);
  const [requestHistory, setRequestHistory] = useState<InvokeHistoryEntry[]>([]);
  const [focusedHistoryId, setFocusedHistoryId] = useState('');

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const isChatEndpoint = Boolean(
    selectedEndpoint && isChatServiceEndpoint(selectedEndpoint),
  );
  const currentBindingRevision = useMemo(
    () => getStudioScopeBindingCurrentRevision(scopeBinding),
    [scopeBinding],
  );
  const preferredServiceId = useMemo(
    () =>
      getPreferredScopeConsoleServiceId(
        services,
        scopeBinding?.available ? scopeBinding.serviceId : undefined,
      ),
    [scopeBinding?.available, scopeBinding?.serviceId, services],
  );
  const currentBindingTarget = describeStudioScopeBindingRevisionTarget(
    currentBindingRevision,
  );
  const currentBindingContext = describeStudioScopeBindingRevisionContext(
    currentBindingRevision,
  );
  const observedEvents = invokeResult.events;
  const canInvoke = Boolean(scopeId && selectedService && selectedEndpoint);
  const latestHistoryEntry = requestHistory[0] ?? null;
  const currentRequestStatus =
    invokeResult.status === 'running'
      ? 'running'
      : invokeResult.status === 'success'
        ? 'completed'
        : invokeResult.status === 'error'
          ? 'failed'
          : 'idle';
  const observedErrorCount = useMemo(
    () =>
      invokeResult.status === 'error'
        ? 1
        : invokeResult.steps.filter((step) => step.status === 'error').length,
    [invokeResult.status, invokeResult.steps],
  );

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
      serviceId: selectedServiceId,
      endpointId: selectedEndpointId,
    });
  }, [onSelectionChange, selectedEndpointId, selectedServiceId]);

  useEffect(() => {
    nyxIdChatBoundRef.current = false;
  }, [scopeId]);

  useEffect(() => {
    if (!selectedEndpoint || isChatServiceEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      return;
    }

    setPayloadTypeUrl(selectedEndpoint.requestTypeUrl || '');
  }, [selectedEndpoint]);

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
    setInvokeResult(createIdleResult());
    setActiveTab(isChatEndpoint ? 'chat' : 'output');
  }, [isChatEndpoint, scopeId, selectedEndpointId, selectedServiceId]);

  useEffect(() => {
    setActiveTab(isChatEndpoint ? 'chat' : 'output');
  }, [isChatEndpoint]);

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
      setFocusedHistoryId(nextEntry.id);
      setRequestHistory((current) => [nextEntry, ...current].slice(0, 8));
    },
    [],
  );

  const handleRestoreRequest = useCallback(
    (entry: InvokeHistoryEntry) => {
      setFocusedHistoryId(entry.id);
      setSelectedServiceId(entry.serviceId);
      setSelectedEndpointId(entry.endpointId);
      setPrompt(entry.prompt);
      setPayloadTypeUrl(entry.payloadTypeUrl);
      setPayloadBase64(entry.payloadBase64);
      setActiveTab(entry.mode === 'stream' ? 'chat' : 'output');
    },
    [],
  );

  const handleAbort = useCallback(() => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setInvokeResult((current) => ({
      ...current,
      error: 'Invocation aborted by operator.',
      status: 'error',
    }));
  }, []);

  const handleInvoke = useCallback(async () => {
    if (!scopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    abortControllerRef.current?.abort();
    abortControllerRef.current = null;

    if (isChatServiceEndpoint(selectedEndpoint)) {
      const userMessageId = createClientId('user');
      const assistantMessageId = createClientId('assistant');
      const trimmedPrompt = prompt.trim();
      const controller = new AbortController();
      const accumulator = createRuntimeEventAccumulator();

      abortControllerRef.current = controller;
      setChatMessages((current) => [
        ...current,
        {
          content: trimmedPrompt,
          id: userMessageId,
          role: 'user',
          status: 'complete',
          timestamp: Date.now(),
        },
        {
          content: '',
          id: assistantMessageId,
          role: 'assistant',
          status: 'streaming',
          timestamp: Date.now(),
        },
      ]);
      setPrompt('');
      setActiveTab('chat');
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
            serviceId: selectedService.serviceId,
          },
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          setChatMessages((current) =>
            current.map((message) =>
              message.id === assistantMessageId
                ? {
                    ...message,
                    content: accumulator.assistantText,
                    error: accumulator.errorText || undefined,
                    status: accumulator.errorText ? 'error' : 'streaming',
                    thinking: accumulator.thinking || undefined,
                  }
                : message,
            ),
          );
          setInvokeResult({
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            endpointId: selectedEndpoint.endpointId,
            error: accumulator.errorText,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
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
          setChatMessages((current) =>
            current.map((message) =>
              message.id === assistantMessageId
                ? {
                    ...message,
                    content: accumulator.assistantText,
                    error: accumulator.errorText || undefined,
                    status: accumulator.errorText ? 'error' : 'complete',
                    thinking: accumulator.thinking || undefined,
                  }
                : message,
            ),
          );
          setInvokeResult({
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            endpointId: selectedEndpoint.endpointId,
            error: accumulator.errorText,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
            mode: 'stream',
            responseJson: '',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            status: accumulator.errorText ? 'error' : 'success',
            steps: [...accumulator.steps],
            thinking: accumulator.thinking,
            toolCalls: [...accumulator.toolCalls],
          });
          appendRequestHistory({
            createdAt: Date.now(),
            endpointId: selectedEndpoint.endpointId,
            endpointLabel:
              selectedEndpoint.displayName || selectedEndpoint.endpointId,
            eventCount: accumulator.events.length,
            mode: 'stream',
            payloadBase64: '',
            payloadTypeUrl: '',
            prompt: trimmedPrompt,
            responsePreview:
              accumulator.errorText ||
              accumulator.assistantText ||
              'Model returned an empty response.',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            serviceLabel: selectedService.displayName || selectedService.serviceId,
            status: accumulator.errorText ? 'error' : 'success',
          });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          const message =
            error instanceof Error ? error.message : String(error);
          setChatMessages((current) =>
            current.map((entry) =>
              entry.id === assistantMessageId
                ? {
                    ...entry,
                    content: accumulator.assistantText,
                    error: message,
                    status: 'error',
                    thinking: accumulator.thinking || undefined,
                  }
                : entry,
            ),
          );
          setInvokeResult({
            ...createIdleResult(),
            actorId: accumulator.actorId,
            assistantText: accumulator.assistantText,
            commandId: accumulator.commandId,
            endpointId: selectedEndpoint.endpointId,
            error: message,
            eventCount: accumulator.events.length,
            events: [...accumulator.events],
            mode: 'stream',
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            status: 'error',
            steps: [...accumulator.steps],
            thinking: accumulator.thinking,
            toolCalls: [...accumulator.toolCalls],
          });
          appendRequestHistory({
            createdAt: Date.now(),
            endpointId: selectedEndpoint.endpointId,
            endpointLabel:
              selectedEndpoint.displayName || selectedEndpoint.endpointId,
            eventCount: accumulator.events.length,
            mode: 'stream',
            payloadBase64: '',
            payloadTypeUrl: '',
            prompt: trimmedPrompt,
            responsePreview: message,
            runId: accumulator.runId,
            serviceId: selectedService.serviceId,
            serviceLabel: selectedService.displayName || selectedService.serviceId,
            status: 'error',
          });
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }

      return;
    }

    setInvokeResult({
      ...createIdleResult(),
      endpointId: selectedEndpoint.endpointId,
      mode: 'invoke',
      serviceId: selectedService.serviceId,
      status: 'running',
    });
    setActiveTab('output');

    try {
      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          payloadBase64: payloadBase64.trim() || undefined,
          payloadTypeUrl: payloadTypeUrl.trim() || undefined,
          prompt: prompt.trim(),
        },
        {
          serviceId: selectedService.serviceId,
        },
      );
      const {
        actorId,
        commandId,
        correlationId,
        runId,
      } = extractRuntimeInvokeReceipt(response);
      const events: RuntimeEvent[] = [
        {
          runId: runId || undefined,
          threadId: correlationId || undefined,
          timestamp: Date.now(),
          type: 'RUN_STARTED',
        } as RuntimeEvent,
      ];

      if (actorId || commandId) {
        events.push({
          name: 'RunContext',
          timestamp: Date.now(),
          type: 'CUSTOM',
          value: {
            actorId: actorId || undefined,
            commandId: commandId || undefined,
          },
        } as RuntimeEvent);
      }

      setInvokeResult({
        ...createIdleResult(),
        actorId,
        commandId,
        endpointId: selectedEndpoint.endpointId,
        eventCount: events.length,
        events,
        mode: 'invoke',
        responseJson: JSON.stringify(response, null, 2),
        runId,
        serviceId: selectedService.serviceId,
        status: 'success',
      });
      appendRequestHistory({
        createdAt: Date.now(),
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: selectedEndpoint.displayName || selectedEndpoint.endpointId,
        eventCount: events.length,
        mode: 'invoke',
        payloadBase64: payloadBase64.trim(),
        payloadTypeUrl: payloadTypeUrl.trim(),
        prompt: prompt.trim(),
        responsePreview: trimPreview(JSON.stringify(response, null, 2)),
        runId,
        serviceId: selectedService.serviceId,
        serviceLabel: selectedService.displayName || selectedService.serviceId,
        status: 'success',
      });
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        error: message,
        mode: 'invoke',
        serviceId: selectedService.serviceId,
        status: 'error',
      });
      appendRequestHistory({
        createdAt: Date.now(),
        endpointId: selectedEndpoint.endpointId,
        endpointLabel: selectedEndpoint.displayName || selectedEndpoint.endpointId,
        eventCount: 0,
        mode: 'invoke',
        payloadBase64: payloadBase64.trim(),
        payloadTypeUrl: payloadTypeUrl.trim(),
        prompt: prompt.trim(),
        responsePreview: message,
        runId: '',
        serviceId: selectedService.serviceId,
        serviceLabel: selectedService.displayName || selectedService.serviceId,
        status: 'error',
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

    const currentPrompt = prompt.trim() || latestHistoryEntry?.prompt || '';
    const currentPayloadTypeUrl =
      (!isChatServiceEndpoint(selectedEndpoint) && payloadTypeUrl.trim()) ||
      latestHistoryEntry?.payloadTypeUrl ||
      '';
    const currentPayloadBase64 =
      (!isChatServiceEndpoint(selectedEndpoint) && payloadBase64.trim()) ||
      latestHistoryEntry?.payloadBase64 ||
      '';
    const observedDraftKey =
      observedEvents.length > 0
        ? saveObservedRunSessionPayload({
            actorId: invokeResult.actorId || undefined,
            commandId: invokeResult.commandId || undefined,
            endpointId: invokeResult.endpointId || selectedEndpoint.endpointId,
            events: observedEvents,
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
    invokeResult.actorId,
    invokeResult.commandId,
    invokeResult.endpointId,
    invokeResult.runId,
    latestHistoryEntry?.payloadBase64,
    latestHistoryEntry?.payloadTypeUrl,
    latestHistoryEntry?.prompt,
    observedEvents,
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
    setInvokeResult(createIdleResult());
    setActiveTab(isChatEndpoint ? 'chat' : 'output');
  }, [isChatEndpoint]);

  const serviceOptions = services.map((service) => ({
    label: service.displayName || service.serviceId,
    value: service.serviceId,
  }));
  const endpointOptions = (selectedService?.endpoints ?? []).map((endpoint) => ({
    label: endpoint.displayName || endpoint.endpointId,
    value: endpoint.endpointId,
  }));

  const activeTabItems = useMemo(() => {
    const items: {
      key: InvokeDockTab;
      label: string;
      children: React.ReactNode;
    }[] = [];

    if (isChatEndpoint) {
      items.push({
        key: 'chat',
        label: `Transcript (${chatMessages.length})`,
        children:
          chatMessages.length > 0 ? (
            <div style={transcriptStyle}>
              {chatMessages.map((message) => {
                const isAssistant = message.role === 'assistant';
                return (
                  <div
                    key={message.id}
                    style={{
                      alignItems: isAssistant ? 'flex-start' : 'flex-end',
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 4,
                    }}
                  >
                    <div
                      style={{
                        ...bubbleBaseStyle,
                        background: isAssistant ? '#ffffff' : '#eff6ff',
                        borderColor: isAssistant ? '#e5e7eb' : '#bfdbfe',
                      }}
                    >
                      <div
                        style={{
                          color: '#6b7280',
                          fontSize: 11,
                          fontWeight: 700,
                          textTransform: 'uppercase',
                        }}
                      >
                        {isAssistant ? 'Assistant' : 'You'}
                      </div>
                      <div
                        style={{
                          color: message.error ? '#b91c1c' : '#111827',
                          lineHeight: 1.7,
                          whiteSpace: 'pre-wrap',
                          wordBreak: 'break-word',
                        }}
                      >
                        {message.content || (message.status === 'streaming' ? '...' : '')}
                      </div>
                      {message.thinking ? (
                        <div
                          style={{
                            borderTop: '1px solid #e5e7eb',
                            color: '#6b7280',
                            fontSize: 12,
                            lineHeight: 1.6,
                            paddingTop: 8,
                            whiteSpace: 'pre-wrap',
                          }}
                        >
                          {message.thinking}
                        </div>
                      ) : null}
                    </div>
                  </div>
                );
              })}
              <div ref={transcriptAnchorRef} />
            </div>
          ) : (
            <Empty
              description="No chat content has been observed yet."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ),
      });
    }

    items.push({
      key: 'events',
      label: `Events (${observedEvents.length})`,
      children: observedEvents.length > 0 ? (
        <RuntimeEventPreviewPanel
          events={observedEvents}
          title={`Observed Events (${observedEvents.length})`}
        />
      ) : (
        <Empty
          description="No events have been observed yet."
          image={Empty.PRESENTED_IMAGE_SIMPLE}
        />
      ),
    });

    items.push({
      key: 'output',
      label: 'Output',
      children: invokeResult.responseJson || invokeResult.assistantText ? (
        <pre style={receiptStyle}>
          {invokeResult.responseJson || invokeResult.assistantText}
        </pre>
      ) : (
        <Empty
          description="Run an invocation to inspect the latest receipt."
          image={Empty.PRESENTED_IMAGE_SIMPLE}
        />
      ),
    });

    return items;
  }, [chatMessages, invokeResult.assistantText, invokeResult.responseJson, isChatEndpoint, observedEvents]);

  const latestOutput =
    invokeResult.responseJson ||
    invokeResult.assistantText ||
    latestHistoryEntry?.responsePreview ||
    '';
  const activeSelectionLabel = selectedEndpoint
    ? `${selectedService?.displayName || selectedService?.serviceId || 'service'} / ${
        selectedEndpoint.displayName || selectedEndpoint.endpointId
      }`
    : 'Select a published service endpoint';
  const invokeNotice =
    invokeResult.status !== 'idle' ? (
      <Alert
        showIcon
        message={
          invokeResult.status === 'error'
            ? 'Invocation returned an error.'
            : invokeResult.status === 'success'
              ? 'Invocation completed.'
              : 'Invocation in progress.'
        }
        description={
          invokeResult.error ||
          `${invokeResult.serviceId || selectedServiceId} / ${
            invokeResult.endpointId || selectedEndpointId || 'endpoint'
          }`
        }
        type={
          invokeResult.status === 'error'
            ? 'error'
            : invokeResult.status === 'success'
              ? 'success'
              : 'info'
        }
      />
    ) : null;

  return (
    <div data-testid="studio-member-invoke-panel" style={surfaceStyle}>
      {!scopeId ? (
        <Alert
          showIcon
          message="Resolve a team scope before invoking this member."
          type="info"
        />
      ) : services.length === 0 ? (
        <Alert
          showIcon
          message="No published member services are available in this scope yet."
          description="Finish binding a member revision first, then come back to invoke it here."
          type="warning"
        />
      ) : (
        <>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            title="Invocation Contract"
            titleHelp="Keep the published service, endpoint, and action posture visible while you move from a quick probe into full runtime observation."
            extra={
              <Space wrap size={[8, 8]}>
                <Button
                  size="small"
                  icon={<PlayCircleOutlined />}
                  onClick={() => void handleInvoke()}
                  type="primary"
                  disabled={!canInvoke || invokeResult.status === 'running'}
                >
                  {isChatEndpoint ? 'Start chat' : 'Invoke'}
                </Button>
                <Button
                  size="small"
                  icon={<StopOutlined />}
                  onClick={handleAbort}
                  disabled={invokeResult.status !== 'running'}
                >
                  Abort
                </Button>
                <Button
                  size="small"
                  icon={<LinkOutlined />}
                  onClick={handleOpenRuns}
                  disabled={!scopeId || !selectedEndpoint}
                >
                  Open Runs
                </Button>
                <Button size="small" icon={<ClearOutlined />} onClick={handleClear}>
                  Clear
                </Button>
              </Space>
            }
          >
            <div style={{ display: 'grid', gap: 14 }}>
              {invokeNotice}
              <div style={controlsGridStyle}>
                <div style={{ display: 'grid', gap: 8 }}>
                  <Typography.Text strong>Published service</Typography.Text>
                  <Select
                    placeholder="Select a published service"
                    options={serviceOptions}
                    value={selectedServiceId || undefined}
                    onChange={(value) => {
                      setSelectedServiceId(String(value || ''));
                      setSelectedEndpointId('');
                    }}
                  />
                </div>
                <div style={{ display: 'grid', gap: 8 }}>
                  <Typography.Text strong>Endpoint</Typography.Text>
                  <Select
                    placeholder="Select an endpoint"
                    options={endpointOptions}
                    value={selectedEndpointId || undefined}
                    onChange={(value) => setSelectedEndpointId(String(value || ''))}
                    disabled={!selectedService}
                  />
                </div>
              </div>

              <div style={summaryGridStyle}>
                <div style={{ display: 'grid', gap: 4 }}>
                  <div style={summaryLabelStyle}>Bound Member</div>
                  <div style={summaryValueStyle}>{currentBindingTarget}</div>
                </div>
                <div style={{ display: 'grid', gap: 4 }}>
                  <div style={summaryLabelStyle}>Binding Context</div>
                  <div style={summaryValueStyle}>
                    {currentBindingContext || 'Not configured'}
                  </div>
                </div>
                <div style={{ display: 'grid', gap: 4 }}>
                  <div style={summaryLabelStyle}>Revision</div>
                  <div style={summaryValueStyle}>
                    {currentBindingRevision?.revisionId || 'Not serving yet'}
                  </div>
                </div>
                <div style={{ display: 'grid', gap: 4 }}>
                  <div style={summaryLabelStyle}>Selection</div>
                  <div style={summaryValueStyle}>{activeSelectionLabel}</div>
                </div>
              </div>

              <Space wrap size={[8, 8]}>
                <AevatarStatusTag
                  domain="run"
                  label="request"
                  status={currentRequestStatus}
                />
                <Tag>{isChatEndpoint ? 'chat transcript' : 'typed invoke'}</Tag>
                {!isChatEndpoint && selectedEndpoint?.requestTypeUrl ? (
                  <Tag color="geekblue">
                    Request · {selectedEndpoint.requestTypeUrl}
                  </Tag>
                ) : null}
                {!isChatEndpoint && selectedEndpoint?.responseTypeUrl ? (
                  <Tag>Response · {selectedEndpoint.responseTypeUrl}</Tag>
                ) : null}
              </Space>
              {selectedEndpoint?.description ? (
                <Typography.Text type="secondary">
                  {selectedEndpoint.description}
                </Typography.Text>
              ) : null}
            </div>
          </AevatarPanel>

          <div style={bodyGridStyle}>
            <div style={listColumnStyle}>
              <AevatarPanel
                layoutMode="document"
                padding={14}
                title="Playground"
                titleHelp="Invoke is where full requests live: prompt, typed payload, transcript, events, and the latest receipt stay in one surface."
              >
                <div style={{ display: 'grid', gap: 12 }}>
                  <div style={{ display: 'grid', gap: 8 }}>
                    <Typography.Text strong>
                      {isChatEndpoint ? 'Prompt' : 'Prompt or command input'}
                    </Typography.Text>
                    <Input.TextArea
                      aria-label="Invoke request input"
                      autoSize={{ minRows: 4, maxRows: 7 }}
                      placeholder={
                        isChatEndpoint
                          ? 'Message the selected member...'
                          : 'Optional prompt text. Use payloadBase64 below when the endpoint needs a typed payload.'
                      }
                      value={prompt}
                      onChange={(event) => setPrompt(event.target.value)}
                    />
                  </div>

                  {!isChatEndpoint ? (
                    <div style={controlsGridStyle}>
                      <div style={{ display: 'grid', gap: 8 }}>
                        <Typography.Text strong>Payload Type URL</Typography.Text>
                        <Input
                          placeholder="type.googleapis.com/example.Command"
                          value={payloadTypeUrl}
                          onChange={(event) => setPayloadTypeUrl(event.target.value)}
                        />
                      </div>
                      <div style={{ display: 'grid', gap: 8 }}>
                        <Typography.Text strong>Payload Base64</Typography.Text>
                        <Input.TextArea
                          autoSize={{ minRows: 4, maxRows: 7 }}
                          placeholder="Paste a pre-encoded protobuf payload when needed."
                          value={payloadBase64}
                          onChange={(event) => setPayloadBase64(event.target.value)}
                        />
                      </div>
                    </div>
                  ) : (
                    <Alert
                      showIcon
                      message="Streaming chat endpoint"
                      description="The transcript and AGUI events stay attached to this workbench while the selected member responds."
                      type="info"
                    />
                  )}
                </div>
              </AevatarPanel>

              <AevatarPanel
                layoutMode="document"
                padding={14}
                title="Live Trace"
                titleHelp="Keep transcript, runtime frames, and the latest output together so operators can pivot without leaving the invoke flow."
              >
                <div style={{ display: 'grid', gap: 14 }}>
                  <div style={metricCardsStyle}>
                    <div style={metricCardStyle}>
                      <Typography.Text type="secondary">Run state</Typography.Text>
                      <Typography.Text strong style={summaryValueStyle}>
                        {currentRequestStatus}
                      </Typography.Text>
                    </div>
                    <div style={metricCardStyle}>
                      <Typography.Text type="secondary">Latest run</Typography.Text>
                      <Typography.Text strong style={summaryValueStyle}>
                        {invokeResult.runId || latestHistoryEntry?.runId || 'n/a'}
                      </Typography.Text>
                    </div>
                    <div style={metricCardStyle}>
                      <Typography.Text type="secondary">Observed events</Typography.Text>
                      <Typography.Text strong style={summaryValueStyle}>
                        {String(observedEvents.length)}
                      </Typography.Text>
                    </div>
                    <div style={metricCardStyle}>
                      <Typography.Text type="secondary">Steps / tools</Typography.Text>
                      <Typography.Text strong style={summaryValueStyle}>
                        {invokeResult.steps.length} / {invokeResult.toolCalls.length}
                      </Typography.Text>
                    </div>
                    <div style={metricCardStyle}>
                      <Typography.Text type="secondary">Errors</Typography.Text>
                      <Typography.Text strong style={summaryValueStyle}>
                        {String(observedErrorCount)}
                      </Typography.Text>
                    </div>
                  </div>

                  <Tabs
                    activeKey={activeTab}
                    items={activeTabItems}
                    onChange={(value) => setActiveTab(value as InvokeDockTab)}
                  />
                </div>
              </AevatarPanel>
            </div>

            <div style={sidePanelStyle}>
              <AevatarPanel
                layoutMode="document"
                padding={14}
                title={`Request History (${requestHistory.length})`}
                titleHelp="Keep recent requests nearby so you can quickly replay a payload or jump back to a prior contract without leaving Studio."
              >
                {requestHistory.length > 0 ? (
                  <div style={{ display: 'grid', gap: 10 }}>
                    {requestHistory.map((entry) => {
                      const isFocused = entry.id === focusedHistoryId;
                      return (
                        <button
                          aria-pressed={isFocused}
                          className={AEVATAR_PRESSABLE_CARD_CLASS}
                          key={entry.id}
                          type="button"
                          onClick={() => handleRestoreRequest(entry)}
                          style={{
                            ...historyCardStyle,
                            background: isFocused ? '#f5f7ff' : '#ffffff',
                            borderColor: isFocused ? '#91caff' : '#eef2f7',
                          }}
                        >
                          <div
                            style={{
                              alignItems: 'center',
                              display: 'flex',
                              gap: 8,
                              justifyContent: 'space-between',
                            }}
                          >
                            <Typography.Text strong>
                              {entry.serviceLabel} / {entry.endpointLabel}
                            </Typography.Text>
                            <AevatarStatusTag
                              domain="run"
                              label={entry.mode === 'stream' ? 'chat' : 'invoke'}
                              status={entry.status}
                            />
                          </div>
                          <Typography.Text type="secondary">
                            {trimPreview(entry.prompt || entry.payloadTypeUrl || 'No prompt')}
                          </Typography.Text>
                          <div
                            style={{
                              color: '#6b7280',
                              display: 'flex',
                              flexWrap: 'wrap',
                              fontSize: 12,
                              gap: 8,
                            }}
                          >
                            <span>{formatHistoryTimestamp(entry.createdAt)}</span>
                            {entry.runId ? <span>Run {entry.runId}</span> : null}
                            <span>{entry.eventCount} events</span>
                          </div>
                        </button>
                      );
                    })}
                  </div>
                ) : (
                  <div style={compactEmptyStateStyle}>
                    <Typography.Text strong>No requests yet</Typography.Text>
                    <Typography.Text type="secondary">
                      Start a request and the latest prompt, payload, and receipt will stay here.
                    </Typography.Text>
                  </div>
                )}
              </AevatarPanel>

              <AevatarPanel
                layoutMode="document"
                padding={14}
                title="Current Contract"
                titleHelp="Keep the current endpoint contract, latest runtime identifiers, and serving posture visible while you iterate."
              >
                <div style={{ display: 'grid', gap: 12 }}>
                  <div style={metricRowStyle}>
                    <DeploymentUnitOutlined style={{ color: '#6b7280', marginTop: 2 }} />
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Actor ID</Typography.Text>
                      <div style={summaryValueStyle}>{invokeResult.actorId || 'n/a'}</div>
                    </div>
                  </div>
                  <div style={metricRowStyle}>
                    <CodeOutlined style={{ color: '#6b7280', marginTop: 2 }} />
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Command ID</Typography.Text>
                      <div style={summaryValueStyle}>{invokeResult.commandId || 'n/a'}</div>
                    </div>
                  </div>
                  <div style={metricRowStyle}>
                    <LinkOutlined style={{ color: '#6b7280', marginTop: 2 }} />
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Endpoint</Typography.Text>
                      <div style={summaryValueStyle}>
                        {selectedEndpoint?.displayName || selectedEndpoint?.endpointId || 'n/a'}
                      </div>
                    </div>
                  </div>
                  <div style={metricRowStyle}>
                    <PlayCircleOutlined style={{ color: '#6b7280', marginTop: 2 }} />
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Latest output</Typography.Text>
                      <div style={summaryValueStyle}>
                        {trimPreview(latestOutput || 'No receipt observed yet.')}
                      </div>
                    </div>
                  </div>
                  {selectedEndpoint ? (
                    <>
                      <div
                        style={{
                          borderTop: '1px solid #eef2f7',
                          marginTop: 2,
                          paddingTop: 10,
                        }}
                      >
                        <Typography.Text strong>Endpoint</Typography.Text>
                      </div>
                      <div style={{ display: 'grid', gap: 10 }}>
                        <div style={summaryValueStyle}>
                          {selectedEndpoint.displayName || selectedEndpoint.endpointId}
                        </div>
                        <Typography.Text type="secondary">
                          {selectedEndpoint.description || 'No endpoint description available.'}
                    </Typography.Text>
                    <Space wrap size={[8, 8]}>
                      <Tag>{selectedService?.displayName || selectedService?.serviceId}</Tag>
                      <Tag>
                        revision · {currentBindingRevision?.revisionId || 'not serving'}
                      </Tag>
                    </Space>
                    {!isChatEndpoint && selectedEndpoint.requestTypeUrl ? (
                      <Typography.Text type="secondary">
                        Request: {selectedEndpoint.requestTypeUrl}
                      </Typography.Text>
                    ) : null}
                        {!isChatEndpoint && selectedEndpoint.responseTypeUrl ? (
                          <Typography.Text type="secondary">
                            Response: {selectedEndpoint.responseTypeUrl}
                          </Typography.Text>
                        ) : null}
                      </div>
                    </>
                  ) : (
                    <div style={compactEmptyStateStyle}>
                      <Typography.Text strong>No endpoint selected</Typography.Text>
                      <Typography.Text type="secondary">
                        Pick a published service endpoint to keep its contract details here.
                      </Typography.Text>
                    </div>
                  )}
                </div>
              </AevatarPanel>
            </div>
          </div>
        </>
      )}
    </div>
  );
};

export default StudioMemberInvokePanel;
