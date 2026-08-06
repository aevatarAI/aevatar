import {
  ArrowRightOutlined,
  CopyOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import { Alert, Button, Input, Select, Space } from 'antd';
import React from 'react';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { buildWorkflowActivityRunHref } from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import { getRunOriginLabel, getRunStatusPresentation } from './runPresentation';

const supportedRunStatuses = new Set(['running', 'completed', 'failed']);
const DEFAULT_RUN_TAKE = 50;
const MAX_RUN_TAKE = 500;

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

function normalizeDateTimeFilter(value: string | null): string {
  const normalized = value?.trim() ?? '';
  return /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(normalized) ? normalized : '';
}

function toUtcFilter(value: string): string | undefined {
  if (!value) return undefined;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date.toISOString();
}

function getShortRunReference(runId: string): string {
  const normalized = runId.trim();
  const tail = normalized.split(':').at(-1) || normalized;
  return tail.length <= 12 ? tail : `${tail.slice(0, 6)}…${tail.slice(-4)}`;
}

function fallbackCopy(text: string): boolean {
  if (typeof document === 'undefined') {
    return false;
  }

  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', 'true');
  textarea.style.position = 'absolute';
  textarea.style.left = '-9999px';
  document.body.append(textarea);
  textarea.select();

  try {
    return document.execCommand('copy');
  } finally {
    textarea.remove();
  }
}

function formatLiveDuration(startedAtUtc: string | null, now: number): string {
  if (!startedAtUtc) {
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  }
  const startedAt = new Date(startedAtUtc).getTime();
  if (!Number.isFinite(startedAt) || !Number.isFinite(now)) {
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  }
  const totalSeconds = Math.max(0, Math.floor((now - startedAt) / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) {
    return t(
      'workflowActivityVNext.activity.durationHoursMinutes',
      '{hours}h {minutes}m',
      { hours, minutes },
    );
  }
  if (minutes > 0 && seconds > 0) {
    return t(
      'workflowActivityVNext.activity.durationMinutesSeconds',
      '{minutes}m {seconds}s',
      { minutes, seconds },
    );
  }
  if (minutes > 0) {
    return t('workflowActivityVNext.activity.durationMinutes', '{minutes}m', {
      minutes,
    });
  }
  return t('workflowActivityVNext.activity.durationSeconds', '{seconds}s', {
    seconds,
  });
}

function preservesRunPageForLoadMore(
  previousQueryKey: readonly unknown[] | undefined,
  nextQueryKey: readonly unknown[],
): boolean {
  if (!previousQueryKey || previousQueryKey.length !== nextQueryKey.length) {
    return false;
  }

  const previousTake = previousQueryKey.at(-1);
  const nextTake = nextQueryKey.at(-1);
  return (
    typeof previousTake === 'number' &&
    typeof nextTake === 'number' &&
    nextTake > previousTake &&
    previousQueryKey
      .slice(0, -1)
      .every((value, index) => Object.is(value, nextQueryKey[index]))
  );
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
  const toast = useConsoleToast();
  const initialParams = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const [status, setStatus] = React.useState(
    normalizeRunStatusFilter(initialParams.get('status')),
  );
  const [origin, setOrigin] = React.useState(initialParams.get('origin') ?? '');
  const [definition, setDefinition] = React.useState(
    initialParams.get('definition') ?? '',
  );
  const [workflowFilter, setWorkflowFilter] = React.useState(
    initialParams.get('workflowFilter') ?? '',
  );
  const [search, setSearch] = React.useState(initialParams.get('q') ?? '');
  const [from, setFrom] = React.useState(
    normalizeDateTimeFilter(initialParams.get('from')),
  );
  const [to, setTo] = React.useState(
    normalizeDateTimeFilter(initialParams.get('to')),
  );
  const [take, setTake] = React.useState(DEFAULT_RUN_TAKE);
  const [copiedRunId, setCopiedRunId] = React.useState('');
  const runsQueryKey = [
    'workflow-activity-vnext',
    'runs',
    scopeId,
    status,
    origin,
    definition,
    from,
    to,
    take,
  ] as const;
  const runs = useQuery({
    queryKey: runsQueryKey,
    queryFn: () =>
      workflowActivityApi.listRuns(scopeId, {
        status: status || undefined,
        origins: origin ? [origin] : undefined,
        definitionActorIds: definition ? [definition] : undefined,
        fromUtc: toUtcFilter(from),
        toUtc: toUtcFilter(to),
        take,
      }),
    placeholderData: (previous, previousQuery) =>
      preservesRunPageForLoadMore(previousQuery?.queryKey, runsQueryKey)
        ? previous
        : undefined,
    retry: false,
  });

  React.useEffect(() => {
    const params = new URLSearchParams(location.search);
    setSearch(params.get('q') ?? '');
    setStatus(normalizeRunStatusFilter(params.get('status')));
    setOrigin(params.get('origin') ?? '');
    setDefinition(params.get('definition') ?? '');
    setWorkflowFilter(params.get('workflowFilter') ?? '');
    setFrom(normalizeDateTimeFilter(params.get('from')));
    setTo(normalizeDateTimeFilter(params.get('to')));
    setTake(DEFAULT_RUN_TAKE);
  }, [location.search]);

  React.useEffect(() => {
    const params = new URLSearchParams();
    if (search.trim()) params.set('q', search.trim());
    if (status) params.set('status', status);
    if (origin) params.set('origin', origin);
    if (definition) params.set('definition', definition);
    if (workflowFilter) params.set('workflowFilter', workflowFilter);
    if (from) params.set('from', from);
    if (to) params.set('to', to);
    const suffix = params.toString();
    history.replace(`${location.pathname}${suffix ? `?${suffix}` : ''}`);
  }, [
    definition,
    from,
    location.pathname,
    origin,
    search,
    status,
    to,
    workflowFilter,
  ]);

  const filtered = (runs.data ?? []).filter((run) => {
    const normalized = search.trim().toLowerCase();
    return (
      !normalized ||
      [run.workflowName, run.runId, run.status].some((value) =>
        value.toLowerCase().includes(normalized),
      )
    );
  });
  const hasLiveRuns = (runs.data ?? []).some(
    (run) => run.status.trim().toLowerCase() === 'running',
  );
  const [now, setNow] = React.useState(() => Date.now());

  React.useEffect(() => {
    if (!hasLiveRuns) return undefined;
    setNow(Date.now());
    const interval = window.setInterval(() => setNow(Date.now()), 30_000);
    return () => window.clearInterval(interval);
  }, [hasLiveRuns]);

  const copyRunReference = async (runId: string) => {
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(runId);
      } else if (!fallbackCopy(runId)) {
        throw new Error('Clipboard is unavailable.');
      }
      setCopiedRunId(runId);
      toast.success(
        t(
          'workflowActivityVNext.activity.copyRunSuccess',
          'Run reference copied.',
        ),
      );
    } catch {
      toast.error(
        t(
          'workflowActivityVNext.activity.copyRunFailed',
          'Failed to copy run reference.',
        ),
      );
    }
  };

  const isLoadingMore = runs.isPlaceholderData && runs.isFetching;
  const canLoadMore = (runs.data?.length ?? 0) === take && take < MAX_RUN_TAKE;

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
      {workflowFilter === 'unavailable' ? (
        <Alert
          closable
          message={t(
            'workflowActivityVNext.activity.workflowFilterUnavailable',
            "This workflow can't be filtered yet. Showing all activity.",
          )}
          onClose={() => setWorkflowFilter('')}
          showIcon
          type="warning"
        />
      ) : null}
      <div className="wa-vnext__toolbar">
        <Input
          allowClear
          aria-label={t(
            'workflowActivityVNext.activity.searchAria',
            'Search runs',
          )}
          className="wa-vnext__toolbar-search"
          onChange={(event) => setSearch(event.target.value)}
          placeholder={t(
            'workflowActivityVNext.activity.search',
            'Search runs',
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
            onChange={(nextStatus) => {
              setStatus(nextStatus);
              setTake(DEFAULT_RUN_TAKE);
            }}
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
            onChange={(nextOrigin) => {
              setOrigin(nextOrigin);
              setTake(DEFAULT_RUN_TAKE);
            }}
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
          <label className="wa-vnext__date-filter">
            <span>
              {t(
                'workflowActivityVNext.activity.activityAfter',
                'Activity after',
              )}
            </span>
            <input
              aria-label={t(
                'workflowActivityVNext.activity.activityAfter',
                'Activity after',
              )}
              onChange={(event) => {
                setFrom(event.target.value);
                setTake(DEFAULT_RUN_TAKE);
              }}
              type="datetime-local"
              value={from}
            />
          </label>
          <label className="wa-vnext__date-filter">
            <span>
              {t(
                'workflowActivityVNext.activity.activityBefore',
                'Activity before',
              )}
            </span>
            <input
              aria-label={t(
                'workflowActivityVNext.activity.activityBefore',
                'Activity before',
              )}
              onChange={(event) => {
                setTo(event.target.value);
                setTake(DEFAULT_RUN_TAKE);
              }}
              type="datetime-local"
              value={to}
            />
          </label>
          {definition ? (
            <Button
              onClick={() => {
                setDefinition('');
                setTake(DEFAULT_RUN_TAKE);
              }}
            >
              {t(
                'workflowActivityVNext.activity.clearWorkflowFilter',
                'Show all workflows',
              )}
            </Button>
          ) : null}
        </Space>
      </div>
      {runs.isPending ? (
        <div aria-live="polite" className="wa-vnext__state">
          <p>
            {t('workflowActivityVNext.activity.loading', 'Loading activity…')}
          </p>
        </div>
      ) : runs.isError ? (
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
              {runs.data?.length
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
          <div aria-live="polite" className="wa-vnext__result-summary">
            <span>
              {search.trim()
                ? t(
                    'workflowActivityVNext.activity.filteredCount',
                    'Showing {visible} of {loaded} loaded runs',
                    {
                      visible: filtered.length,
                      loaded: runs.data?.length ?? 0,
                    },
                  )
                : t(
                    'workflowActivityVNext.activity.resultCount',
                    'Showing {count} loaded runs',
                    { count: runs.data?.length ?? 0 },
                  )}
            </span>
            {canLoadMore || isLoadingMore ? (
              <Button
                disabled={isLoadingMore}
                loading={isLoadingMore}
                onClick={() =>
                  setTake((current) =>
                    Math.min(current + DEFAULT_RUN_TAKE, MAX_RUN_TAKE),
                  )
                }
              >
                {t('workflowActivityVNext.activity.loadMore', 'Load more')}
              </Button>
            ) : null}
          </div>
          <TableScrollRegion
            ariaLabel={t('workflowActivityVNext.activity.title', 'Activity')}
            className="wa-vnext__activity-table"
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
                  <th className="wa-vnext__activity-source">
                    {t('workflowActivityVNext.activity.columnOrigin', 'Source')}
                  </th>
                  <th className="wa-vnext__activity-actions">
                    {t('workflowActivityVNext.activity.columnAction', 'Action')}
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
                  const runReference = getShortRunReference(run.runId);
                  const running = run.status.trim().toLowerCase() === 'running';
                  return (
                    <tr key={run.runId}>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnRun',
                          'Workflow',
                        )}
                      >
                        <button
                          aria-label={t(
                            'workflowActivityVNext.activity.openRunAria',
                            'Open {name} run {reference}',
                            { name: workflowName, reference: runReference },
                          )}
                          className="wa-vnext__run-link"
                          onClick={() =>
                            history.push(
                              buildWorkflowActivityRunHref(scopeId, run.runId),
                            )
                          }
                          style={{
                            background: 'transparent',
                            border: 0,
                            color: 'var(--wa-blue)',
                            padding: 0,
                            textAlign: 'left',
                          }}
                          type="button"
                        >
                          <span className="wa-vnext__title">
                            {workflowName}
                          </span>
                          <span className="wa-vnext__sub">
                            {t(
                              'workflowActivityVNext.activity.runReference',
                              'Run {reference}',
                              { reference: runReference },
                            )}
                          </span>
                        </button>
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
                      </td>
                      <td
                        data-label={t(
                          'workflowActivityVNext.activity.columnStarted',
                          'Started',
                        )}
                      >
                        <span>{formatDate(run.startedAtUtc)}</span>
                        {running ? (
                          <span className="wa-vnext__sub">
                            {t(
                              'workflowActivityVNext.activity.elapsed',
                              '{duration} elapsed',
                              {
                                duration: formatLiveDuration(
                                  run.startedAtUtc,
                                  now,
                                ),
                              },
                            )}
                          </span>
                        ) : null}
                      </td>
                      <td
                        className="wa-vnext__activity-source"
                        data-label={t(
                          'workflowActivityVNext.activity.columnOrigin',
                          'Source',
                        )}
                      >
                        {getRunOriginLabel(run.runOrigin)}
                      </td>
                      <td
                        className="wa-vnext__activity-actions"
                        data-label={t(
                          'workflowActivityVNext.activity.columnAction',
                          'Action',
                        )}
                      >
                        <Space size={4}>
                          <Button
                            aria-label={t(
                              'workflowActivityVNext.activity.copyRunAria',
                              'Copy run reference {reference}',
                              { reference: runReference },
                            )}
                            icon={<CopyOutlined />}
                            onClick={() => void copyRunReference(run.runId)}
                            size="small"
                            title={
                              copiedRunId === run.runId
                                ? t(
                                    'workflowActivityVNext.activity.copiedRun',
                                    'Copied',
                                  )
                                : t(
                                    'workflowActivityVNext.activity.copyRun',
                                    'Copy run reference',
                                  )
                            }
                            type="text"
                          />
                          <Button
                            aria-label={t(
                              'workflowActivityVNext.activity.openExactRunAria',
                              'Open run {reference}',
                              { reference: runReference },
                            )}
                            icon={<ArrowRightOutlined />}
                            onClick={() =>
                              history.push(
                                buildWorkflowActivityRunHref(
                                  scopeId,
                                  run.runId,
                                ),
                              )
                            }
                            size="small"
                            title={t(
                              'workflowActivityVNext.activity.openRun',
                              'Open run',
                            )}
                            type="text"
                          />
                        </Space>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </TableScrollRegion>
        </>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
