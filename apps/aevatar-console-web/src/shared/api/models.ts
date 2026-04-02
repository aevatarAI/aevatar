export interface WorkflowCatalogItem {
  name: string;
  description: string;
  category: string;
  group: string;
  groupLabel: string;
  sortOrder: number;
  source: string;
  sourceLabel: string;
  showInLibrary: boolean;
  isPrimitiveExample: boolean;
  requiresLlmProvider: boolean;
  primitives: string[];
}

export interface WorkflowCatalogRole {
  id: string;
  name: string;
  systemPrompt: string;
  provider: string;
  model: string;
  temperature: number | null;
  maxTokens: number | null;
  maxToolRounds: number | null;
  maxHistoryMessages: number | null;
  streamBufferCapacity: number | null;
  eventModules: string[];
  eventRoutes: string;
  connectors: string[];
}

export interface WorkflowCatalogChildStep {
  id: string;
  type: string;
  targetRole: string;
}

export interface WorkflowCatalogStep {
  id: string;
  type: string;
  targetRole: string;
  parameters: Record<string, string>;
  next: string;
  branches: Record<string, string>;
  children: WorkflowCatalogChildStep[];
}

export interface WorkflowCatalogEdge {
  from: string;
  to: string;
  label: string;
}

export interface WorkflowCatalogDefinition {
  name: string;
  description: string;
  closedWorldMode: boolean;
  roles: WorkflowCatalogRole[];
  steps: WorkflowCatalogStep[];
}

export interface WorkflowCatalogItemDetail {
  catalog: WorkflowCatalogItem;
  yaml: string;
  definition: WorkflowCatalogDefinition;
  edges: WorkflowCatalogEdge[];
}

export interface WorkflowAgentSummary {
  id: string;
  type: string;
  description: string;
}

export interface WorkflowCapabilityParameter {
  name: string;
  type: string;
  required: boolean;
  description: string;
  default: string;
  enum: string[];
}

export interface WorkflowPrimitiveCapability {
  name: string;
  aliases: string[];
  category: string;
  description: string;
  closedWorldBlocked: boolean;
  runtimeModule: string;
  parameters: WorkflowCapabilityParameter[];
}

export interface WorkflowConnectorCapability {
  name: string;
  type: string;
  enabled: boolean;
  timeoutMs: number;
  retry: number;
  allowedInputKeys: string[];
  allowedOperations: string[];
  fixedArguments: string[];
}

export interface WorkflowCapabilityWorkflowStep {
  id: string;
  type: string;
  next: string;
}

export interface WorkflowCapabilityWorkflow {
  name: string;
  description: string;
  source: string;
  closedWorldMode: boolean;
  requiresLlmProvider: boolean;
  primitives: string[];
  requiredConnectors: string[];
  workflowCalls: string[];
  steps: WorkflowCapabilityWorkflowStep[];
}

export interface WorkflowCapabilities {
  schemaVersion: string;
  generatedAtUtc: string;
  primitives: WorkflowPrimitiveCapability[];
  connectors: WorkflowConnectorCapability[];
  workflows: WorkflowCapabilityWorkflow[];
}

export interface WorkflowAuthoringRetryPolicy {
  maxAttempts: number;
  backoff: string;
  delayMs: number;
}

export interface WorkflowAuthoringErrorPolicy {
  strategy: string;
  fallbackStep: string | null;
  defaultOutput: string | null;
}

export interface WorkflowAuthoringRole {
  id: string;
  name: string;
  systemPrompt: string;
  provider: string | null;
  model: string | null;
  temperature: number | null;
  maxTokens: number | null;
  maxToolRounds: number | null;
  maxHistoryMessages: number | null;
  streamBufferCapacity: number | null;
  eventModules: string[];
  eventRoutes: string;
  connectors: string[];
}

export interface WorkflowAuthoringStep {
  id: string;
  type: string;
  targetRole: string;
  parameters: Record<string, string>;
  next: string | null;
  branches: Record<string, string>;
  children: WorkflowAuthoringStep[];
  retry: WorkflowAuthoringRetryPolicy | null;
  onError: WorkflowAuthoringErrorPolicy | null;
  timeoutMs: number | null;
}

export interface WorkflowAuthoringDefinition {
  name: string;
  description: string;
  closedWorldMode: boolean;
  roles: WorkflowAuthoringRole[];
  steps: WorkflowAuthoringStep[];
}

export interface WorkflowAuthoringEdge {
  from: string;
  to: string;
  label: string;
}

export interface PlaygroundWorkflowParseResult {
  valid: boolean;
  error: string | null;
  errors: string[];
  definition: WorkflowAuthoringDefinition | null;
  edges: WorkflowAuthoringEdge[];
}

export interface PlaygroundWorkflowSaveResult {
  saved: boolean;
  filename: string;
  savedPath: string;
  workflowName: string;
  overwritten: boolean;
  savedSource: string;
  effectiveSource: string;
  effectivePath: string;
}

export interface WorkflowPrimitiveParameterDescriptor {
  name: string;
  type: string;
  required: boolean;
  description: string;
  default: string;
  enumValues: string[];
}

export interface WorkflowPrimitiveDescriptor {
  name: string;
  aliases: string[];
  category: string;
  description: string;
  parameters: WorkflowPrimitiveParameterDescriptor[];
  exampleWorkflows: string[];
}

export interface WorkflowLlmStatus {
  available: boolean;
  provider: string | null;
  model: string | null;
  providers: string[];
}

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

export interface ConfigurationWorkflowFileDetail extends ConfigurationWorkflowFile {
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

export interface WorkflowActorSnapshot {
  actorId: string;
  workflowName: string;
  lastCommandId: string;
  stateVersion: number;
  lastEventId: string;
  lastUpdatedAt: string;
  lastSuccess: boolean | null;
  lastOutput: string;
  lastError: string;
  totalSteps: number;
  requestedSteps: number;
  completedSteps: number;
  roleReplyCount: number;
}

export interface WorkflowActorTimelineItem {
  timestamp: string;
  stage: string;
  message: string;
  agentId: string;
  stepId: string;
  stepType: string;
  eventType: string;
  data: Record<string, string>;
}

export interface WorkflowActorGraphNode {
  nodeId: string;
  nodeType: string;
  updatedAt: string;
  properties: Record<string, string>;
}

export interface WorkflowActorGraphEdge {
  edgeId: string;
  fromNodeId: string;
  toNodeId: string;
  edgeType: string;
  updatedAt: string;
  properties: Record<string, string>;
}

export interface WorkflowActorGraphSubgraph {
  rootNodeId: string;
  nodes: WorkflowActorGraphNode[];
  edges: WorkflowActorGraphEdge[];
}
