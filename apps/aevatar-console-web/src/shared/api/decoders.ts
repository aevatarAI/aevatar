import type {
  WorkflowResumeResponse,
  WorkflowSignalResponse,
} from '@aevatar-react-sdk/types';
import type {
  ConfigurationCollectionRawDocument,
  ConfigurationDoctorReport,
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationMcpServer,
  ConfigurationLlmProbeResult,
  ConfigurationLlmInstance,
  ConfigurationLlmProviderType,
  ConfigurationPathInfo,
  ConfigurationPathStatus,
  ConfigurationRawDocument,
  ConfigurationSecretValueStatus,
  ConfigurationSecp256k1GenerateResult,
  ConfigurationSecp256k1PrivateKeyStatus,
  ConfigurationSecp256k1PublicKeyStatus,
  ConfigurationSecp256k1Status,
  ConfigurationSkillsMpStatus,
  ConfigurationSourceStatus,
  ConfigurationValidationResult,
  ConfigurationWebSearchStatus,
  ConfigurationWorkflowFile,
  ConfigurationWorkflowFileDetail,
  PlaygroundWorkflowParseResult,
  PlaygroundWorkflowSaveResult,
  WorkflowActorGraphEdge,
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorGraphNode,
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
  WorkflowAgentSummary,
  WorkflowAuthoringDefinition,
  WorkflowAuthoringEdge,
  WorkflowAuthoringErrorPolicy,
  WorkflowAuthoringRetryPolicy,
  WorkflowAuthoringRole,
  WorkflowAuthoringStep,
  WorkflowCapabilities,
  WorkflowCapabilityParameter,
  WorkflowCapabilityWorkflow,
  WorkflowCapabilityWorkflowStep,
  WorkflowConnectorCapability,
  WorkflowCatalogChildStep,
  WorkflowCatalogDefinition,
  WorkflowCatalogEdge,
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
  WorkflowCatalogRole,
  WorkflowCatalogStep,
  WorkflowLlmStatus,
  WorkflowPrimitiveCapability,
  WorkflowPrimitiveDescriptor,
  WorkflowPrimitiveParameterDescriptor,
} from './models';

export type Decoder<T> = (value: unknown, label?: string) => T;

type JsonRecord = Record<string, unknown>;

function expectRecord(value: unknown, label: string): JsonRecord {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }

  return value as JsonRecord;
}

function expectArray<T>(
  value: unknown,
  label: string,
  decoder: Decoder<T>,
): T[] {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }

  return value.map((entry, index) => decoder(entry, `${label}[${index}]`));
}

function expectString(value: unknown, label: string): string {
  if (typeof value !== 'string') {
    throw new Error(`${label} must be a string.`);
  }

  return value;
}

function expectBoolean(value: unknown, label: string): boolean {
  if (typeof value !== 'boolean') {
    throw new Error(`${label} must be a boolean.`);
  }

  return value;
}

function expectNumber(value: unknown, label: string): number {
  if (typeof value !== 'number' || Number.isNaN(value)) {
    throw new Error(`${label} must be a number.`);
  }

  return value;
}

function expectNullableNumber(value: unknown, label: string): number | null {
  return value === null ? null : expectNumber(value, label);
}

function expectNullableBoolean(value: unknown, label: string): boolean | null {
  return value === null ? null : expectBoolean(value, label);
}

function expectNullableString(value: unknown, label: string): string | null {
  return value === null ? null : expectString(value, label);
}

function expectOptionalString(value: unknown, label: string): string | undefined {
  return value === undefined || value === null
    ? undefined
    : expectString(value, label);
}

function expectOptionalBoolean(value: unknown, label: string): boolean | undefined {
  return value === undefined || value === null
    ? undefined
    : expectBoolean(value, label);
}

function expectOptionalNumber(value: unknown, label: string): number | undefined {
  return value === undefined || value === null
    ? undefined
    : expectNumber(value, label);
}

function expectStringArray(value: unknown, label: string): string[] {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }

  return value.map((entry, index) => expectString(entry, `${label}[${index}]`));
}

function expectStringRecord(
  value: unknown,
  label: string,
): Record<string, string> {
  const record = expectRecord(value, label);
  return Object.fromEntries(
    Object.entries(record).map(([key, entry]) => [
      key,
      expectString(entry, `${label}.${key}`),
    ]),
  );
}

function decodeWorkflowCatalogItem(value: unknown, label = 'WorkflowCatalogItem'): WorkflowCatalogItem {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    category: expectString(record.category, `${label}.category`),
    group: expectString(record.group, `${label}.group`),
    groupLabel: expectString(record.groupLabel, `${label}.groupLabel`),
    sortOrder: expectNumber(record.sortOrder, `${label}.sortOrder`),
    source: expectString(record.source, `${label}.source`),
    sourceLabel: expectString(record.sourceLabel, `${label}.sourceLabel`),
    showInLibrary: expectBoolean(record.showInLibrary, `${label}.showInLibrary`),
    isPrimitiveExample: expectBoolean(
      record.isPrimitiveExample,
      `${label}.isPrimitiveExample`,
    ),
    requiresLlmProvider: expectBoolean(
      record.requiresLlmProvider,
      `${label}.requiresLlmProvider`,
    ),
    primitives: expectStringArray(record.primitives, `${label}.primitives`),
  };
}

function decodeConfigurationPathInfo(
  value: unknown,
  label = 'ConfigurationPathInfo',
): ConfigurationPathInfo {
  const record = expectRecord(value, label);
  const homeEnvValue = expectOptionalString(record.homeEnvValue, `${label}.homeEnvValue`);
  const secretsPathEnvValue = expectOptionalString(
    record.secretsPathEnvValue,
    `${label}.secretsPathEnvValue`,
  );
  return {
    root: expectString(record.root, `${label}.root`),
    secretsJson: expectString(record.secretsJson, `${label}.secretsJson`),
    configJson: expectString(record.configJson, `${label}.configJson`),
    connectorsJson: expectString(record.connectorsJson, `${label}.connectorsJson`),
    mcpJson: expectString(record.mcpJson, `${label}.mcpJson`),
    workflowsHome: expectString(record.workflowsHome, `${label}.workflowsHome`),
    workflowsRepo: expectString(record.workflowsRepo, `${label}.workflowsRepo`),
    homeEnvValue: homeEnvValue ?? null,
    secretsPathEnvValue: secretsPathEnvValue ?? null,
  };
}

function decodeConfigurationPathStatus(
  value: unknown,
  label = 'ConfigurationPathStatus',
): ConfigurationPathStatus {
  const record = expectRecord(value, label);
  const sizeBytes = expectOptionalNumber(record.sizeBytes, `${label}.sizeBytes`);
  const error = expectOptionalString(record.error, `${label}.error`);
  return {
    path: expectString(record.path, `${label}.path`),
    exists: expectBoolean(record.exists, `${label}.exists`),
    readable: expectBoolean(record.readable, `${label}.readable`),
    writable: expectBoolean(record.writable, `${label}.writable`),
    sizeBytes: sizeBytes ?? null,
    error: error ?? null,
  };
}

function decodeConfigurationDoctorReport(
  value: unknown,
  label = 'ConfigurationDoctorReport',
): ConfigurationDoctorReport {
  const record = expectRecord(value, label);
  return {
    paths: decodeConfigurationPathInfo(record.paths, `${label}.paths`),
    secrets: decodeConfigurationPathStatus(record.secrets, `${label}.secrets`),
    config: decodeConfigurationPathStatus(record.config, `${label}.config`),
    connectors: decodeConfigurationPathStatus(
      record.connectors,
      `${label}.connectors`,
    ),
    mcp: decodeConfigurationPathStatus(record.mcp, `${label}.mcp`),
    workflowsHome: decodeConfigurationPathStatus(
      record.workflowsHome,
      `${label}.workflowsHome`,
    ),
    workflowsRepo: decodeConfigurationPathStatus(
      record.workflowsRepo,
      `${label}.workflowsRepo`,
    ),
  };
}

function decodeConfigurationSourceStatus(
  value: unknown,
  label = 'ConfigurationSourceStatus',
): ConfigurationSourceStatus {
  const record = expectRecord(value, label);
  return {
    mode: expectString(record.mode, `${label}.mode`),
    mongoConfigured: expectBoolean(record.mongoConfigured, `${label}.mongoConfigured`),
    fileConfigured: expectBoolean(record.fileConfigured, `${label}.fileConfigured`),
    localRuntimeAccess: expectBoolean(
      record.localRuntimeAccess,
      `${label}.localRuntimeAccess`,
    ),
    paths: decodeConfigurationPathInfo(record.paths, `${label}.paths`),
    doctor: decodeConfigurationDoctorReport(record.doctor, `${label}.doctor`),
  };
}

function decodeConfigurationWorkflowFile(
  value: unknown,
  label = 'ConfigurationWorkflowFile',
): ConfigurationWorkflowFile {
  const record = expectRecord(value, label);
  return {
    filename: expectString(record.filename, `${label}.filename`),
    source: expectString(record.source, `${label}.source`),
    path: expectString(record.path, `${label}.path`),
    sizeBytes: expectNumber(record.sizeBytes, `${label}.sizeBytes`),
    lastModified: expectString(record.lastModified, `${label}.lastModified`),
  };
}

function decodeConfigurationWorkflowFileDetail(
  value: unknown,
  label = 'ConfigurationWorkflowFileDetail',
): ConfigurationWorkflowFileDetail {
  const record = expectRecord(value, label);
  return {
    ...decodeConfigurationWorkflowFile(value, label),
    content: expectString(record.content, `${label}.content`),
  };
}

function decodeConfigurationRawDocument(
  value: unknown,
  label = 'ConfigurationRawDocument',
): ConfigurationRawDocument {
  const record = expectRecord(value, label);
  return {
    json: expectString(record.json, `${label}.json`),
    keyCount: expectNumber(record.keyCount, `${label}.keyCount`),
    exists: expectOptionalBoolean(record.exists, `${label}.exists`),
    path: expectOptionalString(record.path, `${label}.path`),
  };
}

function decodeConfigurationCollectionRawDocument(
  value: unknown,
  label = 'ConfigurationCollectionRawDocument',
): ConfigurationCollectionRawDocument {
  const record = expectRecord(value, label);
  return {
    json: expectString(record.json, `${label}.json`),
    count: expectNumber(record.count, `${label}.count`),
    exists: expectOptionalBoolean(record.exists, `${label}.exists`),
    path: expectOptionalString(record.path, `${label}.path`),
  };
}

function decodeConfigurationValidationResult(
  value: unknown,
  label = 'ConfigurationValidationResult',
): ConfigurationValidationResult {
  const record = expectRecord(value, label);
  return {
    valid: expectBoolean(record.valid, `${label}.valid`),
    message: expectString(record.message, `${label}.message`),
    count: expectNumber(record.count, `${label}.count`),
  };
}

function decodeConfigurationMcpServer(
  value: unknown,
  label = 'ConfigurationMcpServer',
): ConfigurationMcpServer {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    command: expectString(record.command, `${label}.command`),
    args: expectStringArray(record.args, `${label}.args`),
    env: expectStringRecord(record.env, `${label}.env`),
    timeoutMs: expectNumber(record.timeoutMs, `${label}.timeoutMs`),
  };
}

function decodeConfigurationLlmApiKeyStatus(
  value: unknown,
  label = 'ConfigurationLlmApiKeyStatus',
): ConfigurationLlmApiKeyStatus {
  const record = expectRecord(value, label);
  return {
    providerName: expectString(record.providerName, `${label}.providerName`),
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
    value: expectOptionalString(record.value, `${label}.value`),
  };
}

function decodeConfigurationSecretValueStatus(
  value: unknown,
  label = 'ConfigurationSecretValueStatus',
): ConfigurationSecretValueStatus {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
    keyPath: expectString(record.keyPath, `${label}.keyPath`),
    value: expectOptionalString(record.value, `${label}.value`),
  };
}

function decodeConfigurationEmbeddingsStatus(
  value: unknown,
  label = 'ConfigurationEmbeddingsStatus',
): ConfigurationEmbeddingsStatus {
  const record = expectRecord(value, label);
  return {
    enabled: expectNullableBoolean(record.enabled, `${label}.enabled`),
    providerType: expectString(record.providerType, `${label}.providerType`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
    model: expectString(record.model, `${label}.model`),
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
  };
}

function decodeConfigurationWebSearchStatus(
  value: unknown,
  label = 'ConfigurationWebSearchStatus',
): ConfigurationWebSearchStatus {
  const record = expectRecord(value, label);
  return {
    enabled: expectNullableBoolean(record.enabled, `${label}.enabled`),
    effectiveEnabled: expectBoolean(record.effectiveEnabled, `${label}.effectiveEnabled`),
    provider: expectString(record.provider, `${label}.provider`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
    timeoutMs: expectNullableNumber(record.timeoutMs, `${label}.timeoutMs`),
    searchDepth: expectString(record.searchDepth, `${label}.searchDepth`),
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
    available: expectBoolean(record.available, `${label}.available`),
  };
}

function decodeConfigurationSkillsMpStatus(
  value: unknown,
  label = 'ConfigurationSkillsMpStatus',
): ConfigurationSkillsMpStatus {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
    keyPath: expectString(record.keyPath, `${label}.keyPath`),
    baseUrl: expectString(record.baseUrl, `${label}.baseUrl`),
  };
}

function decodeConfigurationSecp256k1PrivateKeyStatus(
  value: unknown,
  label = 'ConfigurationSecp256k1PrivateKeyStatus',
): ConfigurationSecp256k1PrivateKeyStatus {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    masked: expectString(record.masked, `${label}.masked`),
    keyPath: expectString(record.keyPath, `${label}.keyPath`),
    backupsPrefix: expectString(record.backupsPrefix, `${label}.backupsPrefix`),
    backupCount: expectNumber(record.backupCount, `${label}.backupCount`),
  };
}

function decodeConfigurationSecp256k1PublicKeyStatus(
  value: unknown,
  label = 'ConfigurationSecp256k1PublicKeyStatus',
): ConfigurationSecp256k1PublicKeyStatus {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    hex: expectString(record.hex, `${label}.hex`),
  };
}

function decodeConfigurationSecp256k1Status(
  value: unknown,
  label = 'ConfigurationSecp256k1Status',
): ConfigurationSecp256k1Status {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    privateKey: decodeConfigurationSecp256k1PrivateKeyStatus(
      record.privateKey,
      `${label}.privateKey`,
    ),
    publicKey: decodeConfigurationSecp256k1PublicKeyStatus(
      record.publicKey,
      `${label}.publicKey`,
    ),
  };
}

function decodeConfigurationSecp256k1GenerateResult(
  value: unknown,
  label = 'ConfigurationSecp256k1GenerateResult',
): ConfigurationSecp256k1GenerateResult {
  const record = expectRecord(value, label);
  return {
    backedUp: expectBoolean(record.backedUp, `${label}.backedUp`),
    publicKeyHex: expectString(record.publicKeyHex, `${label}.publicKeyHex`),
    status: decodeConfigurationSecp256k1Status(record.status, `${label}.status`),
  };
}

function decodeConfigurationLlmProbeResult(
  value: unknown,
  label = 'ConfigurationLlmProbeResult',
): ConfigurationLlmProbeResult {
  const record = expectRecord(value, label);
  return {
    ok: expectBoolean(record.ok, `${label}.ok`),
    providerName: expectString(record.providerName, `${label}.providerName`),
    kind: expectString(record.kind, `${label}.kind`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
    latencyMs: expectOptionalNumber(record.latencyMs, `${label}.latencyMs`),
    error: expectOptionalString(record.error, `${label}.error`),
    modelsCount: expectOptionalNumber(record.modelsCount, `${label}.modelsCount`),
    sampleModels:
      record.sampleModels === undefined
        ? undefined
        : expectStringArray(record.sampleModels, `${label}.sampleModels`),
    models:
      record.models === undefined
        ? undefined
        : expectStringArray(record.models, `${label}.models`),
  };
}

function decodeConfigurationLlmProviderType(
  value: unknown,
  label = 'ConfigurationLlmProviderType',
): ConfigurationLlmProviderType {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    displayName: expectString(record.displayName, `${label}.displayName`),
    category: expectString(record.category, `${label}.category`),
    description: expectString(record.description, `${label}.description`),
    recommended: expectBoolean(record.recommended, `${label}.recommended`),
    configuredInstancesCount: expectNumber(
      record.configuredInstancesCount,
      `${label}.configuredInstancesCount`,
    ),
  };
}

function decodeConfigurationLlmInstance(
  value: unknown,
  label = 'ConfigurationLlmInstance',
): ConfigurationLlmInstance {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    providerType: expectString(record.providerType, `${label}.providerType`),
    providerDisplayName: expectString(
      record.providerDisplayName,
      `${label}.providerDisplayName`,
    ),
    model: expectString(record.model, `${label}.model`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
  };
}

function decodeWorkflowCatalogRole(value: unknown, label = 'WorkflowCatalogRole'): WorkflowCatalogRole {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    name: expectString(record.name, `${label}.name`),
    systemPrompt: expectString(record.systemPrompt, `${label}.systemPrompt`),
    provider: expectString(record.provider, `${label}.provider`),
    model: expectString(record.model, `${label}.model`),
    temperature: expectNullableNumber(record.temperature, `${label}.temperature`),
    maxTokens: expectNullableNumber(record.maxTokens, `${label}.maxTokens`),
    maxToolRounds: expectNullableNumber(
      record.maxToolRounds,
      `${label}.maxToolRounds`,
    ),
    maxHistoryMessages: expectNullableNumber(
      record.maxHistoryMessages,
      `${label}.maxHistoryMessages`,
    ),
    streamBufferCapacity: expectNullableNumber(
      record.streamBufferCapacity,
      `${label}.streamBufferCapacity`,
    ),
    eventModules: expectStringArray(record.eventModules, `${label}.eventModules`),
    eventRoutes: expectString(record.eventRoutes, `${label}.eventRoutes`),
    connectors: expectStringArray(record.connectors, `${label}.connectors`),
  };
}

function decodeWorkflowCatalogChildStep(
  value: unknown,
  label = 'WorkflowCatalogChildStep',
): WorkflowCatalogChildStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
  };
}

function decodeWorkflowCatalogStep(value: unknown, label = 'WorkflowCatalogStep'): WorkflowCatalogStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
    parameters: expectStringRecord(record.parameters, `${label}.parameters`),
    next: expectString(record.next, `${label}.next`),
    branches: expectStringRecord(record.branches, `${label}.branches`),
    children: expectArray(
      record.children,
      `${label}.children`,
      decodeWorkflowCatalogChildStep,
    ),
  };
}

function decodeWorkflowCatalogEdge(value: unknown, label = 'WorkflowCatalogEdge'): WorkflowCatalogEdge {
  const record = expectRecord(value, label);
  return {
    from: expectString(record.from, `${label}.from`),
    to: expectString(record.to, `${label}.to`),
    label: expectString(record.label, `${label}.label`),
  };
}

function decodeWorkflowCatalogDefinition(
  value: unknown,
  label = 'WorkflowCatalogDefinition',
): WorkflowCatalogDefinition {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`,
    ),
    roles: expectArray(record.roles, `${label}.roles`, decodeWorkflowCatalogRole),
    steps: expectArray(record.steps, `${label}.steps`, decodeWorkflowCatalogStep),
  };
}

function decodeWorkflowCatalogItemDetail(
  value: unknown,
  label = 'WorkflowCatalogItemDetail',
): WorkflowCatalogItemDetail {
  const record = expectRecord(value, label);
  return {
    catalog: decodeWorkflowCatalogItem(record.catalog, `${label}.catalog`),
    yaml: expectString(record.yaml, `${label}.yaml`),
    definition: decodeWorkflowCatalogDefinition(
      record.definition,
      `${label}.definition`,
    ),
    edges: expectArray(record.edges, `${label}.edges`, decodeWorkflowCatalogEdge),
  };
}

function decodeWorkflowAgentSummary(
  value: unknown,
  label = 'WorkflowAgentSummary',
): WorkflowAgentSummary {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    description: expectString(record.description, `${label}.description`),
  };
}

function decodeWorkflowCapabilityParameter(
  value: unknown,
  label = 'WorkflowCapabilityParameter',
): WorkflowCapabilityParameter {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    required: expectBoolean(record.required, `${label}.required`),
    description: expectString(record.description, `${label}.description`),
    default: expectString(record.default, `${label}.default`),
    enum: expectStringArray(record.enum, `${label}.enum`),
  };
}

function decodeWorkflowPrimitiveCapability(
  value: unknown,
  label = 'WorkflowPrimitiveCapability',
): WorkflowPrimitiveCapability {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    aliases: expectStringArray(record.aliases, `${label}.aliases`),
    category: expectString(record.category, `${label}.category`),
    description: expectString(record.description, `${label}.description`),
    closedWorldBlocked: expectBoolean(
      record.closedWorldBlocked,
      `${label}.closedWorldBlocked`,
    ),
    runtimeModule: expectString(record.runtimeModule, `${label}.runtimeModule`),
    parameters: expectArray(
      record.parameters,
      `${label}.parameters`,
      decodeWorkflowCapabilityParameter,
    ),
  };
}

function decodeWorkflowConnectorCapability(
  value: unknown,
  label = 'WorkflowConnectorCapability',
): WorkflowConnectorCapability {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    enabled: expectBoolean(record.enabled, `${label}.enabled`),
    timeoutMs: expectNumber(record.timeoutMs, `${label}.timeoutMs`),
    retry: expectNumber(record.retry, `${label}.retry`),
    allowedInputKeys: expectStringArray(
      record.allowedInputKeys,
      `${label}.allowedInputKeys`,
    ),
    allowedOperations: expectStringArray(
      record.allowedOperations,
      `${label}.allowedOperations`,
    ),
    fixedArguments: expectStringArray(
      record.fixedArguments,
      `${label}.fixedArguments`,
    ),
  };
}

function decodeWorkflowCapabilityWorkflowStep(
  value: unknown,
  label = 'WorkflowCapabilityWorkflowStep',
): WorkflowCapabilityWorkflowStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    next: expectString(record.next, `${label}.next`),
  };
}

function decodeWorkflowCapabilityWorkflow(
  value: unknown,
  label = 'WorkflowCapabilityWorkflow',
): WorkflowCapabilityWorkflow {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    source: expectString(record.source, `${label}.source`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`,
    ),
    requiresLlmProvider: expectBoolean(
      record.requiresLlmProvider,
      `${label}.requiresLlmProvider`,
    ),
    primitives: expectStringArray(record.primitives, `${label}.primitives`),
    requiredConnectors: expectStringArray(
      record.requiredConnectors,
      `${label}.requiredConnectors`,
    ),
    workflowCalls: expectStringArray(
      record.workflowCalls,
      `${label}.workflowCalls`,
    ),
    steps: expectArray(
      record.steps,
      `${label}.steps`,
      decodeWorkflowCapabilityWorkflowStep,
    ),
  };
}

function decodeWorkflowCapabilities(
  value: unknown,
  label = 'WorkflowCapabilities',
): WorkflowCapabilities {
  const record = expectRecord(value, label);
  return {
    schemaVersion: expectString(record.schemaVersion, `${label}.schemaVersion`),
    generatedAtUtc: expectString(record.generatedAtUtc, `${label}.generatedAtUtc`),
    primitives: expectArray(
      record.primitives,
      `${label}.primitives`,
      decodeWorkflowPrimitiveCapability,
    ),
    connectors: expectArray(
      record.connectors,
      `${label}.connectors`,
      decodeWorkflowConnectorCapability,
    ),
    workflows: expectArray(
      record.workflows,
      `${label}.workflows`,
      decodeWorkflowCapabilityWorkflow,
    ),
  };
}

function decodeWorkflowAuthoringRetryPolicy(
  value: unknown,
  label = 'WorkflowAuthoringRetryPolicy',
): WorkflowAuthoringRetryPolicy {
  const record = expectRecord(value, label);
  return {
    maxAttempts: expectNumber(record.maxAttempts, `${label}.maxAttempts`),
    backoff: expectString(record.backoff, `${label}.backoff`),
    delayMs: expectNumber(record.delayMs, `${label}.delayMs`),
  };
}

function decodeWorkflowAuthoringErrorPolicy(
  value: unknown,
  label = 'WorkflowAuthoringErrorPolicy',
): WorkflowAuthoringErrorPolicy {
  const record = expectRecord(value, label);
  return {
    strategy: expectString(record.strategy, `${label}.strategy`),
    fallbackStep: expectNullableString(
      record.fallbackStep,
      `${label}.fallbackStep`,
    ),
    defaultOutput: expectNullableString(
      record.defaultOutput,
      `${label}.defaultOutput`,
    ),
  };
}

function decodeWorkflowAuthoringRole(
  value: unknown,
  label = 'WorkflowAuthoringRole',
): WorkflowAuthoringRole {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    name: expectString(record.name, `${label}.name`),
    systemPrompt: expectString(record.systemPrompt, `${label}.systemPrompt`),
    provider: expectNullableString(record.provider, `${label}.provider`),
    model: expectNullableString(record.model, `${label}.model`),
    temperature: expectNullableNumber(record.temperature, `${label}.temperature`),
    maxTokens: expectNullableNumber(record.maxTokens, `${label}.maxTokens`),
    maxToolRounds: expectNullableNumber(
      record.maxToolRounds,
      `${label}.maxToolRounds`,
    ),
    maxHistoryMessages: expectNullableNumber(
      record.maxHistoryMessages,
      `${label}.maxHistoryMessages`,
    ),
    streamBufferCapacity: expectNullableNumber(
      record.streamBufferCapacity,
      `${label}.streamBufferCapacity`,
    ),
    eventModules: expectStringArray(record.eventModules, `${label}.eventModules`),
    eventRoutes: expectString(record.eventRoutes, `${label}.eventRoutes`),
    connectors: expectStringArray(record.connectors, `${label}.connectors`),
  };
}

function decodeWorkflowAuthoringStep(
  value: unknown,
  label = 'WorkflowAuthoringStep',
): WorkflowAuthoringStep {
  const record = expectRecord(value, label);
  return {
    id: expectString(record.id, `${label}.id`),
    type: expectString(record.type, `${label}.type`),
    targetRole: expectString(record.targetRole, `${label}.targetRole`),
    parameters: expectStringRecord(record.parameters, `${label}.parameters`),
    next: expectNullableString(record.next, `${label}.next`),
    branches: expectStringRecord(record.branches, `${label}.branches`),
    children: expectArray(
      record.children,
      `${label}.children`,
      decodeWorkflowAuthoringStep,
    ),
    retry:
      record.retry === null
        ? null
        : decodeWorkflowAuthoringRetryPolicy(record.retry, `${label}.retry`),
    onError:
      record.onError === null
        ? null
        : decodeWorkflowAuthoringErrorPolicy(record.onError, `${label}.onError`),
    timeoutMs: expectNullableNumber(record.timeoutMs, `${label}.timeoutMs`),
  };
}

function decodeWorkflowAuthoringDefinition(
  value: unknown,
  label = 'WorkflowAuthoringDefinition',
): WorkflowAuthoringDefinition {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    description: expectString(record.description, `${label}.description`),
    closedWorldMode: expectBoolean(
      record.closedWorldMode,
      `${label}.closedWorldMode`,
    ),
    roles: expectArray(record.roles, `${label}.roles`, decodeWorkflowAuthoringRole),
    steps: expectArray(record.steps, `${label}.steps`, decodeWorkflowAuthoringStep),
  };
}

function decodeWorkflowAuthoringEdge(
  value: unknown,
  label = 'WorkflowAuthoringEdge',
): WorkflowAuthoringEdge {
  const record = expectRecord(value, label);
  return {
    from: expectString(record.from, `${label}.from`),
    to: expectString(record.to, `${label}.to`),
    label: expectString(record.label, `${label}.label`),
  };
}

function decodePlaygroundWorkflowParseResult(
  value: unknown,
  label = 'PlaygroundWorkflowParseResult',
): PlaygroundWorkflowParseResult {
  const record = expectRecord(value, label);
  return {
    valid: expectBoolean(record.valid, `${label}.valid`),
    error: expectNullableString(record.error, `${label}.error`),
    errors: expectStringArray(record.errors, `${label}.errors`),
    definition:
      record.definition === null
        ? null
        : decodeWorkflowAuthoringDefinition(
            record.definition,
            `${label}.definition`,
          ),
    edges: expectArray(record.edges, `${label}.edges`, decodeWorkflowAuthoringEdge),
  };
}

function decodePlaygroundWorkflowSaveResult(
  value: unknown,
  label = 'PlaygroundWorkflowSaveResult',
): PlaygroundWorkflowSaveResult {
  const record = expectRecord(value, label);
  return {
    saved: expectBoolean(record.saved, `${label}.saved`),
    filename: expectString(record.filename, `${label}.filename`),
    savedPath: expectString(record.savedPath, `${label}.savedPath`),
    workflowName: expectString(record.workflowName, `${label}.workflowName`),
    overwritten: expectBoolean(record.overwritten, `${label}.overwritten`),
    savedSource: expectString(record.savedSource, `${label}.savedSource`),
    effectiveSource: expectString(
      record.effectiveSource,
      `${label}.effectiveSource`,
    ),
    effectivePath: expectString(record.effectivePath, `${label}.effectivePath`),
  };
}

function decodeWorkflowPrimitiveParameterDescriptor(
  value: unknown,
  label = 'WorkflowPrimitiveParameterDescriptor',
): WorkflowPrimitiveParameterDescriptor {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    type: expectString(record.type, `${label}.type`),
    required: expectBoolean(record.required, `${label}.required`),
    description: expectString(record.description, `${label}.description`),
    default: expectString(record.default, `${label}.default`),
    enumValues: expectStringArray(record.enumValues, `${label}.enumValues`),
  };
}

function decodeWorkflowPrimitiveDescriptor(
  value: unknown,
  label = 'WorkflowPrimitiveDescriptor',
): WorkflowPrimitiveDescriptor {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    aliases: expectStringArray(record.aliases, `${label}.aliases`),
    category: expectString(record.category, `${label}.category`),
    description: expectString(record.description, `${label}.description`),
    parameters: expectArray(
      record.parameters,
      `${label}.parameters`,
      decodeWorkflowPrimitiveParameterDescriptor,
    ),
    exampleWorkflows: expectStringArray(
      record.exampleWorkflows,
      `${label}.exampleWorkflows`,
    ),
  };
}

function decodeWorkflowLlmStatus(
  value: unknown,
  label = 'WorkflowLlmStatus',
): WorkflowLlmStatus {
  const record = expectRecord(value, label);
  return {
    available: expectBoolean(record.available, `${label}.available`),
    provider: expectNullableString(record.provider, `${label}.provider`),
    model: expectNullableString(record.model, `${label}.model`),
    providers: expectStringArray(record.providers, `${label}.providers`),
  };
}

function decodeWorkflowActorSnapshot(
  value: unknown,
  label = 'WorkflowActorSnapshot',
): WorkflowActorSnapshot {
  const record = expectRecord(value, label);
  return {
    actorId: expectString(record.actorId, `${label}.actorId`),
    workflowName: expectString(record.workflowName, `${label}.workflowName`),
    lastCommandId: expectString(record.lastCommandId, `${label}.lastCommandId`),
    stateVersion: expectNumber(record.stateVersion, `${label}.stateVersion`),
    lastEventId: expectString(record.lastEventId, `${label}.lastEventId`),
    lastUpdatedAt: expectString(record.lastUpdatedAt, `${label}.lastUpdatedAt`),
    lastSuccess: expectNullableBoolean(record.lastSuccess, `${label}.lastSuccess`),
    lastOutput: expectString(record.lastOutput, `${label}.lastOutput`),
    lastError: expectString(record.lastError, `${label}.lastError`),
    totalSteps: expectNumber(record.totalSteps, `${label}.totalSteps`),
    requestedSteps: expectNumber(
      record.requestedSteps,
      `${label}.requestedSteps`,
    ),
    completedSteps: expectNumber(
      record.completedSteps,
      `${label}.completedSteps`,
    ),
    roleReplyCount: expectNumber(record.roleReplyCount, `${label}.roleReplyCount`),
  };
}

function decodeWorkflowActorTimelineItem(
  value: unknown,
  label = 'WorkflowActorTimelineItem',
): WorkflowActorTimelineItem {
  const record = expectRecord(value, label);
  return {
    timestamp: expectString(record.timestamp, `${label}.timestamp`),
    stage: expectString(record.stage, `${label}.stage`),
    message: expectString(record.message, `${label}.message`),
    agentId: expectString(record.agentId, `${label}.agentId`),
    stepId: expectString(record.stepId, `${label}.stepId`),
    stepType: expectString(record.stepType, `${label}.stepType`),
    eventType: expectString(record.eventType, `${label}.eventType`),
    data: expectStringRecord(record.data, `${label}.data`),
  };
}

function decodeWorkflowActorGraphNode(
  value: unknown,
  label = 'WorkflowActorGraphNode',
): WorkflowActorGraphNode {
  const record = expectRecord(value, label);
  return {
    nodeId: expectString(record.nodeId, `${label}.nodeId`),
    nodeType: expectString(record.nodeType, `${label}.nodeType`),
    updatedAt: expectString(record.updatedAt, `${label}.updatedAt`),
    properties: expectStringRecord(record.properties, `${label}.properties`),
  };
}

function decodeWorkflowActorGraphEdge(
  value: unknown,
  label = 'WorkflowActorGraphEdge',
): WorkflowActorGraphEdge {
  const record = expectRecord(value, label);
  return {
    edgeId: expectString(record.edgeId, `${label}.edgeId`),
    fromNodeId: expectString(record.fromNodeId, `${label}.fromNodeId`),
    toNodeId: expectString(record.toNodeId, `${label}.toNodeId`),
    edgeType: expectString(record.edgeType, `${label}.edgeType`),
    updatedAt: expectString(record.updatedAt, `${label}.updatedAt`),
    properties: expectStringRecord(record.properties, `${label}.properties`),
  };
}

function decodeWorkflowActorGraphSubgraph(
  value: unknown,
  label = 'WorkflowActorGraphSubgraph',
): WorkflowActorGraphSubgraph {
  const record = expectRecord(value, label);
  return {
    rootNodeId: expectString(record.rootNodeId, `${label}.rootNodeId`),
    nodes: expectArray(record.nodes, `${label}.nodes`, decodeWorkflowActorGraphNode),
    edges: expectArray(record.edges, `${label}.edges`, decodeWorkflowActorGraphEdge),
  };
}

function decodeWorkflowActorGraphEnrichedSnapshot(
  value: unknown,
  label = 'WorkflowActorGraphEnrichedSnapshot',
): WorkflowActorGraphEnrichedSnapshot {
  const record = expectRecord(value, label);
  return {
    snapshot: decodeWorkflowActorSnapshot(record.snapshot, `${label}.snapshot`),
    subgraph: decodeWorkflowActorGraphSubgraph(
      record.subgraph,
      `${label}.subgraph`,
    ),
  };
}

function decodeWorkflowResumeResponse(
  value: unknown,
  label = 'WorkflowResumeResponse',
): WorkflowResumeResponse {
  const record = expectRecord(value, label);
  return {
    accepted: expectBoolean(record.accepted, `${label}.accepted`),
    actorId: expectOptionalString(record.actorId, `${label}.actorId`),
    runId: expectOptionalString(record.runId, `${label}.runId`),
    stepId: expectOptionalString(record.stepId, `${label}.stepId`),
    commandId: expectOptionalString(record.commandId, `${label}.commandId`),
  };
}

function decodeWorkflowSignalResponse(
  value: unknown,
  label = 'WorkflowSignalResponse',
): WorkflowSignalResponse {
  const record = expectRecord(value, label);
  return {
    accepted: expectBoolean(record.accepted, `${label}.accepted`),
    actorId: expectOptionalString(record.actorId, `${label}.actorId`),
    runId: expectOptionalString(record.runId, `${label}.runId`),
    signalName: expectOptionalString(record.signalName, `${label}.signalName`),
    stepId: expectOptionalString(record.stepId, `${label}.stepId`),
    commandId: expectOptionalString(record.commandId, `${label}.commandId`),
  };
}

export const decodeWorkflowAgentSummaries: Decoder<WorkflowAgentSummary[]> = (
  value,
) => expectArray(value, 'WorkflowAgentSummary[]', decodeWorkflowAgentSummary);

export const decodeWorkflowNames: Decoder<string[]> = (value) =>
  expectStringArray(value, 'WorkflowNames');

export const decodeWorkflowCatalogItems: Decoder<WorkflowCatalogItem[]> = (
  value,
) => expectArray(value, 'WorkflowCatalogItem[]', decodeWorkflowCatalogItem);

export const decodeConfigurationSourceStatusResponse: Decoder<ConfigurationSourceStatus> = (
  value,
) => decodeConfigurationSourceStatus(value);

export const decodeConfigurationWorkflowFilesResponse: Decoder<ConfigurationWorkflowFile[]> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationWorkflowFilesResponse');
  return expectArray(
    record.workflows,
    'ConfigurationWorkflowFilesResponse.workflows',
    decodeConfigurationWorkflowFile,
  );
};

export const decodeConfigurationWorkflowFileDetailResponse: Decoder<ConfigurationWorkflowFileDetail> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationWorkflowFileDetailResponse');
  return decodeConfigurationWorkflowFileDetail(
    record.workflow,
    'ConfigurationWorkflowFileDetailResponse.workflow',
  );
};

export const decodeConfigurationWorkflowFileMutationResponse: Decoder<ConfigurationWorkflowFile> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationWorkflowFileMutationResponse');
  return decodeConfigurationWorkflowFile(
    record.workflow,
    'ConfigurationWorkflowFileMutationResponse.workflow',
  );
};

export const decodeConfigurationRawDocumentResponse: Decoder<ConfigurationRawDocument> = (
  value,
) => decodeConfigurationRawDocument(value);

export const decodeConfigurationCollectionRawDocumentResponse: Decoder<ConfigurationCollectionRawDocument> = (
  value,
) => decodeConfigurationCollectionRawDocument(value);

export const decodeConfigurationValidationResultResponse: Decoder<ConfigurationValidationResult> = (
  value,
) => decodeConfigurationValidationResult(value);

export const decodeConfigurationMcpServersResponse: Decoder<ConfigurationMcpServer[]> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationMcpServersResponse');
  return expectArray(
    record.servers,
    'ConfigurationMcpServersResponse.servers',
    decodeConfigurationMcpServer,
  );
};

export const decodeConfigurationMcpServerMutationResponse: Decoder<ConfigurationMcpServer> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationMcpServerMutationResponse');
  return decodeConfigurationMcpServer(
    record.server,
    'ConfigurationMcpServerMutationResponse.server',
  );
};

export const decodeConfigurationLlmApiKeyStatusResponse: Decoder<ConfigurationLlmApiKeyStatus> = (
  value,
) => decodeConfigurationLlmApiKeyStatus(value);

export const decodeConfigurationSecretValueStatusResponse: Decoder<ConfigurationSecretValueStatus> = (
  value,
) => decodeConfigurationSecretValueStatus(value);

export const decodeConfigurationEmbeddingsStatusResponse: Decoder<ConfigurationEmbeddingsStatus> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationEmbeddingsStatusResponse');
  return decodeConfigurationEmbeddingsStatus(
    record.embeddings,
    'ConfigurationEmbeddingsStatusResponse.embeddings',
  );
};

export const decodeConfigurationWebSearchStatusResponse: Decoder<ConfigurationWebSearchStatus> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationWebSearchStatusResponse');
  return decodeConfigurationWebSearchStatus(
    record.webSearch,
    'ConfigurationWebSearchStatusResponse.webSearch',
  );
};

export const decodeConfigurationSkillsMpStatusResponse: Decoder<ConfigurationSkillsMpStatus> = (
  value,
) => decodeConfigurationSkillsMpStatus(value);

export const decodeConfigurationSecp256k1StatusResponse: Decoder<ConfigurationSecp256k1Status> = (
  value,
) => decodeConfigurationSecp256k1Status(value);

export const decodeConfigurationSecp256k1GenerateResponse: Decoder<ConfigurationSecp256k1GenerateResult> = (
  value,
) => decodeConfigurationSecp256k1GenerateResult(value);

export const decodeConfigurationLlmProbeResultResponse: Decoder<ConfigurationLlmProbeResult> = (
  value,
) => decodeConfigurationLlmProbeResult(value);

export const decodeConfigurationLlmProviderTypesResponse: Decoder<ConfigurationLlmProviderType[]> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationLlmProviderTypesResponse');
  return expectArray(
    record.providers,
    'ConfigurationLlmProviderTypesResponse.providers',
    decodeConfigurationLlmProviderType,
  );
};

export const decodeConfigurationLlmInstancesResponse: Decoder<ConfigurationLlmInstance[]> = (
  value,
) => {
  const record = expectRecord(value, 'ConfigurationLlmInstancesResponse');
  return expectArray(
    record.instances,
    'ConfigurationLlmInstancesResponse.instances',
    decodeConfigurationLlmInstance,
  );
};

export const decodeConfigurationLlmDefaultResponse: Decoder<string> = (value) => {
  const record = expectRecord(value, 'ConfigurationLlmDefaultResponse');
  return expectString(record.providerName, 'ConfigurationLlmDefaultResponse.providerName');
};

export const decodeWorkflowCapabilitiesResponse: Decoder<WorkflowCapabilities> = (
  value,
) => decodeWorkflowCapabilities(value);

export const decodePlaygroundWorkflowParseResponse: Decoder<PlaygroundWorkflowParseResult> = (
  value,
) => decodePlaygroundWorkflowParseResult(value);

export const decodePlaygroundWorkflowSaveResponse: Decoder<PlaygroundWorkflowSaveResult> = (
  value,
) => decodePlaygroundWorkflowSaveResult(value);

export const decodeWorkflowPrimitiveDescriptorsResponse: Decoder<WorkflowPrimitiveDescriptor[]> = (
  value,
) => expectArray(value, 'WorkflowPrimitiveDescriptor[]', decodeWorkflowPrimitiveDescriptor);

export const decodeWorkflowLlmStatusResponse: Decoder<WorkflowLlmStatus> = (
  value,
) => decodeWorkflowLlmStatus(value);

export const decodeWorkflowCatalogItemDetailResponse: Decoder<WorkflowCatalogItemDetail> = (
  value,
) => decodeWorkflowCatalogItemDetail(value);

export const decodeWorkflowActorSnapshotResponse: Decoder<WorkflowActorSnapshot> = (
  value,
) => decodeWorkflowActorSnapshot(value);

export const decodeWorkflowActorTimelineResponse: Decoder<WorkflowActorTimelineItem[]> = (
  value,
) => expectArray(value, 'WorkflowActorTimelineItem[]', decodeWorkflowActorTimelineItem);

export const decodeWorkflowActorGraphEnrichedResponse: Decoder<WorkflowActorGraphEnrichedSnapshot> = (
  value,
) => decodeWorkflowActorGraphEnrichedSnapshot(value);

export const decodeWorkflowActorGraphEdgesResponse: Decoder<WorkflowActorGraphEdge[]> = (
  value,
) => expectArray(value, 'WorkflowActorGraphEdge[]', decodeWorkflowActorGraphEdge);

export const decodeWorkflowActorGraphSubgraphResponse: Decoder<WorkflowActorGraphSubgraph> = (
  value,
) => decodeWorkflowActorGraphSubgraph(value);

export const decodeWorkflowResumeResponseBody: Decoder<WorkflowResumeResponse> = (
  value,
) => decodeWorkflowResumeResponse(value);

export const decodeWorkflowSignalResponseBody: Decoder<WorkflowSignalResponse> = (
  value,
) => decodeWorkflowSignalResponse(value);
