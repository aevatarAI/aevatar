import { jsonBody, requestJson, withQuery } from "./http/client";
import { decodeServiceCatalogSnapshots } from "./servicesApi";
import {
  expectArray,
  expectRecord,
  normalizeEnumValue,
  readBoolean,
  readNullableString,
  readNumber,
  readString,
  readStringArray,
  readStringRecord,
} from "./http/decoders";
import type {
  ScopeServiceBindingCatalogSnapshot,
  ScopeServiceBindingInput,
  ScopeServiceEndpointContract,
  ScopeServiceRevisionActionResult,
  ScopeServiceRevisionCatalogSnapshot,
  ScopeServiceRunAuditReport,
  ScopeServiceRunAuditReply,
  ScopeServiceRunAuditSnapshot,
  ScopeServiceRunAuditStep,
  ScopeServiceRunAuditSummary,
  ScopeServiceRunAuditTimelineEvent,
  ScopeServiceRunCatalogSnapshot,
  ScopeServiceRunSummary,
} from "@/shared/models/runtime/scopeServices";
import type {
  BoundConnectorReference,
  BoundSecretReference,
  BoundServiceReference,
  ServiceBindingSnapshot,
} from "@/shared/models/governance";
import type {
  ServiceCatalogSnapshot,
  ServiceCommandAcceptedReceipt,
} from "@/shared/models/services";
import {
  normalizeStudioScopeBindingImplementationKind,
  type StudioScopeBindingRevision,
} from "@/shared/studio/models";

const bindingKindMap = {
  "0": "unspecified",
  "1": "service",
  "2": "connector",
  "3": "secret",
  service_binding_kind_unspecified: "unspecified",
  service_binding_kind_service: "service",
  service_binding_kind_connector: "connector",
  service_binding_kind_secret: "secret",
  unspecified: "unspecified",
  service: "service",
  connector: "connector",
  secret: "secret",
};

const completionStatusMap = {
  "0": "running",
  "1": "completed",
  "2": "timed_out",
  "3": "failed",
  "4": "stopped",
  "5": "not_found",
  "6": "disabled",
  "99": "unknown",
  running: "running",
  completed: "completed",
  timed_out: "timed_out",
  timedout: "timed_out",
  failed: "failed",
  stopped: "stopped",
  not_found: "not_found",
  disabled: "disabled",
  unknown: "unknown",
};

function readOptionalString(
  record: Record<string, unknown>,
  keys: string[],
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
  keys: string[],
): string | number | undefined {
  for (const key of keys) {
    const rawValue = record[key];
    if (typeof rawValue === "string") {
      const normalized = rawValue.trim();
      if (normalized) {
        return normalized;
      }
      continue;
    }

    if (typeof rawValue === "number" && !Number.isNaN(rawValue)) {
      return rawValue;
    }
  }

  return undefined;
}

function readNullableBoolean(
  record: Record<string, unknown>,
  keys: string[],
): boolean | null {
  for (const key of keys) {
    if (!(key in record)) {
      continue;
    }

    const rawValue = record[key];
    if (rawValue === null || rawValue === undefined) {
      return null;
    }

    if (typeof rawValue === "boolean") {
      return rawValue;
    }

    throw new Error(`${key} must be a boolean or null.`);
  }

  return null;
}

function readNullableNumber(
  record: Record<string, unknown>,
  keys: string[],
  label: string,
): number | null {
  for (const key of keys) {
    if (!(key in record)) {
      continue;
    }

    const rawValue = record[key];
    if (rawValue === null || rawValue === undefined) {
      return null;
    }

    if (typeof rawValue === "number" && !Number.isNaN(rawValue)) {
      return rawValue;
    }

    throw new Error(`${label} must be a number or null.`);
  }

  return null;
}

function decodeServiceCommandAcceptedReceipt(
  value: unknown,
  label = "ServiceCommandAcceptedReceipt",
): ServiceCommandAcceptedReceipt {
  const record = expectRecord(value, label);
  return {
    targetActorId: readString(
      record,
      ["targetActorId", "TargetActorId"],
      `${label}.targetActorId`,
    ),
    commandId: readString(
      record,
      ["commandId", "CommandId"],
      `${label}.commandId`,
    ),
    correlationId: readString(
      record,
      ["correlationId", "CorrelationId"],
      `${label}.correlationId`,
    ),
  };
}

function decodeBoundServiceReference(
  value: unknown,
  label = "BoundServiceReference",
): BoundServiceReference {
  const record = expectRecord(value, label);
  const identityRecord = expectRecord(
    record.identity ?? record.Identity,
    `${label}.identity`,
  );

  return {
    identity: {
      tenantId: readString(
        identityRecord,
        ["tenantId", "TenantId"],
        `${label}.identity.tenantId`,
      ),
      appId: readString(
        identityRecord,
        ["appId", "AppId"],
        `${label}.identity.appId`,
      ),
      namespace: readString(
        identityRecord,
        ["namespace", "Namespace"],
        `${label}.identity.namespace`,
      ),
      serviceId: readString(
        identityRecord,
        ["serviceId", "ServiceId"],
        `${label}.identity.serviceId`,
      ),
    },
    endpointId: readString(
      record,
      ["endpointId", "EndpointId"],
      `${label}.endpointId`,
    ),
  };
}

function decodeBoundConnectorReference(
  value: unknown,
  label = "BoundConnectorReference",
): BoundConnectorReference {
  const record = expectRecord(value, label);
  return {
    connectorType: readString(
      record,
      ["connectorType", "ConnectorType"],
      `${label}.connectorType`,
    ),
    connectorId: readString(
      record,
      ["connectorId", "ConnectorId"],
      `${label}.connectorId`,
    ),
  };
}

function decodeBoundSecretReference(
  value: unknown,
  label = "BoundSecretReference",
): BoundSecretReference {
  const record = expectRecord(value, label);
  return {
    secretName: readString(
      record,
      ["secretName", "SecretName"],
      `${label}.secretName`,
    ),
  };
}

function decodeServiceBindingSnapshot(
  value: unknown,
  label = "ServiceBindingSnapshot",
): ServiceBindingSnapshot {
  const record = expectRecord(value, label);
  const serviceRef = record.serviceRef ?? record.ServiceRef;
  const connectorRef = record.connectorRef ?? record.ConnectorRef;
  const secretRef = record.secretRef ?? record.SecretRef;

  return {
    bindingId: readString(
      record,
      ["bindingId", "BindingId"],
      `${label}.bindingId`,
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`,
    ),
    bindingKind: normalizeEnumValue(
      record.bindingKind ?? record.BindingKind,
      `${label}.bindingKind`,
      bindingKindMap,
    ),
    policyIds: readStringArray(
      record,
      ["policyIds", "PolicyIds"],
      `${label}.policyIds`,
    ),
    retired: readBoolean(record, ["retired", "Retired"], `${label}.retired`),
    serviceRef:
      serviceRef === undefined || serviceRef === null
        ? null
        : decodeBoundServiceReference(serviceRef, `${label}.serviceRef`),
    connectorRef:
      connectorRef === undefined || connectorRef === null
        ? null
        : decodeBoundConnectorReference(connectorRef, `${label}.connectorRef`),
    secretRef:
      secretRef === undefined || secretRef === null
        ? null
        : decodeBoundSecretReference(secretRef, `${label}.secretRef`),
  };
}

function decodeServiceBindingCatalogSnapshot(
  value: unknown,
  label = "ScopeServiceBindingCatalogSnapshot",
): ScopeServiceBindingCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`,
    ),
    bindings: expectArray(
      record.bindings ?? record.Bindings,
      `${label}.bindings`,
      decodeServiceBindingSnapshot,
    ),
    updatedAt: readNullableString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`,
    ),
  };
}

function readImplementationKind(
  record: Record<string, unknown>,
  label: string,
): StudioScopeBindingRevision["implementationKind"] {
  const rawValue =
    readOptionalScalar(record, ["implementationKind", "ImplementationKind"]) ??
    "unknown";
  return normalizeStudioScopeBindingImplementationKind(
    normalizeEnumValue(rawValue, `${label}.implementationKind`, {
      "0": "unknown",
      "1": "workflow",
      "2": "script",
      "3": "gagent",
      workflow: "workflow",
      scripting: "script",
      script: "script",
      gagent: "gagent",
      unspecified: "unknown",
    }),
  );
}

function decodeScopeServiceRevision(
  value: unknown,
  label = "ScopeServiceRevision",
): StudioScopeBindingRevision {
  const record = expectRecord(value, label);
  return {
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`,
    ),
    implementationKind: readImplementationKind(record, label),
    status: readString(record, ["status", "Status"], `${label}.status`),
    artifactHash: readString(
      record,
      ["artifactHash", "ArtifactHash"],
      `${label}.artifactHash`,
    ),
    failureReason: readString(
      record,
      ["failureReason", "FailureReason"],
      `${label}.failureReason`,
    ),
    isDefaultServing: readBoolean(
      record,
      ["isDefaultServing", "IsDefaultServing"],
      `${label}.isDefaultServing`,
    ),
    isActiveServing: readBoolean(
      record,
      ["isActiveServing", "IsActiveServing"],
      `${label}.isActiveServing`,
    ),
    isServingTarget: readBoolean(
      record,
      ["isServingTarget", "IsServingTarget"],
      `${label}.isServingTarget`,
    ),
    allocationWeight: readNumber(
      record,
      ["allocationWeight", "AllocationWeight"],
      `${label}.allocationWeight`,
    ),
    servingState: readString(
      record,
      ["servingState", "ServingState"],
      `${label}.servingState`,
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`,
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`,
    ),
    createdAt: readNullableString(
      record,
      ["createdAt", "CreatedAt"],
      `${label}.createdAt`,
    ),
    preparedAt: readNullableString(
      record,
      ["preparedAt", "PreparedAt"],
      `${label}.preparedAt`,
    ),
    publishedAt: readNullableString(
      record,
      ["publishedAt", "PublishedAt"],
      `${label}.publishedAt`,
    ),
    retiredAt: readNullableString(
      record,
      ["retiredAt", "RetiredAt"],
      `${label}.retiredAt`,
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
            `${label}.inlineWorkflowCount`,
          ),
    scriptId: readOptionalString(record, ["scriptId", "ScriptId"]) || "",
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

function decodeScopeServiceRevisionCatalogSnapshot(
  value: unknown,
  label = "ScopeServiceRevisionCatalogSnapshot",
): ScopeServiceRevisionCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`,
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`,
    ),
    defaultServingRevisionId: readString(
      record,
      ["defaultServingRevisionId", "DefaultServingRevisionId"],
      `${label}.defaultServingRevisionId`,
    ),
    activeServingRevisionId: readString(
      record,
      ["activeServingRevisionId", "ActiveServingRevisionId"],
      `${label}.activeServingRevisionId`,
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`,
    ),
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      `${label}.deploymentStatus`,
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`,
    ),
    catalogStateVersion: readNumber(
      record,
      ["catalogStateVersion", "CatalogStateVersion"],
      `${label}.catalogStateVersion`,
    ),
    catalogLastEventId: readString(
      record,
      ["catalogLastEventId", "CatalogLastEventId"],
      `${label}.catalogLastEventId`,
    ),
    updatedAt: readNullableString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`,
    ),
    revisions: expectArray(
      record.revisions ?? record.Revisions,
      `${label}.revisions`,
      decodeScopeServiceRevision,
    ),
  };
}

function decodeScopeServiceRevisionActionResult(
  value: unknown,
  label = "ScopeServiceRevisionActionResult",
): ScopeServiceRevisionActionResult {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`,
    ),
    status: readString(record, ["status", "Status"], `${label}.status`),
  };
}

function decodeScopeServiceEndpointContract(
  value: unknown,
  label = "ScopeServiceEndpointContract",
): ScopeServiceEndpointContract {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    endpointId: readString(
      record,
      ["endpointId", "EndpointId"],
      `${label}.endpointId`,
    ),
    invokePath: readString(
      record,
      ["invokePath", "InvokePath"],
      `${label}.invokePath`,
    ),
    method: readString(record, ["method", "Method"], `${label}.method`),
    requestContentType: readString(
      record,
      ["requestContentType", "RequestContentType"],
      `${label}.requestContentType`,
    ),
    responseContentType: readString(
      record,
      ["responseContentType", "ResponseContentType"],
      `${label}.responseContentType`,
    ),
    requestTypeUrl: readString(
      record,
      ["requestTypeUrl", "RequestTypeUrl"],
      `${label}.requestTypeUrl`,
    ),
    responseTypeUrl: readString(
      record,
      ["responseTypeUrl", "ResponseTypeUrl"],
      `${label}.responseTypeUrl`,
    ),
    supportsSse: readBoolean(
      record,
      ["supportsSse", "SupportsSse"],
      `${label}.supportsSse`,
    ),
    supportsWebSocket: readBoolean(
      record,
      ["supportsWebSocket", "SupportsWebSocket"],
      `${label}.supportsWebSocket`,
    ),
    supportsAguiFrames: readBoolean(
      record,
      ["supportsAguiFrames", "SupportsAguiFrames"],
      `${label}.supportsAguiFrames`,
    ),
    streamFrameFormat: readNullableString(
      record,
      ["streamFrameFormat", "StreamFrameFormat"],
      `${label}.streamFrameFormat`,
    ),
    smokeTestSupported: readBoolean(
      record,
      ["smokeTestSupported", "SmokeTestSupported"],
      `${label}.smokeTestSupported`,
    ),
    defaultSmokeInputMode: readString(
      record,
      ["defaultSmokeInputMode", "DefaultSmokeInputMode"],
      `${label}.defaultSmokeInputMode`,
    ) as ScopeServiceEndpointContract["defaultSmokeInputMode"],
    defaultSmokePrompt: readNullableString(
      record,
      ["defaultSmokePrompt", "DefaultSmokePrompt"],
      `${label}.defaultSmokePrompt`,
    ),
    sampleRequestJson: readNullableString(
      record,
      ["sampleRequestJson", "SampleRequestJson"],
      `${label}.sampleRequestJson`,
    ),
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      `${label}.deploymentStatus`,
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`,
    ),
    curlExample: readNullableString(
      record,
      ["curlExample", "CurlExample"],
      `${label}.curlExample`,
    ),
    fetchExample: readNullableString(
      record,
      ["fetchExample", "FetchExample"],
      `${label}.fetchExample`,
    ),
  };
}

function decodeScopeServiceRunSummary(
  value: unknown,
  label = "ScopeServiceRunSummary",
): ScopeServiceRunSummary {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    runId: readString(record, ["runId", "RunId"], `${label}.runId`),
    actorId: readString(record, ["actorId", "ActorId"], `${label}.actorId`),
    definitionActorId: readString(
      record,
      ["definitionActorId", "DefinitionActorId"],
      `${label}.definitionActorId`,
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`,
    ),
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`,
    ),
    workflowName: readString(
      record,
      ["workflowName", "WorkflowName"],
      `${label}.workflowName`,
    ),
    completionStatus: normalizeEnumValue(
      record.completionStatus ?? record.CompletionStatus,
      `${label}.completionStatus`,
      completionStatusMap,
    ),
    stateVersion: readNumber(
      record,
      ["stateVersion", "StateVersion"],
      `${label}.stateVersion`,
    ),
    lastEventId: readString(
      record,
      ["lastEventId", "LastEventId"],
      `${label}.lastEventId`,
    ),
    lastUpdatedAt: readNullableString(
      record,
      ["lastUpdatedAt", "LastUpdatedAt"],
      `${label}.lastUpdatedAt`,
    ),
    boundAt: readNullableString(
      record,
      ["boundAt", "BoundAt"],
      `${label}.boundAt`,
    ),
    bindingUpdatedAt: readNullableString(
      record,
      ["bindingUpdatedAt", "BindingUpdatedAt"],
      `${label}.bindingUpdatedAt`,
    ),
    lastSuccess: readNullableBoolean(record, ["lastSuccess", "LastSuccess"]),
    totalSteps: readNumber(
      record,
      ["totalSteps", "TotalSteps"],
      `${label}.totalSteps`,
    ),
    completedSteps: readNumber(
      record,
      ["completedSteps", "CompletedSteps"],
      `${label}.completedSteps`,
    ),
    roleReplyCount: readNumber(
      record,
      ["roleReplyCount", "RoleReplyCount"],
      `${label}.roleReplyCount`,
    ),
    lastOutput: readString(
      record,
      ["lastOutput", "LastOutput"],
      `${label}.lastOutput`,
    ),
    lastError: readString(
      record,
      ["lastError", "LastError"],
      `${label}.lastError`,
    ),
  };
}

function decodeScopeServiceRunCatalogSnapshot(
  value: unknown,
  label = "ScopeServiceRunCatalogSnapshot",
): ScopeServiceRunCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    scopeId: readString(record, ["scopeId", "ScopeId"], `${label}.scopeId`),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`,
    ),
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`,
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`,
    ),
    runs: expectArray(
      record.runs ?? record.Runs,
      `${label}.runs`,
      decodeScopeServiceRunSummary,
    ),
  };
}

function decodeStepTypeCounts(
  value: unknown,
  label: string,
): Readonly<Record<string, number>> {
  const record = expectRecord(value, label);
  return Object.fromEntries(
    Object.entries(record).map(([key, rawValue]) => {
      if (typeof rawValue !== "number" || Number.isNaN(rawValue)) {
        throw new Error(`${label}.${key} must be a number.`);
      }
      return [key, rawValue];
    }),
  );
}

function decodeScopeServiceRunAuditSummary(
  value: unknown,
  label = "ScopeServiceRunAuditSummary",
): ScopeServiceRunAuditSummary {
  const record = expectRecord(value, label);
  return {
    totalSteps: readNumber(
      record,
      ["totalSteps", "TotalSteps"],
      `${label}.totalSteps`,
    ),
    requestedSteps: readNumber(
      record,
      ["requestedSteps", "RequestedSteps"],
      `${label}.requestedSteps`,
    ),
    completedSteps: readNumber(
      record,
      ["completedSteps", "CompletedSteps"],
      `${label}.completedSteps`,
    ),
    roleReplyCount: readNumber(
      record,
      ["roleReplyCount", "RoleReplyCount"],
      `${label}.roleReplyCount`,
    ),
    stepTypeCounts: decodeStepTypeCounts(
      record.stepTypeCounts ?? record.StepTypeCounts ?? {},
      `${label}.stepTypeCounts`,
    ),
  };
}

function decodeScopeServiceRunAuditStep(
  value: unknown,
  label = "ScopeServiceRunAuditStep",
): ScopeServiceRunAuditStep {
  const record = expectRecord(value, label);
  return {
    stepId: readString(record, ["stepId", "StepId"], `${label}.stepId`),
    stepType: readString(record, ["stepType", "StepType"], `${label}.stepType`),
    targetRole: readString(
      record,
      ["targetRole", "TargetRole"],
      `${label}.targetRole`,
    ),
    requestedAt: readNullableString(
      record,
      ["requestedAt", "RequestedAt"],
      `${label}.requestedAt`,
    ),
    completedAt: readNullableString(
      record,
      ["completedAt", "CompletedAt"],
      `${label}.completedAt`,
    ),
    success: readNullableBoolean(record, ["success", "Success"]),
    workerId: readString(record, ["workerId", "WorkerId"], `${label}.workerId`),
    outputPreview: readString(
      record,
      ["outputPreview", "OutputPreview"],
      `${label}.outputPreview`,
    ),
    error: readString(record, ["error", "Error"], `${label}.error`),
    requestParameters: readStringRecord(
      record,
      ["requestParameters", "RequestParameters"],
      `${label}.requestParameters`,
    ),
    completionAnnotations: readStringRecord(
      record,
      ["completionAnnotations", "CompletionAnnotations"],
      `${label}.completionAnnotations`,
    ),
    nextStepId: readString(
      record,
      ["nextStepId", "NextStepId"],
      `${label}.nextStepId`,
    ),
    branchKey: readString(
      record,
      ["branchKey", "BranchKey"],
      `${label}.branchKey`,
    ),
    assignedVariable: readString(
      record,
      ["assignedVariable", "AssignedVariable"],
      `${label}.assignedVariable`,
    ),
    assignedValue: readString(
      record,
      ["assignedValue", "AssignedValue"],
      `${label}.assignedValue`,
    ),
    suspensionType: readString(
      record,
      ["suspensionType", "SuspensionType"],
      `${label}.suspensionType`,
    ),
    suspensionPrompt: readString(
      record,
      ["suspensionPrompt", "SuspensionPrompt"],
      `${label}.suspensionPrompt`,
    ),
    suspensionTimeoutSeconds: readNullableNumber(
      record,
      ["suspensionTimeoutSeconds", "SuspensionTimeoutSeconds"],
      `${label}.suspensionTimeoutSeconds`,
    ),
    requestedVariableName: readString(
      record,
      ["requestedVariableName", "RequestedVariableName"],
      `${label}.requestedVariableName`,
    ),
    durationMs: readNullableNumber(
      record,
      ["durationMs", "DurationMs"],
      `${label}.durationMs`,
    ),
  };
}

function decodeScopeServiceRunAuditReply(
  value: unknown,
  label = "ScopeServiceRunAuditReply",
): ScopeServiceRunAuditReply {
  const record = expectRecord(value, label);
  return {
    timestamp: readNullableString(
      record,
      ["timestamp", "Timestamp"],
      `${label}.timestamp`,
    ),
    roleId: readString(record, ["roleId", "RoleId"], `${label}.roleId`),
    sessionId: readString(
      record,
      ["sessionId", "SessionId"],
      `${label}.sessionId`,
    ),
    content: readString(record, ["content", "Content"], `${label}.content`),
    contentLength: readNumber(
      record,
      ["contentLength", "ContentLength"],
      `${label}.contentLength`,
    ),
  };
}

function decodeScopeServiceRunAuditTimelineEvent(
  value: unknown,
  label = "ScopeServiceRunAuditTimelineEvent",
): ScopeServiceRunAuditTimelineEvent {
  const record = expectRecord(value, label);
  return {
    timestamp: readNullableString(
      record,
      ["timestamp", "Timestamp"],
      `${label}.timestamp`,
    ),
    stage: readString(record, ["stage", "Stage"], `${label}.stage`),
    message: readString(record, ["message", "Message"], `${label}.message`),
    agentId: readString(record, ["agentId", "AgentId"], `${label}.agentId`),
    stepId: readString(record, ["stepId", "StepId"], `${label}.stepId`),
    stepType: readString(record, ["stepType", "StepType"], `${label}.stepType`),
    eventType: readString(
      record,
      ["eventType", "EventType"],
      `${label}.eventType`,
    ),
    data: readStringRecord(record, ["data", "Data"], `${label}.data`),
  };
}

function decodeScopeServiceRunAuditReport(
  value: unknown,
  label = "ScopeServiceRunAuditReport",
): ScopeServiceRunAuditReport {
  const record = expectRecord(value, label);
  return {
    reportVersion: readString(
      record,
      ["reportVersion", "ReportVersion"],
      `${label}.reportVersion`,
    ),
    projectionScope: normalizeEnumValue(
      record.projectionScope ?? record.ProjectionScope ?? "unknown",
      `${label}.projectionScope`,
      {
        "0": "actor_shared",
        "1": "run_isolated",
        "99": "unknown",
        actor_shared: "actor_shared",
        run_isolated: "run_isolated",
        unknown: "unknown",
      },
    ),
    topologySource: normalizeEnumValue(
      record.topologySource ?? record.TopologySource ?? "unknown",
      `${label}.topologySource`,
      {
        "0": "runtime_snapshot",
        "99": "unknown",
        runtime_snapshot: "runtime_snapshot",
        unknown: "unknown",
      },
    ),
    completionStatus: normalizeEnumValue(
      record.completionStatus ?? record.CompletionStatus ?? "unknown",
      `${label}.completionStatus`,
      completionStatusMap,
    ),
    workflowName: readString(
      record,
      ["workflowName", "WorkflowName"],
      `${label}.workflowName`,
    ),
    rootActorId: readString(
      record,
      ["rootActorId", "RootActorId"],
      `${label}.rootActorId`,
    ),
    commandId: readString(record, ["commandId", "CommandId"], `${label}.commandId`),
    stateVersion: readNumber(
      record,
      ["stateVersion", "StateVersion"],
      `${label}.stateVersion`,
    ),
    lastEventId: readString(
      record,
      ["lastEventId", "LastEventId"],
      `${label}.lastEventId`,
    ),
    createdAt: readNullableString(
      record,
      ["createdAt", "CreatedAt"],
      `${label}.createdAt`,
    ),
    updatedAt: readNullableString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`,
    ),
    startedAt: readNullableString(
      record,
      ["startedAt", "StartedAt"],
      `${label}.startedAt`,
    ),
    endedAt: readNullableString(
      record,
      ["endedAt", "EndedAt"],
      `${label}.endedAt`,
    ),
    durationMs: readNumber(record, ["durationMs", "DurationMs"], `${label}.durationMs`),
    success: readNullableBoolean(record, ["success", "Success"]),
    input: readString(record, ["input", "Input"], `${label}.input`),
    finalOutput: readString(
      record,
      ["finalOutput", "FinalOutput"],
      `${label}.finalOutput`,
    ),
    finalError: readString(
      record,
      ["finalError", "FinalError"],
      `${label}.finalError`,
    ),
    topology: expectArray(
      record.topology ?? record.Topology ?? [],
      `${label}.topology`,
      (entry, nestedLabel) => {
        const nestedRecord = expectRecord(entry, nestedLabel || `${label}.topology[]`);
        return {
          parent: readString(
            nestedRecord,
            ["parent", "Parent"],
            `${nestedLabel}.parent`,
          ),
          child: readString(
            nestedRecord,
            ["child", "Child"],
            `${nestedLabel}.child`,
          ),
        };
      },
    ),
    steps: expectArray(
      record.steps ?? record.Steps ?? [],
      `${label}.steps`,
      decodeScopeServiceRunAuditStep,
    ),
    roleReplies: expectArray(
      record.roleReplies ?? record.RoleReplies ?? [],
      `${label}.roleReplies`,
      decodeScopeServiceRunAuditReply,
    ),
    timeline: expectArray(
      record.timeline ?? record.Timeline ?? [],
      `${label}.timeline`,
      decodeScopeServiceRunAuditTimelineEvent,
    ),
    summary: decodeScopeServiceRunAuditSummary(
      record.summary ?? record.Summary ?? {},
      `${label}.summary`,
    ),
  };
}

function decodeScopeServiceRunAuditSnapshot(
  value: unknown,
  label = "ScopeServiceRunAuditSnapshot",
): ScopeServiceRunAuditSnapshot {
  const record = expectRecord(value, label);
  return {
    summary: decodeScopeServiceRunSummary(
      record.summary ?? record.Summary,
      `${label}.summary`,
    ),
    audit: decodeScopeServiceRunAuditReport(
      record.audit ?? record.Audit,
      `${label}.audit`,
    ),
  };
}

function encodeScopeServiceBindingPayload(input: ScopeServiceBindingInput) {
  return {
    bindingId: input.bindingId.trim(),
    displayName: input.displayName.trim(),
    bindingKind: input.bindingKind.trim(),
    policyIds: (input.policyIds ?? []).map((item) => item.trim()).filter(Boolean),
    service: input.service
      ? {
          serviceId: input.service.serviceId.trim(),
          endpointId: input.service.endpointId?.trim() || undefined,
        }
      : null,
    connector: input.connector
      ? {
          connectorType: input.connector.connectorType.trim(),
          connectorId: input.connector.connectorId.trim(),
        }
      : null,
    secret: input.secret
      ? {
          secretName: input.secret.secretName.trim(),
        }
      : null,
  };
}

export const scopeRuntimeApi = {
  listServices(
    scopeId: string,
    query?: { appId?: string; take?: number },
  ): Promise<ServiceCatalogSnapshot[]> {
    return requestJson(
      withQuery(`/api/scopes/${encodeURIComponent(scopeId)}/services`, {
        appId: query?.appId?.trim(),
        take: query?.take,
      }),
      decodeServiceCatalogSnapshots,
    );
  },

  getServiceBindings(
    scopeId: string,
    serviceId: string,
  ): Promise<ScopeServiceBindingCatalogSnapshot> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/bindings`,
      decodeServiceBindingCatalogSnapshot,
    );
  },

  createServiceBinding(
    scopeId: string,
    serviceId: string,
    input: ScopeServiceBindingInput,
  ): Promise<ServiceCommandAcceptedReceipt> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/bindings`,
      decodeServiceCommandAcceptedReceipt,
      {
        method: "POST",
        ...jsonBody(encodeScopeServiceBindingPayload(input)),
      },
    );
  },

  updateServiceBinding(
    scopeId: string,
    serviceId: string,
    bindingId: string,
    input: ScopeServiceBindingInput,
  ): Promise<ServiceCommandAcceptedReceipt> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/bindings/${encodeURIComponent(bindingId)}`,
      decodeServiceCommandAcceptedReceipt,
      {
        method: "PUT",
        ...jsonBody(encodeScopeServiceBindingPayload(input)),
      },
    );
  },

  retireServiceBinding(
    scopeId: string,
    serviceId: string,
    bindingId: string,
  ): Promise<ServiceCommandAcceptedReceipt> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/bindings/${encodeURIComponent(bindingId)}:retire`,
      decodeServiceCommandAcceptedReceipt,
      {
        method: "POST",
        ...jsonBody({}),
      },
    );
  },

  getServiceRevisions(
    scopeId: string,
    serviceId: string,
  ): Promise<ScopeServiceRevisionCatalogSnapshot> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/revisions`,
      decodeScopeServiceRevisionCatalogSnapshot,
    );
  },

  getServiceRevision(
    scopeId: string,
    serviceId: string,
    revisionId: string,
  ): Promise<StudioScopeBindingRevision> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/revisions/${encodeURIComponent(revisionId)}`,
      decodeScopeServiceRevision,
    );
  },

  getServiceEndpointContract(
    scopeId: string,
    serviceId: string,
    endpointId: string,
  ): Promise<ScopeServiceEndpointContract> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/endpoints/${encodeURIComponent(endpointId)}/contract`,
      decodeScopeServiceEndpointContract,
    );
  },

  retireServiceRevision(
    scopeId: string,
    serviceId: string,
    revisionId: string,
  ): Promise<ScopeServiceRevisionActionResult> {
    return requestJson(
      `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/revisions/${encodeURIComponent(revisionId)}:retire`,
      decodeScopeServiceRevisionActionResult,
      {
        method: "POST",
        ...jsonBody({}),
      },
    );
  },

  listServiceRuns(
    scopeId: string,
    serviceId: string,
    options?: {
      take?: number;
    },
  ): Promise<ScopeServiceRunCatalogSnapshot> {
    return requestJson(
      withQuery(
        `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/runs`,
        {
          take: options?.take,
        },
      ),
      decodeScopeServiceRunCatalogSnapshot,
    );
  },

  listMemberRuns(
    scopeId: string,
    memberId: string,
    options?: {
      take?: number;
    },
  ): Promise<ScopeServiceRunCatalogSnapshot> {
    return requestJson(
      withQuery(
        `/api/scopes/${encodeURIComponent(scopeId)}/members/${encodeURIComponent(memberId)}/runs`,
        {
          take: options?.take,
        },
      ),
      decodeScopeServiceRunCatalogSnapshot,
    );
  },

  getServiceRunAudit(
    scopeId: string,
    serviceId: string,
    runId: string,
    options?: {
      actorId?: string;
    },
  ): Promise<ScopeServiceRunAuditSnapshot> {
    return requestJson(
      withQuery(
        `/api/scopes/${encodeURIComponent(scopeId)}/services/${encodeURIComponent(serviceId)}/runs/${encodeURIComponent(runId)}/audit`,
        {
          actorId: options?.actorId?.trim(),
        },
      ),
      decodeScopeServiceRunAuditSnapshot,
    );
  },

  getMemberRunAudit(
    scopeId: string,
    memberId: string,
    runId: string,
    options?: {
      actorId?: string;
    },
  ): Promise<ScopeServiceRunAuditSnapshot> {
    return requestJson(
      withQuery(
        `/api/scopes/${encodeURIComponent(scopeId)}/members/${encodeURIComponent(memberId)}/runs/${encodeURIComponent(runId)}/audit`,
        {
          actorId: options?.actorId?.trim(),
        },
      ),
      decodeScopeServiceRunAuditSnapshot,
    );
  },
};
