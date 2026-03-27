import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import {
  loadDraftRunPayload,
  saveDraftRunPayload,
  saveServiceInvocationDraftPayload,
} from "@/shared/runs/draftRunSession";
import { runtimeRunsApi } from "@/shared/api/runtimeRunsApi";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import RunsPage from "./index";

const mockDispatch = jest.fn();
const mockReset = jest.fn();

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
    session: {
      context: undefined,
      status: "idle",
      messages: [],
      events: [],
      activeSteps: new Set<string>(),
      pendingHumanInput: undefined,
      runId: "",
      error: undefined,
    },
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

describe("RunsPage", () => {
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
    jest.clearAllMocks();
    mockDispatch.mockReset();
    mockReset.mockReset();
    mockedRuntimeRunsApi.invokeEndpoint.mockResolvedValue({
      requestId: "cmd-1",
      targetActorId: "actor-1",
      endpointId: "aevatar.tools.cli.hosting.AppScriptCommand",
    });
    mockedRuntimeRunsApi.streamDraftRun.mockResolvedValue({});
  });

  it("renders the runtime run console header and navigation actions", async () => {
    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    expect(container.textContent).toContain("Runtime service endpoint console");
    expect(
      screen.getByRole("button", { name: "Open runtime console guide" })
    );
    expect(
      screen.getByRole("button", { name: "Open Runtime Workflows" })
    ).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Open Runtime Explorer" })
    ).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Open observability hub" })
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Inspector" })).toBeTruthy();
    expect(container.textContent).toContain("Launch rail");
    expect(container.textContent).toContain("Run trace");
    expect(container.textContent).toContain("Inspector");
  });

  it("uses the generic invoke path for prepared service invocation drafts", async () => {
    const draftKey = saveServiceInvocationDraftPayload({
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

    renderWithQueryClient(React.createElement(RunsPage));

    await screen.findByDisplayValue("script payload");
    fireEvent.click(screen.getByRole("button", { name: "Start run" }));

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
      `/runtime/runs?scopeId=scope-1&workflow=workspace-demo&prompt=Run%20the%20draft&draftKey=${draftKey}`
    );

    renderWithQueryClient(React.createElement(RunsPage));

    await waitFor(() => {
      expect(mockedRuntimeRunsApi.streamDraftRun).toHaveBeenCalledWith(
        "scope-1",
        expect.objectContaining({
          prompt: "Run the draft",
          workflow: "workspace-demo",
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
});
