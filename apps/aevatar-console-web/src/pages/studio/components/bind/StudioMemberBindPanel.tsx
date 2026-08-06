import {
  ApiOutlined,
  CheckCircleOutlined,
  CopyOutlined,
  LinkOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Collapse,
  Empty,
  Input,
  Space,
  Tag,
  Typography,
} from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from '@/shared/agui/runtimeEventSemantics';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeRunsApi } from '@/shared/api/runtimeRunsApi';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { t } from '@/shared/i18n/messages';
import type { ScopeServiceBindingCatalogSnapshot } from '@/shared/models/runtime/scopeServices';
import {
  describeScopeServiceBindingTarget,
  getScopeServiceCurrentRevision,
} from '@/shared/models/runtime/scopeServices';
import type {
  ServiceCatalogSnapshot,
  ServiceEndpointSnapshot,
} from '@/shared/models/services';
import { isChatServiceEndpoint } from '@/shared/runs/scopeConsole';
import { studioApi } from '@/shared/studio/api';
import {
  describeStudioMemberBindingRevisionContext,
  describeStudioMemberBindingRevisionTarget,
  formatStudioMemberBindingImplementationKind,
  normalizeStudioMemberBindingImplementationKind,
  type StudioAuthSession,
  type StudioMemberBindingContract,
  type StudioMemberBindingRevision,
  type StudioMemberBindingRunStatusResponse,
} from '@/shared/studio/models';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { AEVATAR_INTERACTIVE_CHIP_CLASS } from '@/shared/ui/interactionStandards';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';
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
  readonly teamId?: string;
  readonly initialServiceId?: string;
  readonly onContinueToInvoke?: (serviceId: string, endpointId: string) => void;
  readonly onBindPendingCandidate?:
    | (() => Promise<PendingBindNotice | undefined>)
    | null;
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

type SmokeTestResult = {
  readonly error: string;
  readonly eventCount: number;
  readonly latencyMs: number;
  readonly responseSummary: string;
  readonly runId: string;
  readonly status: 'idle' | 'running' | 'success' | 'error';
};

type BindFlowGuidance = {
  readonly message: string;
  readonly stage: 'waiting' | 'ready' | 'blocked' | 'failed';
  readonly type: 'success' | 'info' | 'warning' | 'error';
};

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
      message: t(
        'pages.studio.bind.studiomemberbindpanel.binding.completed.studio.is.refreshing.2',
        'Binding completed. Studio is refreshing the published contract.',
      ),
      type: 'success',
    };
  }

  if (run.status === 'failed' || run.status === 'rejected') {
    return {
      message:
        run.failure?.message ||
        (run.status === 'rejected'
          ? 'Binding request was rejected by the member authority.'
          : 'Binding failed while publishing the member contract.'),
      type: 'error',
    };
  }

  if (run.status === 'platform_binding_pending') {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.binding.request.accepted.platform.publication.2',
        'Binding request accepted. Platform publication is still running; Invoke is not ready until the run completes.',
      ),
      type: 'info',
    };
  }

  if (run.status === 'admitted') {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.binding.request.admitted.studio.is.2',
        'Binding request admitted. Studio is starting platform publication; the member is not callable yet.',
      ),
      type: 'info',
    };
  }

  return {
    message: t(
      'pages.studio.bind.studiomemberbindpanel.binding.request.accepted.studio.is.2',
      'Binding request accepted. Studio is waiting for the member authority; this does not mean the member is bound yet.',
    ),
    type: 'info',
  };
}

function buildBindFlowGuidance(input: {
  readonly currentBindingRun: StudioMemberBindingRunStatusResponse | null;
  readonly hasEndpointOptions: boolean;
  readonly hasMember: boolean;
  readonly hasPublishedService: boolean;
  readonly pendingBindingCandidate:
    | StudioMemberBindPanelProps['pendingBindingCandidate']
    | null
    | undefined;
  readonly smokeTestStatus: SmokeTestResult['status'];
}): BindFlowGuidance {
  const { currentBindingRun } = input;
  if (
    currentBindingRun &&
    !isStudioMemberBindingRunTerminal(currentBindingRun)
  ) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.bind.accepted.the.publication.request',
        'Bind accepted the publication request. Stay here until Studio observes the published contract, then continue to Invoke.',
      ),
      stage: 'waiting',
      type: 'info',
    };
  }

  if (
    currentBindingRun &&
    (currentBindingRun.status === 'failed' ||
      currentBindingRun.status === 'rejected')
  ) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.bind.did.not.publish.this',
        'Bind did not publish this member. Return to Build to adjust the member definition before retrying publication.',
      ),
      stage: 'failed',
      type: 'error',
    };
  }

  if (currentBindingRun?.status === 'succeeded' && !input.hasEndpointOptions) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.bind.completed.and.studio.is',
        'Bind completed, and Studio is refreshing the published contract. Continue to Invoke after endpoint data appears.',
      ),
      stage: 'waiting',
      type: 'info',
    };
  }

  if (input.pendingBindingCandidate && !input.hasPublishedService) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.this.member.still.needs.bind',
        'This member still needs Bind. Publish the current revision before trying Invoke or Observe.',
      ),
      stage: 'blocked',
      type: 'warning',
    };
  }

  if (!input.hasMember) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.bind.can.inspect.this.service',
        'Bind can inspect this service, but Invoke stays blocked until Studio resolves a Team member target.',
      ),
      stage: 'blocked',
      type: 'info',
    };
  }

  if (!input.hasEndpointOptions) {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.the.member.is.published.but',
        'The member is published, but Studio has no callable endpoint yet. Wait for the contract to refresh before continuing.',
      ),
      stage: 'waiting',
      type: 'warning',
    };
  }

  if (input.smokeTestStatus === 'success') {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.smoke.test.passed.continue.to',
        'Smoke test passed. Continue to Invoke for a full run transcript, then use Observe for backend events.',
      ),
      stage: 'ready',
      type: 'success',
    };
  }

  if (input.smokeTestStatus === 'error') {
    return {
      message: t(
        'pages.studio.bind.studiomemberbindpanel.smoke.test.failed.only.this',
        'Smoke test failed only this contract check. Retry here, or use Invoke when you need full events and typed payload debugging.',
      ),
      stage: 'failed',
      type: 'warning',
    };
  }

  return {
    message: t(
      'pages.studio.bind.studiomemberbindpanel.bind.is.ready.run.quick',
      'Bind is ready. Run a quick smoke test or continue to Invoke for the full transcript and Observe handoff.',
    ),
    stage: 'ready',
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

const contractAndActionsGridStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'grid',
  gap: 14,
  gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 380px), 1fr))',
};

const equalHeightPanelStyle: React.CSSProperties = {
  height: '100%',
};

const sourceControlStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateRows: 'auto minmax(58px, 1fr)',
  minWidth: 0,
};

const endpointChoiceRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 190px), 1fr))',
};

const endpointChoiceButtonStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#ffffff',
  border: '1px solid #d9e2ef',
  borderRadius: 8,
  color: '#334155',
  cursor: 'pointer',
  display: 'grid',
  fontSize: 12,
  gap: 6,
  minHeight: 94,
  padding: '10px 12px',
  textAlign: 'left',
  width: '100%',
};

const endpointChoiceButtonActiveStyle: React.CSSProperties = {
  ...endpointChoiceButtonStyle,
  background: '#0f172a',
  border: '1px solid #0f172a',
  color: '#ffffff',
};

const endpointChoiceTitleStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
  justifyContent: 'space-between',
  minWidth: 0,
};

const endpointChoiceNameStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 800,
  lineHeight: 1.25,
  minWidth: 0,
  overflowWrap: 'anywhere',
};

const endpointChoiceMetaStyle: React.CSSProperties = {
  fontFamily: monoFontFamily,
  fontSize: 11,
  lineHeight: 1.35,
  overflowWrap: 'anywhere',
};

const endpointChoiceDescriptionStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1.45,
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

const smokeFieldStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  minWidth: 0,
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
  boxSizing: 'border-box',
  fontFamily: monoFontFamily,
  maxWidth: '100%',
  minWidth: 0,
  width: '100%',
};

const smokeTypedPayloadDescriptionStyle: React.CSSProperties = {
  lineHeight: 1.45,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const smokeActionStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  minWidth: 0,
  width: '100%',
};

const flowGuidanceCardStyle: React.CSSProperties = {
  ...surfaceCardStyle,
  background: '#f8fafc',
  display: 'grid',
  gap: 8,
  padding: 12,
};

const flowGuidanceHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
  minWidth: 0,
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

function describeEndpointKind(endpoint: ServiceEndpointSnapshot): string {
  return isChatServiceEndpoint(endpoint)
    ? t('pages.studio.bind.studiomemberbindpanel.copy', 'Default test')
    : t('pages.studio.bind.studiomemberbindpanel.copy.2', 'Advanced input');
}

function describeEndpointPurpose(endpoint: ServiceEndpointSnapshot): string {
  if (isChatServiceEndpoint(endpoint)) {
    return t(
      'pages.studio.bind.studiomemberbindpanel.copy.3',
      'Enter one sentence to quickly confirm whether the member responds correctly.',
    );
  }

  return t(
    'pages.studio.bind.studiomemberbindpanel.api.sdk',
    'Use this for API/SDK calls that require a fixed input shape.',
  );
}

function renderPostBindEntryAction(
  postBindEntryActions: NonNullable<
    StudioMemberBindPanelProps['postBindEntryActions']
  >,
) {
  return (
    <Space direction="vertical" size={8} style={{ width: '100%' }}>
      {postBindEntryActions.isEntryMember ? (
        <>
          <Typography.Text>
            {t(
              'pages.studio.bind.studiomemberbindpanel.team',
              'This member is already the team entry. You can return to the Team page to test the full path.',
            )}
          </Typography.Text>
          <Button
            loading={postBindEntryActions.busy}
            onClick={postBindEntryActions.onSetEntryAndTest}
            size="small"
            type="primary"
          >
            {t('pages.studio.bind.studiomemberbindpanel.team.2', 'Test Team')}
          </Button>
        </>
      ) : (
        <>
          <Typography.Text>
            {t(
              'pages.studio.bind.studiomemberbindpanel.bind.team',
              'Bind is complete. Next, set it as the team entry and return to the Team page to test the full path.',
            )}
          </Typography.Text>
          <Button
            loading={postBindEntryActions.busy}
            onClick={postBindEntryActions.onSetEntryAndTest}
            size="small"
            type="primary"
          >
            {t(
              'pages.studio.bind.studiomemberbindpanel.team.3',
              'Set as entry and test Team',
            )}
          </Button>
        </>
      )}
    </Space>
  );
}

const StudioMemberBindPanel: React.FC<StudioMemberBindPanelProps> = ({
  buildWorkflowYamls,
  scopeId,
  services,
  memberId,
  teamId,
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
  const toast = useConsoleToast();
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
  const [pendingBindNotice, setPendingBindNotice] =
    useState<PendingBindNotice | null>(null);
  const runsCurrentWorkflowDraft = Boolean(buildWorkflowYamls);
  const normalizedMemberId = trimOptional(memberId);
  const normalizedTeamId = trimOptional(teamId);

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
      services.some(
        (service) => service.serviceId === normalizedInitialServiceId,
      )
    ) {
      setSelectedServiceId((current) =>
        current === normalizedInitialServiceId
          ? current
          : normalizedInitialServiceId,
      );
      return;
    }

    const normalizedPreferredServiceId = trimOptional(preferredServiceId);
    if (
      normalizedPreferredServiceId &&
      services.some(
        (service) => service.serviceId === normalizedPreferredServiceId,
      )
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
        current === normalizedInitialEndpointId
          ? current
          : normalizedInitialEndpointId,
      );
      return;
    }

    setSelectedEndpointId((current) =>
      current &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === current,
      )
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
      scopeRuntimeApi.getServiceBindings(
        scopeId,
        selectedService?.serviceId || '',
      ),
  });
  const revisionsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ['studio-bind', 'revisions', scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.getServiceRevisions(
        scopeId,
        selectedService?.serviceId || '',
      ),
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
  const currentBindingRun =
    memberBindingStatusQuery.data?.currentBindingRun ?? null;
  const revisionCatalogQuery = revisionsQuery;
  const currentPublishedRevision = useMemo(
    () =>
      buildRevisionFromMemberBinding(
        memberBindingStatusQuery.data?.lastBinding,
      ) ?? getScopeServiceCurrentRevision(revisionsQuery.data),
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
  const publishedSmokeRequiresAuth =
    !runsCurrentWorkflowDraft &&
    Boolean(bindContract?.authEnabled && !bindContract.authAuthenticated);
  const bindingPublicationReady =
    !currentBindingRun || isStudioMemberBindingRunTerminal(currentBindingRun);
  const canUsePublishedMemberInvoke = Boolean(
    normalizedMemberId &&
      selectedService &&
      selectedEndpoint &&
      bindContract &&
      bindingPublicationReady,
  );

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
            prompt:
              smokeInput.trim() || createDefaultBindSampleInput(bindContract),
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

      if (
        !selectedService ||
        !selectedEndpoint ||
        !normalizedMemberId ||
        !bindContract
      ) {
        return;
      }

      const invokeRouteTarget =
        normalizeStudioMemberBindingImplementationKind(
          currentPublishedRevision?.implementationKind,
        ) === 'gagent' && normalizedTeamId
          ? { teamId: normalizedTeamId }
          : { serviceId: selectedService.serviceId };

      if (isChatServiceEndpoint(selectedEndpoint)) {
        const accumulator = createRuntimeEventAccumulator();
        const response = await runtimeRunsApi.streamChat(
          scopeId,
          {
            prompt:
              smokeInput.trim() || createDefaultBindSampleInput(bindContract),
          },
          new AbortController().signal,
          invokeRouteTarget,
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
          prompt:
            smokeInput.trim() || createDefaultBindSampleInput(bindContract),
        },
        invokeRouteTarget,
      );

      setSmokeTestResult({
        error: '',
        eventCount: 0,
        latencyMs: Date.now() - startedAt,
        responseSummary: JSON.stringify(response, null, 2),
        runId: trimOptional(
          String(response.request_id || response.requestId || ''),
        ),
        status: 'success',
      });
    } catch {
      setSmokeTestResult(createIdleSmokeTestResult());
      toast.error(
        t(
          'pages.studio.bind.studiomemberbindpanel.smoke.test.request.failed',
          'Could not complete the smoke test. Try again.',
        ),
      );
    }
  }, [
    bindContract,
    buildWorkflowYamls,
    normalizedMemberId,
    scopeId,
    selectedEndpoint,
    selectedService,
    smokeInput,
    toast,
  ]);

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
  const bindingCatalog: ScopeServiceBindingCatalogSnapshot | undefined =
    bindingsQuery.data;
  const bindingList = bindingCatalog?.bindings ?? [];
  const revisionList = revisionCatalogQuery.data?.revisions ?? [];
  const hasEndpointOptions = Boolean(selectedService?.endpoints.length);
  const endpointUnavailableMessage =
    selectedService && !hasEndpointOptions
      ? 'This published service has no endpoint data available yet.'
      : '';
  const bindFlowGuidance = buildBindFlowGuidance({
    currentBindingRun,
    hasEndpointOptions,
    hasMember: Boolean(normalizedMemberId),
    hasPublishedService: services.length > 0,
    pendingBindingCandidate,
    smokeTestStatus: smokeTestResult.status,
  });
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
          `${pendingBindingCandidate.displayName} binding request was accepted. Studio will show the published contract after the run completes.`,
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
        message={t(
          'pages.studio.bind.studiomemberbindpanel.resolve.workspace.before.binding.this.2',
          'Resolve a workspace before binding this member.',
        )}
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
            message={t(
              'pages.studio.bind.studiomemberbindpanel.loading.current.member.contracts.2',
              'Loading current member contracts...',
            )}
            description={t(
              'pages.studio.bind.studiomemberbindpanel.studio.is.checking.whether.this.2',
              'Studio is checking whether this member already has a callable published contract in the current workspace.',
            )}
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
            title={t(
              'pages.studio.bind.studiomemberbindpanel.publish.current.member.2',
              'Publish current member',
            )}
            titleHelp={t(
              'pages.studio.bind.studiomemberbindpanel.bind.publishes.the.current.revision.2',
              'Bind publishes the current revision first, then Studio reveals the invoke URL, endpoint contract, and smoke-test entry for this member.',
            )}
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <div style={parameterGridStyle}>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.implementation.kind.2',
                      'Implementation kind',
                    )}
                  </Typography.Text>
                  <Typography.Text strong>
                    {pendingBindingCandidate.kind === 'workflow'
                      ? t(
                          'pages.studio.bind.studiomemberbindpanel.workflow.2',
                          'Workflow',
                        )
                      : pendingBindingCandidate.kind === 'script'
                        ? t(
                            'pages.studio.bind.studiomemberbindpanel.script.2',
                            'Script',
                          )
                        : t(
                            'pages.studio.bind.studiomemberbindpanel.gagent.2',
                            'GAgent',
                          )}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.current.member.3',
                      'Current member',
                    )}
                  </Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {pendingBindingCandidate.displayName}
                  </Typography.Text>
                </div>
                <div style={valueCardStyle}>
                  <Typography.Text type="secondary">
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.workspace.id.3',
                      'Workspace ID',
                    )}
                  </Typography.Text>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {scopeId}
                  </Typography.Text>
                </div>
              </div>
              <Typography.Text type="secondary">
                {pendingBindingCandidate.description}
              </Typography.Text>
              <ConsoleOperationNotice
                errorMessage={t(
                  'pages.studio.bind.studiomemberbindpanel.bindingActionFailed',
                  'Binding action could not be completed. Try again.',
                )}
                notice={pendingBindNotice}
                onClose={() => setPendingBindNotice(null)}
              />
              {currentBindingRunNotice ? (
                <Alert
                  showIcon
                  message={currentBindingRunNotice.message}
                  description={`Run ${currentBindingRun?.bindingRunId}. ${bindFlowGuidance.message}`}
                  type={currentBindingRunNotice.type}
                />
              ) : null}
              <div
                data-testid="studio-bind-flow-guidance"
                style={flowGuidanceCardStyle}
              >
                <div style={flowGuidanceHeaderStyle}>
                  <Typography.Text strong>
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.lifecycle.guidance',
                      'Lifecycle guidance',
                    )}
                  </Typography.Text>
                  <Tag
                    color={
                      bindFlowGuidance.stage === 'ready' ? 'green' : 'blue'
                    }
                  >
                    {bindFlowGuidance.stage}
                  </Tag>
                </div>
                <Typography.Text type="secondary">
                  {bindFlowGuidance.message}
                </Typography.Text>
              </div>
              {postBindEntryActions ? (
                <Alert
                  showIcon
                  type="success"
                  message={
                    postBindEntryActions.isEntryMember
                      ? 'This member is the Team entry.'
                      : 'This member can be the Team entry.'
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
          message={t(
            'pages.studio.bind.studiomemberbindpanel.no.published.contract.is.available.2',
            'No published contract is available for this member in the current workspace yet.',
          )}
          description={t(
            'pages.studio.bind.studiomemberbindpanel.bind.workflow.script.or.gagent.2',
            'Bind a workflow, script, or gagent revision first so Studio can reveal the invoke contract.',
          )}
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
          title={t(
            'pages.studio.bind.studiomemberbindpanel.current.member.publication.2',
            'Current member publication',
          )}
          titleHelp={t(
            'pages.studio.bind.studiomemberbindpanel.bind.is.pinned.to.the.2',
            'Bind is pinned to the selected member. Published service ids stay visible only as supporting diagnostics.',
          )}
          extra={
            <Space wrap size={[6, 6]}>
              <Tag color={bindContract ? 'green' : 'default'}>
                {bindContract
                  ? t(
                      'pages.studio.bind.studiomemberbindpanel.member.contract.selected.2',
                      'member contract selected',
                    )
                  : t(
                      'pages.studio.bind.studiomemberbindpanel.needs.endpoint.2',
                      'needs endpoint',
                    )}
              </Tag>
              {revisionList.length > 0 ? (
                <Tag>
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.revisions.2',
                    'revisions ·',
                  )}
                  {revisionList.length}
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
                  ? 'This member is the Team entry.'
                  : 'This member can be the Team entry.'
              }
              description={renderPostBindEntryAction(postBindEntryActions)}
            />
          ) : null}
          <div style={sourcePanelStyle}>
            <div style={sourceSummaryStyle}>
              <div style={sourceStatusStyle}>
                <ApiOutlined />
                <Typography.Text strong>
                  {getUserFacingIdentifierLabel(
                    selectedService?.displayName || selectedService?.serviceId,
                    t(
                      'pages.studio.bind.studiomemberbindpanel.no.published.service.2',
                      'No published service',
                    ),
                  )}
                </Typography.Text>
                {selectedEndpoint ? (
                  <Typography.Text type="secondary">
                    /{' '}
                    {getUserFacingIdentifierLabel(
                      selectedEndpoint.displayName ||
                        selectedEndpoint.endpointId,
                      t(
                        'pages.studio.bind.studiomemberbindpanel.endpoint',
                        'Endpoint',
                      ),
                    )}
                  </Typography.Text>
                ) : null}
              </div>
              {bindContract ? (
                <Button
                  icon={<CopyOutlined />}
                  onClick={() => void copyText(bindContract.invokeUrl)}
                >
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.copy.url.2',
                    'Copy URL',
                  )}
                </Button>
              ) : null}
            </div>
            <div style={controlsGridStyle}>
              <div style={sourceControlStackStyle}>
                <Typography.Text type="secondary">
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.current.member.4',
                    'Current member',
                  )}
                </Typography.Text>
                <div style={valueCardStyle}>
                  <Typography.Text strong style={{ wordBreak: 'break-word' }}>
                    {getUserFacingIdentifierLabel(
                      selectedService?.displayName ||
                        selectedService?.serviceId,
                      t(
                        'pages.studio.bind.studiomemberbindpanel.no.published.contract.2',
                        'No published contract',
                      ),
                    )}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {normalizedMemberId
                      ? t(
                          'pages.studio.bind.studiomemberbindpanel.member.selected',
                          'Member selected',
                        )
                      : t(
                          'pages.studio.bind.studiomemberbindpanel.no.member.selected.2',
                          'No member selected',
                        )}
                  </Typography.Text>
                </div>
              </div>
              <div style={sourceControlStackStyle}>
                <Space direction="vertical" size={2}>
                  <Typography.Text type="secondary">
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.test.mode.2',
                      'Test mode',
                    )}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.copy.4',
                      'For ordinary tests, you can directly enter a sentence; when you need a fixed format, choose advanced input.',
                    )}
                  </Typography.Text>
                </Space>
                {selectedService && hasEndpointOptions ? (
                  <div style={endpointChoiceRowStyle}>
                    {selectedService.endpoints.map((endpoint) => {
                      const active = endpoint.endpointId === selectedEndpointId;
                      const label = getUserFacingIdentifierLabel(
                        endpoint.displayName || endpoint.endpointId,
                        t(
                          'pages.studio.bind.studiomemberbindpanel.endpoint',
                          'Endpoint',
                        ),
                      );
                      const foregroundColor = active ? '#ffffff' : '#0f172a';
                      const secondaryColor = active
                        ? 'rgba(255, 255, 255, 0.74)'
                        : '#64748b';
                      return (
                        <button
                          aria-pressed={active}
                          className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                          key={endpoint.endpointId}
                          type="button"
                          style={
                            active
                              ? endpointChoiceButtonActiveStyle
                              : endpointChoiceButtonStyle
                          }
                          onClick={() =>
                            setSelectedEndpointId(endpoint.endpointId)
                          }
                        >
                          <span style={endpointChoiceTitleStyle}>
                            <span
                              style={{
                                ...endpointChoiceNameStyle,
                                color: foregroundColor,
                              }}
                            >
                              {label}
                            </span>
                            <Tag
                              color={
                                isChatServiceEndpoint(endpoint)
                                  ? 'geekblue'
                                  : 'default'
                              }
                              style={{ marginInlineEnd: 0 }}
                            >
                              {describeEndpointKind(endpoint)}
                            </Tag>
                          </span>
                          <span
                            style={{
                              ...endpointChoiceMetaStyle,
                              color: secondaryColor,
                            }}
                          >
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.endpoint.ready',
                              'Endpoint ready',
                            )}
                          </span>
                          <span
                            style={{
                              ...endpointChoiceDescriptionStyle,
                              color: secondaryColor,
                            }}
                          >
                            {trimOptional(endpoint.description) ||
                              describeEndpointPurpose(endpoint)}
                          </span>
                        </button>
                      );
                    })}
                  </div>
                ) : (
                  <div style={valueCardStyle}>
                    <Typography.Text strong>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.no.endpoint.data.available.2',
                        'No endpoint data available',
                      )}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.this.member.publication.has.not.2',
                        'This member publication has not exposed callable endpoints yet. Bind can still show revision diagnostics below.',
                      )}
                    </Typography.Text>
                  </div>
                )}
              </div>
            </div>
            {endpointUnavailableMessage ? (
              <Alert
                showIcon
                message={endpointUnavailableMessage}
                description={t(
                  'pages.studio.bind.studiomemberbindpanel.the.service.revision.history.can.2',
                  'The service revision history can still load, but Invoke needs endpoint data on the selected service contract.',
                )}
                type="warning"
              />
            ) : null}
            {!normalizedMemberId && selectedService && selectedEndpoint ? (
              <Alert
                showIcon
                message={t(
                  'pages.studio.bind.studiomemberbindpanel.select.team.member.before.using.2',
                  'Select a Team member before using Invoke.',
                )}
                description={t(
                  'pages.studio.bind.studiomemberbindpanel.bind.can.inspect.the.published.2',
                  'Bind can inspect the published service, but Studio only reveals invoke URLs and live requests after the route resolves to a backend member.',
                )}
                type="info"
              />
            ) : null}
            <div
              data-testid="studio-bind-flow-guidance"
              style={flowGuidanceCardStyle}
            >
              <div style={flowGuidanceHeaderStyle}>
                <Typography.Text strong>
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.lifecycle.guidance.2',
                    'Lifecycle guidance',
                  )}
                </Typography.Text>
                <Tag
                  color={
                    bindFlowGuidance.stage === 'ready'
                      ? 'green'
                      : bindFlowGuidance.stage === 'failed'
                        ? 'red'
                        : 'blue'
                  }
                >
                  {bindFlowGuidance.stage}
                </Tag>
              </div>
              <Typography.Text type="secondary">
                {bindFlowGuidance.message}
              </Typography.Text>
            </div>
          </div>
        </AevatarPanel>

        <div style={contractAndActionsGridStyle}>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            style={equalHeightPanelStyle}
            title={t(
              'pages.studio.bind.studiomemberbindpanel.current.member.contract.2',
              'Current member contract',
            )}
            titleHelp={t(
              'pages.studio.bind.studiomemberbindpanel.keep.only.the.callable.essentials.2',
              'Keep only the callable essentials here so the page opens with the method, URL, auth, and revision at a glance.',
            )}
          >
            <div
              data-testid="studio-bind-contract-section"
              style={contractSectionStyle}
            >
              <Typography.Text type="secondary">
                {runsCurrentWorkflowDraft
                  ? t(
                      'pages.studio.bind.studiomemberbindpanel.keep.the.current.draft.in.focus',
                      'Keep the current draft in focus here; the smoke test and snippets below are the two fastest follow-up actions.',
                    )
                  : t(
                      'pages.studio.bind.studiomemberbindpanel.keep.the.active.invoke.contract.in',
                      'Keep the active invoke contract in focus here; the smoke test and snippets below are the two fastest follow-up actions.',
                    )}
              </Typography.Text>
              {bindContract ? (
                <>
                  <div
                    data-testid="studio-bind-contract-card"
                    style={contractUrlCardStyle}
                  >
                    <div style={contractMethodStyle}>{bindContract.method}</div>
                    <div style={contractUrlStyle}>{bindContract.invokeUrl}</div>
                  </div>
                  <Space wrap size={[6, 6]}>
                    <Tag>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.auth.2',
                        'auth ·',
                      )}
                      {bindContract.authLabel}
                    </Tag>
                    <Tag>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.revision.3',
                        'revision ready',
                      )}
                    </Tag>
                    {bindContract.streaming.sse ? (
                      <Tag color="gold">
                        {t(
                          'pages.studio.bind.studiomemberbindpanel.stream.text.event.stream.2',
                          'stream · text/event-stream',
                        )}
                      </Tag>
                    ) : (
                      <Tag>
                        {t(
                          'pages.studio.bind.studiomemberbindpanel.response.application.json.2',
                          'response · application/json',
                        )}
                      </Tag>
                    )}
                    {bindContract.streaming.aguiFrames ? (
                      <Tag color="geekblue">
                        {t(
                          'pages.studio.bind.studiomemberbindpanel.agui.frames.2',
                          'AGUI frames',
                        )}
                      </Tag>
                    ) : null}
                  </Space>
                </>
              ) : (
                <Alert
                  showIcon
                  message={t(
                    'pages.studio.bind.studiomemberbindpanel.select.an.endpoint.to.reveal.2',
                    'Select an endpoint to reveal the invoke contract.',
                  )}
                  description={
                    normalizedMemberId
                      ? 'The contract URL, revision badge, snippets, and smoke test are generated from the selected member endpoint.'
                      : 'Invoke is member-scoped. Select or create a backend member before Studio reveals the invoke contract.'
                  }
                  type="info"
                />
              )}
            </div>
          </AevatarPanel>

          <AevatarPanel
            layoutMode="document"
            padding={14}
            style={equalHeightPanelStyle}
            title={t(
              'pages.studio.bind.studiomemberbindpanel.quick.smoke.test.2',
              'Quick smoke test',
            )}
            titleHelp={
              runsCurrentWorkflowDraft
                ? t(
                    'pages.studio.bind.studiomemberbindpanel.quick.smoke.test.workflow.draft.help',
                    'Quick smoke test runs the current Studio workflow draft before publish. Continue to Invoke when you want to verify the published contract and endpoint.',
                  )
                : t(
                    'pages.studio.bind.studiomemberbindpanel.quick.smoke.test.invoke.help',
                    'Use a light contract check here, then move into Invoke for the full transcript and event stream.',
                  )
            }
          >
            <div
              data-testid="studio-bind-smoke-test-section"
              style={workflowSectionStyle}
            >
              <div style={{ display: 'grid', gap: 6 }}>
                <Typography.Text strong>
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.authorization.3',
                    'Authorization',
                  )}
                </Typography.Text>
                <Typography.Text type="secondary">
                  {runsCurrentWorkflowDraft
                    ? t(
                        'pages.studio.bind.studiomemberbindpanel.current.draft.smoke.tests.use.studio.2',
                        'Current draft smoke tests use Studio draft execution. Published endpoint authorization is checked after you continue to Invoke.',
                      )
                    : bindContract?.authAuthenticated
                      ? t(
                          'pages.studio.bind.studiomemberbindpanel.in.browser.studio.requests.attach.the',
                          '{value1} In-browser Studio requests attach the active bearer session automatically.',
                          { value1: bindContract.authHint },
                        )
                      : bindContract?.authEnabled
                        ? t(
                            'pages.studio.bind.studiomemberbindpanel.sign.in.before.running.smoke.test',
                            '{value1} Sign in before running a smoke test.',
                            { value1: bindContract?.authHint },
                          )
                        : bindContract?.authHint ||
                          t(
                            'pages.studio.bind.studiomemberbindpanel.studio.auth.is.not.enabled.for',
                            'Studio auth is not enabled for this environment.',
                          )}
                </Typography.Text>
                {runsCurrentWorkflowDraft ? (
                  <Space wrap size={[6, 6]}>
                    <Tag color="blue">
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.current.draft.2',
                        'Current draft',
                      )}
                    </Tag>
                    <Typography.Text type="secondary">
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.quick.smoke.test.runs.the.2',
                        'Quick smoke test runs the current Studio draft before publish.',
                      )}
                    </Typography.Text>
                  </Space>
                ) : null}
              </div>
              <div style={smokeFieldStyle}>
                <Typography.Text strong>
                  {runsCurrentWorkflowDraft ||
                  (selectedEndpoint && isChatServiceEndpoint(selectedEndpoint))
                    ? t(
                        'pages.studio.bind.studiomemberbindpanel.prompt.2',
                        'Prompt',
                      )
                    : t(
                        'pages.studio.bind.studiomemberbindpanel.prompt.command.input.2',
                        'Prompt / command input',
                      )}
                </Typography.Text>
                <Input.TextArea
                  aria-label={t(
                    'pages.studio.bind.studiomemberbindpanel.bind.smoke.test.input.2',
                    'Bind smoke test input',
                  )}
                  autoSize={{ minRows: 4, maxRows: 8 }}
                  placeholder={
                    runsCurrentWorkflowDraft
                      ? 'Ask the current workflow draft to do a quick task...'
                      : selectedEndpoint &&
                          isChatServiceEndpoint(selectedEndpoint)
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
                  message={t(
                    'pages.studio.bind.studiomemberbindpanel.copy.5',
                    'Fixed-format input',
                  )}
                  description={
                    <Typography.Text style={smokeTypedPayloadDescriptionStyle}>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.api.sdk.invoke',
                        'This entry is primarily for API/SDK calls. The current input is sent as simple text; continue to Invoke when you need to debug the full fixed-format input.',
                      )}
                      <br />
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.request.type.2',
                        'Request type:',
                      )}
                      {bindContract.requestTypeUrl}
                    </Typography.Text>
                  }
                  type="warning"
                />
              ) : null}
              <div style={smokeActionStackStyle}>
                <Alert
                  showIcon
                  message={t(
                    'pages.studio.bind.studiomemberbindpanel.next.step',
                    'Next step',
                  )}
                  description={
                    canUsePublishedMemberInvoke
                      ? 'Continue to Invoke opens a full run transcript for the same member and endpoint. Observe will receive the latest run context after Invoke starts.'
                      : bindFlowGuidance.message
                  }
                  type={bindFlowGuidance.type}
                />
                <Button
                  block
                  icon={<CheckCircleOutlined />}
                  loading={smokeTestResult.status === 'running'}
                  type="primary"
                  disabled={
                    (!runsCurrentWorkflowDraft &&
                      !canUsePublishedMemberInvoke) ||
                    publishedSmokeRequiresAuth
                  }
                  onClick={() => void handleRunSmokeTest()}
                >
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.send.smoke.test.2',
                    'Send smoke test',
                  )}
                </Button>
                <Button
                  block
                  icon={<LinkOutlined />}
                  disabled={!canUsePublishedMemberInvoke}
                  onClick={() => {
                    if (
                      !canUsePublishedMemberInvoke ||
                      !selectedService ||
                      !selectedEndpoint
                    ) {
                      return;
                    }

                    onContinueToInvoke?.(
                      selectedService.serviceId,
                      selectedEndpoint.endpointId,
                    );
                  }}
                >
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.continue.to.invoke.2',
                    'Continue to Invoke',
                  )}
                </Button>
              </div>
              {smokeTestResult.status === 'success' ? (
                <Alert
                  showIcon
                  message={`Smoke test passed in ${smokeTestResult.latencyMs}ms`}
                  description={
                    smokeTestResult.runId
                      ? t(
                          'pages.studio.bind.studiomemberbindpanel.run.completed',
                          'Run completed',
                        )
                      : runsCurrentWorkflowDraft
                        ? 'The current Studio draft completed without an error.'
                        : 'The selected contract returned without an error.'
                  }
                  type="success"
                />
              ) : smokeTestResult.status === 'error' ? (
                <Alert
                  showIcon
                  message={t(
                    'pages.studio.bind.studiomemberbindpanel.smoke.test.failed.2',
                    'Smoke test failed',
                  )}
                  description={smokeTestResult.error}
                  type="error"
                />
              ) : null}
              {smokeTestResult.responseSummary ? (
                runsCurrentWorkflowDraft || bindContract?.streaming.sse ? (
                  <div style={{ display: 'grid', gap: 10 }}>
                    <Typography.Text strong>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.streaming.summary.2',
                        'Streaming summary',
                      )}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {smokeTestResult.eventCount}{' '}
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.observed.events.2',
                        'observed events',
                      )}
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
                    <Typography.Text strong>
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.response.summary.2',
                        'Response summary',
                      )}
                    </Typography.Text>
                    <pre style={{ ...snippetBlockStyle, margin: 0 }}>
                      {smokeTestResult.responseSummary}
                    </pre>
                  </div>
                )
              ) : null}
            </div>
          </AevatarPanel>
        </div>

        <div data-testid="studio-bind-primary-grid" style={workflowGridStyle}>
          <AevatarPanel
            layoutMode="document"
            padding={14}
            style={equalHeightPanelStyle}
            title={t(
              'pages.studio.bind.studiomemberbindpanel.integration.snippets.2',
              'Integration snippets',
            )}
            titleHelp={t(
              'pages.studio.bind.studiomemberbindpanel.give.the.user.ready.to.2',
              'Give the user a ready-to-copy call shape right away, without making them hunt through the support sections.',
            )}
          >
            {bindContract ? (
              <div
                data-testid="studio-bind-snippet-section"
                style={workflowSectionStyle}
              >
                <div style={snippetHeaderStyle}>
                  <div style={snippetTabsStyle}>
                    {(['curl', 'fetch', 'sdk'] as SnippetTab[]).map(
                      (tabKey) => (
                        <button
                          aria-pressed={snippetTab === tabKey}
                          className={AEVATAR_INTERACTIVE_CHIP_CLASS}
                          key={tabKey}
                          type="button"
                          style={{
                            ...snippetTabButtonStyle,
                            background:
                              snippetTab === tabKey ? '#111827' : '#ffffff',
                            borderColor:
                              snippetTab === tabKey ? '#111827' : '#d9d9d9',
                            color:
                              snippetTab === tabKey ? '#ffffff' : '#111827',
                          }}
                          onClick={() => setSnippetTab(tabKey)}
                        >
                          {tabKey.toUpperCase()}
                        </button>
                      ),
                    )}
                  </div>
                  <Button
                    icon={<CopyOutlined />}
                    onClick={() => void copyText(selectedSnippet)}
                  >
                    {t(
                      'pages.studio.bind.studiomemberbindpanel.copy.snippet.2',
                      'Copy snippet',
                    )}
                  </Button>
                </div>
                <Typography.Text type="secondary">
                  {t(
                    'pages.studio.bind.studiomemberbindpanel.use.the.selected.snippet.to.2',
                    'Use the selected snippet to call the current member contract from your shell, browser, or SDK.',
                  )}
                </Typography.Text>
                <pre style={snippetPreviewStyle}>{selectedSnippet}</pre>
              </div>
            ) : (
              <Empty
                description={t(
                  'pages.studio.bind.studiomemberbindpanel.inspect.one.contract.first.to.2',
                  'Inspect one contract first to generate its snippets.',
                )}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>
        </div>

        <AevatarPanel
          layoutMode="document"
          padding={14}
          title={t(
            'pages.studio.bind.studiomemberbindpanel.supporting.details.2',
            'Supporting details',
          )}
          titleHelp={t(
            'pages.studio.bind.studiomemberbindpanel.keep.the.source.selector.routing.2',
            'Keep the source selector, routing, bindings, and revision history available below the primary workflow.',
          )}
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
                  label: t(
                    'pages.studio.bind.studiomemberbindpanel.contract.details.2',
                    'Contract details',
                  ),
                  children: bindContract ? (
                    <div style={parameterGridStyle}>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.published.service.2',
                            'Published service',
                          )}
                        </Typography.Text>
                        <Typography.Text
                          strong
                          style={{ wordBreak: 'break-word' }}
                        >
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.service.ready',
                            'Service ready',
                          )}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.platform.diagnostic.id.for.this.2',
                            'Platform diagnostic id for this member contract.',
                          )}
                        </Typography.Text>
                      </div>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.workspace.id.4',
                            'Workspace ID',
                          )}
                        </Typography.Text>
                        <Typography.Text
                          strong
                          style={{ wordBreak: 'break-word' }}
                        >
                          {bindContract.scopeLabel}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {bindContract.scopeSource
                            ? t(
                                'pages.studio.bind.studiomemberbindpanel.resolved.from',
                                'Resolved from {value1}.',
                                { value1: bindContract.scopeSource },
                              )
                            : t(
                                'pages.studio.bind.studiomemberbindpanel.bound.to.the.current.studio.workspace',
                                'Bound to the current Studio workspace.',
                              )}
                        </Typography.Text>
                      </div>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.authorization.4',
                            'Authorization',
                          )}
                        </Typography.Text>
                        <Typography.Text strong>
                          {bindContract.authLabel}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {bindContract.authHint}
                        </Typography.Text>
                      </div>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.revision.4',
                            'Revision',
                          )}
                        </Typography.Text>
                        <Typography.Text strong>
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.revision.ready',
                            'Revision ready',
                          )}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {bindContract.serviceDisplayName}
                        </Typography.Text>
                      </div>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.delivery.2',
                            'Delivery',
                          )}
                        </Typography.Text>
                        <Typography.Text strong>
                          {bindContract.method}
                        </Typography.Text>
                        <Typography.Text type="secondary">
                          {bindContract.streaming.sse
                            ? t(
                                'pages.studio.bind.studiomemberbindpanel.streams.through.text.event.stream.2',
                                'Streams through text/event-stream.',
                              )
                            : t(
                                'pages.studio.bind.studiomemberbindpanel.returns.single.json.response.2',
                                'Returns a single JSON response.',
                              )}
                        </Typography.Text>
                      </div>
                      <div style={valueCardStyle}>
                        <Typography.Text type="secondary">
                          {t(
                            'pages.studio.bind.studiomemberbindpanel.streaming.2',
                            'Streaming',
                          )}
                        </Typography.Text>
                        <Space wrap size={[6, 6]}>
                          <Tag
                            color={
                              bindContract.streaming.sse ? 'blue' : 'default'
                            }
                          >
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.sse.2',
                              'SSE',
                            )}
                          </Tag>
                          <Tag
                            color={
                              bindContract.streaming.webSocket
                                ? 'blue'
                                : 'default'
                            }
                          >
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.websocket.2',
                              'WebSocket',
                            )}
                          </Tag>
                          <Tag
                            color={
                              bindContract.streaming.aguiFrames
                                ? 'geekblue'
                                : 'default'
                            }
                          >
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.agui.2',
                              'AGUI',
                            )}
                          </Tag>
                        </Space>
                      </div>
                      {bindContract.requestTypeUrl ? (
                        <div style={valueCardStyle}>
                          <Typography.Text type="secondary">
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.request.schema.2',
                              'Request schema',
                            )}
                          </Typography.Text>
                          <Typography.Text
                            strong
                            style={{ wordBreak: 'break-word' }}
                          >
                            {bindContract.requestTypeUrl}
                          </Typography.Text>
                        </div>
                      ) : null}
                      {bindContract.responseTypeUrl ? (
                        <div style={valueCardStyle}>
                          <Typography.Text type="secondary">
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.response.schema.2',
                              'Response schema',
                            )}
                          </Typography.Text>
                          <Typography.Text
                            strong
                            style={{ wordBreak: 'break-word' }}
                          >
                            {bindContract.responseTypeUrl}
                          </Typography.Text>
                        </div>
                      ) : null}
                    </div>
                  ) : (
                    <Empty
                      description={t(
                        'pages.studio.bind.studiomemberbindpanel.keep.one.published.contract.in.2',
                        'Keep one published contract in focus to review its details.',
                      )}
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                    />
                  ),
                },
                {
                  key: 'bound-dependencies',
                  label: buildBindingSectionTitle(bindingList.length),
                  children: bindingsQuery.isLoading ? (
                    <Typography.Text type="secondary">
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.loading.bindings.2',
                        'Loading bindings...',
                      )}
                    </Typography.Text>
                  ) : bindingList.length > 0 ? (
                    <div style={listColumnStyle}>
                      {bindingList.map((binding) => (
                        <div key={binding.bindingId} style={compactCardStyle}>
                          <Space wrap size={[8, 8]}>
                            <Typography.Text strong>
                              {getUserFacingIdentifierLabel(
                                binding.displayName || binding.bindingId,
                                t(
                                  'pages.studio.bind.studiomemberbindpanel.binding',
                                  'Binding',
                                ),
                              )}
                            </Typography.Text>
                            <AevatarStatusTag
                              domain="governance"
                              label={binding.bindingKind}
                              status={binding.retired ? 'retired' : 'active'}
                            />
                          </Space>
                          <Typography.Text type="secondary">
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.target.2',
                              'Target',
                            )}
                            {describeScopeServiceBindingTarget(binding)}
                          </Typography.Text>
                          <Typography.Text type="secondary">
                            {t(
                              'pages.studio.bind.studiomemberbindpanel.policies.2',
                              'Policies',
                            )}
                            {binding.policyIds.length > 0
                              ? t(
                                  'pages.studio.bind.studiomemberbindpanel.policy.count',
                                  '{value1} policies',
                                  {
                                    value1: binding.policyIds.length,
                                  },
                                )
                              : 'none'}
                          </Typography.Text>
                        </div>
                      ))}
                    </div>
                  ) : (
                    <Empty
                      description={t(
                        'pages.studio.bind.studiomemberbindpanel.this.service.does.not.depend.2',
                        'This service does not depend on any extra connectors, secrets, or service bindings in the current workspace.',
                      )}
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                    />
                  ),
                },
                {
                  key: 'revisions',
                  label: t(
                    'pages.studio.bind.studiomemberbindpanel.revisions.3',
                    'Revisions ({value1})',
                    { value1: revisionList.length },
                  ),
                  children: revisionCatalogQuery.isLoading ? (
                    <Typography.Text type="secondary">
                      {t(
                        'pages.studio.bind.studiomemberbindpanel.loading.published.revisions.2',
                        'Loading published revisions...',
                      )}
                    </Typography.Text>
                  ) : revisionCatalogQuery.error ? (
                    <Alert
                      showIcon
                      message={t(
                        'pages.studio.bind.studiomemberbindpanel.failed.to.load.revisions.2',
                        'Failed to load revisions',
                      )}
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
                        const isCurrent =
                          revision.revisionId ===
                          currentPublishedRevision?.revisionId;
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
                              <Typography.Text strong>
                                {formatStudioMemberBindingImplementationKind(
                                  revision.implementationKind,
                                )}
                              </Typography.Text>
                              <AevatarStatusTag
                                domain="governance"
                                label={formatStudioMemberBindingImplementationKind(
                                  revision.implementationKind,
                                )}
                                status={revision.status || 'draft'}
                              />
                              {revision.isDefaultServing ? (
                                <Tag color="green">
                                  {t(
                                    'pages.studio.bind.studiomemberbindpanel.default.2',
                                    'default',
                                  )}
                                </Tag>
                              ) : null}
                              {revision.isActiveServing ? (
                                <Tag color="blue">
                                  {t(
                                    'pages.studio.bind.studiomemberbindpanel.active.2',
                                    'active',
                                  )}
                                </Tag>
                              ) : null}
                              {revision.retiredAt ? (
                                <Tag color="red">
                                  {t(
                                    'pages.studio.bind.studiomemberbindpanel.retired.2',
                                    'retired',
                                  )}
                                </Tag>
                              ) : null}
                              {isCurrent ? (
                                <Tag color="gold">
                                  {t(
                                    'pages.studio.bind.studiomemberbindpanel.current.contract.2',
                                    'current contract',
                                  )}
                                </Tag>
                              ) : null}
                            </Space>
                            <Typography.Text type="secondary">
                              {describeStudioMemberBindingRevisionTarget(
                                revision,
                              )}{' '}
                              ·{' '}
                              {describeStudioMemberBindingRevisionContext(
                                revision,
                              ) ||
                                t(
                                  'pages.studio.bind.studiomemberbindpanel.no.detail.2',
                                  'No detail',
                                )}
                            </Typography.Text>
                            <Typography.Text type="secondary">
                              {t(
                                'pages.studio.bind.studiomemberbindpanel.serving.2',
                                'Serving',
                              )}
                              {revision.servingState ||
                                revision.status ||
                                'unknown'}{' '}
                              {t(
                                'pages.studio.bind.studiomemberbindpanel.published.2',
                                '· Published',
                              )}{' '}
                              {formatDateTime(revision.publishedAt)}
                            </Typography.Text>
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <Empty
                      description={t(
                        'pages.studio.bind.studiomemberbindpanel.no.published.revisions.are.available.2',
                        'No published revisions are available for this contract yet.',
                      )}
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
