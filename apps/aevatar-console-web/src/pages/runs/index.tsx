import {
  useHumanInteraction,
  useRunSession,
} from "@aevatar-react-sdk/agui";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
  type WorkflowResumeRequest,
  type WorkflowSignalRequest,
} from "@aevatar-react-sdk/types";
import {
  AppstoreOutlined,
  ArrowLeftOutlined,
  DeploymentUnitOutlined,
  InfoCircleOutlined,
  SendOutlined,
} from "@ant-design/icons";
import type { ProFormInstance } from "@ant-design/pro-components";
import {
  PageContainer,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import { sanitizeReturnTo } from "@/shared/auth/session";
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
import {
  buildRuntimeExplorerHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  Button,
  Drawer,
  Input,
  message,
  Popover,
  Space,
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
import {
  type RunEndpointKind,
  normalizeRunEndpointKind,
  resolveRunEndpointId as resolveStoredRunEndpointId,
} from "@/shared/runs/endpointKinds";
import { isAutoEncodableTextPayloadTypeUrl } from "@/shared/runs/protobufPayload";
import {
  deleteDraftRunPayload as deleteQueuedDraftRunPayload,
  isEndpointInvocationDraftPayload,
  isObservedRunSessionPayload,
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
} from "@/shared/ui/proComponents";
import RunsInspectorPane from "./components/RunsInspectorPane";
import RunsActionRequiredPanel from "./components/RunsActionRequiredPanel";
import RunsEventsView from "./components/RunsEventsView";
import RunsLaunchRail from "./components/RunsLaunchRail";
import RunsMessagesView from "./components/RunsMessagesView";
import RunsStatusStrip from "./components/RunsStatusStrip";
import RunsTracePane from "./components/RunsTracePane";
import RunsTimelineView from "./components/RunsTimelineView";
import {
  buildTimelineGroups,
  buildEventRows,
  resolveRunMessageFallback,
  isHumanApprovalSuspension,
  type RunEventRow,
  type RunTimelineGroup,
  type RunTransport,
} from "./runEventPresentation";
import {
  builtInPresets,
  type ConsoleViewKey,
  defaultRunRouteName,
  formatElapsedDuration,
  type HumanInputRecord,
  readInitialRunFormValues,
  type RecentRunTableRow,
  type ResumeFormValues,
  type RunFocusRecord,
  type RunFormValues,
  type RunStatusValue,
  runStatusValueEnum,
  runsWorkbenchMonitorStyle,
  runsWorkbenchShellStyle,
  type RunSummaryRecord,
  type SelectedRouteRecord,
  type SignalFormValues,
  trimOptional,
  type WaitingSignalRecord,
  workbenchOverviewGridStyle,
} from "./runWorkbenchConfig";

const runsWorkbenchHeaderBarStyle: React.CSSProperties = {
  alignItems: "center",
  background: "var(--ant-color-bg-container)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 10,
  display: "flex",
  flexWrap: "wrap",
  gap: 12,
  justifyContent: "space-between",
  padding: "10px 12px",
};

const runsWorkbenchHeaderTitleStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  gap: 8,
  minWidth: 0,
};

const runsWorkbenchHeaderActionStyle: React.CSSProperties = {
  alignItems: "center",
  backdropFilter: "blur(10px)",
  background:
    "linear-gradient(180deg, rgba(248, 250, 252, 0.9) 0%, rgba(255, 255, 255, 0.78) 100%)",
  border: "1px solid rgba(226, 232, 240, 0.95)",
  borderRadius: 16,
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "flex-end",
  padding: 4,
};

const runsWorkbenchHeaderToolbarClassName = "runs-workbench-header-toolbar";
const runsWorkbenchHeaderButtonClassName = "runs-workbench-header-button";
const runsWorkbenchHeaderButtonAccentClassName =
  "runs-workbench-header-button-accent";

const runsWorkbenchHeaderToolbarCss = `
.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName} {
  align-items: center;
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.98) 0%, rgba(248, 250, 252, 0.96) 100%);
  border: 1px solid rgba(148, 163, 184, 0.24);
  border-radius: 12px;
  box-shadow:
    0 1px 2px rgba(15, 23, 42, 0.05),
    inset 0 1px 0 rgba(255, 255, 255, 0.95);
  color: var(--ant-color-text);
  display: inline-flex;
  font-size: 14px;
  font-weight: 600;
  gap: 8px;
  height: 38px;
  padding-inline: 14px;
  transition:
    border-color 0.2s ease,
    box-shadow 0.2s ease,
    color 0.2s ease,
    transform 0.2s ease;
}

.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName}:hover,
.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName}:focus {
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 1) 0%, rgba(239, 246, 255, 0.92) 100%);
  border-color: rgba(59, 130, 246, 0.3);
  box-shadow:
    0 10px 22px rgba(59, 130, 246, 0.12),
    inset 0 1px 0 rgba(255, 255, 255, 0.96);
  color: var(--ant-color-primary);
  transform: translateY(-1px);
}

.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName} .anticon {
  font-size: 14px;
}

.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonAccentClassName} {
  background:
    linear-gradient(180deg, rgba(239, 246, 255, 0.98) 0%, rgba(219, 234, 254, 0.88) 100%);
  border-color: rgba(59, 130, 246, 0.28);
  box-shadow:
    0 10px 22px rgba(59, 130, 246, 0.14),
    inset 0 1px 0 rgba(255, 255, 255, 0.96);
  color: var(--ant-color-primary);
}

.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonAccentClassName}:hover,
.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonAccentClassName}:focus {
  background:
    linear-gradient(180deg, rgba(219, 234, 254, 1) 0%, rgba(191, 219, 254, 0.96) 100%);
  border-color: rgba(37, 99, 235, 0.38);
  box-shadow:
    0 14px 28px rgba(37, 99, 235, 0.18),
    inset 0 1px 0 rgba(255, 255, 255, 0.98);
  color: #1d4ed8;
}

.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName}[disabled],
.${runsWorkbenchHeaderToolbarClassName} .${runsWorkbenchHeaderButtonClassName}[disabled]:hover {
  background: rgba(248, 250, 252, 0.86);
  border-color: rgba(226, 232, 240, 0.9);
  box-shadow: none;
  color: var(--ant-color-text-tertiary);
  transform: none;
}
`;

const runsSetupStateStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  justifyContent: "center",
  minHeight: 0,
  overflow: "hidden",
};

const runsSetupRailViewportStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  maxWidth: 920,
  minHeight: 0,
  overflow: "hidden",
  width: "100%",
};

const runsRunStateStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  minHeight: 0,
  overflow: "hidden",
};

const runsChatLayoutStyle: React.CSSProperties = {
  display: "grid",
  flex: 1,
  gap: 16,
  gridTemplateColumns: "minmax(272px, 320px) minmax(0, 1fr)",
  minHeight: 0,
  overflow: "hidden",
};

const runsChatSidebarStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  minHeight: 0,
  minWidth: 0,
  overflow: "hidden",
};

const runsChatMainStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 12,
  minHeight: 0,
  minWidth: 0,
  overflow: "hidden",
};

const runsChatTraceWrapStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  minHeight: 0,
  minWidth: 0,
  overflow: "hidden",
};

const runsChatComposerCardStyle: React.CSSProperties = {
  background:
    "linear-gradient(180deg, rgba(255, 255, 255, 0.98) 0%, rgba(248, 250, 252, 0.96) 100%)",
  border: "1px solid rgba(148, 163, 184, 0.18)",
  borderRadius: 20,
  boxShadow: "0 18px 40px rgba(15, 23, 42, 0.08)",
  display: "flex",
  flexDirection: "column",
  gap: 12,
  padding: "14px 16px 16px",
  position: "relative",
};

const runsChatComposerActionsStyle: React.CSSProperties = {
  display: "flex",
  flex: "0 0 auto",
  flexDirection: "column",
  gap: 10,
};

const runsChatComposerInputWrapStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
};

const runsChatComposerHeaderStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 12,
  justifyContent: "space-between",
};

const runsChatComposerLabelStyle: React.CSSProperties = {
  color: "var(--ant-color-primary)",
  fontSize: 12,
  fontWeight: 700,
  letterSpacing: "0.08em",
  lineHeight: 1,
  textTransform: "uppercase",
};

const runsChatComposerHintStyle: React.CSSProperties = {
  color: "var(--ant-color-text-secondary)",
  fontSize: 12,
};

const runsChatComposerSendButtonStyle: React.CSSProperties = {
  borderRadius: 14,
  boxShadow: "0 12px 24px rgba(22, 119, 255, 0.2)",
  fontWeight: 600,
  height: 46,
  paddingInline: 20,
};

const runsChatComposerTextareaStyle: React.CSSProperties = {
  background: "transparent",
  border: "none",
  boxShadow: "none",
  color: "var(--ant-color-text)",
  fontSize: 15,
  lineHeight: 1.65,
  padding: 0,
};

const runsChatComposerClassName = "runs-chat-composer";
const runsChatComposerBodyClassName = "runs-chat-composer-body";
const runsChatComposerInputShellClassName = "runs-chat-composer-input-shell";
const runsChatComposerInputClassName = "runs-chat-composer-input";
const runsChatComposerActionsClassName = "runs-chat-composer-actions";

const runsChatComposerCss = `
.${runsChatComposerClassName} {
  overflow: hidden;
}

.${runsChatComposerClassName}::before {
  background:
    radial-gradient(circle at top right, rgba(22, 119, 255, 0.14), transparent 55%);
  content: "";
  height: 220px;
  pointer-events: none;
  position: absolute;
  right: -36px;
  top: -88px;
  width: 220px;
}

.${runsChatComposerClassName} > * {
  position: relative;
  z-index: 1;
}

.${runsChatComposerBodyClassName} {
  align-items: center;
  display: flex;
  gap: 6px;
  min-width: 0;
}

.${runsChatComposerInputShellClassName} {
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.98) 0%, rgba(248, 250, 252, 0.98) 100%);
  border: 1px solid rgba(148, 163, 184, 0.22);
  border-radius: 16px;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.9);
  min-width: 0;
  padding: 14px 16px 12px;
  transition:
    border-color 0.2s ease,
    box-shadow 0.2s ease,
    transform 0.2s ease;
}

.${runsChatComposerInputShellClassName}:focus-within {
  border-color: rgba(22, 119, 255, 0.36);
  box-shadow:
    0 0 0 4px rgba(22, 119, 255, 0.1),
    inset 0 1px 0 rgba(255, 255, 255, 0.92);
  transform: translateY(-1px);
}

.${runsChatComposerInputClassName},
.${runsChatComposerInputClassName}:hover,
.${runsChatComposerInputClassName}:focus {
  background: transparent !important;
  border: none !important;
  box-shadow: none !important;
}

.${runsChatComposerInputClassName} {
  color: var(--ant-color-text);
  font-size: 15px;
  line-height: 1.65;
  padding: 0 !important;
}

.${runsChatComposerInputClassName}::placeholder {
  color: var(--ant-color-text-tertiary);
}

.${runsChatComposerActionsClassName} {
  align-items: flex-end;
  display: flex;
  flex: 0 0 auto;
  flex-direction: column;
  gap: 10px;
  min-width: 0;
}

@media (max-width: 960px) {
  .${runsChatComposerBodyClassName} {
    align-items: stretch;
    flex-direction: column;
  }

  .${runsChatComposerActionsClassName} {
    align-items: stretch;
    min-width: 0;
    width: 100%;
  }

  .${runsChatComposerActionsClassName} .ant-btn {
    width: 100%;
  }
}
`;

function resolveRequestedServiceId(
  request: Pick<
    RunFormValues,
    "endpointId" | "endpointKind" | "routeName" | "serviceOverrideId"
  >,
  draftMode: boolean
): string {
  if (draftMode) {
    return "";
  }

  const normalizedEndpointKind = normalizeRunEndpointKind(
    request.endpointKind,
    request.endpointId
  );
  const normalizedServiceOverrideId =
    trimOptional(request.serviceOverrideId) || "";
  if (normalizedEndpointKind !== "chat") {
    return normalizedServiceOverrideId;
  }

  return normalizedServiceOverrideId || trimOptional(request.routeName) || "";
}

function extractMissingServiceId(messageText: string): string {
  const match = messageText.match(/Service '([^']+)' was not found/i);
  return match?.[1]?.trim() ?? "";
}

function isMissingScopeServiceError(messageText: string): boolean {
  return extractMissingServiceId(messageText).length > 0;
}

function resolveConsoleViewForEndpoint(
  endpointKind: RunEndpointKind
): ConsoleViewKey {
  return endpointKind === "chat" ? "messages" : "timeline";
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
  const requestedReturnTo = useMemo(() => {
    if (typeof window === "undefined") {
      return "";
    }

    const returnTo = new URLSearchParams(window.location.search).get("returnTo");
    return returnTo ? sanitizeReturnTo(returnTo) : "";
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
  const observedRunDraftPayload = useMemo(
    () =>
      isObservedRunSessionPayload(draftRunPayload)
        ? draftRunPayload
        : undefined,
    [draftRunPayload]
  );
  const initialFormValues = useMemo(
    () => ({
      ...urlInitialFormValues,
      routeName:
        observedRunDraftPayload?.routeName ??
        (endpointInvocationDraftPayload
          ? undefined
          : urlInitialFormValues.routeName),
      prompt:
        observedRunDraftPayload?.prompt ??
        endpointInvocationDraftPayload?.prompt ??
        urlInitialFormValues.prompt,
      scopeId:
        observedRunDraftPayload?.scopeId ?? urlInitialFormValues.scopeId,
      serviceOverrideId:
        observedRunDraftPayload?.serviceOverrideId ??
        endpointInvocationDraftPayload?.serviceOverrideId ??
        urlInitialFormValues.serviceOverrideId,
      endpointKind:
        observedRunDraftPayload?.endpointKind ??
        endpointInvocationDraftPayload?.endpointKind ??
        urlInitialFormValues.endpointKind,
      endpointId:
        observedRunDraftPayload?.endpointId ??
        endpointInvocationDraftPayload?.endpointId ??
        urlInitialFormValues.endpointId,
      payloadTypeUrl:
        observedRunDraftPayload?.payloadTypeUrl ??
        endpointInvocationDraftPayload?.payloadTypeUrl ??
        urlInitialFormValues.payloadTypeUrl,
      payloadBase64:
        observedRunDraftPayload?.payloadBase64 ??
        endpointInvocationDraftPayload?.payloadBase64 ??
        urlInitialFormValues.payloadBase64,
      actorId:
        observedRunDraftPayload?.actorId ?? urlInitialFormValues.actorId,
    }),
    [
      endpointInvocationDraftPayload,
      observedRunDraftPayload,
      urlInitialFormValues,
    ]
  );
  const composerFormRef = useRef<ProFormInstance<RunFormValues> | undefined>(
    undefined
  );
  const [catalogSearch, setCatalogSearch] = useState("");
  const [selectedRouteName, setSelectedRouteName] = useState(
    scopeDraftPayload?.bundleName ??
      (endpointInvocationDraftPayload || observedRunDraftPayload
        ? ""
        : undefined) ??
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
  const [activeTransport, setActiveTransport] = useState<RunTransport>(
    initialFormValues.transport
  );
  const [consoleView, setConsoleView] = useState<ConsoleViewKey>(() =>
    resolveConsoleViewForEndpoint(
      normalizeRunEndpointKind(
        initialFormValues.endpointKind,
        initialFormValues.endpointId
      )
    )
  );
  const [hasStartedRun, setHasStartedRun] = useState(false);
  const [isInspectorDrawerOpen, setIsInspectorDrawerOpen] = useState(false);
  const [isSetupDrawerOpen, setIsSetupDrawerOpen] = useState(false);
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
  const [activeEndpointKind, setActiveEndpointKind] = useState<RunEndpointKind>(
    normalizeRunEndpointKind(
      initialFormValues.endpointKind,
      initialFormValues.endpointId
    )
  );
  const [activeEndpointId, setActiveEndpointId] = useState(
    resolveStoredRunEndpointId(
      initialFormValues.endpointKind,
      initialFormValues.endpointId
    )
  );
  const [composerPrompt, setComposerPrompt] = useState(
    initialFormValues.prompt ?? ""
  );
  const [activePrompt, setActivePrompt] = useState(
    initialFormValues.prompt ?? ""
  );
  const [activePayloadTypeUrl, setActivePayloadTypeUrl] = useState(
    initialFormValues.payloadTypeUrl ?? ""
  );
  const [activePayloadBase64, setActivePayloadBase64] = useState(
    initialFormValues.payloadBase64 ?? ""
  );
  const handleRouteSelection = useCallback((value: string) => {
    const normalizedValue = value ?? "";
    setSelectedRouteName((currentValue) =>
      currentValue === normalizedValue ? currentValue : normalizedValue
    );
  }, []);
  const handleEndpointKindChange = useCallback((value: RunEndpointKind) => {
    setActiveEndpointKind((currentValue) =>
      currentValue === value ? currentValue : value
    );
  }, []);
  const handleEndpointChange = useCallback((value: string) => {
    const normalizedValue = value.trim();
    setActiveEndpointId((currentValue) =>
      currentValue === normalizedValue ? currentValue : normalizedValue
    );
  }, []);
  const handleComposerPromptChange = useCallback((value: string) => {
    setComposerPrompt(value);
    if (composerFormRef.current?.setFieldValue) {
      composerFormRef.current.setFieldValue("prompt", value);
      return;
    }

    composerFormRef.current?.setFieldsValue?.({
      prompt: value,
    });
  }, []);
  const handleTransportChange = useCallback((value: RunTransport) => {
    setSelectedTransport((currentValue) =>
      currentValue === value ? currentValue : value
    );
  }, []);
  const stopActiveRunRef = useRef<(() => void) | undefined>(undefined);
  const autoStartedDraftRunRef = useRef(false);
  const hydratedObservedRunRef = useRef(false);

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

  const hydrateObservedSession = useCallback(
    (snapshot: {
      actorId?: string;
      endpointId: string;
      endpointKind?: RunEndpointKind;
      events: AGUIEvent[];
      payloadBase64?: string;
      payloadTypeUrl?: string;
      prompt: string;
      routeName?: string;
      scopeId: string;
      serviceOverrideId?: string;
    }) => {
      abortRun();
      reset();
      setTransportIssue(undefined);
      setHasStartedRun(true);
      setActiveTransport(selectedTransport);
      setActiveScopeId(snapshot.scopeId);
      setActiveServiceOverrideId(snapshot.serviceOverrideId ?? "");
      setActiveEndpointKind(
        normalizeRunEndpointKind(snapshot.endpointKind, snapshot.endpointId)
      );
      setActiveEndpointId(
        resolveStoredRunEndpointId(snapshot.endpointKind, snapshot.endpointId)
      );
      setSelectedRouteName(snapshot.routeName ?? "");
      setSelectedTraceItemKey("");
      setConsoleView(
        resolveConsoleViewForEndpoint(
          normalizeRunEndpointKind(snapshot.endpointKind, snapshot.endpointId)
        )
      );
      setComposerPrompt(snapshot.prompt);
      setActivePrompt(snapshot.prompt);
      setActivePayloadTypeUrl(snapshot.payloadTypeUrl ?? "");
      setActivePayloadBase64(snapshot.payloadBase64 ?? "");
      setRunStartedAtMs(Date.now());

      composerFormRef.current?.setFieldsValue({
        ...initialFormValues,
        actorId: snapshot.actorId,
        endpointId: snapshot.endpointId,
        endpointKind: normalizeRunEndpointKind(
          snapshot.endpointKind,
          snapshot.endpointId
        ),
        payloadBase64: snapshot.payloadBase64,
        payloadTypeUrl: snapshot.payloadTypeUrl,
        prompt: snapshot.prompt,
        routeName: snapshot.routeName,
        scopeId: snapshot.scopeId,
        serviceOverrideId: snapshot.serviceOverrideId,
        transport: selectedTransport,
      });

      snapshot.events.forEach((event) => {
        dispatch(event);
      });
    },
    [abortRun, dispatch, initialFormValues, reset, selectedTransport]
  );

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
      const runAttempt = async (
        requestedRun: RunFormValues,
        allowMissingServiceRecovery: boolean
      ): Promise<void> => {
        const normalizedScopeId = scopeId.trim();
        const normalizedEndpointKind = normalizeRunEndpointKind(
          requestedRun.endpointKind,
          requestedRun.endpointId
        );
        const normalizedEndpointId = resolveStoredRunEndpointId(
          normalizedEndpointKind,
          requestedRun.endpointId
        );
        const resolvedServiceId = resolveRequestedServiceId(
          requestedRun,
          Boolean(scopeDraftPayload)
        );
        const requestedPayloadTypeUrl =
          requestedRun.payloadTypeUrl?.trim() ?? "";
        const requestedPayloadBase64 =
          requestedRun.payloadBase64?.trim() ?? "";

        if (!normalizedScopeId) {
          throw new Error("Scope ID is required.");
        }
        if (normalizedEndpointKind === "command" && !normalizedEndpointId) {
          throw new Error("Endpoint ID is required for command invokes.");
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
        setHasStartedRun(true);
        setActiveTransport(requestedRun.transport);
        setActiveScopeId(normalizedScopeId);
        setActiveServiceOverrideId(resolvedServiceId);
        setActiveEndpointKind(normalizedEndpointKind);
        setActiveEndpointId(normalizedEndpointId);
        setComposerPrompt(requestedRun.prompt);
        setActivePrompt(requestedRun.prompt);
        setActivePayloadTypeUrl(requestedRun.payloadTypeUrl ?? "");
        setActivePayloadBase64(requestedRun.payloadBase64 ?? "");
        setConsoleView(resolveConsoleViewForEndpoint(normalizedEndpointKind));
        setRunStartedAtMs(Date.now());
        setStreaming(true);

        try {
          const controller = new AbortController();
          stopActiveRunRef.current = () => controller.abort();

          const response = scopeDraftPayload
            ? await runtimeRunsApi.streamDraftRun(
                normalizedScopeId,
                {
                  prompt: requestedRun.prompt,
                  workflowYamls: scopeDraftPayload.bundleYamls,
                },
                controller.signal
              )
            : normalizedEndpointKind === "chat" &&
                !requestedRun.payloadTypeUrl?.trim() &&
                !requestedRun.payloadBase64?.trim()
              ? await runtimeRunsApi.streamChat(
                  normalizedScopeId,
                  {
                    prompt: requestedRun.prompt,
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

              if (
                allowMissingServiceRecovery &&
                normalizedEndpointKind === "chat" &&
                resolvedServiceId &&
                event.type === AGUIEventType.RUN_ERROR &&
                isMissingScopeServiceError(event.message ?? "")
              ) {
                composerFormRef.current?.setFieldsValue({
                  routeName: undefined,
                  serviceOverrideId: undefined,
                });
                setSelectedRouteName("");
                setActiveServiceOverrideId("");
                messageApi.warning(
                  `Selected service '${extractMissingServiceId(
                    event.message ?? ""
                  )}' is no longer available. Retrying with the scope default binding.`
                );
                await runAttempt(
                  {
                    ...requestedRun,
                    routeName: undefined,
                    serviceOverrideId: undefined,
                  },
                  false
                );
                return;
              }

              dispatch(event);
            }
          } else {
            const receipt = await runtimeRunsApi.invokeEndpoint(
              normalizedScopeId,
              {
                endpointId: normalizedEndpointId,
                prompt: requestedRun.prompt,
                commandId: undefined,
                payloadTypeUrl: requestedRun.payloadTypeUrl || undefined,
                payloadBase64: requestedRun.payloadBase64 || undefined,
              },
              {
                serviceId: resolvedServiceId || undefined,
              }
            );
            const receiptRunId =
              String(
                receipt.request_id ??
                  receipt.requestId ??
                  receipt.commandId ??
                  ""
              ).trim() || `${normalizedEndpointId}-${Date.now().toString(36)}`;
            const receiptActorId = String(
              receipt.target_actor_id ??
                receipt.targetActorId ??
                receipt.actorId ??
                ""
            ).trim();
            const receiptCorrelationId =
              String(
                receipt.correlation_id ??
                  receipt.correlationId ??
                  receiptRunId
              ).trim() || receiptRunId;

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
                  String(
                    receipt.command_id ?? receipt.commandId ?? ""
                  ).trim() || undefined,
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
          if (
            allowMissingServiceRecovery &&
            normalizedEndpointKind === "chat" &&
            resolvedServiceId &&
            isMissingScopeServiceError(text)
          ) {
            composerFormRef.current?.setFieldsValue({
              routeName: undefined,
              serviceOverrideId: undefined,
            });
            setSelectedRouteName("");
            setActiveServiceOverrideId("");
            messageApi.warning(
              `Selected service '${extractMissingServiceId(
                text
              )}' is no longer available. Retrying with the scope default binding.`
            );
            await runAttempt(
              {
                ...requestedRun,
                routeName: undefined,
                serviceOverrideId: undefined,
              },
              false
            );
            return;
          }

          reportTransportError(text);
        } finally {
          stopActiveRunRef.current = undefined;
          setStreaming(false);
        }
      };

      await runAttempt(request, true);
    },
    [
      abortRun,
      composerFormRef,
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

  const teamAdvancedHref = useMemo(() => {
    if (requestedReturnTo) {
      return requestedReturnTo;
    }

    const scopeId = resolveRunScopeId();
    if (!scopeId) {
      return "";
    }

    return buildTeamDetailHref({
      scopeId,
      tab: "advanced",
      runId: session.runId || undefined,
    });
  }, [requestedReturnTo, resolveRunScopeId, session.runId]);

  const resolveRunServiceOverrideId = useCallback(() => {
    return (
      activeServiceOverrideId.trim() ||
      composerFormRef.current?.getFieldValue("serviceOverrideId")?.trim?.() ||
      ""
    );
  }, [activeServiceOverrideId]);

  const resolveRunEndpointKind = useCallback(() => {
    return normalizeRunEndpointKind(
      activeEndpointKind,
      activeEndpointId ||
        composerFormRef.current?.getFieldValue("endpointId")?.trim?.()
    );
  }, [activeEndpointId, activeEndpointKind]);

  const resolveRunEndpointId = useCallback(() => {
    return resolveStoredRunEndpointId(
      resolveRunEndpointKind(),
      activeEndpointId ||
        composerFormRef.current?.getFieldValue("endpointId")?.trim?.()
    );
  }, [activeEndpointId, resolveRunEndpointKind]);

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
    if (
      !observedRunDraftPayload ||
      !draftRunKey ||
      hydratedObservedRunRef.current
    ) {
      return;
    }

    hydratedObservedRunRef.current = true;
    deleteQueuedDraftRunPayload(draftRunKey);

    if (typeof window !== "undefined") {
      const searchParams = new URLSearchParams(window.location.search);
      searchParams.delete("draftKey");
      const search = searchParams.toString();
      // Avoid router remount during observed-session handoff; we only need to clean the URL.
      window.history.replaceState(
        window.history.state,
        "",
        `${window.location.pathname}${search ? `?${search}` : ""}${window.location.hash}`
      );
    }

    hydrateObservedSession({
      actorId: observedRunDraftPayload.actorId,
      endpointId: observedRunDraftPayload.endpointId,
      endpointKind: observedRunDraftPayload.endpointKind,
      events: observedRunDraftPayload.events,
      payloadBase64: observedRunDraftPayload.payloadBase64,
      payloadTypeUrl: observedRunDraftPayload.payloadTypeUrl,
      prompt: observedRunDraftPayload.prompt,
      routeName: undefined,
      scopeId: observedRunDraftPayload.scopeId,
      serviceOverrideId: observedRunDraftPayload.serviceOverrideId,
    });
  }, [
    draftRunKey,
    hydrateObservedSession,
    observedRunDraftPayload,
  ]);

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
      endpointKind: "chat",
      payloadBase64: undefined,
      payloadTypeUrl: undefined,
      prompt,
      scopeId,
      serviceOverrideId: undefined,
      routeName: scopeDraftPayload.bundleName,
      transport: initialFormValues.transport ?? "sse",
    });
  }, [draftRunKey, initialFormValues, scopeDraftPayload, sendRun]);

  const endpointKind = resolveRunEndpointKind();
  const endpointName = resolveRunEndpointId();
  const routeName = useMemo(() => {
    const sessionWorkflowName = trimOptional(session.context?.workflowName);
    if (sessionWorkflowName) {
      return sessionWorkflowName;
    }

    if (scopeDraftPayload?.bundleName) {
      return scopeDraftPayload.bundleName;
    }

    if (endpointKind !== "chat") {
      return endpointName;
    }

    return trimOptional(selectedRouteName);
  }, [
    endpointKind,
    endpointName,
    scopeDraftPayload,
    selectedRouteName,
    session.context?.workflowName,
  ]);
  const actorId = session.context?.actorId;
  const commandId = session.context?.commandId ?? "";
  const payloadTypeUrl =
    activePayloadTypeUrl.trim() ||
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

      if (!endpointName || endpointKind === "chat") {
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
    endpointKind,
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

  const snapshotMessageFallback = useMemo(() => {
    const snapshot = actorSnapshotQuery.data;
    if (!snapshot || session.status !== "finished") {
      return "";
    }

    const snapshotCommandId = snapshot.lastCommandId?.trim() ?? "";
    if (commandId && snapshotCommandId && snapshotCommandId !== commandId) {
      return "";
    }

    return snapshot.lastOutput?.trim() ?? "";
  }, [actorSnapshotQuery.data, commandId, session.status]);

  const displayedMessages = useMemo(() => {
    if (session.messages.length > 0) {
      return session.messages;
    }

    const fallbackContent = resolveRunMessageFallback(
      session.events,
      snapshotMessageFallback
    );
    if (!fallbackContent) {
      return session.messages;
    }

    return [
      {
        messageId: `final-output:${session.runId || commandId || actorId || "latest"}`,
        role: "assistant",
        content: fallbackContent,
        complete: true,
      },
    ] as typeof session.messages;
  }, [
    actorId,
    commandId,
    session.events,
    session.messages,
    session.runId,
    snapshotMessageFallback,
  ]);

  const latestMessagePreview = useMemo(() => {
    const lastWithContent = [...displayedMessages]
      .reverse()
      .find((item) => item.content?.trim());
    return lastWithContent?.content?.trim() ?? "";
  }, [displayedMessages]);

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
        onOpenActor: entry.actorId
          ? () =>
              history.push(
                buildRuntimeExplorerHref({
                  actorId: entry.actorId,
                  runId: entry.runId || undefined,
                  scopeId: entry.scopeId || undefined,
                  serviceOverrideId:
                    entry.serviceOverrideId === entry.routeName
                      ? undefined
                      : entry.serviceOverrideId || undefined,
                })
              )
          : undefined,
        onRestore: () => {
          const restoredEndpointKind = normalizeRunEndpointKind(
            entry.endpointKind,
            entry.endpointId
          );
          const restoredEndpointId = resolveStoredRunEndpointId(
            restoredEndpointKind,
            entry.endpointId
          );
          const isChatEndpoint = restoredEndpointKind === "chat";
          const restoredServiceOverrideId =
            isChatEndpoint &&
            entry.serviceOverrideId === entry.routeName
              ? undefined
              : entry.serviceOverrideId || undefined;
          const restoredRouteName = isChatEndpoint
            ? entry.routeName || undefined
            : undefined;

          if (entry.observedEvents.length > 0 && entry.scopeId) {
            hydrateObservedSession({
              actorId: entry.actorId || undefined,
              endpointId: restoredEndpointId,
              endpointKind: restoredEndpointKind,
              events: entry.observedEvents,
              payloadBase64: entry.payloadBase64 || undefined,
              payloadTypeUrl: entry.payloadTypeUrl || undefined,
            prompt: entry.prompt,
            routeName: restoredRouteName,
            scopeId: entry.scopeId,
            serviceOverrideId: restoredServiceOverrideId,
          });
            return;
          }

          composerFormRef.current?.setFieldsValue({
            prompt: entry.prompt,
            routeName: restoredRouteName,
            scopeId: entry.scopeId || undefined,
            serviceOverrideId: restoredServiceOverrideId,
            endpointId: restoredEndpointId,
            endpointKind: restoredEndpointKind,
            payloadTypeUrl: entry.payloadTypeUrl || undefined,
            payloadBase64: entry.payloadBase64 || undefined,
            actorId: entry.actorId || undefined,
            transport: selectedTransport,
          });
          setComposerPrompt(entry.prompt);
          setSelectedRouteName(isChatEndpoint ? entry.routeName : "");
          setActiveEndpointKind(restoredEndpointKind);
          setActiveEndpointId(restoredEndpointId);
        },
      })),
    [hydrateObservedSession, recentRuns, selectedTransport]
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
  const hasRunActivity =
    hasStartedRun ||
    streaming ||
    session.status !== "idle" ||
    session.events.length > 0 ||
    displayedMessages.length > 0 ||
    Boolean(session.runId) ||
    Boolean(actorId) ||
    hasPendingInteraction ||
    Boolean(transportIssue);
  const composerRailInitialValues = useMemo<RunFormValues>(
    () =>
      hasRunActivity
        ? {
            ...initialFormValues,
            actorId: actorId ?? undefined,
            endpointId: activeEndpointId || initialFormValues.endpointId,
            endpointKind: activeEndpointKind,
            payloadBase64: activePayloadBase64 || undefined,
            payloadTypeUrl: activePayloadTypeUrl || undefined,
            prompt: activePrompt,
            routeName: selectedRouteName || undefined,
            scopeId: activeScopeId || undefined,
            serviceOverrideId: activeServiceOverrideId || undefined,
            transport: activeTransport,
          }
        : initialFormValues,
    [
      activeEndpointId,
      activeEndpointKind,
      activePayloadBase64,
      activePayloadTypeUrl,
      activePrompt,
      activeScopeId,
      activeServiceOverrideId,
      activeTransport,
      actorId,
      hasRunActivity,
      initialFormValues,
      selectedRouteName,
    ]
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
    if (hasRunActivity) {
      setIsSetupDrawerOpen(false);
    }
  }, [hasRunActivity]);

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
      routeName: routeName ?? "",
      endpointId: endpointName,
      endpointKind,
      actorId: actorId ?? "",
      commandId,
      runId: session.runId ?? "",
      focusStatus: runFocus.status,
      focusLabel: runFocus.label,
      lastEventAt,
      messageCount: displayedMessages.length,
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
      displayedMessages.length,
      session.runId,
      session.status,
      endpointKind,
      endpointName,
      routeName,
    ]
  );

  useEffect(() => {
    const prompt = activePrompt;
    const currentPayloadTypeUrl = activePayloadTypeUrl;
    const currentPayloadBase64 = activePayloadBase64;
    const currentEndpointKind = resolveRunEndpointKind();
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
          currentEndpointKind === "chat" &&
          currentServiceOverrideId === routeName
            ? ""
            : currentServiceOverrideId,
        endpointId: currentEndpointId,
        endpointKind: currentEndpointKind,
        payloadTypeUrl: currentPayloadTypeUrl,
        payloadBase64: currentPayloadBase64,
        routeName,
        prompt,
        actorId: actorId ?? "",
        commandId,
        runId: session.runId ?? "",
        status: session.status,
        lastMessagePreview: latestMessagePreview,
        observedEvents: session.events.map((event) => ({ ...event })),
      })
    );
  }, [
    activePayloadBase64,
    activePayloadTypeUrl,
    activePrompt,
    actorId,
    commandId,
    latestMessagePreview,
    resolveRunEndpointKind,
    resolveRunScopeId,
    resolveRunServiceOverrideId,
    resolveRunEndpointId,
    session.events,
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
    ? "/api/scopes/{scopeId}/workflow/draft-run"
    : endpointKind === "chat"
      ? resolveRequestedServiceId(
          {
            endpointId: endpointName,
            endpointKind,
            routeName: selectedRouteName,
            serviceOverrideId: hasRunActivity
              ? activeServiceOverrideId || undefined
              : initialFormValues.serviceOverrideId,
          },
          false
        )
        ? "/api/scopes/{scopeId}/services/{serviceId}/invoke/chat:stream"
        : "/api/scopes/{scopeId}/invoke/chat:stream"
      : trimOptional(
          hasRunActivity
            ? activeServiceOverrideId || undefined
            : initialFormValues.serviceOverrideId
        )
        ? "/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}"
        : "/api/scopes/{scopeId}/invoke/{endpointId}";

  const isChatConsole = endpointKind === "chat";

  const handleSubmitResume = useCallback(
    async (values: ResumeFormValues) => {
      if (!actorId || !humanInputRecord?.runId || !humanInputRecord.stepId) {
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
      return true;
    },
    [actorId, commandId, humanInputRecord, messageApi, resume]
  );

  const handleSubmitSignal = useCallback(
    async (values: SignalFormValues) => {
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
      return true;
    },
    [actorId, commandId, messageApi, signal, waitingSignal]
  );

  const chatActionRequiredCard = hasPendingInteraction ? (
    <RunsActionRequiredPanel
      humanInputRecord={humanInputRecord}
      onSubmitResume={handleSubmitResume}
      onSubmitSignal={handleSubmitSignal}
      resuming={resuming}
      signaling={signaling}
      variant="chat"
      waitingSignalRecord={waitingSignalRecord}
    />
  ) : null;

  const messageConsoleView = (
    <RunsMessagesView
      emptyDescription={
        isChatConsole
          ? "No conversation yet. Send a prompt to start the run."
          : "No message output yet."
      }
      messages={displayedMessages}
      topAccessory={isChatConsole ? chatActionRequiredCard : undefined}
      title={isChatConsole ? "Conversation" : "Message stream"}
    />
  );

  const eventConsoleView = (
    <RunsEventsView
      onSelectItem={(item) => {
        setSelectedTraceItemKey(item.key);
        setIsInspectorDrawerOpen(true);
      }}
      rows={eventRows}
      selectedItemKey={selectedTraceItemKey}
    />
  );

  const handleSubmitComposer = useCallback(async () => {
    const prompt = composerPrompt.trim();
    if (!prompt) {
      messageApi.warning("Prompt is required.");
      return;
    }

    const currentValues =
      composerFormRef.current?.getFieldsValue?.() ??
      ({} as Partial<RunFormValues>);
    const nextEndpointKind = normalizeRunEndpointKind(
      currentValues.endpointKind ?? activeEndpointKind,
      currentValues.endpointId ?? activeEndpointId
    );
    const nextValues: RunFormValues = {
      actorId:
        typeof currentValues.actorId === "string"
          ? currentValues.actorId
          : actorId ?? undefined,
      endpointId:
        typeof currentValues.endpointId === "string"
          ? currentValues.endpointId
          : activeEndpointId || "chat",
      endpointKind: nextEndpointKind,
      payloadBase64:
        typeof currentValues.payloadBase64 === "string"
          ? currentValues.payloadBase64
          : activePayloadBase64 || undefined,
      payloadTypeUrl:
        typeof currentValues.payloadTypeUrl === "string"
          ? currentValues.payloadTypeUrl
          : activePayloadTypeUrl || undefined,
      prompt,
      routeName:
        typeof currentValues.routeName === "string"
          ? currentValues.routeName
          : selectedRouteName || undefined,
      scopeId:
        typeof currentValues.scopeId === "string"
          ? currentValues.scopeId
          : activeScopeId || "",
      serviceOverrideId:
        typeof currentValues.serviceOverrideId === "string"
          ? currentValues.serviceOverrideId
          : activeServiceOverrideId || undefined,
      transport:
        currentValues.transport === "ws" ? "ws" : selectedTransport,
    };

    await sendRun(nextValues.scopeId ?? "", nextValues);
  }, [
    activeEndpointId,
    activeEndpointKind,
    activePayloadBase64,
    activePayloadTypeUrl,
    activeScopeId,
    activeServiceOverrideId,
    actorId,
    composerPrompt,
    messageApi,
    selectedRouteName,
    selectedTransport,
    sendRun,
  ]);

  const launchRailContent = (
    <RunsLaunchRail
      actorId={actorId ?? undefined}
      activeEndpointId={endpointName}
      activeEndpointKind={endpointKind}
      catalogSearch={catalogSearch}
      composerFormRef={composerFormRef}
      draftMode={Boolean(draftRunPayload)}
      initialFormValues={composerRailInitialValues}
      recentRunRows={recentRunRows}
      selectedTransport={selectedTransport}
      selectedRouteDetailsPrimitives={selectedRouteDetails?.primitives ?? []}
      selectedRouteRecord={selectedRouteRecord}
      showPromptField={!isChatConsole}
      showSubmitActions={!isChatConsole}
      streaming={streaming}
      submitPathLabel={submitPathLabel}
      transportOptions={[{ label: "Service SSE stream", value: "sse" }]}
      variant={isChatConsole ? "chat" : "default"}
      visiblePresets={visiblePresets}
      workflowCatalogLoading={workflowCatalogQuery.isLoading}
      routeOptions={routeOptions}
      onAbortRun={abortRun}
      onCatalogSearchChange={setCatalogSearch}
      onClearRecentRuns={() => setRecentRuns(clearRecentRuns())}
      onEndpointChange={handleEndpointChange}
      onEndpointKindChange={handleEndpointKindChange}
      onSelectRouteName={handleRouteSelection}
      onSubmitRun={async (values) => {
        await sendRun(values.scopeId ?? "", values);
      }}
      onTransportChange={handleTransportChange}
      onUsePreset={(record) => {
        composerFormRef.current?.setFieldsValue({
          prompt: record.prompt,
          routeName: scopeDraftPayload?.bundleName ?? record.routeName,
          scopeId:
            composerFormRef.current?.getFieldValue("scopeId") ??
            composerRailInitialValues.scopeId,
          serviceOverrideId: undefined,
          endpointId:
            endpointKind === "chat" ? endpointName || "chat" : "chat",
          endpointKind: "chat",
          payloadTypeUrl: undefined,
          payloadBase64: undefined,
          actorId: undefined,
          transport: selectedTransport,
        });
        setComposerPrompt(record.prompt);
        setSelectedRouteName(scopeDraftPayload?.bundleName ?? record.routeName);
        setActiveEndpointKind("chat");
        setActiveEndpointId(
          endpointKind === "chat" ? endpointName || "chat" : "chat"
        );
        setCatalogSearch("");
      }}
    />
  );

  return (
    <PageContainer pageHeaderRender={false} style={{ overflow: "hidden" }}>
      {messageContextHolder}
      <div style={runsWorkbenchShellStyle}>
        <div style={runsWorkbenchHeaderBarStyle}>
          <div style={runsWorkbenchHeaderTitleStyle}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              Run Console
            </Typography.Title>
            <Popover
              content={
                <Typography.Paragraph
                  style={{ margin: 0, maxWidth: 360 }}
                  type="secondary"
                >
                  Start a scoped run over{" "}
                  <Typography.Text code>
                    {submitPathLabel}
                  </Typography.Text>{" "}
                  and stay in one place for conversation, events, trace, and
                  operator actions.
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
          <div
            className={runsWorkbenchHeaderToolbarClassName}
            style={runsWorkbenchHeaderActionStyle}
          >
            <style>{runsWorkbenchHeaderToolbarCss}</style>
            {teamAdvancedHref ? (
              <Button
                className={`${runsWorkbenchHeaderButtonClassName} ${runsWorkbenchHeaderButtonAccentClassName}`}
                icon={<ArrowLeftOutlined />}
                onClick={() => history.push(teamAdvancedHref)}
              >
                返回团队高级编辑
              </Button>
            ) : null}
            <Button
              className={runsWorkbenchHeaderButtonClassName}
              icon={<AppstoreOutlined />}
              onClick={() => history.push(buildRuntimeWorkflowsHref())}
            >
              Workflow catalog
            </Button>
            <Button
              className={runsWorkbenchHeaderButtonClassName}
              disabled={!actorId && !session.runId}
              icon={<DeploymentUnitOutlined />}
              onClick={() =>
                history.push(
                  buildRuntimeExplorerHref({
                    actorId: actorId ?? undefined,
                    runId: session.runId || undefined,
                    scopeId: activeScopeId || undefined,
                    serviceOverrideId: activeServiceOverrideId || undefined,
                  })
                )
              }
            >
              Actor explorer
            </Button>
          </div>
        </div>
        {isChatConsole ? (
          <div style={runsChatLayoutStyle}>
            <div style={runsChatSidebarStyle}>{launchRailContent}</div>
            <div style={runsChatMainStyle}>
              {hasRunActivity ? (
                <RunsStatusStrip
                  activeStepCount={session.activeSteps.size}
                  compact
                  elapsedLabel={elapsedLabel}
                  eventCount={session.events.length}
                  hasPendingInteraction={hasPendingInteraction}
                  isRunLive={isRunLive}
                  messageCount={displayedMessages.length}
                  onAbort={handleAbortRun}
                  onOpenDetails={() => setIsInspectorDrawerOpen(true)}
                  onOpenSetup={() => setIsSetupDrawerOpen(true)}
                  runId={session.runId || commandId || "Not started"}
                  runStatusLabel={runStatusText}
                  showSetupAction={false}
                  statusTone={runStatusTone}
                  transport={activeTransport}
                  endpointId={endpointName}
                  endpointKind={endpointKind}
                />
              ) : null}
              <div style={runsChatTraceWrapStyle}>
                <RunsTracePane
                  consoleView={consoleView}
                  eventConsoleView={eventConsoleView}
                  eventCount={eventRows.length}
                  hasPendingInteraction={hasPendingInteraction}
                  messageConsoleView={messageConsoleView}
                  messageCount={displayedMessages.length}
                  messagesLabel="Conversation"
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
                  title="Conversation"
                />
              </div>
              <div
                className={runsChatComposerClassName}
                style={runsChatComposerCardStyle}
              >
                <style>{runsChatComposerCss}</style>
                <div style={runsChatComposerHeaderStyle}>
                  <div style={runsChatComposerLabelStyle}>Prompt</div>
                  <Typography.Text style={runsChatComposerHintStyle}>
                    Enter to send
                  </Typography.Text>
                </div>
                <div className={runsChatComposerBodyClassName}>
                  <div style={runsChatComposerInputWrapStyle}>
                    <div
                      className={runsChatComposerInputShellClassName}
                    >
                      <Input.TextArea
                        aria-label="Prompt"
                        autoSize={{ minRows: 2, maxRows: 6 }}
                        className={runsChatComposerInputClassName}
                        onChange={(event) =>
                          handleComposerPromptChange(event.target.value)
                        }
                        onKeyDown={(event) => {
                          if (
                            event.key === "Enter" &&
                            !event.shiftKey &&
                            !event.nativeEvent.isComposing
                          ) {
                            event.preventDefault();
                            void handleSubmitComposer();
                          }
                        }}
                        placeholder="Describe the task to run."
                        style={runsChatComposerTextareaStyle}
                        value={composerPrompt}
                      />
                    </div>
                  </div>
                  <div
                    className={runsChatComposerActionsClassName}
                    style={runsChatComposerActionsStyle}
                  >
                    <Button
                      icon={<SendOutlined />}
                      loading={streaming}
                      onClick={() => void handleSubmitComposer()}
                      style={runsChatComposerSendButtonStyle}
                      type="primary"
                    >
                      Send
                    </Button>
                  </div>
                </div>
              </div>
            </div>
          </div>
        ) : hasRunActivity ? (
          <div style={runsRunStateStyle}>
            <div style={runsWorkbenchMonitorStyle}>
              <RunsStatusStrip
                activeStepCount={session.activeSteps.size}
                elapsedLabel={elapsedLabel}
                eventCount={session.events.length}
                hasPendingInteraction={hasPendingInteraction}
                isRunLive={isRunLive}
                messageCount={displayedMessages.length}
                onAbort={handleAbortRun}
                onOpenDetails={() => setIsInspectorDrawerOpen(true)}
                onOpenSetup={() => setIsSetupDrawerOpen(true)}
                runId={session.runId || commandId || "Not started"}
                runStatusLabel={runStatusText}
                statusTone={runStatusTone}
                transport={activeTransport}
                endpointId={endpointName}
                endpointKind={endpointKind}
              />
              {hasPendingInteraction ? (
                <RunsActionRequiredPanel
                  humanInputRecord={humanInputRecord}
                  onSubmitResume={handleSubmitResume}
                  onSubmitSignal={handleSubmitSignal}
                  resuming={resuming}
                  signaling={signaling}
                  variant="card"
                  waitingSignalRecord={waitingSignalRecord}
                />
              ) : null}
              <div style={workbenchOverviewGridStyle}>
                <RunsTracePane
                  consoleView={consoleView}
                  eventConsoleView={eventConsoleView}
                  eventCount={eventRows.length}
                  hasPendingInteraction={hasPendingInteraction}
                  messageConsoleView={messageConsoleView}
                  messageCount={displayedMessages.length}
                  messagesLabel="Messages"
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
                  title="Invocation trace"
                />
              </div>
            </div>
          </div>
        ) : (
          <div style={runsSetupStateStyle}>
            <div style={runsSetupRailViewportStyle}>{launchRailContent}</div>
          </div>
        )}

        {hasRunActivity && isSetupDrawerOpen ? (
          <Drawer
            destroyOnHidden
            mask={false}
            open
            size={560}
            styles={{ body: drawerBodyStyle }}
            title="Run setup"
            onClose={() => setIsSetupDrawerOpen(false)}
          >
            <div style={drawerScrollStyle}>{launchRailContent}</div>
          </Drawer>
        ) : null}

        {isInspectorDrawerOpen ? (
          <Drawer
            destroyOnHidden
            mask={false}
            open
            styles={{ body: drawerBodyStyle }}
            title={
              hasPendingInteraction
                ? "Details · action pending"
                : "Details"
            }
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
              </div>
            </div>
          </Drawer>
        ) : null}
      </div>
    </PageContainer>
  );
};

export default RunsPage;
