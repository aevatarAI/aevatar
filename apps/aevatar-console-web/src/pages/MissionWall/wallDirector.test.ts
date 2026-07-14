import type { MissionWallRunSource, MissionWallSource } from "./models";
import { buildMissionWallSnapshot, chooseFocusRun } from "./wallDirector";

const NOW = Date.parse("2026-06-30T08:30:24Z");

function buildRun(
  overrides: Partial<MissionWallRunSource> = {},
): MissionWallRunSource {
  return {
    completedSteps: 1,
    runId: "run-alpha",
    status: "running",
    steps: [
      {
        nextStepId: "finish",
        status: "active",
        stepId: "start",
        stepType: "llm_call",
      },
      {
        status: "idle",
        stepId: "finish",
        stepType: "emit",
      },
    ],
    totalSteps: 2,
    updatedAt: "2026-06-30T08:30:10Z",
    workflowName: "Alpha Workflow",
    ...overrides,
  };
}

function buildSource(runs: readonly MissionWallRunSource[]): MissionWallSource {
  return {
    generatedAt: "2026-06-30T08:30:24Z",
    live: {
      durableFreshnessSeconds: 2,
      message: "live",
      status: "live",
    },
    runs,
  };
}

function buildSteps(count: number) {
  return Array.from({ length: count }, (_, index) => {
    const stepNumber = index + 1;
    return {
      nextStepId: stepNumber < count ? `step-${stepNumber + 1}` : undefined,
      status: index === count - 1 ? ("active" as const) : ("completed" as const),
      stepId: `step-${stepNumber}`,
      stepType: "llm_call" as const,
    };
  });
}

describe("Mission Wall director", () => {
  it("keeps published workflow entries after the focus retention window", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          runId: "run-live",
          status: "running",
          updatedAt: "2026-06-30T08:30:10Z",
        }),
        buildRun({
          runId: "run-recent",
          status: "completed",
          updatedAt: "2026-06-30T08:27:10Z",
        }),
        buildRun({
          runId: "run-old-complete",
          status: "completed",
          updatedAt: "2026-06-30T08:10:10Z",
        }),
        buildRun({
          runId: "run-failed",
          status: "failed",
          updatedAt: "2026-06-30T08:29:10Z",
        }),
      ]),
      { nowMs: NOW },
    );

    expect(snapshot.runs.map((run) => run.runId)).toEqual([
      "run-live",
      "run-failed",
      "run-recent",
      "run-old-complete",
    ]);
    expect(snapshot.runs.at(-1)?.visibilityReason).toBe("published_workflow");
    expect(snapshot.summary.wallVisibleRuns).toBe(4);
    expect(snapshot.summary.failedRuns).toBe(1);
    expect(snapshot.summary.recentlyCompletedRuns).toBe(1);
  });

  it("does not auto-focus a published workflow entry without a current run", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          hasRuntimeRun: false,
          runId: "published:svc-idle",
          status: "unknown",
          updatedAt: "2026-06-30T08:30:18Z",
        }),
      ]),
      { nowMs: NOW },
    );

    expect(snapshot.runs).toHaveLength(1);
    expect(snapshot.runs[0].visibilityReason).toBe("published_workflow");
    expect(snapshot.focus.runId).toBeUndefined();
    expect(snapshot.topology.workflowGraph?.nodes).toHaveLength(0);
  });

  it("chooses failed runs before waiting and running runs", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          runId: "run-waiting",
          status: "waiting",
          updatedAt: "2026-06-30T08:30:18Z",
        }),
        buildRun({
          runId: "run-running",
          status: "running",
          updatedAt: "2026-06-30T08:30:21Z",
        }),
        buildRun({
          runId: "run-failed",
          status: "failed",
          updatedAt: "2026-06-30T08:30:12Z",
        }),
      ]),
      { nowMs: NOW },
    );

    expect(snapshot.focus.runId).toBe("run-failed");
    expect(snapshot.focus.reason).toBe("failed");
  });

  it("honors a manual focus run override without changing visibility rules", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          runId: "run-waiting",
          status: "waiting",
          updatedAt: "2026-06-30T08:30:18Z",
        }),
        buildRun({
          runId: "run-failed",
          status: "failed",
          updatedAt: "2026-06-30T08:30:12Z",
        }),
      ]),
      {
        focusRunId: "run-waiting",
        nowMs: NOW,
      },
    );

    expect(snapshot.focus.runId).toBe("run-waiting");
    expect(snapshot.topology.selectedRunId).toBe("run-waiting");
  });

  it("chooses the highest-priority run for initial selection", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          runId: "run-waiting",
          status: "waiting",
          updatedAt: "2026-06-30T08:30:18Z",
        }),
        buildRun({
          runId: "run-running",
          status: "running",
          updatedAt: "2026-06-30T08:30:21Z",
        }),
      ]),
      { nowMs: NOW },
    );

    const selected = chooseFocusRun(snapshot.runs);

    expect(selected?.runId).toBe("run-waiting");
    expect(snapshot.focus.selectedAt).toBe("2026-06-30T08:30:24.000Z");
  });

  it("shows all workflow nodes when the workflow fits in the graph window", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          currentStepId: "step-5",
          steps: buildSteps(5),
          totalSteps: 5,
        }),
      ]),
      { nowMs: NOW },
    );

    const graph = snapshot.topology.workflowGraph;

    expect(graph?.nodes.map((node) => node.stepId)).toEqual([
      "step-1",
      "step-2",
      "step-3",
      "step-4",
      "step-5",
    ]);
    expect(graph?.layout?.windowStartIndex).toBe(0);
    expect(graph?.layout?.windowEndIndex).toBe(4);
  });

  it("connects audit steps in execution order when the audit omits explicit next links", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          currentStepId: "step-3",
          steps: buildSteps(5).map(({ nextStepId: _nextStepId, ...step }) => step),
          totalSteps: 5,
        }),
      ]),
      { nowMs: NOW },
    );

    expect(snapshot.topology.workflowGraph?.edges.map((edge) => [
      edge.fromStepId,
      edge.toStepId,
    ])).toEqual([
      ["step-1", "step-2"],
      ["step-2", "step-3"],
      ["step-3", "step-4"],
      ["step-4", "step-5"],
    ]);
  });

  it("does not mark completed run edges as live flow", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          completedSteps: 5,
          currentStepId: "step-5",
          status: "completed",
          steps: buildSteps(5).map((step) => ({
            ...step,
            status: "completed" as const,
          })),
          totalSteps: 5,
        }),
      ]),
      { nowMs: NOW },
    );

    const edges = snapshot.topology.workflowGraph?.edges ?? [];

    expect(edges).toHaveLength(4);
    expect(edges.every((edge) => edge.traversed === true)).toBe(true);
    expect(edges.every((edge) => edge.focused === false)).toBe(true);
  });

  it("keeps all workflow nodes while marking the focused big-screen window", () => {
    const snapshot = buildMissionWallSnapshot(
      buildSource([
        buildRun({
          currentStepId: "step-7",
          steps: buildSteps(7),
          totalSteps: 7,
        }),
      ]),
      { nowMs: NOW },
    );

    const graph = snapshot.topology.workflowGraph;

    expect(graph?.nodes.map((node) => node.stepId)).toEqual([
      "step-1",
      "step-2",
      "step-3",
      "step-4",
      "step-5",
      "step-6",
      "step-7",
    ]);
    expect(graph?.layout?.viewportStepIds).toEqual([
      "step-3",
      "step-4",
      "step-5",
      "step-6",
      "step-7",
    ]);
    expect(graph?.layout?.windowStartIndex).toBe(2);
    expect(graph?.layout?.windowEndIndex).toBe(6);
  });
});
