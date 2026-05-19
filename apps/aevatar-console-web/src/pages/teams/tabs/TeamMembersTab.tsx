import { Typography } from "antd";
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
  readonly description: string;
  readonly implementationKind: string;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
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
};

const TeamMembersTab: React.FC<TeamMembersTabProps> = ({
  rosterError = false,
  rosterLoading = false,
  rosterRows = [],
  rosterSyncing = false,
  rosterTeamId = "",
}) => {
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
              <div style={{ minWidth: 820 }}>
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
                      "minmax(160px, 1.2fr) minmax(220px, 1.4fr) minmax(120px, 0.8fr) minmax(120px, 0.8fr)",
                    padding: "12px 16px",
                  }}
                >
                  <span>成员</span>
                  <span>职责</span>
                  <span>实现</span>
                  <span>服务</span>
                </div>
                {rosterRows.map((row, index) => (
                  <div
                    key={row.key}
                    style={{
                      alignItems: "center",
                      borderTop:
                        index === 0 ? "none" : "1px solid var(--ant-colorBorderSecondary)",
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns:
                        "minmax(160px, 1.2fr) minmax(220px, 1.4fr) minmax(120px, 0.8fr) minmax(120px, 0.8fr)",
                      padding: "14px 16px",
                    }}
                  >
                    <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                      <Typography.Text strong>{row.name}</Typography.Text>
                      <Typography.Text
                        style={{ fontFamily: factValueFontFamily, fontSize: 12 }}
                        type="secondary"
                      >
                        {row.memberId}
                      </Typography.Text>
                    </div>
                    <FactLine rows={2} text={row.description || `归属 Team ${rosterTeamId || "--"}`} />
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
                  </div>
                ))}
              </div>
            </div>
          </div>
        ) : rosterTeamId ? (
          <AevatarInspectorEmpty
            compact
            title="这支 Team 还没有成员"
            description="Team 已经是后端事实，但当前 roster 为空。新增 member 后会出现在这里。"
          />
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
