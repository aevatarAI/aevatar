import {
  ClearOutlined,
  CloseOutlined,
  FileImageOutlined,
  FileOutlined,
  PaperClipOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Button, Input, Typography } from 'antd';
import React from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import type { InvokeResultState } from './StudioMemberInvokePanel.currentRun';
import {
  helperTextStyle,
  studioInvokeColors,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokeComposerPanelProps = {
  readonly acceptedFileTypes?: string;
  readonly attachments?: readonly File[];
  readonly blockedReason?: string;
  readonly canInvoke: boolean;
  readonly defaultPrompt: string;
  readonly enableFileAttachments?: boolean;
  readonly formError: string;
  readonly invokeStatus: InvokeResultState['status'];
  readonly isHistoricalRunSelected?: boolean;
  readonly isChatEndpoint: boolean;
  readonly layout?: 'panel' | 'dock' | 'member-run';
  readonly prompt: string;
  readonly currentRunPrompt?: string;
  readonly onAbort: () => void;
  readonly onAttachmentsAdd?: (files: readonly File[]) => void;
  readonly onAttachmentRemove?: (index: number) => void;
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
  background: 'transparent',
  display: 'grid',
  gap: 8,
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
  background: '#fbfcfe',
  borderColor: '#d8dee9',
  borderRadius: 10,
  fontSize: 14,
  lineHeight: 1.7,
  minHeight: 64,
  padding: '12px 14px',
};

const memberRunLauncherRowStyle: React.CSSProperties = {
  alignItems: 'stretch',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  minWidth: 0,
};

const memberRunLauncherInputStyle: React.CSSProperties = {
  ...memberRunInputStyle,
  flex: '1 1 460px',
  minWidth: 260,
};

const memberRunActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
  justifyContent: 'flex-end',
};

const memberRunInlineActionsStyle: React.CSSProperties = {
  ...memberRunActionsStyle,
  alignContent: 'stretch',
  flex: '0 0 auto',
};

const memberRunPrimaryButtonStyle: React.CSSProperties = {
  minHeight: 42,
  minWidth: 128,
};

const memberRunSecondaryButtonStyle: React.CSSProperties = {
  minHeight: 42,
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
  color: '#475569',
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
  background: '#ecfdf5',
  border: '1px solid #bbf7d0',
  borderRadius: 999,
  color: '#047857',
  flex: '0 0 auto',
  fontSize: 11,
  fontWeight: 700,
  lineHeight: '18px',
  padding: '2px 8px',
  whiteSpace: 'nowrap',
};

const submittedReceiptStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 10,
  display: 'grid',
  flex: '1 1 460px',
  gap: 4,
  minHeight: 42,
  minWidth: 260,
  padding: '10px 12px',
};

const submittedReceiptLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 0,
  lineHeight: '14px',
  textTransform: 'uppercase',
};

const submittedReceiptTextStyle: React.CSSProperties = {
  color: '#0f172a',
  fontSize: 13,
  lineHeight: '18px',
  maxHeight: 54,
  overflow: 'hidden',
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const attachmentToolbarStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  minWidth: 0,
};

const attachmentListStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '1 1 260px',
  flexWrap: 'wrap',
  gap: 6,
  minWidth: 0,
};

const attachmentChipStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 8,
  color: '#334155',
  display: 'inline-flex',
  gap: 6,
  maxWidth: 240,
  minHeight: 28,
  minWidth: 0,
  padding: '3px 5px 3px 8px',
};

const attachmentNameStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '16px',
  maxWidth: 154,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const attachmentMetaStyle: React.CSSProperties = {
  color: '#64748b',
  flex: '0 0 auto',
  fontSize: 11,
  lineHeight: '14px',
};

const attachmentEmptyStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 12,
  lineHeight: '18px',
};

const composerGuidanceStyle: React.CSSProperties = {
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 8,
  color: '#475569',
  display: 'grid',
  gap: 2,
  minWidth: 0,
  padding: '8px 10px',
};

function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return '0 B';
  }

  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const kib = bytes / 1024;
  if (kib < 1024) {
    return `${kib.toFixed(kib >= 10 ? 0 : 1)} KB`;
  }

  const mib = kib / 1024;
  return `${mib.toFixed(mib >= 10 ? 0 : 1)} MB`;
}

function getFileIcon(file: File): React.ReactNode {
  return file.type.startsWith('image/') ? <FileImageOutlined /> : <FileOutlined />;
}

function getAttachmentKey(file: File): string {
  return [
    file.name,
    file.size,
    file.lastModified,
    file.type || 'file',
  ].join(':');
}

export const StudioMemberInvokeComposerPanel: React.FC<
  StudioMemberInvokeComposerPanelProps
> = ({
  acceptedFileTypes,
  attachments = [],
  blockedReason = '',
  canInvoke,
  defaultPrompt,
  enableFileAttachments = false,
  formError,
  invokeStatus,
  isHistoricalRunSelected = false,
  isChatEndpoint,
  layout = 'panel',
  currentRunPrompt,
  onAbort,
  onAttachmentsAdd,
  onAttachmentRemove,
  onClear,
  onInvoke,
  onPromptChange,
  prompt,
}) => {
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const isRunning = invokeStatus === 'running';
  const isDockLayout = layout === 'dock';
  const isMemberRunLayout = layout === 'member-run';
  const inputLocked = isMemberRunLayout && isRunning;
  const displayedPrompt = inputLocked ? currentRunPrompt || prompt : prompt;
  const canAttachFiles =
    enableFileAttachments && isChatEndpoint && isMemberRunLayout;
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
        "pages.studio.studiomemberinvokesetuppanels.run.launcher",
        "Run launcher",
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
  const renderAttachmentToolbar = () => {
    if (!canAttachFiles) {
      return null;
    }

    return (
      <div
        data-testid="studio-invoke-attachment-toolbar"
        style={attachmentToolbarStyle}
      >
        <input
          ref={fileInputRef}
          aria-label={t(
            "pages.studio.studiomemberinvokesetuppanels.attach.files.input",
            "Attach files",
          )}
          accept={acceptedFileTypes}
          multiple
          style={{ display: 'none' }}
          type="file"
          onChange={(event) => {
            const nextFiles = Array.from(event.target.files ?? []);
            if (nextFiles.length > 0) {
              onAttachmentsAdd?.(nextFiles);
            }
            event.currentTarget.value = '';
          }}
        />
        <AevatarTooltip
          title={t(
            "pages.studio.studiomemberinvokesetuppanels.attach.files",
            "Attach files",
          )}
        >
          <Button
            aria-label={t(
              "pages.studio.studiomemberinvokesetuppanels.attach.files.button",
              "Add files",
            )}
            disabled={inputLocked}
            icon={<PaperClipOutlined />}
            onClick={() => fileInputRef.current?.click()}
          />
        </AevatarTooltip>
        <div style={attachmentListStyle}>
          {attachments.length === 0 ? (
            <span style={attachmentEmptyStyle}>
              {t(
                "pages.studio.studiomemberinvokesetuppanels.no.files.attached",
                "No files attached",
              )}
            </span>
          ) : (
            attachments.map((file, index) => (
              <span
                key={getAttachmentKey(file)}
                data-testid="studio-invoke-attachment-chip"
                style={attachmentChipStyle}
              >
                {getFileIcon(file)}
                <AevatarTooltip title={`${file.name} · ${file.type || 'file'} · ${formatFileSize(file.size)}`}>
                  <span style={attachmentNameStyle}>{file.name}</span>
                </AevatarTooltip>
                <span style={attachmentMetaStyle}>{formatFileSize(file.size)}</span>
                <Button
                  aria-label={t(
                    "pages.studio.studiomemberinvokesetuppanels.remove.attachment",
                    "Remove {name}",
                    { name: file.name },
                  )}
                  disabled={inputLocked}
                  icon={<CloseOutlined />}
                  onClick={() => onAttachmentRemove?.(index)}
                  size="small"
                  type="text"
                />
              </span>
            ))
          )}
        </div>
      </div>
    );
  };
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
        {isMemberRunLayout ? (
          <div style={memberRunLauncherRowStyle}>
            {inputLocked ? (
              <fieldset
                aria-label={inputAriaLabel}
                data-testid="studio-invoke-submitted-input-receipt"
                style={submittedReceiptStyle}
              >
                <legend style={submittedReceiptLabelStyle}>
                  {t(
                    "pages.studio.studiomemberinvokesetuppanels.submitted.input",
                    "Submitted input",
                  )}
                </legend>
                <span style={submittedReceiptTextStyle}>
                  {displayedPrompt ||
                    t(
                      "pages.studio.studiomemberinvokesetuppanels.no.input.captured",
                      "No input captured.",
                    )}
                </span>
              </fieldset>
            ) : (
              <Input.TextArea
                aria-label={inputAriaLabel}
                autoSize={{ minRows: 2, maxRows: 5 }}
                placeholder={promptPlaceholder}
                style={memberRunLauncherInputStyle}
                value={displayedPrompt}
                onChange={(event) => onPromptChange(event.target.value)}
              />
            )}
            <div
              data-testid="studio-invoke-playground-actions"
              style={memberRunInlineActionsStyle}
            >
              <Button
                danger={isRunning}
                disabled={!isRunning && !canInvoke}
                icon={primaryButtonIcon}
                onClick={isRunning ? onAbort : onInvoke}
                size="large"
                style={memberRunPrimaryButtonStyle}
                type="primary"
              >
                {primaryButtonLabel}
              </Button>
              <Button
                disabled={isRunning}
                icon={<ClearOutlined />}
                onClick={onClear}
                style={memberRunSecondaryButtonStyle}
              >
                {t("pages.studio.studiomemberinvokesetuppanels.clear.4", "Clear")}
              </Button>
            </div>
          </div>
        ) : isDockLayout ? (
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
                ? { minRows: 3, maxRows: 6 }
                : { minRows: 4, maxRows: 8 }
            }
            placeholder={promptPlaceholder}
            readOnly={inputLocked}
            style={isMemberRunLayout ? memberRunInputStyle : undefined}
            value={displayedPrompt}
            onChange={(event) => onPromptChange(event.target.value)}
          />
        )}
        {renderAttachmentToolbar()}
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

      {isDockLayout || isMemberRunLayout ? null : (
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
