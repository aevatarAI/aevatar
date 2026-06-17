import { buildExecutionTrace } from './execution';
import type { StudioExecutionDetail } from './models';

function createExecutionDetail(
  frames: StudioExecutionDetail['frames'],
): StudioExecutionDetail {
  return {
    actorId: 'actor-1',
    completedAtUtc: '2026-06-08T00:00:03Z',
    error: null,
    executionId: 'execution-1',
    frames,
    output: '',
    prompt: 'Run the workflow',
    serviceId: null,
    startedAtUtc: '2026-06-08T00:00:00Z',
    status: 'succeeded',
    workflowName: 'Workflow Alpha',
  };
}

describe('buildExecutionTrace', () => {
  it('uses business output text for run-finished logs instead of raw result JSON', () => {
    const trace = buildExecutionTrace(
      createExecutionDetail([
        {
          receivedAtUtc: '2026-06-08T00:00:01Z',
          payload: JSON.stringify({
            runFinished: {
              result: {
                output: 'Final answer',
                debug: {
                  stateVersion: 7,
                },
              },
            },
          }),
        },
      ]),
    );

    const outputLog = trace?.logs.find((log) => log.category === 'output');
    expect(outputLog?.clipboardText).toBe('Final answer');
    expect(outputLog?.previewText).toBe('Final answer');
    expect(outputLog?.payloadText).toContain('"stateVersion": 7');
  });
});
