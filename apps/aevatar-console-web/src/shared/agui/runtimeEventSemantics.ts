import { parseCustomEvent } from "@aevatar-react-sdk/agui";
import {
  AGUIEventType,
  CustomEventName,
  type AGUIEvent,
} from "@aevatar-react-sdk/types";
import {
  parseRunContextData,
  parseStepCompletedData,
  parseStepRequestData,
} from "@/shared/agui/customEventData";
import { normalizeBackendSseFrame } from "@/shared/agui/sseFrameNormalizer";

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return undefined;
  }

  return value as JsonRecord;
}

function readOptionalString(
  record: JsonRecord | undefined,
  ...keys: string[]
): string {
  if (!record) {
    return "";
  }

  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim()) {
      return value;
    }
  }

  return "";
}

export type RuntimeEvent = AGUIEvent;

export type RuntimeStepInfo = {
  id?: string;
  name: string;
  status: "running" | "done" | "error";
  startedAt: number;
  finishedAt?: number;
  output?: string;
  error?: string;
  stepType?: string;
};

export type RuntimeToolCallInfo = {
  id: string;
  name: string;
  status: "running" | "done" | "error";
  startedAt: number;
  finishedAt?: number;
  result?: string;
  error?: string;
};

export type RuntimeToolApprovalRequestInfo = {
  requestId: string;
  toolName: string;
  toolCallId: string;
  argumentsJson: string;
  isDestructive: boolean;
  timeoutSeconds: number;
};

export type RuntimeEventAccumulator = {
  actorId: string;
  assistantText: string;
  commandId: string;
  errorText: string;
  events: RuntimeEvent[];
  pendingApproval?: RuntimeToolApprovalRequestInfo;
  runId: string;
  steps: RuntimeStepInfo[];
  thinking: string;
  toolCalls: RuntimeToolCallInfo[];
};

export function normalizeRuntimeFrame(raw: unknown): RuntimeEvent | null {
  return normalizeBackendSseFrame(raw);
}

export function describeRuntimeEvent(event: RuntimeEvent): string {
  if (event.type !== AGUIEventType.CUSTOM) {
    return event.type;
  }

  return `CUSTOM · ${parseCustomEvent(event).name}`;
}

export function extractRunContext(
  event: RuntimeEvent
): { actorId?: string; commandId?: string } | null {
  if (event.type !== AGUIEventType.CUSTOM) {
    return null;
  }

  const custom = parseCustomEvent(event);
  if (
    custom.name !== CustomEventName.RunContext &&
    custom.name !== "aevatar.run.context"
  ) {
    return null;
  }

  const data = parseRunContextData(custom.data);
  if (!data) {
    return null;
  }

  return {
    actorId: data.actorId,
    commandId: data.commandId,
  };
}

export function extractStepCompleted(
  event: RuntimeEvent
): { error?: string; output?: string; stepId: string; success?: boolean } | null {
  if (event.type !== AGUIEventType.CUSTOM) {
    return null;
  }

  const custom = parseCustomEvent(event);
  if (
    custom.name !== CustomEventName.StepCompleted &&
    custom.name !== "aevatar.step.completed"
  ) {
    return null;
  }

  const data = parseStepCompletedData(custom.data);
  const stepId = data?.stepId?.trim() || "";
  if (!stepId) {
    return null;
  }

  return {
    error: data?.error,
    output: data?.output,
    stepId,
    success: data?.success,
  };
}

export function extractStepCompletedOutput(event: RuntimeEvent): string | null {
  return extractStepCompleted(event)?.output ?? null;
}

export function extractReasoningDelta(event: RuntimeEvent): string | null {
  if (event.type !== AGUIEventType.CUSTOM) {
    return null;
  }

  const custom = parseCustomEvent(event);
  if (custom.name !== "aevatar.llm.reasoning") {
    return null;
  }

  const payload = asRecord(custom.data);
  const delta = readOptionalString(payload, "delta", "Delta");
  return delta || null;
}

export function extractStepRequest(
  event: RuntimeEvent
): { input: string; stepId: string; stepType: string } | null {
  if (event.type !== AGUIEventType.CUSTOM) {
    return null;
  }

  const custom = parseCustomEvent(event);
  if (
    custom.name !== CustomEventName.StepRequest &&
    custom.name !== "aevatar.step.request"
  ) {
    return null;
  }

  const data = parseStepRequestData(custom.data);
  if (!data?.stepId?.trim() && !data?.stepType?.trim()) {
    return null;
  }

  return {
    input: data?.input?.trim() || "",
    stepId: data?.stepId?.trim() || "",
    stepType: data?.stepType?.trim() || "",
  };
}

export function extractToolApprovalRequest(
  event: RuntimeEvent
): RuntimeToolApprovalRequestInfo | null {
  if (String(event.type) === "TOOL_APPROVAL_REQUEST") {
    const requestId = readOptionalString(
      event as unknown as JsonRecord,
      "requestId",
      "request_id"
    );
    if (!requestId) {
      return null;
    }

    return {
      argumentsJson: readOptionalString(
        event as unknown as JsonRecord,
        "argumentsJson",
        "arguments_json"
      ),
      isDestructive: Boolean(
        (event as unknown as JsonRecord).isDestructive ??
          (event as unknown as JsonRecord).is_destructive
      ),
      requestId,
      timeoutSeconds:
        typeof (event as unknown as JsonRecord).timeoutSeconds === "number"
          ? ((event as unknown as JsonRecord).timeoutSeconds as number)
          : typeof (event as unknown as JsonRecord).timeout_seconds === "number"
            ? ((event as unknown as JsonRecord).timeout_seconds as number)
            : 15,
      toolCallId: readOptionalString(
        event as unknown as JsonRecord,
        "toolCallId",
        "tool_call_id"
      ),
      toolName: readOptionalString(
        event as unknown as JsonRecord,
        "toolName",
        "tool_name"
      ),
    };
  }

  if (event.type !== AGUIEventType.CUSTOM) {
    return null;
  }

  const custom = parseCustomEvent(event);
  if (custom.name !== "TOOL_APPROVAL_REQUEST") {
    return null;
  }

  const raw = asRecord(custom.data);
  const payload = asRecord(raw?.value) ?? raw;
  if (!payload) {
    return null;
  }

  const requestId = readOptionalString(payload, "requestId", "request_id");
  if (!requestId) {
    return null;
  }

  return {
    argumentsJson: readOptionalString(payload, "argumentsJson", "arguments_json"),
    isDestructive: Boolean(
      payload.isDestructive ?? payload.is_destructive ?? false
    ),
    requestId,
    timeoutSeconds:
      typeof payload.timeoutSeconds === "number"
        ? payload.timeoutSeconds
        : typeof payload.timeout_seconds === "number"
          ? payload.timeout_seconds
          : 15,
    toolCallId: readOptionalString(payload, "toolCallId", "tool_call_id"),
    toolName: readOptionalString(payload, "toolName", "tool_name"),
  };
}

export function isRawObserved(event: RuntimeEvent): boolean {
  if (event.type !== AGUIEventType.CUSTOM) {
    return false;
  }

  return parseCustomEvent(event).name === "aevatar.raw.observed";
}

export function createRuntimeEventAccumulator(input?: {
  actorId?: string;
}): RuntimeEventAccumulator {
  return {
    actorId: input?.actorId ?? "",
    assistantText: "",
    commandId: "",
    errorText: "",
    events: [],
    pendingApproval: undefined,
    runId: "",
    steps: [],
    thinking: "",
    toolCalls: [],
  };
}

export function applyRuntimeEvent(
  accumulator: RuntimeEventAccumulator,
  event: RuntimeEvent
): RuntimeEventAccumulator {
  accumulator.events.push(event);

  if (event.type === AGUIEventType.RUN_STARTED) {
    accumulator.actorId =
      readOptionalString(event as unknown as JsonRecord, "actorId", "threadId") ||
      accumulator.actorId;
    accumulator.runId = event.runId || accumulator.runId;
  }

  if (event.type === AGUIEventType.TEXT_MESSAGE_CONTENT) {
    accumulator.assistantText += String(event.delta || "");
  }

  if (event.type === AGUIEventType.STEP_STARTED) {
    const stepName =
      String(event.stepName || "").trim() ||
      `Step ${accumulator.steps.length + 1}`;
    accumulator.steps.push({
      id: stepName,
      name: stepName,
      startedAt: event.timestamp || Date.now(),
      status: "running",
    });
  }

  if (event.type === AGUIEventType.STEP_FINISHED) {
    const stepName = String(event.stepName || "").trim();
    const existingStep = accumulator.steps.find(
      (step) =>
        step.status === "running" &&
        (!stepName || step.name === stepName || step.id === stepName)
    );
    if (existingStep) {
      existingStep.finishedAt = event.timestamp || Date.now();
      existingStep.status = "done";
    }
  }

  if (event.type === AGUIEventType.TOOL_CALL_START) {
    const toolName = String(event.toolName || "").trim() || "Tool";
    const toolId =
      String(event.toolCallId || "").trim() ||
      `${toolName}-${accumulator.toolCalls.length + 1}`;
    accumulator.toolCalls.push({
      id: toolId,
      name: toolName,
      startedAt: event.timestamp || Date.now(),
      status: "running",
    });
  }

  if (event.type === AGUIEventType.TOOL_CALL_END) {
    const toolId = String(event.toolCallId || "").trim();
    const existingTool = accumulator.toolCalls.find(
      (tool) => tool.status === "running" && (!toolId || tool.id === toolId)
    );
    if (existingTool) {
      existingTool.finishedAt = event.timestamp || Date.now();
      existingTool.result =
        "result" in event && typeof event.result === "string"
          ? event.result.trim()
          : "";
      existingTool.status = "done";
    }
  }

  if (event.type === AGUIEventType.RUN_ERROR) {
    accumulator.errorText = String(
      event.message || "Assistant run failed."
    ).trim();
  }

  const runContext = extractRunContext(event);
  if (runContext) {
    accumulator.actorId = runContext.actorId || accumulator.actorId;
    accumulator.commandId = runContext.commandId || accumulator.commandId;
  }

  const stepRequest = extractStepRequest(event);
  if (stepRequest) {
    const stepIdentity =
      stepRequest.stepId ||
      stepRequest.stepType ||
      `Step ${accumulator.steps.length + 1}`;
    const existingStep = accumulator.steps.find(
      (step) => step.id === stepIdentity || step.name === stepIdentity
    );
    if (!existingStep) {
      accumulator.steps.push({
        id: stepRequest.stepId || stepIdentity,
        name: stepRequest.stepId || stepRequest.stepType || stepIdentity,
        startedAt: event.timestamp || Date.now(),
        status: "running",
        stepType: stepRequest.stepType || undefined,
      });
    }
  }

  const completedStep = extractStepCompleted(event);
  if (completedStep) {
    const existingStep = accumulator.steps.find(
      (step) =>
        step.id === completedStep.stepId || step.name === completedStep.stepId
    );
    if (existingStep) {
      existingStep.error = completedStep.error;
      existingStep.finishedAt = event.timestamp || Date.now();
      existingStep.output = completedStep.output;
      existingStep.status =
        completedStep.success === false ? "error" : "done";
    } else {
      accumulator.steps.push({
        error: completedStep.error,
        finishedAt: event.timestamp || Date.now(),
        id: completedStep.stepId,
        name: completedStep.stepId,
        output: completedStep.output,
        startedAt: event.timestamp || Date.now(),
        status: completedStep.success === false ? "error" : "done",
      });
    }
  }

  const stepOutput = extractStepCompletedOutput(event);
  if (stepOutput && !accumulator.assistantText) {
    accumulator.assistantText = stepOutput;
  }

  const reasoningDelta = extractReasoningDelta(event);
  if (reasoningDelta) {
    accumulator.thinking += reasoningDelta;
  }

  const toolApprovalRequest = extractToolApprovalRequest(event);
  if (toolApprovalRequest) {
    accumulator.pendingApproval = toolApprovalRequest;
  }

  return accumulator;
}
