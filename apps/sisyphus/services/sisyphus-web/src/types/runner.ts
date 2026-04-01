export interface TriggerHistoryItem {
  id: string
  workflowName: string
  triggeredBy: string
  triggeredAt: string
  status: 'running' | 'completed' | 'failed' | 'stopped'
  durationMs?: number
  error?: string
}

export interface RunDetailEvent {
  type: string
  step_name?: string
  data?: Record<string, unknown>
  error?: string
  timestamp: string
}

export interface RunDetail {
  id: string
  workflowName: string
  triggeredBy: string
  triggeredAt: string
  status: 'running' | 'completed' | 'failed' | 'stopped'
  completedAt?: string
  durationMs?: number
  error?: string
  events: RunDetailEvent[]
}

export interface UploadHistoryItem {
  id: string
  uploadedBy: string
  uploadedAt: string
  nodesWritten: number
  edgesWritten: number
}

export interface UploadDetail extends UploadHistoryItem {
  nodeIds: string[]
  edgeIds: string[]
}
