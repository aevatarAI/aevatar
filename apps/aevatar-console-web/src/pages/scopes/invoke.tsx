import {
  AGUIEventType,
  CustomEventName,
} from '@aevatar-react-sdk/types';
import {
  ApiOutlined,
  AppstoreOutlined,
  ArrowLeftOutlined,
  ClearOutlined,
  CodeOutlined,
  DeploymentUnitOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
  UnorderedListOutlined,
} from '@ant-design/icons';
import { ProCard } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Empty, Input, Select, Space, Typography } from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  RuntimeEventPreviewPanel,
} from '@/shared/agui/runtimeConversationPresentation';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  type RuntimeEvent,
  type RuntimeStepInfo,
  type RuntimeToolCallInfo,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { history } from '@/shared/navigation/history';
import { buildTeamWorkspaceRoute } from '@/shared/navigation/scopeRoutes';
import {
  buildRuntimeGAgentsHref,
  buildRuntimeRunsHref,
} from '@/shared/navigation/runtimeRoutes';
import { saveObservedRunSessionPayload } from '@/shared/runs/draftRunSession';
import { studioApi } from '@/shared/studio/api';
import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from '@/shared/models/services';
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  getStudioScopeBindingCurrentRevision,
} from '@/shared/studio/models';
import { buildStudioWorkflowWorkspaceRoute } from '@/shared/studio/navigation';
import {
  AevatarContextDrawer,
  AevatarHelpTooltip,
  AevatarPageShell,
  AevatarStatusTag,
} from '@/shared/ui/aevatarPageShells';
import {
  ChatInput,
  ChatMessageBubble,
  ChatMetaStrip,
  EmptyChatState,
} from '../chat/chatPresentation';
import type { ChatMessage } from '../chat/chatTypes';
import ScopeServiceRuntimeWorkbench from './components/ScopeServiceRuntimeWorkbench';
import { resolveStudioScopeContext } from './components/resolvedScope';
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from './components/scopeQuery';

type InvokeResultState = {
  actorId: string;
  assistantText: string;
  commandId: string;
  endpointId: string;
  error: string;
  eventCount: number;
  events: RuntimeEvent[];
  mode: 'stream' | 'invoke';
  responseJson: string;
  runId: string;
  serviceId: string;
  status: 'idle' | 'running' | 'success' | 'error';
  steps: RuntimeStepInfo[];
  thinking: string;
  toolCalls: RuntimeToolCallInfo[];
};

type InvokeDockTab = 'chat' | 'events' | 'output';

type InvokeContextSurface = 'service' | null;

type MonacoEditorComponentProps = {
  ariaLabel?: string;
  defaultLanguage?: string;
  height?: number | string;
  language?: string;
  onChange?: (value: string | undefined) => void;
  options?: Record<string, unknown>;
  path?: string;
  placeholder?: string;
  theme?: string;
  value?: string;
};

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = 'default';
const scopeServiceNamespace = 'default';
const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";
const viewportShellStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-layout)',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  height: 'calc(100vh - 64px)',
  maxHeight: 'calc(100vh - 64px)',
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};
const pageHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'var(--ant-color-bg-container)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  display: 'flex',
  flex: '0 0 auto',
  gap: 12,
  justifyContent: 'space-between',
  padding: '10px 14px',
  position: 'sticky',
  top: 0,
  zIndex: 3,
};
const workspaceViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: '1 1 auto',
  minHeight: 0,
  overflow: 'hidden',
};
const workspaceGridStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'grid',
  gap: 12,
  gridTemplateColumns: '250px minmax(0, 1fr) 300px',
  height: '100%',
  minHeight: 0,
  minWidth: 0,
  width: '100%',
};
const columnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
};
const viewportCardStyle: React.CSSProperties = {
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  boxShadow: 'none',
  overflow: 'hidden',
};
const viewportFillCardStyle: React.CSSProperties = {
  ...viewportCardStyle,
  height: '100%',
  minHeight: 0,
};
const viewportGrowCardStyle: React.CSSProperties = {
  ...viewportCardStyle,
  flex: '1 1 0',
  minHeight: 0,
};
const viewportCardBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  padding: 12,
};
const scrollColumnStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingRight: 4,
};
const sectionStyle: React.CSSProperties = {
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
  padding: 12,
};
const fieldLabelStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 12,
  fontWeight: 500,
};
const fieldValueStyle: React.CSSProperties = {
  color: 'var(--ant-color-text)',
  fontSize: 13,
  fontWeight: 600,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};
const metricRowStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'grid',
  gap: 8,
  gridTemplateColumns: '16px minmax(0, 1fr)',
};
const editorShellStyle: React.CSSProperties = {
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 280,
  overflow: 'hidden',
  position: 'relative',
};
const editorViewportStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  minHeight: 0,
  overflow: 'hidden',
};
const editorFallbackStyle: React.CSSProperties = {
  background: 'transparent',
  border: 0,
  color: 'var(--ant-color-text)',
  fontFamily: monoFontFamily,
  fontSize: 13,
  height: '100%',
  lineHeight: 1.6,
  outline: 'none',
  padding: '14px 16px 84px',
  resize: 'none',
  width: '100%',
};
const editorActionBarStyle: React.CSSProperties = {
  bottom: 16,
  position: 'absolute',
  right: 16,
  zIndex: 2,
};
const playgroundChatShellStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  minHeight: 0,
};
const playgroundChatMessagesStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 20,
  margin: '0 auto',
  maxWidth: 840,
  minHeight: 0,
  overflow: 'auto',
  padding: '20px 20px 16px',
  width: '100%',
};
const playgroundChatComposerStyle: React.CSSProperties = {
  background: '#ffffff',
  borderTop: '1px solid #e7e5e4',
  flexShrink: 0,
  padding: '14px 20px 18px',
};
const consoleShellStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-container)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  boxShadow: 'none',
  flex: '0 0 auto',
  minHeight: 0,
  overflow: 'hidden',
};
const consoleBodyStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
  padding: 12,
};
const codeBlockStyle: React.CSSProperties = {
  background: 'var(--ant-color-fill-quaternary)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 6,
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  overflow: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

function readQueryValue(name: string): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get(name)?.trim() ?? '';
}

function createClientId(): string {
  return globalThis.crypto?.randomUUID?.()
    ? globalThis.crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function compareServicePriority(
  left: ServiceCatalogSnapshot,
  right: ServiceCatalogSnapshot,
  defaultServiceId?: string,
): number {
  const leftIsDefault = left.serviceId === defaultServiceId ? 1 : 0;
  const rightIsDefault = right.serviceId === defaultServiceId ? 1 : 0;

  if (leftIsDefault !== rightIsDefault) {
    return rightIsDefault - leftIsDefault;
  }

  const endpointDelta = right.endpoints.length - left.endpoints.length;
  if (endpointDelta !== 0) {
    return endpointDelta;
  }

  const leftHasDisplayName = left.displayName.trim() ? 1 : 0;
  const rightHasDisplayName = right.displayName.trim() ? 1 : 0;
  if (leftHasDisplayName !== rightHasDisplayName) {
    return rightHasDisplayName - leftHasDisplayName;
  }

  const updatedAtDelta = right.updatedAt.localeCompare(left.updatedAt);
  if (updatedAtDelta !== 0) {
    return updatedAtDelta;
  }

  const serviceIdDelta = left.serviceId.localeCompare(right.serviceId);
  if (serviceIdDelta !== 0) {
    return serviceIdDelta;
  }

  return left.serviceKey.localeCompare(right.serviceKey);
}

export function buildServiceOptions(
  services: readonly ServiceCatalogSnapshot[],
  defaultServiceId?: string,
): ServiceCatalogSnapshot[] {
  const deduped = new Map<string, ServiceCatalogSnapshot>();

  services.forEach((service) => {
    const current = deduped.get(service.serviceId);
    if (!current) {
      deduped.set(service.serviceId, service);
      return;
    }

    if (compareServicePriority(service, current, defaultServiceId) < 0) {
      deduped.set(service.serviceId, service);
    }
  });

  return [...deduped.values()].sort((left, right) =>
    compareServicePriority(left, right, defaultServiceId),
  );
}

function isChatEndpoint(
  endpoint: ServiceEndpointSnapshot | undefined,
): boolean {
  if (!endpoint) {
    return false;
  }

  return endpoint.kind === 'chat' || endpoint.endpointId.trim() === 'chat';
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

function getEventKey(event: RuntimeEvent, indexHint: number): string {
  const candidate = event as unknown as Record<string, unknown>;
  return [
    event.type,
    String(candidate.timestamp ?? ''),
    String(candidate.runId ?? ''),
    String(candidate.messageId ?? ''),
    String(indexHint),
  ].join('-');
}

function resolveMonacoEditorComponent(): React.ComponentType<MonacoEditorComponentProps> | null {
  if (typeof window === 'undefined') {
    return null;
  }

  if (/jsdom/i.test(window.navigator.userAgent)) {
    return null;
  }

  try {
    const module = require('@monaco-editor/react') as {
      default: React.ComponentType<MonacoEditorComponentProps>;
    };
    return module.default;
  } catch {
    return null;
  }
}

const ScopeInvokePage: React.FC = () => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const previousChatBindingKeyRef = useRef('');
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedServiceId, setSelectedServiceId] = useState(
    readQueryValue('serviceId'),
  );
  const [selectedEndpointId, setSelectedEndpointId] = useState(
    readQueryValue('endpointId'),
  );
  const [prompt, setPrompt] = useState('');
  const [payloadTypeUrl, setPayloadTypeUrl] = useState('');
  const [payloadBase64, setPayloadBase64] = useState('');
  const [preserveEmptySelection, setPreserveEmptySelection] = useState(false);
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [chatMessages, setChatMessages] = useState<ChatMessage[]>([]);
  const [contextSurface, setContextSurface] =
    useState<InvokeContextSurface>(null);
  const [dockTab, setDockTab] = useState<InvokeDockTab>('events');

  const authSessionQuery = useQuery({
    queryKey: ['scopes', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);

  useEffect(() => {
    history.replace(
      buildScopeHref('/scopes/invoke', activeDraft, {
        endpointId: selectedEndpointId,
        serviceId: selectedServiceId,
      }),
    );
  }, [activeDraft, selectedEndpointId, selectedServiceId]);

  useEffect(() => () => abortControllerRef.current?.abort(), []);

  useEffect(() => {
    scrollAnchorRef.current?.scrollIntoView?.({
      behavior: chatMessages.length > 1 ? 'smooth' : 'auto',
      block: 'end',
    });
  }, [chatMessages]);

  const scopeId = activeDraft.scopeId.trim();
  const bindingQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ['scopes', 'binding', scopeId],
    queryFn: () => studioApi.getScopeBinding(scopeId),
  });
  const scopeServicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ['scopes', 'invoke', 'services', scopeId],
    queryFn: () =>
      servicesApi.listServices({
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
        tenantId: scopeId,
      }),
  });

  const services = useMemo(
    () =>
      buildServiceOptions(
        scopeServicesQuery.data ?? [],
        bindingQuery.data?.available ? bindingQuery.data.serviceId : undefined,
      ),
    [
      bindingQuery.data?.available,
      bindingQuery.data?.serviceId,
      scopeServicesQuery.data,
    ],
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

    if (preserveEmptySelection) {
      setSelectedServiceId('');
      return;
    }

    setSelectedServiceId(
      services.find(
        (service) => service.serviceId === bindingQuery.data?.serviceId,
      )?.serviceId ||
        services[0]?.serviceId ||
        '',
    );
  }, [
    bindingQuery.data?.serviceId,
    preserveEmptySelection,
    selectedServiceId,
    services,
  ]);

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;

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

    setSelectedEndpointId(
      selectedService.endpoints.find(
        (endpoint) => endpoint.endpointId === 'chat',
      )?.endpointId ||
        selectedService.endpoints[0]?.endpointId ||
        '',
    );
  }, [selectedEndpointId, selectedService]);

  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const isChatPlayground = Boolean(
    selectedEndpoint && isChatEndpoint(selectedEndpoint),
  );
  const currentBindingRevision = getStudioScopeBindingCurrentRevision(
    bindingQuery.data,
  );
  const currentBindingTarget = describeStudioScopeBindingRevisionTarget(
    currentBindingRevision,
  );
  const currentBindingContext = describeStudioScopeBindingRevisionContext(
    currentBindingRevision,
  );
  const currentBindingActor =
    currentBindingRevision?.primaryActorId ||
    bindingQuery.data?.primaryActorId ||
    '';

  useEffect(() => {
    if (!selectedEndpoint || isChatEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      return;
    }

    setPayloadTypeUrl(selectedEndpoint.requestTypeUrl || '');
  }, [selectedEndpoint]);

  useEffect(() => {
    const bindingKey = `${scopeId}::${selectedServiceId}::${selectedEndpointId}`;
    if (!previousChatBindingKeyRef.current) {
      previousChatBindingKeyRef.current = bindingKey;
      return;
    }

    if (previousChatBindingKeyRef.current === bindingKey) {
      return;
    }

    previousChatBindingKeyRef.current = bindingKey;
    setChatMessages([]);
    setInvokeResult(createIdleResult());
  }, [scopeId, selectedEndpointId, selectedServiceId]);

  useEffect(() => {
    setDockTab(isChatPlayground ? 'chat' : 'output');
  }, [isChatPlayground]);

  const serviceOptions = services.map((service) => ({
    label: service.displayName || service.serviceId,
    value: service.serviceId,
  }));
  const endpointOptions = (selectedService?.endpoints ?? []).map(
    (endpoint) => ({
      label: endpoint.displayName || endpoint.endpointId,
      value: endpoint.endpointId,
    }),
  );

  const handleAbort = () => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setInvokeResult((current) => ({
      ...current,
      error: 'Invocation aborted by operator.',
      status: 'error',
    }));
  };

  const handleInvoke = async () => {
    if (!scopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    abortControllerRef.current?.abort();
    abortControllerRef.current = null;

    if (isChatEndpoint(selectedEndpoint)) {
      const trimmedPrompt = prompt.trim();
      const userMessageId = createClientId();
      const assistantMessageId = createClientId();
      const userMessage: ChatMessage = {
        content: trimmedPrompt,
        id: userMessageId,
        role: 'user',
        status: 'complete',
        timestamp: Date.now(),
      };
      const assistantMessage: ChatMessage = {
        content: '',
        events: [],
        id: assistantMessageId,
        role: 'assistant',
        status: 'streaming',
        steps: [],
        thinking: '',
        timestamp: Date.now(),
        toolCalls: [],
      };
      const controller = new AbortController();
      abortControllerRef.current = controller;
      const accumulator = createRuntimeEventAccumulator();
      setChatMessages((current) => [...current, userMessage, assistantMessage]);
      setDockTab('chat');
      setPrompt('');
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        mode: 'stream',
        serviceId: selectedService.serviceId,
        status: 'running',
      });

      try {
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
            prompt: prompt.trim(),
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
                    events: [...accumulator.events],
                    status: accumulator.errorText ? 'error' : 'streaming',
                    steps: [...accumulator.steps],
                    thinking: accumulator.thinking,
                    toolCalls: [...accumulator.toolCalls],
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
                    events: [...accumulator.events],
                    status: accumulator.errorText ? 'error' : 'complete',
                    steps: [...accumulator.steps],
                    thinking: accumulator.thinking,
                    toolCalls: [...accumulator.toolCalls],
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
                    events: [...accumulator.events],
                    status: 'error',
                    steps: [...accumulator.steps],
                    thinking: accumulator.thinking,
                    toolCalls: [...accumulator.toolCalls],
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
    setDockTab('output');

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
      const responseRunId = String(
        response.request_id ?? response.requestId ?? response.commandId ?? '',
      ).trim();
      const responseActorId = String(
        response.target_actor_id ??
          response.targetActorId ??
          response.actorId ??
          '',
      ).trim();
      const responseCommandId = String(
        response.command_id ?? response.commandId ?? responseRunId,
      ).trim();
      const events: RuntimeEvent[] = [
        {
          runId: responseRunId || undefined,
          threadId:
            String(
              response.correlation_id ??
                response.correlationId ??
                responseRunId,
            ).trim() || undefined,
          timestamp: Date.now(),
          type: AGUIEventType.RUN_STARTED,
        } as RuntimeEvent,
      ];

      if (responseActorId || responseCommandId) {
        events.push({
          name: CustomEventName.RunContext,
          timestamp: Date.now(),
          type: AGUIEventType.CUSTOM,
          value: {
            actorId: responseActorId || undefined,
            commandId: responseCommandId || undefined,
          },
        } as RuntimeEvent);
      }

      setInvokeResult({
        ...createIdleResult(),
        actorId: responseActorId,
        commandId: responseCommandId,
        endpointId: selectedEndpoint.endpointId,
        eventCount: events.length,
        events,
        mode: 'invoke',
        responseJson: JSON.stringify(response, null, 2),
        runId: responseRunId,
        serviceId: selectedService.serviceId,
        status: 'success',
      });
    } catch (error) {
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        error: error instanceof Error ? error.message : String(error),
        mode: 'invoke',
        serviceId: selectedService.serviceId,
        status: 'error',
      });
    }
  };

  const handleOpenRuns = () => {
    if (!scopeId) {
      return;
    }

    const observedDraftKey =
      invokeResult.events.length > 0
        ? saveObservedRunSessionPayload({
            actorId: invokeResult.actorId || undefined,
            commandId: invokeResult.commandId || undefined,
            endpointId:
              invokeResult.endpointId || selectedEndpoint?.endpointId || 'chat',
            events: invokeResult.events,
            payloadBase64:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadBase64 || undefined
                : undefined,
            payloadTypeUrl:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadTypeUrl || undefined
                : undefined,
            prompt,
            runId: invokeResult.runId || undefined,
            scopeId,
            serviceOverrideId: selectedService?.serviceId,
          })
        : '';

    history.push(
      buildRuntimeRunsHref({
        actorId: invokeResult.actorId || undefined,
        draftKey: observedDraftKey || undefined,
        endpointId: selectedEndpoint?.endpointId,
        payloadTypeUrl:
          selectedEndpoint && !isChatEndpoint(selectedEndpoint)
            ? payloadTypeUrl || undefined
            : undefined,
        prompt: prompt || undefined,
        scopeId,
        serviceId: selectedService?.serviceId,
      }),
    );
  };

  const handleReset = () => {
    const nextDraft = normalizeScopeDraft({
      scopeId: resolvedScope?.scopeId ?? '',
    });
    setPreserveEmptySelection(true);
    setDraft(nextDraft);
    setActiveDraft(nextDraft);
    setSelectedServiceId('');
    setSelectedEndpointId('');
    setPrompt('');
    setPayloadTypeUrl('');
    setPayloadBase64('');
    setChatMessages([]);
    setInvokeResult(createIdleResult());
  };

  const handleLoad = () => {
    const nextDraft = normalizeScopeDraft(draft);
    setPreserveEmptySelection(false);
    setDraft(nextDraft);
    setActiveDraft(nextDraft);
  };

  const handleUseResolvedScope = () => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    const nextDraft = normalizeScopeDraft({
      scopeId: resolvedScope.scopeId,
    });
    setPreserveEmptySelection(false);
    setDraft(nextDraft);
    setActiveDraft(nextDraft);
  };

  const handleClearConsole = () => {
    setChatMessages([]);
    setInvokeResult(createIdleResult());
    setDockTab(isChatPlayground ? 'chat' : 'output');
  };

  const recommendedNextStep = !scopeId
      ? {
        action: () => history.push(buildTeamWorkspaceRoute('')),
        actionLabel: 'Open Team Home',
        description:
          'This legacy lab only becomes useful after you anchor the console to a team.',
        title: 'Load a team first',
      }
    : services.length === 0
      ? {
          action: () =>
            history.push(
              buildStudioWorkflowWorkspaceRoute({
                scopeId,
              }),
            ),
          actionLabel: 'Open Team Builder',
          description:
            'No published team services were discovered. Switch the live team setup before you keep probing this legacy lab.',
          title: 'Fix the live team setup',
        }
      : invokeResult.status === 'success'
        ? {
            action: handleOpenRuns,
            actionLabel: 'Continue in Runs',
            description:
              'Promote the current session into runtime observation and keep the trace attached.',
            title: 'Promote this session',
          }
        : {
            action: () => setContextSurface('service'),
            actionLabel: 'Browse services',
            description:
              'Inspect the published catalog when you need deeper runtime context.',
            title: 'Open the service workbench',
          };

  const outputPanels = useMemo(
    () =>
      [
        invokeResult.responseJson
          ? {
              title: 'Invocation Receipt',
              value: invokeResult.responseJson,
            }
          : null,
      ].filter((panel): panel is { title: string; value: string } =>
        Boolean(panel),
      ),
    [invokeResult.responseJson],
  );

  const bindingStatus =
    bindingQuery.data?.deploymentStatus ||
    (bindingQuery.data?.available ? 'ready' : 'missing');

  return (
    <AevatarPageShell pageHeaderRender={false} title="Legacy Invoke Lab">
      <div style={viewportShellStyle}>
        <style>
          {`
            @keyframes pulse {
              0%, 100% { opacity: 1; }
              50% { opacity: 0.45; }
            }
            @keyframes blink {
              50% { opacity: 0; }
            }
            @keyframes bounce {
              0%, 80%, 100% { transform: translateY(0); opacity: 0.7; }
              40% { transform: translateY(-3px); opacity: 1; }
            }
          `}
        </style>
        <div style={pageHeaderStyle}>
          <Space size={12}>
            <Button
              icon={<ArrowLeftOutlined />}
              onClick={() => history.push(buildTeamWorkspaceRoute(activeDraft.scopeId))}
              type="text"
            >
              Team Home
            </Button>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <div
                style={{
                  alignItems: 'center',
                  display: 'inline-flex',
                  flexWrap: 'wrap',
                  gap: 6,
                  maxWidth: '100%',
                }}
              >
                <Typography.Text strong style={{ fontSize: 16 }}>
                  Legacy Invoke Lab
                </Typography.Text>
                <AevatarHelpTooltip content="Legacy deep-link playground for direct endpoint probing. Team home stays the primary surface, while this lab handles raw payloads and older operator flows." />
              </div>
            </div>
          </Space>
          <Space size={[8, 8]} wrap>
            <AevatarStatusTag domain="run" status={invokeResult.status} />
            {scopeId ? (
              <Typography.Text code style={{ marginInlineStart: 4 }}>
                {scopeId}
              </Typography.Text>
            ) : null}
            <Button onClick={() => history.push(buildTeamWorkspaceRoute(activeDraft.scopeId))}>
              Open Team Home
            </Button>
            <Button
              onClick={() =>
                history.push(
                  buildStudioWorkflowWorkspaceRoute({
                    scopeId: activeDraft.scopeId.trim(),
                  }),
                )
              }
            >
              Open Team Builder
            </Button>
            <Button onClick={() => setContextSurface('service')}>
              Browse services
            </Button>
            {invokeResult.status === 'success' ? (
              <Button onClick={handleOpenRuns} type="primary">
                Continue in Runs
              </Button>
            ) : null}
          </Space>
        </div>

        <div data-testid="invoke-lab-workspace-viewport" style={workspaceViewportStyle}>
          <div data-testid="invoke-lab-workspace-grid" style={workspaceGridStyle}>
            <div style={columnStyle}>
              {scopeId ? (
                <Alert
                  description="Team home is now the primary surface. Use this legacy lab when you need direct endpoint probes, raw payload testing, or older deep links."
                  showIcon
                  type="warning"
                />
              ) : null}
              <ProCard
                bodyStyle={viewportCardBodyStyle}
                boxShadow={false}
                headerBordered
                style={viewportCardStyle}
                title={
                  <PaneTitle
                    icon={<DeploymentUnitOutlined />}
                    subtitle="Team selector and reset controls for the legacy lab."
                    title="Legacy Lab Controls"
                  />
                }
              >
                <div style={scrollColumnStyle}>
                  <div style={sectionStyle}>
                    <Typography.Text style={fieldLabelStyle}>
                      Team ID
                    </Typography.Text>
                    <Input
                      allowClear
                      placeholder="Enter team ID"
                      value={draft.scopeId}
                      onChange={(event) =>
                        setDraft({
                          scopeId: event.target.value,
                        })
                      }
                      onPressEnter={handleLoad}
                    />
                    <Space size={[8, 8]} wrap>
                      <Button type="primary" onClick={handleLoad}>
                        Load legacy lab
                      </Button>
                      <Button onClick={handleReset}>Reset</Button>
                    </Space>
                  </div>

                  <div style={sectionStyle}>
                    <Typography.Text style={fieldLabelStyle}>
                      Resolved team
                    </Typography.Text>
                    {resolvedScope?.scopeId ? (
                      <>
                        <Typography.Paragraph copyable style={codeBlockStyle}>
                          {resolvedScope.scopeId}
                        </Typography.Paragraph>
                        <Typography.Text type="secondary">
                          Resolved from the current session via{' '}
                          {resolvedScope.scopeSource || 'session'}.
                        </Typography.Text>
                        {draft.scopeId.trim() !== resolvedScope.scopeId ? (
                          <div>
                            <Button size="small" onClick={handleUseResolvedScope}>
                              Use resolved team
                            </Button>
                          </div>
                        ) : null}
                      </>
                    ) : (
                      <Typography.Text type="secondary">
                        No team context was resolved from the current session.
                      </Typography.Text>
                    )}
                  </div>
                </div>
              </ProCard>

              <ProCard
                bodyStyle={viewportCardBodyStyle}
                boxShadow={false}
                headerBordered
                style={viewportGrowCardStyle}
                title={
                  <PaneTitle
                    icon={<LinkOutlined />}
                    subtitle="Published binding visible beside the playground."
                    title="Current Binding"
                  />
                }
              >
                {!bindingQuery.data?.available || !currentBindingRevision ? (
                  <Alert
                    showIcon
                    title="No published default binding is active for this team yet."
                    type="info"
                  />
                ) : (
                  <div style={scrollColumnStyle}>
                    <div style={sectionStyle}>
                      <Space size={[8, 8]} wrap>
                        <AevatarStatusTag
                          domain="governance"
                          status={bindingStatus}
                        />
                        <Typography.Text strong>
                          {bindingQuery.data.displayName ||
                            bindingQuery.data.serviceId}
                        </Typography.Text>
                      </Space>
                      <MetricRow
                        icon={<LinkOutlined />}
                        label="Target"
                        value={currentBindingTarget}
                      />
                      <MetricRow
                        icon={<CodeOutlined />}
                        label="Revision"
                        value={currentBindingRevision.revisionId}
                      />
                      <MetricRow
                        icon={<DeploymentUnitOutlined />}
                        label="Actor"
                        value={currentBindingActor || 'n/a'}
                      />
                      {currentBindingContext ? (
                        <Typography.Text type="secondary">
                          {currentBindingContext}
                        </Typography.Text>
                      ) : null}
                      <Button
                        onClick={() =>
                          history.push(
                            buildRuntimeGAgentsHref({
                              scopeId,
                              actorId:
                                currentBindingRevision.primaryActorId ||
                                undefined,
                              actorTypeName:
                                currentBindingRevision.staticActorTypeName ||
                                undefined,
                            }),
                          )
                        }
                      >
                        Open Member Runtime
                      </Button>
                    </div>
                  </div>
                )}
              </ProCard>
            </div>

            <ProCard
              bodyStyle={viewportCardBodyStyle}
              boxShadow={false}
              headerBordered
              style={viewportFillCardStyle}
              title={
                <PaneTitle
                  icon={<CodeOutlined />}
                  subtitle="Prompt and payload staging surface."
                  title="Playground"
                />
              }
            >
              <div
                style={{
                  display: 'flex',
                  flex: 1,
                  flexDirection: 'column',
                  gap: 12,
                  minHeight: 0,
                }}
              >
                <Space size={[8, 8]} wrap>
                  <AevatarStatusTag domain="run" status={invokeResult.status} />
                  {selectedService ? (
                    <Typography.Text type="secondary">
                      {selectedService.displayName || selectedService.serviceId}
                    </Typography.Text>
                  ) : (
                    <Typography.Text type="secondary">
                      No service selected
                    </Typography.Text>
                  )}
                  {selectedEndpoint ? (
                    <Typography.Text type="secondary">
                      /{' '}
                      {selectedEndpoint.displayName ||
                        selectedEndpoint.endpointId}
                    </Typography.Text>
                  ) : null}
                </Space>

                {isChatPlayground ? (
                  <div style={playgroundChatShellStyle}>
                    <InvokeLabConsole
                      activeTab={dockTab}
                      chatPanel={
                        <div style={playgroundChatMessagesStyle}>
                          {!scopeId ? (
                            <Alert
                              showIcon
                              title="Select a team to start chatting with a published service."
                              type="info"
                            />
                          ) : !selectedService || !selectedEndpoint ? (
                            <Alert
                              showIcon
                              title="Choose a published service and chat endpoint first."
                              type="info"
                            />
                          ) : chatMessages.length === 0 ? (
                            <EmptyChatState
                              description={`Chat with ${selectedService.displayName || selectedService.serviceId} through the legacy lab while keeping raw runtime observation close by.`}
                              title={
                                selectedService.displayName ||
                                selectedService.serviceId
                              }
                            />
                          ) : (
                            chatMessages.map((message) => (
                              <ChatMessageBubble
                                key={message.id}
                                message={message}
                              />
                            ))
                          )}
                          <div ref={scrollAnchorRef} />
                        </div>
                      }
                      events={invokeResult.events}
                      fillHeight
                      hasChatContent={chatMessages.length > 0}
                      onClear={handleClearConsole}
                      onTabChange={setDockTab}
                      outputPanels={outputPanels}
                    />

                    <div style={playgroundChatComposerStyle}>
                      <div style={{ margin: '0 auto', maxWidth: 840 }}>
                        <ChatInput
                          disabled={!scopeId || !selectedService || !selectedEndpoint}
                          isStreaming={invokeResult.status === 'running'}
                          onChange={setPrompt}
                          onSend={() => void handleInvoke()}
                          onStop={handleAbort}
                          value={prompt}
                        />
                        <ChatMetaStrip
                          actorId={invokeResult.actorId || undefined}
                          commandId={invokeResult.commandId || undefined}
                          runId={invokeResult.runId || undefined}
                          scopeId={scopeId || undefined}
                          serviceId={selectedService?.serviceId}
                        />
                      </div>
                    </div>
                  </div>
                ) : (
                  <div style={{ display: 'flex', flex: 1, flexDirection: 'column', gap: 12, minHeight: 0 }}>
                    <div style={editorShellStyle}>
                      <div
                        style={{
                          alignItems: 'center',
                          borderBottom: '1px solid var(--ant-color-border-secondary)',
                          display: 'flex',
                          justifyContent: 'space-between',
                          padding: '10px 12px',
                        }}
                      >
                        <div
                          style={{ display: 'flex', flexDirection: 'column', gap: 2 }}
                        >
                          <Typography.Text strong>
                            Prompt / Payload
                          </Typography.Text>
                          <Typography.Text type="secondary">
                            Use the editor for operator input. Advanced typed payload
                            stays in the inspector.
                          </Typography.Text>
                        </div>
                      </div>
                      <div style={editorViewportStyle}>
                        <InvokeCodeEditor
                          ariaLabel="Prompt or payload editor"
                          language="plaintext"
                          onChange={setPrompt}
                          placeholder="Prompt or payload text"
                          value={prompt}
                        />
                      </div>
                      <div style={editorActionBarStyle}>
                        <Space size={[8, 8]} wrap>
                          <Button
                            aria-label="Invoke endpoint"
                            disabled={!selectedEndpointId}
                            icon={<PlayCircleOutlined />}
                            loading={invokeResult.status === 'running'}
                            onClick={() => void handleInvoke()}
                            type="primary"
                          >
                            Invoke endpoint
                          </Button>
                          <Button
                            aria-label="Abort"
                            danger
                            disabled={invokeResult.status !== 'running'}
                            icon={<StopOutlined />}
                            onClick={handleAbort}
                          >
                            Abort
                          </Button>
                        </Space>
                      </div>
                    </div>

                    <InvokeLabConsole
                      activeTab={dockTab}
                      chatPanel={null}
                      events={invokeResult.events}
                      hasChatContent={false}
                      onClear={handleClearConsole}
                      onTabChange={setDockTab}
                      outputPanels={outputPanels}
                    />
                  </div>
                )}
              </div>
            </ProCard>

            <ProCard
              bodyStyle={viewportCardBodyStyle}
              boxShadow={false}
              headerBordered
              style={viewportFillCardStyle}
              title={
                <PaneTitle
                  icon={<AppstoreOutlined />}
                  subtitle="Service, endpoint, and execution context."
                  title="Inspector"
                />
              }
            >
              <div style={scrollColumnStyle}>
                <div style={sectionStyle}>
                  <SectionTitle
                    icon={<AppstoreOutlined />}
                    title="Select Service"
                  />
                  <Select
                    onChange={(value) => {
                      setPreserveEmptySelection(false);
                      setSelectedServiceId(value);
                    }}
                    options={serviceOptions}
                    placeholder="Select published service"
                    value={selectedServiceId || undefined}
                  />
                </div>

                <div style={sectionStyle}>
                  <SectionTitle icon={<ApiOutlined />} title="Select Endpoint" />
                  <Select
                    onChange={setSelectedEndpointId}
                    options={endpointOptions}
                    placeholder="Select endpoint"
                    value={selectedEndpointId || undefined}
                  />
                  {selectedEndpoint && !isChatEndpoint(selectedEndpoint) ? (
                    <>
                      <Typography.Text style={fieldLabelStyle}>
                        Payload Type URL
                      </Typography.Text>
                      <Input
                        onChange={(event) =>
                          setPayloadTypeUrl(event.target.value)
                        }
                        placeholder="Payload type URL"
                        value={payloadTypeUrl}
                      />
                      <Typography.Text style={fieldLabelStyle}>
                        Payload Base64
                      </Typography.Text>
                      <Input.TextArea
                        onChange={(event) => setPayloadBase64(event.target.value)}
                        placeholder="Payload base64"
                        rows={5}
                        style={{ fontFamily: monoFontFamily }}
                        value={payloadBase64}
                      />
                    </>
                  ) : null}
                </div>

                <div style={sectionStyle}>
                  <SectionTitle
                    icon={<DeploymentUnitOutlined />}
                    title="Execution Preview"
                  />
                  {!scopeId ? (
                    <Alert
                      showIcon
                      title="Select a team to load its published services."
                      type="info"
                    />
                  ) : !selectedService ? (
                    <Alert
                      showIcon
                      title="No published team service is selected yet."
                      type="warning"
                    />
                  ) : (
                    <>
                      {invokeResult.status !== 'idle' ? (
                        <Alert
                          description={
                            invokeResult.error ||
                            (invokeResult.status === 'running'
                              ? 'Invocation in progress.'
                              : 'Invocation completed.')
                          }
                          showIcon
                          title={`${
                            invokeResult.mode === 'stream' ? 'Streaming' : 'Invoke'
                          } · ${invokeResult.serviceId || selectedService.serviceId} / ${
                            invokeResult.endpointId ||
                            selectedEndpointId ||
                            'endpoint'
                          }`}
                          type={
                            invokeResult.status === 'error'
                              ? 'error'
                              : invokeResult.status === 'success'
                                ? 'success'
                                : 'info'
                          }
                        />
                      ) : null}
                      <MetricRow
                        icon={<AppstoreOutlined />}
                        label="Service"
                        value={
                          selectedService.displayName || selectedService.serviceId
                        }
                      />
                      <MetricRow
                        icon={<ApiOutlined />}
                        label="Endpoint"
                        value={selectedEndpoint?.endpointId || 'n/a'}
                      />
                      <MetricRow
                        icon={<CodeOutlined />}
                        label="Run ID"
                        value={invokeResult.runId || 'n/a'}
                      />
                      <MetricRow
                        icon={<DeploymentUnitOutlined />}
                        label="Actor ID"
                        value={invokeResult.actorId || 'n/a'}
                      />
                      <div
                        style={{
                          borderTop:
                            '1px solid var(--ant-color-border-secondary)',
                          paddingTop: 10,
                        }}
                      >
                        <Typography.Text strong>
                          {recommendedNextStep.title}
                        </Typography.Text>
                        <Typography.Paragraph
                          style={{ marginBottom: 10, marginTop: 6 }}
                          type="secondary"
                        >
                          {recommendedNextStep.description}
                        </Typography.Paragraph>
                        <Space size={[8, 8]} wrap>
                          <Button
                            aria-label={
                              recommendedNextStep.actionLabel ===
                              'Continue in Runs'
                                ? 'Continue in Runs from lab console'
                                : recommendedNextStep.actionLabel
                            }
                            onClick={recommendedNextStep.action}
                          >
                            {recommendedNextStep.actionLabel}
                          </Button>
                          <Typography.Text type="secondary">
                            {invokeResult.eventCount} observed events
                          </Typography.Text>
                        </Space>
                      </div>
                    </>
                  )}
                </div>
              </div>
            </ProCard>
          </div>
        </div>

        {contextSurface === 'service' ? (
          <AevatarContextDrawer
            onClose={() => setContextSurface(null)}
            open
            subtitle={
              selectedService
                ? `${selectedService.namespace}/${selectedService.serviceId}`
                : 'Published service'
            }
            title={
              selectedService?.displayName ||
              selectedService?.serviceId ||
              'Service'
            }
          >
            <ScopeServiceRuntimeWorkbench
              onSelectService={(serviceId) => {
                setPreserveEmptySelection(false);
                setSelectedServiceId(serviceId);
              }}
              onUseEndpoint={(serviceId, endpointId) => {
                setPreserveEmptySelection(false);
                setSelectedServiceId(serviceId);
                setSelectedEndpointId(endpointId);
              }}
              scopeId={scopeId}
              selectedEndpointId={selectedEndpointId}
              selectedServiceId={selectedServiceId}
              services={services}
            />
          </AevatarContextDrawer>
        ) : null}
      </div>
    </AevatarPageShell>
  );
};

const PaneTitle: React.FC<{
  icon: React.ReactNode;
  subtitle: string;
  title: string;
}> = ({ icon, subtitle, title }) => (
  <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
    <Space size={8}>
      <span style={{ color: 'var(--ant-color-text-secondary)' }}>{icon}</span>
      <Typography.Text strong>{title}</Typography.Text>
    </Space>
    <Typography.Text type="secondary">{subtitle}</Typography.Text>
  </div>
);

const SectionTitle: React.FC<{
  icon: React.ReactNode;
  title: string;
}> = ({ icon, title }) => (
  <Space size={8}>
    <span style={{ color: 'var(--ant-color-text-secondary)' }}>{icon}</span>
    <Typography.Text strong>{title}</Typography.Text>
  </Space>
);

const MetricRow: React.FC<{
  icon: React.ReactNode;
  label: string;
  value: React.ReactNode;
}> = ({ icon, label, value }) => (
  <div style={metricRowStyle}>
    <span style={{ color: 'var(--ant-color-text-secondary)', marginTop: 2 }}>
      {icon}
    </span>
    <div
      style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}
    >
      <Typography.Text style={fieldLabelStyle}>{label}</Typography.Text>
      <Typography.Text style={fieldValueStyle}>{value}</Typography.Text>
    </div>
  </div>
);

const InvokeCodeEditor: React.FC<{
  ariaLabel: string;
  language: string;
  onChange: (value: string) => void;
  placeholder: string;
  value: string;
}> = ({ ariaLabel, language, onChange, placeholder, value }) => {
  const MonacoEditor = useMemo(() => resolveMonacoEditorComponent(), []);

  if (!MonacoEditor) {
    return (
      <textarea
        aria-label={ariaLabel}
        placeholder={placeholder}
        style={editorFallbackStyle}
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
    );
  }

  return (
    <MonacoEditor
      ariaLabel={ariaLabel}
      defaultLanguage={language}
      height="100%"
      language={language}
      onChange={(nextValue) => onChange(nextValue ?? '')}
      options={{
        automaticLayout: true,
        fontFamily: monoFontFamily,
        fontLigatures: false,
        fontSize: 13,
        glyphMargin: false,
        lineNumbers: 'on',
        minimap: { enabled: false },
        padding: { top: 14, bottom: 84 },
        renderLineHighlight: 'line',
        roundedSelection: false,
        scrollBeyondLastLine: false,
        smoothScrolling: true,
        wordWrap: 'on',
      }}
      path={`file:///invoke-lab/${language}.txt`}
      placeholder={placeholder}
      theme="vs"
      value={value}
    />
  );
};

const CodePanel: React.FC<{
  title: string;
  value: string;
}> = ({ title, value }) => (
  <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
    <Space size={8}>
      <CodeOutlined />
      <Typography.Text strong>{title}</Typography.Text>
    </Space>
    <pre style={codeBlockStyle}>{value}</pre>
  </div>
);

const InvokeLabConsole: React.FC<{
  activeTab: InvokeDockTab;
  chatPanel: React.ReactNode | null;
  events: RuntimeEvent[];
  fillHeight?: boolean;
  hasChatContent: boolean;
  onClear: () => void;
  onTabChange: (tab: InvokeDockTab) => void;
  outputPanels: { title: string; value: string }[];
}> = ({
  activeTab,
  chatPanel,
  events,
  fillHeight,
  hasChatContent,
  onClear,
  onTabChange,
  outputPanels,
}) => (
  <ProCard
    bodyStyle={{
      display: 'flex',
      flexDirection: 'column',
      height: '100%',
      minHeight: 0,
      overflow: 'hidden',
      padding: 0,
    }}
    boxShadow={false}
    headerBordered={false}
    style={fillHeight ? { ...consoleShellStyle, flex: '1 1 0' } : consoleShellStyle}
  >
    <div
      style={{
        alignItems: 'center',
        borderBottom: '1px solid var(--ant-color-border-secondary)',
        display: 'flex',
        gap: 12,
        justifyContent: 'space-between',
        padding: '10px 12px',
      }}
    >
      <Space size={[8, 8]} wrap>
        <Typography.Text strong>Lab Console</Typography.Text>
        {chatPanel ? (
          <Button
            aria-label="Chat"
            icon={<CodeOutlined />}
            onClick={() => onTabChange('chat')}
            type={activeTab === 'chat' ? 'primary' : 'default'}
          >
            Chat
          </Button>
        ) : null}
        <Button
          aria-label="Observed Events"
          icon={<UnorderedListOutlined />}
          onClick={() => onTabChange('events')}
          type={activeTab === 'events' ? 'primary' : 'default'}
        >
          Observed Events
        </Button>
        {!chatPanel || outputPanels.length > 0 ? (
          <Button
            aria-label="Output"
            icon={<CodeOutlined />}
            onClick={() => onTabChange('output')}
            type={activeTab === 'output' ? 'primary' : 'default'}
          >
            Output
          </Button>
        ) : null}
      </Space>
      <Space size={[8, 8]} wrap>
        <Button
          disabled={!hasChatContent && events.length === 0 && outputPanels.length === 0}
          icon={<ClearOutlined />}
          onClick={onClear}
        >
          Clear
        </Button>
      </Space>
    </div>

    <div style={consoleBodyStyle}>
      {activeTab === 'chat' ? (
        chatPanel ?? (
          <Empty
            description="No chat content has been observed yet."
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        )
      ) : activeTab === 'events' ? (
        events.length > 0 ? (
          <div style={scrollColumnStyle}>
            <RuntimeEventPreviewPanel
              events={events}
              title={`Observed Events (${events.length})`}
            />
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <Typography.Text type="secondary">
                Latest raw payloads
              </Typography.Text>
              {events.slice(-6).map((event, index) => (
                <div
                  key={getEventKey(event, index)}
                  style={{
                    border: '1px solid var(--ant-color-border-secondary)',
                    borderRadius: 6,
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 8,
                    padding: 10,
                  }}
                >
                  <Space size={[8, 8]} wrap>
                    <AevatarStatusTag domain="observation" status="streaming" />
                    <Typography.Text strong>{event.type}</Typography.Text>
                  </Space>
                  <pre style={codeBlockStyle}>
                    {JSON.stringify(event, null, 2)}
                  </pre>
                </div>
              ))}
            </div>
          </div>
        ) : (
          <Empty
            description="No events have been observed yet."
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        )
      ) : outputPanels.length > 0 ? (
        <div style={scrollColumnStyle}>
          {outputPanels.map((panel) => (
            <CodePanel
              key={panel.title}
              title={panel.title}
              value={panel.value}
            />
          ))}
        </div>
      ) : (
        <Empty
          description="Invocation output will appear here after you stream or invoke a service."
          image={Empty.PRESENTED_IMAGE_SIMPLE}
        />
      )}
    </div>
  </ProCard>
);

export default ScopeInvokePage;
