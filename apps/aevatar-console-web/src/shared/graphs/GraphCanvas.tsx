import {
  ApartmentOutlined,
  ApiOutlined,
  AppstoreOutlined,
  CodeOutlined,
  DatabaseOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  applyNodeChanges,
  Background,
  BackgroundVariant,
  Controls,
  type Edge,
  type FitViewOptions,
  Handle,
  MiniMap,
  type Node,
  type NodeChange,
  type NodeProps,
  Position,
  ReactFlow,
  type ReactFlowInstance,
  useEdgesState,
  useNodesState,
  useStore,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import React, { useEffect, useLayoutEffect, useMemo } from 'react';
import { t } from '@/shared/i18n/messages';
import {
  getStudioGraphCategory,
  type StudioGraphNodeData,
} from '@/shared/studio/graph';

type GraphCanvasProps = {
  autoFitKey?: string;
  nodes: Node[];
  edges: Edge[];
  height?: number | string;
  bottomInset?: number;
  overlayContent?: React.ReactNode;
  selectedNodeId?: string;
  selectedEdgeId?: string;
  variant?: 'default' | 'studio';
  onNodeSelect?: (nodeId: string) => void;
  onEdgeSelect?: (edgeId: string) => void;
  onCanvasSelect?: () => void;
  onCanvasContextMenu?: (position: {
    clientX: number;
    clientY: number;
    flowX: number;
    flowY: number;
  }) => void;
  onConnectNodes?: (sourceId: string, targetId: string) => void;
  onNodeLayoutChange?: (nodes: Node[]) => void;
  onDeleteEdges?: (edgeIds: string[]) => Promise<void> | void;
  onDeleteNodes?: (nodeIds: string[]) => Promise<void> | void;
};

const SELF_MANAGED_SELECTION_CLASS = 'graph-canvas-self-managed-selection';
const selfManagedSelectionCss = `
.react-flow__node.${SELF_MANAGED_SELECTION_CLASS},
.react-flow__node.${SELF_MANAGED_SELECTION_CLASS}.selected,
.react-flow__node.${SELF_MANAGED_SELECTION_CLASS}:focus,
.react-flow__node.${SELF_MANAGED_SELECTION_CLASS}:focus-visible {
  background: transparent !important;
  border: none !important;
  box-shadow: none !important;
  outline: none !important;
}
`;
const STUDIO_FIT_VIEW_OPTIONS = {
  duration: 0,
  maxZoom: 1.06,
  minZoom: 0.34,
  padding: 0.18,
} as const satisfies FitViewOptions;
const STUDIO_CANVAS_MIN_ZOOM = 0.28;
const STUDIO_CANVAS_MAX_ZOOM = 1.55;
const STUDIO_FIT_VIEW_ATTEMPT_COUNT = 3;
const STUDIO_NODE_WIDTH = 268;
const STUDIO_NODE_COMPACT_WIDTH = 244;
const STUDIO_NODE_COMPACT_ZOOM = 0.48;
const SELECTED_EDGE_COLOR = '#1677ff';
const SELECTED_EDGE_FILTER = 'drop-shadow(0 0 3px rgba(22, 119, 255, 0.55))';
const SELECTED_EDGE_STROKE_WIDTH = 4;
const studioCanvasCss = `
.studio-canvas {
  background: #f7f9fc;
}

.studio-canvas .react-flow__pane {
  cursor: grab;
}

.studio-canvas .react-flow__pane.dragging {
  cursor: grabbing;
}

.studio-canvas .react-flow__node {
  border-radius: 8px;
}

.studio-canvas .react-flow__controls {
  background: rgba(255, 255, 255, 0.94);
  border: 1px solid #d8e0ea;
  border-radius: 8px;
  box-shadow: 0 10px 24px rgba(15, 23, 42, 0.11);
  overflow: hidden;
}

.studio-canvas .react-flow__controls-button {
  background: transparent;
  border-bottom-color: #e7edf4;
  color: #475569;
  height: 28px;
  width: 28px;
}

.studio-canvas .react-flow__controls-button:hover,
.studio-canvas .react-flow__controls-button:focus-visible {
  background: #eef3f8;
  color: #0f172a;
}

.studio-canvas .react-flow__minimap {
  box-shadow: 0 10px 24px rgba(15, 23, 42, 0.10);
  opacity: 0.72;
  transition: opacity 140ms ease, box-shadow 140ms ease;
}

.studio-canvas .react-flow__minimap:hover,
.studio-canvas .react-flow__minimap:focus-within {
  box-shadow: 0 12px 28px rgba(15, 23, 42, 0.14);
  opacity: 1;
}

.studio-workflow-node {
  --studio-node-accent: #3b82f6;
  background: #ffffff;
  border: 1px solid #dbe3ee;
  border-left: 4px solid var(--studio-node-accent);
  border-radius: 8px;
  box-shadow: 0 14px 30px rgba(15, 23, 42, 0.10);
  color: #0f172a;
  overflow: hidden;
  position: relative;
  transition: border-color 120ms ease, box-shadow 120ms ease, transform 120ms ease;
}

.studio-workflow-node:hover {
  border-color: color-mix(in srgb, var(--studio-node-accent) 45%, #dbe3ee);
  box-shadow: 0 18px 36px rgba(15, 23, 42, 0.14);
  transform: translateY(-1px);
}

.studio-workflow-node--selected {
  border-color: var(--studio-node-accent);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--studio-node-accent) 22%, transparent), 0 20px 42px rgba(15, 23, 42, 0.16);
}

.studio-workflow-node--execution-focused:not(.studio-workflow-node--selected) {
  box-shadow: 0 0 0 2px rgba(37, 99, 235, 0.18), 0 18px 36px rgba(15, 23, 42, 0.14);
}

.studio-workflow-node__header {
  align-items: flex-start;
  display: flex;
  gap: 10px;
  padding: 12px 14px 10px;
}

.studio-workflow-node__icon {
  align-items: center;
  background: color-mix(in srgb, var(--studio-node-accent) 13%, #ffffff);
  border: 1px solid color-mix(in srgb, var(--studio-node-accent) 24%, #ffffff);
  border-radius: 7px;
  color: var(--studio-node-accent);
  display: flex;
  flex: 0 0 auto;
  height: 34px;
  justify-content: center;
  width: 34px;
}

.studio-workflow-node__title-group {
  min-width: 0;
  flex: 1 1 auto;
}

.studio-workflow-node__title {
  color: #0f172a;
  font-size: 14px;
  font-weight: 700;
  line-height: 18px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.studio-workflow-node__subtitle-row {
  align-items: center;
  color: #64748b;
  display: flex;
  flex-wrap: wrap;
  font-size: 11px;
  font-weight: 600;
  gap: 6px;
  line-height: 16px;
  margin-top: 3px;
  min-width: 0;
}

.studio-workflow-node__type {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.studio-workflow-node__branch-count {
  background: #f1f5f9;
  border: 1px solid #e2e8f0;
  border-radius: 999px;
  color: #475569;
  flex: 0 0 auto;
  padding: 1px 6px;
}

.studio-workflow-node__status {
  align-items: center;
  border-radius: 999px;
  display: inline-flex;
  flex: 0 0 auto;
  font-size: 10px;
  font-weight: 700;
  line-height: 1;
  padding: 6px 8px;
  text-transform: uppercase;
}

.studio-workflow-node__body {
  border-top: 1px solid #edf2f7;
  color: #475569;
  display: grid;
  gap: 7px;
  font-size: 12px;
  line-height: 17px;
  padding: 10px 14px 12px;
}

.studio-workflow-node__meta {
  align-items: center;
  display: grid;
  gap: 8px;
  grid-template-columns: auto minmax(0, 1fr);
  min-width: 0;
}

.studio-workflow-node__meta-label {
  color: #94a3b8;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
}

.studio-workflow-node__meta-value,
.studio-workflow-node__summary {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.studio-workflow-node__meta-value {
  color: #334155;
  font-weight: 650;
}

.studio-workflow-node__summary {
  color: #64748b;
}

.studio-workflow-node__handle {
  background: var(--studio-node-accent) !important;
  border: 2px solid #ffffff !important;
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--studio-node-accent) 55%, #ffffff);
  height: 13px !important;
  width: 13px !important;
}

.studio-workflow-node--compact .studio-workflow-node__header {
  gap: 8px;
  padding: 10px 12px 8px;
}

.studio-workflow-node--compact .studio-workflow-node__icon {
  height: 30px;
  width: 30px;
}

.studio-workflow-node--compact .studio-workflow-node__body {
  font-size: 11px;
  padding: 9px 12px 10px;
}

.studio-workflow-node--compact .studio-workflow-node__title {
  font-size: 13px;
}
`;

const STUDIO_NODE_ICON_BY_CATEGORY: Record<
  string,
  React.ComponentType<{ style?: React.CSSProperties }>
> = {
  ai: RobotOutlined,
  composition: AppstoreOutlined,
  control: ApartmentOutlined,
  data: DatabaseOutlined,
  human: UserOutlined,
  integration: ApiOutlined,
  validation: SafetyCertificateOutlined,
  custom: CodeOutlined,
};

function StudioWorkflowNode({
  data,
  selected,
}: NodeProps<Node<StudioGraphNodeData>>) {
  const category = getStudioGraphCategory(data.stepType);
  const Icon =
    STUDIO_NODE_ICON_BY_CATEGORY[category.key] ??
    STUDIO_NODE_ICON_BY_CATEGORY.custom;
  const zoom = useStore((state) => state.transform[2]);
  const compact = zoom < STUDIO_NODE_COMPACT_ZOOM;
  const width = compact ? STUDIO_NODE_COMPACT_WIDTH : STUDIO_NODE_WIDTH;
  const executionStatus = data.executionStatus;
  const executionFocused = Boolean(data.executionFocused);
  const statusColor =
    executionStatus === 'completed'
      ? '#16A34A'
      : executionStatus === 'failed'
        ? '#DC2626'
        : executionStatus === 'waiting'
          ? '#D97706'
          : executionStatus === 'active'
            ? '#2563EB'
            : '#94A3B8';
  const statusBackground =
    executionStatus === 'completed'
      ? '#DCFCE7'
      : executionStatus === 'failed'
        ? '#FEE2E2'
        : executionStatus === 'waiting'
          ? '#FEF3C7'
          : '#DBEAFE';
  const branchCountLabel =
    data.branchCount === 1
      ? t('teamMemberWorkflowStudio.graph.branchCount.one', '1 branch')
      : t(
          'teamMemberWorkflowStudio.graph.branchCount.other',
          '{count} branches',
          { count: data.branchCount },
        );

  return (
    <div
      className={[
        'studio-workflow-node',
        compact ? 'studio-workflow-node--compact' : '',
        selected ? 'studio-workflow-node--selected' : '',
        executionFocused ? 'studio-workflow-node--execution-focused' : '',
      ]
        .filter(Boolean)
        .join(' ')}
      style={
        {
          width,
          '--studio-node-accent': category.color,
        } as React.CSSProperties
      }
    >
      <Handle
        className="studio-workflow-node__handle studio-workflow-node__handle--target"
        type="target"
        position={Position.Left}
      />
      <div className="studio-workflow-node__header">
        <div className="studio-workflow-node__icon">
          <Icon style={{ fontSize: compact ? 14 : 15 }} />
        </div>
        <div className="studio-workflow-node__title-group">
          <div className="studio-workflow-node__title" title={data.stepId}>
            {data.stepId}
          </div>
          <div className="studio-workflow-node__subtitle-row">
            <span className="studio-workflow-node__type" title={data.subtitle}>
              {data.subtitle}
            </span>
            {data.branchCount > 0 ? (
              <span className="studio-workflow-node__branch-count">
                {branchCountLabel}
              </span>
            ) : null}
          </div>
        </div>
        {executionStatus && executionStatus !== 'idle' ? (
          <span
            className="studio-workflow-node__status"
            style={{
              background: statusBackground,
              color: statusColor,
            }}
          >
            {executionStatus}
          </span>
        ) : null}
      </div>
      <div className="studio-workflow-node__body">
        {data.targetRole ? (
          <div className="studio-workflow-node__meta">
            <span className="studio-workflow-node__meta-label">
              {t('teamMemberWorkflowStudio.graph.role', 'Role')}
            </span>
            <span
              className="studio-workflow-node__meta-value"
              title={data.targetRole}
            >
              {data.targetRole}
            </span>
          </div>
        ) : null}
        <div
          className="studio-workflow-node__summary"
          title={data.parametersSummary}
        >
          {data.parametersSummary}
        </div>
      </div>
      <Handle
        className="studio-workflow-node__handle studio-workflow-node__handle--source"
        type="source"
        position={Position.Right}
      />
    </div>
  );
}

const GraphCanvas: React.FC<GraphCanvasProps> = ({
  autoFitKey,
  nodes,
  edges,
  height = 420,
  bottomInset = 0,
  overlayContent,
  selectedNodeId,
  selectedEdgeId,
  variant = 'default',
  onNodeSelect,
  onEdgeSelect,
  onCanvasSelect,
  onCanvasContextMenu,
  onConnectNodes,
  onNodeLayoutChange,
  onDeleteEdges,
  onDeleteNodes,
}) => {
  const isStudioVariant = variant === 'studio';
  const [localNodes, setLocalNodes] = useNodesState(nodes);
  const [localEdges, setLocalEdges] = useEdgesState(edges);
  const [flowInstance, setFlowInstance] =
    React.useState<ReactFlowInstance | null>(null);

  useEffect(() => {
    setLocalNodes(nodes);
  }, [nodes, setLocalNodes]);

  useEffect(() => {
    setLocalEdges(edges);
  }, [edges, setLocalEdges]);

  useLayoutEffect(() => {
    if (
      !autoFitKey ||
      !flowInstance ||
      !isStudioVariant ||
      nodes.length === 0
    ) {
      return;
    }

    const readyFlowInstance = flowInstance;
    const animationFrameIds: number[] = [];
    const timeoutIds: number[] = [];
    let cancelled = false;
    let attemptCount = 0;

    function applyViewport() {
      if (cancelled) {
        return;
      }

      void readyFlowInstance.fitView(STUDIO_FIT_VIEW_OPTIONS);
      attemptCount += 1;

      if (attemptCount < STUDIO_FIT_VIEW_ATTEMPT_COUNT) {
        scheduleFit();
      }
    }

    function scheduleFit() {
      if (typeof window === 'undefined') {
        applyViewport();
        return;
      }

      if (typeof window.requestAnimationFrame === 'function') {
        const frameId = window.requestAnimationFrame(applyViewport);
        animationFrameIds.push(frameId);
        return;
      }

      const timeoutId = window.setTimeout(applyViewport, 16);
      timeoutIds.push(timeoutId);
    }

    scheduleFit();

    return () => {
      cancelled = true;
      animationFrameIds.forEach((frameId) => {
        window.cancelAnimationFrame(frameId);
      });
      timeoutIds.forEach((timeoutId) => {
        window.clearTimeout(timeoutId);
      });
    };
  }, [autoFitKey, flowInstance, isStudioVariant, nodes.length]);

  const decoratedNodes = useMemo(
    () =>
      localNodes.map((node) => {
        const isSelected = node.id === selectedNodeId;
        const managesOwnSelection = node.className
          ?.split(' ')
          .includes(SELF_MANAGED_SELECTION_CLASS);
        if (isStudioVariant) {
          return {
            ...node,
            selected: isSelected,
          };
        }

        if (managesOwnSelection) {
          return {
            ...node,
            selected: isSelected,
          };
        }

        return {
          ...node,
          selected: isSelected,
          style: {
            ...node.style,
            borderColor: isSelected
              ? 'var(--ant-color-primary)'
              : node.style?.borderColor,
            boxShadow: isSelected
              ? '0 0 0 2px rgba(22, 119, 255, 0.18)'
              : node.style?.boxShadow,
          },
        };
      }),
    [isStudioVariant, localNodes, selectedNodeId],
  );

  const decoratedEdges = useMemo(
    () =>
      localEdges.map((edge) => {
        const isSelected = edge.id === selectedEdgeId;
        return {
          ...edge,
          selected: isSelected,
          markerEnd:
            isSelected && edge.markerEnd && typeof edge.markerEnd === 'object'
              ? {
                  ...edge.markerEnd,
                  color: SELECTED_EDGE_COLOR,
                }
              : edge.markerEnd,
          style: {
            ...edge.style,
            filter: isSelected ? SELECTED_EDGE_FILTER : edge.style?.filter,
            stroke: isSelected
              ? 'var(--ant-color-primary)'
              : edge.style?.stroke,
            strokeWidth: isSelected
              ? SELECTED_EDGE_STROKE_WIDTH
              : (edge.style?.strokeWidth ?? 1.5),
          },
          labelStyle: {
            ...edge.labelStyle,
            fill: isSelected
              ? 'var(--ant-color-primary)'
              : edge.labelStyle?.fill,
          },
        };
      }),
    [localEdges, selectedEdgeId],
  );

  const handleNodesChange = (changes: NodeChange[]) => {
    setLocalNodes((currentNodes) => applyNodeChanges(changes, currentNodes));
  };

  return (
    <div
      style={{
        background: isStudioVariant ? '#f7f9fc' : undefined,
        border: isStudioVariant ? '1px solid #d8e0ea' : '1px solid #f0f0f0',
        borderRadius: 8,
        height,
        minHeight: 0,
        overflow: 'hidden',
        position: 'relative',
        width: '100%',
      }}
    >
      <style>
        {isStudioVariant ? studioCanvasCss : selfManagedSelectionCss}
      </style>
      <ReactFlow
        onInit={setFlowInstance}
        nodes={decoratedNodes}
        edges={decoratedEdges}
        fitView
        fitViewOptions={isStudioVariant ? STUDIO_FIT_VIEW_OPTIONS : undefined}
        minZoom={isStudioVariant ? STUDIO_CANVAS_MIN_ZOOM : undefined}
        maxZoom={isStudioVariant ? STUDIO_CANVAS_MAX_ZOOM : undefined}
        nodeTypes={
          isStudioVariant
            ? {
                studioWorkflowNode: StudioWorkflowNode,
              }
            : undefined
        }
        nodesDraggable={isStudioVariant}
        nodesConnectable={Boolean(isStudioVariant && onConnectNodes)}
        elementsSelectable
        deleteKeyCode={
          isStudioVariant && !onDeleteNodes && !onDeleteEdges ? null : undefined
        }
        onNodesChange={isStudioVariant ? handleNodesChange : undefined}
        onBeforeDelete={
          isStudioVariant && (onDeleteNodes || onDeleteEdges)
            ? async ({ edges: edgesToDelete, nodes: nodesToDelete }) => {
                const nodeIds = nodesToDelete
                  .map((node) => String(node.id ?? '').trim())
                  .filter(Boolean);
                const edgeIds = edgesToDelete
                  .map((edge) => String(edge.id ?? '').trim())
                  .filter(Boolean);
                if (nodeIds.length === 0 && edgeIds.length === 0) {
                  return false;
                }

                try {
                  if (nodeIds.length > 0) {
                    await onDeleteNodes?.(nodeIds);
                  }
                  if (edgeIds.length > 0) {
                    await onDeleteEdges?.(edgeIds);
                  }
                } catch {
                  // Keep the local graph unchanged until the parent document confirms deletion.
                }

                return false;
              }
            : undefined
        }
        onNodeDragStop={
          isStudioVariant
            ? () =>
                onNodeLayoutChange?.(
                  (flowInstance?.getNodes() as Node[]) ?? localNodes,
                )
            : undefined
        }
        onConnect={
          isStudioVariant
            ? (connection) => {
                if (!connection.source || !connection.target) {
                  return;
                }

                onConnectNodes?.(connection.source, connection.target);
              }
            : undefined
        }
        onNodeClick={(_, node) => onNodeSelect?.(node.id)}
        onEdgeClick={(_, edge) => onEdgeSelect?.(edge.id)}
        onPaneClick={() => onCanvasSelect?.()}
        onPaneContextMenu={
          isStudioVariant
            ? (event) => {
                event.preventDefault();
                const flowPosition = flowInstance?.screenToFlowPosition({
                  x: event.clientX,
                  y: event.clientY,
                }) ?? { x: 420, y: 220 };
                onCanvasContextMenu?.({
                  clientX: event.clientX,
                  clientY: event.clientY,
                  flowX: flowPosition.x,
                  flowY: flowPosition.y,
                });
              }
            : undefined
        }
        className={isStudioVariant ? 'studio-canvas' : undefined}
        proOptions={isStudioVariant ? { hideAttribution: true } : undefined}
      >
        <Background
          color={isStudioVariant ? '#cbd5e1' : undefined}
          variant={
            isStudioVariant ? BackgroundVariant.Dots : BackgroundVariant.Lines
          }
          gap={isStudioVariant ? 28 : 16}
          size={isStudioVariant ? 1 : 1}
        />
        {isStudioVariant ? (
          <>
            <MiniMap
              position="bottom-right"
              zoomable
              pannable
              style={{
                background: 'rgba(255, 255, 255, 0.90)',
                border: '1px solid #d8e0ea',
                borderRadius: 8,
                height: 82,
                marginBottom: 18 + bottomInset,
                marginRight: 16,
                width: 132,
              }}
              maskColor="rgba(241, 245, 249, 0.72)"
              bgColor="rgba(255, 255, 255, 0.90)"
              nodeBorderRadius={4}
              nodeColor={(node) => {
                const data = node.data as StudioGraphNodeData | undefined;
                return getStudioGraphCategory(data?.stepType || '').color;
              }}
            />
            <Controls
              position="bottom-left"
              showInteractive={false}
              style={{
                marginBottom: 18 + bottomInset,
                marginLeft: 16,
              }}
            />
          </>
        ) : (
          <Controls showInteractive={false} />
        )}
      </ReactFlow>
      {overlayContent}
    </div>
  );
};

export default GraphCanvas;
