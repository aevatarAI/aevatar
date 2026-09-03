import type { Edge, Node } from "@xyflow/react";

type ShallowRecord = Record<string, unknown>;

function shallowEqual(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) {
    return true;
  }

  if (!left || !right || typeof left !== "object" || typeof right !== "object") {
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
        Object.prototype.hasOwnProperty.call(rightRecord, key) &&
        Object.is(leftRecord[key], rightRecord[key]),
    )
  );
}

function nodeSemanticsMatch(previous: Node, incoming: Node): boolean {
  return (
    previous.id === incoming.id &&
    shallowEqual(previous.position, incoming.position) &&
    shallowEqual(previous.positionAbsolute, incoming.positionAbsolute) &&
    shallowEqual(previous.data, incoming.data) &&
    shallowEqual(previous.style, incoming.style) &&
    previous.type === incoming.type &&
    previous.className === incoming.className &&
    previous.width === incoming.width &&
    previous.height === incoming.height &&
    previous.initialWidth === incoming.initialWidth &&
    previous.initialHeight === incoming.initialHeight &&
    shallowEqual(previous.measured, incoming.measured) &&
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
    previous.zIndex === incoming.zIndex &&
    previous.ariaLabel === incoming.ariaLabel
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
    shallowEqual(previous.markerStart, incoming.markerStart) &&
    shallowEqual(previous.markerEnd, incoming.markerEnd) &&
    previous.labelShowBg === incoming.labelShowBg &&
    shallowEqual(previous.labelBgPadding, incoming.labelBgPadding) &&
    previous.labelBgBorderRadius === incoming.labelBgBorderRadius &&
    previous.hidden === incoming.hidden &&
    previous.animated === incoming.animated &&
    previous.selectable === incoming.selectable &&
    previous.deletable === incoming.deletable &&
    previous.focusable === incoming.focusable &&
    previous.reconnectable === incoming.reconnectable &&
    previous.interactionWidth === incoming.interactionWidth &&
    previous.zIndex === incoming.zIndex &&
    previous.ariaLabel === incoming.ariaLabel &&
    previous.className === incoming.className
  );
}

export function reconcileGraphNodes<NodeType extends Node>(
  previous: readonly NodeType[],
  incoming: readonly NodeType[],
  selectedNodeId?: string,
): NodeType[] {
  const previousById = new Map(previous.map((element) => [element.id, element]));
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

  return unchanged ? (previous as NodeType[]) : reconciled;
}

export function reconcileGraphEdges<EdgeType extends Edge>(
  previous: readonly EdgeType[],
  incoming: readonly EdgeType[],
): EdgeType[] {
  const previousById = new Map(previous.map((element) => [element.id, element]));
  let unchanged = previous.length === incoming.length;

  const reconciled = incoming.map((incomingElement, index) => {
    const previousElement = previousById.get(incomingElement.id);

    if (previousElement && edgeSemanticsMatch(previousElement, incomingElement)) {
      unchanged = unchanged && previousElement === previous[index];
      return previousElement;
    }

    unchanged = unchanged && incomingElement === previous[index];
    return incomingElement;
  });

  return unchanged ? (previous as EdgeType[]) : reconciled;
}
