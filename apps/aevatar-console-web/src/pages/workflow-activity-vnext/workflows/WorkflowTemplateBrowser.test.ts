import type { WorkflowTemplateDetail } from '@/shared/models/runtime/workflowTemplates';
import { buildTemplatePreviewGraph } from './WorkflowTemplateBrowser';

function stepBranches(stepId: string): Record<string, string> {
  return stepId === 'classify' ? { approved: 'publish' } : {};
}

function createDetailWithImplicitEdges(): WorkflowTemplateDetail {
  return {
    template: {
      templateId: 'template-implicit-sequence',
      displayName: 'Implicit sequence',
      description: 'Three connected steps.',
      defaultDraftName: 'Implicit sequence',
      authorityStateVersion: 4,
      stepCount: 4,
      requiredConnections: [],
      requiresLlmProvider: false,
      freshness: {
        projectionWatermark: '2026-08-20T00:00:00Z',
        lastEventId: 'event-template-4',
        versionSemantics: 'workflow-catalog-authority-state-version',
      },
    },
    yaml: 'name: implicit_sequence\n',
    definition: {
      name: 'implicit_sequence',
      description: 'Connected steps with a nested child.',
      closedWorldMode: true,
      roles: [],
      steps: ['capture', 'classify', 'publish', 'child'].map((id) => ({
        id,
        type: 'assign',
        targetRole: '',
        parameters: {},
        next: '',
        branches: stepBranches(id),
        children:
          id === 'capture'
            ? [{ id: 'child', type: 'llm_call', targetRole: 'assistant' }]
            : [],
      })),
    },
    edges: [
      { from: 'capture', to: 'classify', label: '' },
      { from: 'classify', to: 'publish', label: 'approved' },
      { from: 'capture', to: 'child', label: 'child' },
    ],
    authorityStateVersion: 4,
    freshness: {
      projectionWatermark: '2026-08-20T00:00:00Z',
      lastEventId: 'event-template-4',
      versionSemantics: 'workflow-catalog-authority-state-version',
    },
  };
}

describe('buildTemplatePreviewGraph', () => {
  it('uses authoritative detail edges for both visible connections and layout', () => {
    const graph = buildTemplatePreviewGraph(createDetailWithImplicitEdges());
    const xByStepId = new Map(
      graph.nodes.map((node) => [node.data.stepId, node.position.x]),
    );

    expect(graph.edges).toEqual([
      expect.objectContaining({
        label: undefined,
        source: 'step:capture',
        target: 'step:classify',
      }),
      expect.objectContaining({
        label: 'approved',
        source: 'step:classify',
        style: expect.objectContaining({ stroke: '#8B5CF6' }),
        target: 'step:publish',
      }),
      expect.objectContaining({
        label: 'child',
        source: 'step:capture',
        style: expect.objectContaining({ stroke: '#2F6FEC' }),
        target: 'step:child',
      }),
    ]);
    expect(xByStepId.get('capture')).toBeLessThan(
      xByStepId.get('classify') as number,
    );
    expect(xByStepId.get('classify')).toBeLessThan(
      xByStepId.get('publish') as number,
    );
    expect(
      graph.nodes.find((node) => node.data.stepId === 'capture')?.data
        .branchCount,
    ).toBe(0);
    expect(
      graph.nodes.find((node) => node.data.stepId === 'classify')?.data
        .branchCount,
    ).toBe(1);
  });
});
