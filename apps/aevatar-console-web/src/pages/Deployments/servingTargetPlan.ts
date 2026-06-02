import type { ServiceServingTargetInput } from "@/shared/models/services";

export type ServingTargetPlanStatus = {
  enabled: boolean;
  reason: string;
  summary: string;
  totalWeight: number;
};

export function buildServingTargetPlanStatus(
  targets: readonly ServiceServingTargetInput[],
): ServingTargetPlanStatus {
  const totalWeight = targets.reduce(
    (total, target) => total + Number(target.allocationWeight || 0),
    0,
  );

  if (!targets.length) {
    return {
      enabled: false,
      reason: "当前没有 serving targets，不能提交空的流量计划。",
      summary: "没有可提交的 serving targets。",
      totalWeight,
    };
  }

  const missingRevision = targets.some((target) => !target.revisionId.trim());
  if (missingRevision) {
    return {
      enabled: false,
      reason: "每个 serving target 都需要 revision，不能提交缺少 revision 的计划。",
      summary: `${targets.length} 个 target，权重合计 ${totalWeight}%。`,
      totalWeight,
    };
  }

  if (totalWeight !== 100) {
    return {
      enabled: false,
      reason: `当前权重合计 ${totalWeight}%，需要等于 100% 才能提交。`,
      summary: `${targets.length} 个 target，权重合计 ${totalWeight}%。`,
      totalWeight,
    };
  }

  return {
    enabled: true,
    reason: "权重计划可提交；提交后仍需等待 serving/traffic readmodel 证据刷新。",
    summary: `${targets.length} 个 target，权重合计 100%。`,
    totalWeight,
  };
}
