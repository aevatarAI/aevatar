import { PageContainer, ProTable } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@umijs/max";
import { Alert, Col, Row } from "antd";
import React, { useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import type { ServiceBindingSnapshot } from "@/shared/models/governance";
import { compactTableCardProps } from "@/shared/ui/proComponents";
import { bindingColumns } from "./components/columns";
import GovernanceQueryCard from "./components/GovernanceQueryCard";
import {
  buildGovernanceHref,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  type GovernanceDraft,
} from "./components/governanceQuery";

const initialDraft = readGovernanceDraft();

const GovernanceBindingsPage: React.FC = () => {
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);

  const query = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft]
  );

  const servicesQuery = useQuery({
    queryKey: ["governance", "bindings", "services", query],
    queryFn: () => servicesApi.listServices({ ...query, take: 200 }),
  });
  const bindingsQuery = useQuery({
    queryKey: ["governance", "bindings", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () => governanceApi.getBindings(activeDraft.serviceId, query),
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
      title="Governance Bindings"
      content="Inspect bound services, connectors, and secrets for a single service."
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
                buildGovernanceHref("/governance/bindings", nextActiveDraft)
              );
            }}
            onReset={() => {
              const nextDraft = readGovernanceDraft("");
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              history.replace("/governance/bindings");
            }}
          />
        </Col>
        <Col xs={24}>
          {activeDraft.serviceId.trim() ? (
            <ProTable<ServiceBindingSnapshot>
              columns={bindingColumns}
              dataSource={bindingsQuery.data?.bindings ?? []}
              loading={bindingsQuery.isLoading}
              rowKey="bindingId"
              search={false}
              pagination={false}
              cardProps={compactTableCardProps}
              toolBarRender={false}
            />
          ) : (
            <Alert
              showIcon
              type="info"
              title="Select a service to inspect its binding catalog."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceBindingsPage;
