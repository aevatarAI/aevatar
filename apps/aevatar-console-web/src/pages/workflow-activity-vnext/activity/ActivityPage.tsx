import { ReloadOutlined, SearchOutlined } from '@ant-design/icons';
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
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { buildWorkflowActivityRunHref } from '../navigation';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import { getRunOriginLabel, getRunStatusPresentation } from './runPresentation';

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
  const initialParams = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const [status, setStatus] = React.useState(initialParams.get('status') ?? '');
  const [origin, setOrigin] = React.useState(initialParams.get('origin') ?? '');
  const [definition, setDefinition] = React.useState(
    initialParams.get('definition') ?? '',
  );
  const [workflowFilter, setWorkflowFilter] = React.useState(
    initialParams.get('workflowFilter') ?? '',
  );
  const [search, setSearch] = React.useState('');
  const runs = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'runs',
      scopeId,
      status,
      origin,
      definition,
    ],
    queryFn: () =>
      workflowActivityApi.listRuns(scopeId, {
        status: status || undefined,
        origins: origin ? [origin] : undefined,
        definitionActorIds: definition ? [definition] : undefined,
        take: 100,
      }),
    retry: false,
  });

  React.useEffect(() => {
    const params = new URLSearchParams(location.search);
    setStatus(params.get('status') ?? '');
    setOrigin(params.get('origin') ?? '');
    setDefinition(params.get('definition') ?? '');
    setWorkflowFilter(params.get('workflowFilter') ?? '');
  }, [location.search]);

  React.useEffect(() => {
    const params = new URLSearchParams();
    if (status) params.set('status', status);
    if (origin) params.set('origin', origin);
    if (definition) params.set('definition', definition);
    if (workflowFilter) params.set('workflowFilter', workflowFilter);
    const suffix = params.toString();
    history.replace(`${location.pathname}${suffix ? `?${suffix}` : ''}`);
  }, [definition, location.pathname, origin, status, workflowFilter]);

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
          onChange={(event) => setSearch(event.target.value)}
          placeholder={t(
            'workflowActivityVNext.activity.search',
            'Search runs',
          )}
          prefix={<SearchOutlined />}
          role="searchbox"
          style={{ width: 320 }}
          value={search}
        />
        <Space wrap>
          <Select
            aria-label={t(
              'workflowActivityVNext.activity.statusFilter',
              'Run status',
            )}
            onChange={setStatus}
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
              {
                label: t(
                  'workflowActivityVNext.activity.statusWaiting',
                  'Waiting',
                ),
                value: 'waiting',
              },
            ]}
            style={{ width: 150 }}
            value={status}
          />
          <Select
            aria-label={t(
              'workflowActivityVNext.activity.originFilter',
              'Run source',
            )}
            onChange={setOrigin}
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
            style={{ width: 160 }}
            value={origin}
          />
          {definition ? (
            <Button onClick={() => setDefinition('')}>
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
        <div className="wa-vnext__table-wrap wa-vnext__activity-table">
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
                    <td>
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
                    <td>
                      <span
                        className={`wa-vnext__status wa-vnext__status--${statusPresentation.className}`}
                      >
                        {statusPresentation.label}
                      </span>
                    </td>
                    <td>{getRunOriginLabel(run.runOrigin)}</td>
                    <td>{formatDate(run.updatedAtUtc)}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </WorkflowActivityVNextShell>
  );
};

export default ActivityPage;
