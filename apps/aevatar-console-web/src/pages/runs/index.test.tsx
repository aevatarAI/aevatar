import { screen } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import RunsPage from "./index";

jest.mock("@aevatar-react-sdk/agui", () => ({
  connectChatWebSocket: jest.fn(),
  parseSSEStream: jest.fn(),
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
    dispatch: jest.fn(),
    reset: jest.fn(),
  })),
}));

jest.mock("@aevatar-react-sdk/types", () => ({
  AGUIEventType: {
    RUN_ERROR: "RUN_ERROR",
  },
  CustomEventName: {
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
    streamChat: jest.fn(),
    resume: jest.fn(),
    signal: jest.fn(),
  },
}));

describe("RunsPage", () => {
  it("renders the runtime run console header and navigation actions", async () => {
    const { container } = renderWithQueryClient(React.createElement(RunsPage));

    expect(container.textContent).toContain("Runtime run console");
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
});
