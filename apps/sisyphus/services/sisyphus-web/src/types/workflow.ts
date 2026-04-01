export type DeploymentStatus = 'draft' | 'compiled' | 'deployed' | 'out_of_sync'

export interface DeploymentState {
  status: DeploymentStatus
  contentHash?: string
  lastCompiledAt?: string
  lastDeployedAt?: string
  deployError?: string
}

export interface WorkflowDefinition {
  id: string
  name: string
  description?: string
  yaml?: string
  roles?: Array<{ name: string; description?: string; skillId?: string }>
  steps?: Array<{ name: string; type: string; order: number; roleRef?: string; connectorRef?: string; parameters?: Record<string, unknown> }>
  parameters?: Record<string, unknown>
  deploymentState?: DeploymentState
  createdAt: string
  updatedAt: string
}

export interface WorkflowListItem {
  id: string
  name: string
  description?: string
  deploymentState?: DeploymentState
  createdAt: string
  updatedAt: string
}

export interface ConnectorDefinition {
  id: string
  name: string
  type: string
  config?: Record<string, unknown>
  [key: string]: unknown
}

export interface WorkflowRun {
  id: string
  workflowId: string
  workflowName: string
  status: 'running' | 'completed' | 'failed' | 'stopped'
  startedAt: string
  completedAt?: string
  triggeredBy: string
  durationMs?: number
  error?: string
}

export interface WorkflowRunDetail extends WorkflowRun {
  events: WorkflowRunEvent[]
}

export interface WorkflowRunEvent {
  type: string
  step_name?: string
  data?: Record<string, unknown>
  error?: string
  timestamp: string
}

/** Parsed YAML workflow step for visualization */
export interface WorkflowStep {
  name: string
  type: string
  connector?: string
  skill_id?: string
  next?: string
  branches?: string[]
  children?: string[]
}

/** Parsed YAML workflow role */
export interface WorkflowRole {
  name: string
  skill_id?: string
  steps: WorkflowStep[]
}
