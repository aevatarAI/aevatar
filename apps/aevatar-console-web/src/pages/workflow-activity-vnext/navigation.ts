export type WorkflowActivitySection = 'workflows' | 'activity' | 'settings';

function encode(value: string): string {
  return encodeURIComponent(value.trim());
}

function buildQuerySuffix(
  query?: Readonly<Record<string, string | undefined>>,
): string {
  if (!query) {
    return '';
  }

  return Object.entries(query)
    .map(([key, value]) => [key.trim(), value?.trim() ?? ''] as const)
    .filter(([key, value]) => Boolean(key) && Boolean(value))
    .map(
      ([key, value]) =>
        `${encodeURIComponent(key)}=${encodeURIComponent(value)}`,
    )
    .join('&');
}

export function buildWorkflowActivityBaseHref(scopeId: string): string {
  return `/scopes/${encode(scopeId)}/workflow-activity-vnext`;
}

export function buildWorkflowActivitySectionHref(
  scopeId: string,
  section: WorkflowActivitySection,
): string {
  return `${buildWorkflowActivityBaseHref(scopeId)}/${section}`;
}

export function buildWorkflowActivityNewHref(scopeId: string): string {
  return `${buildWorkflowActivitySectionHref(scopeId, 'workflows')}/new`;
}

export function buildWorkflowActivityTemplatesHref(scopeId: string): string {
  return `${buildWorkflowActivityNewHref(scopeId)}/templates`;
}

export function buildWorkflowActivityEditorHref(
  scopeId: string,
  workflowId: string,
): string {
  return `${buildWorkflowActivitySectionHref(scopeId, 'workflows')}/${encode(workflowId)}`;
}

export function buildWorkflowActivityEditorRunHref(
  scopeId: string,
  workflowId: string,
): string {
  return `${buildWorkflowActivityEditorHref(scopeId, workflowId)}?run=1`;
}

export function buildWorkflowActivityRunHref(
  scopeId: string,
  runId: string,
  query?: Readonly<Record<string, string | undefined>>,
): string {
  const base = `${buildWorkflowActivitySectionHref(scopeId, 'activity')}/${encode(runId)}`;
  const suffix = buildQuerySuffix(query);
  return suffix ? `${base}?${suffix}` : base;
}
