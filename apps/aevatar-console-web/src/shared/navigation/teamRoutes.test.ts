import {
  buildTeamCreateHref,
  buildTeamDetailHref,
  buildTeamMemberAutomationsHref,
  buildTeamMemberInvokeHref,
  buildTeamMemberPublishedRunsHref,
  buildTeamMemberWorkflowStudioHref,
  buildTeamStudioHref,
  buildTeamsHref,
  buildTeamWorkOrderDetailHref,
  readTeamDetailRouteState,
  readTeamWorkOrderRouteState,
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
      "/scopes/scope-alpha/teams/t-alpha?memberId=member-alpha&workflowId=workflow-1&tab=members&serviceId=service-1&runId=run-1&testTeam=1",
    );
  });

  it("recognizes the team activity tab without collapsing runtime context", () => {
    const memberId = "member-alpha";
    const workflowId = "workflow-alpha";
    const publishedServiceId = "service-alpha";

    expect(
      readTeamDetailRouteState(
        `?memberId=${memberId}&workflowId=${workflowId}&serviceId=${publishedServiceId}&runId=run-alpha&tab=activity`,
        "/scopes/scope-alpha/teams/team-alpha",
      ),
    ).toEqual({
      memberId,
      routeMemberId: "",
      runId: "run-alpha",
      scopeId: "scope-alpha",
      serviceId: publishedServiceId,
      tab: "activity",
      teamId: "team-alpha",
      testTeam: false,
      workflowId,
    });
    expect(memberId).not.toBe(workflowId);
    expect(memberId).not.toBe(publishedServiceId);
    expect(workflowId).not.toBe(publishedServiceId);
  });

  it("falls back to the scope resolver when the scope is empty", () => {
    expect(
      buildTeamDetailHref({
        scopeId: " ",
        workflowId: "workflow-1",
      }),
    ).toBe(buildTeamsHref());
  });

  it("returns to the scoped teams list when teamId is missing", () => {
    expect(
      buildTeamDetailHref({
        memberId: "member-alpha",
        scopeId: " scope-alpha ",
        serviceId: "service-1",
      }),
    ).toBe("/scopes/scope-alpha/teams");
  });

  it("builds a Team-scoped Studio create-member handoff", () => {
    expect(
      buildTeamStudioHref({
        mode: "create-member",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe(
      "/studio?scopeId=scope-alpha&teamId=t-alpha&tab=studio&intent=create-member&returnTo=%2Fscopes%2Fscope-alpha%2Fteams%2Ft-alpha%3Ftab%3Dmembers",
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
      "/studio?scopeId=scope-alpha&teamId=t-alpha&member=member%3Amember-alpha&step=build&returnTo=%2Fscopes%2Fscope-alpha%2Fteams%2Ft-alpha%3FmemberId%3Dmember-alpha%26tab%3Dmembers",
    );
  });

  it("builds explicit Team member workflow studio routes", () => {
    expect(
      buildTeamMemberWorkflowStudioHref({
        mode: "create-member",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha/members/new/workflow");

    expect(
      buildTeamMemberWorkflowStudioHref({
        memberId: " member-alpha ",
        mode: "edit-member",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
        workflowId: " workflow-alpha ",
        workflowSource: "published",
      }),
    ).toBe(
      "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/workflow?workflowId=workflow-alpha&workflowSource=published",
    );

    expect(
      buildTeamMemberWorkflowStudioHref({
        memberId: " member-alpha ",
        mode: "edit-member",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
        workflowId: " wf-alpha-next ",
      }),
    ).toBe(
      "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/workflow?workflowId=wf-alpha-next",
    );
  });

  it("builds explicit Team member invoke routes", () => {
    expect(
      buildTeamMemberInvokeHref({
        memberId: " member-alpha ",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha/members/member-alpha/invoke");

    expect(
      buildTeamMemberInvokeHref({
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha?tab=members");

    expect(
      buildTeamMemberInvokeHref({
        memberId: "member-alpha",
        scopeId: "",
        teamId: "t-alpha",
      }),
    ).toBe(buildTeamsHref());
  });

  it("builds explicit Team member published run routes", () => {
    expect(
      buildTeamMemberPublishedRunsHref({
        actorId: " actor://scope-alpha/run-1 ",
        memberId: " member-alpha ",
        runId: " run-1 ",
        scheduleId: " sch-alpha ",
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe(
      "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/runs?runId=run-1&actorId=actor%3A%2F%2Fscope-alpha%2Frun-1&scheduleId=sch-alpha",
    );

    expect(
      buildTeamMemberPublishedRunsHref({
        memberId: "member-alpha",
        scheduleId: "schedule-alpha",
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe(
      "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/runs?scheduleId=schedule-alpha",
    );

    expect(
      buildTeamMemberPublishedRunsHref({
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha?tab=members");

    expect(
      buildTeamMemberPublishedRunsHref({
        memberId: "member-alpha",
        scopeId: "",
        teamId: "t-alpha",
      }),
    ).toBe(buildTeamsHref());
  });

  it("builds member-owned automation handoffs without using workflow or service identities", () => {
    const memberId = "m-alpha";
    const workflowId = "wf-alpha";
    const publishedServiceId = "svc-alpha";

    expect(
      buildTeamMemberAutomationsHref({
        memberId: ` ${memberId} `,
        scopeId: " scope-alpha ",
        teamId: " t-alpha ",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha/members/m-alpha/automations");
    expect(
      buildTeamMemberAutomationsHref({
        scopeId: "scope-alpha",
        teamId: "t-alpha",
      }),
    ).toBe("/scopes/scope-alpha/teams/t-alpha?tab=automations");
    expect(
      buildTeamMemberAutomationsHref({
        memberId,
        scopeId: "",
        teamId: "t-alpha",
      }),
    ).toBe(buildTeamsHref());
    expect(workflowId).not.toBe(memberId);
    expect(publishedServiceId).not.toBe(memberId);
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
        scopeId: "scope-alpha",
        teamName: "订单助手团队",
      }),
    ).toBe(
      "/scopes/scope-alpha/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F",
    );
  });

  it("keeps query member candidates separate from canonical member path identity", () => {
    expect(
      readTeamDetailRouteState(
        "?memberId=member-alpha&teamId=stale-team&workflowId=wf-1&serviceId=service-1&runId=run-1&tab=members",
        "/scopes/scope-alpha/teams/t-alpha",
      ),
    ).toEqual({
      memberId: "member-alpha",
      routeMemberId: "",
      runId: "run-1",
      scopeId: "scope-alpha",
      serviceId: "service-1",
      tab: "members",
      teamId: "t-alpha",
      testTeam: false,
      workflowId: "wf-1",
    });
  });

  it("reads scope, team, member, and draft workflow identities from scoped member workflow routes", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-alpha",
        "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/workflow",
      ),
    ).toMatchObject({
      memberId: "member-alpha",
      routeMemberId: "member-alpha",
      scopeId: "scope-alpha",
      teamId: "t-alpha",
      workflowId: "wf-alpha",
    });
  });

  it("reads member-owned automation routes from the canonical member path", () => {
    expect(
      readTeamDetailRouteState(
        "?tab=members",
        "/scopes/scope-alpha/teams/t-alpha/members/member-alpha/automations",
      ),
    ).toMatchObject({
      memberId: "member-alpha",
      routeMemberId: "member-alpha",
      scopeId: "scope-alpha",
      tab: "automations",
      teamId: "t-alpha",
    });
  });

  it("does not read removed legacy Team member workflow routes", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-alpha",
        "/teams/scope-alpha/t-alpha/members/member-alpha/workflow",
      ),
    ).toMatchObject({
      memberId: "",
      routeMemberId: "",
      scopeId: "",
      teamId: "",
      workflowId: "wf-alpha",
    });
  });

  it("reads Team Test auto-open intent from the team detail query", () => {
    expect(
      readTeamDetailRouteState(
        "?memberId=member-alpha&testTeam=1",
        "/scopes/scope-alpha/teams/t-alpha",
      ),
    ).toMatchObject({
      memberId: "member-alpha",
      routeMemberId: "",
      scopeId: "scope-alpha",
      teamId: "t-alpha",
      testTeam: true,
    });
  });

  it("does not read removed legacy query team links", () => {
    expect(
      readTeamDetailRouteState(
        "?teamId=t-alpha&tab=members",
        "/teams/scope-alpha",
      ),
    ).toMatchObject({
      scopeId: "",
      tab: "members",
      teamId: "t-alpha",
    });
  });

  it("defaults canonical team routes to the overview tab", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-2&tab=not-real",
        "/scopes/scope-query/teams",
      ),
    ).toEqual({
      memberId: "",
      routeMemberId: "",
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
      routeMemberId: "",
      runId: "",
      scopeId: "scope-query",
      serviceId: "",
      tab: "overview",
      teamId: "",
      testTeam: false,
      workflowId: "wf-2",
    });
  });

  it("builds and reads a canonical Team-scoped WorkOrder detail route", () => {
    const href = buildTeamWorkOrderDetailHref({
      scopeId: " scope-alpha ",
      teamId: " team-alpha ",
      workOrderId: " wo-alpha ",
    });

    expect(href).toBe(
      "/scopes/scope-alpha/teams/team-alpha/work-orders/wo-alpha",
    );
    expect(readTeamWorkOrderRouteState(href)).toEqual({
      scopeId: "scope-alpha",
      teamId: "team-alpha",
      workOrderId: "wo-alpha",
    });
  });

  it("keeps WorkOrders contextual when the detail identity is absent", () => {
    expect(
      buildTeamWorkOrderDetailHref({
        scopeId: "scope-alpha",
        teamId: "team-alpha",
        workOrderId: " ",
      }),
    ).toBe("/scopes/scope-alpha/teams/team-alpha?tab=work-orders");
    expect(
      readTeamDetailRouteState(
        "?tab=work-orders",
        "/scopes/scope-alpha/teams/team-alpha",
      ).tab,
    ).toBe("work-orders");
  });
});
