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
import { t } from '@/shared/i18n/messages';

export type MissionOperatorHandoff = {
  readonly actionLabel: string;
  readonly actionDetail: string;
  readonly connectionDetail: string;
  readonly evidenceDetail: string;
  readonly expectedResult: string;
  readonly inputLabel: string;
  readonly isActionable: boolean;
};

function describeInterventionInput(kind: MissionInterventionKind): string {
  switch (kind) {
    case 'waiting_signal':
      return t(
        'pages.missioncontrol.operatorhandoff.submit.the.signal.payload',
        'Submit the signal payload requested by the paused step.',
      );
    case 'human_input':
      return t(
        'pages.missioncontrol.operatorhandoff.add.the.missing.operator.context',
        'Add the missing operator context, then resume the run.',
      );
    case 'human_approval':
      return t(
        'pages.missioncontrol.operatorhandoff.approve.or.reject.with.a.short',
        'Approve or reject with a short decision note.',
      );
    default:
      return t(
        'pages.missioncontrol.operatorhandoff.review.the.operator.prompt.before',
        'Review the operator prompt before acting.',
      );
  }
}

function describeExpectedResult(kind: MissionInterventionKind): string {
  switch (kind) {
    case 'waiting_signal':
      return t(
        'pages.missioncontrol.operatorhandoff.after.the.signal.is.accepted',
        'After the signal is accepted, Mission Control waits for the next runtime snapshot before showing progress.',
      );
    case 'human_input':
      return t(
        'pages.missioncontrol.operatorhandoff.after.resume.is.accepted',
        'After resume is accepted, the run should continue from the blocked step and new evidence will appear in the dock.',
      );
    case 'human_approval':
      return t(
        'pages.missioncontrol.operatorhandoff.after.approve.or.reject',
        'After approve or reject is accepted, Mission Control waits for runtime confirmation of advance, stop, or rollback.',
      );
    default:
      return t(
        'pages.missioncontrol.operatorhandoff.after.the.action.is.accepted',
        'After the action is accepted, wait for runtime confirmation before treating the run as advanced.',
      );
  }
}

function describeConnection(
  status: MissionRuntimeConnectionStatus,
  hasIntervention: boolean,
): string {
  if (!hasIntervention) {
    if (status === 'idle') {
      return t(
        'pages.missioncontrol.operatorhandoff.attach.mission.control.to.a.live',
        'Attach Mission Control to a live run before taking action.',
      );
    }

    if (status === 'disconnected') {
      return t(
        'pages.missioncontrol.operatorhandoff.runtime.is.disconnected.this.view',
        'Runtime is disconnected; this view can show only the last known facts.',
      );
    }

    if (status === 'degraded') {
      return t(
        'pages.missioncontrol.operatorhandoff.live.stream.is.degraded.use',
        'Live stream is degraded; use the snapshot and dock as eventually consistent evidence.',
      );
    }

    return t(
      'pages.missioncontrol.operatorhandoff.connection.is.no.operator.action',
      'Connection is {connectionStatus}; no operator action is currently required.',
      { connectionStatus: formatConnectionLabel(status) },
    );
  }

  if (status === 'disconnected') {
    return t(
      'pages.missioncontrol.operatorhandoff.action.is.blocked.because.runtime',
      'Action is blocked because runtime is disconnected. Keep the evidence visible and retry after recovery.',
    );
  }

  if (status === 'degraded') {
    return t(
      'pages.missioncontrol.operatorhandoff.action.is.available.but.evidence',
      'Action is available, but evidence may lag because the live stream is degraded.',
    );
  }

  if (status === 'connecting') {
    return t(
      'pages.missioncontrol.operatorhandoff.mission.control.is.still.connecting',
      'Mission Control is still connecting; wait for runtime state before submitting an action.',
    );
  }

  return t(
    'pages.missioncontrol.operatorhandoff.runtime.is.reachable.submit.only',
    'Runtime is reachable; submit only after checking the prompt and recent evidence.',
  );
}

export function buildMissionOperatorHandoff(
  snapshot: MissionControlSnapshot,
  connectionStatus: MissionRuntimeConnectionStatus,
): MissionOperatorHandoff {
  const intervention = snapshot.intervention;
  if (!intervention) {
    const eventCount = snapshot.events.length;
    const eventUnit =
      eventCount === 1
        ? t('pages.missioncontrol.operatorhandoff.event', 'event')
        : t('pages.missioncontrol.operatorhandoff.events', 'events');

    return {
      actionLabel: t(
        'pages.missioncontrol.operatorhandoff.no.operator.action',
        'No operator action',
      ),
      actionDetail: t(
        'pages.missioncontrol.operatorhandoff.status.stage',
        '{status} - {stage}',
        {
          stage: snapshot.summary.activeStageLabel,
          status: formatMissionLabel(snapshot.summary.status),
        },
      ),
      connectionDetail: describeConnection(connectionStatus, false),
      evidenceDetail: t(
        'pages.missioncontrol.operatorhandoff.use.the.event.dock.as.read',
        'Use the event dock as read-only evidence. {eventCount} recent {eventUnit} are available.',
        { eventCount, eventUnit },
      ),
      expectedResult: t(
        'pages.missioncontrol.operatorhandoff.continue.observing.if.a.blocker',
        'Continue observing. If a blocker appears, Mission Control will open the intervention panel.',
      ),
      inputLabel: t(
        'pages.missioncontrol.operatorhandoff.observation.only',
        'Observation only',
      ),
      isActionable: false,
    };
  }

  const actionBlocked =
    connectionStatus === 'disconnected' || connectionStatus === 'connecting';

  return {
    actionLabel: formatInterventionLabel(intervention.kind),
    actionDetail: t(
      'pages.missioncontrol.operatorhandoff.title.step',
      '{title} - step {stepId}',
      { stepId: intervention.stepId, title: intervention.title },
    ),
    connectionDetail: describeConnection(connectionStatus, true),
    evidenceDetail: t(
      'pages.missioncontrol.operatorhandoff.read.the.intervention.prompt',
      'Read the intervention prompt, selected node state, and event dock before submitting an action.',
    ),
    expectedResult: describeExpectedResult(intervention.kind),
    inputLabel: describeInterventionInput(intervention.kind),
    isActionable: !actionBlocked,
  };
}
