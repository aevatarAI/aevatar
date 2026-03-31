export interface WorkflowActorSnapshot {
  actorId: string;
  workflowName: string;
  lastCommandId: string;
  completionStatusValue: number;
  stateVersion: number;
  lastEventId: string;
  lastUpdatedAt: string;
  lastSuccess: boolean | null;
  lastOutput: string;
  lastError: string;
  totalSteps: number;
  requestedSteps: number;
  completedSteps: number;
  roleReplyCount: number;
}

export interface WorkflowActorTimelineItem {
  timestamp: string;
  stage: string;
  message: string;
  agentId: string;
  stepId: string;
  stepType: string;
  eventType: string;
  data: Record<string, string>;
}

export interface WorkflowActorGraphNode {
  nodeId: string;
  nodeType: string;
  updatedAt: string;
  properties: Record<string, string>;
}

export interface WorkflowActorGraphEdge {
  edgeId: string;
  fromNodeId: string;
  toNodeId: string;
  edgeType: string;
  updatedAt: string;
  properties: Record<string, string>;
}

export interface WorkflowActorGraphSubgraph {
  rootNodeId: string;
  nodes: WorkflowActorGraphNode[];
  edges: WorkflowActorGraphEdge[];
}

export interface WorkflowActorGraphEnrichedSnapshot {
  snapshot: WorkflowActorSnapshot;
  subgraph: WorkflowActorGraphSubgraph;
}
