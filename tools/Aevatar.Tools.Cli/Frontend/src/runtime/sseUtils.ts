export type RuntimeEventType =
  | 'RUN_STARTED'
  | 'RUN_FINISHED'
  | 'RUN_ERROR'
  | 'RUN_STOPPED'
  | 'TEXT_MESSAGE_START'
  | 'TEXT_MESSAGE_CONTENT'
  | 'TEXT_MESSAGE_END'
  | 'STEP_STARTED'
  | 'STEP_FINISHED'
  | 'TOOL_CALL_START'
  | 'TOOL_CALL_END'
  | 'TOOL_APPROVAL_REQUEST'
  | 'HUMAN_INPUT_REQUEST'
  | 'MEDIA_CONTENT'
  | 'CUSTOM'
  | 'STATE_SNAPSHOT';

export type RuntimeEvent = {
  type: RuntimeEventType;
  timestamp?: number;
  [key: string]: unknown;
};

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return undefined;
  return value as JsonRecord;
}

function str(record: JsonRecord, ...keys: string[]): string {
  for (const key of keys) {
    const v = record[key];
    if (typeof v === 'string') return v;
  }
  return '';
}

/**
 * Map from oneof key (camelCase) → RuntimeEventType.
 * Used to detect the "nested oneof" format where data sits under a sub-object.
 */
const ONEOF_KEY_MAP: Record<string, RuntimeEventType> = {
  runStarted: 'RUN_STARTED',
  runFinished: 'RUN_FINISHED',
  runError: 'RUN_ERROR',
  runStopped: 'RUN_STOPPED',
  textMessageStart: 'TEXT_MESSAGE_START',
  textMessageContent: 'TEXT_MESSAGE_CONTENT',
  textMessageEnd: 'TEXT_MESSAGE_END',
  stepStarted: 'STEP_STARTED',
  stepFinished: 'STEP_FINISHED',
  toolCallStart: 'TOOL_CALL_START',
  toolCallEnd: 'TOOL_CALL_END',
  toolApprovalRequest: 'TOOL_APPROVAL_REQUEST',
  humanInputRequest: 'HUMAN_INPUT_REQUEST',
  mediaContent: 'MEDIA_CONTENT',
  custom: 'CUSTOM',
  stateSnapshot: 'STATE_SNAPSHOT',
};

/**
 * Normalize an SSE frame from the backend into a flat RuntimeEvent.
 *
 * The backend emits frames in two formats:
 *   1. Typed + nested:  { "type": "RUN_ERROR", "runError": { "message": "..." } }
 *   2. Oneof only:      { "runError": { "message": "..." }, "timestamp": 123 }
 *
 * Both must be flattened to: { type: "RUN_ERROR", message: "...", timestamp: ... }
 */
export function normalizeBackendSseFrame(raw: unknown): RuntimeEvent | null {
  const frame = asRecord(raw);
  if (!frame) return null;

  const rawTs = frame.timestamp;
  const timestamp = typeof rawTs === 'number' ? rawTs : Number(rawTs) || Date.now();

  // Try to find the oneof sub-object (works regardless of whether `type` is present)
  for (const [oneofKey, eventType] of Object.entries(ONEOF_KEY_MAP)) {
    if (!(oneofKey in frame)) continue;

    const nested = asRecord(frame[oneofKey]);

    switch (eventType) {
      case 'RUN_STARTED': {
        const d = nested;
        return {
          type: 'RUN_STARTED', timestamp,
          threadId: d ? str(d, 'threadId') : str(frame, 'threadId', 'actorId'),
          runId: d ? str(d, 'runId') : str(frame, 'runId'),
        };
      }
      case 'RUN_FINISHED': {
        const d = nested;
        return {
          type: 'RUN_FINISHED', timestamp,
          threadId: d ? str(d, 'threadId') : '',
          runId: d ? str(d, 'runId') : '',
        };
      }
      case 'RUN_ERROR': {
        const d = nested;
        return {
          type: 'RUN_ERROR', timestamp,
          message: d ? str(d, 'message') : '',
          code: d ? str(d, 'code') : '',
        };
      }
      case 'RUN_STOPPED': {
        const d = nested;
        return {
          type: 'RUN_STOPPED', timestamp,
          runId: d ? str(d, 'runId') : '',
          reason: d ? str(d, 'reason') : '',
        };
      }
      case 'TEXT_MESSAGE_START': {
        const d = nested;
        return {
          type: 'TEXT_MESSAGE_START', timestamp,
          messageId: d ? str(d, 'messageId') : '',
          role: d ? str(d, 'role') : '',
        };
      }
      case 'TEXT_MESSAGE_CONTENT': {
        const d = nested;
        return {
          type: 'TEXT_MESSAGE_CONTENT', timestamp,
          messageId: d ? str(d, 'messageId') : '',
          delta: d ? str(d, 'delta') : '',
        };
      }
      case 'TEXT_MESSAGE_END': {
        const d = nested;
        return {
          type: 'TEXT_MESSAGE_END', timestamp,
          messageId: d ? str(d, 'messageId') : '',
        };
      }
      case 'STEP_STARTED': {
        const d = nested;
        return {
          type: 'STEP_STARTED', timestamp,
          stepName: d ? str(d, 'stepName') : '',
        };
      }
      case 'STEP_FINISHED': {
        const d = nested;
        return {
          type: 'STEP_FINISHED', timestamp,
          stepName: d ? str(d, 'stepName') : '',
        };
      }
      case 'TOOL_CALL_START': {
        const d = nested;
        return {
          type: 'TOOL_CALL_START', timestamp,
          toolCallId: d ? str(d, 'toolCallId') : '',
          toolName: d ? str(d, 'toolName') : '',
        };
      }
      case 'TOOL_CALL_END': {
        const d = nested;
        return {
          type: 'TOOL_CALL_END', timestamp,
          toolCallId: d ? str(d, 'toolCallId') : '',
          result: d ? str(d, 'result') : '',
        };
      }
      case 'TOOL_APPROVAL_REQUEST': {
        const d = nested;
        return {
          type: 'TOOL_APPROVAL_REQUEST', timestamp,
          requestId: d ? str(d, 'requestId') : '',
          toolName: d ? str(d, 'toolName') : '',
          toolCallId: d ? str(d, 'toolCallId') : '',
          argumentsJson: d ? str(d, 'argumentsJson') : '',
          isDestructive: d ? !!d.isDestructive : false,
          timeoutSeconds: d && typeof d.timeoutSeconds === 'number' ? d.timeoutSeconds : 15,
        };
      }
      case 'HUMAN_INPUT_REQUEST': {
        const d = nested;
        const rawOptions = d ? (d.options as unknown) : undefined;
        const options = Array.isArray(rawOptions) ? rawOptions.filter((o): o is string => typeof o === 'string') : undefined;
        return {
          type: 'HUMAN_INPUT_REQUEST', timestamp,
          stepId: d ? str(d, 'stepId') : '',
          runId: d ? str(d, 'runId') : '',
          prompt: d ? str(d, 'prompt') : '',
          ...(options && options.length > 0 ? { options } : {}),
        };
      }
      case 'MEDIA_CONTENT': {
        const d = nested;
        return {
          type: 'MEDIA_CONTENT', timestamp,
          kind: d ? str(d, 'kind') : '',
          dataBase64: d ? str(d, 'dataBase64') : '',
          mediaType: d ? str(d, 'mediaType') : '',
          uri: d ? str(d, 'uri') : '',
          name: d ? str(d, 'name') : '',
          text: d ? str(d, 'text') : '',
        };
      }
      case 'CUSTOM': {
        const d = nested;
        const name = d ? str(d, 'name') : '';
        const payload = d ? asRecord(d.payload) : undefined;
        return { type: 'CUSTOM', timestamp, name, payload, value: d?.payload ?? d?.value };
      }
      case 'STATE_SNAPSHOT': {
        return { type: 'STATE_SNAPSHOT', timestamp, snapshot: frame[oneofKey] };
      }
    }
  }

  // Fallback: if `type` is present but no oneof key found,
  // the frame is already flat (e.g. { "type": "RUN_STARTED", "threadId": "..." })
  if (typeof frame.type === 'string') {
    return { ...frame, timestamp } as unknown as RuntimeEvent;
  }

  return null;
}

// ── Custom event payload helpers ────────────────────────────────────────────────

/** Extract text output from an aevatar.step.completed custom event payload */
export function extractStepCompletedOutput(evt: RuntimeEvent): string | null {
  if (evt.type !== 'CUSTOM' || evt.name !== 'aevatar.step.completed') return null;
  const payload = asRecord(evt.payload);
  if (!payload) return null;
  const output = payload.output || payload.Output;
  return typeof output === 'string' ? output : null;
}

/** Extract reasoning delta from an aevatar.llm.reasoning custom event payload */
export function extractReasoningDelta(evt: RuntimeEvent): string | null {
  if (evt.type !== 'CUSTOM' || evt.name !== 'aevatar.llm.reasoning') return null;
  const payload = asRecord(evt.payload);
  if (!payload) return null;
  const delta = payload.delta || payload.Delta;
  return typeof delta === 'string' ? delta : null;
}

/** Extract step request info */
export function extractStepRequest(evt: RuntimeEvent): { stepId: string; stepType: string; input: string } | null {
  if (evt.type !== 'CUSTOM' || evt.name !== 'aevatar.step.request') return null;
  const payload = asRecord(evt.payload);
  if (!payload) return null;
  return {
    stepId: str(payload, 'stepId', 'StepId'),
    stepType: str(payload, 'stepType', 'StepType'),
    input: str(payload, 'input', 'Input'),
  };
}

/** Check if a custom event is a raw observed event (catch-all, not user-facing) */
export function isRawObserved(evt: RuntimeEvent): boolean {
  return evt.type === 'CUSTOM' && evt.name === 'aevatar.raw.observed';
}
