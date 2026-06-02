import type {
  ServiceDeploymentSnapshot,
  ServiceRolloutSnapshot,
  ServiceServingSetSnapshot,
  ServiceTrafficViewSnapshot,
} from "@/shared/models/services";
import type { DeploymentReleaseHandoff } from "./releaseHandoff";

export type DeploymentReleaseEvidenceStatus =
  | "observed"
  | "pending"
  | "review";

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

function includesIgnoreCase(value: string | null | undefined, pattern: string): boolean {
  return (value ?? "").toLowerCase().includes(pattern.toLowerCase());
}

function readSummaryValue(
  handoff: DeploymentReleaseHandoff,
  label: string,
): string {
  return (
    handoff.summaryItems.find((item) => item.label === label)?.value.trim() ?? ""
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

function deploymentIsInactive(
  deployments: readonly ServiceDeploymentSnapshot[],
  deploymentId: string,
): boolean {
  const deployment = deployments.find((item) => item.deploymentId === deploymentId);
  if (!deployment) {
    return true;
  }

  const status = deployment.status.trim().toLowerCase();
  return status !== "active";
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
  const candidateRevisionId = readSummaryValue(handoff, "候选 revision");
  const deploymentId = readSummaryValue(handoff, "Deployment");
  const checks: DeploymentReleaseEvidenceCheck[] = [];

  if (handoff.action === "deploy-candidate") {
    checks.push(
      buildCheck(
        "rollout-active",
        "Rollout evidence",
        rollout?.rolloutId ? "observed" : "pending",
        rollout?.rolloutId
          ? `活动 rollout ${rollout.rolloutId} 可见`
          : "等待 rollout 出现或刷新",
      ),
      buildCheck(
        "serving-candidate",
        "Serving evidence",
        hasServingRevision(serving, candidateRevisionId) ? "observed" : "pending",
        hasServingRevision(serving, candidateRevisionId)
          ? `Serving targets 已包含 ${candidateRevisionId}`
          : `等待 serving targets 出现 ${candidateRevisionId || "候选 revision"}`,
      ),
      buildCheck(
        "traffic-candidate",
        "Traffic evidence",
        hasTrafficRevision(traffic, candidateRevisionId) ? "observed" : "pending",
        hasTrafficRevision(traffic, candidateRevisionId)
          ? `Traffic split 已包含 ${candidateRevisionId}`
          : "等待 traffic split 指向候选 revision",
      ),
    );
  } else if (handoff.action === "replace-serving-targets") {
    checks.push(
      buildCheck(
        "serving-generation",
        "Serving generation",
        serving?.updatedAt ? "review" : "pending",
        serving?.updatedAt
          ? `Serving updatedAt ${serving.updatedAt}，请确认是否晚于 command 提交`
          : "等待 serving readmodel 刷新",
      ),
      buildCheck(
        "traffic-generation",
        "Traffic split",
        traffic?.updatedAt ? "review" : "pending",
        traffic?.updatedAt
          ? `Traffic updatedAt ${traffic.updatedAt}，请核对权重是否匹配`
          : "等待 traffic readmodel 刷新",
      ),
    );
  } else if (handoff.action === "deactivate-deployment") {
    checks.push(
      buildCheck(
        "deployment-inactive",
        "Deployment catalog",
        deploymentIsInactive(deployments, deploymentId) ? "observed" : "pending",
        deploymentIsInactive(deployments, deploymentId)
          ? `${deploymentId || "目标 deployment"} 已不再显示为 active`
          : `等待 ${deploymentId || "目标 deployment"} 状态离开 active`,
      ),
      buildCheck(
        "serving-excludes-deployment",
        "Serving targets",
        servingExcludesDeployment(serving, deploymentId) ? "observed" : "pending",
        servingExcludesDeployment(serving, deploymentId)
          ? "Serving targets 已不再包含该 deployment"
          : "等待 serving targets 移除该 deployment",
      ),
      buildCheck(
        "traffic-excludes-deployment",
        "Traffic split",
        trafficExcludesDeployment(traffic, deploymentId) ? "observed" : "pending",
        trafficExcludesDeployment(traffic, deploymentId)
          ? "Traffic split 已不再包含该 deployment"
          : "等待 traffic split 移除该 deployment",
      ),
    );
  } else {
    const rolloutStatus = rollout?.status ?? "";
    const actionStatusNeedle: Record<string, string> = {
      "advance-rollout": "canary",
      "pause-rollout": "pause",
      "resume-rollout": "canary",
      "rollback-rollout": "rollback",
    };
    const needle = actionStatusNeedle[handoff.action] ?? "";

    checks.push(
      buildCheck(
        "rollout-status",
        "Rollout status",
        needle && includesIgnoreCase(rolloutStatus, needle) ? "observed" : "review",
        rolloutStatus
          ? `当前 rollout 状态为 ${rolloutStatus}`
          : "等待 rollout 状态刷新",
      ),
      buildCheck(
        "serving-targets",
        "Serving targets",
        serving?.targets.length ? "review" : "pending",
        serving?.targets.length
          ? `当前可见 ${serving.targets.length} 个 serving targets`
          : "等待 serving targets 刷新",
      ),
      buildCheck(
        "traffic-split",
        "Traffic split",
        traffic?.endpoints.length ? "review" : "pending",
        traffic?.endpoints.length
          ? `当前可见 ${traffic.endpoints.length} 个 traffic endpoints`
          : "等待 traffic split 刷新",
      ),
    );
  }

  const observedCount = checks.filter((check) => check.status === "observed").length;
  const pendingCount = checks.filter((check) => check.status === "pending").length;

  return {
    checks,
    observedCount,
    summary:
      pendingCount === 0
        ? "所有关键证据都已出现或需要人工核对。"
        : `${pendingCount} 项证据仍待观察，避免把 submitted 当作 completed。`,
  };
}
