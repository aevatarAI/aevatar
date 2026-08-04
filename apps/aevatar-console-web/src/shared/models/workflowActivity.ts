export interface WorkflowActivityRunFilter {
  readonly status?: string;
  readonly origins?: readonly string[];
  readonly definitionActorIds?: readonly string[];
  readonly scheduleIds?: readonly string[];
  readonly fromUtc?: string;
  readonly toUtc?: string;
  readonly take?: number;
}

export interface WorkflowActivityRunSummary {
  readonly runId: string;
  readonly workflowName: string;
  readonly status: string;
  readonly success: boolean | null;
  readonly startedAtUtc: string | null;
  readonly updatedAtUtc: string;
  readonly stateVersion: number;
  readonly scopeId: string;
  readonly runOrigin: string;
}

export interface WorkflowActivityUsageTotals {
  readonly promptTokens: number;
  readonly completionTokens: number;
  readonly totalTokens: number;
  readonly cost: number;
}

export interface WorkflowActivityDiagnostic {
  readonly timestampUtc: string | null;
  readonly severity: string;
  readonly code: string;
  readonly source: string;
  readonly message: string;
  readonly hint: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole: string;
}

export interface WorkflowActivityToolApproval {
  readonly executionId: string;
  readonly toolName: string;
  readonly toolCallId: string;
  readonly approvalRequestId: string;
}

export interface WorkflowActivityStep {
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole: string;
  readonly requestedAtUtc: string | null;
  readonly completedAtUtc: string | null;
  readonly success: boolean | null;
  readonly durationMs: number | null;
  readonly outputPreview: string;
  readonly error: string;
  readonly requestParameters: Readonly<Record<string, string>>;
  readonly nextStepId: string;
  readonly branchKey: string;
  readonly suspensionType: string;
  readonly suspensionPrompt: string;
  readonly suspensionContent: string;
  readonly suspensionTimeoutSeconds: number | null;
  readonly toolApproval: WorkflowActivityToolApproval | null;
  readonly usage: WorkflowActivityUsageTotals;
}

export interface WorkflowActivityToolCall {
  readonly toolName: string;
  readonly callId: string;
  readonly argumentsJson: string;
  readonly resultJson: string;
  readonly success: boolean;
  readonly error: string;
}

export interface WorkflowActivityTimelineEvent {
  readonly kind: string;
  readonly timestampUtc: string;
  readonly stage: string;
  readonly message: string;
  readonly agentId: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly toolCall: WorkflowActivityToolCall | null;
  readonly content: string;
  readonly data: Readonly<Record<string, string>>;
}

export interface WorkflowActivityRunStatistics {
  readonly totalSteps: number;
  readonly requestedSteps: number;
  readonly completedSteps: number;
  readonly roleReplyCount: number;
  readonly stepTypeCounts: Readonly<Record<string, number>>;
}

export interface WorkflowActivityRunDetail {
  readonly summary: WorkflowActivityRunSummary;
  readonly input: string;
  readonly finalOutput: string;
  readonly finalError: string;
  readonly diagnostics: readonly WorkflowActivityDiagnostic[];
  readonly steps: readonly WorkflowActivityStep[];
  readonly timeline: readonly WorkflowActivityTimelineEvent[];
  readonly statistics: WorkflowActivityRunStatistics;
  readonly usageTotals: WorkflowActivityUsageTotals;
}

export interface WorkflowActivityGraphNode {
  readonly nodeId: string;
  readonly nodeType: string;
  readonly stepId: string;
}

export interface WorkflowActivityGraphEdge {
  readonly edgeId: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly edgeType: string;
  readonly branchKey: string;
}

export interface WorkflowActivityRunGraph {
  readonly rootNodeId: string;
  readonly nodes: readonly WorkflowActivityGraphNode[];
  readonly edges: readonly WorkflowActivityGraphEdge[];
}

export interface WorkflowRunForkRequest {
  readonly sourceRunId: string;
  readonly startAtStepId: string;
  readonly inlineYaml?: string;
  readonly inlineSubYamls?: Readonly<Record<string, string>>;
  readonly variableOverrides?: Readonly<Record<string, string>>;
  readonly input?: string;
  readonly commandId?: string;
  readonly correlationId?: string;
}

export interface WorkflowRunForkAcceptedReceipt {
  readonly accepted: true;
  readonly sourceRunId: string;
  readonly newRunActorId: string;
  readonly workflowName: string;
  readonly acceptedCommandId: string;
  readonly correlationId: string;
  readonly statusUrl: string;
}
