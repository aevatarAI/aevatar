import {
  Background,
  BackgroundVariant,
  BaseEdge,
  Controls,
  EdgeLabelRenderer,
  Handle,
  MiniMap,
  Position,
  ReactFlow,
  getSmoothStepPath,
  type Edge,
  type EdgeProps,
  type Node,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { Tag, Typography, theme } from 'antd';
import React, { useMemo } from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import type {
  MissionControlSnapshot,
  MissionObservationStatus,
  MissionRunStatus,
  MissionTopologyNode,
} from './models';
import {
  formatMissionLabel,
  formatConnectionLabel,
  formatHandoffSeverityLabel,
  renderMissionKindIcon,
  resolveConnectionTagColor,
  resolveHandoffTagColor,
  resolveMissionStatusTone,
  resolveObservationTone,
  type MissionThemeToken,
} from './presentation';
import { t } from "@/shared/i18n/messages";

type TopologyCanvasProps = {
  activeNodeId?: string;
  connectionMessage?: string;
  connectionStatus: 'idle' | 'connecting' | 'live' | 'degraded' | 'disconnected';
  onCanvasSelect?: () => void;
  onNodeSelect: (nodeId: string) => void;
  snapshot: MissionControlSnapshot;
};

type TopologyNodeData = {
  node: MissionTopologyNode;
  shouldPulse: boolean;
};

type TopologyEdgeData = {
  observationStatus: MissionObservationStatus;
  streaming: boolean;
};

function buildTopologyStyles(_token: MissionThemeToken) {
  return `
    @keyframes missionTopologyFlowDash {
      to {
        stroke-dashoffset: -56;
      }
    }

    @keyframes missionTopologyPulse {
      0%, 100% {
        box-shadow: var(--mission-topology-card-shadow);
        transform: scale(1);
      }
      50% {
        box-shadow:
          var(--mission-topology-card-shadow),
          0 0 0 2px var(--mission-topology-warning),
          0 0 18px var(--mission-topology-warning-glow);
        transform: scale(1.02);
      }
    }

    @keyframes missionTopologyBeacon {
      0%, 100% {
        transform: scale(1);
        opacity: 0.9;
      }
      50% {
        transform: scale(1.45);
        opacity: 0.4;
      }
    }

    .mission-topology-flow-edge {
      animation: missionTopologyFlowDash 1.35s linear infinite;
      filter: drop-shadow(0 0 5px var(--mission-topology-primary));
      stroke-linecap: round;
    }

    .mission-topology-node-breathing {
      animation: missionTopologyPulse 1.8s ease-in-out infinite;
    }

    .mission-topology-freshness-alert::after {
      animation: missionTopologyBeacon 1.8s ease-in-out infinite;
      background: inherit;
      border-radius: 999px;
      content: '';
      inset: 0;
      position: absolute;
    }
  `;
}

function edgeTone(
  token: MissionThemeToken,
  observationStatus: MissionObservationStatus,
) {
  return resolveObservationTone(token, observationStatus);
}

function TopologyNodeCard({
  data,
  selected,
}: NodeProps<Node<TopologyNodeData>>) {
  const { token } = theme.useToken();
  const node = data.node;
  const observationTone = resolveObservationTone(token, node.observationStatus);
  const statusTone = resolveMissionStatusTone(token, node.status);
  const freshnessAlert =
    node.observationStatus === 'delayed' ||
    node.observationStatus === 'snapshot_available';

  return (
    <div
      className={data.shouldPulse ? 'mission-topology-node-breathing' : undefined}
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${selected ? token.colorPrimary : token.colorBorderSecondary}`,
        borderLeft: `3px solid ${statusTone}`,
        borderRadius: 4,
        boxShadow: token.boxShadowSecondary,
        color: token.colorText,
        minHeight: 148,
        padding: 14,
        position: 'relative',
        width: 248,
      }}
    >
      <Handle
        position={Position.Left}
        style={{
          background: token.colorBorder,
          border: 'none',
          height: 8,
          width: 8,
        }}
        type="target"
      />
      <AevatarTooltip
        title={`Observation: ${formatMissionLabel(node.observationStatus)} · freshness ${node.freshnessLabel}`}
      >
        <div
          className={freshnessAlert ? 'mission-topology-freshness-alert' : undefined}
          style={{
            alignItems: 'center',
            background: observationTone,
            border: `2px solid ${token.colorBgContainer}`,
            borderRadius: 999,
            display: 'flex',
            height: 10,
            justifyContent: 'center',
            position: 'absolute',
            right: 12,
            top: 12,
            width: 10,
          }}
        />
      </AevatarTooltip>
      <div
        style={{
          alignItems: 'flex-start',
          display: 'flex',
          gap: 10,
          justifyContent: 'space-between',
        }}
      >
        <div
          style={{
            alignItems: 'center',
            background: token.colorFillTertiary,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 4,
            color: token.colorPrimary,
            display: 'flex',
            flex: '0 0 auto',
            fontSize: 16,
            height: 34,
            justifyContent: 'center',
            width: 34,
          }}
        >
          {renderMissionKindIcon(node.kind)}
        </div>
        <div style={{ flex: 1, minWidth: 0, paddingRight: 12 }}>
          <Typography.Text
            strong
            style={{
              color: token.colorTextHeading,
              display: 'block',
              lineHeight: 1.25,
            }}
          >
            {node.label}
          </Typography.Text>
          <Typography.Text
            style={{
              color: token.colorTextTertiary,
              display: 'block',
              fontSize: 12,
              lineHeight: 1.35,
            }}
          >
            {node.role}
          </Typography.Text>
        </div>
      </div>
      <Typography.Paragraph
        ellipsis={{ rows: 3 }}
        style={{
          color: token.colorTextSecondary,
          fontSize: 12,
          lineHeight: 1.5,
          marginBottom: 12,
          marginTop: 12,
        }}
      >
        {node.summary}
      </Typography.Paragraph>
      <AevatarTooltip title={node.handoff.nextStep}>
        <div
          aria-label={t("pages.missioncontrol.topologycanvas.mission.control.node.handoff", "Mission Control node handoff")}
          style={{
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 4,
            display: 'flex',
            flexDirection: 'column',
            gap: 4,
            marginBottom: 12,
            padding: '8px 10px',
          }}
        >
          <Typography.Text
            style={{
              color: token.colorTextHeading,
              display: 'block',
              fontSize: 12,
              fontWeight: 700,
              lineHeight: 1.25,
            }}
          >
            {node.handoff.title}
          </Typography.Text>
          <Typography.Text
            ellipsis
            style={{ color: token.colorTextTertiary, fontSize: 11 }}
          >
            {node.handoff.evidence}
          </Typography.Text>
        </div>
      </AevatarTooltip>
      <div
        style={{
          borderTop: `1px solid ${token.colorBorderSecondary}`,
          display: 'grid',
          gap: 8,
          gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
          paddingTop: 10,
        }}
      >
        <div>
          <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 11 }}>
            {t("pages.missioncontrol.topologycanvas.status", "Status")}</Typography.Text>
          <Typography.Text
            style={{
              color: statusTone,
              display: 'block',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            {formatMissionLabel(node.status)}
          </Typography.Text>
        </div>
        <div>
          <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 11 }}>
            {t("pages.missioncontrol.topologycanvas.freshness", "Freshness")}</Typography.Text>
          <Typography.Text
            style={{
              color: observationTone,
              display: 'block',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            {node.freshnessLabel}
          </Typography.Text>
        </div>
        <div>
          <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 11 }}>
            {t("pages.missioncontrol.topologycanvas.handoff", "Handoff")}</Typography.Text>
          <Tag
            color={resolveHandoffTagColor(node.handoff.severity)}
            style={{ display: 'block', fontSize: 11, marginInlineEnd: 0, marginTop: 2 }}
          >
            {formatHandoffSeverityLabel(node.handoff.severity)}
          </Tag>
        </div>
      </div>
      <Handle
        position={Position.Right}
        style={{
          background: token.colorBorder,
          border: 'none',
          height: 8,
          width: 8,
        }}
        type="source"
      />
    </div>
  );
}

function StreamingEdge({
  data,
  label,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
}: EdgeProps<Edge<TopologyEdgeData>>) {
  const { token } = theme.useToken();
  const [edgePath, labelX, labelY] = getSmoothStepPath({
    sourceX,
    sourceY,
    targetX,
    targetY,
    sourcePosition,
    targetPosition,
    borderRadius: 18,
    offset: 28,
  });
  const tone = edgeTone(token, data?.observationStatus || 'streaming');

  return (
    <>
      <BaseEdge
        path={edgePath}
        style={{
          stroke: token.colorBorder,
          strokeWidth: 1.4,
        }}
      />
      {data?.streaming ? (
        <path
          className="mission-topology-flow-edge"
          d={edgePath}
          fill="none"
          stroke={tone}
          strokeDasharray="16 10"
          strokeWidth={2.4}
        />
      ) : (
        <path
          d={edgePath}
          fill="none"
          opacity={0.7}
          stroke={tone}
          strokeDasharray="4 8"
          strokeWidth={2}
        />
      )}
      {label ? (
        <EdgeLabelRenderer>
          <div
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 999,
              color: tone,
              fontSize: 11,
              left: labelX,
              padding: '2px 8px',
              position: 'absolute',
              top: labelY,
              transform: 'translate(-50%, -50%)',
              whiteSpace: 'nowrap',
            }}
          >
            {String(label)}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}

function buildNodes(
  snapshot: MissionControlSnapshot,
  runStatus: MissionRunStatus,
): Node<TopologyNodeData>[] {
  return snapshot.nodes.map((node) => ({
    id: node.id,
    type: 'missionNode',
    position: node.position,
    data: {
      node,
      shouldPulse:
        runStatus === 'waiting_approval' &&
        snapshot.intervention?.nodeId === node.id,
    },
    draggable: false,
    selectable: true,
  }));
}

function buildEdges(snapshot: MissionControlSnapshot): Edge<TopologyEdgeData>[] {
  return snapshot.edges.map((edge) => ({
    id: edge.id,
    source: edge.source,
    target: edge.target,
    type: 'streamingEdge',
    label: edge.label,
    animated: false,
    data: {
      observationStatus: edge.observationStatus,
      streaming: edge.streaming,
    },
  }));
}

const nodeTypes = {
  missionNode: React.memo(TopologyNodeCard),
};

const edgeTypes = {
  streamingEdge: React.memo(StreamingEdge),
};

const TopologyCanvas: React.FC<TopologyCanvasProps> = ({
  activeNodeId,
  connectionMessage,
  connectionStatus,
  onCanvasSelect,
  onNodeSelect,
  snapshot,
}) => {
  const { token } = theme.useToken();
  const isDisconnected = connectionStatus === 'disconnected';
  const showConnectionOverlay =
    connectionStatus === 'idle' ||
    connectionStatus === 'connecting' ||
    connectionStatus === 'degraded' ||
    connectionStatus === 'disconnected' ||
    snapshot.nodes.length === 0;
  const nodes = useMemo(
    () => buildNodes(snapshot, snapshot.summary.status),
    [snapshot.intervention?.nodeId, snapshot.nodes, snapshot.summary.status],
  );
  const edges = useMemo(() => buildEdges(snapshot), [snapshot.edges]);
  const selectedNodes = useMemo(
    () =>
      nodes.map((node) => ({
        ...node,
        selected: node.id === activeNodeId,
      })),
    [activeNodeId, nodes],
  );

  return (
    <div
      style={{
        ['--mission-topology-card-shadow' as string]: token.boxShadowSecondary,
        ['--mission-topology-primary' as string]: token.colorPrimary,
        ['--mission-topology-warning' as string]: token.colorWarning,
        ['--mission-topology-warning-glow' as string]: token.colorWarningBorder,
        background: `linear-gradient(180deg, ${token.colorBgContainer} 0%, ${token.colorBgElevated} 100%)`,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 4,
        height: '100%',
        minHeight: 0,
        overflow: 'hidden',
        position: 'relative',
        width: '100%',
      }}
    >
      <style>{buildTopologyStyles(token)}</style>
      <div
        style={{
          filter: isDisconnected
            ? 'grayscale(0.95) opacity(0.55)'
            : connectionStatus === 'degraded'
              ? 'saturate(0.84) opacity(0.78)'
              : undefined,
          height: '100%',
          transition: 'filter 160ms ease, opacity 160ms ease',
        }}
      >
        <ReactFlow
          defaultViewport={{ x: 0, y: 0, zoom: 0.82 }}
          edgeTypes={edgeTypes}
          edges={edges}
          elementsSelectable
          fitView
          fitViewOptions={{ padding: 0.14, maxZoom: 0.94 }}
          maxZoom={1.2}
          minZoom={0.48}
          nodeTypes={nodeTypes}
          nodes={selectedNodes}
          nodesConnectable={false}
          nodesDraggable={false}
          onNodeClick={(_, node) => onNodeSelect(node.id)}
          onPaneClick={onCanvasSelect}
          panOnDrag
          panOnScroll
          proOptions={{ hideAttribution: true }}
        >
          <Background
            color={token.colorFillSecondary}
            gap={22}
            size={1}
            variant={BackgroundVariant.Dots}
          />
          <MiniMap
            pannable
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 4,
              height: 104,
              width: 164,
            }}
            zoomable
          />
          <Controls
            position="bottom-left"
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 4,
              boxShadow: token.boxShadowSecondary,
            }}
            showInteractive={false}
          />
        </ReactFlow>
      </div>
      {showConnectionOverlay ? (
        <div
          style={{
            alignItems: 'center',
            backdropFilter: 'blur(8px)',
            background:
              connectionStatus === 'disconnected'
                ? 'rgba(15, 23, 42, 0.68)'
                : 'rgba(15, 23, 42, 0.32)',
            display: 'flex',
            flexDirection: 'column',
            gap: 10,
            inset: 0,
            justifyContent: 'center',
            pointerEvents: 'none',
            position: 'absolute',
            textAlign: 'center',
          }}
        >
          <Typography.Text strong style={{ color: token.colorTextLightSolid }}>
            {connectionStatus === 'idle'
              ? t("pages.missioncontrol.topologycanvas.attach.live.run.first", "Attach a live run first")
              : snapshot.nodes.length === 0
                ? t("pages.missioncontrol.topologycanvas.waiting.runtime.topology", "Waiting for runtime topology...")
                : t("pages.missioncontrol.topologycanvas.runtime.connection", "Runtime: {connection}", {
                    connection: formatConnectionLabel(connectionStatus),
                  })}
          </Typography.Text>
          <Typography.Text
            style={{
              color: token.colorTextLightSolid,
              maxWidth: 420,
              opacity: 0.86,
            }}
          >
            {connectionMessage ||
              t("pages.missioncontrol.topologycanvas.synchronizing.runtime.state", "Synchronizing runtime state, topology, and key events.")}
          </Typography.Text>
          <Typography.Text
            style={{
              background: token.colorBgContainer,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 999,
              color:
                resolveConnectionTagColor(connectionStatus) === 'error'
                  ? token.colorError
                  : resolveConnectionTagColor(connectionStatus) === 'warning'
                    ? token.colorWarning
                    : token.colorPrimary,
              padding: '4px 10px',
            }}
          >
            {connectionStatus === 'idle'
              ? t("pages.missioncontrol.topologycanvas.live.run.context.required", "Live Run Context Required")
              : snapshot.nodes.length === 0
                ? t("pages.missioncontrol.topologycanvas.topology.pending", "Topology Pending")
                : formatConnectionLabel(connectionStatus)}
          </Typography.Text>
        </div>
      ) : null}
    </div>
  );
};

export default TopologyCanvas;
