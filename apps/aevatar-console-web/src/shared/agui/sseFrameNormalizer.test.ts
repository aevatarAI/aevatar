import { AGUIEventType } from "@aevatar-react-sdk/types";
import { normalizeBackendSseFrame } from "./sseFrameNormalizer";

describe("sseFrameNormalizer", () => {
  it("normalizes tool call frames emitted in backend oneof format", () => {
    expect(
      normalizeBackendSseFrame({
        timestamp: 1,
        toolCallStart: {
          toolCallId: "tool-1",
          toolName: "knowledge.search",
        },
      })
    ).toEqual({
      timestamp: 1,
      toolCallId: "tool-1",
      toolName: "knowledge.search",
      type: AGUIEventType.TOOL_CALL_START,
    });
    expect(
      normalizeBackendSseFrame({
        timestamp: 2,
        toolCallEnd: {
          result: "3 matches",
          toolCallId: "tool-1",
        },
      })
    ).toEqual({
      result: "3 matches",
      timestamp: 2,
      toolCallId: "tool-1",
      type: AGUIEventType.TOOL_CALL_END,
    });
  });

  it("flattens typed tool approval frames that keep payload in a nested object", () => {
    expect(
      normalizeBackendSseFrame({
        timestamp: 3,
        toolApprovalRequest: {
          argumentsJson: "{\"scopeId\":\"scope-a\"}",
          isDestructive: true,
          requestId: "approval-1",
          timeoutSeconds: 30,
          toolCallId: "tool-7",
          toolName: "scope.bind",
        },
        type: "TOOL_APPROVAL_REQUEST",
      })
    ).toEqual({
      argumentsJson: "{\"scopeId\":\"scope-a\"}",
      isDestructive: true,
      requestId: "approval-1",
      timeoutSeconds: 30,
      timestamp: 3,
      toolCallId: "tool-7",
      toolName: "scope.bind",
      type: "TOOL_APPROVAL_REQUEST",
    });
  });

  it("keeps final assistant text from textMessageEnd frames", () => {
    expect(
      normalizeBackendSseFrame({
        textMessageEnd: {
          delta: "final delta",
          message: "final message",
          messageId: "msg-1",
        },
        timestamp: 4,
      })
    ).toEqual({
      delta: "final delta",
      message: "final message",
      messageId: "msg-1",
      timestamp: 4,
      type: AGUIEventType.TEXT_MESSAGE_END,
    });
  });

  it("extracts run identifiers from nested backend frames", () => {
    expect(
      normalizeBackendSseFrame({
        runStarted: {
          actorId: "actor-1",
          commandId: "cmd-1",
          correlationId: "corr-1",
          runId: "run-1",
        },
        timestamp: 5,
      })
    ).toEqual({
      actorId: "actor-1",
      commandId: "cmd-1",
      correlationId: "corr-1",
      runId: "run-1",
      threadId: "actor-1",
      timestamp: 5,
      type: AGUIEventType.RUN_STARTED,
    });

    expect(
      normalizeBackendSseFrame({
        runFinished: {
          command_id: "cmd-2",
          correlation_id: "corr-2",
          result: {
            output: "complete",
          },
          runId: "run-2",
          threadId: "actor-2",
        },
        timestamp: 6,
      })
    ).toEqual({
      commandId: "cmd-2",
      correlationId: "corr-2",
      result: {
        output: "complete",
      },
      runId: "run-2",
      threadId: "actor-2",
      timestamp: 6,
      type: AGUIEventType.RUN_FINISHED,
    });
  });

  it("extracts run identifiers and error code from flat typed backend frames", () => {
    expect(
      normalizeBackendSseFrame({
        code: "ERR_RUNTIME",
        commandId: "cmd-1",
        correlationId: "corr-1",
        message: "failed",
        runId: "run-1",
        timestamp: 7,
        type: AGUIEventType.RUN_ERROR,
      })
    ).toEqual({
      code: "ERR_RUNTIME",
      commandId: "cmd-1",
      correlationId: "corr-1",
      message: "failed",
      runId: "run-1",
      timestamp: 7,
      type: AGUIEventType.RUN_ERROR,
    });
  });
});
