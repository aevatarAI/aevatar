import type { ServiceCommandAcceptedReceipt } from '@/shared/models/services';
import {
  formatConsoleMessage,
  t,
  type ConsoleMessageDescriptor,
} from '@/shared/i18n/messages';

export type DeploymentReleaseHandoffAction =
  | 'deploy-candidate'
  | 'replace-serving-targets'
  | 'advance-rollout'
  | 'pause-rollout'
  | 'resume-rollout'
  | 'rollback-rollout'
  | 'deactivate-deployment';

export type DeploymentReleaseEvidenceView =
  | 'catalog'
  | 'serving'
  | 'rollout'
  | 'traffic';

export type DeploymentReleaseHandoff = {
  action: DeploymentReleaseHandoffAction;
  actionLabel: string;
  actionSummary: string;
  commandId: string;
  correlationId: string;
  createdAt: string;
  evidenceDescription: string;
  evidenceItems: string[];
  evidenceView: DeploymentReleaseEvidenceView;
  evidenceViewLabel: string;
  id: string;
  noticeMessage: string;
  noticeTone: 'success' | 'warning';
  pendingLabel: string;
  summaryItems: Array<{
    label: string;
    value: string;
  }>;
  title: string;
};

export type DeploymentReleaseHandoffInput = {
  action: DeploymentReleaseHandoffAction;
  activeRevisionId?: string;
  candidateRevisionId?: string;
  createdAt?: string;
  deploymentId?: string;
  endpointCount?: number;
  receipt?: Partial<ServiceCommandAcceptedReceipt>;
  rolloutId?: string;
  rolloutStageLabel?: string;
  serviceId: string;
  targetCount?: number;
};

type DeploymentReleaseHandoffCopy = Omit<
  Pick<
    DeploymentReleaseHandoff,
    | 'actionLabel'
    | 'actionSummary'
    | 'evidenceDescription'
    | 'evidenceItems'
    | 'evidenceView'
    | 'evidenceViewLabel'
    | 'noticeMessage'
    | 'noticeTone'
    | 'title'
  >,
  | 'actionLabel'
  | 'actionSummary'
  | 'evidenceDescription'
  | 'evidenceItems'
  | 'evidenceViewLabel'
  | 'noticeMessage'
  | 'title'
> & {
  actionLabel: ConsoleMessageDescriptor;
  actionSummary: ConsoleMessageDescriptor;
  evidenceDescription: ConsoleMessageDescriptor;
  evidenceItems: readonly ConsoleMessageDescriptor[];
  evidenceViewLabel: ConsoleMessageDescriptor;
  noticeMessage: ConsoleMessageDescriptor;
  title: ConsoleMessageDescriptor;
};

const actionCopy: Record<
  DeploymentReleaseHandoffAction,
  DeploymentReleaseHandoffCopy
> = {
  'advance-rollout': {
    actionLabel: {
      defaultMessage: 'Advance rollout',
      id: 'pages.deployments.releasehandoff.actions.advanceRollout.label',
    },
    actionSummary: {
      defaultMessage: 'Advance request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.advanceRollout.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the rollout advance command was accepted. Wait for stage and traffic evidence before treating it as complete.',
      id: 'pages.deployments.releasehandoff.actions.advanceRollout.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage: 'Rollout current stage or updatedAt changes',
        id: 'pages.deployments.releasehandoff.actions.advanceRollout.evidence.stage',
      },
      {
        defaultMessage: 'Serving targets match the current stage targets',
        id: 'pages.deployments.releasehandoff.actions.advanceRollout.evidence.serving',
      },
      {
        defaultMessage: 'Traffic allocation reflects the new stage weights',
        id: 'pages.deployments.releasehandoff.actions.advanceRollout.evidence.traffic',
      },
    ],
    evidenceView: 'rollout',
    evidenceViewLabel: {
      defaultMessage: 'Rollout',
      id: 'pages.deployments.releasehandoff.evidenceViews.rollout',
    },
    noticeMessage: {
      defaultMessage:
        'Rollout advance request was submitted. Waiting for stage evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.advanceRollout.notice',
    },
    noticeTone: 'success',
    title: {
      defaultMessage: 'Rollout advance submitted',
      id: 'pages.deployments.releasehandoff.actions.advanceRollout.title',
    },
  },
  'deactivate-deployment': {
    actionLabel: {
      defaultMessage: 'Deactivate deployment',
      id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.label',
    },
    actionSummary: {
      defaultMessage: 'Deactivate request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the deactivate command was accepted. It does not mean the deployment has disappeared from serving or catalog yet.',
      id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage:
          'The target deployment is no longer active in the deployment catalog',
        id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.evidence.catalog',
      },
      {
        defaultMessage:
          'Serving targets no longer route to the deactivated deployment',
        id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.evidence.serving',
      },
      {
        defaultMessage:
          'Traffic endpoints no longer allocate traffic to that revision/deployment',
        id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.evidence.traffic',
      },
    ],
    evidenceView: 'catalog',
    evidenceViewLabel: {
      defaultMessage: 'Deployment catalog',
      id: 'pages.deployments.releasehandoff.evidenceViews.catalog',
    },
    noticeMessage: {
      defaultMessage:
        'Deployment deactivate request was submitted. Waiting for catalog/serving evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.notice',
    },
    noticeTone: 'warning',
    title: {
      defaultMessage: 'Deployment deactivation submitted',
      id: 'pages.deployments.releasehandoff.actions.deactivateDeployment.title',
    },
  },
  'deploy-candidate': {
    actionLabel: {
      defaultMessage: 'Deploy candidate',
      id: 'pages.deployments.releasehandoff.actions.deployCandidate.label',
    },
    actionSummary: {
      defaultMessage: 'Candidate request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.deployCandidate.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the candidate deployment command was accepted. It does not mean serving has observed the candidate revision yet.',
      id: 'pages.deployments.releasehandoff.actions.deployCandidate.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage: 'Rollout shows an active stage or changed stage targets',
        id: 'pages.deployments.releasehandoff.actions.deployCandidate.evidence.rollout',
      },
      {
        defaultMessage: 'Serving targets include the candidate revision',
        id: 'pages.deployments.releasehandoff.actions.deployCandidate.evidence.serving',
      },
      {
        defaultMessage:
          'Traffic allocation points to the candidate revision before it is treated as effective',
        id: 'pages.deployments.releasehandoff.actions.deployCandidate.evidence.traffic',
      },
    ],
    evidenceView: 'rollout',
    evidenceViewLabel: {
      defaultMessage: 'Rollout',
      id: 'pages.deployments.releasehandoff.evidenceViews.rollout',
    },
    noticeMessage: {
      defaultMessage:
        'Candidate version was submitted. Waiting for rollout/serving evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.deployCandidate.notice',
    },
    noticeTone: 'success',
    title: {
      defaultMessage: 'Candidate deployment submitted',
      id: 'pages.deployments.releasehandoff.actions.deployCandidate.title',
    },
  },
  'pause-rollout': {
    actionLabel: {
      defaultMessage: 'Pause rollout',
      id: 'pages.deployments.releasehandoff.actions.pauseRollout.label',
    },
    actionSummary: {
      defaultMessage: 'Pause request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.pauseRollout.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the pause command was accepted. Wait until rollout status shows paused before stopping follow-up operations.',
      id: 'pages.deployments.releasehandoff.actions.pauseRollout.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage: 'Rollout status refreshes to paused or an equivalent state',
        id: 'pages.deployments.releasehandoff.actions.pauseRollout.evidence.status',
      },
      {
        defaultMessage:
          'Serving targets remain at the last stable allocation before pause',
        id: 'pages.deployments.releasehandoff.actions.pauseRollout.evidence.serving',
      },
      {
        defaultMessage: 'Traffic has not advanced to the next stage',
        id: 'pages.deployments.releasehandoff.actions.pauseRollout.evidence.traffic',
      },
    ],
    evidenceView: 'rollout',
    evidenceViewLabel: {
      defaultMessage: 'Rollout',
      id: 'pages.deployments.releasehandoff.evidenceViews.rollout',
    },
    noticeMessage: {
      defaultMessage:
        'Rollout pause request was submitted. Waiting for status evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.pauseRollout.notice',
    },
    noticeTone: 'success',
    title: {
      defaultMessage: 'Rollout pause submitted',
      id: 'pages.deployments.releasehandoff.actions.pauseRollout.title',
    },
  },
  'replace-serving-targets': {
    actionLabel: {
      defaultMessage: 'Apply weights',
      id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.label',
    },
    actionSummary: {
      defaultMessage:
        'Serving target replacement request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the weight replacement command was accepted. Wait for serving generation and traffic split to refresh.',
      id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage: 'Serving generation or updatedAt refreshes',
        id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.evidence.generation',
      },
      {
        defaultMessage: 'Serving targets show the new revision/weight allocation',
        id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.evidence.serving',
      },
      {
        defaultMessage:
          'Traffic endpoint split aligns with the new serving targets',
        id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.evidence.traffic',
      },
    ],
    evidenceView: 'serving',
    evidenceViewLabel: {
      defaultMessage: 'Serving',
      id: 'pages.deployments.releasehandoff.evidenceViews.serving',
    },
    noticeMessage: {
      defaultMessage:
        'Serving targets were submitted. Waiting for serving/traffic evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.notice',
    },
    noticeTone: 'success',
    title: {
      defaultMessage: 'Serving targets replacement submitted',
      id: 'pages.deployments.releasehandoff.actions.replaceServingTargets.title',
    },
  },
  'resume-rollout': {
    actionLabel: {
      defaultMessage: 'Resume rollout',
      id: 'pages.deployments.releasehandoff.actions.resumeRollout.label',
    },
    actionSummary: {
      defaultMessage: 'Resume request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.resumeRollout.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the resume command was accepted. Wait until rollout status re-enters active advancement.',
      id: 'pages.deployments.releasehandoff.actions.resumeRollout.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage: 'Rollout status no longer remains paused',
        id: 'pages.deployments.releasehandoff.actions.resumeRollout.evidence.status',
      },
      {
        defaultMessage: 'Current stage or updatedAt continues to refresh',
        id: 'pages.deployments.releasehandoff.actions.resumeRollout.evidence.stage',
      },
      {
        defaultMessage:
          'Traffic allocation continues advancing by the stage plan',
        id: 'pages.deployments.releasehandoff.actions.resumeRollout.evidence.traffic',
      },
    ],
    evidenceView: 'rollout',
    evidenceViewLabel: {
      defaultMessage: 'Rollout',
      id: 'pages.deployments.releasehandoff.evidenceViews.rollout',
    },
    noticeMessage: {
      defaultMessage:
        'Rollout resume request was submitted. Waiting for status evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.resumeRollout.notice',
    },
    noticeTone: 'success',
    title: {
      defaultMessage: 'Rollout resume submitted',
      id: 'pages.deployments.releasehandoff.actions.resumeRollout.title',
    },
  },
  'rollback-rollout': {
    actionLabel: {
      defaultMessage: 'Rollback rollout',
      id: 'pages.deployments.releasehandoff.actions.rollbackRollout.label',
    },
    actionSummary: {
      defaultMessage: 'Rollback request entered the release control plane',
      id: 'pages.deployments.releasehandoff.actions.rollbackRollout.summary',
    },
    evidenceDescription: {
      defaultMessage:
        'This only means the rollback command was accepted. It does not mean serving has returned to baseline yet.',
      id: 'pages.deployments.releasehandoff.actions.rollbackRollout.evidenceDescription',
    },
    evidenceItems: [
      {
        defaultMessage:
          'Rollout status shows rollback or returns to the baseline stage',
        id: 'pages.deployments.releasehandoff.actions.rollbackRollout.evidence.status',
      },
      {
        defaultMessage: 'Serving targets align with baseline targets',
        id: 'pages.deployments.releasehandoff.actions.rollbackRollout.evidence.serving',
      },
      {
        defaultMessage:
          'Traffic allocation no longer points to the rolled-back candidate revision',
        id: 'pages.deployments.releasehandoff.actions.rollbackRollout.evidence.traffic',
      },
    ],
    evidenceView: 'rollout',
    evidenceViewLabel: {
      defaultMessage: 'Rollout',
      id: 'pages.deployments.releasehandoff.evidenceViews.rollout',
    },
    noticeMessage: {
      defaultMessage:
        'Rollout rollback request was submitted. Waiting for baseline evidence to refresh.',
      id: 'pages.deployments.releasehandoff.actions.rollbackRollout.notice',
    },
    noticeTone: 'warning',
    title: {
      defaultMessage: 'Rollout rollback submitted',
      id: 'pages.deployments.releasehandoff.actions.rollbackRollout.title',
    },
  },
};

export function buildDeploymentReleaseHandoff(
  input: DeploymentReleaseHandoffInput,
): DeploymentReleaseHandoff {
  const copy = actionCopy[input.action];
  const commandId = input.receipt?.commandId?.trim() || 'pending-command';
  const correlationId =
    input.receipt?.correlationId?.trim() || 'pending-correlation';
  const createdAt = input.createdAt || new Date().toISOString();
  const summaryItems = [
    {
      label: 'Service',
      value: input.serviceId || t("pages.deployments.releasehandoff.not.selected", "Not selected"),
    },
    {
      label: 'Command',
      value: commandId,
    },
    {
      label: 'Correlation',
      value: correlationId,
    },
    {
      label: t("pages.deployments.releasehandoff.currently.serving", "currently serving"),
      value: input.activeRevisionId || t("pages.deployments.releasehandoff.none.yet", "None yet"),
    },
  ];

  if (input.candidateRevisionId) {
    summaryItems.push({
      label: t("pages.deployments.releasehandoff.candidate.revision", "Candidate revision"),
      value: input.candidateRevisionId,
    });
  }

  if (input.deploymentId) {
    summaryItems.push({
      label: 'Deployment',
      value: input.deploymentId,
    });
  }

  if (input.rolloutId) {
    summaryItems.push({
      label: 'Rollout',
      value: input.rolloutId,
    });
  }

  if (input.rolloutStageLabel) {
    summaryItems.push({
      label: t("pages.deployments.releasehandoff.current.stage", "current stage"),
      value: input.rolloutStageLabel,
    });
  }

  if (typeof input.targetCount === 'number') {
    summaryItems.push({
      label: t("pages.deployments.releasehandoff.serving.targets", "Serving targets"),
      value: String(input.targetCount),
    });
  }

  if (typeof input.endpointCount === 'number') {
    summaryItems.push({
      label: t("pages.deployments.releasehandoff.traffic.endpoints", "Traffic endpoints"),
      value: String(input.endpointCount),
    });
  }

  return {
    action: input.action,
    actionLabel: formatConsoleMessage(copy.actionLabel),
    actionSummary: formatConsoleMessage(copy.actionSummary),
    commandId,
    correlationId,
    createdAt,
    evidenceDescription: formatConsoleMessage(copy.evidenceDescription),
    evidenceItems: copy.evidenceItems.map((item) => formatConsoleMessage(item)),
    evidenceView: copy.evidenceView,
    evidenceViewLabel: formatConsoleMessage(copy.evidenceViewLabel),
    id: `${input.action}:${commandId}:${correlationId}`,
    noticeMessage: formatConsoleMessage(copy.noticeMessage),
    noticeTone: copy.noticeTone,
    pendingLabel: t("pages.deployments.releasehandoff.submitted.does.not.mean", "Submitted, does not mean completed"),
    summaryItems,
    title: formatConsoleMessage(copy.title),
  };
}
