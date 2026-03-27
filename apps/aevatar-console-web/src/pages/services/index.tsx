import type {
  ProColumns,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProTable,
} from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import { Button, Col, Row, Space, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { servicesApi } from '@/shared/api/servicesApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import type {
  ServiceCatalogSnapshot,
  ServiceIdentityQuery,
} from '@/shared/models/services';
import {
  cardStackStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
  stretchColumnStyle,
  summaryFieldLabelStyle,
} from '@/shared/ui/proComponents';
import ServiceQueryCard from './components/ServiceQueryCard';
import {
  buildServiceDetailHref,
  buildServicesHref,
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from './components/serviceQuery';

type ServiceCatalogSummaryRecord = {
  services: number;
  activeDeployments: number;
  endpoints: number;
  policies: number;
};

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const initialDraft = readServiceQueryDraft();

const ServicesPage: React.FC = () => {
  const [draft, setDraft] = useState<ServiceQueryDraft>(initialDraft);
  const [query, setQuery] = useState<ServiceIdentityQuery>(
    trimServiceQuery(initialDraft),
  );

  const servicesQuery = useQuery({
    queryKey: ['services', query],
    queryFn: () => servicesApi.listServices(query),
  });

  useEffect(() => {
    history.replace(buildServicesHref(query));
  }, [query]);

  const serviceColumns = useMemo<ProColumns<ServiceCatalogSnapshot>[]>(
    () => [
      {
        title: 'Service',
        dataIndex: 'serviceId',
        render: (_, record) => (
          <Space direction="vertical" size={0}>
            <Typography.Text strong>
              {record.displayName || record.serviceId}
            </Typography.Text>
            <Typography.Text type="secondary">
              {record.serviceId}
            </Typography.Text>
          </Space>
        ),
      },
      {
        title: 'Namespace',
        dataIndex: 'namespace',
      },
      {
        title: 'Endpoints',
        render: (_, record) => record.endpoints.length,
      },
      {
        title: 'Policies',
        render: (_, record) => record.policyIds.length,
      },
      {
        title: 'Serving revision',
        render: (_, record) =>
          record.activeServingRevisionId ||
          record.defaultServingRevisionId ||
          'n/a',
      },
      {
        title: 'Deployment',
        render: (_, record) =>
          `${record.deploymentStatus || 'unknown'}${
            record.deploymentId ? ` · ${record.deploymentId}` : ''
          }`,
      },
      {
        title: 'Updated',
        dataIndex: 'updatedAt',
        render: (_, record) => formatDateTime(record.updatedAt),
      },
      {
        title: 'Action',
        valueType: 'option',
        render: (_, record) => [
          <Button
            key={`${record.serviceKey}-detail`}
            type="link"
            onClick={() =>
              history.push(buildServiceDetailHref(record.serviceId, query))
            }
          >
            Inspect
          </Button>,
          <Button
            key={`${record.serviceKey}-governance`}
            type="link"
            onClick={() =>
              history.push(
                `/governance/bindings?tenantId=${encodeURIComponent(
                  record.tenantId,
                )}&appId=${encodeURIComponent(
                  record.appId,
                )}&namespace=${encodeURIComponent(
                  record.namespace,
                )}&serviceId=${encodeURIComponent(record.serviceId)}`,
              )
            }
          >
            Platform governance
          </Button>,
        ],
      },
    ],
    [query],
  );

  const summaryRecord = useMemo<ServiceCatalogSummaryRecord>(
    () => ({
      services: servicesQuery.data?.length ?? 0,
      activeDeployments: (servicesQuery.data ?? []).filter(
        (item) => item.deploymentId.trim().length > 0,
      ).length,
      endpoints: (servicesQuery.data ?? []).reduce(
        (count, item) => count + item.endpoints.length,
        0,
      ),
      policies: (servicesQuery.data ?? []).reduce(
        (count, item) => count + item.policyIds.length,
        0,
      ),
    }),
    [servicesQuery.data],
  );

  return (
    <PageContainer
      title="Platform Services"
      content="Browse the raw platform service catalog keyed by tenantId, appId, and namespace. End-user workflow and script flows should stay on Scopes."
    >
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24}>
          <ServiceQueryCard
            draft={draft}
            onChange={setDraft}
            onLoad={() => setQuery(trimServiceQuery(draft))}
            onReset={() => {
              const nextDraft = readServiceQueryDraft('');
              setDraft(nextDraft);
              setQuery(trimServiceQuery(nextDraft));
            }}
          />
        </Col>

        <Col xs={24}>
          <div
            style={{
              ...embeddedPanelStyle,
              background: 'var(--ant-color-fill-quaternary)',
            }}
          >
            <Typography.Text strong>Scope-first frontend</Typography.Text>
            <Typography.Paragraph
              style={{ margin: '8px 0 0' }}
              type="secondary"
            >
              Use Scopes for normal user-facing workflow assets. This page
              exposes raw GAgentService service identities for platform
              diagnostics and operator workflows.
            </Typography.Paragraph>
          </div>
        </Col>

        <Col xs={24} xl={10} style={stretchColumnStyle}>
          <ProCard
            {...moduleCardProps}
            style={fillCardStyle}
            title="Catalog digest"
          >
            <div style={summaryMetricGridStyle}>
              <SummaryMetric label="Services" value={summaryRecord.services} />
              <SummaryMetric
                label="Active deployments"
                value={summaryRecord.activeDeployments}
              />
              <SummaryMetric
                label="Endpoints"
                value={summaryRecord.endpoints}
              />
              <SummaryMetric label="Policies" value={summaryRecord.policies} />
            </div>
          </ProCard>
        </Col>
        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard
            {...moduleCardProps}
            style={fillCardStyle}
            title="Related views"
          >
            <div style={cardStackStyle}>
              <Typography.Paragraph style={{ marginBottom: 0 }} type="secondary">
                Jump to the scope-first catalog, platform governance, or the
                runtime console without leaving the current operator context.
              </Typography.Paragraph>
              <Space wrap>
                <Button onClick={() => history.push('/scopes')}>
                  Open scopes
                </Button>
                <Button onClick={() => history.push('/governance')}>
                  Open platform governance
                </Button>
                <Button onClick={() => history.push(buildRuntimeRunsHref())}>
                  Open Runtime Runs
                </Button>
              </Space>
            </div>
          </ProCard>
        </Col>

        <Col xs={24}>
          <ProTable<ServiceCatalogSnapshot>
            columns={serviceColumns}
            dataSource={servicesQuery.data ?? []}
            loading={servicesQuery.isLoading}
            rowKey="serviceKey"
            search={false}
            pagination={{ pageSize: 10 }}
            cardProps={compactTableCardProps}
            toolBarRender={false}
          />
        </Col>
      </Row>
    </PageContainer>
  );
};

export default ServicesPage;
