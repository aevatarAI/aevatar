import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProList,
} from "@ant-design/pro-components";
import type { ProDescriptionsItemProps } from "@ant-design/pro-components";
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
import { history } from "@umijs/max";
import { formatDateTime } from "@/shared/datetime/dateTime";
import {
  cardStackStyle,
  fillCardStyle,
  moduleCardProps,
  scrollPanelStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";
import type {
  ConsoleProfileItem,
  ObservabilityOverviewItem,
} from "./useOverviewData";
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

const capabilitySurfacesCardStyle = {
  ...fillCardStyle,
  height: 640,
};

const capabilitySurfacesScrollStyle = {
  ...scrollPanelStyle,
  maxHeight: 552,
};

const profileColumns: ProDescriptionsItemProps<ConsoleProfileItem>[] = [
  {
    title: "Preferred workflow",
    dataIndex: "preferredWorkflow",
    render: (_, row) => <Tag color="processing">{row.preferredWorkflow}</Tag>,
  },
  {
    title: "Observability",
    dataIndex: "observability",
  },
];

const OverviewPage: React.FC = () => {
  const {
    agentsQuery,
    capabilitiesQuery,
    configuredObservabilityCount,
    grafanaBaseUrl,
    humanFocusedWorkflows,
    liveActors,
    observabilityTargets,
    preferences,
    profileData,
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
        actionLabel: "Open workflows",
        onOpen: () => history.push("/workflows"),
      },
      {
        id: "surface-runtime-state",
        title: "Runtime state",
        summary: `${agentsQuery.data?.length ?? 0} live actors`,
        description:
          "Inspect active runs, runtime actors, and execution-side state directly from the runtime-facing surfaces.",
        actionLabel: "Open runs",
        onOpen: () => history.push("/runs"),
      },
      {
        id: "surface-primitives",
        title: "Primitive browser",
        summary: `${
          capabilitiesQuery.data?.primitives.length ?? 0
        } capabilities`,
        description:
          "Inspect primitive categories, parameters, aliases, and the workflows that currently use them.",
        actionLabel: "Open primitives",
        onOpen: () => history.push("/primitives"),
      },
      {
        id: "surface-scopes",
        title: "Scope assets",
        summary: "Published workflows and scripts",
        description:
          "Inspect scope-owned workflow and script assets directly from GAgentService.",
        actionLabel: "Open scopes",
        onOpen: () => history.push("/scopes"),
      },
      {
        id: "surface-services",
        title: "Service runtime",
        summary: "Lifecycle, deployments, and traffic",
        description:
          "Inspect service catalog snapshots, revisions, serving targets, rollouts, and traffic.",
        actionLabel: "Open services",
        onOpen: () => history.push("/services"),
      },
      {
        id: "surface-governance",
        title: "Governance",
        summary: "Bindings, policies, and endpoint exposure",
        description:
          "Inspect service governance state and activation capability views.",
        actionLabel: "Open governance",
        onOpen: () => history.push("/governance"),
      },
      {
        id: "surface-observability",
        title: "Observability",
        summary: `${configuredObservabilityCount}/${observabilityTargets.length} targets configured`,
        description:
          "Drive Grafana, Jaeger, Loki, and other external tools with the current runtime context.",
        actionLabel: "Open observability",
        onOpen: () =>
          history.push(
            `/observability?workflow=${encodeURIComponent(
              preferences.preferredWorkflow
            )}`
          ),
      },
    ],
    [
      agentsQuery.data?.length,
      configuredObservabilityCount,
      observabilityTargets.length,
      preferences.preferredWorkflow,
      capabilitiesQuery.data?.primitives.length,
      visibleCatalogItems.length,
    ]
  );
  const platformQuickActions = useMemo<QuickActionItem[]>(
    () => [
      {
        id: "quick-start-preferred",
        label: "Start preferred workflow",
        primary: true,
        onOpen: () =>
          history.push(
            `/runs?workflow=${encodeURIComponent(
              preferences.preferredWorkflow
            )}`
          ),
      },
      {
        id: "quick-workflows",
        label: "Open workflow library",
        onOpen: () => history.push("/workflows"),
      },
      {
        id: "quick-runs",
        label: "Open runs",
        onOpen: () => history.push("/runs"),
      },
      {
        id: "quick-actors",
        label: "Open runtime explorer",
        onOpen: () => history.push("/actors"),
      },
      {
        id: "quick-scopes",
        label: "Open scopes",
        onOpen: () => history.push("/scopes"),
      },
      {
        id: "quick-services",
        label: "Open services",
        onOpen: () => history.push("/services"),
      },
      {
        id: "quick-governance",
        label: "Open governance",
        onOpen: () => history.push("/governance"),
      },
      {
        id: "quick-primitives",
        label: "Open primitives",
        onOpen: () => history.push("/primitives"),
      },
    ],
    [preferences.preferredWorkflow]
  );
  const localQuickActions = useMemo<QuickActionItem[]>(
    () =>
      [
        {
          id: "quick-runtime-settings",
          label: "Open runtime settings",
          onOpen: () => history.push("/settings/runtime"),
        },
        {
          id: "quick-console-settings",
          label: "Open console settings",
          onOpen: () => history.push("/settings/console"),
        },
        {
          id: "quick-observability",
          label: "Open observability",
          onOpen: () =>
            history.push(
              `/observability?workflow=${encodeURIComponent(
                preferences.preferredWorkflow
              )}`
            ),
        },
        grafanaBaseUrl
          ? {
              id: "quick-grafana-explore",
              label: "Open Grafana Explore",
              href: `${grafanaBaseUrl}/explore`,
              target: "_blank",
              rel: "noreferrer",
            }
          : null,
      ].filter(Boolean) as QuickActionItem[],
    [grafanaBaseUrl, preferences.preferredWorkflow]
  );

  return (
    <PageContainer
      title="Overview"
      content="Overview of runtime workflows, scope assets, services, governance, actors, and observability."
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
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Space direction="vertical" size={8}>
              <Typography.Text strong>Preferred workflow</Typography.Text>
              <Tag color="processing">{preferences.preferredWorkflow}</Tag>
            </Space>
          </ProCard>
        </Col>
        <Col xs={24} lg={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Space direction="vertical" size={8}>
              <Typography.Text strong>Observability</Typography.Text>
              {grafanaBaseUrl ? (
                <Button
                  type="link"
                  href={grafanaBaseUrl}
                  target="_blank"
                  rel="noreferrer"
                  style={{ paddingInline: 0 }}
                >
                  Open Grafana
                </Button>
              ) : (
                <Tag>Not configured</Tag>
              )}
            </Space>
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} xl={16} style={stretchColumnStyle}>
          <ProCard
            title="Quick actions"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <Space direction="vertical" style={{ width: "100%" }} size={16}>
              <div>
                <Typography.Text strong>Platform entry points</Typography.Text>
                <Typography.Text
                  type="secondary"
                  style={{ display: "block", marginTop: 4 }}
                >
                  Open runtime, scope, service, governance, and capability
                  surfaces.
                </Typography.Text>
                <div style={{ marginTop: 12 }}>
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
              </div>

              <div>
                <Typography.Text strong>Local console tools</Typography.Text>
                <Typography.Text
                  type="secondary"
                  style={{ display: "block", marginTop: 4 }}
                >
                  Jump into browser-level preferences, local runtime
                  configuration, and external observability tools.
                </Typography.Text>
                <div style={{ marginTop: 12 }}>
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
              </div>

              <div>
                <Typography.Text strong>
                  Human-in-the-loop workflows
                </Typography.Text>
                <div style={{ marginTop: 12 }}>
                  <Space wrap size={[8, 8]}>
                    {humanFocusedWorkflows.length > 0 ? (
                      humanFocusedWorkflows.map((item) => (
                        <Button
                          key={item.name}
                          type="dashed"
                          onClick={() =>
                            history.push(
                              `/runs?workflow=${encodeURIComponent(item.name)}`
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
            </Space>
          </ProCard>
        </Col>

        <Col xs={24} xl={8} style={stretchColumnStyle}>
          <ProCard
            title="Console profile"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <Space direction="vertical" style={{ width: "100%" }} size={16}>
              <ProDescriptions<ConsoleProfileItem>
                column={1}
                dataSource={profileData}
                columns={profileColumns}
              />

              <div>
                <Typography.Text strong>Live actor shortcuts</Typography.Text>
                <div style={{ marginTop: 12 }}>
                  <Space wrap size={[8, 8]}>
                    {liveActors.length > 0 ? (
                      liveActors.map((agent) => (
                        <Button
                          key={agent.id}
                          onClick={() =>
                            history.push(
                              `/actors?actorId=${encodeURIComponent(agent.id)}`
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
            </Space>
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard
            title="Capability surfaces"
            {...moduleCardProps}
            style={capabilitySurfacesCardStyle}
          >
            <div style={capabilitySurfacesScrollStyle}>
              <Row gutter={[16, 16]}>
                {capabilitySurfaceItems.map((item) => (
                  <Col key={item.id} xs={24} md={12} style={stretchColumnStyle}>
                    <ProCard style={fillCardStyle}>
                      <div style={cardStackStyle}>
                        <Space direction="vertical" size={4}>
                          <Typography.Text strong>{item.title}</Typography.Text>
                          <Tag color="processing">{item.summary}</Tag>
                        </Space>
                        <Typography.Text type="secondary">
                          {item.description}
                        </Typography.Text>
                        <Button type="primary" onClick={item.onOpen}>
                          {item.actionLabel}
                        </Button>
                      </div>
                    </ProCard>
                  </Col>
                ))}
              </Row>
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
                description={String(capabilitiesQuery.error)}
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
                  <Tag>
                    {capabilitiesQuery.data?.primitives.length ?? 0} primitives
                  </Tag>
                  <Tag>
                    {capabilitiesQuery.data?.connectors.length ?? 0} connectors
                  </Tag>
                  <Tag>
                    {capabilitiesQuery.data?.workflows.length ?? 0} workflows
                  </Tag>
                </Space>
                <Row gutter={[12, 12]}>
                  <Col xs={24} sm={8}>
                    <ProCard size="small">
                      <Statistic
                        title="Primitives"
                        value={capabilitiesQuery.data?.primitives.length ?? 0}
                      />
                    </ProCard>
                  </Col>
                  <Col xs={24} sm={8}>
                    <ProCard size="small">
                      <Statistic
                        title="Connectors"
                        value={capabilitiesQuery.data?.connectors.length ?? 0}
                      />
                    </ProCard>
                  </Col>
                  <Col xs={24} sm={8}>
                    <ProCard size="small">
                      <Statistic
                        title="Workflows"
                        value={capabilitiesQuery.data?.workflows.length ?? 0}
                      />
                    </ProCard>
                  </Col>
                </Row>

                <div>
                  <Typography.Text strong>Primitive categories</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 8 }}
                  >
                    {capabilityPrimitiveCategorySummary.length > 0
                      ? capabilityPrimitiveCategorySummary.join(" · ")
                      : "No primitive categories were returned by the runtime capability digest."}
                  </Typography.Text>
                </div>

                <div>
                  <Typography.Text strong>
                    Connector availability
                  </Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 8 }}
                  >
                    {capabilityConnectorSummary}
                  </Typography.Text>
                </div>

                <div>
                  <Typography.Text strong>Workflow source mix</Typography.Text>
                  <Typography.Text
                    type="secondary"
                    style={{ display: "block", marginTop: 8 }}
                  >
                    {capabilityWorkflowSourceSummary.length > 0
                      ? capabilityWorkflowSourceSummary.join(" · ")
                      : "No capability workflows were exposed by the backend."}
                  </Typography.Text>
                </div>

                <Typography.Text type="secondary">
                  Overview keeps this as a digest. Use Primitives, Workflows,
                  and Runtime Settings for full details.
                </Typography.Text>

                <Space wrap>
                  <Button onClick={() => history.push("/primitives")}>
                    Open primitive browser
                  </Button>
                  <Button onClick={() => history.push("/workflows")}>
                    Open workflow library
                  </Button>
                  <Button onClick={() => history.push("/settings/runtime")}>
                    Open runtime settings
                  </Button>
                </Space>
              </div>
            )}
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} style={stretchColumnStyle}>
          <ProCard
            title="Observability targets"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProList<ObservabilityOverviewItem>
              rowKey="id"
              search={false}
              split
              dataSource={observabilityTargets}
              locale={{
                emptyText: (
                  <Typography.Text type="secondary">
                    No observability targets configured.
                  </Typography.Text>
                ),
              }}
              metas={{
                title: {
                  dataIndex: "label",
                  render: (_, record) => (
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{record.label}</Typography.Text>
                      <Tag
                        color={
                          record.status === "configured" ? "success" : "default"
                        }
                      >
                        {record.status}
                      </Tag>
                    </Space>
                  ),
                },
                description: {
                  dataIndex: "description",
                },
                subTitle: {
                  render: (_, record) =>
                    record.homeUrl ? (
                      <Tag>{record.homeUrl}</Tag>
                    ) : (
                      <Tag>No URL configured</Tag>
                    ),
                },
                actions: {
                  render: (_, record) => [
                    <Button
                      key={`${record.id}-observability`}
                      type="link"
                      onClick={() =>
                        history.push(
                          `/observability?workflow=${encodeURIComponent(
                            preferences.preferredWorkflow
                          )}`
                        )
                      }
                    >
                      Open hub
                    </Button>,
                    <Button
                      key={`${record.id}-external`}
                      type="link"
                      disabled={record.status !== "configured"}
                      href={record.homeUrl || undefined}
                      target="_blank"
                      rel="noreferrer"
                    >
                      Open
                    </Button>,
                  ],
                },
              }}
            />
          </ProCard>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default OverviewPage;
