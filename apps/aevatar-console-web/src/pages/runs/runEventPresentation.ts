import { parseCustomEvent } from '@aevatar-react-sdk/agui';
import {
  type AGUIEvent,
  AGUIEventType,
  CustomEventName,
} from '@aevatar-react-sdk/types';
import {
  parseHumanInputRequestData,
  parseRunContextData,
  parseStepCompletedData,
  parseStepRequestData,
  parseWaitingSignalData,
} from '@/shared/agui/customEventData';
import { formatDateTime } from '@/shared/datetime/dateTime';

export type RunTransport = 'sse' | 'ws';
export type RunEventStatus = 'processing' | 'success' | 'error' | 'default';
export type RunEventCategory =
  | 'lifecycle'
  | 'message'
  | 'tool'
  | 'human_input'
  | 'human_approval'
  | 'wait_signal'
  | 'error'
  | 'state';

export type EventFilterValues = {
  categories: RunEventCategory[];
  query: string;
  errorsOnly: boolean;
};

export type RunEventRow = {
  key: string;
  timestamp: string;
  eventType: string;
  eventCategory: RunEventCategory;
  eventStatus: RunEventStatus;
  description: string;
  payloadPreview: string;
  payloadText: string;
};

const PAYLOAD_PREVIEW_LIMIT = 180;

export const eventStatusValueEnum = {
  processing: { text: 'Processing', status: 'Processing' },
  success: { text: 'Completed', status: 'Success' },
  error: { text: 'Error', status: 'Error' },
  default: { text: 'Observed', status: 'Default' },
} as const;

export const eventCategoryValueEnum = {
  lifecycle: { text: 'Lifecycle', status: 'Default' },
  message: { text: 'Message', status: 'Processing' },
  tool: { text: 'Tool', status: 'Processing' },
  human_input: { text: 'Human input', status: 'Warning' },
  human_approval: { text: 'Approval', status: 'Warning' },
  wait_signal: { text: 'Wait signal', status: 'Warning' },
  error: { text: 'Error', status: 'Error' },
  state: { text: 'State', status: 'Success' },
} as const;

export function isHumanApprovalSuspension(
  suspensionType?: string | null,
): boolean {
  return suspensionType?.toLowerCase().includes('approval') ?? false;
}

function normalizePayloadText(event: AGUIEvent): string {
  if (event.type === AGUIEventType.CUSTOM) {
    const custom = parseCustomEvent(event);
    return JSON.stringify(
      {
        type: event.type,
        name: custom.name,
        value: custom.data,
      },
      null,
      2,
    );
  }

  return JSON.stringify(event, null, 2);
}

function buildPayloadPreview(payloadText: string): string {
  const compact = payloadText.replace(/\s+/g, ' ').trim();
  if (compact.length <= PAYLOAD_PREVIEW_LIMIT) {
    return compact;
  }

  return `${compact.slice(0, PAYLOAD_PREVIEW_LIMIT - 3)}...`;
}

function formatTimestamp(timestamp?: number): string {
  return formatDateTime(timestamp, '');
}

function getEventDisplayType(event: AGUIEvent): string {
  if (event.type !== AGUIEventType.CUSTOM) {
    return event.type;
  }

  const custom = parseCustomEvent(event);
  return `CUSTOM · ${custom.name}`;
}

export function describeEvent(event: AGUIEvent): string {
  switch (event.type) {
    case AGUIEventType.RUN_STARTED:
      return `Run started on ${event.threadId}`;
    case AGUIEventType.RUN_FINISHED:
      return 'Run finished successfully.';
    case AGUIEventType.RUN_ERROR:
      return event.message;
    case AGUIEventType.STEP_STARTED:
      return `Step started: ${event.stepName}`;
    case AGUIEventType.STEP_FINISHED:
      return `Step finished: ${event.stepName}`;
    case AGUIEventType.TEXT_MESSAGE_START:
      return `Message started by ${event.role}`;
    case AGUIEventType.TEXT_MESSAGE_CONTENT:
      return event.delta;
    case AGUIEventType.TEXT_MESSAGE_END:
      return `Message completed: ${event.messageId}`;
    case AGUIEventType.STATE_SNAPSHOT:
      return 'State snapshot updated.';
    case AGUIEventType.TOOL_CALL_START:
      return `Tool started: ${event.toolName}`;
    case AGUIEventType.TOOL_CALL_END:
      return `Tool finished: ${event.toolCallId}`;
    case AGUIEventType.HUMAN_INPUT_REQUEST:
      return event.prompt;
    case AGUIEventType.HUMAN_INPUT_RESPONSE:
      return `Human input submitted for ${event.stepId}`;
    case AGUIEventType.CUSTOM: {
      const custom = parseCustomEvent(event);
      if (custom.name === CustomEventName.RunContext) {
        const data = parseRunContextData(custom.data);
        return `Context attached to actor ${data?.actorId ?? 'unknown'}`;
      }
      if (custom.name === CustomEventName.StepRequest) {
        const data = parseStepRequestData(custom.data);
        return `Step request: ${data?.stepId ?? 'unknown'} (${data?.stepType ?? 'unknown'})`;
      }
      if (custom.name === CustomEventName.StepCompleted) {
        const data = parseStepCompletedData(custom.data);
        return `Step completed: ${data?.stepId ?? 'unknown'} success=${String(data?.success)}`;
      }
      if (custom.name === CustomEventName.HumanInputRequest) {
        return (
          parseHumanInputRequestData(custom.data)?.prompt ??
          'Human input requested.'
        );
      }
      if (custom.name === CustomEventName.WaitingSignal) {
        const data = parseWaitingSignalData(custom.data);
        return `Waiting for signal ${data?.signalName ?? 'unknown'}`;
      }
      return custom.name;
    }
    default:
      return 'Unknown event';
  }
}

export function deriveEventStatus(event: AGUIEvent): RunEventStatus {
  switch (event.type) {
    case AGUIEventType.RUN_ERROR:
      return 'error';
    case AGUIEventType.RUN_FINISHED:
    case AGUIEventType.STEP_FINISHED:
    case AGUIEventType.HUMAN_INPUT_RESPONSE:
    case AGUIEventType.TOOL_CALL_END:
      return 'success';
    case AGUIEventType.RUN_STARTED:
    case AGUIEventType.STEP_STARTED:
    case AGUIEventType.TEXT_MESSAGE_START:
    case AGUIEventType.TEXT_MESSAGE_CONTENT:
    case AGUIEventType.TOOL_CALL_START:
    case AGUIEventType.HUMAN_INPUT_REQUEST:
      return 'processing';
    case AGUIEventType.CUSTOM: {
      const custom = parseCustomEvent(event);
      if (
        custom.name === CustomEventName.WaitingSignal ||
        custom.name === CustomEventName.HumanInputRequest
      ) {
        return 'processing';
      }
      if (custom.name === CustomEventName.StepCompleted) {
        return parseStepCompletedData(custom.data)?.success === false
          ? 'error'
          : 'success';
      }
      return 'default';
    }
    default:
      return 'default';
  }
}

export function deriveEventCategory(event: AGUIEvent): RunEventCategory {
  switch (event.type) {
    case AGUIEventType.RUN_ERROR:
      return 'error';
    case AGUIEventType.RUN_STARTED:
    case AGUIEventType.RUN_FINISHED:
    case AGUIEventType.STEP_STARTED:
    case AGUIEventType.STEP_FINISHED:
      return 'lifecycle';
    case AGUIEventType.TEXT_MESSAGE_START:
    case AGUIEventType.TEXT_MESSAGE_CONTENT:
    case AGUIEventType.TEXT_MESSAGE_END:
      return 'message';
    case AGUIEventType.TOOL_CALL_START:
    case AGUIEventType.TOOL_CALL_END:
      return 'tool';
    case AGUIEventType.STATE_SNAPSHOT:
      return 'state';
    case AGUIEventType.HUMAN_INPUT_REQUEST:
      return isHumanApprovalSuspension(event.suspensionType)
        ? 'human_approval'
        : 'human_input';
    case AGUIEventType.HUMAN_INPUT_RESPONSE:
      return 'human_input';
    case AGUIEventType.CUSTOM: {
      const custom = parseCustomEvent(event);
      if (custom.name === CustomEventName.WaitingSignal) {
        return 'wait_signal';
      }
      if (custom.name === CustomEventName.HumanInputRequest) {
        const data = parseHumanInputRequestData(custom.data);
        return isHumanApprovalSuspension(data?.suspensionType)
          ? 'human_approval'
          : 'human_input';
      }
      if (custom.name === CustomEventName.StepCompleted) {
        return parseStepCompletedData(custom.data)?.success === false
          ? 'error'
          : 'lifecycle';
      }
      return 'state';
    }
    default:
      return 'state';
  }
}

export function buildEventRow(event: AGUIEvent, index: number): RunEventRow {
  const payloadText = normalizePayloadText(event);

  return {
    key: `${event.type}-${event.timestamp ?? 'na'}-${index}`,
    timestamp: formatTimestamp(event.timestamp),
    eventType: getEventDisplayType(event),
    eventCategory: deriveEventCategory(event),
    eventStatus: deriveEventStatus(event),
    description: describeEvent(event),
    payloadPreview: buildPayloadPreview(payloadText),
    payloadText,
  };
}

export function buildEventRows(events: AGUIEvent[]): RunEventRow[] {
  return [...events]
    .reverse()
    .map((event, index) => buildEventRow(event, index));
}

export function filterEventRows(
  rows: RunEventRow[],
  filters: EventFilterValues,
): RunEventRow[] {
  const query = filters.query.trim().toLowerCase();

  return rows.filter((row) => {
    if (filters.errorsOnly && row.eventStatus !== 'error') {
      return false;
    }

    if (
      filters.categories.length > 0 &&
      !filters.categories.includes(row.eventCategory)
    ) {
      return false;
    }

    if (!query) {
      return true;
    }

    return [
      row.eventType,
      row.eventCategory,
      row.description,
      row.payloadPreview,
    ]
      .join(' ')
      .toLowerCase()
      .includes(query);
  });
}
