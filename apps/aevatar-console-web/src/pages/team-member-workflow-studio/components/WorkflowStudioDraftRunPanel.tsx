import React from 'react';
import WorkflowRunInputPanel from '@/shared/workflows/WorkflowRunInputPanel';

type WorkflowStudioDraftRunPanelProps = {
  readonly acceptedFileTypes?: string;
  readonly canRun: boolean;
  readonly disabledReason?: string;
  readonly files?: readonly File[];
  readonly onFilesAdd?: (files: readonly File[]) => void;
  readonly onFileRemove?: (index: number) => void;
  readonly onClose: () => void;
  readonly onRun: () => void;
  readonly onRunMessageChange: (message: string) => void;
  readonly open: boolean;
  readonly pending: boolean;
  readonly runMessage: string;
  readonly width?: number;
};

const WorkflowStudioDraftRunPanel: React.FC<
  WorkflowStudioDraftRunPanelProps
> = ({
  acceptedFileTypes,
  canRun,
  disabledReason,
  files = [],
  onFilesAdd,
  onFileRemove,
  ...props
}) => (
  <WorkflowRunInputPanel
    {...props}
    canRun={canRun}
    disabledReason={disabledReason}
    variant={{
      acceptedFileTypes,
      files,
      kind: 'draft',
      onFilesAdd: (nextFiles) => onFilesAdd?.(nextFiles),
      onFileRemove: (index) => onFileRemove?.(index),
    }}
  />
);

export default WorkflowStudioDraftRunPanel;
