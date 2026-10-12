import { CloseOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import {
  DeferredWorkflowPanel,
  WorkflowPanelLoadState,
} from '@/shared/workflows/DeferredWorkflowPanel';
import WorkflowStudioCanvasRegion from './WorkflowStudioCanvasRegion';

const loadNodeLibrary = () => import('./WorkflowStudioNodeLibrary');

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
    <DeferredWorkflowPanel
      active={nodeLibraryOpen}
      load={loadNodeLibrary}
      renderFallback={(retry) => (
        <aside
          style={{
            background: 'var(--wa-surface, #ffffff)',
            borderRight: '1px solid var(--wa-line, #e5e7eb)',
            bottom: 0,
            left: 0,
            maxWidth: 'calc(100% - 48px)',
            overflow: 'auto',
            padding: 16,
            position: 'absolute',
            top: 0,
            width: 380,
            zIndex: 30,
          }}
        >
          <Button
            aria-label={t(
              'teamMemberWorkflowStudio.nodeLibrary.closeAria',
              'Close node library',
            )}
            icon={<CloseOutlined />}
            onClick={onCloseNodeLibrary}
            type="text"
          />
          <WorkflowPanelLoadState retry={retry} />
        </aside>
      )}
    >
      {(WorkflowStudioNodeLibrary) => (
        <WorkflowStudioNodeLibrary
          onClose={onCloseNodeLibrary}
          onInsertNode={onInsertNode}
          open={nodeLibraryOpen}
        />
      )}
    </DeferredWorkflowPanel>
    {children}
  </WorkflowStudioCanvasRegion>
);

export default React.memo(WorkflowStudioEditorSurface);
