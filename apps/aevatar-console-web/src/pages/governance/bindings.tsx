import { PageContainer, ProCard, ProTable } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Col, Row } from 'antd';
import React, { useMemo, useState } from 'react';
import { governanceApi } from '@/shared/api/governanceApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { history } from '@/shared/navigation/history';
import type { ServiceBindingSnapshot } from '@/shared/models/governance';
import {
  cardStackStyle,
  compactTableCardProps,
  moduleCardProps,
} from '@/shared/ui/proComponents';
import { bindingColumns } from './components/columns';
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

const GovernanceBindingsPage: React.FC = () => {
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
    queryKey: ["governance", "bindings", "services", serviceQuery],
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
  });
  const bindingsQuery = useQuery({
    queryKey: ["governance", "bindings", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () => governanceApi.getBindings(activeDraft.serviceId, query),
  });

  const serviceOptions = useMemo(
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data]
  );

  const bindingCatalog = bindingsQuery.data;
  const bindingMetrics = useMemo<GovernanceSummaryMetric[]>(
    () => [
      {
        label: 'Bindings',
        value: String(bindingCatalog?.bindings.length ?? 0),
      },
      {
        label: 'With policies',
        value: String(
          bindingCatalog?.bindings.filter((binding) => binding.policyIds.length > 0)
            .length ?? 0,
        ),
        tone: 'success',
      },
      {
        label: 'Retired',
        value: String(
          bindingCatalog?.bindings.filter((binding) => binding.retired).length ?? 0,
        ),
        tone: 'warning',
      },
    ],
    [bindingCatalog],
  );

  return (
    <PageContainer
      title="Platform Governance Bindings"
      content="Inspect the raw governance binding view for a single platform service identity."
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
            <div style={cardStackStyle}>
              <GovernanceSummaryPanel
                title="Binding catalog"
                description="Review raw binding targets, attached policies, and retirement state for the selected platform service."
                draft={activeDraft}
                extraFields={[
                  {
                    label: 'Catalog updated',
                    value: formatGovernanceTimestamp(bindingCatalog?.updatedAt),
                  },
                ]}
                metrics={bindingMetrics}
                status={{
                  color: bindingsQuery.isLoading ? 'processing' : 'success',
                  label: bindingsQuery.isLoading ? 'Loading' : 'Loaded',
                }}
              />
              <ProCard title="Bindings" {...moduleCardProps}>
                <ProTable<ServiceBindingSnapshot>
                  columns={bindingColumns}
                  dataSource={bindingCatalog?.bindings ?? []}
                  loading={bindingsQuery.isLoading}
                  rowKey="bindingId"
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
              description="Load a service identity to inspect its raw binding catalog and attached policy targets."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceBindingsPage;
