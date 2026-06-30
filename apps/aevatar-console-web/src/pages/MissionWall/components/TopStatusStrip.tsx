import React from "react";
import { t } from "@/shared/i18n/messages";
import type { MissionWallSnapshot } from "../models";
import { formatFreshness, formatLiveStatus } from "../missionWallFormatters";

function Metric({
  label,
  tone,
  value,
}: {
  readonly label: string;
  readonly tone?: "fresh" | "live" | "red" | "yellow";
  readonly value: React.ReactNode;
}) {
  const valueClassName = [
    "mission-wall-metric__value",
    tone ? `mission-wall-metric__value--${tone}` : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div className="mission-wall-metric">
      <span className="mission-wall-metric__label">{label}</span>
      <span className={valueClassName}>{value}</span>
    </div>
  );
}

export function TopStatusStrip({
  snapshot,
}: {
  readonly snapshot: MissionWallSnapshot;
}) {
  return (
    <header className="mission-wall-top-strip">
      <div className="mission-wall-brand">
        <div className="mission-wall-brand__kicker">
          {t(
            "pages.missionwall.runtimeKicker",
            "AEVATAR WORKFLOW RUNTIME",
          )}
        </div>
        <h1 className="mission-wall-brand__title">
          {t(
            "pages.missionwall.title",
            "Published Run Mission Wall",
          )}
        </h1>
      </div>
      <Metric
        label={t("pages.missionwall.metric.live", "Live")}
        tone="live"
        value={
          <>
            <span className="mission-wall-live-dot" />
            {formatLiveStatus(snapshot.live.status)}
          </>
        }
      />
      <Metric
        label={t("pages.missionwall.metric.running", "Running")}
        value={snapshot.summary.runningRuns}
      />
      <Metric
        label={t("pages.missionwall.metric.waiting", "Waiting")}
        tone="yellow"
        value={snapshot.summary.waitingHuman}
      />
      <Metric
        label={t("pages.missionwall.metric.failed", "Failed")}
        tone="red"
        value={snapshot.summary.failedRuns}
      />
      <Metric
        label={t("pages.missionwall.metric.retrying", "Retrying")}
        value={snapshot.summary.retryingRuns}
      />
      <Metric
        label={t("pages.missionwall.metric.freshness", "Fresh")}
        tone="fresh"
        value={formatFreshness(snapshot.summary.projectionFreshnessSeconds)}
      />
    </header>
  );
}
