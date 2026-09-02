import React from "react";
import type { MissionWallRun } from "../models";

export interface PublishedRunWindowState {
  readonly runs: readonly MissionWallRun[];
  readonly selectRun: (runId: string) => void;
  readonly selectedRun?: MissionWallRun;
  readonly selectedRunId?: string;
}

interface PublishedRunWindowModel {
  readonly manualSelection: boolean;
  readonly runs: readonly MissionWallRun[];
  readonly selectedRunId?: string;
}

function sameWindowRuns(
  leftRuns: readonly MissionWallRun[],
  rightRuns: readonly MissionWallRun[],
): boolean {
  if (leftRuns.length !== rightRuns.length) {
    return false;
  }

  return leftRuns.every((leftRun, index) => {
    const rightRun = rightRuns[index];
    return (
      rightRun !== undefined &&
      leftRun.runId === rightRun.runId &&
      leftRun.status === rightRun.status &&
      leftRun.updatedAt === rightRun.updatedAt &&
      leftRun.durationMs === rightRun.durationMs &&
      leftRun.progress?.completedSteps === rightRun.progress?.completedSteps &&
      leftRun.progress?.totalSteps === rightRun.progress?.totalSteps &&
      leftRun.currentStepId === rightRun.currentStepId &&
      leftRun.currentStepLabel === rightRun.currentStepLabel &&
      leftRun.stateVersion === rightRun.stateVersion &&
      leftRun.lastEventId === rightRun.lastEventId
    );
  });
}

function isLiveRun(run: MissionWallRun | undefined): boolean {
  return (
    run?.status === "running" ||
    run?.status === "waiting" ||
    run?.status === "retrying"
  );
}

export function mergePublishedRunWindowRuns(
  previousRuns: readonly MissionWallRun[],
  nextRuns: readonly MissionWallRun[],
): MissionWallRun[] {
  const nextRunById = new Map(nextRuns.map((run) => [run.runId, run]));
  const previousRunIds = new Set(previousRuns.map((run) => run.runId));
  const newRuns = nextRuns.filter((run) => !previousRunIds.has(run.runId));
  const retainedRuns = previousRuns
    .map((run) => nextRunById.get(run.runId))
    .filter((run): run is MissionWallRun => Boolean(run));

  return [...newRuns, ...retainedRuns];
}

export function reducePublishedRunWindowModel(
  previousModel: PublishedRunWindowModel,
  nextRuns: readonly MissionWallRun[],
  preferredRunId?: string,
): PublishedRunWindowModel {
  const previousRunIds = new Set(previousModel.runs.map((run) => run.runId));
  const nextWindowRuns = mergePublishedRunWindowRuns(
    previousModel.runs,
    nextRuns,
  );
  const newlyAddedRun = nextRuns.find(
    (run) => run.hasRuntimeRun !== false && !previousRunIds.has(run.runId),
  );
  const selectedRunStillVisible =
    previousModel.selectedRunId &&
    nextWindowRuns.some((run) => run.runId === previousModel.selectedRunId);
  const selectedRun = previousModel.selectedRunId
    ? nextWindowRuns.find((run) => run.runId === previousModel.selectedRunId)
    : undefined;
  const preferredRun = preferredRunId
    ? nextWindowRuns.find((run) => run.runId === preferredRunId)
    : undefined;
  const firstLiveRun = nextWindowRuns.find(isLiveRun);
  const manualSelection =
    previousModel.manualSelection && Boolean(selectedRunStillVisible);

  const nextModel = {
    manualSelection,
    runs: nextWindowRuns,
    selectedRunId:
      (manualSelection ? previousModel.selectedRunId : undefined) ??
      preferredRun?.runId ??
      (selectedRunStillVisible && isLiveRun(selectedRun)
        ? previousModel.selectedRunId
        : undefined) ??
      firstLiveRun?.runId ??
      newlyAddedRun?.runId ??
      (selectedRunStillVisible ? previousModel.selectedRunId : undefined) ??
      nextWindowRuns[0]?.runId,
  };

  if (
    previousModel.manualSelection === nextModel.manualSelection &&
    previousModel.selectedRunId === nextModel.selectedRunId &&
    sameWindowRuns(previousModel.runs, nextModel.runs)
  ) {
    return previousModel;
  }

  return nextModel;
}

export function usePublishedRunWindow(
  runs: readonly MissionWallRun[],
  initialSelectedRunId?: string,
): PublishedRunWindowState {
  const [model, setModel] = React.useState<PublishedRunWindowModel>(() => ({
    manualSelection: false,
    runs,
    selectedRunId: initialSelectedRunId ?? runs[0]?.runId,
  }));

  React.useEffect(() => {
    setModel((previousModel) =>
      reducePublishedRunWindowModel(previousModel, runs, initialSelectedRunId),
    );
  }, [initialSelectedRunId, runs]);

  const selectedRun =
    model.runs.find((run) => run.runId === model.selectedRunId) ??
    model.runs[0];

  return {
    runs: model.runs,
    selectRun: (runId) => {
      setModel((previousModel) => ({
        ...previousModel,
        manualSelection: true,
        selectedRunId: runId,
      }));
    },
    selectedRun,
    selectedRunId: selectedRun?.runId,
  };
}
