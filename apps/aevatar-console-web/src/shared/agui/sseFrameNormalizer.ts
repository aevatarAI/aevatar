import { AGUIEventType, type AGUIEvent } from "@aevatar-react-sdk/types";

type JsonRecord = Record<string, unknown>;

type RuntimeEventType =
  | AGUIEventType
  | "RUN_STOPPED"
  | "TOOL_APPROVAL_REQUEST";

const ONEOF_KEY_MAP: Record<string, RuntimeEventType> = {
  custom: AGUIEventType.CUSTOM,
  humanInputRequest: AGUIEventType.HUMAN_INPUT_REQUEST,
  runError: AGUIEventType.RUN_ERROR,
  runFinished: AGUIEventType.RUN_FINISHED,
  runStarted: AGUIEventType.RUN_STARTED,
  runStopped: "RUN_STOPPED",
  stateSnapshot: AGUIEventType.STATE_SNAPSHOT,
  stepFinished: AGUIEventType.STEP_FINISHED,
  stepStarted: AGUIEventType.STEP_STARTED,
  textMessageContent: AGUIEventType.TEXT_MESSAGE_CONTENT,
  textMessageEnd: AGUIEventType.TEXT_MESSAGE_END,
  textMessageStart: AGUIEventType.TEXT_MESSAGE_START,
  toolApprovalRequest: "TOOL_APPROVAL_REQUEST",
  toolCallEnd: AGUIEventType.TOOL_CALL_END,
  toolCallStart: AGUIEventType.TOOL_CALL_START,
};

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }

  return value as JsonRecord;
}

function readString(record: JsonRecord | undefined, ...keys: string[]): string {
  if (!record) {
    return "";
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string") {
      return value;
    }
  }

  return "";
}

function readBoolean(record: JsonRecord | undefined, ...keys: string[]): boolean {
  if (!record) {
    return false;
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "boolean") {
      return value;
    }
  }

  return false;
}

function readNumber(
  record: JsonRecord | undefined,
  fallback: number,
  ...keys: string[]
): number {
  if (!record) {
    return fallback;
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "number" && Number.isFinite(value)) {
      return value;
    }
  }

  return fallback;
}

function createTypedEvent(
  type: RuntimeEventType,
  timestamp: number,
  payload: JsonRecord
): AGUIEvent {
  return {
    ...payload,
    timestamp,
    type,
  } as unknown as AGUIEvent;
}

/**
 * Convert backend SSE frames into the flat AGUI-style shape expected by the UI.
 *
 * Backend may send either:
 * 1. oneof-style frames: { runError: { message: "..." }, timestamp: 1 }
 * 2. typed+nested frames: { type: "RUN_ERROR", runError: { message: "..." } }
 * 3. already-flat frames: { type: "RUN_ERROR", message: "..." }
 */
export function normalizeBackendSseFrame(raw: unknown): AGUIEvent | null {
  const frame = asRecord(raw);
  if (!frame) {
    return null;
  }

  const rawTimestamp = frame.timestamp;
  const timestamp =
    typeof rawTimestamp === "number"
      ? rawTimestamp
      : Number(rawTimestamp) || Date.now();

  for (const [oneofKey, eventType] of Object.entries(ONEOF_KEY_MAP)) {
    if (!(oneofKey in frame)) {
      continue;
    }

    const nested = asRecord(frame[oneofKey]);
    switch (eventType) {
      case AGUIEventType.RUN_STARTED:
        return createTypedEvent(eventType, timestamp, {
          actorId:
            readString(nested, "actorId") ||
            readString(frame, "actorId", "threadId"),
          runId: readString(nested, "runId") || readString(frame, "runId"),
          threadId:
            readString(nested, "threadId", "actorId") ||
            readString(frame, "threadId", "actorId"),
        });
      case AGUIEventType.RUN_FINISHED:
        return createTypedEvent(eventType, timestamp, {
          result: nested?.result,
          runId: readString(nested, "runId") || readString(frame, "runId"),
          threadId:
            readString(nested, "threadId", "actorId") ||
            readString(frame, "threadId", "actorId"),
        });
      case AGUIEventType.RUN_ERROR:
        return createTypedEvent(eventType, timestamp, {
          code: readString(nested, "code") || undefined,
          message: readString(nested, "message"),
          runId: readString(nested, "runId") || undefined,
        });
      case "RUN_STOPPED":
        return createTypedEvent(eventType, timestamp, {
          reason: readString(nested, "reason"),
          runId: readString(nested, "runId"),
        });
      case AGUIEventType.TEXT_MESSAGE_START:
        return createTypedEvent(eventType, timestamp, {
          messageId: readString(nested, "messageId"),
          role: readString(nested, "role"),
        });
      case AGUIEventType.TEXT_MESSAGE_CONTENT:
        return createTypedEvent(eventType, timestamp, {
          delta: readString(nested, "delta"),
          messageId: readString(nested, "messageId"),
        });
      case AGUIEventType.TEXT_MESSAGE_END:
        return createTypedEvent(eventType, timestamp, {
          messageId: readString(nested, "messageId"),
        });
      case AGUIEventType.STEP_STARTED:
        return createTypedEvent(eventType, timestamp, {
          stepName: readString(nested, "stepName"),
        });
      case AGUIEventType.STEP_FINISHED:
        return createTypedEvent(eventType, timestamp, {
          stepName: readString(nested, "stepName"),
        });
      case AGUIEventType.TOOL_CALL_START:
        return createTypedEvent(eventType, timestamp, {
          toolCallId: readString(nested, "toolCallId"),
          toolName: readString(nested, "toolName"),
        });
      case AGUIEventType.TOOL_CALL_END:
        return createTypedEvent(eventType, timestamp, {
          result: readString(nested, "result"),
          toolCallId: readString(nested, "toolCallId"),
        });
      case "TOOL_APPROVAL_REQUEST":
        return createTypedEvent(eventType, timestamp, {
          argumentsJson: readString(nested, "argumentsJson", "arguments_json"),
          isDestructive: readBoolean(
            nested,
            "isDestructive",
            "is_destructive"
          ),
          requestId: readString(nested, "requestId", "request_id"),
          timeoutSeconds: readNumber(
            nested,
            15,
            "timeoutSeconds",
            "timeout_seconds"
          ),
          toolCallId: readString(nested, "toolCallId", "tool_call_id"),
          toolName: readString(nested, "toolName", "tool_name"),
        });
      case AGUIEventType.HUMAN_INPUT_REQUEST:
        return createTypedEvent(eventType, timestamp, {
          metadata: nested?.metadata as Record<string, string> | undefined,
          prompt: readString(nested, "prompt"),
          runId: readString(nested, "runId"),
          stepId: readString(nested, "stepId"),
          suspensionType: readString(nested, "suspensionType"),
          timeoutSeconds: readNumber(nested, 0, "timeoutSeconds"),
        });
      case AGUIEventType.CUSTOM:
        return createTypedEvent(eventType, timestamp, {
          name: readString(nested, "name"),
          payload: nested?.payload,
          value: nested?.payload ?? nested?.value,
        });
      case AGUIEventType.STATE_SNAPSHOT:
        return createTypedEvent(eventType, timestamp, {
          snapshot: frame[oneofKey],
        });
      default:
        return null;
    }
  }

  if (typeof frame.type === "string") {
    return { ...frame, timestamp } as unknown as AGUIEvent;
  }

  return null;
}

/**
 * Parse a backend SSE response into normalized AGUIEvent objects.
 * Unlike the SDK parser, this understands backend protobuf-JSON oneof frames.
 */
export async function* parseBackendSSEStream(
  response: Response,
  options?: { signal?: AbortSignal }
): AsyncGenerator<AGUIEvent, void, undefined> {
  if (!response.ok) {
    const text = await response.text().catch(() => "");
    throw new Error(
      `SSE request failed: HTTP ${response.status} — ${text || response.statusText}`
    );
  }

  const body = response.body;
  if (!body) {
    throw new Error("SSE response has no readable body.");
  }

  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  const dataLines: string[] = [];

  try {
    while (!options?.signal?.aborted) {
      const { done, value } = await reader.read();
      if (done) {
        buffer += "\n";
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split("\n");
      buffer = done ? "" : (lines.pop() ?? "");

      for (const line of lines) {
        if (line === "" || line === "\r") {
          if (dataLines.length > 0) {
            const data = dataLines.splice(0, dataLines.length).join("\n").trim();
            if (data && data !== "[DONE]") {
              try {
                const parsed = JSON.parse(data);
                const event = normalizeBackendSseFrame(parsed);
                if (event) {
                  yield event;
                }
              } catch {
                // Skip malformed JSON frames.
              }
            }
          }
          continue;
        }

        if (line.startsWith("data:")) {
          const payload = line.length > 5 ? line.slice(5) : "";
          dataLines.push(payload.startsWith(" ") ? payload.slice(1) : payload);
        }
      }

      if (done) {
        if (dataLines.length > 0) {
          const data = dataLines.splice(0, dataLines.length).join("\n").trim();
          if (data && data !== "[DONE]") {
            try {
              const parsed = JSON.parse(data);
              const event = normalizeBackendSseFrame(parsed);
              if (event) {
                yield event;
              }
            } catch {
              // Skip malformed JSON frames.
            }
          }
        }
        break;
      }
    }
  } finally {
    reader.releaseLock();
  }
}
