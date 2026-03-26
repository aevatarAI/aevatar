import { PageContainer, ProCard, ProTable } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Col, Row } from 'antd';
import React, { useMemo, useState } from 'react';
import { governanceApi } from '@/shared/api/governanceApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { history } from '@/shared/navigation/history';
import type { ServicePolicySnapshot } from '@/shared/models/governance';
import {
  cardStackStyle,
  compactTableCardProps,
  moduleCardProps,
} from '@/shared/ui/proComponents';
import { policyColumns } from './components/columns';
import GovernanceQueryCard from './components/GovernanceQueryCard';
import {
  formatGovernanceTimestamp,
  GovernanceSelectionNotice,
  GovernanceSummaryPanel,
  type GovernanceSummaryMetric,
} from './components/GovernanceResultPanels';
import {
  buildGovernanceServiceOptions,
  buildGovernanceHref,
  hasGovernanceScope,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  type GovernanceDraft,
} from './components/governanceQuery';

const initialDraft = readGovernanceDraft();

const GovernancePoliciesPage: React.FC = () => {
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);
  const serviceQuery = useMemo(() => normalizeGovernanceQuery(draft), [draft]);
  const serviceSearchEnabled = useMemo(
    () => hasGovernanceScope(draft),
    [draft]
  );

  const query = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft]
  );

  const servicesQuery = useQuery({
    queryKey: ["governance", "policies", "services", serviceQuery],
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
  });
  const policiesQuery = useQuery({
    queryKey: ["governance", "policies", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () => governanceApi.getPolicies(activeDraft.serviceId, query),
  });

  const serviceOptions = useMemo(
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data]
  );

  const policyCatalog = policiesQuery.data;
  const policyMetrics = useMemo<GovernanceSummaryMetric[]>(
    () => [
      {
        label: 'Policies',
        value: String(policyCatalog?.policies.length ?? 0),
      },
      {
        label: 'Activation checks',
        value: String(
          policyCatalog?.policies.filter(
            (policy) => policy.activationRequiredBindingIds.length > 0,
          ).length ?? 0,
        ),
        tone: 'warning',
      },
      {
        label: 'Caller restrictions',
        value: String(
          policyCatalog?.policies.filter(
            (policy) => policy.invokeAllowedCallerServiceKeys.length > 0,
          ).length ?? 0,
        ),
        tone: 'success',
      },
    ],
    [policyCatalog],
  );

  return (
    <PageContainer
      title="Platform Governance Policies"
      content="Inspect raw caller policy, activation requirements, and deployment requirements for one platform service identity."
      onBack={() =>
        history.push(buildGovernanceHref("/governance", activeDraft))
      }
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <GovernanceQueryCard
            draft={draft}
            serviceOptions={serviceOptions}
            serviceSearchEnabled={serviceSearchEnabled}
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
            <div style={cardStackStyle}>
              <GovernanceSummaryPanel
                title="Policy catalog"
                description="Review activation requirements, caller allowlists, and deployment gates for the selected platform service."
                draft={activeDraft}
                extraFields={[
                  {
                    label: 'Catalog updated',
                    value: formatGovernanceTimestamp(policyCatalog?.updatedAt),
                  },
                ]}
                metrics={policyMetrics}
                status={{
                  color: policiesQuery.isLoading ? 'processing' : 'success',
                  label: policiesQuery.isLoading ? 'Loading' : 'Loaded',
                }}
              />
              <ProCard title="Policies" {...moduleCardProps}>
                <ProTable<ServicePolicySnapshot>
                  columns={policyColumns}
                  dataSource={policyCatalog?.policies ?? []}
                  loading={policiesQuery.isLoading}
                  rowKey="policyId"
                  search={false}
                  pagination={false}
                  cardProps={compactTableCardProps}
                  toolBarRender={false}
                />
              </ProCard>
            </div>
          ) : (
            <GovernanceSelectionNotice
              title="Select a platform service"
              description="Load a service identity to inspect its raw policy catalog and activation requirements."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernancePoliciesPage;
