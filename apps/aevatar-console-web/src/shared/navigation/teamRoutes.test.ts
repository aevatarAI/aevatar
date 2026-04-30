import {
  buildTeamCreateHref,
  buildTeamDetailHref,
  buildTeamsHref,
  readTeamDetailRouteState,
} from "./teamRoutes";

describe("teamRoutes", () => {
  it("builds a workflow-aware team detail href and trims empty values", () => {
    expect(
      buildTeamDetailHref({
        memberId: " member-alpha ",
        scopeId: " scope-alpha ",
        teamId: " team-support ",
        workflowId: "workflow-1",
        serviceId: "service-1",
        runId: "run-1",
        tab: "events",
      }),
    ).toBe(
      "/teams/scope-alpha?teamId=team-support&memberId=member-alpha&workflowId=workflow-1&tab=events&serviceId=service-1&runId=run-1",
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

  it("preserves draft team names when returning to the create page", () => {
    expect(
      buildTeamCreateHref({
        scopeId: "scope-alpha",
        teamName: "订单助手团队",
        entryName: "订单入口",
        teamDraftWorkflowId: "workflow-7",
        teamDraftWorkflowName: "order-entry-draft",
      }),
    ).toBe(
      "/teams/new?scopeId=scope-alpha&teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft",
    );
  });

  it("reads the team detail route state from path and query", () => {
    expect(
      readTeamDetailRouteState(
        "?teamId=team-alpha&memberId=member-alpha&workflowId=wf-1&serviceId=service-1&runId=run-1&tab=members",
        "/teams/scope-alpha",
      ),
    ).toEqual({
      memberId: "member-alpha",
      runId: "run-1",
      scopeId: "scope-alpha",
      serviceId: "service-1",
      tab: "members",
      teamId: "team-alpha",
      workflowId: "wf-1",
    });
  });

  it("maps legacy connectors deep links into the bindings tab", () => {
    expect(
      readTeamDetailRouteState(
        "?workflowId=wf-1&tab=connectors",
        "/teams/scope-alpha",
      ),
    ).toEqual({
      memberId: "",
      runId: "",
      scopeId: "scope-alpha",
      serviceId: "",
      tab: "bindings",
      teamId: "",
      workflowId: "wf-1",
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
      workflowId: "wf-2",
    });
  });
});
