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

  it("keeps workflow waiting signal from start result after chat run finishes", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_STARTED,
        actorId: "chat-actor-1",
        runId: "chat-run-1",
        threadId: "chat-actor-1",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.TOOL_CALL_START,
        toolCallId: "tool-1",
        toolName: "aevatar_start_workflow",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.TOOL_CALL_END,
        toolCallId: "tool-1",
        result: JSON.stringify({
          run_id: "workflow-run-1",
          status: "waiting_for_signal",
          result: {
            waiting_signal: {
              run_id: "workflow-run-1",
              step_id: "wait_for_user_choice_timeout",
              signal_name: "dinner_date_user_choice",
              prompt: "Choose one dinner option.",
              timeout_ms: 10000,
            },
          },
        }),
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.RUN_FINISHED,
        result: { output: "" },
        runId: "chat-run-1",
      } as unknown as AGUIEvent,
    ];

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

    expect(accumulator.pendingRunIntervention).toMatchObject({
      actorId: "workflow-run-1",
      kind: "wait_signal",
      runId: "workflow-run-1",
      stepId: "wait_for_user_choice_timeout",
      signalName: "dinner_date_user_choice",
      prompt: "Choose one dinner option.",
      timeoutSeconds: 10,
    });
  });

  it("keeps workflow waiting signal from artifact result after chat run finishes", () => {
    const accumulator = createRuntimeEventAccumulator();
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_STARTED,
        actorId: "chat-actor-1",
        runId: "chat-run-1",
        threadId: "chat-actor-1",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.TOOL_CALL_START,
        toolCallId: "tool-1",
        toolName: "aevatar_read_workflow_run_artifact",
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.TOOL_CALL_END,
        toolCallId: "tool-1",
        result: JSON.stringify({
          workflow_run_id: "workflow-run-1",
          artifact: "report",
          status: "pending",
          pending: true,
          waiting_signal: {
            run_id: "workflow-run-1",
            step_id: "wait_for_user_choice_timeout",
            signal_name: "dinner_date_user_choice",
            prompt: "Choose one dinner option.",
            timeout_ms: 10000,
          },
        }),
      } as unknown as AGUIEvent,
      {
        type: AGUIEventType.RUN_FINISHED,
        result: { output: "" },
        runId: "chat-run-1",
      } as unknown as AGUIEvent,
    ];

    events.forEach((event) => {
      applyRuntimeEvent(accumulator, event);
    });

    expect(accumulator.pendingRunIntervention).toMatchObject({
      actorId: "workflow-run-1",
      kind: "wait_signal",
      runId: "workflow-run-1",
      stepId: "wait_for_user_choice_timeout",
      signalName: "dinner_date_user_choice",
      prompt: "Choose one dinner option.",
      timeoutSeconds: 10,
    });
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
