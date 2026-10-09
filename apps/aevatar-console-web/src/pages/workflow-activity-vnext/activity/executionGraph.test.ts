import type {
  WorkflowActivityRunDetail,
  WorkflowActivityRunGraph,
  WorkflowActivityStep,
} from '@/shared/models/workflowActivity';
import { buildExecutionGraph, reconcileExecutionGraph } from './executionGraph';

function buildStep(
  overrides: Partial<WorkflowActivityStep> &
    Pick<WorkflowActivityStep, 'stepId'>,
): WorkflowActivityStep {
  return {
    branchKey: '',
    completedAtUtc: null,
    durationMs: null,
    error: '',
    nextStepId: '',
    outputPreview: '',
    requestParameters: {},
    requestedAtUtc: null,
    stepType: 'llm_call',
    success: null,
    suspensionContent: '',
    suspensionPrompt: '',
    suspensionTimeoutSeconds: null,
    suspensionType: '',
    targetRole: 'operator',
    toolApproval: null,
    usage: {
      completionTokens: 0,
      cost: 0,
      promptTokens: 0,
      totalTokens: 0,
    },
    ...overrides,
  };
}

const rootStep = buildStep({
  completedAtUtc: '2026-09-03T10:00:01Z',
  durationMs: 1_000,
  nextStepId: 'step-review',
  requestedAtUtc: '2026-09-03T10:00:00Z',
  stepId: 'step-root',
  success: true,
});
const reviewStep = buildStep({
  nextStepId: 'step-publish',
  requestedAtUtc: '2026-09-03T10:00:02Z',
  stepId: 'step-review',
  stepType: 'human_approval',
});
const publishStep = buildStep({
  requestedAtUtc: '2026-09-03T10:00:03Z',
  stepId: 'step-publish',
  stepType: 'connector_call',
});

function buildDetail(
  steps: readonly WorkflowActivityStep[],
): WorkflowActivityRunDetail {
  return {
    diagnostics: [],
    finalError: '',
    finalOutput: '',
    input: 'Publish the approved release',
    lineage: {
      availability: 0,
      retryFork: {
        attempt: 0,
        availability: 0,
        childRuns: [],
        originalRunId: '',
        sourceRunId: '',
        startAtStepId: '',
      },
      subWorkflow: {
        availability: 0,
        childRuns: [],
        depth: 0,
        parentActorId: '',
        parentRunId: '',
        parentStepId: '',
        rootRunId: '',
      },
      unavailableReason: '',
    },
    recoveryCapability: {
      retryFailedStep: {
        eligibility: 0,
        mayIncurModelOrToolCost: false,
        recommendedActions: [],
        reusesPriorStepOutputs: false,
        startingStepId: '',
        unavailableReason: '',
        unavailableReasonCode: 0,
      },
      runAgain: {
        eligibility: 0,
        mayIncurModelOrToolCost: false,
        recommendedActions: [],
        reusesPriorStepOutputs: false,
        startingStepId: '',
        unavailableReason: '',
        unavailableReasonCode: 0,
      },
      workflowDefinitionRevisionId: 'revision-alpha',
      workflowDefinitionVersion: 4,
    },
    statistics: {
      completedSteps: 1,
      requestedSteps: 2,
      roleReplyCount: 0,
      stepTypeCounts: {
        connector_call: 1,
        human_approval: 1,
        llm_call: 1,
      },
      totalSteps: 3,
    },
    steps,
    summary: {
      runId: 'run-alpha',
      runOrigin: 'published',
      scopeId: 'scope-alpha',
      startedAtUtc: '2026-09-03T10:00:00Z',
      stateVersion: 12,
      status: 'running',
      success: null,
      updatedAtUtc: '2026-09-03T10:00:03Z',
      workflowName: 'Release workflow',
    },
    timeline: [],
    usageTotals: {
      completionTokens: 0,
      cost: 0,
      promptTokens: 0,
      totalTokens: 0,
    },
  };
}

const graph: WorkflowActivityRunGraph = {
  edges: [
    {
      branchKey: '',
      edgeId: 'edge-entry-review',
      edgeType: 'next',
      fromNodeId: 'node-entry',
      toNodeId: 'node-review',
    },
    {
      branchKey: 'approved',
      edgeId: 'edge-review-output',
      edgeType: 'branch',
      fromNodeId: 'node-review',
      toNodeId: 'node-output',
    },
  ],
  nodes: [
    { nodeId: 'node-entry', nodeType: 'step', stepId: 'step-root' },
    { nodeId: 'node-review', nodeType: 'step', stepId: 'step-review' },
    { nodeId: 'node-output', nodeType: 'step', stepId: 'step-publish' },
  ],
  rootNodeId: 'node-entry',
};

describe('execution graph', () => {
  it('replaces only the status-changed node and ordered step while preserving unchanged graph references', () => {
    const initialDetail = buildDetail([rootStep, reviewStep, publishStep]);
    const previous = buildExecutionGraph(initialDetail, graph);
    const failedReviewStep = {
      ...reviewStep,
      success: false,
    };
    const next = buildExecutionGraph(
      buildDetail([rootStep, failedReviewStep, publishStep]),
      graph,
    );

    const reconciled = reconcileExecutionGraph(previous, next);

    expect(reconciled.nodes).not.toBe(previous.nodes);
    expect(reconciled.nodes[0]).toBe(previous.nodes[0]);
    expect(reconciled.nodes[0].data).toBe(previous.nodes[0].data);
    expect(reconciled.nodes[1]).not.toBe(previous.nodes[1]);
    expect(reconciled.nodes[1].data).not.toBe(previous.nodes[1].data);
    expect(reconciled.nodes[1].data.executionStatus).toBe('failed');
    expect(reconciled.nodes[2]).toBe(previous.nodes[2]);
    expect(reconciled.nodes[2].data).toBe(previous.nodes[2].data);
    expect(reconciled.edges).toBe(previous.edges);
    expect(reconciled.edges[0]).toBe(previous.edges[0]);
    expect(reconciled.edges[1]).toBe(previous.edges[1]);
    expect(reconciled.orderedSteps).not.toBe(previous.orderedSteps);
    expect(reconciled.orderedSteps[0]).toBe(previous.orderedSteps[0]);
    expect(reconciled.orderedSteps[1]).toBe(failedReviewStep);
    expect(reconciled.orderedSteps[2]).toBe(previous.orderedSteps[2]);
  });

  it('keeps graph elements and ordered steps stable when selection changes outside graph data', () => {
    const detail = buildDetail([rootStep, reviewStep, publishStep]);
    let selectedStepId = 'step-review';
    const previous = buildExecutionGraph(detail, graph);

    selectedStepId = 'step-publish';
    const reconciled = reconcileExecutionGraph(
      previous,
      buildExecutionGraph(detail, graph),
    );

    expect(selectedStepId).toBe('step-publish');
    expect(buildExecutionGraph).toHaveLength(2);
    expect(reconciled.nodes).toBe(previous.nodes);
    expect(reconciled.nodes[0]).toBe(previous.nodes[0]);
    expect(reconciled.nodes[0].data).toBe(previous.nodes[0].data);
    expect(reconciled.nodes[1]).toBe(previous.nodes[1]);
    expect(reconciled.nodes[2]).toBe(previous.nodes[2]);
    expect(reconciled.nodes.every((node) => !node.data.executionFocused)).toBe(
      true,
    );
    expect(reconciled.edges).toBe(previous.edges);
    expect(reconciled.orderedSteps).toBe(previous.orderedSteps);
  });
});
