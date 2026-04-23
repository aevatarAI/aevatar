import { Button, Space, Typography, theme } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { AEVATAR_PRESSABLE_CARD_CLASS } from "@/shared/ui/interactionStandards";
import {
  DetailPill,
  FactLine,
  CompactFactValue,
  SignalCard,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";

type MemberCompositionRow = {
  readonly key: string;
  readonly kindLabel: string;
  readonly kindStyle: React.CSSProperties;
  readonly name: string;
  readonly summary: string;
};

type MemberIdentityRow = {
  readonly actorId: string;
  readonly cardStyle: React.CSSProperties;
  readonly implementationKind: string;
  readonly key: string;
  readonly member: string;
  readonly note: string;
  readonly relationLabel: string;
  readonly serviceId: string;
  readonly statusLabel: string;
  readonly statusStyle: React.CSSProperties;
};

type TeamMembersTabProps = {
  readonly compositionRows: readonly MemberCompositionRow[];
  readonly identityRows: readonly MemberIdentityRow[];
  readonly openRuntimeExplorerDisabled?: boolean;
  readonly openRuntimeExplorerHint?: string;
  readonly onOpenRuntimeExplorer: () => void;
  readonly onOpenServices: () => void;
  readonly onSelectActor: (actorId: string) => void;
};

const TeamMembersTab: React.FC<TeamMembersTabProps> = ({
  compositionRows,
  identityRows,
  openRuntimeExplorerDisabled = false,
  openRuntimeExplorerHint,
  onOpenRuntimeExplorer,
  onOpenServices,
  onSelectActor,
}) => {
  const { token } = theme.useToken();
  const showMembersOverviewEmpty =
    compositionRows.length === 0 && identityRows.length === 0;
  const actionButtonStyle: React.CSSProperties = {
    borderRadius: 14,
    height: 36,
    paddingInline: 16,
  };

  if (showMembersOverviewEmpty) {
    return (
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <AevatarPanel
          title="成员视图"
          extra={
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              participants semantics · 结构 · 运行时身份
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
                label="团队结构"
                value="暂无角色定义"
                caption="当前还没有 workflow 角色定义或可见的团队结构信息。"
              />
              <SignalCard
                label="运行时身份"
                value="暂无可见 Actor"
                caption="当前还没有观察到这支团队的运行时实体身份。"
              />
            </div>
            <Typography.Text style={{ fontSize: 13 }} type="secondary">
              等团队开始运行后，这里会自动出现角色结构和可见 Actor。
            </Typography.Text>
          </div>
        </AevatarPanel>
      </div>
    );
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="参与者结构"
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            角色 · 职责 · 实现
          </Typography.Text>
        }
      >
        {compositionRows.length > 0 ? (
          <div
            style={{
              border: "1px solid var(--ant-colorBorderSecondary)",
              borderRadius: 18,
              overflow: "hidden",
            }}
          >
            <div style={{ overflowX: "auto" }}>
              <div style={{ minWidth: 720 }}>
                <div
                  style={{
                    background: "var(--ant-colorBgContainerDisabled)",
                    borderBottom: "1px solid var(--ant-colorBorderSecondary)",
                    color: "var(--ant-colorTextSecondary)",
                    display: "grid",
                    fontSize: 12,
                    fontWeight: 600,
                    gap: 16,
                    gridTemplateColumns:
                      "minmax(140px, 1fr) minmax(280px, 2fr) minmax(120px, 0.9fr)",
                    padding: "12px 16px",
                  }}
                >
                  <span>角色</span>
                  <span>职责</span>
                  <span>实现</span>
                </div>
                {compositionRows.map((row, index) => (
                  <div
                    key={row.key}
                    style={{
                      alignItems: "center",
                      borderTop:
                        index === 0 ? "none" : "1px solid var(--ant-colorBorderSecondary)",
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(140px, 1fr) minmax(280px, 2fr) minmax(120px, 0.9fr)",
                      padding: "14px 16px",
                    }}
                  >
                    <Typography.Text strong>{row.name}</Typography.Text>
                    <FactLine rows={2} text={row.summary} />
                    <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
                  </div>
                ))}
              </div>
            </div>
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title="暂时还没有团队结构"
            description="当前还没有 workflow 角色定义或可见的团队结构信息。"
          />
        )}
      </AevatarPanel>
      <AevatarPanel
        title="运行时参与者身份"
        extra={
          <Space wrap size={8}>
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              actorId · serviceId · implementation kind
            </Typography.Text>
            <Button onClick={onOpenServices} size="small" style={actionButtonStyle}>
              打开 Services
            </Button>
            <Button
              disabled={openRuntimeExplorerDisabled}
              onClick={onOpenRuntimeExplorer}
              size="small"
              style={actionButtonStyle}
              title={openRuntimeExplorerDisabled ? openRuntimeExplorerHint : undefined}
            >
              查看拓扑
            </Button>
          </Space>
        }
      >
        {identityRows.length > 0 ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            {identityRows.map((row) => (
              <button
                aria-label={`选择成员 ${row.member} ${row.actorId}`}
                className={AEVATAR_PRESSABLE_CARD_CLASS}
                key={row.key}
                onClick={() => onSelectActor(row.actorId)}
                style={{
                  ...row.cardStyle,
                  alignItems: "center",
                  display: "grid",
                  gap: 16,
                  gridTemplateColumns:
                    "minmax(140px, 1fr) minmax(240px, 1.5fr) minmax(180px, 1.1fr) max-content",
                  padding: "14px 16px",
                  textAlign: "left",
                }}
                type="button"
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                  <Typography.Text strong>{row.member}</Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {row.relationLabel}
                  </Typography.Text>
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                  <div
                    style={{
                      alignItems: "center",
                      display: "flex",
                      gap: 6,
                      minWidth: 0,
                    }}
                  >
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      actorId
                    </Typography.Text>
                    <CompactFactValue value={row.actorId} />
                  </div>
                  <div
                    style={{
                      alignItems: "center",
                      display: "flex",
                      gap: 6,
                      minWidth: 0,
                    }}
                  >
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      serviceId
                    </Typography.Text>
                    <CompactFactValue
                      color="var(--ant-color-text-secondary)"
                      strong={false}
                      value={row.serviceId}
                    />
                  </div>
                  <FactLine rows={2} secondary text={row.note} />
                </div>
                <div
                  style={{
                    background: token.colorFillAlter,
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 16,
                    display: "flex",
                    flexDirection: "column",
                    gap: 4,
                    minWidth: 0,
                    padding: "10px 12px",
                  }}
                >
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    实现类型
                  </Typography.Text>
                  <Typography.Text style={{ fontFamily: factValueFontFamily }}>
                    {row.implementationKind}
                  </Typography.Text>
                </div>
                <DetailPill compact style={row.statusStyle} text={row.statusLabel} />
              </button>
            ))}
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title="暂时还没有可见 Actor"
            description="当前还没有观察到这支团队的运行时参与者身份。"
          />
        )}
      </AevatarPanel>
    </div>
  );
};

export default TeamMembersTab;
