import { fireEvent, screen, waitFor } from "@testing-library/react";
import { message } from "antd";
import React from "react";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamCreatePage from "./new";

jest.mock("antd", () => {
  const actual = jest.requireActual("antd");
  return {
    ...actual,
    message: {
      ...actual.message,
      success: jest.fn(),
      info: jest.fn(),
      warning: jest.fn(),
      error: jest.fn(),
      destroy: jest.fn(),
    },
  };
});

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    createTeam: jest.fn(),
  },
}));

describe("TeamCreatePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams/new?scopeId=scope-a&teamName=Legacy%20Support");
    jest.clearAllMocks();

    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    });
  });

  it("renders the real team creation form and reuses legacy teamName as the initial display name", async () => {
    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByRole("heading", { level: 2, name: "Create Team" }),
    ).toBeTruthy();
    expect(screen.getByDisplayValue("scope-a")).toBeTruthy();
    expect(screen.getByDisplayValue("Legacy Support")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Create Team" }).length).toBe(1);
    expect(screen.queryByLabelText("Custom Team ID")).toBeNull();
    expect(screen.queryByText("Saved Draft Recovery")).toBeNull();
  });

  it("disables create when the display name is still empty", async () => {
    window.history.replaceState({}, "", "/teams/new?scopeId=scope-a");

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByRole("button", { name: "Create Team" }),
    ).toBeDisabled();
  });

  it("does not auto-trust a query scope when auth session exposes no resolved scope", async () => {
    window.history.replaceState({}, "", "/teams/new?scopeId=scope-a");
    (studioApi.getAuthSession as jest.Mock).mockResolvedValueOnce({
      enabled: true,
      authenticated: true,
      scopeId: null,
      scopeSource: null,
    });

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(await screen.findByText("Scope Context")).toBeTruthy();
    expect(screen.getByDisplayValue("scope-a")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Create Team" })).toBeDisabled();
    expect(window.location.search).toBe("");
  });

  it("shows an info banner when old draft query params are present", async () => {
    window.history.replaceState(
      {},
      "",
      "/teams/new?scopeId=scope-a&teamName=Legacy%20Support&entryName=entry&teamDraftWorkflowId=workflow-7",
    );

    renderWithQueryClient(React.createElement(TeamCreatePage));

    expect(
      await screen.findByText(
        "Legacy Create Team query params detected. This page now creates a real team record; initial member drafts should be continued in Studio separately.",
      ),
    ).toBeTruthy();
  });

  it("creates a real team record and routes to canonical team detail", async () => {
    (studioApi.createTeam as jest.Mock).mockResolvedValue({
      teamId: "team-support",
      scopeId: "scope-a",
      displayName: "Support Team",
      description: "Handles inbound support",
      lifecycleStage: "active",
      memberCount: 0,
      createdAt: "2026-04-30T08:00:00Z",
      updatedAt: "2026-04-30T08:00:00Z",
    });

    renderWithQueryClient(React.createElement(TeamCreatePage));

    fireEvent.change(await screen.findByLabelText("Display Name"), {
      target: { value: "Support Team" },
    });
    fireEvent.change(screen.getByLabelText("Description"), {
      target: { value: "Handles inbound support" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create Team" }));

    await waitFor(() => {
      expect(studioApi.createTeam).toHaveBeenCalledWith({
        scopeId: "scope-a",
        displayName: "Support Team",
        description: "Handles inbound support",
      });
    });

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("teamId")).toBe("team-support");
    expect(message.success).toHaveBeenCalledWith("团队已创建。");
  });
});
