import { LeftOutlined, RightOutlined } from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  Select,
  Space,
  Tabs,
  Typography,
} from 'antd';
import React from 'react';
import { runtimeCatalogApi } from '@/shared/api/runtimeCatalogApi';
import { t } from '@/shared/i18n/messages';
import type {
  WorkflowTemplateDetail,
  WorkflowTemplateSummary,
} from '@/shared/models/runtime/workflowTemplates';
import { history } from '@/shared/navigation/history';
import { isStudioApiErrorCode, studioApi } from '@/shared/studio/api';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { useDraftMaterialization } from '../hooks/useDraftMaterialization';
import { buildWorkflowActivityEditorHref } from '../navigation';
import TechnicalDetails from '../TechnicalDetails';

const TEMPLATE_PAGE_SIZE = 12;

type WorkflowTemplateBrowserProps = {
  readonly scopeId: string;
};

type TemplateFailure = {
  readonly message: string;
  readonly surface: 'browser' | 'modal';
  readonly stale: boolean;
  readonly templateId: string;
};

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
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
  }).format(timestamp);
}

function TemplateFact({
  label,
  value,
}: {
  readonly label: string;
  readonly value: string;
}) {
  return (
    <div className="wa-vnext__template-fact">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function TemplateRow({
  creating,
  onCreate,
  onView,
  template,
}: {
  readonly creating: boolean;
  readonly onCreate: (template: WorkflowTemplateSummary) => void;
  readonly onView: (templateId: string) => void;
  readonly template: WorkflowTemplateSummary;
}) {
  return (
    <article className="wa-vnext__template-row">
      <div className="wa-vnext__template-identity">
        <Typography.Title level={2}>{template.displayName}</Typography.Title>
        <p>
          {template.description ||
            t(
              'workflowActivityVNext.new.templateBrowser.fallbackDescription',
              'A ready-made workflow for your workspace.',
            )}
        </p>
        <span className="wa-vnext__template-meta">
          {t(
            'workflowActivityVNext.new.templateBrowser.updated',
            'updated {date}',
            { date: formatUpdatedAt(template) },
          )}
        </span>
      </div>
      <div className="wa-vnext__template-facts">
        <TemplateFact
          label={t('workflowActivityVNext.new.templateBrowser.reads', 'Reads')}
          value={t(
            'workflowActivityVNext.new.templateBrowser.workflowInputs',
            'Workflow inputs',
          )}
        />
        <TemplateFact
          label={t(
            'workflowActivityVNext.new.templateBrowser.connection',
            'Connection',
          )}
          value={formatConnections(template)}
        />
        <TemplateFact
          label={t('workflowActivityVNext.new.templateBrowser.does', 'Does')}
          value={t(
            'workflowActivityVNext.new.templateBrowser.runsSteps',
            'Runs {count} {unit}',
            {
              count: template.stepCount,
              unit: t(
                template.stepCount === 1
                  ? 'workflowActivityVNext.new.templateBrowser.step'
                  : 'workflowActivityVNext.new.templateBrowser.steps',
                template.stepCount === 1 ? 'step' : 'steps',
              ),
            },
          )}
        />
      </div>
      <div className="wa-vnext__template-actions">
        <Button
          aria-label={t(
            'workflowActivityVNext.new.templateBrowser.viewNamed',
            'View {name}',
            {
              name: template.displayName,
            },
          )}
          disabled={creating}
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
          disabled={creating}
          loading={creating}
          onClick={() => onCreate(template)}
          type="primary"
        >
          {t('workflowActivityVNext.new.templateBrowser.use', 'Use template')}
        </Button>
      </div>
    </article>
  );
}

function TemplateDetailBody({
  detail,
}: {
  readonly detail: WorkflowTemplateDetail;
}) {
  const overview = (
    <div className="wa-vnext__template-overview">
      <div className="wa-vnext__template-detail-summary">
        <TemplateFact
          label={t('workflowActivityVNext.new.templateBrowser.reads', 'Reads')}
          value={t(
            'workflowActivityVNext.new.templateBrowser.workflowInputs',
            'Workflow inputs',
          )}
        />
        <TemplateFact
          label={t(
            'workflowActivityVNext.new.templateBrowser.connection',
            'Connection',
          )}
          value={formatConnections(detail.template)}
        />
        <TemplateFact
          label={t('workflowActivityVNext.new.templateBrowser.does', 'Does')}
          value={t(
            'workflowActivityVNext.new.templateBrowser.runsSteps',
            'Runs {count} {unit}',
            {
              count: detail.template.stepCount,
              unit: t(
                detail.template.stepCount === 1
                  ? 'workflowActivityVNext.new.templateBrowser.step'
                  : 'workflowActivityVNext.new.templateBrowser.steps',
                detail.template.stepCount === 1 ? 'step' : 'steps',
              ),
            },
          )}
        />
      </div>
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
  );

  const steps = (
    <ol className="wa-vnext__template-steps">
      {detail.definition.steps.map((step, index) => (
        <li key={step.id}>
          <div>
            <strong>
              {step.id ||
                t(
                  'workflowActivityVNext.new.templateBrowser.stepNumber',
                  'Step {number}',
                  {
                    number: index + 1,
                  },
                )}
            </strong>
            <span>{step.type}</span>
          </div>
          {step.targetRole ? (
            <small>
              {t(
                'workflowActivityVNext.new.templateBrowser.role',
                'Role: {role}',
                {
                  role: step.targetRole,
                },
              )}
            </small>
          ) : null}
        </li>
      ))}
    </ol>
  );

  return (
    <Tabs
      items={[
        {
          key: 'overview',
          label: t(
            'workflowActivityVNext.new.templateBrowser.overview',
            'Overview',
          ),
          children: overview,
        },
        {
          key: 'steps',
          label: t(
            'workflowActivityVNext.new.templateBrowser.stepsTab',
            'The {count} steps',
            { count: detail.definition.steps.length },
          ),
          children: steps,
        },
      ]}
    />
  );
}

const WorkflowTemplateBrowser: React.FC<WorkflowTemplateBrowserProps> = ({
  scopeId,
}) => {
  const toast = useConsoleToast();
  const [search, setSearch] = React.useState('');
  const [sort, setSort] = React.useState('-updated');
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [cursorHistory, setCursorHistory] = React.useState<string[]>([]);
  const [selectedTemplateId, setSelectedTemplateId] = React.useState<
    string | null
  >(null);
  const [creatingTemplateId, setCreatingTemplateId] = React.useState<
    string | null
  >(null);
  const [failure, setFailure] = React.useState<TemplateFailure | null>(null);
  const materialization = useDraftMaterialization(scopeId);

  const listQuery = useQuery({
    queryKey: [
      'workflow-activity-vnext',
      'workflow-templates',
      search.trim(),
      sort,
      cursor,
    ],
    queryFn: () =>
      runtimeCatalogApi.listWorkflowTemplates({
        query: search.trim() || undefined,
        sort,
        cursor,
        take: TEMPLATE_PAGE_SIZE,
      }),
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
  const creationPending = Boolean(
    creatingTemplateId || materialization.phase !== 'idle',
  );
  const browserFailure = failure?.surface === 'browser' ? failure : null;
  const modalFailure =
    failure?.surface === 'modal' && failure.templateId === selectedTemplateId
      ? failure
      : null;

  const goToNextPage = () => {
    if (!nextCursor) return;
    setCursorHistory((history) => [...history, cursor ?? '']);
    setCursor(nextCursor);
  };

  const goToPreviousPage = () => {
    setCursorHistory((history) => {
      const previous = [...history];
      const previousCursor = previous.pop();
      setCursor(previousCursor || null);
      return previous;
    });
  };

  const resetQuery = (nextValue: string) => {
    setSearch(nextValue);
    setCursor(null);
    setCursorHistory([]);
  };

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
        <div className="wa-vnext__template-browser-heading">
          <span className="wa-vnext__template-page-status">
            {t(
              'workflowActivityVNext.new.templateBrowser.page',
              'Page {page}',
              {
                page: cursorHistory.length + 1,
              },
            )}
          </span>
        </div>

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
            onChange={(event) => resetQuery(event.target.value)}
            placeholder={t(
              'workflowActivityVNext.new.templateBrowser.search',
              'Search templates',
            )}
            value={search}
          />
          <Select
            aria-label={t(
              'workflowActivityVNext.new.templateBrowser.sort',
              'Sort templates',
            )}
            onChange={(value) => {
              setSort(value);
              setCursor(null);
              setCursorHistory([]);
            }}
            options={[
              {
                label: t(
                  'workflowActivityVNext.new.templateBrowser.sort.recent',
                  'Recently updated',
                ),
                value: '-updated',
              },
              {
                label: t(
                  'workflowActivityVNext.new.templateBrowser.sort.nameAsc',
                  'Name A–Z',
                ),
                value: 'displayName',
              },
              {
                label: t(
                  'workflowActivityVNext.new.templateBrowser.sort.nameDesc',
                  'Name Z–A',
                ),
                value: '-displayName',
              },
              {
                label: t(
                  'workflowActivityVNext.new.templateBrowser.sort.oldest',
                  'Oldest updated',
                ),
                value: 'updated',
              },
            ]}
            value={sort}
          />
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
          <div className="wa-vnext__template-list">
            {currentItems.map((template) => (
              <TemplateRow
                creating={creationPending}
                key={template.templateId}
                onCreate={(selected) =>
                  void createFromTemplate(selected, 'browser')
                }
                onView={setSelectedTemplateId}
                template={template}
              />
            ))}
          </div>
        )}

        <div className="wa-vnext__template-pagination">
          <Typography.Text type="secondary">
            {currentItems.length > 0
              ? t(
                  'workflowActivityVNext.new.templateBrowser.templatesOnPage',
                  '{count} templates on this page',
                  { count: currentItems.length },
                )
              : ''}
          </Typography.Text>
          <Space>
            <Button
              disabled={cursorHistory.length === 0 || creationPending}
              icon={<LeftOutlined aria-hidden="true" />}
              onClick={goToPreviousPage}
            >
              {t(
                'workflowActivityVNext.new.templateBrowser.previous',
                'Previous',
              )}
            </Button>
            <Button
              disabled={!nextCursor || creationPending}
              icon={<RightOutlined aria-hidden="true" />}
              onClick={goToNextPage}
            >
              {t('workflowActivityVNext.new.templateBrowser.next', 'Next')}
            </Button>
          </Space>
        </div>
      </section>

      <Modal
        destroyOnHidden
        footer={
          <Space>
            <Button
              disabled={creationPending}
              onClick={() => setSelectedTemplateId(null)}
            >
              {t('workflowActivityVNext.new.templateBrowser.cancel', 'Cancel')}
            </Button>
            <Button
              disabled={
                !detail ||
                creationPending ||
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
        width={720}
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
