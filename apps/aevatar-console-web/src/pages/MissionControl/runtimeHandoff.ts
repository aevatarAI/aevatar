import type {
  MissionHandoffCue,
  MissionHandoffSeverity,
  MissionInterventionActionKind,
  MissionInterventionState,
  MissionNodeStatus,
  MissionObservationStatus,
  MissionRuntimeConnectionStatus,
  MissionRunStatus,
  MissionTopologyNodeKind,
  WorkflowExecutionEventType,
} from './models';
import { formatMissionLabel } from './presentation';
import { t } from '@/shared/i18n/messages';

function observationEvidence(status: MissionObservationStatus, freshnessLabel: string) {
  switch (status) {
    case 'streaming':
      return t(
        'pages.missioncontrol.runtimehandoff.live.runtime.evidence.fresh',
        'Live runtime evidence, fresh {freshnessLabel}.',
        { freshnessLabel },
      );
    case 'snapshot_available':
      return t(
        'pages.missioncontrol.runtimehandoff.snapshot.evidence.is.available',
        'Snapshot evidence is available, fresh {freshnessLabel}.',
        { freshnessLabel },
      );
    case 'projection_settled':
      return t(
        'pages.missioncontrol.runtimehandoff.committed.terminal.evidence.fresh',
        'Committed terminal evidence, fresh {freshnessLabel}.',
        { freshnessLabel },
      );
    case 'delayed':
      return t(
        'pages.missioncontrol.runtimehandoff.last.known.evidence.is.delayed',
        'Last known evidence is delayed, fresh {freshnessLabel}.',
        { freshnessLabel },
      );
    default:
      return t(
        'pages.missioncontrol.runtimehandoff.no.runtime.evidence.is.attached',
        'No runtime evidence is attached to this node yet.',
      );
  }
}

function severityForNode(
  connectionStatus: MissionRuntimeConnectionStatus,
  observationStatus: MissionObservationStatus,
  nodeStatus: MissionNodeStatus,
  isInterventionNode: boolean,
): MissionHandoffSeverity {
  if (connectionStatus === 'disconnected') {
    return 'blocked';
  }

  if (isInterventionNode) {
    return connectionStatus === 'connecting' ? 'blocked' : 'action';
  }

  if (nodeStatus === 'waiting' || observationStatus === 'delayed') {
    return 'blocked';
  }

  if (nodeStatus === 'active' || observationStatus === 'streaming') {
    return 'confirming';
  }

  return 'observe';
}

function nextStepForNode(
  severity: MissionHandoffSeverity,
  kind: MissionTopologyNodeKind,
  isInterventionNode: boolean,
) {
  if (isInterventionNode) {
    return kind === 'approval'
      ? t(
          'pages.missioncontrol.runtimehandoff.open.the.intervention.panel.review',
          'Open the intervention panel, review recent evidence, then approve or reject.',
        )
      : t(
          'pages.missioncontrol.runtimehandoff.open.the.intervention.panel.provide',
          'Open the intervention panel, provide the requested signal or context, then wait for runtime confirmation.',
        );
  }

  switch (severity) {
    case 'blocked':
      return t(
        'pages.missioncontrol.runtimehandoff.keep.this.as.evidence.and.wait',
        'Keep this as evidence and wait for a newer runtime snapshot before acting.',
      );
    case 'confirming':
      return t(
        'pages.missioncontrol.runtimehandoff.observe.the.next.runtime.event',
        'Observe the next runtime event before treating this step as complete.',
      );
    default:
      return t(
        'pages.missioncontrol.runtimehandoff.use.this.node.as.read.only',
        'Use this node as read-only evidence for the current run.',
      );
  }
}

export function buildMissionNodeHandoffCue(input: {
  connectionStatus: MissionRuntimeConnectionStatus;
  freshnessLabel: string;
  isInterventionNode: boolean;
  kind: MissionTopologyNodeKind;
  label: string;
  observationStatus: MissionObservationStatus;
  status: MissionNodeStatus;
}): MissionHandoffCue {
  const severity = severityForNode(
    input.connectionStatus,
    input.observationStatus,
    input.status,
    input.isInterventionNode,
  );
  const title = input.isInterventionNode
    ? t('pages.missioncontrol.runtimehandoff.operator.handoff', 'Operator handoff')
    : severity === 'blocked'
      ? t('pages.missioncontrol.runtimehandoff.evidence.blocked', 'Evidence blocked')
      : severity === 'confirming'
        ? t(
            'pages.missioncontrol.runtimehandoff.runtime.confirmation',
            'Runtime confirmation',
          )
        : t(
            'pages.missioncontrol.runtimehandoff.observation.evidence',
            'Observation evidence',
          );

  return {
    detail: t(
      'pages.missioncontrol.runtimehandoff.is.with.evidence',
      '{value1} is {value2} with {value3} evidence.',
      {
        value1: input.label,
        value2: formatMissionLabel(input.status),
        value3: formatMissionLabel(input.observationStatus),
      },
    ),
    evidence: observationEvidence(input.observationStatus, input.freshnessLabel),
    nextStep: nextStepForNode(severity, input.kind, input.isInterventionNode),
    severity,
    title,
  };
}

export function buildMissionEventHandoffCue(input: {
  actorId?: string;
  detail: string;
  intervention?: MissionInterventionState;
  runStatus: MissionRunStatus;
  stepId?: string;
  type: WorkflowExecutionEventType;
}): MissionHandoffCue {
  const isInterventionEvent =
    input.type === 'waiting_for_signal' ||
    input.type === 'workflow_suspended' ||
    input.stepId === input.intervention?.stepId;
  const terminal =
    input.type === 'workflow_completed' ||
    input.type === 'workflow_stopped' ||
    input.runStatus === 'completed' ||
    input.runStatus === 'failed' ||
    input.runStatus === 'stopped';

  if (isInterventionEvent && input.intervention) {
    return {
      detail: t(
        'pages.missioncontrol.runtimehandoff.at.step',
        '{value1} at step {value2}.',
        { value1: input.intervention.title, value2: input.intervention.stepId },
      ),
      evidence:
        input.detail ||
        t(
          'pages.missioncontrol.runtimehandoff.runtime.published.a.blocking.event',
          'Runtime published a blocking event.',
        ),
      nextStep:
        input.intervention.kind === 'human_approval'
          ? t(
              'pages.missioncontrol.runtimehandoff.open.the.intervention.panel.and.decide',
              'Open the intervention panel and decide with the latest event dock evidence.',
            )
          : t(
              'pages.missioncontrol.runtimehandoff.open.the.intervention.panel.and.submit',
              'Open the intervention panel and submit the requested context or signal.',
            ),
      severity: 'action',
      title: t(
        'pages.missioncontrol.runtimehandoff.action.handoff',
        'Action handoff',
      ),
    };
  }

  if (terminal) {
    return {
      detail: t(
        'pages.missioncontrol.runtimehandoff.this.event.reflects.terminal.or.settled',
        'This event reflects a terminal or settled runtime fact.',
      ),
      evidence:
        input.detail ||
        t(
          'pages.missioncontrol.runtimehandoff.runtime.emitted.terminal.evidence',
          'Runtime emitted terminal evidence.',
        ),
      nextStep: t(
        'pages.missioncontrol.runtimehandoff.use.this.event.as.committed',
        'Use this event as committed evidence; no operator action is implied.',
      ),
      severity: 'observe',
      title: t(
        'pages.missioncontrol.runtimehandoff.settled.evidence',
        'Settled evidence',
      ),
    };
  }

  if (input.type === 'step_requested' || input.type === 'workflow_run_execution_started') {
    return {
      detail: t(
        'pages.missioncontrol.runtimehandoff.runtime.accepted.work.and.queued.the',
        'Runtime accepted work and queued the next step.',
      ),
      evidence:
        input.detail ||
        t(
          'pages.missioncontrol.runtimehandoff.runtime.emitted.an.execution.start',
          'Runtime emitted an execution start event.',
        ),
      nextStep: t(
        'pages.missioncontrol.runtimehandoff.wait.for.the.matching.completion',
        'Wait for the matching completion, suspension, or signal event.',
      ),
      severity: 'confirming',
      title: t(
        'pages.missioncontrol.runtimehandoff.await.confirmation',
        'Await confirmation',
      ),
    };
  }

  return {
    detail: input.actorId
      ? t(
          'pages.missioncontrol.runtimehandoff.evidence.is.linked.to.actor',
          'Evidence is linked to the current runtime actor.',
        )
      : t(
          'pages.missioncontrol.runtimehandoff.evidence.is.linked.to.the.current',
          'Evidence is linked to the current run.',
        ),
    evidence:
      input.detail ||
      t(
        'pages.missioncontrol.runtimehandoff.runtime.emitted.an.observable.event',
        'Runtime emitted an observable event.',
      ),
    nextStep: t(
      'pages.missioncontrol.runtimehandoff.keep.observing.unless.a.blocker',
      'Keep observing unless a blocker card appears.',
    ),
    severity: 'observe',
    title: t('pages.missioncontrol.runtimehandoff.event.evidence', 'Event evidence'),
  };
}

export function buildMissionActionFeedbackMessage(input: {
  accepted: boolean;
  commandId?: string;
  kind: MissionInterventionActionKind;
  runId?: string;
  signalName?: string;
}) {
  if (!input.accepted) {
    return t(
      'pages.missioncontrol.runtimehandoff.runtime.did.not.accept.the.intervention',
      'Runtime did not accept the intervention request. Keep the blocker open and retry after checking connection state.',
    );
  }

  const commandSuffix = input.commandId
    ? t(
        'pages.missioncontrol.runtimehandoff.command.is.pending.observation',
        ' Command observation is pending.',
      )
    : '';
  const runSuffix = input.runId
    ? t(
        'pages.missioncontrol.runtimehandoff.run.remains.the.evidence.source',
        ' The current run remains the evidence source.',
      )
    : '';

  switch (input.kind) {
    case 'signal':
      return t(
        'pages.missioncontrol.runtimehandoff.signal.was.accepted.wait',
        'Signal {signalName} was accepted. Wait for the next runtime snapshot before marking the gate resolved.{commandSuffix}{runSuffix}',
        {
          commandSuffix,
          runSuffix,
          signalName:
            input.signalName ||
            t('pages.missioncontrol.runtimehandoff.continue', 'continue'),
        },
      );
    case 'approve':
      return t(
        'pages.missioncontrol.runtimehandoff.approval.was.accepted.wait',
        'Approval was accepted. Wait for runtime to confirm advance, stop, or rollback before treating the decision as complete.{commandSuffix}{runSuffix}',
        { commandSuffix, runSuffix },
      );
    case 'reject':
      return t(
        'pages.missioncontrol.runtimehandoff.rejection.was.submitted.wait',
        'Rejection was submitted. Wait for runtime to confirm stop or rollback before closing the blocker.{commandSuffix}{runSuffix}',
        { commandSuffix, runSuffix },
      );
    default:
      return t(
        'pages.missioncontrol.runtimehandoff.resume.was.accepted.wait',
        'Resume was accepted. Wait for the blocked step to publish new evidence.{commandSuffix}{runSuffix}',
        { commandSuffix, runSuffix },
      );
  }
}
