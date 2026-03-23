import { requestJson, withQuery } from "./http/client";
import {
  expectArray,
  expectRecord,
  normalizeEnumValue,
  readNumber,
  readNullableString,
  readString,
  readStringArray,
  type Decoder,
} from "./http/decoders";
import type {
  ServiceCatalogSnapshot,
  ServiceDeploymentCatalogSnapshot,
  ServiceDeploymentSnapshot,
  ServiceEndpointSnapshot,
  ServiceIdentityQuery,
  ServiceRevisionCatalogSnapshot,
  ServiceRevisionSnapshot,
  ServiceRolloutSnapshot,
  ServiceRolloutStageSnapshot,
  ServiceServingSetSnapshot,
  ServiceServingTargetSnapshot,
  ServiceTrafficEndpointSnapshot,
  ServiceTrafficTargetSnapshot,
  ServiceTrafficViewSnapshot,
} from "@/shared/models/services";

const serviceEndpointKindMap = {
  "0": "unspecified",
  "1": "command",
  "2": "chat",
  service_endpoint_kind_unspecified: "unspecified",
  service_endpoint_kind_command: "command",
  service_endpoint_kind_chat: "chat",
  unspecified: "unspecified",
  command: "command",
  chat: "chat",
};

function decodeServiceEndpointSnapshot(
  value: unknown,
  label = "ServiceEndpointSnapshot"
): ServiceEndpointSnapshot {
  const record = expectRecord(value, label);
  return {
    endpointId: readString(
      record,
      ["endpointId", "EndpointId"],
      `${label}.endpointId`
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
    kind: normalizeEnumValue(
      record.kind ?? record.Kind,
      `${label}.kind`,
      serviceEndpointKindMap
    ),
    requestTypeUrl: readString(
      record,
      ["requestTypeUrl", "RequestTypeUrl"],
      `${label}.requestTypeUrl`
    ),
    responseTypeUrl: readString(
      record,
      ["responseTypeUrl", "ResponseTypeUrl"],
      `${label}.responseTypeUrl`
    ),
    description: readString(
      record,
      ["description", "Description"],
      `${label}.description`
    ),
  };
}

function decodeServiceCatalogSnapshot(
  value: unknown,
  label = "ServiceCatalogSnapshot"
): ServiceCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    tenantId: readString(record, ["tenantId", "TenantId"], `${label}.tenantId`),
    appId: readString(record, ["appId", "AppId"], `${label}.appId`),
    namespace: readString(
      record,
      ["namespace", "Namespace"],
      `${label}.namespace`
    ),
    serviceId: readString(
      record,
      ["serviceId", "ServiceId"],
      `${label}.serviceId`
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
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
    deploymentStatus: readString(
      record,
      ["deploymentStatus", "DeploymentStatus"],
      `${label}.deploymentStatus`
    ),
    endpoints: expectArray(
      record.endpoints ?? record.Endpoints,
      `${label}.endpoints`,
      decodeServiceEndpointSnapshot
    ),
    policyIds: readStringArray(
      record,
      ["policyIds", "PolicyIds"],
      `${label}.policyIds`
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceRevisionSnapshot(
  value: unknown,
  label = "ServiceRevisionSnapshot"
): ServiceRevisionSnapshot {
  const record = expectRecord(value, label);
  return {
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    implementationKind: readString(
      record,
      ["implementationKind", "ImplementationKind"],
      `${label}.implementationKind`
    ),
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
    endpoints: expectArray(
      record.endpoints ?? record.Endpoints,
      `${label}.endpoints`,
      decodeServiceEndpointSnapshot
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

function decodeServiceRevisionCatalogSnapshot(
  value: unknown,
  label = "ServiceRevisionCatalogSnapshot"
): ServiceRevisionCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    revisions: expectArray(
      record.revisions ?? record.Revisions,
      `${label}.revisions`,
      decodeServiceRevisionSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceDeploymentSnapshot(
  value: unknown,
  label = "ServiceDeploymentSnapshot"
): ServiceDeploymentSnapshot {
  const record = expectRecord(value, label);
  return {
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`
    ),
    status: readString(record, ["status", "Status"], `${label}.status`),
    activatedAt: readNullableString(
      record,
      ["activatedAt", "ActivatedAt"],
      `${label}.activatedAt`
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceDeploymentCatalogSnapshot(
  value: unknown,
  label = "ServiceDeploymentCatalogSnapshot"
): ServiceDeploymentCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    deployments: expectArray(
      record.deployments ?? record.Deployments,
      `${label}.deployments`,
      decodeServiceDeploymentSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceServingTargetSnapshot(
  value: unknown,
  label = "ServiceServingTargetSnapshot"
): ServiceServingTargetSnapshot {
  const record = expectRecord(value, label);
  return {
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`
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
    enabledEndpointIds: readStringArray(
      record,
      ["enabledEndpointIds", "EnabledEndpointIds"],
      `${label}.enabledEndpointIds`
    ),
  };
}

function decodeServiceServingSetSnapshot(
  value: unknown,
  label = "ServiceServingSetSnapshot"
): ServiceServingSetSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    generation: readNumber(
      record,
      ["generation", "Generation"],
      `${label}.generation`
    ),
    activeRolloutId: readString(
      record,
      ["activeRolloutId", "ActiveRolloutId"],
      `${label}.activeRolloutId`
    ),
    targets: expectArray(
      record.targets ?? record.Targets,
      `${label}.targets`,
      decodeServiceServingTargetSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceRolloutStageSnapshot(
  value: unknown,
  label = "ServiceRolloutStageSnapshot"
): ServiceRolloutStageSnapshot {
  const record = expectRecord(value, label);
  return {
    stageId: readString(record, ["stageId", "StageId"], `${label}.stageId`),
    stageIndex: readNumber(
      record,
      ["stageIndex", "StageIndex"],
      `${label}.stageIndex`
    ),
    targets: expectArray(
      record.targets ?? record.Targets,
      `${label}.targets`,
      decodeServiceServingTargetSnapshot
    ),
  };
}

function decodeServiceRolloutSnapshot(
  value: unknown,
  label = "ServiceRolloutSnapshot"
): ServiceRolloutSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    rolloutId: readString(
      record,
      ["rolloutId", "RolloutId"],
      `${label}.rolloutId`
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
    status: readString(record, ["status", "Status"], `${label}.status`),
    currentStageIndex: readNumber(
      record,
      ["currentStageIndex", "CurrentStageIndex"],
      `${label}.currentStageIndex`
    ),
    stages: expectArray(
      record.stages ?? record.Stages,
      `${label}.stages`,
      decodeServiceRolloutStageSnapshot
    ),
    baselineTargets: expectArray(
      record.baselineTargets ?? record.BaselineTargets,
      `${label}.baselineTargets`,
      decodeServiceServingTargetSnapshot
    ),
    failureReason: readString(
      record,
      ["failureReason", "FailureReason"],
      `${label}.failureReason`
    ),
    startedAt: readNullableString(
      record,
      ["startedAt", "StartedAt"],
      `${label}.startedAt`
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceTrafficTargetSnapshot(
  value: unknown,
  label = "ServiceTrafficTargetSnapshot"
): ServiceTrafficTargetSnapshot {
  const record = expectRecord(value, label);
  return {
    deploymentId: readString(
      record,
      ["deploymentId", "DeploymentId"],
      `${label}.deploymentId`
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    primaryActorId: readString(
      record,
      ["primaryActorId", "PrimaryActorId"],
      `${label}.primaryActorId`
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
  };
}

function decodeServiceTrafficEndpointSnapshot(
  value: unknown,
  label = "ServiceTrafficEndpointSnapshot"
): ServiceTrafficEndpointSnapshot {
  const record = expectRecord(value, label);
  return {
    endpointId: readString(
      record,
      ["endpointId", "EndpointId"],
      `${label}.endpointId`
    ),
    targets: expectArray(
      record.targets ?? record.Targets,
      `${label}.targets`,
      decodeServiceTrafficTargetSnapshot
    ),
  };
}

function decodeServiceTrafficViewSnapshot(
  value: unknown,
  label = "ServiceTrafficViewSnapshot"
): ServiceTrafficViewSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    generation: readNumber(
      record,
      ["generation", "Generation"],
      `${label}.generation`
    ),
    activeRolloutId: readString(
      record,
      ["activeRolloutId", "ActiveRolloutId"],
      `${label}.activeRolloutId`
    ),
    endpoints: expectArray(
      record.endpoints ?? record.Endpoints,
      `${label}.endpoints`,
      decodeServiceTrafficEndpointSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

const decodeServiceCatalogSnapshots: Decoder<ServiceCatalogSnapshot[]> = (
  value
) =>
  expectArray(value, "ServiceCatalogSnapshot[]", decodeServiceCatalogSnapshot);

function buildQuery(query: ServiceIdentityQuery): string {
  return withQuery("", {
    tenantId: query.tenantId?.trim(),
    appId: query.appId?.trim(),
    namespace: query.namespace?.trim(),
    take: query.take,
  });
}

export const servicesApi = {
  listServices(query: ServiceIdentityQuery): Promise<ServiceCatalogSnapshot[]> {
    return requestJson(
      `/api/services${buildQuery(query)}`,
      decodeServiceCatalogSnapshots
    );
  },

  getService(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceCatalogSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceCatalogSnapshot(value, "ServiceCatalogSnapshot")
    );
  },

  getRevisions(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceRevisionCatalogSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}/revisions`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceRevisionCatalogSnapshot(
              value,
              "ServiceRevisionCatalogSnapshot"
            )
    );
  },

  getDeployments(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceDeploymentCatalogSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}/deployments`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceDeploymentCatalogSnapshot(
              value,
              "ServiceDeploymentCatalogSnapshot"
            )
    );
  },

  getServingSet(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceServingSetSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}/serving`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceServingSetSnapshot(value, "ServiceServingSetSnapshot")
    );
  },

  getRollout(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceRolloutSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}/rollouts`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceRolloutSnapshot(value, "ServiceRolloutSnapshot")
    );
  },

  getTraffic(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceTrafficViewSnapshot | null> {
    return requestJson(
      withQuery(`/api/services/${encodeURIComponent(serviceId)}/traffic`, {
        tenantId: query.tenantId?.trim(),
        appId: query.appId?.trim(),
        namespace: query.namespace?.trim(),
      }),
      (value) =>
        value === null
          ? null
          : decodeServiceTrafficViewSnapshot(
              value,
              "ServiceTrafficViewSnapshot"
            )
    );
  },
};
