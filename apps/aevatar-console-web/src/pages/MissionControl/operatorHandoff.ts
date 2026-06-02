import type {
  MissionControlSnapshot,
  MissionInterventionKind,
  MissionRuntimeConnectionStatus,
} from './models';
import {
  formatConnectionLabel,
  formatInterventionLabel,
  formatMissionLabel,
} from './presentation';

export type MissionOperatorHandoff = {
  readonly actionLabel: string;
  readonly actionDetail: string;
  readonly connectionDetail: string;
  readonly evidenceDetail: string;
  readonly expectedResult: string;
  readonly inputLabel: string;
  readonly isActionable: boolean;
  readonly statusLabel: string;
};

function describeInterventionInput(kind: MissionInterventionKind): string {
  switch (kind) {
    case 'waiting_signal':
      return 'Submit the signal payload requested by the paused step.';
    case 'human_input':
      return 'Add the missing operator context, then resume the run.';
    case 'human_approval':
      return 'Approve or reject with a short decision note.';
    default:
      return 'Review the operator prompt before acting.';
  }
}

function describeExpectedResult(kind: MissionInterventionKind): string {
  switch (kind) {
    case 'waiting_signal':
      return 'After the signal is accepted, Mission Control waits for the next runtime snapshot before showing progress.';
    case 'human_input':
      return 'After resume is accepted, the run should continue from the blocked step and new evidence will appear in the dock.';
    case 'human_approval':
      return 'After approve or reject is accepted, Mission Control waits for runtime confirmation of advance, stop, or rollback.';
    default:
      return 'After the action is accepted, wait for runtime confirmation before treating the run as advanced.';
  }
}

function describeConnection(
  status: MissionRuntimeConnectionStatus,
  hasIntervention: boolean,
): string {
  if (!hasIntervention) {
    if (status === 'idle') {
      return 'Attach Mission Control to a live run before taking action.';
    }

    if (status === 'disconnected') {
      return 'Runtime is disconnected; this view can show only the last known facts.';
    }

    if (status === 'degraded') {
      return 'Live stream is degraded; use the snapshot and dock as eventually consistent evidence.';
    }

    return `Connection is ${formatConnectionLabel(status)}; no operator action is currently required.`;
  }

  if (status === 'disconnected') {
    return 'Action is blocked because runtime is disconnected. Keep the evidence visible and retry after recovery.';
  }

  if (status === 'degraded') {
    return 'Action is available, but evidence may lag because the live stream is degraded.';
  }

  if (status === 'connecting') {
    return 'Mission Control is still connecting; wait for runtime state before submitting an action.';
  }

  return 'Runtime is reachable; submit only after checking the prompt and recent evidence.';
}

export function buildMissionOperatorHandoff(
  snapshot: MissionControlSnapshot,
  connectionStatus: MissionRuntimeConnectionStatus,
): MissionOperatorHandoff {
  const intervention = snapshot.intervention;
  if (!intervention) {
    return {
      actionLabel: 'No operator action',
      actionDetail: `${formatMissionLabel(snapshot.summary.status)} - ${snapshot.summary.activeStageLabel}`,
      connectionDetail: describeConnection(connectionStatus, false),
      evidenceDetail: `Use the event dock as read-only evidence. ${snapshot.events.length} recent event${
        snapshot.events.length === 1 ? '' : 's'
      } are available.`,
      expectedResult:
        'Continue observing. If a blocker appears, Mission Control will open the intervention panel.',
      inputLabel: 'Observation only',
      isActionable: false,
      statusLabel: formatMissionLabel(snapshot.summary.status),
    };
  }

  const actionBlocked =
    connectionStatus === 'disconnected' || connectionStatus === 'connecting';

  return {
    actionLabel: formatInterventionLabel(intervention.kind),
    actionDetail: `${intervention.title} - step ${intervention.stepId}`,
    connectionDetail: describeConnection(connectionStatus, true),
    evidenceDetail:
      'Read the intervention prompt, selected node state, and event dock before submitting an action.',
    expectedResult: describeExpectedResult(intervention.kind),
    inputLabel: describeInterventionInput(intervention.kind),
    isActionable: !actionBlocked,
    statusLabel: formatMissionLabel(snapshot.summary.status),
  };
}
