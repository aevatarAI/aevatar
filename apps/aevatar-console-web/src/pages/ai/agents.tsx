import {
  LeftOutlined,
  ReloadOutlined,
  RightOutlined,
  RobotOutlined,
} from '@ant-design/icons';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Button, Tag, Tooltip, Typography } from 'antd';
import React from 'react';
import {
  type AIWorkspaceAgentCollection,
  type AIWorkspaceAgentStatus,
  aiWorkspaceApi,
} from '@/shared/api/aiWorkspaceApi';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import { AI_AGENTS_ROUTE } from '@/shared/navigation/aiRoutes';
import { aiWorkspaceQueryKeys } from '@/shared/query/aiWorkspaceQueryKeys';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from './components/AIWorkspaceShell';
import './index.less';

const AGENT_PAGE_SIZE = 24;

type CursorTrail = {
  current?: string;
  hasPrevious: boolean;
  page: number;
  next: (cursor: string) => void;
  previous: () => void;
};

type AgentCollectionSectionProps = {
  collection: AIWorkspaceAgentCollection;
  cursor: CursorTrail;
  isFetching: boolean;
  kind: 'owned' | 'system';
  onRetry: () => void;
};

function useCursorTrail(resetKey: string): CursorTrail {
  const [trail, setTrail] = React.useState<string[]>(['']);

  React.useEffect(() => {
    setTrail(['']);
  }, [resetKey]);

  return {
    current: trail[trail.length - 1] || undefined,
    hasPrevious: trail.length > 1,
    page: trail.length,
    next: (cursor) => {
      const normalized = cursor.trim();
      if (normalized) {
        setTrail((current) => [...current, normalized]);
      }
    },
    previous: () => {
      setTrail((current) =>
        current.length > 1 ? current.slice(0, -1) : current,
      );
    },
  };
}

function statusLabel(status: AIWorkspaceAgentStatus): string {
  switch (status) {
    case 'active':
      return t('pages.ai.agents.status.active', 'Active');
    case 'failed':
      return t('pages.ai.agents.status.failed', 'Failed');
    case 'provisioning':
      return t('pages.ai.agents.status.provisioning', 'Provisioning');
    default:
      return t('pages.ai.agents.status.unspecified', 'Unknown');
  }
}

function statusColor(status: AIWorkspaceAgentStatus): string | undefined {
  switch (status) {
    case 'active':
      return 'green';
    case 'failed':
      return 'red';
    case 'provisioning':
      return 'blue';
    default:
      return undefined;
  }
}

function collectionTitle(kind: AgentCollectionSectionProps['kind']): string {
  return kind === 'owned'
    ? t('pages.ai.agents.owned.title', 'My Agents')
    : t('pages.ai.agents.system.title', 'System Templates');
}

function collectionEmptyTitle(
  kind: AgentCollectionSectionProps['kind'],
): string {
  return kind === 'owned'
    ? t('pages.ai.agents.owned.empty.title', 'No agents yet')
    : t('pages.ai.agents.system.empty.title', 'No system templates available');
}

function AgentCard({
  agent,
  readOnly,
}: {
  agent: AIWorkspaceAgentCollection['items'][number];
  readOnly: boolean;
}): React.ReactElement {
  const displayName = agent.displayName.trim() || agent.profileSlug;

  return (
    <article className="ai-agent-card">
      <div className="ai-agent-card-heading">
        <span aria-hidden="true" className="ai-agent-card-icon">
          <RobotOutlined />
        </span>
        <div className="ai-agent-card-title-group">
          <Typography.Title className="ai-agent-card-title" level={3}>
            {displayName}
          </Typography.Title>
          <Typography.Text className="ai-agent-card-slug">
            {agent.profileSlug}
          </Typography.Text>
        </div>
        <Tag color={statusColor(agent.status)}>{statusLabel(agent.status)}</Tag>
      </div>

      <Typography.Paragraph className="ai-agent-card-purpose">
        {agent.purpose ||
          t('pages.ai.agents.purpose.empty', 'No purpose provided')}
      </Typography.Paragraph>

      <dl className="ai-agent-card-facts">
        <div>
          <dt>{t('pages.ai.agents.revision', 'Published revision')}</dt>
          <dd>
            {agent.publishedRevision > 0
              ? agent.publishedRevision
              : t('pages.ai.agents.revision.unpublished', 'Not published')}
          </dd>
        </div>
      </dl>

      <div className="ai-agent-card-footer">
        {agent.publishedSnapshotSha256 ? (
          <Typography.Text
            className="ai-agent-card-digest"
            ellipsis={{ tooltip: agent.publishedSnapshotSha256 }}
          >
            SHA-256 {agent.publishedSnapshotSha256}
          </Typography.Text>
        ) : (
          <span />
        )}
        {readOnly ? (
          <Tag>{t('pages.ai.agents.system.readOnly', 'Read only')}</Tag>
        ) : null}
      </div>
    </article>
  );
}

function AgentCollectionPager({
  collection,
  cursor,
  disabled,
}: {
  collection: AIWorkspaceAgentCollection;
  cursor: CursorTrail;
  disabled: boolean;
}): React.ReactElement | null {
  if (!cursor.hasPrevious && !collection.nextCursor) {
    return null;
  }

  return (
    <div className="ai-agent-pagination">
      <Tooltip
        title={t('pages.ai.agents.pagination.previous', 'Previous page')}
      >
        <Button
          aria-label={t('pages.ai.agents.pagination.previous', 'Previous page')}
          disabled={disabled || !cursor.hasPrevious}
          icon={<LeftOutlined />}
          onClick={cursor.previous}
        />
      </Tooltip>
      <Typography.Text className="ai-agent-pagination-label">
        {t('pages.ai.agents.pagination.page', 'Page {page}', {
          page: cursor.page,
        })}
      </Typography.Text>
      <Tooltip title={t('pages.ai.agents.pagination.next', 'Next page')}>
        <Button
          aria-label={t('pages.ai.agents.pagination.next', 'Next page')}
          disabled={disabled || !collection.nextCursor}
          icon={<RightOutlined />}
          onClick={() => {
            if (collection.nextCursor) {
              cursor.next(collection.nextCursor);
            }
          }}
        />
      </Tooltip>
    </div>
  );
}

function AgentCollectionSection({
  collection,
  cursor,
  isFetching,
  kind,
  onRetry,
}: AgentCollectionSectionProps): React.ReactElement {
  const titleId = `ai-agents-${kind}-title`;
  const title = collectionTitle(kind);
  const updatedAt = formatUtcDateTime(collection.updatedAtUtc, '');

  let content: React.ReactNode;
  if (collection.availability === 'unavailable') {
    content = (
      <InventoryReadinessState
        action={{
          label: t('pages.ai.agents.retry', 'Retry'),
          onClick: onRetry,
        }}
        description={
          collection.error?.message ||
          t(
            'pages.ai.agents.collection.unavailable.description',
            'This Agent Profile catalog could not be loaded.',
          )
        }
        kind="error"
        title={t(
          'pages.ai.agents.collection.unavailable.title',
          'Agent catalog unavailable',
        )}
      />
    );
  } else if (collection.availability === 'not_materialized') {
    content = (
      <InventoryReadinessState
        description={t(
          'pages.ai.agents.collection.notMaterialized.description',
          'The Agent Profile catalog has not been materialized for this owner.',
        )}
        kind="empty"
        title={t(
          'pages.ai.agents.collection.notMaterialized.title',
          'Catalog not ready',
        )}
      />
    );
  } else if (collection.items.length === 0) {
    content = (
      <InventoryReadinessState
        description={t(
          'pages.ai.agents.collection.empty.description',
          'No published Agent Profiles are visible in this catalog page.',
        )}
        kind="empty"
        title={collectionEmptyTitle(kind)}
      />
    );
  } else {
    content = (
      <div className="ai-agent-grid">
        {collection.items.map((agent) => (
          <AgentCard
            agent={agent}
            key={agent.profileId}
            readOnly={kind === 'system'}
          />
        ))}
      </div>
    );
  }

  return (
    <section aria-labelledby={titleId} className="ai-agent-section">
      <div className="ai-agent-section-header">
        <div>
          <div className="ai-agent-section-title-row">
            <Typography.Title id={titleId} level={2}>
              {title}
            </Typography.Title>
            <Tag>{collection.totalCount ?? collection.items.length}</Tag>
            {kind === 'system' ? (
              <Tag>{t('pages.ai.agents.system.readOnly', 'Read only')}</Tag>
            ) : null}
          </div>
          <div className="ai-agent-section-freshness">
            {collection.authorityStateVersion !== null ? (
              <Typography.Text>
                {t('pages.ai.agents.stateVersion', 'State version {version}', {
                  version: collection.authorityStateVersion,
                })}
              </Typography.Text>
            ) : null}
            {updatedAt ? (
              <Typography.Text>
                {t('pages.ai.agents.updatedAt', 'Updated {updatedAt}', {
                  updatedAt,
                })}
              </Typography.Text>
            ) : null}
          </div>
        </div>
        <AgentCollectionPager
          collection={collection}
          cursor={cursor}
          disabled={isFetching}
        />
      </div>
      {content}
    </section>
  );
}

export function AIAgentsContent(): React.ReactElement {
  const { context, queryAuthority, scopeId } = useAIWorkspaceContext();
  const agentsDeclared =
    context.pages.agents === AI_AGENTS_ROUTE &&
    context.apis.agents === '/api/ai/agents' &&
    context.features.agents?.availability === 'available' &&
    context.features.agents.page === AI_AGENTS_ROUTE &&
    context.features.agents.api === context.apis.agents;
  const ownedCursor = useCursorTrail(scopeId);
  const systemCursor = useCursorTrail(scopeId);
  const queryInput = {
    ownedCursor: ownedCursor.current,
    systemCursor: systemCursor.current,
    take: AGENT_PAGE_SIZE,
  };
  const agentsQuery = useQuery({
    enabled: agentsDeclared,
    placeholderData: keepPreviousData,
    queryFn: ({ signal }) => aiWorkspaceApi.getAgents(queryInput, signal),
    queryKey: aiWorkspaceQueryKeys.agents(
      { ...queryAuthority, scopeId },
      queryInput,
    ),
    retry: false,
  });
  const scopeMismatch = Boolean(
    agentsQuery.data &&
      (agentsQuery.data.owned.scopeId !== scopeId ||
        agentsQuery.data.systemTemplates.scopeId !== null),
  );

  if (!agentsDeclared) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.agents.notAvailable.description',
            'The backend has not enabled the Agents contract for this workspace.',
          )}
          kind="empty"
          title={t(
            'pages.ai.agents.notAvailable.title',
            'Agents not available',
          )}
        />
      </div>
    );
  }

  if (agentsQuery.isLoading && !agentsQuery.data) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          description={t(
            'pages.ai.agents.loading.description',
            'Loading owned Agent Profiles and system templates.',
          )}
          kind="loading"
          title={t('pages.ai.agents.loading.title', 'Loading Agents')}
        />
      </div>
    );
  }

  if (agentsQuery.isError || !agentsQuery.data || scopeMismatch) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.agents.retry', 'Retry'),
            onClick: () => void agentsQuery.refetch(),
          }}
          description={
            scopeMismatch
              ? t(
                  'pages.ai.agents.scopeMismatch.description',
                  'The Agent Profile catalog did not match the authenticated workspace scope.',
                )
              : describeError(
                  agentsQuery.error,
                  t(
                    'pages.ai.agents.error.description',
                    'The Agent Profile catalogs could not be loaded.',
                  ),
                )
          }
          kind="error"
          title={
            scopeMismatch
              ? t(
                  'pages.ai.agents.scopeMismatch.title',
                  'Agent catalog scope mismatch',
                )
              : t('pages.ai.agents.error.title', 'Agents unavailable')
          }
        />
      </div>
    );
  }

  return (
    <div className="ai-page ai-agents-page">
      <div className="ai-page-heading">
        <div>
          <Typography.Title level={1}>
            {t('pages.ai.agents.title', 'Agents')}
          </Typography.Title>
          <Typography.Text className="ai-page-scope">
            {t('pages.ai.agents.scope', 'Scope {scopeId}', { scopeId })}
          </Typography.Text>
        </div>
        <Button
          icon={<ReloadOutlined />}
          loading={agentsQuery.isFetching}
          onClick={() => void agentsQuery.refetch()}
        >
          {t('pages.ai.agents.refresh', 'Refresh')}
        </Button>
      </div>

      <AgentCollectionSection
        collection={agentsQuery.data.owned}
        cursor={ownedCursor}
        isFetching={agentsQuery.isFetching}
        kind="owned"
        onRetry={() => void agentsQuery.refetch()}
      />
      <AgentCollectionSection
        collection={agentsQuery.data.systemTemplates}
        cursor={systemCursor}
        isFetching={agentsQuery.isFetching}
        kind="system"
        onRetry={() => void agentsQuery.refetch()}
      />
    </div>
  );
}

const AIAgentsPage: React.FC = () => (
  <AIWorkspaceShell>
    <AIAgentsContent />
  </AIWorkspaceShell>
);

export default AIAgentsPage;
