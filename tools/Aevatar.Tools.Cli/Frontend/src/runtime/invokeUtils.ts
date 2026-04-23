import type { RuntimeEvent } from './sseUtils';
import type { PendingHumanInputInfo, ServiceEndpoint, ServiceOption } from './chatTypes';
import { extractReasoningDelta, extractStepCompletedOutput } from './sseUtils';

export type InvokeEventRecord = {
  type: string;
  data: unknown;
};

export type InvokeSurfaceSupport = {
  supported: boolean;
  reason: string;
  suggestedTab: 'chat' | 'raw' | null;
};

export type InvokeRunStatus = 'idle' | 'running' | 'completed' | 'stopped' | 'error' | 'needs-input' | 'submitted';

export type InvokeRunSummary = {
  status: InvokeRunStatus;
  actorId: string;
  runId: string;
  textOutput: string;
  errorMessage: string;
  humanInputPrompt: string;
  eventCount: number;
  stepCount: number;
  toolCallCount: number;
  lastEventType: string;
};

export type InvokeWorkbenchMode = 'timeline' | 'trace' | 'tabs' | 'bubbles' | 'raw';

export type InvokeWorkbenchFrameKind =
  | 'run.start'
  | 'run.finish'
  | 'step.start'
  | 'step.done'
  | 'step.error'
  | 'tool.call'
  | 'tool.result'
  | 'thinking'
  | 'assistant.message'
  | 'human.request'
  | 'status';

export type InvokeWorkbenchFrame = {
  id: string;
  t: number;
  kind: InvokeWorkbenchFrameKind;
  label: string;
  rawType: string;
  step?: string;
  detail?: string;
  text?: string;
  args?: string;
  result?: string;
  error?: string;
  options?: string[];
};

export type ParsedInvokeHeaders = {
  headers: Record<string, string>;
  errors: string[];
};

function normalizeEndpointKind(kind: string | undefined) {
  const normalized = String(kind || '').trim().toLowerCase();
  return normalized || 'command';
}

export function getStreamableInvokeEndpoints(service: ServiceOption | null | undefined): ServiceEndpoint[] {
  if (!service) {
    return [];
  }

  return service.endpoints.filter(endpoint => normalizeEndpointKind(endpoint.kind) === 'chat');
}

export function getNonStreamableInvokeEndpoints(service: ServiceOption | null | undefined): ServiceEndpoint[] {
  if (!service) {
    return [];
  }

  return service.endpoints.filter(endpoint => normalizeEndpointKind(endpoint.kind) !== 'chat');
}

export function getInvokeSurfaceSupport(service: ServiceOption | null | undefined): InvokeSurfaceSupport {
  if (!service) {
    return {
      supported: false,
      reason: 'Select a service first.',
      suggestedTab: null,
    };
  }

  if (service.kind === 'onboarding') {
    return {
      supported: false,
      reason: 'Onboarding 是引导式配置对话，不是实际可调用的 scope service。请在 Chat 里继续完成接入。',
      suggestedTab: 'chat',
    };
  }

  if (service.kind === 'streaming-proxy' || service.kind === 'nyxid-chat') {
    return {
      supported: true,
      reason: '',
      suggestedTab: null,
    };
  }

  const streamableEndpoints = getStreamableInvokeEndpoints(service);
  if (streamableEndpoints.length > 0) {
    return {
      supported: true,
      reason: '',
      suggestedTab: null,
    };
  }

  if (service.endpoints.length === 0) {
    return {
      supported: false,
      reason: '当前 service 还没有暴露任何 endpoint，所以这里没有可调用的目标。',
      suggestedTab: null,
    };
  }

  return {
    supported: false,
    reason: '当前 service 只暴露 command endpoint。这个 Invoke 页面只负责流式 chat 调用；如果你要发 typed command payload，请去 Raw。',
    suggestedTab: 'raw',
  };
}

export function parseInvokeHeaders(text: string): ParsedInvokeHeaders {
  const headers: Record<string, string> = {};
  const errors: string[] = [];
  const lines = text.split(/\r?\n/);

  for (let index = 0; index < lines.length; index += 1) {
    const rawLine = lines[index];
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) {
      continue;
    }

    const separatorIndex = rawLine.indexOf(':');
    if (separatorIndex <= 0) {
      errors.push(`Header line ${index + 1} must use "key: value".`);
      continue;
    }

    const key = rawLine.slice(0, separatorIndex).trim();
    const value = rawLine.slice(separatorIndex + 1).trim();
    if (!key) {
      errors.push(`Header line ${index + 1} is missing a key.`);
      continue;
    }

    headers[key] = value;
  }

  return { headers, errors };
}

export function buildInvokeRequestPayload(
  prompt: string,
  actorId?: string,
  headers?: Record<string, string>,
) {
  const payload: Record<string, unknown> = {
    prompt: prompt.trim(),
  };

  const normalizedActorId = String(actorId || '').trim();
  if (normalizedActorId) {
    payload.actorId = normalizedActorId;
  }

  if (headers && Object.keys(headers).length > 0) {
    payload.headers = headers;
  }

  return payload;
}

export function buildInvokeTransportLabel(
  scopeId: string,
  service: ServiceOption | null | undefined,
  endpointId: string,
) {
  const normalizedScopeId = encodeURIComponent(String(scopeId || '').trim());
  const normalizedEndpointId = encodeURIComponent(String(endpointId || '').trim() || 'chat');

  if (!service) {
    return '';
  }

  if (service.kind === 'streaming-proxy') {
    return `/api/scopes/${normalizedScopeId}/streaming-proxy/rooms/{roomId}:chat`;
  }

  return `/api/scopes/${normalizedScopeId}/services/${encodeURIComponent(service.id)}/invoke/${normalizedEndpointId}:stream`;
}

export function summarizeInvokeEvents(events: InvokeEventRecord[]): InvokeRunSummary {
  const summary: InvokeRunSummary = {
    status: 'idle',
    actorId: '',
    runId: '',
    textOutput: '',
    errorMessage: '',
    humanInputPrompt: '',
    eventCount: events.length,
    stepCount: 0,
    toolCallCount: 0,
    lastEventType: '',
  };

  const stepNames = new Set<string>();
  const toolCallKeys = new Set<string>();

  for (const item of events) {
    const type = String(item?.type || '').trim().toUpperCase();
    const data = (item?.data || {}) as Record<string, unknown>;
    if (!type) {
      continue;
    }

    summary.lastEventType = type;

    switch (type) {
      case 'RUN_STARTED':
        summary.status = 'running';
        summary.actorId ||= String(data.threadId || data.actorId || '').trim();
        summary.runId ||= String(data.runId || '').trim();
        break;

      case 'RUN_FINISHED':
      case 'TEXT_MESSAGE_END':
        if (summary.status !== 'error' && summary.status !== 'needs-input' && summary.status !== 'stopped') {
          summary.status = 'completed';
        }
        summary.actorId ||= String(data.threadId || data.actorId || '').trim();
        summary.runId ||= String(data.runId || '').trim();
        break;

      case 'RUN_STOPPED':
        summary.status = 'stopped';
        break;

      case 'RUN_ERROR':
      case 'ERROR':
        summary.status = 'error';
        summary.errorMessage = String(data.message || data.error || 'Invocation failed.').trim();
        break;

      case 'TEXT_MESSAGE_CONTENT':
        summary.status = summary.status === 'idle' ? 'running' : summary.status;
        summary.textOutput += String(data.delta || '');
        break;

      case 'STEP_STARTED': {
        const stepName = String(data.stepName || '').trim();
        if (stepName) {
          stepNames.add(stepName);
        }
        if (summary.status === 'idle') {
          summary.status = 'running';
        }
        break;
      }

      case 'TOOL_CALL_START': {
        const key = String(data.toolCallId || data.toolName || `tool-${toolCallKeys.size + 1}`).trim();
        if (key) {
          toolCallKeys.add(key);
        }
        break;
      }

      case 'HUMAN_INPUT_REQUEST':
        summary.status = 'needs-input';
        summary.humanInputPrompt = String(data.prompt || '').trim();
        summary.runId ||= String(data.runId || '').trim();
        break;

      case 'HUMAN_INPUT_RESPONSE':
        if (summary.status !== 'error' && summary.status !== 'stopped' && summary.status !== 'completed') {
          summary.status = 'submitted';
        }
        summary.humanInputPrompt = '';
        summary.runId ||= String(data.runId || '').trim();
        break;

      case 'CUSTOM': {
        const evt = data as RuntimeEvent;
        const payload = toRecord(evt.payload) || toRecord(evt.value) || {};
        const customName = String(evt.name || '').trim();
        if (customName === 'aevatar.human_input.request') {
          summary.status = 'needs-input';
          summary.humanInputPrompt = String(payload.prompt || '').trim();
          summary.runId ||= String(payload.runId || payload.run_id || '').trim();
          break;
        }

        if (customName === 'TOOL_APPROVAL_REQUEST') {
          summary.status = 'needs-input';
          summary.humanInputPrompt = 'Tool approval requested';
          break;
        }

        if (customName === 'aevatar.human_input.response') {
          if (summary.status !== 'error' && summary.status !== 'stopped' && summary.status !== 'completed') {
            summary.status = 'submitted';
          }
          summary.humanInputPrompt = '';
          summary.runId ||= String(payload.runId || payload.run_id || '').trim();
          break;
        }

        const stepOutput = extractStepCompletedOutput(evt);
        if (stepOutput && !summary.textOutput.trim()) {
          summary.textOutput = stepOutput.trim();
        }
        break;
      }
    }
  }

  summary.textOutput = summary.textOutput.trim();
  summary.stepCount = stepNames.size;
  summary.toolCallCount = toolCallKeys.size;

  return summary;
}

export function extractInvokePendingHumanInput(
  events: InvokeEventRecord[],
  serviceId: string,
  fallbackActorId?: string,
): PendingHumanInputInfo | null {
  const actorId = String(fallbackActorId || '').trim();
  let resolvedActorId = actorId;

  for (const item of events) {
    const type = String(item?.type || '').trim().toUpperCase();
    const data = (item?.data || {}) as Record<string, unknown>;
    if (type === 'RUN_STARTED') {
      resolvedActorId ||= String(data.threadId || data.actorId || '').trim();
    }
  }

  for (let index = events.length - 1; index >= 0; index -= 1) {
    const item = events[index];
    const type = String(item?.type || '').trim().toUpperCase();
    const data = (item?.data || {}) as Record<string, unknown>;

    if (!type) {
      continue;
    }

    if (type === 'HUMAN_INPUT_REQUEST') {
      const stepId = String(data.stepId || '').trim();
      const runId = String(data.runId || '').trim();
      const prompt = String(data.prompt || '').trim();
      if (!stepId || !runId || !prompt) {
        return null;
      }

      const options = readStringArray(data.options);
      return {
        stepId,
        runId,
        prompt,
        serviceId,
        actorId: resolvedActorId || undefined,
        ...(options.length > 0 ? { options } : {}),
      };
    }

    if (type === 'CUSTOM') {
      const evt = data as RuntimeEvent;
      const customName = String(evt.name || '').trim();
      const payload = toRecord(evt.payload) || toRecord(evt.value) || {};

      if (customName === 'aevatar.human_input.request') {
        const stepId = String(payload.stepId || payload.step_id || '').trim();
        const runId = String(payload.runId || payload.run_id || '').trim();
        const prompt = String(payload.prompt || '').trim();
        if (!stepId || !runId || !prompt) {
          return null;
        }

        const options = readStringArray(payload.options);
        return {
          stepId,
          runId,
          prompt,
          serviceId,
          actorId: resolvedActorId || undefined,
          ...(options.length > 0 ? { options } : {}),
        };
      }

      if (customName === 'aevatar.human_input.response') {
        return null;
      }

      continue;
    }

    if (
      type === 'RUN_FINISHED'
      || type === 'RUN_ERROR'
      || type === 'ERROR'
      || type === 'RUN_STOPPED'
      || type === 'TEXT_MESSAGE_START'
      || type === 'TEXT_MESSAGE_CONTENT'
      || type === 'TEXT_MESSAGE_END'
      || type === 'STEP_STARTED'
      || type === 'STEP_FINISHED'
      || type === 'TOOL_CALL_START'
      || type === 'TOOL_CALL_END'
      || type === 'TOOL_APPROVAL_REQUEST'
      || type === 'HUMAN_INPUT_RESPONSE'
    ) {
      return null;
    }
  }

  return null;
}

function toRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
}

function readString(value: unknown) {
  return typeof value === 'string' ? value.trim() : '';
}

function readStringArray(value: unknown) {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0) : [];
}

function readFrameTimestamp(data: Record<string, unknown>, fallback: number) {
  const raw = data.timestamp;
  if (typeof raw === 'number' && Number.isFinite(raw)) {
    return raw;
  }

  const numeric = Number(raw);
  return Number.isFinite(numeric) ? numeric : fallback;
}

function serializeDetail(value: unknown) {
  if (typeof value === 'string') {
    return value.trim();
  }

  if (value == null) {
    return '';
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function frameId(index: number, kind: InvokeWorkbenchFrameKind) {
  return `${kind}-${index + 1}`;
}

export function buildInvokeWorkbenchFrames(events: InvokeEventRecord[]): InvokeWorkbenchFrame[] {
  if (events.length === 0) {
    return [];
  }

  const firstData = toRecord(events[0]?.data) || {};
  const baseline = readFrameTimestamp(firstData, 0);
  const frames: InvokeWorkbenchFrame[] = [];
  const assistantChunks: string[] = [];
  const thinkingChunks: string[] = [];
  let assistantTimestamp = 0;
  let thinkingTimestamp = 0;
  let fallbackTimestamp = baseline;

  const pushFrame = (frame: Omit<InvokeWorkbenchFrame, 'id'>) => {
    frames.push({
      ...frame,
      id: frameId(frames.length, frame.kind),
    });
  };

  const flushThinking = () => {
    const text = thinkingChunks.join('').trim();
    if (!text) {
      return;
    }

    pushFrame({
      t: Math.max(0, thinkingTimestamp - baseline),
      kind: 'thinking',
      rawType: 'CUSTOM',
      label: 'model thinking',
      detail: text.length > 96 ? `${text.slice(0, 96)}...` : text,
      text,
    });
    thinkingChunks.length = 0;
  };

  const flushAssistantMessage = () => {
    const text = assistantChunks.join('').trim();
    if (!text) {
      return;
    }

    pushFrame({
      t: Math.max(0, assistantTimestamp - baseline),
      kind: 'assistant.message',
      rawType: 'TEXT_MESSAGE_CONTENT',
      label: 'assistant response',
      detail: text.length > 120 ? `${text.slice(0, 120)}...` : text,
      text,
    });
    assistantChunks.length = 0;
  };

  for (const item of events) {
    const type = String(item?.type || '').trim().toUpperCase();
    const data = toRecord(item?.data) || {};
    if (!type) {
      continue;
    }

    const rawTimestamp = readFrameTimestamp(data, fallbackTimestamp + 80);
    fallbackTimestamp = rawTimestamp;
    const t = Math.max(0, fallbackTimestamp - baseline);

    switch (type) {
      case 'RUN_STARTED':
        pushFrame({
          t,
          kind: 'run.start',
          rawType: type,
          label: 'Run started',
          detail: [readString(data.runId), readString(data.threadId || data.actorId)].filter(Boolean).join(' · '),
          step: readString(data.threadId || data.actorId),
        });
        break;

      case 'RUN_FINISHED':
        flushThinking();
        flushAssistantMessage();
        pushFrame({
          t,
          kind: 'run.finish',
          rawType: type,
          label: 'Run finished',
          detail: readString(data.runId),
        });
        break;

      case 'RUN_STOPPED':
        flushThinking();
        flushAssistantMessage();
        pushFrame({
          t,
          kind: 'status',
          rawType: type,
          label: 'Run stopped',
          detail: readString(data.reason) || 'Invocation stopped by user.',
        });
        break;

      case 'RUN_ERROR':
      case 'ERROR':
        flushThinking();
        flushAssistantMessage();
        pushFrame({
          t,
          kind: 'step.error',
          rawType: type,
          label: 'Run error',
          detail: readString(data.message || data.error) || 'Invocation failed.',
          error: readString(data.message || data.error) || 'Invocation failed.',
        });
        break;

      case 'STEP_STARTED':
        flushThinking();
        pushFrame({
          t,
          kind: 'step.start',
          rawType: type,
          label: readString(data.stepName) || 'step started',
          step: readString(data.stepName),
          detail: 'Step started',
        });
        break;

      case 'STEP_FINISHED':
        flushThinking();
        pushFrame({
          t,
          kind: 'step.done',
          rawType: type,
          label: readString(data.stepName) || 'step finished',
          step: readString(data.stepName),
          detail: 'Step finished',
        });
        break;

      case 'TOOL_CALL_START':
        flushThinking();
        pushFrame({
          t,
          kind: 'tool.call',
          rawType: type,
          label: readString(data.toolName) || 'tool call',
          detail: readString(data.toolCallId),
          args: serializeDetail(data.argumentsJson || data.arguments || data.args),
        });
        break;

      case 'TOOL_CALL_END':
        flushThinking();
        pushFrame({
          t,
          kind: 'tool.result',
          rawType: type,
          label: readString(data.toolCallId) || 'tool result',
          detail: readString(data.result) || 'Tool completed',
          result: serializeDetail(data.result),
        });
        break;

      case 'TOOL_APPROVAL_REQUEST':
        flushThinking();
        pushFrame({
          t,
          kind: 'human.request',
          rawType: type,
          label: readString(data.toolName) || 'Tool approval requested',
          detail: readString(data.argumentsJson) || 'Waiting for approval',
          text: readString(data.argumentsJson),
        });
        break;

      case 'TEXT_MESSAGE_CONTENT': {
        const delta = readString(data.delta);
        if (delta) {
          assistantTimestamp = fallbackTimestamp;
          assistantChunks.push(delta);
        }
        break;
      }

      case 'TEXT_MESSAGE_END':
        flushAssistantMessage();
        break;

      case 'HUMAN_INPUT_REQUEST':
        flushThinking();
        flushAssistantMessage();
        pushFrame({
          t,
          kind: 'human.request',
          rawType: type,
          label: 'Human input requested',
          detail: readString(data.prompt) || 'Workflow is waiting for input.',
          text: readString(data.prompt),
          options: readStringArray(data.options),
        });
        break;

      case 'HUMAN_INPUT_RESPONSE': {
        flushThinking();
        flushAssistantMessage();
        const approved = typeof data.approved === 'boolean' ? data.approved : true;
        const userInput = readString(data.userInput);
        pushFrame({
          t,
          kind: 'status',
          rawType: type,
          label: approved ? 'Input received' : 'Input rejected',
          detail: userInput
            ? `${approved ? 'Resume accepted' : 'Resume rejected'} · ${userInput}`
            : approved ? 'Resume accepted. Waiting for the next observation frame.' : 'Resume rejected.',
          text: userInput || undefined,
        });
        break;
      }

      case 'CUSTOM': {
        const runtimeEvent = data as RuntimeEvent;
        const reasoningText = extractReasoningDelta(runtimeEvent);
        if (reasoningText) {
          thinkingTimestamp = fallbackTimestamp;
          thinkingChunks.push(reasoningText);
          break;
        }

        const stepOutput = extractStepCompletedOutput(runtimeEvent);
        if (stepOutput && assistantChunks.join('').trim().length === 0) {
          assistantTimestamp = fallbackTimestamp;
          assistantChunks.push(stepOutput);
          break;
        }

        const payload = toRecord(runtimeEvent.payload) || toRecord(runtimeEvent.value) || {};
        const customName = readString(runtimeEvent.name);
        if (customName === 'aevatar.human_input.request') {
          flushThinking();
          flushAssistantMessage();
          pushFrame({
            t,
            kind: 'human.request',
            rawType: type,
            label: 'Human input requested',
            detail: readString(payload.prompt) || 'Workflow is waiting for input.',
            text: readString(payload.prompt),
            options: readStringArray(payload.options),
          });
          break;
        }

        if (customName === 'aevatar.human_input.response') {
          flushThinking();
          flushAssistantMessage();
          const approved = typeof payload.approved === 'boolean' ? payload.approved : true;
          const userInput = readString(payload.userInput || payload.user_input);
          pushFrame({
            t,
            kind: 'status',
            rawType: type,
            label: approved ? 'Input received' : 'Input rejected',
            detail: userInput
              ? `${approved ? 'Resume accepted' : 'Resume rejected'} · ${userInput}`
              : approved ? 'Resume accepted. Waiting for the next observation frame.' : 'Resume rejected.',
            text: userInput || undefined,
          });
          break;
        }

        if (customName === 'TOOL_APPROVAL_REQUEST') {
          flushThinking();
          pushFrame({
            t,
            kind: 'human.request',
            rawType: type,
            label: readString(payload.toolName) || 'Tool approval requested',
            detail: readString(payload.argumentsJson) || 'Waiting for approval',
            text: readString(payload.argumentsJson),
          });
        }
        break;
      }
    }
  }

  flushThinking();
  flushAssistantMessage();

  return frames;
}
