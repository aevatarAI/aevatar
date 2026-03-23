import type {
  ProColumns,
  ProDescriptionsItemProps,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProTable,
} from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { history } from '@umijs/max';
import { Button, Col, Row, Space, Statistic, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import { servicesApi } from '@/shared/api/servicesApi';
import { formatDateTime } from '@/shared/datetime/dateTime';
import type {
  ServiceCatalogSnapshot,
  ServiceIdentityQuery,
} from '@/shared/models/services';
import {
  compactTableCardProps,
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
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

const summaryColumns: ProDescriptionsItemProps<ServiceCatalogSummaryRecord>[] =
  [
    {
      title: 'Services',
      dataIndex: 'services',
      valueType: 'digit',
    },
    {
      title: 'Active deployments',
      dataIndex: 'activeDeployments',
      valueType: 'digit',
    },
    {
      title: 'Endpoints',
      dataIndex: 'endpoints',
      valueType: 'digit',
    },
    {
      title: 'Policies',
      dataIndex: 'policies',
      valueType: 'digit',
    },
  ];

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
            Governance
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
      title="Services"
      content="Browse the service catalog, then open a dedicated detail page for revisions, deployments, serving, rollout, and traffic."
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

        <Col xs={24} md={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic title="Services" value={summaryRecord.services} />
          </ProCard>
        </Col>
        <Col xs={24} md={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic
              title="Active deployments"
              value={summaryRecord.activeDeployments}
            />
          </ProCard>
        </Col>
        <Col xs={24} md={8} style={stretchColumnStyle}>
          <ProCard {...moduleCardProps} style={fillCardStyle}>
            <Statistic title="Endpoints" value={summaryRecord.endpoints} />
          </ProCard>
        </Col>

        <Col xs={24} xl={10} style={stretchColumnStyle}>
          <ProCard
            {...moduleCardProps}
            style={fillCardStyle}
            title="Catalog summary"
          >
            <ProDescriptions<ServiceCatalogSummaryRecord>
              column={2}
              columns={summaryColumns}
              dataSource={summaryRecord}
            />
          </ProCard>
        </Col>
        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard
            {...moduleCardProps}
            style={fillCardStyle}
            title="Related views"
          >
            <Space wrap>
              <Button onClick={() => history.push('/governance')}>
                Open governance hub
              </Button>
              <Button onClick={() => history.push('/scopes')}>
                Open scopes
              </Button>
              <Button onClick={() => history.push('/runs')}>
                Open runtime runs
              </Button>
            </Space>
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
