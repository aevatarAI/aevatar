import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Modal } from "antd";
import React from "react";
import { parseBackendSSEStream } from "@/shared/agui/sseFrameNormalizer";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { scopeRuntimeApi } from "@/shared/api/scopeRuntimeApi";
import { history } from "@/shared/navigation/history";
import { StudioApiError, studioApi } from "@/shared/studio/api";
import {
  cleanupTestQueryClients,
  createTestQueryClient,
  renderWithQueryClient,
} from "../../../tests/reactQueryTestUtils";
import TeamMemberWorkflowStudioPage from "./index";

jest.mock("antd", () => {
  const actual = jest.requireActual("antd");
  return {
    ...actual,
    Modal: Object.assign(actual.Modal, {
      confirm: jest.fn(),
    }),
  };
});

jest.mock("@/shared/graphs/GraphCanvas", () => ({
  __esModule: true,
  default: (props: {
    nodes?: Array<{
      data?: { executionFocused?: boolean; executionStatus?: string };
      id?: string;
      position?: { x: number; y: number };
    }>;
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
            "data-execution-focused": node.data?.executionFocused ? "true" : "false",
            "data-execution-status": node.data?.executionStatus ?? "idle",
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

jest.mock("@/shared/studio/api", () => {
  class MockStudioApiError extends Error {
    readonly code?: string;
    readonly status: number;

    constructor(message: string, status: number, code?: string) {
      super(message);
      this.name = "StudioApiError";
      this.code = code;
      this.status = status;
    }
  }

  return {
    StudioApiError: MockStudioApiError,
    isStudioApiStatus: (error: unknown, status: number) =>
      error instanceof MockStudioApiError && error.status === status,
    studioApi: {
      authorWorkflow: jest.fn(),
      bindMemberWorkflow: jest.fn(),
      getMember: jest.fn(),
      getTeam: jest.fn(),
      getExecution: jest.fn(),
      getMemberBindingRun: jest.fn(),
      getWorkspaceSettings: jest.fn(),
      getWorkflow: jest.fn(),
      listWorkflows: jest.fn(),
      listExecutions: jest.fn(),
      parseYaml: jest.fn(),
      previewExplicitRequests: jest.fn(),
      getPublishedWorkflow: jest.fn(),
      saveAndBindWorkflow: jest.fn(),
      saveWorkflow: jest.fn(),
      serializeYaml: jest.fn(),
      setTeamEntryMember: jest.fn(),
      startExecution: jest.fn(),
      createMember: jest.fn(),
      createMemberWithId: jest.fn(),
      updateMemberDisplayName: jest.fn(),
      updateMemberImplementationRef: jest.fn(),
      updateMemberTeamAssignment: jest.fn(),
    },
  };
});

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    streamChat: jest.fn(),
    streamDraftRun: jest.fn(),
  },
}));

jest.mock("@/shared/api/scopeRuntimeApi", () => ({
  scopeRuntimeApi: {
    listServices: jest.fn(),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(),
}));

function findRenderedStyleText(fragment: string): string {
  return (
    Array.from(document.querySelectorAll("style"))
      .map((styleElement) => styleElement.textContent ?? "")
      .find((styleText) => styleText.includes(fragment)) ?? ""
  );
}

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

type TestRuntimeEvent = Record<string, unknown>;

function createControlledWorkflowInvokeStream() {
  const queuedEvents: TestRuntimeEvent[] = [];
  const waiters: Array<() => void> = [];
  let completed = false;

  const notify = () => {
    waiters.shift()?.();
  };

  return {
    emit(event: TestRuntimeEvent) {
      queuedEvents.push(event);
      notify();
    },
    finish() {
      completed = true;
      notify();
    },
    async *stream() {
      while (!completed || queuedEvents.length > 0) {
        const nextEvent = queuedEvents.shift();
        if (nextEvent) {
          yield nextEvent;
          continue;
        }

        await new Promise<void>((resolve) => {
          waiters.push(resolve);
        });
      }
    },
  };
}

async function* createWorkflowInvokeEvents(input: string = "Run the workflow") {
  yield {
    actorId: "actor-1",
    commandId: "command-1",
    correlationId: "correlation-1",
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
      input,
      stepId: "triage",
      stepType: "llm_call",
      targetRole: "assistant",
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
    name: "aevatar.human_input.request",
    payload: {
      prompt: "Need approval before deployment",
      runId: "run-1",
      stepId: "approve",
      suspensionType: "human_approval",
    },
    timestamp: Date.parse("2026-06-08T00:00:03Z"),
    type: "CUSTOM",
  };
  yield {
    name: "aevatar.usage",
    payload: {
      completionTokens: 24,
      model: "gpt-test",
      promptTokens: 18,
      totalTokens: 42,
    },
    timestamp: Date.parse("2026-06-08T00:00:03Z"),
    type: "CUSTOM",
  };
  yield {
    result: {
      output: "Workflow complete",
    },
    runId: "run-1",
    timestamp: Date.parse("2026-06-08T00:00:04Z"),
    type: "RUN_FINISHED",
  };
  yield {
    snapshot: {
      currentStepId: "triage",
      stateVersion: 7,
      status: "completed",
      values: {
        answer: "Workflow complete",
      },
    },
    timestamp: Date.parse("2026-06-08T00:00:05Z"),
    type: "STATE_SNAPSHOT",
  };
  yield {
    name: "aevatar.observed.raw",
    payload: {
      evidenceId: "raw-observation-1",
      source: "runtime-observer",
    },
    timestamp: Date.parse("2026-06-08T00:00:06Z"),
    type: "CUSTOM",
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

function mockParseYaml() {
  (studioApi.parseYaml as jest.Mock).mockImplementation(
    async ({ yaml }: { yaml: string }) => {
      const name = /(?:^|\n)name:\s*([^\n]+)/.exec(yaml)?.[1]?.trim() ||
        "Workflow Alpha";
      const steps = Array.from(
        yaml.matchAll(/-\s+id:\s*([^\n]+)\n\s+type:\s*([^\n]+)/g),
      ).map((match) => ({
        id: match[1].trim(),
        type: match[2].trim(),
        targetRole: "assistant",
        parameters: {},
        next: null,
        branches: {},
      }));

      return {
        document: {
          name,
          roles: mockWorkflowDocument.roles,
          steps,
        },
        findings: [],
      };
    },
  );
}

async function flushAsyncWork() {
  for (let index = 0; index < 5; index += 1) {
    await Promise.resolve();
  }
}

function clickYamlAction(name: "Edit YAML" = "Edit YAML") {
  fireEvent.click(screen.getByRole("button", { name }));
}

function openMoreActionsMenu() {
  fireEvent.click(screen.getByRole("button", { name: "More workflow actions" }));
}

function closeOpenMenu() {
  fireEvent.keyDown(document, { key: "Escape" });
  fireEvent.click(document.body);
}

function clickMoreAction(
  name:
    | "Delete selected connection"
    | "Delete selected node",
) {
  openMoreActionsMenu();
  fireEvent.click(screen.getByRole("menuitem", { name }));
}

function clickPublishAction() {
  fireEvent.click(screen.getByRole("button", { name: "Publish" }));
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

function mockNewWorkflowMemberCreateFixtures() {
  const createdWorkflow = {
    directoryId: "scope:scope-1",
    directoryLabel: "scope-1",
    draftExists: true,
    fileName: "wf-untitled-member.yaml",
    filePath: "scope://scope-1/wf-untitled-member.yaml",
    findings: [],
    layout: null,
    name: "Untitled member",
    workflowId: "wf-untitled-member",
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
  const createdMemberSummary = {
    createdAt: "2026-06-08T00:00:00Z",
    description: "",
    displayName: "Untitled member",
    implementationKind: "workflow",
    lastBoundRevisionId: null,
    lifecycleStage: "created",
    memberId: "m-untitled-member",
    publishedServiceId: "member-m-untitled-member",
    scopeId: "scope-1",
    teamId: "t-alpha",
    updatedAt: "2026-06-08T00:00:01Z",
  };
  const createdMemberDetail = {
    implementationRef: {
      implementationKind: "workflow",
      workflowId: "wf-untitled-member",
    },
    summary: createdMemberSummary,
  };

  (studioApi.saveWorkflow as jest.Mock).mockResolvedValue(createdWorkflow);
  (studioApi.createMember as jest.Mock).mockResolvedValue(createdMemberSummary);
  (studioApi.getWorkflow as jest.Mock).mockResolvedValue(createdWorkflow);

  return {
    createdMemberDetail,
    createdMemberSummary,
    createdWorkflow,
  };
}

describe("TeamMemberWorkflowStudioPage", () => {
  beforeEach(() => {
    jest.resetAllMocks();
    window.history.replaceState({}, "", "/");
    mockTeam();
    mockSerializeYaml();
    mockParseYaml();
    (studioApi.previewExplicitRequests as jest.Mock).mockImplementation(
      (input: { workflowId: string; revisionId: string }) => ({
        workflowId: input.workflowId,
        revisionId: input.revisionId,
        items: [],
      }),
    );
    (Modal.confirm as jest.Mock).mockClear();
    (studioApi.listWorkflows as jest.Mock).mockResolvedValue([]);
    (scopeRuntimeApi.listServices as jest.Mock).mockResolvedValue([]);
    (studioApi.updateMemberDisplayName as jest.Mock).mockResolvedValue({
      ackedAt: "2026-06-08T00:00:01Z",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getPublishedWorkflow as jest.Mock).mockImplementation(
      (...args: unknown[]) => (studioApi.getWorkflow as jest.Mock)(...args),
    );
  });

  afterEach(() => {
    cleanupTestQueryClients();
  });

  it("renders a blank new workflow member editor before backend creation", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    expect(screen.getByText("Add first step")).toBeTruthy();
    expect(screen.getByText("nodes:0")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Invoke" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Invoke" })).toHaveAttribute(
      "title",
      "Save this member before invoking it.",
    );
    expect(screen.queryByRole("link", { name: "Invoke" })).toBeNull();
    expect(screen.getByRole("button", { name: "Recurring work" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Recurring work" })).toHaveAttribute(
      "title",
      "Save this member before adding recurring work.",
    );
    expect(screen.queryByRole("link", { name: "Recurring work" })).toBeNull();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(studioApi.getMember).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
  });

  it("keeps save disabled for a new workflow member until a step exists", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.change(await screen.findByLabelText("Workflow title"), {
      target: { value: "Untitled member2" },
    });

    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    const saveButton = screen.getByRole("button", { name: "Save" });
    expect(saveButton).toBeDisabled();
    expect(saveButton).toHaveAttribute(
      "title",
      "Add at least one step before creating this member.",
    );

    fireEvent.click(saveButton);
    await flushAsyncWork();

    expect(studioApi.serializeYaml).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.createMember).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("runs the current unsaved new workflow draft without creating a member first", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", { name: "Run" });
    expect(runDraftButton).toBeEnabled();
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    expect(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    ).toBeDisabled();
    expect(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    ).toHaveAttribute(
      "title",
      "Add at least one step before running this workflow draft.",
    );

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.getByText("Unsaved changes")).toBeTruthy();

    fireEvent.change(within(draftRunPanel).getByRole("textbox"), {
      target: { value: "Run the unsaved workflow" },
    });
    await waitFor(() => {
      expect(
        within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
      ).toBeEnabled();
    });
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );

    await waitFor(() => {
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalled();
    });
    expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
      "scope-1",
      expect.objectContaining({
        prompt: "Run the unsaved workflow",
        workflowYamls: [expect.any(String)],
      }),
      expect.any(AbortSignal),
    );
    const draftRunPayload = (runtimeRunsApi.streamDraftRun as jest.Mock).mock
      .calls[0][1];
    const draftRunYaml = draftRunPayload.workflowYamls[0];
    expect(draftRunYaml).toContain("name: Untitled member");
    expect(draftRunYaml).toContain("id: llm_step");
    expect(draftRunYaml).toContain("type: llm_call");
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.createMember).not.toHaveBeenCalled();
    expect(studioApi.updateMemberImplementationRef).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it("sends draft run files through the scope draft endpoint", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Run" }));
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    expect(within(draftRunPanel).getByTestId("draft-run-file-drop-zone")).toBeTruthy();
    expect(within(draftRunPanel).getByText("No files attached")).toBeTruthy();
    const image = new File(["image-bytes"], "draft.png", { type: "image/png" });
    fireEvent.change(within(draftRunPanel).getByTestId("draft-run-file-input"), {
      target: { files: [image] },
    });
    expect(await within(draftRunPanel).findByText("draft.png")).toBeTruthy();

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );

    await waitFor(() => {
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          files: [image],
          prompt: "",
          workflowYamls: [expect.any(String)],
        }),
        expect.any(AbortSignal),
      );
    });
  });

  it("blocks draft runs while the selected new workflow node configuration is invalid", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.change(screen.getByLabelText("Instruction"), {
      target: { value: "" },
    });

    expect(await screen.findByText("Instruction is required.")).toBeTruthy();
    const runDraftButton = screen.getByRole("button", { name: "Run" });
    expect(runDraftButton).toBeDisabled();
    expect(runDraftButton).toHaveAttribute("title", "Instruction is required.");
    fireEvent.click(runDraftButton);

    await flushAsyncWork();
    expect(screen.queryByLabelText("Draft run panel")).toBeNull();
    expect(screen.getByText("Instruction is required.")).toBeTruthy();
    expect(runtimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(studioApi.serializeYaml).not.toHaveBeenCalled();
  });

  it("opens the node library, inserts a first step, and saves a new linked workflow member", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-untitled-member.yaml",
      filePath: "scope://scope-1/wf-untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "wf-untitled-member",
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
    (studioApi.createMember as jest.Mock).mockResolvedValue({
      createdAt: "2026-06-08T00:00:00Z",
      description: "",
      displayName: "Untitled member",
      implementationKind: "workflow",
      lastBoundRevisionId: null,
      lifecycleStage: "created",
      memberId: "m-untitled-member",
      publishedServiceId: "member-m-untitled-member",
      scopeId: "scope-1",
      teamId: "t-alpha",
      updatedAt: "2026-06-08T00:00:01Z",
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-new",
      memberId: "m-untitled-member",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-untitled-member",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-untitled-member",
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
      fileName: "wf-untitled-member.yaml",
      filePath: "scope://scope-1/wf-untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "wf-untitled-member",
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
    fireEvent.change(screen.getByLabelText("Search nodes"), {
      target: { value: "llm" },
    });
    expect(await screen.findByText("LLM call")).toBeTruthy();
    expect(screen.queryByText("llm_call")).toBeNull();
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      expect(studioApi.createMember).toHaveBeenCalledWith({
        displayName: "Untitled member",
        implementationKind: "workflow",
        scopeId: "scope-1",
        teamId: "t-alpha",
      });
      expect(studioApi.createMemberWithId).not.toHaveBeenCalled();
      expect(studioApi.updateMemberDisplayName).not.toHaveBeenCalled();
      expect(studioApi.updateMemberTeamAssignment).not.toHaveBeenCalled();
      expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
        scopeId: "scope-1",
        memberId: "m-untitled-member",
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "wf-untitled-member",
        },
      });
      expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    });
    expect(screen.getByText("Draft")).toBeTruthy();
    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/t-alpha/members/m-untitled-member/workflow",
    );
    expect(new URLSearchParams(window.location.search).get("workflowId")).toBe(
      "wf-untitled-member",
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

  it("waits for a newly created workflow member to materialize before linking the draft", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    const { createdMemberDetail } = mockNewWorkflowMemberCreateFixtures();
    (studioApi.getMember as jest.Mock)
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockResolvedValue(createdMemberDetail);

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.getMember).toHaveBeenCalledWith(
        "scope-1",
        "m-untitled-member",
      );
      expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
        scopeId: "scope-1",
        memberId: "m-untitled-member",
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "wf-untitled-member",
        },
      });
      expect(window.location.pathname).toBe(
        "/scopes/scope-1/teams/t-alpha/members/m-untitled-member/workflow",
      );
    });
    const getMemberMock = studioApi.getMember as jest.Mock;
    const updateMemberImplementationRefMock =
      studioApi.updateMemberImplementationRef as jest.Mock;
    expect(getMemberMock.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(getMemberMock.mock.invocationCallOrder[0]).toBeLessThan(
      updateMemberImplementationRefMock.mock.invocationCallOrder[0],
    );
    expect(getMemberMock.mock.invocationCallOrder[1]).toBeLessThan(
      updateMemberImplementationRefMock.mock.invocationCallOrder[0],
    );
    expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(1);
    expect(studioApi.createMember).toHaveBeenCalledTimes(1);
  });

  it("rechecks a newly created workflow member when the first implementation link hits a materialization 404", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    const { createdMemberDetail } = mockNewWorkflowMemberCreateFixtures();
    (studioApi.getMember as jest.Mock).mockResolvedValue(createdMemberDetail);
    (studioApi.updateMemberImplementationRef as jest.Mock)
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockResolvedValue(createdMemberDetail);

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.getMember).toHaveBeenCalledWith(
        "scope-1",
        "m-untitled-member",
      );
      expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledTimes(2);
      expect(window.location.pathname).toBe(
        "/scopes/scope-1/teams/t-alpha/members/m-untitled-member/workflow",
      );
    });
    const getMemberMock = studioApi.getMember as jest.Mock;
    const updateMemberImplementationRefMock =
      studioApi.updateMemberImplementationRef as jest.Mock;
    expect(getMemberMock.mock.calls.length).toBeGreaterThanOrEqual(2);
    expect(getMemberMock.mock.invocationCallOrder[1]).toBeLessThan(
      updateMemberImplementationRefMock.mock.invocationCallOrder[1],
    );
    expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(1);
    expect(studioApi.createMember).toHaveBeenCalledTimes(1);
  });

  it("retries linking a pending created workflow member without creating another member", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    const { createdMemberDetail } = mockNewWorkflowMemberCreateFixtures();
    (studioApi.getMember as jest.Mock)
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockRejectedValueOnce(new StudioApiError("Not Found", 404))
      .mockResolvedValue(createdMemberDetail);

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    const saveButton = screen.getByRole("button", { name: "Save" });
    fireEvent.click(saveButton);

    expect(
      await screen.findByText(
        "Workflow member was created but is not visible yet. Retry saving in a moment.",
      ),
    ).toBeTruthy();
    expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(1);
    expect(studioApi.createMember).toHaveBeenCalledTimes(1);
    expect(studioApi.updateMemberImplementationRef).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Retried member" },
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
        scopeId: "scope-1",
        memberId: "m-untitled-member",
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "wf-untitled-member",
        },
      });
      expect(window.location.pathname).toBe(
        "/scopes/scope-1/teams/t-alpha/members/m-untitled-member/workflow",
      );
    });
    expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(2);
    expect(studioApi.saveWorkflow).toHaveBeenLastCalledWith(
      expect.objectContaining({
        workflowId: "wf-untitled-member",
        workflowName: "Retried member",
        yaml: expect.stringContaining("name: Retried member"),
      }),
    );
    expect(studioApi.createMember).toHaveBeenCalledTimes(1);
  });

  it("reloads route state after creating a workflow member", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-untitled-member.yaml",
      filePath: "scope://scope-1/wf-untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "wf-untitled-member",
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
        workflowId: "wf-untitled-member",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-untitled-member",
        publishedServiceId: "member-m-untitled-member",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    };
    (studioApi.createMember as jest.Mock).mockResolvedValue(
      createdMemberDetail.summary,
    );
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-new",
      memberId: "m-untitled-member",
      scopeId: "scope-1",
      status: "accepted",
    });
    const createdWorkflow = {
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-untitled-member.yaml",
      filePath: "scope://scope-1/wf-untitled-member.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "wf-untitled-member",
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
        memberId === "m-untitled-member" ? createdMemberDetail : undefined,
    );
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async (workflowId: string) =>
        workflowId === "wf-untitled-member" ? createdWorkflow : undefined,
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(window.location.pathname).toBe(
        "/scopes/scope-1/teams/t-alpha/members/m-untitled-member/workflow",
      );
      expect(new URLSearchParams(window.location.search).get("workflowId")).toBe(
        "wf-untitled-member",
      );
      expect(studioApi.getMember).toHaveBeenCalledWith(
        "scope-1",
        "m-untitled-member",
      );
      expect(studioApi.getWorkflow).toHaveBeenCalledWith(
        "wf-untitled-member",
        "scope-1",
      );
      expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
        scopeId: "scope-1",
        memberId: "m-untitled-member",
        implementationRef: {
          implementationKind: "workflow",
          workflowId: "wf-untitled-member",
        },
      });
    });
  });

  it("warns before leaving with unsaved workflow changes", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
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
    fireEvent.click(screen.getByRole("link", { name: "Team" }));
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.getByRole("button", { name: "node:step:triage" })).toBeTruthy();
    expect(studioApi.getMember).toHaveBeenCalledWith("scope-1", "member-alpha");
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("workflow-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "member-alpha",
      "scope-1",
    );
    fireEvent.click(screen.getByRole("button", { name: "Back" }));
    expect(`${window.location.pathname}${window.location.search}`).toBe(
      "/scopes/scope-1/teams/t-alpha?memberId=member-alpha&workflowId=workflow-alpha&tab=members",
    );
  });

  it("renders the current workflow YAML from the editable draft", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Stale Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    let resolveSerialize: ((value: unknown) => void) | null = null;
    (studioApi.serializeYaml as jest.Mock).mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          resolveSerialize = resolve;
        }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");

    expect(await screen.findByLabelText("Workflow YAML panel")).toBeTruthy();
    expect(screen.queryByLabelText("Workflow YAML editor")).toBeNull();
    expect(screen.getByRole("button", { name: "Apply to draft" })).toBeDisabled();
    expect(studioApi.serializeYaml).toHaveBeenCalledWith(
      expect.objectContaining({
        availableStepTypes: expect.any(Array),
        document: expect.objectContaining({
          name: "Workflow Alpha",
          steps: expect.arrayContaining([
            expect.objectContaining({ id: "triage", type: "llm_call" }),
          ]),
        }),
      }),
    );

    await act(async () => {
      resolveSerialize?.({
        document: mockWorkflowDocument,
        findings: [],
        yaml: "name: Serialized Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      });
    });

    const yamlView = await screen.findByLabelText("Workflow YAML editor");
    await waitFor(() => {
      const yamlValue = (yamlView as HTMLTextAreaElement).value;
      expect(yamlValue).toContain("Serialized Workflow Alpha");
      expect(yamlValue).toContain("id: triage");
      expect(yamlValue).not.toContain("Stale Workflow Alpha");
    });
    expect(yamlView.tagName).toBe("TEXTAREA");
    expect((yamlView as HTMLTextAreaElement).wrap).toBe("off");
    expect((yamlView as HTMLElement).style.height).toBe("100%");
    expect((yamlView as HTMLElement).style.minHeight).toBe("0");
    expect((yamlView as HTMLElement).style.overflow).toBe("auto");
    const yamlEditorShell = yamlView.parentElement as HTMLElement;
    const yamlPanelBody = yamlEditorShell.parentElement as HTMLElement;
    const yamlPanelFooter = screen
      .getByRole("button", { name: "Apply to draft" })
      .closest("footer") as HTMLElement;
    expect(yamlEditorShell).toHaveStyle({ flex: "1 1 0%", minHeight: 0 });
    expect(yamlPanelBody).toHaveStyle({
      display: "flex",
      flexDirection: "column",
      overflow: "hidden",
    });
    expect(yamlPanelFooter).toHaveStyle({ flex: "0 0 auto" });
    expect(screen.queryByText("Wrap")).toBeNull();
    expect(screen.queryByText("Refresh")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Close YAML editor" }));
    await waitFor(() => {
      expect(screen.queryByLabelText("Workflow YAML panel")).toBeNull();
    });
    clickYamlAction("Edit YAML");
    await screen.findByLabelText("Workflow YAML editor");
    expect(studioApi.serializeYaml).toHaveBeenCalledTimes(2);
  });

  it("surfaces current YAML serialization failure in the editor panel", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    (studioApi.serializeYaml as jest.Mock)
      .mockRejectedValueOnce(new Error("Serialization failed"))
      .mockResolvedValueOnce({
        document: mockWorkflowDocument,
        findings: [],
        yaml: "name: Retried Workflow Alpha\nsteps: []\n",
      });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");

    expect(await screen.findByText("Serialization failed")).toBeTruthy();
    expect(screen.getByLabelText("Workflow YAML panel")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Apply to draft" })).toBeDisabled();
    expect(studioApi.serializeYaml).toHaveBeenCalledTimes(1);
  });

  it("preserves draft workflow query on scoped existing member workflow routes", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(`${window.location.pathname}${window.location.search}`).toBe(
        "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
      );
    });
    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith(
        "workflow-alpha",
        "scope-1",
      );
    });
    expect(studioApi.getMember).toHaveBeenCalledWith("scope-1", "member-alpha");
  });

  it("returns from an existing workflow member editor to the owning Team members tab", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(screen.getByRole("button", { name: "Back" }));

    expect(window.location.pathname).toBe("/scopes/scope-1/teams/t-alpha");
    const params = new URLSearchParams(window.location.search);
    expect(params.get("memberId")).toBe("member-alpha");
    expect(params.get("tab")).toBe("members");
    expect(params.get("workflowId")).toBe("workflow-alpha");
  });

  it("uses the route workflowId instead of published service or member detail identities", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-098e767a6da0468bad1aaa1857e7ebf4/workflow?workflowId=workflow-member-source",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-member-source",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "bind_ready",
        memberId: "m-098e767a6da0468bad1aaa1857e7ebf4",
        publishedServiceId: "member-m-098e767a6da0468bad1aaa1857e7ebf4",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-member-source.yaml",
      filePath: "scope://scope-1/workflow-member-source.yaml",
      findings: [],
      layout: null,
      name: "Untitled member",
      workflowId: "workflow-member-source",
      yaml: "name: Untitled member\nsteps: []\n",
      document: {
        ...mockWorkflowDocument,
        name: "Untitled member",
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(studioApi.getMember).toHaveBeenCalledWith(
      "scope-1",
      "m-098e767a6da0468bad1aaa1857e7ebf4",
    );
    expect(studioApi.getWorkflow).toHaveBeenCalledWith(
      "workflow-member-source",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "member-m-098e767a6da0468bad1aaa1857e7ebf4",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "m-098e767a6da0468bad1aaa1857e7ebf4",
      "scope-1",
    );
    expect(studioApi.listWorkflows).not.toHaveBeenCalled();
  });

  it("recovers the draft workflow id from the member read model when Invoke opens Studio without a query hint", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-alpha",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Workflow Alpha")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("wf-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("m-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("svc-alpha", "scope-1");
    expect(studioApi.getPublishedWorkflow).not.toHaveBeenCalled();
    expect(
      screen.queryByText("No workflow draft is linked to this member yet."),
    ).toBeNull();
  });

  it("distinguishes draft load failure from a missing member draft link", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-alpha",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockRejectedValue(
      new StudioApiError("Draft read model unavailable.", 503),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(
      await screen.findByText("Workflow draft could not be loaded."),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Studio resolved draft workflow wf-alpha, but loading it failed: Draft read model unavailable.",
      ),
    ).toBeTruthy();
    expect(
      screen.queryByText("No workflow draft is linked to this member yet."),
    ).toBeNull();
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("wf-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("m-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("svc-alpha", "scope-1");
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("opens recurring work for a published member without passing workflow or service identities", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha&workflowSource=published",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Workflow Alpha")).toBeTruthy();
    const recurringWorkLink = await screen.findByRole("link", {
      name: "Recurring work",
    });
    expect(recurringWorkLink).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/automations",
    );

    fireEvent.click(recurringWorkLink);

    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/automations",
    );
    expect(window.location.search).toBe("");
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("wf-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("m-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("svc-alpha", "scope-1");
  });

  it("opens the published member invoke workbench without passing workflow or service identities", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha&workflowSource=published",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Workflow Alpha")).toBeTruthy();
    const invokeLink = await screen.findByRole("link", {
      name: "Invoke",
    });
    expect(invokeLink).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/invoke",
    );

    fireEvent.click(invokeLink);

    expect(window.location.pathname).toBe(
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/invoke",
    );
    expect(window.location.search).toBe("");
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("wf-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("m-alpha", "scope-1");
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith("svc-alpha", "scope-1");
  });

  it("does not treat the member id as a workflow draft ref when the read model omits the draft ref", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/untitled-member/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: null,
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

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();
  });

  it("reopens the saved draft when the route carries the draft workflow id", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/untitled-member/workflow?workflowId=untitled-member",
    );
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
        lifecycleStage: "bind_ready",
        memberId: "untitled-member",
        publishedServiceId: "member-untitled-member",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:01Z",
        implementationKind: "workflow",
        publishedServiceId: "member-untitled-member",
        revisionId: "rev-untitled-member",
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
      yaml: "name: Untitled member\nsteps:\n  - id: llm_step\n    type: llm_call\n",
      document: {
        name: "Untitled member",
        roles: [],
        steps: [
          {
            id: "llm_step",
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

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.getByRole("button", { name: "node:step:llm_step" })).toBeTruthy();
    expect(
      screen.queryByText("No workflow draft is linked to this member yet."),
    ).toBeNull();
    expect(studioApi.getWorkflow).toHaveBeenCalledWith(
      "untitled-member",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "member-untitled-member",
      "scope-1",
    );
  });

  it("does not reload runtime display names or fall back to the member id", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/untitled-member/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "Untitled member",
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
    (studioApi.getWorkflow as jest.Mock).mockImplementation(
      async (workflowId: string) => {
        throw Object.assign(new Error(`Not found: ${workflowId}`), { status: 404 });
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member")).toBeTruthy();
    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "Untitled member",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "untitled-member",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("shows a recoverable state when no stable workflow ref exists", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow",
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
    (studioApi.getWorkflow as jest.Mock).mockRejectedValue(
      Object.assign(new Error("Not found"), { status: 404 }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("saves applied YAML for an unlinked existing member by creating a reusable draft id", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-untitled-9/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member 9",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-untitled-9",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockRejectedValue(
      Object.assign(new Error("Not found"), { status: 404 }),
    );
    (studioApi.parseYaml as jest.Mock).mockResolvedValue({
      document: {
        name: "Imported member 9",
        roles: mockWorkflowDocument.roles,
        steps: [
          {
            id: "triage",
            type: "llm_call",
            targetRole: "assistant",
            parameters: { prompt_prefix: "Triage" },
            next: null,
            branches: {},
          },
        ],
      },
      findings: [],
    });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member-9.yaml",
      filePath: "scope://scope-1/untitled-member-9.yaml",
      findings: [],
      layout: null,
      name: "Imported member 9",
      workflowId: "untitled-member-9",
      yaml: "name: Imported member 9\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: null,
      updatedAtUtc: "2026-06-08T00:00:02Z",
    });
    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const saveButton = await screen.findByRole("button", { name: "Save" });
    await waitFor(() => {
      expect(saveButton).toBeDisabled();
      expect(screen.getByDisplayValue("Untitled member 9")).toBeTruthy();
    });
    clickYamlAction("Edit YAML");
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value: "name: Imported member 9\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });
    fireEvent.click(applyButton);

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          draftExists: false,
          scopeId: "scope-1",
          directoryId: "scope:scope-1",
          workflowId: "",
          workflowName: "Imported member 9",
        }),
      );
    });
    expect(new URLSearchParams(window.location.search).get("workflowId")).toBe(
      "untitled-member-9",
    );
    expect(studioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
      scopeId: "scope-1",
      memberId: "m-untitled-9",
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "untitled-member-9",
      },
    });
    expect(studioApi.updateMemberDisplayName).toHaveBeenCalledWith({
      scopeId: "scope-1",
      memberId: "m-untitled-9",
      displayName: "Imported member 9",
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
  });

  it("reuses the route draft id after refresh even before the member read model exposes the link", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-untitled-9/workflow?workflowId=untitled-member-9",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member 9",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-untitled-9",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    const loadedWorkflow = {
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "untitled-member-9.yaml",
      filePath: "scope://scope-1/untitled-member-9.yaml",
      findings: [],
      layout: null,
      name: "Untitled member 9",
      workflowId: "untitled-member-9",
      yaml: "name: Untitled member 9\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    };
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue(loadedWorkflow);
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      ...loadedWorkflow,
      updatedAtUtc: "2026-06-08T00:00:03Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const saveButton = await screen.findByRole("button", { name: "Save" });
    await waitFor(() => {
      expect(studioApi.getWorkflow).toHaveBeenCalledWith(
        "untitled-member-9",
        "scope-1",
      );
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(saveButton).toBeDisabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Add node" }));
    fireEvent.click(await screen.findByRole("button", { name: "Insert Guard node" }));
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          draftExists: true,
          scopeId: "scope-1",
          workflowId: "untitled-member-9",
          workflowName: "Untitled member 9",
        }),
      );
    });
    expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(1);
  });

  it("keeps draft status when only the published service identity exists", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member 8",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockRejectedValue(
      Object.assign(new Error("Not found"), { status: 404 }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member 8")).toBeTruthy();
    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(screen.getByText("Draft")).toBeTruthy();
    expect(screen.queryByText("Published")).toBeNull();
    expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("shows published member status from completed binding facts even when no workflow draft is linked", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Untitled member 8",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-alpha",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockRejectedValue(
      Object.assign(new Error("Not found"), { status: 404 }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Untitled member 8")).toBeTruthy();
    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(screen.getByText("Published")).toBeTruthy();
    expect(screen.queryByText("Draft")).toBeNull();
    expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    const publishedRunsButton = screen.getByRole("link", {
      name: "Published runs",
    });
    expect(publishedRunsButton).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/runs",
    );
    expect(publishedRunsButton).toHaveAttribute(
      "title",
      "View runs from the published member service.",
    );
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("keeps draft status when the previous binding is stale for the current implementation", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-old",
        lifecycleStage: "build_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-old",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(screen.getByText("Draft")).toBeTruthy();
    expect(screen.queryByText("Published")).toBeNull();
    expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    expect(screen.getByRole("button", { name: "Publish" })).toBeEnabled();
  });

  it("saves existing workflow drafts without publishing, execution calls, or canvas reload", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    const loadedWorkflow = {
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
    };
    (studioApi.getWorkflow as jest.Mock)
      .mockResolvedValueOnce(loadedWorkflow)
      .mockResolvedValueOnce({
        ...loadedWorkflow,
        updatedAtUtc: "2026-06-08T00:00:02Z",
      });
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      ...loadedWorkflow,
      document: null,
      updatedAtUtc: "2026-06-08T00:00:01Z",
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n  - id: guard_step\n    type: guard\n",
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
    await flushAsyncWork();
    expect(studioApi.getWorkflow).toHaveBeenCalledTimes(1);
    expect(screen.getByText("nodes:2")).toBeTruthy();
    await waitFor(() => {
      expect(saveButton).toBeDisabled();
    });
  });

  it("loads and saves the workflow draft title even when the member name differs", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Member Alpha",
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
    const loadedWorkflow = {
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
    };
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue(loadedWorkflow);
    (studioApi.saveWorkflow as jest.Mock).mockResolvedValue({
      ...loadedWorkflow,
      document: null,
      name: "Workflow Renamed",
      updatedAtUtc: "2026-06-08T00:00:01Z",
      yaml: "name: Workflow Renamed\nsteps:\n  - id: triage\n    type: llm_call\n",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const saveButton = await screen.findByRole("button", { name: "Save" });
    const titleInput = await screen.findByLabelText("Workflow title");
    await waitFor(() => {
      expect(titleInput).toHaveValue("Workflow Alpha");
    });
    await waitFor(() => {
      expect(saveButton).toBeDisabled();
    });

    fireEvent.change(titleInput, {
      target: { value: "Workflow Renamed" },
    });

    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowId: "workflow-alpha",
          workflowName: "Workflow Renamed",
        }),
      );
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          document: expect.objectContaining({
            name: "Workflow Renamed",
          }),
        }),
      );
      expect(studioApi.updateMemberDisplayName).toHaveBeenCalledWith({
        scopeId: "scope-1",
        memberId: "member-alpha",
        displayName: "Workflow Renamed",
      });
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
    expect(studioApi.updateMemberImplementationRef).not.toHaveBeenCalled();
    expect(titleInput).toHaveValue("Workflow Renamed");
  });

  it("renders unsaved graph edits in the YAML view", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(screen.getByRole("button", { name: "Add node" }));
    fireEvent.click(await screen.findByRole("button", { name: "Insert Guard node" }));
    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
      expect(screen.getByText("Unsaved changes")).toBeTruthy();
    });
    clickYamlAction("Edit YAML");

    const yamlView = await screen.findByLabelText("Workflow YAML editor");
    await waitFor(() => {
      expect((yamlView as HTMLTextAreaElement).value).toContain("type: guard");
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          availableStepTypes: expect.any(Array),
          document: expect.objectContaining({
            name: "Workflow Alpha",
            steps: expect.arrayContaining([
              expect.objectContaining({ id: "triage", type: "llm_call" }),
              expect.objectContaining({ type: "guard" }),
            ]),
          }),
        }),
      );
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("supports selecting, deleting, connecting, and moving nodes before save", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    openMoreActionsMenu();
    expect(
      screen.getByRole("menuitem", { name: "Delete selected node" }),
    ).toBeTruthy();
    closeOpenMenu();
    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockImplementation(() => true);
    clickMoreAction("Delete selected node");
    await waitFor(() => {
      expect(screen.getByText("nodes:0")).toBeTruthy();
    });
    expect(confirmSpy).toHaveBeenCalledWith(
      "Delete the selected node? This cannot be undone.",
    );
    confirmSpy.mockRestore();

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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    openMoreActionsMenu();
    expect(
      screen.getByRole("menuitem", { name: "Delete selected connection" }),
    ).toBeTruthy();
    closeOpenMenu();
    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockImplementation(() => true);
    clickMoreAction("Delete selected connection");
    expect(screen.queryByRole("button", {
      name: "edge:edge:triage:publish:linear",
    })).toBeNull();
    expect(screen.getByText("nodes:2")).toBeTruthy();
    expect(screen.queryByTestId("workflow-node-inspector")).toBeNull();
    expect(
      screen.queryByRole("button", { name: "More workflow actions" }),
    ).toBeNull();
    expect(confirmSpy).toHaveBeenCalledWith(
      "Delete the selected connection? This cannot be undone.",
    );
    confirmSpy.mockRestore();
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockImplementation(() => true);
    clickMoreAction("Delete selected connection");
    confirmSpy.mockRestore();
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    const inspector = screen.getByLabelText("Node inspector");
    expect(inspector).toHaveStyle({
      position: "absolute",
      width: "420px",
    });
    expect(screen.getByLabelText("Resize node inspector")).toBeTruthy();
    expect(within(inspector).queryByText("triage")).toBeNull();
    expect(within(inspector).getAllByText("LLM call").length).toBeGreaterThan(0);
    expect(within(inspector).getByText("Basics")).toBeTruthy();
    expect(within(inspector).queryByText("Step ID")).toBeNull();
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    expect(within(inspector).queryByText("triage")).toBeNull();
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
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

  it("runs the current workflow draft and shows returned logs", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", {
      name: "Run",
    });
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.getByLabelText("Workflow title")).toHaveAttribute(
      "title",
      "Edit workflow name",
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Edit workflow name" }),
    );
    expect(screen.getByLabelText("Workflow title")).toHaveFocus();
    fireEvent.blur(screen.getByLabelText("Workflow title"));
    const headerIdentity = screen.getByTestId("workflow-header-identity");
    const headerPrimaryActions = screen.getByTestId(
      "workflow-header-primary-actions",
    );
    const headerMainRow = screen.getByTestId("workflow-header-main-row");
    expect(headerMainRow).toHaveClass("workflow-studio-header__row");
    expect(headerPrimaryActions).toHaveClass("workflow-studio-header__actions");
    expect(headerPrimaryActions).toHaveAttribute("data-responsive-actions", "true");
    expect(screen.getByTestId("workflow-header-run-actions")).toHaveClass(
      "workflow-studio-header__action-group--primary",
    );
    expect(screen.getByTestId("workflow-header-edit-actions")).toHaveClass(
      "workflow-studio-header__action-group--edit",
    );
    expect(screen.getByTestId("workflow-header-commit-actions")).toHaveClass(
      "workflow-studio-header__action-group--commit",
    );
    expect(screen.getByTestId("workflow-header-secondary-actions")).toHaveClass(
      "workflow-studio-header__action-group--secondary",
    );
    const titleShell = headerIdentity.querySelector(
      ".workflow-studio-header__title-shell",
    );
    expect(titleShell).toBeTruthy();
    const headerResponsiveRules = findRenderedStyleText(
      ".workflow-studio-header__identity",
    );
    expect(headerResponsiveRules).toContain(
      ".workflow-studio-header__identity",
    );
    expect(headerResponsiveRules).toContain("overflow: visible;");
    expect(headerResponsiveRules).toContain("flex-wrap: wrap;");
    expect(headerResponsiveRules).toContain(
      "max-width: min(360px, 100%);",
    );
    expect(headerResponsiveRules).toContain("@media (max-width: 1500px)");
    expect(headerResponsiveRules).toContain("@media (max-width: 980px)");
    expect(headerResponsiveRules).toContain("overflow: hidden;");
    expect(headerResponsiveRules).not.toContain("overflow-x: auto;");
    expect(screen.queryByTestId("workflow-header-context-row")).toBeNull();
    expect(screen.queryByTestId("workflow-header-node-actions")).toBeNull();
    expect(within(headerIdentity).getByRole("link", { name: "Team" })).toHaveAttribute(
      "href",
      "/scopes",
    );
    expect(
      within(headerIdentity).getByRole("link", { name: "Support Team" }),
    ).toHaveAttribute(
      "href",
      "/scopes/scope-1/teams/t-alpha?memberId=member-alpha&workflowId=workflow-alpha&tab=members",
    );
    const globalBackButton = within(headerIdentity).getByRole("button", {
      name: "Back",
    });
    expect(globalBackButton).toBeTruthy();
    expect(globalBackButton).toHaveAttribute("data-aevatar-back-button", "true");
    expect(within(headerIdentity).getByText("Draft")).toBeTruthy();
    expect(
      within(headerIdentity).getByRole("button", { name: "Edit workflow name" }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).getByRole("button", {
        name: "Run",
      }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).getByRole("button", { name: "Add node" }),
    ).toBeTruthy();
    expect(
      within(screen.getByTestId("workflow-header-edit-actions")).getByRole(
        "button",
        { name: "Edit YAML" },
      ),
    ).toBeTruthy();
    const publishedRunsButton = within(headerPrimaryActions).getByRole("button", {
      name: "Published runs",
    });
    expect(publishedRunsButton).toBeDisabled();
    expect(publishedRunsButton).toHaveAttribute(
      "title",
      "Publish this member to start recording published runs.",
    );
    expect(
      within(headerPrimaryActions).getByRole("button", { name: "Save" }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).getByRole("button", { name: "Publish" }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).queryByRole("button", {
        name: "Refresh status",
      }),
    ).toBeNull();
    expect(
      within(headerPrimaryActions).queryByRole("button", {
        name: "Publish member",
      }),
    ).toBeNull();
    expect(
      within(headerPrimaryActions).getByRole("button", { name: "Edit YAML" }),
    ).toBeTruthy();
    expect(
      within(headerPrimaryActions).queryByRole("button", {
        name: "More workflow actions",
      }),
    ).toBeNull();
    expect(screen.queryByRole("button", { name: "Paste YAML" })).toBeNull();
    expect(screen.queryByRole("button", { name: "View YAML" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete node" })).toBeNull();
    expect(
      screen.queryByRole("menuitem", { name: "View YAML" }),
    ).toBeNull();
    expect(
      screen.queryByRole("menuitem", { name: "Paste YAML" }),
    ).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "Publish member" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "Refresh status" })).toBeNull();
    expect(
      screen.queryByRole("menuitem", { name: "Delete selected node" }),
    ).toBeNull();
    expect(screen.queryByText("Runs")).toBeNull();
    expect(screen.queryByText("Set as Team entry")).toBeNull();
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(screen.queryByText("Workflow member")).toBeNull();
    expect(screen.queryByText("Draft workflow member")).toBeNull();
    expect(screen.queryByTestId("member-run-result-panel")).toBeNull();
    expect(screen.queryByText(/execution has been started/i)).toBeNull();
    expect(
      screen.queryByLabelText("Draft run console"),
    ).toBeNull();
    expect(
      screen.queryByLabelText("Draft run panel"),
    ).toBeNull();
    await waitFor(() => {
      expect(runDraftButton).toBeEnabled();
    });
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    expect(
      within(draftRunPanel).getByText(
        "Leave blank to run this draft without user input.",
      ),
    ).toBeTruthy();
    expect(runtimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    const draftRunInput = within(draftRunPanel).getByRole("textbox");
    fireEvent.change(draftRunInput, {
      target: { value: "Run the workflow" },
    });
    expect(draftRunInput).toHaveValue("Run the workflow");
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );
    const resultPanel = await screen.findByTestId("member-run-result-panel");
    const consolePanel = screen.getByLabelText("Draft run console");
    expect(screen.getByLabelText("Draft run panel")).toBeTruthy();
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
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run the workflow",
          workflowYamls: [expect.stringContaining("name: Workflow Alpha")],
        }),
        expect.any(AbortSignal),
      );
    });
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(consolePanel).toHaveTextContent("succeeded");
    });
    await waitFor(() => {
      expect(publishedRunsButton).toBeDisabled();
      expect(publishedRunsButton).not.toHaveAttribute("href");
    });
    expect(within(consolePanel).getByLabelText("Logs overview")).toBeTruthy();
    expect(within(consolePanel).getByLabelText("Log details")).toBeTruthy();
    expect(consolePanel).toHaveTextContent(/Tokens\s*42/);
    expect(within(consolePanel).getByText("Overview")).toBeTruthy();
    expect(within(consolePanel).getByRole("radio", { name: "Nodes" })).toBeTruthy();
    expect(within(consolePanel).getByRole("radio", { name: "Events" })).toBeTruthy();

    const triageLogRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-triage",
    );
    expect(triageLogRow).toHaveTextContent("triage");
    expect(triageLogRow).toHaveTextContent("Success");
    expect(triageLogRow).not.toHaveTextContent("Run the workflow");
    expect(triageLogRow).not.toHaveTextContent("Workflow complete");
    fireEvent.click(triageLogRow);

    expect(
      within(consolePanel).getByRole("button", { name: "Input" }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(
      within(consolePanel).getByRole("button", { name: "Output" }),
    ).toHaveAttribute("aria-pressed", "true");
    const logDetails = within(consolePanel).getByLabelText("Log details");
    expect(logDetails).toHaveTextContent("Input");
    expect(logDetails).toHaveTextContent("Output");
    expect(logDetails).not.toHaveTextContent(
      "aevatar.step.completed",
    );
    expect(logDetails).toHaveTextContent("Run the workflow");
    expect(logDetails).toHaveTextContent("Workflow complete");
    expect(
      within(logDetails).getByTestId("workflow-execution-node-input-block"),
    ).toHaveStyle({ height: "230px" });
    expect(
      within(logDetails).getByTestId("workflow-execution-node-output-block"),
    ).toHaveStyle({ height: "230px" });
    expect(
      within(consolePanel).queryByRole("button", { name: "Copy selected log" }),
    ).toBeNull();
    expect(screen.getByRole("button", { name: "node:step:triage" })).toHaveAttribute(
      "data-execution-focused",
      "true",
    );

    fireEvent.keyDown(within(consolePanel).getByLabelText("Logs overview"), {
      key: "ArrowDown",
    });
    expect(screen.getByRole("button", { name: "node:step:triage" })).toHaveAttribute(
      "data-execution-focused",
      "false",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "approve",
    );

    fireEvent.click(within(consolePanel).getByRole("radio", { name: "Events" }));
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "RUN_STARTED",
    );
    expect(
      within(consolePanel).getByRole("button", { name: "Copy selected log" }),
    ).toBeTruthy();
    const runStartedEventRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-run-0",
    );
    expect(runStartedEventRow).toHaveTextContent("Recorded");
    expect(runStartedEventRow).not.toHaveTextContent("Running");
    fireEvent.click(within(consolePanel).getByRole("radio", { name: "Nodes" }));
    expect(
      within(consolePanel).queryByRole("button", { name: "Copy selected log" }),
    ).toBeNull();

    const approvalLogRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-approve",
    );
    expect(approvalLogRow).toHaveTextContent("approve");
    expect(approvalLogRow).toHaveTextContent("Waiting");
    expect(approvalLogRow).not.toHaveTextContent("Need approval before deployment");
    fireEvent.click(approvalLogRow);
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Need approval before deployment",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "human approval",
    );
    expect(consolePanel).not.toHaveTextContent("aevatar.usage");
    expect(consolePanel).not.toHaveTextContent("STATE_SNAPSHOT");
    expect(consolePanel).not.toHaveTextContent("stateVersion");
    expect(consolePanel).not.toHaveTextContent("raw-observation-1");
    fireEvent.click(within(consolePanel).getByRole("radio", { name: "Events" }));
    expect(within(consolePanel).getByText("aevatar.usage")).toBeTruthy();
    expect(within(consolePanel).getByText("STATE_SNAPSHOT")).toBeTruthy();
    const rawObservedEventRow = within(consolePanel)
      .getByText("aevatar.observed.raw")
      .closest("button");
    expect(rawObservedEventRow).not.toBeNull();
    expect(rawObservedEventRow).toHaveTextContent("Recorded");
    expect(rawObservedEventRow).not.toHaveTextContent("Running");
    expect(consolePanel).not.toHaveTextContent("raw-observation-1");
    fireEvent.click(within(consolePanel).getByText("aevatar.observed.raw"));
    expect(within(consolePanel).getByLabelText("Log details")).not.toHaveTextContent(
      "Running",
    );
    expect(within(consolePanel).getByLabelText("Log details")).not.toHaveTextContent(
      "raw-observation-1",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "runtime-observer",
    );
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(screen.queryByText("run-1")).toBeNull();
    expect(resultPanel).not.toHaveTextContent("Member run");
    expect(resultPanel).not.toHaveTextContent(/persisted workflow state/i);
    fireEvent.click(
      within(consolePanel).getByRole("button", { name: "Clear logs" }),
    );
    expect(screen.queryByLabelText("Draft run console")).toBeNull();
    expect(studioApi.startExecution).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("keeps the header action bar stable while save, YAML, and contextual delete states change", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });

    const headerMainRow = screen.getByTestId("workflow-header-main-row");
    const headerPrimaryActions = screen.getByTestId(
      "workflow-header-primary-actions",
    );
    const readActionButtonNames = () =>
      within(headerPrimaryActions)
        .getAllByRole("button")
        .map((button) => button.getAttribute("aria-label") || button.textContent);

    expect(headerMainRow).toHaveClass("workflow-studio-header__row");
    expect(headerPrimaryActions).toHaveAttribute("data-responsive-actions", "true");
    expect(screen.queryByTestId("workflow-header-context-row")).toBeNull();
    expect(screen.queryByTestId("workflow-header-node-actions")).toBeNull();
    expect(readActionButtonNames()).toEqual([
      "Run",
      "Ask AI",
      "Add node",
      "Edit YAML",
      "Save",
    ]);
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();

    expect(
      within(headerPrimaryActions).queryByRole("button", {
        name: "More workflow actions",
      }),
    ).toBeNull();
    expect(
      screen.queryByRole("menuitem", { name: "Delete selected node" }),
    ).toBeNull();

    const editYamlButton = screen.getByRole("button", { name: "Edit YAML" });
    expect(editYamlButton).toBeTruthy();
    expect(within(editYamlButton).getByText("Edit YAML")).not.toHaveClass(
      "workflow-studio-header__action-label--secondary",
    );
    expect(screen.queryByRole("menuitem", { name: "View YAML" })).toBeNull();
    expect(screen.queryByRole("menuitem", { name: "Paste YAML" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: {
        value:
          "A very long workflow title that should truncate instead of pushing actions down",
      },
    });
    expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
    expect(readActionButtonNames()).toEqual([
      "Run",
      "Ask AI",
      "Add node",
      "Edit YAML",
      "Save",
    ]);

    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    openMoreActionsMenu();
    expect(
      screen.getByRole("menuitem", { name: "Delete selected node" }),
    ).toBeTruthy();
  });

  it("renders draft run log cards as SSE frames arrive", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      document: mockBranchingWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    const stream = createControlledWorkflowInvokeStream();
    (parseBackendSSEStream as jest.Mock).mockReturnValue(stream.stream());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", {
      name: "Run",
    });
    await waitFor(() => {
      expect(runDraftButton).toBeEnabled();
    });
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    const draftRunInput = within(draftRunPanel).getByRole("textbox");
    fireEvent.change(draftRunInput, {
      target: { value: "Run the workflow" },
    });
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );

    const consolePanel = await screen.findByLabelText("Draft run console");
    expect(consolePanel).toHaveTextContent("running");
    expect(
      within(consolePanel).getByRole("button", { name: "Clear logs" }),
    ).toBeDisabled();
    const executionPanelResponsiveRules = findRenderedStyleText(
      ".workflow-studio-execution-panel__body",
    );
    expect(executionPanelResponsiveRules).toContain("@media (max-width: 720px)");
    expect(executionPanelResponsiveRules).toContain(
      "grid-template-columns: minmax(0, 1fr);",
    );
    expect(executionPanelResponsiveRules).toContain("grid-auto-rows: max-content;");
    expect(executionPanelResponsiveRules).toContain(
      "grid-template-rows: min-content max-content;",
    );
    expect(executionPanelResponsiveRules).not.toContain("!important");

    await act(async () => {
      stream.emit({
        actorId: "actor-1",
        commandId: "command-1",
        correlationId: "correlation-1",
        runId: "run-1",
        threadId: "actor-1",
        timestamp: Date.parse("2026-06-08T00:00:00Z"),
        type: "RUN_STARTED",
      });
      await flushAsyncWork();
    });
    fireEvent.click(within(consolePanel).getByRole("radio", { name: "Events" }));
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Run started",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "RUN_STARTED",
    );
    fireEvent.click(within(consolePanel).getByRole("radio", { name: "Nodes" }));
    const pendingTriageRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-triage",
    );
    const pendingGuardRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-guard",
    );
    expect(pendingTriageRow).toHaveTextContent("Pending");
    expect(pendingGuardRow).toHaveTextContent("Pending");
    expect(pendingTriageRow).toBeDisabled();
    expect(pendingGuardRow).toBeDisabled();
    expect(pendingTriageRow).toHaveStyle({ height: "80px", minHeight: "80px" });
    expect(pendingGuardRow).toHaveStyle({ height: "80px", minHeight: "80px" });
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Select a log entry",
    );

    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockImplementation(() => true);
    fireEvent.click(screen.getByRole("button", { name: "node:step:guard" }));
    clickMoreAction("Delete selected node");
    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(
      within(consolePanel).getByTestId("workflow-execution-log-row-node-guard"),
    ).toHaveTextContent("Pending");
    expect(consolePanel).toHaveTextContent(/Steps\s*2/);
    confirmSpy.mockRestore();

    await act(async () => {
      stream.emit({
        name: "aevatar.step.request",
        payload: {
          input: "Run the workflow",
          stepId: "triage",
          stepType: "llm_call",
          targetRole: "assistant",
        },
        timestamp: Date.parse("2026-06-08T00:00:01Z"),
        type: "CUSTOM",
      });
      await flushAsyncWork();
    });
    const runningTriageRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-triage",
    );
    expect(runningTriageRow).toHaveTextContent("Running");
    expect(runningTriageRow).toBeEnabled();
    expect(
      within(consolePanel).getByTestId("workflow-execution-log-row-node-guard"),
    ).toHaveTextContent("Pending");
    expect(runningTriageRow).not.toHaveTextContent("Run the workflow");
    fireEvent.click(runningTriageRow);
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Run the workflow",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "No output captured for this node.",
    );
    fireEvent.keyDown(within(consolePanel).getByLabelText("Logs overview"), {
      key: "ArrowDown",
    });
    expect(runningTriageRow).toHaveAttribute("aria-pressed", "true");
    expect(
      within(consolePanel).getByTestId("workflow-execution-log-row-node-guard"),
    ).toBeDisabled();
    expect(consolePanel).toHaveTextContent(/Events\s*2/);
    expect(consolePanel).toHaveTextContent(/Steps\s*2/);

    await act(async () => {
      stream.emit({
        name: "aevatar.step.completed",
        payload: {
          output: "Workflow complete",
          stepId: "triage",
          success: true,
        },
        timestamp: Date.parse("2026-06-08T00:00:02Z"),
        type: "CUSTOM",
      });
      await flushAsyncWork();
    });
    const completedTriageRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-triage",
    );
    expect(completedTriageRow).toHaveTextContent("Success");
    expect(completedTriageRow).not.toHaveTextContent("Workflow complete");
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Workflow complete",
    );

    await act(async () => {
      stream.emit({
        name: "aevatar.step.request",
        payload: {
          input: "Second pass input",
          stepId: "triage",
          stepType: "llm_call",
          targetRole: "assistant",
        },
        timestamp: Date.parse("2026-06-08T00:00:03Z"),
        type: "CUSTOM",
      });
      await flushAsyncWork();
    });
    let triageAttemptRows = within(consolePanel).getAllByTestId(
      "workflow-execution-log-row-node-triage",
    );
    expect(triageAttemptRows).toHaveLength(2);
    fireEvent.click(triageAttemptRows[1]);
    expect(
      triageAttemptRows.filter((row) => row.getAttribute("aria-pressed") === "true"),
    ).toHaveLength(1);
    expect(triageAttemptRows[0]).toHaveAttribute("aria-pressed", "false");
    expect(triageAttemptRows[1]).toHaveAttribute("aria-pressed", "true");
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Second pass input",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "No output captured for this node.",
    );

    await act(async () => {
      stream.emit({
        name: "aevatar.step.completed",
        payload: {
          output: "Second pass output",
          stepId: "triage",
          success: true,
        },
        timestamp: Date.parse("2026-06-08T00:00:04Z"),
        type: "CUSTOM",
      });
      await flushAsyncWork();
    });
    triageAttemptRows = within(consolePanel).getAllByTestId(
      "workflow-execution-log-row-node-triage",
    );
    expect(
      triageAttemptRows.filter((row) => row.getAttribute("aria-pressed") === "true"),
    ).toHaveLength(1);
    expect(triageAttemptRows[0]).toHaveAttribute("aria-pressed", "false");
    expect(triageAttemptRows[1]).toHaveAttribute("aria-pressed", "true");
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Second pass input",
    );
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "Second pass output",
    );
    expect(consolePanel).toHaveTextContent(/Steps\s*2/);

    await act(async () => {
      stream.emit({
        result: {
          output: "Second pass output",
        },
        runId: "run-1",
        timestamp: Date.parse("2026-06-08T00:00:05Z"),
        type: "RUN_FINISHED",
      });
      stream.finish();
      await flushAsyncWork();
    });
    await waitFor(() => {
      expect(consolePanel).toHaveTextContent("succeeded");
    });
    expect(
      within(consolePanel).getByRole("button", { name: "Clear logs" }),
    ).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Run" }));
    const settledDraftRunPanel = await screen.findByLabelText("Draft run panel");
    await waitFor(() => {
      expect(
        within(settledDraftRunPanel).getByRole("button", {
          name: /Start draft run/,
        }),
      ).toBeEnabled();
    });
    const startSecondRunButton = within(settledDraftRunPanel).getByRole("button", {
      name: /Start draft run/,
    });

    let finishSecondSerialization: (() => void) | undefined;
    (studioApi.serializeYaml as jest.Mock).mockImplementationOnce(
      ({ document }) =>
        new Promise((resolve) => {
          finishSecondSerialization = () => {
            resolve({
              document,
              findings: [],
              yaml: `name: ${document.name}\nsteps:\n${(document.steps ?? [])
                .map(
                  (step: { id?: string; type?: string }) =>
                    `  - id: ${step.id}\n    type: ${step.type}`,
                )
                .join("\n")}`,
            });
          };
        }),
    );
    fireEvent.click(startSecondRunButton);
    await waitFor(() => {
      expect(screen.queryByLabelText("Draft run console")).toBeNull();
    });
    await act(async () => {
      finishSecondSerialization?.();
      await flushAsyncWork();
    });
    const secondRunConsole = await screen.findByLabelText("Draft run console");
    expect(
      within(secondRunConsole).getByTestId("workflow-execution-log-row-node-triage"),
    ).toHaveTextContent("Pending");
    expect(
      within(secondRunConsole).queryByTestId("workflow-execution-log-row-node-guard"),
    ).toBeNull();
    expect(secondRunConsole).toHaveTextContent(/Steps\s*1/);
  });

  it("starts a failed draft run from the draft run panel and keeps the error visible", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(
      createFailedWorkflowInvokeEvents(),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", {
      name: "Run",
    });
    await waitFor(() => {
      expect(runDraftButton).toBeEnabled();
    });
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );

    const resultPanel = await screen.findByTestId(
      "member-run-result-panel",
    );
    await waitFor(() => {
      expect(within(resultPanel).getByLabelText("Log details")).toHaveTextContent(
        "Authenticated member does not match requested member.",
      );
    });
    expect(resultPanel).toHaveTextContent("failed");
    expect(
      within(resultPanel).getByTestId("workflow-execution-log-row-node-triage"),
    ).toHaveTextContent("Pending");
    expect(screen.queryByTestId("member-run-summary")).toBeNull();
    expect(resultPanel).not.toHaveTextContent("Member run");
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(screen.getByLabelText("Draft run panel")).toBeTruthy();
  });

  it("runs draft workflow members before they are published", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(
      createWorkflowInvokeEvents(""),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", {
      name: "Run",
    });
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    await waitFor(() => {
      expect(runDraftButton).toBeEnabled();
    });
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );
    expect(screen.getByLabelText("Draft run panel")).toBeTruthy();

    await waitFor(() => {
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "",
          workflowYamls: [expect.stringContaining("name: Workflow Alpha")],
        }),
        expect.any(AbortSignal),
      );
    });
    const consolePanel = screen.getByLabelText("Draft run console");
    const triageLogRow = within(consolePanel).getByTestId(
      "workflow-execution-log-row-node-triage",
    );
    fireEvent.click(triageLogRow);
    expect(within(consolePanel).getByLabelText("Log details")).toHaveTextContent(
      "No input captured for this node.",
    );
    expect(within(consolePanel).getByLabelText("Log details")).not.toHaveTextContent(
      "Run Workflow Alpha",
    );
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it("applies edited YAML into the workflow editor", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
        steps: [],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.parseYaml as jest.Mock).mockResolvedValue({
      document: {
        name: "Imported workflow",
        roles: mockWorkflowDocument.roles,
        steps: [
          {
            id: "triage",
            type: "llm_call",
            targetRole: "assistant",
            parameters: { prompt_prefix: "Triage" },
            next: "guard",
            branches: {},
          },
          {
            id: "guard",
            type: "guard",
            targetRole: "",
            parameters: { check: "not_empty" },
            next: null,
            branches: {},
          },
        ],
      },
      findings: [],
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:0")).toBeTruthy();
    });
    clickYamlAction("Edit YAML");
    expect(await screen.findByLabelText("Workflow YAML panel")).toBeTruthy();
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value: "name: Imported workflow\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });
    fireEvent.click(applyButton);

    await waitFor(() => {
      expect(studioApi.parseYaml).toHaveBeenCalledWith({
        yaml: expect.stringContaining("Imported workflow"),
        availableStepTypes: expect.any(Array),
      });
    });
    expect(await screen.findByDisplayValue("Imported workflow")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });
    expect(screen.getByLabelText("Workflow YAML panel")).toBeTruthy();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
  });

  it("keeps AI workflow changes reviewable until the proposal is explicitly applied", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.authorWorkflow as jest.Mock).mockImplementation(
      async (_input, options) => {
        options?.onReasoning?.("Add a guard after triage.");
        return [
          "name: Workflow Alpha",
          "steps:",
          "  - id: triage",
          "    type: llm_call",
          "  - id: guard",
          "    type: guard",
        ].join("\n");
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.click(screen.getByRole("button", { name: "Ask AI" }));
    const proposalPanel = await screen.findByLabelText("AI workflow proposal panel");
    fireEvent.change(within(proposalPanel).getByLabelText("Change request"), {
      target: { value: "Add a guard after triage" },
    });
    fireEvent.click(
      within(proposalPanel).getByRole("button", { name: "Generate proposal" }),
    );

    await waitFor(() => {
      expect(studioApi.authorWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          currentYaml: expect.stringContaining("id: triage"),
          prompt: "Add a guard after triage",
        }),
        expect.objectContaining({
          onReasoning: expect.any(Function),
        }),
      );
    });
    expect(await within(proposalPanel).findByText("Ready to review")).toBeTruthy();
    expect(within(proposalPanel).getByText("Add a guard after triage.")).toBeTruthy();
    expect(
      (within(proposalPanel).getByLabelText(
        "Proposed workflow YAML",
      ) as HTMLTextAreaElement).value,
    ).toContain("id: guard");
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    expect(screen.queryByText("Unsaved changes")).toBeNull();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();

    fireEvent.click(
      within(proposalPanel).getByRole("button", { name: "Apply proposal" }),
    );

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:2");
    });
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("blocks an AI proposal when the human draft changes during generation", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    let resolveProposal: ((yaml: string) => void) | null = null;
    (studioApi.authorWorkflow as jest.Mock).mockImplementation(
      () =>
        new Promise<string>((resolve) => {
          resolveProposal = resolve;
        }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    fireEvent.click(await screen.findByRole("button", { name: "Add first step" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Insert LLM call node" }),
    );
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });

    fireEvent.click(screen.getByRole("button", { name: "Ask AI" }));
    const proposalPanel = await screen.findByLabelText("AI workflow proposal panel");
    fireEvent.change(within(proposalPanel).getByLabelText("Change request"), {
      target: { value: "Add a guard after triage" },
    });
    fireEvent.click(
      within(proposalPanel).getByRole("button", { name: "Generate proposal" }),
    );
    await waitFor(() => {
      expect(studioApi.authorWorkflow).toHaveBeenCalled();
    });

    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Human edited title" },
    });
    await act(async () => {
      resolveProposal?.(
        [
          "name: Proposed title",
          "steps:",
          "  - id: llm_step",
          "    type: llm_call",
          "  - id: guard",
          "    type: guard",
        ].join("\n"),
      );
    });

    expect(await within(proposalPanel).findByText("Outdated proposal")).toBeTruthy();
    expect(
      within(proposalPanel).getByText(
        "Draft changed after this proposal was generated. Generate a new proposal from the current draft.",
      ),
    ).toBeTruthy();
    expect(
      within(proposalPanel).getByRole("button", { name: "Apply proposal" }),
    ).toBeDisabled();
    expect(screen.getByLabelText("Workflow title")).toHaveValue("Human edited title");
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.createMember).not.toHaveBeenCalled();
  });

  it("preserves the AI change request and retries after generation fails", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (studioApi.authorWorkflow as jest.Mock)
      .mockRejectedValueOnce(new Error("Generator unavailable"))
      .mockResolvedValueOnce(
        [
          "name: Recovered workflow",
          "steps:",
          "  - id: triage",
          "    type: llm_call",
        ].join("\n"),
      );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await screen.findByLabelText("Workflow title");
    fireEvent.click(screen.getByRole("button", { name: "Ask AI" }));
    const proposalPanel = await screen.findByLabelText("AI workflow proposal panel");
    const changeRequest = within(proposalPanel).getByLabelText("Change request");
    fireEvent.change(changeRequest, {
      target: { value: "Create a triage workflow" },
    });
    fireEvent.click(
      within(proposalPanel).getByRole("button", { name: "Generate proposal" }),
    );

    expect(await within(proposalPanel).findByText("Generator unavailable")).toBeTruthy();
    expect(changeRequest).toHaveValue("Create a triage workflow");
    fireEvent.click(
      within(proposalPanel).getByRole("button", { name: "Retry proposal" }),
    );

    expect(await within(proposalPanel).findByText("Ready to review")).toBeTruthy();
    expect(studioApi.authorWorkflow).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:0");
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("freezes YAML editing while Apply is in flight", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    let resolveApplySerialize: (() => void) | null = null;
    (studioApi.serializeYaml as jest.Mock).mockImplementation(
      ({ document }) => {
        const response = {
          document,
          findings: [],
          yaml: `name: ${document.name}\nsteps:\n${(document.steps ?? [])
            .map((step: { id?: string; type?: string }) => `  - id: ${step.id}\n    type: ${step.type}`)
            .join("\n")}`,
        };

        if (document.name === "Applied workflow") {
          return new Promise((resolve) => {
            resolveApplySerialize = () => resolve(response);
          });
        }

        return Promise.resolve(response);
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await screen.findByLabelText("Workflow title");
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    fireEvent.change(editor, {
      target: {
        value:
          "name: Applied workflow\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });

    fireEvent.click(applyButton);
    await waitFor(() => {
      expect(resolveApplySerialize).toBeTruthy();
      expect(editor).toHaveProperty("readOnly", true);
    });
    fireEvent.change(editor, {
      target: {
        value:
          "name: Newer in-flight edit\nsteps:\n  - id: later\n    type: guard\n",
      },
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Newer canvas title" },
    });

    await act(async () => {
      resolveApplySerialize?.();
    });

    await waitFor(() => {
      expect(editor).toHaveProperty("readOnly", false);
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:0");
      expect(screen.getByLabelText("Workflow title")).toHaveValue(
        "Newer canvas title",
      );
      expect(
        screen.getAllByText(
          "This YAML buffer was based on an older draft. Reopen Edit YAML from the current canvas before applying.",
        ),
      ).not.toHaveLength(0);
    });
    expect((editor as HTMLTextAreaElement).value).toContain(
      "name: Applied workflow",
    );
    expect((editor as HTMLTextAreaElement).value).not.toContain(
      "Newer in-flight edit",
    );
    expect(
      (studioApi.parseYaml as jest.Mock).mock.calls.some(([input]) =>
        String(input.yaml).includes("Newer in-flight edit"),
      ),
    ).toBe(false);
  });

  it("keeps applied YAML in the edit buffer", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
        steps: [],
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.parseYaml as jest.Mock).mockResolvedValue({
      document: {
        name: "Imported workflow",
        roles: mockWorkflowDocument.roles,
        steps: [
          {
            id: "triage",
            type: "llm_call",
            targetRole: "assistant",
            parameters: { prompt_prefix: "Triage" },
            next: "guard",
            branches: {},
          },
          {
            id: "guard",
            type: "guard",
            targetRole: "",
            parameters: { check: "not_empty" },
            next: null,
            branches: {},
          },
        ],
      },
      findings: [],
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:0")).toBeTruthy();
    });
    clickYamlAction("Edit YAML");
    expect(await screen.findByLabelText("Workflow YAML panel")).toBeTruthy();
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value:
          "name: Imported workflow\nsteps:\n  - id: triage\n    type: llm_call\n  - id: guard\n    type: guard\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });
    fireEvent.click(applyButton);

    await waitFor(() => {
      expect(screen.getByText("nodes:2")).toBeTruthy();
    });
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
    const yamlView = await screen.findByLabelText("Workflow YAML editor");
    await waitFor(() => {
      const yamlValue = (yamlView as HTMLTextAreaElement).value;
      expect(yamlValue).toContain("name: Imported workflow");
      expect(yamlValue).toContain("id: guard");
      expect(yamlValue).not.toContain("Workflow Alpha");
      expect(studioApi.serializeYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          availableStepTypes: expect.any(Array),
          document: expect.objectContaining({
            name: "Imported workflow",
            steps: expect.arrayContaining([
              expect.objectContaining({ id: "triage" }),
              expect.objectContaining({ id: "guard" }),
            ]),
          }),
        }),
      );
    });
    await waitFor(() => {
      expect(screen.queryByText("Unapplied")).toBeNull();
      expect(applyButton).toBeDisabled();
    });
    fireEvent.change(yamlView, {
      target: {
        value: `${(yamlView as HTMLTextAreaElement).value}\n# local edit\n`,
      },
    });
    await waitFor(() => {
      expect(screen.getByText("Unapplied")).toBeTruthy();
      expect(applyButton).toBeEnabled();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("keeps Apply to draft enabled after a valid YAML apply failure", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );
    (studioApi.serializeYaml as jest.Mock).mockImplementation(
      async ({ document }) => {
        if (document.name === "Rejected apply") {
          throw new Error("Apply serialization failed");
        }

        return {
          document,
          findings: [],
          yaml: `name: ${document.name}\nsteps:\n${(document.steps ?? [])
            .map((step: { id?: string; type?: string }) => `  - id: ${step.id}\n    type: ${step.type}`)
            .join("\n")}`,
        };
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await screen.findByLabelText("Workflow title");
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    expect(applyButton).toBeDisabled();

    fireEvent.change(editor, {
      target: {
        value:
          "name: Rejected apply\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });

    fireEvent.click(applyButton);

    await waitFor(() => {
      expect(screen.getByText("Apply serialization failed")).toBeTruthy();
      expect(applyButton).toBeEnabled();
    });
    expect(editor).toHaveValue(
      "name: Rejected apply\nsteps:\n  - id: triage\n    type: llm_call\n",
    );
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:0");
  });

  it("keeps invalid YAML in the buffer and preserves the current graph", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.parseYaml as jest.Mock).mockImplementation(
      async ({ yaml }: { yaml: string }) => {
        if (yaml === "not: valid") {
          throw new Error("Invalid workflow YAML");
        }

        return {
          document: mockWorkflowDocument,
          findings: [],
        };
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    expect(await screen.findByLabelText("Workflow YAML panel")).toBeTruthy();
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: { value: "not: valid" },
    });

    await waitFor(() => {
      expect(studioApi.parseYaml).toHaveBeenCalledWith({
        yaml: "not: valid",
        availableStepTypes: expect.any(Array),
      });
    });
    expect(screen.getByLabelText("Workflow YAML panel")).toBeTruthy();
    expect(screen.getByText("Invalid workflow YAML")).toBeTruthy();
    expect(await screen.findByLabelText("Workflow YAML editor")).toHaveValue("not: valid");
    expect(screen.getByRole("button", { name: "Apply to draft" })).toBeDisabled();
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    expect(screen.getByRole("button", { name: "node:step:triage" })).toBeTruthy();
    expect(screen.queryByText("Unsaved changes")).toBeNull();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
  });

  it("blocks applying YAML with error-level findings", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.parseYaml as jest.Mock).mockImplementation(
      async ({ yaml }: { yaml: string }) => ({
        document: {
          ...mockWorkflowDocument,
          name: yaml.includes("unknown_field")
            ? "Rejected workflow"
            : "Workflow Alpha",
        },
        findings: yaml.includes("unknown_field")
          ? [
              {
                level: 2,
                path: "/steps/0/unknown_field",
                message: "Unknown field 'unknown_field'.",
                code: "unknown_field",
              },
            ]
          : [],
      }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value:
          "name: Rejected workflow\nsteps:\n  - id: triage\n    type: llm_call\n    unknown_field: true\n",
      },
    });

    await waitFor(() => {
      expect(studioApi.parseYaml).toHaveBeenCalledWith(
        expect.objectContaining({
          yaml: expect.stringContaining("unknown_field"),
        }),
      );
    });
    expect(await screen.findByText(/Unknown field 'unknown_field'\./)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Apply to draft" })).toBeDisabled();
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    expect(screen.queryByText("Unsaved changes")).toBeNull();
  });

  it("ignores stale YAML validation responses", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    let resolveBad: ((value: unknown) => void) | null = null;
    let resolveGood: ((value: unknown) => void) | null = null;
    (studioApi.parseYaml as jest.Mock).mockImplementation(
      ({ yaml }: { yaml: string }) => {
        if (yaml.includes("bad-step")) {
          return new Promise((resolve) => {
            resolveBad = resolve;
          });
        }

        if (yaml.includes("good-step")) {
          return new Promise((resolve) => {
            resolveGood = resolve;
          });
        }

        return Promise.resolve({
          document: mockWorkflowDocument,
          findings: [],
        });
      },
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    fireEvent.change(editor, {
      target: {
        value: "name: bad-step\nsteps:\n  - id: bad-step\n    type: llm_call\n",
      },
    });
    await waitFor(() => {
      expect(resolveBad).toBeTruthy();
    });
    fireEvent.change(editor, {
      target: {
        value: "name: good-step\nsteps:\n  - id: good-step\n    type: llm_call\n",
      },
    });
    await waitFor(() => {
      expect(resolveGood).toBeTruthy();
    });

    await act(async () => {
      resolveGood?.({
        document: {
          ...mockWorkflowDocument,
          name: "good-step",
          steps: [
            {
              id: "good-step",
              type: "llm_call",
              targetRole: "assistant",
              parameters: {},
              next: null,
              branches: {},
            },
          ],
        },
        findings: [],
      });
      resolveBad?.({
        document: mockWorkflowDocument,
        findings: [
          {
            level: 2,
            path: "/steps/0/type",
            message: "bad-step is invalid.",
          },
        ],
      });
    });

    await waitFor(() => {
      expect(screen.queryByText("bad-step is invalid.")).toBeNull();
      expect(screen.getByRole("button", { name: "Apply to draft" })).toBeEnabled();
    });
  });

  it("refreshes an unchanged YAML buffer after the draft revision advances", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const titleInput = await screen.findByLabelText("Workflow title");
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeDisabled();
    });

    fireEvent.change(titleInput, {
      target: { value: "Renamed member" },
    });

    await waitFor(() => {
      expect((editor as HTMLTextAreaElement).value).toContain(
        "name: Renamed member",
      );
      expect(applyButton).toBeDisabled();
      expect(
        screen.queryByText(
          "This YAML buffer is stale because the canvas or source draft changed.",
        ),
      ).toBeNull();
    });
    expect(titleInput).toHaveValue("Renamed member");
  });

  it("blocks edited YAML after the draft revision advances", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/new/workflow",
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const titleInput = await screen.findByLabelText("Workflow title");
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    fireEvent.change(editor, {
      target: { value: "name: YAML title\nsteps:\n" },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });

    fireEvent.change(titleInput, {
      target: { value: "Canvas title" },
    });

    await waitFor(() => {
      expect(editor).toHaveValue("name: YAML title\nsteps:\n");
      expect(applyButton).toBeDisabled();
      expect(
        screen.getByText(
          "This YAML buffer is stale because the canvas or source draft changed.",
        ),
      ).toBeTruthy();
    });
  });

  it("keeps a declined source refresh pending until YAML edits are discarded", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    const loadedWorkflow = {
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "workflow-alpha.yaml",
      filePath: "scope://scope-1/workflow-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    };
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue(loadedWorkflow);
    const confirmSpy = jest.spyOn(window, "confirm").mockReturnValue(false);
    const { queryClient } = renderWithQueryClient(
      React.createElement(TeamMemberWorkflowStudioPage),
    );

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    const editor = await screen.findByLabelText("Workflow YAML editor");
    fireEvent.change(editor, {
      target: {
        value:
          "name: Local YAML edit\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });

    act(() => {
      queryClient.setQueryData(
        [
          "team-member-workflow-studio",
          "workflow",
          "scope-1",
          "workflow-alpha",
          "draft",
        ],
        {
          ...loadedWorkflow,
          document: {
            ...mockWorkflowDocument,
            name: "Server workflow",
            steps: [
              ...mockWorkflowDocument.steps,
              {
                id: "guard",
                type: "guard",
                targetRole: "",
                parameters: { check: "not_empty" },
                next: null,
                branches: {},
              },
            ],
          },
          name: "Server workflow",
          updatedAtUtc: "2026-06-08T00:00:01Z",
          yaml:
            "name: Server workflow\nsteps:\n  - id: triage\n    type: llm_call\n  - id: guard\n    type: guard\n",
        },
      );
    });

    await waitFor(() => {
      expect(confirmSpy).toHaveBeenCalledTimes(1);
      expect(applyButton).toBeDisabled();
      expect(
        screen.getByText(
          "This YAML buffer is stale because the canvas or source draft changed.",
        ),
      ).toBeTruthy();
    });
    expect((editor as HTMLTextAreaElement).value).toContain("Local YAML edit");
    expect(screen.getByLabelText("Workflow title")).toHaveValue("Workflow Alpha");
    expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");

    confirmSpy.mockReturnValue(true);
    fireEvent.click(screen.getByRole("button", { name: "canvas" }));

    await waitFor(() => {
      expect(confirmSpy).toHaveBeenCalledTimes(2);
      expect(screen.queryByLabelText("Workflow YAML panel")).toBeNull();
      expect(screen.getByLabelText("Workflow title")).toHaveValue(
        "Server workflow",
      );
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:2");
    });
    confirmSpy.mockRestore();
  });

  it("preserves unchanged step layout and adds new YAML steps deterministically", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      layout: {
        nodePositions: {
          triage: { x: 900, y: 320 },
          deleted: { x: 12, y: 34 },
        },
        viewport: { x: 10, y: 20, zoom: 0.8 },
      },
      name: "Workflow Alpha",
      workflowId: "workflow-alpha",
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
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
      name: "Layout Applied",
      workflowId: "workflow-alpha",
      yaml: "name: Layout Applied\nsteps:\n  - id: triage\n    type: llm_call\n  - id: guard\n    type: guard\n",
      document: null,
      updatedAtUtc: "2026-06-08T00:00:02Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value:
          "name: Layout Applied\nsteps:\n  - id: triage\n    type: llm_call\n  - id: guard\n    type: guard\n",
      },
    });
    const applyButton = screen.getByRole("button", { name: "Apply to draft" });
    await waitFor(() => {
      expect(applyButton).toBeEnabled();
    });
    fireEvent.click(applyButton);
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:2");
    });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          workflowName: "Layout Applied",
          layout: expect.objectContaining({
            nodePositions: expect.objectContaining({
              triage: { x: 900, y: 320 },
              guard: expect.objectContaining({
                x: expect.any(Number),
                y: expect.any(Number),
              }),
            }),
          }),
        }),
      );
    });
    const savedLayout = (studioApi.saveWorkflow as jest.Mock).mock.calls[0][0]
      .layout;
    expect(savedLayout.nodePositions.deleted).toBeUndefined();
  });

  it("confirms before discarding unapplied YAML edits when returning to canvas", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    const confirmSpy = jest
      .spyOn(window, "confirm")
      .mockReturnValueOnce(false)
      .mockReturnValueOnce(true);

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickYamlAction("Edit YAML");
    fireEvent.change(await screen.findByLabelText("Workflow YAML editor"), {
      target: {
        value:
          "name: Edited but unapplied\nsteps:\n  - id: triage\n    type: llm_call\n",
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "canvas" }));
    expect(confirmSpy).toHaveBeenCalledWith(
      "You have unapplied YAML edits. Discard them and return to the canvas?",
    );
    expect(screen.getByLabelText("Workflow YAML panel")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "canvas" }));
    await waitFor(() => {
      expect(screen.queryByLabelText("Workflow YAML panel")).toBeNull();
    });
    confirmSpy.mockRestore();
  });

  it("does not clear newer dirty state when an older save completes", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    let resolveSave: ((workflow: unknown) => void) | null = null;
    (studioApi.saveWorkflow as jest.Mock).mockReturnValue(
      new Promise((resolve) => {
        resolveSave = resolve;
      }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    const titleInput = await screen.findByLabelText("Workflow title");
    fireEvent.change(titleInput, { target: { value: "First save title" } });
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledTimes(1);
    });
    fireEvent.change(titleInput, { target: { value: "Newer dirty title" } });

    await act(async () => {
      resolveSave?.({
        directoryId: "scope:scope-1",
        directoryLabel: "scope-1",
        draftExists: true,
        fileName: "workflow-alpha.yaml",
        filePath: "scope://scope-1/workflow-alpha.yaml",
        findings: [],
        layout: null,
        name: "First save title",
        workflowId: "workflow-alpha",
        yaml: "name: First save title\nsteps:\n  - id: triage\n    type: llm_call\n",
        document: null,
        updatedAtUtc: "2026-06-08T00:00:02Z",
      });
    });

    await waitFor(() => {
      expect(screen.getByText("Unsaved changes")).toBeTruthy();
      expect(screen.getByLabelText("Workflow title")).toHaveValue(
        "Newer dirty title",
      );
    });
  });

  it("blocks saving when serialized draft diagnostics contain errors", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      yaml: "name: Workflow Alpha\nsteps:\n  - id: triage\n    type: llm_call\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.serializeYaml as jest.Mock).mockResolvedValue({
      document: mockWorkflowDocument,
      findings: [
        {
          level: 2,
          path: "/",
          message: "Runtime validation rejected this YAML.",
          code: "runtime_validation",
        },
      ],
      yaml: "name: Workflow Alpha renamed\nsteps:\n  - id: triage\n    type: llm_call\n",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha renamed" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(studioApi.serializeYaml).toHaveBeenCalled();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(screen.getByText("Unsaved changes")).toBeTruthy();
  });

  it("loads only the route workflow id even when member detail has binding facts", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "workflow-from-member-detail",
        workflowRevision: "rev-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "member-alpha",
        publishedServiceId: "service-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "service-alpha",
        revisionId: "rev-alpha",
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
    (runtimeRunsApi.streamDraftRun as jest.Mock).mockResolvedValue(
      createSseResponse(),
    );
    (parseBackendSSEStream as jest.Mock).mockReturnValue(createWorkflowInvokeEvents());

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    const runDraftButton = await screen.findByRole("button", {
      name: "Run",
    });
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(studioApi.getWorkflow).toHaveBeenCalledWith(
      "workflow-alpha",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalledWith(
      "workflow-from-member-detail",
      "scope-1",
    );
    expect(studioApi.listWorkflows).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();

    await waitFor(() => {
      expect(runDraftButton).toBeEnabled();
    });
    fireEvent.click(runDraftButton);
    const draftRunPanel = await screen.findByLabelText("Draft run panel");
    fireEvent.click(
      within(draftRunPanel).getByRole("button", { name: "Start draft run" }),
    );

    await waitFor(() => {
      expect(runtimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "",
          workflowYamls: [expect.stringContaining("name: Workflow Alpha")],
        }),
        expect.any(AbortSignal),
      );
    });
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it("does not recover a workflow draft from published service or revision facts when the member read model omits the draft link", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "",
        workflowRevision: "rev-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-alpha",
        lifecycleStage: "bind_ready",
        memberId: "member-alpha",
        publishedServiceId: "service-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "service-alpha",
        revisionId: "rev-alpha",
      },
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("Workflow Alpha")).toBeTruthy();
    expect(
      await screen.findAllByText(
        "No workflow draft is linked to this member yet.",
      ),
    ).not.toHaveLength(0);
    expect(
      screen.getByText(
        "This published member has no materialized draft workflow link. Refresh after the member read model exposes its draft workflow id.",
      ),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save" })).toBeDisabled();
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
    expect(studioApi.listWorkflows).not.toHaveBeenCalled();
    expect(scopeRuntimeApi.listServices).not.toHaveBeenCalled();
    expect(runtimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(runtimeRunsApi.streamChat).not.toHaveBeenCalled();
  });

  it("requires a fresh explicit-request confirmation before publishing an existing workflow member", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=workflow-alpha",
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
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
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
      memberId: "m-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-1",
      memberId: "m-alpha",
      scopeId: "scope-1",
      status: "succeeded",
      stateVersion: 2,
      updatedAt: "2026-06-08T00:00:02Z",
    });
    (studioApi.previewExplicitRequests as jest.Mock).mockImplementation(
      (input: { workflowId: string; revisionId: string }) => ({
        workflowId: input.workflowId,
        revisionId: input.revisionId,
        items: [
          {
            callSiteId: "wf-alpha/request-alpha",
            requestContractDigest: "digest-alpha",
            userServiceId: "usvc-alpha",
            method: "post",
            pathTemplate: "/records/{id}",
            bodyMode: "json",
            bodyRequired: true,
            responseMode: "text",
            effectiveRisk: "write",
            approvalRequired: true,
            allowedExecutionModes: ["interactive"],
          },
        ],
      }),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha Published" },
    });
    expect(screen.queryByRole("switch")).toBeNull();
    clickPublishAction();

    await waitFor(() => {
      expect(studioApi.previewExplicitRequests).toHaveBeenCalledWith({
        executionMode: "interactive",
        inlineWorkflowYamls: {},
        revisionId: expect.stringMatching(/^rev-/),
        scopeId: "scope-1",
        workflowId: "workflow-alpha",
        workflowYaml: expect.stringContaining("name: Workflow Alpha Published"),
      });
      expect(Modal.confirm).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Review external requests",
        }),
      );
    });
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();

    const confirmationConfig = (Modal.confirm as jest.Mock).mock.calls[0]?.[0];
    await act(async () => {
      confirmationConfig.onCancel();
    });
    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
      expect(screen.getByRole("button", { name: "Publish" })).toBeEnabled();
    });

    clickPublishAction();
    await waitFor(() => {
      expect(Modal.confirm).toHaveBeenCalledTimes(2);
    });
    const secondPreviewInput = (studioApi.previewExplicitRequests as jest.Mock).mock
      .calls[1]?.[0];
    const reconfirmationConfig = (Modal.confirm as jest.Mock).mock.calls[1]?.[0];
    await act(async () => {
      await reconfirmationConfig.onOk();
    });

    await waitFor(() => {
      expect(studioApi.saveWorkflow).toHaveBeenCalledWith(
        expect.objectContaining({
          scopeId: "scope-1",
          workflowId: "workflow-alpha",
          workflowName: "Workflow Alpha Published",
        }),
      );
      expect(studioApi.updateMemberDisplayName).toHaveBeenCalledWith({
        displayName: "Workflow Alpha Published",
        memberId: "m-alpha",
        scopeId: "scope-1",
      });
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: "Workflow Alpha Published",
        memberId: "m-alpha",
        scopeId: "scope-1",
        workflowId: "workflow-alpha",
        revisionId: secondPreviewInput.revisionId,
        workflowYamls: [expect.stringContaining("name: Workflow Alpha Published")],
        explicitRequestConfirmations: [
          {
            workflowId: "workflow-alpha",
            revisionId: secondPreviewInput.revisionId,
            callSiteId: "wf-alpha/request-alpha",
            requestContractDigest: "digest-alpha",
            attestedRisk: "write",
          },
        ],
      });
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        "binding-run-1",
      );
    });
    await waitFor(() => {
      expect(screen.getByText("Published")).toBeTruthy();
      expect(screen.getByTitle(/Published member workflow is serviceable/)).toBeTruthy();
    });
    const memberBindingInput = (studioApi.bindMemberWorkflow as jest.Mock).mock
      .calls[0]?.[0];
    expect(memberBindingInput.revisionId).toBe(secondPreviewInput.revisionId);
    expect(memberBindingInput.explicitRequestConfirmations[0]?.revisionId).toBe(
      secondPreviewInput.revisionId,
    );
    expect(memberBindingInput.workflowYamls).toEqual([
      secondPreviewInput.workflowYaml,
    ]);
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("blocks publish when selected node configuration is invalid", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha Published" },
    });
    fireEvent.click(screen.getByRole("button", { name: "node:step:triage" }));
    fireEvent.change(screen.getByLabelText("Instruction"), {
      target: { value: "" },
    });

    expect(await screen.findByText("Instruction is required.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
    clickPublishAction();

    await flushAsyncWork();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("reports rejected publish binding without introducing activation language", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickPublishAction();

    await waitFor(() => {
      expect(screen.getByText("Error")).toBeTruthy();
      expect(
        screen.getByTitle(
          /Binding run was rejected by the member authority/,
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
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
        expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      });
      clickPublishAction();

      await waitFor(() => {
        expect(screen.getByText("Binding")).toBeTruthy();
      });
      for (let index = 0; index < 8; index += 1) {
        await act(async () => {
          jest.advanceTimersByTime(900);
          await flushAsyncWork();
        });
      }

      await waitFor(() => {
        expect(screen.getByText("Binding")).toBeTruthy();
      });
      expect(
        screen.getAllByTitle(/Binding run is still in progress/).length,
      ).toBeGreaterThan(0);
      expect(screen.getByRole("button", { name: "Refresh status" })).toBeEnabled();
      expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledTimes(8);
    } finally {
      await act(async () => {
        jest.runOnlyPendingTimers();
        await flushAsyncWork();
      });
      jest.useRealTimers();
    }
  });

  it("keeps the publish action loading during automatic polling and surfaces polling failures", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      bindingRunId: "binding-run-polling",
      memberId: "member-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock)
      .mockRejectedValueOnce(
        new StudioApiError("Binding run is not materialized.", 404),
      )
      .mockResolvedValueOnce({
        bindingRunId: "binding-run-polling",
        memberId: "member-alpha",
        scopeId: "scope-1",
        status: "accepted",
        stateVersion: 1,
        updatedAt: "2026-06-08T00:00:02Z",
      })
      .mockRejectedValueOnce(new StudioApiError("Bad Gateway", 502));

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    clickPublishAction();

    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalled();
      expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
      expect(screen.getByText("Binding")).toBeTruthy();
      expect(
        screen.getAllByTitle(/Binding candidate accepted for dispatch/).length,
      ).toBeGreaterThan(0);
      expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    });

    await waitFor(
      () => {
        expect(studioApi.getMemberBindingRun).toHaveBeenCalledTimes(2);
        expect(screen.getByRole("button", { name: "Publish" })).toBeDisabled();
      },
      { timeout: 2_000 },
    );
    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    });

    await waitFor(
      () => {
        expect(studioApi.getMemberBindingRun).toHaveBeenCalledTimes(3);
        expect(screen.getByText("Error")).toBeTruthy();
        expect(screen.getByTitle(/Bad Gateway/)).toBeTruthy();
      },
      { timeout: 2_000 },
    );
    expect(screen.queryByText("Binding")).toBeNull();
    expect(screen.getByRole("button", { name: "Refresh status" })).toBeEnabled();
  });

  it("blocks duplicate publish for an already published workflow member without surfacing refresh status", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-1",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(screen.getByText("Published")).toBeTruthy();
      expect(
        screen.queryByRole("button", { name: "Publish" }),
      ).toBeNull();
      expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    });
    expect(screen.getByDisplayValue("Workflow Alpha")).toBeTruthy();
    expect(studioApi.getWorkflow).toHaveBeenCalledWith("wf-alpha", "scope-1");
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getMemberBindingRun).not.toHaveBeenCalled();
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("loads the published workflow for an already published member instead of a stale draft", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha&workflowSource=published",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "document_2",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-2",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:03Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:03Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-2",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "weekly_report_4",
      workflowId: "wf-alpha",
      yaml: "name: weekly_report_4\nsteps: []\n",
      document: {
        ...mockWorkflowDocument,
        name: "weekly_report_4",
      },
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.getPublishedWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: false,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "document_2",
      workflowId: "wf-alpha",
      yaml: "name: document_2\nsteps: []\n",
      document: {
        ...mockWorkflowDocument,
        name: "document_2",
      },
      updatedAtUtc: "2026-06-08T00:00:03Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    expect(await screen.findByDisplayValue("document_2")).toBeTruthy();
    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.queryByDisplayValue("weekly_report_4")).toBeNull();
    expect(studioApi.getPublishedWorkflow).toHaveBeenCalledWith(
      "wf-alpha",
      "scope-1",
    );
    expect(studioApi.getWorkflow).not.toHaveBeenCalled();
  });

  it("keeps publish hidden when an already published workflow member has draft changes", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-1",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(screen.getByText("Published")).toBeTruthy();
      expect(
        screen.queryByRole("button", { name: "Publish" }),
      ).toBeNull();
      expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    });
    expect(screen.getByDisplayValue("Workflow Alpha")).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha v2" },
    });

    await waitFor(() => {
      expect(screen.getByText("Unsaved changes")).toBeTruthy();
      expect(screen.getByRole("button", { name: "Save" })).toBeEnabled();
      expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
      expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getMemberBindingRun).not.toHaveBeenCalled();
    expect(studioApi.getPublishedWorkflow).not.toHaveBeenCalled();
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
  });

  it("saves and rebinds a changed published workflow member through save-and-bind", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha&workflowSource=published",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-1",
      },
    });
    const staleWorkflow = {
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    };
    const materializedWorkflow = {
      ...staleWorkflow,
      name: "Workflow Alpha v2",
      yaml: "name: Workflow Alpha v2\nsteps:\n  - id: triage\n    type: llm_call",
      document: {
        ...mockWorkflowDocument,
        name: "Workflow Alpha v2",
      },
      updatedAtUtc: "2026-06-08T00:00:03Z",
    };
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue(staleWorkflow);
    (studioApi.getPublishedWorkflow as jest.Mock)
      .mockResolvedValueOnce(staleWorkflow)
      .mockResolvedValueOnce(staleWorkflow)
      .mockResolvedValueOnce(staleWorkflow)
      .mockResolvedValue(materializedWorkflow);
    (studioApi.saveAndBindWorkflow as jest.Mock).mockResolvedValue({
      scopeId: "scope-1",
      workflowId: "wf-alpha",
      revisionId: "rev-2",
      workflow: {
        scopeId: "scope-1",
        workflowId: "wf-alpha",
        revisionId: "rev-2",
        readModelUrl: "/api/scopes/scope-1/workflows/wf-alpha",
        acceptanceStage: "accepted",
        propagationStage: "readmodel_propagating",
      },
      binding: {
        scopeId: "scope-1",
        serviceId: "svc-alpha",
        displayName: "Workflow Alpha v2",
        revisionId: "rev-2",
        implementationKind: "workflow",
        targetKind: "workflow",
        targetName: "Workflow Alpha v2",
      },
      acceptanceStage: "accepted",
      propagationStage: "readmodel_propagating",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(screen.getByText("Published")).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha v2" },
    });

    const saveButton = screen.getByRole("button", { name: "Save" });
    await waitFor(() => {
      expect(saveButton).toBeEnabled();
    });
    fireEvent.click(saveButton);

    await waitFor(() => {
      expect(studioApi.saveAndBindWorkflow).toHaveBeenCalledWith({
        appId: "studio",
        displayName: "Workflow Alpha v2",
        exposureDesired: true,
        inlineWorkflowYamls: {},
        scopeId: "scope-1",
        serviceId: "svc-alpha",
        workflowId: "wf-alpha",
        revisionId: expect.stringMatching(/^rev-/),
        workflowName: "Workflow Alpha v2",
        workflowYaml: expect.stringContaining("name: Workflow Alpha v2"),
      });
    });
    const previewInput = (studioApi.previewExplicitRequests as jest.Mock).mock.calls[0]?.[0];
    const saveAndBindInput = (studioApi.saveAndBindWorkflow as jest.Mock).mock.calls[0]?.[0];
    expect(previewInput.revisionId).not.toBe("rev-1");
    expect(saveAndBindInput.revisionId).toBe(previewInput.revisionId);
    expect(Modal.confirm).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getMemberBindingRun).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(studioApi.updateMemberDisplayName).toHaveBeenCalledWith({
        displayName: "Workflow Alpha v2",
        memberId: "m-alpha",
        scopeId: "scope-1",
      });
      expect(studioApi.getWorkflow).not.toHaveBeenCalled();
      expect(studioApi.getPublishedWorkflow).toHaveBeenCalledWith(
        "wf-alpha",
        "scope-1",
      );
      expect(screen.getByDisplayValue("Workflow Alpha v2")).toBeTruthy();
    });
  });

  it("surfaces an explicit-request preview failure without publishing", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha&workflowSource=published",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:00Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-1",
      },
    });
    (studioApi.getPublishedWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.previewExplicitRequests as jest.Mock).mockRejectedValue(
      new StudioApiError("Latest workflow preview failed.", 500),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
      expect(screen.getByText("Published")).toBeTruthy();
    });
    fireEvent.change(screen.getByLabelText("Workflow title"), {
      target: { value: "Workflow Alpha v2" },
    });
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => {
      expect(screen.getByText("Latest workflow preview failed.")).toBeTruthy();
    });
    expect(studioApi.previewExplicitRequests).toHaveBeenCalledWith({
      executionMode: "interactive",
      inlineWorkflowYamls: {},
      revisionId: expect.stringMatching(/^rev-/),
      scopeId: "scope-1",
      workflowId: "wf-alpha",
      workflowYaml: expect.stringContaining("name: Workflow Alpha v2"),
    });
    expect(studioApi.saveAndBindWorkflow).not.toHaveBeenCalled();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("refreshes a stale member current binding run from the binding-run read model", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "build_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      currentBindingRun: {
        bindingRunId: "binding-run-stale-member",
        memberId: "m-alpha",
        scopeId: "scope-1",
        stateVersion: 1,
        status: "accepted",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-stale-member",
      memberId: "m-alpha",
      scopeId: "scope-1",
      stateVersion: 2,
      status: "succeeded",
      updatedAt: "2026-06-08T00:00:10Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("Binding")).toBeTruthy();
    });

    const refreshButton = screen.getByRole("button", { name: "Refresh status" });
    await waitFor(() => {
      expect(refreshButton).not.toBeDisabled();
    });
    fireEvent.click(refreshButton);

    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        "binding-run-stale-member",
      );
      expect(screen.getByText("Published")).toBeTruthy();
    });
    expect(screen.queryByText("Binding")).toBeNull();
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
  });

  it("falls back to member current binding run when refresh read model is not materialized", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    const memberWithFallbackBindingRun = {
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "build_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      currentBindingRun: {
        bindingRunId: "binding-run-fallback",
        memberId: "m-alpha",
        scopeId: "scope-1",
        stateVersion: 1,
        status: "accepted",
        updatedAt: "2026-06-08T00:00:01Z",
      },
    };
    (studioApi.getMember as jest.Mock).mockResolvedValue(memberWithFallbackBindingRun);
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockRejectedValue(
      new StudioApiError("Binding run is not materialized.", 404),
    );

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("Binding")).toBeTruthy();
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockClear();

    const refreshButton = screen.getByRole("button", { name: "Refresh status" });
    await waitFor(() => {
      expect(refreshButton).not.toBeDisabled();
    });
    fireEvent.click(refreshButton);

    await waitFor(() => {
      expect(studioApi.getMemberBindingRun).toHaveBeenCalledWith(
        "scope-1",
        "m-alpha",
        "binding-run-fallback",
      );
      expect(screen.getByText("Binding")).toBeTruthy();
      expect(
        screen.getAllByTitle(/Binding run is still in progress/).length,
      ).toBeGreaterThan(0);
    });
    expect(screen.getByRole("button", { name: "Refresh status" })).toBeEnabled();
    expect(screen.queryByText("Error")).toBeNull();
  });

  it("updates stale publish eligibility from the preflight member read before side effects", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    const initialUnpublishedMember = {
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "build_ready",
        memberId: "m-alpha",
        publishedServiceId: "",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
    };
    const refreshedPublishedMember = {
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: "rev-1",
        lifecycleStage: "bind_ready",
        memberId: "m-alpha",
        publishedServiceId: "svc-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:01Z",
      },
      lastBinding: {
        boundAt: "2026-06-08T00:00:01Z",
        implementationKind: "workflow",
        publishedServiceId: "svc-alpha",
        revisionId: "rev-1",
      },
    };
    (studioApi.getMember as jest.Mock)
      .mockResolvedValueOnce(initialUnpublishedMember)
      .mockResolvedValueOnce(refreshedPublishedMember);
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    const publishButton = screen.getByRole("button", { name: "Publish" });
    await waitFor(() => {
      expect(publishButton).toBeEnabled();
    });
    fireEvent.click(publishButton);

    await waitFor(() => {
      expect(studioApi.getMember).toHaveBeenCalledTimes(2);
      expect(screen.getByText("Published")).toBeTruthy();
    });
    expect(studioApi.getMember).toHaveBeenLastCalledWith("scope-1", "m-alpha");
    expect(screen.queryByText("Error")).toBeNull();
    expect(screen.queryByRole("button", { name: "Publish" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Refresh status" })).toBeNull();
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
    expect(studioApi.getMemberBindingRun).not.toHaveBeenCalled();
  });

  it("allows publishing when the member has a service identity but no completed binding fact", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/m-alpha/workflow?workflowId=wf-alpha",
    );
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      implementationRef: {
        implementationKind: "workflow",
        workflowId: "wf-alpha",
      },
      summary: {
        createdAt: "2026-06-08T00:00:00Z",
        description: "",
        displayName: "Workflow Alpha",
        implementationKind: "workflow",
        lastBoundRevisionId: null,
        lifecycleStage: "created",
        memberId: "m-alpha",
        publishedServiceId: "member-m-alpha",
        scopeId: "scope-1",
        teamId: "t-alpha",
        updatedAt: "2026-06-08T00:00:00Z",
      },
      lastBinding: null,
    });
    (studioApi.getWorkflow as jest.Mock).mockResolvedValue({
      directoryId: "scope:scope-1",
      directoryLabel: "scope-1",
      draftExists: true,
      fileName: "wf-alpha.yaml",
      filePath: "scope://scope-1/wf-alpha.yaml",
      findings: [],
      layout: null,
      name: "Workflow Alpha",
      workflowId: "wf-alpha",
      yaml: "name: Workflow Alpha\nsteps: []\n",
      document: mockWorkflowDocument,
      updatedAtUtc: "2026-06-08T00:00:00Z",
    });
    (studioApi.bindMemberWorkflow as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-identity-only",
      memberId: "m-alpha",
      scopeId: "scope-1",
      status: "accepted",
    });
    (studioApi.getMemberBindingRun as jest.Mock).mockResolvedValue({
      bindingRunId: "binding-run-identity-only",
      memberId: "m-alpha",
      scopeId: "scope-1",
      status: "succeeded",
      stateVersion: 2,
      updatedAt: "2026-06-08T00:00:02Z",
    });

    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByText("nodes:1")).toBeTruthy();
    });
    expect(screen.getByText("Draft")).toBeTruthy();
    const publishButton = screen.getByRole("button", { name: "Publish" });
    await waitFor(() => {
      expect(publishButton).toBeEnabled();
    });

    fireEvent.click(publishButton);

    await waitFor(() => {
      expect(studioApi.bindMemberWorkflow).toHaveBeenCalledWith({
        displayName: "Workflow Alpha",
        memberId: "m-alpha",
        scopeId: "scope-1",
        workflowId: "wf-alpha",
        revisionId: expect.stringMatching(/^rev-/),
        workflowYamls: [expect.stringContaining("name: Workflow Alpha")],
      });
      expect(screen.getByText("Published")).toBeTruthy();
    });
    expect(studioApi.saveWorkflow).not.toHaveBeenCalled();
  });

  it("does not expose Team entry actions in workflow studio", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(studioApi.setTeamEntryMember).not.toHaveBeenCalled();
    expect(screen.queryByText("Set as Team entry")).toBeNull();
    expect(studioApi.bindMemberWorkflow).not.toHaveBeenCalled();
  });

  it("does not expose run history on the workflow editor page", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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
    renderWithQueryClient(React.createElement(TeamMemberWorkflowStudioPage));

    await waitFor(() => {
      expect(screen.getByTestId("graph-canvas")).toHaveTextContent("nodes:1");
    });
    expect(screen.queryByText("Runs")).toBeNull();
    expect(screen.queryByLabelText("Member runs")).toBeNull();
    expect(screen.queryByRole("button", { name: "Open run" })).toBeNull();
    expect(studioApi.listExecutions).not.toHaveBeenCalled();
    expect(studioApi.getExecution).not.toHaveBeenCalled();
  });

  it("keeps the editor page on the canvas when no stable member run owner exists", async () => {
    window.history.replaceState(
      {},
      "",
      "/scopes/scope-1/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
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

    expect(await screen.findByTestId("workflow-studio-canvas")).toBeTruthy();
    expect(screen.queryByText("Runs")).toBeNull();
    expect(studioApi.listExecutions).not.toHaveBeenCalled();
  });
});
