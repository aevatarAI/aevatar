import { deserialize, serialize } from 'node:v8';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import type { Node } from '@xyflow/react';
import React from 'react';
import type { StudioGraphNodeData } from '@/shared/studio/graph';
import GraphCanvas from './GraphCanvas';

const nodes: Node<StudioGraphNodeData>[] = ['first', 'second'].map(
  (stepId, index) => ({
    id: `step:${stepId}`,
    initialHeight: 120,
    initialWidth: 268,
    position: { x: index * 350, y: 0 },
    type: 'studioWorkflowNode',
    data: {
      branchCount: 0,
      kind: 'step',
      label: stepId,
      parametersSummary: '',
      stepId,
      stepType: 'assign',
      subtitle: 'Assign',
      targetRole: '',
      title: stepId,
    },
  }),
);

describe('GraphCanvas connection selection', () => {
  const originalStructuredClone = Object.getOwnPropertyDescriptor(
    globalThis,
    'structuredClone',
  );
  const originalElementFromPoint = Object.getOwnPropertyDescriptor(
    document,
    'elementFromPoint',
  );

  beforeEach(() => {
    setLocale('en-US', false);
    jest
      .spyOn(HTMLElement.prototype, 'offsetWidth', 'get')
      .mockReturnValue(1000);
    jest
      .spyOn(HTMLElement.prototype, 'offsetHeight', 'get')
      .mockReturnValue(600);
    Object.defineProperty(globalThis, 'structuredClone', {
      configurable: true,
      value: (value: unknown) => deserialize(serialize(value)),
      writable: true,
    });
  });

  afterEach(() => {
    if (originalStructuredClone) {
      Object.defineProperty(
        globalThis,
        'structuredClone',
        originalStructuredClone,
      );
    } else {
      Reflect.deleteProperty(globalThis, 'structuredClone');
    }
    if (originalElementFromPoint) {
      Object.defineProperty(
        document,
        'elementFromPoint',
        originalElementFromPoint,
      );
    } else {
      Reflect.deleteProperty(document, 'elementFromPoint');
    }
  });

  it('connects through handles without selecting a node and still selects the node body', () => {
    const onConnectNodes = jest.fn();
    const onNodeSelect = jest.fn();
    render(
      <GraphCanvas
        edges={[]}
        nodes={nodes}
        onConnectNodes={onConnectNodes}
        onNodeSelect={onNodeSelect}
        onlyRenderVisibleElements={false}
        showMiniMap={false}
        variant="studio"
      />,
    );
    const firstNode = screen.getByTestId('rf__node-step:first');
    const secondNode = screen.getByTestId('rf__node-step:second');
    // React Flow handles have no accessible role; use their public CSS classes.
    const sourceHandle = firstNode.querySelector('.react-flow__handle.source');
    const targetHandle = secondNode.querySelector('.react-flow__handle.target');
    if (!sourceHandle || !targetHandle) {
      throw new Error('Expected both connection handles to be rendered');
    }
    Object.defineProperty(document, 'elementFromPoint', {
      configurable: true,
      value: () => targetHandle,
    });

    fireEvent.click(sourceHandle);
    expect(onNodeSelect).not.toHaveBeenCalled();
    fireEvent.click(targetHandle);
    expect(onConnectNodes).toHaveBeenCalledWith('step:first', 'step:second');
    expect(onNodeSelect).not.toHaveBeenCalled();

    fireEvent.click(within(secondNode).getByText('second'));
    expect(onNodeSelect).toHaveBeenCalledTimes(1);
    expect(onNodeSelect).toHaveBeenCalledWith('step:second');
  });
});
