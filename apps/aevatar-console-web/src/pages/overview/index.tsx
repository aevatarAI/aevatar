import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProList,
} from '@ant-design/pro-components';
import type { ProDescriptionsItemProps } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Col, Row, Space, Statistic, Tag, Typography } from 'antd';
import React, { useMemo } from 'react';
import { history } from '@umijs/max';
import { consoleApi } from '@/shared/api/consoleApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { buildObservabilityTargets } from '@/shared/observability/observabilityLinks';
import { loadConsolePreferences } from '@/shared/preferences/consolePreferences';
import { listVisibleWorkflowCatalogItems } from '@/shared/workflows/catalogVisibility';
import { buildStudioRoute } from '@/shared/studio/navigation';
import {
  cardStackStyle,
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
} from '@/shared/ui/proComponents';

type ConsoleProfileItem = {
  preferredWorkflow: string;
  observability: string;
};

type ObservabilityOverviewItem = {
  id: string;
  label: string;
  description: string;
  status: 'configured' | 'missing';
  homeUrl: string;
};

type StudioSurfaceItem = {
  id: string;
  title: string;
  summary: string;
  description: string;
  actionLabel: string;
  onOpen: () => void;
};

const profileColumns: ProDescriptionsItemProps<ConsoleProfileItem>[] = [
  {
    title: 'Preferred workflow',
    dataIndex: 'preferredWorkflow',
    render: (_, row) => <Tag color="processing">{row.preferredWorkflow}</Tag>,
  },
  {
    title: 'Observability',
    dataIndex: 'observability',
  },
];

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/, '');
}

const OverviewPage: React.FC = () => {
  const preferences = useMemo(() => loadConsolePreferences(), []);
  const workflowsQuery = useQuery({
    queryKey: ['overview-workflows'],
    queryFn: () => consoleApi.listWorkflows(),
  });
  const catalogQuery = useQuery({
    queryKey: ['overview-catalog'],
    queryFn: () => consoleApi.listWorkflowCatalog(),
  });
  const agentsQuery = useQuery({
    queryKey: ['overview-agents'],
    queryFn: () => consoleApi.listAgents(),
  });
  const capabilitiesQuery = useQuery({
    queryKey: ['overview-capabilities'],
    queryFn: () => consoleApi.getCapabilities(),
  });
  const visibleCatalogItems = useMemo(
    () => listVisibleWorkflowCatalogItems(catalogQuery.data ?? []),
    [catalogQuery.data],
  );

  const humanFocusedWorkflows = useMemo(
    () =>
      visibleCatalogItems
        .filter((item) =>
          item.primitives.some((primitive) =>
            ['human_input', 'human_approval', 'wait_signal'].includes(primitive),
          ),
        )
        .slice(0, 6),
    [visibleCatalogItems],
  );

  const capabilityPrimitivePreview = useMemo(
    () => (capabilitiesQuery.data?.primitives ?? []).slice(0, 5),
    [capabilitiesQuery.data],
  );
  const capabilityPrimitiveCategorySummary = useMemo(() => {
    const categoryCounts = new Map<string, number>();

    for (const primitive of capabilitiesQuery.data?.primitives ?? []) {
      categoryCounts.set(primitive.category, (categoryCounts.get(primitive.category) ?? 0) + 1);
    }

    return Array.from(categoryCounts.entries())
      .sort((left, right) => right[1] - left[1])
      .slice(0, 3)
      .map(([category, count]) => `${count} ${category}`);
  }, [capabilitiesQuery.data]);
  const capabilityConnectorPreview = useMemo(
    () => (capabilitiesQuery.data?.connectors ?? []).slice(0, 4),
    [capabilitiesQuery.data],
  );
  const capabilityConnectorEnabledCount = useMemo(
    () => (capabilitiesQuery.data?.connectors ?? []).filter((connector) => connector.enabled).length,
    [capabilitiesQuery.data],
  );
  const capabilityConnectorSummary = useMemo(() => {
    const connectors = capabilitiesQuery.data?.connectors ?? [];

    if (connectors.length === 0) {
      return 'No connectors exposed';
    }

    const previewNames = capabilityConnectorPreview.map((connector) => connector.name);
    const remainingCount = connectors.length - previewNames.length;

    return [
      `${capabilityConnectorEnabledCount}/${connectors.length} enabled`,
      previewNames.join(', '),
      remainingCount > 0 ? `+${remainingCount} more` : null,
    ]
      .filter(Boolean)
      .join(' · ');
  }, [
    capabilitiesQuery.data,
    capabilityConnectorEnabledCount,
    capabilityConnectorPreview,
  ]);
  const capabilityWorkflowSummary = useMemo(() => {
    const workflows = capabilitiesQuery.data?.workflows ?? [];
    const sourceCounts = new Map<string, number>();

    for (const workflow of workflows) {
      const source = workflow.source || 'runtime';
      sourceCounts.set(source, (sourceCounts.get(source) ?? 0) + 1);
    }

    return {
      llmRequiredCount: workflows.filter((workflow) => workflow.requiresLlmProvider).length,
      closedWorldCount: workflows.filter((workflow) => workflow.closedWorldMode).length,
      connectorLinkedCount: workflows.filter(
        (workflow) => workflow.requiredConnectors.length > 0,
      ).length,
      sourceSummary: Array.from(sourceCounts.entries())
        .sort((left, right) => right[1] - left[1])
        .slice(0, 3)
        .map(([source, count]) => `${count} ${source}`),
    };
  }, [capabilitiesQuery.data]);

  const liveActors = useMemo(() => (agentsQuery.data ?? []).slice(0, 6), [agentsQuery.data]);
  const grafanaBaseUrl = normalizeBaseUrl(preferences.grafanaBaseUrl);
  const profileData = useMemo<ConsoleProfileItem>(
    () => ({
      preferredWorkflow: preferences.preferredWorkflow,
      observability: grafanaBaseUrl ? 'Configured' : 'Not configured',
    }),
    [grafanaBaseUrl, preferences.preferredWorkflow],
  );
  const observabilityTargets = useMemo<ObservabilityOverviewItem[]>(
    () =>
      buildObservabilityTargets(preferences, {
        workflow: preferences.preferredWorkflow,
        actorId: '',
        commandId: '',
        runId: '',
        stepId: '',
      }).map((target) => ({
        id: target.id,
        label: target.label,
        description: target.description,
        status: target.status,
        homeUrl: target.homeUrl,
      })),
    [preferences],
  );
  const studioSurfaceItems = useMemo<StudioSurfaceItem[]>(
    () => [
      {
        id: 'surface-studio',
        title: 'Studio',
        summary: `${workflowsQuery.data?.length ?? 0} workspace-linked entries`,
        description:
          'Create, inspect, and run workflows from the Studio workbench.',
        actionLabel: 'Open Studio',
        onOpen: () => history.push('/studio'),
      },
      {
        id: 'surface-workflows',
        title: 'Workflow library',
        summary: `${visibleCatalogItems.length} library entries`,
        description:
          'Browse workflow definitions, inspect roles and steps, and launch a run from the selected workflow.',
        actionLabel: 'Open workflows',
        onOpen: () => history.push('/workflows'),
      },
      {
        id: 'surface-primitives',
        title: 'Primitive browser',
        summary: `${capabilitiesQuery.data?.primitives.length ?? 0} capabilities`,
        description:
          'Inspect primitive categories, parameters, aliases, and the workflows that currently use them.',
        actionLabel: 'Open primitives',
        onOpen: () => history.push('/primitives'),
      },
    ],
    [
      capabilitiesQuery.data?.primitives.length,
      visibleCatalogItems.length,
      workflowsQuery.data?.length,
    ],
  );

  return (
    <PageContainer
      title="Overview"
      content="Overview of workflows, runtime capabilities, actors, and observability."
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
          <ProCard title="Quick actions" {...moduleCardProps} style={fillCardStyle}>
          <Space direction="vertical" style={{ width: '100%' }} size={16}>
            <Space wrap size={[8, 8]}>
              <Button
                type="primary"
                onClick={() =>
                  history.push(
                    `/runs?workflow=${encodeURIComponent(preferences.preferredWorkflow)}`,
                  )
                }
              >
                Start preferred workflow
              </Button>
              <Button onClick={() => history.push(buildStudioRoute())}>Open Studio</Button>
              <Button onClick={() => history.push(buildStudioRoute({ draftMode: 'new' }))}>
                New Studio draft
              </Button>
              <Button onClick={() => history.push('/workflows')}>Open workflow library</Button>
              <Button onClick={() => history.push('/primitives')}>Open primitives</Button>
              <Button onClick={() => history.push('/settings')}>Open settings</Button>
              <Button
                onClick={() =>
                  history.push(
                    `/observability?workflow=${encodeURIComponent(
                      preferences.preferredWorkflow,
                    )}`,
                  )
                }
              >
                Open observability
              </Button>
              {grafanaBaseUrl ? (
                <Button href={`${grafanaBaseUrl}/explore`} target="_blank" rel="noreferrer">
                  Open Grafana Explore
                </Button>
              ) : null}
            </Space>

            <div>
              <Typography.Text strong>Human-in-the-loop workflows</Typography.Text>
              <div style={{ marginTop: 12 }}>
                <Space wrap size={[8, 8]}>
                  {humanFocusedWorkflows.length > 0 ? (
                    humanFocusedWorkflows.map((item) => (
                      <Button
                        key={item.name}
                        type="dashed"
                        onClick={() =>
                          history.push(`/runs?workflow=${encodeURIComponent(item.name)}`)
                        }
                      >
                        {item.name}
                      </Button>
                    ))
                  ) : (
                    <Typography.Text type="secondary">
                      No human-interaction workflows were discovered in the catalog.
                    </Typography.Text>
                  )}
                </Space>
              </div>
            </div>
          </Space>
          </ProCard>
        </Col>

        <Col xs={24} xl={8} style={stretchColumnStyle}>
          <ProCard title="Console profile" {...moduleCardProps} style={fillCardStyle}>
          <Space direction="vertical" style={{ width: '100%' }} size={16}>
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
                          history.push(`/actors?actorId=${encodeURIComponent(agent.id)}`)
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
          <ProCard title="Workflow tools" {...moduleCardProps} style={fillCardStyle}>
            <Row gutter={[16, 16]}>
              {studioSurfaceItems.map((item) => (
                <Col key={item.id} xs={24} md={12} style={stretchColumnStyle}>
                  <ProCard style={fillCardStyle}>
                    <div style={cardStackStyle}>
                      <Space direction="vertical" size={4}>
                        <Typography.Text strong>{item.title}</Typography.Text>
                        <Tag color="processing">{item.summary}</Tag>
                      </Space>
                      <Typography.Text type="secondary">{item.description}</Typography.Text>
                      <Button type="primary" onClick={item.onOpen}>
                        {item.actionLabel}
                      </Button>
                    </div>
                  </ProCard>
                </Col>
              ))}
            </Row>
          </ProCard>
        </Col>
        <Col xs={24} xl={10} style={stretchColumnStyle}>
          <ProCard
            title="Runtime capability snapshot"
            {...moduleCardProps}
            style={fillCardStyle}
          >
          {capabilitiesQuery.isError ? (
            <Alert
              showIcon
              type="error"
              message="Failed to load capability snapshot"
              description={String(capabilitiesQuery.error)}
            />
          ) : (
            <div style={cardStackStyle}>
              <Space wrap size={[8, 8]}>
                <Tag color="processing">
                  {capabilitiesQuery.data?.schemaVersion ?? 'capabilities.v1'}
                </Tag>
                <Tag>
                  Updated{' '}
                  {capabilitiesQuery.data?.generatedAtUtc
                    ? formatDateTime(capabilitiesQuery.data.generatedAtUtc)
                    : 'n/a'}
                </Tag>
                <Tag>{capabilitiesQuery.data?.primitives.length ?? 0} primitives</Tag>
                <Tag>{capabilitiesQuery.data?.connectors.length ?? 0} connectors</Tag>
                <Tag>{capabilitiesQuery.data?.workflows.length ?? 0} workflows</Tag>
              </Space>
              <Typography.Text type="secondary">
                Connectors: {capabilityConnectorSummary}
              </Typography.Text>

              <div>
                <Typography.Text strong>Primitive preview</Typography.Text>
                <div style={{ marginTop: 12 }}>
                  {capabilityPrimitivePreview.length > 0 ? (
                    <>
                      <Space wrap size={[8, 8]}>
                        {capabilityPrimitivePreview.map((primitive) => (
                          <Tag key={primitive.name} color="blue">
                            {primitive.name}
                          </Tag>
                        ))}
                      </Space>
                      <Typography.Text
                        type="secondary"
                        style={{ display: 'block', marginTop: 8 }}
                      >
                        {capabilityPrimitiveCategorySummary.join(' · ')}
                      </Typography.Text>
                    </>
                  ) : (
                    <Typography.Text type="secondary">
                      No primitives were returned by the runtime capability snapshot.
                    </Typography.Text>
                  )}
                </div>
              </div>

              <div>
                <Typography.Text strong>Workflow coverage</Typography.Text>
                <div style={{ marginTop: 12 }}>
                  {capabilitiesQuery.data?.workflows.length ? (
                    <>
                      <Row gutter={[12, 12]}>
                        <Col xs={24} sm={8}>
                          <ProCard size="small">
                            <Statistic title="LLM required" value={capabilityWorkflowSummary.llmRequiredCount} />
                          </ProCard>
                        </Col>
                        <Col xs={24} sm={8}>
                          <ProCard size="small">
                            <Statistic title="Closed world" value={capabilityWorkflowSummary.closedWorldCount} />
                          </ProCard>
                        </Col>
                        <Col xs={24} sm={8}>
                          <ProCard size="small">
                            <Statistic
                              title="Connector-linked"
                              value={capabilityWorkflowSummary.connectorLinkedCount}
                            />
                          </ProCard>
                        </Col>
                      </Row>
                      <Typography.Text
                        type="secondary"
                        style={{ display: 'block', marginTop: 8 }}
                      >
                        {capabilityWorkflowSummary.sourceSummary.length > 0
                          ? `Source mix: ${capabilityWorkflowSummary.sourceSummary.join(' · ')}`
                          : 'Source mix unavailable'}
                      </Typography.Text>
                    </>
                  ) : (
                    <Typography.Text type="secondary">
                      No capability workflows were exposed by the backend.
                    </Typography.Text>
                  )}
                </div>
              </div>

              <Typography.Text type="secondary">
                Overview keeps this as a summary. Use Workflows, Studio, or Primitives for full
                per-item details.
              </Typography.Text>

              <Space wrap>
                <Button onClick={() => history.push('/studio')}>Open Studio</Button>
                <Button onClick={() => history.push('/primitives')}>Open primitive browser</Button>
              </Space>
            </div>
          )}
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} style={stretchColumnStyle}>
          <ProCard title="Observability targets" {...moduleCardProps} style={fillCardStyle}>
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
                  dataIndex: 'label',
                  render: (_, record) => (
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{record.label}</Typography.Text>
                      <Tag color={record.status === 'configured' ? 'success' : 'default'}>
                        {record.status}
                      </Tag>
                    </Space>
                  ),
                },
                description: {
                  dataIndex: 'description',
                },
                subTitle: {
                  render: (_, record) =>
                    record.homeUrl ? <Tag>{record.homeUrl}</Tag> : <Tag>No URL configured</Tag>,
                },
                actions: {
                  render: (_, record) => [
                    <Button
                      key={`${record.id}-observability`}
                      type="link"
                      onClick={() =>
                        history.push(
                          `/observability?workflow=${encodeURIComponent(
                            preferences.preferredWorkflow,
                          )}`,
                        )
                      }
                    >
                      Open hub
                    </Button>,
                    <Button
                      key={`${record.id}-external`}
                      type="link"
                      disabled={record.status !== 'configured'}
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
