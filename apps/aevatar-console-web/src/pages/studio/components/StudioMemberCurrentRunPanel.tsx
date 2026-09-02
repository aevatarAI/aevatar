import {
  CloseCircleFilled,
  CopyOutlined,
  ExclamationCircleFilled,
  LoadingOutlined,
  ReloadOutlined,
  UnorderedListOutlined,
} from '@ant-design/icons';
import { Button, Tag, Typography } from 'antd';
import React from 'react';
import {
  getStudioInvokeObserveHandoffText,
  type CurrentRunRequest,
  type InvokeResultState,
  type StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import {
  buildExecutionTrace,
  createStudioExecutionFrame,
  formatDurationBetween,
  normalizeExecutionLogStatus,
  type ExecutionLogItem,
  type ExecutionLogStatus,
} from '@/shared/studio/execution';
import type { StudioExecutionDetail } from '@/shared/studio/models';
import {
  parseMarkdownBlocks,
  tokenizeInlineContent,
  type MarkdownBlock,
} from '@/pages/chat/chatContent';
import {
  helperTextStyle,
  studioInvokeColors,
  trimOptional,
} from './studioInvokeUi';
import {
  getUserFacingIdentifierLabel,
  sanitizeUserFacingText,
} from '@/shared/ui/userFacingIdentifiers';
import { t } from "@/shared/i18n/messages";

type RunViewMode = 'latest' | 'historical';
type CurrentRunPresentation = 'default' | 'member-run';

type InvokeRunLogEntry = {
  readonly category: NonNullable<ExecutionLogItem['category']>;
  readonly completedAt: string;
  readonly eventCount: number;
  readonly eventType: string;
  readonly inputText: string;
  readonly logIndex: number;
  readonly meta: string;
  readonly outputText: string;
  readonly pendingText: string;
  readonly previewText: string;
  readonly rawText: string;
  readonly rowType: 'node' | 'event' | 'run';
  readonly startedAt: string;
  readonly status: ExecutionLogStatus;
  readonly statusLog: ExecutionLogItem;
  readonly stepId: string;
  readonly title: string;
};

type MutableInvokeRunLogEntry = {
  category: NonNullable<ExecutionLogItem['category']>;
  completedAt: string;
  eventCount: number;
  eventType: string;
  inputText: string;
  logIndex: number;
  meta: string;
  outputText: string;
  pendingText: string;
  previewText: string;
  rawText: string;
  rowType: 'node' | 'event' | 'run';
  startedAt: string;
  status: ExecutionLogStatus;
  statusLog: ExecutionLogItem;
  stepId: string;
  title: string;
};

type StudioMemberCurrentRunPanelProps = {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
  readonly runViewMode: RunViewMode;
  readonly presentation?: CurrentRunPresentation;
  readonly transcriptViewportRef: React.RefObject<HTMLDivElement | null>;
  readonly onCopyError: () => void;
  readonly onOpenDiagnostics?: () => void;
  readonly onRetryAsNewRun: () => void;
};

function getOutputText(input: {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly invokeResult: InvokeResultState;
}): string {
  if (input.invokeResult.status === 'running') {
    return '';
  }

  const assistantMessage = [...input.chatMessages]
    .reverse()
    .find((message) => message.role === 'assistant');

  return (
    trimOptional(input.invokeResult.finalOutput) ||
    trimOptional(assistantMessage?.content) ||
    trimOptional(input.invokeResult.assistantText)
  );
}

function getInputText(currentRunRequest: CurrentRunRequest | null): string {
  return trimOptional(currentRunRequest?.prompt);
}

function getStatusLabel(status: InvokeResultState['status']): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'success':
      return 'Succeeded';
    case 'error':
      return 'Failed';
    case 'cancelled':
      return 'Cancelled';
    default:
      return 'Idle';
  }
}

function getRunMarker(input: {
  readonly currentRunHasData: boolean;
  readonly presentation: CurrentRunPresentation;
  readonly runViewMode: RunViewMode;
  readonly status: InvokeResultState['status'];
}): string {
  if (!input.currentRunHasData) {
    return input.presentation === 'member-run' ? 'No result' : 'No run';
  }

  if (input.runViewMode === 'historical') {
    return 'Historical run · Read-only';
  }

  if (input.status === 'running') {
    return 'Running';
  }

  return input.presentation === 'member-run'
    ? 'Latest result'
    : 'Latest response';
}

function sanitizeVisibleText(value: string | null | undefined): string {
  return sanitizeUserFacingText(value) || '';
}

function buildStatusSummary(input: {
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
}): string {
  return `${getStatusLabel(input.invokeResult.status)} · ${
    input.runElapsedLabel
  } · ${input.endpointLabel || 'chat'}`;
}

function toIsoTimestamp(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? new Date(value).toISOString()
    : '';
}

function toExecutionStatus(status: InvokeResultState['status']): string {
  switch (status) {
    case 'success':
      return 'succeeded';
    case 'error':
    case 'cancelled':
      return 'failed';
    case 'running':
      return 'running';
    default:
      return 'idle';
  }
}

function buildInvokeExecutionDetail(input: {
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly outputText: string;
}): StudioExecutionDetail | null {
  if (input.invokeResult.events.length === 0) {
    return null;
  }

  const frames = input.invokeResult.events.map(createStudioExecutionFrame);
  const startedAtUtc =
    toIsoTimestamp(input.currentRunRequest?.startedAt) ||
    frames[0]?.receivedAtUtc ||
    new Date().toISOString();
  const terminal =
    input.invokeResult.status === 'success' ||
    input.invokeResult.status === 'error' ||
    input.invokeResult.status === 'cancelled';

  return {
    actorId: trimOptional(input.invokeResult.actorId) || null,
    auditSource: 'invoke-session',
    completedAtUtc: terminal
      ? frames[frames.length - 1]?.receivedAtUtc || new Date().toISOString()
      : null,
    error: trimOptional(input.invokeResult.error) || null,
    executionId:
      trimOptional(input.invokeResult.runId) ||
      trimOptional(input.invokeResult.commandId) ||
      'current-run',
    frames,
    output: input.outputText,
    prompt: trimOptional(input.currentRunRequest?.prompt),
    serviceId: trimOptional(input.invokeResult.serviceId) || null,
    startedAtUtc,
    status: toExecutionStatus(input.invokeResult.status),
    workflowName: input.endpointLabel || 'workflow run',
  };
}

function isTerminalStepLog(log: ExecutionLogItem | undefined): boolean {
  return log?.tone === 'completed' || log?.tone === 'failed';
}

function buildInvokeRunLogEntries(
  logs: readonly ExecutionLogItem[],
): InvokeRunLogEntry[] {
  const activeEntryIndexByStepId = new Map<string, number>();
  const entries: MutableInvokeRunLogEntry[] = [];

  logs.forEach((log, logIndex) => {
    const category = log.category || 'custom';
    const status = normalizeExecutionLogStatus(log);

    if (category !== 'step' || !log.stepId) {
      entries.push({
        category,
        completedAt: status === 'error' ? log.timestamp : '',
        eventCount: 1,
        eventType: log.eventType || '',
        inputText: '',
        logIndex,
        meta: log.meta,
        outputText: category === 'output' ? log.clipboardText.trim() : '',
        pendingText: '',
        previewText: log.previewText,
        rawText: (log.rawText || log.payloadText || log.clipboardText || '').trim(),
        rowType: category === 'lifecycle' ? 'run' : 'event',
        startedAt: log.timestamp,
        status: status === 'error' ? 'error' : 'recorded',
        statusLog: log,
        stepId: '',
        title: log.title,
      });
      return;
    }

    const activeIndex = activeEntryIndexByStepId.get(log.stepId);
    const activeEntry =
      typeof activeIndex === 'number' ? entries[activeIndex] : undefined;
    const createNodeEntry = (): MutableInvokeRunLogEntry => ({
      category,
      completedAt: '',
      eventCount: 1,
      eventType: log.eventType || '',
      inputText: log.tone === 'started' ? log.clipboardText.trim() : '',
      logIndex,
      meta: log.meta,
      outputText:
        log.tone === 'completed' || log.tone === 'failed'
          ? log.clipboardText.trim()
          : '',
      pendingText: log.tone === 'pending' ? log.clipboardText.trim() : '',
      previewText: log.previewText,
      rawText: (log.rawText || log.payloadText || '').trim(),
      rowType: 'node',
      startedAt: log.timestamp,
      status,
      statusLog: log,
      stepId: log.stepId || '',
      title: getUserFacingIdentifierLabel(
        log.stepId,
        log.title || 'Node',
      ),
    });

    if (log.tone === 'started') {
      entries.push(createNodeEntry());
      activeEntryIndexByStepId.set(log.stepId, entries.length - 1);
      return;
    }

    if (activeEntry && !isTerminalStepLog(activeEntry.statusLog)) {
      activeEntry.completedAt =
        log.tone === 'completed' || log.tone === 'failed'
          ? log.timestamp
          : activeEntry.completedAt;
      activeEntry.eventCount += 1;
      activeEntry.eventType = log.eventType || activeEntry.eventType;
      activeEntry.logIndex = logIndex;
      activeEntry.meta = activeEntry.meta || log.meta;
      activeEntry.outputText =
        log.tone === 'completed' || log.tone === 'failed'
          ? log.clipboardText.trim()
          : activeEntry.outputText;
      activeEntry.pendingText =
        log.tone === 'pending'
          ? log.clipboardText.trim()
          : activeEntry.pendingText;
      activeEntry.previewText = log.previewText || activeEntry.previewText;
      activeEntry.rawText = (log.rawText || activeEntry.rawText).trim();
      activeEntry.status = status;
      activeEntry.statusLog = log;
      if (log.tone === 'completed' || log.tone === 'failed') {
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

const runLogCategoryLabels: Record<NonNullable<ExecutionLogItem['category']>, string> = {
  custom: 'Event',
  lifecycle: 'Run',
  output: 'Output',
  raw: 'Raw',
  snapshot: 'Snapshot',
  step: 'Node',
  usage: 'Usage',
};

const runLogStatusColors: Record<ExecutionLogStatus, string> = {
  error: 'red',
  recorded: 'default',
  running: 'processing',
  success: 'green',
  waiting: 'orange',
};

function getRunLogStatusLabel(status: ExecutionLogStatus): string {
  switch (status) {
    case 'error':
      return t('pages.studio.studiomembercurrentrunpanel.log.status.error', 'Error');
    case 'recorded':
      return t(
        'pages.studio.studiomembercurrentrunpanel.log.status.recorded',
        'Recorded',
      );
    case 'success':
      return t(
        'pages.studio.studiomembercurrentrunpanel.log.status.success',
        'Success',
      );
    case 'waiting':
      return t(
        'pages.studio.studiomembercurrentrunpanel.log.status.waiting',
        'Waiting',
      );
    default:
      return t(
        'pages.studio.studiomembercurrentrunpanel.log.status.running',
        'Running',
      );
  }
}

function renderRunLogStatusIcon(status: ExecutionLogStatus): React.ReactNode {
  switch (status) {
    case 'error':
      return <CloseCircleFilled style={{ color: '#dc2626' }} />;
    case 'recorded':
    case 'success':
      return <span style={runLogSuccessDotStyle} />;
    case 'waiting':
      return <ExclamationCircleFilled style={{ color: '#d97706' }} />;
    default:
      return <LoadingOutlined style={{ color: '#2563eb' }} />;
  }
}

function formatRunLogTime(value: string): string {
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

function renderInlineContent(text: string, keyPrefix: string): React.ReactNode {
  return tokenizeInlineContent(text).map((token, index) => {
    const key = `${keyPrefix}-${index}`;
    if (token.kind === 'code') {
      return (
        <code
          key={key}
          style={{
            background: 'rgba(15, 23, 42, 0.06)',
            borderRadius: 6,
            padding: '1px 5px',
          }}
        >
          {token.text}
        </code>
      );
    }

    if (token.kind === 'link') {
      return (
        <a
          key={key}
          href={token.href}
          rel="noreferrer"
          target="_blank"
        >
          {token.text}
        </a>
      );
    }

    return token.bold ? <strong key={key}>{token.text}</strong> : token.text;
  });
}

function renderMarkdownLines(
  lines: readonly string[],
  keyPrefix: string,
): React.ReactNode {
  return lines.map((line, index) => (
    <React.Fragment key={`${keyPrefix}-${index}`}>
      {index > 0 ? <br /> : null}
      {renderInlineContent(line, `${keyPrefix}-inline-${index}`)}
    </React.Fragment>
  ));
}

function renderMarkdownBlock(
  block: MarkdownBlock,
  index: number,
): React.ReactNode {
  switch (block.kind) {
    case 'heading':
      return (
        <div key={index} style={markdownHeadingStyle(block.level)}>
          {renderInlineContent(block.text, `heading-${index}`)}
        </div>
      );
    case 'unordered-list':
      return (
        <ul key={index} style={markdownListStyle}>
          {block.items.map((item, itemIndex) => (
            <li key={`${index}-${itemIndex}`}>
              {renderInlineContent(item, `ul-${index}-${itemIndex}`)}
            </li>
          ))}
        </ul>
      );
    case 'ordered-list':
      return (
        <ol key={index} style={markdownListStyle}>
          {block.items.map((item, itemIndex) => (
            <li key={`${index}-${itemIndex}`}>
              {renderInlineContent(item, `ol-${index}-${itemIndex}`)}
            </li>
          ))}
        </ol>
      );
    case 'blockquote':
      return (
        <blockquote
          key={index}
          style={{
            borderLeft: '3px solid #cbd5e1',
            color: '#475569',
            margin: '0 0 12px',
            padding: '2px 0 2px 12px',
          }}
        >
          {renderMarkdownLines(block.lines, `quote-${index}`)}
        </blockquote>
      );
    case 'table':
      return renderMarkdownTable(block, index);
    case 'code':
      return (
        <pre key={index} style={markdownCodeStyle}>
          {block.code}
        </pre>
      );
    case 'thematic-break':
      return (
        <div
          key={index}
          style={{ borderTop: '1px solid #dbe3ee', margin: '14px 0' }}
        />
      );
    case 'paragraph':
      return (
        <div key={index} style={markdownParagraphStyle}>
          {renderMarkdownLines(block.lines, `paragraph-${index}`)}
        </div>
      );
  }
}

function renderMarkdownTable(
  block: Extract<MarkdownBlock, { kind: 'table' }>,
  index: number,
) {
  return (
    <div key={`table-${index}`} style={markdownTableWrapperStyle}>
      <table style={markdownTableStyle}>
        <thead>
          <tr>
            {block.headers.map((cell, cellIndex) => (
              <th
                key={cellIndex}
                scope="col"
                style={{
                  ...markdownTableHeaderCellStyle,
                  textAlign: block.alignments[cellIndex] ?? 'left',
                }}
              >
                {renderInlineContent(cell, `table-${index}-head-${cellIndex}`)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {block.rows.map((row, rowIndex) => (
            <tr key={rowIndex}>
              {block.headers.map((_, cellIndex) => (
                <td
                  key={cellIndex}
                  style={{
                    ...markdownTableCellStyle,
                    textAlign: block.alignments[cellIndex] ?? 'left',
                  }}
                >
                  {renderInlineContent(
                    row[cellIndex] ?? '',
                    `table-${index}-${rowIndex}-${cellIndex}`,
                  )}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function renderRunOutputContent(text: string): React.ReactNode {
  const blocks = parseMarkdownBlocks(text);
  if (blocks.length === 0) {
    return null;
  }

  return (
    <div style={renderedOutputStyle}>
      {blocks.map((block, index) => renderMarkdownBlock(block, index))}
    </div>
  );
}

const panelStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const headerStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '0 0 auto',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
  minWidth: 0,
  paddingBottom: 8,
};

const markerStyle: React.CSSProperties = {
  background: '#eef6ff',
  border: '1px solid #bfdbfe',
  borderRadius: 999,
  color: '#1d4ed8',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 800,
  lineHeight: '18px',
  padding: '3px 9px',
};

const summaryStyle: React.CSSProperties = {
  color: '#334155',
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
  minWidth: 0,
};

const outputPaneStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  minWidth: 0,
};

const sectionStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 10,
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const responseSectionStyle: React.CSSProperties = {
  ...sectionStyle,
  background: '#ffffff',
  borderColor: '#cbd5e1',
  boxShadow: 'inset 0 1px 0 rgba(15, 23, 42, 0.03)',
  minHeight: 150,
  padding: '18px 20px',
};

const sectionLabelStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0,
  lineHeight: '16px',
  textTransform: 'uppercase',
};

const bodyTextStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 14,
  lineHeight: 1.7,
  margin: 0,
  overflowWrap: 'anywhere',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const renderedOutputStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 14,
  lineHeight: 1.75,
  minWidth: 0,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const markdownHeadingStyle = (level: number): React.CSSProperties => ({
  color: '#0f172a',
  fontSize: Math.max(18 - (level - 1) * 1.5, 14),
  fontWeight: 800,
  lineHeight: 1.35,
  margin: level <= 3 ? '18px 0 8px' : '14px 0 6px',
});

const markdownListStyle: React.CSSProperties = {
  margin: '0 0 12px',
  paddingLeft: 22,
};

const markdownParagraphStyle: React.CSSProperties = {
  margin: '0 0 12px',
};

const markdownCodeStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 10,
  fontFamily:
    "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace",
  fontSize: 13,
  margin: '8px 0 12px',
  overflowX: 'auto',
  padding: '12px 14px',
  whiteSpace: 'pre-wrap',
};

const markdownTableWrapperStyle: React.CSSProperties = {
  border: '1px solid #dbe3ee',
  borderRadius: 10,
  margin: '8px 0 14px',
  overflowX: 'auto',
};

const markdownTableStyle: React.CSSProperties = {
  borderCollapse: 'collapse',
  fontSize: 13,
  minWidth: '100%',
};

const markdownTableHeaderCellStyle: React.CSSProperties = {
  background: '#f8fafc',
  borderBottom: '1px solid #dbe3ee',
  color: '#475569',
  fontWeight: 800,
  padding: '10px 12px',
  textAlign: 'left',
  whiteSpace: 'nowrap',
};

const markdownTableCellStyle: React.CSSProperties = {
  borderTop: '1px solid #eef2f7',
  padding: '10px 12px',
  verticalAlign: 'top',
};

const emptyStateStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'linear-gradient(180deg, #ffffff 0%, #f8fafc 100%)',
  border: '1px dashed #cbd5e1',
  borderRadius: 12,
  color: '#64748b',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  fontSize: 14,
  gap: 6,
  justifyContent: 'center',
  lineHeight: 1.7,
  minHeight: 280,
  minWidth: 0,
  padding: 18,
  textAlign: 'center',
};

const emptyTitleStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 18,
  fontWeight: 800,
  lineHeight: '26px',
};

const errorActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  marginTop: 10,
};

const recoveryPathStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 10,
  color: '#475569',
  display: 'grid',
  gap: 4,
  minWidth: 0,
  padding: '12px 14px',
};

const errorCardStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: studioInvokeColors.dangerSoft,
  border: `1px solid ${studioInvokeColors.dangerBorder}`,
  borderRadius: 8,
  display: 'grid',
  gap: 12,
  gridTemplateColumns: '28px minmax(0, 1fr)',
  minWidth: 0,
  padding: '16px 18px',
};

const warningCardStyle: React.CSSProperties = {
  ...errorCardStyle,
  background: '#fff7ed',
  border: '1px solid #fed7aa',
};

const errorIconStyle: React.CSSProperties = {
  color: '#ff4d4f',
  fontSize: 22,
  lineHeight: '28px',
};

const warningIconStyle: React.CSSProperties = {
  color: '#f59e0b',
  fontSize: 22,
  lineHeight: '28px',
};

const errorTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 16,
  fontWeight: 800,
  lineHeight: '24px',
  marginBottom: 6,
  minWidth: 0,
};

const errorDescriptionStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 14,
  lineHeight: 1.7,
  margin: 0,
  minWidth: 0,
  overflowWrap: 'anywhere',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const memberRunPanelStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  minHeight: 0,
  minWidth: 0,
};

const memberRunHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'space-between',
  minWidth: 0,
};

const memberRunTitleStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 18,
  fontWeight: 800,
  lineHeight: '26px',
};

const memberRunStatusClusterStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
  minWidth: 0,
};

const memberRunInputReceiptStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#f8fafc',
  border: '1px solid #e2e8f0',
  borderRadius: 10,
  display: 'grid',
  gap: 5,
  minWidth: 0,
  padding: '10px 12px',
};

const memberRunInputLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0,
  lineHeight: '14px',
  textTransform: 'uppercase',
};

const memberRunInputTextStyle: React.CSSProperties = {
  ...bodyTextStyle,
  color: '#334155',
  fontSize: 13,
  lineHeight: '19px',
  maxHeight: 76,
  overflow: 'hidden',
};

const memberRunCanvasStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e2e8f0',
  borderRadius: 12,
  boxShadow: 'inset 0 1px 0 rgba(15, 23, 42, 0.03)',
  display: 'grid',
  gap: 12,
  minHeight: 280,
  minWidth: 0,
  padding: '20px 22px',
};

const memberRunEmptyCanvasStyle: React.CSSProperties = {
  ...emptyStateStyle,
  background: '#ffffff',
  border: '1px dashed #cbd5e1',
  minHeight: 300,
};

const memberRunCanvasLabelStyle: React.CSSProperties = {
  ...sectionLabelStyle,
  color: '#475569',
};

const runLogPanelStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 12,
  display: 'grid',
  gap: 10,
  minWidth: 0,
  padding: '12px 14px',
};

const runLogHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'space-between',
  minWidth: 0,
};

const runLogHeaderTextStyle: React.CSSProperties = {
  display: 'grid',
  gap: 2,
  minWidth: 0,
};

const runLogTitleStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 14,
  fontWeight: 800,
  lineHeight: '20px',
};

const runLogSubtleStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 12,
  lineHeight: '17px',
};

const runLogListStyle: React.CSSProperties = {
  border: '1px solid #e2e8f0',
  borderRadius: 10,
  display: 'grid',
  gap: 8,
  maxHeight: 'min(520px, 58vh)',
  minWidth: 0,
  overflowY: 'auto',
  padding: 8,
  scrollbarGutter: 'stable',
};

const runLogEntryStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e2e8f0',
  borderRadius: 10,
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '10px 12px',
};

const runLogEntryHeaderStyle: React.CSSProperties = {
  alignItems: 'start',
  display: 'grid',
  gap: 8,
  gridTemplateColumns: '18px minmax(0, 1fr) max-content',
  minWidth: 0,
};

const runLogEntryTitleStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 13,
  fontWeight: 800,
  lineHeight: '18px',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const runLogEntryMetaStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 12,
  lineHeight: '17px',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const runLogTagsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
  minWidth: 0,
};

const runLogDetailsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
  minWidth: 0,
};

const runLogDetailCollapseStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e2e8f0',
  borderRadius: 8,
  minWidth: 0,
  overflow: 'hidden',
};

const runLogDetailSummaryStyle: React.CSSProperties = {
  alignItems: 'center',
  color: '#334155',
  cursor: 'pointer',
  display: 'grid',
  flexWrap: 'wrap',
  fontSize: 12,
  fontWeight: 800,
  gap: 8,
  gridTemplateColumns: '16px minmax(0, 1fr) max-content',
  lineHeight: '18px',
  minWidth: 0,
  padding: '7px 9px',
};

const runLogDetailArrowStyle: React.CSSProperties = {
  color: '#64748b',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 900,
  justifyContent: 'center',
  lineHeight: '18px',
  transform: 'rotate(0deg)',
  transition: 'transform 120ms ease',
  width: 16,
};

const runLogDetailSummaryMetaStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 700,
  lineHeight: '16px',
};

const runLogDetailContentStyle: React.CSSProperties = {
  borderTop: '1px solid #e2e8f0',
  padding: 8,
};

const runLogDetailDisclosureCss = `
.studio-invoke-run-log-detail > summary::-webkit-details-marker {
  display: none;
}

.studio-invoke-run-log-detail > summary::marker {
  content: "";
}

.studio-invoke-run-log-detail[open] .studio-invoke-run-log-detail-arrow {
  transform: rotate(90deg);
}
`;

const runLogSnippetStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  display: 'grid',
  gap: 5,
  minWidth: 0,
  padding: '8px 9px',
};

const runLogSnippetLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 10,
  fontWeight: 800,
  letterSpacing: 0,
  lineHeight: '13px',
  textTransform: 'uppercase',
};

const runLogSnippetTextStyle: React.CSSProperties = {
  color: '#334155',
  fontFamily:
    "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, 'Liberation Mono', monospace",
  fontSize: 12,
  lineHeight: '17px',
  margin: 0,
  maxHeight: 116,
  minHeight: 0,
  overflow: 'auto',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const runLogEmptyStyle: React.CSSProperties = {
  border: '1px dashed #cbd5e1',
  borderRadius: 10,
  color: '#64748b',
  fontSize: 13,
  lineHeight: '19px',
  padding: '14px',
  textAlign: 'center',
};

const runLogSuccessDotStyle: React.CSSProperties = {
  background: '#16a34a',
  borderRadius: 999,
  display: 'inline-block',
  height: 10,
  margin: '4px',
  width: 10,
};

function renderRunLogSnippet(
  label: string,
  value: string,
  options: { readonly danger?: boolean; readonly keyName?: string } = {},
): React.ReactNode {
  const text = sanitizeVisibleText(value);
  if (!text) {
    return null;
  }

  return (
    <div
      key={options.keyName}
      style={{
        ...runLogSnippetStyle,
        borderColor: options.danger ? '#fecaca' : '#e5e7eb',
      }}
    >
      <span
        style={{
          ...runLogSnippetLabelStyle,
          color: options.danger ? '#b91c1c' : runLogSnippetLabelStyle.color,
        }}
      >
        {label}
      </span>
      <pre
        style={{
          ...runLogSnippetTextStyle,
          color: options.danger ? '#991b1b' : runLogSnippetTextStyle.color,
        }}
      >
        {text}
      </pre>
    </div>
  );
}

function renderRunLogDetailCollapse(
  entry: InvokeRunLogEntry,
  details: readonly React.ReactNode[],
): React.ReactNode {
  if (!details.length) {
    return null;
  }

  const capturedSections = [
    entry.inputText ? 'Input' : '',
    entry.outputText ? 'Output' : '',
    entry.pendingText ? 'Waiting' : '',
  ].filter(Boolean);

  return (
    <details
      className="studio-invoke-run-log-detail"
      data-testid={`studio-invoke-run-log-details-${entry.stepId || entry.logIndex}`}
      style={runLogDetailCollapseStyle}
    >
      <summary style={runLogDetailSummaryStyle}>
        <span
          aria-hidden="true"
          className="studio-invoke-run-log-detail-arrow"
          style={runLogDetailArrowStyle}
        >
          ▸
        </span>
        <span style={{ minWidth: 0 }}>
          {t(
            'pages.studio.studiomembercurrentrunpanel.input.output.details',
            'Input / Output',
          )}
        </span>
        <span style={runLogDetailSummaryMetaStyle}>
          {capturedSections.join(' · ') ||
            t(
              'pages.studio.studiomembercurrentrunpanel.details.available',
              'Details available',
            )}
        </span>
      </summary>
      <div style={runLogDetailContentStyle}>
        <div style={runLogDetailsGridStyle}>{details}</div>
      </div>
    </details>
  );
}

function renderRunLogEntry(entry: InvokeRunLogEntry): React.ReactNode {
  const duration = entry.completedAt
    ? formatDurationBetween(entry.startedAt, entry.completedAt)
    : '';
  const entryTitle = getUserFacingIdentifierLabel(entry.title, 'Node');
  const entryMeta = sanitizeVisibleText(entry.meta) || runLogCategoryLabels[entry.category];
  const details =
    entry.rowType === 'node'
      ? [
          renderRunLogSnippet(
            t('pages.studio.studiomembercurrentrunpanel.node.input', 'Input'),
            entry.inputText,
            { keyName: 'input' },
          ),
          renderRunLogSnippet(
            entry.status === 'error'
              ? t('pages.studio.studiomembercurrentrunpanel.node.error', 'Error')
              : t('pages.studio.studiomembercurrentrunpanel.node.output', 'Output'),
            entry.outputText,
            { danger: entry.status === 'error', keyName: 'output' },
          ),
          renderRunLogSnippet(
            t('pages.studio.studiomembercurrentrunpanel.node.waiting', 'Waiting'),
            entry.pendingText,
            { keyName: 'waiting' },
          ),
        ].filter(Boolean)
      : [];

  return (
    <div
      data-testid={`studio-invoke-run-log-${entry.rowType}-${entry.stepId || entry.logIndex}`}
      key={`${entry.rowType}-${entry.stepId || entry.logIndex}-${entry.eventType}`}
      style={runLogEntryStyle}
    >
      <div style={runLogEntryHeaderStyle}>
        {renderRunLogStatusIcon(entry.status)}
        <span style={{ display: 'grid', gap: 1, minWidth: 0 }}>
          <span style={runLogEntryTitleStyle}>{entryTitle}</span>
          <span style={runLogEntryMetaStyle}>
            {entryMeta}
          </span>
        </span>
        <span style={runLogSubtleStyle}>{formatRunLogTime(entry.startedAt)}</span>
      </div>
      <div style={runLogTagsStyle}>
        <Tag color={runLogStatusColors[entry.status]} style={{ marginInlineEnd: 0 }}>
          {getRunLogStatusLabel(entry.status)}
        </Tag>
        <Tag color={entry.rowType === 'node' ? 'cyan' : 'default'} style={{ marginInlineEnd: 0 }}>
          {entry.rowType === 'node'
            ? t('pages.studio.studiomembercurrentrunpanel.node', 'Node')
            : runLogCategoryLabels[entry.category]}
        </Tag>
        {duration ? <span style={runLogSubtleStyle}>{duration}</span> : null}
        {entry.eventCount > 1 ? (
          <span style={runLogSubtleStyle}>
            {t(
              'pages.studio.studiomembercurrentrunpanel.events.count',
              'Events ({count})',
              { count: entry.eventCount },
            )}
          </span>
        ) : null}
      </div>
      {renderRunLogDetailCollapse(entry, details)}
    </div>
  );
}

type RunLogsViewProps = {
  readonly entries: readonly InvokeRunLogEntry[];
  readonly eventCount: number;
  readonly status: InvokeResultState['status'];
};

const RUN_LOG_STICKY_BOTTOM_THRESHOLD_PX = 32;

const RunLogsView: React.FC<RunLogsViewProps> = ({
  entries,
  eventCount,
  status,
}) => {
  const scrollRef = React.useRef<HTMLDivElement | null>(null);
  const shouldStickToBottomRef = React.useRef(true);
  const nodeEntries = entries.filter((entry) => entry.rowType === 'node');
  const visibleEntries = nodeEntries.length ? nodeEntries : entries.slice(-6);
  const latestEntryKey =
    visibleEntries.length > 0
      ? `${visibleEntries[visibleEntries.length - 1].rowType}:${
          visibleEntries[visibleEntries.length - 1].stepId ||
          visibleEntries[visibleEntries.length - 1].logIndex
        }:${visibleEntries[visibleEntries.length - 1].eventCount}:${
          visibleEntries[visibleEntries.length - 1].status
        }`
      : '';

  React.useLayoutEffect(() => {
    const scrollElement = scrollRef.current;
    if (!scrollElement || !shouldStickToBottomRef.current) {
      return;
    }

    scrollElement.scrollTop = scrollElement.scrollHeight;
  }, [latestEntryKey, visibleEntries.length]);

  const handleRunLogScroll = React.useCallback(() => {
    const scrollElement = scrollRef.current;
    if (!scrollElement) {
      return;
    }

    const distanceFromBottom =
      scrollElement.scrollHeight -
      scrollElement.scrollTop -
      scrollElement.clientHeight;
    shouldStickToBottomRef.current =
      distanceFromBottom <= RUN_LOG_STICKY_BOTTOM_THRESHOLD_PX;
  }, []);

  return (
    <section data-testid="studio-invoke-run-logs" style={runLogPanelStyle}>
      <style>{runLogDetailDisclosureCss}</style>
      <div style={runLogHeaderStyle}>
        <span style={runLogHeaderTextStyle}>
          <span style={runLogTitleStyle}>
            {t(
              'pages.studio.studiomembercurrentrunpanel.run.logs',
              'Run logs',
            )}
          </span>
          <span style={runLogSubtleStyle}>
            {nodeEntries.length
              ? t(
                  'pages.studio.studiomembercurrentrunpanel.node.logs.summary',
                  '{nodes} node(s) · {events} event(s)',
                  { events: eventCount, nodes: nodeEntries.length },
                )
              : t(
                  'pages.studio.studiomembercurrentrunpanel.event.logs.summary',
                  '{events} event(s) received',
                  { events: eventCount },
                )}
          </span>
        </span>
        {status === 'running' ? (
          <Tag color="processing" style={{ marginInlineEnd: 0 }}>
            {t(
              'pages.studio.studiomembercurrentrunpanel.live',
              'Live',
            )}
          </Tag>
        ) : null}
      </div>
      {visibleEntries.length ? (
        <div
          data-testid="studio-invoke-run-log-scroll"
          onScroll={handleRunLogScroll}
          ref={scrollRef}
          style={runLogListStyle}
        >
          {visibleEntries.map(renderRunLogEntry)}
        </div>
      ) : (
        <div style={runLogEmptyStyle}>
          {status === 'running'
            ? t(
                'pages.studio.studiomembercurrentrunpanel.waiting.for.node.logs',
                'Waiting for node logs from the workflow runtime.',
              )
            : t(
                'pages.studio.studiomembercurrentrunpanel.no.node.logs',
                'No node logs were captured for this run.',
              )}
        </div>
      )}
    </section>
  );
};

const StudioMemberCurrentRunPanel: React.FC<
  StudioMemberCurrentRunPanelProps
> = ({
  chatMessages,
  currentRunHasData,
  currentRunRequest,
  endpointLabel,
  invokeResult,
  onCopyError,
  onOpenDiagnostics,
  onRetryAsNewRun,
  presentation = 'default',
  runElapsedLabel,
  runViewMode,
  transcriptViewportRef,
}) => {
  const outputText = getOutputText({ chatMessages, invokeResult });
  const inputText = getInputText(currentRunRequest);
  const statusSummary = buildStatusSummary({
    endpointLabel,
    invokeResult,
    runElapsedLabel,
  });
  const marker = getRunMarker({
    currentRunHasData,
    presentation,
    runViewMode,
    status: invokeResult.status,
  });
  const errorDescription =
    invokeResult.errorCode && invokeResult.error
      ? `${invokeResult.error}（${invokeResult.errorCode}）`
      : invokeResult.errorCode || invokeResult.error;
  const observeHandoffText = getStudioInvokeObserveHandoffText({
    mode: invokeResult.mode,
    runViewMode,
    status: invokeResult.status,
  });
  const executionDetail = React.useMemo(
    () =>
      buildInvokeExecutionDetail({
        currentRunRequest,
        endpointLabel,
        invokeResult,
        outputText,
      }),
    [currentRunRequest, endpointLabel, invokeResult, outputText],
  );
  const executionTrace = React.useMemo(
    () => buildExecutionTrace(executionDetail),
    [executionDetail],
  );
  const runLogEntries = React.useMemo(
    () => buildInvokeRunLogEntries(executionTrace?.logs ?? []),
    [executionTrace],
  );
  const shouldShowRunLogs =
    currentRunHasData &&
    (invokeResult.status === 'running' || invokeResult.events.length > 0);
  const shouldShowObserveHandoff =
    runViewMode === 'latest' && Boolean(observeHandoffText);
  const openDiagnostics = onOpenDiagnostics ?? (() => {});
  const renderCurrentRunLogs = () =>
    shouldShowRunLogs
      ? (
          <RunLogsView
            entries={runLogEntries}
            eventCount={invokeResult.eventCount || invokeResult.events.length}
            status={invokeResult.status}
          />
        )
      : null;

  const renderMemberRunInputReceipt = () =>
    inputText ? (
      <div style={memberRunInputReceiptStyle}>
        <span style={memberRunInputLabelStyle}>
          {t(
            "pages.studio.studiomembercurrentrunpanel.submitted.input",
            "Submitted input",
          )}
        </span>
        <p style={memberRunInputTextStyle}>{inputText}</p>
      </div>
    ) : null;

  const renderMemberRunOutput = () => {
    if (!currentRunHasData) {
      return (
        <div style={memberRunEmptyCanvasStyle}>
          <div style={emptyTitleStyle}>
            {t(
              "pages.studio.studiomembercurrentrunpanel.no.run.result.yet",
              "No run result yet",
            )}
          </div>
          <div>
            {t(
              "pages.studio.studiomembercurrentrunpanel.start.run.to.see.result",
              "Start a run to see the result here.",
            )}
          </div>
        </div>
      );
    }

    if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      const isCancelled = invokeResult.status === 'cancelled';
      return (
        <div style={outputPaneStyle}>
          {renderMemberRunInputReceipt()}
          <div style={isCancelled ? warningCardStyle : errorCardStyle}>
            {isCancelled ? (
              <ExclamationCircleFilled style={warningIconStyle} />
            ) : (
              <CloseCircleFilled style={errorIconStyle} />
            )}
            <div style={{ minWidth: 0 }}>
              <div style={errorTitleStyle}>
                {isCancelled
                  ? t(
                      "pages.studio.studiomembercurrentrunpanel.run.stopped",
                      "Run stopped",
                    )
                  : t(
                      "pages.studio.studiomembercurrentrunpanel.run.failed",
                      "Run failed",
                    )}
              </div>
              <p style={errorDescriptionStyle}>
                {errorDescription ||
                  (isCancelled
                    ? t(
                        "pages.studio.studiomembercurrentrunpanel.the.run.has.stopped",
                        "The run has stopped and only partial output may currently be displayed.",
                      )
                    : t(
                        "pages.studio.studiomembercurrentrunpanel.run.failed.without.message",
                        "This run failed without an additional error message.",
                      ))}
              </p>
            </div>
          </div>
          {renderCurrentRunLogs()}
          {outputText ? (
            <div
              data-testid="studio-invoke-chat-transcript"
              ref={transcriptViewportRef}
              style={memberRunCanvasStyle}
            >
              <span style={memberRunCanvasLabelStyle}>
                {isCancelled
                  ? t(
                      "pages.studio.studiomembercurrentrunpanel.partial.output",
                      "Partial output",
                    )
                  : t(
                      "pages.studio.studiomembercurrentrunpanel.result",
                      "Result",
                    )}
              </span>
              {renderRunOutputContent(outputText)}
            </div>
          ) : null}
          <div
            data-testid="studio-invoke-recovery-path"
            style={recoveryPathStyle}
          >
            <span style={sectionLabelStyle}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.recovery.path",
                "Recovery path",
              )}
            </span>
            <Typography.Text style={helperTextStyle}>
              {isCancelled
                ? t(
                    "pages.studio.studiomembercurrentrunpanel.this.stopped.run.stays.in.history",
                    "This stopped run stays in history. Retry as a new run when you want fresh output, or switch to Observe to inspect the latest backend events.",
                  )
                : t(
                    "pages.studio.studiomembercurrentrunpanel.this.failed.only.the.invoke.run.open.diagnostics",
                    "This run failed. Retry with a smaller request, open diagnostics for backend signals, or edit the member contract from its owning member surface.",
                  )}
            </Typography.Text>
          </div>
          <div style={errorActionsStyle}>
            <Button icon={<UnorderedListOutlined />} onClick={openDiagnostics}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.open.diagnostics",
                "Open diagnostics",
              )}
            </Button>
            <Button icon={<CopyOutlined />} onClick={onCopyError}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.copy.error",
                "Copy error",
              )}
            </Button>
            <Button icon={<ReloadOutlined />} onClick={onRetryAsNewRun}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.retry.as.new.run",
                "Retry as new run",
              )}
            </Button>
          </div>
        </div>
      );
    }

    return (
      <div style={outputPaneStyle}>
        {renderMemberRunInputReceipt()}
        {renderCurrentRunLogs()}
        <div
          data-testid="studio-invoke-chat-transcript"
          ref={transcriptViewportRef}
          style={memberRunCanvasStyle}
        >
          <span style={memberRunCanvasLabelStyle}>
            {t(
              "pages.studio.studiomembercurrentrunpanel.current.result",
              "Current result",
            )}
          </span>
          {invokeResult.status === 'running' && !outputText ? (
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomembercurrentrunpanel.waiting.for.output",
                "Waiting for a response...",
              )}
            </Typography.Text>
          ) : outputText ? (
            renderRunOutputContent(outputText)
          ) : invokeResult.status === 'success' ? (
            <div style={helperTextStyle}>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.no.displayable.content.returned",
                  "No readable response returned.",
                )}
              </div>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.the.run.ended.successfully",
                  "The run ended successfully, but it did not return user-visible content.",
                )}
              </div>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.you.can.view.events",
                  "Open diagnostics when you need event or payload evidence.",
                )}
              </div>
            </div>
          ) : (
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomembercurrentrunpanel.waiting.for.output.2",
                "Waiting for a response...",
              )}
            </Typography.Text>
          )}
        </div>
      </div>
    );
  };

  const renderOutput = () => {
    if (!currentRunHasData) {
      return (
        <div style={emptyStateStyle}>
          <div style={emptyTitleStyle}>
            {presentation === 'member-run'
              ? t(
                  "pages.studio.studiomembercurrentrunpanel.no.run.result.yet",
                  "No run result yet",
                )
              : t(
                  "pages.studio.studiomembercurrentrunpanel.no.run.yet",
                  "No run yet",
                )}
          </div>
          <div>
            {presentation === 'member-run'
              ? t(
                  "pages.studio.studiomembercurrentrunpanel.start.run.to.see.result",
                  "Start a run to see the result here.",
                )
              : t(
                  "pages.studio.studiomembercurrentrunpanel.send.prompt.above.to.create",
                  "Send a request above to create the first run.",
                )}
          </div>
        </div>
      );
    }

    if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      const isCancelled = invokeResult.status === 'cancelled';
      return (
        <div style={outputPaneStyle}>
          <div style={sectionStyle}>
            <span style={sectionLabelStyle}>
              {t("pages.studio.studiomembercurrentrunpanel.input", "Request")}
            </span>
            <p style={bodyTextStyle}>
              {inputText ||
                t(
                  "pages.studio.studiomembercurrentrunpanel.no.prompt.captured",
                  "No request captured.",
                )}
            </p>
          </div>
          <div style={isCancelled ? warningCardStyle : errorCardStyle}>
            {isCancelled ? (
              <ExclamationCircleFilled style={warningIconStyle} />
            ) : (
              <CloseCircleFilled style={errorIconStyle} />
            )}
            <div style={{ minWidth: 0 }}>
              <div style={errorTitleStyle}>
                {isCancelled
                  ? t(
                      "pages.studio.studiomembercurrentrunpanel.run.stopped",
                      "Run stopped",
                    )
                  : t(
                      "pages.studio.studiomembercurrentrunpanel.run.failed",
                      "Run failed",
                    )}
              </div>
              <p style={errorDescriptionStyle}>
                {errorDescription ||
                  (isCancelled
                    ? t(
                        "pages.studio.studiomembercurrentrunpanel.the.run.has.stopped",
                        "The run has stopped and only partial output may currently be displayed.",
                      )
                    : t(
                        "pages.studio.studiomembercurrentrunpanel.run.failed.without.message",
                        "This run failed without an additional error message.",
                      ))}
              </p>
            </div>
          </div>
          {renderCurrentRunLogs()}
          {isCancelled && outputText ? (
            <div style={warningCardStyle}>
              <ExclamationCircleFilled style={warningIconStyle} />
              <div style={{ minWidth: 0 }}>
                <div style={errorTitleStyle}>
                  {t(
                    "pages.studio.studiomembercurrentrunpanel.partial.output",
                    "Partial output",
                  )}
                </div>
                <p style={errorDescriptionStyle}>
                  {t(
                    "pages.studio.studiomembercurrentrunpanel.the.run.has.stopped.2",
                    "The run has stopped and only partial output may currently be displayed.",
                  )}
                </p>
              </div>
            </div>
          ) : null}
          {outputText ? (
            <div style={sectionStyle}>
              <span style={sectionLabelStyle}>
                {presentation === 'member-run'
                  ? t(
                      "pages.studio.studiomembercurrentrunpanel.result",
                      "Result",
                    )
                  : t(
                      "pages.studio.studiomembercurrentrunpanel.output",
                      "Response",
                    )}
              </span>
              {renderRunOutputContent(outputText)}
            </div>
          ) : null}
          <div
            data-testid="studio-invoke-recovery-path"
            style={recoveryPathStyle}
          >
            <span style={sectionLabelStyle}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.recovery.path",
                "Recovery path",
              )}
            </span>
            <Typography.Text style={helperTextStyle}>
              {isCancelled
                ? t(
                    "pages.studio.studiomembercurrentrunpanel.this.stopped.run.stays.in.history",
                    "This stopped run stays in history. Retry as a new run when you want fresh output, or switch to Observe to inspect the latest backend events.",
                  )
                : t(
                    "pages.studio.studiomembercurrentrunpanel.this.failed.only.the.invoke.run.open.diagnostics",
                    "This run failed. Retry with a smaller request, open diagnostics for backend signals, or edit the member contract from its owning member surface.",
                  )}
            </Typography.Text>
          </div>
          <div style={errorActionsStyle}>
            <Button icon={<UnorderedListOutlined />} onClick={openDiagnostics}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.open.diagnostics",
                "Open diagnostics",
              )}
            </Button>
            <Button icon={<CopyOutlined />} onClick={onCopyError}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.copy.error",
                "Copy error",
              )}
            </Button>
            <Button icon={<ReloadOutlined />} onClick={onRetryAsNewRun}>
              {t(
                "pages.studio.studiomembercurrentrunpanel.retry.as.new.run",
                "Retry as new run",
              )}
            </Button>
          </div>
        </div>
      );
    }

    return (
      <div
        data-testid="studio-invoke-chat-transcript"
        ref={transcriptViewportRef}
        style={outputPaneStyle}
      >
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>
            {t(
              "pages.studio.studiomembercurrentrunpanel.status.summary",
              "Run status",
            )}
          </span>
          <div style={summaryStyle}>{statusSummary}</div>
        </div>
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>
            {t("pages.studio.studiomembercurrentrunpanel.input.2", "Request")}
          </span>
          <p style={bodyTextStyle}>
            {inputText ||
              t(
                "pages.studio.studiomembercurrentrunpanel.no.prompt.captured",
                "No request captured.",
            )}
          </p>
        </div>
        {renderCurrentRunLogs()}
        <div style={responseSectionStyle}>
          <span style={sectionLabelStyle}>
            {presentation === 'member-run'
              ? t(
                  "pages.studio.studiomembercurrentrunpanel.result.2",
                  "Result",
                )
              : t(
                  "pages.studio.studiomembercurrentrunpanel.output.2",
                  "Response",
                )}
          </span>
          {invokeResult.status === 'running' && !outputText ? (
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomembercurrentrunpanel.waiting.for.output",
                "Waiting for a response...",
              )}
            </Typography.Text>
          ) : outputText ? (
            renderRunOutputContent(outputText)
          ) : invokeResult.status === 'success' ? (
            <div style={helperTextStyle}>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.no.displayable.content.returned",
                  "No readable response returned.",
                )}
              </div>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.the.run.ended.successfully",
                  "The run ended successfully, but it did not return user-visible content.",
                )}
              </div>
              <div>
                {t(
                  "pages.studio.studiomembercurrentrunpanel.you.can.view.events",
                  "Open diagnostics when you need event or payload evidence.",
                )}
              </div>
            </div>
          ) : (
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomembercurrentrunpanel.waiting.for.output.2",
                "Waiting for a response...",
              )}
            </Typography.Text>
          )}
        </div>
        {renderObserveHandoff()}
      </div>
    );
  };

  const renderObserveHandoff = () =>
    shouldShowObserveHandoff ? (
      <div
        data-testid="studio-invoke-observe-handoff"
        style={recoveryPathStyle}
      >
        <span style={sectionLabelStyle}>
          {t(
            "pages.studio.studiomembercurrentrunpanel.observe.handoff",
            "Observe handoff",
          )}
        </span>
        <Typography.Text style={helperTextStyle}>
          {observeHandoffText}
        </Typography.Text>
      </div>
    ) : null;

  if (presentation === 'member-run') {
    return (
      <div style={memberRunPanelStyle}>
        <div style={memberRunHeaderStyle}>
          <div style={memberRunTitleStyle}>
            {t(
              "pages.studio.studiomembercurrentrunpanel.current.run",
              "Current run",
            )}
          </div>
          <div style={memberRunStatusClusterStyle}>
            <span style={markerStyle}>{marker}</span>
            {currentRunHasData ? (
              <>
                <span
                  data-testid="studio-invoke-run-status-summary"
                  style={summaryStyle}
                >
                  {statusSummary}
                </span>
                <Button
                  icon={<UnorderedListOutlined />}
                  size="small"
                  onClick={openDiagnostics}
                >
                  {t(
                    "pages.studio.studiomembercurrentrunpanel.diagnostics",
                    "Diagnostics",
                  )}
                </Button>
              </>
            ) : null}
          </div>
        </div>
        {renderMemberRunOutput()}
        {renderObserveHandoff()}
      </div>
    );
  }

  return (
    <div style={panelStyle}>
      <div style={headerStyle}>
        <span style={markerStyle}>{marker}</span>
        {currentRunHasData ? (
          <>
            <span
              data-testid="studio-invoke-run-status-summary"
              style={summaryStyle}
            >
              {statusSummary}
            </span>
            <Button
              icon={<UnorderedListOutlined />}
              size="small"
              onClick={openDiagnostics}
            >
              {t("pages.studio.studiomembercurrentrunpanel.diagnostics", "Diagnostics")}
            </Button>
          </>
        ) : null}
      </div>
      {renderOutput()}
    </div>
  );
};

export default StudioMemberCurrentRunPanel;
