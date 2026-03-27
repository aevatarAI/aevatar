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
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
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

type CapabilitySurfaceItem = {
  id: string;
  title: string;
  summary: string;
  description: string;
  actionLabel: string;
  onOpen: () => void;
};

type QuickActionItem = {
  id: string;
  label: string;
  onOpen?: () => void;
  href?: string;
  target?: string;
  rel?: string;
  primary?: boolean;
};

const overviewSurfaceGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 12,
  gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
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
  const capabilitySurfaceItems = useMemo<CapabilitySurfaceItem[]>(
    () => [
      {
        id: "surface-runtime-workflows",
        title: "Runtime workflows",
        summary: `${visibleCatalogItems.length} library entries`,
        description:
          "Browse runtime workflow definitions, inspect coverage, and launch runs from the runtime-facing workflow library.",
        actionLabel: "Open Runtime Workflows",
        onOpen: () => history.push(buildRuntimeWorkflowsHref()),
      },
      {
        id: "surface-primitives",
        title: "Primitive browser",
        summary: `${
          capabilitiesQuery.data?.primitives.length ?? 0
        } capabilities`,
        description:
          "Inspect primitive categories, parameters, aliases, and the workflows that currently use them.",
        actionLabel: "Open Runtime Primitives",
        onOpen: () => history.push(buildRuntimePrimitivesHref()),
      },
      {
        id: "surface-runtime-runs",
        title: "Runtime runs",
        summary: "SSE / WS console",
        description:
          "Start runs, monitor live events, and handle resume or signal interactions from the runtime console.",
        actionLabel: "Open Runtime Runs",
        onOpen: () => history.push(buildRuntimeRunsHref()),
      },
      {
        id: "surface-runtime-explorer",
        title: "Runtime explorer",
        summary: `${agentsQuery.data?.length ?? 0} live actors`,
        description:
          "Inspect actor snapshots, timeline history, and graph topology for the current workflow runtime.",
        actionLabel: "Open Runtime Explorer",
        onOpen: () => history.push(buildRuntimeExplorerHref()),
      },
      {
        id: "surface-scopes",
        title: "Scope assets",
        summary: "Published workflows and scripts",
        description:
          "Inspect scope-owned workflow and script assets without exposing tenantId or appId in the frontend.",
        actionLabel: "Open scopes",
        onOpen: () => history.push("/scopes"),
      },
      {
        id: "surface-services",
        title: "Platform services",
        summary: "Raw lifecycle, deployments, and traffic",
        description:
          "Inspect the raw platform service catalog keyed by tenantId, appId, and namespace.",
        actionLabel: "Open platform services",
        onOpen: () => history.push("/services"),
      },
      {
        id: "surface-governance",
        title: "Platform governance",
        summary: "Raw bindings, policies, and endpoint exposure",
        description:
          "Inspect raw governance state and activation capability views for concrete platform service identities.",
        actionLabel: "Open platform governance",
        onOpen: () => history.push("/governance"),
      },
    ],
    [
      agentsQuery.data?.length,
      capabilitiesQuery.data?.primitives.length,
      visibleCatalogItems.length,
    ]
  );
  const platformQuickActions = useMemo<QuickActionItem[]>(
    () => [
      {
        id: "quick-start-direct",
        label: "Start direct workflow",
        primary: true,
        onOpen: () => history.push(buildRuntimeRunsHref({ workflow: "direct" })),
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
      {
        id: "quick-scopes",
        label: "Open scopes",
        onOpen: () => history.push("/scopes"),
      },
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
    []
  );
  const localQuickActions = useMemo<QuickActionItem[]>(
    () => [
      {
        id: "quick-console-settings",
        label: "Open settings",
        onOpen: () => history.push("/settings"),
      },
    ],
    []
  );

  return (
    <PageContainer
      title="Overview"
      content="Overview of runtime workflows, scope assets, raw platform services, platform governance, and actors."
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
        <Col xs={24} xl={16} style={stretchColumnStyle}>
          <ProCard title="Quick actions" {...moduleCardProps} style={fillCardStyle}>
            <div style={cardStackStyle}>
              <div style={quickActionSectionStyle}>
                <div>
                  <Typography.Text strong>Platform entry points</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 4 }}
                  >
                    Open runtime, scope, service, governance, and capability
                    surfaces.
                  </Typography.Text>
                </div>
                <Space wrap size={[8, 8]}>
                  {platformQuickActions.map((item) => (
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

              <div style={quickActionSectionStyle}>
                <div>
                  <Typography.Text strong>Local console tools</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 4 }}
                  >
                    Jump into account settings and local console entry points.
                  </Typography.Text>
                </div>
                <Space wrap size={[8, 8]}>
                  {localQuickActions.map((item) => (
                    <Button
                      key={item.id}
                      href={item.href}
                      onClick={item.onOpen}
                      target={item.target}
                      rel={item.rel}
                    >
                      {item.label}
                    </Button>
                  ))}
                </Space>
              </div>

              <div style={quickActionSectionStyle}>
                <div>
                  <Typography.Text strong>
                    Human-in-the-loop workflows
                  </Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 4 }}
                  >
                    Jump straight into the workflows that currently expose
                    human approval, human input, or wait-signal primitives.
                  </Typography.Text>
                </div>
                <div>
                  <Space wrap size={[8, 8]}>
                    {humanFocusedWorkflows.length > 0 ? (
                      humanFocusedWorkflows.map((item) => (
                        <Button
                          key={item.name}
                          type="dashed"
                          onClick={() =>
                            history.push(
                              buildRuntimeRunsHref({
                                workflow: item.name,
                              })
                            )
                          }
                        >
                          {item.name}
                        </Button>
                      ))
                    ) : (
                      <Typography.Text type="secondary">
                        No human-interaction workflows were discovered in the
                        catalog.
                      </Typography.Text>
                    )}
                  </Space>
                </div>
              </div>
            </div>
          </ProCard>
        </Col>

        <Col xs={24} xl={8} style={stretchColumnStyle}>
          <ProCard title="Console profile" {...moduleCardProps} style={fillCardStyle}>
            <div style={cardStackStyle}>
              <div style={quickActionSectionStyle}>
                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Library workflows"
                    value={visibleCatalogItems.length}
                  />
                  <SummaryField
                    label="Live actors"
                    value={agentsQuery.data?.length ?? 0}
                  />
                </div>

                <Space wrap size={[8, 8]}>
                  <Button onClick={() => history.push("/settings")}>
                    Open settings
                  </Button>
                </Space>
              </div>

              <div style={quickActionSectionStyle}>
                <div>
                  <Typography.Text strong>Live actor shortcuts</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 4 }}
                  >
                    Open the explorer with a currently active actor already in
                    focus.
                  </Typography.Text>
                </div>
                <div>
                  <Space wrap size={[8, 8]}>
                    {liveActors.length > 0 ? (
                      liveActors.map((agent) => (
                        <Button
                          key={agent.id}
                          onClick={() =>
                            history.push(
                              buildRuntimeExplorerHref({
                                actorId: agent.id,
                              })
                            )
                          }
                        >
                          {agent.id}
                        </Button>
                      ))
                    ) : (
                      <Typography.Text type="secondary">
                        No live actors were returned by the backend.
                      </Typography.Text>
                    )}
                  </Space>
                </div>
              </div>
            </div>
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard
            title="Capability surfaces"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <div style={overviewSurfaceGridStyle}>
              {capabilitySurfaceItems.map((item) => (
                <div key={item.id} style={cardListItemStyle}>
                  <div style={cardListHeaderStyle}>
                    <div style={cardListMainStyle}>
                      <Typography.Text strong>{item.title}</Typography.Text>
                      <Typography.Text type="secondary">
                        {item.description}
                      </Typography.Text>
                    </div>
                  </div>

                  <Space wrap size={[8, 8]}>
                    <Tag color="processing">{item.summary}</Tag>
                  </Space>

                  <div style={cardListActionStyle}>
                    <Button type="primary" onClick={item.onOpen}>
                      {item.actionLabel}
                    </Button>
                  </div>
                </div>
              ))}
            </div>
          </ProCard>
        </Col>
        <Col xs={24} xl={10} style={stretchColumnStyle}>
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
                    Overview keeps this as a digest. Use Primitives and
                    Workflows for the rest of the runtime operating context.
                  </Typography.Text>
                  <Space wrap>
                    <Button
                      onClick={() => history.push(buildRuntimePrimitivesHref())}
                    >
                      Open Runtime Primitives
                    </Button>
                    <Button
                      onClick={() => history.push(buildRuntimeWorkflowsHref())}
                    >
                      Open Runtime Workflows
                    </Button>
                  </Space>
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
