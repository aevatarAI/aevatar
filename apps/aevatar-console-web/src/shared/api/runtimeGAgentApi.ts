import { authFetch } from "@/shared/auth/fetch";
import { requestJson } from "./http/client";
import {
  expectArray,
  expectRecord,
  readBoolean,
  readNullableString,
  readNumber,
  readOptionalString,
  readString,
  readStringArray,
} from "./http/decoders";
import { readResponseError } from "./http/error";
import type {
  RuntimeGAgentBindingActivationResult,
  RuntimeGAgentBindingEndpointInput,
  RuntimeGAgentBindingResult,
  RuntimeGAgentBindingRetirementResult,
  RuntimeGAgentBindingRevision,
  RuntimeGAgentBindingStatus,
  RuntimeGAgentActorGroup,
  RuntimeGAgentTypeDescriptor,
} from "@/shared/models/runtime/gagents";
import {
  normalizeRuntimeGAgentBindingImplementationKind,
} from "@/shared/models/runtime/gagents";

export type RuntimeGAgentDraftRunRequest = {
  actorTypeName: string;
  prompt: string;
  preferredActorId?: string;
};

export type RuntimeScopeGAgentBindingRequest = {
  scopeId: string;
  displayName?: string;
  actorTypeName: string;
  preferredActorId?: string;
  endpoints: RuntimeGAgentBindingEndpointInput[];
  revisionId?: string;
};

function decodeGAgentTypeDescriptor(
  value: unknown,
  label = "RuntimeGAgentTypeDescriptor"
): RuntimeGAgentTypeDescriptor {
  const record = expectRecord(value, label);
  return {
    typeName: readString(record, ["typeName", "TypeName"], `${label}.typeName`),
    fullName: readString(record, ["fullName", "FullName"], `${label}.fullName`),
    assemblyName: readString(
      record,
      ["assemblyName", "AssemblyName"],
      `${label}.assemblyName`
    ),
  };
}

function decodeGAgentActorGroup(
  value: unknown,
  label = "RuntimeGAgentActorGroup"
): RuntimeGAgentActorGroup {
  const record = expectRecord(value, label);
  return {
    gAgentType: readString(
      record,
      ["gAgentType", "GAgentType"],
      `${label}.gAgentType`
    ),
    actorIds: readStringArray(record, ["actorIds", "ActorIds"], `${label}.actorIds`),
  };
}

function decodeGAgentActorGroupsResponse(value: unknown): RuntimeGAgentActorGroup[] {
  if (Array.isArray(value)) {
    return expectArray(
      value,
      "RuntimeGAgentActorGroup[]",
      decodeGAgentActorGroup
    );
  }

  const record = expectRecord(value, "RuntimeGAgentActorSnapshot");
  const groups = record.groups ?? record.Groups;
  return expectArray(
    groups,
    "RuntimeGAgentActorSnapshot.groups",
    decodeGAgentActorGroup
  );
}

function readImplementationKindValue(
  record: Record<string, unknown>,
  label: string
): string | number | undefined {
  const value = record.implementationKind ?? record.ImplementationKind;
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value === "string" || typeof value === "number") {
    return value;
  }

  throw new Error(`${label}.implementationKind must be a string or number.`);
}

function decodeBindingRevision(
  value: unknown,
  label = "RuntimeGAgentBindingRevision"
): RuntimeGAgentBindingRevision {
  const record = expectRecord(value, label);
  return {
    revisionId: readString(record, ["revisionId", "RevisionId"], `${label}.revisionId`),
    implementationKind: normalizeRuntimeGAgentBindingImplementationKind(
      readImplementationKindValue(record, label)
    ),
    status: readString(record, ["status", "Status"], `${label}.status`),
    artifactHash: readString(record, ["artifactHash", "ArtifactHash"], `${label}.artifactHash`),
    failureReason: readString(record, ["failureReason", "FailureReason"], `${label}.failureReason`),
    isDefaultServing: readBoolean(record, ["isDefaultServing", "IsDefaultServing"], `${label}.isDefaultServing`),
    isActiveServing: readBoolean(record, ["isActiveServing", "IsActiveServing"], `${label}.isActiveServing`),
    isServingTarget: readBoolean(record, ["isServingTarget", "IsServingTarget"], `${label}.isServingTarget`),
    allocationWeight: readNumber(record, ["allocationWeight", "AllocationWeight"], `${label}.allocationWeight`),
    servingState: readString(record, ["servingState", "ServingState"], `${label}.servingState`),
    deploymentId: readString(record, ["deploymentId", "DeploymentId"], `${label}.deploymentId`),
    primaryActorId: readString(record, ["primaryActorId", "PrimaryActorId"], `${label}.primaryActorId`),
    createdAt: readNullableString(record, ["createdAt", "CreatedAt"], `${label}.createdAt`),
    preparedAt: readNullableString(record, ["preparedAt", "PreparedAt"], `${label}.preparedAt`),
    publishedAt: readNullableString(record, ["publishedAt", "PublishedAt"], `${label}.publishedAt`),
    retiredAt: readNullableString(record, ["retiredAt", "RetiredAt"], `${label}.retiredAt`),
    workflowName: readOptionalString(record, ["workflowName", "WorkflowName"], `${label}.workflowName`) || "",
    workflowDefinitionActorId:
      readOptionalString(
        record,
        ["workflowDefinitionActorId", "WorkflowDefinitionActorId"],
        `${label}.workflowDefinitionActorId`
      ) || "",
    inlineWorkflowCount:
      record.inlineWorkflowCount === undefined && record.InlineWorkflowCount === undefined
        ? 0
        : readNumber(
            record,
            ["inlineWorkflowCount", "InlineWorkflowCount"],
            `${label}.inlineWorkflowCount`
          ),
    scriptId: readOptionalString(record, ["scriptId", "ScriptId"], `${label}.scriptId`) || "",
    scriptRevision:
      readOptionalString(record, ["scriptRevision", "ScriptRevision"], `${label}.scriptRevision`) || "",
    scriptDefinitionActorId:
      readOptionalString(
        record,
        ["scriptDefinitionActorId", "ScriptDefinitionActorId"],
        `${label}.scriptDefinitionActorId`
      ) || "",
    scriptSourceHash:
      readOptionalString(record, ["scriptSourceHash", "ScriptSourceHash"], `${label}.scriptSourceHash`) || "",
    staticActorTypeName:
      readOptionalString(record, ["staticActorTypeName", "StaticActorTypeName"], `${label}.staticActorTypeName`) || "",
    staticPreferredActorId:
      readOptionalString(
        record,
        ["staticPreferredActorId", "StaticPreferredActorId"],
        `${label}.staticPreferredActorId`
      ) || "",
  };
}

function decodeBindingStatus(
  value: unknown,
  label = "RuntimeGAgentBindingStatus"
): RuntimeGAgentBindingStatus {
  const record = expectRecord(value, label);
  return {
    available: readBoolean(record, ["available", "Available"], `${label}.available`),
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    serviceKey: readString(record, ["serviceKey", "ServiceKey"], `${label}.serviceKey`),
    defaultServingRevisionId: readString(
      record,
      ["defaultServingRevisionId", "DefaultServingRevisionId"],
      `${label}.defaultServingRevisionId`
    ),
    activeServingRevisionId: readString(
      record,
      ["activeServingRevisionId", "ActiveServingRevisionId"],
      `${label}.activeServingRevisionId`
    ),
    deploymentId: readString(record, ["deploymentId", "DeploymentId"], `${label}.deploymentId`),
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      `${label}.deploymentStatus`
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`
    ),
    updatedAt: readNullableString(record, ["updatedAt", "UpdatedAt"], `${label}.updatedAt`),
    revisions: expectArray(
      record.revisions ?? record.Revisions,
      `${label}.revisions`,
      decodeBindingRevision
    ),
  };
}

function decodeBindingResult(
  value: unknown,
  label = "RuntimeGAgentBindingResult"
): RuntimeGAgentBindingResult {
  const record = expectRecord(value, label);
  const gAgentRecord =
    record.gAgent && typeof record.gAgent === "object"
      ? expectRecord(record.gAgent, `${label}.gAgent`)
      : record.gagent && typeof record.gagent === "object"
        ? expectRecord(record.gagent, `${label}.gagent`)
        : null;

  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readOptionalString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    revisionId: readString(record, ["revisionId", "RevisionId"], `${label}.revisionId`),
    implementationKind: normalizeRuntimeGAgentBindingImplementationKind(
      readImplementationKindValue(record, label)
    ),
    targetName:
      readOptionalString(record, ["targetName", "TargetName"], `${label}.targetName`) ||
      readOptionalString(record, ["displayName", "DisplayName"], `${label}.displayName`) ||
      readString(record, ["revisionId", "RevisionId"], `${label}.revisionId`),
    expectedActorId: readOptionalString(
      record,
      ["expectedActorId", "ExpectedActorId"],
      `${label}.expectedActorId`
    ),
    gAgent: gAgentRecord
      ? {
          actorTypeName:
            readOptionalString(gAgentRecord, ["actorTypeName", "ActorTypeName"], `${label}.gAgent.actorTypeName`) || "",
          preferredActorId:
            readOptionalString(
              gAgentRecord,
              ["preferredActorId", "PreferredActorId"],
              `${label}.gAgent.preferredActorId`
            ) || "",
        }
      : null,
  };
}

function decodeBindingActivationResult(
  value: unknown,
  label = "RuntimeGAgentBindingActivationResult"
): RuntimeGAgentBindingActivationResult {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
    displayName: readString(record, ["displayName", "DisplayName"], `${label}.displayName`),
    revisionId: readString(record, ["revisionId", "RevisionId"], `${label}.revisionId`),
  };
}

function decodeBindingRetirementResult(
  value: unknown,
  label = "RuntimeGAgentBindingRetirementResult"
): RuntimeGAgentBindingRetirementResult {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(record, ["serviceId", "ServiceId"], `${label}.serviceId`),
    revisionId: readString(record, ["revisionId", "RevisionId"], `${label}.revisionId`),
    status: readString(record, ["status", "Status"], `${label}.status`),
  };
}

export const runtimeGAgentApi = {
  listTypes(): Promise<RuntimeGAgentTypeDescriptor[]> {
    return requestJson(
      "/api/scopes/gagent-types",
      (value) =>
        expectArray(value, "RuntimeGAgentTypeDescriptor[]", decodeGAgentTypeDescriptor)
    );
  },

  listActors(scopeId: string): Promise<RuntimeGAgentActorGroup[]> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/gagent-actors`,
      decodeGAgentActorGroupsResponse
    );
  },

  getScopeBinding(scopeId: string): Promise<RuntimeGAgentBindingStatus> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/binding`,
      decodeBindingStatus
    );
  },

  getDefaultRouteTarget(scopeId: string): Promise<RuntimeGAgentBindingStatus> {
    return this.getScopeBinding(scopeId);
  },

  bindScopeGAgent(
    input: RuntimeScopeGAgentBindingRequest
  ): Promise<RuntimeGAgentBindingResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(input.scopeId.trim())}/binding`,
      decodeBindingResult,
      {
        method: "PUT",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          implementationKind: "gagent",
          displayName: input.displayName?.trim() || undefined,
          revisionId: input.revisionId?.trim() || undefined,
          gagent: {
            actorTypeName: input.actorTypeName.trim(),
            preferredActorId: input.preferredActorId?.trim() || undefined,
            endpoints: input.endpoints.map((endpoint) => ({
              endpointId: endpoint.endpointId.trim(),
              displayName:
                endpoint.displayName?.trim() || endpoint.endpointId.trim(),
              kind: endpoint.kind?.trim().toLowerCase() || "command",
              requestTypeUrl: endpoint.requestTypeUrl?.trim() || undefined,
              responseTypeUrl: endpoint.responseTypeUrl?.trim() || undefined,
              description: endpoint.description?.trim() || undefined,
            })),
          },
        }),
      }
    );
  },

  activateScopeBindingRevision(
    scopeId: string,
    revisionId: string
  ): Promise<RuntimeGAgentBindingActivationResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/binding/revisions/${encodeURIComponent(revisionId)}:activate`,
      decodeBindingActivationResult,
      {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({}),
      }
    );
  },

  activateMemberBindingRevision(
    scopeId: string,
    revisionId: string
  ): Promise<RuntimeGAgentBindingActivationResult> {
    return this.activateScopeBindingRevision(scopeId, revisionId);
  },

  retireScopeBindingRevision(
    scopeId: string,
    revisionId: string
  ): Promise<RuntimeGAgentBindingRetirementResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/binding/revisions/${encodeURIComponent(revisionId)}:retire`,
      decodeBindingRetirementResult,
      {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({}),
      }
    );
  },

  retireMemberBindingRevision(
    scopeId: string,
    revisionId: string
  ): Promise<RuntimeGAgentBindingRetirementResult> {
    return this.retireScopeBindingRevision(scopeId, revisionId);
  },

  async addActor(
    scopeId: string,
    gAgentType: string,
    actorId: string
  ): Promise<void> {
    const response = await authFetch(
      `/api/scopes/${encodeURIComponent(scopeId)}/gagent-actors`,
      {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          gagentType: gAgentType.trim(),
          actorId: actorId.trim(),
        }),
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }
  },

  async removeActor(
    scopeId: string,
    gAgentType: string,
    actorId: string
  ): Promise<void> {
    const response = await authFetch(
      `/api/scopes/${encodeURIComponent(scopeId)}/gagent-actors/${encodeURIComponent(actorId)}?gagentType=${encodeURIComponent(gAgentType)}`,
      {
        method: "DELETE",
        headers: {
          Accept: "application/json",
        },
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }
  },

  async streamDraftRun(
    scopeId: string,
    request: RuntimeGAgentDraftRunRequest,
    signal: AbortSignal
  ): Promise<Response> {
    const response = await authFetch(
      `/api/scopes/${encodeURIComponent(scopeId)}/gagent/draft-run`,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Accept: "text/event-stream",
        },
        body: JSON.stringify({
          actorTypeName: request.actorTypeName.trim(),
          prompt: request.prompt.trim(),
          preferredActorId: request.preferredActorId?.trim() || undefined,
        }),
        signal,
      }
    );

    if (!response.ok) {
      throw new Error(await readResponseError(response));
    }

    return response;
  },
};
