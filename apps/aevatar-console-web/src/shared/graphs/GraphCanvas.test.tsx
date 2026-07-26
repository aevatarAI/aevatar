import * as React from 'react';
import { act, render, screen } from '@testing-library/react';
import GraphCanvas from './GraphCanvas';

const mockBackgroundRender = jest.fn();
const mockControlsRender = jest.fn();
const mockMiniMapRender = jest.fn();
const mockReactFlowRender = jest.fn();

jest.mock('@xyflow/react', () => {
  const React = require('react');

  return {
    __esModule: true,
    Background: (props: unknown) => {
      mockBackgroundRender(props);
      return null;
    },
    BackgroundVariant: {
      Dots: 'dots',
      Lines: 'lines',
    },
    Controls: (props: unknown) => {
      mockControlsRender(props);
      return null;
    },
    Handle: (props: {
      className?: string;
      position?: string;
      type?: string;
    }) =>
      React.createElement('span', {
        className: props.className,
        'data-position': props.position,
        'data-testid': `handle-${props.type ?? 'unknown'}`,
      }),
    MiniMap: (props: unknown) => {
      mockMiniMapRender(props);
      return null;
    },
    Position: {
      Left: 'left',
      Right: 'right',
    },
    ReactFlow: (props: any) => {
      mockReactFlowRender(props);
      return React.createElement(
        'div',
        { 'data-testid': 'react-flow-mock' },
        props.children,
      );
    },
    applyNodeChanges: jest.fn((_changes, nodes) => nodes),
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
        subtitle: 'Guard',
        targetRole: '',
        title: 'assert',
      },
      type: 'studioWorkflowNode',
    },
  ];
  const edges: any[] = [
    {
      id: 'edge:assert:publish:linear',
      source: 'step:assert',
      target: 'step:publish',
      data: {
        kind: 'next',
      },
    },
  ];

  beforeEach(() => {
    mockBackgroundRender.mockClear();
    mockControlsRender.mockClear();
    mockMiniMapRender.mockClear();
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

  it('routes studio edge deletion through the parent callback before mutating the graph', async () => {
    const onDeleteEdges = jest.fn(async () => undefined);

    render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onDeleteEdges={onDeleteEdges}
        variant="studio"
      />,
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;

    expect(reactFlowProps.deleteKeyCode).toBeUndefined();
    await act(async () => {
      await expect(
        reactFlowProps.onBeforeDelete?.({
          edges,
          nodes: [],
        }),
      ).resolves.toBe(false);
    });
    expect(onDeleteEdges).toHaveBeenCalledWith(['edge:assert:publish:linear']);
  });

  it('renders studio nodes with their product label instead of the backend step type id', () => {
    render(<GraphCanvas edges={edges} nodes={nodes} variant="studio" />);

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const StudioNode = reactFlowProps.nodeTypes.studioWorkflowNode;
    render(
      <StudioNode
        data={{
          ...nodes[0].data,
          stepType: 'llm_call',
          subtitle: 'LLM call',
        }}
        selected={false}
      />,
    );

    expect(screen.getByText('LLM call')).toBeTruthy();
    expect(screen.queryByText('llm_call')).toBeNull();
  });

  it('uses a readable studio fit view and unobtrusive canvas controls', () => {
    render(
      <GraphCanvas
        autoFitKey="workflow-alpha"
        bottomInset={12}
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
    const miniMapProps = mockMiniMapRender.mock.calls.at(-1)?.[0] as any;
    const backgroundProps = mockBackgroundRender.mock.calls.at(-1)?.[0] as any;

    expect(reactFlowProps.fitView).toBe(true);
    expect(reactFlowProps.fitViewOptions).toEqual({
      duration: 0,
      maxZoom: 1.06,
      minZoom: 0.34,
      padding: 0.18,
    });
    expect(reactFlowProps.minZoom).toBe(0.28);
    expect(reactFlowProps.maxZoom).toBe(1.55);
    expect(reactFlowProps.proOptions).toEqual({ hideAttribution: true });
    expect(controlsProps).toEqual(
      expect.objectContaining({
        position: 'bottom-left',
        showInteractive: false,
      }),
    );
    expect(controlsProps.style).toEqual(
      expect.objectContaining({
        marginBottom: 30,
        marginLeft: 16,
      }),
    );
    expect(miniMapProps).toEqual(
      expect.objectContaining({
        nodeBorderRadius: 4,
        pannable: true,
        position: 'bottom-right',
        zoomable: true,
      }),
    );
    expect(miniMapProps.style).toEqual(
      expect.objectContaining({
        height: 82,
        marginBottom: 30,
        marginRight: 16,
        width: 132,
      }),
    );
    expect(backgroundProps).toEqual(
      expect.objectContaining({
        gap: 28,
        variant: 'dots',
      }),
    );
  });

  it('preserves studio node positions while improving the viewport', () => {
    const wideNodes = [
      {
        ...nodes[0],
        id: 'step:triage',
        position: { x: 240, y: 180 },
      },
      {
        ...nodes[0],
        id: 'step:guard',
        position: { x: 980, y: 180 },
        data: {
          ...nodes[0].data,
          stepId: 'guard',
        },
      },
      {
        ...nodes[0],
        id: 'step:approval',
        position: { x: 1720, y: 420 },
        data: {
          ...nodes[0].data,
          stepId: 'approval',
        },
      },
      {
        ...nodes[0],
        id: 'step:dispatch',
        position: { x: 2460, y: 180 },
        data: {
          ...nodes[0].data,
          stepId: 'dispatch',
        },
      },
    ];

    render(<GraphCanvas edges={edges} nodes={wideNodes} variant="studio" />);

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const renderedNodes = reactFlowProps.nodes as typeof wideNodes;

    expect(renderedNodes.map((node) => node.position)).toEqual(
      wideNodes.map((node) => node.position),
    );
    expect(reactFlowProps.fitViewOptions).toEqual(
      expect.objectContaining({
        minZoom: 0.34,
        padding: 0.18,
      }),
    );
  });

  it('renders readable studio node card details and connection handles', () => {
    render(<GraphCanvas edges={edges} nodes={nodes} variant="studio" />);

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const StudioNode = reactFlowProps.nodeTypes.studioWorkflowNode;
    render(
      <StudioNode
        data={{
          ...nodes[0].data,
          branchCount: 2,
          executionStatus: 'waiting',
          targetRole: 'assistant',
        }}
        selected={true}
      />,
    );

    expect(screen.getByText('assert')).toBeTruthy();
    expect(screen.getByText('Guard')).toBeTruthy();
    expect(screen.getByText('2 branches')).toBeTruthy();
    expect(screen.getByText('waiting')).toBeTruthy();
    expect(screen.getByText('Role')).toBeTruthy();
    expect(screen.getByText('assistant')).toBeTruthy();
    expect(screen.getByTestId('handle-target')).toHaveClass(
      'studio-workflow-node__handle',
    );
    expect(screen.getByTestId('handle-source')).toHaveClass(
      'studio-workflow-node__handle',
    );
  });
});
