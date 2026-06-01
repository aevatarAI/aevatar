import {
  ClearOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Button, Collapse, Input, Typography } from 'antd';
import React from 'react';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import type { InvokeResultState } from './StudioMemberInvokePanel.currentRun';
import {
  helperTextStyle,
  studioInvokeColors,
} from './studioInvokeUi';

type StudioMemberInvokeComposerPanelProps = {
  readonly canInvoke: boolean;
  readonly defaultPrompt: string;
  readonly formError: string;
  readonly invokeStatus: InvokeResultState['status'];
  readonly isHistoricalRunSelected?: boolean;
  readonly isChatEndpoint: boolean;
  readonly layout?: 'panel' | 'dock';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly onAbort: () => void;
  readonly onClear: () => void;
  readonly onInvoke: () => void;
  readonly onPayloadBase64Change: (value: string) => void;
  readonly onPayloadTypeUrlChange: (value: string) => void;
  readonly onPromptChange: (value: string) => void;
};

const playgroundActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-start',
};

const dockComposerStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  display: 'grid',
  gap: 6,
  minWidth: 0,
};

const dockComposerRowStyle: React.CSSProperties = {
  alignItems: 'flex-end',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  minWidth: 0,
};

const dockComposerInputStyle: React.CSSProperties = {
  flex: '1 1 280px',
  minWidth: 0,
};

const dockComposerPrimaryButtonStyle: React.CSSProperties = {
  flex: '0 0 auto',
};

const dockComposerSecondaryButtonStyle: React.CSSProperties = {
  flex: '0 0 auto',
};

const promptLabelRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  justifyContent: 'space-between',
  minWidth: 0,
};

const promptKickerStyle: React.CSSProperties = {
  color: studioInvokeColors.accent,
  fontSize: 10.5,
  fontWeight: 800,
  letterSpacing: 1,
  lineHeight: '14px',
  textTransform: 'uppercase',
};

const promptDockHintStyle: React.CSSProperties = {
  ...helperTextStyle,
  fontSize: 12,
  lineHeight: '16px',
};

const typedPayloadGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  minWidth: 0,
};

export const StudioMemberInvokeComposerPanel: React.FC<
  StudioMemberInvokeComposerPanelProps
> = ({
  canInvoke,
  defaultPrompt,
  formError,
  invokeStatus,
  isHistoricalRunSelected = false,
  isChatEndpoint,
  layout = 'panel',
  onAbort,
  onClear,
  onInvoke,
  onPayloadBase64Change,
  onPayloadTypeUrlChange,
  onPromptChange,
  payloadBase64,
  payloadTypeUrl,
  prompt,
}) => {
  const isRunning = invokeStatus === 'running';
  const promptPlaceholder =
    defaultPrompt || '输入 Prompt，发起一次独立 Invoke。';
  const primaryButtonLabel = isRunning ? 'Stop' : 'Invoke';
  const primaryButtonIcon = isRunning ? (
    <StopOutlined />
  ) : (
    <PlayCircleOutlined />
  );
  const content = (
    <div
      style={
        layout === 'dock' ? dockComposerStyle : { display: 'grid', gap: 12 }
      }
    >
      <div style={{ display: 'grid', gap: 6, minWidth: 0 }}>
        <div style={promptLabelRowStyle}>
          <span style={promptKickerStyle}>Prompt</span>
          {layout === 'dock' ? (
            <Typography.Text style={promptDockHintStyle} type="secondary">
              New run per Invoke
            </Typography.Text>
          ) : null}
        </div>
        {layout === 'dock' ? (
          <div
            data-testid="studio-invoke-playground-actions"
            style={dockComposerRowStyle}
          >
            <Input.TextArea
              aria-label="调用请求输入"
              autoSize={{ minRows: 1, maxRows: 4 }}
              placeholder={promptPlaceholder}
              style={dockComposerInputStyle}
              value={prompt}
              onChange={(event) => onPromptChange(event.target.value)}
            />
            <Button
              disabled={!isRunning && !canInvoke}
              icon={primaryButtonIcon}
              onClick={isRunning ? onAbort : onInvoke}
              size="large"
              style={dockComposerPrimaryButtonStyle}
              type="primary"
            >
              {primaryButtonLabel}
            </Button>
            {layout === 'dock' && !isRunning ? (
              <Button
                disabled
                icon={<StopOutlined />}
                size="large"
                style={dockComposerSecondaryButtonStyle}
              >
                Stop
              </Button>
            ) : null}
            {layout === 'dock' ? (
              <Button
                icon={<ClearOutlined />}
                size="large"
                style={dockComposerSecondaryButtonStyle}
                onClick={onClear}
              >
                Clear
              </Button>
            ) : null}
          </div>
        ) : (
          <Input.TextArea
            aria-label="调用请求输入"
            autoSize={{ minRows: 4, maxRows: 8 }}
            placeholder={promptPlaceholder}
            value={prompt}
            onChange={(event) => onPromptChange(event.target.value)}
          />
        )}
        {formError ? (
          <Typography.Text type="danger">{formError}</Typography.Text>
        ) : isHistoricalRunSelected ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            Sending this prompt will create a new independent Run.
          </Typography.Text>
        ) : !canInvoke ? (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            请选择可调用的 Team member 和 endpoint。
          </Typography.Text>
        ) : isChatEndpoint ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            输入 Prompt，发起一次独立 Invoke。每次 Invoke 都会创建新的 Run。
          </Typography.Text>
        ) : (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            输入 Prompt，发起一次独立 Invoke。每次 Invoke 都会创建新的 Run。
          </Typography.Text>
        )}
      </div>

      {!isChatEndpoint ? (
        <Collapse
          bordered={false}
          items={[
            {
              key: 'typed-payload',
              label: 'Advanced typed payload',
              children: (
                <div style={typedPayloadGridStyle}>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      Payload type URL
                    </Typography.Text>
                    <Input
                      aria-label="Payload type URL"
                      placeholder="type.googleapis.com/google.protobuf.StringValue"
                      value={payloadTypeUrl}
                      onChange={(event) =>
                        onPayloadTypeUrlChange(event.target.value)
                      }
                    />
                  </div>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      Payload base64
                    </Typography.Text>
                    <Input.TextArea
                      aria-label="Payload base64"
                      autoSize={{ minRows: 2, maxRows: 5 }}
                      placeholder="Paste encoded protobuf payload when this type cannot be built from text."
                      value={payloadBase64}
                      onChange={(event) =>
                        onPayloadBase64Change(event.target.value)
                      }
                    />
                  </div>
                </div>
              ),
            },
          ]}
          size="small"
        />
      ) : null}

      {layout === 'dock' ? null : (
        <div
          data-testid="studio-invoke-playground-actions"
          style={playgroundActionsStyle}
        >
          <Button
            disabled={!isRunning && !canInvoke}
            icon={primaryButtonIcon}
            onClick={isRunning ? onAbort : onInvoke}
            type="primary"
          >
            {primaryButtonLabel}
          </Button>
          <Button disabled={!isRunning} icon={<StopOutlined />} onClick={onAbort}>
            Stop
          </Button>
          <Button icon={<ClearOutlined />} onClick={onClear}>
            Clear
          </Button>
        </div>
      )}
    </div>
  );

  if (layout === 'dock') {
    return content;
  }

  return (
    <AevatarPanel
      layoutMode="document"
      padding={14}
      title="调试台"
      titleHelp="先输入 prompt 或载荷，再直接执行当前成员调用。"
    >
      {content}
    </AevatarPanel>
  );
};
