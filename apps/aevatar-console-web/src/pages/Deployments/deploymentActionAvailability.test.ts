import { setLocale } from '@umijs/max';
import { buildDeploymentDeactivateAvailability } from './deploymentActionAvailability';

describe('buildDeploymentDeactivateAvailability', () => {
  beforeEach(() => {
    setLocale('zh-CN', false);
  });

  const deployment = {
    activatedAt: '2026-03-30T10:00:00Z',
    deploymentId: 'dep-1',
    primaryActorId: 'actor-1',
    revisionId: 'rev-11',
    status: 'active',
    updatedAt: '2026-03-30T10:05:00Z',
  };

  it('disables deactivate when no deployment is selected', () => {
    const availability = buildDeploymentDeactivateAvailability(null);

    expect(availability.enabled).toBe(false);
    expect(availability.reason).toContain('未选中部署');
  });

  it('allows deactivate for active deployments', () => {
    const availability = buildDeploymentDeactivateAvailability(deployment);

    expect(availability.enabled).toBe(true);
    expect(availability.reason).toContain('仍需等待');
  });

  it('disables deactivate for inactive deployments', () => {
    const availability = buildDeploymentDeactivateAvailability({
      ...deployment,
      status: 'inactive',
    });

    expect(availability.enabled).toBe(false);
    expect(availability.reason).toContain('只适用于活动部署');
  });

  it('disables deactivate for retired deployments', () => {
    const availability = buildDeploymentDeactivateAvailability({
      ...deployment,
      status: 'retired',
    });

    expect(availability.enabled).toBe(false);
    expect(availability.summary).toContain('不可停用');
  });
});
