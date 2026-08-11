import {
  CloseOutlined,
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
  const search = params.get('q') ?? '';

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
  const runs = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'runs',
      scopeId,
      status,
      origin,
      effectiveDefinition,
    ],
    queryFn: () =>
      workflowActivityApi.listRuns(scopeId, {
        status: status || undefined,
        origins: origin ? [origin] : undefined,
        definitionActorIds: effectiveDefinition
          ? [effectiveDefinition]
          : undefined,
        take: 100,
      }),
    enabled: workflowFilterReady,
    refetchOnMount: 'always',
    retry: false,
  });

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
      ) : workflowFilterPresent && workflow.isPending ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'workflowActivityVNext.activity.workflowFilterLoading',
            'Loading workflow activity…',
          )}
          columnWidths={['minmax(240px, 1fr)', 120, 120, 190]}
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
          columnWidths={['minmax(240px, 1fr)', 120, 120, 190]}
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
                  {t('workflowActivityVNext.activity.columnOrigin', 'Source')}
                </th>
                <th>
                  {t('workflowActivityVNext.activity.columnUpdated', 'Updated')}
                </th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((run) => {
                const statusPresentation = getRunStatusPresentation(run.status);
                const workflowName =
                  run.workflowName ||
                  t(
                    'workflowActivityVNext.activity.unnamed',
                    'Unnamed workflow',
                  );
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
                          'Open {name}',
                          { name: workflowName },
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
                        <span className="wa-vnext__title">{workflowName}</span>
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
                        'workflowActivityVNext.activity.columnOrigin',
                        'Source',
                      )}
                    >
                      {run.runOrigin || '-'}
                    </td>
                    <td
                      data-label={t(
                        'workflowActivityVNext.activity.columnUpdated',
                        'Updated',
                      )}
                    >
                      {formatDate(run.updatedAtUtc)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </TableScrollRegion>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
