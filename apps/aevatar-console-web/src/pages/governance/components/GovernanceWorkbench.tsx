import {
  ApiOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  LinkOutlined,
  PlusOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import type { ProListMetas } from "@ant-design/pro-components";
import { ProList } from "@ant-design/pro-components";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Space,
  Tag,
  Tabs,
  Typography,
  theme,
} from "antd";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import { governanceApi } from "@/shared/api/governanceApi";
import { servicesApi } from "@/shared/api/servicesApi";
import { history } from "@/shared/navigation/history";
import { resolveStudioScopeContext } from "@/shared/scope/context";
import { studioApi } from "@/shared/studio/api";
import type {
  ActivationCapabilityView,
  GovernanceIdentityInput,
  ServiceBindingSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicyInput,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import type {
  ServiceCatalogSnapshot,
  ServiceRevisionSnapshot,
} from "@/shared/models/services";
import {
  AEVATAR_GLOBAL_UI_SPEC,
  buildAevatarMetricCardStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  buildAevatarViewportStyle,
  formatAevatarStatusLabel,
  resolveAevatarMetricVisual,
  resolveAevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import ConsoleMetricCard from "@/shared/ui/ConsoleMetricCard";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { describeError } from "@/shared/ui/errorText";
import GovernanceAuditTimeline, {
  type GovernanceAuditEvent,
} from "./GovernanceAuditTimeline";
import GovernanceContextPanel from "./GovernanceContextPanel";
import GovernanceInspectorDrawer, {
  type GovernanceInspectorTarget,
} from "./GovernanceInspectorDrawer";
import GovernanceQueryCard from "./GovernanceQueryCard";
import type { GovernanceRevisionOption } from "./GovernanceQueryCard";
import { GovernanceSelectionNotice } from "./GovernanceResultPanels";
import {
  applyGovernanceServiceSelection,
  buildGovernanceWorkbenchHref,
  buildGovernanceServiceOptions,
  type GovernanceWorkbenchView,
  hasGovernanceScope,
  normalizeGovernanceDraft,
  normalizeGovernanceQuery,
  readGovernanceDraft,
  readGovernanceWorkbenchView,
  type GovernanceDraft,
} from "./governanceQuery";

type GovernanceNotice = {
  message: string;
  tone: "error" | "info" | "success" | "warning";
};

type GovernanceViewMeta = {
  path: string;
  title: string;
};

const defaultScopeServiceAppId = "default";
const defaultScopeServiceNamespace = "default";

type GovernanceViewActionConfig = {
  label: string;
  icon?: React.ReactNode;
  onClick: () => void;
  type?: "default" | "primary";
};

const governanceViewMeta: Record<GovernanceWorkbenchView, GovernanceViewMeta> = {
  activation: {
    path: "/governance/activation",
    title: "版本激活",
  },
  audit: {
    path: "/governance",
    title: "变更记录",
  },
  bindings: {
    path: "/governance/bindings",
    title: "绑定",
  },
  endpoints: {
    path: "/governance/endpoints",
    title: "入口",
  },
  policies: {
    path: "/governance/policies",
    title: "策略",
  },
};

function pickPreferredRevision(
  revisions: readonly ServiceRevisionSnapshot[],
): string {
  const preferredRevision = revisions.find((revision) =>
    ["published", "ready", "active"].some((status) =>
      revision.status.toLowerCase().includes(status),
    ),
  );

  return preferredRevision?.revisionId ?? revisions[0]?.revisionId ?? "";
}

function matchSelectedService(
  services: readonly ServiceCatalogSnapshot[],
  draft: GovernanceDraft,
): ServiceCatalogSnapshot | null {
  const normalizedServiceId = draft.serviceId.trim();
  if (!normalizedServiceId) {
    return null;
  }

  const exactMatch = services.find(
    (service) =>
      service.serviceId === normalizedServiceId &&
      service.tenantId === draft.tenantId.trim() &&
      service.namespace === draft.namespace.trim() &&
      (!draft.appId.trim() || service.appId === draft.appId.trim()),
  );

  if (exactMatch) {
    return exactMatch;
  }

  return services.find((service) => service.serviceId === normalizedServiceId) ?? null;
}

function buildGovernanceIdentity(
  draft: GovernanceDraft,
): GovernanceIdentityInput | null {
  if (!hasGovernanceScope(draft) || !draft.serviceId.trim()) {
    return null;
  }

  return {
    tenantId: draft.tenantId.trim(),
    appId: draft.appId.trim(),
    namespace: draft.namespace.trim(),
  };
}

function buildBlankPolicy(): ServicePolicySnapshot {
  return {
    activationRequiredBindingIds: [],
    displayName: "",
    invokeAllowedCallerServiceKeys: [],
    invokeRequiresActiveDeployment: false,
    policyId: "",
    retired: false,
  };
}

function openDrawerButton(
  key: string,
  label: string,
  onClick: () => void,
  icon?: React.ReactNode,
) {
  return (
    <Button icon={icon} key={key} onClick={onClick} size="small">
      {label}
    </Button>
  );
}

function buildBindingTargetLabel(record: ServiceBindingSnapshot): string {
  if (record.serviceRef) {
    return `${record.serviceRef.identity.serviceId}:${record.serviceRef.endpointId || "*"}`;
  }

  if (record.connectorRef) {
    return `${record.connectorRef.connectorType}:${record.connectorRef.connectorId}`;
  }

  if (record.secretRef) {
    return record.secretRef.secretName;
  }

  return "暂无";
}

function buildPolicySummary(record: ServicePolicySnapshot): string {
  const segments: string[] = [];

  if (record.activationRequiredBindingIds.length > 0) {
    segments.push(
      `依赖 ${record.activationRequiredBindingIds.length} 个激活绑定`,
    );
  }

  if (record.invokeAllowedCallerServiceKeys.length > 0) {
    segments.push(
      `${record.invokeAllowedCallerServiceKeys.length} 个调用白名单`,
    );
  }

  if (record.invokeRequiresActiveDeployment) {
    segments.push("仅允许已激活部署调用");
  }

  return segments.join(" · ") || "暂无激活或调用限制。";
}

function buildEndpointSummary(record: ServiceEndpointExposureSnapshot): string {
  const segments = [
    record.requestTypeUrl || "暂无请求契约",
    record.policyIds.length > 0
      ? `${record.policyIds.length} 条关联策略`
      : "暂无关联策略",
  ];

  return segments.join(" · ");
}

function buildAuditEvents(input: {
  activationView: ActivationCapabilityView | undefined;
  bindings: ServiceBindingSnapshot[];
  bindingsUpdatedAt?: string;
  endpoints: ServiceEndpointExposureSnapshot[];
  endpointsUpdatedAt?: string;
  policies: ServicePolicySnapshot[];
  policiesUpdatedAt?: string;
  revisions: ServiceRevisionSnapshot[];
  selectedService: ServiceCatalogSnapshot | null;
}): GovernanceAuditEvent[] {
  const {
    activationView,
    bindings,
    bindingsUpdatedAt,
    endpoints,
    endpointsUpdatedAt,
    policies,
    policiesUpdatedAt,
    revisions,
    selectedService,
  } = input;

  const events: GovernanceAuditEvent[] = [];

  if (selectedService) {
    events.push({
      action: "治理范围已挂接",
      actor: "服务注册表",
      at: selectedService.updatedAt,
      id: `service-${selectedService.serviceId}-${selectedService.updatedAt}`,
      status: selectedService.deploymentStatus || "active",
      summary: `治理上下文已切换到 ${selectedService.displayName || selectedService.serviceId}。`,
      targetId: selectedService.serviceId,
      targetKind: "service",
      targetLabel: selectedService.displayName || selectedService.serviceId,
    });
  }

  for (const revision of revisions) {
    if (revision.publishedAt) {
      events.push({
        action: "Revision published",
        actor: "发布管理器",
        at: revision.publishedAt,
        id: `revision-published-${revision.revisionId}`,
        status: "published",
        summary: `版本 ${revision.revisionId} 已发布，可进入治理校验。`,
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: revision.revisionId,
      });
    } else if (revision.preparedAt) {
      events.push({
        action: "Revision prepared",
        actor: "发布管理器",
        at: revision.preparedAt,
        id: `revision-prepared-${revision.revisionId}`,
        status: revision.status || "pending",
        summary: `版本 ${revision.revisionId} 已准备完成，等待激活决策。`,
        targetId: revision.revisionId,
        targetKind: "activation",
        targetLabel: revision.revisionId,
      });
    }
  }

  if (bindingsUpdatedAt) {
    events.push({
      action: "Binding catalog synchronized",
      actor: "绑定注册表",
      at: bindingsUpdatedAt,
      id: `binding-catalog-${bindingsUpdatedAt}`,
      status: bindings.some((binding) => binding.retired) ? "retired" : "active",
      summary: `当前服务已记录 ${bindings.length} 条绑定。`,
      targetId: "binding-catalog",
      targetKind: "binding",
      targetLabel: `${bindings.length} bindings`,
    });
  }

  for (const binding of bindings.filter((item) => item.retired)) {
    events.push({
      action: "Binding retired",
      actor: "绑定注册表",
      at: bindingsUpdatedAt || selectedService?.updatedAt || "",
      id: `binding-retired-${binding.bindingId}`,
      status: "retired",
      summary: `${binding.displayName || binding.bindingId} 已从当前依赖面下线。`,
      targetId: binding.bindingId,
      targetKind: "binding",
      targetLabel: binding.displayName || binding.bindingId,
    });
  }

  if (policiesUpdatedAt) {
    events.push({
      action: "Policy catalog synchronized",
      actor: "策略引擎",
      at: policiesUpdatedAt,
      id: `policy-catalog-${policiesUpdatedAt}`,
      status: policies.some((policy) => policy.retired) ? "retired" : "active",
      summary: `当前服务已物化 ${policies.length} 条治理策略。`,
      targetId: "policy-catalog",
      targetKind: "policy",
      targetLabel: `${policies.length} policies`,
    });
  }

  for (const policy of policies.filter(
    (item) =>
      item.retired ||
      item.invokeRequiresActiveDeployment ||
      item.activationRequiredBindingIds.length > 0,
  )) {
    events.push({
      action: policy.retired ? "策略已下线" : "策略已生效",
      actor: "策略引擎",
      at: policiesUpdatedAt || selectedService?.updatedAt || "",
      id: `policy-${policy.policyId}-${policy.retired ? "retired" : "enforced"}`,
      status: policy.retired ? "retired" : "active",
      summary: buildPolicySummary(policy),
      targetId: policy.policyId,
      targetKind: "policy",
      targetLabel: policy.displayName || policy.policyId,
    });
  }

  if (endpointsUpdatedAt) {
    events.push({
      action: "Endpoint catalog synchronized",
      actor: "入口暴露控制器",
      at: endpointsUpdatedAt,
      id: `endpoint-catalog-${endpointsUpdatedAt}`,
      status: endpoints.some((endpoint) => endpoint.exposureKind === "disabled")
        ? "disabled"
        : "active",
      summary: `当前服务有 ${endpoints.length} 个入口受治理暴露策略控制。`,
      targetId: "endpoint-catalog",
      targetKind: "endpoint",
      targetLabel: `${endpoints.length} endpoints`,
    });
  }

  for (const endpoint of endpoints.filter(
    (item) =>
      item.exposureKind === "public" || item.exposureKind === "disabled",
  )) {
    events.push({
      action:
        endpoint.exposureKind === "public"
          ? "入口已公开"
          : "入口已停用",
      actor: "入口暴露控制器",
      at: endpointsUpdatedAt || selectedService?.updatedAt || "",
      id: `endpoint-${endpoint.endpointId}-${endpoint.exposureKind}`,
      status: endpoint.exposureKind,
      summary: buildEndpointSummary(endpoint),
      targetId: endpoint.endpointId,
      targetKind: "endpoint",
      targetLabel: endpoint.displayName || endpoint.endpointId,
    });
  }

  if (activationView) {
    events.push({
      action:
        activationView.missingPolicyIds.length > 0
          ? "激活被阻塞"
          : "激活校验通过",
      actor: "激活守卫",
      at: selectedService?.updatedAt || policiesUpdatedAt || endpointsUpdatedAt || "",
      id: `activation-${activationView.revisionId || "unresolved"}`,
      status:
        activationView.missingPolicyIds.length > 0 ? "blocked" : "ready",
      summary:
        activationView.missingPolicyIds.length > 0
          ? `版本 ${activationView.revisionId || "未解析"} 缺少策略：${activationView.missingPolicyIds.join(", ")}。`
          : `版本 ${activationView.revisionId || "未解析"} 的治理配置已完整。`,
      targetId: activationView.revisionId || "activation",
      targetKind: "activation",
      targetLabel: activationView.revisionId || "激活视图",
    });
  }

  return events
    .filter((event) => event.at.trim().length > 0)
    .sort(
      (left, right) =>
        new Date(right.at).getTime() - new Date(left.at).getTime(),
    );
}

const WorkbenchStatusTag: React.FC<{
  status: string;
}> = ({ status }) => {
  const { token } = theme.useToken();

  return (
    <span
      style={buildAevatarTagStyle(
        token as AevatarThemeSurfaceToken,
        "governance",
        status,
      )}
    >
      {formatAevatarStatusLabel(status)}
    </span>
  );
};

const GovernanceWorkbench: React.FC = () => {
  const locationSearch = React.useSyncExternalStore(
    (listener) => {
      if (typeof window === "undefined") {
        return () => undefined;
      }

      window.addEventListener("popstate", listener);
      return () => {
        window.removeEventListener("popstate", listener);
      };
    },
    () => (typeof window === "undefined" ? "" : window.location.search),
    () => "",
  );
  const view = useMemo(
    () => readGovernanceWorkbenchView(locationSearch),
    [locationSearch],
  );
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const queryClient = useQueryClient();

  const initialDraft = useMemo(() => readGovernanceDraft(), []);
  const [draft, setDraft] = useState<GovernanceDraft>(initialDraft);
  const [activeDraft, setActiveDraft] = useState<GovernanceDraft>(initialDraft);
  const [showContextPicker, setShowContextPicker] = useState(
    () =>
      initialDraft.serviceId.trim().length === 0 ||
      (view === "activation" && initialDraft.revisionId.trim().length === 0),
  );
  const [visitedViews, setVisitedViews] = useState<GovernanceWorkbenchView[]>(() => [
    view,
  ]);
  const [notice, setNotice] = useState<GovernanceNotice | null>(null);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [drawerTarget, setDrawerTarget] = useState<GovernanceInspectorTarget | null>(
    null,
  );
  const authSessionQuery = useQuery({
    queryKey: ["governance", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  const serviceQuery = useMemo(() => normalizeGovernanceQuery(draft), [draft]);
  const activeQuery = useMemo(
    () => normalizeGovernanceQuery(activeDraft),
    [activeDraft],
  );
  const serviceSearchEnabled = useMemo(() => hasGovernanceScope(draft), [draft]);
  const activeIdentity = useMemo(
    () => buildGovernanceIdentity(activeDraft),
    [activeDraft],
  );
  const hasSelectedServiceContext = Boolean(
    activeIdentity && activeDraft.serviceId.trim(),
  );

  const servicesQuery = useQuery({
    enabled: serviceSearchEnabled,
    queryFn: () => servicesApi.listServices({ ...serviceQuery, take: 200 }),
    queryKey: ["governance", "services", serviceQuery],
  });

  const selectedService = useMemo(
    () => matchSelectedService(servicesQuery.data ?? [], activeDraft),
    [activeDraft, servicesQuery.data],
  );

  const bindingsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => governanceApi.getBindings(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "bindings", activeDraft.serviceId, activeQuery],
  });

  const policiesQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => governanceApi.getPolicies(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "policies", activeDraft.serviceId, activeQuery],
  });

  const endpointsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () =>
      governanceApi.getEndpointCatalog(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "endpoints", activeDraft.serviceId, activeQuery],
  });

  const revisionsQuery = useQuery({
    enabled: hasSelectedServiceContext,
    queryFn: () => servicesApi.getRevisions(activeDraft.serviceId, activeQuery),
    queryKey: ["governance", "revisions", activeDraft.serviceId, activeQuery],
  });

  const preferredRevisionId = useMemo(
    () => pickPreferredRevision(revisionsQuery.data?.revisions ?? []),
    [revisionsQuery.data],
  );

  const activationRevisionId =
    view === "activation"
      ? activeDraft.revisionId.trim()
      : activeDraft.revisionId.trim() || preferredRevisionId;

  const activationQuery = useQuery({
    enabled: hasSelectedServiceContext && activationRevisionId.length > 0,
    queryFn: () =>
      governanceApi.getActivationCapability(activeDraft.serviceId, {
        ...activeQuery,
        revisionId: activationRevisionId,
      }),
    queryKey: [
      "governance",
      "activation",
      activeDraft.serviceId,
      activeQuery,
      activationRevisionId,
    ],
  });

  const serviceOptions = useMemo(
    () => buildGovernanceServiceOptions(servicesQuery.data ?? []),
    [servicesQuery.data],
  );

  const revisionOptions = useMemo<GovernanceRevisionOption[]>(
    () =>
      (revisionsQuery.data?.revisions ?? []).map((revision) => ({
        label: `${revision.revisionId} · ${revision.status}`,
        value: revision.revisionId,
      })),
    [revisionsQuery.data],
  );

  useEffect(() => {
    const scopeId = resolvedScope?.scopeId?.trim();
    if (!scopeId) {
      return;
    }

    if (
      draft.tenantId.trim() ||
      draft.appId.trim() ||
      draft.namespace.trim()
    ) {
      return;
    }

    const nextDraft = {
      ...draft,
      appId: defaultScopeServiceAppId,
      namespace: defaultScopeServiceNamespace,
      tenantId: scopeId,
    };
    setDraft(nextDraft);
    if (
      !activeDraft.tenantId.trim() &&
      !activeDraft.appId.trim() &&
      !activeDraft.namespace.trim() &&
      !activeDraft.serviceId.trim()
    ) {
      setActiveDraft(nextDraft);
    }
  }, [activeDraft, draft, resolvedScope?.scopeId]);

  useEffect(() => {
    if (!serviceOptions.length || activeDraft.serviceId.trim()) {
      return;
    }

    const nextDraft = applyGovernanceServiceSelection(
      {
        ...draft,
        appId: draft.appId.trim() || serviceOptions[0].appId,
        namespace: draft.namespace.trim() || serviceOptions[0].namespace,
        tenantId: draft.tenantId.trim() || serviceOptions[0].tenantId,
      },
      serviceOptions[0],
    );

    setDraft((currentDraft) =>
      currentDraft.serviceId.trim() ? currentDraft : nextDraft,
    );
    setActiveDraft((currentDraft) =>
      currentDraft.serviceId.trim() ? currentDraft : nextDraft,
    );
    setShowContextPicker(false);
    history.replace(buildGovernanceWorkbenchHref(nextDraft, view));
  }, [activeDraft.serviceId, draft, serviceOptions, view]);

  useEffect(() => {
    if (
      view !== "activation" ||
      !preferredRevisionId.trim() ||
      !activeDraft.serviceId.trim() ||
      activeDraft.revisionId.trim()
    ) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.serviceId.trim() && !currentDraft.revisionId.trim()
        ? {
            ...currentDraft,
            revisionId: preferredRevisionId,
          }
        : currentDraft,
    );
    setActiveDraft((currentDraft) =>
      currentDraft.serviceId.trim() && !currentDraft.revisionId.trim()
        ? {
            ...currentDraft,
            revisionId: preferredRevisionId,
          }
        : currentDraft,
    );
    setShowContextPicker(false);
  }, [
    activeDraft.revisionId,
    activeDraft.serviceId,
    preferredRevisionId,
    view,
  ]);

  useEffect(() => {
    if (view === "activation" && !activeDraft.revisionId.trim()) {
      setShowContextPicker(true);
    }
  }, [activeDraft.revisionId, view]);

  useEffect(() => {
    setVisitedViews((currentViews) =>
      currentViews.includes(view) ? currentViews : [...currentViews, view],
    );
  }, [view]);

  useEffect(() => {
    const revisions = revisionsQuery.data?.revisions ?? [];
    if (!draft.serviceId.trim() || revisions.length === 0) {
      return;
    }

    const currentRevisionId = draft.revisionId.trim();
    const revisionExists = revisions.some(
      (revision) => revision.revisionId === currentRevisionId,
    );
    if (currentRevisionId && revisionExists) {
      return;
    }

    const nextRevisionId = pickPreferredRevision(revisions);
    if (!nextRevisionId) {
      return;
    }

    setDraft((currentDraft) =>
      currentDraft.serviceId.trim() !== draft.serviceId.trim()
        ? currentDraft
        : {
            ...currentDraft,
            revisionId: nextRevisionId,
          },
    );
  }, [draft.revisionId, draft.serviceId, revisionsQuery.data]);

  const auditEvents = useMemo(
    () =>
      buildAuditEvents({
        activationView: activationQuery.data,
        bindings: bindingsQuery.data?.bindings ?? [],
        bindingsUpdatedAt: bindingsQuery.data?.updatedAt,
        endpoints: endpointsQuery.data?.endpoints ?? [],
        endpointsUpdatedAt: endpointsQuery.data?.updatedAt,
        policies: policiesQuery.data?.policies ?? [],
        policiesUpdatedAt: policiesQuery.data?.updatedAt,
        revisions: revisionsQuery.data?.revisions ?? [],
        selectedService,
      }),
    [
      activationQuery.data,
      bindingsQuery.data,
      endpointsQuery.data,
      policiesQuery.data,
      revisionsQuery.data,
      selectedService,
    ],
  );

  const summaryMetrics = useMemo(
    () => [
      {
        label: "策略",
        tone: "info" as const,
        value: String(policiesQuery.data?.policies.length ?? 0),
      },
      {
        label: "绑定",
        tone: "default" as const,
        value: String(bindingsQuery.data?.bindings.length ?? 0),
      },
      {
        label: "公开入口",
        tone: "success" as const,
        value: String(
          endpointsQuery.data?.endpoints.filter(
            (endpoint) => endpoint.exposureKind === "public",
          ).length ?? 0,
        ),
      },
      {
        label: "缺失策略",
        tone:
          (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
            ? ("warning" as const)
            : ("success" as const),
        value: String(activationQuery.data?.missingPolicyIds.length ?? 0),
      },
    ],
    [activationQuery.data, bindingsQuery.data, endpointsQuery.data, policiesQuery.data],
  );

  const governanceTabItems = useMemo(
    () =>
      Object.entries(governanceViewMeta).map(([key, meta]) => ({
        key,
        label: meta.title,
      })),
    [],
  );

  const governanceViewActions = useMemo<
    Partial<Record<GovernanceWorkbenchView, GovernanceViewActionConfig>>
  >(
    () => ({
      activation:
        activationQuery.data != null
          ? {
              icon: <DeploymentUnitOutlined />,
              label: "查看诊断",
              onClick: () =>
                setDrawerTarget({
                  kind: "activation",
                  record: activationQuery.data,
                }),
            }
          : undefined,
      policies: {
        icon: <PlusOutlined />,
        label: "新建策略",
        onClick: () =>
          setDrawerTarget({
            kind: "policy",
            mode: "create",
            record: buildBlankPolicy(),
          }),
        type: "primary",
      },
    }),
    [activationQuery.data],
  );

  const policyListMetas = useMemo<ProListMetas<ServicePolicySnapshot>>(
    () => ({
      actions: {
        render: (_, record) => [
          openDrawerButton(
            `policy-${record.policyId}`,
            "配置",
            () =>
              setDrawerTarget({
                kind: "policy",
                mode: "edit",
                record,
              }),
            <EyeOutlined />,
          ),
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
            <SafetyCertificateOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <Typography.Text>{buildPolicySummary(record)}</Typography.Text>
            <Space wrap size={[8, 8]}>
              {record.activationRequiredBindingIds.map((bindingId) => (
                <Tag key={bindingId}>{bindingId}</Tag>
              ))}
            </Space>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.policyId}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
            {record.invokeRequiresActiveDeployment ? (
              <Tag color="gold">部署受限</Tag>
            ) : null}
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Typography.Text strong>
            {record.displayName || record.policyId}
          </Typography.Text>
        ),
      },
    }),
    [surfaceToken],
  );

  const bindingListMetas = useMemo<ProListMetas<ServiceBindingSnapshot>>(
    () => ({
      actions: {
        render: (_, record) => [
          openDrawerButton(
            `binding-${record.bindingId}`,
            "查看",
            () =>
              setDrawerTarget({
                kind: "binding",
                record,
              }),
            <EyeOutlined />,
          ),
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
            <LinkOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <Typography.Text>{buildBindingTargetLabel(record)}</Typography.Text>
            <Space wrap size={[8, 8]}>
              {record.policyIds.map((policyId) => (
                <Tag key={policyId}>{policyId}</Tag>
              ))}
            </Space>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.bindingId}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag status={record.retired ? "retired" : "active"} />
            <Tag color="blue">{formatAevatarStatusLabel(record.bindingKind)}</Tag>
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Typography.Text strong>
            {record.displayName || record.bindingId}
          </Typography.Text>
        ),
      },
    }),
    [surfaceToken],
  );

  const endpointListMetas = useMemo<ProListMetas<ServiceEndpointExposureSnapshot>>(
    () => ({
      actions: {
        render: (_, record) => [
          openDrawerButton(
            `endpoint-${record.endpointId}`,
            "管理",
            () =>
              setDrawerTarget({
                kind: "endpoint",
                record,
              }),
            <EyeOutlined />,
          ),
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
            <ApiOutlined />
          </div>
        ),
      },
      content: {
        render: (_, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <Typography.Text>{buildEndpointSummary(record)}</Typography.Text>
            <Space wrap size={[8, 8]}>
              {record.policyIds.map((policyId) => (
                <Tag key={policyId}>{policyId}</Tag>
              ))}
            </Space>
          </div>
        ),
      },
      description: {
        render: (_, record) => (
          <Typography.Text style={{ color: surfaceToken.colorTextTertiary }}>
            {record.endpointId}
          </Typography.Text>
        ),
      },
      subTitle: {
        render: (_, record) => (
          <Space wrap size={[8, 8]}>
            <WorkbenchStatusTag status={record.exposureKind || "internal"} />
            <Tag color="blue">{formatAevatarStatusLabel(record.kind)}</Tag>
          </Space>
        ),
      },
      title: {
        render: (_, record) => (
          <Typography.Text strong>
            {record.displayName || record.endpointId}
          </Typography.Text>
        ),
      },
    }),
    [surfaceToken],
  );

  const invalidateGovernanceQueries = useCallback(async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["governance"] }),
      queryClient.invalidateQueries({ queryKey: ["services"] }),
    ]);
  }, [queryClient]);

  const runGovernanceAction = useCallback(
    async (
      action: string,
      successMessage: string,
      task: () => Promise<unknown>,
      closeDrawer = false,
    ) => {
      setBusyAction(action);
      try {
        await task();
        setNotice({
          message: successMessage,
          tone: resolveAevatarSemanticTone("governance", action).startsWith("error")
            ? "warning"
            : "success",
        });
        await invalidateGovernanceQueries();
        if (closeDrawer) {
          setDrawerTarget(null);
        }
      } catch (error) {
        setNotice({
          message: describeError(error, "治理操作失败。"),
          tone: "error",
        });
      } finally {
        setBusyAction(null);
      }
    },
    [invalidateGovernanceQueries],
  );

  const handleCreatePolicy = useCallback(
    async (input: ServicePolicyInput) => {
      await runGovernanceAction(
        "create-policy",
        `策略 ${input.policyId} 已提交创建。`,
        () => governanceApi.createPolicy(activeDraft.serviceId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleUpdatePolicy = useCallback(
    async (policyId: string, input: ServicePolicyInput) => {
      await runGovernanceAction(
        "save-policy",
        `策略 ${policyId} 已提交更新。`,
        () => governanceApi.updatePolicy(activeDraft.serviceId, policyId, input),
        true,
      );
    },
    [activeDraft.serviceId, runGovernanceAction],
  );

  const handleRetirePolicy = useCallback(
    async (policyId: string) => {
      if (!activeIdentity) {
        return;
      }

      await runGovernanceAction(
        "retire-policy",
        `策略 ${policyId} 已提交下线。`,
        () => governanceApi.retirePolicy(activeDraft.serviceId, policyId, activeIdentity),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, runGovernanceAction],
  );

  const handleRetireBinding = useCallback(
    async (bindingId: string) => {
      if (!activeIdentity) {
        return;
      }

      await runGovernanceAction(
        "retire-binding",
        `绑定 ${bindingId} 已提交下线。`,
        () =>
          governanceApi.retireBinding(
            activeDraft.serviceId,
            bindingId,
            activeIdentity,
          ),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, runGovernanceAction],
  );

  const handleSetEndpointExposure = useCallback(
    async (endpointId: string, exposureKind: string) => {
      if (!activeIdentity || !endpointsQuery.data) {
        return;
      }

      const payload = {
        ...activeIdentity,
        endpoints: endpointsQuery.data.endpoints.map((endpoint) =>
          endpoint.endpointId === endpointId
            ? {
                ...endpoint,
                exposureKind,
              }
            : endpoint,
        ),
      };

      await runGovernanceAction(
        `set-endpoint-exposure:${exposureKind}`,
        `入口 ${endpointId} 已提交为${formatAevatarStatusLabel(exposureKind)}。`,
        () => governanceApi.updateEndpointCatalog(activeDraft.serviceId, payload),
        true,
      );
    },
    [activeDraft.serviceId, activeIdentity, endpointsQuery.data, runGovernanceAction],
  );

  const openAuditEvent = useCallback(
    (event: GovernanceAuditEvent) => {
      if (event.targetKind === "policy") {
        const record = (policiesQuery.data?.policies ?? []).find(
          (policy) => policy.policyId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "policy",
            mode: "edit",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "binding") {
        const record = (bindingsQuery.data?.bindings ?? []).find(
          (binding) => binding.bindingId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "binding",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "endpoint") {
        const record = (endpointsQuery.data?.endpoints ?? []).find(
          (endpoint) => endpoint.endpointId === event.targetId,
        );
        if (record) {
          setDrawerTarget({
            kind: "endpoint",
            record,
          });
          return;
        }
      }

      if (event.targetKind === "activation" && activationQuery.data) {
        setDrawerTarget({
          kind: "activation",
          record: activationQuery.data,
        });
        return;
      }

      setDrawerTarget({
        kind: "audit",
        event,
      });
    },
    [
      activationQuery.data,
      bindingsQuery.data,
      endpointsQuery.data,
      policiesQuery.data,
    ],
  );

  const renderStageForView = useCallback((targetView: GovernanceWorkbenchView) => {
    if (!hasSelectedServiceContext) {
      return (
        <GovernanceSelectionNotice
          description="先在顶部选择一个服务，再查看策略、绑定、入口和变更记录。"
          highlights={[
            { label: "团队", value: activeDraft.tenantId || "待选择" },
            { label: "命名空间", value: activeDraft.namespace || "待选择" },
          ]}
          title="还没有选中服务"
        />
      );
    }

    if (targetView === "activation" && !activeDraft.revisionId.trim()) {
      return (
        <GovernanceSelectionNotice
          description="先选择一个版本，再查看激活能力、缺失策略和入口约束。"
          highlights={[
            { label: "服务", value: activeDraft.serviceId || "待选择" },
            { label: "版本", value: "待选择" },
          ]}
          title="还没有选中版本"
        />
      );
    }

    if (targetView === "audit") {
      return (
        <GovernanceAuditTimeline
          events={auditEvents}
          loading={
            bindingsQuery.isLoading ||
            policiesQuery.isLoading ||
            endpointsQuery.isLoading
          }
          onSelect={openAuditEvent}
        />
      );
    }

    if (targetView === "policies") {
      return (
        <ProList<ServicePolicySnapshot>
          dataSource={policiesQuery.data?.policies ?? []}
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
            emptyText: policiesQuery.isLoading ? (
              "加载中..."
            ) : (
              <GovernanceSelectionNotice
                description="当前服务还没有治理策略。"
                highlights={[
                  { label: "服务", value: activeDraft.serviceId || "待选择" },
                ]}
                title="还没有策略"
              />
            ),
          }}
          metas={policyListMetas}
          pagination={{ pageSize: 8, showSizeChanger: false }}
          rowKey="policyId"
          search={false}
          showActions="always"
          split={false}
          toolBarRender={false}
        />
      );
    }

    if (targetView === "bindings") {
      return (
        <ProList<ServiceBindingSnapshot>
          dataSource={bindingsQuery.data?.bindings ?? []}
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
            emptyText: bindingsQuery.isLoading ? (
              "加载中..."
            ) : (
              <GovernanceSelectionNotice
                description="当前服务还没有治理绑定。"
                highlights={[
                  { label: "服务", value: activeDraft.serviceId || "待选择" },
                ]}
                title="还没有绑定"
              />
            ),
          }}
          metas={bindingListMetas}
          pagination={{ pageSize: 8, showSizeChanger: false }}
          rowKey="bindingId"
          search={false}
          showActions="always"
          split={false}
          toolBarRender={false}
        />
      );
    }

    if (targetView === "endpoints") {
      return (
        <ProList<ServiceEndpointExposureSnapshot>
          dataSource={endpointsQuery.data?.endpoints ?? []}
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
            emptyText: endpointsQuery.isLoading ? (
              "加载中..."
            ) : (
              <GovernanceSelectionNotice
                description="当前服务还没有暴露入口。"
                highlights={[
                  { label: "服务", value: activeDraft.serviceId || "待选择" },
                ]}
                title="还没有入口"
              />
            ),
          }}
          metas={endpointListMetas}
          pagination={{ pageSize: 8, showSizeChanger: false }}
          rowKey="endpointId"
          search={false}
          showActions="always"
          split={false}
          toolBarRender={false}
        />
      );
    }

    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))",
          }}
        >
          {summaryMetrics.map((metric) => (
            <div
              key={metric.label}
              style={buildAevatarMetricCardStyle(surfaceToken, metric.tone)}
            >
              <Typography.Text
                style={{
                  color: resolveAevatarMetricVisual(surfaceToken, metric.tone)
                    .labelColor,
                }}
              >
                {metric.label}
              </Typography.Text>
              <Typography.Text
                strong
                style={{
                  color: resolveAevatarMetricVisual(surfaceToken, metric.tone)
                    .valueColor,
                }}
              >
                {metric.value}
              </Typography.Text>
            </div>
          ))}
        </div>

        <div
          style={{
            ...buildAevatarPanelStyle(surfaceToken, {
              background: surfaceToken.colorFillAlter,
              padding: 16,
            }),
            boxShadow: "none",
          }}
        >
          <Space direction="vertical" size={10} style={{ display: "flex" }}>
            <Typography.Text strong>
              版本 {activationRevisionId || "未解析"}
            </Typography.Text>
            <Space wrap size={[8, 8]}>
              <WorkbenchStatusTag
                status={
                  (activationQuery.data?.missingPolicyIds.length ?? 0) > 0
                    ? "blocked"
                    : "ready"
                }
              />
              {activationQuery.data?.missingPolicyIds.length ? (
                <Tag color="gold">
                  缺少 {activationQuery.data.missingPolicyIds.length} 条策略
                </Tag>
              ) : null}
            </Space>
          </Space>
        </div>

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
          }}
        >
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorBgContainer,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space direction="vertical" size={10} style={{ display: "flex" }}>
              <Typography.Text strong>缺失策略</Typography.Text>
              {activationQuery.data?.missingPolicyIds.length ? (
                activationQuery.data.missingPolicyIds.map((policyId) => (
                  <Tag key={policyId} color="gold">
                    {policyId}
                  </Tag>
                ))
              ) : (
                <Typography.Text type="secondary">暂无</Typography.Text>
              )}
            </Space>
          </div>

          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorBgContainer,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space direction="vertical" size={10} style={{ display: "flex" }}>
              <Typography.Text strong>当前绑定</Typography.Text>
              {(activationQuery.data?.bindings ?? []).length > 0 ? (
                activationQuery.data?.bindings.map((binding) => (
                  <button
                    key={binding.bindingId}
                    onClick={() =>
                      setDrawerTarget({
                        kind: "binding",
                        record: binding,
                      })
                    }
                    style={{
                      background: "transparent",
                      border: "none",
                      cursor: "pointer",
                      padding: 0,
                      textAlign: "left",
                    }}
                    type="button"
                  >
                    <Typography.Text>{binding.displayName || binding.bindingId}</Typography.Text>
                  </button>
                ))
              ) : (
                <Typography.Text type="secondary">暂无</Typography.Text>
              )}
            </Space>
          </div>

          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorBgContainer,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space direction="vertical" size={10} style={{ display: "flex" }}>
              <Typography.Text strong>当前入口</Typography.Text>
              {(activationQuery.data?.endpoints ?? []).length > 0 ? (
                activationQuery.data?.endpoints.map((endpoint) => (
                  <button
                    key={endpoint.endpointId}
                    onClick={() =>
                      setDrawerTarget({
                        kind: "endpoint",
                        record: endpoint,
                      })
                    }
                    style={{
                      background: "transparent",
                      border: "none",
                      cursor: "pointer",
                      padding: 0,
                      textAlign: "left",
                    }}
                    type="button"
                  >
                    <Typography.Text>{endpoint.displayName || endpoint.endpointId}</Typography.Text>
                  </button>
                ))
              ) : (
                <Typography.Text type="secondary">暂无</Typography.Text>
              )}
            </Space>
          </div>
        </div>
      </div>
    );
  }, [
    activeDraft.revisionId,
    activationQuery.data,
    activationRevisionId,
    auditEvents,
    bindingListMetas,
    bindingsQuery.data,
    bindingsQuery.isLoading,
    endpointListMetas,
    endpointsQuery.data,
    endpointsQuery.isLoading,
    hasSelectedServiceContext,
    openAuditEvent,
    policiesQuery.data,
    policiesQuery.isLoading,
    policyListMetas,
    summaryMetrics,
    surfaceToken,
  ]);

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Platform"
      title="Governance"
    >
      <div style={buildAevatarViewportStyle(surfaceToken)}>
        {notice ? (
          <Alert
            closable
            message={notice.message}
            showIcon
            type={notice.tone}
            onClose={() => setNotice(null)}
          />
        ) : null}

        <GovernanceQueryCard
          draft={draft}
          includeRevision={view === "activation"}
          loadLabel={
            view === "activation" ? "加载激活能力" : "加载治理信息"
          }
          onChange={setDraft}
          onLoad={() => {
            const nextActiveDraft = normalizeGovernanceDraft(draft);
            setDraft(nextActiveDraft);
            setActiveDraft(nextActiveDraft);
            setShowContextPicker(false);
            history.replace(
              buildGovernanceWorkbenchHref(nextActiveDraft, view),
            );
          }}
          onReset={() => {
            const nextDraft = resolvedScope?.scopeId?.trim()
              ? {
                  ...readGovernanceDraft(""),
                  appId: defaultScopeServiceAppId,
                  namespace: defaultScopeServiceNamespace,
                  tenantId: resolvedScope.scopeId.trim(),
                }
              : readGovernanceDraft("");
            setDraft(nextDraft);
            setActiveDraft(nextDraft);
            setShowContextPicker(true);
            history.replace(buildGovernanceWorkbenchHref(nextDraft, view));
          }}
          revisionOptions={revisionOptions}
          revisionOptionsLoading={revisionsQuery.isLoading}
          serviceOptions={serviceOptions}
          serviceSearchEnabled={serviceSearchEnabled}
        />

        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "repeat(4, minmax(0, 1fr))",
          }}
        >
          {summaryMetrics.map((metric) => (
            <ConsoleMetricCard
              key={metric.label}
              label={metric.label}
              tone={
                metric.tone === "success"
                  ? "green"
                  : metric.tone === "info"
                    ? "purple"
                    : "default"
              }
              value={metric.value}
            />
          ))}
        </div>

        <div
          style={{
            display: "grid",
            flex: 1,
            gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
            gridTemplateColumns: "minmax(280px, 320px) minmax(0, 1fr)",
            minHeight: 0,
          }}
        >
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              gap: AEVATAR_GLOBAL_UI_SPEC.tokens.sectionGap,
              minHeight: 0,
            }}
          >
            {hasSelectedServiceContext ? (
              <GovernanceContextPanel
                draft={activeDraft}
                includeRevision={Boolean(activeDraft.revisionId.trim())}
                onChangeService={() => {
                  setDraft(activeDraft);
                  setShowContextPicker(true);
                }}
              />
            ) : (
              <div
                style={{
                  ...buildAevatarPanelStyle(surfaceToken, {
                    background: surfaceToken.colorFillAlter,
                    padding: 16,
                  }),
                  boxShadow: "none",
                }}
              >
                <GovernanceSelectionNotice
                  description="先在顶部确认团队和服务，再进入策略、绑定和入口视图。"
                  highlights={[
                    { label: "团队", value: activeDraft.tenantId || "待选择" },
                    { label: "命名空间", value: activeDraft.namespace || "待选择" },
                  ]}
                  title="当前还没有服务上下文"
                />
              </div>
            )}
          </div>

          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorBgContainer,
              }),
              display: "flex",
              flexDirection: "column",
              minHeight: 0,
              position: "relative",
            }}
          >
            {(() => {
              const activeAction = governanceViewActions[view];

              return (
                <>
                  <div
                    style={{
                      borderBottom: `1px solid ${surfaceToken.colorBorderSecondary}`,
                      display: "flex",
                      flexDirection: "column",
                      gap: 12,
                      padding: "16px 18px 0",
                    }}
                  >
                    <div
                      style={{
                        alignItems: "stretch",
                        columnGap: 12,
                        display: "grid",
                        gridTemplateColumns: "minmax(0, 1fr) auto",
                        minHeight: 72,
                      }}
                    >
                      <Space direction="vertical" size={2} style={{ minWidth: 0 }}>
                        <Typography.Text
                          strong
                          style={{ color: surfaceToken.colorTextHeading, fontSize: 16 }}
                        >
                          {governanceViewMeta[view].title}
                        </Typography.Text>
                      </Space>
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          justifyContent: "flex-end",
                          minHeight: 32,
                          minWidth: 172,
                        }}
                      >
                        {activeAction ? (
                          <Button
                            icon={activeAction.icon}
                            onClick={activeAction.onClick}
                            type={activeAction.type}
                          >
                            {activeAction.label}
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    <Tabs
                      activeKey={view}
                      items={governanceTabItems}
                      onChange={(nextView) =>
                        history.replace(
                          buildGovernanceWorkbenchHref(
                            activeDraft,
                            nextView as GovernanceWorkbenchView,
                          ),
                        )
                      }
                    />
                  </div>

                  <div
                    style={{
                      flex: 1,
                      minHeight: 0,
                      overflow: "hidden",
                      position: "relative",
                    }}
                  >
                    {visitedViews.map((targetView) => (
                      <div
                        key={targetView}
                        style={{
                          inset: 0,
                          opacity: targetView === view ? 1 : 0,
                          overflowY: "auto",
                          padding: "18px 18px 20px",
                          pointerEvents: targetView === view ? "auto" : "none",
                          position: "absolute",
                          transition: "opacity 160ms ease",
                          visibility: targetView === view ? "visible" : "hidden",
                        }}
                      >
                        {renderStageForView(targetView)}
                      </div>
                    ))}
                  </div>
                </>
              );
            })()}
          </div>
        </div>

        <GovernanceInspectorDrawer
          busyAction={busyAction}
          endpointCatalog={endpointsQuery.data ?? null}
          identity={activeIdentity}
          onClose={() => setDrawerTarget(null)}
          onCreatePolicy={handleCreatePolicy}
          onRetireBinding={handleRetireBinding}
          onRetirePolicy={handleRetirePolicy}
          onSetEndpointExposure={handleSetEndpointExposure}
          onUpdatePolicy={handleUpdatePolicy}
          open={Boolean(drawerTarget)}
          serviceId={activeDraft.serviceId}
          target={drawerTarget}
        />
      </div>
    </ConsoleMenuPageShell>
  );
};

export default GovernanceWorkbench;
