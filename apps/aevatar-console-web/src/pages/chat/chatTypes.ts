import type {
  RuntimeEvent,
  RuntimeRunInterventionInfo,
  RuntimeStepInfo,
  RuntimeToolApprovalRequestInfo,
  RuntimeToolCallInfo,
} from "@/shared/agui/runtimeEventSemantics";

export type { RuntimeEvent };

type ExtensibleString<T extends string> = T | (string & Record<never, never>);

export type ChatMessageRole = ExtensibleString<"user" | "assistant">;

export type ChatMessageStatus = ExtensibleString<
  "complete" | "streaming" | "error"
>;

export type StoredChatMessageStatus = ExtensibleString<"complete" | "error">;

export type ChatMessage = {
  id: string;
  role: ChatMessageRole;
  content: string;
  timestamp: number;
  status: ChatMessageStatus;
  authorId?: string | null;
  authorName?: string | null;
  error?: string | null;
  events?: RuntimeEvent[];
  pendingApproval?: PendingApprovalInfo;
  pendingRunIntervention?: PendingRunInterventionInfo;
  steps?: StepInfo[];
  thinking?: string | null;
  toolCalls?: ToolCallInfo[];
};

export type ChatUsageSummary = {
  completionTokens?: number;
  cost?: number;
  latencyMs?: number;
  model?: string;
  promptTokens?: number;
  totalTokens?: number;
};

export type ChatStudioTarget = {
  memberId?: string;
  runId?: string;
  scopeId?: string;
  studioUrl?: string;
  teamId?: string;
  workflowId?: string;
};

export type LocalChatStatus =
  | "draft"
  | "streaming"
  | "needs_confirmation"
  | "creating"
  | "completed_text"
  | "completed_with_studio_target"
  | "error";

export type StepInfo = RuntimeStepInfo;

export type ToolCallInfo = RuntimeToolCallInfo;

export type PendingApprovalInfo = RuntimeToolApprovalRequestInfo;

export type PendingRunInterventionInfo = RuntimeRunInterventionInfo;

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

export type ConversationRuntimeIdentity = {
  actorId?: string;
  commandId?: string;
  runId?: string;
};

export type ConversationLlmPreferences = {
  llmModel?: string;
  llmRoute?: string;
};

export type ConversationSessionSnapshot = {
  preferences?: ConversationLlmPreferences;
  runtime?: ConversationRuntimeIdentity;
};

export type ChatHistoryContext = {
  scopeId: string;
  conversationId: string;
  stateVersion: number;
  turnId: string;
};

export type ConversationMeta = {
  id: string;
  llmModel?: string | null;
  llmRoute?: string | null;
  title: string;
  serviceId?: string;
  serviceKind?: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
};

export type ConversationSessionMeta = ConversationMeta & {
  actorId?: string;
  commandId?: string;
  runId?: string;
  session?: ConversationSessionSnapshot;
};

export type StoredChatMessage = {
  id: string;
  turnId?: string | null;
  role: ChatMessageRole;
  content: string;
  timestamp: number;
  status: StoredChatMessageStatus;
  error?: string | null;
  authorId?: string | null;
  authorName?: string | null;
  thinking?: string | null;
};

export type ChatConversationDetail = {
  messages: StoredChatMessage[];
  stateVersion: number;
};

export type ChatHistoryIndex = {
  conversations: ConversationMeta[];
  nextCursor?: string | null;
};

export type ChatCreateRecovery = {
  conversationId: string;
  stateVersion: number;
  status: string;
  turnId: string;
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
