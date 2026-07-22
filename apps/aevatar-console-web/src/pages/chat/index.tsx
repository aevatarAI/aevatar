import {
  DeleteOutlined,
  EditOutlined,
  MessageOutlined,
  PlusOutlined,
  SendOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  Space,
  Tag,
  Typography,
  theme,
} from "antd";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { studioApi } from "@/shared/studio/api";
import { AevatarPageShell } from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import {
  extractChatStreamArtifacts,
  readChatStreamFrames,
  startChatStream,
} from "./chatApi";
import { chatHistoryApi } from "./chatHistoryApi";
import {
  createConversationId,
  hydrateChatMessages,
  serializeChatMessages,
} from "./chatHistory";
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
  ToolCallInfo,
} from "./chatTypes";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamMemberWorkflowStudioHref,
} from "@/shared/navigation/teamRoutes";
import { t } from "@/shared/i18n/messages";

type ConversationState = {
  id: string;
  messages: ChatMessage[];
  serverConversationId?: string;
  status: LocalChatStatus;
  stateVersion?: number;
  target?: ChatStudioTarget;
  title: string;
  usage?: ChatUsageSummary;
};

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

function normalizeStateVersion(value: number | undefined): number | undefined {
  return typeof value === "number" && Number.isFinite(value) && value > 0
    ? Math.trunc(value)
    : undefined;
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

function isEmptyDraftMeta(conversation: ConversationMeta): boolean {
  return conversation.status === "draft" && conversation.messageCount === 0;
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

const ChatPage: React.FC = () => {
  const { token } = theme.useToken();
  const activeConversationRef = useRef<ConversationState | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const [activeConversation, setActiveConversation] =
    useState<ConversationState | null>(null);
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [prompt, setPrompt] = useState("");
  const [, setSession] = useState<ChatSessionState>(createIdleSession());
  const [renameTarget, setRenameTarget] = useState<ConversationMeta | null>(null);
  const [renameValue, setRenameValue] = useState("");

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
  const isStreaming =
    activeConversation?.status === "streaming" ||
    activeConversation?.status === "creating";
  const studioJump = resolveStudioJump(activeConversation?.target);

  const refreshConversations = useCallback(async () => {
    if (!scopeId) {
      setConversations([]);
      return;
    }

    setConversations(await chatHistoryApi.listConversationMetas(scopeId));
  }, [scopeId]);

  useEffect(() => {
    abortControllerRef.current?.abort();
    setActiveConversation(null);
    setPrompt("");
    setSession(createIdleSession(scopeId));
    void refreshConversations();
  }, [refreshConversations, scopeId]);

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
      abortControllerRef.current?.abort();
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

  const persistConversation = useCallback(
    async (conversation: ConversationState) => {
      if (!scopeId) {
        return;
      }

      const now = new Date().toISOString();
      const existing = conversations.find((item) => item.id === conversation.id);
      const storedMessages = serializeChatMessages(conversation.messages);
      const meta: ConversationMeta = {
        createdAt: existing?.createdAt || now,
        id: conversation.id,
        messageCount: storedMessages.length,
        scopeId,
        serviceId: "chat",
        serviceKind: "chat",
        serverConversationId: conversation.serverConversationId,
        stateVersion: normalizeStateVersion(conversation.stateVersion),
        status: conversation.status,
        target: conversation.target,
        title: conversation.title,
        updatedAt: now,
        usage: conversation.usage,
      };

      setConversations((current) => [
        meta,
        ...current.filter((item) => item.id !== conversation.id),
      ]);
      await chatHistoryApi.saveConversation(scopeId, meta, storedMessages);
    },
    [conversations, scopeId]
  );

  const commitConversation = useCallback(
    (conversation: ConversationState) => {
      activeConversationRef.current = conversation;
      setActiveConversation(conversation);
      void persistConversation(conversation);
    },
    [persistConversation]
  );

  const restoreConversation = useCallback(
    async (conversationId: string) => {
      if (!scopeId) {
        return;
      }

      abortControllerRef.current?.abort();
      const meta = conversations.find((item) => item.id === conversationId);
      const messages = hydrateChatMessages(
        await chatHistoryApi.loadConversation(scopeId, conversationId)
      );
      const restoredConversation: ConversationState = {
        id: conversationId,
        messages,
        serverConversationId: meta?.serverConversationId,
        status: meta?.status || "draft",
        stateVersion: normalizeStateVersion(meta?.stateVersion),
        target: meta?.target,
        title: meta?.title || t("pages.chat.index.newChat", "New chat"),
        usage: meta?.usage,
      };
      activeConversationRef.current = restoredConversation;
      setActiveConversation(restoredConversation);
      setPrompt("");
      setSession({
        ...createIdleSession(scopeId),
        eventCount: messages.flatMap((message) => message.events ?? []).length,
        runId: meta?.target?.runId || meta?.runId || "",
        status:
          meta?.status === "error"
            ? "error"
            : messages.length > 0
              ? "success"
              : "idle",
        updatedAt: meta?.updatedAt ? Date.parse(meta.updatedAt) : undefined,
      });
    },
    [conversations, scopeId]
  );

  const handleNewChat = useCallback(() => {
    const currentConversation = activeConversationRef.current;
    if (isEmptyDraftConversation(currentConversation)) {
      return;
    }

    const reusableDraft = conversations.find(isEmptyDraftMeta);
    if (reusableDraft) {
      void restoreConversation(reusableDraft.id);
      return;
    }

    abortControllerRef.current?.abort();
    const conversation: ConversationState = {
      id: createConversationId(),
      messages: [],
      status: "draft",
      title: t("pages.chat.index.newChat", "New chat"),
    };
    setPrompt("");
    setSession(createIdleSession(scopeId));
    commitConversation(conversation);
  }, [commitConversation, conversations, restoreConversation, scopeId]);

  const handleSelectConversation = useCallback(
    async (conversationId: string) => {
      await restoreConversation(conversationId);
    },
    [restoreConversation]
  );

  const handleDeleteConversation = useCallback(
    async (conversationId: string) => {
      if (!scopeId) {
        return;
      }

      setConversations((current) =>
        current.filter((item) => item.id !== conversationId)
      );
      if (activeConversation?.id === conversationId) {
        activeConversationRef.current = null;
        setActiveConversation(null);
        setSession(createIdleSession(scopeId));
      }

      await chatHistoryApi.deleteConversation(scopeId, conversationId);
    },
    [activeConversation?.id, scopeId]
  );

  const handleRename = useCallback(async () => {
    if (!scopeId || !renameTarget || !renameValue.trim()) {
      return;
    }

    const title = trimTitle(renameValue);
    await chatHistoryApi.renameConversation(scopeId, renameTarget.id, title);
    setConversations((current) =>
      current.map((item) =>
        item.id === renameTarget.id
          ? { ...item, title, updatedAt: new Date().toISOString() }
          : item
      )
    );
    setActiveConversation((current) =>
      current?.id === renameTarget.id ? { ...current, title } : current
    );
    setRenameTarget(null);
    setRenameValue("");
  }, [renameTarget, renameValue, scopeId]);

  const runChat = useCallback(
    async (conversation: ConversationState, input: string) => {
      if (!scopeId || isStreaming) {
        return;
      }

      const trimmedInput = input.trim();
      if (!trimmedInput) {
        return;
      }

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
      const serverConversationId =
        conversation.serverConversationId?.trim() || undefined;
      const sourceStateVersion = normalizeStateVersion(conversation.stateVersion);
      const conversationInput = serverConversationId
        ? {
            conversationId: serverConversationId,
            ...(sourceStateVersion
              ? { minimumStateVersion: sourceStateVersion }
              : {}),
          }
        : {
            conversationId: null,
          };
      const rawFrames: unknown[] = [];
      const accumulator = createRuntimeEventAccumulator();
      let streamingConversation = startedConversation;

      abortControllerRef.current?.abort();
      const controller = new AbortController();
      abortControllerRef.current = controller;
      setPrompt("");
      activeConversationRef.current = startedConversation;
      setActiveConversation(startedConversation);
      void persistConversation(startedConversation);
      setSession({
        ...createIdleSession(scopeId),
        status: "running",
        updatedAt: Date.now(),
      });

      try {
        const response = await startChatStream(
          {
            commandId: serverConversationId ? undefined : conversation.id,
            conversation: conversationInput,
            prompt: trimmedInput,
            scopeId,
            sessionId: conversation.id,
          },
          controller.signal
        );

        for await (const frame of readChatStreamFrames(response, {
          signal: controller.signal,
        })) {
          rawFrames.push(frame.raw);
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
            if (!current || current.id !== conversation.id) {
              return current;
            }

            return patchedConversation;
          });
          void persistConversation(patchedConversation);
          setSession(
            buildSessionFromAccumulator(
              scopeId,
              accumulator,
              accumulator.errorText ? "error" : "running"
            )
          );
        }

        const artifacts = extractChatStreamArtifacts(rawFrames);
        let finalServerConversationId =
          artifacts.chatContext?.conversationId || conversation.serverConversationId;
        let finalStateVersion =
          normalizeStateVersion(artifacts.chatContext?.stateVersion) ??
          normalizeStateVersion(conversation.stateVersion);
        if (finalServerConversationId && !finalStateVersion) {
          try {
            const serverRecord = await chatHistoryApi.loadServerConversation(
              scopeId,
              finalServerConversationId
            );
            finalStateVersion =
              normalizeStateVersion(serverRecord?.stateVersion) ?? finalStateVersion;
          } catch {
            finalStateVersion = undefined;
          }
        }
        const finalAssistantStatus: ChatMessage["status"] = accumulator.errorText
          ? "error"
          : "complete";
        const finalTarget = artifacts.target || conversation.target;
        const finalUsage = artifacts.usage || conversation.usage;
        const finalContent = accumulator.finalOutput || accumulator.assistantText;
        const finalStatus: LocalChatStatus = accumulator.errorText
          ? "error"
          : resolveStudioJump(finalTarget)
            ? "completed_with_studio_target"
            : shouldAskForConfirmation(finalContent)
              ? "needs_confirmation"
              : "completed_text";
        const finalConversation: ConversationState = {
          ...startedConversation,
          messages: startedConversation.messages.map((message) =>
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
          serverConversationId: finalServerConversationId,
          status: finalStatus,
          stateVersion: finalStateVersion,
          target: finalTarget,
          usage: finalUsage,
        };
        setActiveConversation(finalConversation);
        await persistConversation(finalConversation);
        setSession(
          buildSessionFromAccumulator(
            scopeId,
            accumulator,
            accumulator.errorText ? "error" : "success"
          )
        );
      } catch (error) {
        const message =
          controller.signal.aborted && !accumulator.errorText
            ? t("pages.chat.index.chatStopped", "Chat stopped.")
            : error instanceof Error
              ? error.message
              : String(error);
        accumulator.errorText = message;
        const failedConversation: ConversationState = {
          ...startedConversation,
          messages: startedConversation.messages.map((entry) =>
            entry.id === assistantMessageId
              ? {
                  ...entry,
                  ...buildAssistantMessagePatch(accumulator, "error"),
                }
              : entry
          ),
          status: "error",
        };
        setActiveConversation(failedConversation);
        await persistConversation(failedConversation);
        setSession(buildSessionFromAccumulator(scopeId, accumulator, "error"));
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }
    },
    [isStreaming, persistConversation, scopeId]
  );

  const handleSend = useCallback(() => {
    const conversation =
      activeConversation ??
      ({
        id: createConversationId(),
        messages: [],
        status: "draft",
        title: t("pages.chat.index.newChat", "New chat"),
      } satisfies ConversationState);

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
          gridTemplateColumns: "260px minmax(0, 1fr)",
          height: "100%",
          minHeight: 0,
          overflow: "hidden",
        }}
      >
        <aside
          style={{
            background: token.colorBgContainer,
            borderRight: `1px solid ${token.colorBorderSecondary}`,
            display: "flex",
            flexDirection: "column",
            minHeight: 0,
          }}
        >
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
              icon={<PlusOutlined />}
              onClick={handleNewChat}
              style={{ height: 36 }}
              type="primary"
            >
              {t("pages.chat.index.newChatAction", "New Chat")}
            </Button>
            <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 12 }}>
              {t(
                "pages.chat.index.historyStoredLocally",
                "History is stored in this browser."
              )}
            </Typography.Text>
          </div>

          <div
            style={{
              flex: 1,
              minHeight: 0,
              overflow: "auto",
              padding: "8px 6px 10px",
            }}
          >
            {conversations.length === 0 ? (
              <Empty
                description={t("pages.chat.index.noChatHistory", "No chat history")}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                style={{ marginTop: 24 }}
              />
            ) : null}
            <Space direction="vertical" size={6} style={{ width: "100%" }}>
              {conversations.map((conversation) => {
                const active = conversation.id === activeConversation?.id;
                return (
                  <div
                    aria-label={conversation.title}
                    key={conversation.id}
                    onClick={() => void handleSelectConversation(conversation.id)}
                    onKeyDown={(event) => {
                      if (event.key === "Enter" || event.key === " ") {
                        event.preventDefault();
                        void handleSelectConversation(conversation.id);
                      }
                    }}
                    role="button"
                    style={{
                      background: active ? token.colorPrimaryBg : "transparent",
                      border: `1px solid ${
                        active ? token.colorPrimaryBorder : "transparent"
                      }`,
                      borderRadius: token.borderRadius,
                      boxShadow: active
                        ? `inset 3px 0 0 ${token.colorPrimary}`
                        : undefined,
                      cursor: "pointer",
                      display: "flex",
                      gap: 8,
                      padding: "9px 8px",
                      textAlign: "left",
                      width: "100%",
                    }}
                    tabIndex={0}
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
                        <span>{formatStatusLabel(conversation.status || "draft")}</span>
                        <span>{formatRelativeTime(conversation.updatedAt)}</span>
                      </span>
                    </span>
                    <span
                      style={{
                        alignItems: "center",
                        display: "flex",
                        flex: "0 0 auto",
                        gap: 2,
                      }}
                    >
                      <Button
                        aria-label={t("pages.chat.index.renameChat", "Rename {title}", {
                          title: conversation.title,
                        })}
                        icon={<EditOutlined />}
                        onClick={(event) => {
                          event.stopPropagation();
                          setRenameTarget(conversation);
                          setRenameValue(conversation.title);
                        }}
                        size="small"
                        type="text"
                      />
                      <Button
                        aria-label={t("pages.chat.index.deleteChat", "Delete {title}", {
                          title: conversation.title,
                        })}
                        danger
                        icon={<DeleteOutlined />}
                        onClick={(event) => {
                          event.stopPropagation();
                          void handleDeleteConversation(conversation.id);
                        }}
                        size="small"
                        type="text"
                      />
                    </span>
                  </div>
                );
              })}
            </Space>
          </div>
        </aside>

        <main
          style={{
            background: token.colorBgContainer,
            display: "flex",
            flexDirection: "column",
            minHeight: 0,
          }}
        >
          <div
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
            <div style={{ minWidth: 0 }}>
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
            <Space>
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
            {messageCount === 0 ? (
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
              disabled={!scopeId}
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

      <Modal
        destroyOnHidden
        okButtonProps={{ disabled: !renameValue.trim() }}
        onCancel={() => {
          setRenameTarget(null);
          setRenameValue("");
        }}
        onOk={() => void handleRename()}
        open={Boolean(renameTarget)}
        title={t("pages.chat.index.renameChatTitle", "Rename chat")}
      >
        <Input
          aria-label={t("pages.chat.index.conversationTitle", "Conversation title")}
          autoFocus
          onChange={(event) => setRenameValue(event.target.value)}
          onPressEnter={() => void handleRename()}
          value={renameValue}
        />
      </Modal>
    </AevatarPageShell>
  );
};

export default ChatPage;
