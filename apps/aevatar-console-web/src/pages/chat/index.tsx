import {
  DeleteOutlined,
  HistoryOutlined,
  MessageOutlined,
  PlusOutlined,
  ReloadOutlined,
  SendOutlined,
} from "@ant-design/icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Modal,
  Space,
  Spin,
  Tag,
  Typography,
  theme,
} from "antd";
import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { studioApi } from "@/shared/studio/api";
import { AevatarPageShell } from "@/shared/ui/aevatarPageShells";
import { useConsoleToast } from "@/shared/ui/ConsoleToast";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import {
  chatRequestMayHaveBeenAccepted,
  extractChatHistoryContext,
  extractChatStreamArtifacts,
  readChatStreamFrames,
  startChatStreamWithHistoryRefreshRetry,
} from "./chatApi";
import { ChatHistoryApiError, chatHistoryApi } from "./chatHistoryApi";
import { ChatInput, ChatMessageBubble } from "./chatPresentation";
import type {
  ChatMessage,
  ChatSessionState,
  ChatStudioTarget,
  ChatUsageSummary,
  ChatCreateRecovery,
  ConversationMeta,
  LocalChatStatus,
  RuntimeEvent,
  StepInfo,
  StoredChatMessage,
  ToolCallInfo,
} from "./chatTypes";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import { t } from "@/shared/i18n/messages";

type ConversationState = {
  clientId: string;
  conversationId?: string;
  createCommandId?: string;
  createRequestPrompt?: string;
  expectedTurnCount: number;
  latestTurnId?: string;
  messages: ChatMessage[];
  sessionId: string;
  stateVersion?: number;
  status: LocalChatStatus;
  target?: ChatStudioTarget;
  title: string;
  usage?: ChatUsageSummary;
};

const EMPTY_CONVERSATION_IDS: ReadonlySet<string> = new Set();

type HistoryReconciliationState =
  | { status: "pending" }
  | { message: string; retryable: boolean; status: "failed" };

type PendingConversation = {
  conversation: ConversationState;
  reconciliation: HistoryReconciliationState;
};

const EMPTY_PENDING_CONVERSATIONS: ReadonlyMap<string, PendingConversation> =
  new Map();

type ConversationListItem = ConversationMeta & {
  historyReconciliation?: HistoryReconciliationState;
  liveStatus?: LocalChatStatus;
};

type DetailLoadState =
  | { status: "idle" }
  | { status: "loading" }
  | { message: string; status: "error" };

type StudioJump = {
  href: string;
  label: string;
};

function readChatQueryValue(
  key: string,
  search = typeof window === "undefined" ? "" : window.location.search
): string {
  return new URLSearchParams(search).get(key)?.trim() ?? "";
}

function createClientId(): string {
  return globalThis.crypto?.randomUUID?.()
    ? globalThis.crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function createDraftConversation(): ConversationState {
  return {
    clientId: createClientId(),
    createCommandId: createClientId(),
    expectedTurnCount: 0,
    messages: [],
    sessionId: createClientId(),
    status: "draft",
    title: t("pages.chat.index.newChat", "New chat"),
  };
}

function isContinuationStateVersion(
  value: number | undefined
): value is number {
  return Number.isSafeInteger(value) && (value ?? 0) > 0;
}

export function hydrateStoredMessages(
  messages: readonly StoredChatMessage[]
): ChatMessage[] {
  return messages.map((message) => ({
    authorId: message.authorId,
    authorName: message.authorName,
    content: message.content,
    error: message.error || undefined,
    id: message.id,
    role: message.role,
    status: message.error?.trim() ? "error" : message.status,
    thinking: message.thinking || undefined,
    timestamp: message.timestamp,
  }));
}

function resolveStoredConversationStatus(
  messages: readonly StoredChatMessage[]
): LocalChatStatus {
  const latestAssistantMessage = [...messages]
    .reverse()
    .find((message) => message.role === "assistant");
  const latestTerminalMessage = latestAssistantMessage ?? messages.at(-1);

  return latestTerminalMessage?.status === "error" ||
    Boolean(latestTerminalMessage?.error?.trim())
    ? "error"
    : "completed_text";
}

function ChatMessageEntry({ message }: { message: ChatMessage }): React.ReactElement {
  const authorName = message.authorName?.trim() || "";
  const isStandardRole = message.role === "user" || message.role === "assistant";

  if (isStandardRole) {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        {authorName ? (
          <Typography.Text
            style={{
              alignSelf: message.role === "user" ? "flex-end" : "flex-start",
              color: "#6b7280",
              fontSize: 11,
              lineHeight: 1.3,
              marginLeft: message.role === "assistant" ? 34 : 0,
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
    message.role.trim() || t("pages.chat.index.unknownRole", "Message");
  const displayName = authorName || roleLabel;

  return (
    <article
      aria-label={`${displayName} ${roleLabel} message`}
      style={{ display: "flex", gap: 10 }}
    >
      <MessageOutlined
        style={{
          background: "#f3f4f6",
          border: "1px solid #e5e7eb",
          borderRadius: 999,
          color: "#4b5563",
          flex: "0 0 auto",
          fontSize: 12,
          height: 24,
          lineHeight: "22px",
          marginTop: 3,
          textAlign: "center",
          width: 24,
        }}
      />
      <div style={{ flex: 1, maxWidth: "82%", minWidth: 0 }}>
        <Space align="center" size={6} wrap>
          <Typography.Text strong style={{ fontSize: 12 }}>
            {displayName}
          </Typography.Text>
          {authorName ? <Tag>{roleLabel}</Tag> : null}
        </Space>
        {message.thinking ? (
          <Typography.Paragraph
            style={{ color: "#6b7280", fontSize: 12, margin: "6px 0" }}
          >
            {message.thinking}
          </Typography.Paragraph>
        ) : null}
        {message.content ? (
          <div
            style={{
              color: "#1f2937",
              fontSize: 14,
              lineHeight: 1.65,
              whiteSpace: "pre-wrap",
              wordBreak: "break-word",
            }}
          >
            {message.content}
          </div>
        ) : null}
        {message.status === "error" && message.error ? (
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

function abortableDelay(delayMs: number, signal: AbortSignal): Promise<void> {
  if (delayMs <= 0) {
    return signal.aborted
      ? Promise.reject(new DOMException("Aborted", "AbortError"))
      : Promise.resolve();
  }

  return new Promise((resolve, reject) => {
    const timeout = window.setTimeout(() => {
      signal.removeEventListener("abort", handleAbort);
      resolve();
    }, delayMs);
    const handleAbort = () => {
      window.clearTimeout(timeout);
      reject(new DOMException("Aborted", "AbortError"));
    };

    signal.addEventListener("abort", handleAbort, { once: true });
  });
}

async function recoverCreateIdentity(
  scopeId: string,
  commandId: string,
  signal: AbortSignal
): Promise<ChatCreateRecovery | null> {
  for (const delayMs of [0, 300, 900, 1_800]) {
    await abortableDelay(delayMs, signal);
    try {
      return await chatHistoryApi.recoverCreate(scopeId, commandId, signal);
    } catch (error) {
      if (!(error instanceof ChatHistoryApiError) || error.status !== 404) {
        throw error;
      }
    }
  }

  return null;
}

function bindCreateRecovery(
  conversation: ConversationState,
  recovery: ChatCreateRecovery
): ConversationState {
  return {
    ...conversation,
    conversationId: recovery.conversationId,
    expectedTurnCount: conversation.expectedTurnCount + 1,
    latestTurnId: recovery.turnId,
    stateVersion: undefined,
  };
}

function createChatMessage(
  role: ChatMessage["role"],
  content: string,
  status: ChatMessage["status"] = "complete"
): ChatMessage {
  return {
    content,
    id: createClientId(),
    role,
    status,
    timestamp: Date.now(),
  };
}

function createIdleSession(scopeId = ""): ChatSessionState {
  return {
    actorId: "",
    commandId: "",
    endpointId: "chat",
    eventCount: 0,
    runId: "",
    scopeId,
    serviceId: "chat",
    status: "idle",
    updatedAt: undefined,
  };
}

function cloneStepInfo(steps?: readonly StepInfo[]): StepInfo[] {
  return (steps ?? []).map((step) => ({ ...step }));
}

function cloneToolCallInfo(toolCalls?: readonly ToolCallInfo[]): ToolCallInfo[] {
  return (toolCalls ?? []).map((toolCall) => ({ ...toolCall }));
}

function hydrateAuthoritativeMessages(
  storedMessages: readonly StoredChatMessage[],
  localMessages: readonly ChatMessage[],
  latestTurnId: string | undefined
): ChatMessage[] {
  const localMessagesById = new Map(
    localMessages.map((message) => [message.id, message] as const)
  );
  const latestLocalAssistant = latestTurnId
    ? [...localMessages]
        .reverse()
        .find((message) => message.role === "assistant")
    : undefined;

  return storedMessages.map((storedMessage) => {
    const authoritativeMessage = hydrateStoredMessages([storedMessage])[0];
    const localMessage =
      localMessagesById.get(storedMessage.id) ??
      (storedMessage.role === "assistant" &&
      storedMessage.turnId === latestTurnId
        ? latestLocalAssistant
        : undefined);
    if (!localMessage) {
      return authoritativeMessage;
    }

    return {
      ...authoritativeMessage,
      ...(localMessage.events
        ? { events: [...localMessage.events] }
        : {}),
      ...(localMessage.pendingApproval
        ? { pendingApproval: { ...localMessage.pendingApproval } }
        : {}),
      ...(localMessage.pendingRunIntervention
        ? {
            pendingRunIntervention: {
              ...localMessage.pendingRunIntervention,
            },
          }
        : {}),
      ...(localMessage.steps
        ? { steps: cloneStepInfo(localMessage.steps) }
        : {}),
      ...(localMessage.toolCalls
        ? { toolCalls: cloneToolCallInfo(localMessage.toolCalls) }
        : {}),
    };
  });
}

function resolveEventTimestamp(events: readonly RuntimeEvent[]): number {
  const lastTimestamp = events[events.length - 1]?.timestamp;
  return typeof lastTimestamp === "number" && Number.isFinite(lastTimestamp)
    ? lastTimestamp
    : Date.now();
}

function buildAssistantMessagePatch(
  accumulator: ReturnType<typeof createRuntimeEventAccumulator>,
  status: ChatMessage["status"]
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

function buildSessionFromAccumulator(
  scopeId: string,
  accumulator: ReturnType<typeof createRuntimeEventAccumulator>,
  status: ChatSessionState["status"]
): ChatSessionState {
  return {
    actorId: accumulator.actorId,
    commandId: accumulator.commandId,
    endpointId: "chat",
    error: accumulator.errorText || undefined,
    eventCount: accumulator.events.length,
    runId: accumulator.runId || accumulator.actorId,
    scopeId,
    serviceId: "chat",
    status,
    updatedAt: resolveEventTimestamp(accumulator.events),
  };
}

function trimTitle(value: string): string {
  const normalized = value.trim().replace(/\s+/g, " ");
  if (!normalized) {
    return t("pages.chat.index.newChat", "New chat");
  }

  return normalized.length > 60 ? `${normalized.slice(0, 57)}...` : normalized;
}

function formatStatusLabel(status: LocalChatStatus): string {
  switch (status) {
    case "draft":
      return t("pages.chat.index.status.draft", "Draft");
    case "streaming":
      return t("pages.chat.index.status.streaming", "Streaming");
    case "needs_confirmation":
      return t("pages.chat.index.status.needsConfirmation", "Needs confirmation");
    case "creating":
      return t("pages.chat.index.status.creating", "Creating");
    case "completed_with_studio_target":
      return t("pages.chat.index.status.studioReady", "Studio ready");
    case "completed_text":
      return t("pages.chat.index.status.completed", "Completed");
    case "error":
      return t("pages.chat.index.status.error", "Error");
    default:
      return t("pages.chat.index.status.draft", "Draft");
  }
}

function resolveStatusTone(
  status: LocalChatStatus
): "default" | "processing" | "success" | "warning" | "error" {
  switch (status) {
    case "streaming":
    case "creating":
      return "processing";
    case "needs_confirmation":
      return "warning";
    case "completed_with_studio_target":
    case "completed_text":
      return "success";
    case "error":
      return "error";
    default:
      return "default";
  }
}

function shouldAskForConfirmation(content: string): boolean {
  const normalized = content.toLowerCase();
  if (!normalized.trim()) {
    return false;
  }

  return (
    normalized.includes("confirm") ||
    normalized.includes("approval") ||
    normalized.includes("approve") ||
    normalized.includes("确认") ||
    normalized.includes("同意")
  );
}

function resolveStudioJump(target: ChatStudioTarget | undefined): StudioJump | null {
  if (!target) {
    return null;
  }

  if (target.studioUrl) {
    return {
      href: target.studioUrl,
      label: t("pages.chat.index.openWorkflowStudio", "Open Workflow Studio"),
    };
  }

  if (target.scopeId && target.teamId && target.memberId) {
    return {
      href: buildTeamMemberWorkflowStudioHref({
        memberId: target.memberId,
        mode: "edit-member",
        scopeId: target.scopeId,
        teamId: target.teamId,
        workflowId: target.workflowId,
      }),
      label: t("pages.chat.index.openWorkflowStudio", "Open Workflow Studio"),
    };
  }

  if (target.scopeId && target.teamId) {
    return {
      href: buildTeamDetailHref({
        scopeId: target.scopeId,
        tab: "members",
        teamId: target.teamId,
      }),
      label: t("pages.chat.index.openTeam", "Open Team"),
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
        usage.latencyMs)
  );
}

function isEmptyDraftConversation(
  conversation: ConversationState | null | undefined
): boolean {
  return Boolean(
    conversation &&
      conversation.status === "draft" &&
      conversation.messages.length === 0
  );
}

function formatRelativeTime(isoString: string): string {
  const timestamp = Date.parse(isoString);
  if (!Number.isFinite(timestamp)) {
    return "";
  }

  const minutes = Math.floor((Date.now() - timestamp) / 60_000);
  if (minutes < 1) {
    return t("pages.chat.index.time.justNow", "just now");
  }
  if (minutes < 60) {
    return t("pages.chat.index.time.minutesAgo", "{count}m ago", {
      count: minutes,
    });
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return t("pages.chat.index.time.hoursAgo", "{count}h ago", {
      count: hours,
    });
  }

  return t("pages.chat.index.time.daysAgo", "{count}d ago", {
    count: Math.floor(hours / 24),
  });
}

function formatTurnCount(count: number): string {
  return t("pages.chat.index.turnCount", "{count} turns", { count });
}

const ChatPage: React.FC = () => {
  const toast = useConsoleToast();
  const { token } = theme.useToken();
  const queryClient = useQueryClient();
  const activeConversationRef = useRef<ConversationState | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const createRecoveryControllerRef = useRef<AbortController | null>(null);
  const detailRequestRef = useRef("");
  const reconciliationControllersRef = useRef(
    new Map<string, AbortController>()
  );
  const scopeEpochRef = useRef(0);
  const scopeIdentityRef = useRef("");
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const [storedActiveConversation, setActiveConversation] =
    useState<ConversationState | null>(null);
  const [conversationStateScopeId, setConversationStateScopeId] = useState("");
  const [deleteTarget, setDeleteTarget] = useState<ConversationMeta | null>(null);
  const [deletingConversation, setDeletingConversation] = useState(false);
  const [deletedConversationIds, setDeletedConversationIds] = useState<
    ReadonlySet<string>
  >(() => new Set());
  const [pendingConversations, setPendingConversations] = useState<
    ReadonlyMap<string, PendingConversation>
  >(() => new Map());
  const [detailLoadState, setDetailLoadState] = useState<DetailLoadState>({
    status: "idle",
  });
  const [historyDrawerOpen, setHistoryDrawerOpen] = useState(false);
  const [prompt, setPrompt] = useState("");
  const [, setSession] = useState<ChatSessionState>(createIdleSession());

  const authSessionQuery = useQuery({
    queryKey: ["chat", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const routeSearch = typeof window === "undefined" ? "" : window.location.search;
  const routeScopeId = useMemo(
    () => readChatQueryValue("scopeId", routeSearch),
    [routeSearch]
  );
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data]
  );
  const authenticatedScopeId = resolvedScope?.scopeId.trim() || "";
  const scopeMismatch = Boolean(
    authSessionQuery.isSuccess &&
      routeScopeId &&
      authenticatedScopeId &&
      routeScopeId !== authenticatedScopeId
  );
  const scopeId =
    authSessionQuery.isSuccess && !scopeMismatch ? authenticatedScopeId : "";
  const canStartChat = Boolean(
    authSessionQuery.isSuccess &&
      authSessionQuery.data?.enabled === true &&
      authSessionQuery.data.authenticated === true &&
      scopeId
  );
  const chatCreationUnavailable = Boolean(
    authSessionQuery.isSuccess &&
      authSessionQuery.data?.enabled === false &&
      scopeId
  );
  const scopeLabelId = scopeMismatch ? routeScopeId : scopeId;
  if (scopeIdentityRef.current !== scopeId) {
    scopeIdentityRef.current = scopeId;
    scopeEpochRef.current += 1;
  }
  const scopeStateIsCurrent = conversationStateScopeId === scopeId;
  const activeConversation = scopeStateIsCurrent
    ? storedActiveConversation
    : null;
  const scopedDeletedConversationIds = scopeStateIsCurrent
    ? deletedConversationIds
    : EMPTY_CONVERSATION_IDS;
  const scopedPendingConversations = scopeStateIsCurrent
    ? pendingConversations
    : EMPTY_PENDING_CONVERSATIONS;
  const conversationsQuery = useQuery({
    enabled: Boolean(scopeId),
    queryFn: () => chatHistoryApi.listConversationMetas(scopeId),
    queryKey: ["chat-history", scopeId],
    retry: false,
  });
  const conversations = (conversationsQuery.data ?? []).filter(
    (conversation) => !scopedDeletedConversationIds.has(conversation.id)
  );
  const isStreaming =
    activeConversation?.status === "streaming" ||
    activeConversation?.status === "creating";
  const studioJump = resolveStudioJump(activeConversation?.target);
  const visibleConversations = useMemo<ConversationListItem[]>(() => {
    const activeConversationId = activeConversation?.conversationId;
    const liveConversations = [
      ...(activeConversationId && activeConversation
        ? [activeConversation]
        : []),
      ...[...scopedPendingConversations.values()]
        .map((pending) => pending.conversation)
        .filter(
          (conversation) =>
            conversation.conversationId !== activeConversationId &&
            !scopedDeletedConversationIds.has(conversation.conversationId ?? "")
        ),
    ];
    const liveById = new Map(
      liveConversations.flatMap((conversation) =>
        conversation.conversationId
          ? [[conversation.conversationId, conversation] as const]
          : []
      )
    );
    const serverIds = new Set(conversations.map((conversation) => conversation.id));
    const serverItems = conversations.map((conversation) => {
      const pending = scopedPendingConversations.get(conversation.id);
      return {
        ...conversation,
        ...(pending ? { historyReconciliation: pending.reconciliation } : {}),
        ...(liveById.has(conversation.id)
          ? { liveStatus: liveById.get(conversation.id)?.status }
          : {}),
      };
    });
    const overlayItems = liveConversations.flatMap<ConversationListItem>(
      (conversation) => {
        const conversationId = conversation.conversationId;
        if (!conversationId || serverIds.has(conversationId)) {
          return [];
        }

        const timestamps = conversation.messages.map((message) => message.timestamp);
        return [
          {
            createdAt: new Date(timestamps[0] ?? Date.now()).toISOString(),
            ...(scopedPendingConversations.has(conversationId)
              ? {
                  historyReconciliation:
                    scopedPendingConversations.get(conversationId)
                      ?.reconciliation,
                }
              : {}),
            id: conversationId,
            liveStatus: conversation.status,
            messageCount: conversation.expectedTurnCount,
            serviceId: "",
            serviceKind: "",
            title: conversation.title,
            updatedAt: new Date(
              timestamps[timestamps.length - 1] ?? Date.now()
            ).toISOString(),
          },
        ];
      }
    );
    return [...overlayItems, ...serverItems];
  }, [
    activeConversation,
    conversations,
    scopedDeletedConversationIds,
    scopedPendingConversations,
  ]);
  const activeHistoryReconciliation = activeConversation?.conversationId
    ? scopedPendingConversations.get(activeConversation.conversationId)
        ?.reconciliation
    : undefined;
  const isConversationActionDisabled =
    !canStartChat ||
    detailLoadState.status === "loading" ||
    Boolean(activeHistoryReconciliation);
  const hasFailedHistoryReconciliation = [
    ...scopedPendingConversations.values(),
  ].some((pending) => pending.reconciliation.status === "failed");
  const hasPendingHistoryReconciliation = [
    ...scopedPendingConversations.values(),
  ].some((pending) => pending.reconciliation.status === "pending");

  useLayoutEffect(() => {
    abortControllerRef.current?.abort();
    createRecoveryControllerRef.current?.abort();
    createRecoveryControllerRef.current = null;
    for (const controller of reconciliationControllersRef.current.values()) {
      controller.abort();
    }
    reconciliationControllersRef.current.clear();
    detailRequestRef.current = createClientId();
    activeConversationRef.current = null;
    setConversationStateScopeId(scopeId);
    setActiveConversation(null);
    setDeleteTarget(null);
    setDeletingConversation(false);
    setDeletedConversationIds(new Set());
    setPendingConversations(new Map());
    setDetailLoadState({ status: "idle" });
    setHistoryDrawerOpen(false);
    setPrompt("");
    setSession(createIdleSession(scopeId));
  }, [scopeId]);

  useEffect(() => {
    activeConversationRef.current = activeConversation;
  }, [activeConversation]);

  useEffect(() => {
    scrollAnchorRef.current?.scrollIntoView?.({
      behavior: "smooth",
      block: "end",
    });
  }, [activeConversation?.messages]);

  useEffect(
    () => () => {
      scopeEpochRef.current += 1;
      abortControllerRef.current?.abort();
      createRecoveryControllerRef.current?.abort();
      for (const controller of reconciliationControllersRef.current.values()) {
        controller.abort();
      }
      reconciliationControllersRef.current.clear();
    },
    []
  );

  useEffect(() => {
    if (typeof document === "undefined") {
      return undefined;
    }

    document.body.classList.add("aevatar-chat-page-host");
    return () => {
      document.body.classList.remove("aevatar-chat-page-host");
    };
  }, []);

  const restoreConversation = useCallback(
    async (conversationId: string) => {
      if (!scopeId || isStreaming) {
        return;
      }

      abortControllerRef.current?.abort();
      createRecoveryControllerRef.current?.abort();
      createRecoveryControllerRef.current = null;
      const pendingConversation = pendingConversations.get(conversationId);
      if (pendingConversation) {
        detailRequestRef.current = createClientId();
        activeConversationRef.current = pendingConversation.conversation;
        setActiveConversation(pendingConversation.conversation);
        setDetailLoadState({ status: "idle" });
        setPrompt("");
        setSession(createIdleSession(scopeId));
        return;
      }

      const meta = conversations.find((item) => item.id === conversationId);
      const requestId = createClientId();
      const placeholder: ConversationState = {
        clientId: createClientId(),
        conversationId,
        expectedTurnCount: meta?.messageCount ?? 0,
        messages: [],
        sessionId: createClientId(),
        status: "completed_text",
        title: meta?.title || t("pages.chat.index.newChat", "New chat"),
      };
      detailRequestRef.current = requestId;
      activeConversationRef.current = placeholder;
      setActiveConversation(placeholder);
      setDetailLoadState({ status: "loading" });
      setPrompt("");
      setSession(createIdleSession(scopeId));

      try {
        const detail = await chatHistoryApi.loadConversation(
          scopeId,
          conversationId
        );
        if (detailRequestRef.current !== requestId) {
          return;
        }

        const restoredConversation: ConversationState = {
          ...placeholder,
          messages: hydrateStoredMessages(detail.messages),
          stateVersion: detail.stateVersion,
          status: resolveStoredConversationStatus(detail.messages),
        };
        activeConversationRef.current = restoredConversation;
        setActiveConversation(restoredConversation);
        setDetailLoadState({ status: "idle" });
        setSession({
          ...createIdleSession(scopeId),
          status: detail.messages.length > 0 ? "success" : "idle",
          updatedAt: meta?.updatedAt ? Date.parse(meta.updatedAt) : undefined,
        });
      } catch (error) {
        if (detailRequestRef.current !== requestId) {
          return;
        }

        setDetailLoadState({ message: errorMessage(error), status: "error" });
      }
    },
    [conversations, isStreaming, pendingConversations, scopeId]
  );

  const handleNewChat = useCallback(() => {
    if (isStreaming) {
      return;
    }

    const currentConversation = activeConversationRef.current;
    if (isEmptyDraftConversation(currentConversation)) {
      setHistoryDrawerOpen(false);
      return;
    }

    abortControllerRef.current?.abort();
    createRecoveryControllerRef.current?.abort();
    createRecoveryControllerRef.current = null;
    detailRequestRef.current = createClientId();
    const conversation = createDraftConversation();
    activeConversationRef.current = conversation;
    setActiveConversation(conversation);
    setDetailLoadState({ status: "idle" });
    setHistoryDrawerOpen(false);
    setPrompt("");
    setSession(createIdleSession(scopeId));
  }, [isStreaming, scopeId]);

  const handleSelectConversation = useCallback(
    async (conversationId: string) => {
      if (isStreaming) {
        return;
      }

      setHistoryDrawerOpen(false);
      if (
        activeConversationRef.current?.conversationId === conversationId &&
        detailLoadState.status !== "error"
      ) {
        return;
      }

      await restoreConversation(conversationId);
    },
    [detailLoadState.status, isStreaming, restoreConversation]
  );

  const handleDeleteConversation = useCallback(async () => {
    if (!scopeId || !deleteTarget || deletingConversation || isStreaming) {
      return;
    }

    setDeletingConversation(true);
    const deleteScopeEpoch = scopeEpochRef.current;
    try {
      await chatHistoryApi.deleteConversation(scopeId, deleteTarget.id);
      if (scopeEpochRef.current !== deleteScopeEpoch) {
        return;
      }
      await queryClient.cancelQueries({ queryKey: ["chat-history", scopeId] });
      setDeletedConversationIds((current) => {
        const next = new Set(current);
        next.add(deleteTarget.id);
        return next;
      });
      setPendingConversations((current) => {
        const next = new Map(current);
        next.delete(deleteTarget.id);
        return next;
      });
      reconciliationControllersRef.current.get(deleteTarget.id)?.abort();
      reconciliationControllersRef.current.delete(deleteTarget.id);
      if (activeConversationRef.current?.conversationId === deleteTarget.id) {
        activeConversationRef.current = null;
        setActiveConversation(null);
        setDetailLoadState({ status: "idle" });
        setSession(createIdleSession(scopeId));
      }
      setDeleteTarget(null);
      await queryClient.invalidateQueries({ queryKey: ["chat-history", scopeId] });
    } catch {
      if (scopeEpochRef.current !== deleteScopeEpoch) {
        return;
      }
      toast.error(
        t(
          "pages.chat.index.deleteChatFailed",
          "Conversation could not be deleted",
        ),
      );
    } finally {
      if (scopeEpochRef.current === deleteScopeEpoch) {
        setDeletingConversation(false);
      }
    }
  }, [deleteTarget, deletingConversation, isStreaming, queryClient, scopeId, toast]);

  const reconcileConversation = useCallback(
    (conversation: ConversationState) => {
      if (!scopeId || !conversation.conversationId) {
        return;
      }

      const conversationId = conversation.conversationId;
      reconciliationControllersRef.current.get(conversationId)?.abort();
      const controller = new AbortController();
      reconciliationControllersRef.current.set(conversationId, controller);
      setPendingConversations((current) => {
        const next = new Map(current);
        next.set(conversationId, {
          conversation,
          reconciliation: { status: "pending" },
        });
        return next;
      });
      const reconciliationScopeEpoch = scopeEpochRef.current;
      const delaysMs = [0, 300, 900, 1_800];

      void (async () => {
        let lastError: unknown;
        for (const delayMs of delaysMs) {
          try {
            await abortableDelay(delayMs, controller.signal);
            const nextConversations =
              await chatHistoryApi.listConversationMetas(
                scopeId,
                controller.signal
              );
            if (
              controller.signal.aborted ||
              scopeEpochRef.current !== reconciliationScopeEpoch
            ) {
              return;
            }

            queryClient.setQueryData(
              ["chat-history", scopeId],
              nextConversations
            );
            const serverMeta = nextConversations.find(
              (item) => item.id === conversationId
            );
            if (!serverMeta) {
              continue;
            }

            const detail = await chatHistoryApi.loadConversation(
              scopeId,
              conversationId,
              controller.signal
            );
            if (
              controller.signal.aborted ||
              scopeEpochRef.current !== reconciliationScopeEpoch
            ) {
              return;
            }
            const observedExpectedTurn = conversation.latestTurnId
              ? detail.messages.some(
                  (message) =>
                    message.role === "assistant" &&
                    message.turnId === conversation.latestTurnId
                )
              : serverMeta.messageCount >= conversation.expectedTurnCount;
            if (
              !observedExpectedTurn ||
              !isContinuationStateVersion(detail.stateVersion) ||
              detail.stateVersion < (conversation.stateVersion ?? 0)
            ) {
              continue;
            }

            setPendingConversations((current) => {
              const next = new Map(current);
              next.delete(conversationId);
              return next;
            });
            setActiveConversation((current) => {
              if (current?.clientId !== conversation.clientId) {
                return current;
              }

              const next = {
                ...current,
                expectedTurnCount: Math.max(
                  serverMeta.messageCount,
                  conversation.expectedTurnCount
                ),
                stateVersion: Math.max(
                  current.stateVersion ?? 0,
                  detail.stateVersion
                ),
                messages: hydrateAuthoritativeMessages(
                  detail.messages,
                  current.messages,
                  conversation.latestTurnId
                ),
                title: serverMeta.title || current.title,
              };
              activeConversationRef.current = next;
              return next;
            });
            return;
          } catch (error) {
            if (
              controller.signal.aborted ||
              scopeEpochRef.current !== reconciliationScopeEpoch
            ) {
              return;
            }
            lastError = error;
          }
        }

        const failureMessage = lastError
          ? errorMessage(lastError)
          : t(
              "pages.chat.index.historySaveNotObserved",
              "History save was not observed by the server."
            );
        setPendingConversations((current) => {
          const pending = current.get(conversationId);
          if (pending?.conversation.clientId !== conversation.clientId) {
            return current;
          }

          const next = new Map(current);
          next.set(conversationId, {
            conversation: pending.conversation,
            reconciliation: {
              message: failureMessage,
              retryable: true,
              status: "failed",
            },
          });
          return next;
        });
      })().finally(() => {
        if (
          reconciliationControllersRef.current.get(conversationId) === controller
        ) {
          reconciliationControllersRef.current.delete(conversationId);
        }
      });
    },
    [queryClient, scopeId]
  );

  const handleRetryReconciliation = useCallback(
    (conversationId: string) => {
      const pending = pendingConversations.get(conversationId);
      if (
        !pending ||
        pending.reconciliation.status !== "failed" ||
        !pending.reconciliation.retryable
      ) {
        return;
      }

      reconcileConversation(pending.conversation);
    },
    [pendingConversations, reconcileConversation]
  );

  const runChat = useCallback(
    async (conversation: ConversationState, input: string) => {
      if (!canStartChat || !scopeId || isStreaming) {
        return;
      }

      const trimmedInput = input.trim();
      if (!trimmedInput) {
        return;
      }

      if (
        conversation.conversationId &&
        pendingConversations.has(conversation.conversationId)
      ) {
        return;
      }
      if (
        conversation.conversationId &&
        !isContinuationStateVersion(conversation.stateVersion)
      ) {
        setSession({
          ...createIdleSession(scopeId),
          error: t(
            "pages.chat.index.historySynchronizing",
            "Conversation history is still synchronizing. Try again shortly."
          ),
          status: "error",
          updatedAt: Date.now(),
        });
        reconcileConversation(conversation);
        return;
      }

      const runScopeEpoch = scopeEpochRef.current;
      detailRequestRef.current = createClientId();
      setDetailLoadState({ status: "idle" });
      const userMessage = createChatMessage("user", trimmedInput);
      const assistantMessageId = createClientId();
      const assistantMessage: ChatMessage = {
        content: "",
        events: [],
        id: assistantMessageId,
        role: "assistant",
        status: "streaming",
        steps: [],
        thinking: "",
        timestamp: Date.now(),
        toolCalls: [],
      };
      const title =
        conversation.title === t("pages.chat.index.newChat", "New chat")
          ? trimTitle(trimmedInput)
          : conversation.title;
      const nextStatus: LocalChatStatus =
        conversation.status === "needs_confirmation" ? "creating" : "streaming";
      const createCommandId = conversation.conversationId
        ? undefined
        : !conversation.createRequestPrompt ||
            conversation.createRequestPrompt === trimmedInput
          ? conversation.createCommandId ?? conversation.clientId
          : createClientId();
      const startedConversation: ConversationState = {
        ...conversation,
        createCommandId,
        createRequestPrompt: conversation.conversationId
          ? undefined
          : trimmedInput,
        latestTurnId: undefined,
        messages: [...conversation.messages, userMessage, assistantMessage],
        status: nextStatus,
        title,
      };
      const rawFrames: unknown[] = [];
      const accumulator = createRuntimeEventAccumulator();
      let receivedChatHistoryContext = false;
      let acceptedChatHistoryContext: ReturnType<
        typeof extractChatHistoryContext
      > = null;
      let createRecoveryAttempted = false;
      let latestRefreshedDetail:
        | Awaited<ReturnType<typeof chatHistoryApi.loadConversation>>
        | undefined;
      let streamingConversation = startedConversation;

      abortControllerRef.current?.abort();
      createRecoveryControllerRef.current?.abort();
      createRecoveryControllerRef.current = null;
      if (conversation.conversationId) {
        reconciliationControllersRef.current
          .get(conversation.conversationId)
          ?.abort();
        reconciliationControllersRef.current.delete(conversation.conversationId);
      }
      const controller = new AbortController();
      abortControllerRef.current = controller;
      setPrompt("");
      activeConversationRef.current = startedConversation;
      setActiveConversation(startedConversation);
      setSession({
        ...createIdleSession(scopeId),
        status: "running",
        updatedAt: Date.now(),
      });

      try {
        const response = await startChatStreamWithHistoryRefreshRetry(
          {
            commandId: createCommandId,
            conversation: conversation.conversationId
              ? {
                  conversationId: conversation.conversationId,
                  minimumStateVersion: conversation.stateVersion as number,
                }
              : { conversationId: null },
            prompt: trimmedInput,
            sessionId: conversation.sessionId,
          },
          controller.signal,
          conversation.conversationId
            ? {
                refreshMinimumStateVersion: async (signal) => {
                  const detail = await chatHistoryApi.loadConversation(
                    scopeId,
                    conversation.conversationId as string,
                    signal
                  );
                  if (
                    detail.stateVersion >= (conversation.stateVersion as number) &&
                    (!latestRefreshedDetail ||
                      detail.stateVersion > latestRefreshedDetail.stateVersion)
                  ) {
                    latestRefreshedDetail = detail;
                  }
                  return detail.stateVersion;
                },
              }
            : undefined
        );

        for await (const frame of readChatStreamFrames(response, {
          signal: controller.signal,
        })) {
          if (scopeEpochRef.current !== runScopeEpoch) {
            controller.abort();
            return;
          }
          rawFrames.push(frame.raw);
          const chatHistoryContext = extractChatHistoryContext(frame.raw);
          if (chatHistoryContext) {
            if (chatHistoryContext.scopeId !== scopeId) {
              throw new Error("Chat History context does not match the active scope.");
            }
            if (
              conversation.conversationId &&
              chatHistoryContext.conversationId !== conversation.conversationId
            ) {
              throw new Error("Chat History returned a different conversation identity.");
            }
            if (
              acceptedChatHistoryContext &&
              (chatHistoryContext.scopeId !==
                acceptedChatHistoryContext.scopeId ||
                chatHistoryContext.conversationId !==
                  acceptedChatHistoryContext.conversationId ||
                chatHistoryContext.turnId !==
                  acceptedChatHistoryContext.turnId ||
                chatHistoryContext.stateVersion !==
                  acceptedChatHistoryContext.stateVersion)
            ) {
              throw new Error("Chat History context changed during the stream.");
            }
            if (!acceptedChatHistoryContext) {
              acceptedChatHistoryContext = chatHistoryContext;
              receivedChatHistoryContext = true;
              const acceptedConversationStateVersion = conversation.conversationId
                ? chatHistoryContext.stateVersion
                : 0;
              streamingConversation = {
                ...streamingConversation,
                conversationId: chatHistoryContext.conversationId,
                expectedTurnCount: conversation.expectedTurnCount + 1,
                latestTurnId: chatHistoryContext.turnId,
                stateVersion: Math.max(
                  streamingConversation.stateVersion ?? 0,
                  latestRefreshedDetail?.stateVersion ?? 0,
                  acceptedConversationStateVersion
                ),
              };
              activeConversationRef.current = streamingConversation;
              setActiveConversation((current) =>
                current?.clientId === conversation.clientId
                  ? streamingConversation
                  : current
              );
            }
          }
          if (!frame.event) {
            continue;
          }

          applyRuntimeEvent(accumulator, frame.event);
          if (isRawObserved(frame.event)) {
            continue;
          }

          const patch = buildAssistantMessagePatch(
            accumulator,
            accumulator.errorText ? "error" : "streaming"
          );
          const patchedConversation: ConversationState = {
            ...streamingConversation,
            messages: streamingConversation.messages.map((message) =>
              message.id === assistantMessageId
                ? { ...message, ...patch }
                : message
            ),
          };
          streamingConversation = patchedConversation;
          activeConversationRef.current = patchedConversation;
          setActiveConversation((current) => {
            if (!current || current.clientId !== conversation.clientId) {
              return current;
            }

            return patchedConversation;
          });
          setSession(
            buildSessionFromAccumulator(
              scopeId,
              accumulator,
              accumulator.errorText ? "error" : "running"
            )
          );
        }

        if (controller.signal.aborted) {
          throw controller.signal.reason;
        }
        if (scopeEpochRef.current !== runScopeEpoch) {
          return;
        }
        if (!receivedChatHistoryContext && createCommandId) {
          createRecoveryAttempted = true;
          const recovery = await recoverCreateIdentity(
            scopeId,
            createCommandId,
            controller.signal
          );
          if (recovery) {
            receivedChatHistoryContext = true;
            streamingConversation = bindCreateRecovery(
              streamingConversation,
              recovery
            );
            activeConversationRef.current = streamingConversation;
            setActiveConversation((current) =>
              current?.clientId === conversation.clientId
                ? streamingConversation
                : current
            );
          }
        }
        if (!receivedChatHistoryContext) {
          throw new Error(
            t(
              "pages.chat.index.missingChatHistoryContext",
              "Chat completed without a conversation context."
            )
          );
        }

        const artifacts = extractChatStreamArtifacts(rawFrames);
        const finalAssistantStatus: ChatMessage["status"] = accumulator.errorText
          ? "error"
          : "complete";
        const finalTarget = artifacts.target || streamingConversation.target;
        const finalUsage = artifacts.usage || streamingConversation.usage;
        const finalContent = accumulator.finalOutput || accumulator.assistantText;
        const finalStatus: LocalChatStatus = accumulator.errorText
          ? "error"
          : resolveStudioJump(finalTarget)
            ? "completed_with_studio_target"
            : shouldAskForConfirmation(finalContent)
              ? "needs_confirmation"
              : "completed_text";
        const finalConversation: ConversationState = {
          ...streamingConversation,
          messages: streamingConversation.messages.map((message) =>
            message.id === assistantMessageId
              ? {
                  ...message,
                  ...buildAssistantMessagePatch(
                    accumulator,
                    finalAssistantStatus
                  ),
                }
              : message
          ),
          status: finalStatus,
          target: finalTarget,
          usage: finalUsage,
        };
        activeConversationRef.current = finalConversation;
        setActiveConversation((current) =>
          current?.clientId === conversation.clientId ? finalConversation : current
        );
        setSession(
          buildSessionFromAccumulator(
            scopeId,
            accumulator,
            accumulator.errorText ? "error" : "success"
          )
        );
        reconcileConversation(finalConversation);
      } catch (error) {
        if (scopeEpochRef.current !== runScopeEpoch) {
          return;
        }
        const shouldRecoverCreate = Boolean(
          !conversation.conversationId &&
            !receivedChatHistoryContext &&
            !createRecoveryAttempted &&
            createCommandId &&
            chatRequestMayHaveBeenAccepted(error)
        );
        if (shouldRecoverCreate && !controller.signal.aborted) {
          try {
            const recovery = await recoverCreateIdentity(
              scopeId,
              createCommandId as string,
              controller.signal
            );
            if (recovery) {
              receivedChatHistoryContext = true;
              streamingConversation = bindCreateRecovery(
                streamingConversation,
                recovery
              );
            }
          } catch {
            // Preserve the accepted request's original stream failure.
          }
        }
        const isAmbiguousContinuation = Boolean(
          conversation.conversationId &&
            !receivedChatHistoryContext &&
            chatRequestMayHaveBeenAccepted(error)
        );
        const message = isAmbiguousContinuation
          ? t(
              "pages.chat.index.continuationContextMissing",
              "The continuation may have been accepted, but its turn identity was not received. Reload this page before continuing."
            )
          : controller.signal.aborted && !accumulator.errorText
            ? t("pages.chat.index.chatStopped", "Chat stopped.")
            : error instanceof Error
              ? error.message
              : String(error);
        accumulator.errorText = message;
        const failedMessages = streamingConversation.messages.map((entry) =>
          entry.id === assistantMessageId
            ? {
                ...entry,
                ...buildAssistantMessagePatch(accumulator, "error"),
              }
            : entry
        );
        const refreshedDetail = latestRefreshedDetail;
        const authoritativeMessages = refreshedDetail
          ? hydrateAuthoritativeMessages(
              refreshedDetail.messages,
              streamingConversation.messages,
              undefined
            )
          : undefined;
        const authoritativeMessageIds = new Set(
          authoritativeMessages?.map((entry) => entry.id) ?? []
        );
        const failedAttemptMessages = failedMessages.filter(
          (entry) =>
            (entry.id === userMessage.id || entry.id === assistantMessageId) &&
            !authoritativeMessageIds.has(entry.id)
        );
        const failedConversation: ConversationState = {
          ...streamingConversation,
          ...(refreshedDetail
            ? {
                ...(receivedChatHistoryContext
                  ? {}
                  : { latestTurnId: undefined }),
                stateVersion: Math.max(
                  streamingConversation.stateVersion ?? 0,
                  refreshedDetail.stateVersion
                ),
              }
            : {}),
          messages: authoritativeMessages
            ? [...authoritativeMessages, ...failedAttemptMessages]
            : failedMessages,
          status: "error",
        };
        activeConversationRef.current = failedConversation;
        setActiveConversation((current) =>
          current?.clientId === conversation.clientId ? failedConversation : current
        );
        setSession(buildSessionFromAccumulator(scopeId, accumulator, "error"));
        if (
          failedConversation.conversationId &&
          receivedChatHistoryContext
        ) {
          reconcileConversation(failedConversation);
        } else if (
          failedConversation.conversationId &&
          isAmbiguousContinuation
        ) {
          setPendingConversations((current) => {
            const next = new Map(current);
            next.set(failedConversation.conversationId as string, {
              conversation: failedConversation,
              reconciliation: {
                message,
                retryable: false,
                status: "failed",
              },
            });
            return next;
          });
        } else if (shouldRecoverCreate && controller.signal.aborted) {
          const recoveryController = new AbortController();
          createRecoveryControllerRef.current = recoveryController;
          void recoverCreateIdentity(
            scopeId,
            createCommandId as string,
            recoveryController.signal
          )
            .then((recovery) => {
              if (
                !recovery ||
                recoveryController.signal.aborted ||
                scopeEpochRef.current !== runScopeEpoch
              ) {
                return;
              }

              void queryClient.invalidateQueries({
                queryKey: ["chat-history", scopeId],
              });
              const current = activeConversationRef.current;
              if (
                current?.clientId !== conversation.clientId ||
                current.conversationId ||
                current.createCommandId !== createCommandId
              ) {
                return;
              }

              const recoveredConversation = bindCreateRecovery(current, recovery);
              activeConversationRef.current = recoveredConversation;
              setActiveConversation(recoveredConversation);
              reconcileConversation(recoveredConversation);
            })
            .catch(() => undefined)
            .finally(() => {
              if (createRecoveryControllerRef.current === recoveryController) {
                createRecoveryControllerRef.current = null;
              }
            });
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }
    },
    [
      canStartChat,
      isStreaming,
      pendingConversations,
      queryClient,
      reconcileConversation,
      scopeId,
    ]
  );

  const handleSend = useCallback(() => {
    const conversation = activeConversation ?? createDraftConversation();

    void runChat(conversation, prompt);
  }, [activeConversation, prompt, runChat]);

  const handleConfirmCreate = useCallback(() => {
    if (!activeConversation) {
      return;
    }

    void runChat(
      activeConversation,
      t("pages.chat.index.confirmPrompt", "Confirm. Please create it now.")
    );
  }, [activeConversation, runChat]);

  const handleStop = useCallback(() => {
    abortControllerRef.current?.abort();
  }, []);

  const handleOpenTarget = useCallback(() => {
    if (!studioJump) {
      return;
    }

    history.push(studioJump.href);
  }, [studioJump]);

  const messageCount = activeConversation?.messages.length ?? 0;
  const historyRail = (
    <>
      <div
        style={{
          borderBottom: `1px solid ${token.colorBorderSecondary}`,
          display: "flex",
          flexDirection: "column",
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
          {t("pages.chat.index.newChatAction", "New Chat")}
        </Button>
        <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 12 }}>
          {hasFailedHistoryReconciliation
            ? t(
                "pages.chat.index.historySaveNeedsAttention",
                "Some history changes are not confirmed."
              )
            : hasPendingHistoryReconciliation
              ? t(
                  "pages.chat.index.historySavePending",
                  "Saving recent history..."
                )
              : t(
                  "pages.chat.index.historyStoredInWorkspace",
                  "History is saved to this workspace."
                )}
        </Typography.Text>
      </div>

      <div
        aria-busy={conversationsQuery.isLoading}
        style={{
          flex: 1,
          minHeight: 0,
          overflow: "auto",
          padding: "8px 6px 10px",
        }}
      >
        {conversationsQuery.isLoading ? (
          <div
            style={{
              alignItems: "center",
              display: "flex",
              justifyContent: "center",
              minHeight: 120,
            }}
          >
            <Spin
              description={t(
                "pages.chat.index.loadingHistory",
                "Loading chat history"
              )}
              size="small"
            />
          </div>
        ) : conversationsQuery.isError ? (
          <Alert
            action={
              <Button
                aria-label={t("pages.chat.index.retryHistory", "Retry chat history")}
                icon={<ReloadOutlined />}
                onClick={() => void conversationsQuery.refetch()}
                size="small"
                type="text"
              />
            }
            description={errorMessage(conversationsQuery.error)}
            message={t(
              "pages.chat.index.failedToLoadHistory",
              "Chat history could not be loaded"
            )}
            showIcon
            type="error"
          />
        ) : visibleConversations.length === 0 ? (
          <Empty
            description={t("pages.chat.index.noChatHistory", "No chat history")}
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            style={{ marginTop: 24 }}
          />
        ) : (
          <Space direction="vertical" size={6} style={{ width: "100%" }}>
            {visibleConversations.map((conversation) => {
              const active =
                conversation.id === activeConversation?.conversationId;
              return (
                <div
                  className="aevatar-chat-history-item"
                  key={conversation.id}
                  style={{
                    background: active ? token.colorPrimaryBg : "transparent",
                    border: `1px solid ${
                      active ? token.colorPrimaryBorder : "transparent"
                    }`,
                    borderRadius: token.borderRadius,
                    boxShadow: active
                      ? `inset 3px 0 0 ${token.colorPrimary}`
                      : undefined,
                    display: "flex",
                    gap: 4,
                    padding: 4,
                    width: "100%",
                  }}
                >
                  <button
                    aria-current={active ? "page" : undefined}
                    aria-label={conversation.title}
                    className="aevatar-chat-history-select"
                    disabled={isStreaming}
                    onClick={() => void handleSelectConversation(conversation.id)}
                    style={{
                      alignItems: "flex-start",
                      background: "transparent",
                      border: 0,
                      color: "inherit",
                      cursor: isStreaming ? "not-allowed" : "pointer",
                      display: "flex",
                      flex: 1,
                      gap: 8,
                      minHeight: 40,
                      minWidth: 0,
                      padding: "5px 4px",
                      textAlign: "left",
                    }}
                    type="button"
                  >
                    <MessageOutlined
                      style={{
                        color: active
                          ? token.colorPrimary
                          : token.colorTextTertiary,
                        flex: "0 0 auto",
                        fontSize: 14,
                        marginTop: 2,
                      }}
                    />
                    <span style={{ flex: 1, minWidth: 0 }}>
                      <span
                        style={{
                          color: token.colorText,
                          display: "block",
                          fontSize: 13,
                          fontWeight: 600,
                          overflow: "hidden",
                          textOverflow: "ellipsis",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {conversation.title}
                      </span>
                      <span
                        style={{
                          color: token.colorTextTertiary,
                          display: "flex",
                          fontSize: 11,
                          gap: 6,
                          lineHeight: 1.35,
                          marginTop: 3,
                        }}
                      >
                        <span>
                          {conversation.historyReconciliation?.status ===
                          "failed"
                            ? t(
                                "pages.chat.index.historySaveFailed",
                                "Save not confirmed"
                              )
                            : conversation.historyReconciliation?.status ===
                              "pending"
                            ? t(
                                "pages.chat.index.historySavePendingShort",
                                "Saving history"
                              )
                            : conversation.liveStatus === "streaming" ||
                              conversation.liveStatus === "creating"
                            ? formatStatusLabel(conversation.liveStatus)
                            : formatTurnCount(conversation.messageCount)}
                        </span>
                        <span>{formatRelativeTime(conversation.updatedAt)}</span>
                      </span>
                    </span>
                  </button>
                  {conversation.historyReconciliation?.status === "failed" &&
                  conversation.historyReconciliation.retryable ? (
                    <AevatarTooltip
                      title={t(
                        "pages.chat.index.retryHistorySave",
                        "Retry saving {title}",
                        { title: conversation.title }
                      )}
                    >
                      <Button
                        aria-label={t(
                          "pages.chat.index.retryHistorySave",
                          "Retry saving {title}",
                          { title: conversation.title }
                        )}
                        disabled={isStreaming}
                        icon={<ReloadOutlined />}
                        onClick={() =>
                          handleRetryReconciliation(conversation.id)
                        }
                        style={{ minHeight: 40, minWidth: 40 }}
                        type="text"
                      />
                    </AevatarTooltip>
                  ) : null}
                  <AevatarTooltip
                    title={t("pages.chat.index.deleteChat", "Delete {title}", {
                      title: conversation.title,
                    })}
                  >
                    <Button
                      aria-label={t("pages.chat.index.deleteChat", "Delete {title}", {
                        title: conversation.title,
                      })}
                      danger
                      disabled={isStreaming}
                      icon={<DeleteOutlined />}
                      onClick={() => {
                        setDeleteTarget(conversation);
                      }}
                      style={{ minHeight: 40, minWidth: 40 }}
                      type="text"
                    />
                  </AevatarTooltip>
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
      title={t("pages.chat.index.title", "Chat")}
    >
      <div
        className="aevatar-chat-page"
        style={{
          background: token.colorBgContainer,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: token.borderRadius,
          boxShadow: "0 1px 2px rgba(15, 23, 42, 0.04)",
          display: "grid",
          flex: 1,
          height: "100%",
          minHeight: 0,
          overflow: "hidden",
        }}
      >
        <aside
          className="aevatar-chat-history-desktop"
          style={{
            background: token.colorBgContainer,
            borderRight: `1px solid ${token.colorBorderSecondary}`,
            display: "flex",
            flexDirection: "column",
            minHeight: 0,
          }}
        >
          {historyRail}
        </aside>

        <main
          className="aevatar-chat-main"
          style={{
            background: token.colorBgContainer,
            display: "flex",
            flexDirection: "column",
            minHeight: 0,
          }}
        >
          <div
            className="aevatar-chat-main-header"
            style={{
              alignItems: "center",
              borderBottom: `1px solid ${token.colorBorderSecondary}`,
              display: "flex",
              gap: 12,
              justifyContent: "space-between",
              minHeight: 54,
              padding: "10px 14px",
            }}
          >
            <AevatarTooltip
              title={t("pages.chat.index.openHistory", "Open chat history")}
            >
              <Button
                aria-label={t("pages.chat.index.openHistory", "Open chat history")}
                className="aevatar-chat-history-trigger"
                icon={<HistoryOutlined />}
                onClick={() => setHistoryDrawerOpen(true)}
                style={{ minHeight: 44, minWidth: 44 }}
                type="text"
              />
            </AevatarTooltip>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Typography.Text
                strong
                style={{
                  color: token.colorTextHeading,
                  display: "block",
                  fontSize: 18,
                  lineHeight: 1.25,
                  overflow: "hidden",
                  textOverflow: "ellipsis",
                  whiteSpace: "nowrap",
                }}
              >
                {activeConversation?.title || t("pages.chat.index.title", "Chat")}
              </Typography.Text>
              <Typography.Text
                style={{
                  color: token.colorTextTertiary,
                  display: "block",
                  fontSize: 12,
                  lineHeight: 1.4,
                  marginTop: 3,
                }}
              >
                {scopeLabelId
                  ? t("pages.chat.index.scopeValue", "Scope {scopeId}", {
                      scopeId: scopeLabelId,
                    })
                  : t("pages.chat.index.resolvingScope", "Resolving scope")}
              </Typography.Text>
            </div>
            <Space className="aevatar-chat-main-actions" wrap>
              {activeConversation ? (
                <Tag color={resolveStatusTone(activeConversation.status)}>
                  {formatStatusLabel(activeConversation.status)}
                </Tag>
              ) : null}
              {studioJump ? (
                <Button onClick={handleOpenTarget} type="primary">
                  {studioJump.label}
                </Button>
              ) : null}
            </Space>
          </div>

          {scopeMismatch ? (
            <Alert
              banner
              message={t(
                "pages.chat.index.scopeMismatch",
                "Requested scope {requestedScopeId} does not match authenticated scope {authenticatedScopeId}. Open Chat from the active workspace or sign in again.",
                {
                  authenticatedScopeId,
                  requestedScopeId: routeScopeId,
                }
              )}
              type="error"
            />
          ) : chatCreationUnavailable ? (
            <Alert
              banner
              message={t(
                "pages.chat.index.chatRequiresAuthentication",
                "Starting or continuing a chat requires a trusted authenticated scope. Existing chat history remains available to manage."
              )}
              type="info"
            />
          ) : !scopeId && !authSessionQuery.isLoading ? (
            <Alert
              banner
              message={t(
                "pages.chat.index.noScope",
                "No usable scope was resolved for this account. Refresh and try again."
              )}
              type="warning"
            />
          ) : null}

          {activeHistoryReconciliation?.status === "pending" ? (
            <Alert
              banner
              message={t(
                "pages.chat.index.historySavePending",
                "Saving recent history..."
              )}
              type="info"
            />
          ) : activeHistoryReconciliation?.status === "failed" &&
            activeConversation?.conversationId ? (
            <Alert
              action={
                activeHistoryReconciliation.retryable ? (
                  <Button
                    icon={<ReloadOutlined />}
                    onClick={() =>
                      handleRetryReconciliation(
                        activeConversation.conversationId as string
                      )
                    }
                    size="small"
                  >
                    {t("pages.chat.index.retry", "Retry")}
                  </Button>
                ) : undefined
              }
              banner
              description={activeHistoryReconciliation.message}
              message={t(
                "pages.chat.index.historySaveFailedLong",
                "History save was not confirmed"
              )}
              type="warning"
            />
          ) : null}

          <div
            style={{
              background: token.colorBgLayout,
              display: "flex",
              flexDirection: "column",
              flex: 1,
              minHeight: 0,
              overflow: "auto",
              padding: 16,
            }}
          >
            {detailLoadState.status === "loading" ? (
              <div
                aria-live="polite"
                style={{
                  alignItems: "center",
                  display: "flex",
                  flex: 1,
                  justifyContent: "center",
                  minHeight: 180,
                }}
              >
                <Spin
                  description={t(
                    "pages.chat.index.loadingConversation",
                    "Loading conversation"
                  )}
                />
              </div>
            ) : detailLoadState.status === "error" ? (
              <Alert
                action={
                  activeConversation?.conversationId ? (
                    <Button
                      icon={<ReloadOutlined />}
                      onClick={() =>
                        void restoreConversation(
                          activeConversation.conversationId as string
                        )
                      }
                      size="small"
                    >
                      {t("pages.chat.index.retry", "Retry")}
                    </Button>
                  ) : null
                }
                description={detailLoadState.message}
                message={t(
                  "pages.chat.index.failedToLoadConversation",
                  "Conversation could not be loaded"
                )}
                showIcon
                type="error"
              />
            ) : messageCount === 0 ? (
              <div
                style={{
                  alignItems: "center",
                  background: token.colorBgContainer,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: token.borderRadius,
                  color: token.colorTextTertiary,
                  display: "flex",
                  gap: 10,
                  lineHeight: 1.55,
                  margin: "8px 0 0",
                  maxWidth: 720,
                  padding: "14px 16px",
                  width: "100%",
                }}
              >
                <MessageOutlined
                  style={{
                    color: token.colorPrimary,
                    flex: "0 0 auto",
                    fontSize: 16,
                  }}
                />
                <Typography.Text style={{ color: token.colorTextSecondary }}>
                  {t(
                    "pages.chat.index.emptyDescription",
                    "Describe the Team, Member, or Workflow you want to create."
                  )}
                </Typography.Text>
              </div>
            ) : (
              <Space
                className="aevatar-chat-message-list"
                direction="vertical"
                size={14}
                style={{
                  marginInline: "auto",
                  maxWidth: 1440,
                  width: "100%",
                }}
              >
                {activeConversation?.messages.map((message) => (
                  <ChatMessageEntry key={message.id} message={message} />
                ))}
                {activeConversation?.status === "needs_confirmation" ? (
                  <div
                    style={{
                      background: token.colorWarningBg,
                      border: `1px solid ${token.colorWarningBorder}`,
                      borderRadius: token.borderRadius,
                      marginLeft: 34,
                      maxWidth: 760,
                      padding: 12,
                    }}
                  >
                    <Space direction="vertical" size={10}>
                      <Typography.Text strong>
                        {t(
                          "pages.chat.index.reviewPlan",
                          "Review the plan before creating resources."
                        )}
                      </Typography.Text>
                      <Button
                        disabled={isConversationActionDisabled}
                        icon={<SendOutlined />}
                        onClick={handleConfirmCreate}
                        type="primary"
                      >
                        {t("pages.chat.index.confirmAndCreate", "Confirm and create")}
                      </Button>
                    </Space>
                  </div>
                ) : null}
              </Space>
            )}
            <div ref={scrollAnchorRef} />
          </div>

          <div
            style={{
              background: token.colorBgContainer,
              borderTop: `1px solid ${token.colorBorderSecondary}`,
              padding: "10px 14px 12px",
            }}
          >
            {hasUsage(activeConversation?.usage) ? (
              <Space size={8} style={{ marginBottom: 10 }} wrap>
                {activeConversation?.usage?.totalTokens !== undefined ? (
                  <Tag>
                    {t("pages.chat.index.totalTokens", "{count} tokens", {
                      count: activeConversation.usage.totalTokens.toLocaleString(),
                    })}
                  </Tag>
                ) : null}
                {activeConversation?.usage?.promptTokens !== undefined ||
                activeConversation?.usage?.completionTokens !== undefined ? (
                  <Tag>
                    {t("pages.chat.index.tokenSplit", "{input} in / {output} out", {
                      input: activeConversation.usage?.promptTokens ?? 0,
                      output: activeConversation?.usage?.completionTokens ?? 0,
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
              isStreaming={isStreaming}
              onChange={setPrompt}
              onSend={handleSend}
              onStop={handleStop}
              placeholder={t(
                "pages.chat.index.composerPlaceholder",
                "Describe the workflow you want, or ask about the current setup..."
              )}
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
        title={t("pages.chat.index.historyTitle", "Chat history")}
      >
        <div
          style={{
            display: "flex",
            flexDirection: "column",
            height: "100%",
            minHeight: 0,
          }}
        >
          {historyRail}
        </div>
      </Drawer>

      <Modal
        cancelButtonProps={{ disabled: deletingConversation }}
        cancelText={t("pages.chat.index.cancel", "Cancel")}
        confirmLoading={deletingConversation}
        destroyOnHidden
        okButtonProps={{ danger: true, disabled: isStreaming }}
        onCancel={() => {
          if (!deletingConversation) {
            setDeleteTarget(null);
          }
        }}
        onOk={() => void handleDeleteConversation()}
        okText={t("pages.chat.index.delete", "Delete")}
        open={Boolean(deleteTarget)}
        title={t("pages.chat.index.deleteChatTitle", "Delete conversation?")}
      >
        <Typography.Paragraph>
          {t(
            "pages.chat.index.deleteChatDescription",
            'Delete "{title}" permanently? This conversation cannot be recovered.',
            { title: deleteTarget?.title || "" }
          )}
        </Typography.Paragraph>
      </Modal>
    </AevatarPageShell>
  );
};

export default ChatPage;
