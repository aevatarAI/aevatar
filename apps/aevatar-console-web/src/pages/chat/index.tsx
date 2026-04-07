import { useQuery } from "@tanstack/react-query";
import { Alert } from "antd";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from "@/shared/models/services";
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
import {
  extractReasoningDelta,
  extractRunContext,
  extractStepCompleted,
  extractStepCompletedOutput,
  extractStepRequest,
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
  RuntimeEvent,
  ServiceOption,
  StepInfo,
  ToolCallInfo,
} from "./chatTypes";

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";
const nyxIdChatActorTypeName = "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent";
const nyxIdChatServiceId = "nyxid-chat";
const nyxIdChatLabel = "NyxID Chat";

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

function isChatEndpoint(endpoint: ServiceEndpointSnapshot | undefined): boolean {
  if (!endpoint) {
    return false;
  }

  return endpoint.kind === "chat" || endpoint.endpointId.trim() === "chat";
}

function createNyxIdServiceOption(): ServiceOption {
  return {
    endpoints: [
      {
        description: "Chat with NyxID about services, credentials, and configuration.",
        displayName: "Chat",
        endpointId: "chat",
        kind: "chat",
      },
    ],
    id: nyxIdChatServiceId,
    kind: "nyxid-chat",
    label: nyxIdChatLabel,
  };
}

function buildServiceOptions(
  services: readonly ServiceCatalogSnapshot[],
  defaultServiceId?: string
): ServiceOption[] {
  const builtIn = createNyxIdServiceOption();
  const remoteServices = services
    .filter((service) => service.endpoints.some(isChatEndpoint))
    .filter((service) => service.serviceId !== builtIn.id)
    .map((service) => ({
      deploymentStatus: service.deploymentStatus,
      endpoints: service.endpoints
        .filter(isChatEndpoint)
        .map((endpoint) => ({
          description: endpoint.description,
          displayName: endpoint.displayName,
          endpointId: endpoint.endpointId,
          kind: endpoint.kind,
          requestTypeUrl: endpoint.requestTypeUrl,
          responseTypeUrl: endpoint.responseTypeUrl,
        })),
      id: service.serviceId,
      kind: "service" as const,
      label: service.displayName || service.serviceId,
      primaryActorId: service.primaryActorId,
    }))
    .sort((left, right) => {
      const leftIsDefault = left.id === defaultServiceId ? 1 : 0;
      const rightIsDefault = right.id === defaultServiceId ? 1 : 0;
      if (leftIsDefault !== rightIsDefault) {
        return rightIsDefault - leftIsDefault;
      }

      return left.label.localeCompare(right.label);
    });

  return [builtIn, ...remoteServices];
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
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [session, setSession] = useState<ChatSessionState>(createIdleSession());
  const [showDebug, setShowDebug] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);

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
      buildServiceOptions(
        servicesQuery.data ?? [],
        bindingQuery.data?.available ? bindingQuery.data.serviceId : undefined
      ),
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
      setActiveConversationId(null);
      setConversations([]);
      setDebugEvents([]);
      setMessages([]);
      previousServiceIdRef.current = "";
      setSession(createIdleSession());
      return;
    }

    let cancelled = false;
    setActiveConversationId(null);
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

    await studioApi.bindScopeGAgent({
      actorTypeName: nyxIdChatActorTypeName,
      displayName: nyxIdChatLabel,
      endpoints: [
        {
          description: "Chat with NyxID about services, credentials, and configuration.",
          displayName: "Chat",
          endpointId: "chat",
          kind: "chat",
        },
      ],
      scopeId,
      serviceId: nyxIdChatServiceId,
    });
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
    setSession({
      ...createIdleSession(scopeId, selectedService.id),
      status: "running",
      updatedAt: Date.now(),
    });

    const events: RuntimeEvent[] = [];
    const steps: StepInfo[] = [];
    const toolCalls: ToolCallInfo[] = [];
    let assistantContent = "";
    let thinking = "";
    let runId = "";
    let actorId = selectedService.kind === "nyxid-chat" ? conversationId : "";
    let commandId = "";
    let errorText = "";

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
        events.push(event);
        setDebugEvents([...events]);

        if (event.type === "RUN_STARTED") {
          runId = event.runId || runId;
        }

        if (event.type === "TEXT_MESSAGE_CONTENT") {
          assistantContent += String(event.delta || "");
        }

        if (event.type === "STEP_STARTED") {
          const stepName = String(event.stepName || "").trim() || `Step ${steps.length + 1}`;
          steps.push({
            id: stepName,
            name: stepName,
            startedAt: event.timestamp || Date.now(),
            status: "running",
          });
        }

        if (event.type === "STEP_FINISHED") {
          const stepName = String(event.stepName || "").trim();
          const existingStep = steps.find(
            (step) =>
              step.status === "running" &&
              (!stepName || step.name === stepName || step.id === stepName)
          );
          if (existingStep) {
            existingStep.finishedAt = event.timestamp || Date.now();
            existingStep.status = "done";
          }
        }

        if (event.type === "TOOL_CALL_START") {
          const toolName = String(event.toolName || "").trim() || "Tool";
          const toolId =
            String(event.toolCallId || "").trim() || `${toolName}-${toolCalls.length + 1}`;
          toolCalls.push({
            id: toolId,
            name: toolName,
            startedAt: event.timestamp || Date.now(),
            status: "running",
          });
        }

        if (event.type === "TOOL_CALL_END") {
          const toolId = String(event.toolCallId || "").trim();
          const existingTool = toolCalls.find(
            (tool) => tool.status === "running" && (!toolId || tool.id === toolId)
          );
          if (existingTool) {
            existingTool.finishedAt = event.timestamp || Date.now();
            existingTool.result =
              "result" in event && typeof event.result === "string"
                ? event.result.trim()
                : "";
            existingTool.status = "done";
          }
        }

        if (event.type === "RUN_ERROR") {
          errorText = String(event.message || "Assistant run failed.").trim();
        }

        const runContext = extractRunContext(event);
        if (runContext) {
          actorId = runContext.actorId || actorId;
          commandId = runContext.commandId || commandId;
        }

        const stepRequest = extractStepRequest(event);
        if (stepRequest) {
          const stepIdentity = stepRequest.stepId || stepRequest.stepType || `Step ${steps.length + 1}`;
          const existingStep = steps.find(
            (step) => step.id === stepIdentity || step.name === stepIdentity
          );
          if (!existingStep) {
            steps.push({
              id: stepRequest.stepId || stepIdentity,
              name: stepRequest.stepId || stepRequest.stepType || stepIdentity,
              startedAt: event.timestamp || Date.now(),
              status: "running",
              stepType: stepRequest.stepType || undefined,
            });
          }
        }

        const completedStep = extractStepCompleted(event);
        if (completedStep) {
          const existingStep = steps.find(
            (step) =>
              step.id === completedStep.stepId || step.name === completedStep.stepId
          );
          if (existingStep) {
            existingStep.error = completedStep.error;
            existingStep.finishedAt = event.timestamp || Date.now();
            existingStep.output = completedStep.output;
            existingStep.status = completedStep.success === false ? "error" : "done";
          } else {
            steps.push({
              error: completedStep.error,
              finishedAt: event.timestamp || Date.now(),
              id: completedStep.stepId,
              name: completedStep.stepId,
              output: completedStep.output,
              startedAt: event.timestamp || Date.now(),
              status: completedStep.success === false ? "error" : "done",
            });
          }
        }

        const stepOutput = extractStepCompletedOutput(event);
        if (stepOutput && !assistantContent) {
          assistantContent = stepOutput;
        }

        const reasoningDelta = extractReasoningDelta(event);
        if (reasoningDelta) {
          thinking += reasoningDelta;
        }

        if (isRawObserved(event)) {
          continue;
        }

        updateAssistantMessage(assistantMessageId, {
          content: assistantContent,
          error: errorText || undefined,
          events: [...events],
          status: errorText ? "error" : "streaming",
          steps: [...steps],
          thinking,
          toolCalls: [...toolCalls],
        });

        setSession({
          actorId,
          commandId,
          endpointId: "chat",
          error: errorText || undefined,
          eventCount: events.length,
          runId,
          scopeId,
          serviceId: selectedService.id,
          status: errorText ? "error" : "running",
          updatedAt: event.timestamp || Date.now(),
        });
      }

      const finalAssistantStatus: ChatMessage["status"] = errorText
        ? "error"
        : "complete";
      const finalSession: ChatSessionState = {
        actorId,
        commandId,
        endpointId: "chat",
        error: errorText || undefined,
        eventCount: events.length,
        runId,
        scopeId,
        serviceId: selectedService.id,
        status: errorText ? "error" : "success",
        updatedAt: Date.now(),
      };

      setMessages((current) => {
        const completedMessages = current.map((message) =>
          message.id === assistantMessageId
            ? {
                ...message,
                content: assistantContent,
                error: errorText || undefined,
                events: [...events],
                status: finalAssistantStatus,
                steps: [...steps],
                thinking,
                toolCalls: [...toolCalls],
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
        controller.signal.aborted && !errorText
          ? "Chat stopped by operator."
          : error instanceof Error
            ? error.message
            : String(error);
      const finalSession: ChatSessionState = {
        actorId,
        commandId,
        endpointId: "chat",
        error: message,
        eventCount: events.length,
        runId,
        scopeId,
        serviceId: selectedService.id,
        status: "error",
        updatedAt: Date.now(),
      };
      setMessages((current) => {
        const erroredMessages = current.map((entry) =>
          entry.id === assistantMessageId
            ? {
                ...entry,
                content: assistantContent,
                error: message,
                events: [...events],
                status: "error" as const,
                steps: [...steps],
                thinking,
                toolCalls: [...toolCalls],
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
                    <ChatMessageBubble key={message.id} message={message} />
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
    </AevatarPageShell>
  );
};

export default ChatPage;
