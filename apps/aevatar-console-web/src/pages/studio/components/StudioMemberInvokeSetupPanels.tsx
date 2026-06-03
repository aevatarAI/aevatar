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
import { t } from "@/shared/i18n/messages";

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
  letterSpacing: 0,
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
    defaultPrompt || t("pages.studio.studiomemberinvokesetuppanels.enter.prompt.to.initiate", "Enter Prompt to initiate an independent Invoke.");
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
          <span style={promptKickerStyle}>{t("pages.studio.studiomemberinvokesetuppanels.prompt", "Prompt")}</span>
          {layout === 'dock' ? (
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {t("pages.studio.studiomemberinvokesetuppanels.new.run.per.invoke", "New run per Invoke")}</Typography.Text>
          ) : null}
        </div>
        {layout === 'dock' ? (
          <div
            data-testid="studio-invoke-playground-actions"
            style={dockComposerRowStyle}
          >
            <Input.TextArea
              aria-label={t("pages.studio.studiomemberinvokesetuppanels.call.request.input", "call request input")}
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
                {t("pages.studio.studiomemberinvokesetuppanels.stop", "Stop")}</Button>
            ) : null}
            {layout === 'dock' ? (
              <Button
                icon={<ClearOutlined />}
                size="large"
                style={dockComposerSecondaryButtonStyle}
                onClick={onClear}
              >
                {t("pages.studio.studiomemberinvokesetuppanels.clear", "Clear")}</Button>
            ) : null}
          </div>
        ) : (
          <Input.TextArea
            aria-label={t("pages.studio.studiomemberinvokesetuppanels.call.request.input.2", "call request input")}
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
            {t("pages.studio.studiomemberinvokesetuppanels.sending.this.prompt.will.create", "Sending this prompt will create a new independent Run.")}</Typography.Text>
        ) : !canInvoke ? (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t("pages.studio.studiomemberinvokesetuppanels.please.select.callable.team", "Please select a callable team member and endpoint.")}</Typography.Text>
        ) : isChatEndpoint ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            {t("pages.studio.studiomemberinvokesetuppanels.enter.prompt.to.initiate.2", "Enter Prompt to initiate an independent Invoke. Each Invoke creates a new Run.")}</Typography.Text>
        ) : (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t("pages.studio.studiomemberinvokesetuppanels.enter.prompt.to.initiate.3", "Enter Prompt to initiate an independent Invoke. Each Invoke creates a new Run.")}</Typography.Text>
        )}
      </div>

      {!isChatEndpoint ? (
        <Collapse
          bordered={false}
          defaultActiveKey={['typed-payload']}
          items={[
            {
              key: 'typed-payload',
              label: t("pages.studio.studiomemberinvokesetuppanels.advanced.typed.payload", "Advanced typed payload"),
              children: (
                <div style={typedPayloadGridStyle}>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      {t("pages.studio.studiomemberinvokesetuppanels.payload.type.url", "Payload type URL")}</Typography.Text>
                    <Input
                      aria-label={t("pages.studio.studiomemberinvokesetuppanels.payload.type.url.2", "Payload type URL")}
                      placeholder="type.googleapis.com/google.protobuf.StringValue"
                      value={payloadTypeUrl}
                      onChange={(event) =>
                        onPayloadTypeUrlChange(event.target.value)
                      }
                    />
                  </div>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      {t("pages.studio.studiomemberinvokesetuppanels.payload.base64", "Payload base64")}</Typography.Text>
                    <Input.TextArea
                      aria-label={t("pages.studio.studiomemberinvokesetuppanels.payload.base64.2", "Payload base64")}
                      autoSize={{ minRows: 2, maxRows: 5 }}
                      placeholder={t("pages.studio.studiomemberinvokesetuppanels.paste.encoded.protobuf.payload.when", "Paste encoded protobuf payload when this type cannot be built from text.")}
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
            {t("pages.studio.studiomemberinvokesetuppanels.stop.2", "Stop")}</Button>
          <Button icon={<ClearOutlined />} onClick={onClear}>
            {t("pages.studio.studiomemberinvokesetuppanels.clear.2", "Clear")}</Button>
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
      title={t("pages.studio.studiomemberinvokesetuppanels.debugging.bench", "Debugging bench")}
      titleHelp={t("pages.studio.studiomemberinvokesetuppanels.enter.prompt.or.payload", "Enter prompt or payload first, and then directly execute the current member call.")}
    >
      {content}
    </AevatarPanel>
  );
};
