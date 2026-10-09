import React from 'react';

export const WORKFLOW_SIDE_PANEL_DEFAULT_WIDTH = 420;
export const WORKFLOW_SIDE_PANEL_MIN_WIDTH = 320;
export const WORKFLOW_SIDE_PANEL_MAX_WIDTH = 640;
export const WORKFLOW_CANVAS_MIN_WIDTH = 360;
export const WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT = 160;
export const WORKFLOW_EXECUTION_PANEL_MAX_HEIGHT = 520;
export const WORKFLOW_EXECUTION_PANEL_MAX_HEIGHT_RATIO = 0.6;
export const WORKFLOW_PANEL_RESIZE_KEYBOARD_STEP = 24;

type ResizeContainerRef = React.RefObject<HTMLElement | null>;

type UseWorkflowPanelResizeOptions = {
  readonly editorRegionRef: ResizeContainerRef;
  readonly executionPanelMaxHeight?: number;
  readonly executionPanelMaxHeightRatio?: number;
  readonly initialExecutionPanelHeight: number;
  readonly mainRef: ResizeContainerRef;
};

export type WorkflowPanelResizeHandleProps = {
  readonly max: number;
  readonly min: number;
  readonly onKeyDown: React.KeyboardEventHandler<HTMLHRElement>;
  readonly onMouseDown: React.MouseEventHandler<HTMLHRElement>;
  readonly value: number;
};

function clampDimension(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

function resolveSidePanelMaxWidth(container: HTMLElement | null): number {
  if (!container?.clientWidth) return WORKFLOW_SIDE_PANEL_MAX_WIDTH;
  return Math.max(
    WORKFLOW_SIDE_PANEL_MIN_WIDTH,
    Math.min(
      WORKFLOW_SIDE_PANEL_MAX_WIDTH,
      container.clientWidth - WORKFLOW_CANVAS_MIN_WIDTH,
    ),
  );
}

function resolveExecutionPanelMaxHeight(
  container: HTMLElement | null,
  maximum: number,
  ratio: number,
): number {
  if (!container?.clientHeight) return maximum;
  return Math.max(
    WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT,
    Math.min(maximum, Math.floor(container.clientHeight * ratio)),
  );
}

export function useWorkflowPanelResize({
  editorRegionRef,
  executionPanelMaxHeight = WORKFLOW_EXECUTION_PANEL_MAX_HEIGHT,
  executionPanelMaxHeightRatio = WORKFLOW_EXECUTION_PANEL_MAX_HEIGHT_RATIO,
  initialExecutionPanelHeight,
  mainRef,
}: UseWorkflowPanelResizeOptions) {
  const resizeCleanupRef = React.useRef<(() => void) | null>(null);
  const [sidePanelWidth, setSidePanelWidth] = React.useState(
    WORKFLOW_SIDE_PANEL_DEFAULT_WIDTH,
  );
  const [executionPanelHeight, setExecutionPanelHeight] = React.useState(
    initialExecutionPanelHeight,
  );
  const [sidePanelMaxWidth, setSidePanelMaxWidth] = React.useState(
    WORKFLOW_SIDE_PANEL_MAX_WIDTH,
  );
  const [resolvedExecutionPanelMaxHeight, setExecutionPanelMaxHeight] =
    React.useState(executionPanelMaxHeight);

  const refreshAvailableBounds = React.useCallback(() => {
    resizeCleanupRef.current?.();
    const nextSidePanelMaxWidth = resolveSidePanelMaxWidth(
      editorRegionRef.current,
    );
    const nextExecutionPanelMaxHeight = resolveExecutionPanelMaxHeight(
      mainRef.current,
      executionPanelMaxHeight,
      executionPanelMaxHeightRatio,
    );

    setSidePanelMaxWidth(nextSidePanelMaxWidth);
    setExecutionPanelMaxHeight(nextExecutionPanelMaxHeight);
    setSidePanelWidth((currentWidth) =>
      clampDimension(
        currentWidth,
        WORKFLOW_SIDE_PANEL_MIN_WIDTH,
        nextSidePanelMaxWidth,
      ),
    );
    setExecutionPanelHeight((currentHeight) =>
      clampDimension(
        currentHeight,
        WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT,
        nextExecutionPanelMaxHeight,
      ),
    );
  }, [
    editorRegionRef,
    executionPanelMaxHeight,
    executionPanelMaxHeightRatio,
    mainRef,
  ]);

  React.useLayoutEffect(() => {
    refreshAvailableBounds();
    const observer =
      typeof ResizeObserver === 'undefined'
        ? null
        : new ResizeObserver(refreshAvailableBounds);
    const containers = [editorRegionRef.current, mainRef.current].filter(
      (container): container is HTMLElement => Boolean(container),
    );
    containers.forEach((container) => {
      observer?.observe(container);
    });
    window.addEventListener('resize', refreshAvailableBounds);

    return () => {
      observer?.disconnect();
      window.removeEventListener('resize', refreshAvailableBounds);
      resizeCleanupRef.current?.();
    };
  }, [editorRegionRef, mainRef, refreshAvailableBounds]);

  const attachResizeListeners = React.useCallback(
    (
      cursor: 'col-resize' | 'row-resize',
      onMouseMove: (event: MouseEvent) => void,
    ) => {
      resizeCleanupRef.current?.();
      const previousCursor = document.body.style.cursor;
      const previousUserSelect = document.body.style.userSelect;
      document.body.style.cursor = cursor;
      document.body.style.userSelect = 'none';

      const cleanup = () => {
        window.removeEventListener('mousemove', onMouseMove);
        window.removeEventListener('mouseup', cleanup);
        document.body.style.cursor = previousCursor;
        document.body.style.userSelect = previousUserSelect;
        if (resizeCleanupRef.current === cleanup) {
          resizeCleanupRef.current = null;
        }
      };

      resizeCleanupRef.current = cleanup;
      window.addEventListener('mousemove', onMouseMove);
      window.addEventListener('mouseup', cleanup);
    },
    [],
  );

  const updateSidePanelWidth = React.useCallback(
    (nextWidth: number) => {
      setSidePanelWidth(
        clampDimension(
          nextWidth,
          WORKFLOW_SIDE_PANEL_MIN_WIDTH,
          resolveSidePanelMaxWidth(editorRegionRef.current),
        ),
      );
    },
    [editorRegionRef],
  );

  const updateExecutionPanelHeight = React.useCallback(
    (nextHeight: number) => {
      setExecutionPanelHeight(
        clampDimension(
          nextHeight,
          WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT,
          resolveExecutionPanelMaxHeight(
            mainRef.current,
            executionPanelMaxHeight,
            executionPanelMaxHeightRatio,
          ),
        ),
      );
    },
    [executionPanelMaxHeight, executionPanelMaxHeightRatio, mainRef],
  );

  const startSidePanelResize = React.useCallback(
    (event: React.MouseEvent<HTMLHRElement>) => {
      event.preventDefault();
      event.currentTarget.focus();
      const startX = event.clientX;
      const startWidth = sidePanelWidth;
      const maxWidth = resolveSidePanelMaxWidth(editorRegionRef.current);
      attachResizeListeners('col-resize', (moveEvent) => {
        setSidePanelWidth(
          clampDimension(
            startWidth + (startX - moveEvent.clientX),
            WORKFLOW_SIDE_PANEL_MIN_WIDTH,
            maxWidth,
          ),
        );
      });
    },
    [attachResizeListeners, editorRegionRef, sidePanelWidth],
  );

  const startExecutionPanelResize = React.useCallback(
    (event: React.MouseEvent<HTMLHRElement>) => {
      event.preventDefault();
      event.currentTarget.focus();
      const startY = event.clientY;
      const startHeight = executionPanelHeight;
      const maxHeight = resolveExecutionPanelMaxHeight(
        mainRef.current,
        executionPanelMaxHeight,
        executionPanelMaxHeightRatio,
      );
      attachResizeListeners('row-resize', (moveEvent) => {
        setExecutionPanelHeight(
          clampDimension(
            startHeight + (startY - moveEvent.clientY),
            WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT,
            maxHeight,
          ),
        );
      });
    },
    [
      attachResizeListeners,
      executionPanelHeight,
      executionPanelMaxHeight,
      executionPanelMaxHeightRatio,
      mainRef,
    ],
  );

  const handleSidePanelResizeKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLHRElement>) => {
      const direction =
        event.key === 'ArrowLeft' ? 1 : event.key === 'ArrowRight' ? -1 : 0;
      if (direction) {
        event.preventDefault();
        updateSidePanelWidth(
          sidePanelWidth + direction * WORKFLOW_PANEL_RESIZE_KEYBOARD_STEP,
        );
      } else if (event.key === 'Home') {
        event.preventDefault();
        updateSidePanelWidth(WORKFLOW_SIDE_PANEL_MIN_WIDTH);
      } else if (event.key === 'End') {
        event.preventDefault();
        updateSidePanelWidth(resolveSidePanelMaxWidth(editorRegionRef.current));
      }
    },
    [editorRegionRef, sidePanelWidth, updateSidePanelWidth],
  );

  const handleExecutionPanelResizeKeyDown = React.useCallback(
    (event: React.KeyboardEvent<HTMLHRElement>) => {
      const direction =
        event.key === 'ArrowUp' ? 1 : event.key === 'ArrowDown' ? -1 : 0;
      if (direction) {
        event.preventDefault();
        updateExecutionPanelHeight(
          executionPanelHeight +
            direction * WORKFLOW_PANEL_RESIZE_KEYBOARD_STEP,
        );
      } else if (event.key === 'Home') {
        event.preventDefault();
        updateExecutionPanelHeight(WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT);
      } else if (event.key === 'End') {
        event.preventDefault();
        updateExecutionPanelHeight(
          resolveExecutionPanelMaxHeight(
            mainRef.current,
            executionPanelMaxHeight,
            executionPanelMaxHeightRatio,
          ),
        );
      }
    },
    [
      executionPanelHeight,
      executionPanelMaxHeight,
      executionPanelMaxHeightRatio,
      mainRef,
      updateExecutionPanelHeight,
    ],
  );

  return {
    executionPanelHandleProps: {
      max: resolvedExecutionPanelMaxHeight,
      min: WORKFLOW_EXECUTION_PANEL_MIN_HEIGHT,
      onKeyDown: handleExecutionPanelResizeKeyDown,
      onMouseDown: startExecutionPanelResize,
      value: executionPanelHeight,
    } satisfies WorkflowPanelResizeHandleProps,
    executionPanelHeight,
    sidePanelHandleProps: {
      max: sidePanelMaxWidth,
      min: WORKFLOW_SIDE_PANEL_MIN_WIDTH,
      onKeyDown: handleSidePanelResizeKeyDown,
      onMouseDown: startSidePanelResize,
      value: sidePanelWidth,
    } satisfies WorkflowPanelResizeHandleProps,
    sidePanelWidth,
  };
}
