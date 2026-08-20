import {
  ArrowLeftOutlined,
  CheckCircleOutlined,
  CloseCircleOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Empty,
  Skeleton,
  Space,
  Tabs,
  Tag,
  Tooltip,
  Typography,
} from 'antd';
import React from 'react';
import {
  AIWorkspaceApiError,
  type AIWorkspaceRunDetail,
  type AIWorkspaceRunDetailSectionVersion,
  type AIWorkspaceRunOperation,
  type AIWorkspaceRunStepDetail,
  type AIWorkspaceRunTimelineEvent,
  type AIWorkspaceUsageTotals,
  aiWorkspaceApi,
} from '@/shared/api/aiWorkspaceApi';
import { formatUtcDateTime } from '@/shared/datetime/dateTime';
import { t } from '@/shared/i18n/messages';
import {
  AI_ACTIVITY_ROUTE,
  parseAIActivityRunDetailPath,
} from '@/shared/navigation/aiRoutes';
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';
import { aiWorkspaceQueryKeys } from '@/shared/query/aiWorkspaceQueryKeys';
import { describeError } from '@/shared/ui/errorText';
import InventoryReadinessState from '@/shared/ui/InventoryReadinessState';
import AIWorkspaceShell, {
  useAIWorkspaceContext,
} from '../components/AIWorkspaceShell';
import './activity.less';

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
    default:
      return 'default';
  }
}

function runStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    completed: t('pages.ai.activity.status.completed', 'Completed'),
    failed: t('pages.ai.activity.status.failed', 'Failed'),
    running: t('pages.ai.activity.status.running', 'Running'),
    stopped: t('pages.ai.activity.status.stopped', 'Stopped'),
    timed_out: t('pages.ai.activity.status.timedOut', 'Timed out'),
  };
  return (
    labels[status.trim().toLowerCase()] ??
    t('pages.ai.activity.status.unknown', 'Unknown')
  );
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

function OutcomeTag({
  success,
  value,
}: {
  success: boolean | null;
  value: string;
}): React.ReactElement {
  return (
    <Tag
      color={
        success === true ? 'success' : success === false ? 'error' : 'default'
      }
      icon={
        success === true ? (
          <CheckCircleOutlined />
        ) : success === false ? (
          <CloseCircleOutlined />
        ) : undefined
      }
    >
      {humanize(value)}
    </Tag>
  );
}

function UsageSummary({
  usage,
}: {
  usage: AIWorkspaceUsageTotals;
}): React.ReactElement {
  return (
    <dl className="ai-run-detail-usage">
      <div>
        <dt>{t('pages.ai.runDetail.usage.prompt', 'Prompt tokens')}</dt>
        <dd>{usage.promptTokens.toLocaleString()}</dd>
      </div>
      <div>
        <dt>{t('pages.ai.runDetail.usage.completion', 'Completion tokens')}</dt>
        <dd>{usage.completionTokens.toLocaleString()}</dd>
      </div>
      <div>
        <dt>{t('pages.ai.runDetail.usage.total', 'Total tokens')}</dt>
        <dd>{usage.totalTokens.toLocaleString()}</dd>
      </div>
      <div>
        <dt>{t('pages.ai.runDetail.usage.cost', 'Recorded cost value')}</dt>
        <dd>{usage.cost.toLocaleString()}</dd>
      </div>
    </dl>
  );
}

function SectionFreshness({
  label,
  section,
}: {
  label: string;
  section: AIWorkspaceRunDetailSectionVersion;
}): React.ReactElement {
  const warning =
    section.versionStatus === 'unavailable' ||
    section.versionStatus === 'version_mismatch' ||
    section.versionStatus === 'unknown';
  return (
    <div className="ai-run-detail-freshness-row">
      <div>
        <Typography.Text strong>{label}</Typography.Text>
        <Typography.Text type="secondary">
          {t(
            'pages.ai.runDetail.freshness.versions',
            'Detail {detailVersion} · source {sourceVersion}',
            {
              detailVersion: section.detailStateVersion,
              sourceVersion: section.sourceStateVersion,
            },
          )}
        </Typography.Text>
      </div>
      <Tag color={warning ? 'warning' : 'success'}>
        {section.versionStatus === 'aligned'
          ? t('pages.ai.runDetail.freshness.aligned', 'Aligned')
          : section.versionStatus === 'version_mismatch'
            ? t('pages.ai.runDetail.freshness.mismatch', 'Version mismatch')
            : section.versionStatus === 'unavailable'
              ? t('pages.ai.runDetail.freshness.unavailable', 'Unavailable')
              : t('pages.ai.runDetail.freshness.unknown', 'Unknown')}
      </Tag>
      {warning && section.reason?.trim() ? (
        <Alert description={section.reason} showIcon type="warning" />
      ) : null}
    </div>
  );
}

function TimelineList({
  events,
}: {
  events: AIWorkspaceRunTimelineEvent[];
}): React.ReactElement {
  if (!events.length) {
    return (
      <Empty
        description={t(
          'pages.ai.runDetail.timeline.empty',
          'No timeline events observed.',
        )}
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    );
  }
  return (
    <ol className="ai-run-detail-timeline">
      {events.map((event) => (
        <li
          key={`${event.timestampUtc}-${event.kind}-${event.stage}-${event.stepId}-${event.toolCall?.callId ?? ''}`}
        >
          <div className="ai-run-detail-timeline-marker" />
          <div className="ai-run-detail-timeline-content">
            <div className="ai-run-detail-item-heading">
              <Typography.Text strong>{humanize(event.kind)}</Typography.Text>
              <Typography.Text type="secondary">
                {formatUtcDateTime(
                  event.timestampUtc,
                  t('pages.ai.activity.value.unavailable', 'Unavailable'),
                )}
              </Typography.Text>
            </div>
            <Space size={[6, 4]} wrap>
              {event.stage.trim() ? <Tag>{humanize(event.stage)}</Tag> : null}
              {event.stepId.trim() ? (
                <Typography.Text code>{event.stepId}</Typography.Text>
              ) : null}
              {event.toolCall ? (
                <Tag color={event.toolCall.success ? 'success' : 'error'}>
                  {event.toolCall.toolName ||
                    t('pages.ai.runDetail.toolCall', 'Tool call')}
                </Tag>
              ) : null}
            </Space>
          </div>
        </li>
      ))}
    </ol>
  );
}

function OperationList({
  operations,
}: {
  operations: AIWorkspaceRunOperation[];
}): React.ReactElement {
  if (!operations.length) {
    return (
      <Empty
        description={t(
          'pages.ai.runDetail.operations.empty',
          'No operations observed.',
        )}
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    );
  }
  return (
    <div className="ai-run-detail-item-list">
      {operations.map((operation) => (
        <article
          className="ai-run-detail-item"
          key={`${operation.operationId}-${operation.kind}-${operation.startedAtUtc ?? ''}-${operation.toolCallId}`}
        >
          <div className="ai-run-detail-item-heading">
            <Space size={8} wrap>
              <Typography.Text strong>
                {humanize(operation.kind)}
              </Typography.Text>
              <OutcomeTag
                success={operation.success}
                value={operation.finishReason}
              />
            </Space>
            <Typography.Text type="secondary">
              {formatDuration(operation.durationMs)}
            </Typography.Text>
          </div>
          <dl className="ai-run-detail-item-facts">
            <div>
              <dt>{t('pages.ai.runDetail.operations.provider', 'Provider')}</dt>
              <dd>{operation.provider.trim() || humanize('')}</dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.operations.model', 'Model')}</dt>
              <dd>{operation.model.trim() || humanize('')}</dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.started', 'Started')}</dt>
              <dd>
                {formatUtcDateTime(
                  operation.startedAtUtc,
                  t('pages.ai.activity.value.unavailable', 'Unavailable'),
                )}
              </dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.completed', 'Completed')}</dt>
              <dd>
                {formatUtcDateTime(
                  operation.completedAtUtc,
                  t('pages.ai.activity.value.unavailable', 'Unavailable'),
                )}
              </dd>
            </div>
          </dl>
          {operation.toolName.trim() ? (
            <Typography.Text>
              {t('pages.ai.runDetail.operations.tool', 'Tool: {tool}', {
                tool: operation.toolName,
              })}
            </Typography.Text>
          ) : null}
          {operation.availableToolNames.length ? (
            <Space size={[4, 4]} wrap>
              {operation.availableToolNames.map((toolName) => (
                <Tag key={toolName}>{toolName}</Tag>
              ))}
            </Space>
          ) : null}
          <UsageSummary usage={operation.usage} />
        </article>
      ))}
    </div>
  );
}

function StepList({
  steps,
}: {
  steps: AIWorkspaceRunStepDetail[];
}): React.ReactElement {
  if (!steps.length) {
    return (
      <Empty
        description={t('pages.ai.runDetail.steps.empty', 'No steps observed.')}
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    );
  }
  return (
    <div className="ai-run-detail-item-list">
      {steps.map((step) => (
        <article
          className="ai-run-detail-item"
          key={`${step.stepId}-${step.requestedAtUtc ?? ''}-${step.displayName}`}
        >
          <div className="ai-run-detail-item-heading">
            <Space size={8} wrap>
              <Typography.Text strong>
                {step.displayName.trim() || step.stepId || humanize('')}
              </Typography.Text>
              <OutcomeTag success={step.success} value={step.outcome} />
            </Space>
            <Typography.Text type="secondary">
              {formatDuration(step.durationMs)}
            </Typography.Text>
          </div>
          {step.stepId.trim() ? (
            <Typography.Text code copyable={{ text: step.stepId }}>
              {step.stepId}
            </Typography.Text>
          ) : null}
          <dl className="ai-run-detail-item-facts">
            <div>
              <dt>{t('pages.ai.runDetail.steps.requested', 'Requested')}</dt>
              <dd>
                {formatUtcDateTime(
                  step.requestedAtUtc,
                  t('pages.ai.activity.value.unavailable', 'Unavailable'),
                )}
              </dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.completed', 'Completed')}</dt>
              <dd>
                {formatUtcDateTime(
                  step.completedAtUtc,
                  t('pages.ai.activity.value.unavailable', 'Unavailable'),
                )}
              </dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.steps.next', 'Next step')}</dt>
              <dd>{step.nextStepId.trim() || humanize('')}</dd>
            </div>
            <div>
              <dt>{t('pages.ai.runDetail.steps.branch', 'Branch')}</dt>
              <dd>{step.branchKey.trim() || humanize('')}</dd>
            </div>
          </dl>
          {step.suspensionType.trim() ? (
            <Alert
              description={
                step.suspensionTimeoutSeconds === null
                  ? humanize(step.suspensionType)
                  : t(
                      'pages.ai.runDetail.steps.suspensionTimeout',
                      '{type} · timeout {seconds}s',
                      {
                        seconds: step.suspensionTimeoutSeconds,
                        type: humanize(step.suspensionType),
                      },
                    )
              }
              message={t('pages.ai.runDetail.steps.suspended', 'Suspension')}
              showIcon
              type="warning"
            />
          ) : null}
          {step.failureOutputTruncated ? (
            <Typography.Text type="warning">
              {t(
                'pages.ai.runDetail.steps.failureTruncated',
                'The recorded failure output was truncated.',
              )}
            </Typography.Text>
          ) : null}
          <UsageSummary usage={step.usage} />
        </article>
      ))}
    </div>
  );
}

function ResultSection({
  detail,
}: {
  detail: AIWorkspaceRunDetail;
}): React.ReactElement {
  const failure = detail.summary.firstFailure;
  return (
    <section
      aria-labelledby="ai-run-result-title"
      className="ai-run-detail-section"
    >
      <div className="ai-run-detail-section-heading">
        <Typography.Title id="ai-run-result-title" level={2}>
          {failure
            ? t('pages.ai.runDetail.result.failure', 'Failure')
            : t('pages.ai.runDetail.result.output', 'Result')}
        </Typography.Title>
        <Tag color={statusColor(detail.summary.status)}>
          {runStatusLabel(detail.summary.status)}
        </Tag>
      </div>
      {failure ? (
        <Alert
          description={failure.message || humanize('')}
          message={
            failure.stepId
              ? t('pages.ai.runDetail.result.failedStep', 'Step {stepId}', {
                  stepId: failure.stepId,
                })
              : t('pages.ai.runDetail.result.failure', 'Failure')
          }
          showIcon
          type="error"
        />
      ) : detail.finalOutput.trim() ? (
        <pre className="ai-run-detail-output">{detail.finalOutput}</pre>
      ) : (
        <Empty
          description={t(
            'pages.ai.runDetail.result.empty',
            'No final output was recorded.',
          )}
          image={Empty.PRESENTED_IMAGE_SIMPLE}
        />
      )}
    </section>
  );
}

function RunDetailView({
  detail,
  isRefreshing,
  onRefresh,
}: {
  detail: AIWorkspaceRunDetail;
  isRefreshing: boolean;
  onRefresh: () => void;
}): React.ReactElement {
  const summary = detail.summary;
  const workflowName =
    summary.workflowName.trim() ||
    t('pages.ai.activity.runs.untitled', 'Unnamed workflow run');
  return (
    <div className="ai-page ai-run-detail-page">
      <div className="ai-run-detail-toolbar">
        <Button
          icon={<ArrowLeftOutlined />}
          onClick={() => history.push(AI_ACTIVITY_ROUTE)}
        >
          {t('pages.ai.runDetail.back', 'Back to Activity')}
        </Button>
        <Tooltip title={t('pages.ai.runDetail.refresh', 'Refresh run detail')}>
          <Button
            aria-label={t('pages.ai.runDetail.refresh', 'Refresh run detail')}
            icon={<ReloadOutlined />}
            loading={isRefreshing}
            onClick={onRefresh}
          />
        </Tooltip>
      </div>
      <header className="ai-run-detail-header">
        <div className="ai-run-detail-title-group">
          <Space size={[8, 6]} wrap>
            <Tag color={statusColor(summary.status)}>
              {runStatusLabel(summary.status)}
            </Tag>
            <Tag>{humanize(summary.runOrigin)}</Tag>
          </Space>
          <Typography.Title level={1}>{workflowName}</Typography.Title>
          <Typography.Text code copyable={{ text: summary.runId }}>
            {summary.runId}
          </Typography.Text>
        </div>
        <div className="ai-run-detail-observation">
          <Typography.Text>
            {t(
              'pages.ai.runDetail.observedVersion',
              'Observed version {version}',
              { version: detail.authorityStateVersion },
            )}
          </Typography.Text>
          <Typography.Text type="secondary">
            {formatUtcDateTime(
              detail.updatedAtUtc,
              t('pages.ai.activity.value.unavailable', 'Unavailable'),
            )}
          </Typography.Text>
          {detail.reportVersion ? (
            <Typography.Text type="secondary">
              {t('pages.ai.runDetail.reportVersion', 'Report {version}', {
                version: detail.reportVersion,
              })}
            </Typography.Text>
          ) : null}
        </div>
      </header>

      <div className="ai-run-detail-content">
        <ResultSection detail={detail} />

        <dl className="ai-run-detail-facts">
          <div>
            <dt>{t('pages.ai.runDetail.started', 'Started')}</dt>
            <dd>
              {formatUtcDateTime(
                summary.startedAtUtc,
                t('pages.ai.activity.value.unavailable', 'Unavailable'),
              )}
            </dd>
          </div>
          <div>
            <dt>{t('pages.ai.runDetail.completed', 'Completed')}</dt>
            <dd>
              {formatUtcDateTime(
                summary.completedAtUtc,
                t('pages.ai.activity.value.unavailable', 'Unavailable'),
              )}
            </dd>
          </div>
          <div>
            <dt>{t('pages.ai.runDetail.duration', 'Recorded duration')}</dt>
            <dd>{formatDuration(summary.durationMs)}</dd>
          </div>
          <div>
            <dt>{t('pages.ai.runDetail.steps.total', 'Total steps')}</dt>
            <dd>{detail.statistics.totalSteps.toLocaleString()}</dd>
          </div>
        </dl>

        <section
          aria-labelledby="ai-run-usage-title"
          className="ai-run-detail-section"
        >
          <Typography.Title id="ai-run-usage-title" level={2}>
            {t('pages.ai.runDetail.usage.title', 'Recorded usage')}
          </Typography.Title>
          <UsageSummary usage={detail.usageTotals} />
        </section>

        <section className="ai-run-detail-section ai-run-detail-tabs-section">
          <Tabs
            items={[
              {
                children: <TimelineList events={detail.timeline} />,
                key: 'timeline',
                label: t('pages.ai.runDetail.timeline.title', 'Timeline'),
              },
              {
                children: <OperationList operations={detail.operations} />,
                key: 'operations',
                label: t('pages.ai.runDetail.operations.title', 'Operations'),
              },
              {
                children: <StepList steps={detail.steps} />,
                key: 'steps',
                label: t('pages.ai.runDetail.steps.title', 'Steps'),
              },
              {
                children: (
                  <div className="ai-run-detail-freshness">
                    <SectionFreshness
                      label={t(
                        'pages.ai.runDetail.freshness.overview',
                        'Overview',
                      )}
                      section={detail.sections.overview}
                    />
                    <SectionFreshness
                      label={t('pages.ai.runDetail.freshness.steps', 'Steps')}
                      section={detail.sections.steps}
                    />
                    <SectionFreshness
                      label={t(
                        'pages.ai.runDetail.freshness.timeline',
                        'Timeline',
                      )}
                      section={detail.sections.timeline}
                    />
                    <SectionFreshness
                      label={t(
                        'pages.ai.runDetail.freshness.executionPath',
                        'Execution path',
                      )}
                      section={detail.sections.executionPath}
                    />
                  </div>
                ),
                key: 'freshness',
                label: t('pages.ai.runDetail.freshness.title', 'Freshness'),
              },
            ]}
          />
        </section>
      </div>
    </div>
  );
}

export function AIRunDetailContent(): React.ReactElement {
  const { context, queryAuthority, scopeId } = useAIWorkspaceContext();
  const location = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    getLocationSnapshot,
  );
  const route = parseAIActivityRunDetailPath(location);
  const runId = route?.runId ?? '';
  const activityDeclared =
    context.pages.activity === AI_ACTIVITY_ROUTE &&
    context.features.activity?.availability === 'available' &&
    context.features.activity.page === AI_ACTIVITY_ROUTE &&
    context.features.activity.api === context.apis.activity &&
    context.apis.activity === '/api/ai/activity' &&
    context.apis.runs === '/api/ai/activity/runs';
  const query = useQuery({
    enabled: activityDeclared && Boolean(runId && scopeId),
    queryFn: ({ signal }) => aiWorkspaceApi.getRun(runId, signal),
    queryKey: aiWorkspaceQueryKeys.activityRunDetail(
      { ...queryAuthority, scopeId },
      runId,
    ),
    retry: false,
  });

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
  if (!route) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.runDetail.back', 'Back to Activity'),
            onClick: () => history.push(AI_ACTIVITY_ROUTE),
          }}
          description={t(
            'pages.ai.runDetail.invalid.description',
            'This run detail link does not contain a valid opaque run id.',
          )}
          kind="error"
          title={t('pages.ai.runDetail.invalid.title', 'Invalid run link')}
        />
      </div>
    );
  }
  if (query.isPending) {
    return (
      <div aria-busy="true" className="ai-page ai-run-detail-page">
        <Skeleton active paragraph={{ rows: 10 }} title />
      </div>
    );
  }
  if (query.isError) {
    const notFound =
      query.error instanceof AIWorkspaceApiError && query.error.status === 404;
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          action={
            notFound
              ? {
                  label: t('pages.ai.runDetail.back', 'Back to Activity'),
                  onClick: () => history.push(AI_ACTIVITY_ROUTE),
                }
              : {
                  label: t('pages.ai.activity.retry', 'Retry'),
                  onClick: () => void query.refetch(),
                }
          }
          description={
            notFound
              ? t(
                  'pages.ai.runDetail.notFound.description',
                  'The requested run is not visible in this authenticated scope.',
                )
              : describeError(
                  query.error,
                  t(
                    'pages.ai.runDetail.error.description',
                    'The run detail could not be loaded.',
                  ),
                )
          }
          kind="error"
          title={
            notFound
              ? t('pages.ai.runDetail.notFound.title', 'Run not found')
              : t('pages.ai.runDetail.error.title', 'Run detail unavailable')
          }
        />
      </div>
    );
  }
  if (query.data.scopeId !== scopeId || query.data.summary.runId !== runId) {
    return (
      <div className="ai-page-boundary">
        <InventoryReadinessState
          action={{
            label: t('pages.ai.runDetail.back', 'Back to Activity'),
            onClick: () => history.push(AI_ACTIVITY_ROUTE),
          }}
          description={t(
            'pages.ai.runDetail.identityMismatch.description',
            'The run detail did not match the authenticated scope and requested identity.',
          )}
          kind="error"
          title={t(
            'pages.ai.runDetail.identityMismatch.title',
            'Run identity mismatch',
          )}
        />
      </div>
    );
  }

  return (
    <RunDetailView
      detail={query.data}
      isRefreshing={query.isFetching}
      onRefresh={() => void query.refetch()}
    />
  );
}

const AIRunDetailPage: React.FC = () => (
  <AIWorkspaceShell>
    <AIRunDetailContent />
  </AIWorkspaceShell>
);

export default AIRunDetailPage;
