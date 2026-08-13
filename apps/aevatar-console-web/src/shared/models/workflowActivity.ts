export interface WorkflowActivityRunFilter {
  readonly status?: string;
  readonly origins?: readonly string[];
  readonly definitionActorIds?: readonly string[];
  readonly scheduleIds?: readonly string[];
  readonly fromUtc?: string;
  readonly toUtc?: string;
  readonly take?: number;
}

export interface WorkflowActivityRunFeedFilter
  extends WorkflowActivityRunFilter {
  readonly workflowId?: string;
  readonly searchText?: string;
  readonly cursor?: string;
  readonly includeTotalCount?: boolean;
}

export type WorkflowRecoveryEligibility = 0 | 1 | 2 | 3;
export type WorkflowRecoveryUnavailableReasonCode =
  | 0
  | 1
  | 2
  | 3
  | 4
  | 5
  | 6
  | 7;
export type WorkflowRecoveryRecommendedAction = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;
export type WorkflowRunLineageAvailability = 0 | 1 | 2 | 3;
export type WorkflowRunLineageRelationKind = 0 | 1 | 2;

export interface WorkflowRecoveryActionCapability {
  readonly eligibility: WorkflowRecoveryEligibility;
  readonly unavailableReasonCode: WorkflowRecoveryUnavailableReasonCode;
  readonly unavailableReason: string;
  readonly recommendedActions: readonly WorkflowRecoveryRecommendedAction[];
  readonly startingStepId: string;
  readonly reusesPriorStepOutputs: boolean;
  readonly mayIncurModelOrToolCost: boolean;
}

export interface WorkflowRunRecoveryCapability {
  readonly retryFailedStep: WorkflowRecoveryActionCapability;
  readonly runAgain: WorkflowRecoveryActionCapability;
  readonly workflowDefinitionRevisionId: string;
  readonly workflowDefinitionVersion: number;
}

export interface WorkflowRunLineageRunRef {
  readonly runId: string;
  readonly actorId: string;
  readonly relationshipId: string;
  readonly stepId: string;
  readonly attempt: number;
  readonly relationKind: WorkflowRunLineageRelationKind;
}

export interface WorkflowRunRetryForkLineage {
  readonly availability: WorkflowRunLineageAvailability;
  readonly sourceRunId: string;
  readonly originalRunId: string;
  readonly attempt: number;
  readonly startAtStepId: string;
  readonly childRuns: readonly WorkflowRunLineageRunRef[];
}

export interface WorkflowRunSubWorkflowLineage {
  readonly availability: WorkflowRunLineageAvailability;
  readonly parentRunId: string;
  readonly parentActorId: string;
  readonly parentStepId: string;
  readonly rootRunId: string;
  readonly depth: number;
  readonly childRuns: readonly WorkflowRunLineageRunRef[];
}

export interface WorkflowRunLineage {
  readonly availability: WorkflowRunLineageAvailability;
  readonly retryFork: WorkflowRunRetryForkLineage;
  readonly subWorkflow: WorkflowRunSubWorkflowLineage;
  readonly unavailableReason: string;
}

export interface WorkflowActivityRunInitiator {
  readonly platform: string;
  readonly tenant: string;
  readonly externalUserId: string;
  readonly scope: string;
  readonly bindingId: string;
  readonly displayValue: string;
  readonly availability: string;
}

export interface WorkflowActivityRunCurrentStep {
  readonly stepId: string;
  readonly inputSummary: string;
  readonly availability: string;
}

export interface WorkflowActivityRunFirstFailure {
  readonly stepId: string;
  readonly message: string;
  readonly availability: string;
}

export interface WorkflowActivityRunWaiting {
  readonly stepId: string;
  readonly waitingKind: string;
  readonly prompt: string;
  readonly availability: string;
}

export interface WorkflowActivityRunFeedRow {
  readonly runId: string;
  readonly actorId: string;
  readonly workflowId: string;
  readonly workflowName: string;
  readonly scopeId: string;
  readonly status: string;
  readonly runOrigin: string;
  readonly success: boolean | null;
  readonly initiator: WorkflowActivityRunInitiator;
  readonly inputSummary: string;
  readonly currentStep: WorkflowActivityRunCurrentStep;
  readonly firstFailure: WorkflowActivityRunFirstFailure;
  readonly waiting: WorkflowActivityRunWaiting;
  readonly startedAtUtc: string | null;
  readonly completedAtUtc: string | null;
  readonly updatedAtUtc: string;
  readonly durationMs: number | null;
  readonly stateVersion: number;
  readonly recoveryCapability: WorkflowRunRecoveryCapability;
  readonly lineage: WorkflowRunLineage;
}

export interface WorkflowActivityRunFeedPage {
  readonly items: readonly WorkflowActivityRunFeedRow[];
  readonly nextCursor: string | null;
  readonly hasMore: boolean;
  readonly totalCount: number | null;
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
  readonly recoveryCapability: WorkflowRunRecoveryCapability;
  readonly lineage: WorkflowRunLineage;
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
  readonly newRunId: string;
  readonly newRunActorId: string;
  readonly workflowName: string;
  readonly acceptedCommandId: string;
  readonly correlationId: string;
  readonly statusUrl: string;
}
