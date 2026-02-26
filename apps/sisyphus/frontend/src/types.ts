export interface SSEEvent {
  type: string
  timestamp?: number
  threadId?: string
  result?: unknown
  message?: string
  code?: string
  stepName?: string
  messageId?: string
  role?: string
  delta?: string
  snapshot?: unknown
  toolCallId?: string
  toolName?: string
  args?: string
  name?: string
  value?: unknown
}

export interface AgentMessage {
  id: string
  role: string
  content: string
  isStreaming: boolean
}

export interface ToolCall {
  id: string
  name: string
  args?: string
  result?: string
  status: 'running' | 'completed' | 'error'
  startTime: number
  endTime?: number
}

export type TimelineItem =
  | { type: 'message'; id: string }
  | { type: 'tool'; id: string }
  | { type: 'step'; name: string; role: string; iteration?: number; childId?: string }

export type RunStatus = 'idle' | 'running' | 'completed' | 'error'
