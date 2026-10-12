import { useIntl } from '@umijs/max';
import { Alert, Tag, Typography } from 'antd';
import React from 'react';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import type { WorkflowActivityRunSummary } from '@/shared/models/workflowActivity';
import { getRunStatusPresentation } from './runPresentation';

export type RunHistoryEntry = WorkflowActivityRunSummary & {
  readonly context?: string;
  readonly inputSummary?: string;
  readonly workflowId?: string;
};

type RunHistoryRailProps = {
  readonly entries: readonly RunHistoryEntry[];
  readonly selectedRunId: string;
  readonly workflowName: string;
  readonly isPending: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly onOpenRun: (runId: string) => void;
};

const RunHistoryRail = React.memo(function RunHistoryRail({
  entries,
  selectedRunId,
  workflowName,
  isPending,
  isError,
  error,
  onOpenRun,
}: RunHistoryRailProps) {
  // Locale updates must reach the memoized rail even when its data is unchanged.
  useIntl();
  const historyDescription =
    workflowName || t('workflowActivityVNext.common.unknown', 'Unknown');

  return (
    <aside className="wa-vnext-run-detail__rail">
      <div className="wa-vnext-run-detail__rail-header">
        <div className="wa-vnext-run-detail__rail-title">
          <Typography.Title level={5} style={{ margin: 0 }}>
            {t(
              'pages.runs.memberPublishedRuns.publishedRuns',
              'Published runs',
            )}
          </Typography.Title>
          <Typography.Text
            className="wa-vnext-run-detail__ellipsis"
            title={historyDescription}
            type="secondary"
          >
            {historyDescription}
          </Typography.Text>
        </div>
      </div>
      <div className="wa-vnext-run-detail__rail-list">
        {isPending ? (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <p>
              {t(
                'workflowActivityVNext.run.historyLoading',
                'Loading run history…',
              )}
            </p>
          </div>
        ) : isError ? (
          <Alert
            showIcon
            type="error"
            message={t(
              'workflowActivityVNext.run.historyUnavailable',
              'Run history is unavailable.',
            )}
            description={error instanceof Error ? error.message : String(error)}
          />
        ) : entries.length ? (
          entries.map((entry) => {
            const selected = entry.runId === selectedRunId;
            const status = getRunStatusPresentation(entry.status);
            const runTime = formatDateTime(entry.updatedAtUtc);
            const runOrigin =
              entry.runOrigin ||
              t('workflowActivityVNext.common.unknown', 'Unknown');

            return (
              <button
                aria-current={selected ? 'true' : undefined}
                aria-label={t(
                  'workflowActivityVNext.run.openRunAria',
                  'Open {runId}',
                  { runId: entry.runId },
                )}
                className={`wa-vnext-run-detail__run${selected ? ' wa-vnext-run-detail__run--selected' : ''}`}
                key={entry.runId}
                onClick={() => onOpenRun(entry.runId)}
                type="button"
              >
                <div className="wa-vnext-run-detail__run-title">
                  <Tag
                    className={`wa-vnext__status wa-vnext__status--${status.className}`}
                    style={{ marginInlineEnd: 0 }}
                  >
                    {status.label}
                  </Tag>
                  <Typography.Text
                    className="wa-vnext-run-detail__ellipsis"
                    title={runTime}
                    style={{ minWidth: 0 }}
                  >
                    {runTime}
                  </Typography.Text>
                </div>
                <Typography.Text
                  className="wa-vnext-run-detail__ellipsis"
                  title={runOrigin}
                  type="secondary"
                >
                  {runOrigin}
                </Typography.Text>
              </button>
            );
          })
        ) : (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <h3>
              {t(
                'workflowActivityVNext.run.noHistory',
                'No published runs yet.',
              )}
            </h3>
            <p>
              {t(
                'workflowActivityVNext.run.noHistoryDescription',
                'This workflow has no other runs in the current history context.',
              )}
            </p>
          </div>
        )}
      </div>
    </aside>
  );
});

export default RunHistoryRail;
