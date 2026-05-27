import {
  ClearOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Button, Collapse, Input, Typography } from 'antd';
import React from 'react';
import { useTranslation } from '@/shared/i18n/localization';
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
  const { t } = useTranslation();
  const isRunning = invokeStatus === 'running';
  const promptPlaceholder =
    defaultPrompt || t('studio.invoke.promptPlaceholder');
  const primaryButtonLabel = isRunning
    ? t('studio.invoke.primary.stop')
    : t('studio.invoke.primary.invoke');
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
          <span style={promptKickerStyle}>{t('studio.invoke.promptLabel')}</span>
          {layout === 'dock' ? (
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {t('studio.invoke.newRunShort')}
            </Typography.Text>
          ) : null}
        </div>
        {layout === 'dock' ? (
          <div
            data-testid="studio-invoke-playground-actions"
            style={dockComposerRowStyle}
          >
            <Input.TextArea
              aria-label={t('studio.invoke.promptInputAria')}
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
                {t('studio.invoke.primary.stop')}
              </Button>
            ) : null}
            {layout === 'dock' ? (
              <Button
                icon={<ClearOutlined />}
                size="large"
                style={dockComposerSecondaryButtonStyle}
                onClick={onClear}
              >
                {t('studio.invoke.clear')}
              </Button>
            ) : null}
          </div>
        ) : (
          <Input.TextArea
            aria-label={t('studio.invoke.promptInputAria')}
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
            {t('studio.invoke.sendingHistorical')}
          </Typography.Text>
        ) : !canInvoke ? (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t('studio.invoke.selectCallable')}
          </Typography.Text>
        ) : isChatEndpoint ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            {t('studio.invoke.newRunHint')}
          </Typography.Text>
        ) : (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t('studio.invoke.newRunHint')}
          </Typography.Text>
        )}
      </div>

      {!isChatEndpoint ? (
        <Collapse
          bordered={false}
          defaultActiveKey={['typed-payload']}
          items={[
            {
              key: 'typed-payload',
              label: t('studio.invoke.advancedPayload'),
              children: (
                <div style={typedPayloadGridStyle}>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      {t('studio.invoke.payloadTypeUrl')}
                    </Typography.Text>
                    <Input
                      aria-label={t('studio.invoke.payloadTypeUrl')}
                      placeholder="type.googleapis.com/google.protobuf.StringValue"
                      value={payloadTypeUrl}
                      onChange={(event) =>
                        onPayloadTypeUrlChange(event.target.value)
                      }
                    />
                  </div>
                  <div style={typedPayloadGridStyle}>
                    <Typography.Text style={helperTextStyle} type="secondary">
                      {t('studio.invoke.payloadBase64')}
                    </Typography.Text>
                    <Input.TextArea
                      aria-label={t('studio.invoke.payloadBase64')}
                      autoSize={{ minRows: 2, maxRows: 5 }}
                      placeholder={t('studio.invoke.payloadBase64Placeholder')}
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
            {t('studio.invoke.primary.stop')}
          </Button>
          <Button icon={<ClearOutlined />} onClick={onClear}>
            {t('studio.invoke.clear')}
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
      title={t('studio.invoke.debugTitle')}
      titleHelp={t('studio.invoke.debugHelp')}
    >
      {content}
    </AevatarPanel>
  );
};
