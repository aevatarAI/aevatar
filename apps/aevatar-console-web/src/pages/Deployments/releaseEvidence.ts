import type {
  ServiceDeploymentSnapshot,
  ServiceRolloutSnapshot,
  ServiceServingSetSnapshot,
  ServiceTrafficViewSnapshot,
} from '@/shared/models/services';
import type { DeploymentReleaseHandoff } from './releaseHandoff';

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
  const candidateRevisionId = readSummaryValue(handoff, '候选 revision');
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
          `活动 rollout ${rollout?.rolloutId} 已在本次提交后刷新`,
          rollout?.rolloutId
            ? `活动 rollout ${rollout.rolloutId} 可见，但 updatedAt 早于本次提交，请等待刷新`
            : '等待本次 rollout 出现或刷新',
          '等待本次 rollout 出现或刷新',
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
          `Serving targets 已在本次提交后包含 ${candidateRevisionId}`,
          `Serving targets 已包含 ${candidateRevisionId}，但 updatedAt 早于本次提交，请等待 readmodel 刷新`,
          `等待 serving targets 出现 ${candidateRevisionId || '候选 revision'}`,
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
          `Traffic split 已在本次提交后包含 ${candidateRevisionId}`,
          `Traffic split 已包含 ${candidateRevisionId}，但 updatedAt 早于本次提交，请等待 readmodel 刷新`,
          '等待 traffic split 指向候选 revision',
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
          ? `Serving updatedAt ${serving?.updatedAt} 晚于本次提交，请确认权重是否匹配`
          : '等待本次提交后的 serving readmodel 刷新',
      ),
      buildCheck(
        'traffic-generation',
        'Traffic split',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? `Traffic updatedAt ${traffic?.updatedAt} 晚于本次提交，请核对权重是否匹配`
          : '等待本次提交后的 traffic readmodel 刷新',
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
          `${deploymentId || '目标 deployment'} 已在本次提交后离开 active`,
          `${deploymentId || '目标 deployment'} 已不再显示为 active，但 updatedAt 早于本次提交，请等待 catalog 刷新`,
          deployment
            ? `等待 ${deploymentId || '目标 deployment'} 状态离开 active`
            : `等待 ${deploymentId || '目标 deployment'} 出现在 catalog 并显示非 active 状态`,
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
          'Serving targets 已在本次提交后不再包含该 deployment',
          'Serving targets 当前不包含该 deployment，但 updatedAt 早于本次提交，请等待 readmodel 刷新',
          '等待 serving targets 移除该 deployment',
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
          'Traffic split 已在本次提交后不再包含该 deployment',
          'Traffic split 当前不包含该 deployment，但 updatedAt 早于本次提交，请等待 readmodel 刷新',
          '等待 traffic split 移除该 deployment',
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
              `当前 rollout 状态已在本次提交后刷新为 ${rolloutStatus}`,
              `当前 rollout 状态为 ${rolloutStatus}，但 updatedAt 早于本次提交，请等待刷新`,
              `当前 rollout 状态为 ${rolloutStatus}，等待匹配本次命令的状态`,
              handoff,
            )
          : '等待 rollout 状态刷新',
      ),
      buildCheck(
        'serving-targets',
        'Serving targets',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(serving?.updatedAt, handoff)
          ? `当前可见 ${serving?.targets.length ?? 0} 个 serving targets，请核对是否匹配本次命令`
          : '等待本次提交后的 serving targets 刷新',
      ),
      buildCheck(
        'traffic-split',
        'Traffic split',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? 'review'
          : 'pending',
        occurredAfterHandoff(traffic?.updatedAt, handoff)
          ? `当前可见 ${traffic?.endpoints.length ?? 0} 个 traffic endpoints，请核对是否匹配本次命令`
          : '等待本次提交后的 traffic split 刷新',
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
        ? `${pendingCount} 项证据仍待观察，避免把 submitted 当作 completed。`
        : reviewCount > 0
          ? `${reviewCount} 项证据需要人工核对，避免把旧 readmodel 当作本次完成。`
          : '所有关键证据都已在本次提交后观察到。',
  };
}
