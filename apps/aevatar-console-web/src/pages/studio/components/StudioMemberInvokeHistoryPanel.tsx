import { Typography } from 'antd';
import React from 'react';
import { AevatarPanel, AevatarStatusTag } from '@/shared/ui/aevatarPageShells';
import { AEVATAR_PRESSABLE_CARD_CLASS } from '@/shared/ui/interactionStandards';
import type { InvokeHistoryEntry } from './StudioMemberInvokePanel.currentRun';
import {
  contractValueStyle,
  formatHistoryTimestamp,
  helperTextStyle,
  studioInvokeColors,
  trimOptional,
  trimPreview,
  truncateMiddle,
} from './studioInvokeUi';

type StudioMemberInvokeHistoryPanelProps = {
  readonly expandedHistoryId: string;
  readonly entries: readonly InvokeHistoryEntry[];
  readonly onSelectEntry: (entryId: string) => void;
};

function formatDuration(startedAt: number, completedAt: number): string {
  if (!Number.isFinite(startedAt) || !Number.isFinite(completedAt)) {
    return '未知';
  }

  const durationMs = Math.max(0, completedAt - startedAt);
  if (durationMs < 1000) {
    return `${durationMs} ms`;
  }

  return `${(durationMs / 1000).toFixed(durationMs >= 10_000 ? 0 : 1)} s`;
}

const runsListStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  minWidth: 0,
};

const historyCardStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 12,
  cursor: 'pointer',
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
  textAlign: 'left',
  width: '100%',
};

const historyMetaStyle: React.CSSProperties = {
  color: studioInvokeColors.meta,
  display: 'flex',
  flexWrap: 'wrap',
  fontSize: 12,
  gap: 8,
  minWidth: 0,
};

const inlineDetailStyle: React.CSSProperties = {
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 12,
  display: 'grid',
  gap: 10,
  minWidth: 0,
  padding: '12px 14px',
};

const detailRowStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const CompactCopyableValue: React.FC<{
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
      {truncateMiddle(normalized)}
    </Typography.Text>
  );
};

const StudioMemberInvokeHistoryPanel: React.FC<
  StudioMemberInvokeHistoryPanelProps
> = ({ entries, expandedHistoryId, onSelectEntry }) => {
  if (entries.length === 0) {
    return null;
  }

  return (
    <AevatarPanel
      layoutMode="document"
      padding={14}
      title={`Runs（${entries.length}）`}
      titleHelp="这里只保留历史运行列表和技术详情，不再重复展示结果内容。"
    >
      <div data-testid="studio-invoke-history-scroll" style={runsListStyle}>
        {entries.map((entry) => {
          const isExpanded = expandedHistoryId === entry.id;
          return (
            <div key={entry.id} style={runsListStyle}>
              <button
                aria-expanded={isExpanded}
                aria-pressed={isExpanded}
                className={AEVATAR_PRESSABLE_CARD_CLASS}
                style={{
                  ...historyCardStyle,
                  background: isExpanded
                    ? studioInvokeColors.surfaceActive
                    : studioInvokeColors.panel,
                  borderColor: isExpanded
                    ? studioInvokeColors.activeBorder
                    : studioInvokeColors.border,
                }}
                type="button"
                onClick={() => onSelectEntry(entry.id)}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    gap: 8,
                    justifyContent: 'space-between',
                    minWidth: 0,
                  }}
                >
                  <Typography.Text
                    strong
                    style={{ ...contractValueStyle, flex: 1 }}
                  >
                    {trimPreview(entry.prompt || entry.summary, 72)}
                  </Typography.Text>
                  <AevatarStatusTag
                    domain="run"
                    label={entry.status === 'success' ? '成功' : '失败'}
                    status={entry.status}
                  />
                </div>
                <div style={historyMetaStyle}>
                  <span>{formatHistoryTimestamp(entry.createdAt)}</span>
                  <span>{entry.eventCount} 个事件</span>
                  <span>{entry.endpointLabel}</span>
                </div>
              </button>

              {isExpanded ? (
                <div data-testid="studio-invoke-inline-detail" style={inlineDetailStyle}>
                  {entry.snapshot.result.commandId ? (
                    <div style={detailRowStyle}>
                      <Typography.Text type="secondary">Command ID</Typography.Text>
                      <CompactCopyableValue value={entry.snapshot.result.commandId} />
                    </div>
                  ) : null}
                  {entry.snapshot.result.actorId ? (
                    <div style={detailRowStyle}>
                      <Typography.Text type="secondary">Actor ID</Typography.Text>
                      <CompactCopyableValue value={entry.snapshot.result.actorId} />
                    </div>
                  ) : null}
                  {entry.runId || entry.snapshot.result.runId ? (
                    <div style={detailRowStyle}>
                      <Typography.Text type="secondary">Run ID</Typography.Text>
                      <CompactCopyableValue
                        value={entry.runId || entry.snapshot.result.runId}
                      />
                    </div>
                  ) : null}
                  <div style={detailRowStyle}>
                    <Typography.Text type="secondary">Duration</Typography.Text>
                    <div style={contractValueStyle}>
                      {formatDuration(entry.startedAt, entry.completedAt)}
                    </div>
                  </div>
                  <div style={detailRowStyle}>
                    <Typography.Text type="secondary">Timestamps</Typography.Text>
                    <div style={helperTextStyle}>
                      开始：{formatHistoryTimestamp(entry.startedAt)}
                    </div>
                    <div style={helperTextStyle}>
                      完成：{formatHistoryTimestamp(entry.completedAt)}
                    </div>
                  </div>
                  {entry.errorDetail ? (
                    <div style={detailRowStyle}>
                      <Typography.Text type="secondary">Error Detail</Typography.Text>
                      <div style={contractValueStyle}>{entry.errorDetail}</div>
                    </div>
                  ) : null}
                </div>
              ) : null}
            </div>
          );
        })}
      </div>
    </AevatarPanel>
  );
};

export default StudioMemberInvokeHistoryPanel;
