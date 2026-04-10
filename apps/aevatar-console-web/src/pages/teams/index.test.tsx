import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsIndexPage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
  },
}));

jest.mock("@/shared/config/consoleFeatures", () => ({
  isTeamFirstEnabled: jest.fn(() => true),
}));

jest.mock("./runtime/useTeamRuntimeLens", () => ({
  useTeamRuntimeLens: jest.fn(),
}));

import { isTeamFirstEnabled } from "@/shared/config/consoleFeatures";
import { studioApi } from "@/shared/studio/api";
import { useTeamRuntimeLens } from "./runtime/useTeamRuntimeLens";

const mockedIsTeamFirstEnabled = isTeamFirstEnabled as jest.Mock;

function createTeamLensResult(overrides?: Record<string, unknown>) {
  return {
    actorGraphQuery: {
      isError: false,
      isLoading: false,
    },
    baselineRunAuditQuery: {
      isError: false,
      isLoading: false,
    },
    bindingQuery: {
      isError: false,
      isLoading: false,
    },
    currentRunAuditQuery: {
      isError: false,
      isLoading: false,
    },
    lens: {
      baselineRun: {
        runId: "run-good",
      },
      compare: {
        summary: "Current run differs from the latest prior good run.",
      },
      currentBindingContext: "Serving revision rev-2 on default service.",
      currentBindingTarget: "Workflow support-triage",
      currentRun: {
        completionStatus: "waiting_approval",
        lastUpdatedAt: "2026-04-09T09:05:00Z",
        runId: "run-current",
      },
      graph: {
        available: true,
        focusActorId: "actor-intake",
        focusReason: "The latest visible run is still centered on actor-intake.",
        relationships: [
          {
            fromActorId: "actor-intake",
            toActorId: "actor-risk",
          },
        ],
        stageSummary:
          "This canvas shows the currently focused actor and the nearest visible collaboration paths around it.",
      },
      healthStatus: "blocked",
      healthSummary: "The team is waiting on human approval.",
      healthTone: "warning",
      humanInterventionDetected: true,
      members: [
        { actorId: "actor-intake", actorType: "workflow", isFocused: true },
        { actorId: "actor-risk", actorType: "workflow", isFocused: false },
      ],
      partialSignals: [],
      playback: {
        events: [],
        interactionLabel: "human_approval",
        prompt: "Approve escalation",
        summary: "Playback is centered on the current human gate.",
        steps: [],
      },
      recentRunCount: 4,
      subtitle: "Support escalations with human approval when risk spikes.",
      title: "Support Escalation Triage",
    },
    runsQuery: {
      isError: false,
      isLoading: false,
    },
    scriptsQuery: {
      isError: false,
      isLoading: false,
    },
    servicesQuery: {
      isError: false,
      isLoading: false,
    },
    workflowsQuery: {
      isError: false,
      isLoading: false,
    },
    ...overrides,
  };
}

describe("TeamsIndexPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockedIsTeamFirstEnabled.mockReturnValue(true);
    window.history.replaceState({}, "", "/teams");
    (useTeamRuntimeLens as jest.Mock).mockReturnValue(createTeamLensResult());
  });

  it("renders the honest single-team roster when the feature is enabled", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "scope-team",
      scopeSource: "claim:scope_id",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(
      await screen.findByRole("button", { name: "View details" }),
    ).toBeTruthy();
    expect(screen.getByText("Reference roster")).toBeTruthy();
    expect(screen.getByText("Current session team only")).toBeTruthy();
    expect(screen.getByText("Why now")).toBeTruthy();
    expect(screen.getByText("Support Escalation Triage")).toBeTruthy();
    expect(screen.queryByText("Priority roster")).toBeNull();
    expect(screen.queryByRole("button", { name: "Pause" })).toBeNull();
    expect(window.location.pathname).toBe("/teams");
  });

  it("opens the current team workspace from the roster action", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "scope-team",
      scopeSource: "claim:scope_id",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    fireEvent.click(
      await screen.findByRole("button", { name: "View details" }),
    );

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-team");
      expect(window.location.search).toBe("?scopeId=scope-team");
    });
  });

  it("can fall back to the legacy teams home when the flag is disabled", async () => {
    mockedIsTeamFirstEnabled.mockReturnValue(false);
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "scope-team",
      scopeSource: "claim:scope_id",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(await screen.findByText("Current collaboration snapshot")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Handle current blockage" })).toBeTruthy();
    expect(screen.queryByText("Reference roster")).toBeNull();
  });

  it("shows Team Builder as the first action when no current team is available", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "",
      scopeSource: "",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(await screen.findByText("Team context unavailable")).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Build first team" }),
    ).toBeTruthy();
    expect(screen.getByRole("button", { name: "Open Settings" })).toBeTruthy();
  });

  it("does not leak raw session errors into the error state", async () => {
    (studioApi.getAuthSession as jest.Mock).mockRejectedValue(
      new Error("No stub for /api/auth/me"),
    );

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(await screen.findByText("Team context unavailable")).toBeTruthy();
    expect(
      screen.getAllByText(
        "The current session could not be refreshed into a usable team context. Retry, or open Settings while the team context is repaired.",
      ).length,
    ).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Retry" })).toBeTruthy();
    expect(screen.queryByText("No stub for /api/auth/me")).toBeNull();
  });
});
