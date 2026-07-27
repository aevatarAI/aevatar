import type { StudioWorkflowDocument } from "@/shared/studio/models";
import {
  prepareWorkflowTemplateApplication,
  restoreWorkflowTemplateSnapshot,
  type WorkflowTemplateEditorSnapshot,
} from "./workflowTemplateApplication";

describe("workflow template application", () => {
  const currentDocument: StudioWorkflowDocument = {
    name: "Incident triage",
    roles: [{ id: "existing-role", name: "Existing role" }],
    steps: [{ id: "existing-step", type: "assign" }],
  };
  const currentLayout = {
    nodePositions: { "existing-step": { x: 720, y: 240 } },
    viewport: { x: 24, y: 32, zoom: 0.8 },
  };

  const snapshot: WorkflowTemplateEditorSnapshot = {
    dirty: false,
    document: currentDocument,
    layout: currentLayout,
    selectedEdgeId: "edge:existing-step:review:linear",
    selectedNodeId: "step:existing-step",
    title: "Incident triage",
  };

  it("validates and prepares one title-preserving replacement with a complete undo snapshot", async () => {
    const parseYaml = jest.fn().mockResolvedValue({
      document: {
        name: "Template-owned title",
        roles: [{ id: "template-role", name: "Template role" }],
        steps: [
          { id: "prepare", type: "assign", next: "review" },
          { id: "review", type: "llm_call", targetRole: "template-role" },
        ],
      },
      findings: [],
    });

    const prepared = await prepareWorkflowTemplateApplication({
      parseYaml,
      snapshot,
      templateId: "conditional-routing",
      templateRevision: 3,
      yaml: "name: Template-owned title\nsteps:\n  - id: prepare\n",
    });

    expect(parseYaml).toHaveBeenCalledWith({
      availableStepTypes: expect.arrayContaining(["assign", "llm_call"]),
      yaml: "name: Template-owned title\nsteps:\n  - id: prepare\n",
    });
    expect(prepared.document).toEqual({
      name: "Incident triage",
      roles: [{ id: "template-role", name: "Template role" }],
      steps: [
        { id: "prepare", type: "assign", next: "review" },
        { id: "review", type: "llm_call", targetRole: "template-role" },
      ],
    });
    expect(prepared.layout).toMatchObject({
      entryWorkflow: "Incident triage",
      nodePositions: {
        prepare: expect.any(Object),
        review: expect.any(Object),
      },
    });
    expect(prepared.layout).not.toEqual(currentLayout);
    expect(prepared.selectedEdgeId).toBe("");
    expect(prepared.selectedNodeId).toBe("step:prepare");
    expect(prepared.snapshot).toEqual(snapshot);
    expect(prepared.snapshot).not.toBe(snapshot);
    expect(prepared.snapshot.document).not.toBe(currentDocument);
    expect(prepared.snapshot.layout).not.toBe(currentLayout);
    expect(prepared.templateId).toBe("conditional-routing");
    expect(prepared.templateRevision).toBe(3);
  });

  it("rejects parser, schema, or primitive findings before producing replacement state", async () => {
    const parseYaml = jest.fn().mockResolvedValue({
      document: {
        name: "Broken template",
        steps: [{ id: "unsupported", type: "future_primitive" }],
      },
      findings: [
        {
          code: "unsupported_primitive",
          level: "error",
          message: "Primitive future_primitive is not supported.",
          path: "steps[0].type",
        },
      ],
    });

    await expect(
      prepareWorkflowTemplateApplication({
        parseYaml,
        snapshot,
        templateId: "future-template",
        templateRevision: 1,
        yaml: "name: Broken template",
      }),
    ).rejects.toThrow("Primitive future_primitive is not supported.");

    expect(snapshot).toEqual({
      dirty: false,
      document: currentDocument,
      layout: currentLayout,
      selectedEdgeId: "edge:existing-step:review:linear",
      selectedNodeId: "step:existing-step",
      title: "Incident triage",
    });
  });

  it("restores document, layout, selection, title, and the previous dirty state", () => {
    const restored = restoreWorkflowTemplateSnapshot(snapshot);

    expect(restored).toEqual(snapshot);
    expect(restored).not.toBe(snapshot);
    expect(restored.document).not.toBe(currentDocument);
    expect(restored.layout).not.toBe(currentLayout);
  });
});
