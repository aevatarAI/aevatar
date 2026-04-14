import { Alert, Button, Grid, Space, Typography, theme } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildTeamWorkspaceRoute } from "@/shared/navigation/scopeRoutes";
import { AevatarPageShell, AevatarPanel, AevatarStatusTag } from "@/shared/ui/aevatarPageShells";
import {
  RosterField,
  SharedTeamsHomeProps,
  deriveRosterReason,
  formatFreshnessAge,
  renderHealthLabel,
  resolveFreshnessTimestamp,
  summarizeOwner,
} from "./teamsHomeShared";

const TeamsHomeRosterV0: React.FC<SharedTeamsHomeProps> = ({
  lens,
  resolvedScopeId,
  teamSignalIssues,
}) => {
  const { token } = theme.useToken();
  const screens = Grid.useBreakpoint();
  const reason = deriveRosterReason(lens);
  const currentOwner = summarizeOwner(
    lens.graph.focusActorId,
    lens.currentRun?.actorId,
  );
  const freshnessAge = formatFreshnessAge(resolveFreshnessTimestamp(lens));
  const rowTruthStatus = lens.partialSignals.length > 0 ? "partial" : "live";
  const rowTruthLabel =
    lens.partialSignals.length > 0 ? "Partial truth" : "Live truth";
  const recentActivityLabel =
    lens.recentRunCount === 1 ? "1 visible run" : `${lens.recentRunCount} visible runs`;
  const whyNowBorderColor =
    lens.healthStatus === "healthy"
      ? token.colorInfoBorder
      : token.colorWarningBorder;
  const rosterGridTemplateColumns = screens.xxl
    ? "minmax(220px, 1.1fr) minmax(260px, 1.35fr) minmax(200px, 0.95fr) minmax(180px, 0.9fr) minmax(152px, 0.7fr)"
    : screens.xl
      ? "minmax(220px, 1.1fr) minmax(260px, 1.35fr) minmax(200px, 0.95fr) minmax(180px, 0.9fr)"
      : screens.md
        ? "minmax(220px, 1fr) minmax(240px, 1.15fr)"
        : "1fr";
  const actionFieldStyle = screens.xxl
    ? { alignSelf: "start" as const }
    : screens.md
      ? {
          alignItems: "flex-end" as const,
          gridColumn: "1 / -1",
        }
      : undefined;
  const actionButtonStyle = screens.xxl
    ? { width: "100%" }
    : screens.md
      ? { minWidth: 168 }
      : { width: "100%" };
  const actionHelperStyle: React.CSSProperties = {
    maxWidth: screens.xxl ? 190 : screens.md ? 260 : undefined,
    textAlign: screens.xxl ? "left" : screens.md ? "right" : "left",
  };
  const technicalTextStyle: React.CSSProperties = {
    overflowWrap: "anywhere",
    wordBreak: "break-word",
  };

  return (
    <AevatarPageShell
      content="Read the current session team like a roster row before you open the deeper workspace."
      title="Teams"
      titleHelp="This is a roster-style preview for the team exposed by the active session. It does not rank multiple teams yet."
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        <div
          style={{
            alignItems: screens.md ? "center" : "flex-start",
            background: token.colorBgContainer,
            border: `1px solid ${token.colorBorderSecondary}`,
            borderRadius: 20,
            display: "flex",
            flexDirection: screens.md ? "row" : "column",
            gap: 12,
            justifyContent: "space-between",
            padding: screens.md ? "16px 18px" : "16px",
          }}
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text strong>Reference roster</Typography.Text>
            <Typography.Text type="secondary">
              This page uses roster grammar to preview the team currently exposed
              by the active session. It does not rank multiple teams yet.
            </Typography.Text>
          </div>
          <Space size={8} wrap>
            <AevatarStatusTag
              domain="observation"
              label="Current session team only"
              status="partial"
            />
            <AevatarStatusTag
              domain="observation"
              label={rowTruthLabel}
              status={rowTruthStatus}
            />
          </Space>
        </div>

        {teamSignalIssues.length > 0 ? (
          <Alert
            description={teamSignalIssues.join(" ")}
            title="Some team signals are currently unavailable"
            showIcon
            type="warning"
          />
        ) : null}

        <AevatarPanel
          extra={
            <Space size={8} wrap>
              <AevatarStatusTag
                domain="run"
                label={renderHealthLabel(lens.healthStatus)}
                status={
                  lens.healthStatus === "healthy"
                    ? "completed"
                    : lens.healthStatus
                }
              />
              <AevatarStatusTag
                domain="observation"
                label={lens.currentBindingTarget}
                status="live"
              />
            </Space>
          }
          title="Current session roster"
          description="One team, read like a roster row. The question here is whether it is worth opening this team right now."
        >
          <div
            style={{
              alignItems: "start",
              background: token.colorBgLayout,
              border: `1px solid ${token.colorBorderSecondary}`,
              borderRadius: 24,
              display: "grid",
              gap: 20,
              gridTemplateColumns: rosterGridTemplateColumns,
              padding: screens.md ? 24 : 18,
            }}
          >
            <RosterField title="Identity">
              <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <Typography.Title
                  level={4}
                  style={{ margin: 0, ...technicalTextStyle }}
                >
                  {lens.title}
                </Typography.Title>
                <Typography.Text type="secondary" style={technicalTextStyle}>
                  {resolvedScopeId}
                </Typography.Text>
                <Typography.Paragraph style={{ margin: 0, ...technicalTextStyle }}>
                  {lens.subtitle}
                </Typography.Paragraph>
              </div>
            </RosterField>

            <RosterField title="Why now">
              <div
                style={{
                  background: token.colorBgContainer,
                  border: `1px solid ${whyNowBorderColor}`,
                  borderRadius: 18,
                  display: "flex",
                  flexDirection: "column",
                  gap: 10,
                  padding: 16,
                }}
              >
                <Typography.Text strong style={{ fontSize: 16 }}>
                  {reason.label}
                </Typography.Text>
                <Typography.Paragraph style={{ margin: 0 }}>
                  {reason.detail}
                </Typography.Paragraph>
                {reason.support.length > 0 ? (
                  <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    {reason.support.map((item) => (
                      <Typography.Text key={item} type="secondary">
                        {item}
                      </Typography.Text>
                    ))}
                  </div>
                ) : null}
              </div>
            </RosterField>

            <RosterField title="Ownership">
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                <div>
                  <Typography.Text strong>Current owner</Typography.Text>
                  <Typography.Paragraph
                    style={{ margin: "4px 0 0", ...technicalTextStyle }}
                  >
                    {currentOwner}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Current focus</Typography.Text>
                  <Typography.Paragraph
                    style={{ margin: "4px 0 0", ...technicalTextStyle }}
                  >
                    {lens.graph.focusReason}
                  </Typography.Paragraph>
                </div>
              </div>
            </RosterField>

            <RosterField title="Signals">
              <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
                <div>
                  <Typography.Text strong>Recent activity</Typography.Text>
                  <Typography.Paragraph style={{ margin: "4px 0 0" }}>
                    {recentActivityLabel}
                  </Typography.Paragraph>
                </div>
                <div>
                  <Typography.Text strong>Freshness</Typography.Text>
                  <Typography.Paragraph style={{ margin: "4px 0 0" }}>
                    {freshnessAge}
                  </Typography.Paragraph>
                </div>
                {lens.partialSignals.length > 0 ? (
                  <div>
                    <Typography.Text strong>Missing signals</Typography.Text>
                    <Typography.Paragraph
                      style={{ margin: "4px 0 0", ...technicalTextStyle }}
                    >
                      {lens.partialSignals.join(" · ")}
                    </Typography.Paragraph>
                  </div>
                ) : null}
              </div>
            </RosterField>

            <RosterField style={actionFieldStyle} title="Action">
              <div
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: 10,
                  justifyContent: screens.xxl ? "stretch" : screens.md ? "flex-end" : "stretch",
                  width: "100%",
                }}
              >
                <Button
                  onClick={() => history.push(buildTeamWorkspaceRoute(resolvedScopeId))}
                  style={actionButtonStyle}
                  type="primary"
                >
                  View details
                </Button>
                <Typography.Text style={actionHelperStyle} type="secondary">
                  Open the full workspace for compare, topology, playback, and governance detail.
                </Typography.Text>
              </div>
            </RosterField>
          </div>
        </AevatarPanel>
      </div>
    </AevatarPageShell>
  );
};

export default TeamsHomeRosterV0;
