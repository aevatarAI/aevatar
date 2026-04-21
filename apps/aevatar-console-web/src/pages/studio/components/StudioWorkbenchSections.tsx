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
import { describeError } from '@/shared/ui/errorText';

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
    action: 'submit' | 'approve' | 'reject',
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

  React.useEffect(() => {
    setActiveExecutionLogIndex(executionTrace?.defaultLogIndex ?? null);
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [executionTrace, selectedExecutionDetail?.executionId]);

  const currentWorkflowExecutions = React.useMemo(() => {
    const workflowName = activeWorkflowName.trim().toLowerCase();
    const items = executions.data ?? [];
    if (!workflowName) {
      return items;
    }

    return items.filter(
      (item) => item.workflowName.trim().toLowerCase() === workflowName,
    );
  }, [activeWorkflowName, executions.data]);

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
  const executionExecutedSteps = React.useMemo(
    () =>
      new Set(
        (executionTrace?.logs ?? [])
          .map((log) => log.stepId || '')
          .filter(Boolean),
      ).size,
    [executionTrace],
  );
  const executionTotalSteps =
    workflowGraph.steps.length || workflowGraph.nodes.length;
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
    action: 'submit' | 'approve' | 'reject',
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
    const hasRuns = currentWorkflowExecutions.length > 0;

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
                    : `${currentWorkflowExecutions.length} 次运行`}
                </option>
                {currentWorkflowExecutions.map((execution) => (
                  <option key={execution.executionId} value={execution.executionId}>
                    {formatDateTime(execution.startedAtUtc)} · {execution.status}
                  </option>
                ))}
              </select>
            ) : null}

            {executionTrace?.logs?.length ? (
              <button
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
                    : '等待人工输入'}
                </Typography.Text>
                <div style={{ color: '#6b7280', fontSize: 12, marginTop: 4 }}>
                  {activeExecutionInteraction.kind === 'human_approval'
                    ? '查看当前关卡并决定通过或驳回。'
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
