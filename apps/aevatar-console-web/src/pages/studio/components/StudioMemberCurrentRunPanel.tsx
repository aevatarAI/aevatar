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
import { translate } from '@/shared/i18n/localization';
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
      return translate('studio.run.status.running');
    case 'success':
      return translate('studio.run.status.succeeded');
    case 'error':
      return translate('studio.run.status.failed');
    case 'cancelled':
      return translate('studio.run.status.cancelled');
    default:
      return translate('studio.run.status.idle');
  }
}

function getRunMarker(input: {
  readonly currentRunHasData: boolean;
  readonly runViewMode: RunViewMode;
  readonly status: InvokeResultState['status'];
}): string {
  if (!input.currentRunHasData) {
    return translate('studio.run.marker.none');
  }

  if (input.runViewMode === 'historical') {
    return translate('studio.run.marker.historical');
  }

  if (input.status === 'running') {
    return translate('studio.run.marker.running');
  }

  return translate('studio.run.marker.latest');
}

function getShortRunId(runId: string): string {
  const normalized = trimOptional(runId);
  return normalized ? truncateMiddle(normalized, 6, 4) : translate('studio.run.pending');
}

function buildStatusSummary(input: {
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
}): string {
  return `${getStatusLabel(input.invokeResult.status)} · ${
    input.runElapsedLabel
  } · ${input.endpointLabel || 'chat'} · ${translate('common.id.run')} ${getShortRunId(
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
        detail: startedAtLabel || translate('studio.run.timeline.pendingStart'),
        label: translate('studio.run.timeline.started'),
      },
    ];

    if (invokeResult.events.length > 0) {
      for (const event of invokeResult.events.slice(0, 8)) {
        const type = String(event.type || 'Event');
        if (type === 'TEXT_MESSAGE_CONTENT') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.agentMessage'),
          });
        } else if (type === 'RUN_STARTED') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.started'),
          });
        } else if (type === 'RUN_FINISHED') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.finished'),
          });
        } else if (type === 'RUN_ERROR') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.failed'),
          });
        } else if (type === 'PARTICIPANT_JOINED') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.participantJoined'),
          });
        } else if (type === 'PARTICIPANT_LEFT') {
          items.push({
            detail: getEventPreview(event),
            label: translate('studio.run.timeline.participantLeft'),
          });
        } else {
          items.push({ detail: getEventPreview(event), label: type });
        }
      }
    }

    if (invokeResult.status === 'success') {
      items.push({
        detail: finishedAtLabel || translate('studio.run.timeline.completed'),
        label: translate('studio.run.timeline.finished'),
      });
    } else if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      items.push({
        detail: errorDescription || translate('studio.run.timeline.noExtraError'),
        label:
          invokeResult.status === 'cancelled'
            ? translate('studio.run.stopped')
            : translate('studio.run.timeline.failed'),
      });
    } else if (invokeResult.status === 'running') {
      items.push({
        detail: translate('studio.run.waitingForOutput'),
        label: translate('studio.run.timeline.inProgress'),
      });
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
    { key: 'output' as const, label: translate('common.output') },
    { key: 'timeline' as const, label: translate('studio.run.tabs.timeline') },
    { key: 'events' as const, label: translate('studio.run.tabs.events') },
    { key: 'metadata' as const, label: translate('studio.run.tabs.metadata') },
  ];

  const renderOutput = () => {
    if (!currentRunHasData) {
      return (
        <div style={emptyStateStyle}>
          <div style={emptyTitleStyle}>{translate('studio.run.none')}</div>
          <div>{translate('studio.invoke.promptPlaceholder')}</div>
        </div>
      );
    }

    if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      const isCancelled = invokeResult.status === 'cancelled';
      return (
        <div style={outputPaneStyle}>
          <div style={sectionStyle}>
            <span style={sectionLabelStyle}>{translate('common.input')}</span>
            <p style={bodyTextStyle}>{inputText || translate('studio.run.noPrompt')}</p>
          </div>
          <div style={isCancelled ? warningCardStyle : errorCardStyle}>
            {isCancelled ? (
              <ExclamationCircleFilled style={warningIconStyle} />
            ) : (
              <CloseCircleFilled style={errorIconStyle} />
            )}
            <div style={{ minWidth: 0 }}>
              <div style={errorTitleStyle}>
                {isCancelled ? translate('studio.run.stopped') : translate('studio.run.failed')}
              </div>
              <p style={errorDescriptionStyle}>
                {errorDescription ||
                  (isCancelled
                    ? translate('studio.invoke.runStoppedPartial')
                    : translate('studio.run.failedNoMessage'))}
              </p>
            </div>
          </div>
          {isCancelled && outputText ? (
            <div style={warningCardStyle}>
              <ExclamationCircleFilled style={warningIconStyle} />
              <div style={{ minWidth: 0 }}>
                <div style={errorTitleStyle}>{translate('studio.run.partialOutput')}</div>
                <p style={errorDescriptionStyle}>
                  {translate('studio.invoke.runStoppedPartial')}
                </p>
              </div>
            </div>
          ) : null}
          {outputText ? (
            <div style={sectionStyle}>
              <span style={sectionLabelStyle}>{translate('common.output')}</span>
              <p style={bodyTextStyle}>{outputText}</p>
            </div>
          ) : null}
          <div style={errorActionsStyle}>
            <Button
              icon={<UnorderedListOutlined />}
              onClick={() => onTabChange('events')}
            >
              {translate('studio.run.viewEvents')}
            </Button>
            <Button icon={<CopyOutlined />} onClick={onCopyError}>
              {translate('studio.run.copyError')}
            </Button>
            <Button icon={<ReloadOutlined />} onClick={onRetryAsNewRun}>
              {translate('studio.run.retryAsNew')}
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
          <span style={sectionLabelStyle}>{translate('studio.run.summary')}</span>
          <div style={summaryStyle}>{statusSummary}</div>
        </div>
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>{translate('common.input')}</span>
          <p style={bodyTextStyle}>{inputText || translate('studio.run.noPrompt')}</p>
        </div>
        <div style={sectionStyle}>
          <span style={sectionLabelStyle}>{translate('common.output')}</span>
          {invokeResult.status === 'running' && !outputText ? (
            <Typography.Text style={helperTextStyle} type="secondary">
              {translate('studio.run.waitingForOutput')}
            </Typography.Text>
          ) : outputText ? (
            <p style={bodyTextStyle}>{outputText}</p>
          ) : invokeResult.status === 'success' ? (
            <div style={helperTextStyle}>
              <div>{translate('studio.run.noVisibleContent')}</div>
              <div>{translate('studio.run.successNoVisibleOutput')}</div>
              <div>{translate('studio.run.inspectEventsMetadata')}</div>
            </div>
          ) : (
            <Typography.Text style={helperTextStyle} type="secondary">
              {translate('studio.run.waitingForOutput')}
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
          {translate('studio.run.none')}
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
          {translate('studio.run.noStructuredEvents')}
        </Typography.Text>
      ) : (
        <>
          <RuntimeEventPreviewPanel
            events={invokeResult.events}
            title={translate('studio.run.eventsTitle', {
              count: invokeResult.events.length,
            })}
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
        <span style={sectionLabelStyle}>{translate('studio.run.technicalFields')}</span>
        <div style={metadataGridStyle}>
          <MetadataItem
            label={translate('studio.run.fullRunId')}
            value={<MetadataValue value={invokeResult.runId} />}
          />
          <MetadataItem
            label={translate('studio.run.commandId')}
            value={<MetadataValue value={invokeResult.commandId} />}
          />
          <MetadataItem
            label={translate('studio.run.actorId')}
            value={<MetadataValue value={invokeResult.actorId} />}
          />
          <MetadataItem
            label={translate('studio.run.memberId')}
            value={<MetadataValue value={memberId} />}
          />
          <MetadataItem
            label={translate('studio.run.endpoint')}
            value={<MetadataValue value={endpointLabel} />}
          />
          <MetadataItem
            label={translate('studio.run.revision')}
            value={<MetadataValue value={revisionId} />}
          />
          <MetadataItem
            label={translate('studio.run.publishedContext')}
            value={<MetadataValue value={publishedContext} />}
          />
          <MetadataItem
            label={translate('studio.run.startedAt')}
            value={<MetadataValue value={startedAtLabel} />}
          />
          <MetadataItem
            label={translate('studio.run.finishedAt')}
            value={<MetadataValue value={finishedAtLabel} />}
          />
          <MetadataItem
            label={translate('studio.run.duration')}
            value={<MetadataValue value={runElapsedLabel} />}
          />
          <MetadataItem
            label={translate('studio.run.eventCount')}
            value={
              <div style={contractValueStyle}>
                {invokeResult.eventCount || invokeResult.events.length}
              </div>
            }
          />
        </div>
      </div>
      <details style={sectionStyle}>
        <summary style={contractValueStyle}>{translate('studio.run.advancedDetails')}</summary>
        <pre style={rawOutputStyle}>{currentRawOutput || translate('studio.run.noRawJson')}</pre>
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
        <div
          aria-label={translate('studio.run.outputViewsAria')}
          role="tablist"
          style={tabListStyle}
        >
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
