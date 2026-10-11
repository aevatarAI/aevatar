type ScopedRoute = { readonly scopeId: string };

export type WorkflowActivityRoute = ScopedRoute &
  (
    | {
        readonly page:
          | 'workflows'
          | 'newWorkflow'
          | 'workflowTemplates'
          | 'activity'
          | 'channels'
          | 'settings';
      }
    | { readonly page: 'workflowEditor'; readonly workflowId: string }
    | { readonly page: 'runDetail'; readonly runId: string }
    | {
        readonly page: 'channelBind';
        readonly botId: string;
        readonly defaultSkillId: string | undefined;
        readonly requestedServiceIds: string[];
        readonly routeKey: string;
      }
    | {
        readonly page: 'channelEdit';
        readonly registrationId: string;
        readonly requestedServiceIds: string[];
        readonly routeKey: string;
      }
    | {
        readonly page: 'channelDetails';
        readonly registrationId: string;
        readonly routeKey: string;
      }
  );

// Keep route decoding independent of page imports so each Umi entry loads
// only the page it owns. IDs are decoded once and retain their resource type.
export function parseWorkflowActivityRoute(
  pathname: string,
  search = '',
): WorkflowActivityRoute | null {
  const scopeMatch = /^\/scopes\/([^/]+)\/(.+)$/.exec(pathname);
  if (!scopeMatch) return null;
  const scopeId = decodeURIComponent(scopeMatch[1]);
  const resourcePath = scopeMatch[2];

  switch (resourcePath) {
    case 'workflows':
      return { page: 'workflows', scopeId };
    case 'workflows/new':
      return { page: 'newWorkflow', scopeId };
    case 'workflows/new/templates':
      return { page: 'workflowTemplates', scopeId };
    case 'activity':
      return { page: 'activity', scopeId };
    case 'channels':
      return { page: 'channels', scopeId };
    case 'settings':
      return { page: 'settings', scopeId };
  }

  const workflowMatch = /^workflows\/([^/]+)$/.exec(resourcePath);
  if (workflowMatch) {
    return {
      page: 'workflowEditor',
      scopeId,
      workflowId: decodeURIComponent(workflowMatch[1]),
    };
  }
  const runMatch = /^activity\/([^/]+)$/.exec(resourcePath);
  if (runMatch) {
    return {
      page: 'runDetail',
      scopeId,
      runId: decodeURIComponent(runMatch[1]),
    };
  }
  const channelBindMatch = /^channels\/bind\/([^/]+)$/.exec(resourcePath);
  if (channelBindMatch) {
    const query = new URLSearchParams(search);
    return {
      page: 'channelBind',
      scopeId,
      botId: decodeURIComponent(channelBindMatch[1]),
      defaultSkillId: query.get('skillId')?.trim() || undefined,
      requestedServiceIds: query.getAll('requiredServiceId'),
      routeKey: `${scopeId}:bind:${channelBindMatch[1]}`,
    };
  }
  const channelEditMatch = /^channels\/([^/]+)\/edit$/.exec(resourcePath);
  if (channelEditMatch) {
    return {
      page: 'channelEdit',
      scopeId,
      registrationId: decodeURIComponent(channelEditMatch[1]),
      requestedServiceIds: new URLSearchParams(search).getAll(
        'requiredServiceId',
      ),
      routeKey: `${scopeId}:${channelEditMatch[1]}`,
    };
  }
  const channelMatch = /^channels\/([^/]+)$/.exec(resourcePath);
  if (channelMatch) {
    return {
      page: 'channelDetails',
      scopeId,
      registrationId: decodeURIComponent(channelMatch[1]),
      routeKey: `${scopeId}:${channelMatch[1]}`,
    };
  }
  return null;
}
