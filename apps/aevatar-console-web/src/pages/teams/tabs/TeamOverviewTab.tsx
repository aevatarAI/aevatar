import {
  ClockCircleOutlined,
  EyeOutlined,
  HistoryOutlined,
  InfoCircleOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  ToolOutlined,
} from "@ant-design/icons";
import { Button, Space, Tooltip, Typography, theme } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import { AevatarInspectorEmpty } from "@/shared/ui/aevatarPageShells";
import {
  DetailPill,
  FactLine,
} from "../components/TeamDetailPrimitives";
import { t } from "@/shared/i18n/messages";

type OverviewCompositionRow = {
  readonly bindHref?: string;
  readonly bindLabel?: string;
  readonly canBind?: boolean;
  readonly canRun?: boolean;
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
  readonly workflowHref?: string;
};

type OverviewConfigurationRow = {
  readonly label: string;
  readonly note: string;
  readonly noteTooltip?: string;
  readonly value: string;
};

type OverviewRunRow = {
  readonly detailsHref?: string;
  readonly detailItems: readonly {
    readonly label: string;
    readonly value: string;
  }[];
  readonly detailTooltipLabel: string;
  readonly memberLabel: string;
  readonly outputPreview: string;
  readonly runId: string;
  readonly statusLabel: string;
  readonly statusStyle: React.CSSProperties;
  readonly updatedLabel: string;
  readonly workflowLabel: string;
  readonly workflowMetaLabel: string;
};

type TeamOverviewTabProps = {
  readonly configurationDetailRows: readonly OverviewConfigurationRow[];
  readonly compositionRows: readonly OverviewCompositionRow[];
  readonly currentDeploymentPillStyle: React.CSSProperties;
  readonly currentDeploymentPillText: string;
  readonly currentHeaderStatusFriendly: string;
  readonly currentHeaderStatusStyle: React.CSSProperties;
  readonly currentMemberCardCaption: string;
  readonly currentMemberCardTooltip: string;
  readonly currentMemberLabel: string;
  readonly currentRunCardCaption: string;
  readonly currentRunCardTooltip: string;
  readonly currentRunFriendly: string;
  readonly currentRunPillStyle: React.CSSProperties;
  readonly currentRunPillText: string;
  readonly currentServiceCardCaption: string;
  readonly currentServiceCardTooltip: string;
  readonly currentServiceFriendly: string;
  readonly currentServicePillStyle: React.CSSProperties;
  readonly currentServicePillText: string;
  readonly entryMemberId?: string | null;
  readonly entryMemberLabel?: string;
  readonly entryMemberUpdating?: boolean;
  readonly latestRuns?: readonly OverviewRunRow[];
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly onClearEntryMember?: () => void;
  readonly onNavigate?: (href: string) => void;
  readonly onOpenTeamTest?: () => void;
  readonly startupGuidance: string;
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
  padding: 20,
});

const TeamOverviewTab: React.FC<TeamOverviewTabProps> = ({
  configurationDetailRows,
  compositionRows,
  currentDeploymentPillStyle,
  currentDeploymentPillText,
  currentHeaderStatusFriendly,
  currentHeaderStatusStyle,
  currentMemberCardCaption,
  currentMemberCardTooltip,
  currentMemberLabel,
  currentRunCardCaption,
  currentRunCardTooltip,
  currentRunFriendly,
  currentRunPillStyle,
  currentRunPillText,
  currentServiceCardCaption,
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
  startupGuidance,
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
  const openWorkflowLabel = intl.formatMessage({
    id: "teams.detail.overview.composition.actions.workflow",
  });
  const summaryItems = [
    {
      caption: currentMemberCardCaption,
      label: intl.formatMessage({
        defaultMessage: "Current member",
        id: "teams.detail.overview.cards.currentMember",
      }),
      tooltip: currentMemberCardTooltip,
      value: currentMemberLabel,
    },
    {
      caption: currentServiceCardCaption,
      label: intl.formatMessage({ id: "teams.detail.overview.cards.currentService" }),
      tooltip: currentServiceCardTooltip,
      value: currentServiceFriendly,
    },
    {
      caption: currentRunCardCaption,
      label: intl.formatMessage({ id: "teams.detail.overview.cards.currentRun" }),
      tooltip: currentRunCardTooltip,
      value: currentRunFriendly,
    },
    {
      caption: latestVisibleUpdateNote,
      label: intl.formatMessage({ id: "teams.detail.overview.cards.latestUpdate" }),
      value: latestVisibleUpdateLabel,
    },
    {
      caption: hasEntryMember
        ? intl.formatMessage({
            id: "teams.detail.overview.entry.configuredCaption",
          })
        : intl.formatMessage({
            id: "teams.detail.overview.entry.unconfiguredCaption",
          }),
      label: intl.formatMessage({ id: "teams.detail.overview.cards.entryMember" }),
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
          <div style={{ display: "flex", flex: "1 1 520px", flexDirection: "column", gap: 8 }}>
            <Space wrap size={[8, 6]}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                {intl.formatMessage({ id: "teams.detail.overview.status.title" })}
              </Typography.Text>
              <Typography.Text type="secondary">
                {t("pages.teams.tabs.teamoverviewtab.copy", "Startup status")}
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
            <Typography.Text type="secondary">
              {startupGuidance}
            </Typography.Text>
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
              <Typography.Text strong ellipsis>
                {item.value}
              </Typography.Text>
              <FactLine
                rows={1}
                secondary
                text={item.caption}
                tooltipText={item.tooltip}
              />
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
        <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
          <Typography.Title level={3} style={{ margin: 0 }}>
            {intl.formatMessage({
              id: "teams.detail.overview.composition.title",
            })}
          </Typography.Title>
          {configurationDetailRows.length > 0 ? (
            <Typography.Text style={{ fontSize: 12 }} type="secondary">
              {intl.formatMessage({
                id: "teams.detail.overview.configuration.title",
              })}
            </Typography.Text>
          ) : null}
        </div>
        <div
          style={{
            display: "grid",
            gap: 18,
            gridTemplateColumns:
              configurationDetailRows.length > 0
                ? "repeat(auto-fit, minmax(min(100%, 320px), 1fr))"
                : "minmax(0, 1fr)",
          }}
        >
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
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      {row.serviceLabel || row.summary}
                    </Typography.Text>
                  </div>
                  <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                    <Space size={6} wrap>
                      <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
                      {row.statusLabel && row.statusStyle ? (
                        <DetailPill compact style={row.statusStyle} text={row.statusLabel} />
                      ) : null}
                    </Space>
                    <FactLine rows={2} secondary text={row.summary} />
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
                    {row.workflowHref ? (
                      <Tooltip title={openWorkflowLabel}>
                        <Button
                          aria-label={openWorkflowLabel}
                          href={row.workflowHref}
                          icon={<ToolOutlined />}
                          onClick={handleNavigate(row.workflowHref)}
                          size="small"
                        />
                      </Tooltip>
                    ) : null}
                    {row.bindHref && row.bindLabel ? (
                      <Tooltip title={row.bindLabel}>
                        <Button
                          aria-label={row.bindLabel}
                          href={row.bindHref}
                          icon={<LinkOutlined />}
                          onClick={handleNavigate(row.bindHref)}
                          size="small"
                          type={row.canBind ? "default" : "dashed"}
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

          {configurationDetailRows.length > 0 ? (
            <div
              style={{
                background: token.colorFillQuaternary,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 8,
                display: "grid",
                gap: 0,
              }}
            >
              {configurationDetailRows.map((row, index) => (
                <div
                  key={row.label}
                  style={{
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 6,
                    padding: "10px 12px",
                  }}
                >
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {row.label}
                  </Typography.Text>
                  <Typography.Text strong>{row.value}</Typography.Text>
                  <FactLine
                    rows={2}
                    secondary
                    text={row.note}
                    tooltipText={row.noteTooltip}
                  />
                </div>
              ))}
            </div>
          ) : null}
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
        {latestRuns.length > 0 ? (
          <div style={{ display: "grid", gap: 10 }}>
            {latestRuns.map((run) => (
              <div
                key={run.runId}
                style={{
                  alignItems: "start",
                  border: `1px solid ${token.colorBorderSecondary}`,
                  borderRadius: 8,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "minmax(0, 1fr) max-content",
                  padding: 12,
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <Space size={6} wrap>
                    <DetailPill
                      compact
                      style={run.statusStyle}
                      text={run.statusLabel}
                    />
                    <Typography.Text style={{ fontSize: 12 }} type="secondary">
                      <ClockCircleOutlined /> {run.updatedLabel}
                    </Typography.Text>
                    <Tooltip
                      title={
                        <div style={{ display: "grid", gap: 4 }}>
                          {run.detailItems.map((item) => (
                            <span key={item.label}>
                              {item.label}: {item.value}
                            </span>
                          ))}
                        </div>
                      }
                    >
                      <span
                        aria-label={run.detailTooltipLabel}
                        role="img"
                        style={{ color: token.colorTextTertiary, cursor: "help" }}
                        tabIndex={0}
                      >
                        <InfoCircleOutlined />
                      </span>
                    </Tooltip>
                  </Space>
                  <Typography.Text strong>{run.memberLabel}</Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {run.workflowMetaLabel}
                  </Typography.Text>
                  <FactLine rows={2} secondary text={run.outputPreview} />
                </div>
                {run.detailsHref ? (
                  <Tooltip
                    title={intl.formatMessage({
                      id: "teams.detail.overview.history.actions.view",
                    })}
                  >
                    <Button
                      aria-label={intl.formatMessage({
                        id: "teams.detail.overview.history.actions.view",
                      })}
                      href={run.detailsHref}
                      icon={<EyeOutlined />}
                      onClick={handleNavigate(run.detailsHref)}
                      size="small"
                    />
                  </Tooltip>
                ) : null}
              </div>
            ))}
          </div>
        ) : (
          <div
            style={{
              alignItems: "center",
              background: token.colorFillQuaternary,
              border: `1px dashed ${token.colorBorderSecondary}`,
              borderRadius: 8,
              display: "flex",
              gap: 10,
              justifyContent: "center",
              padding: "18px 16px",
              textAlign: "center",
            }}
          >
            <AevatarInspectorEmpty
              compact
              title={intl.formatMessage({
                id: "teams.detail.overview.history.empty.title",
              })}
              description={intl.formatMessage({
                id: "teams.detail.overview.history.empty.description",
              })}
            />
          </div>
        )}
      </section>
    </div>
  );
};

export default TeamOverviewTab;
