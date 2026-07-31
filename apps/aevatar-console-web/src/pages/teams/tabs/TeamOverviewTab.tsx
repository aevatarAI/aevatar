import {
  HistoryOutlined,
  PlayCircleOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import TeamRecentRunsList, {
  type TeamActivityRunRow,
} from "../components/TeamRecentRunsList";
import { DetailPill } from "../components/TeamDetailPrimitives";

type OverviewCompositionRow = {
  readonly canConfigure?: boolean;
  readonly canRun?: boolean;
  readonly configureDisabledReason?: string;
  readonly configureHref?: string;
  readonly configureLabel?: string;
  readonly entryLabel?: string;
  readonly key: string;
  readonly kindLabel: string;
  readonly kindStyle: React.CSSProperties;
  readonly name: string;
  readonly runDisabledReason?: string;
  readonly runHref?: string;
  readonly selectedLabel?: string;
  readonly serviceLabel?: string;
  readonly statusLabel?: string;
  readonly statusStyle?: React.CSSProperties;
  readonly summary: string;
};

type OverviewConfigurationRow = {
  readonly label: string;
  readonly note: string;
  readonly noteTooltip?: string;
  readonly value: string;
};

type TeamOverviewTabProps = {
  readonly configurationDetailRows: readonly OverviewConfigurationRow[];
  readonly compositionRows: readonly OverviewCompositionRow[];
  readonly currentDeploymentPillStyle: React.CSSProperties;
  readonly currentDeploymentPillText: string;
  readonly currentHeaderStatusFriendly: string;
  readonly currentHeaderStatusStyle: React.CSSProperties;
  readonly currentMemberCardTooltip: string;
  readonly currentMemberLabel: string;
  readonly currentRunCardTooltip: string;
  readonly currentRunFriendly: string;
  readonly currentRunPillStyle: React.CSSProperties;
  readonly currentRunPillText: string;
  readonly currentServiceCardTooltip: string;
  readonly currentServiceFriendly: string;
  readonly currentServicePillStyle: React.CSSProperties;
  readonly currentServicePillText: string;
  readonly entryMemberId?: string | null;
  readonly entryMemberLabel?: string;
  readonly entryMemberUpdating?: boolean;
  readonly latestRuns?: readonly TeamActivityRunRow[];
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly onClearEntryMember?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onOpenTeamTest?: () => void;
  readonly teamRunDisabled?: boolean;
  readonly teamRunDisabledReason?: string;
};

const surfaceStyle = (
  token: ReturnType<typeof theme.useToken>["token"],
): React.CSSProperties => ({
  background: token.colorBgContainer,
  border: `1px solid ${token.colorBorderSecondary}`,
  borderRadius: 8,
  boxShadow: token.boxShadowTertiary,
  display: "flex",
  flexDirection: "column",
  gap: 16,
  minWidth: 0,
  padding: 20,
});

const TeamOverviewTab: React.FC<TeamOverviewTabProps> = ({
  configurationDetailRows,
  compositionRows,
  currentDeploymentPillStyle,
  currentDeploymentPillText,
  currentHeaderStatusFriendly,
  currentHeaderStatusStyle,
  currentMemberCardTooltip,
  currentMemberLabel,
  currentRunCardTooltip,
  currentRunFriendly,
  currentRunPillStyle,
  currentRunPillText,
  currentServiceCardTooltip,
  currentServiceFriendly,
  currentServicePillStyle,
  currentServicePillText,
  entryMemberId,
  entryMemberLabel,
  entryMemberUpdating = false,
  latestRuns = [],
  latestVisibleUpdateLabel,
  latestVisibleUpdateNote,
  onClearEntryMember,
  onNavigate,
  onOpenTeamTest,
  teamRunDisabled = false,
  teamRunDisabledReason,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const hasEntryMember = Boolean(entryMemberId?.trim());
  const handleNavigate = React.useCallback(
    (href?: string) => (event: React.MouseEvent<HTMLElement>) => {
      if (!href || !onNavigate) {
        return;
      }

      event.preventDefault();
      onNavigate(href);
    },
    [onNavigate],
  );
  const entryMemberValue =
    entryMemberLabel ||
    entryMemberId ||
    intl.formatMessage({ id: "teams.detail.overview.entry.unconfigured" });
  const runMemberLabel = intl.formatMessage({
    id: "teams.detail.overview.composition.actions.run",
  });
  const summaryItems = [
    {
      label: intl.formatMessage({
        defaultMessage: "Current member",
        id: "teams.detail.overview.cards.currentMember",
      }),
      tooltip: currentMemberCardTooltip,
      value: currentMemberLabel,
    },
    {
      label: intl.formatMessage({ id: "teams.detail.overview.cards.currentService" }),
      tooltip: currentServiceCardTooltip,
      value: currentServiceFriendly,
    },
    {
      label: intl.formatMessage({ id: "teams.detail.overview.cards.currentRun" }),
      tooltip: currentRunCardTooltip,
      value: currentRunFriendly,
    },
    {
      label: intl.formatMessage({ id: "teams.detail.overview.cards.latestUpdate" }),
      tooltip: latestVisibleUpdateNote,
      value: latestVisibleUpdateLabel,
    },
    {
      label: intl.formatMessage({ id: "teams.detail.overview.cards.entryMember" }),
      tooltip: hasEntryMember
        ? intl.formatMessage({
            id: "teams.detail.overview.entry.configuredCaption",
          })
        : intl.formatMessage({
            id: "teams.detail.overview.entry.unconfiguredCaption",
          }),
      value: entryMemberValue,
    },
  ];

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
      <section style={surfaceStyle(token)}>
        <div
          style={{
            alignItems: "flex-start",
            display: "flex",
            flexWrap: "wrap",
            gap: 16,
            justifyContent: "space-between",
          }}
        >
          <div
            style={{
              display: "flex",
              flex: "1 1 520px",
              flexDirection: "column",
              gap: 8,
              minWidth: 0,
            }}
          >
            <Space wrap size={[8, 6]}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                {intl.formatMessage({ id: "teams.detail.overview.status.title" })}
              </Typography.Text>
              <DetailPill
                style={currentHeaderStatusStyle}
                text={currentHeaderStatusFriendly}
              />
              <DetailPill
                compact
                style={currentServicePillStyle}
                text={currentServicePillText}
              />
              <DetailPill
                compact
                style={currentDeploymentPillStyle}
                text={currentDeploymentPillText}
              />
              <DetailPill compact style={currentRunPillStyle} text={currentRunPillText} />
            </Space>
          </div>
          <Tooltip title={teamRunDisabled ? teamRunDisabledReason : undefined}>
            <Button
              disabled={teamRunDisabled}
              icon={<PlayCircleOutlined />}
              onClick={onOpenTeamTest}
              type="primary"
            >
              {intl.formatMessage({
                id: "teams.detail.overview.quickRun.runTeam",
              })}
            </Button>
          </Tooltip>
        </div>
        <div
          style={{
            background: token.colorFillQuaternary,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 8,
            display: "grid",
            gap: 0,
            gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))",
          }}
        >
          {summaryItems.map((item, index) => (
            <div
              key={item.label}
              style={{
                borderLeft:
                  index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                display: "flex",
                flexDirection: "column",
                gap: 4,
                minWidth: 0,
                padding: "10px 12px",
              }}
            >
              <Typography.Text style={{ fontSize: 12 }} type="secondary">
                {item.label}
              </Typography.Text>
              <Tooltip title={item.tooltip}>
                <Typography.Text strong ellipsis>
                  {item.value}
                </Typography.Text>
              </Tooltip>
            </div>
          ))}
        </div>
        {hasEntryMember && onClearEntryMember ? (
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 8,
              justifyContent: "space-between",
            }}
          >
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {intl.formatMessage({
                id: "teams.detail.overview.quickRun.entryHint",
              })}
            </Typography.Text>
            <Button
              loading={entryMemberUpdating}
              onClick={onClearEntryMember}
              size="small"
            >
              {intl.formatMessage({ id: "teams.members.actions.clearEntry" })}
            </Button>
          </div>
        ) : null}
      </section>

      <section style={surfaceStyle(token)}>
        <div
          style={{
            alignItems: "center",
            display: "flex",
            justifyContent: "space-between",
            gap: 12,
          }}
        >
          <Typography.Title level={3} style={{ margin: 0 }}>
            {intl.formatMessage({
              id: "teams.detail.overview.composition.title",
            })}
          </Typography.Title>
        </div>
        {configurationDetailRows.length > 0 ? (
          <div
            style={{
              background: token.colorFillQuaternary,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 8,
              display: "grid",
              gap: 0,
              gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
            }}
          >
            {configurationDetailRows.map((row, index) => (
              <div
                key={row.label}
                style={{
                  borderLeft:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "flex",
                  flexDirection: "column",
                  gap: 4,
                  minWidth: 0,
                  padding: "10px 12px",
                }}
              >
                <Typography.Text style={{ fontSize: 12 }} type="secondary">
                  {row.label}
                </Typography.Text>
                <Tooltip title={row.noteTooltip || row.note}>
                  <Typography.Text strong ellipsis>
                    {row.value}
                  </Typography.Text>
                </Tooltip>
              </div>
            ))}
          </div>
        ) : null}
        <div style={{ display: "grid", gap: 0 }}>
          {compositionRows.length > 0 ? (
            compositionRows.map((row, index) => (
              <div
                key={row.key}
                style={{
                  alignItems: "center",
                  borderTop:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns:
                    "minmax(120px, 0.65fr) minmax(0, 1fr) max-content",
                  padding: index === 0 ? "0 0 12px" : "12px 0",
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                  <Space size={6} wrap>
                    <Typography.Text strong>{row.name}</Typography.Text>
                    {row.entryLabel ? (
                      <DetailPill
                        compact
                        style={{
                          background: token.colorSuccessBg,
                          border: `1px solid ${token.colorSuccessBorder}`,
                          color: token.colorSuccess,
                        }}
                        text={row.entryLabel}
                      />
                    ) : null}
                    {row.selectedLabel ? (
                      <DetailPill
                        compact
                        style={{
                          background: token.colorInfoBg,
                          border: `1px solid ${token.colorInfoBorder}`,
                          color: token.colorInfo,
                        }}
                        text={row.selectedLabel}
                      />
                    ) : null}
                  </Space>
                  {row.serviceLabel ? (
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      {row.serviceLabel}
                    </Typography.Text>
                  ) : null}
                  {row.summary ? (
                    <Typography.Text ellipsis style={{ fontSize: 12 }} type="secondary">
                      {row.summary}
                    </Typography.Text>
                  ) : null}
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <Space size={6} wrap>
                    <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
                    {row.statusLabel && row.statusStyle ? (
                      <DetailPill compact style={row.statusStyle} text={row.statusLabel} />
                    ) : null}
                  </Space>
                </div>
                <Space.Compact>
                  <Tooltip
                    title={
                      row.canRun
                        ? runMemberLabel
                        : row.runDisabledReason || runMemberLabel
                    }
                  >
                    <Button
                      aria-label={runMemberLabel}
                      href={row.canRun ? row.runHref : undefined}
                      disabled={!row.canRun}
                      icon={<PlayCircleOutlined />}
                      onClick={
                        row.canRun
                          ? handleNavigate(row.runHref)
                          : undefined
                      }
                      size="small"
                      type={row.canRun ? "primary" : "default"}
                    />
                  </Tooltip>
                  {row.configureLabel ? (
                    <Tooltip
                      title={
                        row.canConfigure
                          ? row.configureLabel
                          : row.configureDisabledReason || row.configureLabel
                      }
                    >
                      <Button
                        aria-label={row.configureLabel}
                        disabled={!row.canConfigure}
                        href={row.canConfigure ? row.configureHref : undefined}
                        icon={<ToolOutlined />}
                        onClick={
                          row.canConfigure
                            ? handleNavigate(row.configureHref)
                            : undefined
                        }
                        size="small"
                        type={row.canConfigure ? "default" : "dashed"}
                      />
                    </Tooltip>
                  ) : null}
                </Space.Compact>
              </div>
            ))
          ) : (
            <AevatarInspectorEmpty
              title={intl.formatMessage({
                id: "teams.detail.overview.composition.empty.title",
              })}
              description={intl.formatMessage({
                id: "teams.detail.overview.composition.empty.description",
              })}
            />
          )}
        </div>
      </section>

      <section style={surfaceStyle(token)}>
        <div
          style={{
            alignItems: "center",
            display: "flex",
            flexWrap: "wrap",
            gap: 10,
            justifyContent: "space-between",
          }}
        >
          <Space size={8} wrap>
            <HistoryOutlined style={{ color: token.colorPrimary }} />
            <Typography.Title level={3} style={{ margin: 0 }}>
              {intl.formatMessage({
                id: "teams.detail.overview.history.title",
              })}
            </Typography.Title>
          </Space>
          <Typography.Text style={{ fontSize: 12, maxWidth: 360 }} type="secondary">
            {intl.formatMessage({
              id: "teams.detail.overview.history.subtitle",
            })}
          </Typography.Text>
        </div>
        <TeamRecentRunsList
          emptyDescription={intl.formatMessage({
            id: "teams.detail.overview.history.empty.description",
          })}
          emptyTitle={intl.formatMessage({
            id: "teams.detail.overview.history.empty.title",
          })}
          onNavigate={onNavigate}
          runs={latestRuns}
        />
      </section>
    </div>
  );
};

export default TeamOverviewTab;
