import type { ServiceDeploymentSnapshot } from '@/shared/models/services';

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
      reason: '未选中 deployment，不能提交停用命令。',
      summary: '未选中 deployment。',
    };
  }

  if (!isActiveDeploymentStatus(deployment.status)) {
    return {
      enabled: false,
      reason: `当前 deployment 状态为 ${deployment.status || 'unknown'}，停用命令只适用于活动 deployment。`,
      summary: '当前 deployment 不可停用。',
    };
  }

  return {
    enabled: true,
    reason: '停用会提交命令，仍需等待 catalog/serving/traffic 证据刷新。',
    summary: '当前 deployment 可提交停用命令。',
  };
}
