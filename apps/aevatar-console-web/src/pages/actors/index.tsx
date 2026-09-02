import {
  FullscreenOutlined,
  RadarChartOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useIntl } from "@umijs/max";
import type { Edge, Node } from "@xyflow/react";
import { Position } from "@xyflow/react";
import {
  Alert,
  Button,
  Drawer,
  Empty,
  Input,
  Modal,
  Select,
  Space,
  Table,
  Tabs,
  Tag,
  Typography,
  theme,
} from "antd";
import type { ColumnsType } from "antd/es/table";
import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import { runtimeActorsApi, type ActorGraphDirection } from "@/shared/api/runtimeActorsApi";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";
import type { WorkflowAgentSummary } from "@/shared/models/runtime/query";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeExplorerHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  type AevatarBreadcrumbItem,
  AevatarInspectorEmpty,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";
import {
  AevatarCompactTag,
  AevatarCompactText,
  truncateMiddle,
} from "@/shared/ui/compactText";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import AevatarContentSkeleton from "@/shared/ui/AevatarContentSkeleton";
import {
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import { t } from "@/shared/i18n/messages";

type ExplorerRouteSelection = {
  actorId: string;
  runId: string;
  scopeId: string;
  serviceId: string;
};

type TopologyTabKey = "graph" | "timeline" | "edges" | "snapshot";

type DisplayActorRecord = {
  description: string;
  id: string;
  subtitle?: string;
  type?: string;
  workflowName?: string;
};

const actorTableMaxHeight = "min(52vh, 440px)";
const actorTableBodyMaxHeight = `calc(${actorTableMaxHeight} - 64px)`;
const actorTableMinWidth = 1120;
const actorPageStackStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 18,
  maxWidth: "100%",
  minWidth: 0,
  width: "100%",
};
const platformBreadcrumbItems: AevatarBreadcrumbItem[] = [
  {
    title: "Platform",
  },
  {
    current: true,
    title: "Topology",
  },
];
const actorTableShellStyle: React.CSSProperties = {
  maxWidth: "100%",
  minWidth: 0,
  overflowX: "auto",
  overflowY: "hidden",
  width: "100%",
};
const actorTableStyle: React.CSSProperties = {
  maxHeight: actorTableMaxHeight,
  maxWidth: "100%",
  minWidth: 0,
  overflow: "hidden",
  width: "100%",
};

function readExplorerSelection(): ExplorerRouteSelection {
  if (typeof window === "undefined") {
    return {
      actorId: "",
      runId: "",
      scopeId: "",
      serviceId: "",
    };
  }

  const searchParams = new URLSearchParams(window.location.search);
  return {
    actorId: searchParams.get("actorId")?.trim() ?? "",
    runId: searchParams.get("runId")?.trim() ?? "",
    scopeId: searchParams.get("scopeId")?.trim() ?? "",
    serviceId: searchParams.get("serviceId")?.trim() ?? "",
  };
}

function readAgentWorkflowName(actor: WorkflowAgentSummary): string {
  const match = actor.description.match(/\[(.+)\]$/);
  return match?.[1]?.trim() || actor.description || actor.type || "Actor";
}

function statusKeyFromCompletionValue(value?: number): string {
  switch (value) {
    case 0:
      return "running";
    case 1:
      return "completed";
    case 2:
      return "timed_out";
    case 3:
      return "failed";
    case 4:
      return "stopped";
    case 5:
      return "not_found";
    case 6:
      return "disabled";
    default:
      return "unknown";
  }
}

function buildContextLabel(scopeId?: string, serviceId?: string, runId?: string): string {
  const segments = [
    scopeId ? truncateMiddle(scopeId, 6, 4) : "",
    serviceId || "",
    runId ? truncateMiddle(runId, 8, 6) : "",
  ].filter(Boolean);
  return segments.length > 0 ? segments.join(" / ") : t("pages.actors.index.entry.context.not.brought", "Entry context not brought in");
}

function filterSubgraph(
  subgraph: WorkflowActorGraphSubgraph,
  direction: ActorGraphDirection,
  edgeTypes: readonly string[],
  depth: number,
): WorkflowActorGraphSubgraph {
  const boundedDepth = Math.max(1, depth);
  const normalizedEdgeTypes = edgeTypes.filter(Boolean);
  const filteredEdges = subgraph.edges.filter(
    (edge) =>
      normalizedEdgeTypes.length === 0 ||
      normalizedEdgeTypes.includes(edge.edgeType),
  );

  const outboundMap = new Map<string, WorkflowActorGraphEdge[]>();
  const inboundMap = new Map<string, WorkflowActorGraphEdge[]>();
  for (const edge of filteredEdges) {
    const outbound = outboundMap.get(edge.fromNodeId) ?? [];
    outbound.push(edge);
    outboundMap.set(edge.fromNodeId, outbound);

    const inbound = inboundMap.get(edge.toNodeId) ?? [];
    inbound.push(edge);
    inboundMap.set(edge.toNodeId, inbound);
  }

  const visitedNodes = new Set<string>([subgraph.rootNodeId]);
  const visitedEdges = new Set<string>();
  const queue: Array<{ depth: number; nodeId: string }> = [
    { depth: 0, nodeId: subgraph.rootNodeId },
  ];

  while (queue.length > 0) {
    const current = queue.shift();
    if (!current) {
      continue;
    }

    if (current.depth >= boundedDepth) {
      continue;
    }

    const neighbors: WorkflowActorGraphEdge[] = [];
    if (direction === "Both" || direction === "Outbound") {
      neighbors.push(...(outboundMap.get(current.nodeId) ?? []));
    }
    if (direction === "Both" || direction === "Inbound") {
      neighbors.push(...(inboundMap.get(current.nodeId) ?? []));
    }

    for (const edge of neighbors) {
      visitedEdges.add(edge.edgeId);
      const neighborId =
        edge.fromNodeId === current.nodeId ? edge.toNodeId : edge.fromNodeId;
      if (visitedNodes.has(neighborId)) {
        continue;
      }

      visitedNodes.add(neighborId);
      queue.push({ depth: current.depth + 1, nodeId: neighborId });
    }
  }

  const nodes = subgraph.nodes.filter((node) => visitedNodes.has(node.nodeId));
  const edges = filteredEdges.filter((edge) => visitedEdges.has(edge.edgeId));
  return {
    edges,
    nodes,
    rootNodeId: subgraph.rootNodeId,
  };
}

function edgeTypeLabel(edgeType: string): string {
  switch (edgeType) {
    case "OWNS":
      return "Run owns";
    case "CONTAINS_STEP":
      return "Contains step";
    case "CHILD_OF":
      return "Child actor";
    default:
      return formatAevatarStatusLabel(edgeType);
  }
}

function graphNodeTitle(node: WorkflowActorGraphNode): string {
  if (node.nodeType === "WorkflowRun") {
    return node.properties.workflowName || "Workflow Run";
  }

  if (node.nodeType === "WorkflowStep") {
    return node.properties.stepId || node.nodeId;
  }

  return node.properties.role || node.nodeId;
}

function graphNodeSubtitle(node: WorkflowActorGraphNode): string {
  if (node.nodeType === "WorkflowRun") {
    return truncateMiddle(node.properties.commandId || node.nodeId, 8, 6);
  }

  if (node.nodeType === "WorkflowStep") {
    return node.properties.stepType || node.nodeType;
  }

  return truncateMiddle(node.nodeId, 8, 6);
}

function isHttp404Error(error: unknown): boolean {
  return (
    error instanceof Error &&
    /^HTTP 404\b/i.test(error.message.trim())
  );
}

function graphNodeTone(nodeType: string): {
  background: string;
  border: string;
  text: string;
} {
  switch (nodeType) {
    case "WorkflowRun":
      return {
        background: "rgba(59, 130, 246, 0.10)",
        border: "rgba(59, 130, 246, 0.28)",
        text: "#2563EB",
      };
    case "WorkflowStep":
      return {
        background: "rgba(34, 197, 94, 0.10)",
        border: "rgba(34, 197, 94, 0.26)",
        text: "#15803D",
      };
    default:
      return {
        background: "rgba(249, 115, 22, 0.10)",
        border: "rgba(249, 115, 22, 0.24)",
        text: "#C2410C",
      };
  }
}

function graphLane(nodeType: string): number {
  switch (nodeType) {
    case "Actor":
      return 0;
    case "WorkflowRun":
      return 1;
    case "WorkflowStep":
      return 2;
    default:
      return 3;
  }
}

const TopologyInlineToken: React.FC<{
  head?: number;
  maxWidth?: React.CSSProperties["maxWidth"];
  monospace?: boolean;
  strong?: boolean;
  tail?: number;
  value: string;
}> = ({
  head = 8,
  maxWidth = "100%",
  monospace = false,
  strong = false,
  tail = 6,
  value,
}) => (
  <AevatarCompactText
    head={head}
    maxWidth={maxWidth}
    mode={monospace ? "middle" : "tail"}
    monospace={monospace}
    strong={strong || monospace}
    tail={tail}
    value={value}
  />
);

const TopologyMetricCard: React.FC<{
  compact?: boolean;
  label: string;
  value: React.ReactNode;
}> = ({ compact = false, label, value }) => (
  <div
    style={{
      background: "rgba(248, 250, 252, 0.92)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 16,
      display: "flex",
      flexDirection: "column",
      gap: compact ? 4 : 6,
      minHeight: compact ? 78 : 94,
      minWidth: 0,
      padding: compact ? 14 : 16,
    }}
  >
    <Typography.Text style={{ color: "var(--ant-color-text-secondary)", fontSize: 12 }}>
      {label}
    </Typography.Text>
    <Typography.Text
      strong
      style={{
        color: "var(--ant-color-text)",
        display: "block",
        fontSize: compact ? 16 : 18,
        lineHeight: 1.35,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const TopologyCompactLabelText: React.FC<{
  color?: string;
  maxWidth?: React.CSSProperties["maxWidth"];
  strong?: boolean;
  value: string;
}> = ({ color, maxWidth = 180, strong = false, value }) => {
  return (
    <AevatarCompactText
      color={color}
      maxChars={18}
      maxWidth={maxWidth}
      mode="tail"
      strong={strong}
      style={{ fontSize: strong ? 13 : 12 }}
      value={value}
    />
  );
};

const TopologyCompactIdentifierTag: React.FC<{
  color?: string;
  value: string;
}> = ({ color, value }) => {
  return (
    <AevatarCompactTag
      color={color}
      head={4}
      style={{ borderRadius: 999 }}
      tail={4}
      value={value}
    />
  );
};

const TopologyStatusPill: React.FC<{
  status: string;
}> = ({ status }) => {
  const { token } = theme.useToken();

  return (
    <Tag
      bordered
      style={{
        ...buildAevatarTagStyle(token as AevatarThemeSurfaceToken, "run", status),
        fontSize: 12,
        gap: 4,
        lineHeight: "18px",
        marginInlineEnd: 0,
        paddingInline: 8,
      }}
    >
      {formatAevatarStatusLabel(status)}
    </Tag>
  );
};

const topologySelectionPanelStyle: React.CSSProperties = {
  background: "rgba(248, 250, 252, 0.92)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 16,
  display: "flex",
  flexDirection: "column",
  gap: 12,
  padding: 16,
};

const topologyGraphManagedSelectionClassName =
  "graph-canvas-self-managed-selection";

const TopologyNodeCard: React.FC<{
  node: WorkflowActorGraphNode;
  selected?: boolean;
}> = ({ node, selected = false }) => {
  const tone = graphNodeTone(node.nodeType);

  return (
    <div
      style={{
        background: "#fff",
        border: `1px solid ${selected ? "rgba(59, 130, 246, 0.5)" : tone.border}`,
        borderRadius: 18,
        boxShadow: selected
          ? "0 0 0 2px rgba(59, 130, 246, 0.14), 0 18px 34px rgba(15, 23, 42, 0.12)"
          : "0 14px 30px rgba(15, 23, 42, 0.08)",
        minWidth: 212,
        padding: "12px 14px",
        transition: "border-color 160ms ease, box-shadow 160ms ease",
      }}
    >
      <Space size={8} style={{ marginBottom: 8 }}>
        <Tag
          style={{
            background: tone.background,
            border: `1px solid ${tone.border}`,
            borderRadius: 999,
            color: tone.text,
            fontSize: 11,
            fontWeight: 700,
            marginInlineEnd: 0,
          }}
        >
          {node.nodeType}
        </Tag>
      </Space>
      <Typography.Text
        strong
        style={{
          color: selected ? "#1D4ED8" : undefined,
          display: "block",
          fontSize: 13,
        }}
      >
        {graphNodeTitle(node)}
      </Typography.Text>
      <Typography.Text
        style={{
          color: "var(--ant-color-text-secondary)",
          display: "block",
          fontFamily: '"IBM Plex Mono", "SF Mono", monospace',
          fontSize: 11,
          marginTop: 4,
        }}
      >
        {graphNodeSubtitle(node)}
      </Typography.Text>
    </div>
  );
};

function buildGraphCanvasNodes(
  subgraph: WorkflowActorGraphSubgraph,
  selectedNodeId?: string,
): Node[] {
  const groups = new Map<number, WorkflowActorGraphNode[]>();
  for (const node of subgraph.nodes) {
    const lane = graphLane(node.nodeType);
    const laneNodes = groups.get(lane) ?? [];
    laneNodes.push(node);
    groups.set(lane, laneNodes);
  }

  for (const laneNodes of groups.values()) {
    laneNodes.sort((left, right) =>
      left.nodeId === subgraph.rootNodeId
        ? -1
        : right.nodeId === subgraph.rootNodeId
          ? 1
          : left.nodeId.localeCompare(right.nodeId),
    );
  }

  return subgraph.nodes.map((node) => {
    const lane = graphLane(node.nodeType);
    const laneNodes = groups.get(lane) ?? [];
    const index = laneNodes.findIndex((item) => item.nodeId === node.nodeId);

    return {
      className: topologyGraphManagedSelectionClassName,
      data: {
        label: React.createElement(TopologyNodeCard, {
          node,
          selected: node.nodeId === selectedNodeId,
        }),
      },
      id: node.nodeId,
      position: {
        x: lane * 320,
        y: index * 164,
      },
      sourcePosition: Position.Right,
      style: {
        background: "transparent",
        border: "none",
        boxShadow: "none",
        padding: 0,
        width: 232,
      },
      targetPosition: Position.Left,
      type: "default",
    };
  });
}

function buildGraphCanvasEdges(subgraph: WorkflowActorGraphSubgraph): Edge[] {
  return subgraph.edges.map((edge) => {
    const color =
      edge.edgeType === "OWNS"
        ? "#2563EB"
        : edge.edgeType === "CONTAINS_STEP"
          ? "#16A34A"
          : "#D97706";

    return {
      animated: edge.edgeType === "CHILD_OF",
      id: edge.edgeId,
      label: edgeTypeLabel(edge.edgeType),
      labelStyle: {
        fill: "#475467",
        fontSize: 11,
        fontWeight: 600,
      },
      source: edge.fromNodeId,
      style: {
        stroke: color,
        strokeWidth: 2,
      },
      target: edge.toNodeId,
      type: "smoothstep",
    };
  });
}

export const TopologyExplorerPage: React.FC<{
  detailOnly?: boolean;
}> = ({ detailOnly = false }) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const initialRouteRef = useRef<ExplorerRouteSelection>(readExplorerSelection());
  const workbenchRef = useRef<HTMLDivElement | null>(null);
  const initialActorId = initialRouteRef.current.actorId || "";
  const [activeTab, setActiveTab] = useState<TopologyTabKey>("graph");
  const [actorKeyword, setActorKeyword] = useState("");
  const [actorInput, setActorInput] = useState(initialActorId);
  const [selectedActorId, setSelectedActorId] = useState(initialActorId);
  const [direction, setDirection] = useState<ActorGraphDirection>("Both");
  const [depth, setDepth] = useState(2);
  const [edgeTypes, setEdgeTypes] = useState<string[]>([]);
  const [selectedNodeId, setSelectedNodeId] = useState(initialActorId);
  const [selectedEdgeId, setSelectedEdgeId] = useState("");
  const [previewActorId, setPreviewActorId] = useState(initialActorId);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [graphFullscreenOpen, setGraphFullscreenOpen] = useState(false);

  const actorsQuery = useQuery({
    enabled: !detailOnly,
    queryFn: () => runtimeQueryApi.listAgents(),
    queryKey: ["runtime-agents"],
  });
  const selectedSnapshotQuery = useQuery({
    enabled: detailOnly && selectedActorId.trim().length > 0,
    queryFn: () => runtimeActorsApi.getActorSnapshot(selectedActorId),
    queryKey: ["runtime-actor-snapshot", selectedActorId],
  });
  const timelineQuery = useQuery({
    enabled: detailOnly && selectedActorId.trim().length > 0,
    queryFn: () => runtimeActorsApi.getActorTimeline(selectedActorId, { take: 40 }),
    queryKey: ["runtime-actor-timeline", selectedActorId],
  });
  const graphQuery = useQuery({
    enabled: detailOnly && selectedActorId.trim().length > 0,
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(selectedActorId, {
        depth,
        direction,
        edgeTypes,
        take: 120,
      }),
    queryKey: [
      "runtime-actor-graph",
      selectedActorId,
      depth,
      direction,
      edgeTypes.join("|"),
    ],
  });
  const previewSnapshotQuery = useQuery({
    enabled: previewOpen && previewActorId.trim().length > 0,
    queryFn: () => runtimeActorsApi.getActorSnapshot(previewActorId),
    queryKey: ["runtime-actor-snapshot", previewActorId, "preview"],
  });
  const previewTimelineQuery = useQuery({
    enabled: previewOpen && previewActorId.trim().length > 0,
    queryFn: () => runtimeActorsApi.getActorTimeline(previewActorId, { take: 8 }),
    queryKey: ["runtime-actor-timeline", previewActorId, "preview"],
  });
  const previewGraphQuery = useQuery({
    enabled: previewOpen && previewActorId.trim().length > 0,
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(previewActorId, {
        depth: 2,
        direction: "Both",
        edgeTypes: [],
        take: 60,
      }),
    queryKey: ["runtime-actor-graph", previewActorId, "preview"],
  });

  const liveActors = useMemo<DisplayActorRecord[]>(() => {
    const keyword = actorKeyword.trim().toLowerCase();
    const actors = actorsQuery.data ?? [];
    const filtered = keyword
      ? actors.filter((actor) =>
          [actor.id, actor.type, actor.description]
            .join(" ")
            .toLowerCase()
            .includes(keyword),
        )
      : actors;

    return filtered.map((actor) => ({
      description: actor.type || "WorkflowRunGAgent",
      id: actor.id,
      subtitle: actor.type,
      type: actor.type,
      workflowName: readAgentWorkflowName(actor),
    }));
  }, [actorKeyword, actorsQuery.data]);

  const displayActors = liveActors;
  const selectedDisplayActor =
    displayActors.find((actor) => actor.id === selectedActorId) ?? null;
  const previewDisplayActor =
    displayActors.find((actor) => actor.id === previewActorId) ?? null;
  const selectedSnapshot = selectedSnapshotQuery.data ?? null;
  const selectedTimeline = timelineQuery.data ?? [];
  const rawSubgraph = graphQuery.data?.subgraph ?? null;
  const previewSnapshot = previewSnapshotQuery.data ?? null;
  const previewTimelineRecords = previewTimelineQuery.data ?? [];
  const previewSubgraph = previewGraphQuery.data?.subgraph ?? {
    edges: [],
    nodes: [],
    rootNodeId: previewActorId,
  };
  const selectedSubgraph = useMemo(() => {
    if (!rawSubgraph) {
      return {
        edges: [],
        nodes: [],
        rootNodeId: selectedActorId,
      } satisfies WorkflowActorGraphSubgraph;
    }

    return filterSubgraph(rawSubgraph, direction, edgeTypes, depth);
  }, [depth, direction, edgeTypes, rawSubgraph, selectedActorId]);

  const selectedNode = useMemo(
    () =>
      selectedSubgraph.nodes.find((node) => node.nodeId === selectedNodeId) ??
      selectedSubgraph.nodes.find((node) => node.nodeId === selectedSubgraph.rootNodeId) ??
      null,
    [selectedNodeId, selectedSubgraph],
  );

  const selectedEdge = useMemo(
    () =>
      selectedSubgraph.edges.find((edge) => edge.edgeId === selectedEdgeId) ?? null,
    [selectedEdgeId, selectedSubgraph.edges],
  );

  useEffect(() => {
    if (!selectedSubgraph.nodes.length) {
      setSelectedNodeId("");
      setSelectedEdgeId("");
      return;
    }

    if (
      selectedNodeId &&
      selectedSubgraph.nodes.some((node) => node.nodeId === selectedNodeId)
    ) {
      return;
    }

    setSelectedNodeId(selectedSubgraph.rootNodeId);
    setSelectedEdgeId("");
  }, [selectedNodeId, selectedSubgraph]);

  useEffect(() => {
    const routeSelection = readExplorerSelection();
    history.replace(
      buildRuntimeExplorerHref({
        actorId: detailOnly ? selectedActorId || undefined : undefined,
        runId:
          detailOnly &&
          routeSelection.actorId &&
          routeSelection.actorId === selectedActorId
            ? routeSelection.runId || undefined
            : detailOnly
              ? initialRouteRef.current.runId || undefined
              : undefined,
        scopeId: detailOnly ? routeSelection.scopeId || undefined : undefined,
        serviceId: detailOnly ? routeSelection.serviceId || undefined : undefined,
      }),
    );
  }, [detailOnly, selectedActorId]);

  const currentContextLabel = useMemo(
    () =>
      buildContextLabel(
        initialRouteRef.current.scopeId,
        initialRouteRef.current.serviceId,
        initialRouteRef.current.runId,
      ),
    [],
  );

  const focusStatus = statusKeyFromCompletionValue(
    selectedSnapshot?.completionStatusValue,
  );
  const focusStatusLabel = formatAevatarStatusLabel(focusStatus);
  const previewStatus = statusKeyFromCompletionValue(
    previewSnapshot?.completionStatusValue,
  );
  const previewTimeline = previewTimelineRecords.slice(0, 4);
  const previewUpdatedAt =
    previewSnapshot?.lastUpdatedAt ||
    "";
  const previewContextLabel = buildContextLabel(
    initialRouteRef.current.scopeId,
    initialRouteRef.current.serviceId,
    initialRouteRef.current.runId,
  );
  const graphCanvasNodes = useMemo(
    () => buildGraphCanvasNodes(selectedSubgraph, selectedNode?.nodeId),
    [selectedNode?.nodeId, selectedSubgraph],
  );
  const graphCanvasEdges = useMemo(
    () => buildGraphCanvasEdges(selectedSubgraph),
    [selectedSubgraph],
  );
  const availableEdgeTypes = useMemo(
    () =>
      Array.from(
        new Set(selectedSubgraph.edges.map((edge) => edge.edgeType).filter(Boolean)),
      ).sort((left, right) => left.localeCompare(right)),
    [selectedSubgraph.edges],
  );

  const actorTableColumns = useMemo<ColumnsType<WorkflowActorTimelineItem>>(
    () => [
      {
        dataIndex: "timestamp",
        key: "timestamp",
        title: t("pages.actors.index.time", "time"),
        width: 170,
        render: (value: string) => formatDateTime(value),
      },
      {
        dataIndex: "stage",
        key: "stage",
        title: t("pages.actors.index.stage", "stage"),
        width: 132,
        render: (value: string) => <AevatarStatusTag domain="run" status={value || "observed"} />,
      },
      {
        dataIndex: "message",
        key: "message",
        title: t("pages.actors.index.event", "event"),
        render: (value: string, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text strong>{value}</Typography.Text>
            <Typography.Text style={{ color: token.colorTextSecondary }}>
              {record.stepId || record.eventType} · {record.stepType || "n/a"}
            </Typography.Text>
          </div>
        ),
      },
      {
        dataIndex: "agentId",
        key: "agentId",
        title: "Actor",
        width: 220,
        render: (value: string) => <TopologyInlineToken monospace value={value} />,
      },
    ],
    [token.colorTextSecondary],
  );

  const edgeTableColumns = useMemo<ColumnsType<WorkflowActorGraphEdge>>(
    () => [
      {
        dataIndex: "edgeType",
        key: "edgeType",
        title: intl.formatMessage({
          id: "pages.actors.index.relation",
          defaultMessage: "relation",
        }),
        width: 140,
        render: (value: string) => <Tag>{edgeTypeLabel(value)}</Tag>,
      },
      {
        dataIndex: "fromNodeId",
        key: "fromNodeId",
        title: "From",
        render: (value: string) => <TopologyInlineToken monospace value={value} />,
      },
      {
        dataIndex: "toNodeId",
        key: "toNodeId",
        title: "To",
        render: (value: string) => <TopologyInlineToken monospace value={value} />,
      },
      {
        dataIndex: "updatedAt",
        key: "updatedAt",
        title: intl.formatMessage({
          id: "pages.actors.index.latest.updates",
          defaultMessage: "Latest updates",
        }),
        width: 170,
        render: (value: string) => formatDateTime(value),
      },
    ],
    [intl],
  );

  const commitWorkbenchActor = useCallback((actorId: string) => {
    setActorInput(actorId);
    setSelectedActorId(actorId);
    setSelectedNodeId(actorId);
    setSelectedEdgeId("");
    setPreviewActorId(actorId);
  }, []);

  const handleLoadFocus = useCallback(() => {
    const nextValue = actorInput.trim();
    if (!nextValue) {
      if (detailOnly) {
        setActorInput("");
        setSelectedActorId("");
        setSelectedNodeId("");
        setPreviewActorId("");
        setSelectedEdgeId("");
      }
      return;
    }

    if (!detailOnly) {
      history.push(
        buildRuntimeExplorerHref({
          actorId: nextValue,
          runId: initialRouteRef.current.runId || undefined,
          scopeId: initialRouteRef.current.scopeId || undefined,
          serviceId: initialRouteRef.current.serviceId || undefined,
        }),
      );
      return;
    }

    commitWorkbenchActor(nextValue);
  }, [actorInput, commitWorkbenchActor, detailOnly]);

  const handleOpenPreview = useCallback(
    (actorId: string) => {
      setPreviewActorId(actorId);
      setPreviewOpen(true);
    },
    [],
  );

  const handleEnterWorkbench = useCallback(
    () => {
      const nextActorId = previewActorId.trim();
      if (!nextActorId) {
        return;
      }

      history.push(
        buildRuntimeExplorerHref({
          actorId: nextActorId,
          runId: initialRouteRef.current.runId || undefined,
          scopeId: initialRouteRef.current.scopeId || undefined,
          serviceId: initialRouteRef.current.serviceId || undefined,
        }),
      );
      setPreviewOpen(false);
    },
    [previewActorId],
  );

  const handleOpenRuns = useCallback((targetActorId?: string, workflowName?: string) => {
    const nextActorId = (targetActorId ?? selectedActorId).trim();
    if (!nextActorId) {
      return;
    }

    history.push(
      buildRuntimeRunsHref({
        actorId: nextActorId,
        route: workflowName?.trim() || undefined,
        scopeId: initialRouteRef.current.scopeId || undefined,
        serviceId: initialRouteRef.current.serviceId || undefined,
        returnTo: buildRuntimeExplorerHref({
          actorId: nextActorId || undefined,
          runId: initialRouteRef.current.runId || undefined,
          scopeId: initialRouteRef.current.scopeId || undefined,
          serviceId: initialRouteRef.current.serviceId || undefined,
        }),
      }),
    );
  }, [selectedActorId]);

  const handleBackToExplorerList = useCallback(() => {
    history.push(buildRuntimeExplorerHref());
  }, []);

  const loadingLiveTopology =
    detailOnly &&
    (selectedSnapshotQuery.isLoading || timelineQuery.isLoading || graphQuery.isLoading);
  const loadingPreviewTopology =
    previewOpen &&
    (previewSnapshotQuery.isLoading || previewTimelineQuery.isLoading || previewGraphQuery.isLoading);
  const selectedActorUnavailable =
    detailOnly &&
    selectedActorId.trim().length > 0 &&
    (isHttp404Error(selectedSnapshotQuery.error) ||
      isHttp404Error(graphQuery.error));
  const previewActorUnavailable =
    previewOpen &&
    previewActorId.trim().length > 0 &&
    (isHttp404Error(previewSnapshotQuery.error) ||
      isHttp404Error(previewGraphQuery.error));
  const liveError =
    actorsQuery.error ||
    (selectedActorUnavailable
      ? null
      : selectedSnapshotQuery.error || timelineQuery.error || graphQuery.error);
  const previewError =
    previewActorUnavailable
      ? null
      : previewSnapshotQuery.error || previewTimelineQuery.error || previewGraphQuery.error;

  const actorListColumns = useMemo<ColumnsType<DisplayActorRecord>>(
    () => [
      {
        key: "workflow",
        title: t("pages.actors.index.workflow.object", "workflow/Object"),
        width: 280,
        render: (_value, record) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
            <TopologyCompactLabelText
              maxWidth={196}
              strong
              value={record.workflowName || record.subtitle || "Actor"}
            />
            <TopologyCompactLabelText
              color={token.colorTextSecondary}
              maxWidth={240}
              value={record.description}
            />
          </div>
        ),
      },
      {
        dataIndex: "id",
        key: "id",
        title: t("pages.actors.index.actor.id", "Actor ID"),
        width: 180,
        render: (value: string) => (
          <TopologyInlineToken head={4} maxWidth={156} monospace strong tail={4} value={value} />
        ),
      },
      {
        dataIndex: "type",
        key: "type",
        title: t("pages.actors.index.actor.type", "actor type"),
        width: 196,
        render: (value?: string) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
            <TopologyCompactIdentifierTag color="default" value={value || "WorkflowRunGAgent"} />
            <TopologyCompactLabelText
              color={token.colorTextSecondary}
              maxWidth={180}
              value={t(
                "pages.actors.index.list.exposes.backend.objects",
                "The current list exposes real backend objects by actor.",
              )}
            />
          </div>
        ),
      },
      {
        key: "context",
        title: t("pages.actors.index.entry.context", "Entry context"),
        width: 220,
        render: () => (
          <TopologyCompactLabelText
            color={token.colorTextSecondary}
            maxWidth={196}
            value={currentContextLabel}
          />
        ),
      },
      {
        key: "actions",
        title: t("pages.actors.index.operate", "operate"),
        width: 108,
        render: (_value, record) => (
          <Button
            onClick={(event) => {
              event.stopPropagation();
              handleOpenPreview(record.id);
            }}
            type="link"
          >
            {t("pages.actors.index.view.overview", "View overview")}</Button>
        ),
      },
    ],
    [currentContextLabel, handleOpenPreview, token.colorTextSecondary],
  );

  const graphControlLabelStyle: React.CSSProperties = {
    color: token.colorTextSecondary,
    fontSize: 12,
    fontWeight: 600,
  };

  const graphControls = (
    <div
      style={{
        display: "grid",
        gap: 12,
        gridTemplateColumns: "repeat(3, minmax(0, 180px))",
      }}
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text style={graphControlLabelStyle}>{t("pages.actors.index.figure.direction", "Figure direction")}</Typography.Text>
        <Select
          options={[
            { label: t("pages.actors.index.two.way", "Two-way"), value: "Both" },
            { label: t("pages.actors.index.only.the.edges.can", "Only the edges can be seen"), value: "Outbound" },
            { label: t("pages.actors.index.only.look.inward", "Only look inward"), value: "Inbound" },
          ]}
          value={direction}
          onChange={(value) => setDirection(value as ActorGraphDirection)}
        />
      </div>
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text style={graphControlLabelStyle}>{t("pages.actors.index.topological.depth", "topological depth")}</Typography.Text>
        <Select
          options={[
            { label: t("pages.actors.index.jump", "1 jump"), value: 1 },
            { label: t("pages.actors.index.jumps", "2 jumps"), value: 2 },
            { label: t("pages.actors.index.jumps.2", "3 jumps"), value: 3 },
          ]}
          value={depth}
          onChange={(value) => setDepth(Number(value))}
        />
      </div>
      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text style={graphControlLabelStyle}>{t("pages.actors.index.relationship.type", "Relationship type")}</Typography.Text>
        <Select
          allowClear
          mode="multiple"
          options={availableEdgeTypes.map((edgeType) => ({
            label: edgeTypeLabel(edgeType),
            value: edgeType,
          }))}
          placeholder={availableEdgeTypes.length > 0 ? t("pages.actors.index.all.relations", "All relations") : t("pages.actors.index.no.relationship.type.yet", "No relationship type yet")}
          value={edgeTypes}
          onChange={(values) => setEdgeTypes(values)}
        />
      </div>
    </div>
  );

  const selectionProperties =
    selectedNode && !selectedEdge ? Object.entries(selectedNode.properties) : [];

  const selectionInspector = (
    <AevatarPanel
      layoutMode="document"
      padding={16}
      title={selectedEdge ? t("pages.actors.index.currently.selected.relationship", "Currently selected relationship") : t("pages.actors.index.currently.selected.node", "Currently selected node")}
    >
      {selectedEdge ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Space size={[8, 8]} wrap>
            <Tag color="processing">{edgeTypeLabel(selectedEdge.edgeType)}</Tag>
            <Typography.Text style={{ color: token.colorTextSecondary, fontSize: 12 }}>
              {t("pages.actors.index.latest.updates.2", "Latest updates")}{formatDateTime(selectedEdge.updatedAt)}
            </Typography.Text>
          </Space>
          <div style={topologySelectionPanelStyle}>
            <Typography.Text strong>{t("pages.actors.index.from", "From")}</Typography.Text>
            <TopologyInlineToken maxWidth="100%" monospace value={selectedEdge.fromNodeId} />
          </div>
          <div style={topologySelectionPanelStyle}>
            <Typography.Text strong>To</Typography.Text>
            <TopologyInlineToken maxWidth="100%" monospace value={selectedEdge.toNodeId} />
          </div>
        </div>
      ) : selectedNode ? (
        <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
          <Space size={[8, 8]} wrap>
            <Tag color="blue">{selectedNode.nodeType}</Tag>
            <Typography.Text style={{ color: token.colorTextSecondary, fontSize: 12 }}>
              {t("pages.actors.index.latest.updates.3", "Latest updates")}{formatDateTime(selectedNode.updatedAt)}
            </Typography.Text>
          </Space>
          <div style={topologySelectionPanelStyle}>
            <Typography.Text strong>{t("pages.actors.index.node.id", "Node ID")}</Typography.Text>
            <TopologyInlineToken maxWidth="100%" monospace value={selectedNode.nodeId} />
          </div>
          <div style={topologySelectionPanelStyle}>
            <Typography.Text strong>{t("pages.actors.index.key.attributes", "Key attributes")}</Typography.Text>
            {selectionProperties.length > 0 ? (
              selectionProperties.slice(0, 6).map(([key, value]) => (
                <div
                  key={`${selectedNode.nodeId}-${key}`}
                  style={{
                    alignItems: "flex-start",
                    display: "grid",
                    gap: 10,
                    gridTemplateColumns: "92px minmax(0, 1fr)",
                  }}
                >
                  <Typography.Text
                    style={{
                      color: token.colorTextSecondary,
                      fontSize: 12,
                      fontWeight: 600,
                    }}
                  >
                    {key}
                  </Typography.Text>
                  <TopologyInlineToken
                    maxWidth="100%"
                    monospace={value.length > 18}
                    value={value || "n/a"}
                  />
                </div>
              ))
            ) : (
              <Typography.Text type="secondary">{t("pages.actors.index.there.are.currently.no", "There are currently no additional properties.")}</Typography.Text>
            )}
          </div>
        </div>
      ) : (
        <AevatarInspectorEmpty description={t("pages.actors.index.first.click.on.node", "First click on a node or relationship in the graph.")} />
      )}
    </AevatarPanel>
  );

  const actorUnavailableNotice = (
    _actorId: string,
    options?: {
      action?: React.ReactNode;
      compact?: boolean;
      contextLabel?: string;
      title?: string;
    },
  ) => (
    <Alert
      action={options?.action}
      description={
        <div style={{ display: "flex", flexDirection: "column", gap: options?.compact ? 8 : 10 }}>
          <Typography.Text style={{ color: token.colorTextSecondary }}>
            {t("pages.actors.index.currently.the.backend.can", "Currently, the backend can still reference this actor, but its snapshot can no longer be found. Common reasons are that the backend is restarted, the running state has been cleaned, or this is the remnant of historical binding.")}</Typography.Text>
          <div
            style={{
              display: "grid",
              gap: 8,
              gridTemplateColumns: "96px minmax(0, 1fr)",
            }}
          >
            {options?.contextLabel ? (
              <>
                <Typography.Text
                  style={{ color: token.colorTextSecondary, fontSize: 12, fontWeight: 600 }}
                >
                  {t("pages.actors.index.entry.context.2", "Entry context")}</Typography.Text>
                <TopologyInlineToken
                  head={4}
                  maxWidth="100%"
                  monospace
                  tail={4}
                  value={options.contextLabel}
                />
              </>
            ) : null}
          </div>
        </div>
      }
      message={options?.title || t("pages.actors.index.the.current.actor.cannot", "The current actor cannot be queried")}
      showIcon
      type="warning"
    />
  );

  return (
    <ConsoleMenuPageShell
      breadcrumbItems={platformBreadcrumbItems}
      extra={
        <Tag color="blue">{t("pages.actors.index.real.data", "real data")}</Tag>
      }
      title="Topology"
    >
      <div style={actorPageStackStyle}>
        <div
          style={{
            ...buildAevatarPanelStyle(surfaceToken),
            display: "flex",
            flexDirection: "column",
            gap: 16,
            padding: 18,
          }}
        >
          <div
            style={{
              alignItems: "flex-start",
              display: "flex",
              flexWrap: "wrap",
              gap: 14,
              justifyContent: "space-between",
            }}
          >
            <div
              style={{
                display: "flex",
                flex: "1 1 180px",
                flexDirection: "column",
                gap: 6,
                minWidth: 180,
              }}
            >
              <Typography.Text
                style={{
                  color: token.colorPrimary,
                  fontSize: 11,
                  fontWeight: 700,
                  letterSpacing: "0.08em",
                  textTransform: "uppercase",
                }}
              >
                {detailOnly ? t("pages.actors.index.track.down.details", "Track down details") : t("pages.actors.index.trace.the.entrance", "Trace the entrance")}
              </Typography.Text>
              <Typography.Text strong style={{ fontSize: 22 }}>
                {detailOnly ? t("pages.actors.index.tracing.object", "Tracing object") : t("pages.actors.index.select.tracing.object", "Select tracing object")}
              </Typography.Text>
            </div>
            <div
              style={{
                alignItems: "flex-end",
                display: "flex",
                flex: "0 1 auto",
                flexDirection: "column",
                gap: 8,
                maxWidth: "100%",
              }}
            >
              <div
                style={{
                  alignItems: "center",
                  background: "rgba(24, 144, 255, 0.06)",
                  border: "1px solid rgba(24, 144, 255, 0.12)",
                  borderRadius: 999,
                  color: token.colorPrimary,
                  display: "inline-flex",
                  fontSize: 12,
                  fontWeight: 600,
                  maxWidth: "100%",
                  minHeight: 32,
                  overflowWrap: "anywhere",
                  padding: "0 14px",
                }}
              >
                {t("pages.actors.index.entry.context.3", "Entry context")}{currentContextLabel}
              </div>
              {detailOnly && selectedActorId ? (
                <TopologyInlineToken
                  head={4}
                  maxWidth={320}
                  monospace
                  tail={4}
                  value={selectedActorId}
                />
              ) : null}
            </div>
          </div>

          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: detailOnly
                ? "minmax(280px, 1fr)"
                : "minmax(280px, 1.3fr) minmax(220px, 1fr)",
            }}
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <Typography.Text style={{ color: token.colorTextSecondary, fontSize: 12, fontWeight: 600 }}>
                {t("pages.actors.index.actor.id.3", "Actor ID")}</Typography.Text>
              <Input
                onChange={(event) => setActorInput(event.target.value)}
                placeholder={t("pages.actors.index.enter.actor.id", "Enter actor ID")}
                style={{
                  fontFamily: '"IBM Plex Mono", "SF Mono", monospace',
                  fontSize: 12,
                }}
                value={actorInput}
              />
            </div>
            {!detailOnly ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                <Typography.Text style={{ color: token.colorTextSecondary, fontSize: 12, fontWeight: 600 }}>
                  {t("pages.actors.index.filter.actors", "Filter Actors")}</Typography.Text>
                <Input
                  onChange={(event) => setActorKeyword(event.target.value)}
                  placeholder={t("pages.actors.index.filter.actors.2", "Filter Actors")}
                  value={actorKeyword}
                />
              </div>
            ) : null}
          </div>

          <div
            style={{
              alignItems: "center",
              display: "flex",
              justifyContent: "flex-end",
            }}
          >
            <Space size={8}>
              <Button onClick={handleLoadFocus} type="primary">
                {detailOnly ? t("pages.actors.index.refresh.tracking.objects", "Refresh tracking objects") : t("pages.actors.index.open.tracing.details", "Open tracing details")}
              </Button>
              {detailOnly ? (
                <Button onClick={handleBackToExplorerList}>{t("pages.actors.index.return.object.list", "Return object list")}</Button>
              ) : (
                <Button onClick={() => actorsQuery.refetch()}>{t("pages.actors.index.refresh.list", "Refresh list")}</Button>
              )}
            </Space>
          </div>
        </div>

        {liveError ? (
          <Alert
            message={
              liveError instanceof Error
                ? liveError.message
                : t("pages.actors.index.topology.read.failed", "Topology read failed.")
            }
            showIcon
            type="error"
          />
        ) : null}

        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(128px, 1fr))",
          }}
        >
          {detailOnly ? (
            <>
              <TopologyMetricCard
                compact
                label="Workflow"
                value={selectedSnapshot?.workflowName || selectedDisplayActor?.workflowName || "n/a"}
              />
              <TopologyMetricCard
                compact
                label={t("pages.actors.index.actor.id.4", "Actor ID")}
                value={
                  selectedActorId ? (
                    <TopologyInlineToken
                      head={4}
                      maxWidth="100%"
                      monospace
                      tail={4}
                      value={selectedActorId}
                    />
                  ) : (
                    "n/a"
                  )
                }
              />
              <TopologyMetricCard
                compact
                label={t("pages.actors.index.entry.context.4", "Entry context")}
                value={<TopologyInlineToken head={4} maxWidth="100%" monospace tail={4} value={currentContextLabel} />}
              />
              <TopologyMetricCard
                compact
                label={t("pages.actors.index.last.updated.time", "last updated time")}
                value={selectedSnapshot ? formatDateTime(selectedSnapshot.lastUpdatedAt) : "n/a"}
              />
            </>
          ) : (
            <>
              <TopologyMetricCard compact label={t("pages.actors.index.traceable.objects", "Traceable objects")} value={displayActors.length} />
              <TopologyMetricCard compact label={t("pages.actors.index.data.source", "data source")} value={t("pages.actors.index.actor.query", "Actor Query")} />
              <TopologyMetricCard compact label={t("pages.actors.index.entry.context.5", "Entry context")} value={currentContextLabel} />
              <TopologyMetricCard
                compact
                label={t("pages.actors.index.list.status", "list status")}
                value={actorsQuery.isLoading ? t("pages.actors.index.reading", "Reading") : t("pages.actors.index.loaded", "Loaded")}
              />
            </>
          )}
        </div>

        {!detailOnly ? (
          <AevatarPanel
            layoutMode="document"
            padding={18}
            title={t("pages.actors.index.traceable.objects.2", "Traceable objects")}
            extra={<Tag color="default">{t("pages.actors.index.real.time", "real time")}</Tag>}
          >
            {actorsQuery.isLoading ? (
              <AevatarContentSkeleton
                ariaLabel={t("pages.actors.index.reading", "Reading traceable objects")}
                columnWidths={["1.4fr", "1fr", "1fr", "1.2fr", 160]}
                rows={4}
                tableMinWidth={actorTableMinWidth}
                variant="table"
              />
            ) : displayActors.length > 0 ? (
              <div className="topology-actor-table-shell" style={actorTableShellStyle}>
                <Table<DisplayActorRecord>
                  className="topology-actor-table"
                  columns={actorListColumns}
                  dataSource={displayActors}
                  locale={{ emptyText: t("pages.actors.index.there.are.currently.no.2", "There are currently no targets to trace.") }}
                  pagination={false}
                  rowKey={(record) => record.id}
                  scroll={{ x: actorTableMinWidth, y: actorTableBodyMaxHeight }}
                  size="middle"
                  style={actorTableStyle}
                  tableLayout="fixed"
                />
              </div>
            ) : (
              <AevatarInspectorEmpty
                description={t("pages.actors.index.there.are.no.actors", "There are no actors visible under the current tenant, or the actor query endpoints are not enabled.")}
                title={t("pages.actors.index.there.is.currently.no", "There is currently no traceable target")}
              />
            )}
          </AevatarPanel>
        ) : null}

        {!detailOnly ? (
          <Drawer
            destroyOnClose={false}
            onClose={() => setPreviewOpen(false)}
            open={previewOpen}
            size="large"
            title={t("pages.actors.index.quick.overview.of.objects", "Quick overview of objects")}
          >
          {!previewActorId ? (
            <AevatarInspectorEmpty description={t("pages.actors.index.first.select.an.actor", "First select an actor from the list.")} />
          ) : loadingPreviewTopology ? (
            <AevatarInspectorEmpty description={t("pages.actors.index.reading.snapshot.timeline.and", "Reading snapshot, timeline and graph subgraph.")} />
          ) : previewActorUnavailable ? (
            actorUnavailableNotice(previewActorId, {
              compact: true,
              contextLabel: previewContextLabel,
              title: t("pages.actors.index.this.actor.is.currently", "This actor is currently not available for preview"),
            })
          ) : previewError ? (
            <Alert
              message={
                previewError instanceof Error
                  ? previewError.message
                  : t("pages.actors.index.failed.to.read.preview", "Failed to read preview object.")
              }
              showIcon
              type="error"
            />
          ) : !previewSnapshot ? (
            <AevatarInspectorEmpty
              description={t("pages.actors.index.there.is.currently.no.2", "There is currently no readable snapshot of the actor.")}
              title={t("pages.actors.index.no.running.data.yet", "No running data yet")}
            />
          ) : (
            <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                <Space size={[8, 8]} wrap>
                  <Tag color="blue">{t("pages.actors.index.real.time.object", "real time object")}</Tag>
                  <TopologyStatusPill status={previewStatus} />
                </Space>
                <TopologyCompactLabelText
                  maxWidth="100%"
                  strong
                  value={
                    previewSnapshot.workflowName ||
                    previewDisplayActor?.workflowName ||
                    previewDisplayActor?.subtitle ||
                    "Actor"
                  }
                />
                <TopologyCompactLabelText
                  color={token.colorTextSecondary}
                  maxWidth="100%"
                  value={previewDisplayActor?.description || t("pages.actors.index.there.are.currently.no.3", "There are currently no additional descriptions.")}
                />
              </div>

              <div
                style={{
                  display: "grid",
                  gap: 10,
                  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                }}
              >
                <TopologyMetricCard compact label={t("pages.actors.index.status.version", "status version")} value={previewSnapshot.stateVersion} />
                <TopologyMetricCard
                  compact
                  label={t("pages.actors.index.recently.synced", "Recently synced")}
                  value={previewUpdatedAt ? formatDateTime(previewUpdatedAt) : "n/a"}
                />
                <TopologyMetricCard compact label={t("pages.actors.index.completed.steps", "Completed steps")} value={previewSnapshot.completedSteps} />
                <TopologyMetricCard compact label={t("pages.actors.index.relationship.edge", "relationship edge")} value={previewSubgraph.edges.length} />
              </div>

              <div
                style={{
                  background: "rgba(248, 250, 252, 0.92)",
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 16,
                  display: "flex",
                  flexDirection: "column",
                  gap: 8,
                  padding: 14,
                }}
              >
                <Typography.Text strong>{t("pages.actors.index.entry.context.6", "Entry context")}</Typography.Text>
                <TopologyInlineToken head={4} maxWidth="100%" monospace tail={4} value={previewContextLabel} />
                <Typography.Paragraph style={{ marginBottom: 0, minHeight: 0 }}>
                  {previewSnapshot.lastOutput || t("pages.actors.index.there.are.currently.no.4", "There are currently no recent outputs.")}
                </Typography.Paragraph>
              </div>

              <div
                style={{
                  background: "rgba(248, 250, 252, 0.92)",
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 16,
                  display: "flex",
                  flexDirection: "column",
                  gap: 12,
                  padding: 16,
                }}
              >
                <Typography.Text strong>{t("pages.actors.index.recent.events", "recent events")}</Typography.Text>
                {previewTimeline.length > 0 ? (
                  previewTimeline.map((event) => (
                    <div
                      key={`${event.timestamp}-${event.stage}-${event.stepId}`}
                      style={{
                        borderBottom: `1px solid ${token.colorBorderSecondary}`,
                        display: "flex",
                        flexDirection: "column",
                        gap: 4,
                        paddingBottom: 10,
                      }}
                      >
                      <Space size={8} wrap>
                        <TopologyStatusPill status={event.stage || "observed"} />
                        <Typography.Text style={{ color: token.colorTextSecondary }}>
                          {formatDateTime(event.timestamp)}
                        </Typography.Text>
                      </Space>
                      <TopologyCompactLabelText maxWidth="100%" strong value={event.message} />
                      <TopologyCompactLabelText
                        color={token.colorTextSecondary}
                        maxWidth="100%"
                        value={`${event.stepId || event.eventType} · ${event.stepType || "n/a"}`}
                      />
                    </div>
                  ))
                ) : (
                  <Typography.Text type="secondary">{t("pages.actors.index.there.are.currently.no.5", "There are currently no recent events.")}</Typography.Text>
                )}
              </div>

              <Space size={8} wrap>
                <Button onClick={() => handleEnterWorkbench()} type="primary">
                  {t("pages.actors.index.enter.the.tracing.workbench", "Enter the tracing workbench")}</Button>
                <Button
                  icon={<RadarChartOutlined />}
                  onClick={() =>
                    handleOpenRuns(
                      previewActorId,
                      previewSnapshot?.workflowName ||
                        previewDisplayActor?.workflowName,
                    )
                  }
                >
                  {t("pages.actors.index.view.run", "View run")}</Button>
              </Space>
            </div>
          )}
          </Drawer>
        ) : null}

        {detailOnly ? (
          <div ref={workbenchRef}>
          <AevatarPanel
            layoutMode="document"
            padding={18}
            title={t("pages.actors.index.trace.work.area", "Trace work area")}
            extra={
              selectedActorId ? (
                <Space size={8} wrap>
                  <Tag color="blue">{t("pages.actors.index.current.focus", "current focus")}</Tag>
                  <Button
                    icon={<RadarChartOutlined />}
                    onClick={() =>
                      handleOpenRuns(
                        selectedActorId,
                        selectedSnapshot?.workflowName ||
                          selectedDisplayActor?.workflowName,
                      )
                    }
                  >
                    {t("pages.actors.index.view.run.2", "View run")}</Button>
                </Space>
              ) : undefined
            }
          >
            {!selectedActorId ? (
              <AevatarInspectorEmpty
                description={t("pages.actors.index.enter.an.actorid.or", "Enter an actorId, or first select a workflow run actor from the object list above.")}
                title={t("pages.actors.index.lock.the.focus.actor", "Lock the focus actor first")}
              />
            ) : loadingLiveTopology ? (
              <AevatarInspectorEmpty description={t("pages.actors.index.reading.snapshot.timeline.and.2", "Reading snapshot, timeline and graph subgraph.")} />
            ) : selectedActorUnavailable ? (
              actorUnavailableNotice(selectedActorId, {
                action: <Button onClick={handleBackToExplorerList}>{t("pages.actors.index.return.object.list.2", "Return object list")}</Button>,
                contextLabel: currentContextLabel,
              })
            ) : !selectedSnapshot ? (
              <AevatarInspectorEmpty
                description={t("pages.actors.index.there.is.currently.no.3", "There is currently no readable snapshot of the actor.")}
                title={t("pages.actors.index.no.running.data.yet.2", "No running data yet")}
              />
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(4, minmax(0, 1fr))",
                  }}
                >
                  <TopologyMetricCard label={t("pages.actors.index.status.version.2", "status version")} value={selectedSnapshot.stateVersion} />
                  <TopologyMetricCard label={t("pages.actors.index.completion.status", "completion status")} value={focusStatusLabel} />
                  <TopologyMetricCard label={t("pages.actors.index.completed.steps.2", "Completed steps")} value={selectedSnapshot.completedSteps} />
                  <TopologyMetricCard label={t("pages.actors.index.character.reply", "character reply")} value={selectedSnapshot.roleReplyCount} />
                </div>

                <Tabs
                  activeKey={activeTab}
                  items={[
                    {
                      key: "graph",
                      label: t("pages.actors.index.relationship.diagram", "relationship diagram"),
                      children: (
                        <div
                          style={{
                            display: "grid",
                            gap: 16,
                            gridTemplateColumns: "minmax(0, 1.7fr) minmax(280px, 0.68fr)",
                          }}
                        >
                          <AevatarPanel
                            layoutMode="document"
                            padding={16}
                            title={t("pages.actors.index.actor.relationship.diagram", "actor relationship diagram")}
                            extra={
                              <Space size={8} wrap>
                                <Tag color="default">{t("pages.actors.index.node", "node")}{selectedSubgraph.nodes.length}</Tag>
                                <Tag color="default">{t("pages.actors.index.side", "side")}{selectedSubgraph.edges.length}</Tag>
                                <Button
                                  icon={<FullscreenOutlined />}
                                  onClick={() => setGraphFullscreenOpen(true)}
                                >
                                  {t("pages.actors.index.view.relationship.diagram.full", "View relationship diagram full screen")}</Button>
                              </Space>
                            }
                          >
                            {selectedSubgraph.nodes.length > 0 ? (
                              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                                {graphControls}
                                <Typography.Text style={{ color: token.colorTextSecondary }}>
                                  {t("pages.actors.index.the.focus.actor.will", "The focus actor will be fixed as the root node, and the current relationship between workflow run, step and child actor is also shown in the figure.")}</Typography.Text>
                                <div
                                  style={{
                                    background: "linear-gradient(180deg, rgba(248, 250, 252, 0.94) 0%, rgba(255, 255, 255, 0.98) 100%)",
                                    border: `1px solid ${token.colorBorderSecondary}`,
                                    borderRadius: 18,
                                    padding: 10,
                                  }}
                                >
                                  <GraphCanvas
                                    edges={graphCanvasEdges}
                                    height={560}
                                    nodes={graphCanvasNodes}
                                    onCanvasSelect={() => setSelectedEdgeId("")}
                                    onEdgeSelect={(edgeId) => {
                                      setSelectedEdgeId(edgeId);
                                    }}
                                    onNodeSelect={(nodeId) => {
                                      setSelectedNodeId(nodeId);
                                      setSelectedEdgeId("");
                                    }}
                                    selectedEdgeId={selectedEdgeId}
                                    selectedNodeId={selectedNode?.nodeId}
                                  />
                                </div>
                              </div>
                            ) : (
                              <Empty
                                description={t("pages.actors.index.the.current.actor.has", "The current actor has no visible relationships.")}
                                image={Empty.PRESENTED_IMAGE_SIMPLE}
                              />
                            )}
                          </AevatarPanel>

                          {selectionInspector}
                        </div>
                      ),
                    },
                    {
                      key: "timeline",
                      label: t("pages.actors.index.recent.events.2", "recent events"),
                      children: (
                        <AevatarPanel layoutMode="document" padding={16} title={t("pages.actors.index.timeline.evidence", "Timeline evidence")}>
                          <Table<WorkflowActorTimelineItem>
                            columns={actorTableColumns}
                            dataSource={selectedTimeline}
                            locale={{ emptyText: t("pages.actors.index.there.are.currently.no.6", "There are currently no timeline events.") }}
                            pagination={false}
                            rowKey={(record) =>
                              `${record.timestamp}-${record.stage}-${record.stepId}-${record.eventType}`
                            }
                            size="small"
                          />
                        </AevatarPanel>
                      ),
                    },
                    {
                      key: "edges",
                      label: t("pages.actors.index.side.relationship", "side relationship"),
                      children: (
                        <AevatarPanel layoutMode="document" padding={16} title={t("pages.actors.index.edge.table", "Edge table")}>
                          <Table<WorkflowActorGraphEdge>
                            columns={edgeTableColumns}
                            dataSource={selectedSubgraph.edges}
                            locale={{ emptyText: t("pages.actors.index.there.are.currently.no.7", "There are currently no edge relationships.") }}
                            pagination={false}
                            rowKey={(record) => record.edgeId}
                            size="small"
                          />
                        </AevatarPanel>
                      ),
                    },
                    {
                      key: "snapshot",
                      label: t("pages.actors.index.snapshot", "Snapshot"),
                      children: (
                        <AevatarPanel layoutMode="document" padding={16} title={t("pages.actors.index.actor.snapshot", "Actor snapshot")}>
                          <div
                            style={{
                              display: "grid",
                              gap: 12,
                              gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                            }}
                          >
	                            <TopologyMetricCard
	                              label={t("pages.actors.index.actor", "Actor")}
	                              value={t("pages.actors.index.snapshot.available", "Snapshot available")}
	                            />
	                            <TopologyMetricCard
	                              label={t("pages.actors.index.last.command", "Last command")}
	                              value={
                                  selectedSnapshot.lastCommandId
                                    ? t("pages.actors.index.command.recorded", "Command recorded")
                                    : "n/a"
                                }
	                            />
                            <TopologyMetricCard
                              label={t("pages.actors.index.last.event", "Last event")}
                              value={<TopologyInlineToken monospace value={selectedSnapshot.lastEventId || "n/a"} />}
                            />
                            <TopologyMetricCard label="Completion" value={focusStatusLabel} />
                            <TopologyMetricCard label={t("pages.actors.index.requested.steps", "Requested steps")} value={selectedSnapshot.requestedSteps} />
                            <TopologyMetricCard label={t("pages.actors.index.total.steps", "Total steps")} value={selectedSnapshot.totalSteps} />
                          </div>
                          <div
                            style={{
                              display: "grid",
                              gap: 12,
                              gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                              marginTop: 12,
                            }}
                          >
                            <div
                              style={{
                                background: "rgba(248, 250, 252, 0.92)",
                                border: `1px solid ${token.colorBorderSecondary}`,
                                borderRadius: 16,
                                padding: 16,
                              }}
                            >
                              <Typography.Text strong style={{ display: "block", marginBottom: 8 }}>
                                {t("pages.actors.index.recent.output", "recent output")}</Typography.Text>
                              <Typography.Paragraph style={{ marginBottom: 0 }}>
                                {selectedSnapshot.lastOutput || t("pages.actors.index.no.output.yet", "No output yet.")}
                              </Typography.Paragraph>
                            </div>
                            <div
                              style={{
                                background: "rgba(248, 250, 252, 0.92)",
                                border: `1px solid ${token.colorBorderSecondary}`,
                                borderRadius: 16,
                                padding: 16,
                              }}
                            >
                              <Typography.Text strong style={{ display: "block", marginBottom: 8 }}>
                                {t("pages.actors.index.recent.errors", "recent errors")}</Typography.Text>
                              <Typography.Paragraph style={{ marginBottom: 0 }}>
                                {selectedSnapshot.lastError || t("pages.actors.index.there.are.currently.no.8", "There are currently no errors.")}
                              </Typography.Paragraph>
                            </div>
                          </div>
                        </AevatarPanel>
                      ),
                    },
                  ]}
                  onChange={(key) => setActiveTab(key as TopologyTabKey)}
                />
              </div>
            )}
          </AevatarPanel>
        </div>
        ) : null}

        {detailOnly ? (
          <Modal
            footer={null}
            onCancel={() => setGraphFullscreenOpen(false)}
            open={graphFullscreenOpen}
            style={{ top: 24 }}
            title={t("pages.actors.index.full.screen.relationship.diagram", "Full screen relationship diagram")}
            width="calc(100vw - 48px)"
          >
            <div
              style={{
                display: "grid",
                gap: 18,
                gridTemplateColumns: "minmax(0, 1.82fr) minmax(320px, 0.68fr)",
                minHeight: "72vh",
              }}
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                {graphControls}
                <div
                  style={{
                    background: "linear-gradient(180deg, rgba(248, 250, 252, 0.94) 0%, rgba(255, 255, 255, 0.98) 100%)",
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 20,
                    flex: 1,
                    minHeight: "64vh",
                    padding: 12,
                  }}
                >
                  <GraphCanvas
                    edges={graphCanvasEdges}
                    height={760}
                    nodes={graphCanvasNodes}
                    onCanvasSelect={() => setSelectedEdgeId("")}
                    onEdgeSelect={(edgeId) => {
                      setSelectedEdgeId(edgeId);
                    }}
                    onNodeSelect={(nodeId) => {
                      setSelectedNodeId(nodeId);
                      setSelectedEdgeId("");
                    }}
                    selectedEdgeId={selectedEdgeId}
                    selectedNodeId={selectedNode?.nodeId}
                  />
                </div>
              </div>
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                <AevatarPanel
                  layoutMode="document"
                  padding={16}
                  title={t("pages.actors.index.figure.summary", "Figure summary")}
                >
                  <div
                    style={{
                      display: "grid",
                      gap: 10,
                      gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                    }}
                  >
                    <TopologyMetricCard compact label={t("pages.actors.index.node.2", "node")} value={selectedSubgraph.nodes.length} />
                    <TopologyMetricCard compact label={t("pages.actors.index.side.2", "side")} value={selectedSubgraph.edges.length} />
                    <TopologyMetricCard compact label={t("pages.actors.index.depth", "depth")} value={t("pages.actors.index.jump.2", "{value1} jump", { value1: depth })} />
                    <TopologyMetricCard compact label={t("pages.actors.index.direction", "direction")} value={direction} />
                  </div>
                </AevatarPanel>
                {selectionInspector}
              </div>
            </div>
          </Modal>
        ) : null}
      </div>
    </ConsoleMenuPageShell>
  );
};

const ActorsPage: React.FC = () => <TopologyExplorerPage />;

export default ActorsPage;
