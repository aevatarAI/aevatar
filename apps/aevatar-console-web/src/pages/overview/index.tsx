import {
  ApartmentOutlined,
  ControlOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  ThunderboltOutlined,
} from "@ant-design/icons";
import { ProCard } from "@ant-design/pro-components";
import { Alert, Button, Empty, Space, Typography } from "antd";
import React, { useMemo, useState } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimePrimitivesHref,
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import { buildStudioWorkflowWorkspaceRoute } from "@/shared/studio/navigation";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import { theme } from "antd";
import { useOverviewData } from "./useOverviewData";

type OverviewFocus =
  | "human-workflows"
  | "live-actors"
  | "connectors"
  | "primitives"
  | "catalog"
  | null;

type CommandTile = {
  description: string;
  icon: React.ReactNode;
  id: Exclude<OverviewFocus, null>;
  label: string;
  tone?: "default" | "error" | "info" | "success" | "warning";
  value: string;
};

const quickStartSteps = [
  {
    description:
      "Start from the project workspace so every later action stays tied to a project scope instead of raw platform pages.",
    label: "Open Projects",
    onClick: () => history.push("/scopes/overview"),
    secondary: {
      label: "Open workflow workspace",
      onClick: () => history.push(buildStudioWorkflowWorkspaceRoute()),
    },
    step: "01",
    title: "Anchor work to a project",
  },
  {
    description:
      "Draft or revise a capability in Studio, then move it into an active binding when it is ready to serve.",
    label: "Open workflow workspace",
    onClick: () => history.push(buildStudioWorkflowWorkspaceRoute()),
    secondary: {
      label: "Open Assets",
      onClick: () => history.push("/scopes/assets"),
    },
    step: "02",
    title: "Promote a capability",
  },
  {
    description:
      "Open Runs first to attach to a real execution. Mission Control only becomes useful after a live run context exists.",
    label: "Open Runs",
    onClick: () => history.push(buildRuntimeRunsHref()),
    secondary: {
      label: "Open Invoke Lab",
      onClick: () => history.push("/scopes/invoke"),
    },
    step: "03",
    title: "Operate the runtime",
  },
] as const;

const OverviewPage: React.FC = () => {
  const { token } = theme.useToken();
  const {
    agentsQuery,
    capabilitiesQuery,
    humanFocusedWorkflows,
    liveActors,
    visibleCatalogItems,
    workflowsQuery,
    capabilityConnectorSummary,
    capabilityPrimitiveCategorySummary,
    capabilityWorkflowSourceSummary,
  } = useOverviewData();
  const [focus, setFocus] = useState<OverviewFocus>(null);

  const tiles = useMemo<CommandTile[]>(
    () => [
      {
        description: "Workflows that already expose approval, input, or wait-signal paths.",
        icon: <ControlOutlined />,
        id: "human-workflows",
        label: "Human Loop",
        tone: "warning",
        value: String(humanFocusedWorkflows.length),
      },
      {
        description: "Live or recently observed actors that can be reopened in runtime explorer.",
        icon: <EyeOutlined />,
        id: "live-actors",
        label: "Live Actors",
        tone: "info",
        value: String(liveActors.length),
      },
      {
        description: "Connector readiness across the runtime capability surface.",
        icon: <DeploymentUnitOutlined />,
        id: "connectors",
        label: "Connectors",
        tone: "success",
        value: capabilityConnectorSummary,
      },
      {
        description: "Top primitive categories visible in the current capability catalog.",
        icon: <ThunderboltOutlined />,
        id: "primitives",
        label: "Primitive Surface",
        tone: "info",
        value: capabilityPrimitiveCategorySummary.join(" · ") || "No primitives",
      },
      {
        description: "Visible catalog items that can move from design to runtime.",
        icon: <ApartmentOutlined />,
        id: "catalog",
        label: "Catalog",
        tone: "default",
        value: `${visibleCatalogItems.length} visible`,
      },
    ],
    [
      capabilityConnectorSummary,
      capabilityPrimitiveCategorySummary,
      humanFocusedWorkflows.length,
      liveActors.length,
      visibleCatalogItems.length,
    ],
  );

  const focusTitle =
    focus === "human-workflows"
      ? "Human-loop workflows"
      : focus === "live-actors"
        ? "Live actor shortcuts"
        : focus === "connectors"
          ? "Connector readiness"
          : focus === "primitives"
            ? "Primitive surface"
            : focus === "catalog"
              ? "Workflow catalog"
              : "Overview";

  return (
    <AevatarPageShell
      content="A single command-center view from login to runtime: project-first actions on the left, ecosystem health in the center, and detail only when you ask for it."
      title="Overview"
    >
      <AevatarWorkbenchLayout
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              description="The console now teaches one consistent path: project, publish, observe, govern."
              title="Command Path"
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                {quickStartSteps.map((step) => (
                  <div
                    key={step.step}
                    style={{
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 12,
                      display: "flex",
                      flexDirection: "column",
                      gap: 8,
                      padding: 12,
                    }}
                  >
                    <Typography.Text type="secondary">{step.step}</Typography.Text>
                    <Typography.Text strong>{step.title}</Typography.Text>
                    <Typography.Text type="secondary">
                      {step.description}
                    </Typography.Text>
                    <Space wrap>
                      <Button onClick={step.onClick} type="primary">
                        {step.label}
                      </Button>
                      <Button onClick={step.secondary.onClick}>
                        {step.secondary.label}
                      </Button>
                    </Space>
                  </div>
                ))}
              </div>
            </AevatarPanel>

            <AevatarPanel title="Operator Shortcuts">
              <Space direction="vertical" size={8} style={{ width: "100%" }}>
                <Button onClick={() => history.push("/scopes/assets")}>
                  Open assets
                </Button>
                <Button onClick={() => history.push("/deployments")}>
                  Open deployments
                </Button>
                <Button onClick={() => history.push("/governance")}>
                  Open governance
                </Button>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {workflowsQuery.error || agentsQuery.error || capabilitiesQuery.error ? (
              <Alert
                title="Some overview feeds failed to load. The dashboard will continue with partial data."
                showIcon
                type="warning"
              />
            ) : null}

            <ProCard
              bodyStyle={{ padding: 0 }}
              ghost
              title={false}
            >
              <div
                style={{
                  display: "grid",
                  gap: 16,
                  gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
                }}
              >
                {tiles.map((tile) => {
                  const visual = resolveAevatarMetricVisual(
                    token as AevatarThemeSurfaceToken,
                    tile.tone || "default",
                  );

                  return (
                    <button
                      key={tile.id}
                      onClick={() => setFocus(tile.id)}
                      style={{
                        ...buildAevatarMetricCardStyle(
                          token as AevatarThemeSurfaceToken,
                          tile.tone || "default",
                        ),
                        WebkitAppearance: "none",
                        alignItems: "flex-start",
                        appearance: "none",
                        borderRadius: 12,
                        cursor: "pointer",
                        font: "inherit",
                        textAlign: "left",
                      }}
                      type="button"
                    >
                      <Space size={10}>
                        <span style={{ color: visual.iconColor, display: "inline-flex" }}>
                          {tile.icon}
                        </span>
                        <Typography.Text strong style={{ color: visual.valueColor }}>
                          {tile.label}
                        </Typography.Text>
                      </Space>
                      <Typography.Text
                        style={{ color: visual.valueColor, fontSize: 18, fontWeight: 700 }}
                      >
                        {tile.value}
                      </Typography.Text>
                      <Typography.Text style={{ color: visual.secondaryColor }}>
                        {tile.description}
                      </Typography.Text>
                    </button>
                  );
                })}
              </div>
            </ProCard>

            <AevatarPanel
              description="A ghost-board view keeps the command center visually fused to the global shell instead of feeling like a separate application."
              title="State Board"
            >
              <div
                style={{
                  display: "grid",
                  gap: 16,
                  gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
                }}
              >
                <GhostBoardCard
                  description={capabilityWorkflowSourceSummary.join(" · ") || "No workflow sources loaded yet."}
                  onOpen={() => setFocus("catalog")}
                  title="Workflow sources"
                />
                <GhostBoardCard
                  description={
                    humanFocusedWorkflows
                      .map((item) => item.name)
                      .slice(0, 3)
                      .join(" · ") || "No human-loop workflows discovered."
                  }
                  onOpen={() => setFocus("human-workflows")}
                  title="Human-loop focus"
                />
                <GhostBoardCard
                  description={
                    liveActors.map((item) => item.id).slice(0, 3).join(" · ") ||
                    "No live actors returned by the backend."
                  }
                  onOpen={() => setFocus("live-actors")}
                  title="Runtime attention"
                />
              </div>
            </AevatarPanel>
          </div>
        }
      />

      <AevatarContextDrawer
        onClose={() => setFocus(null)}
        open={Boolean(focus)}
        subtitle="Focused command-center detail"
        title={focusTitle}
      >
        {!focus ? (
          <AevatarInspectorEmpty description="Choose a command-center surface to inspect details without losing the main dashboard." />
        ) : focus === "human-workflows" ? (
          <FocusList
            emptyText="No human-loop workflows were discovered in the runtime catalog."
            items={humanFocusedWorkflows.map((item) => ({
              action: () =>
                history.push(
                  buildRuntimeRunsHref({
                    workflow: item.name,
                  }),
                ),
              actionLabel: "Open runs",
              description: item.description || "Workflow ready for human approval or signal choreography.",
              label: item.name,
              status: item.requiresLlmProvider ? "active" : "draft",
            }))}
          />
        ) : focus === "live-actors" ? (
          <FocusList
            emptyText="No live actors are currently available."
            items={liveActors.map((item) => ({
              action: () =>
                history.push(
                  buildRuntimeExplorerHref({
                    actorId: item.id,
                  }),
                ),
              actionLabel: "Open explorer",
              description: item.description || item.type,
              label: item.id,
              status: "live",
            }))}
          />
        ) : focus === "connectors" ? (
          <FocusList
            emptyText="No connector information is currently available."
            items={(capabilitiesQuery.data?.connectors ?? []).map((item) => ({
              action: () => history.push(buildRuntimePrimitivesHref()),
              actionLabel: "Open primitives",
              description: `${item.type} · ${item.allowedOperations.join(", ") || "No operations declared"}`,
              label: item.name,
              status: item.enabled ? "ready" : "disabled",
            }))}
          />
        ) : focus === "primitives" ? (
          <FocusList
            emptyText="No primitives are currently available."
            items={(capabilitiesQuery.data?.primitives ?? []).map((item) => ({
              action: () =>
                history.push(
                  buildRuntimePrimitivesHref({
                    primitive: item.name,
                  }),
                ),
              actionLabel: "Inspect primitive",
              description: item.description || item.category,
              label: item.name,
              status: item.closedWorldBlocked ? "blocked" : "ready",
            }))}
          />
        ) : (
          <FocusList
            emptyText="No catalog items are currently visible."
            items={visibleCatalogItems.map((item) => ({
              action: () =>
                history.push(
                  buildRuntimeWorkflowsHref({
                    workflow: item.name,
                  }),
                ),
              actionLabel: "Open workflow",
              description: item.description || item.groupLabel,
              label: item.name,
              status: item.requiresLlmProvider ? "active" : "draft",
            }))}
          />
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

const GhostBoardCard: React.FC<{
  description: string;
  onOpen: () => void;
  title: string;
}> = ({ description, onOpen, title }) => (
  <ProCard
    bodyStyle={{ padding: 16 }}
    ghost
    style={{
      background: "rgba(255, 255, 255, 0.5)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
    }}
  >
    <Space direction="vertical" size={8} style={{ width: "100%" }}>
      <Typography.Text strong>{title}</Typography.Text>
      <Typography.Text type="secondary">{description}</Typography.Text>
      <Button onClick={onOpen}>Inspect</Button>
    </Space>
  </ProCard>
);

const FocusList: React.FC<{
  emptyText: string;
  items: Array<{
    action: () => void;
    actionLabel: string;
    description: string;
    label: string;
    status: string;
  }>;
}> = ({ emptyText, items }) =>
  items.length === 0 ? (
    <Empty description={emptyText} image={Empty.PRESENTED_IMAGE_SIMPLE} />
  ) : (
    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
      {items.map((item) => (
        <div
          key={item.label}
          style={{
            border: "1px solid var(--ant-color-border-secondary)",
            borderRadius: 12,
            display: "flex",
            flexDirection: "column",
            gap: 8,
            padding: 12,
          }}
        >
          <Space wrap size={[8, 8]}>
            <Typography.Text strong>{item.label}</Typography.Text>
            <AevatarStatusTag
              domain={
                item.status === "live" ? "observation" : "governance"
              }
              status={item.status}
            />
          </Space>
          <Typography.Text type="secondary">{item.description}</Typography.Text>
          <Button onClick={item.action}>{item.actionLabel}</Button>
        </div>
      ))}
    </div>
  );

export default OverviewPage;
