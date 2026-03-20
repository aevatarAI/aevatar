import {
  buildTimelineRows,
  deriveSubgraphFromEdges,
  filterTimelineRows,
  type ActorTimelineFilters,
} from './actorPresentation';

function createFilters(
  overrides?: Partial<ActorTimelineFilters>,
): ActorTimelineFilters {
  return {
    stages: [],
    eventTypes: [],
    stepTypes: [],
    query: '',
    errorsOnly: false,
    ...overrides,
  };
}

describe('actorPresentation', () => {
  it('builds timeline rows with derived status and data summary', () => {
    const rows = buildTimelineRows([
      {
        timestamp: '2026-03-12T10:00:00Z',
        stage: 'workflow.failed',
        message: 'Step failed',
        agentId: 'actor-1',
        stepId: 'review',
        stepType: 'human_input',
        eventType: 'run.error',
        data: {
          reason: 'timeout',
        },
      },
    ]);

    expect(rows).toHaveLength(1);
    expect(rows[0].timelineStatus).toBe('error');
    expect(rows[0].dataSummary).toContain('timeout');
  });

  it('filters timeline rows by stage, event type, query, and errors-only', () => {
    const rows = buildTimelineRows([
      {
        timestamp: '2026-03-12T10:00:00Z',
        stage: 'workflow.start',
        message: 'Started actor run',
        agentId: 'actor-1',
        stepId: 'start',
        stepType: 'llm_call',
        eventType: 'run.started',
        data: {},
      },
      {
        timestamp: '2026-03-12T10:01:00Z',
        stage: 'workflow.failed',
        message: 'Approval rejected',
        agentId: 'actor-1',
        stepId: 'approve',
        stepType: 'human_approval',
        eventType: 'run.error',
        data: {
          approver: 'ops',
        },
      },
    ]);

    const filtered = filterTimelineRows(
      rows,
      createFilters({
        stages: ['workflow.failed'],
        eventTypes: ['run.error'],
        stepTypes: ['human_approval'],
        query: 'ops',
        errorsOnly: true,
      }),
    );

    expect(filtered).toHaveLength(1);
    expect(filtered[0].stepId).toBe('approve');
  });

  it('derives a synthetic subgraph from edges-only payloads', () => {
    const subgraph = deriveSubgraphFromEdges(
      [
        {
          edgeId: 'edge-1',
          fromNodeId: 'actor-1',
          toNodeId: 'actor-2',
          edgeType: 'CHILD_OF',
          updatedAt: '',
          properties: {},
        },
        {
          edgeId: 'edge-2',
          fromNodeId: 'actor-2',
          toNodeId: 'actor-3',
          edgeType: 'OWNS',
          updatedAt: '',
          properties: {},
        },
      ],
      'actor-1',
    );

    expect(subgraph.rootNodeId).toBe('actor-1');
    expect(subgraph.nodes.map((node) => node.nodeId)).toEqual([
      'actor-1',
      'actor-2',
      'actor-3',
    ]);
    expect(subgraph.edges).toHaveLength(2);
  });
});
