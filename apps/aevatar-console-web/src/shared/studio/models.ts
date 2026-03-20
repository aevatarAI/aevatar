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
  readonly features: {
    readonly publishedWorkflows: boolean;
    readonly scripts: boolean;
  };
}

export interface StudioAuthSession {
  readonly enabled: boolean;
  readonly authenticated: boolean;
  readonly providerDisplayName?: string;
  readonly loginUrl?: string;
  readonly logoutUrl?: string;
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

export interface StudioWorkflowSummary {
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

export interface StudioWorkflowFile {
  readonly workflowId: string;
  readonly name: string;
  readonly fileName: string;
  readonly filePath: string;
  readonly directoryId: string;
  readonly directoryLabel: string;
  readonly yaml: string;
  readonly document?: StudioWorkflowDocument | null;
  readonly layout?: unknown;
  readonly findings: StudioValidationFinding[];
  readonly updatedAtUtc: string;
}

export interface StudioSaveWorkflowInput {
  readonly workflowId?: string | null;
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
