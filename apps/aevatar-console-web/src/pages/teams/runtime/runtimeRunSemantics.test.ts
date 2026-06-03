import type { ScopeServiceRunSummary } from "@/shared/models/runtime/scopeServices";
import {
  compareTeamRuns,
  formatTeamRunStatusLabel,
  isFailedTeamRun,
  isSuccessfulTeamRun,
  isWaitingTeamRun,
  normalizeTeamRunStatus,
  selectLatestTeamRun,
} from "./runtimeRunSemantics";

function buildRun(
  overrides: Partial<ScopeServiceRunSummary>,
): ScopeServiceRunSummary {
  return {
    scopeId: "scope-a",
    serviceId: "service-a",
    runId: "run-a",
    actorId: "actor://a",
    definitionActorId: "definition://a",
    revisionId: "rev-a",
    deploymentId: "dep-a",
    workflowName: "workflow-a",
    completionStatus: "completed",
    stateVersion: 1,
    lastEventId: "evt-a",
    lastUpdatedAt: "2026-04-13T09:00:00Z",
    boundAt: "2026-04-13T08:00:00Z",
    bindingUpdatedAt: "2026-04-13T08:00:00Z",
    lastSuccess: true,
    totalSteps: 2,
    completedSteps: 2,
    roleReplyCount: 1,
    lastOutput: "Done",
    lastError: "",
    ...overrides,
  };
}

describe("runtimeRunSemantics", () => {
  it("normalizes status and preserves Team runtime labels", () => {
    expect(normalizeTeamRunStatus(" Waiting_Approval ")).toBe("waiting_approval");
    expect(formatTeamRunStatusLabel("waiting_signal")).toBe("待关注");
    expect(formatTeamRunStatusLabel("failed")).toBe("异常");
    expect(formatTeamRunStatusLabel("completed")).toBe("已完成");
    expect(formatTeamRunStatusLabel(" custom_status ")).toBe("custom_status");
    expect(formatTeamRunStatusLabel("")).toBe("未知");
  });

  it("orders runs by timestamp, state version, and run id", () => {
    const runs = [
      buildRun({
        runId: "run-a",
        stateVersion: 1,
        lastUpdatedAt: "2026-04-13T09:00:00Z",
      }),
      buildRun({
        runId: "run-b",
        stateVersion: 2,
        lastUpdatedAt: "2026-04-13T09:00:00Z",
      }),
      buildRun({
        runId: "run-c",
        stateVersion: 2,
        lastUpdatedAt: "2026-04-13T09:00:00Z",
      }),
      buildRun({
        runId: "run-old",
        stateVersion: 99,
        lastUpdatedAt: "2026-04-13T08:00:00Z",
      }),
    ];

    expect([...runs].sort(compareTeamRuns).map((run) => run.runId)).toEqual([
      "run-c",
      "run-b",
      "run-a",
      "run-old",
    ]);
    expect(selectLatestTeamRun(runs)?.runId).toBe("run-c");
    expect(selectLatestTeamRun(runs, { preferredRunId: "run-a" })?.runId).toBe(
      "run-a",
    );
  });

  it("classifies success, waiting, and failed runs with waiting taking precedence", () => {
    const waitingRun = buildRun({
      completionStatus: "waiting_approval",
      lastSuccess: false,
    });
    const failedRun = buildRun({
      completionStatus: "timedout",
      lastSuccess: null,
    });
    const successfulRun = buildRun({
      completionStatus: "succeeded",
      lastSuccess: null,
    });

    expect(isWaitingTeamRun(waitingRun)).toBe(true);
    expect(isFailedTeamRun(waitingRun)).toBe(false);
    expect(isFailedTeamRun(failedRun)).toBe(true);
    expect(isSuccessfulTeamRun(successfulRun)).toBe(true);
    expect(isSuccessfulTeamRun(null)).toBe(false);
  });
});
