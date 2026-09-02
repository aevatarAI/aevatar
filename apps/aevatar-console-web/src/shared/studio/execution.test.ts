import type { Node } from '@xyflow/react';
import {
  buildExecutionTrace,
  buildWorkflowExecutionNodeSnapshots,
  decorateNodesForExecution,
  type ExecutionTrace,
} from './execution';
import type { StudioGraphNodeData } from './graph';
import type { StudioExecutionDetail } from './models';

function createExecutionDetail(
  frames: StudioExecutionDetail['frames'],
): StudioExecutionDetail {
  return {
    actorId: 'actor-1',
    completedAtUtc: '2026-06-08T00:00:03Z',
    error: null,
    executionId: 'execution-1',
    frames,
    output: '',
    prompt: 'Run the workflow',
    serviceId: null,
    startedAtUtc: '2026-06-08T00:00:00Z',
    status: 'succeeded',
    workflowName: 'Workflow Alpha',
  };
}

describe('buildExecutionTrace', () => {
  it('uses business output text for run-finished logs instead of raw result JSON', () => {
    const trace = buildExecutionTrace(
      createExecutionDetail([
        {
          receivedAtUtc: '2026-06-08T00:00:01Z',
          payload: JSON.stringify({
            runFinished: {
              result: {
                output: 'Final answer',
                debug: {
                  stateVersion: 7,
                },
              },
            },
          }),
        },
      ]),
    );

    const outputLog = trace?.logs.find((log) => log.category === 'output');
    expect(outputLog?.clipboardText).toBe('Final answer');
    expect(outputLog?.previewText).toBe('Final answer');
    expect(outputLog?.payloadText).toContain('"stateVersion": 7');
  });
});

describe('buildWorkflowExecutionNodeSnapshots', () => {
  it('builds the submitted node snapshot directly from the workflow document', () => {
    const snapshots = buildWorkflowExecutionNodeSnapshots({
      name: 'Workflow Alpha',
      steps: [
        {
          id: ' triage ',
          type: 'llm_call',
          targetRole: ' assistant ',
        },
        {
          id: 'validate',
          originalType: 'workflow_yaml_validate',
          target_role: ' reviewer ',
        },
        {
          id: '',
          type: 'emit',
        },
      ],
    });

    expect(snapshots).toEqual([
      {
        stepId: 'triage',
        stepType: 'llm_call',
        subtitle: 'LLM call',
        targetRole: 'assistant',
        title: 'triage',
      },
      {
        stepId: 'validate',
        stepType: 'workflow_yaml_validate',
        subtitle: 'Workflow YAML validation',
        targetRole: 'reviewer',
        title: 'validate',
      },
    ]);
  });
});

describe('decorateNodesForExecution', () => {
  it('keeps workflow editor nodes draggable while applying execution status', () => {
    const nodes: Array<Node<StudioGraphNodeData>> = [
      {
        id: 'step:draft',
        position: { x: 120, y: 80 },
        data: {
          branchCount: 0,
          kind: 'step',
          label: 'draft',
          parametersSummary: 'instruction: Draft report',
          stepId: 'draft',
          stepType: 'llm_call',
          subtitle: 'LLM call',
          targetRole: 'analyst',
          title: 'draft',
        },
        type: 'studioWorkflowNode',
      },
    ];
    const trace: ExecutionTrace = {
      defaultLogIndex: null,
      latestStepId: 'draft',
      logs: [],
      stepStates: new Map([
        [
          'draft',
          {
            branchKey: '',
            completedAt: null,
            error: '',
            nextStepId: '',
            startedAt: '2026-06-08T00:00:01Z',
            status: 'active',
            stepId: 'draft',
            stepType: 'llm_call',
            success: null,
            targetRole: 'analyst',
          },
        ],
      ]),
      traversedEdges: new Set(),
    };

    const decorated = decorateNodesForExecution(nodes, trace, null);

    expect(decorated[0]?.draggable).toBeUndefined();
    expect(decorated[0]?.selectable).toBe(true);
    expect(decorated[0]?.data.executionStatus).toBe('active');
  });
});
