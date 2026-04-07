import type { RuntimeEvent } from './sseUtils';

export type ContentPartDto = {
  type: 'text' | 'image' | 'audio' | 'video';
  text?: string;
  dataBase64?: string;
  mediaType?: string;
  uri?: string;
  name?: string;
};

export type AttachmentInfo = {
  id: string;
  name: string;
  mediaType: string;
  size: number;
  storageKey: string;
  previewUrl?: string;
  /** Transient File reference for upload — not serialized to history */
  file?: File;
};

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
  /** Pending tool approval request from the agent */
  pendingApproval?: PendingApprovalInfo;
  /** Pending human_input request from a workflow */
  pendingHumanInput?: PendingHumanInputInfo;
  /** User-uploaded file attachments */
  attachments?: AttachmentInfo[];
  /** LLM-generated media parts from MEDIA_CONTENT events */
  mediaParts?: ContentPartDto[];
};

export type PendingApprovalInfo = {
  requestId: string;
  toolName: string;
  toolCallId: string;
  argumentsJson: string;
  isDestructive: boolean;
  timeoutSeconds: number;
};

export type PendingHumanInputInfo = {
  stepId: string;
  runId: string;
  prompt: string;
  serviceId?: string;
  actorId?: string;
  /** Structured options from backend (if provided) */
  options?: string[];
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
  kind: 'nyxid-chat' | 'onboarding' | 'streaming-proxy' | 'service';
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
  llmRoute?: string;
  llmModel?: string;
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
  attachments?: AttachmentInfo[];
  mediaParts?: ContentPartDto[];
};
