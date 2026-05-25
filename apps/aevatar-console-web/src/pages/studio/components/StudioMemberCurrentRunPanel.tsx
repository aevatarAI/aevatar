import {
  CloseCircleFilled,
  CopyOutlined,
  ExclamationCircleFilled,
  ReloadOutlined,
  UnorderedListOutlined,
} from '@ant-design/icons';
import { Button, Typography } from 'antd';
import React, { useMemo } from 'react';
import { RuntimeEventPreviewPanel } from '@/shared/agui/runtimeConversationPresentation';
import type {
  CurrentRunRequest,
  InvokeResultState,
  StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import {
  contractValueStyle,
  formatHistoryTimestamp,
  helperTextStyle,
  monoFontFamily,
  studioInvokeColors,
  trimOptional,
  truncateMiddle,
} from './studioInvokeUi';

type RunOutputTab = 'output' | 'timeline' | 'events' | 'metadata';

type RunViewMode = 'latest' | 'historical';

type StudioMemberCurrentRunPanelProps = {
  readonly activeTab: RunOutputTab;
  readonly activeRunCompletedAt: number | null;
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly currentRawOutput: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly memberId: string;
  readonly publishedContext: string;
  readonly revisionId: string;
  readonly runElapsedLabel: string;
  readonly runViewMode: RunViewMode;
  readonly transcriptViewportRef: React.RefObject<HTMLDivElement | null>;
  readonly onCopyError: () => void;
  readonly onRetryAsNewRun: () => void;
  readonly onTabChange: (tab: RunOutputTab) => void;
};

function getOutputText(input: {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly invokeResult: InvokeResultState;
}): string {
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
  readonly runViewMode: RunViewMode;
  readonly status: InvokeResultState['status'];
}): string {
  if (!input.currentRunHasData) {
    return 'No run';
  }

  if (input.runViewMode === 'historical') {
    return 'Historical run · Read-only';
  }

  if (input.status === 'running') {
    return 'Running run';
  }

  return 'Latest run';
}

function getShortRunId(runId: string): string {
  const normalized = trimOptional(runId);
  return normalized ? truncateMiddle(normalized, 6, 4) : 'pending';
}

function buildStatusSummary(input: {
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
}): string {
  return `${getStatusLabel(input.invokeResult.status)} · ${
    input.runElapsedLabel
  } · ${input.endpointLabel || 'chat'} · Run ${getShortRunId(
    input.invokeResult.runId,
  )}`;
}

function readEventString(event: unknown, key: string): string {
  if (!event || typeof event !== 'object' || !(key in event)) {
    return '';
  }

  const value = (event as Record<string, unknown>)[key];
  return typeof value === 'string' ? value : '';
}

function getEventPreview(event: unknown): string {
  const delta = readEventString(event, 'delta');
  if (delta) {
    return delta;
  }

  const message = readEventString(event, 'message');
  if (message) {
    return message;
  }

  const name = readEventString(event, 'name');
  if (name) {
    return name;
  }

  const stepName = readEventString(event, 'stepName');
  if (stepName) {
    return stepName;
  }

  return '';
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
  background: studioInvokeColors.surfaceActive,
  border: `1px solid ${studioInvokeColors.borderStrong}`,
  borderRadius: 999,
  color: studioInvokeColors.textSoft,
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 800,
  lineHeight: '18px',
  padding: '3px 9px',
};

const summaryStyle: React.CSSProperties = {
  color: studioInvokeColors.textSoft,
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
  minWidth: 0,
};

const tabsStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const tabListStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  gap: 6,
  minWidth: 0,
  overflowX: 'auto',
  paddingBottom: 8,
};

const tabButtonStyle: React.CSSProperties = {
  background: 'transparent',
  border: 0,
  borderRadius: 8,
  color: studioInvokeColors.muted,
  cursor: 'pointer',
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
  minHeight: 32,
  padding: '6px 10px',
};

const activeTabButtonStyle: React.CSSProperties = {
  background: studioInvokeColors.surfaceActive,
  color: studioInvokeColors.text,
};

const tabPaneStyle: React.CSSProperties = {
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const outputPaneStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  minWidth: 0,
};

const sectionStyle: React.CSSProperties = {
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const sectionLabelStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0.6,
  lineHeight: '16px',
  textTransform: 'uppercase',
};

const bodyTextStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 14,
  lineHeight: 1.7,
  margin: 0,
  whiteSpace: 'pre-wrap',
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const emptyStateStyle: React.CSSProperties = {
  alignItems: 'center',
  color: studioInvokeColors.muted,
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  fontSize: 14,
  gap: 6,
  justifyContent: 'center',
  lineHeight: 1.7,
  minHeight: 180,
  minWidth: 0,
  padding: 18,
  textAlign: 'center',
};

const emptyTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 15,
  fontWeight: 800,
  lineHeight: '22px',
};

const errorActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  marginTop: 10,
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

const timelineStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  minWidth: 0,
};

const timelineRowStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'grid',
  gap: 10,
  gridTemplateColumns: '12px minmax(0, 1fr)',
  minWidth: 0,
};

const timelineDotStyle: React.CSSProperties = {
  background: studioInvokeColors.accent,
  borderRadius: 999,
  height: 8,
  marginTop: 7,
  width: 8,
};

const eventListStyle: React.CSSProperties = {
  display: 'grid',
  gap: 6,
  minWidth: 0,
};

const eventRowStyle: React.CSSProperties = {
  alignItems: 'center',
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  display: 'grid',
  gap: 8,
  gridTemplateColumns: '36px minmax(120px, 0.32fr) minmax(0, 1fr)',
  minWidth: 0,
  padding: '8px 10px',
};

const eventIndexStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  fontFamily: monoFontFamily,
  fontSize: 11,
  textAlign: 'right',
};

const eventTypeStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontFamily: monoFontFamily,
  fontSize: 12,
  fontWeight: 800,
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const eventPreviewStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  fontSize: 12,
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const metadataGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
  minWidth: 0,
};

const metadataItemStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const rawOutputStyle: React.CSSProperties = {
  background: studioInvokeColors.rawSurface,
  borderRadius: 8,
  color: studioInvokeColors.rawText,
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  minHeight: 120,
  minWidth: 0,
  overflow: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const MetadataValue: React.FC<{
  readonly fallback?: string;
  readonly value?: string;
}> = ({ fallback = '—', value }) => {
  const normalized = trimOptional(value);
  if (!normalized) {
    return (
      <Typography.Text style={helperTextStyle} type="secondary">
        {fallback}
      </Typography.Text>
    );
  }

  return (
    <Typography.Text copyable={{ text: normalized }} style={contractValueStyle}>
      {normalized}
    </Typography.Text>
  );
};

const MetadataItem: React.FC<{
  readonly label: string;
  readonly value?: React.ReactNode;
}> = ({ label, value }) => (
  <div style={metadataItemStyle}>
    <span style={sectionLabelStyle}>{label}</span>
    {value}
  </div>
);

const StudioMemberCurrentRunPanel: React.FC<
  StudioMemberCurrentRunPanelProps
> = ({
  activeRunCompletedAt,
  activeTab,
  chatMessages,
  currentRawOutput,
  currentRunHasData,
  currentRunRequest,
  endpointLabel,
  invokeResult,
  memberId,
  onCopyError,
  onRetryAsNewRun,
  onTabChange,
  publishedContext,
  revisionId,
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
    runViewMode,
    status: invokeResult.status,
  });
  const errorDescription =
    invokeResult.errorCode && invokeResult.error
      ? `${invokeResult.error}（${invokeResult.errorCode}）`
      : invokeResult.errorCode || invokeResult.error;
  const startedAtLabel = currentRunRequest?.startedAt
    ? formatHistoryTimestamp(currentRunRequest.startedAt)
    : '';
  const finishedAtLabel = activeRunCompletedAt
    ? formatHistoryTimestamp(activeRunCompletedAt)
    : '';

  const timelineItems = useMemo(() => {
    if (!currentRunHasData) {
      return [];
    }

    const items = [
      {
        detail: startedAtLabel || 'Pending start timestamp',
        label: 'Run started',
      },
    ];

    if (invokeResult.events.length > 0) {
      for (const event of invokeResult.events.slice(0, 8)) {
        const type = String(event.type || 'Event');
        if (type === 'TEXT_MESSAGE_CONTENT') {
          items.push({ detail: getEventPreview(event), label: 'Agent message' });
        } else if (type === 'RUN_STARTED') {
          items.push({ detail: getEventPreview(event), label: 'Run started' });
        } else if (type === 'RUN_FINISHED') {
          items.push({ detail: getEventPreview(event), label: 'Run finished' });
        } else if (type === 'RUN_ERROR') {
          items.push({ detail: getEventPreview(event), label: 'Run failed' });
        } else if (type === 'PARTICIPANT_JOINED') {
          items.push({
            detail: getEventPreview(event),
            label: 'Participant joined',
          });
        } else if (type === 'PARTICIPANT_LEFT') {
          items.push({
            detail: getEventPreview(event),
            label: 'Participant left',
          });
        } else {
          items.push({ detail: getEventPreview(event), label: type });
        }
      }
    }

    if (invokeResult.status === 'success') {
      items.push({
        detail: finishedAtLabel || 'Completed',
        label: 'Run finished',
      });
    } else if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      items.push({
        detail: errorDescription || 'No extra error text',
        label: invokeResult.status === 'cancelled' ? 'Run stopped' : 'Run failed',
      });
    } else if (invokeResult.status === 'running') {
      items.push({ detail: 'Waiting for output', label: 'Run in progress' });
    }

    return items;
  }, [
    currentRunHasData,
    errorDescription,
    finishedAtLabel,
    invokeResult.events,
    invokeResult.status,
    startedAtLabel,
  ]);

  const tabItems = [
    { key: 'output' as const, label: 'Output' },
    { key: 'timeline' as const, label: 'Timeline' },
    { key: 'events' as const, label: 'Events' },
    { key: 'metadata' as const, label: 'Metadata' },
  ];

  const renderOutput = () => {
    if (!currentRunHasData) {
      return (
        <div style={emptyStateStyle}>
          <div style={emptyTitleStyle}>No run yet</div>
          <div>Send a prompt above to create the first run.</div>
        </div>
      );
    }

    if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      const isCancelled = invokeResult.status === 'cancelled';
      return (
        <div style={outputPaneStyle}>
          <div style={sectionStyle}>
            <span style={sectionLabelStyle}>Input</span>
            <p style={bodyTextStyle}>{inputText || 'No prompt captured.'}</p>
          </div>
          <div style={isCancelled ? warningCardStyle : errorCardStyle}>
            {isCancelled ? (
              <ExclamationCircleFilled style={warningIconStyle} />
            ) : (
              <CloseCircleFilled style={errorIconStyle} />
            )}
            <div style={{ minWidth: 0 }}>
              <div style={errorTitleStyle}>
                {isCancelled ? 'Run stopped' : 'Run failed'}
              </div>
              <p style={errorDescriptionStyle}>
                {errorDescription ||
                  (isCancelled
                    ? '该 Run 已停止，当前可能只显示部分输出。'
                    : 'This run failed without an additional error message.')}
              </p>
            </div>
          </div>
          {isCancelled && outputText ? (
            <div style={warningCardStyle}>
              <ExclamationCircleFilled style={warningIconStyle} />
              <div style={{ minWidth: 0 }}>
                <div style={errorTitleStyle}>Partial output</div>
                <p style={errorDescriptionStyle}>
                  该 Run 已停止，当前可能只显示部分输出。
                </p>
              </div>
            </div>
          ) : null}
          {outputText ? (
            <div style={sectionStyle}>
              <span style={sectionLabelStyle}>Output</span>
              <p style={bodyTextStyle}>{outputText}</p>
            </div>
          ) : null}
          <div style={errorActionsStyle}>
            <Button
              icon={<UnorderedListOutlined />}
              onClick={() => onTabChange('events')}
            >
              View events
            </Button>
            <Button icon={<CopyOutlined />} onClick={onCopyError}>
              Copy error
            </Button>
            <Button icon={<ReloadOutlined />} onClick={onRetryAsNewRun}>
              Retry as new run
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
          <span style={sectionLabelStyle}>Status summary</span>
          <div style={summaryStyle}>{statusSummary}</div>
        </div>
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>Input</span>
          <p style={bodyTextStyle}>{inputText || 'No prompt captured.'}</p>
        </div>
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>Output</span>
          {invokeResult.status === 'running' && !outputText ? (
            <Typography.Text style={helperTextStyle} type="secondary">
              Waiting for output...
            </Typography.Text>
          ) : outputText ? (
            <p style={bodyTextStyle}>{outputText}</p>
          ) : invokeResult.status === 'success' ? (
            <div style={helperTextStyle}>
              <div>没有返回可展示内容。</div>
              <div>
                该 Run 已成功结束，但没有返回用户可见的 Output。
              </div>
              <div>你可以查看 Events 或 Metadata 排查原因。</div>
            </div>
          ) : (
            <Typography.Text style={helperTextStyle} type="secondary">
              Waiting for output...
            </Typography.Text>
          )}
        </div>
      </div>
    );
  };

  const renderTimeline = () => (
    <div style={timelineStyle}>
      {timelineItems.length === 0 ? (
        <Typography.Text style={helperTextStyle} type="secondary">
          No run yet.
        </Typography.Text>
      ) : (
        timelineItems.map((item, index) => (
          <div key={`${item.label}-${index}`} style={timelineRowStyle}>
            <span style={timelineDotStyle} />
            <div style={{ minWidth: 0 }}>
              <div style={contractValueStyle}>{item.label}</div>
              {item.detail ? (
                <div style={helperTextStyle}>{item.detail}</div>
              ) : null}
            </div>
          </div>
        ))
      )}
    </div>
  );

  const renderEvents = () => (
    <div style={eventListStyle}>
      {invokeResult.events.length === 0 ? (
        <Typography.Text style={helperTextStyle} type="secondary">
          当前 Run 还没有结构化事件。
        </Typography.Text>
      ) : (
        <>
          <RuntimeEventPreviewPanel
            events={invokeResult.events}
            title={`Events (${invokeResult.events.length})`}
          />
          {invokeResult.events.map((event, index) => (
            <div
              key={`${event.type}-${event.timestamp || index}-${index}`}
              style={eventRowStyle}
            >
              <span style={eventIndexStyle}>#{index + 1}</span>
              <span title={event.type} style={eventTypeStyle}>
                {event.type}
              </span>
              <span style={eventPreviewStyle}>{getEventPreview(event)}</span>
            </div>
          ))}
        </>
      )}
    </div>
  );

  const renderMetadata = () => (
    <div style={outputPaneStyle}>
      <div style={sectionStyle}>
        <span style={sectionLabelStyle}>Technical fields</span>
        <div style={metadataGridStyle}>
          <MetadataItem
            label="Full Run ID"
            value={<MetadataValue value={invokeResult.runId} />}
          />
          <MetadataItem
            label="Command ID"
            value={<MetadataValue value={invokeResult.commandId} />}
          />
          <MetadataItem
            label="Actor ID"
            value={<MetadataValue value={invokeResult.actorId} />}
          />
          <MetadataItem
            label="Member ID"
            value={<MetadataValue value={memberId} />}
          />
          <MetadataItem
            label="Endpoint"
            value={<MetadataValue value={endpointLabel} />}
          />
          <MetadataItem
            label="Revision"
            value={<MetadataValue value={revisionId} />}
          />
          <MetadataItem
            label="Published context"
            value={<MetadataValue value={publishedContext} />}
          />
          <MetadataItem
            label="Started at"
            value={<MetadataValue value={startedAtLabel} />}
          />
          <MetadataItem
            label="Finished at"
            value={<MetadataValue value={finishedAtLabel} />}
          />
          <MetadataItem
            label="Duration"
            value={<MetadataValue value={runElapsedLabel} />}
          />
          <MetadataItem
            label="Event count"
            value={
              <div style={contractValueStyle}>
                {invokeResult.eventCount || invokeResult.events.length}
              </div>
            }
          />
        </div>
      </div>
      <details style={sectionStyle}>
        <summary style={contractValueStyle}>Advanced details</summary>
        <pre style={rawOutputStyle}>{currentRawOutput || 'No raw JSON.'}</pre>
      </details>
    </div>
  );

  const renderActivePane = () => {
    if (activeTab === 'timeline') {
      return renderTimeline();
    }

    if (activeTab === 'events') {
      return renderEvents();
    }

    if (activeTab === 'metadata') {
      return renderMetadata();
    }

    return renderOutput();
  };

  return (
    <div style={panelStyle}>
      <div style={headerStyle}>
        <span style={markerStyle}>{marker}</span>
        {currentRunHasData ? (
          <span
            data-testid="studio-invoke-run-status-summary"
            style={summaryStyle}
          >
            {statusSummary}
          </span>
        ) : null}
      </div>
      <div style={tabsStyle}>
        <div aria-label="Run output views" role="tablist" style={tabListStyle}>
          {tabItems.map((item) => {
            const selected = item.key === activeTab;
            return (
              <button
                key={item.key}
                aria-selected={selected}
                role="tab"
                style={{
                  ...tabButtonStyle,
                  ...(selected ? activeTabButtonStyle : null),
                }}
                type="button"
                onClick={() => onTabChange(item.key)}
              >
                {item.label}
              </button>
            );
          })}
        </div>
        <div role="tabpanel" style={tabPaneStyle}>
          {renderActivePane()}
        </div>
      </div>
    </div>
  );
};

export default StudioMemberCurrentRunPanel;
