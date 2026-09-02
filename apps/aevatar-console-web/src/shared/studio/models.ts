export interface StudioValidationFinding {
  readonly level?: string | number;
  readonly path?: string | null;
  readonly message: string;
  readonly code?: string | null;
}

export type StudioWorkflowRoleDocument = Record<string, unknown> & {
  id?: string;
  name?: string;
  systemPrompt?: string;
  provider?: string | null;
  model?: string | null;
  toolSets?: string[] | null;
  connectors?: unknown[];
};

export type StudioWorkflowStepDocument = Record<string, unknown> & {
  id?: string;
  type?: string;
  originalType?: string;
  capability?: StudioWorkflowCapability | null;
  targetRole?: string | null;
  target_role?: string | null;
  toolSets?: string[] | null;
  parameters?: Record<string, unknown> | null;
  next?: string | null;
  branches?: Record<string, string> | null;
};

export type StudioWorkflowCapability = {
  readonly nyxid_operation: {
    readonly user_service_id: string;
    readonly endpoint_id: string;
  };
};

export function normalizeStudioWorkflowCapability(
  value: unknown,
): StudioWorkflowCapability | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return null;
  }

  const operation = (value as Record<string, unknown>).nyxid_operation;
  if (!operation || typeof operation !== 'object' || Array.isArray(operation)) {
    return null;
  }

  const userServiceIdValue = (operation as Record<string, unknown>)
    .user_service_id;
  const endpointIdValue = (operation as Record<string, unknown>).endpoint_id;
  if (
    typeof userServiceIdValue !== 'string' ||
    typeof endpointIdValue !== 'string'
  ) {
    return null;
  }

  const userServiceId = userServiceIdValue.trim();
  const endpointId = endpointIdValue.trim();
  if (!userServiceId || !endpointId) {
    return null;
  }

  return {
    nyxid_operation: {
      user_service_id: userServiceId,
      endpoint_id: endpointId,
    },
  };
}

export type StudioWorkflowDocument = Record<string, unknown> & {
  name?: string;
  description?: string;
  roles?: StudioWorkflowRoleDocument[];
  steps?: StudioWorkflowStepDocument[];
};

export interface StudioAppContext {
  readonly mode: 'embedded' | 'proxy';
  readonly scopeId: string | null;
  readonly scopeResolved: boolean;
  readonly scopeSource: string;
  readonly workflowStorageMode: 'workspace' | 'scope';
  readonly scriptStorageMode: 'draft' | 'scope';
  readonly features: {
    readonly publishedWorkflows: boolean;
    readonly scripts: boolean;
  };
  readonly scriptContract: {
    readonly inputType: string;
    readonly readModelFields: readonly string[];
  };
}

export interface StudioAuthSession {
  readonly enabled: boolean;
  readonly authenticated: boolean;
  readonly subject?: string | null;
  readonly providerDisplayName?: string;
  readonly loginUrl?: string;
  readonly logoutUrl?: string;
  readonly invokeAuthMode?: 'studio-session' | 'bearer-token' | 'anonymous';
  readonly externalCallerHint?: string;
  readonly name?: string;
  readonly email?: string;
  readonly picture?: string;
  readonly errorMessage?: string;
  readonly scopeId?: string | null;
  readonly scopeSource?: string | null;
  readonly profile?: StudioAuthProfile | null;
  readonly session?: StudioAuthSessionDetails | null;
}

export interface StudioAuthProfile {
  readonly subject?: string | null;
  readonly name?: string | null;
  readonly email?: string | null;
  readonly emailVerified?: boolean | null;
  readonly picture?: string | null;
  readonly roles: readonly string[];
  readonly groups: readonly string[];
}

export interface StudioAuthSessionDetails {
  readonly authenticated: boolean;
  readonly providerDisplayName?: string | null;
  readonly scopeId?: string | null;
  readonly scopeSource?: string | null;
  readonly expiresAtUtc?: string | null;
}

export interface StudioWorkflowDirectory {
  readonly directoryId: string;
  readonly label: string;
  readonly path: string;
  readonly isBuiltIn: boolean;
}

export interface StudioWorkspaceSettings {
  readonly runtimeBaseUrl: string;
  readonly directories: StudioWorkflowDirectory[];
}

export interface StudioWorkflowDraftSummary {
  readonly activeRevisionId?: string | null;
  readonly serviceKey?: string | null;
  readonly workflowId: string;
  readonly name: string;
  readonly description: string;
  readonly fileName: string;
  readonly filePath: string;
  readonly directoryId: string;
  readonly directoryLabel: string;
  readonly stepCount: number;
  readonly hasLayout: boolean;
  readonly updatedAtUtc: string;
}

export interface StudioWorkflowCommittedSummary {
  readonly workflowId: string;
  readonly name: string;
  readonly description: string;
  readonly stepCount: number;
  readonly updatedAtUtc?: string | null;
}

export interface StudioWorkflowDraft {
  readonly workflowId: string;
  readonly name: string;
  readonly fileName: string;
  readonly filePath: string;
  readonly directoryId: string;
  readonly directoryLabel: string;
  readonly yaml: string;
  readonly layout?: unknown;
  readonly updatedAtUtc: string;
}

export interface StudioCommittedWorkflow {
  readonly workflowId: string;
  readonly name: string;
  readonly yaml: string;
  readonly document?: StudioWorkflowDocument | null;
  readonly findings: StudioValidationFinding[];
  readonly updatedAtUtc?: string | null;
}

export interface StudioSaveWorkflowInput {
  readonly workflowId?: string | null;
  readonly draftExists?: boolean | null;
  readonly scopeId?: string | null;
  readonly directoryId: string;
  readonly workflowName: string;
  readonly fileName?: string | null;
  readonly yaml: string;
  readonly layout?: unknown;
}

export interface StudioSaveAndBindWorkflowInput {
  readonly scopeId: string;
  readonly workflowId?: string | null;
  readonly revisionId: string;
  readonly workflowYaml: string;
  readonly workflowName?: string | null;
  readonly displayName?: string | null;
  readonly inlineWorkflowYamls?: Record<string, string> | null;
  readonly appId?: string | null;
  readonly serviceId?: string | null;
  readonly exposureDesired?: boolean | null;
  readonly explicitRequestConfirmations?:
    | readonly StudioExplicitRequestConfirmation[]
    | null;
}

export interface StudioPublishWorkflowInput {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly revisionId: string;
  readonly workflowYaml: string;
  readonly workflowName?: string | null;
  readonly displayName?: string | null;
  readonly inlineWorkflowYamls?: Record<string, string> | null;
  readonly explicitRequestConfirmations?:
    | readonly StudioExplicitRequestConfirmation[]
    | null;
}

export type StudioExplicitRequestMethod =
  | 'get'
  | 'head'
  | 'options'
  | 'post'
  | 'put'
  | 'patch'
  | 'delete';

export type StudioExplicitRequestBodyMode = 'none' | 'json';

export type StudioExplicitRequestResponseMode = 'text' | 'file_artifact';

export type StudioExplicitRequestRisk = 'read_only' | 'write' | 'destructive';

export type StudioExplicitRequestExecutionMode = 'interactive' | 'durable';

export type StudioWorkflowCapabilitySelector =
  | {
      readonly kind: 'nyxid_operation';
      readonly userServiceId: string;
      readonly endpointId: string;
    }
  | {
      readonly kind: 'host_connector';
      readonly connectorCapabilityRef: string;
      readonly operationId: string;
      readonly contractDigest: string;
    }
  | {
      readonly kind: 'nyxid_request';
      readonly userServiceId: string;
      readonly method: StudioExplicitRequestMethod;
      readonly pathTemplate: string;
      readonly queryParameters: readonly string[];
      readonly headerParameters: readonly string[];
      readonly bodyMode: StudioExplicitRequestBodyMode;
      readonly responseMode: StudioExplicitRequestResponseMode;
      readonly bodyRequired: boolean;
    };

export type StudioWorkflowCapabilitySourceKind =
  | 'connector_catalog'
  | 'nyxid_user_services'
  | 'nyxid_open_api'
  | 'durable_authorization_catalog'
  | 'nyxid_mcp_config';

export interface StudioWorkflowCapabilitySource {
  readonly kind: StudioWorkflowCapabilitySourceKind;
  readonly sourceId: string;
  readonly sourceVersion: number;
  readonly observedAt: string | null;
  readonly freshUntil: string | null;
}

export interface StudioWorkflowCapabilityDescriptor {
  readonly displayName: string;
  readonly readOnly: boolean;
  readonly destructive: boolean;
  readonly selector: StudioWorkflowCapabilitySelector;
  readonly source: StudioWorkflowCapabilitySource | null;
}

export type StudioWorkflowCapabilityDiagnosticCode =
  | 'source_unavailable'
  | 'no_exact_user_service'
  | 'generic_proxy_rejected'
  | 'invalid_service_identity'
  | 'ambiguous_service_identity'
  | 'invalid_endpoint_identity'
  | 'ambiguous_endpoint_identity'
  | 'unsupported_parameter'
  | 'unsupported_request_body'
  | 'unsupported_schema'
  | 'unsupported_response';

export interface StudioWorkflowCapabilityDiagnostic {
  readonly code: StudioWorkflowCapabilityDiagnosticCode;
  readonly safeMessage: string;
  readonly count: number;
  readonly source: StudioWorkflowCapabilitySource | null;
}

export interface StudioWorkflowCapabilityList {
  readonly capabilities: readonly StudioWorkflowCapabilityDescriptor[];
  readonly candidateCount: number;
  readonly rejectedCount: number;
  readonly diagnostics: readonly StudioWorkflowCapabilityDiagnostic[];
}

export type StudioWorkflowCapabilityReadinessStatus =
  | 'selection_required'
  | 'connector_not_found'
  | 'service_registration_required'
  | 'credential_connection_required'
  | 'service_access_denied'
  | 'node_binding_required'
  | 'node_unavailable'
  | 'endpoint_contract_required'
  | 'operation_selection_required'
  | 'source_stale'
  | 'durable_authorization_unavailable'
  | 'contract_drift'
  | 'ready'
  | 'admission_rebind_required';

export type StudioWorkflowCapabilityRemediationAction =
  | 'select_capability'
  | 'configure_connector'
  | 'register_service'
  | 'connect_credential'
  | 'request_access'
  | 'bind_node'
  | 'restore_node'
  | 'publish_endpoint_contract'
  | 'select_operation'
  | 'refresh_source'
  | 'use_interactive_execution'
  | 'rebind_workflow';

export type StudioWorkflowCapabilityParameterLocation =
  | 'path'
  | 'query'
  | 'header';

export type StudioWorkflowCapabilityValueKind =
  | 'string'
  | 'integer'
  | 'number'
  | 'boolean'
  | 'object'
  | 'array';

export interface StudioWorkflowCapabilitySchema {
  readonly valueKind: StudioWorkflowCapabilityValueKind;
  readonly properties: readonly StudioWorkflowCapabilitySchemaProperty[];
  readonly requiredProperties: readonly string[];
  readonly items: StudioWorkflowCapabilitySchema | null;
  readonly allowedValues: readonly string[];
  readonly additionalPropertiesAllowed: boolean;
}

export interface StudioWorkflowCapabilitySchemaProperty {
  readonly name: string;
  readonly schema: StudioWorkflowCapabilitySchema;
}

export interface StudioWorkflowCapabilityParameter {
  readonly name: string;
  readonly location: StudioWorkflowCapabilityParameterLocation;
  readonly required: boolean;
  readonly schema: StudioWorkflowCapabilitySchema;
}

export interface StudioWorkflowCapabilityOperation {
  readonly userServiceId: string;
  readonly endpointId: string;
  readonly serviceSlug: string;
  readonly httpMethod: string;
  readonly pathTemplate: string;
  readonly parameters: readonly StudioWorkflowCapabilityParameter[];
  readonly requestBody: {
    readonly required: boolean;
    readonly mediaType: string;
    readonly schema: StudioWorkflowCapabilitySchema;
  } | null;
  readonly responsePolicy: {
    readonly textAllowed: boolean;
    readonly fileArtifactAllowed: boolean;
    readonly mediaTypes: readonly string[];
  } | null;
  readonly executionPolicy: {
    readonly risk: StudioExplicitRequestRisk;
    readonly approval: 'none' | 'required';
    readonly enforcementOwner: 'aevatar' | 'nyxid';
    readonly allowedExecutionModes: readonly StudioExplicitRequestExecutionMode[];
  } | null;
}

export interface StudioWorkflowCapabilityBlocker {
  readonly status: StudioWorkflowCapabilityReadinessStatus;
  readonly code: string;
  readonly safeMessage: string;
}

export interface StudioWorkflowCapabilityRemediation {
  readonly actionKind: StudioWorkflowCapabilityRemediationAction;
  readonly label: string;
  readonly trustedLocator: string;
}

export interface StudioWorkflowCapabilityReadiness {
  readonly executionMode: StudioExplicitRequestExecutionMode;
  readonly status: StudioWorkflowCapabilityReadinessStatus;
  readonly selectedSelector: StudioWorkflowCapabilitySelector | null;
  readonly selectedOperation: StudioWorkflowCapabilityOperation | null;
  readonly blockers: readonly StudioWorkflowCapabilityBlocker[];
  readonly remediations: readonly StudioWorkflowCapabilityRemediation[];
  readonly sources: readonly StudioWorkflowCapabilitySource[];
}

export interface StudioWorkflowCapabilityReadinessInput {
  readonly scopeId: string;
  readonly selector: Extract<
    StudioWorkflowCapabilitySelector,
    { readonly kind: 'nyxid_operation' }
  >;
  readonly executionMode: 'interactive';
}

export interface StudioExplicitRequestPreviewInput {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly workflowYaml: string;
  readonly executionMode: 'interactive';
  readonly inlineWorkflowYamls?: Record<string, string> | null;
  readonly revisionId: string;
}

export interface StudioExplicitRequestPreview {
  readonly workflowId: string;
  readonly revisionId: string;
  readonly items: readonly StudioExplicitRequestPreviewItem[];
}

export interface StudioExplicitRequestPreviewItem {
  readonly callSiteId: string;
  readonly requestContractDigest: string;
  readonly userServiceId: string;
  readonly method: StudioExplicitRequestMethod;
  readonly pathTemplate: string;
  readonly bodyMode: StudioExplicitRequestBodyMode;
  readonly bodyRequired: boolean;
  readonly responseMode: StudioExplicitRequestResponseMode;
  readonly effectiveRisk: StudioExplicitRequestRisk;
  readonly approvalRequired: boolean;
  readonly allowedExecutionModes: readonly StudioExplicitRequestExecutionMode[];
}

export interface StudioExplicitRequestConfirmation {
  readonly workflowId: string;
  readonly revisionId: string;
  readonly callSiteId: string;
  readonly requestContractDigest: string;
  readonly attestedRisk: StudioExplicitRequestRisk;
}

export interface StudioWorkflowDraftCreateReadiness {
  readonly readable: boolean;
  readonly stage: string;
  readonly message: string;
}

export interface StudioWorkflowDraftCreateAcceptedReceipt {
  readonly accepted: true;
  readonly workflowId: string;
  readonly commandId: string;
  readonly ackStage: string;
  readonly actorId: string;
  readonly workspaceId: string;
  readonly expectedVersion?: number | null;
  readonly ackedAtUtc: string;
  readonly readiness: StudioWorkflowDraftCreateReadiness;
}

export interface StudioParseYamlResult {
  readonly document?: StudioWorkflowDocument | null;
  readonly graph?: unknown;
  readonly findings: StudioValidationFinding[];
}

export type StudioWorkflowSummary = StudioWorkflowDraftSummary;

export type StudioWorkflowFile = StudioWorkflowDraft & {
  readonly document?: StudioWorkflowDocument | null;
  readonly draftExists?: boolean;
  readonly findings: StudioValidationFinding[];
};

export type StudioWorkflowSaveResult =
  | {
      readonly kind: 'materialized';
      readonly workflow: StudioWorkflowFile;
    }
  | {
      readonly kind: 'accepted';
      readonly receipt: StudioWorkflowDraftCreateAcceptedReceipt;
    };

export interface StudioSaveAndBindWorkflowAcceptedResult {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly revisionId: string;
  readonly workflow?: {
    readonly scopeId: string;
    readonly workflowId: string;
    readonly serviceKey?: string;
    readonly revisionId: string;
    readonly readModelUrl?: string;
    readonly acceptanceStage?: string;
    readonly propagationStage?: string;
    readonly displayName?: string;
    readonly workflowName?: string;
  };
  readonly binding?: StudioScopeBindingResult;
  readonly acceptanceStage: string;
  readonly propagationStage: string;
}

export interface StudioPublishWorkflowAcceptedResult {
  readonly scopeId: string;
  readonly workflowId: string;
  readonly serviceKey: string;
  readonly revisionId: string;
  readonly acceptanceStage: string;
  readonly propagationStage: string;
}

export interface StudioSerializeYamlResult {
  readonly yaml: string;
  readonly document: StudioWorkflowDocument;
  readonly findings: StudioValidationFinding[];
}

export interface StudioExecutionSummary {
  readonly executionId: string;
  readonly workflowName: string;
  readonly prompt: string;
  readonly status: string;
  readonly startedAtUtc: string;
  readonly completedAtUtc: string | null;
  readonly actorId: string | null;
  readonly error: string | null;
  readonly serviceId?: string | null;
  readonly revisionId?: string | null;
  readonly definitionActorId?: string | null;
  readonly stateVersion?: number | null;
  readonly lastEventId?: string | null;
  readonly updatedAtUtc?: string | null;
  readonly totalSteps?: number | null;
  readonly completedSteps?: number | null;
  readonly roleReplyCount?: number | null;
  readonly output?: string | null;
  readonly auditUpdatedAtUtc?: string | null;
  readonly auditSource?:
    | 'service-run-summary'
    | 'run-audit'
    | 'invoke-session'
    | 'draft-run-session';
}

export interface StudioExecutionFrame {
  readonly receivedAtUtc: string;
  readonly payload: string;
}

export interface StudioExecutionDetail extends StudioExecutionSummary {
  readonly frames: StudioExecutionFrame[];
}

export interface StudioStartExecutionInput {
  readonly workflowName: string;
  readonly prompt: string;
  readonly workflowYamls: string[];
  readonly runtimeBaseUrl?: string | null;
  readonly scopeId?: string | null;
  readonly workflowId?: string | null;
  readonly eventFormat?: string | null;
}

export interface StudioMemberWorkflowBindingInput {
  readonly scopeId: string;
  readonly memberId: string;
  readonly displayName?: string | null;
  readonly workflowId: string;
  readonly workflowYamls: readonly string[];
  readonly revisionId: string;
  readonly explicitRequestConfirmations?:
    | readonly StudioExplicitRequestConfirmation[]
    | null;
}

export type StudioScopeBindingImplementationKind =
  | 'workflow'
  | 'script'
  | 'gagent'
  | 'unknown';

export type StudioScopeBindingTargetKind = StudioScopeBindingImplementationKind;

export function normalizeStudioScopeBindingImplementationKind(
  value: string | number | null | undefined,
): StudioScopeBindingImplementationKind {
  if (typeof value === 'number') {
    switch (value) {
      case 1:
        return 'workflow';
      case 2:
        return 'script';
      case 3:
        return 'gagent';
      default:
        return 'unknown';
    }
  }

  const normalized = String(value || '')
    .trim()
    .toLowerCase();
  switch (normalized) {
    case 'workflow':
      return 'workflow';
    case 'script':
    case 'scripting':
      return 'script';
    case 'gagent':
      return 'gagent';
    default:
      return 'unknown';
  }
}

export function formatStudioScopeBindingImplementationKind(
  value: StudioScopeBindingImplementationKind | string | null | undefined,
): string {
  switch (normalizeStudioScopeBindingImplementationKind(value)) {
    case 'workflow':
      return 'Workflow';
    case 'script':
      return 'Script';
    case 'gagent':
      return 'GAgent';
    default:
      return 'Unknown';
  }
}

export interface StudioScopeBindingResult {
  readonly scopeId: string;
  readonly serviceId?: string;
  readonly displayName: string;
  readonly revisionId: string;
  readonly implementationKind?: StudioScopeBindingImplementationKind;
  readonly targetKind: StudioScopeBindingTargetKind;
  readonly targetName: string;
  readonly workflowName?: string;
  readonly definitionActorIdPrefix?: string;
  readonly expectedActorId?: string;
  readonly workflow?: {
    readonly workflowName: string;
    readonly definitionActorIdPrefix: string;
  } | null;
  readonly script?: {
    readonly scriptId: string;
    readonly scriptRevision: string;
    readonly definitionActorId: string;
  } | null;
  readonly gAgent?: {
    readonly diagnosticClrTypeName: string;
  } | null;
}

export interface StudioScopeBindingRevision {
  readonly revisionId: string;
  readonly implementationKind: StudioScopeBindingImplementationKind;
  readonly status: string;
  readonly artifactHash: string;
  readonly failureReason: string;
  readonly isDefaultServing: boolean;
  readonly isActiveServing: boolean;
  readonly isServingTarget: boolean;
  readonly allocationWeight: number;
  readonly servingState: string;
  readonly deploymentId: string;
  readonly primaryActorId: string;
  readonly createdAt: string | null;
  readonly preparedAt: string | null;
  readonly publishedAt: string | null;
  readonly retiredAt: string | null;
  readonly workflowName: string;
  readonly workflowDefinitionActorId: string;
  readonly inlineWorkflowCount: number;
  readonly scriptId: string;
  readonly scriptRevision: string;
  readonly scriptDefinitionActorId: string;
  readonly scriptSourceHash: string;
  readonly staticActorTypeName: string;
  readonly staticAgentKind?: string;
}

export interface StudioScopeBindingStatus {
  readonly available: boolean;
  readonly scopeId: string;
  readonly serviceId: string;
  readonly displayName: string;
  readonly serviceKey: string;
  readonly defaultServingRevisionId: string;
  readonly activeServingRevisionId: string;
  readonly deploymentId: string;
  readonly deploymentStatus: string;
  readonly primaryActorId: string;
  readonly updatedAt: string | null;
  readonly revisions: readonly StudioScopeBindingRevision[];
}

export interface StudioScopeBindingActivationResult {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly displayName: string;
  readonly revisionId: string;
}

export interface StudioScopeBindingRetirementResult {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly revisionId: string;
  readonly status: string;
}

export function describeStudioScopeBindingRevisionTarget(
  revision: StudioScopeBindingRevision | null | undefined,
): string {
  if (!revision) {
    return 'Not configured';
  }

  switch (
    normalizeStudioScopeBindingImplementationKind(revision.implementationKind)
  ) {
    case 'workflow':
      return revision.workflowName || 'Workflow';
    case 'script':
      return revision.scriptId || 'Script';
    case 'gagent':
      return (
        revision.staticAgentKind || revision.staticActorTypeName || 'GAgent'
      );
    default:
      return 'Unknown';
  }
}

export function describeStudioScopeBindingRevisionContext(
  revision: StudioScopeBindingRevision | null | undefined,
): string {
  if (!revision) {
    return '';
  }

  switch (
    normalizeStudioScopeBindingImplementationKind(revision.implementationKind)
  ) {
    case 'workflow':
      if (revision.workflowDefinitionActorId) {
        return revision.workflowDefinitionActorId;
      }
      if (revision.inlineWorkflowCount > 0) {
        return `${revision.inlineWorkflowCount} inline workflow${revision.inlineWorkflowCount === 1 ? '' : 's'}`;
      }
      return '';
    case 'script':
      if (revision.scriptRevision && revision.scriptSourceHash) {
        return `${revision.scriptRevision} · ${revision.scriptSourceHash}`;
      }
      return revision.scriptRevision || revision.scriptSourceHash || '';
    case 'gagent':
      return '';
    default:
      return '';
  }
}

export function getStudioScopeBindingCurrentRevision(
  status: StudioScopeBindingStatus | null | undefined,
): StudioScopeBindingRevision | null {
  if (!status?.revisions?.length) {
    return null;
  }

  return (
    status.revisions.find((revision) => revision.isActiveServing) ||
    status.revisions.find((revision) => revision.isDefaultServing) ||
    status.revisions[0] ||
    null
  );
}

export type StudioMemberBindingImplementationKind =
  StudioScopeBindingImplementationKind;
export type StudioMemberImplementationKind =
  StudioScopeBindingImplementationKind;
export type StudioMemberLifecycleStage =
  | 'created'
  | 'build_ready'
  | 'bind_ready'
  | 'unknown';

export function normalizeStudioMemberLifecycleStage(
  value: string | null | undefined,
): StudioMemberLifecycleStage {
  switch (
    String(value || '')
      .trim()
      .toLowerCase()
  ) {
    case 'created':
      return 'created';
    case 'build_ready':
    case 'buildready':
      return 'build_ready';
    case 'bind_ready':
    case 'bindready':
      return 'bind_ready';
    default:
      return 'unknown';
  }
}

export function formatStudioMemberLifecycleStage(
  value: StudioMemberLifecycleStage | string | null | undefined,
): string {
  switch (normalizeStudioMemberLifecycleStage(value)) {
    case 'created':
      return 'Created';
    case 'build_ready':
      return 'Build ready';
    case 'bind_ready':
      return 'Bind ready';
    default:
      return 'Unknown';
  }
}

export interface StudioMemberSummary {
  readonly memberId: string;
  readonly scopeId: string;
  readonly displayName: string;
  readonly description: string;
  readonly implementationKind: StudioMemberImplementationKind;
  readonly implementationRef?: StudioMemberImplementationRef | null;
  readonly lifecycleStage: StudioMemberLifecycleStage;
  readonly publishedServiceId: string;
  readonly lastBoundRevisionId: string | null;
  readonly teamId?: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface StudioMemberImplementationRef {
  readonly implementationKind: StudioMemberImplementationKind;
  readonly workflowId?: string | null;
  readonly workflowRevision?: string | null;
  readonly scriptId?: string | null;
  readonly scriptRevision?: string | null;
  readonly agentKind?: string | null;
  readonly diagnosticActorTypeName?: string | null;
}

export interface StudioMemberBindingContract {
  readonly publishedServiceId: string;
  readonly revisionId: string;
  readonly implementationKind: StudioMemberImplementationKind;
  readonly boundAt: string;
}

export type StudioMemberBindingRunStatus =
  | 'accepted'
  | 'admission_pending'
  | 'admitted'
  | 'platform_binding_pending'
  | 'member_notification_pending'
  | 'succeeded'
  | 'failed'
  | 'rejected'
  | 'unknown';

export type StudioMemberBindingAckStage = 'dispatch_accepted' | 'unknown';

export type StudioMemberBindingRunRole = 'candidate' | 'unknown';

export interface StudioMemberBindingFailure {
  readonly code: string;
  readonly message?: string | null;
  readonly failedAt?: string | null;
}

export interface StudioMemberBindingRunStatusResponse {
  readonly status: StudioMemberBindingRunStatus;
  readonly bindingRunId: string;
  readonly scopeId: string;
  readonly memberId: string;
  readonly stateVersion?: number | null;
  readonly platformBindingCommandId?: string | null;
  readonly result?: StudioMemberBindingRunResult | null;
  readonly failure?: StudioMemberBindingFailure | null;
  readonly updatedAt?: string | null;
}

export interface StudioMemberBindingRunResult {
  readonly publishedServiceId: string;
  readonly revisionId: string;
  readonly implementationKind: StudioMemberImplementationKind;
  readonly expectedActorId?: string | null;
}

export interface StudioMemberBindingAcceptedResponse {
  readonly status: StudioMemberBindingRunStatus;
  readonly bindingRunId: string;
  readonly scopeId: string;
  readonly memberId: string;
  readonly ackStage?: StudioMemberBindingAckStage | null;
  readonly bindingRunRole?: StudioMemberBindingRunRole | null;
}

export type StudioMemberCommandStatus =
  | 'accepted'
  | 'delete_accepted'
  | 'no_change'
  | 'unknown';

export interface StudioMemberCommandResponse {
  readonly status: StudioMemberCommandStatus;
  readonly scopeId: string;
  readonly memberId: string;
  readonly ackedAt?: string | null;
}

export interface StudioMemberDetail {
  readonly summary: StudioMemberSummary;
  readonly implementationRef?: StudioMemberImplementationRef | null;
  readonly lastBinding?: StudioMemberBindingContract | null;
  readonly currentBindingRun?: StudioMemberBindingRunStatusResponse | null;
}

export interface StudioMemberBindingViewResponse {
  readonly lastBinding?: StudioMemberBindingContract | null;
  readonly currentBindingRun?: StudioMemberBindingRunStatusResponse | null;
}

export interface StudioMemberRoster {
  readonly scopeId: string;
  readonly members: readonly StudioMemberSummary[];
  readonly nextPageToken?: string | null;
}

export type StudioTeamLifecycleStage = 'active' | 'archived' | 'unknown';

export function normalizeStudioTeamLifecycleStage(
  value: string | null | undefined,
): StudioTeamLifecycleStage {
  switch (
    String(value || '')
      .trim()
      .toLowerCase()
  ) {
    case 'active':
      return 'active';
    case 'archived':
      return 'archived';
    default:
      return 'unknown';
  }
}

export function formatStudioTeamLifecycleStage(
  value: StudioTeamLifecycleStage | string | null | undefined,
): string {
  switch (normalizeStudioTeamLifecycleStage(value)) {
    case 'active':
      return 'Active';
    case 'archived':
      return 'Archived';
    default:
      return 'Unknown';
  }
}

export interface StudioTeamSummary {
  readonly teamId: string;
  readonly scopeId: string;
  readonly displayName: string;
  readonly description: string;
  readonly entryMemberId?: string | null;
  readonly lifecycleStage: StudioTeamLifecycleStage;
  readonly memberCount: number;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface StudioTeamRoster {
  readonly scopeId: string;
  readonly teams: readonly StudioTeamSummary[];
  readonly nextPageToken?: string | null;
}

export type StudioWorkflowBoardExecutionAvailability =
  | 'available'
  | 'unavailable'
  | 'pending_backend_contract'
  | 'unknown';

export type StudioWorkflowBoardExecutionStatus =
  | 'running'
  | 'waiting'
  | 'failed'
  | 'timed_out'
  | 'retrying'
  | 'completed'
  | 'stopped'
  | 'unknown';

export type StudioWorkflowBoardCurrentNodeStatus =
  | 'running'
  | 'waiting'
  | 'pending'
  | 'failed'
  | 'completed'
  | 'unknown';

export type StudioWorkflowBoardPendingNodeStatus =
  | 'waiting'
  | 'pending'
  | 'queued'
  | 'unknown';

export interface StudioWorkflowBoardSnapshotRequest {
  readonly teamId?: string | null;
  readonly memberId?: string | null;
  readonly take?: number;
}

export interface StudioWorkflowBoardCounts {
  readonly running: number;
  readonly waiting: number;
  readonly failed: number;
  readonly retrying: number;
  readonly completed: number;
}

export interface StudioWorkflowBoardProgress {
  readonly completedSteps: number;
  readonly totalSteps: number;
}

export interface StudioWorkflowBoardCurrentNode {
  readonly nodeId: string;
  readonly name: string;
  readonly status: StudioWorkflowBoardCurrentNodeStatus;
  readonly startedAt?: string | null;
  readonly updatedAt?: string | null;
  readonly durationMs?: number | null;
}

export interface StudioWorkflowBoardCompletedNode {
  readonly nodeId: string;
  readonly name: string;
  readonly completedAt?: string | null;
  readonly durationMs?: number | null;
}

export interface StudioWorkflowBoardPendingNode {
  readonly nodeId: string;
  readonly name: string;
  readonly status: StudioWorkflowBoardPendingNodeStatus;
  readonly reason?: string | null;
}

export interface StudioWorkflowBoardFailedNode {
  readonly nodeId: string;
  readonly name: string;
  readonly failedAt?: string | null;
}

export interface StudioWorkflowBoardMemberSnapshot {
  readonly memberId: string;
  readonly displayName: string;
  readonly executionAvailability: StudioWorkflowBoardExecutionAvailability;
  readonly executionStatus: StudioWorkflowBoardExecutionStatus;
  readonly progress: StudioWorkflowBoardProgress;
  readonly completedNodes: readonly StudioWorkflowBoardCompletedNode[];
  readonly pendingNodes: readonly StudioWorkflowBoardPendingNode[];
  readonly failedNodes: readonly StudioWorkflowBoardFailedNode[];
  readonly workflowId?: string | null;
  readonly workflowName?: string | null;
  readonly publishedServiceId?: string | null;
  readonly actorId?: string | null;
  readonly roleSummary?: string | null;
  readonly currentExecutionId?: string | null;
  readonly currentNode?: StudioWorkflowBoardCurrentNode | null;
  readonly lastNodeUpdatedAt?: string | null;
}

export interface StudioWorkflowBoardTeamSnapshot {
  readonly teamId: string;
  readonly teamName: string;
  readonly totalMemberCount?: number | null;
  readonly members: readonly StudioWorkflowBoardMemberSnapshot[];
}

export interface StudioWorkflowBoardSnapshot {
  readonly scopeId: string;
  readonly generatedAt: string;
  readonly watermark?: string | null;
  readonly counts: StudioWorkflowBoardCounts;
  readonly teams: readonly StudioWorkflowBoardTeamSnapshot[];
  readonly lastNodeUpdatedAt?: string | null;
}

export interface StudioTeamCreateInput {
  readonly scopeId: string;
  readonly displayName: string;
  readonly description?: string | null;
  readonly teamId?: string | null;
}

export interface StudioTeamUpdateInput {
  readonly scopeId: string;
  readonly teamId: string;
  readonly displayName?: string | null;
  readonly description?: string | null;
}

export type StudioTeamCommandStatus = 'accepted' | 'no_change' | 'unknown';

export interface StudioTeamCommandResponse {
  readonly status: StudioTeamCommandStatus;
  readonly scopeId: string;
  readonly teamId: string;
  readonly commandId?: string | null;
  readonly correlationId?: string | null;
  readonly ackedAt?: string | null;
}

export type StudioMemberBindingTargetKind = StudioScopeBindingTargetKind;
export type StudioMemberBindingResult = StudioScopeBindingResult;
export type StudioMemberBindingRevision = StudioScopeBindingRevision;
export type StudioMemberBindingStatus = StudioMemberBindingViewResponse;
export type StudioMemberBindingActivationResult =
  StudioScopeBindingActivationResult;
export type StudioMemberBindingRetirementResult =
  StudioScopeBindingRetirementResult;
export const normalizeStudioMemberBindingImplementationKind =
  normalizeStudioScopeBindingImplementationKind;
export const formatStudioMemberBindingImplementationKind =
  formatStudioScopeBindingImplementationKind;
export const describeStudioMemberBindingRevisionTarget =
  describeStudioScopeBindingRevisionTarget;
export const describeStudioMemberBindingRevisionContext =
  describeStudioScopeBindingRevisionContext;
export const getStudioMemberBindingCurrentRevision =
  getStudioScopeBindingCurrentRevision;

export type StudioDefaultRouteTargetRevision = StudioScopeBindingRevision;
export type StudioDefaultRouteTargetStatus = StudioScopeBindingStatus;
export const describeStudioDefaultRouteTargetRevisionTarget =
  describeStudioScopeBindingRevisionTarget;
export const describeStudioDefaultRouteTargetRevisionContext =
  describeStudioScopeBindingRevisionContext;
export const getStudioDefaultRouteTargetCurrentRevision =
  getStudioScopeBindingCurrentRevision;

export interface StudioScopeScriptBindingInput {
  readonly scopeId: string;
  readonly displayName?: string | null;
  readonly serviceId?: string | null;
  readonly scriptId: string;
  readonly scriptRevision: string;
}

export type StudioScopeScriptBindingResult = StudioScopeBindingResult;
export type StudioScopeScriptBindingStatus = StudioScopeBindingStatus;
export type StudioScopeScriptBindingActivationResult =
  StudioScopeBindingActivationResult;

export interface StudioScopeGAgentEndpointInput {
  readonly endpointId: string;
  readonly displayName?: string | null;
  readonly kind?: 'command' | 'chat' | null;
  readonly requestTypeUrl?: string | null;
  readonly responseTypeUrl?: string | null;
  readonly description?: string | null;
}

export interface StudioScopeGAgentBindingInput {
  readonly scopeId: string;
  readonly serviceId?: string | null;
  readonly displayName?: string | null;
  readonly agentKind: string;
  readonly endpoints: readonly StudioScopeGAgentEndpointInput[];
  readonly revisionId?: string | null;
}

export type StudioScopeGAgentBindingResult = StudioScopeBindingResult;

export interface StudioHttpConnectorDefinition {
  readonly baseUrl: string;
  readonly allowedMethods: string[];
  readonly allowedPaths: string[];
  readonly allowedInputKeys: string[];
  readonly defaultHeaders: Record<string, string>;
}

export interface StudioCliConnectorDefinition {
  readonly command: string;
  readonly fixedArguments: string[];
  readonly allowedOperations: string[];
  readonly allowedInputKeys: string[];
  readonly workingDirectory: string;
  readonly environment: Record<string, string>;
}

export interface StudioMcpConnectorDefinition {
  readonly serverName: string;
  readonly command: string;
  readonly arguments: string[];
  readonly environment: Record<string, string>;
  readonly defaultTool: string;
  readonly allowedTools: string[];
  readonly allowedInputKeys: string[];
}

export interface StudioConnectorDefinition {
  readonly name: string;
  readonly type: string;
  readonly enabled: boolean;
  readonly timeoutMs: number;
  readonly retry: number;
  readonly http?: StudioHttpConnectorDefinition;
  readonly cli?: StudioCliConnectorDefinition;
  readonly mcp?: StudioMcpConnectorDefinition;
}

export interface StudioConnectorCatalog {
  readonly homeDirectory: string;
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly connectors: StudioConnectorDefinition[];
}

export interface StudioConnectorCatalogImportResult
  extends StudioConnectorCatalog {
  readonly sourceFilePath: string;
  readonly sourceFileExists: boolean;
  readonly importedCount: number;
}

export interface StudioConnectorDraftResponse {
  readonly homeDirectory: string;
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly updatedAtUtc: string | null;
  readonly draft: StudioConnectorDefinition | null;
}

export interface StudioRoleDefinition {
  readonly id: string;
  readonly name: string;
  readonly systemPrompt: string;
  readonly provider: string;
  readonly model: string;
  readonly connectors: string[];
}

export interface StudioRoleCatalog {
  readonly homeDirectory: string;
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly roles: StudioRoleDefinition[];
}

export interface StudioRoleCatalogImportResult extends StudioRoleCatalog {
  readonly sourceFilePath: string;
  readonly sourceFileExists: boolean;
  readonly importedCount: number;
}

export interface StudioRoleDraftResponse {
  readonly homeDirectory: string;
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly updatedAtUtc: string | null;
  readonly draft: StudioRoleDefinition | null;
}

export interface StudioUserConfig {
  readonly defaultModel: string;
  readonly preferredLlmRoute?: string | null;
  readonly runtimeMode?: string | null;
  readonly localRuntimeBaseUrl?: string | null;
  readonly remoteRuntimeBaseUrl?: string | null;
  readonly maxToolRounds?: number | null;
}

export interface StudioUserLlmRouteOption {
  readonly routeValue: string;
  readonly label: string;
  readonly source: string;
  readonly status: string;
  readonly allowed: boolean;
  readonly ready: boolean;
  readonly userServiceId?: string | null;
  readonly serviceSlug?: string | null;
  readonly modelCatalog: StudioLlmModelCatalog;
  readonly description?: string | null;
}

export interface StudioUserLlmModelGroup {
  readonly routeValue: string;
  readonly groupId: string;
  readonly label: string;
  readonly models: readonly string[];
}

export interface StudioUserLlmSettingsCapabilities {
  readonly canEditRoute: boolean;
  readonly canEditModel: boolean;
  readonly canSave: boolean;
  readonly canRetryCatalog: boolean;
}

export type StudioLlmModelSelection =
  | { readonly kind: 'unspecified' }
  | { readonly kind: 'provider_default' }
  | { readonly kind: 'explicit_model'; readonly modelId: string };

export type StudioSelectedLlmModelSelection = Exclude<
  StudioLlmModelSelection,
  { kind: 'unspecified' }
>;

export type StudioLlmSelection =
  | {
      readonly routeKind: 'unspecified';
      readonly modelSelection: { readonly kind: 'unspecified' };
    }
  | {
      readonly routeKind: 'gateway';
      readonly routeValue: string;
      readonly modelSelection: StudioSelectedLlmModelSelection;
    }
  | {
      readonly routeKind: 'nyx_id_user_service';
      readonly routeValue: string;
      readonly nyxIdUserServiceId: string;
      readonly serviceSlugSnapshot: string;
      readonly modelSelection: StudioSelectedLlmModelSelection;
    };

export type StudioLlmModelCatalogCertainty =
  | 'enumerated'
  | 'not_verifiable'
  | 'unavailable';

export type StudioLlmModelCatalogDiagnostic =
  | 'unspecified'
  | 'not_published'
  | 'route_not_ready'
  | 'access_denied'
  | 'observation_unavailable'
  | 'response_invalid'
  | 'response_too_large'
  | 'pattern_only';

export interface StudioLlmModelCatalog {
  readonly certainty: StudioLlmModelCatalogCertainty;
  readonly modelIds: readonly string[];
  readonly defaultModelId?: string | null;
  readonly diagnostic: StudioLlmModelCatalogDiagnostic;
}

export type StudioUserLlmSelectionStatus =
  | 'system_default'
  | 'ready'
  | 'verification_unavailable'
  | 'needs_repair'
  | 'legacy_repair_required';

export type StudioUserLlmRemediation =
  | 'none'
  | 'retry_catalog'
  | 'connect_provider'
  | 'choose_replacement'
  | 'reselect';

export type StudioSaveUserLlmIntent =
  | { readonly action: 'reset' }
  | {
      readonly action: 'select_gateway';
      readonly gateway: { readonly model: StudioSelectedLlmModelSelection };
    }
  | {
      readonly action: 'select_user_service';
      readonly userService: {
        readonly userServiceId: string;
        readonly model: StudioSelectedLlmModelSelection;
      };
    }
  | {
      readonly action: 'activate_preset';
      readonly preset: { readonly presetId: string };
    };

export interface StudioUserLlmSettings {
  readonly savedSelection?: StudioLlmSelection | null;
  readonly savedRouteLabel: string;
  readonly selectionStatus: StudioUserLlmSelectionStatus;
  readonly catalogDiagnostic: StudioLlmModelCatalogDiagnostic;
  readonly remediation: StudioUserLlmRemediation;
  readonly routeOptions: readonly StudioUserLlmRouteOption[];
  readonly modelGroupsByRoute: readonly StudioUserLlmModelGroup[];
  readonly catalogStatus: 'ready' | 'empty' | 'unavailable' | string;
  readonly capabilities: StudioUserLlmSettingsCapabilities;
  readonly setupHint?: unknown;
}

export interface StudioUserConfigSaveReceipt {
  readonly accepted: boolean;
  readonly commandId: string;
  readonly ackStage: string;
  readonly actorId: string;
  readonly correlationId: string;
  readonly ackedAtUtc: string;
}

export interface StudioUserConfigRuntimeDefaults {
  readonly localRuntimeBaseUrl: string;
  readonly remoteRuntimeBaseUrl: string;
  readonly localMode: string;
  readonly remoteMode: string;
}

export interface StudioUserConfigRuntime {
  readonly runtimeMode: string;
  readonly activeRuntimeBaseUrl: string;
  readonly localRuntimeBaseUrl: string;
  readonly remoteRuntimeBaseUrl: string;
  readonly runtimeDefaults: StudioUserConfigRuntimeDefaults;
}

export interface StudioOrnnSkillSummary {
  readonly guid: string;
  readonly name: string;
  readonly description: string;
  readonly isPrivate: boolean;
}

export interface StudioOrnnSkillSearchResult {
  readonly baseUrl: string;
  readonly total: number;
  readonly totalPages: number;
  readonly page: number;
  readonly pageSize: number;
  readonly items: StudioOrnnSkillSummary[];
  readonly message?: string;
}

export interface StudioOrnnHealthResult {
  readonly baseUrl: string;
  readonly reachable: boolean;
  readonly message: string;
}
