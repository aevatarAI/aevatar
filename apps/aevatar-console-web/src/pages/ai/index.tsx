import {
  ArrowRightOutlined,
  MessageOutlined,
  ReloadOutlined,
  RobotOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Button, Tag, Tooltip, Typography } from 'antd';
import React from 'react';
import {
  type AIWorkspaceActivityCollectionAvailability,
  type AIWorkspaceConversationCollection,
  type AIWorkspaceConversationSummary,
  type AIWorkspaceOverviewSource,
  type AIWorkspaceRunCollection,
  type AIWorkspaceRunSummary,
  aiWorkspaceApi,
} from '@/shared/api/aiWorkspaceApi';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import {
  AI_AGENTS_ROUTE,
  AI_OVERVIEW_ROUTE,
  buildAIChatHref,
} from '@/shared/navigation/aiRoutes';
import { history } from '@/shared/navigation/history';
import { aiWorkspaceQueryKeys } from '@/shared/query/aiWorkspaceQueryKeys';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from './components/AIWorkspaceShell';
import './index.less';

const OVERVIEW_TAKE = 5;

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

function humanize(value: string): string {
  const normalized = value.trim().replace(/[_-]+/g, ' ');
  return normalized
    ? `${normalized.charAt(0).toUpperCase()}${normalized.slice(1)}`
    : t('pages.ai.overview.value.unavailable', 'Unavailable');
}

function humanizeOr(value: string, fallback: string): string {
  return value.trim() ? humanize(value) : fallback;
}

function sourceAvailabilityLabel(
  availability: AIWorkspaceOverviewSource['availability'],
): string {
  const labels: Record<AIWorkspaceOverviewSource['availability'], string> = {
    available: t('pages.ai.overview.availability.available', 'Available'),
    not_materialized: t(
      'pages.ai.overview.availability.notMaterialized',
      'Not ready',
    ),
    unavailable: t('pages.ai.overview.availability.unavailable', 'Unavailable'),
  };
  return labels[availability];
}

function sourceAvailabilityColor(
  availability: AIWorkspaceOverviewSource['availability'],
): 'default' | 'error' | 'success' {
  if (availability === 'available') {
    return 'success';
  }
  return availability === 'unavailable' ? 'error' : 'default';
}

function AgentReadinessCard({
  label,
  source,
}: {
  label: string;
  source: AIWorkspaceOverviewSource;
}): React.ReactElement {
  const updatedAt = formatUtcDateTime(source.updatedAtUtc, '');
  const count =
    source.itemCount === null
      ? t('pages.ai.overview.value.unavailable', 'Unavailable')
      : String(source.itemCount);

  return (
    <article className="ai-overview-agent-source">
      <div className="ai-overview-agent-source-heading">
        <span aria-hidden="true" className="ai-overview-agent-source-icon">
          <RobotOutlined />
        </span>
        <div className="ai-overview-agent-source-copy">
          <Typography.Title level={3}>{label}</Typography.Title>
          <Typography.Text type="secondary">
            {t('pages.ai.overview.agents.source', 'Agent Profile catalog')}
          </Typography.Text>
        </div>
        <Tag color={sourceAvailabilityColor(source.availability)}>
          {sourceAvailabilityLabel(source.availability)}
        </Tag>
      </div>

      <div className="ai-overview-agent-source-count">
        <Typography.Text>
          {t('pages.ai.overview.agents.visible', 'Visible profiles')}
        </Typography.Text>
        <Typography.Title level={3}>{count}</Typography.Title>
      </div>

      <div className="ai-overview-source-freshness">
        {source.authorityStateVersion !== null ? (
          <Typography.Text>
            {t('pages.ai.overview.stateVersion', 'State version {version}', {
              version: source.authorityStateVersion,
            })}
          </Typography.Text>
        ) : null}
        {updatedAt ? (
          <Typography.Text>
            {t('pages.ai.overview.updatedAt', 'Updated {updatedAt}', {
              updatedAt,
            })}
          </Typography.Text>
        ) : null}
      </div>

      {source.error ? (
        <Typography.Text className="ai-overview-source-error" type="danger">
          {source.error.message}
        </Typography.Text>
      ) : null}
    </article>
  );
}

function ActivityAvailabilityTag({
  availability,
}: {
  availability: AIWorkspaceActivityCollectionAvailability;
}): React.ReactElement {
  return (
    <Tag color={availability === 'available' ? 'success' : 'error'}>
      {availability === 'available'
        ? t('pages.ai.overview.availability.available', 'Available')
        : t('pages.ai.overview.availability.unavailable', 'Unavailable')}
    </Tag>
  );
}

function ConversationRow({
  conversation,
}: {
  conversation: AIWorkspaceConversationSummary;
}): React.ReactElement {
  const title =
    conversation.title.trim() ||
    t('pages.ai.overview.conversations.untitled', 'Untitled conversation');
  const href = buildAIChatHref(conversation.conversationId);
  const updatedAt = formatUtcDateTime(conversation.updatedAtUtc, '');

  return (
    <a
      aria-label={t(
        'pages.ai.overview.conversations.open',
        'Open conversation {title}',
        { title },
      )}
      className="ai-overview-conversation-row"
      href={href}
      onClick={(event) => navigateFromAnchor(event, href)}
    >
      <div className="ai-overview-conversation-copy">
        <Typography.Text
          className="ai-overview-conversation-title"
          ellipsis={{ tooltip: title }}
          strong
        >
          {title}
        </Typography.Text>
        <Typography.Text className="ai-overview-conversation-meta">
          {t('pages.ai.overview.conversations.messages', '{count} messages', {
            count: conversation.messageCount,
          })}{' '}
          |{' '}
          {humanizeOr(
            conversation.serviceKind,
            t(
              'pages.ai.overview.conversations.service.unknown',
              'Unknown service',
            ),
          )}
        </Typography.Text>
        {conversation.activeStepSummary ? (
          <Typography.Text
            className="ai-overview-conversation-step"
            ellipsis={{ tooltip: conversation.activeStepSummary }}
          >
            {conversation.activeStepSummary}
          </Typography.Text>
        ) : null}
      </div>
      <div className="ai-overview-conversation-status">
        {conversation.attentionKind ? (
          <Tag color="warning">{humanize(conversation.attentionKind)}</Tag>
        ) : conversation.taskStatus ? (
          <Tag>{humanize(conversation.taskStatus)}</Tag>
        ) : null}
      </div>
      <div className="ai-overview-conversation-updated">
        <Typography.Text>{updatedAt}</Typography.Text>
        <Typography.Text>
          {t('pages.ai.overview.version', 'v{version}', {
            version: conversation.authorityStateVersion,
          })}
        </Typography.Text>
      </div>
      <ArrowRightOutlined aria-hidden="true" />
    </a>
  );
}

function ConversationsSection({
  collection,
  onRetry,
}: {
  collection: AIWorkspaceConversationCollection;
  onRetry: () => void;
}): React.ReactElement {
  let content: React.ReactNode;
  if (collection.availability === 'unavailable') {
    content = (
      <InventoryReadinessState
        action={{
          label: t('pages.ai.overview.retry', 'Retry'),
          onClick: onRetry,
        }}
        description={
          collection.error?.message ||
          t(
            'pages.ai.overview.conversations.unavailable.description',
            'Recent conversations could not be read.',
          )
        }
        kind="error"
        title={t(
          'pages.ai.overview.conversations.unavailable.title',
          'Conversations unavailable',
        )}
      />
    );
  } else if (collection.items.length === 0) {
    content = (
      <InventoryReadinessState
        description={t(
          'pages.ai.overview.conversations.empty.description',
          'Start a conversation to see it here.',
        )}
        kind="empty"
        title={t(
          'pages.ai.overview.conversations.empty.title',
          'No conversations yet',
        )}
      />
    );
  } else {
    content = (
      <div className="ai-overview-conversation-list">
        {collection.items.map((conversation) => (
          <ConversationRow
            conversation={conversation}
            key={conversation.conversationId}
          />
        ))}
      </div>
    );
  }

  return (
    <section
      aria-labelledby="ai-overview-conversations-title"
      className="ai-overview-section"
    >
      <div className="ai-overview-section-header">
        <div>
          <Typography.Title id="ai-overview-conversations-title" level={2}>
            {t('pages.ai.overview.conversations.title', 'Recent conversations')}
          </Typography.Title>
          <Typography.Text type="secondary">
            {t(
              'pages.ai.overview.conversations.source',
              'Chat history read model',
            )}
          </Typography.Text>
        </div>
        <ActivityAvailabilityTag availability={collection.availability} />
      </div>
      {content}
    </section>
  );
}

function runStatusColor(run: AIWorkspaceRunSummary): string {
  if (run.success === true) {
    return 'success';
  }
  if (run.success === false || run.firstFailure) {
    return 'error';
  }
  if (run.waiting) {
    return 'warning';
  }
  return run.status.toLowerCase().includes('running')
    ? 'processing'
    : 'default';
}

function RunRow({ run }: { run: AIWorkspaceRunSummary }): React.ReactElement {
  const updatedAt = formatUtcDateTime(run.updatedAtUtc, '');
  const workflowName =
    run.workflowName.trim() ||
    t('pages.ai.overview.runs.workflow.unnamed', 'Unnamed workflow');
  const detail =
    run.inputSummary.trim() ||
    run.currentStep?.inputSummary.trim() ||
    t('pages.ai.overview.runs.input.empty', 'No input summary');

  return (
    <article className="ai-overview-run-row">
      <div className="ai-overview-run-copy">
        <Typography.Text
          className="ai-overview-run-title"
          ellipsis={{ tooltip: workflowName }}
          strong
        >
          {workflowName}
        </Typography.Text>
        <Typography.Text
          className="ai-overview-run-summary"
          ellipsis={{ tooltip: detail }}
        >
          {detail}
        </Typography.Text>
        <Typography.Text className="ai-overview-run-meta">
          {humanizeOr(
            run.runOrigin,
            t('pages.ai.overview.runs.origin.unknown', 'Unknown origin'),
          )}
        </Typography.Text>
      </div>
      <div className="ai-overview-run-status">
        <Tag color={runStatusColor(run)}>
          {humanizeOr(
            run.status,
            t('pages.ai.overview.runs.status.unknown', 'Unknown status'),
          )}
        </Tag>
        {run.waiting ? (
          <Tag color="warning">{humanize(run.waiting.waitingKind)}</Tag>
        ) : null}
      </div>
      <div className="ai-overview-run-updated">
        <Typography.Text>{updatedAt}</Typography.Text>
        <Typography.Text>
          {t('pages.ai.overview.version', 'v{version}', {
            version: run.authorityStateVersion,
          })}
        </Typography.Text>
      </div>
    </article>
  );
}

function RunsSection({
  collection,
  onRetry,
}: {
  collection: AIWorkspaceRunCollection;
  onRetry: () => void;
}): React.ReactElement {
  let content: React.ReactNode;
  if (collection.availability === 'unavailable') {
    content = (
      <InventoryReadinessState
        action={{
          label: t('pages.ai.overview.retry', 'Retry'),
          onClick: onRetry,
        }}
        description={
          collection.error?.message ||
          t(
            'pages.ai.overview.runs.unavailable.description',
            'Recent workflow runs could not be read.',
          )
        }
        kind="error"
        title={t(
          'pages.ai.overview.runs.unavailable.title',
          'Runs unavailable',
        )}
      />
    );
  } else if (collection.items.length === 0) {
    content = (
      <InventoryReadinessState
        description={t(
          'pages.ai.overview.runs.empty.description',
          'Workflow runs will appear here after they start.',
        )}
        kind="empty"
        title={t('pages.ai.overview.runs.empty.title', 'No recent runs')}
      />
    );
  } else {
    content = (
      <div className="ai-overview-run-list">
        {collection.items.map((run) => (
          <RunRow key={run.runId} run={run} />
        ))}
      </div>
    );
  }

  return (
    <section
      aria-labelledby="ai-overview-runs-title"
      className="ai-overview-section"
    >
      <div className="ai-overview-section-header">
        <div>
          <Typography.Title id="ai-overview-runs-title" level={2}>
            {t('pages.ai.overview.runs.title', 'Recent runs')}
          </Typography.Title>
          <Typography.Text type="secondary">
            {t('pages.ai.overview.runs.source', 'Workflow run observatory')}
          </Typography.Text>
        </div>
        <ActivityAvailabilityTag availability={collection.availability} />
      </div>
      {content}
    </section>
  );
}

export function AIOverviewContent(): React.ReactElement {
  const { context, queryAuthority, scopeId } = useAIWorkspaceContext();
  const overviewDeclared =
    context.pages.overview === AI_OVERVIEW_ROUTE &&
    context.apis.overview === '/api/ai/overview' &&
    context.features.overview?.availability === 'available' &&
    context.features.overview.page === AI_OVERVIEW_ROUTE &&
    context.features.overview.api === context.apis.overview;
  const overviewQuery = useQuery({
    enabled: overviewDeclared,
    queryFn: ({ signal }) =>
      aiWorkspaceApi.getOverview({ take: OVERVIEW_TAKE }, signal),
    queryKey: aiWorkspaceQueryKeys.overview({
      ...queryAuthority,
      scopeId,
    }),
    retry: false,
  });
  const scopeMismatch = Boolean(
    overviewQuery.data &&
      (overviewQuery.data.recentConversations.scopeId !== scopeId ||
        overviewQuery.data.recentRuns.scopeId !== scopeId),
  );

  if (!overviewDeclared) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.overview.notAvailable.description',
            'The backend has not enabled the Overview contract for this workspace.',
          )}
          kind="empty"
          title={t(
            'pages.ai.overview.notAvailable.title',
            'Overview not available',
          )}
        />
      </div>
    );
  }

  if (overviewQuery.isLoading) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.overview.loading.description',
            'Reading Agent, Conversation, and Run sources.',
          )}
          kind="loading"
          title={t('pages.ai.overview.loading.title', 'Loading Overview')}
        />
      </div>
    );
  }

  if (overviewQuery.isError || !overviewQuery.data || scopeMismatch) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.overview.retry', 'Retry'),
            onClick: () => void overviewQuery.refetch(),
          }}
          description={
            scopeMismatch
              ? t(
                  'pages.ai.overview.scopeMismatch.description',
                  'The Overview sources did not match the authenticated workspace scope.',
                )
              : describeError(
                  overviewQuery.error,
                  t(
                    'pages.ai.overview.error.description',
                    'The AI workspace Overview could not be loaded.',
                  ),
                )
          }
          kind="error"
          title={
            scopeMismatch
              ? t(
                  'pages.ai.overview.scopeMismatch.title',
                  'Overview scope mismatch',
                )
              : t('pages.ai.overview.error.title', 'Overview unavailable')
          }
        />
      </div>
    );
  }

  const overview = overviewQuery.data;
  const agentsHref = AI_AGENTS_ROUTE;
  const chatHref = buildAIChatHref();
  const retry = () => void overviewQuery.refetch();

  return (
    <div className="ai-page ai-overview-page">
      <div className="ai-page-heading">
        <div>
          <Typography.Title level={1}>
            {t('pages.ai.overview.title', 'Overview')}
          </Typography.Title>
          <Typography.Text className="ai-page-scope">
            {t('pages.ai.overview.scope', 'Scope {scopeId}', { scopeId })}
          </Typography.Text>
        </div>
        <Tooltip title={t('pages.ai.overview.refresh', 'Refresh Overview')}>
          <Button
            aria-label={t('pages.ai.overview.refresh', 'Refresh Overview')}
            icon={<ReloadOutlined />}
            loading={overviewQuery.isFetching}
            onClick={retry}
          />
        </Tooltip>
      </div>

      <div className="ai-overview-content">
        <section className="ai-overview-command-band">
          <div className="ai-overview-command-copy">
            <Typography.Title className="ai-overview-command-title" level={2}>
              {t('pages.ai.overview.command.title', 'Start a new conversation')}
            </Typography.Title>
            <Typography.Paragraph className="ai-overview-command-description">
              {t(
                'pages.ai.overview.command.description',
                'Work with the agents and models available in this scope.',
              )}
            </Typography.Paragraph>
          </div>
          <Button
            href={chatHref}
            icon={<MessageOutlined />}
            onClick={(event) => navigateFromAnchor(event, chatHref)}
            size="large"
            type="primary"
          >
            {t('pages.ai.overview.command.newChat', 'New Chat')}
          </Button>
        </section>

        <section
          aria-labelledby="ai-overview-agents-title"
          className="ai-overview-section"
        >
          <div className="ai-overview-section-header">
            <div>
              <Typography.Title id="ai-overview-agents-title" level={2}>
                {t('pages.ai.overview.agents.title', 'Agent readiness')}
              </Typography.Title>
              <Typography.Text type="secondary">
                {t(
                  'pages.ai.overview.agents.description',
                  'Owned profiles and system templates are independent catalogs.',
                )}
              </Typography.Text>
            </div>
            <Button
              href={agentsHref}
              icon={<ArrowRightOutlined />}
              onClick={(event) => navigateFromAnchor(event, agentsHref)}
            >
              {t('pages.ai.overview.agents.open', 'Open Agents')}
            </Button>
          </div>
          <div className="ai-overview-agent-grid">
            <AgentReadinessCard
              label={t('pages.ai.overview.agents.owned', 'My Agents')}
              source={overview.agents.owned}
            />
            <AgentReadinessCard
              label={t('pages.ai.overview.agents.system', 'System Templates')}
              source={overview.agents.systemTemplates}
            />
          </div>
        </section>

        <ConversationsSection
          collection={overview.recentConversations}
          onRetry={retry}
        />
        <RunsSection collection={overview.recentRuns} onRetry={retry} />
      </div>
    </div>
  );
}

const AIOverviewPage: React.FC = () => (
  <AIWorkspaceShell>
    <AIOverviewContent />
  </AIWorkspaceShell>
);

export default AIOverviewPage;
