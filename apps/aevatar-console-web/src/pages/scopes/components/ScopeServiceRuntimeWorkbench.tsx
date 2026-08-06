import {
  ApiOutlined,
  BranchesOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  LinkOutlined,
  PlusOutlined,
  RetweetOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Empty,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Tabs,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { scopeRuntimeApi } from '@/shared/api/scopeRuntimeApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import type { ServiceBindingSnapshot } from '@/shared/models/governance';
import {
  describeScopeServiceBindingTarget,
  getScopeServiceCurrentRevision,
  type ScopeServiceBindingInput,
  type ScopeServiceRunSummary,
} from '@/shared/models/runtime/scopeServices';
import type { ServiceCatalogSnapshot } from '@/shared/models/services';
import { history } from '@/shared/navigation/history';
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from '@/shared/navigation/runtimeRoutes';
import {
  describeStudioMemberBindingRevisionContext,
  describeStudioMemberBindingRevisionTarget,
  formatStudioMemberBindingImplementationKind,
} from '@/shared/studio/models';
import {
  AevatarInspectorEmpty,
  AevatarPanel,
  AevatarStatusTag,
} from '@/shared/ui/aevatarPageShells';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';

type ScopeServiceRuntimeWorkbenchProps = {
  readonly scopeId: string;
  readonly services: readonly ServiceCatalogSnapshot[];
  readonly selectedServiceId?: string;
  readonly selectedEndpointId?: string;
  readonly onSelectService?: (serviceId: string) => void;
  readonly onUseEndpoint: (serviceId: string, endpointId: string) => void;
  readonly initialServiceId?: string;
  readonly preferredServiceId?: string;
  readonly onSelectionChange?: (selection: {
    serviceId: string;
    endpointId: string;
  }) => void;
};

type ServiceRuntimeTab = 'overview' | 'bindings' | 'revisions' | 'runs';
type BindingEditorMode = 'create' | 'edit';

type BindingEditorState = {
  readonly mode: BindingEditorMode;
  readonly bindingId?: string;
} | null;

type BindingEditorDraft = {
  readonly bindingId: string;
  readonly displayName: string;
  readonly bindingKind: string;
  readonly policyIdsText: string;
  readonly targetServiceId: string;
  readonly targetEndpointId: string;
  readonly connectorType: string;
  readonly connectorId: string;
  readonly secretName: string;
};

type RunAuditTarget = {
  readonly runId: string;
  readonly actorId: string;
} | null;

type RuntimeActionLocation = 'bindings' | 'binding-editor' | 'revisions';

type RuntimeActionStatus =
  | 'submitting'
  | 'accepted'
  | 'observing'
  | 'refreshing'
  | 'delayed'
  | 'failed'
  | 'observed';

type RuntimeActionTarget =
  | {
      readonly kind: 'binding-retired';
      readonly bindingId: string;
    }
  | {
      readonly kind: 'binding-payload';
      readonly bindingId: string;
      readonly payload: ScopeServiceBindingInput;
    }
  | {
      readonly kind: 'revision-retired';
      readonly revisionId: string;
    };

type RuntimeActionState = {
  readonly requestId: number;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly location: RuntimeActionLocation;
  readonly status: RuntimeActionStatus;
  readonly target: RuntimeActionTarget;
};

type RuntimeActionIdentity = {
  readonly scopeId: string;
  readonly serviceId: string;
};

type RetiringBindingState = RuntimeActionIdentity & {
  readonly bindingId: string;
};

type BindingEditorField =
  | 'bindingId'
  | 'targetServiceId'
  | 'connectorType'
  | 'connectorId'
  | 'secretName';

type BindingEditorValidationErrors = Partial<
  Record<BindingEditorField, string>
>;

const scopeServiceAppId = 'default';
const scopeServiceNamespace = 'default';

function scopeRuntimeBindingsQueryKey(scopeId: string, serviceId: string) {
  return ['scope-runtime', 'bindings', scopeId, serviceId] as const;
}

function scopeRuntimeRevisionsQueryKey(scopeId: string, serviceId: string) {
  return ['scope-runtime', 'revisions', scopeId, serviceId] as const;
}

function runtimeActionIdentityKey(identity: RuntimeActionIdentity): string {
  return `${identity.scopeId}\u0000${identity.serviceId}`;
}

function runtimeActionBlocksWrites(status: RuntimeActionStatus): boolean {
  return (
    status === 'submitting' ||
    status === 'accepted' ||
    status === 'observing' ||
    status === 'refreshing' ||
    status === 'delayed' ||
    status === 'failed'
  );
}

function buildScopedServiceCatalogHref(
  scopeId: string,
  serviceId: string,
): string {
  const params = new URLSearchParams();
  params.set('tenantId', scopeId.trim());
  params.set('appId', scopeServiceAppId);
  params.set('namespace', scopeServiceNamespace);
  params.set('serviceId', serviceId.trim());
  return `/services?${params.toString()}`;
}

function createEmptyBindingDraft(): BindingEditorDraft {
  return {
    bindingId: '',
    displayName: '',
    bindingKind: 'service',
    policyIdsText: '',
    targetServiceId: '',
    targetEndpointId: '',
    connectorType: '',
    connectorId: '',
    secretName: '',
  };
}

function createBindingDraftFromSnapshot(
  binding: ServiceBindingSnapshot,
): BindingEditorDraft {
  return {
    bindingId: binding.bindingId,
    displayName: binding.displayName,
    bindingKind: binding.bindingKind,
    policyIdsText: binding.policyIds.join(', '),
    targetServiceId: binding.serviceRef?.identity.serviceId || '',
    targetEndpointId: binding.serviceRef?.endpointId || '',
    connectorType: binding.connectorRef?.connectorType || '',
    connectorId: binding.connectorRef?.connectorId || '',
    secretName: binding.secretRef?.secretName || '',
  };
}

function parsePolicyIds(value: string): string[] {
  return value
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean);
}

function buildBindingPayload(
  draft: BindingEditorDraft,
): ScopeServiceBindingInput {
  const bindingKind = draft.bindingKind.trim() || 'service';
  return {
    bindingId: draft.bindingId.trim(),
    displayName: draft.displayName.trim(),
    bindingKind,
    policyIds: parsePolicyIds(draft.policyIdsText),
    service:
      bindingKind === 'service'
        ? {
            serviceId: draft.targetServiceId.trim(),
            endpointId: draft.targetEndpointId.trim() || null,
          }
        : null,
    connector:
      bindingKind === 'connector'
        ? {
            connectorType: draft.connectorType.trim(),
            connectorId: draft.connectorId.trim(),
          }
        : null,
    secret:
      bindingKind === 'secret'
        ? {
            secretName: draft.secretName.trim(),
          }
        : null,
  };
}

function hasSameValues(
  left: readonly string[],
  right: readonly string[] | undefined,
): boolean {
  const normalizedRight = right ?? [];
  return (
    left.length === normalizedRight.length &&
    left.every((value, index) => value === normalizedRight[index])
  );
}

function bindingReflectsPayload(
  binding: ServiceBindingSnapshot,
  payload: ScopeServiceBindingInput,
): boolean {
  if (
    binding.bindingId !== payload.bindingId ||
    binding.displayName !== payload.displayName ||
    binding.bindingKind !== payload.bindingKind ||
    !hasSameValues(binding.policyIds, payload.policyIds)
  ) {
    return false;
  }

  if (payload.bindingKind === 'service') {
    return (
      binding.serviceRef?.identity.serviceId === payload.service?.serviceId &&
      (binding.serviceRef?.endpointId || null) ===
        (payload.service?.endpointId || null)
    );
  }

  if (payload.bindingKind === 'connector') {
    return (
      binding.connectorRef?.connectorType ===
        payload.connector?.connectorType &&
      binding.connectorRef?.connectorId === payload.connector?.connectorId
    );
  }

  if (payload.bindingKind === 'secret') {
    return binding.secretRef?.secretName === payload.secret?.secretName;
  }

  return false;
}

function isRuntimeActionObserved(
  target: RuntimeActionTarget,
  bindings: readonly ServiceBindingSnapshot[],
  revisions: readonly {
    readonly revisionId: string;
    readonly retiredAt: string | null;
    readonly status: string;
  }[],
): boolean {
  if (target.kind === 'binding-retired') {
    return bindings.some(
      (binding) => binding.bindingId === target.bindingId && binding.retired,
    );
  }

  if (target.kind === 'binding-payload') {
    return bindings.some((binding) =>
      bindingReflectsPayload(binding, target.payload),
    );
  }

  return revisions.some(
    (revision) =>
      revision.revisionId === target.revisionId &&
      (Boolean(revision.retiredAt) ||
        revision.status.trim().toLowerCase() === 'retired'),
  );
}

function getBindingEditorValidationErrors(
  payload: ScopeServiceBindingInput,
): BindingEditorValidationErrors {
  const errors: BindingEditorValidationErrors = {};

  if (!payload.bindingId) {
    errors.bindingId = t(
      'pages.scopes.scopeserviceruntimeworkbench.binding.id.required',
      'Enter a binding ID.',
    );
  }

  if (payload.bindingKind === 'service' && !payload.service?.serviceId) {
    errors.targetServiceId = t(
      'pages.scopes.scopeserviceruntimeworkbench.target.service.required',
      'Select a target service.',
    );
  }

  if (payload.bindingKind === 'connector') {
    if (!payload.connector?.connectorType) {
      errors.connectorType = t(
        'pages.scopes.scopeserviceruntimeworkbench.connector.type.required',
        'Enter a connector type.',
      );
    }
    if (!payload.connector?.connectorId) {
      errors.connectorId = t(
        'pages.scopes.scopeserviceruntimeworkbench.connector.id.required',
        'Enter a connector ID.',
      );
    }
  }

  if (payload.bindingKind === 'secret' && !payload.secret?.secretName) {
    errors.secretName = t(
      'pages.scopes.scopeserviceruntimeworkbench.secret.name.required',
      'Enter a secret name.',
    );
  }

  return errors;
}

const RuntimeActionFeedback: React.FC<{
  readonly action: RuntimeActionState;
  readonly onDismiss: () => void;
  readonly onRefresh: () => void;
}> = ({ action, onDismiss, onRefresh }) => {
  const copyByStatus: Record<
    RuntimeActionStatus,
    {
      readonly description: string;
      readonly title: string;
      readonly type: 'error' | 'info' | 'success' | 'warning';
    }
  > = {
    submitting: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.submitting.description',
        'Sending your update.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.submitting.title',
        'Saving update',
      ),
      type: 'info',
    },
    accepted: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.accepted.description',
        'Checking the latest status.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.accepted.title',
        'Request accepted',
      ),
      type: 'info',
    },
    delayed: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.delayed.description',
        'The latest list has not reflected this request yet. Refresh to check again.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.delayed.title',
        'Update is still pending.',
      ),
      type: 'warning',
    },
    failed: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.failed.description',
        'Refresh the latest status before trying again.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.failed.title',
        'Could not confirm the update',
      ),
      type: 'error',
    },
    observed: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.observed.description',
        'The latest list reflects this change.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.observed.title',
        'Update confirmed.',
      ),
      type: 'success',
    },
    observing: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.observing.description',
        'Waiting for the latest list to update.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.observing.title',
        'Checking current status',
      ),
      type: 'info',
    },
    refreshing: {
      description: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.refreshing.description',
        'Checking the latest list.',
      ),
      title: t(
        'pages.scopes.scopeserviceruntimeworkbench.action.refreshing.title',
        'Refreshing current status',
      ),
      type: 'info',
    },
  };
  const copy = copyByStatus[action.status];
  const canRefresh =
    action.status === 'delayed' ||
    action.status === 'failed' ||
    action.status === 'observed';
  const canDismiss =
    action.status === 'refreshing' ||
    action.status === 'delayed' ||
    action.status === 'failed' ||
    action.status === 'observed';

  return (
    <Alert
      action={
        canRefresh || canDismiss ? (
          <Space size={8} wrap>
            {canRefresh ? (
              <Button onClick={onRefresh} size="small">
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.action.refresh',
                  'Refresh',
                )}
              </Button>
            ) : null}
            {canDismiss ? (
              <Button onClick={onDismiss} size="small" type="text">
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.action.dismiss',
                  'Dismiss',
                )}
              </Button>
            ) : null}
          </Space>
        ) : undefined
      }
      description={copy.description}
      showIcon
      title={copy.title}
      type={copy.type}
    />
  );
};

function getBindingKindLabel(kind: string): string {
  switch (kind.trim().toLowerCase()) {
    case 'service':
      return 'Service';
    case 'connector':
      return 'Connector';
    case 'secret':
      return 'Secret';
    default:
      return kind || 'Binding';
  }
}

const RuntimeMetricCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      border: '1px solid var(--ant-color-border-secondary)',
      borderRadius: 12,
      display: 'flex',
      flexDirection: 'column',
      gap: 4,
      minWidth: 0,
      padding: 12,
    }}
  >
    <Typography.Text type="secondary">{label}</Typography.Text>
    <Typography.Text strong>{value}</Typography.Text>
  </div>
);

const ScopeServiceRuntimeWorkbench: React.FC<
  ScopeServiceRuntimeWorkbenchProps
> = ({
  scopeId,
  services,
  selectedServiceId,
  selectedEndpointId,
  onSelectService,
  onUseEndpoint,
  initialServiceId,
  preferredServiceId,
  onSelectionChange,
}) => {
  const queryClient = useQueryClient();
  const [activeTab, setActiveTab] = useState<ServiceRuntimeTab>('overview');
  const [bindingEditorState, setBindingEditorState] =
    useState<BindingEditorState>(null);
  const [bindingEditorDraft, setBindingEditorDraft] =
    useState<BindingEditorDraft>(createEmptyBindingDraft());
  const [bindingEditorValidationErrors, setBindingEditorValidationErrors] =
    useState<BindingEditorValidationErrors>({});
  const [bindingEditorSubmitting, setBindingEditorSubmitting] = useState(false);
  const [runtimeActionStates, setRuntimeActionStates] = useState<
    ReadonlyMap<string, RuntimeActionState>
  >(() => new Map());
  const [selectedRevisionId, setSelectedRevisionId] = useState('');
  const [selectedRunAuditTarget, setSelectedRunAuditTarget] =
    useState<RunAuditTarget>(null);
  const [retiringBinding, setRetiringBinding] =
    useState<RetiringBindingState | null>(null);
  const runtimeActionRequestId = useRef(0);
  const runtimeActionStatesRef = useRef<Map<string, RuntimeActionState>>(
    new Map(),
  );
  const [internalSelectedServiceId, setInternalSelectedServiceId] = useState(
    () => initialServiceId?.trim() || preferredServiceId?.trim() || '',
  );
  const [internalSelectedEndpointId, setInternalSelectedEndpointId] =
    useState('');
  const isControlledSelection =
    typeof selectedServiceId === 'string' &&
    typeof selectedEndpointId === 'string' &&
    typeof onSelectService === 'function';
  const resolvedSelectedServiceId = isControlledSelection
    ? selectedServiceId || ''
    : internalSelectedServiceId;
  const resolvedSelectedEndpointId = isControlledSelection
    ? selectedEndpointId || ''
    : internalSelectedEndpointId;

  const selectedService =
    services.find(
      (service) => service.serviceId === resolvedSelectedServiceId,
    ) ?? null;
  const selectedServiceRuntimeId = selectedService?.serviceId || '';
  const selectedRuntimeActionIdentity = {
    scopeId,
    serviceId: selectedServiceRuntimeId,
  };

  useEffect(() => {
    if (isControlledSelection) {
      return;
    }

    if (!services.length) {
      setInternalSelectedServiceId('');
      return;
    }

    if (
      resolvedSelectedServiceId &&
      services.some(
        (service) => service.serviceId === resolvedSelectedServiceId,
      )
    ) {
      return;
    }

    const normalizedInitialServiceId = initialServiceId?.trim() || '';
    if (
      normalizedInitialServiceId &&
      services.some(
        (service) => service.serviceId === normalizedInitialServiceId,
      )
    ) {
      setInternalSelectedServiceId(normalizedInitialServiceId);
      return;
    }

    const normalizedPreferredServiceId = preferredServiceId?.trim() || '';
    setInternalSelectedServiceId(
      normalizedPreferredServiceId || services[0]?.serviceId || '',
    );
  }, [
    initialServiceId,
    isControlledSelection,
    preferredServiceId,
    resolvedSelectedServiceId,
    services,
  ]);

  useEffect(() => {
    if (isControlledSelection) {
      return;
    }

    if (!selectedService) {
      setInternalSelectedEndpointId('');
      return;
    }

    if (
      resolvedSelectedEndpointId &&
      selectedService.endpoints.some(
        (endpoint) => endpoint.endpointId === resolvedSelectedEndpointId,
      )
    ) {
      return;
    }

    setInternalSelectedEndpointId(
      selectedService.endpoints[0]?.endpointId || '',
    );
  }, [isControlledSelection, resolvedSelectedEndpointId, selectedService]);

  useEffect(() => {
    if (isControlledSelection) {
      return;
    }

    onSelectionChange?.({
      serviceId: resolvedSelectedServiceId,
      endpointId: resolvedSelectedEndpointId,
    });
  }, [
    isControlledSelection,
    onSelectionChange,
    resolvedSelectedEndpointId,
    resolvedSelectedServiceId,
  ]);

  const handleSelectService = (serviceId: string) => {
    if (isControlledSelection) {
      onSelectService?.(serviceId);
      return;
    }

    setInternalSelectedServiceId(serviceId);
    setInternalSelectedEndpointId('');
  };

  const handleUseEndpoint = (serviceId: string, endpointId: string) => {
    if (!isControlledSelection) {
      setInternalSelectedServiceId(serviceId);
      setInternalSelectedEndpointId(endpointId);
    }

    onUseEndpoint(serviceId, endpointId);
  };

  const bindingsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: scopeRuntimeBindingsQueryKey(scopeId, selectedServiceRuntimeId),
    queryFn: () =>
      scopeRuntimeApi.getServiceBindingCatalogSnapshot(
        scopeId,
        selectedServiceRuntimeId,
      ),
  });

  const revisionsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: scopeRuntimeRevisionsQueryKey(scopeId, selectedServiceRuntimeId),
    queryFn: () =>
      scopeRuntimeApi.getServiceRevisions(scopeId, selectedServiceRuntimeId),
  });

  const runsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ['scope-runtime', 'runs', scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.listServiceRuns(
        scopeId,
        selectedService?.serviceId || '',
        {
          take: 12,
        },
      ),
  });

  const selectedRevisionQuery = useQuery({
    enabled: Boolean(
      scopeId && selectedService?.serviceId && selectedRevisionId,
    ),
    queryKey: [
      'scope-runtime',
      'revision',
      scopeId,
      selectedService?.serviceId,
      selectedRevisionId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRevision(
        scopeId,
        selectedService?.serviceId || '',
        selectedRevisionId,
      ),
  });

  const selectedRunAuditQuery = useQuery({
    enabled: Boolean(
      scopeId &&
        selectedService?.serviceId &&
        selectedRunAuditTarget?.runId.trim(),
    ),
    queryKey: [
      'scope-runtime',
      'run-audit',
      scopeId,
      selectedService?.serviceId,
      selectedRunAuditTarget?.runId,
      selectedRunAuditTarget?.actorId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRunAudit(
        scopeId,
        selectedService?.serviceId || '',
        selectedRunAuditTarget?.runId || '',
        {
          actorId: selectedRunAuditTarget?.actorId || undefined,
        },
      ),
  });

  const isRuntimeActionCurrent = (action: RuntimeActionState) => {
    return (
      runtimeActionStatesRef.current.get(runtimeActionIdentityKey(action))
        ?.requestId === action.requestId
    );
  };

  const hasBlockingRuntimeAction = (identity: RuntimeActionIdentity) => {
    const action = runtimeActionStatesRef.current.get(
      runtimeActionIdentityKey(identity),
    );
    return Boolean(
      action &&
        isRuntimeActionCurrent(action) &&
        runtimeActionBlocksWrites(action.status),
    );
  };

  const createRuntimeAction = (
    location: RuntimeActionLocation,
    target: RuntimeActionTarget,
    identity: RuntimeActionIdentity,
  ): RuntimeActionState | null => {
    if (hasBlockingRuntimeAction(identity)) {
      return null;
    }

    const requestId = runtimeActionRequestId.current + 1;
    const action = {
      location,
      requestId,
      scopeId: identity.scopeId,
      serviceId: identity.serviceId,
      status: 'submitting' as const,
      target,
    };
    runtimeActionRequestId.current = requestId;
    runtimeActionStatesRef.current.set(
      runtimeActionIdentityKey(action),
      action,
    );
    setRuntimeActionStates(new Map(runtimeActionStatesRef.current));
    return action;
  };

  const updateRuntimeActionState = (nextState: RuntimeActionState) => {
    if (isRuntimeActionCurrent(nextState)) {
      runtimeActionStatesRef.current.set(
        runtimeActionIdentityKey(nextState),
        nextState,
      );
      setRuntimeActionStates(new Map(runtimeActionStatesRef.current));
    }
  };

  const dismissRuntimeAction = (action: RuntimeActionState) => {
    if (!isRuntimeActionCurrent(action)) {
      return;
    }
    runtimeActionStatesRef.current.delete(runtimeActionIdentityKey(action));
    setRuntimeActionStates(new Map(runtimeActionStatesRef.current));
  };

  const writesBlockedByRuntimeAction = hasBlockingRuntimeAction(
    selectedRuntimeActionIdentity,
  );

  const invalidateBindingViews = async (action: RuntimeActionState) => {
    if (!isRuntimeActionCurrent(action)) {
      return false;
    }

    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: scopeRuntimeBindingsQueryKey(
          action.scopeId,
          action.serviceId,
        ),
        refetchType: 'none',
      }),
      queryClient.invalidateQueries({
        queryKey: ['scopes', 'invoke', 'services', action.scopeId],
      }),
    ]);

    return isRuntimeActionCurrent(action);
  };

  const invalidateRevisionViews = async (action: RuntimeActionState) => {
    if (!isRuntimeActionCurrent(action)) {
      return false;
    }

    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: scopeRuntimeRevisionsQueryKey(
          action.scopeId,
          action.serviceId,
        ),
        refetchType: 'none',
      }),
      queryClient.invalidateQueries({
        queryKey: ['scopes', 'invoke', 'services', action.scopeId],
      }),
    ]);

    return isRuntimeActionCurrent(action);
  };

  const refreshBindingAction = async (
    action: RuntimeActionState,
    status: 'observing' | 'refreshing',
  ) => {
    if (!isRuntimeActionCurrent(action)) {
      return;
    }

    updateRuntimeActionState({ ...action, status });
    try {
      const bindings = await queryClient.fetchQuery({
        queryFn: () =>
          scopeRuntimeApi.getServiceBindingCatalogSnapshot(
            action.scopeId,
            action.serviceId,
          ),
        queryKey: scopeRuntimeBindingsQueryKey(
          action.scopeId,
          action.serviceId,
        ),
        staleTime: 0,
      });
      if (!isRuntimeActionCurrent(action)) {
        return;
      }

      if (bindings.kind === 'not_materialized') {
        updateRuntimeActionState({ ...action, status: 'delayed' });
        return;
      }

      updateRuntimeActionState({
        ...action,
        status: isRuntimeActionObserved(
          action.target,
          bindings.snapshot.bindings,
          [],
        )
          ? 'observed'
          : 'delayed',
      });
    } catch {
      updateRuntimeActionState({ ...action, status: 'failed' });
    }
  };

  const refreshRevisionAction = async (
    action: RuntimeActionState,
    status: 'observing' | 'refreshing',
  ) => {
    if (!isRuntimeActionCurrent(action)) {
      return;
    }

    updateRuntimeActionState({ ...action, status });
    try {
      const revisions = await queryClient.fetchQuery({
        queryFn: () =>
          scopeRuntimeApi.getServiceRevisions(action.scopeId, action.serviceId),
        queryKey: scopeRuntimeRevisionsQueryKey(
          action.scopeId,
          action.serviceId,
        ),
        staleTime: 0,
      });
      if (!isRuntimeActionCurrent(action)) {
        return;
      }

      updateRuntimeActionState({
        ...action,
        status: isRuntimeActionObserved(action.target, [], revisions.revisions)
          ? 'observed'
          : 'delayed',
      });
    } catch {
      updateRuntimeActionState({ ...action, status: 'failed' });
    }
  };

  const handleRefreshRuntimeAction = (action: RuntimeActionState) => {
    if (!isRuntimeActionCurrent(action)) {
      return;
    }

    if (action.target.kind === 'revision-retired') {
      void refreshRevisionAction(action, 'refreshing');
      return;
    }

    void refreshBindingAction(action, 'refreshing');
  };

  const retireRevisionMutation = useMutation({
    mutationFn: ({
      scopeId: actionScopeId,
      serviceId,
      revisionId,
    }: {
      readonly scopeId: string;
      readonly serviceId: string;
      readonly revisionId: string;
    }) =>
      scopeRuntimeApi.retireServiceRevision(
        actionScopeId,
        serviceId,
        revisionId,
      ),
    onMutate: ({ scopeId: actionScopeId, revisionId, serviceId }) =>
      createRuntimeAction(
        'revisions',
        {
          kind: 'revision-retired',
          revisionId,
        },
        { scopeId: actionScopeId, serviceId },
      ),
    onSuccess: async (_result, _variables, action) => {
      if (!action || !isRuntimeActionCurrent(action)) {
        return;
      }

      const acceptedAction = { ...action, status: 'accepted' as const };
      updateRuntimeActionState(acceptedAction);
      if (await invalidateRevisionViews(acceptedAction)) {
        await refreshRevisionAction(acceptedAction, 'observing');
      }
    },
    onError: (_error, _variables, action) => {
      if (action) {
        updateRuntimeActionState({ ...action, status: 'failed' });
      }
    },
  });

  useEffect(() => {
    setBindingEditorState(null);
    setBindingEditorDraft(createEmptyBindingDraft());
    setBindingEditorValidationErrors({});
    setBindingEditorSubmitting(false);
    setSelectedRunAuditTarget(null);
    setRetiringBinding(null);
  }, [scopeId, selectedService?.serviceId]);

  useEffect(() => {
    runtimeActionStatesRef.current.clear();
    setRuntimeActionStates(new Map());
  }, [scopeId]);

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!revisions.length) {
      setSelectedRevisionId('');
      return;
    }

    if (
      selectedRevisionId &&
      revisions.some((revision) => revision.revisionId === selectedRevisionId)
    ) {
      return;
    }

    setSelectedRevisionId(
      getScopeServiceCurrentRevision(revisionsQuery.data)?.revisionId ||
        revisions[0]?.revisionId ||
        '',
    );
  }, [revisionsQuery.data, selectedRevisionId]);

  const selectedBindingTargetService =
    services.find(
      (service) =>
        service.serviceId === bindingEditorDraft.targetServiceId.trim(),
    ) ?? null;

  const bindingTargetEndpointOptions = (
    selectedBindingTargetService?.endpoints ?? []
  ).map((endpoint) => ({
    label: getUserFacingIdentifierLabel(
      endpoint.displayName || endpoint.endpointId,
      t('pages.scopes.scopeserviceruntimeworkbench.endpoint', 'Endpoint'),
    ),
    value: endpoint.endpointId,
  }));

  const bindingList =
    bindingsQuery.data?.kind === 'available'
      ? bindingsQuery.data.snapshot.bindings
      : [];
  const revisionList = revisionsQuery.data?.revisions ?? [];
  const currentRevision =
    selectedRevisionQuery.data ||
    revisionList.find(
      (revision) => revision.revisionId === selectedRevisionId,
    ) ||
    getScopeServiceCurrentRevision(revisionsQuery.data);
  const recentRuns = runsQuery.data?.runs ?? [];
  const auditTimeline = selectedRunAuditQuery.data?.audit.timeline ?? [];
  const auditSteps = selectedRunAuditQuery.data?.audit.steps ?? [];
  const auditSummary = selectedRunAuditQuery.data?.audit.summary;
  const visibleRuntimeAction = runtimeActionStates.get(
    runtimeActionIdentityKey({
      scopeId,
      serviceId: selectedServiceRuntimeId,
    }),
  );
  const isRetiringBinding = (bindingId: string) =>
    retiringBinding?.scopeId === scopeId &&
    retiringBinding.serviceId === selectedServiceRuntimeId &&
    retiringBinding.bindingId === bindingId;
  const clearRetiringBinding = (action: RuntimeActionState) => {
    if (action.target.kind !== 'binding-retired') {
      return;
    }
    const bindingId = action.target.bindingId;

    setRetiringBinding((current) =>
      current?.scopeId === action.scopeId &&
      current.serviceId === action.serviceId &&
      current.bindingId === bindingId
        ? null
        : current,
    );
  };

  const bindingCards = bindingList.length ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {bindingList.map((binding) => (
        <div
          key={binding.bindingId}
          style={{
            border: '1px solid var(--ant-color-border-secondary)',
            borderRadius: 12,
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
            padding: 12,
          }}
        >
          <Space wrap size={[8, 8]}>
            <Typography.Text strong>
              {getUserFacingIdentifierLabel(
                binding.displayName || binding.bindingId,
                t(
                  'pages.scopes.scopeserviceruntimeworkbench.binding',
                  'Binding',
                ),
              )}
            </Typography.Text>
            <AevatarStatusTag
              domain="governance"
              status={binding.retired ? 'retired' : 'active'}
              label={getBindingKindLabel(binding.bindingKind)}
            />
          </Space>
          <Typography.Text type="secondary">
            {t('pages.scopes.scopeserviceruntimeworkbench.target', 'Target')}
            {binding.serviceRef
              ? t(
                  'pages.scopes.scopeserviceruntimeworkbench.service.target',
                  'Service target',
                )
              : describeScopeServiceBindingTarget(binding)}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t(
              'pages.scopes.scopeserviceruntimeworkbench.policies',
              'Policies',
            )}{' '}
            {binding.policyIds.length > 0
              ? t(
                  'pages.scopes.scopeserviceruntimeworkbench.policy.count',
                  '{value1} policies',
                  {
                    value1: binding.policyIds.length,
                  },
                )
              : 'none'}
          </Typography.Text>
          <Space wrap>
            <Button
              disabled={binding.retired || writesBlockedByRuntimeAction}
              icon={<EyeOutlined />}
              onClick={() => {
                if (hasBlockingRuntimeAction(selectedRuntimeActionIdentity)) {
                  return;
                }

                setBindingEditorDraft(createBindingDraftFromSnapshot(binding));
                setBindingEditorValidationErrors({});
                setBindingEditorState({
                  mode: 'edit',
                  bindingId: binding.bindingId,
                });
              }}
            >
              {t(
                'pages.scopes.scopeserviceruntimeworkbench.edit.binding',
                'Edit binding',
              )}
            </Button>
            <Button
              danger
              disabled={binding.retired || writesBlockedByRuntimeAction}
              loading={isRetiringBinding(binding.bindingId)}
              onClick={async () => {
                if (hasBlockingRuntimeAction(selectedRuntimeActionIdentity)) {
                  return;
                }

                const serviceId = selectedService?.serviceId;
                if (!serviceId) {
                  return;
                }

                const action = createRuntimeAction(
                  'bindings',
                  {
                    bindingId: binding.bindingId,
                    kind: 'binding-retired',
                  },
                  { scopeId, serviceId },
                );
                if (!action) {
                  return;
                }
                setRetiringBinding({
                  bindingId: binding.bindingId,
                  scopeId: action.scopeId,
                  serviceId: action.serviceId,
                });
                try {
                  await scopeRuntimeApi.retireServiceBinding(
                    scopeId,
                    serviceId,
                    binding.bindingId,
                  );
                } catch {
                  updateRuntimeActionState({ ...action, status: 'failed' });
                  clearRetiringBinding(action);
                  return;
                }

                if (!isRuntimeActionCurrent(action)) {
                  clearRetiringBinding(action);
                  return;
                }

                const acceptedAction = {
                  ...action,
                  status: 'accepted' as const,
                };
                updateRuntimeActionState(acceptedAction);
                try {
                  if (await invalidateBindingViews(acceptedAction)) {
                    await refreshBindingAction(acceptedAction, 'observing');
                  }
                } catch {
                  updateRuntimeActionState({ ...action, status: 'failed' });
                } finally {
                  clearRetiringBinding(action);
                }
              }}
            >
              {t('pages.scopes.scopeserviceruntimeworkbench.retire', 'Retire')}
            </Button>
          </Space>
        </div>
      ))}
    </div>
  ) : (
    <Empty
      description={t(
        'pages.scopes.scopeserviceruntimeworkbench.no.workspace.bindings.are.published',
        'No workspace bindings are published for this service yet.',
      )}
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const revisionCards = revisionList.length ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {revisionList.map((revision) => {
        const isSelected = revision.revisionId === selectedRevisionId;
        return (
          <div
            key={revision.revisionId}
            style={{
              border: isSelected
                ? '1px solid var(--ant-color-primary)'
                : '1px solid var(--ant-color-border-secondary)',
              borderRadius: 12,
              display: 'flex',
              flexDirection: 'column',
              gap: 10,
              padding: 12,
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
                status={revision.status || 'draft'}
                label={formatStudioMemberBindingImplementationKind(
                  revision.implementationKind,
                )}
              />
              {revision.isDefaultServing ? (
                <AevatarStatusTag
                  domain="governance"
                  status="active"
                  label={t(
                    'pages.scopes.scopeserviceruntimeworkbench.default',
                    'default',
                  )}
                />
              ) : null}
              {revision.isActiveServing ? (
                <AevatarStatusTag
                  domain="run"
                  status="running"
                  label={t(
                    'pages.scopes.scopeserviceruntimeworkbench.active',
                    'active',
                  )}
                />
              ) : null}
              {revision.retiredAt ? (
                <AevatarStatusTag domain="governance" status="retired" />
              ) : null}
            </Space>
            <Typography.Text type="secondary">
              {describeStudioMemberBindingRevisionTarget(revision)} ·{' '}
              {describeStudioMemberBindingRevisionContext(revision) ||
                t(
                  'pages.scopes.scopeserviceruntimeworkbench.no.detail',
                  'No detail',
                )}
            </Typography.Text>
            <Typography.Text type="secondary">
              {t(
                'pages.scopes.scopeserviceruntimeworkbench.serving',
                'Serving',
              )}
              {revision.servingState || revision.status}{' '}
              {t(
                'pages.scopes.scopeserviceruntimeworkbench.published',
                '· Published',
              )}{' '}
              {formatDateTime(revision.publishedAt)}
            </Typography.Text>
            <Space wrap>
              <Button
                icon={<EyeOutlined />}
                onClick={() => setSelectedRevisionId(revision.revisionId)}
                type={isSelected ? 'primary' : 'default'}
              >
                {isSelected
                  ? t(
                      'pages.scopes.scopeserviceruntimeworkbench.inspecting',
                      'Inspecting',
                    )
                  : t(
                      'pages.scopes.scopeserviceruntimeworkbench.inspect',
                      'Inspect',
                    )}
              </Button>
              <Button
                danger
                disabled={
                  Boolean(revision.retiredAt) ||
                  revision.isDefaultServing ||
                  writesBlockedByRuntimeAction
                }
                loading={
                  retireRevisionMutation.isPending &&
                  retireRevisionMutation.variables?.revisionId ===
                    revision.revisionId
                }
                onClick={() => {
                  if (
                    !selectedServiceRuntimeId ||
                    hasBlockingRuntimeAction(selectedRuntimeActionIdentity)
                  ) {
                    return;
                  }

                  retireRevisionMutation.mutate({
                    revisionId: revision.revisionId,
                    scopeId,
                    serviceId: selectedServiceRuntimeId,
                  });
                }}
              >
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.retire.revision',
                  'Retire revision',
                )}
              </Button>
            </Space>
          </div>
        );
      })}
    </div>
  ) : (
    <Empty
      description={t(
        'pages.scopes.scopeserviceruntimeworkbench.no.published.revisions.are.available',
        'No published revisions are available for this service.',
      )}
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const runCards = recentRuns.length ? (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      {recentRuns.map((run) => (
        <RunSummaryCard
          key={`${run.runId}:${run.actorId}`}
          run={run}
          selected={
            selectedRunAuditTarget?.runId === run.runId &&
            selectedRunAuditTarget?.actorId === run.actorId
          }
          onInspectAudit={() =>
            setSelectedRunAuditTarget({
              runId: run.runId,
              actorId: run.actorId,
            })
          }
          onOpenExplorer={() =>
            history.push(
              buildRuntimeExplorerHref({
                actorId: run.actorId,
                runId: run.runId,
                scopeId,
                serviceId: selectedService?.serviceId,
              }),
            )
          }
          onOpenRuns={() =>
            history.push(
              buildRuntimeRunsHref({
                actorId: run.actorId,
                scopeId,
                serviceId: selectedService?.serviceId,
              }),
            )
          }
        />
      ))}
    </div>
  ) : (
    <Empty
      description={t(
        'pages.scopes.scopeserviceruntimeworkbench.no.recent.scope.runs.were',
        'No recent scope runs were found for this service.',
      )}
      image={Empty.PRESENTED_IMAGE_SIMPLE}
    />
  );

  const tabItems = [
    {
      key: 'overview',
      label: 'Overview',
      children: selectedService ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <AevatarPanel
            title={t(
              'pages.scopes.scopeserviceruntimeworkbench.runtime.posture',
              'Runtime Posture',
            )}
            titleHelp={t(
              'pages.scopes.scopeserviceruntimeworkbench.this.service.level.posture.is',
              'This service-level posture is the fastest way to confirm what the selected project service is actually serving right now.',
            )}
          >
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
              }}
            >
              <RuntimeMetricCard
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.service.key',
                  'Service key',
                )}
                value={selectedService.serviceKey}
              />
              <RuntimeMetricCard
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.serving.revision',
                  'Serving revision',
                )}
                value={
                  selectedService.activeServingRevisionId ||
                  selectedService.defaultServingRevisionId ||
                  'n/a'
                }
              />
              <RuntimeMetricCard
                label="Deployment"
                value={selectedService.deploymentStatus || 'draft'}
              />
              <RuntimeMetricCard
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.primary.actor',
                  'Primary actor',
                )}
                value={selectedService.primaryActorId || 'n/a'}
              />
            </div>
            <Space wrap>
              <Button
                icon={<ApiOutlined />}
                onClick={() =>
                  history.push(
                    buildScopedServiceCatalogHref(
                      scopeId,
                      selectedService.serviceId,
                    ),
                  )
                }
              >
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.open.services',
                  'Open Services',
                )}
              </Button>
              <Button
                icon={<DeploymentUnitOutlined />}
                onClick={() => setActiveTab('runs')}
                type="primary"
              >
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.review.runs',
                  'Review runs',
                )}
              </Button>
              <Button
                icon={<SafetyCertificateOutlined />}
                onClick={() => setActiveTab('bindings')}
              >
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.review.bindings',
                  'Review bindings',
                )}
              </Button>
            </Space>
          </AevatarPanel>

          <AevatarPanel
            title={t(
              'pages.scopes.scopeserviceruntimeworkbench.endpoint.surface',
              'Endpoint Surface',
            )}
            titleHelp={t(
              'pages.scopes.scopeserviceruntimeworkbench.operators.can.switch.endpoints.from',
              'Operators can switch endpoints from here without losing the current workspace and service context.',
            )}
          >
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {selectedService.endpoints.length > 0 ? (
                selectedService.endpoints.map((endpoint) => (
                  <div
                    key={endpoint.endpointId}
                    style={{
                      border: '1px solid var(--ant-color-border-secondary)',
                      borderRadius: 12,
                      display: 'flex',
                      flexDirection: 'column',
                      gap: 8,
                      padding: 12,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>
                        {getUserFacingIdentifierLabel(
                          endpoint.displayName || endpoint.endpointId,
                          t(
                            'pages.scopes.scopeserviceruntimeworkbench.endpoint',
                            'Endpoint',
                          ),
                        )}
                      </Typography.Text>
                      <AevatarStatusTag
                        domain="observation"
                        label={endpoint.kind || 'endpoint'}
                        status={
                          endpoint.endpointId === resolvedSelectedEndpointId
                            ? 'streaming'
                            : 'snapshot_available'
                        }
                      />
                    </Space>
                    <Typography.Text type="secondary">
                      {endpoint.description ||
                        t(
                          'pages.scopes.scopeserviceruntimeworkbench.no.endpoint.description',
                          'No endpoint description.',
                        )}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {t(
                        'pages.scopes.scopeserviceruntimeworkbench.request',
                        'Request',
                      )}
                      {endpoint.requestTypeUrl || 'n/a'}
                    </Typography.Text>
                    <Space wrap>
                      <Button
                        onClick={() =>
                          handleUseEndpoint(
                            selectedService.serviceId,
                            endpoint.endpointId,
                          )
                        }
                        type={
                          endpoint.endpointId === resolvedSelectedEndpointId
                            ? 'primary'
                            : 'default'
                        }
                      >
                        {endpoint.endpointId === resolvedSelectedEndpointId
                          ? t(
                              'pages.scopes.scopeserviceruntimeworkbench.selected',
                              'Selected',
                            )
                          : t(
                              'pages.scopes.scopeserviceruntimeworkbench.use.endpoint',
                              'Use endpoint',
                            )}
                      </Button>
                    </Space>
                  </div>
                ))
              ) : (
                <Empty
                  description={t(
                    'pages.scopes.scopeserviceruntimeworkbench.no.endpoint.catalog.is.available',
                    'No endpoint catalog is available for this service.',
                  )}
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </div>
          </AevatarPanel>
        </div>
      ) : (
        <AevatarInspectorEmpty
          description={t(
            'pages.scopes.scopeserviceruntimeworkbench.choose.published.service.to.inspect',
            'Choose a published service to inspect runtime posture, bindings, revisions, and recent runs.',
          )}
        />
      ),
    },
    {
      key: 'bindings',
      label: t(
        'pages.scopes.scopeserviceruntimeworkbench.bindings.count',
        'Bindings ({count})',
        {
          count: bindingList.length,
        },
      ),
      children: selectedService ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <AevatarPanel
            extra={
              <Button
                disabled={writesBlockedByRuntimeAction}
                icon={<PlusOutlined />}
                onClick={() => {
                  if (hasBlockingRuntimeAction(selectedRuntimeActionIdentity)) {
                    return;
                  }

                  setBindingEditorDraft(createEmptyBindingDraft());
                  setBindingEditorValidationErrors({});
                  setBindingEditorState({ mode: 'create' });
                }}
                type="primary"
              >
                {t(
                  'pages.scopes.scopeserviceruntimeworkbench.add.binding',
                  'Add binding',
                )}
              </Button>
            }
            title={t(
              'pages.scopes.scopeserviceruntimeworkbench.dependency.surface',
              'Dependency Surface',
            )}
            titleHelp={t(
              'pages.scopes.scopeserviceruntimeworkbench.workspace.bindings.describe.which.services',
              'Workspace bindings describe which services, connectors, or secrets this published service is allowed to depend on inside the project.',
            )}
          >
            {bindingsQuery.error ? (
              <Alert
                showIcon
                title={
                  bindingsQuery.error instanceof Error
                    ? bindingsQuery.error.message
                    : 'Failed to load default route revisions.'
                }
                type="error"
              />
            ) : bindingsQuery.isLoading ? (
              <AevatarInspectorEmpty
                description={t(
                  'pages.scopes.scopeserviceruntimeworkbench.loading.default.route.revisions',
                  'Loading default route revisions.',
                )}
              />
            ) : bindingsQuery.data?.kind === 'not_materialized' &&
              visibleRuntimeAction?.location !== 'bindings' ? (
              <Alert
                action={
                  <Button
                    loading={bindingsQuery.isFetching}
                    onClick={() => void bindingsQuery.refetch()}
                    size="small"
                  >
                    {t(
                      'pages.scopes.scopeserviceruntimeworkbench.action.refresh',
                      'Refresh',
                    )}
                  </Button>
                }
                description={t(
                  'pages.scopes.scopeserviceruntimeworkbench.bindings.preparing.description',
                  'Refresh to see when bindings are ready.',
                )}
                showIcon
                title={t(
                  'pages.scopes.scopeserviceruntimeworkbench.bindings.preparing.title',
                  'Bindings are still being prepared',
                )}
                type="info"
              />
            ) : (
              bindingCards
            )}
          </AevatarPanel>
        </div>
      ) : (
        <AevatarInspectorEmpty
          description={t(
            'pages.scopes.scopeserviceruntimeworkbench.choose.service.first',
            'Choose a service first.',
          )}
        />
      ),
    },
    {
      key: 'revisions',
      label: t(
        'pages.scopes.scopeserviceruntimeworkbench.revisions.count',
        'Revisions ({count})',
        {
          count: revisionList.length,
        },
      ),
      children: selectedService ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <AevatarPanel
            title={t(
              'pages.scopes.scopeserviceruntimeworkbench.revision.catalog',
              'Revision Catalog',
            )}
            titleHelp={t(
              'pages.scopes.scopeserviceruntimeworkbench.revisions.tell.you.which.artifact',
              'Revisions tell you which artifact is serving now and which historical versions are still available or ready to retire.',
            )}
          >
            {revisionsQuery.error ? (
              <Alert
                showIcon
                title={
                  revisionsQuery.error instanceof Error
                    ? revisionsQuery.error.message
                    : 'Failed to load revisions.'
                }
                type="error"
              />
            ) : revisionsQuery.isLoading ? (
              <AevatarInspectorEmpty
                description={t(
                  'pages.scopes.scopeserviceruntimeworkbench.loading.service.revisions',
                  'Loading service revisions.',
                )}
              />
            ) : (
              revisionCards
            )}
          </AevatarPanel>

          {currentRevision ? (
            <AevatarPanel
              title={t(
                'pages.scopes.scopeserviceruntimeworkbench.selected.revision',
                'Selected Revision',
              )}
              titleHelp={t(
                'pages.scopes.scopeserviceruntimeworkbench.the.selected.revision.stays.expanded',
                'The selected revision stays expanded here so operators can compare implementation target, serving posture, and actor assignment without leaving the tab.',
              )}
            >
              <div
                style={{
                  display: 'grid',
                  gap: 12,
                  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
                }}
              >
                <RuntimeMetricCard
                  label={t(
                    'pages.scopes.scopeserviceruntimeworkbench.version',
                    'Version',
                  )}
                  value={
                    currentRevision.status ||
                    t(
                      'pages.scopes.scopeserviceruntimeworkbench.ready',
                      'Ready',
                    )
                  }
                />
                <RuntimeMetricCard
                  label="Implementation"
                  value={formatStudioMemberBindingImplementationKind(
                    currentRevision.implementationKind,
                  )}
                />
                <RuntimeMetricCard
                  label="Target"
                  value={describeStudioMemberBindingRevisionTarget(
                    currentRevision,
                  )}
                />
                <RuntimeMetricCard
                  label="Actor"
                  value={
                    currentRevision.primaryActorId
                      ? t(
                          'pages.scopes.scopeserviceruntimeworkbench.actor.available',
                          'Actor available',
                        )
                      : 'n/a'
                  }
                />
              </div>
              {describeStudioMemberBindingRevisionContext(currentRevision) ? (
                <Alert
                  description={describeStudioMemberBindingRevisionContext(
                    currentRevision,
                  )}
                  showIcon
                  title={t(
                    'pages.scopes.scopeserviceruntimeworkbench.revision.detail',
                    'Revision detail',
                  )}
                  type="info"
                />
              ) : null}
            </AevatarPanel>
          ) : null}
        </div>
      ) : (
        <AevatarInspectorEmpty
          description={t(
            'pages.scopes.scopeserviceruntimeworkbench.choose.service.first.2',
            'Choose a service first.',
          )}
        />
      ),
    },
    {
      key: 'runs',
      label: t(
        'pages.scopes.scopeserviceruntimeworkbench.runs.count',
        'Runs ({count})',
        {
          count: recentRuns.length,
        },
      ),
      children: selectedService ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          <AevatarPanel
            title={t(
              'pages.scopes.scopeserviceruntimeworkbench.recent.runs',
              'Recent Runs',
            )}
            titleHelp={t(
              'pages.scopes.scopeserviceruntimeworkbench.recent.runs.are.the.shortest',
              'Recent runs are the shortest path from a published service to a traceable execution posture.',
            )}
          >
            {runsQuery.error ? (
              <Alert
                showIcon
                title={
                  runsQuery.error instanceof Error
                    ? runsQuery.error.message
                    : 'Failed to load runs.'
                }
                type="error"
              />
            ) : runsQuery.isLoading ? (
              <AevatarInspectorEmpty
                description={t(
                  'pages.scopes.scopeserviceruntimeworkbench.loading.recent.runs',
                  'Loading recent runs.',
                )}
              />
            ) : (
              runCards
            )}
          </AevatarPanel>

          {selectedRunAuditTarget ? (
            <AevatarPanel
              title={t(
                'pages.scopes.scopeserviceruntimeworkbench.run.audit',
                'Run Audit',
              )}
              titleHelp={t(
                'pages.scopes.scopeserviceruntimeworkbench.audit.detail.keeps.the.latest',
                'Audit detail keeps the latest selected run in view so operators can understand failure posture or completion depth before opening full Runs.',
              )}
            >
              {selectedRunAuditQuery.error ? (
                <Alert
                  showIcon
                  title={
                    selectedRunAuditQuery.error instanceof Error
                      ? selectedRunAuditQuery.error.message
                      : 'Failed to load run audit.'
                  }
                  type="error"
                />
              ) : selectedRunAuditQuery.isLoading ? (
                <AevatarInspectorEmpty
                  description={t(
                    'pages.scopes.scopeserviceruntimeworkbench.loading.run.audit',
                    'Loading run audit.',
                  )}
                />
              ) : selectedRunAuditQuery.data ? (
                <div
                  style={{ display: 'flex', flexDirection: 'column', gap: 16 }}
                >
                  <div
                    style={{
                      display: 'grid',
                      gap: 12,
                      gridTemplateColumns:
                        'repeat(auto-fit, minmax(180px, 1fr))',
                    }}
                  >
                    <RuntimeMetricCard
                      label="Completion"
                      value={selectedRunAuditQuery.data.audit.completionStatus}
                    />
                    <RuntimeMetricCard
                      label="Duration"
                      value={`${Math.round(selectedRunAuditQuery.data.audit.durationMs)} ms`}
                    />
                    <RuntimeMetricCard
                      label="Steps"
                      value={`${auditSummary?.completedSteps ?? 0}/${auditSummary?.totalSteps ?? 0}`}
                    />
                    <RuntimeMetricCard
                      label={t(
                        'pages.scopes.scopeserviceruntimeworkbench.role.replies',
                        'Role replies',
                      )}
                      value={auditSummary?.roleReplyCount ?? 0}
                    />
                  </div>
                  {selectedRunAuditQuery.data.audit.finalOutput ? (
                    <Alert
                      description={selectedRunAuditQuery.data.audit.finalOutput}
                      showIcon
                      title={t(
                        'pages.scopes.scopeserviceruntimeworkbench.final.output',
                        'Final output',
                      )}
                      type="success"
                    />
                  ) : null}
                  {selectedRunAuditQuery.data.audit.finalError ? (
                    <Alert
                      description={selectedRunAuditQuery.data.audit.finalError}
                      showIcon
                      title={t(
                        'pages.scopes.scopeserviceruntimeworkbench.final.error',
                        'Final error',
                      )}
                      type="error"
                    />
                  ) : null}
                  <div
                    style={{
                      display: 'grid',
                      gap: 16,
                      gridTemplateColumns:
                        'repeat(auto-fit, minmax(260px, 1fr))',
                    }}
                  >
                    <AevatarPanel
                      title={t(
                        'pages.scopes.scopeserviceruntimeworkbench.timeline.highlights',
                        'Timeline Highlights',
                      )}
                    >
                      {auditTimeline.length > 0 ? (
                        <div
                          style={{
                            display: 'flex',
                            flexDirection: 'column',
                            gap: 10,
                          }}
                        >
                          {auditTimeline.slice(0, 8).map((event) => (
                            <div
                              key={[
                                event.timestamp || 'event',
                                event.eventType,
                                event.stage,
                                event.stepId,
                                event.agentId,
                                event.message,
                              ].join(':')}
                              style={{
                                border:
                                  '1px solid var(--ant-color-border-secondary)',
                                borderRadius: 12,
                                display: 'flex',
                                flexDirection: 'column',
                                gap: 6,
                                padding: 12,
                              }}
                            >
                              <Typography.Text strong>
                                {event.stage || event.eventType || 'event'}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {event.message ||
                                  t(
                                    'pages.scopes.scopeserviceruntimeworkbench.no.message',
                                    'No message',
                                  )}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {formatDateTime(event.timestamp)}
                              </Typography.Text>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <Empty
                          description={t(
                            'pages.scopes.scopeserviceruntimeworkbench.no.timeline.events.were.captured',
                            'No timeline events were captured.',
                          )}
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </AevatarPanel>
                    <AevatarPanel
                      title={t(
                        'pages.scopes.scopeserviceruntimeworkbench.step.highlights',
                        'Step Highlights',
                      )}
                    >
                      {auditSteps.length > 0 ? (
                        <div
                          style={{
                            display: 'flex',
                            flexDirection: 'column',
                            gap: 10,
                          }}
                        >
                          {auditSteps.slice(0, 6).map((step) => (
                            <div
                              key={step.stepId}
                              style={{
                                border:
                                  '1px solid var(--ant-color-border-secondary)',
                                borderRadius: 12,
                                display: 'flex',
                                flexDirection: 'column',
                                gap: 6,
                                padding: 12,
                              }}
                            >
                              <Typography.Text strong>
                                {step.stepId}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {step.stepType || 'step'} ·{' '}
                                {step.targetRole || 'unassigned'}
                              </Typography.Text>
                              <Typography.Text type="secondary">
                                {step.outputPreview ||
                                  step.error ||
                                  t(
                                    'pages.scopes.scopeserviceruntimeworkbench.no.step.preview',
                                    'No step preview.',
                                  )}
                              </Typography.Text>
                            </div>
                          ))}
                        </div>
                      ) : (
                        <Empty
                          description={t(
                            'pages.scopes.scopeserviceruntimeworkbench.no.step.traces.were.captured',
                            'No step traces were captured.',
                          )}
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                        />
                      )}
                    </AevatarPanel>
                  </div>
                </div>
              ) : null}
            </AevatarPanel>
          ) : null}
        </div>
      ) : (
        <AevatarInspectorEmpty
          description={t(
            'pages.scopes.scopeserviceruntimeworkbench.choose.service.first.3',
            'Choose a service first.',
          )}
        />
      ),
    },
  ];

  return (
    <>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
        <AevatarPanel
          title={t(
            'pages.scopes.scopeserviceruntimeworkbench.published.services',
            'Published Services',
          )}
          titleHelp={t(
            'pages.scopes.scopeserviceruntimeworkbench.operators.stay.in.the.same',
            'Operators stay in the same drawer while switching between published services in the current project.',
          )}
        >
          {services.length > 0 ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {services.map((service) => (
                <div
                  key={service.serviceKey}
                  style={{
                    border:
                      service.serviceId === resolvedSelectedServiceId
                        ? '1px solid var(--ant-color-primary)'
                        : '1px solid var(--ant-color-border-secondary)',
                    borderRadius: 12,
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 8,
                    padding: 12,
                  }}
                >
                  <Space wrap size={[8, 8]}>
                    <Typography.Text strong>
                      {getUserFacingIdentifierLabel(
                        service.displayName || service.serviceId,
                        t(
                          'pages.scopes.scopeserviceruntimeworkbench.service',
                          'Service',
                        ),
                      )}
                    </Typography.Text>
                    <AevatarStatusTag
                      domain="governance"
                      status={service.deploymentStatus || 'draft'}
                    />
                  </Space>
                  <Typography.Text type="secondary">
                    {service.endpoints.length}{' '}
                    {t(
                      'pages.scopes.scopeserviceruntimeworkbench.endpoints',
                      'endpoints',
                    )}{' '}
                    ·{' '}
                    {service.activeServingRevisionId ||
                    service.defaultServingRevisionId
                      ? t(
                          'pages.scopes.scopeserviceruntimeworkbench.serving.version.ready',
                          'serving version ready',
                        )
                      : 'n/a'}
                  </Typography.Text>
                  <Space wrap>
                    <Button
                      onClick={() => handleSelectService(service.serviceId)}
                      type={
                        service.serviceId === resolvedSelectedServiceId
                          ? 'primary'
                          : 'default'
                      }
                    >
                      {service.serviceId === resolvedSelectedServiceId
                        ? t(
                            'pages.scopes.scopeserviceruntimeworkbench.selected',
                            'Selected',
                          )
                        : t(
                            'pages.scopes.scopeserviceruntimeworkbench.inspect.service',
                            'Inspect service',
                          )}
                    </Button>
                    <Button
                      icon={<LinkOutlined />}
                      onClick={() =>
                        history.push(
                          buildScopedServiceCatalogHref(
                            scopeId,
                            service.serviceId,
                          ),
                        )
                      }
                    >
                      {t(
                        'pages.scopes.scopeserviceruntimeworkbench.open.services.2',
                        'Open Services',
                      )}
                    </Button>
                  </Space>
                </div>
              ))}
            </div>
          ) : (
            <Empty
              description={t(
                'pages.scopes.scopeserviceruntimeworkbench.no.published.services.were.discovered',
                'No published services were discovered for this project.',
              )}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </AevatarPanel>

        {visibleRuntimeAction &&
        (visibleRuntimeAction.location !== 'binding-editor' ||
          !bindingEditorState) ? (
          <RuntimeActionFeedback
            action={visibleRuntimeAction}
            onDismiss={() => dismissRuntimeAction(visibleRuntimeAction)}
            onRefresh={() => handleRefreshRuntimeAction(visibleRuntimeAction)}
          />
        ) : null}

        {selectedService ? (
          <Tabs
            activeKey={activeTab}
            items={tabItems}
            onChange={(value) => setActiveTab(value as ServiceRuntimeTab)}
          />
        ) : null}
      </div>

      <Modal
        cancelButtonProps={{ disabled: bindingEditorSubmitting }}
        closable={!bindingEditorSubmitting}
        destroyOnHidden
        keyboard={!bindingEditorSubmitting}
        mask={{ closable: !bindingEditorSubmitting }}
        okButtonProps={{
          disabled: bindingEditorSubmitting || writesBlockedByRuntimeAction,
          loading: bindingEditorSubmitting,
        }}
        okText={
          bindingEditorState?.mode === 'edit'
            ? t(
                'pages.scopes.scopeserviceruntimeworkbench.save.binding',
                'Save binding',
              )
            : t(
                'pages.scopes.scopeserviceruntimeworkbench.create.binding',
                'Create binding',
              )
        }
        onCancel={() => {
          if (bindingEditorSubmitting) {
            return;
          }

          setBindingEditorState(null);
          setBindingEditorDraft(createEmptyBindingDraft());
          setBindingEditorValidationErrors({});
          if (visibleRuntimeAction?.location === 'binding-editor') {
            updateRuntimeActionState({
              ...visibleRuntimeAction,
              location: 'bindings',
            });
          }
        }}
        onOk={async () => {
          if (
            !selectedService ||
            bindingEditorSubmitting ||
            hasBlockingRuntimeAction(selectedRuntimeActionIdentity)
          ) {
            return;
          }

          const payload = buildBindingPayload(bindingEditorDraft);
          const validationErrors = getBindingEditorValidationErrors(payload);
          setBindingEditorValidationErrors(validationErrors);
          if (Object.keys(validationErrors).length > 0) {
            return;
          }

          const action = createRuntimeAction(
            'bindings',
            {
              bindingId: payload.bindingId,
              kind: 'binding-payload',
              payload,
            },
            { scopeId, serviceId: selectedService.serviceId },
          );
          if (!action) {
            return;
          }
          setBindingEditorSubmitting(true);
          try {
            if (
              bindingEditorState?.mode === 'edit' &&
              bindingEditorState.bindingId
            ) {
              await scopeRuntimeApi.updateServiceBinding(
                scopeId,
                selectedService.serviceId,
                bindingEditorState.bindingId,
                payload,
              );
            } else {
              await scopeRuntimeApi.createServiceBinding(
                scopeId,
                selectedService.serviceId,
                payload,
              );
            }
          } catch {
            updateRuntimeActionState({
              ...action,
              location: 'binding-editor',
              status: 'failed',
            });
            setBindingEditorSubmitting(false);
            return;
          }

          if (!isRuntimeActionCurrent(action)) {
            setBindingEditorSubmitting(false);
            return;
          }

          const acceptedAction = { ...action, status: 'accepted' as const };
          updateRuntimeActionState(acceptedAction);
          setBindingEditorState(null);
          setBindingEditorDraft(createEmptyBindingDraft());
          setBindingEditorValidationErrors({});
          try {
            if (await invalidateBindingViews(acceptedAction)) {
              await refreshBindingAction(acceptedAction, 'observing');
            }
          } catch {
            updateRuntimeActionState({ ...action, status: 'failed' });
          } finally {
            setBindingEditorSubmitting(false);
          }
        }}
        open={Boolean(bindingEditorState)}
        title={
          bindingEditorState?.mode === 'edit'
            ? t(
                'pages.scopes.scopeserviceruntimeworkbench.edit.binding',
                'Edit binding',
              )
            : t(
                'pages.scopes.scopeserviceruntimeworkbench.create.binding',
                'Create binding',
              )
        }
      >
        <Form layout="vertical" requiredMark={false}>
          {visibleRuntimeAction?.location === 'binding-editor' ? (
            <RuntimeActionFeedback
              action={visibleRuntimeAction}
              onDismiss={() => dismissRuntimeAction(visibleRuntimeAction)}
              onRefresh={() => handleRefreshRuntimeAction(visibleRuntimeAction)}
            />
          ) : null}
          <Form.Item
            help={
              bindingEditorValidationErrors.bindingId ? (
                <span id="scope-runtime-binding-id-error" role="alert">
                  {bindingEditorValidationErrors.bindingId}
                </span>
              ) : null
            }
            htmlFor="scope-runtime-binding-id"
            label={t(
              'pages.scopes.scopeserviceruntimeworkbench.binding.id.label',
              'Binding ID',
            )}
            validateStatus={
              bindingEditorValidationErrors.bindingId ? 'error' : undefined
            }
          >
            <Input
              aria-describedby={
                bindingEditorValidationErrors.bindingId
                  ? 'scope-runtime-binding-id-error'
                  : undefined
              }
              aria-invalid={Boolean(bindingEditorValidationErrors.bindingId)}
              disabled={bindingEditorState?.mode === 'edit'}
              id="scope-runtime-binding-id"
              onChange={(event) => {
                setBindingEditorDraft((current) => ({
                  ...current,
                  bindingId: event.target.value,
                }));
                setBindingEditorValidationErrors((current) => ({
                  ...current,
                  bindingId: undefined,
                }));
              }}
              placeholder={t(
                'pages.scopes.scopeserviceruntimeworkbench.binding.id',
                'binding id',
              )}
              value={bindingEditorDraft.bindingId}
            />
          </Form.Item>
          <Form.Item
            htmlFor="scope-runtime-binding-display-name"
            label={t(
              'pages.scopes.scopeserviceruntimeworkbench.display.name.label',
              'Display name',
            )}
          >
            <Input
              id="scope-runtime-binding-display-name"
              onChange={(event) =>
                setBindingEditorDraft((current) => ({
                  ...current,
                  displayName: event.target.value,
                }))
              }
              placeholder={t(
                'pages.scopes.scopeserviceruntimeworkbench.display.name',
                'display name',
              )}
              value={bindingEditorDraft.displayName}
            />
          </Form.Item>
          <Form.Item
            htmlFor="scope-runtime-binding-kind"
            label={t(
              'pages.scopes.scopeserviceruntimeworkbench.binding.kind.label',
              'Binding type',
            )}
          >
            <Select
              id="scope-runtime-binding-kind"
              onChange={(value) => {
                setBindingEditorDraft((current) => ({
                  ...createEmptyBindingDraft(),
                  bindingId: current.bindingId,
                  displayName: current.displayName,
                  policyIdsText: current.policyIdsText,
                  bindingKind: value,
                }));
                setBindingEditorValidationErrors({});
              }}
              options={[
                {
                  label: t(
                    'pages.scopes.scopeserviceruntimeworkbench.binding.kind.service',
                    'Service',
                  ),
                  value: 'service',
                },
                {
                  label: t(
                    'pages.scopes.scopeserviceruntimeworkbench.binding.kind.connector',
                    'Connector',
                  ),
                  value: 'connector',
                },
                {
                  label: t(
                    'pages.scopes.scopeserviceruntimeworkbench.binding.kind.secret',
                    'Secret',
                  ),
                  value: 'secret',
                },
              ]}
              value={bindingEditorDraft.bindingKind}
            />
          </Form.Item>
          {bindingEditorDraft.bindingKind === 'service' ? (
            <>
              <Form.Item
                help={
                  bindingEditorValidationErrors.targetServiceId ? (
                    <span id="scope-runtime-target-service-error" role="alert">
                      {bindingEditorValidationErrors.targetServiceId}
                    </span>
                  ) : null
                }
                htmlFor="scope-runtime-target-service"
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.target.service.label',
                  'Target service',
                )}
                validateStatus={
                  bindingEditorValidationErrors.targetServiceId
                    ? 'error'
                    : undefined
                }
              >
                <Select
                  aria-describedby={
                    bindingEditorValidationErrors.targetServiceId
                      ? 'scope-runtime-target-service-error'
                      : undefined
                  }
                  aria-invalid={Boolean(
                    bindingEditorValidationErrors.targetServiceId,
                  )}
                  id="scope-runtime-target-service"
                  onChange={(value) => {
                    setBindingEditorDraft((current) => ({
                      ...current,
                      targetEndpointId: '',
                      targetServiceId: value,
                    }));
                    setBindingEditorValidationErrors((current) => ({
                      ...current,
                      targetServiceId: undefined,
                    }));
                  }}
                  options={services
                    .filter(
                      (service) =>
                        service.serviceId !== selectedService?.serviceId,
                    )
                    .map((service) => ({
                      label: getUserFacingIdentifierLabel(
                        service.displayName || service.serviceId,
                        t(
                          'pages.scopes.scopeserviceruntimeworkbench.service',
                          'Service',
                        ),
                      ),
                      value: service.serviceId,
                    }))}
                  placeholder={t(
                    'pages.scopes.scopeserviceruntimeworkbench.target.service',
                    'target service',
                  )}
                  value={bindingEditorDraft.targetServiceId || undefined}
                />
              </Form.Item>
              <Form.Item
                htmlFor="scope-runtime-target-endpoint"
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.target.endpoint.optional.label',
                  'Target endpoint (optional)',
                )}
              >
                <Select
                  allowClear
                  id="scope-runtime-target-endpoint"
                  onChange={(value) =>
                    setBindingEditorDraft((current) => ({
                      ...current,
                      targetEndpointId: value || '',
                    }))
                  }
                  options={bindingTargetEndpointOptions}
                  placeholder={t(
                    'pages.scopes.scopeserviceruntimeworkbench.target.endpoint.optional',
                    'target endpoint (optional)',
                  )}
                  value={bindingEditorDraft.targetEndpointId || undefined}
                />
              </Form.Item>
            </>
          ) : null}
          {bindingEditorDraft.bindingKind === 'connector' ? (
            <>
              <Form.Item
                help={
                  bindingEditorValidationErrors.connectorType ? (
                    <span id="scope-runtime-connector-type-error" role="alert">
                      {bindingEditorValidationErrors.connectorType}
                    </span>
                  ) : null
                }
                htmlFor="scope-runtime-connector-type"
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.connector.type.label',
                  'Connector type',
                )}
                validateStatus={
                  bindingEditorValidationErrors.connectorType
                    ? 'error'
                    : undefined
                }
              >
                <Input
                  aria-describedby={
                    bindingEditorValidationErrors.connectorType
                      ? 'scope-runtime-connector-type-error'
                      : undefined
                  }
                  aria-invalid={Boolean(
                    bindingEditorValidationErrors.connectorType,
                  )}
                  id="scope-runtime-connector-type"
                  onChange={(event) => {
                    setBindingEditorDraft((current) => ({
                      ...current,
                      connectorType: event.target.value,
                    }));
                    setBindingEditorValidationErrors((current) => ({
                      ...current,
                      connectorType: undefined,
                    }));
                  }}
                  placeholder={t(
                    'pages.scopes.scopeserviceruntimeworkbench.connector.type',
                    'connector type',
                  )}
                  value={bindingEditorDraft.connectorType}
                />
              </Form.Item>
              <Form.Item
                help={
                  bindingEditorValidationErrors.connectorId ? (
                    <span id="scope-runtime-connector-id-error" role="alert">
                      {bindingEditorValidationErrors.connectorId}
                    </span>
                  ) : null
                }
                htmlFor="scope-runtime-connector-id"
                label={t(
                  'pages.scopes.scopeserviceruntimeworkbench.connector.id.label',
                  'Connector ID',
                )}
                validateStatus={
                  bindingEditorValidationErrors.connectorId
                    ? 'error'
                    : undefined
                }
              >
                <Input
                  aria-describedby={
                    bindingEditorValidationErrors.connectorId
                      ? 'scope-runtime-connector-id-error'
                      : undefined
                  }
                  aria-invalid={Boolean(
                    bindingEditorValidationErrors.connectorId,
                  )}
                  id="scope-runtime-connector-id"
                  onChange={(event) => {
                    setBindingEditorDraft((current) => ({
                      ...current,
                      connectorId: event.target.value,
                    }));
                    setBindingEditorValidationErrors((current) => ({
                      ...current,
                      connectorId: undefined,
                    }));
                  }}
                  placeholder={t(
                    'pages.scopes.scopeserviceruntimeworkbench.connector.id',
                    'connector id',
                  )}
                  value={bindingEditorDraft.connectorId}
                />
              </Form.Item>
            </>
          ) : null}
          {bindingEditorDraft.bindingKind === 'secret' ? (
            <Form.Item
              help={
                bindingEditorValidationErrors.secretName ? (
                  <span id="scope-runtime-secret-name-error" role="alert">
                    {bindingEditorValidationErrors.secretName}
                  </span>
                ) : null
              }
              htmlFor="scope-runtime-secret-name"
              label={t(
                'pages.scopes.scopeserviceruntimeworkbench.secret.name.label',
                'Secret name',
              )}
              validateStatus={
                bindingEditorValidationErrors.secretName ? 'error' : undefined
              }
            >
              <Input
                aria-describedby={
                  bindingEditorValidationErrors.secretName
                    ? 'scope-runtime-secret-name-error'
                    : undefined
                }
                aria-invalid={Boolean(bindingEditorValidationErrors.secretName)}
                id="scope-runtime-secret-name"
                onChange={(event) => {
                  setBindingEditorDraft((current) => ({
                    ...current,
                    secretName: event.target.value,
                  }));
                  setBindingEditorValidationErrors((current) => ({
                    ...current,
                    secretName: undefined,
                  }));
                }}
                placeholder={t(
                  'pages.scopes.scopeserviceruntimeworkbench.secret.name',
                  'secret name',
                )}
                value={bindingEditorDraft.secretName}
              />
            </Form.Item>
          ) : null}
          <Form.Item
            htmlFor="scope-runtime-binding-policy-ids"
            label={t(
              'pages.scopes.scopeserviceruntimeworkbench.policy.ids.label',
              'Policy IDs',
            )}
          >
            <Input.TextArea
              id="scope-runtime-binding-policy-ids"
              onChange={(event) =>
                setBindingEditorDraft((current) => ({
                  ...current,
                  policyIdsText: event.target.value,
                }))
              }
              placeholder={t(
                'pages.scopes.scopeserviceruntimeworkbench.policy.ids.separated.by.commas',
                'policy ids, separated by commas',
              )}
              rows={3}
              value={bindingEditorDraft.policyIdsText}
            />
          </Form.Item>
        </Form>
      </Modal>
    </>
  );
};

const RunSummaryCard: React.FC<{
  run: ScopeServiceRunSummary;
  selected: boolean;
  onInspectAudit: () => void;
  onOpenExplorer: () => void;
  onOpenRuns: () => void;
}> = ({ run, selected, onInspectAudit, onOpenExplorer, onOpenRuns }) => (
  <div
    style={{
      border: selected
        ? '1px solid var(--ant-color-primary)'
        : '1px solid var(--ant-color-border-secondary)',
      borderRadius: 12,
      display: 'flex',
      flexDirection: 'column',
      gap: 10,
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]}>
      <Typography.Text strong>
        {formatDateTime(
          run.lastUpdatedAt || run.boundAt || run.bindingUpdatedAt,
        )}
      </Typography.Text>
      <AevatarStatusTag
        domain="run"
        status={run.completionStatus || 'unknown'}
        label={
          run.completionStatus ||
          t('pages.scopes.scopeserviceruntimeworkbench.unknown', 'unknown')
        }
      />
    </Space>
    <Typography.Text type="secondary">
      {t('pages.scopes.scopeserviceruntimeworkbench.workflow', 'Workflow')}
      {getUserFacingIdentifierLabel(
        run.workflowName,
        t('pages.scopes.scopeserviceruntimeworkbench.not.available', 'n/a'),
      )}
    </Typography.Text>
    <Typography.Text type="secondary">
      {t('pages.scopes.scopeserviceruntimeworkbench.updated', 'Updated')}
      {formatDateTime(run.lastUpdatedAt)}
    </Typography.Text>
    <Typography.Text type="secondary">
      {run.lastError ||
        run.lastOutput ||
        t(
          'pages.scopes.scopeserviceruntimeworkbench.no.output.snapshot.captured',
          'No output snapshot has been captured yet.',
        )}
    </Typography.Text>
    <Space wrap>
      <Button
        icon={<EyeOutlined />}
        onClick={onInspectAudit}
        type={selected ? 'primary' : 'default'}
      >
        {selected
          ? t(
              'pages.scopes.scopeserviceruntimeworkbench.inspecting',
              'Inspecting',
            )
          : t(
              'pages.scopes.scopeserviceruntimeworkbench.load.audit',
              'Load audit',
            )}
      </Button>
      <Button icon={<BranchesOutlined />} onClick={onOpenExplorer}>
        {t('pages.scopes.scopeserviceruntimeworkbench.runtime', 'Runtime')}
      </Button>
      <Button icon={<RetweetOutlined />} onClick={onOpenRuns}>
        {t('pages.scopes.scopeserviceruntimeworkbench.open.runs', 'Open Runs')}
      </Button>
    </Space>
  </div>
);

export default ScopeServiceRuntimeWorkbench;
