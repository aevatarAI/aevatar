import React from "react";
import { t } from "@/shared/i18n/messages";
import type { MissionWallRun } from "../models";
import { PublishedRunCard } from "./PublishedRunCard";

export function PublishedRunWindow({
  focusRunId,
  onSelectRun,
  runs,
}: {
  readonly focusRunId?: string;
  readonly onSelectRun: (runId: string) => void;
  readonly runs: readonly MissionWallRun[];
}) {
  return (
    <aside
      aria-label={t(
        "pages.missionwall.publishedRunWindowAria",
        "Published run window",
      )}
      className="mission-wall-panel mission-wall-run-window"
    >
      <div className="mission-wall-panel-head">
        <div className="mission-wall-panel-title">
          {t("pages.missionwall.publishedRunWindow", "Published Run Window")}
        </div>
        <div className="mission-wall-panel-count">{runs.length}</div>
      </div>
      <div
        className="mission-wall-run-window__viewport"
        data-testid="mission-wall-run-window-viewport"
      >
        <div
          className="mission-wall-run-list"
          data-testid="mission-wall-run-list"
        >
          {runs.map((run) => (
            <PublishedRunCard
              focus={run.runId === focusRunId}
              key={run.runId}
              onSelect={onSelectRun}
              run={run}
            />
          ))}
        </div>
      </div>
    </aside>
  );
}
