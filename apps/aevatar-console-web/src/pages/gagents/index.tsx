import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from '@aevatar-react-sdk/types';
import {
  EyeOutlined,
  PlayCircleOutlined,
  ReloadOutlined,
  RobotOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Checkbox,
  Empty,
  Grid,
  Input,
  Select,
  Space,
  Tag,
  Tabs,
  Typography,
} from 'antd';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { AEVATAR_PRESSABLE_CARD_CLASS } from '@/shared/ui/interactionStandards';
import { parseRunContextData } from '@/shared/agui/customEventData';
import { parseBackendSSEStream } from '@/shared/agui/sseFrameNormalizer';
import { runtimeGAgentApi } from '@/shared/api/runtimeGAgentApi';
import { history } from '@/shared/navigation/history';
import {
  buildRuntimeGAgentsHref,
  buildRuntimeRunsHref,
} from '@/shared/navigation/runtimeRoutes';
import {
  buildRuntimeGAgentKindValue,
  buildRuntimeGAgentKindLabel,
  collectRuntimeGAgentActorIds,
  describeRuntimeGAgentBindingRevisionTarget,
  formatRuntimeGAgentBindingImplementationKind,
  getRuntimeGAgentCurrentBindingRevision,
  matchesRuntimeGAgentKindDescriptor,
  type RuntimeGAgentBindingEndpointInput,
  type RuntimeGAgentBindingRevision,
} from '@/shared/models/runtime/gagents';
import { normalizeRunEndpointKind } from '@/shared/runs/endpointKinds';
import { saveObservedRunSessionPayload } from '@/shared/runs/draftRunSession';
import { saveRecentRun } from '@/shared/runs/recentRuns';
import { studioApi } from '@/shared/studio/api';
import {
  AevatarContextDrawer,
  type AevatarBreadcrumbItem,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from '@/shared/ui/aevatarPageShells';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';
import { describeError } from '@/shared/ui/errorText';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';
import { resolveStudioScopeContext } from '@/pages/scopes/components/resolvedScope';
import { t } from "@/shared/i18n/messages";

type ActorReuseMode = 'new' | 'existing';
const GAGENT_DRAFT_RUN_TIMEOUT_MS = 30_000;
const GAGENT_DRAFT_RUN_CLIENT_TIMEOUT_MS = GAGENT_DRAFT_RUN_TIMEOUT_MS + 5_000;
type NoticeState = {
  message: string;
  type: 'success' | 'error';
};

type GAgentRunState = {
  actorId: string;
  assistantText: string;
  commandId: string;
  error: string;
  events: AGUIEvent[];
  runId: string;
  status: 'idle' | 'running' | 'success' | 'error';
};

type GAgentBindingEndpointDraft = {
  endpointId: string;
  displayName: string;
  kind: 'command' | 'chat';
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
};

type GAgentBindingDraft = {
  displayName: string;
  preferredActorId: string;
  endpoints: GAgentBindingEndpointDraft[];
  openRunsEndpointId: string;
};

const CHAT_ENDPOINT_ID = 'chat';
const DEFAULT_GAGENT_REQUEST_TYPE_URL =
  'type.googleapis.com/google.protobuf.StringValue';
const CLI_BORDER_COLOR = '#E6E3DE';
const CLI_MUTED_BACKGROUND = '#FAFAF9';
const CLI_PAGE_BACKGROUND = '#F2F1EE';
const scrollColumnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  maxHeight: 420,
  minHeight: 0,
  overflowY: 'auto',
  paddingRight: 4,
};
const wrappedTextStyle: React.CSSProperties = {
  marginBottom: 0,
  marginTop: 4,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};
const infoCardStyle: React.CSSProperties = {
  background: CLI_MUTED_BACKGROUND,
  border: `1px solid ${CLI_BORDER_COLOR}`,
  borderRadius: 14,
  minWidth: 0,
  padding: 12,
};
const compactListStyle: React.CSSProperties = {
  ...scrollColumnStyle,
  gap: 12,
};
const workbenchColumnStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
};
const summaryMetricStyle: React.CSSProperties = {
  background: CLI_MUTED_BACKGROUND,
  border: `1px solid ${CLI_BORDER_COLOR}`,
  borderRadius: 14,
  minHeight: 0,
  padding: 12,
};
const cliCardStyle: React.CSSProperties = {
  background: '#ffffff',
  border: `1px solid ${CLI_BORDER_COLOR}`,
  borderRadius: 20,
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
  padding: 20,
};
const cliCardHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
  width: '100%',
};
const cliCardLabelStyle: React.CSSProperties = {
  color: '#9ca3af',
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: '0.14em',
  textTransform: 'uppercase',
};
const cliCardTitleStyle: React.CSSProperties = {
  color: '#1f2937',
  fontSize: 16,
  fontWeight: 700,
  lineHeight: 1.35,
};
const cliCardDescriptionStyle: React.CSSProperties = {
  color: '#6b7280',
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};
const cliFieldLabelStyle: React.CSSProperties = {
  color: '#6b7280',
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
};
const cliRailShellStyle: React.CSSProperties = {
  background: '#ffffff',
  border: `1px solid ${CLI_BORDER_COLOR}`,
  borderRadius: 20,
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
};
const cliRailSectionStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  minWidth: 0,
  padding: 16,
};
const cliSectionDividerStyle: React.CSSProperties = {
  borderTop: `1px solid ${CLI_BORDER_COLOR}`,
};

type WorkbenchCardProps = {
  children: React.ReactNode;
  description?: React.ReactNode;
  extra?: React.ReactNode;
  eyebrow?: React.ReactNode;
  title: React.ReactNode;
};

const WorkbenchCard: React.FC<WorkbenchCardProps> = ({
  children,
  description,
  extra,
  eyebrow,
  title,
}) => (
  <div style={cliCardStyle}>
    <div style={cliCardHeaderStyle}>
      <div style={{ flex: 1, minWidth: 0 }}>
        {eyebrow ? <div style={cliCardLabelStyle}>{eyebrow}</div> : null}
        <div style={{ ...cliCardTitleStyle, marginTop: eyebrow ? 4 : 0 }}>
          {title}
        </div>
        {description ? (
          <Typography.Paragraph style={cliCardDescriptionStyle}>
            {description}
          </Typography.Paragraph>
        ) : null}
      </div>
      {extra ? <div style={{ flexShrink: 0 }}>{extra}</div> : null}
    </div>
    {children}
  </div>
);

function createIdleRunState(): GAgentRunState {
  return {
    actorId: '',
    assistantText: '',
    commandId: '',
    error: '',
    events: [],
    runId: '',
    status: 'idle',
  };
}

function createBindingEndpointDraft(
  overrides?: Partial<GAgentBindingEndpointDraft>,
): GAgentBindingEndpointDraft {
  return {
    endpointId: overrides?.endpointId ?? 'run',
    displayName: overrides?.displayName ?? 'Run',
    kind: overrides?.kind ?? 'command',
    requestTypeUrl:
      overrides?.requestTypeUrl ?? DEFAULT_GAGENT_REQUEST_TYPE_URL,
    responseTypeUrl: overrides?.responseTypeUrl ?? '',
    description: overrides?.description ?? 'Run the published GAgent.',
  };
}

function createBindingDraft(displayName: string): GAgentBindingDraft {
  const defaultEndpoint = createBindingEndpointDraft();
  return {
    displayName,
    preferredActorId: '',
    endpoints: [defaultEndpoint],
    openRunsEndpointId: defaultEndpoint.endpointId,
  };
}

function readQueryValue(name: string): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get(name)?.trim() ?? '';
}

function createDraftRunTimeoutError(): Error {
  return new Error('GAgent draft run timed out before the backend returned any event.');
}

function readEventString(event: AGUIEvent, key: string): string {
  const record = event as unknown as Record<string, unknown>;
  const value = record[key];
  return typeof value === 'string' ? value : '';
}

function createEventKey(event: AGUIEvent, index: number): string {
  return [
    event.type,
    readEventString(event, 'runId'),
    readEventString(event, 'messageId'),
    String(index),
  ].join(':');
}

function buildEventSummary(event: AGUIEvent): string {
  if (event.type === AGUIEventType.TEXT_MESSAGE_CONTENT) {
    return (
      readEventString(event, 'delta') || 'Assistant streamed a text chunk.'
    );
  }

  if (event.type === AGUIEventType.RUN_STARTED) {
    return readEventString(event, 'runId') || 'Run started.';
  }

  if (event.type === AGUIEventType.RUN_ERROR) {
    return readEventString(event, 'message') || 'Run failed.';
  }

  if (event.type === AGUIEventType.CUSTOM) {
    try {
      const custom = parseCustomEvent(event);
      if (custom.name === CustomEventName.RunContext) {
        const context = parseRunContextData(custom.data);
        return context?.actorId || context?.commandId || 'Run context updated.';
      }

      return custom.name || 'Custom event received.';
    } catch {
      return 'Custom event received.';
    }
  }

  return event.type;
}

function formatTimestamp(value?: string | null): string {
  if (!value) {
    return 'n/a';
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return value;
  }

  return parsed.toLocaleString();
}

function getBindingTone(
  revision: RuntimeGAgentBindingRevision | null | undefined,
): 'blue' | 'gold' | 'purple' | 'default' {
  if (!revision) {
    return 'default';
  }

  switch (revision.implementationKind) {
    case 'gagent':
      return 'blue';
    case 'workflow':
      return 'gold';
    case 'script':
      return 'purple';
    default:
      return 'default';
  }
}

function getRevisionStatusTone(
  revision: RuntimeGAgentBindingRevision,
): 'success' | 'processing' | 'warning' | 'default' {
  if (revision.isActiveServing) {
    return 'processing';
  }
  if (revision.isDefaultServing) {
    return 'success';
  }
  if (revision.retiredAt) {
    return 'default';
  }
  return 'warning';
}

function normalizeBindingEndpoints(
  endpoints: readonly GAgentBindingEndpointDraft[],
): RuntimeGAgentBindingEndpointInput[] {
  return endpoints
    .map((endpoint) => ({
      endpointId: endpoint.endpointId.trim(),
      displayName: endpoint.displayName.trim() || endpoint.endpointId.trim(),
      kind: endpoint.kind,
      requestTypeUrl: endpoint.requestTypeUrl.trim() || undefined,
      responseTypeUrl: endpoint.responseTypeUrl.trim() || undefined,
      description: endpoint.description.trim() || undefined,
    }))
    .filter((endpoint) => endpoint.endpointId.length > 0);
}

const GAgentsPage: React.FC = () => {
  const screens = Grid.useBreakpoint();
  const queryClient = useQueryClient();
  const abortControllerRef = useRef<AbortController | null>(null);
  const [scopeId, setScopeId] = useState(() => readQueryValue('scopeId'));
  const [typeFilter, setTypeFilter] = useState('');
  const [selectedAgentKind, setSelectedAgentKind] = useState(() =>
    readQueryValue('type'),
  );
  const [actorReuseMode, setActorReuseMode] = useState<ActorReuseMode>(() =>
    readQueryValue('actorId') ? 'existing' : 'new',
  );
  const [preferredActorId, setPreferredActorId] = useState(() =>
    readQueryValue('actorId'),
  );
  const [bindingActorReuseMode, setBindingActorReuseMode] =
    useState<ActorReuseMode>(() =>
      readQueryValue('actorId') ? 'existing' : 'new',
    );
  const [prompt, setPrompt] = useState('');
  const [registryNotice, setRegistryNotice] = useState<NoticeState | null>(
    null,
  );
  const [bindingNotice, setBindingNotice] = useState<NoticeState | null>(null);
  const [registryPendingKey, setRegistryPendingKey] = useState('');
  const [bindingPendingKey, setBindingPendingKey] = useState('');
  const [publishAcknowledged, setPublishAcknowledged] = useState(false);
  const [runState, setRunState] = useState<GAgentRunState>(createIdleRunState);
  const [selectedRevisionId, setSelectedRevisionId] = useState('');
  const [activeWorkbenchTab, setActiveWorkbenchTab] = useState('draft');
  const [activePublishTab, setActivePublishTab] = useState('settings');
  const [activeRunOutputTab, setActiveRunOutputTab] = useState('transcript');
  const [isActorRegistryDrawerOpen, setIsActorRegistryDrawerOpen] =
    useState(false);
  const [isRevisionDrawerOpen, setIsRevisionDrawerOpen] = useState(false);
  const [bindingDraft, setBindingDraft] = useState(() =>
    createBindingDraft(readQueryValue('type')),
  );
  const normalizedScopeId = scopeId.trim();

  const authSessionQuery = useQuery({
    queryKey: ['gagents', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );
  const gAgentKindsQuery = useQuery({
    queryKey: ['runtime-gagents', 'kinds'],
    queryFn: () => runtimeGAgentApi.listKinds(),
    retry: false,
  });
  const gAgentActorsQuery = useQuery({
    enabled: normalizedScopeId.length > 0,
    queryKey: ['runtime-gagents', 'actors', normalizedScopeId],
    queryFn: () => runtimeGAgentApi.listActors(normalizedScopeId),
    retry: false,
  });
  const bindingQuery = useQuery({
    enabled: normalizedScopeId.length > 0,
    queryKey: ['runtime-gagents', 'binding', normalizedScopeId],
    queryFn: () => runtimeGAgentApi.getDefaultRouteTarget(normalizedScopeId),
    retry: false,
  });

  useEffect(() => {
    if (!scopeId.trim() && resolvedScope?.scopeId) {
      setScopeId(resolvedScope.scopeId);
    }
  }, [resolvedScope?.scopeId, scopeId]);

  const selectedKindDescriptor = useMemo(
    () =>
      (gAgentKindsQuery.data ?? []).find((descriptor) =>
        matchesRuntimeGAgentKindDescriptor(selectedAgentKind, descriptor),
      ) || null,
    [gAgentKindsQuery.data, selectedAgentKind],
  );

  useEffect(() => {
    if (selectedKindDescriptor || !gAgentKindsQuery.data?.length) {
      return;
    }

    const defaultType = gAgentKindsQuery.data[0];
    setSelectedAgentKind(
      buildRuntimeGAgentKindValue(defaultType),
    );
    setBindingDraft((current) =>
      current.displayName.trim()
        ? current
        : {
            ...current,
            displayName: buildRuntimeGAgentKindLabel(defaultType),
          },
    );
  }, [gAgentKindsQuery.data, selectedKindDescriptor]);

  const currentBindingRevision = useMemo(
    () => getRuntimeGAgentCurrentBindingRevision(bindingQuery.data),
    [bindingQuery.data],
  );

  useEffect(() => {
    const revisions = bindingQuery.data?.revisions ?? [];
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
      currentBindingRevision?.revisionId || revisions[0]?.revisionId || '',
    );
  }, [
    bindingQuery.data?.revisions,
    currentBindingRevision,
    selectedRevisionId,
  ]);

  const selectedRevision = useMemo(
    () =>
      bindingQuery.data?.revisions.find(
        (revision) => revision.revisionId === selectedRevisionId,
      ) ||
      currentBindingRevision ||
      null,
    [bindingQuery.data?.revisions, currentBindingRevision, selectedRevisionId],
  );

  const filteredKinds = useMemo(() => {
    const normalizedKeyword = typeFilter.trim().toLowerCase();
    const descriptors = gAgentKindsQuery.data ?? [];
    if (!normalizedKeyword) {
      return descriptors;
    }

    return descriptors.filter((descriptor) =>
      [descriptor.displayName, descriptor.agentKind, descriptor.diagnosticClrTypeName]
        .join(' ')
        .toLowerCase()
        .includes(normalizedKeyword),
    );
  }, [gAgentKindsQuery.data, typeFilter]);

  const savedActorIds = useMemo(
    () =>
      collectRuntimeGAgentActorIds(
        selectedAgentKind,
        gAgentActorsQuery.data ?? [],
        selectedKindDescriptor,
      ),
    [gAgentActorsQuery.data, selectedAgentKind, selectedKindDescriptor],
  );

  const selectedActorStoreAgentKind =
    selectedKindDescriptor?.agentKind || selectedAgentKind.split(',')[0]?.trim() || '';

  const actorGroups = useMemo(() => {
    const descriptors = gAgentKindsQuery.data ?? [];
    return [...(gAgentActorsQuery.data ?? [])].sort((left, right) => {
      const leftSelected = descriptors.some(
        (descriptor) =>
          matchesRuntimeGAgentKindDescriptor(left.agentKind, descriptor) &&
          matchesRuntimeGAgentKindDescriptor(selectedAgentKind, descriptor),
      );
      const rightSelected = descriptors.some(
        (descriptor) =>
          matchesRuntimeGAgentKindDescriptor(right.agentKind, descriptor) &&
          matchesRuntimeGAgentKindDescriptor(selectedAgentKind, descriptor),
      );
      if (leftSelected !== rightSelected) {
        return leftSelected ? -1 : 1;
      }

      return left.agentKind.localeCompare(right.agentKind);
    });
  }, [gAgentActorsQuery.data, gAgentKindsQuery.data, selectedAgentKind]);

  const totalSavedActors = useMemo(
    () =>
      actorGroups.reduce((count, group) => count + group.actorIds.length, 0),
    [actorGroups],
  );

  const bindingSavedActorId = useMemo(() => {
    const normalizedPreferredActorId = bindingDraft.preferredActorId.trim();
    if (
      !normalizedPreferredActorId ||
      !savedActorIds.includes(normalizedPreferredActorId)
    ) {
      return undefined;
    }

    return normalizedPreferredActorId;
  }, [bindingDraft.preferredActorId, savedActorIds]);

  const launchableBindingEndpoints = useMemo(
    () =>
      bindingDraft.endpoints.filter(
        (endpoint) => endpoint.endpointId.trim().length > 0,
      ),
    [bindingDraft.endpoints],
  );

  const selectedLaunchEndpoint = useMemo(
    () =>
      launchableBindingEndpoints.find(
        (endpoint) =>
          endpoint.endpointId.trim() === bindingDraft.openRunsEndpointId.trim(),
      ) ||
      launchableBindingEndpoints[0] ||
      null,
    [bindingDraft.openRunsEndpointId, launchableBindingEndpoints],
  );

  const currentBindingMatchesSelectedType = useMemo(
    () =>
      selectedKindDescriptor
        ? matchesRuntimeGAgentKindDescriptor(
            currentBindingRevision?.staticAgentKind ?? '',
            selectedKindDescriptor,
          )
        : false,
    [currentBindingRevision?.staticAgentKind, selectedKindDescriptor],
  );

  const bindingImpactMessage = useMemo(() => {
    if (!selectedKindDescriptor) {
      return 'Select a discovered GAgent kind to prepare a published binding.';
    }

    if (!bindingQuery.data?.available || !currentBindingRevision) {
      return 'This will create the first published default service for the current workspace.';
    }

    if (currentBindingMatchesSelectedType) {
      return 'This will publish a new revision for the current GAgent service without changing the product surface.';
    }

    return `This will replace the current default service (${formatRuntimeGAgentBindingImplementationKind(currentBindingRevision.implementationKind)} · ${describeRuntimeGAgentBindingRevisionTarget(currentBindingRevision)}) with the selected GAgent kind.`;
  }, [
    bindingQuery.data?.available,
    currentBindingMatchesSelectedType,
    currentBindingRevision,
    selectedKindDescriptor,
  ]);

  useEffect(() => {
    history.replace(
      buildRuntimeGAgentsHref({
        scopeId: normalizedScopeId || undefined,
        agentKind: selectedAgentKind.trim() || undefined,
        actorId:
          actorReuseMode === 'existing'
            ? preferredActorId.trim() || undefined
            : undefined,
      }),
    );
  }, [
    actorReuseMode,
    normalizedScopeId,
    preferredActorId,
    selectedAgentKind,
  ]);

  useEffect(() => () => abortControllerRef.current?.abort(), []);

  useEffect(() => {
    const routeName =
      selectedKindDescriptor?.displayName ||
      selectedAgentKind.split(',')[0]?.trim() ||
      '';
    const candidateId =
      runState.commandId ||
      runState.runId ||
      (runState.actorId && routeName ? `${routeName}:${runState.actorId}` : '');

    if (!candidateId || !prompt.trim() || runState.events.length === 0) {
      return;
    }

    saveRecentRun({
      id: candidateId,
      scopeId: normalizedScopeId,
      serviceOverrideId: '',
      endpointId: CHAT_ENDPOINT_ID,
      endpointKind: 'chat',
      payloadTypeUrl: '',
      payloadBase64: '',
      routeName,
      prompt: prompt.trim(),
      actorId: runState.actorId,
      commandId: runState.commandId,
      runId: runState.runId,
      status: runState.status,
      lastMessagePreview:
        runState.assistantText.trim() || runState.error.trim() || routeName,
      observedEvents: runState.events.map((event) => ({ ...event })),
    });
  }, [
    prompt,
    runState.actorId,
    runState.assistantText,
    runState.commandId,
    runState.error,
    runState.events,
    runState.runId,
    runState.status,
    normalizedScopeId,
    selectedAgentKind,
    selectedKindDescriptor?.displayName,
  ]);

  const invalidateActorQueries = async (targetScopeId: string) => {
    const normalizedTargetScopeId = targetScopeId.trim();
    if (!normalizedTargetScopeId) {
      return;
    }

    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: ['runtime-gagents', 'actors', normalizedTargetScopeId],
      }),
      queryClient.invalidateQueries({
        queryKey: ['studio-runtime-gagent-actors', normalizedTargetScopeId],
      }),
    ]);
  };

  const invalidateBindingQueries = async (targetScopeId: string) => {
    const normalizedTargetScopeId = targetScopeId.trim();
    if (!normalizedTargetScopeId) {
      return;
    }

    await Promise.all([
      queryClient.invalidateQueries({
        queryKey: ['runtime-gagents', 'binding', normalizedTargetScopeId],
      }),
      queryClient.invalidateQueries({
        queryKey: ['studio-scope-binding', normalizedTargetScopeId],
      }),
      queryClient.invalidateQueries({
        queryKey: ['scopes', 'binding', normalizedTargetScopeId],
      }),
    ]);
  };

  const resolveAgentKindSelection = (agentKind: string): string => {
    const descriptor = (gAgentKindsQuery.data ?? []).find((entry) =>
      matchesRuntimeGAgentKindDescriptor(agentKind, entry),
    );
    return descriptor
      ? buildRuntimeGAgentKindValue(descriptor)
      : agentKind.trim();
  };

  const handleSelectKind = (agentKind: string) => {
    const descriptor = (gAgentKindsQuery.data ?? []).find((entry) =>
      matchesRuntimeGAgentKindDescriptor(agentKind, entry),
    );
    const nextAgentKind = descriptor
      ? buildRuntimeGAgentKindValue(descriptor)
      : agentKind.trim();

    setSelectedAgentKind(nextAgentKind);
    setBindingNotice(null);
    setPublishAcknowledged(false);
    setBindingDraft((current) => {
      const currentDisplayName = current.displayName.trim();
      const selectedKindLabel = selectedKindDescriptor?.displayName.trim() || '';
      const nextDisplayName =
        !currentDisplayName || currentDisplayName === selectedKindLabel
          ? descriptor?.displayName || current.displayName
          : current.displayName;
      return {
        ...current,
        displayName: nextDisplayName,
      };
    });

    if (actorReuseMode === 'existing' && !preferredActorId.trim()) {
      const nextActorId =
        collectRuntimeGAgentActorIds(
          nextAgentKind,
          gAgentActorsQuery.data ?? [],
          descriptor,
        )[0] || '';
      setPreferredActorId(nextActorId);
    }

    if (
      bindingActorReuseMode === 'existing' &&
      !bindingDraft.preferredActorId.trim()
    ) {
      const nextBindingActorId =
        collectRuntimeGAgentActorIds(
          nextAgentKind,
          gAgentActorsQuery.data ?? [],
          descriptor,
        )[0] || '';
      setBindingDraft((current) => ({
        ...current,
        preferredActorId: nextBindingActorId,
      }));
    }
  };

  const handleUseRegistryActor = (agentKind: string, actorId: string) => {
    handleSelectKind(agentKind);
    setActorReuseMode('existing');
    setPreferredActorId(actorId);
    setActiveWorkbenchTab('draft');
    setIsActorRegistryDrawerOpen(false);
    setRegistryNotice({
      type: 'success',
      message: t('pages.gagents.index.prepared.actor.for.next', 'Saved actor is ready for the next draft run.'),
    });
  };

  const handleRemoveRegistryActor = async (
    agentKind: string,
    actorId: string,
  ) => {
    if (!normalizedScopeId) {
      setRegistryNotice({
        type: 'error',
        message: t("pages.gagents.index.workspace.is.required.before.removing", "Workspace is required before removing a saved actor."),
      });
      return;
    }

    setRegistryPendingKey(`remove:${agentKind}:${actorId}`);
    setRegistryNotice(null);
    try {
      await runtimeGAgentApi.removeActor(
        normalizedScopeId,
        agentKind,
        actorId,
      );
      await invalidateActorQueries(normalizedScopeId);
      if (preferredActorId.trim() === actorId.trim()) {
        setPreferredActorId('');
      }
      if (bindingDraft.preferredActorId.trim() === actorId.trim()) {
        setBindingDraft((current) => ({
          ...current,
          preferredActorId: '',
        }));
      }
      setRegistryNotice({
        type: 'success',
        message: t('pages.gagents.index.removed.actor.from.registry', 'Removed the saved actor from the registry.'),
      });
    } catch (error) {
      setRegistryNotice({
        type: 'error',
        message: describeError(error),
      });
    } finally {
      setRegistryPendingKey('');
    }
  };

  const updateBindingEndpointDraft = (
    index: number,
    patch: Partial<GAgentBindingEndpointDraft>,
  ) => {
    setBindingDraft((current) => {
      const previousEndpoint = current.endpoints[index];
      if (!previousEndpoint) {
        return current;
      }

      const nextEndpoint = {
        ...previousEndpoint,
        ...patch,
      };
      const nextEndpoints = current.endpoints.map((endpoint, endpointIndex) =>
        endpointIndex === index ? nextEndpoint : endpoint,
      );
      const nextOpenRunsEndpointId =
        current.openRunsEndpointId.trim() === previousEndpoint.endpointId.trim()
          ? nextEndpoint.endpointId
          : current.openRunsEndpointId;

      return {
        ...current,
        endpoints: nextEndpoints,
        openRunsEndpointId: nextOpenRunsEndpointId,
      };
    });
    setPublishAcknowledged(false);
    setBindingNotice(null);
  };

  const addBindingEndpointDraft = () => {
    setActivePublishTab('endpoints');
    setBindingDraft((current) => {
      const nextIndex = current.endpoints.length + 1;
      const endpoint = createBindingEndpointDraft({
        endpointId: `run-${nextIndex}`,
        displayName: `Run ${nextIndex}`,
      });

      return {
        ...current,
        endpoints: [...current.endpoints, endpoint],
      };
    });
    setPublishAcknowledged(false);
    setBindingNotice(null);
  };

  const removeBindingEndpointDraft = (index: number) => {
    setBindingDraft((current) => {
      if (current.endpoints.length <= 1) {
        return current;
      }

      const removedEndpoint = current.endpoints[index];
      if (!removedEndpoint) {
        return current;
      }

      const nextEndpoints = current.endpoints.filter(
        (_endpoint, endpointIndex) => endpointIndex !== index,
      );
      const nextOpenRunsEndpointId =
        current.openRunsEndpointId.trim() === removedEndpoint.endpointId.trim()
          ? (nextEndpoints[0]?.endpointId ?? '')
          : current.openRunsEndpointId;

      return {
        ...current,
        endpoints: nextEndpoints,
        openRunsEndpointId: nextOpenRunsEndpointId,
      };
    });
    setPublishAcknowledged(false);
    setBindingNotice(null);
  };

  const openRunsForPublishedEndpoint = (
    endpoint: RuntimeGAgentBindingEndpointInput | GAgentBindingEndpointDraft,
  ) => {
    const endpointKind = normalizeRunEndpointKind(
      endpoint.kind,
      endpoint.endpointId,
    );
    history.push(
      buildRuntimeRunsHref({
        scopeId: normalizedScopeId || undefined,
        endpointId: endpoint.endpointId.trim() || undefined,
        endpointKind,
        payloadTypeUrl:
          endpointKind === 'command'
            ? endpoint.requestTypeUrl?.trim() || undefined
            : undefined,
        prompt:
          endpointKind === 'chat' ? prompt.trim() || undefined : undefined,
      }),
    );
  };

  const handlePublishBinding = async (options?: { openRuns?: boolean }) => {
    const agentKind =
      selectedKindDescriptor != null
        ? buildRuntimeGAgentKindValue(selectedKindDescriptor)
        : selectedAgentKind.trim();
    const normalizedEndpoints = normalizeBindingEndpoints(
      bindingDraft.endpoints,
    );
    const preferredActor =
      bindingActorReuseMode === 'existing'
        ? bindingDraft.preferredActorId.trim()
        : '';
    const duplicateEndpointId = normalizedEndpoints.find(
      (endpoint, index) =>
        normalizedEndpoints.findIndex(
          (candidate) => candidate.endpointId === endpoint.endpointId,
        ) !== index,
    )?.endpointId;

    if (!normalizedScopeId) {
      setBindingNotice({
        type: 'error',
        message:
          t("pages.gagents.index.resolve.the.current.workspace.before", "Resolve the current workspace before publishing a GAgent binding."),
      });
      return;
    }

    if (!agentKind) {
      setBindingNotice({
        type: 'error',
        message: t("pages.gagents.index.choose.discovered.gagent.kind.before", "Choose a discovered GAgent kind before publishing a binding."),
      });
      return;
    }

    if (normalizedEndpoints.length === 0) {
      setActivePublishTab('endpoints');
      setBindingNotice({
        type: 'error',
        message:
          t("pages.gagents.index.add.at.least.one.published", "Add at least one published endpoint before binding the selected GAgent."),
      });
      return;
    }

    if (duplicateEndpointId) {
      setActivePublishTab('endpoints');
      setBindingNotice({
        type: 'error',
        message: t(
          'pages.gagents.index.endpoint.id.duplicated',
          'Endpoint is duplicated. Published endpoints must be unique.',
        ),
      });
      return;
    }

    if (bindingActorReuseMode === 'existing' && !preferredActor) {
      setActivePublishTab('settings');
      setBindingNotice({
        type: 'error',
        message:
          t("pages.gagents.index.provide.preferred.actor.id.when", "Provide a preferred actor id when reusing a stable published actor."),
      });
      return;
    }

    if (bindingQuery.data?.available && !publishAcknowledged) {
      setBindingNotice({
        type: 'error',
        message:
          t("pages.gagents.index.acknowledge.the.replacement.impact.before", "Acknowledge the replacement impact before publishing a new binding revision."),
      });
      return;
    }

    setBindingPendingKey(options?.openRuns ? 'publish:runs' : 'publish');
    setBindingNotice(null);
    try {
      const result = await runtimeGAgentApi.bindScopeGAgent({
        scopeId: normalizedScopeId,
        displayName:
          bindingDraft.displayName.trim() ||
          selectedKindDescriptor?.displayName ||
          agentKind,
        agentKind,
        preferredActorId: preferredActor || undefined,
        endpoints: normalizedEndpoints,
      });
      await invalidateBindingQueries(normalizedScopeId);
      setSelectedRevisionId(result.revisionId);
      setActiveWorkbenchTab('serving');
      setIsRevisionDrawerOpen(true);
      setPublishAcknowledged(false);
      setBindingNotice({
        type: 'success',
        message: t(
          'pages.gagents.index.published.target.on.revision',
          'Published {targetName}.',
          {
            targetName: result.displayName || result.targetName,
          },
        ),
      });

      if (options?.openRuns && selectedLaunchEndpoint) {
        openRunsForPublishedEndpoint(selectedLaunchEndpoint);
      }
    } catch (error) {
      setBindingNotice({
        type: 'error',
        message: describeError(error),
      });
    } finally {
      setBindingPendingKey('');
    }
  };

  const handleActivateRevision = async (revisionId: string) => {
    if (!normalizedScopeId) {
      return;
    }

    setBindingPendingKey(`activate:${revisionId}`);
    setBindingNotice(null);
    try {
      const result = await runtimeGAgentApi.activateMemberBindingRevision(
        normalizedScopeId,
        revisionId,
      );
      await invalidateBindingQueries(normalizedScopeId);
      setSelectedRevisionId(result.revisionId);
      setActiveWorkbenchTab('serving');
      setIsRevisionDrawerOpen(true);
      setBindingNotice({
        type: 'success',
        message: t(
          'pages.gagents.index.workspace.serving.revision',
          'Workspace is now serving the selected revision.',
        ),
      });
    } catch (error) {
      setBindingNotice({
        type: 'error',
        message: describeError(error),
      });
    } finally {
      setBindingPendingKey('');
    }
  };

  const handleRetireRevision = async (revisionId: string) => {
    if (!normalizedScopeId) {
      return;
    }

    setBindingPendingKey(`retire:${revisionId}`);
    setBindingNotice(null);
    try {
      const result = await runtimeGAgentApi.retireMemberBindingRevision(
        normalizedScopeId,
        revisionId,
      );
      await invalidateBindingQueries(normalizedScopeId);
      setActiveWorkbenchTab('serving');
      setBindingNotice({
        type: 'success',
        message: t(
          'pages.gagents.index.revision.accepted.for.retirement',
          'Revision was accepted for retirement.',
        ),
      });
    } catch (error) {
      setBindingNotice({
        type: 'error',
        message: describeError(error),
      });
    } finally {
      setBindingPendingKey('');
    }
  };

  const handleRun = async () => {
    const normalizedAgentKind =
      selectedAgentKind.trim() ||
      (selectedKindDescriptor
        ? buildRuntimeGAgentKindValue(selectedKindDescriptor)
        : '');
    const normalizedPrompt = prompt.trim();
    const normalizedPreferredActorId =
      actorReuseMode === 'existing' ? preferredActorId.trim() : '';

    if (!normalizedScopeId || !normalizedAgentKind || !normalizedPrompt) {
      setRunState((current) => ({
        ...current,
        error: 'Workspace, GAgent kind, and prompt are required before running.',
        status: 'error',
      }));
      return;
    }

    abortControllerRef.current?.abort();
    const controller = new AbortController();
    abortControllerRef.current = controller;
    const timeoutId = window.setTimeout(() => {
      if (!controller.signal.aborted) {
        controller.abort(createDraftRunTimeoutError());
      }
    }, GAGENT_DRAFT_RUN_CLIENT_TIMEOUT_MS);
    setActiveWorkbenchTab('draft');
    setActiveRunOutputTab('transcript');
    setRunState({
      actorId: '',
      assistantText: '',
      commandId: '',
      error: '',
      events: [],
      runId: '',
      status: 'running',
    });

    try {
      const response = await runtimeGAgentApi.streamDraftRun(
        normalizedScopeId,
        {
          agentKind: normalizedAgentKind,
          prompt: normalizedPrompt,
          preferredActorId: normalizedPreferredActorId || undefined,
          timeoutMs: GAGENT_DRAFT_RUN_TIMEOUT_MS,
        },
        controller.signal,
      );

      for await (const event of parseBackendSSEStream(response, {
        signal: controller.signal,
      })) {
        if (controller.signal.aborted) {
          break;
        }

        setRunState((current) => {
          const nextEvents = [...current.events, event];
          let nextAssistantText = current.assistantText;
          let nextActorId = current.actorId;
          let nextCommandId = current.commandId;
          let nextRunId = current.runId;
          let nextError = current.error;
          let nextStatus = current.status;

          if (event.type === AGUIEventType.TEXT_MESSAGE_CONTENT) {
            nextAssistantText += readEventString(event, 'delta');
          }

          if (event.type === AGUIEventType.RUN_STARTED) {
            nextRunId = readEventString(event, 'runId') || nextRunId;
            nextActorId = readEventString(event, 'threadId') || nextActorId;
          }

          if (event.type === AGUIEventType.CUSTOM) {
            try {
              const custom = parseCustomEvent(event);
              if (custom.name === CustomEventName.RunContext) {
                const context = parseRunContextData(custom.data);
                nextActorId = context?.actorId || nextActorId;
                nextCommandId = context?.commandId || nextCommandId;
              }
            } catch {
              // Ignore malformed custom payloads.
            }
          }

          if (event.type === AGUIEventType.RUN_ERROR) {
            nextError =
              readEventString(event, 'message') || 'Draft run failed.';
            nextStatus = 'error';
          }

          return {
            actorId: nextActorId,
            assistantText: nextAssistantText,
            commandId: nextCommandId,
            error: nextError,
            events: nextEvents,
            runId: nextRunId,
            status: nextStatus,
          };
        });
      }

      await invalidateActorQueries(normalizedScopeId);
      setRunState((current) =>
        current.status === 'error' || controller.signal.aborted
          ? current
          : {
              ...current,
              status: current.events.length > 0 ? 'success' : 'idle',
            },
      );
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        const reason = controller.signal.reason;
        if (reason instanceof Error) {
          setRunState((current) => ({
            ...current,
            error: reason.message,
            status: 'error',
          }));
        }
        return;
      }

      setRunState((current) => ({
        ...current,
        error: error instanceof Error ? error.message : String(error),
        status: 'error',
      }));
    } finally {
      window.clearTimeout(timeoutId);
      if (abortControllerRef.current === controller) {
        abortControllerRef.current = null;
      }
    }
  };

  const handleStop = () => {
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    setRunState((current) =>
      current.status === 'running'
        ? {
            ...current,
            status: current.events.length > 0 ? 'success' : 'idle',
          }
        : current,
    );
  };

  const handleOpenRuns = () => {
    const draftKey =
      runState.events.length > 0
        ? saveObservedRunSessionPayload({
            scopeId: normalizedScopeId,
            routeName:
              selectedKindDescriptor?.displayName ||
              selectedAgentKind.split(',')[0]?.trim() ||
              undefined,
            endpointId: CHAT_ENDPOINT_ID,
            endpointKind: 'chat',
            prompt: prompt.trim(),
            events: runState.events,
            actorId: runState.actorId || undefined,
            commandId: runState.commandId || undefined,
            runId: runState.runId || undefined,
          })
        : '';

    history.push(
      buildRuntimeRunsHref({
        route:
          selectedKindDescriptor?.displayName ||
          selectedAgentKind.split(',')[0]?.trim() ||
          undefined,
        scopeId: normalizedScopeId || undefined,
        endpointId: CHAT_ENDPOINT_ID,
        endpointKind: 'chat',
        prompt: prompt.trim() || undefined,
        actorId: runState.actorId || undefined,
        draftKey: draftKey || undefined,
      }),
    );
  };

  const selectedSavedActorId = useMemo(() => {
    const normalizedPreferredActorId = preferredActorId.trim();
    if (
      !normalizedPreferredActorId ||
      !savedActorIds.includes(normalizedPreferredActorId)
    ) {
      return undefined;
    }

    return normalizedPreferredActorId;
  }, [preferredActorId, savedActorIds]);
  const rail = (
    <div style={cliRailShellStyle}>
      <div style={cliRailSectionStyle}>
        <div style={cliCardLabelStyle}>{t("pages.gagents.index.workspace.context", "Workspace Context")}</div>
        <Input
          aria-label={t("pages.gagents.index.workspace.id", "Workspace ID")}
          onChange={(event) => setScopeId(event.target.value)}
          placeholder={t("pages.gagents.index.workspace.id.2", "Workspace ID")}
          value={scopeId}
        />
        <Typography.Text type="secondary">
          {resolvedScope?.scopeId
            ? t("pages.gagents.index.nyxid.resolved.workspace", "NyxID resolved workspace: {scopeId}", {
                scopeId: resolvedScope.scopeId,
              })
            : t("pages.gagents.index.no.workspace.resolved", "No workspace was resolved from the current session.")}
        </Typography.Text>
        {bindingQuery.data?.available ? (
          <div
            style={{
              background: CLI_MUTED_BACKGROUND,
              border: `1px solid ${CLI_BORDER_COLOR}`,
              borderRadius: 14,
              display: 'flex',
              flexDirection: 'column',
              gap: 6,
              padding: 12,
            }}
          >
            <Typography.Text style={cliFieldLabelStyle}>
              {t("pages.gagents.index.current.default", "Current default")}</Typography.Text>
            <Typography.Text strong style={{ overflowWrap: 'anywhere' }}>
              {getUserFacingIdentifierLabel(
                bindingQuery.data.displayName || bindingQuery.data.serviceId,
                t("pages.gagents.index.service", "Service"),
              )}
            </Typography.Text>
            <Typography.Text style={cliCardDescriptionStyle}>
              {formatRuntimeGAgentBindingImplementationKind(
                currentBindingRevision?.implementationKind,
              )}{' '}
              ·{' '}
              {describeRuntimeGAgentBindingRevisionTarget(
                currentBindingRevision,
              )}
            </Typography.Text>
          </div>
        ) : null}
      </div>

      <div style={cliSectionDividerStyle} />

      <div style={{ ...cliRailSectionStyle, flex: 1, minHeight: 0, padding: 0 }}>
        <div
          style={{
            ...cliRailSectionStyle,
            borderBottom: `1px solid ${CLI_BORDER_COLOR}`,
            gap: 10,
          }}
        >
          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 12,
              justifyContent: 'space-between',
            }}
          >
            <div style={cliCardLabelStyle}>{t("pages.gagents.index.gagent.types", "GAgent Kinds")}</div>
            <Button
              icon={<ReloadOutlined />}
              onClick={() => void gAgentKindsQuery.refetch()}
              size="small"
              type="text"
            >
              {t("pages.gagents.index.refresh", "Refresh")}</Button>
          </div>
          <Input
            aria-label={t("pages.gagents.index.filter.gagent.types", "Filter GAgent kinds")}
            onChange={(event) => setTypeFilter(event.target.value)}
            placeholder={t("pages.gagents.index.filter.gagent.types.2", "Filter GAgent kinds")}
            value={typeFilter}
          />
          {gAgentKindsQuery.error ? (
            <Alert
              showIcon
              type="error"
              title={describeError(gAgentKindsQuery.error)}
            />
          ) : null}
        </div>

        {gAgentKindsQuery.isLoading ? (
          <AevatarContentSkeleton
            ariaLabel="Loading runtime GAgent kinds"
            listLayout="stack"
            rows={5}
            variant="list"
          />
        ) : filteredKinds.length === 0 ? (
          <div style={{ padding: 16 }}>
            <Empty
              description={
                gAgentKindsQuery.isLoading
                  ? 'Loading runtime GAgent kinds.'
                  : 'No GAgent kinds matched the current filter.'
              }
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          </div>
        ) : (
          <div
            style={{
              ...scrollColumnStyle,
              gap: 0,
              maxHeight: 720,
              paddingRight: 0,
            }}
          >
            {filteredKinds.map((descriptor) => {
              const agentKindValue =
                buildRuntimeGAgentKindValue(descriptor);
              const isSelected =
                selectedKindDescriptor?.agentKind === descriptor.agentKind ||
                selectedAgentKind.trim() === agentKindValue;
              const isActiveBindingType = matchesRuntimeGAgentKindDescriptor(
                currentBindingRevision?.staticAgentKind ?? '',
                descriptor,
              );
              const actorCount = collectRuntimeGAgentActorIds(
                agentKindValue,
                gAgentActorsQuery.data ?? [],
                descriptor,
              ).length;

              return (
                <button
                  className={AEVATAR_PRESSABLE_CARD_CLASS}
                  key={agentKindValue}
                  onClick={() => handleSelectKind(agentKindValue)}
                  style={{
                    background: isSelected ? '#eff6ff' : '#ffffff',
                    border: 'none',
                    borderBottom: `1px solid ${CLI_BORDER_COLOR}`,
                    borderLeft: isSelected
                      ? '3px solid #2563eb'
                      : '3px solid transparent',
                    cursor: 'pointer',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 6,
                    minWidth: 0,
                    padding: '14px 16px',
                    textAlign: 'left',
                    width: '100%',
                  }}
                  type="button"
                >
                  <Space size={[8, 8]} wrap>
                    <Space size={[8, 8]} wrap>
                      <RobotOutlined />
                      <Typography.Text strong>
                        {buildRuntimeGAgentKindLabel(descriptor)}
                      </Typography.Text>
                    </Space>
                    {isActiveBindingType ? <Tag color="success">{t("pages.gagents.index.serving", "Serving")}</Tag> : null}
                    {actorCount > 0 ? <Tag>{actorCount} {t("pages.gagents.index.actors", "actors")}</Tag> : null}
                  </Space>
                  <Typography.Text
                    style={{
                      color: '#6b7280',
                      fontSize: 12,
                      overflowWrap: 'anywhere',
                      wordBreak: 'break-word',
                    }}
                  >
                    {descriptor.agentKind}
                  </Typography.Text>
                </button>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );

  const selectedTypePanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.current.kind.selection.that.drives", "Current kind selection that drives both draft runs and published bindings.")}
      eyebrow="Selected Kind"
      extra={
        <Space size={[8, 8]} wrap>
          <Button
            disabled={!selectedKindDescriptor}
            onClick={() => setIsActorRegistryDrawerOpen(true)}
            size="small"
          >
            {t("pages.gagents.index.manage.actors", "Manage actors")}</Button>
          {selectedKindDescriptor ? (
            <>
              {currentBindingMatchesSelectedType ? (
                <Tag color="success">{t("pages.gagents.index.active.binding", "Active binding")}</Tag>
              ) : null}
              {savedActorIds.length > 0 ? (
                <Tag>{savedActorIds.length} {t("pages.gagents.index.actors.2", "actors")}</Tag>
              ) : null}
            </>
          ) : (
            <Tag>{t("pages.gagents.index.choose.kind", "Choose a kind")}</Tag>
          )}
        </Space>
      }
      title={selectedKindDescriptor ? selectedKindDescriptor.displayName : 'No kind selected'}
    >
      {selectedKindDescriptor ? (
        <div
          style={{
            display: 'grid',
            gap: 12,
            gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
          }}
        >
          <div style={summaryMetricStyle}>
            <Typography.Text type="secondary">{t("pages.gagents.index.kind", "Kind")}</Typography.Text>
            <Typography.Paragraph style={wrappedTextStyle}>
              {selectedKindDescriptor.agentKind}
            </Typography.Paragraph>
          </div>
          <div style={summaryMetricStyle}>
            <Typography.Text type="secondary">{t("pages.gagents.index.assembly", "Assembly")}</Typography.Text>
            <Typography.Paragraph style={wrappedTextStyle}>
              {selectedKindDescriptor.diagnosticClrTypeName}
            </Typography.Paragraph>
          </div>
          <div style={summaryMetricStyle}>
            <Typography.Text type="secondary">{t("pages.gagents.index.saved.actors", "Saved actors")}</Typography.Text>
            <Typography.Paragraph style={wrappedTextStyle}>
              {savedActorIds.length}
            </Typography.Paragraph>
          </div>
          <div style={summaryMetricStyle}>
            <Typography.Text type="secondary">{t("pages.gagents.index.published.target", "Published target")}</Typography.Text>
            <Typography.Paragraph style={wrappedTextStyle}>
              {currentBindingMatchesSelectedType
                ? describeRuntimeGAgentBindingRevisionTarget(
                    currentBindingRevision,
                  )
                : t("pages.gagents.index.not.serving.this.kind.yet", "Not serving this kind yet")}
            </Typography.Paragraph>
          </div>
        </div>
      ) : (
        <AevatarInspectorEmpty description={t("pages.gagents.index.choose.discovered.gagent.kind.from", "Choose a discovered GAgent kind from the left rail to prepare draft runs or published bindings.")} />
      )}
    </WorkbenchCard>
  );

  const selectedRevisionPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.inspect.the.selected.published.revision", "Inspect the selected published revision.")}
      eyebrow={t("pages.gagents.index.selected.revision", "Selected Revision")}
      title={
        selectedRevision
          ? t("pages.gagents.index.selected.revision", "Selected Revision")
          : t("pages.gagents.index.no.revision.selected", "No revision selected")
      }
    >
      {selectedRevision ? (
        <Space orientation="vertical" size={12} style={{ width: '100%' }}>
          <Space size={[8, 8]} wrap>
            <Tag color={getBindingTone(selectedRevision)}>
              {formatRuntimeGAgentBindingImplementationKind(
                selectedRevision.implementationKind,
              )}
            </Tag>
          </Space>
          <Typography.Text
            style={{ overflowWrap: 'anywhere', wordBreak: 'break-word' }}
          >
            {describeRuntimeGAgentBindingRevisionTarget(selectedRevision)}
          </Typography.Text>
          <div style={{ display: 'grid', gap: 12 }}>
            <div>
              <Typography.Text type="secondary">
                {t("pages.gagents.index.preferred.actor", "Preferred actor")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {selectedRevision.staticPreferredActorId
                  ? t("pages.gagents.index.configured", "Configured")
                  : 'n/a'}
              </Typography.Paragraph>
            </div>
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.primary.actor", "Primary actor")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {selectedRevision.primaryActorId
                  ? t("pages.gagents.index.available", "Available")
                  : 'n/a'}
              </Typography.Paragraph>
            </div>
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.deployment", "Deployment")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {selectedRevision.deploymentId
                  ? t("pages.gagents.index.attached", "Attached")
                  : 'draft'}
              </Typography.Paragraph>
            </div>
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.published", "Published")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {formatTimestamp(selectedRevision.publishedAt)}
              </Typography.Paragraph>
            </div>
          </div>
        </Space>
      ) : (
        <AevatarInspectorEmpty description={t("pages.gagents.index.publish.or.select.revision.to", "Publish or select a revision to inspect its serving details.")} />
      )}
    </WorkbenchCard>
  );

  const runOutputPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.transcript.and.runtime.events.from", "Transcript and runtime events from the most recent draft run.")}
      eyebrow="Run Output"
      title={runState.runId ? t("pages.gagents.index.latest.run.output", "Latest Run Output") : 'Run Output'}
    >
      <Tabs
        activeKey={activeRunOutputTab}
        items={[
          {
            key: 'transcript',
            label: 'Transcript',
            children: runState.assistantText.trim() ? (
              <Typography.Paragraph
                style={{
                  marginBottom: 0,
                  maxHeight: 360,
                  overflowY: 'auto',
                  paddingRight: 4,
                  whiteSpace: 'pre-wrap',
                }}
              >
                {runState.assistantText}
              </Typography.Paragraph>
            ) : (
              <AevatarInspectorEmpty description={t("pages.gagents.index.run.draft.prompt.to.watch", "Run a draft prompt to watch the streamed GAgent response here.")} />
            ),
          },
          {
            key: 'events',
            label: t('pages.gagents.index.event.feed.count', 'Event Feed ({count})', { count: runState.events.length }),
            children:
              runState.events.length === 0 ? (
                <AevatarInspectorEmpty description={t("pages.gagents.index.no.events.captured.yet", "No events captured yet.")} />
              ) : (
                <div style={{ ...scrollColumnStyle, maxHeight: 360 }}>
                  {runState.events.slice(-12).map((event, index) => (
                    <div
                      key={createEventKey(event, index)}
                      style={{
                        ...infoCardStyle,
                        display: 'flex',
                        flexDirection: 'column',
                        gap: 4,
                      }}
                    >
                      <Space size={[8, 8]} wrap>
                        <Tag>{event.type}</Tag>
                      </Space>
                      <Typography.Text
                        style={{ overflowWrap: 'anywhere', wordBreak: 'break-word' }}
                      >
                        {buildEventSummary(event)}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ),
          },
        ]}
        onChange={setActiveRunOutputTab}
      />
    </WorkbenchCard>
  );

  const actorRegistryPanel = (
    <WorkbenchCard
      description={
        selectedKindDescriptor
          ? `Reusable actor ids saved for ${selectedKindDescriptor.displayName} in this workspace.`
          : 'Reusable actor ids saved for this workspace.'
      }
      eyebrow="Actor Registry"
      extra={
        <Button
          icon={<ReloadOutlined />}
          onClick={() => void gAgentActorsQuery.refetch()}
          size="small"
          type="text"
        >
          {t("pages.gagents.index.refresh.2", "Refresh")}</Button>
      }
      title={t("pages.gagents.index.actor.registry", "Actor Registry")}
    >
      <Space orientation="vertical" size={12} style={{ width: '100%' }}>
        <ConsoleOperationNotice
          errorMessage={t(
            'pages.gagents.index.registryActionFailed',
            'Actor registry action could not be completed. Try again.',
          )}
          notice={registryNotice}
          onClose={() => setRegistryNotice(null)}
        />

        {gAgentActorsQuery.error ? (
          <Alert
            showIcon
            title={describeError(gAgentActorsQuery.error)}
            type="warning"
          />
        ) : gAgentActorsQuery.isLoading ? (
          <AevatarContentSkeleton
            ariaLabel="Loading actor registry"
            listLayout="stack"
            rows={4}
            variant="list"
          />
        ) : actorGroups.length === 0 ? (
          <Empty
            description={
              gAgentActorsQuery.isLoading
                ? 'Loading actor registry.'
                : 'No saved actors were found for this workspace.'
            }
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          />
        ) : (
          <div style={compactListStyle}>
            <Typography.Text type="secondary">
              {totalSavedActors} {t("pages.gagents.index.saved.actor", "saved actor")}{totalSavedActors === 1 ? '' : 's'}{' '}
              {t("pages.gagents.index.across", "across")}{actorGroups.length} {t("pages.gagents.index.kind.2", "kind")}{actorGroups.length === 1 ? '' : 's'}.
            </Typography.Text>
            {actorGroups.map((group) => {
              const descriptor = (gAgentKindsQuery.data ?? []).find((entry) =>
                matchesRuntimeGAgentKindDescriptor(group.agentKind, entry),
              );
              const groupLabel = descriptor
                ? buildRuntimeGAgentKindLabel(descriptor)
                : group.agentKind.split('.').pop() || group.agentKind;
              const isSelectedGroup = descriptor
                ? matchesRuntimeGAgentKindDescriptor(
                    selectedAgentKind,
                    descriptor,
                  )
                : selectedActorStoreAgentKind === group.agentKind;

              return (
                <div
                  key={group.agentKind}
                  style={{
                    border: isSelectedGroup
                      ? '1px solid rgba(22, 119, 255, 0.35)'
                      : '1px solid rgba(15, 23, 42, 0.08)',
                    borderRadius: 10,
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 10,
                    padding: 12,
                  }}
                >
                  <Space size={[8, 8]} wrap>
                    <Typography.Text strong>{groupLabel}</Typography.Text>
                    <Tag>{group.actorIds.length}</Tag>
                    {isSelectedGroup ? <Tag color="blue">{t("pages.gagents.index.selected", "Selected")}</Tag> : null}
                  </Space>
                  <Typography.Text
                    style={{
                      overflowWrap: 'anywhere',
                      wordBreak: 'break-word',
                    }}
                    type="secondary"
                  >
                    {group.agentKind}
                  </Typography.Text>
                  <div
                    style={{ display: 'flex', flexDirection: 'column', gap: 8 }}
                  >
                    {group.actorIds.map((actorId) => {
                      const removeKey = `remove:${group.agentKind}:${actorId}`;
                      return (
                        <div
                          key={`${group.agentKind}:${actorId}`}
                          style={{
                            alignItems: screens.sm ? 'center' : 'flex-start',
                            display: 'flex',
                            flexDirection: screens.sm ? 'row' : 'column',
                            gap: 8,
                            justifyContent: 'space-between',
                          }}
                        >
                          <Typography.Text style={{ flex: 1, minWidth: 0 }}>
                            {t("pages.gagents.index.saved.actor", "Saved actor")}
                          </Typography.Text>
                          <Space
                            size={[8, 8]}
                            style={
                              screens.sm
                                ? undefined
                                : { justifyContent: 'flex-start' }
                            }
                            wrap
                          >
                            <Button
                              onClick={() =>
                                handleUseRegistryActor(
                                  group.agentKind,
                                  actorId,
                                )
                              }
                              size="small"
                            >
                              {t("pages.gagents.index.use", "Use")}</Button>
                            <Button
                              danger
                              loading={registryPendingKey === removeKey}
                              onClick={() =>
                                void handleRemoveRegistryActor(
                                  group.agentKind,
                                  actorId,
                                )
                              }
                              size="small"
                            >
                              {t("pages.gagents.index.remove", "Remove")}</Button>
                          </Space>
                        </div>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </Space>
    </WorkbenchCard>
  );

  const currentBindingPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.current.default.service.for.this", "Current default service for this workspace.")}
      eyebrow="Current Workspace Binding"
      extra={
        <Space size={[8, 8]} wrap>
          <Button
            disabled={!selectedRevision}
            onClick={() => setIsRevisionDrawerOpen(true)}
            size="small"
          >
            {t("pages.gagents.index.revision.details", "Revision details")}</Button>
          <Button
            icon={<ReloadOutlined />}
            onClick={() => void bindingQuery.refetch()}
            size="small"
            type="text"
          >
            {t("pages.gagents.index.refresh.3", "Refresh")}</Button>
        </Space>
      }
      title={
        bindingQuery.data?.available
          ? bindingQuery.data.displayName || bindingQuery.data.serviceId
          : 'No published binding'
      }
    >
      <Space orientation="vertical" size={16} style={{ width: '100%' }}>
        {bindingQuery.error ? (
          <Alert
            showIcon
            type="error"
            title={describeError(bindingQuery.error)}
          />
        ) : bindingQuery.isLoading ? (
          <AevatarInspectorEmpty description={t("pages.gagents.index.loading.the.current.published.binding", "Loading the current published binding.")} />
        ) : !bindingQuery.data?.available ? (
          <AevatarInspectorEmpty description={t("pages.gagents.index.no.default.scope.service.has", "No default scope service has been published yet.")} />
        ) : (
          <>
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
              }}
            >
              <div style={infoCardStyle}>
                <Typography.Text type="secondary">{t("pages.gagents.index.display.name", "Display name")}</Typography.Text>
                <Typography.Paragraph style={wrappedTextStyle}>
                  {getUserFacingIdentifierLabel(
                    bindingQuery.data.displayName || bindingQuery.data.serviceId,
                    t("pages.gagents.index.service", "Service"),
                  )}
                </Typography.Paragraph>
              </div>
              <div style={infoCardStyle}>
                <Typography.Text type="secondary">
                  {t("pages.gagents.index.implementation", "Implementation")}</Typography.Text>
                <Space orientation="vertical" size={4} style={{ marginTop: 4 }}>
                  <Tag color={getBindingTone(currentBindingRevision)}>
                    {formatRuntimeGAgentBindingImplementationKind(
                      currentBindingRevision?.implementationKind,
                    )}
                  </Tag>
                  <Typography.Text
                    style={{
                      overflowWrap: 'anywhere',
                      wordBreak: 'break-word',
                    }}
                  >
                    {describeRuntimeGAgentBindingRevisionTarget(
                      currentBindingRevision,
                    )}
                  </Typography.Text>
                </Space>
              </div>
              <div style={infoCardStyle}>
                <Typography.Text type="secondary">{t("pages.gagents.index.serving.2", "Serving")}</Typography.Text>
                <Typography.Paragraph style={wrappedTextStyle}>
                  {bindingQuery.data.deploymentStatus || 'n/a'}
                </Typography.Paragraph>
              </div>
              <div style={infoCardStyle}>
                <Typography.Text type="secondary">
                  {t("pages.gagents.index.default.revision", "Default revision")}</Typography.Text>
                <Typography.Paragraph style={wrappedTextStyle}>
                  {bindingQuery.data.defaultServingRevisionId || 'n/a'}
                </Typography.Paragraph>
              </div>
            </div>

            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
              }}
            >
              <div>
                <Typography.Text type="secondary">{t("pages.gagents.index.service.key", "Service key")}</Typography.Text>
                <Typography.Paragraph copyable style={wrappedTextStyle}>
                  {bindingQuery.data.serviceKey || 'n/a'}
                </Typography.Paragraph>
              </div>
              <div>
                <Typography.Text type="secondary">
                  {t("pages.gagents.index.primary.actor.2", "Primary actor")}</Typography.Text>
                <Typography.Paragraph copyable style={wrappedTextStyle}>
                  {bindingQuery.data.primaryActorId ||
                    currentBindingRevision?.primaryActorId ||
                    'n/a'}
                </Typography.Paragraph>
              </div>
              <div>
                <Typography.Text type="secondary">{t("pages.gagents.index.updated.2", "Updated")}</Typography.Text>
                <Typography.Paragraph style={wrappedTextStyle}>
                  {formatTimestamp(bindingQuery.data.updatedAt)}
                </Typography.Paragraph>
              </div>
            </div>
          </>
        )}
      </Space>
    </WorkbenchCard>
  );

  const publishBindingPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.publish.the.selected.gagent.kind", "Publish the selected GAgent kind as this workspace's default service.")}
      eyebrow="Publish Binding"
      extra={
        <Button
          disabled={!selectedKindDescriptor}
          onClick={() => setIsActorRegistryDrawerOpen(true)}
          size="small"
        >
          {t("pages.gagents.index.manage.actors.2", "Manage actors")}</Button>
      }
      title={
        selectedKindDescriptor
          ? `Publish ${selectedKindDescriptor.displayName}`
          : 'Publish GAgent Binding'
      }
    >
      <Space orientation="vertical" size={16} style={{ width: '100%' }}>
        {!selectedKindDescriptor ? (
          <AevatarInspectorEmpty description={t("pages.gagents.index.choose.discovered.gagent.kind.before.2", "Choose a discovered GAgent kind before configuring a published binding.")} />
        ) : (
          <>
            <Tabs
              activeKey={activePublishTab}
              items={[
                {
                  key: 'settings',
                  label: t("pages.gagents.index.service.settings", "Service settings"),
                  children: (
                    <Space orientation="vertical" size={16} style={{ width: '100%' }}>
                      <div
                        style={{
                          display: 'grid',
                          gap: 12,
                          gridTemplateColumns:
                            'repeat(auto-fit, minmax(220px, 1fr))',
                        }}
                      >
                        <div>
                          <Typography.Text type="secondary">
                            {t("pages.gagents.index.selected.kind", "Selected kind")}</Typography.Text>
                          <Typography.Paragraph style={wrappedTextStyle}>
                            {selectedKindDescriptor.agentKind}
                          </Typography.Paragraph>
                        </div>
                        <div>
                          <Typography.Text type="secondary">{t("pages.gagents.index.assembly.2", "Assembly")}</Typography.Text>
                          <Typography.Paragraph style={wrappedTextStyle}>
                            {selectedKindDescriptor.diagnosticClrTypeName}
                          </Typography.Paragraph>
                        </div>
                      </div>

                      <Input
                        aria-label={t("pages.gagents.index.binding.display.name", "Binding display name")}
                        onChange={(event) => {
                          setBindingDraft((current) => ({
                            ...current,
                            displayName: event.target.value,
                          }));
                          setPublishAcknowledged(false);
                          setBindingNotice(null);
                        }}
                        placeholder={t("pages.gagents.index.published.display.name", "Published display name")}
                        value={bindingDraft.displayName}
                      />

                      <Select
                        aria-label={t("pages.gagents.index.binding.actor.reuse.mode", "Binding actor reuse mode")}
                        onChange={(value) => {
                          setBindingActorReuseMode(value);
                          if (value === 'new') {
                            setBindingDraft((current) => ({
                              ...current,
                              preferredActorId: '',
                            }));
                          }
                          setPublishAcknowledged(false);
                          setBindingNotice(null);
                        }}
                        options={[
                          { label: t("pages.gagents.index.allocate.actor.on.activation", "Allocate actor on activation"), value: 'new' },
                          { label: t("pages.gagents.index.reuse.stable.actor.id", "Reuse a stable actor id"), value: 'existing' },
                        ]}
                        value={bindingActorReuseMode}
                      />

                      {bindingActorReuseMode === 'existing' ? (
                        <Space
                          orientation="vertical"
                          size={12}
                          style={{ width: '100%' }}
                        >
                          <Select
                            allowClear
                            aria-label={t("pages.gagents.index.published.actor.id", "Published actor id")}
                            onChange={(value) => {
                              setBindingDraft((current) => ({
                                ...current,
                                preferredActorId: value ?? '',
                              }));
                              setPublishAcknowledged(false);
                              setBindingNotice(null);
                            }}
                            optionFilterProp="label"
                            options={savedActorIds.map((actorId) => ({
                              value: actorId,
                              label: actorId,
                            }))}
                            placeholder={
                              gAgentActorsQuery.isLoading
                                ? 'Loading saved actors'
                                : 'Reuse a saved actor (optional)'
                            }
                            showSearch
                            style={{ width: '100%' }}
                            value={bindingSavedActorId}
                          />
                          <Input
                            aria-label={t("pages.gagents.index.binding.preferred.actor.id", "Binding preferred actor id")}
                            onChange={(event) => {
                              setBindingDraft((current) => ({
                                ...current,
                                preferredActorId: event.target.value,
                              }));
                              setPublishAcknowledged(false);
                              setBindingNotice(null);
                            }}
                            placeholder={t("pages.gagents.index.preferred.actor.id", "Preferred actor id")}
                            value={bindingDraft.preferredActorId}
                          />
                        </Space>
                      ) : (
                        <Typography.Text type="secondary">
                          {t("pages.gagents.index.activation.allocates.the.serving.actor", "Activation allocates the serving actor when this revision starts receiving traffic.")}</Typography.Text>
                      )}
                    </Space>
                  ),
                },
                {
                  key: 'endpoints',
                  label: t('pages.gagents.index.endpoints.count', 'Endpoints ({count})', { count: bindingDraft.endpoints.length }),
                  children: (
                    <Space orientation="vertical" size={16} style={{ width: '100%' }}>
                      <div
                        style={{
                          ...infoCardStyle,
                          display: 'flex',
                          flexDirection: 'column',
                          gap: 12,
                        }}
                      >
                        <Space
                          align="center"
                          style={{ justifyContent: 'space-between', width: '100%' }}
                        >
                          <div style={{ minWidth: 0 }}>
                            <Typography.Text strong>
                              {t("pages.gagents.index.published.endpoints", "Published endpoints")}</Typography.Text>
                            <Typography.Paragraph
                              style={{ marginBottom: 0 }}
                              type="secondary"
                            >
                              {t("pages.gagents.index.endpoint.id.kind.and.payload", "Endpoint id, kind, and payload contract define how Runs invoke the published service.")}</Typography.Paragraph>
                          </div>
                          <Button
                            onClick={addBindingEndpointDraft}
                            size="small"
                            type="default"
                          >
                            {t("pages.gagents.index.add.endpoint", "Add endpoint")}</Button>
                        </Space>

                        <Space orientation="vertical" size={12} style={{ width: '100%' }}>
                          {bindingDraft.endpoints.map((endpoint, index) => (
                            <div
                              key={endpoint.endpointId}
                              style={{
                                ...summaryMetricStyle,
                                display: 'flex',
                                flexDirection: 'column',
                                gap: 10,
                              }}
                            >
                              <Space size={[8, 8]} wrap>
                                <Typography.Text strong>
                                  {t("pages.gagents.index.endpoint", "Endpoint")}{index + 1}
                                </Typography.Text>
                                <Tag>{endpoint.kind}</Tag>
                              </Space>
                              <div
                                style={{
                                  display: 'grid',
                                  gap: 12,
                                  gridTemplateColumns:
                                    'repeat(auto-fit, minmax(180px, 1fr))',
                                }}
                              >
                                <Input
                                  aria-label={`Binding endpoint id ${index + 1}`}
                                  onChange={(event) =>
                                    updateBindingEndpointDraft(index, {
                                      endpointId: event.target.value,
                                    })
                                  }
                                  placeholder={t("pages.gagents.index.endpoint.id", "endpoint id")}
                                  value={endpoint.endpointId}
                                />
                                <Input
                                  aria-label={`Binding endpoint display name ${index + 1}`}
                                  onChange={(event) =>
                                    updateBindingEndpointDraft(index, {
                                      displayName: event.target.value,
                                    })
                                  }
                                  placeholder={t("pages.gagents.index.display.name.2", "Display name")}
                                  value={endpoint.displayName}
                                />
                                <Select
                                  aria-label={`Binding endpoint kind ${index + 1}`}
                                  onChange={(value) =>
                                    updateBindingEndpointDraft(index, {
                                      kind: value,
                                    })
                                  }
                                  options={[
                                    { label: 'command', value: 'command' },
                                    { label: 'chat', value: 'chat' },
                                  ]}
                                  value={endpoint.kind}
                                />
                                <Input
                                  aria-label={`Binding endpoint request kind ${index + 1}`}
                                  onChange={(event) =>
                                    updateBindingEndpointDraft(index, {
                                      requestTypeUrl: event.target.value,
                                    })
                                  }
                                  placeholder={t("pages.gagents.index.request.kind.url", "request kind url")}
                                  value={endpoint.requestTypeUrl}
                                />
                                <Input
                                  aria-label={`Binding endpoint response kind ${index + 1}`}
                                  onChange={(event) =>
                                    updateBindingEndpointDraft(index, {
                                      responseTypeUrl: event.target.value,
                                    })
                                  }
                                  placeholder={t("pages.gagents.index.response.kind.url", "response kind url")}
                                  value={endpoint.responseTypeUrl}
                                />
                              </div>
                              <Input.TextArea
                                aria-label={`Binding endpoint description ${index + 1}`}
                                autoSize={{ minRows: 2, maxRows: 4 }}
                                onChange={(event) =>
                                  updateBindingEndpointDraft(index, {
                                    description: event.target.value,
                                  })
                                }
                                placeholder={t("pages.gagents.index.describe.when.users.should.invoke", "Describe when users should invoke this endpoint")}
                                value={endpoint.description}
                              />
                              <Space size={[8, 8]} wrap>
                                <Button
                                  danger
                                  disabled={bindingDraft.endpoints.length <= 1}
                                  onClick={() => removeBindingEndpointDraft(index)}
                                  size="small"
                                >
                                  {t("pages.gagents.index.remove.endpoint", "Remove endpoint")}</Button>
                                <Typography.Text type="secondary">
                                  {endpoint.kind === 'chat'
                                    ? t("pages.gagents.index.chat.endpoints.open.runs", "Chat endpoints can open Runs with a prompt.")
                                    : t("pages.gagents.index.command.endpoints.open.runs", "Command endpoints can open Runs with a typed payload contract.")}
                                </Typography.Text>
                              </Space>
                            </div>
                          ))}
                        </Space>
                      </div>

                      {launchableBindingEndpoints.length > 0 ? (
                        <Select
                          aria-label={t("pages.gagents.index.open.runs.endpoint", "Open Runs endpoint")}
                          onChange={(value) =>
                            setBindingDraft((current) => ({
                              ...current,
                              openRunsEndpointId: value,
                            }))
                          }
                          options={launchableBindingEndpoints.map((endpoint) => ({
                            label: getUserFacingIdentifierLabel(
                              endpoint.displayName || endpoint.endpointId,
                              t("pages.gagents.index.endpoint", "Endpoint"),
                            ),
                            value: endpoint.endpointId,
                          }))}
                          value={
                            selectedLaunchEndpoint?.endpointId ||
                            bindingDraft.openRunsEndpointId
                          }
                        />
                      ) : null}
                    </Space>
                  ),
                },
              ]}
              onChange={setActivePublishTab}
            />

            <Alert
              showIcon
              type={bindingQuery.data?.available ? 'warning' : 'info'}
              title={bindingImpactMessage}
              description={t("pages.gagents.index.kind.template.binding.published.default", "Kind = template. Binding = published default service. Actor = runtime instance created by activation or invocation.")}
            />

            <Checkbox
              checked={publishAcknowledged}
              onChange={(event) => setPublishAcknowledged(event.target.checked)}
            >
              {t("pages.gagents.index.understand.this.changes.the.workspace", "I understand this changes the workspace's published default service.")}</Checkbox>

            <Space size={[8, 8]} wrap>
              <Button
                disabled={!selectedKindDescriptor || !normalizedScopeId}
                loading={bindingPendingKey === 'publish'}
                onClick={() => void handlePublishBinding()}
                type="primary"
              >
                {t("pages.gagents.index.publish.binding", "Publish binding")}</Button>
              <Button
                disabled={
                  !selectedKindDescriptor || !normalizedScopeId || !selectedLaunchEndpoint
                }
                loading={bindingPendingKey === 'publish:runs'}
                onClick={() => void handlePublishBinding({ openRuns: true })}
              >
                {t("pages.gagents.index.publish.and.open.runs", "Publish and open Runs")}</Button>
            </Space>
          </>
        )}
      </Space>
    </WorkbenchCard>
  );

  const bindingRevisionsPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.activate.or.retire.published.revisions", "Activate or retire published revisions.")}
      eyebrow="Binding Revisions"
      extra={
        <Button
          disabled={!selectedRevision}
          onClick={() => setIsRevisionDrawerOpen(true)}
          size="small"
        >
          {t("pages.gagents.index.revision.details.2", "Revision details")}</Button>
      }
      title={
        bindingQuery.data?.available
          ? `${bindingQuery.data.revisions.length} revision${bindingQuery.data.revisions.length === 1 ? '' : 's'}`
          : 'No revisions yet'
      }
    >
      <Space orientation="vertical" size={12} style={{ width: '100%' }}>
        {bindingQuery.error ? (
          <Alert
            showIcon
            type="error"
            title={describeError(bindingQuery.error)}
          />
        ) : bindingQuery.isLoading ? (
          <AevatarInspectorEmpty description={t("pages.gagents.index.loading.binding.revisions", "Loading binding revisions.")} />
        ) : !bindingQuery.data?.available ||
          bindingQuery.data.revisions.length === 0 ? (
          <AevatarInspectorEmpty description={t("pages.gagents.index.publish.the.selected.gagent.to", "Publish the selected GAgent to create the first revision.")} />
        ) : (
          <div style={compactListStyle}>
            {bindingQuery.data.revisions.map((revision) => {
              const canActivate =
                !revision.isDefaultServing &&
                !revision.isActiveServing &&
                !revision.retiredAt;
              const canRetire =
                !revision.retiredAt &&
                revision.revisionId !==
                  bindingQuery.data?.defaultServingRevisionId;

              return (
                <div
                  key={revision.revisionId}
                  onClick={() => {
                    setSelectedRevisionId(revision.revisionId);
                    setIsRevisionDrawerOpen(true);
                  }}
                  style={{
                    border:
                      selectedRevision?.revisionId === revision.revisionId
                        ? '1px solid rgba(22, 119, 255, 0.45)'
                        : '1px solid rgba(15, 23, 42, 0.08)',
                    borderRadius: 12,
                    cursor: 'pointer',
                    display: 'flex',
                    gap: 16,
                    justifyContent: 'space-between',
                    padding: 14,
                  }}
                >
                  <div
                    style={{
                      display: 'flex',
                      flex: 1,
                      flexDirection: 'column',
                      gap: 6,
                      minWidth: 0,
                    }}
                  >
                    <Space size={[8, 8]} wrap>
                      <Tag color={getBindingTone(revision)}>
                        {formatRuntimeGAgentBindingImplementationKind(
                          revision.implementationKind,
                        )}
                      </Tag>
                      <Tag color={getRevisionStatusTone(revision)}>
                        {revision.status || 'unknown'}
                      </Tag>
                      {revision.isDefaultServing ? (
                        <Tag color="success">{t("pages.gagents.index.default", "default")}</Tag>
                      ) : null}
                      {revision.isActiveServing ? (
                        <Tag color="processing">{t("pages.gagents.index.active", "active")}</Tag>
                      ) : null}
                      {revision.retiredAt ? <Tag>{t("pages.gagents.index.retired", "retired")}</Tag> : null}
                    </Space>
                    <Typography.Text
                      style={{
                        overflowWrap: 'anywhere',
                        wordBreak: 'break-word',
                      }}
                    >
                      {describeRuntimeGAgentBindingRevisionTarget(revision)}
                    </Typography.Text>
                    <Typography.Text
                      style={{
                        overflowWrap: 'anywhere',
                        wordBreak: 'break-word',
                      }}
                      type="secondary"
                    >
                      {revision.deploymentId
                        ? t("pages.gagents.index.deployment.ready", "Deployment ready")
                        : t("pages.gagents.index.draft.deployment", "Draft deployment")}{' '}
                      {revision.primaryActorId || revision.staticPreferredActorId
                        ? t("pages.gagents.index.actor.ready", "· Actor ready")
                        : t("pages.gagents.index.actor.not.assigned", "· Actor not assigned")}{' '}
                      {t("pages.gagents.index.updated", "· Updated")}{' '}
                      {formatTimestamp(
                        revision.publishedAt ||
                          revision.preparedAt ||
                          revision.createdAt,
                      )}
                    </Typography.Text>
                    {revision.failureReason ? (
                      <Typography.Text type="danger">
                        {revision.failureReason}
                      </Typography.Text>
                    ) : null}
                  </div>

                  <Space orientation="vertical" size={8}>
                    <Button
                      disabled={!canActivate}
                      loading={
                        bindingPendingKey === `activate:${revision.revisionId}`
                      }
                      onClick={(event) => {
                        event.stopPropagation();
                        void handleActivateRevision(revision.revisionId);
                      }}
                      type={canActivate ? 'primary' : 'default'}
                    >
                      {revision.isDefaultServing
                        ? t("pages.gagents.index.serving", "Serving")
                        : t("pages.gagents.index.activate", "Activate")}
                    </Button>
                    <Button
                      danger
                      disabled={!canRetire}
                      loading={
                        bindingPendingKey === `retire:${revision.revisionId}`
                      }
                      onClick={(event) => {
                        event.stopPropagation();
                        void handleRetireRevision(revision.revisionId);
                      }}
                    >
                      {t("pages.gagents.index.retire", "Retire")}</Button>
                  </Space>
                </div>
              );
            })}
          </div>
        )}
      </Space>
    </WorkbenchCard>
  );

  const draftRunPanel = (
    <WorkbenchCard
      description={t("pages.gagents.index.test.the.selected.gagent.kind", "Test the selected GAgent kind before publishing.")}
      eyebrow="Draft Run"
      extra={
        <Space size={[8, 8]} wrap>
          <AevatarStatusTag domain="run" status={runState.status} />
        </Space>
      }
      title={selectedKindDescriptor ? selectedKindDescriptor.displayName : 'GAgent Draft Run'}
    >
      <Space orientation="vertical" size={16} style={{ width: '100%' }}>
        {selectedKindDescriptor ? (
          <div
            style={{
              display: 'grid',
              gap: 12,
              gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
            }}
          >
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.kind.3", "Kind")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {selectedKindDescriptor.agentKind}
              </Typography.Paragraph>
            </div>
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.saved.actors.2", "Saved actors")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {savedActorIds.length}
              </Typography.Paragraph>
            </div>
            <div>
              <Typography.Text type="secondary">{t("pages.gagents.index.observed.actor", "Observed actor")}</Typography.Text>
              <Typography.Paragraph style={wrappedTextStyle}>
                {runState.actorId ? t("pages.gagents.index.available", "Available") : 'n/a'}
              </Typography.Paragraph>
            </div>
          </div>
        ) : (
          <AevatarInspectorEmpty description={t("pages.gagents.index.select.discovered.gagent.kind.before", "Select a discovered GAgent kind before drafting a direct run.")} />
        )}

        <Select
          aria-label={t("pages.gagents.index.actor.reuse.mode", "Actor reuse mode")}
          onChange={(value) => setActorReuseMode(value)}
          options={[
            { label: t("pages.gagents.index.create.new.actor", "Create new actor"), value: 'new' },
            { label: t("pages.gagents.index.reuse.existing.actor", "Reuse existing actor"), value: 'existing' },
          ]}
          value={actorReuseMode}
        />

        {actorReuseMode === 'existing' ? (
          <Space orientation="vertical" size={12} style={{ width: '100%' }}>
            <Select
              allowClear
              aria-label={t("pages.gagents.index.saved.actor.id", "Saved actor id")}
              onChange={(value) => setPreferredActorId(value ?? '')}
              optionFilterProp="label"
              options={savedActorIds.map((actorId) => ({
                value: actorId,
                label: actorId,
              }))}
              placeholder={
                gAgentActorsQuery.isLoading
                  ? 'Loading saved actors'
                  : 'Reuse a saved actor (optional)'
              }
              showSearch
              style={{ width: '100%' }}
              value={selectedSavedActorId}
            />
            <Input
              aria-label={t("pages.gagents.index.preferred.actor.id.2", "Preferred actor id")}
              onChange={(event) => setPreferredActorId(event.target.value)}
              placeholder={t("pages.gagents.index.preferred.actor.id.3", "Preferred actor id")}
              value={preferredActorId}
            />
            {gAgentActorsQuery.error ? (
              <Alert
                showIcon
                type="warning"
                title={describeError(gAgentActorsQuery.error)}
              />
            ) : (
              <Typography.Text type="secondary">
                {t("pages.gagents.index.leave.the.saved.actor.selector", "Leave the saved actor selector empty if you want to type a known actor id manually.")}</Typography.Text>
            )}
          </Space>
        ) : (
          <Typography.Text type="secondary">
            {t("pages.gagents.index.new.actor.mode.lets.the", "New actor mode lets the runtime allocate a fresh actor and persist it for future reuse.")}</Typography.Text>
        )}

        <Input.TextArea
          aria-label={t("pages.gagents.index.draft.prompt", "Draft prompt")}
          autoSize={{ minRows: 4, maxRows: 8 }}
          onChange={(event) => setPrompt(event.target.value)}
          placeholder={t("pages.gagents.index.enter.direct.prompt.for.the", "Enter a direct prompt for the selected GAgent kind")}
          value={prompt}
        />

        <Space size={[8, 8]} wrap>
          <Button
            icon={<PlayCircleOutlined />}
            loading={runState.status === 'running'}
            onClick={() => void handleRun()}
            type="primary"
          >
            {t("pages.gagents.index.run.draft.prompt", "Run draft prompt")}</Button>
          <Button
            danger
            disabled={runState.status !== 'running'}
            icon={<StopOutlined />}
            onClick={handleStop}
          >
            {t("pages.gagents.index.stop.run", "Stop run")}</Button>
          <Button
            disabled={runState.events.length === 0}
            icon={<EyeOutlined />}
            onClick={handleOpenRuns}
          >
            {t("pages.gagents.index.continue.in.runs", "Continue in Runs")}</Button>
        </Space>

        {runState.error ? (
          <Alert showIcon type="error" title={runState.error} />
        ) : null}
      </Space>
    </WorkbenchCard>
  );

  const workbenchTabs = [
    {
      key: 'draft',
      label: t("pages.gagents.index.draft.run", "Draft Run"),
      children: (
        <div style={workbenchColumnStyle}>
          {draftRunPanel}
          {runOutputPanel}
        </div>
      ),
    },
    {
      key: 'publish',
      label: t("pages.gagents.index.publish", "Publish"),
      children: (
        <div style={workbenchColumnStyle}>
          {publishBindingPanel}
        </div>
      ),
    },
    {
      key: 'serving',
      label: t("pages.gagents.index.serving", "Serving"),
      children: (
        <div style={workbenchColumnStyle}>
          {currentBindingPanel}
          {bindingRevisionsPanel}
        </div>
      ),
    },
  ];

  const stage = (
    <div
      style={{
        ...workbenchColumnStyle,
        background: CLI_PAGE_BACKGROUND,
        borderRadius: 24,
        padding: screens.md ? 24 : 16,
      }}
    >
      {selectedTypePanel}
      <ConsoleOperationNotice
        errorMessage={t(
          'pages.gagents.index.bindingActionFailed',
          'Binding action could not be completed. Try again.',
        )}
        notice={bindingNotice}
        onClose={() => setBindingNotice(null)}
      />
      <Tabs
        activeKey={activeWorkbenchTab}
        items={workbenchTabs}
        onChange={setActiveWorkbenchTab}
      />
    </div>
  );
  const breadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      title: t('pages.gagents.index.platformBreadcrumb', 'Platform'),
    },
    {
      current: true,
      title: t('pages.gagents.index.teamMembersBreadcrumb', 'Team members'),
    },
  ];

  return (
    <AevatarPageShell
      breadcrumbItems={breadcrumbItems}
      layoutMode="document"
      extra={
        <Space size={[8, 8]} wrap>
          <Typography.Text type="secondary">{t("pages.gagents.index.workspace.id.3", "Workspace ID")}</Typography.Text>
          <Typography.Text style={{ maxWidth: 320 }} strong>
            {normalizedScopeId ||
              resolvedScope?.scopeId ||
              t("pages.gagents.index.not.resolved", "Not resolved")}
          </Typography.Text>
        </Space>
      }
      title={t("pages.gagents.index.team.member", "team member")}
      titleHelp={t("pages.gagents.index.the.original.gagent.runtime", "The original GAgent runtime capabilities are retained here, but are unified and externally described as a team member management and binding workbench.")}
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={rail}
        railWidth={320}
        stage={stage}
      />
      <AevatarContextDrawer
        onClose={() => setIsActorRegistryDrawerOpen(false)}
        open={isActorRegistryDrawerOpen}
        title={t("pages.gagents.index.member.registration.form", "Member registration form")}
        width={screens.xl ? 680 : 520}
      >
        {actorRegistryPanel}
      </AevatarContextDrawer>
      <AevatarContextDrawer
        onClose={() => setIsRevisionDrawerOpen(false)}
        open={isRevisionDrawerOpen}
        subtitle={
          selectedRevision
            ? describeRuntimeGAgentBindingRevisionTarget(selectedRevision)
            : undefined
        }
        title={t("pages.gagents.index.version.details", "Version details")}
        width={screens.xl ? 620 : 480}
      >
        {selectedRevisionPanel}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

export default GAgentsPage;
