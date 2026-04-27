import {
  ClearOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Alert, Button, Grid, Input, Select, Tabs, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
  type RuntimeStepInfo,
  type RuntimeToolCallInfo,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { RuntimeEventPreviewPanel } from '@/shared/agui/runtimeConversationPresentation';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
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
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { AEVATAR_PRESSABLE_CARD_CLASS } from '@/shared/ui/interactionStandards';

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

type InvokeResultState = {
  readonly actorId: string;
  readonly assistantText: string;
  readonly commandId: string;
  readonly endpointId: string;
  readonly error: string;
  readonly eventCount: number;
  readonly events: RuntimeEvent[];
  readonly finalOutput: string;
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

type CurrentRunRequest = {
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly startedAt: number;
};

type InvokeHistoryEntry = {
  readonly completedAt: number;
  readonly createdAt: number;
  readonly endpointId: string;
  readonly endpointLabel: string;
  readonly errorDetail: string;
  readonly eventCount: number;
  readonly id: string;
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly startedAt: number;
  readonly status: 'success' | 'error';
  readonly summary: string;
  readonly snapshot: {
    readonly chatMessages: StudioInvokeChatMessage[];
    readonly result: InvokeResultState;
  };
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

function trimPreview(value: string, limit = 180): string {
  const trimmed = value.trim();
  if (!trimmed) {
    return '';
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

function toIsoTimestamp(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? new Date(value).toISOString()
    : '';
}

function truncateMiddle(value: string, head = 18, tail = 12): string {
  if (value.length <= head + tail + 3) {
    return value;
  }

  return `${value.slice(0, head)}...${value.slice(-tail)}`;
}

function formatHistoryTimestamp(value: number): string {
  if (!Number.isFinite(value) || value <= 0) {
    return '刚刚';
  }

  return new Intl.DateTimeFormat('zh-CN', {
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    month: 'short',
  }).format(value);
}

function formatDuration(startedAt: number, completedAt: number): string {
  if (!Number.isFinite(startedAt) || !Number.isFinite(completedAt)) {
    return '未知';
  }

  const durationMs = Math.max(0, completedAt - startedAt);
  if (durationMs < 1000) {
    return `${durationMs} ms`;
  }

  return `${(durationMs / 1000).toFixed(durationMs >= 10_000 ? 0 : 1)} s`;
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
    finalOutput: '',
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

function cloneInvokeResult(result: InvokeResultState): InvokeResultState {
  return {
    ...result,
    events: [...result.events],
    steps: [...result.steps],
    toolCalls: [...result.toolCalls],
  };
}

function cloneChatMessages(
  messages: readonly StudioInvokeChatMessage[],
): StudioInvokeChatMessage[] {
  return messages.map((message) => ({ ...message }));
}

function getCurrentResultStatusLabel(status: InvokeResultState['status']): string {
  switch (status) {
    case 'running':
      return '运行中';
    case 'success':
      return '成功';
    case 'error':
      return '失败';
    default:
      return '空闲';
  }
}

function getCurrentResultStatusStyle(
  status: InvokeResultState['status'],
): React.CSSProperties {
  if (status === 'running') {
    return {
      background: '#eff6ff',
      border: '1px solid #bfdbfe',
      color: '#1d4ed8',
    };
  }

  if (status === 'success') {
    return {
      background: '#f0fdf4',
      border: '1px solid #86efac',
      color: '#15803d',
    };
  }

  if (status === 'error') {
    return {
      background: '#fef2f2',
      border: '1px solid #fecaca',
      color: '#b91c1c',
    };
  }

  return {
    background: '#f8fafc',
    border: '1px solid #e5e7eb',
    color: '#475569',
  };
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

const surfaceStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
  minWidth: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingBottom: 12,
};

const contractGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
  minWidth: 0,
};

const contractFieldStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const contractLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.4,
  lineHeight: '16px',
  textTransform: 'uppercase',
};

const contractValueStyle: React.CSSProperties = {
  color: '#111827',
  display: 'block',
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  minWidth: 0,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const contractStatusPillBaseStyle: React.CSSProperties = {
  borderRadius: 999,
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '18px',
  padding: '4px 10px',
  width: 'fit-content',
};

const helperTextStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 13,
  lineHeight: 1.6,
  minWidth: 0,
};

const playgroundActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'flex-start',
};

const controlsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
  minWidth: 0,
};

const requestSummaryStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e5e7eb',
  borderRadius: 12,
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const requestSummaryRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const consoleFrameStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  minHeight: 0,
  minWidth: 0,
};

const consolePaneStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minHeight: 320,
  minWidth: 0,
};

const resultSurfaceStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minHeight: 0,
  minWidth: 0,
};

const transcriptStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  maxHeight: 360,
  minHeight: 0,
  minWidth: 0,
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
  minWidth: 0,
  padding: '12px 14px',
};

const plainResultStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 14,
  color: '#111827',
  minWidth: 0,
  padding: '14px 16px',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const rawOutputStyle: React.CSSProperties = {
  background: '#0f172a',
  borderRadius: 14,
  color: '#e2e8f0',
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  maxHeight: 360,
  minHeight: 0,
  minWidth: 0,
  overflow: 'auto',
  padding: 16,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const emptyConsoleTextStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 14,
  lineHeight: 1.7,
  minWidth: 0,
};

const runsListStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  minWidth: 0,
};

const historyCardStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 12,
  cursor: 'pointer',
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
  textAlign: 'left',
  width: '100%',
};

const historyMetaStyle: React.CSSProperties = {
  color: '#6b7280',
  display: 'flex',
  flexWrap: 'wrap',
  fontSize: 12,
  gap: 8,
  minWidth: 0,
};

const inlineDetailStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e5e7eb',
  borderRadius: 12,
  display: 'grid',
  gap: 10,
  minWidth: 0,
  padding: '12px 14px',
};

const detailRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const consoleTabLabelStyle: React.CSSProperties = {
  fontWeight: 600,
};

const CompactCopyableValue: React.FC<{
  readonly fallback?: string;
  readonly value?: string;
}> = ({ fallback = '—', value }) => {
  const normalized = trimOptional(value);
  if (!normalized) {
    return (
      <Typography.Text style={helperTextStyle} type="secondary">
        {fallback}
      </Typography.Text>
    );
  }

  return (
    <Typography.Text copyable={{ text: normalized }} style={contractValueStyle}>
      {truncateMiddle(normalized)}
    </Typography.Text>
  );
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
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [currentRunRequest, setCurrentRunRequest] = useState<CurrentRunRequest | null>(
    null,
  );
  const [chatMessages, setChatMessages] = useState<StudioInvokeChatMessage[]>([]);
  const [requestHistory, setRequestHistory] = useState<InvokeHistoryEntry[]>([]);
  const [expandedHistoryId, setExpandedHistoryId] = useState('');
  const [consoleTab, setConsoleTab] = useState<'result' | 'trace' | 'raw'>(
    'result',
  );
  const [activeRunCompletedAt, setActiveRunCompletedAt] = useState<number | null>(
    null,
  );

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
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
  const currentRunHasData =
    invokeResult.status !== 'idle' ||
    Boolean(currentRunRequest?.prompt) ||
    Boolean(currentRunRequest?.payloadBase64) ||
    Boolean(currentRunRequest?.payloadTypeUrl) ||
    Boolean(invokeResult.runId) ||
    Boolean(invokeResult.commandId) ||
    Boolean(invokeResult.actorId) ||
    Boolean(invokeResult.error) ||
    Boolean(invokeResult.finalOutput) ||
    Boolean(invokeResult.responseJson) ||
    Boolean(invokeResult.assistantText) ||
    chatMessages.length > 0 ||
    invokeResult.events.length > 0;
  const currentObserveSessionSeed = useMemo<StudioObserveSessionSeed | null>(() => {
    const serviceId = trimOptional(selectedServiceId);
    const endpointId = trimOptional(selectedEndpointId);
    if (!serviceId || !endpointId || !currentRunHasData) {
      return null;
    }

    return {
      actorId: trimOptional(invokeResult.actorId),
      assistantText: invokeResult.assistantText,
      commandId: trimOptional(invokeResult.commandId),
      completedAtUtc: toIsoTimestamp(activeRunCompletedAt) || null,
      endpointId,
      error: invokeResult.error,
      events: [...invokeResult.events],
      finalOutput: invokeResult.finalOutput,
      mode: invokeResult.mode,
      payloadBase64:
        currentRunRequest?.payloadBase64 ||
        (!isChatEndpoint ? payloadBase64.trim() : '') ||
        '',
      payloadTypeUrl:
        currentRunRequest?.payloadTypeUrl ||
        (!isChatEndpoint ? payloadTypeUrl.trim() : '') ||
        '',
      prompt: currentRunRequest?.prompt || '',
      runId: trimOptional(invokeResult.runId),
      serviceId,
      serviceLabel:
        trimOptional(selectedService?.displayName) ||
        currentMemberLabel,
      startedAtUtc: toIsoTimestamp(currentRunRequest?.startedAt) || '',
      status: invokeResult.status === 'error' ? 'error' : invokeResult.status === 'success' ? 'success' : 'running',
    };
  }, [
    activeRunCompletedAt,
    currentMemberLabel,
    currentRunHasData,
    currentRunRequest?.payloadBase64,
    currentRunRequest?.payloadTypeUrl,
    currentRunRequest?.prompt,
    currentRunRequest?.startedAt,
    invokeResult.actorId,
    invokeResult.assistantText,
    invokeResult.commandId,
    invokeResult.error,
    invokeResult.events,
    invokeResult.finalOutput,
    invokeResult.mode,
    invokeResult.runId,
    invokeResult.status,
    isChatEndpoint,
    payloadBase64,
    payloadTypeUrl,
    selectedEndpointId,
    selectedService?.displayName,
    selectedServiceId,
  ]);
  const currentResultStatusLabel = getCurrentResultStatusLabel(invokeResult.status);
  const currentContractStatusLabel = getContractStatusLabel({
    hasEndpoint: Boolean(selectedEndpoint),
    hasMember: Boolean(selectedService),
  });
  const currentRawOutput = useMemo(() => {
    if (invokeResult.responseJson) {
      return invokeResult.responseJson;
    }

    if (!currentRunHasData) {
      return '';
    }

    return JSON.stringify(
      {
        actorId: invokeResult.actorId || undefined,
        commandId: invokeResult.commandId || undefined,
        endpointId: invokeResult.endpointId || selectedEndpointId || undefined,
        error: invokeResult.error || undefined,
        eventCount: invokeResult.eventCount || invokeResult.events.length,
        finalOutput: invokeResult.finalOutput || undefined,
        mode: invokeResult.mode || currentRunRequest?.mode,
        runId: invokeResult.runId || undefined,
        serviceId: invokeResult.serviceId || selectedServiceId || undefined,
        status: invokeResult.status,
        stepCount: invokeResult.steps.length,
        toolCallCount: invokeResult.toolCalls.length,
      },
      null,
      2,
    );
  }, [
    currentRunHasData,
    currentRunRequest?.mode,
    invokeResult.actorId,
    invokeResult.commandId,
    invokeResult.endpointId,
    invokeResult.error,
    invokeResult.eventCount,
    invokeResult.events.length,
    invokeResult.finalOutput,
    invokeResult.mode,
    invokeResult.responseJson,
    invokeResult.runId,
    invokeResult.serviceId,
    invokeResult.status,
    invokeResult.steps.length,
    invokeResult.toolCalls.length,
    selectedEndpointId,
    selectedServiceId,
  ]);
  const consoleMinHeight = screens.xl || screens.lg ? 420 : 320;

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
    setCurrentRunRequest(null);
    setExpandedHistoryId('');
    setFormError('');
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
    setConsoleTab('result');
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
    setConsoleTab('result');
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
    setConsoleTab('result');
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
            endpointId: selectedEndpoint.endpointId,
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
            endpointId: selectedEndpoint.endpointId,
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
            endpointId: selectedEndpoint.endpointId,
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
          runId: runId || undefined,
          threadId: correlationId || undefined,
          timestamp: completedAt,
          type: 'RUN_STARTED',
        } as RuntimeEvent,
      ];

      if (actorId || commandId) {
        events.push({
          name: 'RunContext',
          timestamp: completedAt,
          type: 'CUSTOM',
          value: {
            actorId: actorId || undefined,
            commandId: commandId || undefined,
          },
        } as RuntimeEvent);
      }

      const finalResult: InvokeResultState = {
        ...createIdleResult(),
        actorId,
        commandId,
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
    setConsoleTab('result');
    setCurrentRunRequest(null);
    setFormError('');
    setInvokeResult(createIdleResult());
    setActiveRunCompletedAt(null);
  }, []);

  const endpointOptions = (selectedService?.endpoints ?? []).map((endpoint) => ({
    label: endpoint.displayName || endpoint.endpointId,
    value: endpoint.endpointId,
  }));

  const consoleItems = useMemo(
    () => [
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {currentRunHasData ? (
              <div style={resultSurfaceStyle}>
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 10,
                    justifyContent: 'space-between',
                  }}
                >
                  <span
                    style={{
                      ...contractStatusPillBaseStyle,
                      ...getCurrentResultStatusStyle(invokeResult.status),
                    }}
                  >
                    {currentResultStatusLabel}
                  </span>
                  {currentRunRequest?.startedAt ? (
                    <Typography.Text style={helperTextStyle} type="secondary">
                      开始于 {formatHistoryTimestamp(currentRunRequest.startedAt)}
                    </Typography.Text>
                  ) : null}
                </div>

                {currentRunRequest ? (
                  <div style={requestSummaryStyle}>
                    <div style={requestSummaryRowStyle}>
                      <Typography.Text type="secondary">当前输入</Typography.Text>
                      <div style={contractValueStyle}>
                        {currentRunRequest.prompt ||
                          trimPreview(currentRunRequest.payloadTypeUrl, 96) ||
                          '这次调用使用了类型化载荷。'}
                      </div>
                    </div>
                    {!isChatEndpoint &&
                    (currentRunRequest.payloadTypeUrl ||
                      currentRunRequest.payloadBase64) ? (
                      <div style={requestSummaryRowStyle}>
                        {currentRunRequest.payloadTypeUrl ? (
                          <Typography.Text style={helperTextStyle} type="secondary">
                            类型：{currentRunRequest.payloadTypeUrl}
                          </Typography.Text>
                        ) : null}
                        {currentRunRequest.payloadBase64 ? (
                          <Typography.Text style={helperTextStyle} type="secondary">
                            已附带 payloadBase64
                          </Typography.Text>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                ) : null}

                {invokeResult.status === 'error' && invokeResult.error ? (
                  <Alert
                    showIcon
                    message="这次调用失败了。"
                    description={invokeResult.error}
                    type="error"
                  />
                ) : null}

                {chatMessages.length > 0 ? (
                  <div
                    data-testid="studio-invoke-chat-transcript"
                    style={transcriptStyle}
                  >
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
                              {isAssistant ? '成员响应' : '你'}
                            </div>
                            <div
                              style={{
                                color: message.error ? '#b91c1c' : '#111827',
                                lineHeight: 1.7,
                                whiteSpace: 'pre-wrap',
                                wordBreak: 'break-word',
                              }}
                            >
                              {message.content ||
                                (message.status === 'streaming' ? '正在响应…' : '')}
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
                ) : invokeResult.status === 'running' ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    调用已经发出，当前结果会在这里持续更新。
                  </Typography.Text>
                ) : invokeResult.responseJson ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    这次结构化调用已经返回结果。切到“原始”可以查看完整返回体。
                  </Typography.Text>
                ) : invokeResult.finalOutput ? (
                  <div style={plainResultStyle}>{invokeResult.finalOutput}</div>
                ) : invokeResult.assistantText ? (
                  <div style={plainResultStyle}>{invokeResult.assistantText}</div>
                ) : invokeResult.status === 'error' ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    这次调用失败了，没有额外结果文本。
                  </Typography.Text>
                ) : null}
              </div>
            ) : (
              <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                还没有开始调用。先在上方输入提示词或载荷，再发起一次调用。
              </Typography.Text>
            )}
          </div>
        ),
        key: 'result',
        label: <span style={consoleTabLabelStyle}>结果</span>,
      },
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {invokeResult.events.length > 0 ? (
              <RuntimeEventPreviewPanel
                events={invokeResult.events}
                title={`观测事件（${invokeResult.events.length}）`}
              />
            ) : (
              <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                当前 run 还没有可展示的追踪事件。
              </Typography.Text>
            )}

            {(invokeResult.steps.length > 0 || invokeResult.toolCalls.length > 0) && (
              <div style={requestSummaryStyle}>
                <Typography.Text type="secondary">调试概览</Typography.Text>
                <Typography.Text style={contractValueStyle}>
                  步骤 {invokeResult.steps.length} 个，工具调用{' '}
                  {invokeResult.toolCalls.length} 个。
                </Typography.Text>
              </div>
            )}
          </div>
        ),
        key: 'trace',
        label: <span style={consoleTabLabelStyle}>追踪</span>,
      },
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {currentRawOutput ? (
              <pre style={rawOutputStyle}>{currentRawOutput}</pre>
            ) : (
              <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                当前 run 没有额外原始输出。
              </Typography.Text>
            )}
          </div>
        ),
        key: 'raw',
        label: <span style={consoleTabLabelStyle}>原始</span>,
      },
    ],
    [
      consoleMinHeight,
      currentRawOutput,
      currentResultStatusLabel,
      currentRunHasData,
      currentRunRequest,
      invokeResult.assistantText,
      invokeResult.error,
      invokeResult.events,
      invokeResult.finalOutput,
      invokeResult.responseJson,
      invokeResult.status,
      invokeResult.steps.length,
      invokeResult.toolCalls.length,
      isChatEndpoint,
      chatMessages,
    ],
  );

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
            padding={10}
            title="调用契约"
            titleHelp="这里只保留当前调用对象和契约准备状态，不展示运行结果，也不读取输入校验。"
          >
            <div style={contractGridStyle}>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>状态</div>
                <span
                  style={{
                    ...contractStatusPillBaseStyle,
                    ...getCurrentResultStatusStyle(
                      currentContractStatusLabel === '已就绪' ? 'success' : 'idle',
                    ),
                  }}
                >
                  {currentContractStatusLabel}
                </span>
              </div>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>Member</div>
                <div style={contractValueStyle}>{currentMemberLabel}</div>
              </div>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>Endpoint</div>
                <div style={contractValueStyle}>
                  {selectedEndpoint?.displayName || selectedEndpointId || '未选择'}
                </div>
              </div>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>Revision</div>
                <CompactCopyableValue
                  fallback="尚未开始服务"
                  value={currentMemberRevision?.revisionId}
                />
              </div>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>Published Context</div>
                <CompactCopyableValue
                  fallback="尚未配置"
                  value={currentPublishedContext}
                />
              </div>
              <div style={contractFieldStyle}>
                <div style={contractLabelStyle}>Actor ID</div>
                <CompactCopyableValue
                  fallback="尚未分配"
                  value={currentMemberActorId}
                />
              </div>
            </div>
          </AevatarPanel>

          <AevatarPanel
            layoutMode="document"
            padding={14}
            title="调试台"
            titleHelp="先输入 prompt 或载荷，再直接执行当前成员调用。"
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
                <Typography.Text strong>
                  {isChatEndpoint ? '提示词' : '提示词或命令输入'}
                </Typography.Text>
                <Input.TextArea
                  aria-label="调用请求输入"
                  autoSize={{ minRows: 4, maxRows: 8 }}
                  placeholder={
                    isChatEndpoint
                      ? '输入你想发给当前成员的消息...'
                      : '这里可以填写补充提示词；如果端点需要类型化载荷，请在下方填写 payloadBase64。'
                  }
                  value={prompt}
                  onChange={(event) => setPrompt(event.target.value)}
                />
                {formError ? (
                  <Typography.Text type="danger">{formError}</Typography.Text>
                ) : isChatEndpoint ? (
                  <Typography.Text style={helperTextStyle} type="secondary">
                    这是当前成员的对话输入区。开始对话后，结果会直接显示在下方工作台。
                  </Typography.Text>
                ) : null}
              </div>

              {!isChatEndpoint ? (
                <div style={controlsGridStyle}>
                  <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
                    <Typography.Text strong>载荷类型 URL</Typography.Text>
                    <Input
                      placeholder="type.googleapis.com/example.Command"
                      value={payloadTypeUrl}
                      onChange={(event) => setPayloadTypeUrl(event.target.value)}
                    />
                  </div>
                  <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
                    <Typography.Text strong>载荷 Base64</Typography.Text>
                    <Input.TextArea
                      autoSize={{ minRows: 4, maxRows: 8 }}
                      placeholder="如需类型化调用，请粘贴预编码的 protobuf payload。"
                      value={payloadBase64}
                      onChange={(event) => setPayloadBase64(event.target.value)}
                    />
                  </div>
                </div>
              ) : null}

              <div
                data-testid="studio-invoke-playground-actions"
                style={playgroundActionsStyle}
              >
                <Button
                  disabled={!canInvoke || invokeResult.status === 'running'}
                  icon={<PlayCircleOutlined />}
                  onClick={() => void handleInvoke()}
                  type="primary"
                >
                  {isChatEndpoint ? '开始对话' : '执行调用'}
                </Button>
                <Button
                  disabled={invokeResult.status !== 'running'}
                  icon={<StopOutlined />}
                  onClick={handleAbort}
                >
                  中止
                </Button>
                <Button
                  disabled={!scopeId || !selectedEndpoint}
                  icon={<LinkOutlined />}
                  onClick={handleOpenRuns}
                >
                  打开运行记录
                </Button>
                <Button icon={<ClearOutlined />} onClick={handleClear}>
                  清空
                </Button>
              </div>
            </div>
          </AevatarPanel>

          <AevatarPanel
            layoutMode="document"
            padding={14}
            title="当前结果"
            titleHelp="这是唯一的结果展示区。默认看结果，追踪和原始信息作为调试视图延后展示。"
          >
            <div style={consoleFrameStyle}>
              <Tabs
                activeKey={consoleTab}
                items={consoleItems}
                onChange={(value) =>
                  setConsoleTab(value as 'result' | 'trace' | 'raw')
                }
              />
            </div>
          </AevatarPanel>

          {visibleRequestHistory.length > 0 ? (
            <AevatarPanel
              layoutMode="document"
              padding={14}
              title={`Runs（${visibleRequestHistory.length}）`}
              titleHelp="这里只保留历史运行列表和技术详情，不再重复展示结果内容。"
            >
              <div data-testid="studio-invoke-history-scroll" style={runsListStyle}>
                {visibleRequestHistory.map((entry) => {
                  const isExpanded = expandedHistoryId === entry.id;
                  return (
                    <div key={entry.id} style={runsListStyle}>
                      <button
                        aria-expanded={isExpanded}
                        aria-pressed={isExpanded}
                        className={AEVATAR_PRESSABLE_CARD_CLASS}
                        style={{
                          ...historyCardStyle,
                          background: isExpanded ? '#f5f7ff' : '#ffffff',
                          borderColor: isExpanded ? '#91caff' : '#e5e7eb',
                        }}
                        type="button"
                        onClick={() => handleSelectHistoryEntry(entry.id)}
                      >
                        <div
                          style={{
                            alignItems: 'center',
                            display: 'flex',
                            gap: 8,
                            justifyContent: 'space-between',
                            minWidth: 0,
                          }}
                        >
                          <Typography.Text
                            strong
                            style={{ ...contractValueStyle, flex: 1 }}
                          >
                            {trimPreview(entry.prompt || entry.summary, 72)}
                          </Typography.Text>
                          <AevatarStatusTag
                            domain="run"
                            label={entry.status === 'success' ? '成功' : '失败'}
                            status={entry.status}
                          />
                        </div>
                        <div style={historyMetaStyle}>
                          <span>{formatHistoryTimestamp(entry.createdAt)}</span>
                          <span>{entry.eventCount} 个事件</span>
                          <span>{entry.endpointLabel}</span>
                        </div>
                      </button>

                      {isExpanded ? (
                        <div
                          data-testid="studio-invoke-inline-detail"
                          style={inlineDetailStyle}
                        >
                          {entry.snapshot.result.commandId ? (
                            <div style={detailRowStyle}>
                              <Typography.Text type="secondary">
                                Command ID
                              </Typography.Text>
                              <CompactCopyableValue
                                value={entry.snapshot.result.commandId}
                              />
                            </div>
                          ) : null}
                          {entry.snapshot.result.actorId ? (
                            <div style={detailRowStyle}>
                              <Typography.Text type="secondary">
                                Actor ID
                              </Typography.Text>
                              <CompactCopyableValue
                                value={entry.snapshot.result.actorId}
                              />
                            </div>
                          ) : null}
                          {(entry.runId || entry.snapshot.result.runId) ? (
                            <div style={detailRowStyle}>
                              <Typography.Text type="secondary">
                                Metadata
                              </Typography.Text>
                              <CompactCopyableValue
                                value={entry.runId || entry.snapshot.result.runId}
                              />
                            </div>
                          ) : null}
                          <div style={detailRowStyle}>
                            <Typography.Text type="secondary">
                              Duration
                            </Typography.Text>
                            <div style={contractValueStyle}>
                              {formatDuration(entry.startedAt, entry.completedAt)}
                            </div>
                          </div>
                          <div style={detailRowStyle}>
                            <Typography.Text type="secondary">
                              Timestamps
                            </Typography.Text>
                            <div style={helperTextStyle}>
                              开始：{formatHistoryTimestamp(entry.startedAt)}
                            </div>
                            <div style={helperTextStyle}>
                              完成：{formatHistoryTimestamp(entry.completedAt)}
                            </div>
                          </div>
                          {entry.errorDetail ? (
                            <div style={detailRowStyle}>
                              <Typography.Text type="secondary">
                                Error Detail
                              </Typography.Text>
                              <div style={contractValueStyle}>{entry.errorDetail}</div>
                            </div>
                          ) : null}
                        </div>
                      ) : null}
                    </div>
                  );
                })}
              </div>
            </AevatarPanel>
          ) : null}
        </>
      )}
    </div>
  );
};

export default StudioMemberInvokePanel;
