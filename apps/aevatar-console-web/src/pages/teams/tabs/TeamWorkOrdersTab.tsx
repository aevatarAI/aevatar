import { ArrowRightOutlined, ReloadOutlined } from '@ant-design/icons';
import { type InfiniteData, useInfiniteQuery } from '@tanstack/react-query';
import { useIntl } from '@umijs/max';
import {
  Alert,
  Button,
  Empty,
  Skeleton,
  Space,
  Tag,
  Typography,
  theme,
} from 'antd';
import React from 'react';
import {
  type WorkOrderListResult,
  type WorkOrderLifecycleStatus,
  workOrdersApi,
} from '@/shared/api/workOrdersApi';
import { formatCompactDateTime } from '@/shared/datetime/dateTime';
import { buildTeamWorkOrderDetailHref } from '@/shared/navigation/teamRoutes';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';

type Props = {
  readonly onNavigate: (href: string) => void;
  readonly scopeId: string;
  readonly teamId: string;
};

const statusColors: Record<WorkOrderLifecycleStatus, string> = {
  accepted: 'processing',
  ready: 'cyan',
  dispatch_pending: 'processing',
  running: 'blue',
  completed: 'success',
  failed: 'error',
  stopped: 'default',
  cancelled: 'default',
  timed_out: 'warning',
};

const responsiveStyle = `
.team-work-order-list-header,
.team-work-order-row {
  display: grid;
  gap: 16px;
  grid-template-columns: minmax(220px, 1.5fr) minmax(180px, 1fr) minmax(130px, 0.7fr) auto;
  min-width: 0;
}

.team-work-order-row > * {
  min-width: 0;
}

@media (max-width: 880px) {
  .team-work-order-list-header {
    display: none;
  }

  .team-work-order-row {
    grid-template-columns: minmax(0, 1fr) auto;
  }

  .team-work-order-assignment,
  .team-work-order-updated {
    grid-column: 1 / -1;
  }
}
`;

const TeamWorkOrdersTab: React.FC<Props> = ({
  onNavigate,
  scopeId,
  teamId,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const query = useInfiniteQuery<
    WorkOrderListResult,
    Error,
    InfiniteData<WorkOrderListResult>,
    readonly ['work-orders', string, 'team', string],
    string | undefined
  >({
    enabled: Boolean(scopeId && teamId),
    getNextPageParam: (lastPage) => lastPage.nextPageToken ?? undefined,
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      workOrdersApi.list({
        scopeId,
        teamId,
        ...(pageParam ? { pageToken: pageParam } : {}),
      }),
    queryKey: ['work-orders', scopeId, 'team', teamId] as const,
    retry: false,
  });
  const workOrders = query.data?.pages.flatMap((page) => page.workOrders) ?? [];

  const refreshLabel = intl.formatMessage({ id: 'workOrders.actions.refresh' });

  return (
    <AevatarPanel
      extra={
        <Button
          aria-label={refreshLabel}
          icon={<ReloadOutlined />}
          loading={query.isFetching}
          onClick={() => void query.refetch()}
          title={refreshLabel}
        />
      }
      title={intl.formatMessage({ id: 'workOrders.list.title' })}
    >
      <style>{responsiveStyle}</style>
      {query.isLoading ? (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          <Skeleton active paragraph={{ rows: 2 }} title={false} />
          <Skeleton active paragraph={{ rows: 2 }} title={false} />
        </Space>
      ) : query.isError ? (
        <Alert
          action={
            <Button onClick={() => void query.refetch()} size="small">
              {intl.formatMessage({ id: 'workOrders.actions.retry' })}
            </Button>
          }
          message={intl.formatMessage({ id: 'workOrders.list.error' })}
          showIcon
          type="error"
        />
      ) : workOrders.length ? (
        <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <div
            className="team-work-order-list-header"
            style={{
              borderBottom: `1px solid ${token.colorBorderSecondary}`,
              color: token.colorTextSecondary,
              fontSize: 12,
              fontWeight: 600,
              padding: '0 12px 10px',
              textTransform: 'uppercase',
            }}
          >
            <span>
              {intl.formatMessage({ id: 'workOrders.fields.request' })}
            </span>
            <span>
              {intl.formatMessage({ id: 'workOrders.fields.assignment' })}
            </span>
            <span>
              {intl.formatMessage({ id: 'workOrders.fields.updated' })}
            </span>
            <span />
          </div>
          {workOrders.map((workOrder) => (
            <div
              className="team-work-order-row"
              key={workOrder.workOrderId}
              style={{
                alignItems: 'center',
                borderBottom: `1px solid ${token.colorBorderSecondary}`,
                padding: '16px 12px',
              }}
            >
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <Space size={8} wrap>
                  <Tag color={statusColors[workOrder.lifecycleStatus]}>
                    {intl.formatMessage({
                      id: `workOrders.status.${workOrder.lifecycleStatus}`,
                    })}
                  </Tag>
                  <Typography.Text
                    code
                    copyable={{ text: workOrder.workOrderId }}
                  >
                    {workOrder.workOrderId}
                  </Typography.Text>
                </Space>
                <Typography.Text
                  ellipsis={{ tooltip: workOrder.intent }}
                  strong
                >
                  {workOrder.intent}
                </Typography.Text>
              </div>
              <div
                className="team-work-order-assignment"
                style={{ display: 'flex', flexDirection: 'column', gap: 4 }}
              >
                <Typography.Text ellipsis={{ tooltip: workOrder.memberId }}>
                  {workOrder.memberId}
                </Typography.Text>
                <Typography.Text
                  ellipsis={{ tooltip: workOrder.publishedServiceId }}
                  type="secondary"
                >
                  {workOrder.publishedServiceId}
                </Typography.Text>
              </div>
              <Typography.Text
                className="team-work-order-updated"
                type="secondary"
              >
                {formatCompactDateTime(workOrder.updatedAtUtc)}
              </Typography.Text>
              <Button
                aria-label={intl.formatMessage(
                  { id: 'workOrders.actions.openAria' },
                  { workOrderId: workOrder.workOrderId },
                )}
                icon={<ArrowRightOutlined />}
                onClick={() =>
                  onNavigate(
                    buildTeamWorkOrderDetailHref({
                      scopeId,
                      teamId,
                      workOrderId: workOrder.workOrderId,
                    }),
                  )
                }
                title={intl.formatMessage({ id: 'workOrders.actions.open' })}
              />
            </div>
          ))}
          {query.hasNextPage ? (
            <Button
              loading={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
              style={{ alignSelf: 'center', marginTop: 16 }}
            >
              {intl.formatMessage({ id: 'workOrders.actions.loadMore' })}
            </Button>
          ) : null}
        </div>
      ) : (
        <Empty
          description={intl.formatMessage({ id: 'workOrders.list.empty' })}
        />
      )}
    </AevatarPanel>
  );
};

export default TeamWorkOrdersTab;
