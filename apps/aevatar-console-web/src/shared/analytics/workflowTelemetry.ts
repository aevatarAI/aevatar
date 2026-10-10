import { type AGUIEvent, AGUIEventType } from '@aevatar-react-sdk/types';
import type { WorkflowActivityRunDetail } from '@/shared/models/workflowActivity';
import { captureConsoleEvent } from './posthog';
import type { ConsoleEventProperties } from './types';

function eventTime(timestamp: number | undefined): number {
  return typeof timestamp === 'number' &&
    Number.isFinite(timestamp) &&
    timestamp > 0
    ? timestamp
    : Date.now();
}

/** One observer belongs to one user invocation, never to a workflow catalogue row. */
export function createWorkflowTelemetry(workflowId: string, nodeCount: number) {
  let runId = '';
  let startedAt: number | null = null;
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

  const start = (timestamp: number) => {
    if (startedAt !== null) return;
    startedAt = timestamp;
    captureConsoleEvent('workflow_started', { workflowId, nodeCount });
    reportDisconnect();
  };

  const finish = (status: 'success' | 'failed', timestamp: number) => {
    terminal = true;
    if (finished || startedAt === null) return;
    finished = true;
    captureConsoleEvent('workflow_finished', {
      workflowId,
      status,
      duration: Math.max(0, timestamp - startedAt),
    });
  };

  return {
    observeEvent(event: AGUIEvent) {
      const record = event as unknown as Record<string, unknown>;
      if (
        event.type !== AGUIEventType.RUN_STARTED &&
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
        if (!terminal) start(timestamp);
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
      if (Number.isFinite(timestamp)) start(timestamp);
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
