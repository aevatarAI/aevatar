import type {
  ServiceDeploymentSnapshot,
  ServiceRolloutSnapshot,
  ServiceServingSetSnapshot,
  ServiceTrafficViewSnapshot,
} from '@/shared/models/services';
import type { DeploymentReleaseHandoff } from './releaseHandoff';
import { t } from "@/shared/i18n/messages";

export type DeploymentReleaseEvidenceStatus = 'observed' | 'pending' | 'review';

export type DeploymentReleaseEvidenceCheck = {
  detail: string;
  key: string;
  label: string;
  status: DeploymentReleaseEvidenceStatus;
};

export type DeploymentReleaseEvidenceSnapshot = {
  checks: DeploymentReleaseEvidenceCheck[];
  observedCount: number;
  summary: string;
};

export type DeploymentReleaseEvidenceInput = {
  deployments: readonly ServiceDeploymentSnapshot[];
  handoff: DeploymentReleaseHandoff;
  rollout?: ServiceRolloutSnapshot | null;
  serving?: ServiceServingSetSnapshot | null;
  traffic?: ServiceTrafficViewSnapshot | null;
};

function normalizeStatus(value: string | null | undefined): string {
  return (value ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
}

function readSummaryValue(
  handoff: DeploymentReleaseHandoff,
  label: string,
): string {
  return (
    handoff.summaryItems.find((item) => item.label === label)?.value.trim() ??
    ''
  );
}

function buildCheck(
  key: string,
  label: string,
  status: DeploymentReleaseEvidenceStatus,
  detail: string,
): DeploymentReleaseEvidenceCheck {
  return {
    detail,
    key,
    label,
    status,
  };
}

function hasServingRevision(
  serving: ServiceServingSetSnapshot | null | undefined,
  revisionId: string,
): boolean {
  return Boolean(
    revisionId &&
      serving?.targets.some((target) => target.revisionId === revisionId),
  );
}

function hasTrafficRevision(
  traffic: ServiceTrafficViewSnapshot | null | undefined,
  revisionId: string,
): boolean {
  return Boolean(
    revisionId &&
      traffic?.endpoints.some((endpoint) =>
        endpoint.targets.some((target) => target.revisionId === revisionId),
      ),
  );
}

function occurredAfterHandoff(
  observedAt: string | null | undefined,
  handoff: DeploymentReleaseHandoff,
): boolean {
  if (!observedAt) {
    return false;
  }

  const observedTime = Date.parse(observedAt);
  const submittedTime = Date.parse(handoff.createdAt);
  if (Number.isNaN(observedTime) || Number.isNaN(submittedTime)) {
    return false;
  }

  return observedTime >= submittedTime;
}

function buildFreshStatus(
  observed: boolean,
  observedAt: string | null | undefined,
  handoff: DeploymentReleaseHandoff,
): DeploymentReleaseEvidenceStatus {
  if (!observed) {
    return 'pending';
  }

  return occurredAfterHandoff(observedAt, handoff) ? 'observed' : 'review';
}

function buildFreshDetail(
  observed: boolean,
  observedAt: string | null | undefined,
  freshDetail: string,
  staleDetail: string,
  pendingDetail: string,
  handoff: DeploymentReleaseHandoff,
): string {
  if (!observed) {
    return pendingDetail;
  }

  if (occurredAfterHandoff(observedAt, handoff)) {
    return freshDetail;
  }

  return staleDetail;
}

function deploymentIsInactive(
  deployments: readonly ServiceDeploymentSnapshot[],
  deploymentId: string,
): boolean {
  const deployment = deployments.find(
    (item) => item.deploymentId === deploymentId,
  );
  if (!deployment) {
    return false;
  }

  const status = deployment.status.trim().toLowerCase();
  return status !== 'active';
}

function findDeployment(
  deployments: readonly ServiceDeploymentSnapshot[],
  deploymentId: string,
): ServiceDeploymentSnapshot | undefined {
  return deployments.find((item) => item.deploymentId === deploymentId);
}

function servingExcludesDeployment(
  serving: ServiceServingSetSnapshot | null | undefined,
  deploymentId: string,
): boolean {
  return Boolean(
    deploymentId &&
      serving &&
      !serving.targets.some((target) => target.deploymentId === deploymentId),
  );
}

function trafficExcludesDeployment(
  traffic: ServiceTrafficViewSnapshot | null | undefined,
  deploymentId: string,
): boolean {
  return Boolean(
    deploymentId &&
      traffic &&
      !traffic.endpoints.some((endpoint) =>
        endpoint.targets.some((target) => target.deploymentId === deploymentId),
      ),
  );
}

export function buildDeploymentReleaseEvidenceSnapshot({
  deployments,
  handoff,
  rollout,
  serving,
  traffic,
}: DeploymentReleaseEvidenceInput): DeploymentReleaseEvidenceSnapshot {
  const candidateRevisionId = readSummaryValue(handoff, t("pages.deployments.releaseevidence.candidate.revision", "Candidate revision"));
  const deploymentId = readSummaryValue(handoff, 'Deployment');
  const checks: DeploymentReleaseEvidenceCheck[] = [];

  if (handoff.action === 'deploy-candidate') {
    const rolloutMatchesHandoff =
      Boolean(rollout?.rolloutId) &&
      (!readSummaryValue(handoff, 'Rollout') ||
        rollout?.rolloutId === readSummaryValue(handoff, 'Rollout'));
    const rolloutStatus = buildFreshStatus(
      rolloutMatchesHandoff,
      rollout?.updatedAt,
      handoff,
    );
    const servingHasCandidate = hasServingRevision(
      serving,
      candidateRevisionId,
    );
    const trafficHasCandidate = hasTrafficRevision(
      traffic,
      candidateRevisionId,
    );

    checks.push(
      buildCheck(
        'rollout-active',
        'Rollout evidence',
        rolloutStatus,
        buildFreshDetail(
	          rolloutMatchesHandoff,
	          rollout?.updatedAt,
	          t("pages.deployments.releaseevidence.active.rollout.has.been", "Active rollout has been refreshed after this commit"),
	          rollout?.rolloutId
	            ? t("pages.deployments.releaseevidence.activity.rollout.is.visible", "Activity rollout is visible, but updatedAt is earlier than this submission, please wait for refresh")
	            : t("pages.deployments.releaseevidence.wait.for.this.rollout", "Wait for this rollout to appear or refresh"),
          t("pages.deployments.releaseevidence.wait.for.this.rollout.2", "Wait for this rollout to appear or refresh"),
          handoff,
        ),
      ),
      buildCheck(
        'serving-candidate',
        'Serving evidence',
        buildFreshStatus(servingHasCandidate, serving?.updatedAt, handoff),
        buildFreshDetail(
	          servingHasCandidate,
	          serving?.updatedAt,
	          t("pages.deployments.releaseevidence.serving.targets.already.contain", "Serving targets already contain the candidate revision after this commit"),
	          t("pages.deployments.releaseevidence.serving.targets.already.contain.2", "Serving targets already contain the candidate revision, but updatedAt is earlier than this submission, please wait for readmodel to refresh"),
	          t("pages.deployments.releaseevidence.wait.for.serving.targets", "Wait for candidate revision to appear in serving targets"),
          handoff,
        ),
      ),
      buildCheck(
        'traffic-candidate',
        'Traffic evidence',
        buildFreshStatus(trafficHasCandidate, traffic?.updatedAt, handoff),
        buildFreshDetail(
	          trafficHasCandidate,
	          traffic?.updatedAt,
	          t("pages.deployments.releaseevidence.traffic.split.already.contains", "Traffic split already contains the candidate revision after this commit"),
	          t("pages.deployments.releaseevidence.traffic.split.already.contains.2", "Traffic split already contains the candidate revision, but updatedAt is earlier than this submission, please wait for readmodel to refresh"),
          t("pages.deployments.releaseevidence.wait.for.traffic.split", "Wait for traffic split to point to candidate revision"),
          handoff,
        ),
      ),
    );
  } else if (handoff.action === 'replace-serving-targets') {
    checks.push(
      buildCheck(
        'serving-generation',
        'Serving generation',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? t("pages.deployments.releaseevidence.serving.updatedat.is.later", "serving updatedAt {value1} is later than this submission, please confirm whether the weights match", { value1: serving?.updatedAt })
          : t("pages.deployments.releaseevidence.wait.for.serving.readmodel", "Wait for serving readmodel to refresh after this submission"),
      ),
      buildCheck(
        'traffic-generation',
        'Traffic split',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? t("pages.deployments.releaseevidence.traffic.updatedat.is.later", "Traffic updatedAt {value1} is later than this submission, please check whether the weights match.", { value1: traffic?.updatedAt })
          : t("pages.deployments.releaseevidence.wait.for.the.traffic", "Wait for the traffic readmodel to be refreshed after this submission"),
      ),
    );
  } else if (handoff.action === 'deactivate-deployment') {
    const deployment = findDeployment(deployments, deploymentId);
    const inactive = deploymentIsInactive(deployments, deploymentId);
    const catalogStatus = buildFreshStatus(
      inactive,
      deployment?.updatedAt,
      handoff,
    );
    const servingExcludesTarget = servingExcludesDeployment(
      serving,
      deploymentId,
    );
    const trafficExcludesTarget = trafficExcludesDeployment(
      traffic,
      deploymentId,
    );

    checks.push(
      buildCheck(
        'deployment-inactive',
        'Deployment catalog',
        catalogStatus,
        buildFreshDetail(
	          inactive,
	          deployment?.updatedAt,
	          t("pages.deployments.releaseevidence.has.left.active.after", "Target deployment has left active after this commit"),
	          t("pages.deployments.releaseevidence.is.no.longer.displayed", "Target deployment is no longer displayed as active, but updatedAt is earlier than this submission, please wait for the catalog to refresh"),
	          deployment
	            ? t("pages.deployments.releaseevidence.wait.for.state.to", "Wait for target deployment state to leave active")
	            : t("pages.deployments.releaseevidence.wait.for.to.appear", "Wait for target deployment to appear in catalog and show inactive status"),
          handoff,
        ),
      ),
      buildCheck(
        'serving-excludes-deployment',
        'Serving targets',
        buildFreshStatus(servingExcludesTarget, serving?.updatedAt, handoff),
        buildFreshDetail(
          servingExcludesTarget,
          serving?.updatedAt,
          t("pages.deployments.releaseevidence.serving.targets.no.longer", "serving targets no longer contain the deployment after this submission"),
          t("pages.deployments.releaseevidence.serving.targets.currently.do", "serving targets currently do not contain this deployment, but updatedAt is earlier than this submission, please wait for readmodel to refresh"),
          t("pages.deployments.releaseevidence.wait.for.serving.targets.2", "Wait for serving targets to remove the deployment"),
          handoff,
        ),
      ),
      buildCheck(
        'traffic-excludes-deployment',
        'Traffic split',
        buildFreshStatus(trafficExcludesTarget, traffic?.updatedAt, handoff),
        buildFreshDetail(
          trafficExcludesTarget,
          traffic?.updatedAt,
          t("pages.deployments.releaseevidence.traffic.split.no.longer", "Traffic split no longer contains the deployment after this submission"),
          t("pages.deployments.releaseevidence.traffic.split.currently.does", "Traffic split currently does not contain this deployment, but updatedAt is earlier than this submission, please wait for readmodel to refresh"),
          t("pages.deployments.releaseevidence.wait.for.traffic.split.2", "Wait for traffic split to remove the deployment"),
          handoff,
        ),
      ),
    );
  } else {
    const rolloutStatus = rollout?.status ?? '';
    const actionStatusNeedle: Partial<Record<string, string>> = {
      'advance-rollout': 'inprogress',
      'pause-rollout': 'paused',
      'resume-rollout': 'inprogress',
      'rollback-rollout': 'rolledback',
    };
    const needle = actionStatusNeedle[handoff.action] ?? '';
    const rolloutHasExpectedStatus =
      needle && normalizeStatus(rolloutStatus).includes(needle);

    checks.push(
      buildCheck(
        'rollout-status',
        'Rollout status',
        buildFreshStatus(
          Boolean(rolloutHasExpectedStatus),
          rollout?.updatedAt,
          handoff,
        ),
        rolloutStatus
          ? buildFreshDetail(
              Boolean(rolloutHasExpectedStatus),
              rollout?.updatedAt,
              t("pages.deployments.releaseevidence.the.current.rollout.status", "The current rollout status has been refreshed to {value1} after this commit", { value1: rolloutStatus }),
              t("pages.deployments.releaseevidence.the.current.rollout.status.2", "The current rollout status is {value1}, but updatedAt is earlier than this submission, please wait for the refresh", { value1: rolloutStatus }),
              t("pages.deployments.releaseevidence.the.current.rollout.status.3", "The current rollout status is {value1}, waiting for the status matching this command", { value1: rolloutStatus }),
              handoff,
            )
          : t("pages.deployments.releaseevidence.wait.for.rollout.status", "Wait for rollout status to refresh"),
      ),
      buildCheck(
        'serving-targets',
        'Serving targets',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? t("pages.deployments.releaseevidence.currently.serving.targets.are", "Currently {value1} serving targets are visible, please check whether they match this command.", { value1: serving?.targets.length ?? 0 })
          : t("pages.deployments.releaseevidence.wait.for.serving.targets.3", "Wait for serving targets to be refreshed after this submission"),
      ),
      buildCheck(
        'traffic-split',
        'Traffic split',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? t("pages.deployments.releaseevidence.traffic.endpoints.are.currently", "{value1} traffic endpoints are currently visible, please check whether they match this command.", { value1: traffic?.endpoints.length ?? 0 })
          : t("pages.deployments.releaseevidence.wait.for.the.traffic.2", "Wait for the traffic split to be refreshed after this submission"),
      ),
    );
  }

  const observedCount = checks.filter(
    (check) => check.status === 'observed',
  ).length;
  const pendingCount = checks.filter(
    (check) => check.status === 'pending',
  ).length;
  const reviewCount = checks.filter(
    (check) => check.status === 'review',
  ).length;

  return {
    checks,
    observedCount,
    summary:
      pendingCount > 0
        ? t("pages.deployments.releaseevidence.evidence.remains.to.be", "{value1} evidence remains to be seen, avoid treating submitted as completed.", { value1: pendingCount })
        : reviewCount > 0
          ? t("pages.deployments.releaseevidence.evidence.needs.to.be", "{value1} evidence needs to be manually checked to avoid treating the old readmodel as completed this time.", { value1: reviewCount })
          : t("pages.deployments.releaseevidence.all.key.evidence.has", "All key evidence has been observed following this submission."),
  };
}
