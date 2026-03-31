import type {
  ChatRunRequest,
  WorkflowResumeRequest,
  WorkflowResumeResponse,
  WorkflowSignalRequest,
  WorkflowSignalResponse,
} from "@aevatar-react-sdk/types";
import { authFetch } from "@/shared/auth/fetch";
import {
  decodeWorkflowResumeResponseBody,
  decodeWorkflowSignalResponseBody,
} from "./runtimeDecoders";
import { requestJson } from "./http/client";
import { readResponseError } from "./http/error";
import {
  encodeAppScriptCommandBase64,
  encodeStringValueBase64,
  getAppScriptCommandEndpointId,
  getAppScriptCommandTypeUrl,
  isAutoEncodableTextPayloadTypeUrl,
  getStringValueTypeUrl,
} from "@/shared/runs/protobufPayload";

const JSON_HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
};

function trimOptional(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined)
  ) as T;
}

function encodeSegment(value: string): string {
  return encodeURIComponent(value.trim());
}

function buildScopePath(scopeId: string): string {
  return `/api/scopes/${encodeSegment(scopeId)}`;
}

function buildScopedServicePath(scopeId: string, serviceId: string): string {
  return `/api/scopes/${encodeSegment(scopeId)}/services/${encodeSegment(
    serviceId
  )}`;
}

function buildInvokeEndpointPath(
  scopeId: string,
  endpointId: string,
  serviceId?: string
): string {
  const encodedEndpointId = encodeSegment(endpointId);
  return serviceId?.trim()
    ? `${buildScopedServicePath(scopeId, serviceId)}/invoke/${encodedEndpointId}`
    : `${buildScopePath(scopeId)}/invoke/${encodedEndpointId}`;
}

function buildInvokeChatStreamPath(
  scopeId: string,
  serviceId?: string
): string {
  return serviceId?.trim()
    ? `${buildScopedServicePath(scopeId, serviceId)}/invoke/chat:stream`
    : `${buildScopePath(scopeId)}/invoke/chat:stream`;
}

function buildRunControlPath(
  scopeId: string,
  runId: string,
  action: "resume" | "signal" | "stop",
  serviceId?: string
): string {
  const encodedRunId = encodeSegment(runId);
  return serviceId?.trim()
    ? `${buildScopedServicePath(scopeId, serviceId)}/runs/${encodedRunId}:${action}`
    : `${buildScopePath(scopeId)}/runs/${encodedRunId}:${action}`;
}

function createClientCommandId(): string {
  return globalThis.crypto?.randomUUID?.()
    ? globalThis.crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function inferPayloadTypeUrl(endpointId: string, requestedTypeUrl?: string): string {
  if (requestedTypeUrl?.trim()) {
    return requestedTypeUrl.trim();
  }

  return endpointId.trim() === getAppScriptCommandEndpointId()
    ? getAppScriptCommandTypeUrl()
    : getStringValueTypeUrl();
}

function resolvePayloadBase64(
  request: EndpointInvokeRequest,
  payloadTypeUrl: string,
  prompt: string,
  commandId?: string
): string {
  const explicitPayloadBase64 = trimOptional(request.payloadBase64);
  if (explicitPayloadBase64) {
    return explicitPayloadBase64;
  }

  if (!isAutoEncodableTextPayloadTypeUrl(payloadTypeUrl)) {
    throw new Error(
      `payloadBase64 is required for payloadTypeUrl '${payloadTypeUrl}'.`
    );
  }

  return payloadTypeUrl === getAppScriptCommandTypeUrl()
    ? encodeAppScriptCommandBase64({
        commandId: commandId ?? createClientCommandId(),
        input: prompt,
      })
    : encodeStringValueBase64(prompt);
}

export type WorkflowStopRequest = {
  actorId?: string;
  runId: string;
  commandId?: string;
  reason?: string;
};

export type WorkflowStopResponse = {
  accepted: boolean;
  actorId?: string;
  runId?: string;
  commandId?: string;
  correlationId?: string;
  reason?: string;
};

export type EndpointInvokeRequest = {
  endpointId: string;
  prompt?: string;
  commandId?: string;
  correlationId?: string;
  payloadTypeUrl?: string;
  payloadBase64?: string;
};

export const runtimeRunsApi = {
  async streamChat(
    scopeId: string,
    request: ChatRunRequest,
    signal: AbortSignal,
    options?: {
      serviceId?: string;
    }
  ): Promise<Response> {
    const sessionId = trimOptional(
      (request as ChatRunRequest & { sessionId?: string }).sessionId
    );
    const response = await authFetch(
      buildInvokeChatStreamPath(scopeId, options?.serviceId),
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "text/event-stream",
        },
        body: JSON.stringify(
          compactObject({
            prompt: request.prompt.trim(),
            sessionId,
            headers: request.metadata,
          })
        ),
        signal,
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return response;
  },

  async streamDraftRun(
    scopeId: string,
    request: ChatRunRequest,
    signal: AbortSignal
  ): Promise<Response> {
    const response = await authFetch(`${buildScopePath(scopeId)}/workflow/draft-run`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream",
      },
      body: JSON.stringify(
        compactObject({
          prompt: request.prompt.trim(),
          workflowYamls:
            request.workflowYamls && request.workflowYamls.length > 0
              ? request.workflowYamls
              : undefined,
          headers: request.metadata,
        })
      ),
      signal,
    });

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return response;
  },

  async invokeEndpoint(
    scopeId: string,
    request: EndpointInvokeRequest,
    options?: {
      serviceId?: string;
    }
  ): Promise<Record<string, unknown>> {
    const normalizedPrompt = request.prompt?.trim() ?? "";
    const payloadTypeUrl = inferPayloadTypeUrl(
      request.endpointId,
      request.payloadTypeUrl
    );
    const resolvedCommandId =
      trimOptional(request.commandId) ||
      (payloadTypeUrl === getAppScriptCommandTypeUrl()
        ? createClientCommandId()
        : undefined);
    const payloadBase64 = resolvePayloadBase64(
      request,
      payloadTypeUrl,
      normalizedPrompt,
      resolvedCommandId
    );
    const correlationId =
      trimOptional(request.correlationId) || resolvedCommandId;

    return requestJson(
      buildInvokeEndpointPath(scopeId, request.endpointId, options?.serviceId),
      (value) => value as Record<string, unknown>,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            commandId: resolvedCommandId,
            correlationId,
            payloadTypeUrl,
            payloadBase64,
          })
        ),
      }
    );
  },

  resume(
    scopeId: string,
    request: WorkflowResumeRequest,
    options?: {
      serviceId?: string;
    }
  ): Promise<WorkflowResumeResponse> {
    return requestJson(
      buildRunControlPath(scopeId, request.runId, "resume", options?.serviceId),
      decodeWorkflowResumeResponseBody,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            actorId: request.actorId,
            runId: request.runId,
            stepId: request.stepId,
            commandId: trimOptional(request.commandId),
            approved: request.approved,
            userInput: trimOptional(request.userInput),
            metadata: request.metadata,
          })
        ),
      }
    );
  },

  signal(
    scopeId: string,
    request: WorkflowSignalRequest,
    options?: {
      serviceId?: string;
    }
  ): Promise<WorkflowSignalResponse> {
    return requestJson(
      buildRunControlPath(scopeId, request.runId, "signal", options?.serviceId),
      decodeWorkflowSignalResponseBody,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            actorId: request.actorId,
            runId: request.runId,
            signalName: request.signalName,
            stepId: trimOptional(request.stepId),
            commandId: trimOptional(request.commandId),
            payload: trimOptional(request.payload),
          })
        ),
      }
    );
  },

  async stop(
    scopeId: string,
    request: WorkflowStopRequest,
    options?: {
      serviceId?: string;
    }
  ): Promise<WorkflowStopResponse> {
    const response = await authFetch(
      buildRunControlPath(scopeId, request.runId, "stop", options?.serviceId),
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            actorId: trimOptional(request.actorId),
            runId: request.runId,
            commandId: trimOptional(request.commandId),
            reason: trimOptional(request.reason),
          })
        ),
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return (await response.json()) as WorkflowStopResponse;
  },
};
