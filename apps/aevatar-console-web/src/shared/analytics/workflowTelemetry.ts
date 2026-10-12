import { type AGUIEvent, AGUIEventType } from '@aevatar-react-sdk/types';
import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';
import { captureConsoleEvent } from './posthog';
import type { ConsoleEventProperties, WorkflowStartSource } from './types';

let fallbackInvocationSequence = 0;

function createTelemetryInvocationId(): string {
  try {
    const uuid = globalThis.crypto?.randomUUID?.();
    if (uuid) return uuid;
  } catch {
    // Optional telemetry must not prevent a run when crypto is unavailable.
  }
  fallbackInvocationSequence += 1;
  return `console-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${fallbackInvocationSequence.toString(36)}`;
}

function eventTime(timestamp: number | undefined): number {
  return typeof timestamp === 'number' &&
    Number.isFinite(timestamp) &&
    timestamp > 0
    ? timestamp
    : Date.now();
}

/** One observer belongs to one user invocation, never to a workflow catalogue row. */
export function createWorkflowTelemetry(
  scopeId: string,
  workflowId: string,
  nodeCount: number,
) {
  const invocationId = createTelemetryInvocationId();
  let runId = '';
  let startedAt: number | null = null;
  let startSource: WorkflowStartSource | null = null;
  let terminal = false;
  let finished = false;
  let disconnected = false;
  let disconnectReason:
    | ConsoleEventProperties['sse_disconnect']['reason']
    | null = null;

  const reportDisconnect = () => {
    if (startedAt === null || disconnected || !disconnectReason) return;
    disconnected = true;
    captureConsoleEvent('sse_disconnect', {
      workflowId,
      reason: disconnectReason,
    });
  };

  const start = (timestamp: number, source: WorkflowStartSource) => {
    if (startedAt !== null) {
      // A later authoritative observation can recover the time before the
      // first step. Never move the baseline forward or emit a second start.
      if (
        !finished &&
        startSource === 'first_step' &&
        source !== 'first_step' &&
        timestamp <= startedAt
      ) {
        startedAt = timestamp;
        startSource = source;
      }
      return;
    }
    startedAt = timestamp;
    startSource = source;
    captureConsoleEvent('workflow_started', {
      scopeId,
      workflowId,
      invocationId,
      nodeCount,
      start_source: source,
    });
    reportDisconnect();
  };

  const finish = (status: 'success' | 'failed', timestamp: number) => {
    terminal = true;
    if (finished || startedAt === null || startSource === null) return;
    finished = true;
    captureConsoleEvent('workflow_finished', {
      scopeId,
      workflowId,
      invocationId,
      status,
      duration: Math.max(0, timestamp - startedAt),
      start_source: startSource,
    });
  };

  return {
    observeEvent(event: AGUIEvent) {
      const record = event as unknown as Record<string, unknown>;
      if (
        event.type !== AGUIEventType.RUN_STARTED &&
        event.type !== AGUIEventType.STEP_STARTED &&
        event.type !== AGUIEventType.RUN_FINISHED &&
        event.type !== AGUIEventType.RUN_ERROR &&
        record.type !== 'RUN_STOPPED'
      )
        return;
      const reportedRunId =
        typeof record.runId === 'string' ? record.runId.trim() : '';
      if (runId && reportedRunId && runId !== reportedRunId) return;
      if (reportedRunId) runId = reportedRunId;
      const timestamp = eventTime(event.timestamp);

      if (event.type === AGUIEventType.RUN_STARTED) {
        if (!terminal) {
          // The transport may supply an arrival-time fallback for missing
          // timestamps. Such a value cannot replace an earlier first step.
          start(timestamp, 'run_started');
        }
      } else if (event.type === AGUIEventType.STEP_STARTED) {
        // Some workflow streams omit RUN_STARTED but report executed steps.
        // An accepted run context alone is deliberately not execution proof.
        if (!terminal) start(timestamp, 'first_step');
      } else if (event.type === AGUIEventType.RUN_ERROR) {
        finish('failed', timestamp);
      } else if (event.type === AGUIEventType.RUN_FINISHED) {
        // Workflow streams omit status on successful completion. Shared AGUI
        // streams explicitly distinguish completed, failed, and blocked.
        const status = record.status;
        if (
          status === 2 ||
          status === 3 ||
          status === 'RUN_COMPLETION_STATUS_FAILED' ||
          status === 'RUN_COMPLETION_STATUS_BLOCKED'
        ) {
          finish('failed', timestamp);
        } else if (
          status === undefined ||
          status === 1 ||
          status === 'RUN_COMPLETION_STATUS_COMPLETED'
        ) {
          finish('success', timestamp);
        } else {
          terminal = true;
        }
      } else if (record.type === 'RUN_STOPPED') {
        terminal = true;
      }
    },

    observeRun(run: WorkflowActivityRunDetail) {
      if (!runId || run.summary.runId !== runId || finished) return;
      const timestamp = Date.parse(run.summary.startedAtUtc ?? '');
      if (Number.isFinite(timestamp)) start(timestamp, 'activity');
      const completedAt = Date.parse(run.summary.updatedAtUtc);
      if (!Number.isFinite(completedAt)) return;
      switch (run.summary.status.toLowerCase()) {
        case 'completed':
          finish(
            run.summary.success === false ? 'failed' : 'success',
            completedAt,
          );
          break;
        case 'failed':
        case 'timed_out':
          finish('failed', completedAt);
          break;
        case 'stopped':
          terminal = true;
          break;
      }
    },

    disconnect(reason: ConsoleEventProperties['sse_disconnect']['reason']) {
      if (terminal || disconnected) return;
      disconnectReason = reason;
      reportDisconnect();
    },
  };
}
