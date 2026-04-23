import { AGUIEventType, CustomEventName, type AGUIEvent } from "@aevatar-react-sdk/types";
import {
  applyRuntimeEvent,
  createRuntimeEventAccumulator,
} from "./runtimeEventSemantics";

describe("runtimeEventSemantics", () => {
  it("keeps run-finished output ahead of later completed step output", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_FINISHED,
        result: {
          output: "final run answer",
        },
        runId: "run-1",
        threadId: "thread-1",
      },
      {
        type: AGUIEventType.CUSTOM,
        name: CustomEventName.StepCompleted,
        value: {
          runId: "run-1",
          stepId: "late-step",
          success: true,
          output: "late step output",
        },
      },
    ];

    events.forEach((event) => applyRuntimeEvent(accumulator, event));

    expect(accumulator.finalOutput).toBe("final run answer");
  });

  it("allows run-finished output to replace earlier step output", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.CUSTOM,
        name: CustomEventName.StepCompleted,
        value: {
          runId: "run-1",
          stepId: "first-step",
          success: true,
          output: "first step output",
        },
      },
      {
        type: AGUIEventType.RUN_FINISHED,
        result: {
          output: "final run answer",
        },
        runId: "run-1",
        threadId: "thread-1",
      },
    ];

    events.forEach((event) => applyRuntimeEvent(accumulator, event));

    expect(accumulator.finalOutput).toBe("final run answer");
  });
});
