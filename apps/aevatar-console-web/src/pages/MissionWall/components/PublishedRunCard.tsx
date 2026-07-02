import React from "react";
import { t } from "@/shared/i18n/messages";
import type { MissionWallRun } from "../models";
import {
  formatDuration,
  formatRunStage,
  formatRunStatus,
  priorityTone,
} from "../missionWallFormatters";

function progressPercent(run: MissionWallRun): number {
  if (!run.progress?.totalSteps) {
    return 0;
  }

  return Math.min(
    100,
    Math.max(
      0,
      Math.round((run.progress.completedSteps / run.progress.totalSteps) * 100),
    ),
  );
}

export function PublishedRunCard({
  focus,
  onSelect,
  run,
}: {
  readonly focus: boolean;
  readonly onSelect: (runId: string) => void;
  readonly run: MissionWallRun;
}) {
  const tone = priorityTone(run.priorityLevel, run.status);
  const cardClassName = [
    "mission-wall-run-card",
    `mission-wall-tone--${tone}`,
    focus ? "mission-wall-run-card--focus" : "",
  ]
    .filter(Boolean)
    .join(" ");
  const completedSteps = run.progress?.completedSteps ?? 0;
  const totalSteps = run.progress?.totalSteps ?? 0;
  const hasRuntimeRun = run.hasRuntimeRun !== false;

  return (
    <button
      aria-current={focus ? "true" : undefined}
      aria-pressed={focus}
      className={cardClassName}
      data-run-id={run.runId}
      onClick={() => onSelect(run.runId)}
      type="button"
    >
      <div className="mission-wall-row">
        <div style={{ minWidth: 0 }}>
          <div className="mission-wall-run-card__name">{run.workflowName}</div>
        </div>
        <span className={`mission-wall-pill mission-wall-pill--${tone}`}>
          {formatRunStatus(run.status)}
        </span>
      </div>
      <div className="mission-wall-run-card__stage">{formatRunStage(run)}</div>
      {hasRuntimeRun ? (
        <>
          <div className="mission-wall-run-card__progress-row">
            <span className="mission-wall-run-card__progress-label">
              {t(
                "pages.missionwall.runProgress",
                "{completed} / {total} steps",
                {
                  completed: String(completedSteps),
                  total: String(totalSteps),
                },
              )}
            </span>
            <span className="mission-wall-run-card__duration">
              {formatDuration(run.durationMs)}
            </span>
          </div>
          <div className="mission-wall-progress" aria-hidden="true">
            <div
              className={`mission-wall-progress__bar mission-wall-progress__bar--${tone}`}
              style={{ width: `${progressPercent(run)}%` }}
            />
          </div>
        </>
      ) : (
        <div className="mission-wall-run-card__progress-row mission-wall-run-card__progress-row--single">
          <span className="mission-wall-run-card__progress-label">
            {t(
              "pages.missionwall.runtimeData.noRuntimeRun",
              "No visible run",
            )}
          </span>
        </div>
      )}
    </button>
  );
}
