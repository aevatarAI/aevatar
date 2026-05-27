import { Button, Space, Typography, theme } from "antd";
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

type OverviewConfigurationRow = {
  readonly label: string;
  readonly note: string;
  readonly noteTooltip?: string;
  readonly value: string;
};

type TeamOverviewTabProps = {
  readonly configurationDetailRows: readonly OverviewConfigurationRow[];
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
  readonly entryMemberId?: string | null;
  readonly entryMemberLabel?: string;
  readonly entryMemberUpdating?: boolean;
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly onClearEntryMember?: () => void;
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
  configurationDetailRows,
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
  entryMemberId,
  entryMemberLabel,
  entryMemberUpdating = false,
  latestVisibleUpdateLabel,
  latestVisibleUpdateNote,
  onClearEntryMember,
}) => {
  const { token } = theme.useToken();
  const hasEntryMember = Boolean(entryMemberId?.trim());

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      <section style={surfaceStyle(token)}>
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
          <SignalCard
            label="入口成员"
            value={entryMemberLabel || entryMemberId || "未配置"}
            caption={
              hasEntryMember
                ? "调用这支 Team 时会先路由到这个成员。"
                : "测试或调用前，请先设置一个入口成员。"
            }
          />
        </div>
        {hasEntryMember && onClearEntryMember ? (
          <div style={{ display: "flex", justifyContent: "flex-end" }}>
            <Button
              loading={entryMemberUpdating}
              onClick={onClearEntryMember}
              size="small"
            >
              清除入口成员
            </Button>
          </div>
        ) : null}
      </section>

      <div
        style={{
          display: "grid",
          gap: 18,
          gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
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

        {configurationDetailRows.length > 0 ? (
          <section style={surfaceStyle(token)}>
            <Typography.Title level={3} style={{ margin: 0 }}>
              配置明细
            </Typography.Title>
            <div style={{ display: "grid", gap: 12 }}>
              {configurationDetailRows.map((row, index) => (
                <div
                  key={row.label}
                  style={{
                    alignItems: "start",
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(96px, 128px) minmax(0, 1fr)",
                    paddingTop: index === 0 ? 0 : 12,
                  }}
                >
                  <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                    {row.label}
                  </Typography.Text>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    <Typography.Text strong>{row.value}</Typography.Text>
                    <FactLine
                      rows={2}
                      secondary
                      text={row.note}
                      tooltipText={row.noteTooltip}
                    />
                  </div>
                </div>
              ))}
            </div>
          </section>
        ) : null}
      </div>
    </div>
  );
};

export default TeamOverviewTab;
