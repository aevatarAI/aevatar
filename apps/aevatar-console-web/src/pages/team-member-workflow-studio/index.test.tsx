import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import {
  cleanupTestQueryClients,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import { history } from "@/shared/navigation/history";
import TeamMemberWorkflowStudioPage from "./index";
import { studioApi } from "@/shared/studio/api";

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: (props: {
    nodes?: Array<{ id?: string; position?: { x: number; y: number } }>;
    onCanvasSelect?: () => void;
    onConnectNodes?: (sourceNodeId: string, targetNodeId: string) => void;
    onNodeLayoutChange?: (
      nodes: Array<{ id?: string; position?: { x: number; y: number } }>,
    ) => void;
    onNodeSelect?: (nodeId: string) => void;
    overlayContent?: unknown;
  }) => {
    const React = require("react");
    return React.createElement(
      "div",
      { "data-testid": "graph-canvas" },
      React.createElement(
        "span",
        { key: "count" },
        `nodes:${props.nodes?.length ?? 0}`,
      ),
      props.nodes?.map((node) =>
        React.createElement(
          "button",
          {
            key: node.id,
            onClick: () => props.onNodeSelect?.(String(node.id ?? "")),
            type: "button",
          },
          `node:${node.id}`,
        ),
      ),
      React.createElement(
        "button",
        {
          key: "canvas",
          onClick: () => props.onCanvasSelect?.(),
          type: "button",
        },
        "canvas",
      ),
      React.createElement(
        "button",
        {
          key: "connect",
          onClick: () => {
            const [source, target] = props.nodes ?? [];
            if (source?.id && target?.id) {
              props.onConnectNodes?.(source.id, target.id);
            }
          },
          type: "button",
        },
        "connect first two nodes",
      ),
      React.createElement(
        "button",
        {
          key: "move",
          onClick: () => {
            props.onNodeLayoutChange?.(
              (props.nodes ?? []).map((node, index) => ({
                ...node,
                position:
                  index === 0
                    ? { x: 900, y: 320 }
                    : node.position ?? { x: 0, y: 0 },
              })),
            );
          },
          type: "button",
        },
        "move first node",
      ),
      props.overlayContent,
    );
  },
}));

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    bindMemberWorkflow: jest.fn(),
    getMember: jest.fn(),
    getTeam: jest.fn(),
    getExecution: jest.fn(),
    getMemberBindingRun: jest.fn(),
    getWorkspaceSettings: jest.fn(),
    getWorkflow: jest.fn(),
    listExecutions: jest.fn(),
    parseYaml: jest.fn(),
    saveWorkflow: jest.fn(),
    serializeYaml: jest.fn(),
    setTeamEntryMember: jest.fn(),
    startExecution: jest.fn(),
  },
}));

const mockWorkflowDocument = {
  name: "Support workflow",
  roles: [
    {
      id: "assistant",
      name: "Assistant",
      systemPrompt: "Help.",
      provider: "openai",
      model: "gpt-test",
      connectors: [],
    },
  ],
  steps: [
    {
      id: "triage",
      type: "llm_call",
      targetRole: "assistant",
      parameters: { prompt_prefix: "Triage the request" },
      next: null,
      branches: {},
    },
  ],
};

function mockTeam() {
  (studioApi.getTeam as jest.Mock).mockResolvedValue({
    createdAt: "2026-06-08T00:00:00Z",
    description: "",
    displayName: "Support Team",
    entryMemberId: null,
    lifecycleStage: "active",
    memberCount: 1,
    scopeId: "scope-1",
    teamId: "t-alpha",
    updatedAt: "2026-06-08T00:00:00Z",
  });
    (studioApi.getWorkspaceSettings as jest.Mock).mockResolvedValue({
      directories: [],
      runtimeBaseUrl: "https://runtime.example.test",
    });
  (studioApi.listExecutions as jest.Mock).mockResolvedValue([]);
}

function mockSerializeYaml() {
  (studioApi.serializeYaml as jest.Mock).mockImplementation(
    async ({ document }) => ({
      document,
      findings: [],
      yaml: `name: ${document.name}\nsteps:\n${(document.steps ?? [])
        .map((step: { id?: string; type?: string }) => `  - id: ${step.id}\n    type: ${step.type}`)
        .join("\n")}`,
    }),
  );
}

describe("TeamMemberWorkflowStudioPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.replaceState({}, "", "/");
    mockTeam();
    mockSerializeYaml();
  });

  afterEach(() => {
    cleanupTestQueryClients();
  });

  it("renders a blank new workflow member editor without backend creation", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    expect(screen.getByText("Add first step")).toBeTruthy();
    expect(screen.getByText("nodes:0")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(studioApi.getMember).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
  });

  it("opens the node library and inserts a first step into a new local draft", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    expect(await screen.findByText("Node library")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Search nodes"), {
      target: { value: "llm" },
    });
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert Llm Call node" }),
    );

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
  });

  it("warns before leaving with unsaved workflow changes", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/new/workflow",
    );
    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockImplementation(() => false);
    const addEventListenerSpy = jest.spyOn(window, "addEventListener");
    const historyPushSpy = jest.spyOn(history, "push").mockImplementation(jest.fn());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert Llm Call node" }),
    );

    await waitFor(() => {
      expect(addEventListenerSpy).toHaveBeenCalledWith(
        "beforeunload",
        expect.any(Function),
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Team" }));
    expect(confirmSpy).toHaveBeenCalledWith(
      "You have unsaved workflow changes. Leave this editor and discard them?",
    );
    expect(historyPushSpy).not.toHaveBeenCalled();

    confirmSpy.mockRestore();
    historyPushSpy.mockRestore();
    addEventListenerSpy.mockRestore();
  });

  it("renders an existing workflow member graph from a stable workflow ref", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Workflow Alpha")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("workflow-alpha", "scope-1");
  });

  it("shows a recoverable state when no stable workflow ref exists", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("saves existing workflow drafts without activation or execution calls", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      workflowId: "workflow-alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const saveButton = await screen.findByRole("button", { name: "Save" });
    await waitFor(() => {
      expect(saveButton).toBeDisabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Add node" }));
    fireEvent.click(await screen.findByRole("button", { name: "Insert Guard node" }));
    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            name: "Workflow Alpha",
            steps: expect.arrayContaining([
              expect.objectContaining({ id: "triage", type: "llm_call" }),
              expect.objectContaining({ type: "guard" }),
            ]),
          }),
        }),
      );
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          directoryId: "scope:scope-1",
          scopeId: "scope-1",
          workflowId: "workflow-alpha",
          workflowName: "Workflow Alpha",
        }),
      );
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
  });

  it("supports selecting, deleting, connecting, and moving nodes before save", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      workflowId: "workflow-alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    expect(screen.getByRole("button", { name: "Delete node" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Delete node" }));
    await waitFor(() => {
      expect(screen.getByText("nodes:0")).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "Add node" }));
    fireEvent.click(await screen.findByRole("button", { name: "Insert Llm Call node" }));
    fireEvent.click(screen.getByRole("button", { name: "Add node" }));
    fireEvent.click(await screen.findByRole("button", { name: "Insert Guard node" }));
    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "connect first two nodes" }));
    fireEvent.click(screen.getByRole("button", { name: "move first node" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({ next: "guard" }),
            ]),
          }),
        }),
      );
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          layout: expect.objectContaining({
            nodePositions: expect.objectContaining({
              llm_call: { x: 900, y: 320 },
            }),
          }),
        }),
      );
    });
  });

  it("opens node detail and applies parameter edits into the workflow document", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      workflowId: "workflow-alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    expect(screen.getByLabelText("Node detail")).toBeTruthy();
    expect(screen.getByText("Input")).toBeTruthy();
    expect(screen.getByText("Parameters")).toBeTruthy();
    expect(screen.getByText("Output")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Node parameters"), {
      target: {
        value: '{\n  "prompt_prefix": "Updated instruction"\n}',
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "triage",
                parameters: expect.objectContaining({
                  prompt_prefix: "Updated instruction",
                }),
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("shows a node detail error for invalid parameter JSON", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    fireEvent.change(screen.getByLabelText("Node parameters"), {
      target: { value: "not-json" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(await screen.findByText(/Unexpected token/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("executes the current draft as a whole workflow and shows returned logs", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.startExecution as jest.Mock).mockResolvedValue({
      actorId: "actor-1",
      completedAtUtc: "2026-06-08T00:00:02Z",
      error: null,
      executionId: "execution-1",
      frames: [
        {
          receivedAtUtc: "2026-06-08T00:00:01Z",
          payload: JSON.stringify({
            custom: {
              name: "aevatar.run.context",
              payload: { workflowName: "Workflow Alpha" },
            },
          }),
        },
      ],
      output: "Workflow complete",
      prompt: "Run the workflow",
      startedAtUtc: "2026-06-08T00:00:00Z",
      status: "succeeded",
      workflowName: "Workflow Alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const executeButton = await screen.findByRole("button", {
      name: "Execute workflow",
    });
    expect(executeButton).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Execution prompt"), {
      target: { value: "Run the workflow" },
    });
    await waitFor(() => {
      expect(executeButton).toBeEnabled();
    });
    fireEvent.click(executeButton);

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            name: "Workflow Alpha",
            steps: expect.arrayContaining([
              expect.objectContaining({ id: "triage", type: "llm_call" }),
            ]),
          }),
        }),
      );
      expect(studioApi.startExecution).toHaveBeenCalledWith(
        expect.objectContaining({
          prompt: "Run the workflow",
          runtimeBaseUrl: "https://runtime.example.test",
          scopeId: "scope-1",
          workflowId: "workflow-alpha",
          workflowName: "Workflow Alpha",
          workflowYamls: [expect.stringContaining("name: Workflow Alpha")],
        }),
      );
    });
    expect(await screen.findByText("Workflow complete")).toBeTruthy();
    expect(screen.getByText("Run started")).toBeTruthy();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("activates an existing workflow member through save, bind, and binding-run observation only", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "build_ready",
        memberId: "member-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:01Z",
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-1",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-1",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "succeeded",
      stateVersion: 2,
      updatedAt: "2026-06-08T00:00:02Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha Published" },
    });
    fireEvent.click(screen.getByRole("switch", { name: "Activate workflow member" }));

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          workflowId: "workflow-alpha",
          workflowName: "Workflow Alpha Published",
        }),
      );
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: "Workflow Alpha Published",
        memberId: "member-alpha",
        scopeId: "scope-1",
        workflowYamls: [expect.stringContaining("name: Workflow Alpha Published")],
      });
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        "scope-1",
        "member-alpha",
        "binding-run-1",
      );
    });
    expect(screen.getByText("Active member: workflow is published and serviceable.")).toBeTruthy();
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("sets Team entry only from the explicit More menu action", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "member-alpha",
        publishedServiceId: "service-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.setTeamEntryMember as jest.Mock).mockResolvedValue({
      createdAt: "2026-06-08T00:00:00Z",
      description: "",
      displayName: "Support Team",
      entryMemberId: "member-alpha",
      lifecycleStage: "active",
      memberCount: 1,
      scopeId: "scope-1",
      teamId: "t-alpha",
      updatedAt: "2026-06-08T00:00:03Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "More workflow actions" }));
    fireEvent.click(await screen.findByText("Set as Team entry"));

    await waitFor(() => {
      expect(studioApi.setTeamEntryMember).toHaveBeenCalledWith(
        "scope-1",
        "t-alpha",
        "member-alpha",
      );
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("shows only safely scoped execution history and opens execution details", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "member-alpha",
        publishedServiceId: "service-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.listExecutions as jest.Mock).mockResolvedValue([
      {
        actorId: "actor-1",
        completedAtUtc: "2026-06-08T00:00:02Z",
        error: null,
        executionId: "execution-matching",
        prompt: "Run scoped",
        serviceId: "",
        startedAtUtc: "2026-06-08T00:00:00Z",
        status: "succeeded",
        workflowId: "workflow-alpha",
        workflowName: "Workflow Alpha",
      },
      {
        actorId: "actor-2",
        completedAtUtc: "2026-06-08T00:00:04Z",
        error: null,
        executionId: "execution-unrelated",
        prompt: "Run same name",
        serviceId: "",
        startedAtUtc: "2026-06-08T00:00:03Z",
        status: "succeeded",
        workflowName: "Workflow Alpha",
      },
    ]);
    (studioApi.getExecution as jest.Mock).mockResolvedValue({
      actorId: "actor-1",
      completedAtUtc: "2026-06-08T00:00:02Z",
      error: null,
      executionId: "execution-matching",
      frames: [],
      output: "Historical output",
      prompt: "Run scoped",
      serviceId: "",
      startedAtUtc: "2026-06-08T00:00:00Z",
      status: "succeeded",
      workflowName: "Workflow Alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByText("Executions"));
    expect(await screen.findByText("execution-matching")).toBeTruthy();
    expect(screen.queryByText("execution-unrelated")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Inspect" }));

    await waitFor(() => {
      expect(studioApi.getExecution).toHaveBeenCalledWith("execution-matching");
    });
    expect(await screen.findByText("Historical output")).toBeTruthy();
  });
});
