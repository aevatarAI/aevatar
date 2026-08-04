import type { StudioWorkflowDocument } from "@/shared/studio/models";

export type WorkflowCreationMode = "describe" | "blank" | "import" | "template";

export type BundledWorkflowTemplate = {
  readonly id: string;
  readonly version: string;
  readonly yaml: string;
};

export const BUNDLED_WORKFLOW_TEMPLATES: readonly BundledWorkflowTemplate[] = [
  {
    id: "incident-triage",
    version: "2026.08.1",
    yaml: `name: incident_triage
description: Classify an incident and prepare a reviewed response.
roles:
  - id: responder
    name: Incident responder
    systemPrompt: Review incident reports carefully.
steps:
  - id: classify
    type: llm_call
    targetRole: responder
    parameters:
      prompt_prefix: Classify severity and summarize impact.
    next: approve
  - id: approve
    type: human_approval
    parameters:
      prompt: Approve the proposed response?
      on_reject: fail
`,
  },
] as const;

export function slugifyWorkflowFileName(name: string): string {
  const slug = name
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return `${slug || "workflow"}.yaml`;
}

export function createBlankWorkflowYaml(name: string): string {
  const documentName = name.trim().replace(/[^A-Za-z0-9_]+/g, "_").replace(/^_+|_+$/g, "") || "workflow";
  return `name: ${documentName}\ndescription: \nroles: []\nsteps: []\n`;
}

export function hasBlockingFindings(
  document: StudioWorkflowDocument | null | undefined,
  findings: readonly { readonly level?: string | number; readonly message: string }[],
): boolean {
  if (!document) return true;
  return findings.some((finding) => {
    const level = String(finding.level ?? "").toLowerCase();
    return level === "error" || level === "fatal" || level === "2" || level === "3";
  });
}
