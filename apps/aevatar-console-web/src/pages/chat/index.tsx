import { useQuery, useQueryClient } from "@tanstack/react-query";
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
import {
  buildConversationHeaders,
  buildConversationModelGroups,
  buildConversationRouteOptions,
  describeConversationRoute,
  normalizeUserLlmRoute,
  trimConversationValue,
} from "./chatConversationConfig";
import {
  buildConversationSessionSnapshot,
  readConversationPreferences,
  resolveConversationRuntimeIdentity,
} from "./chatSessionIdentity";
import { ChatAdvancedConsole } from "./chatAdvancedConsole";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import { ChatOnboardingGuide } from "./chatOnboardingGuide";
import {
  ChatInput,
  ConversationLlmConfigBar,
  ChatToolsMenu,
  ChatMetaStrip,
  ChatMessageBubble,
  ConversationSidebar,
  DebugPanel,
  EmptyChatState,
  LoadingState,
  ServiceSelector,
} from "./chatPresentation";
import {
  buildOnboardingApiKeyErrorPrompt,
  buildOnboardingApiKeyPrompt,
  buildOnboardingCreatingMessage,
  buildOnboardingCustomEndpointErrorPrompt,
  buildOnboardingCustomEndpointPrompt,
  buildOnboardingDonePrompt,
  buildOnboardingEndpointModeErrorPrompt,
  buildOnboardingEndpointModePrompt,
  buildOnboardingProviderErrorPrompt,
  buildOnboardingProviderPrompt,
  buildOnboardingSaveSettingsInput,
  buildOnboardingSuccessPrompt,
  createOnboardingProviderSettings,
  createOnboardingServiceOption,
  getOnboardingComposerPlaceholder,
  hasConfiguredProviders,
  isValidOnboardingEndpoint,
  onboardingServiceId,
  resolveOnboardingEndpointMode,
  resolveOnboardingProviderType,
  redactOnboardingSecret,
  type OnboardingState,
} from "./onboarding";
import type {
  ChatMessage,
  ChatSessionState,
  ConversationMeta,
  PendingApprovalInfo,
  PendingRunInterventionInfo,
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
  previousCreatedAt?: string,
  options?: {
    llmModel?: string;
    llmRoute?: string;
  }
): ConversationMeta {
  const firstUserMessage = messages.find((message) => message.role === "user");
  const title = (firstUserMessage?.content || "Untitled").trim().slice(0, 60);
  const existingMessages = messages.filter((message) => message.status !== "streaming");
  const now = new Date(session.updatedAt || Date.now()).toISOString();
  const sessionSnapshot = buildConversationSessionSnapshot(messages, session, {
    llmModel: options?.llmModel,
    llmRoute: options?.llmRoute,
  });
  const runtimeIdentity = sessionSnapshot?.runtime;
  const preferences = sessionSnapshot?.preferences;

  return {
    actorId: runtimeIdentity?.actorId,
    commandId: runtimeIdentity?.commandId,
    createdAt: previousCreatedAt || now,
    id: conversationId,
    llmModel: preferences?.llmModel,
    llmRoute: preferences?.llmRoute,
    messageCount: existingMessages.length,
    runId: runtimeIdentity?.runId,
    session: sessionSnapshot,
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
  const runtimeIdentity = resolveConversationRuntimeIdentity({
    messages,
    meta,
  });

  return {
    actorId: runtimeIdentity.actorId || "",
    commandId: runtimeIdentity.commandId || "",
    endpointId: "chat",
    error: lastAssistant?.status === "error" ? lastAssistant.error : undefined,
    eventCount: events.length,
    runId: runtimeIdentity.runId || "",
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

function clonePendingRunIntervention(
  pendingRunIntervention?: PendingRunInterventionInfo
): PendingRunInterventionInfo | undefined {
  return pendingRunIntervention ? { ...pendingRunIntervention } : undefined;
}

type RunInterventionActionRequest =
  | { kind: "resume"; value?: string }
  | { kind: "approve"; value?: string }
  | { kind: "reject"; value?: string }
  | { kind: "signal"; value?: string };

function createAssistantStatusMessage(content: string): ChatMessage {
  return createChatMessage("assistant", content, "complete");
}

function createAssistantErrorMessage(error: string): ChatMessage {
  return {
    ...createChatMessage("assistant", "", "error"),
    error,
  };
}

function buildRunInterventionFeedback(
  intervention: PendingRunInterventionInfo,
  action: RunInterventionActionRequest["kind"]
): string {
  if (action === "signal") {
    return `Signal ${intervention.signalName || "continue"} accepted for ${intervention.stepId}.`;
  }

  if (intervention.kind === "human_approval") {
    return action === "reject"
      ? `Rejection submitted for ${intervention.stepId}.`
      : `Approval submitted for ${intervention.stepId}.`;
  }

  return `Input submitted for ${intervention.stepId}.`;
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
    pendingRunIntervention: clonePendingRunIntervention(
      accumulator.pendingRunIntervention
    ),
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
  const queryClient = useQueryClient();
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
  const [activeRunInterventionKey, setActiveRunInterventionKey] = useState<
    string | null
  >(null);
  const [conversations, setConversations] = useState<ConversationMeta[]>([]);
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null);
  const [session, setSession] = useState<ChatSessionState>(createIdleSession());
  const [showDebug, setShowDebug] = useState(false);
  const [sidebarOpen, setSidebarOpen] = useState(true);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [onboardingState, setOnboardingState] = useState<OnboardingState | null>(null);
  const [conversationRoute, setConversationRoute] = useState<string | undefined>(
    undefined
  );
  const [conversationModel, setConversationModel] = useState<string | undefined>(
    undefined
  );

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
  const settingsQuery = useQuery({
    enabled: authSessionQuery.isSuccess,
    queryKey: ["studio-settings"],
    queryFn: () => studioApi.getSettings(),
  });
  const userConfigQuery = useQuery({
    enabled: authSessionQuery.isSuccess,
    queryKey: ["chat", "user-config"],
    queryFn: () => studioApi.getUserConfig(),
  });
  const userConfigModelsQuery = useQuery({
    enabled: authSessionQuery.isSuccess,
    queryKey: ["chat", "user-config-models"],
    queryFn: () => studioApi.getUserConfigModels(),
  });

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
    () => [
      createOnboardingServiceOption(),
      ...buildScopeConsoleServiceOptions(
        servicesQuery.data ?? [],
        bindingQuery.data?.available ? bindingQuery.data.serviceId : undefined,
        {
          chatOnly: true,
        }
      ).map(mapChatServiceOption),
    ],
    [bindingQuery.data?.available, bindingQuery.data?.serviceId, servicesQuery.data]
  );
  const providerConfigured = useMemo(
    () => hasConfiguredProviders(settingsQuery.data?.providers ?? []),
    [settingsQuery.data?.providers]
  );

  const selectedService =
    services.find((service) => service.id === selectedServiceId) ?? null;
  const globalPreferredRoute = normalizeUserLlmRoute(
    userConfigQuery.data?.preferredLlmRoute
  );
  const routeOptions = useMemo(
    () =>
      buildConversationRouteOptions(
        userConfigModelsQuery.data,
        globalPreferredRoute,
        conversationRoute
      ),
    [conversationRoute, globalPreferredRoute, userConfigModelsQuery.data]
  );
  const effectiveRoute =
    conversationRoute !== undefined ? conversationRoute : globalPreferredRoute;
  const effectiveRouteLabel = useMemo(
    () => describeConversationRoute(effectiveRoute, routeOptions),
    [effectiveRoute, routeOptions]
  );
  const effectiveModel =
    trimConversationValue(conversationModel) ||
    trimConversationValue(userConfigQuery.data?.defaultModel) ||
    "";
  const modelGroups = useMemo(
    () =>
      buildConversationModelGroups({
        conversationModel,
        effectiveRoute,
        globalDefaultModel: userConfigQuery.data?.defaultModel,
        models: userConfigModelsQuery.data,
      }),
    [
      conversationModel,
      effectiveRoute,
      userConfigModelsQuery.data,
      userConfigQuery.data?.defaultModel,
    ]
  );
  const conversationHeaders = useMemo(
    () => buildConversationHeaders(conversationRoute, conversationModel),
    [conversationModel, conversationRoute]
  );

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
      setActiveRunInterventionKey(null);
      setActiveConversationId(null);
      setConversationModel(undefined);
      setConversationRoute(undefined);
      setConversations([]);
      setDebugEvents([]);
      setMessages([]);
      setOnboardingState(null);
      setAdvancedOpen(false);
      previousServiceIdRef.current = "";
      setSession(createIdleSession());
      return;
    }

    let cancelled = false;
    setActiveApprovalRequestId(null);
    setActiveRunInterventionKey(null);
    setActiveConversationId(null);
    setAdvancedOpen(false);
    setConversationModel(undefined);
    setConversationRoute(undefined);
    setDebugEvents([]);
    setMessages([]);
    setOnboardingState(null);
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
    const onboardingPreferredServiceId =
      settingsQuery.isSuccess &&
      !providerConfigured &&
      !bindingQuery.data?.available &&
      services.some((service) => service.id === onboardingServiceId)
        ? onboardingServiceId
        : "";
    const preferredServiceId =
      routePreferredServiceId ||
      onboardingPreferredServiceId ||
      (bindingQuery.data?.available ? bindingQuery.data.serviceId : "") ||
      services.find((service) => service.id === nyxIdChatServiceId)?.id ||
      services[0]?.id ||
      "";

    const hasSelectedService =
      selectedServiceId &&
      services.some((service) => service.id === selectedServiceId);
    const canAutoReselect =
      !activeConversationId && messages.length === 0 && !isStreaming;

    if (!hasSelectedService) {
      serviceSelectionSourceRef.current = "auto";
      setSelectedServiceId(preferredServiceId);
      return;
    }

    if (
      serviceSelectionSourceRef.current === "auto" &&
      canAutoReselect &&
      preferredServiceId &&
      selectedServiceId !== preferredServiceId
    ) {
      setSelectedServiceId(preferredServiceId);
    }
  }, [
    activeConversationId,
    bindingQuery.data?.available,
    bindingQuery.data?.serviceId,
    isStreaming,
    messages.length,
    providerConfigured,
    routeServiceId,
    selectedServiceId,
    settingsQuery.isSuccess,
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
    setActiveRunInterventionKey(null);
    setActiveConversationId(null);
    setConversationModel(undefined);
    setConversationRoute(undefined);
    setDebugEvents([]);
    setMessages([]);
    setSession(createIdleSession(scopeId, selectedServiceId));
  }, [scopeId, selectedServiceId]);

  useEffect(() => {
    if (selectedService?.kind !== "onboarding") {
      setOnboardingState(null);
      return;
    }

    if (messages.length > 0 || onboardingState) {
      return;
    }

    setOnboardingState({ step: "select_provider" });
    setMessages([
      createChatMessage(
        "assistant",
        buildOnboardingProviderPrompt(settingsQuery.data?.providerTypes ?? [])
      ),
    ]);
    setSession(createIdleSession(scopeId, onboardingServiceId));
  }, [
    messages.length,
    onboardingState,
    scopeId,
    selectedService?.kind,
    settingsQuery.data?.providerTypes,
  ]);

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
        previousMeta?.createdAt,
        {
          llmModel: conversationModel,
          llmRoute: conversationRoute,
        }
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
    [conversationModel, conversationRoute, conversations, scopeId]
  );

  const handleNewChat = useCallback(() => {
    abortControllerRef.current?.abort();
    setPrompt("");
    setAdvancedOpen(false);
    setActiveApprovalRequestId(null);
    setActiveRunInterventionKey(null);
    setActiveConversationId(null);
    setDebugEvents([]);
    setMessages([]);
    setIsStreaming(false);
    setConversationModel(undefined);
    setConversationRoute(undefined);
    setOnboardingState(null);
    setSession(createIdleSession(scopeId, selectedServiceId));
    nyxIdChatBoundRef.current = false;
  }, [scopeId, selectedServiceId]);

  const handleStartOnboarding = useCallback(() => {
    serviceSelectionSourceRef.current = "manual";
    setSelectedServiceId(onboardingServiceId);
  }, []);

  const persistConversationOverrides = useCallback(
    async (nextRoute: string | undefined, nextModel: string | undefined) => {
      if (
        !scopeId ||
        !activeConversationId ||
        !selectedService ||
        isStreaming ||
        messages.length === 0
      ) {
        return;
      }

      const existingMeta = conversations.find(
        (conversation) => conversation.id === activeConversationId
      );
      const nextMeta = buildConversationMeta(
        activeConversationId,
        messages,
        {
          ...session,
          updatedAt: Date.now(),
        },
        selectedService,
        existingMeta?.createdAt,
        {
          llmModel: nextModel,
          llmRoute: nextRoute,
        }
      );

      setConversations((current) => [
        nextMeta,
        ...current.filter((conversation) => conversation.id !== activeConversationId),
      ]);

      await chatHistoryApi.saveConversation(
        scopeId,
        nextMeta,
        serializeChatMessages(messages)
      );
    },
    [
      activeConversationId,
      conversations,
      isStreaming,
      messages,
      scopeId,
      selectedService,
      session,
    ]
  );

  const handleConversationRouteChange = useCallback(
    (value: string | undefined) => {
      setConversationRoute(value);
      void persistConversationOverrides(value, conversationModel);
    },
    [conversationModel, persistConversationOverrides]
  );

  const handleConversationModelChange = useCallback(
    (value: string | undefined) => {
      const normalized = trimConversationValue(value);
      setConversationModel(normalized);
      void persistConversationOverrides(conversationRoute, normalized);
    },
    [conversationRoute, persistConversationOverrides]
  );

  const handleResetConversationLlm = useCallback(() => {
    setConversationModel(undefined);
    setConversationRoute(undefined);
    void persistConversationOverrides(undefined, undefined);
  }, [persistConversationOverrides]);

  const handleCreate = useCallback(() => {
    history.push(buildStudioWorkflowEditorRoute());
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
      const preferences = readConversationPreferences(meta);

      setActiveConversationId(conversationId);
      setActiveApprovalRequestId(null);
      setActiveRunInterventionKey(null);
      setConversationModel(preferences.llmModel);
      setConversationRoute(preferences.llmRoute);
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
        setActiveRunInterventionKey(null);
        setActiveConversationId(null);
        setConversationModel(undefined);
        setConversationRoute(undefined);
        setDebugEvents([]);
        setMessages([]);
        setSession(createIdleSession(scopeId, selectedServiceId));
      }

      await chatHistoryApi.deleteConversation(scopeId, conversationId);
    },
    [activeConversationId, scopeId, selectedServiceId]
  );

  const handleOnboardingSend = useCallback(
    async (input: string) => {
      if (!scopeId || !selectedService || selectedService.kind !== "onboarding") {
        return false;
      }

      const conversationId = activeConversationId || createConversationId();
      const providerTypes = settingsQuery.data?.providerTypes ?? [];
      const currentState = onboardingState ?? { step: "select_provider" as const };
      const trimmedInput = input.trim();
      const createOnboardingSession = (
        status: ChatSessionState["status"] = "success"
      ): ChatSessionState => ({
        ...createIdleSession(scopeId, selectedService.id),
        status,
        updatedAt: Date.now(),
      });
      const commitOnboardingMessages = async (
        nextMessages: ChatMessage[],
        nextSession: ChatSessionState = createOnboardingSession()
      ) => {
        setActiveConversationId(conversationId);
        setMessages(nextMessages);
        setSession(nextSession);
        setDebugEvents([]);
        setActiveApprovalRequestId(null);
        await persistConversationState(
          conversationId,
          nextMessages,
          nextSession,
          selectedService
        );
      };

      const selectedProviderType = currentState.providerTypeId
        ? providerTypes.find(
            (providerType) => providerType.id === currentState.providerTypeId
          ) || null
        : null;

      const createAssistantReply = (content: string, status: ChatMessage["status"] = "complete") =>
        createChatMessage("assistant", content, status);

      if (currentState.step === "done") {
        const shouldRestart = ["restart", "reset", "start over"].includes(
          trimmedInput.toLowerCase()
        );
        const nextState = shouldRestart
          ? { step: "select_provider" as const }
          : currentState;
        const nextMessages = [
          ...messages,
          createChatMessage("user", trimmedInput),
          createAssistantReply(
            shouldRestart
              ? buildOnboardingProviderPrompt(providerTypes)
              : buildOnboardingDonePrompt()
          ),
        ];
        setOnboardingState(nextState);
        await commitOnboardingMessages(nextMessages);
        return true;
      }

      if (currentState.step === "select_provider") {
        const providerType = resolveOnboardingProviderType(trimmedInput, providerTypes);
        const nextMessages = [
          ...messages,
          createChatMessage("user", trimmedInput),
          createAssistantReply(
            providerType
              ? providerType.defaultEndpoint.trim()
                ? buildOnboardingEndpointModePrompt(providerType)
                : buildOnboardingCustomEndpointPrompt(providerType.displayName)
              : buildOnboardingProviderErrorPrompt(providerTypes)
          ),
        ];
        setOnboardingState(
          providerType
            ? providerType.defaultEndpoint.trim()
              ? {
                  providerTypeId: providerType.id,
                  providerTypeLabel: providerType.displayName,
                  step: "select_endpoint_mode",
                }
              : {
                  providerTypeId: providerType.id,
                  providerTypeLabel: providerType.displayName,
                  step: "ask_custom_endpoint",
                }
            : { step: "select_provider" }
        );
        await commitOnboardingMessages(nextMessages);
        return true;
      }

      if (!selectedProviderType) {
        const nextMessages = [
          ...messages,
          createChatMessage("user", trimmedInput),
          createAssistantReply(buildOnboardingProviderPrompt(providerTypes)),
        ];
        setOnboardingState({ step: "select_provider" });
        await commitOnboardingMessages(nextMessages);
        return true;
      }

      if (currentState.step === "select_endpoint_mode") {
        const endpointMode = resolveOnboardingEndpointMode(trimmedInput);
        const nextState =
          endpointMode === "default"
            ? {
                endpointUrl: selectedProviderType.defaultEndpoint,
                providerTypeId: selectedProviderType.id,
                providerTypeLabel: selectedProviderType.displayName,
                step: "ask_api_key" as const,
              }
            : endpointMode === "custom"
              ? {
                  providerTypeId: selectedProviderType.id,
                  providerTypeLabel: selectedProviderType.displayName,
                  step: "ask_custom_endpoint" as const,
                }
              : currentState;
        const nextMessages = [
          ...messages,
          createChatMessage("user", trimmedInput),
          createAssistantReply(
            endpointMode
              ? endpointMode === "default"
                ? buildOnboardingApiKeyPrompt(
                    selectedProviderType.displayName,
                    selectedProviderType.defaultEndpoint
                  )
                : buildOnboardingCustomEndpointPrompt(
                    selectedProviderType.displayName
                  )
              : buildOnboardingEndpointModeErrorPrompt(selectedProviderType)
          ),
        ];
        setOnboardingState(nextState);
        await commitOnboardingMessages(nextMessages);
        return true;
      }

      if (currentState.step === "ask_custom_endpoint") {
        const nextMessages = [
          ...messages,
          createChatMessage("user", trimmedInput),
          createAssistantReply(
            isValidOnboardingEndpoint(trimmedInput)
              ? buildOnboardingApiKeyPrompt(
                  selectedProviderType.displayName,
                  trimmedInput
                )
              : buildOnboardingCustomEndpointErrorPrompt(
                  selectedProviderType.displayName
                )
          ),
        ];
        setOnboardingState(
          isValidOnboardingEndpoint(trimmedInput)
            ? {
                endpointUrl: trimmedInput,
                providerTypeId: selectedProviderType.id,
                providerTypeLabel: selectedProviderType.displayName,
                step: "ask_api_key",
              }
            : currentState
        );
        await commitOnboardingMessages(nextMessages);
        return true;
      }

      if (currentState.step === "ask_api_key") {
        if (!trimmedInput) {
          const nextMessages = [
            ...messages,
            createChatMessage("user", "API key provided"),
            createAssistantReply(
              buildOnboardingApiKeyErrorPrompt(
                currentState.providerTypeLabel || selectedProviderType.displayName,
                currentState.endpointUrl || selectedProviderType.defaultEndpoint,
                "The API key cannot be empty."
              )
            ),
          ];
          await commitOnboardingMessages(nextMessages);
          return true;
        }

        if (!settingsQuery.data) {
          const nextMessages = [
            ...messages,
            createChatMessage("user", redactOnboardingSecret(trimmedInput)),
            createAssistantReply(
              buildOnboardingApiKeyErrorPrompt(
                currentState.providerTypeLabel || selectedProviderType.displayName,
                currentState.endpointUrl || selectedProviderType.defaultEndpoint,
                "Studio Settings are still loading. Try again in a moment."
              )
            ),
          ];
          await commitOnboardingMessages(nextMessages);
          return true;
        }

        const creatingMessage = createAssistantReply(
          buildOnboardingCreatingMessage(
            currentState.providerTypeLabel || selectedProviderType.displayName
          ),
          "streaming"
        );
        const creatingMessages = [
          ...messages,
          createChatMessage("user", redactOnboardingSecret(trimmedInput)),
          creatingMessage,
        ];
        const creatingSession = createOnboardingSession("running");
        setOnboardingState({
          ...currentState,
          step: "creating",
        });
        setPrompt("");
        setIsStreaming(true);
        await commitOnboardingMessages(creatingMessages, creatingSession);

        try {
          const nextProvider = createOnboardingProviderSettings(
            settingsQuery.data,
            selectedProviderType.id,
            trimmedInput,
            currentState.endpointUrl || selectedProviderType.defaultEndpoint
          );
          const response = await studioApi.saveSettings(
            buildOnboardingSaveSettingsInput(settingsQuery.data, nextProvider)
          );
          queryClient.setQueryData(["studio-settings"], response);
          serviceSelectionSourceRef.current = "manual";
          const completedMessages = creatingMessages.map((message) =>
            message.id === creatingMessage.id
              ? {
                  ...message,
                  content: buildOnboardingSuccessPrompt(
                    nextProvider.providerName,
                    currentState.providerTypeLabel || selectedProviderType.displayName
                  ),
                  status: "complete" as const,
                }
              : message
          );
          setOnboardingState({
            endpointUrl:
              currentState.endpointUrl || selectedProviderType.defaultEndpoint,
            providerTypeId: selectedProviderType.id,
            providerTypeLabel:
              currentState.providerTypeLabel || selectedProviderType.displayName,
            step: "done",
          });
          setIsStreaming(false);
          await commitOnboardingMessages(
            completedMessages,
            createOnboardingSession("success")
          );
        } catch (error) {
          const completedMessages = creatingMessages.map((message) =>
            message.id === creatingMessage.id
              ? {
                  ...message,
                  content: buildOnboardingApiKeyErrorPrompt(
                    currentState.providerTypeLabel || selectedProviderType.displayName,
                    currentState.endpointUrl || selectedProviderType.defaultEndpoint,
                    error instanceof Error
                      ? error.message
                      : "Saving the provider failed."
                  ),
                  status: "complete" as const,
                }
              : message
          );
          setOnboardingState({
            endpointUrl:
              currentState.endpointUrl || selectedProviderType.defaultEndpoint,
            providerTypeId: selectedProviderType.id,
            providerTypeLabel:
              currentState.providerTypeLabel || selectedProviderType.displayName,
            step: "ask_api_key",
          });
          setIsStreaming(false);
          await commitOnboardingMessages(
            completedMessages,
            createOnboardingSession("error")
          );
        }

        return true;
      }

      return false;
    },
    [
      activeConversationId,
      messages,
      onboardingState,
      persistConversationState,
      queryClient,
      scopeId,
      selectedService,
      settingsQuery.data,
    ]
  );

  const handleSend = useCallback(async () => {
    const trimmedPrompt = prompt.trim();
    if (!scopeId || !selectedService || !trimmedPrompt || isStreaming) {
      return;
    }

    setPrompt("");

    if (selectedService.kind === "onboarding") {
      await handleOnboardingSend(trimmedPrompt);
      return;
    }

    const conversationId = activeConversationId || createConversationId();
    const userMessage = createChatMessage("user", trimmedPrompt);
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
    setDebugEvents([]);
    setIsStreaming(true);
    setActiveApprovalRequestId(null);
    setActiveRunInterventionKey(null);
    setSession({
      ...createIdleSession(scopeId, selectedService.id),
      status: "running",
      updatedAt: Date.now(),
    });
    const accumulator = createRuntimeEventAccumulator();

    try {
      if (selectedService.kind === "nyxid-chat") {
        await ensureNyxIdChatBound();
      }

      const response = await runtimeRunsApi.streamChat(
        scopeId,
        {
          metadata: conversationHeaders,
          prompt: trimmedPrompt,
          sessionId: conversationId,
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
    conversationHeaders,
    handleOnboardingSend,
    ensureNyxIdChatBound,
    isStreaming,
    messages,
    persistConversationState,
    prompt,
    scopeId,
    selectedService,
    updateAssistantMessage,
  ]);

  const handleSelectOnboardingProvider = useCallback(
    (providerTypeId: string) => {
      void handleOnboardingSend(providerTypeId);
    },
    [handleOnboardingSend]
  );

  const handleSelectOnboardingEndpointMode = useCallback(
    (mode: "custom" | "default") => {
      void handleOnboardingSend(mode);
    },
    [handleOnboardingSend]
  );

  const handleSubmitOnboardingCustomEndpoint = useCallback(
    (value: string) => {
      void handleOnboardingSend(value);
    },
    [handleOnboardingSend]
  );

  const handleSubmitOnboardingApiKey = useCallback(
    (value: string) => {
      void handleOnboardingSend(value);
    },
    [handleOnboardingSend]
  );

  const handleRestartOnboarding = useCallback(() => {
    void handleOnboardingSend("restart");
  }, [handleOnboardingSend]);

  const handleOpenNyxIdChat = useCallback(() => {
    serviceSelectionSourceRef.current = "manual";
    setSelectedServiceId(nyxIdChatServiceId);
  }, []);

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
        resolveConversationRuntimeIdentity({
          messages,
          meta: conversations.find(
            (conversation) => conversation.id === activeConversationId
          ),
          session,
        }).actorId || "";

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
      const approvalRuntimeIdentity = resolveConversationRuntimeIdentity({
        messages,
        meta: conversations.find(
          (conversation) => conversation.id === activeConversationId
        ),
        session,
      });
      accumulator.assistantText = targetMessage.content;
      accumulator.commandId =
        approvalRuntimeIdentity.commandId || session.commandId;
      accumulator.events = [...(targetMessage.events ?? [])];
      accumulator.runId = approvalRuntimeIdentity.runId || session.runId;
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

  const handleRunInterventionAction = useCallback(
    async (
      messageId: string,
      intervention: PendingRunInterventionInfo,
      action: RunInterventionActionRequest
    ) => {
      if (!scopeId || !selectedService) {
        return;
      }

      const conversationId = activeConversationId || createConversationId();
      const actorId =
        intervention.actorId ||
        resolveConversationRuntimeIdentity({
          messages,
          meta: conversations.find(
            (conversation) => conversation.id === conversationId
          ),
          session,
        }).actorId ||
        "";
      const runId =
        intervention.runId ||
        resolveConversationRuntimeIdentity({
          messages,
          meta: conversations.find(
            (conversation) => conversation.id === conversationId
          ),
          session,
        }).runId ||
        session.runId;

      if (intervention.kind === "human_input" && !trimConversationValue(action.value)) {
        return;
      }

      if (!runId || !intervention.stepId || !actorId) {
        const unavailableMessage =
          "Unable to submit the runtime action because the run identity is incomplete.";
        const errorNote = createAssistantErrorMessage(unavailableMessage);
        const nextSession = {
          ...session,
          updatedAt: Date.now(),
        };

        setActiveConversationId(conversationId);
        setMessages((current) => {
          const updated = [...current, errorNote];
          void persistConversationState(
            conversationId,
            updated,
            nextSession,
            selectedService
          );
          return updated;
        });
        setSession(nextSession);
        return;
      }

      setActiveRunInterventionKey(intervention.key);

      try {
        if (action.kind === "signal") {
          const result = await runtimeRunsApi.signal(
            scopeId,
            {
              actorId,
              payload: trimConversationValue(action.value),
              runId,
              signalName: intervention.signalName || "continue",
              stepId: intervention.stepId,
            },
            {
              serviceId: selectedService.id,
            }
          );

          if (!result.accepted) {
            throw new Error("Runtime did not accept the signal request.");
          }

          const nextSession: ChatSessionState = {
            ...session,
            actorId: result.actorId || actorId,
            commandId: result.commandId || session.commandId,
            error: undefined,
            runId: result.runId || runId,
            scopeId,
            serviceId: selectedService.id,
            status: "running",
            updatedAt: Date.now(),
          };
          const note = createAssistantStatusMessage(
            buildRunInterventionFeedback(intervention, action.kind)
          );

          setActiveConversationId(conversationId);
          setMessages((current) => {
            const updated = current.map((message) =>
              message.id === messageId
                ? {
                    ...message,
                    pendingRunIntervention: undefined,
                  }
                : message
            ) as ChatMessage[];
            updated.push(note);
            void persistConversationState(
              conversationId,
              updated,
              nextSession,
              selectedService
            );
            return updated;
          });
          setSession(nextSession);
          return;
        }

        const result = await runtimeRunsApi.resume(
          scopeId,
          {
            actorId,
            approved: action.kind !== "reject",
            runId,
            stepId: intervention.stepId,
            userInput: trimConversationValue(action.value),
          },
          {
            serviceId: selectedService.id,
          }
        );

        if (!result.accepted) {
          throw new Error("Runtime did not accept the resume request.");
        }

        const nextSession: ChatSessionState = {
          ...session,
          actorId: result.actorId || actorId,
          commandId: result.commandId || session.commandId,
          error: undefined,
          runId: result.runId || runId,
          scopeId,
          serviceId: selectedService.id,
          status: "running",
          updatedAt: Date.now(),
        };
        const note = createAssistantStatusMessage(
          buildRunInterventionFeedback(intervention, action.kind)
        );

        setActiveConversationId(conversationId);
        setMessages((current) => {
          const updated = current.map((message) =>
            message.id === messageId
              ? {
                  ...message,
                  pendingRunIntervention: undefined,
                }
              : message
          ) as ChatMessage[];
          updated.push(note);
          void persistConversationState(
            conversationId,
            updated,
            nextSession,
            selectedService
          );
          return updated;
        });
        setSession(nextSession);
      } catch (error) {
        const errorMessage =
          error instanceof Error
            ? error.message
            : "Failed to submit the runtime action.";
        const errorNote = createAssistantErrorMessage(errorMessage);
        const nextSession = {
          ...session,
          updatedAt: Date.now(),
        };

        setActiveConversationId(conversationId);
        setMessages((current) => {
          const updated = [...current, errorNote];
          void persistConversationState(
            conversationId,
            updated,
            nextSession,
            selectedService
          );
          return updated;
        });
        setSession(nextSession);
      } finally {
        setActiveRunInterventionKey((current) =>
          current === intervention.key ? null : current
        );
      }
    },
    [
      activeConversationId,
      conversations,
      persistConversationState,
      scopeId,
      selectedService,
      session,
    ]
  );

  const handleAdvancedTimelineActionResult = useCallback(
    (result: {
      action: "resume" | "approve" | "reject" | "signal";
      actorId: string;
      commandId?: string;
      content: string;
      error?: string;
      kind: "human_input" | "human_approval" | "wait_signal";
      runId: string;
      serviceId: string;
      signalName?: string;
      stepId: string;
      success: boolean;
    }) => {
      if (!scopeId) {
        return;
      }

      const conversationId = activeConversationId || createConversationId();
      const matchedService =
        services.find((service) => service.id === result.serviceId) ||
        (selectedService?.id === result.serviceId ? selectedService : undefined);
      const targetService: ServiceOption =
        matchedService ||
        selectedService || {
          endpoints: [],
          id: result.serviceId,
          kind: "service",
          label: result.serviceId,
        };
      const nextSession: ChatSessionState = {
        ...session,
        actorId: result.actorId || session.actorId,
        commandId: result.commandId || session.commandId,
        error: result.success ? undefined : result.error || result.content,
        runId: result.runId || session.runId,
        scopeId,
        serviceId: targetService.id,
        status: result.success ? "running" : "error",
        updatedAt: Date.now(),
      };
      const note = result.success
        ? createAssistantStatusMessage(result.content)
        : createAssistantErrorMessage(result.error || result.content);

      setActiveConversationId(conversationId);
      setActiveRunInterventionKey(null);
      setMessages((current) => {
        const updated = current.map((message) => {
          const pending = message.pendingRunIntervention;
          if (!pending) {
            return message;
          }

          const sameSignal =
            result.kind !== "wait_signal" ||
            pending.signalName === result.signalName;
          const sameIntervention =
            pending.kind === result.kind &&
            pending.runId === result.runId &&
            pending.stepId === result.stepId &&
            sameSignal;

          return sameIntervention
            ? {
                ...message,
                pendingRunIntervention: undefined,
              }
            : message;
        }) as ChatMessage[];

        updated.push(note);
        void persistConversationState(
          conversationId,
          updated,
          nextSession,
          targetService
        );
        return updated;
      });
      setSession(nextSession);
    },
    [
      activeConversationId,
      persistConversationState,
      scopeId,
      selectedService,
      services,
      session,
    ]
  );

  const handleStop = useCallback(() => {
    abortControllerRef.current?.abort();
  }, []);

  const isLoadingScope =
    authSessionQuery.isLoading || (scopeId.length > 0 && servicesQuery.isLoading);
  const isOnboardingSelected = selectedService?.kind === "onboarding";
  const composerDisabled =
    !scopeId ||
    !selectedService ||
    (isOnboardingSelected && onboardingState?.step === "creating");
  const composerPlaceholder = isOnboardingSelected
    ? getOnboardingComposerPlaceholder(onboardingState)
    : undefined;

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
              <ChatToolsMenu
                advancedOpen={advancedOpen}
                eventStreamOpen={showDebug}
                onToggleAdvanced={() => setAdvancedOpen((value) => !value)}
                onToggleEventStream={() => setShowDebug((value) => !value)}
              />
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
                  {scopeId && selectedService?.kind === "onboarding" ? (
                    <ChatOnboardingGuide
                      loading={settingsQuery.isLoading}
                      onChooseEndpointMode={handleSelectOnboardingEndpointMode}
                      onRestart={handleRestartOnboarding}
                      onSelectProvider={handleSelectOnboardingProvider}
                      onSubmitApiKey={handleSubmitOnboardingApiKey}
                      onSubmitCustomEndpoint={handleSubmitOnboardingCustomEndpoint}
                      onSwitchToChat={handleOpenNyxIdChat}
                      providerTypes={settingsQuery.data?.providerTypes ?? []}
                      state={onboardingState}
                    />
                  ) : null}
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
                ) : messages.length === 0 &&
                  selectedService.kind !== "onboarding" ? (
                  <EmptyChatState
                    actionLabel={
                      settingsQuery.isSuccess &&
                      !providerConfigured &&
                      selectedService.kind === "nyxid-chat"
                        ? "Start onboarding"
                        : undefined
                    }
                    description={
                      selectedService.kind === "nyxid-chat"
                        ? settingsQuery.isSuccess && !providerConfigured
                          ? "Connect a provider and save it to Studio Settings before starting your first NyxID conversation."
                          : "Chat with NyxID about services, credentials, and configuration."
                        : `Invoke the "${selectedService.label}" service with a chat message.`
                    }
                    footnote={
                      selectedService.kind === "nyxid-chat"
                        ? "Use Tools when you need runtime evidence, timeline context, or low-level event inspection."
                        : "Open Tools for deeper runtime inspection when a normal chat message is not enough."
                    }
                    highlights={
                      selectedService.kind === "nyxid-chat"
                        ? settingsQuery.isSuccess && !providerConfigured
                          ? [
                              "Connect a provider before starting the first NyxID conversation.",
                              "Once configured, ask NyxID for help with services, credentials, or runtime setup.",
                              "Open Tools only when you need audit evidence or protocol-level detail.",
                            ]
                          : [
                              "Ask NyxID to inspect services, credentials, or scope bindings.",
                              "Use natural-language prompts first, then open Tools for deeper runtime evidence.",
                              "Keep model and route overrides in the composer footer when you need a specific provider path.",
                            ]
                        : [
                            `Start with a natural-language prompt for ${selectedService.label}.`,
                            "Use Advanced Console when you need a specific endpoint, actor, or audit trail.",
                            "Open Event Stream only when you need raw AGUI evidence for debugging.",
                          ]
                    }
                    onAction={
                      settingsQuery.isSuccess &&
                      !providerConfigured &&
                      selectedService.kind === "nyxid-chat"
                        ? handleStartOnboarding
                        : undefined
                    }
                    title={selectedService.label}
                  />
                ) : (
                  messages.map((message) => (
                    <ChatMessageBubble
                      activeApprovalRequestId={activeApprovalRequestId}
                      activeRunInterventionKey={activeRunInterventionKey}
                      key={message.id}
                      message={message}
                      onApprovalDecision={(requestId, approved) => {
                        void handleApprovalDecision(requestId, approved);
                      }}
                      onRunInterventionAction={(messageId, intervention, action) => {
                        void handleRunInterventionAction(
                          messageId,
                          intervention,
                          action
                        );
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
                  disabled={composerDisabled}
                  footer={
                    isOnboardingSelected ? undefined : (
                      <ConversationLlmConfigBar
                        disabled={isStreaming || !scopeId || !selectedService}
                        effectiveModel={effectiveModel}
                        effectiveRoute={effectiveRoute}
                        effectiveRouteLabel={effectiveRouteLabel}
                        modelGroups={modelGroups}
                        modelValue={conversationModel}
                        modelsLoading={
                          userConfigModelsQuery.isLoading ||
                          Boolean(userConfigModelsQuery.isFetching)
                        }
                        onModelChange={handleConversationModelChange}
                        onReset={handleResetConversationLlm}
                        onRouteChange={handleConversationRouteChange}
                        routeOptions={routeOptions}
                        routeValue={conversationRoute}
                      />
                    )
                  }
                  isStreaming={isOnboardingSelected ? false : isStreaming}
                  onChange={setPrompt}
                  onSend={() => void handleSend()}
                  onStop={handleStop}
                  placeholder={composerPlaceholder}
                  value={prompt}
                />
                {isOnboardingSelected ? null : (
                  <ChatMetaStrip
                    actorId={session.actorId}
                    commandId={session.commandId}
                    modelLabel={effectiveModel || "provider default"}
                    runId={session.runId}
                    routeLabel={effectiveRouteLabel}
                    scopeId={scopeId}
                    serviceId={selectedService?.id}
                  />
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
      <ChatAdvancedConsole
        defaultServiceId={selectedServiceId}
        onClose={() => setAdvancedOpen(false)}
        onEnsureNyxIdBound={ensureNyxIdChatBound}
        onTimelineActionResult={handleAdvancedTimelineActionResult}
        open={advancedOpen}
        scopeId={scopeId}
        services={servicesQuery.data ?? []}
        sessionActorId={session.actorId || undefined}
      />
    </AevatarPageShell>
  );
};

export default ChatPage;
