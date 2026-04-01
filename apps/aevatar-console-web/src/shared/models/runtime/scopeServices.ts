import type { ServiceBindingSnapshot } from "@/shared/models/governance";
import type { StudioScopeBindingRevision } from "@/shared/studio/models";

export interface ScopeServiceBindingInput {
  readonly bindingId: string;
  readonly displayName: string;
  readonly bindingKind: string;
  readonly policyIds?: readonly string[];
  readonly service?: {
    readonly serviceId: string;
    readonly endpointId?: string | null;
  } | null;
  readonly connector?: {
    readonly connectorType: string;
    readonly connectorId: string;
  } | null;
  readonly secret?: {
    readonly secretName: string;
  } | null;
}

export interface ScopeServiceRevisionCatalogSnapshot {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly serviceKey: string;
  readonly displayName: string;
  readonly defaultServingRevisionId: string;
  readonly activeServingRevisionId: string;
  readonly deploymentId: string;
  readonly deploymentStatus: string;
  readonly primaryActorId: string;
  readonly catalogStateVersion: number;
  readonly catalogLastEventId: string;
  readonly updatedAt: string | null;
  readonly revisions: readonly StudioScopeBindingRevision[];
}

export interface ScopeServiceRevisionActionResult {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly revisionId: string;
  readonly status: string;
}

export interface ScopeServiceRunSummary {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly runId: string;
  readonly actorId: string;
  readonly definitionActorId: string;
  readonly revisionId: string;
  readonly deploymentId: string;
  readonly workflowName: string;
  readonly completionStatus: string;
  readonly stateVersion: number;
  readonly lastEventId: string;
  readonly lastUpdatedAt: string | null;
  readonly boundAt: string | null;
  readonly bindingUpdatedAt: string | null;
  readonly lastSuccess: boolean | null;
  readonly totalSteps: number;
  readonly completedSteps: number;
  readonly roleReplyCount: number;
  readonly lastOutput: string;
  readonly lastError: string;
}

export interface ScopeServiceRunCatalogSnapshot {
  readonly scopeId: string;
  readonly serviceId: string;
  readonly serviceKey: string;
  readonly displayName: string;
  readonly runs: readonly ScopeServiceRunSummary[];
}

export interface ScopeServiceRunAuditSummary {
  readonly totalSteps: number;
  readonly requestedSteps: number;
  readonly completedSteps: number;
  readonly roleReplyCount: number;
  readonly stepTypeCounts: Readonly<Record<string, number>>;
}

export interface ScopeServiceRunAuditStep {
  readonly stepId: string;
  readonly stepType: string;
  readonly targetRole: string;
  readonly requestedAt: string | null;
  readonly completedAt: string | null;
  readonly success: boolean | null;
  readonly workerId: string;
  readonly outputPreview: string;
  readonly error: string;
  readonly requestParameters: Readonly<Record<string, string>>;
  readonly completionAnnotations: Readonly<Record<string, string>>;
  readonly nextStepId: string;
  readonly branchKey: string;
  readonly assignedVariable: string;
  readonly assignedValue: string;
  readonly suspensionType: string;
  readonly suspensionPrompt: string;
  readonly suspensionTimeoutSeconds: number | null;
  readonly requestedVariableName: string;
  readonly durationMs: number | null;
}

export interface ScopeServiceRunAuditReply {
  readonly timestamp: string | null;
  readonly roleId: string;
  readonly sessionId: string;
  readonly content: string;
  readonly contentLength: number;
}

export interface ScopeServiceRunAuditTimelineEvent {
  readonly timestamp: string | null;
  readonly stage: string;
  readonly message: string;
  readonly agentId: string;
  readonly stepId: string;
  readonly stepType: string;
  readonly eventType: string;
  readonly data: Readonly<Record<string, string>>;
}

export interface ScopeServiceRunAuditReport {
  readonly reportVersion: string;
  readonly projectionScope: string;
  readonly topologySource: string;
  readonly completionStatus: string;
  readonly workflowName: string;
  readonly rootActorId: string;
  readonly commandId: string;
  readonly stateVersion: number;
  readonly lastEventId: string;
  readonly createdAt: string | null;
  readonly updatedAt: string | null;
  readonly startedAt: string | null;
  readonly endedAt: string | null;
  readonly durationMs: number;
  readonly success: boolean | null;
  readonly input: string;
  readonly finalOutput: string;
  readonly finalError: string;
  readonly topology: readonly {
    readonly parent: string;
    readonly child: string;
  }[];
  readonly steps: readonly ScopeServiceRunAuditStep[];
  readonly roleReplies: readonly ScopeServiceRunAuditReply[];
  readonly timeline: readonly ScopeServiceRunAuditTimelineEvent[];
  readonly summary: ScopeServiceRunAuditSummary;
}

export interface ScopeServiceRunAuditSnapshot {
  readonly summary: ScopeServiceRunSummary;
  readonly audit: ScopeServiceRunAuditReport;
}

export type ScopeServiceBindingCatalogSnapshot = {
  readonly serviceKey: string;
  readonly bindings: readonly ServiceBindingSnapshot[];
  readonly updatedAt: string | null;
};

export function getScopeServiceCurrentRevision(
  catalog: ScopeServiceRevisionCatalogSnapshot | null | undefined,
): StudioScopeBindingRevision | null {
  if (!catalog?.revisions.length) {
    return null;
  }

  return (
    catalog.revisions.find((revision) => revision.isActiveServing) ||
    catalog.revisions.find((revision) => revision.isDefaultServing) ||
    catalog.revisions[0] ||
    null
  );
}

export function describeScopeServiceBindingTarget(
  binding: ServiceBindingSnapshot | null | undefined,
): string {
  if (!binding) {
    return "n/a";
  }

  if (binding.serviceRef) {
    return `${binding.serviceRef.identity.serviceId}:${binding.serviceRef.endpointId || "*"}`;
  }

  if (binding.connectorRef) {
    return `${binding.connectorRef.connectorType}:${binding.connectorRef.connectorId}`;
  }

  if (binding.secretRef) {
    return binding.secretRef.secretName;
  }

  return "n/a";
}
