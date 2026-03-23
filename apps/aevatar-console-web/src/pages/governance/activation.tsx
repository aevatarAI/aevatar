import { PageContainer, ProCard, ProTable } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import { Alert, Col, Row, Space } from "antd";
import React, { useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import type {
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import {
  compactTableCardProps,
  moduleCardProps,
} from "@/shared/ui/proComponents";
import {
  bindingColumns,
  endpointColumns,
  policyColumns,
} from "./components/columns";
import GovernanceQueryCard from "./components/GovernanceQueryCard";
import {
  buildGovernanceHref,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  type GovernanceDraft,
} from "./components/governanceQuery";

const initialDraft = readGovernanceDraft();

const GovernanceActivationPage: React.FC = () => {
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);

  const query = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft]
  );

  const servicesQuery = useQuery({
    queryKey: ["governance", "activation", "services", query],
    queryFn: () => servicesApi.listServices({ ...query, take: 200 }),
  });
  const activationQuery = useQuery({
    queryKey: [
      "governance",
      "activation",
      query,
      activeDraft.serviceId,
      activeDraft.revisionId,
    ],
    enabled:
      activeDraft.serviceId.trim().length > 0 &&
      activeDraft.revisionId.trim().length > 0,
    queryFn: () =>
      governanceApi.getActivationCapability(activeDraft.serviceId, {
        ...query,
        revisionId: activeDraft.revisionId,
      }),
  });

  const serviceOptions = useMemo(
    () =>
      (servicesQuery.data ?? []).map((item) => ({
        label: item.displayName
          ? `${item.displayName} (${item.serviceId})`
          : item.serviceId,
        value: item.serviceId,
      })),
    [servicesQuery.data]
  );

  const activationView = activationQuery.data;

  return (
    <PageContainer
      title="Governance Activation Capability"
      content="Inspect the revision-specific assembled activation view exposed by governance."
      onBack={() =>
        history.push(buildGovernanceHref("/governance", activeDraft))
      }
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <GovernanceQueryCard
            draft={draft}
            serviceOptions={serviceOptions}
            includeRevision
            loadLabel="Load activation capability"
            onChange={setDraft}
            onLoad={() => {
              const nextActiveDraft = normalizeGovernanceDraft(draft);
              setDraft(nextActiveDraft);
              setActiveDraft(nextActiveDraft);
              history.replace(
                buildGovernanceHref("/governance/activation", nextActiveDraft)
              );
            }}
            onReset={() => {
              const nextDraft = readGovernanceDraft("");
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              history.replace("/governance/activation");
            }}
          />
        </Col>
        <Col xs={24}>
          {!activeDraft.serviceId.trim() || !activeDraft.revisionId.trim() ? (
            <Alert
              showIcon
              type="info"
              title="Select a service and revision to assemble the activation capability view."
            />
          ) : activationView ? (
            <Space direction="vertical" size={16} style={{ width: "100%" }}>
              <Alert
                showIcon
                type={
                  activationView.missingPolicyIds.length > 0
                    ? "warning"
                    : "success"
                }
                title={`Revision ${activationView.revisionId}`}
                description={
                  activationView.missingPolicyIds.length > 0
                    ? `Missing policies: ${activationView.missingPolicyIds.join(
                        ", "
                      )}`
                    : "No missing policy references."
                }
              />
              <ProCard title="Bindings" {...moduleCardProps}>
                <ProTable<ServiceBindingSnapshot>
                  columns={bindingColumns}
                  dataSource={activationView.bindings}
                  rowKey="bindingId"
                  search={false}
                  pagination={false}
                  cardProps={compactTableCardProps}
                  toolBarRender={false}
                />
              </ProCard>
              <ProCard title="Policies" {...moduleCardProps}>
                <ProTable<ServicePolicySnapshot>
                  columns={policyColumns}
                  dataSource={activationView.policies}
                  rowKey="policyId"
                  search={false}
                  pagination={false}
                  cardProps={compactTableCardProps}
                  toolBarRender={false}
                />
              </ProCard>
              <ProCard title="Endpoints" {...moduleCardProps}>
                <ProTable<ServiceEndpointExposureSnapshot>
                  columns={endpointColumns}
                  dataSource={activationView.endpoints}
                  rowKey="endpointId"
                  search={false}
                  pagination={false}
                  cardProps={compactTableCardProps}
                  toolBarRender={false}
                />
              </ProCard>
            </Space>
          ) : (
            <Alert
              showIcon
              type="info"
              title="Activation capability view is unavailable."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceActivationPage;
