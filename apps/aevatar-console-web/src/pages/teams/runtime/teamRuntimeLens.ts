import type { WorkflowActorGraphEnrichedSnapshot } from "@/shared/models/runtime/actors";
import type { RuntimeGAgentActorGroup } from "@/shared/models/runtime/gagents";
import type {
  ScopeServiceRevisionCatalogSnapshot,
  ScopeServiceRunAuditSnapshot,
  ScopeServiceRunSummary,
} from "@/shared/models/runtime/scopeServices";
import { getScopeServiceCurrentRevision } from "@/shared/models/runtime/scopeServices";
import type { ServiceCatalogSnapshot } from "@/shared/models/services";
import type { StudioMemberBindingRevision } from "@/shared/studio/models";

export type TeamHealthStatus =
  | "healthy"
  | "attention"
  | "degraded"
  | "blocked"
  | "human-overridden";

export type TeamHealthTone = "default" | "info" | "success" | "warning" | "error";

export type TeamMemberSummary = {
  actorId: string;
  actorType: string;
  isFocused: boolean;
};

export type TeamCompareSection = {
  key: string;
  title: string;
  items: string[];
};

export type TeamCompareSummary = {
  available: boolean;
  title: string;
  summary: string;
  details: string[];
  sections: TeamCompareSection[];
};

export type TeamGraphNodeSummary = {
  actorId: string;
  actorType: string;
  caption: string;
  isFocused: boolean;
  relationCount: number;
};

export type TeamGraphRelationshipSummary = {
  key: string;
  fromActorId: string;
  toActorId: string;
  edgeType: string;
  direction: "inbound" | "outbound" | "peer";
};

export type TeamGraphSummary = {
  available: boolean;
  focusActorId: string;
  focusReason: string;
  nodeCount: number;
  edgeCount: number;
  stageSummary: string;
  nodes: TeamGraphNodeSummary[];
  relationships: TeamGraphRelationshipSummary[];
};

export type TeamGovernanceSummary = {
  servingRevision: string;
  traceability: string;
  humanIntervention: string;
  fallback: string;
  rollout: string;
};

export type TeamPlaybackStatus =
  | "waiting"
  | "active"
  | "completed"
  | "failed";

export type TeamPlaybackStep = {
  key: string;
  stepId: string;
  stepType: string;
  actorId: string | null;
  runId: string | null;
  owner: string;
  status: TeamPlaybackStatus;
  summary: string;
  detail: string;
  timestamp: string | null;
};

export type TeamPlaybackEvent = {
  key: string;
  stage: string;
  message: string;
  detail: string;
  actorId: string | null;
  runId: string | null;
  stepId: string | null;
  timestamp: string | null;
  tone: TeamHealthTone;
};

export type TeamPlaybackSummary = {
  available: boolean;
  commandId: string | null;
  currentRunId: string | null;
  launchPrompt: string;
  rootActorId: string | null;
  title: string;
  summary: string;
  interactionLabel: string;
  prompt: string;
  timeoutLabel: string;
  workflowName: string | null;
  steps: TeamPlaybackStep[];
  events: TeamPlaybackEvent[];
  roleReplies: string[];
};

export type TeamRuntimeLens = {
  scopeId: string;
  title: string;
  subtitle: string;
  activeRevision: StudioMemberBindingRevision | null;
  previousRevision: StudioMemberBindingRevision | null;
  currentService: ServiceCatalogSnapshot | null;
  currentRun: ScopeServiceRunSummary | null;
  baselineRun: ScopeServiceRunSummary | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
  baselineRunAudit: ScopeServiceRunAuditSnapshot | null;
  healthStatus: TeamHealthStatus;
  healthTone: TeamHealthTone;
  healthSummary: string;
  healthDetails: string[];
  members: TeamMemberSummary[];
  graph: TeamGraphSummary;
  compare: TeamCompareSummary;
  playback: TeamPlaybackSummary;
  governance: TeamGovernanceSummary;
  workflowCount: number;
  scriptCount: number;
  serviceCount: number;
  recentRunCount: number;
  partialSignals: string[];
  humanInterventionDetected: boolean;
};

export type TeamRuntimeLensInput = {
  scopeId: string;
  focusedServiceId: string | null;
  serviceRevisionCatalog: ScopeServiceRevisionCatalogSnapshot | null;
  services: readonly ServiceCatalogSnapshot[];
  actors: readonly RuntimeGAgentActorGroup[];
  runs: readonly ScopeServiceRunSummary[];
  currentRun?: ScopeServiceRunSummary | null;
  baselineRun?: ScopeServiceRunSummary | null;
  actorGraph: WorkflowActorGraphEnrichedSnapshot | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
  baselineRunAudit: ScopeServiceRunAuditSnapshot | null;
  workflowCount: number;
  scriptCount: number;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function normalizeStatus(value: string | null | undefined): string {
  return trimOptional(value).toLowerCase();
}

function shortActorId(value: string | null | undefined): string {
  const normalized = trimOptional(value);
  if (!normalized) {
    return "n/a";
  }

  const segment = normalized.split("/").pop() || normalized;
  return segment.split(":").pop() || segment;
}

function sortRuns(runs: readonly ScopeServiceRunSummary[]): ScopeServiceRunSummary[] {
  return [...runs].sort((left, right) => {
    const leftTime = Date.parse(left.lastUpdatedAt || "");
    const rightTime = Date.parse(right.lastUpdatedAt || "");
    return (Number.isFinite(rightTime) ? rightTime : 0) - (Number.isFinite(leftTime) ? leftTime : 0);
  });
}

function sortServices(
  services: readonly ServiceCatalogSnapshot[],
): ServiceCatalogSnapshot[] {
  return [...services].sort((left, right) => {
    const leftDisplayName = trimOptional(left.displayName);
    const rightDisplayName = trimOptional(right.displayName);
    if (leftDisplayName && rightDisplayName && leftDisplayName !== rightDisplayName) {
      return leftDisplayName.localeCompare(rightDisplayName);
    }

    if (leftDisplayName && !rightDisplayName) {
      return -1;
    }

    if (!leftDisplayName && rightDisplayName) {
      return 1;
    }

    return trimOptional(left.serviceId).localeCompare(trimOptional(right.serviceId));
  });
}

function isSuccessfulRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === true) {
    return true;
  }

  return ["completed", "finished", "succeeded", "success"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function isBlockedRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  return [
    "waiting",
    "waiting_signal",
    "waiting_approval",
    "human_input",
    "human_approval",
    "suspended",
    "blocked",
  ].includes(normalizeStatus(run.completionStatus));
}

function isFailedRun(run: ScopeServiceRunSummary | null | undefined): boolean {
  if (!run) {
    return false;
  }

  if (run.lastSuccess === false) {
    return true;
  }

  return ["failed", "error", "stopped", "cancelled", "degraded"].includes(
    normalizeStatus(run.completionStatus),
  );
}

function hasHumanIntervention(
  audit: ScopeServiceRunAuditSnapshot | null | undefined,
): boolean {
  if (!audit) {
    return false;
  }

  return audit.audit.steps.some((step) => {
    const normalizedType = normalizeStatus(step.stepType);
    const normalizedSuspension = normalizeStatus(step.suspensionType);
    return (
      normalizedType.includes("human") ||
      normalizedSuspension.includes("human") ||
      normalizedSuspension.includes("approval") ||
      normalizedSuspension.includes("signal")
    );
  });
}

function describeHighlightedStep(
  audit: ScopeServiceRunAuditSnapshot | null | undefined,
): string {
  if (!audit) {
    return "";
  }

  const suspendedStep = audit.audit.steps.find(
    (step) =>
      trimOptional(step.suspensionType) ||
      normalizeStatus(step.stepType).includes("human"),
  );
  if (suspendedStep) {
    return `${suspendedStep.stepId} · ${suspendedStep.stepType || "step"}`;
  }

  const failedStep = audit.audit.steps.find((step) => step.success === false);
  if (failedStep) {
    return `${failedStep.stepId} · ${failedStep.stepType || "step"}`;
  }

  return "";
}

function describeStepState(
  success: boolean | null | undefined,
  completedAt: string | null | undefined,
  suspensionType: string | null | undefined,
  error: string | null | undefined,
): TeamPlaybackStatus {
  if (trimOptional(suspensionType)) {
    return "waiting";
  }

  if (success === false || trimOptional(error)) {
    return "failed";
  }

  if (success === true || trimOptional(completedAt)) {
    return "completed";
  }

  return "active";
}

function formatStepDiffValue(value: string | null | undefined): string {
  return trimOptional(value) || "n/a";
}

export function selectTeamCompareRuns(
  runs: readonly ScopeServiceRunSummary[],
  options?: {
    preferredRunId?: string | null;
  },
): {
  baselineRun: ScopeServiceRunSummary | null;
  currentRun: ScopeServiceRunSummary | null;
} {
  const sortedRuns = sortRuns(runs);
  const preferredRunId = trimOptional(options?.preferredRunId);
  const currentRun =
    (preferredRunId
      ? sortedRuns.find((run) => trimOptional(run.runId) === preferredRunId) ?? null
      : null) ||
    sortedRuns[0] ||
    null;
  const baselineRun =
    sortedRuns.find((run) => run.runId !== currentRun?.runId && isSuccessfulRun(run)) ||
    null;

  return {
    baselineRun,
    currentRun,
  };
}

function deriveFocusActorId(input: TeamRuntimeLensInput, currentRun: ScopeServiceRunSummary | null): {
  actorId: string;
  reason: string;
} {
  const currentRunActorId = trimOptional(currentRun?.actorId);
  if (currentRunActorId) {
    return {
      actorId: currentRunActorId,
      reason: "Focused on the actor behind the most recent team activity.",
    };
  }

  const activeRevision = getScopeServiceCurrentRevision(input.serviceRevisionCatalog);
  const activeRevisionActorId = trimOptional(activeRevision?.primaryActorId);
  if (activeRevisionActorId) {
    return {
      actorId: activeRevisionActorId,
      reason: "Focused on the currently selected service revision actor because no active run was selected.",
    };
  }

  const focusedServiceActorId = trimOptional(
    input.services.find(
      (service) => trimOptional(service.serviceId) === trimOptional(input.focusedServiceId),
    )?.primaryActorId,
  );
  if (focusedServiceActorId) {
    return {
      actorId: focusedServiceActorId,
      reason: "Focused on the currently selected service actor because no stronger runtime signal was available.",
    };
  }

  const firstActorId = input.actors.flatMap((group) => group.actorIds)[0] ?? "";
  if (firstActorId) {
    return {
      actorId: firstActorId,
      reason: "Focused on the first known team member because no stronger runtime signal was available.",
    };
  }

  return {
    actorId: "",
    reason: "No actor focus is available yet.",
  };
}

function deriveStepDiffs(input: {
  baselineRunAudit: ScopeServiceRunAuditSnapshot | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
}): string[] {
  const currentSteps = input.currentRunAudit?.audit.steps ?? [];
  const baselineSteps = input.baselineRunAudit?.audit.steps ?? [];
  if (currentSteps.length === 0 && baselineSteps.length === 0) {
    return [];
  }

  const currentById = new Map(currentSteps.map((step) => [step.stepId, step]));
  const baselineById = new Map(baselineSteps.map((step) => [step.stepId, step]));
  const stepIds = [...new Set([...currentById.keys(), ...baselineById.keys()])].slice(0, 8);

  return stepIds.flatMap((stepId) => {
    const current = currentById.get(stepId);
    const baseline = baselineById.get(stepId);
    if (current && !baseline) {
      return [
        `Step ${stepId} is new in the current run as ${formatStepDiffValue(current.stepType)}.`,
      ];
    }
    if (!current && baseline) {
      return [
        `Step ${stepId} was visible in the baseline but is absent from the current run.`,
      ];
    }
    if (!current || !baseline) {
      return [];
    }

    const deltas: string[] = [];
    if (normalizeStatus(current.stepType) !== normalizeStatus(baseline.stepType)) {
      deltas.push(
        `type ${formatStepDiffValue(baseline.stepType)} -> ${formatStepDiffValue(current.stepType)}`,
      );
    }
    if (
      normalizeStatus(current.suspensionType) !==
      normalizeStatus(baseline.suspensionType)
    ) {
      deltas.push(
        `gate ${formatStepDiffValue(baseline.suspensionType)} -> ${formatStepDiffValue(current.suspensionType)}`,
      );
    }
    if (normalizeStatus(current.targetRole) !== normalizeStatus(baseline.targetRole)) {
      deltas.push(
        `owner ${formatStepDiffValue(baseline.targetRole)} -> ${formatStepDiffValue(current.targetRole)}`,
      );
    }
    if (trimOptional(current.nextStepId) !== trimOptional(baseline.nextStepId)) {
      deltas.push(
        `next ${formatStepDiffValue(baseline.nextStepId)} -> ${formatStepDiffValue(current.nextStepId)}`,
      );
    }
    if (current.success !== baseline.success) {
      deltas.push(
        `status ${baseline.success === true ? "completed" : baseline.success === false ? "failed" : "pending"} -> ${
          current.success === true ? "completed" : current.success === false ? "failed" : "pending"
        }`,
      );
    }

    return deltas.length > 0 ? [`Step ${stepId}: ${deltas.join(", ")}.`] : [];
  });
}

function deriveHandoffDiffs(input: {
  baselineRunAudit: ScopeServiceRunAuditSnapshot | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
}): string[] {
  const currentTopology = input.currentRunAudit?.audit.topology ?? [];
  const baselineTopology = input.baselineRunAudit?.audit.topology ?? [];
  if (currentTopology.length === 0 && baselineTopology.length === 0) {
    return [];
  }

  const toKey = (entry: { parent: string; child: string }) =>
    `${shortActorId(entry.parent)}->${shortActorId(entry.child)}`;

  const currentKeys = new Set(currentTopology.map(toKey));
  const baselineKeys = new Set(baselineTopology.map(toKey));

  const added = [...currentKeys].filter((key) => !baselineKeys.has(key)).slice(0, 4);
  const removed = [...baselineKeys].filter((key) => !currentKeys.has(key)).slice(0, 4);
  const kept = [...currentKeys].filter((key) => baselineKeys.has(key)).slice(0, 2);

  const items: string[] = [];
  if (added.length > 0) {
    items.push(`New handoffs in the current run: ${added.join(", ")}.`);
  }
  if (removed.length > 0) {
    items.push(`Handoffs no longer visible: ${removed.join(", ")}.`);
  }
  if (items.length === 0 && kept.length > 0) {
    items.push(`Handoff path stayed stable on ${kept.join(", ")}.`);
  }
  return items;
}

function deriveCompareSummary(input: {
  baselineRun: ScopeServiceRunSummary | null;
  baselineRunAudit: ScopeServiceRunAuditSnapshot | null;
  currentRun: ScopeServiceRunSummary | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
}): TeamCompareSummary {
  const { baselineRun, baselineRunAudit, currentRun, currentRunAudit } = input;
  if (!currentRun) {
    return {
      available: false,
      title: "Run Compare / Change Diff",
      summary: "No team activity has been captured yet.",
      details: [],
      sections: [],
    };
  }

  if (!baselineRun) {
    return {
      available: false,
      title: "Run Compare / Change Diff",
      summary: "No successful baseline is available yet.",
      details: ["The team has not produced a comparable prior good run."],
      sections: [],
    };
  }

  const runtimeItems: string[] = [];
  if (
    trimOptional(currentRun.revisionId) &&
    trimOptional(baselineRun.revisionId) &&
    trimOptional(currentRun.revisionId) !== trimOptional(baselineRun.revisionId)
  ) {
    runtimeItems.push(`Revision changed from ${baselineRun.revisionId} to ${currentRun.revisionId}.`);
  } else if (trimOptional(currentRun.revisionId)) {
    runtimeItems.push(`Revision stayed on ${currentRun.revisionId}.`);
  }

  if (normalizeStatus(currentRun.completionStatus) !== normalizeStatus(baselineRun.completionStatus)) {
    runtimeItems.push(
      `Run outcome moved from ${baselineRun.completionStatus || "unknown"} to ${currentRun.completionStatus || "unknown"}.`,
    );
  }

  if (
    trimOptional(currentRun.actorId) &&
    trimOptional(baselineRun.actorId) &&
    trimOptional(currentRun.actorId) !== trimOptional(baselineRun.actorId)
  ) {
    runtimeItems.push(`Focus actor moved from ${baselineRun.actorId} to ${currentRun.actorId}.`);
  }

  const currentHighlightedStep = describeHighlightedStep(currentRunAudit);
  const baselineHighlightedStep = describeHighlightedStep(baselineRunAudit);
  if (currentHighlightedStep && currentHighlightedStep !== baselineHighlightedStep) {
    runtimeItems.push(`Current highlighted step: ${currentHighlightedStep}.`);
  }

  if (trimOptional(currentRun.lastError)) {
    runtimeItems.push(`Current run error: ${trimOptional(currentRun.lastError)}.`);
  } else if (trimOptional(currentRun.lastOutput)) {
    runtimeItems.push(`Current run output preview: ${trimOptional(currentRun.lastOutput)}.`);
  }

  const stepItems = deriveStepDiffs({
    baselineRunAudit,
    currentRunAudit,
  });
  const handoffItems = deriveHandoffDiffs({
    baselineRunAudit,
    currentRunAudit,
  });

  const sections: TeamCompareSection[] = [
    {
      key: "runtime",
      title: "Runtime deltas",
      items:
        runtimeItems.length > 0
          ? runtimeItems
          : ["No top-level runtime delta was detected."],
    },
    ...(stepItems.length > 0
      ? [
          {
            key: "steps",
            title: "Step deltas",
            items: stepItems,
          },
        ]
      : []),
    ...(handoffItems.length > 0
      ? [
          {
            key: "handoffs",
            title: "Handoff deltas",
            items: handoffItems,
          },
        ]
      : []),
  ];

  return {
    available: true,
    title: "Run Compare / Change Diff",
    summary: `Comparing run ${currentRun.runId} against baseline ${baselineRun.runId}.`,
    details: sections.flatMap((section) => section.items),
    sections,
  };
}

function deriveHealth(input: {
  activeRevision: StudioMemberBindingRevision | null;
  currentRun: ScopeServiceRunSummary | null;
  currentRunAudit: ScopeServiceRunAuditSnapshot | null;
  currentService: ServiceCatalogSnapshot | null;
}): {
  details: string[];
  status: TeamHealthStatus;
  summary: string;
  tone: TeamHealthTone;
} {
  const details: string[] = [];
  const humanInterventionDetected = hasHumanIntervention(input.currentRunAudit);

  if (humanInterventionDetected) {
    details.push("A human-in-the-loop step is visible in the current run.");
  }

  if (trimOptional(input.activeRevision?.revisionId)) {
    details.push(`Focused revision is ${input.activeRevision?.revisionId}.`);
  }

  if (trimOptional(input.currentService?.deploymentStatus)) {
    details.push(`Service deployment is ${input.currentService?.deploymentStatus}.`);
  }

  if (!input.currentService && !input.activeRevision && !input.currentRun) {
    return {
      status: "attention",
      tone: "warning",
      summary:
        "The current team does not have a published member service or visible run yet.",
      details: [
        "No published member-scoped service is visible for the current team focus.",
      ],
    };
  }

  if (!input.currentRun) {
    if (normalizeStatus(input.currentService?.deploymentStatus) === "active") {
      return {
        status: "attention",
        tone: "info",
        summary:
          "The current team has a published member service, but no recent run is available to prove runtime health.",
        details: [
          ...details,
          "No recent team activity is available to verify the current member service.",
        ],
      };
    }

    return {
      status: "attention",
      tone: "info",
      summary: "The current team is partially visible, but no recent run is available yet.",
      details,
    };
  }

  if (isBlockedRun(input.currentRun)) {
    return {
      status: "blocked",
      tone: "warning",
      summary: "The current team is waiting on a blocked or human-gated run.",
      details: [
        ...details,
        `Current run ${input.currentRun?.runId || "n/a"} is ${input.currentRun?.completionStatus || "blocked"}.`,
      ],
    };
  }

  if (humanInterventionDetected) {
    return {
      status: "human-overridden",
      tone: "warning",
      summary: "The current team is healthy enough to inspect, but human intervention is active.",
      details,
    };
  }

  if (isFailedRun(input.currentRun)) {
    return {
      status: "degraded",
      tone: "error",
      summary: "The current team is degraded by a failed or unhealthy recent run.",
      details: [
        ...details,
        input.currentRun?.lastError
          ? `Latest error: ${input.currentRun.lastError}.`
          : `Current run status is ${input.currentRun?.completionStatus || "failed"}.`,
      ],
    };
  }

  if (isSuccessfulRun(input.currentRun)) {
    return {
      status: "healthy",
      tone: "success",
      summary: "The current team has an active serving path with no visible critical blocker.",
      details,
    };
  }

  return {
    status: "attention",
    tone: "info",
    summary: "The current team is partially visible, but the latest runtime signal is not strong enough to call healthy.",
    details,
  };
}

function deriveMembers(
  actorGroups: readonly RuntimeGAgentActorGroup[],
  focusActorId: string,
  fallbackActorId: string,
): TeamMemberSummary[] {
  const members = actorGroups.flatMap((group) =>
    group.actorIds.map((actorId) => ({
      actorId,
      actorType: trimOptional(group.gAgentType) || "Team member",
      isFocused: actorId === focusActorId,
    })),
  );

  if (members.length > 0) {
    return members;
  }

  if (fallbackActorId) {
    return [
      {
        actorId: fallbackActorId,
        actorType: "Primary actor",
        isFocused: fallbackActorId === focusActorId,
      },
    ];
  }

  return [];
}

function deriveGraphSummary(input: {
  actorGraph: WorkflowActorGraphEnrichedSnapshot | null;
  focusActorId: string;
  focusReason: string;
}): TeamGraphSummary {
  if (!input.actorGraph) {
    return {
      available: false,
      focusActorId: input.focusActorId,
      focusReason: input.focusReason,
      nodeCount: 0,
      edgeCount: 0,
      stageSummary: "No collaboration graph is currently available.",
      nodes: [],
      relationships: [],
    };
  }

  const rootNodeId =
    trimOptional(input.focusActorId) || input.actorGraph.subgraph.rootNodeId;
  const relationCountByNode = new Map<string, number>();
  input.actorGraph.subgraph.edges.forEach((edge) => {
    relationCountByNode.set(
      edge.fromNodeId,
      (relationCountByNode.get(edge.fromNodeId) ?? 0) + 1,
    );
    relationCountByNode.set(
      edge.toNodeId,
      (relationCountByNode.get(edge.toNodeId) ?? 0) + 1,
    );
  });

  const nodes = [...input.actorGraph.subgraph.nodes]
    .sort((left, right) => {
      const leftFocused = left.nodeId === rootNodeId ? 1 : 0;
      const rightFocused = right.nodeId === rootNodeId ? 1 : 0;
      if (leftFocused !== rightFocused) {
        return rightFocused - leftFocused;
      }
      return (relationCountByNode.get(right.nodeId) ?? 0) - (relationCountByNode.get(left.nodeId) ?? 0);
    })
    .slice(0, 6)
    .map((node) => ({
      actorId: node.nodeId,
      actorType: trimOptional(node.nodeType) || "actor",
      caption:
        trimOptional(node.properties.label) ||
        trimOptional(node.properties.role) ||
        `Last updated ${node.updatedAt || "unknown"}`,
      isFocused: node.nodeId === rootNodeId,
      relationCount: relationCountByNode.get(node.nodeId) ?? 0,
    }));

  const relationships: TeamGraphRelationshipSummary[] = input.actorGraph.subgraph.edges
    .slice(0, 8)
    .map((edge) => ({
      key: edge.edgeId || `${edge.fromNodeId}:${edge.toNodeId}`,
      fromActorId: edge.fromNodeId,
      toActorId: edge.toNodeId,
      edgeType: trimOptional(edge.edgeType) || "relation",
      direction:
        edge.fromNodeId === rootNodeId
          ? ("outbound" as const)
          : edge.toNodeId === rootNodeId
            ? ("inbound" as const)
            : ("peer" as const),
    }));

  return {
    available: true,
    focusActorId: input.focusActorId,
    focusReason: input.focusReason,
    nodeCount: input.actorGraph.subgraph.nodes.length,
    edgeCount: input.actorGraph.subgraph.edges.length,
    stageSummary:
      relationships.length > 0
        ? "This canvas shows the currently focused actor and the nearest visible collaboration paths around it."
        : "The current actor graph has no visible collaboration edges yet.",
    nodes,
    relationships,
  };
}

function derivePlaybackSummary(
  currentRunAudit: ScopeServiceRunAuditSnapshot | null,
): TeamPlaybackSummary {
  if (!currentRunAudit) {
    return {
      available: false,
      commandId: null,
      currentRunId: null,
      launchPrompt: "",
      rootActorId: null,
      title: "Human Escalation Playback",
      summary: "当前团队最近还没有可见事件。",
      interactionLabel: "",
      prompt: "",
      timeoutLabel: "",
      workflowName: null,
      steps: [],
      events: [],
      roleReplies: [],
    };
  }

  const runId = trimOptional(currentRunAudit.summary.runId) || null;
  const rootActorId =
    trimOptional(currentRunAudit.audit.rootActorId) ||
    trimOptional(currentRunAudit.summary.actorId) ||
    null;
  const commandId = trimOptional(currentRunAudit.audit.commandId) || null;
  const workflowName =
    trimOptional(currentRunAudit.audit.workflowName) ||
    trimOptional(currentRunAudit.summary.workflowName) ||
    null;
  const launchPrompt = trimOptional(currentRunAudit.audit.input);
  const stepActorMap = new Map<string, string>();
  currentRunAudit.audit.steps.forEach((step) => {
    const actorId =
      trimOptional(step.workerId) || trimOptional(currentRunAudit.audit.rootActorId);
    if (!actorId) {
      return;
    }

    stepActorMap.set(step.stepId, actorId);
  });

  const steps = currentRunAudit.audit.steps.slice(0, 5).map((step) => {
    const status = describeStepState(
      step.success,
      step.completedAt,
      step.suspensionType,
      step.error,
    );
    const actorId =
      trimOptional(step.workerId) ||
      trimOptional(currentRunAudit.audit.rootActorId) ||
      trimOptional(currentRunAudit.summary.actorId) ||
      null;
    const owner = trimOptional(step.targetRole) || trimOptional(step.workerId) || "team";
    const stepType = trimOptional(step.stepType) || "step";
    const detailParts = [
      trimOptional(step.suspensionPrompt)
        ? `Prompt: ${trimOptional(step.suspensionPrompt)}`
        : "",
      trimOptional(step.error) ? `Error: ${trimOptional(step.error)}` : "",
      trimOptional(step.outputPreview)
        ? `Output: ${trimOptional(step.outputPreview)}`
        : "",
      trimOptional(step.nextStepId)
        ? `Next: ${trimOptional(step.nextStepId)}`
        : "",
    ].filter(Boolean);

    return {
      key: step.stepId,
      stepId: step.stepId,
      stepType,
      actorId,
      runId,
      owner,
      status,
      summary: `${stepType} · ${owner}`,
      detail:
        detailParts[0] || "No additional playback detail is available for this step yet.",
      timestamp: step.completedAt || step.requestedAt,
    };
  });

  const gatingStep =
    currentRunAudit.audit.steps.find((step) => trimOptional(step.suspensionType)) ||
    currentRunAudit.audit.steps.find((step) => normalizeStatus(step.stepType).includes("human")) ||
    currentRunAudit.audit.steps.find((step) => step.success === false) ||
    null;

  const events: TeamPlaybackEvent[] =
    currentRunAudit.audit.timeline.length > 0
      ? currentRunAudit.audit.timeline.slice(-5).reverse().map((event, index) => ({
          key: `${event.stage}:${event.timestamp || index}`,
          stage: trimOptional(event.stage) || "runtime",
          message: trimOptional(event.message) || "No timeline message",
          detail:
            trimOptional(event.stepId) || trimOptional(event.agentId)
              ? [trimOptional(event.stepId), trimOptional(event.agentId)]
                  .filter(Boolean)
                  .join(" · ")
              : trimOptional(event.eventType) || "runtime event",
          actorId:
            trimOptional(event.agentId) ||
            stepActorMap.get(trimOptional(event.stepId)) ||
            trimOptional(currentRunAudit.audit.rootActorId) ||
            trimOptional(currentRunAudit.summary.actorId) ||
            null,
          runId,
          stepId: trimOptional(event.stepId) || null,
          timestamp: event.timestamp,
          tone:
            normalizeStatus(event.stage).includes("error") ||
            normalizeStatus(event.eventType).includes("error")
              ? ("error" as const)
              : normalizeStatus(event.stage).includes("wait") ||
                  normalizeStatus(event.stage).includes("human")
                ? ("warning" as const)
                : ("info" as const),
        }))
      : steps.slice(0, 3).map((step, index) => ({
          key: `derived-step-${step.stepId}`,
          stage: step.status === "waiting" ? "waiting" : "step",
          message: `${step.stepId} is ${step.status}.`,
          detail: step.summary,
          actorId: step.actorId,
          runId: step.runId,
          stepId: step.stepId,
          timestamp: step.timestamp,
          tone:
            step.status === "failed"
              ? ("error" as const)
              : step.status === "waiting"
                ? ("warning" as const)
                : ("info" as const),
        }));

  return {
    available: true,
    commandId,
    currentRunId: runId,
    launchPrompt,
    rootActorId,
    title: "Human Escalation Playback",
    summary: gatingStep
      ? `Current playback is centered on ${gatingStep.stepId}, which is the clearest visible gate in the latest run.`
      : `Playback is showing the latest ${steps.length} visible steps from the current run.`,
    interactionLabel: trimOptional(gatingStep?.suspensionType) || trimOptional(gatingStep?.stepType),
    prompt: trimOptional(gatingStep?.suspensionPrompt),
    timeoutLabel:
      gatingStep?.suspensionTimeoutSeconds != null
        ? `${gatingStep.suspensionTimeoutSeconds}s timeout`
        : "",
    workflowName,
    steps,
    events,
    roleReplies: currentRunAudit.audit.roleReplies.slice(-3).map((reply) => {
      const prefix = trimOptional(reply.roleId) || trimOptional(reply.sessionId) || "reply";
      const content = trimOptional(reply.content) || "No reply content";
      return `${prefix}: ${content}`;
    }),
  };
}

export function deriveTeamRuntimeLens(
  input: TeamRuntimeLensInput,
): TeamRuntimeLens {
  const activeRevision = getScopeServiceCurrentRevision(input.serviceRevisionCatalog);
  const previousRevision =
    input.serviceRevisionCatalog?.revisions.find(
      (revision) => revision.revisionId !== activeRevision?.revisionId,
    ) || null;
  const sortedServices = sortServices(input.services);
  const selectedRuns =
    input.currentRun !== undefined || input.baselineRun !== undefined
      ? {
          baselineRun: input.baselineRun ?? null,
          currentRun: input.currentRun ?? null,
        }
      : selectTeamCompareRuns(input.runs);
  const { baselineRun, currentRun } = selectedRuns;
  const currentService =
    sortedServices.find(
      (service) => trimOptional(service.serviceId) === trimOptional(input.focusedServiceId),
    ) ||
    sortedServices.find(
      (service) =>
        trimOptional(service.serviceId) ===
        trimOptional(input.serviceRevisionCatalog?.serviceId),
    ) ||
    sortedServices.find(
      (service) => trimOptional(service.serviceId) === trimOptional(currentRun?.serviceId),
    ) ||
    sortedServices[0] ||
    null;
  const focus = deriveFocusActorId(input, currentRun);
  const members = deriveMembers(
    input.actors,
    focus.actorId,
    trimOptional(currentService?.primaryActorId) || trimOptional(activeRevision?.primaryActorId),
  );
  const compare = deriveCompareSummary({
    baselineRun,
    baselineRunAudit: input.baselineRunAudit,
    currentRun,
    currentRunAudit: input.currentRunAudit,
  });
  const playback = derivePlaybackSummary(input.currentRunAudit);
  const health = deriveHealth({
    activeRevision,
    currentRun,
    currentRunAudit: input.currentRunAudit,
    currentService,
  });
  const partialSignals: string[] = [];
  if (!input.actorGraph) {
    partialSignals.push("Actor graph unavailable");
  }
  if (!baselineRun) {
    partialSignals.push("No successful baseline run");
  }
  if (!currentRun) {
    partialSignals.push("No recent runs");
  }
  const subtitleParts = [
    input.workflowCount > 0 ? `${input.workflowCount} 个 workflow` : "",
    input.scriptCount > 0 ? `${input.scriptCount} 个 script` : "",
    input.services.length > 0 ? `${input.services.length} 个 service` : "",
  ].filter(Boolean);

  return {
    scopeId: input.scopeId,
    title: "当前团队",
    subtitle:
      subtitleParts.length > 0
        ? `团队容器 · ${subtitleParts.join(" / ")}`
        : "团队容器，成员绑定与运行信号会在这里汇总。",
    activeRevision,
    previousRevision,
    currentService,
    currentRun,
    baselineRun,
    currentRunAudit: input.currentRunAudit,
    baselineRunAudit: input.baselineRunAudit,
    healthStatus: health.status,
    healthTone: health.tone,
    healthSummary: health.summary,
    healthDetails: health.details,
    members,
    graph: deriveGraphSummary({
      actorGraph: input.actorGraph,
      focusActorId: focus.actorId,
      focusReason: focus.reason,
    }),
    compare,
    playback,
    governance: {
      servingRevision:
        activeRevision?.revisionId ||
        trimOptional(currentService?.activeServingRevisionId) ||
        trimOptional(currentService?.defaultServingRevisionId) ||
        "Unknown",
      traceability: currentRun
        ? `Recent run ${currentRun.runId} is traceable through team activity.`
        : "No recent run is available yet.",
      humanIntervention: hasHumanIntervention(input.currentRunAudit)
        ? "Human intervention is visible in the current run."
        : "No active human intervention is visible.",
      fallback: baselineRun
        ? `Prior good run ${baselineRun.runId} on revision ${baselineRun.revisionId || "unknown"}.`
        : "Fallback state unavailable.",
      rollout: currentService?.deploymentStatus
        ? `Current deployment is ${currentService.deploymentStatus}.`
        : "Deployment status unavailable.",
    },
    workflowCount: input.workflowCount,
    scriptCount: input.scriptCount,
    serviceCount: input.services.length,
    recentRunCount: input.runs.length,
    partialSignals,
    humanInterventionDetected: hasHumanIntervention(input.currentRunAudit),
  };
}
