import React from 'react';
import type { WorkflowPanelResizeHandleProps } from './useWorkflowPanelResize';

type WorkflowPanelResizeHandleComponentProps =
  WorkflowPanelResizeHandleProps & {
    readonly ariaLabel: string;
    readonly className?: string;
    readonly orientation: 'horizontal' | 'vertical';
  };

const WorkflowPanelResizeHandle: React.FC<
  WorkflowPanelResizeHandleComponentProps
> = ({
  ariaLabel,
  className,
  max,
  min,
  onKeyDown,
  onMouseDown,
  orientation,
  value,
}) => {
  const vertical = orientation === 'vertical';
  return (
    <hr
      aria-label={ariaLabel}
      aria-orientation={orientation}
      aria-valuemax={max}
      aria-valuemin={min}
      aria-valuenow={value}
      className={className}
      onKeyDown={onKeyDown}
      onMouseDown={onMouseDown}
      style={
        vertical
          ? {
              background: '#cbd5e1',
              borderBottom: 0,
              borderLeft: '3px solid #ffffff',
              borderRight: '3px solid #ffffff',
              borderTop: 0,
              boxSizing: 'border-box',
              cursor: 'col-resize',
              flex: '0 0 10px',
              height: '100%',
              margin: 0,
              minHeight: 0,
              position: 'relative',
              zIndex: 3,
            }
          : {
              background: '#cbd5e1',
              borderBottom: '4px solid #ffffff',
              borderLeft: 0,
              borderRight: 0,
              borderTop: '4px solid #ffffff',
              boxSizing: 'border-box',
              cursor: 'row-resize',
              flex: '0 0 12px',
              height: 12,
              margin: 0,
              position: 'relative',
              width: '100%',
              zIndex: 3,
            }
      }
      tabIndex={0}
    />
  );
};

export default WorkflowPanelResizeHandle;
