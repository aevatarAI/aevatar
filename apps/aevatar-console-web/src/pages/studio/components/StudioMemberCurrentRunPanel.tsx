import { Alert, Tabs, Typography } from 'antd';
import React, { useMemo } from 'react';
import { RuntimeEventPreviewPanel } from '@/shared/agui/runtimeConversationPresentation';
import type {
  CurrentRunRequest,
  InvokeResultState,
  StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import {
  contractStatusPillBaseStyle,
  contractValueStyle,
  formatHistoryTimestamp,
  getInvokeRunStatusLabel,
  getInvokeStatusTone,
  helperTextStyle,
  monoFontFamily,
  studioInvokeColors,
  trimPreview,
} from './studioInvokeUi';

type StudioMemberCurrentRunPanelProps = {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly consoleMinHeight: number;
  readonly currentRawOutput: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly isChatEndpoint: boolean;
  readonly invokeResult: InvokeResultState;
  readonly activeTab: 'conversation' | 'timeline' | 'events';
  readonly onTabChange: (tab: 'conversation' | 'timeline' | 'events') => void;
  readonly transcriptAnchorRef: React.RefObject<HTMLDivElement | null>;
};

function getCurrentResultStatusStyle(
  status: InvokeResultState['status'],
): React.CSSProperties {
  const tone = getInvokeStatusTone(status);
  return {
    background: tone.background,
    border: `1px solid ${tone.border}`,
    color: tone.color,
  };
}

const requestSummaryStyle: React.CSSProperties = {
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 12,
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const requestSummaryRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const consolePaneStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minHeight: 220,
  minWidth: 0,
};

const emptyConversationStyle: React.CSSProperties = {
  alignItems: 'center',
  color: studioInvokeColors.muted,
  display: 'flex',
  fontSize: 14,
  justifyContent: 'center',
  lineHeight: 1.7,
  minHeight: 220,
  minWidth: 0,
  textAlign: 'center',
};

const resultSurfaceStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minHeight: 0,
  minWidth: 0,
};

const transcriptStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  maxHeight: 360,
  minHeight: 0,
  minWidth: 0,
  overflowY: 'auto',
  paddingRight: 4,
};

const bubbleBaseStyle: React.CSSProperties = {
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 14,
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  maxWidth: '88%',
  minWidth: 0,
  padding: '12px 14px',
};

const plainResultStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 14,
  color: studioInvokeColors.text,
  minWidth: 0,
  padding: '14px 16px',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const rawOutputStyle: React.CSSProperties = {
  background: studioInvokeColors.rawSurface,
  borderRadius: 14,
  color: studioInvokeColors.rawText,
  fontFamily: monoFontFamily,
  fontSize: 12,
  lineHeight: 1.6,
  margin: 0,
  maxHeight: 360,
  minHeight: 0,
  minWidth: 0,
  overflow: 'auto',
  padding: 16,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};

const emptyConsoleTextStyle: React.CSSProperties = {
  color: studioInvokeColors.muted,
  fontSize: 14,
  lineHeight: 1.7,
  minWidth: 0,
};

const consoleTabLabelStyle: React.CSSProperties = {
  fontWeight: 700,
};

const StudioMemberCurrentRunPanel: React.FC<StudioMemberCurrentRunPanelProps> = ({
  activeTab,
  chatMessages,
  consoleMinHeight,
  currentRawOutput,
  currentRunHasData,
  currentRunRequest,
  invokeResult,
  isChatEndpoint,
  onTabChange,
  transcriptAnchorRef,
}) => {
  const currentResultStatusLabel = getInvokeRunStatusLabel(
    invokeResult.status,
  );
  const errorDescription =
    invokeResult.errorCode && invokeResult.error
      ? `${invokeResult.error}（${invokeResult.errorCode}）`
      : invokeResult.errorCode || invokeResult.error;
  const consoleItems = useMemo(
    () => [
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {currentRunHasData ? (
              <div style={resultSurfaceStyle}>
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 10,
                    justifyContent: 'space-between',
                  }}
                >
                  <span
                    style={{
                      ...contractStatusPillBaseStyle,
                      ...getCurrentResultStatusStyle(invokeResult.status),
                    }}
                  >
                    {currentResultStatusLabel}
                  </span>
                  {currentRunRequest?.startedAt ? (
                    <Typography.Text style={helperTextStyle} type="secondary">
                      开始于 {formatHistoryTimestamp(currentRunRequest.startedAt)}
                    </Typography.Text>
                  ) : null}
                </div>

                {currentRunRequest ? (
                  <div style={requestSummaryStyle}>
                    <div style={requestSummaryRowStyle}>
                      <Typography.Text type="secondary">当前输入</Typography.Text>
                      <div style={contractValueStyle}>
                        {currentRunRequest.prompt ||
                          trimPreview(currentRunRequest.payloadTypeUrl, 96) ||
                          '这次调用使用了类型化载荷。'}
                      </div>
                    </div>
                    {!isChatEndpoint &&
                    (currentRunRequest.payloadTypeUrl ||
                      currentRunRequest.payloadBase64) ? (
                      <div style={requestSummaryRowStyle}>
                        {currentRunRequest.payloadTypeUrl ? (
                          <Typography.Text style={helperTextStyle} type="secondary">
                            类型：{currentRunRequest.payloadTypeUrl}
                          </Typography.Text>
                        ) : null}
                        {currentRunRequest.payloadBase64 ? (
                          <Typography.Text style={helperTextStyle} type="secondary">
                            已附带 payloadBase64
                          </Typography.Text>
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                ) : null}

                {invokeResult.status === 'error' && errorDescription ? (
                  <Alert
                    showIcon
                    message="这次调用失败了。"
                    description={errorDescription}
                    type="error"
                  />
                ) : null}

                {chatMessages.length > 0 ? (
                  <div
                    data-testid="studio-invoke-chat-transcript"
                    style={transcriptStyle}
                  >
                    {chatMessages.map((message) => {
                      const isAssistant = message.role === 'assistant';
                      return (
                        <div
                          key={message.id}
                          style={{
                            alignItems: isAssistant ? 'flex-start' : 'flex-end',
                            display: 'flex',
                            flexDirection: 'column',
                            gap: 4,
                          }}
                        >
                          <div
                            style={{
                              ...bubbleBaseStyle,
                              background: isAssistant
                                ? studioInvokeColors.panel
                                : studioInvokeColors.assistantSoft,
                              borderColor: isAssistant
                                ? studioInvokeColors.border
                                : studioInvokeColors.borderStrong,
                            }}
                          >
                            <div
                              style={{
                                color: studioInvokeColors.meta,
                                fontSize: 11,
                                fontWeight: 700,
                                textTransform: 'uppercase',
                              }}
                            >
                              {isAssistant ? '成员响应' : '你'}
                            </div>
                            <div
                              style={{
                                color: message.error
                                  ? studioInvokeColors.danger
                                  : studioInvokeColors.text,
                                lineHeight: 1.7,
                                whiteSpace: 'pre-wrap',
                                wordBreak: 'break-word',
                              }}
                            >
                              {message.content ||
                                (message.status === 'streaming'
                                  ? '正在响应…'
                                  : '')}
                            </div>
                            {message.thinking ? (
                              <div
                                style={{
                                  borderTop: `1px solid ${studioInvokeColors.border}`,
                                  color: studioInvokeColors.meta,
                                  fontSize: 12,
                                  lineHeight: 1.6,
                                  paddingTop: 8,
                                  whiteSpace: 'pre-wrap',
                                }}
                              >
                                {message.thinking}
                              </div>
                            ) : null}
                          </div>
                        </div>
                      );
                    })}
                    <div ref={transcriptAnchorRef} />
                  </div>
                ) : invokeResult.status === 'running' ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    调用已经发出，当前结果会在这里持续更新。
                  </Typography.Text>
                ) : invokeResult.responseJson ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    这次结构化调用已经返回结果。切到 Events 可以查看完整返回体。
                  </Typography.Text>
                ) : invokeResult.finalOutput ? (
                  <div style={plainResultStyle}>{invokeResult.finalOutput}</div>
                ) : invokeResult.assistantText ? (
                  <div style={plainResultStyle}>{invokeResult.assistantText}</div>
                ) : invokeResult.status === 'error' ? (
                  <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                    这次调用失败了，没有额外结果文本。
                  </Typography.Text>
                ) : null}
              </div>
            ) : (
              <div style={{ ...emptyConversationStyle, minHeight: consoleMinHeight }}>
                No conversation yet. Send a prompt to start the run.
              </div>
            )}
          </div>
        ),
        key: 'conversation',
        label: <span style={consoleTabLabelStyle}>Conversation</span>,
      },
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {invokeResult.events.length > 0 ? (
              <RuntimeEventPreviewPanel
                events={invokeResult.events}
                title={`观测事件（${invokeResult.events.length}）`}
              />
            ) : (
              <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                当前 run 还没有可展示的追踪事件。
              </Typography.Text>
            )}

            {(invokeResult.steps.length > 0 || invokeResult.toolCalls.length > 0) && (
              <div style={requestSummaryStyle}>
                <Typography.Text type="secondary">调试概览</Typography.Text>
                <Typography.Text style={contractValueStyle}>
                  步骤 {invokeResult.steps.length} 个，工具调用{' '}
                  {invokeResult.toolCalls.length} 个。
                </Typography.Text>
              </div>
            )}
          </div>
        ),
        key: 'timeline',
        label: <span style={consoleTabLabelStyle}>Timeline</span>,
      },
      {
        children: (
          <div style={{ ...consolePaneStyle, minHeight: consoleMinHeight }}>
            {currentRawOutput ? (
              <pre style={rawOutputStyle}>{currentRawOutput}</pre>
            ) : (
              <Typography.Text style={emptyConsoleTextStyle} type="secondary">
                当前 run 没有额外原始输出。
              </Typography.Text>
            )}
          </div>
        ),
        key: 'events',
        label: <span style={consoleTabLabelStyle}>Events</span>,
      },
    ],
    [
      chatMessages,
      consoleMinHeight,
      currentRawOutput,
      currentResultStatusLabel,
      currentRunHasData,
      currentRunRequest,
      errorDescription,
      invokeResult,
      isChatEndpoint,
      transcriptAnchorRef,
    ],
  );

  return (
    <Tabs
      activeKey={activeTab}
      items={consoleItems}
      onChange={(value) =>
        onTabChange(value as 'conversation' | 'timeline' | 'events')
      }
      style={{ minHeight: 0 }}
    />
  );
};

export default StudioMemberCurrentRunPanel;
