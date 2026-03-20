import type { PlaygroundDraft } from './playgroundDraft';

export type PlaygroundDraftStatusTone = 'default' | 'processing' | 'success' | 'warning';

export type PlaygroundDraftAlertType = 'info' | 'success' | 'warning';

export interface PlaygroundDraftStatus {
  hasDraft: boolean;
  lineCount: number;
  label: string;
  summary: string;
  detail: string;
  tagColor: PlaygroundDraftStatusTone;
  alertType: PlaygroundDraftAlertType;
  sourceWorkflow: string;
  updatedAt: string;
  matchesReferenceWorkflow: boolean;
  differsFromReference: boolean | null;
}

export interface YamlLineDiffSummary {
  draftLineCount: number;
  referenceLineCount: number;
  changedLineCount: number;
  addedLineCount: number;
  removedLineCount: number;
}

export function countYamlLines(value: string): number {
  if (!value.trim()) {
    return 0;
  }

  return value.split(/\r?\n/).length;
}

function normalizeYaml(value?: string | null): string {
  return value?.trim() ?? '';
}

export function summarizeYamlLineDiff(
  draftYaml: string,
  referenceYaml: string,
): YamlLineDiffSummary {
  const normalizedDraftYaml = normalizeYaml(draftYaml);
  const normalizedReferenceYaml = normalizeYaml(referenceYaml);
  const draftLines = normalizedDraftYaml ? normalizedDraftYaml.split(/\r?\n/) : [];
  const referenceLines = normalizedReferenceYaml
    ? normalizedReferenceYaml.split(/\r?\n/)
    : [];
  const sharedLength = Math.min(draftLines.length, referenceLines.length);
  let changedLineCount = 0;

  for (let index = 0; index < sharedLength; index += 1) {
    if (draftLines[index] !== referenceLines[index]) {
      changedLineCount += 1;
    }
  }

  return {
    draftLineCount: draftLines.length,
    referenceLineCount: referenceLines.length,
    changedLineCount: changedLineCount + Math.abs(draftLines.length - referenceLines.length),
    addedLineCount: Math.max(draftLines.length - referenceLines.length, 0),
    removedLineCount: Math.max(referenceLines.length - draftLines.length, 0),
  };
}

export function getPlaygroundDraftStatus(
  draft: PlaygroundDraft,
  options?: {
    referenceWorkflow?: string;
    referenceYaml?: string;
  },
): PlaygroundDraftStatus {
  const normalizedDraftYaml = normalizeYaml(draft.yaml);
  const sourceWorkflow = draft.sourceWorkflow.trim();
  const referenceWorkflow = options?.referenceWorkflow?.trim() ?? '';
  const hasDraft = normalizedDraftYaml.length > 0;
  const matchesReferenceWorkflow = Boolean(
    sourceWorkflow && referenceWorkflow && sourceWorkflow === referenceWorkflow,
  );
  const differsFromReference =
    hasDraft && matchesReferenceWorkflow && typeof options?.referenceYaml === 'string'
      ? normalizedDraftYaml !== normalizeYaml(options.referenceYaml)
      : null;

  if (!hasDraft) {
    return {
      hasDraft: false,
      lineCount: 0,
      label: 'No draft saved',
      summary: 'No draft saved',
      detail: referenceWorkflow
        ? `Import ${referenceWorkflow} into Playground to start editing a local draft.`
        : 'Import a workflow into Playground to start editing a local draft.',
      tagColor: 'default',
      alertType: 'info',
      sourceWorkflow,
      updatedAt: draft.updatedAt,
      matchesReferenceWorkflow,
      differsFromReference: null,
    };
  }

  if (matchesReferenceWorkflow) {
    if (differsFromReference === false) {
      return {
        hasDraft: true,
        lineCount: countYamlLines(draft.yaml),
        label: 'Aligned with template',
        summary: `Matches ${referenceWorkflow}`,
        detail: `The current draft still matches ${referenceWorkflow} and can be resumed in Playground without re-importing.`,
        tagColor: 'success',
        alertType: 'success',
        sourceWorkflow,
        updatedAt: draft.updatedAt,
        matchesReferenceWorkflow,
        differsFromReference,
      };
    }

    if (differsFromReference === true) {
      return {
        hasDraft: true,
        lineCount: countYamlLines(draft.yaml),
        label: 'Modified from template',
        summary: `Edited from ${referenceWorkflow}`,
        detail: `The current draft started from ${referenceWorkflow} and now contains local edits that differ from the library YAML.`,
        tagColor: 'warning',
        alertType: 'warning',
        sourceWorkflow,
        updatedAt: draft.updatedAt,
        matchesReferenceWorkflow,
        differsFromReference,
      };
    }

    return {
      hasDraft: true,
      lineCount: countYamlLines(draft.yaml),
      label: 'Linked to template',
      summary: `Based on ${referenceWorkflow}`,
      detail: `The current draft is linked to ${referenceWorkflow}; compare or re-import it from Playground when needed.`,
      tagColor: 'processing',
      alertType: 'info',
      sourceWorkflow,
      updatedAt: draft.updatedAt,
      matchesReferenceWorkflow,
      differsFromReference,
    };
  }

  if (sourceWorkflow) {
    return {
      hasDraft: true,
      lineCount: countYamlLines(draft.yaml),
      label: referenceWorkflow ? 'Linked to another workflow' : 'Workflow-based draft',
      summary: `Based on ${sourceWorkflow}`,
      detail: referenceWorkflow
        ? `The current draft is still based on ${sourceWorkflow}. Import ${referenceWorkflow} into Playground if you want this workflow to replace the local draft.`
        : `The current draft is based on ${sourceWorkflow} and can be resumed in Playground.`,
      tagColor: 'processing',
      alertType: 'info',
      sourceWorkflow,
      updatedAt: draft.updatedAt,
      matchesReferenceWorkflow,
      differsFromReference,
    };
  }

  return {
    hasDraft: true,
    lineCount: countYamlLines(draft.yaml),
    label: 'Inline draft',
    summary: 'Inline local draft',
    detail: referenceWorkflow
      ? `The current draft is not linked to ${referenceWorkflow}; it only exists in local browser storage.`
      : 'The current draft only exists in local browser storage and is not linked to a workflow template.',
    tagColor: 'processing',
    alertType: 'info',
    sourceWorkflow,
    updatedAt: draft.updatedAt,
    matchesReferenceWorkflow,
    differsFromReference,
  };
}
