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
import { Alert, Button, Col, Drawer, Row, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { studioApi } from "@/shared/studio/api";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type {
  ScopeWorkflowDetail,
  ScopeWorkflowSummary,
} from "@/shared/models/scopes";
import {
  compactTableCardProps,
  moduleCardProps,
} from "@/shared/ui/proComponents";
import ScopeQueryCard from "./components/ScopeQueryCard";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import { renderMultilineText } from "./components/renderMultilineText";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "./components/scopeQuery";

const workflowDetailColumns: ProDescriptionsItemProps<ScopeWorkflowDetail>[] = [
  {
    title: "Display name",
    dataIndex: ["workflow", "displayName"],
  },
  {
    title: "Service key",
    render: (_, record) => (
      <Typography.Text copyable>
        {record.workflow?.serviceKey || "n/a"}
      </Typography.Text>
    ),
  },
  {
    title: "Definition actor",
    render: (_, record) => (
      <Typography.Text copyable>
        {record.source?.definitionActorId || "n/a"}
      </Typography.Text>
    ),
  },
];

const initialDraft = readScopeQueryDraft();
const initialWorkflowId =
  typeof window === "undefined"
    ? ""
    : new URLSearchParams(window.location.search).get("workflowId")?.trim() ??
      "";

const ScopeWorkflowsPage: React.FC = () => {
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedWorkflowId, setSelectedWorkflowId] =
    useState(initialWorkflowId);
  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const studioHostAccessResolved =
    !authSessionQuery.isLoading && !authSessionQuery.isError;
  const studioHostAuthenticated =
    authSessionQuery.data?.enabled === false ||
    Boolean(authSessionQuery.data?.authenticated);
  const appContextQuery = useQuery({
    queryKey: ["scopes", "app-context"],
    enabled: studioHostAccessResolved && studioHostAuthenticated,
    queryFn: () => studioApi.getAppContext(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () =>
      resolveStudioScopeContext(authSessionQuery.data, appContextQuery.data),
    [appContextQuery.data, authSessionQuery.data]
  );

  useEffect(() => {
    history.replace(
      buildScopeHref("/scopes/workflows", activeDraft, {
        workflowId: selectedWorkflowId,
      })
    );
  }, [activeDraft, selectedWorkflowId]);

  useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId }
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId }
    );
  }, [resolvedScope?.scopeId]);

  const workflowsQuery = useQuery({
    queryKey: ["scopes", "workflows", activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listWorkflows(activeDraft.scopeId),
  });
  const workflowDetailQuery = useQuery({
    queryKey: [
      "scopes",
      "workflow-detail",
      activeDraft.scopeId,
      selectedWorkflowId,
    ],
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedWorkflowId.trim().length > 0,
    queryFn: () =>
      scopesApi.getWorkflowDetail(activeDraft.scopeId, selectedWorkflowId),
  });

  const workflowColumns = useMemo<ProColumns<ScopeWorkflowSummary>[]>(
    () => [
      {
        title: "Workflow",
        dataIndex: "workflowId",
        render: (_, record) => (
          <Space direction="vertical" size={0}>
            <Typography.Text strong>
              {record.displayName || record.workflowId}
            </Typography.Text>
            <Typography.Text type="secondary">
              {record.workflowId}
            </Typography.Text>
          </Space>
        ),
      },
      {
        title: "Workflow name",
        dataIndex: "workflowName",
      },
      {
        title: "Actor",
        dataIndex: "actorId",
        render: (_, record) => (
          <Typography.Text copyable>{record.actorId}</Typography.Text>
        ),
      },
      {
        title: "Revision",
        dataIndex: "activeRevisionId",
      },
      {
        title: "Deployment",
        dataIndex: "deploymentStatus",
        render: (_, record) =>
          `${record.deploymentStatus || "unknown"}${
            record.deploymentId ? ` · ${record.deploymentId}` : ""
          }`,
      },
      {
        title: "Updated",
        dataIndex: "updatedAt",
        render: (_, record) => formatDateTime(record.updatedAt),
      },
      {
        title: "Action",
        valueType: "option",
        render: (_, record) => [
          <Button
            key={`workflow-${record.workflowId}`}
            type="link"
            onClick={() => setSelectedWorkflowId(record.workflowId)}
          >
            Inspect
          </Button>,
        ],
      },
    ],
    []
  );

  return (
    <PageContainer
      title="Scope Workflows"
      content="Inspect published workflow assets for a single scope."
      onBack={() => history.push(buildScopeHref("/scopes", activeDraft))}
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <ScopeQueryCard
            draft={draft}
            onChange={setDraft}
            loadLabel="Load workflow assets"
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
              setSelectedWorkflowId("");
            }}
            onLoad={() => {
              const nextDraft = normalizeScopeDraft(draft);
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedWorkflowId("");
            }}
            onReset={() => {
              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope?.scopeId ?? "",
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedWorkflowId("");
            }}
          />
        </Col>

        <Col xs={24}>
          {activeDraft.scopeId.trim() ? (
            <ProTable<ScopeWorkflowSummary>
              columns={workflowColumns}
              dataSource={workflowsQuery.data ?? []}
              loading={workflowsQuery.isLoading}
              rowKey="workflowId"
              search={false}
              pagination={{ pageSize: 10 }}
              cardProps={compactTableCardProps}
              toolBarRender={false}
            />
          ) : (
            <Alert
              showIcon
              type="info"
              title="Select a scope to inspect its published workflow assets."
            />
          )}
        </Col>
      </Row>

      <Drawer
        destroyOnHidden
        open={selectedWorkflowId.trim().length > 0}
        title={
          selectedWorkflowId ? `Workflow ${selectedWorkflowId}` : "Workflow"
        }
        size={760}
        onClose={() => setSelectedWorkflowId("")}
      >
        {workflowDetailQuery.data ? (
          <Space direction="vertical" size={16} style={{ width: "100%" }}>
            <ProDescriptions<ScopeWorkflowDetail>
              column={1}
              dataSource={workflowDetailQuery.data}
              columns={workflowDetailColumns}
            />
            <ProCard title="Workflow YAML" {...moduleCardProps}>
              {renderMultilineText(
                workflowDetailQuery.data.source?.workflowYaml
              )}
            </ProCard>
          </Space>
        ) : (
          <Alert
            showIcon
            type="info"
            title="Select a workflow to inspect its source."
          />
        )}
      </Drawer>
    </PageContainer>
  );
};

export default ScopeWorkflowsPage;
