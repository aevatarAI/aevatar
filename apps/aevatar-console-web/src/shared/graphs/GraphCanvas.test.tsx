import { act, render, screen } from '@testing-library/react';
import * as React from 'react';
import GraphCanvas from './GraphCanvas';

const mockBackgroundRender = jest.fn();
const mockControlsRender = jest.fn();
const mockMiniMapRender = jest.fn();
const mockReactFlowRender = jest.fn();
const mockApplyNodeChanges = jest.fn();
const mockUseStore = jest.fn();
const mockFlowStoreListeners = new Set<() => void>();
const mockNodesInitializedListeners = new Set<() => void>();
const mockInitializedNodeTopologies = new Set<string>();
let mockZoom = 1;
let mockRenderedNodeTopology = '[]';
let mockReactFlowInstance: any;
let mockRenderFlowNodes = false;
let mockViewportHeight = 600;
let mockViewportNodeLookup = new Map<string, any>();
let mockViewportWidth = 1000;
let mockViewportX = 0;
let mockViewportY = 0;
const mockFlowStoreApi = {
  getState: () => ({
    height: mockViewportHeight,
    nodeLookup: mockViewportNodeLookup,
    transform: [mockViewportX, mockViewportY, mockZoom],
    width: mockViewportWidth,
  }),
};

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
      mockViewportNodeLookup = new Map(
        props.nodes.map((node: any) => [
          node.id,
          {
            ...node,
            internals: {
              positionAbsolute: node.position,
            },
            measured: {
              height:
                node.measured?.height ??
                node.height ??
                node.initialHeight ??
                120,
              width:
                node.measured?.width ?? node.width ?? node.initialWidth ?? 268,
            },
          },
        ]),
      );
      mockRenderedNodeTopology = JSON.stringify(
        props.nodes.map((node: any) => node.id),
      );
      const renderedNodes = mockRenderFlowNodes
        ? props.nodes.map((node: any) => {
            const NodeComponent = props.nodeTypes?.[node.type];
            return NodeComponent
              ? React.createElement(NodeComponent, {
                  data: node.data,
                  id: node.id,
                  key: node.id,
                  selected: Boolean(node.selected),
                })
              : null;
          })
        : null;
      return React.createElement(
        'div',
        { 'data-testid': 'react-flow-mock' },
        renderedNodes,
        props.children,
      );
    },
    applyNodeChanges: (...args: unknown[]) => mockApplyNodeChanges(...args),
    useNodesInitialized: () => {
      const [, forceRender] = React.useReducer((value: number) => value + 1, 0);
      React.useEffect(() => {
        mockNodesInitializedListeners.add(forceRender);
        return () => {
          mockNodesInitializedListeners.delete(forceRender);
        };
      }, [forceRender]);
      return mockInitializedNodeTopologies.has(mockRenderedNodeTopology);
    },
    useReactFlow: () => mockReactFlowInstance,
    useEdgesState: (initialEdges: any[]) => React.useState(initialEdges),
    useNodesState: (initialNodes: any[]) => React.useState(initialNodes),
    useStore: (selector: any) => {
      mockUseStore(selector);
      return React.useSyncExternalStore(
        (listener: () => void) => {
          mockFlowStoreListeners.add(listener);
          return () => mockFlowStoreListeners.delete(listener);
        },
        () => selector({ transform: [0, 0, mockZoom] }),
        () => selector({ transform: [0, 0, mockZoom] }),
      );
    },
    useStoreApi: () => mockFlowStoreApi,
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
    mockRenderFlowNodes = false;
    mockRenderedNodeTopology = '[]';
    mockReactFlowInstance = createFlowInstance();
    mockViewportHeight = 600;
    mockViewportNodeLookup = new Map();
    mockViewportWidth = 1000;
    mockViewportX = 0;
    mockViewportY = 0;
    mockFlowStoreListeners.clear();
    mockNodesInitializedListeners.clear();
    mockInitializedNodeTopologies.clear();
    mockApplyNodeChanges.mockReset();
    mockApplyNodeChanges.mockImplementation((changes, currentNodes) =>
      currentNodes.map((node: any) => {
        const positionChange = changes.find(
          (change: any) => change.id === node.id && change.type === 'position',
        );
        const dimensionsChange = changes.find(
          (change: any) =>
            change.id === node.id && change.type === 'dimensions',
        );
        if (!positionChange?.position && !dimensionsChange?.dimensions) {
          return node;
        }
        return {
          ...node,
          dragging: positionChange?.dragging ?? node.dragging,
          measured: dimensionsChange?.dimensions ?? node.measured,
          position: positionChange?.position ?? node.position,
          resizing: dimensionsChange?.resizing ?? node.resizing,
        };
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
    const callbacks = new Map<number, FrameRequestCallback>();
    let nextFrameId = 1;
    const requestAnimationFrame = jest
      .spyOn(window, 'requestAnimationFrame')
      .mockImplementation((callback) => {
        const frameId = nextFrameId;
        nextFrameId += 1;
        callbacks.set(frameId, callback);
        return frameId;
      });
    jest.spyOn(window, 'cancelAnimationFrame').mockImplementation((frameId) => {
      callbacks.delete(frameId);
    });

    return {
      flush: () => {
        const scheduled = [...callbacks.values()];
        callbacks.clear();
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

  const updateZoom = (zoom: number) => {
    act(() => {
      mockZoom = zoom;
      mockFlowStoreListeners.forEach((listener) => {
        listener();
      });
    });
  };

  const updateNodesInitialized = (initialized: boolean) => {
    act(() => {
      if (initialized) {
        mockInitializedNodeTopologies.add(mockRenderedNodeTopology);
      } else {
        mockInitializedNodeTopologies.delete(mockRenderedNodeTopology);
      }
      mockNodesInitializedListeners.forEach((listener) => {
        listener();
      });
    });
  };

  const markNodesInitialized = (nodesToInitialize: readonly any[]) => {
    mockInitializedNodeTopologies.add(
      JSON.stringify(nodesToInitialize.map((node) => node.id)),
    );
  };

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

    const { rerender } = render(
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

    rerender(
      <GraphCanvas edges={styledEdges} nodes={nodes} variant="studio" />,
    );
    const restoredEdges = latestReactFlowProps().edges;
    expect(restoredEdges[0]).toEqual(
      expect.objectContaining({
        markerEnd: styledEdges[0].markerEnd,
        selected: false,
        style: styledEdges[0].style,
      }),
    );
    expect(restoredEdges[1]).toBe(unselectedEdge);
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

    expect(reactFlowProps.fitView).toBe(false);
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
      'onMoveEnd',
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

  it('preserves Studio rendering defaults and accepts explicit benchmark policy overrides', () => {
    render(<GraphCanvas edges={edges} nodes={nodes} variant="studio" />);

    expect(latestReactFlowProps().onlyRenderVisibleElements).toBe(true);
    expect(mockMiniMapRender).toHaveBeenCalled();

    mockMiniMapRender.mockClear();
    render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onlyRenderVisibleElements={false}
        showMiniMap={false}
        variant="studio"
      />,
    );

    expect(latestReactFlowProps().onlyRenderVisibleElements).toBe(false);
    expect(mockMiniMapRender).not.toHaveBeenCalled();
  });

  it('reports committed Studio node renders without adding instrumentation to node data', () => {
    mockRenderFlowNodes = true;
    const onStudioNodeRender = jest.fn();
    const { rerender } = render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onStudioNodeRender={onStudioNodeRender}
        variant="studio"
      />,
    );

    expect(onStudioNodeRender).toHaveBeenCalledWith('step:assert');
    expect(latestReactFlowProps().nodes[0].data).toBe(nodes[0].data);

    onStudioNodeRender.mockClear();
    rerender(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onStudioNodeRender={onStudioNodeRender}
        variant="studio"
      />,
    );
    expect(onStudioNodeRender).not.toHaveBeenCalled();

    rerender(
      <GraphCanvas
        edges={edges}
        nodes={[
          {
            ...nodes[0],
            data: { ...nodes[0].data, executionStatus: 'active' },
          },
        ]}
        onStudioNodeRender={onStudioNodeRender}
        variant="studio"
      />,
    );
    expect(onStudioNodeRender).toHaveBeenCalledTimes(1);
    expect(onStudioNodeRender).toHaveBeenLastCalledWith('step:assert');
  });

  it('exposes the actual Studio node references delivered to React Flow', () => {
    const onRenderedNodesChange = jest.fn();
    const { rerender } = render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onRenderedNodesChange={onRenderedNodesChange}
        variant="studio"
      />,
    );
    const firstRenderedNodes = latestReactFlowProps().nodes;

    expect(onRenderedNodesChange).toHaveBeenLastCalledWith(firstRenderedNodes);

    onRenderedNodesChange.mockClear();
    rerender(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onRenderedNodesChange={onRenderedNodesChange}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );
    const selectedRenderedNodes = latestReactFlowProps().nodes;

    expect(selectedRenderedNodes).not.toBe(firstRenderedNodes);
    expect(onRenderedNodesChange).toHaveBeenLastCalledWith(
      selectedRenderedNodes,
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
    const { rerender } = render(
      <GraphCanvas edges={edges} nodes={studioNodes} variant="studio" />,
    );
    const beforeChange = latestReactFlowProps();
    const untouchedNode = beforeChange.nodes[1];
    const untouchedData = untouchedNode.data;

    act(() => {
      beforeChange.onNodesChange([
        {
          dragging: true,
          id: 'step:assert',
          position: { x: 48, y: 72 },
          type: 'position',
        },
        {
          dimensions: { height: 144, width: 280 },
          id: 'step:assert',
          resizing: true,
          type: 'dimensions',
        },
      ]);
    });

    const afterChange = latestReactFlowProps();
    expect(mockApplyNodeChanges).toHaveBeenCalledTimes(1);
    expect(afterChange.nodes[0].position).toEqual({ x: 48, y: 72 });
    expect(afterChange.nodes[1]).toBe(untouchedNode);
    expect(afterChange.nodes[1].data).toBe(untouchedData);

    rerender(
      <GraphCanvas
        bottomInset={24}
        edges={[...edges]}
        nodes={[...studioNodes]}
        variant="studio"
      />,
    );
    const afterUnrelatedParentRender = latestReactFlowProps();
    expect(afterUnrelatedParentRender.nodes[0]).toBe(afterChange.nodes[0]);
    expect(afterUnrelatedParentRender.nodes[0].position).toEqual({
      x: 48,
      y: 72,
    });
    expect(afterUnrelatedParentRender.nodes[1]).toBe(untouchedNode);

    const statusUpdatedNodes = [
      studioNodes[0],
      {
        ...studioNodes[1],
        data: { ...studioNodes[1].data, executionStatus: 'active' },
      },
    ];
    rerender(
      <GraphCanvas
        bottomInset={24}
        edges={edges}
        nodes={statusUpdatedNodes}
        selectedNodeId="step:publish"
        variant="studio"
      />,
    );
    const afterSiblingStatusUpdate = latestReactFlowProps();
    expect(afterSiblingStatusUpdate.nodes[0]).toBe(
      afterUnrelatedParentRender.nodes[0],
    );
    expect(afterSiblingStatusUpdate.nodes[0]).toEqual(
      expect.objectContaining({
        dragging: true,
        measured: { height: 144, width: 280 },
        position: { x: 48, y: 72 },
        resizing: true,
      }),
    );
    expect(afterSiblingStatusUpdate.nodes[1]).not.toBe(untouchedNode);
    expect(afterSiblingStatusUpdate.nodes[1]).toEqual(
      expect.objectContaining({
        data: expect.objectContaining({ executionStatus: 'active' }),
        selected: true,
      }),
    );

    rerender(
      <GraphCanvas
        edges={edges}
        nodes={[
          { ...studioNodes[0], position: { x: 120, y: 90 } },
          statusUpdatedNodes[1],
        ]}
        selectedNodeId="step:publish"
        variant="studio"
      />,
    );
    expect(latestReactFlowProps().nodes[0]).toEqual(
      expect.objectContaining({
        measured: { height: 144, width: 280 },
        position: { x: 120, y: 90 },
      }),
    );
  });

  it('keeps the drag-stop handler stable while reporting the latest local nodes', () => {
    const onNodeLayoutChange = jest.fn();
    render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        onNodeLayoutChange={onNodeLayoutChange}
        variant="studio"
      />,
    );
    const beforeChange = latestReactFlowProps();

    act(() => {
      beforeChange.onNodesChange([
        {
          dragging: true,
          id: 'step:assert',
          position: { x: 48, y: 72 },
          type: 'position',
        },
      ]);
    });

    const afterChange = latestReactFlowProps();
    act(() => {
      afterChange.onNodeDragStop({}, afterChange.nodes[0], afterChange.nodes);
    });

    expect(onNodeLayoutChange).toHaveBeenCalledWith(afterChange.nodes);
    expect(afterChange.onNodeDragStop).toBe(beforeChange.onNodeDragStop);
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

  it('rerenders Studio nodes only when their compact zoom band changes', () => {
    mockZoom = 0.8;
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

    const initialRenderCount = mockUseStore.mock.calls.length;
    updateZoom(0.75);
    expect(mockUseStore).toHaveBeenCalledTimes(initialRenderCount);
    updateZoom(0.4);
    expect(mockUseStore).toHaveBeenCalledTimes(initialRenderCount + 1);
    updateZoom(0.3);
    expect(mockUseStore).toHaveBeenCalledTimes(initialRenderCount + 1);
    expect(screen.getByText('assert')).toBeTruthy();
    expect(screen.getByText('Guard')).toBeTruthy();
    expect(screen.getByText('waiting')).toBeTruthy();
    expect(screen.queryByText('Role')).toBeNull();
    expect(screen.queryByText('assistant')).toBeNull();
    expect(screen.queryByText('No parameters configured')).toBeNull();
    expect(screen.getByTestId('handle-target')).toBeTruthy();
    expect(screen.getByTestId('handle-source')).toBeTruthy();
  });

  it('fits each ready Studio topology through one authoritative animation frame', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementNodes = [
      {
        ...nodes[0],
        id: 'step:replacement',
      },
    ];
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );

    expect(latestReactFlowProps().fitView).toBe(false);
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();

    updateNodesInitialized(true);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        bottomInset={24}
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
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
        nodes={replacementNodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();

    updateNodesInitialized(true);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
  });

  it('fits ready Studio topology without an explicit key and tracks node and edge ids', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementEdges = [
      {
        ...edges[0],
        id: 'edge:assert:archive:linear',
        target: 'step:archive',
      },
    ];
    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    const { rerender } = render(
      <GraphCanvas edges={edges} nodes={nodes} variant="studio" />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    updateNodesInitialized(true);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);

    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();
    rerender(
      <GraphCanvas
        edges={edges}
        nodes={[
          {
            ...nodes[0],
            data: { ...nodes[0].data, executionStatus: 'active' },
            position: { x: 20, y: 40 },
          },
        ]}
        selectedNodeId="step:assert"
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();

    rerender(
      <GraphCanvas edges={replacementEdges} nodes={nodes} variant="studio" />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);

    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();
    rerender(
      <GraphCanvas
        edges={replacementEdges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    updateNodesInitialized(true);
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('refits identical Studio topology when the explicit fit identity changes', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    markNodesInitialized(nodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="identity-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    rerender(
      <GraphCanvas
        autoFitKey="identity-beta"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('retains built-in fitView only for the default variant', () => {
    render(<GraphCanvas edges={edges} nodes={nodes} />);

    expect(latestReactFlowProps().fitView).toBe(true);
    const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
    expect(controlsProps.onFitView).toBeUndefined();
    expect(controlsProps.onZoomIn).toBeUndefined();
    expect(controlsProps.onZoomOut).toBeUndefined();
  });

  it('fits when a same-key Studio topology first becomes non-empty', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    markNodesInitialized(nodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={[]}
        nodes={[]}
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('fits when a same-key non-empty canvas first switches to Studio', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    markNodesInitialized(nodes);
    const { rerender } = render(
      <GraphCanvas autoFitKey="topology-alpha" edges={edges} nodes={nodes} />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('cancels pending fits for stale instances and unmounted canvases', () => {
    const animationFrame = installAnimationFrameMock();
    const staleFlowInstance = createFlowInstance();
    mockReactFlowInstance = staleFlowInstance;
    markNodesInitialized(nodes);
    const { rerender, unmount } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);

    const activeFlowInstance = createFlowInstance();
    mockReactFlowInstance = activeFlowInstance;
    rerender(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(2);
    animationFrame.flush();
    expect(staleFlowInstance.fitView).not.toHaveBeenCalled();
    expect(activeFlowInstance.fitView).toHaveBeenCalledTimes(1);

    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    markNodesInitialized(replacementNodes);
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(3);
    unmount();
    animationFrame.flush();
    expect(activeFlowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('suppresses ordinary topology fitting after navigation but reveals an offscreen added selected node', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    const focusedNodes = [
      { ...nodes[0], id: 'step:replacement' },
      {
        ...nodes[0],
        id: 'step:new',
        position: { x: 1800, y: 1200 },
      },
    ];
    markNodesInitialized(nodes);
    markNodesInitialized(replacementNodes);
    markNodesInitialized(focusedNodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    act(() => {
      const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
      controlsProps.onZoomIn();
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );
    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();

    rerender(
      <GraphCanvas
        autoFitKey="topology-gamma"
        edges={edges}
        nodes={focusedNodes}
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

  it('preserves manual navigation when an added selected node is already visible', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const focusedNodes = [
      nodes[0],
      {
        ...nodes[0],
        id: 'step:new',
        position: { x: 320, y: 180 },
      },
    ];
    markNodesInitialized(nodes);
    markNodesInitialized(focusedNodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    act(() => {
      const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
      controlsProps.onZoomIn();
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={focusedNodes}
        selectedNodeId="step:new"
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();
  });

  it.each([
    'onZoomIn',
    'onZoomOut',
    'onFitView',
  ])('treats Studio Controls %s as manual navigation', (controlCallback) => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    markNodesInitialized(nodes);
    markNodesInitialized(replacementNodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();
    const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;

    expect(controlsProps[controlCallback]).toEqual(expect.any(Function));
    act(() => {
      controlsProps[controlCallback]();
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();
  });

  it('does not treat a pane click without viewport movement as manual navigation', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    markNodesInitialized(nodes);
    markNodesInitialized(replacementNodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    const flowProps = latestReactFlowProps();
    expect(flowProps.onMoveEnd).toEqual(expect.any(Function));
    act(() => {
      flowProps.onMoveStart({}, { x: 0, y: 0, zoom: 1 });
      flowProps.onMoveEnd({}, { x: 0, y: 0, zoom: 1 });
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).toHaveBeenCalledTimes(1);
    animationFrame.flush();
    expect(flowInstance.fitView).toHaveBeenCalledTimes(1);
  });

  it('treats null-source MiniMap viewport movement as manual navigation', async () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    const replacementNodes = [{ ...nodes[0], id: 'step:replacement' }];
    markNodesInitialized(nodes);
    markNodesInitialized(replacementNodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
    animationFrame.flush();
    await act(async () => {
      await Promise.resolve();
    });
    animationFrame.requestAnimationFrame.mockClear();
    flowInstance.fitView.mockClear();

    const flowProps = latestReactFlowProps();
    expect(flowProps.onMoveEnd).toEqual(expect.any(Function));
    act(() => {
      flowProps.onMoveStart(null, { x: 0, y: 0, zoom: 1 });
      flowProps.onMoveEnd(null, { x: -120, y: -48, zoom: 1 });
    });
    rerender(
      <GraphCanvas
        autoFitKey="topology-beta"
        edges={edges}
        nodes={replacementNodes}
        variant="studio"
      />,
    );

    expect(animationFrame.requestAnimationFrame).not.toHaveBeenCalled();
    expect(flowInstance.fitView).not.toHaveBeenCalled();
  });

  it('does not fit for selection, position, or execution-status-only changes', () => {
    const animationFrame = installAnimationFrameMock();
    const flowInstance = createFlowInstance();
    mockReactFlowInstance = flowInstance;
    markNodesInitialized(nodes);
    const { rerender } = render(
      <GraphCanvas
        autoFitKey="topology-alpha"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );
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
