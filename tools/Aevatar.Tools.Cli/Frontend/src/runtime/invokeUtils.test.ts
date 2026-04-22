import { describe, expect, it } from 'vitest';

import type { ServiceOption } from './chatTypes';
import {
  buildInvokeWorkbenchFrames,
  buildInvokeRequestPayload,
  extractInvokePendingHumanInput,
  getInvokeSurfaceSupport,
  getStreamableInvokeEndpoints,
  parseInvokeHeaders,
  summarizeInvokeEvents,
} from './invokeUtils';

describe('parseInvokeHeaders', () => {
  it('parses header lines and ignores blanks or comments', () => {
    const result = parseInvokeHeaders(`
nyxid.route_preference: /api/v1/proxy/s/demo
# comment
aevatar.model_override: gpt-5
`);

    expect(result.errors).toEqual([]);
    expect(result.headers).toEqual({
      'nyxid.route_preference': '/api/v1/proxy/s/demo',
      'aevatar.model_override': 'gpt-5',
    });
  });

  it('reports invalid header syntax', () => {
    const result = parseInvokeHeaders('broken-line');

    expect(result.headers).toEqual({});
    expect(result.errors).toEqual(['Header line 1 must use "key: value".']);
  });
});

describe('getInvokeSurfaceSupport', () => {
  it('sends onboarding traffic back to chat', () => {
    const service: ServiceOption = {
      id: 'onboarding',
      label: 'Onboarding',
      kind: 'onboarding',
      endpoints: [{ endpointId: 'chat', displayName: 'Chat', kind: 'chat' }],
    };

    expect(getInvokeSurfaceSupport(service)).toEqual({
      supported: false,
      reason: 'Onboarding 是引导式配置对话，不是实际可调用的 scope service。请在 Chat 里继续完成接入。',
      suggestedTab: 'chat',
    });
  });

  it('blocks command-only services from the streaming invoke surface', () => {
    const service: ServiceOption = {
      id: 'orders',
      label: 'Orders',
      kind: 'service',
      endpoints: [{ endpointId: 'run', displayName: 'Run', kind: 'command' }],
    };

    const support = getInvokeSurfaceSupport(service);

    expect(support.supported).toBe(false);
    expect(support.suggestedTab).toBe('raw');
  });

  it('keeps only chat endpoints as streamable', () => {
    const service: ServiceOption = {
      id: 'orders',
      label: 'Orders',
      kind: 'service',
      endpoints: [
        { endpointId: 'chat', displayName: 'Chat', kind: 'chat' },
        { endpointId: 'run', displayName: 'Run', kind: 'command' },
      ],
    };

    expect(getStreamableInvokeEndpoints(service)).toEqual([
      { endpointId: 'chat', displayName: 'Chat', kind: 'chat' },
    ]);
  });
});

describe('buildInvokeRequestPayload', () => {
  it('keeps only the supported streaming fields', () => {
    const payload = buildInvokeRequestPayload(' hello ', ' actor-1 ', { demo: 'true' });

    expect(payload).toEqual({
      prompt: 'hello',
      actorId: 'actor-1',
      headers: { demo: 'true' },
    });
  });
});

describe('summarizeInvokeEvents', () => {
  it('summarizes a successful streaming run', () => {
    const summary = summarizeInvokeEvents([
      { type: 'RUN_STARTED', data: { threadId: 'actor-1', runId: 'run-1' } },
      { type: 'STEP_STARTED', data: { stepName: 'answer' } },
      { type: 'TEXT_MESSAGE_CONTENT', data: { delta: 'Hello' } },
      { type: 'TEXT_MESSAGE_CONTENT', data: { delta: ' world' } },
      { type: 'TOOL_CALL_START', data: { toolCallId: 'tool-1', toolName: 'search' } },
      { type: 'RUN_FINISHED', data: { threadId: 'actor-1', runId: 'run-1' } },
    ]);

    expect(summary).toEqual({
      status: 'completed',
      actorId: 'actor-1',
      runId: 'run-1',
      textOutput: 'Hello world',
      errorMessage: '',
      humanInputPrompt: '',
      eventCount: 6,
      stepCount: 1,
      toolCallCount: 1,
      lastEventType: 'RUN_FINISHED',
    });
  });

  it('surfaces pending human input ahead of completion', () => {
    const summary = summarizeInvokeEvents([
      { type: 'RUN_STARTED', data: { threadId: 'actor-2', runId: 'run-2' } },
      { type: 'HUMAN_INPUT_REQUEST', data: { runId: 'run-2', prompt: 'Need approval' } },
    ]);

    expect(summary.status).toBe('needs-input');
    expect(summary.humanInputPrompt).toBe('Need approval');
  });

  it('reads custom workflow human-input events', () => {
    const summary = summarizeInvokeEvents([
      { type: 'RUN_STARTED', data: { threadId: 'actor-3', runId: 'run-3' } },
      {
        type: 'CUSTOM',
        data: {
          type: 'CUSTOM',
          name: 'aevatar.human_input.request',
          payload: { prompt: 'Choose one', runId: 'run-3' },
        },
      },
    ]);

    expect(summary.status).toBe('needs-input');
    expect(summary.humanInputPrompt).toBe('Choose one');
  });

  it('moves pending input into submitted once a response is posted', () => {
    const summary = summarizeInvokeEvents([
      { type: 'RUN_STARTED', data: { threadId: 'actor-4', runId: 'run-4' } },
      { type: 'HUMAN_INPUT_REQUEST', data: { runId: 'run-4', prompt: 'Choose one', stepId: 'step-1' } },
      { type: 'HUMAN_INPUT_RESPONSE', data: { runId: 'run-4', stepId: 'step-1', approved: true, userInput: '1' } },
    ]);

    expect(summary.status).toBe('submitted');
    expect(summary.humanInputPrompt).toBe('');
  });
});

describe('buildInvokeWorkbenchFrames', () => {
  it('converts runtime events into timeline-friendly frames', () => {
    const frames = buildInvokeWorkbenchFrames([
      { type: 'RUN_STARTED', data: { timestamp: 1000, threadId: 'actor-1', runId: 'run-1' } },
      { type: 'STEP_STARTED', data: { timestamp: 1100, stepName: 'intake' } },
      {
        type: 'CUSTOM',
        data: {
          type: 'CUSTOM',
          timestamp: 1200,
          name: 'aevatar.llm.reasoning',
          payload: { delta: 'Classifying intent' },
        },
      },
      { type: 'TOOL_CALL_START', data: { timestamp: 1400, toolCallId: 'tool-1', toolName: 'classify' } },
      { type: 'TOOL_CALL_END', data: { timestamp: 1600, toolCallId: 'tool-1', result: '{"intent":"refund"}' } },
      { type: 'TEXT_MESSAGE_CONTENT', data: { timestamp: 1800, delta: 'Refund request detected.' } },
      { type: 'TEXT_MESSAGE_END', data: { timestamp: 1900 } },
      { type: 'RUN_FINISHED', data: { timestamp: 2000, runId: 'run-1' } },
    ]);

    expect(frames.map(frame => frame.kind)).toEqual([
      'run.start',
      'step.start',
      'thinking',
      'tool.call',
      'tool.result',
      'assistant.message',
      'run.finish',
    ]);
    expect(frames[2]?.text).toBe('Classifying intent');
    expect(frames[5]?.text).toBe('Refund request detected.');
  });

  it('maps human-input requests into a dedicated frame', () => {
    const frames = buildInvokeWorkbenchFrames([
      { type: 'RUN_STARTED', data: { timestamp: 1000, threadId: 'actor-2', runId: 'run-2' } },
      {
        type: 'HUMAN_INPUT_REQUEST',
        data: { timestamp: 1600, prompt: 'Approve refund?', options: ['Approve', 'Reject'] },
      },
    ]);

    expect(frames[frames.length - 1]).toMatchObject({
      kind: 'human.request',
      text: 'Approve refund?',
      options: ['Approve', 'Reject'],
    });
  });

  it('maps human-input responses into a status frame', () => {
    const frames = buildInvokeWorkbenchFrames([
      { type: 'RUN_STARTED', data: { timestamp: 1000, threadId: 'actor-2', runId: 'run-2' } },
      { type: 'HUMAN_INPUT_RESPONSE', data: { timestamp: 1700, runId: 'run-2', stepId: 'step-1', approved: true, userInput: 'Approve' } },
    ]);

    expect(frames[frames.length - 1]).toMatchObject({
      kind: 'status',
      label: 'Input received',
      text: 'Approve',
    });
  });
});

describe('extractInvokePendingHumanInput', () => {
  it('returns the latest unresolved human-input request', () => {
    const pending = extractInvokePendingHumanInput([
      { type: 'RUN_STARTED', data: { threadId: 'actor-9', runId: 'run-9' } },
      { type: 'HUMAN_INPUT_REQUEST', data: { stepId: 'step-1', runId: 'run-9', prompt: 'Approve?', options: ['Approve', 'Reject'] } },
    ], 'svc-demo');

    expect(pending).toEqual({
      stepId: 'step-1',
      runId: 'run-9',
      prompt: 'Approve?',
      serviceId: 'svc-demo',
      actorId: 'actor-9',
      options: ['Approve', 'Reject'],
    });
  });

  it('clears pending state once later continuation events exist', () => {
    const pending = extractInvokePendingHumanInput([
      { type: 'RUN_STARTED', data: { threadId: 'actor-9', runId: 'run-9' } },
      { type: 'HUMAN_INPUT_REQUEST', data: { stepId: 'step-1', runId: 'run-9', prompt: 'Approve?' } },
      { type: 'STEP_STARTED', data: { stepName: 'continue' } },
    ], 'svc-demo');

    expect(pending).toBeNull();
  });

  it('clears pending state once a response event is recorded', () => {
    const pending = extractInvokePendingHumanInput([
      { type: 'RUN_STARTED', data: { threadId: 'actor-9', runId: 'run-9' } },
      { type: 'HUMAN_INPUT_REQUEST', data: { stepId: 'step-1', runId: 'run-9', prompt: 'Approve?' } },
      { type: 'HUMAN_INPUT_RESPONSE', data: { stepId: 'step-1', runId: 'run-9', approved: true, userInput: 'Approve' } },
    ], 'svc-demo');

    expect(pending).toBeNull();
  });
});
