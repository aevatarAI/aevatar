import { Button, Space, Typography, theme } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { AEVATAR_PRESSABLE_CARD_CLASS } from "@/shared/ui/interactionStandards";
import {
  CompactFactValue,
  DetailPill,
  FactLine,
  SignalCard,
} from "../components/TeamDetailPrimitives";

type BindingSummaryCard = {
  readonly caption: string;
  readonly icon?: React.ReactNode;
  readonly label: string;
  readonly value: React.ReactNode;
};

type BindingCatalogCard = {
  readonly availabilityLabel: string;
  readonly availabilityStyle: React.CSSProperties;
  readonly buttonStyle: React.CSSProperties;
  readonly key: string;
  readonly name: string;
  readonly summary: string;
  readonly typeLabel: string;
  readonly typeStyle: React.CSSProperties;
  readonly usageLabel: string;
  readonly usageStyle: React.CSSProperties;
  readonly usageSummary: string;
};

type SelectedBindingRow = {
  readonly badgeStyle: React.CSSProperties;
  readonly badgeText: string;
  readonly label: string;
  readonly note: string;
  readonly value: string;
};

type TeamBindingsTabProps = {
  readonly catalogCards: readonly BindingCatalogCard[];
  readonly emptyDescription: string;
  readonly onOpenDeployments: () => void;
  readonly onOpenGovernance: () => void;
  readonly onOpenServices: () => void;
  readonly onSelectBinding: (bindingKey: string) => void;
  readonly provenanceLabel: string;
  readonly provenanceStyle: React.CSSProperties;
  readonly selectedBindingDetailRows: readonly SelectedBindingRow[];
  readonly selectedBindingEmpty: boolean;
  readonly selectedBindingStatusLabel: string;
  readonly selectedBindingStatusStyle: React.CSSProperties;
  readonly selectedBindingName: string;
  readonly selectedBindingSummary: string;
  readonly summaryCards: readonly BindingSummaryCard[];
};

const TeamBindingsTab: React.FC<TeamBindingsTabProps> = ({
  catalogCards,
  emptyDescription,
  onOpenDeployments,
  onOpenGovernance,
  onOpenServices,
  onSelectBinding,
  provenanceLabel,
  provenanceStyle,
  selectedBindingDetailRows,
  selectedBindingEmpty,
  selectedBindingStatusLabel,
  selectedBindingStatusStyle,
  selectedBindingName,
  selectedBindingSummary,
  summaryCards,
}) => {
  const { token } = theme.useToken();
  const actionButtonStyle: React.CSSProperties = {
    borderRadius: 14,
    height: 36,
    paddingInline: 16,
  };
  const looksIdentifierLike = (value: string): boolean =>
    value.length > 18 && /[:/._-]/.test(value);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="当前绑定与治理摘要"
        extra={
          <DetailPill compact style={provenanceStyle} text={provenanceLabel} />
        }
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
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
                icon={card.icon}
                label={card.label}
                value={card.value}
              />
            ))}
          </div>
          <Space wrap>
            <Button onClick={onOpenServices} style={actionButtonStyle}>
              打开 Services
            </Button>
            <Button onClick={onOpenGovernance} style={actionButtonStyle} type="primary">
              打开 Governance
            </Button>
            <Button onClick={onOpenDeployments} style={actionButtonStyle}>
              打开 Deployments
            </Button>
          </Space>
        </div>
      </AevatarPanel>
      {catalogCards.length > 0 ? (
        <div
          style={{
            display: "grid",
            gap: 16,
            gridTemplateColumns: "minmax(0, 1.2fr) minmax(320px, 0.8fr)",
          }}
        >
          <AevatarPanel
            title="Bindings 与连接能力"
            extra={
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                连接器 · 服务入口 · 策略摘要
              </Typography.Text>
            }
          >
            <div
              style={{
                display: "grid",
                gap: 10,
                gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
              }}
            >
              {catalogCards.map((binding) => (
                <button
                  aria-label={`选择绑定 ${binding.name}`}
                  className={AEVATAR_PRESSABLE_CARD_CLASS}
                  key={binding.key}
                  onClick={() => onSelectBinding(binding.key)}
                  style={{
                    ...binding.buttonStyle,
                    display: "flex",
                    flexDirection: "column",
                    gap: 10,
                    padding: 16,
                    textAlign: "left",
                  }}
                  type="button"
                >
                  <div
                    style={{
                      alignItems: "flex-start",
                      display: "flex",
                      gap: 10,
                      justifyContent: "space-between",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      <Typography.Text strong>{binding.name}</Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {binding.summary}
                      </Typography.Text>
                    </div>
                    <DetailPill compact style={binding.typeStyle} text={binding.typeLabel} />
                  </div>
                  <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
                    <DetailPill
                      compact
                      style={binding.availabilityStyle}
                      text={binding.availabilityLabel}
                    />
                    <DetailPill compact style={binding.usageStyle} text={binding.usageLabel} />
                  </div>
                  <Typography.Text type="secondary">{binding.usageSummary}</Typography.Text>
                </button>
              ))}
            </div>
          </AevatarPanel>
          <AevatarPanel
            title="当前选中绑定"
            extra={
              !selectedBindingEmpty ? (
                <DetailPill
                  compact
                  style={selectedBindingStatusStyle}
                  text={selectedBindingStatusLabel}
                />
              ) : null
            }
          >
            {!selectedBindingEmpty ? (
              <div style={{ display: "flex", flexDirection: "column", gap: 14 }}>
                <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                  <Typography.Title level={3} style={{ margin: 0 }}>
                    {selectedBindingName}
                  </Typography.Title>
                  <Typography.Text type="secondary">
                    {selectedBindingSummary}
                  </Typography.Text>
                </div>
                {selectedBindingDetailRows.map((row) => (
                  <div
                    key={row.label}
                    style={{
                      borderTop: `1px solid ${token.colorBorderSecondary}`,
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "minmax(96px, 120px) minmax(0, 1fr) max-content",
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
                    <DetailPill compact style={row.badgeStyle} text={row.badgeText} />
                  </div>
                ))}
              </div>
            ) : (
              <AevatarInspectorEmpty
                compact
                title="请选择一个绑定"
                description="点击左侧卡片，查看它在这支团队里的接入方式和治理上下文。"
              />
            )}
          </AevatarPanel>
        </div>
      ) : (
        <AevatarPanel
          title="Bindings 视图"
          extra={
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              连接能力 · 服务入口 · 策略摘要
            </Typography.Text>
          }
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              <SignalCard
                label="工作区目录"
                value="暂无绑定能力"
                caption="当前工作区还没有可见的连接器或绑定目录定义。"
              />
              <SignalCard
                label="治理摘要"
                value="等待服务入口"
                caption="一旦 scope binding、策略或 endpoint 暴露可见，这里会自动展开。"
              />
            </div>
            <Typography.Text style={{ fontSize: 13 }} type="secondary">
              {emptyDescription}
            </Typography.Text>
          </div>
        </AevatarPanel>
      )}
    </div>
  );
};

export default TeamBindingsTab;
