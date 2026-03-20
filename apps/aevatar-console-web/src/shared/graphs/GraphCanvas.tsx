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
  nodes: Node[];
  edges: Edge[];
  height?: number | string;
  bottomInset?: number;
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
};

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
        borderRadius: 24,
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
            borderRadius: 14,
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
            {data.stepType}
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
            <span style={{ color: '#9CA3AF' }}>role:</span> {data.targetRole}
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
  nodes,
  edges,
  height = 420,
  bottomInset = 0,
  selectedNodeId,
  selectedEdgeId,
  variant = 'default',
  onNodeSelect,
  onEdgeSelect,
  onCanvasSelect,
  onCanvasContextMenu,
  onConnectNodes,
  onNodeLayoutChange,
}) => {
  const [localNodes, setLocalNodes] = useNodesState(nodes);
  const [localEdges, setLocalEdges] = useEdgesState(edges);
  const [flowInstance, setFlowInstance] =
    React.useState<ReactFlowInstance | null>(null);
  const isStudioVariant = variant === 'studio';

  useEffect(() => {
    setLocalNodes(nodes);
  }, [nodes, setLocalNodes]);

  useEffect(() => {
    setLocalEdges(edges);
  }, [edges, setLocalEdges]);

  const decoratedNodes = useMemo(
    () =>
      localNodes.map((node) => {
        const isSelected = node.id === selectedNodeId;
        if (isStudioVariant) {
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
        border: isStudioVariant ? '1px solid #E8E2D9' : '1px solid #f0f0f0',
        borderRadius: isStudioVariant ? 24 : 8,
        height,
        minHeight: 0,
        overflow: 'hidden',
        width: '100%',
      }}
    >
      <ReactFlow
        onInit={setFlowInstance}
        nodes={decoratedNodes}
        edges={decoratedEdges}
        fitView
        fitViewOptions={
          isStudioVariant
            ? {
                padding: 0.2,
                minZoom: 0.14,
                maxZoom: 0.92,
              }
            : undefined
        }
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
        onNodesChange={isStudioVariant ? handleNodesChange : undefined}
        onNodeDragStop={
          isStudioVariant
            ? (_, __, nextNodes) => onNodeLayoutChange?.(nextNodes)
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
          variant={isStudioVariant ? BackgroundVariant.Dots : BackgroundVariant.Lines}
          gap={isStudioVariant ? 24 : 16}
          size={isStudioVariant ? 1 : 1}
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
                borderRadius: 18,
                height: 108,
                marginBottom: 24 + bottomInset,
                marginLeft: 16,
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
              position="bottom-left"
              style={{
                marginBottom: 20 + bottomInset,
                marginLeft: 16,
              }}
            />
          </>
        ) : (
          <Controls showInteractive={false} />
        )}
      </ReactFlow>
    </div>
  );
};

export default GraphCanvas;
