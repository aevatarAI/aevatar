import React from 'react';
import WorkflowStudioCanvasRegion from './WorkflowStudioCanvasRegion';
import WorkflowStudioNodeLibrary from './WorkflowStudioNodeLibrary';

type WorkflowStudioCanvasRegionProps = React.ComponentProps<
  typeof WorkflowStudioCanvasRegion
>;

type WorkflowStudioEditingCallbacks = Required<
  Pick<
    WorkflowStudioCanvasRegionProps,
    | 'onCanvasSelect'
    | 'onConnectNodes'
    | 'onDeleteEdges'
    | 'onDeleteNodes'
    | 'onEdgeSelect'
    | 'onNodeLayoutChange'
    | 'onNodeSelect'
  >
>;

type WorkflowStudioEditorSurfaceProps = Omit<
  WorkflowStudioCanvasRegionProps,
  keyof WorkflowStudioEditingCallbacks | 'children'
> &
  WorkflowStudioEditingCallbacks & {
    readonly children?: React.ReactNode;
    readonly nodeLibraryOpen: boolean;
    readonly onCloseNodeLibrary: () => void;
    readonly onInsertNode: (stepType: string) => void;
  };

const WorkflowStudioEditorSurface: React.FC<
  WorkflowStudioEditorSurfaceProps
> = ({
  children,
  nodeLibraryOpen,
  onCloseNodeLibrary,
  onInsertNode,
  ...canvasProps
}) => (
  <WorkflowStudioCanvasRegion {...canvasProps}>
    <WorkflowStudioNodeLibrary
      onClose={onCloseNodeLibrary}
      onInsertNode={onInsertNode}
      open={nodeLibraryOpen}
    />
    {children}
  </WorkflowStudioCanvasRegion>
);

export default WorkflowStudioEditorSurface;
