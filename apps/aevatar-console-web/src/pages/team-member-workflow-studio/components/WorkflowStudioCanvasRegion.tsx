import React from 'react';
import WorkflowStudioCanvas from './WorkflowStudioCanvas';

type WorkflowStudioCanvasRegionProps = React.ComponentProps<
  typeof WorkflowStudioCanvas
> & {
  readonly ariaLabel?: string;
  readonly children?: React.ReactNode;
  readonly style?: React.CSSProperties;
};

const WorkflowStudioCanvasRegion = React.forwardRef<
  HTMLElement,
  WorkflowStudioCanvasRegionProps
>(function WorkflowStudioCanvasRegion(
  { ariaLabel, children, style, ...canvasProps },
  ref,
) {
  return (
    <section
      aria-label={ariaLabel}
      ref={ref}
      style={{
        display: 'flex',
        flex: 1,
        minHeight: 0,
        overflow: 'hidden',
        position: 'relative',
        ...style,
      }}
    >
      <div
        style={{
          display: 'flex',
          flex: 1,
          height: '100%',
          minHeight: 0,
          minWidth: 0,
          width: '100%',
        }}
      >
        <WorkflowStudioCanvas {...canvasProps} />
      </div>
      {children}
    </section>
  );
});

export default WorkflowStudioCanvasRegion;
