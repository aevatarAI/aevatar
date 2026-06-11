import type { ServiceDeploymentSnapshot } from '@/shared/models/services';
import { t } from "@/shared/i18n/messages";

export type DeploymentActionAvailability = {
  enabled: boolean;
  reason: string;
  summary: string;
};

function normalizeStatus(status: string | null | undefined): string {
  return (status ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
}

function isActiveDeploymentStatus(status: string | null | undefined): boolean {
  const normalized = normalizeStatus(status);
  if (normalized === 'inactive' || normalized === 'deactivated') {
    return false;
  }

  return (
    normalized === 'active' ||
    normalized === 'activated' ||
    normalized === 'canary' ||
    normalized === 'ready' ||
    normalized === 'running' ||
    normalized.startsWith('active')
  );
}

export function buildDeploymentDeactivateAvailability(
  deployment: ServiceDeploymentSnapshot | null | undefined,
): DeploymentActionAvailability {
  if (!deployment?.deploymentId?.trim()) {
    return {
      enabled: false,
      reason: t("pages.deployments.deploymentactionavailability.the.deployment.is.not", "The deployment is not selected and the deactivation command cannot be submitted."),
      summary: t("pages.deployments.deploymentactionavailability.deployment.is.not.selected", "deployment is not selected."),
    };
  }

  if (!isActiveDeploymentStatus(deployment.status)) {
    return {
      enabled: false,
      reason: t("pages.deployments.deploymentactionavailability.the.current.deployment.status", "The current deployment status is {value1} and the deactivation command only applies to active deployments.", { value1: deployment.status || 'unknown' }),
      summary: t("pages.deployments.deploymentactionavailability.the.current.deployment.cannot", "The current deployment cannot be deactivated."),
    };
  }

  return {
    enabled: true,
    reason: t("pages.deployments.deploymentactionavailability.deactivation.will.submit.the", "Deactivation will submit the command and still need to wait for the catalog/serving/traffic evidence to be refreshed."),
    summary: t("pages.deployments.deploymentactionavailability.the.current.deployment.can", "The current deployment can submit deactivation commands."),
  };
}
