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

async function readError(response: Response): Promise<string> {
  const text = await response.text();
  if (!text) {
    return `HTTP ${response.status}`;
  }

  try {
    const payload = JSON.parse(text) as {
      message?: string;
      error?: string;
      code?: string;
    };
    return payload.message || payload.error || payload.code || text;
  } catch {
    return text;
  }
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

export const runtimeRunsApi = {
  async streamChat(
    scopeId: string,
    request: ChatRunRequest,
    signal: AbortSignal,
    options?: {
      serviceId?: string;
    }
  ): Promise<Response> {
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
            workflow: trimOptional(request.workflow),
            agentId: trimOptional(request.agentId),
            workflowYamls:
              request.workflowYamls && request.workflowYamls.length > 0
                ? request.workflowYamls
                : undefined,
            metadata: request.metadata,
          })
        ),
        signal,
      }
    );

    if (!response.ok) {
      throw new Error(await readError(response));
    }

    return response;
  },

  async streamDraftRun(
    scopeId: string,
    request: ChatRunRequest,
    signal: AbortSignal
  ): Promise<Response> {
    const response = await authFetch(`${buildScopePath(scopeId)}/draft-run`, {
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
      throw new Error(await readError(response));
    }

    return response;
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
      throw new Error(await readError(response));
    }

    return (await response.json()) as WorkflowStopResponse;
  },
};
