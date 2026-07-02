import React from "react";
import { MissionStage } from "./components/MissionStage";
import { PublishedRunWindow } from "./components/PublishedRunWindow";
import { TopStatusStrip } from "./components/TopStatusStrip";
import { useMissionWallRuntimeData } from "./hooks/useMissionWallData";
import { usePublishedRunWindow } from "./hooks/usePublishedRunWindow";
import { missionWallStyles } from "./missionWallStyles";
import { buildMissionWallSnapshot } from "./wallDirector";

const MissionWallPage: React.FC = () => {
  const runtimeData = useMissionWallRuntimeData();
  const {
    buildSource,
    isLoading,
    nowMs,
    routeFocusRunId,
  } = runtimeData;
  const missionWallSource = React.useMemo(
    () => buildSource(),
    [buildSource],
  );
  const snapshot = React.useMemo(
    () =>
      buildMissionWallSnapshot(missionWallSource, {
        focusRunId: routeFocusRunId,
        nowMs,
      }),
    [missionWallSource, nowMs, routeFocusRunId],
  );
  const publishedRunWindow = usePublishedRunWindow(
    snapshot.runs,
    snapshot.focus.runId,
  );
  const focusSnapshot = React.useMemo(
    () =>
      buildMissionWallSnapshot(missionWallSource, {
        focusRunId: publishedRunWindow.selectedRunId,
        nowMs,
      }),
    [missionWallSource, nowMs, publishedRunWindow.selectedRunId],
  );
  const focusRun =
    focusSnapshot.runs.find(
      (run) => run.runId === publishedRunWindow.selectedRunId,
    ) ?? publishedRunWindow.selectedRun;
  const publishedRuns = React.useMemo(
    () =>
      publishedRunWindow.runs.map((run) =>
        run.runId === focusRun?.runId ? focusRun : run,
      ),
    [focusRun, publishedRunWindow.runs],
  );

  return (
    <main className="mission-wall">
      <style>{missionWallStyles}</style>
      <TopStatusStrip snapshot={snapshot} />
      <section className="mission-wall-screen">
        <PublishedRunWindow
          focusRunId={publishedRunWindow.selectedRunId}
          onSelectRun={publishedRunWindow.selectRun}
          runs={publishedRuns}
        />
        <MissionStage
          focusRun={focusRun}
          isRuntimeLoading={isLoading}
          snapshot={focusSnapshot}
        />
      </section>
    </main>
  );
};

export default MissionWallPage;
