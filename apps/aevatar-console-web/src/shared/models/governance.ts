import type { ServiceIdentity } from "./services";

export interface BoundServiceReference {
  identity: ServiceIdentity;
  endpointId: string;
}

export interface BoundConnectorReference {
  connectorType: string;
  connectorId: string;
}

export interface BoundSecretReference {
  secretName: string;
}

export interface ServiceBindingSnapshot {
  bindingId: string;
  displayName: string;
  bindingKind: string;
  policyIds: string[];
  retired: boolean;
  serviceRef: BoundServiceReference | null;
  connectorRef: BoundConnectorReference | null;
  secretRef: BoundSecretReference | null;
}

export interface ServiceBindingCatalogSnapshot {
  serviceKey: string;
  bindings: ServiceBindingSnapshot[];
  updatedAt: string;
}

export interface ServicePolicySnapshot {
  policyId: string;
  displayName: string;
  activationRequiredBindingIds: string[];
  invokeAllowedCallerServiceKeys: string[];
  invokeRequiresActiveDeployment: boolean;
  retired: boolean;
}

export interface ServicePolicyCatalogSnapshot {
  serviceKey: string;
  policies: ServicePolicySnapshot[];
  updatedAt: string;
}

export interface ServiceEndpointExposureSnapshot {
  endpointId: string;
  displayName: string;
  kind: string;
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
  exposureKind: string;
  policyIds: string[];
}

export interface ServiceEndpointCatalogSnapshot {
  serviceKey: string;
  endpoints: ServiceEndpointExposureSnapshot[];
  updatedAt: string;
}

export interface ActivationCapabilityView {
  identity: ServiceIdentity;
  revisionId: string;
  bindings: ServiceBindingSnapshot[];
  endpoints: ServiceEndpointExposureSnapshot[];
  policies: ServicePolicySnapshot[];
  missingPolicyIds: string[];
}
