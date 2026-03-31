import type { ServiceIdentity } from "./services";

export interface GovernanceIdentityInput {
  tenantId: string;
  appId: string;
  namespace: string;
}

export interface BoundServiceReference {
  identity: ServiceIdentity;
  endpointId: string;
}

export interface BoundServiceInput extends GovernanceIdentityInput {
  serviceId: string;
  endpointId?: string;
}

export interface BoundConnectorReference {
  connectorType: string;
  connectorId: string;
}

export interface BoundConnectorInput {
  connectorType: string;
  connectorId: string;
}

export interface BoundSecretReference {
  secretName: string;
}

export interface BoundSecretInput {
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

export interface ServiceBindingInput extends GovernanceIdentityInput {
  bindingId: string;
  displayName: string;
  bindingKind: string;
  policyIds?: string[];
  service?: BoundServiceInput | null;
  connector?: BoundConnectorInput | null;
  secret?: BoundSecretInput | null;
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

export interface ServicePolicyInput extends GovernanceIdentityInput {
  policyId: string;
  displayName: string;
  activationRequiredBindingIds: string[];
  invokeAllowedCallerServiceKeys: string[];
  invokeRequiresActiveDeployment: boolean;
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

export interface ServiceEndpointExposureInput {
  endpointId: string;
  displayName: string;
  kind: string;
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
  exposureKind: string;
  policyIds?: string[];
}

export interface ServiceEndpointCatalogInput extends GovernanceIdentityInput {
  endpoints: ServiceEndpointExposureInput[];
}

export interface ActivationCapabilityView {
  identity: ServiceIdentity;
  revisionId: string;
  bindings: ServiceBindingSnapshot[];
  endpoints: ServiceEndpointExposureSnapshot[];
  policies: ServicePolicySnapshot[];
  missingPolicyIds: string[];
}
