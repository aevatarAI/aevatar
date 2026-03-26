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

function buildScopedServicePath(scopeId: string, serviceId: string): string {
  return `/api/scopes/${encodeSegment(scopeId)}/services/${encodeSegment(
    serviceId
  )}`;
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
    serviceId: string,
    request: ChatRunRequest,
    signal: AbortSignal
  ): Promise<Response> {
    const response = await authFetch(
      `${buildScopedServicePath(scopeId, serviceId)}/invoke/chat:stream`,
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

  resume(
    scopeId: string,
    serviceId: string,
    request: WorkflowResumeRequest
  ): Promise<WorkflowResumeResponse> {
    return requestJson(
      `${buildScopedServicePath(scopeId, serviceId)}/runs/${encodeSegment(
        request.runId
      )}:resume`,
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
    serviceId: string,
    request: WorkflowSignalRequest
  ): Promise<WorkflowSignalResponse> {
    return requestJson(
      `${buildScopedServicePath(scopeId, serviceId)}/runs/${encodeSegment(
        request.runId
      )}:signal`,
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
    serviceId: string,
    request: WorkflowStopRequest
  ): Promise<WorkflowStopResponse> {
    const response = await authFetch(
      `${buildScopedServicePath(scopeId, serviceId)}/runs/${encodeSegment(
        request.runId
      )}:stop`,
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
