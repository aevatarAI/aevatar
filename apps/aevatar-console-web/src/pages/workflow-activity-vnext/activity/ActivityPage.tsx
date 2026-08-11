import {
  ArrowRightOutlined,
  CloseOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useInfiniteQuery } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import { Alert, Button, DatePicker, Input, Select, Space } from 'antd';
import dayjs from 'dayjs';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import type { WorkflowActivityRunFeedRow } from '@/shared/models/workflowActivity';
import { history } from '@/shared/navigation/history';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { buildWorkflowActivityRunHref } from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import { getRunStatusPresentation } from './runPresentation';

const supportedRunStatuses = new Set(['running', 'completed', 'failed']);

function normalizeRunStatusFilter(value: string | null): string {
  const normalized = value?.trim().toLowerCase() ?? '';
  return supportedRunStatuses.has(normalized) ? normalized : '';
}

function formatDate(value: string | null): string {
  if (!value)
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(getLocale(), {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(date);
}

function formatDuration(milliseconds: number | null): string {
  if (milliseconds === null || !Number.isFinite(milliseconds)) return '-';
  const seconds = Math.max(0, Math.round(milliseconds / 1000));
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return remainingMinutes ? `${hours}h ${remainingMinutes}m` : `${hours}h`;
}

function runDuration(run: WorkflowActivityRunFeedRow, now: number): string {
  if (run.durationMs !== null) return formatDuration(run.durationMs);
  if (!run.startedAtUtc || run.completedAtUtc) return '-';
  const startedAt = new Date(run.startedAtUtc).getTime();
  return Number.isNaN(startedAt) ? '-' : formatDuration(now - startedAt);
}

function shortRunReference(runId: string): string {
  const segments = runId.split(':').filter(Boolean);
  const lastSegment = segments.at(-1) ?? runId;
  return lastSegment.length > 18
    ? `${lastSegment.slice(0, 8)}…${lastSegment.slice(-6)}`
    : lastSegment;
}

function isAvailable(value: string): boolean {
  return value.trim().toLowerCase() === 'available';
}

function runContext(run: WorkflowActivityRunFeedRow): string | null {
  if (isAvailable(run.firstFailure.availability)) {
    return run.firstFailure.message || run.firstFailure.stepId || null;
  }
  if (isAvailable(run.waiting.availability)) {
    return (
      run.waiting.prompt ||
      run.waiting.waitingKind ||
      run.waiting.stepId ||
      null
    );
  }
  if (isAvailable(run.currentStep.availability)) {
    return run.currentStep.stepId || run.currentStep.inputSummary || null;
  }
  return null;
}

function failureTitle(error: unknown): string {
  if (error instanceof WorkflowActivityApiError) {
    if (error.status === 401)
      return t(
        'workflowActivityVNext.state.unauthorized',
        'Sign in to continue',
      );
    if (error.status === 403)
      return t(
        'workflowActivityVNext.state.forbidden',
        "You don't have access to this workspace",
      );
  }
  return t(
    'workflowActivityVNext.activity.unavailable',
    'Activity unavailable',
  );
}

const ActivityPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const params = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const rawStatus = params.get('status');
  const status = normalizeRunStatusFilter(rawStatus);
  const origin = params.get('origin') ?? '';
  const definition = params.get('definition')?.trim() ?? '';
  const workflowFilterPresent = params.has('workflowId');
  const workflowId = params.get('workflowId')?.trim() ?? '';
  const fromUtc = params.get('from')?.trim() ?? '';
  const toUtc = params.get('to')?.trim() ?? '';
  const search = params.get('q') ?? '';
  const [now, setNow] = React.useState(() => Date.now());

  const replaceParams = React.useCallback(
    (update: (next: URLSearchParams) => void) => {
      const next = new URLSearchParams(location.search);
      update(next);
      const suffix = next.toString();
      history.replace(`${location.pathname}${suffix ? `?${suffix}` : ''}`);
    },
    [location.pathname, location.search],
  );
  const replaceParam = React.useCallback(
    (name: string, value: string) =>
      replaceParams((next) => {
        if (value) next.set(name, value);
        else next.delete(name);
      }),
    [replaceParams],
  );
  const clearWorkflowFilter = React.useCallback(
    () =>
      replaceParams((next) => {
        next.delete('workflowId');
        next.delete('definition');
      }),
    [replaceParams],
  );

  React.useEffect(() => {
    if (!rawStatus || status) return;
    replaceParam('status', '');
  }, [rawStatus, replaceParam, status]);

  const runs = useInfiniteQuery({
    queryKey: [
      'workflow-activity-vnext',
      'activity-runs',
      scopeId,
      status,
      origin,
      definition,
      workflowId,
      fromUtc,
      toUtc,
    ],
    queryFn: ({ pageParam }) =>
      workflowActivityApi.listActivityRuns(scopeId, {
        status: status || undefined,
        origins: origin ? [origin] : undefined,
        definitionActorIds: definition ? [definition] : undefined,
        workflowId: workflowId || undefined,
        fromUtc: fromUtc || undefined,
        toUtc: toUtc || undefined,
        take: 50,
        cursor: pageParam,
        includeTotalCount: pageParam === undefined,
      }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (lastPage) =>
      lastPage.hasMore ? (lastPage.nextCursor ?? undefined) : undefined,
    enabled: !workflowFilterPresent || Boolean(workflowId),
    refetchOnMount: 'always',
    retry: false,
  });

  const loadedRuns = React.useMemo(
    () => runs.data?.pages.flatMap((page) => page.items) ?? [],
    [runs.data],
  );
  const hasRunningRun = loadedRuns.some(
    (run) => run.status.trim().toLowerCase() === 'running',
  );
  React.useEffect(() => {
    if (!hasRunningRun) return;
    const interval = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(interval);
  }, [hasRunningRun]);

  const filtered = loadedRuns.filter((run) => {
    const normalized = search.trim().toLowerCase();
    return (
      !normalized ||
      [
        run.workflowName,
        run.runId,
        run.status,
        run.inputSummary,
        run.initiator.displayValue,
      ].some((value) => value.toLowerCase().includes(normalized))
    );
  });
  const totalCount = runs.data?.pages[0]?.totalCount ?? null;
  const cursorMalformed =
    runs.error instanceof WorkflowActivityApiError &&
    runs.error.code === 'malformed_cursor';

  return (
    <WorkflowActivityVNextShell
      activeSection="activity"
      description={t(
        'workflowActivityVNext.activity.description',
        'Review recent workflow runs and open one for details.',
      )}
      headerActions={
        <Button icon={<ReloadOutlined />} onClick={() => void runs.refetch()}>
          {t('workflowActivityVNext.common.refresh', 'Refresh')}
        </Button>
      }
      scopeId={scopeId}
      title={t('workflowActivityVNext.activity.title', 'Activity')}
    >
      {workflowFilterPresent ? (
        <Space wrap>
          <Button
            aria-label={t(
              'workflowActivityVNext.activity.removeWorkflowFilterAria',
              'Remove workflow filter {workflowId}',
              { workflowId: workflowId || 'invalid' },
            )}
            icon={<CloseOutlined />}
            onClick={clearWorkflowFilter}
          >
            {t(
              'workflowActivityVNext.activity.workflowFilterLabel',
              'Workflow: {workflowId}',
              { workflowId: workflowId || 'Invalid' },
            )}
          </Button>
        </Space>
      ) : null}
      <div className="wa-vnext__toolbar">
        <Input
          allowClear
          aria-label={t(
            'workflowActivityVNext.activity.filterLoadedAria',
            'Filter loaded runs',
          )}
          className="wa-vnext__toolbar-search"
          onChange={(event) => replaceParam('q', event.target.value)}
          placeholder={t(
            'workflowActivityVNext.activity.filterLoaded',
            'Filter loaded runs',
          )}
          prefix={<SearchOutlined />}
          role="searchbox"
          value={search}
        />
        <Space className="wa-vnext__toolbar-filters" wrap>
          <Select
            aria-label={t(
              'workflowActivityVNext.activity.statusFilter',
              'Run status',
            )}
            onChange={(value) => replaceParam('status', value)}
            options={[
              {
                label: t(
                  'workflowActivityVNext.activity.allStatuses',
                  'All statuses',
                ),
                value: '',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.statusRunning',
                  'Running',
                ),
                value: 'running',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.statusCompleted',
                  'Completed',
                ),
                value: 'completed',
              },
              {
                label: t('workflowActivityVNext.common.failed', 'Failed'),
                value: 'failed',
              },
            ]}
            value={status}
          />
          <Select
            aria-label={t(
              'workflowActivityVNext.activity.originFilter',
              'Run source',
            )}
            onChange={(value) => replaceParam('origin', value)}
            options={[
              {
                label: t(
                  'workflowActivityVNext.activity.allOrigins',
                  'All sources',
                ),
                value: '',
              },
              {
                label: t('workflowActivityVNext.activity.originChat', 'Chat'),
                value: 'ad-hoc-chat',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.originEditor',
                  'Editor',
                ),
                value: 'draft',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.originMember',
                  'Team member',
                ),
                value: 'member-invoke',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.originService',
                  'Service',
                ),
                value: 'service-invoke',
              },
              {
                label: t(
                  'workflowActivityVNext.activity.originSchedule',
                  'Schedule',
                ),
                value: 'schedule',
              },
            ]}
            value={origin}
          />
          <DatePicker.RangePicker
            allowEmpty={[true, true]}
            onChange={(range) =>
              replaceParams((next) => {
                const [after, before] = range ?? [];
                if (after) next.set('from', after.toISOString());
                else next.delete('from');
                if (before) next.set('to', before.toISOString());
                else next.delete('to');
              })
            }
            placeholder={[
              t(
                'workflowActivityVNext.activity.afterPlaceholder',
                'Activity after',
              ),
              t(
                'workflowActivityVNext.activity.beforePlaceholder',
                'Activity before',
              ),
            ]}
            showTime
            value={[
              fromUtc && dayjs(fromUtc).isValid() ? dayjs(fromUtc) : null,
              toUtc && dayjs(toUtc).isValid() ? dayjs(toUtc) : null,
            ]}
          />
          {definition && !workflowFilterPresent ? (
            <Button onClick={() => replaceParam('definition', '')}>
              {t(
                'workflowActivityVNext.activity.clearWorkflowFilter',
                'Show all workflows',
              )}
            </Button>
          ) : null}
        </Space>
      </div>
      {workflowFilterPresent && !workflowId ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>
              {t(
                'workflowActivityVNext.activity.workflowFilterInvalidTitle',
                'Choose a workflow to filter Activity',
              )}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.activity.workflowFilterInvalidDescription',
                'This Activity link does not contain a workflow identity.',
              )}
            </p>
          </div>
        </div>
      ) : runs.isPending ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'workflowActivityVNext.activity.loading',
            'Loading activity…',
          )}
          columnWidths={['minmax(220px, 1fr)', 180, 190, 180, 220, 56]}
          rows={4}
          tableMinWidth={1080}
          variant="table"
        />
      ) : runs.isError && !runs.data ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>{failureTitle(runs.error)}</h2>
            <p>
              {t(
                'workflowActivityVNext.activity.unavailableDescription',
                'Try again to load recent workflow runs.',
              )}
            </p>
            <Button onClick={() => void runs.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
            <TechnicalDetails>
              {runs.error instanceof Error
                ? runs.error.message
                : String(runs.error)}
            </TechnicalDetails>
          </div>
        </div>
      ) : filtered.length === 0 ? (
        <div className="wa-vnext__state">
          <div>
            <h2>
              {loadedRuns.length
                ? t(
                    'workflowActivityVNext.activity.noMatch',
                    'No matching runs',
                  )
                : t('workflowActivityVNext.activity.empty', 'No runs yet')}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.activity.emptyDescription',
                'Runs will appear here after a workflow starts.',
              )}
            </p>
          </div>
        </div>
      ) : (
        <>
          <TableScrollRegion
            ariaLabel={t('workflowActivityVNext.activity.title', 'Activity')}
            minWidth={1080}
          >
            <table className="wa-vnext__table">
              <thead>
                <tr>
                  <th>
                    {t('workflowActivityVNext.activity.columnRun', 'Workflow')}
                  </th>
                  <th>
                    {t('workflowActivityVNext.activity.columnStatus', 'Status')}
                  </th>
                  <th>
                    {t(
                      'workflowActivityVNext.activity.columnStarted',
                      'Started',
                    )}
                  </th>
                  <th>
                    {t(
                      'workflowActivityVNext.activity.columnInitiator',
                      'Initiator',
                    )}
                  </th>
                  <th>
                    {t('workflowActivityVNext.activity.columnInput', 'Input')}
                  </th>
                  <th>
                    <span className="aevatar-loading-visually-hidden">
                      {t(
                        'workflowActivityVNext.activity.columnAction',
                        'Open run',
                      )}
                    </span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((run) => {
                  const statusPresentation = getRunStatusPresentation(
                    run.status,
                  );
                  const workflowName =
                    run.workflowName ||
                    t(
                      'workflowActivityVNext.activity.unnamed',
                      'Unnamed workflow',
                    );
                  const context = runContext(run);
                  return (
                    <tr key={run.runId}>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnRun',
                          'Workflow',
                        )}
                      >
                        <span className="wa-vnext__title">{workflowName}</span>
                        <span className="wa-vnext__sub wa-vnext__mono">
                          {shortRunReference(run.runId)}
                        </span>
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnStatus',
                          'Status',
                        )}
                      >
                        <span
                          className={`wa-vnext__status wa-vnext__status--${statusPresentation.className}`}
                        >
                          {statusPresentation.label}
                        </span>
                        {context ? (
                          <span className="wa-vnext__sub">{context}</span>
                        ) : null}
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnStarted',
                          'Started',
                        )}
                      >
                        {formatDate(run.startedAtUtc)}
                        <span className="wa-vnext__sub">
                          {runDuration(run, now)}
                        </span>
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnInitiator',
                          'Initiator',
                        )}
                      >
                        {isAvailable(run.initiator.availability)
                          ? run.initiator.displayValue
                          : t(
                              'workflowActivityVNext.common.unavailable',
                              'Unavailable',
                            )}
                        <span className="wa-vnext__sub">
                          {run.runOrigin || '-'}
                        </span>
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnInput',
                          'Input',
                        )}
                      >
                        {run.inputSummary || '-'}
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnAction',
                          'Open run',
                        )}
                      >
                        <Button
                          aria-label={t(
                            'workflowActivityVNext.activity.openRunAria',
                            'Open {name}',
                            { name: workflowName },
                          )}
                          icon={<ArrowRightOutlined />}
                          onClick={() =>
                            history.push(
                              buildWorkflowActivityRunHref(scopeId, run.runId),
                            )
                          }
                          type="text"
                        />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </TableScrollRegion>
          <div className="wa-vnext__activity-footer">
            <span aria-live="polite">
              {totalCount === null
                ? t(
                    'workflowActivityVNext.activity.loadedCount',
                    '{loaded} runs loaded',
                    { loaded: loadedRuns.length },
                  )
                : t(
                    'workflowActivityVNext.activity.loadedTotalCount',
                    '{loaded} of {total} runs loaded',
                    { loaded: loadedRuns.length, total: totalCount },
                  )}
            </span>
            {runs.isFetchNextPageError ? (
              <Alert
                action={
                  <Button
                    onClick={() =>
                      cursorMalformed
                        ? void runs.refetch()
                        : void runs.fetchNextPage()
                    }
                  >
                    {cursorMalformed
                      ? t(
                          'workflowActivityVNext.activity.refreshFromStart',
                          'Refresh from start',
                        )
                      : t(
                          'workflowActivityVNext.activity.retryLoadMore',
                          'Retry loading more',
                        )}
                  </Button>
                }
                message={t(
                  'workflowActivityVNext.activity.loadMoreFailed',
                  "Couldn't load more runs",
                )}
                showIcon
                type="warning"
              />
            ) : runs.hasNextPage ? (
              <Button
                loading={runs.isFetchingNextPage}
                onClick={() => void runs.fetchNextPage()}
              >
                {t('workflowActivityVNext.activity.loadMore', 'Load more')}
              </Button>
            ) : null}
          </div>
        </>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
