import { Space, Typography, theme } from "antd";
import React from "react";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
  SignalCard,
} from "../components/TeamDetailPrimitives";

type OverviewCompositionRow = {
  readonly key: string;
  readonly kindLabel: string;
  readonly kindStyle: React.CSSProperties;
  readonly name: string;
  readonly summary: string;
};

type OverviewRuntimeSummaryRow = {
  readonly badge: string;
  readonly badgeStyle: React.CSSProperties;
  readonly key: string;
  readonly label: string;
  readonly note: string;
  readonly noteMonospace?: boolean;
  readonly noteTooltip?: string;
  readonly value: string;
};

type TeamOverviewTabProps = {
  readonly compositionRows: readonly OverviewCompositionRow[];
  readonly currentDeploymentPillStyle: React.CSSProperties;
  readonly currentDeploymentPillText: string;
  readonly currentHeaderStatusFriendly: string;
  readonly currentHeaderStatusStyle: React.CSSProperties;
  readonly currentRunCardCaption: string;
  readonly currentRunCardTooltip: string;
  readonly currentRunFriendly: string;
  readonly currentRunPillStyle: React.CSSProperties;
  readonly currentRunPillText: string;
  readonly currentServiceCardCaption: string;
  readonly currentServiceCardTooltip: string;
  readonly currentServiceFriendly: string;
  readonly currentServicePillStyle: React.CSSProperties;
  readonly currentServicePillText: string;
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly runtimeSummaryRows: readonly OverviewRuntimeSummaryRow[];
};

const surfaceStyle = (
  token: ReturnType<typeof theme.useToken>["token"],
): React.CSSProperties => ({
  background: token.colorBgContainer,
  border: `1px solid ${token.colorBorderSecondary}`,
  borderRadius: 24,
  boxShadow: token.boxShadowSecondary,
  display: "flex",
  flexDirection: "column",
  gap: 18,
  padding: 24,
});

const TeamOverviewTab: React.FC<TeamOverviewTabProps> = ({
  compositionRows,
  currentDeploymentPillStyle,
  currentDeploymentPillText,
  currentHeaderStatusFriendly,
  currentHeaderStatusStyle,
  currentRunCardCaption,
  currentRunCardTooltip,
  currentRunFriendly,
  currentRunPillStyle,
  currentRunPillText,
  currentServiceCardCaption,
  currentServiceCardTooltip,
  currentServiceFriendly,
  currentServicePillStyle,
  currentServicePillText,
  latestVisibleUpdateLabel,
  latestVisibleUpdateNote,
  runtimeSummaryRows,
}) => {
  const { token } = theme.useToken();

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      <div style={surfaceStyle(token)}>
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
            <Space wrap size={8}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                当前态势
              </Typography.Text>
              <DetailPill
                style={currentHeaderStatusStyle}
                text={currentHeaderStatusFriendly}
              />
            </Space>
          </div>
          <Space wrap size={[8, 8]}>
            <DetailPill
              style={currentServicePillStyle}
              text={currentServicePillText}
            />
            <DetailPill
              style={currentDeploymentPillStyle}
              text={currentDeploymentPillText}
            />
            <DetailPill style={currentRunPillStyle} text={currentRunPillText} />
          </Space>
        </div>
        <div
          style={{
            display: "grid",
            gap: 14,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          <SignalCard
            label="当前服务"
            value={currentServiceFriendly}
            caption={currentServiceCardCaption}
            captionTooltip={currentServiceCardTooltip}
          />
          <SignalCard
            label="最近运行"
            value={currentRunFriendly}
            caption={currentRunCardCaption}
            captionTooltip={currentRunCardTooltip}
          />
          <SignalCard
            label="最近一次更新"
            value={latestVisibleUpdateLabel}
            caption={latestVisibleUpdateNote}
          />
        </div>
      </div>

      <div
        style={{
          display: "grid",
          gap: 18,
          gridTemplateColumns: "repeat(auto-fit, minmax(360px, 1fr))",
        }}
      >
        <div style={surfaceStyle(token)}>
          <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
            <div>
              <Typography.Title level={3} style={{ margin: 0 }}>
                团队构成
              </Typography.Title>
            </div>
          </div>
          {compositionRows.length > 0 ? (
            compositionRows.map((row, index) => (
              <div
                key={row.key}
                style={{
                  alignItems: "start",
                  borderTop:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "minmax(120px, 180px) minmax(0, 1fr) max-content",
                  paddingTop: index === 0 ? 0 : 16,
                }}
              >
                <Typography.Text strong>{row.name}</Typography.Text>
                <FactLine rows={3} secondary text={row.summary} />
                <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
              </div>
            ))
          ) : (
            <AevatarInspectorEmpty
              title="暂无团队构成"
              description="当前还没有足够事实来生成团队构成。"
            />
          )}
        </div>

        <div style={surfaceStyle(token)}>
          <div>
            <Typography.Title level={3} style={{ margin: 0 }}>
              运行摘要
            </Typography.Title>
          </div>
          {runtimeSummaryRows.map((row, index) => (
            <div
              key={row.key}
              style={{
                alignItems: "start",
                borderTop:
                  index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                display: "grid",
                gap: 12,
                gridTemplateColumns: "minmax(96px, 128px) minmax(0, 1fr) max-content",
                paddingTop: index === 0 ? 0 : 16,
              }}
            >
              <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                {row.label}
              </Typography.Text>
              <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                <FactLine rows={2} text={String(row.value)} />
                <FactLine
                  monospace={row.noteMonospace ?? false}
                  rows={3}
                  secondary
                  text={String(row.note)}
                  tooltipText={row.noteTooltip}
                />
              </div>
              <div
                style={{
                  alignSelf: "start",
                  display: "flex",
                  justifyContent: "flex-end",
                  minWidth: 0,
                  paddingTop: 2,
                }}
              >
                <DetailPill compact style={row.badgeStyle} text={row.badge} />
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

export default TeamOverviewTab;
