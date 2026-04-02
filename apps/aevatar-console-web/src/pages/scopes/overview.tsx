import {
  CodeOutlined,
  EyeOutlined,
  RocketOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProList } from "@ant-design/pro-components";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeGAgentsHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { studioApi } from "@/shared/studio/api";
import {
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowWorkspaceRoute,
} from "@/shared/studio/navigation";
import type {
  ScopeScriptSummary,
  ScopeWorkflowSummary,
} from "@/shared/models/scopes";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  describeStudioScopeBindingRevisionContext,
  describeStudioScopeBindingRevisionTarget,
  formatStudioScopeBindingImplementationKind,
  getStudioScopeBindingCurrentRevision,
  type StudioScopeBindingRevision,
} from "@/shared/studio/models";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import { resolveStudioScopeContext } from "./components/resolvedScope";
import ScopeQueryCard from "./components/ScopeQueryCard";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "./components/scopeQuery";

type ScopeFocus =
  | { id: string; kind: "revision" | "script" | "workflow" }
  | null;

function readSelectedFocus(): ScopeFocus {
  if (typeof window === "undefined") {
    return null;
  }

  const params = new URLSearchParams(window.location.search);
  const revisionId = params.get("revisionId")?.trim();
  const workflowId = params.get("workflowId")?.trim();
  const scriptId = params.get("scriptId")?.trim();

  if (revisionId) {
    return { kind: "revision", id: revisionId };
  }

  if (workflowId) {
    return { kind: "workflow", id: workflowId };
  }

  if (scriptId) {
    return { kind: "script", id: scriptId };
  }

  return null;
}

const initialDraft = readScopeQueryDraft();
const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function buildScopedServiceHref(scopeId: string, serviceId: string): string {
  const params = new URLSearchParams();
  params.set("tenantId", scopeId.trim());
  params.set("appId", scopeServiceAppId);
  params.set("namespace", scopeServiceNamespace);
  params.set("serviceId", serviceId.trim());
  return `/services?${params.toString()}`;
}

const ScopeOverviewPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<ScopeQueryDraft>(initialDraft);
  const [focus, setFocus] = useState<ScopeFocus>(readSelectedFocus());

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

  const scopeId = activeDraft.scopeId.trim();
  const bindingQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "binding", scopeId],
    queryFn: () => studioApi.getScopeBinding(scopeId),
  });
  const workflowsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "workflows", scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
  });
  const scriptsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "scripts", scopeId],
    queryFn: () => scopesApi.listScripts(scopeId),
  });
  const scopeServicesQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["scopes", "services", scopeId],
    queryFn: () =>
      servicesApi.listServices({
        tenantId: scopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
      }),
  });
  const workflowDetailQuery = useQuery({
    enabled: scopeId.length > 0 && focus?.kind === "workflow",
    queryKey: ["scopes", "workflow", scopeId, focus?.id],
    queryFn: () => scopesApi.getWorkflowDetail(scopeId, focus?.id || ""),
  });
  const scriptDetailQuery = useQuery({
    enabled: scopeId.length > 0 && focus?.kind === "script",
    queryKey: ["scopes", "script", scopeId, focus?.id],
    queryFn: () => scopesApi.getScriptDetail(scopeId, focus?.id || ""),
  });
  const activateRevisionMutation = useMutation({
    mutationFn: (revisionId: string) =>
      studioApi.activateScopeBindingRevision({
        revisionId,
        scopeId,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["scopes", "binding", scopeId],
      });
    },
  });
  const retireRevisionMutation = useMutation({
    mutationFn: (revisionId: string) =>
      studioApi.retireScopeBindingRevision({
        revisionId,
        scopeId,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({
        queryKey: ["scopes", "binding", scopeId],
      });
    },
  });

  useEffect(() => {
    history.replace(
      buildScopeHref("/scopes/overview", activeDraft, {
        revisionId: focus?.kind === "revision" ? focus.id : "",
        workflowId: focus?.kind === "workflow" ? focus.id : "",
        scriptId: focus?.kind === "script" ? focus.id : "",
      }),
    );
  }, [activeDraft, focus]);

  const binding = bindingQuery.data;
  const revisions = binding?.revisions ?? [];
  const activeRevision = getStudioScopeBindingCurrentRevision(binding);
  const focusedRevision =
    focus?.kind === "revision"
      ? revisions.find((item) => item.revisionId === focus.id) ?? null
      : activeRevision;
  const currentBindingTarget = describeStudioScopeBindingRevisionTarget(activeRevision);
  const currentBindingContext = describeStudioScopeBindingRevisionContext(activeRevision);
  const currentBindingActor =
    activeRevision?.primaryActorId ||
    activeRevision?.staticPreferredActorId ||
    binding?.primaryActorId ||
    "";
  const selectedServiceCard =
    scopeServicesQuery.data?.find((service) => service.serviceId === binding?.serviceId) ??
    null;

  const workflowMetas = useMemo<ProListMetas<ScopeWorkflowSummary>>(
    () => ({
      actions: {
        render: (_, workflow) => [
          <Button
            icon={<EyeOutlined />}
            key={`${workflow.workflowId}-inspect`}
            onClick={() => setFocus({ kind: "workflow", id: workflow.workflowId })}
            type="link"
          >
            Inspect
          </Button>,
          <Button
            icon={<RocketOutlined />}
            key={`${workflow.workflowId}-runs`}
            onClick={() =>
              history.push(
                buildRuntimeRunsHref({
                  scopeId,
                }),
              )
            }
            type="link"
          >
            Runs
          </Button>,
        ],
      },
      description: {
        render: (_, workflow) =>
          workflow.serviceKey
            ? `Entrypoint ${workflow.serviceKey}`
            : "Workflow asset is not yet published as a project entrypoint.",
      },
      subTitle: {
        render: (_, workflow) => (
          <Space wrap size={[8, 8]}>
            <AevatarStatusTag
              domain="asset"
              status={workflow.activeRevisionId ? "active" : "draft"}
            />
            <AevatarStatusTag
              domain="governance"
              status={workflow.deploymentStatus || "draft"}
            />
          </Space>
        ),
      },
      title: {
        render: (_, workflow) => workflow.displayName || workflow.workflowId,
      },
    }),
    [scopeId],
  );
  const scriptMetas = useMemo<ProListMetas<ScopeScriptSummary>>(
    () => ({
      actions: {
        render: (_, script) => [
          <Button
            icon={<EyeOutlined />}
            key={`${script.scriptId}-inspect`}
            onClick={() => setFocus({ kind: "script", id: script.scriptId })}
            type="link"
          >
            Inspect
          </Button>,
          <Button
            icon={<CodeOutlined />}
            key={`${script.scriptId}-studio`}
            onClick={() =>
              history.push(
                buildStudioScriptsWorkspaceRoute({
                  scriptId: script.scriptId,
                }),
              )
            }
            type="link"
          >
            Open scripts workspace
          </Button>,
        ],
      },
      description: {
        render: (_, script) =>
          script.activeSourceHash
            ? `Source hash ${script.activeSourceHash}`
            : "Script asset is waiting for a committed source hash.",
      },
      subTitle: {
        render: (_, script) => (
          <Space wrap size={[8, 8]}>
            <AevatarStatusTag
              domain="asset"
              status={script.activeRevision ? "active" : "draft"}
            />
            <Typography.Text type="secondary">
              Revision {script.activeRevision || "n/a"}
            </Typography.Text>
          </Space>
        ),
      },
      title: {
        render: (_, script) => script.scriptId,
      },
    }),
    [],
  );

  return (
    <AevatarPageShell
      layoutMode="document"
      title="Project Overview"
      titleHelp="Project Overview is now a true scope-level status board: binding posture, asset surface, and next-step actions all live on one stage."
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title="Project Scope"
              titleHelp="Everything downstream stays project-scoped once you load a scope."
            >
              <ScopeQueryCard
                draft={draft}
                loadLabel="Load project overview"
                onChange={setDraft}
                onLoad={() => {
                  const nextDraft = normalizeScopeDraft(draft);
                  setDraft(nextDraft);
                  setActiveDraft(nextDraft);
                }}
                onReset={() => {
                  const nextDraft = normalizeScopeDraft({
                    scopeId: resolvedScope?.scopeId ?? "",
                  });
                  setDraft(nextDraft);
                  setActiveDraft(nextDraft);
                  setFocus(null);
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
                }}
                resolvedScopeId={resolvedScope?.scopeId}
                resolvedScopeSource={resolvedScope?.scopeSource}
              />
            </AevatarPanel>

            <AevatarPanel title="Project Lanes">
              <Space direction="vertical" size={8} style={{ width: "100%" }}>
                <Button onClick={() => history.push(buildScopeHref("/scopes/assets", activeDraft))}>
                  Open assets
                </Button>
                <Button onClick={() => history.push(buildScopeHref("/scopes/invoke", activeDraft, {
                  serviceId: binding?.serviceId ?? "",
                }))}>
                  Open invoke lab
                </Button>
                <Button
                  onClick={() =>
                    history.push(
                      buildRuntimeGAgentsHref({
                        scopeId,
                        actorId: activeRevision?.staticPreferredActorId || undefined,
                        actorTypeName: activeRevision?.staticActorTypeName || undefined,
                      }),
                    )
                  }
                >
                  Manage GAgents
                </Button>
                <Button
                  onClick={() =>
                    history.push(buildStudioWorkflowWorkspaceRoute())
                  }
                >
                  Open workflow workspace
                </Button>
                <Button
                  onClick={() =>
                    history.push(
                      selectedServiceCard
                        ? buildScopedServiceHref(scopeId, selectedServiceCard.serviceId)
                        : "/services",
                    )
                  }
                >
                  Open services
                </Button>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {!scopeId ? (
              <Alert
                title="Select a project to inspect its current binding, active revisions, and owned assets."
                showIcon
                type="info"
              />
            ) : null}

            {scopeId ? (
              <>
                <AevatarPanel
                  title="Scope Status Board"
                  titleHelp="The command surface users actually care about: whether the project is bound, serving, and ready to invoke."
                >
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <MetricCard label="Project" value={scopeId} />
                    <MetricCard
                      label="Binding"
                      value={binding?.available ? binding.displayName || binding.serviceId : "Not bound"}
                    />
                    <MetricCard
                      label="Binding target"
                      value={currentBindingTarget}
                    />
                    <MetricCard
                      label="Binding kind"
                      value={formatStudioScopeBindingImplementationKind(
                        activeRevision?.implementationKind,
                      )}
                    />
                    <MetricCard
                      label="Workflows"
                      value={workflowsQuery.data?.length ?? 0}
                    />
                    <MetricCard label="Scripts" value={scriptsQuery.data?.length ?? 0} />
                    <MetricCard
                      label="Services"
                      value={scopeServicesQuery.data?.length ?? 0}
                    />
                    <MetricCard
                      label="Serving"
                      value={binding?.deploymentStatus || "draft"}
                    />
                  </div>
                </AevatarPanel>

                <AevatarPanel
                  title="Current Binding"
                  titleHelp="The current project binding should be legible without leaving the overview page."
                >
                  {!binding?.available || !activeRevision ? (
                    <Alert
                      title="No published default binding is active for this project yet."
                      showIcon
                      type="info"
                    />
                  ) : (
                    <>
                      <div
                        style={{
                          display: "grid",
                          gap: 12,
                          gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                        }}
                      >
                        <MetricCard label="Service" value={binding.displayName || binding.serviceId} />
                        <MetricCard label="Target" value={currentBindingTarget} />
                        <MetricCard
                          label="Revision"
                          value={activeRevision.revisionId}
                        />
                        <MetricCard
                          label="Actor"
                          value={currentBindingActor || "n/a"}
                        />
                      </div>
                      {currentBindingContext ? (
                        <Alert
                          description={currentBindingContext}
                          showIcon
                          title="Binding detail"
                          type="info"
                        />
                      ) : null}
                      <Space wrap>
                        <Button
                          onClick={() =>
                            history.push(
                              buildRuntimeGAgentsHref({
                                scopeId,
                                actorId:
                                  activeRevision.staticPreferredActorId || undefined,
                                actorTypeName:
                                  activeRevision.staticActorTypeName || undefined,
                              }),
                            )
                          }
                        >
                          Open in GAgents
                        </Button>
                        <Button
                          onClick={() =>
                            history.push(
                              buildScopeHref("/scopes/invoke", activeDraft, {
                                serviceId: binding.serviceId,
                              }),
                            )
                          }
                          type="primary"
                        >
                          Test in Invoke Lab
                        </Button>
                      </Space>
                    </>
                  )}
                </AevatarPanel>

                <AevatarPanel
                  title="Revision Rollout"
                  titleHelp="Binding revisions are treated as the project's runtime posture, not hidden on a secondary page."
                >
                  {revisions.length > 0 ? (
                    <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                      {revisions.map((revision) => (
                        <RevisionCard
                          activating={
                            activateRevisionMutation.isPending &&
                            activateRevisionMutation.variables === revision.revisionId
                          }
                          canActivate={
                            !revision.isActiveServing &&
                            !revision.isDefaultServing &&
                            !revision.retiredAt
                          }
                          canRetire={
                            !revision.retiredAt &&
                            revision.revisionId !== binding?.defaultServingRevisionId
                          }
                          key={revision.revisionId}
                          onActivate={() => activateRevisionMutation.mutate(revision.revisionId)}
                          onInspect={() =>
                            setFocus({ kind: "revision", id: revision.revisionId })
                          }
                          onRetire={() => retireRevisionMutation.mutate(revision.revisionId)}
                          revision={revision}
                          retiring={
                            retireRevisionMutation.isPending &&
                            retireRevisionMutation.variables === revision.revisionId
                          }
                        />
                      ))}
                    </div>
                  ) : (
                    <Empty
                      description="No binding revisions are available yet."
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                    />
                  )}
                </AevatarPanel>

                <div
                  style={{
                    display: "grid",
                    gap: 16,
                    gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
                  }}
                >
                  <AevatarPanel
                    title="Published Services"
                    titleHelp="Published services are the runtime surfaces users can actually invoke from this project."
                  >
                    {scopeServicesQuery.error ? (
                      <Alert
                        showIcon
                        title={
                          scopeServicesQuery.error instanceof Error
                            ? scopeServicesQuery.error.message
                            : "Failed to load published services."
                        }
                        type="error"
                      />
                    ) : scopeServicesQuery.isLoading ? (
                      <AevatarInspectorEmpty description="Loading published services." />
                    ) : scopeServicesQuery.data && scopeServicesQuery.data.length > 0 ? (
                      <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                        {scopeServicesQuery.data.map((service) => (
                          <ScopeServiceCard
                            key={service.serviceKey}
                            onOpenInvoke={() =>
                              history.push(
                                buildScopeHref("/scopes/invoke", activeDraft, {
                                  serviceId: service.serviceId,
                                }),
                              )
                            }
                            onOpenServices={() =>
                              history.push(
                                buildScopedServiceHref(scopeId, service.serviceId),
                              )
                            }
                            service={service}
                          />
                        ))}
                      </div>
                    ) : (
                      <Empty
                        description="No published services were found for this scope."
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                  </AevatarPanel>

                  <AevatarPanel title="Workflow Assets">
                    <ProList<ScopeWorkflowSummary>
                      dataSource={workflowsQuery.data ?? []}
                      grid={{ gutter: 16, column: 1 }}
                      itemCardProps={{
                        bodyStyle: { padding: 16 },
                        style: { borderRadius: 12 },
                      }}
                      locale={{
                        emptyText: (
                          <Empty
                            description="No workflow assets were found for this scope."
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                          />
                        ),
                      }}
                      metas={workflowMetas}
                      pagination={false}
                      rowKey="workflowId"
                      split={false}
                    />
                  </AevatarPanel>

                  <AevatarPanel title="Script Assets">
                    <ProList<ScopeScriptSummary>
                      dataSource={scriptsQuery.data ?? []}
                      grid={{ gutter: 16, column: 1 }}
                      itemCardProps={{
                        bodyStyle: { padding: 16 },
                        style: { borderRadius: 12 },
                      }}
                      locale={{
                        emptyText: (
                          <Empty
                            description="No script assets were found for this scope."
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                          />
                        ),
                      }}
                      metas={scriptMetas}
                      pagination={false}
                      rowKey="scriptId"
                      split={false}
                    />
                  </AevatarPanel>
                </div>
              </>
            ) : null}
          </div>
        }
      />

      <AevatarContextDrawer
        onClose={() => setFocus(null)}
        open={Boolean(focus)}
        subtitle="Scope inspector"
        title={
          focus?.kind === "revision"
            ? focusedRevision?.revisionId || focus?.id || "Revision"
            : focus?.id || "Scope detail"
        }
      >
        {!focus ? (
          <AevatarInspectorEmpty description="Choose a revision, workflow, or script to inspect its role in the current project." />
        ) : focus.kind === "revision" && focusedRevision ? (
          <AevatarPanel
            title="Revision Snapshot"
            titleHelp="Revision posture, rollout identity, and activation affordance stay in one place."
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <MetricCard label="Revision" value={focusedRevision.revisionId} />
              <MetricCard
                label="Implementation"
                value={formatStudioScopeBindingImplementationKind(
                  focusedRevision.implementationKind,
                )}
              />
              <MetricCard
                label="Target"
                value={describeStudioScopeBindingRevisionTarget(focusedRevision)}
              />
              <MetricCard label="Serving state" value={focusedRevision.servingState || focusedRevision.status} />
              <MetricCard
                label="Actor"
                value={
                  focusedRevision.primaryActorId ||
                  focusedRevision.staticPreferredActorId ||
                  "n/a"
                }
              />
            </div>
            {describeStudioScopeBindingRevisionContext(focusedRevision) ? (
              <Alert
                description={describeStudioScopeBindingRevisionContext(
                  focusedRevision,
                )}
                showIcon
                title="Binding detail"
                type="info"
              />
            ) : null}
            <Space wrap>
              <Button
                disabled={focusedRevision.isActiveServing}
                loading={activateRevisionMutation.isPending}
                onClick={() => activateRevisionMutation.mutate(focusedRevision.revisionId)}
                type="primary"
              >
                Activate revision
              </Button>
              <Button
                danger
                disabled={
                  Boolean(focusedRevision.retiredAt) ||
                  focusedRevision.revisionId === binding?.defaultServingRevisionId
                }
                loading={retireRevisionMutation.isPending}
                onClick={() => retireRevisionMutation.mutate(focusedRevision.revisionId)}
              >
                Retire revision
              </Button>
              <Button
                onClick={() =>
                  history.push(
                    buildRuntimeGAgentsHref({
                      scopeId,
                      actorId:
                        focusedRevision.staticPreferredActorId || undefined,
                      actorTypeName:
                        focusedRevision.staticActorTypeName || undefined,
                    }),
                  )
                }
              >
                Open in GAgents
              </Button>
            </Space>
          </AevatarPanel>
        ) : focus.kind === "workflow" ? (
          workflowDetailQuery.data?.available && workflowDetailQuery.data.workflow ? (
            <>
              <AevatarPanel title="Workflow Asset">
                <Space direction="vertical" size={8}>
                  <Space wrap size={[8, 8]}>
                    <AevatarStatusTag
                      domain="asset"
                      status={workflowDetailQuery.data.workflow.activeRevisionId ? "active" : "draft"}
                    />
                    <AevatarStatusTag
                      domain="governance"
                      status={workflowDetailQuery.data.workflow.deploymentStatus || "draft"}
                    />
                  </Space>
                  <Typography.Text strong>
                    {workflowDetailQuery.data.workflow.displayName || workflowDetailQuery.data.workflow.workflowId}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    {workflowDetailQuery.data.workflow.serviceKey || "No published entrypoint"}
                  </Typography.Text>
                </Space>
              </AevatarPanel>
              <AevatarPanel title="Workflow Source">
                <pre style={codeBlockStyle}>
                  {workflowDetailQuery.data.source?.workflowYaml || "No workflow YAML available."}
                </pre>
              </AevatarPanel>
            </>
          ) : (
            <AevatarInspectorEmpty description="Workflow detail is not available yet." />
          )
        ) : focus.kind === "script" ? (
          scriptDetailQuery.data?.available && scriptDetailQuery.data.script ? (
            <>
              <AevatarPanel title="Script Asset">
                <Space direction="vertical" size={8}>
                  <AevatarStatusTag
                    domain="asset"
                    status={scriptDetailQuery.data.script.activeRevision ? "active" : "draft"}
                  />
                  <Typography.Text strong>
                    {scriptDetailQuery.data.script.scriptId}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    Revision {scriptDetailQuery.data.script.activeRevision || "n/a"} · Hash{" "}
                    {scriptDetailQuery.data.script.activeSourceHash || "n/a"}
                  </Typography.Text>
                </Space>
              </AevatarPanel>
              <AevatarPanel title="Source Snapshot">
                <pre style={codeBlockStyle}>
                  {scriptDetailQuery.data.source?.sourceText || "No script source available."}
                </pre>
              </AevatarPanel>
            </>
          ) : (
            <AevatarInspectorEmpty description="Script detail is not available yet." />
          )
        ) : (
          <AevatarInspectorEmpty description="No scope detail is available." />
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

const MetricCard: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      background: "var(--ant-color-fill-quaternary)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
      overflow: "hidden",
      padding: 12,
    }}
  >
    <Typography.Text
      style={{ color: "var(--ant-color-text-secondary)", fontWeight: 500 }}
    >
      {label}
    </Typography.Text>
    <Typography.Text
      strong
      style={{
        display: "block",
        maxWidth: "100%",
        overflowWrap: "anywhere",
        whiteSpace: "normal",
        wordBreak: "break-word",
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const ScopeServiceCard: React.FC<{
  onOpenInvoke: () => void;
  onOpenServices: () => void;
  service: ServiceCatalogSnapshot;
}> = ({ onOpenInvoke, onOpenServices, service }) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 8,
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]}>
      <Typography.Text strong>
        {service.displayName || service.serviceId}
      </Typography.Text>
      <AevatarStatusTag
        domain="governance"
        status={service.deploymentStatus || "draft"}
      />
    </Space>
    <Typography.Text type="secondary">
      {service.endpoints.length} endpoints · Revision{" "}
      {service.activeServingRevisionId ||
        service.defaultServingRevisionId ||
        "n/a"}
    </Typography.Text>
    <Typography.Text type="secondary">
      Actor {service.primaryActorId || "n/a"} · Updated {formatDateTime(service.updatedAt)}
    </Typography.Text>
    <Space wrap>
      <Button onClick={onOpenInvoke} type="primary">
        Open invoke
      </Button>
      <Button onClick={onOpenServices}>Open services</Button>
    </Space>
  </div>
);

const RevisionCard: React.FC<{
  activating: boolean;
  canActivate: boolean;
  canRetire: boolean;
  onActivate: () => void;
  onInspect: () => void;
  onRetire: () => void;
  revision: StudioScopeBindingRevision;
  retiring: boolean;
}> = ({
  activating,
  canActivate,
  canRetire,
  onActivate,
  onInspect,
  onRetire,
  revision,
  retiring,
}) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 12,
      display: "flex",
      flexDirection: "column",
      gap: 8,
      minWidth: 0,
      overflow: "hidden",
      padding: 12,
    }}
  >
    <Space wrap size={[8, 8]} style={{ minWidth: 0 }}>
      <Typography.Text
        strong
        style={{
          maxWidth: "100%",
          overflowWrap: "anywhere",
          whiteSpace: "normal",
          wordBreak: "break-word",
        }}
      >
        {revision.revisionId}
      </Typography.Text>
      <AevatarStatusTag
        domain="governance"
        status={revision.servingState || revision.status}
      />
      {revision.isActiveServing ? <AevatarStatusTag domain="run" status="active" label="Active serving" /> : null}
      {revision.isDefaultServing ? <AevatarStatusTag domain="asset" status="active" label="Default" /> : null}
    </Space>
    <Typography.Text
      style={{
        color: "var(--ant-color-text-secondary)",
        display: "block",
        maxWidth: "100%",
        overflowWrap: "anywhere",
        whiteSpace: "normal",
        wordBreak: "break-word",
      }}
    >
      {formatStudioScopeBindingImplementationKind(revision.implementationKind)} ·{" "}
      {describeStudioScopeBindingRevisionTarget(revision)}
    </Typography.Text>
    {describeStudioScopeBindingRevisionContext(revision) ? (
      <Typography.Text
        style={{
          color: "var(--ant-color-text-secondary)",
          display: "block",
          maxWidth: "100%",
          overflowWrap: "anywhere",
          whiteSpace: "normal",
          wordBreak: "break-word",
        }}
      >
        {describeStudioScopeBindingRevisionContext(revision)}
      </Typography.Text>
    ) : null}
    <Typography.Text
      style={{
        color: "var(--ant-color-text-secondary)",
        display: "block",
        maxWidth: "100%",
        overflowWrap: "anywhere",
        whiteSpace: "normal",
        wordBreak: "break-word",
      }}
    >
      Actor {revision.primaryActorId || revision.staticPreferredActorId || "n/a"} · Deployment{" "}
      {revision.deploymentId || "draft"}
    </Typography.Text>
    <Space wrap>
      <Button icon={<EyeOutlined />} onClick={onInspect}>
        Inspect
      </Button>
      <Button
        disabled={!canActivate}
        loading={activating}
        onClick={onActivate}
        type="primary"
      >
        Activate
      </Button>
      <Button
        danger
        disabled={!canRetire}
        loading={retiring}
        onClick={onRetire}
      >
        Retire
      </Button>
    </Space>
  </div>
);

const codeBlockStyle: React.CSSProperties = {
  background: "var(--ant-color-fill-quaternary)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  margin: 0,
  maxHeight: 360,
  overflow: "auto",
  padding: 12,
  whiteSpace: "pre-wrap",
};

export default ScopeOverviewPage;
