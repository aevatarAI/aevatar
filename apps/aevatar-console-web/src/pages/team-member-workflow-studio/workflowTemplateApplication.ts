import {
  buildStudioGraphElements,
  buildStudioWorkflowLayout,
  STUDIO_GRAPH_CATEGORIES,
} from "@/shared/studio/graph";
import type {
  StudioValidationFinding,
  StudioWorkflowDocument,
} from "@/shared/studio/models";

export type WorkflowTemplateEditorSnapshot = {
  readonly dirty: boolean;
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly selectedEdgeId: string;
  readonly selectedNodeId: string;
  readonly title: string;
};

export type PreparedWorkflowTemplateApplication = {
  readonly document: StudioWorkflowDocument;
  readonly layout: unknown;
  readonly selectedEdgeId: "";
  readonly selectedNodeId: string;
  readonly snapshot: WorkflowTemplateEditorSnapshot;
  readonly templateId: string;
  readonly templateRevision: number;
};

type ParseWorkflowYaml = (input: {
  readonly availableStepTypes: string[];
  readonly yaml: string;
}) => Promise<{
  readonly document?: StudioWorkflowDocument | null;
  readonly findings?: readonly StudioValidationFinding[];
}>;

const AVAILABLE_STEP_TYPES = STUDIO_GRAPH_CATEGORIES.flatMap((category) => [
  ...category.items,
]);

function cloneValue<T>(value: T): T {
  if (value === undefined || value === null) {
    return value;
  }

  return JSON.parse(JSON.stringify(value)) as T;
}

function isBlockingFinding(finding: StudioValidationFinding): boolean {
  if (typeof finding.level === "number") {
    return finding.level >= 2;
  }

  const level = String(finding.level ?? "").trim().toLowerCase();
  return level === "error" || level === "fatal" || level === "2";
}

function describeBlockingFindings(
  findings: readonly StudioValidationFinding[],
): string {
  return findings
    .filter(isBlockingFinding)
    .map((finding) => {
      const path = String(finding.path ?? "").trim();
      return path ? `${path}: ${finding.message}` : finding.message;
    })
    .filter(Boolean)
    .join(" ");
}

export function restoreWorkflowTemplateSnapshot(
  snapshot: WorkflowTemplateEditorSnapshot,
): WorkflowTemplateEditorSnapshot {
  return cloneValue(snapshot);
}

export async function prepareWorkflowTemplateApplication(input: {
  readonly parseYaml: ParseWorkflowYaml;
  readonly snapshot: WorkflowTemplateEditorSnapshot;
  readonly templateId: string;
  readonly templateRevision: number;
  readonly yaml: string;
}): Promise<PreparedWorkflowTemplateApplication> {
  const templateId = input.templateId.trim();
  if (!templateId || !Number.isInteger(input.templateRevision) || input.templateRevision < 1) {
    throw new Error("Choose a specific workflow template revision and try again.");
  }

  if (!input.yaml.trim()) {
    throw new Error("This workflow template does not contain YAML to apply.");
  }

  const snapshot = restoreWorkflowTemplateSnapshot(input.snapshot);
  const parsed = await input.parseYaml({
    availableStepTypes: [...AVAILABLE_STEP_TYPES],
    yaml: input.yaml,
  });
  const findings = parsed.findings ?? [];
  const blockingMessage = describeBlockingFindings(findings);
  if (blockingMessage) {
    throw new Error(blockingMessage);
  }

  if (!parsed.document || typeof parsed.document !== "object") {
    throw new Error("The template YAML did not produce a workflow document.");
  }

  const document: StudioWorkflowDocument = {
    ...cloneValue(parsed.document),
    name: snapshot.title,
  };
  const graph = buildStudioGraphElements(document);
  const layout = buildStudioWorkflowLayout(snapshot.title, graph.nodes);

  return {
    document,
    layout,
    selectedEdgeId: "",
    selectedNodeId: graph.nodes[0]?.id ?? "",
    snapshot,
    templateId,
    templateRevision: input.templateRevision,
  };
}
