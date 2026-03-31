import type { AGUIEvent } from '@aevatar-react-sdk/types';
import type {
  WorkflowActorGraphEnrichedSnapshot,
  WorkflowActorTimelineItem,
} from '@/shared/models/runtime/actors';
import {
  buildMissionRuntimePlaceholderSnapshot,
  buildMissionSnapshotFromRuntime,
} from './runtimeAdapter';

describe('Mission Control runtimeAdapter', () => {
  it('derives waiting approval and tool calls from committed runtime artifacts', () => {
    const graph: WorkflowActorGraphEnrichedSnapshot = {
      snapshot: {
        actorId: 'root-actor',
        completedSteps: 1,
        completionStatusValue: 0,
        lastCommandId: 'run-1',
        lastError: '',
        lastEventId: 'evt-4',
        lastOutput: 'buy 600519 with guarded size',
        lastSuccess: null,
        lastUpdatedAt: '2026-03-30T08:00:20.000Z',
        requestedSteps: 2,
        roleReplyCount: 1,
        stateVersion: 7,
        totalSteps: 3,
        workflowName: 'wf-a-share',
      },
      subgraph: {
        rootNodeId: 'run:root-actor:run-1',
        nodes: [
          {
            nodeId: 'root-actor',
            nodeType: 'Actor',
            updatedAt: '2026-03-30T08:00:20.000Z',
            properties: {
              workflowName: 'wf-a-share',
            },
          },
          {
            nodeId: 'research-agent',
            nodeType: 'Actor',
            updatedAt: '2026-03-30T08:00:18.000Z',
            properties: {
              workflowName: 'wf-a-share',
            },
          },
          {
            nodeId: 'run:root-actor:run-1',
            nodeType: 'WorkflowRun',
            updatedAt: '2026-03-30T08:00:20.000Z',
            properties: {
              commandId: 'run-1',
              input: 'Analyze Kweichow Moutai and size the trade.',
              rootActorId: 'root-actor',
              workflowName: 'wf-a-share',
            },
          },
          {
            nodeId: 'step:root-actor:run-1:research',
            nodeType: 'WorkflowStep',
            updatedAt: '2026-03-30T08:00:16.000Z',
            properties: {
              commandId: 'run-1',
              rootActorId: 'root-actor',
              stepId: 'research',
              stepType: 'llm_call',
              success: 'true',
              targetRole: 'strategy_researcher',
              workerId: 'research-agent',
            },
          },
          {
            nodeId: 'step:root-actor:run-1:approval',
            nodeType: 'WorkflowStep',
            updatedAt: '2026-03-30T08:00:20.000Z',
            properties: {
              commandId: 'run-1',
              rootActorId: 'root-actor',
              stepId: 'approval',
              stepType: 'human_approval',
              success: '',
              targetRole: 'approval_board',
              workerId: '',
            },
          },
        ],
        edges: [
          {
            edgeId: 'edge-1',
            edgeType: 'OWNS',
            fromNodeId: 'root-actor',
            properties: {},
            toNodeId: 'run:root-actor:run-1',
            updatedAt: '2026-03-30T08:00:20.000Z',
          },
          {
            edgeId: 'edge-2',
            edgeType: 'CONTAINS_STEP',
            fromNodeId: 'run:root-actor:run-1',
            properties: {
              stepId: 'research',
              stepType: 'llm_call',
            },
            toNodeId: 'step:root-actor:run-1:research',
            updatedAt: '2026-03-30T08:00:16.000Z',
          },
          {
            edgeId: 'edge-3',
            edgeType: 'CONTAINS_STEP',
            fromNodeId: 'run:root-actor:run-1',
            properties: {
              stepId: 'approval',
              stepType: 'human_approval',
            },
            toNodeId: 'step:root-actor:run-1:approval',
            updatedAt: '2026-03-30T08:00:20.000Z',
          },
          {
            edgeId: 'edge-4',
            edgeType: 'CHILD_OF',
            fromNodeId: 'root-actor',
            properties: {},
            toNodeId: 'research-agent',
            updatedAt: '2026-03-30T08:00:18.000Z',
          },
        ],
      },
    };

    const timeline: WorkflowActorTimelineItem[] = [
      {
        agentId: 'root-actor',
        data: {},
        eventType: 'WorkflowRunExecutionStartedEvent',
        message: 'command=run-1',
        stage: 'workflow.start',
        stepId: '',
        stepType: '',
        timestamp: '2026-03-30T08:00:10.000Z',
      },
      {
        agentId: 'root-actor',
        data: {
          temperature: '0.2',
        },
        eventType: 'StepRequestEvent',
        message: 'research (llm_call)',
        stage: 'step.request',
        stepId: 'research',
        stepType: 'llm_call',
        timestamp: '2026-03-30T08:00:12.000Z',
      },
      {
        agentId: 'research-agent',
        data: {
          call_id: 'call-1',
          endpoint: 'market.quote',
          latency_ms: '128',
          tool_name: 'market.quote',
        },
        eventType: 'WorkflowRoleReplyRecordedEvent',
        message: 'market.quote',
        stage: 'tool.call',
        stepId: 'research',
        stepType: 'llm_call',
        timestamp: '2026-03-30T08:00:14.000Z',
      },
      {
        agentId: 'research-agent',
        data: {
          session_id: 'sess-1',
        },
        eventType: 'WorkflowRoleReplyRecordedEvent',
        message: 'strategy_researcher',
        stage: 'role.reply',
        stepId: '',
        stepType: '',
        timestamp: '2026-03-30T08:00:15.000Z',
      },
      {
        agentId: 'research-agent',
        data: {
          confidence: '0.76',
        },
        eventType: 'StepCompletedEvent',
        message: 'research (success)',
        stage: 'step.completed',
        stepId: 'research',
        stepType: 'llm_call',
        timestamp: '2026-03-30T08:00:16.000Z',
      },
      {
        agentId: 'root-actor',
        data: {
          prompt: 'Approve guarded buy on 600519 before order routing.',
        },
        eventType: 'WorkflowSuspendedEvent',
        message: 'approval (human_approval)',
        stage: 'workflow.suspended',
        stepId: 'approval',
        stepType: 'human_approval',
        timestamp: '2026-03-30T08:00:20.000Z',
      },
    ];

    const snapshot = buildMissionSnapshotFromRuntime({
      connectionStatus: 'live',
      nowMs: Date.parse('2026-03-30T08:00:22.000Z'),
      recentEvents: [] as AGUIEvent[],
      resources: {
        artifacts: {
          fetchedAtMs: Date.parse('2026-03-30T08:00:21.000Z'),
          graph,
          timeline,
        },
        session: {
          runId: 'run-1',
          status: 'running',
        },
      },
      routeContext: {
        runId: 'run-1',
        scopeId: 'scope-a',
        serviceId: 'svc-1',
      },
    });

    expect(snapshot.summary.status).toBe('waiting_approval');
    expect(snapshot.summary.observationStatus).toBe('streaming');
    expect(snapshot.summary.activeStageLabel).toBe('Waiting for approval');
    expect(snapshot.summary.scriptEvolutionStatus).toBeUndefined();
    expect(snapshot.intervention).toMatchObject({
      kind: 'human_approval',
      nodeId: 'step:root-actor:run-1:approval',
      stepId: 'approval',
    });

    const researchActor = snapshot.nodes.find((node) => node.id === 'research-agent');
    expect(researchActor?.toolCalls).toHaveLength(1);
    expect(researchActor?.toolCalls[0]).toMatchObject({
      endpoint: 'market.quote',
      latencyMs: 128,
      toolName: 'market.quote',
    });

    const approvalStep = snapshot.nodes.find(
      (node) => node.id === 'step:root-actor:run-1:approval',
    );
    expect(approvalStep?.status).toBe('waiting');
    expect(approvalStep?.reasoningChain[0]).toMatchObject({
      title: 'Workflow suspended',
    });
  });

  it('builds an honest empty snapshot when runtime context is missing', () => {
    const snapshot = buildMissionRuntimePlaceholderSnapshot({
      connectionStatus: 'idle',
      context: {},
      nowMs: Date.parse('2026-03-30T08:00:22.000Z'),
    });

    expect(snapshot.summary.status).toBe('idle');
    expect(snapshot.summary.observationStatus).toBe('unavailable');
    expect(snapshot.summary.activeStageLabel).toBe('Awaiting runtime context');
    expect(snapshot.summary.scriptEvolutionStatus).toBeUndefined();
    expect(snapshot.nodes).toHaveLength(0);
    expect(snapshot.edges).toHaveLength(0);
  });
});
