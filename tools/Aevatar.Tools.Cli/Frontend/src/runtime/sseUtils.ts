export type RuntimeEventType =
  | 'RUN_STARTED'
  | 'RUN_FINISHED'
  | 'RUN_ERROR'
  | 'TEXT_MESSAGE_START'
  | 'TEXT_MESSAGE_CONTENT'
  | 'TEXT_MESSAGE_END'
  | 'STEP_STARTED'
  | 'STEP_FINISHED'
  | 'HUMAN_INPUT_REQUEST'
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

function str(record: JsonRecord, key: string): string {
  const v = record[key];
  return typeof v === 'string' ? v : '';
}

export function normalizeBackendSseFrame(raw: unknown): RuntimeEvent | null {
  const frame = asRecord(raw);
  if (!frame) return null;

  if (typeof frame.type === 'string') return frame as unknown as RuntimeEvent;

  const rawTs = frame.timestamp;
  const timestamp = typeof rawTs === 'number' ? rawTs : Number(rawTs) || Date.now();

  if (frame.runStarted) {
    const d = asRecord(frame.runStarted);
    return { type: 'RUN_STARTED', timestamp, threadId: d ? str(d, 'threadId') : '', runId: d ? str(d, 'runId') : '' };
  }
  if (frame.runFinished) {
    const d = asRecord(frame.runFinished);
    return { type: 'RUN_FINISHED', timestamp, threadId: d ? str(d, 'threadId') : '', runId: d ? str(d, 'runId') : '' };
  }
  if (frame.runError) {
    const d = asRecord(frame.runError);
    return { type: 'RUN_ERROR', timestamp, message: d ? str(d, 'message') : '', code: d ? str(d, 'code') : '' };
  }
  if (frame.textMessageStart) {
    const d = asRecord(frame.textMessageStart);
    return { type: 'TEXT_MESSAGE_START', timestamp, messageId: d ? str(d, 'messageId') : '', role: d ? str(d, 'role') : '' };
  }
  if (frame.textMessageContent) {
    const d = asRecord(frame.textMessageContent);
    return { type: 'TEXT_MESSAGE_CONTENT', timestamp, messageId: d ? str(d, 'messageId') : '', delta: d ? str(d, 'delta') : '' };
  }
  if (frame.textMessageEnd) {
    const d = asRecord(frame.textMessageEnd);
    return { type: 'TEXT_MESSAGE_END', timestamp, messageId: d ? str(d, 'messageId') : '' };
  }
  if (frame.stepStarted) {
    const d = asRecord(frame.stepStarted);
    return { type: 'STEP_STARTED', timestamp, stepName: d ? str(d, 'stepName') : '' };
  }
  if (frame.stepFinished) {
    const d = asRecord(frame.stepFinished);
    return { type: 'STEP_FINISHED', timestamp, stepName: d ? str(d, 'stepName') : '' };
  }
  if (frame.humanInputRequest) {
    const d = asRecord(frame.humanInputRequest);
    return {
      type: 'HUMAN_INPUT_REQUEST', timestamp,
      stepId: d ? str(d, 'stepId') : '', runId: d ? str(d, 'runId') : '',
      prompt: d ? str(d, 'prompt') : '',
    };
  }
  if (frame.custom) {
    const d = asRecord(frame.custom);
    return { type: 'CUSTOM', timestamp, name: d ? str(d, 'name') : '', value: d?.payload ?? d?.value };
  }
  if (frame.stateSnapshot) {
    return { type: 'STATE_SNAPSHOT', timestamp, snapshot: frame.stateSnapshot };
  }

  return null;
}
