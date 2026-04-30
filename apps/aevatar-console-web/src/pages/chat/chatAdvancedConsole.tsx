import { AGUIEventType, CustomEventName } from "@aevatar-react-sdk/types";
import { Alert, Empty, Space, Typography } from "antd";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { authFetch } from "@/shared/auth/fetch";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import type {
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorSnapshot,
} from "@/shared/models/runtime/actors";
import type { ScopeServiceRunAuditSnapshot } from "@/shared/models/runtime/scopeServices";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import {
  buildScopeConsoleServiceOptions,
  extractRuntimeInvokeReceipt,
  scopeServiceAppId,
} from "@/shared/runs/scopeConsole";
import { studioApi } from "@/shared/studio/api";
import { AevatarContextDrawer } from "@/shared/ui/aevatarPageShells";
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  AEVATAR_PRESSABLE_CARD_CLASS,
} from "@/shared/ui/interactionStandards";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import { DebugPanel } from "./chatPresentation";
import type { RuntimeEvent } from "./chatTypes";
import {
  buildTimelineRows,
  filterTimelineRows,
} from "../actors/actorPresentation";
import {
  buildTimelineBlockingSummary,
  describeActorCompletionStatus,
} from "./runtimeInspector";

type ConsoleTab = "query" | "execute" | "timeline" | "raw";
type QueryTarget = "binding" | "services" | "workflows" | "actor";

type ConsoleFlow = {
  badge?: string;
  description: string;
  group: "developer" | "operate" | "understand";
  id: ConsoleTab;
  label: string;
  priority: "primary" | "secondary";
};

type ChatAdvancedConsoleProps = {
  defaultServiceId?: string;
  onClose: () => void;
  onEnsureNyxIdBound?: () => Promise<void>;
  onTimelineActionResult?: (input: {
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
  }) => void;
  open: boolean;
  scopeId: string;
  services: readonly ServiceCatalogSnapshot[];
  sessionActorId?: string;
};

type ExecuteLaunchContext = {
  endpointId: string;
  endpointKind: string;
  payloadBase64: string;
  payloadTypeUrl: string;
  prompt: string;
  serviceId: string;
};

const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";

const queryTargets: { description: string; id: QueryTarget; label: string }[] = [
  {
    description: "Current default binding for this scope.",
    id: "binding",
    label: "Scope Binding",
  },
  {
    description: "All published services currently visible to this scope.",
    id: "services",
    label: "Services",
  },
  {
    description: "Workflow assets currently deployed into this scope.",
    id: "workflows",
    label: "Workflows",
  },
  {
    description: "Inspect a specific actor by its runtime ID.",
    id: "actor",
    label: "Actor Snapshot",
  },
];

const consoleFlows: readonly ConsoleFlow[] = [
  {
    badge: "Recommended first",
    description:
      "Check the default route target, published services, deployed workflows, or inspect an actor directly.",
    group: "understand",
    id: "query",
    label: "Query",
    priority: "primary",
  },
  {
    description:
      "Inspect actor state, timeline evidence, graph topology, and any blocking gate that needs operator action.",
    group: "understand",
    id: "timeline",
    label: "Timeline",
    priority: "secondary",
  },
  {
    badge: "Common next step",
    description:
      "Launch a service endpoint, capture the run receipt, and continue into Runs or Explorer when needed.",
    group: "operate",
    id: "execute",
    label: "Execute",
    priority: "primary",
  },
  {
    badge: "Expert",
    description:
      "Send direct API requests only when you need low-level integration or protocol debugging.",
    group: "developer",
    id: "raw",
    label: "Raw API",
    priority: "secondary",
  },
];

const drawerSectionStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #e7e5e4",
  borderRadius: 16,
  display: "flex",
  flexDirection: "column",
  gap: 12,
  padding: 16,
};

const fieldLabelStyle: React.CSSProperties = {
  color: "#6b7280",
  fontSize: 12,
  fontWeight: 600,
};

const monoBlockStyle: React.CSSProperties = {
  background: "#fafaf8",
  border: "1px solid #e7e5e4",
  borderRadius: 12,
  fontFamily: monoFontFamily,
  fontSize: 12,
  margin: 0,
  maxHeight: 320,
  overflow: "auto",
  padding: 14,
  whiteSpace: "pre-wrap",
};

const inputStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #d6d3d1",
  borderRadius: 10,
  color: "#111827",
  fontSize: 13,
  minHeight: 40,
  outline: "none",
  padding: "10px 12px",
  width: "100%",
};

const textareaStyle: React.CSSProperties = {
  ...inputStyle,
  fontFamily: monoFontFamily,
  minHeight: 120,
  resize: "vertical",
};

const selectStyle: React.CSSProperties = {
  ...inputStyle,
  fontFamily: monoFontFamily,
};

const actionButtonStyle = (
  tone: "primary" | "secondary",
  disabled = false
): React.CSSProperties => ({
  background: tone === "primary" ? "#111827" : "#ffffff",
  border: `1px solid ${tone === "primary" ? "#111827" : "#d6d3d1"}`,
  borderRadius: 10,
  color: tone === "primary" ? "#ffffff" : "#4b5563",
  cursor: disabled ? "not-allowed" : "pointer",
  fontSize: 13,
  fontWeight: 600,
  opacity: disabled ? 0.45 : 1,
  padding: "9px 14px",
});

function timelineStatusTone(
  status: "processing" | "success" | "error" | "default"
): { background: string; color: string } {
  switch (status) {
    case "processing":
      return {
        background: "#eff6ff",
        color: "#1d4ed8",
      };
    case "success":
      return {
        background: "#ecfdf5",
        color: "#047857",
      };
    case "error":
      return {
        background: "#fef2f2",
        color: "#dc2626",
      };
    default:
      return {
        background: "#f5f5f4",
        color: "#57534e",
      };
  }
}

function safeJson(value: unknown): string {
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function createResultPanel(
  label: string,
  value: string,
  onCopy?: () => void
): React.ReactElement {
  return (
    <div style={drawerSectionStyle}>
      <div
        style={{
          alignItems: "center",
          display: "flex",
          gap: 12,
          justifyContent: "space-between",
        }}
      >
        <Typography.Text strong>{label}</Typography.Text>
        {onCopy ? (
          <button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={onCopy}
            style={actionButtonStyle("secondary")}
            type="button"
          >
            Copy
          </button>
        ) : null}
      </div>
      <pre style={monoBlockStyle}>{value}</pre>
    </div>
  );
}

function renderAuditPreviewCard(
  title: string,
  description: string,
  stamp?: string | null,
  keySuffix?: string
): React.ReactElement {
  return (
    <div
      key={`${title}-${stamp || "nostamp"}-${keySuffix || "item"}`}
      style={{
        border: "1px solid #e7e5e4",
        borderRadius: 12,
        display: "flex",
        flexDirection: "column",
        gap: 6,
        padding: 12,
      }}
    >
      <Typography.Text strong>{title}</Typography.Text>
      <Typography.Text type="secondary">
        {description || "No detail"}
      </Typography.Text>
      {stamp ? (
        <Typography.Text type="secondary">
          {formatDateTime(stamp)}
        </Typography.Text>
      ) : null}
    </div>
  );
}

function createObservedExecutionEvents(context: {
  actorId?: string;
  commandId?: string;
  correlationId?: string;
  runId?: string;
}): RuntimeEvent[] {
  const events: RuntimeEvent[] = [];

  if (context.runId?.trim()) {
    events.push({
      runId: context.runId.trim(),
      threadId:
        context.correlationId?.trim() ||
        context.commandId?.trim() ||
        context.runId.trim(),
      timestamp: Date.now(),
      type: AGUIEventType.RUN_STARTED,
    } as RuntimeEvent);
  }

  if (context.actorId?.trim() || context.commandId?.trim()) {
    events.push({
      name: CustomEventName.RunContext,
      timestamp: Date.now(),
      type: AGUIEventType.CUSTOM,
      value: {
        actorId: context.actorId?.trim() || undefined,
        commandId: context.commandId?.trim() || undefined,
      },
    } as RuntimeEvent);
  }

  return events;
}

export function ChatAdvancedConsole({
  defaultServiceId,
  onClose,
  onEnsureNyxIdBound,
  onTimelineActionResult,
  open,
  scopeId,
  services,
  sessionActorId,
}: ChatAdvancedConsoleProps): React.ReactElement {
  const executeAbortRef = useRef<AbortController | null>(null);

  const consoleServices = useMemo(
    () =>
      buildScopeConsoleServiceOptions(services, defaultServiceId, {
        sortBy: "displayName",
      }),
    [defaultServiceId, services]
  );
  const [activeTab, setActiveTab] = useState<ConsoleTab>("query");
  const [queryTarget, setQueryTarget] = useState<QueryTarget>("binding");
  const [queryActorId, setQueryActorId] = useState("");
  const [queryLoading, setQueryLoading] = useState(false);
  const [queryResult, setQueryResult] = useState<string | null>(null);
  const [timelineActorInput, setTimelineActorInput] = useState("");
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [timelineError, setTimelineError] = useState("");
  const [timelineSnapshot, setTimelineSnapshot] =
    useState<WorkflowActorSnapshot | null>(null);
  const [timelineGraph, setTimelineGraph] =
    useState<WorkflowActorGraphEnrichedSnapshot | null>(null);
  const [timelineSearch, setTimelineSearch] = useState("");
  const [timelineOnlyErrors, setTimelineOnlyErrors] = useState(false);
  const [timelineSelectedStage, setTimelineSelectedStage] = useState("");
  const [timelineItems, setTimelineItems] = useState<
    ReturnType<typeof buildTimelineRows>
  >([]);
  const [timelineRefreshTick, setTimelineRefreshTick] = useState(0);
  const [timelineSelectedKey, setTimelineSelectedKey] = useState<string | null>(
    null
  );
  const [timelineActionInput, setTimelineActionInput] = useState("");
  const [timelineActionLoading, setTimelineActionLoading] = useState(false);
  const [timelineActionError, setTimelineActionError] = useState("");
  const [timelineActionNotice, setTimelineActionNotice] = useState("");

  const [executeServiceId, setExecuteServiceId] = useState(defaultServiceId || "");
  const [executeEndpointId, setExecuteEndpointId] = useState("chat");
  const [executePrompt, setExecutePrompt] = useState("");
  const [executePayloadTypeUrl, setExecutePayloadTypeUrl] = useState("");
  const [executePayloadBase64, setExecutePayloadBase64] = useState("");
  const [executeEvents, setExecuteEvents] = useState<RuntimeEvent[]>([]);
  const [executeAssistantText, setExecuteAssistantText] = useState("");
  const [executeResponseText, setExecuteResponseText] = useState("");
  const [executeActorId, setExecuteActorId] = useState("");
  const [executeCommandId, setExecuteCommandId] = useState("");
  const [executeCorrelationId, setExecuteCorrelationId] = useState("");
  const [executeRunId, setExecuteRunId] = useState("");
  const [executeAuditSnapshot, setExecuteAuditSnapshot] =
    useState<ScopeServiceRunAuditSnapshot | null>(null);
  const [executeAuditLoading, setExecuteAuditLoading] = useState(false);
  const [executeAuditError, setExecuteAuditError] = useState("");
  const [executeLaunchContext, setExecuteLaunchContext] =
    useState<ExecuteLaunchContext | null>(null);
  const [executeStatus, setExecuteStatus] = useState<
    "idle" | "running" | "success" | "error"
  >("idle");
  const [executeError, setExecuteError] = useState("");

  const [rawMethod, setRawMethod] = useState("GET");
  const [rawPath, setRawPath] = useState("");
  const [rawBody, setRawBody] = useState("");
  const [rawLoading, setRawLoading] = useState(false);
  const [rawResult, setRawResult] = useState<{
    body: string;
    status: number;
    statusText: string;
  } | null>(null);

  const activeExecuteService =
    consoleServices.find((service) => service.serviceId === executeServiceId) ??
    consoleServices[0] ??
    null;
  const activeExecuteEndpoint =
    activeExecuteService?.endpoints.find(
      (endpoint) => endpoint.endpointId === executeEndpointId
    ) ??
    activeExecuteService?.endpoints[0] ??
    null;
  const effectiveTimelineServiceId =
    executeLaunchContext?.serviceId || defaultServiceId || executeServiceId || "";
  const effectiveTimelineActorId = (
    timelineActorInput.trim() ||
    executeActorId.trim() ||
    sessionActorId?.trim() ||
    queryActorId.trim()
  ).trim();
  const timelineRows = useMemo(
    () =>
      filterTimelineRows(timelineItems, {
        errorsOnly: timelineOnlyErrors,
        eventTypes: [],
        query: timelineSearch,
        stages: timelineSelectedStage ? [timelineSelectedStage] : [],
        stepTypes: [],
      }),
    [timelineItems, timelineOnlyErrors, timelineSearch, timelineSelectedStage]
  );
  const timelineStageOptions = useMemo(
    () =>
      [...new Set(timelineItems.map((item) => item.stage).filter(Boolean))].sort(
        (left, right) => left.localeCompare(right)
      ),
    [timelineItems]
  );
  const selectedTimelineRow = useMemo(() => {
    if (!timelineRows.length) {
      return null;
    }

    return (
      timelineRows.find((item) => item.key === timelineSelectedKey) ||
      timelineRows[0]
    );
  }, [timelineRows, timelineSelectedKey]);
  const timelineBlockingSummary = useMemo(
    () => buildTimelineBlockingSummary(timelineItems),
    [timelineItems]
  );
  const consoleFlowGroups = useMemo(
    () => [
      {
        description: "Inspect the current scope and understand runtime state.",
        flows: consoleFlows.filter((flow) => flow.group === "understand"),
        id: "understand",
        label: "Understand",
      },
      {
        description: "Run work, inspect the receipt, and act on runtime gates.",
        flows: consoleFlows.filter((flow) => flow.group === "operate"),
        id: "operate",
        label: "Operate",
      },
      {
        description: "Drop to direct API calls when you need low-level debugging.",
        flows: consoleFlows.filter((flow) => flow.group === "developer"),
        id: "developer",
        label: "Developer",
      },
    ],
    []
  );
  const activeConsoleFlow = useMemo(
    () => consoleFlows.find((flow) => flow.id === activeTab) || null,
    [activeTab]
  );

  const rawShortcuts = useMemo(
    () => [
      {
        label: "Binding",
        method: "GET",
        path: `/scopes/${scopeId}/binding`,
      },
      {
        label: "Services",
        method: "GET",
        path: `/scopes/${scopeId}/services?appId=${scopeServiceAppId}&take=20`,
      },
      {
        label: "Workflows",
        method: "GET",
        path: `/scopes/${scopeId}/workflows`,
      },
      activeExecuteService
        ? {
            label: "Runs",
            method: "GET",
            path: `/scopes/${scopeId}/services/${activeExecuteService.serviceId}/runs?take=10`,
          }
        : null,
      {
        label: "Auth Session",
        method: "GET",
        path: "/auth/me",
      },
    ].filter(Boolean) as Array<{ label: string; method: string; path: string }>,
    [activeExecuteService, scopeId]
  );

  useEffect(() => {
    if (!open) {
      return;
    }

    if (!queryActorId && sessionActorId) {
      setQueryActorId(sessionActorId);
    }
  }, [open, queryActorId, sessionActorId]);

  useEffect(() => {
    if (!consoleServices.length) {
      setExecuteServiceId("");
      return;
    }

    const preferredServiceId =
      (defaultServiceId &&
      consoleServices.some((service) => service.serviceId === defaultServiceId)
        ? defaultServiceId
        : "") ||
      consoleServices[0].serviceId;

    if (
      !executeServiceId ||
      !consoleServices.some((service) => service.serviceId === executeServiceId)
    ) {
      setExecuteServiceId(preferredServiceId);
    }
  }, [consoleServices, defaultServiceId, executeServiceId]);

  useEffect(() => {
    const defaultPath = scopeId ? `/scopes/${scopeId}/binding` : "/auth/me";
    setRawPath((current) => (current.trim() ? current : defaultPath));
  }, [scopeId]);

  useEffect(() => {
    if (!activeExecuteService) {
      setExecuteEndpointId("");
      return;
    }

    if (
      !executeEndpointId ||
      !activeExecuteService.endpoints.some(
        (endpoint) => endpoint.endpointId === executeEndpointId
      )
    ) {
      setExecuteEndpointId(activeExecuteService.endpoints[0]?.endpointId || "");
    }
  }, [activeExecuteService, executeEndpointId]);

  useEffect(() => {
    setExecutePayloadTypeUrl(activeExecuteEndpoint?.requestTypeUrl || "");
  }, [activeExecuteEndpoint?.endpointId, activeExecuteEndpoint?.requestTypeUrl]);

  useEffect(() => {
    if (!open || activeTab !== "timeline") {
      return;
    }

    if (!effectiveTimelineActorId) {
      setTimelineError("");
      setTimelineSnapshot(null);
      setTimelineGraph(null);
      setTimelineItems([]);
      setTimelineSelectedKey(null);
      return;
    }

    let cancelled = false;
    setTimelineLoading(true);
    setTimelineError("");

    void Promise.all([
      runtimeActorsApi.getActorSnapshot(effectiveTimelineActorId),
      runtimeActorsApi.getActorTimeline(effectiveTimelineActorId, { take: 40 }),
      runtimeActorsApi.getActorGraphEnriched(effectiveTimelineActorId, {
        depth: 2,
        take: 40,
      }),
    ])
      .then(([snapshot, timeline, graph]) => {
        if (cancelled) {
          return;
        }

        setTimelineSnapshot(snapshot);
        setTimelineGraph(graph);
        setTimelineItems(buildTimelineRows(timeline));
      })
      .catch((error) => {
        if (cancelled) {
          return;
        }

        setTimelineSnapshot(null);
        setTimelineGraph(null);
        setTimelineItems([]);
        setTimelineError(error instanceof Error ? error.message : String(error));
      })
      .finally(() => {
        if (!cancelled) {
          setTimelineLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [activeTab, effectiveTimelineActorId, open, timelineRefreshTick]);

  useEffect(() => {
    if (!timelineRows.length) {
      setTimelineSelectedKey(null);
      return;
    }

    setTimelineSelectedKey((current) =>
      current && timelineRows.some((item) => item.key === current)
        ? current
        : timelineRows[0].key
    );
  }, [timelineRows]);

  useEffect(() => {
    setTimelineActionError("");
    setTimelineActionInput("");
    setTimelineActionNotice("");
  }, [timelineBlockingSummary?.kind, timelineBlockingSummary?.stepId]);

  useEffect(
    () => () => {
      executeAbortRef.current?.abort();
    },
    []
  );

  const handleCopy = useCallback((value: string) => {
    void navigator.clipboard?.writeText(value);
  }, []);

  const handleQuerySubmit = useCallback(async () => {
    if (!scopeId) {
      return;
    }

    setQueryLoading(true);
    setQueryResult(null);
    try {
      let result: unknown;
      switch (queryTarget) {
        case "binding":
          result = await studioApi.getDefaultRouteTarget(scopeId);
          break;
        case "services":
          result = await scopeRuntimeApi.listServices(scopeId, {
            appId: scopeServiceAppId,
            take: 100,
          });
          break;
        case "workflows":
          result = await scopesApi.listWorkflows(scopeId);
          break;
        case "actor":
          if (!queryActorId.trim()) {
            setQueryResult(safeJson({ error: "Actor ID is required." }));
            setQueryLoading(false);
            return;
          }
          result = await runtimeActorsApi.getActorSnapshot(queryActorId.trim());
          break;
      }

      setQueryResult(safeJson(result));
    } catch (error) {
      setQueryResult(
        safeJson({
          error: error instanceof Error ? error.message : String(error),
        })
      );
    } finally {
      setQueryLoading(false);
    }
  }, [queryActorId, queryTarget, scopeId]);

  const handleExecuteSubmit = useCallback(async () => {
    if (!scopeId || !activeExecuteService || !activeExecuteEndpoint) {
      return;
    }

    executeAbortRef.current?.abort();
    const controller = new AbortController();
    executeAbortRef.current = controller;

    setExecuteAssistantText("");
    setExecuteActorId("");
    setExecuteAuditError("");
    setExecuteAuditLoading(false);
    setExecuteAuditSnapshot(null);
    setExecuteCommandId("");
    setExecuteCorrelationId("");
    setExecuteError("");
    setExecuteEvents([]);
    const launchContext: ExecuteLaunchContext = {
      endpointId: activeExecuteEndpoint.endpointId,
      endpointKind: activeExecuteEndpoint.kind,
      payloadBase64: executePayloadBase64.trim(),
      payloadTypeUrl: executePayloadTypeUrl.trim(),
      prompt: executePrompt.trim(),
      serviceId: activeExecuteService.serviceId,
    };
    setExecuteLaunchContext(launchContext);
    setExecuteResponseText("");
    setExecuteRunId("");
    setExecuteStatus("running");

    try {
      if (activeExecuteService.kind === "nyxid-chat") {
        await onEnsureNyxIdBound?.();
      }

      const isStreamingEndpoint =
        activeExecuteEndpoint.kind === "chat" ||
        activeExecuteEndpoint.endpointId.trim() === "chat";

      if (isStreamingEndpoint) {
        const accumulator = createRuntimeEventAccumulator();
        const response = await runtimeRunsApi.streamEndpoint(
          scopeId,
          {
            endpointId: activeExecuteEndpoint.endpointId,
            prompt: executePrompt,
          },
          controller.signal,
          {
            serviceId: activeExecuteService.serviceId,
          }
        );

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          applyRuntimeEvent(accumulator, event);
          setExecuteEvents([...accumulator.events]);
          setExecuteAssistantText(accumulator.assistantText);
          setExecuteActorId(accumulator.actorId);
          setExecuteCommandId(accumulator.commandId);
          setExecuteRunId(accumulator.runId);
          setExecuteError(accumulator.errorText);
        }

        setExecuteStatus(accumulator.errorText ? "error" : "success");
        return;
      }

      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: activeExecuteEndpoint.endpointId,
          payloadBase64: executePayloadBase64.trim() || undefined,
          payloadTypeUrl: executePayloadTypeUrl.trim() || undefined,
          prompt: executePrompt,
        },
        {
          serviceId: activeExecuteService.serviceId,
        }
      );
      const {
        actorId: responseActorId,
        commandId: responseCommandId,
        correlationId: responseCorrelationId,
        runId: responseRunId,
      } = extractRuntimeInvokeReceipt(response);

      setExecuteActorId(responseActorId);
      setExecuteCommandId(responseCommandId);
      setExecuteCorrelationId(responseCorrelationId);
      setExecuteRunId(responseRunId);
      setExecuteResponseText(safeJson(response));
      setExecuteStatus("success");
    } catch (error) {
      if (controller.signal.aborted) {
        setExecuteError("Execution stopped by operator.");
      } else {
        setExecuteError(error instanceof Error ? error.message : String(error));
      }
      setExecuteStatus("error");
    } finally {
      if (executeAbortRef.current === controller) {
        executeAbortRef.current = null;
      }
    }
  }, [
    activeExecuteEndpoint,
    activeExecuteService,
    executePayloadBase64,
    executePayloadTypeUrl,
    executePrompt,
    onEnsureNyxIdBound,
    scopeId,
  ]);

  const handleOpenRuns = useCallback(() => {
    if (!scopeId || !executeLaunchContext) {
      return;
    }

    const observedEvents =
      executeEvents.length > 0
        ? executeEvents
        : createObservedExecutionEvents({
            actorId: executeActorId,
            commandId: executeCommandId,
            correlationId: executeCorrelationId,
            runId: executeRunId,
          });
    const draftKey =
      observedEvents.length > 0
        ? saveObservedRunSessionPayload({
            actorId: executeActorId || undefined,
            commandId: executeCommandId || undefined,
            endpointId: executeLaunchContext.endpointId,
            endpointKind: executeLaunchContext.endpointKind as
              | "chat"
              | "command"
              | undefined,
            events: observedEvents,
            payloadBase64:
              executeLaunchContext.endpointKind !== "chat"
                ? executeLaunchContext.payloadBase64 || undefined
                : undefined,
            payloadTypeUrl:
              executeLaunchContext.endpointKind !== "chat"
                ? executeLaunchContext.payloadTypeUrl || undefined
                : undefined,
            prompt: executeLaunchContext.prompt,
            runId: executeRunId || undefined,
            scopeId,
            serviceOverrideId: executeLaunchContext.serviceId,
          })
        : "";

    history.push(
      buildRuntimeRunsHref({
        actorId: executeActorId || undefined,
        draftKey: draftKey || undefined,
        endpointId: executeLaunchContext.endpointId,
        endpointKind: executeLaunchContext.endpointKind,
        payloadBase64:
          executeLaunchContext.endpointKind !== "chat"
            ? executeLaunchContext.payloadBase64 || undefined
            : undefined,
        payloadTypeUrl:
          executeLaunchContext.endpointKind !== "chat"
            ? executeLaunchContext.payloadTypeUrl || undefined
            : undefined,
        prompt: executeLaunchContext.prompt || undefined,
        scopeId,
        serviceId: executeLaunchContext.serviceId,
      })
    );
  }, [
    executeActorId,
    executeCommandId,
    executeCorrelationId,
    executeEvents,
    executeLaunchContext,
    executeRunId,
    scopeId,
  ]);

  const handleOpenExplorer = useCallback(() => {
    if (!scopeId) {
      return;
    }

    history.push(
      buildRuntimeExplorerHref({
        actorId: effectiveTimelineActorId || undefined,
        runId: executeRunId || undefined,
        scopeId,
        serviceId: executeLaunchContext?.serviceId,
      })
    );
  }, [effectiveTimelineActorId, executeLaunchContext?.serviceId, executeRunId, scopeId]);

  const handleLoadAudit = useCallback(async () => {
    if (!scopeId || !executeLaunchContext?.serviceId || !executeRunId) {
      return;
    }

    setExecuteAuditLoading(true);
    setExecuteAuditError("");
    try {
      const snapshot = await scopeRuntimeApi.getServiceRunAudit(
        scopeId,
        executeLaunchContext.serviceId,
        executeRunId,
        {
          actorId: effectiveTimelineActorId || undefined,
        }
      );
      setExecuteAuditSnapshot(snapshot);
    } catch (error) {
      setExecuteAuditSnapshot(null);
      setExecuteAuditError(error instanceof Error ? error.message : String(error));
    } finally {
      setExecuteAuditLoading(false);
    }
  }, [
    effectiveTimelineActorId,
    executeLaunchContext?.serviceId,
    executeRunId,
    scopeId,
  ]);

  const executeAuditTimeline = executeAuditSnapshot?.audit.timeline ?? [];
  const executeAuditSteps = executeAuditSnapshot?.audit.steps ?? [];
  const executeAuditReplies = executeAuditSnapshot?.audit.roleReplies ?? [];
  const executeAuditSummary = executeAuditSnapshot?.audit.summary;
  const relatedAuditStep = useMemo(() => {
    const stepId =
      selectedTimelineRow?.stepId || timelineBlockingSummary?.stepId || "";
    if (!stepId) {
      return null;
    }

    return (
      executeAuditSteps.find((step) => step.stepId === stepId) || null
    );
  }, [executeAuditSteps, selectedTimelineRow?.stepId, timelineBlockingSummary?.stepId]);

  const handleTimelineAction = useCallback(
    async (action: "resume" | "approve" | "reject" | "signal") => {
      if (
        !scopeId ||
        !timelineBlockingSummary ||
        !effectiveTimelineActorId ||
        !executeRunId ||
        !effectiveTimelineServiceId
      ) {
        return;
      }

      setTimelineActionLoading(true);
      setTimelineActionError("");
      setTimelineActionNotice("");

      try {
        if (action === "signal") {
          const result = await runtimeRunsApi.signal(
            scopeId,
            {
              actorId: effectiveTimelineActorId,
              payload: timelineActionInput.trim() || undefined,
              runId: executeRunId,
              signalName: timelineBlockingSummary.signalName || "continue",
              stepId: timelineBlockingSummary.stepId,
            },
            {
              serviceId: effectiveTimelineServiceId,
            }
          );

          const content = `Signal ${
            timelineBlockingSummary.signalName || "continue"
          } submitted.`;
          setTimelineActionNotice(content);
          onTimelineActionResult?.({
            action,
            actorId: result.actorId || effectiveTimelineActorId,
            commandId: result.commandId,
            content,
            kind: timelineBlockingSummary.kind,
            runId: result.runId || executeRunId,
            serviceId: effectiveTimelineServiceId,
            signalName: timelineBlockingSummary.signalName,
            stepId: timelineBlockingSummary.stepId,
            success: true,
          });
        } else {
          const result = await runtimeRunsApi.resume(
            scopeId,
            {
              actorId: effectiveTimelineActorId,
              approved: action !== "reject",
              runId: executeRunId,
              stepId: timelineBlockingSummary.stepId,
              userInput: timelineActionInput.trim() || undefined,
            },
            {
              serviceId: effectiveTimelineServiceId,
            }
          );

          const content =
            action === "reject"
              ? `Rejection submitted for ${timelineBlockingSummary.stepId}.`
              : timelineBlockingSummary.kind === "human_approval"
                ? `Approval submitted for ${timelineBlockingSummary.stepId}.`
                : `Input submitted for ${timelineBlockingSummary.stepId}.`;
          setTimelineActionNotice(content);
          onTimelineActionResult?.({
            action,
            actorId: result.actorId || effectiveTimelineActorId,
            commandId: result.commandId,
            content,
            kind: timelineBlockingSummary.kind,
            runId: result.runId || executeRunId,
            serviceId: effectiveTimelineServiceId,
            signalName: timelineBlockingSummary.signalName,
            stepId: timelineBlockingSummary.stepId,
            success: true,
          });
        }

        setTimelineActionInput("");
        setTimelineRefreshTick((current) => current + 1);
        if (executeAuditSnapshot) {
          void handleLoadAudit();
        }
      } catch (error) {
        const errorMessage = error instanceof Error ? error.message : String(error);
        setTimelineActionError(errorMessage);
        onTimelineActionResult?.({
          action,
          actorId: effectiveTimelineActorId,
          content: errorMessage,
          error: errorMessage,
          kind: timelineBlockingSummary.kind,
          runId: executeRunId,
          serviceId: effectiveTimelineServiceId,
          signalName: timelineBlockingSummary.signalName,
          stepId: timelineBlockingSummary.stepId,
          success: false,
        });
      } finally {
        setTimelineActionLoading(false);
      }
    },
    [
      effectiveTimelineActorId,
      effectiveTimelineServiceId,
      executeAuditSnapshot,
      executeRunId,
      handleLoadAudit,
      onTimelineActionResult,
      scopeId,
      timelineActionInput,
      timelineBlockingSummary,
    ]
  );

  const handleRawSubmit = useCallback(async () => {
    const normalizedPath = rawPath.trim();
    if (!normalizedPath) {
      return;
    }

    setRawLoading(true);
    setRawResult(null);

    try {
      const response = await authFetch(
        `/api${normalizedPath.startsWith("/") ? "" : "/"}${normalizedPath}`,
        {
          body:
            rawMethod !== "GET" && rawBody.trim().length > 0
              ? rawBody
              : undefined,
          headers:
            rawMethod !== "GET" && rawBody.trim().length > 0
              ? {
                  "Content-Type": "application/json",
                }
              : undefined,
          method: rawMethod,
        }
      );

      const contentType = response.headers.get("content-type") || "";
      const body = contentType.includes("json")
        ? safeJson(await response.json())
        : await response.text();

      setRawResult({
        body,
        status: response.status,
        statusText: response.statusText,
      });
    } catch (error) {
      setRawResult({
        body: error instanceof Error ? error.message : String(error),
        status: 0,
        statusText: "Network Error",
      });
    } finally {
      setRawLoading(false);
    }
  }, [rawBody, rawMethod, rawPath]);

  return (
    <AevatarContextDrawer
      onClose={onClose}
      open={open}
      subtitle="Inspect scope state, invoke endpoints, or hit raw API paths without leaving chat."
      title="Advanced Console"
      width={960}
    >
      {!scopeId ? (
        <Alert
          description="Open a scoped chat first so the console has a project context."
          showIcon
          title="No scope is currently active."
          type="warning"
        />
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={drawerSectionStyle}>
            <Typography.Text strong>Choose a task</Typography.Text>
            <Typography.Text type="secondary">
              Advanced Console keeps runtime inspection, operator actions, and
              developer tooling in one drawer. Start from the task you are
              trying to complete.
            </Typography.Text>
            <div
              style={{
                background: "#fafaf8",
                border: "1px solid #ece8e1",
                borderRadius: 12,
                color: "#57534e",
                fontSize: 12,
                lineHeight: 1.6,
                padding: "10px 12px",
              }}
            >
              Suggested path: start with <strong>Query</strong> to orient the
              scope, move to <strong>Execute</strong> when you are ready to act,
              then use <strong>Timeline</strong> if the run needs evidence or
              operator input. Keep <strong>Raw API</strong> for protocol-level
              debugging.
            </div>

            <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
              {consoleFlowGroups.map((group) => (
                <div
                  key={group.id}
                  style={{ display: "flex", flexDirection: "column", gap: 10 }}
                >
                  <div>
                    <div
                      style={{
                        color: "#111827",
                        fontSize: 13,
                        fontWeight: 700,
                        marginBottom: 4,
                      }}
                    >
                      {group.label}
                    </div>
                    <div style={{ color: "#6b7280", fontSize: 12, lineHeight: 1.5 }}>
                      {group.description}
                    </div>
                  </div>
                  <div
                    style={{
                      display: "grid",
                      gap: 10,
                      gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))",
                    }}
                  >
                    {group.flows.map((flow) => {
                      const active = activeTab === flow.id;
                      return (
                        <button
                          aria-label={flow.label}
                          aria-pressed={active}
                          className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                          key={flow.id}
                          onClick={() => setActiveTab(flow.id)}
                          style={{
                            background: active ? "#f8fafc" : "#ffffff",
                            border: `1px solid ${active ? "#bfdbfe" : "#e7e5e4"}`,
                            borderRadius: 14,
                            cursor: "pointer",
                            display: "flex",
                            flexDirection: "column",
                            gap: 8,
                            minHeight: 110,
                            padding: 14,
                            textAlign: "left",
                          }}
                          type="button"
                        >
                          <div
                            style={{
                              alignItems: "center",
                              display: "flex",
                              gap: 8,
                              justifyContent: "space-between",
                            }}
                          >
                            <div
                              style={{
                                alignItems: "center",
                                display: "flex",
                                flexWrap: "wrap",
                                gap: 8,
                              }}
                            >
                              <span
                                style={{
                                  color: "#111827",
                                  fontSize: 14,
                                  fontWeight: 700,
                                }}
                              >
                                {flow.label}
                              </span>
                              {flow.badge ? (
                                <span
                                  style={{
                                    background:
                                      flow.priority === "primary"
                                        ? "#fef3c7"
                                        : "#f5f5f4",
                                    borderRadius: 999,
                                    color:
                                      flow.priority === "primary"
                                        ? "#92400e"
                                        : "#57534e",
                                    fontSize: 10,
                                    fontWeight: 700,
                                    letterSpacing: "0.04em",
                                    padding: "3px 8px",
                                    textTransform: "uppercase",
                                  }}
                                >
                                  {flow.badge}
                                </span>
                              ) : null}
                            </div>
                            {active ? (
                              <span
                                style={{
                                  background: "#eff6ff",
                                  borderRadius: 999,
                                  color: "#2563eb",
                                  fontSize: 10,
                                  fontWeight: 700,
                                  letterSpacing: "0.08em",
                                  padding: "3px 8px",
                                  textTransform: "uppercase",
                                }}
                              >
                                Active
                              </span>
                            ) : null}
                          </div>
                          <div
                            style={{
                              color: "#6b7280",
                              fontSize: 12,
                              lineHeight: 1.6,
                            }}
                          >
                            {flow.description}
                          </div>
                        </button>
                      );
                    })}
                  </div>
                </div>
              ))}
            </div>
          </div>

          {activeConsoleFlow ? (
            <Alert
              description={activeConsoleFlow.description}
              message={`Current task: ${activeConsoleFlow.label}`}
              showIcon
              type="info"
            />
          ) : null}

          {activeTab === "query" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>Query Scope State</Typography.Text>
                <div
                  style={{
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
                  }}
                >
                  {queryTargets.map((target) => (
                    <button
                      aria-pressed={queryTarget === target.id}
                      className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                      key={target.id}
                      onClick={() => {
                        setQueryTarget(target.id);
                        setQueryResult(null);
                      }}
                      style={{
                        background:
                          queryTarget === target.id ? "#f5f5f4" : "#ffffff",
                        border:
                          queryTarget === target.id
                            ? "1px solid #111827"
                            : "1px solid #e7e5e4",
                        borderRadius: 12,
                        cursor: "pointer",
                        minHeight: 88,
                        padding: 14,
                        textAlign: "left",
                      }}
                      type="button"
                    >
                      <div
                        style={{
                          color: "#111827",
                          fontSize: 13,
                          fontWeight: 700,
                          marginBottom: 6,
                        }}
                      >
                        {target.label}
                      </div>
                      <div style={{ color: "#6b7280", fontSize: 12, lineHeight: 1.5 }}>
                        {target.description}
                      </div>
                    </button>
                  ))}
                </div>

                {queryTarget === "actor" ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>Actor ID</span>
                    <input
                      aria-label="Advanced query actor ID"
                      onChange={(event) => setQueryActorId(event.target.value)}
                      onKeyDown={(event) => {
                        if (event.key === "Enter") {
                          void handleQuerySubmit();
                        }
                      }}
                      placeholder="actor://..."
                      style={{ ...inputStyle, fontFamily: monoFontFamily }}
                      value={queryActorId}
                    />
                  </div>
                ) : null}

                <div>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={
                      queryLoading ||
                      (queryTarget === "actor" && !queryActorId.trim())
                    }
                    onClick={() => void handleQuerySubmit()}
                    style={actionButtonStyle(
                      "primary",
                      queryLoading ||
                        (queryTarget === "actor" && !queryActorId.trim())
                    )}
                    type="button"
                  >
                    {queryLoading
                      ? "Loading..."
                      : `Query ${
                          queryTargets.find((target) => target.id === queryTarget)
                            ?.label || "Scope"
                        }`}
                  </button>
                </div>
              </div>

              {queryResult
                ? createResultPanel("Query Result", queryResult, () =>
                    handleCopy(queryResult)
                  )
                : null}
            </div>
          ) : null}

          {activeTab === "execute" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>Execute Service Endpoint</Typography.Text>
                <div style={{ display: "grid", gap: 12 }}>
                  <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>Service</span>
                    <select
                      aria-label="Advanced execute service"
                      onChange={(event) => setExecuteServiceId(event.target.value)}
                      style={selectStyle}
                      value={activeExecuteService?.serviceId || ""}
                    >
                      {consoleServices.map((service) => (
                        <option key={service.serviceId} value={service.serviceId}>
                          {service.displayName} ({service.kind})
                        </option>
                      ))}
                    </select>
                  </label>

                  <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>Endpoint</span>
                    <select
                      aria-label="Advanced execute endpoint"
                      onChange={(event) => setExecuteEndpointId(event.target.value)}
                      style={selectStyle}
                      value={activeExecuteEndpoint?.endpointId || ""}
                    >
                      {(activeExecuteService?.endpoints ?? []).map((endpoint) => (
                        <option key={endpoint.endpointId} value={endpoint.endpointId}>
                          {endpoint.endpointId} ({endpoint.kind})
                        </option>
                      ))}
                    </select>
                  </label>

                  <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>Prompt</span>
                    <textarea
                      aria-label="Advanced execute prompt"
                      onChange={(event) => setExecutePrompt(event.target.value)}
                      placeholder="Describe the call you want to make."
                      style={textareaStyle}
                      value={executePrompt}
                    />
                  </label>

                  {activeExecuteEndpoint &&
                  activeExecuteEndpoint.kind !== "chat" ? (
                    <>
                      <label
                        style={{ display: "flex", flexDirection: "column", gap: 8 }}
                      >
                        <span style={fieldLabelStyle}>Payload Type URL</span>
                        <input
                          aria-label="Advanced execute payload type URL"
                          onChange={(event) =>
                            setExecutePayloadTypeUrl(event.target.value)
                          }
                          placeholder="type.googleapis.com/..."
                          style={{ ...inputStyle, fontFamily: monoFontFamily }}
                          value={executePayloadTypeUrl}
                        />
                      </label>
                      <label
                        style={{ display: "flex", flexDirection: "column", gap: 8 }}
                      >
                        <span style={fieldLabelStyle}>Payload Base64</span>
                        <textarea
                          aria-label="Advanced execute payload base64"
                          onChange={(event) =>
                            setExecutePayloadBase64(event.target.value)
                          }
                          placeholder="Optional protobuf payload in base64."
                          style={textareaStyle}
                          value={executePayloadBase64}
                        />
                      </label>
                    </>
                  ) : null}

                  <Space wrap>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={
                        executeStatus === "running" ||
                        !activeExecuteService ||
                        !activeExecuteEndpoint
                      }
                      onClick={() => void handleExecuteSubmit()}
                      style={actionButtonStyle(
                        "primary",
                        executeStatus === "running" ||
                          !activeExecuteService ||
                          !activeExecuteEndpoint
                      )}
                      type="button"
                    >
                      {executeStatus === "running" ? "Running..." : "Run"}
                    </button>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={executeStatus !== "running"}
                      onClick={() => executeAbortRef.current?.abort()}
                      style={actionButtonStyle(
                        "secondary",
                        executeStatus !== "running"
                      )}
                      type="button"
                    >
                      Stop
                    </button>
                  </Space>
                </div>
              </div>

              {executeError ? (
                <Alert showIcon title={executeError} type="error" />
              ) : null}

              {executeActorId || executeCommandId || executeRunId ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Execution Metadata</Typography.Text>
                  <Space wrap>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      onClick={handleOpenRuns}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      Open Runs
                    </button>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={!executeActorId && !executeRunId}
                      onClick={handleOpenExplorer}
                      style={actionButtonStyle(
                        "secondary",
                        !executeActorId && !executeRunId
                      )}
                      type="button"
                    >
                      Open Explorer
                    </button>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      disabled={!executeRunId || executeAuditLoading}
                      onClick={() => void handleLoadAudit()}
                      style={actionButtonStyle(
                        "secondary",
                        !executeRunId || executeAuditLoading
                      )}
                      type="button"
                    >
                      {executeAuditLoading
                        ? "Loading Audit..."
                        : executeAuditSnapshot
                          ? "Refresh Audit"
                          : "Load Audit"}
                    </button>
                  </Space>
                  <div
                    style={{
                      color: "#4b5563",
                      display: "grid",
                      gap: 8,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div>
                      <div style={fieldLabelStyle}>Actor</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeActorId || "Unavailable"}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>Command</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeCommandId || "Unavailable"}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>Run</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeRunId || "Unavailable"}
                      </div>
                    </div>
                  </div>
                </div>
              ) : null}

              {executeAuditError ? (
                <Alert showIcon title={executeAuditError} type="error" />
              ) : null}

              {executeAuditSnapshot ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Run Audit</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Completion</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSnapshot.audit.completionStatus}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Duration</Typography.Text>
                      <Typography.Text strong>
                        {Math.round(executeAuditSnapshot.audit.durationMs)} ms
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Steps</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSummary?.completedSteps ?? 0}/
                        {executeAuditSummary?.totalSteps ?? 0}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Role replies</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSummary?.roleReplyCount ?? 0}
                      </Typography.Text>
                    </div>
                  </div>

                  {executeAuditSnapshot.audit.input ? (
                    createResultPanel(
                      "Audit Input",
                      executeAuditSnapshot.audit.input,
                      () => handleCopy(executeAuditSnapshot.audit.input)
                    )
                  ) : null}

                  {executeAuditSnapshot.audit.finalOutput ? (
                    <Alert
                      description={executeAuditSnapshot.audit.finalOutput}
                      showIcon
                      title="Final output"
                      type="success"
                    />
                  ) : null}

                  {executeAuditSnapshot.audit.finalError ? (
                    <Alert
                      description={executeAuditSnapshot.audit.finalError}
                      showIcon
                      title="Final error"
                      type="error"
                    />
                  ) : null}

                  <div
                    style={{
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>Timeline Highlights</Typography.Text>
                      {executeAuditTimeline.length > 0 ? (
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 10,
                          }}
                        >
                          {executeAuditTimeline
                            .slice(0, 8)
                            .map((event, index) =>
                              renderAuditPreviewCard(
                                event.stage || event.eventType || "event",
                                event.message || "No message",
                                event.timestamp,
                                String(index)
                              )
                            )}
                        </div>
                      ) : (
                        <Empty
                          description="No timeline events were captured."
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </div>

                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>Step Highlights</Typography.Text>
                      {executeAuditSteps.length > 0 ? (
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 10,
                          }}
                        >
                          {executeAuditSteps.slice(0, 6).map((step) =>
                            renderAuditPreviewCard(
                              step.stepId,
                              `${step.stepType || "step"} · ${
                                step.targetRole || "unassigned"
                              }`,
                              step.completedAt || step.requestedAt,
                              step.stepId
                            )
                          )}
                        </div>
                      ) : (
                        <Empty
                          description="No step audit records were captured."
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </div>
                  </div>

                  <div style={drawerSectionStyle}>
                    <Typography.Text strong>Reply Highlights</Typography.Text>
                    {executeAuditReplies.length > 0 ? (
                      <div
                        style={{
                          display: "flex",
                          flexDirection: "column",
                          gap: 10,
                        }}
                      >
                        {executeAuditReplies.slice(0, 4).map((reply, index) =>
                          renderAuditPreviewCard(
                            reply.roleId || `reply-${index + 1}`,
                            reply.content || "No content",
                            reply.timestamp,
                            String(index)
                          )
                        )}
                      </div>
                    ) : (
                      <Empty
                        description="No role replies were captured."
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                  </div>
                </div>
              ) : null}

              {executeAssistantText
                ? createResultPanel("Streaming Output", executeAssistantText, () =>
                    handleCopy(executeAssistantText)
                  )
                : null}

              {executeResponseText
                ? createResultPanel("Invoke Response", executeResponseText, () =>
                    handleCopy(executeResponseText)
                  )
                : null}

              {executeEvents.length > 0 ? (
                <div style={drawerSectionStyle}>
                  <DebugPanel events={executeEvents} />
                </div>
              ) : null}
            </div>
          ) : null}

          {activeTab === "timeline" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>Actor Timeline</Typography.Text>
                <Typography.Text type="secondary">
                  Inspect the current actor snapshot, recent runtime stages, and
                  any blocking gate without leaving chat.
                </Typography.Text>

                <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                  <span style={fieldLabelStyle}>Actor ID</span>
                  <input
                    aria-label="Advanced timeline actor ID"
                    onChange={(event) => setTimelineActorInput(event.target.value)}
                    placeholder={
                      executeActorId || sessionActorId || "actor://..."
                    }
                    style={{ ...inputStyle, fontFamily: monoFontFamily }}
                    value={timelineActorInput}
                  />
                </label>

                <Space wrap>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={!effectiveTimelineActorId || timelineLoading}
                    onClick={() => setTimelineRefreshTick((current) => current + 1)}
                    style={actionButtonStyle(
                      "primary",
                      !effectiveTimelineActorId || timelineLoading
                    )}
                    type="button"
                  >
                    {timelineLoading ? "Refreshing..." : "Refresh Timeline"}
                  </button>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={!executeRunId || executeAuditLoading}
                    onClick={() => void handleLoadAudit()}
                    style={actionButtonStyle(
                      "secondary",
                      !executeRunId || executeAuditLoading
                    )}
                    type="button"
                  >
                    {executeAuditLoading
                      ? "Loading Audit..."
                      : executeAuditSnapshot
                        ? "Refresh Audit"
                        : "Load Audit for Timeline"}
                  </button>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={!effectiveTimelineActorId}
                    onClick={handleOpenExplorer}
                    style={actionButtonStyle(
                      "secondary",
                      !effectiveTimelineActorId
                    )}
                    type="button"
                  >
                    Open Explorer
                  </button>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={!executeLaunchContext}
                    onClick={handleOpenRuns}
                    style={actionButtonStyle("secondary", !executeLaunchContext)}
                    type="button"
                  >
                    Open Runs
                  </button>
                </Space>

                {!effectiveTimelineActorId ? (
                  <Alert
                    description="Run a service endpoint or provide an actor ID to inspect runtime state."
                    showIcon
                    title="No actor is currently selected."
                    type="info"
                  />
                ) : null}

                {effectiveTimelineActorId ? (
                  <div
                    style={{
                      color: "#4b5563",
                      display: "grid",
                      gap: 8,
                      gridTemplateColumns:
                        "repeat(auto-fit, minmax(220px, 1fr))",
                    }}
                  >
                    <div>
                      <div style={fieldLabelStyle}>Effective actor</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {effectiveTimelineActorId}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>Run</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeRunId || "Unavailable"}
                      </div>
                    </div>
                  </div>
                ) : null}
              </div>

              {timelineError ? (
                <Alert showIcon title={timelineError} type="error" />
              ) : null}

              {timelineSnapshot ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Snapshot</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns:
                        "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Workflow</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.workflowName || "n/a"}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Completion</Typography.Text>
                      <Typography.Text strong>
                        {describeActorCompletionStatus(timelineSnapshot)}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">State version</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.stateVersion}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Completed steps</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.completedSteps}/
                        {timelineSnapshot.totalSteps}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Role replies</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.roleReplyCount}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Last update</Typography.Text>
                      <Typography.Text strong>
                        {formatDateTime(timelineSnapshot.lastUpdatedAt)}
                      </Typography.Text>
                    </div>
                  </div>
                </div>
              ) : null}

              {timelineGraph ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Topology Digest</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns:
                        "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Nodes</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.nodes.length}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Edges</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.edges.length}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">Root node</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.rootNodeId || "Unavailable"}
                      </Typography.Text>
                    </div>
                  </div>
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 8,
                    }}
                  >
                    {(timelineGraph.subgraph.nodes ?? []).slice(0, 6).map((node) => (
                      <div
                        key={node.nodeId}
                        style={{
                          alignItems: "center",
                          border: "1px solid #e7e5e4",
                          borderRadius: 12,
                          display: "flex",
                          gap: 8,
                          justifyContent: "space-between",
                          padding: 12,
                        }}
                      >
                        <Typography.Text strong>{node.nodeId}</Typography.Text>
                        <Typography.Text type="secondary">
                          {node.nodeType || "node"}
                        </Typography.Text>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}

              {timelineBlockingSummary ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Blocking State</Typography.Text>
                  <div
                    style={{
                      background: "#fffbeb",
                      border: "1px solid #fde68a",
                      borderRadius: 14,
                      display: "flex",
                      flexDirection: "column",
                      gap: 12,
                      padding: 14,
                    }}
                  >
                    <div
                      style={{
                        alignItems: "flex-start",
                        display: "flex",
                        flexWrap: "wrap",
                        gap: 10,
                        justifyContent: "space-between",
                      }}
                    >
                      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                        <span
                          style={{
                            color: "#92400e",
                            fontSize: 11,
                            fontWeight: 700,
                            letterSpacing: "0.08em",
                            textTransform: "uppercase",
                          }}
                        >
                          {timelineBlockingSummary.kind === "wait_signal"
                            ? "Waiting on signal"
                            : timelineBlockingSummary.kind === "human_approval"
                              ? "Approval required"
                              : "Input required"}
                        </span>
                        <Typography.Text strong style={{ fontSize: 16 }}>
                          {timelineBlockingSummary.title}
                        </Typography.Text>
                      </div>
                      <Space wrap size={[8, 8]}>
                        <span
                          style={{
                            background: "#fef3c7",
                            borderRadius: 999,
                            color: "#92400e",
                            fontFamily: monoFontFamily,
                            fontSize: 11,
                            fontWeight: 700,
                            padding: "4px 8px",
                          }}
                        >
                          {timelineBlockingSummary.stepId}
                        </span>
                        {timelineBlockingSummary.signalName ? (
                          <span
                            style={{
                              background: "#ffffff",
                              border: "1px solid #fde68a",
                              borderRadius: 999,
                              color: "#92400e",
                              fontSize: 11,
                              fontWeight: 600,
                              padding: "4px 8px",
                            }}
                          >
                            Signal {timelineBlockingSummary.signalName}
                          </span>
                        ) : null}
                        {timelineBlockingSummary.timeoutLabel ? (
                          <span
                            style={{
                              background: "#ffffff",
                              border: "1px solid #fde68a",
                              borderRadius: 999,
                              color: "#92400e",
                              fontSize: 11,
                              fontWeight: 600,
                              padding: "4px 8px",
                            }}
                          >
                            {timelineBlockingSummary.timeoutLabel}
                          </span>
                        ) : null}
                      </Space>
                    </div>

                    <Alert
                      description={
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 8,
                          }}
                        >
                          <span>{timelineBlockingSummary.summary}</span>
                          <span>{timelineBlockingSummary.prompt}</span>
                        </div>
                      }
                      message="Current runtime gate"
                      showIcon
                      type="warning"
                    />

                    <div
                      style={{
                        display: "grid",
                        gap: 10,
                        gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                      }}
                    >
                      <div
                        style={{
                          background: "#ffffff",
                          border: "1px solid #fde68a",
                          borderRadius: 12,
                          padding: 12,
                        }}
                      >
                        <div style={fieldLabelStyle}>Recommended next step</div>
                        <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
                          {timelineBlockingSummary.kind === "wait_signal"
                            ? "Send the signal payload that the runtime is waiting for."
                            : timelineBlockingSummary.kind === "human_approval"
                              ? "Review the gate and approve or reject it."
                              : "Provide the missing value, then resume the run."}
                        </div>
                      </div>
                      <div
                        style={{
                          background: "#ffffff",
                          border: "1px solid #fde68a",
                          borderRadius: 12,
                          padding: 12,
                        }}
                      >
                        <div style={fieldLabelStyle}>Action context</div>
                        <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
                          {timelineBlockingSummary.kind === "wait_signal"
                            ? "Signal payload is optional unless your workflow expects a value."
                            : timelineBlockingSummary.kind === "human_approval"
                              ? "Approval notes are optional and will be sent with the decision."
                              : "Input is required before the workflow can continue."}
                        </div>
                      </div>
                    </div>
                  </div>
                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>
                      {timelineBlockingSummary.kind === "wait_signal"
                        ? "Signal payload"
                        : timelineBlockingSummary.kind === "human_approval"
                          ? "Approval note"
                          : "Operator input"}
                    </span>
                    <textarea
                      aria-label="Advanced timeline action input"
                      disabled={timelineActionLoading}
                      onChange={(event) =>
                        setTimelineActionInput(event.target.value)
                      }
                      placeholder={
                        timelineBlockingSummary.kind === "wait_signal"
                          ? "Optional signal payload"
                          : timelineBlockingSummary.kind === "human_approval"
                            ? "Optional approval note"
                            : "Provide the requested input"
                      }
                      style={textareaStyle}
                      value={timelineActionInput}
                    />
                  </label>

                  <Space wrap>
                    {timelineBlockingSummary.kind === "wait_signal" ? (
                      <button
                        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                        disabled={
                          timelineActionLoading ||
                          !executeRunId ||
                          !effectiveTimelineServiceId
                        }
                        onClick={() => void handleTimelineAction("signal")}
                        style={actionButtonStyle(
                          "primary",
                          timelineActionLoading ||
                            !executeRunId ||
                            !effectiveTimelineServiceId
                        )}
                        type="button"
                      >
                        {timelineActionLoading ? "Sending..." : "Send Signal"}
                      </button>
                    ) : (
                      <>
                        <button
                          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                          disabled={
                            timelineActionLoading ||
                            !executeRunId ||
                            !effectiveTimelineServiceId ||
                            (timelineBlockingSummary.kind === "human_input" &&
                              !timelineActionInput.trim())
                          }
                          onClick={() =>
                            void handleTimelineAction(
                              timelineBlockingSummary.kind === "human_approval"
                                ? "approve"
                                : "resume"
                            )
                          }
                          style={actionButtonStyle(
                            "primary",
                            timelineActionLoading ||
                              !executeRunId ||
                              !effectiveTimelineServiceId ||
                              (timelineBlockingSummary.kind === "human_input" &&
                                !timelineActionInput.trim())
                          )}
                          type="button"
                        >
                          {timelineActionLoading
                            ? "Applying..."
                            : timelineBlockingSummary.kind === "human_approval"
                              ? "Approve"
                              : "Resume"}
                        </button>
                        {timelineBlockingSummary.kind === "human_approval" ? (
                          <button
                            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                            disabled={
                              timelineActionLoading ||
                              !executeRunId ||
                              !effectiveTimelineServiceId
                            }
                            onClick={() => void handleTimelineAction("reject")}
                            style={actionButtonStyle(
                              "secondary",
                              timelineActionLoading ||
                                !executeRunId ||
                                !effectiveTimelineServiceId
                            )}
                            type="button"
                          >
                            Reject
                          </button>
                        ) : null}
                      </>
                    )}
                  </Space>

                  {!executeRunId || !effectiveTimelineServiceId ? (
                    <Typography.Text type="secondary">
                      Run actions become available after the console has a run
                      ID and service context.
                    </Typography.Text>
                  ) : null}

                  {timelineActionError ? (
                    <Alert showIcon title={timelineActionError} type="error" />
                  ) : null}

                  {timelineActionNotice ? (
                    <Alert showIcon title={timelineActionNotice} type="success" />
                  ) : null}
                </div>
              ) : null}

              <div style={drawerSectionStyle}>
                <Typography.Text strong>Timeline Filters</Typography.Text>
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns:
                      "repeat(auto-fit, minmax(180px, 1fr))",
                  }}
                >
                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>Search</span>
                    <input
                      aria-label="Advanced timeline search"
                      onChange={(event) => setTimelineSearch(event.target.value)}
                      placeholder="Filter by stage, step, message, or agent"
                      style={inputStyle}
                      value={timelineSearch}
                    />
                  </label>

                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>Stage</span>
                    <select
                      aria-label="Advanced timeline stage"
                      onChange={(event) =>
                        setTimelineSelectedStage(event.target.value)
                      }
                      style={selectStyle}
                      value={timelineSelectedStage}
                    >
                      <option value="">All stages</option>
                      {timelineStageOptions.map((stage) => (
                        <option key={stage} value={stage}>
                          {stage}
                        </option>
                      ))}
                    </select>
                  </label>

                  <label
                    style={{
                      alignItems: "center",
                      display: "flex",
                      gap: 8,
                      paddingTop: 28,
                    }}
                  >
                    <input
                      aria-label="Advanced timeline errors only"
                      checked={timelineOnlyErrors}
                      onChange={(event) =>
                        setTimelineOnlyErrors(event.target.checked)
                      }
                      type="checkbox"
                    />
                    <span style={{ color: "#4b5563", fontSize: 13 }}>
                      Errors only
                    </span>
                  </label>
                </div>
              </div>

              <div
                style={{
                  display: "grid",
                  gap: 16,
                  gridTemplateColumns: "minmax(0, 1.4fr) minmax(280px, 1fr)",
                }}
              >
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>Timeline Events</Typography.Text>
                  {timelineLoading && !timelineRows.length ? (
                    <Alert
                      description="Loading the latest actor timeline."
                      showIcon
                      title="Fetching runtime evidence"
                      type="info"
                    />
                  ) : timelineRows.length > 0 ? (
                    <div
                      style={{
                        display: "flex",
                        flexDirection: "column",
                        gap: 10,
                        maxHeight: 480,
                        overflow: "auto",
                      }}
                    >
                      {timelineRows.map((row) => {
                        const tone = timelineStatusTone(row.timelineStatus);
                        const isSelected = row.key === selectedTimelineRow?.key;
                        const hasAuditMatch = executeAuditSteps.some(
                          (step) => step.stepId === row.stepId
                        );

                        return (
                          <button
                            className={AEVATAR_PRESSABLE_CARD_CLASS}
                            key={row.key}
                            onClick={() => setTimelineSelectedKey(row.key)}
                            style={{
                              background: isSelected ? "#faf5ff" : "#ffffff",
                              border: `1px solid ${
                                isSelected ? "#c4b5fd" : "#e7e5e4"
                              }`,
                              borderRadius: 12,
                              cursor: "pointer",
                              padding: 12,
                              textAlign: "left",
                            }}
                            type="button"
                          >
                            <div
                              style={{
                                alignItems: "center",
                                display: "flex",
                                flexWrap: "wrap",
                                gap: 8,
                                marginBottom: 8,
                              }}
                            >
                              <span
                                style={{
                                  background: tone.background,
                                  borderRadius: 999,
                                  color: tone.color,
                                  fontSize: 11,
                                  fontWeight: 700,
                                  padding: "3px 8px",
                                }}
                              >
                                {row.stage || row.eventType || "event"}
                              </span>
                              {row.stepId ? (
                                <span
                                  style={{
                                    background: "#f5f5f4",
                                    borderRadius: 999,
                                    color: "#57534e",
                                    fontSize: 11,
                                    padding: "3px 8px",
                                  }}
                                >
                                  {row.stepId}
                                </span>
                              ) : null}
                              {hasAuditMatch ? (
                                <span
                                  style={{
                                    background: "#eff6ff",
                                    borderRadius: 999,
                                    color: "#2563eb",
                                    fontSize: 11,
                                    fontWeight: 700,
                                    padding: "3px 8px",
                                  }}
                                >
                                  Audit linked
                                </span>
                              ) : null}
                            </div>
                            <div
                              style={{
                                color: "#111827",
                                fontSize: 13,
                                fontWeight: 600,
                                marginBottom: 6,
                              }}
                            >
                              {row.message || "No message"}
                            </div>
                            <div
                              style={{
                                color: "#6b7280",
                                fontSize: 12,
                                lineHeight: 1.6,
                              }}
                            >
                              {row.dataSummary || "No structured data"}
                            </div>
                            <div
                              style={{
                                color: "#9ca3af",
                                fontFamily: monoFontFamily,
                                fontSize: 11,
                                marginTop: 8,
                              }}
                            >
                              {formatDateTime(row.timestamp)}
                              {row.stepType ? ` · ${row.stepType}` : ""}
                              {row.agentId ? ` · ${row.agentId}` : ""}
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  ) : (
                    <Empty
                      description="No timeline items matched the current filters."
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                    />
                  )}
                </div>

                <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                  {selectedTimelineRow ? (
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>Selected Event</Typography.Text>
                      <div
                        style={{
                          color: "#4b5563",
                          display: "flex",
                          flexDirection: "column",
                          gap: 8,
                        }}
                      >
                        <div>
                          <div style={fieldLabelStyle}>Stage</div>
                          <div>{selectedTimelineRow.stage || "n/a"}</div>
                        </div>
                        <div>
                          <div style={fieldLabelStyle}>Message</div>
                          <div>{selectedTimelineRow.message || "No message"}</div>
                        </div>
                        <div>
                          <div style={fieldLabelStyle}>Timestamp</div>
                          <div>{formatDateTime(selectedTimelineRow.timestamp)}</div>
                        </div>
                      </div>
                      <pre style={monoBlockStyle}>
                        {safeJson(selectedTimelineRow.data)}
                      </pre>
                    </div>
                  ) : null}

                  {relatedAuditStep ? (
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>Related Audit Step</Typography.Text>
                      <div
                        style={{
                          display: "flex",
                          flexDirection: "column",
                          gap: 8,
                        }}
                      >
                        <Typography.Text strong>{relatedAuditStep.stepId}</Typography.Text>
                        <Typography.Text type="secondary">
                          {relatedAuditStep.stepType || "step"} ·{" "}
                          {relatedAuditStep.targetRole || "unassigned"}
                        </Typography.Text>
                        {relatedAuditStep.outputPreview ? (
                          <Alert
                            description={relatedAuditStep.outputPreview}
                            showIcon
                            title="Output preview"
                            type="success"
                          />
                        ) : null}
                        {relatedAuditStep.suspensionPrompt ? (
                          <Alert
                            description={relatedAuditStep.suspensionPrompt}
                            showIcon
                            title="Suspension prompt"
                            type="warning"
                          />
                        ) : null}
                      </div>
                    </div>
                  ) : executeRunId && !executeAuditSnapshot ? (
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>Related Audit Step</Typography.Text>
                      <Empty
                        description="Load the run audit to correlate timeline events with structured step details."
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    </div>
                  ) : null}
                </div>
              </div>
            </div>
          ) : null}

          {activeTab === "raw" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>Raw API Console</Typography.Text>
                <Space wrap>
                  {rawShortcuts.map((shortcut) => (
                    <button
                      className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                      key={`${shortcut.method}-${shortcut.path}`}
                      onClick={() => {
                        setRawMethod(shortcut.method);
                        setRawPath(shortcut.path);
                      }}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      {shortcut.label}
                    </button>
                  ))}
                </Space>

                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "120px minmax(0, 1fr)",
                  }}
                >
                  <select
                    aria-label="Advanced raw method"
                    onChange={(event) => setRawMethod(event.target.value)}
                    style={selectStyle}
                    value={rawMethod}
                  >
                    {["GET", "POST", "PUT", "DELETE"].map((method) => (
                      <option key={method} value={method}>
                        {method}
                      </option>
                    ))}
                  </select>
                  <input
                    aria-label="Advanced raw path"
                    onChange={(event) => setRawPath(event.target.value)}
                    placeholder="/scopes/{scopeId}/binding"
                    style={{ ...inputStyle, fontFamily: monoFontFamily }}
                    value={rawPath}
                  />
                </div>

                {rawMethod !== "GET" ? (
                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>Request Body</span>
                    <textarea
                      aria-label="Advanced raw body"
                      onChange={(event) => setRawBody(event.target.value)}
                      placeholder='{"key":"value"}'
                      style={textareaStyle}
                      value={rawBody}
                    />
                  </label>
                ) : null}

                <div>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={rawLoading || !rawPath.trim()}
                    onClick={() => void handleRawSubmit()}
                    style={actionButtonStyle(
                      "primary",
                      rawLoading || !rawPath.trim()
                    )}
                    type="button"
                  >
                    {rawLoading ? "Sending..." : "Send Request"}
                  </button>
                </div>
              </div>

              {rawResult ? (
                <div style={drawerSectionStyle}>
                  <div
                    style={{
                      alignItems: "center",
                      display: "flex",
                      gap: 12,
                      justifyContent: "space-between",
                    }}
                  >
                    <Typography.Text strong>
                      Response · {rawResult.status} {rawResult.statusText}
                    </Typography.Text>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      onClick={() => handleCopy(rawResult.body)}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      Copy
                    </button>
                  </div>
                  <pre style={monoBlockStyle}>{rawResult.body}</pre>
                </div>
              ) : null}
            </div>
          ) : null}
        </div>
      )}
    </AevatarContextDrawer>
  );
}
