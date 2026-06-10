import {
  CheckCircleOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  ToolOutlined,
} from "@ant-design/icons";
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
  CompactFactValue,
  factValueFontFamily,
} from "../components/TeamDetailPrimitives";
import { t } from "@/shared/i18n/messages";

type TeamRosterMemberRow = {
  readonly canInvokeAsEntry: boolean;
  readonly canInvokeMember: boolean;
  readonly description: string;
  readonly implementationKind: string;
  readonly isServiceBound: boolean;
  readonly isEntryMember?: boolean;
  readonly isSelectedMember?: boolean;
  readonly key: string;
  readonly lifecycleLabel: string;
  readonly lifecycleStyle: React.CSSProperties;
  readonly buildStudioHref: string;
  readonly editStudioHref: string;
  readonly invokeHref: string;
  readonly memberId: string;
  readonly name: string;
  readonly serviceId: string;
  readonly studioHref: string;
  readonly workflowSupported: boolean;
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

const tableGridTemplateColumns =
  "minmax(260px, 1.7fr) minmax(150px, 0.75fr) minmax(180px, 0.8fr) minmax(330px, max-content)";

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
          <Space size={10} wrap>
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {rosterRows.length > 0
                ? intl.formatMessage(
                    { id: "teams.members.count" },
                    { count: rosterRows.length },
                  )
                : intl.formatMessage({ id: "teams.members.roster" })}
            </Typography.Text>
            {createMemberHref ? (
              <Button
                href={createMemberHref}
                icon={<PlusOutlined />}
                onClick={handleNavigate(createMemberHref)}
                size="small"
                style={{
                  borderRadius: 999,
                  boxShadow: token.boxShadowTertiary,
                  fontWeight: 600,
                  height: 30,
                  paddingInline: 14,
                }}
                type="primary"
              >
                {intl.formatMessage({
                  id: "teams.members.actions.createWorkflowMember",
                })}
              </Button>
            ) : null}
          </Space>
        }
      >
        <Typography.Text style={{ maxWidth: 720 }} type="secondary">
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
          <div
            style={{
              border: "1px solid var(--ant-colorBorderSecondary)",
              borderRadius: 18,
              overflow: "hidden",
            }}
          >
            <div style={{ overflowX: "auto" }}>
              <div style={{ minWidth: 1060 }}>
                <div
                  style={{
                    background: "var(--ant-colorBgContainerDisabled)",
                    borderBottom: "1px solid var(--ant-colorBorderSecondary)",
                    color: "var(--ant-colorTextSecondary)",
                    display: "grid",
                    fontSize: 12,
                    fontWeight: 600,
                    gap: 16,
                    gridTemplateColumns: tableGridTemplateColumns,
                    padding: "12px 16px",
                  }}
                >
                  <span>{intl.formatMessage({ id: "teams.members.columns.member" })}</span>
                  <span>
                    {intl.formatMessage({ id: "teams.members.columns.implementation" })}
                  </span>
                  <span>{intl.formatMessage({ id: "teams.members.columns.service" })}</span>
                  <span style={{ justifySelf: "flex-end" }}>
                    {intl.formatMessage({ id: "teams.members.columns.actions" })}
                  </span>
                </div>
                {rosterRows.map((row, index) => {
                  const invokeDisabledReason = row.workflowSupported
                    ? intl.formatMessage({
                        id: "teams.members.actions.invokeRequiresBinding",
                      })
                    : intl.formatMessage({
                        id: "teams.members.actions.workflowOnlyTitle",
                      });
                  const rowBusy = entryActionBusyMemberId === row.memberId;

                  return (
                    <div
                      key={row.key}
                      style={{
                        alignItems: "center",
                        background: row.isEntryMember
                          ? "linear-gradient(90deg, var(--ant-colorPrimaryBg) 0%, var(--ant-colorBgContainer) 30%)"
                          : row.isSelectedMember
                            ? "var(--ant-colorFillQuaternary)"
                            : token.colorBgContainer,
                        borderTop:
                          index === 0 ? "none" : "1px solid var(--ant-colorBorderSecondary)",
                        boxShadow: row.isEntryMember
                          ? "inset 4px 0 0 var(--ant-colorSuccess)"
                          : row.isSelectedMember
                            ? "inset 4px 0 0 var(--ant-colorInfo)"
                            : undefined,
                        display: "grid",
                        gap: 16,
                        gridTemplateColumns: tableGridTemplateColumns,
                        minHeight: 86,
                        padding: "14px 16px",
                      }}
                    >
                      <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
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
                        <Space size={8} style={{ minWidth: 0 }} wrap>
                          <EllipsisText
                            monospace
                            style={{
                              color: token.colorTextSecondary,
                              fontSize: 12,
                              maxWidth: 180,
                            }}
                            type="secondary"
                          >
                            {row.memberId}
                          </EllipsisText>
                          <Typography.Text style={{ color: token.colorTextQuaternary }}>
                            ·
                          </Typography.Text>
                          <div style={{ maxWidth: 300, minWidth: 0 }}>
                            <FactLine
                              monospace={false}
                              rows={1}
                              secondary
                              text={
                                row.description ||
                                intl.formatMessage(
                                  { id: "teams.members.fallback.team" },
                                  { teamId: rosterTeamId || "--" },
                                )
                              }
                            />
                          </div>
                        </Space>
                      </div>
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          flexDirection: "column",
                          gap: 6,
                          minWidth: 0,
                        }}
                      >
                        <Typography.Text strong>{row.implementationKind}</Typography.Text>
                        <DetailPill
                          compact
                          style={{
                            ...row.lifecycleStyle,
                            maxWidth: "100%",
                            padding: "6px 9px",
                            width: "fit-content",
                          }}
                          text={row.lifecycleLabel}
                        />
                      </div>
                      <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                        <CompactFactValue
                          color={row.isServiceBound ? token.colorText : token.colorTextTertiary}
                          head={4}
                          maxWidth={160}
                          strong={row.isServiceBound}
                          tail={4}
                          value={row.serviceId}
                        />
                        <Typography.Text style={{ fontSize: 12 }} type="secondary">
                          {row.isServiceBound
                            ? intl.formatMessage({ id: "teams.members.service.bound" })
                            : intl.formatMessage({ id: "teams.members.service.notBound" })}
                        </Typography.Text>
                      </div>
                      <Space
                        wrap
                        size={8}
                        style={{ justifyContent: "flex-end", width: "100%" }}
                      >
                        <Button
                          href={row.canInvokeMember ? row.invokeHref : undefined}
                          disabled={!row.canInvokeMember}
                          icon={<PlayCircleOutlined />}
                          onClick={
                            row.canInvokeMember
                              ? handleNavigate(row.invokeHref)
                              : undefined
                          }
                          size="small"
                          style={
                            row.canInvokeMember
                              ? { color: token.colorSuccess, fontWeight: 600 }
                              : undefined
                          }
                          title={row.canInvokeMember ? undefined : invokeDisabledReason}
                          type="text"
                        >
                          {intl.formatMessage({ id: "teams.members.actions.invokeWorkflow" })}
                        </Button>
                        <Button
                          href={row.workflowSupported ? row.studioHref : undefined}
                          disabled={!row.workflowSupported}
                          icon={<ToolOutlined />}
                          onClick={
                            row.workflowSupported
                              ? handleNavigate(row.studioHref)
                              : undefined
                          }
                          size="small"
                          title={
                            row.workflowSupported
                              ? undefined
                              : intl.formatMessage({
                                  id: "teams.members.actions.workflowOnlyTitle",
                                })
                          }
                          type="default"
                        >
                          {intl.formatMessage({ id: "teams.members.actions.workflowStudio" })}
                        </Button>
                        {row.isEntryMember ? (
                          <Button
                            icon={<CheckCircleOutlined />}
                            disabled={
                              rowBusy ||
                              (isEntryActionBusy && entryActionBusyMemberId !== row.memberId)
                            }
                            loading={entryActionBusyMemberId === row.memberId}
                            onClick={onClearEntry}
                            size="small"
                            type="text"
                          >
                            {intl.formatMessage({ id: "teams.members.actions.clearEntry" })}
                          </Button>
                        ) : row.canInvokeAsEntry && onSetEntry ? (
                          <Button
                            disabled={
                              rowBusy ||
                              (isEntryActionBusy &&
                                entryActionBusyMemberId !== row.memberId)
                            }
                            loading={entryActionBusyMemberId === row.memberId}
                            onClick={() => onSetEntry(row.memberId)}
                            size="small"
                            type="text"
                          >
                            {intl.formatMessage({ id: "teams.members.actions.setEntry" })}
                          </Button>
                        ) : null}
                      </Space>
                    </div>
                  );
                })}
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
              <div
                style={{
                  display: "flex",
                  flexWrap: "wrap",
                  gap: 8,
                  justifyContent: "center",
                }}
              >
                <Button
                  href={createMemberHref}
                  onClick={handleNavigate(createMemberHref)}
                  type="primary"
                >
                  {intl.formatMessage({
                    id: "teams.members.actions.createFirstWorkflow",
                  })}
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
