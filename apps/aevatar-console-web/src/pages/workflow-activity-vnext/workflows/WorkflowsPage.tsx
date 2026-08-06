import {
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  MoreOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Button, Dropdown, Input, Modal, Select, Space, Tooltip } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import { t } from '@/shared/i18n/messages';
import type { ScopeWorkflowSummary } from '@/shared/models/scopes';
import { history } from '@/shared/navigation/history';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import type { StudioWorkflowDraftSummary } from '@/shared/studio/models';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';

type WorkflowRow = {
  readonly activeRevisionId: string;
  readonly description: string;
  readonly hasCommittedSource: boolean;
  readonly hasDraftSource: boolean;
  readonly name: string;
  readonly ownershipLabel: string;
  readonly stepCount?: number;
  readonly updatedAtUtc: string | null;
  readonly workflowId: string;
};

function toDraftRow(
  item: StudioWorkflowDraftSummary,
  committed?: WorkflowRow,
): WorkflowRow {
  return {
    activeRevisionId:
      item.activeRevisionId?.trim() || committed?.activeRevisionId || '',
    description: item.description,
    hasCommittedSource: Boolean(committed),
    hasDraftSource: true,
    name: item.name,
    ownershipLabel:
      item.directoryLabel.trim() ||
      t('workflowActivityVNext.workflows.workspaceOwner', 'Workspace'),
    stepCount: item.stepCount,
    updatedAtUtc: item.updatedAtUtc,
    workflowId: item.workflowId,
  };
}

function toCommittedRow(item: ScopeWorkflowSummary): WorkflowRow {
  return {
    activeRevisionId: item.activeRevisionId.trim(),
    description: '',
    hasCommittedSource: true,
    hasDraftSource: false,
    name: item.displayName || item.workflowName,
    ownershipLabel: t(
      'workflowActivityVNext.workflows.workspaceOwner',
      'Workspace',
    ),
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

function normalizeWorkflowName(value: string): string {
  return value.trim().toLocaleLowerCase();
}

const WorkflowsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const toast = useConsoleToast();
  const lastListErrorSignature = React.useRef('');
  const suppressNextDraftListError = React.useRef(false);
  const initialParams = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const [query, setQuery] = React.useState(initialParams.get('q') ?? '');
  const [view, setView] = React.useState<WorkflowView>(
    readWorkflowView(initialParams),
  );
  const [renameTarget, setRenameTarget] = React.useState<WorkflowRow | null>(
    null,
  );
  const [renameName, setRenameName] = React.useState('');
  const [renaming, setRenaming] = React.useState(false);
  const [deleteTarget, setDeleteTarget] = React.useState<WorkflowRow | null>(
    null,
  );
  const [deleteFailed, setDeleteFailed] = React.useState(false);
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
  const renameDuplicates = React.useMemo(() => {
    if (!renameTarget) return false;
    const normalizedName = normalizeWorkflowName(renameName);
    return Boolean(
      normalizedName &&
        rows.some(
          (row) =>
            row.workflowId !== renameTarget.workflowId &&
            normalizeWorkflowName(row.name) === normalizedName,
        ),
    );
  }, [renameName, renameTarget, rows]);

  React.useEffect(() => {
    if (loading || (!drafts.isError && !committed.isError)) return;

    const errorSignature = [
      scopeId,
      drafts.isError ? drafts.errorUpdatedAt : 0,
      committed.isError ? committed.errorUpdatedAt : 0,
    ].join(':');
    if (lastListErrorSignature.current === errorSignature) return;

    lastListErrorSignature.current = errorSignature;
    if (suppressNextDraftListError.current && drafts.isError) {
      suppressNextDraftListError.current = false;
      return;
    }

    toast.error(
      t(
        'workflowActivityVNext.workflows.partialUnavailable',
        "Some workflows couldn't be loaded",
      ),
    );
  }, [
    committed.errorUpdatedAt,
    committed.isError,
    drafts.errorUpdatedAt,
    drafts.isError,
    loading,
    scopeId,
    toast,
  ]);

  const retry = () => {
    void drafts.refetch();
    void committed.refetch();
  };

  const openActivity = (row: WorkflowRow) => {
    const activityHref = buildWorkflowActivitySectionHref(scopeId, 'activity');
    history.push(
      `${activityHref}?workflowId=${encodeURIComponent(row.workflowId)}`,
    );
  };

  const copyWorkflowReference = async (row: WorkflowRow) => {
    try {
      if (!navigator.clipboard?.writeText)
        throw new Error('Clipboard unavailable');
      await navigator.clipboard.writeText(row.workflowId);
      toast.success(
        t(
          'workflowActivityVNext.workflows.referenceCopied',
          'Workflow reference copied',
        ),
      );
    } catch {
      toast.error(
        t(
          'workflowActivityVNext.workflows.referenceCopyFailed',
          "Workflow reference couldn't be copied",
        ),
      );
    }
  };

  const openRename = (row: WorkflowRow) => {
    setRenameTarget(row);
    setRenameName(row.name);
  };

  const closeRename = () => {
    if (renaming) return;
    setRenameTarget(null);
    setRenameName('');
  };

  const confirmRename = async () => {
    const workflowName = renameName.trim();
    if (!renameTarget || !workflowName || renaming) return;
    setRenaming(true);
    try {
      const draft = await studioApi.getWorkflowDraft(
        renameTarget.workflowId,
        scopeId,
      );
      await studioApi.updateWorkflowDraft({
        directoryId: draft.directoryId,
        fileName: draft.fileName,
        layout: draft.layout,
        scopeId,
        workflowId: renameTarget.workflowId,
        workflowName,
        yaml: draft.yaml,
      });
      const refreshed = await drafts.refetch();
      if (refreshed.isError) throw refreshed.error;
      setRenameTarget(null);
      setRenameName('');
      toast.success(
        t('workflowActivityVNext.workflows.renameSuccess', 'Workflow renamed'),
      );
    } catch {
      toast.error(
        t(
          'workflowActivityVNext.workflows.renameFailed',
          "Workflow couldn't be renamed",
        ),
      );
    } finally {
      setRenaming(false);
    }
  };

  const closeDelete = () => {
    if (deleting) return;
    setDeleteTarget(null);
    setDeleteFailed(false);
    setDeleteSucceeded(false);
  };

  const confirmDelete = async () => {
    if (!deleteTarget || deleting) return;
    setDeleting(true);
    setDeleteFailed(false);
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

      suppressNextDraftListError.current = true;
      const refreshed = await drafts.refetch();
      if (!refreshed.isError) suppressNextDraftListError.current = false;
      if (refreshed.isError) throw refreshed.error;
      setDeleteTarget(null);
      setDeleteSucceeded(false);
    } catch {
      toast.error(
        removed
          ? t(
              'workflowActivityVNext.workflows.deleteRefreshFailed',
              "Draft was deleted, but workflows couldn't refresh",
            )
          : t(
              'workflowActivityVNext.workflows.deleteFailed',
              "Draft couldn't be deleted",
            ),
      );
      setDeleteFailed(true);
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
                    <span className="wa-vnext__workflow-context">
                      <span
                        className={`wa-vnext__status ${
                          row.activeRevisionId
                            ? 'wa-vnext__status--committed'
                            : 'wa-vnext__status--draft'
                        }`}
                      >
                        {row.activeRevisionId
                          ? t(
                              'workflowActivityVNext.workflows.publishedRevision',
                              'Published {revision}',
                              { revision: row.activeRevisionId },
                            )
                          : t(
                              'workflowActivityVNext.workflows.draftStatus',
                              'Draft',
                            )}
                      </span>
                      <span aria-hidden="true">·</span>
                      <span>{row.ownershipLabel}</span>
                      <span aria-hidden="true">·</span>
                      <span>
                        {t(
                          'workflowActivityVNext.workflows.updatedContext',
                          'Updated {updatedAt}',
                          { updatedAt: formatDate(row.updatedAtUtc) },
                        )}
                      </span>
                    </span>
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
                      <Button onClick={() => openActivity(row)}>
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
                              setDeleteFailed(false);
                              setDeleteSucceeded(false);
                            }}
                            type="text"
                          />
                        </Tooltip>
                      ) : null}
                      <Dropdown
                        menu={{
                          items: [
                            ...(row.hasDraftSource
                              ? [
                                  {
                                    icon: <EditOutlined />,
                                    key: 'rename',
                                    label: t(
                                      'workflowActivityVNext.workflows.rename',
                                      'Rename',
                                    ),
                                  },
                                ]
                              : []),
                            {
                              icon: <CopyOutlined />,
                              key: 'copy-reference',
                              label: t(
                                'workflowActivityVNext.workflows.copyReference',
                                'Copy workflow reference',
                              ),
                            },
                          ],
                          onClick: ({ key }) => {
                            if (key === 'rename') openRename(row);
                            if (key === 'copy-reference')
                              void copyWorkflowReference(row);
                          },
                        }}
                        placement="bottomRight"
                        trigger={['click']}
                      >
                        <Button
                          aria-label={t(
                            'workflowActivityVNext.workflows.moreActionsAria',
                            'More actions for {name}',
                            { name: row.name },
                          )}
                          icon={<MoreOutlined />}
                          type="text"
                        />
                      </Dropdown>
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
        closable={!renaming}
        confirmLoading={renaming}
        mask={{ closable: false }}
        okButtonProps={{ disabled: !renameName.trim() }}
        okText={t('workflowActivityVNext.workflows.renameSave', 'Save name')}
        onCancel={closeRename}
        onOk={() => void confirmRename()}
        open={Boolean(renameTarget)}
        title={t(
          'workflowActivityVNext.workflows.renameTitle',
          'Rename workflow',
        )}
      >
        <div className="wa-vnext__modal-field">
          <label htmlFor="wa-vnext-rename-name">
            {t('workflowActivityVNext.new.name', 'Workflow name')}
          </label>
          <Input
            aria-label={t('workflowActivityVNext.new.name', 'Workflow name')}
            autoFocus
            id="wa-vnext-rename-name"
            onChange={(event) => setRenameName(event.target.value)}
            value={renameName}
          />
        </div>
        {renameDuplicates ? (
          <p className="wa-vnext__duplicate-warning" role="status">
            {t(
              'workflowActivityVNext.workflows.duplicateNameWarning',
              'Another workflow already uses this name. Duplicate names are allowed.',
            )}
          </p>
        ) : null}
      </Modal>
      <Modal
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        closable={!deleting}
        confirmLoading={deleting}
        mask={{ closable: false }}
        okButtonProps={{ danger: true }}
        okText={
          deleteFailed
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
      </Modal>
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowsPage;
