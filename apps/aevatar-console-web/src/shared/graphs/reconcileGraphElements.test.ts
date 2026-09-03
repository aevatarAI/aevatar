import {
  type BuiltInEdge,
  type Edge,
  type Node,
  Position,
} from '@xyflow/react';

import {
  reconcileGraphEdges,
  reconcileGraphNodes,
} from './reconcileGraphElements';

const node = (id: string, overrides: Partial<Node> = {}): Node => ({
  id,
  position: { x: 0, y: 0 },
  data: {},
  ...overrides,
});

const edge = (
  id: string,
  source: string,
  target: string,
  overrides: Partial<Edge> = {},
): Edge => ({
  id,
  source,
  target,
  ...overrides,
});

function assertReconcilerOwnershipTypes(): void {
  const mutableNodes: Node[] = [];
  const mutableEdges: Edge[] = [];
  reconcileGraphNodes(mutableNodes, []).push(node('step:mutable'));
  reconcileGraphEdges(mutableEdges, []).push(
    edge('edge:mutable', 'step:source', 'step:target'),
  );

  const readonlyNodes: readonly Node[] = [];
  const readonlyEdges: readonly Edge[] = [];
  // @ts-expect-error A readonly previous node array produces a readonly result.
  reconcileGraphNodes(readonlyNodes, []).push(node('step:readonly'));
  // @ts-expect-error A readonly previous edge array produces a readonly result.
  reconcileGraphEdges(readonlyEdges, []).push(
    edge('edge:readonly', 'step:source', 'step:target'),
  );

  type NarrowNode = Node & { nodeKind: 'narrow' };
  type NarrowEdge = Edge & { edgeKind: 'narrow' };
  const narrowNodes: NarrowNode[] = [];
  const narrowEdges: NarrowEdge[] = [];
  const broadNodes: readonly Node[] = [];
  const broadEdges: readonly Edge[] = [];

  const narrowNodeResult = reconcileGraphNodes(narrowNodes, narrowNodes);
  const narrowEdgeResult = reconcileGraphEdges(narrowEdges, narrowEdges);
  const narrowNodeKind: 'narrow' = narrowNodeResult[0].nodeKind;
  const narrowEdgeKind: 'narrow' = narrowEdgeResult[0].edgeKind;
  void narrowNodeKind;
  void narrowEdgeKind;

  // @ts-expect-error Broad incoming nodes cannot widen a narrow mutable previous array.
  reconcileGraphNodes(narrowNodes, broadNodes);
  // @ts-expect-error Broad incoming edges cannot widen a narrow mutable previous array.
  reconcileGraphEdges(narrowEdges, broadEdges);
}

void assertReconcilerOwnershipTypes;

describe('reconcileGraphElements', () => {
  it('returns the previous node array and objects when incoming nodes are unchanged', () => {
    const previous = [
      node('step:alpha', {
        data: { label: 'Alpha' },
        handles: [
          {
            id: 'handle:alpha',
            type: 'source',
            position: Position.Right,
            x: 10,
            y: 20,
          },
        ],
        position: { x: 24, y: 48 },
      }),
      node('step:beta', { data: { label: 'Beta' }, selected: true }),
    ];
    const incoming = previous.map((element) => ({
      ...element,
      data: { ...element.data },
      handles: element.handles?.map((handle) => ({ ...handle })),
      position: { ...element.position },
    }));

    const result = reconcileGraphNodes(previous, incoming, 'step:beta');

    expect(result).toBe(previous);
    expect(result[0]).toBe(previous[0]);
    expect(result[1]).toBe(previous[1]);
  });

  it('replaces only the position-changed node in a 500-node graph', () => {
    const previous = Array.from({ length: 500 }, (_, index) =>
      node(`step:${index}`, {
        data: { label: `Step ${index}` },
        position: { x: index, y: index * 2 },
      }),
    );
    const incoming = previous.map((element) => ({
      ...element,
      data: { ...element.data },
      position: { ...element.position },
    }));
    incoming[237] = {
      ...incoming[237],
      position: { x: 2_370, y: 4_740 },
    };

    const result = reconcileGraphNodes(previous, incoming);

    expect(result).not.toBe(previous);
    expect(result[237]).not.toBe(previous[237]);
    expect(result[237].position).toBe(incoming[237].position);
    expect(result[237].position).toEqual({ x: 2_370, y: 4_740 });
    expect(result[0]).toBe(previous[0]);
    expect(result[236]).toBe(previous[236]);
    expect(result[238]).toBe(previous[238]);
    expect(result[499]).toBe(previous[499]);
  });

  it('replaces only the previous and next selected nodes during a selection transition', () => {
    const previous = [
      node('step:09', { selected: false }),
      node('step:10', { selected: true }),
      node('step:11', { selected: false }),
      node('step:12', { selected: false }),
    ];
    const incoming = previous.map((element) => ({
      ...element,
      selected: false,
    }));

    const result = reconcileGraphNodes(previous, incoming, 'step:11');

    expect(result[0]).toBe(previous[0]);
    expect(result[1]).not.toBe(previous[1]);
    expect(result[1].selected).toBe(false);
    expect(result[2]).not.toBe(previous[2]);
    expect(result[2].selected).toBe(true);
    expect(result[3]).toBe(previous[3]);
  });

  it('replaces a status-only node update while preserving every edge reference', () => {
    const previousNodes = [
      node('step:source', {
        data: { executionStatus: 'running', label: 'Source' },
      }),
      node('step:target', {
        data: { executionStatus: 'pending', label: 'Target' },
      }),
    ];
    const incomingNodes = [
      node('step:source', {
        data: { executionStatus: 'completed', label: 'Source' },
      }),
      node('step:target', {
        data: { executionStatus: 'pending', label: 'Target' },
      }),
    ];
    const previousEdges = [
      edge('edge:source-target', 'step:source', 'step:target'),
    ];
    const incomingEdges = previousEdges.map((element) => ({ ...element }));

    const nodes = reconcileGraphNodes(previousNodes, incomingNodes);
    const edges = reconcileGraphEdges(previousEdges, incomingEdges);

    expect(nodes[0]).not.toBe(previousNodes[0]);
    expect(nodes[0].data.executionStatus).toBe('completed');
    expect(nodes[1]).toBe(previousNodes[1]);
    expect(edges).toBe(previousEdges);
    expect(edges[0]).toBe(previousEdges[0]);
  });

  it('returns the previous edge array and objects when incoming edges are unchanged', () => {
    const previous = [
      edge('edge:alpha-beta', 'step:alpha', 'step:beta', {
        data: { condition: 'success' },
        label: 'continues',
        style: { stroke: '#1677ff' },
      }),
      edge('edge:beta-gamma', 'step:beta', 'step:gamma', { animated: true }),
    ];
    const incoming = previous.map((element) => ({
      ...element,
      data: element.data ? { ...element.data } : element.data,
      style: element.style ? { ...element.style } : element.style,
    }));

    const result = reconcileGraphEdges(previous, incoming);

    expect(result).toBe(previous);
    expect(result[0]).toBe(previous[0]);
    expect(result[1]).toBe(previous[1]);
  });

  it('keeps reused objects in the incoming node and edge order', () => {
    const previousNodes = [node('step:first'), node('step:second')];
    const previousEdges = [
      edge('edge:first', 'step:first', 'step:second'),
      edge('edge:second', 'step:second', 'step:first'),
    ];

    const nodes = reconcileGraphNodes(previousNodes, [
      previousNodes[1],
      previousNodes[0],
    ]);
    const edges = reconcileGraphEdges(previousEdges, [
      previousEdges[1],
      previousEdges[0],
    ]);

    expect(nodes).not.toBe(previousNodes);
    expect(nodes[0]).toBe(previousNodes[1]);
    expect(nodes[1]).toBe(previousNodes[0]);
    expect(edges).not.toBe(previousEdges);
    expect(edges[0]).toBe(previousEdges[1]);
    expect(edges[1]).toBe(previousEdges[0]);
  });

  it.each<{
    name: string;
    previous: Partial<Node>;
    incoming: Partial<Node>;
    read: (candidate: Node) => unknown;
    expected: unknown;
  }>([
    {
      name: 'style',
      previous: { style: { opacity: 0.5 } },
      incoming: { style: { opacity: 1 } },
      read: (candidate) => candidate.style,
      expected: { opacity: 1 },
    },
    {
      name: 'type',
      previous: { type: 'input' },
      incoming: { type: 'output' },
      read: (candidate) => candidate.type,
      expected: 'output',
    },
    {
      name: 'class name',
      previous: { className: 'node-before' },
      incoming: { className: 'node-after' },
      read: (candidate) => candidate.className,
      expected: 'node-after',
    },
    {
      name: 'drag handle',
      previous: { dragHandle: '.drag-before' },
      incoming: { dragHandle: '.drag-after' },
      read: (candidate) => candidate.dragHandle,
      expected: '.drag-after',
    },
    {
      name: 'width',
      previous: { width: 100 },
      incoming: { width: 200 },
      read: (candidate) => candidate.width,
      expected: 200,
    },
    {
      name: 'height',
      previous: { height: 60 },
      incoming: { height: 120 },
      read: (candidate) => candidate.height,
      expected: 120,
    },
    {
      name: 'initial width',
      previous: { initialWidth: 100 },
      incoming: { initialWidth: 200 },
      read: (candidate) => candidate.initialWidth,
      expected: 200,
    },
    {
      name: 'initial height',
      previous: { initialHeight: 60 },
      incoming: { initialHeight: 120 },
      read: (candidate) => candidate.initialHeight,
      expected: 120,
    },
    {
      name: 'measured dimensions',
      previous: { measured: { width: 100, height: 60 } },
      incoming: { measured: { width: 200, height: 120 } },
      read: (candidate) => candidate.measured,
      expected: { width: 200, height: 120 },
    },
    {
      name: 'handles',
      previous: {
        handles: [
          {
            id: 'handle:before',
            type: 'source',
            position: Position.Right,
            x: 10,
            y: 20,
          },
        ],
      },
      incoming: {
        handles: [
          {
            id: 'handle:after',
            type: 'source',
            position: Position.Right,
            x: 10,
            y: 20,
          },
        ],
      },
      read: (candidate) => candidate.handles,
      expected: [
        {
          id: 'handle:after',
          type: 'source',
          position: Position.Right,
          x: 10,
          y: 20,
        },
      ],
    },
    {
      name: 'parent ID',
      previous: { parentId: 'step:parent-before' },
      incoming: { parentId: 'step:parent-after' },
      read: (candidate) => candidate.parentId,
      expected: 'step:parent-after',
    },
    {
      name: 'extent',
      previous: { extent: 'parent' },
      incoming: {
        extent: [
          [0, 0],
          [200, 120],
        ],
      },
      read: (candidate) => candidate.extent,
      expected: [
        [0, 0],
        [200, 120],
      ],
    },
    {
      name: 'origin',
      previous: { origin: [0, 0] },
      incoming: { origin: [0.5, 0.5] },
      read: (candidate) => candidate.origin,
      expected: [0.5, 0.5],
    },
    {
      name: 'source position',
      previous: { sourcePosition: Position.Left },
      incoming: { sourcePosition: Position.Right },
      read: (candidate) => candidate.sourcePosition,
      expected: Position.Right,
    },
    {
      name: 'target position',
      previous: { targetPosition: Position.Top },
      incoming: { targetPosition: Position.Bottom },
      read: (candidate) => candidate.targetPosition,
      expected: Position.Bottom,
    },
    {
      name: 'hidden flag',
      previous: { hidden: false },
      incoming: { hidden: true },
      read: (candidate) => candidate.hidden,
      expected: true,
    },
    {
      name: 'draggable flag',
      previous: { draggable: false },
      incoming: { draggable: true },
      read: (candidate) => candidate.draggable,
      expected: true,
    },
    {
      name: 'selectable flag',
      previous: { selectable: false },
      incoming: { selectable: true },
      read: (candidate) => candidate.selectable,
      expected: true,
    },
    {
      name: 'connectable flag',
      previous: { connectable: false },
      incoming: { connectable: true },
      read: (candidate) => candidate.connectable,
      expected: true,
    },
    {
      name: 'deletable flag',
      previous: { deletable: false },
      incoming: { deletable: true },
      read: (candidate) => candidate.deletable,
      expected: true,
    },
    {
      name: 'focusable flag',
      previous: { focusable: false },
      incoming: { focusable: true },
      read: (candidate) => candidate.focusable,
      expected: true,
    },
    {
      name: 'expand-parent flag',
      previous: { expandParent: false },
      incoming: { expandParent: true },
      read: (candidate) => candidate.expandParent,
      expected: true,
    },
    {
      name: 'dragging state',
      previous: { dragging: false },
      incoming: { dragging: true },
      read: (candidate) => candidate.dragging,
      expected: true,
    },
    {
      name: 'resizing state',
      previous: { resizing: false },
      incoming: { resizing: true },
      read: (candidate) => candidate.resizing,
      expected: true,
    },
    {
      name: 'z-index',
      previous: { zIndex: 1 },
      incoming: { zIndex: 2 },
      read: (candidate) => candidate.zIndex,
      expected: 2,
    },
    {
      name: 'ARIA label',
      previous: { ariaLabel: 'Before node' },
      incoming: { ariaLabel: 'After node' },
      read: (candidate) => candidate.ariaLabel,
      expected: 'After node',
    },
    {
      name: 'ARIA role',
      previous: { ariaRole: 'group' },
      incoming: { ariaRole: 'button' },
      read: (candidate) => candidate.ariaRole,
      expected: 'button',
    },
    {
      name: 'DOM attributes',
      previous: { domAttributes: { tabIndex: 0 } },
      incoming: { domAttributes: { tabIndex: 1 } },
      read: (candidate) => candidate.domAttributes,
      expected: { tabIndex: 1 },
    },
  ])('replaces a node when its $name changes', ({
    previous: before,
    incoming: after,
    read,
    expected,
  }) => {
    const previousChanged = node('step:semantic', before);
    const previousStable = node('step:stable', { data: { label: 'Stable' } });
    const incomingChanged = node('step:semantic', after);
    const incomingStable = node('step:stable', { data: { label: 'Stable' } });

    const result = reconcileGraphNodes(
      [previousChanged, previousStable],
      [incomingChanged, incomingStable],
    );

    expect(result[0]).toBe(incomingChanged);
    expect(result[1]).toBe(previousStable);
    expect(read(result[0])).toEqual(expected);
  });

  it.each<{
    name: string;
    previous: Partial<Edge>;
    incoming: Partial<Edge>;
    read: (candidate: Edge) => unknown;
    expected: unknown;
  }>([
    {
      name: 'source endpoint',
      previous: { source: 'step:source-before' },
      incoming: { source: 'step:source-after' },
      read: (candidate) => candidate.source,
      expected: 'step:source-after',
    },
    {
      name: 'target endpoint',
      previous: { target: 'step:target-before' },
      incoming: { target: 'step:target-after' },
      read: (candidate) => candidate.target,
      expected: 'step:target-after',
    },
    {
      name: 'source handle',
      previous: { sourceHandle: 'handle:before' },
      incoming: { sourceHandle: 'handle:after' },
      read: (candidate) => candidate.sourceHandle,
      expected: 'handle:after',
    },
    {
      name: 'target handle',
      previous: { targetHandle: 'handle:before' },
      incoming: { targetHandle: 'handle:after' },
      read: (candidate) => candidate.targetHandle,
      expected: 'handle:after',
    },
    {
      name: 'type',
      previous: { type: 'default' },
      incoming: { type: 'smoothstep' },
      read: (candidate) => candidate.type,
      expected: 'smoothstep',
    },
    {
      name: 'label',
      previous: { label: 'Before' },
      incoming: { label: 'After' },
      read: (candidate) => candidate.label,
      expected: 'After',
    },
    {
      name: 'data',
      previous: { data: { condition: 'before' } },
      incoming: { data: { condition: 'after' } },
      read: (candidate) => candidate.data,
      expected: { condition: 'after' },
    },
    {
      name: 'style',
      previous: { style: { stroke: '#000000' } },
      incoming: { style: { stroke: '#ffffff' } },
      read: (candidate) => candidate.style,
      expected: { stroke: '#ffffff' },
    },
    {
      name: 'label style',
      previous: { labelStyle: { fill: '#000000' } },
      incoming: { labelStyle: { fill: '#ffffff' } },
      read: (candidate) => candidate.labelStyle,
      expected: { fill: '#ffffff' },
    },
    {
      name: 'label background style',
      previous: { labelBgStyle: { fill: '#000000' } },
      incoming: { labelBgStyle: { fill: '#ffffff' } },
      read: (candidate) => candidate.labelBgStyle,
      expected: { fill: '#ffffff' },
    },
    {
      name: 'label background visibility',
      previous: { labelShowBg: false },
      incoming: { labelShowBg: true },
      read: (candidate) => candidate.labelShowBg,
      expected: true,
    },
    {
      name: 'label background padding',
      previous: { labelBgPadding: [4, 6] },
      incoming: { labelBgPadding: [8, 10] },
      read: (candidate) => candidate.labelBgPadding,
      expected: [8, 10],
    },
    {
      name: 'label background radius',
      previous: { labelBgBorderRadius: 2 },
      incoming: { labelBgBorderRadius: 6 },
      read: (candidate) => candidate.labelBgBorderRadius,
      expected: 6,
    },
    {
      name: 'markers',
      previous: {
        markerStart: 'marker:before-start',
        markerEnd: 'marker:before-end',
      },
      incoming: {
        markerStart: 'marker:after-start',
        markerEnd: 'marker:after-end',
      },
      read: (candidate) => [candidate.markerStart, candidate.markerEnd],
      expected: ['marker:after-start', 'marker:after-end'],
    },
    {
      name: 'hidden flag',
      previous: { hidden: false },
      incoming: { hidden: true },
      read: (candidate) => candidate.hidden,
      expected: true,
    },
    {
      name: 'animated flag',
      previous: { animated: false },
      incoming: { animated: true },
      read: (candidate) => candidate.animated,
      expected: true,
    },
    {
      name: 'selectable flag',
      previous: { selectable: false },
      incoming: { selectable: true },
      read: (candidate) => candidate.selectable,
      expected: true,
    },
    {
      name: 'selected flag',
      previous: { selected: false },
      incoming: { selected: true },
      read: (candidate) => candidate.selected,
      expected: true,
    },
    {
      name: 'deletable flag',
      previous: { deletable: false },
      incoming: { deletable: true },
      read: (candidate) => candidate.deletable,
      expected: true,
    },
    {
      name: 'focusable flag',
      previous: { focusable: false },
      incoming: { focusable: true },
      read: (candidate) => candidate.focusable,
      expected: true,
    },
    {
      name: 'reconnectable setting',
      previous: { reconnectable: false },
      incoming: { reconnectable: 'source' },
      read: (candidate) => candidate.reconnectable,
      expected: 'source',
    },
    {
      name: 'interaction width',
      previous: { interactionWidth: 10 },
      incoming: { interactionWidth: 24 },
      read: (candidate) => candidate.interactionWidth,
      expected: 24,
    },
    {
      name: 'z-index',
      previous: { zIndex: 1 },
      incoming: { zIndex: 2 },
      read: (candidate) => candidate.zIndex,
      expected: 2,
    },
    {
      name: 'ARIA label',
      previous: { ariaLabel: 'Before edge' },
      incoming: { ariaLabel: 'After edge' },
      read: (candidate) => candidate.ariaLabel,
      expected: 'After edge',
    },
    {
      name: 'ARIA role',
      previous: { ariaRole: 'group' },
      incoming: { ariaRole: 'button' },
      read: (candidate) => candidate.ariaRole,
      expected: 'button',
    },
    {
      name: 'DOM attributes',
      previous: { domAttributes: { tabIndex: 0 } },
      incoming: { domAttributes: { tabIndex: 1 } },
      read: (candidate) => candidate.domAttributes,
      expected: { tabIndex: 1 },
    },
    {
      name: 'class name',
      previous: { className: 'edge-before' },
      incoming: { className: 'edge-after' },
      read: (candidate) => candidate.className,
      expected: 'edge-after',
    },
  ])('replaces only the edge whose $name changes', ({
    previous: before,
    incoming: after,
    read,
    expected,
  }) => {
    const previousChanged = edge(
      'edge:semantic',
      'step:source',
      'step:target',
      before,
    );
    const previousStable = edge(
      'edge:stable',
      'step:stable-source',
      'step:stable-target',
      {
        data: { condition: 'stable' },
        style: { stroke: '#1677ff' },
      },
    );
    const incomingChanged = { ...previousChanged, ...after };
    const incomingStable = {
      ...previousStable,
      data: { ...previousStable.data },
      style: { ...previousStable.style },
    };
    const previousEdges = [previousStable, previousChanged];
    const incomingEdges = [incomingStable, incomingChanged];

    const result = reconcileGraphEdges(previousEdges, incomingEdges);

    expect(result).not.toBe(previousEdges);
    expect(result[0]).toBe(previousStable);
    expect(result[1]).toBe(incomingChanged);
    expect(read(result[1])).toEqual(expected);
  });

  it('replaces a built-in edge when its path options change', () => {
    const previousChanged = {
      id: 'edge:path-options',
      source: 'step:path-source',
      target: 'step:path-target',
      type: 'smoothstep',
      pathOptions: { offset: 10 },
    } satisfies BuiltInEdge;
    const previousStable = {
      id: 'edge:path-stable',
      source: 'step:stable-source',
      target: 'step:stable-target',
      type: 'smoothstep',
      pathOptions: { offset: 20 },
    } satisfies BuiltInEdge;
    const incomingChanged = {
      ...previousChanged,
      pathOptions: { offset: 40 },
    } satisfies BuiltInEdge;
    const incomingStable = {
      ...previousStable,
      pathOptions: { offset: 20 },
    } satisfies BuiltInEdge;
    const previous = [previousChanged, previousStable];

    const result = reconcileGraphEdges(previous, [
      incomingChanged,
      incomingStable,
    ]);

    expect(result).not.toBe(previous);
    expect(result[0]).toBe(incomingChanged);
    expect(result[0].pathOptions.offset).toBe(40);
    expect(result[1]).toBe(previousStable);
  });

  it('preserves incoming node additions, removals, and ordering', () => {
    const previousFirst = node('step:first');
    const previousRemoved = node('step:removed');
    const previousLast = node('step:last');
    const added = node('step:added');
    const previous = [previousFirst, previousRemoved, previousLast];
    const incoming = [{ ...previousLast }, added, { ...previousFirst }];

    const result = reconcileGraphNodes(previous, incoming);

    expect(result).not.toBe(previous);
    expect(result.map((candidate) => candidate.id)).toEqual([
      'step:last',
      'step:added',
      'step:first',
    ]);
    expect(result[0]).toBe(previousLast);
    expect(result[1]).toBe(added);
    expect(result[2]).toBe(previousFirst);
    expect(result.some((candidate) => candidate.id === 'step:removed')).toBe(
      false,
    );
  });

  it('preserves incoming edge additions, removals, and ordering', () => {
    const previousFirst = edge('edge:first', 'step:a', 'step:b');
    const previousRemoved = edge('edge:removed', 'step:b', 'step:c');
    const previousLast = edge('edge:last', 'step:c', 'step:d');
    const added = edge('edge:added', 'step:d', 'step:e');
    const previous = [previousFirst, previousRemoved, previousLast];
    const incoming = [{ ...previousLast }, added, { ...previousFirst }];

    const result = reconcileGraphEdges(previous, incoming);

    expect(result).not.toBe(previous);
    expect(result.map((candidate) => candidate.id)).toEqual([
      'edge:last',
      'edge:added',
      'edge:first',
    ]);
    expect(result[0]).toBe(previousLast);
    expect(result[1]).toBe(added);
    expect(result[2]).toBe(previousFirst);
    expect(result.some((candidate) => candidate.id === 'edge:removed')).toBe(
      false,
    );
  });

  it('returns unchanged frozen readonly arrays by identity', () => {
    const previousNodes = Object.freeze([node('step:frozen')]);
    const previousEdges = Object.freeze([
      edge('edge:frozen', 'step:frozen', 'step:target'),
    ]);

    const nodes = reconcileGraphNodes(previousNodes, [{ ...previousNodes[0] }]);
    const edges = reconcileGraphEdges(previousEdges, [{ ...previousEdges[0] }]);

    expect(nodes).toBe(previousNodes);
    expect(edges).toBe(previousEdges);
    expect(Object.isFrozen(nodes)).toBe(true);
    expect(Object.isFrozen(edges)).toBe(true);
  });
});
