import { AGUIEventType, CustomEventName, type AGUIEvent } from '@aevatar-react-sdk/types';
import {
  buildPlaygroundStepSummaries,
  summarizePlaygroundSteps,
} from './stepSummary';

describe('stepSummary', () => {
  it('merges reference steps with timeline and custom checkpoint events', () => {
    const steps = buildPlaygroundStepSummaries({
      referenceSteps: [
        {
          id: 'draft',
          type: 'llm',
          targetRole: 'writer',
          parameters: {},
          next: 'approve',
          branches: {},
          children: [],
        },
        {
          id: 'approve',
          type: 'human_approval',
          targetRole: 'operator',
          parameters: {},
          next: '',
          branches: {},
          children: [],
        },
      ],
      actorTimeline: [
        {
          timestamp: '2026-03-12T00:00:01Z',
          stage: 'StepStarted',
          message: 'Draft step started.',
          agentId: 'writer',
          stepId: 'draft',
          stepType: 'llm',
          eventType: 'StepStarted',
          data: {},
        },
        {
          timestamp: '2026-03-12T00:00:03Z',
          stage: 'WaitingApproval',
          message: 'Approval checkpoint pending.',
          agentId: 'operator',
          stepId: 'approve',
          stepType: 'human_approval',
          eventType: 'HumanInputRequest',
          data: {},
        },
      ],
      events: [
        {
          type: AGUIEventType.CUSTOM,
          timestamp: 1,
          name: CustomEventName.StepRequest,
          value: {
            stepId: 'draft',
            stepType: 'llm',
          },
        } satisfies AGUIEvent,
        {
          type: AGUIEventType.CUSTOM,
          timestamp: 2,
          name: CustomEventName.StepCompleted,
          value: {
            stepId: 'draft',
            success: true,
          },
        } satisfies AGUIEvent,
        {
          type: AGUIEventType.CUSTOM,
          timestamp: 3,
          name: CustomEventName.HumanInputRequest,
          value: {
            stepId: 'approve',
            suspensionType: 'approval',
            prompt: 'Approve the result',
          },
        } satisfies AGUIEvent,
      ],
    });

    expect(steps).toHaveLength(2);
    expect(steps[0]).toMatchObject({
      stepId: 'draft',
      source: 'merged',
      status: 'success',
      statusLabel: 'Completed',
      targetRole: 'writer',
      stepType: 'llm',
    });
    expect(steps[1]).toMatchObject({
      stepId: 'approve',
      source: 'merged',
      status: 'waiting',
      statusLabel: 'Waiting',
      checkpointLabel: 'Approval',
      targetRole: 'operator',
    });

    expect(summarizePlaygroundSteps(steps)).toEqual({
      totalReferenceSteps: 2,
      observedSteps: 2,
      runningSteps: 0,
      waitingSteps: 1,
      successfulSteps: 1,
      failedSteps: 0,
    });
  });

  it('adds runtime-only steps when no reference definition is available', () => {
    const steps = buildPlaygroundStepSummaries({
      events: [
        {
          type: AGUIEventType.CUSTOM,
          timestamp: 5,
          name: CustomEventName.StepCompleted,
          value: {
            stepId: 'runtime_only',
            success: false,
          },
        } satisfies AGUIEvent,
      ],
    });

    expect(steps).toHaveLength(1);
    expect(steps[0]).toMatchObject({
      stepId: 'runtime_only',
      source: 'runtime',
      status: 'error',
      statusLabel: 'Failed',
    });
  });
});
