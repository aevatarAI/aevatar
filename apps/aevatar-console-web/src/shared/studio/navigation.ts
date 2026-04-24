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
export type StudioMemberKey =
  | `member:${string}`
  | `workflow:${string}`
  | `script:${string}`;

type StudioRouteOptions = {
  scopeId?: string;
  memberId?: string;
  memberKey?: StudioMemberKey | string;
  step?: StudioStep;
  focus?: StudioBuildFocus;
  tab?: StudioTab;
  prompt?: string;
  executionId?: string;
  logsMode?: 'popout';
} & Record<string, unknown>;

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function readWorkflowFileStem(fileName: string | null | undefined): string {
  return trimOptional(fileName).replace(/\.(ya?ml)$/i, '');
}

export function resolveStudioWorkflowMemberRouteValue(options?: {
  workflowId?: string | null;
  workflowName?: string | null;
  fileName?: string | null;
}): string {
  const workflowId = trimOptional(options?.workflowId);
  if (workflowId && workflowId.toLowerCase() !== 'default') {
    return workflowId;
  }

  return (
    trimOptional(options?.workflowName) ||
    readWorkflowFileStem(options?.fileName) ||
    workflowId
  );
}

export function buildStudioWorkflowMemberKey(options?: {
  workflowId?: string | null;
  workflowName?: string | null;
  fileName?: string | null;
}): StudioMemberKey | undefined {
  const routeValue = resolveStudioWorkflowMemberRouteValue(options);
  return routeValue ? (`workflow:${routeValue}` as const) : undefined;
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

function normalizeStudioMemberKey(
  value: StudioMemberKey | string | null | undefined,
  fallbackMemberId?: string | null | undefined,
): StudioMemberKey | undefined {
  const normalizedValue = trimOptional(value);
  if (
    normalizedValue.startsWith('member:') ||
    normalizedValue.startsWith('workflow:') ||
    normalizedValue.startsWith('script:')
  ) {
    return normalizedValue as StudioMemberKey;
  }

  const normalizedMemberId = trimOptional(fallbackMemberId);
  return normalizedMemberId
    ? (`member:${normalizedMemberId}` as const)
    : undefined;
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
  const memberKey = normalizeStudioMemberKey(
    options?.memberKey,
    options?.memberId,
  );
  if (memberKey) {
    params.set('member', memberKey);
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
  memberKey?: StudioMemberKey | string;
} & Record<string, unknown>): string {
  return buildStudioRoute({
    ...options,
    tab: 'studio',
  });
}

export function buildStudioWorkflowEditorRoute(options?: {
  scopeId?: string;
  memberId?: string;
  memberKey?: StudioMemberKey | string;
  workflowId?: string;
  template?: string;
  prompt?: string;
} & Record<string, unknown>): string {
  const workflowId = trimOptional(options?.workflowId);
  const template = trimOptional(options?.template);
  const memberKey = normalizeStudioMemberKey(
    options?.memberKey,
    options?.memberId,
  );
  const hasWorkflowMemberKey = memberKey?.startsWith('workflow:');
  const workflowFocus = workflowId
    ? (`workflow:${workflowId}` as const)
    : undefined;
  return buildStudioRoute({
    ...options,
    focus:
      !hasWorkflowMemberKey && workflowFocus && workflowFocus !== memberKey
        ? workflowFocus
        : template
        ? `template:${template}`
        : undefined,
    tab: 'studio',
  });
}

export function buildStudioBindingWorkspaceRoute(options?: {
  scopeId?: string;
  memberId?: string;
  memberKey?: StudioMemberKey | string;
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
  memberKey?: StudioMemberKey | string;
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
  memberKey?: StudioMemberKey | string;
  scriptId?: string;
} & Record<string, unknown>): string {
  const scriptId = trimOptional(options?.scriptId);
  const memberKey = normalizeStudioMemberKey(
    options?.memberKey,
    options?.memberId,
  );
  const scriptFocus = scriptId ? (`script:${scriptId}` as const) : undefined;
  return buildStudioRoute({
    ...options,
    focus:
      scriptFocus && scriptFocus !== memberKey ? scriptFocus : undefined,
    tab: 'scripts',
  });
}
