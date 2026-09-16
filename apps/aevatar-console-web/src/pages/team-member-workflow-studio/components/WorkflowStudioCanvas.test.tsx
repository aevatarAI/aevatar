import { render } from '@testing-library/react';
import * as React from 'react';
import WorkflowStudioCanvas from './WorkflowStudioCanvas';

type GraphCanvasProps = {
  readonly autoFitKey?: string;
  readonly edges: readonly unknown[];
  readonly nodes: readonly unknown[];
};

const mockGraphCanvas = jest.fn((_props: GraphCanvasProps) => null);

jest.mock('@/shared/graphs/GraphCanvas', () => ({
  __esModule: true,
  default: (props: GraphCanvasProps) => mockGraphCanvas(props),
}));

const nodes = [
  {
    data: {
      branchCount: 0,
      kind: 'step',
      label: 'Start',
      parametersSummary: '',
      stepId: 'step-start',
      stepType: 'start',
      subtitle: '',
      targetRole: '',
      title: 'Start',
    },
    id: 'step-start',
    position: { x: 0, y: 0 },
    type: 'studio',
  },
] as const;

const edges = [
  {
    data: { kind: 'next' },
    id: 'edge-start-end',
    source: 'step-start',
    target: 'step-end',
  },
] as const;

describe('WorkflowStudioCanvas', () => {
  beforeEach(() => {
    mockGraphCanvas.mockClear();
  });

  it('preserves graph collection identity and fit key through unrelated rerenders', () => {
    const { rerender } = render(
      <WorkflowStudioCanvas edges={edges} nodes={nodes} />,
    );
    const firstProps = mockGraphCanvas.mock.calls[0]?.[0];

    rerender(
      <WorkflowStudioCanvas
        edges={edges}
        nodes={nodes}
        selectedNodeId="step-start"
      />,
    );
    const secondProps = mockGraphCanvas.mock.calls[1]?.[0];

    expect(firstProps).toBeDefined();
    expect(secondProps).toBeDefined();
    expect(secondProps?.nodes).toBe(nodes);
    expect(secondProps?.edges).toBe(edges);
    expect(secondProps?.autoFitKey).toBe(firstProps?.autoFitKey);

    rerender(
      <WorkflowStudioCanvas
        edges={edges}
        emptyDescription="Updated empty description"
        nodes={nodes}
        selectedNodeId="step-start"
      />,
    );
    const thirdProps = mockGraphCanvas.mock.calls[2]?.[0];

    expect(thirdProps?.nodes).toBe(nodes);
    expect(thirdProps?.edges).toBe(edges);
    expect(thirdProps?.autoFitKey).toBe(firstProps?.autoFitKey);
  });

  it('changes the fit key when ordered node or edge identities change', () => {
    const { rerender } = render(
      <WorkflowStudioCanvas edges={edges} nodes={nodes} />,
    );
    const initialAutoFitKey = mockGraphCanvas.mock.calls[0]?.[0].autoFitKey;

    const nodesWithEnd = [
      ...nodes,
      {
        data: {
          branchCount: 0,
          kind: 'step',
          label: 'End',
          parametersSummary: '',
          stepId: 'step-end',
          stepType: 'end',
          subtitle: '',
          targetRole: '',
          title: 'End',
        },
        id: 'step-end',
        position: { x: 280, y: 0 },
        type: 'studio',
      },
    ] as const;
    rerender(<WorkflowStudioCanvas edges={edges} nodes={nodesWithEnd} />);
    const nodeAutoFitKey = mockGraphCanvas.mock.calls[1]?.[0].autoFitKey;

    const edgesWithBranch = [
      ...edges,
      {
        data: { kind: 'branch' },
        id: 'edge-start-branch',
        source: 'step-start',
        target: 'step-branch',
      },
    ] as const;
    rerender(
      <WorkflowStudioCanvas edges={edgesWithBranch} nodes={nodesWithEnd} />,
    );
    const edgeAutoFitKey = mockGraphCanvas.mock.calls[2]?.[0].autoFitKey;

    expect(nodeAutoFitKey).not.toBe(initialAutoFitKey);
    expect(edgeAutoFitKey).not.toBe(nodeAutoFitKey);
  });
});
