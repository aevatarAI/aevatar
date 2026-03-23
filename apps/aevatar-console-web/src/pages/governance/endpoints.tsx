import { PageContainer, ProTable } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@umijs/max";
import { Alert, Col, Row } from "antd";
import React, { useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import type { ServiceEndpointExposureSnapshot } from "@/shared/models/governance";
import { compactTableCardProps } from "@/shared/ui/proComponents";
import { endpointColumns } from "./components/columns";
import GovernanceQueryCard from "./components/GovernanceQueryCard";
import {
  buildGovernanceHref,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  type GovernanceDraft,
} from "./components/governanceQuery";

const initialDraft = readGovernanceDraft();

const GovernanceEndpointsPage: React.FC = () => {
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);

  const query = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft]
  );

  const servicesQuery = useQuery({
    queryKey: ["governance", "endpoints", "services", query],
    queryFn: () => servicesApi.listServices({ ...query, take: 200 }),
  });
  const endpointsQuery = useQuery({
    queryKey: ["governance", "endpoints", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () =>
      governanceApi.getEndpointCatalog(activeDraft.serviceId, query),
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
      title="Governance Endpoint Catalog"
      content="Inspect endpoint exposure modes and attached policies for a single service."
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
                buildGovernanceHref("/governance/endpoints", nextActiveDraft)
              );
            }}
            onReset={() => {
              const nextDraft = readGovernanceDraft("");
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              history.replace("/governance/endpoints");
            }}
          />
        </Col>
        <Col xs={24}>
          {activeDraft.serviceId.trim() ? (
            <ProTable<ServiceEndpointExposureSnapshot>
              columns={endpointColumns}
              dataSource={endpointsQuery.data?.endpoints ?? []}
              loading={endpointsQuery.isLoading}
              rowKey="endpointId"
              search={false}
              pagination={false}
              cardProps={compactTableCardProps}
              toolBarRender={false}
            />
          ) : (
            <Alert
              showIcon
              type="info"
              title="Select a service to inspect its endpoint exposure catalog."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceEndpointsPage;
