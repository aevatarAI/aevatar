import { parseWorkflowActivityRoute } from './routeParameters';

describe('console route parameters', () => {
  it('keeps creation and template entry paths out of workflow draft identities', () => {
    expect(parseWorkflowActivityRoute('/scopes/s-alpha/workflows/new')).toEqual(
      {
        page: 'newWorkflow',
        scopeId: 's-alpha',
      },
    );
    expect(
      parseWorkflowActivityRoute('/scopes/s-alpha/workflows/new/templates'),
    ).toEqual({ page: 'workflowTemplates', scopeId: 's-alpha' });
    expect(
      parseWorkflowActivityRoute('/scopes/s-alpha/teams/m-alpha'),
    ).toBeNull();
  });

  it('decodes scope and resource identities once without replacing them with query hints', () => {
    expect(
      parseWorkflowActivityRoute(
        '/scopes/scope%2Falpha/workflows/wf%252Falpha',
        '?workflowId=wf-other&memberId=m-alpha&publishedServiceId=svc-alpha',
      ),
    ).toEqual({
      page: 'workflowEditor',
      scopeId: 'scope/alpha',
      workflowId: 'wf%2Falpha',
    });
    expect(
      parseWorkflowActivityRoute('/scopes/s-alpha/activity/run%2Falpha'),
    ).toEqual({
      page: 'runDetail',
      scopeId: 's-alpha',
      runId: 'run/alpha',
    });
  });

  it('preserves bind defaults and every requested service for the exact bot', () => {
    expect(
      parseWorkflowActivityRoute(
        '/scopes/s-alpha/channels/bind/bot%2Falpha',
        '?skillId=%20skill-alpha%20&requiredServiceId=us-ornn&requiredServiceId=us%2Flark',
      ),
    ).toEqual({
      page: 'channelBind',
      scopeId: 's-alpha',
      botId: 'bot/alpha',
      defaultSkillId: 'skill-alpha',
      requestedServiceIds: ['us-ornn', 'us/lark'],
      routeKey: 's-alpha:bind:bot%2Falpha',
    });
  });

  it('keeps editing and detail on the registration identity and ignores bind-only defaults', () => {
    const path = '/scopes/s-beta/channels/reg%2Falpha';
    expect(
      parseWorkflowActivityRoute(
        `${path}/edit`,
        '?skillId=ignored&requiredServiceId=us-github',
      ),
    ).toEqual({
      page: 'channelEdit',
      scopeId: 's-beta',
      registrationId: 'reg/alpha',
      requestedServiceIds: ['us-github'],
      routeKey: 's-beta:reg%2Falpha',
    });
    expect(parseWorkflowActivityRoute(path)).toEqual({
      page: 'channelDetails',
      scopeId: 's-beta',
      registrationId: 'reg/alpha',
      routeKey: 's-beta:reg%2Falpha',
    });
  });
});
