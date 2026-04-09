import {
  CodeOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import {
  PageContainer,
  ProCard,
  ProList,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Drawer,
  Space,
  Tabs,
  Tag,
  Typography,
  theme,
} from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { history } from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute } from "@/shared/navigation/scopeRoutes";
import { scopesApi } from "@/shared/api/scopesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { buildRuntimeGAgentsHref } from "@/shared/navigation/runtimeRoutes";
import type {
  ScopeScriptCatalog,
  ScopeScriptSummary,
  ScopeWorkflowSummary,
} from "@/shared/models/scopes";
import { studioApi } from "@/shared/studio/api";
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  formatStudioScopeBindingImplementationKind,
  getStudioScopeBindingCurrentRevision,
} from "@/shared/studio/models";
import {
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import {
  AEVATAR_GLOBAL_UI_SPEC,
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarMetricCardStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  resolveAevatarMetricVisual,
  type AevatarAssetLifecycleStatus,
  type AevatarStatusDomain,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import {
  embeddedPanelStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { AevatarTitleWithHelp } from "@/shared/ui/aevatarPageShells";
import ScopeQueryCard from "./components/ScopeQueryCard";
import { renderMultilineText } from "./components/renderMultilineText";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "./components/scopeQuery";

type AssetTab = "scripts" | "workflows";

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  tone?: "default" | "info" | "success" | "warning";
  value: React.ReactNode;
};

type AssetWorkspaceItem = {
  actorId?: string;
  assetId: string;
  capabilityStatus: AevatarAssetLifecycleStatus;
  governanceStatus?: string;
  key: string;
  primaryMetaLabel: string;
  primaryMetaValue: string;
  secondaryMetaLabel: string;
  secondaryMetaValue: string;
  subtitle: string;
  summary: string;
  tertiaryMetaLabel?: string;
  tertiaryMetaValue?: string;
  title: string;
  updatedAtLabel: string;
};

type InspectorSelection =
  | { assetId: string; kind: "script" }
  | { assetId: string; kind: "workflow" }
  | null;

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {typeof value === "string" || typeof value === "number" ? (
      <Typography.Text>{value}</Typography.Text>
    ) : (
      value
    )}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = "default",
  value,
}) => {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={buildAevatarMetricCardStyle(
        token as AevatarThemeSurfaceToken,
        tone,
      )}
    >
      <Typography.Text style={{ ...summaryFieldLabelStyle, color: visual.labelColor }}>
        {label}
      </Typography.Text>
      <Typography.Text style={{ ...summaryMetricValueStyle, color: visual.valueColor }}>
        {value}
      </Typography.Text>
    </div>
  );
};

const StatusTag: React.FC<{
  domain: AevatarStatusDomain;
  label?: string;
  status: string;
}> = ({ domain, label, status }) => {
  const { token } = theme.useToken();

  return (
    <Tag
      bordered
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        domain,
        status,
      )}
    >
      {label ?? formatAevatarStatusLabel(status)}
    </Tag>
  );
};

function readAssetTab(): AssetTab {
  if (typeof window === "undefined") {
    return "workflows";
  }

  const tab = new URLSearchParams(window.location.search).get("tab")?.trim();
  return tab === "scripts" ? "scripts" : "workflows";
}

function readWorkflowId(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("workflowId")?.trim() ?? ""
  );
}

function readScriptId(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("scriptId")?.trim() ?? ""
  );
}

function resolveWorkflowCapabilityStatus(
  workflow: ScopeWorkflowSummary,
): AevatarAssetLifecycleStatus {
  const deploymentStatus = workflow.deploymentStatus.trim().toLowerCase();

  if (
    workflow.deploymentId.trim() ||
    deploymentStatus === "published" ||
    deploymentStatus === "active" ||
    deploymentStatus === "activated" ||
    deploymentStatus === "canary"
  ) {
    return "active";
  }

  return workflow.activeRevisionId.trim() ? "draft" : "draft";
}

function resolveScriptCapabilityStatus(
  script: ScopeScriptSummary,
): AevatarAssetLifecycleStatus {
  return script.activeRevision.trim() ? "active" : "draft";
}

function buildWorkflowWorkspaceItems(
  workflows: ScopeWorkflowSummary[] | undefined,
): AssetWorkspaceItem[] {
  return (workflows ?? [])
    .map((workflow) => ({
      actorId: workflow.actorId,
      assetId: workflow.workflowId,
      capabilityStatus: resolveWorkflowCapabilityStatus(workflow),
      governanceStatus: workflow.deploymentStatus.trim() || "draft",
      key: `workflow:${workflow.workflowId}`,
      primaryMetaLabel: "Revision",
      primaryMetaValue: workflow.activeRevisionId || "n/a",
      secondaryMetaLabel: "Updated",
      secondaryMetaValue: formatDateTime(workflow.updatedAt),
      subtitle: workflow.workflowName || "Workflow capability",
      summary: workflow.serviceKey
        ? `Published entrypoint ${workflow.serviceKey}`
        : "Workflow capability pending published binding.",
      tertiaryMetaLabel: "Entrypoint",
      tertiaryMetaValue: workflow.serviceKey || "Unbound",
      title: workflow.displayName || workflow.workflowId,
      updatedAtLabel: formatDateTime(workflow.updatedAt),
    }))
    .sort((left, right) => left.title.localeCompare(right.title));
}

function buildScriptWorkspaceItems(
  scripts: ScopeScriptSummary[] | undefined,
): AssetWorkspaceItem[] {
  return (scripts ?? [])
    .map((script) => ({
      actorId: script.catalogActorId,
      assetId: script.scriptId,
      capabilityStatus: resolveScriptCapabilityStatus(script),
      key: `script:${script.scriptId}`,
      primaryMetaLabel: "Revision",
      primaryMetaValue: script.activeRevision || "n/a",
      secondaryMetaLabel: "Updated",
      secondaryMetaValue: formatDateTime(script.updatedAt),
      subtitle: "Script capability",
      summary: "Governed script asset ready for Studio and catalog inspection.",
      tertiaryMetaLabel: "Source hash",
      tertiaryMetaValue: script.activeSourceHash || "n/a",
      title: script.scriptId,
      updatedAtLabel: formatDateTime(script.updatedAt),
    }))
    .sort((left, right) => left.title.localeCompare(right.title));
}

const TeamAssetsPage: React.FC = () => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;

  const [draft, setDraft] = useState<ScopeQueryDraft>(() => readScopeQueryDraft());
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );
  const [activeTab, setActiveTab] = useState<AssetTab>(readAssetTab);
  const [selectedWorkflowId, setSelectedWorkflowId] = useState(readWorkflowId);
  const [selectedScriptId, setSelectedScriptId] = useState(readScriptId);

  const authSessionQuery = useQuery({
    queryKey: ["scopes", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  useEffect(() => {
    history.replace(
      buildScopeHref("/scopes/assets", activeDraft, {
        scriptId: activeTab === "scripts" ? selectedScriptId : "",
        tab: activeTab,
        workflowId: activeTab === "workflows" ? selectedWorkflowId : "",
      }),
    );
  }, [activeDraft, activeTab, selectedScriptId, selectedWorkflowId]);

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

  const workflowsQuery = useQuery({
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listWorkflows(activeDraft.scopeId),
    queryKey: ["scopes", "workflows", activeDraft.scopeId],
  });
  const scriptsQuery = useQuery({
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => scopesApi.listScripts(activeDraft.scopeId),
    queryKey: ["scopes", "scripts", activeDraft.scopeId],
  });
  const bindingQuery = useQuery({
    enabled: activeDraft.scopeId.trim().length > 0,
    queryFn: () => studioApi.getScopeBinding(activeDraft.scopeId),
    queryKey: ["scopes", "binding", activeDraft.scopeId],
  });
  const workflowDetailQuery = useQuery({
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedWorkflowId.trim().length > 0,
    queryFn: () =>
      scopesApi.getWorkflowDetail(activeDraft.scopeId, selectedWorkflowId),
    queryKey: [
      "scopes",
      "workflow-detail",
      activeDraft.scopeId,
      selectedWorkflowId,
    ],
  });
  const scriptDetailQuery = useQuery({
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedScriptId.trim().length > 0,
    queryFn: () => scopesApi.getScriptDetail(activeDraft.scopeId, selectedScriptId),
    queryKey: ["scopes", "script-detail", activeDraft.scopeId, selectedScriptId],
  });
  const scriptCatalogQuery = useQuery({
    enabled:
      activeDraft.scopeId.trim().length > 0 &&
      selectedScriptId.trim().length > 0,
    queryFn: () =>
      scopesApi.getScriptCatalog(activeDraft.scopeId, selectedScriptId),
    queryKey: [
      "scopes",
      "script-catalog",
      activeDraft.scopeId,
      selectedScriptId,
    ],
  });

  const workflowItems = useMemo(
    () => buildWorkflowWorkspaceItems(workflowsQuery.data),
    [workflowsQuery.data],
  );
  const scriptItems = useMemo(
    () => buildScriptWorkspaceItems(scriptsQuery.data),
    [scriptsQuery.data],
  );

  const workflowCount = workflowItems.length;
  const scriptCount = scriptItems.length;
  const activeCapabilityCount = [...workflowItems, ...scriptItems].filter(
    (item) => item.capabilityStatus === "active",
  ).length;
  const draftCapabilityCount = workflowCount + scriptCount - activeCapabilityCount;
  const currentBindingRevision = getStudioScopeBindingCurrentRevision(
    bindingQuery.data,
  );
  const currentBindingLabel = bindingQuery.data?.available
    ? currentBindingRevision
      ? describeStudioScopeBindingRevisionTarget(currentBindingRevision)
      : bindingQuery.data.displayName || bindingQuery.data.serviceId
    : "Not bound";
  const currentBindingKind = currentBindingRevision
    ? formatStudioScopeBindingImplementationKind(
        currentBindingRevision.implementationKind,
      )
    : bindingQuery.data?.available
      ? "Published"
      : "Unknown";
  const currentBindingContext = describeStudioScopeBindingRevisionContext(
    currentBindingRevision,
  );
  const currentBindingActor =
    currentBindingRevision?.primaryActorId ||
    bindingQuery.data?.primaryActorId ||
    "";

  const selectedWorkflow = useMemo(
    () =>
      workflowsQuery.data?.find(
        (workflow) => workflow.workflowId === selectedWorkflowId,
      ) ?? null,
    [selectedWorkflowId, workflowsQuery.data],
  );
  const selectedScript = useMemo(
    () =>
      scriptsQuery.data?.find((script) => script.scriptId === selectedScriptId) ??
      null,
    [scriptsQuery.data, selectedScriptId],
  );

  const inspectorSelection: InspectorSelection = selectedWorkflowId.trim()
    ? { assetId: selectedWorkflowId, kind: "workflow" }
    : selectedScriptId.trim()
      ? { assetId: selectedScriptId, kind: "script" }
      : null;

  const workflowListMetas = useMemo<ProListMetas<AssetWorkspaceItem>>(
    () => ({
      actions: {
        render: (_, record) => [
          <Button
            key={`inspect-${record.assetId}`}
            icon={<EyeOutlined />}
            onClick={() => {
              setActiveTab("workflows");
              setSelectedScriptId("");
              setSelectedWorkflowId(record.assetId);
            }}
          >
            Inspect
          </Button>,
          <Button
            key={`studio-${record.assetId}`}
            type="link"
            onClick={() =>
              history.push(
                buildStudioWorkflowEditorRoute({
                  workflowId: record.assetId,
                }),
              )
            }
          >
            Open workflow editor
          </Button>,
        ],
      },
      avatar: {
        render: () => (
          <div
            style={{
              alignItems: "center",
              background: surfaceToken.colorFillTertiary,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadiusLG,
              color: surfaceToken.colorPrimary,
              display: "inline-flex",
              height: 36,
              justifyContent: "center",
              width: 36,
            }}
          >
            <DeploymentUnitOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
              {record.summary}
            </Typography.Text>
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(120px, 1fr))",
              }}
            >
              <AssetMetaField
                label={record.primaryMetaLabel}
                value={record.primaryMetaValue}
              />
              <AssetMetaField
                label={record.secondaryMetaLabel}
                value={record.secondaryMetaValue}
              />
              <AssetMetaField
                label={record.tertiaryMetaLabel ?? "Actor"}
                value={record.tertiaryMetaValue ?? record.actorId ?? "n/a"}
              />
            </div>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.subtitle}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={8}>
            <StatusTag
              domain="asset"
              label={formatAevatarStatusLabel(record.capabilityStatus)}
              status={record.capabilityStatus}
            />
            {record.governanceStatus ? (
              <StatusTag
                domain="governance"
                label={formatAevatarStatusLabel(record.governanceStatus)}
                status={record.governanceStatus}
              />
            ) : null}
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Space direction="vertical" size={2}>
            <Typography.Text
              strong
              style={{ color: surfaceToken.colorTextHeading, fontSize: 15 }}
            >
              {record.title}
            </Typography.Text>
            <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
              Last synced {record.updatedAtLabel}
            </Typography.Text>
          </Space>
        ),
      },
    }),
    [surfaceToken],
  );

  const scriptListMetas = useMemo<ProListMetas<AssetWorkspaceItem>>(
    () => ({
      actions: {
        render: (_, record) => [
          <Button
            key={`inspect-${record.assetId}`}
            icon={<EyeOutlined />}
            onClick={() => {
              setActiveTab("scripts");
              setSelectedWorkflowId("");
              setSelectedScriptId(record.assetId);
            }}
          >
            Inspect
          </Button>,
          <Button
            key={`studio-${record.assetId}`}
            type="link"
            onClick={() =>
              history.push(
                buildStudioScriptsWorkspaceRoute({
                  scriptId: record.assetId,
                }),
              )
            }
          >
            Open scripts workspace
          </Button>,
        ],
      },
      avatar: {
        render: () => (
          <div
            style={{
              alignItems: "center",
              background: surfaceToken.colorFillTertiary,
              border: `1px solid ${surfaceToken.colorBorderSecondary}`,
              borderRadius: surfaceToken.borderRadiusLG,
              color: surfaceToken.colorPrimary,
              display: "inline-flex",
              height: 36,
              justifyContent: "center",
              width: 36,
            }}
          >
            <CodeOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Typography.Text style={{ color: surfaceToken.colorTextSecondary }}>
              {record.summary}
            </Typography.Text>
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(120px, 1fr))",
              }}
            >
              <AssetMetaField
                label={record.primaryMetaLabel}
                value={record.primaryMetaValue}
              />
              <AssetMetaField
                label={record.secondaryMetaLabel}
                value={record.secondaryMetaValue}
              />
              <AssetMetaField
                label={record.tertiaryMetaLabel ?? "Catalog actor"}
                value={record.tertiaryMetaValue ?? record.actorId ?? "n/a"}
              />
            </div>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.subtitle}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={8}>
            <StatusTag
              domain="asset"
              label={formatAevatarStatusLabel(record.capabilityStatus)}
              status={record.capabilityStatus}
            />
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Space direction="vertical" size={2}>
            <Typography.Text
              strong
              style={{ color: surfaceToken.colorTextHeading, fontSize: 15 }}
            >
              {record.title}
            </Typography.Text>
            <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
              Last synced {record.updatedAtLabel}
            </Typography.Text>
          </Space>
        ),
      },
    }),
    [surfaceToken],
  );

  return (
    <PageContainer
      className="aevatar-page-shell-document"
      extra={[
        <Button
          key="open-studio"
          type="primary"
          onClick={() =>
            history.push(
              activeTab === "scripts"
                ? buildStudioScriptsWorkspaceRoute()
                : buildStudioWorkflowWorkspaceRoute(),
            )
          }
        >
          {activeTab === "scripts"
            ? "Open scripts workspace"
            : "Open workflow workspace"}
        </Button>,
        <Button
          key="open-overview"
          onClick={() => history.push(buildTeamWorkspaceRoute(activeDraft.scopeId))}
        >
          Open Team Workspace
        </Button>,
        <Button
          key="open-gagents"
          disabled={!activeDraft.scopeId.trim()}
          onClick={() =>
            history.push(
              buildRuntimeGAgentsHref({
                scopeId: activeDraft.scopeId.trim(),
                actorId: currentBindingRevision?.primaryActorId || undefined,
                actorTypeName: currentBindingRevision?.staticActorTypeName || undefined,
              }),
            )
          }
        >
          Open Member Runtime
        </Button>,
      ]}
      onBack={() => history.push(buildTeamWorkspaceRoute(activeDraft.scopeId))}
      title={
        <AevatarTitleWithHelp
          help="Browse the workflows and scripts owned by the current team from a single asset workspace. Capability state stays on stage, while source detail moves into the inspector."
          title="Team Assets"
        />
      }
    >
      <div
        style={{
          display: "flex",
          flexDirection: "column",
          gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
          width: "100%",
        }}
      >
        <ScopeQueryCard
          draft={draft}
          loadLabel="Load team assets"
          onChange={setDraft}
          onLoad={() => {
            const nextDraft = normalizeScopeDraft(draft);
            setDraft(nextDraft);
            setActiveDraft(nextDraft);
            setSelectedWorkflowId("");
            setSelectedScriptId("");
          }}
          onReset={() => {
            const nextDraft = normalizeScopeDraft({
              scopeId: resolvedScope?.scopeId ?? "",
            });
            setDraft(nextDraft);
            setActiveDraft(nextDraft);
            setSelectedWorkflowId("");
            setSelectedScriptId("");
            setActiveTab("workflows");
          }}
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
            setSelectedScriptId("");
            setActiveTab("workflows");
          }}
          resolvedScopeId={resolvedScope?.scopeId}
          resolvedScopeSource={resolvedScope?.scopeSource}
        />

        {!activeDraft.scopeId.trim() ? (
          <Alert
            showIcon
            message="Select a team to inspect its workflow and script assets."
            type="info"
          />
        ) : (
          <>
            <ProCard
              bodyStyle={{ padding: 18 }}
              style={buildAevatarPanelStyle(surfaceToken)}
              title="Team asset summary"
            >
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))",
                }}
              >
                <SummaryMetric label="Team" value={activeDraft.scopeId} />
                <SummaryMetric label="Default binding" value={currentBindingLabel} />
                <SummaryMetric label="Binding kind" value={currentBindingKind} />
                <SummaryMetric
                  label="Live capabilities"
                  tone="success"
                  value={activeCapabilityCount}
                />
                <SummaryMetric
                  label="Draft capabilities"
                  tone="warning"
                  value={draftCapabilityCount}
                />
              </div>

              <div
                style={{
                  ...embeddedPanelStyle,
                  background: surfaceToken.colorFillAlter,
                  borderColor: surfaceToken.colorBorderSecondary,
                  marginTop: 16,
                }}
              >
                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Focus"
                    value="Stage capability posture first. Open the inspector only when you need source, schema, or catalog detail."
                  />
                  <SummaryField
                    label="Binding detail"
                    value={
                      currentBindingContext ||
                      bindingQuery.data?.serviceKey ||
                      "No published default binding"
                    }
                  />
                  <SummaryField
                    label="Serving actor"
                    value={currentBindingActor || "n/a"}
                  />
                  <SummaryField
                    label="Workflows"
                    value={`${workflowCount} capability${workflowCount === 1 ? "" : "ies"} tracked in this team`}
                  />
                  <SummaryField
                    label="Scripts"
                    value={`${scriptCount} governed script asset${scriptCount === 1 ? "" : "s"} available for Studio`}
                  />
                </div>
              </div>
            </ProCard>

            <ProCard
              bodyStyle={{ display: "flex", flexDirection: "column" }}
              style={buildAevatarPanelStyle(surfaceToken)}
            >
              <div
                style={{
                  alignItems: "flex-start",
                  borderBottom: `1px solid ${surfaceToken.colorBorderSecondary}`,
                  display: "flex",
                  gap: 12,
                  justifyContent: "space-between",
                  padding: "16px 18px 0",
                }}
              >
                <Space direction="vertical" size={2}>
                  <Typography.Text
                    strong
                    style={{ color: surfaceToken.colorTextHeading, fontSize: 16 }}
                  >
                    Capability inventory
                  </Typography.Text>
                  <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
                    One working surface for team-owned assets. Inspectors carry the heavy detail, not the list.
                  </Typography.Text>
                </Space>
                <StatusTag
                  domain="asset"
                  label={`${activeCapabilityCount} active`}
                  status="active"
                />
              </div>

              <Tabs
                activeKey={activeTab}
                onChange={(nextKey) => {
                  const nextTab = nextKey === "scripts" ? "scripts" : "workflows";
                  setActiveTab(nextTab);
                  if (nextTab === "workflows") {
                    setSelectedScriptId("");
                  } else {
                    setSelectedWorkflowId("");
                  }
                }}
                style={{ padding: "0 18px 18px" }}
                items={[
                  {
                    children: (
                      <ProList<AssetWorkspaceItem>
                        dataSource={workflowItems}
                        grid={{ gutter: 12, md: 2, sm: 1, xl: 2, xs: 1, xxl: 3 }}
                        itemCardProps={{
                          bodyStyle: { padding: 16 },
                          style: {
                            background: surfaceToken.colorBgContainer,
                            border: `1px solid ${surfaceToken.colorBorderSecondary}`,
                            borderRadius: surfaceToken.borderRadiusLG,
                            boxShadow: surfaceToken.boxShadowSecondary,
                          },
                        }}
                        locale={{
                          emptyText: "No workflow assets were found for this team.",
                        }}
                        metas={workflowListMetas}
                        pagination={{ pageSize: 6, showSizeChanger: false }}
                        rowKey="key"
                        search={false}
                        showActions="always"
                        split={false}
                        toolBarRender={false}
                      />
                    ),
                    key: "workflows",
                    label: `Workflows (${workflowCount})`,
                  },
                  {
                    children: (
                      <ProList<AssetWorkspaceItem>
                        dataSource={scriptItems}
                        grid={{ gutter: 12, md: 2, sm: 1, xl: 2, xs: 1, xxl: 3 }}
                        itemCardProps={{
                          bodyStyle: { padding: 16 },
                          style: {
                            background: surfaceToken.colorBgContainer,
                            border: `1px solid ${surfaceToken.colorBorderSecondary}`,
                            borderRadius: surfaceToken.borderRadiusLG,
                            boxShadow: surfaceToken.boxShadowSecondary,
                          },
                        }}
                        locale={{
                          emptyText: "No script assets were found for this team.",
                        }}
                        metas={scriptListMetas}
                        pagination={{ pageSize: 6, showSizeChanger: false }}
                        rowKey="key"
                        search={false}
                        showActions="always"
                        split={false}
                        toolBarRender={false}
                      />
                    ),
                    key: "scripts",
                    label: `Scripts (${scriptCount})`,
                  },
                ]}
              />
            </ProCard>
          </>
        )}
      </div>

      <Drawer
        destroyOnHidden
        open={Boolean(inspectorSelection)}
        styles={{ body: aevatarDrawerBodyStyle }}
        title={
          inspectorSelection?.kind === "workflow"
            ? selectedWorkflow?.displayName || selectedWorkflowId || "Workflow"
            : selectedScriptId || "Script"
        }
        size={AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth}
        onClose={() => {
          setSelectedWorkflowId("");
          setSelectedScriptId("");
        }}
      >
        <div style={aevatarDrawerScrollStyle}>
          {inspectorSelection?.kind === "workflow" ? (
            workflowDetailQuery.data ? (
              <>
                <div
                  style={{
                    ...embeddedPanelStyle,
                    background: surfaceToken.colorFillAlter,
                    borderColor: surfaceToken.colorBorderSecondary,
                  }}
                >
                  <Space wrap size={8} style={{ marginBottom: 12 }}>
                    <StatusTag
                      domain="asset"
                      status={
                        selectedWorkflow
                          ? resolveWorkflowCapabilityStatus(selectedWorkflow)
                          : "draft"
                      }
                    />
                    {selectedWorkflow?.deploymentStatus ? (
                      <StatusTag
                        domain="governance"
                        status={selectedWorkflow.deploymentStatus}
                      />
                    ) : null}
                  </Space>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label="Display name"
                      value={workflowDetailQuery.data.workflow?.displayName || "n/a"}
                    />
                    <SummaryField
                      label="Revision"
                      value={selectedWorkflow?.activeRevisionId || "n/a"}
                    />
                    <SummaryField
                      label="Service key"
                      value={
                        <Typography.Text copyable>
                          {workflowDetailQuery.data.workflow?.serviceKey || "n/a"}
                        </Typography.Text>
                      }
                    />
                    <SummaryField
                      label="Definition actor"
                      value={
                        <Typography.Text copyable>
                          {workflowDetailQuery.data.source?.definitionActorId || "n/a"}
                        </Typography.Text>
                      }
                    />
                  </div>
                </div>
                <ProCard
                  bodyStyle={{ padding: 16 }}
                  style={buildAevatarPanelStyle(surfaceToken)}
                  title="Workflow YAML"
                >
                  {renderMultilineText(workflowDetailQuery.data.source?.workflowYaml)}
                </ProCard>
                <Button
                  type="primary"
                  onClick={() =>
                    history.push(
                      buildStudioWorkflowEditorRoute({
                        workflowId: selectedWorkflowId,
                      }),
                    )
                  }
                >
                  Open workflow editor
                </Button>
              </>
            ) : (
              <Alert
                showIcon
                message="Select a workflow asset to inspect its source."
                type="info"
              />
            )
          ) : scriptDetailQuery.data ? (
            <>
              <div
                style={{
                  ...embeddedPanelStyle,
                  background: surfaceToken.colorFillAlter,
                  borderColor: surfaceToken.colorBorderSecondary,
                }}
              >
                <Space wrap size={8} style={{ marginBottom: 12 }}>
                  <StatusTag
                    domain="asset"
                    status={
                      selectedScript
                        ? resolveScriptCapabilityStatus(selectedScript)
                        : "draft"
                    }
                  />
                </Space>
                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Revision"
                    value={scriptDetailQuery.data.script?.activeRevision || "n/a"}
                  />
                  <SummaryField
                    label="Definition actor"
                    value={
                      <Typography.Text copyable>
                        {scriptDetailQuery.data.script?.definitionActorId || "n/a"}
                      </Typography.Text>
                    }
                  />
                  <SummaryField
                    label="Catalog actor"
                    value={
                      <Typography.Text copyable>
                        {scriptDetailQuery.data.script?.catalogActorId || "n/a"}
                      </Typography.Text>
                    }
                  />
                </div>
              </div>
              <ProCard
                bodyStyle={{ padding: 16 }}
                style={buildAevatarPanelStyle(surfaceToken)}
                title="Catalog state"
              >
                {scriptCatalogQuery.data ? (
                  <ScopeScriptCatalogSummary catalog={scriptCatalogQuery.data} />
                ) : (
                  <Typography.Text type="secondary">
                    Catalog snapshot unavailable.
                  </Typography.Text>
                )}
              </ProCard>
              <ProCard
                bodyStyle={{ padding: 16 }}
                style={buildAevatarPanelStyle(surfaceToken)}
                title="Source text"
              >
                {renderMultilineText(scriptDetailQuery.data.source?.sourceText)}
              </ProCard>
              <Button
                type="primary"
                onClick={() =>
                  history.push(
                    buildStudioScriptsWorkspaceRoute({
                      scriptId: selectedScriptId,
                    }),
                  )
                }
              >
                Open scripts workspace
              </Button>
            </>
          ) : (
            <Alert
              showIcon
              message="Select a script asset to inspect its source."
              type="info"
            />
          )}
        </div>
      </Drawer>
    </PageContainer>
  );
};

const AssetMetaField: React.FC<{ label: string; value: React.ReactNode }> = ({
  label,
  value,
}) => (
  <div
    style={{
      background: "var(--ant-color-fill-quaternary)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 8,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
      padding: "10px 12px",
    }}
  >
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text ellipsis>{value}</Typography.Text>
  </div>
);

const ScopeScriptCatalogSummary: React.FC<{ catalog: ScopeScriptCatalog }> = ({
  catalog,
}) => (
  <div style={summaryFieldGridStyle}>
    <SummaryField label="Active revision" value={catalog.activeRevision || "n/a"} />
    <SummaryField
      label="Previous revision"
      value={catalog.previousRevision || "n/a"}
    />
    <SummaryField
      label="History"
      value={catalog.revisionHistory.join(", ") || "n/a"}
    />
    <SummaryField label="Last proposal" value={catalog.lastProposalId || "n/a"} />
  </div>
);

export default TeamAssetsPage;
