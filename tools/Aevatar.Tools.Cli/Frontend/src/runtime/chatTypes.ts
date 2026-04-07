import type { RuntimeEvent } from './sseUtils';

export type ChatMessage = {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  authorId?: string;
  authorName?: string;
  timestamp: number;
  status: 'complete' | 'streaming' | 'error';
  error?: string;
  events?: RuntimeEvent[];
  /** Accumulated step events for display inside assistant bubbles */
  steps?: StepInfo[];
  /** LLM reasoning/thinking text */
  thinking?: string;
  /** Tool calls */
  toolCalls?: ToolCallInfo[];
};

export type StepInfo = {
  name: string;
  status: 'running' | 'done';
  startedAt: number;
  finishedAt?: number;
  output?: string;
};

export type ToolCallInfo = {
  id: string;
  name: string;
  status: 'running' | 'done';
  result?: string;
};

export type ServiceEndpoint = {
  endpointId: string;
  displayName: string;
  kind: string;
};

export type ServiceOption = {
  id: string;
  label: string;
  kind: 'nyxid-chat' | 'streaming-proxy' | 'service';
  endpoints: ServiceEndpoint[];
};

/* ─── Chat History Persistence Types ─── */

export type ConversationMeta = {
  id: string;
  actorId?: string;
  title: string;
  serviceId: string;
  serviceKind: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
};

export type StoredChatMessage = {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  authorId?: string;
  authorName?: string;
  timestamp: number;
  status: 'complete' | 'error';
  error?: string;
  thinking?: string;
};
