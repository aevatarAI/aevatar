import {
  ClockCircleOutlined,
  HistoryOutlined,
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
  SignalCard,
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
  readonly outputPreview: string;
  readonly revisionLabel: string;
  readonly runId: string;
  readonly serviceLabel: string;
  readonly statusLabel: string;
  readonly statusStyle: React.CSSProperties;
  readonly updatedLabel: string;
  readonly workflowLabel: string;
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
  borderRadius: 24,
  boxShadow: token.boxShadowSecondary,
  display: "flex",
  flexDirection: "column",
  gap: 18,
  padding: 24,
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

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 20 }}>
      <section style={surfaceStyle(token)}>
        <div
          style={{
            alignItems: "flex-start",
            display: "flex",
            flexWrap: "wrap",
            gap: 12,
            justifyContent: "space-between",
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
            <Space wrap size={8}>
              <Typography.Text strong style={{ fontSize: 16 }}>
                {intl.formatMessage({ id: "teams.detail.overview.status.title" })}
              </Typography.Text>
              <Typography.Text type="secondary">
                {t("pages.teams.tabs.teamoverviewtab.copy", "Startup status")}</Typography.Text>
              <DetailPill
                style={currentHeaderStatusStyle}
                text={currentHeaderStatusFriendly}
              />
            </Space>
            <Typography.Text type="secondary">
              {startupGuidance}
            </Typography.Text>
          </div>
          <div
            style={{
              alignItems: "flex-end",
              display: "flex",
              flexDirection: "column",
              gap: 10,
            }}
          >
            <Space wrap size={[8, 8]}>
              <DetailPill
                style={currentServicePillStyle}
                text={currentServicePillText}
              />
              <DetailPill
                style={currentDeploymentPillStyle}
                text={currentDeploymentPillText}
              />
              <DetailPill style={currentRunPillStyle} text={currentRunPillText} />
            </Space>
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
        </div>
        <div
          style={{
            display: "grid",
            gap: 14,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          <SignalCard
            label={intl.formatMessage({
              defaultMessage: "Current member",
              id: "teams.detail.overview.cards.currentMember",
            })}
            value={currentMemberLabel}
            caption={currentMemberCardCaption}
            captionTooltip={currentMemberCardTooltip}
          />
          <SignalCard
            label={intl.formatMessage({ id: "teams.detail.overview.cards.currentService" })}
            value={currentServiceFriendly}
            caption={currentServiceCardCaption}
            captionTooltip={currentServiceCardTooltip}
          />
          <SignalCard
            label={intl.formatMessage({ id: "teams.detail.overview.cards.currentRun" })}
            value={currentRunFriendly}
            caption={currentRunCardCaption}
            captionTooltip={currentRunCardTooltip}
          />
          <SignalCard
            label={intl.formatMessage({ id: "teams.detail.overview.cards.latestUpdate" })}
            value={latestVisibleUpdateLabel}
            caption={latestVisibleUpdateNote}
          />
          <SignalCard
            label={intl.formatMessage({ id: "teams.detail.overview.cards.entryMember" })}
            value={
              entryMemberLabel ||
              entryMemberId ||
              intl.formatMessage({ id: "teams.detail.overview.entry.unconfigured" })
            }
            caption={
              hasEntryMember
                ? intl.formatMessage({
                    id: "teams.detail.overview.entry.configuredCaption",
                  })
                : intl.formatMessage({
                    id: "teams.detail.overview.entry.unconfiguredCaption",
                  })
            }
          />
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

      <div
        style={{
          display: "grid",
          gap: 18,
          gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
        }}
      >
        <div style={surfaceStyle(token)}>
          <div style={{ display: "flex", justifyContent: "space-between", gap: 12 }}>
            <div>
              <Typography.Title level={3} style={{ margin: 0 }}>
                {intl.formatMessage({
                  id: "teams.detail.overview.composition.title",
                })}
              </Typography.Title>
            </div>
          </div>
          {compositionRows.length > 0 ? (
            compositionRows.map((row, index) => (
              <div
                key={row.key}
                style={{
                  alignItems: "start",
                  borderTop:
                    index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                  display: "grid",
                  gap: 14,
                  gridTemplateColumns:
                    "minmax(128px, 180px) minmax(0, 1fr) minmax(150px, max-content)",
                  paddingTop: index === 0 ? 0 : 16,
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
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
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: 8, minWidth: 0 }}>
                  <Space size={6} wrap>
                    <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
                    {row.statusLabel && row.statusStyle ? (
                      <DetailPill compact style={row.statusStyle} text={row.statusLabel} />
                    ) : null}
                  </Space>
                  <FactLine rows={3} secondary text={row.summary} />
                </div>
                <Space size={6} style={{ justifySelf: "end" }} wrap>
                  <Tooltip
                    title={row.canRun ? undefined : row.runDisabledReason}
                  >
                    <Button
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
                    >
                      {intl.formatMessage({
                        id: "teams.detail.overview.composition.actions.run",
                      })}
                    </Button>
                  </Tooltip>
                  {row.workflowHref ? (
                    <Button
                      href={row.workflowHref}
                      icon={<ToolOutlined />}
                      onClick={handleNavigate(row.workflowHref)}
                      size="small"
                    >
                      {intl.formatMessage({
                        id: "teams.detail.overview.composition.actions.workflow",
                      })}
                    </Button>
                  ) : null}
                  {row.bindHref && row.bindLabel ? (
                    <Button
                      href={row.bindHref}
                      onClick={handleNavigate(row.bindHref)}
                      size="small"
                      type={row.canBind ? "default" : "dashed"}
                    >
                      {row.bindLabel}
                    </Button>
                  ) : null}
                </Space>
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
          <section style={surfaceStyle(token)}>
            <Typography.Title level={3} style={{ margin: 0 }}>
              {intl.formatMessage({
                id: "teams.detail.overview.configuration.title",
              })}
            </Typography.Title>
            <div style={{ display: "grid", gap: 12 }}>
              {configurationDetailRows.map((row, index) => (
                <div
                  key={row.label}
                  style={{
                    alignItems: "start",
                    borderTop:
                      index === 0 ? "none" : `1px solid ${token.colorBorderSecondary}`,
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "minmax(96px, 128px) minmax(0, 1fr)",
                    paddingTop: index === 0 ? 0 : 12,
                  }}
                >
                  <Typography.Text style={{ paddingTop: 2 }} type="secondary">
                    {row.label}
                  </Typography.Text>
                  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                    <Typography.Text strong>{row.value}</Typography.Text>
                    <FactLine
                      rows={2}
                      secondary
                      text={row.note}
                      tooltipText={row.noteTooltip}
                    />
                  </div>
                </div>
              ))}
            </div>
          </section>
        ) : null}
      </div>

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
          <Typography.Text style={{ fontSize: 12 }} type="secondary">
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
                  borderRadius: 12,
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns:
                    "minmax(150px, 0.8fr) minmax(0, 1fr) max-content",
                  padding: 14,
                }}
              >
                <div style={{ display: "flex", flexDirection: "column", gap: 6, minWidth: 0 }}>
                  <Space size={6} wrap>
                    <Typography.Text strong>{run.runId}</Typography.Text>
                    <DetailPill
                      compact
                      style={run.statusStyle}
                      text={run.statusLabel}
                    />
                  </Space>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    <ClockCircleOutlined /> {run.updatedLabel}
                  </Typography.Text>
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
                  <Typography.Text>
                    {run.workflowLabel} · {run.revisionLabel}
                  </Typography.Text>
                  <Typography.Text style={{ fontSize: 12 }} type="secondary">
                    {run.serviceLabel}
                  </Typography.Text>
                  <FactLine rows={2} secondary text={run.outputPreview} />
                </div>
                {run.detailsHref ? (
                  <Button
                    href={run.detailsHref}
                    onClick={handleNavigate(run.detailsHref)}
                    size="small"
                  >
                    {intl.formatMessage({
                      id: "teams.detail.overview.history.actions.view",
                    })}
                  </Button>
                ) : null}
              </div>
            ))}
          </div>
        ) : (
          <AevatarInspectorEmpty
            compact
            title={intl.formatMessage({
              id: "teams.detail.overview.history.empty.title",
            })}
            description={intl.formatMessage({
              id: "teams.detail.overview.history.empty.description",
            })}
          />
        )}
      </section>
    </div>
  );
};

export default TeamOverviewTab;
