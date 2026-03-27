import {
  useHumanInteraction,
  useRunSession,
} from "@aevatar-react-sdk/agui";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import {
  AGUIEventType,
  type ChatRunRequest,
  CustomEventName,
  type WorkflowResumeRequest,
  type WorkflowSignalRequest,
} from "@aevatar-react-sdk/types";
import { InfoCircleOutlined } from "@ant-design/icons";
import type { ProFormInstance } from "@ant-design/pro-components";
import {
  PageContainer,
  ProDescriptions,
  ProForm,
  ProFormSwitch,
  ProFormTextArea,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  Button,
  Divider,
  Drawer,
  Empty,
  message,
  Popover,
  Space,
  Tabs,
  Typography,
} from "antd";
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  getLatestCustomEventData,
  parseStepRequestData,
  parseWaitingSignalData,
} from "@/shared/agui/customEventData";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import {
  clearRecentRuns,
  loadRecentRuns,
  type RecentRunEntry,
  saveRecentRun,
} from "@/shared/runs/recentRuns";
import { isAutoEncodableTextPayloadTypeUrl } from "@/shared/runs/protobufPayload";
import {
  deleteDraftRunPayload as deleteQueuedDraftRunPayload,
  isEndpointInvocationDraftPayload,
  isScopeDraftRunPayload,
  loadDraftRunPayload as loadQueuedDraftRunPayload,
} from "@/shared/runs/draftRunSession";
import {
  buildWorkflowCatalogOptions,
  findWorkflowCatalogItem,
  listVisibleWorkflowCatalogItems,
} from "@/shared/workflows/catalogVisibility";
import {
  cardStackStyle,
  drawerBodyStyle,
  drawerScrollStyle,
  embeddedPanelStyle,
} from "@/shared/ui/proComponents";
import RunsInspectorPane from "./components/RunsInspectorPane";
import RunsLaunchRail from "./components/RunsLaunchRail";
import RunsStatusStrip from "./components/RunsStatusStrip";
import RunsTracePane from "./components/RunsTracePane";
import RunsTimelineView from "./components/RunsTimelineView";
import {
  buildTimelineGroups,
  buildEventRows,
  isHumanApprovalSuspension,
  type RunEventRow,
  type RunTimelineGroup,
  type RunTransport,
} from "./runEventPresentation";
import {
  builtInPresets,
  composerRailDefaultWidth,
  composerRailKeyboardStep,
  type ConsoleViewKey,
  defaultRunRouteName,
  formatElapsedDuration,
  humanInputColumns,
  type HumanInputRecord,
  readInitialRunFormValues,
  type RecentRunTableRow,
  type ResumeFormValues,
  type RunFocusRecord,
  type RunFormValues,
  type RunStatusValue,
  runStatusValueEnum,
  runsWorkbenchComposerRailStyle,
  runsWorkbenchMainStyle,
  runsWorkbenchMonitorStyle,
  runsWorkbenchResizeHandleStyle,
  runsWorkbenchResizeRailStyle,
  runsWorkbenchShellStyle,
  resolveResponsiveComposerWidth,
  type RunSummaryRecord,
  type SelectedRouteRecord,
  type SignalFormValues,
  trimOptional,
  waitingSignalColumns,
  type WaitingSignalRecord,
  workbenchConsoleScrollStyle,
  workbenchConsoleSurfaceStyle,
  workbenchEventHeaderStyle,
  workbenchEventRowStyle,
  workbenchMessageListStyle,
  workbenchOverviewGridStyle,
} from "./runWorkbenchConfig";

const runsWorkbenchHeaderBarStyle: React.CSSProperties = {
  alignItems: "center",
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  display: "flex",
  flexWrap: "wrap",
  gap: 16,
  justifyContent: "space-between",
  padding: "14px 16px",
};

const runsWorkbenchHeaderTitleStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  gap: 8,
  minWidth: 0,
};

const runsWorkbenchHeaderActionStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "flex-end",
};

function resolveRequestedServiceId(
  request: Pick<RunFormValues, "endpointId" | "routeName" | "serviceOverrideId">,
  draftMode: boolean
): string {
  if (draftMode) {
    return "";
  }

  const normalizedEndpointId = trimOptional(request.endpointId) || "chat";
  const normalizedServiceOverrideId =
    trimOptional(request.serviceOverrideId) || "";
  if (normalizedEndpointId !== "chat") {
    return normalizedServiceOverrideId;
  }

  return normalizedServiceOverrideId || trimOptional(request.routeName) || "";
}

const RunsPage: React.FC = () => {
  const [messageApi, messageContextHolder] = message.useMessage();
  const urlInitialFormValues = useMemo(() => readInitialRunFormValues(), []);
  const draftRunKey = useMemo(() => {
    if (typeof window === "undefined") {
      return "";
    }

    return new URLSearchParams(window.location.search).get("draftKey") ?? "";
  }, []);
  const draftRunPayload = useMemo(
    () => loadQueuedDraftRunPayload(draftRunKey),
    [draftRunKey]
  );
  const scopeDraftPayload = useMemo(
    () =>
      isScopeDraftRunPayload(draftRunPayload) ? draftRunPayload : undefined,
    [draftRunPayload]
  );
  const endpointInvocationDraftPayload = useMemo(
    () =>
      isEndpointInvocationDraftPayload(draftRunPayload)
        ? draftRunPayload
        : undefined,
    [draftRunPayload]
  );
  const initialFormValues = useMemo(
    () => ({
      ...urlInitialFormValues,
      routeName: endpointInvocationDraftPayload
        ? undefined
        : urlInitialFormValues.routeName,
      prompt:
        endpointInvocationDraftPayload?.prompt ?? urlInitialFormValues.prompt,
      serviceOverrideId:
        endpointInvocationDraftPayload?.serviceOverrideId ??
        urlInitialFormValues.serviceOverrideId,
      endpointId:
        endpointInvocationDraftPayload?.endpointId ??
        urlInitialFormValues.endpointId,
      payloadTypeUrl:
        endpointInvocationDraftPayload?.payloadTypeUrl ??
        urlInitialFormValues.payloadTypeUrl,
      payloadBase64:
        endpointInvocationDraftPayload?.payloadBase64 ??
        urlInitialFormValues.payloadBase64,
    }),
    [endpointInvocationDraftPayload, urlInitialFormValues]
  );
  const composerFormRef = useRef<ProFormInstance<RunFormValues> | undefined>(
    undefined
  );
  const runsWorkbenchMainRef = useRef<HTMLDivElement | null>(null);
  const resumeFormRef = useRef<ProFormInstance<ResumeFormValues> | undefined>(
    undefined
  );
  const signalFormRef = useRef<ProFormInstance<SignalFormValues> | undefined>(
    undefined
  );
  const [catalogSearch, setCatalogSearch] = useState("");
  const [selectedRouteName, setSelectedRouteName] = useState(
    scopeDraftPayload?.bundleName ??
      (endpointInvocationDraftPayload ? "" : undefined) ??
      initialFormValues.routeName ??
      defaultRunRouteName
  );
  const [recentRuns, setRecentRuns] = useState<RecentRunEntry[]>(() =>
    loadRecentRuns()
  );
  const [selectedTransport, setSelectedTransport] = useState<RunTransport>(
    initialFormValues.transport
  );
  const [selectedTraceItemKey, setSelectedTraceItemKey] = useState("");
  const [composerWidth, setComposerWidth] = useState(composerRailDefaultWidth);
  const [activeTransport, setActiveTransport] = useState<RunTransport>(
    initialFormValues.transport
  );
  const [consoleView, setConsoleView] = useState<ConsoleViewKey>("timeline");
  const [isInspectorDrawerOpen, setIsInspectorDrawerOpen] = useState(false);
  const [isComposerResizing, setIsComposerResizing] = useState(false);
  const [runStartedAtMs, setRunStartedAtMs] = useState<number | undefined>(
    undefined
  );
  const [elapsedNow, setElapsedNow] = useState(() => Date.now());
  const [transportIssue, setTransportIssue] = useState<
    { code?: string; message: string } | undefined
  >(undefined);
  const [activeScopeId, setActiveScopeId] = useState(
    initialFormValues.scopeId ?? ""
  );
  const [activeServiceOverrideId, setActiveServiceOverrideId] = useState(
    initialFormValues.serviceOverrideId ?? ""
  );
  const [activeEndpointId, setActiveEndpointId] = useState(
    initialFormValues.endpointId ?? "chat"
  );
  const stopActiveRunRef = useRef<(() => void) | undefined>(undefined);
  const autoStartedDraftRunRef = useRef(false);

  const workflowCatalogQuery = useQuery({
    queryKey: ["workflow-catalog"],
    queryFn: () => runtimeCatalogApi.listWorkflowCatalog(),
  });

  const { session, dispatch, reset } = useRunSession();

  const [streaming, setStreaming] = useState(false);

  const abortRun = useCallback(() => {
    stopActiveRunRef.current?.();
    stopActiveRunRef.current = undefined;
    setStreaming(false);
  }, []);

  const reportTransportError = useCallback(
    (messageText: string, code?: string) => {
      setTransportIssue({ message: messageText, code });
      dispatch({
        type: AGUIEventType.RUN_ERROR,
        message: messageText,
        code,
      });
      messageApi.error(code ? `${code}: ${messageText}` : messageText);
    },
    [dispatch, messageApi]
  );

  const sendRun = useCallback(
    async (
      scopeId: string,
      request: RunFormValues
    ) => {
      const normalizedScopeId = scopeId.trim();
      const normalizedEndpointId = request.endpointId?.trim() || "chat";
      const resolvedServiceId = resolveRequestedServiceId(
        request,
        Boolean(scopeDraftPayload)
      );
      const requestedPayloadTypeUrl = request.payloadTypeUrl?.trim() ?? "";
      const requestedPayloadBase64 = request.payloadBase64?.trim() ?? "";
      if (!normalizedScopeId) {
        throw new Error("Scope ID is required.");
      }
      if (
        requestedPayloadTypeUrl &&
        !requestedPayloadBase64 &&
        !isAutoEncodableTextPayloadTypeUrl(requestedPayloadTypeUrl)
      ) {
        throw new Error(
          `payloadBase64 is required for payloadTypeUrl '${requestedPayloadTypeUrl}'.`
        );
      }

      abortRun();
      reset();
      setTransportIssue(undefined);
      setActiveTransport(request.transport);
      setActiveScopeId(normalizedScopeId);
      setActiveServiceOverrideId(resolvedServiceId);
      setActiveEndpointId(normalizedEndpointId);
      setRunStartedAtMs(Date.now());
      setStreaming(true);

      try {
        const controller = new AbortController();
        stopActiveRunRef.current = () => controller.abort();

        const response = scopeDraftPayload
          ? await runtimeRunsApi.streamDraftRun(
              normalizedScopeId,
              {
                prompt: request.prompt,
                workflowYamls: scopeDraftPayload.bundleYamls,
              },
              controller.signal
            )
          : normalizedEndpointId === "chat" &&
              !request.payloadTypeUrl?.trim() &&
              !request.payloadBase64?.trim()
            ? await runtimeRunsApi.streamChat(
                normalizedScopeId,
                {
                  prompt: request.prompt,
                  metadata: undefined,
                },
                controller.signal,
                {
                  serviceId: resolvedServiceId || undefined,
                }
              )
            : null;

        if (response) {
          for await (const event of parseBackendSSEStream(response, {
            signal: controller.signal,
          })) {
            if (controller.signal.aborted) {
              break;
            }

            dispatch(event);
          }
        } else {
          const receipt = await runtimeRunsApi.invokeEndpoint(
            normalizedScopeId,
            {
              endpointId: normalizedEndpointId,
              prompt: request.prompt,
              commandId: undefined,
              payloadTypeUrl: request.payloadTypeUrl || undefined,
              payloadBase64: request.payloadBase64 || undefined,
            },
            {
              serviceId: resolvedServiceId || undefined,
            }
          );
          const receiptRunId =
            String(receipt.request_id ?? receipt.requestId ?? receipt.commandId ?? "").trim() ||
            `${normalizedEndpointId}-${Date.now().toString(36)}`;
          const receiptActorId =
            String(
              receipt.target_actor_id ?? receipt.targetActorId ?? receipt.actorId ?? ""
            ).trim();
          const receiptCorrelationId =
            String(receipt.correlation_id ?? receipt.correlationId ?? receiptRunId).trim() ||
            receiptRunId;

          dispatch({
            type: AGUIEventType.RUN_STARTED,
            threadId: receiptCorrelationId,
            runId: receiptRunId,
          });
          dispatch({
            type: AGUIEventType.CUSTOM,
            name: CustomEventName.RunContext,
            value: {
              actorId: receiptActorId || undefined,
              workflowName: normalizedEndpointId,
              commandId:
                String(receipt.command_id ?? receipt.commandId ?? "").trim() || undefined,
            },
          });
          messageApi.success(
            `Endpoint ${normalizedEndpointId} accepted with request ${receiptRunId}.`
          );
        }
      } catch (error) {
        if (error instanceof Error && error.name === "AbortError") {
          return;
        }

        const text = error instanceof Error ? error.message : String(error);
        reportTransportError(text);
      } finally {
        stopActiveRunRef.current = undefined;
        setStreaming(false);
      }
    },
    [
      abortRun,
      dispatch,
      messageApi,
      reportTransportError,
      reset,
      scopeDraftPayload,
    ]
  );

  const resolveRunScopeId = useCallback(() => {
    return (
      activeScopeId.trim() ||
      composerFormRef.current?.getFieldValue("scopeId")?.trim?.() ||
      ""
    );
  }, [activeScopeId]);

  const resolveRunServiceOverrideId = useCallback(() => {
    return (
      activeServiceOverrideId.trim() ||
      composerFormRef.current?.getFieldValue("serviceOverrideId")?.trim?.() ||
      ""
    );
  }, [activeServiceOverrideId]);

  const resolveRunEndpointId = useCallback(() => {
    return (
      activeEndpointId.trim() ||
      composerFormRef.current?.getFieldValue("endpointId")?.trim?.() ||
      "chat"
    );
  }, [activeEndpointId]);

  const resizeComposerRail = useCallback((clientX: number) => {
    const containerRect = runsWorkbenchMainRef.current?.getBoundingClientRect();
    if (!containerRect) {
      return;
    }

    setComposerWidth(
      resolveResponsiveComposerWidth(
        clientX - containerRect.left,
        containerRect.width
      )
    );
  }, []);

  const setComposerWidthWithinBounds = useCallback((requestedWidth: number) => {
    const containerRect = runsWorkbenchMainRef.current?.getBoundingClientRect();
    if (!containerRect) {
      setComposerWidth(requestedWidth);
      return;
    }

    setComposerWidth(
      resolveResponsiveComposerWidth(requestedWidth, containerRect.width)
    );
  }, []);

  const { resume, signal, resuming, signaling } = useHumanInteraction({
    resume: (request: WorkflowResumeRequest) => {
      const scopeId = resolveRunScopeId();
      const serviceOverrideId = resolveRunServiceOverrideId();
      if (!scopeId) {
        throw new Error("Scope ID is required to resume a run.");
      }

      return runtimeRunsApi.resume(scopeId, request, {
        serviceId: serviceOverrideId || undefined,
      });
    },
    signal: (request: WorkflowSignalRequest) => {
      const scopeId = resolveRunScopeId();
      const serviceOverrideId = resolveRunServiceOverrideId();
      if (!scopeId) {
        throw new Error("Scope ID is required to signal a run.");
      }

      return runtimeRunsApi.signal(scopeId, request, {
        serviceId: serviceOverrideId || undefined,
      });
    },
  });

  useEffect(() => () => abortRun(), [abortRun]);

  useEffect(() => {
    if (!scopeDraftPayload || !draftRunKey || autoStartedDraftRunRef.current) {
      return;
    }

    const scopeId = initialFormValues.scopeId?.trim() ?? "";
    const prompt = initialFormValues.prompt?.trim() ?? "";
    if (!scopeId || !prompt) {
      return;
    }

    autoStartedDraftRunRef.current = true;
    deleteQueuedDraftRunPayload(draftRunKey);

    if (typeof window !== "undefined") {
      const searchParams = new URLSearchParams(window.location.search);
      searchParams.delete("draftKey");
      const search = searchParams.toString();
      // Avoid router remount during auto-start handoff; we only need to clean the URL.
      window.history.replaceState(
        window.history.state,
        "",
        `${window.location.pathname}${search ? `?${search}` : ""}${window.location.hash}`
      );
    }

    void sendRun(scopeId, {
      ...initialFormValues,
      actorId: undefined,
      endpointId: "chat",
      payloadBase64: undefined,
      payloadTypeUrl: undefined,
      prompt,
      scopeId,
      serviceOverrideId: undefined,
      routeName: scopeDraftPayload.bundleName,
      transport: initialFormValues.transport ?? "sse",
    });
  }, [draftRunKey, initialFormValues, scopeDraftPayload, sendRun]);

  useEffect(() => {
    const syncComposerWidth = () => {
      const containerRect =
        runsWorkbenchMainRef.current?.getBoundingClientRect();
      if (!containerRect) {
        return;
      }

      setComposerWidth((currentWidth) =>
        resolveResponsiveComposerWidth(currentWidth, containerRect.width)
      );
    };

    syncComposerWidth();
    window.addEventListener("resize", syncComposerWidth);
    return () => {
      window.removeEventListener("resize", syncComposerWidth);
    };
  }, []);

  useEffect(() => {
    if (!isComposerResizing) {
      return undefined;
    }

    const handlePointerMove = (event: PointerEvent) => {
      resizeComposerRail(event.clientX);
    };
    const handlePointerUp = () => {
      setIsComposerResizing(false);
    };

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp);
    document.body.style.cursor = "col-resize";
    document.body.style.userSelect = "none";

    return () => {
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);
      document.body.style.cursor = "";
      document.body.style.userSelect = "";
    };
  }, [isComposerResizing, resizeComposerRail]);

  const endpointName =
    activeEndpointId ||
    composerFormRef.current?.getFieldValue("endpointId")?.trim?.() ||
    "chat";
  const routeName = useMemo(() => {
    const sessionWorkflowName = trimOptional(session.context?.workflowName);
    if (sessionWorkflowName) {
      return sessionWorkflowName;
    }

    if (scopeDraftPayload?.bundleName) {
      return scopeDraftPayload.bundleName;
    }

    if (endpointName !== "chat") {
      return endpointName;
    }

    return "";
  }, [endpointName, scopeDraftPayload, session.context?.workflowName]);
  const actorId = session.context?.actorId;
  const commandId = session.context?.commandId ?? "";
  const payloadTypeUrl =
    composerFormRef.current?.getFieldValue("payloadTypeUrl")?.trim?.() ||
    initialFormValues.payloadTypeUrl ||
    "";

  const waitingSignal = useMemo(
    () =>
      getLatestCustomEventData(
        session.events,
        CustomEventName.WaitingSignal,
        parseWaitingSignalData
      ),
    [session.events]
  );
  const latestStepRequest = useMemo(
    () =>
      getLatestCustomEventData(
        session.events,
        CustomEventName.StepRequest,
        parseStepRequestData
      ),
    [session.events]
  );

  const actorSnapshotQuery = useQuery({
    queryKey: ["run-actor-snapshot", actorId],
    enabled: Boolean(actorId),
    queryFn: () => runtimeActorsApi.getActorSnapshot(actorId || ""),
    refetchInterval:
      actorId && (streaming || session.status === "running") ? 2_000 : false,
  });

  const filteredCatalog = useMemo(() => {
    const keyword = catalogSearch.trim().toLowerCase();
    const items = listVisibleWorkflowCatalogItems(
      workflowCatalogQuery.data ?? []
    );
    if (!keyword) {
      return items;
    }

    return items.filter((item) =>
      [item.name, item.description, item.groupLabel, item.category]
        .join(" ")
        .toLowerCase()
        .includes(keyword)
    );
  }, [catalogSearch, workflowCatalogQuery.data]);

  const routeOptions = useMemo(() => {
    const visibleNames = new Set(filteredCatalog.map((item) => item.name));
    return buildWorkflowCatalogOptions(
      workflowCatalogQuery.data ?? [],
      selectedRouteName
    ).filter(
      (option) =>
        option.value === selectedRouteName || visibleNames.has(option.value)
    );
  }, [filteredCatalog, selectedRouteName, workflowCatalogQuery.data]);

  const selectedRouteDetails = useMemo(
    () =>
      findWorkflowCatalogItem(
        workflowCatalogQuery.data ?? [],
        selectedRouteName
      ),
    [selectedRouteName, workflowCatalogQuery.data]
  );

  const selectedRouteRecord = useMemo<
    SelectedRouteRecord | undefined
  >(() => {
    if (!selectedRouteDetails) {
      if (scopeDraftPayload) {
        return {
          routeName: scopeDraftPayload.bundleName,
          groupLabel: "Studio",
          sourceLabel: "Draft bundle",
          llmStatus: "success",
          description:
            "Executing the current Studio draft bundle through the scope draft-run endpoint.",
        };
      }

      if (!endpointName || endpointName === "chat") {
        return undefined;
      }

      return {
        routeName: endpointName,
        groupLabel: endpointInvocationDraftPayload ? "Scope" : "Scope binding",
        sourceLabel: endpointInvocationDraftPayload
          ? "Invocation draft"
          : payloadTypeUrl
          ? "Typed payload"
          : "StringValue default",
        llmStatus: "success",
        description: endpointInvocationDraftPayload
          ? "Invoking the scoped endpoint with a prepared protobuf payload."
          : `Invoking the scoped endpoint '${endpointName}' through the generic invoke path.`,
      };
    }

    return {
      routeName: selectedRouteDetails.name,
      groupLabel: selectedRouteDetails.groupLabel,
      sourceLabel: selectedRouteDetails.sourceLabel,
      llmStatus: selectedRouteDetails.requiresLlmProvider
        ? "processing"
        : "success",
      description: selectedRouteDetails.description,
    };
  }, [
    endpointName,
    endpointInvocationDraftPayload,
    payloadTypeUrl,
    scopeDraftPayload,
    selectedRouteDetails,
  ]);

  const visiblePresets = useMemo(() => {
    const available = new Set(
      listVisibleWorkflowCatalogItems(workflowCatalogQuery.data ?? []).map(
        (item) => item.name
      )
    );
    return builtInPresets.filter((preset) => available.has(preset.routeName));
  }, [workflowCatalogQuery.data]);

  const latestMessagePreview = useMemo(() => {
    const lastWithContent = [...session.messages]
      .reverse()
      .find((item) => item.content?.trim());
    return lastWithContent?.content?.trim() ?? "";
  }, [session.messages]);

  const recentRunRows = useMemo<RecentRunTableRow[]>(
    () =>
      recentRuns.map((entry) => ({
        ...entry,
        key: entry.id,
        statusValue: ["idle", "running", "finished", "error"].includes(
          entry.status
        )
          ? (entry.status as RunStatusValue)
          : "unknown",
        onRestore: () => {
          const isChatEndpoint =
            !entry.endpointId || entry.endpointId === "chat";
          const restoredServiceOverrideId =
            isChatEndpoint &&
            entry.serviceOverrideId === entry.routeName
              ? undefined
              : entry.serviceOverrideId || undefined;
          composerFormRef.current?.setFieldsValue({
            prompt: entry.prompt,
            routeName: isChatEndpoint ? entry.routeName : undefined,
            scopeId: entry.scopeId || undefined,
            serviceOverrideId: restoredServiceOverrideId,
            endpointId: entry.endpointId || "chat",
            payloadTypeUrl: entry.payloadTypeUrl || undefined,
            payloadBase64: entry.payloadBase64 || undefined,
            actorId: entry.actorId || undefined,
            transport: selectedTransport,
          });
          setSelectedRouteName(isChatEndpoint ? entry.routeName : "");
          setActiveEndpointId(entry.endpointId || "chat");
        },
        onOpenActor: entry.actorId
          ? () =>
              history.push(
                buildRuntimeExplorerHref({
                  actorId: entry.actorId,
                })
              )
          : undefined,
      })),
    [recentRuns, selectedTransport]
  );

  const eventRows = useMemo<RunEventRow[]>(
    () => buildEventRows(session.events),
    [session.events]
  );
  const timelineGroups = useMemo<RunTimelineGroup[]>(
    () => buildTimelineGroups(eventRows),
    [eventRows]
  );
  const selectedTraceItem = useMemo(
    () => eventRows.find((item) => item.key === selectedTraceItemKey),
    [eventRows, selectedTraceItemKey]
  );
  const waitingSignalRecord = useMemo<WaitingSignalRecord | undefined>(() => {
    if (!waitingSignal) {
      return undefined;
    }

    return {
      signalName: waitingSignal.signalName ?? "",
      stepId: waitingSignal.stepId ?? "",
      runId: waitingSignal.runId ?? "",
      prompt: waitingSignal.prompt ?? "",
    };
  }, [waitingSignal]);

  useEffect(() => {
    if (eventRows.length === 0) {
      if (selectedTraceItemKey) {
        setSelectedTraceItemKey("");
      }
      return;
    }

    if (!eventRows.some((item) => item.key === selectedTraceItemKey)) {
      setSelectedTraceItemKey(eventRows[0].key);
    }
  }, [eventRows, selectedTraceItemKey]);

  const humanInputRecord = useMemo<HumanInputRecord | undefined>(() => {
    if (!session.pendingHumanInput) {
      return undefined;
    }

    return {
      stepId:
        session.pendingHumanInput.stepId ?? latestStepRequest?.stepId ?? "",
      runId: session.pendingHumanInput.runId ?? session.runId ?? "",
      suspensionType:
        session.pendingHumanInput.suspensionType ??
        latestStepRequest?.stepType ??
        "",
      prompt: session.pendingHumanInput.prompt ?? "",
      timeoutSeconds: session.pendingHumanInput.timeoutSeconds ?? 0,
    };
  }, [
    latestStepRequest?.stepId,
    latestStepRequest?.stepType,
    session.pendingHumanInput,
    session.runId,
  ]);

  const runFocus = useMemo<RunFocusRecord>(() => {
    if (transportIssue || session.error || session.status === "error") {
      return {
        status: "error" as const,
        label:
          transportIssue?.message || session.error?.message || "Run failed",
        alertType: "error" as const,
        title: transportIssue?.code ?? session.error?.code ?? "Run error",
        description:
          transportIssue?.message ||
          session.error?.message ||
          "The run ended with an error.",
      };
    }

    if (humanInputRecord) {
      const approval = isHumanApprovalSuspension(
        humanInputRecord.suspensionType
      );
      return {
        status: approval
          ? ("human_approval" as const)
          : ("human_input" as const),
        label: approval
          ? `Awaiting approval on ${humanInputRecord.stepId || "current step"}`
          : `Awaiting human input on ${
              humanInputRecord.stepId || "current step"
            }`,
        alertType: "warning" as const,
        title: approval ? "Approval required" : "Human input required",
        description:
          humanInputRecord.prompt || "Operator action is required to continue.",
      };
    }

    if (waitingSignalRecord) {
      return {
        status: "wait_signal" as const,
        label: `Waiting for signal ${
          waitingSignalRecord.signalName || "unknown"
        }`,
        alertType: "warning" as const,
        title: "Waiting for external signal",
        description:
          waitingSignalRecord.prompt ||
          "The run is paused until the expected signal arrives.",
      };
    }

    if (streaming) {
      return {
        status: "running" as const,
        label: `Streaming over ${activeTransport.toUpperCase()}`,
        alertType: "info" as const,
        title: "Run in progress",
        description: "Messages and events are still arriving from the backend.",
      };
    }

    if (session.status === "running") {
      return {
        status: "running" as const,
        label: "Invocation accepted",
        alertType: "info" as const,
        title: "Awaiting observation",
        description:
          "The backend accepted the command. This console will stay pending until observed events arrive.",
      };
    }

    if (session.status === "finished") {
      return {
        status: "finished" as const,
        label: "Run completed",
        alertType: "success" as const,
        title: "Run finished",
        description: "The backend reported a completed run.",
      };
    }

    return {
      status: "idle" as const,
      label: "Ready to start a run",
      alertType: "info" as const,
      title: "Idle",
      description:
        "Compose a prompt or payload and start a scoped endpoint run.",
    };
  }, [
    activeTransport,
    humanInputRecord,
    session.error,
    session.status,
    streaming,
    transportIssue,
    waitingSignalRecord,
  ]);

  const hasPendingInteraction = Boolean(
    humanInputRecord || waitingSignalRecord
  );
  const runStatusText =
    runStatusValueEnum[session.status]?.text ?? session.status;
  const isRunLive =
    streaming ||
    session.status === "running" ||
    hasPendingInteraction ||
    runFocus.status === "wait_signal";
  const runStatusTone = isRunLive
    ? ("processing" as const)
    : session.status === "finished"
    ? ("success" as const)
    : session.status === "error"
    ? ("error" as const)
    : ("default" as const);

  useEffect(() => {
    if (hasPendingInteraction) {
      setIsInspectorDrawerOpen(true);
    }
  }, [hasPendingInteraction]);

  useEffect(() => {
    if (!runStartedAtMs) {
      return undefined;
    }

    if (!isRunLive) {
      setElapsedNow(Date.now());
      return undefined;
    }

    const timer = window.setInterval(() => {
      setElapsedNow(Date.now());
    }, 1_000);

    return () => {
      window.clearInterval(timer);
    };
  }, [isRunLive, runStartedAtMs]);

  const elapsedLabel = runStartedAtMs
    ? formatElapsedDuration(elapsedNow - runStartedAtMs)
    : "00:00";

  const lastEventAt = useMemo(() => {
    const latest = session.events[session.events.length - 1];
    return formatDateTime(latest?.timestamp, "");
  }, [session.events]);

  const runSummaryRecord = useMemo<RunSummaryRecord>(
    () => ({
      status: session.status,
      transport: activeTransport,
      routeName,
      endpointId: endpointName,
      actorId: actorId ?? "",
      commandId,
      runId: session.runId ?? "",
      focusStatus: runFocus.status,
      focusLabel: runFocus.label,
      lastEventAt,
      messageCount: session.messages.length,
      eventCount: session.events.length,
      activeSteps: [...session.activeSteps],
    }),
    [
      activeTransport,
      actorId,
      commandId,
      lastEventAt,
      runFocus.label,
      runFocus.status,
      session.activeSteps,
      session.events.length,
      session.messages.length,
      session.runId,
      session.status,
      endpointName,
      routeName,
    ]
  );

  useEffect(() => {
    const prompt = composerFormRef.current?.getFieldValue("prompt") ?? "";
    const currentPayloadTypeUrl =
      composerFormRef.current?.getFieldValue("payloadTypeUrl") ?? "";
    const currentPayloadBase64 =
      composerFormRef.current?.getFieldValue("payloadBase64") ?? "";
    const currentEndpointId = resolveRunEndpointId();
    const currentServiceOverrideId = resolveRunServiceOverrideId();
    const candidateId =
      commandId ??
      session.runId ??
      (actorId && routeName ? `${routeName}:${actorId}` : "");

    if (!candidateId || (!routeName && !prompt)) {
      return;
    }

    setRecentRuns(
      saveRecentRun({
        id: candidateId,
        scopeId: resolveRunScopeId(),
        serviceOverrideId:
          currentEndpointId === "chat" &&
          currentServiceOverrideId === routeName
            ? ""
            : currentServiceOverrideId,
        endpointId: currentEndpointId,
        payloadTypeUrl: currentPayloadTypeUrl,
        payloadBase64: currentPayloadBase64,
        routeName,
        prompt,
        actorId: actorId ?? "",
        commandId,
        runId: session.runId ?? "",
        status: session.status,
        lastMessagePreview: latestMessagePreview,
      })
    );
  }, [
    actorId,
    commandId,
    latestMessagePreview,
    payloadTypeUrl,
    resolveRunScopeId,
    resolveRunServiceOverrideId,
    resolveRunEndpointId,
    session.runId,
    session.status,
    routeName,
  ]);

  const handleAbortRun = useCallback(async () => {
    const scopeId = resolveRunScopeId();
    const serviceOverrideId = resolveRunServiceOverrideId();
    const runId = session.runId?.trim() ?? "";
    const currentActorId = actorId?.trim() ?? "";

    if (scopeId && runId) {
      try {
        await runtimeRunsApi.stop(scopeId, {
          actorId: currentActorId || undefined,
          runId,
          commandId: commandId || undefined,
          reason: "aborted from runtime console",
        }, {
          serviceId: serviceOverrideId || undefined,
        });
      } catch (error) {
        const text = error instanceof Error ? error.message : String(error);
        messageApi.error(`Failed to stop remote run: ${text}`);
      }
    }

    abortRun();
  }, [
    abortRun,
    actorId,
    commandId,
    messageApi,
    resolveRunScopeId,
    resolveRunServiceOverrideId,
    session.runId,
  ]);

  const submitPathLabel = scopeDraftPayload
    ? "/api/scopes/{scopeId}/draft-run"
    : endpointName === "chat"
      ? resolveRequestedServiceId(
          {
            endpointId: endpointName,
            routeName: selectedRouteName,
            serviceOverrideId:
              activeServiceOverrideId || initialFormValues.serviceOverrideId,
          },
          false
        )
        ? "/api/scopes/{scopeId}/services/{serviceId}/invoke/chat:stream"
        : "/api/scopes/{scopeId}/invoke/chat:stream"
      : trimOptional(activeServiceOverrideId || initialFormValues.serviceOverrideId)
        ? "/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}"
        : "/api/scopes/{scopeId}/invoke/{endpointId}";

  const messageConsoleView = (
    <div style={workbenchConsoleSurfaceStyle}>
      <div
        style={{
          borderBottom: "1px solid var(--ant-color-border-secondary)",
          color: "var(--ant-color-text-secondary)",
          padding: "10px 12px",
        }}
      >
        Message stream
      </div>
      <div style={workbenchConsoleScrollStyle}>
        {session.messages.length > 0 ? (
          <div style={workbenchMessageListStyle}>
            {session.messages.map((record) => (
              <div
                key={record.messageId}
                style={{
                  alignSelf: record.role === "user" ? "flex-end" : "flex-start",
                  background:
                    record.role === "user"
                      ? "rgba(22, 119, 255, 0.10)"
                      : "rgba(15, 23, 42, 0.04)",
                  border:
                    record.complete === false
                      ? "1px solid rgba(22, 119, 255, 0.28)"
                      : "1px solid var(--ant-color-border-secondary)",
                  borderRadius: 12,
                  maxWidth: "88%",
                  padding: 12,
                }}
              >
                <Space separator={<Divider orientation="vertical" />} size={8}>
                  <Typography.Text
                    style={{
                      color: "var(--ant-color-text)",
                      fontFamily: "inherit",
                    }}
                  >
                    {record.role}
                  </Typography.Text>
                  <Typography.Text
                    style={{
                      color: "var(--ant-color-text-secondary)",
                      fontFamily: "inherit",
                    }}
                  >
                    {record.messageId}
                  </Typography.Text>
                  <Typography.Text
                    style={{
                      color: "var(--ant-color-text-secondary)",
                      fontFamily: "inherit",
                    }}
                  >
                    {record.complete ? "complete" : "streaming"}
                  </Typography.Text>
                </Space>
                <Typography.Paragraph
                  style={{
                    color: "var(--ant-color-text)",
                    fontFamily: "inherit",
                    margin: "8px 0 0",
                    whiteSpace: "pre-wrap",
                  }}
                >
                  {record.content || "(streaming...)"}
                </Typography.Paragraph>
              </div>
            ))}
          </div>
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="No message output yet."
          />
        )}
      </div>
    </div>
  );

  const eventConsoleView = (
    <div style={workbenchConsoleSurfaceStyle}>
      <div style={workbenchEventHeaderStyle}>
        <span>Timestamp</span>
        <span>Category</span>
        <span>Description</span>
      </div>
      <div style={workbenchConsoleScrollStyle}>
        {eventRows.length > 0 ? (
          eventRows.map((record) => (
            <div key={record.key} style={workbenchEventRowStyle}>
              <Typography.Text
                style={{
                  color: "var(--ant-color-text-secondary)",
                  fontFamily: "inherit",
                }}
              >
                {record.timestamp || "n/a"}
              </Typography.Text>
              <Space direction="vertical" size={4}>
                <Typography.Text
                  style={{
                    color: "var(--ant-color-text)",
                    fontFamily: "inherit",
                  }}
                >
                  {record.eventCategory}
                </Typography.Text>
                <Typography.Text
                  style={{
                    color: "var(--ant-color-text-secondary)",
                    fontFamily: "inherit",
                  }}
                >
                  {record.eventStatus}
                </Typography.Text>
              </Space>
              <div>
                <Typography.Text
                  style={{
                    color: "var(--ant-color-text)",
                    fontFamily: "inherit",
                  }}
                >
                  {record.eventType}
                </Typography.Text>
                <Typography.Paragraph
                  ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
                  style={{
                    color: "var(--ant-color-text-secondary)",
                    fontFamily: "inherit",
                    margin: "6px 0 0",
                    whiteSpace: "pre-wrap",
                  }}
                >
                  {record.description}
                  {record.payloadPreview ? `\n${record.payloadPreview}` : ""}
                </Typography.Paragraph>
              </div>
            </div>
          ))
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="No events observed yet."
          />
        )}
      </div>
    </div>
  );

  return (
    <PageContainer pageHeaderRender={false} style={{ overflow: "hidden" }}>
      {messageContextHolder}
      <div style={runsWorkbenchShellStyle}>
        <div style={runsWorkbenchHeaderBarStyle}>
          <div style={runsWorkbenchHeaderTitleStyle}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              Runtime endpoint console
            </Typography.Title>
            <Popover
              content={
                <Typography.Paragraph
                  style={{ margin: 0, maxWidth: 360 }}
                  type="secondary"
                >
                  Drive scoped endpoints over{" "}
                  <Typography.Text code>
                    {submitPathLabel}
                  </Typography.Text>
                  , monitor the live event stream when the endpoint is streamed,
                  and jump into adjacent runtime surfaces directly from the
                  runtime console.
                </Typography.Paragraph>
              }
              placement="bottomLeft"
              trigger={["hover", "click"]}
            >
              <Button
                aria-label="Open runtime console guide"
                icon={<InfoCircleOutlined />}
                shape="circle"
                type="text"
              />
            </Popover>
          </div>
          <div style={runsWorkbenchHeaderActionStyle}>
            <Button onClick={() => history.push(buildRuntimeWorkflowsHref())}>
              Open Runtime Catalog
            </Button>
            <Button
              onClick={() =>
                history.push(
                  buildRuntimeExplorerHref({
                    actorId: actorId ?? undefined,
                  })
                )
              }
            >
              Open Runtime Explorer
            </Button>
          </div>
        </div>
        <RunsStatusStrip
          activeStepCount={session.activeSteps.size}
          elapsedLabel={elapsedLabel}
          eventCount={session.events.length}
          hasPendingInteraction={hasPendingInteraction}
          isRunLive={isRunLive}
          messageCount={session.messages.length}
          onAbort={handleAbortRun}
          onOpenInspector={() => setIsInspectorDrawerOpen(true)}
          runId={session.runId || commandId || "Not started"}
          runStatusLabel={runStatusText}
          statusTone={runStatusTone}
          transport={activeTransport}
          endpointId={endpointName}
        />

        <div ref={runsWorkbenchMainRef} style={runsWorkbenchMainStyle}>
          <div
            style={{
              ...runsWorkbenchComposerRailStyle,
              flex: `0 0 ${composerWidth}px`,
              maxWidth: composerWidth,
              minWidth: composerWidth,
              width: composerWidth,
            }}
          >
            <RunsLaunchRail
              actorId={actorId ?? undefined}
              activeEndpointId={endpointName}
              catalogSearch={catalogSearch}
              composerFormRef={composerFormRef}
              draftMode={Boolean(draftRunPayload)}
              initialFormValues={initialFormValues}
              recentRunRows={recentRunRows}
              selectedTransport={selectedTransport}
              selectedRouteDetailsPrimitives={
                selectedRouteDetails?.primitives ?? []
              }
              selectedRouteRecord={selectedRouteRecord}
              streaming={streaming}
              submitPathLabel={submitPathLabel}
              transportOptions={[
                { label: "Service SSE stream", value: "sse" },
              ]}
              visiblePresets={visiblePresets}
              workflowCatalogLoading={workflowCatalogQuery.isLoading}
              routeOptions={routeOptions}
              onAbortRun={abortRun}
              onCatalogSearchChange={setCatalogSearch}
              onClearRecentRuns={() => setRecentRuns(clearRecentRuns())}
              onEndpointChange={setActiveEndpointId}
              onSelectRouteName={setSelectedRouteName}
              onSubmitRun={async (values) => {
                await sendRun(values.scopeId ?? "", values);
              }}
              onTransportChange={setSelectedTransport}
              onUsePreset={(record) => {
                composerFormRef.current?.setFieldsValue({
                  prompt: record.prompt,
                  routeName: scopeDraftPayload?.bundleName ?? record.routeName,
                  scopeId:
                    composerFormRef.current?.getFieldValue("scopeId") ??
                    initialFormValues.scopeId,
                  serviceOverrideId: undefined,
                  endpointId: "chat",
                  payloadTypeUrl: undefined,
                  payloadBase64: undefined,
                  actorId: undefined,
                  transport: selectedTransport,
                });
                setSelectedRouteName(
                  scopeDraftPayload?.bundleName ?? record.routeName
                );
                setActiveEndpointId("chat");
                setCatalogSearch("");
              }}
            />
          </div>
          <button
            aria-label="Resize composer panel"
            type="button"
            style={runsWorkbenchResizeRailStyle}
            onDoubleClick={() =>
              setComposerWidthWithinBounds(composerRailDefaultWidth)
            }
            onKeyDown={(event) => {
              if (event.key === "ArrowLeft") {
                event.preventDefault();
                setComposerWidthWithinBounds(
                  composerWidth - composerRailKeyboardStep
                );
              }

              if (event.key === "ArrowRight") {
                event.preventDefault();
                setComposerWidthWithinBounds(
                  composerWidth + composerRailKeyboardStep
                );
              }
            }}
            onPointerDown={(event) => {
              event.preventDefault();
              setIsComposerResizing(true);
              resizeComposerRail(event.clientX);
            }}
          >
            <div
              style={{
                ...runsWorkbenchResizeHandleStyle,
                background: isComposerResizing
                  ? "var(--ant-color-primary)"
                  : "var(--ant-color-border-secondary)",
                transform: isComposerResizing ? "scaleX(1.15)" : "scaleX(1)",
              }}
            />
          </button>
          <div style={runsWorkbenchMonitorStyle}>
            <div style={workbenchOverviewGridStyle}>
              <Tabs
                activeKey="trace-layout"
                items={[
                  {
                    key: "trace-layout",
                    label: "Trace workspace",
                    children: (
                      <div style={{ display: "flex", flex: 1, minHeight: 0 }}>
                        <RunsTracePane
                          consoleView={consoleView}
                          eventConsoleView={eventConsoleView}
                          eventCount={eventRows.length}
                          hasPendingInteraction={hasPendingInteraction}
                          messageConsoleView={messageConsoleView}
                          messageCount={session.messages.length}
                          onConsoleViewChange={setConsoleView}
                          timelineView={
                            <RunsTimelineView
                              groups={timelineGroups}
                              onSelectItem={(item) => {
                                setSelectedTraceItemKey(item.key);
                                setIsInspectorDrawerOpen(true);
                              }}
                              selectedItemKey={selectedTraceItemKey}
                            />
                          }
                        />
                      </div>
                    ),
                  },
                ]}
              />
            </div>
          </div>
        </div>

        <Drawer
          destroyOnHidden
          mask={false}
          open={isInspectorDrawerOpen}
          styles={{ body: drawerBodyStyle }}
          title={hasPendingInteraction ? "Inspector · interaction pending" : "Inspector"}
          size={520}
          onClose={() => setIsInspectorDrawerOpen(false)}
        >
          <div style={drawerScrollStyle}>
            <div style={cardStackStyle}>
            <RunsInspectorPane
              actorSnapshot={actorSnapshotQuery.data}
              actorSnapshotLoading={
                actorSnapshotQuery.isLoading || actorSnapshotQuery.isFetching
              }
              humanInputRecord={humanInputRecord}
              latestMessagePreview={latestMessagePreview}
              runFocus={runFocus}
              runSummaryRecord={runSummaryRecord}
              selectedTraceItem={selectedTraceItem}
              selectedRoutePrimitives={
                selectedRouteDetails?.primitives ?? []
              }
              selectedRouteRecord={selectedRouteRecord}
              showInteractionAction={false}
              variant="plain"
              waitingSignalRecord={waitingSignalRecord}
            />
            {humanInputRecord ? (
              <div style={embeddedPanelStyle}>
                <Space direction="vertical" style={{ width: "100%" }} size={16}>
                  <ProDescriptions<HumanInputRecord>
                    column={1}
                    dataSource={humanInputRecord}
                    columns={humanInputColumns}
                  />
                  <ProForm<ResumeFormValues>
                    key={`${humanInputRecord.runId}-${humanInputRecord.stepId}`}
                    formRef={resumeFormRef}
                    layout="vertical"
                    initialValues={{ approved: true, userInput: "" }}
                    onFinish={async (values) => {
                      if (
                        !actorId ||
                        !humanInputRecord.runId ||
                        !humanInputRecord.stepId
                      ) {
                        return false;
                      }

                      await resume({
                        actorId,
                        runId: humanInputRecord.runId,
                        stepId: humanInputRecord.stepId,
                        approved: values.approved,
                        userInput: values.userInput || undefined,
                        commandId,
                      });

                      messageApi.success("Resume request accepted.");
                      resumeFormRef.current?.setFieldsValue({
                        approved: true,
                        userInput: "",
                      });
                      return true;
                    }}
                    submitter={{
                      render: (props) => (
                        <Space wrap>
                          <Button
                            type="primary"
                            loading={resuming}
                            onClick={() => props.form?.submit?.()}
                          >
                            Submit resume
                          </Button>
                        </Space>
                      ),
                    }}
                  >
                    <ProFormSwitch
                      name="approved"
                      label={
                        isHumanApprovalSuspension(
                          humanInputRecord.suspensionType
                        )
                          ? "Approved"
                          : "Continue run"
                      }
                    />
                    <ProFormTextArea
                      name="userInput"
                      label="Operator response"
                      fieldProps={{ rows: 4 }}
                      placeholder="Optional human response"
                    />
                  </ProForm>
                </Space>
              </div>
            ) : null}

            {waitingSignalRecord ? (
              <div style={embeddedPanelStyle}>
                <Space direction="vertical" style={{ width: "100%" }} size={16}>
                  <ProDescriptions<WaitingSignalRecord>
                    column={1}
                    dataSource={waitingSignalRecord}
                    columns={waitingSignalColumns}
                  />
                  <ProForm<SignalFormValues>
                    key={`${waitingSignalRecord.runId}-${waitingSignalRecord.stepId}`}
                    formRef={signalFormRef}
                    layout="vertical"
                    initialValues={{ payload: "" }}
                    onFinish={async (values) => {
                      if (
                        !actorId ||
                        !waitingSignal?.runId ||
                        !waitingSignal.signalName
                      ) {
                        return false;
                      }

                      await signal({
                        actorId,
                        runId: waitingSignal.runId,
                        stepId: waitingSignal.stepId,
                        signalName: waitingSignal.signalName,
                        payload: values.payload || undefined,
                        commandId,
                      });

                      messageApi.success("Signal accepted.");
                      signalFormRef.current?.setFieldsValue({ payload: "" });
                      return true;
                    }}
                    submitter={{
                      render: (props) => (
                        <Space wrap>
                          <Button
                            type="primary"
                            loading={signaling}
                            onClick={() => props.form?.submit?.()}
                          >
                            Send signal
                          </Button>
                        </Space>
                      ),
                    }}
                  >
                    <ProFormTextArea
                      name="payload"
                      label="Signal payload"
                      fieldProps={{ rows: 4 }}
                      placeholder="Optional signal payload"
                    />
                  </ProForm>
                </Space>
              </div>
            ) : null}
            </div>
          </div>
        </Drawer>
      </div>
    </PageContainer>
  );
};

export default RunsPage;
