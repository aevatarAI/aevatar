import {
  PlayCircleOutlined,
  ReloadOutlined,
  SendOutlined,
  StopOutlined,
  SwapOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useIntl } from '@umijs/max';
import {
  Alert,
  Button,
  Empty,
  Input,
  Modal,
  message,
  Result,
  Select,
  Skeleton,
  Space,
  Tag,
  Typography,
  theme,
} from 'antd';
import React from 'react';
import {
  type WorkOrderAcceptedReceipt,
  type WorkOrderLifecycleStatus,
  workOrdersApi,
} from '@/shared/api/workOrdersApi';
import { formatCompactDateTime } from '@/shared/datetime/dateTime';
import {
  getLocationSnapshot,
  history,
  subscribeToLocationChanges,
} from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import {
  buildTeamDetailHref,
  readTeamWorkOrderRouteState,
} from '@/shared/navigation/teamRoutes';
import { studioApi } from '@/shared/studio/api';
import { AevatarPageShell, AevatarPanel } from '@/shared/ui/aevatarPageShells';
import { describeError } from '@/shared/ui/errorText';

type PendingAction = 'reassign' | 'dispatch' | 'cancel' | null;

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
.work-order-detail-grid {
  align-items: start;
  display: grid;
  gap: 16px;
  grid-template-columns: minmax(0, 1.35fr) minmax(300px, 0.65fr);
  min-width: 0;
}

@media (max-width: 960px) {
  .work-order-detail-grid {
    grid-template-columns: minmax(0, 1fr);
  }
}
`;

function FactRow({
  label,
  value,
}: {
  readonly label: React.ReactNode;
  readonly value: React.ReactNode;
}) {
  const { token } = theme.useToken();
  return (
    <div
      style={{
        alignItems: 'start',
        borderBottom: `1px solid ${token.colorBorderSecondary}`,
        display: 'grid',
        gap: 16,
        gridTemplateColumns: 'minmax(110px, 0.35fr) minmax(0, 1fr)',
        paddingBlock: 11,
      }}
    >
      <Typography.Text type="secondary">{label}</Typography.Text>
      <div style={{ minWidth: 0 }}>{value}</div>
    </div>
  );
}

const TeamWorkOrderDetailPage: React.FC = () => {
  const intl = useIntl();
  const queryClient = useQueryClient();
  const locationSnapshot = React.useSyncExternalStore(
    subscribeToLocationChanges,
    getLocationSnapshot,
    () => '',
  );
  const routeState = React.useMemo(
    () => readTeamWorkOrderRouteState(locationSnapshot.split(/[?#]/, 1)[0]),
    [locationSnapshot],
  );
  const [pendingAction, setPendingAction] = React.useState<PendingAction>(null);
  const [receipt, setReceipt] = React.useState<WorkOrderAcceptedReceipt | null>(
    null,
  );
  const [reassignOpen, setReassignOpen] = React.useState(false);
  const [dispatchOpen, setDispatchOpen] = React.useState(false);
  const [cancelOpen, setCancelOpen] = React.useState(false);
  const [selectedMemberId, setSelectedMemberId] = React.useState('');
  const [cancelReason, setCancelReason] = React.useState('');

  const hasRouteIdentity = Boolean(
    routeState.scopeId && routeState.teamId && routeState.workOrderId,
  );
  const detailQueryKey = React.useMemo(
    () => ['work-orders', routeState.scopeId, routeState.workOrderId] as const,
    [routeState.scopeId, routeState.workOrderId],
  );
  const detailQuery = useQuery({
    enabled: hasRouteIdentity,
    queryFn: () =>
      workOrdersApi.get(routeState.scopeId, routeState.workOrderId),
    queryKey: detailQueryKey,
    retry: false,
  });
  const authSessionQuery = useQuery({
    enabled: hasRouteIdentity,
    queryFn: () => studioApi.getAuthSession(),
    queryKey: ['studio', 'auth', 'session'],
    retry: false,
  });
  const authenticatedSubject =
    authSessionQuery.data?.profile?.subject?.trim() ?? '';
  const canManage = Boolean(
    detailQuery.data &&
      !authSessionQuery.isLoading &&
      !authSessionQuery.isError &&
      (authSessionQuery.data?.enabled === false ||
        authenticatedSubject === detailQuery.data.requester.principalId),
  );
  const memberQuery = useQuery({
    enabled: Boolean(
      canManage &&
        detailQuery.data?.availableActions.canReassign &&
        routeState.scopeId &&
        routeState.teamId,
    ),
    queryFn: () =>
      studioApi.listTeamMembers(routeState.scopeId, routeState.teamId),
    queryKey: ['teams', 'team-members', routeState.scopeId, routeState.teamId],
    retry: false,
  });
  const assignableMembers = React.useMemo(
    () =>
      (memberQuery.data?.members ?? []).filter(
        (member) =>
          member.memberId.trim() &&
          member.publishedServiceId.trim() &&
          member.memberId !== detailQuery.data?.memberId,
      ),
    [detailQuery.data?.memberId, memberQuery.data?.members],
  );

  React.useEffect(() => {
    setPendingAction(null);
    setReceipt(null);
    setReassignOpen(false);
    setDispatchOpen(false);
    setCancelOpen(false);
    setSelectedMemberId('');
    setCancelReason('');
  }, [routeState.scopeId, routeState.teamId, routeState.workOrderId]);

  const teamRequestsHref = buildTeamDetailHref({
    scopeId: routeState.scopeId,
    teamId: routeState.teamId,
    tab: 'work-orders',
  });

  const refreshAfterMutation = React.useCallback(async () => {
    await Promise.all([
      detailQuery.refetch(),
      queryClient.invalidateQueries({
        queryKey: [
          'work-orders',
          routeState.scopeId,
          'team',
          routeState.teamId,
        ],
      }),
    ]);
  }, [detailQuery, queryClient, routeState.scopeId, routeState.teamId]);

  const runMutation = React.useCallback(
    async (
      action: Exclude<PendingAction, null>,
      command: () => Promise<WorkOrderAcceptedReceipt>,
    ) => {
      setPendingAction(action);
      setReceipt(null);
      try {
        const accepted = await command();
        setReceipt(accepted);
        message.info(intl.formatMessage({ id: 'workOrders.receipt.accepted' }));
      } catch (error) {
        message.error(describeError(error));
      } finally {
        await Promise.allSettled([refreshAfterMutation()]);
        setPendingAction(null);
      }
    },
    [intl, refreshAfterMutation],
  );

  const submitReassign = React.useCallback(async () => {
    const workOrder = detailQuery.data;
    const selectedMember = assignableMembers.find(
      (member) => member.memberId === selectedMemberId,
    );
    if (!workOrder || !selectedMember) {
      return;
    }
    setReassignOpen(false);
    await runMutation('reassign', () =>
      workOrdersApi.reassign({
        scopeId: routeState.scopeId,
        workOrderId: routeState.workOrderId,
        memberId: selectedMember.memberId,
        publishedServiceId: selectedMember.publishedServiceId,
        expectedLifecycleVersion: workOrder.lifecycleVersion,
      }),
    );
  }, [
    assignableMembers,
    detailQuery.data,
    routeState.scopeId,
    routeState.workOrderId,
    runMutation,
    selectedMemberId,
  ]);

  if (!hasRouteIdentity) {
    return (
      <Result
        extra={
          <Button onClick={() => history.push('/scopes')} type="primary">
            {intl.formatMessage({ id: 'workOrders.actions.backToTeams' })}
          </Button>
        }
        status="404"
        title={intl.formatMessage({ id: 'workOrders.detail.invalidRoute' })}
      />
    );
  }

  const workOrder = detailQuery.data;
  const runHref = workOrder?.run
    ? buildRuntimeRunsHref({
        actorId: workOrder.run.runActorId,
        endpointId: workOrder.endpointId,
        returnTo: getLocationSnapshot(),
        runId: workOrder.run.runId,
        scopeId: workOrder.scopeId,
        serviceOverrideId: workOrder.publishedServiceId,
      })
    : '';

  return (
    <AevatarPageShell
      backAriaLabel={intl.formatMessage({
        id: 'workOrders.actions.backToRequests',
      })}
      backTitle={intl.formatMessage({
        id: 'workOrders.actions.backToRequests',
      })}
      breadcrumbItems={[
        {
          href: buildTeamDetailHref({
            scopeId: routeState.scopeId,
            teamId: routeState.teamId,
          }),
          onClick: (event) => {
            event.preventDefault();
            history.push(
              buildTeamDetailHref({
                scopeId: routeState.scopeId,
                teamId: routeState.teamId,
              }),
            );
          },
          title: routeState.teamId,
        },
        {
          href: teamRequestsHref,
          onClick: (event) => {
            event.preventDefault();
            history.push(teamRequestsHref);
          },
          title: intl.formatMessage({ id: 'workOrders.list.title' }),
        },
        { current: true, title: routeState.workOrderId },
      ]}
      content={
        workOrder ? (
          <Space size={8} wrap>
            <Tag color={statusColors[workOrder.lifecycleStatus]}>
              {intl.formatMessage({
                id: `workOrders.status.${workOrder.lifecycleStatus}`,
              })}
            </Tag>
            <Typography.Text type="secondary">
              {intl.formatMessage(
                { id: 'workOrders.detail.observedVersion' },
                { version: workOrder.stateVersion },
              )}
            </Typography.Text>
          </Space>
        ) : null
      }
      extra={
        <Space wrap>
          <Button
            aria-label={intl.formatMessage({
              id: 'workOrders.actions.refresh',
            })}
            icon={<ReloadOutlined />}
            loading={detailQuery.isFetching}
            onClick={() => void detailQuery.refetch()}
            title={intl.formatMessage({ id: 'workOrders.actions.refresh' })}
          />
          {workOrder?.run ? (
            <Button
              icon={<PlayCircleOutlined />}
              onClick={() => history.push(runHref)}
            >
              {intl.formatMessage({ id: 'workOrders.actions.openRun' })}
            </Button>
          ) : null}
        </Space>
      }
      layoutMode="document"
      onBack={() => history.push(teamRequestsHref)}
      title={workOrder?.intent || routeState.workOrderId}
    >
      <style>{responsiveStyle}</style>
      {detailQuery.isLoading ? (
        <AevatarPanel>
          <Skeleton active paragraph={{ rows: 8 }} />
        </AevatarPanel>
      ) : detailQuery.isError || !workOrder ? (
        <AevatarPanel>
          <Alert
            action={
              <Button onClick={() => void detailQuery.refetch()} size="small">
                {intl.formatMessage({ id: 'workOrders.actions.retry' })}
              </Button>
            }
            message={intl.formatMessage({ id: 'workOrders.detail.error' })}
            showIcon
            type="error"
          />
        </AevatarPanel>
      ) : (
        <Space direction="vertical" size={16} style={{ width: '100%' }}>
          {receipt ? (
            <Alert
              description={intl.formatMessage(
                { id: 'workOrders.receipt.description' },
                {
                  commandId: receipt.commandId,
                  version: workOrder.stateVersion,
                },
              )}
              message={intl.formatMessage({ id: 'workOrders.receipt.title' })}
              showIcon
              type="info"
            />
          ) : null}
          {authSessionQuery.isError ? (
            <Alert
              message={intl.formatMessage({
                id: 'workOrders.authorization.error',
              })}
              showIcon
              type="warning"
            />
          ) : !authSessionQuery.isLoading && !canManage ? (
            <Alert
              message={intl.formatMessage({
                id: 'workOrders.authorization.requesterOnly',
              })}
              showIcon
              type="info"
            />
          ) : null}
          <div className="work-order-detail-grid">
            <AevatarPanel
              title={intl.formatMessage({ id: 'workOrders.detail.request' })}
            >
              <FactRow
                label={intl.formatMessage({ id: 'workOrders.fields.intent' })}
                value={
                  <Typography.Paragraph style={{ margin: 0 }}>
                    {workOrder.intent}
                  </Typography.Paragraph>
                }
              />
              <FactRow
                label={intl.formatMessage({ id: 'workOrders.fields.prompt' })}
                value={
                  <Typography.Paragraph
                    style={{ margin: 0, whiteSpace: 'pre-wrap' }}
                  >
                    {workOrder.input.chat.prompt}
                  </Typography.Paragraph>
                }
              />
              <FactRow
                label={intl.formatMessage({
                  id: 'workOrders.fields.requester',
                })}
                value={
                  <Typography.Text copyable>
                    {workOrder.requester.principalId}
                  </Typography.Text>
                }
              />
              <FactRow
                label={intl.formatMessage({ id: 'workOrders.fields.created' })}
                value={
                  <Typography.Text>
                    {formatCompactDateTime(workOrder.createdAtUtc)}
                  </Typography.Text>
                }
              />
              <FactRow
                label={intl.formatMessage({ id: 'workOrders.fields.deadline' })}
                value={
                  <Typography.Text>
                    {workOrder.timeoutAtUtc
                      ? formatCompactDateTime(workOrder.timeoutAtUtc)
                      : intl.formatMessage({
                          id: 'workOrders.values.noDeadline',
                        })}
                  </Typography.Text>
                }
              />
            </AevatarPanel>

            <Space direction="vertical" size={16} style={{ width: '100%' }}>
              <AevatarPanel
                title={intl.formatMessage({
                  id: 'workOrders.detail.assignment',
                })}
              >
                <FactRow
                  label={intl.formatMessage({ id: 'workOrders.fields.member' })}
                  value={
                    <Typography.Text copyable>
                      {workOrder.memberId}
                    </Typography.Text>
                  }
                />
                <FactRow
                  label={intl.formatMessage({
                    id: 'workOrders.fields.service',
                  })}
                  value={
                    <Typography.Text copyable>
                      {workOrder.publishedServiceId}
                    </Typography.Text>
                  }
                />
                {workOrder.workflowId ? (
                  <FactRow
                    label={intl.formatMessage({
                      id: 'workOrders.fields.workflow',
                    })}
                    value={
                      <Typography.Text copyable>
                        {workOrder.workflowId}
                      </Typography.Text>
                    }
                  />
                ) : null}
                <FactRow
                  label={intl.formatMessage({
                    id: 'workOrders.fields.updated',
                  })}
                  value={
                    <Typography.Text>
                      {formatCompactDateTime(workOrder.updatedAtUtc)}
                    </Typography.Text>
                  }
                />
              </AevatarPanel>

              <AevatarPanel
                title={intl.formatMessage({ id: 'workOrders.detail.run' })}
              >
                {workOrder.run ? (
                  <Space
                    direction="vertical"
                    size={10}
                    style={{ width: '100%' }}
                  >
                    <Typography.Text copyable>
                      {workOrder.run.runId}
                    </Typography.Text>
                    <Typography.Text type="secondary">
                      {intl.formatMessage(
                        { id: 'workOrders.detail.runAcceptedAt' },
                        {
                          time: formatCompactDateTime(
                            workOrder.run.acceptedAtUtc,
                          ),
                        },
                      )}
                    </Typography.Text>
                    <Button
                      icon={<PlayCircleOutlined />}
                      onClick={() => history.push(runHref)}
                    >
                      {intl.formatMessage({ id: 'workOrders.actions.openRun' })}
                    </Button>
                  </Space>
                ) : (
                  <Empty
                    description={intl.formatMessage({
                      id: 'workOrders.detail.noRun',
                    })}
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                )}
              </AevatarPanel>
            </Space>
          </div>

          {canManage &&
          (workOrder.availableActions.canReassign ||
            workOrder.availableActions.canDispatch ||
            workOrder.availableActions.canCancel) ? (
            <AevatarPanel
              title={intl.formatMessage({ id: 'workOrders.detail.actions' })}
            >
              <Space wrap>
                {workOrder.availableActions.canReassign ? (
                  <Button
                    icon={<SwapOutlined />}
                    loading={pendingAction === 'reassign'}
                    onClick={() => {
                      setSelectedMemberId('');
                      setReassignOpen(true);
                    }}
                  >
                    {intl.formatMessage({ id: 'workOrders.actions.reassign' })}
                  </Button>
                ) : null}
                {workOrder.availableActions.canDispatch ? (
                  <Button
                    icon={<SendOutlined />}
                    loading={pendingAction === 'dispatch'}
                    onClick={() => setDispatchOpen(true)}
                    type="primary"
                  >
                    {intl.formatMessage({ id: 'workOrders.actions.dispatch' })}
                  </Button>
                ) : null}
                {workOrder.availableActions.canCancel ? (
                  <Button
                    danger
                    icon={<StopOutlined />}
                    loading={pendingAction === 'cancel'}
                    onClick={() => setCancelOpen(true)}
                  >
                    {intl.formatMessage({ id: 'workOrders.actions.cancel' })}
                  </Button>
                ) : null}
              </Space>
            </AevatarPanel>
          ) : null}
        </Space>
      )}

      <Modal
        cancelText={intl.formatMessage({ id: 'workOrders.actions.close' })}
        confirmLoading={pendingAction === 'reassign'}
        okButtonProps={{ disabled: !selectedMemberId }}
        okText={intl.formatMessage({ id: 'workOrders.actions.reassign' })}
        onCancel={() => setReassignOpen(false)}
        onOk={() => void submitReassign()}
        open={reassignOpen}
        title={intl.formatMessage({ id: 'workOrders.reassign.title' })}
      >
        {memberQuery.isError ? (
          <Alert
            message={intl.formatMessage({ id: 'workOrders.reassign.error' })}
            showIcon
            type="error"
          />
        ) : (
          <Select
            aria-label={intl.formatMessage({ id: 'workOrders.fields.member' })}
            loading={memberQuery.isLoading}
            onChange={setSelectedMemberId}
            options={assignableMembers.map((member) => ({
              label: `${member.displayName} · ${member.publishedServiceId}`,
              value: member.memberId,
            }))}
            placeholder={intl.formatMessage({
              id: 'workOrders.reassign.placeholder',
            })}
            style={{ width: '100%' }}
            value={selectedMemberId || undefined}
          />
        )}
      </Modal>

      <Modal
        cancelText={intl.formatMessage({ id: 'workOrders.actions.close' })}
        confirmLoading={pendingAction === 'dispatch'}
        okText={intl.formatMessage({ id: 'workOrders.actions.dispatch' })}
        onCancel={() => setDispatchOpen(false)}
        onOk={() => {
          setDispatchOpen(false);
          void runMutation('dispatch', () =>
            workOrdersApi.dispatch({
              scopeId: routeState.scopeId,
              workOrderId: routeState.workOrderId,
              expectedLifecycleVersion: workOrder?.lifecycleVersion ?? 0,
            }),
          );
        }}
        open={dispatchOpen}
        title={intl.formatMessage({ id: 'workOrders.dispatch.title' })}
      >
        <Typography.Text>
          {intl.formatMessage({ id: 'workOrders.dispatch.confirm' })}
        </Typography.Text>
      </Modal>

      <Modal
        cancelText={intl.formatMessage({ id: 'workOrders.actions.close' })}
        confirmLoading={pendingAction === 'cancel'}
        okButtonProps={{ danger: true }}
        okText={intl.formatMessage({ id: 'workOrders.actions.cancel' })}
        onCancel={() => setCancelOpen(false)}
        onOk={() => {
          setCancelOpen(false);
          void runMutation('cancel', () =>
            workOrdersApi.cancel({
              scopeId: routeState.scopeId,
              workOrderId: routeState.workOrderId,
              expectedLifecycleVersion: workOrder?.lifecycleVersion ?? 0,
              reason: cancelReason,
            }),
          );
        }}
        open={cancelOpen}
        title={intl.formatMessage({ id: 'workOrders.cancel.title' })}
      >
        <Input.TextArea
          aria-label={intl.formatMessage({ id: 'workOrders.cancel.reason' })}
          maxLength={500}
          onChange={(event) => setCancelReason(event.target.value)}
          placeholder={intl.formatMessage({
            id: 'workOrders.cancel.reasonPlaceholder',
          })}
          rows={3}
          value={cancelReason}
        />
      </Modal>
    </AevatarPageShell>
  );
};

export default TeamWorkOrderDetailPage;
