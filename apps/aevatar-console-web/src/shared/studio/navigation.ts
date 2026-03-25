export type StudioTab =
  | 'workflows'
  | 'studio'
  | 'scripts'
  | 'executions'
  | 'roles'
  | 'connectors'
  | 'settings';

export function buildStudioRoute(options?: {
  workflowId?: string;
  scriptId?: string;
  template?: string;
  tab?: StudioTab;
  draftMode?: 'new';
  prompt?: string;
  legacySource?: 'playground';
  executionId?: string;
  logsMode?: 'popout';
}): string {
  const params = new URLSearchParams();
  if (options?.workflowId?.trim()) {
    params.set('workflow', options.workflowId.trim());
  }
  if (options?.scriptId?.trim()) {
    params.set('script', options.scriptId.trim());
  }
  if (options?.template?.trim()) {
    params.set('template', options.template.trim());
  }
  if (options?.tab?.trim()) {
    params.set('tab', options.tab.trim());
  }
  if (options?.draftMode === 'new') {
    params.set('draft', 'new');
  }
  if (options?.prompt?.trim()) {
    params.set('prompt', options.prompt.trim());
  }
  if (options?.legacySource === 'playground') {
    params.set('legacy', 'playground');
  }
  if (options?.executionId?.trim()) {
    params.set('execution', options.executionId.trim());
  }
  if (options?.logsMode === 'popout') {
    params.set('logs', 'popout');
  }

  const query = params.toString();
  return query ? `/studio?${query}` : '/studio';
}
