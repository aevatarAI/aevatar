export interface ConfigurationPathInfo {
  root: string;
  secretsJson: string;
  configJson: string;
  connectorsJson: string;
  mcpJson: string;
  workflowsHome: string;
  workflowsRepo: string;
  homeEnvValue: string | null;
  secretsPathEnvValue: string | null;
}

export interface ConfigurationPathStatus {
  path: string;
  exists: boolean;
  readable: boolean;
  writable: boolean;
  sizeBytes: number | null;
  error: string | null;
}

export interface ConfigurationDoctorReport {
  paths: ConfigurationPathInfo;
  secrets: ConfigurationPathStatus;
  config: ConfigurationPathStatus;
  connectors: ConfigurationPathStatus;
  mcp: ConfigurationPathStatus;
  workflowsHome: ConfigurationPathStatus;
  workflowsRepo: ConfigurationPathStatus;
}

export interface ConfigurationSourceStatus {
  mode: string;
  mongoConfigured: boolean;
  fileConfigured: boolean;
  localRuntimeAccess: boolean;
  paths: ConfigurationPathInfo;
  doctor: ConfigurationDoctorReport;
}

export interface ConfigurationWorkflowFile {
  filename: string;
  source: string;
  path: string;
  sizeBytes: number;
  lastModified: string;
}

export interface ConfigurationWorkflowFileDetail
  extends ConfigurationWorkflowFile {
  content: string;
}

export interface ConfigurationRawDocument {
  json: string;
  keyCount: number;
  exists?: boolean;
  path?: string;
}

export interface ConfigurationCollectionRawDocument {
  json: string;
  count: number;
  exists?: boolean;
  path?: string;
}

export interface ConfigurationValidationResult {
  valid: boolean;
  message: string;
  count: number;
}

export interface ConfigurationMcpServer {
  name: string;
  command: string;
  args: string[];
  env: Record<string, string>;
  timeoutMs: number;
}

export interface ConfigurationLlmApiKeyStatus {
  providerName: string;
  configured: boolean;
  masked: string;
  value?: string;
}

export interface ConfigurationSecretValueStatus {
  configured: boolean;
  masked: string;
  keyPath: string;
  value?: string;
}

export interface ConfigurationEmbeddingsStatus {
  enabled: boolean | null;
  providerType: string;
  endpoint: string;
  model: string;
  configured: boolean;
  masked: string;
}

export interface ConfigurationWebSearchStatus {
  enabled: boolean | null;
  effectiveEnabled: boolean;
  provider: string;
  endpoint: string;
  timeoutMs: number | null;
  searchDepth: string;
  configured: boolean;
  masked: string;
  available: boolean;
}

export interface ConfigurationSkillsMpStatus {
  configured: boolean;
  masked: string;
  keyPath: string;
  baseUrl: string;
}

export interface ConfigurationSecp256k1PrivateKeyStatus {
  configured: boolean;
  masked: string;
  keyPath: string;
  backupsPrefix: string;
  backupCount: number;
}

export interface ConfigurationSecp256k1PublicKeyStatus {
  configured: boolean;
  hex: string;
}

export interface ConfigurationSecp256k1Status {
  configured: boolean;
  privateKey: ConfigurationSecp256k1PrivateKeyStatus;
  publicKey: ConfigurationSecp256k1PublicKeyStatus;
}

export interface ConfigurationSecp256k1GenerateResult {
  backedUp: boolean;
  publicKeyHex: string;
  status: ConfigurationSecp256k1Status;
}

export interface ConfigurationLlmProbeResult {
  ok: boolean;
  providerName: string;
  kind: string;
  endpoint: string;
  latencyMs?: number;
  error?: string;
  modelsCount?: number;
  sampleModels?: string[];
  models?: string[];
}

export interface ConfigurationLlmProviderType {
  id: string;
  displayName: string;
  category: string;
  description: string;
  recommended: boolean;
  configuredInstancesCount: number;
}

export interface ConfigurationLlmInstance {
  name: string;
  providerType: string;
  providerDisplayName: string;
  model: string;
  endpoint: string;
}
