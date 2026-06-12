import {
  ClearOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Button, Input, Typography } from 'antd';
import React from 'react';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import type { InvokeResultState } from './StudioMemberInvokePanel.currentRun';
import {
  helperTextStyle,
  studioInvokeColors,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokeComposerPanelProps = {
  readonly blockedReason?: string;
  readonly canInvoke: boolean;
  readonly defaultPrompt: string;
  readonly formError: string;
  readonly invokeStatus: InvokeResultState['status'];
  readonly isHistoricalRunSelected?: boolean;
  readonly isChatEndpoint: boolean;
  readonly layout?: 'panel' | 'dock';
  readonly prompt: string;
  readonly onAbort: () => void;
  readonly onClear: () => void;
  readonly onInvoke: () => void;
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

const composerGuidanceStyle: React.CSSProperties = {
  background: studioInvokeColors.surfaceActive,
  border: `1px solid ${studioInvokeColors.borderStrong}`,
  borderRadius: 8,
  color: studioInvokeColors.textSoft,
  display: 'grid',
  gap: 2,
  minWidth: 0,
  padding: '8px 10px',
};

export const StudioMemberInvokeComposerPanel: React.FC<
  StudioMemberInvokeComposerPanelProps
> = ({
  blockedReason = '',
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
  onPromptChange,
  prompt,
}) => {
  const isRunning = invokeStatus === 'running';
  const promptPlaceholder =
    defaultPrompt || t("pages.studio.studiomemberinvokesetuppanels.prompt.invoke", "Describe what the workflow should do.");
  const primaryButtonLabel = isRunning
    ? t("pages.studio.studiomemberinvokesetuppanels.stop.current.run", "Stop")
    : t("pages.studio.studiomemberinvokesetuppanels.run.workflow", "Run workflow");
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
          <span style={promptKickerStyle}>{t("pages.studio.studiomemberinvokesetuppanels.prompt.3", "Request")}</span>
          {layout === 'dock' ? (
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {t("pages.studio.studiomemberinvokesetuppanels.new.run.per.request", "Each request starts a new run")}</Typography.Text>
          ) : null}
        </div>
        {layout === 'dock' ? (
          <div
            data-testid="studio-invoke-playground-actions"
            style={dockComposerRowStyle}
          >
            <Input.TextArea
              aria-label={t("pages.studio.studiomemberinvokesetuppanels.copy", "Workflow request input")}
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
            {layout === 'dock' ? (
              <Button
                icon={<ClearOutlined />}
                size="large"
                style={dockComposerSecondaryButtonStyle}
                onClick={onClear}
              >
                {t("pages.studio.studiomemberinvokesetuppanels.clear.3", "Clear")}</Button>
            ) : null}
          </div>
        ) : (
          <Input.TextArea
            aria-label={t("pages.studio.studiomemberinvokesetuppanels.copy.2", "Workflow request input")}
            autoSize={{ minRows: 4, maxRows: 8 }}
            placeholder={promptPlaceholder}
            value={prompt}
            onChange={(event) => onPromptChange(event.target.value)}
          />
        )}
        {formError ? (
          <Typography.Text type="danger">{formError}</Typography.Text>
        ) : isHistoricalRunSelected ? (
          <div
            data-testid="studio-invoke-composer-guidance"
            style={composerGuidanceStyle}
          >
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {t("pages.studio.studiomemberinvokesetuppanels.historical.run.is.read.only", "Historical run is read-only. Sending this request starts a new run and fresh Observe handoff.")}</Typography.Text>
          </div>
        ) : !canInvoke ? (
          <div
            data-testid="studio-invoke-composer-guidance"
            style={composerGuidanceStyle}
          >
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {blockedReason || t("pages.studio.studiomemberinvokesetuppanels.team.member.endpoint", "Select a runnable Team member and endpoint.")}
            </Typography.Text>
          </div>
        ) : isChatEndpoint ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            {t("pages.studio.studiomemberinvokesetuppanels.prompt.invoke.invoke.run", "Describe the work this workflow should perform. Each request starts a new run.")}</Typography.Text>
        ) : (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t("pages.studio.studiomemberinvokesetuppanels.prompt.invoke.invoke.run.2", "Describe the work this workflow should perform. Each request starts a new run.")}</Typography.Text>
        )}
      </div>

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
            {t("pages.studio.studiomemberinvokesetuppanels.stop.4", "Stop")}</Button>
          <Button icon={<ClearOutlined />} onClick={onClear}>
            {t("pages.studio.studiomemberinvokesetuppanels.clear.4", "Clear")}</Button>
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
      title={t("pages.studio.studiomemberinvokesetuppanels.copy.3", "Request")}
      titleHelp={t("pages.studio.studiomemberinvokesetuppanels.prompt.2", "Describe the work to run against this workflow member.")}
    >
      {content}
    </AevatarPanel>
  );
};
