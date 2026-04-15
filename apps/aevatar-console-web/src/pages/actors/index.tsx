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
            Inspect
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
            Runs
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
    selectedSnapshotQuery.data?.workflowName || selectedActorId || '--';

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      title="Topology"
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
        <AevatarPanel layoutMode="document" padding={20} title="Search">
          <Space wrap size={[12, 12]} style={{ width: '100%' }}>
            <Input
              onChange={(event) => setSelectedActorId(event.target.value.trim())}
              placeholder="Actor ID"
              style={{ width: 260 }}
              value={selectedActorId}
            />
            <Input
              onChange={(event) => setActorKeyword(event.target.value)}
              placeholder="Filter actors"
              style={{ width: 260 }}
              value={actorKeyword}
            />
            <Button onClick={() => setSelectedActorId('')}>Reset</Button>
          </Space>
        </AevatarPanel>

        <div
          style={{
            display: 'grid',
            gap: 16,
            gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
          }}
        >
          <ConsoleMetricCard label="可见 Actor" tone="purple" value={filteredActors.length} />
          <ConsoleMetricCard label="当前焦点" value={selectedActorSummary} />
          <ConsoleMetricCard label="时间线事件" value={timelineItemCount} />
          <ConsoleMetricCard label="图谱节点" tone="green" value={graphNodeCount} />
        </div>

        <AevatarPanel layoutMode="document" padding={20} title="Actors">
          {actorsQuery.error ? (
            <Alert
              title={
                actorsQuery.error instanceof Error
                  ? actorsQuery.error.message
                  : 'Failed to load actors.'
              }
              showIcon
              type="error"
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
                  description="No actors"
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
        subtitle="Topology"
        title={selectedSnapshotQuery.data?.actorId || selectedActorId || "Actor"}
      >
        {!selectedActorId ? (
          <AevatarInspectorEmpty description="Select an actor" />
        ) : selectedSnapshotQuery.error ? (
          <Alert
            title={
              selectedSnapshotQuery.error instanceof Error
                ? selectedSnapshotQuery.error.message
                : "Failed to load actor snapshot."
            }
            showIcon
            type="error"
          />
        ) : !selectedSnapshotQuery.data ? (
          <AevatarInspectorEmpty description="No data" />
        ) : (
          <>
            <AevatarPanel title="Summary">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <MetricCard label="Workflow" value={selectedSnapshotQuery.data.workflowName || "n/a"} />
                <MetricCard label="State version" value={selectedSnapshotQuery.data.stateVersion} />
                <MetricCard label="Completed steps" value={selectedSnapshotQuery.data.completedSteps} />
                <MetricCard
                  label="Last update"
                  value={formatDateTime(selectedSnapshotQuery.data.lastUpdatedAt)}
                />
              </div>
              <MetricCard
                label="Last output"
                value={selectedSnapshotQuery.data.lastOutput || "No output"}
              />
            </AevatarPanel>

            <AevatarPanel title="Timeline">
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
                        {formatDateTime(item.timestamp)} · {item.stepType || "n/a"}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  description="No timeline"
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel title="Topology">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <MetricCard
                  label="Nodes"
                  value={graphQuery.data?.subgraph.nodes.length ?? 0}
                />
                <MetricCard
                  label="Edges"
                  value={graphQuery.data?.subgraph.edges.length ?? 0}
                />
                <MetricCard
                  label="Role replies"
                  value={selectedSnapshotQuery.data.roleReplyCount}
                />
                <MetricCard
                  label="Completion"
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
