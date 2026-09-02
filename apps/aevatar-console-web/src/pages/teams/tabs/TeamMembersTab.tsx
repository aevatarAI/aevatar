import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  DeleteOutlined,
  HistoryOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  StopOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { Button, Skeleton, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import {
  AevatarInspectorEmpty,
  AevatarPanel,
} from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";
import "./TeamMembersTab.css";

type TeamRosterMemberRow = {
  readonly automationDisabledReason: string;
  readonly automationsHref: string;
  readonly canAutomateMember: boolean;
  readonly canInvokeAsEntry: boolean;
  readonly canInvokeMember: boolean;
  readonly canOpenPublishedRuns: boolean;
  readonly canSetAsEntry: boolean;
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
  readonly publishedRunsDisabledReason: string;
  readonly publishedRunsHref: string;
  readonly serviceId: string;
  readonly studioHref: string;
  readonly workflowSupported: boolean;
};

export type TeamMembersDeleteTarget = Pick<
  TeamRosterMemberRow,
  "isEntryMember" | "memberId" | "name"
>;

type TeamMembersTabProps = {
  readonly rosterError?: boolean;
  readonly rosterLoading?: boolean;
  readonly rosterRows?: readonly TeamRosterMemberRow[];
  readonly rosterSyncing?: boolean;
  readonly createMemberHref?: string;
  readonly deletingMemberId?: string;
  readonly entryActionBusyMemberId?: string;
  readonly onClearEntry?: () => void;
  readonly onDeleteMember?: (target: TeamMembersDeleteTarget) => void;
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

const tableGridMetrics = {
  actionsMinWidth: 252,
  columnGap: 20,
  implementationMinWidth: 140,
  memberMinWidth: 260,
  rowPaddingInline: 18,
  serviceMinWidth: 180,
} as const;

const tableGridTemplateColumns =
  `minmax(${tableGridMetrics.memberMinWidth}px, 1.4fr) ` +
  `minmax(${tableGridMetrics.implementationMinWidth}px, 0.45fr) ` +
  `minmax(${tableGridMetrics.serviceMinWidth}px, 0.7fr) ` +
  `${tableGridMetrics.actionsMinWidth}px`;

const tableShellStyle: React.CSSProperties = {
  borderRadius: 8,
  overflow: "hidden",
};

const tableScrollStyle: React.CSSProperties = {
  overflowX: "auto",
};

const tableInnerStyle: React.CSSProperties = {
  minWidth: 0,
  width: "100%",
  "--team-members-grid-template": tableGridTemplateColumns,
} as React.CSSProperties;

const tableHeaderStyle: React.CSSProperties = {
  alignItems: "center",
  display: "grid",
  fontSize: 12,
  fontWeight: 700,
  gap: tableGridMetrics.columnGap,
  gridTemplateColumns: tableGridTemplateColumns,
  padding: `10px ${tableGridMetrics.rowPaddingInline}px`,
};

const tableHeaderActionStyle: React.CSSProperties = {
  justifySelf: "end",
  textAlign: "right",
};

const rosterRowBaseStyle: React.CSSProperties = {
  alignItems: "center",
  display: "grid",
  gap: tableGridMetrics.columnGap,
  gridTemplateColumns: tableGridTemplateColumns,
  minHeight: 82,
  padding: `14px ${tableGridMetrics.rowPaddingInline}px`,
};

const memberNameRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  gap: 8,
  minWidth: 0,
};

const memberCellStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 6,
  minWidth: 0,
};

const implementationCellStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  flexDirection: "column",
  gap: 6,
  minWidth: 0,
};

const serviceCellStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
  minWidth: 0,
};

const actionCellStyle: React.CSSProperties = {
  alignItems: "flex-start",
  display: "flex",
  justifyContent: "flex-end",
  justifyItems: "end",
  justifySelf: "stretch",
  minWidth: 0,
  width: "100%",
};

const primaryActionsStyle: React.CSSProperties = {
  alignItems: "center",
  borderRadius: 12,
  display: "flex",
  flex: "0 0 auto",
  gap: 4,
  justifyContent: "flex-end",
  minWidth: 0,
  padding: 4,
  width: "max-content",
};

const memberActionButtonBaseStyle: React.CSSProperties = {
  border: "none",
  borderRadius: 8,
  boxShadow: "none",
  height: 32,
  lineHeight: 1,
  minWidth: 32,
  paddingInline: 0,
  width: 32,
};

const memberRosterSkeletonRowKeys = ["primary", "secondary", "tertiary"] as const;

const SkeletonLine: React.FC<{
  readonly height?: number;
  readonly width: number | string;
}> = ({ height = 16, width }) => (
  <Skeleton.Input
    active
    size="small"
    style={{
      borderRadius: 999,
      height,
      maxWidth: "100%",
      width,
    }}
  />
);

const panelHeaderStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 12,
  justifyContent: "space-between",
  marginBottom: 12,
  minWidth: 0,
};

const panelTitleGroupStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 10,
  minWidth: 0,
};

const panelTitleStyle: React.CSSProperties = {
  fontSize: 16,
  fontWeight: 800,
  lineHeight: "24px",
  margin: 0,
};

const panelCreateActionStyle: React.CSSProperties = {
  flex: "0 0 auto",
  minWidth: 0,
};

const EllipsisText: React.FC<{
  readonly children: string;
  readonly strong?: boolean;
  readonly style?: React.CSSProperties;
  readonly type?: "secondary";
}> = ({ children, strong = false, style, type }) => (
  <AevatarTooltip placement="topLeft" title={children}>
    <Typography.Text
      strong={strong}
      style={{
        ...ellipsisTextStyle,
        ...style,
      }}
      type={type}
    >
      {children}
    </Typography.Text>
  </AevatarTooltip>
);

const TeamMembersTab: React.FC<TeamMembersTabProps> = ({
  createMemberHref = "",
  deletingMemberId = "",
  entryActionBusyMemberId = "",
  onClearEntry,
  onDeleteMember,
  onNavigate,
  onSetEntry,
  rosterError = false,
  rosterLoading = false,
  rosterRows = [],
  rosterSyncing = false,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const isEntryActionBusy = entryActionBusyMemberId.trim().length > 0;
  const isDeleteActionBusy = deletingMemberId.trim().length > 0;
  const tableFrameStyle: React.CSSProperties = {
    ...tableShellStyle,
    border: `1px solid ${token.colorBorderSecondary}`,
  };
  const tableHeadStyle: React.CSSProperties = {
    ...tableHeaderStyle,
    background: token.colorFillQuaternary,
    borderBottom: `1px solid ${token.colorBorderSecondary}`,
    color: token.colorTextSecondary,
  };
  const renderTableHeader = () => (
    <div className="team-members-table-header" style={tableHeadStyle}>
      <span>{intl.formatMessage({ id: "teams.members.columns.member" })}</span>
      <span>
        {intl.formatMessage({ id: "teams.members.columns.implementation" })}
      </span>
      <span>{intl.formatMessage({ id: "teams.members.columns.service" })}</span>
      <span style={tableHeaderActionStyle}>
        {intl.formatMessage({ id: "teams.members.columns.actions" })}
      </span>
    </div>
  );
  const renderTableSkeleton = (status: "loading" | "syncing") => {
    const statusTitle = intl.formatMessage({
      id:
        status === "syncing"
          ? "teams.members.syncing.title"
          : "teams.members.loading.title",
    });
    const statusDescription = intl.formatMessage({
      id:
        status === "syncing"
          ? "teams.members.syncing.description"
          : "teams.members.loading.description",
    });

    return (
      <div
        aria-busy="true"
        aria-label={statusTitle}
        data-testid="team-members-skeleton"
        role="status"
        style={{ display: "flex", flexDirection: "column", gap: 12 }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
          <Typography.Text strong>{statusTitle}</Typography.Text>
          <Typography.Text style={{ fontSize: 13 }} type="secondary">
            {statusDescription}
          </Typography.Text>
        </div>
        <div
          className="team-members-table-container"
          data-responsive-layout="container"
          data-testid="team-members-table"
          style={tableFrameStyle}
        >
          <div style={tableScrollStyle}>
            <div style={tableInnerStyle}>
              {renderTableHeader()}
              {memberRosterSkeletonRowKeys.map((key, index) => (
                <div
                  className="team-members-table-row"
                  data-testid="team-members-skeleton-row"
                  key={key}
                  style={{
                    ...rosterRowBaseStyle,
                    background: token.colorBgContainer,
                    borderTop:
                      index === 0
                        ? "none"
                        : `1px solid ${token.colorBorderSecondary}`,
                  }}
                >
                  <div style={memberCellStyle}>
                    <SkeletonLine height={22} width="64%" />
                    <SkeletonLine width="88%" />
                  </div>
                  <div style={implementationCellStyle}>
                    <SkeletonLine width="58%" />
                    <SkeletonLine height={24} width={92} />
                  </div>
                  <div style={serviceCellStyle}>
                    <SkeletonLine width="72%" />
                    <SkeletonLine width="86%" />
                  </div>
                  <div className="team-members-table-actions" style={actionCellStyle}>
                    <div
                      className="team-members-table-primary-actions"
                      style={primaryActionsStyle}
                    >
                      <Skeleton.Button
                        active
                        className="team-members-table-invoke-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                      <Skeleton.Button
                        active
                        className="team-members-table-automate-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                      <Skeleton.Button
                        active
                        className="team-members-table-published-runs-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                      <Skeleton.Button
                        active
                        className="team-members-table-studio-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                      <Skeleton.Button
                        active
                        className="team-members-table-entry-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                      <Skeleton.Button
                        active
                        className="team-members-table-delete-action"
                        size="small"
                        style={memberActionButtonBaseStyle}
                      />
                    </div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    );
  };
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
      <AevatarPanel>
        <div style={panelHeaderStyle}>
          <div style={panelTitleGroupStyle}>
            <Typography.Title level={3} style={panelTitleStyle}>
              {intl.formatMessage({ id: "teams.members.title" })}
            </Typography.Title>
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {rosterRows.length > 0
                ? intl.formatMessage(
                    { id: "teams.members.count" },
                    { count: rosterRows.length },
                  )
                : intl.formatMessage({ id: "teams.members.roster" })}
            </Typography.Text>
          </div>
          {createMemberHref ? (
            <Button
              href={createMemberHref}
              icon={<PlusOutlined />}
              onClick={handleNavigate(createMemberHref)}
              size="small"
              style={{
                ...panelCreateActionStyle,
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
        </div>
        <Typography.Text style={{ maxWidth: 720 }} type="secondary">
          {intl.formatMessage({ id: "teams.members.description" })}
        </Typography.Text>
        {rosterSyncing ? (
          renderTableSkeleton("syncing")
        ) : rosterLoading ? (
          renderTableSkeleton("loading")
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
            className="team-members-table-container"
            data-responsive-layout="container"
            data-testid="team-members-table"
            style={tableFrameStyle}
          >
            <div style={tableScrollStyle}>
              <div style={tableInnerStyle}>
                {renderTableHeader()}
                {rosterRows.map((row, index) => {
                  const invokeDisabledReason = row.workflowSupported
                    ? intl.formatMessage({
                        id: "teams.members.actions.invokeRequiresBinding",
                      })
                    : intl.formatMessage({
                        id: "teams.members.actions.workflowOnlyTitle",
                      });
                  const rowBusy = entryActionBusyMemberId === row.memberId;
                  const rowDeleting = deletingMemberId === row.memberId;
                  const invokeActionLabel = intl.formatMessage({
                    id: "teams.members.actions.invokeWorkflow",
                  });
                  const automateActionLabel = intl.formatMessage({
                    defaultMessage: "Automate",
                    id: "teams.members.actions.automate",
                  });
                  const publishedRunsActionLabel = intl.formatMessage({
                    id: "teams.members.actions.publishedRuns",
                  });
                  const studioActionLabel = intl.formatMessage({
                    id: "teams.members.actions.workflowStudio",
                  });
                  const entryActionLabel = row.isEntryMember
                    ? intl.formatMessage({ id: "teams.members.actions.clearEntry" })
                    : intl.formatMessage({ id: "teams.members.actions.setEntry" });
                  const entryActionIcon = row.isEntryMember ? (
                    <StopOutlined />
                  ) : (
                    <CheckCircleOutlined />
                  );
                  const deleteActionLabel = intl.formatMessage({
                    defaultMessage: "Delete member",
                    id: "teams.members.actions.delete",
                  });
                  const buildMemberActionButtonStyle = (
                    tone: "danger" | "default" | "primary" | "success" = "default",
                  ): React.CSSProperties => {
                    if (tone === "danger") {
                      return {
                        ...memberActionButtonBaseStyle,
                        background: token.colorErrorBg,
                        color: token.colorError,
                      };
                    }

                    if (tone === "success") {
                      return {
                        ...memberActionButtonBaseStyle,
                        background: token.colorSuccessBg,
                        color: token.colorSuccess,
                      };
                    }

                    if (tone === "primary") {
                      return {
                        ...memberActionButtonBaseStyle,
                        background: token.colorPrimaryBg,
                        color: token.colorPrimary,
                      };
                    }

                    return {
                      ...memberActionButtonBaseStyle,
                      background: token.colorBgContainer,
                      color: token.colorTextSecondary,
                    };
                  };

                  return (
                    <div
                      className="team-members-table-row"
                      key={row.key}
                      style={{
                        ...rosterRowBaseStyle,
                        alignItems: "center",
                        background: row.isEntryMember
                          ? `linear-gradient(90deg, ${token.colorPrimaryBg} 0%, ${token.colorBgContainer} 30%)`
                          : row.isSelectedMember
                            ? token.colorFillQuaternary
                            : token.colorBgContainer,
                        borderTop:
                          index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                        boxShadow: row.isEntryMember
                          ? `inset 4px 0 0 ${token.colorSuccess}`
                          : row.isSelectedMember
                            ? `inset 4px 0 0 ${token.colorInfo}`
                            : undefined,
                      }}
                    >
                      <div style={memberCellStyle}>
                        <div style={memberNameRowStyle}>
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
                        {row.description ? (
                          <div style={{ maxWidth: 360, minWidth: 0 }}>
                            <FactLine
                              monospace={false}
                              rows={2}
                              secondary
                              text={row.description}
                            />
                          </div>
                        ) : null}
                      </div>
                      <div style={implementationCellStyle}>
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
                      <div style={serviceCellStyle}>
                        <Typography.Text
                          strong={row.isServiceBound}
                          type={row.isServiceBound ? undefined : "secondary"}
                        >
                          {row.isServiceBound
                            ? intl.formatMessage({ id: "teams.members.service.bound" })
                            : intl.formatMessage({ id: "teams.members.service.notBound" })}
                        </Typography.Text>
                        <Typography.Text style={{ fontSize: 12 }} type="secondary">
                          {row.isServiceBound
                            ? intl.formatMessage({ id: "teams.members.service.ready" })
                            : intl.formatMessage({ id: "teams.members.service.needsBinding" })}
                        </Typography.Text>
                      </div>
                      <div className="team-members-table-actions" style={actionCellStyle}>
                        <div
                          className="team-members-table-primary-actions"
                          style={{
                            ...primaryActionsStyle,
                            background: token.colorFillQuaternary,
                            border: `1px solid ${token.colorBorderSecondary}`,
                          }}
                        >
                          <AevatarTooltip
                            title={
                              row.canInvokeMember
                                ? invokeActionLabel
                                : invokeDisabledReason
                            }
                          >
                            <Button
                              aria-label={invokeActionLabel}
                              className="team-members-table-action-button team-members-table-invoke-action"
                              href={row.canInvokeMember ? row.invokeHref : undefined}
                              disabled={!row.canInvokeMember}
                              icon={<PlayCircleOutlined />}
                              onClick={
                                row.canInvokeMember
                                  ? handleNavigate(row.invokeHref)
                                  : undefined
                              }
                              size="small"
                              style={buildMemberActionButtonStyle(
                                row.canInvokeMember ? "success" : "default",
                              )}
                              type="default"
                            />
                          </AevatarTooltip>
                          <AevatarTooltip
                            title={
                              row.canOpenPublishedRuns
                                ? publishedRunsActionLabel
                                : row.publishedRunsDisabledReason
                            }
                          >
                            <Button
                              aria-label={publishedRunsActionLabel}
                              className="team-members-table-action-button team-members-table-published-runs-action"
                              disabled={!row.canOpenPublishedRuns}
                              href={
                                row.canOpenPublishedRuns
                                  ? row.publishedRunsHref
                                  : undefined
                              }
                              icon={<HistoryOutlined />}
                              onClick={
                                row.canOpenPublishedRuns
                                  ? handleNavigate(row.publishedRunsHref)
                                  : undefined
                              }
                              size="small"
                              style={buildMemberActionButtonStyle()}
                              type="default"
                            />
                          </AevatarTooltip>
                          <AevatarTooltip
                            title={
                              row.canAutomateMember
                                ? automateActionLabel
                                : row.automationDisabledReason
                            }
                          >
                            <Button
                              aria-label={automateActionLabel}
                              className="team-members-table-action-button team-members-table-automate-action"
                              href={
                                row.canAutomateMember
                                  ? row.automationsHref
                                  : undefined
                              }
                              disabled={!row.canAutomateMember}
                              icon={<ClockCircleOutlined />}
                              onClick={
                                row.canAutomateMember
                                  ? handleNavigate(row.automationsHref)
                                  : undefined
                              }
                              size="small"
                              style={buildMemberActionButtonStyle()}
                              type="default"
                            />
                          </AevatarTooltip>
                          <AevatarTooltip
                            title={
                              row.workflowSupported
                                ? studioActionLabel
                                : intl.formatMessage({
                                    id: "teams.members.actions.workflowOnlyTitle",
                                  })
                            }
                          >
                            <Button
                              aria-label={studioActionLabel}
                              className="team-members-table-action-button team-members-table-studio-action"
                              href={row.workflowSupported ? row.studioHref : undefined}
                              disabled={!row.workflowSupported}
                              icon={<ToolOutlined />}
                              onClick={
                                row.workflowSupported
                                  ? handleNavigate(row.studioHref)
                                  : undefined
                              }
                              size="small"
                              style={buildMemberActionButtonStyle()}
                              type="default"
                            />
                          </AevatarTooltip>
                          {row.isEntryMember ? (
                            <AevatarTooltip title={entryActionLabel}>
                              <Button
                                aria-label={entryActionLabel}
                                icon={entryActionIcon}
                                className="team-members-table-action-button team-members-table-entry-action"
                                disabled={
                                  rowBusy ||
                                  (isEntryActionBusy &&
                                    entryActionBusyMemberId !== row.memberId) ||
                                  isDeleteActionBusy
                                }
                                loading={entryActionBusyMemberId === row.memberId}
                                onClick={onClearEntry}
                                size="small"
                                style={buildMemberActionButtonStyle("primary")}
                                type="default"
                              />
                            </AevatarTooltip>
                          ) : row.canSetAsEntry && onSetEntry ? (
                            <AevatarTooltip title={entryActionLabel}>
                              <Button
                                aria-label={entryActionLabel}
                                className="team-members-table-action-button team-members-table-entry-action"
                                disabled={
                                  rowBusy ||
                                  (isEntryActionBusy &&
                                    entryActionBusyMemberId !== row.memberId) ||
                                  isDeleteActionBusy
                                }
                                icon={entryActionIcon}
                                loading={entryActionBusyMemberId === row.memberId}
                                onClick={() => onSetEntry(row.memberId)}
                                size="small"
                                style={buildMemberActionButtonStyle()}
                                type="default"
                              />
                            </AevatarTooltip>
                          ) : null}
                          {onDeleteMember ? (
                            <AevatarTooltip title={deleteActionLabel}>
                              <Button
                                aria-label={deleteActionLabel}
                                className="team-members-table-action-button team-members-table-delete-action"
                                disabled={
                                  rowDeleting ||
                                  (isDeleteActionBusy &&
                                    deletingMemberId !== row.memberId) ||
                                  isEntryActionBusy
                                }
                                icon={<DeleteOutlined />}
                                loading={rowDeleting}
                                onClick={() =>
                                  onDeleteMember({
                                    isEntryMember: row.isEntryMember,
                                    memberId: row.memberId,
                                    name: row.name,
                                  })
                                }
                                size="small"
                                style={buildMemberActionButtonStyle("danger")}
                                type="default"
                              />
                            </AevatarTooltip>
                          ) : null}
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        ) : createMemberHref ? (
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
