import { CopyOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Typography } from 'antd';
import React from 'react';
import { AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { AEVATAR_PRESSABLE_CARD_CLASS } from '@/shared/ui/interactionStandards';
import type { InvokeHistoryEntry } from './StudioMemberInvokePanel.currentRun';
import {
  formatHistoryTimestamp,
  helperTextStyle,
  studioInvokeColors,
  trimOptional,
  trimPreview,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokeHistoryPanelProps = {
  readonly entries: readonly InvokeHistoryEntry[];
  readonly selectedHistoryId: string;
  readonly style?: React.CSSProperties;
  readonly getEntryOutputText?: (entryId: string) => string;
  readonly onCopyInput: (entryId: string) => void;
  readonly onCopyOutput: (entryId: string) => void;
  readonly onRetryAsNewRun: (entryId: string) => void;
  readonly onSelectEntry: (entryId: string) => void;
};

function formatRunElapsed(startedAt: number, completedAt: number): string {
  if (!Number.isFinite(startedAt) || !Number.isFinite(completedAt)) {
    return '00:00';
  }

  const elapsedSeconds = Math.max(
    0,
    Math.floor((completedAt - startedAt) / 1000),
  );
  const minutes = Math.floor(elapsedSeconds / 60)
    .toString()
    .padStart(2, '0');
  const seconds = (elapsedSeconds % 60).toString().padStart(2, '0');
  return `${minutes}:${seconds}`;
}

const historyPanelStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 10,
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
};

const historySummaryStyle: React.CSSProperties = {
  alignItems: 'center',
  cursor: 'pointer',
  display: 'flex',
  flex: '0 0 auto',
  gap: 8,
  minHeight: 40,
  minWidth: 0,
  padding: '10px 12px',
};

const historyTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  fontSize: 14,
  fontWeight: 800,
  lineHeight: '20px',
};

const historyBodyStyle: React.CSSProperties = {
  borderTop: `1px solid ${studioInvokeColors.border}`,
  display: 'flex',
  flex: '0 0 auto',
  flexDirection: 'column',
  gap: 8,
  minHeight: 0,
  minWidth: 0,
  overflow: 'visible',
  padding: 10,
};

const historyCardStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  cursor: 'pointer',
  display: 'grid',
  gap: 6,
  minWidth: 0,
  padding: '8px 10px',
  textAlign: 'left',
  width: '100%',
};

const historyCardButtonStyle: React.CSSProperties = {
  background: 'transparent',
  border: 0,
  cursor: 'pointer',
  display: 'grid',
  gap: 6,
  minWidth: 0,
  padding: 0,
  textAlign: 'left',
  width: '100%',
};

const historyCardHeaderStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 8,
  justifyContent: 'space-between',
  minWidth: 0,
};

const historyCardTitleStyle: React.CSSProperties = {
  color: studioInvokeColors.text,
  flex: '1 1 auto',
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
  minWidth: 0,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const historyMetaStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  display: 'flex',
  flexWrap: 'wrap',
  fontSize: 12,
  gap: 6,
  lineHeight: '18px',
  minWidth: 0,
};

const historyActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 6,
  minWidth: 0,
};

const historySelectedNoticeStyle: React.CSSProperties = {
  background: studioInvokeColors.surfaceActive,
  border: `1px solid ${studioInvokeColors.borderStrong}`,
  borderRadius: 8,
  color: studioInvokeColors.textSoft,
  display: 'grid',
  gap: 2,
  minWidth: 0,
  padding: '8px 10px',
};

const StudioMemberInvokeHistoryPanel: React.FC<
  StudioMemberInvokeHistoryPanelProps
> = ({
  entries,
  getEntryOutputText,
  onCopyInput,
  onCopyOutput,
  onRetryAsNewRun,
  onSelectEntry,
  selectedHistoryId,
  style,
}) => (
  <details
    aria-label={t("pages.studio.studiomemberinvokehistorypanel.run.history.3", "Run history")}
    data-testid="studio-invoke-history-panel"
    style={{ ...historyPanelStyle, ...style }}
  >
    <summary style={historySummaryStyle}>
      <span style={historyTitleStyle}>{t("pages.studio.studiomemberinvokehistorypanel.run.history.4", "Run history (")}{entries.length})</span>
      {entries.length === 0 ? (
        <Typography.Text style={helperTextStyle} type="secondary">
          {t("pages.studio.studiomemberinvokehistorypanel.no.runs.yet.3", "No runs yet")}</Typography.Text>
      ) : null}
    </summary>
    <div data-testid="studio-invoke-history-scroll" style={historyBodyStyle}>
      {entries.length === 0 ? (
        <Typography.Text style={helperTextStyle} type="secondary">
          {t("pages.studio.studiomemberinvokehistorypanel.no.runs.yet.4", "No runs yet")}</Typography.Text>
      ) : (
        entries.map((entry) => {
          const isSelected = selectedHistoryId === entry.id;
          const hasInput = Boolean(trimOptional(entry.prompt));
          const hasOutput = Boolean(
            trimOptional(getEntryOutputText?.(entry.id)),
          );
          return (
            <div
              key={entry.id}
              className={AEVATAR_PRESSABLE_CARD_CLASS}
              style={{
                ...historyCardStyle,
                background: isSelected
                  ? studioInvokeColors.surfaceActive
                  : studioInvokeColors.panel,
                borderColor: isSelected
                  ? studioInvokeColors.activeBorder
                  : studioInvokeColors.border,
              }}
            >
              <button
                aria-label={`View historical run ${entry.prompt || entry.summary}`}
                style={historyCardButtonStyle}
                type="button"
                onClick={() => onSelectEntry(entry.id)}
              >
                <div style={historyCardHeaderStyle}>
                  <span style={historyCardTitleStyle}>
                    {trimPreview(entry.prompt || entry.summary, 72) || t("pages.studio.studiomemberinvokehistorypanel.run.3", "Run")}
                  </span>
                  <AevatarStatusTag
                    domain="run"
                    label={
                      entry.status === 'success'
                        ? 'Succeeded'
                        : entry.status === 'running'
                          ? 'Running'
                          : entry.status === 'cancelled'
                            ? 'Cancelled'
                            : 'Failed'
                    }
                    status={entry.status}
                  />
                </div>
                <div style={historyMetaStyle}>
                  <span>{formatHistoryTimestamp(entry.createdAt)}</span>
                  <span>·</span>
                  <span>
                    {formatRunElapsed(entry.startedAt, entry.completedAt)}
                  </span>
                  <span>·</span>
                  <span>{entry.eventCount} {t("pages.studio.studiomemberinvokehistorypanel.events.2", "events")}</span>
                  <span>·</span>
                  <span>{entry.endpointLabel || 'chat'}</span>
                </div>
              </button>
              {isSelected ? (
                <>
                  <div
                    data-testid="studio-invoke-history-readonly-guidance"
                    style={historySelectedNoticeStyle}
                  >
                    <Typography.Text style={helperTextStyle}>
                      {t("pages.studio.studiomemberinvokehistorypanel.historical.run.is.read.only", "Historical run is read-only. Retry as new run restores the prompt without changing this record.")}</Typography.Text>
                  </div>
                  <div style={historyActionsStyle}>
                    <Button
                      disabled={!hasInput}
                      icon={<CopyOutlined />}
                      size="small"
                      onClick={(event) => {
                        event.stopPropagation();
                        onCopyInput(entry.id);
                      }}
                    >
                      {t("pages.studio.studiomemberinvokehistorypanel.copy.input.2", "Copy input")}</Button>
                    <Button
                      disabled={!hasOutput}
                      icon={<CopyOutlined />}
                      size="small"
                      onClick={(event) => {
                        event.stopPropagation();
                        onCopyOutput(entry.id);
                      }}
                    >
                      {t("pages.studio.studiomemberinvokehistorypanel.copy.output.2", "Copy output")}</Button>
                    <Button
                      icon={<ReloadOutlined />}
                      size="small"
                      onClick={(event) => {
                        event.stopPropagation();
                        onRetryAsNewRun(entry.id);
                      }}
                    >
                      {t("pages.studio.studiomemberinvokehistorypanel.retry.as.new.run.2", "Retry as new run")}</Button>
                  </div>
                </>
              ) : null}
            </div>
          );
        })
      )}
    </div>
  </details>
);

export default StudioMemberInvokeHistoryPanel;
