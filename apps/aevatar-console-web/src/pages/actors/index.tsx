import {
  ApartmentOutlined,
  EyeOutlined,
  NodeIndexOutlined,
  RadarChartOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProList } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Input, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import { buildRuntimeExplorerHref, buildRuntimeRunsHref } from "@/shared/navigation/runtimeRoutes";
import type { WorkflowAgentSummary } from "@/shared/models/runtime/query";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";

function readActorSelection(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return new URLSearchParams(window.location.search).get("actorId")?.trim() ?? "";
}

const ActorsPage: React.FC = () => {
  const [actorKeyword, setActorKeyword] = useState("");
  const [selectedActorId, setSelectedActorId] = useState(readActorSelection());

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
    history.replace(
      buildRuntimeExplorerHref({
        actorId: selectedActorId || undefined,
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

  return (
    <AevatarPageShell
      layoutMode="document"
      title="Runtime Explorer"
      titleHelp="Explorer is now an entity workbench. Actor discovery stays on stage while timeline, snapshot, and graph context slide into the inspector."
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title="Actor Focus"
              titleHelp="Paste a known actor ID or search discovered runtime agents."
            >
              <Space direction="vertical" size={12} style={{ width: "100%" }}>
                <Input
                  onChange={(event) => setSelectedActorId(event.target.value.trim())}
                  placeholder="Enter actor ID"
                  value={selectedActorId}
                />
                <Input
                  onChange={(event) => setActorKeyword(event.target.value)}
                  placeholder="Filter discovered actors"
                  value={actorKeyword}
                />
                <Button onClick={() => setSelectedActorId("")}>Clear focus</Button>
              </Space>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" title="Explorer Digest">
              <Space direction="vertical" size={6}>
                <Typography.Text strong>
                  {filteredActors.length} actor entries in view
                </Typography.Text>
                <Typography.Text type="secondary">
                  Snapshot, timeline, and subgraph all resolve from the same actor-focused inspector.
                </Typography.Text>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            layoutMode="document"
            title="Observed Actors"
            titleHelp="Actor cards replace the old multi-panel explorer so the stage remains readable even when the runtime catalog is large."
          >
            {actorsQuery.error ? (
              <Alert
                title={
                  actorsQuery.error instanceof Error
                    ? actorsQuery.error.message
                    : "Failed to load actors."
                }
                showIcon
                type="error"
              />
            ) : null}

            <ProList<WorkflowAgentSummary>
              dataSource={filteredActors}
              grid={{ gutter: 16, column: 1 }}
              itemCardProps={{
                bodyStyle: { padding: 16 },
                style: { borderRadius: 12 },
              }}
              locale={{
                emptyText: (
                  <Empty
                    description="No runtime actors matched the current filter."
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
        }
      />

      <AevatarContextDrawer
        onClose={() => setSelectedActorId("")}
        open={Boolean(selectedActorId)}
        subtitle="Actor snapshot"
        title={
          selectedSnapshotQuery.data?.actorId || selectedActorId || "Actor Inspector"
        }
      >
        {!selectedActorId ? (
          <AevatarInspectorEmpty description="Choose an actor to inspect its latest snapshot and runtime trace." />
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
          <AevatarInspectorEmpty description="No actor snapshot is available for the current actor ID yet." />
        ) : (
          <>
            <AevatarPanel
              title="Snapshot"
              titleHelp="Current actor state and completion signal."
            >
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
              <Typography.Text type="secondary">
                Last output: {selectedSnapshotQuery.data.lastOutput || "No output yet."}
              </Typography.Text>
            </AevatarPanel>

            <AevatarPanel
              title="Timeline"
              titleHelp="The event feed replaces the old timeline table."
            >
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
                  description="No timeline items were returned."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel
              title="Topology Digest"
              titleHelp="Graph context stays summarized here instead of becoming a full secondary canvas."
            >
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
    </AevatarPageShell>
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
