import {
  buildTeamCreateHref,
  buildTeamDetailHref,
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
        tab: "members",
      }),
    ).toBe(
      "/teams/scope-alpha/t-alpha?memberId=member-alpha&workflowId=workflow-1&tab=members&serviceId=service-1&runId=run-1",
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

  it("preserves draft team names when returning to the create page", () => {
    expect(
      buildTeamCreateHref({
        teamName: "订单助手团队",
        entryName: "订单入口",
        teamDraftWorkflowId: "workflow-7",
        teamDraftWorkflowName: "order-entry-draft",
      }),
    ).toBe(
      "/teams/new?teamName=%E8%AE%A2%E5%8D%95%E5%8A%A9%E6%89%8B%E5%9B%A2%E9%98%9F&entryName=%E8%AE%A2%E5%8D%95%E5%85%A5%E5%8F%A3&teamDraftWorkflowId=workflow-7&teamDraftWorkflowName=order-entry-draft",
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
      workflowId: "wf-1",
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

  it.each(["connectors", "events", "topology"])(
    "falls back legacy %s deep links to the overview tab",
    (tab) => {
      expect(
        readTeamDetailRouteState(
          `?workflowId=wf-1&tab=${tab}`,
          "/teams/scope-alpha",
        ),
      ).toEqual({
        memberId: "",
        runId: "",
        scopeId: "scope-alpha",
        serviceId: "",
        tab: "overview",
        teamId: "",
        workflowId: "wf-1",
      });
    },
  );

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
