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

type OverviewGovernanceRow = {
  readonly badge: string;
  readonly badgeStyle: React.CSSProperties;
  readonly key: string;
  readonly label: string;
  readonly note: string;
  readonly value: string;
};

type OverviewCompareSection = {
  readonly items: readonly string[];
  readonly key: string;
  readonly title: string;
};

type TeamOverviewTabProps = {
  readonly compareAvailable: boolean;
  readonly compareSections: readonly OverviewCompareSection[];
  readonly compareStatusLabel: string;
  readonly compareStatusStyle: React.CSSProperties;
  readonly compareSummary: string;
  readonly compareTitle: string;
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
  readonly governanceRows: readonly OverviewGovernanceRow[];
  readonly healthActionLabel: string;
  readonly healthDetails: readonly string[];
  readonly healthStatusLabel: string;
  readonly healthStatusStyle: React.CSSProperties;
  readonly healthSummary: string;
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly partialSignals: readonly string[];
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

const decisionSurfaceStyle = (
  token: ReturnType<typeof theme.useToken>["token"],
): React.CSSProperties => ({
  ...surfaceStyle(token),
  gap: 16,
  minHeight: "100%",
});

const TeamOverviewTab: React.FC<TeamOverviewTabProps> = ({
  compareAvailable,
  compareSections,
  compareStatusLabel,
  compareStatusStyle,
  compareSummary,
  compareTitle,
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
  governanceRows,
  healthActionLabel,
  healthDetails,
  healthStatusLabel,
  healthStatusStyle,
  healthSummary,
  latestVisibleUpdateLabel,
  latestVisibleUpdateNote,
  partialSignals,
  runtimeSummaryRows,
}) => {
  const { token } = theme.useToken();

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
        </div>
      </section>

      <div
        style={{
          display: "grid",
          gap: 18,
          gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
        }}
      >
        <section style={decisionSurfaceStyle(token)}>
          <div
            style={{
              alignItems: "flex-start",
              display: "flex",
              gap: 12,
              justifyContent: "space-between",
            }}
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                信任态势
              </Typography.Text>
              <Typography.Title level={3} style={{ margin: 0 }}>
                {healthActionLabel}
              </Typography.Title>
            </div>
            <DetailPill style={healthStatusStyle} text={healthStatusLabel} />
          </div>
          <FactLine monospace={false} rows={3} secondary text={healthSummary} />
          <div style={{ display: "grid", gap: 10 }}>
            {healthDetails.length > 0 ? (
              healthDetails.map((detail, index) => (
                <div
                  key={`${detail}-${index}`}
                  style={{
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    paddingTop: index === 0 ? 0 : 10,
                  }}
                >
                  <FactLine monospace={false} rows={2} text={detail} />
                </div>
              ))
            ) : (
              <Typography.Text type="secondary">
                当前没有更多健康说明。
              </Typography.Text>
            )}
          </div>
          {partialSignals.length > 0 ? (
            <div
              style={{
                background: token.colorFillQuaternary,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                display: "flex",
                flexDirection: "column",
                gap: 8,
                padding: 14,
              }}
            >
              <Space wrap size={8}>
                <DetailPill
                  compact
                  style={{
                    background: token.colorInfoBg,
                    color: token.colorInfo,
                  }}
                  text="部分信号"
                />
                <Typography.Text type="secondary">
                  缺失事实不会被当作健康处理。
                </Typography.Text>
              </Space>
              {partialSignals.map((signal) => (
                <FactLine
                  key={signal}
                  monospace={false}
                  rows={2}
                  secondary
                  text={signal}
                />
              ))}
            </div>
          ) : null}
        </section>

        <section style={decisionSurfaceStyle(token)}>
          <div
            style={{
              alignItems: "flex-start",
              display: "flex",
              gap: 12,
              justifyContent: "space-between",
            }}
          >
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                治理快照
              </Typography.Text>
              <Typography.Text type="secondary">
                支撑当前信任判断的版本、审计和回退事实。
              </Typography.Text>
            </div>
          </div>
          <div style={{ display: "grid", gap: 12 }}>
            {governanceRows.map((row, index) => (
              <div
                key={row.key}
                style={{
                  alignItems: "start",
                  borderTop:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "minmax(96px, 128px) minmax(0, 1fr) max-content",
                  paddingTop: index === 0 ? 0 : 12,
                }}
              >
                <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                  {row.label}
                </Typography.Text>
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <FactLine rows={2} text={row.value} />
                  <FactLine monospace={false} rows={3} secondary text={row.note} />
                </div>
                <DetailPill compact style={row.badgeStyle} text={row.badge} />
              </div>
            ))}
          </div>
        </section>
      </div>

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
            <Typography.Title level={3} style={{ margin: 0 }}>
              {compareTitle}
            </Typography.Title>
            <FactLine monospace={false} rows={2} secondary text={compareSummary} />
          </div>
          <DetailPill compact style={compareStatusStyle} text={compareStatusLabel} />
        </div>
        {compareAvailable && compareSections.length > 0 ? (
          <div
            style={{
              display: "grid",
              gap: 14,
              gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
            }}
          >
            {compareSections.map((section) => (
              <div
                key={section.key}
                style={{
                  background: token.colorFillAlter,
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 18,
                  display: "flex",
                  flexDirection: "column",
                  gap: 10,
                  padding: 16,
                }}
              >
                <Typography.Text strong>{section.title}</Typography.Text>
                {section.items.map((item, index) => (
                  <div
                    key={`${section.key}-${item}-${index}`}
                    style={{
                      borderTop:
                        index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                      paddingTop: index === 0 ? 0 : 10,
                    }}
                  >
                    <FactLine monospace={false} rows={3} secondary text={item} />
                  </div>
                ))}
              </div>
            ))}
          </div>
        ) : (
          <AevatarInspectorEmpty title="暂无可比较运行" description={compareSummary} />
        )}
      </section>

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
