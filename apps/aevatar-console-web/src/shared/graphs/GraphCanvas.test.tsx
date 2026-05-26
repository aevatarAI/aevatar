import * as React from 'react';
import { act, render } from '@testing-library/react';
import GraphCanvas from './GraphCanvas';

const mockReactFlowRender = jest.fn();

jest.mock('@xyflow/react', () => {
  const React = require('react');

  return {
    __esModule: true,
    Background: () => null,
    BackgroundVariant: {
      Dots: 'dots',
    },
    Controls: () => null,
    Handle: () => null,
    MiniMap: () => null,
    Position: {
      Left: 'left',
      Right: 'right',
    },
    ReactFlow: (props: unknown) => {
      mockReactFlowRender(props);
      return React.createElement('div', { 'data-testid': 'react-flow-mock' });
    },
    applyNodeChanges: jest.fn((changes, nodes) => nodes),
    useEdgesState: (initialEdges: any[]) => React.useState(initialEdges),
    useNodesState: (initialNodes: any[]) => React.useState(initialNodes),
    useStore: (selector: any) => selector({ transform: [0, 0, 1] }),
  };
});

describe('GraphCanvas', () => {
  const nodes: any[] = [
    {
      id: 'step:assert',
      position: { x: 0, y: 0 },
      data: {
        branchCount: 0,
        kind: 'step',
        label: 'assert',
        parametersSummary: 'No parameters configured',
        stepId: 'assert',
        stepType: 'guard',
        subtitle: 'guard',
        targetRole: '',
        title: 'assert',
      },
      type: 'studioWorkflowNode',
    },
  ];
  const edges: any[] = [];

  beforeEach(() => {
    mockReactFlowRender.mockClear();
  });

  it('routes studio node deletion through the parent callback before mutating the graph', async () => {
    const onDeleteNodes = jest.fn(async () => undefined);

    render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onDeleteNodes={onDeleteNodes}
        variant="studio"
      />,
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;

    expect(reactFlowProps.deleteKeyCode).toBeUndefined();
    await act(async () => {
      await expect(
        reactFlowProps.onBeforeDelete?.({
          edges: [],
          nodes,
        }),
      ).resolves.toBe(false);
    });
    expect(onDeleteNodes).toHaveBeenCalledWith(['step:assert']);
  });

  it('disables keyboard deletion for studio canvases without a document-level delete handler', () => {
    render(<GraphCanvas edges={edges} nodes={nodes} variant="studio" />);

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;

    expect(reactFlowProps.deleteKeyCode).toBeNull();
    expect(reactFlowProps.onBeforeDelete).toBeUndefined();
  });
});
