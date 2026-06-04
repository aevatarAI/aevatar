import type { ServiceRolloutSnapshot } from '@/shared/models/services';
import { t } from "@/shared/i18n/messages";

export type RolloutControlAction = 'advance' | 'pause' | 'resume' | 'rollback';

export type ReleaseActionAvailability = {
  enabled: boolean;
  reason: string;
};

function normalizeStatus(status: string | null | undefined): string {
  return (status ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
}

function hasAnyStatus(status: string, patterns: readonly string[]): boolean {
  return patterns.some((pattern) => status.includes(pattern));
}

export function buildRolloutActionAvailability(
  rollout: ServiceRolloutSnapshot | null | undefined,
): Record<RolloutControlAction, ReleaseActionAvailability> {
  if (!rollout?.rolloutId?.trim()) {
    const reason = t("pages.deployments.releaseactionavailability.there.is.currently.no", "There is currently no active rollout and rollout control actions cannot be submitted.");
    return {
      advance: { enabled: false, reason },
      pause: { enabled: false, reason },
      resume: { enabled: false, reason },
      rollback: { enabled: false, reason },
    };
  }

  const status = normalizeStatus(rollout.status);
  const terminal = hasAnyStatus(status, [
    'cancel',
    'complete',
    'done',
    'fail',
    'inactive',
    'rolledback',
    'retire',
    'success',
  ]);
  const paused = hasAnyStatus(status, ['pause']);
  const rollbackActive = hasAnyStatus(status, ['rollback', 'rollingback']);

  if (terminal) {
    const reason = t("pages.deployments.releaseactionavailability.the.current.rollout.status", "The current rollout status is {value1}, and the control action cannot be submitted.", { value1: rollout.status || 'terminal' });
    return {
      advance: { enabled: false, reason },
      pause: { enabled: false, reason },
      resume: { enabled: false, reason },
      rollback: { enabled: false, reason },
    };
  }

  if (rollbackActive) {
    const reason = t("pages.deployments.releaseactionavailability.the.current.rollout.is", "The current rollout is already in the rollback process, wait for the rollback evidence to be refreshed before proceeding.");
    return {
      advance: { enabled: false, reason },
      pause: { enabled: false, reason },
      resume: { enabled: false, reason },
      rollback: { enabled: false, reason },
    };
  }

  return {
    advance: {
      enabled: !paused,
      reason: paused
        ? t("pages.deployments.releaseactionavailability.the.current.rollout.is.2", "The current rollout is paused; please resume it before advancing to the next stage.")
        : t("pages.deployments.releaseactionavailability.the.push.will.submit", "The push will submit the command and still need to wait for the rollout/serving/traffic evidence to be refreshed."),
    },
    pause: {
      enabled: !paused,
      reason: paused
        ? t("pages.deployments.releaseactionavailability.the.current.rollout.is.3", "The current rollout is paused and does not need to be paused again.")
        : t("pages.deployments.releaseactionavailability.pause.will.submit.the", "Pause will submit the command, and you still need to wait for the rollout status to show paused."),
    },
    resume: {
      enabled: paused,
      reason: paused
        ? t("pages.deployments.releaseactionavailability.recovery.will.submit.the", "Recovery will submit the command and still wait for the rollout state to become active again.")
        : t("pages.deployments.releaseactionavailability.only.rollouts.in.the", "Only rollouts in the paused state need to be restored."),
    },
    rollback: {
      enabled: true,
      reason: t("pages.deployments.releaseactionavailability.rollback.will.commit.the", "Rollback will commit the command and still need to wait for evidence that serving returns to baseline."),
    },
  };
}
