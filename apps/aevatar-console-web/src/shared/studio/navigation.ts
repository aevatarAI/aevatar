export type StudioTab =
  | 'workflows'
  | 'studio'
  | 'bindings'
  | 'invoke'
  | 'scripts'
  | 'gagents'
  | 'executions';

export type StudioStep = 'build' | 'bind' | 'invoke' | 'observe';
export type StudioBuildFocus = `workflow:${string}` | `script:${string}` | `template:${string}`;
export type StudioIntent = 'create-member';

type StudioRouteOptions = {
  scopeId?: string;
  memberId?: string;
  step?: StudioStep;
  focus?: StudioBuildFocus;
  tab?: StudioTab;
  intent?: StudioIntent;
  prompt?: string;
  executionId?: string;
  logsMode?: 'popout';
} & Record<string, unknown>;

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function normalizeStudioBuildFocus(
  value: StudioBuildFocus | string | null | undefined,
): StudioBuildFocus | undefined {
  const normalizedValue = trimOptional(value);
  if (
    normalizedValue.startsWith('workflow:') ||
    normalizedValue.startsWith('script:') ||
    normalizedValue.startsWith('template:')
  ) {
    return normalizedValue as StudioBuildFocus;
  }

  return undefined;
}

function resolveStudioTab(options?: StudioRouteOptions): StudioTab | undefined {
  if (options?.tab?.trim()) {
    return options.tab.trim() as StudioTab;
  }

  if (options?.step === 'bind') {
    return 'bindings';
  }

  if (options?.step === 'invoke') {
    return 'invoke';
  }

  if (options?.step === 'observe') {
    return 'executions';
  }

  if (options?.executionId?.trim()) {
    return 'executions';
  }

  const focus = normalizeStudioBuildFocus(options?.focus);
  if (focus?.startsWith('script:')) {
    return 'scripts';
  }

  if (
    focus?.startsWith('workflow:') ||
    focus?.startsWith('template:') ||
    options?.prompt?.trim()
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
  if (options?.memberId?.trim()) {
    params.set('memberId', options.memberId.trim());
  }
  if (options?.step) {
    params.set('step', options.step);
  }
  const focus = normalizeStudioBuildFocus(options?.focus);
  if (focus) {
    params.set('focus', focus);
  }
  const tab = resolveStudioTab(options);
  if (tab) {
    params.set('tab', tab);
  }
  if (options?.intent === 'create-member') {
    params.set('intent', options.intent);
  }
  if (options?.prompt?.trim()) {
    params.set('prompt', options.prompt.trim());
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
  memberId?: string;
} & Record<string, unknown>): string {
  return buildStudioRoute({
    ...options,
    tab: 'studio',
  });
}

export function buildStudioWorkflowEditorRoute(options?: {
  scopeId?: string;
  memberId?: string;
  workflowId?: string;
  template?: string;
  prompt?: string;
} & Record<string, unknown>): string {
  const workflowId = trimOptional(options?.workflowId);
  const template = trimOptional(options?.template);
  return buildStudioRoute({
    ...options,
    focus: workflowId
      ? `workflow:${workflowId}`
      : template
        ? `template:${template}`
        : undefined,
    tab: 'studio',
  });
}

export function buildStudioBindingWorkspaceRoute(options?: {
  scopeId?: string;
  memberId?: string;
} & Record<string, unknown>): string {
  return buildStudioRoute({
    ...options,
    step: 'bind',
    tab: 'bindings',
  });
}

export function buildStudioInvokeWorkspaceRoute(options?: {
  scopeId?: string;
  memberId?: string;
} & Record<string, unknown>): string {
  return buildStudioRoute({
    ...options,
    step: 'invoke',
    tab: 'invoke',
  });
}

export function buildStudioScriptsWorkspaceRoute(options?: {
  scopeId?: string;
  memberId?: string;
  scriptId?: string;
} & Record<string, unknown>): string {
  const scriptId = trimOptional(options?.scriptId);
  return buildStudioRoute({
    ...options,
    focus: scriptId ? `script:${scriptId}` : undefined,
    tab: 'scripts',
  });
}
