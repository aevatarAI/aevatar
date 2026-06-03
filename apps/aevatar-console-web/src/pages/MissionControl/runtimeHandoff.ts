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
import { t } from "@/shared/i18n/messages";

function observationEvidence(status: MissionObservationStatus, freshnessLabel: string) {
  switch (status) {
    case 'streaming':
      return `Live runtime evidence, fresh ${freshnessLabel}.`;
    case 'snapshot_available':
      return `Snapshot evidence is available, fresh ${freshnessLabel}.`;
    case 'projection_settled':
      return `Committed terminal evidence, fresh ${freshnessLabel}.`;
    case 'delayed':
      return `Last known evidence is delayed, fresh ${freshnessLabel}.`;
    default:
      return 'No runtime evidence is attached to this node yet.';
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
      ? 'Open the intervention panel, review recent evidence, then approve or reject.'
      : 'Open the intervention panel, provide the requested signal or context, then wait for runtime confirmation.';
  }

  switch (severity) {
    case 'blocked':
      return 'Keep this as evidence and wait for a newer runtime snapshot before acting.';
    case 'confirming':
      return 'Observe the next runtime event before treating this step as complete.';
    default:
      return 'Use this node as read-only evidence for the current run.';
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
    ? 'Operator handoff'
    : severity === 'blocked'
      ? 'Evidence blocked'
      : severity === 'confirming'
        ? 'Runtime confirmation'
        : 'Observation evidence';

  return {
    detail: t("pages.missioncontrol.runtimehandoff.is.with.evidence", "{value1} is {value2} with {value3} evidence.", { value1: input.label, value2: formatMissionLabel(input.status), value3: formatMissionLabel(
      input.observationStatus,
    ) }),
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
      detail: t("pages.missioncontrol.runtimehandoff.at.step", "{value1} at step {value2}.", { value1: input.intervention.title, value2: input.intervention.stepId }),
      evidence: input.detail || 'Runtime published a blocking event.',
      nextStep:
        input.intervention.kind === 'human_approval'
          ? 'Open the intervention panel and decide with the latest event dock evidence.'
          : 'Open the intervention panel and submit the requested context or signal.',
      severity: 'action',
      title: t("pages.missioncontrol.runtimehandoff.action.handoff", "Action handoff"),
    };
  }

  if (terminal) {
    return {
      detail: t("pages.missioncontrol.runtimehandoff.this.event.reflects.terminal.or.settled", "This event reflects a terminal or settled runtime fact."),
      evidence: input.detail || 'Runtime emitted terminal evidence.',
      nextStep: 'Use this event as committed evidence; no operator action is implied.',
      severity: 'observe',
      title: t("pages.missioncontrol.runtimehandoff.settled.evidence", "Settled evidence"),
    };
  }

  if (input.type === 'step_requested' || input.type === 'workflow_run_execution_started') {
    return {
      detail: t("pages.missioncontrol.runtimehandoff.runtime.accepted.work.and.queued.the", "Runtime accepted work and queued the next step."),
      evidence: input.detail || 'Runtime emitted an execution start event.',
      nextStep: 'Wait for the matching completion, suspension, or signal event.',
      severity: 'confirming',
      title: t("pages.missioncontrol.runtimehandoff.await.confirmation", "Await confirmation"),
    };
  }

  return {
    detail: input.actorId
      ? `Evidence is linked to actor ${input.actorId}.`
      : 'Evidence is linked to the current run.',
    evidence: input.detail || 'Runtime emitted an observable event.',
    nextStep: 'Keep observing unless a blocker card appears.',
    severity: 'observe',
    title: t("pages.missioncontrol.runtimehandoff.event.evidence", "Event evidence"),
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
    return 'Runtime did not accept the intervention request. Keep the blocker open and retry after checking connection state.';
  }

  const commandSuffix = input.commandId ? ` Command ${input.commandId} is pending observation.` : '';
  const runSuffix = input.runId ? ` Run ${input.runId} remains the evidence source.` : '';

  switch (input.kind) {
    case 'signal':
      return `Signal ${input.signalName || 'continue'} was accepted. Wait for the next runtime snapshot before marking the gate resolved.${commandSuffix}${runSuffix}`;
    case 'approve':
      return `Approval was accepted. Wait for runtime to confirm advance, stop, or rollback before treating the decision as complete.${commandSuffix}${runSuffix}`;
    case 'reject':
      return `Rejection was submitted. Wait for runtime to confirm stop or rollback before closing the blocker.${commandSuffix}${runSuffix}`;
    default:
      return `Resume was accepted. Wait for the blocked step to publish new evidence.${commandSuffix}${runSuffix}`;
  }
}
