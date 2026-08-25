import {
  DeleteOutlined,
  HistoryOutlined,
  MessageOutlined,
  PlusOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Modal,
  Space,
  Spin,
  Tag,
  Tooltip,
  Typography,
  theme,
} from 'antd';
import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { isNyxIdChatWireInspectorEnabled } from '@/shared/config/consoleFeatures';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from '@/shared/navigation/teamRoutes';
import { NyxIDAuthClient } from '@/shared/auth/client';
import { getNyxIDRuntimeConfig } from '@/shared/auth/config';
import { studioApi } from '@/shared/studio/api';
import { AevatarPageShell } from '@/shared/ui/aevatarPageShells';
import { resolveStudioScopeContext } from '../scopes/components/resolvedScope';
import { type ChatActionJourney, ChatActorControls } from './ChatActorControls';
import {
  actorCan,
  applyCurrentStateResult,
  type ChatActorProjection,
  type ChatActorStep,
  type ChatPendingInput,
  type ChatNyxIdActionRequest,
  chatActionIdentityKey,
  createChatActorProjection,
  decodeActorFrame,
  reduceActorFrame,
} from './chatActorState';
import {
  type ChatActionResource,
  type ChatCommand,
  type ChatInputAnswer,
  extractChatStreamArtifacts,
  readChatStreamFrames,
  sendChatCommand,
} from './chatApi';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from './chatEventAdapter';
import { chatHistoryApi } from './chatHistoryApi';
import { ChatInput, ChatMessageBubble } from './chatPresentation';
import type { ChatPlanGate } from './chatTaskPlan';
import type {
  ChatMessage,
  ChatStudioTarget,
  ChatUsageSummary,
  ConversationMeta,
  LocalChatStatus,
  StepInfo,
  StoredChatMessage,
  ToolCallInfo,
} from './chatTypes';
import {
  buildNyxIdConnectUrl,
  createNyxIdCatalogKey,
  listNyxIdConnectors,
  matchingUserServiceIds,
  matchNewUserServiceId,
} from './nyxIdServiceApi';

type ConversationState = {
  clientId: string;
  conversationId?: string;
  expectedTurnCount: number;
  latestTurnId?: string;
  messages: ChatMessage[];
  status: LocalChatStatus;
  target?: ChatStudioTarget;
  title: string;
  usage?: ChatUsageSummary;
};

type ConversationListItem = ConversationMeta & {
  liveStatus?: LocalChatStatus;
};

type DetailLoadState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { message: string; status: 'error' };

type Notice = {
  message: string;
  type: 'error' | 'info' | 'success' | 'warning';
};

type StudioJump = { href: string; label: string };

type ActionDisposition =
  | 'completed'
  | 'declined'
  | 'failed'
  | 'cancelled'
  | 'expired';

type ChatControlCommand = Exclude<ChatCommand, { type: 'text' }>;

const ACTIVE_STATE_REFRESH_DELAYS_MS = [250, 500, 1_000, 2_000] as const;

function readChatQueryValue(
  key: string,
  search = typeof window === 'undefined' ? '' : window.location.search,
): string {
  return new URLSearchParams(search).get(key)?.trim() ?? '';
}

function createClientId(): string {
  return (
    globalThis.crypto?.randomUUID?.() ??
    `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
  );
}

function createDraftConversation(): ConversationState {
  return {
    clientId: createClientId(),
    expectedTurnCount: 0,
    messages: [],
    status: 'draft',
    title: t('pages.chat.index.newChat', 'New chat'),
  };
}

export function hydrateStoredMessages(
  messages: readonly StoredChatMessage[],
): ChatMessage[] {
  return messages.map((message) => ({
    authorId: message.authorId,
    authorName: message.authorName,
    content: message.content,
    error: message.error || undefined,
    id: message.id,
    role: message.role,
    status: message.error?.trim() ? 'error' : message.status,
    thinking: message.thinking || undefined,
    timestamp: message.timestamp,
  }));
}

function resolveStoredConversationStatus(
  messages: readonly StoredChatMessage[],
): LocalChatStatus {
  const terminal =
    [...messages].reverse().find((message) => message.role === 'assistant') ??
    messages.at(-1);
  return terminal?.status === 'error' || Boolean(terminal?.error?.trim())
    ? 'error'
    : 'completed_text';
}

function ChatMessageEntry({
  message,
}: {
  message: ChatMessage;
}): React.ReactElement {
  const authorName = message.authorName?.trim() || '';
  if (message.role === 'user' || message.role === 'assistant') {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        {authorName ? (
          <Typography.Text
            style={{
              alignSelf: message.role === 'user' ? 'flex-end' : 'flex-start',
              color: '#6b7280',
              fontSize: 11,
              marginLeft: message.role === 'assistant' ? 34 : 0,
            }}
          >
            {authorName}
          </Typography.Text>
        ) : null}
        <ChatMessageBubble message={message} />
      </div>
    );
  }

  const roleLabel =
    message.role.trim() || t('pages.chat.index.unknownRole', 'Message');
  const displayName = authorName || roleLabel;
  return (
    <article
      aria-label={`${displayName} ${roleLabel} message`}
      style={{ display: 'flex', gap: 10 }}
    >
      <MessageOutlined
        style={{
          background: '#f3f4f6',
          border: '1px solid #e5e7eb',
          borderRadius: 999,
          color: '#4b5563',
          fontSize: 12,
          height: 24,
          marginTop: 3,
          textAlign: 'center',
          width: 24,
        }}
      />
      <div style={{ flex: 1, maxWidth: '82%', minWidth: 0 }}>
        <Space align="center" size={6} wrap>
          <Typography.Text strong style={{ fontSize: 12 }}>
            {displayName}
          </Typography.Text>
          {authorName ? <Tag>{roleLabel}</Tag> : null}
        </Space>
        {message.thinking ? (
          <Typography.Paragraph
            style={{ color: '#6b7280', fontSize: 12, margin: '6px 0' }}
          >
            {message.thinking}
          </Typography.Paragraph>
        ) : null}
        {message.content ? (
          <div
            style={{
              color: '#1f2937',
              fontSize: 14,
              lineHeight: 1.65,
              whiteSpace: 'pre-wrap',
              wordBreak: 'break-word',
            }}
          >
            {message.content}
          </div>
        ) : null}
        {message.status === 'error' && message.error ? (
          <Alert
            description={message.error}
            showIcon
            style={{ marginTop: 8 }}
            type="error"
          />
        ) : null}
      </div>
    </article>
  );
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function createChatMessage(
  role: ChatMessage['role'],
  content: string,
  status: ChatMessage['status'] = 'complete',
): ChatMessage {
  return {
    content,
    id: createClientId(),
    role,
    status,
    timestamp: Date.now(),
  };
}

function cloneStepInfo(steps?: readonly StepInfo[]): StepInfo[] {
  return (steps ?? []).map((step) => ({ ...step }));
}

function cloneToolCallInfo(
  toolCalls?: readonly ToolCallInfo[],
): ToolCallInfo[] {
  return (toolCalls ?? []).map((toolCall) => ({ ...toolCall }));
}

function buildAssistantMessagePatch(
  accumulator: ReturnType<typeof createRuntimeEventAccumulator>,
  status: ChatMessage['status'],
): Partial<ChatMessage> {
  return {
    content: accumulator.finalOutput || accumulator.assistantText,
    error: accumulator.errorText || undefined,
    events: [...accumulator.events],
    pendingApproval: accumulator.pendingApproval
      ? { ...accumulator.pendingApproval }
      : undefined,
    pendingRunIntervention: accumulator.pendingRunIntervention
      ? { ...accumulator.pendingRunIntervention }
      : undefined,
    status,
    steps: cloneStepInfo(accumulator.steps),
    thinking: accumulator.thinking,
    toolCalls: cloneToolCallInfo(accumulator.toolCalls),
  };
}

function trimTitle(value: string): string {
  const normalized = value.trim().replace(/\s+/g, ' ');
  if (!normalized) return t('pages.chat.index.newChat', 'New chat');
  return normalized.length > 60 ? `${normalized.slice(0, 57)}...` : normalized;
}

function formatStatusLabel(status: LocalChatStatus): string {
  switch (status) {
    case 'streaming':
      return t('pages.chat.index.status.streaming', 'Streaming');
    case 'completed_with_studio_target':
      return t('pages.chat.index.status.studioReady', 'Studio ready');
    case 'completed_text':
      return t('pages.chat.index.status.completed', 'Completed');
    case 'error':
      return t('pages.chat.index.status.error', 'Error');
    default:
      return t('pages.chat.index.status.draft', 'Draft');
  }
}

function resolveStatusTone(
  status: LocalChatStatus,
): 'default' | 'processing' | 'success' | 'error' {
  if (status === 'streaming') return 'processing';
  if (
    status === 'completed_text' ||
    status === 'completed_with_studio_target'
  ) {
    return 'success';
  }
  return status === 'error' ? 'error' : 'default';
}

function resolveStudioJump(
  target: ChatStudioTarget | undefined,
): StudioJump | null {
  if (!target) return null;
  if (target.studioUrl) {
    return {
      href: target.studioUrl,
      label: t('pages.chat.index.openWorkflowStudio', 'Open Workflow Studio'),
    };
  }
  if (target.scopeId && target.teamId && target.memberId) {
    return {
      href: buildTeamMemberWorkflowStudioHref({
        memberId: target.memberId,
        mode: 'edit-member',
        scopeId: target.scopeId,
        teamId: target.teamId,
        workflowId: target.workflowId,
      }),
      label: t('pages.chat.index.openWorkflowStudio', 'Open Workflow Studio'),
    };
  }
  if (target.scopeId && target.teamId) {
    return {
      href: buildTeamDetailHref({
        scopeId: target.scopeId,
        tab: 'members',
        teamId: target.teamId,
      }),
      label: t('pages.chat.index.openTeam', 'Open Team'),
    };
  }
  return null;
}

function hasUsage(usage: ChatUsageSummary | undefined): boolean {
  return Boolean(
    usage &&
      (usage.totalTokens ||
        usage.promptTokens ||
        usage.completionTokens ||
        usage.model ||
        usage.cost ||
        usage.latencyMs),
  );
}

function formatRelativeTime(isoString: string): string {
  const timestamp = Date.parse(isoString);
  if (!Number.isFinite(timestamp)) return '';
  const minutes = Math.floor((Date.now() - timestamp) / 60_000);
  if (minutes < 1) return t('pages.chat.index.time.justNow', 'just now');
  if (minutes < 60) {
    return t('pages.chat.index.time.minutesAgo', '{count}m ago', {
      count: minutes,
    });
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return t('pages.chat.index.time.hoursAgo', '{count}h ago', {
      count: hours,
    });
  }
  return t('pages.chat.index.time.daysAgo', '{count}d ago', {
    count: Math.floor(hours / 24),
  });
}

function formatTurnCount(count: number): string {
  return t('pages.chat.index.turnCount', '{count} turns', { count });
}

function stringField(
  record: Record<string, unknown> | null | undefined,
  key: string,
): string {
  const value = record?.[key];
  return typeof value === 'string' ? value : '';
}

function actionReportResource(userServiceId: string): ChatActionResource {
  return { userService: { userServiceId } };
}

const ChatPage: React.FC = () => {
  const { token } = theme.useToken();
  const queryClient = useQueryClient();
  const activeConversationRef = useRef<ConversationState | null>(null);
  const projectionRef = useRef<ChatActorProjection | null>(null);
  const streamControllerRef = useRef<AbortController | null>(null);
  const controlControllerRef = useRef<AbortController | null>(null);
  const detailRequestRef = useRef('');
  const scopeIdentityRef = useRef('');
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const [activeConversation, setActiveConversation] =
    useState<ConversationState | null>(null);
  const [projection, setProjection] = useState<ChatActorProjection | null>(
    null,
  );
  const [actionJourneys, setActionJourneys] = useState<
    ReadonlyMap<string, ChatActionJourney>
  >(() => new Map());
  const [deleteTarget, setDeleteTarget] = useState<ConversationMeta | null>(
    null,
  );
  const [deletingConversation, setDeletingConversation] = useState(false);
  const [deleteError, setDeleteError] = useState('');
  const [detailLoadState, setDetailLoadState] = useState<DetailLoadState>({
    status: 'idle',
  });
  const [historyDrawerOpen, setHistoryDrawerOpen] = useState(false);
  const [prompt, setPrompt] = useState('');
  const [notice, setNotice] = useState<Notice | null>(null);
  const [controlBusy, setControlBusy] = useState(false);
  const [diagnosticWire, setDiagnosticWire] = useState<unknown>(undefined);
  const wireInspectorEnabled = isNyxIdChatWireInspectorEnabled();

  const authSessionQuery = useQuery({
    queryKey: ['chat', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const routeSearch =
    typeof window === 'undefined' ? '' : window.location.search;
  const routeScopeId = useMemo(
    () => readChatQueryValue('scopeId', routeSearch),
    [routeSearch],
  );
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );
  const authenticatedScopeId = resolvedScope?.scopeId.trim() || '';
  const scopeMismatch = Boolean(
    authSessionQuery.isSuccess &&
      routeScopeId &&
      authenticatedScopeId &&
      routeScopeId !== authenticatedScopeId,
  );
  const scopeId =
    authSessionQuery.isSuccess && !scopeMismatch ? authenticatedScopeId : '';
  const canStartChat = Boolean(
    authSessionQuery.isSuccess &&
      authSessionQuery.data?.enabled === true &&
      authSessionQuery.data.authenticated === true &&
      scopeId,
  );
  const chatCreationUnavailable = Boolean(
    authSessionQuery.isSuccess && authSessionQuery.data?.enabled === false,
  );
  const conversationsQuery = useQuery({
    enabled: canStartChat,
    queryFn: () => chatHistoryApi.listConversationMetas(),
    queryKey: ['chat-conversations', scopeId],
    retry: false,
  });
  const conversations = conversationsQuery.data ?? [];
  const isStreaming = activeConversation?.status === 'streaming';
  const actorHasActiveWork = Boolean(
    projection?.activeTurn?.status === 'active' ||
      projection?.task?.status === 'active',
  );
  const visibleConversations = useMemo<ConversationListItem[]>(() => {
    const serverItems = conversations.map((conversation) => ({
      ...conversation,
      ...(conversation.id === activeConversation?.conversationId
        ? { liveStatus: activeConversation.status }
        : {}),
    }));
    if (
      !activeConversation?.conversationId ||
      conversations.some(
        (item) => item.id === activeConversation.conversationId,
      )
    ) {
      return serverItems;
    }
    const timestamps = activeConversation.messages.map(
      (message) => message.timestamp,
    );
    return [
      {
        createdAt: new Date(timestamps[0] ?? Date.now()).toISOString(),
        id: activeConversation.conversationId,
        liveStatus: activeConversation.status,
        messageCount: activeConversation.expectedTurnCount,
        title: activeConversation.title,
        updatedAt: new Date(timestamps.at(-1) ?? Date.now()).toISOString(),
      },
      ...serverItems,
    ];
  }, [activeConversation, conversations]);
  const studioJump = resolveStudioJump(activeConversation?.target);
  const isConversationActionDisabled =
    !canStartChat || detailLoadState.status === 'loading' || controlBusy;

  const applyProjection = useCallback((next: ChatActorProjection | null) => {
    projectionRef.current = next;
    setProjection(next);
  }, []);

  useLayoutEffect(() => {
    if (scopeIdentityRef.current === scopeId) return;
    scopeIdentityRef.current = scopeId;
    streamControllerRef.current?.abort();
    controlControllerRef.current?.abort();
    detailRequestRef.current = createClientId();
    activeConversationRef.current = null;
    setActiveConversation(null);
    applyProjection(null);
    setActionJourneys(new Map());
    setDeleteTarget(null);
    setDeleteError('');
    setDetailLoadState({ status: 'idle' });
    setHistoryDrawerOpen(false);
    setNotice(null);
    setPrompt('');
    setDiagnosticWire(undefined);
  }, [applyProjection, scopeId]);

  useEffect(() => {
    activeConversationRef.current = activeConversation;
  }, [activeConversation]);

  useEffect(() => {
    scrollAnchorRef.current?.scrollIntoView?.({
      behavior: 'smooth',
      block: 'end',
    });
  }, [activeConversation?.messages, projection]);

  useEffect(() => {
    document.body.classList.add('aevatar-chat-page-host');
    return () => document.body.classList.remove('aevatar-chat-page-host');
  }, []);

  useEffect(
    () => () => {
      streamControllerRef.current?.abort();
      controlControllerRef.current?.abort();
    },
    [],
  );

  const loadActorState = useCallback(
    async (
      conversationId: string,
      current: ChatActorProjection | null,
      signal?: AbortSignal,
      useCursor = true,
    ): Promise<ChatActorProjection> => {
      const turnId = stringField(
        current?.activeTurn ?? current?.latestTurn,
        'turnId',
      );
      const cursor =
        useCursor && current && current.stateVersion > 0
          ? {
              afterStateVersion: current.stateVersion,
              ...(turnId ? { turnId } : {}),
            }
          : {};
      const envelope = await chatHistoryApi.loadConversationState(
        conversationId,
        cursor,
        signal,
      );
      if (
        current &&
        envelope &&
        typeof envelope === 'object' &&
        'status' in envelope &&
        envelope.status === 'not_found'
      ) {
        return current;
      }
      let result = applyCurrentStateResult(
        current ?? createChatActorProjection(conversationId),
        envelope,
      );
      if (result.reloadWithoutCursor) {
        result = applyCurrentStateResult(
          result.projection,
          await chatHistoryApi.loadConversationState(
            conversationId,
            {},
            signal,
          ),
        );
      }
      if (activeConversationRef.current?.conversationId === conversationId) {
        applyProjection(result.projection);
      }
      return result.projection;
    },
    [applyProjection],
  );

  useEffect(() => {
    const conversationId = activeConversation?.conversationId?.trim();
    if (
      !isStreaming ||
      !conversationId ||
      (projection?.stateVersion ?? 0) > 0
    ) {
      return;
    }

    const controller = new AbortController();
    let delayIndex = 0;
    let timeoutId: number | undefined;

    const scheduleRefresh = () => {
      if (
        controller.signal.aborted ||
        delayIndex >= ACTIVE_STATE_REFRESH_DELAYS_MS.length
      ) {
        return;
      }
      const delay = ACTIVE_STATE_REFRESH_DELAYS_MS[delayIndex];
      delayIndex += 1;
      timeoutId = window.setTimeout(() => void refresh(), delay);
    };

    const refresh = async () => {
      const current = projectionRef.current;
      if (
        controller.signal.aborted ||
        !current ||
        current.actorId !== conversationId ||
        current.stateVersion > 0
      ) {
        return;
      }
      try {
        await loadActorState(conversationId, current, controller.signal, false);
      } catch {
        // A failed read stays version-fenced and may be retried within this bounded window.
      }
      if ((projectionRef.current?.stateVersion ?? 0) === 0) {
        scheduleRefresh();
      }
    };

    scheduleRefresh();
    return () => {
      controller.abort();
      if (timeoutId !== undefined) window.clearTimeout(timeoutId);
    };
  }, [
    activeConversation?.conversationId,
    isStreaming,
    loadActorState,
    projection?.stateVersion,
  ]);

  const restoreConversation = useCallback(
    async (conversationId: string) => {
      if (!canStartChat || isStreaming) return;
      streamControllerRef.current?.abort();
      const meta = conversations.find((item) => item.id === conversationId);
      const requestId = createClientId();
      const placeholder: ConversationState = {
        clientId: createClientId(),
        conversationId,
        expectedTurnCount: meta?.messageCount ?? 0,
        messages: [],
        status: 'completed_text',
        title: meta?.title || t('pages.chat.index.newChat', 'New chat'),
      };
      detailRequestRef.current = requestId;
      activeConversationRef.current = placeholder;
      setActiveConversation(placeholder);
      applyProjection(createChatActorProjection(conversationId));
      setActionJourneys(new Map());
      setDetailLoadState({ status: 'loading' });
      setNotice(null);
      setPrompt('');
      try {
        const [detail, stateEnvelope] = await Promise.all([
          chatHistoryApi.loadConversation(conversationId),
          chatHistoryApi.loadConversationState(conversationId),
        ]);
        if (detailRequestRef.current !== requestId) return;
        const state = applyCurrentStateResult(
          createChatActorProjection(conversationId),
          stateEnvelope,
        );
        if (wireInspectorEnabled) setDiagnosticWire(stateEnvelope);
        const restored: ConversationState = {
          ...placeholder,
          latestTurnId:
            stringField(
              state.projection.activeTurn ?? state.projection.latestTurn,
              'turnId',
            ) || undefined,
          messages: hydrateStoredMessages(detail.messages),
          status: resolveStoredConversationStatus(detail.messages),
        };
        activeConversationRef.current = restored;
        setActiveConversation(restored);
        applyProjection(state.projection);
        setDetailLoadState({ status: 'idle' });
      } catch (error) {
        if (detailRequestRef.current === requestId) {
          setDetailLoadState({ message: errorMessage(error), status: 'error' });
        }
      }
    },
    [
      applyProjection,
      canStartChat,
      conversations,
      isStreaming,
      wireInspectorEnabled,
    ],
  );

  const handleNewChat = useCallback(() => {
    if (isStreaming) return;
    const current = activeConversationRef.current;
    if (current?.status === 'draft' && current.messages.length === 0) {
      setHistoryDrawerOpen(false);
      return;
    }
    const draft = createDraftConversation();
    activeConversationRef.current = draft;
    setActiveConversation(draft);
    applyProjection(null);
    setActionJourneys(new Map());
    setDetailLoadState({ status: 'idle' });
    setHistoryDrawerOpen(false);
    setNotice(null);
    setPrompt('');
    setDiagnosticWire(undefined);
  }, [applyProjection, isStreaming]);

  const handleDeleteConversation = useCallback(async () => {
    if (!deleteTarget || deletingConversation || isStreaming) return;
    setDeletingConversation(true);
    setDeleteError('');
    try {
      await chatHistoryApi.deleteConversation(deleteTarget.id);
      setNotice({
        message: t(
          'pages.chat.index.deleteAccepted',
          'Deletion request accepted; waiting for actor and transcript projection.',
        ),
        type: 'info',
      });
      setDeleteTarget(null);
      await queryClient.invalidateQueries({
        queryKey: ['chat-conversations', scopeId],
      });
    } catch (error) {
      setDeleteError(errorMessage(error));
    } finally {
      setDeletingConversation(false);
    }
  }, [deleteTarget, deletingConversation, isStreaming, queryClient, scopeId]);

  const streamCommand = useCallback(
    async (
      conversation: ConversationState,
      command: ChatCommand,
      safeUserText: string,
    ): Promise<boolean> => {
      if (!canStartChat || isStreaming) return false;
      const userMessage = createChatMessage('user', safeUserText);
      const assistantMessageId = createClientId();
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
      let streaming: ConversationState = {
        ...conversation,
        messages: [...conversation.messages, userMessage, assistantMessage],
        status: 'streaming',
        title:
          conversation.title === t('pages.chat.index.newChat', 'New chat')
            ? trimTitle(safeUserText)
            : conversation.title,
      };
      const currentProjection = projectionRef.current;
      let actorState =
        currentProjection &&
        currentProjection.actorId === (conversation.conversationId ?? null)
          ? currentProjection
          : createChatActorProjection(conversation.conversationId ?? null);
      const rawFrames: unknown[] = [];
      const accumulator = createRuntimeEventAccumulator();
      let authoritativeConversationId = '';
      let authoritativeTurnId = '';
      const controller = new AbortController();
      streamControllerRef.current?.abort();
      streamControllerRef.current = controller;
      detailRequestRef.current = createClientId();
      setDetailLoadState({ status: 'idle' });
      setNotice(null);
      setPrompt('');
      activeConversationRef.current = streaming;
      setActiveConversation(streaming);
      try {
        const response = await sendChatCommand(command, controller.signal);
        for await (const frame of readChatStreamFrames(response, {
          signal: controller.signal,
        })) {
          rawFrames.push(frame.raw);
          if (wireInspectorEnabled) setDiagnosticWire(frame.raw);
          const actorFrame = decodeActorFrame(frame.raw);
          if (actorFrame.type !== 'ignored') {
            actorState = reduceActorFrame(actorState, actorFrame);
            applyProjection(actorState);
          }
          if (!frame.event) continue;
          applyRuntimeEvent(accumulator, frame.event);
          if (frame.event.type === 'RUN_STARTED') {
            const conversationId = accumulator.actorId.trim();
            const turnId = accumulator.runId.trim();
            if (!conversationId || !turnId) {
              throw new Error(
                t(
                  'pages.chat.index.invalidRunIdentity',
                  'Chat RUN_STARTED did not contain authoritative conversation and turn identities.',
                ),
              );
            }
            if (
              command.conversationId &&
              command.conversationId !== conversationId
            ) {
              throw new Error(
                t(
                  'pages.chat.index.conversationIdentityMismatch',
                  'Chat returned a different conversation identity.',
                ),
              );
            }
            if (
              (authoritativeConversationId &&
                authoritativeConversationId !== conversationId) ||
              (authoritativeTurnId && authoritativeTurnId !== turnId)
            ) {
              throw new Error(
                t(
                  'pages.chat.index.runIdentityChanged',
                  'Chat RUN_STARTED identity changed during the stream.',
                ),
              );
            }
            authoritativeConversationId = conversationId;
            authoritativeTurnId = turnId;
            if (actorState.actorId && actorState.actorId !== conversationId) {
              throw new Error(
                t(
                  'pages.chat.index.actorIdentityMismatch',
                  'Actor state does not match the chat conversation.',
                ),
              );
            }
            actorState = { ...actorState, actorId: conversationId };
            applyProjection(actorState);
            try {
              actorState = await loadActorState(
                conversationId,
                actorState,
                controller.signal,
                false,
              );
              applyProjection(actorState);
            } catch {
              // Live facts remain visible, but version-fenced controls stay disabled without current state.
            }
            streaming = {
              ...streaming,
              conversationId,
              expectedTurnCount: conversation.expectedTurnCount + 1,
              latestTurnId: turnId,
            };
          }
          if (isRawObserved(frame.event)) continue;
          streaming = {
            ...streaming,
            messages: streaming.messages.map((message) =>
              message.id === assistantMessageId
                ? {
                    ...message,
                    ...buildAssistantMessagePatch(
                      accumulator,
                      accumulator.errorText ? 'error' : 'streaming',
                    ),
                  }
                : message,
            ),
          };
          activeConversationRef.current = streaming;
          setActiveConversation(streaming);
        }
        if (controller.signal.aborted) throw controller.signal.reason;
        if (!authoritativeConversationId || !authoritativeTurnId) {
          throw new Error(
            t(
              'pages.chat.index.missingRunIdentity',
              'Chat stream ended without authoritative conversation and turn identities.',
            ),
          );
        }
        const artifacts = extractChatStreamArtifacts(rawFrames);
        const target = artifacts.target || streaming.target;
        const final: ConversationState = {
          ...streaming,
          messages: streaming.messages.map((message) =>
            message.id === assistantMessageId
              ? {
                  ...message,
                  ...buildAssistantMessagePatch(
                    accumulator,
                    accumulator.errorText ? 'error' : 'complete',
                  ),
                }
              : message,
          ),
          status: accumulator.errorText
            ? 'error'
            : resolveStudioJump(target)
              ? 'completed_with_studio_target'
              : 'completed_text',
          target,
          usage: artifacts.usage || streaming.usage,
        };
        activeConversationRef.current = final;
        setActiveConversation(final);
        await queryClient.invalidateQueries({
          queryKey: ['chat-conversations', scopeId],
        });
        try {
          actorState = await loadActorState(
            authoritativeConversationId,
            actorState,
            controller.signal,
            false,
          );
          applyProjection(actorState);
        } catch {
          // Current-state materialization is eventually consistent; live actor facts remain visible.
        }
        return true;
      } catch (error) {
        const message =
          controller.signal.aborted && !accumulator.errorText
            ? t('pages.chat.index.observationStopped', 'Observation stopped.')
            : errorMessage(error);
        accumulator.errorText = message;
        const failed: ConversationState = {
          ...streaming,
          messages: streaming.messages.map((entry) =>
            entry.id === assistantMessageId
              ? {
                  ...entry,
                  ...buildAssistantMessagePatch(accumulator, 'error'),
                }
              : entry,
          ),
          status: 'error',
        };
        activeConversationRef.current = failed;
        setActiveConversation(failed);
        return false;
      } finally {
        if (streamControllerRef.current === controller) {
          streamControllerRef.current = null;
        }
      }
    },
    [
      applyProjection,
      canStartChat,
      isStreaming,
      loadActorState,
      queryClient,
      scopeId,
      wireInspectorEnabled,
    ],
  );

  const handleSend = useCallback(() => {
    const value = prompt.trim();
    if (!value || actorHasActiveWork) return;
    const conversation = activeConversation ?? createDraftConversation();
    void streamCommand(
      conversation,
      {
        type: 'text',
        ...(conversation.conversationId
          ? { conversationId: conversation.conversationId }
          : {}),
        clientRequestId: createClientId(),
        prompt: value,
      },
      value,
    );
  }, [activeConversation, actorHasActiveWork, prompt, streamCommand]);

  const dispatchAcceptedCommand = useCallback(
    async (command: ChatControlCommand) => {
      if (controlBusy) return;
      const controller = new AbortController();
      controlControllerRef.current?.abort();
      controlControllerRef.current = controller;
      setControlBusy(true);
      setNotice(null);
      try {
        await sendChatCommand(command, controller.signal);
        setNotice({
          message: t('pages.chat.index.requestAccepted', 'Request accepted'),
          type: 'info',
        });
        try {
          await loadActorState(
            command.conversationId,
            projectionRef.current,
            controller.signal,
          );
        } catch {
          // A 202 receipt is dispatch-only; stale state remains honest until refreshed.
        }
      } catch (error) {
        setNotice({ message: errorMessage(error), type: 'error' });
      } finally {
        if (controlControllerRef.current === controller) {
          controlControllerRef.current = null;
        }
        setControlBusy(false);
      }
    },
    [controlBusy, loadActorState],
  );

  const requireControlContext = useCallback(() => {
    const conversationId = activeConversationRef.current?.conversationId;
    const state = projectionRef.current;
    if (!conversationId || !state) return null;
    return { conversationId, state };
  }, []);

  const handleInputResolve = useCallback(
    (answer: ChatInputAnswer, input: ChatPendingInput) => {
      const context = requireControlContext();
      if (!context) return;
      void dispatchAcceptedCommand({
        type: 'input.resolve',
        conversationId: context.conversationId,
        requestId: input.requestId,
        clientRequestId: createClientId(),
        answer,
        expectedStateVersion: context.state.stateVersion,
      });
    },
    [dispatchAcceptedCommand, requireControlContext],
  );

  const handlePlanResolve = useCallback(
    (confirmed: boolean, gate: ChatPlanGate) => {
      const context = requireControlContext();
      if (
        !context ||
        gate.mode !== 'confirm' ||
        gate.status !== 'pending' ||
        !gate.requestId ||
        !gate.taskId ||
        !gate.planId ||
        gate.planRevision === undefined
      ) {
        return;
      }
      void dispatchAcceptedCommand({
        type: 'plan.resolve',
        conversationId: context.conversationId,
        taskId: gate.taskId,
        planId: gate.planId,
        requestId: gate.requestId,
        clientRequestId: createClientId(),
        planRevision: gate.planRevision,
        confirmed,
        expectedStateVersion: context.state.stateVersion,
      });
    },
    [dispatchAcceptedCommand, requireControlContext],
  );

  const handleTaskStop = useCallback(() => {
    const context = requireControlContext();
    const turnId = stringField(context?.state.activeTurn, 'turnId');
    if (!context || !turnId || !actorCan(context.state, 'stop')) return;
    void dispatchAcceptedCommand({
      type: 'task.stop',
      conversationId: context.conversationId,
      turnId,
      stopRequestId: createClientId(),
      clientRequestId: createClientId(),
      expectedStateVersion: context.state.stateVersion,
    });
  }, [dispatchAcceptedCommand, requireControlContext]);

  const handleSteer = useCallback(
    (instruction: string) => {
      const context = requireControlContext();
      const turnId = stringField(context?.state.activeTurn, 'turnId');
      if (!context || !turnId) return;
      void dispatchAcceptedCommand({
        type: 'task.steer',
        conversationId: context.conversationId,
        turnId,
        steeringId: createClientId(),
        clientRequestId: createClientId(),
        instruction,
        expectedStateVersion: context.state.stateVersion,
      });
    },
    [dispatchAcceptedCommand, requireControlContext],
  );

  const handleComposerSend = useCallback(() => {
    const value = prompt.trim();
    if (!value) return;
    const pendingInput = projectionRef.current?.pendingInput;
    if (pendingInput?.allowFreeText) {
      handleInputResolve({ freeText: value }, pendingInput);
      setPrompt('');
      return;
    }
    if (actorHasActiveWork) {
      handleSteer(value);
      setPrompt('');
      return;
    }
    handleSend();
  }, [actorHasActiveWork, handleInputResolve, handleSend, handleSteer, prompt]);

  const dispatchStepControl = useCallback(
    (type: 'step.retry' | 'step.skip', step: ChatActorStep) => {
      const context = requireControlContext();
      const operation = step.operation;
      const turnId =
        stringField(operation, 'turnId') ||
        stringField(context?.state.activeTurn, 'turnId');
      const taskId =
        stringField(operation, 'taskId') ||
        stringField(context?.state.task, 'taskId');
      const generation = operation?.operationGeneration;
      if (
        !context ||
        !turnId ||
        !taskId ||
        typeof generation !== 'number' ||
        !actorCan(
          context.state,
          type === 'step.retry' ? 'retry' : 'skip',
          step.stepId,
        )
      ) {
        return;
      }
      const requestId = createClientId();
      void dispatchAcceptedCommand(
        type === 'step.retry'
          ? {
              type,
              conversationId: context.conversationId,
              turnId,
              taskId,
              stepId: step.stepId,
              retryRequestId: requestId,
              clientRequestId: createClientId(),
              expectedOperationGeneration: generation,
              expectedStateVersion: context.state.stateVersion,
            }
          : {
              type,
              conversationId: context.conversationId,
              turnId,
              taskId,
              stepId: step.stepId,
              skipRequestId: requestId,
              clientRequestId: createClientId(),
              expectedOperationGeneration: generation,
              expectedStateVersion: context.state.stateVersion,
            },
      );
    },
    [dispatchAcceptedCommand, requireControlContext],
  );

  const updateJourney = useCallback(
    (
      request: Pick<
        ChatNyxIdActionRequest,
        'actorId' | 'actionRequestId'
      >,
      patch: Partial<ChatActionJourney>,
    ) => {
      setActionJourneys((current) => {
        const next = new Map(current);
        const key = chatActionIdentityKey(
          request.actorId,
          request.actionRequestId,
        );
        next.set(key, {
          ...(next.get(key) ?? {}),
          ...patch,
        });
        return next;
      });
    },
    [],
  );

  const sendActionReport = useCallback(
    async (
      request: ChatNyxIdActionRequest,
      disposition: ActionDisposition,
      resource?: ChatActionResource,
    ) => {
      const conversation = activeConversationRef.current;
      if (
        !conversation?.conversationId ||
        conversation.conversationId !== request.actorId
      ) {
        updateJourney(request, {
          error: t(
            'pages.chat.index.actionIdentityChanged',
            'This action no longer matches the active conversation.',
          ),
        });
        return;
      }
      const report = {
        actionRequestId: request.actionRequestId,
        originTurnId: request.originTurnId,
        disposition,
        ...(resource ? { resource } : {}),
      };
      updateJourney(request, { busy: true, error: undefined });
      const accepted = await streamCommand(
        conversation,
        {
          type: 'action.continue',
          conversationId: conversation.conversationId,
          originTurnId: request.originTurnId,
          clientRequestId: createClientId(),
          actions: [report],
        },
        t(
          'pages.chat.index.actionUpdate',
          'NyxID action update: {disposition}.',
          { disposition },
        ),
      );
      updateJourney(
        request,
        accepted
          ? { busy: false, report: report as ChatActionJourney['report'] }
          : {
              busy: false,
              error: t(
                'pages.chat.index.actionReportNotAccepted',
                'Action report was not accepted.',
              ),
              report: undefined,
            },
      );
    },
    [streamCommand, updateJourney],
  );

  const handleActionOpen = useCallback(
    async (request: ChatNyxIdActionRequest) => {
      if (request.action === 'service.access_review') {
        // The service is already connected; only this session's authorization
        // is missing. Run the consent round-trip against the exact resource
        // and return to this conversation to submit the completion report.
        updateJourney(request, { busy: true, error: undefined });
        try {
          const client = new NyxIDAuthClient(getNyxIDRuntimeConfig());
          await client.loginWithRedirect({
            flow: 'serviceAccessReview',
            resources: [request.params.serviceAccessReview.resourceUri],
            returnTo:
              `/chat?conversationId=${encodeURIComponent(request.actorId)}` +
              `&accessReview=${encodeURIComponent(request.actionRequestId)}`,
          });
        } catch (error) {
          updateJourney(request, { busy: false, error: errorMessage(error) });
        }
        return;
      }
      updateJourney(request, { busy: true, error: undefined });
      try {
        const connectors = await listNyxIdConnectors();
        const baseline = matchingUserServiceIds(request, connectors);
        updateJourney(request, { baseline, busy: false });
        const slug =
          'catalogService' in request.params
            ? request.params.catalogService.serviceSlug
            : undefined;
        const opened = window.open(
          buildNyxIdConnectUrl(slug),
          'nyxid-connect',
          'noopener,noreferrer',
        );
        opened?.focus?.();
        if (!opened) {
          updateJourney(request, {
            error: t(
              'pages.chat.index.popupBlocked',
              'The NyxID window was blocked. Allow popups and try again.',
            ),
          });
        }
      } catch (error) {
        updateJourney(request, {
          busy: false,
          error: errorMessage(error),
        });
      }
    },
    [updateJourney],
  );

  const handleActionRefresh = useCallback(
    async (request: ChatNyxIdActionRequest) => {
      if (request.action !== 'service.connect') return;
      const journey = actionJourneys.get(
        chatActionIdentityKey(request.actorId, request.actionRequestId),
      );
      if (!journey?.baseline) {
        updateJourney(request, {
          error: t(
            'pages.chat.index.openNyxIdFirst',
            'Open NyxID from this action first so the connection baseline is known.',
          ),
        });
        return;
      }
      updateJourney(request, { busy: true, error: undefined });
      try {
        const current = matchingUserServiceIds(
          request,
          await listNyxIdConnectors(),
        );
        const userServiceId = matchNewUserServiceId(journey.baseline, current);
        if (!userServiceId) {
          updateJourney(request, {
            busy: false,
            error: t(
              'pages.chat.index.connectionNotUnique',
              'Exactly one new matching UserService was not found; no completion was reported.',
            ),
          });
          return;
        }
        await sendActionReport(
          request,
          'completed',
          actionReportResource(userServiceId),
        );
      } catch (error) {
        updateJourney(request, {
          busy: false,
          error: errorMessage(error),
        });
      }
    },
    [actionJourneys, sendActionReport, updateJourney],
  );

  const handleActionConnectCredential = useCallback(
    async (request: ChatNyxIdActionRequest, credential: string) => {
      if (!('catalogService' in request.params)) return;
      updateJourney(request, { busy: true, error: undefined });
      try {
        const serviceSlug = request.params.catalogService.serviceSlug;
        const userServiceId = await createNyxIdCatalogKey({
          serviceSlug,
          credential,
          label: serviceSlug,
        });
        await sendActionReport(
          request,
          'completed',
          actionReportResource(userServiceId),
        );
      } catch (error) {
        updateJourney(request, {
          busy: false,
          error: errorMessage(error),
        });
        if (
          !(
            error instanceof Error &&
            'code' in error &&
            error.code === 'NYXID_USER_SERVICE_ID_MISSING'
          )
        ) {
          await sendActionReport(request, 'failed');
        }
      }
    },
    [sendActionReport, updateJourney],
  );

  const handleComposerStop = useCallback(() => {
    if (projectionRef.current && actorCan(projectionRef.current, 'stop')) {
      handleTaskStop();
      return;
    }
    streamControllerRef.current?.abort();
  }, [handleTaskStop]);

  // Returning from a NyxID service access review: reopen the paused
  // conversation and report the completed action so the original request
  // resumes after the server verifies the postcondition.
  const pendingAccessReviewReturnRef = useRef<{
    conversationId: string;
    actionRequestId: string;
  } | null>(
    (() => {
      const conversationId = readChatQueryValue('conversationId');
      const actionRequestId = readChatQueryValue('accessReview');
      return conversationId && actionRequestId
        ? { conversationId, actionRequestId }
        : null;
    })(),
  );

  useEffect(() => {
    const pending = pendingAccessReviewReturnRef.current;
    if (!pending || !canStartChat) return;
    if (activeConversationRef.current?.conversationId === pending.conversationId)
      return;
    void restoreConversation(pending.conversationId);
  }, [canStartChat, restoreConversation]);

  useEffect(() => {
    const pending = pendingAccessReviewReturnRef.current;
    if (!pending || projection?.actorId !== pending.conversationId) return;
    const summary = [...(projection?.actions.values() ?? [])].find(
      (item) => item.actionRequestId === pending.actionRequestId,
    );
    const request = summary?.request;
    if (request?.action !== 'service.access_review') return;
    pendingAccessReviewReturnRef.current = null;
    const url = new URL(window.location.href);
    url.searchParams.delete('accessReview');
    url.searchParams.delete('conversationId');
    window.history.replaceState(null, '', url.toString());
    if ((summary.reports ?? []).length === 0) {
      void sendActionReport(request, 'completed', {
        userService: {
          userServiceId: request.params.serviceAccessReview.userServiceId,
        },
      });
    }
  }, [projection, sendActionReport]);

  const messageCount = activeConversation?.messages.length ?? 0;
  const activeConversationId = activeConversation?.conversationId;
  const historyRail = (
    <>
      <div
        style={{
          borderBottom: `1px solid ${token.colorBorderSecondary}`,
          display: 'flex',
          flexDirection: 'column',
          gap: 8,
          padding: 12,
        }}
      >
        <Button
          block
          disabled={isStreaming}
          icon={<PlusOutlined />}
          onClick={handleNewChat}
          style={{ minHeight: 44 }}
          type="primary"
        >
          {t('pages.chat.index.newChatAction', 'New Chat')}
        </Button>
        <Typography.Text
          style={{ color: token.colorTextTertiary, fontSize: 12 }}
        >
          {t(
            'pages.chat.index.historyStoredInWorkspace',
            'History is saved to this workspace.',
          )}
        </Typography.Text>
      </div>
      <div
        aria-busy={conversationsQuery.isLoading}
        style={{
          flex: 1,
          minHeight: 0,
          overflow: 'auto',
          padding: '8px 6px 10px',
        }}
      >
        {conversationsQuery.isLoading ? (
          <div
            style={{
              display: 'flex',
              justifyContent: 'center',
              minHeight: 120,
            }}
          >
            <Spin
              description={t(
                'pages.chat.index.loadingHistory',
                'Loading chat history',
              )}
              size="small"
            />
          </div>
        ) : conversationsQuery.isError ? (
          <Alert
            action={
              <Button
                aria-label={t(
                  'pages.chat.index.retryHistory',
                  'Retry chat history',
                )}
                icon={<ReloadOutlined />}
                onClick={() => void conversationsQuery.refetch()}
                size="small"
                type="text"
              />
            }
            description={errorMessage(conversationsQuery.error)}
            message={t(
              'pages.chat.index.failedToLoadHistory',
              'Chat history could not be loaded',
            )}
            showIcon
            type="error"
          />
        ) : visibleConversations.length === 0 ? (
          <Empty
            description={t('pages.chat.index.noChatHistory', 'No chat history')}
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            style={{ marginTop: 24 }}
          />
        ) : (
          <Space direction="vertical" size={6} style={{ width: '100%' }}>
            {visibleConversations.map((conversation) => {
              const active =
                conversation.id === activeConversation?.conversationId;
              return (
                <div
                  className="aevatar-chat-history-item"
                  key={conversation.id}
                  style={{
                    background: active ? token.colorPrimaryBg : 'transparent',
                    border: `1px solid ${
                      active ? token.colorPrimaryBorder : 'transparent'
                    }`,
                    borderRadius: token.borderRadius,
                    boxShadow: active
                      ? `inset 3px 0 0 ${token.colorPrimary}`
                      : undefined,
                    display: 'flex',
                    gap: 4,
                    padding: 4,
                    width: '100%',
                  }}
                >
                  <button
                    aria-current={active ? 'page' : undefined}
                    aria-label={conversation.title}
                    className="aevatar-chat-history-select"
                    disabled={isStreaming}
                    onClick={() => {
                      setHistoryDrawerOpen(false);
                      if (
                        activeConversation?.conversationId !== conversation.id
                      ) {
                        void restoreConversation(conversation.id);
                      }
                    }}
                    style={{
                      alignItems: 'flex-start',
                      background: 'transparent',
                      border: 0,
                      color: 'inherit',
                      cursor: isStreaming ? 'not-allowed' : 'pointer',
                      display: 'flex',
                      flex: 1,
                      gap: 8,
                      minHeight: 40,
                      minWidth: 0,
                      padding: '5px 4px',
                      textAlign: 'left',
                    }}
                    type="button"
                  >
                    <MessageOutlined
                      style={{
                        color: active
                          ? token.colorPrimary
                          : token.colorTextTertiary,
                        fontSize: 14,
                        marginTop: 2,
                      }}
                    />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <span
                        style={{
                          color: token.colorText,
                          display: 'block',
                          fontSize: 13,
                          fontWeight: 600,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                      >
                        {conversation.title}
                      </span>
                      <span
                        style={{
                          color: token.colorTextTertiary,
                          display: 'flex',
                          fontSize: 11,
                          gap: 6,
                          marginTop: 3,
                        }}
                      >
                        <span>
                          {conversation.liveStatus === 'streaming'
                            ? formatStatusLabel(conversation.liveStatus)
                            : formatTurnCount(conversation.messageCount)}
                        </span>
                        <span>
                          {formatRelativeTime(conversation.updatedAt)}
                        </span>
                      </span>
                    </span>
                  </button>
                  <Tooltip
                    title={t('pages.chat.index.deleteChat', 'Delete {title}', {
                      title: conversation.title,
                    })}
                  >
                    <Button
                      aria-label={t(
                        'pages.chat.index.deleteChat',
                        'Delete {title}',
                        {
                          title: conversation.title,
                        },
                      )}
                      danger
                      disabled={isStreaming}
                      icon={<DeleteOutlined />}
                      onClick={() => {
                        setDeleteError('');
                        setDeleteTarget(conversation);
                      }}
                      style={{ minHeight: 40, minWidth: 40 }}
                      type="text"
                    />
                  </Tooltip>
                </div>
              );
            })}
          </Space>
        )}
      </div>
    </>
  );

  return (
    <AevatarPageShell
      layoutMode="viewport"
      pageHeaderRender={false}
      title={t('pages.chat.index.title', 'Chat')}
    >
      <div
        className="aevatar-chat-page"
        style={{
          background: token.colorBgContainer,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadius,
          boxShadow: '0 1px 2px rgba(15, 23, 42, 0.04)',
          display: 'grid',
          flex: 1,
          height: '100%',
          minHeight: 0,
          overflow: 'hidden',
        }}
      >
        <aside
          className="aevatar-chat-history-desktop"
          style={{
            background: token.colorBgContainer,
            borderRight: `1px solid ${token.colorBorderSecondary}`,
            display: 'flex',
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          {historyRail}
        </aside>
        <main
          className="aevatar-chat-main"
          style={{
            background: token.colorBgContainer,
            display: 'flex',
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <div
            className="aevatar-chat-main-header"
            style={{
              alignItems: 'center',
              borderBottom: `1px solid ${token.colorBorderSecondary}`,
              display: 'flex',
              gap: 12,
              justifyContent: 'space-between',
              minHeight: 54,
              padding: '10px 14px',
            }}
          >
            <Tooltip
              title={t('pages.chat.index.openHistory', 'Open chat history')}
            >
              <Button
                aria-label={t(
                  'pages.chat.index.openHistory',
                  'Open chat history',
                )}
                className="aevatar-chat-history-trigger"
                icon={<HistoryOutlined />}
                onClick={() => setHistoryDrawerOpen(true)}
                style={{ minHeight: 44, minWidth: 44 }}
                type="text"
              />
            </Tooltip>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Typography.Text
                strong
                style={{
                  color: token.colorTextHeading,
                  display: 'block',
                  fontSize: 18,
                  overflow: 'hidden',
                  textOverflow: 'ellipsis',
                  whiteSpace: 'nowrap',
                }}
              >
                {activeConversation?.title ||
                  t('pages.chat.index.title', 'Chat')}
              </Typography.Text>
              <Typography.Text
                style={{
                  color: token.colorTextTertiary,
                  display: 'block',
                  fontSize: 12,
                }}
              >
                {scopeId
                  ? t('pages.chat.index.scopeValue', 'Scope {scopeId}', {
                      scopeId,
                    })
                  : t('pages.chat.index.resolvingScope', 'Resolving scope')}
              </Typography.Text>
            </div>
            <Space className="aevatar-chat-main-actions" wrap>
              {activeConversation ? (
                <Tag color={resolveStatusTone(activeConversation.status)}>
                  {formatStatusLabel(activeConversation.status)}
                </Tag>
              ) : null}
              {studioJump ? (
                <Button
                  onClick={() => history.push(studioJump.href)}
                  type="primary"
                >
                  {studioJump.label}
                </Button>
              ) : null}
            </Space>
          </div>

          {scopeMismatch ? (
            <Alert
              banner
              message={t(
                'pages.chat.index.scopeMismatch',
                'Requested scope {requestedScopeId} does not match authenticated scope {authenticatedScopeId}. Open Chat from the active workspace or sign in again.',
                { authenticatedScopeId, requestedScopeId: routeScopeId },
              )}
              type="error"
            />
          ) : chatCreationUnavailable ? (
            <Alert
              banner
              message={t(
                'pages.chat.index.chatRequiresAuthentication',
                'Starting or continuing a chat requires a trusted authenticated scope.',
              )}
              type="info"
            />
          ) : !scopeId && !authSessionQuery.isLoading ? (
            <Alert
              banner
              message={t(
                'pages.chat.index.noScope',
                'No usable scope was resolved for this account. Refresh and try again.',
              )}
              type="warning"
            />
          ) : null}
          {notice ? (
            <Alert
              banner
              closable
              message={notice.message}
              onClose={() => setNotice(null)}
              type={notice.type}
            />
          ) : null}

          <div
            style={{
              background: token.colorBgLayout,
              display: 'flex',
              flex: 1,
              flexDirection: 'column',
              minHeight: 0,
              overflow: 'auto',
              padding: 16,
            }}
          >
            {detailLoadState.status === 'loading' ? (
              <div
                aria-live="polite"
                style={{
                  display: 'flex',
                  flex: 1,
                  justifyContent: 'center',
                  minHeight: 180,
                }}
              >
                <Spin
                  description={t(
                    'pages.chat.index.loadingConversation',
                    'Loading conversation',
                  )}
                />
              </div>
            ) : detailLoadState.status === 'error' ? (
              <Alert
                action={
                  activeConversationId ? (
                    <Button
                      icon={<ReloadOutlined />}
                      onClick={() =>
                        void restoreConversation(activeConversationId)
                      }
                      size="small"
                    >
                      {t('pages.chat.index.retry', 'Retry')}
                    </Button>
                  ) : null
                }
                description={detailLoadState.message}
                message={t(
                  'pages.chat.index.failedToLoadConversation',
                  'Conversation could not be loaded',
                )}
                showIcon
                type="error"
              />
            ) : messageCount === 0 ? (
              <div
                style={{
                  alignItems: 'center',
                  background: token.colorBgContainer,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: token.borderRadius,
                  display: 'flex',
                  gap: 10,
                  margin: '8px 0 0',
                  maxWidth: 720,
                  padding: '14px 16px',
                }}
              >
                <MessageOutlined
                  style={{ color: token.colorPrimary, fontSize: 16 }}
                />
                <Typography.Text style={{ color: token.colorTextSecondary }}>
                  {t(
                    'pages.chat.index.emptyDescription',
                    'Describe the Team, Member, or Workflow you want to create.',
                  )}
                </Typography.Text>
              </div>
            ) : (
              <Space
                className="aevatar-chat-message-list"
                direction="vertical"
                size={14}
                style={{ marginInline: 'auto', maxWidth: 1440, width: '100%' }}
              >
                {activeConversation?.messages.map((message) => (
                  <ChatMessageEntry key={message.id} message={message} />
                ))}
              </Space>
            )}
            <div
              style={{ margin: '14px auto 0', maxWidth: 1440, width: '100%' }}
            >
              <ChatActorControls
                actionJourneys={actionJourneys}
                diagnosticWire={diagnosticWire}
                disabled={controlBusy || projection?.stateVersion === 0}
                onActionConnectCredential={handleActionConnectCredential}
                onActionOpen={handleActionOpen}
                onActionRefresh={handleActionRefresh}
                onActionReport={(request, disposition) =>
                  void sendActionReport(request, disposition)
                }
                onInputResolve={handleInputResolve}
                onPlanResolve={handlePlanResolve}
                onRetry={(step) => dispatchStepControl('step.retry', step)}
                onSkip={(step) => dispatchStepControl('step.skip', step)}
                onSteer={handleSteer}
                onStop={handleTaskStop}
                projection={projection}
                wireInspectorEnabled={wireInspectorEnabled && canStartChat}
              />
            </div>
            <div ref={scrollAnchorRef} />
          </div>

          <div
            style={{
              background: token.colorBgContainer,
              borderTop: `1px solid ${token.colorBorderSecondary}`,
              padding: '10px 14px 12px',
            }}
          >
            {hasUsage(activeConversation?.usage) ? (
              <Space size={8} style={{ marginBottom: 10 }} wrap>
                {activeConversation?.usage?.totalTokens !== undefined ? (
                  <Tag>
                    {t('pages.chat.index.totalTokens', '{count} tokens', {
                      count:
                        activeConversation.usage.totalTokens.toLocaleString(),
                    })}
                  </Tag>
                ) : null}
                {activeConversation?.usage?.model ? (
                  <Tag>{activeConversation.usage.model}</Tag>
                ) : null}
              </Space>
            ) : null}
            <ChatInput
              disabled={isConversationActionDisabled}
              isStreaming={isStreaming && !actorHasActiveWork}
              onChange={setPrompt}
              onSend={handleComposerSend}
              onStop={handleComposerStop}
              placeholder={
                projection?.pendingInput?.allowFreeText
                  ? t(
                      'pages.chat.index.composerInputAnswer',
                      'Answer the current question...',
                    )
                  : actorHasActiveWork
                    ? t(
                        'pages.chat.index.composerSteering',
                        'Steer the active task...',
                      )
                    : t(
                        'pages.chat.index.composerPlaceholder',
                        'Describe the workflow you want, or ask about the current setup...',
                      )
              }
              value={prompt}
            />
          </div>
        </main>
      </div>

      <Drawer
        className="aevatar-chat-history-drawer"
        destroyOnHidden
        onClose={() => setHistoryDrawerOpen(false)}
        open={historyDrawerOpen}
        placement="left"
        size="min(320px, calc(100vw - 48px))"
        title={t('pages.chat.index.historyTitle', 'Chat history')}
      >
        <div
          style={{ display: 'flex', flexDirection: 'column', height: '100%' }}
        >
          {historyRail}
        </div>
      </Drawer>

      <Modal
        cancelButtonProps={{ disabled: deletingConversation }}
        cancelText={t('pages.chat.index.cancel', 'Cancel')}
        confirmLoading={deletingConversation}
        destroyOnHidden
        okButtonProps={{ danger: true, disabled: isStreaming }}
        onCancel={() => {
          if (!deletingConversation) {
            setDeleteTarget(null);
            setDeleteError('');
          }
        }}
        onOk={() => void handleDeleteConversation()}
        okText={t('pages.chat.index.delete', 'Delete')}
        open={Boolean(deleteTarget)}
        title={t('pages.chat.index.deleteChatTitle', 'Delete conversation?')}
      >
        <Typography.Paragraph>
          {t(
            'pages.chat.index.deleteChatDescription',
            'Delete "{title}"? The command is asynchronous and the item remains until deletion is observed.',
            { title: deleteTarget?.title || '' },
          )}
        </Typography.Paragraph>
        {deleteError ? (
          <Alert
            description={deleteError}
            message={t(
              'pages.chat.index.deleteChatFailed',
              'Conversation could not be deleted',
            )}
            showIcon
            type="error"
          />
        ) : null}
      </Modal>
    </AevatarPageShell>
  );
};

export default ChatPage;
