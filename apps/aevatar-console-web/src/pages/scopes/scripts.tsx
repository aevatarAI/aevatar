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
import { buildStudioRoute } from "@/shared/studio/navigation";
import { formatDateTime } from "@/shared/datetime/dateTime";
import type {
  ScopeScriptCatalog,
  ScopeScriptDetail,
  ScopeScriptSummary,
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

const scriptDetailColumns: ProDescriptionsItemProps<ScopeScriptDetail>[] = [
  {
    title: "Revision",
    render: (_, record) => record.script?.activeRevision || "n/a",
  },
  {
    title: "Definition actor",
    render: (_, record) => (
      <Typography.Text copyable>
        {record.script?.definitionActorId || "n/a"}
      </Typography.Text>
    ),
  },
  {
    title: "Catalog actor",
    render: (_, record) => (
      <Typography.Text copyable>
        {record.script?.catalogActorId || "n/a"}
      </Typography.Text>
    ),
  },
];

const initialDraft = readScopeQueryDraft();
const initialScriptId =
  typeof window === "undefined"
    ? ""
    : new URLSearchParams(window.location.search).get("scriptId")?.trim() ?? "";

const ScopeScriptsPage: React.FC = () => {
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [selectedScriptId, setSelectedScriptId] = useState(initialScriptId);
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
      buildScopeHref("/scopes/scripts", activeDraft, {
        scriptId: selectedScriptId,
      })
    );
  }, [activeDraft, selectedScriptId]);

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

  const scriptsQuery = useQuery({
    queryKey: ["scopes", "scripts", activeDraft.scopeId],
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listScripts(activeDraft.scopeId),
  });
  const scriptDetailQuery = useQuery({
    queryKey: [
      "scopes",
      "script-detail",
      activeDraft.scopeId,
      selectedScriptId,
    ],
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedScriptId.trim().length > 0,
    queryFn: () =>
      scopesApi.getScriptDetail(activeDraft.scopeId, selectedScriptId),
  });
  const scriptCatalogQuery = useQuery({
    queryKey: [
      "scopes",
      "script-catalog",
      activeDraft.scopeId,
      selectedScriptId,
    ],
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedScriptId.trim().length > 0,
    queryFn: () =>
      scopesApi.getScriptCatalog(activeDraft.scopeId, selectedScriptId),
  });

  const scriptColumns = useMemo<ProColumns<ScopeScriptSummary>[]>(
    () => [
      {
        title: "Script",
        dataIndex: "scriptId",
      },
      {
        title: "Revision",
        dataIndex: "activeRevision",
      },
      {
        title: "Source hash",
        dataIndex: "activeSourceHash",
        render: (_, record) => (
          <Typography.Text copyable>{record.activeSourceHash}</Typography.Text>
        ),
      },
      {
        title: "Definition actor",
        dataIndex: "definitionActorId",
        render: (_, record) => (
          <Typography.Text copyable>{record.definitionActorId}</Typography.Text>
        ),
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
            key={`script-${record.scriptId}`}
            type="link"
            onClick={() => setSelectedScriptId(record.scriptId)}
          >
            Inspect
          </Button>,
          <Button
            key={`studio-${record.scriptId}`}
            type="link"
            onClick={() =>
              history.push(
                buildStudioRoute({
                  tab: "scripts",
                  scriptId: record.scriptId,
                })
              )
            }
          >
            Open In Studio
          </Button>,
        ],
      },
    ],
    []
  );

  return (
    <PageContainer
      title="Scope Scripts"
      content="Inspect script catalog state and source material for a single scope. tenantId and appId stay platform-managed and hidden behind this view."
      onBack={() => history.push(buildScopeHref("/scopes", activeDraft))}
    >
      <Row gutter={[16, 16]}>
        <Col xs={24}>
          <ScopeQueryCard
            draft={draft}
            onChange={setDraft}
            loadLabel="Load script assets"
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
              setSelectedScriptId("");
            }}
            onLoad={() => {
              const nextDraft = normalizeScopeDraft(draft);
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedScriptId("");
            }}
            onReset={() => {
              const nextDraft = normalizeScopeDraft({
                scopeId: resolvedScope?.scopeId ?? "",
              });
              setDraft(nextDraft);
              setActiveDraft(nextDraft);
              setSelectedScriptId("");
            }}
          />
        </Col>

        <Col xs={24}>
          {activeDraft.scopeId.trim() ? (
            <ProTable<ScopeScriptSummary>
              columns={scriptColumns}
              dataSource={scriptsQuery.data ?? []}
              loading={scriptsQuery.isLoading}
              rowKey="scriptId"
              search={false}
              pagination={{ pageSize: 10 }}
              cardProps={compactTableCardProps}
              toolBarRender={false}
            />
          ) : (
            <Alert
              showIcon
              type="info"
              title="Select a scope to inspect its script catalog."
            />
          )}
        </Col>
      </Row>

      <Drawer
        destroyOnHidden
        open={selectedScriptId.trim().length > 0}
        title={selectedScriptId ? `Script ${selectedScriptId}` : "Script"}
        size={760}
        onClose={() => setSelectedScriptId("")}
      >
        {scriptDetailQuery.data ? (
          <Space direction="vertical" size={16} style={{ width: "100%" }}>
            <ProDescriptions<ScopeScriptDetail>
              column={1}
              dataSource={scriptDetailQuery.data}
              columns={scriptDetailColumns}
            />
            <ProCard title="Catalog state" {...moduleCardProps}>
              {scriptCatalogQuery.data ? (
                <ScopeScriptCatalogSummary catalog={scriptCatalogQuery.data} />
              ) : (
                <Typography.Text type="secondary">
                  Catalog snapshot unavailable.
                </Typography.Text>
              )}
            </ProCard>
            <ProCard title="Source text" {...moduleCardProps}>
              {renderMultilineText(scriptDetailQuery.data.source?.sourceText)}
            </ProCard>
            <Button
              type="primary"
              onClick={() =>
                history.push(
                  buildStudioRoute({
                    tab: "scripts",
                    scriptId: selectedScriptId,
                  })
                )
              }
            >
              Open In Studio
            </Button>
          </Space>
        ) : (
          <Alert
            showIcon
            type="info"
            title="Select a script to inspect its source."
          />
        )}
      </Drawer>
    </PageContainer>
  );
};

const ScopeScriptCatalogSummary: React.FC<{ catalog: ScopeScriptCatalog }> = ({
  catalog,
}) => (
  <Space direction="vertical" size={4}>
    <Typography.Text>
      Active revision: {catalog.activeRevision || "n/a"}
    </Typography.Text>
    <Typography.Text>
      Previous revision: {catalog.previousRevision || "n/a"}
    </Typography.Text>
    <Typography.Text>
      History: {catalog.revisionHistory.join(", ") || "n/a"}
    </Typography.Text>
    <Typography.Text>
      Last proposal: {catalog.lastProposalId || "n/a"}
    </Typography.Text>
  </Space>
);

export default ScopeScriptsPage;
