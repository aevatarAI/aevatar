import React from 'react';
import { t } from '@/shared/i18n/messages';
import type { WorkflowExecutionNodeSnapshot } from '@/shared/studio/execution';
import { buildExecutionTrace } from '@/shared/studio/execution';
import type { StudioExecutionDetail } from '@/shared/studio/models';
import WorkflowExecutionLogsPanel, {
  type WorkflowExecutionLogsModel,
} from '@/shared/workflows/WorkflowExecutionLogsPanel';

type WorkflowStudioExecutionPanelProps = {
  readonly activeLogIndex?: number | null;
  readonly clearDisabled?: boolean;
  readonly detail: StudioExecutionDetail | null;
  readonly error?: string;
  readonly height?: number;
  readonly onClear?: () => void;
  readonly onSelectLog?: (index: number | null) => void;
  readonly workflowNodes?: readonly WorkflowExecutionNodeSnapshot[];
};

function adaptStudioExecution(
  detail: StudioExecutionDetail | null,
): WorkflowExecutionLogsModel | null {
  if (!detail) return null;

  const trace = buildExecutionTrace(detail);
  if (!trace) return null;

  return {
    completedAtUtc: detail.completedAtUtc,
    eventCount: detail.frames.length,
    outputText: detail.output ?? '',
    startedAtUtc: detail.startedAtUtc,
    status: detail.status,
    trace,
    workflowName: detail.workflowName,
  };
}

const WorkflowStudioExecutionPanel: React.FC<
  WorkflowStudioExecutionPanelProps
> = ({ detail, ...props }) => {
  const execution = React.useMemo(() => adaptStudioExecution(detail), [detail]);
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
