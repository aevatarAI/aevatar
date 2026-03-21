import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from '@aevatar-react-sdk/types';
import {
  buildEventRows,
  filterEventRows,
  type EventFilterValues,
} from './runEventPresentation';

function createDefaultFilters(
  overrides?: Partial<EventFilterValues>,
): EventFilterValues {
  return {
    categories: [],
    query: '',
    errorsOnly: false,
    ...overrides,
  };
}

describe('runEventPresentation', () => {
  it('classifies approval and wait-signal events correctly', () => {
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.HUMAN_INPUT_REQUEST,
        runId: 'run-1',
        stepId: 'approve',
        suspensionType: 'human_approval',
        prompt: 'Approve release?',
        timeoutSeconds: 300,
      },
      {
        type: AGUIEventType.CUSTOM,
        name: CustomEventName.WaitingSignal,
        value: {
          runId: 'run-1',
          stepId: 'wait',
          signalName: 'deployment_ready',
        },
      },
    ];

    const rows = buildEventRows(events);

    expect(rows[1].eventCategory).toBe('human_approval');
    expect(rows[0].eventCategory).toBe('wait_signal');
  });

  it('filters rows by category and free text', () => {
    const events: AGUIEvent[] = [
      {
        type: AGUIEventType.RUN_ERROR,
        message: 'Workflow crashed',
        code: 'RUN_FAILED',
      },
      {
        type: AGUIEventType.TEXT_MESSAGE_CONTENT,
        messageId: 'msg-1',
        delta: 'hello world',
      },
    ];

    const rows = buildEventRows(events);
    const errorRows = filterEventRows(
      rows,
      createDefaultFilters({ categories: ['error'] }),
    );
    const messageRows = filterEventRows(
      rows,
      createDefaultFilters({ query: 'hello world' }),
    );

    expect(errorRows).toHaveLength(1);
    expect(errorRows[0].eventStatus).toBe('error');
    expect(messageRows).toHaveLength(1);
    expect(messageRows[0].eventCategory).toBe('message');
  });
});
