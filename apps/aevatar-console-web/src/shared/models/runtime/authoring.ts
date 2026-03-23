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
