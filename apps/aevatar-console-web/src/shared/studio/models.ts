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
  connectors?: unknown[];
};

export type StudioWorkflowStepDocument = Record<string, unknown> & {
  id?: string;
  type?: string;
  originalType?: string;
  targetRole?: string | null;
  target_role?: string | null;
  parameters?: Record<string, unknown> | null;
  next?: string | null;
  branches?: Record<string, string> | null;
};

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
  readonly providerDisplayName?: string;
  readonly loginUrl?: string;
  readonly logoutUrl?: string;
  readonly invokeAuthMode?: "studio-session" | "bearer-token" | "anonymous";
  readonly externalCallerHint?: string;
  readonly name?: string;
  readonly email?: string;
  readonly picture?: string;
  readonly errorMessage?: string;
  readonly scopeId?: string | null;
  readonly scopeSource?: string | null;
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
  readonly auditSource?: 'service-run-summary' | 'run-audit' | 'invoke-session';
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

export type StudioScopeBindingImplementationKind =
  | 'workflow'
  | 'script'
  | 'gagent'
  | 'unknown';

export type StudioScopeBindingTargetKind =
  StudioScopeBindingImplementationKind;

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

  const normalized = String(value || '').trim().toLowerCase();
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
    readonly actorTypeName: string;
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

  switch (normalizeStudioScopeBindingImplementationKind(revision.implementationKind)) {
    case 'workflow':
      return revision.workflowName || 'Workflow';
    case 'script':
      return revision.scriptId || 'Script';
    case 'gagent':
      return revision.staticActorTypeName || 'GAgent';
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

  switch (normalizeStudioScopeBindingImplementationKind(revision.implementationKind)) {
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
  switch (String(value || '').trim().toLowerCase()) {
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
  readonly lifecycleStage: StudioMemberLifecycleStage;
  readonly publishedServiceId: string;
  readonly lastBoundRevisionId: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface StudioMemberImplementationRef {
  readonly implementationKind: StudioMemberImplementationKind;
  readonly workflowId?: string | null;
  readonly workflowRevision?: string | null;
  readonly scriptId?: string | null;
  readonly scriptRevision?: string | null;
  readonly actorTypeName?: string | null;
}

export interface StudioMemberBindingContract {
  readonly publishedServiceId: string;
  readonly revisionId: string;
  readonly implementationKind: StudioMemberImplementationKind;
  readonly boundAt: string;
}

export interface StudioMemberDetail {
  readonly summary: StudioMemberSummary;
  readonly implementationRef?: StudioMemberImplementationRef | null;
  readonly lastBinding?: StudioMemberBindingContract | null;
}

export interface StudioMemberRoster {
  readonly scopeId: string;
  readonly members: readonly StudioMemberSummary[];
  readonly nextPageToken?: string | null;
}

export type StudioMemberBindingTargetKind = StudioScopeBindingTargetKind;
export type StudioMemberBindingResult = StudioScopeBindingResult;
export type StudioMemberBindingRevision = StudioScopeBindingRevision;
export type StudioMemberBindingStatus = StudioScopeBindingStatus;
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
  readonly scriptId: string;
  readonly scriptRevision: string;
  readonly revisionId?: string | null;
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
  readonly actorTypeName: string;
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

export interface StudioProviderType {
  readonly id: string;
  readonly displayName: string;
  readonly category: string;
  readonly description: string;
  readonly recommended: boolean;
  readonly defaultEndpoint: string;
  readonly defaultModel: string;
}

export interface StudioProviderSettings {
  readonly providerName: string;
  readonly providerType: string;
  readonly displayName: string;
  readonly category: string;
  readonly description: string;
  readonly model: string;
  readonly endpoint: string;
  readonly apiKey: string;
  readonly apiKeyConfigured: boolean;
  readonly clearApiKeyRequested?: boolean;
}

export interface StudioSettings {
  readonly runtimeBaseUrl: string;
  readonly defaultProviderName: string;
  readonly providerTypes: StudioProviderType[];
  readonly providers: StudioProviderSettings[];
}

export interface StudioSaveSettingsInput {
  readonly runtimeBaseUrl?: string | null;
  readonly defaultProviderName?: string | null;
  readonly providers?: Array<{
    readonly providerName: string;
    readonly providerType: string;
    readonly model: string;
    readonly endpoint?: string | null;
    readonly apiKey?: string | null;
    readonly clearApiKey?: boolean | null;
  }>;
}

export interface StudioRuntimeTestResult {
  readonly runtimeBaseUrl: string;
  readonly reachable: boolean;
  readonly checkedUrl: string;
  readonly statusCode: number | null;
  readonly message: string;
}

export interface StudioUserConfig {
  readonly defaultModel: string;
  readonly preferredLlmRoute?: string | null;
  readonly runtimeMode?: string | null;
  readonly localRuntimeBaseUrl?: string | null;
  readonly remoteRuntimeBaseUrl?: string | null;
  readonly maxToolRounds?: number | null;
}

export interface StudioUserConfigProviderStatus {
  readonly providerSlug: string;
  readonly providerName: string;
  readonly status: string;
  readonly proxyUrl: string;
  readonly source?: string;
}

export interface StudioUserConfigModelsResponse {
  readonly providers: StudioUserConfigProviderStatus[];
  readonly gatewayUrl: string;
  readonly modelsByProvider?: Record<string, string[]>;
  readonly supportedModels: string[];
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
