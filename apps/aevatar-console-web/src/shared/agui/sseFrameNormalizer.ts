import { AGUIEventType, type AGUIEvent } from '@aevatar-react-sdk/types';

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return undefined;
  }
  return value as JsonRecord;
}

function readString(record: JsonRecord, key: string): string {
  const value = record[key];
  return typeof value === 'string' ? value : '';
}

/**
 * Convert a backend protobuf-JSON SSE frame into the AGUIEvent format
 * expected by @aevatar-react-sdk/agui.
 *
 * Backend sends oneof-style frames:
 *   { "timestamp": "...", "runStarted": { "threadId": "...", "runId": "..." } }
 *
 * SDK expects typed frames:
 *   { "type": "RUN_STARTED", "timestamp": "...", "threadId": "...", "runId": "..." }
 */
export function normalizeBackendSseFrame(raw: unknown): AGUIEvent | null {
  const frame = asRecord(raw);
  if (!frame) {
    return null;
  }

  // If the frame already has a `type` field, it's already in SDK format.
  if (typeof frame.type === 'string') {
    return frame as unknown as AGUIEvent;
  }

  const rawTimestamp = frame.timestamp;
  const timestamp = typeof rawTimestamp === 'number'
    ? rawTimestamp
    : Number(rawTimestamp) || Date.now();

  if (frame.runStarted) {
    const data = asRecord(frame.runStarted);
    return {
      type: AGUIEventType.RUN_STARTED,
      timestamp,
      threadId: data ? readString(data, 'threadId') : '',
      runId: data ? readString(data, 'runId') : '',
    };
  }

  if (frame.runFinished) {
    const data = asRecord(frame.runFinished);
    return {
      type: AGUIEventType.RUN_FINISHED,
      timestamp,
      threadId: data ? readString(data, 'threadId') : '',
      runId: data ? readString(data, 'runId') : '',
      result: data?.result,
    };
  }

  if (frame.runError) {
    const data = asRecord(frame.runError);
    return {
      type: AGUIEventType.RUN_ERROR,
      timestamp,
      message: data ? readString(data, 'message') : '',
      code: data ? readString(data, 'code') || undefined : undefined,
      runId: data ? readString(data, 'runId') || undefined : undefined,
    };
  }

  if (frame.stepStarted) {
    const data = asRecord(frame.stepStarted);
    return {
      type: AGUIEventType.STEP_STARTED,
      timestamp,
      stepName: data ? readString(data, 'stepName') : '',
    };
  }

  if (frame.stepFinished) {
    const data = asRecord(frame.stepFinished);
    return {
      type: AGUIEventType.STEP_FINISHED,
      timestamp,
      stepName: data ? readString(data, 'stepName') : '',
    };
  }

  if (frame.toolCallStart) {
    const data = asRecord(frame.toolCallStart);
    return {
      type: AGUIEventType.TOOL_CALL_START,
      timestamp,
      toolCallId: data ? readString(data, 'toolCallId') : '',
      toolName: data ? readString(data, 'toolName') : '',
    };
  }

  if (frame.toolCallEnd) {
    const data = asRecord(frame.toolCallEnd);
    return {
      type: AGUIEventType.TOOL_CALL_END,
      timestamp,
      toolCallId: data ? readString(data, 'toolCallId') : '',
      result: data ? readString(data, 'result') : '',
    };
  }

  if (frame.textMessageStart) {
    const data = asRecord(frame.textMessageStart);
    return {
      type: AGUIEventType.TEXT_MESSAGE_START,
      timestamp,
      messageId: data ? readString(data, 'messageId') : '',
      role: data ? readString(data, 'role') : '',
    };
  }

  if (frame.textMessageContent) {
    const data = asRecord(frame.textMessageContent);
    return {
      type: AGUIEventType.TEXT_MESSAGE_CONTENT,
      timestamp,
      messageId: data ? readString(data, 'messageId') : '',
      delta: data ? readString(data, 'delta') : '',
    };
  }

  if (frame.textMessageEnd) {
    const data = asRecord(frame.textMessageEnd);
    return {
      type: AGUIEventType.TEXT_MESSAGE_END,
      timestamp,
      messageId: data ? readString(data, 'messageId') : '',
    };
  }

  if (frame.humanInputRequest) {
    const data = asRecord(frame.humanInputRequest);
    return {
      type: AGUIEventType.HUMAN_INPUT_REQUEST,
      timestamp,
      stepId: data ? readString(data, 'stepId') : '',
      runId: data ? readString(data, 'runId') : '',
      suspensionType: data ? readString(data, 'suspensionType') : '',
      prompt: data ? readString(data, 'prompt') : '',
      timeoutSeconds: typeof data?.timeoutSeconds === 'number' ? data.timeoutSeconds : 0,
      metadata: data?.metadata as Record<string, string> | undefined,
    };
  }

  if (frame.custom) {
    const data = asRecord(frame.custom);
    return {
      type: AGUIEventType.CUSTOM,
      timestamp,
      name: data ? readString(data, 'name') : '',
      value: data?.payload ?? data?.value,
    };
  }

  if (frame.stateSnapshot) {
    return {
      type: AGUIEventType.STATE_SNAPSHOT,
      timestamp,
      snapshot: frame.stateSnapshot,
    };
  }

  return null;
}

/**
 * Parse a backend SSE response into normalized AGUIEvent objects.
 * Unlike the SDK's parseSSEStream, this handles the protobuf-JSON oneof
 * format where event type is encoded as a field name instead of a `type` string.
 */
export async function* parseBackendSSEStream(
  response: Response,
  options?: { signal?: AbortSignal },
): AsyncGenerator<AGUIEvent, void, undefined> {
  if (!response.ok) {
    const text = await response.text().catch(() => '');
    throw new Error(`SSE request failed: HTTP ${response.status} — ${text || response.statusText}`);
  }

  const body = response.body;
  if (!body) {
    throw new Error('SSE response has no readable body.');
  }

  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  const dataLines: string[] = [];

  try {
    while (!options?.signal?.aborted) {
      const { done, value } = await reader.read();
      if (done) {
        buffer += '\n';
      } else {
        buffer += decoder.decode(value, { stream: true });
      }

      const lines = buffer.split('\n');
      buffer = done ? '' : (lines.pop() ?? '');

      for (const line of lines) {
        if (line === '' || line === '\r') {
          if (dataLines.length > 0) {
            const data = dataLines.splice(0, dataLines.length).join('\n').trim();
            if (data && data !== '[DONE]') {
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

        if (line.startsWith('data:')) {
          const payload = line.length > 5 ? line.slice(5) : '';
          dataLines.push(payload.startsWith(' ') ? payload.slice(1) : payload);
        }
      }

      if (done) {
        if (dataLines.length > 0) {
          const data = dataLines.splice(0, dataLines.length).join('\n').trim();
          if (data && data !== '[DONE]') {
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
