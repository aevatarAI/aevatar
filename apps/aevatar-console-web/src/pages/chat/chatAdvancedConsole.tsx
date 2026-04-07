import { AGUIEventType, CustomEventName } from "@aevatar-react-sdk/types";
import { Alert, Empty, Space, Typography } from "antd";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { authFetch } from "@/shared/auth/fetch";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
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
  scopeServiceNamespace,
} from "@/shared/runs/scopeConsole";
import { studioApi } from "@/shared/studio/api";
import { AevatarContextDrawer } from "@/shared/ui/aevatarPageShells";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
  isRawObserved,
} from "./chatEventAdapter";
import { DebugPanel } from "./chatPresentation";
import type { RuntimeEvent } from "./chatTypes";

type ConsoleTab = "query" | "execute" | "raw";
type QueryTarget = "binding" | "services" | "workflows" | "actor";

type ChatAdvancedConsoleProps = {
  defaultServiceId?: string;
  onClose: () => void;
  onEnsureNyxIdBound?: () => Promise<void>;
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
          <button onClick={onCopy} style={actionButtonStyle("secondary")} type="button">
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
        path: `/services?tenantId=${scopeId}&appId=${scopeServiceAppId}&namespace=${scopeServiceNamespace}&take=20`,
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
          result = await studioApi.getScopeBinding(scopeId);
          break;
        case "services":
          result = await servicesApi.listServices({
            appId: scopeServiceAppId,
            namespace: scopeServiceNamespace,
            take: 100,
            tenantId: scopeId,
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
    if (!scopeId || !executeLaunchContext) {
      return;
    }

    history.push(
      buildRuntimeExplorerHref({
        actorId: executeActorId || undefined,
        runId: executeRunId || undefined,
        scopeId,
        serviceId: executeLaunchContext.serviceId,
      })
    );
  }, [executeActorId, executeLaunchContext, executeRunId, scopeId]);

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
          actorId: executeActorId || undefined,
        }
      );
      setExecuteAuditSnapshot(snapshot);
    } catch (error) {
      setExecuteAuditSnapshot(null);
      setExecuteAuditError(error instanceof Error ? error.message : String(error));
    } finally {
      setExecuteAuditLoading(false);
    }
  }, [executeActorId, executeLaunchContext?.serviceId, executeRunId, scopeId]);

  const executeAuditTimeline = executeAuditSnapshot?.audit.timeline ?? [];
  const executeAuditSteps = executeAuditSnapshot?.audit.steps ?? [];
  const executeAuditReplies = executeAuditSnapshot?.audit.roleReplies ?? [];
  const executeAuditSummary = executeAuditSnapshot?.audit.summary;

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
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 8,
            }}
          >
            {([
              ["query", "Query"],
              ["execute", "Execute"],
              ["raw", "Raw API"],
            ] as Array<[ConsoleTab, string]>).map(([tab, label]) => (
              <button
                key={tab}
                onClick={() => setActiveTab(tab)}
                style={{
                  ...actionButtonStyle(
                    activeTab === tab ? "primary" : "secondary"
                  ),
                  minWidth: 96,
                }}
                type="button"
              >
                {label}
              </button>
            ))}
          </div>

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
                      onClick={handleOpenRuns}
                      style={actionButtonStyle("secondary")}
                      type="button"
                    >
                      Open Runs
                    </button>
                    <button
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

          {activeTab === "raw" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={drawerSectionStyle}>
                <Typography.Text strong>Raw API Console</Typography.Text>
                <Space wrap>
                  {rawShortcuts.map((shortcut) => (
                    <button
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
