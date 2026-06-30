import React from "react";
import { t } from "@/shared/i18n/messages";
import type { MissionWallRun, MissionWallSnapshot } from "../models";
import { WorkflowReplayCanvas } from "./WorkflowReplayCanvas";

export function MissionStage({
  focusRun,
  isAuditLoading,
  isRuntimeLoading,
  snapshot,
}: {
  readonly focusRun?: MissionWallRun;
  readonly isAuditLoading?: boolean;
  readonly isRuntimeLoading?: boolean;
  readonly snapshot: MissionWallSnapshot;
}) {
  const graph = snapshot.topology.workflowGraph;
  const graphHasNodes = Boolean(graph?.nodes.length);
  const selectedPublishedWorkflowWithoutRun =
    focusRun?.hasRuntimeRun === false ||
    focusRun?.visibilityReason === "published_workflow";

  return (
    <section className="mission-wall-panel mission-wall-stage">
      <header className="mission-wall-stage-head">
        <div style={{ minWidth: 0 }}>
          <h2 className="mission-wall-stage-title">
            {focusRun
              ? t(
                  "pages.missionwall.stageTitle",
                  "{workflowName} · Step Flow",
                  { workflowName: focusRun.workflowName },
                )
              : t("pages.missionwall.noFocusRun", "No focus run")}
          </h2>
          <div className="mission-wall-stage-subtitle">
            {focusRun
              ? t(
                  "pages.missionwall.stageSubtitle",
                  "Team {teamName} · {memberName}",
                  {
                    memberName:
                      focusRun.entryMemberName ||
                      t(
                        "pages.missionwall.unknownEntryMember",
                        "Unknown entry member",
                      ),
                    teamName:
                      focusRun.teamName ||
                      t("pages.missionwall.unknownTeam", "Unknown team"),
                  },
                )
              : t(
                  "pages.missionwall.noFocusExplain",
                  "Select a workflow.",
                )}
          </div>
        </div>
      </header>
      {isRuntimeLoading || !focusRun || !graphHasNodes ? (
        <div className="mission-wall-state-panel">
          <div className="mission-wall-state-panel__kicker">
            {isRuntimeLoading
              ? t("pages.missionwall.state.loadingKicker", "Loading runtime")
              : t("pages.missionwall.state.emptyKicker", "Waiting for runs")}
          </div>
          <div className="mission-wall-state-panel__title">
            {isRuntimeLoading
              ? t(
                  "pages.missionwall.state.loadingTitle",
                  "Loading workflow runs",
                )
              : focusRun
                ? selectedPublishedWorkflowWithoutRun
                  ? t(
                      "pages.missionwall.state.publishedWorkflowTitle",
                      "No visible run",
                    )
                  : t(
                      "pages.missionwall.state.auditPendingTitle",
                      "No step flow for this run yet",
                    )
                : t(
                    "pages.missionwall.state.emptyTitle",
                    "No published workflows are visible",
                  )}
          </div>
        </div>
      ) : (
        <>
          {isAuditLoading ? (
            <div className="mission-wall-audit-loading">
              {t(
                "pages.missionwall.state.auditLoading",
                "Loading selected run",
              )}
            </div>
          ) : null}
          <WorkflowReplayCanvas graph={graph} />
        </>
      )}
    </section>
  );
}
