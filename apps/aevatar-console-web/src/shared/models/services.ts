export interface ServiceIdentityQuery {
  tenantId?: string;
  appId?: string;
  namespace?: string;
  take?: number;
}

export interface ServiceIdentity {
  tenantId: string;
  appId: string;
  namespace: string;
  serviceId: string;
}

export interface ServiceCommandAcceptedReceipt {
  targetActorId: string;
  commandId: string;
  correlationId: string;
}

export interface ServiceEndpointSnapshot {
  endpointId: string;
  displayName: string;
  kind: string;
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
}

export interface ServiceCatalogSnapshot {
  serviceKey: string;
  tenantId: string;
  appId: string;
  namespace: string;
  serviceId: string;
  displayName: string;
  defaultServingRevisionId: string;
  activeServingRevisionId: string;
  deploymentId: string;
  primaryActorId: string;
  deploymentStatus: string;
  endpoints: ServiceEndpointSnapshot[];
  policyIds: string[];
  updatedAt: string;
}

export interface ServiceRevisionSnapshot {
  revisionId: string;
  implementationKind: string;
  status: string;
  artifactHash: string;
  failureReason: string;
  endpoints: ServiceEndpointSnapshot[];
  createdAt: string | null;
  preparedAt: string | null;
  publishedAt: string | null;
  retiredAt: string | null;
}

export interface ServiceRevisionCatalogSnapshot {
  serviceKey: string;
  revisions: ServiceRevisionSnapshot[];
  updatedAt: string;
}

export interface ServiceDeploymentSnapshot {
  deploymentId: string;
  revisionId: string;
  primaryActorId: string;
  status: string;
  activatedAt: string | null;
  updatedAt: string;
}

export interface ServiceDeploymentCatalogSnapshot {
  serviceKey: string;
  deployments: ServiceDeploymentSnapshot[];
  updatedAt: string;
}

export interface ServiceServingTargetSnapshot {
  deploymentId: string;
  revisionId: string;
  primaryActorId: string;
  allocationWeight: number;
  servingState: string;
  enabledEndpointIds: string[];
}

export interface ServiceServingTargetInput {
  revisionId: string;
  allocationWeight: number;
  servingState?: string;
  enabledEndpointIds?: string[];
}

export interface ServiceServingSetSnapshot {
  serviceKey: string;
  generation: number;
  activeRolloutId: string;
  targets: ServiceServingTargetSnapshot[];
  updatedAt: string;
}

export interface ServiceRolloutStageSnapshot {
  stageId: string;
  stageIndex: number;
  targets: ServiceServingTargetSnapshot[];
}

export interface ServiceRolloutSnapshot {
  serviceKey: string;
  rolloutId: string;
  displayName: string;
  status: string;
  currentStageIndex: number;
  stages: ServiceRolloutStageSnapshot[];
  baselineTargets: ServiceServingTargetSnapshot[];
  failureReason: string;
  startedAt: string | null;
  updatedAt: string;
}

export interface ServiceTrafficTargetSnapshot {
  deploymentId: string;
  revisionId: string;
  primaryActorId: string;
  allocationWeight: number;
  servingState: string;
}

export interface ServiceTrafficEndpointSnapshot {
  endpointId: string;
  targets: ServiceTrafficTargetSnapshot[];
}

export interface ServiceTrafficViewSnapshot {
  serviceKey: string;
  generation: number;
  activeRolloutId: string;
  endpoints: ServiceTrafficEndpointSnapshot[];
  updatedAt: string;
}
