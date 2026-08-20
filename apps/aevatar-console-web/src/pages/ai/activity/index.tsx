import {
  ArrowRightOutlined,
  HistoryOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { type InfiniteData, useInfiniteQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Empty,
  Input,
  Select,
  Skeleton,
  Space,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import React from 'react';
import {
  type AIWorkspaceConversationCollection,
  type AIWorkspaceConversationSummary,
  type AIWorkspaceRunCollection,
  type AIWorkspaceRunSummary,
  aiWorkspaceApi,
} from '@/shared/api/aiWorkspaceApi';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import {
  AI_ACTIVITY_ROUTE,
  buildAIActivityRunDetailHref,
  buildAIChatHref,
} from '@/shared/navigation/aiRoutes';
import { history } from '@/shared/navigation/history';
import {
  type AIWorkspaceScopeAuthority,
  aiWorkspaceQueryKeys,
} from '@/shared/query/aiWorkspaceQueryKeys';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from '../components/AIWorkspaceShell';
import './activity.less';

const ACTIVITY_PAGE_SIZE = 20;

type RunFilters = {
  status?: string;
  q?: string;
};

type ConversationPage = AIWorkspaceConversationCollection;
type RunPage = AIWorkspaceRunCollection;

function isPlainPrimaryClick(event: React.MouseEvent<HTMLElement>): boolean {
  return (
    event.button === 0 &&
    !event.altKey &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.shiftKey
  );
}

function navigateFromAnchor(
  event: React.MouseEvent<HTMLElement>,
  href: string,
): void {
  if (!isPlainPrimaryClick(event)) {
    return;
  }
  event.preventDefault();
  history.push(href);
}

function humanize(value: string | null | undefined): string {
  const normalized = String(value ?? '')
    .trim()
    .replace(/[_-]+/g, ' ');
  return normalized
    ? normalized
        .split(' ')
        .filter(Boolean)
        .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
        .join(' ')
    : t('pages.ai.activity.value.unavailable', 'Unavailable');
}

function statusColor(status: string): string {
  switch (status.trim().toLowerCase()) {
    case 'completed':
      return 'success';
    case 'failed':
      return 'error';
    case 'running':
      return 'processing';
    case 'timed_out':
      return 'warning';
    case 'stopped':
      return 'default';
    default:
      return 'default';
  }
}

function statusLabel(status: string): string {
  const normalized = status.trim().toLowerCase();
  const labels: Record<string, string> = {
    completed: t('pages.ai.activity.status.completed', 'Completed'),
    failed: t('pages.ai.activity.status.failed', 'Failed'),
    running: t('pages.ai.activity.status.running', 'Running'),
    stopped: t('pages.ai.activity.status.stopped', 'Stopped'),
    timed_out: t('pages.ai.activity.status.timedOut', 'Timed out'),
  };
  return labels[normalized] ?? t('pages.ai.activity.status.unknown', 'Unknown');
}

function formatDuration(value: number | null): string {
  if (value === null || !Number.isFinite(value) || value < 0) {
    return t('pages.ai.activity.value.unavailable', 'Unavailable');
  }
  if (value < 1000) {
    return `${Math.round(value)}ms`;
  }
  if (value < 60_000) {
    return `${(value / 1000).toFixed(value < 10_000 ? 2 : 1)}s`;
  }
  const minutes = Math.floor(value / 60_000);
  const seconds = Math.round((value % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

function CollectionError({
  message,
  onRetry,
  stale,
}: {
  message: string;
  onRetry: () => void;
  stale?: boolean;
}): React.ReactElement {
  return (
    <Alert
      action={
        <Button onClick={onRetry} size="small">
          {t('pages.ai.activity.retry', 'Retry')}
        </Button>
      }
      description={
        stale
          ? t(
              'pages.ai.activity.refreshError.staleDescription',
              '{message} Showing the last observed page.',
              { message },
            )
          : message
      }
      message={t('pages.ai.activity.sourceUnavailable', 'Source unavailable')}
      showIcon
      type="warning"
    />
  );
}

function CollectionLoading(): React.ReactElement {
  return (
    <div aria-busy="true" className="ai-activity-loading">
      <Skeleton active paragraph={{ rows: 2 }} title={false} />
      <Skeleton active paragraph={{ rows: 2 }} title={false} />
    </div>
  );
}

function ConversationRow({
  conversation,
}: {
  conversation: AIWorkspaceConversationSummary;
}): React.ReactElement {
  const title =
    conversation.title.trim() ||
    t('pages.ai.activity.conversations.untitled', 'Untitled conversation');
  const updatedAt = formatUtcDateTime(
    conversation.updatedAtUtc,
    t('pages.ai.activity.value.unavailable', 'Unavailable'),
  );
  const href = buildAIChatHref(conversation.conversationId);

  return (
    <a
      className="ai-activity-row ai-activity-conversation-row"
      href={href}
      onClick={(event) => navigateFromAnchor(event, href)}
    >
      <div className="ai-activity-row-main">
        <Typography.Text ellipsis={{ tooltip: title }} strong>
          {title}
        </Typography.Text>
        <Typography.Text className="ai-activity-row-secondary">
          {conversation.serviceKind.trim()
            ? humanize(conversation.serviceKind)
            : t('pages.ai.activity.value.unavailable', 'Unavailable')}
          {' · '}
          {t('pages.ai.activity.conversations.messages', '{count} messages', {
            count: conversation.messageCount,
          })}
        </Typography.Text>
        {conversation.activeStepSummary?.trim() ? (
          <Typography.Text
            className="ai-activity-row-detail"
            ellipsis={{ tooltip: conversation.activeStepSummary }}
          >
            {conversation.activeStepSummary}
          </Typography.Text>
        ) : null}
      </div>
      <div className="ai-activity-row-meta">
        {conversation.taskStatus?.trim() ? (
          <Tag color={statusColor(conversation.taskStatus)}>
            {statusLabel(conversation.taskStatus)}
          </Tag>
        ) : null}
        <Typography.Text type="secondary">{updatedAt}</Typography.Text>
      </div>
      <ArrowRightOutlined aria-hidden="true" />
    </a>
  );
}

function RunRow({ run }: { run: AIWorkspaceRunSummary }): React.ReactElement {
  const title =
    run.workflowName.trim() ||
    t('pages.ai.activity.runs.untitled', 'Unnamed workflow run');
  const href = buildAIActivityRunDetailHref(run.runId);
  const failure = run.firstFailure?.message.trim();
  const waiting = run.waiting?.waitingKind.trim();
  const detail = failure
    ? failure
    : waiting
      ? t('pages.ai.activity.runs.waiting', 'Waiting: {kind}', {
          kind: humanize(waiting),
        })
      : run.inputSummary.trim() ||
        t('pages.ai.activity.value.unavailable', 'Unavailable');

  return (
    <a
      className="ai-activity-row ai-activity-run-row"
      href={href}
      onClick={(event) => navigateFromAnchor(event, href)}
    >
      <div className="ai-activity-row-main">
        <Typography.Text ellipsis={{ tooltip: title }} strong>
          {title}
        </Typography.Text>
        <Typography.Text
          className="ai-activity-row-secondary"
          ellipsis={{ tooltip: detail }}
        >
          {detail}
        </Typography.Text>
        <Typography.Text className="ai-activity-row-detail">
          {humanize(run.runOrigin)}
          {' · '}
          {formatDuration(run.durationMs)}
        </Typography.Text>
      </div>
      <div className="ai-activity-row-meta">
        <Tag color={statusColor(run.status)}>{statusLabel(run.status)}</Tag>
        <Typography.Text type="secondary">
          {formatUtcDateTime(
            run.updatedAtUtc,
            t('pages.ai.activity.value.unavailable', 'Unavailable'),
          )}
        </Typography.Text>
      </div>
      <ArrowRightOutlined aria-hidden="true" />
    </a>
  );
}

function Pager({
  hasNextPage,
  isFetching,
  onNext,
}: {
  hasNextPage: boolean;
  isFetching: boolean;
  onNext: () => void;
}): React.ReactElement | null {
  if (!hasNextPage) {
    return null;
  }
  return (
    <div className="ai-activity-pager">
      <Button disabled={isFetching} loading={isFetching} onClick={onNext}>
        {t('pages.ai.activity.loadMore', 'Load more')}
      </Button>
    </div>
  );
}

function ConversationsSource({
  scopeId,
  queryAuthority,
  enabled,
}: {
  scopeId: string;
  queryAuthority: AIWorkspaceScopeAuthority;
  enabled: boolean;
}): React.ReactElement {
  const query = useInfiniteQuery<
    ConversationPage,
    Error,
    InfiniteData<ConversationPage>,
    ReturnType<typeof aiWorkspaceQueryKeys.activityConversations>,
    string
  >({
    enabled,
    getNextPageParam: (lastPage) => lastPage.nextCursor ?? undefined,
    initialPageParam: '',
    queryFn: ({ pageParam, signal }) =>
      aiWorkspaceApi.getConversations(
        {
          cursor: pageParam || undefined,
          take: ACTIVITY_PAGE_SIZE,
        },
        signal,
      ),
    queryKey: aiWorkspaceQueryKeys.activityConversations(queryAuthority, {
      take: ACTIVITY_PAGE_SIZE,
    }),
    retry: false,
  });
  const pages = query.data?.pages ?? [];
  const scopeMismatch = pages.some((page) => page.scopeId !== scopeId);
  const items = pages.flatMap((page) => page.items);
  const unavailablePage = pages.find(
    (page) => page.availability === 'unavailable',
  );
  const sourceError = unavailablePage?.error?.message;
  const showBlockingError = Boolean(query.isError && items.length === 0);

  return (
    <section
      aria-labelledby="ai-activity-conversations-title"
      className="ai-activity-source"
    >
      <div className="ai-activity-source-heading">
        <div>
          <div className="ai-activity-source-title-row">
            <Typography.Title id="ai-activity-conversations-title" level={2}>
              {t('pages.ai.activity.conversations.title', 'Conversations')}
            </Typography.Title>
            <Tag>
              {t('pages.ai.activity.conversations.source', 'Chat history')}
            </Tag>
          </div>
          <Typography.Text type="secondary">
            {t(
              'pages.ai.activity.conversations.description',
              'Your recent conversations in this workspace.',
            )}
          </Typography.Text>
        </div>
        <Tooltip title={t('pages.ai.activity.refresh', 'Refresh activity')}>
          <Button
            aria-label={t('pages.ai.activity.refresh', 'Refresh activity')}
            icon={<ReloadOutlined />}
            loading={query.isFetching}
            onClick={() => void query.refetch()}
          />
        </Tooltip>
      </div>

      {scopeMismatch ? (
        <Alert
          description={t(
            'pages.ai.activity.scopeMismatch.description',
            'The conversation source returned a different authorized scope.',
          )}
          message={t(
            'pages.ai.activity.scopeMismatch.title',
            'Activity scope mismatch',
          )}
          showIcon
          type="error"
        />
      ) : (
        <>
          {query.isError && !showBlockingError ? (
            <CollectionError
              message={describeError(
                query.error,
                t(
                  'pages.ai.activity.conversations.error.description',
                  'Conversation activity could not be refreshed.',
                ),
              )}
              onRetry={() => void query.refetch()}
              stale
            />
          ) : null}
          {sourceError ? (
            <CollectionError
              message={sourceError}
              onRetry={() => void query.refetch()}
              stale={items.length > 0}
            />
          ) : null}
          {query.isPending ? (
            <CollectionLoading />
          ) : showBlockingError ? (
            <InventoryReadinessState
              action={{
                label: t('pages.ai.activity.retry', 'Retry'),
                onClick: () => void query.refetch(),
              }}
              description={describeError(
                query.error,
                t(
                  'pages.ai.activity.conversations.error.description',
                  'Conversation activity could not be loaded.',
                ),
              )}
              kind="error"
              title={t(
                'pages.ai.activity.conversations.error.title',
                'Conversations unavailable',
              )}
            />
          ) : !items.length ? (
            sourceError ? null : (
              <Empty
                description={t(
                  'pages.ai.activity.conversations.empty',
                  'No conversations have been observed yet.',
                )}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )
          ) : (
            <>
              <div className="ai-activity-list">
                {items.map((conversation) => (
                  <ConversationRow
                    conversation={conversation}
                    key={conversation.conversationId}
                  />
                ))}
              </div>
              <Pager
                hasNextPage={Boolean(query.hasNextPage)}
                isFetching={query.isFetchingNextPage}
                onNext={() => void query.fetchNextPage()}
              />
            </>
          )}
        </>
      )}
    </section>
  );
}

function RunsSource({
  filters,
  onFiltersChange,
  scopeId,
  queryAuthority,
  enabled,
}: {
  filters: RunFilters;
  onFiltersChange: (next: RunFilters) => void;
  scopeId: string;
  queryAuthority: AIWorkspaceScopeAuthority;
  enabled: boolean;
}): React.ReactElement {
  const query = useInfiniteQuery<
    RunPage,
    Error,
    InfiniteData<RunPage>,
    ReturnType<typeof aiWorkspaceQueryKeys.activityRuns>,
    string
  >({
    enabled,
    getNextPageParam: (lastPage) =>
      lastPage.hasMore ? (lastPage.nextCursor ?? undefined) : undefined,
    initialPageParam: '',
    queryFn: ({ pageParam, signal }) =>
      aiWorkspaceApi.getRuns(
        {
          cursor: pageParam || undefined,
          q: filters.q,
          status: filters.status,
          take: ACTIVITY_PAGE_SIZE,
        },
        signal,
      ),
    queryKey: aiWorkspaceQueryKeys.activityRuns(queryAuthority, {
      q: filters.q,
      status: filters.status,
      take: ACTIVITY_PAGE_SIZE,
    }),
    retry: false,
  });
  const pages = query.data?.pages ?? [];
  const scopeMismatch = pages.some((page) => page.scopeId !== scopeId);
  const items = pages.flatMap((page) => page.items);
  const unavailablePage = pages.find(
    (page) => page.availability === 'unavailable',
  );
  const sourceError = unavailablePage?.error?.message;
  const showBlockingError = Boolean(query.isError && items.length === 0);

  return (
    <section
      aria-labelledby="ai-activity-runs-title"
      className="ai-activity-source"
    >
      <div className="ai-activity-source-heading ai-activity-runs-heading">
        <div>
          <div className="ai-activity-source-title-row">
            <Typography.Title id="ai-activity-runs-title" level={2}>
              {t('pages.ai.activity.runs.title', 'Runs')}
            </Typography.Title>
            <Tag>
              {t('pages.ai.activity.runs.source', 'Workflow Observatory')}
            </Tag>
          </div>
          <Typography.Text type="secondary">
            {t(
              'pages.ai.activity.runs.description',
              'Workflow runs recorded for this workspace.',
            )}
          </Typography.Text>
        </div>
        <Space wrap>
          <Select
            allowClear
            aria-label={t(
              'pages.ai.activity.runs.statusFilter',
              'Filter by status',
            )}
            className="ai-activity-status-filter"
            onChange={(status) => onFiltersChange({ ...filters, status })}
            placeholder={t(
              'pages.ai.activity.runs.statusFilter',
              'Filter by status',
            )}
            options={[
              ['running', t('pages.ai.activity.status.running', 'Running')],
              [
                'completed',
                t('pages.ai.activity.status.completed', 'Completed'),
              ],
              ['failed', t('pages.ai.activity.status.failed', 'Failed')],
              [
                'timed_out',
                t('pages.ai.activity.status.timedOut', 'Timed out'),
              ],
              ['stopped', t('pages.ai.activity.status.stopped', 'Stopped')],
            ].map(([value, label]) => ({ label, value }))}
            value={filters.status || undefined}
          />
          <Input.Search
            allowClear
            aria-label={t('pages.ai.activity.runs.search', 'Search runs')}
            defaultValue={filters.q}
            onSearch={(q) =>
              onFiltersChange({ ...filters, q: q.trim() || undefined })
            }
            placeholder={t('pages.ai.activity.runs.search', 'Search runs')}
            style={{ width: 220 }}
          />
          <Tooltip title={t('pages.ai.activity.refresh', 'Refresh activity')}>
            <Button
              aria-label={t('pages.ai.activity.refresh', 'Refresh activity')}
              icon={<ReloadOutlined />}
              loading={query.isFetching}
              onClick={() => void query.refetch()}
            />
          </Tooltip>
        </Space>
      </div>

      {scopeMismatch ? (
        <Alert
          description={t(
            'pages.ai.activity.scopeMismatch.description',
            'The run source returned a different authorized scope.',
          )}
          message={t(
            'pages.ai.activity.scopeMismatch.title',
            'Activity scope mismatch',
          )}
          showIcon
          type="error"
        />
      ) : (
        <>
          {query.isError && !showBlockingError ? (
            <CollectionError
              message={describeError(
                query.error,
                t(
                  'pages.ai.activity.runs.error.description',
                  'Run activity could not be refreshed.',
                ),
              )}
              onRetry={() => void query.refetch()}
              stale
            />
          ) : null}
          {sourceError ? (
            <CollectionError
              message={sourceError}
              onRetry={() => void query.refetch()}
              stale={items.length > 0}
            />
          ) : null}
          {query.isPending ? (
            <CollectionLoading />
          ) : showBlockingError ? (
            <InventoryReadinessState
              action={{
                label: t('pages.ai.activity.retry', 'Retry'),
                onClick: () => void query.refetch(),
              }}
              description={describeError(
                query.error,
                t(
                  'pages.ai.activity.runs.error.description',
                  'Run activity could not be loaded.',
                ),
              )}
              kind="error"
              title={t(
                'pages.ai.activity.runs.error.title',
                'Runs unavailable',
              )}
            />
          ) : !items.length ? (
            sourceError ? null : (
              <Empty
                description={t(
                  'pages.ai.activity.runs.empty',
                  'No workflow runs match the current filters.',
                )}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )
          ) : (
            <>
              <div className="ai-activity-list">
                {items.map((run) => (
                  <RunRow key={run.runId} run={run} />
                ))}
              </div>
              <Pager
                hasNextPage={Boolean(query.hasNextPage)}
                isFetching={query.isFetchingNextPage}
                onNext={() => void query.fetchNextPage()}
              />
            </>
          )}
        </>
      )}
    </section>
  );
}

function ActivityContent(): React.ReactElement {
  const { context, queryAuthority, scopeId } = useAIWorkspaceContext();
  const scopeAuthority = { ...queryAuthority, scopeId };
  const [filters, setFilters] = React.useState<RunFilters>({});
  const activityDeclared =
    context.pages.activity === AI_ACTIVITY_ROUTE &&
    context.features.activity?.availability === 'available' &&
    context.features.activity.page === AI_ACTIVITY_ROUTE &&
    context.features.activity.api === context.apis.activity &&
    context.apis.activity === '/api/ai/activity' &&
    context.apis.conversations === '/api/ai/activity/conversations' &&
    context.apis.runs === '/api/ai/activity/runs';

  if (!activityDeclared) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.activity.notAvailable.description',
            'The backend has not enabled Activity for this workspace.',
          )}
          kind="empty"
          title={t(
            'pages.ai.activity.notAvailable.title',
            'Activity not available',
          )}
        />
      </div>
    );
  }

  return (
    <div className="ai-page ai-activity-page">
      <div className="ai-page-heading">
        <div>
          <Typography.Title level={1}>
            {t('pages.ai.activity.title', 'Activity')}
          </Typography.Title>
          <Typography.Text className="ai-page-scope">
            {t('pages.ai.activity.scope', 'Scope {scopeId}', { scopeId })}
          </Typography.Text>
        </div>
        <HistoryOutlined
          aria-hidden="true"
          className="ai-activity-title-icon"
        />
      </div>

      <div className="ai-activity-content">
        <ConversationsSource
          enabled={Boolean(scopeId)}
          queryAuthority={scopeAuthority}
          scopeId={scopeId}
        />
        <RunsSource
          enabled={Boolean(scopeId)}
          filters={filters}
          onFiltersChange={setFilters}
          queryAuthority={scopeAuthority}
          scopeId={scopeId}
        />
      </div>
    </div>
  );
}

const AIActivityPage: React.FC = () => (
  <AIWorkspaceShell>
    <ActivityContent />
  </AIWorkspaceShell>
);

export default AIActivityPage;
