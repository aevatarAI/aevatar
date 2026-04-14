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
import { buildTeamDetailHref } from "@/shared/navigation/teamRoutes";
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
      buildScopeHref("/teams", activeDraft, {
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
            查看
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
            事件流
          </Button>,
        ],
      },
      description: {
        render: (_, workflow) =>
          workflow.serviceKey
            ? `入口 ${workflow.serviceKey}`
            : "该行为定义还没有发布为团队默认入口。",
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
            查看
          </Button>,
          <Button
            icon={<CodeOutlined />}
            key={`${script.scriptId}-studio`}
            onClick={() =>
              history.push(
                buildStudioScriptsWorkspaceRoute({
                  scopeId,
                  scopeLabel: scopeId,
                  scriptId: script.scriptId,
                }),
              )
            }
            type="link"
          >
            打开脚本行为
          </Button>,
        ],
      },
      description: {
        render: (_, script) =>
          script.activeSourceHash
            ? `源码哈希 ${script.activeSourceHash}`
            : "该脚本行为正在等待已提交的源码哈希。",
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
      title="我的团队"
      titleHelp="当前团队首页继续复用 scope 级状态板，但会用 Team 语义组织默认绑定、成员资产和下一步动作。"
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title="团队范围"
              titleHelp="这里先锁定当前 Team（Scope），后续成员、事件流和高级编辑都会沿用这个上下文。"
            >
              <ScopeQueryCard
                activeScopeId={scopeId}
                draft={draft}
                loadLabel="加载团队状态"
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
                resetDisabled={
                  normalizeScopeDraft(draft).scopeId ===
                    (resolvedScope?.scopeId?.trim() ?? "") &&
                  scopeId === (resolvedScope?.scopeId?.trim() ?? "") &&
                  focus == null
                }
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

            {authSessionQuery.isLoading ? (
              <Alert
                showIcon
                title="正在解析当前会话的团队上下文。"
                type="info"
              />
            ) : null}

            {authSessionQuery.isError ? (
              <Alert
                description={
                  authSessionQuery.error instanceof Error
                    ? authSessionQuery.error.message
                    : "当前会话团队解析失败。"
                }
                showIcon
                title="当前会话团队解析失败，仍可手动输入 scopeId。"
                type="warning"
              />
            ) : null}

            <AevatarPanel title="团队操作">
              <Space direction="vertical" size={8} style={{ width: "100%" }}>
                <Button
                  disabled={!scopeId}
                  onClick={() =>
                    history.push(
                      scopeId
                        ? buildTeamDetailHref({
                            scopeId,
                          })
                        : "/teams",
                    )
                  }
                  type="primary"
                >
                  打开团队详情
                </Button>
                <Button
                  disabled={!scopeId}
                  onClick={() =>
                    history.push(
                      scopeId
                        ? buildTeamDetailHref({
                            scopeId,
                            tab: "advanced",
                          })
                        : "/teams",
                    )
                  }
                >
                  打开高级编辑
                </Button>
                <Button onClick={() => history.push(buildScopeHref("/scopes/assets", activeDraft))}>
                  打开团队资产
                </Button>
                <Button onClick={() => history.push(buildScopeHref("/scopes/invoke", activeDraft, {
                  serviceId: binding?.serviceId ?? "",
                }))}>
                  打开测试入口
                </Button>
                <Button
                  onClick={() =>
                    history.push(
                      buildRuntimeGAgentsHref({
                        scopeId,
                        actorId: activeRevision?.primaryActorId || undefined,
                        actorTypeName: activeRevision?.staticActorTypeName || undefined,
                      }),
                    )
                  }
                >
                  管理成员
                </Button>
                <Button
                  onClick={() =>
                    history.push(
                      buildStudioWorkflowWorkspaceRoute({
                        scopeId,
                        scopeLabel: scopeId,
                      }),
                    )
                  }
                >
                  打开行为定义
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
                  打开平台服务
                </Button>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            {!scopeId ? (
              <Alert
                title="选择一个团队以查看当前默认成员、版本发布和已拥有资产。"
                showIcon
                type="info"
              />
            ) : null}

            {scopeId && bindingQuery.isLoading ? (
              <Alert
                showIcon
                title="正在加载团队状态。"
                type="info"
              />
            ) : null}

            {scopeId && bindingQuery.isError ? (
              <Alert
                description={
                  bindingQuery.error instanceof Error
                    ? bindingQuery.error.message
                    : "加载团队绑定失败。"
                }
                showIcon
                title="加载团队状态失败。"
                type="error"
              />
            ) : null}

            {scopeId ? (
              <>
                <AevatarPanel
                  title="团队状态"
                  titleHelp="团队首页会把默认入口、成员资产和是否可测试集中展示，不再暴露 project 术语。"
                >
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                    }}
                  >
                    <MetricCard label="团队" value={scopeId} />
                    <MetricCard
                      label="默认成员"
                      value={binding?.available ? binding.displayName || binding.serviceId : "Not bound"}
                    />
                    <MetricCard
                      label="绑定目标"
                      value={currentBindingTarget}
                    />
                    <MetricCard
                      label="实现类型"
                      value={formatStudioScopeBindingImplementationKind(
                        activeRevision?.implementationKind,
                      )}
                    />
                    <MetricCard
                      label="行为定义"
                      value={workflowsQuery.data?.length ?? 0}
                    />
                    <MetricCard label="脚本行为" value={scriptsQuery.data?.length ?? 0} />
                    <MetricCard
                      label="成员服务"
                      value={scopeServicesQuery.data?.length ?? 0}
                    />
                    <MetricCard
                      label="发布状态"
                      value={binding?.deploymentStatus || "draft"}
                    />
                  </div>
                </AevatarPanel>

                <AevatarPanel
                  title="当前默认成员"
                  titleHelp="这里展示当前团队默认服务绑定到的成员实现，便于继续进入事件流或高级编辑。"
                >
                  {!binding?.available || !activeRevision ? (
                    <Alert
                      title="当前团队还没有可用的默认成员绑定。"
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
                        <MetricCard label="成员" value={binding.displayName || binding.serviceId} />
                        <MetricCard label="目标" value={currentBindingTarget} />
                        <MetricCard
                          label="版本"
                          value={activeRevision.revisionId}
                        />
                        <MetricCard
                          label="实例"
                          value={currentBindingActor || "n/a"}
                        />
                      </div>
                      {currentBindingContext ? (
                      <Alert
                        description={currentBindingContext}
                        showIcon
                        title="绑定详情"
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
                                  activeRevision.primaryActorId || undefined,
                                actorTypeName:
                                  activeRevision.staticActorTypeName || undefined,
                              }),
                            )
                          }
                        >
                          在成员页查看
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
                          打开测试入口
                        </Button>
                      </Space>
                    </>
                  )}
                </AevatarPanel>

                <AevatarPanel
                  title="版本发布"
                  titleHelp="团队默认入口的历史版本仍然保留在这里，方便继续切换或退役。"
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
                      description="当前团队还没有可切换的版本。"
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
                    title="已发布成员"
                    titleHelp="当前团队下已发布的 service 会在这里展示，并作为成员列表和连接器聚合的基础。"
                  >
                    {scopeServicesQuery.error ? (
                      <Alert
                        showIcon
                        title={
                          scopeServicesQuery.error instanceof Error
                            ? scopeServicesQuery.error.message
                            : "加载已发布成员失败。"
                        }
                        type="error"
                      />
                    ) : scopeServicesQuery.isLoading ? (
                      <AevatarInspectorEmpty description="正在加载已发布成员。" />
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
                        description="当前团队还没有已发布成员。"
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                      />
                    )}
                  </AevatarPanel>

                  <AevatarPanel title="行为定义资产">
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
                            description="当前团队还没有行为定义资产。"
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

                  <AevatarPanel title="脚本行为资产">
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
                            description="当前团队还没有脚本行为资产。"
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
        subtitle="团队检查器"
        title={
          focus?.kind === "revision"
            ? focusedRevision?.revisionId || focus?.id || "版本"
            : focus?.id || "团队详情"
        }
      >
        {!focus ? (
          <AevatarInspectorEmpty description="选择一个版本、行为定义或脚本行为来查看它在当前团队中的作用。" />
        ) : focus.kind === "revision" && focusedRevision ? (
          <AevatarPanel
            title="版本快照"
            titleHelp="Revision posture, rollout identity, and activation affordance stay in one place."
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <MetricCard label="版本" value={focusedRevision.revisionId} />
              <MetricCard
                label="实现类型"
                value={formatStudioScopeBindingImplementationKind(
                  focusedRevision.implementationKind,
                )}
              />
              <MetricCard
                label="目标"
                value={describeStudioScopeBindingRevisionTarget(focusedRevision)}
              />
              <MetricCard label="发布状态" value={focusedRevision.servingState || focusedRevision.status} />
              <MetricCard
                label="实例"
                value={focusedRevision.primaryActorId || "n/a"}
              />
            </div>
            {describeStudioScopeBindingRevisionContext(focusedRevision) ? (
              <Alert
                description={describeStudioScopeBindingRevisionContext(
                  focusedRevision,
                )}
                showIcon
                title="绑定详情"
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
                        focusedRevision.primaryActorId || undefined,
                      actorTypeName:
                        focusedRevision.staticActorTypeName || undefined,
                    }),
                  )
                }
              >
                在成员页查看
              </Button>
            </Space>
          </AevatarPanel>
        ) : focus.kind === "workflow" ? (
          workflowDetailQuery.data?.available && workflowDetailQuery.data.workflow ? (
            <>
              <AevatarPanel title="行为定义资产">
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
                    {workflowDetailQuery.data.workflow.serviceKey || "当前还没有已发布入口"}
                  </Typography.Text>
                </Space>
              </AevatarPanel>
              <AevatarPanel title="行为定义源码">
                <pre style={codeBlockStyle}>
                  {workflowDetailQuery.data.source?.workflowYaml || "当前还没有可查看的行为定义 YAML。"}
                </pre>
              </AevatarPanel>
            </>
          ) : (
            <AevatarInspectorEmpty description="当前还没有可查看的行为定义详情。" />
          )
        ) : focus.kind === "script" ? (
          scriptDetailQuery.data?.available && scriptDetailQuery.data.script ? (
            <>
              <AevatarPanel title="脚本行为资产">
                <Space direction="vertical" size={8}>
                  <AevatarStatusTag
                    domain="asset"
                    status={scriptDetailQuery.data.script.activeRevision ? "active" : "draft"}
                  />
                  <Typography.Text strong>
                    {scriptDetailQuery.data.script.scriptId}
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    版本 {scriptDetailQuery.data.script.activeRevision || "n/a"} · 哈希{" "}
                    {scriptDetailQuery.data.script.activeSourceHash || "n/a"}
                  </Typography.Text>
                </Space>
              </AevatarPanel>
              <AevatarPanel title="源码快照">
                <pre style={codeBlockStyle}>
                  {scriptDetailQuery.data.source?.sourceText || "当前还没有可查看的脚本源码。"}
                </pre>
              </AevatarPanel>
            </>
          ) : (
            <AevatarInspectorEmpty description="当前还没有可查看的脚本行为详情。" />
          )
        ) : (
          <AevatarInspectorEmpty description="当前还没有可查看的团队详情。" />
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
      {service.endpoints.length} 个端点 · 版本{" "}
      {service.activeServingRevisionId ||
        service.defaultServingRevisionId ||
        "n/a"}
    </Typography.Text>
    <Typography.Text type="secondary">
      实例 {service.primaryActorId || "n/a"} · 更新时间 {formatDateTime(service.updatedAt)}
    </Typography.Text>
    <Space wrap>
      <Button onClick={onOpenInvoke} type="primary">
        打开测试入口
      </Button>
      <Button onClick={onOpenServices}>打开平台服务</Button>
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
      实例 {revision.primaryActorId || "n/a"} · 部署{" "}
      {revision.deploymentId || "draft"}
    </Typography.Text>
    <Space wrap>
      <Button icon={<EyeOutlined />} onClick={onInspect}>
        查看
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
