import { ApiOutlined, CheckCircleOutlined, CopyOutlined, LinkOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Empty, Input, Select, Space, Tag, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { history } from '@/shared/navigation/history';
import type {
  ScopeServiceBindingCatalogSnapshot,
} from '@/shared/models/runtime/scopeServices';
import type {
  ServiceCatalogSnapshot,
} from '@/shared/models/services';
import {
  scopeServiceAppId,
  scopeServiceNamespace,
  isChatServiceEndpoint,
} from '@/shared/runs/scopeConsole';
import {
  describeScopeServiceBindingTarget,
} from '@/shared/models/runtime/scopeServices';
import {
  type StudioAuthSession,
  type StudioScopeBindingStatus,
} from '@/shared/studio/models';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import {
  buildStudioBindContract,
  type StudioBindContract,
} from './bindContract';
import {
  buildCurlSnippet,
  buildFetchSnippet,
  buildSdkSnippet,
  createDefaultBindSampleInput,
} from './bindSnippets';

type StudioMemberBindPanelProps = {
  readonly initialEndpointId?: string;
  readonly initialServiceId?: string;
  readonly onContinueToInvoke?: (serviceId: string, endpointId: string) => void;
  readonly onSelectionChange?: (selection: {
    serviceId: string;
    endpointId: string;
  }) => void;
  readonly preferredServiceId?: string;
  readonly authSession?: StudioAuthSession | null;
  readonly scopeBinding?: StudioScopeBindingStatus | null;
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
};

type SnippetTab = 'curl' | 'fetch' | 'sdk';

type SmokeTestResult = {
  readonly error: string;
  readonly eventCount: number;
  readonly latencyMs: number;
  readonly responseSummary: string;
  readonly runId: string;
  readonly status: 'idle' | 'running' | 'success' | 'error';
};

const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";

const rootStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
  minWidth: 0,
  overflow: 'auto',
};

const controlsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr) auto',
};

const bindBodyGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(320px, 380px)',
  minHeight: 0,
};

const parameterGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
};

const valueCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 12,
  display: 'grid',
  gap: 4,
  minWidth: 0,
  padding: 12,
};

const snippetHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
};

const snippetTabsStyle: React.CSSProperties = {
  display: 'inline-flex',
  gap: 4,
};

const snippetTabButtonStyle: React.CSSProperties = {
  border: '1px solid #d9d9d9',
  borderRadius: 999,
  fontSize: 12,
  fontWeight: 600,
  padding: '6px 12px',
};

const snippetBlockStyle: React.CSSProperties = {
  background: '#0f172a',
  borderRadius: 14,
  color: '#e2e8f0',
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  overflowX: 'auto',
  padding: 14,
  whiteSpace: 'pre-wrap',
};

const listColumnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const compactCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 12,
  display: 'grid',
  gap: 8,
  padding: 12,
};

const sidePanelStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
};

const smokeInputStyle: React.CSSProperties = {
  fontFamily: monoFontFamily,
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function buildScopedServiceCatalogHref(
  scopeId: string,
  service: Pick<
    ServiceCatalogSnapshot,
    'appId' | 'namespace' | 'serviceId' | 'tenantId'
  >,
): string {
  const params = new URLSearchParams();
  params.set('tenantId', trimOptional(service.tenantId) || scopeId);
  params.set('appId', trimOptional(service.appId) || scopeServiceAppId);
  params.set('namespace', trimOptional(service.namespace) || scopeServiceNamespace);
  params.set('serviceId', trimOptional(service.serviceId));
  return `/services?${params.toString()}`;
}

function createIdleSmokeTestResult(): SmokeTestResult {
  return {
    error: '',
    eventCount: 0,
    latencyMs: 0,
    responseSummary: '',
    runId: '',
    status: 'idle',
  };
}

function copyText(value: string): Promise<void> | void {
  if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
    return;
  }

  return navigator.clipboard.writeText(value);
}

function formatDeploymentTag(value: string): string {
  const normalized = trimOptional(value).toLowerCase();
  if (!normalized) {
    return 'draft';
  }

  if (normalized === 'active') {
    return 'live';
  }

  return normalized;
}

function buildBindingSectionTitle(count: number): string {
  return count === 1 ? 'Bound dependency' : `Bound dependencies (${count})`;
}

const StudioMemberBindPanel: React.FC<StudioMemberBindPanelProps> = ({
  scopeId,
  scopeBinding,
  services,
  initialServiceId,
  initialEndpointId,
  preferredServiceId,
  onSelectionChange,
  onContinueToInvoke,
  authSession,
}) => {
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    trimOptional(initialServiceId),
  );
  const [selectedEndpointId, setSelectedEndpointId] = useState(() =>
    trimOptional(initialEndpointId),
  );
  const [snippetTab, setSnippetTab] = useState<SnippetTab>('curl');
  const [smokeInput, setSmokeInput] = useState('');
  const [smokeTestResult, setSmokeTestResult] = useState<SmokeTestResult>(
    createIdleSmokeTestResult(),
  );

  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const selectedEndpoint =
    selectedService?.endpoints.find(
      (endpoint) => endpoint.endpointId === selectedEndpointId,
    ) ?? null;

  useEffect(() => {
    if (!services.length) {
      setSelectedServiceId('');
      return;
    }

    const normalizedInitialServiceId = trimOptional(initialServiceId);
    if (
      normalizedInitialServiceId &&
      services.some((service) => service.serviceId === normalizedInitialServiceId)
    ) {
      setSelectedServiceId((current) =>
        current === normalizedInitialServiceId ? current : normalizedInitialServiceId,
      );
      return;
    }

    const normalizedPreferredServiceId = trimOptional(preferredServiceId);
    if (
      normalizedPreferredServiceId &&
      services.some((service) => service.serviceId === normalizedPreferredServiceId)
    ) {
      setSelectedServiceId((current) =>
        current === normalizedPreferredServiceId
          ? current
          : normalizedPreferredServiceId,
      );
      return;
    }

    setSelectedServiceId((current) =>
      current && services.some((service) => service.serviceId === current)
        ? current
        : services[0]?.serviceId || '',
    );
  }, [initialServiceId, preferredServiceId, services]);

  useEffect(() => {
    if (!selectedService) {
      setSelectedEndpointId('');
      return;
    }

    const normalizedInitialServiceId = trimOptional(initialServiceId);
    const normalizedInitialEndpointId = trimOptional(initialEndpointId);
    if (
      normalizedInitialServiceId === selectedService.serviceId &&
      normalizedInitialEndpointId &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === normalizedInitialEndpointId,
      )
    ) {
      setSelectedEndpointId((current) =>
        current === normalizedInitialEndpointId ? current : normalizedInitialEndpointId,
      );
      return;
    }

    setSelectedEndpointId((current) =>
      current &&
      selectedService.endpoints.some((endpoint) => endpoint.endpointId === current)
        ? current
        : selectedService.endpoints[0]?.endpointId || '',
    );
  }, [initialEndpointId, initialServiceId, selectedService]);

  useEffect(() => {
    if (!selectedService || !selectedEndpointId) {
      return;
    }

    onSelectionChange?.({
      serviceId: selectedService.serviceId,
      endpointId: selectedEndpointId,
    });
  }, [onSelectionChange, selectedEndpointId, selectedService]);

  const bindingsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ['studio-bind', 'bindings', scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.getServiceBindings(scopeId, selectedService?.serviceId || ''),
  });

  const bindContract = useMemo<StudioBindContract | null>(
    () =>
      buildStudioBindContract({
        authSession,
        endpoint: selectedEndpoint,
        scopeBinding,
        scopeId,
        service: selectedService,
      }),
    [authSession, scopeBinding, scopeId, selectedEndpoint, selectedService],
  );

  useEffect(() => {
    const nextDefaultInput = createDefaultBindSampleInput(bindContract);
    setSmokeInput((current) => (current ? current : nextDefaultInput));
    setSmokeTestResult(createIdleSmokeTestResult());
  }, [bindContract?.endpointId, bindContract?.serviceId]);

  const handleRunSmokeTest = useCallback(async () => {
    if (!scopeId || !selectedService || !selectedEndpoint) {
      return;
    }

    const startedAt = Date.now();
    setSmokeTestResult({
      ...createIdleSmokeTestResult(),
      status: 'running',
    });

    try {
      if (isChatServiceEndpoint(selectedEndpoint)) {
        const accumulator = createRuntimeEventAccumulator();
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
            prompt: smokeInput.trim() || createDefaultBindSampleInput(bindContract),
          },
          new AbortController().signal,
          {
            serviceId: selectedService.serviceId,
          },
        );

        for await (const event of parseBackendSSEStream(response, {})) {
          applyRuntimeEvent(accumulator, event);
        }

        setSmokeTestResult({
          error: accumulator.errorText,
          eventCount: accumulator.events.length,
          latencyMs: Date.now() - startedAt,
          responseSummary:
            accumulator.errorText ||
            accumulator.assistantText ||
            'Model returned an empty response.',
          runId: accumulator.runId,
          status: accumulator.errorText ? 'error' : 'success',
        });
        return;
      }

      const response = await runtimeRunsApi.invokeEndpoint(
        scopeId,
        {
          endpointId: selectedEndpoint.endpointId,
          prompt: smokeInput.trim() || createDefaultBindSampleInput(bindContract),
        },
        {
          serviceId: selectedService.serviceId,
        },
      );

      setSmokeTestResult({
        error: '',
        eventCount: 0,
        latencyMs: Date.now() - startedAt,
        responseSummary: JSON.stringify(response, null, 2),
        runId: trimOptional(String(response.request_id || response.requestId || '')),
        status: 'success',
      });
    } catch (error) {
      setSmokeTestResult({
        error: error instanceof Error ? error.message : String(error),
        eventCount: 0,
        latencyMs: Date.now() - startedAt,
        responseSummary: '',
        runId: '',
        status: 'error',
      });
    }
  }, [bindContract, scopeId, selectedEndpoint, selectedService, smokeInput]);

  const serviceOptions = useMemo(
    () =>
      services.map((service) => ({
        label: service.displayName || service.serviceId,
        value: service.serviceId,
      })),
    [services],
  );

  const endpointOptions = useMemo(
    () =>
      (selectedService?.endpoints ?? []).map((endpoint) => ({
        label: endpoint.displayName || endpoint.endpointId,
        value: endpoint.endpointId,
      })),
    [selectedService?.endpoints],
  );

  const snippetMap = useMemo(() => {
    if (!bindContract) {
      return {
        curl: '',
        fetch: '',
        sdk: '',
      };
    }

    return {
      curl: buildCurlSnippet(bindContract, smokeInput),
      fetch: buildFetchSnippet(bindContract, smokeInput),
      sdk: buildSdkSnippet(bindContract, smokeInput),
    };
  }, [bindContract, smokeInput]);

  const selectedSnippet = snippetMap[snippetTab];
  const bindingCatalog: ScopeServiceBindingCatalogSnapshot | undefined = bindingsQuery.data;
  const bindingList = bindingCatalog?.bindings ?? [];

  if (!scopeId) {
    return (
      <Alert
        showIcon
        message="Resolve a team scope before binding this member."
        type="info"
      />
    );
  }

  if (!services.length) {
    return (
      <div data-testid="studio-bind-surface" style={rootStyle}>
        <Alert
          showIcon
          message="No published member services are available in this scope yet."
          description="Finish binding a workflow, script, or gagent revision first."
          type="warning"
        />
      </div>
    );
  }

  return (
    <div data-testid="studio-bind-surface" style={rootStyle}>
      <div style={bindBodyGridStyle}>
        <div style={listColumnStyle}>
          <AevatarPanel
            title="Invoke URL"
            titleHelp="This is the contract users reach first, so keep the URL, auth, revision, and stream posture visible together."
            extra={
              <Button
                icon={<CopyOutlined />}
                onClick={() => void copyText(bindContract?.invokeUrl || '')}
              >
                Copy URL
              </Button>
            }
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={controlsGridStyle}>
                <div style={{ display: 'grid', gap: 8 }}>
                  <Typography.Text strong>Published service</Typography.Text>
                  <Select
                    options={serviceOptions}
                    placeholder="Select a published service"
                    value={selectedServiceId || undefined}
                    onChange={(value) => {
                      setSelectedServiceId(String(value || ''));
                      setSelectedEndpointId('');
                    }}
                  />
                </div>
                <div style={{ display: 'grid', gap: 8 }}>
                  <Typography.Text strong>Endpoint</Typography.Text>
                  <Select
                    disabled={!selectedService}
                    options={endpointOptions}
                    placeholder="Select an endpoint"
                    value={selectedEndpointId || undefined}
                    onChange={(value) => setSelectedEndpointId(String(value || ''))}
                  />
                </div>
                <div
                  style={{
                    alignItems: 'flex-end',
                    display: 'flex',
                    justifyContent: 'flex-end',
                  }}
                >
                  <Button
                    icon={<ApiOutlined />}
                    disabled={!selectedService}
                    onClick={() => {
                      if (!selectedService) {
                        return;
                      }

                      history.push(buildScopedServiceCatalogHref(scopeId, selectedService));
                    }}
                  >
                    Open Services
                  </Button>
                </div>
              </div>
              {bindContract ? (
                <>
                  <div
                    data-testid="studio-bind-contract-card"
                    style={{
                      alignItems: 'stretch',
                      border: '1px solid #d9d9d9',
                      borderRadius: 12,
                      display: 'flex',
                      overflow: 'hidden',
                    }}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        background: '#fafafa',
                        borderRight: '1px solid #d9d9d9',
                        color: '#6b7280',
                        display: 'flex',
                        fontFamily: monoFontFamily,
                        fontSize: 12,
                        fontWeight: 700,
                        justifyContent: 'center',
                        minWidth: 74,
                        padding: '0 14px',
                      }}
                    >
                      {bindContract.method}
                    </div>
                    <div
                      style={{
                        color: '#111827',
                        flex: 1,
                        fontFamily: monoFontFamily,
                        fontSize: 12.5,
                        minWidth: 0,
                        overflowX: 'auto',
                        padding: '12px 14px',
                        whiteSpace: 'nowrap',
                      }}
                    >
                      {bindContract.invokeUrl}
                    </div>
                  </div>
                  <Space wrap size={[8, 8]}>
                    <Tag color="blue">{formatDeploymentTag(bindContract.deploymentStatus)}</Tag>
                    <Tag>auth · {bindContract.authLabel}</Tag>
                    <Tag>scope · {bindContract.scopeLabel}</Tag>
                    <Tag>revision · {bindContract.revisionId}</Tag>
                    {bindContract.streaming.sse ? (
                      <Tag color="gold">stream · text/event-stream</Tag>
                    ) : (
                      <Tag>response · application/json</Tag>
                    )}
                    {bindContract.streaming.aguiFrames ? (
                      <Tag color="geekblue">AGUI frames</Tag>
                    ) : null}
                  </Space>
                  <Typography.Text type="secondary">
                    {bindContract.endpointDescription}{' '}
                    {bindContract.authHint}
                  </Typography.Text>
                </>
              ) : (
                <Empty
                  description="Select a published service endpoint to inspect its invoke contract."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </div>
          </AevatarPanel>

          <AevatarPanel
            title="Binding parameters"
            titleHelp="Only keep the details that actually change how this member is invoked or validated."
          >
            {bindContract ? (
              <div style={parameterGridStyle}>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Scope</Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {bindContract.scopeLabel}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {bindContract.scopeSource
                      ? `Resolved from ${bindContract.scopeSource}.`
                      : 'Bound to the current Studio scope.'}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Authorization</Typography.Text>
                  <Typography.Text strong>{bindContract.authLabel}</Typography.Text>
                  <Typography.Text type="secondary">{bindContract.authHint}</Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Revision</Typography.Text>
                  <Typography.Text strong>{bindContract.revisionId}</Typography.Text>
                  <Typography.Text type="secondary">
                    {bindContract.serviceDisplayName}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Delivery</Typography.Text>
                  <Typography.Text strong>{bindContract.method}</Typography.Text>
                  <Typography.Text type="secondary">
                    {bindContract.streaming.sse
                      ? 'Streams through text/event-stream.'
                      : 'Returns a single JSON response.'}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Streaming</Typography.Text>
                  <Space wrap size={[6, 6]}>
                    <Tag color={bindContract.streaming.sse ? 'blue' : 'default'}>SSE</Tag>
                    <Tag color={bindContract.streaming.webSocket ? 'blue' : 'default'}>
                      WebSocket
                    </Tag>
                    <Tag
                      color={bindContract.streaming.aguiFrames ? 'geekblue' : 'default'}
                    >
                      AGUI
                    </Tag>
                  </Space>
                </div>
                {bindContract.requestTypeUrl ? (
                  <div style={valueCardStyle}>
                    <Typography.Text type="secondary">Request schema</Typography.Text>
                    <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                      {bindContract.requestTypeUrl}
                    </Typography.Text>
                  </div>
                ) : null}
                {bindContract.responseTypeUrl ? (
                  <div style={valueCardStyle}>
                    <Typography.Text type="secondary">Response schema</Typography.Text>
                    <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                      {bindContract.responseTypeUrl}
                    </Typography.Text>
                  </div>
                ) : null}
              </div>
            ) : (
              <Empty
                description="Select a contract to review its parameters."
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>

          <AevatarPanel
            title="Snippets"
            titleHelp="Copy these examples directly into your external integration or keep iterating inside Studio Invoke."
          >
            {bindContract ? (
              <div style={{ display: 'grid', gap: 12 }}>
                <div style={snippetHeaderStyle}>
                  <div style={snippetTabsStyle}>
                    {(['curl', 'fetch', 'sdk'] as SnippetTab[]).map((tabKey) => (
                      <button
                        key={tabKey}
                        type="button"
                        style={{
                          ...snippetTabButtonStyle,
                          background: snippetTab === tabKey ? '#111827' : '#ffffff',
                          borderColor: snippetTab === tabKey ? '#111827' : '#d9d9d9',
                          color: snippetTab === tabKey ? '#ffffff' : '#111827',
                        }}
                        onClick={() => setSnippetTab(tabKey)}
                      >
                        {tabKey.toUpperCase()}
                      </button>
                    ))}
                  </div>
                  <Button
                    icon={<CopyOutlined />}
                    onClick={() => void copyText(selectedSnippet)}
                  >
                    Copy snippet
                  </Button>
                </div>
                <pre style={snippetBlockStyle}>{selectedSnippet}</pre>
              </div>
            ) : (
              <Empty
                description="Select a contract to generate snippets."
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>

          <AevatarPanel
            title={buildBindingSectionTitle(bindingList.length)}
            titleHelp="These are extra scope-level dependencies, such as connectors, secrets, or other services, that this member needs at runtime."
          >
            {bindingsQuery.isLoading ? (
              <Typography.Text type="secondary">Loading bindings...</Typography.Text>
            ) : bindingList.length > 0 ? (
              <div style={listColumnStyle}>
                {bindingList.map((binding) => (
                  <div key={binding.bindingId} style={compactCardStyle}>
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>
                        {binding.displayName || binding.bindingId}
                      </Typography.Text>
                      <AevatarStatusTag
                        domain="governance"
                        label={binding.bindingKind}
                        status={binding.retired ? 'retired' : 'active'}
                      />
                    </Space>
                    <Typography.Text type="secondary">
                      Target {describeScopeServiceBindingTarget(binding)}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      Policies{' '}
                      {binding.policyIds.length > 0 ? binding.policyIds.join(', ') : 'none'}
                    </Typography.Text>
                  </div>
                ))}
              </div>
            ) : (
              <Empty
                description="This service does not depend on any extra connectors, secrets, or service bindings in the current scope."
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>
        </div>

        <div style={sidePanelStyle}>
          <AevatarPanel
            title="Smoke-test"
            titleHelp="Use a light contract check here, then move into Invoke for the full transcript and event stream."
          >
            <div data-testid="studio-bind-smoke-test" style={{ display: 'grid', gap: 12 }}>
              <Alert
                showIcon
                message="Authorization"
                description={
                  bindContract?.authAuthenticated
                    ? `${bindContract.authHint} In-browser Studio requests attach the active bearer session automatically.`
                    : bindContract?.authEnabled
                      ? `${bindContract?.authHint} Sign in before running a smoke test.`
                      : bindContract?.authHint || 'Studio auth is not enabled for this environment.'
                }
                type={
                  bindContract?.authEnabled && !bindContract?.authAuthenticated
                    ? 'warning'
                    : 'info'
                }
              />
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>
                  {selectedEndpoint && isChatServiceEndpoint(selectedEndpoint)
                    ? 'Prompt'
                    : 'Prompt / command input'}
                </Typography.Text>
                <Input.TextArea
                  aria-label="Bind smoke test input"
                  autoSize={{ minRows: 5, maxRows: 10 }}
                  placeholder={
                    selectedEndpoint && isChatServiceEndpoint(selectedEndpoint)
                      ? 'Ask the selected member to do a quick task...'
                      : 'Enter a quick smoke test input. Use Invoke for typed payload debugging.'
                  }
                  style={smokeInputStyle}
                  value={smokeInput}
                  onChange={(event) => setSmokeInput(event.target.value)}
                />
              </div>
              {bindContract?.requestTypeUrl &&
              !isChatServiceEndpoint(selectedEndpoint) ? (
                <Alert
                  showIcon
                  message="Typed payload endpoint"
                  description={`Request type: ${bindContract.requestTypeUrl}. Use Invoke when you need a custom protobuf payload.`}
                  type="warning"
                />
              ) : null}
              <Space direction="vertical" size={10} style={{ width: '100%' }}>
                <Button
                  block
                  icon={<CheckCircleOutlined />}
                  loading={smokeTestResult.status === 'running'}
                  type="primary"
                  disabled={
                    !selectedService ||
                    !selectedEndpoint ||
                    Boolean(bindContract?.authEnabled && !bindContract?.authAuthenticated)
                  }
                  onClick={() => void handleRunSmokeTest()}
                >
                  Send test request
                </Button>
                <Button
                  block
                  icon={<LinkOutlined />}
                  disabled={!selectedService || !selectedEndpoint}
                  onClick={() => {
                    if (!selectedService || !selectedEndpoint) {
                      return;
                    }

                    onContinueToInvoke?.(
                      selectedService.serviceId,
                      selectedEndpoint.endpointId,
                    );
                  }}
                >
                  Continue to Invoke
                </Button>
              </Space>
              {smokeTestResult.status === 'success' ? (
                <Alert
                  showIcon
                  message={`Smoke test passed in ${smokeTestResult.latencyMs}ms`}
                  description={
                    smokeTestResult.runId
                      ? `Run ${smokeTestResult.runId}`
                      : 'The selected contract accepted the request.'
                  }
                  type="success"
                />
              ) : smokeTestResult.status === 'error' ? (
                <Alert
                  showIcon
                  message="Smoke test failed"
                  description={smokeTestResult.error}
                  type="error"
                />
              ) : null}
              {smokeTestResult.responseSummary ? (
                bindContract?.streaming.sse ? (
                  <div style={{ display: 'grid', gap: 10 }}>
                    <Typography.Text strong>
                      Streaming summary
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {smokeTestResult.eventCount} observed events
                    </Typography.Text>
                    <div
                      style={{
                        background: '#ffffff',
                        border: '1px solid #eef2f7',
                        borderRadius: 12,
                        color: '#111827',
                        fontSize: 13,
                        lineHeight: 1.7,
                        padding: 12,
                        whiteSpace: 'pre-wrap',
                        wordBreak: 'break-word',
                      }}
                    >
                      {smokeTestResult.responseSummary}
                    </div>
                  </div>
                ) : (
                  <div style={{ display: 'grid', gap: 10 }}>
                    <Typography.Text strong>Response summary</Typography.Text>
                    <pre style={{ ...snippetBlockStyle, margin: 0 }}>
                      {smokeTestResult.responseSummary}
                    </pre>
                  </div>
                )
              ) : null}
            </div>
          </AevatarPanel>
        </div>
      </div>
    </div>
  );
};

export default StudioMemberBindPanel;
