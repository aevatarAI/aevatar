import {
  ApiOutlined,
  ApartmentOutlined,
  AppstoreOutlined,
  CodeOutlined,
  DatabaseOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  Background,
  BackgroundVariant,
  Controls,
  Handle,
  MiniMap,
  Position,
  ReactFlow,
  applyNodeChanges,
  useEdgesState,
  useNodesState,
  useStore,
  type Edge,
  type Node,
  type NodeChange,
  type NodeProps,
  type ReactFlowInstance,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import React, { useEffect, useMemo } from 'react';
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
  viewportRightInset?: number;
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
const STUDIO_CANVAS_RADIUS = 8;
const STUDIO_NODE_ICON_RADIUS = 6;
const STUDIO_CONTROLS_LEFT_INSET = 16;
const STUDIO_MINIMAP_LEFT_INSET = 58;
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
    STUDIO_NODE_ICON_BY_CATEGORY[category.key] ?? STUDIO_NODE_ICON_BY_CATEGORY.custom;
  const zoom = useStore((state) => state.transform[2]);
  const compact = zoom < 0.72;
  const width = compact ? 168 : 244;
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

  return (
    <div
      style={{
        width,
        borderRadius: STUDIO_CANVAS_RADIUS,
        overflow: 'hidden',
        border: `1px solid ${selected ? category.color : '#E8E2D9'}`,
        background: '#FFFFFF',
        boxShadow: selected
          ? `0 0 0 2px ${category.color}22, 0 22px 48px rgba(17, 24, 39, 0.14)`
          : executionFocused
            ? '0 0 0 2px rgba(37, 99, 235, 0.18), 0 22px 48px rgba(17, 24, 39, 0.14)'
            : '0 18px 42px rgba(17, 24, 39, 0.10)',
        transition: 'box-shadow 120ms ease, border-color 120ms ease',
      }}
    >
      <Handle
        type="target"
        position={Position.Left}
        style={{
          background: category.color,
          border: 'none',
          height: 10,
          width: 10,
        }}
      />
      <div
        style={{
          alignItems: 'center',
          borderBottom: '1px solid #F1ECE5',
          display: 'flex',
          gap: 10,
          padding: compact ? '10px 12px' : '12px 14px',
        }}
      >
        <div
          style={{
            alignItems: 'center',
            background: `${category.color}18`,
            borderRadius: STUDIO_NODE_ICON_RADIUS,
            color: category.color,
            display: 'flex',
            flexShrink: 0,
            height: compact ? 28 : 32,
            justifyContent: 'center',
            width: compact ? 28 : 32,
          }}
        >
          <Icon style={{ fontSize: compact ? 14 : 15 }} />
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div
            style={{
              color: '#111827',
              fontSize: compact ? 12 : 13,
              fontWeight: 600,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {data.stepId}
          </div>
          <div
            style={{
              color: '#6B7280',
              fontSize: 11,
              lineHeight: 1.4,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {data.subtitle}
          </div>
        </div>
        {executionStatus && executionStatus !== 'idle' ? (
          <span
            style={{
              alignItems: 'center',
              background:
                executionStatus === 'completed'
                  ? '#DCFCE7'
                  : executionStatus === 'failed'
                    ? '#FEE2E2'
                    : executionStatus === 'waiting'
                      ? '#FEF3C7'
                      : '#DBEAFE',
              borderRadius: 999,
              color: statusColor,
              display: 'inline-flex',
              flexShrink: 0,
              fontSize: 10,
              fontWeight: 600,
              letterSpacing: '0.04em',
              lineHeight: 1,
              padding: '6px 8px',
              textTransform: 'uppercase',
            }}
          >
            {executionStatus}
          </span>
        ) : null}
      </div>
      <div
        style={{
          color: '#6B7280',
          fontSize: 11,
          lineHeight: 1.55,
          padding: compact ? '10px 12px' : '12px 14px',
        }}
      >
        {data.targetRole ? (
          <div
            style={{
              marginBottom: compact ? 4 : 6,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            <span style={{ color: 'var(--ant-color-text-tertiary)' }}>role:</span> {data.targetRole}
          </div>
        ) : null}
        <div
          style={{
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {data.parametersSummary}
        </div>
      </div>
      <Handle
        type="source"
        position={Position.Right}
        style={{
          background: category.color,
          border: 'none',
          height: 10,
          width: 10,
        }}
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
  viewportRightInset = 0,
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
  const [localNodes, setLocalNodes] = useNodesState(nodes);
  const [localEdges, setLocalEdges] = useEdgesState(edges);
  const [flowInstance, setFlowInstance] =
    React.useState<ReactFlowInstance | null>(null);
  const isStudioVariant = variant === 'studio';
  const studioViewportRightInset =
    isStudioVariant &&
    Number.isFinite(viewportRightInset) &&
    viewportRightInset > 0
      ? viewportRightInset
      : 0;
  const previousViewportRightInsetRef = React.useRef(studioViewportRightInset);
  const studioFitViewOptions = React.useMemo(
    () => ({
      padding: 0.2,
      minZoom: 0.14,
      maxZoom: 0.92,
      duration: 0,
    }),
    [],
  );

  React.useLayoutEffect(() => {
    const previousInset = previousViewportRightInsetRef.current;
    const insetDelta = studioViewportRightInset - previousInset;
    previousViewportRightInsetRef.current = studioViewportRightInset;

    if (!flowInstance || !isStudioVariant || insetDelta === 0) {
      return;
    }

    const viewport = flowInstance.getViewport();
    void flowInstance.setViewport(
      {
        x: viewport.x - insetDelta / 2,
        y: viewport.y,
        zoom: viewport.zoom,
      },
      { duration: 0 },
    );
  }, [flowInstance, isStudioVariant, studioViewportRightInset]);

  useEffect(() => {
    setLocalNodes(nodes);
  }, [nodes, setLocalNodes]);

  useEffect(() => {
    setLocalEdges(edges);
  }, [edges, setLocalEdges]);

  useEffect(() => {
    if (!autoFitKey || !flowInstance || !isStudioVariant || nodes.length === 0) {
      return;
    }

    if (typeof window === 'undefined') {
      flowInstance.fitView(studioFitViewOptions);
      return;
    }

    const useAnimationFrame =
      typeof window.requestAnimationFrame === 'function' &&
      typeof window.cancelAnimationFrame === 'function';
    const handle = useAnimationFrame
      ? window.requestAnimationFrame(() => {
          flowInstance.fitView(studioFitViewOptions);
        })
      : window.setTimeout(() => {
          flowInstance.fitView(studioFitViewOptions);
        }, 0);

    return () => {
      if (useAnimationFrame) {
        window.cancelAnimationFrame(handle);
        return;
      }

      window.clearTimeout(handle);
    };
  }, [
    autoFitKey,
    flowInstance,
    isStudioVariant,
    nodes.length,
    studioFitViewOptions,
  ]);

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
          style: {
            ...edge.style,
            stroke: isSelected
              ? 'var(--ant-color-primary)'
              : edge.style?.stroke,
            strokeWidth: isSelected ? 3 : (edge.style?.strokeWidth ?? 1.5),
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
        background: isStudioVariant ? '#FBFBFC' : undefined,
        border: isStudioVariant ? '1px solid #E8E2D9' : '1px solid #f0f0f0',
        borderRadius: 8,
        height,
        minHeight: 0,
        overflow: 'hidden',
        position: 'relative',
        width: '100%',
      }}
    >
      {!isStudioVariant ? <style>{selfManagedSelectionCss}</style> : null}
      <div
        data-testid="graph-canvas-viewport"
        style={{
          bottom: 0,
          left: 0,
          minWidth: 0,
          position: 'absolute',
          right: studioViewportRightInset,
          top: 0,
        }}
      >
        <ReactFlow
          onInit={setFlowInstance}
          nodes={decoratedNodes}
          edges={decoratedEdges}
          fitView
          fitViewOptions={isStudioVariant ? studioFitViewOptions : undefined}
          minZoom={isStudioVariant ? 0.14 : undefined}
          maxZoom={isStudioVariant ? 1.6 : undefined}
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
        >
          <Background
            color={isStudioVariant ? '#D8D2C8' : undefined}
            variant={
              isStudioVariant ? BackgroundVariant.Dots : BackgroundVariant.Lines
            }
            gap={isStudioVariant ? 24 : 16}
            size={1}
          />
          {isStudioVariant ? (
            <>
              <MiniMap
                position="bottom-left"
                zoomable
                pannable
                style={{
                  background: 'rgba(248, 247, 244, 0.98)',
                  border: '1px solid #E8E2D9',
                  borderRadius: STUDIO_CANVAS_RADIUS,
                  height: 108,
                  marginBottom: 24 + bottomInset,
                  marginLeft: STUDIO_MINIMAP_LEFT_INSET,
                  width: 164,
                }}
                maskColor="rgba(255, 255, 255, 0.76)"
                bgColor="rgba(248, 247, 244, 0.98)"
                nodeBorderRadius={8}
                nodeColor={(node) => {
                  const data = node.data as StudioGraphNodeData | undefined;
                  return getStudioGraphCategory(data?.stepType || '').color;
                }}
              />
              <Controls
                fitViewOptions={studioFitViewOptions}
                position="bottom-left"
                style={{
                  marginBottom: 20 + bottomInset,
                  marginLeft: STUDIO_CONTROLS_LEFT_INSET,
                }}
              />
            </>
          ) : (
            <Controls showInteractive={false} />
          )}
        </ReactFlow>
      </div>
      {overlayContent}
    </div>
  );
};

export default GraphCanvas;
