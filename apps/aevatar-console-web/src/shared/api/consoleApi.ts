import type {
  ChatRunRequest,
  WorkflowResumeRequest,
  WorkflowResumeResponse,
  WorkflowSignalRequest,
  WorkflowSignalResponse,
} from '@aevatar-react-sdk/types';
import type {
  PlaygroundWorkflowParseResult,
  PlaygroundWorkflowSaveResult,
  WorkflowActorGraphEdge,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
  WorkflowAgentSummary,
  WorkflowLlmStatus,
  WorkflowCapabilities,
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
  WorkflowPrimitiveDescriptor,
} from './models';
import {
  type Decoder,
  decodePlaygroundWorkflowParseResponse,
  decodePlaygroundWorkflowSaveResponse,
  decodeWorkflowActorGraphEdgesResponse,
  decodeWorkflowActorGraphSubgraphResponse,
  decodeWorkflowActorSnapshotResponse,
  decodeWorkflowActorTimelineResponse,
  decodeWorkflowAgentSummaries,
  decodeWorkflowCapabilitiesResponse,
  decodeWorkflowCatalogItemDetailResponse,
  decodeWorkflowCatalogItems,
  decodeWorkflowLlmStatusResponse,
  decodeWorkflowNames,
  decodeWorkflowPrimitiveDescriptorsResponse,
  decodeWorkflowResumeResponseBody,
  decodeWorkflowSignalResponseBody,
} from './decoders';
import { authFetch } from '@/shared/auth/fetch';

const JSON_HEADERS = {
  'Content-Type': 'application/json',
  Accept: 'application/json',
};

function trimOptional(value?: string): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}

function compactObject<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(
    Object.entries(value).filter(([, entry]) => entry !== undefined),
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

async function requestJson<T>(
  input: string,
  decoder: Decoder<T>,
  init?: RequestInit,
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  return decoder(await response.json());
}

function parseJsonText(text: string): unknown {
  if (!text) {
    return {};
  }

  try {
    return JSON.parse(text);
  } catch {
    return {};
  }
}

function readErrorFromPayload(
  payload: unknown,
  fallback: string,
  status: number,
): string {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) {
    return fallback || `HTTP ${status}`;
  }

  const record = payload as Record<string, unknown>;
  const message = record.message || record.error || record.code;
  return typeof message === 'string' && message.trim().length > 0
    ? message
    : fallback || `HTTP ${status}`;
}

export const consoleApi = {
  listAgents(): Promise<WorkflowAgentSummary[]> {
    return requestJson('/api/agents', decodeWorkflowAgentSummaries);
  },

  listWorkflows(): Promise<string[]> {
    return requestJson('/api/workflows', decodeWorkflowNames);
  },

  listWorkflowCatalog(): Promise<WorkflowCatalogItem[]> {
    return requestJson('/api/workflow-catalog', decodeWorkflowCatalogItems);
  },

  getCapabilities(): Promise<WorkflowCapabilities> {
    return requestJson('/api/capabilities', decodeWorkflowCapabilitiesResponse);
  },

  async parseWorkflow(input: { yaml: string }): Promise<PlaygroundWorkflowParseResult> {
    const response = await authFetch('/api/workflow-authoring/parse', {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify({
        yaml: input.yaml,
      }),
    });
    const text = await response.text();
    const payload = parseJsonText(text);
    if (!response.ok && response.status !== 400) {
      throw new Error(readErrorFromPayload(payload, text, response.status));
    }

    return decodePlaygroundWorkflowParseResponse(payload);
  },

  saveWorkflow(input: {
    yaml: string;
    filename?: string;
    overwrite?: boolean;
  }): Promise<PlaygroundWorkflowSaveResult> {
    return requestJson(
      '/api/workflow-authoring/workflows',
      decodePlaygroundWorkflowSaveResponse,
      {
        method: 'POST',
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            yaml: input.yaml,
            filename: trimOptional(input.filename),
            overwrite: input.overwrite ?? false,
          }),
        ),
      },
    );
  },

  listPrimitives(): Promise<WorkflowPrimitiveDescriptor[]> {
    return requestJson(
      '/api/primitives',
      decodeWorkflowPrimitiveDescriptorsResponse,
    );
  },

  getLlmStatus(): Promise<WorkflowLlmStatus> {
    return requestJson('/api/llm/status', decodeWorkflowLlmStatusResponse);
  },

  getWorkflowDetail(workflowName: string): Promise<WorkflowCatalogItemDetail> {
    return requestJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse,
    );
  },

  getActorSnapshot(actorId: string): Promise<WorkflowActorSnapshot> {
    return requestJson(
      `/api/actors/${encodeURIComponent(actorId)}`,
      decodeWorkflowActorSnapshotResponse,
    );
  },

  getActorTimeline(
    actorId: string,
    options?: { take?: number },
  ): Promise<WorkflowActorTimelineItem[]> {
    const params = new URLSearchParams();
    if (options?.take) {
      params.set('take', String(options.take));
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    return requestJson(
      `/api/actors/${encodeURIComponent(actorId)}/timeline${suffix}`,
      decodeWorkflowActorTimelineResponse,
    );
  },

  getActorGraphEdges(
    actorId: string,
    options?: {
      take?: number;
      direction?: 'Both' | 'Outbound' | 'Inbound';
      edgeTypes?: string[];
    },
  ): Promise<WorkflowActorGraphEdge[]> {
    const params = new URLSearchParams();
    if (options?.take) {
      params.set('take', String(options.take));
    }
    if (options?.direction) {
      params.set('direction', options.direction);
    }
    for (const edgeType of options?.edgeTypes ?? []) {
      const normalized = trimOptional(edgeType);
      if (normalized) {
        params.append('edgeTypes', normalized);
      }
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    return requestJson(
      `/api/actors/${encodeURIComponent(actorId)}/graph-edges${suffix}`,
      decodeWorkflowActorGraphEdgesResponse,
    );
  },

  getActorGraphSubgraph(
    actorId: string,
    options?: {
      depth?: number;
      take?: number;
      direction?: 'Both' | 'Outbound' | 'Inbound';
      edgeTypes?: string[];
    },
  ): Promise<WorkflowActorGraphSubgraph> {
    const params = new URLSearchParams();
    if (options?.depth) {
      params.set('depth', String(options.depth));
    }
    if (options?.take) {
      params.set('take', String(options.take));
    }
    if (options?.direction) {
      params.set('direction', options.direction);
    }
    for (const edgeType of options?.edgeTypes ?? []) {
      const normalized = trimOptional(edgeType);
      if (normalized) {
        params.append('edgeTypes', normalized);
      }
    }

    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    return requestJson(
      `/api/actors/${encodeURIComponent(actorId)}/graph-subgraph${suffix}`,
      decodeWorkflowActorGraphSubgraphResponse,
    );
  },

  async streamChat(request: ChatRunRequest, signal: AbortSignal): Promise<Response> {
    const response = await authFetch('/api/chat', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
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
        }),
      ),
      signal,
    });

    if (!response.ok) {
      throw new Error(await readError(response));
    }

    return response;
  },

  resume(request: WorkflowResumeRequest): Promise<WorkflowResumeResponse> {
    return requestJson('/api/workflows/resume', decodeWorkflowResumeResponseBody, {
      method: 'POST',
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
        }),
      ),
    });
  },

  signal(request: WorkflowSignalRequest): Promise<WorkflowSignalResponse> {
    return requestJson('/api/workflows/signal', decodeWorkflowSignalResponseBody, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          actorId: request.actorId,
          runId: request.runId,
          signalName: request.signalName,
          stepId: trimOptional(request.stepId),
          commandId: trimOptional(request.commandId),
          payload: trimOptional(request.payload),
        }),
      ),
    });
  },
};
