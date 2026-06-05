import {
  CheckCircleOutlined,
  EditOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { Button, Grid, Tooltip, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  CompactFactValue,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";

type TeamRosterMemberRow = {
  readonly canInvokeAsEntry: boolean;
  readonly description: string;
  readonly implementationKind: string;
  readonly isEntryMember?: boolean;
  readonly isSelectedMember?: boolean;
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
  const intl = useIntl();
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const isStackedRoster = !screens.xl;
  const isEntryActionBusy = entryActionBusyMemberId.trim().length > 0;
  const mutedTextColor = token.colorTextSecondary;
  const subtleTextColor = token.colorTextTertiary;
  const rosterRowStyle = React.useMemo<React.CSSProperties>(
    () => ({
      alignItems: "center",
      background: token.colorBgContainer,
      border: `1px solid ${token.colorBorderSecondary}`,
      borderRadius: 8,
      display: "grid",
      gap: isStackedRoster ? 14 : 18,
      gridTemplateColumns: isStackedRoster
        ? "minmax(0, 1fr)"
        : "minmax(180px, 1.1fr) minmax(180px, 0.9fr) max-content",
      padding: isStackedRoster ? "14px 16px" : "16px 18px",
    }),
    [isStackedRoster, token.colorBgContainer, token.colorBorderSecondary],
  );
  const compactRosterRowStyle = React.useMemo<React.CSSProperties>(
    () => ({
      ...rosterRowStyle,
      background: token.colorFillQuaternary,
      borderColor: token.colorBorder,
    }),
    [rosterRowStyle, token.colorBorder, token.colorFillQuaternary],
  );
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
        title={intl.formatMessage({ id: "teams.members.title" })}
        extra={
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
            {rosterRows.length > 0
              ? intl.formatMessage(
                  { id: "teams.members.count" },
                  { count: rosterRows.length },
                )
              : intl.formatMessage({ id: "teams.members.roster" })}
          </Typography.Text>
        }
      >
        <Typography.Text
          style={{ display: "block", fontSize: 15, marginBottom: 8 }}
          type="secondary"
        >
          {intl.formatMessage({ id: "teams.members.description" })}
        </Typography.Text>
        {rosterSyncing ? (
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({ id: "teams.members.syncing.title" })}
            description={intl.formatMessage({
              id: "teams.members.syncing.description",
            })}
          />
        ) : rosterLoading ? (
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({ id: "teams.members.loading.title" })}
            description={intl.formatMessage({
              id: "teams.members.loading.description",
            })}
          />
        ) : rosterError ? (
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({ id: "teams.members.unavailable.title" })}
            description={intl.formatMessage({
              id: "teams.members.unavailable.description",
            })}
          />
        ) : rosterRows.length > 0 ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            {rosterRows.map((row) => {
              const rowStyle = row.isEntryMember || row.isSelectedMember
                ? compactRosterRowStyle
                : rosterRowStyle;
              const hasPublishedService =
                row.serviceId.trim().length > 0 && row.serviceId !== "--";
              return (
                <div
                  key={row.key}
                  style={{
                    ...rowStyle,
                    boxShadow: row.isEntryMember
                      ? `inset 4px 0 0 ${token.colorPrimary}`
                      : row.isSelectedMember
                        ? `inset 4px 0 0 ${token.colorInfo}`
                        : undefined,
                  }}
                >
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 8,
                      minWidth: 0,
                    }}
                  >
                    <div
                      style={{
                        alignItems: "center",
                        display: "flex",
                        flexWrap: "wrap",
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
                          text={intl.formatMessage({ id: "teams.members.entry" })}
                        />
                      ) : null}
                      {row.isSelectedMember ? (
                        <DetailPill
                          compact
                          style={{
                            background: token.colorInfoBg,
                            border: `1px solid ${token.colorInfoBorder}`,
                            color: token.colorInfo,
                          }}
                          text={intl.formatMessage({ id: "teams.members.selected" })}
                        />
                      ) : null}
                    </div>
                    <EllipsisText
                      monospace
                      style={{
                        color: subtleTextColor,
                        fontSize: 12,
                      }}
                    >
                      {row.memberId}
                    </EllipsisText>
                    {row.description ? (
                      <Typography.Text
                        style={{
                          color: mutedTextColor,
                          display: "-webkit-box",
                          fontSize: 13,
                          overflow: "hidden",
                          overflowWrap: "anywhere",
                          WebkitBoxOrient: "vertical",
                          WebkitLineClamp: 2,
                        }}
                      >
                        {row.description}
                      </Typography.Text>
                    ) : null}
                  </div>
                  <div
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 8,
                      minWidth: 0,
                    }}
                  >
                    <div
                      style={{
                        alignItems: "center",
                        display: "flex",
                        flexWrap: "wrap",
                        gap: 8,
                      }}
                    >
                      <DetailPill compact style={row.lifecycleStyle} text={row.lifecycleLabel} />
                      <Typography.Text style={{ color: mutedTextColor, fontSize: 13 }}>
                        {intl.formatMessage(
                          { id: "teams.members.kind" },
                          { kind: row.implementationKind },
                        )}
                      </Typography.Text>
                    </div>
                    <div
                      style={{
                        alignItems: "center",
                        color: mutedTextColor,
                        display: "flex",
                        fontSize: 13,
                        gap: 6,
                        minWidth: 0,
                      }}
                    >
                      <Typography.Text style={{ color: mutedTextColor, fontSize: 13 }}>
                        {intl.formatMessage({ id: "teams.members.service" })}
                      </Typography.Text>
                      {hasPublishedService ? (
                        <CompactFactValue
                          color={mutedTextColor}
                          head={12}
                          strong={false}
                          tail={4}
                          value={row.serviceId}
                        />
                      ) : (
                        <Typography.Text style={{ color: mutedTextColor, fontSize: 13 }}>
                          {intl.formatMessage({
                            id: "teams.members.service.notPublished",
                          })}
                        </Typography.Text>
                      )}
                    </div>
                  </div>
                  <div
                    style={{
                      alignItems: "center",
                      display: "flex",
                      flexWrap: "wrap",
                      gap: 8,
                      justifyContent: isStackedRoster ? "flex-start" : "flex-end",
                      justifySelf: isStackedRoster ? "stretch" : "end",
                      minWidth: 0,
                    }}
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
                        {intl.formatMessage({ id: "teams.members.actions.clearEntry" })}
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
                        {intl.formatMessage({ id: "teams.members.actions.setEntry" })}
                      </Button>
                    ) : null}
                    <Button
                      href={row.buildStudioHref}
                      icon={<ToolOutlined />}
                      onClick={handleNavigate(row.buildStudioHref)}
                      size="small"
                      type="primary"
                    >
                      {intl.formatMessage({ id: "teams.members.actions.build" })}
                    </Button>
                    <Tooltip
                      title={intl.formatMessage({
                        id: "teams.members.actions.editInStudio",
                      })}
                    >
                      <Button
                        aria-label={intl.formatMessage({
                          id: "teams.members.actions.editInStudio",
                        })}
                        href={row.editStudioHref}
                        icon={<EditOutlined />}
                        onClick={handleNavigate(row.editStudioHref)}
                        size="small"
                        style={{ width: 32 }}
                      />
                    </Tooltip>
                  </div>
                </div>
              );
            })}
          </div>
        ) : rosterTeamId ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <AevatarInspectorEmpty
              compact
              title={intl.formatMessage({ id: "teams.members.empty.title" })}
              description={intl.formatMessage({
                id: "teams.members.empty.description",
              })}
            />
            {createMemberHref ? (
              <div style={{ display: "flex", justifyContent: "center" }}>
                <Button
                  href={createMemberHref}
                  onClick={handleNavigate(createMemberHref)}
                  type="primary"
                >
                  {intl.formatMessage({ id: "teams.members.actions.createFirst" })}
                </Button>
              </div>
            ) : null}
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({ id: "teams.members.noSelection.title" })}
            description={intl.formatMessage({
              id: "teams.members.noSelection.description",
            })}
          />
        )}
      </AevatarPanel>
    </div>
  );
};

export default TeamMembersTab;
