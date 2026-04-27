import { Alert, Button, Empty, Skeleton, Space, Typography, theme } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import { buildStudioWorkflowEditorRoute } from "@/shared/studio/navigation";
import {
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";
import {
  buildAevatarMetricCardStyle,
  resolveAevatarMetricVisual,
  type AevatarSemanticTone,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import type { TeamRuntimeLens } from "./runtime/teamRuntimeLens";

export type SharedTeamsHomeProps = {
  actorGraphUnavailable: boolean;
  activityUnavailable: boolean;
  lens: TeamRuntimeLens;
  resolvedScopeId: string;
  teamSignalIssues: string[];
};

type SummaryCardProps = {
  caption?: React.ReactNode;
  icon?: React.ReactNode;
  label: React.ReactNode;
  tone?: AevatarSemanticTone;
  value: React.ReactNode;
};

type RosterFieldProps = {
  children: React.ReactNode;
  style?: React.CSSProperties;
  title: string;
};

export type RosterReason = {
  detail: string;
  label: string;
  support: string[];
};

export function renderHealthLabel(status: string): string {
  switch (status) {
    case "human-overridden":
      return "Human Override";
    case "blocked":
      return "Blocked";
    case "degraded":
      return "Degraded";
    case "healthy":
      return "Healthy";
    default:
      return "Attention";
  }
}

export function summarizeOwner(
  focusActorId?: string,
  fallbackActorId?: string,
): string {
  const resolvedActorId = focusActorId?.trim() || fallbackActorId?.trim() || "";
  if (!resolvedActorId) {
    return "No current owner visible";
  }

  return resolvedActorId;
}

export function summarizeLatestHandoff(
  fromActorId?: string,
  toActorId?: string,
): string {
  const from = fromActorId?.trim() || "";
  const to = toActorId?.trim() || "";
  if (!from || !to) {
    return "No visible handoff yet";
  }

  return `${from} -> ${to}`;
}

export function resolveFreshnessTimestamp(lens: TeamRuntimeLens): string | null {
  const playbackStepTimestamp =
    lens.playback.steps.find((step) => Boolean(step.timestamp))?.timestamp ?? null;
  const playbackEventTimestamp =
    lens.playback.events.find((event) => Boolean(event.timestamp))?.timestamp ?? null;

  return (
    lens.currentRun?.lastUpdatedAt || playbackStepTimestamp || playbackEventTimestamp
  );
}

export function formatFreshnessAge(timestamp: string | null): string {
  const parsed = Date.parse(timestamp || "");
  if (!Number.isFinite(parsed)) {
    return "Not visible";
  }

  const ageMs = Math.max(0, Date.now() - parsed);
  if (ageMs < 60_000) {
    return "<1m ago";
  }

  const ageMinutes = Math.floor(ageMs / 60_000);
  if (ageMinutes < 60) {
    return `${ageMinutes}m ago`;
  }

  const ageHours = Math.floor(ageMinutes / 60);
  if (ageHours < 24) {
    return `${ageHours}h ago`;
  }

  const ageDays = Math.floor(ageHours / 24);
  return `${ageDays}d ago`;
}

export function deriveRosterReason(lens: TeamRuntimeLens): RosterReason {
  const compareSummary = lens.compare.summary.trim();
  const missingSignals =
    lens.partialSignals.length > 0
      ? `Missing signals: ${lens.partialSignals.join(" · ")}`
      : "";

  if (
    lens.healthStatus === "blocked" ||
    lens.healthStatus === "human-overridden"
  ) {
    return {
      detail: lens.playback.prompt || lens.healthSummary,
      label: "A recent run is waiting on human input",
      support: [compareSummary].filter(Boolean).slice(0, 2),
    };
  }

  if (lens.healthStatus === "degraded" || lens.healthStatus === "attention") {
    return {
      detail: lens.healthSummary,
      label: "Current runtime health still needs attention",
      support: [compareSummary].filter(Boolean).slice(0, 2),
    };
  }

  if (lens.partialSignals.length > 0) {
    return {
      detail: missingSignals,
      label: "Some runtime signals are incomplete",
      support: [compareSummary].filter(Boolean).slice(0, 2),
    };
  }

  if ((lens.recentRunCount ?? 0) === 0) {
    return {
      detail:
        "No recent run is visible yet, so this page can only offer a reference read before you open the team workspace.",
      label: "No recent runtime proof is visible",
      support: [compareSummary].filter(Boolean).slice(0, 2),
    };
  }

  return {
    detail:
      lens.healthSummary ||
      "Recent runtime signals look healthy enough to continue in the team workspace.",
    label: "The current session team looks stable",
    support: [compareSummary].filter(Boolean).slice(0, 2),
  };
}

export function SummaryCard({
  caption,
  icon,
  label,
  tone = "default",
  value,
}: SummaryCardProps) {
  const { token } = theme.useToken();
  const visual = resolveAevatarMetricVisual(
    token as AevatarThemeSurfaceToken,
    tone,
  );

  return (
    <div
      style={{
        ...buildAevatarMetricCardStyle(token as AevatarThemeSurfaceToken, tone),
        display: "flex",
        flexDirection: "column",
        gap: 10,
        minHeight: 120,
        padding: 16,
      }}
    >
      <Space align="center" size={10}>
        {icon ? (
          <span
            style={{
              alignItems: "center",
              color: visual.iconColor,
              display: "inline-flex",
              fontSize: 18,
              justifyContent: "center",
            }}
          >
            {icon}
          </span>
        ) : null}
        <Typography.Text
          style={{
            color: visual.labelColor,
          }}
        >
          {label}
        </Typography.Text>
      </Space>
      <Typography.Title
        level={4}
        style={{
          color: visual.valueColor,
          margin: 0,
        }}
      >
        {value}
      </Typography.Title>
      {caption ? (
        <Typography.Text
          style={{
            color: visual.secondaryColor,
          }}
        >
          {caption}
        </Typography.Text>
      ) : null}
    </div>
  );
}

export function RosterField({ children, style, title }: RosterFieldProps) {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 10,
        minWidth: 0,
        ...style,
      }}
    >
      <Typography.Text
        style={{
          color: token.colorTextDescription,
          fontSize: 12,
          letterSpacing: "0.05em",
          textTransform: "uppercase",
        }}
      >
        {title}
      </Typography.Text>
      {children}
    </div>
  );
}

export const TeamsHomeLoading: React.FC = () => {
  return (
    <AevatarPageShell
      content="Read the current session team like a roster row before you open the deeper workspace."
      title="Teams"
      titleHelp="This page keeps a roster-shaped shell even while the current session team is still loading."
    >
      <AevatarPanel
        extra={
          <AevatarStatusTag
            domain="observation"
            label="Loading"
            status="delayed"
          />
        }
        title="Current session team"
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 18 }}>
          <Skeleton active paragraph={{ rows: 2 }} title />
          <Skeleton active paragraph={{ rows: 8 }} title={false} />
        </div>
      </AevatarPanel>
    </AevatarPageShell>
  );
};

export const TeamContextUnavailable: React.FC<{
  authSessionErrored: boolean;
  onRetry: () => void;
  teamResolutionDescription: string;
}> = ({ authSessionErrored, onRetry, teamResolutionDescription }) => {
  return (
    <AevatarPageShell
      title="Teams"
      content="Open a current team when one is available, or start the first team from Studio."
    >
      <AevatarPanel
        extra={
          <AevatarStatusTag
            domain="observation"
            label={authSessionErrored ? "Unavailable" : "Empty"}
            status={authSessionErrored ? "unavailable" : "partial"}
          />
        }
        title="Team context unavailable"
      >
        <Space orientation="vertical" size={16} style={{ width: "100%" }}>
          <Typography.Paragraph style={{ margin: 0 }}>
            The console could not resolve a current team from the active session.
            Open Settings, retry, or start the first team from Studio.
          </Typography.Paragraph>
          {authSessionErrored ? (
            <Alert
              description={teamResolutionDescription}
              title="Current session lookup failed"
              showIcon
              type="warning"
            />
          ) : null}
          <Empty description={teamResolutionDescription} />
          <Space wrap>
            {authSessionErrored ? (
              <Button onClick={onRetry} type="primary">
                Retry
              </Button>
            ) : (
              <Button
                onClick={() =>
                  history.push(
                    buildStudioWorkflowEditorRoute(),
                  )
                }
                type="primary"
              >
                Open Studio
              </Button>
            )}
            {authSessionErrored ? (
              <Button
                onClick={() =>
                  history.push(
                    buildStudioWorkflowEditorRoute(),
                  )
                }
              >
                Open Studio
              </Button>
            ) : null}
            <Button onClick={() => history.push("/settings")}>Open Settings</Button>
          </Space>
        </Space>
      </AevatarPanel>
    </AevatarPageShell>
  );
};
