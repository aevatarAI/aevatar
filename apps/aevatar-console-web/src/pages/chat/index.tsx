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
  Tooltip,
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
import { studioApi } from "@/shared/studio/api";
import { AevatarPageShell } from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import {
  extractChatHistoryContext,
  extractChatStreamArtifacts,
  readChatStreamFrames,
  startChatStreamWithProjectionRetry,
} from "./chatApi";
import { chatHistoryApi } from "./chatHistoryApi";
import { ChatInput, ChatMessageBubble } from "./chatPresentation";
import type {
  ChatMessage,
  ChatSessionState,
  ChatStudioTarget,
  ChatUsageSummary,
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
  expectedTurnCount: number;
  latestTurnId?: string;
  messages: ChatMessage[];
  sessionId: string;
  status: LocalChatStatus;
  target?: ChatStudioTarget;
  title: string;
  usage?: ChatUsageSummary;
};

const EMPTY_CONVERSATION_IDS: ReadonlySet<string> = new Set();
const EMPTY_PENDING_CONVERSATIONS: ReadonlyMap<string, ConversationState> =
  new Map();

type ConversationListItem = ConversationMeta & {
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
    expectedTurnCount: 0,
    messages: [],
    sessionId: createClientId(),
    status: "draft",
    title: t("pages.chat.index.newChat", "New chat"),
  };
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
  return messages.some(
    (message) => message.status === "error" || Boolean(message.error?.trim())
  )
    ? "error"
    : "completed_text";
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
  const { token } = theme.useToken();
  const queryClient = useQueryClient();
  const activeConversationRef = useRef<ConversationState | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
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
  const [deleteError, setDeleteError] = useState("");
  const [deletedConversationIds, setDeletedConversationIds] = useState<
    ReadonlySet<string>
  >(() => new Set());
  const [pendingConversations, setPendingConversations] = useState<
    ReadonlyMap<string, ConversationState>
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
  const scopeId = routeScopeId || resolvedScope?.scopeId || "";
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
      ...[...scopedPendingConversations.values()].filter(
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
    const serverItems = conversations.map((conversation) => ({
      ...conversation,
      ...(liveById.has(conversation.id)
        ? { liveStatus: liveById.get(conversation.id)?.status }
        : {}),
    }));
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

  useLayoutEffect(() => {
    abortControllerRef.current?.abort();
    for (const controller of reconciliationControllersRef.current.values()) {
      controller.abort();
    }
    reconciliationControllersRef.current.clear();
    detailRequestRef.current = createClientId();
    activeConversationRef.current = null;
    setConversationStateScopeId(scopeId);
    setActiveConversation(null);
    setDeleteTarget(null);
    setDeleteError("");
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
    const serverConversations = conversationsQuery.data ?? [];
    setPendingConversations((current) => {
      const next = new Map(current);
      let changed = false;
      for (const [conversationId, pendingConversation] of current) {
        const serverMeta = serverConversations.find(
          (conversation) => conversation.id === conversationId
        );
        if (
          serverMeta &&
          serverMeta.messageCount >= pendingConversation.expectedTurnCount
        ) {
          next.delete(conversationId);
          changed = true;
        }
      }
      return changed ? next : current;
    });
  }, [conversationsQuery.data]);

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
      const pendingConversation = pendingConversations.get(conversationId);
      if (pendingConversation) {
        detailRequestRef.current = createClientId();
        activeConversationRef.current = pendingConversation;
        setActiveConversation(pendingConversation);
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
        const storedMessages = await chatHistoryApi.loadConversation(
          scopeId,
          conversationId
        );
        if (detailRequestRef.current !== requestId) {
          return;
        }

        const restoredConversation: ConversationState = {
          ...placeholder,
          messages: hydrateStoredMessages(storedMessages),
          status: resolveStoredConversationStatus(storedMessages),
        };
        activeConversationRef.current = restoredConversation;
        setActiveConversation(restoredConversation);
        setDetailLoadState({ status: "idle" });
        setSession({
          ...createIdleSession(scopeId),
          status: storedMessages.length > 0 ? "success" : "idle",
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

    setDeleteError("");
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
    } catch (error) {
      if (scopeEpochRef.current !== deleteScopeEpoch) {
        return;
      }
      setDeleteError(errorMessage(error));
    } finally {
      if (scopeEpochRef.current === deleteScopeEpoch) {
        setDeletingConversation(false);
      }
    }
  }, [deleteTarget, deletingConversation, isStreaming, queryClient, scopeId]);

  const reconcileConversation = useCallback(
    (conversation: ConversationState) => {
      if (!scopeId || !conversation.conversationId) {
        return;
      }

      const conversationId = conversation.conversationId;
      reconciliationControllersRef.current.get(conversationId)?.abort();
      const controller = new AbortController();
      reconciliationControllersRef.current.set(conversationId, controller);
      const reconciliationScopeEpoch = scopeEpochRef.current;
      const delaysMs = [0, 300, 900, 1_800];

      void (async () => {
        for (const delayMs of delaysMs) {
          try {
            await abortableDelay(delayMs, controller.signal);
            const [nextConversations, storedMessages] = await Promise.all([
              chatHistoryApi.listConversationMetas(scopeId),
              chatHistoryApi.loadConversation(scopeId, conversationId),
            ]);
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
            const observedExpectedTurn =
              Boolean(
                serverMeta &&
                  serverMeta.messageCount >= conversation.expectedTurnCount
              ) ||
              (conversation.latestTurnId
                ? storedMessages.some(
                  (message) =>
                    message.id === `${conversation.latestTurnId}:assistant`
                )
                : false);
            if (!serverMeta || !observedExpectedTurn) {
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
                expectedTurnCount: serverMeta.messageCount,
                title: serverMeta.title || current.title,
              };
              activeConversationRef.current = next;
              return next;
            });
            return;
          } catch {
            if (
              controller.signal.aborted ||
              scopeEpochRef.current !== reconciliationScopeEpoch
            ) {
              return;
            }
          }
        }
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

  const runChat = useCallback(
    async (conversation: ConversationState, input: string) => {
      if (!scopeId || isStreaming) {
        return;
      }

      const trimmedInput = input.trim();
      if (!trimmedInput) {
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
      const startedConversation: ConversationState = {
        ...conversation,
        messages: [...conversation.messages, userMessage, assistantMessage],
        status: nextStatus,
        title,
      };
      const rawFrames: unknown[] = [];
      const accumulator = createRuntimeEventAccumulator();
      let receivedChatHistoryContext = false;
      let streamingConversation = startedConversation;

      abortControllerRef.current?.abort();
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
        const response = await startChatStreamWithProjectionRetry(
          {
            conversation: conversation.conversationId
              ? { conversationId: conversation.conversationId }
              : {},
            prompt: trimmedInput,
            sessionId: conversation.sessionId,
          },
          controller.signal
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
            receivedChatHistoryContext = true;
            if (chatHistoryContext.scopeId !== scopeId) {
              throw new Error("Chat History context does not match the active scope.");
            }
            if (
              conversation.conversationId &&
              chatHistoryContext.conversationId !== conversation.conversationId
            ) {
              throw new Error("Chat History returned a different conversation identity.");
            }

            streamingConversation = {
              ...streamingConversation,
              conversationId: chatHistoryContext.conversationId,
              expectedTurnCount: conversation.expectedTurnCount + 1,
              latestTurnId: chatHistoryContext.turnId,
            };
            activeConversationRef.current = streamingConversation;
            setActiveConversation((current) =>
              current?.clientId === conversation.clientId
                ? streamingConversation
                : current
            );
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
        if (finalConversation.conversationId) {
          setPendingConversations((current) => {
            const next = new Map(current);
            next.set(finalConversation.conversationId as string, finalConversation);
            return next;
          });
        }
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
        const message =
          controller.signal.aborted && !accumulator.errorText
            ? t("pages.chat.index.chatStopped", "Chat stopped.")
            : error instanceof Error
              ? error.message
              : String(error);
        accumulator.errorText = message;
        const failedConversation: ConversationState = {
          ...streamingConversation,
          messages: streamingConversation.messages.map((entry) =>
            entry.id === assistantMessageId
              ? {
                  ...entry,
                  ...buildAssistantMessagePatch(accumulator, "error"),
                }
              : entry
          ),
          status: "error",
        };
        if (receivedChatHistoryContext && failedConversation.conversationId) {
          setPendingConversations((current) => {
            const next = new Map(current);
            next.set(failedConversation.conversationId as string, failedConversation);
            return next;
          });
        }
        activeConversationRef.current = failedConversation;
        setActiveConversation((current) =>
          current?.clientId === conversation.clientId ? failedConversation : current
        );
        setSession(buildSessionFromAccumulator(scopeId, accumulator, "error"));
        if (failedConversation.conversationId) {
          reconcileConversation(failedConversation);
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }
    },
    [isStreaming, reconcileConversation, scopeId]
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
          {t(
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
                          {conversation.liveStatus === "streaming" ||
                          conversation.liveStatus === "creating"
                            ? formatStatusLabel(conversation.liveStatus)
                            : formatTurnCount(conversation.messageCount)}
                        </span>
                        <span>{formatRelativeTime(conversation.updatedAt)}</span>
                      </span>
                    </span>
                  </button>
                  <Tooltip
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
                        setDeleteError("");
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
            <Tooltip
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
            </Tooltip>
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
                {scopeId
                  ? t("pages.chat.index.scopeValue", "Scope {scopeId}", { scopeId })
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

          {!scopeId && !authSessionQuery.isLoading ? (
            <Alert
              banner
              message={t(
                "pages.chat.index.noScope",
                "No usable scope was resolved for this account. Refresh and try again."
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
                direction="vertical"
                size={14}
                style={{
                  maxWidth: 920,
                  width: "100%",
                }}
              >
                {activeConversation?.messages.map((message) => (
                  <ChatMessageBubble key={message.id} message={message} />
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
              disabled={!scopeId || detailLoadState.status === "loading"}
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
            setDeleteError("");
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
        {deleteError ? (
          <Alert
            description={deleteError}
            message={t(
              "pages.chat.index.deleteChatFailed",
              "Conversation could not be deleted"
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
