import type {
  ProColumns,
} from '@ant-design/pro-components';
import {
  PageContainer,
  ProCard,
  ProTable,
} from '@ant-design/pro-components';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import { studioApi } from '@/shared/studio/api';
import { buildStudioRoute } from '@/shared/studio/navigation';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { scopesApi } from '@/shared/api/scopesApi';
import type {
  ScopeScriptSummary,
  ScopeWorkflowSummary,
} from '@/shared/models/scopes';
import type {
  StudioScopeBindingRevision,
  StudioScopeBindingStatus,
} from '@/shared/studio/models';
import { formatStudioScopeBindingImplementationKind } from '@/shared/studio/models';
import {
  cardStackStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import { Alert, Button, Col, Menu, Row, Space, Tag, Typography } from 'antd';
import React, { useEffect, useMemo, useState } from 'react';
import ScopeQueryCard from './components/ScopeQueryCard';
import { resolveStudioScopeContext } from './components/resolvedScope';
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from './components/scopeQuery';

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {typeof value === 'string' || typeof value === 'number' ? (
      <Typography.Text>{value}</Typography.Text>
    ) : (
      value
    )}
  </div>
);

function readInitialRevisionId(): string {
  if (typeof window === 'undefined') {
    return '';
  }

  return new URLSearchParams(window.location.search).get('revisionId')?.trim() ?? '';
}

function findActiveRevision(
  binding: StudioScopeBindingStatus | undefined,
): StudioScopeBindingRevision | null {
  if (!binding?.available) {
    return null;
  }

  return (
    binding.revisions.find((item) => item.isActiveServing) ??
    binding.revisions.find((item) => item.isDefaultServing) ??
    binding.revisions[0] ??
    null
  );
}

function describeRevisionState(
  revision: StudioScopeBindingRevision | null | undefined,
): string {
  return revision?.servingState || revision?.status || 'n/a';
}

const initialDraft = readScopeQueryDraft();
const initialRevisionId = readInitialRevisionId();

const ScopeOverviewPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedRevisionId, setSelectedRevisionId] = useState(initialRevisionId);
  const [activatingRevisionId, setActivatingRevisionId] = useState('');

  const authSessionQuery = useQuery({
    queryKey: ['scopes', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    history.replace(
      buildScopeHref('/scopes/overview', activeDraft, {
        revisionId: selectedRevisionId,
      }),
    );
  }, [activeDraft, selectedRevisionId]);

  useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);

  const bindingQuery = useQuery({
    queryKey: ['scopes', 'binding', activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => studioApi.getScopeBinding(activeDraft.scopeId),
  });
  const workflowsQuery = useQuery({
    queryKey: ['scopes', 'workflows', activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listWorkflows(activeDraft.scopeId),
  });
  const scriptsQuery = useQuery({
    queryKey: ['scopes', 'scripts', activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listScripts(activeDraft.scopeId),
  });

  const binding = bindingQuery.data;
  const activeRevision = useMemo(
    () => findActiveRevision(binding),
    [binding],
  );
  const projectViewItems = useMemo(
    () => [
      {
        key: 'workflows',
        label: 'Workflows',
        href: buildScopeHref('/scopes/workflows', activeDraft),
      },
      {
        key: 'scripts',
        label: 'Scripts',
        href: buildScopeHref('/scopes/scripts', activeDraft),
      },
      {
        key: 'invoke',
        label: 'Invoke',
        href: buildScopeHref('/scopes/invoke', activeDraft, {
          serviceId: binding?.serviceId ?? '',
        }),
      },
      {
        key: 'runs',
        label: 'Runs',
        href: buildRuntimeRunsHref({
          scopeId: activeDraft.scopeId,
        }),
      },
    ],
    [activeDraft, binding?.serviceId],
  );

  useEffect(() => {
    if (!binding?.available) {
      setSelectedRevisionId('');
      return;
    }

    if (
      selectedRevisionId &&
      binding.revisions.some((item) => item.revisionId === selectedRevisionId)
    ) {
      return;
    }

    setSelectedRevisionId(activeRevision?.revisionId ?? '');
  }, [activeRevision?.revisionId, binding, selectedRevisionId]);

  const selectedRevision = useMemo(() => {
    if (!binding?.available) {
      return null;
    }

    return (
      binding.revisions.find((item) => item.revisionId === selectedRevisionId) ??
      activeRevision
    );
  }, [activeRevision, binding, selectedRevisionId]);

  const workflowColumns = useMemo<ProColumns<ScopeWorkflowSummary>[]>(
    () => [
      {
        title: 'Workflow',
        dataIndex: 'workflowId',
        render: (_, record) => (
          <Space direction="vertical" size={0}>
            <Typography.Text strong>
              {record.displayName || record.workflowId}
            </Typography.Text>
            <Typography.Text type="secondary">
              {record.workflowName}
            </Typography.Text>
          </Space>
        ),
      },
      {
        title: 'Revision',
        dataIndex: 'activeRevisionId',
      },
      {
        title: 'Deployment',
        render: (_, record) =>
          `${record.deploymentStatus || 'unknown'}${
            record.deploymentId ? ` · ${record.deploymentId}` : ''
          }`,
      },
      {
        title: 'Action',
        valueType: 'option',
        render: (_, record) => [
          <Button
            key={`workflow-${record.workflowId}`}
            type="link"
            onClick={() =>
              history.push(
                buildScopeHref('/scopes/workflows', activeDraft, {
                  workflowId: record.workflowId,
                }),
              )
            }
          >
            Inspect
          </Button>,
        ],
      },
    ],
    [activeDraft],
  );

  const scriptColumns = useMemo<ProColumns<ScopeScriptSummary>[]>(
    () => [
      {
        title: 'Script',
        dataIndex: 'scriptId',
      },
      {
        title: 'Revision',
        dataIndex: 'activeRevision',
      },
      {
        title: 'Source hash',
        dataIndex: 'activeSourceHash',
        render: (_, record) => (
          <Typography.Text copyable>{record.activeSourceHash}</Typography.Text>
        ),
      },
      {
        title: 'Action',
        valueType: 'option',
        render: (_, record) => [
          <Button
            key={`script-${record.scriptId}`}
            type="link"
            onClick={() =>
              history.push(
                buildScopeHref('/scopes/scripts', activeDraft, {
                  scriptId: record.scriptId,
                }),
              )
            }
          >
            Inspect
          </Button>,
          <Button
            key={`studio-${record.scriptId}`}
            type="link"
            onClick={() =>
              history.push(
                buildStudioRoute({
                  tab: 'scripts',
                  scriptId: record.scriptId,
                }),
              )
            }
          >
            Open In Studio
          </Button>,
        ],
      },
    ],
    [activeDraft],
  );

  const handleActivateRevision = async (revisionId: string) => {
    const scopeId = activeDraft.scopeId.trim();
    if (!scopeId) {
      return;
    }

    setActivatingRevisionId(revisionId);
    try {
      await studioApi.activateScopeBindingRevision({
        scopeId,
        revisionId,
      });
      await queryClient.invalidateQueries({
        queryKey: ['scopes', 'binding', scopeId],
      });
    } finally {
      setActivatingRevisionId('');
    }
  };

  const scopeSelected = activeDraft.scopeId.trim().length > 0;
  const workflowCount = workflowsQuery.data?.length ?? 0;
  const scriptCount = scriptsQuery.data?.length ?? 0;

  return (
    <PageContainer
      title="Scope Overview"
      content="Use the scope as the user-facing root. This page summarizes the current binding, active revisions, and the scope assets that feed the default service."
      onBack={() => history.push('/overview')}
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <ScopeQueryCard
            draft={draft}
            onChange={setDraft}
            loadLabel="Load scope overview"
            resolvedScopeId={resolvedScope?.scopeId}
            resolvedScopeSource={resolvedScope?.scopeSource}
            onUseResolvedScope={() => {
              if (!resolvedScope?.scopeId) {
                return;
              }

              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope.scopeId,
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
            }}
            onLoad={() => {
              const nextDraft = normalizeScopeDraft(draft);
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
            }}
            onReset={() => {
              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope?.scopeId ?? '',
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedRevisionId('');
            }}
          />
        </Col>

        {!scopeSelected ? (
          <Col xs={24}>
            <Alert
              showIcon
              type="info"
              title="Select a scope to inspect its current binding, workflow assets, and script assets."
            />
          </Col>
        ) : (
          <>
            <Col xs={24}>
              <ProCard {...moduleCardProps}>
                <div style={cardStackStyle}>
                  <div style={summaryMetricGridStyle}>
                    <SummaryMetric label="Scope" value={activeDraft.scopeId} />
                    <SummaryMetric
                      label="Default binding"
                      value={
                        binding?.available
                          ? binding.displayName || binding.serviceId
                          : 'Not bound'
                      }
                    />
                    <SummaryMetric
                      label="Implementation"
                      value={
                        activeRevision
                          ? formatStudioScopeBindingImplementationKind(
                              activeRevision.implementationKind,
                            )
                          : 'n/a'
                      }
                    />
                    <SummaryMetric
                      label="Workflow assets"
                      value={workflowCount}
                    />
                    <SummaryMetric label="Script assets" value={scriptCount} />
                  </div>

                  <Space wrap>
                    <Button
                      type="primary"
                      onClick={() =>
                        history.push(buildStudioRoute({ tab: 'studio' }))
                      }
                    >
                      Open Studio
                    </Button>
                  </Space>

                  <div
                    style={{
                      borderTop: '1px solid var(--ant-color-border-secondary)',
                      paddingTop: 12,
                    }}
                  >
                    <Typography.Text style={summaryFieldLabelStyle}>
                      Project views
                    </Typography.Text>
                    <Menu
                      mode="horizontal"
                      selectable={false}
                      style={{
                        background: 'transparent',
                        borderBottom: 'none',
                        marginTop: 8,
                      }}
                      items={projectViewItems.map((item) => ({
                        key: item.key,
                        label: item.label,
                      }))}
                      onClick={({ key }) => {
                        const target = projectViewItems.find(
                          (item) => item.key === key,
                        );
                        if (target) {
                          history.push(target.href);
                        }
                      }}
                    />
                  </div>
                </div>
              </ProCard>
            </Col>

            <Col xs={24} lg={12}>
              <ProCard
                {...moduleCardProps}
                title="Binding Snapshot"
                loading={bindingQuery.isLoading}
              >
                {binding?.available ? (
                  <div style={summaryFieldGridStyle}>
                    <SummaryField label="Display name" value={binding.displayName} />
                    <SummaryField label="Default service" value={binding.serviceId} />
                    <SummaryField
                      label="Active revision"
                      value={binding.activeServingRevisionId || 'n/a'}
                    />
                    <SummaryField
                      label="Default revision"
                      value={binding.defaultServingRevisionId || 'n/a'}
                    />
                    <SummaryField
                      label="Deployment"
                      value={`${binding.deploymentStatus || 'unknown'}${
                        binding.deploymentId ? ` · ${binding.deploymentId}` : ''
                      }`}
                    />
                    <SummaryField
                      label="Primary actor"
                      value={
                        binding.primaryActorId ? (
                          <Typography.Text copyable>
                            {binding.primaryActorId}
                          </Typography.Text>
                        ) : (
                          'n/a'
                        )
                      }
                    />
                    <SummaryField
                      label="Updated"
                      value={formatDateTime(binding.updatedAt)}
                    />
                    <SummaryField
                      label="Revision state"
                      value={describeRevisionState(selectedRevision)}
                    />
                  </div>
                ) : (
                  <Alert
                    showIcon
                    type="info"
                    title="No default scope binding is active yet. Use Studio to bind a workflow, script, or GAgent to this scope."
                  />
                )}
              </ProCard>
            </Col>

            <Col xs={24} lg={12}>
              <ProCard
                {...moduleCardProps}
                title="Revision Rollout"
                loading={bindingQuery.isLoading}
              >
                {binding?.available ? (
                  <div style={cardStackStyle}>
                    {(binding.revisions.length > 0
                      ? binding.revisions
                      : [activeRevision].filter(Boolean)
                    ).map((revision) =>
                      revision ? (
                        <div
                          key={revision.revisionId}
                          style={{
                            border: '1px solid #E5E7EB',
                            borderRadius: 12,
                            padding: 12,
                          }}
                        >
                          <div
                            style={{
                              alignItems: 'center',
                              display: 'flex',
                              justifyContent: 'space-between',
                              gap: 12,
                            }}
                          >
                            <Space wrap size={[8, 8]}>
                              <Typography.Text strong>
                                {revision.revisionId}
                              </Typography.Text>
                              <Tag color={revision.isActiveServing ? 'success' : 'default'}>
                                {revision.isActiveServing ? 'active' : 'inactive'}
                              </Tag>
                              {revision.isDefaultServing ? <Tag>default</Tag> : null}
                              <Tag>
                                {formatStudioScopeBindingImplementationKind(
                                  revision.implementationKind,
                                )}
                              </Tag>
                            </Space>
                            <Button
                              size="small"
                              disabled={revision.isActiveServing}
                              loading={activatingRevisionId === revision.revisionId}
                              onClick={() => void handleActivateRevision(revision.revisionId)}
                            >
                              Activate {revision.revisionId}
                            </Button>
                          </div>
                          <div style={{ marginTop: 8 }}>
                            <Typography.Text type="secondary">
                              {revision.servingState || revision.status || 'Unknown'} ·{' '}
                              {revision.primaryActorId || revision.deploymentId || 'No actor yet'}
                            </Typography.Text>
                          </div>
                        </div>
                      ) : null,
                    )}
                  </div>
                ) : (
                  <Alert
                    showIcon
                    type="info"
                    title="Binding revisions will appear here after the first scope bind."
                  />
                )}
              </ProCard>
            </Col>

            <Col xs={24} lg={12}>
              <ProTable<ScopeWorkflowSummary>
                columns={workflowColumns}
                dataSource={workflowsQuery.data ?? []}
                loading={workflowsQuery.isLoading}
                rowKey="workflowId"
                search={false}
                pagination={false}
                cardProps={compactTableCardProps}
                toolBarRender={false}
                headerTitle="Workflow Assets"
              />
            </Col>

            <Col xs={24} lg={12}>
              <ProTable<ScopeScriptSummary>
                columns={scriptColumns}
                dataSource={scriptsQuery.data ?? []}
                loading={scriptsQuery.isLoading}
                rowKey="scriptId"
                search={false}
                pagination={false}
                cardProps={compactTableCardProps}
                toolBarRender={false}
                headerTitle="Script Assets"
              />
            </Col>

            <Col xs={24}>
              <ProCard
                {...moduleCardProps}
                title="Operator Views"
              >
                <div style={embeddedPanelStyle}>
                  <Typography.Text type="secondary">
                    Platform Services and Platform Governance remain available for raw
                    tenant/app/namespace operations. Keep user flows on Scopes unless
                    you need those platform internals.
                  </Typography.Text>
                </div>
              </ProCard>
            </Col>
          </>
        )}
      </Row>
    </PageContainer>
  );
};

export default ScopeOverviewPage;
