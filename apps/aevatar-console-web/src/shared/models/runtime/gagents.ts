export interface RuntimeGAgentEndpointDescriptor {
  endpointId: string;
  displayName: string;
  kind: string;
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
  auto: boolean;
}

export interface RuntimeGAgentKindDescriptor {
  agentKind: string;
  displayName: string;
  diagnosticClrTypeName: string;
  endpoints: RuntimeGAgentEndpointDescriptor[];
}

export interface RuntimeGAgentActorGroup {
  agentKind: string;
  actorIds: string[];
}

export type RuntimeGAgentBindingImplementationKind =
  | "workflow"
  | "script"
  | "gagent"
  | "unknown";

export interface RuntimeGAgentBindingEndpointInput {
  endpointId: string;
  displayName?: string;
  kind?: "command" | "chat";
  requestTypeUrl?: string;
  responseTypeUrl?: string;
  description?: string;
}

export interface RuntimeGAgentBindingResult {
  scopeId: string;
  serviceId?: string;
  displayName: string;
  revisionId: string;
  implementationKind: RuntimeGAgentBindingImplementationKind;
  targetName: string;
  expectedActorId?: string;
  gAgent?: {
    agentKind: string;
    diagnosticActorTypeName: string;
    preferredActorId: string;
  } | null;
}

export interface RuntimeGAgentBindingRevision {
  revisionId: string;
  implementationKind: RuntimeGAgentBindingImplementationKind;
  status: string;
  artifactHash: string;
  failureReason: string;
  isDefaultServing: boolean;
  isActiveServing: boolean;
  isServingTarget: boolean;
  allocationWeight: number;
  servingState: string;
  deploymentId: string;
  primaryActorId: string;
  createdAt: string | null;
  preparedAt: string | null;
  publishedAt: string | null;
  retiredAt: string | null;
  workflowName: string;
  workflowDefinitionActorId: string;
  inlineWorkflowCount: number;
  scriptId: string;
  scriptRevision: string;
  scriptDefinitionActorId: string;
  scriptSourceHash: string;
  staticActorTypeName: string;
  staticAgentKind: string;
  staticPreferredActorId: string;
}

export interface RuntimeGAgentBindingStatus {
  available: boolean;
  scopeId: string;
  serviceId: string;
  displayName: string;
  serviceKey: string;
  defaultServingRevisionId: string;
  activeServingRevisionId: string;
  deploymentId: string;
  deploymentStatus: string;
  primaryActorId: string;
  updatedAt: string | null;
  revisions: RuntimeGAgentBindingRevision[];
}

export interface RuntimeGAgentBindingActivationResult {
  scopeId: string;
  serviceId: string;
  displayName: string;
  revisionId: string;
}

export interface RuntimeGAgentBindingRetirementResult {
  scopeId: string;
  serviceId: string;
  revisionId: string;
  status: string;
}

export function normalizeRuntimeGAgentBindingImplementationKind(
  value: string | number | null | undefined
): RuntimeGAgentBindingImplementationKind {
  if (typeof value === "number") {
    switch (value) {
      case 1:
        return "workflow";
      case 2:
        return "script";
      case 3:
        return "gagent";
      default:
        return "unknown";
    }
  }

  const normalized = String(value || "").trim().toLowerCase();
  switch (normalized) {
    case "workflow":
      return "workflow";
    case "script":
    case "scripting":
      return "script";
    case "gagent":
      return "gagent";
    default:
      return "unknown";
  }
}

export function formatRuntimeGAgentBindingImplementationKind(
  value: RuntimeGAgentBindingImplementationKind | string | null | undefined
): string {
  switch (normalizeRuntimeGAgentBindingImplementationKind(value)) {
    case "workflow":
      return "Workflow";
    case "script":
      return "Script";
    case "gagent":
      return "GAgent";
    default:
      return "Unknown";
  }
}

export function normalizeRuntimeGAgentKindName(value: string): string {
  return value
    .split(",")
    .map((segment) => segment.trim())
    .filter((segment) => segment.length > 0)
    .join(", ");
}

export function normalizeRuntimeGAgentKind(value: string): string {
  return normalizeRuntimeGAgentKindName(value);
}

export function buildRuntimeGAgentKindValue(
  descriptor: RuntimeGAgentKindDescriptor
): string {
  return descriptor.agentKind.trim();
}

export function buildRuntimeGAgentKindLabel(
  descriptor: RuntimeGAgentKindDescriptor
): string {
  return descriptor.displayName.trim() || descriptor.agentKind.trim();
}

export function matchesRuntimeGAgentKindDescriptor(
  agentKind: string,
  descriptor: RuntimeGAgentKindDescriptor
): boolean {
  const normalizedAgentKind = normalizeRuntimeGAgentKind(agentKind);
  if (!normalizedAgentKind) {
    return false;
  }

  return normalizeRuntimeGAgentKind(descriptor.agentKind) === normalizedAgentKind;
}

export function describeRuntimeGAgentBindingRevisionTarget(
  revision: RuntimeGAgentBindingRevision | null | undefined
): string {
  if (!revision) {
    return "Not configured";
  }

  switch (normalizeRuntimeGAgentBindingImplementationKind(revision.implementationKind)) {
    case "workflow":
      return revision.workflowName || "Workflow";
    case "script":
      return revision.scriptId || "Script";
    case "gagent":
      return revision.staticActorTypeName || "GAgent";
    default:
      return "Unknown";
  }
}

export function getRuntimeGAgentCurrentBindingRevision(
  status: RuntimeGAgentBindingStatus | null | undefined
): RuntimeGAgentBindingRevision | null {
  if (!status?.revisions.length) {
    return null;
  }

  return (
    status.revisions.find((revision) => revision.isActiveServing) ||
    status.revisions.find((revision) => revision.isDefaultServing) ||
    status.revisions[0] ||
    null
  );
}

export function collectRuntimeGAgentActorIds(
  agentKind: string,
  actorGroups: readonly RuntimeGAgentActorGroup[],
  descriptor?: RuntimeGAgentKindDescriptor | null
): string[] {
  const keys = new Set<string>();
  const normalizedAgentKind = normalizeRuntimeGAgentKind(agentKind);

  if (normalizedAgentKind) {
    keys.add(normalizedAgentKind);
  }

  if (descriptor) {
    keys.add(normalizeRuntimeGAgentKind(descriptor.agentKind));
  }

  const actorIds = new Set<string>();
  actorGroups.forEach((group) => {
    if (!keys.has(normalizeRuntimeGAgentKind(group.agentKind))) {
      return;
    }

    group.actorIds.forEach((actorId) => {
      const normalizedActorId = actorId.trim();
      if (normalizedActorId) {
        actorIds.add(normalizedActorId);
      }
    });
  });

  return Array.from(actorIds);
}
