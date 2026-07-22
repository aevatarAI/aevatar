import type {
  RuntimeEvent,
  RuntimeRunInterventionInfo,
  RuntimeStepInfo,
  RuntimeToolApprovalRequestInfo,
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
  pendingApproval?: PendingApprovalInfo;
  pendingRunIntervention?: PendingRunInterventionInfo;
  steps?: StepInfo[];
  thinking?: string;
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

export type ChatContext = {
  conversationId: string;
  scopeId: string;
  stateVersion: number;
  turnId: string;
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
  kind: "nyxid-chat" | "onboarding" | "service";
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

export type ConversationMeta = {
  id: string;
  actorId?: string;
  commandId?: string;
  runId?: string;
  llmModel?: string;
  llmRoute?: string;
  session?: ConversationSessionSnapshot;
  title: string;
  serviceId: string;
  serviceKind: string;
  pendingReadModelStateVersionFloor?: number;
  serverConversationId?: string;
  scopeId?: string;
  stateVersion?: number;
  status?: LocalChatStatus;
  target?: ChatStudioTarget;
  usage?: ChatUsageSummary;
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
  pendingApproval?: PendingApprovalInfo;
  pendingRunIntervention?: PendingRunInterventionInfo;
  steps?: StepInfo[];
  thinking?: string;
  toolCalls?: ToolCallInfo[];
};

export type LocalChatConversation = {
  createdAt: string;
  id: string;
  messages: StoredChatMessage[];
  pendingReadModelStateVersionFloor?: number;
  scopeId: string;
  serverConversationId?: string;
  status: LocalChatStatus;
  stateVersion?: number;
  target?: ChatStudioTarget;
  title: string;
  updatedAt: string;
  usage?: ChatUsageSummary;
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
