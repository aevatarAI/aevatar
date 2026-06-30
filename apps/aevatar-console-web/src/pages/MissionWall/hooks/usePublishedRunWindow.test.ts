import type { MissionWallRun } from "../models";
import {
  mergePublishedRunWindowRuns,
  reducePublishedRunWindowModel,
} from "./usePublishedRunWindow";

function run(runId: string): MissionWallRun {
  return {
    focusPriority: 500,
    id: runId,
    priorityLevel: "none",
    runId,
    status: "running",
    visibilityReason: "running",
    workflowName: runId,
  };
}

describe("Published Run Window state", () => {
  it("adds newly observed runs to the top without moving existing runs", () => {
    const first = run("run-first");
    const second = run("run-second");
    const third = run("run-third");

    expect(
      mergePublishedRunWindowRuns([first, second], [third, first, second]).map(
        (item) => item.runId,
      ),
    ).toEqual(["run-third", "run-first", "run-second"]);
  });

  it("keeps the current selection when no new run arrives", () => {
    const first = run("run-first");
    const second = run("run-second");

    const model = reducePublishedRunWindowModel(
      {
        runs: [first, second],
        selectedRunId: "run-second",
      },
      [first, second],
    );

    expect(model.runs.map((item) => item.runId)).toEqual([
      "run-first",
      "run-second",
    ]);
    expect(model.selectedRunId).toBe("run-second");
  });

  it("selects a newly observed run when it appears at the top", () => {
    const first = run("run-first");
    const second = run("run-second");
    const third = run("run-third");

    const model = reducePublishedRunWindowModel(
      {
        runs: [first, second],
        selectedRunId: "run-second",
      },
      [third, first, second],
    );

    expect(model.runs.map((item) => item.runId)).toEqual([
      "run-third",
      "run-first",
      "run-second",
    ]);
    expect(model.selectedRunId).toBe("run-third");
  });
});
