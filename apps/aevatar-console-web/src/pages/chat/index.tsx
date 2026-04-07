import { useQuery } from "@tanstack/react-query";
import { Alert } from "antd";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { nyxIdChatApi } from "@/shared/api/nyxIdChatApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import {
  buildScopeConsoleServiceOptions,
  createNyxIdChatBindingInput,
  nyxIdChatServiceId,
  scopeServiceAppId,
  scopeServiceNamespace,
  type ScopeConsoleServiceOption,
} from "@/shared/runs/scopeConsole";
import { studioApi } from "@/shared/studio/api";
import { buildStudioWorkflowEditorRoute } from "@/shared/studio/navigation";
import { AevatarPageShell } from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import { chatHistoryApi } from "./chatHistoryApi";
import {
  createConversationId,
  hydrateChatMessages,
  serializeChatMessages,
} from "./chatHistory";
import { ChatAdvancedConsole } from "./chatAdvancedConsole";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import {
  ChatInput,
  ChatMetaStrip,
  ChatMessageBubble,
  ConversationSidebar,
  DebugPanel,
  EmptyChatState,
  LoadingState,
  ServiceSelector,
} from "./chatPresentation";
import type {
  ChatMessage,
  ChatSessionState,
  ConversationMeta,
  PendingApprovalInfo,
  RuntimeEvent,
  ServiceOption,
  StepInfo,
  ToolCallInfo,
} from "./chatTypes";

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

function mapChatServiceOption(service: ScopeConsoleServiceOption): ServiceOption {
  return {
    deploymentStatus: service.deploymentStatus,
    endpoints: service.endpoints,
    id: service.serviceId,
    kind: service.kind,
    label: service.displayName,
    primaryActorId: service.primaryActorId,
  };
}

function createIdleSession(scopeId = "", serviceId = ""): ChatSessionState {
  return {
    actorId: "",
    commandId: "",
    endpointId: "chat",
    eventCount: 0,
    runId: "",
    scopeId,
    serviceId,
    status: "idle",
    updatedAt: undefined,
  };
}

function buildConversationMeta(
  conversationId: string,
  messages: readonly ChatMessage[],
  session: ChatSessionState,
  service: ServiceOption,
  previousCreatedAt?: string
): ConversationMeta {
  const firstUserMessage = messages.find((message) => message.role === "user");
  const title = (firstUserMessage?.content || "Untitled").trim().slice(0, 60);
  const existingMessages = messages.filter((message) => message.status !== "streaming");
  const now = new Date(session.updatedAt || Date.now()).toISOString();

  return {
    actorId:
      session.actorId ||
      (service.kind === "nyxid-chat" ? conversationId : undefined),
    commandId: session.commandId || undefined,
    createdAt: previousCreatedAt || now,
    id: conversationId,
    messageCount: existingMessages.length,
    runId: session.runId || undefined,
    serviceId: service.id,
    serviceKind: service.kind,
    title: title || "Untitled",
    updatedAt: now,
  };
}

function collectConversationEvents(messages: readonly ChatMessage[]): RuntimeEvent[] {
  return messages.flatMap((message) => message.events ?? []);
}

function deriveConversationSession(
  scopeId: string,
  meta: ConversationMeta | undefined,
  messages: readonly ChatMessage[]
): ChatSessionState {
  const events = collectConversationEvents(messages);
  const lastAssistant = [...messages].reverse().find((message) => message.role === "assistant");

  return {
    actorId: meta?.actorId || "",
    commandId: meta?.commandId || "",
    endpointId: "chat",
    error: lastAssistant?.status === "error" ? lastAssistant.error : undefined,
    eventCount: events.length,
    runId: meta?.runId || "",
    scopeId,
    serviceId: meta?.serviceId || "",
    status:
      lastAssistant?.status === "error"
        ? "error"
        : messages.length > 0
          ? "success"
          : "idle",
    updatedAt: meta?.updatedAt ? Date.parse(meta.updatedAt) : undefined,
  };
}

function cloneStepInfo(steps?: readonly StepInfo[]): StepInfo[] {
  return (steps ?? []).map((step) => ({ ...step }));
}

function cloneToolCallInfo(toolCalls?: readonly ToolCallInfo[]): ToolCallInfo[] {
  return (toolCalls ?? []).map((toolCall) => ({ ...toolCall }));
}

function clonePendingApproval(
  pendingApproval?: PendingApprovalInfo
): PendingApprovalInfo | undefined {
  return pendingApproval ? { ...pendingApproval } : undefined;
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
    content: accumulator.assistantText,
    error: accumulator.errorText || undefined,
    events: [...accumulator.events],
    pendingApproval: clonePendingApproval(accumulator.pendingApproval),
    status,
    steps: cloneStepInfo(accumulator.steps),
    thinking: accumulator.thinking,
    toolCalls: cloneToolCallInfo(accumulator.toolCalls),
  };
}

function buildSessionFromAccumulator(
  scopeId: string,
  serviceId: string,
  accumulator: ReturnType<typeof createRuntimeEventAccumulator>,
  status: ChatSessionState["status"],
  fallback?: Partial<Pick<ChatSessionState, "actorId" | "commandId" | "runId">>
): ChatSessionState {
  return {
    actorId: accumulator.actorId || fallback?.actorId || "",
    commandId: accumulator.commandId || fallback?.commandId || "",
    endpointId: "chat",
    error: accumulator.errorText || undefined,
    eventCount: accumulator.events.length,
    runId: accumulator.runId || fallback?.runId || "",
    scopeId,
    serviceId,
    status,
    updatedAt: resolveEventTimestamp(accumulator.events),
  };
}

const ChatPage: React.FC = () => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const previousServiceIdRef = useRef("");
  const restoringConversationRef = useRef(false);
  const scrollAnchorRef = useRef<HTMLDivElement | null>(null);
  const nyxIdChatBoundRef = useRef(false);
  const serviceSelectionSourceRef = useRef<"auto" | "manual" | "conversation">("auto");

  const [selectedServiceId, setSelectedServiceId] = useState("");
  const [prompt, setPrompt] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [debugEvents, setDebugEvents] = useState<RuntimeEvent[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [activeApprovalRequestId, setActiveApprovalRequestId] = useState<string | null>(
    null
  );
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [session, setSession] = useState<ChatSessionState>(createIdleSession());
  const [showDebug, setShowDebug] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [advancedOpen, setAdvancedOpen] = useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["chat", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const routeSearch = typeof window === "undefined" ? "" : window.location.search;
  const routeScopeId = useMemo(() => readChatQueryValue("scopeId", routeSearch), [routeSearch]);
  const routeServiceId = useMemo(
    () => readChatQueryValue("serviceId", routeSearch),
    [routeSearch]
  );
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data]
  );
  const scopeId = routeScopeId || resolvedScope?.scopeId || "";

  const bindingQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["chat", "binding", scopeId],
    queryFn: () => studioApi.getScopeBinding(scopeId),
  });
  const servicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["chat", "services", scopeId],
    queryFn: () =>
      servicesApi.listServices({
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
        tenantId: scopeId,
      }),
  });

  const services = useMemo(
    () =>
      buildScopeConsoleServiceOptions(
        servicesQuery.data ?? [],
        bindingQuery.data?.available ? bindingQuery.data.serviceId : undefined,
        {
          chatOnly: true,
        }
      ).map(mapChatServiceOption),
    [bindingQuery.data?.available, bindingQuery.data?.serviceId, servicesQuery.data]
  );

  const selectedService =
    services.find((service) => service.id === selectedServiceId) ?? null;

  useEffect(() => {
    scrollAnchorRef.current?.scrollIntoView?.({
      behavior: messages.length > 1 ? "smooth" : "auto",
      block: "end",
    });
  }, [messages]);

  useEffect(() => {
    const html = document.documentElement;
    const body = document.body;
    const previousHtmlOverflow = html.style.overflow;
    const previousBodyOverflow = body.style.overflow;
    const previousBodyOverscrollBehavior = body.style.overscrollBehavior;
    const layoutElements = Array.from(
      document.querySelectorAll<HTMLElement>(
        ".ant-layout-content, .ant-pro-layout-content, .ant-pro-basicLayout-content"
      )
    );
    const previousLayoutStyles = layoutElements.map((element) => ({
      overflow: element.style.overflow,
      overscrollBehavior: element.style.overscrollBehavior,
    }));

    html.style.overflow = "hidden";
    body.style.overflow = "hidden";
    body.style.overscrollBehavior = "none";
    try {
      window.scrollTo({ top: 0, behavior: "auto" });
    } catch {
      // jsdom does not implement window.scrollTo.
    }

    layoutElements.forEach((element) => {
      element.style.overflow = "hidden";
      element.style.overscrollBehavior = "none";
    });

    return () => {
      html.style.overflow = previousHtmlOverflow;
      body.style.overflow = previousBodyOverflow;
      body.style.overscrollBehavior = previousBodyOverscrollBehavior;
      layoutElements.forEach((element, index) => {
        const previousStyle = previousLayoutStyles[index];
        if (!previousStyle) {
          return;
        }

        element.style.overflow = previousStyle.overflow;
        element.style.overscrollBehavior = previousStyle.overscrollBehavior;
      });
    };
  }, []);

  useEffect(() => {
    if (!scopeId) {
      setActiveApprovalRequestId(null);
      setActiveConversationId(null);
      setConversations([]);
      setDebugEvents([]);
      setMessages([]);
      setAdvancedOpen(false);
      previousServiceIdRef.current = "";
      setSession(createIdleSession());
      return;
    }

    let cancelled = false;
    setActiveApprovalRequestId(null);
    setActiveConversationId(null);
    setAdvancedOpen(false);
    setDebugEvents([]);
    setMessages([]);
    setSession(createIdleSession(scopeId));
    nyxIdChatBoundRef.current = false;
    previousServiceIdRef.current = "";
    restoringConversationRef.current = false;
    serviceSelectionSourceRef.current = "auto";

    void chatHistoryApi.listConversationMetas(scopeId).then((items) => {
      if (!cancelled) {
        setConversations(items);
      }
    });

    return () => {
      cancelled = true;
    };
  }, [scopeId]);

  useEffect(() => {
    if (!services.length) {
      serviceSelectionSourceRef.current = "auto";
      setSelectedServiceId("");
      return;
    }

    const routePreferredServiceId =
      routeServiceId && services.some((service) => service.id === routeServiceId)
        ? routeServiceId
        : "";
    const preferredServiceId =
      routePreferredServiceId ||
      (bindingQuery.data?.available ? bindingQuery.data.serviceId : "") ||
      services.find((service) => service.id === nyxIdChatServiceId)?.id ||
      services[0]?.id ||
      "";

    const hasSelectedService =
      selectedServiceId &&
      services.some((service) => service.id === selectedServiceId);

    if (!hasSelectedService) {
      serviceSelectionSourceRef.current = "auto";
      setSelectedServiceId(preferredServiceId);
      return;
    }

    if (
      serviceSelectionSourceRef.current === "auto" &&
      preferredServiceId &&
      selectedServiceId !== preferredServiceId
    ) {
      setSelectedServiceId(preferredServiceId);
    }
  }, [
    bindingQuery.data?.available,
    bindingQuery.data?.serviceId,
    routeServiceId,
    selectedServiceId,
    services,
  ]);

  useEffect(() => {
    if (!selectedServiceId) {
      return;
    }

    if (!previousServiceIdRef.current) {
      previousServiceIdRef.current = selectedServiceId;
      setSession((current) => ({
        ...current,
        scopeId,
        serviceId: selectedServiceId,
      }));
      return;
    }

    if (previousServiceIdRef.current === selectedServiceId) {
      return;
    }

    previousServiceIdRef.current = selectedServiceId;
    if (restoringConversationRef.current) {
      restoringConversationRef.current = false;
      setSession((current) => ({
        ...current,
        serviceId: selectedServiceId,
      }));
      return;
    }

    abortControllerRef.current?.abort();
    setActiveApprovalRequestId(null);
    setActiveConversationId(null);
    setDebugEvents([]);
    setMessages([]);
    setSession(createIdleSession(scopeId, selectedServiceId));
  }, [scopeId, selectedServiceId]);

  useEffect(
    () => () => {
      abortControllerRef.current?.abort();
    },
    []
  );

  const updateAssistantMessage = useCallback(
    (messageId: string, patch: Partial<ChatMessage>) => {
      setMessages((current) =>
        current.map((message) =>
          message.id === messageId ? { ...message, ...patch } : message
        )
      );
    },
    []
  );

  const ensureNyxIdChatBound = useCallback(async () => {
    if (!scopeId || nyxIdChatBoundRef.current) {
      return;
    }

    await studioApi.bindScopeGAgent(createNyxIdChatBindingInput(scopeId));
    nyxIdChatBoundRef.current = true;
  }, [scopeId]);

  const persistConversationState = useCallback(
    async (
      conversationId: string,
      nextMessages: ChatMessage[],
      nextSession: ChatSessionState,
      service: ServiceOption
    ) => {
      if (!scopeId || !conversationId) {
        return;
      }

      const previousMeta = conversations.find((item) => item.id === conversationId);
      const nextMeta = buildConversationMeta(
        conversationId,
        nextMessages,
        nextSession,
        service,
        previousMeta?.createdAt
      );

      setConversations((current) => {
        const filtered = current.filter((item) => item.id !== conversationId);
        return [nextMeta, ...filtered];
      });

      await chatHistoryApi.saveConversation(
        scopeId,
        nextMeta,
        serializeChatMessages(nextMessages)
      );
    },
    [conversations, scopeId]
  );

  const handleNewChat = useCallback(() => {
    abortControllerRef.current?.abort();
    setPrompt("");
    setAdvancedOpen(false);
    setActiveApprovalRequestId(null);
    setActiveConversationId(null);
    setDebugEvents([]);
    setMessages([]);
    setIsStreaming(false);
    setSession(createIdleSession(scopeId, selectedServiceId));
    nyxIdChatBoundRef.current = false;
  }, [scopeId, selectedServiceId]);

  const handleCreate = useCallback(() => {
    history.push(
      buildStudioWorkflowEditorRoute({
        draftMode: "new",
      })
    );
  }, []);

  const handleSelectConversation = useCallback(
    async (conversationId: string) => {
      if (!scopeId) {
        return;
      }

      const meta = conversations.find((item) => item.id === conversationId);
      const restoredMessages = hydrateChatMessages(
        await chatHistoryApi.loadConversation(scopeId, conversationId)
      );
      const restoredEvents = collectConversationEvents(restoredMessages);

      if (meta?.serviceId && meta.serviceId !== selectedServiceId) {
        restoringConversationRef.current = true;
        serviceSelectionSourceRef.current = "conversation";
        previousServiceIdRef.current = meta.serviceId;
        setSelectedServiceId(meta.serviceId);
      }

      setActiveConversationId(conversationId);
      setActiveApprovalRequestId(null);
      setMessages(restoredMessages);
      setDebugEvents(restoredEvents);
      setSession(deriveConversationSession(scopeId, meta, restoredMessages));
    },
    [conversations, scopeId, selectedServiceId]
  );

  const handleDeleteConversation = useCallback(
    async (conversationId: string) => {
      if (!scopeId) {
        return;
      }

      setConversations((current) => current.filter((item) => item.id !== conversationId));
      if (activeConversationId === conversationId) {
        setActiveApprovalRequestId(null);
        setActiveConversationId(null);
        setDebugEvents([]);
        setMessages([]);
        setSession(createIdleSession(scopeId, selectedServiceId));
      }

      await chatHistoryApi.deleteConversation(scopeId, conversationId);
    },
    [activeConversationId, scopeId, selectedServiceId]
  );

  const handleSend = useCallback(async () => {
    const trimmedPrompt = prompt.trim();
    if (!scopeId || !selectedService || !trimmedPrompt || isStreaming) {
      return;
    }

    const conversationId =
      activeConversationId || createConversationId();
    const userMessage: ChatMessage = {
      content: trimmedPrompt,
      id: createClientId(),
      role: "user",
      status: "complete",
      timestamp: Date.now(),
    };
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

    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;

    const nextMessages = [...messages, userMessage, assistantMessage];
    setActiveConversationId(conversationId);
    setMessages(nextMessages);
    setPrompt("");
    setDebugEvents([]);
    setIsStreaming(true);
    setActiveApprovalRequestId(null);
    setSession({
      ...createIdleSession(scopeId, selectedService.id),
      status: "running",
      updatedAt: Date.now(),
    });
    const accumulator = createRuntimeEventAccumulator({
      actorId: selectedService.kind === "nyxid-chat" ? conversationId : "",
    });

    try {
      if (selectedService.kind === "nyxid-chat") {
        await ensureNyxIdChatBound();
      }

      const response = await runtimeRunsApi.streamChat(
        scopeId,
        {
          prompt: trimmedPrompt,
          sessionId:
            selectedService.kind === "nyxid-chat" ? undefined : conversationId,
        } as Parameters<typeof runtimeRunsApi.streamChat>[1],
        controller.signal,
        {
          serviceId: selectedService.id,
        }
      );

      for await (const event of parseBackendSSEStream(response, {
        signal: controller.signal,
      })) {
        applyRuntimeEvent(accumulator, event);
        setDebugEvents([...accumulator.events]);

        if (isRawObserved(event)) {
          continue;
        }

        updateAssistantMessage(
          assistantMessageId,
          buildAssistantMessagePatch(
            accumulator,
            accumulator.errorText ? "error" : "streaming"
          )
        );

        setSession(
          buildSessionFromAccumulator(
            scopeId,
            selectedService.id,
            accumulator,
            accumulator.errorText ? "error" : "running"
          )
        );
      }

      const finalAssistantStatus: ChatMessage["status"] = accumulator.errorText
        ? "error"
        : "complete";
      const finalSession = buildSessionFromAccumulator(
        scopeId,
        selectedService.id,
        accumulator,
        accumulator.errorText ? "error" : "success"
      );

      setMessages((current) => {
        const completedMessages = current.map((message) =>
          message.id === assistantMessageId
            ? {
                ...message,
                ...buildAssistantMessagePatch(
                  accumulator,
                  finalAssistantStatus
                ),
              }
            : message
        ) as ChatMessage[];
        void persistConversationState(
          conversationId,
          completedMessages,
          finalSession,
          selectedService
        );
        return completedMessages;
      });
      setSession(finalSession);
    } catch (error) {
      const message =
        controller.signal.aborted && !accumulator.errorText
          ? "Chat stopped by operator."
          : error instanceof Error
            ? error.message
            : String(error);
      accumulator.errorText = message;
      const finalSession = buildSessionFromAccumulator(
        scopeId,
        selectedService.id,
        accumulator,
        "error"
      );
      setMessages((current) => {
        const erroredMessages = current.map((entry) =>
          entry.id === assistantMessageId
            ? {
                ...entry,
                ...buildAssistantMessagePatch(accumulator, "error"),
              }
            : entry
        ) as ChatMessage[];
        void persistConversationState(
          conversationId,
          erroredMessages,
          finalSession,
          selectedService
        );
        return erroredMessages;
      });
      setSession(finalSession);
    } finally {
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }

      setIsStreaming(false);
    }
  }, [
    activeConversationId,
    ensureNyxIdChatBound,
    isStreaming,
    messages,
    persistConversationState,
    prompt,
    scopeId,
    selectedService,
    updateAssistantMessage,
  ]);

  const handleApprovalDecision = useCallback(
    async (requestId: string, approved: boolean) => {
      if (
        !scopeId ||
        !selectedService ||
        selectedService.kind !== "nyxid-chat" ||
        !activeConversationId
      ) {
        return;
      }

      const targetMessage = messages.find(
        (message) =>
          message.role === "assistant" &&
          message.pendingApproval?.requestId === requestId
      );
      if (!targetMessage) {
        return;
      }

      const actorId =
        session.actorId ||
        conversations.find((conversation) => conversation.id === activeConversationId)
          ?.actorId ||
        activeConversationId;

      if (!actorId) {
        const unavailableMessage =
          "Unable to resume approval because the NyxID conversation actor is unavailable.";
        const finalSession: ChatSessionState = {
          ...session,
          error: unavailableMessage,
          status: "error",
          updatedAt: Date.now(),
        };
        setMessages((current) => {
          const updated = current.map((message) =>
            message.id === targetMessage.id
              ? {
                  ...message,
                  error: unavailableMessage,
                  pendingApproval: undefined,
                  status: "error" as const,
                }
              : message
          ) as ChatMessage[];
          void persistConversationState(
            activeConversationId,
            updated,
            finalSession,
            selectedService
          );
          return updated;
        });
        setSession(finalSession);
        return;
      }

      abortControllerRef.current?.abort();
      const controller = new AbortController();
      abortControllerRef.current = controller;
      setActiveApprovalRequestId(requestId);
      setIsStreaming(true);

      const accumulator = createRuntimeEventAccumulator({
        actorId,
      });
      accumulator.assistantText = targetMessage.content;
      accumulator.commandId = session.commandId;
      accumulator.events = [...(targetMessage.events ?? [])];
      accumulator.runId = session.runId;
      accumulator.steps = cloneStepInfo(targetMessage.steps);
      accumulator.thinking = targetMessage.thinking ?? "";
      accumulator.toolCalls = cloneToolCallInfo(targetMessage.toolCalls);

      setMessages((current) =>
        current.map((message) =>
          message.id === targetMessage.id
            ? {
                ...message,
                error: undefined,
                pendingApproval: undefined,
                status: "streaming" as const,
              }
            : message
        )
      );
      setSession((current) => ({
        ...current,
        error: undefined,
        status: "running",
        updatedAt: Date.now(),
      }));

      try {
        const response = await nyxIdChatApi.approveToolCall(
          scopeId,
          actorId,
          {
            approved,
            reason: approved ? undefined : "Rejected by console operator.",
            requestId,
            sessionId: activeConversationId,
          },
          controller.signal
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          setDebugEvents([...accumulator.events]);

          if (isRawObserved(event)) {
            continue;
          }

          updateAssistantMessage(
            targetMessage.id,
            buildAssistantMessagePatch(
              accumulator,
              accumulator.errorText ? "error" : "streaming"
            )
          );
          setSession(
            buildSessionFromAccumulator(
              scopeId,
              selectedService.id,
              accumulator,
              accumulator.errorText ? "error" : "running",
              session
            )
          );
        }

        const finalStatus: ChatMessage["status"] = accumulator.errorText
          ? "error"
          : "complete";
        const finalSession = buildSessionFromAccumulator(
          scopeId,
          selectedService.id,
          accumulator,
          accumulator.errorText ? "error" : "success",
          session
        );

        setMessages((current) => {
          const updated = current.map((message) =>
            message.id === targetMessage.id
              ? {
                  ...message,
                  ...buildAssistantMessagePatch(accumulator, finalStatus),
                }
              : message
          ) as ChatMessage[];
          void persistConversationState(
            activeConversationId,
            updated,
            finalSession,
            selectedService
          );
          return updated;
        });
        setSession(finalSession);
      } catch (error) {
        const message =
          controller.signal.aborted && !accumulator.errorText
            ? "Approval continuation stopped by operator."
            : error instanceof Error
              ? error.message
              : String(error);
        accumulator.errorText = message;
        const finalSession = buildSessionFromAccumulator(
          scopeId,
          selectedService.id,
          accumulator,
          "error",
          session
        );
        setMessages((current) => {
          const updated = current.map((entry) =>
            entry.id === targetMessage.id
              ? {
                  ...entry,
                  ...buildAssistantMessagePatch(accumulator, "error"),
                }
              : entry
          ) as ChatMessage[];
          void persistConversationState(
            activeConversationId,
            updated,
            finalSession,
            selectedService
          );
          return updated;
        });
        setSession(finalSession);
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }

        setActiveApprovalRequestId((current) =>
          current === requestId ? null : current
        );
        setIsStreaming(false);
      }
    },
    [
      activeConversationId,
      conversations,
      messages,
      persistConversationState,
      scopeId,
      selectedService,
      session,
      updateAssistantMessage,
    ]
  );

  const handleStop = useCallback(() => {
    abortControllerRef.current?.abort();
  }, []);

  const isLoadingScope =
    authSessionQuery.isLoading || (scopeId.length > 0 && servicesQuery.isLoading);

  return (
    <AevatarPageShell pageHeaderRender={false} title="Chat">
      <div
        style={{
          background: "#f2f1ee",
          border: "1px solid #e7e5e4",
          borderRadius: 18,
          display: "flex",
          flex: 1,
          flexDirection: "column",
          height: "100%",
          minHeight: 0,
          overflow: "hidden",
        }}
      >
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

        <header
          style={{
            background: "rgba(255,255,255,0.95)",
            backdropFilter: "blur(10px)",
            borderBottom: "1px solid #e7e5e4",
            flexShrink: 0,
            overflow: "visible",
            padding: "0 20px",
            position: "relative",
            zIndex: 4,
          }}
        >
          <div
            style={{
              alignItems: "center",
              display: "flex",
              gap: 12,
              height: 54,
              justifyContent: "space-between",
            }}
          >
            <div
              style={{
                alignItems: "center",
                display: "flex",
                gap: 12,
                minWidth: 0,
              }}
            >
              <div
                style={{
                  color: "#111827",
                  fontSize: 14,
                  fontWeight: 600,
                }}
              >
                Console
              </div>
              {scopeId && services.length > 0 ? (
                <ServiceSelector
                  onCreate={handleCreate}
                  onSelect={(serviceId) => {
                    serviceSelectionSourceRef.current = "manual";
                    setSelectedServiceId(serviceId);
                  }}
                  selected={selectedServiceId}
                  services={services}
                />
              ) : null}
            </div>

            <div
              style={{
                alignItems: "center",
                display: "flex",
                gap: 8,
              }}
            >
              <button
                onClick={handleNewChat}
                style={{
                  background: "#ffffff",
                  border: "1px solid #e7e5e4",
                  borderRadius: 10,
                  color: "#6b7280",
                  cursor: "pointer",
                  fontSize: 12,
                  padding: "8px 12px",
                }}
                type="button"
              >
                New Chat
              </button>
              <button
                onClick={() => setAdvancedOpen((value) => !value)}
                style={{
                  background: advancedOpen ? "#eff6ff" : "#ffffff",
                  border: `1px solid ${advancedOpen ? "#93c5fd" : "#e7e5e4"}`,
                  borderRadius: 10,
                  color: advancedOpen ? "#2563eb" : "#6b7280",
                  cursor: "pointer",
                  fontSize: 12,
                  fontWeight: 600,
                  padding: "8px 12px",
                }}
                type="button"
              >
                Advanced
              </button>
              <button
                onClick={() => setShowDebug((value) => !value)}
                style={{
                  background: showDebug ? "#eff6ff" : "#ffffff",
                  border: `1px solid ${showDebug ? "#93c5fd" : "#e7e5e4"}`,
                  borderRadius: 10,
                  color: showDebug ? "#2563eb" : "#9ca3af",
                  cursor: "pointer",
                  fontSize: 11,
                  fontWeight: 600,
                  padding: "8px 12px",
                }}
                type="button"
              >
                Debug
              </button>
            </div>
          </div>

        </header>

        <div style={{ display: "flex", flex: 1, minHeight: 0 }}>
          <ConversationSidebar
            activeId={activeConversationId}
            conversations={conversations}
            onDelete={handleDeleteConversation}
            onNewChat={handleNewChat}
            onSelect={(conversationId) => {
              void handleSelectConversation(conversationId);
            }}
            onToggle={() => setSidebarOpen((value) => !value)}
            open={sidebarOpen}
          />

          <div
            style={{
              display: "flex",
              flex: 1,
              flexDirection: "column",
              minHeight: 0,
              minWidth: 0,
            }}
          >
            <div
              style={{
                background: "#fafaf8",
                flex: 1,
                minHeight: 0,
                overscrollBehavior: "contain",
                overflow: "auto",
              }}
            >
              <div
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: 20,
                  margin: "0 auto",
                  maxWidth: 840,
                  padding: "24px 20px",
                }}
              >
                {isLoadingScope ? (
                  <LoadingState />
                ) : !scopeId ? (
                  <Alert
                    showIcon
                    title="No project scope is currently available."
                    type="warning"
                  />
                ) : !selectedService || !selectedServiceId ? (
                  <Alert
                    showIcon
                    title="No chat-capable services are currently available."
                    type="info"
                  />
                ) : messages.length === 0 ? (
                  <EmptyChatState
                    description={
                      selectedService.kind === "nyxid-chat"
                        ? "Chat with NyxID about services, credentials, and configuration."
                        : `Invoke the "${selectedService.label}" service with a chat message.`
                    }
                    title={selectedService.label}
                  />
                ) : (
                  messages.map((message) => (
                    <ChatMessageBubble
                      activeApprovalRequestId={activeApprovalRequestId}
                      key={message.id}
                      message={message}
                      onApprovalDecision={(requestId, approved) => {
                        void handleApprovalDecision(requestId, approved);
                      }}
                    />
                  ))
                )}
                <div ref={scrollAnchorRef} />
              </div>
            </div>

            {showDebug && debugEvents.length > 0 ? (
              <div
                style={{
                  background: "#fafaf8",
                  borderTop: "1px solid #e7e5e4",
                  flexShrink: 0,
                  maxHeight: 280,
                  overscrollBehavior: "contain",
                  padding: "12px 20px",
                }}
              >
                <div style={{ margin: "0 auto", maxWidth: 840 }}>
                  <DebugPanel events={debugEvents} />
                </div>
              </div>
            ) : null}

            <div
              style={{
                background: "#ffffff",
                borderTop: "1px solid #e7e5e4",
                flexShrink: 0,
                padding: "14px 20px",
              }}
            >
              <div style={{ margin: "0 auto", maxWidth: 840 }}>
                <ChatInput
                  disabled={!scopeId || !selectedService}
                  isStreaming={isStreaming}
                  onChange={setPrompt}
                  onSend={() => void handleSend()}
                  onStop={handleStop}
                  value={prompt}
                />
                <ChatMetaStrip
                  actorId={session.actorId}
                  commandId={session.commandId}
                  runId={session.runId}
                  scopeId={scopeId}
                  serviceId={selectedService?.id}
                />
              </div>
            </div>
          </div>
        </div>
      </div>
      <ChatAdvancedConsole
        defaultServiceId={selectedServiceId}
        onClose={() => setAdvancedOpen(false)}
        onEnsureNyxIdBound={ensureNyxIdChatBound}
        open={advancedOpen}
        scopeId={scopeId}
        services={servicesQuery.data ?? []}
        sessionActorId={session.actorId || undefined}
      />
    </AevatarPageShell>
  );
};

export default ChatPage;
