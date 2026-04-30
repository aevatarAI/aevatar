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

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

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

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

    expect(accumulator.finalOutput).toBe("final run answer");
  });

  it("tracks command, correlation, and error code identifiers", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_STARTED,
        actorId: "actor-1",
        commandId: "cmd-1",
        correlationId: "corr-1",
        runId: "run-1",
        threadId: "actor-1",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.RUN_ERROR,
        code: "ERR_RUNTIME",
        commandId: "cmd-1",
        correlationId: "corr-1",
        message: "failed",
        runId: "run-1",
      } as unknown as AGUIEvent,
    ];

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

    expect(accumulator.actorId).toBe("actor-1");
    expect(accumulator.commandId).toBe("cmd-1");
    expect(accumulator.correlationId).toBe("corr-1");
    expect(accumulator.errorCode).toBe("ERR_RUNTIME");
    expect(accumulator.errorText).toBe("failed");
  });

  it("keeps run-started command and correlation ids through run finish", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_STARTED,
        actorId: "actor-1",
        commandId: "cmd-1",
        correlationId: "corr-1",
        runId: "run-1",
        threadId: "actor-1",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.RUN_FINISHED,
        result: {
          output: "done",
        },
        runId: "run-1",
      } as unknown as AGUIEvent,
    ];

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

    expect(accumulator.actorId).toBe("actor-1");
    expect(accumulator.commandId).toBe("cmd-1");
    expect(accumulator.correlationId).toBe("corr-1");
    expect(accumulator.finalOutput).toBe("done");
    expect(accumulator.runId).toBe("run-1");
  });
});
