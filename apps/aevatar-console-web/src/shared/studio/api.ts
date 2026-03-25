import type {
  StudioAppContext,
  StudioAuthSession,
  StudioConnectorCatalog,
  StudioConnectorCatalogImportResult,
  StudioConnectorDraftResponse,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioParseYamlResult,
  StudioRoleCatalogImportResult,
  StudioRoleCatalog,
  StudioRoleDraftResponse,
  StudioRuntimeTestResult,
  StudioSaveSettingsInput,
  StudioSaveWorkflowInput,
  StudioSerializeYamlResult,
  StudioSettings,
  StudioStartExecutionInput,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from "./models";
import type { WorkflowCatalogItemDetail } from "@/shared/api/models";
import { decodeWorkflowCatalogItemDetailResponse } from "@/shared/api/runtimeDecoders";
import { authFetch } from "@/shared/auth/fetch";

const JSON_HEADERS = {
  "Content-Type": "application/json",
  Accept: "application/json",
};

async function studioHostFetch(
  input: string,
  init?: RequestInit
): Promise<Response> {
  return authFetch(input, {
    credentials: "same-origin",
    ...init,
  });
}

function isJsonContentType(contentType: string | null): boolean {
  const value = String(contentType || "").toLowerCase();
  return value.includes("application/json") || value.includes("+json");
}

function trimOptional(value: string | null | undefined): string | undefined {
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

async function requestJson<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function requestDecodedJson<T>(
  input: string,
  decoder: (value: unknown) => T,
  init?: RequestInit
): Promise<T> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return decoder(await response.json());
}

async function request<T>(input: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  const isFormDataBody =
    typeof FormData !== "undefined" && init?.body instanceof FormData;
  if (!isFormDataBody && !headers.has("Content-Type")) {
    headers.set("Content-Type", "application/json");
  }
  if (!headers.has("Accept")) {
    headers.set("Accept", "application/json");
  }

  const response = await studioHostFetch(input, {
    ...init,
    headers,
  });
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  if (!isJsonContentType(response.headers.get("content-type"))) {
    throw new Error("Studio API returned an unexpected response format.");
  }

  return (await response.json()) as T;
}

async function streamSse(
  input: string,
  body: unknown,
  onFrame: (frame: unknown) => void,
  signal?: AbortSignal
): Promise<void> {
  const response = await studioHostFetch(input, {
    method: "POST",
    headers: {
      Accept: "text/event-stream",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
    signal,
  });
  if (!response.ok) {
    throw new Error(await readError(response));
  }

  if (!response.body) {
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });

    let boundary = buffer.indexOf("\n\n");
    while (boundary >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);

      const data = block
        .split("\n")
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice(5).trim())
        .join("\n");

      if (data && data !== "[DONE]") {
        onFrame(JSON.parse(data) as unknown);
      }

      boundary = buffer.indexOf("\n\n");
    }

    if (done) {
      break;
    }
  }
}

function normalizeAssistantFrame(
  frame: unknown
): { type: string; delta?: string; message?: string } | null {
  if (!frame || typeof frame !== "object") {
    return null;
  }

  const candidate = frame as Record<string, unknown>;
  if (typeof candidate.type === "string") {
    return {
      type: candidate.type,
      delta: typeof candidate.delta === "string" ? candidate.delta : undefined,
      message:
        typeof candidate.message === "string" ? candidate.message : undefined,
    };
  }

  if (candidate.textMessageContent) {
    const payload = candidate.textMessageContent as Record<string, unknown>;
    return {
      type: "TEXT_MESSAGE_CONTENT",
      delta: typeof payload.delta === "string" ? payload.delta : "",
    };
  }

  if (candidate.textMessageReasoning) {
    const payload = candidate.textMessageReasoning as Record<string, unknown>;
    return {
      type: "TEXT_MESSAGE_REASONING",
      delta: typeof payload.delta === "string" ? payload.delta : "",
    };
  }

  if (candidate.textMessageEnd) {
    const payload = candidate.textMessageEnd as Record<string, unknown>;
    return {
      type: "TEXT_MESSAGE_END",
      delta: typeof payload.delta === "string" ? payload.delta : "",
      message: typeof payload.message === "string" ? payload.message : "",
    };
  }

  if (candidate.runError) {
    const payload = candidate.runError as Record<string, unknown>;
    return {
      type: "RUN_ERROR",
      message:
        typeof payload.message === "string"
          ? payload.message
          : "Assistant run failed.",
    };
  }

  return null;
}

export const studioApi = {
  getAppContext(): Promise<StudioAppContext> {
    return requestJson("/api/app/context");
  },

  getAuthSession(): Promise<StudioAuthSession> {
    return requestJson("/api/auth/me");
  },

  getWorkspaceSettings(): Promise<StudioWorkspaceSettings> {
    return requestJson("/api/workspace/");
  },

  listWorkflows(): Promise<StudioWorkflowSummary[]> {
    return requestJson("/api/workspace/workflows");
  },

  getTemplateWorkflow(
    workflowName: string
  ): Promise<WorkflowCatalogItemDetail> {
    return requestDecodedJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse
    );
  },

  getWorkflow(workflowId: string): Promise<StudioWorkflowFile> {
    return requestJson(
      `/api/workspace/workflows/${encodeURIComponent(workflowId)}`
    );
  },

  saveWorkflow(input: StudioSaveWorkflowInput): Promise<StudioWorkflowFile> {
    return requestJson("/api/workspace/workflows", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          workflowId: trimOptional(input.workflowId),
          directoryId: input.directoryId,
          workflowName: input.workflowName.trim(),
          fileName: trimOptional(input.fileName),
          yaml: input.yaml,
          layout: input.layout,
        })
      ),
    });
  },

  parseYaml(input: {
    yaml: string;
    availableWorkflowNames?: string[];
  }): Promise<StudioParseYamlResult> {
    return requestJson("/api/editor/parse-yaml", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        yaml: input.yaml,
        availableWorkflowNames: input.availableWorkflowNames,
      }),
    });
  },

  serializeYaml(input: {
    document: StudioWorkflowDocument;
    availableWorkflowNames?: string[];
  }): Promise<StudioSerializeYamlResult> {
    return requestJson("/api/editor/serialize-yaml", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        document: input.document,
        availableWorkflowNames: input.availableWorkflowNames,
      }),
    });
  },

  listExecutions(): Promise<StudioExecutionSummary[]> {
    return requestJson("/api/executions/");
  },

  getExecution(executionId: string): Promise<StudioExecutionDetail> {
    return requestJson(`/api/executions/${encodeURIComponent(executionId)}`);
  },

  startExecution(
    input: StudioStartExecutionInput
  ): Promise<StudioExecutionDetail> {
    return requestJson("/api/executions/", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          workflowName: input.workflowName.trim(),
          prompt: input.prompt.trim(),
          workflowYamls: input.workflowYamls,
          runtimeBaseUrl: trimOptional(input.runtimeBaseUrl),
          scopeId: trimOptional(input.scopeId),
          workflowId: trimOptional(input.workflowId),
          eventFormat: trimOptional(input.eventFormat),
        })
      ),
    });
  },

  stopExecution(
    executionId: string,
    input: { reason?: string | null }
  ): Promise<StudioExecutionDetail> {
    return requestJson(
      `/api/executions/${encodeURIComponent(executionId)}/stop`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          reason: trimOptional(input.reason),
        }),
      }
    );
  },

  resumeExecution(
    executionId: string,
    input: {
      runId: string;
      stepId: string;
      approved: boolean;
      userInput?: string | null;
      suspensionType: "human_input" | "human_approval";
    }
  ): Promise<StudioExecutionDetail> {
    return requestJson(
      `/api/executions/${encodeURIComponent(executionId)}/resume`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({
          runId: input.runId,
          stepId: input.stepId,
          approved: input.approved,
          userInput: trimOptional(input.userInput),
          suspensionType: input.suspensionType,
        }),
      }
    );
  },

  getConnectorCatalog(): Promise<StudioConnectorCatalog> {
    return requestJson("/api/connectors/");
  },

  getConnectorDraft(): Promise<StudioConnectorDraftResponse> {
    return requestJson("/api/connectors/draft");
  },

  saveConnectorCatalog(input: {
    connectors: StudioConnectorCatalog["connectors"];
  }): Promise<StudioConnectorCatalog> {
    return requestJson("/api/connectors/", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        connectors: input.connectors,
      }),
    });
  },

  saveConnectorDraft(input: {
    draft: StudioConnectorDraftResponse["draft"];
  }): Promise<StudioConnectorDraftResponse> {
    return requestJson("/api/connectors/draft", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        draft: input.draft,
      }),
    });
  },

  deleteConnectorDraft(): Promise<void> {
    return request<void>("/api/connectors/draft", {
      method: "DELETE",
    });
  },

  importConnectorCatalog(
    file: File
  ): Promise<StudioConnectorCatalogImportResult> {
    const form = new FormData();
    form.set("file", file, file.name);
    return request("/api/connectors/import", {
      method: "POST",
      body: form,
    });
  },

  getRoleCatalog(): Promise<StudioRoleCatalog> {
    return requestJson("/api/roles/");
  },

  getRoleDraft(): Promise<StudioRoleDraftResponse> {
    return requestJson("/api/roles/draft");
  },

  saveRoleCatalog(input: {
    roles: StudioRoleCatalog["roles"];
  }): Promise<StudioRoleCatalog> {
    return requestJson("/api/roles/", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        roles: input.roles,
      }),
    });
  },

  saveRoleDraft(input: {
    draft: StudioRoleDraftResponse["draft"];
  }): Promise<StudioRoleDraftResponse> {
    return requestJson("/api/roles/draft", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        draft: input.draft,
      }),
    });
  },

  deleteRoleDraft(): Promise<void> {
    return request<void>("/api/roles/draft", {
      method: "DELETE",
    });
  },

  importRoleCatalog(file: File): Promise<StudioRoleCatalogImportResult> {
    const form = new FormData();
    form.set("file", file, file.name);
    return request("/api/roles/import", {
      method: "POST",
      body: form,
    });
  },

  getSettings(): Promise<StudioSettings> {
    return requestJson("/api/settings/");
  },

  saveSettings(input: StudioSaveSettingsInput): Promise<StudioSettings> {
    return requestJson("/api/settings/", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          runtimeBaseUrl: trimOptional(input.runtimeBaseUrl),
          defaultProviderName: trimOptional(input.defaultProviderName),
          providers: input.providers?.map((provider) =>
            compactObject({
              providerName: provider.providerName.trim(),
              providerType: provider.providerType.trim(),
              model: provider.model.trim(),
              endpoint: trimOptional(provider.endpoint),
              apiKey: trimOptional(provider.apiKey),
              clearApiKey: provider.clearApiKey ? true : undefined,
            })
          ),
        })
      ),
    });
  },

  testRuntimeConnection(input: {
    runtimeBaseUrl?: string | null;
  }): Promise<StudioRuntimeTestResult> {
    return requestJson("/api/settings/runtime/test", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        runtimeBaseUrl: trimOptional(input.runtimeBaseUrl),
      }),
    });
  },

  addWorkflowDirectory(input: {
    path: string;
    label?: string | null;
  }): Promise<StudioWorkspaceSettings> {
    return requestJson("/api/workspace/directories", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          path: input.path.trim(),
          label: trimOptional(input.label),
        })
      ),
    });
  },

  removeWorkflowDirectory(directoryId: string): Promise<void> {
    return request<void>(
      `/api/workspace/directories/${encodeURIComponent(directoryId)}`,
      {
        method: "DELETE",
      }
    );
  },

  async authorWorkflow(
    input: {
      prompt: string;
      currentYaml?: string;
      availableWorkflowNames?: string[];
      metadata?: Record<string, string>;
    },
    options?: {
      signal?: AbortSignal;
      onText?: (text: string) => void;
      onReasoning?: (text: string) => void;
    }
  ): Promise<string> {
    let generatedText = "";
    let reasoningText = "";

    await streamSse(
      "/api/app/workflow-generator",
      {
        prompt: input.prompt.trim(),
        currentYaml: input.currentYaml,
        availableWorkflowNames: input.availableWorkflowNames,
        metadata: input.metadata,
      },
      (frame) => {
        const normalized = normalizeAssistantFrame(frame);
        if (!normalized) {
          return;
        }

        if (normalized.type === "TEXT_MESSAGE_CONTENT") {
          generatedText += normalized.delta || "";
          options?.onText?.(generatedText);
          return;
        }

        if (normalized.type === "TEXT_MESSAGE_REASONING") {
          reasoningText += normalized.delta || "";
          options?.onReasoning?.(reasoningText);
          return;
        }

        if (normalized.type === "TEXT_MESSAGE_END") {
          generatedText =
            generatedText || normalized.message || normalized.delta || "";
          options?.onText?.(generatedText);
          return;
        }

        if (normalized.type === "RUN_ERROR") {
          throw new Error(normalized.message || "Assistant run failed.");
        }
      },
      options?.signal
    );

    return generatedText;
  },
};
