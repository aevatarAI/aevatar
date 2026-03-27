import { PageContainer, ProCard, ProTable } from '@ant-design/pro-components';
import { useQuery } from '@tanstack/react-query';
import { Col, Row } from 'antd';
import React, { useMemo, useState } from 'react';
import { governanceApi } from '@/shared/api/governanceApi';
import { servicesApi } from '@/shared/api/servicesApi';
import { history } from '@/shared/navigation/history';
import type {
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicySnapshot,
} from '@/shared/models/governance';
import {
  cardStackStyle,
  compactTableCardProps,
  moduleCardProps,
} from '@/shared/ui/proComponents';
import {
  bindingColumns,
  endpointColumns,
  policyColumns,
} from './components/columns';
import GovernanceQueryCard from './components/GovernanceQueryCard';
import {
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

const GovernanceActivationPage: React.FC = () => {
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
    queryKey: ["governance", "activation", "services", serviceQuery],
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
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
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data]
  );

  const activationView = activationQuery.data;
  const activationMetrics = useMemo<GovernanceSummaryMetric[]>(
    () => [
      {
        label: 'Bindings',
        value: String(activationView?.bindings.length ?? 0),
      },
      {
        label: 'Policies',
        value: String(activationView?.policies.length ?? 0),
      },
      {
        label: 'Endpoints',
        value: String(activationView?.endpoints.length ?? 0),
      },
      {
        label: 'Missing policies',
        value: String(activationView?.missingPolicyIds.length ?? 0),
        tone:
          (activationView?.missingPolicyIds.length ?? 0) > 0
            ? 'warning'
            : 'success',
      },
    ],
    [activationView],
  );

  const activationSummaryDescription = activationQuery.isLoading
    ? 'Assembling the revision-specific activation capability view for the selected platform service.'
    : activationView && activationView.missingPolicyIds.length > 0
      ? `Missing policies: ${activationView.missingPolicyIds.join(', ')}`
      : 'No missing policy references were found for this revision.';

  return (
    <PageContainer
      title="Platform Governance Activation Capability"
      content="Inspect the revision-specific raw activation view assembled for one platform service identity."
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
            <GovernanceSelectionNotice
              title="Select a platform service and revision"
              description="Load a service identity and revision to assemble the raw activation capability view."
            />
          ) : activationQuery.isLoading || activationView ? (
            <div style={cardStackStyle}>
              <GovernanceSummaryPanel
                title="Activation capability"
                description={activationSummaryDescription}
                draft={activeDraft}
                revisionId={activeDraft.revisionId}
                metrics={activationMetrics}
                status={{
                  color: activationQuery.isLoading
                    ? 'processing'
                    : (activationView?.missingPolicyIds.length ?? 0) > 0
                      ? 'warning'
                      : 'success',
                  label: activationQuery.isLoading
                    ? 'Loading'
                    : (activationView?.missingPolicyIds.length ?? 0) > 0
                      ? 'Missing policies'
                      : 'Ready',
                }}
              />
              <ProCard title="Bindings" {...moduleCardProps}>
                <ProTable<ServiceBindingSnapshot>
                  columns={bindingColumns}
                  dataSource={activationView?.bindings ?? []}
                  loading={activationQuery.isLoading}
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
                  dataSource={activationView?.policies ?? []}
                  loading={activationQuery.isLoading}
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
                  dataSource={activationView?.endpoints ?? []}
                  loading={activationQuery.isLoading}
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
              title="Raw activation capability view is unavailable"
              description="The selected service revision did not return a composed activation snapshot."
            />
          )}
        </Col>
      </Row>
    </PageContainer>
  );
};

export default GovernanceActivationPage;
