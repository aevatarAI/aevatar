import type {
  ConfigurationCollectionRawDocument,
  ConfigurationDoctorReport,
  ConfigurationEmbeddingsStatus,
  ConfigurationLlmApiKeyStatus,
  ConfigurationLlmInstance,
  ConfigurationLlmProbeResult,
  ConfigurationLlmProviderType,
  ConfigurationMcpServer,
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
} from "@/shared/models/platform/configuration";
import {
  type Decoder,
  expectArray,
  expectBoolean,
  expectNullableBoolean,
  expectNullableNumber,
  expectNumber,
  expectOptionalBoolean,
  expectOptionalNumber,
  expectOptionalString,
  expectRecord,
  expectString,
  expectStringArray,
  expectStringRecord,
} from "./decodeUtils";

function decodeConfigurationPathInfo(
  value: unknown,
  label = "ConfigurationPathInfo"
): ConfigurationPathInfo {
  const record = expectRecord(value, label);
  const homeEnvValue = expectOptionalString(
    record.homeEnvValue,
    `${label}.homeEnvValue`
  );
  const secretsPathEnvValue = expectOptionalString(
    record.secretsPathEnvValue,
    `${label}.secretsPathEnvValue`
  );
  return {
    root: expectString(record.root, `${label}.root`),
    secretsJson: expectString(record.secretsJson, `${label}.secretsJson`),
    configJson: expectString(record.configJson, `${label}.configJson`),
    connectorsJson: expectString(
      record.connectorsJson,
      `${label}.connectorsJson`
    ),
    mcpJson: expectString(record.mcpJson, `${label}.mcpJson`),
    workflowsHome: expectString(record.workflowsHome, `${label}.workflowsHome`),
    workflowsRepo: expectString(record.workflowsRepo, `${label}.workflowsRepo`),
    homeEnvValue: homeEnvValue ?? null,
    secretsPathEnvValue: secretsPathEnvValue ?? null,
  };
}

function decodeConfigurationPathStatus(
  value: unknown,
  label = "ConfigurationPathStatus"
): ConfigurationPathStatus {
  const record = expectRecord(value, label);
  const sizeBytes = expectOptionalNumber(
    record.sizeBytes,
    `${label}.sizeBytes`
  );
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
  label = "ConfigurationDoctorReport"
): ConfigurationDoctorReport {
  const record = expectRecord(value, label);
  return {
    paths: decodeConfigurationPathInfo(record.paths, `${label}.paths`),
    secrets: decodeConfigurationPathStatus(record.secrets, `${label}.secrets`),
    config: decodeConfigurationPathStatus(record.config, `${label}.config`),
    connectors: decodeConfigurationPathStatus(
      record.connectors,
      `${label}.connectors`
    ),
    mcp: decodeConfigurationPathStatus(record.mcp, `${label}.mcp`),
    workflowsHome: decodeConfigurationPathStatus(
      record.workflowsHome,
      `${label}.workflowsHome`
    ),
    workflowsRepo: decodeConfigurationPathStatus(
      record.workflowsRepo,
      `${label}.workflowsRepo`
    ),
  };
}

function decodeConfigurationSourceStatus(
  value: unknown,
  label = "ConfigurationSourceStatus"
): ConfigurationSourceStatus {
  const record = expectRecord(value, label);
  return {
    mode: expectString(record.mode, `${label}.mode`),
    mongoConfigured: expectBoolean(
      record.mongoConfigured,
      `${label}.mongoConfigured`
    ),
    fileConfigured: expectBoolean(
      record.fileConfigured,
      `${label}.fileConfigured`
    ),
    localRuntimeAccess: expectBoolean(
      record.localRuntimeAccess,
      `${label}.localRuntimeAccess`
    ),
    paths: decodeConfigurationPathInfo(record.paths, `${label}.paths`),
    doctor: decodeConfigurationDoctorReport(record.doctor, `${label}.doctor`),
  };
}

function decodeConfigurationWorkflowFile(
  value: unknown,
  label = "ConfigurationWorkflowFile"
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
  label = "ConfigurationWorkflowFileDetail"
): ConfigurationWorkflowFileDetail {
  const record = expectRecord(value, label);
  return {
    ...decodeConfigurationWorkflowFile(value, label),
    content: expectString(record.content, `${label}.content`),
  };
}

function decodeConfigurationRawDocument(
  value: unknown,
  label = "ConfigurationRawDocument"
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
  label = "ConfigurationCollectionRawDocument"
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
  label = "ConfigurationValidationResult"
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
  label = "ConfigurationMcpServer"
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
  label = "ConfigurationLlmApiKeyStatus"
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
  label = "ConfigurationSecretValueStatus"
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
  label = "ConfigurationEmbeddingsStatus"
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
  label = "ConfigurationWebSearchStatus"
): ConfigurationWebSearchStatus {
  const record = expectRecord(value, label);
  return {
    enabled: expectNullableBoolean(record.enabled, `${label}.enabled`),
    effectiveEnabled: expectBoolean(
      record.effectiveEnabled,
      `${label}.effectiveEnabled`
    ),
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
  label = "ConfigurationSkillsMpStatus"
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
  label = "ConfigurationSecp256k1PrivateKeyStatus"
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
  label = "ConfigurationSecp256k1PublicKeyStatus"
): ConfigurationSecp256k1PublicKeyStatus {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    hex: expectString(record.hex, `${label}.hex`),
  };
}

function decodeConfigurationSecp256k1Status(
  value: unknown,
  label = "ConfigurationSecp256k1Status"
): ConfigurationSecp256k1Status {
  const record = expectRecord(value, label);
  return {
    configured: expectBoolean(record.configured, `${label}.configured`),
    privateKey: decodeConfigurationSecp256k1PrivateKeyStatus(
      record.privateKey,
      `${label}.privateKey`
    ),
    publicKey: decodeConfigurationSecp256k1PublicKeyStatus(
      record.publicKey,
      `${label}.publicKey`
    ),
  };
}

function decodeConfigurationSecp256k1GenerateResult(
  value: unknown,
  label = "ConfigurationSecp256k1GenerateResult"
): ConfigurationSecp256k1GenerateResult {
  const record = expectRecord(value, label);
  return {
    backedUp: expectBoolean(record.backedUp, `${label}.backedUp`),
    publicKeyHex: expectString(record.publicKeyHex, `${label}.publicKeyHex`),
    status: decodeConfigurationSecp256k1Status(
      record.status,
      `${label}.status`
    ),
  };
}

function decodeConfigurationLlmProbeResult(
  value: unknown,
  label = "ConfigurationLlmProbeResult"
): ConfigurationLlmProbeResult {
  const record = expectRecord(value, label);
  return {
    ok: expectBoolean(record.ok, `${label}.ok`),
    providerName: expectString(record.providerName, `${label}.providerName`),
    kind: expectString(record.kind, `${label}.kind`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
    latencyMs: expectOptionalNumber(record.latencyMs, `${label}.latencyMs`),
    error: expectOptionalString(record.error, `${label}.error`),
    modelsCount: expectOptionalNumber(
      record.modelsCount,
      `${label}.modelsCount`
    ),
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
  label = "ConfigurationLlmProviderType"
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
      `${label}.configuredInstancesCount`
    ),
  };
}

function decodeConfigurationLlmInstance(
  value: unknown,
  label = "ConfigurationLlmInstance"
): ConfigurationLlmInstance {
  const record = expectRecord(value, label);
  return {
    name: expectString(record.name, `${label}.name`),
    providerType: expectString(record.providerType, `${label}.providerType`),
    providerDisplayName: expectString(
      record.providerDisplayName,
      `${label}.providerDisplayName`
    ),
    model: expectString(record.model, `${label}.model`),
    endpoint: expectString(record.endpoint, `${label}.endpoint`),
  };
}

export const decodeConfigurationSourceStatusResponse: Decoder<
  ConfigurationSourceStatus
> = (value) => decodeConfigurationSourceStatus(value);

export const decodeConfigurationWorkflowFilesResponse: Decoder<
  ConfigurationWorkflowFile[]
> = (value) => {
  const record = expectRecord(value, "ConfigurationWorkflowFilesResponse");
  return expectArray(
    record.workflows,
    "ConfigurationWorkflowFilesResponse.workflows",
    decodeConfigurationWorkflowFile
  );
};

export const decodeConfigurationWorkflowFileDetailResponse: Decoder<
  ConfigurationWorkflowFileDetail
> = (value) => {
  const record = expectRecord(value, "ConfigurationWorkflowFileDetailResponse");
  return decodeConfigurationWorkflowFileDetail(
    record.workflow,
    "ConfigurationWorkflowFileDetailResponse.workflow"
  );
};

export const decodeConfigurationWorkflowFileMutationResponse: Decoder<
  ConfigurationWorkflowFile
> = (value) => {
  const record = expectRecord(
    value,
    "ConfigurationWorkflowFileMutationResponse"
  );
  return decodeConfigurationWorkflowFile(
    record.workflow,
    "ConfigurationWorkflowFileMutationResponse.workflow"
  );
};

export const decodeConfigurationRawDocumentResponse: Decoder<
  ConfigurationRawDocument
> = (value) => decodeConfigurationRawDocument(value);

export const decodeConfigurationCollectionRawDocumentResponse: Decoder<
  ConfigurationCollectionRawDocument
> = (value) => decodeConfigurationCollectionRawDocument(value);

export const decodeConfigurationValidationResultResponse: Decoder<
  ConfigurationValidationResult
> = (value) => decodeConfigurationValidationResult(value);

export const decodeConfigurationMcpServersResponse: Decoder<
  ConfigurationMcpServer[]
> = (value) => {
  const record = expectRecord(value, "ConfigurationMcpServersResponse");
  return expectArray(
    record.servers,
    "ConfigurationMcpServersResponse.servers",
    decodeConfigurationMcpServer
  );
};

export const decodeConfigurationMcpServerMutationResponse: Decoder<
  ConfigurationMcpServer
> = (value) => {
  const record = expectRecord(value, "ConfigurationMcpServerMutationResponse");
  return decodeConfigurationMcpServer(
    record.server,
    "ConfigurationMcpServerMutationResponse.server"
  );
};

export const decodeConfigurationLlmApiKeyStatusResponse: Decoder<
  ConfigurationLlmApiKeyStatus
> = (value) => decodeConfigurationLlmApiKeyStatus(value);

export const decodeConfigurationSecretValueStatusResponse: Decoder<
  ConfigurationSecretValueStatus
> = (value) => decodeConfigurationSecretValueStatus(value);

export const decodeConfigurationEmbeddingsStatusResponse: Decoder<
  ConfigurationEmbeddingsStatus
> = (value) => {
  const record = expectRecord(value, "ConfigurationEmbeddingsStatusResponse");
  return decodeConfigurationEmbeddingsStatus(
    record.embeddings,
    "ConfigurationEmbeddingsStatusResponse.embeddings"
  );
};

export const decodeConfigurationWebSearchStatusResponse: Decoder<
  ConfigurationWebSearchStatus
> = (value) => {
  const record = expectRecord(value, "ConfigurationWebSearchStatusResponse");
  return decodeConfigurationWebSearchStatus(
    record.webSearch,
    "ConfigurationWebSearchStatusResponse.webSearch"
  );
};

export const decodeConfigurationSkillsMpStatusResponse: Decoder<
  ConfigurationSkillsMpStatus
> = (value) => decodeConfigurationSkillsMpStatus(value);

export const decodeConfigurationSecp256k1StatusResponse: Decoder<
  ConfigurationSecp256k1Status
> = (value) => decodeConfigurationSecp256k1Status(value);

export const decodeConfigurationSecp256k1GenerateResponse: Decoder<
  ConfigurationSecp256k1GenerateResult
> = (value) => decodeConfigurationSecp256k1GenerateResult(value);

export const decodeConfigurationLlmProbeResultResponse: Decoder<
  ConfigurationLlmProbeResult
> = (value) => decodeConfigurationLlmProbeResult(value);

export const decodeConfigurationLlmProviderTypesResponse: Decoder<
  ConfigurationLlmProviderType[]
> = (value) => {
  const record = expectRecord(value, "ConfigurationLlmProviderTypesResponse");
  return expectArray(
    record.providers,
    "ConfigurationLlmProviderTypesResponse.providers",
    decodeConfigurationLlmProviderType
  );
};

export const decodeConfigurationLlmInstancesResponse: Decoder<
  ConfigurationLlmInstance[]
> = (value) => {
  const record = expectRecord(value, "ConfigurationLlmInstancesResponse");
  return expectArray(
    record.instances,
    "ConfigurationLlmInstancesResponse.instances",
    decodeConfigurationLlmInstance
  );
};

export const decodeConfigurationLlmDefaultResponse: Decoder<string> = (
  value
) => {
  const record = expectRecord(value, "ConfigurationLlmDefaultResponse");
  return expectString(
    record.providerName,
    "ConfigurationLlmDefaultResponse.providerName"
  );
};
