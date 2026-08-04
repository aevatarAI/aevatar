export type WorkflowActivitySection = "workflows" | "activity" | "settings";

function encode(value: string): string {
  return encodeURIComponent(value.trim());
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
  return `${buildWorkflowActivitySectionHref(scopeId, "workflows")}/new`;
}

export function buildWorkflowActivityEditorHref(
  scopeId: string,
  workflowId: string,
): string {
  return `${buildWorkflowActivitySectionHref(scopeId, "workflows")}/${encode(workflowId)}`;
}

export function buildWorkflowActivityRunHref(
  scopeId: string,
  runId: string,
): string {
  return `${buildWorkflowActivitySectionHref(scopeId, "activity")}/${encode(runId)}`;
}
