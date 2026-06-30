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
import { t } from '@/shared/i18n/messages';
import { getUserFacingIdentifierLabel } from '@/shared/ui/userFacingIdentifiers';

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
  agentId: string;
  key: string;
  stepId: string;
  stepType: string;
  timestamp: string;
  timelineKey: string;
  timelineLabel: string;
  eventType: string;
  eventCategory: RunEventCategory;
  eventStatus: RunEventStatus;
  description: string;
  payloadPreview: string;
  payloadText: string;
};

export type RunTimelineGroup = {
  eventCount: number;
  items: RunEventRow[];
  key: string;
  latestTimestamp: string;
  label: string;
  status: RunEventStatus;
};

const PAYLOAD_PREVIEW_LIMIT = 180;

type JsonRecord = Record<string, unknown>;

const eventStatusLabelMessages: Record<RunEventStatus, { id: string; defaultMessage: string }> = {
  default: {
    id: 'pages.runs.runeventpresentation.observed',
    defaultMessage: 'Observed',
  },
  error: {
    id: 'pages.runs.runeventpresentation.error',
    defaultMessage: 'Error',
  },
  processing: {
    id: 'pages.runs.runeventpresentation.processing',
    defaultMessage: 'Processing',
  },
  success: {
    id: 'pages.runs.runeventpresentation.completed',
    defaultMessage: 'Completed',
  },
};

const eventCategoryLabelMessages: Record<RunEventCategory, { id: string; defaultMessage: string }> = {
  error: {
    id: 'pages.runs.runeventpresentation.error.2',
    defaultMessage: 'Error',
  },
  human_approval: {
    id: 'pages.runs.runeventpresentation.approval',
    defaultMessage: 'Approval',
  },
  human_input: {
    id: 'pages.runs.runeventpresentation.human.input',
    defaultMessage: 'Human input',
  },
  lifecycle: {
    id: 'pages.runs.runeventpresentation.lifecycle',
    defaultMessage: 'Lifecycle',
  },
  message: {
    id: 'pages.runs.runeventpresentation.message',
    defaultMessage: 'Message',
  },
  state: {
    id: 'pages.runs.runeventpresentation.state',
    defaultMessage: 'State',
  },
  tool: {
    id: 'pages.runs.runeventpresentation.tool',
    defaultMessage: 'Tool',
  },
  wait_signal: {
    id: 'pages.runs.runeventpresentation.wait.signal',
    defaultMessage: 'Wait signal',
  },
};

export function getEventStatusLabel(status: RunEventStatus): string {
  return t(
    eventStatusLabelMessages[status].id,
    eventStatusLabelMessages[status].defaultMessage,
  );
}

export function getEventCategoryLabel(category: RunEventCategory): string {
  return t(
    eventCategoryLabelMessages[category].id,
    eventCategoryLabelMessages[category].defaultMessage,
  );
}

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

function readOptionalEventString(event: AGUIEvent, key: string): string {
  const candidate = (event as unknown as Record<string, unknown>)[key];
  return typeof candidate === 'string' ? candidate : '';
}

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return undefined;
  }

  return value as JsonRecord;
}

function readOptionalRecordString(
  record: JsonRecord | undefined,
  key: string,
): string {
  if (!record) {
    return '';
  }

  const candidate = record[key];
  return typeof candidate === 'string' ? candidate : '';
}

export function extractRunFinishedOutput(events: AGUIEvent[]): string {
  const latestRunFinished = [...events]
    .reverse()
    .find((event) => event.type === AGUIEventType.RUN_FINISHED);

  if (!latestRunFinished) {
    return '';
  }

  const result = asRecord(
    (latestRunFinished as unknown as Record<string, unknown>).result,
  );

  // Refactor (iter98/cluster-790): Old: missed-live fallback expected generic result keys after backend text synthesis. New: RunFinished.result is typed GAgentDraftRunResultPayload, and consumers read result.output directly.
  return (
    readOptionalRecordString(result, 'output') ||
    readOptionalRecordString(result, 'Output') ||
    ''
  );
}

export function resolveRunMessageFallback(
  events: AGUIEvent[],
  snapshotLastOutput?: string | null,
): string {
  const runFinishedOutput = extractRunFinishedOutput(events).trim();
  if (runFinishedOutput) {
    return runFinishedOutput;
  }

  return typeof snapshotLastOutput === 'string'
    ? snapshotLastOutput.trim()
    : '';
}

function mergeTimelineStatus(
  current: RunEventStatus,
  incoming: RunEventStatus,
): RunEventStatus {
  if (current === 'error' || incoming === 'error') {
    return 'error';
  }

  if (current === 'processing' || incoming === 'processing') {
    return 'processing';
  }

  if (current === 'success' || incoming === 'success') {
    return 'success';
  }

  return 'default';
}

function buildTimelineContext(event: AGUIEvent): {
  agentId: string;
  stepId: string;
  stepType: string;
  timelineKey: string;
  timelineLabel: string;
} {
  const defaultAgentId = readOptionalEventString(event, 'threadId');
  const defaultStepId = readOptionalEventString(event, 'stepId');
  const defaultStepType = readOptionalEventString(event, 'stepType');

  const formatStepTimelineLabel = (
    value: string,
    fallback: string,
    prefix = 'Step',
  ): string => {
    const visibleValue = getUserFacingIdentifierLabel(value, '').trim();
    return visibleValue ? `${prefix} · ${visibleValue}` : fallback;
  };

  if (event.type === AGUIEventType.HUMAN_INPUT_REQUEST) {
    const approval = isHumanApprovalSuspension(event.suspensionType);
    return {
      agentId: readOptionalEventString(event, 'agentId') || defaultAgentId,
      stepId: event.stepId || '',
      stepType: event.suspensionType || '',
      timelineKey: event.stepId ? `step:${event.stepId}` : 'interaction:human',
      timelineLabel: event.stepId
        ? `Interaction · ${event.stepId}`
        : approval
          ? 'Human approval'
          : 'Human input',
    };
  }

  if (event.type === AGUIEventType.HUMAN_INPUT_RESPONSE) {
    return {
      agentId: readOptionalEventString(event, 'agentId') || defaultAgentId,
      stepId: event.stepId || '',
      stepType: defaultStepType,
      timelineKey: event.stepId ? `step:${event.stepId}` : 'interaction:human',
      timelineLabel: formatStepTimelineLabel(
        event.stepId || '',
        event.stepId ? 'Interaction step' : 'Human input',
        'Interaction',
      ),
    };
  }

  if (event.type === AGUIEventType.CUSTOM) {
    const custom = parseCustomEvent(event);

    if (custom.name === CustomEventName.RunContext) {
      const data = parseRunContextData(custom.data);
      return {
        agentId: data?.actorId ?? defaultAgentId,
        stepId: '',
        stepType: '',
        timelineKey: 'run:context',
        timelineLabel: 'Run context',
      };
    }

    if (custom.name === CustomEventName.StepRequest) {
      const data = parseStepRequestData(custom.data);
      const stepId = data?.stepId ?? '';
      const stepType = data?.stepType ?? '';
      return {
        agentId: data?.targetRole ?? defaultAgentId,
        stepId,
        stepType,
        timelineKey: stepId ? `step:${stepId}` : 'step:request',
        timelineLabel: formatStepTimelineLabel(
          stepId,
          stepType ? `Step request · ${stepType}` : 'Step request',
        ),
      };
    }

    if (custom.name === CustomEventName.StepCompleted) {
      const data = parseStepCompletedData(custom.data);
      const stepId = data?.stepId ?? '';
      return {
        agentId: defaultAgentId,
        stepId,
        stepType: '',
        timelineKey: stepId ? `step:${stepId}` : 'step:completed',
        timelineLabel: formatStepTimelineLabel(stepId, 'Step completed'),
      };
    }

    if (custom.name === CustomEventName.HumanInputRequest) {
      const data = parseHumanInputRequestData(custom.data);
      const stepId = data?.stepId ?? '';
      const approval = isHumanApprovalSuspension(data?.suspensionType);
      return {
        agentId: defaultAgentId,
        stepId,
        stepType: data?.suspensionType ?? '',
        timelineKey: stepId ? `step:${stepId}` : 'interaction:human',
        timelineLabel: stepId
          ? `Interaction · ${stepId}`
          : approval
            ? 'Human approval'
            : 'Human input',
      };
    }

    if (custom.name === CustomEventName.WaitingSignal) {
      const data = parseWaitingSignalData(custom.data);
      const stepId = data?.stepId ?? '';
      return {
        agentId: defaultAgentId,
        stepId,
        stepType: '',
        timelineKey: stepId ? `step:${stepId}` : 'interaction:signal',
        timelineLabel: stepId
          ? `Waiting signal · ${stepId}`
          : 'Waiting signal',
      };
    }

    return {
      agentId: defaultAgentId,
      stepId: '',
      stepType: '',
      timelineKey: `custom:${custom.name}`,
      timelineLabel: custom.name,
    };
  }

  if (defaultStepId) {
    return {
      agentId: readOptionalEventString(event, 'agentId') || defaultAgentId,
      stepId: defaultStepId,
      stepType: defaultStepType,
      timelineKey: `step:${defaultStepId}`,
      timelineLabel: `Step · ${defaultStepId}`,
    };
  }

  if (event.type === AGUIEventType.RUN_STARTED || event.type === AGUIEventType.RUN_FINISHED) {
    return {
      agentId: defaultAgentId,
      stepId: '',
      stepType: '',
      timelineKey: 'run:lifecycle',
      timelineLabel: 'Run lifecycle',
    };
  }

  if (
    event.type === AGUIEventType.TEXT_MESSAGE_START ||
    event.type === AGUIEventType.TEXT_MESSAGE_CONTENT ||
    event.type === AGUIEventType.TEXT_MESSAGE_END
  ) {
    const role = readOptionalEventString(event, 'role');
    return {
      agentId: role || defaultAgentId,
      stepId: '',
      stepType: '',
      timelineKey: role ? `message:${role}` : 'message:stream',
      timelineLabel: role ? `Message · ${role}` : 'Message stream',
    };
  }

  if (
    event.type === AGUIEventType.TOOL_CALL_START ||
    event.type === AGUIEventType.TOOL_CALL_END
  ) {
    const toolName = readOptionalEventString(event, 'toolName');
    return {
      agentId: defaultAgentId,
      stepId: '',
      stepType: '',
      timelineKey: toolName ? `tool:${toolName}` : 'tool:call',
      timelineLabel: toolName ? `Tool · ${toolName}` : 'Tool call',
    };
  }

  return {
    agentId: readOptionalEventString(event, 'agentId') || defaultAgentId,
    stepId: '',
    stepType: defaultStepType,
    timelineKey: `category:${deriveEventCategory(event)}`,
    timelineLabel: deriveEventCategory(event),
  };
}

export function describeEvent(event: AGUIEvent): string {
  switch (event.type) {
    case AGUIEventType.RUN_STARTED:
      return 'Run started.';
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
      return 'Message completed.';
    case AGUIEventType.STATE_SNAPSHOT:
      return 'State snapshot updated.';
    case AGUIEventType.TOOL_CALL_START:
      return `Tool started: ${event.toolName}`;
    case AGUIEventType.TOOL_CALL_END:
      return 'Tool finished.';
    case AGUIEventType.HUMAN_INPUT_REQUEST:
      return event.prompt;
    case AGUIEventType.HUMAN_INPUT_RESPONSE:
      return 'Human input submitted.';
    case AGUIEventType.CUSTOM: {
      const custom = parseCustomEvent(event);
      if (custom.name === CustomEventName.RunContext) {
        return 'Context attached to runtime actor.';
      }
      if (custom.name === CustomEventName.StepRequest) {
        const data = parseStepRequestData(custom.data);
        return `Step request: ${data?.stepType ?? 'unknown'}`;
      }
      if (custom.name === CustomEventName.StepCompleted) {
        const data = parseStepCompletedData(custom.data);
        return `Step completed: success=${String(data?.success)}`;
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
  const timelineContext = buildTimelineContext(event);

  return {
    agentId: timelineContext.agentId,
    key: `${event.type}-${event.timestamp ?? 'na'}-${index}`,
    stepId: timelineContext.stepId,
    stepType: timelineContext.stepType,
    timestamp: formatTimestamp(event.timestamp),
    timelineKey: timelineContext.timelineKey,
    timelineLabel: timelineContext.timelineLabel,
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
      row.agentId,
      row.eventType,
      row.eventCategory,
      row.description,
      row.payloadPreview,
      row.stepId,
      row.stepType,
      row.timelineLabel,
    ]
      .join(' ')
      .toLowerCase()
      .includes(query);
  });
}

export function buildTimelineGroups(rows: RunEventRow[]): RunTimelineGroup[] {
  const groups: RunTimelineGroup[] = [];

  for (const row of rows) {
    const current = groups[groups.length - 1];
    if (current && current.items[0]?.timelineKey === row.timelineKey) {
      current.items.push(row);
      current.eventCount += 1;
      current.status = mergeTimelineStatus(current.status, row.eventStatus);
      continue;
    }

    groups.push({
      eventCount: 1,
      items: [row],
      key: `${row.timelineKey}:${groups.length}`,
      latestTimestamp: row.timestamp,
      label: row.timelineLabel,
      status: row.eventStatus,
    });
  }

  return groups;
}
