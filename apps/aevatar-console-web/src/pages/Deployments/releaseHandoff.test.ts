import { setLocale } from '@umijs/max';
import { buildDeploymentReleaseHandoff } from './releaseHandoff';

describe('buildDeploymentReleaseHandoff', () => {
  beforeEach(() => {
    setLocale('zh-CN', false);
  });

  it('keeps submitted commands separate from observed serving state', () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: 'deploy-candidate',
      activeRevisionId: 'rev-11',
      candidateRevisionId: 'rev-12',
      receipt: {
        commandId: 'cmd-1',
        correlationId: 'corr-1',
        targetActorId: 'actor-1',
      },
      rolloutId: 'rollout-1',
      rolloutStageLabel: '2/3',
      serviceId: 'trade-agent',
      targetCount: 2,
    });

    expect(handoff.pendingLabel).toBe('已提交，不代表已完成');
    expect(handoff.noticeMessage).toContain('等待 rollout/serving 证据刷新');
    expect(handoff.evidenceDescription).toContain(
      '尚未说明候选 revision 已经被 serving 观察到',
    );
    expect(handoff.evidenceView).toBe('rollout');
    expect(handoff.summaryItems).toEqual(
      expect.arrayContaining([
        {
          label: '候选 revision',
          value: 'rev-12',
        },
        {
          label: '当前 serving',
          value: 'rev-11',
        },
      ]),
    );
  });

  it('routes serving replacement evidence to serving and traffic checks', () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: 'replace-serving-targets',
      activeRevisionId: 'rev-11',
      endpointCount: 1,
      receipt: {
        commandId: 'cmd-2',
        correlationId: 'corr-2',
      },
      serviceId: 'trade-agent',
      targetCount: 2,
    });

    expect(handoff.evidenceView).toBe('serving');
    expect(handoff.evidenceItems.join(' ')).toContain('Serving generation');
    expect(handoff.evidenceItems.join(' ')).toContain('Traffic');
    expect(handoff.noticeMessage).toContain('等待 serving/traffic 证据刷新');
  });

  it('does not describe rollback as completed serving state', () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: 'rollback-rollout',
      activeRevisionId: 'rev-12',
      receipt: {
        commandId: 'cmd-6',
        correlationId: 'corr-6',
      },
      rolloutId: 'rollout-1',
      serviceId: 'trade-agent',
    });

    expect(handoff.noticeTone).toBe('warning');
    expect(handoff.evidenceDescription).toContain(
      '不代表 serving 已经回到 baseline',
    );
    expect(handoff.evidenceItems.join(' ')).toContain('baseline');
  });

  it('keeps deactivate handoff pointed at catalog and serving evidence', () => {
    const handoff = buildDeploymentReleaseHandoff({
      action: 'deactivate-deployment',
      deploymentId: 'dep-1',
      receipt: {
        commandId: 'cmd-7',
        correlationId: 'corr-7',
      },
      serviceId: 'trade-agent',
    });

    expect(handoff.evidenceView).toBe('catalog');
    expect(handoff.summaryItems).toEqual(
      expect.arrayContaining([
        {
          label: 'Deployment',
          value: 'dep-1',
        },
      ]),
    );
    expect(handoff.evidenceItems.join(' ')).toContain('Serving targets');
  });
});
