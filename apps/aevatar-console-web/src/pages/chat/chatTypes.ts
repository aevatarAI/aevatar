import type {
  RuntimeEvent,
  RuntimeStepInfo,
  RuntimeToolCallInfo,
} from "@/shared/agui/runtimeEventSemantics";

export type { RuntimeEvent };

export type ChatMessage = {
  id: string;
  role: "user" | "assistant";
  content: string;
  timestamp: number;
  status: "complete" | "streaming" | "error";
  error?: string;
  events?: RuntimeEvent[];
  steps?: StepInfo[];
  thinking?: string;
  toolCalls?: ToolCallInfo[];
};

export type StepInfo = RuntimeStepInfo;

export type ToolCallInfo = RuntimeToolCallInfo;

export type ServiceEndpoint = {
  endpointId: string;
  displayName: string;
  kind: string;
  description?: string;
  requestTypeUrl?: string;
  responseTypeUrl?: string;
};

export type ServiceOption = {
  id: string;
  label: string;
  kind: "nyxid-chat" | "service";
  endpoints: ServiceEndpoint[];
  deploymentStatus?: string;
  primaryActorId?: string;
};

export type ConversationMeta = {
  id: string;
  actorId?: string;
  commandId?: string;
  runId?: string;
  title: string;
  serviceId: string;
  serviceKind: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
};

export type StoredChatMessage = {
  id: string;
  role: "user" | "assistant";
  content: string;
  timestamp: number;
  status: "complete" | "error";
  error?: string;
  events?: RuntimeEvent[];
  steps?: StepInfo[];
  thinking?: string;
  toolCalls?: ToolCallInfo[];
};

export type ChatSessionState = {
  scopeId: string;
  serviceId: string;
  endpointId: string;
  actorId: string;
  commandId: string;
  runId: string;
  eventCount: number;
  status: "idle" | "running" | "success" | "error";
  error?: string;
  updatedAt?: number;
};
