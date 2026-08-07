import {
  ArrowRightOutlined,
  CloseOutlined,
  CopyOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { getLocale } from '@umijs/max';
import { Button, Input, Select, Space } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import {
  WorkflowActivityApiError,
  workflowActivityApi,
} from '@/shared/api/workflowActivityApi';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { buildWorkflowActivityRunHref } from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import { getRunStatusPresentation } from './runPresentation';

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
  if (typeof document === 'undefined') return false;

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
  const params = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const rawStatus = params.get('status');
  const status = normalizeRunStatusFilter(rawStatus);
  const legacyOriginPresent = params.has('origin');
  const definition = params.get('definition')?.trim() ?? '';
  const workflowFilterPresent = params.has('workflowId');
  const workflowId = params.get('workflowId')?.trim() ?? '';
  const search = params.get('q') ?? '';
  const from = normalizeDateTimeFilter(params.get('from'));
  const to = normalizeDateTimeFilter(params.get('to'));
  const [take, setTake] = React.useState(DEFAULT_RUN_TAKE);
  const [copiedRunId, setCopiedRunId] = React.useState('');

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
    const unsupportedStatus = Boolean(rawStatus && !status);
    if (!unsupportedStatus && !legacyOriginPresent) return;
    replaceParams((next) => {
      if (unsupportedStatus) next.delete('status');
      next.delete('origin');
    });
  }, [legacyOriginPresent, rawStatus, replaceParams, status]);

  const workflow = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'activity-workflow',
      scopeId,
      workflowId,
    ],
    queryFn: () => scopesApi.getWorkflowDetail(scopeId, workflowId),
    enabled: workflowFilterPresent && Boolean(workflowId),
    retry: false,
  });
  const resolvedDefinition =
    workflow.data?.source?.definitionActorId.trim() ?? '';
  const effectiveDefinition = workflowFilterPresent
    ? resolvedDefinition
    : definition;
  const workflowFilterReady =
    !workflowFilterPresent || Boolean(resolvedDefinition);
  const runsQueryKey = [
    'workflow-activity-vnext',
    'runs',
    scopeId,
    status,
    effectiveDefinition,
    from,
    to,
    take,
  ] as const;
  const runs = useQuery({
    queryKey: runsQueryKey,
    queryFn: () =>
      workflowActivityApi.listRuns(scopeId, {
        status: status || undefined,
        origins: undefined,
        definitionActorIds: effectiveDefinition
          ? [effectiveDefinition]
          : undefined,
        fromUtc: toUtcFilter(from),
        toUtc: toUtcFilter(to),
        take,
      }),
    enabled: workflowFilterReady,
    placeholderData: (previous, previousQuery) =>
      preservesRunPageForLoadMore(previousQuery?.queryKey, runsQueryKey)
        ? previous
        : undefined,
    retry: false,
  });

  React.useEffect(() => {
    setTake(DEFAULT_RUN_TAKE);
  }, [scopeId, status, effectiveDefinition, from, to]);

  const refresh = () => {
    if (workflowFilterPresent && !resolvedDefinition) {
      if (workflowId) void workflow.refetch();
      return;
    }
    void runs.refetch();
  };

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
        <Button icon={<ReloadOutlined />} onClick={refresh}>
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
          onChange={(event) => replaceParam('q', event.target.value)}
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
            onChange={(value) => {
              setTake(DEFAULT_RUN_TAKE);
              replaceParam('status', value);
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
                setTake(DEFAULT_RUN_TAKE);
                replaceParam('from', event.target.value);
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
                setTake(DEFAULT_RUN_TAKE);
                replaceParam('to', event.target.value);
              }}
              type="datetime-local"
              value={to}
            />
          </label>
          {definition && !workflowFilterPresent ? (
            <Button
              onClick={() => {
                setTake(DEFAULT_RUN_TAKE);
                replaceParam('definition', '');
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
      ) : workflowFilterPresent && workflow.isPending ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'workflowActivityVNext.activity.workflowFilterLoading',
            'Loading workflow activity…',
          )}
          columnWidths={['minmax(240px, 1fr)', 120, 190, 150, 92]}
          rows={4}
          tableMinWidth={900}
          variant="table"
        />
      ) : workflowFilterPresent && workflow.isError ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>
              {t(
                'workflowActivityVNext.activity.workflowFilterResolutionFailed',
                'Workflow activity unavailable',
              )}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.activity.workflowFilterResolutionFailedDescription',
                'Try again or remove the workflow filter.',
              )}
            </p>
            <Button onClick={() => void workflow.refetch()}>
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
            <TechnicalDetails>
              {workflow.error instanceof Error
                ? workflow.error.message
                : String(workflow.error)}
            </TechnicalDetails>
          </div>
        </div>
      ) : workflowFilterPresent && !resolvedDefinition ? (
        <div className="wa-vnext__state">
          <div>
            <h2>
              {t(
                'workflowActivityVNext.activity.workflowFilterUnavailableTitle',
                'Activity filtering is unavailable',
              )}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.activity.workflowFilterUnavailableDescription',
                'No runs are shown because this workflow does not expose an Activity filter.',
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
          columnWidths={['minmax(240px, 1fr)', 120, 190, 150, 92]}
          rows={4}
          tableMinWidth={900}
          variant="table"
        />
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
                        {run.runOrigin || '-'}
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
