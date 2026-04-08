import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import {
  loadDraftRunPayload,
  saveDraftRunPayload,
  saveEndpointInvocationDraftPayload,
  saveObservedRunSessionPayload,
} from "@/shared/runs/draftRunSession";
import { saveRecentRun } from "@/shared/runs/recentRuns";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import RunsPage from "./index";

const mockDispatch = jest.fn();
const mockReset = jest.fn();
const mockSession = {
  context: undefined,
  status: "idle",
  messages: [],
  events: [],
  activeSteps: new Set<string>(),
  pendingHumanInput: undefined,
  runId: "",
  error: undefined,
};

jest.mock("@aevatar-react-sdk/agui", () => ({
  connectChatWebSocket: jest.fn(),
  parseSSEStream: jest.fn(() => (async function* () {})()),
  useHumanInteraction: jest.fn(() => ({
    resume: jest.fn(),
    signal: jest.fn(),
    resuming: false,
    signaling: false,
  })),
  useRunSession: jest.fn(() => ({
    session: mockSession,
    dispatch: mockDispatch,
    reset: mockReset,
  })),
}));

jest.mock("@aevatar-react-sdk/types", () => ({
  AGUIEventType: {
    CUSTOM: "CUSTOM",
    RUN_FINISHED: "RUN_FINISHED",
    RUN_STARTED: "RUN_STARTED",
    RUN_ERROR: "RUN_ERROR",
  },
  CustomEventName: {
    RunContext: "aevatar.run.context",
    WaitingSignal: "WaitingSignal",
    StepRequest: "StepRequest",
  },
}));

jest.mock("@/shared/api/runtimeCatalogApi", () => ({
  runtimeCatalogApi: {
    listWorkflowCatalog: jest.fn(async () => []),
  },
}));

jest.mock("@/shared/agui/sseFrameNormalizer", () => ({
  parseBackendSSEStream: jest.fn(() => (async function* () {})()),
}));

jest.mock("@/shared/api/runtimeActorsApi", () => ({
  runtimeActorsApi: {
    getActorSnapshot: jest.fn(),
  },
}));

jest.mock("@/shared/api/runtimeRunsApi", () => ({
  runtimeRunsApi: {
    invokeEndpoint: jest.fn(),
    streamChat: jest.fn(),
    streamDraftRun: jest.fn(),
    resume: jest.fn(),
    signal: jest.fn(),
    stop: jest.fn(),
  },
}));

function getButtonByText(label: string): HTMLButtonElement {
  const button = screen
    .getAllByText((_, element) => element?.textContent?.trim() === label)
    .map((element) =>
      element instanceof HTMLButtonElement ? element : element.closest("button")
    )
    .find((element): element is HTMLButtonElement => element instanceof HTMLButtonElement);

  if (!button) {
    throw new Error(`Unable to find button with text '${label}'.`);
  }

  return button;
}

describe("RunsPage", () => {
  const mockedRuntimeCatalogApi = runtimeCatalogApi as unknown as {
    listWorkflowCatalog: jest.Mock;
  };
  const mockedRuntimeRunsApi = runtimeRunsApi as unknown as {
    invokeEndpoint: jest.Mock;
    streamChat: jest.Mock;
    streamDraftRun: jest.Mock;
    resume: jest.Mock;
    signal: jest.Mock;
    stop: jest.Mock;
  };

  beforeEach(() => {
    window.history.replaceState({}, "", "/runtime/runs");
    window.sessionStorage.clear();
    window.localStorage.clear();
    jest.clearAllMocks();
    mockDispatch.mockReset();
    mockReset.mockReset();
    mockSession.context = undefined;
    mockSession.status = "idle";
    mockSession.messages = [];
    mockSession.events = [];
    mockSession.activeSteps = new Set<string>();
    mockSession.pendingHumanInput = undefined;
    mockSession.runId = "";
    mockSession.error = undefined;
    mockedRuntimeRunsApi.invokeEndpoint.mockResolvedValue({
      requestId: "cmd-1",
      targetActorId: "actor-1",
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
    });
    mockedRuntimeRunsApi.streamChat.mockResolvedValue({
      ok: true,
      body: {},
    });
    mockedRuntimeRunsApi.streamDraftRun.mockResolvedValue({});
    mockedRuntimeCatalogApi.listWorkflowCatalog.mockResolvedValue([]);
  });

  it("renders the runtime run console header and navigation actions", async () => {
    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    expect(container.textContent).toContain("Runtime endpoint console");
    expect(screen.getByLabelText("Open runtime console guide")).toBeTruthy();
    expect(screen.getByText("Catalog")).toBeTruthy();
    expect(screen.getByText("Explorer")).toBeTruthy();
    expect(screen.queryByLabelText("Open observability hub")).toBeNull();
    expect(screen.getByText("Inspector")).toBeTruthy();
    expect(screen.getByLabelText("Scope ID")).toBeTruthy();
    expect(container.textContent).toContain("Launch rail");
    expect(container.textContent).toContain("Run trace");
    expect(container.textContent).toContain("Inspector");
  });

  it("uses the generic invoke path for prepared service invocation drafts", async () => {
    const draftKey = saveEndpointInvocationDraftPayload({
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
      prompt: "script payload",
      payloadTypeUrl: "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
      payloadBase64: "CgBSCnNjcmlwdCBwYXlsb2Fk",
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&endpointId=aevatar.tools.cli.hosting.AppScriptCommand&draftKey=${draftKey}`
    );

    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("script payload");
    const form = container.querySelector("form");
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.invokeEndpoint).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
          prompt: "script payload",
          payloadTypeUrl:
            "type.googleapis.com/aevatar.tools.cli.hosting.AppScriptCommand",
          payloadBase64: "CgBSCnNjcmlwdCBwYXlsb2Fk",
        }),
        {
          serviceId: undefined,
        }
      );
    });
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(mockDispatch).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "cmd-1",
      })
    );
    expect(mockDispatch).not.toHaveBeenCalledWith(
      expect.objectContaining({
        type: "RUN_FINISHED",
      })
    );
  });

  it("auto-starts workflow draft runs handed off from Studio", async () => {
    const draftKey = saveDraftRunPayload({
      workflowName: "workspace-demo",
      workflowYamls: ["name: workspace-demo\nsteps:\n  - id: review_step\n"],
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&route=workspace-demo&prompt=Run%20the%20draft&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("Run the draft");
    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run the draft",
          workflowYamls: [
            expect.stringContaining("name: workspace-demo"),
          ],
        }),
        expect.any(AbortSignal)
      );
    });

    expect(new URLSearchParams(window.location.search).get("draftKey")).toBeNull();
    expect(loadDraftRunPayload(draftKey)).toBeNull();
  });

  it("hydrates observed run sessions without starting a new invoke", async () => {
    const draftKey = saveObservedRunSessionPayload({
      scopeId: "scope-1",
      serviceOverrideId: "svc-1",
      endpointId: "chat",
      prompt: "hello observed run",
      actorId: "actor-1",
      commandId: "cmd-1",
      runId: "run-1",
      events: [
        {
          type: "RUN_STARTED",
          runId: "run-1",
          threadId: "thread-1",
          timestamp: Date.now(),
        } as any,
        {
          type: "CUSTOM",
          name: "aevatar.run.context",
          value: {
            actorId: "actor-1",
            commandId: "cmd-1",
            workflowName: "chat",
          },
          timestamp: Date.now(),
        } as any,
      ],
    });
    window.history.replaceState(
      {},
      "",
      `/runtime/runs?scopeId=scope-1&endpointId=chat&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("hello observed run");
    await waitFor(() => {
      expect(mockReset).toHaveBeenCalled();
      expect(mockDispatch).toHaveBeenCalledTimes(2);
    });

    expect(mockDispatch).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "run-1",
      })
    );
    expect(mockDispatch).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        type: "CUSTOM",
        name: "aevatar.run.context",
      })
    );
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
    expect(new URLSearchParams(window.location.search).get("draftKey")).toBeNull();
    expect(loadDraftRunPayload(draftKey)).toBeNull();
  });

  it("replays observed logs when restoring a recent run", async () => {
    saveRecentRun({
      id: "cmd-recent",
      scopeId: "scope-1",
      routeName: "direct",
      endpointId: "chat",
      prompt: "recent replay",
      actorId: "actor-1",
      commandId: "cmd-1",
      runId: "run-1",
      status: "finished",
      observedEvents: [
        {
          type: "RUN_STARTED",
          runId: "run-1",
          threadId: "thread-1",
          timestamp: Date.now(),
        } as any,
        {
          type: "CUSTOM",
          name: "aevatar.run.context",
          value: {
            actorId: "actor-1",
            commandId: "cmd-1",
            workflowName: "direct",
          },
          timestamp: Date.now(),
        } as any,
      ],
    });

    renderWithQueryClient(React.createElement(RunsPage));

    mockDispatch.mockClear();
    mockReset.mockClear();

    fireEvent.click(screen.getByText("Recent (1)"));
    fireEvent.click(getButtonByText("Restore"));

    await waitFor(() => {
      expect(mockReset).toHaveBeenCalled();
      expect(mockDispatch).toHaveBeenCalledTimes(2);
    });

    expect(mockDispatch).toHaveBeenNthCalledWith(
      1,
      expect.objectContaining({
        type: "RUN_STARTED",
        runId: "run-1",
      })
    );
    expect(mockDispatch).toHaveBeenNthCalledWith(
      2,
      expect.objectContaining({
        type: "CUSTOM",
        name: "aevatar.run.context",
      })
    );
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamChat).not.toHaveBeenCalled();
    expect(mockedRuntimeRunsApi.streamDraftRun).not.toHaveBeenCalled();
  });

  it("routes chat runs through the selected workflow service when endpoint kind is chat", async () => {
    mockedRuntimeCatalogApi.listWorkflowCatalog.mockResolvedValue([
      {
        name: "direct",
        description: "Direct chat workflow",
        category: "core",
        group: "default",
        groupLabel: "Default",
        sortOrder: 0,
        source: "built-in",
        sourceLabel: "Built-in",
        showInLibrary: true,
        isPrimitiveExample: false,
        requiresLlmProvider: true,
        primitives: [],
      },
    ]);

    window.history.replaceState(
      {},
      "",
      "/runtime/runs?scopeId=scope-1&route=direct&endpointId=support-chat&endpointKind=chat&prompt=Run%20it"
    );

    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("Run it");
    const form = container.querySelector("form");
    expect(form).toBeTruthy();
    fireEvent.submit(form!);

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamChat).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run it",
        }),
        expect.any(AbortSignal),
        {
          serviceId: "direct",
        }
      );
    });
    expect(mockedRuntimeRunsApi.invokeEndpoint).not.toHaveBeenCalled();
  });
});
