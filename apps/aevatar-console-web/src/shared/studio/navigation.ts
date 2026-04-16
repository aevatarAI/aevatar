export type StudioTab =
  | 'workflows'
  | 'files'
  | 'studio'
  | 'scripts'
  | 'executions'
  | 'roles'
  | 'connectors'
  | 'settings';

type StudioRouteOptions = {
  scopeId?: string;
  scopeLabel?: string;
  memberId?: string;
  memberLabel?: string;
  teamMode?: 'create';
  teamName?: string;
  entryName?: string;
  teamDraftWorkflowId?: string;
  teamDraftWorkflowName?: string;
  workflowId?: string;
  scriptId?: string;
  template?: string;
  tab?: StudioTab;
  draftMode?: 'new';
  prompt?: string;
  legacySource?: 'playground';
  executionId?: string;
  logsMode?: 'popout';
};

function resolveStudioTab(options?: StudioRouteOptions): StudioTab | undefined {
  if (options?.tab?.trim()) {
    return options.tab.trim() as StudioTab;
  }

  if (options?.executionId?.trim()) {
    return 'executions';
  }

  if (options?.scriptId?.trim()) {
    return 'scripts';
  }

  if (
    options?.teamMode === 'create' ||
    options?.workflowId?.trim() ||
    options?.template?.trim() ||
    options?.draftMode === 'new' ||
    options?.prompt?.trim() ||
    options?.legacySource === 'playground'
  ) {
    return 'studio';
  }

  return undefined;
}

export function buildStudioRoute(options?: StudioRouteOptions): string {
  const params = new URLSearchParams();
  if (options?.scopeId?.trim()) {
    params.set('scopeId', options.scopeId.trim());
  }
  if (options?.scopeLabel?.trim()) {
    params.set('scopeLabel', options.scopeLabel.trim());
  }
  if (options?.memberId?.trim()) {
    params.set('memberId', options.memberId.trim());
  }
  if (options?.memberLabel?.trim()) {
    params.set('memberLabel', options.memberLabel.trim());
  }
  if (options?.teamMode === 'create') {
    params.set('teamMode', 'create');
  }
  if (options?.teamName?.trim()) {
    params.set('teamName', options.teamName.trim());
  }
  if (options?.entryName?.trim()) {
    params.set('entryName', options.entryName.trim());
  }
  if (options?.teamDraftWorkflowId?.trim()) {
    params.set('teamDraftWorkflowId', options.teamDraftWorkflowId.trim());
  }
  if (options?.teamDraftWorkflowName?.trim()) {
    params.set('teamDraftWorkflowName', options.teamDraftWorkflowName.trim());
  }
  if (options?.workflowId?.trim()) {
    params.set('workflow', options.workflowId.trim());
  }
  if (options?.scriptId?.trim()) {
    params.set('script', options.scriptId.trim());
  }
  if (options?.template?.trim()) {
    params.set('template', options.template.trim());
  }
  const tab = resolveStudioTab(options);
  if (tab) {
    params.set('tab', tab);
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

export function buildStudioWorkflowWorkspaceRoute(options?: {
  scopeId?: string;
  scopeLabel?: string;
  memberId?: string;
  memberLabel?: string;
}): string {
  return buildStudioRoute({
    ...options,
    tab: 'workflows',
  });
}

export function buildStudioFilesWorkspaceRoute(): string {
  return buildStudioRoute({
    tab: 'files',
  });
}

export function buildStudioWorkflowEditorRoute(options?: {
  scopeId?: string;
  scopeLabel?: string;
  memberId?: string;
  memberLabel?: string;
  teamMode?: 'create';
  teamName?: string;
  entryName?: string;
  teamDraftWorkflowId?: string;
  teamDraftWorkflowName?: string;
  workflowId?: string;
  template?: string;
  draftMode?: 'new';
  prompt?: string;
  legacySource?: 'playground';
}): string {
  return buildStudioRoute({
    ...options,
    tab: 'studio',
  });
}

export function buildStudioScriptsWorkspaceRoute(options?: {
  scopeId?: string;
  scopeLabel?: string;
  memberId?: string;
  memberLabel?: string;
  scriptId?: string;
}): string {
  return buildStudioRoute({
    ...options,
    tab: 'scripts',
  });
}
