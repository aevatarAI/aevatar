import { t } from '@/shared/i18n/messages';
import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';
import type {
  ExecutionLogItem,
  ExecutionTrace,
  StepExecutionState,
} from '@/shared/studio/execution';
import type { WorkflowExecutionLogsModel } from './WorkflowExecutionLogsPanel';

function formatRecord(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value;

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function preview(value: string): string {
  const text = value.trim();
  return text.length > 180 ? `${text.slice(0, 177)}...` : text;
}

function createStepState(
  step: WorkflowActivityRunDetail['steps'][number],
): StepExecutionState {
  const waiting = Boolean(step.suspensionType);
  return {
    branchKey: step.branchKey,
    completedAt: step.completedAtUtc,
    error: step.error,
    nextStepId: step.nextStepId,
    startedAt: step.requestedAtUtc,
    status: waiting
      ? 'waiting'
      : step.completedAtUtc
        ? step.success === false
          ? 'failed'
          : 'completed'
        : step.requestedAtUtc
          ? 'active'
          : 'idle',
    stepId: step.stepId,
    stepType: step.stepType,
    success: step.success,
    targetRole: step.targetRole,
  };
}

export function buildActivityExecutionTrace(
  run: WorkflowActivityRunDetail,
): ExecutionTrace {
  const logs: ExecutionLogItem[] = [];
  const stepStates = new Map<string, StepExecutionState>();
  const traversedEdges = new Set<string>();

  if (run.summary.startedAtUtc) {
    logs.push({
      category: 'lifecycle',
      clipboardText: run.input,
      interaction: null,
      meta: run.summary.workflowName,
      previewText: preview(run.input),
      stepId: null,
      timestamp: run.summary.startedAtUtc,
      title: t('shared.studio.execution.run.started', 'Run started'),
      tone: 'run',
      eventType: 'RUN_STARTED',
    });
  }

  for (const step of run.steps) {
    if (!step.stepId.trim()) continue;

    stepStates.set(step.stepId, createStepState(step));
    if (step.nextStepId)
      traversedEdges.add(`${step.stepId}->${step.nextStepId}`);

    const requestText = formatRecord(step.requestParameters);
    if (step.requestedAtUtc) {
      logs.push({
        category: 'step',
        clipboardText: requestText,
        interaction: null,
        meta: [step.stepType, step.targetRole].filter(Boolean).join(' · '),
        payloadText: requestText,
        previewText: preview(requestText),
        stepId: step.stepId,
        timestamp: step.requestedAtUtc,
        title: `${step.stepId} started`,
        tone: 'started',
        eventType: 'ACTIVITY_STEP_REQUESTED',
      });
    }

    if (step.suspensionType) {
      const suspensionText = step.suspensionPrompt || step.suspensionContent;
      logs.push({
        category: 'step',
        clipboardText: suspensionText,
        interaction: null,
        meta: step.suspensionType,
        previewText: preview(suspensionText),
        stepId: step.stepId,
        timestamp:
          step.completedAtUtc ||
          step.requestedAtUtc ||
          run.summary.updatedAtUtc,
        title: `${step.stepId} waiting`,
        tone: 'pending',
        eventType: 'ACTIVITY_STEP_SUSPENDED',
      });
    } else if (step.completedAtUtc) {
      const resultText = step.error || step.outputPreview;
      logs.push({
        category: 'step',
        clipboardText: resultText,
        interaction: null,
        meta: [step.stepType, step.branchKey ? `branch ${step.branchKey}` : '']
          .filter(Boolean)
          .join(' · '),
        previewText: preview(resultText),
        stepId: step.stepId,
        timestamp: step.completedAtUtc,
        title: `${step.stepId} ${step.success === false ? 'failed' : 'completed'}`,
        tone: step.success === false ? 'failed' : 'completed',
        eventType: 'ACTIVITY_STEP_COMPLETED',
      });
    }
  }

  for (const event of run.timeline) {
    const payloadText = formatRecord(event.data);
    logs.push({
      category: 'custom',
      clipboardText: event.content || event.message || payloadText,
      eventType: event.kind || event.stage,
      interaction: null,
      meta: [event.stage, event.agentId].filter(Boolean).join(' · '),
      payloadText,
      previewText: preview(event.message || event.content || payloadText),
      stepId: event.stepId || null,
      timestamp: event.timestampUtc,
      title: event.message || event.kind || event.stage || 'Activity event',
      tone: 'run',
    });
  }

  if (run.finalOutput) {
    logs.push({
      category: 'output',
      clipboardText: run.finalOutput,
      interaction: null,
      meta: '',
      previewText: preview(run.finalOutput),
      stepId: null,
      timestamp: run.summary.updatedAtUtc,
      title: t('shared.studio.execution.run.finished', 'Run finished'),
      tone: 'run',
      eventType: 'RUN_FINISHED',
    });
  }

  if (run.finalError) {
    logs.push({
      category: 'lifecycle',
      clipboardText: run.finalError,
      interaction: null,
      meta: '',
      previewText: preview(run.finalError),
      stepId: null,
      timestamp: run.summary.updatedAtUtc,
      title: t('shared.studio.execution.run.failed', 'Run failed'),
      tone: 'failed',
      eventType: 'RUN_ERROR',
    });
  }

  if (run.usageTotals.totalTokens > 0) {
    const payloadText = formatRecord(run.usageTotals);
    logs.push({
      category: 'usage',
      clipboardText: payloadText,
      interaction: null,
      meta: `total ${run.usageTotals.totalTokens}`,
      payloadText,
      previewText: preview(payloadText),
      stepId: null,
      timestamp: run.summary.updatedAtUtc,
      title: t('shared.workflowExecutionLogs.tokenUsage', 'Token usage'),
      tone: 'run',
      eventType: 'ACTIVITY_USAGE',
    });
  }

  let defaultLogIndex: number | null = null;
  for (let index = logs.length - 1; index >= 0; index -= 1) {
    if (logs[index].stepId) {
      defaultLogIndex = index;
      break;
    }
  }

  return {
    defaultLogIndex,
    latestStepId: run.steps.at(-1)?.stepId || null,
    logs,
    stepStates,
    traversedEdges,
  };
}

export function adaptActivityRunToExecutionLogs(
  run: WorkflowActivityRunDetail,
): WorkflowExecutionLogsModel {
  const trace = buildActivityExecutionTrace(run);
  const terminal = isTerminalActivityRunStatus(run.summary.status);

  return {
    completedAtUtc: terminal ? run.summary.updatedAtUtc : null,
    eventCount: trace.logs.length,
    outputText: run.finalOutput,
    startedAtUtc: run.summary.startedAtUtc,
    status: run.summary.status,
    trace,
    workflowName: run.summary.workflowName,
  };
}

export function isTerminalActivityRunStatus(status: string): boolean {
  return [
    'completed',
    'succeeded',
    'failed',
    'timed_out',
    'timedout',
    'canceled',
    'cancelled',
    'stopped',
  ].includes(status.trim().toLowerCase());
}
