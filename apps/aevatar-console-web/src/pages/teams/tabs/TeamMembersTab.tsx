import { CheckCircleOutlined, EditOutlined, ToolOutlined } from "@ant-design/icons";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
  CompactFactValue,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";

type TeamRosterMemberRow = {
  readonly canInvokeAsEntry: boolean;
  readonly description: string;
  readonly implementationKind: string;
  readonly isEntryMember?: boolean;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly buildStudioHref: string;
  readonly editStudioHref: string;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
};

type TeamMembersTabProps = {
  readonly rosterError?: boolean;
  readonly rosterLoading?: boolean;
  readonly rosterRows?: readonly TeamRosterMemberRow[];
  readonly rosterSyncing?: boolean;
  readonly rosterTeamId?: string;
  readonly createMemberHref?: string;
  readonly entryActionBusyMemberId?: string;
  readonly onClearEntry?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onSetEntry?: (memberId: string) => void;
};

const ellipsisTextStyle: React.CSSProperties = {
  display: "block",
  maxWidth: "100%",
  minWidth: 0,
  overflow: "hidden",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

const EllipsisText: React.FC<{
  readonly children: string;
  readonly monospace?: boolean;
  readonly strong?: boolean;
  readonly style?: React.CSSProperties;
  readonly type?: "secondary";
}> = ({ children, monospace = false, strong = false, style, type }) => (
  <Tooltip placement="topLeft" title={children}>
    <Typography.Text
      strong={strong}
      style={{
        ...ellipsisTextStyle,
        fontFamily: monospace ? factValueFontFamily : undefined,
        ...style,
      }}
      type={type}
    >
      {children}
    </Typography.Text>
  </Tooltip>
);

const TeamMembersTab: React.FC<TeamMembersTabProps> = ({
  createMemberHref = "",
  entryActionBusyMemberId = "",
  onClearEntry,
  onNavigate,
  onSetEntry,
  rosterError = false,
  rosterLoading = false,
  rosterRows = [],
  rosterSyncing = false,
  rosterTeamId = "",
}) => {
  const { token } = theme.useToken();
  const isEntryActionBusy = entryActionBusyMemberId.trim().length > 0;
  const handleNavigate = React.useCallback(
    (href: string) => (event: React.MouseEvent<HTMLElement>) => {
      if (!href || !onNavigate) {
        return;
      }

      event.preventDefault();
      onNavigate(href);
    },
    [onNavigate],
  );

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <AevatarPanel
        title="团队成员"
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {rosterRows.length > 0 ? `${rosterRows.length} 个成员` : "成员清单"}
          </Typography.Text>
        }
      >
        {rosterSyncing ? (
          <AevatarInspectorEmpty
            compact
            title="成员清单正在同步"
            description="Team 已创建，成员清单正在同步。这里会自动刷新。"
          />
        ) : rosterLoading ? (
          <AevatarInspectorEmpty
            compact
            title="正在读取成员清单"
            description="正在读取这支 Team 的成员。"
          />
        ) : rosterError ? (
          <AevatarInspectorEmpty
            compact
            title="成员清单暂不可见"
            description="当前无法读取这支 Team 的成员清单。"
          />
        ) : rosterRows.length > 0 ? (
          <div
            style={{
              border: "1px solid var(--ant-colorBorderSecondary)",
              borderRadius: 18,
              overflow: "hidden",
            }}
          >
            <div style={{ overflowX: "auto" }}>
              <div style={{ minWidth: 980 }}>
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
                      "minmax(160px, 1.1fr) minmax(220px, 1.4fr) minmax(120px, 0.7fr) minmax(120px, 0.7fr) minmax(260px, max-content)",
                    padding: "12px 16px",
                  }}
                >
                  <span>成员</span>
                  <span>职责</span>
                  <span>实现</span>
                  <span>服务</span>
                  <span>操作</span>
                </div>
                {rosterRows.map((row, index) => (
                  <div
                    key={row.key}
                    style={{
                      alignItems: "center",
                      background: row.isEntryMember
                        ? "linear-gradient(90deg, var(--ant-colorPrimaryBg) 0%, var(--ant-colorBgContainer) 34%)"
                        : undefined,
                      borderTop:
                        index === 0 ? "none" : "1px solid var(--ant-colorBorderSecondary)",
                      boxShadow: row.isEntryMember
                        ? "inset 4px 0 0 var(--ant-colorPrimary)"
                        : undefined,
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(160px, 1.1fr) minmax(220px, 1.4fr) minmax(120px, 0.7fr) minmax(120px, 0.7fr) minmax(260px, max-content)",
                      padding: "14px 16px",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                      <div
                        style={{
                          alignItems: "center",
                          display: "flex",
                          gap: 8,
                          minWidth: 0,
                        }}
                      >
                        <EllipsisText strong>{row.name}</EllipsisText>
                        {row.isEntryMember ? (
                          <DetailPill
                            compact
                            style={{
                              background: token.colorSuccessBg,
                              border: `1px solid ${token.colorSuccessBorder}`,
                              color: token.colorSuccess,
                            }}
                            text="入口成员"
                          />
                        ) : null}
                      </div>
                      <EllipsisText
                        monospace
                        style={{
                          fontSize: 12,
                        }}
                        type="secondary"
                      >
                        {row.memberId}
                      </EllipsisText>
                    </div>
                    <div style={{ minWidth: 0 }}>
                      <FactLine rows={1} text={row.description || `归属 Team ${rosterTeamId || "--"}`} />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      <DetailPill compact style={row.lifecycleStyle} text={row.lifecycleLabel} />
                      <Typography.Text style={{ fontFamily: factValueFontFamily, fontSize: 12 }}>
                        {row.implementationKind}
                      </Typography.Text>
                    </div>
                    <CompactFactValue
                      color="var(--ant-color-text-secondary)"
                      strong={false}
                      value={row.serviceId}
                    />
                    <Space wrap size={8}>
                      {row.isEntryMember ? (
                        <Button
                          icon={<CheckCircleOutlined />}
                          disabled={
                            isEntryActionBusy && entryActionBusyMemberId !== row.memberId
                          }
                          loading={entryActionBusyMemberId === row.memberId}
                          onClick={onClearEntry}
                          size="small"
                        >
                          清除入口成员
                        </Button>
                      ) : row.canInvokeAsEntry && onSetEntry ? (
                        <Button
                          disabled={
                            isEntryActionBusy && entryActionBusyMemberId !== row.memberId
                          }
                          loading={entryActionBusyMemberId === row.memberId}
                          onClick={() => onSetEntry(row.memberId)}
                          size="small"
                        >
                          Set entry
                        </Button>
                      ) : null}
                      <Button
                        href={row.editStudioHref}
                        icon={<EditOutlined />}
                        onClick={handleNavigate(row.editStudioHref)}
                        size="small"
                      >
                        Edit in Studio
                      </Button>
                      <Button
                        href={row.buildStudioHref}
                        icon={<ToolOutlined />}
                        onClick={handleNavigate(row.buildStudioHref)}
                        size="small"
                        type="primary"
                      >
                        Build
                      </Button>
                    </Space>
                  </div>
                ))}
              </div>
            </div>
          </div>
        ) : rosterTeamId ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <AevatarInspectorEmpty
              compact
              title="这支 Team 还没有成员"
              description="Team 已经是后端事实，但当前 roster 为空。新增 member 后会出现在这里。"
            />
            {createMemberHref ? (
              <div style={{ display: "flex", justifyContent: "center" }}>
                <Button
                  href={createMemberHref}
                  onClick={handleNavigate(createMemberHref)}
                  type="primary"
                >
                  创建第一个成员
                </Button>
              </div>
            ) : null}
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title="尚未选中真实 Team"
            description="当前路由还没有 teamId，所以只能展示运行时观察到的成员身份。"
          />
        )}
      </AevatarPanel>
    </div>
  );
};

export default TeamMembersTab;
