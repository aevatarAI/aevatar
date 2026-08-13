import {
  CloseOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import { Button, DatePicker, Input, Pagination, Select, Space } from 'antd';
import dayjs from 'dayjs';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowActivityRunFeedFilter,
  WorkflowActivityRunFeedPage,
  WorkflowActivityRunFeedRow,
} from '@/shared/models/workflowActivity';
import { history } from '@/shared/navigation/history';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { buildWorkflowActivityRunHref } from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import { getRunStatusPresentation } from './runPresentation';

const supportedRunStatuses = new Set(['running', 'completed', 'failed']);
const activityPageSize = 25;
const activitySearchDebounceMs = 300;
const activityRunsQueryPrefix = ['workflow-activity-vnext', 'activity-runs'];

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

function isAvailable(value: string): boolean {
  return value.trim().toLowerCase() === 'available';
}

function runContext(run: WorkflowActivityRunFeedRow): string | null {
  if (isAvailable(run.firstFailure.availability)) {
    return run.firstFailure.message || null;
  }
  if (isAvailable(run.waiting.availability)) {
    return run.waiting.prompt || run.waiting.waitingKind || null;
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

interface ActivityPaginationState {
  readonly filterKey: string;
  readonly page: number;
  readonly cursors: readonly (string | undefined)[];
}

function createActivityPaginationState(
  filterKey: string,
): ActivityPaginationState {
  return { filterKey, page: 1, cursors: [undefined] };
}

const ActivityPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const queryClient = useQueryClient();
  const params = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const rawStatus = params.get('status');
  const status = normalizeRunStatusFilter(rawStatus);
  const origin = params.get('origin') ?? '';
  const definition = params.get('definition')?.trim() ?? '';
  const schedule = params.get('schedule')?.trim() ?? '';
  const workflowFilterPresent = params.has('workflowId');
  const workflowId = params.get('workflowId')?.trim() ?? '';
  const fromUtc = params.get('from')?.trim() ?? '';
  const toUtc = params.get('to')?.trim() ?? '';
  const search = params.get('q') ?? '';
  const [searchInput, setSearchInput] = React.useState(search);
  const [now, setNow] = React.useState(() => Date.now());
  const filterKey = React.useMemo(
    () =>
      JSON.stringify([
        scopeId,
        status,
        origin,
        definition,
        schedule,
        workflowId,
        search,
        fromUtc,
        toUtc,
      ]),
    [
      definition,
      fromUtc,
      origin,
      schedule,
      scopeId,
      search,
      status,
      toUtc,
      workflowId,
    ],
  );
  const [pagination, setPagination] = React.useState<ActivityPaginationState>(
    () => createActivityPaginationState(filterKey),
  );
  const [isResolvingPage, setIsResolvingPage] = React.useState(false);
  const [pageNavigationError, setPageNavigationError] = React.useState<
    unknown | null
  >(null);
  const [pendingPage, setPendingPage] = React.useState<number | null>(null);
  const navigationRequestId = React.useRef(0);
  const currentFilterKey = React.useRef(filterKey);
  currentFilterKey.current = filterKey;
  const activePagination =
    pagination.filterKey === filterKey
      ? pagination
      : createActivityPaginationState(filterKey);
  const currentCursor = activePagination.cursors[activePagination.page - 1];

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

  React.useEffect(() => {
    setSearchInput(search);
  }, [search]);

  React.useEffect(() => {
    if (searchInput === search) return;
    const timer = window.setTimeout(
      () => replaceParam('q', searchInput),
      activitySearchDebounceMs,
    );
    return () => window.clearTimeout(timer);
  }, [replaceParam, search, searchInput]);

  React.useEffect(() => {
    if (pagination.filterKey === filterKey) return;
    navigationRequestId.current += 1;
    setIsResolvingPage(false);
    setPageNavigationError(null);
    setPendingPage(null);
    setPagination(createActivityPaginationState(filterKey));
  }, [filterKey, pagination.filterKey]);

  const buildActivityRunFilter = React.useCallback(
    (cursor: string | undefined): WorkflowActivityRunFeedFilter => ({
      status: status || undefined,
      origins: origin ? [origin] : undefined,
      definitionActorIds: definition ? [definition] : undefined,
      scheduleIds: schedule ? [schedule] : undefined,
      workflowId: workflowId || undefined,
      searchText: search.trim() || undefined,
      fromUtc: fromUtc || undefined,
      toUtc: toUtc || undefined,
      take: activityPageSize,
      cursor,
      includeTotalCount: true,
    }),
    [definition, fromUtc, origin, schedule, search, status, toUtc, workflowId],
  );
  const activityRunsQueryKey = React.useCallback(
    (cursor: string | undefined) =>
      [...activityRunsQueryPrefix, filterKey, cursor] as const,
    [filterKey],
  );
  const fetchActivityPage = React.useCallback(
    (cursor: string | undefined) =>
      workflowActivityApi.listActivityRuns(
        scopeId,
        buildActivityRunFilter(cursor),
      ),
    [buildActivityRunFilter, scopeId],
  );
  const canQueryActivityRuns = !workflowFilterPresent || Boolean(workflowId);

  const runs = useQuery({
    queryKey: activityRunsQueryKey(currentCursor),
    queryFn: () => fetchActivityPage(currentCursor),
    enabled: canQueryActivityRuns,
    refetchOnMount: 'always',
    retry: false,
  });

  const currentRuns = runs.data?.items ?? [];
  const hasRunningRun = currentRuns.some(
    (run) => run.status.trim().toLowerCase() === 'running',
  );
  React.useEffect(() => {
    if (!hasRunningRun) return;
    const interval = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(interval);
  }, [hasRunningRun]);

  const totalCount = runs.data?.totalCount ?? null;
  const totalPages =
    totalCount === null
      ? null
      : Math.max(1, Math.ceil(totalCount / activityPageSize));
  const paginationTotal =
    totalCount ??
    activePagination.page * activityPageSize + (runs.data?.hasMore ? 1 : 0);
  const hasFilters = Boolean(
    status ||
      origin ||
      definition ||
      schedule ||
      workflowId ||
      search ||
      fromUtc ||
      toUtc,
  );
  const goToPage = React.useCallback(
    async (requestedPage: number) => {
      if (
        !canQueryActivityRuns ||
        requestedPage < 1 ||
        requestedPage === activePagination.page ||
        (totalPages !== null && requestedPage > totalPages)
      ) {
        return;
      }

      const requestId = navigationRequestId.current + 1;
      navigationRequestId.current = requestId;
      setIsResolvingPage(true);
      setPageNavigationError(null);
      setPendingPage(requestedPage);

      try {
        const cursors = [...activePagination.cursors];
        for (let page = 1; page < requestedPage; page += 1) {
          if (cursors[page] !== undefined) continue;

          const cursor = cursors[page - 1];
          const queryKey = activityRunsQueryKey(cursor);
          const pageData =
            queryClient.getQueryData<WorkflowActivityRunFeedPage>(queryKey) ??
            (await queryClient.fetchQuery({
              queryKey,
              queryFn: () => fetchActivityPage(cursor),
            }));

          if (
            navigationRequestId.current !== requestId ||
            currentFilterKey.current !== filterKey
          ) {
            return;
          }
          if (!pageData || !pageData.hasMore || !pageData.nextCursor) {
            throw new Error('The requested activity page is unavailable.');
          }
          cursors[page] = pageData.nextCursor;
        }

        if (
          navigationRequestId.current !== requestId ||
          currentFilterKey.current !== filterKey
        ) {
          return;
        }
        setPagination({ filterKey, page: requestedPage, cursors });
        setPendingPage(null);
      } catch (error) {
        if (
          navigationRequestId.current === requestId &&
          currentFilterKey.current === filterKey
        ) {
          setPageNavigationError(error);
        }
      } finally {
        if (
          navigationRequestId.current === requestId &&
          currentFilterKey.current === filterKey
        ) {
          setIsResolvingPage(false);
        }
      }
    },
    [
      activePagination,
      activityRunsQueryKey,
      canQueryActivityRuns,
      fetchActivityPage,
      filterKey,
      queryClient,
      totalPages,
    ],
  );
  const retryPageNavigation = React.useCallback(() => {
    if (pendingPage === null) return;
    void goToPage(pendingPage);
  }, [goToPage, pendingPage]);

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
            'workflowActivityVNext.activity.searchAria',
            'Search runs',
          )}
          className="wa-vnext__toolbar-search"
          onChange={(event) => setSearchInput(event.target.value)}
          placeholder={t(
            'workflowActivityVNext.activity.search',
            'Search runs',
          )}
          prefix={<SearchOutlined />}
          role="searchbox"
          value={searchInput}
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
          columnWidths={['minmax(220px, 1fr)', 180, 190, 180, 220]}
          rows={4}
          tableMinWidth={1080}
          variant="table"
        />
      ) : runs.isError && !runs.data ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>
              {activePagination.page === 1
                ? failureTitle(runs.error)
                : t(
                    'workflowActivityVNext.activity.pageUnavailable',
                    "Couldn't load this page",
                  )}
            </h2>
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
      ) : currentRuns.length === 0 ? (
        <div className="wa-vnext__state">
          <div>
            <h2>
              {hasFilters
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
          >
            <table className="wa-vnext__table">
              <colgroup>
                <col className="wa-vnext__activity-column--workflow" />
                <col className="wa-vnext__activity-column--status" />
                <col className="wa-vnext__activity-column--started" />
                <col className="wa-vnext__activity-column--duration" />
                <col className="wa-vnext__activity-column--input" />
              </colgroup>
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
                      'workflowActivityVNext.activity.columnDuration',
                      'Duration',
                    )}
                  </th>
                  <th>
                    {t(
                      'workflowActivityVNext.activity.columnInputPreview',
                      'Input preview',
                    )}
                  </th>
                </tr>
              </thead>
              <tbody>
                {currentRuns.map((run) => {
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
                    <tr
                      className="wa-vnext__activity-row"
                      key={run.runId}
                      onClick={() =>
                        history.push(
                          buildWorkflowActivityRunHref(scopeId, run.runId),
                        )
                      }
                      onKeyDown={(event) => {
                        if (event.key !== 'Enter' && event.key !== ' ') return;
                        event.preventDefault();
                        history.push(
                          buildWorkflowActivityRunHref(scopeId, run.runId),
                        );
                      }}
                      tabIndex={0}
                    >
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnRun',
                          'Workflow',
                        )}
                      >
                        <span className="wa-vnext__title">{workflowName}</span>
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
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnDuration',
                          'Duration',
                        )}
                      >
                        {runDuration(run, now)}
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnInputPreview',
                          'Input preview',
                        )}
                      >
                        <span className="wa-vnext__input-preview">
                          {run.inputSummary || '-'}
                        </span>
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
                ? t('workflowActivityVNext.activity.page', 'Page {page}', {
                    page: activePagination.page,
                  })
                : t(
                    'workflowActivityVNext.activity.pageOf',
                    'Page {page} of {total}',
                    { page: activePagination.page, total: totalPages },
                  )}
            </span>
            <Pagination
              current={activePagination.page}
              data-testid="activity-pagination"
              disabled={isResolvingPage || runs.isFetching}
              onChange={(page) => void goToPage(page)}
              pageSize={activityPageSize}
              showQuickJumper={totalCount !== null}
              showSizeChanger={false}
              total={paginationTotal}
            />
          </div>
          {pageNavigationError ? (
            <div className="wa-vnext__pagination-actions" role="alert">
              <p>
                {t(
                  'workflowActivityVNext.activity.pageUnavailable',
                  "Couldn't load this page",
                )}
              </p>
              <Button onClick={retryPageNavigation}>
                {t('workflowActivityVNext.common.retry', 'Retry')}
              </Button>
              <TechnicalDetails>
                {pageNavigationError instanceof Error
                  ? pageNavigationError.message
                  : String(pageNavigationError)}
              </TechnicalDetails>
            </div>
          ) : null}
        </>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
