import type {
  StudioAppContext,
  StudioAuthSession,
  StudioConnectorCatalog,
  StudioConnectorCatalogImportResult,
  StudioConnectorDraftResponse,
  StudioScopeBindingActivationResult,
  StudioScopeBindingImplementationKind,
  StudioScopeBindingRetirementResult,
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
  StudioOrnnHealthResult,
  StudioOrnnSkillSearchResult,
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
  StudioUserConfig,
  StudioUserConfigModelsResponse,
  StudioWorkflowDraft,
  StudioWorkflowDraftSummary,
  StudioWorkflowDocument,
  StudioWorkflowFile,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from "./models";
import { normalizeStudioScopeBindingImplementationKind } from "./models";
import type { WorkflowCatalogItemDetail } from "@/shared/api/models";
import { scopesApi } from "@/shared/api/scopesApi";
import {
  expectArray,
  expectRecord,
  expectString,
  normalizeEnumValue,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
} from "@/shared/api/http/decoders";
import { readResponseError } from "@/shared/api/http/error";
import { decodeWorkflowCatalogItemDetailResponse } from "@/shared/api/runtimeDecoders";
import { authFetch } from "@/shared/auth/fetch";
import type {
  ScopeWorkflowDetail,
  ScopeWorkflowSummary,
} from "@/shared/models/scopes";
import { getOrnnRuntimeConfig } from "./ornnConfig";

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

async function externalFetch(
  input: string,
  init?: RequestInit
): Promise<Response> {
  const headers = new Headers(init?.headers);
  if (!headers.has("Accept")) {
    headers.set("Accept", "application/json");
  }

  return authFetch(input, {
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

function toScopeWorkflowDirectoryId(scopeId: string): string {
  return `scope:${scopeId}`;
}

function toScopeWorkflowPath(scopeId: string, workflowId: string): string {
  return `scope://${scopeId}/${workflowId}.yaml`;
}

function resolveScopeWorkflowName(workflow: ScopeWorkflowSummary): string {
  return workflow.displayName?.trim() || workflow.workflowName?.trim() || workflow.workflowId;
}

function toCommittedWorkflowSummary(
  scopeId: string,
  workflow: ScopeWorkflowSummary
): StudioWorkflowSummary {
  return {
    workflowId: workflow.workflowId,
    name: resolveScopeWorkflowName(workflow),
    description: "",
    fileName: `${workflow.workflowId}.yaml`,
    filePath: toScopeWorkflowPath(scopeId, workflow.workflowId),
    directoryId: toScopeWorkflowDirectoryId(scopeId),
    directoryLabel: scopeId,
    stepCount: 0,
    hasLayout: false,
    updatedAtUtc: workflow.updatedAt,
  };
}

function toCommittedWorkflowFile(
  scopeId: string,
  detail: ScopeWorkflowDetail
): StudioWorkflowFile {
  const workflow = detail.workflow;
  if (!workflow) {
    throw new Error("Not Found");
  }

  return {
    workflowId: workflow.workflowId,
    name: resolveScopeWorkflowName(workflow),
    fileName: `${workflow.workflowId}.yaml`,
    filePath: toScopeWorkflowPath(scopeId, workflow.workflowId),
    directoryId: toScopeWorkflowDirectoryId(scopeId),
    directoryLabel: scopeId,
    yaml: detail.source?.workflowYaml ?? "",
    document: null,
    draftExists: false,
    findings: [],
    updatedAtUtc: workflow.updatedAt,
  };
}

function toWorkflowFile(
  draft: StudioWorkflowDraft,
  draftExists: boolean
): StudioWorkflowFile {
  return {
    ...draft,
    document: null,
    draftExists,
    findings: [],
  };
}

function selectLatestTimestamp(left: string, right: string): string {
  return Date.parse(left) >= Date.parse(right) ? left : right;
}

function withOptionalScopeId(
  path: string,
  scopeId?: string | null
): string {
  const normalizedScopeId = trimOptional(scopeId);
  if (!normalizedScopeId) {
    return path;
  }

  const separator = path.includes("?") ? "&" : "?";
  return `${path}${separator}scopeId=${encodeURIComponent(normalizedScopeId)}`;
}

function normalizeOrnnBaseUrl(baseUrl?: string | null): string {
  return trimOptional(baseUrl)?.replace(/\/+$/, "") ?? "";
}

function readOptionalNumber(value: unknown): number | undefined {
  return typeof value === "number" && !Number.isNaN(value) ? value : undefined;
}

function readOptionalBoolean(value: unknown): boolean | undefined {
  return typeof value === "boolean" ? value : undefined;
}

function decodeOrnnSkillSearchResult(
  value: unknown,
  baseUrl: string,
  fallbackPage: number,
  fallbackPageSize: number
): StudioOrnnSkillSearchResult {
  const record = expectRecord(value, "Ornn search response");
  const payload =
    record.data === undefined
      ? record
      : expectRecord(record.data, "Ornn search response.data");

  const items = Array.isArray(payload.items)
    ? payload.items.map((entry, index) => {
        const skill = expectRecord(entry, `Ornn search response.items[${index}]`);
        return {
          guid: readNullableString(
            skill,
            "guid",
            `Ornn search response.items[${index}].guid`
          ) ?? "",
          name:
            readNullableString(
              skill,
              "name",
              `Ornn search response.items[${index}].name`
            ) ?? "Unnamed skill",
          description:
            readNullableString(
              skill,
              "description",
              `Ornn search response.items[${index}].description`
            ) ?? "",
          isPrivate:
            readOptionalBoolean(skill.isPrivate) ??
            readOptionalBoolean(skill.private) ??
            false,
        };
      })
    : [];

  return {
    baseUrl,
    total: readOptionalNumber(payload.total) ?? items.length,
    totalPages: readOptionalNumber(payload.totalPages) ?? 1,
    page: readOptionalNumber(payload.page) ?? fallbackPage,
    pageSize: readOptionalNumber(payload.pageSize) ?? fallbackPageSize,
    items,
    message:
      readNullableString(payload, "message", "Ornn search response.message") ??
      undefined,
  };
}

function decodeStudioUserConfigModelsResponse(
  value: unknown,
  label = "StudioUserConfigModelsResponse"
): StudioUserConfigModelsResponse {
  const record = expectRecord(value, label);
  const providersSource = record.providers ?? [];
  const supportedModelsSource =
    record.supportedModels ?? record.supported_models ?? [];
  return {
    providers: expectArray(
      providersSource,
      `${label}.providers`,
      (entry, providerLabel) => {
        const resolvedProviderLabel =
          providerLabel ?? `${label}.providers[]`;
        const provider = expectRecord(entry, resolvedProviderLabel);
        return {
          providerSlug: readNullableString(
            provider,
            ["providerSlug", "provider_slug"],
            `${resolvedProviderLabel}.providerSlug`
          ) ?? "",
          providerName: readNullableString(
            provider,
            ["providerName", "provider_name"],
            `${resolvedProviderLabel}.providerName`
          ) ?? "",
          status:
            readNullableString(
              provider,
              "status",
              `${resolvedProviderLabel}.status`
            )?.trim().toLowerCase() ?? "",
          proxyUrl:
            readNullableString(
              provider,
              ["proxyUrl", "proxy_url"],
              `${resolvedProviderLabel}.proxyUrl`
            ) ?? "",
          source:
            readNullableString(
              provider,
              "source",
              `${resolvedProviderLabel}.source`
            ) ?? undefined,
        };
      }
    ),
    gatewayUrl:
      readNullableString(
        record,
        ["gatewayUrl", "gateway_url"],
        `${label}.gatewayUrl`
      ) ?? "",
    supportedModels: expectArray(
      supportedModelsSource,
      `${label}.supportedModels`,
      (entry, entryLabel) =>
        expectString(entry, entryLabel ?? `${label}.supportedModels[]`)
    ),
    modelsByProvider: Object.fromEntries(
      Object.entries(
        expectRecord(
          record.modelsByProvider ?? record.models_by_provider ?? {},
          `${label}.modelsByProvider`
        )
      ).map(([providerSlug, models]) => [
        providerSlug,
        expectArray(
          models,
          `${label}.modelsByProvider.${providerSlug}`,
          (entry, entryLabel) =>
            expectString(
              entry,
              entryLabel ?? `${label}.modelsByProvider.${providerSlug}[]`
            )
        ),
      ])
    ),
  };
}

async function requestJson<T>(input: string, init?: RequestInit): Promise<T> {
  const response = await studioHostFetch(input, init);
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

async function requestJsonOrNull<T>(
  input: string,
  init?: RequestInit
): Promise<T | null> {
  const response = await studioHostFetch(input, init);
  if (response.status === 404) {
    return null;
  }

  if (!response.ok) {
    throw new Error(await readResponseError(response));
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
    throw new Error(await readResponseError(response));
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
    throw new Error(await readResponseError(response));
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
    throw new Error(await readResponseError(response));
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
    workflowName:
      readOptionalString(record, ["workflowName", "WorkflowName"]) || "",
    workflowDefinitionActorId:
      readOptionalString(record, [
        "workflowDefinitionActorId",
        "WorkflowDefinitionActorId",
      ]) || "",
    inlineWorkflowCount:
      record.inlineWorkflowCount === undefined &&
      record.InlineWorkflowCount === undefined
        ? 0
        : readNumber(
            record,
            ["inlineWorkflowCount", "InlineWorkflowCount"],
            `${label}.inlineWorkflowCount`
          ),
    scriptId:
      readOptionalString(record, ["scriptId", "ScriptId"]) || "",
    scriptRevision:
      readOptionalString(record, ["scriptRevision", "ScriptRevision"]) || "",
    scriptDefinitionActorId:
      readOptionalString(record, [
        "scriptDefinitionActorId",
        "ScriptDefinitionActorId",
      ]) || "",
    scriptSourceHash:
      readOptionalString(record, ["scriptSourceHash", "ScriptSourceHash"]) || "",
    staticActorTypeName:
      readOptionalString(record, [
        "staticActorTypeName",
        "StaticActorTypeName",
      ]) || "",
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
  const serviceId = readOptionalString(record, [
    "serviceId",
    "ServiceId",
    "publishedServiceId",
    "PublishedServiceId",
  ]);
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
      [
        "serviceId",
        "ServiceId",
        "publishedServiceId",
        "PublishedServiceId",
      ],
      "StudioScopeBindingStatus.serviceId"
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      "StudioScopeBindingStatus.displayName"
    ),
    serviceKey: readString(
      record,
      [
        "serviceKey",
        "ServiceKey",
        "publishedServiceKey",
        "PublishedServiceKey",
      ],
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

  getWorkspaceSettings(scopeId?: string | null): Promise<StudioWorkspaceSettings> {
    return requestJson(withOptionalScopeId("/api/workspace/", scopeId));
  },

  listWorkflowDrafts(scopeId?: string | null): Promise<StudioWorkflowDraftSummary[]> {
    return requestJson(withOptionalScopeId("/api/workspace/workflow-drafts", scopeId));
  },

  getTemplateWorkflow(
    workflowName: string
  ): Promise<WorkflowCatalogItemDetail> {
    return requestDecodedJson(
      `/api/workflows/${encodeURIComponent(workflowName)}`,
      decodeWorkflowCatalogItemDetailResponse
    );
  },

  getWorkflowDraft(
    workflowId: string,
    scopeId?: string | null
  ): Promise<StudioWorkflowDraft> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        scopeId
      )
    );
  },

  createWorkflowDraft(
    input: Omit<StudioSaveWorkflowInput, "workflowId">
  ): Promise<StudioWorkflowDraft> {
    return requestJson(withOptionalScopeId("/api/workspace/workflow-drafts", input.scopeId), {
      method: "POST",
      headers: JSON_HEADERS,
      body: JSON.stringify(
        compactObject({
          directoryId: input.directoryId,
          workflowName: input.workflowName.trim(),
          fileName: trimOptional(input.fileName),
          yaml: input.yaml,
          layout: input.layout,
        })
      ),
    });
  },

  updateWorkflowDraft(
    input: StudioSaveWorkflowInput & { workflowId: string }
  ): Promise<StudioWorkflowDraft> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(input.workflowId)}`,
        input.scopeId
      ),
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            directoryId: input.directoryId,
            workflowName: input.workflowName.trim(),
            fileName: trimOptional(input.fileName),
            yaml: input.yaml,
            layout: input.layout,
          })
        ),
      }
    );
  },

  deleteWorkflowDraft(
    workflowId: string,
    scopeId?: string | null
  ): Promise<void> {
    return requestJson(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        scopeId
      ),
      {
        method: "DELETE",
      }
    );
  },

  listWorkflows(scopeId?: string | null): Promise<StudioWorkflowSummary[]> {
    const normalizedScopeId = trimOptional(scopeId);
    if (!normalizedScopeId) {
      return this.listWorkflowDrafts(scopeId);
    }

    return Promise.all([
      this.listWorkflowDrafts(normalizedScopeId),
      scopesApi.listWorkflows(normalizedScopeId),
    ]).then(([drafts, committed]) => {
      const merged = new Map<string, StudioWorkflowSummary>();

      for (const workflow of committed) {
        merged.set(
          workflow.workflowId,
          toCommittedWorkflowSummary(normalizedScopeId, workflow)
        );
      }

      for (const draft of drafts) {
        const existing = merged.get(draft.workflowId);
        merged.set(
          draft.workflowId,
          existing
            ? {
                ...draft,
                updatedAtUtc: selectLatestTimestamp(
                  draft.updatedAtUtc,
                  existing.updatedAtUtc
                ),
              }
            : draft
        );
      }

      return Array.from(merged.values()).sort(
        (left, right) =>
          Date.parse(right.updatedAtUtc) - Date.parse(left.updatedAtUtc)
      );
    });
  },

  async getWorkflow(
    workflowId: string,
    scopeId?: string | null
  ): Promise<StudioWorkflowFile> {
    const normalizedScopeId = trimOptional(scopeId);
    if (!normalizedScopeId) {
      const draft = await this.getWorkflowDraft(workflowId, scopeId);
      return toWorkflowFile(draft, true);
    }

    const draft = await requestJsonOrNull<StudioWorkflowDraft>(
      withOptionalScopeId(
        `/api/workspace/workflow-drafts/${encodeURIComponent(workflowId)}`,
        normalizedScopeId
      )
    );
    if (draft) {
      return toWorkflowFile(draft, true);
    }

    return toCommittedWorkflowFile(
      normalizedScopeId,
      await scopesApi.getWorkflowDetail(normalizedScopeId, workflowId)
    );
  },

  saveWorkflow(input: StudioSaveWorkflowInput): Promise<StudioWorkflowFile> {
    const normalizedWorkflowId = trimOptional(input.workflowId);
    const shouldUpdate =
      Boolean(normalizedWorkflowId) &&
      (input.draftExists ?? Boolean(normalizedWorkflowId));
    const request = shouldUpdate && normalizedWorkflowId
      ? this.updateWorkflowDraft({
          ...input,
          workflowId: normalizedWorkflowId,
        })
      : this.createWorkflowDraft({
          scopeId: input.scopeId,
          directoryId: input.directoryId,
          workflowName: input.workflowName,
          fileName: input.fileName,
          yaml: input.yaml,
          layout: input.layout,
        });
    return request.then((draft) => toWorkflowFile(draft, true));
  },

  deleteWorkflow(
    workflowId: string,
    scopeId?: string | null
  ): Promise<void> {
    return this.deleteWorkflowDraft(workflowId, scopeId);
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
            serviceId: trimOptional(input.serviceId),
            displayName: trimOptional(input.displayName),
            gagent: compactObject({
              actorTypeName: input.actorTypeName.trim(),
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

  bindMemberWorkflow(input: {
    scopeId: string;
    memberId: string;
    displayName?: string | null;
    workflowYamls: readonly string[];
    revisionId?: string | null;
  }): Promise<StudioScopeBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
      decodeStudioScopeBindingResult,
      {
        method: "PUT",
        headers: JSON_HEADERS,
        body: JSON.stringify(
          compactObject({
            implementationKind: "workflow",
            displayName: trimOptional(input.displayName),
            workflow: {
              workflowYamls: input.workflowYamls,
            },
            revisionId: trimOptional(input.revisionId),
          })
        ),
      }
    );
  },

  bindMemberScript(input: {
    scopeId: string;
    memberId: string;
    displayName?: string | null;
    scriptId: string;
    scriptRevision: string;
    revisionId?: string | null;
  }): Promise<StudioScopeScriptBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
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

  bindMemberGAgent(input: {
    scopeId: string;
    memberId: string;
    displayName?: string | null;
    actorTypeName: string;
    endpoints: StudioScopeGAgentBindingInput["endpoints"];
    revisionId?: string | null;
  }): Promise<StudioScopeGAgentBindingResult> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/members/${encodeURIComponent(input.memberId.trim())}/binding`,
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

  getMemberBinding(
    scopeId: string,
    memberId: string
  ): Promise<StudioScopeBindingStatus> {
    return requestDecodedJson(
      `/api/scopes/${encodeURIComponent(scopeId.trim())}/members/${encodeURIComponent(memberId.trim())}/binding`,
      decodeStudioScopeBindingStatus
    );
  },

  getDefaultRouteTarget(scopeId: string): Promise<StudioScopeBindingStatus> {
    return this.getScopeBinding(scopeId);
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

  retireScopeBindingRevision(input: {
    scopeId: string;
    revisionId: string;
  }): Promise<StudioScopeBindingRetirementResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(
        input.scopeId.trim()
      )}/binding/revisions/${encodeURIComponent(
        input.revisionId.trim()
      )}:retire`,
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

  getUserConfig(): Promise<StudioUserConfig> {
    return requestJson("/api/user-config");
  },

  saveUserConfig(input: StudioUserConfig): Promise<StudioUserConfig> {
    return requestJson("/api/user-config", {
      method: "PUT",
      headers: JSON_HEADERS,
      body: JSON.stringify({
        defaultModel: input.defaultModel.trim(),
        preferredLlmRoute: trimOptional(input.preferredLlmRoute),
        runtimeMode: trimOptional(input.runtimeMode),
        localRuntimeBaseUrl: trimOptional(input.localRuntimeBaseUrl),
        remoteRuntimeBaseUrl: trimOptional(input.remoteRuntimeBaseUrl),
        maxToolRounds: input.maxToolRounds ?? null,
      }),
    });
  },

  getUserConfigModels(): Promise<StudioUserConfigModelsResponse> {
    return requestDecodedJson(
      "/api/user-config/models",
      decodeStudioUserConfigModelsResponse
    );
  },

  async getSkillsHealth(): Promise<StudioOrnnHealthResult> {
    const ornnConfig = getOrnnRuntimeConfig();
    const baseUrl = normalizeOrnnBaseUrl(ornnConfig.baseUrl);
    if (ornnConfig.configurationError || !baseUrl) {
      return {
        baseUrl,
        reachable: false,
        message:
          ornnConfig.configurationError ?? "Ornn base URL is not configured.",
      };
    }

    const url = `${baseUrl}/api/web/skill-search?query=&scope=public&page=1&pageSize=1`;

    try {
      const response = await externalFetch(url);
      if (!response.ok) {
        return {
          baseUrl,
          reachable: false,
          message: `Cannot reach Ornn (${response.status}).`,
        };
      }

      return {
        baseUrl,
        reachable: true,
        message: "Connected to Ornn.",
      };
    } catch (error) {
      return {
        baseUrl,
        reachable: false,
        message:
          error instanceof Error && error.message
            ? error.message
            : "Cannot reach Ornn.",
      };
    }
  },

  async searchSkills(input?: {
    query?: string | null;
    scope?: string | null;
    page?: number | null;
    pageSize?: number | null;
  }): Promise<StudioOrnnSkillSearchResult> {
    const ornnConfig = getOrnnRuntimeConfig();
    const baseUrl = normalizeOrnnBaseUrl(ornnConfig.baseUrl);
    const query = trimOptional(input?.query) ?? "";
    const scope = trimOptional(input?.scope) ?? "mixed";
    const page = input?.page && input.page > 0 ? input.page : 1;
    const pageSize = input?.pageSize && input.pageSize > 0 ? input.pageSize : 50;
    if (ornnConfig.configurationError || !baseUrl) {
      return {
        baseUrl,
        total: 0,
        totalPages: 0,
        page,
        pageSize,
        items: [],
        message:
          ornnConfig.configurationError ?? "Ornn base URL is not configured.",
      };
    }

    const params = new URLSearchParams({
      query,
      mode: "keyword",
      scope,
      page: String(page),
      pageSize: String(pageSize),
    });

    const response = await externalFetch(
      `${baseUrl}/api/web/skill-search?${params.toString()}`
    );
    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    const contentType = response.headers?.get?.("content-type") ?? null;
    if (contentType !== null && !isJsonContentType(contentType)) {
      throw new Error("Ornn API returned an unexpected response format.");
    }

    return decodeOrnnSkillSearchResult(
      await response.json(),
      baseUrl,
      page,
      pageSize
    );
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
