import {
  CaretRightFilled,
  CheckOutlined,
  CloseOutlined,
  CopyOutlined,
  ExpandOutlined,
  FileTextOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  Button,
  Empty,
  Input,
  Modal,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { Edge, Node } from '@xyflow/react';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
  StudioGraphRole,
  StudioGraphStep,
} from '@/shared/studio/graph';
import type {
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioScopeBindingImplementationKind,
  StudioWorkflowSummary,
} from '@/shared/studio/models';
import {
  buildExecutionTrace,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  formatDurationBetween,
  formatExecutionLogClipboard,
  formatExecutionLogsClipboard,
  type ExecutionInteractionState,
} from '@/shared/studio/execution';
import { formatDateTime } from '@/shared/datetime/dateTime';
import {
  cardStackStyle,
  fillCardStyle,
} from '@/shared/ui/proComponents';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { describeError } from '@/shared/ui/errorText';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_PRESSABLE_CARD_CLASS,
} from '@/shared/ui/interactionStandards';

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type StudioNoticeLike = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

function readWorkflowSortTimestamp(value: string): number {
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function compareWorkflowSummaryPriority(
  left: StudioWorkflowSummary,
  right: StudioWorkflowSummary,
): number {
  const updatedDelta =
    readWorkflowSortTimestamp(right.updatedAtUtc) -
    readWorkflowSortTimestamp(left.updatedAtUtc);
  if (updatedDelta !== 0) {
    return updatedDelta;
  }

  if (left.stepCount !== right.stepCount) {
    return right.stepCount - left.stepCount;
  }

  const leftDescriptionLength = left.description.trim().length;
  const rightDescriptionLength = right.description.trim().length;
  if (leftDescriptionLength !== rightDescriptionLength) {
    return rightDescriptionLength - leftDescriptionLength;
  }

  return left.workflowId.localeCompare(right.workflowId);
}

export function dedupeStudioWorkflowSummaries(
  workflows: readonly StudioWorkflowSummary[],
): StudioWorkflowSummary[] {
  const dedupedWorkflows = new Map<string, StudioWorkflowSummary>();

  for (const workflow of workflows) {
    const key =
      workflow.name.trim().toLowerCase() ||
      workflow.workflowId.trim().toLowerCase();
    const current = dedupedWorkflows.get(key);
    if (!current) {
      dedupedWorkflows.set(key, workflow);
      continue;
    }

    if (compareWorkflowSummaryPriority(workflow, current) < 0) {
      dedupedWorkflows.set(key, workflow);
    }
  }

  return Array.from(dedupedWorkflows.values()).sort((left, right) =>
    compareWorkflowSummaryPriority(left, right),
  );
}

function getStudioNoticeAccent(
  type: StudioNoticeLike['type'] | 'default',
): { border: string; background: string; label: string } {
  switch (type) {
    case 'success':
      return {
        border: 'rgba(82, 196, 26, 0.28)',
        background: 'rgba(246, 255, 237, 0.96)',
        label: '成功',
      };
    case 'warning':
      return {
        border: 'rgba(250, 173, 20, 0.28)',
        background: 'rgba(255, 251, 230, 0.96)',
        label: '注意',
      };
    case 'error':
      return {
        border: 'rgba(255, 77, 79, 0.28)',
        background: 'rgba(255, 241, 240, 0.96)',
        label: '错误',
      };
    case 'info':
      return {
        border: 'rgba(22, 119, 255, 0.24)',
        background: 'rgba(240, 245, 255, 0.96)',
        label: '提示',
      };
    default:
      return {
        border: 'var(--ant-color-border-secondary)',
        background: 'var(--ant-color-fill-quaternary)',
        label: '状态',
      };
  }
}

const studioNoticeCardStyle: React.CSSProperties = {
  border: '1px solid',
  borderRadius: 18,
  display: 'grid',
  gap: 10,
  padding: 14,
};

const studioEmptyPanelStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px dashed #d9d9d9',
  borderRadius: 18,
  color: '#6b7280',
  display: 'grid',
  gap: 8,
  justifyItems: 'center',
  padding: 28,
  textAlign: 'center',
};

const panelIconButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 10,
  color: '#4b5563',
  cursor: 'pointer',
  display: 'inline-flex',
  height: 30,
  justifyContent: 'center',
  width: 30,
};

const logCardBaseStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#ffffff',
  border: '1px solid #eef2f7',
  borderRadius: 14,
  cursor: 'pointer',
  display: 'grid',
  gap: 6,
  padding: 12,
  textAlign: 'left',
  width: '100%',
};

const executionActionButtonStyle: React.CSSProperties = {
  borderRadius: 10,
  minWidth: 96,
};

const executionTextareaStyle: React.CSSProperties = {
  border: '1px solid #d9d9d9',
  borderRadius: 12,
  fontSize: 13,
  lineHeight: '20px',
  minHeight: 108,
  padding: 12,
  resize: 'vertical',
  width: '100%',
};

const observeSummaryGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
  gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 340px)',
};

const observeSummaryStackStyle: React.CSSProperties = {
  display: 'grid',
  gap: 16,
};

const observeMetricGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
};

const observeMetricCardStyle: React.CSSProperties = {
  border: '1px solid #eef2f7',
  borderRadius: 12,
  display: 'grid',
  gap: 4,
  padding: 12,
};

type ObserveCompareRow = {
  readonly baseline: string;
  readonly current: string;
  readonly delta: 'same' | 'changed' | 'regression' | 'current-only';
  readonly label: string;
};

type ObserveHealthItem = {
  readonly label: string;
  readonly note: string;
  readonly status: 'active' | 'blocked' | 'warning' | 'pending';
  readonly value: string;
};

type ObservePlaybackEntry = {
  readonly detail: string;
  readonly label: string;
  readonly status: 'active' | 'done' | 'waiting';
  readonly timestamp: string;
};

const StudioNoticeCard: React.FC<{
  readonly type?: StudioNoticeLike['type'] | 'default';
  readonly title: React.ReactNode;
  readonly description?: React.ReactNode;
}> = ({ type = 'default', title, description }) => {
  const accent = getStudioNoticeAccent(type);

  return (
    <div
      style={{
        ...studioNoticeCardStyle,
        background: accent.background,
        borderColor: accent.border,
      }}
    >
      <Space wrap size={[8, 8]}>
        <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
        <Typography.Text strong>{title}</Typography.Text>
      </Space>
      {description ? (
        typeof description === 'string' ? (
          <Typography.Paragraph style={{ margin: 0 }} type="secondary">
            {description}
          </Typography.Paragraph>
        ) : (
          description
        )
      ) : null}
    </div>
  );
};

function StudioCatalogEmptyPanel(props: {
  readonly icon: React.ReactNode;
  readonly title: string;
  readonly copy: string;
}) {
  return (
    <div style={studioEmptyPanelStyle}>
      <div style={{ fontSize: 22 }}>{props.icon}</div>
      <Typography.Text strong>{props.title}</Typography.Text>
      <Typography.Text type="secondary">{props.copy}</Typography.Text>
    </div>
  );
}

function trimObserveText(value: string | null | undefined, limit = 84): string {
  const trimmed = String(value || '').trim();
  if (!trimmed) {
    return 'n/a';
  }

  return trimmed.length > limit ? `${trimmed.slice(0, limit - 3)}...` : trimmed;
}

function readExecutionDurationMs(
  execution: Pick<StudioExecutionSummary, 'startedAtUtc' | 'completedAtUtc'> | null | undefined,
): number {
  if (!execution?.startedAtUtc) {
    return 0;
  }

  const start = Date.parse(execution.startedAtUtc);
  const end = execution.completedAtUtc
    ? Date.parse(execution.completedAtUtc)
    : Date.now();
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    return 0;
  }

  return end - start;
}

function readObserveStepCoverage(
  execution:
    | Pick<StudioExecutionSummary, 'completedSteps' | 'totalSteps'>
    | null
    | undefined,
  fallbackCompleted?: number,
  fallbackTotal?: number,
): string {
  const completedSteps =
    typeof execution?.completedSteps === 'number'
      ? execution.completedSteps
      : fallbackCompleted ?? null;
  const totalSteps =
    typeof execution?.totalSteps === 'number'
      ? execution.totalSteps
      : fallbackTotal ?? null;

  if (completedSteps === null && totalSteps === null) {
    return 'n/a';
  }

  return `${completedSteps ?? 0}/${totalSteps ?? 0}`;
}

function resolveObserveDelta(input: {
  current: string;
  baseline: string;
  regressionWhen?: boolean;
}): ObserveCompareRow['delta'] {
  if (!input.baseline || input.baseline === 'n/a') {
    return 'current-only';
  }

  if (input.regressionWhen) {
    return 'regression';
  }

  return input.current === input.baseline ? 'same' : 'changed';
}

function buildObserveCompareRows(input: {
  baselineExecution: StudioExecutionSummary | null;
  selectedExecution: StudioExecutionDetail | null | undefined;
  traceLogCount: number;
  executedStepCount: number;
}): ObserveCompareRow[] {
  const { baselineExecution, selectedExecution, traceLogCount, executedStepCount } = input;
  const baselineDurationMs = readExecutionDurationMs(baselineExecution);
  const currentDurationMs = readExecutionDurationMs(selectedExecution);
  const baselineDurationLabel = baselineExecution
    ? formatDurationBetween(
        baselineExecution.startedAtUtc,
        baselineExecution.completedAtUtc,
      ) || 'n/a'
    : 'n/a';
  const currentDurationLabel = selectedExecution
    ? formatDurationBetween(
        selectedExecution.startedAtUtc,
        selectedExecution.completedAtUtc,
      ) || 'n/a'
    : 'n/a';

  const compare = (
    label: string,
    current: string,
    baseline: string,
    delta: ObserveCompareRow['delta'],
  ): ObserveCompareRow => ({
    baseline,
    current,
    delta,
    label,
  });

  if (!selectedExecution) {
    return [
      compare('status', 'no run selected', 'n/a', 'current-only'),
      compare('duration', 'n/a', 'n/a', 'current-only'),
      compare('actor', 'n/a', 'n/a', 'current-only'),
    ];
  }

  const rows: ObserveCompareRow[] = [
    compare(
      'status',
      trimObserveText(selectedExecution.status),
      trimObserveText(baselineExecution?.status),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.status),
        baseline: trimObserveText(baselineExecution?.status),
        regressionWhen:
          selectedExecution.status.toLowerCase().includes('fail') ||
          selectedExecution.status.toLowerCase().includes('stopped'),
      }),
    ),
    compare(
      'revision',
      trimObserveText(selectedExecution.revisionId),
      trimObserveText(baselineExecution?.revisionId),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.revisionId),
        baseline: trimObserveText(baselineExecution?.revisionId),
      }),
    ),
    compare(
      'state version',
      trimObserveText(
        selectedExecution.stateVersion !== null &&
          selectedExecution.stateVersion !== undefined
          ? `v${selectedExecution.stateVersion}`
          : 'n/a',
      ),
      trimObserveText(
        baselineExecution?.stateVersion !== null &&
          baselineExecution?.stateVersion !== undefined
          ? `v${baselineExecution.stateVersion}`
          : 'n/a',
      ),
      resolveObserveDelta({
        current: trimObserveText(
          selectedExecution.stateVersion !== null &&
            selectedExecution.stateVersion !== undefined
            ? `v${selectedExecution.stateVersion}`
            : 'n/a',
        ),
        baseline: trimObserveText(
          baselineExecution?.stateVersion !== null &&
            baselineExecution?.stateVersion !== undefined
            ? `v${baselineExecution.stateVersion}`
            : 'n/a',
        ),
      }),
    ),
    compare(
      'duration',
      currentDurationLabel,
      baselineDurationLabel,
      resolveObserveDelta({
        current: currentDurationLabel,
        baseline: baselineDurationLabel,
        regressionWhen:
          baselineDurationMs > 0 && currentDurationMs > baselineDurationMs,
      }),
    ),
    compare(
      'steps',
      `${readObserveStepCoverage(
        selectedExecution,
        executedStepCount,
      )} · ${traceLogCount} logs`,
      baselineExecution
        ? `${readObserveStepCoverage(baselineExecution)} · ${
            baselineExecution.roleReplyCount ?? 0
          } replies`
        : 'n/a',
      resolveObserveDelta({
        current: `${readObserveStepCoverage(
          selectedExecution,
          executedStepCount,
        )} · ${traceLogCount} logs`,
        baseline: baselineExecution
          ? `${readObserveStepCoverage(baselineExecution)} · ${
              baselineExecution.roleReplyCount ?? 0
            } replies`
          : 'n/a',
      }),
    ),
    compare(
      'actor',
      trimObserveText(selectedExecution.actorId),
      trimObserveText(baselineExecution?.actorId),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.actorId),
        baseline: trimObserveText(baselineExecution?.actorId),
      }),
    ),
    compare(
      'output',
      trimObserveText(selectedExecution.output),
      trimObserveText(baselineExecution?.output),
      resolveObserveDelta({
        current: trimObserveText(selectedExecution.output),
        baseline: trimObserveText(baselineExecution?.output),
      }),
    ),
  ];

  if (selectedExecution.error || baselineExecution?.error) {
    rows.push(
      compare(
        'error',
        trimObserveText(selectedExecution.error || 'none'),
        trimObserveText(baselineExecution?.error || 'none'),
        baselineExecution
          ? selectedExecution.error === baselineExecution.error
            ? 'same'
            : selectedExecution.error
              ? 'regression'
              : 'changed'
          : 'current-only',
      ),
    );
  }

  return rows;
}

function buildObserveHealthItems(input: {
  activeExecutionInteraction: ExecutionInteractionState | null;
  executions: readonly StudioExecutionSummary[];
  baselineExecution: StudioExecutionSummary | null;
  selectedExecution: StudioExecutionDetail | null | undefined;
  traceLogCount: number;
}): ObserveHealthItem[] {
  const {
    activeExecutionInteraction,
    baselineExecution,
    executions,
    selectedExecution,
    traceLogCount,
  } = input;
  const recentExecutions = executions.slice(0, 5);
  const failedCount = recentExecutions.filter((item) =>
    String(item.status || '').trim().toLowerCase().includes('fail'),
  ).length;
  const stoppedCount = recentExecutions.filter((item) =>
    String(item.status || '').trim().toLowerCase().includes('stop'),
  ).length;
  const runtimeStatus = String(selectedExecution?.status || '').trim().toLowerCase();
  const selectedCoverage = readObserveStepCoverage(selectedExecution);
  const auditReady = selectedExecution?.auditSource === 'run-audit';
  const humanGateValue = activeExecutionInteraction
    ? activeExecutionInteraction.kind === 'human_approval'
      ? 'awaiting approval'
      : activeExecutionInteraction.kind === 'wait_signal'
        ? 'awaiting signal'
        : 'awaiting input'
    : 'clear';

  return [
    {
      label: 'runtime',
      note: selectedExecution
        ? `Selected run ${trimObserveText(selectedExecution.executionId)} · updated ${formatDateTime(
            selectedExecution.updatedAtUtc || selectedExecution.startedAtUtc,
          )}`
        : 'No workflow run selected yet.',
      status: selectedExecution
        ? runtimeStatus.includes('fail')
          ? 'blocked'
          : runtimeStatus.includes('stop')
            ? 'warning'
            : runtimeStatus.includes('run')
            ? 'active'
            : 'pending'
        : 'pending',
      value: selectedExecution ? trimObserveText(selectedExecution.status) : 'idle',
    },
    {
      label: 'recent runs',
      note: `${failedCount} failed, ${stoppedCount} stopped in the latest ${
        recentExecutions.length || 0
      } runs.`,
      status: failedCount > 0 || stoppedCount > 0 ? 'warning' : 'active',
      value: recentExecutions.length ? `${recentExecutions.length} tracked` : 'warming up',
    },
    {
      label: 'human gate',
      note: activeExecutionInteraction
        ? activeExecutionInteraction.prompt
        : 'No human approval or input is currently blocking this run.',
      status: activeExecutionInteraction ? 'warning' : 'active',
      value: humanGateValue,
    },
    {
      label: 'audit fidelity',
      note:
        selectedExecution
          ? auditReady
            ? `Run audit updated ${formatDateTime(
                selectedExecution.auditUpdatedAtUtc || selectedExecution.updatedAtUtc,
              )}.`
            : 'Only the run summary is available so far.'
          : 'No run selected yet.',
      status: selectedExecution ? (auditReady ? 'active' : 'pending') : 'pending',
      value: auditReady ? 'run audit ready' : 'summary only',
    },
    {
      label: 'coverage',
      note:
        selectedExecution
          ? `${selectedCoverage} steps completed · ${
              selectedExecution.roleReplyCount ?? 0
            } role replies · ${traceLogCount} trace logs.`
          : 'No run selected yet.',
      status:
        selectedExecution && traceLogCount > 0
          ? 'active'
          : selectedExecution
            ? 'warning'
            : 'pending',
      value: selectedExecution ? selectedCoverage : 'n/a',
    },
    {
      label: 'baseline',
      note: baselineExecution
        ? `Comparing against ${trimObserveText(
            baselineExecution.executionId,
          )} from the same member service.`
        : 'Observe becomes more trustworthy after another member run lands and a baseline exists.',
      status: baselineExecution ? 'active' : 'pending',
      value: baselineExecution ? 'available' : 'warming up',
    },
  ];
}

function buildObservePlaybackEntries(
  logs: NonNullable<ReturnType<typeof buildExecutionTrace>>['logs'] | undefined,
): ObservePlaybackEntry[] {
  if (!logs?.length) {
    return [];
  }

  return logs
    .filter((log) =>
      Boolean(
        log.interaction ||
          log.title.toLowerCase().includes('approved') ||
          log.title.toLowerCase().includes('rejected') ||
          log.title.toLowerCase().includes('input submitted') ||
          log.title.toLowerCase().includes('signal sent') ||
          log.title.toLowerCase().includes('waiting for signal') ||
          log.title.toLowerCase().includes('stop requested'),
      ),
    )
    .slice(-6)
    .map((log) => ({
      detail: trimObserveText(log.previewText || log.meta || '', 140),
      label: log.title,
      status: log.interaction
        ? 'waiting'
        : log.tone === 'pending'
          ? 'active'
          : 'done' as ObservePlaybackEntry['status'],
      timestamp: formatDateTime(log.timestamp),
    }))
    .reverse();
}

export type StudioExecutionPageProps = {
  readonly executions: QueryState<StudioExecutionSummary[]>;
  readonly selectedExecution: QueryState<StudioExecutionDetail>;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Node<StudioGraphNodeData>[];
    readonly edges: Edge<StudioGraphEdgeData>[];
  };
  readonly draftWorkflowName: string;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeDirectoryLabel: string;
  readonly selectedMemberLabel?: string;
  readonly currentImplementationLabel?: string;
  readonly currentImplementationKind?: StudioScopeBindingImplementationKind;
  readonly emptyState?: {
    readonly title: string;
    readonly description: string;
  } | null;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly runPending: boolean;
  readonly canOpenRunWorkflow: boolean;
  readonly canRunWorkflow: boolean;
  readonly executionCanStop: boolean;
  readonly executionStopPending: boolean;
  readonly runPrompt: string;
  readonly executionNotice: StudioNoticeLike | null;
  readonly logsPopoutMode?: boolean;
  readonly logsDetached?: boolean;
  readonly onOpenExecution: (executionId: string) => void;
  readonly onSaveDraft: () => void;
  readonly onExportDraft: () => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onSetWorkflowDescription: (value: string) => void;
  readonly onRunPromptChange: (value: string) => void;
  readonly onStartExecution: () => void;
  readonly onResumeExecution: (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject' | 'signal',
    userInput: string,
  ) => Promise<void>;
  readonly onStopExecution: () => void;
  readonly onPopOutLogs?: () => void;
};

export const StudioExecutionPage: React.FC<StudioExecutionPageProps> = ({
  executions,
  selectedExecution,
  workflowGraph,
  draftWorkflowName,
  activeWorkflowName,
  activeWorkflowDescription,
  activeDirectoryLabel,
  selectedMemberLabel,
  currentImplementationLabel,
  currentImplementationKind = 'unknown',
  emptyState = null,
  runPending,
  canRunWorkflow,
  executionCanStop,
  executionStopPending,
  runPrompt,
  executionNotice,
  logsPopoutMode = false,
  logsDetached = false,
  onOpenExecution,
  onRunPromptChange,
  onStartExecution,
  onResumeExecution,
  onStopExecution,
  onPopOutLogs,
}) => {
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedExecutionLogIndex, setCopiedExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedAllExecutionLogs, setCopiedAllExecutionLogs] = React.useState(false);
  const [copiedExecutionActorId, setCopiedExecutionActorId] =
    React.useState<string | null>(null);
  const [executionActionInput, setExecutionActionInput] = React.useState('');
  const [executionActionPendingKey, setExecutionActionPendingKey] =
    React.useState('');
  const [runModalOpen, setRunModalOpen] = React.useState(false);

  const selectedExecutionDetail = selectedExecution.data;
  const executionTrace = React.useMemo(
    () => buildExecutionTrace(selectedExecutionDetail),
    [selectedExecutionDetail],
  );
  const workflowGraphAvailable =
    currentImplementationKind === 'workflow' && workflowGraph.nodes.length > 0;

  React.useEffect(() => {
    setActiveExecutionLogIndex(executionTrace?.defaultLogIndex ?? null);
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [executionTrace, selectedExecutionDetail?.executionId]);

  const currentMemberExecutions = React.useMemo(
    () => executions.data ?? [],
    [executions.data],
  );

  const selectedExecutionActorId = selectedExecutionDetail?.actorId || null;
  const activeExecutionLog =
    executionTrace && Number.isInteger(activeExecutionLogIndex)
      ? executionTrace.logs[activeExecutionLogIndex as number] || null
      : null;
  const activeExecutionInteraction =
    activeExecutionLog?.interaction &&
    activeExecutionLog.stepId &&
    executionTrace?.stepStates.get(activeExecutionLog.stepId)?.status === 'waiting'
      ? activeExecutionLog.interaction
      : null;
  const executionActionKeyBase =
    selectedExecutionDetail?.executionId && activeExecutionInteraction
      ? `${selectedExecutionDetail.executionId}:${activeExecutionInteraction.stepId}`
      : '';
  const decoratedExecutionNodes = React.useMemo(
    () =>
      decorateNodesForExecution(
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.nodes],
  );
  const decoratedExecutionEdges = React.useMemo(
    () =>
      decorateEdgesForExecution(
        workflowGraph.edges,
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.edges, workflowGraph.nodes],
  );
  const executionLogCount = executionTrace?.logs.length ?? 0;
  const executionExecutedSteps = React.useMemo(() => {
    const tracedStepCount = new Set(
      (executionTrace?.logs ?? [])
        .map((log) => log.stepId || '')
        .filter(Boolean),
    ).size;

    if (tracedStepCount > 0) {
      return tracedStepCount;
    }

    return selectedExecutionDetail?.completedSteps ?? 0;
  }, [executionTrace, selectedExecutionDetail?.completedSteps]);
  const executionTotalSteps =
    (workflowGraphAvailable
      ? workflowGraph.steps.length || workflowGraph.nodes.length
      : 0) ||
    selectedExecutionDetail?.totalSteps ||
    workflowGraph.steps.length ||
    workflowGraph.nodes.length;
  const executionStatusKey = String(selectedExecutionDetail?.status || '')
    .trim()
    .toLowerCase();
  const executionStatusLabel =
    executionStatusKey === 'running'
      ? '运行中'
      : executionStatusKey === 'completed'
        ? '已完成'
        : executionStatusKey === 'failed'
          ? '执行失败'
          : selectedExecutionDetail
            ? '等待执行'
            : '未开始';
  const executionAccentColor =
    executionStatusKey === 'running'
      ? '#1890ff'
      : executionStatusKey === 'completed'
        ? '#52c41a'
        : executionStatusKey === 'failed'
          ? '#ff4d4f'
          : '#8c8c8c';
  const executionBarStyle: React.CSSProperties =
    executionStatusKey === 'running'
      ? {
          background: '#e6f7ff',
          borderBottom: '1px solid #91d5ff',
        }
      : executionStatusKey === 'completed'
        ? {
            background: '#f6ffed',
            borderBottom: '1px solid #b7eb8f',
          }
        : executionStatusKey === 'failed'
          ? {
              background: '#fff2f0',
              borderBottom: '1px solid #ffccc7',
            }
          : {
              background: '#fafafa',
              borderBottom: '1px solid #f0f0f0',
            };
  const executionPromptPreview = (selectedExecutionDetail?.prompt || runPrompt).trim();
  const executionDurationLabel = selectedExecutionDetail
    ? formatDurationBetween(
        selectedExecutionDetail.startedAtUtc,
        selectedExecutionDetail.completedAtUtc,
      )
    : '';
  const baselineExecution =
    currentMemberExecutions.find(
      (item) => item.executionId !== selectedExecutionDetail?.executionId,
    ) || null;
  const observeCompareRows = React.useMemo(
    () =>
      buildObserveCompareRows({
        baselineExecution,
        selectedExecution: selectedExecutionDetail,
        traceLogCount: executionLogCount,
        executedStepCount: executionExecutedSteps,
      }),
    [
      baselineExecution,
      executionExecutedSteps,
      executionLogCount,
      selectedExecutionDetail,
    ],
  );
  const observeHealthItems = React.useMemo(
    () =>
      buildObserveHealthItems({
        activeExecutionInteraction,
        baselineExecution,
        executions: currentMemberExecutions,
        selectedExecution: selectedExecutionDetail,
        traceLogCount: executionLogCount,
      }),
    [
      activeExecutionInteraction,
      baselineExecution,
      currentMemberExecutions,
      executionLogCount,
      selectedExecutionDetail,
    ],
  );
  const observePlaybackEntries = React.useMemo(
    () => buildObservePlaybackEntries(executionTrace?.logs),
    [executionTrace?.logs],
  );
  const workflowGraphFallback = React.useMemo(() => {
    switch (currentImplementationKind) {
      case 'script':
        return {
          title: 'Script members do not expose a workflow graph.',
          copy:
            'Observe still shows runtime logs, audit facts, and run controls below. Workflow graph playback is available for workflow-backed members only.',
        };
      case 'gagent':
        return {
          title: 'GAgent members do not expose a workflow graph.',
          copy:
            'Observe still shows runtime logs, audit facts, and run controls below. Workflow graph playback is available for workflow-backed members only.',
        };
      case 'workflow':
        return {
          title: 'Workflow graph unavailable for this member.',
          copy:
            'Studio could not resolve a matching workflow document for the current member context right now. Logs, audit facts, and run controls are still available below.',
        };
      default:
        return {
          title: 'Workflow graph unavailable.',
          copy:
            'Observe still shows runtime logs, audit facts, and run controls below.',
        };
    }
  }, [currentImplementationKind]);

  const copyText = async (value: string): Promise<boolean> => {
    if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
      return false;
    }

    await navigator.clipboard.writeText(value);
    return true;
  };

  const showExecutionLogCopyFeedback = (mode: 'single' | 'all', index?: number) => {
    setCopiedExecutionLogIndex(mode === 'single' ? index ?? null : null);
    setCopiedAllExecutionLogs(mode === 'all');
    window.setTimeout(() => {
      setCopiedExecutionLogIndex(null);
      setCopiedAllExecutionLogs(false);
    }, 1600);
  };

  const handleExecutionLogClick = async (
    log: NonNullable<typeof executionTrace>['logs'][number],
    index: number,
  ) => {
    setActiveExecutionLogIndex(index);
    const copied = await copyText(formatExecutionLogClipboard(log));
    if (copied) {
      showExecutionLogCopyFeedback('single', index);
    }
  };

  const handleCopyAllExecutionLogs = async () => {
    const copied = await copyText(formatExecutionLogsClipboard(executionTrace));
    if (copied) {
      showExecutionLogCopyFeedback('all');
    }
  };

  const handleCopyExecutionActorId = async (actorId: string) => {
    const copied = await copyText(actorId);
    if (!copied) {
      return;
    }

    setCopiedExecutionActorId(actorId);
    window.setTimeout(() => {
      setCopiedExecutionActorId((current) =>
        current === actorId ? null : current,
      );
    }, 1600);
  };

  const handleExecutionInteraction = async (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject' | 'signal',
  ) => {
    const trimmedInput = executionActionInput.trim();
    if (interaction.kind === 'human_input' && !trimmedInput) {
      return;
    }

    const pendingKey = `${executionActionKeyBase}:${action}`;
    setExecutionActionPendingKey(pendingKey);

    try {
      await onResumeExecution(interaction, action, trimmedInput);
      setExecutionActionInput('');
    } finally {
      setExecutionActionPendingKey('');
    }
  };

  const renderExecutionLogs = (fullscreen: boolean) => {
    const hasRuns = currentMemberExecutions.length > 0;

    return (
      <section
        style={{
          background: '#ffffff',
          borderTop: fullscreen ? 'none' : '1px solid #f0f0f0',
          display: 'grid',
          gap: 12,
          minHeight: fullscreen ? '100%' : activeExecutionInteraction ? 300 : 220,
          padding: 12,
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
          <div style={{ display: 'grid', gap: 2 }}>
            <Typography.Text strong style={{ fontSize: 12, margin: 0 }}>
              执行日志
            </Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 11, margin: 0 }}>
              {executionLogCount} 个事件
            </Typography.Text>
          </div>

          <div
            style={{
              alignItems: 'center',
              display: 'flex',
              flexWrap: 'wrap',
              gap: 8,
              justifyContent: 'flex-end',
            }}
          >
            {hasRuns ? (
              <select
                aria-label="选择测试运行"
                value={selectedExecutionDetail?.executionId || ''}
                onChange={(event) => {
                  if (event.target.value) {
                    onOpenExecution(event.target.value);
                  }
                }}
                style={{
                  border: '1px solid #d9d9d9',
                  borderRadius: 8,
                  fontSize: 12,
                  height: 30,
                  minWidth: 220,
                  padding: '0 8px',
                }}
              >
                <option value="">
                  {selectedExecutionDetail
                    ? `${formatDateTime(selectedExecutionDetail.startedAtUtc)} · ${selectedExecutionDetail.status}`
                    : `${currentMemberExecutions.length} 次运行`}
                </option>
                {currentMemberExecutions.map((execution) => (
                  <option key={execution.executionId} value={execution.executionId}>
                    {formatDateTime(execution.startedAtUtc)} · {execution.status}
                  </option>
                ))}
              </select>
            ) : null}

            {executionTrace?.logs?.length ? (
              <button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={panelIconButtonStyle}
                title="复制全部执行日志"
                aria-label="Copy all execution logs."
                onClick={() => void handleCopyAllExecutionLogs()}
              >
                {copiedAllExecutionLogs ? <CheckOutlined /> : <CopyOutlined />}
              </button>
            ) : null}

            {selectedExecutionActorId ? (
              <>
                <Typography.Text type="secondary" style={{ fontSize: 11, margin: 0 }}>
                  Actor ID
                </Typography.Text>
                <code
                  title={selectedExecutionActorId}
                  style={{
                    color: '#595959',
                    fontSize: 11,
                    maxWidth: 220,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {selectedExecutionActorId}
                </code>
                <button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  type="button"
                  style={panelIconButtonStyle}
                  title="复制 Actor ID"
                  aria-label="Copy Actor ID."
                  onClick={() =>
                    void handleCopyExecutionActorId(selectedExecutionActorId)
                  }
                >
                  {copiedExecutionActorId === selectedExecutionActorId ? (
                    <CheckOutlined />
                  ) : (
                    <CopyOutlined />
                  )}
                </button>
              </>
            ) : null}

            {selectedExecutionDetail?.executionId && !fullscreen ? (
              <button
                aria-pressed={logsDetached}
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={{
                  ...panelIconButtonStyle,
                  borderColor: logsDetached ? '#1677ff' : '#e5e7eb',
                  color: logsDetached ? '#1677ff' : '#4b5563',
                }}
                title={logsDetached ? '聚焦日志窗口' : '弹出日志窗口'}
                aria-label="Pop out execution logs."
                onClick={onPopOutLogs}
              >
                <ExpandOutlined />
              </button>
            ) : null}

            {fullscreen ? (
              <button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                style={panelIconButtonStyle}
                title="关闭窗口"
                aria-label="Close logs window."
                onClick={() => {
                  if (typeof window !== 'undefined') {
                    window.close();
                  }
                }}
              >
                <CloseOutlined />
              </button>
            ) : null}
          </div>
        </div>

        {activeExecutionInteraction ? (
          <div
            style={{
              border: '1px solid #e5e7eb',
              borderRadius: 16,
              display: 'grid',
              gap: 12,
              padding: 14,
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
              <div>
                <Typography.Text strong>
                  {activeExecutionInteraction.kind === 'human_approval'
                    ? '等待人工审批'
                    : activeExecutionInteraction.kind === 'wait_signal'
                      ? '等待外部信号'
                    : '等待人工输入'}
                </Typography.Text>
                <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>
                  {activeExecutionInteraction.kind === 'human_approval'
                    ? '查看当前关卡并决定通过或驳回。'
                    : activeExecutionInteraction.kind === 'wait_signal'
                      ? '发送当前步骤等待的信号后，运行会继续执行。'
                    : '补充缺失信息后，当前步骤会继续执行。'}
                </div>
              </div>
              <span
                style={{
                  background: '#f3f4f6',
                  borderRadius: 999,
                  color: '#4b5563',
                  fontSize: 11,
                  padding: '4px 10px',
                }}
              >
                {activeExecutionInteraction.stepId}
              </span>
            </div>

            {activeExecutionInteraction.prompt ? (
              <div
                style={{
                  background: '#fafafa',
                  border: '1px solid #f0f0f0',
                  borderRadius: 12,
                  color: '#374151',
                  fontSize: 12,
                  lineHeight: '20px',
                  padding: 12,
                }}
              >
                {activeExecutionInteraction.prompt}
              </div>
            ) : null}

            <textarea
              aria-label="执行交互输入"
              value={executionActionInput}
              onChange={(event) => setExecutionActionInput(event.target.value)}
              style={executionTextareaStyle}
              placeholder={
                activeExecutionInteraction.kind === 'human_approval'
                  ? '可选补充说明'
                  : activeExecutionInteraction.kind === 'wait_signal'
                    ? '可选 signal payload'
                  : '输入继续执行所需的内容'
              }
            />

            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
                justifyContent: 'flex-end',
              }}
            >
              {activeExecutionInteraction.kind === 'human_approval' ? (
                <>
                  <Button
                    danger
                    style={executionActionButtonStyle}
                    disabled={
                      executionActionPendingKey ===
                      `${executionActionKeyBase}:reject`
                    }
                    onClick={() =>
                      void handleExecutionInteraction(
                        activeExecutionInteraction,
                        'reject',
                      )
                    }
                  >
                    {executionActionPendingKey ===
                    `${executionActionKeyBase}:reject`
                      ? '驳回中...'
                      : '驳回'}
                  </Button>
                  <Button
                    type="primary"
                    style={executionActionButtonStyle}
                    disabled={
                      executionActionPendingKey ===
                      `${executionActionKeyBase}:approve`
                    }
                    onClick={() =>
                      void handleExecutionInteraction(
                        activeExecutionInteraction,
                        'approve',
                      )
                    }
                  >
                    {executionActionPendingKey ===
                    `${executionActionKeyBase}:approve`
                      ? '通过中...'
                      : '通过'}
                  </Button>
                </>
              ) : activeExecutionInteraction.kind === 'wait_signal' ? (
                <Button
                  type="primary"
                  style={executionActionButtonStyle}
                  disabled={
                    executionActionPendingKey ===
                    `${executionActionKeyBase}:signal`
                  }
                  onClick={() =>
                    void handleExecutionInteraction(
                      activeExecutionInteraction,
                      'signal',
                    )
                  }
                >
                  {executionActionPendingKey ===
                  `${executionActionKeyBase}:signal`
                    ? '发送中...'
                    : '发送信号'}
                </Button>
              ) : (
                <Button
                  type="primary"
                  style={executionActionButtonStyle}
                  disabled={
                    executionActionPendingKey ===
                    `${executionActionKeyBase}:submit`
                  }
                  onClick={() =>
                    void handleExecutionInteraction(
                      activeExecutionInteraction,
                      'submit',
                    )
                  }
                >
                  {executionActionPendingKey ===
                  `${executionActionKeyBase}:submit`
                    ? '提交中...'
                    : '提交'}
                </Button>
              )}
            </div>
          </div>
        ) : null}

        <div
          style={{
            display: 'grid',
            gap: 10,
            maxHeight: fullscreen ? 'none' : 320,
            overflowY: 'auto',
          }}
        >
          {!hasRuns ? (
            <StudioCatalogEmptyPanel
              icon={<CaretRightFilled style={{ color: '#CBD5E1' }} />}
              title="暂无运行记录"
              copy="开始一次测试运行后，这里会显示执行日志。"
            />
          ) : executionTrace?.logs?.length ? (
            executionTrace.logs.map((log, index) => (
              <button
                className={AEVATAR_PRESSABLE_CARD_CLASS}
                key={`${log.timestamp}-${log.stepId || 'run'}-${log.title}`}
                type="button"
                onClick={() => void handleExecutionLogClick(log, index)}
                style={{
                  ...logCardBaseStyle,
                  background:
                    activeExecutionLogIndex === index ? '#F5F7FF' : '#FFFFFF',
                  borderColor:
                    activeExecutionLogIndex === index ? '#91caff' : '#eef2f7',
                }}
                title="点击复制这条日志"
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                  }}
                >
                  <Typography.Text strong style={{ fontSize: 12 }}>
                    {log.title}
                  </Typography.Text>
                  <div
                    style={{
                      alignItems: 'center',
                      color: '#9ca3af',
                      display: 'flex',
                      gap: 8,
                      fontSize: 11,
                    }}
                  >
                    {copiedExecutionLogIndex === index ? (
                      <span style={{ color: '#1677ff' }}>
                        <CheckOutlined /> Copied
                      </span>
                    ) : null}
                    {formatDateTime(log.timestamp)}
                  </div>
                </div>
                {log.meta ? (
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    {log.meta}
                  </Typography.Text>
                ) : null}
                <div style={{ color: '#374151', fontSize: 12 }}>
                  {log.previewText || log.meta || log.title}
                </div>
              </button>
            ))
          ) : (
            <StudioCatalogEmptyPanel
              icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
              title="还没有日志"
              copy="选择一条测试运行后，这里会显示步骤执行和状态变化。"
            />
          )}
        </div>
      </section>
    );
  };

  if (logsPopoutMode) {
    return (
      <div style={{ ...fillCardStyle, height: '100%' }}>
        {selectedExecution.isError ? (
          <StudioNoticeCard
            type="error"
            title="读取执行详情失败"
            description={describeError(selectedExecution.error)}
          />
        ) : selectedExecution.data ? (
          renderExecutionLogs(true)
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="选择一条测试运行后，这里会显示执行日志。"
          />
        )}
      </div>
    );
  }

  return (
    <div style={cardStackStyle}>
      {executions.isError ? (
        <StudioNoticeCard
          type="error"
          title="读取测试运行列表失败"
          description={describeError(executions.error)}
        />
      ) : null}
      {selectedExecution.isError ? (
        <StudioNoticeCard
          type="error"
          title="读取执行详情失败"
          description={describeError(selectedExecution.error)}
        />
      ) : null}
      {executionNotice ? (
        <StudioNoticeCard
          type={executionNotice.type}
          title={
            executionNotice.type === 'error'
              ? '执行操作失败'
              : executionNotice.type === 'info'
                ? '已请求停止运行'
                : '执行状态已更新'
          }
          description={executionNotice.message}
        />
      ) : null}
      {selectedExecutionDetail?.error ? (
        <StudioNoticeCard
          type="error"
          title="执行异常"
          description={selectedExecutionDetail.error}
        />
      ) : null}
      {emptyState ? (
        <StudioNoticeCard
          type="info"
          title={emptyState.title}
          description={emptyState.description}
        />
      ) : null}

      <div style={observeSummaryGridStyle}>
        <div style={observeSummaryStackStyle}>
          <AevatarPanel
            title="Run Compare"
            titleHelp="Observe should answer what changed between the selected run and the nearest baseline before operators dive into logs."
            extra={
              baselineExecution ? (
                <Tag>{`${baselineExecution.executionId} baseline`}</Tag>
              ) : (
                <Tag>no baseline yet</Tag>
              )
            }
          >
            <div style={{ display: 'grid', gap: 10 }}>
              {observeCompareRows.map((row) => (
                <div
                  key={row.label}
                  style={{
                    alignItems: 'center',
                    borderBottom: '1px solid #f3f4f6',
                    display: 'grid',
                    gap: 12,
                    gridTemplateColumns: '120px minmax(0, 1fr) minmax(0, 1fr) auto',
                    paddingBottom: 10,
                  }}
                >
                  <Typography.Text type="secondary">{row.label}</Typography.Text>
                  <Typography.Text>{row.current}</Typography.Text>
                  <Typography.Text type="secondary">{row.baseline}</Typography.Text>
                  <AevatarStatusTag
                    domain="observation"
                    label="delta"
                    status={row.delta}
                  />
                </div>
              ))}
            </div>
          </AevatarPanel>

          <AevatarPanel
            title="Human Escalation Playback"
            titleHelp="Keep waiting approvals, submitted answers, and recent human hand-offs visible before the operator scrolls into the raw event log."
          >
            {observePlaybackEntries.length > 0 ? (
              <div style={{ display: 'grid', gap: 10 }}>
                {observePlaybackEntries.map((entry) => (
                  <div
                    key={`${entry.timestamp}-${entry.label}`}
                    style={{
                      borderBottom: '1px solid #f3f4f6',
                      display: 'grid',
                      gap: 4,
                      paddingBottom: 10,
                    }}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        justifyContent: 'space-between',
                      }}
                    >
                      <Typography.Text strong>{entry.label}</Typography.Text>
                      <AevatarStatusTag
                        domain="observation"
                        label="playback"
                        status={entry.status}
                      />
                    </div>
                    <Typography.Text type="secondary">{entry.detail}</Typography.Text>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {entry.timestamp}
                    </Typography.Text>
                  </div>
                ))}
              </div>
            ) : (
              <StudioCatalogEmptyPanel
                icon={<UserOutlined style={{ color: '#CBD5E1' }} />}
                title="暂无人工介入"
                copy="当前选择的运行还没有出现 approval、input 或 replay 片段。"
              />
            )}
          </AevatarPanel>

          <AevatarPanel
            title="Member Snapshot"
            titleHelp="Observe keeps the current member identity, active actor, and selected run provenance visible without sending operators back into Bind."
          >
            <div style={observeMetricGridStyle}>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Member</Typography.Text>
                <Typography.Text strong>
                  {selectedMemberLabel || activeWorkflowName || draftWorkflowName || 'Current member'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Implementation</Typography.Text>
                <Typography.Text strong>
                  {currentImplementationLabel ||
                    activeWorkflowName ||
                    draftWorkflowName ||
                    'Current implementation'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Workspace</Typography.Text>
                <Typography.Text strong>{activeDirectoryLabel || 'Workspace'}</Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Execution</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail?.executionId || 'No run selected'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Service</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail?.serviceId || 'n/a'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Revision</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail?.revisionId || 'n/a'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Actor</Typography.Text>
                <Typography.Text strong>{selectedExecutionActorId || 'n/a'}</Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">State Version</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail?.stateVersion !== null &&
                  selectedExecutionDetail?.stateVersion !== undefined
                    ? `v${selectedExecutionDetail.stateVersion}`
                    : 'n/a'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Started</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail
                    ? formatDateTime(selectedExecutionDetail.startedAtUtc)
                    : 'n/a'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Updated</Typography.Text>
                <Typography.Text strong>
                  {selectedExecutionDetail?.updatedAtUtc
                    ? formatDateTime(selectedExecutionDetail.updatedAtUtc)
                    : 'n/a'}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Prompt</Typography.Text>
                <Typography.Text strong>
                  {trimObserveText(executionPromptPreview || 'No prompt yet.', 72)}
                </Typography.Text>
              </div>
              <div style={observeMetricCardStyle}>
                <Typography.Text type="secondary">Output</Typography.Text>
                <Typography.Text strong>
                  {trimObserveText(selectedExecutionDetail?.output || 'No output yet.', 72)}
                </Typography.Text>
              </div>
            </div>
          </AevatarPanel>
        </div>

        <div style={observeSummaryStackStyle}>
          <AevatarPanel
            title="Health & Trust"
            titleHelp="Observe should quickly answer whether the current member looks healthy, whether humans are blocked, and whether the trace can be trusted."
          >
            <div style={{ display: 'grid', gap: 12 }}>
              {observeHealthItems.map((item) => (
                <div
                  key={item.label}
                  style={{
                    borderBottom: '1px solid #f3f4f6',
                    display: 'grid',
                    gap: 6,
                    paddingBottom: 10,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 8,
                      justifyContent: 'space-between',
                    }}
                  >
                    <Typography.Text type="secondary">{item.label}</Typography.Text>
                    <AevatarStatusTag
                      domain="observation"
                      label="health"
                      status={item.status}
                    />
                  </div>
                  <Typography.Text strong>{item.value}</Typography.Text>
                  <Typography.Text type="secondary">{item.note}</Typography.Text>
                </div>
              ))}
            </div>
          </AevatarPanel>

          <AevatarPanel
            title="Provenance"
            titleHelp="Be explicit about what is live, what is inferred from the selected execution, and what still needs another run or baseline."
          >
            <div style={{ display: 'grid', gap: 12 }}>
              <Space wrap size={[8, 8]}>
                <Tag
                  color={
                    selectedExecutionDetail?.auditSource === 'run-audit'
                      ? 'green'
                      : 'default'
                  }
                >
                  {selectedExecutionDetail?.auditSource === 'run-audit'
                    ? 'run audit ready'
                    : 'summary only'}
                </Tag>
                <Tag
                  color={
                    workflowGraphAvailable
                      ? 'green'
                      : currentImplementationKind === 'workflow'
                        ? 'gold'
                        : 'default'
                  }
                >
                  {workflowGraphAvailable
                    ? 'workflow graph ready'
                    : currentImplementationKind === 'workflow'
                      ? 'workflow graph unavailable'
                      : 'workflow-only graph'}
                </Tag>
                <Tag color={baselineExecution ? 'blue' : 'default'}>
                  {baselineExecution ? 'baseline available' : 'baseline warming up'}
                </Tag>
              </Space>
              <div style={observeMetricGridStyle}>
                <div style={observeMetricCardStyle}>
                  <Typography.Text type="secondary">Definition Actor</Typography.Text>
                  <Typography.Text strong>
                    {selectedExecutionDetail?.definitionActorId || 'n/a'}
                  </Typography.Text>
                </div>
                <div style={observeMetricCardStyle}>
                  <Typography.Text type="secondary">Last Event</Typography.Text>
                  <Typography.Text strong>
                    {selectedExecutionDetail?.lastEventId || 'n/a'}
                  </Typography.Text>
                </div>
                <div style={observeMetricCardStyle}>
                  <Typography.Text type="secondary">Audit Updated</Typography.Text>
                  <Typography.Text strong>
                    {selectedExecutionDetail?.auditUpdatedAtUtc
                      ? formatDateTime(selectedExecutionDetail.auditUpdatedAtUtc)
                      : 'n/a'}
                  </Typography.Text>
                </div>
              </div>
              <Typography.Text type="secondary">
                {selectedExecutionDetail
                  ? `Selected run facts are coming from service ${trimObserveText(
                      selectedExecutionDetail.serviceId,
                    )}, revision ${trimObserveText(
                      selectedExecutionDetail.revisionId,
                    )}, actor ${trimObserveText(selectedExecutionDetail.actorId)}.`
                  : activeWorkflowDescription ||
                    '当前 Observe 页只展示当前 member 的运行事实；契约与发布信息留在 Bind。'}
              </Typography.Text>
              <Typography.Text type="secondary">
                {baselineExecution
                  ? `The compare baseline is ${baselineExecution.executionId} on revision ${trimObserveText(
                      baselineExecution.revisionId,
                    )}, started ${formatDateTime(baselineExecution.startedAtUtc)}.`
                  : 'Observe can compare more meaningfully after the next run lands and a baseline exists.'}
              </Typography.Text>
            </div>
          </AevatarPanel>
        </div>
      </div>

      <div
        style={{
          background: '#FFFFFF',
          border: '1px solid #E6E3DE',
          borderRadius: 28,
          boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
          display: 'flex',
          flexDirection: 'column',
          minHeight: 'calc(100vh - 176px)',
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            ...executionBarStyle,
            alignItems: 'center',
            display: 'flex',
            flexWrap: 'wrap',
            gap: 12,
            minHeight: 36,
            padding: '0 16px',
          }}
        >
          <span
            style={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              background: executionAccentColor,
            }}
          />
          <Typography.Text
            strong
            style={{ color: executionAccentColor, margin: 0 }}
          >
            {executionStatusLabel}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ margin: 0 }}>
            {(activeWorkflowName || draftWorkflowName || '当前流程').trim() || '当前流程'} ·
            已执行 {executionExecutedSteps}/{executionTotalSteps || 0} 步骤
            {executionDurationLabel ? ` · 耗时 ${executionDurationLabel}` : ''}
          </Typography.Text>
          {executionPromptPreview ? (
            <Typography.Text
              type="secondary"
              style={{
                marginLeft: 'auto',
                maxWidth: 380,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
            >
              输入: "{executionPromptPreview}"
            </Typography.Text>
          ) : null}
          <Button
            type="primary"
            size="small"
            loading={runPending}
            disabled={!canRunWorkflow || runPending}
            onClick={() => setRunModalOpen(true)}
          >
            重新运行
          </Button>
          {executionCanStop ? (
            <Button
              danger
              size="small"
              loading={executionStopPending}
              disabled={executionStopPending}
              onClick={onStopExecution}
            >
              停止
            </Button>
          ) : null}
        </div>

        <div
          style={{
            background: '#FAFAFA',
            display: 'flex',
            flex: 1,
            flexDirection: 'column',
            minHeight: 0,
          }}
        >
          <div
            style={{
              flex: 1,
              minHeight: 320,
              overflow: 'hidden',
              position: 'relative',
            }}
          >
            {workflowGraphAvailable ? (
              <GraphCanvas
                height="100%"
                bottomInset={0}
                variant="studio"
                nodes={decoratedExecutionNodes}
                edges={decoratedExecutionEdges}
                selectedNodeId={
                  activeExecutionLog?.stepId
                    ? decoratedExecutionNodes.find(
                        (node) => node.data.stepId === activeExecutionLog.stepId,
                      )?.id
                    : undefined
                }
                onNodeSelect={(nodeId) => {
                  const stepId =
                    decoratedExecutionNodes.find((node) => node.id === nodeId)?.data.stepId ||
                    '';
                  const logIndex = findExecutionLogIndexForStep(executionTrace, stepId);
                  if (logIndex !== null) {
                    setActiveExecutionLogIndex(logIndex);
                  }
                }}
              />
            ) : (
              <div
                style={{
                  alignItems: 'center',
                  display: 'grid',
                  height: '100%',
                  padding: 24,
                }}
              >
                <StudioCatalogEmptyPanel
                  icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
                  title={workflowGraphFallback.title}
                  copy={workflowGraphFallback.copy}
                />
              </div>
            )}
          </div>

          {renderExecutionLogs(false)}
        </div>
      </div>

      <Modal
        open={runModalOpen}
        title="测试运行"
        onCancel={() => setRunModalOpen(false)}
        onOk={() => {
          void onStartExecution();
          setRunModalOpen(false);
        }}
        okText="开始运行"
        okButtonProps={{
          disabled: !canRunWorkflow,
          loading: runPending,
          icon: <CaretRightFilled />,
        }}
      >
        <div style={cardStackStyle}>
          <Typography.Text type="secondary">
            这段输入会作为 <Typography.Text code>$input</Typography.Text> 传入当前行为定义。
          </Typography.Text>
          <Input.TextArea
            aria-label="Studio execution prompt"
            autoSize={{ minRows: 6, maxRows: 10 }}
            placeholder="这次运行要做什么？"
            value={runPrompt}
            onChange={(event) => onRunPromptChange(event.target.value)}
          />
        </div>
      </Modal>
    </div>
  );
};
