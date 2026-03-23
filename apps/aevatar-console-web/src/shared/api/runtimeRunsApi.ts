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

export const runtimeRunsApi = {
  async streamChat(
    request: ChatRunRequest,
    signal: AbortSignal
  ): Promise<Response> {
    const response = await authFetch("/api/chat", {
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
    });

    if (!response.ok) {
      throw new Error(await readError(response));
    }

    return response;
  },

  resume(request: WorkflowResumeRequest): Promise<WorkflowResumeResponse> {
    return requestJson(
      "/api/workflows/resume",
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

  signal(request: WorkflowSignalRequest): Promise<WorkflowSignalResponse> {
    return requestJson(
      "/api/workflows/signal",
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
};
