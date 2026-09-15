import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  CloseCircleOutlined,
  CloseOutlined,
  CopyOutlined,
  DownOutlined,
  MinusCircleOutlined,
  PauseCircleOutlined,
} from '@ant-design/icons';
import { Alert, Button, Segmented, Tag, Typography } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import {
  type ExecutionLogItem,
  type ExecutionLogStatus,
  type ExecutionTrace,
  formatDurationBetween,
  formatExecutionLogsClipboard,
  normalizeExecutionLogStatus,
  type WorkflowExecutionNodeSnapshot,
} from '@/shared/studio/execution';
import { AevatarLoadingDots } from '@/shared/ui/AevatarLoading';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import {
  type ConsoleToastApi,
  useConsoleToast,
} from '@/shared/ui/ConsoleToast';
import {
  getUserFacingIdentifierLabel,
  sanitizeUserFacingText,
} from '@/shared/ui/userFacingIdentifiers';

export type WorkflowExecutionLogsModel = {
  readonly completedAtUtc: string | null;
  readonly eventCount: number;
  readonly outputText: string;
  readonly startedAtUtc: string | null;
  readonly status: string;
  readonly trace: ExecutionTrace;
  readonly workflowName: string;
};

type WorkflowExecutionLogsPanelProps = {
  readonly activeLogIndex?: number | null;
  readonly ariaLabel?: string;
  readonly collapseButtonRef?: React.Ref<React.ElementRef<typeof Button>>;
  readonly collapseControlsId?: string;
  readonly error?: string;
  readonly execution: WorkflowExecutionLogsModel | null;
  readonly height?: number;
  readonly id?: string;
  readonly onClear?: () => void;
  readonly onCollapse?: () => void;
  readonly onSelectLog?: (index: number | null) => void;
  readonly workflowNodes?: readonly WorkflowExecutionNodeSnapshot[];
};

type OverviewMode = 'nodes' | 'events';
type DetailPanelState = 'both' | 'input' | 'output';
type OverviewEntryType = 'node' | 'run' | 'event';
type ExecutionOverviewStatus = ExecutionLogStatus | 'not-run' | 'pending';

type ExecutionOverviewEntry = {
  readonly category: NonNullable<ExecutionLogItem['category']>;
  readonly completedAt: string;
  readonly entryId: string;
  readonly eventCount: number;
  readonly eventType: string;
  readonly inputText: string;
  readonly interactionText: string;
  readonly logIndex: number;
  readonly logIndexes: readonly number[];
  readonly meta: string;
  readonly outputText: string;
  readonly payloadText: string;
  readonly pendingText: string;
  readonly previewText: string;
  readonly rawText: string;
  readonly rowType: OverviewEntryType;
  readonly startedAt: string;
  readonly status: ExecutionOverviewStatus;
  readonly statusLog?: ExecutionLogItem;
  readonly stepId: string;
  readonly subtitle: string;
  readonly title: string;
};

type MutableExecutionOverviewEntry = {
  category: NonNullable<ExecutionLogItem['category']>;
  completedAt: string;
  entryId: string;
  eventCount: number;
  eventType: string;
  inputText: string;
  interactionText: string;
  logIndex: number;
  logIndexes: number[];
  meta: string;
  outputText: string;
  payloadText: string;
  pendingText: string;
  previewText: string;
  rawText: string;
  rowType: OverviewEntryType;
  startedAt: string;
  status: ExecutionOverviewStatus;
  statusLog?: ExecutionLogItem;
  stepId: string;
  subtitle: string;
  title: string;
};

type ExecutionTokenUsage = {
  readonly completionTokens: number;
  readonly promptTokens: number;
  readonly totalTokens: number;
};

type ExecutionPanelCssVariables = React.CSSProperties &
  Record<`--${string}`, string | number>;

const categoryLabels: Record<
  NonNullable<ExecutionLogItem['category']>,
  string
> = {
  custom: 'Custom',
  lifecycle: 'Run',
  output: 'Output',
  raw: 'Raw',
  snapshot: 'Snapshot',
  step: 'Step',
  usage: 'Usage',
};

const categoryColors: Record<
  NonNullable<ExecutionLogItem['category']>,
  string
> = {
  custom: 'default',
  lifecycle: 'blue',
  output: 'green',
  raw: 'volcano',
  snapshot: 'geekblue',
  step: 'cyan',
  usage: 'gold',
};

const statusColors: Record<ExecutionOverviewStatus, string> = {
  error: 'red',
  'not-run': 'default',
  pending: 'default',
  recorded: 'default',
  running: 'processing',
  success: 'green',
  waiting: 'orange',
};

const nodeDetailBlockHeight = 230;
const overviewRowHeight = 80;
const workflowStudioExecutionPanelCss = `
.workflow-studio-execution-panel__body {
  grid-template-columns: var(--workflow-execution-panel-columns);
}

.workflow-studio-execution-panel__overview {
  border-right: var(--workflow-execution-panel-overview-border-right);
  grid-template-rows: min-content minmax(0, 1fr);
}

@media (max-width: 720px) {
  .workflow-studio-execution-panel__body {
    align-content: start;
    grid-auto-rows: max-content;
    grid-template-columns: minmax(0, 1fr);
    overflow-y: auto;
  }

  .workflow-studio-execution-panel__overview {
    border-bottom: 1px solid #edf2f7;
    border-right: 0;
    grid-template-rows: min-content max-content;
  }

  .workflow-studio-execution-panel__details {
    min-height: 280px;
  }
}
`;

function formatConsoleDateTime(value: string | null | undefined): string {
  if (!value) {
    return '';
  }

  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) {
    return value;
  }

  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

function sanitizeVisibleText(value: string | null | undefined): string {
  return sanitizeUserFacingText(value) || '';
}

function isDetailPaneVisible(
  state: DetailPanelState,
  pane: 'input' | 'output',
): boolean {
  return state === 'both' || state === pane;
}

function toggleDetailPanelState(
  state: DetailPanelState,
  pane: 'input' | 'output',
): DetailPanelState {
  if (pane === 'input') {
    return state === 'output' ? 'both' : state === 'both' ? 'output' : 'input';
  }

  return state === 'input' ? 'both' : state === 'both' ? 'input' : 'output';
}

function isTerminalStepLog(log: ExecutionLogItem | undefined): boolean {
  return log?.tone === 'completed' || log?.tone === 'failed';
}

function readStatusLabel(status: ExecutionOverviewStatus): string {
  switch (status) {
    case 'error':
      return t('teamMemberWorkflowStudio.executionPanel.status.error', 'Error');
    case 'not-run':
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.notRun',
        'Not run',
      );
    case 'pending':
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.pending',
        'Pending',
      );
    case 'recorded':
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.recorded',
        'Recorded',
      );
    case 'success':
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.success',
        'Success',
      );
    case 'waiting':
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.waiting',
        'Waiting',
      );
    default:
      return t(
        'teamMemberWorkflowStudio.executionPanel.status.running',
        'Running',
      );
  }
}

function renderStatusIcon(status: ExecutionOverviewStatus): React.ReactNode {
  switch (status) {
    case 'error':
      return <CloseCircleOutlined style={{ color: '#dc2626' }} />;
    case 'not-run':
      return <MinusCircleOutlined style={{ color: '#94a3b8' }} />;
    case 'pending':
      return <ClockCircleOutlined style={{ color: '#94a3b8' }} />;
    case 'recorded':
      return <CheckCircleOutlined style={{ color: '#64748b' }} />;
    case 'success':
      return <CheckCircleOutlined style={{ color: '#16a34a' }} />;
    case 'waiting':
      return <PauseCircleOutlined style={{ color: '#d97706' }} />;
    default:
      return (
        <span
          data-testid="workflow-execution-running-indicator"
          style={{
            alignItems: 'center',
            display: 'inline-flex',
            height: 18,
            justifyContent: 'center',
            overflow: 'visible',
            width: 18,
          }}
        >
          <AevatarLoadingDots color="#2563eb" decorative gap={2} size="small" />
        </span>
      );
  }
}

function buildOutputText(
  explicitOutputValue: string,
  logs: readonly ExecutionLogItem[],
): string {
  const explicitOutput = String(explicitOutputValue || '').trim();
  if (explicitOutput) {
    return explicitOutput;
  }

  const finishedLog = [...logs]
    .reverse()
    .find((log) => log.category === 'output' && log.clipboardText.trim());
  return finishedLog?.clipboardText.trim() || '';
}

function readJsonObject(text: string): Record<string, unknown> | null {
  try {
    const parsed = JSON.parse(text);
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function readTokenNumber(
  record: Record<string, unknown>,
  ...keys: string[]
): number {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value)) {
      return value;
    }

    if (typeof value === 'string') {
      const parsed = Number(value);
      if (Number.isFinite(parsed)) {
        return parsed;
      }
    }
  }

  return 0;
}

function buildTokenUsage(
  logs: readonly ExecutionLogItem[],
): ExecutionTokenUsage {
  return logs
    .filter((log) => log.category === 'usage' && log.payloadText)
    .reduce(
      (total, log) => {
        const payload = readJsonObject(log.payloadText || '');
        if (!payload) {
          return total;
        }

        return {
          completionTokens:
            total.completionTokens +
            readTokenNumber(payload, 'completionTokens', 'completion_tokens'),
          promptTokens:
            total.promptTokens +
            readTokenNumber(payload, 'promptTokens', 'prompt_tokens'),
          totalTokens:
            total.totalTokens +
            readTokenNumber(payload, 'totalTokens', 'total_tokens'),
        };
      },
      {
        completionTokens: 0,
        promptTokens: 0,
        totalTokens: 0,
      },
    );
}

function buildOverviewEntries(
  logs: readonly ExecutionLogItem[],
): ExecutionOverviewEntry[] {
  const activeEntryIndexByStepId = new Map<string, number>();
  const entries: MutableExecutionOverviewEntry[] = [];

  logs.forEach((log, logIndex) => {
    const category = log.category || 'custom';
    const status = normalizeExecutionLogStatus(log);

    if (category !== 'step' || !log.stepId) {
      const eventStatus: ExecutionLogStatus =
        status === 'error' ? 'error' : 'recorded';
      entries.push({
        category,
        completedAt:
          eventStatus === 'recorded' || eventStatus === 'error'
            ? log.timestamp
            : '',
        entryId: `event:${logIndex}:${log.eventType || log.title}`,
        eventCount: 1,
        eventType: log.eventType || '',
        inputText: '',
        interactionText: '',
        logIndex,
        logIndexes: [logIndex],
        meta: log.meta,
        outputText: category === 'output' ? log.clipboardText.trim() : '',
        payloadText: (log.payloadText || log.clipboardText || '').trim(),
        pendingText: '',
        previewText: log.previewText,
        rawText: (log.rawText || '').trim(),
        rowType: category === 'lifecycle' ? 'run' : 'event',
        startedAt: log.timestamp,
        status: eventStatus,
        statusLog: log,
        stepId: '',
        subtitle: sanitizeVisibleText(log.meta) || categoryLabels[category],
        title: getUserFacingIdentifierLabel(
          log.title,
          categoryLabels[category],
        ),
      });
      return;
    }

    const activeIndex = activeEntryIndexByStepId.get(log.stepId);
    const activeEntry =
      typeof activeIndex === 'number' ? entries[activeIndex] : undefined;

    const createNodeEntry = (): MutableExecutionOverviewEntry => ({
      category,
      completedAt: '',
      entryId: `node:${log.stepId}:${logIndex}`,
      eventCount: 1,
      eventType: log.eventType || '',
      inputText: log.tone === 'started' ? log.clipboardText.trim() : '',
      interactionText: log.tone === 'run' ? log.clipboardText.trim() : '',
      logIndex,
      logIndexes: [logIndex],
      meta: log.meta,
      outputText:
        log.tone === 'completed' || log.tone === 'failed'
          ? log.clipboardText.trim()
          : '',
      payloadText: (log.payloadText || '').trim(),
      pendingText: log.tone === 'pending' ? log.clipboardText.trim() : '',
      previewText: log.previewText,
      rawText: (log.rawText || '').trim(),
      rowType: 'node',
      startedAt: log.timestamp,
      status,
      statusLog: log,
      stepId: log.stepId || '',
      subtitle: sanitizeVisibleText(log.meta) || categoryLabels[category],
      title: getUserFacingIdentifierLabel(
        log.stepId,
        log.title || categoryLabels[category],
      ),
    });

    if (log.tone === 'started') {
      entries.push(createNodeEntry());
      activeEntryIndexByStepId.set(log.stepId, entries.length - 1);
      return;
    }

    if (activeEntry && !isTerminalStepLog(activeEntry.statusLog)) {
      activeEntry.eventCount += 1;
      activeEntry.eventType = log.eventType || activeEntry.eventType;
      activeEntry.logIndex = logIndex;
      activeEntry.logIndexes.push(logIndex);
      activeEntry.meta = log.meta || activeEntry.meta;
      activeEntry.payloadText = (
        log.payloadText || activeEntry.payloadText
      ).trim();
      activeEntry.previewText = log.previewText || activeEntry.previewText;
      activeEntry.rawText = (log.rawText || activeEntry.rawText).trim();
      activeEntry.status = status;
      activeEntry.statusLog = log;
      activeEntry.subtitle =
        sanitizeVisibleText(log.meta) || activeEntry.subtitle;

      if (log.tone === 'pending') {
        activeEntry.pendingText = log.clipboardText.trim();
      }

      if (log.tone === 'run') {
        activeEntry.interactionText = log.clipboardText.trim();
      }

      if (log.tone === 'completed' || log.tone === 'failed') {
        activeEntry.completedAt = log.timestamp;
        activeEntry.outputText = log.clipboardText.trim();
        activeEntryIndexByStepId.delete(log.stepId);
      }
      return;
    }

    const nextEntry = createNodeEntry();
    if (log.tone === 'completed' || log.tone === 'failed') {
      nextEntry.completedAt = log.timestamp;
    } else {
      activeEntryIndexByStepId.set(log.stepId, entries.length);
    }
    entries.push(nextEntry);
  });

  return entries;
}

function buildNodeOverviewEntries(
  entries: readonly ExecutionOverviewEntry[],
  workflowNodes: WorkflowExecutionLogsPanelProps['workflowNodes'],
  runIsTerminal: boolean,
): ExecutionOverviewEntry[] {
  const loggedNodeEntries = entries.filter((entry) => entry.rowType === 'node');
  if (!workflowNodes?.length) {
    return loggedNodeEntries;
  }

  const loggedEntriesByStepId = new Map<string, ExecutionOverviewEntry[]>();
  loggedNodeEntries.forEach((entry) => {
    const matchingEntries = loggedEntriesByStepId.get(entry.stepId) ?? [];
    matchingEntries.push(entry);
    loggedEntriesByStepId.set(entry.stepId, matchingEntries);
  });

  const definitionStepIds = new Set<string>();
  const orderedEntries: ExecutionOverviewEntry[] = [];
  workflowNodes.forEach((node) => {
    const stepId = node.stepId.trim();
    if (!stepId || definitionStepIds.has(stepId)) {
      return;
    }

    definitionStepIds.add(stepId);
    const matchingEntries = loggedEntriesByStepId.get(stepId);
    if (matchingEntries?.length) {
      orderedEntries.push(...matchingEntries);
      return;
    }

    orderedEntries.push({
      category: 'step',
      completedAt: '',
      entryId: `node-definition:${stepId}`,
      eventCount: 0,
      eventType: '',
      inputText: '',
      interactionText: '',
      logIndex: -1,
      logIndexes: [],
      meta: sanitizeVisibleText(node.targetRole),
      outputText: '',
      payloadText: '',
      pendingText: '',
      previewText: '',
      rawText: '',
      rowType: 'node',
      startedAt: '',
      status: runIsTerminal ? 'not-run' : 'pending',
      stepId,
      subtitle:
        sanitizeVisibleText(node.subtitle) ||
        sanitizeVisibleText(node.stepType) ||
        categoryLabels.step,
      title: getUserFacingIdentifierLabel(stepId, node.title || stepId),
    });
  });

  orderedEntries.push(
    ...loggedNodeEntries.filter(
      (entry) => !definitionStepIds.has(entry.stepId),
    ),
  );
  return orderedEntries;
}

function findSelectedEntry(
  entries: readonly ExecutionOverviewEntry[],
  activeLogIndex: number | null | undefined,
): ExecutionOverviewEntry | null {
  if (typeof activeLogIndex !== 'number') {
    return null;
  }

  const direct = entries.find((entry) =>
    entry.logIndexes.includes(activeLogIndex),
  );
  if (direct) {
    return direct;
  }
  return null;
}

function isEntrySelected(
  entry: ExecutionOverviewEntry,
  selectedEntry: ExecutionOverviewEntry | null,
): boolean {
  return entry.entryId === selectedEntry?.entryId;
}

function renderMetric(label: string, value: React.ReactNode): React.ReactNode {
  return (
    <span
      style={{
        alignItems: 'baseline',
        display: 'inline-flex',
        gap: 5,
        whiteSpace: 'nowrap',
      }}
    >
      <Typography.Text style={{ color: '#64748b', fontSize: 11 }}>
        {label}
      </Typography.Text>
      <Typography.Text strong style={{ color: '#111827', fontSize: 12 }}>
        {value}
      </Typography.Text>
    </span>
  );
}

function renderDataBlock(
  label: string,
  value: string,
  emptyText: string,
  options: {
    readonly danger?: boolean;
    readonly height?: number;
    readonly maxHeight?: number;
    readonly testId?: string;
  } = {},
): React.ReactNode {
  const text = sanitizeVisibleText(value);
  const blockHeight = options.height;

  return (
    <div
      style={{
        alignContent: 'start',
        display: 'grid',
        gap: 6,
        minHeight: 0,
        minWidth: 0,
      }}
    >
      <Typography.Text
        style={{
          color: options.danger ? '#b91c1c' : '#64748b',
          fontSize: 11,
          fontWeight: 700,
          textTransform: 'uppercase',
        }}
      >
        {label}
      </Typography.Text>
      <pre
        data-testid={options.testId}
        style={{
          background: text ? '#f8fafc' : '#ffffff',
          border: `1px solid ${options.danger ? '#fecaca' : '#e5e7eb'}`,
          borderRadius: 6,
          color: text ? (options.danger ? '#991b1b' : '#334155') : '#94a3b8',
          fontFamily:
            'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
          fontSize: 12,
          height: blockHeight,
          lineHeight: '18px',
          margin: 0,
          maxHeight: blockHeight ? undefined : (options.maxHeight ?? 180),
          minHeight: 50,
          overflow: 'auto',
          padding: '9px 10px',
          whiteSpace: 'pre-wrap',
          wordBreak: 'break-word',
        }}
      >
        {text || emptyText}
      </pre>
    </div>
  );
}

function formatEntryClipboard(entry: ExecutionOverviewEntry): string {
  const lines = [
    `[${entry.startedAt}] ${entry.title}`,
    entry.subtitle,
    entry.inputText ? `Input:\n${entry.inputText}` : '',
    entry.pendingText ? `Prompt:\n${entry.pendingText}` : '',
    entry.interactionText ? `Interaction:\n${entry.interactionText}` : '',
    entry.outputText ? `Output:\n${entry.outputText}` : '',
    entry.payloadText ? `Payload:\n${entry.payloadText}` : '',
  ].filter(Boolean);

  return lines.join('\n\n');
}

async function copyToClipboard(
  text: string,
  successText: string,
  failureText: string,
  toast: ConsoleToastApi,
): Promise<void> {
  if (!text.trim()) {
    return;
  }

  const clipboard = globalThis.navigator?.clipboard;
  if (!clipboard?.writeText) {
    toast.error(failureText);
    return;
  }

  try {
    await clipboard.writeText(text);
    toast.success(successText);
  } catch {
    toast.error(failureText);
  }
}

function renderOverviewRow(
  entry: ExecutionOverviewEntry,
  selected: boolean,
  onSelectLog?: (index: number | null) => void,
  ref?: React.Ref<HTMLButtonElement>,
): React.ReactNode {
  const selectable = entry.logIndex >= 0;
  const duration =
    entry.startedAt && entry.completedAt
      ? formatDurationBetween(entry.startedAt, entry.completedAt)
      : '';

  return (
    <button
      aria-pressed={selected}
      data-testid={`workflow-execution-log-row-${entry.rowType}-${entry.stepId || entry.logIndex}`}
      disabled={!selectable}
      key={entry.entryId}
      onClick={() => {
        if (selectable) {
          onSelectLog?.(entry.logIndex);
        }
      }}
      ref={ref}
      style={{
        appearance: 'none',
        background: selected ? '#eef4ff' : '#ffffff',
        border: `1px solid ${selected ? '#b7cdfd' : '#e5e7eb'}`,
        borderRadius: 6,
        boxSizing: 'border-box',
        color: 'inherit',
        cursor: selectable ? 'pointer' : 'default',
        display: 'grid',
        gap: 6,
        height: overviewRowHeight,
        minHeight: overviewRowHeight,
        overflow: 'hidden',
        padding: '8px 10px',
        textAlign: 'left',
        width: '100%',
      }}
      type="button"
    >
      <span
        style={{
          alignItems: 'center',
          display: 'grid',
          gap: 8,
          gridTemplateColumns: '18px minmax(0, 1fr) max-content',
          minWidth: 0,
        }}
      >
        {renderStatusIcon(entry.status)}
        <span
          style={{
            display: 'grid',
            gap: 1,
            minWidth: 0,
          }}
        >
          <Typography.Text
            strong
            style={{
              color: '#111827',
              display: 'block',
              fontSize: 12,
              lineHeight: '17px',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {entry.title}
          </Typography.Text>
          <Typography.Text
            style={{
              color: '#64748b',
              fontSize: 11,
              lineHeight: '15px',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {entry.subtitle || categoryLabels[entry.category]}
          </Typography.Text>
        </span>
        <Typography.Text style={{ color: '#94a3b8', fontSize: 11 }}>
          {formatConsoleDateTime(entry.startedAt)}
        </Typography.Text>
      </span>
      <span
        style={{
          alignItems: 'center',
          display: 'flex',
          flexWrap: 'nowrap',
          gap: 5,
          minWidth: 0,
          overflow: 'hidden',
        }}
      >
        <Tag color={statusColors[entry.status]} style={{ marginInlineEnd: 0 }}>
          {readStatusLabel(entry.status)}
        </Tag>
        <Tag
          color={categoryColors[entry.category]}
          style={{ marginInlineEnd: 0 }}
        >
          {categoryLabels[entry.category]}
        </Tag>
        {duration ? (
          <Typography.Text style={{ color: '#64748b', fontSize: 11 }}>
            {duration}
          </Typography.Text>
        ) : null}
        {entry.eventCount > 1 ? (
          <Typography.Text style={{ color: '#64748b', fontSize: 11 }}>
            {t(
              'teamMemberWorkflowStudio.executionPanel.eventCount',
              '{count} events',
              { count: entry.eventCount },
            )}
          </Typography.Text>
        ) : null}
      </span>
    </button>
  );
}

function renderNodeInputPane(
  entry: ExecutionOverviewEntry,
  blockHeight = nodeDetailBlockHeight,
): React.ReactNode {
  const interactionContext = entry.meta.trim();

  return (
    <div
      style={{
        alignContent: 'start',
        alignSelf: 'start',
        display: 'grid',
        gap: 10,
        minHeight: 0,
        minWidth: 0,
      }}
    >
      {renderDataBlock(
        t('teamMemberWorkflowStudio.executionPanel.nodeInput', 'Input'),
        entry.inputText,
        t(
          'teamMemberWorkflowStudio.executionPanel.emptyNodeInput',
          'No input captured for this node.',
        ),
        { height: blockHeight, testId: 'workflow-execution-node-input-block' },
      )}
      {entry.pendingText
        ? renderDataBlock(
            t('teamMemberWorkflowStudio.executionPanel.nodePrompt', 'Prompt'),
            [interactionContext, entry.pendingText]
              .filter(Boolean)
              .join('\n\n'),
            '',
            { maxHeight: Math.max(90, Math.floor(blockHeight / 2)) },
          )
        : null}
      {entry.interactionText
        ? renderDataBlock(
            t(
              'teamMemberWorkflowStudio.executionPanel.nodeInteraction',
              'Interaction',
            ),
            [interactionContext, entry.interactionText]
              .filter(Boolean)
              .join('\n\n'),
            '',
            { maxHeight: Math.max(90, Math.floor(blockHeight / 2)) },
          )
        : null}
    </div>
  );
}

function renderNodeOutputPane(
  entry: ExecutionOverviewEntry,
  blockHeight = nodeDetailBlockHeight,
): React.ReactNode {
  const outputIsError = entry.status === 'error';

  return renderDataBlock(
    outputIsError
      ? t('teamMemberWorkflowStudio.executionPanel.error', 'Error')
      : t('teamMemberWorkflowStudio.executionPanel.nodeOutput', 'Output'),
    entry.outputText,
    t(
      'teamMemberWorkflowStudio.executionPanel.emptyNodeOutput',
      'No output captured for this node.',
    ),
    {
      danger: outputIsError,
      height: blockHeight,
      testId: 'workflow-execution-node-output-block',
    },
  );
}

function renderSelectedDetails(
  entry: ExecutionOverviewEntry | null,
  detailPanelState: DetailPanelState,
): React.ReactNode {
  if (!entry) {
    return (
      <div
        style={{
          alignItems: 'center',
          color: '#64748b',
          display: 'flex',
          fontSize: 12,
          justifyContent: 'center',
          minHeight: 0,
          padding: 16,
          textAlign: 'center',
        }}
      >
        {t(
          'teamMemberWorkflowStudio.executionPanel.selectLog',
          'Select a log entry to inspect its input, output, and raw event data.',
        )}
      </div>
    );
  }

  const jsonText = [
    entry.eventType ? `eventType: ${entry.eventType}` : '',
    entry.payloadText || entry.rawText,
  ]
    .filter(Boolean)
    .join('\n\n');

  if (entry.rowType !== 'node') {
    return renderDataBlock(
      t(
        'teamMemberWorkflowStudio.executionPanel.eventPayload',
        'Event payload',
      ),
      jsonText || sanitizeVisibleText(entry.previewText),
      t(
        'teamMemberWorkflowStudio.executionPanel.emptyEventPayload',
        'No event payload was captured.',
      ),
      { maxHeight: 260 },
    );
  }

  const showInput = isDetailPaneVisible(detailPanelState, 'input');
  const showOutput = isDetailPaneVisible(detailPanelState, 'output');

  return (
    <div
      style={{
        alignItems: 'start',
        display: 'grid',
        gap: 10,
        gridTemplateColumns:
          showInput && showOutput
            ? 'minmax(0, 1fr) minmax(0, 1fr)'
            : 'minmax(0, 1fr)',
        minHeight: 0,
        minWidth: 0,
      }}
    >
      {showInput ? renderNodeInputPane(entry) : null}
      {showOutput ? renderNodeOutputPane(entry) : null}
    </div>
  );
}

const WorkflowExecutionLogsPanel: React.FC<WorkflowExecutionLogsPanelProps> = ({
  activeLogIndex,
  ariaLabel = t(
    'shared.workflowExecutionLogs.consoleAria',
    'Workflow run console',
  ),
  collapseButtonRef,
  collapseControlsId,
  error,
  execution,
  height = 210,
  id,
  onClear,
  onCollapse,
  onSelectLog,
  workflowNodes,
}) => {
  const toast = useConsoleToast();
  const [overviewMode, setOverviewMode] = React.useState<OverviewMode>('nodes');
  const [detailPanelState, setDetailPanelState] =
    React.useState<DetailPanelState>('both');
  const trace = execution?.trace ?? null;
  const logs = trace?.logs ?? [];
  const entries = React.useMemo(() => buildOverviewEntries(logs), [logs]);
  const runStatus: ExecutionLogStatus =
    execution?.status === 'failed'
      ? 'error'
      : execution?.status === 'succeeded' || execution?.status === 'completed'
        ? 'success'
        : 'running';
  const runIsTerminal =
    runStatus !== 'running' || Boolean(execution?.completedAtUtc);
  const nodeEntries = React.useMemo(
    () => buildNodeOverviewEntries(entries, workflowNodes, runIsTerminal),
    [entries, runIsTerminal, workflowNodes],
  );
  const eventEntries = entries.filter((entry) => entry.rowType !== 'node');
  const latestExecutionNodeEntry = React.useMemo(() => {
    const latestStepId = trace?.latestStepId;
    if (!latestStepId) {
      return null;
    }

    for (let index = nodeEntries.length - 1; index >= 0; index -= 1) {
      if (nodeEntries[index]?.stepId === latestStepId) {
        return nodeEntries[index];
      }
    }

    return null;
  }, [nodeEntries, trace?.latestStepId]);
  const liveFollowEntry =
    runStatus === 'running' && !execution?.completedAtUtc
      ? latestExecutionNodeEntry
      : null;
  const controlledSelectedEntry = findSelectedEntry(entries, activeLogIndex);
  const baseSelectedEntry =
    (overviewMode === 'nodes' && controlledSelectedEntry?.rowType !== 'node'
      ? liveFollowEntry
      : controlledSelectedEntry) ||
    liveFollowEntry ||
    entries.find((entry) => entry.status === 'error') ||
    nodeEntries.find((entry) => entry.logIndex >= 0) ||
    entries.find((entry) => entry.logIndex >= 0) ||
    null;
  const hasExecutionContent = Boolean(error || execution);
  const outputText = execution
    ? buildOutputText(execution.outputText, logs)
    : '';
  const tokenUsage = React.useMemo(() => buildTokenUsage(logs), [logs]);
  const duration = execution
    ? formatDurationBetween(execution.startedAtUtc, execution.completedAtUtc)
    : '';
  const totalStepCount = new Set(nodeEntries.map((entry) => entry.stepId)).size;
  const visibleEntries = overviewMode === 'nodes' ? nodeEntries : eventEntries;
  const selectedEntry =
    visibleEntries.find((entry) => isEntrySelected(entry, baseSelectedEntry)) ||
    visibleEntries.find((entry) => entry.logIndex >= 0) ||
    (baseSelectedEntry?.status === 'error' ? baseSelectedEntry : null);
  const selectableEntries = visibleEntries.filter(
    (entry) => entry.logIndex >= 0,
  );
  const overviewRowRefs = React.useRef(new Map<string, HTMLButtonElement>());
  const lastAutoFollowTargetRef = React.useRef('');
  const previousExecutionSessionRef = React.useRef(
    execution?.startedAtUtc || '',
  );
  React.useEffect(() => {
    const executionSession = execution?.startedAtUtc || '';
    if (
      executionSession &&
      previousExecutionSessionRef.current !== executionSession &&
      runStatus === 'running'
    ) {
      setOverviewMode('nodes');
    }
    previousExecutionSessionRef.current = executionSession;
  }, [execution?.startedAtUtc, runStatus]);
  const liveFollowTargetKey = liveFollowEntry
    ? `${execution?.startedAtUtc || ''}:${liveFollowEntry.entryId}`
    : '';
  React.useEffect(() => {
    if (!liveFollowEntry || !onSelectLog) {
      return;
    }

    if (lastAutoFollowTargetRef.current === liveFollowTargetKey) {
      return;
    }

    lastAutoFollowTargetRef.current = liveFollowTargetKey;
    onSelectLog(liveFollowEntry.logIndex);
  }, [liveFollowEntry, liveFollowTargetKey, onSelectLog]);
  React.useEffect(() => {
    if (overviewMode !== 'nodes' || !liveFollowEntry) {
      return;
    }

    overviewRowRefs.current
      .get(liveFollowEntry.entryId)
      ?.scrollIntoView?.({ block: 'nearest' });
  }, [liveFollowTargetKey, overviewMode]);
  const handleOverviewKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLElement>) => {
      if (!selectableEntries.length || !onSelectLog) {
        return;
      }

      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') {
        return;
      }

      event.preventDefault();
      const selectedVisibleIndex = selectableEntries.findIndex((entry) =>
        isEntrySelected(entry, selectedEntry),
      );
      const fallbackIndex =
        event.key === 'ArrowDown' ? -1 : selectableEntries.length;
      const currentIndex =
        selectedVisibleIndex >= 0 ? selectedVisibleIndex : fallbackIndex;
      const nextIndex =
        event.key === 'ArrowDown'
          ? Math.min(currentIndex + 1, selectableEntries.length - 1)
          : Math.max(currentIndex - 1, 0);

      onSelectLog(selectableEntries[nextIndex]?.logIndex ?? null);
    },
    [onSelectLog, selectableEntries, selectedEntry],
  );

  if (!hasExecutionContent) {
    return null;
  }

  return (
    <aside
      aria-label={ariaLabel}
      id={id}
      style={{
        background: '#ffffff',
        borderTop: '1px solid #dbe3ee',
        display: 'grid',
        flex: `0 0 ${height}px`,
        gridTemplateRows: 'min-content minmax(0, 1fr)',
        height,
        minHeight: 0,
      }}
    >
      <section
        data-testid="member-run-result-panel"
        style={{
          display: 'contents',
        }}
      >
        {error && !execution ? (
          <div style={{ padding: '10px 14px' }}>
            <Alert message={error} showIcon type="error" />
          </div>
        ) : execution ? (
          <>
            <div
              style={{
                alignItems: 'center',
                borderBottom: '1px solid #edf2f7',
                display: 'flex',
                flexWrap: 'wrap',
                gap: 12,
                justifyContent: 'space-between',
                minHeight: 42,
                padding: '7px 14px',
              }}
            >
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  flexWrap: 'wrap',
                  gap: 9,
                  minWidth: 0,
                }}
              >
                <Typography.Text
                  strong
                  style={{ color: '#111827', fontSize: 13 }}
                >
                  {t('teamMemberWorkflowStudio.executionPanel.logs', 'Logs')}
                </Typography.Text>
                <Typography.Text
                  style={{
                    color: '#64748b',
                    fontSize: 12,
                    maxWidth: 240,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                >
                  {execution.workflowName}
                </Typography.Text>
                <Tag
                  color={statusColors[runStatus]}
                  style={{ marginInlineEnd: 0 }}
                >
                  {execution.status}
                </Tag>
                {duration
                  ? renderMetric(
                      t(
                        'teamMemberWorkflowStudio.executionPanel.duration',
                        'Duration',
                      ),
                      duration,
                    )
                  : null}
              </div>
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  flexWrap: 'wrap',
                  gap: 10,
                }}
              >
                {renderMetric(
                  t('teamMemberWorkflowStudio.executionPanel.events', 'Events'),
                  execution.eventCount,
                )}
                {renderMetric(
                  t('teamMemberWorkflowStudio.executionPanel.steps', 'Steps'),
                  totalStepCount,
                )}
                {renderMetric(
                  t('teamMemberWorkflowStudio.executionPanel.output', 'Output'),
                  outputText ? '1' : '0',
                )}
                {tokenUsage.totalTokens > 0
                  ? renderMetric(
                      t(
                        'teamMemberWorkflowStudio.executionPanel.tokens',
                        'Tokens',
                      ),
                      tokenUsage.totalTokens,
                    )
                  : null}
                <AevatarTooltip
                  title={t(
                    'teamMemberWorkflowStudio.executionPanel.copyAll',
                    'Copy all logs',
                  )}
                >
                  <Button
                    aria-label={t(
                      'teamMemberWorkflowStudio.executionPanel.copyAll',
                      'Copy all logs',
                    )}
                    icon={<CopyOutlined />}
                    onClick={() =>
                      void copyToClipboard(
                        formatExecutionLogsClipboard(trace),
                        t(
                          'teamMemberWorkflowStudio.executionPanel.copyAllDone',
                          'Copied all logs.',
                        ),
                        t(
                          'teamMemberWorkflowStudio.executionPanel.copyFailed',
                          'Could not copy logs.',
                        ),
                        toast,
                      )
                    }
                    size="small"
                    type="text"
                  />
                </AevatarTooltip>
                {onCollapse ? (
                  <AevatarTooltip
                    title={t(
                      'shared.workflowExecutionLogs.collapse',
                      'Collapse workflow logs',
                    )}
                  >
                    <Button
                      aria-label={t(
                        'shared.workflowExecutionLogs.collapse',
                        'Collapse workflow logs',
                      )}
                      aria-controls={collapseControlsId}
                      aria-expanded={true}
                      icon={<DownOutlined />}
                      onClick={onCollapse}
                      ref={collapseButtonRef}
                      size="small"
                      type="text"
                    />
                  </AevatarTooltip>
                ) : null}
                <AevatarTooltip
                  title={t(
                    'teamMemberWorkflowStudio.executionPanel.clear',
                    'Clear logs',
                  )}
                >
                  <Button
                    aria-label={t(
                      'teamMemberWorkflowStudio.executionPanel.clear',
                      'Clear logs',
                    )}
                    icon={<CloseOutlined />}
                    onClick={onClear}
                    size="small"
                    type="text"
                  />
                </AevatarTooltip>
              </div>
              {error ? (
                <div style={{ flexBasis: '100%' }}>
                  <Alert message={error} showIcon type="error" />
                </div>
              ) : null}
            </div>

            <div
              className="workflow-studio-execution-panel__body"
              style={
                {
                  '--workflow-execution-panel-columns': visibleEntries.length
                    ? 'minmax(300px, 0.82fr) minmax(420px, 1.18fr)'
                    : 'minmax(420px, 1fr)',
                  display: 'grid',
                  gap: 0,
                  minHeight: 0,
                  minWidth: 0,
                } as ExecutionPanelCssVariables
              }
            >
              <section
                className="workflow-studio-execution-panel__overview"
                style={
                  {
                    '--workflow-execution-panel-overview-border-right':
                      visibleEntries.length ? '1px solid #edf2f7' : '0',
                    display: 'grid',
                    minHeight: 0,
                    minWidth: 0,
                    padding: '10px 12px 12px',
                  } as ExecutionPanelCssVariables
                }
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                    marginBottom: 8,
                    minWidth: 0,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 7,
                      minWidth: 0,
                    }}
                  >
                    <ClockCircleOutlined style={{ color: '#64748b' }} />
                    <Typography.Text strong style={{ fontSize: 13 }}>
                      {t(
                        'teamMemberWorkflowStudio.executionPanel.overview',
                        'Overview',
                      )}
                    </Typography.Text>
                  </div>
                  <Segmented
                    onChange={(value) => setOverviewMode(value as OverviewMode)}
                    options={[
                      {
                        label: t(
                          'teamMemberWorkflowStudio.executionPanel.nodes',
                          'Nodes',
                        ),
                        value: 'nodes',
                      },
                      {
                        label: t(
                          'teamMemberWorkflowStudio.executionPanel.events',
                          'Events',
                        ),
                        value: 'events',
                      },
                    ]}
                    size="small"
                    value={overviewMode}
                  />
                </div>
                <div
                  aria-label={t(
                    'teamMemberWorkflowStudio.executionPanel.logsOverview',
                    'Logs overview',
                  )}
                  onKeyDown={handleOverviewKeyDown}
                  role="listbox"
                  tabIndex={0}
                  style={{
                    display: 'grid',
                    gap: 8,
                    alignContent: 'start',
                    gridAutoRows: `${overviewRowHeight}px`,
                    minHeight: 0,
                    overflow: 'auto',
                    paddingRight: 3,
                  }}
                >
                  {visibleEntries.length ? (
                    visibleEntries.map((entry) =>
                      renderOverviewRow(
                        entry,
                        isEntrySelected(entry, selectedEntry),
                        onSelectLog,
                        (element) => {
                          if (element) {
                            overviewRowRefs.current.set(entry.entryId, element);
                          } else {
                            overviewRowRefs.current.delete(entry.entryId);
                          }
                        },
                      ),
                    )
                  ) : (
                    <Typography.Text style={{ color: '#64748b', fontSize: 12 }}>
                      {overviewMode === 'nodes'
                        ? execution.eventCount
                          ? t(
                              'teamMemberWorkflowStudio.executionPanel.rawFrames',
                              '{count} run event(s) received. Waiting for the first node to start.',
                              { count: execution.eventCount },
                            )
                          : t(
                              'teamMemberWorkflowStudio.executionPanel.emptyLogs',
                              'Node logs will appear after the workflow draft runs.',
                            )
                        : t(
                            'teamMemberWorkflowStudio.executionPanel.emptyEvidence',
                            'Runtime events will appear here when the backend emits them.',
                          )}
                    </Typography.Text>
                  )}
                </div>
              </section>

              {visibleEntries.length ? (
                <section
                  aria-label={t(
                    'teamMemberWorkflowStudio.executionPanel.logDetails',
                    'Log details',
                  )}
                  className="workflow-studio-execution-panel__details"
                  style={{
                    display: 'grid',
                    gridTemplateRows: 'min-content minmax(0, 1fr)',
                    minHeight: 0,
                    minWidth: 0,
                    padding: '10px 14px 12px',
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      flexWrap: 'wrap',
                      gap: 8,
                      justifyContent: 'space-between',
                      marginBottom: 8,
                      minWidth: 0,
                    }}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        minWidth: 0,
                      }}
                    >
                      {selectedEntry ? (
                        <>
                          {renderStatusIcon(selectedEntry.status)}
                          <Typography.Text
                            strong
                            style={{
                              fontSize: 13,
                              overflow: 'hidden',
                              textOverflow: 'ellipsis',
                              whiteSpace: 'nowrap',
                            }}
                          >
                            {selectedEntry.title}
                          </Typography.Text>
                          <Tag
                            color={statusColors[selectedEntry.status]}
                            style={{ marginInlineEnd: 0 }}
                          >
                            {readStatusLabel(selectedEntry.status)}
                          </Tag>
                        </>
                      ) : null}
                    </div>
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        flexWrap: 'wrap',
                        gap: 8,
                      }}
                    >
                      {selectedEntry?.rowType === 'node' ? (
                        <div
                          style={{
                            alignItems: 'center',
                            background: '#f8fafc',
                            border: '1px solid #e5e7eb',
                            borderRadius: 6,
                            display: 'inline-flex',
                            gap: 2,
                            padding: 2,
                          }}
                        >
                          {(['input', 'output'] as const).map((pane) => {
                            const active = isDetailPaneVisible(
                              detailPanelState,
                              pane,
                            );
                            const label =
                              pane === 'input'
                                ? t(
                                    'teamMemberWorkflowStudio.executionPanel.nodeInput',
                                    'Input',
                                  )
                                : t(
                                    'teamMemberWorkflowStudio.executionPanel.nodeOutput',
                                    'Output',
                                  );

                            return (
                              <Button
                                aria-pressed={active}
                                key={pane}
                                onClick={() =>
                                  setDetailPanelState((current) =>
                                    toggleDetailPanelState(current, pane),
                                  )
                                }
                                size="small"
                                style={{
                                  background: active
                                    ? '#ffffff'
                                    : 'transparent',
                                  borderColor: active
                                    ? '#dbe3ee'
                                    : 'transparent',
                                  boxShadow: active
                                    ? '0 1px 2px rgba(15, 23, 42, 0.06)'
                                    : 'none',
                                  color: active ? '#111827' : '#64748b',
                                  fontWeight: active ? 600 : 400,
                                }}
                                type="text"
                              >
                                {label}
                              </Button>
                            );
                          })}
                        </div>
                      ) : null}
                      {overviewMode === 'events' && selectedEntry ? (
                        <AevatarTooltip
                          title={t(
                            'teamMemberWorkflowStudio.executionPanel.copySelected',
                            'Copy selected log',
                          )}
                        >
                          <Button
                            aria-label={t(
                              'teamMemberWorkflowStudio.executionPanel.copySelected',
                              'Copy selected log',
                            )}
                            icon={<CopyOutlined />}
                            onClick={() =>
                              void copyToClipboard(
                                formatEntryClipboard(selectedEntry),
                                t(
                                  'teamMemberWorkflowStudio.executionPanel.copySelectedDone',
                                  'Copied selected log.',
                                ),
                                t(
                                  'teamMemberWorkflowStudio.executionPanel.copyFailed',
                                  'Could not copy logs.',
                                ),
                                toast,
                              )
                            }
                            size="small"
                            type="text"
                          />
                        </AevatarTooltip>
                      ) : null}
                    </div>
                  </div>
                  <div
                    style={{
                      minHeight: 0,
                      overflow: 'auto',
                      paddingRight: 2,
                    }}
                  >
                    {renderSelectedDetails(selectedEntry, detailPanelState)}
                  </div>
                </section>
              ) : null}
            </div>
            <style>{workflowStudioExecutionPanelCss}</style>
          </>
        ) : null}
      </section>
    </aside>
  );
};

export default WorkflowExecutionLogsPanel;
