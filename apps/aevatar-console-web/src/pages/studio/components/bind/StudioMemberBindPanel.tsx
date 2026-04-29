import { CheckCircleOutlined, CopyOutlined, LinkOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Collapse, Empty, Input, Select, Space, Tag, Typography, message } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import type {
  ScopeServiceBindingCatalogSnapshot,
} from '@/shared/models/runtime/scopeServices';
import type {
  ServiceCatalogSnapshot,
} from '@/shared/models/services';
import { isChatServiceEndpoint } from '@/shared/runs/scopeConsole';
import {
  describeScopeServiceBindingTarget,
  getScopeServiceCurrentRevision,
} from '@/shared/models/runtime/scopeServices';
import {
  describeStudioMemberBindingRevisionContext,
  describeStudioMemberBindingRevisionTarget,
  formatStudioMemberBindingImplementationKind,
  getStudioMemberBindingCurrentRevision,
  type StudioAuthSession,
} from '@/shared/studio/models';
import { studioApi } from '@/shared/studio/api';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { AEVATAR_INTERACTIVE_CHIP_CLASS } from '@/shared/ui/interactionStandards';
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
  readonly buildWorkflowYamls?: (() => Promise<string[]>) | null;
  readonly initialEndpointId?: string;
  readonly memberId?: string;
  readonly initialServiceId?: string;
  readonly onContinueToInvoke?: (serviceId: string, endpointId: string) => void;
  readonly onBindPendingCandidate?: (() => Promise<void>) | null;
  readonly onSelectionChange?: (selection: {
    serviceId: string;
    endpointId: string;
  }) => void;
  readonly pendingBindingCandidate?: {
    readonly kind: 'workflow' | 'script' | 'gagent';
    readonly displayName: string;
    readonly description: string;
    readonly actionLabel: string;
  } | null;
  readonly preferredServiceId?: string;
  readonly authSession?: StudioAuthSession | null;
  readonly servicesLoading?: boolean;
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
  minWidth: 0,
  width: '100%',
};

const controlsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr) auto',
};

const pageFlowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  width: '100%',
};

const contractSectionStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
};

const workflowGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 360px), 1fr))',
  alignItems: 'start',
  width: '100%',
};

const parameterGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
};

const surfaceCardStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #eef2f7',
  borderRadius: 12,
};

const valueCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  display: 'grid',
  gap: 3,
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
  padding: '6px 10px',
};

const snippetBlockStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e5e7eb',
  borderRadius: 12,
  color: '#0f172a',
  fontFamily: monoFontFamily,
  fontSize: 12.5,
  lineHeight: 1.65,
  margin: 0,
  overflowX: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
};

const snippetPreviewStyle: React.CSSProperties = {
  ...snippetBlockStyle,
};

const workflowSectionStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
};

const listColumnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const compactCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  display: 'grid',
  gap: 6,
  padding: 12,
};

const smokeInputStyle: React.CSSProperties = {
  fontFamily: monoFontFamily,
};

const revisionCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  display: 'grid',
  gap: 6,
  padding: 12,
};

const supportingSectionStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function formatDateTime(value: string | null | undefined): string {
  const normalized = trimOptional(value);
  if (!normalized) {
    return 'n/a';
  }

  const date = new Date(normalized);
  if (Number.isNaN(date.getTime())) {
    return normalized;
  }

  return date.toLocaleString();
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

function buildBindingSectionTitle(count: number): string {
  return count === 1 ? 'Bound dependency' : `Bound dependencies (${count})`;
}

const StudioMemberBindPanel: React.FC<StudioMemberBindPanelProps> = ({
  buildWorkflowYamls,
  scopeId,
  services,
  memberId,
  initialServiceId,
  initialEndpointId,
  preferredServiceId,
  onSelectionChange,
  onContinueToInvoke,
  onBindPendingCandidate,
  pendingBindingCandidate,
  authSession,
  servicesLoading,
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
  const [pendingBindBusy, setPendingBindBusy] = useState(false);
  const [pendingBindNotice, setPendingBindNotice] = useState<{
    readonly message: string;
    readonly type: 'success' | 'error';
  } | null>(null);
  const runsCurrentWorkflowDraft = Boolean(buildWorkflowYamls);
  const normalizedMemberId = trimOptional(memberId);

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
  const revisionsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ['studio-bind', 'revisions', scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.getServiceRevisions(scopeId, selectedService?.serviceId || ''),
  });
  const memberBindingStatusQuery = useQuery({
    enabled: Boolean(scopeId && normalizedMemberId && selectedService?.serviceId),
    queryKey: ['studio-bind', 'member-binding', scopeId, normalizedMemberId],
    queryFn: () => studioApi.getMemberBinding(scopeId, normalizedMemberId),
  });
  const revisionCatalogQuery = normalizedMemberId
    ? memberBindingStatusQuery
    : revisionsQuery;
  const currentPublishedRevision = useMemo(
    () =>
      normalizedMemberId
        ? getStudioMemberBindingCurrentRevision(memberBindingStatusQuery.data)
        : getScopeServiceCurrentRevision(revisionsQuery.data),
    [memberBindingStatusQuery.data, normalizedMemberId, revisionsQuery.data],
  );

  const bindContract = useMemo<StudioBindContract | null>(
    () =>
      buildStudioBindContract({
        authSession,
        endpoint: selectedEndpoint,
        memberId: normalizedMemberId || undefined,
        revision: currentPublishedRevision,
        scopeId,
        service: selectedService,
      }),
    [
      authSession,
      currentPublishedRevision,
      scopeId,
      selectedEndpoint,
      selectedService,
    ],
  );
  const publishedSmokeRequiresAuth =
    !runsCurrentWorkflowDraft &&
    Boolean(bindContract?.authEnabled && !bindContract.authAuthenticated);

  useEffect(() => {
    const nextDefaultInput = createDefaultBindSampleInput(bindContract);
    setSmokeInput((current) => (current ? current : nextDefaultInput));
    setSmokeTestResult(createIdleSmokeTestResult());
  }, [bindContract?.endpointId, bindContract?.serviceId]);

  const handleRunSmokeTest = useCallback(async () => {
    if (!scopeId) {
      return;
    }

    const startedAt = Date.now();
    setSmokeTestResult({
      ...createIdleSmokeTestResult(),
      status: 'running',
    });

    try {
      if (buildWorkflowYamls) {
        const accumulator = createRuntimeEventAccumulator();
        const response = await runtimeRunsApi.streamDraftRun(
          scopeId,
          {
            prompt: smokeInput.trim() || createDefaultBindSampleInput(bindContract),
            workflowYamls: await buildWorkflowYamls(),
          },
          new AbortController().signal,
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
            accumulator.finalOutput ||
            accumulator.assistantText ||
            'Model returned an empty response.',
          runId: accumulator.runId,
          status: accumulator.errorText ? 'error' : 'success',
        });
        return;
      }

      if (!selectedService || !selectedEndpoint) {
        return;
      }

      if (isChatServiceEndpoint(selectedEndpoint)) {
        const accumulator = createRuntimeEventAccumulator();
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
            prompt: smokeInput.trim() || createDefaultBindSampleInput(bindContract),
          },
          new AbortController().signal,
          {
            memberId: normalizedMemberId || undefined,
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
            accumulator.finalOutput ||
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
          memberId: normalizedMemberId || undefined,
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
      void message.error(
        error instanceof Error ? error.message : String(error),
      );
    }
  }, [
    bindContract,
    buildWorkflowYamls,
    scopeId,
    selectedEndpoint,
    selectedService,
    smokeInput,
  ]);

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
  const hasMultiplePublishedServices = services.length > 1;
  const revisionList = revisionCatalogQuery.data?.revisions ?? [];
  const bindSurfaceIdentity = useMemo(() => {
    const pendingCandidateIdentity = pendingBindingCandidate
      ? `candidate:${scopeId}:${pendingBindingCandidate.kind}:${pendingBindingCandidate.displayName}`
      : '';
    if (pendingCandidateIdentity) {
      return pendingCandidateIdentity;
    }

    const currentServiceIdentity =
      trimOptional(initialServiceId) ||
      trimOptional(preferredServiceId) ||
      trimOptional(selectedService?.serviceId);
    if (currentServiceIdentity) {
      return `service:${scopeId}:${currentServiceIdentity}`;
    }

    return `scope:${scopeId}:empty`;
  }, [
    initialServiceId,
    pendingBindingCandidate,
    preferredServiceId,
    scopeId,
    selectedService?.serviceId,
  ]);
  const bindSurfaceIdentityRef = React.useRef(bindSurfaceIdentity);

  useEffect(() => {
    bindSurfaceIdentityRef.current = bindSurfaceIdentity;
    setPendingBindBusy(false);
    setPendingBindNotice(null);
  }, [bindSurfaceIdentity]);

  const handleBindPendingCandidate = useCallback(async () => {
    if (!onBindPendingCandidate || !pendingBindingCandidate) {
      return;
    }

    const requestBindIdentity = bindSurfaceIdentity;
    setPendingBindBusy(true);
    setPendingBindNotice(null);
    try {
      await onBindPendingCandidate();
      if (bindSurfaceIdentityRef.current !== requestBindIdentity) {
        return;
      }
      setPendingBindNotice({
        message: `${pendingBindingCandidate.displayName} binding was accepted. Studio will refresh the invoke contract when the binding completes.`,
        type: 'success',
      });
    } catch (error) {
      if (bindSurfaceIdentityRef.current !== requestBindIdentity) {
        return;
      }
      setPendingBindNotice({
        message: error instanceof Error ? error.message : String(error),
        type: 'error',
      });
    } finally {
      if (bindSurfaceIdentityRef.current === requestBindIdentity) {
        setPendingBindBusy(false);
      }
    }
  }, [bindSurfaceIdentity, onBindPendingCandidate, pendingBindingCandidate]);

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
    if (servicesLoading) {
      return (
        <div data-testid="studio-bind-surface" style={rootStyle}>
          <Alert
            showIcon
            message="Loading current member contracts..."
            description="Studio is checking whether this member already has a callable published contract in the current scope."
            type="info"
          />
        </div>
      );
    }

    if (pendingBindingCandidate) {
      return (
        <div data-testid="studio-bind-surface" style={rootStyle}>
          <Alert
            showIcon
            message={`No published contract exists for ${pendingBindingCandidate.displayName} yet.`}
            description={pendingBindingCandidate.description}
            type="info"
          />
          <AevatarPanel
            title="Publish current member"
            titleHelp="Bind publishes the current revision first, then Studio reveals the invoke URL, endpoint contract, and smoke-test entry for this member."
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={parameterGridStyle}>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Implementation kind</Typography.Text>
                  <Typography.Text strong>
                    {pendingBindingCandidate.kind === 'workflow'
                      ? 'Workflow'
                      : pendingBindingCandidate.kind === 'script'
                        ? 'Script'
                        : 'GAgent'}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Current member</Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {pendingBindingCandidate.displayName}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">Scope</Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {scopeId}
                  </Typography.Text>
                </div>
              </div>
              <Typography.Text type="secondary">
                {pendingBindingCandidate.description}
              </Typography.Text>
              {pendingBindNotice ? (
                <Alert
                  showIcon
                  message={pendingBindNotice.message}
                  type={pendingBindNotice.type}
                />
              ) : null}
              <div style={{ display: 'flex', justifyContent: 'flex-start' }}>
                <Button
                  loading={pendingBindBusy}
                  type="primary"
                  onClick={() => void handleBindPendingCandidate()}
                >
                  {pendingBindingCandidate.actionLabel}
                </Button>
              </div>
            </div>
          </AevatarPanel>
        </div>
      );
    }

    return (
      <div data-testid="studio-bind-surface" style={rootStyle}>
        <Alert
          showIcon
          message="No published contract is available for this member in the current scope yet."
          description="Bind a workflow, script, or gagent revision first so Studio can reveal the invoke contract."
          type="warning"
        />
      </div>
    );
  }

  return (
    <div data-testid="studio-bind-surface" style={rootStyle}>
      <div style={pageFlowStyle}>
        <AevatarPanel
          layoutMode="document"
          padding={14}
          title="Current member contract"
          titleHelp="Keep only the callable essentials here so the page opens with the method, URL, auth, and revision at a glance."
          extra={
            <Button
              icon={<CopyOutlined />}
              onClick={() => void copyText(bindContract?.invokeUrl || '')}
            >
              Copy URL
            </Button>
          }
        >
          <div
            data-testid="studio-bind-contract-section"
            style={contractSectionStyle}
          >
            <Typography.Text type="secondary">
              {runsCurrentWorkflowDraft
                ? 'Keep the current draft in focus here; the smoke test and snippets below are the two fastest follow-up actions.'
                : 'Keep the active invoke contract in focus here; the smoke test and snippets below are the two fastest follow-up actions.'}
            </Typography.Text>
            {bindContract ? (
              <>
                <div
                  data-testid="studio-bind-contract-card"
                  style={{
                    ...surfaceCardStyle,
                    display: 'grid',
                    gridTemplateColumns: '88px minmax(0, 1fr)',
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      background: '#f8fafc',
                      borderRight: '1px solid #eef2f7',
                      color: '#475569',
                      display: 'flex',
                      fontFamily: monoFontFamily,
                      fontSize: 12,
                      fontWeight: 700,
                      justifyContent: 'center',
                      minWidth: 0,
                      padding: '10px 12px',
                    }}
                  >
                    {bindContract.method}
                  </div>
                  <div
                    style={{
                      color: '#0f172a',
                      flex: 1,
                      fontFamily: monoFontFamily,
                      fontSize: 12.5,
                      minWidth: 0,
                      overflowX: 'auto',
                      padding: '10px 14px',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {bindContract.invokeUrl}
                  </div>
                </div>
                <Space wrap size={[6, 6]}>
                  <Tag>auth · {bindContract.authLabel}</Tag>
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
              </>
            ) : (
              <Empty
                description="Inspect the published contract in focus to reveal its invoke URL and endpoint details."
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </div>
        </AevatarPanel>

        <div data-testid="studio-bind-primary-grid" style={workflowGridStyle}>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            title="Quick smoke test"
            titleHelp={
              runsCurrentWorkflowDraft
                ? 'Quick smoke test runs the current Studio workflow draft before publish. Continue to Invoke when you want to verify the published contract and endpoint.'
                : 'Use a light contract check here, then move into Invoke for the full transcript and event stream.'
            }
          >
            <div
              data-testid="studio-bind-smoke-test-section"
              style={workflowSectionStyle}
            >
              <div style={{ display: 'grid', gap: 6 }}>
                <Typography.Text strong>Authorization</Typography.Text>
                <Typography.Text type="secondary">
                  {runsCurrentWorkflowDraft
                    ? 'Current draft smoke tests use Studio draft execution. Published endpoint authorization is checked after you continue to Invoke.'
                    : bindContract?.authAuthenticated
                      ? `${bindContract.authHint} In-browser Studio requests attach the active bearer session automatically.`
                      : bindContract?.authEnabled
                        ? `${bindContract?.authHint} Sign in before running a smoke test.`
                        : bindContract?.authHint || 'Studio auth is not enabled for this environment.'}
                </Typography.Text>
                {runsCurrentWorkflowDraft ? (
                  <Space wrap size={[6, 6]}>
                    <Tag color="blue">Current draft</Tag>
                    <Typography.Text type="secondary">
                      Quick smoke test runs the current Studio draft before publish.
                    </Typography.Text>
                  </Space>
                ) : null}
              </div>
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>
                  {runsCurrentWorkflowDraft ||
                  (selectedEndpoint && isChatServiceEndpoint(selectedEndpoint))
                    ? 'Prompt'
                    : 'Prompt / command input'}
                </Typography.Text>
                <Input.TextArea
                  aria-label="Bind smoke test input"
                  autoSize={{ minRows: 4, maxRows: 8 }}
                  placeholder={
                    runsCurrentWorkflowDraft
                      ? 'Ask the current workflow draft to do a quick task...'
                      : selectedEndpoint && isChatServiceEndpoint(selectedEndpoint)
                      ? 'Ask the selected member to do a quick task...'
                      : 'Enter a quick smoke test input. Use Invoke for typed payload debugging.'
                  }
                  style={smokeInputStyle}
                  value={smokeInput}
                  onChange={(event) => setSmokeInput(event.target.value)}
                />
              </div>
              {bindContract?.requestTypeUrl &&
              !runsCurrentWorkflowDraft &&
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
                    (!runsCurrentWorkflowDraft &&
                      (!selectedService || !selectedEndpoint)) ||
                    publishedSmokeRequiresAuth
                  }
                  onClick={() => void handleRunSmokeTest()}
                >
                  Send smoke test
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
                      : runsCurrentWorkflowDraft
                        ? 'The current Studio draft accepted the request.'
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
                runsCurrentWorkflowDraft || bindContract?.streaming.sse ? (
                  <div style={{ display: 'grid', gap: 10 }}>
                    <Typography.Text strong>Streaming summary</Typography.Text>
                    <Typography.Text type="secondary">
                      {smokeTestResult.eventCount} observed events
                    </Typography.Text>
                    <div
                      style={{
                        background: '#f8fafc',
                        border: '1px solid #e5e7eb',
                        borderRadius: 12,
                        color: '#0f172a',
                        fontFamily: monoFontFamily,
                        fontSize: 12.5,
                        lineHeight: 1.65,
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

          <AevatarPanel
            layoutMode="document"
            padding={14}
            title="Integration snippets"
            titleHelp="Give the user a ready-to-copy call shape right away, without making them hunt through the support sections."
          >
            {bindContract ? (
              <div
                data-testid="studio-bind-snippet-section"
                style={workflowSectionStyle}
              >
                <div style={snippetHeaderStyle}>
                  <div style={snippetTabsStyle}>
                    {(['curl', 'fetch', 'sdk'] as SnippetTab[]).map((tabKey) => (
                      <button
                        aria-pressed={snippetTab === tabKey}
                        className={AEVATAR_INTERACTIVE_CHIP_CLASS}
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
                  <Button icon={<CopyOutlined />} onClick={() => void copyText(selectedSnippet)}>
                    Copy snippet
                  </Button>
                </div>
                <Typography.Text type="secondary">
                  Use the selected snippet to call the current member contract from your shell,
                  browser, or SDK.
                </Typography.Text>
                <pre style={snippetPreviewStyle}>{selectedSnippet}</pre>
              </div>
            ) : (
              <Empty
                description="Inspect one contract first to generate its snippets."
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>
        </div>

        <AevatarPanel
          layoutMode="document"
          padding={14}
          title="Supporting details"
          titleHelp="Keep the source selector, routing, bindings, and revision history available below the primary workflow."
        >
          <div
            data-testid="studio-bind-supporting-section"
            style={supportingSectionStyle}
          >
            <Collapse
              bordered={false}
              defaultActiveKey={[]}
              ghost
              items={[
              {
                key: 'published-contract-source',
                label: 'Published contract source',
                children: (
                  <div style={{ display: 'grid', gap: 12 }}>
                    <Typography.Text type="secondary">
                      Studio still resolves invoke URL, revisions, and governance details through
                      the published service surface. Keep this section nearby when you need to
                      switch the active contract source.
                    </Typography.Text>
                    <div style={controlsGridStyle}>
                      <div style={{ display: 'grid', gap: 8 }}>
                        <Typography.Text type="secondary">Published service</Typography.Text>
                        {hasMultiplePublishedServices ? (
                          <Select
                            options={serviceOptions}
                            placeholder="Select a published service"
                            value={selectedServiceId || undefined}
                            onChange={(value) => {
                              setSelectedServiceId(String(value || ''));
                              setSelectedEndpointId('');
                            }}
                          />
                        ) : (
                          <div style={valueCardStyle}>
                            <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                              {selectedService?.displayName ||
                                selectedService?.serviceId ||
                                'No published service'}
                            </Typography.Text>
                            <Typography.Text type="secondary">
                              {selectedService?.serviceId || 'No service id'}
                            </Typography.Text>
                          </div>
                        )}
                      </div>
                      <div style={{ display: 'grid', gap: 8 }}>
                        <Typography.Text type="secondary">Endpoint</Typography.Text>
                        <Select
                          disabled={!selectedService}
                          options={endpointOptions}
                          placeholder="Select an endpoint"
                          value={selectedEndpointId || undefined}
                          onChange={(value) => setSelectedEndpointId(String(value || ''))}
                        />
                      </div>
                    </div>
                  </div>
                ),
              },
              {
                key: 'contract-details',
                label: 'Contract details',
                children: bindContract ? (
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
                      <Typography.Text type="secondary">{bindContract.serviceDisplayName}</Typography.Text>
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
                        <Tag color={bindContract.streaming.aguiFrames ? 'geekblue' : 'default'}>
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
                    description="Keep one published contract in focus to review its details."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              },
              {
                key: 'bound-dependencies',
                label: buildBindingSectionTitle(bindingList.length),
                children: bindingsQuery.isLoading ? (
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
                          Policies {binding.policyIds.length > 0 ? binding.policyIds.join(', ') : 'none'}
                        </Typography.Text>
                      </div>
                    ))}
                  </div>
                ) : (
                  <Empty
                    description="This service does not depend on any extra connectors, secrets, or service bindings in the current scope."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              },
              {
                key: 'revisions',
                label: `Revisions (${revisionList.length})`,
                children: revisionCatalogQuery.isLoading ? (
                  <Typography.Text type="secondary">Loading published revisions...</Typography.Text>
                ) : revisionCatalogQuery.error ? (
                  <Alert
                    showIcon
                    message="Failed to load revisions"
                    description={
                      revisionCatalogQuery.error instanceof Error
                        ? revisionCatalogQuery.error.message
                        : 'Studio could not load the published revisions for this contract.'
                    }
                    type="error"
                  />
                ) : revisionList.length > 0 ? (
                  <div style={listColumnStyle}>
                    {revisionList.map((revision) => {
                      const isCurrent = revision.revisionId === currentPublishedRevision?.revisionId;
                      return (
                        <div
                          key={revision.revisionId}
                          style={{
                            ...revisionCardStyle,
                            borderColor: isCurrent ? '#6b8cff' : '#eef2f7',
                            boxShadow: isCurrent
                              ? '0 0 0 1px rgba(107, 140, 255, 0.18)'
                              : 'none',
                          }}
                        >
                          <Space wrap size={[8, 8]}>
                            <Typography.Text strong>{revision.revisionId}</Typography.Text>
                            <AevatarStatusTag
                              domain="governance"
                              label={formatStudioMemberBindingImplementationKind(
                                revision.implementationKind,
                              )}
                              status={revision.status || 'draft'}
                            />
                            {revision.isDefaultServing ? (
                              <Tag color="green">default</Tag>
                            ) : null}
                            {revision.isActiveServing ? (
                              <Tag color="blue">active</Tag>
                            ) : null}
                            {revision.retiredAt ? <Tag color="red">retired</Tag> : null}
                            {isCurrent ? <Tag color="gold">current contract</Tag> : null}
                          </Space>
                          <Typography.Text type="secondary">
                            {describeStudioMemberBindingRevisionTarget(revision)} ·{' '}
                            {describeStudioMemberBindingRevisionContext(revision) || 'No detail'}
                          </Typography.Text>
                          <Typography.Text type="secondary">
                            Serving {revision.servingState || revision.status || 'unknown'} · Published{' '}
                            {formatDateTime(revision.publishedAt)}
                          </Typography.Text>
                        </div>
                      );
                    })}
                  </div>
                ) : (
                  <Empty
                    description="No published revisions are available for this contract yet."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              },
              ]}
            />
          </div>
        </AevatarPanel>
      </div>
    </div>
  );
};

export default StudioMemberBindPanel;
