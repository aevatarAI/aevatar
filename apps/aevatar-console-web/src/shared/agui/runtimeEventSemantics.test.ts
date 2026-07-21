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

  it("snapshots tool display identity while retaining the invocation protocol name", () => {
    const accumulator = createRuntimeEventAccumulator();
    const presentation = {
      availability: "available",
      description: "Gets one repository.",
      displayName: "Work GitHub - Get repository",
      invocationName: "nyxid_api-github-work__get_repository",
      kind: "nyxIdOperation",
      sourceRef: {
        nyxIdOperation: {
          catalogServiceSlug: "github",
          connectedServiceId: "connected-service-github",
          connectionLabel: "Work GitHub",
          connectorDisplayName: "GitHub",
          serviceSlug: "api-github-work",
        },
        type: "nyxIdOperation",
      },
    };

    applyRuntimeEvent(accumulator, {
      presentation,
      timestamp: 1,
      toolCallId: "call-1",
      toolName: "nyxid_api-github-work__get_repository",
      type: AGUIEventType.TOOL_CALL_START,
    } as unknown as AGUIEvent);
    presentation.displayName = "Renamed connector";

    expect(accumulator.toolCalls).toEqual([
      expect.objectContaining({
        displayName: "Work GitHub - Get repository",
        invocationName: "nyxid_api-github-work__get_repository",
        name: "Work GitHub - Get repository",
        presentation: expect.objectContaining({
          displayName: "Work GitHub - Get repository",
        }),
      }),
    ]);
  });

  it("drops mismatched tool kind and source combinations", () => {
    const accumulator = createRuntimeEventAccumulator();

    applyRuntimeEvent(accumulator, {
      presentation: {
        availability: "available",
        displayName: "Invalid mixed identity",
        invocationName: "tool.invalid",
        kind: "nyxIdOperation",
        sourceRef: {
          builtIn: { toolId: "tool.invalid" },
          type: "builtIn",
        },
      },
      timestamp: 1,
      toolCallId: "call-invalid",
      toolName: "tool.invalid",
      type: AGUIEventType.TOOL_CALL_START,
    } as unknown as AGUIEvent);

    expect(accumulator.toolCalls[0].presentation).toEqual(
      expect.objectContaining({ kind: "generic", sourceRef: undefined })
    );
  });
});
