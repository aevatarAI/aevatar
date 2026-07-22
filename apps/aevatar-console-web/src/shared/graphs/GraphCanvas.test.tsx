import * as React from 'react';
import { act, render, screen } from '@testing-library/react';
import GraphCanvas from './GraphCanvas';

const mockReactFlowRender = jest.fn();
const mockControlsRender = jest.fn();
const mockMiniMapRender = jest.fn();
const mockFitView = jest.fn(() => Promise.resolve(true));
const mockGetViewport = jest.fn();
const mockSetViewport = jest.fn(
  (
    _viewport: { x: number; y: number; zoom: number },
    _options?: { duration?: number },
  ) => Promise.resolve(true),
);
const mockRequestAnimationFrame = jest.fn(
  (callback: FrameRequestCallback): number => {
    callback(0);
    return 1;
  },
);
let mockViewport = { x: 100, y: 40, zoom: 0.46 };

jest.mock('@xyflow/react', () => {
  const React = require('react');

  return {
    __esModule: true,
    Background: () => null,
    BackgroundVariant: {
      Dots: 'dots',
      Lines: 'lines',
    },
    Controls: (props: unknown) => {
      mockControlsRender(props);
      return React.createElement('div', { 'data-testid': 'controls-mock' });
    },
    Handle: () => null,
    MiniMap: (props: unknown) => {
      mockMiniMapRender(props);
      return React.createElement('div', { 'data-testid': 'minimap-mock' });
    },
    Position: {
      Left: 'left',
      Right: 'right',
    },
    ReactFlow: (props: any) => {
      mockReactFlowRender(props);
      React.useEffect(() => {
        props.onInit?.({
          fitView: mockFitView,
          getNodes: () => props.nodes ?? [],
          getViewport: mockGetViewport,
          screenToFlowPosition: ({ x, y }: { x: number; y: number }) => ({ x, y }),
          setViewport: mockSetViewport,
        });
      }, [props.onInit]);
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
    mockViewport = { x: 100, y: 40, zoom: 0.46 };
    mockControlsRender.mockClear();
    mockFitView.mockClear();
    mockGetViewport.mockReset();
    mockGetViewport.mockImplementation(() => ({ ...mockViewport }));
    mockMiniMapRender.mockClear();
    mockReactFlowRender.mockClear();
    mockRequestAnimationFrame.mockClear();
    mockSetViewport.mockReset();
    mockSetViewport.mockImplementation((viewport) => {
      mockViewport = { ...viewport };
      return Promise.resolve(true);
    });
    Object.defineProperty(window, 'requestAnimationFrame', {
      configurable: true,
      value: mockRequestAnimationFrame,
    });
    Object.defineProperty(window, 'cancelAnimationFrame', {
      configurable: true,
      value: jest.fn(),
    });
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

  it('uses the same fit options for initial fit, automatic fit, and the fit-view control', () => {
    render(
      <GraphCanvas
        autoFitKey="step:assert"
        edges={edges}
        nodes={nodes}
        variant="studio"
      />,
    );

    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
    const expectedOptions = {
      duration: 0,
      maxZoom: 0.92,
      minZoom: 0.14,
      padding: 0.2,
    };

    expect(reactFlowProps.fitViewOptions).toEqual(expectedOptions);
    expect(controlsProps.fitViewOptions).toBe(reactFlowProps.fitViewOptions);
    expect(mockFitView).toHaveBeenCalledWith(reactFlowProps.fitViewOptions);
  });

  it('keeps the current zoom while recentering across inspector open, resize, and close', () => {
    const renderCanvas = (viewportRightInset: number) => (
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        variant="studio"
        viewportRightInset={viewportRightInset}
      />
    );
    const { rerender } = render(renderCanvas(0));

    expect(mockSetViewport).not.toHaveBeenCalled();

    rerender(renderCanvas(452));
    expect(screen.getByTestId('graph-canvas-viewport')).toHaveStyle({
      right: '452px',
    });
    expect(mockSetViewport).toHaveBeenLastCalledWith(
      { x: -126, y: 40, zoom: 0.46 },
      { duration: 0 },
    );

    rerender(renderCanvas(532));
    expect(mockSetViewport).toHaveBeenLastCalledWith(
      { x: -166, y: 40, zoom: 0.46 },
      { duration: 0 },
    );

    rerender(renderCanvas(0));
    expect(mockSetViewport).toHaveBeenLastCalledWith(
      { x: 100, y: 40, zoom: 0.46 },
      { duration: 0 },
    );
    expect(mockSetViewport).toHaveBeenCalledTimes(3);
    expect(mockFitView).not.toHaveBeenCalled();
  });

  it('does not refit or move the viewport when only selection changes', () => {
    const { rerender } = render(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        variant="studio"
        viewportRightInset={452}
      />,
    );

    mockFitView.mockClear();
    mockSetViewport.mockClear();
    rerender(
      <GraphCanvas
        edges={edges}
        nodes={nodes}
        selectedNodeId="step:assert"
        variant="studio"
        viewportRightInset={452}
      />,
    );

    expect(mockFitView).not.toHaveBeenCalled();
    expect(mockSetViewport).not.toHaveBeenCalled();
  });

  it('separates the controls from the minimap and uses the compact studio radii', () => {
    const { container } = render(
      <GraphCanvas edges={edges} nodes={nodes} variant="studio" />,
    );

    const graphViewport = screen.getByTestId('graph-canvas-viewport');
    const graphSurface = graphViewport.parentElement;
    const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
    const controlsProps = mockControlsRender.mock.calls.at(-1)?.[0] as any;
    const miniMapProps = mockMiniMapRender.mock.calls.at(-1)?.[0] as any;
    const StudioNode = reactFlowProps.nodeTypes.studioWorkflowNode;
    const nodeRender = render(
      <StudioNode data={nodes[0].data} selected={false} />,
    );
    const nodeSurface = nodeRender.container.firstElementChild as HTMLElement;
    const nodeHeader = nodeSurface.firstElementChild as HTMLElement;
    const nodeIcon = nodeHeader.firstElementChild as HTMLElement;

    expect(container).toContainElement(graphSurface);
    expect(graphSurface).toHaveStyle({ borderRadius: '8px' });
    expect(nodeSurface).toHaveStyle({ borderRadius: '8px' });
    expect(nodeIcon).toHaveStyle({ borderRadius: '6px' });
    expect(controlsProps.style).toEqual(
      expect.objectContaining({ marginLeft: 16 }),
    );
    expect(miniMapProps.style).toEqual(
      expect.objectContaining({ borderRadius: 8, marginLeft: 58 }),
    );
  });
});
