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
