import {
  CalendarOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  ProfileOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { MarkerType } from '@xyflow/react';
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  Pagination,
  Select,
  Space,
  Typography,
} from 'antd';
import React from 'react';
import { runtimeCatalogApi } from '@/shared/api/runtimeCatalogApi';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowTemplateDetail,
  WorkflowTemplateListResponse,
  WorkflowTemplateSummary,
} from '@/shared/models/runtime/workflowTemplates';
import { history } from '@/shared/navigation/history';
import { isStudioApiErrorCode, studioApi } from '@/shared/studio/api';
import {
  buildStudioGraphElements,
  formatStudioStepTypeLabel,
  type StudioGraphElements,
} from '@/shared/studio/graph';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useDraftMaterialization } from '../hooks/useDraftMaterialization';
import { buildWorkflowActivityEditorHref } from '../navigation';
import TableScrollRegion from '../TableScrollRegion';
import TechnicalDetails from '../TechnicalDetails';

const TEMPLATE_PAGE_SIZE = 12;

type WorkflowTemplateBrowserProps = {
  readonly scopeId: string;
};

type PreviewEdge = StudioGraphElements['edges'][number];

interface TemplatePaginationState {
  readonly filterKey: string;
  readonly page: number;
  readonly cursors: readonly (string | undefined)[];
}

type TemplateFailure = {
  readonly message: string;
  readonly surface: 'browser' | 'modal';
  readonly stale: boolean;
  readonly templateId: string;
};

function createTemplatePaginationState(
  filterKey: string,
): TemplatePaginationState {
  return { filterKey, page: 1, cursors: [undefined] };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function hasStatus(error: unknown, status: number): boolean {
  return Boolean(
    error &&
      typeof error === 'object' &&
      'status' in error &&
      (error as { status?: unknown }).status === status,
  );
}

function formatConnections(template: WorkflowTemplateSummary): string {
  const connections = [...template.requiredConnections];
  if (template.requiresLlmProvider) {
    connections.unshift(
      t(
        'workflowActivityVNext.new.templateBrowser.llmProvider',
        'LLM provider',
      ),
    );
  }
  return connections.length > 0
    ? connections.join(', ')
    : t('workflowActivityVNext.new.templateBrowser.none', 'None');
}

function formatUpdatedAt(template: WorkflowTemplateSummary): string {
  const timestamp = Date.parse(template.freshness.projectionWatermark);
  if (!Number.isFinite(timestamp))
    return template.freshness.projectionWatermark;
  const date = new Date(timestamp);
  const month = String(date.getUTCMonth() + 1).padStart(2, '0');
  const day = String(date.getUTCDate()).padStart(2, '0');
  return `${date.getUTCFullYear()}/${month}/${day}`;
}

function TemplateStepTooltip({
  detail,
  hasRequestedDetails,
  isError,
  isLoading,
  stepCount,
}: {
  readonly detail?: WorkflowTemplateDetail;
  readonly hasRequestedDetails: boolean;
  readonly isError: boolean;
  readonly isLoading: boolean;
  readonly stepCount: number;
}) {
  const steps = detail?.definition.steps ?? [];

  return (
    <div className="wa-vnext__template-step-tooltip">
      <strong>
        {t(
          'workflowActivityVNext.new.templateBrowser.stepsTooltipTitle',
          'Workflow steps ({count})',
          { count: detail?.definition.steps.length ?? stepCount },
        )}
      </strong>
      {!hasRequestedDetails || isLoading ? (
        <span>
          {t(
            'workflowActivityVNext.new.templateBrowser.stepsTooltipLoading',
            'Loading step details…',
          )}
        </span>
      ) : isError ? (
        <span>
          {t(
            'workflowActivityVNext.new.templateBrowser.stepsTooltipUnavailable',
            'Step details are unavailable. Open View to inspect this template.',
          )}
        </span>
      ) : steps.length === 0 ? (
        <span>
          {t(
            'workflowActivityVNext.new.templateBrowser.stepsTooltipEmpty',
            'No workflow steps are exposed for this template.',
          )}
        </span>
      ) : (
        <ol>
          {steps.map((step) => (
            <li key={step.id}>
              <span>{step.id}</span>
              <small>{formatStudioStepTypeLabel(step.type)}</small>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}

function TemplateRow({
  creating,
  disabled,
  onCreate,
  onView,
  template,
}: {
  readonly creating: boolean;
  readonly disabled: boolean;
  readonly onCreate: (template: WorkflowTemplateSummary) => void;
  readonly onView: (templateId: string) => void;
  readonly template: WorkflowTemplateSummary;
}) {
  const [stepTooltipOpen, setStepTooltipOpen] = React.useState(false);
  const stepDetailQuery = useQuery({
    enabled: stepTooltipOpen,
    queryKey: [
      'workflow-activity-vnext',
      'workflow-template',
      template.templateId,
    ],
    queryFn: () => runtimeCatalogApi.getWorkflowTemplate(template.templateId),
    retry: false,
  });
  const templateLabel = t(
    'workflowActivityVNext.new.templateBrowser.template',
    'Template',
  );
  const connectionLabel = t(
    'workflowActivityVNext.new.templateBrowser.connection',
    'Connection',
  );
  const doesLabel = t('workflowActivityVNext.new.templateBrowser.does', 'Does');
  const updatedLabel = t(
    'workflowActivityVNext.new.templateBrowser.updatedColumn',
    'Updated',
  );
  const actionsLabel = t(
    'workflowActivityVNext.new.templateBrowser.actions',
    'Actions',
  );
  const stepUnit = t(
    template.stepCount === 1
      ? 'workflowActivityVNext.new.templateBrowser.step'
      : 'workflowActivityVNext.new.templateBrowser.steps',
    template.stepCount === 1 ? 'step' : 'steps',
  );
  const stepSummary = t(
    'workflowActivityVNext.new.templateBrowser.runsSteps',
    'Runs {count} {unit}',
    { count: template.stepCount, unit: stepUnit },
  );
  return (
    <tr className="wa-vnext__template-row">
      <td className="wa-vnext__template-cell--left" data-label={templateLabel}>
        <div className="wa-vnext__template-identity">
          <div className="wa-vnext__template-copy">
            <AevatarTooltip title={template.displayName}>
              <Typography.Title level={2}>
                {template.displayName}
              </Typography.Title>
            </AevatarTooltip>
            <p>
              {template.description ||
                t(
                  'workflowActivityVNext.new.templateBrowser.fallbackDescription',
                  'A ready-made workflow for your workspace.',
                )}
            </p>
          </div>
        </div>
      </td>
      <td
        className="wa-vnext__template-cell--left wa-vnext__template-fact"
        data-label={connectionLabel}
      >
        <strong>{formatConnections(template)}</strong>
      </td>
      <td
        className="wa-vnext__template-cell--left wa-vnext__template-fact"
        data-label={doesLabel}
      >
        <AevatarTooltip
          onOpenChange={setStepTooltipOpen}
          title={
            <TemplateStepTooltip
              detail={stepDetailQuery.data}
              hasRequestedDetails={stepTooltipOpen}
              isError={stepDetailQuery.isError}
              isLoading={stepDetailQuery.isLoading}
              stepCount={template.stepCount}
            />
          }
          trigger={['hover', 'focus', 'click']}
        >
          <button className="wa-vnext__template-step-summary" type="button">
            {stepSummary}
          </button>
        </AevatarTooltip>
      </td>
      <td
        className="wa-vnext__template-cell--right wa-vnext__template-updated"
        data-label={updatedLabel}
      >
        {formatUpdatedAt(template)}
      </td>
      <td
        className="wa-vnext__template-cell--right wa-vnext__template-actions-cell"
        data-label={actionsLabel}
      >
        <div className="wa-vnext__template-actions">
          <Button
            aria-label={t(
              'workflowActivityVNext.new.templateBrowser.viewNamed',
              'View {name}',
              {
                name: template.displayName,
              },
            )}
            disabled={disabled}
            onClick={() => onView(template.templateId)}
          >
            {t('workflowActivityVNext.new.templateBrowser.view', 'View')}
          </Button>
          <Button
            aria-label={t(
              'workflowActivityVNext.new.templateBrowser.useNamed',
              'Use template {name}',
              {
                name: template.displayName,
              },
            )}
            disabled={disabled}
            loading={creating}
            onClick={() => onCreate(template)}
            type="primary"
          >
            {t('workflowActivityVNext.new.templateBrowser.use', 'Use template')}
          </Button>
        </div>
      </td>
    </tr>
  );
}

export function buildTemplatePreviewGraph(
  detail: WorkflowTemplateDetail,
): StudioGraphElements {
  const stepIds = new Set(detail.definition.steps.map((step) => step.id));
  const validEdges = detail.edges.flatMap((edge, index) =>
    stepIds.has(edge.from) && stepIds.has(edge.to) ? [{ edge, index }] : [],
  );
  if (validEdges.length === 0) {
    return buildStudioGraphElements(detail.definition);
  }

  const outgoingEdgesByStepId = new Map<string, typeof validEdges>();
  validEdges.forEach((entry) => {
    const outgoing = outgoingEdgesByStepId.get(entry.edge.from) ?? [];
    outgoing.push(entry);
    outgoingEdgesByStepId.set(entry.edge.from, outgoing);
  });

  const layoutDefinition = {
    ...detail.definition,
    steps: detail.definition.steps.map((step) => {
      const outgoing = outgoingEdgesByStepId.get(step.id) ?? [];
      return {
        ...step,
        next: outgoing[0]?.edge.to ?? '',
        branches: Object.fromEntries(
          outgoing
            .slice(1)
            .map(({ edge, index }) => [`__template_edge_${index}`, edge.to]),
        ),
      };
    }),
  };
  const baseGraph = buildStudioGraphElements(layoutDefinition);

  const sourceStepById = new Map(
    detail.definition.steps.map((step) => [step.id, step]),
  );
  const nodeIdByStepId = new Map(
    baseGraph.nodes.map((node) => [String(node.data.stepId), node.id]),
  );
  const authoritativeEdges = validEdges.map<PreviewEdge>(({ edge, index }) => {
    const source = nodeIdByStepId.get(edge.from);
    const target = nodeIdByStepId.get(edge.to);

    const label = edge.label.trim();
    const sourceStep = sourceStepById.get(edge.from);
    const isChildEdge =
      label === 'child' &&
      sourceStep?.children.some((child) => child.id === edge.to);
    const isBranchEdge = Boolean(
      !isChildEdge &&
        label &&
        (sourceStep?.branches[edge.label] ?? sourceStep?.branches[label]) ===
          edge.to,
    );
    const color = isBranchEdge ? '#8B5CF6' : '#2F6FEC';
    return {
      id: `template-edge:${index}:${edge.from}:${edge.to}`,
      source: source as string,
      target: target as string,
      type: 'smoothstep',
      label: label || undefined,
      markerEnd: {
        type: MarkerType.ArrowClosed,
        width: 11,
        height: 11,
        color,
      },
      style: { stroke: color, strokeWidth: 2.5 },
      labelStyle: label ? { fill: '#6B7280', fontSize: 12 } : undefined,
      zIndex: 4,
    };
  });
  const nodes = baseGraph.nodes.map((node) => ({
    ...node,
    data: {
      ...node.data,
      branchCount: Object.keys(
        sourceStepById.get(node.data.stepId)?.branches ?? {},
      ).length,
    },
  }));

  return {
    ...baseGraph,
    nodes,
    edges: authoritativeEdges,
  };
}

function TemplateDetailBody({
  detail,
}: {
  readonly detail: WorkflowTemplateDetail;
}) {
  const graph = React.useMemo(
    () => buildTemplatePreviewGraph(detail),
    [detail],
  );

  return (
    <div className="wa-vnext__template-detail">
      <div className="wa-vnext__template-preview-heading">
        <strong>
          {t(
            'workflowActivityVNext.new.templateBrowser.preview',
            'Workflow preview',
          )}
        </strong>
        <span>
          {t(
            'workflowActivityVNext.new.templateBrowser.previewDescription',
            '{count} {unit} and the paths between them.',
            {
              count: detail.definition.steps.length,
              unit: t(
                detail.definition.steps.length === 1
                  ? 'workflowActivityVNext.new.templateBrowser.step'
                  : 'workflowActivityVNext.new.templateBrowser.steps',
                detail.definition.steps.length === 1 ? 'step' : 'steps',
              ),
            },
          )}
        </span>
      </div>

      {graph.nodes.length > 0 ? (
        <GraphCanvas
          autoFitKey={`${detail.template.templateId}:${detail.authorityStateVersion}`}
          edges={graph.edges}
          height="clamp(300px, 48vh, 430px)"
          nodes={graph.nodes}
          variant="studio"
        />
      ) : (
        <div className="wa-vnext__template-preview-empty">
          {t(
            'workflowActivityVNext.new.templateBrowser.noPreviewSteps',
            'This template does not expose any workflow steps.',
          )}
        </div>
      )}

      <div className="wa-vnext__template-description">
        <Typography.Paragraph>
          {detail.definition.description ||
            detail.template.description ||
            t(
              'workflowActivityVNext.new.templateBrowser.fallbackDescription',
              'A ready-made workflow for your workspace.',
            )}
        </Typography.Paragraph>
        <Typography.Text type="secondary">
          {t(
            'workflowActivityVNext.new.templateBrowser.source',
            'Source: public workflow template · version {version}',
            { version: detail.authorityStateVersion },
          )}
        </Typography.Text>
      </div>
    </div>
  );
}

const WorkflowTemplateBrowser: React.FC<WorkflowTemplateBrowserProps> = ({
  scopeId,
}) => {
  const toast = useConsoleToast();
  const queryClient = useQueryClient();
  const [search, setSearch] = React.useState('');
  const [sort, setSort] = React.useState('-updated');
  const [selectedTemplateId, setSelectedTemplateId] = React.useState<
    string | null
  >(null);
  const [creatingTemplateId, setCreatingTemplateId] = React.useState<
    string | null
  >(null);
  const [failure, setFailure] = React.useState<TemplateFailure | null>(null);
  const filterKey = React.useMemo(
    () => JSON.stringify([scopeId, search.trim(), sort]),
    [scopeId, search, sort],
  );
  const [pagination, setPagination] = React.useState<TemplatePaginationState>(
    () => createTemplatePaginationState(filterKey),
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
      : createTemplatePaginationState(filterKey);
  const currentCursor = activePagination.cursors[activePagination.page - 1];

  React.useEffect(() => {
    if (pagination.filterKey === filterKey) return;
    navigationRequestId.current += 1;
    setIsResolvingPage(false);
    setPageNavigationError(null);
    setPendingPage(null);
    setPagination(createTemplatePaginationState(filterKey));
  }, [filterKey, pagination.filterKey]);

  const templateQueryKey = React.useCallback(
    (cursor: string | undefined) =>
      [
        'workflow-activity-vnext',
        'workflow-templates',
        filterKey,
        cursor,
      ] as const,
    [filterKey],
  );
  const fetchTemplatePage = React.useCallback(
    (cursor: string | undefined) =>
      runtimeCatalogApi.listWorkflowTemplates({
        query: search.trim() || undefined,
        sort,
        cursor,
        take: TEMPLATE_PAGE_SIZE,
      }),
    [search, sort],
  );
  const materialization = useDraftMaterialization(scopeId);

  const listQuery = useQuery({
    queryKey: templateQueryKey(currentCursor),
    queryFn: () => fetchTemplatePage(currentCursor),
  });
  const detailQuery = useQuery({
    enabled: Boolean(selectedTemplateId),
    queryKey: [
      'workflow-activity-vnext',
      'workflow-template',
      selectedTemplateId,
    ],
    queryFn: () =>
      runtimeCatalogApi.getWorkflowTemplate(selectedTemplateId as string),
  });

  const navigateToWorkflow = React.useCallback(
    (workflowId: string) =>
      history.push(buildWorkflowActivityEditorHref(scopeId, workflowId)),
    [scopeId],
  );

  const createFromTemplate = React.useCallback(
    async (
      template: WorkflowTemplateSummary,
      surface: TemplateFailure['surface'],
    ) => {
      if (creatingTemplateId) return;
      setCreatingTemplateId(template.templateId);
      setFailure(null);
      try {
        const receipt = await studioApi.instantiateWorkflowTemplate({
          expectedAuthorityStateVersion: template.authorityStateVersion,
          scopeId,
          templateId: template.templateId,
        });
        const readable = await materialization.observe(receipt);
        if (readable) navigateToWorkflow(readable.workflowId);
      } catch (error) {
        const stale = isStudioApiErrorCode(
          error,
          409,
          'WORKFLOW_TEMPLATE_VERSION_CONFLICT',
        );
        setFailure({
          message: stale
            ? t(
                'workflowActivityVNext.new.templateBrowser.templateOutOfDateDescription',
                'This template changed while you were viewing it. Refresh the catalog and try again.',
              )
            : errorMessage(error),
          surface,
          stale,
          templateId: template.templateId,
        });
        toast.error(
          t(
            'workflowActivityVNext.new.templateBrowser.couldNotUse',
            'Workflow template could not be used',
          ),
        );
      } finally {
        setCreatingTemplateId(null);
      }
    },
    [
      creatingTemplateId,
      materialization.observe,
      navigateToWorkflow,
      scopeId,
      toast,
    ],
  );

  const retryObservation = React.useCallback(async () => {
    const readable = await materialization.retry();
    if (readable) navigateToWorkflow(readable.workflowId);
  }, [materialization.retry, navigateToWorkflow]);

  const currentItems = listQuery.data?.items ?? [];
  const nextCursor = listQuery.data?.nextCursor ?? null;
  const detail = detailQuery.data;
  const creationBusy = Boolean(
    creatingTemplateId ||
      materialization.phase === 'accepted' ||
      materialization.phase === 'observing',
  );
  const creationLocked = Boolean(
    creatingTemplateId || materialization.phase !== 'idle',
  );
  const browserFailure = failure?.surface === 'browser' ? failure : null;
  const modalFailure =
    failure?.surface === 'modal' && failure.templateId === selectedTemplateId
      ? failure
      : null;

  const paginationTotal =
    activePagination.page * TEMPLATE_PAGE_SIZE + (nextCursor ? 1 : 0);
  const goToPage = React.useCallback(
    async (requestedPage: number) => {
      const maximumKnownPage = Math.max(
        1,
        Math.ceil(paginationTotal / TEMPLATE_PAGE_SIZE),
      );
      if (
        requestedPage < 1 ||
        requestedPage === activePagination.page ||
        requestedPage > maximumKnownPage
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
          const queryKey = templateQueryKey(cursor);
          const pageData =
            queryClient.getQueryData<WorkflowTemplateListResponse>(queryKey) ??
            (await queryClient.fetchQuery({
              queryKey,
              queryFn: () => fetchTemplatePage(cursor),
            }));

          if (
            navigationRequestId.current !== requestId ||
            currentFilterKey.current !== filterKey
          ) {
            return;
          }
          if (!pageData?.nextCursor) {
            throw new Error('The requested template page is unavailable.');
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
      fetchTemplatePage,
      filterKey,
      paginationTotal,
      queryClient,
      templateQueryKey,
    ],
  );
  const retryPageNavigation = React.useCallback(() => {
    if (pendingPage === null) return;
    void goToPage(pendingPage);
  }, [goToPage, pendingPage]);

  const refreshTemplate = async () => {
    const [listResult, detailResult] = await Promise.all([
      listQuery.refetch(),
      failure?.templateId === selectedTemplateId
        ? detailQuery.refetch()
        : Promise.resolve(null),
    ]);
    if (
      !listResult.isError &&
      (detailResult === null ||
        (!detailResult.isError && Boolean(detailResult.data)))
    ) {
      setFailure(null);
    }
  };

  return (
    <>
      <section
        className="wa-vnext__template-browser"
        aria-label={t(
          'workflowActivityVNext.new.templateBrowser.title',
          'Workflow templates',
        )}
      >
        {browserFailure ? (
          <Alert
            action={
              browserFailure.stale ? (
                <Button
                  loading={listQuery.isFetching}
                  onClick={() => void refreshTemplate()}
                >
                  {t(
                    'workflowActivityVNext.new.templateBrowser.refreshCatalog',
                    'Refresh catalog',
                  )}
                </Button>
              ) : undefined
            }
            description={browserFailure.message}
            title={
              browserFailure.stale
                ? t(
                    'workflowActivityVNext.new.templateBrowser.templateOutOfDate',
                    'Template is out of date',
                  )
                : t(
                    'workflowActivityVNext.new.templateBrowser.creationFailed',
                    'Template creation failed',
                  )
            }
            showIcon
            type="error"
          />
        ) : null}

        {materialization.phase !== 'idle' && materialization.receipt ? (
          <div
            className={
              materialization.phase === 'failed'
                ? 'wa-vnext__notice wa-vnext__notice--error'
                : 'wa-vnext__notice'
            }
            role="status"
          >
            <strong>
              {materialization.phase === 'delayed'
                ? t(
                    'workflowActivityVNext.new.templateBrowser.projectionDelayed',
                    'This is taking longer than expected',
                  )
                : materialization.phase === 'failed'
                  ? t(
                      'workflowActivityVNext.new.templateBrowser.observationFailed',
                      "Workflow couldn't be opened",
                    )
                  : t(
                      'workflowActivityVNext.new.templateBrowser.creating',
                      'Creating workflow…',
                    )}
            </strong>
            <p>
              {materialization.phase === 'delayed' ||
              materialization.phase === 'failed'
                ? t(
                    'workflowActivityVNext.new.templateBrowser.safeToRetry',
                    'Your work is safe. Try again to finish opening the workflow.',
                  )
                : t(
                    'workflowActivityVNext.new.templateBrowser.creatingDescription',
                    'This usually takes only a moment.',
                  )}
            </p>
            {materialization.error ? (
              <TechnicalDetails>
                {errorMessage(materialization.error)}
              </TechnicalDetails>
            ) : null}
            {materialization.phase === 'delayed' ||
            materialization.phase === 'failed' ? (
              <Button onClick={() => void retryObservation()}>
                {t(
                  'workflowActivityVNext.new.templateBrowser.tryAgain',
                  'Try again',
                )}
              </Button>
            ) : null}
          </div>
        ) : null}

        <div className="wa-vnext__template-toolbar">
          <Input.Search
            aria-label={t(
              'workflowActivityVNext.new.templateBrowser.search',
              'Search templates',
            )}
            className="wa-vnext__template-search"
            onChange={(event) => setSearch(event.target.value)}
            placeholder={t(
              'workflowActivityVNext.new.templateBrowser.search',
              'Search templates',
            )}
            value={search}
          />
          <div className="wa-vnext__template-sort">
            <span className="wa-vnext__template-sort-label">
              {t('workflowActivityVNext.new.templateBrowser.sortBy', 'Sort by')}
            </span>
            <Select
              aria-label={t(
                'workflowActivityVNext.new.templateBrowser.sort',
                'Sort templates',
              )}
              onChange={(value) => setSort(value)}
              options={[
                {
                  label: t(
                    'workflowActivityVNext.new.templateBrowser.sort.recent',
                    'Last updated: newest first',
                  ),
                  value: '-updated',
                },
                {
                  label: t(
                    'workflowActivityVNext.new.templateBrowser.sort.nameAsc',
                    'Name: A to Z',
                  ),
                  value: 'displayName',
                },
                {
                  label: t(
                    'workflowActivityVNext.new.templateBrowser.sort.nameDesc',
                    'Name: Z to A',
                  ),
                  value: '-displayName',
                },
                {
                  label: t(
                    'workflowActivityVNext.new.templateBrowser.sort.oldest',
                    'Last updated: oldest first',
                  ),
                  value: 'updated',
                },
              ]}
              popupMatchSelectWidth={280}
              value={sort}
            />
          </div>
        </div>

        {listQuery.isPending ? (
          <div className="wa-vnext__state" role="status">
            <p>
              {t(
                'workflowActivityVNext.new.templateBrowser.loading',
                'Loading templates…',
              )}
            </p>
          </div>
        ) : listQuery.isError ? (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <Alert
              action={
                <Button onClick={() => void listQuery.refetch()}>
                  {t(
                    'workflowActivityVNext.new.templateBrowser.retry',
                    'Retry',
                  )}
                </Button>
              }
              description={
                hasStatus(listQuery.error, 401)
                  ? t(
                      'workflowActivityVNext.new.templateBrowser.signIn',
                      'Sign in to browse public workflow templates.',
                    )
                  : hasStatus(listQuery.error, 404)
                    ? t(
                        'workflowActivityVNext.new.templateBrowser.unavailableDescription',
                        'The template catalog is not available in this environment yet.',
                      )
                    : errorMessage(listQuery.error)
              }
              title={t(
                hasStatus(listQuery.error, 404)
                  ? 'workflowActivityVNext.new.templateBrowser.unavailable'
                  : 'workflowActivityVNext.new.templateBrowser.loadFailed',
                hasStatus(listQuery.error, 404)
                  ? 'Templates are not available in this environment.'
                  : 'Templates could not be loaded',
              )}
              showIcon
              type="error"
            />
            {hasStatus(listQuery.error, 404) ? (
              <TechnicalDetails>
                {errorMessage(listQuery.error)}
              </TechnicalDetails>
            ) : null}
          </div>
        ) : currentItems.length === 0 ? (
          <div className="wa-vnext__state wa-vnext__state--compact">
            <Empty
              description={
                search.trim()
                  ? t(
                      'workflowActivityVNext.new.templateBrowser.noSearchResults',
                      'No templates matched your search.',
                    )
                  : t(
                      'workflowActivityVNext.new.templateBrowser.noTemplates',
                      'No public workflow templates are available yet.',
                    )
              }
            />
          </div>
        ) : (
          <TableScrollRegion
            ariaLabel={t(
              'workflowActivityVNext.new.templateBrowser.catalogue',
              'Workflow template catalogue',
            )}
            className="wa-vnext__template-table-region"
          >
            <table
              aria-label={t(
                'workflowActivityVNext.new.templateBrowser.catalogue',
                'Workflow template catalogue',
              )}
              className="wa-vnext__table wa-vnext__template-table"
            >
              <colgroup>
                <col className="wa-vnext__template-column--identity" />
                <col className="wa-vnext__template-column--connection" />
                <col className="wa-vnext__template-column--does" />
                <col className="wa-vnext__template-column--updated" />
                <col className="wa-vnext__template-column--actions" />
              </colgroup>
              <thead>
                <tr>
                  <th className="wa-vnext__template-cell--left" scope="col">
                    <span className="wa-vnext__template-header-label">
                      <ProfileOutlined aria-hidden="true" />
                      {t(
                        'workflowActivityVNext.new.templateBrowser.template',
                        'Template',
                      )}
                    </span>
                  </th>
                  <th className="wa-vnext__template-cell--left" scope="col">
                    <span className="wa-vnext__template-header-label">
                      <LinkOutlined aria-hidden="true" />
                      {t(
                        'workflowActivityVNext.new.templateBrowser.connection',
                        'Connection',
                      )}
                    </span>
                  </th>
                  <th className="wa-vnext__template-cell--left" scope="col">
                    <span className="wa-vnext__template-header-label">
                      <PlayCircleOutlined aria-hidden="true" />
                      {t(
                        'workflowActivityVNext.new.templateBrowser.does',
                        'Does',
                      )}
                    </span>
                  </th>
                  <th className="wa-vnext__template-cell--right" scope="col">
                    <span className="wa-vnext__template-header-label">
                      <CalendarOutlined aria-hidden="true" />
                      {t(
                        'workflowActivityVNext.new.templateBrowser.updatedColumn',
                        'Updated',
                      )}
                    </span>
                  </th>
                  <th
                    aria-label={t(
                      'workflowActivityVNext.new.templateBrowser.actions',
                      'Actions',
                    )}
                    className="wa-vnext__template-cell--right"
                    scope="col"
                  />
                </tr>
              </thead>
              <tbody>
                {currentItems.map((template) => (
                  <TemplateRow
                    creating={creationBusy}
                    disabled={creationLocked}
                    key={template.templateId}
                    onCreate={(selected) =>
                      void createFromTemplate(selected, 'browser')
                    }
                    onView={setSelectedTemplateId}
                    template={template}
                  />
                ))}
              </tbody>
            </table>
          </TableScrollRegion>
        )}

        <div className="wa-vnext__activity-footer">
          <span aria-live="polite">
            {t('workflowActivityVNext.activity.page', 'Page {page}', {
              page: activePagination.page,
            })}
          </span>
          <Pagination
            current={activePagination.page}
            data-testid="activity-pagination"
            disabled={creationLocked || isResolvingPage || listQuery.isFetching}
            onChange={(page) => void goToPage(page)}
            pageSize={TEMPLATE_PAGE_SIZE}
            showQuickJumper={false}
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
      </section>

      <Modal
        destroyOnHidden
        footer={
          <Space>
            <Button
              disabled={creationLocked}
              onClick={() => setSelectedTemplateId(null)}
            >
              {t('workflowActivityVNext.new.templateBrowser.cancel', 'Cancel')}
            </Button>
            <Button
              disabled={
                !detail ||
                creationLocked ||
                detailQuery.isFetching ||
                detailQuery.isError ||
                detailQuery.isRefetchError ||
                Boolean(modalFailure?.stale)
              }
              loading={Boolean(
                detail && creatingTemplateId === detail.template.templateId,
              )}
              onClick={() =>
                detail && void createFromTemplate(detail.template, 'modal')
              }
              type="primary"
            >
              {t(
                'workflowActivityVNext.new.templateBrowser.useThis',
                'Use this template',
              )}
            </Button>
          </Space>
        }
        onCancel={() => setSelectedTemplateId(null)}
        open={Boolean(selectedTemplateId)}
        title={
          detail?.template.displayName ??
          t('workflowActivityVNext.new.templateBrowser.view', 'View template')
        }
        width={1040}
      >
        {modalFailure ? (
          <Alert
            action={
              modalFailure.stale ? (
                <Button
                  loading={listQuery.isFetching || detailQuery.isFetching}
                  onClick={() => void refreshTemplate()}
                >
                  {t(
                    'workflowActivityVNext.new.templateBrowser.refreshCatalog',
                    'Refresh catalog',
                  )}
                </Button>
              ) : undefined
            }
            description={modalFailure.message}
            title={
              modalFailure.stale
                ? t(
                    'workflowActivityVNext.new.templateBrowser.templateOutOfDate',
                    'Template is out of date',
                  )
                : t(
                    'workflowActivityVNext.new.templateBrowser.creationFailed',
                    'Template creation failed',
                  )
            }
            showIcon
            type="error"
          />
        ) : null}
        {detailQuery.isPending ? (
          <div role="status">
            {t(
              'workflowActivityVNext.new.templateBrowser.detailLoading',
              'Loading template details…',
            )}
          </div>
        ) : null}
        {detailQuery.isError ? (
          <Alert
            action={
              <Button onClick={() => void detailQuery.refetch()}>
                {t('workflowActivityVNext.new.templateBrowser.retry', 'Retry')}
              </Button>
            }
            description={
              hasStatus(detailQuery.error, 404)
                ? t(
                    'workflowActivityVNext.new.templateBrowser.noLongerAvailable',
                    'This template is no longer available.',
                  )
                : errorMessage(detailQuery.error)
            }
            title={t(
              'workflowActivityVNext.new.templateBrowser.detailFailed',
              'Template details could not be loaded',
            )}
            showIcon
            type="error"
          />
        ) : null}
        {detail ? <TemplateDetailBody detail={detail} /> : null}
      </Modal>
    </>
  );
};

export default WorkflowTemplateBrowser;
