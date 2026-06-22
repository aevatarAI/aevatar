import { AGUIEventType } from "@aevatar-react-sdk/types";
import type { RuntimeEvent } from "@/shared/agui/runtimeEventSemantics";
import type { StudioExecutionFrame } from "./models";

function readRuntimeEventString(
  event: RuntimeEvent,
  ...keys: string[]
): string {
  const record = event as unknown as Record<string, unknown>;
  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) {
      return value;
    }
  }

  return "";
}

export function readRuntimeEventTimestamp(event: RuntimeEvent): number {
  const value = (event as unknown as { timestamp?: unknown }).timestamp;
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string") {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return Date.now();
}

export function formatRuntimeEventTimestamp(event: RuntimeEvent): string {
  const date = new Date(readRuntimeEventTimestamp(event));
  return Number.isFinite(date.getTime())
    ? date.toISOString()
    : new Date().toISOString();
}

export function serializeRuntimeEventFrame(event: RuntimeEvent): string {
  const record = event as unknown as Record<string, unknown>;
  const timestamp = readRuntimeEventTimestamp(event);

  switch (event.type) {
    case AGUIEventType.CUSTOM:
      return JSON.stringify({
        timestamp,
        custom: {
          name: readRuntimeEventString(event, "name"),
          payload: record.payload ?? record.value,
        },
      });
    case AGUIEventType.RUN_STARTED:
      return JSON.stringify({
        timestamp,
        runStarted: {
          actorId: readRuntimeEventString(event, "actorId", "threadId"),
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          runId: readRuntimeEventString(event, "runId"),
          threadId: readRuntimeEventString(event, "threadId", "actorId"),
        },
      });
    case AGUIEventType.RUN_FINISHED:
      return JSON.stringify({
        timestamp,
        runFinished: {
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          result: record.result,
          runId: readRuntimeEventString(event, "runId"),
          threadId: readRuntimeEventString(event, "threadId", "actorId"),
        },
      });
    case AGUIEventType.RUN_ERROR:
      return JSON.stringify({
        timestamp,
        runError: {
          code: readRuntimeEventString(event, "code", "errorCode", "error_code"),
          commandId: readRuntimeEventString(event, "commandId", "command_id"),
          correlationId: readRuntimeEventString(
            event,
            "correlationId",
            "correlation_id",
          ),
          message: readRuntimeEventString(event, "message"),
          runId: readRuntimeEventString(event, "runId"),
        },
      });
    case AGUIEventType.STEP_STARTED:
      return JSON.stringify({
        timestamp,
        stepStarted: {
          stepName: readRuntimeEventString(event, "stepName"),
        },
      });
    case AGUIEventType.STEP_FINISHED:
      return JSON.stringify({
        timestamp,
        stepFinished: {
          stepName: readRuntimeEventString(event, "stepName"),
        },
      });
    default:
      return JSON.stringify({
        ...record,
        timestamp,
      });
  }
}

export function createStudioExecutionFrame(
  event: RuntimeEvent,
): StudioExecutionFrame {
  return {
    payload: serializeRuntimeEventFrame(event),
    receivedAtUtc: formatRuntimeEventTimestamp(event),
  };
}
