import { fireEvent, screen, waitFor } from "@testing-library/react";
import React from "react";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamsHomePage from "./home";

jest.mock("@/shared/studio/api", () => ({
  studioApi: {
    getAuthSession: jest.fn(),
    listTeams: jest.fn(),
    listMembers: jest.fn(),
  },
}));

const teamRoster = {
  scopeId: "scope-a",
  teams: [
    {
      teamId: "team-support",
      scopeId: "scope-a",
      displayName: "Support Team",
      description: "Handles inbound support requests",
      lifecycleStage: "active",
      memberCount: 2,
      createdAt: "2026-04-30T08:00:00Z",
      updatedAt: "2026-04-30T09:00:00Z",
    },
    {
      teamId: "team-ops",
      scopeId: "scope-a",
      displayName: "Ops Team",
      description: "Owns escalation follow-through",
      lifecycleStage: "archived",
      memberCount: 1,
      createdAt: "2026-04-29T08:00:00Z",
      updatedAt: "2026-04-29T09:00:00Z",
    },
  ],
  nextPageToken: null,
};

const memberRoster = {
  scopeId: "scope-a",
  members: [
    {
      memberId: "member-planner",
      scopeId: "scope-a",
      teamId: "team-support",
      displayName: "Planner",
      description: "Routes support issues",
      implementationKind: "workflow",
      lifecycleStage: "bind_ready",
      publishedServiceId: "service-planner",
      lastBoundRevisionId: "rev-1",
      createdAt: "2026-04-30T08:00:00Z",
      updatedAt: "2026-04-30T09:00:00Z",
    },
    {
      memberId: "member-responder",
      scopeId: "scope-a",
      teamId: "team-support",
      displayName: "Responder",
      description: "Drafts answers",
      implementationKind: "workflow",
      lifecycleStage: "bind_ready",
      publishedServiceId: "service-responder",
      lastBoundRevisionId: "rev-2",
      createdAt: "2026-04-30T08:10:00Z",
      updatedAt: "2026-04-30T09:10:00Z",
    },
    {
      memberId: "member-floater",
      scopeId: "scope-a",
      teamId: null,
      displayName: "Floater",
      description: "Unassigned helper",
      implementationKind: "workflow",
      lifecycleStage: "created",
      publishedServiceId: "",
      lastBoundRevisionId: null,
      createdAt: "2026-04-30T08:20:00Z",
      updatedAt: "2026-04-30T09:20:00Z",
    },
  ],
  nextPageToken: null,
};

describe("TeamsHomePage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams?scopeId=scope-a");
    jest.clearAllMocks();

    (studioApi.getAuthSession as jest.Mock).mockResolvedValue({
      enabled: false,
      scopeId: "scope-a",
      scopeSource: "nyxid",
    });
    (studioApi.listTeams as jest.Mock).mockResolvedValue(teamRoster);
    (studioApi.listMembers as jest.Mock).mockResolvedValue(memberRoster);
  });

  it("renders the current scope team roster instead of the old member-first homepage", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("Support Team")).toBeTruthy();
    expect(screen.getByText("My Teams")).toBeTruthy();
    expect(screen.getByText("Handles inbound support requests")).toBeTruthy();
    expect(screen.getByText("Planner · Responder")).toBeTruthy();
    expect(screen.getByText("Ops Team")).toBeTruthy();
    expect(screen.getByText("Assigned Members")).toBeTruthy();
    expect(screen.getByText("Unassigned Members")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Create Team" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: "Manage Team" }).length).toBe(2);
  });

  it("opens the canonical team detail route with teamId in query", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    fireEvent.click(await screen.findAllByRole("button", { name: "Manage Team" }).then((buttons) => buttons[0]));

    await waitFor(() => {
      expect(window.location.pathname).toBe("/teams/scope-a");
    });

    const params = new URLSearchParams(window.location.search);
    expect(params.get("teamId")).toBe("team-support");
  });

  it("routes Create Team into the real team creation page with scope context", async () => {
    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("Support Team")).toBeTruthy();
    fireEvent.click(await screen.findByRole("button", { name: "Create Team" }));

    expect(window.location.pathname).toBe("/teams/new");
  });

  it("keeps scope selection manual when the live session lookup has no scope context", async () => {
    window.history.replaceState({}, "", "/teams");
    (studioApi.getAuthSession as jest.Mock).mockResolvedValueOnce({
      enabled: true,
      authenticated: true,
      scopeId: null,
      scopeSource: null,
    });

    renderWithQueryClient(React.createElement(TeamsHomePage));

    expect(await screen.findByText("Scope Context")).toBeTruthy();
    expect(window.location.pathname).toBe("/teams");
    expect(new URLSearchParams(window.location.search).get("scopeId")).toBeNull();
  });
});
