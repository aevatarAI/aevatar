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
});
