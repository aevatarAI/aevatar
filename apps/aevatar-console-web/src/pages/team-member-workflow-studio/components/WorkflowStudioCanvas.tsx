import type { Edge, Node } from '@xyflow/react';
import React from 'react';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import type {
  StudioGraphEdgeData,
  StudioGraphNodeData,
} from '@/shared/studio/graph';
import WorkflowStudioEmptyState from './WorkflowStudioEmptyState';

type WorkflowStudioCanvasProps = {
  readonly addFirstStepDisabled?: boolean;
  readonly edges: readonly Edge<StudioGraphEdgeData>[];
  readonly emptyDescription?: string;
  readonly nodes: readonly Node<StudioGraphNodeData>[];
  readonly onAddFirstStep?: () => void;
  readonly onCanvasSelect?: () => void;
  readonly onConnectNodes?: (
    sourceNodeId: string,
    targetNodeId: string,
  ) => void;
  readonly onDeleteEdges?: (edgeIds: string[]) => Promise<void> | void;
  readonly onDeleteNodes?: (nodeIds: string[]) => Promise<void> | void;
  readonly onEdgeSelect?: (edgeId: string) => void;
  readonly onNodeLayoutChange?: (nodes: Node<StudioGraphNodeData>[]) => void;
  readonly onNodeSelect?: (nodeId: string) => void;
  readonly selectedEdgeId?: string;
  readonly selectedNodeId?: string;
};

const WorkflowStudioCanvas: React.FC<WorkflowStudioCanvasProps> = React.memo(
  function WorkflowStudioCanvas({
    addFirstStepDisabled,
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
  }) {
    const autoFitKey = React.useMemo(
      () =>
        JSON.stringify({
          edges: edges.map((edge) => edge.id),
          nodes: nodes.map((node) => node.id),
        }),
      [edges, nodes],
    );
    const overlayContent = React.useMemo(
      () =>
        nodes.length === 0 ? (
          <WorkflowStudioEmptyState
            description={emptyDescription}
            disabled={addFirstStepDisabled}
            onAddFirstStep={onAddFirstStep}
          />
        ) : null,
      [addFirstStepDisabled, emptyDescription, nodes.length, onAddFirstStep],
    );

    return (
      <div
        data-testid="workflow-studio-canvas"
        style={{
          background: '#eef3f8',
          flex: 1,
          minHeight: 0,
          padding: 10,
          position: 'relative',
        }}
      >
        <GraphCanvas
          autoFitKey={autoFitKey}
          bottomInset={12}
          edges={edges}
          height="100%"
          nodes={nodes}
          overlayContent={overlayContent}
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
  },
);

export default WorkflowStudioCanvas;
