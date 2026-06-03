import type { ServiceServingTargetInput } from '@/shared/models/services';
import { t } from "@/shared/i18n/messages";

export type ServingTargetPlanStatus = {
  enabled: boolean;
  reason: string;
  summary: string;
  totalWeight: number;
};

const allowedServingStates = new Set([
  '',
  'active',
  'paused',
  'draining',
  'disabled',
]);

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
      reason: t("pages.deployments.servingtargetplan.there.are.currently.no", "There are currently no serving targets, and you cannot submit an empty traffic plan."),
      summary: t("pages.deployments.servingtargetplan.there.are.no.serving", "There are no serving targets to submit."),
      totalWeight,
    };
  }

  const missingRevision = targets.some((target) => !target.revisionId.trim());
  if (missingRevision) {
    return {
      enabled: false,
      reason:
        t("pages.deployments.servingtargetplan.each.serving.target.requires", "Each serving target requires a revision, and a plan that lacks a revision cannot be submitted."),
      summary: t("pages.deployments.servingtargetplan.targets.with.total.weight", "{value1} targets, with a total weight of {value2}%.", { value1: targets.length, value2: totalWeight }),
      totalWeight,
    };
  }

  const invalidServingState = targets.some(
    (target) =>
      !allowedServingStates.has(
        (target.servingState ?? '').trim().toLowerCase(),
      ),
  );
  if (invalidServingState) {
    return {
      enabled: false,
      reason:
        t("pages.deployments.servingtargetplan.serving.status.can.only", "serving status can only be selected from active, paused, draining or disabled to avoid being silently rewritten by the backend after submission."),
      summary: t("pages.deployments.servingtargetplan.targets.with.total.weight.2", "{value1} targets, with a total weight of {value2}%.", { value1: targets.length, value2: totalWeight }),
      totalWeight,
    };
  }

  if (totalWeight !== 100) {
    return {
      enabled: false,
      reason: t("pages.deployments.servingtargetplan.the.current.weight.totals", "The current weight totals {value1}% and needs to be equal to 100% to submit.", { value1: totalWeight }),
      summary: t("pages.deployments.servingtargetplan.targets.with.total.weight.3", "{value1} targets, with a total weight of {value2}%.", { value1: targets.length, value2: totalWeight }),
      totalWeight,
    };
  }

  return {
    enabled: true,
    reason:
      t("pages.deployments.servingtargetplan.the.weight.plan.can", "The weight plan can be submitted; after submission, you still need to wait for the serving/traffic readmodel evidence to be refreshed."),
    summary: t("pages.deployments.servingtargetplan.targets.with.total.weight.4", "{value1} targets, with a total weight of 100%.", { value1: targets.length }),
    totalWeight,
  };
}
