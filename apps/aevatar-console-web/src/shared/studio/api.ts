import type {
  StudioAppContext,
  StudioAuthSession,
  StudioConnectorCatalog,
  StudioConnectorCatalogImportResult,
  StudioConnectorDraftResponse,
  StudioScopeBindingActivationResult,
  StudioScopeBindingImplementationKind,
  StudioScopeBindingRevision,
  StudioScopeBindingTargetKind,
  StudioScopeGAgentBindingInput,
  StudioScopeBindingStatus,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioParseYamlResult,
  StudioRoleCatalogImportResult,
  StudioRoleCatalog,
  StudioRoleDraftResponse,
  StudioScopeScriptBindingActivationResult,
  StudioScopeScriptBindingInput,
  StudioScopeBindingResult,
  StudioScopeGAgentBindingResult,
  StudioScopeScriptBindingResult,
  StudioScopeScriptBindingStatus,
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
import { normalizeStudioScopeBindingImplementationKind } from "./models";
import type { WorkflowCatalogItemDetail } from "@/shared/api/models";
import {
  expectArray,
  expectRecord,
  normalizeEnumValue,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
} from "@/shared/api/http/decoders";
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
  const headers = new Headers(init?.headers);
  return authFetch(input, {
    credentials: "same-origin",
    ...init,
    headers,
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

function decodeStudioScopeBindingRevision(
  value: unknown,
  label = "StudioScopeBindingRevision"
): StudioScopeBindingRevision {
  const record = expectRecord(value, label);
  const implementationKind = readScopeBindingImplementationKind(record, [
    "implementationKind",
    "ImplementationKind",
  ]);
  return {
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    implementationKind,
    status: readString(record, ["status", "Status"], `${label}.status`),
    artifactHash: readString(
      record,
      ["artifactHash", "ArtifactHash"],
      `${label}.artifactHash`
    ),
    failureReason: readString(
      record,
      ["failureReason", "FailureReason"],
      `${label}.failureReason`
    ),
    isDefaultServing: readBoolean(
      record,
      ["isDefaultServing", "IsDefaultServing"],
      `${label}.isDefaultServing`
    ),
    isActiveServing: readBoolean(
      record,
      ["isActiveServing", "IsActiveServing"],
      `${label}.isActiveServing`
    ),
    isServingTarget: readBoolean(
      record,
      ["isServingTarget", "IsServingTarget"],
      `${label}.isServingTarget`
    ),
    allocationWeight: readNumber(
      record,
      ["allocationWeight", "AllocationWeight"],
      `${label}.allocationWeight`
    ),
    servingState: readString(
      record,
      ["servingState", "ServingState"],
      `${label}.servingState`
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`
    ),
    createdAt: readNullableString(
      record,
      ["createdAt", "CreatedAt"],
      `${label}.createdAt`
    ),
    preparedAt: readNullableString(
      record,
      ["preparedAt", "PreparedAt"],
      `${label}.preparedAt`
    ),
    publishedAt: readNullableString(
      record,
      ["publishedAt", "PublishedAt"],
      `${label}.publishedAt`
    ),
    retiredAt: readNullableString(
      record,
      ["retiredAt", "RetiredAt"],
      `${label}.retiredAt`
    ),
  };
}

function readOptionalString(
  record: Record<string, unknown>,
  keys: string[]
): string | undefined {
  for (const key of keys) {
    const rawValue = record[key];
    if (typeof rawValue !== "string") {
      continue;
    }

    const normalized = rawValue.trim();
    if (normalized) {
      return normalized;
    }
  }

  return undefined;
}

function readOptionalScalar(
  record: Record<string, unknown>,
  keys: string[]
): string | number | undefined {
  for (const key of keys) {
    const rawValue = record[key];
    if (
      typeof rawValue === "string" ||
      (typeof rawValue === "number" && !Number.isNaN(rawValue))
    ) {
      return rawValue;
    }
  }

  return undefined;
}

function readScopeBindingImplementationKind(
  record: Record<string, unknown>,
  keys: string[],
  fallback?: string | number
): StudioScopeBindingImplementationKind {
  const rawValue = readOptionalScalar(record, keys) ?? fallback;
  if (rawValue === undefined) {
    return "unknown";
  }

  return normalizeStudioScopeBindingImplementationKind(
    normalizeEnumValue(rawValue, "implementationKind", {
      "0": "unknown",
      "1": "workflow",
      "2": "script",
      "3": "gagent",
      workflow: "workflow",
      scripting: "script",
      script: "script",
      gagent: "gagent",
      unspecified: "unknown",
    })
  );
}

function decodeStudioScopeBindingResult(
  value: unknown
): StudioScopeBindingResult {
  const record = expectRecord(value, "StudioScopeBindingResult");
  const displayName =
    readOptionalString(record, ["displayName", "DisplayName"]) || "";
  const serviceId = readOptionalString(record, ["serviceId", "ServiceId"]);
  const workflowRecord =
    record.workflow && typeof record.workflow === "object"
      ? expectRecord(record.workflow, "StudioScopeBindingResult.workflow")
      : null;
  const scriptRecord =
    record.script && typeof record.script === "object"
      ? expectRecord(record.script, "StudioScopeBindingResult.script")
      : null;
  const gAgentRecord =
    (record.gAgent ?? record.gagent) &&
    typeof (record.gAgent ?? record.gagent) === "object"
      ? expectRecord(
          record.gAgent ?? record.gagent,
          "StudioScopeBindingResult.gAgent"
        )
      : null;

  const workflowName =
    workflowRecord == null
      ? readOptionalString(record, ["workflowName", "WorkflowName"])
      : readOptionalString(workflowRecord, ["workflowName", "WorkflowName"]) ||
        readOptionalString(record, ["workflowName", "WorkflowName"]);
  const definitionActorIdPrefix =
    workflowRecord == null
      ? readOptionalString(record, [
          "definitionActorIdPrefix",
          "DefinitionActorIdPrefix",
        ])
      : readOptionalString(workflowRecord, [
          "definitionActorIdPrefix",
          "DefinitionActorIdPrefix",
        ]) ||
        readOptionalString(record, [
          "definitionActorIdPrefix",
          "DefinitionActorIdPrefix",
        ]);
  const implementationKind = readScopeBindingImplementationKind(
    record,
    ["implementationKind", "ImplementationKind"],
    scriptRecord
      ? "script"
      : gAgentRecord
        ? "gagent"
        : workflowRecord || workflowName
          ? "workflow"
          : "unknown"
  );
  const targetKind: StudioScopeBindingTargetKind = implementationKind;
  const targetName =
    (targetKind === "workflow"
      ? workflowName
      : targetKind === "script"
        ? readOptionalString(scriptRecord ?? {}, ["scriptId", "ScriptId"])
        : targetKind === "gagent"
          ? readOptionalString(gAgentRecord ?? {}, [
              "preferredActorId",
              "PreferredActorId",
            ]) ||
            readOptionalString(gAgentRecord ?? {}, [
              "actorTypeName",
              "ActorTypeName",
            ])
          : undefined) ||
    displayName ||
    serviceId ||
    readString(
      record,
      ["revisionId", "RevisionId"],
      "StudioScopeBindingResult.revisionId"
    );

  return {
    scopeId: readString(
      record,
      ["scopeId", "ScopeId"],
      "StudioScopeBindingResult.scopeId"
    ),
    serviceId,
    displayName,
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      "StudioScopeBindingResult.revisionId"
    ),
    implementationKind,
    targetKind,
    targetName,
    workflowName,
    definitionActorIdPrefix,
    expectedActorId: readOptionalString(record, [
      "expectedActorId",
      "ExpectedActorId",
    ]),
    workflow:
      targetKind === "workflow" && (workflowName || definitionActorIdPrefix)
        ? {
            workflowName: workflowName || displayName || targetName,
            definitionActorIdPrefix: definitionActorIdPrefix || "",
          }
        : null,
    script: scriptRecord
      ? {
          scriptId:
            readOptionalString(scriptRecord, ["scriptId", "ScriptId"]) || "",
          scriptRevision:
            readOptionalString(scriptRecord, [
              "scriptRevision",
              "ScriptRevision",
            ]) || "",
          definitionActorId:
            readOptionalString(scriptRecord, [
              "definitionActorId",
              "DefinitionActorId",
            ]) || "",
        }
      : null,
    gAgent: gAgentRecord
      ? {
          actorTypeName:
            readOptionalString(gAgentRecord, [
              "actorTypeName",
              "ActorTypeName",
            ]) || "",
          preferredActorId:
            readOptionalString(gAgentRecord, [
              "preferredActorId",
              "PreferredActorId",
            ]) || "",
        }
      : null,
  };
}

function decodeStudioScopeBindingStatus(
  value: unknown
): StudioScopeBindingStatus {
  const record = expectRecord(value, "StudioScopeBindingStatus");
  return {
    available: readBoolean(
      record,
      ["available", "Available"],
      "StudioScopeBindingStatus.available"
    ),
    scopeId: readString(
      record,
      ["scopeId", "ScopeId"],
      "StudioScopeBindingStatus.scopeId"
    ),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      "StudioScopeBindingStatus.serviceId"
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      "StudioScopeBindingStatus.displayName"
    ),
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      "StudioScopeBindingStatus.serviceKey"
    ),
    defaultServingRevisionId: readString(
      record,
      ["defaultServingRevisionId", "DefaultServingRevisionId"],
      "StudioScopeBindingStatus.defaultServingRevisionId"
    ),
    activeServingRevisionId: readString(
      record,
      ["activeServingRevisionId", "ActiveServingRevisionId"],
      "StudioScopeBindingStatus.activeServingRevisionId"
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      "StudioScopeBindingStatus.deploymentId"
    ),
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      "StudioScopeBindingStatus.deploymentStatus"
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      "StudioScopeBindingStatus.primaryActorId"
    ),
    updatedAt: readNullableString(
      record,
      ["updatedAt", "UpdatedAt"],
      "StudioScopeBindingStatus.updatedAt"
    ),
    revisions: expectArray(
      record.revisions ?? record.Revisions,
      "StudioScopeBindingStatus.revisions",
      decodeStudioScopeBindingRevision
    ),
  };
}

export const studioApi = {
  getAppContext(): Promise<StudioAppContext> {
    return requestJson("/api/studio/context");
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
    availableStepTypes?: string[];
  }): Promise<StudioParseYamlResult> {
    return requestJson("/api/editor/parse-yaml", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        yaml: input.yaml,
        availableWorkflowNames: input.availableWorkflowNames,
        availableStepTypes: input.availableStepTypes,
      }),
    });
  },

  serializeYaml(input: {
    document: StudioWorkflowDocument;
    availableWorkflowNames?: string[];
    availableStepTypes?: string[];
  }): Promise<StudioSerializeYamlResult> {
    return requestJson("/api/editor/serialize-yaml", {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        document: input.document,
        availableWorkflowNames: input.availableWorkflowNames,
        availableStepTypes: input.availableStepTypes,
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

  bindScopeWorkflow(input: {
    scopeId: string;
    displayName?: string | null;
    workflowYamls: string[];
    revisionId?: string | null;
  }): Promise<StudioScopeBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: "workflow",
            displayName: trimOptional(input.displayName),
            workflowYamls:
              input.workflowYamls.length > 0 ? input.workflowYamls : undefined,
            revisionId: trimOptional(input.revisionId),
          })
        ),
      }
    );
  },

  bindScopeScript(
    input: StudioScopeScriptBindingInput
  ): Promise<StudioScopeScriptBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: "script",
            displayName: trimOptional(input.displayName),
            script: compactObject({
              scriptId: input.scriptId.trim(),
              scriptRevision: input.scriptRevision.trim(),
            }),
            revisionId: trimOptional(input.revisionId),
          })
        ),
      }
    );
  },

  bindScopeGAgent(
    input: StudioScopeGAgentBindingInput
  ): Promise<StudioScopeGAgentBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: "gagent",
            displayName: trimOptional(input.displayName),
            gagent: compactObject({
              actorTypeName: input.actorTypeName.trim(),
              preferredActorId: trimOptional(input.preferredActorId),
              endpoints: input.endpoints.map((endpoint) =>
                compactObject({
                  endpointId: endpoint.endpointId.trim(),
                  displayName:
                    trimOptional(endpoint.displayName) ||
                    endpoint.endpointId.trim(),
                  kind: trimOptional(endpoint.kind)?.toLowerCase() || "command",
                  requestTypeUrl: trimOptional(endpoint.requestTypeUrl),
                  responseTypeUrl: trimOptional(endpoint.responseTypeUrl),
                  description: trimOptional(endpoint.description),
                })
              ),
            }),
            revisionId: trimOptional(input.revisionId),
          })
        ),
      }
    );
  },

  getScopeBinding(scopeId: string): Promise<StudioScopeBindingStatus> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/binding`,
      decodeStudioScopeBindingStatus
    );
  },

  getScopeScriptBinding(
    scopeId: string
  ): Promise<StudioScopeScriptBindingStatus> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/binding`,
      decodeStudioScopeBindingStatus
    );
  },

  activateScopeBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeBindingActivationResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim()
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim()
      )}:activate`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({}),
      }
    );
  },

  activateScopeScriptBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeScriptBindingActivationResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim()
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim()
      )}:activate`,
      {
        method: "POST",
        headers: JSON_HEADERS,
        body: JSON.stringify({}),
      }
    );
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
      "/api/workflows/generator",
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
