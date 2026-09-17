import type { RuntimeEventAccumulator } from '@/shared/agui/runtimeEventSemantics';
import { buildExecutionTrace } from '@/shared/studio/execution';
import type {
  StudioExecutionDetail,
  StudioExecutionFrame,
} from '@/shared/studio/models';
import type { WorkflowExecutionLogsModel } from './WorkflowExecutionLogsPanel';

type StreamingExecutionDetailInput = {
  readonly accumulator: RuntimeEventAccumulator;
  readonly completedAtUtc?: string | null;
  readonly error?: string | null;
  readonly executionId: string;
  readonly frames: readonly StudioExecutionFrame[];
  readonly prompt: string;
  readonly serviceId: string;
  readonly startedAtUtc: string;
  readonly status: string;
  readonly workflowName: string;
};

export function createStreamingExecutionDetail(
  input: StreamingExecutionDetailInput,
): StudioExecutionDetail {
  const output =
    input.error ||
    input.accumulator.finalOutput ||
    input.accumulator.assistantText ||
    '';

  return {
    actorId: input.accumulator.actorId || null,
    auditSource: 'invoke-session',
    completedAtUtc: input.completedAtUtc ?? null,
    error: input.error ?? null,
    executionId: input.accumulator.runId || input.executionId,
    frames: [...input.frames],
    output,
    prompt: input.prompt,
    serviceId: input.serviceId || null,
    startedAtUtc: input.startedAtUtc,
    status: input.status,
    workflowName: input.workflowName,
  };
}

export function adaptExecutionDetailToLogs(
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
