import type { ServiceRolloutSnapshot } from '@/shared/models/services';

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
    const reason = '当前没有活动 rollout，不能提交 rollout 控制动作。';
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
    const reason = `当前 rollout 状态为 ${rollout.status || 'terminal'}，控制动作已不可提交。`;
    return {
      advance: { enabled: false, reason },
      pause: { enabled: false, reason },
      resume: { enabled: false, reason },
      rollback: { enabled: false, reason },
    };
  }

  if (rollbackActive) {
    const reason = '当前 rollout 已在回滚流程中，等待回滚证据刷新后再操作。';
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
        ? '当前 rollout 已暂停；请先恢复，再推进到下一阶段。'
        : '推进会提交命令，仍需等待 rollout/serving/traffic 证据刷新。',
    },
    pause: {
      enabled: !paused,
      reason: paused
        ? '当前 rollout 已暂停，不需要再次暂停。'
        : '暂停会提交命令，仍需等待 rollout 状态显示 paused。',
    },
    resume: {
      enabled: paused,
      reason: paused
        ? '恢复会提交命令，仍需等待 rollout 状态重新活动。'
        : '只有 paused 状态的 rollout 才需要恢复。',
    },
    rollback: {
      enabled: true,
      reason: '回滚会提交命令，仍需等待 serving 回到 baseline 证据。',
    },
  };
}
