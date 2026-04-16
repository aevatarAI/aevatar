import type {
  WorkflowActorGraphSubgraph,
  WorkflowActorSnapshot,
  WorkflowActorTimelineItem,
} from "@/shared/models/runtime/actors";
import type { WorkflowAgentSummary } from "@/shared/models/runtime/query";

export type TopologyMockRecord = {
  actor: WorkflowAgentSummary;
  graph: WorkflowActorGraphSubgraph;
  runId: string;
  scopeId: string;
  serviceId: string;
  snapshot: WorkflowActorSnapshot;
  timeline: WorkflowActorTimelineItem[];
  workflowName: string;
};

const scopeId = "1626c177-917b-4fcc-a5ee-aa74a171b0d6";
const serviceId = "draft";
const serviceIdSecondary = "billing-review";
const runId = "run-20260415-213928";
const runIdSecondary = "run-20260415-204410";
const workflowName = "CustomerSupportTriage";
const workflowNameSecondary = "BillingDisputeEscalation";
const commandId = "cmd-20260415-213928-67f2";
const commandIdSecondary = "cmd-20260415-204410-11ab";

const rootActorId =
  "actor://workflow-run/1626c177-917b-4fcc-a5ee-aa74a171b0d6/default/default/draft/run-20260415-213928/root";
const plannerActorId =
  "actor://workflow-run/1626c177-917b-4fcc-a5ee-aa74a171b0d6/default/default/draft/run-20260415-213928/planner";
const retrieverActorId =
  "actor://workflow-run/1626c177-917b-4fcc-a5ee-aa74a171b0d6/default/default/draft/run-20260415-213928/retriever";
const composerActorId =
  "actor://workflow-run/1626c177-917b-4fcc-a5ee-aa74a171b0d6/default/default/draft/run-20260415-213928/composer";
const escalationActorId =
  "actor://workflow-run/1626c177-917b-4fcc-a5ee-aa74a171b0d6/default/default/billing-review/run-20260415-204410/root";

const runNodeId = `run:${rootActorId}:${commandId}`;
const stepPlanId = `step:${rootActorId}:${commandId}:plan-intake`;
const stepRetrieveId = `step:${rootActorId}:${commandId}:retrieve-history`;
const stepDraftId = `step:${rootActorId}:${commandId}:draft-response`;
const stepEscalateId = `step:${rootActorId}:${commandId}:request-governance`;

const escalationRunNodeId = `run:${escalationActorId}:${commandIdSecondary}`;
const escalationStepReviewId = `step:${escalationActorId}:${commandIdSecondary}:review-claim`;
const escalationStepNotifyId = `step:${escalationActorId}:${commandIdSecondary}:notify-billing`;

function graphNode(
  nodeId: string,
  nodeType: string,
  updatedAt: string,
  properties: Record<string, string>,
) {
  return {
    nodeId,
    nodeType,
    properties,
    updatedAt,
  };
}

function graphEdge(
  edgeId: string,
  fromNodeId: string,
  toNodeId: string,
  edgeType: string,
  updatedAt: string,
  properties: Record<string, string> = {},
) {
  return {
    edgeId,
    edgeType,
    fromNodeId,
    properties,
    toNodeId,
    updatedAt,
  };
}

const sharedUpdatedAt = "2026-04-15T21:40:12Z";
const completedUpdatedAt = "2026-04-15T20:45:41Z";

const sharedGraphNodes = [
  graphNode(rootActorId, "Actor", sharedUpdatedAt, {
    role: "root-supervisor",
    scopeId,
    serviceId,
    workflowName,
  }),
  graphNode(plannerActorId, "Actor", sharedUpdatedAt, {
    role: "planner",
    scopeId,
    serviceId,
    workflowName,
  }),
  graphNode(retrieverActorId, "Actor", sharedUpdatedAt, {
    role: "history-retriever",
    scopeId,
    serviceId,
    workflowName,
  }),
  graphNode(composerActorId, "Actor", sharedUpdatedAt, {
    role: "response-composer",
    scopeId,
    serviceId,
    workflowName,
  }),
  graphNode(runNodeId, "WorkflowRun", sharedUpdatedAt, {
    commandId,
    input:
      "Customer reports duplicate charge and asks whether the dispute can be resolved without a refund freeze.",
    rootActorId,
    workflowName,
  }),
  graphNode(stepPlanId, "WorkflowStep", sharedUpdatedAt, {
    rootActorId,
    stepId: "plan-intake",
    stepType: "planner",
    success: "true",
    targetRole: "planner",
    workerId: plannerActorId,
  }),
  graphNode(stepRetrieveId, "WorkflowStep", sharedUpdatedAt, {
    rootActorId,
    stepId: "retrieve-history",
    stepType: "tool_call",
    success: "true",
    targetRole: "history-retriever",
    workerId: retrieverActorId,
  }),
  graphNode(stepDraftId, "WorkflowStep", sharedUpdatedAt, {
    rootActorId,
    stepId: "draft-response",
    stepType: "compose",
    success: "true",
    targetRole: "response-composer",
    workerId: composerActorId,
  }),
  graphNode(stepEscalateId, "WorkflowStep", sharedUpdatedAt, {
    rootActorId,
    stepId: "request-governance",
    stepType: "governance_gate",
    success: "",
    targetRole: "governance",
    workerId: "",
  }),
];

const sharedGraphEdges = [
  graphEdge("OWNS:ROOT-RUN", rootActorId, runNodeId, "OWNS", sharedUpdatedAt),
  graphEdge(
    "CONTAINS:PLAN",
    runNodeId,
    stepPlanId,
    "CONTAINS_STEP",
    sharedUpdatedAt,
    {
      stepId: "plan-intake",
      stepType: "planner",
    },
  ),
  graphEdge(
    "CONTAINS:RETRIEVE",
    runNodeId,
    stepRetrieveId,
    "CONTAINS_STEP",
    sharedUpdatedAt,
    {
      stepId: "retrieve-history",
      stepType: "tool_call",
    },
  ),
  graphEdge(
    "CONTAINS:DRAFT",
    runNodeId,
    stepDraftId,
    "CONTAINS_STEP",
    sharedUpdatedAt,
    {
      stepId: "draft-response",
      stepType: "compose",
    },
  ),
  graphEdge(
    "CONTAINS:ESCALATE",
    runNodeId,
    stepEscalateId,
    "CONTAINS_STEP",
    sharedUpdatedAt,
    {
      stepId: "request-governance",
      stepType: "governance_gate",
    },
  ),
  graphEdge("CHILD:ROOT-PLANNER", rootActorId, plannerActorId, "CHILD_OF", sharedUpdatedAt),
  graphEdge(
    "CHILD:PLANNER-RETRIEVER",
    plannerActorId,
    retrieverActorId,
    "CHILD_OF",
    sharedUpdatedAt,
  ),
  graphEdge(
    "CHILD:ROOT-COMPOSER",
    rootActorId,
    composerActorId,
    "CHILD_OF",
    sharedUpdatedAt,
  ),
];

const escalationGraphNodes = [
  graphNode(escalationActorId, "Actor", completedUpdatedAt, {
    role: "root-supervisor",
    scopeId,
    serviceId: serviceIdSecondary,
    workflowName: workflowNameSecondary,
  }),
  graphNode(escalationRunNodeId, "WorkflowRun", completedUpdatedAt, {
    commandId: commandIdSecondary,
    input:
      "Billing dispute escalated after payment processor rejected the first refund request.",
    rootActorId: escalationActorId,
    workflowName: workflowNameSecondary,
  }),
  graphNode(escalationStepReviewId, "WorkflowStep", completedUpdatedAt, {
    rootActorId: escalationActorId,
    stepId: "review-claim",
    stepType: "manual_review",
    success: "true",
    targetRole: "ops-reviewer",
    workerId: escalationActorId,
  }),
  graphNode(escalationStepNotifyId, "WorkflowStep", completedUpdatedAt, {
    rootActorId: escalationActorId,
    stepId: "notify-billing",
    stepType: "connector_call",
    success: "true",
    targetRole: "billing-connector",
    workerId: escalationActorId,
  }),
];

const escalationGraphEdges = [
  graphEdge(
    "OWNS:ESCALATION-RUN",
    escalationActorId,
    escalationRunNodeId,
    "OWNS",
    completedUpdatedAt,
  ),
  graphEdge(
    "CONTAINS:REVIEW",
    escalationRunNodeId,
    escalationStepReviewId,
    "CONTAINS_STEP",
    completedUpdatedAt,
    {
      stepId: "review-claim",
      stepType: "manual_review",
    },
  ),
  graphEdge(
    "CONTAINS:NOTIFY",
    escalationRunNodeId,
    escalationStepNotifyId,
    "CONTAINS_STEP",
    completedUpdatedAt,
    {
      stepId: "notify-billing",
      stepType: "connector_call",
    },
  ),
];

function buildSharedGraph(rootNodeId: string): WorkflowActorGraphSubgraph {
  return {
    edges: sharedGraphEdges,
    nodes: sharedGraphNodes,
    rootNodeId,
  };
}

const topologyMockRecords: TopologyMockRecord[] = [
  {
    actor: {
      description: "WorkflowRunGAgent[CustomerSupportTriage] · root supervisor",
      id: rootActorId,
      type: "WorkflowRunGAgent",
    },
    graph: buildSharedGraph(rootActorId),
    runId,
    scopeId,
    serviceId,
    snapshot: {
      actorId: rootActorId,
      completedSteps: 3,
      completionStatusValue: 0,
      lastCommandId: commandId,
      lastError: "",
      lastEventId: "evt-20260415-214012-41c1",
      lastOutput:
        "Draft escalation summary ready; governance confirmation is still pending before the customer-facing reply can be sent.",
      lastSuccess: true,
      lastUpdatedAt: sharedUpdatedAt,
      requestedSteps: 4,
      roleReplyCount: 3,
      stateVersion: 57,
      totalSteps: 6,
      workflowName,
    },
    timeline: [
      {
        agentId: rootActorId,
        data: {
          commandId,
          inputChannel: "service.invoke/chat",
          promptWindow: "last-3-messages",
        },
        eventType: "WorkflowStarted",
        message: "Workflow run accepted from service endpoint chat.",
        stage: "workflow.started",
        stepId: "session-start",
        stepType: "workflow_start",
        timestamp: "2026-04-15T21:39:28Z",
      },
      {
        agentId: plannerActorId,
        data: {
          assignedRole: "planner",
          workerId: plannerActorId,
        },
        eventType: "StepRequested",
        message: "Planner step created to classify the dispute and choose the next route.",
        stage: "step.requested",
        stepId: "plan-intake",
        stepType: "planner",
        timestamp: "2026-04-15T21:39:31Z",
      },
      {
        agentId: plannerActorId,
        data: {
          branch: "refund-review",
          confidence: "0.86",
          workerId: plannerActorId,
        },
        eventType: "StepCompleted",
        message: "Planner labeled the case as refund-review and requested payment history.",
        stage: "step.completed",
        stepId: "plan-intake",
        stepType: "planner",
        timestamp: "2026-04-15T21:39:36Z",
      },
      {
        agentId: retrieverActorId,
        data: {
          connector: "chrono-storage.payment-history",
          hits: "3",
          workerId: retrieverActorId,
        },
        eventType: "RoleReplyObserved",
        message: "History retriever returned three payment attempts and one recent chargeback note.",
        stage: "role.replied",
        stepId: "retrieve-history",
        stepType: "tool_call",
        timestamp: "2026-04-15T21:39:44Z",
      },
      {
        agentId: composerActorId,
        data: {
          workerId: composerActorId,
          draftVersion: "2",
        },
        eventType: "StepCompleted",
        message: "Composer prepared a customer-facing draft with refund hold explanation.",
        stage: "step.completed",
        stepId: "draft-response",
        stepType: "compose",
        timestamp: "2026-04-15T21:39:57Z",
      },
      {
        agentId: rootActorId,
        data: {
          gate: "governance.confirmation",
          waitingFor: "finance-approval",
        },
        eventType: "WorkflowWaiting",
        message: "Run is waiting on governance confirmation before activation of the final reply.",
        stage: "workflow.waiting",
        stepId: "request-governance",
        stepType: "governance_gate",
        timestamp: "2026-04-15T21:40:12Z",
      },
    ],
    workflowName,
  },
  {
    actor: {
      description: "WorkflowRunGAgent[CustomerSupportTriage] · planner role",
      id: plannerActorId,
      type: "WorkflowRunGAgent",
    },
    graph: buildSharedGraph(plannerActorId),
    runId,
    scopeId,
    serviceId,
    snapshot: {
      actorId: plannerActorId,
      completedSteps: 1,
      completionStatusValue: 1,
      lastCommandId: commandId,
      lastError: "",
      lastEventId: "evt-20260415-213936-1920",
      lastOutput:
        "Classification completed: refund-review. Next action is retrieve-history.",
      lastSuccess: true,
      lastUpdatedAt: "2026-04-15T21:39:36Z",
      requestedSteps: 1,
      roleReplyCount: 1,
      stateVersion: 18,
      totalSteps: 1,
      workflowName,
    },
    timeline: [
      {
        agentId: plannerActorId,
        data: {
          workerId: plannerActorId,
        },
        eventType: "RoleAssigned",
        message: "Planner actor was assigned to the intake classification request.",
        stage: "role.assigned",
        stepId: "plan-intake",
        stepType: "planner",
        timestamp: "2026-04-15T21:39:31Z",
      },
      {
        agentId: plannerActorId,
        data: {
          branch: "refund-review",
          confidence: "0.86",
        },
        eventType: "RoleReplyObserved",
        message: "Planner returned the classification result and routing branch.",
        stage: "role.replied",
        stepId: "plan-intake",
        stepType: "planner",
        timestamp: "2026-04-15T21:39:36Z",
      },
    ],
    workflowName,
  },
  {
    actor: {
      description: "WorkflowRunGAgent[CustomerSupportTriage] · history retriever",
      id: retrieverActorId,
      type: "WorkflowRunGAgent",
    },
    graph: buildSharedGraph(retrieverActorId),
    runId,
    scopeId,
    serviceId,
    snapshot: {
      actorId: retrieverActorId,
      completedSteps: 1,
      completionStatusValue: 1,
      lastCommandId: commandId,
      lastError: "",
      lastEventId: "evt-20260415-213944-772c",
      lastOutput:
        "Found 3 payment attempts, 1 chargeback note, 0 prior manual overrides.",
      lastSuccess: true,
      lastUpdatedAt: "2026-04-15T21:39:44Z",
      requestedSteps: 1,
      roleReplyCount: 1,
      stateVersion: 12,
      totalSteps: 1,
      workflowName,
    },
    timeline: [
      {
        agentId: retrieverActorId,
        data: {
          connector: "chrono-storage.payment-history",
          queryWindow: "90d",
        },
        eventType: "ConnectorInvoked",
        message: "Retriever called the payment history connector for the previous 90 days.",
        stage: "connector.invoked",
        stepId: "retrieve-history",
        stepType: "tool_call",
        timestamp: "2026-04-15T21:39:40Z",
      },
      {
        agentId: retrieverActorId,
        data: {
          chargebackNote: "present",
          hits: "3",
        },
        eventType: "RoleReplyObserved",
        message: "Retriever summarized payment evidence and attached connector findings.",
        stage: "role.replied",
        stepId: "retrieve-history",
        stepType: "tool_call",
        timestamp: "2026-04-15T21:39:44Z",
      },
    ],
    workflowName,
  },
  {
    actor: {
      description: "WorkflowRunGAgent[CustomerSupportTriage] · response composer",
      id: composerActorId,
      type: "WorkflowRunGAgent",
    },
    graph: buildSharedGraph(composerActorId),
    runId,
    scopeId,
    serviceId,
    snapshot: {
      actorId: composerActorId,
      completedSteps: 1,
      completionStatusValue: 0,
      lastCommandId: commandId,
      lastError: "",
      lastEventId: "evt-20260415-213957-aa21",
      lastOutput:
        "Prepared response draft v2 and requested governance confirmation for the refund hold wording.",
      lastSuccess: true,
      lastUpdatedAt: "2026-04-15T21:39:57Z",
      requestedSteps: 2,
      roleReplyCount: 1,
      stateVersion: 21,
      totalSteps: 2,
      workflowName,
    },
    timeline: [
      {
        agentId: composerActorId,
        data: {
          tone: "empathetic",
          workerId: composerActorId,
        },
        eventType: "StepRequested",
        message: "Composer was asked to write a customer-safe explanation and next steps.",
        stage: "step.requested",
        stepId: "draft-response",
        stepType: "compose",
        timestamp: "2026-04-15T21:39:48Z",
      },
      {
        agentId: composerActorId,
        data: {
          draftVersion: "2",
          policyGate: "finance-approval",
        },
        eventType: "WorkflowWaiting",
        message: "Composer completed the draft but is waiting on finance approval language.",
        stage: "workflow.waiting",
        stepId: "request-governance",
        stepType: "governance_gate",
        timestamp: "2026-04-15T21:39:57Z",
      },
    ],
    workflowName,
  },
  {
    actor: {
      description: "WorkflowRunGAgent[BillingDisputeEscalation] · closed run",
      id: escalationActorId,
      type: "WorkflowRunGAgent",
    },
    graph: {
      edges: escalationGraphEdges,
      nodes: escalationGraphNodes,
      rootNodeId: escalationActorId,
    },
    runId: runIdSecondary,
    scopeId,
    serviceId: serviceIdSecondary,
    snapshot: {
      actorId: escalationActorId,
      completedSteps: 2,
      completionStatusValue: 1,
      lastCommandId: commandIdSecondary,
      lastError: "",
      lastEventId: "evt-20260415-204541-7755",
      lastOutput:
        "Billing team notified and dispute moved to manual queue without blocking new captures.",
      lastSuccess: true,
      lastUpdatedAt: completedUpdatedAt,
      requestedSteps: 2,
      roleReplyCount: 1,
      stateVersion: 34,
      totalSteps: 2,
      workflowName: workflowNameSecondary,
    },
    timeline: [
      {
        agentId: escalationActorId,
        data: {
          commandId: commandIdSecondary,
          escalationReason: "processor_reject",
        },
        eventType: "WorkflowStarted",
        message: "Escalation workflow started after refund request failed at the processor.",
        stage: "workflow.started",
        stepId: "review-claim",
        stepType: "manual_review",
        timestamp: "2026-04-15T20:44:10Z",
      },
      {
        agentId: escalationActorId,
        data: {
          reviewer: "billing-ops",
          severity: "medium",
        },
        eventType: "StepCompleted",
        message: "Billing ops reviewed the dispute and routed it to a manual resolution queue.",
        stage: "step.completed",
        stepId: "review-claim",
        stepType: "manual_review",
        timestamp: "2026-04-15T20:44:46Z",
      },
      {
        agentId: escalationActorId,
        data: {
          connector: "billing-connector",
          ticketId: "BL-4421",
        },
        eventType: "WorkflowCompleted",
        message: "Billing team notified successfully; run closed with no blocking errors.",
        stage: "workflow.completed",
        stepId: "notify-billing",
        stepType: "connector_call",
        timestamp: "2026-04-15T20:45:41Z",
      },
    ],
    workflowName: workflowNameSecondary,
  },
];

export const topologyMockEdgeTypes = ["OWNS", "CONTAINS_STEP", "CHILD_OF"] as const;

export const topologyMockDefaultActorId = topologyMockRecords[0].actor.id;

export const topologyMockRecordMap = new Map(
  topologyMockRecords.map((record) => [record.actor.id, record] as const),
);

export { topologyMockRecords };
