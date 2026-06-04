import { setLocale } from '@umijs/max';
import { buildServingTargetPlanStatus } from './servingTargetPlan';

describe('buildServingTargetPlanStatus', () => {
  beforeEach(() => {
    setLocale('zh-CN', false);
  });

  it('disables empty serving target submissions', () => {
    const status = buildServingTargetPlanStatus([]);

    expect(status.enabled).toBe(false);
    expect(status.reason).toContain('不能提交空的流量计划');
  });

  it('requires every target to identify a revision', () => {
    const status = buildServingTargetPlanStatus([
      {
        allocationWeight: 100,
        revisionId: '',
      },
    ]);

    expect(status.enabled).toBe(false);
    expect(status.reason).toContain('缺少修订');
  });

  it('requires allocation weights to add up to 100 percent', () => {
    const status = buildServingTargetPlanStatus([
      {
        allocationWeight: 60,
        revisionId: 'rev-1',
      },
      {
        allocationWeight: 20,
        revisionId: 'rev-2',
      },
    ]);

    expect(status.enabled).toBe(false);
    expect(status.totalWeight).toBe(80);
    expect(status.reason).toContain('80%');
  });

  it('rejects serving states that the API would silently coerce to active', () => {
    const status = buildServingTargetPlanStatus([
      {
        allocationWeight: 100,
        revisionId: 'rev-1',
        servingState: 'canary',
      },
    ]);

    expect(status.enabled).toBe(false);
    expect(status.reason).toContain('服务态状态只能选择');
  });

  it('allows a complete 100 percent serving target plan', () => {
    const status = buildServingTargetPlanStatus([
      {
        allocationWeight: 90,
        revisionId: 'rev-1',
        servingState: 'active',
      },
      {
        allocationWeight: 10,
        revisionId: 'rev-2',
        servingState: 'draining',
      },
    ]);

    expect(status.enabled).toBe(true);
    expect(status.summary).toContain('100%');
  });
});
