import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import React from "react";
import {
  cleanupTestQueryClients,
  createTestQueryClient,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { history } from "@/shared/navigation/history";
import TeamMemberWorkflowStudioPage from "./index";
import { studioApi } from "@/shared/studio/api";

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: (props: {
    nodes?: Array<{ id?: string; position?: { x: number; y: number } }>;
    edges?: Array<{ id?: string }>;
    onCanvasSelect?: () => void;
    onConnectNodes?: (sourceNodeId: string, targetNodeId: string) => void;
    onEdgeSelect?: (edgeId: string) => void;
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
      props.edges?.map((edge) =>
        React.createElement(
          "button",
          {
            key: edge.id,
            onClick: () => props.onEdgeSelect?.(String(edge.id ?? "")),
            type: "button",
          },
          `edge:${edge.id}`,
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
    createMember: jest.fn(),
    updateMemberTeamAssignment: jest.fn(),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(),
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

const mockBranchingWorkflowDocument = {
  ...mockWorkflowDocument,
  steps: [
    {
      id: "triage",
      type: "llm_call",
      targetRole: "assistant",
      parameters: { prompt_prefix: "Triage the request" },
      next: "guard",
      branches: { urgent: "guard" },
    },
    {
      id: "guard",
      type: "guard",
      targetRole: "",
      parameters: { check: "not_empty", on_fail: "fail" },
      next: null,
      branches: {},
    },
  ],
};

function createSseResponse(): Response {
  return {} as Response;
}

async function* createWorkflowInvokeEvents() {
  yield {
    actorId: "actor-1",
    runId: "run-1",
    threadId: "actor-1",
    timestamp: Date.parse("2026-06-08T00:00:00Z"),
    type: "RUN_STARTED",
  };
  yield {
    name: "aevatar.run.context",
    payload: {
      actorId: "actor-1",
      workflowName: "Workflow Alpha",
    },
    timestamp: Date.parse("2026-06-08T00:00:00Z"),
    type: "CUSTOM",
  };
  yield {
    name: "aevatar.step.request",
    payload: {
      input: "Run the workflow",
      stepId: "triage",
      stepType: "llm_call",
    },
    timestamp: Date.parse("2026-06-08T00:00:01Z"),
    type: "CUSTOM",
  };
  yield {
    name: "aevatar.step.completed",
    payload: {
      output: "Workflow complete",
      stepId: "triage",
      success: true,
    },
    timestamp: Date.parse("2026-06-08T00:00:02Z"),
    type: "CUSTOM",
  };
  yield {
    result: {
      output: "Workflow complete",
    },
    runId: "run-1",
    timestamp: Date.parse("2026-06-08T00:00:02Z"),
    type: "RUN_FINISHED",
  };
}

async function* createFailedWorkflowInvokeEvents() {
  yield {
    actorId: "actor-1",
    runId: "run-failed",
    threadId: "actor-1",
    timestamp: Date.parse("2026-06-08T00:00:00Z"),
    type: "RUN_STARTED",
  };
  yield {
    message: "Authenticated member does not match requested member.",
    runId: "run-failed",
    timestamp: Date.parse("2026-06-08T00:00:01Z"),
    type: "RUN_ERROR",
  };
}

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
    directories: [
      {
        directoryId: "scope:scope-1",
        isBuiltIn: true,
        label: "scope-1",
        path: "scope://scope-1",
      },
    ],
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

async function flushAsyncWork() {
  for (let index = 0; index < 5; index += 1) {
    await Promise.resolve();
  }
}

function createPointerDragEvent(
  type: "pointerdown" | "pointermove" | "pointerup",
  clientX: number,
): Event {
  const event = new MouseEvent(type, {
    bubbles: true,
    cancelable: true,
    clientX,
  });
  Object.defineProperty(event, "pointerId", { value: 7 });
  return event;
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

  it("renders a blank new workflow member editor before backend creation", async () => {
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

  it("opens the node library, inserts a first step, and saves a new linked workflow member", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/new/workflow",
    );
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member.yaml",
      filePath: "scope://scope-1/untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "untitled-member",
      yaml: "name: Untitled member\nsteps: []\n",
      document: {
        name: "Untitled member",
        roles: [],
        steps: [
          {
            id: "llm_call",
            type: "llm_call",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:01Z",
    });
    (studioApi.updateMemberTeamAssignment as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "untitled-member",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "untitled-member",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-new",
      memberId: "untitled-member",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "untitled-member",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "untitled-member",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member.yaml",
      filePath: "scope://scope-1/untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "untitled-member",
      yaml: "name: Untitled member\nsteps: []\n",
      document: {
        name: "Untitled member",
        roles: [],
        steps: [
          {
            id: "llm_call",
            type: "llm_call",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:01Z",
    });

    const queryClient = createTestQueryClient();
    const invalidateQueriesSpy = jest.spyOn(queryClient, "invalidateQueries");

    renderWithQueryClient(
      React.createElement(TeamMemberWorkflowStudioPage),
      queryClient,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    expect(await screen.findByText("Node library")).toBeTruthy();
    expect(screen.getByTestId("node-library-layer")).toHaveStyle({
      position: "absolute",
    });
    expect(screen.getByLabelText("Node library")).toHaveStyle({
      position: "absolute",
    });
    fireEvent.change(screen.getByLabelText("Search nodes"), {
      target: { value: "llm" },
    });
    expect(await screen.findByText("LLM call")).toBeTruthy();
    expect(screen.queryByText("llm_call")).toBeNull();
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(screen.getByRole("button", { name: "node:step:llm_step" })).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    const saveButton = screen.getByRole("button", { name: "Save" });
    expect(saveButton).toBeEnabled();
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          directoryId: "scope:scope-1",
          scopeId: "scope-1",
          workflowName: "Untitled member",
          workflowId: "",
        }),
      );
      expect(studioApi.createMember).not.toHaveBeenCalled();
      expect(studioApi.updateMemberTeamAssignment).toHaveBeenCalledWith({
        memberId: "untitled-member",
        scopeId: "scope-1",
        teamId: "t-alpha",
      });
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: "Untitled member",
        memberId: "untitled-member",
        scopeId: "scope-1",
        workflowYamls: [expect.stringContaining("name: Untitled member")],
      });
    });
    expect(window.location.pathname).toBe(
      "/teams/scope-1/t-alpha/members/untitled-member/workflow",
    );
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ["team-member-workflow-studio", "team", "scope-1", "t-alpha"],
    });
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ["teams", "team-members", "scope-1", "t-alpha"],
    });
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ["teams", "team-summary", "scope-1", "t-alpha"],
    });
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ["teams", "members", "scope-1"],
    });
    expect(invalidateQueriesSpy).toHaveBeenCalledWith({
      queryKey: ["teams", "roster", "scope-1"],
    });
  });

  it("reloads route state after creating a workflow member", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/scope-1/t-alpha/members/new/workflow",
    );
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member.yaml",
      filePath: "scope://scope-1/untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "untitled-member",
      yaml: "name: Untitled member\nsteps: []\n",
      document: {
        name: "Untitled member",
        roles: [],
        steps: [
          {
            id: "llm_call",
            type: "llm_call",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:01Z",
    });
    const createdMemberDetail = {
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "untitled-member",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "untitled-member",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    };
    (studioApi.updateMemberTeamAssignment as jest.Mock).mockResolvedValue(
      createdMemberDetail,
    );
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-new",
      memberId: "untitled-member",
      scopeId: "scope-1",
      status: "accepted",
    });
    const createdWorkflow = {
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member.yaml",
      filePath: "scope://scope-1/untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "untitled-member",
      yaml: "name: Untitled member\nsteps: []\n",
      document: {
        name: "Untitled member",
        roles: [],
        steps: [
          {
            id: "llm_call",
            type: "llm_call",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:01Z",
    };
    (studioApi.getMember as jest.Mock).mockImplementation(
      async (_scopeId: string, memberId: string) =>
        memberId === "untitled-member" ? createdMemberDetail : undefined,
    );
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async (workflowId: string) =>
        workflowId === "untitled-member" ? createdWorkflow : undefined,
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe(
        "/teams/scope-1/t-alpha/members/untitled-member/workflow",
      );
      expect(studioApi.getMember).toHaveBeenCalledWith(
        "scope-1",
        "untitled-member",
      );
      expect(studioApi.getWorkflow).toHaveBeenCalledWith(
        "untitled-member",
        "scope-1",
      );
    });
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
      await screen.findByRole("button", { name: "Insert LLM call node" }),
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

  it("saves existing workflow drafts without publishing or execution calls", async () => {
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
    fireEvent.click(await screen.findByRole("button", { name: "Insert LLM call node" }));
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
              expect.objectContaining({ next: "guard_step" }),
            ]),
          }),
        }),
      );
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          layout: expect.objectContaining({
            nodePositions: expect.objectContaining({
              llm_step: { x: 900, y: 320 },
            }),
          }),
        }),
      );
    });
  });

  it("deletes a selected connection without deleting either node", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "triage",
            type: "llm_call",
            targetRole: "assistant",
            parameters: {},
            next: "publish",
            branches: {},
          },
          {
            id: "publish",
            type: "emit",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      workflowId: "workflow-alpha",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });
    fireEvent.click(
      screen.getByRole("button", { name: "edge:edge:triage:publish:linear" }),
    );
    expect(
      screen.getByRole("button", { name: "Delete connection" }),
    ).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Delete connection" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "triage",
                next: null,
              }),
              expect.objectContaining({
                id: "publish",
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("deletes a branch labeled next without touching the linear next connection", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "guard",
            type: "conditional",
            targetRole: null,
            parameters: {},
            next: "linear_target",
            branches: {
              next: "branch_target",
            },
          },
          {
            id: "linear_target",
            type: "emit",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
          {
            id: "branch_target",
            type: "emit",
            targetRole: null,
            parameters: {},
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:3")).toBeTruthy();
    });
    fireEvent.click(
      screen.getByRole("button", {
        name: "edge:edge:guard:branch_target:branch:next",
      }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Delete connection" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                branches: {},
                id: "guard",
                next: "linear_target",
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("opens the floating node inspector and applies guided configuration edits into the workflow document", async () => {
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
    const inspector = screen.getByLabelText("Node inspector");
    expect(inspector).toHaveStyle({
      position: "absolute",
      width: "420px",
    });
    expect(screen.getByLabelText("Resize node inspector")).toBeTruthy();
    expect(within(inspector).getAllByText("triage").length).toBeGreaterThan(0);
    expect(within(inspector).getAllByText("LLM call").length).toBeGreaterThan(0);
    expect(within(inspector).getByText("Basics")).toBeTruthy();
    expect(within(inspector).getByText("Step ID")).toBeTruthy();
    expect(within(inspector).getByText("Type")).toBeTruthy();
    expect(within(inspector).getByText("Target role")).toBeTruthy();
    expect(within(inspector).getByText("assistant")).toBeTruthy();
    expect(screen.queryByText("llm_call")).toBeNull();
    expect(screen.queryByText("Input")).toBeNull();
    expect(within(inspector).getByText("Configuration")).toBeTruthy();
    expect(screen.getByLabelText("Instruction")).toHaveValue("Triage the request");
    expect(screen.queryByText("Parameters")).toBeNull();
    expect(screen.queryByLabelText("Raw node configuration")).toBeNull();
    expect(screen.queryByText(/prompt_prefix/)).toBeNull();
    expect(screen.queryByText("Output")).toBeNull();
    fireEvent.click(screen.getByText("Advanced raw configuration"));
    expect(
      (screen.getByLabelText("Raw node configuration") as HTMLTextAreaElement)
        .value,
    ).toContain('"prompt_prefix": "Triage the request"');

    fireEvent.change(screen.getByLabelText("Instruction"), {
      target: { value: "Updated instruction" },
    });
    expect(
      (screen.getByLabelText("Raw node configuration") as HTMLTextAreaElement)
        .value,
    ).toContain('"prompt_prefix": "Updated instruction"');
    fireEvent.click(screen.getByRole("button", { name: "Update node" }));
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

  it("keeps the node inspector mounted while switching selected nodes", async () => {
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
      document: mockBranchingWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    const inspector = screen.getByLabelText("Node inspector");
    expect(within(inspector).getAllByText("triage").length).toBeGreaterThan(0);
    expect(within(inspector).getAllByText("LLM call").length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole("button", { name: "node:step:guard" }));

    await waitFor(() => {
      expect(screen.getByLabelText("Node inspector")).toBe(inspector);
      expect(within(inspector).getAllByText("Guard").length).toBeGreaterThan(0);
      expect(within(inspector).getByText("No branches")).toBeTruthy();
    });
    expect(within(inspector).queryByText("triage")).toBeNull();
  });

  it("resizes the node inspector with keyboard controls and clamps width", async () => {
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
      document: mockBranchingWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    const inspector = screen.getByLabelText("Node inspector");
    const resizeHandle = screen.getByLabelText("Resize node inspector");
    expect(inspector).toHaveStyle({ width: "420px" });
    expect(resizeHandle).toHaveAttribute("aria-valuemin", "360");
    expect(resizeHandle).toHaveAttribute("aria-valuemax", "500");
    expect(resizeHandle).toHaveAttribute("aria-valuenow", "420");

    for (let index = 0; index < 10; index += 1) {
      fireEvent.keyDown(resizeHandle, { key: "ArrowLeft" });
    }

    expect(inspector).toHaveStyle({ width: "500px" });
    expect(resizeHandle).toHaveAttribute("aria-valuenow", "500");

    for (let index = 0; index < 20; index += 1) {
      fireEvent.keyDown(resizeHandle, { key: "ArrowRight" });
    }

    expect(inspector).toHaveStyle({ width: "360px" });
    expect(resizeHandle).toHaveAttribute("aria-valuenow", "360");
  });

  it("resizes the node inspector with pointer drag and restores page resize styles", async () => {
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
      document: mockBranchingWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });

    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    const inspector = screen.getByLabelText("Node inspector");
    const resizeHandle = screen.getByLabelText("Resize node inspector");
    const setPointerCapture = jest.fn();
    const releasePointerCapture = jest.fn();
    resizeHandle.setPointerCapture = setPointerCapture;
    resizeHandle.releasePointerCapture = releasePointerCapture;
    resizeHandle.hasPointerCapture = jest.fn(() => true);
    document.body.style.cursor = "default";
    document.body.style.userSelect = "text";

    fireEvent(resizeHandle, createPointerDragEvent("pointerdown", 480));

    expect(setPointerCapture).toHaveBeenCalledWith(7);
    expect(document.body.style.cursor).toBe("ew-resize");
    expect(document.body.style.userSelect).toBe("none");

    fireEvent(resizeHandle, createPointerDragEvent("pointermove", 240));

    expect(inspector).toHaveStyle({ width: "500px" });
    expect(resizeHandle).toHaveAttribute("aria-valuenow", "500");

    fireEvent(resizeHandle, createPointerDragEvent("pointerup", 240));

    expect(releasePointerCapture).toHaveBeenCalledWith(7);
    expect(document.body.style.cursor).toBe("default");
    expect(document.body.style.userSelect).toBe("text");
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
    expect(screen.queryByLabelText("Raw node configuration")).toBeNull();
    fireEvent.click(screen.getByText("Advanced raw configuration"));
    fireEvent.change(screen.getByLabelText("Raw node configuration"), {
      target: { value: "not-json" },
    });

    expect(await screen.findByText(/Unexpected token/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Apply raw JSON" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("applies non-LLM guided node configuration without changing parameter value shapes", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "wait_for_signal",
            type: "wait_signal",
            targetRole: null,
            parameters: { signal_name: "continue", timeout_ms: "60000" },
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(
      screen.getByRole("button", { name: "node:step:wait_for_signal" }),
    );
    expect(screen.getAllByText("Wait for signal").length).toBeGreaterThan(0);
    fireEvent.change(screen.getByLabelText("Signal name"), {
      target: { value: "approval-ready" },
    });
    fireEvent.change(screen.getByLabelText("Timeout ms"), {
      target: { value: "90000" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Update node" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "wait_for_signal",
                parameters: expect.objectContaining({
                  signal_name: "approval-ready",
                  timeout_ms: "90000",
                }),
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("shows backend child step ids as product labels in guided cache node configuration", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "cache_response",
            type: "cache",
            targetRole: null,
            parameters: {
              cache_key: "$input",
              child_step_type: "llm_call",
              ttl_seconds: "600",
            },
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:cache_response" }));

    expect(screen.getAllByText("Cache").length).toBeGreaterThan(0);
    expect(screen.getByText("Cached node")).toBeTruthy();
    expect(screen.getByText("LLM call")).toBeTruthy();
    expect(screen.queryByText("llm_call")).toBeNull();
    expect(screen.queryByLabelText("Raw node configuration")).toBeNull();

    fireEvent.change(screen.getByLabelText("TTL seconds"), {
      target: { value: "900" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Update node" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "cache_response",
                parameters: expect.objectContaining({
                  child_step_type: "llm_call",
                  ttl_seconds: "900",
                }),
              }),
            ]),
          }),
        }),
      );
    });

    expect(screen.queryByText("llm_call")).toBeNull();
    fireEvent.click(screen.getByText("Advanced raw configuration"));
    expect(
      (screen.getByLabelText("Raw node configuration") as HTMLTextAreaElement)
        .value,
    ).toContain('"child_step_type": "llm_call"');
  });

  it("infers typed configuration fields for unknown node parameters and writes them through raw JSON", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "custom_step",
            type: "custom_node",
            targetRole: null,
            parameters: {
              enabled: true,
              limit: 3,
              payload: { source: "input" },
              title: "Draft",
            },
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:custom_step" }));

    expect(screen.getByLabelText("Title")).toHaveValue("Draft");
    expect(screen.getByLabelText("Limit")).toHaveValue("3");
    expect(screen.getByRole("switch", { name: "Enabled" })).toHaveAttribute(
      "aria-checked",
      "true",
    );
    expect((screen.getByLabelText("Payload") as HTMLTextAreaElement).value).toContain(
      '"source": "input"',
    );

    fireEvent.click(screen.getByText("Advanced raw configuration"));
    fireEvent.click(screen.getByRole("switch", { name: "Enabled" }));
    fireEvent.change(screen.getByLabelText("Limit"), {
      target: { value: "5" },
    });
    fireEvent.change(screen.getByLabelText("Payload"), {
      target: { value: '{ "source": "updated" }' },
    });
    expect(
      (screen.getByLabelText("Raw node configuration") as HTMLTextAreaElement)
        .value,
    ).toContain('"enabled": false');
    fireEvent.click(screen.getByRole("button", { name: "Update node" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "custom_step",
                parameters: expect.objectContaining({
                  enabled: false,
                  limit: 5,
                  payload: { source: "updated" },
                }),
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("keeps inferred fields visible after optional unknown node values are cleared", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "custom_step",
            type: "custom_node",
            targetRole: null,
            parameters: {
              enabled: true,
              title: "Draft",
            },
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:custom_step" }));
    fireEvent.click(screen.getByText("Advanced raw configuration"));

    fireEvent.change(screen.getByLabelText("Title"), {
      target: { value: "" },
    });

    expect(screen.getByLabelText("Title")).toBeTruthy();
    expect(screen.getByLabelText("Title")).toHaveValue("");
    expect(
      (screen.getByLabelText("Raw node configuration") as HTMLTextAreaElement)
        .value,
    ).not.toContain('"title"');

    fireEvent.click(screen.getByRole("button", { name: "Update node" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            steps: expect.arrayContaining([
              expect.objectContaining({
                id: "custom_step",
                parameters: expect.not.objectContaining({
                  title: expect.anything(),
                }),
              }),
            ]),
          }),
        }),
      );
    });
  });

  it("blocks invalid inferred object edits before they can be applied", async () => {
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
      document: {
        ...mockWorkflowDocument,
        steps: [
          {
            id: "custom_step",
            type: "custom_node",
            targetRole: null,
            parameters: { payload: { source: "input" } },
            next: null,
            branches: {},
          },
        ],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:custom_step" }));
    fireEvent.change(screen.getByLabelText("Payload"), {
      target: { value: "not-json" },
    });

    expect(await screen.findByText(/Payload.*Unexpected token/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Update node" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(studioApi.serializeYaml).not.toHaveBeenCalledWith(
      expect.objectContaining({
        document: expect.objectContaining({
          steps: expect.arrayContaining([
            expect.objectContaining({
              id: "custom_step",
              parameters: expect.objectContaining({ payload: "not-json" }),
            }),
          ]),
        }),
      }),
    );
  });

  it("runs the active workflow member through invoke and shows returned logs", async () => {
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
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue(createSseResponse());
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runActiveMemberButton = await screen.findByRole("button", {
      name: "Run active member",
    });
    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(screen.getByLabelText("Workflow title")).toHaveAttribute(
      "title",
      "Edit workflow name",
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Edit workflow name" }),
    );
    expect(screen.getByLabelText("Workflow title")).toHaveFocus();
    const headerIdentity = screen.getByTestId("workflow-header-identity");
    const headerPrimaryActions = screen.getByTestId(
      "workflow-header-primary-actions",
    );
    const headerTabs = screen.getByTestId("workflow-header-tabs");
    const headerNodeActions = screen.getByTestId(
      "workflow-header-node-actions",
    );
    expect(within(headerIdentity).getByText("Published")).toBeTruthy();
    expect(
      within(headerIdentity).getByRole("button", { name: "Edit workflow name" }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).queryByRole("button", {
        name: "Publish member",
      }),
    ).toBeNull();
    expect(
      within(headerPrimaryActions).getByRole("button", {
        name: "Run active member",
      }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).getByRole("button", { name: "Add node" }),
    ).toBeTruthy();
    expect(within(headerTabs).getByText("Editor")).toBeTruthy();
    expect(within(headerTabs).getByText("Runs")).toBeTruthy();
    expect(
      within(headerNodeActions).getByRole("button", { name: "Delete node" }),
    ).toBeTruthy();
    expect(
      within(headerNodeActions).getByRole("button", { name: "Save" }),
    ).toBeTruthy();
    expect(
      within(headerNodeActions).queryByRole("button", {
        name: "More workflow actions",
      }),
    ).toBeNull();
    expect(screen.queryByText("Set as Team entry")).toBeNull();
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(screen.queryByText("Workflow member")).toBeNull();
    expect(screen.queryByText("Draft workflow member")).toBeNull();
    expect(screen.queryByTestId("member-run-result-panel")).toBeNull();
    expect(screen.queryByText(/execution has been started/i)).toBeNull();
    expect(
      screen.queryByLabelText("Member run console"),
    ).toBeNull();
    expect(screen.queryByLabelText("Message to active member")).toBeNull();
    expect(
      screen.queryByLabelText("Run options panel"),
    ).toBeNull();
    expect(
      screen.getByRole("button", { name: "Run options" }),
    ).toBeTruthy();
    await waitFor(() => {
      expect(runActiveMemberButton).toBeEnabled();
    });
    fireEvent.click(screen.getByTestId("workflow-run-options-button"));
    expect(await screen.findByLabelText("Run options panel")).toHaveStyle({
      borderLeft: "1px solid #e5e7eb",
    });
    fireEvent.change(screen.getByLabelText("Message to active member"), {
      target: { value: "Run the workflow" },
    });
    expect(screen.getByLabelText("Message to active member")).toHaveValue(
      "Run the workflow",
    );
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    fireEvent.click(runActiveMemberButton);
    const resultPanel = await screen.findByTestId("member-run-result-panel");
    const consolePanel = screen.getByLabelText("Member run console");
    const consoleResizeHandle = screen.getByRole("separator", {
      name: "Resize run console",
    });
    expect(consoleResizeHandle).toHaveAttribute("aria-orientation", "horizontal");
    expect(consolePanel).toHaveStyle({ flex: "0 0 210px" });

    fireEvent.mouseDown(consoleResizeHandle, { clientY: 700 });
    fireEvent.mouseMove(window, { clientY: 600 });
    fireEvent.mouseUp(window);
    await waitFor(() => {
      expect(consolePanel).toHaveStyle({ flex: "0 0 310px" });
      expect(consoleResizeHandle).toHaveAttribute("aria-valuenow", "310");
    });

    fireEvent.keyDown(consoleResizeHandle, { key: "ArrowDown" });
    await waitFor(() => {
      expect(consolePanel).toHaveStyle({ flex: "0 0 286px" });
    });

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-1",
        {
          metadata: {
            workflowId: "workflow-alpha",
          },
          prompt: "Run the workflow",
        },
        expect.any(AbortSignal),
        {
          memberId: "member-alpha",
        },
      );
    });
    await waitFor(() => {
      expect(screen.getAllByText("Workflow complete").length).toBeGreaterThan(0);
    });
    expect(resultPanel).toHaveTextContent("Workflow complete");
    expect(screen.getByText("triage started")).toBeTruthy();
    expect(screen.getByText("triage completed")).toBeTruthy();
    expect(screen.getByText("Run started")).toBeTruthy();
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(screen.queryByText("run-1")).toBeNull();
    expect(resultPanel).not.toHaveTextContent("Member run");
    expect(studioApi.startExecution).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("keeps failed member run errors in the result panel while run message stays collapsed", async () => {
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
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue(createSseResponse());
    (parseBackendSSEStream as jest.Mock).mockReturnValue(
      createFailedWorkflowInvokeEvents(),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runActiveMemberButton = await screen.findByRole("button", {
      name: "Run active member",
    });
    await waitFor(() => {
      expect(runActiveMemberButton).toBeEnabled();
    });
    fireEvent.click(runActiveMemberButton);

    const resultPanel = await screen.findByTestId(
      "member-run-result-panel",
    );
    await waitFor(() => {
      expect(resultPanel).toHaveTextContent(
        "Authenticated member does not match requested member.",
      );
    });
    expect(resultPanel).toHaveTextContent("failed");
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(resultPanel).not.toHaveTextContent("Member run");
    expect(screen.queryByLabelText("Message to active member")).toBeNull();
    expect(
      screen.queryByLabelText("Run options panel"),
    ).toBeNull();
  });

  it("keeps Run active member disabled until the workflow member is active", async () => {
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

    const runActiveMemberButton = await screen.findByRole("button", {
      name: "Run active member",
    });
    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    await waitFor(() => {
      expect(runActiveMemberButton).toBeDisabled();
    });
    expect(runActiveMemberButton).toHaveAttribute(
      "title",
      "Publish this workflow member before running it.",
    );
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it("can run an active published member even when the editor workflow ref is missing", async () => {
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
        lifecycleStage: "bind_ready",
        memberId: "member-alpha",
        publishedServiceId: "service-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (runtimeRunsApi.streamChat as jest.Mock).mockResolvedValue(createSseResponse());
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runActiveMemberButton = await screen.findByRole("button", {
      name: "Run active member",
    });
    await waitFor(() => {
      expect(runActiveMemberButton).toBeEnabled();
    });
    fireEvent.click(runActiveMemberButton);

    await waitFor(() => {
      expect(runtimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-1",
        {
          metadata: undefined,
          prompt: "Run Workflow Alpha",
        },
        expect.any(AbortSignal),
        {
          memberId: "member-alpha",
        },
      );
    });
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("publishes an existing workflow member through save, bind, and binding-run observation only", async () => {
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
    expect(screen.queryByRole("switch")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Publish member" }));

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
    await waitFor(() => {
      expect(screen.getByText("Published")).toBeTruthy();
      expect(screen.getByTitle(/Published member workflow is serviceable/)).toBeTruthy();
    });
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("blocks publish when selected node configuration is invalid", async () => {
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

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha Published" },
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    fireEvent.change(screen.getByLabelText("Instruction"), {
      target: { value: "" },
    });

    expect(await screen.findByText("Instruction is required.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Publish member" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Publish member" }));

    await flushAsyncWork();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("reports rejected publish binding without introducing activation language", async () => {
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
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-rejected",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-rejected",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "rejected",
      stateVersion: 2,
      updatedAt: "2026-06-08T00:00:02Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    fireEvent.click(screen.getByRole("button", { name: "Publish member" }));

    await waitFor(() => {
      expect(screen.getByText("Error")).toBeTruthy();
      expect(
        screen.getByTitle(
          /Publish binding request was rejected by the member authority/,
        ),
      ).toBeTruthy();
    });
    expect(screen.queryByTitle(/Activation/)).toBeNull();
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("stops local publish loading when binding remains in progress after the observation window", async () => {
    jest.useFakeTimers();
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
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-pending",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-pending",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
      stateVersion: 1,
      updatedAt: "2026-06-08T00:00:02Z",
    });

    try {
      renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

      await waitFor(() => {
        expect(screen.getByText("nodes:1")).toBeTruthy();
      });
      fireEvent.click(screen.getByRole("button", { name: "Publish member" }));

      await waitFor(() => {
        expect(screen.getByText("Publishing")).toBeTruthy();
      });
      for (let index = 0; index < 8; index += 1) {
        await act(async () => {
          jest.advanceTimersByTime(900);
          await flushAsyncWork();
        });
      }

      await waitFor(() => {
        expect(screen.queryByText("Publishing")).toBeNull();
        expect(screen.getByText("Binding")).toBeTruthy();
      });
      expect(screen.getByRole("button", { name: "Publish member" })).toBeDisabled();
      expect(
        screen.getAllByTitle(/Publish was accepted and binding is still in progress/).length,
      ).toBeGreaterThan(0);
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledTimes(8);
    } finally {
      jest.useRealTimers();
    }
  });

  it("allows publishing a new draft version for an already published workflow member", async () => {
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
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha v2",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha v2\nsteps: []\n",
      document: {
        ...mockWorkflowDocument,
        name: "Workflow Alpha v2",
      },
      updatedAtUtc: "2026-06-08T00:00:01Z",
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-2",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-2",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "succeeded",
      stateVersion: 3,
      updatedAt: "2026-06-08T00:00:02Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
      expect(screen.getByText("Published")).toBeTruthy();
      expect(
        screen.queryByRole("button", { name: "Publish member" }),
      ).toBeNull();
    });
    expect(screen.getByDisplayValue("Workflow Alpha")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha v2" },
    });

    const publishButton = await screen.findByRole("button", {
      name: "Publish member",
    });
    await waitFor(() => {
      expect(publishButton).toBeEnabled();
    });
    fireEvent.click(publishButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: "Workflow Alpha v2",
        }),
      );
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: "Workflow Alpha v2",
        memberId: "member-alpha",
        scopeId: "scope-1",
        workflowYamls: [expect.stringContaining("name: Workflow Alpha v2")],
      });
    });
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("does not expose Team entry actions in workflow studio", async () => {
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
    expect(
      screen.queryByRole("button", { name: "More workflow actions" }),
    ).toBeNull();
    expect(screen.queryByText("Set as Team entry")).toBeNull();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("shows only linked member run history and opens run details", async () => {
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

    fireEvent.click(await screen.findByText("Runs"));
    expect(await screen.findByLabelText("Member runs")).toBeTruthy();
    expect(await screen.findByText("Input: Run scoped")).toBeTruthy();
    expect(screen.queryByText("execution-matching")).toBeNull();
    expect(screen.queryByText("execution-unrelated")).toBeNull();
    expect(screen.queryByText("Input: Run same name")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Open run" }));

    await waitFor(() => {
      expect(studioApi.getExecution).toHaveBeenCalledWith("execution-matching");
    });
    expect(await screen.findByText("Historical output")).toBeTruthy();
  });

  it("does not request global run history without a stable member run owner", async () => {
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

    fireEvent.click(await screen.findByText("Runs"));

    expect(
      await screen.findByText(
        "Run history is available after this member has a saved workflow link or active published version.",
      ),
    ).toBeTruthy();
    expect(studioApi.listExecutions).not.toHaveBeenCalled();
  });
});
