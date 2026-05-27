import { CheckCircleOutlined, EditOutlined, ToolOutlined } from "@ant-design/icons";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import { useTranslation } from "@/shared/i18n/localization";
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

const memberTableMinWidth = 1380;

const memberTableColumns = [
  { key: "member", width: 260 },
  { key: "role", width: 320 },
  { key: "implementation", width: 170 },
  { key: "service", width: 190 },
  { key: "actions", width: 440 },
] as const;

const memberTableHeaderCellStyle: React.CSSProperties = {
  color: "var(--ant-colorTextSecondary)",
  fontSize: 12,
  fontWeight: 600,
  padding: "12px 16px",
  textAlign: "left",
  whiteSpace: "nowrap",
};

const memberTableCellStyle: React.CSSProperties = {
  minWidth: 0,
  padding: "14px 16px",
  verticalAlign: "middle",
};

const memberTableGridCellStyle: React.CSSProperties = {
  minWidth: 0,
  width: "100%",
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
  const { t } = useTranslation();
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
        title={t("team.members.title")}
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {rosterRows.length > 0
              ? t("team.members.count", { count: rosterRows.length })
              : t("team.members.roster")}
          </Typography.Text>
        }
      >
        {rosterSyncing ? (
          <AevatarInspectorEmpty
            compact
            title={t("team.members.syncing.title")}
            description={t("team.members.syncing.description")}
          />
        ) : rosterLoading ? (
          <AevatarInspectorEmpty
            compact
            title={t("team.members.loading.title")}
            description={t("team.members.loading.description")}
          />
        ) : rosterError ? (
          <AevatarInspectorEmpty
            compact
            title={t("team.members.error.title")}
            description={t("team.members.error.description")}
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
              <table
                style={{
                  borderCollapse: "separate",
                  borderSpacing: 0,
                  minWidth: memberTableMinWidth,
                  tableLayout: "fixed",
                  width: "100%",
                }}
              >
                <colgroup>
                  {memberTableColumns.map((column) => (
                    <col key={column.key} style={{ width: column.width }} />
                  ))}
                </colgroup>
                <thead>
                  <tr
                    style={{
                      background: "var(--ant-colorBgContainerDisabled)",
                    }}
                  >
                    <th style={memberTableHeaderCellStyle} scope="col">
                      {t("team.members.columns.member")}
                    </th>
                    <th style={memberTableHeaderCellStyle} scope="col">
                      {t("team.members.columns.role")}
                    </th>
                    <th style={memberTableHeaderCellStyle} scope="col">
                      {t("team.members.columns.implementation")}
                    </th>
                    <th style={memberTableHeaderCellStyle} scope="col">
                      {t("team.members.columns.service")}
                    </th>
                    <th
                      style={{
                        ...memberTableHeaderCellStyle,
                        textAlign: "right",
                      }}
                      scope="col"
                    >
                      {t("team.members.columns.actions")}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {rosterRows.map((row, index) => (
                    <tr
                      key={row.key}
                      style={{
                        background: row.isEntryMember
                          ? "linear-gradient(90deg, var(--ant-colorPrimaryBg) 0%, var(--ant-colorBgContainer) 34%)"
                          : undefined,
                      }}
                    >
                      <td
                        style={{
                          ...memberTableCellStyle,
                          borderTop: "1px solid var(--ant-colorBorderSecondary)",
                          boxShadow: row.isEntryMember
                            ? "inset 4px 0 0 var(--ant-colorPrimary)"
                            : undefined,
                        }}
                      >
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 4,
                            ...memberTableGridCellStyle,
                          }}
                        >
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
                                  flex: "0 0 auto",
                                }}
                                text={t("team.members.entryBadge")}
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
                      </td>
                      <td style={memberTableCellStyle}>
                        <div style={memberTableGridCellStyle}>
                          <FactLine
                            rows={1}
                            text={
                              row.description ||
                              t("team.members.descriptionFallback", {
                                teamId: rosterTeamId || "--",
                              })
                            }
                          />
                        </div>
                      </td>
                      <td style={memberTableCellStyle}>
                        <div
                          style={{
                            display: "flex",
                            flexDirection: "column",
                            gap: 6,
                            ...memberTableGridCellStyle,
                          }}
                        >
                          <DetailPill
                            compact
                            style={{ ...row.lifecycleStyle, maxWidth: "100%" }}
                            text={row.lifecycleLabel}
                          />
                          <Typography.Text
                            style={{
                              display: "block",
                              fontFamily: factValueFontFamily,
                              fontSize: 12,
                              overflow: "hidden",
                              textOverflow: "ellipsis",
                              whiteSpace: "nowrap",
                            }}
                          >
                            {row.implementationKind}
                          </Typography.Text>
                        </div>
                      </td>
                      <td style={memberTableCellStyle}>
                        <CompactFactValue
                          color="var(--ant-color-text-secondary)"
                          strong={false}
                          value={row.serviceId}
                        />
                      </td>
                      <td style={memberTableCellStyle}>
                        <Space
                          size={8}
                          style={{
                            display: "flex",
                            justifyContent: "flex-end",
                            minWidth: 0,
                            width: "100%",
                          }}
                          wrap={false}
                        >
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
                              {t("team.members.clearEntry")}
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
                              {t("team.members.setEntry")}
                            </Button>
                          ) : null}
                          <Button
                            href={row.editStudioHref}
                            icon={<EditOutlined />}
                            onClick={handleNavigate(row.editStudioHref)}
                            size="small"
                          >
                            {t("team.members.editInStudio")}
                          </Button>
                          <Button
                            href={row.buildStudioHref}
                            icon={<ToolOutlined />}
                            onClick={handleNavigate(row.buildStudioHref)}
                            size="small"
                            type="primary"
                          >
                            {t("team.members.build")}
                          </Button>
                        </Space>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ) : rosterTeamId ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <AevatarInspectorEmpty
              compact
              title={t("team.members.empty.title")}
              description={t("team.members.empty.description")}
            />
            {createMemberHref ? (
              <div style={{ display: "flex", justifyContent: "center" }}>
                <Button
                  href={createMemberHref}
                  onClick={handleNavigate(createMemberHref)}
                  type="primary"
                >
                  {t("team.members.empty.createFirst")}
                </Button>
              </div>
            ) : null}
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title={t("team.members.noTeam.title")}
            description={t("team.members.noTeam.description")}
          />
        )}
      </AevatarPanel>
    </div>
  );
};

export default TeamMembersTab;
