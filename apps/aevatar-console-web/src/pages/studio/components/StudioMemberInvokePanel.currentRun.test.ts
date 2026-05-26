import {
  buildStudioInvokeCurrentRunViewModel,
  createIdleInvokeResult,
} from './StudioMemberInvokePanel.currentRun';

describe('StudioMemberInvokePanel current run model', () => {
  it('returns an empty model before the first invoke', () => {
    const model = buildStudioInvokeCurrentRunViewModel({
      activeRunCompletedAt: null,
      chatMessageCount: 0,
      currentMemberLabel: 'script-1',
      currentRunRequest: null,
      invokeResult: createIdleInvokeResult(),
      isChatEndpoint: false,
      payloadBase64: '',
      payloadTypeUrl: '',
      selectedEndpointId: 'command',
      selectedServiceDisplayName: 'script-1',
      selectedServiceId: 'script-1',
    });

    expect(model.hasData).toBe(false);
    expect(model.observeSessionSeed).toBeNull();
    expect(model.rawOutput).toBe('');
  });

  it('builds observe seed and raw output with separated identifiers', () => {
    const startedAt = Date.parse('2026-04-30T08:00:00Z');
    const completedAt = Date.parse('2026-04-30T08:00:01Z');
    const model = buildStudioInvokeCurrentRunViewModel({
      activeRunCompletedAt: completedAt,
      chatMessageCount: 0,
      currentMemberLabel: 'script-1',
      currentRunRequest: {
        mode: 'invoke',
        payloadBase64: 'cGF5bG9hZA==',
        payloadTypeUrl: 'type.googleapis.com/example.Command',
        prompt: 'hello',
        startedAt,
      },
      invokeResult: {
        ...createIdleInvokeResult(),
        actorId: 'actor-1',
        commandId: 'cmd-1',
        correlationId: 'corr-1',
        endpointId: 'command',
        errorCode: 'ERR_NONE',
        eventCount: 2,
        finalOutput: 'ok',
        runId: 'run-1',
        serviceId: 'script-1',
        status: 'success',
      },
      isChatEndpoint: false,
      payloadBase64: '',
      payloadTypeUrl: '',
      selectedEndpointId: 'command',
      selectedServiceDisplayName: 'Script One',
      selectedServiceId: 'script-1',
    });

    expect(model.hasData).toBe(true);
    expect(model.observeSessionSeed).toEqual(
      expect.objectContaining({
        actorId: 'actor-1',
        commandId: 'cmd-1',
        correlationId: 'corr-1',
        completedAtUtc: '2026-04-30T08:00:01.000Z',
        endpointId: 'command',
        errorCode: 'ERR_NONE',
        payloadTypeUrl: 'type.googleapis.com/example.Command',
        prompt: 'hello',
        runId: 'run-1',
        serviceId: 'script-1',
        serviceLabel: 'Script One',
        startedAtUtc: '2026-04-30T08:00:00.000Z',
        status: 'success',
      }),
    );
    expect(JSON.parse(model.rawOutput)).toEqual(
      expect.objectContaining({
        actorId: 'actor-1',
        commandId: 'cmd-1',
        correlationId: 'corr-1',
        endpointId: 'command',
        errorCode: 'ERR_NONE',
        runId: 'run-1',
        serviceId: 'script-1',
        status: 'success',
      }),
    );
  });
});
