import { fireEvent, screen, waitFor } from "@testing-library/react";
import { message } from "antd";
import React from "react";
import { studioApi } from "@/shared/studio/api";
import { renderWithQueryClient } from "../../../tests/reactQueryTestUtils";
import TeamDetailPage from "./detail";

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
    listTeams: jest.fn(),
    getMember: jest.fn(),
    getTeam: jest.fn(),
    listTeamMembers: jest.fn(),
    listMembers: jest.fn(),
    updateMemberTeam: jest.fn(),
    createMember: jest.fn(),
    updateTeam: jest.fn(),
    archiveTeam: jest.fn(),
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
      lifecycleStage: "active",
      memberCount: 1,
      createdAt: "2026-04-30T08:10:00Z",
      updatedAt: "2026-04-30T09:10:00Z",
    },
  ],
  nextPageToken: null,
};

const teamSummary = {
  teamId: "team-support",
  scopeId: "scope-a",
  displayName: "Support Team",
  description: "Handles inbound support requests",
  lifecycleStage: "active",
  memberCount: 2,
  createdAt: "2026-04-30T08:00:00Z",
  updatedAt: "2026-04-30T09:00:00Z",
};

const teamMembers = {
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
  ],
  nextPageToken: null,
};

const allMembers = {
  scopeId: "scope-a",
  members: [
    ...teamMembers.members,
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

describe("TeamDetailPage", () => {
  beforeEach(() => {
    window.history.replaceState({}, "", "/teams/scope-a?teamId=team-support");
    jest.clearAllMocks();

    (studioApi.listTeams as jest.Mock).mockResolvedValue(teamRoster);
    (studioApi.getTeam as jest.Mock).mockResolvedValue(teamSummary);
    (studioApi.listTeamMembers as jest.Mock).mockResolvedValue(teamMembers);
    (studioApi.listMembers as jest.Mock).mockResolvedValue(allMembers);
    (studioApi.getMember as jest.Mock).mockResolvedValue({
      summary: teamMembers.members[0],
      implementationRef: null,
      lastBinding: null,
    });
    (studioApi.updateMemberTeam as jest.Mock).mockResolvedValue({
      summary: teamMembers.members[0],
      implementationRef: null,
      lastBinding: null,
    });
    (studioApi.createMember as jest.Mock).mockResolvedValue(teamMembers.members[0]);
    (studioApi.updateTeam as jest.Mock).mockResolvedValue(teamSummary);
    (studioApi.archiveTeam as jest.Mock).mockResolvedValue({
      ...teamSummary,
      lifecycleStage: "archived",
    });
  });

  it("renders team summary and member management around the backend team endpoints", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    expect(await screen.findByText("Support Team")).toBeTruthy();
    expect(screen.getByText("Team Summary")).toBeTruthy();
    expect(screen.getAllByText("Planner").length).toBeGreaterThan(0);
    expect(
      screen.getAllByText("Handles inbound support requests").length,
    ).toBeGreaterThan(0);
    expect(screen.getAllByRole("button", { name: "Open in Studio" }).length).toBe(2);
    expect(screen.getAllByRole("button", { name: "Remove from Team" }).length).toBe(2);
    expect(screen.getByText("Create Member In This Team")).toBeTruthy();
    expect(screen.getByText("Add Existing Member")).toBeTruthy();
  });

  it("creates a new member directly inside the current team", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    fireEvent.change(await screen.findByLabelText("New Member Display Name"), {
      target: { value: "Analyst" },
    });
    fireEvent.change(screen.getByLabelText("New Member ID"), {
      target: { value: "member-analyst" },
    });
    fireEvent.change(screen.getByLabelText("New Member Description"), {
      target: { value: "Checks support tickets before execution" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create Member" }));

    await waitFor(() => {
      expect(studioApi.createMember).toHaveBeenCalledWith({
        scopeId: "scope-a",
        teamId: "team-support",
        displayName: "Analyst",
        description: "Checks support tickets before execution",
        implementationKind: "workflow",
        memberId: "member-analyst",
      });
    });

    expect(message.success).toHaveBeenCalledWith("成员已创建并加入团队。");
  });

  it("assigns an existing member into the current team", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    await screen.findByRole("option", {
      name: "Floater · member-floater · currently unassigned",
    });
    fireEvent.change(await screen.findByLabelText("Existing Member Selector"), {
      target: { value: "member-floater" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add Member" }));

    await waitFor(() => {
      expect(studioApi.updateMemberTeam).toHaveBeenCalledWith(
        "scope-a",
        "member-floater",
        "team-support",
      );
    });
  });

  it("removes a member from the current team", async () => {
    renderWithQueryClient(React.createElement(TeamDetailPage));

    const removeButtons = await screen.findAllByRole("button", {
      name: "Remove from Team",
    });
    fireEvent.click(removeButtons[1]);

    await waitFor(() => {
      expect(studioApi.updateMemberTeam).toHaveBeenCalledWith(
        "scope-a",
        "member-planner",
        null,
      );
    });
  });

  it("resolves old member-first deep links into canonical team routes", async () => {
    window.history.replaceState({}, "", "/teams/scope-a?memberId=member-planner");

    renderWithQueryClient(React.createElement(TeamDetailPage));

    await waitFor(() => {
      expect(new URLSearchParams(window.location.search).get("teamId")).toBe(
        "team-support",
      );
    });

    expect(studioApi.getMember).toHaveBeenCalledWith("scope-a", "member-planner");
  });
});
