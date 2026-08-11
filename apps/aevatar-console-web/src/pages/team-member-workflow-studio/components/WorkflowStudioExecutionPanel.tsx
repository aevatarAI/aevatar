import React from 'react';
import { t } from '@/shared/i18n/messages';
import type { WorkflowExecutionNodeSnapshot } from '@/shared/studio/execution';
import type { StudioExecutionDetail } from '@/shared/studio/models';
import { adaptExecutionDetailToLogs } from '@/shared/workflows/executionDetail';
import WorkflowExecutionLogsPanel from '@/shared/workflows/WorkflowExecutionLogsPanel';

type WorkflowStudioExecutionPanelProps = {
  readonly activeLogIndex?: number | null;
  readonly detail: StudioExecutionDetail | null;
  readonly error?: string;
  readonly height?: number;
  readonly onClear?: () => void;
  readonly onSelectLog?: (index: number | null) => void;
  readonly workflowNodes?: readonly WorkflowExecutionNodeSnapshot[];
};

const WorkflowStudioExecutionPanel: React.FC<
  WorkflowStudioExecutionPanelProps
> = ({ detail, ...props }) => {
  const execution = React.useMemo(
    () => adaptExecutionDetailToLogs(detail),
    [detail],
  );
  return (
    <WorkflowExecutionLogsPanel
      {...props}
      ariaLabel={t(
        'teamMemberWorkflowStudio.executionPanel.consoleAria',
        'Draft run console',
      )}
      execution={execution}
    />
  );
};

export default WorkflowStudioExecutionPanel;
