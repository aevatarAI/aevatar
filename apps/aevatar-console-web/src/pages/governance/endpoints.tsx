import { PageContainer, ProCard, ProTable } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Col, Row } from 'antd';
import React, { useMemo, useState } from 'react';
import { governanceApi } from '@/shared/api/governanceApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { history } from '@/shared/navigation/history';
import type { ServiceEndpointExposureSnapshot } from '@/shared/models/governance';
import {
  cardStackStyle,
  compactTableCardProps,
  moduleCardProps,
} from '@/shared/ui/proComponents';
import { endpointColumns } from './components/columns';
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

const GovernanceEndpointsPage: React.FC = () => {
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
    queryKey: ["governance", "endpoints", "services", serviceQuery],
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
  });
  const endpointsQuery = useQuery({
    queryKey: ["governance", "endpoints", query, activeDraft.serviceId],
    enabled: activeDraft.serviceId.trim().length > 0,
    queryFn: () =>
      governanceApi.getEndpointCatalog(activeDraft.serviceId, query),
  });

  const serviceOptions = useMemo(
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data]
  );

  const endpointCatalog = endpointsQuery.data;
  const endpointMetrics = useMemo<GovernanceSummaryMetric[]>(
    () => [
      {
        label: 'Endpoints',
        value: String(endpointCatalog?.endpoints.length ?? 0),
      },
      {
        label: 'Public',
        value: String(
          endpointCatalog?.endpoints.filter(
            (endpoint) => endpoint.exposureKind === 'public',
          ).length ?? 0,
        ),
        tone: 'success',
      },
      {
        label: 'Disabled',
        value: String(
          endpointCatalog?.endpoints.filter(
            (endpoint) => endpoint.exposureKind === 'disabled',
          ).length ?? 0,
        ),
        tone: 'warning',
      },
    ],
    [endpointCatalog],
  );

  return (
    <PageContainer
      title="Platform Governance Endpoint Catalog"
      content="Inspect raw endpoint exposure modes and attached policies for one platform service identity."
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
            <div style={cardStackStyle}>
              <GovernanceSummaryPanel
                title="Endpoint catalog"
                description="Review raw endpoint exposure modes, request contracts, and attached policy references."
                draft={activeDraft}
                extraFields={[
                  {
                    label: 'Catalog updated',
                    value: formatGovernanceTimestamp(endpointCatalog?.updatedAt),
                  },
                ]}
                metrics={endpointMetrics}
                status={{
                  color: endpointsQuery.isLoading ? 'processing' : 'success',
                  label: endpointsQuery.isLoading ? 'Loading' : 'Loaded',
                }}
              />
              <ProCard title="Endpoints" {...moduleCardProps}>
                <ProTable<ServiceEndpointExposureSnapshot>
                  columns={endpointColumns}
                  dataSource={endpointCatalog?.endpoints ?? []}
                  loading={endpointsQuery.isLoading}
                  rowKey="endpointId"
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
              description="Load a service identity to inspect raw endpoint exposure modes and attached policy references."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceEndpointsPage;
