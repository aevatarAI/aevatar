import { parseCustomEvent } from "@aevatar-react-sdk/agui";
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from "@aevatar-react-sdk/types";
import {
  BorderBottomOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Input, Select, Space, Typography } from "antd";
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { parseRunContextData } from "@/shared/agui/customEventData";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeGAgentsHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { saveObservedRunSessionPayload } from "@/shared/runs/draftRunSession";
import { studioApi } from "@/shared/studio/api";
import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from "@/shared/models/services";
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  getStudioScopeBindingCurrentRevision,
} from "@/shared/studio/models";
import {
  AevatarContextDrawer,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import ScopeServiceRuntimeWorkbench from "./components/ScopeServiceRuntimeWorkbench";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import ScopeQueryCard from "./components/ScopeQueryCard";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "./components/scopeQuery";

type InvokeResultState = {
  actorId: string;
  assistantText: string;
  commandId: string;
  endpointId: string;
  error: string;
  eventCount: number;
  events: AGUIEvent[];
  mode: "stream" | "invoke";
  responseJson: string;
  runId: string;
  serviceId: string;
  status: "idle" | "running" | "success" | "error";
};

type InvokeDockTab = "events" | "output";

type InvokeContextSurface = "insight" | "service" | null;

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function readQueryValue(name: string): string {
  if (typeof window === "undefined") {
    return "";
  }

  return new URLSearchParams(window.location.search).get(name)?.trim() ?? "";
}

function buildServiceOptions(
  services: readonly ServiceCatalogSnapshot[],
  defaultServiceId?: string,
): ServiceCatalogSnapshot[] {
  return [...services].sort((left, right) => {
    const leftIsDefault = left.serviceId === defaultServiceId ? 1 : 0;
    const rightIsDefault = right.serviceId === defaultServiceId ? 1 : 0;

    if (leftIsDefault !== rightIsDefault) {
      return rightIsDefault - leftIsDefault;
    }

    return left.serviceId.localeCompare(right.serviceId);
  });
}

function isChatEndpoint(endpoint: ServiceEndpointSnapshot | undefined): boolean {
  if (!endpoint) {
    return false;
  }

  return endpoint.kind === "chat" || endpoint.endpointId.trim() === "chat";
}

function createIdleResult(): InvokeResultState {
  return {
    actorId: "",
    assistantText: "",
    commandId: "",
    endpointId: "",
    error: "",
    eventCount: 0,
    events: [],
    mode: "invoke",
    responseJson: "",
    runId: "",
    serviceId: "",
    status: "idle",
  };
}

function getEventKey(event: AGUIEvent, indexHint: number): string {
  const candidate = event as unknown as Record<string, unknown>;
  return [
    event.type,
    String(candidate.timestamp ?? ""),
    String(candidate.runId ?? ""),
    String(candidate.messageId ?? ""),
    String(indexHint),
  ].join("-");
}

const ScopeInvokePage: React.FC = () => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedServiceId, setSelectedServiceId] = useState(readQueryValue("serviceId"));
  const [selectedEndpointId, setSelectedEndpointId] = useState(
    readQueryValue("endpointId"),
  );
  const [prompt, setPrompt] = useState("");
  const [payloadTypeUrl, setPayloadTypeUrl] = useState("");
  const [payloadBase64, setPayloadBase64] = useState("");
  const [preserveEmptySelection, setPreserveEmptySelection] = useState(false);
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );
  const [contextSurface, setContextSurface] =
    useState<InvokeContextSurface>(null);
  const [dockTab, setDockTab] = useState<InvokeDockTab>("events");
  const [dockHeight, setDockHeight] = useState(248);
  const [isDockCollapsed, setDockCollapsed] = useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);

  useEffect(() => {
    history.replace(
      buildScopeHref("/scopes/invoke", activeDraft, {
        endpointId: selectedEndpointId,
        serviceId: selectedServiceId,
      }),
    );
  }, [activeDraft, selectedEndpointId, selectedServiceId]);

  useEffect(() => () => abortControllerRef.current?.abort(), []);

  const scopeId = activeDraft.scopeId.trim();
  const bindingQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "binding", scopeId],
    queryFn: () => studioApi.getScopeBinding(scopeId),
  });
  const scopeServicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "invoke", "services", scopeId],
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
        scopeServicesQuery.data ?? [],
        bindingQuery.data?.available ? bindingQuery.data.serviceId : undefined,
      ),
    [bindingQuery.data?.available, bindingQuery.data?.serviceId, scopeServicesQuery.data],
  );

  useEffect(() => {
    if (!services.length) {
      setSelectedServiceId("");
      return;
    }

    if (
      selectedServiceId &&
      services.some((service) => service.serviceId === selectedServiceId)
    ) {
      return;
    }

    if (preserveEmptySelection) {
      setSelectedServiceId("");
      return;
    }

    setSelectedServiceId(
      services.find((service) => service.serviceId === bindingQuery.data?.serviceId)
        ?.serviceId ||
        services[0]?.serviceId ||
        "",
    );
  }, [
    bindingQuery.data?.serviceId,
    preserveEmptySelection,
    selectedServiceId,
    services,
  ]);

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;

  useEffect(() => {
    if (!selectedService) {
      setSelectedEndpointId("");
      return;
    }

    if (
      selectedEndpointId &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === selectedEndpointId,
      )
    ) {
      return;
    }

    setSelectedEndpointId(
      selectedService.endpoints.find((endpoint) => endpoint.endpointId === "chat")
        ?.endpointId ||
        selectedService.endpoints[0]?.endpointId ||
        "",
    );
  }, [selectedEndpointId, selectedService]);

  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;
  const currentBindingRevision = getStudioScopeBindingCurrentRevision(
    bindingQuery.data,
  );
  const currentBindingTarget = describeStudioScopeBindingRevisionTarget(
    currentBindingRevision,
  );
  const currentBindingContext = describeStudioScopeBindingRevisionContext(
    currentBindingRevision,
  );
  const currentBindingActor =
    currentBindingRevision?.primaryActorId ||
    currentBindingRevision?.staticPreferredActorId ||
    bindingQuery.data?.primaryActorId ||
    "";

  useEffect(() => {
    if (!selectedEndpoint || isChatEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl("");
      setPayloadBase64("");
      return;
    }

    setPayloadTypeUrl(selectedEndpoint.requestTypeUrl || "");
  }, [selectedEndpoint]);

  const serviceOptions = services.map((service) => ({
    label: service.displayName || service.serviceId,
    value: service.serviceId,
  }));
  const endpointOptions = (selectedService?.endpoints ?? []).map((endpoint) => ({
    label: endpoint.displayName || endpoint.endpointId,
    value: endpoint.endpointId,
  }));

  const handleAbort = () => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setInvokeResult((current) => ({
      ...current,
      error: "Invocation aborted by operator.",
      status: "error",
    }));
  };

  const handleInvoke = async () => {
    if (!scopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    abortControllerRef.current?.abort();
    abortControllerRef.current = null;

    if (isChatEndpoint(selectedEndpoint)) {
      const controller = new AbortController();
      abortControllerRef.current = controller;
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        mode: "stream",
        serviceId: selectedService.serviceId,
        status: "running",
      });

      try {
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
            prompt: prompt.trim(),
          },
          controller.signal,
          {
            serviceId: selectedService.serviceId,
          },
        );

        let assistantText = "";
        let actorId = "";
        let commandId = "";
        let runId = "";
        let eventCount = 0;
        let runError = "";
        const events: AGUIEvent[] = [];

        for await (const event of parseBackendSSEStream(response, {
          signal: controller.signal,
        })) {
          eventCount += 1;
          events.push(event);

          if (event.type === AGUIEventType.RUN_STARTED) {
            runId = event.runId || runId;
          }

          if (event.type === AGUIEventType.TEXT_MESSAGE_CONTENT) {
            assistantText += event.delta || "";
          }

          if (event.type === AGUIEventType.RUN_ERROR) {
            runError = event.message || "Assistant run failed.";
          }

          if (event.type === AGUIEventType.CUSTOM) {
            const custom = parseCustomEvent(event);
            if (custom.name === CustomEventName.RunContext) {
              const context = parseRunContextData(custom.data);
              actorId = context?.actorId ?? actorId;
              commandId = context?.commandId ?? commandId;
            }
          }

          setInvokeResult({
            actorId,
            assistantText,
            commandId,
            endpointId: selectedEndpoint.endpointId,
            error: runError,
            eventCount,
            events: [...events],
            mode: "stream",
            responseJson: "",
            runId,
            serviceId: selectedService.serviceId,
            status: runError ? "error" : "running",
          });
        }

        if (!controller.signal.aborted) {
          setInvokeResult({
            actorId,
            assistantText,
            commandId,
            endpointId: selectedEndpoint.endpointId,
            error: runError,
            eventCount,
            events,
            mode: "stream",
            responseJson: "",
            runId,
            serviceId: selectedService.serviceId,
            status: runError ? "error" : "success",
          });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setInvokeResult({
            ...createIdleResult(),
            endpointId: selectedEndpoint.endpointId,
            error: error instanceof Error ? error.message : String(error),
            mode: "stream",
            serviceId: selectedService.serviceId,
            status: "error",
          });
        }
      } finally {
        if (abortControllerRef.current === controller) {
          abortControllerRef.current = null;
        }
      }

      return;
    }

    setInvokeResult({
      ...createIdleResult(),
      endpointId: selectedEndpoint.endpointId,
      mode: "invoke",
      serviceId: selectedService.serviceId,
      status: "running",
    });

    try {
      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          payloadBase64: payloadBase64.trim() || undefined,
          payloadTypeUrl: payloadTypeUrl.trim() || undefined,
          prompt: prompt.trim(),
        },
        {
          serviceId: selectedService.serviceId,
        },
      );
      const responseRunId = String(
        response.request_id ?? response.requestId ?? response.commandId ?? "",
      ).trim();
      const responseActorId = String(
        response.target_actor_id ?? response.targetActorId ?? response.actorId ?? "",
      ).trim();
      const responseCommandId = String(
        response.command_id ?? response.commandId ?? responseRunId,
      ).trim();
      const events: AGUIEvent[] = [
        {
          runId: responseRunId || undefined,
          threadId:
            String(
              response.correlation_id ?? response.correlationId ?? responseRunId,
            ).trim() || undefined,
          timestamp: Date.now(),
          type: AGUIEventType.RUN_STARTED,
        } as AGUIEvent,
      ];

      if (responseActorId || responseCommandId) {
        events.push({
          name: CustomEventName.RunContext,
          timestamp: Date.now(),
          type: AGUIEventType.CUSTOM,
          value: {
            actorId: responseActorId || undefined,
            commandId: responseCommandId || undefined,
          },
        } as AGUIEvent);
      }

      setInvokeResult({
        ...createIdleResult(),
        actorId: responseActorId,
        commandId: responseCommandId,
        endpointId: selectedEndpoint.endpointId,
        eventCount: events.length,
        events,
        mode: "invoke",
        responseJson: JSON.stringify(response, null, 2),
        runId: responseRunId,
        serviceId: selectedService.serviceId,
        status: "success",
      });
    } catch (error) {
      setInvokeResult({
        ...createIdleResult(),
        endpointId: selectedEndpoint.endpointId,
        error: error instanceof Error ? error.message : String(error),
        mode: "invoke",
        serviceId: selectedService.serviceId,
        status: "error",
      });
    }
  };

  const handleOpenRuns = () => {
    if (!scopeId) {
      return;
    }

    const observedDraftKey =
      invokeResult.events.length > 0
        ? saveObservedRunSessionPayload({
            actorId: invokeResult.actorId || undefined,
            commandId: invokeResult.commandId || undefined,
            endpointId: invokeResult.endpointId || selectedEndpoint?.endpointId || "chat",
            events: invokeResult.events,
            payloadBase64:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadBase64 || undefined
                : undefined,
            payloadTypeUrl:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadTypeUrl || undefined
                : undefined,
            prompt,
            runId: invokeResult.runId || undefined,
            scopeId,
            serviceOverrideId: selectedService?.serviceId,
          })
        : "";

    history.push(
      buildRuntimeRunsHref({
        actorId: invokeResult.actorId || undefined,
        draftKey: observedDraftKey || undefined,
        endpointId: selectedEndpoint?.endpointId,
        payloadTypeUrl:
          selectedEndpoint && !isChatEndpoint(selectedEndpoint)
            ? payloadTypeUrl || undefined
            : undefined,
        prompt: prompt || undefined,
        scopeId,
        serviceId: selectedService?.serviceId,
      }),
    );
  };

  const recommendedNextStep = !scopeId
    ? {
        action: () => history.push("/scopes/overview"),
        actionLabel: "Open projects",
        description:
          "Project Invoke only becomes useful after you anchor the console to a scope.",
        title: "Load a project first",
      }
    : services.length === 0
      ? {
          action: () =>
            history.push(
              buildRuntimeGAgentsHref({
                scopeId,
                actorId: currentBindingRevision?.staticPreferredActorId || undefined,
                actorTypeName: currentBindingRevision?.staticActorTypeName || undefined,
              }),
            ),
          actionLabel: "Open GAgents",
          description:
            "No published scope services were discovered. Manage the current binding before using the invoke lab.",
          title: "Publish or switch the default binding",
        }
      : invokeResult.status === "success"
        ? {
            action: handleOpenRuns,
            actionLabel: "Continue in Runs",
            description:
              "The latest invoke has context attached. Move to Runs for the full trace and intervention controls.",
            title: "Promote this session to runtime observation",
          }
        : {
            action: () => setContextSurface("service"),
            actionLabel: "Browse services",
            description:
              "Pick the right service and endpoint before you start streaming or generic invocation.",
            title: "Choose the published entrypoint",
          };

  const outputPanels = useMemo(
    () =>
      [
        invokeResult.assistantText
          ? {
              title: "Assistant Output",
              value: invokeResult.assistantText,
            }
          : null,
        invokeResult.responseJson
          ? {
              title: "Invocation Receipt",
              value: invokeResult.responseJson,
            }
          : null,
      ].filter((panel): panel is { title: string; value: string } => Boolean(panel)),
    [invokeResult.assistantText, invokeResult.responseJson],
  );

  const startDockResize = useCallback(
    (event: React.MouseEvent<HTMLDivElement>) => {
      if (isDockCollapsed) {
        return;
      }

      event.preventDefault();
      const startY = event.clientY;
      const startHeight = dockHeight;

      const handleMouseMove = (moveEvent: MouseEvent) => {
        const nextHeight = Math.max(
          180,
          Math.min(420, startHeight + startY - moveEvent.clientY),
        );
        setDockHeight(nextHeight);
      };

      const handleMouseUp = () => {
        window.removeEventListener("mousemove", handleMouseMove);
        window.removeEventListener("mouseup", handleMouseUp);
      };

      window.addEventListener("mousemove", handleMouseMove);
      window.addEventListener("mouseup", handleMouseUp);
    },
    [dockHeight, isDockCollapsed],
  );

  return (
    <AevatarPageShell
      content="Invoke Lab keeps parameters on the left, execution on the main stage, and deeper context in the drawer or lab console."
      onBack={() => history.push(buildScopeHref("/scopes/overview", activeDraft))}
      title="Invoke Lab"
    >
      <AevatarWorkbenchLayout
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              description="Load the current scope, then choose the published service and endpoint you want to probe."
              title="Invocation Controls"
            >
              <ScopeQueryCard
                draft={draft}
                loadLabel="Load invoke lab"
                onChange={setDraft}
                onLoad={() => {
                  const nextDraft = normalizeScopeDraft(draft);
                  setPreserveEmptySelection(false);
                  setDraft(nextDraft);
                  setActiveDraft(nextDraft);
                }}
                onReset={() => {
                  const nextDraft = normalizeScopeDraft({
                    scopeId: resolvedScope?.scopeId ?? "",
                  });
                  setPreserveEmptySelection(true);
                  setDraft(nextDraft);
                  setActiveDraft(nextDraft);
                  setSelectedServiceId("");
                  setSelectedEndpointId("");
                  setPrompt("");
                  setPayloadTypeUrl("");
                  setPayloadBase64("");
                  setInvokeResult(createIdleResult());
                }}
                onUseResolvedScope={() => {
                  if (!resolvedScope?.scopeId) {
                    return;
                  }

                  const nextDraft = normalizeScopeDraft({
                    scopeId: resolvedScope.scopeId,
                  });
                  setPreserveEmptySelection(false);
                  setDraft(nextDraft);
                  setActiveDraft(nextDraft);
                }}
                resolvedScopeId={resolvedScope?.scopeId}
                resolvedScopeSource={resolvedScope?.scopeSource}
              />

              <Select
                onChange={(value) => {
                  setPreserveEmptySelection(false);
                  setSelectedServiceId(value);
                }}
                options={serviceOptions}
                placeholder="Select published service"
                value={selectedServiceId || undefined}
              />
              <Select
                onChange={setSelectedEndpointId}
                options={endpointOptions}
                placeholder="Select endpoint"
                value={selectedEndpointId || undefined}
              />
              <Input.TextArea
                onChange={(event) => setPrompt(event.target.value)}
                placeholder="Prompt or payload text"
                rows={5}
                value={prompt}
              />
              {selectedEndpoint && !isChatEndpoint(selectedEndpoint) ? (
                <>
                  <Input
                    onChange={(event) => setPayloadTypeUrl(event.target.value)}
                    placeholder="Payload type URL"
                    value={payloadTypeUrl}
                  />
                  <Input.TextArea
                    onChange={(event) => setPayloadBase64(event.target.value)}
                    placeholder="Payload base64"
                    rows={3}
                    value={payloadBase64}
                  />
                </>
              ) : (
                <Alert
                  title="Chat endpoints stream SSE responses and emit run context events automatically."
                  showIcon
                  type="info"
                />
              )}
              <Space wrap>
                <Button
                  aria-label={
                    selectedEndpoint && isChatEndpoint(selectedEndpoint)
                      ? "Stream chat"
                      : "Invoke endpoint"
                  }
                  disabled={!selectedEndpointId}
                  icon={<PlayCircleOutlined />}
                  loading={invokeResult.status === "running"}
                  onClick={() => void handleInvoke()}
                  type="primary"
                >
                  {selectedEndpoint && isChatEndpoint(selectedEndpoint)
                    ? "Stream chat"
                    : "Invoke endpoint"}
                </Button>
                <Button
                  aria-label="Abort"
                  disabled={invokeResult.status !== "running"}
                  icon={<StopOutlined />}
                  onClick={handleAbort}
                >
                  Abort
                </Button>
              </Space>
            </AevatarPanel>

            <AevatarPanel
              description="Keep the default binding visible while you choose a service and endpoint."
              title="Current Binding"
            >
              {!bindingQuery.data?.available || !currentBindingRevision ? (
                <Alert
                  title="No published default binding is active for this project yet."
                  showIcon
                  type="info"
                />
              ) : (
                <Space direction="vertical" size={12} style={{ width: "100%" }}>
                  <MetricCard
                    label="Target"
                    value={currentBindingTarget}
                  />
                  <MetricCard
                    label="Revision"
                    value={currentBindingRevision.revisionId}
                  />
                  <MetricCard
                    label="Actor"
                    value={currentBindingActor || "n/a"}
                  />
                  {currentBindingContext ? (
                    <Typography.Text type="secondary">
                      {currentBindingContext}
                    </Typography.Text>
                  ) : null}
                  <Button
                    onClick={() =>
                      history.push(
                        buildRuntimeGAgentsHref({
                          scopeId,
                          actorId:
                            currentBindingRevision.staticPreferredActorId ||
                            undefined,
                          actorTypeName:
                            currentBindingRevision.staticActorTypeName ||
                            undefined,
                        }),
                      )
                    }
                  >
                    Manage in GAgents
                  </Button>
                </Space>
              )}
            </AevatarPanel>
          </div>
        }
        stage={
          <div
            style={{
              display: "flex",
              flex: 1,
              flexDirection: "column",
              gap: 16,
              minHeight: 0,
            }}
          >
            <AevatarPanel
              description="The center stage stays focused on execution. Service catalog details and operator guidance move into the contextual drawer."
              extra={
                <Space wrap>
                  <Button onClick={() => setContextSurface("service")}>
                    Browse services
                  </Button>
                  <Button onClick={() => setContextSurface("insight")}>
                    Open operator brief
                  </Button>
                  {invokeResult.status === "success" ? (
                    <Button onClick={handleOpenRuns} type="primary">
                      Continue in Runs
                    </Button>
                  ) : null}
                </Space>
              }
              minHeight={320}
              title="Execution Preview"
            >
              {!scopeId ? (
                <Alert
                  title="Select a project to load its published services."
                  showIcon
                  type="info"
                />
              ) : !selectedService ? (
                <Alert
                  title="No published project service is selected yet."
                  showIcon
                  type="warning"
                />
              ) : (
                <>
                  {invokeResult.status !== "idle" ? (
                    <Alert
                      description={
                        invokeResult.error ||
                        (invokeResult.status === "running"
                          ? "Invocation in progress."
                          : "Invocation completed.")
                      }
                      title={`${
                        invokeResult.mode === "stream" ? "Streaming" : "Invoke"
                      } · ${invokeResult.serviceId || selectedService.serviceId} / ${
                        invokeResult.endpointId || selectedEndpointId || "endpoint"
                      }`}
                      showIcon
                      type={
                        invokeResult.status === "error"
                          ? "error"
                          : invokeResult.status === "success"
                            ? "success"
                            : "info"
                      }
                    />
                  ) : (
                    <Alert
                      title="Ready to invoke"
                      showIcon
                      type="info"
                    />
                  )}

                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <MetricCard label="Service" value={selectedService.displayName || selectedService.serviceId} />
                    <MetricCard label="Endpoint" value={selectedEndpoint?.endpointId || "n/a"} />
                    <MetricCard label="Run ID" value={invokeResult.runId || "n/a"} />
                    <MetricCard label="Actor ID" value={invokeResult.actorId || "n/a"} />
                  </div>

                  <Alert
                    showIcon
                    title={
                      invokeResult.status === "idle"
                        ? "The lab console is ready for live events and invocation output."
                        : invokeResult.status === "running"
                          ? "Live data is streaming into the lab console."
                          : "Open the lab console for the full event trace and response payload."
                    }
                    type={
                      invokeResult.status === "error"
                        ? "error"
                        : invokeResult.status === "success"
                          ? "success"
                          : "info"
                    }
                  />
                </>
              )}
            </AevatarPanel>
          </div>
        }
      />

      <InvokeLabDock
        activeTab={dockTab}
        dockHeight={dockHeight}
        events={invokeResult.events}
        isCollapsed={isDockCollapsed}
        onResizeStart={startDockResize}
        onTabChange={setDockTab}
        outputPanels={outputPanels}
        setCollapsed={setDockCollapsed}
      />

      <AevatarContextDrawer
        onClose={() => setContextSurface(null)}
        open={Boolean(contextSurface)}
        subtitle={
          contextSurface === "service"
            ? selectedService
              ? `${selectedService.namespace}/${selectedService.serviceId}`
              : "Published service"
            : "Recommended next step for the current invoke session"
        }
        title={
          contextSurface === "service"
            ? selectedService?.displayName || selectedService?.serviceId || "Service"
            : "Operator Brief"
        }
        extra={
          contextSurface === "insight" ? (
            <Button onClick={recommendedNextStep.action} type="primary">
              {recommendedNextStep.actionLabel}
            </Button>
          ) : null
        }
      >
        {contextSurface === "insight" ? (
          <>
            <AevatarPanel
              description="The operator brief keeps the next step out of the main stage until you need it."
              title="Recommended Action"
            >
              <Space direction="vertical" size={10} style={{ width: "100%" }}>
                <Typography.Text strong>{recommendedNextStep.title}</Typography.Text>
                <Typography.Text type="secondary">
                  {recommendedNextStep.description}
                </Typography.Text>
                <AevatarStatusTag
                  domain="run"
                  status={
                    invokeResult.status === "idle" ? "draft" : invokeResult.status
                  }
                />
                <Typography.Text type="secondary">
                  {invokeResult.eventCount} observed events · Mode {invokeResult.mode}
                </Typography.Text>
              </Space>
            </AevatarPanel>
            <AevatarPanel title="Session Snapshot">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <MetricCard label="Project" value={scopeId || "n/a"} />
                <MetricCard
                  label="Service"
                  value={selectedService?.displayName || selectedService?.serviceId || "n/a"}
                />
                <MetricCard
                  label="Endpoint"
                  value={selectedEndpoint?.displayName || selectedEndpoint?.endpointId || "n/a"}
                />
                <MetricCard label="Run ID" value={invokeResult.runId || "n/a"} />
              </div>
            </AevatarPanel>
          </>
        ) : (
          <ScopeServiceRuntimeWorkbench
            onSelectService={(serviceId) => {
              setPreserveEmptySelection(false);
              setSelectedServiceId(serviceId);
            }}
            onUseEndpoint={(serviceId, endpointId) => {
              setPreserveEmptySelection(false);
              setSelectedServiceId(serviceId);
              setSelectedEndpointId(endpointId);
            }}
            scopeId={scopeId}
            selectedEndpointId={selectedEndpointId}
            selectedServiceId={selectedServiceId}
            services={services}
          />
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

const MetricCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      background: "var(--ant-color-fill-quaternary)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      padding: 12,
    }}
  >
    <Typography.Text type="secondary">{label}</Typography.Text>
    <Typography.Text strong>{value}</Typography.Text>
  </div>
);

const CodePanel: React.FC<{
  title: string;
  value: string;
}> = ({ title, value }) => (
  <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
    <Typography.Text strong>{title}</Typography.Text>
    <pre
      style={{
        background: "var(--ant-color-fill-quaternary)",
        border: "1px solid var(--ant-color-border-secondary)",
        borderRadius: 12,
        margin: 0,
        maxHeight: 320,
        overflow: "auto",
        padding: 12,
        whiteSpace: "pre-wrap",
      }}
    >
      {value}
    </pre>
  </div>
);

const InvokeLabDock: React.FC<{
  activeTab: InvokeDockTab;
  dockHeight: number;
  events: AGUIEvent[];
  isCollapsed: boolean;
  onResizeStart: (event: React.MouseEvent<HTMLDivElement>) => void;
  onTabChange: (tab: InvokeDockTab) => void;
  outputPanels: { title: string; value: string }[];
  setCollapsed: (collapsed: boolean) => void;
}> = ({
  activeTab,
  dockHeight,
  events,
  isCollapsed,
  onResizeStart,
  onTabChange,
  outputPanels,
  setCollapsed,
}) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flex: `0 0 ${isCollapsed ? 54 : dockHeight}px`,
      flexDirection: "column",
      minHeight: isCollapsed ? 54 : 188,
      overflow: "hidden",
    }}
  >
    <hr
      aria-label="Resize lab console"
      onMouseDown={onResizeStart}
      style={{
        background: "var(--ant-color-fill-tertiary)",
        border: 0,
        cursor: isCollapsed ? "default" : "row-resize",
        flex: "0 0 8px",
        margin: 0,
      }}
    />
    <div
      style={{
        alignItems: "center",
        borderBottom: "1px solid var(--ant-color-border-secondary)",
        display: "flex",
        gap: 12,
        justifyContent: "space-between",
        padding: "12px 16px",
      }}
    >
      <Space wrap size={[8, 8]}>
        <Typography.Text strong>Lab Console</Typography.Text>
        <Button
          icon={<BorderBottomOutlined />}
          onClick={() => onTabChange("events")}
          type={activeTab === "events" ? "primary" : "default"}
        >
          Observed Events
        </Button>
        <Button
          onClick={() => onTabChange("output")}
          type={activeTab === "output" ? "primary" : "default"}
        >
          Output
        </Button>
      </Space>
      <Button onClick={() => setCollapsed(!isCollapsed)}>
        {isCollapsed ? "Expand Console" : "Collapse Console"}
      </Button>
    </div>
    {!isCollapsed ? (
      <div
        style={{
          display: "flex",
          flex: 1,
          flexDirection: "column",
          minHeight: 0,
          overflow: "auto",
          padding: 16,
        }}
      >
        {activeTab === "events" ? (
          events.length > 0 ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              {events.slice(-16).map((event, index) => (
                <div
                  key={getEventKey(event, index)}
                  style={{
                    border: "1px solid var(--ant-color-border-secondary)",
                    borderRadius: 12,
                    display: "flex",
                    flexDirection: "column",
                    gap: 6,
                    padding: 12,
                  }}
                >
                  <Space wrap size={[8, 8]}>
                    <AevatarStatusTag domain="observation" status="streaming" />
                    <Typography.Text strong>{event.type}</Typography.Text>
                  </Space>
                  <Typography.Text type="secondary">
                    {JSON.stringify(event, null, 2)}
                  </Typography.Text>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              description="No events have been observed yet."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )
        ) : outputPanels.length > 0 ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {outputPanels.map((panel) => (
              <CodePanel key={panel.title} title={panel.title} value={panel.value} />
            ))}
          </div>
        ) : (
          <Empty
            description="Invocation output will appear here after you stream or invoke a service."
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        )}
      </div>
    ) : null}
  </div>
);

export default ScopeInvokePage;
