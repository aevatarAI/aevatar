import { CheckCircleOutlined, EditOutlined, ToolOutlined } from "@ant-design/icons";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";

type TeamRosterMemberRow = {
  readonly canInvokeAsEntry: boolean;
  readonly description: string;
  readonly hasPublishedService: boolean;
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
  readonly rosterRetrying?: boolean;
  readonly rosterSyncing?: boolean;
  readonly rosterTeamId?: string;
  readonly createMemberHref?: string;
  readonly entryActionBusyMemberId?: string;
  readonly entryClearBusy?: boolean;
  readonly onClearEntry?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onRetryRoster?: () => void;
  readonly onSetEntry?: (memberId: string) => void;
};

const memberGridTemplateColumns =
  "minmax(190px, 1.15fr) minmax(260px, 1.6fr) minmax(140px, 0.75fr) minmax(170px, 0.85fr) minmax(220px, max-content)";

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
  entryClearBusy = false,
  onClearEntry,
  onNavigate,
  onRetryRoster,
  onSetEntry,
  rosterError = false,
  rosterLoading = false,
  rosterRetrying = false,
  rosterRows = [],
  rosterSyncing = false,
  rosterTeamId = "",
}) => {
  const intl = useIntl();
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
        <Typography.Text type="secondary">
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
          <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
            <AevatarInspectorEmpty
              compact
              title={intl.formatMessage({ id: "teams.members.unavailable.title" })}
              description={intl.formatMessage({
                id: "teams.members.unavailable.description",
              })}
            />
            {onRetryRoster ? (
              <div style={{ display: "flex", justifyContent: "center" }}>
                <Button loading={rosterRetrying} onClick={onRetryRoster}>
                  {intl.formatMessage({ id: "teams.members.actions.retryRoster" })}
                </Button>
              </div>
            ) : null}
          </div>
        ) : rosterRows.length > 0 ? (
          <div
            style={{
              border: "1px solid var(--ant-colorBorderSecondary)",
              borderRadius: 18,
              overflow: "hidden",
            }}
          >
            <div style={{ overflowX: "auto" }}>
              <div style={{ minWidth: 920 }}>
                <div
                  style={{
                    background: "var(--ant-colorBgContainerDisabled)",
                    borderBottom: "1px solid var(--ant-colorBorderSecondary)",
                    color: "var(--ant-colorTextSecondary)",
                    display: "grid",
                    fontSize: 12,
                    fontWeight: 600,
                    gap: 16,
                    gridTemplateColumns: memberGridTemplateColumns,
                    padding: "12px 16px",
                  }}
                >
                  <span>{intl.formatMessage({ id: "teams.members.columns.member" })}</span>
                  <span>{intl.formatMessage({ id: "teams.members.columns.role" })}</span>
                  <span>
                    {intl.formatMessage({ id: "teams.members.columns.implementation" })}
                  </span>
                  <span>{intl.formatMessage({ id: "teams.members.columns.service" })}</span>
                  <span>{intl.formatMessage({ id: "teams.members.columns.actions" })}</span>
                </div>
                {rosterRows.map((row, index) => (
                  <div
                    aria-label={intl.formatMessage(
                      { id: "teams.members.row.label" },
                      { memberId: row.memberId, name: row.name },
                    )}
                    data-testid={`team-member-row-${row.memberId}`}
                    key={row.key}
                    role="group"
                    style={{
                      alignItems: "start",
                      background: row.isEntryMember
                        ? "linear-gradient(90deg, var(--ant-colorPrimaryBg) 0%, var(--ant-colorBgContainer) 34%)"
                        : row.isSelectedMember
                          ? "var(--ant-colorFillQuaternary)"
                        : undefined,
                      borderTop:
                        index === 0 ? "none" : "1px solid var(--ant-colorBorderSecondary)",
                      boxShadow: row.isEntryMember
                        ? "inset 4px 0 0 var(--ant-colorPrimary)"
                        : row.isSelectedMember
                          ? "inset 4px 0 0 var(--ant-colorInfo)"
                        : undefined,
                      display: "grid",
                      gap: 16,
                      gridTemplateColumns: memberGridTemplateColumns,
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
                          fontSize: 12,
                        }}
                        type="secondary"
                      >
                        {row.memberId}
                      </EllipsisText>
                    </div>
                    <div style={{ minWidth: 0 }}>
                      <FactLine
                        monospace={false}
                        rows={2}
                        secondary
                        text={
                          row.description ||
                          intl.formatMessage({ id: "teams.members.fallback.purpose" })
                        }
                      />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      <DetailPill compact style={row.lifecycleStyle} text={row.lifecycleLabel} />
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {intl.formatMessage(
                          { id: "teams.members.kind" },
                          { kind: row.implementationKind },
                        )}
                      </Typography.Text>
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                      {row.hasPublishedService ? (
                        <>
                          <EllipsisText
                            monospace
                            style={{
                              fontSize: 12,
                            }}
                            type="secondary"
                          >
                            {row.serviceId}
                          </EllipsisText>
                          {row.canInvokeAsEntry ? (
                            <Typography.Text
                              style={{ color: token.colorSuccess, fontSize: 12 }}
                            >
                              {intl.formatMessage({
                                id: "teams.members.service.runnable",
                              })}
                            </Typography.Text>
                          ) : null}
                        </>
                      ) : (
                        <>
                          <Typography.Text type="secondary">
                            {intl.formatMessage({
                              id: "teams.members.service.notPublished",
                            })}
                          </Typography.Text>
                          <Typography.Text style={{ fontSize: 12 }} type="secondary">
                            {intl.formatMessage({
                              id: "teams.members.service.buildFirst",
                            })}
                          </Typography.Text>
                        </>
                      )}
                    </div>
                    <Space
                      wrap
                      size={6}
                      style={{ justifyContent: "flex-end", minWidth: 0 }}
                    >
                      {row.isEntryMember ? (
                        <Button
                          aria-label={intl.formatMessage(
                            { id: "teams.members.actions.clearEntryFor" },
                            { name: row.name },
                          )}
                          icon={<CheckCircleOutlined />}
                          disabled={isEntryActionBusy && !entryClearBusy}
                          loading={entryClearBusy}
                          onClick={onClearEntry}
                          size="small"
                        >
                          {intl.formatMessage({ id: "teams.members.actions.clearEntry" })}
                        </Button>
                      ) : row.canInvokeAsEntry && onSetEntry ? (
                        <Button
                          aria-label={intl.formatMessage(
                            { id: "teams.members.actions.setEntryFor" },
                            { name: row.name },
                          )}
                          disabled={
                            isEntryActionBusy && entryActionBusyMemberId !== row.memberId
                          }
                          loading={entryActionBusyMemberId === row.memberId}
                          onClick={() => onSetEntry(row.memberId)}
                          size="small"
                        >
                          {intl.formatMessage({ id: "teams.members.actions.setEntry" })}
                        </Button>
                      ) : !row.canInvokeAsEntry ? (
                        <Typography.Text style={{ fontSize: 12 }} type="secondary">
                          {intl.formatMessage({
                            id: "teams.members.service.buildFirst",
                          })}
                        </Typography.Text>
                      ) : null}
                      <Button
                        aria-label={intl.formatMessage(
                          { id: "teams.members.actions.buildFor" },
                          { name: row.name },
                        )}
                        href={row.buildStudioHref}
                        icon={<ToolOutlined />}
                        onClick={handleNavigate(row.buildStudioHref)}
                        size="small"
                        type="primary"
                      >
                        {intl.formatMessage({ id: "teams.members.actions.build" })}
                      </Button>
                      <Button
                        aria-label={intl.formatMessage(
                          { id: "teams.members.actions.editInStudioFor" },
                          { name: row.name },
                        )}
                        href={row.editStudioHref}
                        icon={<EditOutlined />}
                        onClick={handleNavigate(row.editStudioHref)}
                        size="small"
                      >
                        {intl.formatMessage({ id: "teams.members.actions.editInStudio" })}
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
