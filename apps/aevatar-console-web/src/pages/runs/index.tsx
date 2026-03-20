import {
  connectChatWebSocket,
  parseSSEStream,
  type RunStatus,
  useHumanInteraction,
  useRunSession,
} from '@aevatar-react-sdk/agui';
import {
  AGUIEventType,
  type ChatRunRequest,
  type ChatWsAckPayload,
  CustomEventName,
  type WorkflowResumeRequest,
  type WorkflowSignalRequest,
} from '@aevatar-react-sdk/types';
import type {
  ProColumns,
  ProDescriptionsItemProps,
  ProFormInstance,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProForm,
  ProFormSelect,
  ProFormSwitch,
  ProFormText,
  ProFormTextArea,
  ProList,
  ProTable,
} from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { history } from '@umijs/max';
import {
  Alert,
  Badge,
  Button,
  Col,
  Divider,
  Drawer,
  Empty,
  message,
  Row,
  Space,
  Statistic,
  Tabs,
  Tag,
  Typography,
} from 'antd';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  getLatestCustomEventData,
  parseStepRequestData,
  parseWaitingSignalData,
} from '@/shared/agui/customEventData';
import { consoleApi } from '@/shared/api/consoleApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { loadConsolePreferences } from '@/shared/preferences/consolePreferences';
import {
  clearRecentRuns,
  loadRecentRuns,
  type RecentRunEntry,
  saveRecentRun,
} from '@/shared/runs/recentRuns';
import {
  buildWorkflowCatalogOptions,
  findWorkflowCatalogItem,
  listVisibleWorkflowCatalogItems,
} from '@/shared/workflows/catalogVisibility';
import {
  cardStackStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  moduleCardProps,
  scrollPanelStyle,
  stretchColumnStyle,
} from '@/shared/ui/proComponents';
import {
  buildEventRows,
  isHumanApprovalSuspension,
  type RunEventRow,
  type RunTransport,
} from './runEventPresentation';

type RunFormValues = {
  prompt: string;
  workflow?: string;
  actorId?: string;
  transport: RunTransport;
};

type ResumeFormValues = {
  approved: boolean;
  userInput?: string;
};

type SignalFormValues = {
  payload?: string;
};

type RunPreset = {
  key: string;
  title: string;
  workflow: string;
  prompt: string;
  description: string;
  tags: string[];
};

type RunStatusValue = RunStatus | 'unknown';
type RunFocusStatus =
  | 'idle'
  | 'running'
  | 'human_input'
  | 'human_approval'
  | 'wait_signal'
  | 'finished'
  | 'error';

type RecentRunRow = RecentRunEntry & {
  key: string;
  statusValue: RunStatusValue;
};

type RecentRunTableRow = RecentRunRow & {
  onRestore?: () => void;
  onOpenActor?: () => void;
};

type RunSummaryRecord = {
  status: RunStatus;
  transport: RunTransport;
  workflowName: string;
  actorId: string;
  commandId: string;
  runId: string;
  focusStatus: RunFocusStatus;
  focusLabel: string;
  lastEventAt: string;
  messageCount: number;
  eventCount: number;
  activeSteps: string[];
};

type SelectedWorkflowRecord = {
  workflowName: string;
  groupLabel: string;
  sourceLabel: string;
  llmStatus: 'processing' | 'success';
  description: string;
};

type WaitingSignalRecord = {
  signalName: string;
  stepId: string;
  runId: string;
  prompt: string;
};

type HumanInputRecord = {
  stepId: string;
  runId: string;
  suspensionType: string;
  prompt: string;
  timeoutSeconds: number;
};

type ConsoleViewKey = 'dual' | 'messages' | 'events';

const composerRailMinWidth = 320;
const composerRailDefaultWidth = 360;
const composerRailMaxWidth = 560;
const composerRailKeyboardStep = 24;
const monitorWorkbenchMinWidth = 520;

const builtInPresets: RunPreset[] = [
  {
    key: 'direct',
    title: 'Direct chat',
    workflow: 'direct',
    prompt:
      'Summarize what this workflow can do and produce a concise execution result.',
    description:
      'Baseline direct workflow for quick validation of the chat stream.',
    tags: ['baseline', 'llm'],
  },
  {
    key: 'human-input',
    title: 'Human input triage',
    workflow: 'human_input_manual_triage',
    prompt:
      'A production incident needs manual classification before the workflow can continue.',
    description: 'Use this to verify human input prompts and resume flow.',
    tags: ['human_input', 'resume'],
  },
  {
    key: 'human-approval',
    title: 'Human approval gate',
    workflow: 'human_approval_release_gate',
    prompt:
      'Prepare a release summary that requires explicit human approval before rollout.',
    description: 'Use this to verify approval flow and moderation checkpoints.',
    tags: ['human_approval', 'approval'],
  },
  {
    key: 'wait-signal',
    title: 'Wait signal',
    workflow: 'wait_signal_manual_success',
    prompt: 'Wait for an external readiness signal before completing the run.',
    description:
      'Use this to verify waiting_signal and manual signal delivery.',
    tags: ['wait_signal', 'signal'],
  },
];

const runStatusValueEnum = {
  idle: { text: 'Idle', status: 'Default' },
  running: { text: 'Running', status: 'Processing' },
  finished: { text: 'Finished', status: 'Success' },
  error: { text: 'Error', status: 'Error' },
  unknown: { text: 'Unknown', status: 'Default' },
} as const;

const transportValueEnum = {
  sse: { text: 'SSE', status: 'Processing' },
  ws: { text: 'WebSocket', status: 'Success' },
} as const;

const runFocusValueEnum = {
  idle: { text: 'Idle', status: 'Default' },
  running: { text: 'Running', status: 'Processing' },
  human_input: { text: 'Human input', status: 'Warning' },
  human_approval: { text: 'Approval', status: 'Warning' },
  wait_signal: { text: 'Wait signal', status: 'Warning' },
  finished: { text: 'Finished', status: 'Success' },
  error: { text: 'Error', status: 'Error' },
} as const;

const runSummaryColumns: ProDescriptionsItemProps<RunSummaryRecord>[] = [
  {
    title: 'Transport',
    dataIndex: 'transport',
    valueType: 'status' as any,
    valueEnum: transportValueEnum,
  },
  {
    title: 'Workflow',
    dataIndex: 'workflowName',
    render: (_, record) => record.workflowName || 'n/a',
  },
  {
    title: 'Actor',
    dataIndex: 'actorId',
    render: (_, record) =>
      record.actorId ? (
        <Typography.Text copyable>{record.actorId}</Typography.Text>
      ) : (
        'n/a'
      ),
  },
  {
    title: 'Command',
    dataIndex: 'commandId',
    render: (_, record) =>
      record.commandId ? (
        <Typography.Text copyable>{record.commandId}</Typography.Text>
      ) : (
        'n/a'
      ),
  },
  {
    title: 'RunId',
    dataIndex: 'runId',
    render: (_, record) =>
      record.runId ? (
        <Typography.Text copyable>{record.runId}</Typography.Text>
      ) : (
        'n/a'
      ),
  },
  {
    title: 'Current focus',
    dataIndex: 'focusStatus',
    valueType: 'status' as any,
    valueEnum: runFocusValueEnum,
    render: (_, record) => <Tag color="processing">{record.focusLabel}</Tag>,
  },
  {
    title: 'Last event',
    dataIndex: 'lastEventAt',
    valueType: 'dateTime',
    render: (_, record) => record.lastEventAt || 'n/a',
  },
  {
    title: 'Active steps',
    dataIndex: 'activeSteps',
    render: (_, record) =>
      record.activeSteps.length > 0 ? (
        <Space wrap size={[4, 4]}>
          {record.activeSteps.map((step) => (
            <Tag key={step} color="processing">
              {step}
            </Tag>
          ))}
        </Space>
      ) : (
        <Tag>None</Tag>
      ),
  },
];

const humanInputColumns: ProDescriptionsItemProps<HumanInputRecord>[] = [
  {
    title: 'Step',
    dataIndex: 'stepId',
    render: (_, record) => record.stepId || 'n/a',
  },
  {
    title: 'Run',
    dataIndex: 'runId',
    render: (_, record) => record.runId || 'n/a',
  },
  {
    title: 'Suspension',
    dataIndex: 'suspensionType',
    render: (_, record) => record.suspensionType || 'n/a',
  },
  {
    title: 'Timeout',
    dataIndex: 'timeoutSeconds',
    valueType: 'digit',
  },
  {
    title: 'Prompt',
    dataIndex: 'prompt',
    render: (_, record) => record.prompt || 'n/a',
  },
];

const workflowDescriptionColumns: ProDescriptionsItemProps<SelectedWorkflowRecord>[] =
  [
    {
      title: 'Workflow',
      dataIndex: 'workflowName',
      render: (_, record) => (
        <Tag color="processing">{record.workflowName}</Tag>
      ),
    },
    {
      title: 'Group',
      dataIndex: 'groupLabel',
    },
    {
      title: 'Source',
      dataIndex: 'sourceLabel',
    },
    {
      title: 'LLM',
      dataIndex: 'llmStatus',
      valueType: 'status' as any,
      valueEnum: {
        processing: { text: 'Required', status: 'Processing' },
        success: { text: 'Optional', status: 'Success' },
      },
    },
    {
      title: 'Description',
      dataIndex: 'description',
    },
  ];

const waitingSignalColumns: ProDescriptionsItemProps<WaitingSignalRecord>[] = [
  {
    title: 'Signal name',
    dataIndex: 'signalName',
  },
  {
    title: 'Step',
    dataIndex: 'stepId',
    render: (_, record) => record.stepId || 'n/a',
  },
  {
    title: 'Run',
    dataIndex: 'runId',
    render: (_, record) => record.runId || 'n/a',
  },
  {
    title: 'Prompt',
    dataIndex: 'prompt',
    render: (_, record) => record.prompt || 'n/a',
  },
];

const runsWorkbenchShellStyle = {
  background:
    'linear-gradient(180deg, rgba(15, 23, 42, 0.03) 0%, rgba(15, 23, 42, 0.01) 100%)',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  height: 'calc(100vh - 64px)',
  overflow: 'hidden',
  padding: 12,
  position: 'relative',
} as const;

const runsWorkbenchHeaderStyle = {
  alignItems: 'center',
  backdropFilter: 'blur(8px)',
  background: 'var(--ant-color-bg-container)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 14,
  display: 'flex',
  flex: '0 0 auto',
  justifyContent: 'space-between',
  minHeight: 52,
  padding: '0 16px',
  position: 'sticky',
  top: 0,
  zIndex: 6,
} as const;

const runsWorkbenchMainStyle = {
  display: 'flex',
  flex: 1,
  minHeight: 0,
  overflow: 'hidden',
} as const;

const runsWorkbenchComposerRailStyle = {
  display: 'flex',
  minWidth: 0,
  overflow: 'hidden',
} as const;

const runsWorkbenchResizeRailStyle = {
  alignItems: 'stretch',
  background: 'transparent',
  border: 'none',
  cursor: 'col-resize',
  display: 'flex',
  flex: '0 0 20px',
  justifyContent: 'center',
  outline: 'none',
  padding: '0 6px',
  userSelect: 'none',
} as const;

const runsWorkbenchResizeHandleStyle = {
  background: 'var(--ant-color-border-secondary)',
  borderRadius: 999,
  transition: 'background-color 0.2s ease, transform 0.2s ease',
  width: 4,
} as const;

const runsWorkbenchMonitorStyle = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 12,
  minWidth: 0,
  overflow: 'hidden',
} as const;

const workbenchCardStyle = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
} as const;

const workbenchCardBodyStyle = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
  padding: 12,
} as const;

const workbenchScrollableBodyStyle = {
  flex: 1,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingRight: 4,
} as const;

const workbenchHudCardStyle = {
  ...workbenchCardStyle,
  flex: '0 0 auto',
} as const;

const workbenchHudBodyStyle = {
  ...workbenchCardBodyStyle,
  overflow: 'visible',
} as const;

const workbenchOverviewGridStyle = {
  flex: 1,
  minHeight: 0,
} as const;

const workbenchOverviewCardStyle = {
  ...workbenchCardStyle,
  minHeight: 0,
} as const;

const workbenchConsoleCardStyle = {
  ...workbenchCardStyle,
  flex: '0 0 calc((100vh - 64px) * 0.3)',
  minHeight: 260,
} as const;

const workbenchConsoleBodyStyle = {
  ...workbenchCardBodyStyle,
  overflow: 'hidden',
} as const;

const workbenchConsoleViewportStyle = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
} as const;

const workbenchConsoleTabPanelStyle = {
  display: 'flex',
  flexDirection: 'column',
  height: 'calc((100vh - 64px) * 0.3 - 120px)',
  minHeight: 180,
} as const;

const workbenchConsoleSurfaceStyle = {
  background:
    'linear-gradient(180deg, rgba(248, 250, 252, 0.96) 0%, rgba(255, 255, 255, 0.98) 100%)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 12,
  color: 'var(--ant-color-text)',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  fontFamily:
    "'Monaco', 'Consolas', 'SFMono-Regular', 'Liberation Mono', monospace",
  minHeight: 0,
  overflow: 'hidden',
} as const;

const workbenchConsoleScrollStyle = {
  flex: 1,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  padding: 12,
} as const;

const workbenchMessageListStyle = {
  display: 'flex',
  flexDirection: 'column',
  gap: 10,
} as const;

const workbenchEventHeaderStyle = {
  borderBottom: '1px solid var(--ant-color-border-secondary)',
  color: 'var(--ant-color-text-secondary)',
  display: 'grid',
  fontSize: 12,
  gap: 12,
  gridTemplateColumns: '220px 120px minmax(0, 1fr)',
  padding: '12px 12px 8px',
} as const;

const workbenchEventRowStyle = {
  borderBottom: '1px solid rgba(5, 5, 5, 0.06)',
  display: 'grid',
  gap: 12,
  gridTemplateColumns: '220px 120px minmax(0, 1fr)',
  padding: '10px 12px',
} as const;

const recentRunColumns: ProColumns<RecentRunTableRow>[] = [
  {
    title: 'Workflow',
    dataIndex: 'workflowName',
    ellipsis: true,
  },
  {
    title: 'Status',
    dataIndex: 'statusValue',
    width: 120,
    valueType: 'status' as any,
    valueEnum: runStatusValueEnum,
  },
  {
    title: 'Recorded',
    dataIndex: 'recordedAt',
    width: 220,
    valueType: 'dateTime',
    render: (_, record) => formatDateTime(record.recordedAt),
  },
  {
    title: 'RunId',
    dataIndex: 'runId',
    width: 180,
    render: (_, record) => record.runId || 'n/a',
  },
  {
    title: 'Preview',
    dataIndex: 'lastMessagePreview',
    ellipsis: true,
    render: (_, record) =>
      record.lastMessagePreview || record.prompt || 'No preview recorded.',
  },
  {
    title: 'Actions',
    valueType: 'option',
    width: 160,
    render: (_, record) => [
      <Space key={`${record.id}-actions`}>
        <Button type="link" onClick={() => record.onRestore?.()}>
          Restore
        </Button>
        {record.actorId ? (
          <Button type="link" onClick={() => record.onOpenActor?.()}>
            Actor
          </Button>
        ) : null}
      </Space>,
    ],
  },
];

function trimOptional(value?: string | null): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function formatElapsedDuration(totalMilliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  if (hours > 0) {
    return [hours, minutes, seconds]
      .map((value) => value.toString().padStart(2, '0'))
      .join(':');
  }

  return [minutes, seconds]
    .map((value) => value.toString().padStart(2, '0'))
    .join(':');
}

function clampComposerWidth(
  requestedWidth: number,
  containerWidth: number,
): number {
  const maxWidth = Math.max(
    composerRailMinWidth,
    Math.min(composerRailMaxWidth, containerWidth - monitorWorkbenchMinWidth),
  );

  return Math.min(Math.max(requestedWidth, composerRailMinWidth), maxWidth);
}

function readInitialRunFormValues(preferredWorkflow: string): RunFormValues {
  if (typeof window === 'undefined') {
    return {
      prompt: '',
      workflow: preferredWorkflow,
      actorId: undefined,
      transport: 'sse',
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    prompt: params.get('prompt') ?? '',
    workflow: trimOptional(params.get('workflow')) ?? preferredWorkflow,
    actorId: trimOptional(params.get('actorId')),
    transport: trimOptional(params.get('transport')) === 'ws' ? 'ws' : 'sse',
  };
}

const RunsPage: React.FC = () => {
  const preferences = useMemo(() => loadConsolePreferences(), []);
  const [messageApi, messageContextHolder] = message.useMessage();
  const initialFormValues = useMemo(
    () => readInitialRunFormValues(preferences.preferredWorkflow),
    [preferences.preferredWorkflow],
  );
  const composerFormRef = useRef<ProFormInstance<RunFormValues> | undefined>(
    undefined,
  );
  const runsWorkbenchMainRef = useRef<HTMLDivElement | null>(null);
  const resumeFormRef = useRef<ProFormInstance<ResumeFormValues> | undefined>(
    undefined,
  );
  const signalFormRef = useRef<ProFormInstance<SignalFormValues> | undefined>(
    undefined,
  );
  const [catalogSearch, setCatalogSearch] = useState('');
  const [selectedWorkflowName, setSelectedWorkflowName] = useState(
    initialFormValues.workflow ?? preferences.preferredWorkflow,
  );
  const [recentRuns, setRecentRuns] = useState<RecentRunEntry[]>(() =>
    loadRecentRuns(),
  );
  const [selectedTransport, setSelectedTransport] = useState<RunTransport>(
    initialFormValues.transport,
  );
  const [composerWidth, setComposerWidth] = useState(composerRailDefaultWidth);
  const [activeTransport, setActiveTransport] = useState<RunTransport>(
    initialFormValues.transport,
  );
  const [consoleView, setConsoleView] = useState<ConsoleViewKey>('dual');
  const [isInteractionDrawerOpen, setIsInteractionDrawerOpen] = useState(false);
  const [isComposerResizing, setIsComposerResizing] = useState(false);
  const [runStartedAtMs, setRunStartedAtMs] = useState<number | undefined>(
    undefined,
  );
  const [elapsedNow, setElapsedNow] = useState(() => Date.now());
  const [transportIssue, setTransportIssue] = useState<
    { code?: string; message: string } | undefined
  >(undefined);
  const [wsAck, setWsAck] = useState<ChatWsAckPayload | undefined>(undefined);
  const stopActiveRunRef = useRef<(() => void) | undefined>(undefined);

  const workflowCatalogQuery = useQuery({
    queryKey: ['workflow-catalog'],
    queryFn: () => consoleApi.listWorkflowCatalog(),
  });

  const { session, dispatch, reset } = useRunSession();

  const [streaming, setStreaming] = useState(false);

  const abortRun = useCallback(() => {
    stopActiveRunRef.current?.();
    stopActiveRunRef.current = undefined;
    setStreaming(false);
  }, []);

  const reportTransportError = useCallback(
    (messageText: string, code?: string) => {
      setTransportIssue({ message: messageText, code });
      dispatch({
        type: AGUIEventType.RUN_ERROR,
        message: messageText,
        code,
      });
      messageApi.error(code ? `${code}: ${messageText}` : messageText);
    },
    [dispatch, messageApi],
  );

  const sendRun = useCallback(
    async (request: ChatRunRequest, transport: RunTransport) => {
      abortRun();
      reset();
      setTransportIssue(undefined);
      setWsAck(undefined);
      setActiveTransport(transport);
      setRunStartedAtMs(Date.now());
      setStreaming(true);

      try {
        if (transport === 'ws') {
          const { events, close } = connectChatWebSocket(request, {
            onAck: (payload) => {
              setWsAck(payload);
            },
            onError: (code, messageText) => {
              reportTransportError(messageText, code);
            },
          });

          stopActiveRunRef.current = close;

          for await (const event of events) {
            dispatch(event);
          }
        } else {
          const controller = new AbortController();
          stopActiveRunRef.current = () => controller.abort();

          const response = await consoleApi.streamChat(
            request,
            controller.signal,
          );
          for await (const event of parseSSEStream(response, {
            signal: controller.signal,
          })) {
            if (controller.signal.aborted) {
              break;
            }

            dispatch(event);
          }
        }
      } catch (error) {
        if (error instanceof Error && error.name === 'AbortError') {
          return;
        }

        const text = error instanceof Error ? error.message : String(error);
        reportTransportError(text);
      } finally {
        stopActiveRunRef.current = undefined;
        setStreaming(false);
      }
    },
    [abortRun, dispatch, reportTransportError, reset],
  );

  const resizeComposerRail = useCallback((clientX: number) => {
    const containerRect = runsWorkbenchMainRef.current?.getBoundingClientRect();
    if (!containerRect) {
      return;
    }

    setComposerWidth(
      clampComposerWidth(clientX - containerRect.left, containerRect.width),
    );
  }, []);

  const setComposerWidthWithinBounds = useCallback((requestedWidth: number) => {
    const containerRect = runsWorkbenchMainRef.current?.getBoundingClientRect();
    if (!containerRect) {
      setComposerWidth(requestedWidth);
      return;
    }

    setComposerWidth(clampComposerWidth(requestedWidth, containerRect.width));
  }, []);

  const { resume, signal, resuming, signaling } = useHumanInteraction({
    resume: (request: WorkflowResumeRequest) => consoleApi.resume(request),
    signal: (request: WorkflowSignalRequest) => consoleApi.signal(request),
  });

  useEffect(() => () => abortRun(), [abortRun]);

  useEffect(() => {
    const syncComposerWidth = () => {
      const containerRect =
        runsWorkbenchMainRef.current?.getBoundingClientRect();
      if (!containerRect) {
        return;
      }

      setComposerWidth((currentWidth) =>
        clampComposerWidth(currentWidth, containerRect.width),
      );
    };

    syncComposerWidth();
    window.addEventListener('resize', syncComposerWidth);
    return () => {
      window.removeEventListener('resize', syncComposerWidth);
    };
  }, []);

  useEffect(() => {
    if (!isComposerResizing) {
      return undefined;
    }

    const handlePointerMove = (event: PointerEvent) => {
      resizeComposerRail(event.clientX);
    };
    const handlePointerUp = () => {
      setIsComposerResizing(false);
    };

    window.addEventListener('pointermove', handlePointerMove);
    window.addEventListener('pointerup', handlePointerUp);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';

    return () => {
      window.removeEventListener('pointermove', handlePointerMove);
      window.removeEventListener('pointerup', handlePointerUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
  }, [isComposerResizing, resizeComposerRail]);

  const workflowName =
    session.context?.workflowName ??
    wsAck?.workflow ??
    composerFormRef.current?.getFieldValue('workflow') ??
    '';
  const actorId = session.context?.actorId ?? wsAck?.actorId;
  const commandId = session.context?.commandId ?? wsAck?.commandId ?? '';

  const waitingSignal = useMemo(
    () =>
      getLatestCustomEventData(
        session.events,
        CustomEventName.WaitingSignal,
        parseWaitingSignalData,
      ),
    [session.events],
  );
  const latestStepRequest = useMemo(
    () =>
      getLatestCustomEventData(
        session.events,
        CustomEventName.StepRequest,
        parseStepRequestData,
      ),
    [session.events],
  );

  const actorSnapshotQuery = useQuery({
    queryKey: ['run-actor-snapshot', actorId],
    enabled: Boolean(actorId),
    queryFn: () => consoleApi.getActorSnapshot(actorId || ''),
    refetchInterval:
      actorId && (streaming || session.status === 'running') ? 2_000 : false,
  });

  const filteredCatalog = useMemo(() => {
    const keyword = catalogSearch.trim().toLowerCase();
    const items = listVisibleWorkflowCatalogItems(workflowCatalogQuery.data ?? []);
    if (!keyword) {
      return items;
    }

    return items.filter((item) =>
      [item.name, item.description, item.groupLabel, item.category]
        .join(' ')
        .toLowerCase()
        .includes(keyword),
    );
  }, [catalogSearch, workflowCatalogQuery.data]);

  const workflowOptions = useMemo(() => {
    const visibleNames = new Set(filteredCatalog.map((item) => item.name));
    return buildWorkflowCatalogOptions(
      workflowCatalogQuery.data ?? [],
      selectedWorkflowName,
    ).filter(
      (option) => option.value === selectedWorkflowName || visibleNames.has(option.value),
    );
  }, [filteredCatalog, selectedWorkflowName, workflowCatalogQuery.data]);

  const selectedWorkflowDetails = useMemo(
    () =>
      findWorkflowCatalogItem(workflowCatalogQuery.data ?? [], selectedWorkflowName),
    [selectedWorkflowName, workflowCatalogQuery.data],
  );

  const selectedWorkflowRecord = useMemo<
    SelectedWorkflowRecord | undefined
  >(() => {
    if (!selectedWorkflowDetails) {
      return undefined;
    }

    return {
      workflowName: selectedWorkflowDetails.name,
      groupLabel: selectedWorkflowDetails.groupLabel,
      sourceLabel: selectedWorkflowDetails.sourceLabel,
      llmStatus: selectedWorkflowDetails.requiresLlmProvider
        ? 'processing'
        : 'success',
      description: selectedWorkflowDetails.description,
    };
  }, [selectedWorkflowDetails]);

  const visiblePresets = useMemo(() => {
    const available = new Set(
      listVisibleWorkflowCatalogItems(workflowCatalogQuery.data ?? []).map(
        (item) => item.name,
      ),
    );
    return builtInPresets.filter((preset) => available.has(preset.workflow));
  }, [workflowCatalogQuery.data]);

  const latestMessagePreview = useMemo(() => {
    const lastWithContent = [...session.messages]
      .reverse()
      .find((item) => item.content?.trim());
    return lastWithContent?.content?.trim() ?? '';
  }, [session.messages]);

  const recentRunRows = useMemo<RecentRunTableRow[]>(
    () =>
      recentRuns.map((entry) => ({
        ...entry,
        key: entry.id,
        statusValue: ['idle', 'running', 'finished', 'error'].includes(
          entry.status,
        )
          ? (entry.status as RunStatus)
          : 'unknown',
        onRestore: () => {
          composerFormRef.current?.setFieldsValue({
            prompt: entry.prompt,
            workflow: entry.workflowName,
            actorId: entry.actorId || undefined,
            transport: selectedTransport,
          });
          setSelectedWorkflowName(entry.workflowName);
        },
        onOpenActor: entry.actorId
          ? () =>
              history.push(
                `/actors?actorId=${encodeURIComponent(entry.actorId)}`,
              )
          : undefined,
      })),
    [recentRuns, selectedTransport],
  );

  const eventRows = useMemo<RunEventRow[]>(
    () => buildEventRows(session.events),
    [session.events],
  );
  const waitingSignalRecord = useMemo<WaitingSignalRecord | undefined>(() => {
    if (!waitingSignal) {
      return undefined;
    }

    return {
      signalName: waitingSignal.signalName ?? '',
      stepId: waitingSignal.stepId ?? '',
      runId: waitingSignal.runId ?? '',
      prompt: waitingSignal.prompt ?? '',
    };
  }, [waitingSignal]);

  const humanInputRecord = useMemo<HumanInputRecord | undefined>(() => {
    if (!session.pendingHumanInput) {
      return undefined;
    }

    return {
      stepId:
        session.pendingHumanInput.stepId ?? latestStepRequest?.stepId ?? '',
      runId: session.pendingHumanInput.runId ?? session.runId ?? '',
      suspensionType:
        session.pendingHumanInput.suspensionType ??
        latestStepRequest?.stepType ??
        '',
      prompt: session.pendingHumanInput.prompt ?? '',
      timeoutSeconds: session.pendingHumanInput.timeoutSeconds ?? 0,
    };
  }, [
    latestStepRequest?.stepId,
    latestStepRequest?.stepType,
    session.pendingHumanInput,
    session.runId,
  ]);

  const runFocus = useMemo(() => {
    if (transportIssue || session.error || session.status === 'error') {
      return {
        status: 'error' as RunFocusStatus,
        label:
          transportIssue?.message || session.error?.message || 'Run failed',
        alertType: 'error' as const,
        title: transportIssue?.code ?? session.error?.code ?? 'Run error',
        description:
          transportIssue?.message ||
          session.error?.message ||
          'The run ended with an error.',
      };
    }

    if (humanInputRecord) {
      const approval = isHumanApprovalSuspension(
        humanInputRecord.suspensionType,
      );
      return {
        status: approval
          ? ('human_approval' as const)
          : ('human_input' as const),
        label: approval
          ? `Awaiting approval on ${humanInputRecord.stepId || 'current step'}`
          : `Awaiting human input on ${humanInputRecord.stepId || 'current step'}`,
        alertType: 'warning' as const,
        title: approval ? 'Approval required' : 'Human input required',
        description:
          humanInputRecord.prompt || 'Operator action is required to continue.',
      };
    }

    if (waitingSignalRecord) {
      return {
        status: 'wait_signal' as const,
        label: `Waiting for signal ${waitingSignalRecord.signalName || 'unknown'}`,
        alertType: 'warning' as const,
        title: 'Waiting for external signal',
        description:
          waitingSignalRecord.prompt ||
          'The workflow is paused until the expected signal arrives.',
      };
    }

    if (streaming || session.status === 'running') {
      return {
        status: 'running' as const,
        label: `Streaming over ${activeTransport.toUpperCase()}`,
        alertType: 'info' as const,
        title: 'Run in progress',
        description: 'Messages and events are still arriving from the backend.',
      };
    }

    if (session.status === 'finished') {
      return {
        status: 'finished' as const,
        label: 'Run completed',
        alertType: 'success' as const,
        title: 'Run finished',
        description: 'The backend reported a completed run.',
      };
    }

    return {
      status: 'idle' as const,
      label: 'Ready to start a run',
      alertType: 'info' as const,
      title: 'Idle',
      description: 'Compose a prompt and start a workflow run.',
    };
  }, [
    activeTransport,
    humanInputRecord,
    session.error,
    session.status,
    streaming,
    transportIssue,
    waitingSignalRecord,
  ]);

  const hasPendingInteraction = Boolean(
    humanInputRecord || waitingSignalRecord,
  );
  const runStatusText =
    runStatusValueEnum[session.status]?.text ?? session.status;
  const isRunLive =
    streaming ||
    session.status === 'running' ||
    hasPendingInteraction ||
    runFocus.status === 'wait_signal';

  useEffect(() => {
    if (hasPendingInteraction) {
      setIsInteractionDrawerOpen(true);
      return;
    }

    setIsInteractionDrawerOpen(false);
  }, [hasPendingInteraction]);

  useEffect(() => {
    if (!runStartedAtMs) {
      return undefined;
    }

    if (!isRunLive) {
      setElapsedNow(Date.now());
      return undefined;
    }

    const timer = window.setInterval(() => {
      setElapsedNow(Date.now());
    }, 1_000);

    return () => {
      window.clearInterval(timer);
    };
  }, [isRunLive, runStartedAtMs]);

  const elapsedLabel = runStartedAtMs
    ? formatElapsedDuration(elapsedNow - runStartedAtMs)
    : '00:00';

  const lastEventAt = useMemo(() => {
    const latest = session.events[session.events.length - 1];
    return formatDateTime(latest?.timestamp, '');
  }, [session.events]);

  const runSummaryRecord = useMemo<RunSummaryRecord>(
    () => ({
      status: session.status,
      transport: activeTransport,
      workflowName,
      actorId: actorId ?? '',
      commandId,
      runId: session.runId ?? '',
      focusStatus: runFocus.status,
      focusLabel: runFocus.label,
      lastEventAt,
      messageCount: session.messages.length,
      eventCount: session.events.length,
      activeSteps: [...session.activeSteps],
    }),
    [
      activeTransport,
      actorId,
      commandId,
      lastEventAt,
      runFocus.label,
      runFocus.status,
      session.activeSteps,
      session.events.length,
      session.messages.length,
      session.runId,
      session.status,
      workflowName,
    ],
  );

  useEffect(() => {
    const prompt = composerFormRef.current?.getFieldValue('prompt') ?? '';
    const candidateId =
      commandId ??
      session.runId ??
      (actorId && workflowName ? `${workflowName}:${actorId}` : '');

    if (!candidateId || (!workflowName && !prompt)) {
      return;
    }

    setRecentRuns(
      saveRecentRun({
        id: candidateId,
        workflowName,
        prompt,
        actorId: actorId ?? '',
        commandId,
        runId: session.runId ?? '',
        status: session.status,
        lastMessagePreview: latestMessagePreview,
      }),
    );
  }, [
    actorId,
    commandId,
    latestMessagePreview,
    session.runId,
    session.status,
    workflowName,
  ]);

  const messageConsoleView = (
    <div style={workbenchConsoleSurfaceStyle}>
      <div
        style={{
          borderBottom: '1px solid var(--ant-color-border-secondary)',
          color: 'var(--ant-color-text-secondary)',
          padding: '10px 12px',
        }}
      >
        Message stream
      </div>
      <div style={workbenchConsoleScrollStyle}>
        {session.messages.length > 0 ? (
          <div style={workbenchMessageListStyle}>
            {session.messages.map((record) => (
              <div
                key={record.messageId}
                style={{
                  alignSelf: record.role === 'user' ? 'flex-end' : 'flex-start',
                  background:
                    record.role === 'user'
                      ? 'rgba(22, 119, 255, 0.10)'
                      : 'rgba(15, 23, 42, 0.04)',
                  border:
                    record.complete === false
                      ? '1px solid rgba(22, 119, 255, 0.28)'
                      : '1px solid var(--ant-color-border-secondary)',
                  borderRadius: 12,
                  maxWidth: '88%',
                  padding: 12,
                }}
              >
                <Space split={<Divider type="vertical" />} size={8}>
                  <Typography.Text
                    style={{
                      color: 'var(--ant-color-text)',
                      fontFamily: 'inherit',
                    }}
                  >
                    {record.role}
                  </Typography.Text>
                  <Typography.Text
                    style={{
                      color: 'var(--ant-color-text-secondary)',
                      fontFamily: 'inherit',
                    }}
                  >
                    {record.messageId}
                  </Typography.Text>
                  <Typography.Text
                    style={{
                      color: 'var(--ant-color-text-secondary)',
                      fontFamily: 'inherit',
                    }}
                  >
                    {record.complete ? 'complete' : 'streaming'}
                  </Typography.Text>
                </Space>
                <Typography.Paragraph
                  style={{
                    color: 'var(--ant-color-text)',
                    fontFamily: 'inherit',
                    margin: '8px 0 0',
                    whiteSpace: 'pre-wrap',
                  }}
                >
                  {record.content || '(streaming...)'}
                </Typography.Paragraph>
              </div>
            ))}
          </div>
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="No message output yet."
          />
        )}
      </div>
    </div>
  );

  const eventConsoleView = (
    <div style={workbenchConsoleSurfaceStyle}>
      <div style={workbenchEventHeaderStyle}>
        <span>Timestamp</span>
        <span>Category</span>
        <span>Description</span>
      </div>
      <div style={workbenchConsoleScrollStyle}>
        {eventRows.length > 0 ? (
          eventRows.map((record) => (
            <div key={record.key} style={workbenchEventRowStyle}>
              <Typography.Text
                style={{
                  color: 'var(--ant-color-text-secondary)',
                  fontFamily: 'inherit',
                }}
              >
                {record.timestamp || 'n/a'}
              </Typography.Text>
              <Space direction="vertical" size={4}>
                <Typography.Text
                  style={{
                    color: 'var(--ant-color-text)',
                    fontFamily: 'inherit',
                  }}
                >
                  {record.eventCategory}
                </Typography.Text>
                <Typography.Text
                  style={{
                    color: 'var(--ant-color-text-secondary)',
                    fontFamily: 'inherit',
                  }}
                >
                  {record.eventStatus}
                </Typography.Text>
              </Space>
              <div>
                <Typography.Text
                  style={{
                    color: 'var(--ant-color-text)',
                    fontFamily: 'inherit',
                  }}
                >
                  {record.eventType}
                </Typography.Text>
                <Typography.Paragraph
                  ellipsis={{ rows: 2, expandable: true, symbol: 'more' }}
                  style={{
                    color: 'var(--ant-color-text-secondary)',
                    fontFamily: 'inherit',
                    margin: '6px 0 0',
                    whiteSpace: 'pre-wrap',
                  }}
                >
                  {record.description}
                  {record.payloadPreview ? `\n${record.payloadPreview}` : ''}
                </Typography.Paragraph>
              </div>
            </div>
          ))
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="No events observed yet."
          />
        )}
      </div>
    </div>
  );

  return (
    <PageContainer pageHeaderRender={false} style={{ overflow: 'hidden' }}>
      {messageContextHolder}
      <div style={runsWorkbenchShellStyle}>
        <div style={runsWorkbenchHeaderStyle}>
          <Space split={<Divider type="vertical" />} size={16}>
            <Space size={8}>
              <Badge
                status={
                  isRunLive
                    ? 'processing'
                    : session.status === 'finished'
                      ? 'success'
                      : session.status === 'error'
                        ? 'error'
                        : 'default'
                }
              />
              <Typography.Text strong>Run ID</Typography.Text>
              <Typography.Text code>
                {session.runId || commandId || 'Not started'}
              </Typography.Text>
            </Space>
            <Space size={8}>
              <Typography.Text type="secondary">Elapsed</Typography.Text>
              <Typography.Text code>{elapsedLabel}</Typography.Text>
            </Space>
            <Space size={8}>
              <Typography.Text type="secondary">Workflow</Typography.Text>
              <Typography.Text>{workflowName || 'n/a'}</Typography.Text>
            </Space>
          </Space>
          <Space split={<Divider type="vertical" />} size={16}>
            <Tag color={activeTransport === 'ws' ? 'success' : 'processing'}>
              {activeTransport.toUpperCase()}
            </Tag>
            <Badge
              color="#ff4d4f"
              count={hasPendingInteraction ? 'Pending' : 0}
              offset={[-4, 4]}
            >
              <Button onClick={() => setIsInteractionDrawerOpen(true)}>
                Interaction
              </Button>
            </Badge>
            <Button
              danger
              type="primary"
              disabled={!isRunLive}
              onClick={abortRun}
            >
              Abort
            </Button>
          </Space>
        </div>

        <div ref={runsWorkbenchMainRef} style={runsWorkbenchMainStyle}>
          <div
            style={{
              ...runsWorkbenchComposerRailStyle,
              flex: `0 0 ${composerWidth}px`,
              maxWidth: composerWidth,
              minWidth: composerWidth,
              width: composerWidth,
            }}
          >
            <ProCard
              title="Composer"
              hoverable
              {...moduleCardProps}
              style={workbenchCardStyle}
              bodyStyle={workbenchCardBodyStyle}
            >
              <div style={workbenchScrollableBodyStyle}>
                <Tabs
                  items={[
                    {
                      key: 'compose',
                      label: 'Compose',
                      children: (
                        <div style={cardStackStyle}>
                          <ProForm<RunFormValues>
                            formRef={composerFormRef}
                            layout="vertical"
                            initialValues={initialFormValues}
                            onValuesChange={(_, values) => {
                              setSelectedWorkflowName(values.workflow ?? '');
                              if (values.transport) {
                                setSelectedTransport(values.transport);
                              }
                            }}
                            onFinish={async (values) => {
                              await sendRun(
                                {
                                  prompt: values.prompt,
                                  workflow: values.workflow,
                                  agentId: values.actorId,
                                },
                                values.transport,
                              );
                              return true;
                            }}
                            submitter={{
                              render: (props) => (
                                <Space wrap>
                                  <Button
                                    type="primary"
                                    loading={streaming}
                                    onClick={() => props.form?.submit?.()}
                                  >
                                    Start run
                                  </Button>
                                  <Button
                                    onClick={abortRun}
                                    disabled={!streaming}
                                  >
                                    Abort run
                                  </Button>
                                  {actorId ? (
                                    <Button
                                      onClick={() =>
                                        history.push(
                                          `/actors?actorId=${encodeURIComponent(actorId)}`,
                                        )
                                      }
                                    >
                                      Open actor
                                    </Button>
                                  ) : null}
                                </Space>
                              ),
                            }}
                          >
                            <ProFormTextArea
                              name="prompt"
                              label="Prompt"
                              fieldProps={{ rows: 8 }}
                              placeholder="Describe the task for this run."
                              rules={[
                                {
                                  required: true,
                                  message: 'Prompt is required.',
                                },
                              ]}
                            />
                            <ProFormSelect<RunTransport>
                              name="transport"
                              label="Transport"
                              options={[
                                {
                                  label: 'SSE /api/chat',
                                  value: 'sse',
                                },
                                {
                                  label: 'WebSocket /api/ws/chat',
                                  value: 'ws',
                                },
                              ]}
                              rules={[
                                {
                                  required: true,
                                  message: 'Transport is required.',
                                },
                              ]}
                              extra="SSE is the default path. Use WebSocket to validate the alternate live transport already exposed by the backend."
                            />
                            <ProFormSelect
                              name="workflow"
                              label="Workflow"
                              placeholder="Select a workflow"
                              options={workflowOptions}
                              fieldProps={{
                                allowClear: true,
                                showSearch: true,
                                filterOption: false,
                                onSearch: setCatalogSearch,
                                notFoundContent:
                                  workflowCatalogQuery.isLoading ? (
                                    <Typography.Text type="secondary">
                                      Loading workflows...
                                    </Typography.Text>
                                  ) : (
                                    <Empty
                                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                                      description="No workflows available."
                                    />
                                  ),
                              }}
                            />
                            <ProFormText
                              name="actorId"
                              label="Existing actorId (optional)"
                              placeholder="Workflow:..."
                              extra="Provide an actorId to continue on a bound workflow actor."
                            />
                          </ProForm>

                          {selectedWorkflowRecord ? (
                            <div style={embeddedPanelStyle}>
                              <Alert
                                showIcon
                                type={
                                  selectedTransport === 'ws'
                                    ? 'success'
                                    : 'info'
                                }
                                message={
                                  selectedTransport === 'ws'
                                    ? 'WebSocket transport selected'
                                    : 'SSE transport selected'
                                }
                                description={
                                  selectedTransport === 'ws'
                                    ? 'The next run will be sent through /api/ws/chat.'
                                    : 'The next run will be sent through /api/chat.'
                                }
                                style={{ marginBottom: 16 }}
                              />
                              <ProDescriptions<SelectedWorkflowRecord>
                                column={1}
                                dataSource={selectedWorkflowRecord}
                                columns={workflowDescriptionColumns}
                              />
                              {selectedWorkflowDetails?.primitives.length ? (
                                <Space wrap size={[8, 8]}>
                                  {selectedWorkflowDetails.primitives.map(
                                    (primitive) => (
                                      <Tag key={primitive}>{primitive}</Tag>
                                    ),
                                  )}
                                </Space>
                              ) : null}
                            </div>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description="Select a workflow to see its profile."
                            />
                          )}
                        </div>
                      ),
                    },
                    {
                      key: 'recent',
                      label: `Recent (${recentRunRows.length})`,
                      children: (
                        <div style={cardStackStyle}>
                          <ProTable<RecentRunTableRow>
                            rowKey="key"
                            search={false}
                            options={false}
                            pagination={false}
                            columns={recentRunColumns}
                            dataSource={recentRunRows}
                            cardProps={compactTableCardProps}
                            scroll={{ y: 420 }}
                            locale={{
                              emptyText: (
                                <Empty
                                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                                  description="No local runs have been recorded yet."
                                />
                              ),
                            }}
                          />
                          {recentRunRows.length > 0 ? (
                            <Space>
                              <Button
                                danger
                                onClick={() => setRecentRuns(clearRecentRuns())}
                              >
                                Clear local runs
                              </Button>
                            </Space>
                          ) : null}
                        </div>
                      ),
                    },
                    {
                      key: 'presets',
                      label: `Presets (${visiblePresets.length})`,
                      children: (
                        <div style={scrollPanelStyle}>
                          <ProList<RunPreset>
                            rowKey="key"
                            search={false}
                            split
                            dataSource={visiblePresets}
                            locale={{
                              emptyText: (
                                <Empty
                                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                                  description="No presets are currently available."
                                />
                              ),
                            }}
                            metas={{
                              title: {
                                dataIndex: 'title',
                              },
                              description: {
                                dataIndex: 'description',
                              },
                              subTitle: {
                                render: (_, record) => (
                                  <Space wrap size={[4, 4]}>
                                    <Tag color="processing">
                                      {record.workflow}
                                    </Tag>
                                    {record.tags.map((tag) => (
                                      <Tag key={`${record.key}-${tag}`}>
                                        {tag}
                                      </Tag>
                                    ))}
                                  </Space>
                                ),
                              },
                              actions: {
                                render: (_, record) => [
                                  <Space key={`${record.key}-actions`}>
                                    <Button
                                      type="link"
                                      onClick={() => {
                                        composerFormRef.current?.setFieldsValue(
                                          {
                                            prompt: record.prompt,
                                            workflow: record.workflow,
                                            actorId: undefined,
                                            transport: selectedTransport,
                                          },
                                        );
                                        setSelectedWorkflowName(
                                          record.workflow,
                                        );
                                        setCatalogSearch('');
                                      }}
                                    >
                                      Use preset
                                    </Button>
                                  </Space>,
                                ],
                              },
                            }}
                          />
                        </div>
                      ),
                    },
                  ]}
                />
              </div>
            </ProCard>
          </div>
          <button
            aria-label="Resize composer panel"
            type="button"
            style={runsWorkbenchResizeRailStyle}
            onDoubleClick={() =>
              setComposerWidthWithinBounds(composerRailDefaultWidth)
            }
            onKeyDown={(event) => {
              if (event.key === 'ArrowLeft') {
                event.preventDefault();
                setComposerWidthWithinBounds(
                  composerWidth - composerRailKeyboardStep,
                );
              }

              if (event.key === 'ArrowRight') {
                event.preventDefault();
                setComposerWidthWithinBounds(
                  composerWidth + composerRailKeyboardStep,
                );
              }
            }}
            onPointerDown={(event) => {
              event.preventDefault();
              setIsComposerResizing(true);
              resizeComposerRail(event.clientX);
            }}
          >
            <div
              style={{
                ...runsWorkbenchResizeHandleStyle,
                background: isComposerResizing
                  ? 'var(--ant-color-primary)'
                  : 'var(--ant-color-border-secondary)',
                transform: isComposerResizing ? 'scaleX(1.15)' : 'scaleX(1)',
              }}
            />
          </button>
          <div style={runsWorkbenchMonitorStyle}>
            <ProCard
              title="Metric HUD"
              hoverable
              {...moduleCardProps}
              style={workbenchHudCardStyle}
              bodyStyle={workbenchHudBodyStyle}
            >
              <Row gutter={12}>
                <Col span={6}>
                  <Statistic
                    title={
                      <Space size={6}>
                        <Badge
                          status={
                            isRunLive
                              ? 'processing'
                              : session.status === 'finished'
                                ? 'success'
                                : session.status === 'error'
                                  ? 'error'
                                  : 'default'
                          }
                        />
                        <span>Status</span>
                      </Space>
                    }
                    value={runStatusText}
                  />
                </Col>
                <Col span={6}>
                  <Statistic
                    title={
                      <Space size={6}>
                        <Badge
                          status={
                            session.messages.length > 0
                              ? 'processing'
                              : 'default'
                          }
                        />
                        <span>Messages</span>
                      </Space>
                    }
                    value={session.messages.length}
                  />
                </Col>
                <Col span={6}>
                  <Statistic
                    title={
                      <Space size={6}>
                        <Badge
                          status={
                            session.events.length > 0 ? 'processing' : 'default'
                          }
                        />
                        <span>Events</span>
                      </Space>
                    }
                    value={session.events.length}
                  />
                </Col>
                <Col span={6}>
                  <Statistic
                    title={
                      <Space size={6}>
                        <Badge
                          status={
                            session.activeSteps.size > 0 ? 'warning' : 'default'
                          }
                        />
                        <span>Active steps</span>
                      </Space>
                    }
                    value={session.activeSteps.size}
                  />
                </Col>
              </Row>
            </ProCard>

            <Row gutter={12} style={workbenchOverviewGridStyle}>
              <Col xs={24} xl={14} style={stretchColumnStyle}>
                <ProCard
                  title="Live overview"
                  hoverable
                  {...moduleCardProps}
                  style={workbenchOverviewCardStyle}
                  bodyStyle={workbenchCardBodyStyle}
                >
                  <div style={workbenchScrollableBodyStyle}>
                    <div style={cardStackStyle}>
                      <Alert
                        showIcon
                        type={runFocus.alertType}
                        message={runFocus.title}
                        description={runFocus.description}
                      />
                      <ProDescriptions<RunSummaryRecord>
                        column={1}
                        dataSource={runSummaryRecord}
                        columns={runSummaryColumns}
                      />
                      {latestMessagePreview ? (
                        <div style={embeddedPanelStyle}>
                          <Typography.Text type="secondary">
                            Latest message preview
                          </Typography.Text>
                          <Typography.Paragraph
                            style={{
                              margin: '8px 0 0',
                              whiteSpace: 'pre-wrap',
                            }}
                          >
                            {latestMessagePreview}
                          </Typography.Paragraph>
                        </div>
                      ) : null}
                      {actorSnapshotQuery.data ? (
                        <Alert
                          showIcon
                          type={
                            actorSnapshotQuery.data.lastSuccess === false
                              ? 'error'
                              : 'success'
                          }
                          message="Latest actor snapshot"
                          description={
                            <Space direction="vertical" size={4}>
                              <Typography.Paragraph
                                ellipsis={{
                                  rows: 2,
                                  expandable: true,
                                  symbol: 'Expand output',
                                }}
                                style={{ marginBottom: 0 }}
                              >
                                Output:{' '}
                                {actorSnapshotQuery.data.lastOutput || 'n/a'}
                              </Typography.Paragraph>
                              <Typography.Text>
                                Updated:{' '}
                                {formatDateTime(
                                  actorSnapshotQuery.data.lastUpdatedAt,
                                )}
                              </Typography.Text>
                            </Space>
                          }
                        />
                      ) : null}
                      {session.error ? (
                        <Alert
                          showIcon
                          type="error"
                          message={session.error.code ?? 'Run error'}
                          description={session.error.message}
                        />
                      ) : null}
                    </div>
                  </div>
                </ProCard>
              </Col>
              <Col xs={24} xl={10} style={stretchColumnStyle}>
                <ProCard
                  title="Workflow profile"
                  hoverable
                  {...moduleCardProps}
                  style={workbenchOverviewCardStyle}
                  bodyStyle={workbenchCardBodyStyle}
                >
                  <div style={workbenchScrollableBodyStyle}>
                    {selectedWorkflowRecord ? (
                      <div style={cardStackStyle}>
                        <ProDescriptions<SelectedWorkflowRecord>
                          column={1}
                          dataSource={selectedWorkflowRecord}
                          columns={workflowDescriptionColumns}
                        />
                        {selectedWorkflowDetails?.primitives.length ? (
                          <Space wrap size={[8, 8]}>
                            {selectedWorkflowDetails.primitives.map(
                              (primitive) => (
                                <Tag key={primitive}>{primitive}</Tag>
                              ),
                            )}
                          </Space>
                        ) : null}
                      </div>
                    ) : (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="Select a workflow to inspect its profile."
                      />
                    )}
                  </div>
                </ProCard>
              </Col>
            </Row>
          </div>
        </div>

        <ProCard
          title="Console"
          hoverable
          {...moduleCardProps}
          style={workbenchConsoleCardStyle}
          bodyStyle={workbenchConsoleBodyStyle}
          extra={
            <Space split={<Divider type="vertical" />} size={12}>
              <Typography.Text type="secondary">
                {session.messages.length} messages
              </Typography.Text>
              <Typography.Text type="secondary">
                {eventRows.length} events
              </Typography.Text>
              <Typography.Text type="secondary">
                {hasPendingInteraction ? 'interaction pending' : 'monitoring'}
              </Typography.Text>
            </Space>
          }
        >
          <div style={workbenchConsoleViewportStyle}>
            <Tabs
              activeKey={consoleView}
              items={[
                {
                  key: 'dual',
                  label: 'Dual stream',
                  children: (
                    <div style={workbenchConsoleTabPanelStyle}>
                      <Row gutter={12} style={{ flex: 1, minHeight: 0 }}>
                        <Col span={12} style={stretchColumnStyle}>
                          {messageConsoleView}
                        </Col>
                        <Col span={12} style={stretchColumnStyle}>
                          {eventConsoleView}
                        </Col>
                      </Row>
                    </div>
                  ),
                },
                {
                  key: 'messages',
                  label: 'Messages',
                  children: (
                    <div style={workbenchConsoleTabPanelStyle}>
                      {messageConsoleView}
                    </div>
                  ),
                },
                {
                  key: 'events',
                  label: 'Events',
                  children: (
                    <div style={workbenchConsoleTabPanelStyle}>
                      {eventConsoleView}
                    </div>
                  ),
                },
              ]}
              onChange={(key) => setConsoleView(key as ConsoleViewKey)}
            />
          </div>
        </ProCard>

        <Drawer
          destroyOnHidden
          mask={false}
          open={isInteractionDrawerOpen}
          title={hasPendingInteraction ? 'Pending interaction' : 'Interaction'}
          width={420}
          onClose={() => setIsInteractionDrawerOpen(false)}
        >
          <div style={cardStackStyle}>
            {humanInputRecord ? (
              <div style={embeddedPanelStyle}>
                <Space direction="vertical" style={{ width: '100%' }} size={16}>
                  <ProDescriptions<HumanInputRecord>
                    column={1}
                    dataSource={humanInputRecord}
                    columns={humanInputColumns}
                  />
                  <ProForm<ResumeFormValues>
                    key={`${humanInputRecord.runId}-${humanInputRecord.stepId}`}
                    formRef={resumeFormRef}
                    layout="vertical"
                    initialValues={{ approved: true, userInput: '' }}
                    onFinish={async (values) => {
                      if (
                        !actorId ||
                        !humanInputRecord.runId ||
                        !humanInputRecord.stepId
                      ) {
                        return false;
                      }

                      await resume({
                        actorId,
                        runId: humanInputRecord.runId,
                        stepId: humanInputRecord.stepId,
                        approved: values.approved,
                        userInput: values.userInput || undefined,
                        commandId,
                      });

                      messageApi.success('Resume request accepted.');
                      resumeFormRef.current?.setFieldsValue({
                        approved: true,
                        userInput: '',
                      });
                      return true;
                    }}
                    submitter={{
                      render: (props) => (
                        <Space wrap>
                          <Button
                            type="primary"
                            loading={resuming}
                            onClick={() => props.form?.submit?.()}
                          >
                            Submit resume
                          </Button>
                        </Space>
                      ),
                    }}
                  >
                    <ProFormSwitch
                      name="approved"
                      label={
                        isHumanApprovalSuspension(
                          humanInputRecord.suspensionType,
                        )
                          ? 'Approved'
                          : 'Continue run'
                      }
                    />
                    <ProFormTextArea
                      name="userInput"
                      label="Operator response"
                      fieldProps={{ rows: 4 }}
                      placeholder="Optional human response"
                    />
                  </ProForm>
                </Space>
              </div>
            ) : null}

            {waitingSignalRecord ? (
              <div style={embeddedPanelStyle}>
                <Space direction="vertical" style={{ width: '100%' }} size={16}>
                  <ProDescriptions<WaitingSignalRecord>
                    column={1}
                    dataSource={waitingSignalRecord}
                    columns={waitingSignalColumns}
                  />
                  <ProForm<SignalFormValues>
                    key={`${waitingSignalRecord.runId}-${waitingSignalRecord.stepId}`}
                    formRef={signalFormRef}
                    layout="vertical"
                    initialValues={{ payload: '' }}
                    onFinish={async (values) => {
                      if (
                        !actorId ||
                        !waitingSignal?.runId ||
                        !waitingSignal.signalName
                      ) {
                        return false;
                      }

                      await signal({
                        actorId,
                        runId: waitingSignal.runId,
                        stepId: waitingSignal.stepId,
                        signalName: waitingSignal.signalName,
                        payload: values.payload || undefined,
                        commandId,
                      });

                      messageApi.success('Signal accepted.');
                      signalFormRef.current?.setFieldsValue({ payload: '' });
                      return true;
                    }}
                    submitter={{
                      render: (props) => (
                        <Space wrap>
                          <Button
                            type="primary"
                            loading={signaling}
                            onClick={() => props.form?.submit?.()}
                          >
                            Send signal
                          </Button>
                        </Space>
                      ),
                    }}
                  >
                    <ProFormTextArea
                      name="payload"
                      label="Signal payload"
                      fieldProps={{ rows: 4 }}
                      placeholder="Optional signal payload"
                    />
                  </ProForm>
                </Space>
              </div>
            ) : null}

            {!humanInputRecord && !waitingSignalRecord ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="No pending human interaction."
              />
            ) : null}
          </div>
        </Drawer>
      </div>
    </PageContainer>
  );
};

export default RunsPage;
