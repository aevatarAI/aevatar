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
  readonly layout?: 'panel' | 'dock' | 'member-run';
  readonly prompt: string;
  readonly currentRunPrompt?: string;
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

const memberRunComposerStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  display: 'grid',
  gap: 12,
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

const memberRunInputStyle: React.CSSProperties = {
  borderRadius: 8,
  fontSize: 14,
  lineHeight: 1.7,
  padding: '12px 14px',
};

const memberRunLockedInputStyle: React.CSSProperties = {
  background: studioInvokeColors.surfaceActive,
  borderColor: studioInvokeColors.borderStrong,
  boxShadow: 'inset 0 0 0 1px rgba(22, 119, 255, 0.06)',
  color: studioInvokeColors.textSoft,
  cursor: 'default',
};

const memberRunActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'flex-end',
};

const memberRunPrimaryButtonStyle: React.CSSProperties = {
  minWidth: 112,
};

const memberRunSecondaryButtonStyle: React.CSSProperties = {
  minWidth: 92,
};

const promptLabelRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 8,
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

const memberRunStatePillStyle: React.CSSProperties = {
  background: studioInvokeColors.assistantSoft,
  border: `1px solid ${studioInvokeColors.borderStrong}`,
  borderRadius: 999,
  color: '#1d4ed8',
  flex: '0 0 auto',
  fontSize: 11,
  fontWeight: 700,
  lineHeight: '18px',
  padding: '2px 8px',
  whiteSpace: 'nowrap',
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
  currentRunPrompt,
  onAbort,
  onClear,
  onInvoke,
  onPromptChange,
  prompt,
}) => {
  const isRunning = invokeStatus === 'running';
  const isDockLayout = layout === 'dock';
  const isMemberRunLayout = layout === 'member-run';
  const inputLocked = isMemberRunLayout && isRunning;
  const displayedPrompt = inputLocked ? currentRunPrompt || prompt : prompt;
  const promptPlaceholder =
    defaultPrompt ||
    (isMemberRunLayout
      ? t(
          "pages.studio.studiomemberinvokesetuppanels.member.run.prompt.invoke",
          "Describe the task or input for this run.",
        )
      : t(
          "pages.studio.studiomemberinvokesetuppanels.prompt.invoke",
          "Describe what the workflow should do.",
        ));
  const primaryButtonLabel = isRunning
    ? isMemberRunLayout
      ? t(
          "pages.studio.studiomemberinvokesetuppanels.stop.run",
          "Stop run",
        )
      : t(
          "pages.studio.studiomemberinvokesetuppanels.stop.current.run",
          "Stop",
        )
    : isMemberRunLayout
      ? t(
          "pages.studio.studiomemberinvokesetuppanels.start.run",
          "Start run",
        )
      : t(
          "pages.studio.studiomemberinvokesetuppanels.run.workflow",
          "Run workflow",
        );
  const primaryButtonIcon = isRunning ? (
    <StopOutlined />
  ) : (
    <PlayCircleOutlined />
  );
  const promptLabel = isMemberRunLayout
    ? t(
        "pages.studio.studiomemberinvokesetuppanels.task.for.this.run",
        "Task for this run",
      )
    : t("pages.studio.studiomemberinvokesetuppanels.prompt.3", "Request");
  const inputAriaLabel = isMemberRunLayout
      ? t(
          "pages.studio.studiomemberinvokesetuppanels.run.input",
          "Run input",
        )
    : t(
        "pages.studio.studiomemberinvokesetuppanels.copy",
        "Workflow request input",
      );
  const memberRunInputStyleForState = inputLocked
    ? {
        ...memberRunInputStyle,
        ...memberRunLockedInputStyle,
      }
    : memberRunInputStyle;
  const content = (
    <div
      style={
        isDockLayout
          ? dockComposerStyle
          : isMemberRunLayout
            ? memberRunComposerStyle
            : { display: 'grid', gap: 12 }
      }
    >
      <div style={{ display: 'grid', gap: 6, minWidth: 0 }}>
        <div style={promptLabelRowStyle}>
          <span style={promptKickerStyle}>{promptLabel}</span>
          {isDockLayout ? (
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {t(
                "pages.studio.studiomemberinvokesetuppanels.new.run.per.request",
                "Each request starts a new run",
              )}
            </Typography.Text>
          ) : inputLocked ? (
            <span style={memberRunStatePillStyle}>
              {t(
                "pages.studio.studiomemberinvokesetuppanels.in.progress",
                "In progress",
              )}
            </span>
          ) : null}
        </div>
        {isDockLayout ? (
          <div
            data-testid="studio-invoke-playground-actions"
            style={dockComposerRowStyle}
          >
            <Input.TextArea
              aria-label={inputAriaLabel}
              autoSize={{ minRows: 1, maxRows: 4 }}
              placeholder={promptPlaceholder}
              style={dockComposerInputStyle}
              value={displayedPrompt}
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
            <Button
              icon={<ClearOutlined />}
              size="large"
              style={dockComposerSecondaryButtonStyle}
              onClick={onClear}
            >
              {t("pages.studio.studiomemberinvokesetuppanels.clear.3", "Clear")}
            </Button>
          </div>
        ) : (
          <Input.TextArea
            aria-label={
              isMemberRunLayout
                ? inputAriaLabel
                : t(
                    "pages.studio.studiomemberinvokesetuppanels.copy.2",
                    "Workflow request input",
                  )
            }
            autoSize={
              isMemberRunLayout
                ? { minRows: 5, maxRows: 10 }
                : { minRows: 4, maxRows: 8 }
            }
            placeholder={promptPlaceholder}
            readOnly={inputLocked}
            style={
              isMemberRunLayout ? memberRunInputStyleForState : undefined
            }
            value={displayedPrompt}
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
              {isMemberRunLayout
                ? t(
                    "pages.studio.studiomemberinvokesetuppanels.historical.run.starts.separate.run",
                    "Historical runs are read-only. Starting again creates a separate run.",
                  )
                : t(
                    "pages.studio.studiomemberinvokesetuppanels.historical.run.is.read.only",
                    "Historical run is read-only. Sending this request starts a new run and fresh Observe handoff.",
                  )}
            </Typography.Text>
          </div>
        ) : !canInvoke ? (
          <div
            data-testid="studio-invoke-composer-guidance"
            style={composerGuidanceStyle}
          >
            <Typography.Text style={promptDockHintStyle} type="secondary">
              {blockedReason ||
                t(
                  "pages.studio.studiomemberinvokesetuppanels.team.member.endpoint",
                  "Select a runnable Team member and endpoint.",
                )}
            </Typography.Text>
          </div>
        ) : isMemberRunLayout ? (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {inputLocked
              ? t(
                  "pages.studio.studiomemberinvokesetuppanels.submitted.input.locked",
                  "This submitted input is locked while the run is in progress.",
                )
              : t(
                  "pages.studio.studiomemberinvokesetuppanels.member.run.isolated",
                  "Each run is isolated. Previous runs are not sent as context.",
                )}
          </Typography.Text>
        ) : isChatEndpoint ? (
          <Typography.Text
            style={layout === 'dock' ? promptDockHintStyle : helperTextStyle}
            type="secondary"
          >
            {t(
              "pages.studio.studiomemberinvokesetuppanels.prompt.invoke.invoke.run",
              "Describe the work this workflow should perform. Each request starts a new run.",
            )}
          </Typography.Text>
        ) : (
          <Typography.Text style={promptDockHintStyle} type="secondary">
            {t(
              "pages.studio.studiomemberinvokesetuppanels.prompt.invoke.invoke.run.2",
              "Describe the work this workflow should perform. Each request starts a new run.",
            )}
          </Typography.Text>
        )}
      </div>

      {isDockLayout ? null : (
        <div
          data-testid="studio-invoke-playground-actions"
          style={
            isMemberRunLayout ? memberRunActionsStyle : playgroundActionsStyle
          }
        >
          <Button
            danger={isMemberRunLayout && isRunning}
            disabled={!isRunning && !canInvoke}
            icon={primaryButtonIcon}
            onClick={isRunning ? onAbort : onInvoke}
            size={isMemberRunLayout ? 'large' : undefined}
            style={isMemberRunLayout ? memberRunPrimaryButtonStyle : undefined}
            type="primary"
          >
            {primaryButtonLabel}
          </Button>
          {isMemberRunLayout ? null : (
            <Button
              disabled={!isRunning}
              icon={<StopOutlined />}
              onClick={onAbort}
            >
              {t("pages.studio.studiomemberinvokesetuppanels.stop.4", "Stop")}
            </Button>
          )}
          <Button
            disabled={isMemberRunLayout && isRunning}
            icon={<ClearOutlined />}
            onClick={onClear}
            style={
              isMemberRunLayout ? memberRunSecondaryButtonStyle : undefined
            }
          >
            {t("pages.studio.studiomemberinvokesetuppanels.clear.4", "Clear")}
          </Button>
        </div>
      )}
    </div>
  );

  if (isDockLayout || isMemberRunLayout) {
    return content;
  }

  return (
    <AevatarPanel
      layoutMode="document"
      padding={14}
      title={t("pages.studio.studiomemberinvokesetuppanels.copy.3", "Request")}
      titleHelp={t(
        "pages.studio.studiomemberinvokesetuppanels.prompt.2",
        "Describe the work to run against this workflow member.",
      )}
    >
      {content}
    </AevatarPanel>
  );
};
