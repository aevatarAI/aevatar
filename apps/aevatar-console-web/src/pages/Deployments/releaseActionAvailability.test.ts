import { setLocale } from '@umijs/max';
import { buildRolloutActionAvailability } from './releaseActionAvailability';

describe('buildRolloutActionAvailability', () => {
  beforeEach(() => {
    setLocale('zh-CN', false);
  });

  const rollout = {
    baselineTargets: [],
    currentStageIndex: 0,
    displayName: 'March Canary',
    failureReason: '',
    rolloutId: 'rollout-1',
    serviceKey: 'scope-1:trade-agent',
    stages: [],
    startedAt: '2026-03-30T10:00:00Z',
    status: 'canary',
    updatedAt: '2026-03-30T10:05:00Z',
  };

  it('disables every control when there is no active rollout', () => {
    const availability = buildRolloutActionAvailability(null);

    expect(availability.advance.enabled).toBe(false);
    expect(availability.pause.enabled).toBe(false);
    expect(availability.resume.enabled).toBe(false);
    expect(availability.rollback.enabled).toBe(false);
    expect(availability.advance.reason).toContain('没有活动发布推进');
  });

  it('allows active rollout advance, pause, and rollback while keeping resume honest', () => {
    const availability = buildRolloutActionAvailability(rollout);

    expect(availability.advance.enabled).toBe(true);
    expect(availability.pause.enabled).toBe(true);
    expect(availability.resume.enabled).toBe(false);
    expect(availability.rollback.enabled).toBe(true);
    expect(availability.resume.reason).toContain('暂停状态');
  });

  it('allows only resume and rollback when the rollout is paused', () => {
    const availability = buildRolloutActionAvailability({
      ...rollout,
      status: 'paused',
    });

    expect(availability.advance.enabled).toBe(false);
    expect(availability.pause.enabled).toBe(false);
    expect(availability.resume.enabled).toBe(true);
    expect(availability.rollback.enabled).toBe(true);
    expect(availability.advance.reason).toContain('先恢复');
  });

  it('disables controls for terminal rollout statuses', () => {
    const availability = buildRolloutActionAvailability({
      ...rollout,
      status: 'completed',
    });

    expect(availability.advance.enabled).toBe(false);
    expect(availability.rollback.enabled).toBe(false);
    expect(availability.rollback.reason).toContain('不可提交');
  });

  it('treats RolledBack as a terminal rollout status', () => {
    const availability = buildRolloutActionAvailability({
      ...rollout,
      status: 'RolledBack',
    });

    expect(availability.advance.enabled).toBe(false);
    expect(availability.pause.enabled).toBe(false);
    expect(availability.resume.enabled).toBe(false);
    expect(availability.rollback.enabled).toBe(false);
    expect(availability.advance.reason).toContain('不可提交');
  });
});
