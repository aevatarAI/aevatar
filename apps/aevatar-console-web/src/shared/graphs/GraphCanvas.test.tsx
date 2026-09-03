import { act, render, screen } from '@testing-library/react';
import * as React from 'react';
import GraphCanvas from './GraphCanvas';

const mockBackgroundRender = jest.fn();
const mockControlsRender = jest.fn();
const mockMiniMapRender = jest.fn();
const mockReactFlowRender = jest.fn();
const mockApplyNodeChanges = jest.fn();
const mockUseStore = jest.fn();
let mockZoom = 1;

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
    Handle: (props: { className?: string; position?: string; type?: string }) =>
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
    applyNodeChanges: (...args: unknown[]) => mockApplyNodeChanges(...args),
    useEdgesState: (initialEdges: any[]) => React.useState(initialEdges),
    useNodesState: (initialNodes: any[]) => React.useState(initialNodes),
    useStore: (selector: any) => {
      mockUseStore(selector);
      return selector({ transform: [0, 0, mockZoom] });
    },
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
    mockZoom = 1;
    mockApplyNodeChanges.mockReset();
    mockApplyNodeChanges.mockImplementation((changes, currentNodes) =>
      currentNodes.map((node: any) => {
        const positionChange = changes.find(
          (change: any) => change.id === node.id && change.type === 'position',
        );
        return positionChange?.position
          ? { ...node, position: positionChange.position }
          : node;
      }),
    );
    mockUseStore.mockClear();
    mockBackgroundRender.mockClear();
    mockControlsRender.mockClear();
    mockMiniMapRender.mockClear();
    mockReactFlowRender.mockClear();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  const latestReactFlowProps = () =>
    mockReactFlowRender.mock.calls.at(-1)?.[0] as any;

  const installAnimationFrameMock = () => {
    const callbacks: FrameRequestCallback[] = [];
    const requestAnimationFrame = jest
      .spyOn(window, 'requestAnimationFrame')
      .mockImplementation((callback) => {
        callbacks.push(callback);
        return callbacks.length;
      });
    jest
      .spyOn(window, 'cancelAnimationFrame')
      .mockImplementation(() => undefined);

    return {
      flush: () => {
        const scheduled = callbacks.splice(0);
        act(() => {
          scheduled.forEach((callback) => {
            callback(0);
          });
        });
      },
      requestAnimationFrame,
    };
  };

  const createFlowInstance = () => ({
    fitView: jest.fn(async () => true),
    getNodes: jest.fn(() => nodes),
    screenToFlowPosition: jest.fn(({ x, y }) => ({ x, y })),
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

  it('makes the selected edge visually distinct without changing other edges', () => {
    const styledEdges = [
      {
        ...edges[0],
        markerEnd: {
          color: '#2F6FEC',
          height: 11,
          type: 'arrowclosed',
          width: 11,
        },
        style: {
          opacity: 0.9,
          stroke: '#2F6FEC',
          strokeWidth: 2.5,
        },
      },
      {
        ...edges[0],
        id: 'edge:publish:archive:linear',
        markerEnd: {
          color: '#8B5CF6',
          height: 11,
          type: 'arrowclosed',
          width: 11,
        },
        source: 'step:publish',
        style: {
          stroke: '#8B5CF6',
          strokeWidth: 2.5,
        },
        target: 'step:archive',
      },
    ];

    render(
      <GraphCanvas
        edges={styledEdges}
        nodes={nodes}
        selectedEdgeId="edge:assert:publish:linear"
        variant="studio"
      />,
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const selectedEdge = reactFlowProps.edges[0];
    const unselectedEdge = reactFlowProps.edges[1];

    expect(selectedEdge.selected).toBe(true);
    expect(selectedEdge.style).toEqual(
      expect.objectContaining({
        filter: 'drop-shadow(0 0 3px rgba(22, 119, 255, 0.55))',
        opacity: 0.9,
        stroke: 'var(--ant-color-primary)',
        strokeWidth: 4,
      }),
    );
    expect(selectedEdge.markerEnd).toEqual({
      color: '#1677ff',
      height: 11,
      type: 'arrowclosed',
      width: 11,
    });
    expect(unselectedEdge).toEqual(
      expect.objectContaining({
        markerEnd: styledEdges[1].markerEnd,
        selected: false,
        style: styledEdges[1].style,
      }),
    );
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

  it('keeps React Flow invariants and semantic event handlers stable across parent rerenders', () => {
    const callbacks = {
      onCanvasContextMenu: jest.fn(),
      onCanvasSelect: jest.fn(),
      onConnectNodes: jest.fn(),
      onDeleteEdges: jest.fn(),
      onDeleteNodes: jest.fn(),
      onEdgeSelect: jest.fn(),
      onNodeLayoutChange: jest.fn(),
      onNodeSelect: jest.fn(),
    };
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="workflow-alpha"
        bottomInset={12}
        edges={edges}
        nodes={nodes}
        variant="studio"
        {...callbacks}
      />,
    );
    const firstFlowProps = latestReactFlowProps();
    const firstMiniMapProps = mockMiniMapRender.mock.calls.at(-1)?.[0] as any;
    const firstControlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;

    rerender(
      <GraphCanvas
        autoFitKey="workflow-alpha"
        bottomInset={12}
        edges={edges}
        nodes={nodes}
        variant="studio"
        {...callbacks}
      />,
    );

    const nextFlowProps = latestReactFlowProps();
    const nextMiniMapProps = mockMiniMapRender.mock.calls.at(-1)?.[0] as any;
    const nextControlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;

    expect(nextFlowProps.nodeTypes).toBe(firstFlowProps.nodeTypes);
    expect(nextFlowProps.proOptions).toBe(firstFlowProps.proOptions);
    expect(nextFlowProps.fitViewOptions).toBe(firstFlowProps.fitViewOptions);
    expect(nextMiniMapProps.nodeColor).toBe(firstMiniMapProps.nodeColor);
    expect(nextMiniMapProps.style).toBe(firstMiniMapProps.style);
    expect(nextControlsProps.style).toBe(firstControlsProps.style);
    expect(nextFlowProps.onlyRenderVisibleElements).toBe(true);
    [
      'onBeforeDelete',
      'onConnect',
      'onEdgeClick',
      'onMoveStart',
      'onNodeClick',
      'onNodeDragStop',
      'onNodesChange',
      'onPaneClick',
      'onPaneContextMenu',
    ].forEach((handlerName) => {
      expect(nextFlowProps[handlerName]).toBe(firstFlowProps[handlerName]);
    });

    mockReactFlowRender.mockClear();
    render(
      <GraphCanvas edges={edges} nodes={nodes} selectedNodeId="step:assert" />,
    );
    const defaultFlowProps = latestReactFlowProps();
    expect(defaultFlowProps.onlyRenderVisibleElements).toBeUndefined();
    expect(defaultFlowProps.nodes[0]).toEqual(
      expect.objectContaining({
        selected: true,
        style: expect.objectContaining({
          borderColor: 'var(--ant-color-primary)',
        }),
      }),
    );
  });

  it('applies a drag change once while preserving untouched Studio node and data references', () => {
    const studioNodes = [
      nodes[0],
      {
        ...nodes[0],
        id: 'step:publish',
        data: { ...nodes[0].data, stepId: 'publish', title: 'publish' },
        position: { x: 320, y: 0 },
      },
    ];
    render(<GraphCanvas edges={edges} nodes={studioNodes} variant="studio" />);
    const beforeChange = latestReactFlowProps();
    const untouchedNode = beforeChange.nodes[1];
    const untouchedData = untouchedNode.data;

    act(() => {
      beforeChange.onNodesChange([
        {
          id: 'step:assert',
          position: { x: 48, y: 72 },
          type: 'position',
        },
      ]);
    });

    const afterChange = latestReactFlowProps();
    expect(mockApplyNodeChanges).toHaveBeenCalledTimes(1);
    expect(afterChange.nodes[0].position).toEqual({ x: 48, y: 72 });
    expect(afterChange.nodes[1]).toBe(untouchedNode);
    expect(afterChange.nodes[1].data).toBe(untouchedData);
  });

  it('replaces only the previous and next selected Studio nodes', () => {
    const studioNodes = [
      nodes[0],
      {
        ...nodes[0],
        id: 'step:publish',
        data: { ...nodes[0].data, stepId: 'publish', title: 'publish' },
      },
      {
        ...nodes[0],
        id: 'step:archive',
        data: { ...nodes[0].data, stepId: 'archive', title: 'archive' },
      },
    ];
    const { rerender } = render(
      <GraphCanvas
        edges={edges}
        nodes={studioNodes}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );
    const beforeSelection = latestReactFlowProps().nodes;

    rerender(
      <GraphCanvas
        edges={edges}
        nodes={studioNodes}
        selectedNodeId="step:publish"
        variant="studio"
      />,
    );

    const afterSelection = latestReactFlowProps().nodes;
    expect(afterSelection[0]).not.toBe(beforeSelection[0]);
    expect(afterSelection[0].selected).toBeFalsy();
    expect(afterSelection[1]).not.toBe(beforeSelection[1]);
    expect(afterSelection[1].selected).toBe(true);
    expect(afterSelection[2]).toBe(beforeSelection[2]);
    expect(afterSelection[2].data).toBe(beforeSelection[2].data);
  });

  it('memoizes Studio nodes and subscribes to a compact zoom band that removes body detail', () => {
    mockZoom = 0.4;
    render(<GraphCanvas edges={edges} nodes={nodes} variant="studio" />);
    const StudioNode = latestReactFlowProps().nodeTypes.studioWorkflowNode;

    expect(StudioNode.$$typeof).toBe(Symbol.for('react.memo'));
    render(
      <StudioNode
        data={{
          ...nodes[0].data,
          executionStatus: 'waiting',
          targetRole: 'assistant',
        }}
        selected={false}
      />,
    );

    const compactSelector = mockUseStore.mock.calls.at(-1)?.[0];
    expect(compactSelector({ transform: [0, 0, 0.2] })).toBe(true);
    expect(compactSelector({ transform: [0, 0, 0.47] })).toBe(true);
    expect(compactSelector({ transform: [0, 0, 0.48] })).toBe(false);
    expect(compactSelector({ transform: [0, 0, 0.9] })).toBe(false);
    expect(screen.getByText('assert')).toBeTruthy();
    expect(screen.getByText('Guard')).toBeTruthy();
    expect(screen.getByText('waiting')).toBeTruthy();
    expect(screen.queryByText('Role')).toBeNull();
    expect(screen.queryByText('assistant')).toBeNull();
    expect(screen.queryByText('No parameters configured')).toBeNull();
    expect(screen.getByTestId('handle-target')).toBeTruthy();
    expect(screen.getByTestId('handle-source')).toBeTruthy();
  });

  it('fits each initial or replacement topology in exactly one animation frame', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );

    act(() => {
      latestReactFlowProps().onInit(flowInstance);
    });
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
    expect(flowInstance.fitView).toHaveBeenLastCalledWith({
      duration: 0,
      maxZoom: 1.06,
      minZoom: 0.34,
      padding: 0.18,
    });
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);

    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={[
          {
            ...nodes[0],
            id: 'step:replacement',
          },
        ]}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
  });

  it('suppresses ordinary topology fitting after navigation but reveals an added selected node', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    act(() => {
      latestReactFlowProps().onInit(flowInstance);
    });
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    act(() => {
      latestReactFlowProps().onMoveStart({}, { x: 0, y: 0, zoom: 1 });
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={[{ ...nodes[0], id: 'step:replacement' }]}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();

    rerender(
      <GraphCanvas
        autoFitKey="topology-gamma"
        edges={edges}
        nodes={[
          { ...nodes[0], id: 'step:replacement' },
          { ...nodes[0], id: 'step:new' },
        ]}
        selectedNodeId="step:new"
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
    expect(flowInstance.fitView).toHaveBeenCalledWith(
      expect.objectContaining({
        nodes: [{ id: 'step:new' }],
      }),
    );
  });

  it('does not fit for selection, position, or execution-status-only changes', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    act(() => {
      latestReactFlowProps().onInit(flowInstance);
    });
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={[
          {
            ...nodes[0],
            position: { x: 20, y: 40 },
          },
        ]}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={[
          {
            ...nodes[0],
            data: { ...nodes[0].data, executionStatus: 'active' },
          },
        ]}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();
  });
});
