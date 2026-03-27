import type { ProColumns } from '@ant-design/pro-components';
import { PageContainer, ProCard, ProTable } from '@ant-design/pro-components';
import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from '@aevatar-react-sdk/types';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Col, Input, Row, Select, Space, Tag, Typography } from 'antd';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { parseRunContextData } from '@/shared/agui/customEventData';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import { saveObservedRunSessionPayload } from '@/shared/runs/draftRunSession';
import { studioApi } from '@/shared/studio/api';
import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from '@/shared/models/services';
import { buildServiceDetailHref } from '@/pages/services/components/serviceQuery';
import {
  cardStackStyle,
  codeBlockStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import ScopeQueryCard from './components/ScopeQueryCard';
import { resolveStudioScopeContext } from './components/resolvedScope';
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from './components/scopeQuery';

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

type InvokeResultState = {
  status: 'idle' | 'running' | 'success' | 'error';
  mode: 'stream' | 'invoke';
  serviceId: string;
  endpointId: string;
  assistantText: string;
  responseJson: string;
  error: string;
  runId: string;
  actorId: string;
  commandId: string;
  eventCount: number;
  events: AGUIEvent[];
};

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {typeof value === 'string' || typeof value === 'number' ? (
      <Typography.Text>{value}</Typography.Text>
    ) : (
      value
    )}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

function readQueryValue(name: string): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get(name)?.trim() ?? '';
}

function createIdleResult(): InvokeResultState {
  return {
    status: 'idle',
    mode: 'invoke',
    serviceId: '',
    endpointId: '',
    assistantText: '',
    responseJson: '',
    error: '',
    runId: '',
    actorId: '',
    commandId: '',
    eventCount: 0,
    events: [],
  };
}

function isChatEndpoint(endpoint: ServiceEndpointSnapshot | undefined): boolean {
  if (!endpoint) {
    return false;
  }

  return endpoint.kind === 'chat' || endpoint.endpointId.trim() === 'chat';
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

const initialDraft = readScopeQueryDraft();
const initialServiceId = readQueryValue('serviceId');
const initialEndpointId = readQueryValue('endpointId');
const scopeServiceAppId = 'default';
const scopeServiceNamespace = 'default';

const ScopeInvokePage: React.FC = () => {
  const abortControllerRef = useRef<AbortController | null>(null);
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedServiceId, setSelectedServiceId] = useState(initialServiceId);
  const [selectedEndpointId, setSelectedEndpointId] = useState(initialEndpointId);
  const [prompt, setPrompt] = useState('');
  const [payloadTypeUrl, setPayloadTypeUrl] = useState('');
  const [payloadBase64, setPayloadBase64] = useState('');
  const [invokeResult, setInvokeResult] = useState<InvokeResultState>(
    createIdleResult(),
  );

  const authSessionQuery = useQuery({
    queryKey: ['scopes', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    history.replace(
      buildScopeHref('/scopes/invoke', activeDraft, {
        serviceId: selectedServiceId,
        endpointId: selectedEndpointId,
      }),
    );
  }, [activeDraft, selectedEndpointId, selectedServiceId]);

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

  useEffect(() => () => abortControllerRef.current?.abort(), []);

  const bindingQuery = useQuery({
    queryKey: ['scopes', 'binding', activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => studioApi.getScopeBinding(activeDraft.scopeId),
  });
  const scopeServicesQuery = useQuery({
    queryKey: ['scopes', 'invoke', 'services', activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () =>
      servicesApi.listServices({
        tenantId: activeDraft.scopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
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
    if (services.length === 0) {
      setSelectedServiceId('');
      return;
    }

    if (
      selectedServiceId &&
      services.some((service) => service.serviceId === selectedServiceId)
    ) {
      return;
    }

    const preferredServiceId =
      services.find(
        (service) => service.serviceId === bindingQuery.data?.serviceId,
      )?.serviceId ??
      services[0]?.serviceId ??
      '';

    setSelectedServiceId(preferredServiceId);
  }, [bindingQuery.data?.serviceId, selectedServiceId, services]);

  const selectedService = useMemo(
    () =>
      services.find((service) => service.serviceId === selectedServiceId) ?? null,
    [selectedServiceId, services],
  );

  useEffect(() => {
    if (!selectedService) {
      setSelectedEndpointId('');
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

    const preferredEndpointId =
      selectedService.endpoints.find((endpoint) => endpoint.endpointId === 'chat')
        ?.endpointId ??
      selectedService.endpoints[0]?.endpointId ??
      '';

    setSelectedEndpointId(preferredEndpointId);
  }, [selectedEndpointId, selectedService]);

  const selectedEndpoint = useMemo(
    () =>
      selectedService?.endpoints.find(
        (endpoint) => endpoint.endpointId === selectedEndpointId,
      ) ?? null,
    [selectedEndpointId, selectedService],
  );

  useEffect(() => {
    if (!selectedEndpoint) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      return;
    }

    if (isChatEndpoint(selectedEndpoint)) {
      setPayloadTypeUrl('');
      setPayloadBase64('');
      return;
    }

    setPayloadTypeUrl(selectedEndpoint.requestTypeUrl || '');
    setPayloadBase64('');
  }, [selectedEndpointId, selectedServiceId, selectedEndpoint]);

  const isStreaming = invokeResult.status === 'running' && invokeResult.mode === 'stream';
  const isInvoking = invokeResult.status === 'running' && invokeResult.mode === 'invoke';
  const selectedScopeId = activeDraft.scopeId.trim();

  const serviceColumns = useMemo<ProColumns<ServiceCatalogSnapshot>[]>(
    () => [
      {
        title: 'Service',
        dataIndex: 'serviceId',
        render: (_, record) => (
          <Space direction="vertical" size={4}>
            <Space wrap size={[8, 8]}>
              <Typography.Text strong>
                {record.displayName || record.serviceId}
              </Typography.Text>
              <Tag color={record.serviceId === bindingQuery.data?.serviceId ? 'processing' : 'default'}>
                {record.serviceId === bindingQuery.data?.serviceId
                  ? 'default binding'
                  : 'scope service'}
              </Tag>
            </Space>
            <Typography.Text type="secondary">
              {record.namespace} / {record.serviceId}
            </Typography.Text>
          </Space>
        ),
      },
      {
        title: 'Endpoints',
        render: (_, record) => record.endpoints.length,
      },
      {
        title: 'Deployment',
        render: (_, record) =>
          `${record.deploymentStatus || 'unknown'}${
            record.deploymentId ? ` · ${record.deploymentId}` : ''
          }`,
      },
      {
        title: 'Updated',
        render: (_, record) => formatDateTime(record.updatedAt),
      },
      {
        title: 'Action',
        valueType: 'option',
        render: (_, record) => [
          <Button
            key={`${record.serviceKey}-select`}
            type={record.serviceId === selectedServiceId ? 'primary' : 'link'}
            onClick={() => setSelectedServiceId(record.serviceId)}
          >
            {record.serviceId === selectedServiceId ? 'Selected' : 'Use'}
          </Button>,
          <Button
            key={`${record.serviceKey}-detail`}
            type="link"
            onClick={() =>
              history.push(
                buildServiceDetailHref(record.serviceId, {
                  tenantId: selectedScopeId,
                  appId: record.appId,
                  namespace: record.namespace,
                }),
              )
            }
          >
            Platform detail
          </Button>,
        ],
      },
    ],
    [bindingQuery.data?.serviceId, selectedScopeId, selectedServiceId],
  );

  const handleAbort = () => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setInvokeResult((current) => ({
      ...current,
      status: 'error',
      error: 'Invocation aborted by operator.',
    }));
  };

  const handleInvoke = async () => {
    if (!selectedScopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    abortControllerRef.current?.abort();
    abortControllerRef.current = null;

    if (isChatEndpoint(selectedEndpoint)) {
      const controller = new AbortController();
      abortControllerRef.current = controller;
      setInvokeResult({
        ...createIdleResult(),
        status: 'running',
        mode: 'stream',
        serviceId: selectedService.serviceId,
        endpointId: selectedEndpoint.endpointId,
      });

      try {
        const response = await runtimeRunsApi.streamChat(
          selectedScopeId,
          {
            prompt: prompt.trim(),
          },
          controller.signal,
          {
            serviceId: selectedService.serviceId,
          },
        );

        let assistantText = '';
        let actorId = '';
        let commandId = '';
        let runId = '';
        let eventCount = 0;
        let runError = '';
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
            assistantText += event.delta || '';
          }

          if (event.type === AGUIEventType.RUN_ERROR) {
            runError = event.message || 'Assistant run failed.';
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
            status: runError ? 'error' : 'running',
            mode: 'stream',
            serviceId: selectedService.serviceId,
            endpointId: selectedEndpoint.endpointId,
            assistantText,
            responseJson: '',
            error: runError,
            runId,
            actorId,
            commandId,
            eventCount,
            events: [...events],
          });
        }

        if (!controller.signal.aborted) {
          setInvokeResult({
            status: runError ? 'error' : 'success',
            mode: 'stream',
            serviceId: selectedService.serviceId,
            endpointId: selectedEndpoint.endpointId,
            assistantText,
            responseJson: '',
            error: runError,
            runId,
            actorId,
            commandId,
            eventCount,
            events,
          });
        }
      } catch (error) {
        if (!controller.signal.aborted) {
          setInvokeResult({
            ...createIdleResult(),
            status: 'error',
            mode: 'stream',
            serviceId: selectedService.serviceId,
            endpointId: selectedEndpoint.endpointId,
            error: error instanceof Error ? error.message : String(error),
            events: [],
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
      status: 'running',
      mode: 'invoke',
      serviceId: selectedService.serviceId,
      endpointId: selectedEndpoint.endpointId,
    });

    try {
      const response = await runtimeRunsApi.invokeEndpoint(
        selectedScopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          prompt: prompt.trim(),
          payloadTypeUrl: payloadTypeUrl.trim() || undefined,
          payloadBase64: payloadBase64.trim() || undefined,
        },
        {
          serviceId: selectedService.serviceId,
        },
      );
      const responseRunId = String(
        response.request_id ?? response.requestId ?? response.commandId ?? '',
      ).trim();
      const responseActorId = String(
        response.target_actor_id ?? response.targetActorId ?? response.actorId ?? '',
      ).trim();
      const responseCommandId = String(
        response.command_id ?? response.commandId ?? responseRunId,
      ).trim();
      const events: AGUIEvent[] = [
        {
          type: AGUIEventType.RUN_STARTED,
          runId: responseRunId || undefined,
          threadId:
            String(
              response.correlation_id ?? response.correlationId ?? responseRunId,
            ).trim() || undefined,
          timestamp: Date.now(),
        } as AGUIEvent,
      ];
      if (responseActorId || responseCommandId) {
        events.push({
          type: AGUIEventType.CUSTOM,
          name: CustomEventName.RunContext,
          value: {
            actorId: responseActorId || undefined,
            commandId: responseCommandId || undefined,
          },
          timestamp: Date.now(),
        } as AGUIEvent);
      }

      setInvokeResult({
        ...createIdleResult(),
        status: 'success',
        mode: 'invoke',
        serviceId: selectedService.serviceId,
        endpointId: selectedEndpoint.endpointId,
        responseJson: JSON.stringify(response, null, 2),
        runId: responseRunId,
        actorId: responseActorId,
        commandId: responseCommandId,
        eventCount: events.length,
        events,
      });
    } catch (error) {
      setInvokeResult({
        ...createIdleResult(),
        status: 'error',
        mode: 'invoke',
        serviceId: selectedService.serviceId,
        endpointId: selectedEndpoint.endpointId,
        error: error instanceof Error ? error.message : String(error),
        events: [],
      });
    }
  };

  const selectedEndpointOptions = useMemo(
    () =>
      (selectedService?.endpoints ?? []).map((endpoint) => ({
        label: `${endpoint.endpointId} · ${endpoint.kind || 'unknown'}`,
        value: endpoint.endpointId,
      })),
    [selectedService?.endpoints],
  );

  const handleOpenRuns = () => {
    if (!selectedScopeId) {
      return;
    }

    const observedDraftKey =
      invokeResult.events.length > 0
        ? saveObservedRunSessionPayload({
            scopeId: selectedScopeId,
            serviceOverrideId: selectedService?.serviceId,
            endpointId: invokeResult.endpointId || selectedEndpoint?.endpointId || 'chat',
            prompt,
            payloadTypeUrl:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadTypeUrl || undefined
                : undefined,
            payloadBase64:
              selectedEndpoint && !isChatEndpoint(selectedEndpoint)
                ? payloadBase64 || undefined
                : undefined,
            actorId: invokeResult.actorId || undefined,
            commandId: invokeResult.commandId || undefined,
            runId: invokeResult.runId || undefined,
            events: invokeResult.events,
          })
        : '';

    history.push(
      buildRuntimeRunsHref({
        scopeId: selectedScopeId,
        serviceId: selectedService?.serviceId,
        endpointId: selectedEndpoint?.endpointId,
        payloadTypeUrl:
          selectedEndpoint && !isChatEndpoint(selectedEndpoint)
            ? payloadTypeUrl || undefined
            : undefined,
        prompt: prompt || undefined,
        actorId: invokeResult.actorId || undefined,
        draftKey: observedDraftKey || undefined,
      }),
    );
  };

  return (
    <PageContainer
      title="Scope Service Invoke"
      content="Invoke the services already published into a scope. This stays scope-first, but exposes the real service endpoints and runtime invoke paths behind the current scope."
      onBack={() => history.push(buildScopeHref('/scopes/overview', activeDraft))}
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <ScopeQueryCard
            draft={draft}
            onChange={setDraft}
            loadLabel="Load scope services"
            resolvedScopeId={resolvedScope?.scopeId}
            resolvedScopeSource={resolvedScope?.scopeSource}
            onUseResolvedScope={() => {
              if (!resolvedScope?.scopeId) {
                return;
              }

              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope.scopeId,
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
            }}
            onLoad={() => {
              const nextDraft = normalizeScopeDraft(draft);
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
            }}
            onReset={() => {
              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope?.scopeId ?? '',
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedServiceId('');
              setSelectedEndpointId('');
              setPrompt('');
              setPayloadTypeUrl('');
              setPayloadBase64('');
              setInvokeResult(createIdleResult());
            }}
          />
        </Col>

        <Col xs={24}>
          <div
            style={{
              ...embeddedPanelStyle,
              background: 'var(--ant-color-fill-quaternary)',
            }}
          >
            <Typography.Text strong>Scope-first invoke surface</Typography.Text>
            <Typography.Paragraph
              style={{ margin: '8px 0 0' }}
              type="secondary"
            >
              This page resolves the scope to its published service identities,
              then uses the same runtime invoke contracts as the operator
              workbench. Use chat endpoints for SSE runs and non-chat endpoints
              for generic protobuf invoke.
            </Typography.Paragraph>
          </div>
        </Col>

        {!selectedScopeId ? (
          <Col xs={24}>
            <Alert
              showIcon
              type="info"
              title="Select a scope to inspect its published services and invoke endpoints."
            />
          </Col>
        ) : (
          <>
            <Col xs={24}>
              <ProCard {...moduleCardProps} title="Scope invoke summary">
                <div style={cardStackStyle}>
                  <div style={summaryMetricGridStyle}>
                    <SummaryMetric label="Scope" value={selectedScopeId} />
                    <SummaryMetric
                      label="Published services"
                      value={services.length}
                    />
                    <SummaryMetric
                      label="Default binding"
                      value={
                        bindingQuery.data?.available
                          ? bindingQuery.data.displayName || bindingQuery.data.serviceId
                          : 'Not bound'
                      }
                    />
                    <SummaryMetric
                      label="Selected endpoint"
                      value={selectedEndpoint?.endpointId || 'n/a'}
                    />
                  </div>
                  <Space wrap>
                    <Button
                      onClick={() =>
                        history.push(
                          buildRuntimeRunsHref({
                            scopeId: selectedScopeId,
                          }),
                        )
                      }
                    >
                      Open Runs Workbench
                    </Button>
                    <Button
                      onClick={() =>
                        history.push(
                          buildScopeHref('/scopes/overview', activeDraft),
                        )
                      }
                    >
                      Back to Scope Overview
                    </Button>
                  </Space>
                </div>
              </ProCard>
            </Col>

            <Col xs={24} xl={11}>
              <ProTable<ServiceCatalogSnapshot>
                columns={serviceColumns}
                dataSource={services}
                loading={scopeServicesQuery.isLoading}
                rowKey="serviceKey"
                search={false}
                pagination={false}
                cardProps={compactTableCardProps}
                toolBarRender={false}
                headerTitle="Scope Services"
              />
            </Col>

            <Col xs={24} xl={13}>
              <ProCard
                {...moduleCardProps}
                title={
                  selectedService
                    ? `Invoke ${selectedService.displayName || selectedService.serviceId}`
                    : 'Invoke service'
                }
                loading={scopeServicesQuery.isLoading}
              >
                {!selectedService ? (
                  <Alert
                    showIcon
                    type="info"
                    title="No scope services are available yet."
                    description="Publish or bind a scope service first, then return here to invoke it."
                  />
                ) : (
                  <div style={cardStackStyle}>
                    <div style={summaryFieldGridStyle}>
                      <SummaryField
                        label="Service key"
                        value={
                          <Typography.Text copyable>
                            {selectedService.serviceKey}
                          </Typography.Text>
                        }
                      />
                      <SummaryField
                        label="Deployment"
                        value={`${selectedService.deploymentStatus || 'unknown'}${
                          selectedService.deploymentId
                            ? ` · ${selectedService.deploymentId}`
                            : ''
                        }`}
                      />
                      <SummaryField
                        label="Active revision"
                        value={
                          selectedService.activeServingRevisionId ||
                          selectedService.defaultServingRevisionId ||
                          'n/a'
                        }
                      />
                      <SummaryField
                        label="Primary actor"
                        value={
                          selectedService.primaryActorId ? (
                            <Typography.Text copyable>
                              {selectedService.primaryActorId}
                            </Typography.Text>
                          ) : (
                            'n/a'
                          )
                        }
                      />
                    </div>

                    <div>
                      <Typography.Text style={summaryFieldLabelStyle}>
                        Endpoint
                      </Typography.Text>
                      <Select
                        aria-label="Endpoint"
                        style={{ display: 'block', marginTop: 8, width: '100%' }}
                        options={selectedEndpointOptions}
                        value={selectedEndpointId || undefined}
                        onChange={(value) => setSelectedEndpointId(value)}
                      />
                      <Typography.Paragraph
                        style={{ margin: '8px 0 0' }}
                        type="secondary"
                      >
                        {selectedEndpoint?.description ||
                          'Select a published endpoint for this scope service.'}
                      </Typography.Paragraph>
                    </div>

                    <div>
                      <Typography.Text style={summaryFieldLabelStyle}>
                        {selectedEndpoint && isChatEndpoint(selectedEndpoint)
                          ? 'Prompt'
                          : 'Payload text'}
                      </Typography.Text>
                      <Input.TextArea
                        placeholder="Describe the request or payload text."
                        rows={5}
                        style={{ marginTop: 8 }}
                        value={prompt}
                        onChange={(event) => setPrompt(event.target.value)}
                      />
                    </div>

                    {selectedEndpoint && !isChatEndpoint(selectedEndpoint) ? (
                      <>
                        <div>
                          <Typography.Text style={summaryFieldLabelStyle}>
                            Payload type URL
                          </Typography.Text>
                          <Input
                            placeholder="type.googleapis.com/google.protobuf.StringValue"
                            style={{ marginTop: 8 }}
                            value={payloadTypeUrl}
                            onChange={(event) => setPayloadTypeUrl(event.target.value)}
                          />
                        </div>
                        <div>
                          <Typography.Text style={summaryFieldLabelStyle}>
                            Payload base64 (advanced)
                          </Typography.Text>
                          <Input.TextArea
                            placeholder="Leave empty only when the endpoint accepts StringValue or AppScriptCommand."
                            rows={3}
                            style={{ marginTop: 8 }}
                            value={payloadBase64}
                            onChange={(event) => setPayloadBase64(event.target.value)}
                          />
                        </div>
                      </>
                    ) : (
                      <Alert
                        showIcon
                        type="info"
                        title="Chat endpoints run as SSE streams."
                        description="This path uses the published scope service and will stream assistant output plus run context in real time."
                      />
                    )}

                    <Space wrap>
                      <Button
                        type="primary"
                        loading={isStreaming || isInvoking}
                        disabled={!selectedEndpointId}
                        onClick={() => void handleInvoke()}
                      >
                        {selectedEndpoint && isChatEndpoint(selectedEndpoint)
                          ? 'Stream chat'
                          : 'Invoke endpoint'}
                      </Button>
                      <Button disabled={!isStreaming} onClick={handleAbort}>
                        Abort stream
                      </Button>
                      <Button onClick={handleOpenRuns}>
                        Open in Runs
                      </Button>
                    </Space>

                    {invokeResult.status !== 'idle' ? (
                      <div style={cardStackStyle}>
                        <Alert
                          showIcon
                          type={
                            invokeResult.status === 'error'
                              ? 'error'
                              : invokeResult.status === 'success'
                                ? 'success'
                                : 'info'
                          }
                          title={`${
                            invokeResult.mode === 'stream'
                              ? 'Streaming invoke'
                              : 'Generic invoke'
                          } · ${invokeResult.serviceId || selectedService.serviceId} / ${
                            invokeResult.endpointId || selectedEndpointId || 'endpoint'
                          }`}
                          description={
                            invokeResult.error ||
                            (invokeResult.status === 'running'
                              ? 'Invocation in progress.'
                              : 'Invocation completed.')
                          }
                        />

                        <div style={summaryFieldGridStyle}>
                          <SummaryField
                            label="Run ID"
                            value={
                              invokeResult.runId ? (
                                <Typography.Text copyable>
                                  {invokeResult.runId}
                                </Typography.Text>
                              ) : (
                                'n/a'
                              )
                            }
                          />
                          <SummaryField
                            label="Actor ID"
                            value={
                              invokeResult.actorId ? (
                                <Typography.Text copyable>
                                  {invokeResult.actorId}
                                </Typography.Text>
                              ) : (
                                'n/a'
                              )
                            }
                          />
                          <SummaryField
                            label="Command ID"
                            value={
                              invokeResult.commandId ? (
                                <Typography.Text copyable>
                                  {invokeResult.commandId}
                                </Typography.Text>
                              ) : (
                                'n/a'
                              )
                            }
                          />
                          <SummaryField
                            label="Observed events"
                            value={invokeResult.eventCount}
                          />
                        </div>

                        {invokeResult.assistantText ? (
                          <div>
                            <Typography.Text style={summaryFieldLabelStyle}>
                              Assistant stream
                            </Typography.Text>
                            <pre style={codeBlockStyle}>{invokeResult.assistantText}</pre>
                          </div>
                        ) : null}

                        {invokeResult.responseJson ? (
                          <div>
                            <Typography.Text style={summaryFieldLabelStyle}>
                              Invoke receipt
                            </Typography.Text>
                            <pre style={codeBlockStyle}>{invokeResult.responseJson}</pre>
                          </div>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                )}
              </ProCard>
            </Col>
          </>
        )}
      </Row>
    </PageContainer>
  );
};

export default ScopeInvokePage;
