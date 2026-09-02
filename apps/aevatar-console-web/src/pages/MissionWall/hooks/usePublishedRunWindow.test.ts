import type { MissionWallRun } from "../models";
import {
  mergePublishedRunWindowRuns,
  reducePublishedRunWindowModel,
} from "./usePublishedRunWindow";

function run(
  runId: string,
  overrides: Partial<MissionWallRun> = {},
): MissionWallRun {
  return {
    focusPriority: 500,
    id: runId,
    priorityLevel: "none",
    progress: {
      completedSteps: 0,
      totalSteps: 0,
    },
    runId,
    status: "running",
    visibilityReason: "running",
    workflowName: runId,
    ...overrides,
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
        manualSelection: false,
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

  it("keeps the currently selected live run when a new live run appears", () => {
    const first = run("run-first");
    const second = run("run-second");
    const third = run("run-third");

    const model = reducePublishedRunWindowModel(
      {
        manualSelection: true,
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
    expect(model.selectedRunId).toBe("run-second");
  });

  it("moves automatic focus to the preferred run when a new live run appears", () => {
    const first = run("run-first");
    const second = run("run-second");
    const third = run("run-third");

    const model = reducePublishedRunWindowModel(
      {
        manualSelection: false,
        runs: [first, second],
        selectedRunId: "run-second",
      },
      [third, first, second],
      "run-third",
    );

    expect(model.runs.map((item) => item.runId)).toEqual([
      "run-third",
      "run-first",
      "run-second",
    ]);
    expect(model.selectedRunId).toBe("run-third");
  });

  it("selects a newly observed run after the current selection is done", () => {
    const first = run("run-first");
    const second = run("run-second", {
      status: "completed",
      visibilityReason: "recently_completed",
    });
    const third = run("run-third");

    const model = reducePublishedRunWindowModel(
      {
        manualSelection: false,
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

  it("updates existing run progress without moving the manual list order", () => {
    const first = run("run-first");
    const second = run("run-second");
    const updatedFirst: MissionWallRun = {
      ...first,
      durationMs: 8200,
      progress: {
        completedSteps: 5,
        totalSteps: 5,
      },
    };

    const model = reducePublishedRunWindowModel(
      {
        manualSelection: false,
        runs: [first, second],
        selectedRunId: "run-second",
      },
      [updatedFirst, second],
    );

    expect(model.runs.map((item) => item.runId)).toEqual([
      "run-first",
      "run-second",
    ]);
    expect(model.runs[0].progress).toEqual({
      completedSteps: 5,
      totalSteps: 5,
    });
    expect(model.runs[0].durationMs).toBe(8200);
    expect(model.selectedRunId).toBe("run-second");
  });
});
