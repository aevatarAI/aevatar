import { screen, waitFor } from "@testing-library/react";
import React from "react";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsIndexPage from "./index";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
  },
}));

import { studioApi } from "@/shared/studio/api";

describe("TeamsIndexPage", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.history.replaceState({}, "", "/teams");
  });

  it("redirects to the current team workspace when auth session resolves a scope", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "scope-team",
      scopeSource: "claim:scope_id",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-team");
      expect(window.location.search).toBe("?scopeId=scope-team");
    });
  });

  it("shows an honest empty state when no team context can be resolved", async () => {
    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: true,
      scopeId: "",
      scopeSource: "",
    });

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(await screen.findByText("Team context unavailable")).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Open legacy project workspace" }),
    ).toBeTruthy();
  });

  it("does not leak raw session errors into the empty state", async () => {
    (studioApi.getAuthSession as jest.Mock).mockRejectedValue(
      new Error("No stub for /api/auth/me"),
    );

    renderWithQueryClient(React.createElement(TeamsIndexPage));

    expect(await screen.findByText("Failed to resolve the current team")).toBeTruthy();
        expect(
            screen.getAllByText(
                "The current session could not be refreshed into a usable team context. Retry, or open the legacy workspace while the session context is repaired.",
            ).length,
        ).toBeGreaterThan(0);
    expect(screen.queryByText("No stub for /api/auth/me")).toBeNull();
  });
});
