import { PageContainer, ProCard } from "@ant-design/pro-components";
import {
  Alert,
  Button,
  Col,
  Row,
  Space,
  Statistic,
  Tag,
  Typography,
} from "antd";
import React, { useMemo } from "react";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimePrimitivesHref,
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import { formatDateTime } from "@/shared/datetime/dateTime";
import {
  cardStackStyle,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";
import { describeError } from "@/shared/ui/errorText";
import { useOverviewData } from "./useOverviewData";

type QuickActionItem = {
  id: string;
  label: string;
  onOpen?: () => void;
  href?: string;
  target?: string;
  rel?: string;
  primary?: boolean;
};

type ActionGroup = {
  id: string;
  title: string;
  description: string;
  items: QuickActionItem[];
  emptyState?: string;
};

const summarySectionStyle: React.CSSProperties = {
  borderTop: "1px solid var(--ant-color-border-secondary)",
  display: "flex",
  flexDirection: "column",
  gap: 8,
  paddingTop: 12,
};

const quickActionSectionStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  background: "var(--ant-color-fill-quaternary)",
  display: "flex",
  flexDirection: "column",
  gap: 12,
};

const overviewSummaryMetricGridStyle: React.CSSProperties = {
  ...summaryMetricGridStyle,
  gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text>{value}</Typography.Text>
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const OverviewPage: React.FC = () => {
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
  const currentProjectActions = useMemo<QuickActionItem[]>(
    () => [
      {
        id: "current-project",
        label: "Enter Current Project",
        onOpen: () => history.push("/scopes"),
        primary: true,
      },
      {
        id: "current-studio",
        label: "Open Studio",
        onOpen: () => history.push("/studio"),
      },
    ],
    []
  );
  const operatorToolGroups = useMemo<ActionGroup[]>(
    () => [
      {
        id: "operator-runtime",
        title: "Runtime tools",
        description:
          "Workflow, primitive, run, and explorer views for runtime diagnostics.",
        items: [
          {
            id: "quick-start-direct",
            label: "Start direct workflow",
            primary: true,
            onOpen: () =>
              history.push(buildRuntimeRunsHref({ workflow: "direct" })),
          },
          {
            id: "quick-workflows",
            label: "Open Runtime Workflows",
            onOpen: () => history.push(buildRuntimeWorkflowsHref()),
          },
          {
            id: "quick-primitives",
            label: "Open Runtime Primitives",
            onOpen: () => history.push(buildRuntimePrimitivesHref()),
          },
          {
            id: "quick-runs",
            label: "Open Runtime Runs",
            onOpen: () => history.push(buildRuntimeRunsHref()),
          },
          {
            id: "quick-actors",
            label: "Open Runtime Explorer",
            onOpen: () => history.push(buildRuntimeExplorerHref()),
          },
        ],
      },
      {
        id: "operator-platform",
        title: "Platform operator views",
        description:
          "Raw platform services and governance surfaces for tenant/app/namespace operations.",
        items: [
          {
            id: "quick-services",
            label: "Open platform services",
            onOpen: () => history.push("/services"),
          },
          {
            id: "quick-governance",
            label: "Open platform governance",
            onOpen: () => history.push("/governance"),
          },
        ],
      },
      {
        id: "operator-console",
        title: "Console tools",
        description:
          "Account and local console settings that are not part of the project-facing path.",
        items: [
          {
            id: "quick-console-settings",
            label: "Open settings",
            onOpen: () => history.push("/settings"),
          },
        ],
      },
      {
        id: "operator-human-workflows",
        title: "Human-in-the-loop workflows",
        description:
          "Jump straight into workflows that currently need approval, input, or wait-signal handling.",
        items: humanFocusedWorkflows.map((item) => ({
          id: `human-workflow-${item.name}`,
          label: item.name,
          onOpen: () =>
            history.push(
              buildRuntimeRunsHref({
                workflow: item.name,
              })
            ),
        })),
        emptyState: "No human-interaction workflows were discovered in the catalog.",
      },
      {
        id: "operator-live-actors",
        title: "Live actor shortcuts",
        description:
          "Open the explorer with a currently active actor already in focus.",
        items: liveActors.map((agent) => ({
          id: `live-actor-${agent.id}`,
          label: agent.id,
          onOpen: () =>
            history.push(
              buildRuntimeExplorerHref({
                actorId: agent.id,
              })
            ),
        })),
        emptyState: "No live actors were returned by the backend.",
      },
    ],
    [humanFocusedWorkflows, liveActors]
  );

  return (
    <PageContainer
      title="Overview"
      content="Start from the current project for scope-first workflow, script, and binding flows. Runtime and platform diagnostics stay grouped under Operator Tools."
    >
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic
              title="Registered workflows"
              value={workflowsQuery.data?.length ?? 0}
            />
          </ProCard>
        </Col>
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic
              title="Live actors"
              value={agentsQuery.data?.length ?? 0}
            />
          </ProCard>
        </Col>
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic
              title="Runtime primitives"
              value={capabilitiesQuery.data?.primitives.length ?? 0}
            />
          </ProCard>
        </Col>
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic
              title="Library workflows"
              value={visibleCatalogItems.length}
            />
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} xl={10} style={stretchColumnStyle}>
          <ProCard title="Current project" {...moduleCardProps} style={fillCardStyle}>
            <div style={cardStackStyle}>
              <div style={quickActionSectionStyle}>
                <div>
                  <Typography.Text strong>Scope-first path</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 4 }}
                  >
                    Use the current project as the main user-facing entry. Open
                    Studio when you need to author or rebind workflows, scripts,
                    or GAgents.
                  </Typography.Text>
                </div>
                <Space wrap size={[8, 8]}>
                  {currentProjectActions.map((item) => (
                    <Button
                      key={item.id}
                      type={item.primary ? "primary" : "default"}
                      onClick={item.onOpen}
                    >
                      {item.label}
                    </Button>
                  ))}
                </Space>
              </div>
            </div>
          </ProCard>
        </Col>

        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard title="Operator Tools" {...moduleCardProps} style={fillCardStyle}>
            <div style={cardStackStyle}>
              {operatorToolGroups.map((group) => (
                <div key={group.id} style={quickActionSectionStyle}>
                  <div>
                    <Typography.Text strong>{group.title}</Typography.Text>
                    <Typography.Text
                      type="secondary"
                      style={{ display: "block", marginTop: 4 }}
                    >
                      {group.description}
                    </Typography.Text>
                  </div>
                  {group.items.length > 0 ? (
                    <Space wrap size={[8, 8]}>
                      {group.items.map((item) => (
                        <Button
                          key={item.id}
                          type={item.primary ? "primary" : "default"}
                          href={item.href}
                          onClick={item.onOpen}
                          target={item.target}
                          rel={item.rel}
                        >
                          {item.label}
                        </Button>
                      ))}
                    </Space>
                  ) : (
                    <div>
                      <Typography.Text type="secondary">
                        {group.emptyState || "No operator tools are available."}
                      </Typography.Text>
                    </div>
                  )}
                </div>
              ))}
            </div>
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} style={stretchColumnStyle}>
          <ProCard
            title="Capability digest"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            {capabilitiesQuery.isError ? (
              <Alert
                showIcon
                type="error"
                title="Failed to load capability digest"
                description={describeError(capabilitiesQuery.error)}
              />
            ) : (
              <div style={cardStackStyle}>
                <Space wrap size={[8, 8]}>
                  <Tag color="processing">
                    {capabilitiesQuery.data?.schemaVersion ?? "capabilities.v1"}
                  </Tag>
                  <Tag>
                    Updated{" "}
                    {capabilitiesQuery.data?.generatedAtUtc
                      ? formatDateTime(capabilitiesQuery.data.generatedAtUtc)
                      : "n/a"}
                  </Tag>
                </Space>

                <div style={overviewSummaryMetricGridStyle}>
                  <SummaryMetric
                    label="Primitives"
                    value={capabilitiesQuery.data?.primitives.length ?? 0}
                  />
                  <SummaryMetric
                    label="Connectors"
                    value={capabilitiesQuery.data?.connectors.length ?? 0}
                  />
                  <SummaryMetric
                    label="Workflows"
                    value={capabilitiesQuery.data?.workflows.length ?? 0}
                  />
                </div>

                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Primitive categories"
                    value={
                      capabilityPrimitiveCategorySummary.length > 0
                        ? capabilityPrimitiveCategorySummary.join(" · ")
                        : "No primitive categories returned."
                    }
                  />
                  <SummaryField
                    label="Connector availability"
                    value={capabilityConnectorSummary}
                  />
                  <SummaryField
                    label="Workflow source mix"
                    value={
                      capabilityWorkflowSourceSummary.length > 0
                        ? capabilityWorkflowSourceSummary.join(" · ")
                        : "No capability workflows were exposed."
                    }
                  />
                </div>

                <div style={summarySectionStyle}>
                  <Typography.Text
                    type="secondary"
                  >
                    Overview keeps this as a digest. Use Operator Tools when
                    you need to drill into runtime or platform operating
                    surfaces.
                  </Typography.Text>
                </div>
              </div>
            )}
          </ProCard>
        </Col>
      </Row>

    </PageContainer>
  );
};

export default OverviewPage;
