import { AGUIEventType, CustomEventName } from "@aevatar-react-sdk/types";
import { useIntl } from "@umijs/max";
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
import { useConsoleToast } from "@/shared/ui/ConsoleToast";
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
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from "@/shared/i18n/messages";

type ConsoleTab = "query" | "execute" | "timeline" | "raw";
type QueryTarget = "binding" | "services" | "workflows" | "actor";

type ConsoleFlow = {
  badge?: ConsoleMessageDescriptor;
  description: ConsoleMessageDescriptor;
  group: "developer" | "operate" | "understand";
  id: ConsoleTab;
  label: ConsoleMessageDescriptor;
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

const queryTargets: {
  description: ConsoleMessageDescriptor;
  id: QueryTarget;
  label: ConsoleMessageDescriptor;
}[] = [
  {
    description: {
      id: "pages.chat.chatadvancedconsole.current.default.binding.for.this",
      defaultMessage: "Current default binding for this workspace.",
    },
    id: "binding",
    label: {
      id: "pages.chat.chatadvancedconsole.workspace.binding",
      defaultMessage: "Workspace Binding",
    },
  },
  {
    description: {
      id: "pages.chat.chatadvancedconsole.all.published.services.currently.visible",
      defaultMessage: "All published services currently visible to this workspace.",
    },
    id: "services",
    label: {
      id: "pages.chat.chatadvancedconsole.services",
      defaultMessage: "Services",
    },
  },
  {
    description: {
      id: "pages.chat.chatadvancedconsole.workflow.assets.currently.deployed.into",
      defaultMessage: "Workflow assets currently deployed into this workspace.",
    },
    id: "workflows",
    label: {
      id: "pages.chat.chatadvancedconsole.workflows",
      defaultMessage: "Workflows",
    },
  },
  {
    description: {
      id: "pages.chat.chatadvancedconsole.inspect.a.specific.actor.by",
      defaultMessage: "Inspect a specific actor by its runtime ID.",
    },
    id: "actor",
    label: {
      id: "pages.chat.chatadvancedconsole.actor.snapshot",
      defaultMessage: "Actor Snapshot",
    },
  },
];

const consoleFlows: readonly ConsoleFlow[] = [
  {
    badge: {
      id: "pages.chat.chatadvancedconsole.recommended.first",
      defaultMessage: "Recommended first",
    },
    description: {
      id: "pages.chat.chatadvancedconsole.check.the.default.route.target",
      defaultMessage:
        "Check the default route target, published services, deployed workflows, or inspect an actor directly.",
    },
    group: "understand",
    id: "query",
    label: {
      id: "pages.chat.chatadvancedconsole.query",
      defaultMessage: "Query",
    },
    priority: "primary",
  },
  {
    description: {
      id: "pages.chat.chatadvancedconsole.inspect.actor.state.timeline.evidence",
      defaultMessage:
        "Inspect actor state, timeline evidence, graph topology, and any blocking gate that needs operator action.",
    },
    group: "understand",
    id: "timeline",
    label: {
      id: "pages.chat.chatadvancedconsole.timeline",
      defaultMessage: "Timeline",
    },
    priority: "secondary",
  },
  {
    badge: {
      id: "pages.chat.chatadvancedconsole.common.next.step",
      defaultMessage: "Common next step",
    },
    description: {
      id: "pages.chat.chatadvancedconsole.launch.a.service.endpoint.capture",
      defaultMessage:
        "Launch a service endpoint, capture the run receipt, and continue into Runs or Explorer when needed.",
    },
    group: "operate",
    id: "execute",
    label: {
      id: "pages.chat.chatadvancedconsole.execute",
      defaultMessage: "Execute",
    },
    priority: "primary",
  },
  {
    badge: {
      id: "pages.chat.chatadvancedconsole.expert",
      defaultMessage: "Expert",
    },
    description: {
      id: "pages.chat.chatadvancedconsole.send.direct.api.requests.only",
      defaultMessage:
        "Send direct API requests only when you need low-level integration or protocol debugging.",
    },
    group: "developer",
    id: "raw",
    label: {
      id: "pages.chat.chatadvancedconsole.raw.api.2",
      defaultMessage: "Raw API",
    },
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
            {t("pages.chat.chatadvancedconsole.copy.2", "Copy")}</button>
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
        {description || t("pages.chat.chatadvancedconsole.no.detail", "No detail")}
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
  const intl = useIntl();
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
  const [timelineActionNotice, setTimelineActionNotice] = useState("");
  const toast = useConsoleToast();

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
        description: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.inspect.the.current.workspace.and",
          defaultMessage: "Inspect the current workspace and understand runtime state.",
        }),
        flows: consoleFlows.filter((flow) => flow.group === "understand"),
        id: "understand",
        label: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.understand",
          defaultMessage: "Understand",
        }),
      },
      {
        description: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.run.work.inspect.the.receipt",
          defaultMessage: "Run work, inspect the receipt, and act on runtime gates.",
        }),
        flows: consoleFlows.filter((flow) => flow.group === "operate"),
        id: "operate",
        label: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.operate",
          defaultMessage: "Operate",
        }),
      },
      {
        description: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.drop.to.direct.api.calls",
          defaultMessage: "Drop to direct API calls when you need low-level debugging.",
        }),
        flows: consoleFlows.filter((flow) => flow.group === "developer"),
        id: "developer",
        label: intl.formatMessage({
          id: "pages.chat.chatadvancedconsole.developer",
          defaultMessage: "Developer",
        }),
      },
    ],
    [intl]
  );
  const activeConsoleFlow = useMemo(
    () => consoleFlows.find((flow) => flow.id === activeTab) || null,
    [activeTab]
  );

  const rawShortcuts = useMemo(
    () => [
      {
        label: t("pages.chat.chatadvancedconsole.binding", "Binding"),
        method: "GET",
        path: `/scopes/${scopeId}/binding`,
      },
      {
        label: t("pages.chat.chatadvancedconsole.services.2", "Services"),
        method: "GET",
        path: `/scopes/${scopeId}/services?appId=${scopeServiceAppId}&take=20`,
      },
      {
        label: t("pages.chat.chatadvancedconsole.workflows.2", "Workflows"),
        method: "GET",
        path: `/scopes/${scopeId}/workflows`,
      },
      activeExecuteService
        ? {
            label: t("pages.chat.chatadvancedconsole.runs", "Runs"),
            method: "GET",
            path: `/scopes/${scopeId}/services/${activeExecuteService.serviceId}/runs?take=10`,
          }
        : null,
      {
        label: t("pages.chat.chatadvancedconsole.auth.session", "Auth Session"),
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
        toast.error(
          t(
            "pages.chat.chatadvancedconsole.timelineActionFailed",
            "Run action could not be completed. Try again.",
          ),
        );
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
      subtitle={t("pages.chat.chatadvancedconsole.inspect.workspace.state.invoke.endpoints", "Inspect workspace state, invoke endpoints, or hit raw API paths without leaving chat.")}
      title={t("pages.chat.chatadvancedconsole.advanced.console", "Advanced Console")}
      width={960}
    >
      {!scopeId ? (
        <Alert
          description={t("pages.chat.chatadvancedconsole.open.workspace.chat.first.so", "Open a workspace chat first so the console has a project context.")}
          showIcon
          title={t("pages.chat.chatadvancedconsole.no.workspace.is.currently.active", "No workspace is currently active.")}
          type="warning"
        />
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <div style={drawerSectionStyle}>
            <Typography.Text strong>{t("pages.chat.chatadvancedconsole.choose.task", "Choose a task")}</Typography.Text>
            <Typography.Text type="secondary">
              {t("pages.chat.chatadvancedconsole.advanced.console.keeps.runtime.inspection", "Advanced Console keeps runtime inspection, operator actions, and developer tooling in one drawer. Start from the task you are trying to complete.")}</Typography.Text>
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
              {t("pages.chat.chatadvancedconsole.suggested.path.start.with", "Suggested path: start with")}<strong>{t("pages.chat.chatadvancedconsole.query", "Query")}</strong> {t("pages.chat.chatadvancedconsole.to.orient.the.workspace.move", "to orient the workspace, move to")}<strong>{t("pages.chat.chatadvancedconsole.execute", "Execute")}</strong> {t("pages.chat.chatadvancedconsole.when.you.are.ready.to", "when you are ready to act, then use")}<strong>{t("pages.chat.chatadvancedconsole.timeline", "Timeline")}</strong> {t("pages.chat.chatadvancedconsole.if.the.run.needs.evidence", "if the run needs evidence or operator input. Keep")}<strong>{t("pages.chat.chatadvancedconsole.raw.api", "Raw API")}</strong> {t("pages.chat.chatadvancedconsole.for.protocol.level.debugging", "for protocol-level debugging.")}</div>

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
                      const flowLabel = formatConsoleMessage(flow.label);
                      const flowDescription = formatConsoleMessage(flow.description);
                      const flowBadge = flow.badge
                        ? formatConsoleMessage(flow.badge)
                        : "";
                      return (
                        <button
                          aria-label={flowLabel}
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
                                {flowLabel}
                              </span>
                              {flowBadge ? (
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
                                  {flowBadge}
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
                                {t("pages.chat.chatadvancedconsole.active", "Active")}</span>
                            ) : null}
                          </div>
                          <div
                            style={{
                              color: "#6b7280",
                              fontSize: 12,
                              lineHeight: 1.6,
                            }}
                          >
                            {flowDescription}
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
              description={formatConsoleMessage(activeConsoleFlow.description)}
              message={t(
                "pages.chat.chatadvancedconsole.current.task",
                "Current task: {task}",
                { task: formatConsoleMessage(activeConsoleFlow.label) },
              )}
              showIcon
              type="info"
            />
          ) : null}

          {activeTab === "query" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>{t("pages.chat.chatadvancedconsole.query.workspace.state", "Query Workspace State")}</Typography.Text>
                <div
                  style={{
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
                  }}
                >
                  {queryTargets.map((target) => {
                    const targetLabel = formatConsoleMessage(target.label);
                    const targetDescription = formatConsoleMessage(target.description);

                    return (
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
                          {targetLabel}
                        </div>
                        <div style={{ color: "#6b7280", fontSize: 12, lineHeight: 1.5 }}>
                          {targetDescription}
                        </div>
                      </button>
                    );
                  })}
                </div>

                {queryTarget === "actor" ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.actor.id", "Actor ID")}</span>
                    <input
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.query.actor.id", "Advanced query actor ID")}
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
                      ? t("pages.chat.chatadvancedconsole.loading", "Loading...")
                      : t(
                          "pages.chat.chatadvancedconsole.query.target",
                          "Query {target}",
                          {
                            target: queryTargets.find(
                              (target) => target.id === queryTarget,
                            )?.label
                              ? formatConsoleMessage(
                                  queryTargets.find(
                                    (target) => target.id === queryTarget,
                                  )!.label,
                                )
                              : t("pages.chat.chatadvancedconsole.workspace", "Workspace"),
                          },
                        )}
                  </button>
                </div>
              </div>

              {queryResult
                ? createResultPanel(
                    t("pages.chat.chatadvancedconsole.query.result", "Query Result"),
                    queryResult,
                    () => handleCopy(queryResult),
                  )
                : null}
            </div>
          ) : null}

          {activeTab === "execute" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>{t("pages.chat.chatadvancedconsole.execute.service.endpoint", "Execute Service Endpoint")}</Typography.Text>
                <div style={{ display: "grid", gap: 12 }}>
                  <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.service", "Service")}</span>
                    <select
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.execute.service", "Advanced execute service")}
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
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.endpoint", "Endpoint")}</span>
                    <select
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.execute.endpoint", "Advanced execute endpoint")}
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
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.prompt", "Prompt")}</span>
                    <textarea
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.execute.prompt", "Advanced execute prompt")}
                      onChange={(event) => setExecutePrompt(event.target.value)}
                      placeholder={t("pages.chat.chatadvancedconsole.describe.the.call.you.want", "Describe the call you want to make.")}
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
                        <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.payload.type.url", "Payload Type URL")}</span>
                        <input
                          aria-label={t("pages.chat.chatadvancedconsole.advanced.execute.payload.type.url", "Advanced execute payload type URL")}
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
                        <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.payload.base64", "Payload Base64")}</span>
                        <textarea
                          aria-label={t("pages.chat.chatadvancedconsole.advanced.execute.payload.base64", "Advanced execute payload base64")}
                          onChange={(event) =>
                            setExecutePayloadBase64(event.target.value)
                          }
                          placeholder={t("pages.chat.chatadvancedconsole.optional.protobuf.payload.in.base64", "Optional protobuf payload in base64.")}
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
                      {executeStatus === "running" ? t("pages.chat.chatadvancedconsole.running", "Running...") : t("pages.chat.chatadvancedconsole.run", "Run")}
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
                      {t("pages.chat.chatadvancedconsole.stop", "Stop")}</button>
                  </Space>
                </div>
              </div>

              {executeError ? (
                <Alert showIcon title={executeError} type="error" />
              ) : null}

              {executeActorId || executeCommandId || executeRunId ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.execution.metadata", "Execution Metadata")}</Typography.Text>
                  <Space wrap>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      onClick={handleOpenRuns}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      {t("pages.chat.chatadvancedconsole.open.runs", "Open Runs")}</button>
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
                      {t("pages.chat.chatadvancedconsole.open.explorer", "Open Explorer")}</button>
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
                        ? t("pages.chat.chatadvancedconsole.loading.audit", "Loading Audit...")
                        : executeAuditSnapshot
                          ? t("pages.chat.chatadvancedconsole.refresh.audit", "Refresh Audit")
                          : t("pages.chat.chatadvancedconsole.load.audit", "Load Audit")}
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
                      <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.actor", "Actor")}</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeActorId || t("pages.chat.chatadvancedconsole.unavailable", "Unavailable")}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.command", "Command")}</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeCommandId || t("pages.chat.chatadvancedconsole.unavailable", "Unavailable")}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.run", "Run")}</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeRunId || t("pages.chat.chatadvancedconsole.unavailable", "Unavailable")}
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
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.run.audit", "Run Audit")}</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.completion", "Completion")}</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSnapshot.audit.completionStatus}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.duration", "Duration")}</Typography.Text>
                      <Typography.Text strong>
                        {Math.round(executeAuditSnapshot.audit.durationMs)} ms
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.steps", "Steps")}</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSummary?.completedSteps ?? 0}/
                        {executeAuditSummary?.totalSteps ?? 0}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.role.replies", "Role replies")}</Typography.Text>
                      <Typography.Text strong>
                        {executeAuditSummary?.roleReplyCount ?? 0}
                      </Typography.Text>
                    </div>
                  </div>

                  {executeAuditSnapshot.audit.input ? (
                    createResultPanel(
                      t("pages.chat.chatadvancedconsole.audit.input", "Audit Input"),
                      executeAuditSnapshot.audit.input,
                      () => handleCopy(executeAuditSnapshot.audit.input)
                    )
                  ) : null}

                  {executeAuditSnapshot.audit.finalOutput ? (
                    <Alert
                      description={executeAuditSnapshot.audit.finalOutput}
                      showIcon
                      title={t("pages.chat.chatadvancedconsole.final.output", "Final output")}
                      type="success"
                    />
                  ) : null}

                  {executeAuditSnapshot.audit.finalError ? (
                    <Alert
                      description={executeAuditSnapshot.audit.finalError}
                      showIcon
                      title={t("pages.chat.chatadvancedconsole.final.error", "Final error")}
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
                      <Typography.Text strong>{t("pages.chat.chatadvancedconsole.timeline.highlights", "Timeline Highlights")}</Typography.Text>
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
                                event.message || t("pages.chat.chatadvancedconsole.no.message", "No message"),
                                event.timestamp,
                                String(index)
                              )
                            )}
                        </div>
                      ) : (
                        <Empty
                          description={t("pages.chat.chatadvancedconsole.no.timeline.events.were.captured", "No timeline events were captured.")}
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </div>

                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>{t("pages.chat.chatadvancedconsole.step.highlights", "Step Highlights")}</Typography.Text>
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
                          description={t("pages.chat.chatadvancedconsole.no.step.audit.records.were", "No step audit records were captured.")}
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </div>
                  </div>

                  <div style={drawerSectionStyle}>
                    <Typography.Text strong>{t("pages.chat.chatadvancedconsole.reply.highlights", "Reply Highlights")}</Typography.Text>
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
                            reply.content || t("pages.chat.chatadvancedconsole.no.content", "No content"),
                            reply.timestamp,
                            String(index)
                          )
                        )}
                      </div>
                    ) : (
                      <Empty
                        description={t("pages.chat.chatadvancedconsole.no.role.replies.were.captured", "No role replies were captured.")}
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                  </div>
                </div>
              ) : null}

              {executeAssistantText
                ? createResultPanel(t("pages.chat.chatadvancedconsole.streaming.output", "Streaming Output"), executeAssistantText, () =>
                    handleCopy(executeAssistantText)
                  )
                : null}

              {executeResponseText
                ? createResultPanel(t("pages.chat.chatadvancedconsole.invoke.response", "Invoke Response"), executeResponseText, () =>
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
                <Typography.Text strong>{t("pages.chat.chatadvancedconsole.actor.timeline", "Actor Timeline")}</Typography.Text>
                <Typography.Text type="secondary">
                  {t("pages.chat.chatadvancedconsole.inspect.the.current.actor.snapshot", "Inspect the current actor snapshot, recent runtime stages, and any blocking gate without leaving chat.")}</Typography.Text>

                <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                  <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.actor.id.2", "Actor ID")}</span>
                  <input
                    aria-label={t("pages.chat.chatadvancedconsole.advanced.timeline.actor.id", "Advanced timeline actor ID")}
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
                    {timelineLoading ? t("pages.chat.chatadvancedconsole.refreshing", "Refreshing...") : t("pages.chat.chatadvancedconsole.refresh.timeline", "Refresh Timeline")}
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
                      ? t("pages.chat.chatadvancedconsole.loading.audit", "Loading Audit...")
                      : executeAuditSnapshot
                        ? t("pages.chat.chatadvancedconsole.refresh.audit", "Refresh Audit")
                        : t("pages.chat.chatadvancedconsole.load.audit.for.timeline", "Load Audit for Timeline")}
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
                    {t("pages.chat.chatadvancedconsole.open.explorer.2", "Open Explorer")}</button>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    disabled={!executeLaunchContext}
                    onClick={handleOpenRuns}
                    style={actionButtonStyle("secondary", !executeLaunchContext)}
                    type="button"
                  >
                    {t("pages.chat.chatadvancedconsole.open.runs.2", "Open Runs")}</button>
                </Space>

                {!effectiveTimelineActorId ? (
                  <Alert
                    description={t("pages.chat.chatadvancedconsole.run.service.endpoint.or.provide", "Run a service endpoint or provide an actor ID to inspect runtime state.")}
                    showIcon
                    title={t("pages.chat.chatadvancedconsole.no.actor.is.currently.selected", "No actor is currently selected.")}
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
                      <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.effective.actor", "Effective actor")}</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {effectiveTimelineActorId}
                      </div>
                    </div>
                    <div>
                      <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.run.2", "Run")}</div>
                      <div style={{ fontFamily: monoFontFamily, fontSize: 12 }}>
                        {executeRunId || t("pages.chat.chatadvancedconsole.unavailable", "Unavailable")}
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
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.snapshot", "Snapshot")}</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns:
                        "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.workflow", "Workflow")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.workflowName || "n/a"}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.completion.2", "Completion")}</Typography.Text>
                      <Typography.Text strong>
                        {describeActorCompletionStatus(timelineSnapshot)}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.state.version", "State version")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.stateVersion}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.completed.steps", "Completed steps")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.completedSteps}/
                        {timelineSnapshot.totalSteps}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.role.replies.2", "Role replies")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineSnapshot.roleReplyCount}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.last.update", "Last update")}</Typography.Text>
                      <Typography.Text strong>
                        {formatDateTime(timelineSnapshot.lastUpdatedAt)}
                      </Typography.Text>
                    </div>
                  </div>
                </div>
              ) : null}

              {timelineGraph ? (
                <div style={drawerSectionStyle}>
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.topology.digest", "Topology Digest")}</Typography.Text>
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns:
                        "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.nodes", "Nodes")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.nodes.length}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.edges", "Edges")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.edges.length}
                      </Typography.Text>
                    </div>
                    <div style={drawerSectionStyle}>
                      <Typography.Text type="secondary">{t("pages.chat.chatadvancedconsole.root.node", "Root node")}</Typography.Text>
                      <Typography.Text strong>
                        {timelineGraph.subgraph.rootNodeId || t("pages.chat.chatadvancedconsole.unavailable", "Unavailable")}
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
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.blocking.state", "Blocking State")}</Typography.Text>
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
                            ? t("pages.chat.chatadvancedconsole.waiting.on.signal", "Waiting on signal")
                            : timelineBlockingSummary.kind === "human_approval"
                              ? t("pages.chat.chatadvancedconsole.approval.required", "Approval required")
                              : t("pages.chat.chatadvancedconsole.input.required", "Input required")}
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
                            {t("pages.chat.chatadvancedconsole.signal", "Signal")}{timelineBlockingSummary.signalName}
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
                      message={t("pages.chat.chatadvancedconsole.current.runtime.gate", "Current runtime gate")}
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
                        <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.recommended.next.step", "Recommended next step")}</div>
                        <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
                          {timelineBlockingSummary.kind === "wait_signal"
                            ? t("pages.chat.chatadvancedconsole.send.the.signal.payload.that.the.runtime.is.waiting.for", "Send the signal payload that the runtime is waiting for.")
                            : timelineBlockingSummary.kind === "human_approval"
                              ? t("pages.chat.chatadvancedconsole.review.the.gate.and.approve.or.reject.it", "Review the gate and approve or reject it.")
                              : t("pages.chat.chatadvancedconsole.provide.the.missing.value.then.resume.the.run", "Provide the missing value, then resume the run.")}
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
                        <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.action.context", "Action context")}</div>
                        <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
                          {timelineBlockingSummary.kind === "wait_signal"
                            ? t("pages.chat.chatadvancedconsole.signal.payload.is.optional.unless.your.workflow.expects.a.value", "Signal payload is optional unless your workflow expects a value.")
                            : timelineBlockingSummary.kind === "human_approval"
                              ? t("pages.chat.chatadvancedconsole.approval.notes.are.optional.and.will.be.sent.with.the.decision", "Approval notes are optional and will be sent with the decision.")
                              : t("pages.chat.chatadvancedconsole.input.is.required.before.the.workflow.can.continue", "Input is required before the workflow can continue.")}
                        </div>
                      </div>
                    </div>
                  </div>
                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>
                      {timelineBlockingSummary.kind === "wait_signal"
                        ? t("pages.chat.chatadvancedconsole.signal.payload", "Signal payload")
                        : timelineBlockingSummary.kind === "human_approval"
                          ? t("pages.chat.chatadvancedconsole.approval.note", "Approval note")
                          : t("pages.chat.chatadvancedconsole.operator.input", "Operator input")}
                    </span>
                    <textarea
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.timeline.action.input", "Advanced timeline action input")}
                      disabled={timelineActionLoading}
                      onChange={(event) =>
                        setTimelineActionInput(event.target.value)
                      }
                      placeholder={
                        timelineBlockingSummary.kind === "wait_signal"
                          ? t("pages.chat.chatadvancedconsole.optional.signal.payload", "Optional signal payload")
                          : timelineBlockingSummary.kind === "human_approval"
                            ? t("pages.chat.chatadvancedconsole.optional.approval.note", "Optional approval note")
                            : t("pages.chat.chatadvancedconsole.provide.the.requested.input", "Provide the requested input")
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
                        {timelineActionLoading ? t("pages.chat.chatadvancedconsole.sending", "Sending...") : t("pages.chat.chatadvancedconsole.send.signal", "Send Signal")}
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
                            ? t("pages.chat.chatadvancedconsole.applying", "Applying...")
                            : timelineBlockingSummary.kind === "human_approval"
                              ? t("pages.chat.chatadvancedconsole.approve", "Approve")
                              : t("pages.chat.chatadvancedconsole.resume", "Resume")}
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
                            {t("pages.chat.chatadvancedconsole.reject", "Reject")}</button>
                        ) : null}
                      </>
                    )}
                  </Space>

                  {!executeRunId || !effectiveTimelineServiceId ? (
                    <Typography.Text type="secondary">
                      {t("pages.chat.chatadvancedconsole.run.actions.become.available.after", "Run actions become available after the console has a run ID and service context.")}</Typography.Text>
                  ) : null}

                  {timelineActionNotice ? (
                    <Alert showIcon title={timelineActionNotice} type="success" />
                  ) : null}
                </div>
              ) : null}

              <div style={drawerSectionStyle}>
                <Typography.Text strong>{t("pages.chat.chatadvancedconsole.timeline.filters", "Timeline Filters")}</Typography.Text>
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
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.search", "Search")}</span>
                    <input
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.timeline.search", "Advanced timeline search")}
                      onChange={(event) => setTimelineSearch(event.target.value)}
                      placeholder={t("pages.chat.chatadvancedconsole.filter.by.stage.step.message", "Filter by stage, step, message, or agent")}
                      style={inputStyle}
                      value={timelineSearch}
                    />
                  </label>

                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.stage", "Stage")}</span>
                    <select
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.timeline.stage", "Advanced timeline stage")}
                      onChange={(event) =>
                        setTimelineSelectedStage(event.target.value)
                      }
                      style={selectStyle}
                      value={timelineSelectedStage}
                    >
                      <option value="">{t("pages.chat.chatadvancedconsole.all.stages", "All stages")}</option>
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
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.timeline.errors.only", "Advanced timeline errors only")}
                      checked={timelineOnlyErrors}
                      onChange={(event) =>
                        setTimelineOnlyErrors(event.target.checked)
                      }
                      type="checkbox"
                    />
                    <span style={{ color: "#4b5563", fontSize: 13 }}>
                      {t("pages.chat.chatadvancedconsole.errors.only", "Errors only")}</span>
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
                  <Typography.Text strong>{t("pages.chat.chatadvancedconsole.timeline.events", "Timeline Events")}</Typography.Text>
                  {timelineLoading && !timelineRows.length ? (
                    <Alert
                      description={t("pages.chat.chatadvancedconsole.loading.the.latest.actor.timeline", "Loading the latest actor timeline.")}
                      showIcon
                      title={t("pages.chat.chatadvancedconsole.fetching.runtime.evidence", "Fetching runtime evidence")}
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
                                  {t("pages.chat.chatadvancedconsole.audit.linked", "Audit linked")}</span>
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
                              {row.message || t("pages.chat.chatadvancedconsole.no.message", "No message")}
                            </div>
                            <div
                              style={{
                                color: "#6b7280",
                                fontSize: 12,
                                lineHeight: 1.6,
                              }}
                            >
                              {row.dataSummary || t("pages.chat.chatadvancedconsole.no.structured.data", "No structured data")}
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
                              {row.stepType ? t("pages.chat.chatadvancedconsole.copy.4", "· {value1}", { value1: row.stepType }) : ""}
                              {row.agentId ? t("pages.chat.chatadvancedconsole.copy.5", "· {value1}", { value1: row.agentId }) : ""}
                            </div>
                          </button>
                        );
                      })}
                    </div>
                  ) : (
                    <Empty
                      description={t("pages.chat.chatadvancedconsole.no.timeline.items.matched.the", "No timeline items matched the current filters.")}
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                    />
                  )}
                </div>

                <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                  {selectedTimelineRow ? (
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>{t("pages.chat.chatadvancedconsole.selected.event", "Selected Event")}</Typography.Text>
                      <div
                        style={{
                          color: "#4b5563",
                          display: "flex",
                          flexDirection: "column",
                          gap: 8,
                        }}
                      >
                        <div>
                          <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.stage.2", "Stage")}</div>
                          <div>{selectedTimelineRow.stage || "n/a"}</div>
                        </div>
                        <div>
                          <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.message", "Message")}</div>
                          <div>{selectedTimelineRow.message || t("pages.chat.chatadvancedconsole.no.message", "No message")}</div>
                        </div>
                        <div>
                          <div style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.timestamp", "Timestamp")}</div>
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
                      <Typography.Text strong>{t("pages.chat.chatadvancedconsole.related.audit.step", "Related Audit Step")}</Typography.Text>
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
                            title={t("pages.chat.chatadvancedconsole.output.preview", "Output preview")}
                            type="success"
                          />
                        ) : null}
                        {relatedAuditStep.suspensionPrompt ? (
                          <Alert
                            description={relatedAuditStep.suspensionPrompt}
                            showIcon
                            title={t("pages.chat.chatadvancedconsole.suspension.prompt", "Suspension prompt")}
                            type="warning"
                          />
                        ) : null}
                      </div>
                    </div>
                  ) : executeRunId && !executeAuditSnapshot ? (
                    <div style={drawerSectionStyle}>
                      <Typography.Text strong>{t("pages.chat.chatadvancedconsole.related.audit.step.2", "Related Audit Step")}</Typography.Text>
                      <Empty
                        description={t("pages.chat.chatadvancedconsole.load.the.run.audit.to", "Load the run audit to correlate timeline events with structured step details.")}
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
                <Typography.Text strong>{t("pages.chat.chatadvancedconsole.raw.api.console", "Raw API Console")}</Typography.Text>
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
                    aria-label={t("pages.chat.chatadvancedconsole.advanced.raw.method", "Advanced raw method")}
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
                    aria-label={t("pages.chat.chatadvancedconsole.advanced.raw.path", "Advanced raw path")}
                    onChange={(event) => setRawPath(event.target.value)}
                    placeholder={t("pages.chat.chatadvancedconsole.scopes.binding", "/scopes/{scopeId}/binding")}
                    style={{ ...inputStyle, fontFamily: monoFontFamily }}
                    value={rawPath}
                  />
                </div>

                {rawMethod !== "GET" ? (
                  <label
                    style={{ display: "flex", flexDirection: "column", gap: 8 }}
                  >
                    <span style={fieldLabelStyle}>{t("pages.chat.chatadvancedconsole.request.body", "Request Body")}</span>
                    <textarea
                      aria-label={t("pages.chat.chatadvancedconsole.advanced.raw.body", "Advanced raw body")}
                      onChange={(event) => setRawBody(event.target.value)}
                      placeholder={t("pages.chat.chatadvancedconsole.copy", "{\"key\":\"value\"}")}
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
                    {rawLoading ? t("pages.chat.chatadvancedconsole.sending", "Sending...") : t("pages.chat.chatadvancedconsole.send.request", "Send Request")}
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
                      {t("pages.chat.chatadvancedconsole.response", "Response ·")}{rawResult.status} {rawResult.statusText}
                    </Typography.Text>
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      onClick={() => handleCopy(rawResult.body)}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      {t("pages.chat.chatadvancedconsole.copy.3", "Copy")}</button>
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
