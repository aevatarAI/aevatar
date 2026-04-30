import type {
  RuntimeEvent,
  RuntimeStepInfo,
  RuntimeToolCallInfo,
} from '@/shared/agui/runtimeEventSemantics';
import type { StudioObserveSessionSeed } from '@/shared/studio/observeSession';

export type InvokeResultState = {
  readonly actorId: string;
  readonly assistantText: string;
  readonly commandId: string;
  readonly correlationId: string;
  readonly endpointId: string;
  readonly error: string;
  readonly errorCode: string;
  readonly eventCount: number;
  readonly events: RuntimeEvent[];
  readonly finalOutput: string;
  readonly mode: 'stream' | 'invoke';
  readonly responseJson: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly status: 'idle' | 'running' | 'success' | 'error';
  readonly steps: RuntimeStepInfo[];
  readonly thinking: string;
  readonly toolCalls: RuntimeToolCallInfo[];
};

export type CurrentRunRequest = {
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly startedAt: number;
};

export type StudioInvokeChatMessage = {
  readonly content: string;
  readonly error?: string;
  readonly id: string;
  readonly role: 'assistant' | 'user';
  readonly status: 'complete' | 'error' | 'streaming';
  readonly thinking?: string;
  readonly timestamp: number;
};

export type InvokeHistoryEntry = {
  readonly completedAt: number;
  readonly createdAt: number;
  readonly endpointId: string;
  readonly endpointLabel: string;
  readonly errorDetail: string;
  readonly eventCount: number;
  readonly id: string;
  readonly mode: 'stream' | 'invoke';
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly runId: string;
  readonly serviceId: string;
  readonly startedAt: number;
  readonly status: 'success' | 'error';
  readonly summary: string;
  readonly snapshot: {
    readonly chatMessages: StudioInvokeChatMessage[];
    readonly result: InvokeResultState;
  };
};

export type StudioInvokeCurrentRunViewModel = {
  readonly hasData: boolean;
  readonly observeSessionSeed: StudioObserveSessionSeed | null;
  readonly rawOutput: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function toIsoTimestamp(value: number | null | undefined): string {
  return typeof value === 'number' && Number.isFinite(value)
    ? new Date(value).toISOString()
    : '';
}

export function createIdleInvokeResult(): InvokeResultState {
  return {
    actorId: '',
    assistantText: '',
    commandId: '',
    correlationId: '',
    endpointId: '',
    error: '',
    errorCode: '',
    eventCount: 0,
    events: [],
    finalOutput: '',
    mode: 'invoke',
    responseJson: '',
    runId: '',
    serviceId: '',
    status: 'idle',
    steps: [],
    thinking: '',
    toolCalls: [],
  };
}

export function cloneInvokeResult(result: InvokeResultState): InvokeResultState {
  return {
    ...result,
    events: [...result.events],
    steps: [...result.steps],
    toolCalls: [...result.toolCalls],
  };
}

function hasCurrentRunData(input: {
  readonly chatMessageCount: number;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly invokeResult: InvokeResultState;
}): boolean {
  const { chatMessageCount, currentRunRequest, invokeResult } = input;
  return (
    invokeResult.status !== 'idle' ||
    Boolean(currentRunRequest?.prompt) ||
    Boolean(currentRunRequest?.payloadBase64) ||
    Boolean(currentRunRequest?.payloadTypeUrl) ||
    Boolean(invokeResult.runId) ||
    Boolean(invokeResult.commandId) ||
    Boolean(invokeResult.correlationId) ||
    Boolean(invokeResult.actorId) ||
    Boolean(invokeResult.errorCode) ||
    Boolean(invokeResult.error) ||
    Boolean(invokeResult.finalOutput) ||
    Boolean(invokeResult.responseJson) ||
    Boolean(invokeResult.assistantText) ||
    chatMessageCount > 0 ||
    invokeResult.events.length > 0
  );
}

function buildObserveSessionSeed(input: {
  readonly activeRunCompletedAt: number | null;
  readonly currentMemberLabel: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly invokeResult: InvokeResultState;
  readonly isChatEndpoint: boolean;
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly selectedEndpointId: string;
  readonly selectedServiceDisplayName?: string;
  readonly selectedServiceId: string;
}): StudioObserveSessionSeed | null {
  const serviceId = trimOptional(input.selectedServiceId);
  const endpointId = trimOptional(input.selectedEndpointId);
  if (!serviceId || !endpointId || !input.currentRunHasData) {
    return null;
  }

  return {
    actorId: trimOptional(input.invokeResult.actorId),
    assistantText: input.invokeResult.assistantText,
    commandId: trimOptional(input.invokeResult.commandId),
    correlationId: trimOptional(input.invokeResult.correlationId),
    completedAtUtc: toIsoTimestamp(input.activeRunCompletedAt) || null,
    endpointId,
    error: input.invokeResult.error,
    errorCode: input.invokeResult.errorCode,
    events: [...input.invokeResult.events],
    finalOutput: input.invokeResult.finalOutput,
    mode: input.invokeResult.mode,
    payloadBase64:
      input.currentRunRequest?.payloadBase64 ||
      (!input.isChatEndpoint ? input.payloadBase64.trim() : '') ||
      '',
    payloadTypeUrl:
      input.currentRunRequest?.payloadTypeUrl ||
      (!input.isChatEndpoint ? input.payloadTypeUrl.trim() : '') ||
      '',
    prompt: input.currentRunRequest?.prompt || '',
    runId: trimOptional(input.invokeResult.runId),
    serviceId,
    serviceLabel:
      trimOptional(input.selectedServiceDisplayName) || input.currentMemberLabel,
    startedAtUtc: toIsoTimestamp(input.currentRunRequest?.startedAt) || '',
    status:
      input.invokeResult.status === 'error'
        ? 'error'
        : input.invokeResult.status === 'success'
          ? 'success'
          : 'running',
  };
}

function buildRawOutput(input: {
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly invokeResult: InvokeResultState;
  readonly selectedEndpointId: string;
  readonly selectedServiceId: string;
}): string {
  if (input.invokeResult.responseJson) {
    return input.invokeResult.responseJson;
  }

  if (!input.currentRunHasData) {
    return '';
  }

  return JSON.stringify(
    {
      actorId: input.invokeResult.actorId || undefined,
      commandId: input.invokeResult.commandId || undefined,
      correlationId: input.invokeResult.correlationId || undefined,
      endpointId:
        input.invokeResult.endpointId || input.selectedEndpointId || undefined,
      errorCode: input.invokeResult.errorCode || undefined,
      error: input.invokeResult.error || undefined,
      eventCount:
        input.invokeResult.eventCount || input.invokeResult.events.length,
      finalOutput: input.invokeResult.finalOutput || undefined,
      mode: input.invokeResult.mode || input.currentRunRequest?.mode,
      runId: input.invokeResult.runId || undefined,
      serviceId:
        input.invokeResult.serviceId || input.selectedServiceId || undefined,
      status: input.invokeResult.status,
      stepCount: input.invokeResult.steps.length,
      toolCallCount: input.invokeResult.toolCalls.length,
    },
    null,
    2,
  );
}

export function buildStudioInvokeCurrentRunViewModel(input: {
  readonly activeRunCompletedAt: number | null;
  readonly chatMessageCount: number;
  readonly currentMemberLabel: string;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly invokeResult: InvokeResultState;
  readonly isChatEndpoint: boolean;
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly selectedEndpointId: string;
  readonly selectedServiceDisplayName?: string;
  readonly selectedServiceId: string;
}): StudioInvokeCurrentRunViewModel {
  const currentRunHasData = hasCurrentRunData({
    chatMessageCount: input.chatMessageCount,
    currentRunRequest: input.currentRunRequest,
    invokeResult: input.invokeResult,
  });

  return {
    hasData: currentRunHasData,
    observeSessionSeed: buildObserveSessionSeed({
      activeRunCompletedAt: input.activeRunCompletedAt,
      currentMemberLabel: input.currentMemberLabel,
      currentRunHasData,
      currentRunRequest: input.currentRunRequest,
      invokeResult: input.invokeResult,
      isChatEndpoint: input.isChatEndpoint,
      payloadBase64: input.payloadBase64,
      payloadTypeUrl: input.payloadTypeUrl,
      selectedEndpointId: input.selectedEndpointId,
      selectedServiceDisplayName: input.selectedServiceDisplayName,
      selectedServiceId: input.selectedServiceId,
    }),
    rawOutput: buildRawOutput({
      currentRunHasData,
      currentRunRequest: input.currentRunRequest,
      invokeResult: input.invokeResult,
      selectedEndpointId: input.selectedEndpointId,
      selectedServiceId: input.selectedServiceId,
    }),
  };
}
