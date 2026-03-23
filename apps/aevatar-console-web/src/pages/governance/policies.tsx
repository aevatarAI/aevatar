import { PageContainer, ProTable } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import { Alert, Col, Row } from "antd";
import React, { useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import type { ServicePolicySnapshot } from "@/shared/models/governance";
import { compactTableCardProps } from "@/shared/ui/proComponents";
import { policyColumns } from "./components/columns";
import GovernanceQueryCard from "./components/GovernanceQueryCard";
import {
  buildGovernanceHref,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  type GovernanceDraft,
} from "./components/governanceQuery";

const initialDraft = readGovernanceDraft();

const GovernancePoliciesPage: React.FC = () => {
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);

  const query = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft]
  );

  const servicesQuery = useQuery({
    queryKey: ["governance", "policies", "services", query],
    queryFn: () => servicesApi.listServices({ ...query, take: 200 }),
  });
  const policiesQuery = useQuery({
    queryKey: ["governance", "policies", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () => governanceApi.getPolicies(activeDraft.serviceId, query),
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

  return (
    <PageContainer
      title="Governance Policies"
      content="Inspect caller policy, activation requirements, and deployment requirements for a single service."
      onBack={() =>
        history.push(buildGovernanceHref("/governance", activeDraft))
      }
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <GovernanceQueryCard
            draft={draft}
            serviceOptions={serviceOptions}
            onChange={setDraft}
            onLoad={() => {
              const nextActiveDraft = normalizeGovernanceDraft(draft);
              setDraft(nextActiveDraft);
              setActiveDraft(nextActiveDraft);
              history.replace(
                buildGovernanceHref("/governance/policies", nextActiveDraft)
              );
            }}
            onReset={() => {
              const nextDraft = readGovernanceDraft("");
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              history.replace("/governance/policies");
            }}
          />
        </Col>
        <Col xs={24}>
          {activeDraft.serviceId.trim() ? (
            <ProTable<ServicePolicySnapshot>
              columns={policyColumns}
              dataSource={policiesQuery.data?.policies ?? []}
              loading={policiesQuery.isLoading}
              rowKey="policyId"
              search={false}
              pagination={false}
              cardProps={compactTableCardProps}
              toolBarRender={false}
            />
          ) : (
            <Alert
              showIcon
              type="info"
              title="Select a service to inspect its policy catalog."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernancePoliciesPage;
