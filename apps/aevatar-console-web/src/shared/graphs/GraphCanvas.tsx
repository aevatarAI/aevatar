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
  type ReactFlowProps,
  useEdgesState,
  useNodesInitialized,
  useNodesState,
  useReactFlow,
  useStore,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
} from 'react';
import { t } from '@/shared/i18n/messages';
import {
  getStudioGraphCategory,
  type StudioGraphNodeData,
} from '@/shared/studio/graph';
import {
  reconcileGraphEdges,
  reconcileGraphNodes,
} from './reconcileGraphElements';

type GraphCanvasProps = {
  autoFitKey?: string;
  nodes: readonly Node[];
  edges: readonly Edge[];
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
const STUDIO_NODE_WIDTH = 268;
const STUDIO_NODE_COMPACT_WIDTH = 244;
const STUDIO_NODE_COMPACT_ZOOM = 0.48;
const STUDIO_PRO_OPTIONS = { hideAttribution: true } as const;
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

const selectStudioNodeCompact = (state: {
  transform: [number, number, number];
}) => state.transform[2] < STUDIO_NODE_COMPACT_ZOOM;

const getStudioMiniMapNodeColor = (node: Node) => {
  const data = node.data as StudioGraphNodeData | undefined;
  return getStudioGraphCategory(data?.stepType || '').color;
};

function StudioWorkflowNode({
  data,
  selected,
}: NodeProps<Node<StudioGraphNodeData>>) {
  const category = getStudioGraphCategory(data.stepType);
  const Icon =
    STUDIO_NODE_ICON_BY_CATEGORY[category.key] ??
    STUDIO_NODE_ICON_BY_CATEGORY.custom;
  const compact = useStore(selectStudioNodeCompact);
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
      {compact ? null : (
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
      )}
      <Handle
        className="studio-workflow-node__handle studio-workflow-node__handle--source"
        type="source"
        position={Position.Right}
      />
    </div>
  );
}

const STUDIO_NODE_TYPES = {
  studioWorkflowNode: React.memo(StudioWorkflowNode),
};

type StudioViewportControl = {
  cancelOrdinaryFit?: () => void;
  manuallyNavigated: boolean;
};

type StudioViewportControllerProps = {
  autoFitKey?: string;
  edgeIdsKey: string;
  navigationControlRef: React.RefObject<StudioViewportControl>;
  nodeIds: readonly string[];
  nodeIdsKey: string;
  selectedNodeId?: string;
};

function StudioViewportController({
  autoFitKey,
  edgeIdsKey,
  navigationControlRef,
  nodeIds,
  nodeIdsKey,
  selectedNodeId,
}: StudioViewportControllerProps) {
  const nodesInitialized = useNodesInitialized();
  const flowInstance = useReactFlow();
  const previousNodeIdsRef = useRef<ReadonlySet<string>>(new Set());
  const latestNodeIdsRef = useRef(nodeIds);
  const latestSelectedNodeIdRef = useRef(selectedNodeId);
  const lastFittedRef = useRef<{
    flowInstance: ReactFlowInstance;
    reason: string;
  }>(undefined);

  latestNodeIdsRef.current = nodeIds;
  latestSelectedNodeIdRef.current = selectedNodeId;

  useLayoutEffect(() => {
    const currentNodeIds = new Set(latestNodeIdsRef.current);
    if (currentNodeIds.size === 0) {
      previousNodeIdsRef.current = currentNodeIds;
      lastFittedRef.current = undefined;
      return;
    }
    if (!nodesInitialized) {
      return;
    }

    const fitReason = JSON.stringify(
      autoFitKey === undefined
        ? ['topology', nodeIdsKey, edgeIdsKey]
        : ['explicit', autoFitKey, nodeIdsKey, edgeIdsKey],
    );
    if (
      lastFittedRef.current?.reason === fitReason &&
      lastFittedRef.current.flowInstance === flowInstance
    ) {
      previousNodeIdsRef.current = currentNodeIds;
      return;
    }

    const selectedNodeId = latestSelectedNodeIdRef.current;
    const addedSelectedNode =
      selectedNodeId !== undefined &&
      currentNodeIds.has(selectedNodeId) &&
      !previousNodeIdsRef.current.has(selectedNodeId);
    previousNodeIdsRef.current = currentNodeIds;

    if (navigationControlRef.current.manuallyNavigated && !addedSelectedNode) {
      return;
    }

    const focusNodeId = navigationControlRef.current.manuallyNavigated
      ? selectedNodeId
      : undefined;
    let active = true;
    let animationFrameId: number | undefined;
    const cancelFit = () => {
      active = false;
      if (
        animationFrameId !== undefined &&
        typeof window !== 'undefined' &&
        typeof window.cancelAnimationFrame === 'function'
      ) {
        window.cancelAnimationFrame(animationFrameId);
      }
      if (navigationControlRef.current.cancelOrdinaryFit === cancelFit) {
        navigationControlRef.current.cancelOrdinaryFit = undefined;
      }
    };
    navigationControlRef.current.cancelOrdinaryFit = focusNodeId
      ? undefined
      : cancelFit;

    const applyViewport = () => {
      if (!active) {
        return;
      }
      animationFrameId = undefined;
      navigationControlRef.current.cancelOrdinaryFit = undefined;
      lastFittedRef.current = { flowInstance, reason: fitReason };
      void flowInstance.fitView(
        focusNodeId
          ? {
              ...STUDIO_FIT_VIEW_OPTIONS,
              nodes: [{ id: focusNodeId }],
            }
          : STUDIO_FIT_VIEW_OPTIONS,
      );
    };

    if (
      typeof window !== 'undefined' &&
      typeof window.requestAnimationFrame === 'function'
    ) {
      animationFrameId = window.requestAnimationFrame(applyViewport);
    } else {
      applyViewport();
    }

    return cancelFit;
  }, [
    autoFitKey,
    edgeIdsKey,
    flowInstance,
    navigationControlRef,
    nodeIdsKey,
    nodesInitialized,
  ]);

  return null;
}

function decorateGraphEdge(edge: Edge, selectedEdgeId?: string): Edge {
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
      stroke: isSelected ? 'var(--ant-color-primary)' : edge.style?.stroke,
      strokeWidth: isSelected
        ? SELECTED_EDGE_STROKE_WIDTH
        : (edge.style?.strokeWidth ?? 1.5),
    },
    labelStyle: {
      ...edge.labelStyle,
      fill: isSelected ? 'var(--ant-color-primary)' : edge.labelStyle?.fill,
    },
  };
}

function shallowGraphValueEqual(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) {
    return true;
  }
  if (
    !left ||
    !right ||
    typeof left !== 'object' ||
    typeof right !== 'object'
  ) {
    return false;
  }

  const leftRecord = left as Record<string, unknown>;
  const rightRecord = right as Record<string, unknown>;
  const leftKeys = Object.keys(leftRecord);
  return (
    leftKeys.length === Object.keys(rightRecord).length &&
    leftKeys.every(
      (key) =>
        Object.hasOwn(rightRecord, key) &&
        Object.is(leftRecord[key], rightRecord[key]),
    )
  );
}

function mergeIncomingNodeDelta(
  currentNodes: readonly Node[],
  previousIncomingNodes: readonly Node[],
  incomingNodes: readonly Node[],
): Node[] {
  const currentById = new Map(currentNodes.map((node) => [node.id, node]));
  const previousIncomingById = new Map(
    previousIncomingNodes.map((node) => [node.id, node]),
  );

  return incomingNodes.map((incomingNode) => {
    const currentNode = currentById.get(incomingNode.id);
    const previousIncomingNode = previousIncomingById.get(incomingNode.id);
    if (!currentNode || !previousIncomingNode) {
      return incomingNode;
    }

    return {
      ...incomingNode,
      dragging:
        previousIncomingNode.dragging === incomingNode.dragging
          ? currentNode.dragging
          : incomingNode.dragging,
      height:
        previousIncomingNode.height === incomingNode.height
          ? currentNode.height
          : incomingNode.height,
      measured: shallowGraphValueEqual(
        previousIncomingNode.measured,
        incomingNode.measured,
      )
        ? currentNode.measured
        : incomingNode.measured,
      position: shallowGraphValueEqual(
        previousIncomingNode.position,
        incomingNode.position,
      )
        ? currentNode.position
        : incomingNode.position,
      resizing:
        previousIncomingNode.resizing === incomingNode.resizing
          ? currentNode.resizing
          : incomingNode.resizing,
      width:
        previousIncomingNode.width === incomingNode.width
          ? currentNode.width
          : incomingNode.width,
    };
  });
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
  const [localNodes, setLocalNodes] = useNodesState([...nodes]);
  const [localEdges, setLocalEdges] = useEdgesState([...edges]);
  const [flowInstance, setFlowInstance] =
    React.useState<ReactFlowInstance | null>(null);
  const navigationControlRef = useRef<StudioViewportControl>({
    manuallyNavigated: false,
  });
  const latestLocalNodesRef = useRef(localNodes);
  const renderedAutoFitKeyRef = useRef(autoFitKey);
  const incomingStudioNodesRef = useRef<readonly Node[]>([]);
  const incomingStudioEdgesRef = useRef<readonly Edge[]>([]);
  const incomingNodeIdsKey = useMemo(
    () => JSON.stringify(nodes.map((node) => node.id)),
    [nodes],
  );
  const incomingEdgeIdsKey = useMemo(
    () => JSON.stringify(edges.map((edge) => edge.id)),
    [edges],
  );
  const renderedStudioTopology = useMemo(() => {
    const nodeIds = localNodes.map((node) => node.id);
    return {
      edgeIdsKey: JSON.stringify(localEdges.map((edge) => edge.id)),
      nodeIds,
      nodeIdsKey: JSON.stringify(nodeIds),
    };
  }, [localEdges, localNodes]);
  const renderedTopologyMatchesIncoming =
    incomingNodeIdsKey === renderedStudioTopology.nodeIdsKey &&
    incomingEdgeIdsKey === renderedStudioTopology.edgeIdsKey;
  const renderedAutoFitKey = renderedTopologyMatchesIncoming
    ? autoFitKey
    : renderedAutoFitKeyRef.current;

  latestLocalNodesRef.current = localNodes;

  useEffect(() => {
    if (renderedTopologyMatchesIncoming) {
      renderedAutoFitKeyRef.current = autoFitKey;
    }
  }, [autoFitKey, renderedTopologyMatchesIncoming]);

  useEffect(() => {
    if (!isStudioVariant) {
      incomingStudioNodesRef.current = [];
      setLocalNodes([...nodes]);
      return;
    }

    const previousIncomingNodes = incomingStudioNodesRef.current;
    const reconciledIncomingNodes = reconcileGraphNodes(
      previousIncomingNodes,
      nodes,
      selectedNodeId,
    );
    if (reconciledIncomingNodes === previousIncomingNodes) {
      return;
    }

    incomingStudioNodesRef.current = reconciledIncomingNodes;
    setLocalNodes((currentNodes) => {
      const mergedIncomingNodes = mergeIncomingNodeDelta(
        currentNodes,
        previousIncomingNodes,
        reconciledIncomingNodes,
      );
      return reconcileGraphNodes(
        currentNodes,
        mergedIncomingNodes,
        selectedNodeId,
      );
    });
  }, [isStudioVariant, nodes, selectedNodeId, setLocalNodes]);

  useEffect(() => {
    if (!isStudioVariant) {
      incomingStudioEdgesRef.current = [];
      setLocalEdges([...edges]);
      return;
    }

    const reconciledIncomingEdges = reconcileGraphEdges(
      incomingStudioEdgesRef.current,
      edges.map((edge) => decorateGraphEdge(edge, selectedEdgeId)),
    );
    if (reconciledIncomingEdges === incomingStudioEdgesRef.current) {
      return;
    }

    incomingStudioEdgesRef.current = reconciledIncomingEdges;
    setLocalEdges((currentEdges) =>
      reconcileGraphEdges(currentEdges, reconciledIncomingEdges),
    );
  }, [edges, isStudioVariant, selectedEdgeId, setLocalEdges]);

  const decoratedNodes = useMemo(() => {
    if (isStudioVariant) {
      return localNodes;
    }

    return localNodes.map((node) => {
      const isSelected = node.id === selectedNodeId;
      const managesOwnSelection = node.className
        ?.split(' ')
        .includes(SELF_MANAGED_SELECTION_CLASS);

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
    });
  }, [isStudioVariant, localNodes, selectedNodeId]);

  const decoratedEdges = useMemo(
    () =>
      isStudioVariant
        ? localEdges
        : localEdges.map((edge) => decorateGraphEdge(edge, selectedEdgeId)),
    [isStudioVariant, localEdges, selectedEdgeId],
  );

  const canvasStyle = useMemo<React.CSSProperties>(
    () => ({
      background: isStudioVariant ? '#f7f9fc' : undefined,
      border: isStudioVariant ? '1px solid #d8e0ea' : '1px solid #f0f0f0',
      borderRadius: 8,
      height,
      minHeight: 0,
      overflow: 'hidden',
      position: 'relative',
      width: '100%',
    }),
    [height, isStudioVariant],
  );
  const miniMapStyle = useMemo<React.CSSProperties>(
    () => ({
      background: 'rgba(255, 255, 255, 0.90)',
      border: '1px solid #d8e0ea',
      borderRadius: 8,
      height: 82,
      marginBottom: 18 + bottomInset,
      marginRight: 16,
      width: 132,
    }),
    [bottomInset],
  );
  const studioControlsStyle = useMemo<React.CSSProperties>(
    () => ({
      marginBottom: 18 + bottomInset,
      marginLeft: 16,
    }),
    [bottomInset],
  );

  const handleNodesChange = useCallback(
    (changes: NodeChange[]) => {
      setLocalNodes((currentNodes) => applyNodeChanges(changes, currentNodes));
    },
    [setLocalNodes],
  );
  const handleBeforeDelete = useCallback<
    NonNullable<ReactFlowProps['onBeforeDelete']>
  >(
    async ({ edges: edgesToDelete, nodes: nodesToDelete }) => {
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
    },
    [onDeleteEdges, onDeleteNodes],
  );
  const handleNodeDragStop = useCallback<
    NonNullable<ReactFlowProps['onNodeDragStop']>
  >(() => {
    onNodeLayoutChange?.(
      (flowInstance?.getNodes() as Node[] | undefined) ??
        latestLocalNodesRef.current,
    );
  }, [flowInstance, onNodeLayoutChange]);
  const handleConnect = useCallback<NonNullable<ReactFlowProps['onConnect']>>(
    (connection) => {
      if (!connection.source || !connection.target) {
        return;
      }
      onConnectNodes?.(connection.source, connection.target);
    },
    [onConnectNodes],
  );
  const handleNodeClick = useCallback<
    NonNullable<ReactFlowProps['onNodeClick']>
  >((_, node) => onNodeSelect?.(node.id), [onNodeSelect]);
  const handleEdgeClick = useCallback<
    NonNullable<ReactFlowProps['onEdgeClick']>
  >((_, edge) => onEdgeSelect?.(edge.id), [onEdgeSelect]);
  const handlePaneClick = useCallback(() => {
    onCanvasSelect?.();
  }, [onCanvasSelect]);
  const handlePaneContextMenu = useCallback<
    NonNullable<ReactFlowProps['onPaneContextMenu']>
  >(
    (event) => {
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
    },
    [flowInstance, onCanvasContextMenu],
  );
  const markManuallyNavigated = useCallback(() => {
    navigationControlRef.current.manuallyNavigated = true;
    navigationControlRef.current.cancelOrdinaryFit?.();
  }, []);
  const handleMoveStart = useCallback<
    NonNullable<ReactFlowProps['onMoveStart']>
  >(
    (event) => {
      if (event !== null) {
        markManuallyNavigated();
      }
    },
    [markManuallyNavigated],
  );

  return (
    <div style={canvasStyle}>
      <style>
        {isStudioVariant ? studioCanvasCss : selfManagedSelectionCss}
      </style>
      <ReactFlow
        onInit={setFlowInstance}
        nodes={decoratedNodes}
        edges={decoratedEdges}
        fitView={!isStudioVariant}
        fitViewOptions={isStudioVariant ? STUDIO_FIT_VIEW_OPTIONS : undefined}
        minZoom={isStudioVariant ? STUDIO_CANVAS_MIN_ZOOM : undefined}
        maxZoom={isStudioVariant ? STUDIO_CANVAS_MAX_ZOOM : undefined}
        nodeTypes={isStudioVariant ? STUDIO_NODE_TYPES : undefined}
        nodesDraggable={isStudioVariant}
        nodesConnectable={Boolean(isStudioVariant && onConnectNodes)}
        elementsSelectable
        onlyRenderVisibleElements={isStudioVariant ? true : undefined}
        deleteKeyCode={
          isStudioVariant && !onDeleteNodes && !onDeleteEdges ? null : undefined
        }
        onNodesChange={isStudioVariant ? handleNodesChange : undefined}
        onBeforeDelete={
          isStudioVariant && (onDeleteNodes || onDeleteEdges)
            ? handleBeforeDelete
            : undefined
        }
        onNodeDragStop={isStudioVariant ? handleNodeDragStop : undefined}
        onConnect={isStudioVariant ? handleConnect : undefined}
        onNodeClick={handleNodeClick}
        onEdgeClick={handleEdgeClick}
        onPaneClick={handlePaneClick}
        onPaneContextMenu={isStudioVariant ? handlePaneContextMenu : undefined}
        onMoveStart={isStudioVariant ? handleMoveStart : undefined}
        className={isStudioVariant ? 'studio-canvas' : undefined}
        proOptions={isStudioVariant ? STUDIO_PRO_OPTIONS : undefined}
      >
        {isStudioVariant ? (
          <StudioViewportController
            autoFitKey={renderedAutoFitKey}
            edgeIdsKey={renderedStudioTopology.edgeIdsKey}
            navigationControlRef={navigationControlRef}
            nodeIds={renderedStudioTopology.nodeIds}
            nodeIdsKey={renderedStudioTopology.nodeIdsKey}
            selectedNodeId={selectedNodeId}
          />
        ) : null}
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
              style={miniMapStyle}
              maskColor="rgba(241, 245, 249, 0.72)"
              bgColor="rgba(255, 255, 255, 0.90)"
              nodeBorderRadius={4}
              nodeColor={getStudioMiniMapNodeColor}
            />
            <Controls
              onFitView={markManuallyNavigated}
              onZoomIn={markManuallyNavigated}
              onZoomOut={markManuallyNavigated}
              position="bottom-left"
              showInteractive={false}
              style={studioControlsStyle}
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
