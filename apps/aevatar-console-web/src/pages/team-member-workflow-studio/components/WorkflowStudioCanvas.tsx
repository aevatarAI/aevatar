import type { Edge, Node } from "@xyflow/react";
import React from "react";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from "@/shared/studio/graph";
import WorkflowStudioEmptyState from "./WorkflowStudioEmptyState";

type WorkflowStudioCanvasProps = {
  readonly edges: readonly Edge<StudioGraphEdgeData>[];
  readonly emptyDescription?: string;
  readonly nodes: readonly Node<StudioGraphNodeData>[];
  readonly onAddFirstStep?: () => void;
  readonly onCanvasSelect?: () => void;
  readonly onConnectNodes?: (sourceNodeId: string, targetNodeId: string) => void;
  readonly onDeleteEdges?: (edgeIds: string[]) => Promise<void> | void;
  readonly onDeleteNodes?: (nodeIds: string[]) => Promise<void> | void;
  readonly onEdgeSelect?: (edgeId: string) => void;
  readonly onNodeLayoutChange?: (nodes: Node<StudioGraphNodeData>[]) => void;
  readonly onNodeSelect?: (nodeId: string) => void;
  readonly selectedEdgeId?: string;
  readonly selectedNodeId?: string;
};

const WorkflowStudioCanvas: React.FC<WorkflowStudioCanvasProps> = ({
  edges,
  emptyDescription,
  nodes,
  onAddFirstStep,
  onCanvasSelect,
  onConnectNodes,
  onDeleteEdges,
  onDeleteNodes,
  onEdgeSelect,
  onNodeLayoutChange,
  onNodeSelect,
  selectedEdgeId,
  selectedNodeId,
}) => (
  <div
    data-testid="workflow-studio-canvas"
    style={{
      background: "#fbfbfc",
      flex: 1,
      minHeight: 0,
      position: "relative",
    }}
  >
    <GraphCanvas
      autoFitKey={JSON.stringify({
        edges: edges.map((edge) => edge.id),
        nodes: nodes.map((node) => node.id),
      })}
      bottomInset={12}
      edges={[...edges]}
      height="100%"
      nodes={[...nodes]}
      overlayContent={
        nodes.length === 0 ? (
          <WorkflowStudioEmptyState
            description={emptyDescription}
            onAddFirstStep={onAddFirstStep}
          />
        ) : null
      }
      onCanvasSelect={onCanvasSelect}
      onConnectNodes={onConnectNodes}
      onDeleteEdges={onDeleteEdges}
      onDeleteNodes={onDeleteNodes}
      onEdgeSelect={onEdgeSelect}
      onNodeLayoutChange={onNodeLayoutChange as (nodes: Node[]) => void}
      onNodeSelect={onNodeSelect}
      selectedEdgeId={selectedEdgeId}
      selectedNodeId={selectedNodeId}
      variant="studio"
    />
  </div>
);

export default WorkflowStudioCanvas;
