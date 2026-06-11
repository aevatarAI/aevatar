import { Button, Space, Typography, theme } from "antd";
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
  readonly key: string;
  readonly kindLabel: string;
  readonly kindStyle: React.CSSProperties;
  readonly name: string;
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
  readonly latestVisibleUpdateLabel: string;
  readonly latestVisibleUpdateNote: string;
  readonly onClearEntryMember?: () => void;
  readonly startupGuidance: string;
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
  latestVisibleUpdateLabel,
  latestVisibleUpdateNote,
  onClearEntryMember,
  startupGuidance,
}) => {
  const intl = useIntl();
  const { token } = theme.useToken();
  const hasEntryMember = Boolean(entryMemberId?.trim());

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
          <div style={{ display: "flex", justifyContent: "flex-end" }}>
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
                  gap: 12,
                  gridTemplateColumns: "minmax(120px, 180px) minmax(0, 1fr) max-content",
                  paddingTop: index === 0 ? 0 : 16,
                }}
              >
                <Typography.Text strong>{row.name}</Typography.Text>
                <FactLine rows={3} secondary text={row.summary} />
                <DetailPill compact style={row.kindStyle} text={row.kindLabel} />
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
    </div>
  );
};

export default TeamOverviewTab;
