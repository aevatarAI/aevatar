import {
  DeleteOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Input, Modal, Select, Space, Tooltip } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import { t } from '@/shared/i18n/messages';
import type { ScopeWorkflowSummary } from '@/shared/models/scopes';
import { history } from '@/shared/navigation/history';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import type { StudioWorkflowDraftSummary } from '@/shared/studio/models';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';

type WorkflowRow = {
  readonly description: string;
  readonly hasCommittedSource: boolean;
  readonly hasDraftSource: boolean;
  readonly name: string;
  readonly stepCount?: number;
  readonly updatedAtUtc: string | null;
  readonly workflowId: string;
};

function toDraftRow(
  item: StudioWorkflowDraftSummary,
  committed?: WorkflowRow,
): WorkflowRow {
  return {
    description: item.description,
    hasCommittedSource: Boolean(committed),
    hasDraftSource: true,
    name: item.name,
    stepCount: item.stepCount,
    updatedAtUtc: item.updatedAtUtc,
    workflowId: item.workflowId,
  };
}

function toCommittedRow(item: ScopeWorkflowSummary): WorkflowRow {
  return {
    description: '',
    hasCommittedSource: true,
    hasDraftSource: false,
    name: item.displayName || item.workflowName,
    updatedAtUtc: item.updatedAt,
    workflowId: item.workflowId,
  };
}

type WorkflowView = 'all' | 'drafts';

function readWorkflowView(params: URLSearchParams): WorkflowView {
  return params.get('view') === 'drafts' ? 'drafts' : 'all';
}

function formatDate(value: string | null): string {
  if (!value)
    return t('workflowActivityVNext.common.unavailable', 'Unavailable');
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? value
    : new Intl.DateTimeFormat(undefined, {
        dateStyle: 'medium',
        timeStyle: 'short',
      }).format(date);
}

const WorkflowsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const initialParams = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const [query, setQuery] = React.useState(initialParams.get('q') ?? '');
  const [view, setView] = React.useState<WorkflowView>(
    readWorkflowView(initialParams),
  );
  const [activityWorkflowId, setActivityWorkflowId] = React.useState('');
  const [activityError, setActivityError] = React.useState('');
  const [deleteTarget, setDeleteTarget] = React.useState<WorkflowRow | null>(
    null,
  );
  const [deleteError, setDeleteError] = React.useState('');
  const [deleteSucceeded, setDeleteSucceeded] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const drafts = useQuery({
    queryKey: ['workflow-activity-vnext', 'drafts', scopeId],
    queryFn: () => studioApi.listWorkflowDrafts(scopeId),
    retry: false,
  });
  const committed = useQuery({
    queryKey: ['workflow-activity-vnext', 'committed', scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
    retry: false,
  });

  React.useEffect(() => {
    const params = new URLSearchParams(location.search);
    setQuery(params.get('q') ?? '');
    setView(readWorkflowView(params));
  }, [location.search]);

  React.useEffect(() => {
    const params = new URLSearchParams();
    if (query.trim()) params.set('q', query.trim());
    if (view === 'drafts') params.set('view', 'drafts');
    const suffix = params.toString();
    history.replace(`${location.pathname}${suffix ? `?${suffix}` : ''}`);
  }, [location.pathname, query, view]);

  const loading = drafts.isPending || committed.isPending;
  const draftWorkflowIds = React.useMemo(
    () => new Set((drafts.data ?? []).map((item) => item.workflowId)),
    [drafts.data],
  );
  const rows = React.useMemo(() => {
    const merged = new Map<string, WorkflowRow>();
    for (const item of committed.data ?? [])
      merged.set(item.workflowId, toCommittedRow(item));
    for (const item of drafts.data ?? [])
      merged.set(
        item.workflowId,
        toDraftRow(item, merged.get(item.workflowId)),
      );
    const normalized = query.trim().toLowerCase();
    return [...merged.values()]
      .filter(
        (item) => view !== 'drafts' || draftWorkflowIds.has(item.workflowId),
      )
      .filter(
        (item) =>
          !normalized ||
          [item.name, item.description, item.workflowId].some((value) =>
            value.toLowerCase().includes(normalized),
          ),
      )
      .sort(
        (left, right) =>
          Date.parse(right.updatedAtUtc ?? '') -
          Date.parse(left.updatedAtUtc ?? ''),
      );
  }, [committed.data, draftWorkflowIds, drafts.data, query, view]);
  const totalFailure = drafts.isError && committed.isError;
  const filtersActive = Boolean(query.trim()) || view === 'drafts';

  const retry = () => {
    void drafts.refetch();
    void committed.refetch();
  };

  const openActivity = async (row: WorkflowRow) => {
    const activityHref = buildWorkflowActivitySectionHref(scopeId, 'activity');
    setActivityError('');
    if (!row.hasCommittedSource) {
      history.push(`${activityHref}?workflowFilter=unavailable`);
      return;
    }

    setActivityWorkflowId(row.workflowId);
    try {
      const detail = await scopesApi.getWorkflowDetail(scopeId, row.workflowId);
      const definitionActorId = detail.source?.definitionActorId.trim() ?? '';
      history.push(
        definitionActorId
          ? `${activityHref}?definition=${encodeURIComponent(definitionActorId)}`
          : `${activityHref}?workflowFilter=unavailable`,
      );
    } catch (error) {
      setActivityError(error instanceof Error ? error.message : String(error));
    } finally {
      setActivityWorkflowId('');
    }
  };

  const closeDelete = () => {
    if (deleting) return;
    setDeleteTarget(null);
    setDeleteError('');
    setDeleteSucceeded(false);
  };

  const confirmDelete = async () => {
    if (!deleteTarget || deleting) return;
    setDeleting(true);
    setDeleteError('');
    let removed = deleteSucceeded;
    try {
      if (!removed) {
        try {
          await studioApi.deleteWorkflowDraft(deleteTarget.workflowId, scopeId);
          removed = true;
          setDeleteSucceeded(true);
        } catch (error) {
          if (!isStudioApiStatus(error, 404)) throw error;
          removed = true;
          setDeleteSucceeded(true);
        }
      }

      const refreshed = await drafts.refetch();
      if (refreshed.isError) throw refreshed.error;
      setDeleteTarget(null);
      setDeleteSucceeded(false);
    } catch (error) {
      setDeleteError(error instanceof Error ? error.message : String(error));
      setDeleteSucceeded(removed);
    } finally {
      setDeleting(false);
    }
  };

  return (
    <WorkflowActivityVNextShell
      activeSection="workflows"
      description={t(
        'workflowActivityVNext.workflows.description',
        'Create, edit, and run your workflows.',
      )}
      headerActions={
        <Space wrap>
          <Button
            aria-label={t(
              'workflowActivityVNext.workflows.refreshAria',
              'Refresh workflows',
            )}
            icon={<ReloadOutlined />}
            onClick={retry}
          >
            {t('workflowActivityVNext.common.refresh', 'Refresh')}
          </Button>
          <Button
            icon={<PlusOutlined />}
            onClick={() => history.push(buildWorkflowActivityNewHref(scopeId))}
            type="primary"
          >
            {t('workflowActivityVNext.workflows.new', 'New workflow')}
          </Button>
        </Space>
      }
      scopeId={scopeId}
      title={t('workflowActivityVNext.workflows.title', 'Workflows')}
    >
      <div className="wa-vnext__toolbar">
        <Input
          allowClear
          aria-label={t(
            'workflowActivityVNext.workflows.searchAria',
            'Search workflows',
          )}
          className="wa-vnext__toolbar-search"
          onChange={(event) => setQuery(event.target.value)}
          placeholder={t(
            'workflowActivityVNext.workflows.search',
            'Search workflows',
          )}
          prefix={<SearchOutlined />}
          role="searchbox"
          value={query}
        />
        <Space className="wa-vnext__toolbar-filters" wrap>
          <Select
            aria-label={t(
              'workflowActivityVNext.workflows.viewFilter',
              'Workflow view',
            )}
            onChange={setView}
            options={[
              {
                label: t(
                  'workflowActivityVNext.workflows.allView',
                  'All workflows',
                ),
                value: 'all',
              },
              {
                disabled: drafts.isError,
                label: t(
                  'workflowActivityVNext.workflows.draftsView',
                  'Drafts',
                ),
                value: 'drafts',
              },
            ]}
            value={view}
          />
        </Space>
      </div>

      {drafts.isError && !committed.isError && view !== 'drafts' ? (
        <Alert
          message={t(
            'workflowActivityVNext.workflows.partialUnavailable',
            "Some workflows couldn't be loaded",
          )}
          showIcon
          type="warning"
        />
      ) : null}
      {committed.isError && !drafts.isError ? (
        <Alert
          message={t(
            'workflowActivityVNext.workflows.partialUnavailable',
            "Some workflows couldn't be loaded",
          )}
          showIcon
          type="warning"
        />
      ) : null}
      {activityError ? (
        <Alert
          message={t(
            'workflowActivityVNext.workflows.activityResolutionFailed',
            "Activity couldn't be opened for this workflow",
          )}
          description={<TechnicalDetails>{activityError}</TechnicalDetails>}
          showIcon
          type="error"
        />
      ) : null}

      {loading ? (
        <div aria-live="polite" className="wa-vnext__state">
          <p>
            {t('workflowActivityVNext.workflows.loading', 'Loading workflows')}
          </p>
        </div>
      ) : view === 'drafts' && drafts.isError ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>
              {t(
                'workflowActivityVNext.workflows.draftsUnavailable',
                'Draft workflows unavailable',
              )}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.workflows.draftsUnavailableDescription',
                'Try again to load draft workflows.',
              )}
            </p>
            <Button
              aria-label={t(
                'workflowActivityVNext.workflows.retryAria',
                'Retry workflows',
              )}
              icon={<ReloadOutlined />}
              onClick={() => void drafts.refetch()}
            >
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          </div>
        </div>
      ) : totalFailure ? (
        <div className="wa-vnext__state" role="alert">
          <div>
            <h2>
              {t(
                'workflowActivityVNext.workflows.unavailable',
                'Workflows unavailable',
              )}
            </h2>
            <p>
              {t(
                'workflowActivityVNext.workflows.unavailableDescription',
                'Try again to load your workflows.',
              )}
            </p>
            <Button
              aria-label={t(
                'workflowActivityVNext.workflows.retryAria',
                'Retry workflows',
              )}
              icon={<ReloadOutlined />}
              onClick={retry}
            >
              {t('workflowActivityVNext.common.retry', 'Retry')}
            </Button>
          </div>
        </div>
      ) : rows.length === 0 ? (
        <div className="wa-vnext__state">
          <div>
            <h2>
              {filtersActive
                ? t(
                    'workflowActivityVNext.workflows.noMatch',
                    'No matching workflows',
                  )
                : t(
                    'workflowActivityVNext.workflows.empty',
                    'No workflows yet',
                  )}
            </h2>
            <p>
              {filtersActive
                ? t(
                    'workflowActivityVNext.workflows.noMatchDescription',
                    'Try a different search or filter.',
                  )
                : t(
                    'workflowActivityVNext.workflows.emptyDescription',
                    'Create a workflow to get started.',
                  )}
            </p>
            {filtersActive ? (
              <Button
                onClick={() => {
                  setQuery('');
                  setView('all');
                }}
              >
                {t(
                  'workflowActivityVNext.workflows.clearFilters',
                  'Clear filters',
                )}
              </Button>
            ) : (
              <Button
                icon={<PlusOutlined />}
                onClick={() =>
                  history.push(buildWorkflowActivityNewHref(scopeId))
                }
                type="primary"
              >
                {t('workflowActivityVNext.workflows.new', 'New workflow')}
              </Button>
            )}
          </div>
        </div>
      ) : (
        <TableScrollRegion
          ariaLabel={t('workflowActivityVNext.workflows.title', 'Workflows')}
        >
          <table className="wa-vnext__table">
            <thead>
              <tr>
                <th>
                  {t(
                    'workflowActivityVNext.workflows.columnWorkflow',
                    'Workflow',
                  )}
                </th>
                <th>
                  {t(
                    'workflowActivityVNext.workflows.columnUpdated',
                    'Updated',
                  )}
                </th>
                <th>
                  {t(
                    'workflowActivityVNext.workflows.columnActions',
                    'Actions',
                  )}
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.workflowId}>
                  <td
                    data-label={t(
                      'workflowActivityVNext.workflows.columnWorkflow',
                      'Workflow',
                    )}
                  >
                    <span className="wa-vnext__title">{row.name}</span>
                    {row.description ? (
                      <span className="wa-vnext__sub">{row.description}</span>
                    ) : null}
                  </td>
                  <td
                    data-label={t(
                      'workflowActivityVNext.workflows.columnUpdated',
                      'Updated',
                    )}
                  >
                    {formatDate(row.updatedAtUtc)}
                  </td>
                  <td
                    data-label={t(
                      'workflowActivityVNext.workflows.columnActions',
                      'Actions',
                    )}
                  >
                    <Space>
                      <Button
                        aria-label={t(
                          'workflowActivityVNext.workflows.openAria',
                          'Open {name}',
                          { name: row.name },
                        )}
                        onClick={() =>
                          history.push(
                            buildWorkflowActivityEditorHref(
                              scopeId,
                              row.workflowId,
                            ),
                          )
                        }
                      >
                        {t('workflowActivityVNext.common.open', 'Open')}
                      </Button>
                      <Button
                        loading={activityWorkflowId === row.workflowId}
                        onClick={() => void openActivity(row)}
                        title={
                          !row.hasCommittedSource
                            ? t(
                                'workflowActivityVNext.workflows.activityFilterUnavailable',
                                "Activity filtering isn't available for this workflow yet.",
                              )
                            : undefined
                        }
                      >
                        {t(
                          'workflowActivityVNext.workflows.viewActivity',
                          'Activity',
                        )}
                      </Button>
                      {row.hasDraftSource ? (
                        <Tooltip
                          title={t(
                            'workflowActivityVNext.workflows.deleteDraft',
                            'Delete draft',
                          )}
                        >
                          <Button
                            aria-label={t(
                              'workflowActivityVNext.workflows.deleteAria',
                              'Delete {name}',
                              { name: row.name },
                            )}
                            danger
                            icon={<DeleteOutlined />}
                            onClick={() => {
                              setDeleteTarget(row);
                              setDeleteError('');
                              setDeleteSucceeded(false);
                            }}
                            type="text"
                          />
                        </Tooltip>
                      ) : null}
                    </Space>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </TableScrollRegion>
      )}
      <Modal
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        closable={!deleting}
        confirmLoading={deleting}
        mask={{ closable: false }}
        okButtonProps={{ danger: true }}
        okText={
          deleteError
            ? t('workflowActivityVNext.workflows.deleteRetry', 'Try again')
            : t('workflowActivityVNext.workflows.deleteDraft', 'Delete draft')
        }
        onCancel={closeDelete}
        onOk={() => void confirmDelete()}
        open={Boolean(deleteTarget)}
        title={t(
          'workflowActivityVNext.workflows.deleteTitle',
          'Delete editable draft?',
        )}
      >
        <p>
          {t(
            'workflowActivityVNext.workflows.deleteDescription',
            'This deletes only the editable draft. Published versions and run history remain available.',
          )}
        </p>
        {deleteError ? (
          <Alert
            description={<TechnicalDetails>{deleteError}</TechnicalDetails>}
            message={
              deleteSucceeded
                ? t(
                    'workflowActivityVNext.workflows.deleteRefreshFailed',
                    "Draft was deleted, but workflows couldn't refresh",
                  )
                : t(
                    'workflowActivityVNext.workflows.deleteFailed',
                    "Draft couldn't be deleted",
                  )
            }
            showIcon
            type="error"
          />
        ) : null}
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowsPage;
