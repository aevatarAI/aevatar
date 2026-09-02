import type { StudioMemberDetail } from "./models";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

export function isStudioWorkflowDraftIdentity(
  value: string | null | undefined,
): boolean {
  const normalizedValue = trimOptional(value);
  return Boolean(normalizedValue && !/\s/.test(normalizedValue));
}

export function resolveStudioMemberDraftWorkflowId(
  member: Pick<StudioMemberDetail, "implementationRef"> | null | undefined,
): string {
  const implementationRef = member?.implementationRef;
  if (implementationRef?.implementationKind !== "workflow") {
    return "";
  }

  const draftWorkflowId = trimOptional(implementationRef.workflowId);
  return isStudioWorkflowDraftIdentity(draftWorkflowId) ? draftWorkflowId : "";
}
