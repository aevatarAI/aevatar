import {
  CalendarOutlined,
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  HistoryOutlined,
  InboxOutlined,
  MoreOutlined,
  PlusOutlined,
  ReloadOutlined,
  SearchOutlined,
} from '@ant-design/icons';
import { useInfiniteQuery } from '@tanstack/react-query';
import { Button, Dropdown, Input, Modal, Popover, Select, Space } from 'antd';
import React from 'react';
import { scopesApi } from '@/shared/api/scopesApi';
import { t } from '@/shared/i18n/messages';
import type {
  ScopeWorkflowCatalogueRow,
  ScopeWorkflowCatalogueRowCapabilities,
  ScopeWorkflowCatalogueView,
} from '@/shared/models/scopes';
import { history } from '@/shared/navigation/history';
import { isStudioApiStatus, studioApi } from '@/shared/studio/api';
import AevatarContentSkeleton from '@/shared/ui/AevatarContentSkeleton';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from '@/shared/ui/interactionStandards';
import { useConsoleLocation } from '../hooks/useConsoleLocation';
import { observeDraftMaterialization } from '../hooks/useDraftMaterialization';
import {
  buildWorkflowActivityEditorHref,
  buildWorkflowActivityNewHref,
  buildWorkflowActivitySectionHref,
} from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import WorkflowActivityVNextShell from '../WorkflowActivityVNextShell';
import WorkflowScheduleSurface from './WorkflowScheduleSurface';
import {
  canArchiveWorkflow,
  isWorkflowArchived,
  observeWorkflowArchival,
} from './workflowArchival';
import { observeWorkflowRemoval } from './workflowRemoval';

type WorkflowRow = {
  readonly activeRevisionId: string;
  readonly deploymentId: string;
  readonly deploymentStatus: string;
  readonly description: string;
  readonly capabilities: ScopeWorkflowCatalogueRowCapabilities;
  readonly hasCommittedSource: boolean;
  readonly name: string;
  readonly ownershipLabel: string;
  readonly updatedAtUtc: string | null;
  readonly workflowId: string;
};

function toWorkflowRow(item: ScopeWorkflowCatalogueRow): WorkflowRow {
  return {
    activeRevisionId: item.committed?.activeRevisionId.trim() ?? '',
    capabilities: item.capabilities,
    description: item.description,
    deploymentId: item.committed?.deploymentId.trim() ?? '',
    deploymentStatus: item.committed?.deploymentStatus.trim() ?? '',
    hasCommittedSource: item.hasCommittedSource,
    name: item.name,
    ownershipLabel: t(
      'workflowActivityVNext.workflows.workspaceOwner',
      'Workspace',
    ),
    updatedAtUtc: item.updatedAtUtc,
    workflowId: item.workflowId,
  };
}

async function readWorkflowCatalogueMatch(
  scopeId: string,
  view: ScopeWorkflowCatalogueView,
  workflowId: string,
): Promise<readonly WorkflowRow[]> {
  let cursor: string | undefined;
  const visitedCursors = new Set<string>();

  do {
    const response = await scopesApi.queryWorkflowCatalogue({
      scopeId,
      view,
      query: workflowId,
      cursor,
      take: 100,
    });
    const target = response.items.find(
      (item) => item.workflowId === workflowId,
    );
    if (target) return [toWorkflowRow(target)];

    const nextCursor = response.nextPageToken ?? undefined;
    if (!nextCursor || visitedCursors.has(nextCursor)) return [];
    visitedCursors.add(nextCursor);
    cursor = nextCursor;
  } while (cursor);

  return [];
}

function readWorkflowView(params: URLSearchParams): ScopeWorkflowCatalogueView {
  const view = params.get('view');
  if (view === 'archived') return 'archived';
  return view === 'drafts' ? 'drafts' : 'all';
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

function handleClientLinkClick(
  event: React.MouseEvent<HTMLElement>,
  href: string,
): void {
  if (
    event.button !== 0 ||
    event.altKey ||
    event.ctrlKey ||
    event.metaKey ||
    event.shiftKey
  )
    return;

  event.preventDefault();
  history.push(href);
}

const WorkflowsPage: React.FC<{ readonly scopeId: string }> = ({ scopeId }) => {
  const location = useConsoleLocation();
  const toast = useConsoleToast();
  const lastListErrorSignature = React.useRef('');
  const selfAuthoredSearch = React.useRef<string | null>(null);
  const initialParams = React.useMemo(
    () => new URLSearchParams(location.search),
    [location.search],
  );
  const [query, setQuery] = React.useState(initialParams.get('q') ?? '');
  const [debouncedQuery, setDebouncedQuery] = React.useState(query.trim());
  const [view, setView] = React.useState<ScopeWorkflowCatalogueView>(
    readWorkflowView(initialParams),
  );
  const [archiveTarget, setArchiveTarget] = React.useState<WorkflowRow | null>(
    null,
  );
  const [archiving, setArchiving] = React.useState(false);
  const [archiveSubmitted, setArchiveSubmitted] = React.useState(false);
  const [archivePhase, setArchivePhase] = React.useState<
    'delayed' | 'failed' | 'idle'
  >('idle');
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
  const [scheduleTarget, setScheduleTarget] =
    React.useState<WorkflowRow | null>(null);
  const catalogue = useInfiniteQuery({
    queryKey: [
      'workflow-activity-vnext',
      'workflow-catalogue',
      scopeId,
      view,
      debouncedQuery,
    ],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) =>
      scopesApi.queryWorkflowCatalogue(
        {
          scopeId,
          view,
          query: debouncedQuery || undefined,
          cursor: pageParam,
          take: 50,
        },
        signal,
      ),
    getNextPageParam: (lastPage) => lastPage.nextPageToken ?? undefined,
    refetchOnMount: 'always',
    retry: false,
  });

  React.useEffect(() => {
    if (selfAuthoredSearch.current === location.search) {
      selfAuthoredSearch.current = null;
      return;
    }
    selfAuthoredSearch.current = null;
    const params = new URLSearchParams(location.search);
    const routeQuery = params.get('q') ?? '';
    setQuery(routeQuery);
    setDebouncedQuery(routeQuery.trim());
    setView(readWorkflowView(params));
  }, [location.search]);

  React.useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedQuery(query.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [query]);

  React.useEffect(() => {
    const params = new URLSearchParams();
    if (query.trim()) params.set('q', query.trim());
    if (view !== 'all') params.set('view', view);
    const suffix = params.toString();
    const nextSearch = suffix ? `?${suffix}` : '';
    if (nextSearch === location.search) return;
    selfAuthoredSearch.current = nextSearch;
    history.replace(`${location.pathname}${nextSearch}`);
  }, [location.pathname, location.search, query, view]);

  const loading =
    catalogue.isPending ||
    (catalogue.isFetching && !catalogue.isFetchingNextPage);
  const rows = React.useMemo(() => {
    return (catalogue.data?.pages ?? []).flatMap((page) =>
      page.items
        .filter(
          (item) =>
            view !== 'drafts' ||
            (item.hasDraftSource && !item.committed?.activeRevisionId.trim()),
        )
        .map(toWorkflowRow),
    );
  }, [catalogue.data?.pages, view]);
  const filtersActive = Boolean(query.trim()) || view !== 'all';
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
    if (loading || !catalogue.isLoadingError) return;

    const errorSignature = [scopeId, catalogue.errorUpdatedAt].join(':');
    if (lastListErrorSignature.current === errorSignature) return;

    lastListErrorSignature.current = errorSignature;
    toast.error(
      t('workflowActivityVNext.workflows.unavailable', 'Workflows unavailable'),
    );
  }, [
    catalogue.errorUpdatedAt,
    catalogue.isLoadingError,
    loading,
    scopeId,
    toast,
  ]);

  const retry = () => {
    void catalogue.refetch();
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
      const parsed = await studioApi.parseYaml({ yaml: draft.yaml });
      if (!parsed.document)
        throw new Error('Workflow YAML could not be parsed');
      const serialized = await studioApi.serializeYaml({
        document: {
          ...parsed.document,
          name: workflowName,
        },
      });
      await studioApi.updateWorkflowDraft({
        directoryId: draft.directoryId,
        fileName: draft.fileName,
        layout: draft.layout,
        scopeId,
        workflowId: renameTarget.workflowId,
        workflowName,
        yaml: serialized.yaml,
      });
      const observation = await observeDraftMaterialization({
        workflowId: renameTarget.workflowId,
        read: (workflowId) => studioApi.getWorkflowDraft(workflowId, scopeId),
        isNotFound: (candidate) => isStudioApiStatus(candidate, 404),
        isObserved: (candidate) => candidate.name.trim() === workflowName,
      });
      if (observation.kind === 'delayed') {
        throw new Error('Workflow rename was not observed');
      }
      const refreshed = await catalogue.refetch();
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

  const openArchive = (row: WorkflowRow) => {
    setArchiveTarget(row);
    setArchiveSubmitted(false);
    setArchivePhase('idle');
  };

  const closeArchive = () => {
    if (archiving) return;
    setArchiveTarget(null);
    setArchiveSubmitted(false);
    setArchivePhase('idle');
  };

  const confirmArchive = async () => {
    if (!archiveTarget || archiving) return;
    const target = archiveTarget;
    let accepted = archiveSubmitted;
    setArchiving(true);
    setArchivePhase('idle');

    try {
      if (!accepted) {
        await scopesApi.archiveWorkflow(scopeId, target.workflowId);
        accepted = true;
        setArchiveSubmitted(true);
      }

      const observation = await observeWorkflowArchival({
        readWorkflows: () =>
          readWorkflowCatalogueMatch(scopeId, 'archived', target.workflowId),
        workflowId: target.workflowId,
      });
      if (observation.kind === 'delayed') {
        setArchivePhase('delayed');
        return;
      }

      await catalogue.refetch();
      setArchiveTarget(null);
      setArchiveSubmitted(false);
      setArchivePhase('idle');
      toast.success(
        t(
          'workflowActivityVNext.workflows.archiveSuccess',
          'Workflow archived',
        ),
      );
    } catch {
      if (accepted) {
        setArchivePhase('delayed');
      } else {
        setArchivePhase('failed');
        toast.error(
          t(
            'workflowActivityVNext.workflows.archiveFailed',
            "Workflow couldn't be archived",
          ),
        );
      }
    } finally {
      setArchiving(false);
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

      const observation = await observeWorkflowRemoval({
        readWorkflows: () =>
          readWorkflowCatalogueMatch(scopeId, 'all', deleteTarget.workflowId),
        workflowId: deleteTarget.workflowId,
      });
      if (observation.kind === 'delayed') {
        toast.error(
          t(
            'workflowActivityVNext.workflows.deleteObservationDelayed',
            'Draft was deleted, but the workflow catalogue has not confirmed its removal yet',
          ),
        );
        setDeleteFailed(true);
        setDeleteSucceeded(true);
        return;
      }

      const refreshed = await catalogue.refetch();
      if (refreshed.isError) throw refreshed.error;
      setDeleteTarget(null);
      setDeleteSucceeded(false);
    } catch {
      toast.error(
        removed
          ? t(
              'workflowActivityVNext.workflows.deleteRefreshFailed',
              'Draft was deleted, but the workflow list could not refresh. Please try again.',
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
          maxLength={
            catalogue.data?.pages[0]?.search.maximumQueryLength ?? undefined
          }
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
                label: t(
                  'workflowActivityVNext.workflows.draftsView',
                  'Drafts',
                ),
                value: 'drafts',
              },
              {
                label: t(
                  'workflowActivityVNext.workflows.archivedView',
                  'Show archived workflows',
                ),
                value: 'archived',
              },
            ]}
            virtual={false}
            value={view}
          />
        </Space>
      </div>

      {loading ? (
        <AevatarContentSkeleton
          ariaLabel={t(
            'workflowActivityVNext.workflows.loading',
            'Loading workflows',
          )}
          columnWidths={['minmax(240px, 1fr)', 120, 190, 270]}
          rows={4}
          tableMinWidth={900}
          variant="table"
        />
      ) : catalogue.isLoadingError ? (
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
          <table className="wa-vnext__table wa-vnext__table--workflow-catalogue">
            <colgroup>
              <col />
              <col style={{ width: '120px' }} />
              <col style={{ width: '190px' }} />
              <col style={{ width: '500px' }} />
            </colgroup>
            <thead>
              <tr>
                <th>
                  {t(
                    'workflowActivityVNext.workflows.columnWorkflow',
                    'Workflow',
                  )}
                </th>
                <th className="wa-vnext__table-column--status">
                  {t('workflowActivityVNext.workflows.columnStatus', 'Status')}
                </th>
                <th className="wa-vnext__table-column--updated">
                  {t(
                    'workflowActivityVNext.workflows.columnUpdated',
                    'Last updated',
                  )}
                </th>
                <th className="wa-vnext__table-column--actions wa-vnext__workflow-actions-cell">
                  {t(
                    'workflowActivityVNext.workflows.columnActions',
                    'Actions',
                  )}
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const editorHref = buildWorkflowActivityEditorHref(
                  scopeId,
                  row.workflowId,
                );
                const activityHref = `${buildWorkflowActivitySectionHref(
                  scopeId,
                  'activity',
                )}?workflowId=${encodeURIComponent(row.workflowId)}`;
                const description = row.description.trim();
                const isArchived = isWorkflowArchived(row);
                const isPublished = Boolean(row.activeRevisionId);
                const canDeleteDraft =
                  row.capabilities.delete.available && !isPublished;
                const workflowName = (
                  <span className="wa-vnext__title">{row.name}</span>
                );

                return (
                  <tr key={row.workflowId}>
                    <td
                      data-label={t(
                        'workflowActivityVNext.workflows.columnWorkflow',
                        'Workflow',
                      )}
                    >
                      {description ? (
                        <Popover
                          classNames={{
                            container: 'wa-vnext__workflow-description-popover',
                          }}
                          content={
                            <p className="wa-vnext__workflow-description">
                              {description}
                            </p>
                          }
                          destroyOnHidden
                          placement="bottomLeft"
                          trigger={['hover', 'focus']}
                        >
                          <button
                            aria-label={t(
                              'workflowActivityVNext.workflows.descriptionAria',
                              'Description for {name}',
                              { name: row.name },
                            )}
                            className="wa-vnext__workflow-name-trigger"
                            type="button"
                          >
                            {workflowName}
                          </button>
                        </Popover>
                      ) : (
                        workflowName
                      )}
                    </td>
                    <td
                      data-label={t(
                        'workflowActivityVNext.workflows.columnStatus',
                        'Status',
                      )}
                    >
                      <span
                        className={`wa-vnext__status ${
                          isArchived
                            ? 'wa-vnext__status--archived'
                            : isPublished
                              ? 'wa-vnext__status--committed'
                              : 'wa-vnext__status--draft'
                        }`}
                      >
                        {isArchived
                          ? t(
                              'workflowActivityVNext.workflows.archivedStatus',
                              'Archived',
                            )
                          : isPublished
                            ? t(
                                'workflowActivityVNext.workflows.publishedStatus',
                                'Published',
                              )
                            : t(
                                'workflowActivityVNext.workflows.draftStatus',
                                'Draft',
                              )}
                      </span>
                    </td>
                    <td
                      data-label={t(
                        'workflowActivityVNext.workflows.columnUpdated',
                        'Last updated',
                      )}
                    >
                      {formatDate(row.updatedAtUtc)}
                    </td>
                    <td
                      className="wa-vnext__workflow-actions-cell"
                      data-label={t(
                        'workflowActivityVNext.workflows.columnActions',
                        'Actions',
                      )}
                    >
                      <Space className="wa-vnext__workflow-actions" size={6}>
                        <Button
                          aria-label={t(
                            'workflowActivityVNext.workflows.openAria',
                            'Open {name} in {owner}',
                            { name: row.name, owner: row.ownershipLabel },
                          )}
                          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                          disabled={!row.capabilities.open.available}
                          href={
                            row.capabilities.open.available
                              ? editorHref
                              : undefined
                          }
                          icon={<EditOutlined />}
                          onClick={
                            row.capabilities.open.available
                              ? (event) =>
                                  handleClientLinkClick(event, editorHref)
                              : undefined
                          }
                          type="primary"
                        >
                          {t('workflowActivityVNext.common.open', 'Open')}
                        </Button>
                        <Button
                          aria-label={t(
                            'workflowActivityVNext.workflows.viewActivityAria',
                            'View activity for {name} in {owner}',
                            { name: row.name, owner: row.ownershipLabel },
                          )}
                          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                          disabled={!row.capabilities.activity.available}
                          href={
                            row.capabilities.activity.available
                              ? activityHref
                              : undefined
                          }
                          icon={<HistoryOutlined />}
                          onClick={
                            row.capabilities.activity.available
                              ? (event) =>
                                  handleClientLinkClick(event, activityHref)
                              : undefined
                          }
                        >
                          {t(
                            'workflowActivityVNext.workflows.viewActivity',
                            'View activity',
                          )}
                        </Button>
                        {isPublished && !isArchived ? (
                          <Button
                            aria-label={t(
                              'workflowActivityVNext.schedule.openAria',
                              'Manage schedules for {name}',
                              { name: row.name },
                            )}
                            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                            icon={<CalendarOutlined />}
                            onClick={() => setScheduleTarget(row)}
                          >
                            {t(
                              'workflowActivityVNext.schedule.open',
                              'Schedule',
                            )}
                          </Button>
                        ) : null}
                        <Dropdown
                          menu={{
                            items: [
                              ...(row.capabilities.rename.available
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
                              ...(canDeleteDraft || canArchiveWorkflow(row)
                                ? [{ type: 'divider' as const }]
                                : []),
                              ...(canDeleteDraft
                                ? [
                                    {
                                      danger: true,
                                      icon: <DeleteOutlined />,
                                      key: 'delete',
                                      label: t(
                                        'workflowActivityVNext.workflows.deleteDraft',
                                        'Delete draft',
                                      ),
                                    },
                                  ]
                                : []),
                              ...(canArchiveWorkflow(row)
                                ? [
                                    {
                                      danger: true,
                                      icon: <InboxOutlined />,
                                      key: 'archive',
                                      label: t(
                                        'workflowActivityVNext.workflows.archive',
                                        'Archive',
                                      ),
                                    },
                                  ]
                                : []),
                            ],
                            onClick: ({ key }) => {
                              if (key === 'rename') openRename(row);
                              if (key === 'copy-reference')
                                void copyWorkflowReference(row);
                              if (key === 'delete') {
                                setDeleteTarget(row);
                                setDeleteFailed(false);
                                setDeleteSucceeded(false);
                              }
                              if (key === 'archive') openArchive(row);
                            },
                          }}
                          placement="bottomRight"
                          trigger={['click']}
                        >
                          <Button
                            aria-label={t(
                              'workflowActivityVNext.workflows.moreActionsAria',
                              'More actions for {name} in {owner}',
                              { name: row.name, owner: row.ownershipLabel },
                            )}
                            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                            icon={<MoreOutlined />}
                          />
                        </Dropdown>
                      </Space>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </TableScrollRegion>
      )}
      {catalogue.hasNextPage ? (
        <div className="wa-vnext__pagination-actions">
          {catalogue.isFetchNextPageError ? (
            <p role="alert">
              {t(
                'workflowActivityVNext.workflows.loadMoreFailed',
                "More workflows couldn't be loaded",
              )}
            </p>
          ) : null}
          <Button
            loading={catalogue.isFetchingNextPage}
            onClick={() => void catalogue.fetchNextPage()}
          >
            {t('workflowActivityVNext.workflows.loadMore', 'Load more')}
          </Button>
        </div>
      ) : null}
      <Modal
        cancelButtonProps={{ disabled: archiving }}
        cancelText={t('workflowActivityVNext.common.cancel', 'Cancel')}
        closable={!archiving}
        confirmLoading={archiving}
        destroyOnHidden
        keyboard={!archiving}
        mask={{ closable: false }}
        okButtonProps={{ danger: true }}
        okText={
          archivePhase === 'delayed'
            ? t(
                'workflowActivityVNext.workflows.archiveCheckAgain',
                'Check again',
              )
            : archivePhase === 'failed'
              ? t(
                  'workflowActivityVNext.workflows.archiveTryAgain',
                  'Try again',
                )
              : t(
                  'workflowActivityVNext.workflows.archiveConfirm',
                  'Archive workflow',
                )
        }
        onCancel={closeArchive}
        onOk={() => void confirmArchive()}
        open={Boolean(archiveTarget)}
        title={t(
          'workflowActivityVNext.workflows.archiveTitle',
          'Archive this workflow?',
        )}
      >
        <p>
          {t(
            'workflowActivityVNext.workflows.archiveDescription',
            'This stops new runs for the published workflow. Its editable draft, published revisions, and Activity history remain available. Publishing it again restores it.',
          )}
        </p>
        {archivePhase === 'failed' ? (
          <p className="wa-vnext__duplicate-warning" role="alert">
            {t(
              'workflowActivityVNext.workflows.archiveFailed',
              "Workflow couldn't be archived",
            )}
          </p>
        ) : archivePhase === 'delayed' ? (
          <p className="wa-vnext__duplicate-warning" role="status">
            {t(
              'workflowActivityVNext.workflows.archiveDelayed',
              "Archive was accepted, but it hasn't been confirmed yet",
            )}
          </p>
        ) : null}
      </Modal>
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
      <WorkflowScheduleSurface
        available={Boolean(scheduleTarget?.activeRevisionId)}
        initialView="list"
        mode="modal"
        onClose={() => setScheduleTarget(null)}
        open={Boolean(scheduleTarget)}
        scopeId={scopeId}
        workflowId={scheduleTarget?.workflowId ?? ''}
        workflowName={scheduleTarget?.name ?? ''}
      />
    </WorkflowActivityVNextShell>
  );
};

export default WorkflowsPage;
