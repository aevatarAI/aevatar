import type {
  ProColumns,
  ProDescriptionsItemProps,
} from "@ant-design/pro-components";
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProTable,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import { Alert, Button, Col, Row, Space, Tabs, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type {
  ServiceDeploymentSnapshot,
  ServiceEndpointSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionSnapshot,
  ServiceServingTargetSnapshot,
  ServiceTrafficEndpointSnapshot,
} from "@/shared/models/services";
import {
  compactTableCardProps,
  moduleCardProps,
} from "@/shared/ui/proComponents";
import ServiceQueryCard from "./components/ServiceQueryCard";
import {
  buildServiceDetailHref,
  buildServicesHref,
  readServiceIdFromPathname,
  readServiceQueryDraft,
  trimServiceQuery,
  type ServiceQueryDraft,
} from "./components/serviceQuery";

type ServiceSummaryRecord = {
  serviceKey: string;
  displayName: string;
  endpointCount: number;
  policyCount: number;
  deploymentStatus: string;
  updatedAt: string;
};

const summaryColumns: ProDescriptionsItemProps<ServiceSummaryRecord>[] = [
  {
    title: "Service key",
    dataIndex: "serviceKey",
    render: (_, record) => (
      <Typography.Text copyable>{record.serviceKey}</Typography.Text>
    ),
  },
  {
    title: "Display name",
    dataIndex: "displayName",
  },
  {
    title: "Endpoints",
    dataIndex: "endpointCount",
    valueType: "digit",
  },
  {
    title: "Policies",
    dataIndex: "policyCount",
    valueType: "digit",
  },
  {
    title: "Deployment status",
    dataIndex: "deploymentStatus",
  },
  {
    title: "Updated",
    dataIndex: "updatedAt",
    render: (_, record) => formatDateTime(record.updatedAt),
  },
];

const initialDraft = readServiceQueryDraft();
const initialServiceId = readServiceIdFromPathname();

const ServiceDetailPage: React.FC = () => {
  const [draft, setDraft] = useState<ServiceQueryDraft>(initialDraft);
  const [query, setQuery] = useState<ServiceIdentityQuery>(
    trimServiceQuery(initialDraft)
  );
  const [serviceId, setServiceId] = useState(initialServiceId);

  useEffect(() => {
    if (serviceId.trim()) {
      history.replace(buildServiceDetailHref(serviceId, query));
    }
  }, [query, serviceId]);

  const serviceDetailQuery = useQuery({
    queryKey: ["services", "detail", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getService(serviceId, query),
  });
  const revisionsQuery = useQuery({
    queryKey: ["services", "revisions", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getRevisions(serviceId, query),
  });
  const deploymentsQuery = useQuery({
    queryKey: ["services", "deployments", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getDeployments(serviceId, query),
  });
  const servingQuery = useQuery({
    queryKey: ["services", "serving", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getServingSet(serviceId, query),
  });
  const rolloutQuery = useQuery({
    queryKey: ["services", "rollout", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getRollout(serviceId, query),
  });
  const trafficQuery = useQuery({
    queryKey: ["services", "traffic", query, serviceId],
    enabled: serviceId.trim().length > 0,
    queryFn: () => servicesApi.getTraffic(serviceId, query),
  });

  const endpointColumns = useMemo<ProColumns<ServiceEndpointSnapshot>[]>(
    () => [
      {
        title: "Endpoint",
        dataIndex: "endpointId",
      },
      {
        title: "Display name",
        dataIndex: "displayName",
      },
      {
        title: "Kind",
        dataIndex: "kind",
      },
      {
        title: "Request type",
        dataIndex: "requestTypeUrl",
      },
      {
        title: "Response type",
        dataIndex: "responseTypeUrl",
      },
    ],
    []
  );

  const revisionColumns = useMemo<ProColumns<ServiceRevisionSnapshot>[]>(
    () => [
      {
        title: "Revision",
        dataIndex: "revisionId",
      },
      {
        title: "Implementation",
        dataIndex: "implementationKind",
      },
      {
        title: "Status",
        dataIndex: "status",
      },
      {
        title: "Artifact hash",
        dataIndex: "artifactHash",
        render: (_, record) => (
          <Typography.Text copyable>{record.artifactHash}</Typography.Text>
        ),
      },
      {
        title: "Published",
        render: (_, record) => formatDateTime(record.publishedAt),
      },
    ],
    []
  );

  const deploymentColumns = useMemo<ProColumns<ServiceDeploymentSnapshot>[]>(
    () => [
      {
        title: "Deployment",
        dataIndex: "deploymentId",
      },
      {
        title: "Revision",
        dataIndex: "revisionId",
      },
      {
        title: "Status",
        dataIndex: "status",
      },
      {
        title: "Primary actor",
        dataIndex: "primaryActorId",
        render: (_, record) => (
          <Typography.Text copyable>{record.primaryActorId}</Typography.Text>
        ),
      },
      {
        title: "Activated",
        render: (_, record) => formatDateTime(record.activatedAt),
      },
    ],
    []
  );

  const servingColumns = useMemo<ProColumns<ServiceServingTargetSnapshot>[]>(
    () => [
      {
        title: "Deployment",
        dataIndex: "deploymentId",
      },
      {
        title: "Revision",
        dataIndex: "revisionId",
      },
      {
        title: "Weight",
        dataIndex: "allocationWeight",
      },
      {
        title: "State",
        dataIndex: "servingState",
      },
      {
        title: "Enabled endpoints",
        render: (_, record) => record.enabledEndpointIds.join(", ") || "all",
      },
    ],
    []
  );

  const trafficColumns = useMemo<ProColumns<ServiceTrafficEndpointSnapshot>[]>(
    () => [
      {
        title: "Endpoint",
        dataIndex: "endpointId",
      },
      {
        title: "Targets",
        render: (_, record) =>
          record.targets
            .map(
              (item) =>
                `${item.revisionId}:${item.allocationWeight}% (${item.servingState})`
            )
            .join(" | "),
      },
    ],
    []
  );

  const summaryRecord = useMemo<ServiceSummaryRecord | undefined>(() => {
    if (!serviceDetailQuery.data) {
      return undefined;
    }

    return {
      serviceKey: serviceDetailQuery.data.serviceKey,
      displayName: serviceDetailQuery.data.displayName,
      endpointCount: serviceDetailQuery.data.endpoints.length,
      policyCount: serviceDetailQuery.data.policyIds.length,
      deploymentStatus: serviceDetailQuery.data.deploymentStatus,
      updatedAt: serviceDetailQuery.data.updatedAt,
    };
  }, [serviceDetailQuery.data]);

  return (
    <PageContainer
      title={serviceId ? `Service ${serviceId}` : "Service detail"}
      content="Inspect the service lifecycle view exposed by GAgentService: detail, revisions, deployments, serving, rollout, and traffic."
      extra={[
        <Button
          key="back"
          onClick={() => history.push(buildServicesHref(query))}
        >
          Back to catalog
        </Button>,
        <Button
          key="governance"
          type="primary"
          onClick={() =>
            history.push(
              `/governance/bindings?tenantId=${encodeURIComponent(
                query.tenantId ?? ""
              )}&appId=${encodeURIComponent(
                query.appId ?? ""
              )}&namespace=${encodeURIComponent(
                query.namespace ?? ""
              )}&serviceId=${encodeURIComponent(serviceId)}`
            )
          }
        >
          Open governance
        </Button>,
      ]}
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <ServiceQueryCard
            draft={draft}
            onChange={setDraft}
            loadLabel="Reload service"
            onLoad={() => {
              const nextQuery = trimServiceQuery(draft);
              setQuery(nextQuery);
              setServiceId(readServiceIdFromPathname());
            }}
          />
        </Col>

        <Col xs={24}>
          {!serviceId ? (
            <Alert
              showIcon
              type="warning"
              title="Missing serviceId"
              description="Open this page from the Services catalog so the route can supply a concrete service identifier."
            />
          ) : summaryRecord ? (
            <Space direction="vertical" size={16} style={{ width: "100%" }}>
              <ProCard {...moduleCardProps} title="Summary">
                <ProDescriptions<ServiceSummaryRecord>
                  column={2}
                  dataSource={summaryRecord}
                  columns={summaryColumns}
                />
              </ProCard>

              <Tabs
                items={[
                  {
                    key: "endpoints",
                    label: `Endpoints (${
                      serviceDetailQuery.data?.endpoints.length ?? 0
                    })`,
                    children: (
                      <ProTable<ServiceEndpointSnapshot>
                        columns={endpointColumns}
                        dataSource={serviceDetailQuery.data?.endpoints ?? []}
                        rowKey="endpointId"
                        search={false}
                        pagination={false}
                        cardProps={compactTableCardProps}
                        toolBarRender={false}
                      />
                    ),
                  },
                  {
                    key: "revisions",
                    label: `Revisions (${
                      revisionsQuery.data?.revisions.length ?? 0
                    })`,
                    children: (
                      <ProTable<ServiceRevisionSnapshot>
                        columns={revisionColumns}
                        dataSource={revisionsQuery.data?.revisions ?? []}
                        rowKey="revisionId"
                        search={false}
                        pagination={false}
                        cardProps={compactTableCardProps}
                        toolBarRender={false}
                      />
                    ),
                  },
                  {
                    key: "deployments",
                    label: `Deployments (${
                      deploymentsQuery.data?.deployments.length ?? 0
                    })`,
                    children: (
                      <ProTable<ServiceDeploymentSnapshot>
                        columns={deploymentColumns}
                        dataSource={deploymentsQuery.data?.deployments ?? []}
                        rowKey="deploymentId"
                        search={false}
                        pagination={false}
                        cardProps={compactTableCardProps}
                        toolBarRender={false}
                      />
                    ),
                  },
                  {
                    key: "serving",
                    label: `Serving (${
                      servingQuery.data?.targets.length ?? 0
                    })`,
                    children: (
                      <ProTable<ServiceServingTargetSnapshot>
                        columns={servingColumns}
                        dataSource={servingQuery.data?.targets ?? []}
                        rowKey="deploymentId"
                        search={false}
                        pagination={false}
                        cardProps={compactTableCardProps}
                        toolBarRender={false}
                      />
                    ),
                  },
                  {
                    key: "rollout",
                    label: "Rollout",
                    children: rolloutQuery.data ? (
                      <Space
                        direction="vertical"
                        size={12}
                        style={{ width: "100%" }}
                      >
                        <Alert
                          showIcon
                          type={
                            rolloutQuery.data.status
                              .toLowerCase()
                              .includes("fail")
                              ? "error"
                              : "info"
                          }
                          title={`${
                            rolloutQuery.data.displayName ||
                            rolloutQuery.data.rolloutId
                          } · stage ${rolloutQuery.data.currentStageIndex}`}
                          description={
                            rolloutQuery.data.failureReason ||
                            `Started ${formatDateTime(
                              rolloutQuery.data.startedAt
                            )}`
                          }
                        />
                        <ProTable<ServiceServingTargetSnapshot>
                          columns={servingColumns}
                          dataSource={rolloutQuery.data.stages.flatMap(
                            (stage) => stage.targets
                          )}
                          rowKey={(record) =>
                            `${record.deploymentId}-${record.revisionId}`
                          }
                          search={false}
                          pagination={false}
                          cardProps={compactTableCardProps}
                          toolBarRender={false}
                        />
                      </Space>
                    ) : (
                      <Alert showIcon type="info" title="No active rollout." />
                    ),
                  },
                  {
                    key: "traffic",
                    label: `Traffic (${
                      trafficQuery.data?.endpoints.length ?? 0
                    })`,
                    children: (
                      <ProTable<ServiceTrafficEndpointSnapshot>
                        columns={trafficColumns}
                        dataSource={trafficQuery.data?.endpoints ?? []}
                        rowKey="endpointId"
                        search={false}
                        pagination={false}
                        cardProps={compactTableCardProps}
                        toolBarRender={false}
                      />
                    ),
                  },
                ]}
              />
            </Space>
          ) : (
            <Alert
              showIcon
              type="info"
              title="Service detail is unavailable."
              description="Load a valid tenant/app/namespace context or return to the catalog to pick a concrete service."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default ServiceDetailPage;
