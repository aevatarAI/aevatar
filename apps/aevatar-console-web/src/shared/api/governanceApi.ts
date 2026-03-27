import { requestJson, withQuery } from "./http/client";
import {
  expectArray,
  expectRecord,
  normalizeEnumValue,
  readBoolean,
  readString,
  readStringArray,
} from "./http/decoders";
import type {
  ActivationCapabilityView,
  BoundConnectorReference,
  BoundSecretReference,
  BoundServiceReference,
  ServiceBindingCatalogSnapshot,
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServiceEndpointExposureSnapshot,
  ServicePolicyCatalogSnapshot,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import type {
  ServiceIdentity,
  ServiceIdentityQuery,
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

const exposureKindMap = {
  "0": "unspecified",
  "1": "internal",
  "2": "public",
  "3": "disabled",
  service_endpoint_exposure_kind_unspecified: "unspecified",
  service_endpoint_exposure_kind_internal: "internal",
  service_endpoint_exposure_kind_public: "public",
  service_endpoint_exposure_kind_disabled: "disabled",
  unspecified: "unspecified",
  internal: "internal",
  public: "public",
  disabled: "disabled",
};

function decodeServiceIdentity(
  value: unknown,
  label = "ServiceIdentity"
): ServiceIdentity {
  const record = expectRecord(value, label);
  return {
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
  };
}

function decodeBoundServiceReference(
  value: unknown,
  label = "BoundServiceReference"
): BoundServiceReference {
  const record = expectRecord(value, label);
  return {
    identity: decodeServiceIdentity(
      record.identity ?? record.Identity,
      `${label}.identity`
    ),
    endpointId: readString(
      record,
      ["endpointId", "EndpointId"],
      `${label}.endpointId`
    ),
  };
}

function decodeBoundConnectorReference(
  value: unknown,
  label = "BoundConnectorReference"
): BoundConnectorReference {
  const record = expectRecord(value, label);
  return {
    connectorType: readString(
      record,
      ["connectorType", "ConnectorType"],
      `${label}.connectorType`
    ),
    connectorId: readString(
      record,
      ["connectorId", "ConnectorId"],
      `${label}.connectorId`
    ),
  };
}

function decodeBoundSecretReference(
  value: unknown,
  label = "BoundSecretReference"
): BoundSecretReference {
  const record = expectRecord(value, label);
  return {
    secretName: readString(
      record,
      ["secretName", "SecretName"],
      `${label}.secretName`
    ),
  };
}

function decodeServiceBindingSnapshot(
  value: unknown,
  label = "ServiceBindingSnapshot"
): ServiceBindingSnapshot {
  const record = expectRecord(value, label);
  const serviceRef = record.serviceRef ?? record.ServiceRef;
  const connectorRef = record.connectorRef ?? record.ConnectorRef;
  const secretRef = record.secretRef ?? record.SecretRef;

  return {
    bindingId: readString(
      record,
      ["bindingId", "BindingId"],
      `${label}.bindingId`
    ),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
    bindingKind: normalizeEnumValue(
      record.bindingKind ?? record.BindingKind,
      `${label}.bindingKind`,
      bindingKindMap
    ),
    policyIds: readStringArray(
      record,
      ["policyIds", "PolicyIds"],
      `${label}.policyIds`
    ),
    retired: readBoolean(record, ["retired", "Retired"], `${label}.retired`),
    serviceRef:
      serviceRef === null || serviceRef === undefined
        ? null
        : decodeBoundServiceReference(serviceRef, `${label}.serviceRef`),
    connectorRef:
      connectorRef === null || connectorRef === undefined
        ? null
        : decodeBoundConnectorReference(connectorRef, `${label}.connectorRef`),
    secretRef:
      secretRef === null || secretRef === undefined
        ? null
        : decodeBoundSecretReference(secretRef, `${label}.secretRef`),
  };
}

function decodeServiceBindingCatalogSnapshot(
  value: unknown,
  label = "ServiceBindingCatalogSnapshot"
): ServiceBindingCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    bindings: expectArray(
      record.bindings ?? record.Bindings,
      `${label}.bindings`,
      decodeServiceBindingSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServicePolicySnapshot(
  value: unknown,
  label = "ServicePolicySnapshot"
): ServicePolicySnapshot {
  const record = expectRecord(value, label);
  return {
    policyId: readString(record, ["policyId", "PolicyId"], `${label}.policyId`),
    displayName: readString(
      record,
      ["displayName", "DisplayName"],
      `${label}.displayName`
    ),
    activationRequiredBindingIds: readStringArray(
      record,
      ["activationRequiredBindingIds", "ActivationRequiredBindingIds"],
      `${label}.activationRequiredBindingIds`
    ),
    invokeAllowedCallerServiceKeys: readStringArray(
      record,
      ["invokeAllowedCallerServiceKeys", "InvokeAllowedCallerServiceKeys"],
      `${label}.invokeAllowedCallerServiceKeys`
    ),
    invokeRequiresActiveDeployment: readBoolean(
      record,
      ["invokeRequiresActiveDeployment", "InvokeRequiresActiveDeployment"],
      `${label}.invokeRequiresActiveDeployment`
    ),
    retired: readBoolean(record, ["retired", "Retired"], `${label}.retired`),
  };
}

function decodeServicePolicyCatalogSnapshot(
  value: unknown,
  label = "ServicePolicyCatalogSnapshot"
): ServicePolicyCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    policies: expectArray(
      record.policies ?? record.Policies,
      `${label}.policies`,
      decodeServicePolicySnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeServiceEndpointExposureSnapshot(
  value: unknown,
  label = "ServiceEndpointExposureSnapshot"
): ServiceEndpointExposureSnapshot {
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
    exposureKind: normalizeEnumValue(
      record.exposureKind ?? record.ExposureKind,
      `${label}.exposureKind`,
      exposureKindMap
    ),
    policyIds: readStringArray(
      record,
      ["policyIds", "PolicyIds"],
      `${label}.policyIds`
    ),
  };
}

function decodeServiceEndpointCatalogSnapshot(
  value: unknown,
  label = "ServiceEndpointCatalogSnapshot"
): ServiceEndpointCatalogSnapshot {
  const record = expectRecord(value, label);
  return {
    serviceKey: readString(
      record,
      ["serviceKey", "ServiceKey"],
      `${label}.serviceKey`
    ),
    endpoints: expectArray(
      record.endpoints ?? record.Endpoints,
      `${label}.endpoints`,
      decodeServiceEndpointExposureSnapshot
    ),
    updatedAt: readString(
      record,
      ["updatedAt", "UpdatedAt"],
      `${label}.updatedAt`
    ),
  };
}

function decodeActivationCapabilityView(
  value: unknown,
  label = "ActivationCapabilityView"
): ActivationCapabilityView {
  const record = expectRecord(value, label);

  const bindingsSource = record.bindings ?? record.Bindings ?? [];
  const endpointsSource = record.endpoints ?? record.Endpoints ?? [];
  const policiesSource = record.policies ?? record.Policies ?? [];
  const missingPolicyIdsSource =
    record.missingPolicyIds ?? record.MissingPolicyIds ?? [];

  return {
    identity: decodeServiceIdentity(
      record.identity ?? record.Identity,
      `${label}.identity`
    ),
    revisionId: readString(
      record,
      ["revisionId", "RevisionId"],
      `${label}.revisionId`
    ),
    bindings: expectArray(
      bindingsSource,
      `${label}.bindings`,
      (entry, nestedLabel) => {
        const binding = expectRecord(
          entry,
          nestedLabel ?? "ServiceBindingSpec"
        );
        return {
          bindingId: readString(
            binding,
            ["bindingId", "BindingId"],
            `${nestedLabel}.bindingId`
          ),
          displayName: readString(
            binding,
            ["displayName", "DisplayName"],
            `${nestedLabel}.displayName`
          ),
          bindingKind: normalizeEnumValue(
            binding.bindingKind ?? binding.BindingKind,
            `${nestedLabel}.bindingKind`,
            bindingKindMap
          ),
          policyIds: readStringArray(
            binding,
            ["policyIds", "PolicyIds"],
            `${nestedLabel}.policyIds`
          ),
          retired: false,
          serviceRef:
            binding.serviceRef ?? binding.ServiceRef
              ? decodeBoundServiceReference(
                  binding.serviceRef ?? binding.ServiceRef,
                  `${nestedLabel}.serviceRef`
                )
              : null,
          connectorRef:
            binding.connectorRef ?? binding.ConnectorRef
              ? decodeBoundConnectorReference(
                  binding.connectorRef ?? binding.ConnectorRef,
                  `${nestedLabel}.connectorRef`
                )
              : null,
          secretRef:
            binding.secretRef ?? binding.SecretRef
              ? decodeBoundSecretReference(
                  binding.secretRef ?? binding.SecretRef,
                  `${nestedLabel}.secretRef`
                )
              : null,
        };
      }
    ),
    endpoints: expectArray(
      endpointsSource,
      `${label}.endpoints`,
      decodeServiceEndpointExposureSnapshot
    ),
    policies: expectArray(
      policiesSource,
      `${label}.policies`,
      (entry, nestedLabel) => {
        const policy = expectRecord(entry, nestedLabel ?? "ServicePolicySpec");
        return {
          policyId: readString(
            policy,
            ["policyId", "PolicyId"],
            `${nestedLabel}.policyId`
          ),
          displayName: readString(
            policy,
            ["displayName", "DisplayName"],
            `${nestedLabel}.displayName`
          ),
          activationRequiredBindingIds: readStringArray(
            policy,
            ["activationRequiredBindingIds", "ActivationRequiredBindingIds"],
            `${nestedLabel}.activationRequiredBindingIds`
          ),
          invokeAllowedCallerServiceKeys: readStringArray(
            policy,
            [
              "invokeAllowedCallerServiceKeys",
              "InvokeAllowedCallerServiceKeys",
            ],
            `${nestedLabel}.invokeAllowedCallerServiceKeys`
          ),
          invokeRequiresActiveDeployment: readBoolean(
            policy,
            [
              "invokeRequiresActiveDeployment",
              "InvokeRequiresActiveDeployment",
            ],
            `${nestedLabel}.invokeRequiresActiveDeployment`
          ),
          retired: false,
        };
      }
    ),
    missingPolicyIds: Array.isArray(missingPolicyIdsSource)
      ? missingPolicyIdsSource.map((entry, index) =>
          readString({ entry }, "entry", `${label}.missingPolicyIds[${index}]`)
        )
      : [],
  };
}

function buildIdentityQuery(query: ServiceIdentityQuery) {
  return {
    tenantId: query.tenantId?.trim(),
    namespace: query.namespace?.trim(),
  };
}

export const governanceApi = {
  getBindings(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceBindingCatalogSnapshot | null> {
    return requestJson(
      withQuery(
        `/api/services/${encodeURIComponent(serviceId)}/bindings`,
        buildIdentityQuery(query)
      ),
      (value) =>
        value === null ? null : decodeServiceBindingCatalogSnapshot(value)
    );
  },

  getPolicies(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServicePolicyCatalogSnapshot | null> {
    return requestJson(
      withQuery(
        `/api/services/${encodeURIComponent(serviceId)}/policies`,
        buildIdentityQuery(query)
      ),
      (value) =>
        value === null ? null : decodeServicePolicyCatalogSnapshot(value)
    );
  },

  getEndpointCatalog(
    serviceId: string,
    query: ServiceIdentityQuery
  ): Promise<ServiceEndpointCatalogSnapshot | null> {
    return requestJson(
      withQuery(
        `/api/services/${encodeURIComponent(serviceId)}/endpoint-catalog`,
        buildIdentityQuery(query)
      ),
      (value) =>
        value === null ? null : decodeServiceEndpointCatalogSnapshot(value)
    );
  },

  getActivationCapability(
    serviceId: string,
    query: ServiceIdentityQuery & { revisionId?: string }
  ): Promise<ActivationCapabilityView> {
    return requestJson(
      withQuery(
        `/api/services/${encodeURIComponent(serviceId)}:activation-capability`,
        {
          ...buildIdentityQuery(query),
          revisionId: query.revisionId?.trim(),
        }
      ),
      decodeActivationCapabilityView
    );
  },
};
