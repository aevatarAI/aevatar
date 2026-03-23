import type {
  WorkflowActorGraphEdge,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";

export type TimelineStatus = "processing" | "success" | "error" | "default";

export type ActorTimelineRow = WorkflowActorTimelineItem & {
  key: string;
  timelineStatus: TimelineStatus;
  dataSummary: string;
  dataCount: number;
};

export type ActorTimelineFilters = {
  stages: string[];
  eventTypes: string[];
  stepTypes: string[];
  query: string;
  errorsOnly: boolean;
};

export function deriveTimelineStatus(stage: string): TimelineStatus {
  const normalized = stage.toLowerCase();
  if (normalized.includes("error") || normalized.includes("failed")) {
    return "error";
  }
  if (
    normalized.includes("completed") ||
    normalized.includes("finish") ||
    normalized.includes("end")
  ) {
    return "success";
  }
  if (
    normalized.includes("start") ||
    normalized.includes("running") ||
    normalized.includes("wait")
  ) {
    return "processing";
  }
  return "default";
}

function summarizeTimelineData(data: Record<string, string>): {
  dataSummary: string;
  dataCount: number;
} {
  const entries = Object.entries(data);
  if (entries.length === 0) {
    return {
      dataSummary: "",
      dataCount: 0,
    };
  }

  const preview = entries
    .slice(0, 2)
    .map(([key, value]) => `${key}=${value}`)
    .join(" · ");

  return {
    dataSummary:
      entries.length > 2 ? `${preview} · +${entries.length - 2} more` : preview,
    dataCount: entries.length,
  };
}

export function buildTimelineRows(
  items: WorkflowActorTimelineItem[]
): ActorTimelineRow[] {
  return items.map((item, index) => ({
    ...item,
    key: `${item.timestamp}-${index}`,
    timelineStatus: deriveTimelineStatus(item.stage),
    ...summarizeTimelineData(item.data),
  }));
}

export function filterTimelineRows(
  rows: ActorTimelineRow[],
  filters: ActorTimelineFilters
): ActorTimelineRow[] {
  const query = filters.query.trim().toLowerCase();

  return rows.filter((row) => {
    if (filters.errorsOnly && row.timelineStatus !== "error") {
      return false;
    }

    if (filters.stages.length > 0 && !filters.stages.includes(row.stage)) {
      return false;
    }

    if (
      filters.eventTypes.length > 0 &&
      !filters.eventTypes.includes(row.eventType)
    ) {
      return false;
    }

    if (
      filters.stepTypes.length > 0 &&
      !filters.stepTypes.includes(row.stepType)
    ) {
      return false;
    }

    if (!query) {
      return true;
    }

    return [
      row.stage,
      row.message,
      row.stepId,
      row.stepType,
      row.agentId,
      row.eventType,
      row.dataSummary,
    ]
      .join(" ")
      .toLowerCase()
      .includes(query);
  });
}

export function deriveSubgraphFromEdges(
  edges: WorkflowActorGraphEdge[],
  rootNodeId: string
): WorkflowActorGraphSubgraph {
  const nodesById = new Map<string, WorkflowActorGraphNode>();

  function ensureNode(nodeId: string): void {
    if (!nodeId || nodesById.has(nodeId)) {
      return;
    }

    nodesById.set(nodeId, {
      nodeId,
      nodeType: nodeId === rootNodeId ? "RootActor" : "DerivedActor",
      updatedAt: "",
      properties: {
        nodeId,
        source: "graph-edges",
      },
    });
  }

  ensureNode(rootNodeId);

  for (const edge of edges) {
    ensureNode(edge.fromNodeId);
    ensureNode(edge.toNodeId);
  }

  return {
    rootNodeId,
    nodes: [...nodesById.values()],
    edges,
  };
}
