import type { Edge, Node } from "@xyflow/react";
import { Button, Space, Typography, theme } from "antd";
import React from "react";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";

type TopologyDetailDisplayRow = {
  readonly badge: string;
  readonly badgeStyle: React.CSSProperties;
  readonly label: string;
  readonly note: string;
  readonly noteMonospace?: boolean;
  readonly noteRows?: number;
  readonly value: string;
  readonly valueMonospace?: boolean;
  readonly valueRows?: number;
};

type TeamTopologyTabProps = {
  readonly graphDepth: number;
  readonly graphEdgeCount: number;
  readonly graphFocusLabel: string;
  readonly graphNodeCount: number;
  readonly isError: boolean;
  readonly isLoading: boolean;
  readonly onCanvasSelect: () => void;
  readonly onNodeSelect: (nodeId: string) => void;
  readonly onOpenPlatformTopology: () => void;
  readonly onSetGraphDepth: (depth: number) => void;
  readonly openPlatformTopologyButtonStyle: React.CSSProperties;
  readonly provenanceLabel: string;
  readonly provenanceStyle: React.CSSProperties;
  readonly selectedEntityBadgeLabel?: string;
  readonly selectedEntityBadgeStyle?: React.CSSProperties;
  readonly selectedEntityDetailRows: readonly TopologyDetailDisplayRow[];
  readonly selectedEntityEmpty: boolean;
  readonly selectedEntityKindLabel?: string;
  readonly selectedEntityKindStyle?: React.CSSProperties;
  readonly selectedEntitySummary?: string;
  readonly selectedEntityTitle?: string;
  readonly selectedFocusReason: string;
  readonly selectedNodeId: string;
  readonly topologyEdges: readonly Edge[];
  readonly topologyNodes: readonly Node[];
};

const TeamTopologyTab: React.FC<TeamTopologyTabProps> = ({
  graphDepth,
  graphEdgeCount,
  graphFocusLabel,
  graphNodeCount,
  isError,
  isLoading,
  onCanvasSelect,
  onNodeSelect,
  onOpenPlatformTopology,
  onSetGraphDepth,
  openPlatformTopologyButtonStyle,
  provenanceLabel,
  provenanceStyle,
  selectedEntityBadgeLabel,
  selectedEntityBadgeStyle,
  selectedEntityDetailRows,
  selectedEntityEmpty,
  selectedEntityKindLabel,
  selectedEntityKindStyle,
  selectedEntitySummary,
  selectedEntityTitle,
  selectedFocusReason,
  selectedNodeId,
  topologyEdges,
  topologyNodes,
}) => {
  const { token } = theme.useToken();

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
      <div
        style={{
          alignItems: "flex-start",
          background: token.colorBgContainer,
          border: `1px solid ${token.colorBorderSecondary}`,
          borderRadius: 24,
          boxShadow: token.boxShadowSecondary,
          display: "flex",
          flexWrap: "wrap",
          gap: 16,
          justifyContent: "space-between",
          padding: 20,
        }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
          <Space wrap size={8}>
            <Typography.Text strong style={{ fontSize: 16 }}>
              当前拓扑视角
            </Typography.Text>
            <DetailPill compact style={provenanceStyle} text={provenanceLabel} />
          </Space>
          <Typography.Text style={{ fontSize: 13 }} type="secondary">
            {selectedFocusReason || "围绕当前焦点成员展开团队消息路径。点击左侧节点即可切换视角。"}
          </Typography.Text>
        </div>
        <Space size={10} wrap>
          <div
            aria-label="拓扑深度"
            role="group"
            style={{
              alignItems: "center",
              background: token.colorFillAlter,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 999,
              display: "inline-flex",
              gap: 4,
              padding: 4,
            }}
          >
            {[1, 2, 3].map((depth) => {
              const active = graphDepth === depth;
              return (
                <button
                  key={depth}
                  onClick={() => onSetGraphDepth(depth)}
                  style={{
                    background: active ? token.colorPrimaryBg : "transparent",
                    border: "none",
                    borderRadius: 999,
                    color: active ? token.colorPrimary : token.colorTextSecondary,
                    cursor: "pointer",
                    fontSize: 13,
                    fontWeight: active ? 700 : 500,
                    height: 32,
                    padding: "0 14px",
                    transition: "all 140ms ease",
                  }}
                  type="button"
                >
                  {depth === 1 ? "近邻" : depth === 3 ? "全景" : "扩展"}
                </button>
              );
            })}
          </div>
          <Button
            onClick={onOpenPlatformTopology}
            style={openPlatformTopologyButtonStyle}
            type="primary"
          >
            打开平台拓扑
          </Button>
        </Space>
      </div>
      {isLoading ? (
        <AevatarInspectorEmpty description="正在加载团队拓扑。" />
      ) : isError ? (
        <AevatarInspectorEmpty
          title="拓扑暂不可用"
          description="当前无法读取团队拓扑，请稍后重试。"
        />
      ) : (
        <div
          style={{
            display: "grid",
            gap: 18,
            gridTemplateColumns: "minmax(0, 1.2fr) minmax(320px, 0.88fr)",
          }}
        >
          <AevatarPanel title="团队事件路径">
            {topologyNodes.length > 0 ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                <div
                  style={{
                    alignItems: "flex-start",
                    display: "flex",
                    flexWrap: "wrap",
                    gap: 12,
                    justifyContent: "space-between",
                  }}
                >
                  <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    <Typography.Text strong style={{ fontSize: 15 }}>
                      从当前焦点成员出发，查看消息如何流向服务与连接器
                    </Typography.Text>
                    <Typography.Text style={{ fontSize: 13 }} type="secondary">
                      {`当前视图包含 ${graphNodeCount} 个节点，${graphEdgeCount} 条连线。`}
                    </Typography.Text>
                  </div>
                  <Typography.Text style={{ fontSize: 13 }} type="secondary">
                    {graphFocusLabel}
                  </Typography.Text>
                </div>
                <GraphCanvas
                  edges={[...topologyEdges]}
                  height={384}
                  nodes={[...topologyNodes]}
                  onCanvasSelect={onCanvasSelect}
                  onNodeSelect={onNodeSelect}
                  selectedNodeId={selectedNodeId}
                />
                <Space size={[8, 8]} wrap>
                  <DetailPill
                    compact
                    style={{
                      background: token.colorInfoBg,
                      color: token.colorInfo,
                    }}
                    text="实线关系 = 运行事实"
                  />
                  <DetailPill
                    compact
                    style={{
                      background: "rgba(250, 173, 20, 0.12)",
                      color: token.colorWarning,
                    }}
                    text="虚线关系 = 配置推导"
                  />
                  <DetailPill
                    compact
                    style={{
                      background: token.colorFillQuaternary,
                      color: token.colorTextSecondary,
                    }}
                    text="节点语义来自成员、服务、连接器的当前事实"
                  />
                </Space>
              </div>
            ) : (
              <AevatarInspectorEmpty
                title="暂无可见关系"
                description="当前没有更多可见的事件拓扑关系。"
              />
            )}
          </AevatarPanel>
          <AevatarPanel
            title="当前选中节点"
            extra={
              !selectedEntityEmpty && selectedEntityBadgeLabel && selectedEntityBadgeStyle ? (
                <DetailPill
                  compact
                  style={selectedEntityBadgeStyle}
                  text={selectedEntityBadgeLabel}
                />
              ) : undefined
            }
          >
            {!selectedEntityEmpty ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
                <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                  <Space wrap size={8}>
                    <Typography.Title level={3} style={{ margin: 0 }}>
                      {selectedEntityTitle}
                    </Typography.Title>
                    {selectedEntityKindLabel && selectedEntityKindStyle ? (
                      <DetailPill
                        compact
                        style={selectedEntityKindStyle}
                        text={selectedEntityKindLabel}
                      />
                    ) : null}
                  </Space>
                  <Typography.Text type="secondary">
                    {selectedEntitySummary}
                  </Typography.Text>
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                  {selectedEntityDetailRows.map((row, index) => (
                    <div
                      key={`${row.label}-${index}`}
                      style={{
                        alignItems: "start",
                        borderTop:
                          index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "minmax(88px, 120px) minmax(0, 1fr) max-content",
                        paddingTop: index === 0 ? 0 : 14,
                      }}
                    >
                      <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                        {row.label}
                      </Typography.Text>
                      <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                        <FactLine
                          monospace={row.valueMonospace ?? false}
                          rows={row.valueRows ?? 2}
                          text={String(row.value)}
                        />
                        <FactLine
                          monospace={row.noteMonospace ?? false}
                          rows={row.noteRows ?? 2}
                          secondary
                          text={String(row.note)}
                        />
                      </div>
                      <DetailPill compact style={row.badgeStyle} text={row.badge} />
                    </div>
                  ))}
                </div>
              </div>
            ) : (
              <AevatarInspectorEmpty
                title="当前还没有选中节点"
                description="请先从左侧团队事件路径里选择一个节点。"
              />
            )}
          </AevatarPanel>
        </div>
      )}
    </div>
  );
};

export default TeamTopologyTab;
