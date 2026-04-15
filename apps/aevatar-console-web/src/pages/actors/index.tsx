import {
  ApartmentOutlined,
  EyeOutlined,
  NodeIndexOutlined,
  RadarChartOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProList } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Input, Space, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { runtimeActorsApi } from '@/shared/api/runtimeActorsApi';
import { runtimeQueryApi } from '@/shared/api/runtimeQueryApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { history } from '@/shared/navigation/history';
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from '@/shared/navigation/runtimeRoutes';
import type { WorkflowAgentSummary } from '@/shared/models/runtime/query';
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPanel,
  AevatarStatusTag,
} from '@/shared/ui/aevatarPageShells';
import ConsoleMetricCard from '@/shared/ui/ConsoleMetricCard';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';
import { describeError } from '@/shared/ui/errorText';

type ExplorerRouteSelection = {
  actorId: string;
  runId: string;
  scopeId: string;
  serviceId: string;
};

function readExplorerSelection(): ExplorerRouteSelection {
  if (typeof window === "undefined") {
    return {
      actorId: "",
      runId: "",
      scopeId: "",
      serviceId: "",
    };
  }

  const searchParams = new URLSearchParams(window.location.search);
  return {
    actorId: searchParams.get("actorId")?.trim() ?? "",
    runId: searchParams.get("runId")?.trim() ?? "",
    scopeId: searchParams.get("scopeId")?.trim() ?? "",
    serviceId: searchParams.get("serviceId")?.trim() ?? "",
  };
}

const ActorsPage: React.FC = () => {
  const [actorKeyword, setActorKeyword] = useState("");
  const [selectedActorId, setSelectedActorId] = useState(
    readExplorerSelection().actorId,
  );

  const actorsQuery = useQuery({
    queryKey: ["runtime-agents"],
    queryFn: () => runtimeQueryApi.listAgents(),
  });
  const selectedSnapshotQuery = useQuery({
    enabled: selectedActorId.trim().length > 0,
    queryKey: ["runtime-actor-snapshot", selectedActorId],
    queryFn: () => runtimeActorsApi.getActorSnapshot(selectedActorId),
  });
  const timelineQuery = useQuery({
    enabled: selectedActorId.trim().length > 0,
    queryKey: ["runtime-actor-timeline", selectedActorId],
    queryFn: () => runtimeActorsApi.getActorTimeline(selectedActorId, { take: 20 }),
  });
  const graphQuery = useQuery({
    enabled: selectedActorId.trim().length > 0,
    queryKey: ["runtime-actor-graph", selectedActorId],
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(selectedActorId, {
        depth: 2,
        take: 40,
      }),
  });

  useEffect(() => {
    const routeSelection = readExplorerSelection();
    history.replace(
      buildRuntimeExplorerHref({
        actorId: selectedActorId || undefined,
        runId:
          routeSelection.actorId && routeSelection.actorId === selectedActorId
            ? routeSelection.runId || undefined
            : undefined,
        scopeId: routeSelection.scopeId || undefined,
        serviceId: routeSelection.serviceId || undefined,
      }),
    );
  }, [selectedActorId]);

  const filteredActors = useMemo(() => {
    const normalizedKeyword = actorKeyword.trim().toLowerCase();
    const actors = actorsQuery.data ?? [];

    if (!normalizedKeyword) {
      return actors;
    }

    return actors.filter((actor) =>
      [actor.id, actor.type, actor.description]
        .join(" ")
        .toLowerCase()
        .includes(normalizedKeyword),
    );
  }, [actorKeyword, actorsQuery.data]);

  const metas = useMemo<ProListMetas<WorkflowAgentSummary>>(
    () => ({
      actions: {
        render: (_, actor) => [
          <Button
            icon={<EyeOutlined />}
            key={`${actor.id}-inspect`}
            onClick={() => setSelectedActorId(actor.id)}
            type="link"
          >
            查看详情
          </Button>,
          <Button
            icon={<RadarChartOutlined />}
            key={`${actor.id}-runs`}
            onClick={() =>
              history.push(
                buildRuntimeRunsHref({
                  actorId: actor.id,
                }),
              )
            }
            type="link"
          >
            运行记录
          </Button>,
        ],
      },
      avatar: {
        render: () => <ApartmentOutlined style={{ fontSize: 18 }} />,
      },
      description: {
        render: (_, actor) => actor.description || actor.type,
      },
      subTitle: {
        render: (_, actor) => (
          <Space wrap size={[8, 8]}>
            <AevatarStatusTag domain="observation" status="live" />
            <Typography.Text type="secondary">{actor.type}</Typography.Text>
          </Space>
        ),
      },
      title: {
        render: (_, actor) => actor.id,
      },
    }),
    [],
  );
  const graphNodeCount = graphQuery.data?.subgraph.nodes.length ?? 0;
  const timelineItemCount = timelineQuery.data?.length ?? 0;
  const selectedActorSummary =
    selectedSnapshotQuery.data?.workflowName || selectedActorId || '待选择';

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      title="事件拓扑"
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
        <AevatarPanel layoutMode="document" padding={20} title="筛选条件">
          <Space wrap size={[12, 12]} style={{ width: '100%' }}>
            <Input
              onChange={(event) => setSelectedActorId(event.target.value.trim())}
              placeholder="成员 ID"
              style={{ width: 260 }}
              value={selectedActorId}
            />
            <Input
              onChange={(event) => setActorKeyword(event.target.value)}
              placeholder="过滤成员"
              style={{ width: 260 }}
              value={actorKeyword}
            />
            <Button onClick={() => setSelectedActorId('')}>重置</Button>
          </Space>
        </AevatarPanel>

        <div
          style={{
            display: 'grid',
            gap: 16,
            gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
          }}
        >
          <ConsoleMetricCard label="可见成员" tone="purple" value={filteredActors.length} />
          <ConsoleMetricCard label="当前焦点成员" value={selectedActorSummary} />
          <ConsoleMetricCard label="时间线事件" value={timelineItemCount} />
          <ConsoleMetricCard label="图谱节点" tone="green" value={graphNodeCount} />
        </div>

        <AevatarPanel layoutMode="document" padding={20} title="团队成员">
          {actorsQuery.error ? (
            <Alert
              description="请稍后刷新，或先切换到其他团队上下文。"
              showIcon
              title={describeError(actorsQuery.error, '成员列表暂时不可用。')}
              type="warning"
            />
          ) : null}

          <ProList<WorkflowAgentSummary>
            dataSource={filteredActors}
            grid={{ gutter: 16, column: 2 }}
            itemCardProps={{
              bodyStyle: { padding: 20 },
              style: {
                borderRadius: 12,
                boxShadow: '0 1px 3px rgba(15, 23, 42, 0.04)',
              },
            }}
            locale={{
              emptyText: (
                <Empty
                  description="当前还没有可见成员"
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              ),
            }}
            metas={metas}
            pagination={{ defaultPageSize: 8, showSizeChanger: false }}
            rowKey="id"
            split={false}
          />
        </AevatarPanel>
      </div>

      <AevatarContextDrawer
        onClose={() => setSelectedActorId("")}
        open={Boolean(selectedActorId)}
        subtitle="事件拓扑"
        title={selectedSnapshotQuery.data?.actorId || selectedActorId || "成员详情"}
      >
        {!selectedActorId ? (
          <AevatarInspectorEmpty description="先选择一位成员，再查看当前状态和事件流" />
        ) : selectedSnapshotQuery.error ? (
          <Alert
            description="请稍后再试，或先查看其他成员。"
            showIcon
            title={describeError(selectedSnapshotQuery.error, "成员快照暂时不可用。")}
            type="warning"
          />
        ) : !selectedSnapshotQuery.data ? (
          <AevatarInspectorEmpty description="当前成员还没有可见快照" />
        ) : (
          <>
            <AevatarPanel title="当前状态">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <MetricCard
                  label="流程"
                  value={selectedSnapshotQuery.data.workflowName || "待同步"}
                />
                <MetricCard label="状态版本" value={selectedSnapshotQuery.data.stateVersion} />
                <MetricCard label="已完成步骤" value={selectedSnapshotQuery.data.completedSteps} />
                <MetricCard
                  label="最近更新"
                  value={formatDateTime(selectedSnapshotQuery.data.lastUpdatedAt)}
                />
              </div>
              <MetricCard
                label="最近输出"
                value={selectedSnapshotQuery.data.lastOutput || "暂无输出"}
              />
            </AevatarPanel>

            <AevatarPanel title="事件流">
              {timelineQuery.data?.length ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                  {timelineQuery.data.map((item) => (
                    <div
                      key={`${item.timestamp}-${item.stage}-${item.stepId}-${item.eventType}`}
                      style={{
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 12,
                        display: "flex",
                        flexDirection: "column",
                        gap: 6,
                        padding: 12,
                      }}
                    >
                      <Space wrap size={[8, 8]}>
                        <AevatarStatusTag domain="run" status={item.stage || "observed"} />
                        <Typography.Text strong>{item.stepId || item.eventType}</Typography.Text>
                      </Space>
                      <Typography.Text>{item.message}</Typography.Text>
                      <Typography.Text type="secondary">
                        {formatDateTime(item.timestamp)} · {item.stepType || "未知类型"}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  description="暂无事件"
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel title="拓扑概览">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <MetricCard
                  label="节点"
                  value={graphQuery.data?.subgraph.nodes.length ?? 0}
                />
                <MetricCard
                  label="连线"
                  value={graphQuery.data?.subgraph.edges.length ?? 0}
                />
                <MetricCard
                  label="角色回复"
                  value={selectedSnapshotQuery.data.roleReplyCount}
                />
                <MetricCard
                  label="完成度"
                  value={`${selectedSnapshotQuery.data.completionStatusValue}%`}
                />
              </div>
              <Space direction="vertical" size={8} style={{ width: "100%" }}>
                {(graphQuery.data?.subgraph.nodes ?? []).slice(0, 6).map((node) => (
                  <div
                    key={node.nodeId}
                    style={{
                      alignItems: "center",
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 12,
                      display: "flex",
                      gap: 8,
                      justifyContent: "space-between",
                      padding: 12,
                    }}
                  >
                    <Space size={8}>
                      <NodeIndexOutlined />
                      <Typography.Text strong>{node.nodeId}</Typography.Text>
                    </Space>
                    <Typography.Text type="secondary">{node.nodeType}</Typography.Text>
                  </div>
                ))}
              </Space>
            </AevatarPanel>
          </>
        )}
      </AevatarContextDrawer>
    </ConsoleMenuPageShell>
  );
};

const MetricCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      background: "var(--ant-color-fill-quaternary)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      padding: 12,
    }}
  >
    <Typography.Text type="secondary">{label}</Typography.Text>
    <Typography.Text strong>{value}</Typography.Text>
  </div>
);

export default ActorsPage;
