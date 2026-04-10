import {
  ApiOutlined,
  ApartmentOutlined,
  BranchesOutlined,
  BuildOutlined,
  DeploymentUnitOutlined,
  EyeOutlined,
  LinkOutlined,
  RocketOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Select,
  Space,
  Table,
  Tabs,
  Typography,
} from "antd";
import type { Edge, Node } from "@xyflow/react";
import React, { useEffect, useMemo, useState } from "react";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import { history } from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  buildTeamsHref,
  type TeamDetailTab,
} from "@/shared/navigation/teamRoutes";
import {
  buildStudioRoute,
  buildStudioScriptsWorkspaceRoute,
  buildStudioWorkflowEditorRoute,
} from "@/shared/studio/navigation";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { runtimeActorsApi } from "@/shared/api/runtimeActorsApi";
import { runtimeGAgentApi } from "@/shared/api/runtimeGAgentApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { scopesApi } from "@/shared/api/scopesApi";
import { servicesApi } from "@/shared/api/servicesApi";
import type { ScopeScriptSummary, ScopeWorkflowSummary } from "@/shared/models/scopes";
import type {
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorGraphNode,
} from "@/shared/models/runtime/actors";
import type {
  ScopeServiceRunAuditTimelineEvent,
  ScopeServiceRunSummary,
} from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import {
  describeScopeServiceBindingTarget,
} from "@/shared/models/runtime/scopeServices";
import {
  describeRuntimeGAgentBindingRevisionTarget,
  formatRuntimeGAgentBindingImplementationKind,
  getRuntimeGAgentCurrentBindingRevision,
  type RuntimeGAgentBindingRevision,
} from "@/shared/models/runtime/gagents";
import {
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";

type TeamDetailRouteState = {
  readonly runId: string;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly tab: TeamDetailTab;
};

type TeamConnectorRow = {
  readonly bindingId: string;
  readonly connectorLabel: string;
  readonly displayName: string;
  readonly retired: boolean;
  readonly serviceDisplayName: string;
  readonly serviceId: string;
  readonly targetLabel: string;
};

const scopeServiceAppId = "default";
const scopeServiceNamespace = "default";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function parseTeamTab(value: string | null): TeamDetailTab {
  switch (trimOptional(value).toLowerCase()) {
    case "topology":
    case "events":
    case "members":
    case "connectors":
    case "advanced":
      return trimOptional(value).toLowerCase() as TeamDetailTab;
    default:
      return "overview";
  }
}

function readInitialTeamRouteState(): TeamDetailRouteState {
  if (typeof window === "undefined") {
    return {
      runId: "",
      scopeId: "",
      serviceId: "",
      tab: "overview",
    };
  }

  const pathname = window.location.pathname.split("/").filter(Boolean);
  const params = new URLSearchParams(window.location.search);
  return {
    runId: trimOptional(params.get("runId")),
    scopeId: trimOptional(pathname[1]),
    serviceId: trimOptional(params.get("serviceId")),
    tab: parseTeamTab(params.get("tab")),
  };
}

function buildScopedServiceHref(scopeId: string, serviceId: string): string {
  const params = new URLSearchParams();
  params.set("tenantId", scopeId.trim());
  params.set("appId", scopeServiceAppId);
  params.set("namespace", scopeServiceNamespace);
  params.set("serviceId", serviceId.trim());
  return `/services?${params.toString()}`;
}

function shortActorId(actorId: string): string {
  const normalized = actorId.trim();
  if (!normalized) {
    return "n/a";
  }

  if (normalized.length <= 24) {
    return normalized;
  }

  return `${normalized.slice(0, 10)}...${normalized.slice(-10)}`;
}

function inferTeamNodeLabel(node: WorkflowActorGraphNode): {
  readonly label: string;
  readonly subtitle: string;
} {
  const workflowName = trimOptional(node.properties.workflowName);
  const stepId = trimOptional(node.properties.stepId);
  const stepType = trimOptional(node.properties.stepType);
  const targetRole = trimOptional(node.properties.targetRole);

  switch (node.nodeType) {
    case "WorkflowRun":
      return {
        label: workflowName || "Workflow Run",
        subtitle: shortActorId(node.nodeId),
      };
    case "WorkflowStep":
      return {
        label: stepId || "Step",
        subtitle: [stepType, targetRole].filter(Boolean).join(" · ") || shortActorId(node.nodeId),
      };
    default:
      return {
        label: workflowName || shortActorId(node.nodeId),
        subtitle: node.nodeType || "Actor",
      };
  }
}

function resolveNodeLevelMap(
  graph: WorkflowActorGraphEnrichedSnapshot,
): Map<string, number> {
  const levels = new Map<string, number>();
  const rootNodeId =
    trimOptional(graph.subgraph.rootNodeId) ||
    trimOptional(graph.snapshot.actorId) ||
    trimOptional(graph.subgraph.nodes[0]?.nodeId);

  if (!rootNodeId) {
    return levels;
  }

  const queue = [rootNodeId];
  levels.set(rootNodeId, 0);

  while (queue.length > 0) {
    const currentNodeId = queue.shift() ?? "";
    const currentLevel = levels.get(currentNodeId) ?? 0;

    graph.subgraph.edges
      .filter((edge) => edge.fromNodeId === currentNodeId)
      .forEach((edge) => {
        if (levels.has(edge.toNodeId)) {
          return;
        }

        levels.set(edge.toNodeId, currentLevel + 1);
        queue.push(edge.toNodeId);
      });
  }

  let fallbackLevel = Math.max(...Array.from(levels.values()), 0) + 1;
  graph.subgraph.nodes.forEach((node) => {
    if (!levels.has(node.nodeId)) {
      levels.set(node.nodeId, fallbackLevel);
      fallbackLevel += 1;
    }
  });

  return levels;
}

function buildTopologyGraph(
  graph: WorkflowActorGraphEnrichedSnapshot | undefined,
): {
  readonly edges: Edge[];
  readonly nodes: Node[];
} {
  if (!graph) {
    return { edges: [], nodes: [] };
  }

  const levelMap = resolveNodeLevelMap(graph);
  const rowsByLevel = new Map<number, number>();

  const nodes = graph.subgraph.nodes.map((node) => {
    const level = levelMap.get(node.nodeId) ?? 0;
    const row = rowsByLevel.get(level) ?? 0;
    rowsByLevel.set(level, row + 1);
    const label = inferTeamNodeLabel(node);
    const accentColor =
      node.nodeType === "WorkflowRun"
        ? "#7c3aed"
        : node.nodeType === "WorkflowStep"
          ? "#1677ff"
          : "#16a34a";

    return {
      id: node.nodeId,
      data: {
        label: (
          <div style={{ minWidth: 180 }}>
            <div style={{ fontSize: 13, fontWeight: 600 }}>{label.label}</div>
            <div style={{ color: "var(--ant-color-text-tertiary)", fontSize: 11 }}>
              {label.subtitle}
            </div>
          </div>
        ),
      },
      position: {
        x: level * 260,
        y: row * 132,
      },
      style: {
        background: "var(--ant-color-bg-container)",
        border: `1px solid ${accentColor}`,
        borderRadius: 16,
        boxShadow: "0 12px 28px rgba(15, 23, 42, 0.08)",
        padding: 12,
      },
    } satisfies Node;
  });

  const edges = graph.subgraph.edges.map((edge) => ({
    id: edge.edgeId,
    label: edge.edgeType,
    source: edge.fromNodeId,
    target: edge.toNodeId,
    animated: edge.edgeType !== "CONTAINS_STEP",
    style: {
      stroke:
        edge.edgeType === "CHILD_OF"
          ? "#16a34a"
          : edge.edgeType === "OWNS"
            ? "#7c3aed"
            : "#1677ff",
      strokeDasharray: edge.edgeType === "CHILD_OF" ? "4 4" : undefined,
      strokeWidth: edge.edgeType === "CONTAINS_STEP" ? 1.5 : 2,
    },
  })) satisfies Edge[];

  return { edges, nodes };
}

function resolveMemberEditorHref(options: {
  readonly memberLabel: string;
  readonly scopeId: string;
  readonly scopeLabel: string;
  readonly scripts: readonly ScopeScriptSummary[];
  readonly service: ServiceCatalogSnapshot | null | undefined;
  readonly workflows: readonly ScopeWorkflowSummary[];
  readonly preferredRevision?: RuntimeGAgentBindingRevision | null;
}): string {
  const {
    memberLabel,
    preferredRevision,
    scopeId,
    scopeLabel,
    scripts,
    service,
    workflows,
  } = options;
  const memberId = service?.serviceId || "";
  const workflowMatch =
    workflows.find((item) => item.workflowId === memberId) ||
    workflows.find((item) => item.workflowName === memberId) ||
    workflows.find((item) => item.displayName === memberLabel) ||
    workflows.find(
      (item) =>
        trimOptional(preferredRevision?.workflowName) &&
        item.workflowName === trimOptional(preferredRevision?.workflowName),
    ) ||
    workflows.find(
      (item) =>
        trimOptional(preferredRevision?.workflowName) &&
        item.displayName === trimOptional(preferredRevision?.workflowName),
    ) ||
    null;

  if (workflowMatch) {
    return buildStudioWorkflowEditorRoute({
      memberId,
      memberLabel,
      scopeId,
      scopeLabel,
      workflowId: workflowMatch.workflowId,
    });
  }

  const scriptMatch =
    scripts.find((item) => item.scriptId === memberId) ||
    scripts.find(
      (item) =>
        trimOptional(preferredRevision?.scriptId) &&
        item.scriptId === trimOptional(preferredRevision?.scriptId),
    ) ||
    null;

  if (scriptMatch) {
    return buildStudioScriptsWorkspaceRoute({
      memberId,
      memberLabel,
      scopeId,
      scopeLabel,
      scriptId: scriptMatch.scriptId,
    });
  }

  return buildStudioRoute({
    memberId,
    memberLabel,
    scopeId,
    scopeLabel,
    tab: "workflows",
  });
}

const TeamMetricCard: React.FC<{
  readonly label: string;
  readonly value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 14,
      display: "flex",
      flexDirection: "column",
      gap: 4,
      minWidth: 0,
      padding: 14,
    }}
  >
    <Typography.Text type="secondary">{label}</Typography.Text>
    <Typography.Text strong style={{ fontSize: 18 }}>
      {value}
    </Typography.Text>
  </div>
);

const TeamDetailPage: React.FC = () => {
  const initialState = useMemo(() => readInitialTeamRouteState(), []);
  const [activeTab, setActiveTab] = useState<TeamDetailTab>(initialState.tab);
  const [selectedServiceId, setSelectedServiceId] = useState(initialState.serviceId);
  const [selectedRunId, setSelectedRunId] = useState(initialState.runId);

  const scopeId = initialState.scopeId;

  const bindingQuery = useQuery({
    enabled: Boolean(scopeId),
    queryKey: ["teams", "binding", scopeId],
    queryFn: () => runtimeGAgentApi.getScopeBinding(scopeId),
  });
  const servicesQuery = useQuery({
    enabled: Boolean(scopeId),
    queryKey: ["teams", "services", scopeId],
    queryFn: () =>
      servicesApi.listServices({
        tenantId: scopeId,
        appId: scopeServiceAppId,
        namespace: scopeServiceNamespace,
      }),
  });
  const actorsQuery = useQuery({
    enabled: Boolean(scopeId),
    queryKey: ["teams", "actors", scopeId],
    queryFn: () => runtimeGAgentApi.listActors(scopeId),
  });
  const workflowsQuery = useQuery({
    enabled: Boolean(scopeId),
    queryKey: ["teams", "workflows", scopeId],
    queryFn: () => scopesApi.listWorkflows(scopeId),
  });
  const scriptsQuery = useQuery({
    enabled: Boolean(scopeId),
    queryKey: ["teams", "scripts", scopeId],
    queryFn: () => scopesApi.listScripts(scopeId),
  });

  const currentBindingRevision = useMemo(
    () => getRuntimeGAgentCurrentBindingRevision(bindingQuery.data),
    [bindingQuery.data],
  );
  const services = servicesQuery.data ?? [];
  const selectedService =
    services.find((service) => service.serviceId === selectedServiceId) ?? null;
  const teamLabel = scopeId || "团队";

  useEffect(() => {
    if (!selectedServiceId && services.length > 0) {
      const nextServiceId =
        trimOptional(bindingQuery.data?.serviceId) || services[0]?.serviceId || "";
      setSelectedServiceId(nextServiceId);
    }
  }, [bindingQuery.data?.serviceId, selectedServiceId, services]);

  const runsQuery = useQuery({
    enabled: Boolean(scopeId && selectedService?.serviceId),
    queryKey: ["teams", "runs", scopeId, selectedService?.serviceId],
    queryFn: () =>
      scopeRuntimeApi.listServiceRuns(scopeId, selectedService?.serviceId || "", {
        take: 12,
      }),
  });
  const recentRuns = runsQuery.data?.runs ?? [];

  useEffect(() => {
    if (!recentRuns.length) {
      setSelectedRunId("");
      return;
    }

    if (selectedRunId && recentRuns.some((run) => run.runId === selectedRunId)) {
      return;
    }

    setSelectedRunId(recentRuns[0]?.runId || "");
  }, [recentRuns, selectedRunId]);

  const selectedRun =
    recentRuns.find((run) => run.runId === selectedRunId) ?? recentRuns[0] ?? null;

  const selectedRunAuditQuery = useQuery({
    enabled: Boolean(
      scopeId &&
        selectedService?.serviceId &&
        selectedRun?.runId &&
        selectedRun.actorId,
    ),
    queryKey: [
      "teams",
      "run-audit",
      scopeId,
      selectedService?.serviceId,
      selectedRun?.runId,
      selectedRun?.actorId,
    ],
    queryFn: () =>
      scopeRuntimeApi.getServiceRunAudit(
        scopeId,
        selectedService?.serviceId || "",
        selectedRun?.runId || "",
        {
          actorId: selectedRun?.actorId || undefined,
        },
      ),
  });

  const topologyQuery = useQuery({
    enabled: Boolean(trimOptional(bindingQuery.data?.primaryActorId)),
    queryKey: ["teams", "topology", trimOptional(bindingQuery.data?.primaryActorId)],
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(
        trimOptional(bindingQuery.data?.primaryActorId),
        {
          depth: 4,
          take: 120,
          direction: "Both",
        },
      ),
  });

  const connectorBindingsQuery = useQuery({
    enabled: Boolean(scopeId && services.length > 0),
    queryKey: [
      "teams",
      "connectors",
      scopeId,
      services.map((service) => service.serviceId).join("|"),
    ],
    queryFn: async () => {
      const results = await Promise.all(
        services.map(async (service) => {
          try {
            const snapshot = await scopeRuntimeApi.getServiceBindings(
              scopeId,
              service.serviceId,
            );
            return snapshot.bindings
              .filter((binding) => binding.connectorRef)
              .map(
                (binding) =>
                  ({
                    bindingId: binding.bindingId,
                    connectorLabel: `${binding.connectorRef?.connectorType || ""}:${binding.connectorRef?.connectorId || ""}`,
                    displayName: binding.displayName || binding.bindingId,
                    retired: binding.retired,
                    serviceDisplayName: service.displayName || service.serviceId,
                    serviceId: service.serviceId,
                    targetLabel: describeScopeServiceBindingTarget(binding),
                  }) satisfies TeamConnectorRow,
              );
          } catch {
            return [] as TeamConnectorRow[];
          }
        }),
      );

      return results.flat();
    },
  });

  useEffect(() => {
    if (!scopeId) {
      return;
    }

    history.replace(
      buildTeamDetailHref({
        scopeId,
        tab: activeTab,
        serviceId: selectedService?.serviceId || undefined,
        runId: selectedRun?.runId || undefined,
      }),
    );
  }, [activeTab, scopeId, selectedRun?.runId, selectedService?.serviceId]);

  const connectorRows = connectorBindingsQuery.data ?? [];
  const actorCount = useMemo(
    () =>
      (actorsQuery.data ?? []).reduce(
        (count, group) => count + group.actorIds.length,
        0,
      ),
    [actorsQuery.data],
  );
  const topologyGraph = useMemo(
    () => buildTopologyGraph(topologyQuery.data),
    [topologyQuery.data],
  );
  const defaultEditorHref = resolveMemberEditorHref({
    memberLabel: selectedService?.displayName || selectedService?.serviceId || teamLabel,
    preferredRevision: currentBindingRevision,
    scopeId,
    scopeLabel: teamLabel,
    scripts: scriptsQuery.data ?? [],
    service: selectedService,
    workflows: workflowsQuery.data ?? [],
  });

  const eventTimeline = selectedRunAuditQuery.data?.audit.timeline ?? [];
  const eventSummary = selectedRunAuditQuery.data?.audit.summary;

  const memberColumns = [
    {
      dataIndex: "displayName",
      key: "member",
      render: (_: unknown, service: ServiceCatalogSnapshot) => (
        <div>
          <Typography.Text strong>
            {service.displayName || service.serviceId}
          </Typography.Text>
          <div style={{ color: "var(--ant-color-text-tertiary)", fontSize: 12 }}>
            {service.serviceKey}
          </div>
        </div>
      ),
      title: "成员",
    },
    {
      dataIndex: "serviceId",
      key: "serviceId",
      render: (value: string) => (
        <Typography.Text code>{value}</Typography.Text>
      ),
      title: "Service ID",
    },
    {
      dataIndex: "primaryActorId",
      key: "actorId",
      render: (value: string) => (
        <Typography.Text code>{shortActorId(value)}</Typography.Text>
      ),
      title: "Actor",
    },
    {
      dataIndex: "deploymentStatus",
      key: "status",
      render: (value: string) => (
        <AevatarStatusTag domain="governance" status={value || "draft"} />
      ),
      title: "状态",
    },
    {
      dataIndex: "endpoints",
      key: "endpoints",
      render: (value: ServiceCatalogSnapshot["endpoints"]) => value.length,
      title: "端点",
    },
    {
      dataIndex: "updatedAt",
      key: "updatedAt",
      render: (value: string) => formatDateTime(value),
      title: "更新时间",
    },
    {
      key: "actions",
      render: (_: unknown, service: ServiceCatalogSnapshot) => (
        <Space wrap>
          <Button
            icon={<EyeOutlined />}
            onClick={() => {
              setSelectedServiceId(service.serviceId);
              setActiveTab("events");
            }}
          >
            查看事件流
          </Button>
          <Button
            icon={<LinkOutlined />}
            onClick={() =>
              history.push(buildScopedServiceHref(scopeId, service.serviceId))
            }
          >
            平台详情
          </Button>
          <Button
            icon={<BuildOutlined />}
            onClick={() =>
              history.push(
                resolveMemberEditorHref({
                  memberLabel: service.displayName || service.serviceId,
                  scopeId,
                  scopeLabel: teamLabel,
                  scripts: scriptsQuery.data ?? [],
                  service,
                  workflows: workflowsQuery.data ?? [],
                }),
              )
            }
            type="primary"
          >
            编辑
          </Button>
        </Space>
      ),
      title: "操作",
    },
  ];

  const connectorColumns = [
    {
      dataIndex: "displayName",
      key: "displayName",
      title: "连接器绑定",
    },
    {
      dataIndex: "connectorLabel",
      key: "connectorLabel",
      render: (value: string) => <Typography.Text code>{value}</Typography.Text>,
      title: "连接器",
    },
    {
      dataIndex: "serviceDisplayName",
      key: "serviceDisplayName",
      render: (value: string, row: TeamConnectorRow) => (
        <div>
          <Typography.Text strong>{value}</Typography.Text>
          <div style={{ color: "var(--ant-color-text-tertiary)", fontSize: 12 }}>
            {row.serviceId}
          </div>
        </div>
      ),
      title: "来源成员",
    },
    {
      dataIndex: "targetLabel",
      key: "targetLabel",
      title: "目标",
    },
    {
      dataIndex: "retired",
      key: "retired",
      render: (value: boolean) => (
        <AevatarStatusTag
          domain="governance"
          status={value ? "retired" : "active"}
        />
      ),
      title: "状态",
    },
  ];

  const eventColumns = [
    {
      dataIndex: "timestamp",
      key: "timestamp",
      render: (value: string | null) => formatDateTime(value),
      title: "时间",
      width: 180,
    },
    {
      dataIndex: "eventType",
      key: "eventType",
      title: "类型",
      width: 220,
    },
    {
      dataIndex: "agentId",
      key: "agentId",
      render: (value: string) => <Typography.Text code>{shortActorId(value)}</Typography.Text>,
      title: "Actor",
      width: 180,
    },
    {
      dataIndex: "stepId",
      key: "stepId",
      render: (value: string) => value || "—",
      title: "步骤",
      width: 140,
    },
    {
      dataIndex: "message",
      key: "message",
      title: "详情",
    },
  ];

  const tabItems = [
    {
      key: "overview",
      label: "概览",
      children: (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="团队总览"
            titleHelp="当前前端以 Scope = Team 的语义组织信息，团队详情把默认入口、成员规模和运行状态收敛到一个页面。"
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
              }}
            >
              <TeamMetricCard label="团队 ID" value={scopeId || "n/a"} />
              <TeamMetricCard label="成员数" value={services.length} />
              <TeamMetricCard label="Actor 实例" value={actorCount} />
              <TeamMetricCard label="行为定义" value={workflowsQuery.data?.length ?? 0} />
              <TeamMetricCard label="脚本行为" value={scriptsQuery.data?.length ?? 0} />
              <TeamMetricCard label="连接器" value={connectorRows.length} />
              <TeamMetricCard
                label="默认入口"
                value={bindingQuery.data?.displayName || bindingQuery.data?.serviceId || "未绑定"}
              />
              <TeamMetricCard
                label="部署状态"
                value={bindingQuery.data?.deploymentStatus || "draft"}
              />
            </div>
          </AevatarPanel>

          <AevatarPanel
            title="当前默认成员"
            titleHelp="这里展示当前 scope 默认服务绑定到的运行入口，也是事件拓扑和事件流的默认观察对象。"
          >
            {!bindingQuery.data?.available || !currentBindingRevision ? (
              <Alert
                showIcon
                title="当前团队还没有可观察的默认成员绑定。"
                type="info"
              />
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                <Space wrap size={[8, 8]}>
                  <Typography.Text strong style={{ fontSize: 16 }}>
                    {bindingQuery.data.displayName || bindingQuery.data.serviceId}
                  </Typography.Text>
                  <AevatarStatusTag
                    domain="governance"
                    status={bindingQuery.data.deploymentStatus || "draft"}
                    label={formatRuntimeGAgentBindingImplementationKind(
                      currentBindingRevision.implementationKind,
                    )}
                  />
                </Space>
                <Typography.Text type="secondary">
                  {describeRuntimeGAgentBindingRevisionTarget(currentBindingRevision)}
                </Typography.Text>
                <Typography.Text type="secondary">
                  Actor {currentBindingRevision.primaryActorId || "n/a"} · Revision{" "}
                  {currentBindingRevision.revisionId}
                </Typography.Text>
              </div>
            )}
          </AevatarPanel>
        </div>
      ),
    },
    {
      key: "topology",
      label: "事件拓扑",
      children: (
        <AevatarPanel
          title="EventEnvelope 事件拓扑"
          titleHelp="这里复用现有 XYFlow 画布，把当前默认成员的 actor 子图直接展开成团队运行时拓扑。"
        >
          {topologyQuery.error ? (
            <Alert
              showIcon
              title={
                topologyQuery.error instanceof Error
                  ? topologyQuery.error.message
                  : "加载事件拓扑失败。"
              }
              type="error"
            />
          ) : topologyQuery.isLoading ? (
            <AevatarInspectorEmpty description="正在加载团队事件拓扑。" />
          ) : topologyGraph.nodes.length > 0 ? (
            <GraphCanvas
              edges={topologyGraph.edges}
              nodes={topologyGraph.nodes}
              height={520}
            />
          ) : (
            <Empty
              description="当前默认成员还没有可展示的运行时拓扑。"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </AevatarPanel>
      ),
    },
    {
      key: "events",
      label: "事件流",
      children: (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="运行选择"
            titleHelp="事件流直接复用现有 service run audit，先选择成员，再查看最近一次运行的 EventEnvelope 时间线。"
          >
            <Space wrap size={[12, 12]}>
              <Select
                options={services.map((service) => ({
                  label: service.displayName || service.serviceId,
                  value: service.serviceId,
                }))}
                onChange={setSelectedServiceId}
                placeholder="选择成员"
                style={{ minWidth: 220 }}
                value={selectedService?.serviceId || undefined}
              />
              <Select
                disabled={!selectedService}
                options={recentRuns.map((run) => ({
                  label: `${run.runId} · ${run.completionStatus}`,
                  value: run.runId,
                }))}
                onChange={setSelectedRunId}
                placeholder="选择运行"
                style={{ minWidth: 260 }}
                value={selectedRun?.runId || undefined}
              />
              {selectedService ? (
                <Button
                  icon={<RocketOutlined />}
                  onClick={() =>
                    history.push(
                      buildScopedServiceHref(scopeId, selectedService.serviceId),
                    )
                  }
                >
                  打开平台服务
                </Button>
              ) : null}
            </Space>
          </AevatarPanel>

          {eventSummary ? (
            <AevatarPanel title="运行摘要">
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                }}
              >
                <TeamMetricCard label="总步骤" value={eventSummary.totalSteps} />
                <TeamMetricCard
                  label="已请求步骤"
                  value={eventSummary.requestedSteps}
                />
                <TeamMetricCard
                  label="已完成步骤"
                  value={eventSummary.completedSteps}
                />
                <TeamMetricCard
                  label="角色回复"
                  value={eventSummary.roleReplyCount}
                />
              </div>
            </AevatarPanel>
          ) : null}

          <AevatarPanel
            title="事件时间线"
            titleHelp="当前展示的是选中运行的审计时间线，用来还原消息进入、路由、工具调用和完成状态。"
          >
            {runsQuery.error ? (
              <Alert
                showIcon
                title={
                  runsQuery.error instanceof Error
                    ? runsQuery.error.message
                    : "加载运行列表失败。"
                }
                type="error"
              />
            ) : runsQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在加载成员运行列表。" />
            ) : selectedRunAuditQuery.error ? (
              <Alert
                showIcon
                title={
                  selectedRunAuditQuery.error instanceof Error
                    ? selectedRunAuditQuery.error.message
                    : "加载运行审计失败。"
                }
                type="error"
              />
            ) : selectedRunAuditQuery.isLoading ? (
              <AevatarInspectorEmpty description="正在加载事件流审计。" />
            ) : eventTimeline.length > 0 ? (
              <Table<ScopeServiceRunAuditTimelineEvent>
                columns={eventColumns}
                dataSource={eventTimeline}
                pagination={{ pageSize: 8, showSizeChanger: false }}
                rowKey={(row) =>
                  `${row.timestamp || "ts"}:${row.eventType}:${row.agentId}:${row.stepId}`
                }
                size="small"
              />
            ) : (
              <Empty
                description="当前成员还没有可展示的事件流。"
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            )}
          </AevatarPanel>
        </div>
      ),
    },
    {
      key: "members",
      label: "成员",
      children: (
        <AevatarPanel
          title="团队成员"
          titleHelp="当前版本以 scope 下已发布 service 作为团队成员视图，并保留跳转到 Platform Services 和团队构建器的入口。"
        >
          {servicesQuery.error ? (
            <Alert
              showIcon
              title={
                servicesQuery.error instanceof Error
                  ? servicesQuery.error.message
                  : "加载团队成员失败。"
              }
              type="error"
            />
          ) : servicesQuery.isLoading ? (
            <AevatarInspectorEmpty description="正在加载团队成员。" />
          ) : services.length > 0 ? (
            <Table<ServiceCatalogSnapshot>
              columns={memberColumns}
              dataSource={services}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey="serviceKey"
            />
          ) : (
            <Empty
              description="当前团队还没有已发布成员。"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </AevatarPanel>
      ),
    },
    {
      key: "connectors",
      label: "连接器",
      children: (
        <AevatarPanel
          title="团队连接器"
          titleHelp="连接器视图会聚合当前 scope 下各成员 service 绑定出的 connector 依赖，帮助你快速看到外部集成面。"
        >
          {connectorBindingsQuery.isLoading ? (
            <AevatarInspectorEmpty description="正在汇总团队连接器。" />
          ) : connectorRows.length > 0 ? (
            <Table<TeamConnectorRow>
              columns={connectorColumns}
              dataSource={connectorRows}
              pagination={{ pageSize: 8, showSizeChanger: false }}
              rowKey={(row) => `${row.serviceId}:${row.bindingId}`}
            />
          ) : (
            <Empty
              description="当前团队还没有公开的连接器绑定。"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          )}
        </AevatarPanel>
      ),
    },
    {
      key: "advanced",
      label: "高级编辑",
      children: (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <AevatarPanel
            title="团队构建器入口"
            titleHelp="Studio 已经从一级导航移出，这里是当前团队进入行为定义、脚本行为、Agent 角色、集成和测试运行的统一入口。"
          >
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              <Button
                block
                icon={<BuildOutlined />}
                onClick={() => history.push(defaultEditorHref)}
                type="primary"
              >
                打开团队构建器
              </Button>
              <Button
                block
                icon={<BranchesOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioRoute({
                      scopeId,
                      scopeLabel: teamLabel,
                      tab: "workflows",
                    }),
                  )
                }
              >
                行为定义
              </Button>
              <Button
                block
                icon={<ApartmentOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioRoute({
                      scopeId,
                      scopeLabel: teamLabel,
                      tab: "scripts",
                    }),
                  )
                }
              >
                脚本行为
              </Button>
              <Button
                block
                icon={<DeploymentUnitOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioRoute({
                      scopeId,
                      scopeLabel: teamLabel,
                      tab: "roles",
                    }),
                  )
                }
              >
                Agent 角色
              </Button>
              <Button
                block
                icon={<ApiOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioRoute({
                      scopeId,
                      scopeLabel: teamLabel,
                      tab: "connectors",
                    }),
                  )
                }
              >
                集成
              </Button>
              <Button
                block
                icon={<RocketOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioRoute({
                      scopeId,
                      scopeLabel: teamLabel,
                      tab: "executions",
                    }),
                  )
                }
              >
                测试运行
              </Button>
            </div>
          </AevatarPanel>

          <AevatarPanel title="当前上下文">
            <Space direction="vertical" size={8}>
              <Typography.Text>
                团队: <Typography.Text code>{scopeId || "n/a"}</Typography.Text>
              </Typography.Text>
              <Typography.Text>
                默认成员:{" "}
                <Typography.Text code>
                  {selectedService?.serviceId || bindingQuery.data?.serviceId || "n/a"}
                </Typography.Text>
              </Typography.Text>
              <Typography.Text type="secondary">
                团队构建器会自动保留当前 scope，上下文按钮会优先尝试打开匹配的行为定义或脚本行为。
              </Typography.Text>
            </Space>
          </AevatarPanel>
        </div>
      ),
    },
  ];

  return (
    <AevatarPageShell
      extra={
        <Space wrap size={[8, 8]}>
          <Button onClick={() => history.push(buildTeamsHref())}>返回我的团队</Button>
          <Button
            icon={<BuildOutlined />}
            onClick={() => history.push(defaultEditorHref)}
            type="primary"
          >
            打开团队构建器
          </Button>
        </Space>
      }
      layoutMode="document"
      onBack={() => history.push(buildTeamsHref())}
      title={teamLabel || "团队详情"}
      titleHelp="团队详情页把 Scope 的运行语义重新包装成 Team 视图，并把事件拓扑、事件流、成员和高级编辑收敛到统一入口。"
    >
      {!scopeId ? (
        <Alert
          showIcon
          title="缺少团队 ID，暂时无法加载团队详情。"
          type="warning"
        />
      ) : (
        <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
          <Space wrap size={[8, 8]}>
            <AevatarStatusTag
              domain="governance"
              status={bindingQuery.data?.deploymentStatus || "draft"}
            />
            {selectedService ? (
              <Typography.Text type="secondary">
                默认成员 {selectedService.displayName || selectedService.serviceId}
              </Typography.Text>
            ) : null}
            {selectedRun ? (
              <Typography.Text type="secondary">
                最近运行 {selectedRun.runId}
              </Typography.Text>
            ) : null}
          </Space>
          <Tabs
            activeKey={activeTab}
            items={tabItems}
            onChange={(value) => setActiveTab(value as TeamDetailTab)}
          />
        </div>
      )}
    </AevatarPageShell>
  );
};

export default TeamDetailPage;
