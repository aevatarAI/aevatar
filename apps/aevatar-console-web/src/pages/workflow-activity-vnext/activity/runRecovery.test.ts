import { resolveRunRecovery } from "./runRecovery";

describe("resolveRunRecovery", () => {
  it("enables retry only for one explicit failed step and run again for an explicit graph root step", () => {
    expect(
      resolveRunRecovery(
        [
          { stepId: "step-second", success: false },
          { stepId: "step-first", success: true },
        ],
        {
          rootNodeId: "node-root",
          nodes: [
            { nodeId: "node-root", stepId: "step-first" },
            { nodeId: "node-second", stepId: "step-second" },
          ],
        },
      ),
    ).toEqual({ retryStepId: "step-second", runAgainStepId: "step-first" });
  });

  it("does not guess a failed step or first step from array order", () => {
    expect(
      resolveRunRecovery([
        { stepId: "step-first", success: false },
        { stepId: "step-second", success: false },
      ]),
    ).toEqual({ retryStepId: null, runAgainStepId: null });
  });

  it("does not enable run again when the graph root is missing or lacks an explicit step id", () => {
    const steps = [{ stepId: "step-first", success: true }] as const;

    expect(
      resolveRunRecovery(steps, {
        rootNodeId: "node-missing",
        nodes: [{ nodeId: "node-first", stepId: "step-first" }],
      }),
    ).toEqual({ retryStepId: null, runAgainStepId: null });
    expect(
      resolveRunRecovery(steps, {
        rootNodeId: "node-root",
        nodes: [{ nodeId: "node-root", stepId: "" }],
      }),
    ).toEqual({ retryStepId: null, runAgainStepId: null });
  });

  it("does not invent a first step for an empty run", () => {
    expect(resolveRunRecovery([])).toEqual({ retryStepId: null, runAgainStepId: null });
  });
});
