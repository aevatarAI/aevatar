import {
  CloseCircleFilled,
  CopyOutlined,
  ExclamationCircleFilled,
  ReloadOutlined,
  UnorderedListOutlined,
} from '@ant-design/icons';
import { Button, Typography } from 'antd';
import React from 'react';
import {
  getStudioInvokeObserveHandoffText,
  type CurrentRunRequest,
  type InvokeResultState,
  type StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import {
  helperTextStyle,
  studioInvokeColors,
  trimOptional,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type RunViewMode = 'latest' | 'historical';
type RunOutputTab = 'output' | 'timeline' | 'events' | 'metadata';
type CurrentRunPresentation = 'default' | 'member-run';

type StudioMemberCurrentRunPanelProps = {
  readonly activeTab?: RunOutputTab;
  readonly activeRunCompletedAt?: number | null;
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly currentRawOutput?: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
  readonly runViewMode: RunViewMode;
  readonly presentation?: CurrentRunPresentation;
  readonly showDebugTabs?: boolean;
  readonly transcriptViewportRef: React.RefObject<HTMLDivElement | null>;
  readonly onCopyError: () => void;
  readonly onOpenDiagnostics?: () => void;
  readonly onOpenInspector?: () => void;
  readonly onRetryAsNewRun: () => void;
  readonly onTabChange?: (tab: RunOutputTab) => void;
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

function buildStatusSummary(input: {
  readonly endpointLabel: string;
  readonly invokeResult: InvokeResultState;
  readonly runElapsedLabel: string;
}): string {
  return `${getStatusLabel(input.invokeResult.status)} · ${
    input.runElapsedLabel
  } · ${input.endpointLabel || 'chat'}`;
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

const outputPaneStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
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

const responseSectionStyle: React.CSSProperties = {
  ...sectionStyle,
  background: studioInvokeColors.panel,
  borderColor: studioInvokeColors.borderStrong,
  padding: '16px 18px',
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

const emptyStateStyle: React.CSSProperties = {
  alignItems: 'center',
  background: studioInvokeColors.surface,
  border: `1px dashed ${studioInvokeColors.borderStrong}`,
  borderRadius: 8,
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

const recoveryPathStyle: React.CSSProperties = {
  background: studioInvokeColors.surfaceActive,
  border: `1px solid ${studioInvokeColors.borderStrong}`,
  borderRadius: 8,
  color: studioInvokeColors.textSoft,
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
  onOpenInspector,
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
  const openDiagnostics = onOpenDiagnostics ?? onOpenInspector ?? (() => {});

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
              <p style={bodyTextStyle}>{outputText}</p>
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
                    "This failed only the Invoke run. Retry with a smaller prompt, open diagnostics for backend signals, or return to Build/Bind if the member contract needs changes.",
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
            <p style={bodyTextStyle}>{outputText}</p>
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
        {presentation !== 'member-run' && observeHandoffText ? (
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
        ) : null}
      </div>
    );
  };

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
