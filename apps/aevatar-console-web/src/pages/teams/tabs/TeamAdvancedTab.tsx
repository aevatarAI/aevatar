import { Button, Space, Typography, theme } from "antd";
import React from "react";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  CompactFactValue,
  DetailPill,
  FactLine,
  SignalCard,
} from "../components/TeamDetailPrimitives";

type AdvancedSummaryCard = {
  readonly caption?: string;
  readonly captionMonospace?: boolean;
  readonly label: string;
  readonly value: string;
};

type AdvancedDetailRow = {
  readonly label: string;
  readonly note: string;
  readonly value: string;
};

type AdvancedAdjustmentRow = {
  readonly badge: string;
  readonly label: string;
  readonly note: string;
  readonly value: string;
};

type TeamAdvancedTabProps = {
  readonly adjustmentBadgeStyle: React.CSSProperties;
  readonly configurationAdjustmentRows: readonly AdvancedAdjustmentRow[];
  readonly configurationDetailRows: readonly AdvancedDetailRow[];
  readonly conversationActionLabel: string;
  readonly currentDeploymentBadgeStyle: React.CSSProperties;
  readonly currentDeploymentFriendly: string;
  readonly currentServiceFriendly: string;
  readonly currentVersionFriendly: string;
  readonly onOpenConversation: () => void;
  readonly onOpenServiceMapping: () => void;
  readonly onOpenTeamBuilder: () => void;
  readonly serviceMappingDisabled?: boolean;
  readonly serviceMappingHint?: string;
  readonly primaryActionButtonStyle: React.CSSProperties;
  readonly secondaryActionButtonStyle: React.CSSProperties;
  readonly serviceMappingActionLabel: string;
  readonly summaryCards: readonly AdvancedSummaryCard[];
  readonly teamBuilderActionLabel: string;
  readonly teamImpactSummary: string;
};

const TeamAdvancedTab: React.FC<TeamAdvancedTabProps> = ({
  adjustmentBadgeStyle,
  configurationAdjustmentRows,
  configurationDetailRows,
  conversationActionLabel,
  currentDeploymentBadgeStyle,
  currentDeploymentFriendly,
  currentServiceFriendly,
  currentVersionFriendly,
  onOpenConversation,
  onOpenServiceMapping,
  onOpenTeamBuilder,
  serviceMappingDisabled = false,
  serviceMappingHint,
  primaryActionButtonStyle,
  secondaryActionButtonStyle,
  serviceMappingActionLabel,
  summaryCards,
  teamBuilderActionLabel,
  teamImpactSummary,
}) => {
  const { token } = theme.useToken();
  const looksIdentifierLike = (value: string): boolean =>
    value.length > 18 && /[:/._-]/.test(value);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel title="当前配置主线">
        <div
          style={{
            display: "grid",
            gap: 12,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          {summaryCards.map((card) => (
            <SignalCard
              key={card.label}
              caption={card.caption}
              captionMonospace={card.captionMonospace}
              label={card.label}
              value={card.value}
            />
          ))}
        </div>
      </AevatarPanel>
      <div
        style={{
          display: "grid",
          gap: 16,
          gridTemplateColumns: "minmax(0, 1fr) minmax(320px, 0.78fr)",
        }}
      >
        <AevatarPanel
          title="当前配置明细"
          extra={
            <DetailPill
              compact
              style={currentDeploymentBadgeStyle}
              text={currentDeploymentFriendly}
            />
          }
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
            {configurationDetailRows.map((row) => (
              <div
                key={row.label}
                style={{
                  borderTop: `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "minmax(96px, 120px) minmax(0, 1fr)",
                  paddingTop: 14,
                }}
              >
                <Typography.Text type="secondary">{row.label}</Typography.Text>
                <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                  {looksIdentifierLike(row.value) ? (
                    <CompactFactValue value={row.value} />
                  ) : (
                    <Typography.Text strong>{row.value}</Typography.Text>
                  )}
                  <FactLine rows={2} secondary text={row.note} />
                </div>
              </div>
            ))}
          </div>
        </AevatarPanel>
        <AevatarPanel title="继续调整这支团队">
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              <Typography.Text strong>
                先确认这次要调整的是流程、服务映射，还是连接器引用。
              </Typography.Text>
              <Typography.Text type="secondary">
                当前会影响 {currentServiceFriendly}、{currentVersionFriendly}，以及
                {teamImpactSummary}。
              </Typography.Text>
            </div>
            <div
              style={{
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 18,
                display: "flex",
                flexDirection: "column",
                overflow: "hidden",
              }}
            >
              {configurationAdjustmentRows.map((row, index) => (
                <div
                  key={row.label}
                  style={{
                    alignItems: "start",
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(84px, 108px) minmax(0, 1fr) max-content",
                    padding: "14px 16px",
                  }}
                >
                  <Typography.Text type="secondary">{row.label}</Typography.Text>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    {looksIdentifierLike(row.value) ? (
                      <CompactFactValue value={row.value} />
                    ) : (
                      <Typography.Text strong>{row.value}</Typography.Text>
                    )}
                    <FactLine monospace={false} rows={2} secondary text={row.note} />
                  </div>
                  <DetailPill compact style={adjustmentBadgeStyle} text={row.badge} />
                </div>
              ))}
            </div>
            <Space wrap>
              <Button
                disabled={serviceMappingDisabled}
                onClick={onOpenServiceMapping}
                style={primaryActionButtonStyle}
                title={serviceMappingDisabled ? serviceMappingHint : undefined}
                type="primary"
              >
                {serviceMappingActionLabel}
              </Button>
              <Button onClick={onOpenTeamBuilder} style={secondaryActionButtonStyle}>
                {teamBuilderActionLabel}
              </Button>
              <Button onClick={onOpenConversation} style={secondaryActionButtonStyle}>
                {conversationActionLabel}
              </Button>
            </Space>
          </div>
        </AevatarPanel>
      </div>
    </div>
  );
};

export default TeamAdvancedTab;
