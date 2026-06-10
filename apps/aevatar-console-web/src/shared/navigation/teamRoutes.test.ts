import {
  buildTeamCreateHref,
  buildTeamDetailHref,
  buildTeamMemberInvokeHref,
  buildTeamMemberWorkflowStudioHref,
  buildTeamStudioHref,
  buildTeamsHref,
  readTeamDetailRouteState,
} from "./teamRoutes";

describe("teamRoutes", () => {
  it("builds a canonical team detail href and trims empty values", () => {
    expect(
      buildTeamDetailHref({
        memberId: " member-alpha ",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
        workflowId: "workflow-1",
        serviceId: "service-1",
        runId: "run-1",
        testTeam: true,
        tab: "members",
      }),
    ).toBe(
      "/teams/scope-alpha/t-alpha?memberId=member-alpha&workflowId=workflow-1&tab=members&serviceId=service-1&runId=run-1&testTeam=1",
    );
  });

  it("falls back to /teams when the scope is empty", () => {
    expect(
      buildTeamDetailHref({
        scopeId: " ",
        workflowId: "workflow-1",
      }),
    ).toBe(buildTeamsHref());
  });

  it("returns to the teams list with scope context when teamId is missing", () => {
    expect(
      buildTeamDetailHref({
        memberId: "member-alpha",
        scopeId: " scope-alpha ",
        serviceId: "service-1",
      }),
    ).toBe("/teams?scopeId=scope-alpha");
  });

  it("builds a Team-scoped Studio create-member handoff", () => {
    expect(
      buildTeamStudioHref({
        mode: "create-member",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe(
      "/studio?scopeId=scope-alpha&teamId=t-alpha&tab=studio&intent=create-member&returnTo=%2Fteams%2Fscope-alpha%2Ft-alpha%3Ftab%3Dmembers",
    );
  });

  it("builds a Team-scoped Studio member build handoff", () => {
    expect(
      buildTeamStudioHref({
        memberId: " member-alpha ",
        mode: "build-member",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe(
      "/studio?scopeId=scope-alpha&teamId=t-alpha&member=member%3Amember-alpha&step=build&returnTo=%2Fteams%2Fscope-alpha%2Ft-alpha%3FmemberId%3Dmember-alpha%26tab%3Dmembers",
    );
  });

  it("builds explicit Team member workflow studio routes", () => {
    expect(
      buildTeamMemberWorkflowStudioHref({
        mode: "create-member",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe("/teams/scope-alpha/t-alpha/members/new/workflow");

    expect(
      buildTeamMemberWorkflowStudioHref({
        memberId: " member-alpha ",
        mode: "edit-member",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
        workflowId: " workflow-alpha ",
      }),
    ).toBe(
      "/teams/scope-alpha/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha",
    );
  });

  it("builds explicit Team member invoke routes", () => {
    expect(
      buildTeamMemberInvokeHref({
        memberId: " member-alpha ",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe("/teams/scope-alpha/t-alpha/members/member-alpha/invoke");

    expect(
      buildTeamMemberInvokeHref({
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe("/teams/scope-alpha/t-alpha?tab=members");

    expect(
      buildTeamMemberInvokeHref({
        memberId: "member-alpha",
        scopeId: "",
        teamId: "t-alpha",
      }),
    ).toBe("/teams");
  });

  it("keeps old Studio helpers on /studio", () => {
    expect(
      buildTeamStudioHref({
        memberId: "member-alpha",
        mode: "edit-member",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }).startsWith("/studio?"),
    ).toBe(true);
  });

  it("preserves draft team names when returning to the create page", () => {
    expect(
      buildTeamCreateHref({
        teamName: "订单助手团队",
      }),
    ).toBe(
      "/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F",
    );
  });

  it("reads the canonical team detail route state from path and query", () => {
    expect(
      readTeamDetailRouteState(
        "?memberId=member-alpha&teamId=stale-team&workflowId=wf-1&serviceId=service-1&runId=run-1&tab=members",
        "/teams/scope-alpha/t-alpha",
      ),
    ).toEqual({
      memberId: "member-alpha",
      runId: "run-1",
      scopeId: "scope-alpha",
      serviceId: "service-1",
      tab: "members",
      teamId: "t-alpha",
      testTeam: false,
      workflowId: "wf-1",
    });
  });

  it("reads Team Test auto-open intent from the team detail query", () => {
    expect(
      readTeamDetailRouteState(
        "?memberId=member-alpha&testTeam=1",
        "/teams/scope-alpha/t-alpha",
      ),
    ).toMatchObject({
      memberId: "member-alpha",
      scopeId: "scope-alpha",
      teamId: "t-alpha",
      testTeam: true,
    });
  });

  it("reads legacy query team links", () => {
    expect(
      readTeamDetailRouteState(
        "?teamId=t-alpha&tab=members",
        "/teams/scope-alpha",
      ),
    ).toMatchObject({
      scopeId: "scope-alpha",
      tab: "members",
      teamId: "t-alpha",
    });
  });

  it("defaults canonical team routes to the overview tab", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-2&tab=not-real",
        "/teams/scope-query",
      ),
    ).toEqual({
      memberId: "",
      runId: "",
      scopeId: "scope-query",
      serviceId: "",
      tab: "overview",
      teamId: "",
      testTeam: false,
      workflowId: "wf-2",
    });
  });

  it("falls back to the query scope and overview tab when the path is malformed", () => {
    expect(
      readTeamDetailRouteState(
        "?scopeId=scope-query&workflowId=wf-2&tab=not-real",
        "/runtime/runs",
      ),
    ).toEqual({
      memberId: "",
      runId: "",
      scopeId: "scope-query",
      serviceId: "",
      tab: "overview",
      teamId: "",
      testTeam: false,
      workflowId: "wf-2",
    });
  });
});
