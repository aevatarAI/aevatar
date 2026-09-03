import type { Edge, Node } from '@xyflow/react';

type ShallowRecord = Record<string, unknown>;

function shallowEqual(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) {
    return true;
  }

  if (
    !left ||
    !right ||
    typeof left !== 'object' ||
    typeof right !== 'object'
  ) {
    return false;
  }

  const leftRecord = left as ShallowRecord;
  const rightRecord = right as ShallowRecord;
  const leftKeys = Object.keys(leftRecord);
  const rightKeys = Object.keys(rightRecord);

  return (
    leftKeys.length === rightKeys.length &&
    leftKeys.every(
      (key) =>
        Object.hasOwn(rightRecord, key) &&
        Object.is(leftRecord[key], rightRecord[key]),
    )
  );
}

function shallowArrayItemsEqual(
  left: readonly unknown[] | undefined,
  right: readonly unknown[] | undefined,
): boolean {
  if (left === right) {
    return true;
  }

  if (!left || !right || left.length !== right.length) {
    return false;
  }

  return left.every((item, index) => shallowEqual(item, right[index]));
}

function readEdgePathOptions(edge: Edge): unknown {
  return 'pathOptions' in edge ? edge.pathOptions : undefined;
}

function edgePathOptionsMatch(previous: Edge, incoming: Edge): boolean {
  return shallowEqual(
    readEdgePathOptions(previous),
    readEdgePathOptions(incoming),
  );
}

function nodeSemanticsMatch(previous: Node, incoming: Node): boolean {
  return (
    previous.id === incoming.id &&
    shallowEqual(previous.position, incoming.position) &&
    shallowEqual(previous.data, incoming.data) &&
    shallowEqual(previous.style, incoming.style) &&
    previous.type === incoming.type &&
    previous.className === incoming.className &&
    previous.dragHandle === incoming.dragHandle &&
    previous.width === incoming.width &&
    previous.height === incoming.height &&
    previous.initialWidth === incoming.initialWidth &&
    previous.initialHeight === incoming.initialHeight &&
    shallowEqual(previous.measured, incoming.measured) &&
    shallowArrayItemsEqual(previous.handles, incoming.handles) &&
    previous.parentId === incoming.parentId &&
    shallowEqual(previous.extent, incoming.extent) &&
    shallowEqual(previous.origin, incoming.origin) &&
    previous.sourcePosition === incoming.sourcePosition &&
    previous.targetPosition === incoming.targetPosition &&
    previous.hidden === incoming.hidden &&
    previous.draggable === incoming.draggable &&
    previous.selectable === incoming.selectable &&
    previous.connectable === incoming.connectable &&
    previous.deletable === incoming.deletable &&
    previous.focusable === incoming.focusable &&
    previous.expandParent === incoming.expandParent &&
    previous.dragging === incoming.dragging &&
    previous.resizing === incoming.resizing &&
    previous.zIndex === incoming.zIndex &&
    previous.ariaLabel === incoming.ariaLabel &&
    previous.ariaRole === incoming.ariaRole &&
    shallowEqual(previous.domAttributes, incoming.domAttributes)
  );
}

function edgeSemanticsMatch(previous: Edge, incoming: Edge): boolean {
  return (
    previous.id === incoming.id &&
    previous.source === incoming.source &&
    previous.target === incoming.target &&
    previous.sourceHandle === incoming.sourceHandle &&
    previous.targetHandle === incoming.targetHandle &&
    previous.type === incoming.type &&
    previous.label === incoming.label &&
    shallowEqual(previous.data, incoming.data) &&
    shallowEqual(previous.style, incoming.style) &&
    shallowEqual(previous.labelStyle, incoming.labelStyle) &&
    shallowEqual(previous.labelBgStyle, incoming.labelBgStyle) &&
    shallowEqual(previous.markerStart, incoming.markerStart) &&
    shallowEqual(previous.markerEnd, incoming.markerEnd) &&
    previous.labelShowBg === incoming.labelShowBg &&
    shallowEqual(previous.labelBgPadding, incoming.labelBgPadding) &&
    previous.labelBgBorderRadius === incoming.labelBgBorderRadius &&
    previous.hidden === incoming.hidden &&
    previous.animated === incoming.animated &&
    previous.selectable === incoming.selectable &&
    previous.selected === incoming.selected &&
    previous.deletable === incoming.deletable &&
    previous.focusable === incoming.focusable &&
    previous.reconnectable === incoming.reconnectable &&
    previous.interactionWidth === incoming.interactionWidth &&
    previous.zIndex === incoming.zIndex &&
    previous.ariaLabel === incoming.ariaLabel &&
    previous.ariaRole === incoming.ariaRole &&
    previous.className === incoming.className &&
    shallowEqual(previous.domAttributes, incoming.domAttributes) &&
    edgePathOptionsMatch(previous, incoming)
  );
}

/**
 * Reconciles React Flow user nodes by ID while preserving unchanged references.
 * IDs must be unique strings within each input; duplicate IDs are unsupported.
 */
export function reconcileGraphNodes<NodeType extends Node>(
  previous: NodeType[],
  incoming: readonly NodeType[],
  selectedNodeId?: string,
): NodeType[];
export function reconcileGraphNodes<NodeType extends Node>(
  previous: readonly NodeType[],
  incoming: readonly NodeType[],
  selectedNodeId?: string,
): readonly NodeType[];
export function reconcileGraphNodes<NodeType extends Node>(
  previous: readonly NodeType[],
  incoming: readonly NodeType[],
  selectedNodeId?: string,
): readonly NodeType[] {
  const previousById = new Map(
    previous.map((element) => [element.id, element]),
  );
  let unchanged = previous.length === incoming.length;

  const reconciled = incoming.map((incomingElement, index) => {
    const previousElement = previousById.get(incomingElement.id);
    const selected = incomingElement.id === selectedNodeId;

    if (
      previousElement &&
      nodeSemanticsMatch(previousElement, incomingElement) &&
      Boolean(previousElement.selected) === selected
    ) {
      unchanged = unchanged && previousElement === previous[index];
      return previousElement;
    }

    const nextElement =
      Boolean(incomingElement.selected) === selected
        ? incomingElement
        : ({ ...incomingElement, selected } as NodeType);

    unchanged = unchanged && nextElement === previous[index];
    return nextElement;
  });

  return unchanged ? previous : reconciled;
}

/**
 * Reconciles React Flow user edges by ID while preserving unchanged references.
 * IDs must be unique strings within each input; duplicate IDs are unsupported.
 */
export function reconcileGraphEdges<EdgeType extends Edge>(
  previous: EdgeType[],
  incoming: readonly EdgeType[],
): EdgeType[];
export function reconcileGraphEdges<EdgeType extends Edge>(
  previous: readonly EdgeType[],
  incoming: readonly EdgeType[],
): readonly EdgeType[];
export function reconcileGraphEdges<EdgeType extends Edge>(
  previous: readonly EdgeType[],
  incoming: readonly EdgeType[],
): readonly EdgeType[] {
  const previousById = new Map(
    previous.map((element) => [element.id, element]),
  );
  let unchanged = previous.length === incoming.length;

  const reconciled = incoming.map((incomingElement, index) => {
    const previousElement = previousById.get(incomingElement.id);

    if (
      previousElement &&
      edgeSemanticsMatch(previousElement, incomingElement)
    ) {
      unchanged = unchanged && previousElement === previous[index];
      return previousElement;
    }

    unchanged = unchanged && incomingElement === previous[index];
    return incomingElement;
  });

  return unchanged ? previous : reconciled;
}
