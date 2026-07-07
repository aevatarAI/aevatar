import { Input, Typography } from 'antd';
import React, { useMemo } from 'react';
import { RuntimeEventPreviewPanel } from '@/shared/agui/runtimeConversationPresentation';
import { AevatarContextDrawer } from '@/shared/ui/aevatarPageShells';
import {
  getUserFacingIdentifierLabel,
  sanitizeUserFacingText,
} from '@/shared/ui/userFacingIdentifiers';
import type {
  CurrentRunRequest,
  InvokeHistoryEntry,
  InvokeResultState,
  StudioInvokeChatMessage,
  StudioInvokeRunViewMode,
} from './StudioMemberInvokePanel.currentRun';
import {
  contractValueStyle,
  formatHistoryTimestamp,
  helperTextStyle,
  monoFontFamily,
  studioInvokeColors,
  trimOptional,
} from './studioInvokeUi';
import { t } from '@/shared/i18n/messages';

type TimelineItem = {
  readonly detail: string;
  readonly id: string;
  readonly label: string;
};

type StudioInvokeDiagnosticsDrawerProps = {
  readonly activeRunCompletedAt: number | null;
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly currentRawOutput: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly historyEntry?: InvokeHistoryEntry | null;
  readonly invokeResult: InvokeResultState;
  readonly isChatEndpoint: boolean;
  readonly open: boolean;
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly runElapsedLabel: string;
  readonly runViewMode: StudioInvokeRunViewMode;
  readonly title?: React.ReactNode;
  readonly onClose: () => void;
  readonly onPayloadBase64Change: (value: string) => void;
  readonly onPayloadTypeUrlChange: (value: string) => void;
};

function readEventString(event: unknown, key: string): string {
  if (!event || typeof event !== 'object' || !(key in event)) {
    return '';
  }

  const value = (event as Record<string, unknown>)[key];
  return typeof value === 'string' ? value : '';
}

function sanitizeVisibleText(value: string | null | undefined): string {
  return sanitizeUserFacingText(value) || '';
}

function getEventPreview(event: unknown): string {
  const delta = readEventString(event, 'delta');
  if (delta) {
    return sanitizeVisibleText(delta);
  }

  const message = readEventString(event, 'message');
  if (message) {
    return sanitizeVisibleText(message);
  }

  const name = readEventString(event, 'name');
  if (name) {
    return sanitizeVisibleText(name);
  }

  const stepName = readEventString(event, 'stepName');
  if (stepName) {
    return sanitizeVisibleText(stepName);
  }

  return '';
}

function getEventKey(event: unknown): string {
  if (!event || typeof event !== 'object') {
    return 'event-empty';
  }

  const eventRecord = event as Record<string, unknown>;
  return JSON.stringify({
    commandId: eventRecord.commandId,
    id: eventRecord.id,
    name: eventRecord.name,
    runId: eventRecord.runId,
    timestamp: eventRecord.timestamp,
    type: eventRecord.type,
  });
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
    trimOptional(input.invokeResult.assistantText) ||
    trimOptional(input.invokeResult.error)
  );
}

const drawerSectionStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  display: 'grid',
  gap: 10,
  minWidth: 0,
  padding: '12px 14px',
};

const sectionLabelStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0,
  lineHeight: '16px',
  textTransform: 'uppercase',
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

const typedPayloadGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
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

const outputTextStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 14,
  lineHeight: 1.7,
  margin: 0,
  overflowWrap: 'anywhere',
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

const StudioInvokeDiagnosticsDrawer: React.FC<
  StudioInvokeDiagnosticsDrawerProps
> = ({
  activeRunCompletedAt,
  chatMessages,
  currentRawOutput,
  currentRunHasData,
  currentRunRequest,
  endpointLabel,
  historyEntry,
  invokeResult,
  isChatEndpoint,
  onClose,
  onPayloadBase64Change,
  onPayloadTypeUrlChange,
  open,
  payloadBase64,
  payloadTypeUrl,
  runElapsedLabel,
  runViewMode,
  title,
}) => {
  const startedAtLabel = currentRunRequest?.startedAt
    ? formatHistoryTimestamp(currentRunRequest.startedAt)
    : '';
  const finishedAtLabel = activeRunCompletedAt
    ? formatHistoryTimestamp(activeRunCompletedAt)
    : '';
  const statusLabel = getStatusLabel(invokeResult.status);
  const outputText = getOutputText({ chatMessages, invokeResult });
  const errorDescription =
    invokeResult.errorCode && invokeResult.error
      ? `${invokeResult.error}（${invokeResult.errorCode}）`
      : invokeResult.errorCode || invokeResult.error;
  const canEditPayload = !isChatEndpoint && !historyEntry;
  const timelineItems = useMemo<TimelineItem[]>(() => {
    if (!currentRunHasData) {
      return [];
    }

    const items: TimelineItem[] = [
      {
        detail:
          startedAtLabel ||
          t(
            'pages.studio.studioinvokediagnosticsdrawer.pending.start.timestamp',
            'Pending start timestamp',
          ),
        id: 'run-started',
        label: t(
          'pages.studio.studioinvokediagnosticsdrawer.run.started',
          'Run started',
        ),
      },
    ];

    for (const event of invokeResult.events.slice(0, 8)) {
      const type = String(event.type || 'Event');
      if (type === 'TEXT_MESSAGE_CONTENT') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.agent.message',
            'Agent message',
          ),
        });
      } else if (type === 'RUN_STARTED') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.run.started.2',
            'Run started',
          ),
        });
      } else if (type === 'RUN_FINISHED') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.run.finished',
            'Run finished',
          ),
        });
      } else if (type === 'RUN_ERROR') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.run.failed',
            'Run failed',
          ),
        });
      } else if (type === 'PARTICIPANT_JOINED') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.participant.joined',
            'Participant joined',
          ),
        });
      } else if (type === 'PARTICIPANT_LEFT') {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: t(
            'pages.studio.studioinvokediagnosticsdrawer.participant.left',
            'Participant left',
          ),
        });
      } else {
        items.push({
          detail: getEventPreview(event),
          id: `event-${String(event.timestamp || items.length)}-${type}`,
          label: type,
        });
      }
    }

    if (invokeResult.status === 'success') {
      items.push({
        detail:
          finishedAtLabel ||
          t('pages.studio.studioinvokediagnosticsdrawer.completed', 'Completed'),
        id: 'run-finished',
        label: t(
          'pages.studio.studioinvokediagnosticsdrawer.run.finished.2',
          'Run finished',
        ),
      });
    } else if (invokeResult.status === 'error' || invokeResult.status === 'cancelled') {
      items.push({
        detail:
          errorDescription ||
          t(
            'pages.studio.studioinvokediagnosticsdrawer.no.extra.error.text',
            'No extra error text',
          ),
        id:
          invokeResult.status === 'cancelled'
            ? 'run-stopped'
            : 'run-failed',
        label:
          invokeResult.status === 'cancelled'
            ? t(
                'pages.studio.studioinvokediagnosticsdrawer.run.stopped',
                'Run stopped',
              )
            : t(
                'pages.studio.studioinvokediagnosticsdrawer.run.failed.2',
                'Run failed',
              ),
      });
    } else if (invokeResult.status === 'running') {
      items.push({
        detail: t(
          'pages.studio.studioinvokediagnosticsdrawer.waiting.for.output',
          'Waiting for output',
        ),
        id: 'run-in-progress',
        label: t(
          'pages.studio.studioinvokediagnosticsdrawer.run.in.progress',
          'Run in progress',
        ),
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

  return (
    <AevatarContextDrawer
      mobilePlacement="bottom"
      open={open}
      subtitle={
        runViewMode === 'historical'
          ? t(
              'pages.studio.studioinvokediagnosticsdrawer.historical.run.detail',
              'Historical run detail',
            )
          : t(
              'pages.studio.studioinvokediagnosticsdrawer.latest.run.detail',
              'Latest run detail',
            )
      }
      title={
        title ??
        t(
          'pages.studio.studioinvokediagnosticsdrawer.run.diagnostics',
          'Run diagnostics',
        )
      }
      onClose={onClose}
    >
      <div data-testid="studio-invoke-diagnostics-drawer">
        {!isChatEndpoint ? (
          <div style={drawerSectionStyle}>
            <span style={sectionLabelStyle}>
              {t(
                'pages.studio.studioinvokediagnosticsdrawer.typed.payload',
                'Advanced typed payload',
              )}
            </span>
            <div style={typedPayloadGridStyle}>
              <Input
                aria-label={t(
                  'pages.studio.studioinvokediagnosticsdrawer.payload.type.url',
                  'Payload type URL',
                )}
                disabled={!canEditPayload}
                value={historyEntry?.payloadTypeUrl ?? payloadTypeUrl}
                onChange={(event) =>
                  onPayloadTypeUrlChange(event.target.value)
                }
              />
              <Input.TextArea
                aria-label={t(
                  'pages.studio.studioinvokediagnosticsdrawer.payload.base64',
                  'Payload base64',
                )}
                autoSize={{ minRows: 3, maxRows: 8 }}
                disabled={!canEditPayload}
                value={historyEntry?.payloadBase64 ?? payloadBase64}
                onChange={(event) =>
                  onPayloadBase64Change(event.target.value)
                }
              />
              <Typography.Text style={helperTextStyle} type="secondary">
                {historyEntry
                  ? t(
                      'pages.studio.studioinvokediagnosticsdrawer.historical.payload.read.only',
                      'Historical run payload is read-only. Retry as a new run before changing payload fields.',
                    )
                  : t(
                      'pages.studio.studioinvokediagnosticsdrawer.payload.help',
                      'Paste encoded protobuf payload when this type cannot be built from text.',
                    )}
              </Typography.Text>
            </div>
          </div>
        ) : null}

        {!currentRunHasData ? (
          <div style={drawerSectionStyle}>
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                'pages.studio.studioinvokediagnosticsdrawer.no.run.selected',
                'No run is selected yet.',
              )}
            </Typography.Text>
          </div>
        ) : (
          <>
            {historyEntry ? (
              <div
                data-testid="studio-invoke-diagnostics-history-detail"
                style={drawerSectionStyle}
              >
                <span style={sectionLabelStyle}>
                  {t(
                    'pages.studio.studioinvokediagnosticsdrawer.history.detail',
                    'History detail',
                  )}
                </span>
                <div style={metadataGridStyle}>
                  <MetadataItem
                    label={t(
                      'pages.studio.studioinvokediagnosticsdrawer.summary',
                      'Summary',
                    )}
                    value={<MetadataValue value={historyEntry.summary} />}
                  />
                  <MetadataItem
                    label={t(
                      'pages.studio.studioinvokediagnosticsdrawer.created',
                      'Created',
                    )}
                    value={
                      <MetadataValue
                        value={formatHistoryTimestamp(historyEntry.createdAt)}
                      />
                    }
                  />
                  <MetadataItem
                    label={t(
                      'pages.studio.studioinvokediagnosticsdrawer.endpoint',
                      'Endpoint',
                    )}
                    value={
                      <MetadataValue
                        value={getUserFacingIdentifierLabel(
                          historyEntry.endpointLabel || endpointLabel,
                          t(
                            'pages.studio.studioinvokediagnosticsdrawer.endpoint.ready',
                            'Endpoint ready',
                          ),
                        )}
                      />
                    }
                  />
                  <MetadataItem
                    label={t(
                      'pages.studio.studioinvokediagnosticsdrawer.status',
                      'Status',
                    )}
                    value={<MetadataValue value={statusLabel} />}
                  />
                </div>
              </div>
            ) : null}

            <div style={drawerSectionStyle}>
              <span style={sectionLabelStyle}>
                {t(
                  'pages.studio.studioinvokediagnosticsdrawer.timeline',
                  'Timeline',
                )}
              </span>
              <div style={timelineStyle}>
                {timelineItems.length === 0 ? (
                  <Typography.Text style={helperTextStyle} type="secondary">
                    {t(
                      'pages.studio.studioinvokediagnosticsdrawer.no.run.yet',
                      'No run yet.',
                    )}
                  </Typography.Text>
                ) : (
                  timelineItems.map((item) => (
                    <div key={item.id} style={timelineRowStyle}>
                      <span style={timelineDotStyle} />
                      <div style={{ minWidth: 0 }}>
                        <div style={contractValueStyle}>{item.label}</div>
                        {item.detail ? (
                          <div style={helperTextStyle}>
                            {sanitizeVisibleText(item.detail)}
                          </div>
                        ) : null}
                      </div>
                    </div>
                  ))
                )}
              </div>
            </div>

            <div style={drawerSectionStyle}>
              <span style={sectionLabelStyle}>
                {t(
                  'pages.studio.studioinvokediagnosticsdrawer.events',
                  'Events',
                )}
              </span>
              <div style={eventListStyle}>
                {invokeResult.events.length === 0 ? (
                  <Typography.Text style={helperTextStyle} type="secondary">
                    {t(
                      'pages.studio.studioinvokediagnosticsdrawer.run.has.no.structured.events',
                      'Currently Run has no structured events.',
                    )}
                  </Typography.Text>
                ) : (
                  <>
                    <RuntimeEventPreviewPanel
                      events={invokeResult.events}
                      title={t(
                        'pages.studio.studioinvokediagnosticsdrawer.events.count',
                        'Events ({count})',
                        { count: invokeResult.events.length },
                      )}
                    />
                    {invokeResult.events.map((event) => (
                      <div
                        key={getEventKey(event)}
                        style={eventRowStyle}
                      >
                        <span style={eventIndexStyle}>
                          #{invokeResult.events.indexOf(event) + 1}
                        </span>
                        <span title={event.type} style={eventTypeStyle}>
                          {event.type}
                        </span>
                        <span style={eventPreviewStyle}>
                          {getEventPreview(event)}
                        </span>
                      </div>
                    ))}
                  </>
                )}
              </div>
            </div>

            <div style={drawerSectionStyle}>
              <span style={sectionLabelStyle}>
                {t(
                  'pages.studio.studioinvokediagnosticsdrawer.run.details',
                  'Run details',
                )}
              </span>
              <div style={metadataGridStyle}>
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.status.2',
                    'Status',
                  )}
                  value={<MetadataValue value={statusLabel} />}
                />
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.endpoint.2',
                    'Endpoint',
                  )}
                  value={
                    <MetadataValue
                      value={getUserFacingIdentifierLabel(
                        endpointLabel,
                        t(
                          'pages.studio.studioinvokediagnosticsdrawer.endpoint.ready',
                          'Endpoint ready',
                        ),
                      )}
                    />
                  }
                />
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.started.at',
                    'Started at',
                  )}
                  value={<MetadataValue value={startedAtLabel} />}
                />
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.finished.at',
                    'Finished at',
                  )}
                  value={<MetadataValue value={finishedAtLabel} />}
                />
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.duration',
                    'Duration',
                  )}
                  value={<MetadataValue value={runElapsedLabel} />}
                />
                <MetadataItem
                  label={t(
                    'pages.studio.studioinvokediagnosticsdrawer.event.count',
                    'Event count',
                  )}
                  value={
                    <div style={contractValueStyle}>
                      {invokeResult.eventCount || invokeResult.events.length}
                    </div>
                  }
                />
              </div>
            </div>

            <div style={drawerSectionStyle}>
              <span style={sectionLabelStyle}>
                {t('pages.studio.studioinvokediagnosticsdrawer.output', 'Output')}
              </span>
              {outputText ? (
                <p style={outputTextStyle}>{sanitizeVisibleText(outputText)}</p>
              ) : (
                <Typography.Text style={helperTextStyle} type="secondary">
                  {t(
                    'pages.studio.studioinvokediagnosticsdrawer.no.displayable.output',
                    'No displayable output.',
                  )}
                </Typography.Text>
              )}
            </div>

            <details style={drawerSectionStyle}>
              <summary style={contractValueStyle}>
                {t(
                  'pages.studio.studioinvokediagnosticsdrawer.event.payload',
                  'Event payload',
                )}
              </summary>
              <pre style={rawOutputStyle}>
                {sanitizeVisibleText(currentRawOutput) ||
                  t(
                    'pages.studio.studioinvokediagnosticsdrawer.no.raw.json',
                    'No raw JSON.',
                  )}
              </pre>
            </details>
          </>
        )}
      </div>
    </AevatarContextDrawer>
  );
};

export default StudioInvokeDiagnosticsDrawer;
