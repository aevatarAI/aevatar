import type { StudioWorkflowDocument } from '@/shared/studio/models';

export type WorkflowCreationMode = 'describe' | 'blank' | 'import' | 'template';

export type BundledWorkflowTemplate = {
  readonly copyNameMessage: WorkflowTemplateMessage;
  readonly descriptionMessage: WorkflowTemplateMessage;
  readonly id: string;
  readonly nameMessage: WorkflowTemplateMessage;
  readonly version: string;
  readonly yaml: string;
};

export type WorkflowTemplateMessage = {
  readonly defaultMessage: string;
  readonly id: string;
};

export const BUNDLED_WORKFLOW_TEMPLATES: readonly BundledWorkflowTemplate[] = [
  {
    copyNameMessage: {
      defaultMessage: 'Incident triage copy',
      id: 'workflowActivityVNext.new.templateCopyName.incidentTriage',
    },
    descriptionMessage: {
      defaultMessage:
        'Classify an incident, prepare a response, and request human approval.',
      id: 'workflowActivityVNext.new.templateDescription.incidentTriage',
    },
    id: 'incident-triage',
    nameMessage: {
      defaultMessage: 'Incident triage',
      id: 'workflowActivityVNext.new.templateName.incidentTriage',
    },
    version: '2026.08.1',
    yaml: `name: incident_triage
description: Classify an incident and prepare a reviewed response.
roles:
  - id: responder
    name: Incident responder
    system_prompt: Review incident reports carefully.
steps:
  - id: classify
    type: llm_call
    target_role: responder
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
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return `${slug || 'workflow'}.yaml`;
}

export function resolveAvailableWorkflowFileName(
  name: string,
  directoryId: string,
  drafts: readonly {
    readonly directoryId: string;
    readonly fileName: string;
  }[],
): string {
  const preferred = slugifyWorkflowFileName(name);
  const stem = preferred.slice(0, -'.yaml'.length);
  const occupied = new Set(
    drafts
      .filter((draft) => draft.directoryId === directoryId)
      .map((draft) => draft.fileName.trim().toLowerCase()),
  );

  if (!occupied.has(preferred.toLowerCase())) return preferred;

  let suffix = 2;
  while (occupied.has(`${stem}-${suffix}.yaml`.toLowerCase())) suffix += 1;
  return `${stem}-${suffix}.yaml`;
}

export function createBlankWorkflowYaml(name: string): string {
  const documentName =
    name
      .trim()
      .replace(/[^A-Za-z0-9_]+/g, '_')
      .replace(/^_+|_+$/g, '') || 'workflow';
  return `name: ${documentName}\ndescription: \nroles: []\nsteps: []\n`;
}

export function hasBlockingFindings(
  document: StudioWorkflowDocument | null | undefined,
  findings: readonly {
    readonly level?: string | number;
    readonly message: string;
  }[],
): boolean {
  if (!document) return true;
  return findings.some((finding) => {
    const level = String(finding.level ?? '').toLowerCase();
    return (
      level === 'error' || level === 'fatal' || level === '2' || level === '3'
    );
  });
}
