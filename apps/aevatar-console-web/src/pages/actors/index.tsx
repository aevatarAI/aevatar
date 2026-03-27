import type { ProColumns, ProFormInstance } from "@ant-design/pro-components";
import {
  PageContainer,
  ProCard,
  ProForm,
  ProFormCheckbox,
  ProFormDigit,
  ProFormSelect,
  ProFormText,
  ProTable,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  Alert,
  Button,
  Col,
  Drawer,
  Empty,
  Row,
  Space,
  Statistic,
  Tag,
  Tabs,
  Typography,
} from "antd";
import React, { useEffect, useMemo, useRef, useState } from "react";
import {
  runtimeActorsApi,
  type ActorGraphDirection,
} from "@/shared/api/runtimeActorsApi";
import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
} from "@/shared/models/runtime/actors";
import { formatDateTime } from "@/shared/datetime/dateTime";
import { buildActorGraphElements } from "@/shared/graphs/buildGraphElements";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import { loadRecentRuns } from "@/shared/runs/recentRuns";
import {
  codeBlockStyle,
  cardStackStyle,
  drawerBodyStyle,
  drawerScrollStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";
import { describeError } from "@/shared/ui/errorText";
import {
  type ActorTimelineFilters,
  type ActorTimelineRow,
  buildTimelineRows,
  deriveSubgraphFromEdges,
  filterTimelineRows,
} from "./actorPresentation";

type ActorGraphViewMode = "enriched" | "subgraph" | "edges";
type ActorWorkspaceView = "timeline" | "graph";
type ActorInspectorMode = "snapshot" | "timeline" | "graph";

type ActorPageState = {
  actorId: string;
  timelineTake: number;
  graphDepth: number;
  graphTake: number;
  graphDirection: ActorGraphDirection;
  edgeTypes: string[];
};

type ActorSnapshotRecord = WorkflowActorSnapshot & {
  executionStatus: "success" | "error" | "default";
  completionRate: number;
};

type ActorGraphSummaryRecord = {
  mode: ActorGraphViewMode;
  direction: ActorGraphDirection;
  depth: number;
  take: number;
  edgeTypes: string;
  rootNodeId: string;
  nodeCount: number;
  edgeCount: number;
};

type ActorNodeDetailRecord = WorkflowActorGraphNode & {
  propertyCount: number;
  primaryLabel: string;
  isRoot: boolean;
};

type ActorEdgeDetailRecord = WorkflowActorGraphEdge & {
  propertyCount: number;
};

type GraphControlValues = {
  graphViewMode: ActorGraphViewMode;
};

const defaultActorTimelineTake = 50;
const defaultActorGraphDepth = 3;
const defaultActorGraphTake = 100;
const defaultActorGraphDirection: ActorGraphDirection = "Both";

type SummaryFieldProps = {
  copyable?: boolean;
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  tone?: "default" | "info" | "success" | "warning" | "error";
  value: React.ReactNode;
};

type SectionHeaderProps = {
  description?: React.ReactNode;
  title: string;
};

const defaultTimelineFilters: ActorTimelineFilters = {
  stages: [],
  eventTypes: [],
  stepTypes: [],
  query: "",
  errorsOnly: false,
};

const graphViewOptions: Array<{ label: string; value: ActorGraphViewMode }> = [
  { label: "Enriched", value: "enriched" },
  { label: "Subgraph", value: "subgraph" },
  { label: "Edges only", value: "edges" },
];

const timelineStatusValueEnum = {
  processing: { text: "Processing", status: "Processing" },
  success: { text: "Completed", status: "Success" },
  error: { text: "Error", status: "Error" },
  default: { text: "Observed", status: "Default" },
} as const;

const graphViewLabels: Record<ActorGraphViewMode, string> = {
  enriched: "Backend enriched snapshot",
  subgraph: "Subgraph",
  edges: "Edges only",
};

const workspaceCardBodyStyle = {
  display: "flex",
  flexDirection: "column",
  minHeight: 0,
} as const;

const workspacePanelStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 16,
  minHeight: 0,
};

const graphCanvasShellStyle: React.CSSProperties = {
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  overflow: "hidden",
};

const sectionHeaderStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
};

const sectionDividerStyle: React.CSSProperties = {
  borderTop: "1px solid var(--ant-color-border-secondary)",
  display: "flex",
  flexDirection: "column",
  gap: 12,
  paddingTop: 12,
};

const executionTagColorMap: Record<
  ActorSnapshotRecord["executionStatus"],
  string
> = {
  success: "success",
  error: "error",
  default: "default",
};

const summaryMetricToneMap: Record<
  NonNullable<SummaryMetricProps["tone"]>,
  { color: string }
> = {
  default: { color: "var(--ant-color-text)" },
  error: { color: "var(--ant-color-error)" },
  info: { color: "var(--ant-color-primary)" },
  success: { color: "var(--ant-color-success)" },
  warning: { color: "var(--ant-color-warning)" },
};

const SummaryField: React.FC<SummaryFieldProps> = ({
  copyable,
  label,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {copyable && typeof value === "string" && value && value !== "n/a" ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    )}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({
  label,
  tone = "default",
  value,
}) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        color: summaryMetricToneMap[tone].color,
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

const SectionHeader: React.FC<SectionHeaderProps> = ({
  description,
  title,
}) => (
  <div style={sectionHeaderStyle}>
    <Typography.Text strong>{title}</Typography.Text>
    {description ? (
      <Typography.Paragraph
        style={{ margin: 0 }}
        type="secondary"
      >
        {description}
      </Typography.Paragraph>
    ) : null}
  </div>
);

function renderPropertyList(properties: Record<string, string>) {
  const entries = Object.entries(properties);
  if (entries.length === 0) {
    return "n/a";
  }

  return (
    <Space direction="vertical" size={4} style={{ width: "100%" }}>
      {entries.map(([key, value]) => (
        <Typography.Text key={key}>
          <Typography.Text type="secondary">{key}</Typography.Text>:{" "}
          {value || "n/a"}
        </Typography.Text>
      ))}
    </Space>
  );
}

const timelineColumns: ProColumns<ActorTimelineRow>[] = [
  {
    title: "Timestamp",
    dataIndex: "timestamp",
    valueType: "dateTime",
    width: 220,
    render: (_, record) => formatDateTime(record.timestamp),
  },
  {
    title: "Status",
    dataIndex: "timelineStatus",
    valueType: "status" as any,
    valueEnum: timelineStatusValueEnum,
    width: 120,
  },
  {
    title: "Stage",
    dataIndex: "stage",
    width: 180,
  },
  {
    title: "Event type",
    dataIndex: "eventType",
    width: 220,
    render: (_, record) => record.eventType || "n/a",
  },
  {
    title: "Message",
    dataIndex: "message",
    ellipsis: true,
  },
  {
    title: "Step",
    dataIndex: "stepId",
    width: 180,
    render: (_, record) => record.stepId || "n/a",
  },
  {
    title: "Step type",
    dataIndex: "stepType",
    width: 160,
    render: (_, record) => record.stepType || "n/a",
  },
  {
    title: "Actor",
    dataIndex: "agentId",
    width: 200,
    render: (_, record) => record.agentId || "n/a",
  },
  {
    title: "Data",
    dataIndex: "dataSummary",
    ellipsis: true,
    render: (_, record) =>
      record.dataCount > 0
        ? `${record.dataCount} field${record.dataCount === 1 ? "" : "s"} · ${
            record.dataSummary
          }`
        : "n/a",
  },
];

function parsePositiveInt(value: string | null, fallback: number): number {
  if (!value) {
    return fallback;
  }

  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed <= 0) {
    return fallback;
  }

  return Math.floor(parsed);
}

function parseDirection(
  value: string | null,
  fallback: ActorGraphDirection
): ActorGraphDirection {
  if (value === "Both" || value === "Outbound" || value === "Inbound") {
    return value;
  }

  return fallback;
}

function parseGraphViewMode(value: string | null): ActorGraphViewMode {
  if (value === "subgraph" || value === "edges" || value === "enriched") {
    return value;
  }

  return "enriched";
}

function readStateFromUrl(): ActorPageState {
  if (typeof window === "undefined") {
    return {
      actorId: "",
      timelineTake: defaultActorTimelineTake,
      graphDepth: defaultActorGraphDepth,
      graphTake: defaultActorGraphTake,
      graphDirection: defaultActorGraphDirection,
      edgeTypes: [],
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    actorId: params.get("actorId") ?? "",
    timelineTake: parsePositiveInt(
      params.get("timelineTake"),
      defaultActorTimelineTake
    ),
    graphDepth: parsePositiveInt(
      params.get("graphDepth"),
      defaultActorGraphDepth
    ),
    graphTake: parsePositiveInt(
      params.get("graphTake"),
      defaultActorGraphTake
    ),
    graphDirection: parseDirection(
      params.get("graphDirection"),
      defaultActorGraphDirection
    ),
    edgeTypes: params
      .getAll("edgeTypes")
      .map((value) => value.trim())
      .filter(Boolean),
  };
}

function readGraphViewModeFromUrl(): ActorGraphViewMode {
  if (typeof window === "undefined") {
    return "enriched";
  }

  return parseGraphViewMode(
    new URLSearchParams(window.location.search).get("graphView")
  );
}

const ActorsPage: React.FC = () => {
  const initialState = useMemo(() => readStateFromUrl(), []);
  const initialGraphViewMode = useMemo(() => readGraphViewModeFromUrl(), []);
  const formRef = useRef<ProFormInstance<ActorPageState> | undefined>(
    undefined
  );
  const timelineFormRef = useRef<
    ProFormInstance<ActorTimelineFilters> | undefined
  >(undefined);
  const graphControlFormRef = useRef<
    ProFormInstance<GraphControlValues> | undefined
  >(undefined);
  const [filters, setFilters] = useState<ActorPageState>(initialState);
  const [graphViewMode, setGraphViewMode] =
    useState<ActorGraphViewMode>(initialGraphViewMode);
  const [workspaceView, setWorkspaceView] =
    useState<ActorWorkspaceView>("timeline");
  const [timelineFilters, setTimelineFilters] = useState<ActorTimelineFilters>(
    defaultTimelineFilters
  );
  const [isInspectorDrawerOpen, setIsInspectorDrawerOpen] = useState(false);
  const [inspectorMode, setInspectorMode] =
    useState<ActorInspectorMode>("snapshot");
  const [selectedNodeId, setSelectedNodeId] = useState<string>("");
  const [selectedEdgeId, setSelectedEdgeId] = useState<string>("");
  const [selectedTimelineKey, setSelectedTimelineKey] = useState<string>("");

  const recentActorRuns = useMemo(() => {
    const seenActorIds = new Set<string>();
    return loadRecentRuns().filter((entry) => {
      const actorId = entry.actorId.trim();
      if (!actorId || seenActorIds.has(actorId)) {
        return false;
      }

      seenActorIds.add(actorId);
      return true;
    });
  }, []);

  const loadActorIntoExplorer = (actorId: string) => {
    const nextActorId = actorId.trim();
    const currentValues = formRef.current?.getFieldsValue?.() ?? filters;
    const nextFilters: ActorPageState = {
      actorId: nextActorId,
      timelineTake: Number(currentValues.timelineTake ?? filters.timelineTake),
      graphDepth: Number(currentValues.graphDepth ?? filters.graphDepth),
      graphTake: Number(currentValues.graphTake ?? filters.graphTake),
      graphDirection: currentValues.graphDirection ?? filters.graphDirection,
      edgeTypes: Array.isArray(currentValues.edgeTypes)
        ? currentValues.edgeTypes
        : filters.edgeTypes,
    };

    formRef.current?.setFieldsValue({
      ...currentValues,
      actorId: nextActorId,
    });
    setFilters(nextFilters);
  };

  const snapshotQuery = useQuery({
    queryKey: ["actor-snapshot", filters.actorId],
    enabled: Boolean(filters.actorId),
    queryFn: () => runtimeActorsApi.getActorSnapshot(filters.actorId),
  });

  const timelineQuery = useQuery({
    queryKey: ["actor-timeline", filters.actorId, filters.timelineTake],
    enabled: Boolean(filters.actorId),
    queryFn: () =>
      runtimeActorsApi.getActorTimeline(filters.actorId, {
        take: filters.timelineTake,
      }),
  });

  const graphEnrichedQuery = useQuery({
    queryKey: [
      "actor-graph-enriched",
      filters.actorId,
      filters.graphDepth,
      filters.graphTake,
      filters.graphDirection,
      [...filters.edgeTypes].sort().join(","),
    ],
    enabled: Boolean(filters.actorId),
    queryFn: () =>
      runtimeActorsApi.getActorGraphEnriched(filters.actorId, {
        depth: filters.graphDepth,
        take: filters.graphTake,
        direction: filters.graphDirection,
        edgeTypes: filters.edgeTypes,
      }),
  });

  const graphSubgraphQuery = useQuery({
    queryKey: [
      "actor-graph-subgraph",
      filters.actorId,
      filters.graphDepth,
      filters.graphTake,
      filters.graphDirection,
      [...filters.edgeTypes].sort().join(","),
    ],
    enabled: Boolean(filters.actorId) && graphViewMode === "subgraph",
    queryFn: () =>
      runtimeActorsApi.getActorGraphSubgraph(filters.actorId, {
        depth: filters.graphDepth,
        take: filters.graphTake,
        direction: filters.graphDirection,
        edgeTypes: filters.edgeTypes,
      }),
  });

  const graphEdgesQuery = useQuery({
    queryKey: [
      "actor-graph-edges",
      filters.actorId,
      filters.graphTake,
      filters.graphDirection,
      [...filters.edgeTypes].sort().join(","),
    ],
    enabled: Boolean(filters.actorId) && graphViewMode === "edges",
    queryFn: () =>
      runtimeActorsApi.getActorGraphEdges(filters.actorId, {
        take: filters.graphTake,
        direction: filters.graphDirection,
        edgeTypes: filters.edgeTypes,
      }),
  });

  const snapshotRecord = useMemo<ActorSnapshotRecord | undefined>(() => {
    if (!snapshotQuery.data) {
      return undefined;
    }

    return {
      ...snapshotQuery.data,
      executionStatus:
        snapshotQuery.data.lastSuccess === null
          ? "default"
          : snapshotQuery.data.lastSuccess
          ? "success"
          : "error",
      completionRate:
        snapshotQuery.data.totalSteps > 0
          ? snapshotQuery.data.completedSteps / snapshotQuery.data.totalSteps
          : 0,
    };
  }, [snapshotQuery.data]);

  const timelineRows = useMemo<ActorTimelineRow[]>(
    () => buildTimelineRows(timelineQuery.data ?? []),
    [timelineQuery.data]
  );

  const filteredTimelineRows = useMemo(
    () => filterTimelineRows(timelineRows, timelineFilters),
    [timelineRows, timelineFilters]
  );

  const selectedTimelineRecord = useMemo<ActorTimelineRow | undefined>(
    () => filteredTimelineRows.find((row) => row.key === selectedTimelineKey),
    [filteredTimelineRows, selectedTimelineKey]
  );

  const timelineStageOptions = useMemo(
    () =>
      Array.from(new Set(timelineRows.map((row) => row.stage).filter(Boolean)))
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows]
  );

  const timelineEventTypeOptions = useMemo(
    () =>
      Array.from(
        new Set(timelineRows.map((row) => row.eventType).filter(Boolean))
      )
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows]
  );

  const timelineStepTypeOptions = useMemo(
    () =>
      Array.from(
        new Set(timelineRows.map((row) => row.stepType).filter(Boolean))
      )
        .sort((left, right) => left.localeCompare(right))
        .map((value) => ({ label: value, value })),
    [timelineRows]
  );

  const currentGraph = useMemo<WorkflowActorGraphSubgraph | undefined>(() => {
    if (!filters.actorId) {
      return undefined;
    }

    if (graphViewMode === "subgraph") {
      return graphSubgraphQuery.data;
    }

    if (graphViewMode === "edges") {
      return graphEdgesQuery.data
        ? deriveSubgraphFromEdges(graphEdgesQuery.data, filters.actorId)
        : undefined;
    }

    return graphEnrichedQuery.data?.subgraph;
  }, [
    filters.actorId,
    graphEdgesQuery.data,
    graphEnrichedQuery.data?.subgraph,
    graphSubgraphQuery.data,
    graphViewMode,
  ]);

  const currentGraphError =
    graphViewMode === "subgraph"
      ? graphSubgraphQuery.error
      : graphViewMode === "edges"
      ? graphEdgesQuery.error
      : graphEnrichedQuery.error;

  const graphElements = useMemo(() => {
    if (!currentGraph) {
      return { nodes: [], edges: [] };
    }

    return buildActorGraphElements(
      currentGraph.nodes,
      currentGraph.edges,
      currentGraph.rootNodeId || filters.actorId
    );
  }, [currentGraph, filters.actorId]);

  const availableEdgeTypes = useMemo(
    () =>
      Array.from(
        new Set(
          [
            ...filters.edgeTypes,
            ...(graphEnrichedQuery.data?.subgraph.edges ?? []).map(
              (edge) => edge.edgeType
            ),
            ...(graphSubgraphQuery.data?.edges ?? []).map(
              (edge) => edge.edgeType
            ),
            ...(graphEdgesQuery.data ?? []).map((edge) => edge.edgeType),
          ]
            .map((value) => value.trim())
            .filter(Boolean)
        )
      ).sort((left, right) => left.localeCompare(right)),
    [
      filters.edgeTypes,
      graphEdgesQuery.data,
      graphEnrichedQuery.data?.subgraph.edges,
      graphSubgraphQuery.data?.edges,
    ]
  );

  const graphSummary = useMemo<ActorGraphSummaryRecord | undefined>(() => {
    if (!currentGraph) {
      return undefined;
    }

    return {
      mode: graphViewMode,
      direction: filters.graphDirection,
      depth: filters.graphDepth,
      take: filters.graphTake,
      edgeTypes:
        filters.edgeTypes.length > 0 ? filters.edgeTypes.join(", ") : "All",
      rootNodeId: currentGraph.rootNodeId || filters.actorId,
      nodeCount: currentGraph.nodes.length,
      edgeCount: currentGraph.edges.length,
    };
  }, [
    currentGraph,
    filters.actorId,
    filters.edgeTypes,
    filters.graphDepth,
    filters.graphDirection,
    filters.graphTake,
    graphViewMode,
  ]);

  const selectedNodeRecord = useMemo<ActorNodeDetailRecord | undefined>(() => {
    const node = currentGraph?.nodes.find(
      (item) => item.nodeId === selectedNodeId
    );
    if (!node) {
      return undefined;
    }

    return {
      ...node,
      propertyCount: Object.keys(node.properties).length,
      primaryLabel:
        node.properties.stepId || node.properties.workflowName || node.nodeId,
      isRoot: node.nodeId === currentGraph?.rootNodeId,
    };
  }, [currentGraph, selectedNodeId]);

  const selectedEdgeRecord = useMemo<ActorEdgeDetailRecord | undefined>(() => {
    const edge = currentGraph?.edges.find(
      (item) => item.edgeId === selectedEdgeId
    );
    if (!edge) {
      return undefined;
    }

    return {
      ...edge,
      propertyCount: Object.keys(edge.properties).length,
    };
  }, [currentGraph, selectedEdgeId]);

  const handleOpenInspector = () => {
    if (workspaceView === "timeline" && selectedTimelineRecord) {
      setInspectorMode("timeline");
    } else if (workspaceView === "graph") {
      setInspectorMode("graph");
    } else {
      setInspectorMode("snapshot");
    }

    setIsInspectorDrawerOpen(true);
  };

  const inspectorTitle =
    inspectorMode === "timeline"
      ? "Inspector · timeline detail"
      : inspectorMode === "graph"
      ? "Inspector · graph detail"
      : "Inspector";

  useEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    const url = new URL(window.location.href);
    if (filters.actorId) {
      url.searchParams.set("actorId", filters.actorId);
    } else {
      url.searchParams.delete("actorId");
    }
    url.searchParams.set("timelineTake", String(filters.timelineTake));
    url.searchParams.set("graphDepth", String(filters.graphDepth));
    url.searchParams.set("graphTake", String(filters.graphTake));
    url.searchParams.set("graphDirection", filters.graphDirection);
    url.searchParams.set("graphView", graphViewMode);
    url.searchParams.delete("edgeTypes");
    for (const edgeType of filters.edgeTypes) {
      url.searchParams.append("edgeTypes", edgeType);
    }
    window.history.replaceState(null, "", `${url.pathname}${url.search}`);
  }, [filters, graphViewMode]);

  useEffect(() => {
    timelineFormRef.current?.setFieldsValue(defaultTimelineFilters);
    setTimelineFilters(defaultTimelineFilters);
    setWorkspaceView("timeline");
    setSelectedNodeId("");
    setSelectedEdgeId("");
    setSelectedTimelineKey("");
    setIsInspectorDrawerOpen(false);
    setInspectorMode("snapshot");
  }, [filters.actorId]);

  useEffect(() => {
    if (!selectedTimelineKey) {
      return;
    }

    if (!filteredTimelineRows.some((row) => row.key === selectedTimelineKey)) {
      setSelectedTimelineKey("");
    }
  }, [filteredTimelineRows, selectedTimelineKey]);

  useEffect(() => {
    if (!currentGraph) {
      setSelectedNodeId("");
      setSelectedEdgeId("");
      return;
    }

    if (
      selectedNodeId &&
      !currentGraph.nodes.some((node) => node.nodeId === selectedNodeId)
    ) {
      setSelectedNodeId("");
    }

    if (
      selectedEdgeId &&
      !currentGraph.edges.some((edge) => edge.edgeId === selectedEdgeId)
    ) {
      setSelectedEdgeId("");
    }
  }, [currentGraph, selectedEdgeId, selectedNodeId]);

  useEffect(() => {
    if (inspectorMode === "timeline" && !selectedTimelineRecord) {
      setInspectorMode("snapshot");
    }
  }, [inspectorMode, selectedTimelineRecord]);

  return (
    <PageContainer
      title="Runtime Explorer"
      content="Inspect runtime actor snapshots, filter execution history, and switch across enriched, subgraph, and edges-only topology views."
    >
      <ProCard
        title="Runtime actor query"
        {...moduleCardProps}
        extra={
          <Space wrap>
            <Button onClick={() => history.push(buildRuntimeRunsHref())}>
              Open Runtime Runs
            </Button>
            <Button onClick={() => history.push(buildRuntimeWorkflowsHref())}>
              Open Runtime Workflows
            </Button>
          </Space>
        }
      >
        {recentActorRuns.length ? (
          <div style={{ marginBottom: 16 }}>
            <SectionHeader
              title="Recent runs"
              description="Reuse a recently observed runtime actor without copying it by hand."
            />
            <Space wrap style={{ marginTop: 12 }}>
              {recentActorRuns.map((entry) => (
                <Button
                  key={entry.id}
                  onClick={() => loadActorIntoExplorer(entry.actorId)}
                  title={entry.actorId}
                >
                  {`${entry.workflowName} · ${entry.actorId}`}
                </Button>
              ))}
            </Space>
          </div>
        ) : (
          <Alert
            style={{ marginBottom: 16 }}
            type="info"
            showIcon
            title="No recent runs yet"
            description="Run a workflow once, or open Runtime Runs, and the latest actorIds will appear here for one-click lookup."
          />
        )}
        <ProForm<ActorPageState>
          formRef={formRef}
          layout="vertical"
          initialValues={initialState}
          onFinish={async (values) => {
            setFilters({
              actorId: (values.actorId ?? "").trim(),
              timelineTake: values.timelineTake,
              graphDepth: values.graphDepth,
              graphTake: values.graphTake,
              graphDirection: values.graphDirection,
              edgeTypes: values.edgeTypes ?? [],
            });
            return true;
          }}
          submitter={{
            render: (props) => (
              <Space wrap>
                <Button type="primary" onClick={() => props.form?.submit?.()}>
                  Load actor
                </Button>
                <Button
                  onClick={() => {
                    formRef.current?.setFieldsValue(initialState);
                    timelineFormRef.current?.setFieldsValue(
                      defaultTimelineFilters
                    );
                    graphControlFormRef.current?.setFieldsValue({
                      graphViewMode: initialGraphViewMode,
                    });
                    setFilters(initialState);
                    setGraphViewMode(initialGraphViewMode);
                    setWorkspaceView("timeline");
                    setTimelineFilters(defaultTimelineFilters);
                  }}
                >
                  Reset filters
                </Button>
                {filters.actorId ? (
                  <Tag color="processing">{filters.actorId}</Tag>
                ) : null}
              </Space>
            ),
          }}
        >
          <Row gutter={[16, 16]}>
            <Col xs={24} lg={10}>
              <ProFormText
                name="actorId"
                label="ActorId"
                placeholder="Workflow:19fe1b04"
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="timelineTake"
                label="Timeline take"
                min={10}
                max={500}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="graphDepth"
                label="Graph depth"
                min={1}
                max={8}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={8} lg={4}>
              <ProFormDigit
                name="graphTake"
                label="Graph take"
                min={10}
                max={500}
                fieldProps={{ precision: 0 }}
              />
            </Col>
            <Col xs={24} md={12} lg={6}>
              <ProFormSelect<ActorGraphDirection>
                name="graphDirection"
                label="Graph direction"
                options={[
                  { label: "Both", value: "Both" },
                  { label: "Outbound", value: "Outbound" },
                  { label: "Inbound", value: "Inbound" },
                ]}
              />
            </Col>
            <Col xs={24} md={12} lg={10}>
              <ProFormSelect<string[]>
                name="edgeTypes"
                label="Edge types"
                options={availableEdgeTypes.map((edgeType) => ({
                  label: edgeType,
                  value: edgeType,
                }))}
                fieldProps={{
                  mode: "multiple",
                  allowClear: true,
                  placeholder: "Filter graph edge types",
                }}
              />
            </Col>
          </Row>
        </ProForm>
      </ProCard>

      {!filters.actorId ? (
        <ProCard style={{ marginTop: 16 }} {...moduleCardProps}>
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="Provide a runtime actorId, or choose one from Recent runs, to load actor data."
          />
        </ProCard>
      ) : null}

      {filters.actorId ? (
        <>
          {snapshotRecord ? (
            <Row gutter={[16, 16]} style={{ marginTop: 16 }}>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="Completed steps"
                    value={`${snapshotRecord.completedSteps}/${snapshotRecord.totalSteps}`}
                  />
                </ProCard>
              </Col>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="Role replies"
                    value={snapshotRecord.roleReplyCount}
                  />
                </ProCard>
              </Col>
              <Col xs={24} md={8} style={stretchColumnStyle}>
                <ProCard {...moduleCardProps} style={fillCardStyle}>
                  <Statistic
                    title="State version"
                    value={snapshotRecord.stateVersion}
                  />
                </ProCard>
              </Col>
            </Row>
          ) : null}

          <ProCard
            title="Actor workspace"
            style={{ marginTop: 16 }}
            {...moduleCardProps}
            bodyStyle={workspaceCardBodyStyle}
            extra={
              <Space wrap size={[8, 8]}>
                {snapshotRecord ? (
                  <>
                    <Tag color={executionTagColorMap[snapshotRecord.executionStatus]}>
                      {snapshotRecord.executionStatus === "success"
                        ? "Healthy"
                        : snapshotRecord.executionStatus === "error"
                        ? "Error"
                        : "Unknown"}
                    </Tag>
                    {snapshotRecord.workflowName ? (
                      <Tag>{snapshotRecord.workflowName}</Tag>
                    ) : null}
                  </>
                ) : null}
                <Button onClick={handleOpenInspector}>Inspector</Button>
              </Space>
            }
          >
            <div style={workspacePanelStyle}>
              <Tabs
                activeKey={workspaceView}
                items={[
                  {
                    key: "timeline",
                    label: `Timeline (${filteredTimelineRows.length})`,
                    children: (
                      <div style={workspacePanelStyle}>
                        <ProForm<ActorTimelineFilters>
                          formRef={timelineFormRef}
                          layout="vertical"
                          initialValues={defaultTimelineFilters}
                          submitter={false}
                          onValuesChange={(_, values) => {
                            setTimelineFilters({
                              stages: values.stages ?? [],
                              eventTypes: values.eventTypes ?? [],
                              stepTypes: values.stepTypes ?? [],
                              query: values.query ?? "",
                              errorsOnly: Boolean(values.errorsOnly),
                            });
                          }}
                        >
                          <Row gutter={[16, 16]}>
                            <Col xs={24} md={12} xl={8}>
                              <ProFormText
                                name="query"
                                label="Search"
                                placeholder="Search message, event type, step or payload"
                              />
                            </Col>
                            <Col xs={24} md={12} xl={5}>
                              <ProFormSelect<string[]>
                                name="stages"
                                label="Stages"
                                options={timelineStageOptions}
                                fieldProps={{
                                  mode: "multiple",
                                  allowClear: true,
                                  placeholder: "All stages",
                                }}
                              />
                            </Col>
                            <Col xs={24} md={12} xl={5}>
                              <ProFormSelect<string[]>
                                name="eventTypes"
                                label="Event types"
                                options={timelineEventTypeOptions}
                                fieldProps={{
                                  mode: "multiple",
                                  allowClear: true,
                                  placeholder: "All event types",
                                }}
                              />
                            </Col>
                            <Col xs={24} md={12} xl={4}>
                              <ProFormSelect<string[]>
                                name="stepTypes"
                                label="Step types"
                                options={timelineStepTypeOptions}
                                fieldProps={{
                                  mode: "multiple",
                                  allowClear: true,
                                  placeholder: "All step types",
                                }}
                              />
                            </Col>
                            <Col xs={24} md={12} xl={2}>
                              <ProFormCheckbox
                                name="errorsOnly"
                                label=" "
                                tooltip="Only show error rows"
                              >
                                Errors only
                              </ProFormCheckbox>
                            </Col>
                          </Row>
                        </ProForm>

                        {timelineQuery.isError ? (
                          <Alert
                            showIcon
                            type="error"
                            title="Failed to load timeline"
                            description={describeError(timelineQuery.error)}
                          />
                        ) : null}

                        <ProTable<ActorTimelineRow>
                          rowKey="key"
                          search={false}
                          options={false}
                          columns={timelineColumns}
                          dataSource={filteredTimelineRows}
                          loading={timelineQuery.isLoading}
                          pagination={{ pageSize: 8, showSizeChanger: false }}
                          cardProps={compactTableCardProps}
                          scroll={{ x: 1460, y: 540 }}
                          onRow={(record) => ({
                            onClick: () => {
                              setWorkspaceView("timeline");
                              setSelectedTimelineKey(record.key);
                              setInspectorMode("timeline");
                              setIsInspectorDrawerOpen(true);
                            },
                          })}
                          rowClassName={(record) =>
                            record.key === selectedTimelineKey
                              ? "ant-table-row-selected"
                              : ""
                          }
                          locale={{
                            emptyText: (
                              <Empty
                                image={Empty.PRESENTED_IMAGE_SIMPLE}
                                description="No timeline rows match the current filters."
                              />
                            ),
                          }}
                        />
                      </div>
                    ),
                  },
                  {
                    key: "graph",
                    label: `Graph (${currentGraph?.nodes.length ?? 0})`,
                    children: (
                      <div style={workspacePanelStyle}>
                        <div style={embeddedPanelStyle}>
                          <Space
                            direction="vertical"
                            size={12}
                            style={{ width: "100%" }}
                          >
                            <SectionHeader
                              title="Graph controls"
                              description="Choose the topology slice you want to inspect, then click nodes or edges to open the inspector."
                            />
                            <ProForm<GraphControlValues>
                              formRef={graphControlFormRef}
                              layout="vertical"
                              initialValues={{ graphViewMode }}
                              submitter={false}
                              onValuesChange={(_, values) => {
                                if (values.graphViewMode) {
                                  setGraphViewMode(values.graphViewMode);
                                }
                              }}
                            >
                              <ProFormSelect<ActorGraphViewMode>
                                name="graphViewMode"
                                label="Graph view"
                                options={graphViewOptions}
                              />
                            </ProForm>

                            {graphSummary ? (
                              <>
                                <div style={summaryMetricGridStyle}>
                                  <SummaryMetric
                                    label="Nodes"
                                    value={graphSummary.nodeCount}
                                  />
                                  <SummaryMetric
                                    label="Edges"
                                    value={graphSummary.edgeCount}
                                  />
                                </div>
                                <div style={summaryFieldGridStyle}>
                                  <SummaryField
                                    label="View"
                                    value={graphViewLabels[graphSummary.mode]}
                                  />
                                  <SummaryField
                                    label="Direction"
                                    value={graphSummary.direction}
                                  />
                                  <SummaryField
                                    label="Depth"
                                    value={graphSummary.depth}
                                  />
                                  <SummaryField
                                    label="Take"
                                    value={graphSummary.take}
                                  />
                                  <SummaryField
                                    label="Root node"
                                    value={graphSummary.rootNodeId}
                                    copyable
                                  />
                                  <SummaryField
                                    label="Edge types"
                                    value={graphSummary.edgeTypes}
                                  />
                                </div>
                              </>
                            ) : (
                              <Typography.Text type="secondary">
                                No graph summary is available yet for this actor.
                              </Typography.Text>
                            )}
                          </Space>
                        </div>

                        {currentGraphError ? (
                          <Alert
                            showIcon
                            type="error"
                            title="Failed to load graph topology"
                            description={describeError(currentGraphError)}
                          />
                        ) : currentGraph && currentGraph.nodes.length > 0 ? (
                          <div style={graphCanvasShellStyle}>
                            <GraphCanvas
                              nodes={graphElements.nodes}
                              edges={graphElements.edges}
                              selectedNodeId={selectedNodeId}
                              selectedEdgeId={selectedEdgeId}
                              onNodeSelect={(nodeId) => {
                                setWorkspaceView("graph");
                                setSelectedNodeId(nodeId);
                                setSelectedEdgeId("");
                                setInspectorMode("graph");
                                setIsInspectorDrawerOpen(true);
                              }}
                              onEdgeSelect={(edgeId) => {
                                setWorkspaceView("graph");
                                setSelectedEdgeId(edgeId);
                                setSelectedNodeId("");
                                setInspectorMode("graph");
                                setIsInspectorDrawerOpen(true);
                              }}
                              height={620}
                            />
                          </div>
                        ) : (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="No graph topology returned for this actor."
                          />
                        )}
                      </div>
                    ),
                  },
                ]}
                onChange={(key) => setWorkspaceView(key as ActorWorkspaceView)}
              />
            </div>
          </ProCard>

          <Drawer
            destroyOnHidden
            open={isInspectorDrawerOpen}
            styles={{ body: drawerBodyStyle }}
            title={inspectorTitle}
            size={520}
            onClose={() => setIsInspectorDrawerOpen(false)}
          >
            <div style={drawerScrollStyle}>
              <div style={cardStackStyle}>
                <div style={embeddedPanelStyle}>
                  <Space direction="vertical" size={12} style={{ width: "100%" }}>
                    <SectionHeader
                      title="Actor digest"
                      description="Current actor status and the latest runtime facts exposed by the backend."
                    />

                    {snapshotQuery.isError ? (
                      <Alert
                        showIcon
                        type="error"
                        title="Failed to load actor"
                        description={describeError(snapshotQuery.error)}
                      />
                    ) : snapshotRecord ? (
                      <>
                        <Space wrap size={[6, 6]}>
                          <Tag
                            color={
                              executionTagColorMap[snapshotRecord.executionStatus]
                            }
                          >
                            {snapshotRecord.executionStatus === "success"
                              ? "Healthy"
                              : snapshotRecord.executionStatus === "error"
                              ? "Error"
                              : "Unknown"}
                          </Tag>
                          {snapshotRecord.workflowName ? (
                            <Tag>{snapshotRecord.workflowName}</Tag>
                          ) : null}
                        </Space>

                        <div style={summaryMetricGridStyle}>
                          <SummaryMetric
                            label="Completion"
                            tone={
                              snapshotRecord.executionStatus === "success"
                                ? "success"
                                : snapshotRecord.executionStatus === "error"
                                ? "error"
                                : "default"
                            }
                            value={`${snapshotRecord.completedSteps}/${snapshotRecord.totalSteps}`}
                          />
                          <SummaryMetric
                            label="Role replies"
                            value={snapshotRecord.roleReplyCount}
                          />
                          <SummaryMetric
                            label="State version"
                            value={snapshotRecord.stateVersion}
                          />
                          <SummaryMetric
                            label="Updated"
                            value={formatDateTime(snapshotRecord.lastUpdatedAt)}
                          />
                        </div>

                        <div style={summaryFieldGridStyle}>
                          <SummaryField
                            label="ActorId"
                            value={snapshotRecord.actorId}
                            copyable
                          />
                          <SummaryField
                            label="Workflow"
                            value={snapshotRecord.workflowName || "n/a"}
                          />
                          <SummaryField
                            label="Last command"
                            value={snapshotRecord.lastCommandId || "n/a"}
                            copyable
                          />
                        </div>

                        <div style={sectionDividerStyle}>
                          <div>
                            <Typography.Text style={summaryFieldLabelStyle}>
                              Last output
                            </Typography.Text>
                            <Typography.Paragraph
                              ellipsis={{
                                rows: 3,
                                expandable: true,
                                symbol: "more",
                              }}
                              style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
                            >
                              {snapshotRecord.lastOutput || "No output recorded."}
                            </Typography.Paragraph>
                          </div>

                          {snapshotRecord.lastError ? (
                            <div>
                              <Typography.Text style={summaryFieldLabelStyle}>
                                Last error
                              </Typography.Text>
                              <Typography.Paragraph
                                style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
                                type="danger"
                              >
                                {snapshotRecord.lastError}
                              </Typography.Paragraph>
                            </div>
                          ) : null}
                        </div>
                      </>
                    ) : (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No actor snapshot available."
                      />
                    )}
                  </Space>
                </div>

                {inspectorMode === "timeline" ? (
                  <div style={embeddedPanelStyle}>
                    <Space direction="vertical" size={12} style={{ width: "100%" }}>
                      <SectionHeader
                        title="Timeline detail"
                        description="The currently selected execution event from the timeline."
                      />

                      {selectedTimelineRecord ? (
                        <>
                          <Space wrap size={[6, 6]}>
                            <Tag
                              color={
                                selectedTimelineRecord.timelineStatus === "error"
                                  ? "error"
                                  : selectedTimelineRecord.timelineStatus ===
                                    "success"
                                  ? "success"
                                  : selectedTimelineRecord.timelineStatus ===
                                    "processing"
                                  ? "processing"
                                  : "default"
                              }
                            >
                              {selectedTimelineRecord.timelineStatus}
                            </Tag>
                            <Tag>{selectedTimelineRecord.stage || "n/a"}</Tag>
                            {selectedTimelineRecord.eventType ? (
                              <Tag>{selectedTimelineRecord.eventType}</Tag>
                            ) : null}
                          </Space>

                          <div style={summaryFieldGridStyle}>
                            <SummaryField
                              label="Timestamp"
                              value={formatDateTime(selectedTimelineRecord.timestamp)}
                            />
                            <SummaryField
                              label="Actor"
                              value={selectedTimelineRecord.agentId || "n/a"}
                            />
                            <SummaryField
                              label="Step"
                              value={selectedTimelineRecord.stepId || "n/a"}
                            />
                            <SummaryField
                              label="Step type"
                              value={selectedTimelineRecord.stepType || "n/a"}
                            />
                          </div>

                          <div style={sectionDividerStyle}>
                            <div>
                              <Typography.Text style={summaryFieldLabelStyle}>
                                Message
                              </Typography.Text>
                              <Typography.Paragraph
                                style={{ margin: "8px 0 0", whiteSpace: "pre-wrap" }}
                              >
                                {selectedTimelineRecord.message || "No message recorded."}
                              </Typography.Paragraph>
                            </div>

                            <div>
                              <Typography.Text style={summaryFieldLabelStyle}>
                                Structured data
                              </Typography.Text>
                              <div style={{ marginTop: 8 }}>
                                {selectedTimelineRecord.dataCount > 0 ? (
                                  renderPropertyList(selectedTimelineRecord.data)
                                ) : (
                                  <Typography.Text type="secondary">
                                    No structured data was attached to this timeline
                                    entry.
                                  </Typography.Text>
                                )}
                              </div>
                            </div>

                            {selectedTimelineRecord.dataCount > 0 ? (
                              <div>
                                <Typography.Text style={summaryFieldLabelStyle}>
                                  Raw JSON
                                </Typography.Text>
                                <pre style={codeBlockStyle}>
                                  {JSON.stringify(
                                    selectedTimelineRecord.data,
                                    null,
                                    2
                                  )}
                                </pre>
                              </div>
                            ) : null}
                          </div>
                        </>
                      ) : (
                        <Empty
                          image={Empty.PRESENTED_IMAGE_SIMPLE}
                          description="Select a timeline row to inspect its details."
                        />
                      )}
                    </Space>
                  </div>
                ) : null}

                {inspectorMode === "graph" ? (
                  <>
                    <div style={embeddedPanelStyle}>
                      <Space direction="vertical" size={12} style={{ width: "100%" }}>
                        <SectionHeader
                          title="Graph summary"
                          description="Current topology slice for the selected actor."
                        />

                        {currentGraphError ? (
                          <Alert
                            showIcon
                            type="error"
                            title="Failed to load graph view"
                            description={describeError(currentGraphError)}
                          />
                        ) : graphSummary ? (
                          <>
                            <div style={summaryMetricGridStyle}>
                              <SummaryMetric
                                label="Nodes"
                                value={graphSummary.nodeCount}
                              />
                              <SummaryMetric
                                label="Edges"
                                value={graphSummary.edgeCount}
                              />
                            </div>
                            <div style={summaryFieldGridStyle}>
                              <SummaryField
                                label="View"
                                value={graphViewLabels[graphSummary.mode]}
                              />
                              <SummaryField
                                label="Direction"
                                value={graphSummary.direction}
                              />
                              <SummaryField
                                label="Depth"
                                value={graphSummary.depth}
                              />
                              <SummaryField
                                label="Take"
                                value={graphSummary.take}
                              />
                              <SummaryField
                                label="Root node"
                                value={graphSummary.rootNodeId}
                                copyable
                              />
                              <SummaryField
                                label="Edge types"
                                value={graphSummary.edgeTypes}
                              />
                            </div>
                          </>
                        ) : (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="No graph summary available yet."
                          />
                        )}
                      </Space>
                    </div>

                    <div style={embeddedPanelStyle}>
                      <Space direction="vertical" size={12} style={{ width: "100%" }}>
                        <SectionHeader
                          title="Selection detail"
                          description="Node or edge details from the runtime graph."
                        />

                        {selectedNodeRecord ? (
                          <>
                            <Space wrap size={[6, 6]}>
                              <Tag color="processing">Node</Tag>
                              <Tag>{selectedNodeRecord.nodeType || "n/a"}</Tag>
                              {selectedNodeRecord.isRoot ? <Tag>Root</Tag> : null}
                            </Space>

                            <div style={summaryFieldGridStyle}>
                              <SummaryField
                                label="NodeId"
                                value={selectedNodeRecord.nodeId}
                                copyable
                              />
                              <SummaryField
                                label="Primary label"
                                value={selectedNodeRecord.primaryLabel}
                              />
                              <SummaryField
                                label="Role"
                                value={selectedNodeRecord.properties.role || "n/a"}
                              />
                              <SummaryField
                                label="Updated"
                                value={formatDateTime(selectedNodeRecord.updatedAt)}
                              />
                              <SummaryField
                                label="Property count"
                                value={selectedNodeRecord.propertyCount}
                              />
                            </div>

                            <div style={sectionDividerStyle}>
                              <div>
                                <Typography.Text style={summaryFieldLabelStyle}>
                                  Properties
                                </Typography.Text>
                                <div style={{ marginTop: 8 }}>
                                  {renderPropertyList(selectedNodeRecord.properties)}
                                </div>
                              </div>
                            </div>
                          </>
                        ) : selectedEdgeRecord ? (
                          <>
                            <Space wrap size={[6, 6]}>
                              <Tag color="purple">Edge</Tag>
                              <Tag>{selectedEdgeRecord.edgeType || "n/a"}</Tag>
                            </Space>

                            <div style={summaryFieldGridStyle}>
                              <SummaryField
                                label="EdgeId"
                                value={selectedEdgeRecord.edgeId}
                                copyable
                              />
                              <SummaryField
                                label="From"
                                value={selectedEdgeRecord.fromNodeId}
                                copyable
                              />
                              <SummaryField
                                label="To"
                                value={selectedEdgeRecord.toNodeId}
                                copyable
                              />
                              <SummaryField
                                label="Updated"
                                value={formatDateTime(selectedEdgeRecord.updatedAt)}
                              />
                              <SummaryField
                                label="Property count"
                                value={selectedEdgeRecord.propertyCount}
                              />
                            </div>

                            <div style={sectionDividerStyle}>
                              <div>
                                <Typography.Text style={summaryFieldLabelStyle}>
                                  Properties
                                </Typography.Text>
                                <div style={{ marginTop: 8 }}>
                                  {renderPropertyList(selectedEdgeRecord.properties)}
                                </div>
                              </div>
                            </div>
                          </>
                        ) : (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="Select a node or edge from the graph to inspect its details."
                          />
                        )}
                      </Space>
                    </div>
                  </>
                ) : null}
              </div>
            </div>
          </Drawer>
        </>
      ) : null}
    </PageContainer>
  );
};

export default ActorsPage;
