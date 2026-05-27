import {
  ApiOutlined,
  CopyOutlined,
  LinkOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Collapse, Empty, Space, Tag, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { translate, useTranslation } from '@/shared/i18n/localization';
import type {
  ScopeServiceBindingCatalogSnapshot,
} from '@/shared/models/runtime/scopeServices';
import type { ServiceCatalogSnapshot } from '@/shared/models/services';
import { isChatServiceEndpoint } from '@/shared/runs/scopeConsole';
import {
  describeScopeServiceBindingTarget,
  getScopeServiceCurrentRevision,
} from '@/shared/models/runtime/scopeServices';
import {
  describeStudioMemberBindingRevisionContext,
  describeStudioMemberBindingRevisionTarget,
  formatStudioMemberBindingImplementationKind,
  type StudioAuthSession,
  type StudioMemberBindingContract,
  type StudioMemberBindingRevision,
  type StudioMemberBindingRunStatusResponse,
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
  readonly initialEndpointId?: string;
  readonly memberId?: string;
  readonly initialServiceId?: string;
  readonly onContinueToInvoke?: (serviceId: string, endpointId: string) => void;
  readonly onBindPendingCandidate?: (() => Promise<PendingBindNotice | void>) | null;
  readonly postBindEntryActions?: {
    readonly busy?: boolean;
    readonly isEntryMember?: boolean;
    readonly memberId: string;
    readonly onSetEntryAndTest: () => void;
  } | null;
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

type PendingBindNotice = {
  readonly message: string;
  readonly type: 'success' | 'info' | 'warning' | 'error';
};

type SnippetTab = 'curl' | 'fetch' | 'sdk';

function isStudioMemberBindingRunTerminal(
  run: StudioMemberBindingRunStatusResponse | null | undefined,
): boolean {
  return Boolean(
    run && ['succeeded', 'failed', 'rejected'].includes(run.status),
  );
}

function describeStudioMemberBindingRunStatus(
  run: StudioMemberBindingRunStatusResponse,
): PendingBindNotice {
  if (run.status === 'succeeded') {
    return {
      message: translate('team.bind.run.completed'),
      type: 'success',
    };
  }

  if (run.status === 'failed' || run.status === 'rejected') {
    return {
      message:
        run.failure?.message ||
        (run.status === 'rejected'
          ? translate('team.bind.run.rejected')
          : translate('team.bind.run.failed')),
      type: 'error',
    };
  }

  if (run.status === 'platform_binding_pending') {
    return {
      message: translate('team.bind.run.platformPending'),
      type: 'info',
    };
  }

  if (run.status === 'admitted') {
    return {
      message: translate('team.bind.run.admitted'),
      type: 'info',
    };
  }

  return {
    message: translate('team.bind.run.waiting'),
    type: 'info',
  };
}

function buildRevisionFromMemberBinding(
  binding: StudioMemberBindingContract | null | undefined,
): StudioMemberBindingRevision | null {
  if (!binding) {
    return null;
  }

  return {
    allocationWeight: 100,
    artifactHash: '',
    createdAt: binding.boundAt,
    deploymentId: '',
    failureReason: '',
    implementationKind: binding.implementationKind,
    inlineWorkflowCount: 0,
    isActiveServing: true,
    isDefaultServing: true,
    isServingTarget: true,
    preparedAt: binding.boundAt,
    primaryActorId: '',
    publishedAt: binding.boundAt,
    retiredAt: null,
    revisionId: binding.revisionId,
    scriptDefinitionActorId: '',
    scriptId: '',
    scriptRevision: '',
    scriptSourceHash: '',
    servingState: 'active',
    staticActorTypeName: '',
    status: 'active',
    workflowDefinitionActorId: '',
    workflowName: '',
  };
}

const monoFontFamily =
  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace";

const rootStyle: React.CSSProperties = {
  minWidth: 0,
  width: '100%',
};

const controlsGridStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'minmax(240px, 1fr) minmax(240px, 1fr)',
};

const pageFlowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  width: '100%',
};

const contractSectionStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
};

const workflowGridStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 360px), 1fr))',
  width: '100%',
};

const sourcePanelStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
};

const sourceSummaryStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
};

const sourceStatusStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#475569',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
  minWidth: 0,
};

const equalHeightPanelStyle: React.CSSProperties = {
  height: '100%',
};

const contractActionRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
  minWidth: 0,
};

const invokeActionButtonStyle: React.CSSProperties = {
  minWidth: 132,
};

const sourceControlStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateRows: 'auto minmax(58px, 1fr)',
  minWidth: 0,
};

const parameterGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
};

const surfaceCardStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #eef2f7',
  borderRadius: 10,
};

const valueCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  display: 'grid',
  gap: 3,
  minHeight: 58,
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
  minWidth: 0,
  overflow: 'hidden',
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

const contractUrlCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  display: 'grid',
  gridTemplateColumns: '82px minmax(0, 1fr)',
  overflow: 'hidden',
};

const contractMethodStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#111827',
  color: '#fffdf8',
  display: 'flex',
  fontFamily: monoFontFamily,
  fontSize: 12,
  fontWeight: 700,
  justifyContent: 'center',
  minWidth: 0,
  padding: '10px 12px',
};

const contractUrlStyle: React.CSSProperties = {
  color: '#0f172a',
  fontFamily: monoFontFamily,
  fontSize: 12.5,
  minWidth: 0,
  overflowX: 'auto',
  padding: '10px 14px',
  whiteSpace: 'nowrap',
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
    return translate('common.notAvailable');
  }

  const date = new Date(normalized);
  if (Number.isNaN(date.getTime())) {
    return normalized;
  }

  return date.toLocaleString();
}

function copyText(value: string): Promise<void> | void {
  if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
    return;
  }

  return navigator.clipboard.writeText(value);
}

function buildBindingSectionTitle(count: number): string {
  return count === 1
    ? translate('team.bind.dependencies.one')
    : translate('team.bind.dependencies.many', { count });
}

function resolveBindDefaultEndpointId(
  service: Pick<ServiceCatalogSnapshot, 'endpoints'> | null | undefined,
): string {
  if (!service?.endpoints.length) {
    return '';
  }

  return (
    service.endpoints.find(isChatServiceEndpoint)?.endpointId ||
    service.endpoints[0]?.endpointId ||
    ''
  );
}

function renderPostBindEntryAction(
  postBindEntryActions: NonNullable<StudioMemberBindPanelProps['postBindEntryActions']>,
) {
  return (
    <Space direction="vertical" size={8} style={{ width: '100%' }}>
      {postBindEntryActions.isEntryMember ? (
        <>
          <Typography.Text>
            {translate('team.bind.entry.already')}
          </Typography.Text>
          <Button
            loading={postBindEntryActions.busy}
            onClick={postBindEntryActions.onSetEntryAndTest}
            size="small"
            type="primary"
          >
            {translate('team.bind.entry.testTeam')}
          </Button>
        </>
      ) : (
        <>
          <Typography.Text>
            {translate('team.bind.entry.completed')}
          </Typography.Text>
          <Button
            loading={postBindEntryActions.busy}
            onClick={postBindEntryActions.onSetEntryAndTest}
            size="small"
            type="primary"
          >
            {translate('team.bind.entry.setAndTest')}
          </Button>
        </>
      )}
    </Space>
  );
}

const StudioMemberBindPanel: React.FC<StudioMemberBindPanelProps> = ({
  scopeId,
  services,
  memberId,
  initialServiceId,
  initialEndpointId,
  preferredServiceId,
  onSelectionChange,
  onContinueToInvoke,
  onBindPendingCandidate,
  postBindEntryActions,
  pendingBindingCandidate,
  authSession,
  servicesLoading,
}) => {
  const { t } = useTranslation();
  const [selectedServiceId, setSelectedServiceId] = useState(() =>
    trimOptional(initialServiceId),
  );
  const [selectedEndpointId, setSelectedEndpointId] = useState(() =>
    trimOptional(initialEndpointId),
  );
  const [snippetTab, setSnippetTab] = useState<SnippetTab>('curl');
  const [pendingBindBusy, setPendingBindBusy] = useState(false);
  const [pendingBindNotice, setPendingBindNotice] =
    useState<PendingBindNotice | null>(null);
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
        : services.find((service) =>
            service.endpoints.some(isChatServiceEndpoint),
          )?.serviceId ||
          services[0]?.serviceId ||
          '',
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
        : resolveBindDefaultEndpointId(selectedService),
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
    enabled: Boolean(scopeId && normalizedMemberId),
    queryKey: ['studio-bind', 'member-binding', scopeId, normalizedMemberId],
    queryFn: () => studioApi.getMemberBinding(scopeId, normalizedMemberId),
    refetchInterval: (query) => {
      const data = query.state.data as
        | Awaited<ReturnType<typeof studioApi.getMemberBinding>>
        | undefined;
      return data?.currentBindingRun &&
        !isStudioMemberBindingRunTerminal(data.currentBindingRun)
        ? 1_500
        : false;
    },
  });
  const currentBindingRun = memberBindingStatusQuery.data?.currentBindingRun ?? null;
  const revisionCatalogQuery = revisionsQuery;
  const currentPublishedRevision = useMemo(
    () =>
      buildRevisionFromMemberBinding(memberBindingStatusQuery.data?.lastBinding) ??
      getScopeServiceCurrentRevision(revisionsQuery.data),
    [memberBindingStatusQuery.data?.lastBinding, revisionsQuery.data],
  );
  const currentBindingRunNotice = currentBindingRun
    ? describeStudioMemberBindingRunStatus(currentBindingRun)
    : null;

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
  const canUsePublishedMemberInvoke = Boolean(
    normalizedMemberId && selectedService && selectedEndpoint && bindContract,
  );

  const snippetMap = useMemo(() => {
    if (!bindContract) {
      return {
        curl: '',
        fetch: '',
        sdk: '',
      };
    }

    const sampleInput = createDefaultBindSampleInput(bindContract);
    return {
      curl: buildCurlSnippet(bindContract, sampleInput),
      fetch: buildFetchSnippet(bindContract, sampleInput),
      sdk: buildSdkSnippet(bindContract, sampleInput),
    };
  }, [bindContract]);

  const selectedSnippet = snippetMap[snippetTab];
  const bindingCatalog: ScopeServiceBindingCatalogSnapshot | undefined = bindingsQuery.data;
  const bindingList = bindingCatalog?.bindings ?? [];
  const revisionList = revisionCatalogQuery.data?.revisions ?? [];
  const hasEndpointOptions = Boolean(selectedService?.endpoints.length);
  const endpointCount = selectedService?.endpoints.length ?? 0;
  const endpointUnavailableMessage =
    selectedService && !hasEndpointOptions
      ? t('team.bind.endpoint.unavailable.title')
      : '';
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
      const resultNotice = await onBindPendingCandidate();
      if (bindSurfaceIdentityRef.current !== requestBindIdentity) {
        return;
      }
      setPendingBindNotice({
        message:
          resultNotice?.message ||
          t('team.bind.pending.accepted', {
            name: pendingBindingCandidate.displayName,
          }),
        type: resultNotice?.type || 'info',
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
        message={t('team.bind.noScope')}
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
            message={t('team.bind.loadingContracts.title')}
            description={t('team.bind.loadingContracts.description')}
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
            message={t('team.bind.pending.noContract', {
              name: pendingBindingCandidate.displayName,
            })}
            description={pendingBindingCandidate.description}
            type="info"
          />
          <AevatarPanel
            title={t('team.bind.pending.publishTitle')}
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={parameterGridStyle}>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t('team.bind.pending.implementationKind')}
                  </Typography.Text>
                  <Typography.Text strong>
                    {pendingBindingCandidate.kind === 'workflow'
                      ? 'Workflow'
                      : pendingBindingCandidate.kind === 'script'
                        ? 'Script'
                        : 'GAgent'}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t('team.bind.pending.currentMember')}
                  </Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {pendingBindingCandidate.displayName}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t('common.id.scope')}
                  </Typography.Text>
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
              {currentBindingRunNotice ? (
                <Alert
                  showIcon
                  message={currentBindingRunNotice.message}
                  description={t('team.bind.run.description', {
                    runId: currentBindingRun?.bindingRunId || '',
                  })}
                  type={currentBindingRunNotice.type}
                />
              ) : null}
              {postBindEntryActions ? (
                <Alert
                  showIcon
                  type="success"
                  message={
                    postBindEntryActions.isEntryMember
                      ? t('team.bind.entry.isEntry')
                      : t('team.bind.entry.canBeEntry')
                  }
                  description={renderPostBindEntryAction(postBindEntryActions)}
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
          message={t('team.bind.noContract.title')}
          description={t('team.bind.noContract.description')}
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
          title={t('team.bind.publication.title')}
          extra={
            <Space wrap size={[6, 6]}>
              <Tag color={bindContract ? 'green' : 'default'}>
                {bindContract
                  ? t('team.bind.publication.selected')
                  : t('team.bind.publication.needsEndpoint')}
              </Tag>
              {revisionList.length > 0 ? (
                <Tag>
                  {t('team.bind.publication.revisions', {
                    count: revisionList.length,
                  })}
                </Tag>
              ) : null}
            </Space>
          }
        >
          {postBindEntryActions ? (
            <Alert
              showIcon
              type="success"
              message={
                postBindEntryActions.isEntryMember
                  ? t('team.bind.entry.isEntry')
                  : t('team.bind.entry.canBeEntry')
              }
              description={renderPostBindEntryAction(postBindEntryActions)}
            />
          ) : null}
          <div style={sourcePanelStyle}>
            <div style={sourceSummaryStyle}>
              <div style={sourceStatusStyle}>
                <ApiOutlined />
                <Typography.Text strong>
                  {selectedService?.displayName ||
                    selectedService?.serviceId ||
                    t('team.bind.publication.noService')}
                </Typography.Text>
                {selectedEndpoint ? (
                  <Typography.Text type="secondary">
                    / {selectedEndpoint.displayName || selectedEndpoint.endpointId}
                  </Typography.Text>
                ) : null}
              </div>
              {bindContract ? (
                <Button
                  icon={<CopyOutlined />}
                  onClick={() => void copyText(bindContract.invokeUrl)}
                >
                  {t('common.copyUrl')}
                </Button>
              ) : null}
            </div>
            <div style={controlsGridStyle}>
              <div style={sourceControlStackStyle}>
                <Typography.Text type="secondary">
                  {t('team.bind.pending.currentMember')}
                </Typography.Text>
                <div style={valueCardStyle}>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {selectedService?.displayName ||
                      selectedService?.serviceId ||
                      t('team.bind.publication.noContract')}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {normalizedMemberId
                      ? `member:${normalizedMemberId}`
                      : t('team.bind.publication.noMember')}
                  </Typography.Text>
                </div>
              </div>
              <div style={sourceControlStackStyle}>
                <Typography.Text type="secondary">
                  {t('team.bind.publication.invokeTarget')}
                </Typography.Text>
                {selectedService && hasEndpointOptions ? (
                  <div style={valueCardStyle}>
                    <Space wrap size={[6, 6]}>
                      <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                        {selectedEndpoint?.displayName ||
                          selectedEndpoint?.endpointId ||
                          t('team.bind.publication.callableEndpoint')}
                      </Typography.Text>
                      {selectedEndpoint ? (
                        <Tag
                          color={
                            isChatServiceEndpoint(selectedEndpoint)
                              ? 'geekblue'
                              : 'default'
                          }
                          style={{ marginInlineEnd: 0 }}
                        >
                          {isChatServiceEndpoint(selectedEndpoint)
                            ? 'chat'
                            : 'command'}
                        </Tag>
                      ) : null}
                    </Space>
                    <Typography.Text type="secondary">
                      {selectedEndpoint
                        ? `${t('common.id.endpoint')}: ${selectedEndpoint.endpointId}`
                        : t('team.bind.publication.noEndpoint')}
                    </Typography.Text>
                    {endpointCount > 1 ? (
                      <Typography.Text type="secondary">
                        {t('team.bind.endpoint.moreEntrypoints', {
                          count: endpointCount - 1,
                        })}
                      </Typography.Text>
                    ) : null}
                  </div>
                ) : (
                  <div style={valueCardStyle}>
                    <Typography.Text strong>
                      {t('team.bind.endpoint.noData.title')}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {t('team.bind.endpoint.noData.description')}
                    </Typography.Text>
                  </div>
                )}
              </div>
            </div>
            {endpointUnavailableMessage ? (
              <Alert
                showIcon
                message={endpointUnavailableMessage}
                description={t('team.bind.endpoint.unavailable.description')}
                type="warning"
              />
            ) : null}
            {!normalizedMemberId && selectedService && selectedEndpoint ? (
              <Alert
                showIcon
                message={t('team.bind.invoke.memberRequired.title')}
                description={t('team.bind.invoke.memberRequired.description')}
                type="info"
              />
            ) : null}
          </div>
        </AevatarPanel>

        <AevatarPanel
          layoutMode="document"
          padding={14}
          title={t('team.bind.contract.title')}
        >
          <div
            data-testid="studio-bind-contract-section"
            style={contractSectionStyle}
          >
            <Typography.Text type="secondary">
              {t('team.bind.contract.description')}
            </Typography.Text>
            {bindContract ? (
              <>
                <div
                  data-testid="studio-bind-contract-card"
                  style={contractUrlCardStyle}
                >
                  <div style={contractMethodStyle}>
                    {bindContract.method}
                  </div>
                  <div style={contractUrlStyle}>
                    {bindContract.invokeUrl}
                  </div>
                </div>
                <Space wrap size={[6, 6]}>
                  <Tag>
                    {t('team.bind.contract.auth', {
                      auth: bindContract.authLabel,
                    })}
                  </Tag>
                  <Tag>
                    {t('team.bind.contract.revision', {
                      revision: bindContract.revisionId,
                    })}
                  </Tag>
                  {bindContract.streaming.sse ? (
                    <Tag color="gold">
                      {t('team.bind.contract.stream')}
                    </Tag>
                  ) : (
                    <Tag>{t('team.bind.contract.response')}</Tag>
                  )}
                  {bindContract.streaming.aguiFrames ? (
                    <Tag color="geekblue">AGUI frames</Tag>
                  ) : null}
                </Space>
              </>
            ) : (
              <Alert
                showIcon
                message={t('team.bind.contract.selectEndpoint.title')}
                description={
                  normalizedMemberId
                    ? t('team.bind.contract.selectEndpoint.memberDescription')
                    : t('team.bind.contract.selectEndpoint.noMemberDescription')
                }
                type="info"
              />
            )}
            <div
              data-testid="studio-bind-next-step-section"
              style={contractActionRowStyle}
            >
              <Button
                icon={<LinkOutlined />}
                disabled={!canUsePublishedMemberInvoke}
                style={invokeActionButtonStyle}
                type="primary"
                onClick={() => {
                  if (!canUsePublishedMemberInvoke || !selectedService || !selectedEndpoint) {
                    return;
                  }

                  onContinueToInvoke?.(
                    selectedService.serviceId,
                    selectedEndpoint.endpointId,
                  );
                }}
              >
                {t('common.openInvoke')}
              </Button>
            </div>
          </div>
        </AevatarPanel>

        <div data-testid="studio-bind-primary-grid" style={workflowGridStyle}>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            style={equalHeightPanelStyle}
            title={t('team.bind.snippets.title')}
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
                    {t('team.bind.snippets.copy')}
                  </Button>
                </div>
                <Typography.Text type="secondary">
                  {t('team.bind.snippets.description')}
                </Typography.Text>
                <pre style={snippetPreviewStyle}>{selectedSnippet}</pre>
              </div>
            ) : (
              <Empty
                description={t('team.bind.snippets.empty')}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>
        </div>

        <AevatarPanel
          layoutMode="document"
          padding={14}
          title={t('team.bind.supporting.title')}
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
                key: 'contract-details',
                label: t('team.bind.contractDetails.title'),
                children: bindContract ? (
                  <div style={parameterGridStyle}>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.publishedService')}
                      </Typography.Text>
                      <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                        {bindContract.serviceId}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.diagnostic')}
                      </Typography.Text>
                    </div>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.workspace')}
                      </Typography.Text>
                      <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                        {bindContract.scopeLabel}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        {bindContract.scopeSource
                          ? t('team.bind.contractDetails.resolvedFrom', {
                              source: bindContract.scopeSource,
                            })
                          : t('team.bind.contractDetails.boundWorkspace')}
                      </Typography.Text>
                    </div>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.authorization')}
                      </Typography.Text>
                      <Typography.Text strong>{bindContract.authLabel}</Typography.Text>
                      <Typography.Text type="secondary">{bindContract.authHint}</Typography.Text>
                    </div>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.revision')}
                      </Typography.Text>
                      <Typography.Text strong>{bindContract.revisionId}</Typography.Text>
                      <Typography.Text type="secondary">{bindContract.serviceDisplayName}</Typography.Text>
                    </div>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.delivery')}
                      </Typography.Text>
                      <Typography.Text strong>{bindContract.method}</Typography.Text>
                      <Typography.Text type="secondary">
                        {bindContract.streaming.sse
                          ? t('team.bind.contractDetails.deliveryStream')
                          : t('team.bind.contractDetails.deliveryJson')}
                      </Typography.Text>
                    </div>
                    <div style={valueCardStyle}>
                      <Typography.Text type="secondary">
                        {t('team.bind.contractDetails.streaming')}
                      </Typography.Text>
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
                        <Typography.Text type="secondary">
                          {t('team.bind.contractDetails.requestSchema')}
                        </Typography.Text>
                        <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                          {bindContract.requestTypeUrl}
                        </Typography.Text>
                      </div>
                    ) : null}
                    {bindContract.responseTypeUrl ? (
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t('team.bind.contractDetails.responseSchema')}
                        </Typography.Text>
                        <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                          {bindContract.responseTypeUrl}
                        </Typography.Text>
                      </div>
                    ) : null}
                  </div>
                ) : (
                  <Empty
                    description={t('team.bind.contractDetails.empty')}
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              },
              {
                key: 'bound-dependencies',
                label: buildBindingSectionTitle(bindingList.length),
                children: bindingsQuery.isLoading ? (
                  <Typography.Text type="secondary">
                    {t('team.bind.dependencies.loading')}
                  </Typography.Text>
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
                          {t('team.bind.dependencies.target', {
                            target: describeScopeServiceBindingTarget(binding),
                          })}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {t('team.bind.dependencies.policies', {
                            policies:
                              binding.policyIds.length > 0
                                ? binding.policyIds.join(', ')
                                : t('common.none'),
                          })}
                        </Typography.Text>
                      </div>
                    ))}
                  </div>
                ) : (
                  <Empty
                    description={t('team.bind.dependencies.empty')}
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              },
              {
                key: 'revisions',
                label: t('team.bind.revisions.title', {
                  count: revisionList.length,
                }),
                children: revisionCatalogQuery.isLoading ? (
                  <Typography.Text type="secondary">
                    {t('team.bind.revisions.loading')}
                  </Typography.Text>
                ) : revisionCatalogQuery.error ? (
                  <Alert
                    showIcon
                    message={t('team.bind.revisions.failed')}
                    description={
                      revisionCatalogQuery.error instanceof Error
                        ? revisionCatalogQuery.error.message
                        : t('team.bind.revisions.failedFallback')
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
                              <Tag color="green">
                                {t('team.bind.revisions.default')}
                              </Tag>
                            ) : null}
                            {revision.isActiveServing ? (
                              <Tag color="blue">
                                {t('team.bind.revisions.active')}
                              </Tag>
                            ) : null}
                            {revision.retiredAt ? (
                              <Tag color="red">
                                {t('team.bind.revisions.retired')}
                              </Tag>
                            ) : null}
                            {isCurrent ? (
                              <Tag color="gold">
                                {t('team.bind.revisions.current')}
                              </Tag>
                            ) : null}
                          </Space>
                          <Typography.Text type="secondary">
                            {describeStudioMemberBindingRevisionTarget(revision)} ·{' '}
                            {describeStudioMemberBindingRevisionContext(revision) ||
                              t('team.bind.revisions.noDetail')}
                          </Typography.Text>
                          <Typography.Text type="secondary">
                            {t('team.bind.revisions.serving', {
                              state:
                                revision.servingState ||
                                revision.status ||
                                t('common.status.unknown'),
                              time: formatDateTime(revision.publishedAt),
                            })}
                          </Typography.Text>
                        </div>
                      );
                    })}
                  </div>
                ) : (
                  <Empty
                    description={t('team.bind.revisions.empty')}
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
